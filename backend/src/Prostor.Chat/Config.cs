using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Prostor.Chat;

/// <summary>Одно текстовое поле ТЗ: идентификатор, заголовок и подсказка из
/// шаблона (tz.template.required_fields). Нужен экстрактору полей из диалога,
/// чтобы модель раскладывала переписку по реальным полям конструктора, а не
/// по захардкоженному списку.</summary>
public sealed record TzTextField(string Key, string Title, string? Hint);

public sealed class AppConfig
{
    public string ConnectionString { get; init; } = "";
    public string TzBaseUrl { get; init; } = "http://tz:8080";

    // LLM (чат): любой OpenAI-совместимый chat/completions endpoint. Сейчас — DeepSeek.
    public string LlmBaseUrl { get; init; } = "https://api.deepseek.com";
    public string LlmApiKey { get; init; } = "";
    public string LlmModel { get; init; } = "deepseek-v4-flash";

    // Эмбеддинги: отдельный endpoint (обычно локальный, без ключа) — модель и провайдер
    // могут не совпадать с LLM. По умолчанию — Ollama с bge-m3 на хосте.
    public string EmbeddingBaseUrl { get; init; } = "http://host.docker.internal:11434/v1";
    public string EmbeddingApiKey { get; init; } = "";
    public string EmbeddingModel { get; init; } = "bge-m3";

    /// <summary>Должна совпадать с vector(N) в db/init/01_schema.sql.</summary>
    public int EmbeddingDimensions { get; init; } = 1024;

    /// <summary>Без ключа LLM отвечает шаблонным «стенографом», без внешних вызовов.</summary>
    public bool UseStubLlm => string.IsNullOrWhiteSpace(LlmApiKey);

    /// <summary>Без адреса эмбеддера каталог индексируется детерминированной заглушкой.</summary>
    public bool UseStubEmbedder => string.IsNullOrWhiteSpace(EmbeddingBaseUrl);

    /// <summary>
    /// Слать ли в JSON-вызовах response_format json_schema (строгая схема)
    /// вместо json_object. По умолчанию false: DeepSeek на дефолтном эндпоинте
    /// строгую схему не держит, а json_object держит. true имеет смысл для
    /// OpenAI-совместимых провайдеров с поддержкой Structured Outputs (OpenAI,
    /// vLLM с guided json и т.п.). Если провайдер json_schema не понял, клиент
    /// сам откатывается на json_object; битый JSON чинится повторным вызовом
    /// независимо от этого флага (см. OpenAiLlm.CompleteJsonAsync).
    /// </summary>
    public bool StructuredOutputs { get; init; } = false;

    public int EmbeddingTimeoutSeconds { get; init; } = 5;
    public int LlmTimeoutSeconds { get; init; } = 45;
    public int TurnTimeoutSeconds { get; init; } = 60;
    public int HeartbeatSeconds { get; init; } = 15;

    /// <summary>
    /// Рубильник «мозга» диалога (см. TurnPipeline.ThinkAsync). false — ход
    /// идёт по детерминированной ветке Brain.Fallback: без выбранной услуги
    /// любой свободный текст трактуется как поиск, с выбранной — ответ
    /// собирается из состояния заявки. Быстрый откат без деплоя, если провайдер
    /// плохо держит структурированный ответ. Имя переменной оставлено прежним
    /// (ENABLE_ROUTER), чтобы не ломать существующие .env.
    /// </summary>
    public bool EnableRouter { get; init; } = true;

    // ---------------------------------------------------------------- уверенность поиска
    // score из find_products — сумма разнородных величин, абсолютное значение
    // само по себе мало что значит. Поэтому решение принимается по трём
    // сигналам сразу (TurnPipeline.Assess): абсолютный уровень, наличие
    // доказательств (matched_terms) и отрыв лидера от остальной выдачи.
    // Замеры на реальном каталоге после переноса веса на вектор: точный запрос
    // ~0.69, осмысленный запрос своими словами ~0.55, шум и запрос с опечаткой,
    // по которому ничего не нашлось, ~0.35–0.38.

    /// <summary>Ниже этого — «ничего не нашлось», карточки не показываем вовсе.</summary>
    public decimal SearchMinScore { get; init; } = 0.20m;

    /// <summary>Выше этого выдача подаётся как уверенная находка.</summary>
    public decimal SearchConfidentScore { get; init; } = 0.45m;

    /// <summary>
    /// Насколько лидер должен оторваться от второго места. Планка снижена
    /// вместе с переносом веса на семантический канал (db/init/04_functions.sql):
    /// score теперь на три четверти состоит из косинусной близости, а она у
    /// нескольких профильных услуг закономерно похожа — отрыв в 0.08 стал
    /// недостижим, и уверенная выдача уходила в «догадку» даже когда наверху
    /// стояла ровно та услуга, о которой спрашивали. Проверку на шум держат
    /// SearchConfidentScore и SearchSemanticFloor; здесь остаётся защита от
    /// действительно плоской выдачи — совпадения балл в балл у дубликатов.
    /// </summary>
    public decimal SearchMinMargin { get; init; } = 0.005m;

    /// <summary>
    /// Планка близости, когда лексических доказательств нет совсем
    /// (matched_terms пуст) и остаётся верить только вектору.
    /// </summary>
    public decimal SearchSemanticFloor { get; init; } = 0.60m;

    public int TopCompanies { get; init; } = 6;

    public int TopProducts { get; init; } = 5;
    public int TopExecutors { get; init; } = 6;
    public int TopStages { get; init; } = 12;

    public static AppConfig FromEnvironment(IConfiguration configuration)
    {
        string Get(string key, string fallback) =>
            configuration[key] is { Length: > 0 } value ? value : fallback;

        int GetInt(string key, int fallback) =>
            int.TryParse(configuration[key], out var value) ? value : fallback;

        bool GetBool(string key, bool fallback) =>
            bool.TryParse(configuration[key], out var value) ? value : fallback;

        decimal GetDecimal(string key, decimal fallback) =>
            decimal.TryParse(configuration[key], System.Globalization.NumberStyles.Float,
                             System.Globalization.CultureInfo.InvariantCulture, out var value)
                ? value : fallback;

        var host = Get("PGHOST", "db");
        var db = Get("PGDATABASE", "prostor");
        var user = Get("PGUSER", "prostor");
        var password = Get("PGPASSWORD", "prostor");
        var port = GetInt("PGPORT", 5432);

        return new AppConfig
        {
            ConnectionString = Get("DATABASE_URL",
                $"Host={host};Port={port};Database={db};Username={user};Password={password};" +
                "Include Error Detail=true;Maximum Pool Size=20"),
            TzBaseUrl = Get("TZ_BASE_URL", "http://tz:8080"),

            LlmBaseUrl = Get("LLM_BASE_URL", "https://api.deepseek.com"),
            LlmApiKey = Get("LLM_API_KEY", ""),
            LlmModel = Get("LLM_MODEL", "deepseek-v4-flash"),

            EmbeddingBaseUrl = Get("EMBEDDING_BASE_URL", "http://host.docker.internal:11434/v1"),
            EmbeddingApiKey = Get("EMBEDDING_API_KEY", ""),
            EmbeddingModel = Get("EMBEDDING_MODEL", "bge-m3"),
            EmbeddingDimensions = GetInt("EMBEDDING_DIMENSIONS", 1024),

            StructuredOutputs = GetBool("LLM_STRUCTURED_OUTPUTS", false),

            EmbeddingTimeoutSeconds = GetInt("EMBEDDING_TIMEOUT_SECONDS", 5),
            LlmTimeoutSeconds = GetInt("LLM_TIMEOUT_SECONDS", 45),
            TurnTimeoutSeconds = GetInt("TURN_TIMEOUT_SECONDS", 60),
            EnableRouter = GetBool("ENABLE_ROUTER", true),

            SearchMinScore       = GetDecimal("SEARCH_MIN_SCORE", 0.20m),
            SearchConfidentScore = GetDecimal("SEARCH_CONFIDENT_SCORE", 0.45m),
            SearchMinMargin      = GetDecimal("SEARCH_MIN_MARGIN", 0.005m),
            SearchSemanticFloor  = GetDecimal("SEARCH_SEMANTIC_FLOOR", 0.60m),

            TopProducts = GetInt("TOP_PRODUCTS", 5),
            TopCompanies = GetInt("TOP_COMPANIES", 6),
            TopExecutors = GetInt("TOP_EXECUTORS", 6),
            TopStages = GetInt("TOP_STAGES", 12)
        };
    }
}

/// <summary>Синхронный HTTP к генератору ТЗ. Ни очереди, ни outbox — операция на десятки миллисекунд.</summary>
public sealed class TzClient
{
    private readonly HttpClient _http;
    private readonly ILogger<TzClient> _log;

    public TzClient(HttpClient http, AppConfig config, ILogger<TzClient> log)
    {
        _http = http;
        _log = log;
        _http.BaseAddress = new Uri(config.TzBaseUrl.TrimEnd('/') + "/");
        _http.Timeout = TimeSpan.FromSeconds(5);
    }

    // Поля со значением-действием (клик, а не текст) экстрактору из диалога не
    // нужны: сроки/этапы/операции/исполнители заполняются кнопками и уже лежат
    // в ChatState. Модель раскладывает по диалогу только свободный текст.
    private static readonly HashSet<string> NonTextFieldKeys =
        new(StringComparer.Ordinal) { "period", "stages", "operations", "executors" };

    /// <summary>
    /// Список текстовых полей шаблона ТЗ (key/title/hint) для экстракции из
    /// диалога. Читается из конструктора по HTTP (GET /api/v1/tz/templates) —
    /// общих таблиц у сервисов нет. Отказ/недоступность — пустой список,
    /// вызывающая сторона решает, что показать пользователю.
    /// </summary>
    public async Task<IReadOnlyList<TzTextField>> GetTextFieldsAsync(string? templateId, CancellationToken ct)
    {
        var wanted = string.IsNullOrWhiteSpace(templateId) ? "tpl-generic" : templateId;
        try
        {
            using var response = await _http.GetAsync("api/v1/tz/templates", ct);
            response.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));

            if (!doc.RootElement.TryGetProperty("items", out var items) ||
                items.ValueKind != JsonValueKind.Array)
                return Array.Empty<TzTextField>();

            JsonElement? chosen = null;
            JsonElement? generic = null;
            foreach (var t in items.EnumerateArray())
            {
                var id = t.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                if (id == wanted) { chosen = t; break; }
                if (id == "tpl-generic") generic = t;
            }
            chosen ??= generic;
            if (chosen is null ||
                !chosen.Value.TryGetProperty("fields", out var fields) ||
                fields.ValueKind != JsonValueKind.Array)
                return Array.Empty<TzTextField>();

            var result = new List<TzTextField>();
            foreach (var f in fields.EnumerateArray())
            {
                var key = f.TryGetProperty("key", out var k) ? k.GetString() : null;
                if (string.IsNullOrEmpty(key) || NonTextFieldKeys.Contains(key)) continue;
                result.Add(new TzTextField(
                    key,
                    f.TryGetProperty("title", out var ti) ? ti.GetString() ?? key : key,
                    f.TryGetProperty("hint", out var h) ? h.GetString() : null));
            }
            return result;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _log.LogWarning(ex, "не удалось получить список полей ТЗ из конструктора");
            return Array.Empty<TzTextField>();
        }
    }

    public async Task<JsonObject?> DraftAsync(Guid sessionId, ChatState state, CancellationToken ct)
    {
        var payload = new
        {
            sessionId,
            templateId = state.TemplateId ?? "tpl-generic",
            state = System.Text.Json.JsonSerializer.Deserialize<JsonObject>(state.ToJson())
        };

        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                using var response = await _http.PostAsJsonAsync("internal/tz/draft", payload, ct);
                response.EnsureSuccessStatusCode();
                var body = await response.Content.ReadAsStringAsync(ct);
                return System.Text.Json.JsonSerializer.Deserialize<JsonObject>(body);
            }
            catch (Exception ex) when (attempt == 0 && !ct.IsCancellationRequested)
            {
                _log.LogWarning(ex, "первая попытка вызова TZ Generator не удалась, повторяем");
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "TZ Generator недоступен");
                return null;
            }
        }
        return null;
    }
}
