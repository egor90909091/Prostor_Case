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

/// <summary>
/// Схема ожидаемого JSON-ответа: имя + тело JSON Schema. Передаётся модели
/// двумя путями сразу — как <c>response_format: json_schema</c> (если провайдер
/// это держит и включён <c>LLM_STRUCTURED_OUTPUTS</c>) и всегда текстом внутри
/// системной подсказки вызывающей стороны, — чтобы форма соблюдалась и на
/// json_object-only провайдере вроде DeepSeek.
/// </summary>
public sealed record JsonReplySchema(string Name, JsonObject Schema);

/// <summary>Разбор JSON-ответа модели, устойчивый к типовому мусору вокруг.</summary>
public static class JsonReply
{
    /// <summary>
    /// Достаёт первый сбалансированный JSON-объект из ответа модели, терпя
    /// markdown-фенсы (```json … ```) и текст до/после. Возвращает null, если
    /// объекта нет вовсе.
    /// </summary>
    public static JsonObject? Extract(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return null;
        var text = content.Trim();

        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNewLine = text.IndexOf('\n');
            if (firstNewLine >= 0) text = text[(firstNewLine + 1)..];
            var closingFence = text.LastIndexOf("```", StringComparison.Ordinal);
            if (closingFence >= 0) text = text[..closingFence];
            text = text.Trim();
        }

        if (TryParse(text, out var direct)) return direct;

        var start = text.IndexOf('{');
        if (start < 0) return null;

        var depth = 0;
        var inString = false;
        var escaped = false;
        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == '"') inString = false;
                continue;
            }
            if (c == '"') inString = true;
            else if (c == '{') depth++;
            else if (c == '}' && --depth == 0)
                return TryParse(text[start..(i + 1)], out var slice) ? slice : null;
        }
        return null;
    }

    private static bool TryParse(string candidate, out JsonObject? result)
    {
        result = null;
        try
        {
            result = JsonNode.Parse(candidate) as JsonObject;
            return result is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

public interface ILlm
{
    /// <summary>Один вызов на ход. Данные уже найдены — модель только формулирует ответ.</summary>
    IAsyncEnumerable<string> StreamAsync(string system, string user, CancellationToken ct);

    /// <summary>
    /// Один нестримящийся текстовый вызов: ответ модели приходит целиком за один
    /// HTTP round-trip (используется консультацией по выбранной услуге — там
    /// важнее дождаться готового ответа, чем получить первые токены). Реализация
    /// OpenAiLlm делает это честным stream=false; заглушка без ключа просто
    /// отдаёт текст после маркера ОТВЕТ_ЗАГЛУШКИ, как и StreamAsync.
    /// </summary>
    Task<string?> CompleteAsync(string system, string user, CancellationToken ct);

    /// <summary>
    /// Один нестримящийся вызов в JSON-mode: структурированное извлечение
    /// (поля ТЗ из диалога) или ревью текста. <paramref name="schema"/> —
    /// ожидаемая форма ответа: уходит в response_format json_schema, если
    /// провайдер это держит и включён LLM_STRUCTURED_OUTPUTS, иначе вызов идёт
    /// в json_object (форму задаёт system-подсказка вызывающей стороны).
    ///
    /// Сетевые/не-2xx ошибки не перехватываются здесь — это дело вызывающей
    /// стороны (linked CTS + try/catch, как вокруг вызова в TurnPipeline.ThinkAsync).
    /// А вот невалидный JSON в ответе обрабатывается: реализация делает один
    /// повторный вызов с показом модели её же битого ответа. Возвращает null,
    /// если даже после починки объект получить не удалось (или ответ пуст).
    /// </summary>
    Task<JsonObject?> CompleteJsonAsync(
        string system, string user, JsonReplySchema? schema, CancellationToken ct);

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
    /// Пустой, но не null объект: «отработал, предложить нечего» — не путать с
    /// недоступностью. Ход диалога распознаёт такой ответ как «модели нет» и
    /// уходит в Brain.Fallback, а экстракция полей ТЗ — как «данных не нашлось».
    /// </summary>
    public Task<JsonObject?> CompleteJsonAsync(
        string system, string user, JsonReplySchema? schema, CancellationToken ct) =>
        Task.FromResult<JsonObject?>(new JsonObject());

    /// <summary>
    /// Без ключа связного ответа взять неоткуда: возвращаем null, вызывающая
    /// ветка подставит свой детерминированный текст (Brain.Fallback).
    /// </summary>
    public Task<string?> CompleteAsync(string system, string user, CancellationToken ct) =>
        Task.FromResult<string?>(null);
}

public sealed class OpenAiLlm : ILlm
{
    private readonly HttpClient _http;
    private readonly AppConfig _config;

    // Взводится один раз, если провайдер отверг response_format json_schema:
    // дальше в этом процессе такие вызовы сразу идут в json_object, без
    // заведомо провального первого хода.
    private volatile bool _schemaUnsupported;

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
    /// Один нестримящийся round-trip с response_format: на нём держится и ход
    /// диалога (Brain.Schema), и извлечение полей ТЗ, и ревью текста.
    ///
    /// Три уровня устойчивости: (1) при LLM_STRUCTURED_OUTPUTS шлём строгую
    /// json_schema, при 400 с жалобой на response_format прозрачно откатываемся
    /// на json_object; (2) ответ разбираем терпимо (JsonReply.Extract снимает
    /// markdown-фенсы и текст вокруг); (3) если объект всё равно не собрался —
    /// один повторный вызов, показывающий модели её же битый ответ.
    /// </summary>
    public Task<JsonObject?> CompleteJsonAsync(
        string system, string user, JsonReplySchema? schema, CancellationToken ct) =>
        CompleteStructuredAsync(system, user, schema, ct);

    /// <summary>
    /// Нестримящийся текстовый вызов: один HTTP round-trip со stream=false и
    /// без response_format — модели не нужно «запираться» в JSON, она просто
    /// отвечает пользователю. Разбор choices[0].message.content общий с JSON-веткой.
    /// </summary>
    public async Task<string?> CompleteAsync(string system, string user, CancellationToken ct)
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
            }
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, "chat/completions")
        {
            Content = JsonContent.Create(payload)
        };
        using var response = await _http.SendAsync(request, ct);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException(
                $"LLM вернул {(int)response.StatusCode}: {error[..Math.Min(error.Length, 300)]}");
        }

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var choices = doc.RootElement.GetProperty("choices");
        if (choices.GetArrayLength() == 0) return null;

        var message = choices[0].GetProperty("message");
        var content = message.TryGetProperty("content", out var c) ? c.GetString() : null;
        return string.IsNullOrWhiteSpace(content) ? null : content.Trim();
    }

    /// <summary>Общий движок JSON-вызовов: используется и CompleteJsonAsync, и роутером.</summary>
    private async Task<JsonObject?> CompleteStructuredAsync(
        string system, string user, JsonReplySchema? schema, CancellationToken ct)
    {
        var withSchema = _config.StructuredOutputs && schema is not null && !_schemaUnsupported;

        var (content, providerRejectedSchema) = await CallJsonAsync(system, user, schema, withSchema, ct);
        if (providerRejectedSchema)
        {
            _schemaUnsupported = true;
            (content, _) = await CallJsonAsync(system, user, schema, withSchema: false, ct);
        }

        var parsed = JsonReply.Extract(content);
        if (parsed is not null) return parsed;

        var repairUser =
            user +
            "\n\n---\nТвой предыдущий ответ не удалось разобрать как JSON:\n" +
            (string.IsNullOrEmpty(content) ? "(пустой ответ)" : content[..Math.Min(content.Length, 800)]) +
            "\n\nВерни СТРОГО один JSON-объект. Без markdown, без ```, без пояснений до или после.";

        (content, _) = await CallJsonAsync(system, repairUser, schema, withSchema: false, ct);
        return JsonReply.Extract(content);
    }

    /// <summary>
    /// Один HTTP-ход JSON-вызова. Возвращает содержимое ответа и флаг «провайдер
    /// не понял json_schema» — по нему вызывающий код повторяет запрос в
    /// json_object. Прочие не-2xx по-прежнему летят исключением.
    /// </summary>
    private async Task<(string? Content, bool ProviderRejectedSchema)> CallJsonAsync(
        string system, string user, JsonReplySchema? schema, bool withSchema, CancellationToken ct)
    {
        object responseFormat = withSchema && schema is not null
            ? new
            {
                type = "json_schema",
                json_schema = new { name = schema.Name, schema = schema.Schema, strict = true }
            }
            : new { type = "json_object" };

        var payload = new
        {
            model = _config.LlmModel,
            stream = false,
            temperature = 0.1,
            messages = new object[]
            {
                new { role = "system", content = system },
                new { role = "user", content = user }
            },
            response_format = responseFormat
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, "chat/completions")
        {
            Content = JsonContent.Create(payload)
        };
        using var response = await _http.SendAsync(request, ct);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(ct);
            if (withSchema && (int)response.StatusCode == 400 &&
                error.Contains("response_format", StringComparison.OrdinalIgnoreCase))
                return (null, true);

            throw new HttpRequestException(
                $"LLM вернул {(int)response.StatusCode}: {error[..Math.Min(error.Length, 300)]}");
        }

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var choices = doc.RootElement.GetProperty("choices");
        if (choices.GetArrayLength() == 0) return (null, false);

        var message = choices[0].GetProperty("message");
        var content = message.TryGetProperty("content", out var c) ? c.GetString() : null;
        return (content, false);
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

        // Цикл по ReadLineAsync, а не по reader.EndOfStream: последнее делает
        // синхронное чтение вперёд и блокирует поток на сетевом I/O.
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            ct.ThrowIfCancellationRequested();
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
