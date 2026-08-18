using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Prostor.Chat;

public interface IEmbedder
{
    /// <summary>Один вектор на один запрос пользователя. Больше в рамках хода не вызывается.</summary>
    Task<float[]> EmbedAsync(string text, CancellationToken ct);
    int Dimensions { get; }
    string Kind { get; }
}

/// <summary>
/// Детерминированный эмбеддинг без внешних сервисов: hashing trick по словам
/// и символьным триграммам. Нужен, чтобы демо разворачивалось и работало
/// вообще без ключей.
/// </summary>
public sealed class StubEmbedder : IEmbedder
{
    // Должна совпадать с vector(N) в db/init/01_schema.sql и с AppConfig.EmbeddingDimensions
    // (нативная размерность bge-m3 — 1024).
    public const int Dim = 1024;
    private const uint FnvOffset = 2166136261;
    private const uint FnvPrime = 16777619;

    public int Dimensions => Dim;
    public string Kind => "stub";

    public Task<float[]> EmbedAsync(string text, CancellationToken ct) => Task.FromResult(Embed(text));

    public static float[] Embed(string text)
    {
        var vector = new float[Dim];
        foreach (var (feature, weight) in Features(text))
        {
            var hash = Fnv1a(feature);
            var index = (int)(hash % Dim);
            var sign = ((hash >> 31) & 1) == 1 ? -1f : 1f;
            vector[index] += sign * weight;
        }

        double norm = 0;
        foreach (var value in vector) norm += value * value;
        norm = Math.Sqrt(norm);
        if (norm > 0)
            for (var i = 0; i < Dim; i++) vector[i] = (float)(vector[i] / norm);

        return vector;
    }

    public static string Normalize(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var sb = new StringBuilder(text.Length);
        var lastSpace = true;
        foreach (var raw in text.ToLowerInvariant())
        {
            var c = raw == 'ё' ? 'е' : raw;
            var keep = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'z') || (c >= 'а' && c <= 'я');
            if (keep)
            {
                sb.Append(c);
                lastSpace = false;
            }
            else if (!lastSpace)
            {
                sb.Append(' ');
                lastSpace = true;
            }
        }
        return sb.ToString().Trim();
    }

    private static IEnumerable<(string Feature, float Weight)> Features(string text)
    {
        foreach (var word in Normalize(text).Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (word.Length < 2) continue;
            yield return ("w:" + word, 1.0f);

            var padded = "_" + word + "_";
            for (var i = 0; i + 3 <= padded.Length; i++)
                yield return ("t:" + padded.Substring(i, 3), 0.35f);
        }
    }

    private static uint Fnv1a(string value)
    {
        var hash = FnvOffset;
        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            hash ^= b;
            hash *= FnvPrime;
        }
        return hash;
    }
}

/// <summary>
/// Любой OpenAI-совместимый /embeddings endpoint: Ollama, HF TEI, vLLM, OpenAI...
/// Адрес и ключ — отдельные от LLM (см. AppConfig.EmbeddingBaseUrl), потому что
/// провайдер эмбеддингов обычно другой (сейчас — локальный Ollama с bge-m3).
/// </summary>
public sealed class OpenAiEmbedder : IEmbedder
{
    private readonly HttpClient _http;
    private readonly string _model;
    private readonly int _dimensions;

    public OpenAiEmbedder(HttpClient http, AppConfig config)
    {
        _http = http;
        _model = config.EmbeddingModel;
        _dimensions = config.EmbeddingDimensions;
        _http.BaseAddress = new Uri(config.EmbeddingBaseUrl.TrimEnd('/') + "/");
        if (!string.IsNullOrWhiteSpace(config.EmbeddingApiKey))
            _http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", config.EmbeddingApiKey);
        _http.Timeout = TimeSpan.FromSeconds(config.EmbeddingTimeoutSeconds);
    }

    public int Dimensions => _dimensions;
    public string Kind => "openai-compatible";

    public async Task<float[]> EmbedAsync(string text, CancellationToken ct)
    {
        // "dimensions" в запрос не шлём: это proprietary matryoshka-параметр OpenAI,
        // bge-m3/Ollama и большинство других провайдеров его не понимают.
        using var response = await _http.PostAsJsonAsync(
            "embeddings",
            new { model = _model, input = text },
            ct);
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var array = doc.RootElement.GetProperty("data")[0].GetProperty("embedding");
        var vector = new float[array.GetArrayLength()];
        var i = 0;
        foreach (var item in array.EnumerateArray()) vector[i++] = item.GetSingle();

        if (vector.Length != _dimensions)
            throw new InvalidOperationException(
                $"эмбеддер '{_model}' вернул вектор размерности {vector.Length}, а схема БД " +
                $"ожидает {_dimensions}. Проверьте EMBEDDING_MODEL/EMBEDDING_DIMENSIONS и " +
                "vector(...) в db/init/01_schema.sql.");

        return vector;
    }
}

/// <summary>
/// Кэш эмбеддингов в Postgres. Повторные формулировки не уходят в модель
/// второй раз: на живом трафике это 30–50% запросов.
/// </summary>
public sealed class CachingEmbedder : IEmbedder
{
    private readonly IEmbedder _inner;
    private readonly Db _db;
    private readonly ILogger<CachingEmbedder> _log;

    public CachingEmbedder(IEmbedder inner, Db db, ILogger<CachingEmbedder> log)
    {
        _inner = inner;
        _db = db;
        _log = log;
    }

    public int Dimensions => _inner.Dimensions;
    public string Kind => _inner.Kind + "+cache";

    public async Task<float[]> EmbedAsync(string text, CancellationToken ct)
    {
        var hash = Sha256(_inner.Kind + "|" + StubEmbedder.Normalize(text));

        var cached = await _db.GetCachedEmbeddingAsync(hash, ct);
        if (cached is not null) return cached;

        var vector = await _inner.EmbedAsync(text, ct);
        try
        {
            await _db.PutCachedEmbeddingAsync(hash, vector, ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "не удалось записать эмбеддинг в кэш");
        }
        return vector;
    }

    private static string Sha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}

public static class VectorLiteral
{
    /// <summary>pgvector принимает вектор текстом: '[0.1,0.2,...]'::vector.</summary>
    public static string From(float[] vector)
    {
        var sb = new StringBuilder(vector.Length * 9 + 2);
        sb.Append('[');
        for (var i = 0; i < vector.Length; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(vector[i].ToString("0.######", CultureInfo.InvariantCulture));
        }
        sb.Append(']');
        return sb.ToString();
    }
}

/// <summary>Инструменты, доступные роутеру. Больше их нет и не появляется динамически.</summary>
public static class RouterTool
{
    /// <summary>Пользователь описал вид работ — показать услуги каталога.</summary>
    public const string SearchServices = "search_services";

    /// <summary>Пользователь ищет подрядчика — показать компании по способностям.</summary>
    public const string SearchExecutors = "search_executors";
}

/// <summary>
/// Результат вызова-роутера. Либо заполнен <see cref="ToolName"/> — модель
/// попросила передать управление одному из двух детерминированных поисков
/// (оба без аргументов и без побочных эффектов), либо заполнен
/// <see cref="ReplyText"/> — это и есть ответ пользователю, без обращения к БД.
/// </summary>
public sealed record LlmDecision(string? ReplyText, string? ToolName)
{
    public bool ToolCalled => ToolName is not null;
}

public interface ILlm
{
    /// <summary>Один вызов на ход. Данные уже найдены — модель только формулирует ответ.</summary>
    IAsyncEnumerable<string> StreamAsync(string system, string user, CancellationToken ct);

    /// <summary>
    /// Один нестримящийся вызов-роутер на свободный текст: консультировать пользователя
    /// текстом, показать услуги каталога или показать исполнителей. Модель не выбирает,
    /// ЧТО искать, — у инструментов нет аргументов, поисковым запросом остаётся исходный
    /// текст пользователя. Это не agentic tool-calling из раздела 5 architecture.md:
    /// инструментов ровно два, оба без параметров и без побочных эффектов, они лишь
    /// переключают, какая из уже существующих детерминированных веток хода выполнится.
    /// </summary>
    Task<LlmDecision> DecideAsync(string system, string user, CancellationToken ct);

    string Kind { get; }
}

/// <summary>Шаблонный «стенограф»: работает без ключей и без сети.</summary>
public sealed class StubLlm : ILlm
{
    public string Kind => "stub";

    public async IAsyncEnumerable<string> StreamAsync(
        string system, string user,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        // user содержит уже отрендеренную сводку найденного — заглушка просто
        // отдаёт её порциями, имитируя потоковую выдачу.
        var text = ExtractSummary(user);
        foreach (var chunk in Split(text, 42))
        {
            ct.ThrowIfCancellationRequested();
            yield return chunk;
            await Task.Delay(15, ct);
        }
    }

    private static string ExtractSummary(string user)
    {
        const string marker = "ОТВЕТ_ЗАГЛУШКИ:";
        var index = user.IndexOf(marker, StringComparison.Ordinal);
        return index >= 0 ? user[(index + marker.Length)..].Trim() : "Готово.";
    }

    private static IEnumerable<string> Split(string text, int size)
    {
        for (var i = 0; i < text.Length; i += size)
            yield return text.Substring(i, Math.Min(size, text.Length - i));
    }

    /// <summary>
    /// Без реальной модели роутер не умеет вести диалог — воспроизводим правило,
    /// которое действовало до его появления: любой свободный текст (кроме отдельно
    /// перехватываемого TryFillFreeField в Pipeline) считается поиском услуг.
    /// Демо без LLM_API_KEY ведёт себя так же, как до этой фичи, независимо от
    /// значения ENABLE_ROUTER.
    /// </summary>
    public Task<LlmDecision> DecideAsync(string system, string user, CancellationToken ct) =>
        Task.FromResult(new LlmDecision(ReplyText: null, ToolName: RouterTool.SearchServices));
}

public sealed class OpenAiLlm : ILlm
{
    // Все инструменты, которые вообще может увидеть модель. Оба без аргументов:
    // поисковым запросом остаётся исходный текст пользователя, а не то, что
    // модель могла бы пересказать своими словами.
    private static readonly JsonArray RouterTools = new()
    {
        Tool(RouterTool.SearchServices,
            "Вызови, когда пользователь описал ВИД РАБОТ и готов увидеть подходящие услуги " +
            "из каталога ПРОСТОР. Не вызывай, если ещё уточняешь потребность, отвечаешь на " +
            "общий вопрос или пользователь просит изменить уже выбранные параметры."),
        Tool(RouterTool.SearchExecutors,
            "Вызови, когда пользователь ищет ИСПОЛНИТЕЛЯ, подрядчика или компанию: «кто может " +
            "это сделать», «найди подрядчика», «кто пробурит скважину». Отличай от " +
            "search_services: там пользователь ищет саму услугу, здесь — того, кто её выполнит.")
    };

    private static JsonObject Tool(string name, string description) => new()
    {
        ["type"] = "function",
        ["function"] = new JsonObject
        {
            ["name"] = name,
            ["description"] = description,
            ["parameters"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject(),
                ["additionalProperties"] = false
            }
        }
    };

    private static readonly HashSet<string> KnownTools =
        new(StringComparer.Ordinal) { RouterTool.SearchServices, RouterTool.SearchExecutors };

    private readonly HttpClient _http;
    private readonly AppConfig _config;

    public OpenAiLlm(HttpClient http, AppConfig config)
    {
        _http = http;
        _config = config;
        _http.BaseAddress = new Uri(config.LlmBaseUrl.TrimEnd('/') + "/");
        _http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", config.LlmApiKey);
        _http.Timeout = TimeSpan.FromSeconds(config.LlmTimeoutSeconds);
    }

    public string Kind => "openai-compatible";

    /// <summary>
    /// Нестримящийся вызов-роутер: одно решение за один HTTP round-trip, без разбора
    /// потоковых SSE-кадров с фрагментами tool_calls[].function.arguments — этот путь
    /// сознательно не совмещает streaming и tool calling, чтобы не зависеть от того,
    /// насколько аккуратно конкретный OpenAI-совместимый провайдер поддерживает их
    /// комбинацию (у DeepSeek на момент написания она не задокументирована).
    /// </summary>
    public async Task<LlmDecision> DecideAsync(string system, string user, CancellationToken ct)
    {
        var payload = new
        {
            model = _config.LlmModel,
            stream = false,
            temperature = 0.2,
            messages = new object[]
            {
                new { role = "system", content = system },
                new { role = "user", content = user }
            },
            tools = RouterTools,
            tool_choice = "auto"
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, "chat/completions")
        {
            Content = JsonContent.Create(payload)
        };
        using var response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var choices = doc.RootElement.GetProperty("choices");
        if (choices.GetArrayLength() == 0) return new LlmDecision(null, null);
        var message = choices[0].GetProperty("message");

        if (message.TryGetProperty("tool_calls", out var toolCalls) && toolCalls.GetArrayLength() > 0)
        {
            var name = toolCalls[0].GetProperty("function").GetProperty("name").GetString();
            // Имя сверяем с белым списком: провайдер может прислать что-то
            // неожиданное, и тогда это отказ от вызова, а не повод падать или
            // запускать поиск вслепую.
            return new LlmDecision(null, name is not null && KnownTools.Contains(name) ? name : null);
        }

        var text = message.TryGetProperty("content", out var content) ? content.GetString() : null;
        return new LlmDecision(text, null);
    }

    public async IAsyncEnumerable<string> StreamAsync(
        string system, string user,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var payload = new
        {
            model = _config.LlmModel,
            stream = true,
            temperature = 0.2,
            messages = new object[]
            {
                new { role = "system", content = system },
                new { role = "user", content = user }
            }
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, "chat/completions")
        {
            Content = JsonContent.Create(payload)
        };
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        while (!reader.EndOfStream)
        {
            ct.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(ct);
            if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("data:", StringComparison.Ordinal))
                continue;

            var data = line[5..].Trim();
            if (data == "[DONE]") yield break;

            string? delta = null;
            try
            {
                using var doc = JsonDocument.Parse(data);
                var choices = doc.RootElement.GetProperty("choices");
                if (choices.GetArrayLength() > 0 &&
                    choices[0].TryGetProperty("delta", out var d) &&
                    d.TryGetProperty("content", out var c))
                {
                    delta = c.GetString();
                }
            }
            catch (JsonException)
            {
                // битый кадр стрима игнорируем — соединение продолжается
            }

            if (!string.IsNullOrEmpty(delta)) yield return delta;
        }
    }
}
