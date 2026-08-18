using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace Prostor.Chat;

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

    public int EmbeddingTimeoutSeconds { get; init; } = 5;
    public int LlmTimeoutSeconds { get; init; } = 45;
    public int TurnTimeoutSeconds { get; init; } = 60;
    public int HeartbeatSeconds { get; init; } = 15;

    /// <summary>
    /// Рубильник роутера свободного текста (см. TurnPipeline.TryRouteAsync).
    /// false — старое поведение: любой свободный текст трактуется как поиск услуг,
    /// ILlm.DecideAsync вообще не вызывается. Быстрый откат без деплоя, если
    /// провайдер плохо держит tool calling.
    /// </summary>
    public bool EnableRouter { get; init; } = true;

    // ---------------------------------------------------------------- уверенность поиска
    // score из find_products — сумма разнородных величин, абсолютное значение
    // само по себе мало что значит. Поэтому решение принимается по трём
    // сигналам сразу (TurnPipeline.Assess): абсолютный уровень, наличие
    // доказательств (matched_terms) и отрыв лидера от остальной выдачи.
    // Замеры на реальном каталоге: точный запрос ~0.64, запрос с опечаткой,
    // но по адресу ~0.63, шум из услуг-дубликатов ~0.12–0.33.

    /// <summary>Ниже этого — «ничего не нашлось», карточки не показываем вовсе.</summary>
    public decimal SearchMinScore { get; init; } = 0.20m;

    /// <summary>Выше этого выдача подаётся как уверенная находка.</summary>
    public decimal SearchConfidentScore { get; init; } = 0.45m;

    /// <summary>
    /// Насколько лидер должен оторваться от второго места. Плоская выдача —
    /// признак шума: ровно так выглядели пять юридических услуг-дубликатов,
    /// набравших одинаковый балл на запрос про бурение.
    /// </summary>
    public decimal SearchMinMargin { get; init; } = 0.08m;

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

            EmbeddingTimeoutSeconds = GetInt("EMBEDDING_TIMEOUT_SECONDS", 5),
            LlmTimeoutSeconds = GetInt("LLM_TIMEOUT_SECONDS", 45),
            TurnTimeoutSeconds = GetInt("TURN_TIMEOUT_SECONDS", 60),
            EnableRouter = GetBool("ENABLE_ROUTER", true),

            SearchMinScore       = GetDecimal("SEARCH_MIN_SCORE", 0.20m),
            SearchConfidentScore = GetDecimal("SEARCH_CONFIDENT_SCORE", 0.45m),
            SearchMinMargin      = GetDecimal("SEARCH_MIN_MARGIN", 0.08m),
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
