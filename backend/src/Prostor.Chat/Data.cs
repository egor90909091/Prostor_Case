using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;
using NpgsqlTypes;

namespace Prostor.Chat;

/// <param name="MatchedTerms">
/// Слова запроса, которые реально нашлись у услуги, — и только информативные:
/// SQL отбрасывает лексемы вроде «котор», встречающиеся в трети каталога.
/// Пустой массив означает, что лексических доказательств нет вообще и
/// единственным сигналом был вектор. Обоснования подбора в чате строятся
/// отсюда, а не из порогов «на глазок».
/// </param>
public record ProductHit(
    long Rank, string ProductId, string Name, string Category, string Snippet,
    decimal Similarity, decimal Lexical, decimal Fuzzy, decimal Popularity, decimal Score,
    string[] MatchedTerms,
    int ContractsCnt, int CalcsCnt, int CompaniesCnt, int? TypicalDays,
    int OperationsCnt, string TemplateId);

/// <summary>
/// Исполнитель, найденный по способностям (search.find_companies) — до выбора
/// услуги и без периода. Занятости здесь принципиально нет: без периода она не
/// определена, показывать её было бы враньём. Не путать с ExecutorHit, который
/// приходит из ops.find_executors и уже знает и продукт, и даты.
/// </summary>
public record CompanyHit(
    long Rank, string CompanyId, string Name, int Rating,
    decimal Similarity, decimal Lexical, decimal Score, string[] MatchedTerms,
    int CalcsCnt, int ProductsCnt, DateOnly? LastEndDate, string[] TopProducts, string Snippet);

/// <summary>
/// Покрытие индекса эмбеддингов. Индексатор заполняет только строки с
/// embedding IS NULL, поэтому смена модели (заглушка -> bge-m3) сама по себе
/// вектора не пересчитывает: старый индекс останется несовместимым с новыми
/// запросами и поиск будет тихо выдавать чушь. Показываем это в /health.
/// </summary>
public record EmbeddingCoverage(int Total, int Missing)
{
    public bool Complete => Total > 0 && Missing == 0;
}

public record ExecutorHit(
    long Rank, string CompanyId, string Name, int Rating, int Experience,
    DateOnly? LastEndDate, int BusyDays, int PeriodDays, string Availability,
    int LoadPct, bool Subcontract, bool IsFallback, decimal Score, string[] Reasons);

public record StageInfo(string Key, string Name, int UsedCount, int? MedianDays, string? Documentation);

public record ProductRisk(string Title, string Severity, int Count);

public record RelatedProduct(string ProductId, string Name, string Category, int Count, decimal Confidence);

public record SimilarCalc(
    string CalcId, string Name, string? CompanyName, string? ContractNumber,
    DateOnly? StartDate, DateOnly? EndDate, int? DurationDays, int StagesCount);

public record OperationInfo(string OperationId, string Name, bool Required, int Order);

public record ProductCard(
    string ProductId, string Name, string Category, int? TypicalDays,
    int CalcsCount, int CompaniesCount, string TemplateId);

/// <summary>
/// Доступ к данным. Вся релевантность и ранжирование — в SQL-функциях,
/// здесь только вызовы и маппинг.
/// </summary>
public sealed class Db
{
    private readonly NpgsqlDataSource _source;

    public Db(NpgsqlDataSource source) => _source = source;

    // ------------------------------------------------------------- кэш эмбеддингов
    public async Task<float[]?> GetCachedEmbeddingAsync(string hash, CancellationToken ct)
    {
        await using var cmd = _source.CreateCommand(
            "SELECT embedding::text FROM search.embedding_cache WHERE text_hash = @h");
        cmd.Parameters.AddWithValue("h", hash);
        var raw = await cmd.ExecuteScalarAsync(ct);
        return raw is string s ? ParseVector(s) : null;
    }

    public async Task PutCachedEmbeddingAsync(string hash, float[] vector, CancellationToken ct)
    {
        await using var cmd = _source.CreateCommand(
            "INSERT INTO search.embedding_cache (text_hash, embedding) VALUES (@h, @v::vector) " +
            "ON CONFLICT (text_hash) DO NOTHING");
        cmd.Parameters.AddWithValue("h", hash);
        cmd.Parameters.AddWithValue("v", VectorLiteral.From(vector));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ------------------------------------------------------------- индексация
    private static readonly HashSet<string> IndexableTables =
        new(StringComparer.Ordinal) { "search.product_chunk", "search.company_chunk" };

    public async Task<List<(long Id, string Text)>> GetChunksWithoutEmbeddingAsync(
        string table, int limit, CancellationToken ct)
    {
        // имя таблицы не может прийти извне: только из белого списка
        if (!IndexableTables.Contains(table)) throw new ArgumentException("недопустимая таблица", nameof(table));

        var result = new List<(long, string)>();
        await using var cmd = _source.CreateCommand(
            $"SELECT id, chunk_text FROM {table} WHERE embedding IS NULL ORDER BY id LIMIT @l");
        cmd.Parameters.AddWithValue("l", limit);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct)) result.Add((rd.GetInt64(0), rd.GetString(1)));
        return result;
    }

    public async Task SetChunkEmbeddingAsync(string table, long id, float[] vector, CancellationToken ct)
    {
        if (!IndexableTables.Contains(table)) throw new ArgumentException("недопустимая таблица", nameof(table));

        await using var cmd = _source.CreateCommand(
            $"UPDATE {table} SET embedding = @v::vector, updated_at = now() WHERE id = @id");
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("v", VectorLiteral.From(vector));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static float[] ParseVector(string literal)
    {
        var body = literal.Trim('[', ']');
        var parts = body.Split(',');
        var vector = new float[parts.Length];
        for (var i = 0; i < parts.Length; i++)
            vector[i] = float.Parse(parts[i], System.Globalization.CultureInfo.InvariantCulture);
        return vector;
    }

    // ------------------------------------------------------------- сессии
    public async Task<(Guid SessionId, ChatState State)> CreateSessionAsync(string? customer, CancellationToken ct)
    {
        await using var cmd = _source.CreateCommand(
            "INSERT INTO chat.session (customer_name, state) VALUES (@c, @s::jsonb) RETURNING session_id");
        var state = new ChatState { Customer = customer };
        cmd.Parameters.AddWithValue("c", (object?)customer ?? DBNull.Value);
        cmd.Parameters.AddWithValue("s", state.ToJson());
        var id = (Guid)(await cmd.ExecuteScalarAsync(ct))!;
        return (id, state);
    }

    public async Task<ChatState?> GetStateAsync(Guid sessionId, CancellationToken ct)
    {
        await using var cmd = _source.CreateCommand(
            "SELECT state::text FROM chat.session WHERE session_id = @id");
        cmd.Parameters.AddWithValue("id", sessionId);
        var raw = await cmd.ExecuteScalarAsync(ct);
        return raw is string s ? ChatState.Parse(s) : null;
    }

    /// <summary>Полное состояние «как есть» — им инициализируется конструктор ТЗ.</summary>
    public async Task<string?> GetStateJsonAsync(Guid sessionId, CancellationToken ct)
    {
        await using var cmd = _source.CreateCommand(
            "SELECT state::text FROM chat.session WHERE session_id = @id");
        cmd.Parameters.AddWithValue("id", sessionId);
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    public async Task SaveStateAsync(Guid sessionId, ChatState state, CancellationToken ct)
    {
        await using var cmd = _source.CreateCommand(
            "UPDATE chat.session SET state = @s::jsonb, updated_at = now() WHERE session_id = @id");
        cmd.Parameters.AddWithValue("id", sessionId);
        cmd.Parameters.AddWithValue("s", state.ToJson());
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<Guid> AppendMessageAsync(Guid sessionId, string role, string blocksJson, CancellationToken ct)
    {
        await using var cmd = _source.CreateCommand(
            "INSERT INTO chat.message (session_id, seq, role, blocks) " +
            "SELECT @id, coalesce(max(seq), 0) + 1, @r, @b::jsonb FROM chat.message WHERE session_id = @id " +
            "RETURNING message_id");
        cmd.Parameters.AddWithValue("id", sessionId);
        cmd.Parameters.AddWithValue("r", role);
        cmd.Parameters.AddWithValue("b", blocksJson);
        return (Guid)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task<List<(int Seq, string Role, string Blocks, DateTime CreatedAt)>> GetHistoryAsync(
        Guid sessionId, CancellationToken ct)
    {
        var result = new List<(int, string, string, DateTime)>();
        await using var cmd = _source.CreateCommand(
            "SELECT seq, role, blocks::text, created_at FROM chat.message " +
            "WHERE session_id = @id ORDER BY seq");
        cmd.Parameters.AddWithValue("id", sessionId);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
            result.Add((rd.GetInt32(0), rd.GetString(1), rd.GetString(2), rd.GetDateTime(3)));
        return result;
    }

    /// <summary>
    /// Плоский транскрипт диалога для экстракции полей ТЗ: по строке на реплику,
    /// «Заказчик:» / «Ассистент:». Берём только текстовые блоки (реплики
    /// пользователя и текстовые ответы бота вроде «Записал: объект — …»);
    /// блоки-действия и карточки в разбор не идут. Хвост обрезаем по лимиту
    /// символов — на длинных сессиях экономим и контекст, и таймаут.
    /// </summary>
    public async Task<string> GetDialogueTranscriptAsync(
        Guid sessionId, CancellationToken ct, int maxChars = 8000)
    {
        var history = await GetHistoryAsync(sessionId, ct);
        var lines = new List<string>();
        foreach (var (_, role, blocks, _) in history)
        {
            var text = ExtractPlainText(blocks);
            if (string.IsNullOrWhiteSpace(text)) continue;
            lines.Add($"{(role == "user" ? "Заказчик" : "Ассистент")}: {text}");
        }

        var transcript = string.Join("\n", lines).Trim();
        return transcript.Length <= maxChars
            ? transcript
            : transcript[^maxChars..];
    }

    private static string? ExtractPlainText(string blocksJson)
    {
        try
        {
            if (JsonNode.Parse(blocksJson) is not JsonArray array) return null;
            var parts = new List<string>();
            foreach (var block in array)
            {
                if (block is not JsonObject obj) continue;
                if (obj["type"]?.GetValue<string>() != "text") continue;
                var text = obj["text"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(text)) parts.Add(text.Trim());
            }
            return parts.Count == 0 ? null : string.Join(" ", parts);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // ------------------------------------------------------------- ходы диалога
    /// <summary>
    /// Идемпотентность и защита от параллельных ходов: пара
    /// (session_id, idempotency_key) уникальна. Повтор возвращает прежний ход.
    /// </summary>
    public async Task<(Guid TurnId, bool IsNew)> StartTurnAsync(
        Guid sessionId, string idempotencyKey, CancellationToken ct)
    {
        await using var cmd = _source.CreateCommand(
            "INSERT INTO chat.turn (session_id, idempotency_key) VALUES (@s, @k) " +
            "ON CONFLICT (session_id, idempotency_key) DO NOTHING RETURNING turn_id");
        cmd.Parameters.AddWithValue("s", sessionId);
        cmd.Parameters.AddWithValue("k", idempotencyKey);
        var created = await cmd.ExecuteScalarAsync(ct);
        if (created is Guid id) return (id, true);

        await using var find = _source.CreateCommand(
            "SELECT turn_id FROM chat.turn WHERE session_id = @s AND idempotency_key = @k");
        find.Parameters.AddWithValue("s", sessionId);
        find.Parameters.AddWithValue("k", idempotencyKey);
        return ((Guid)(await find.ExecuteScalarAsync(ct))!, false);
    }

    /// <summary>Один активный ход на сессию: второй параллельный запрос получит 409.</summary>
    public async Task<Guid?> GetRunningTurnAsync(Guid sessionId, CancellationToken ct)
    {
        await using var cmd = _source.CreateCommand(
            "SELECT turn_id FROM chat.turn " +
            "WHERE session_id = @s AND status = 'running' AND created_at > now() - interval '2 minutes' " +
            "LIMIT 1");
        cmd.Parameters.AddWithValue("s", sessionId);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is Guid id ? id : null;
    }

    public async Task AppendTurnEventAsync(Guid turnId, string eventJson, CancellationToken ct)
    {
        await using var cmd = _source.CreateCommand(
            "UPDATE chat.turn SET events = events || @e::jsonb WHERE turn_id = @id");
        cmd.Parameters.AddWithValue("id", turnId);
        cmd.Parameters.Add(new NpgsqlParameter("e", NpgsqlDbType.Jsonb) { Value = "[" + eventJson + "]" });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task FinishTurnAsync(Guid turnId, string status, CancellationToken ct)
    {
        await using var cmd = _source.CreateCommand("UPDATE chat.turn SET status = @st WHERE turn_id = @id");
        cmd.Parameters.AddWithValue("id", turnId);
        cmd.Parameters.AddWithValue("st", status);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<(string Status, string Events)?> GetTurnAsync(Guid turnId, CancellationToken ct)
    {
        await using var cmd = _source.CreateCommand(
            "SELECT status, events::text FROM chat.turn WHERE turn_id = @id");
        cmd.Parameters.AddWithValue("id", turnId);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        return await rd.ReadAsync(ct) ? (rd.GetString(0), rd.GetString(1)) : null;
    }

    // ------------------------------------------------------------- поиск
    /// <param name="embedding">
    /// null — эмбеддинг-сервис недоступен: поиск деградирует до полнотекстового
    /// канала, а не падает целиком.
    /// </param>
    public async Task<List<ProductHit>> FindProductsAsync(
        float[]? embedding, string query, int top, CancellationToken ct)
    {
        var result = new List<ProductHit>();
        await using var cmd = _source.CreateCommand(
            "SELECT rank, product_id, name, category, snippet, " +
            "       similarity, lexical, fuzzy, popularity, score, matched_terms, " +
            "       contracts_cnt, calcs_cnt, companies_cnt, typical_days, operations_cnt, template_id " +
            "FROM search.find_products(@e::vector, @q, @t)");
        cmd.Parameters.Add(new NpgsqlParameter("e", NpgsqlDbType.Text)
        {
            Value = embedding is null ? DBNull.Value : VectorLiteral.From(embedding)
        });
        cmd.Parameters.AddWithValue("q", query);
        cmd.Parameters.AddWithValue("t", top);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
        {
            result.Add(new ProductHit(
                rd.GetInt64(0), rd.GetString(1), rd.GetString(2),
                rd.IsDBNull(3) ? "" : rd.GetString(3),
                rd.IsDBNull(4) ? "" : rd.GetString(4),
                rd.GetDecimal(5), rd.GetDecimal(6), rd.GetDecimal(7), rd.GetDecimal(8), rd.GetDecimal(9),
                rd.IsDBNull(10) ? Array.Empty<string>() : rd.GetFieldValue<string[]>(10),
                rd.GetInt32(11), rd.GetInt32(12), rd.GetInt32(13),
                rd.IsDBNull(14) ? null : rd.GetInt32(14),
                rd.GetInt32(15), rd.GetString(16)));
        }
        return result;
    }

    /// <summary>
    /// Подбор исполнителей по способностям — до выбора услуги и без периода.
    /// Отвечает на вопрос «кто вообще это умеет», на который ops.find_executors
    /// ответить не может: та требует product_id и даты.
    /// </summary>
    public async Task<List<CompanyHit>> FindCompaniesAsync(
        float[]? embedding, string query, int top, CancellationToken ct)
    {
        var result = new List<CompanyHit>();
        await using var cmd = _source.CreateCommand(
            "SELECT rank, company_id, name, rating, similarity, lexical, score, matched_terms, " +
            "       calcs_cnt, products_cnt, last_end_date, top_products, snippet " +
            "FROM search.find_companies(@e::vector, @q, @t)");
        cmd.Parameters.Add(new NpgsqlParameter("e", NpgsqlDbType.Text)
        {
            Value = embedding is null ? DBNull.Value : VectorLiteral.From(embedding)
        });
        cmd.Parameters.AddWithValue("q", query);
        cmd.Parameters.AddWithValue("t", top);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
        {
            result.Add(new CompanyHit(
                rd.GetInt64(0), rd.GetString(1), rd.GetString(2), rd.GetInt32(3),
                rd.GetDecimal(4), rd.GetDecimal(5), rd.GetDecimal(6),
                rd.IsDBNull(7) ? Array.Empty<string>() : rd.GetFieldValue<string[]>(7),
                rd.GetInt32(8), rd.GetInt32(9),
                rd.IsDBNull(10) ? null : rd.GetFieldValue<DateOnly>(10),
                rd.IsDBNull(11) ? Array.Empty<string>() : rd.GetFieldValue<string[]>(11),
                rd.IsDBNull(12) ? "" : rd.GetString(12)));
        }
        return result;
    }

    /// <summary>
    /// Полный справочник компаний — для переключателя роли в шапке.
    /// Это не поиск: ранжирование здесь не при чём, нужен ровный список
    /// всех активных исполнителей в алфавитном порядке.
    /// </summary>
    public async Task<List<CompanyRef>> ListCompaniesAsync(CancellationToken ct)
    {
        var result = new List<CompanyRef>();
        await using var cmd = _source.CreateCommand(
            "SELECT company_id, code, name, rating FROM catalog.company " +
            "WHERE is_active ORDER BY name");
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
            result.Add(new CompanyRef(
                rd.GetString(0), rd.GetString(1), rd.GetString(2), rd.GetInt32(3)));
        return result;
    }

    /// <summary>
    /// Сколько чанков каталога осталось без эмбеддинга. Ноль из ненулевого
    /// total — индекс готов; иначе семантический канал работает частично или
    /// не работает вовсе, и это надо видеть, а не угадывать по странной выдаче.
    /// </summary>
    public async Task<EmbeddingCoverage> GetEmbeddingCoverageAsync(CancellationToken ct)
    {
        await using var cmd = _source.CreateCommand(
            "SELECT count(*)::int, count(*) FILTER (WHERE embedding IS NULL)::int " +
            "FROM search.product_chunk");
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        return await rd.ReadAsync(ct)
            ? new EmbeddingCoverage(rd.GetInt32(0), rd.GetInt32(1))
            : new EmbeddingCoverage(0, 0);
    }

    public async Task<ProductCard?> GetProductAsync(string productId, CancellationToken ct)
    {
        await using var cmd = _source.CreateCommand("""
            SELECT p.product_id, p.name, coalesce(p.category, ''),
                   s.typical_duration_days, coalesce(s.calcs_cnt, 0), coalesce(s.companies_cnt, 0),
                   coalesce((SELECT t.template_id FROM tz.template t
                              WHERE p.product_id = ANY (t.product_ids) LIMIT 1), 'tpl-generic')
            FROM catalog.product p
            LEFT JOIN analytics.product_stats s ON s.product_id = p.product_id
            WHERE p.product_id = @p
            """);
        cmd.Parameters.AddWithValue("p", productId);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        if (!await rd.ReadAsync(ct)) return null;
        return new ProductCard(
            rd.GetString(0), rd.GetString(1), rd.GetString(2),
            rd.IsDBNull(3) ? null : rd.GetInt32(3),
            rd.GetInt32(4), rd.GetInt32(5), rd.GetString(6));
    }

    public async Task<List<StageInfo>> GetStagesAsync(string productId, int top, CancellationToken ct)
    {
        var result = new List<StageInfo>();
        await using var cmd = _source.CreateCommand(
            "SELECT stage_key, name, used_cnt, median_days, documentation " +
            "FROM catalog.product_stages(@p, @t)");
        cmd.Parameters.AddWithValue("p", productId);
        cmd.Parameters.AddWithValue("t", top);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
            result.Add(new StageInfo(rd.GetString(0), rd.GetString(1), rd.GetInt32(2),
                rd.IsDBNull(3) ? null : rd.GetInt32(3),
                rd.IsDBNull(4) ? null : rd.GetString(4)));
        return result;
    }

    // HAVING отсекает риски, встретившиеся в 1-2 ТЗ по услуге — на такой выборке
    // это шум, а не типовой паттерн (тот же принцип, что calcs_cnt >= 10
    // у productizationCandidates в GetAnalyticsJsonAsync).
    public async Task<List<ProductRisk>> GetProductRisksAsync(string productId, int top, CancellationToken ct)
    {
        var result = new List<ProductRisk>();
        await using var cmd = _source.CreateCommand(
            "SELECT r->>'title' AS title, r->>'severity' AS severity, count(*)::int AS cnt " +
            "FROM tz.document d, jsonb_array_elements(d.risks) r " +
            "WHERE d.product_id = @p " +
            "GROUP BY 1, 2 HAVING count(*) >= 3 ORDER BY cnt DESC LIMIT @t");
        cmd.Parameters.AddWithValue("p", productId);
        cmd.Parameters.AddWithValue("t", top);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
            result.Add(new ProductRisk(rd.GetString(0), rd.GetString(1), rd.GetInt32(2)));
        return result;
    }

    public async Task<List<OperationInfo>> GetOperationsAsync(string productId, CancellationToken ct)
    {
        var result = new List<OperationInfo>();
        await using var cmd = _source.CreateCommand(
            "SELECT operation_id, name, is_required, order_num FROM catalog.operation " +
            "WHERE product_id = @p ORDER BY order_num");
        cmd.Parameters.AddWithValue("p", productId);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
            result.Add(new OperationInfo(rd.GetString(0), rd.GetString(1), rd.GetBoolean(2), rd.GetInt32(3)));
        return result;
    }

    public async Task<List<ExecutorHit>> FindExecutorsAsync(
        string productId, DateOnly from, DateOnly to, bool allowSubcontract, int top, CancellationToken ct)
    {
        var result = new List<ExecutorHit>();
        await using var cmd = _source.CreateCommand(
            "SELECT rank, company_id, name, rating, experience, last_end_date, busy_days, period_days, " +
            "       availability, load_pct, subcontract, is_fallback, score, reasons " +
            "FROM ops.find_executors(@p, @f, @t, @s, @n)");
        cmd.Parameters.AddWithValue("p", productId);
        cmd.Parameters.AddWithValue("f", from);
        cmd.Parameters.AddWithValue("t", to);
        cmd.Parameters.AddWithValue("s", allowSubcontract);
        cmd.Parameters.AddWithValue("n", top);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
        {
            result.Add(new ExecutorHit(
                rd.GetInt64(0), rd.GetString(1), rd.GetString(2), rd.GetInt32(3), rd.GetInt32(4),
                rd.IsDBNull(5) ? null : rd.GetFieldValue<DateOnly>(5),
                rd.GetInt32(6), rd.GetInt32(7), rd.GetString(8), rd.GetInt32(9),
                rd.GetBoolean(10), rd.GetBoolean(11), rd.GetDecimal(12),
                rd.IsDBNull(13) ? Array.Empty<string>() : rd.GetFieldValue<string[]>(13)));
        }
        return result;
    }

    public async Task<Dictionary<string, string>> GetCompanyNamesAsync(
        IReadOnlyCollection<string> ids, CancellationToken ct)
    {
        var result = new Dictionary<string, string>();
        if (ids.Count == 0) return result;

        await using var cmd = _source.CreateCommand(
            "SELECT company_id, name FROM catalog.company WHERE company_id = ANY(@ids)");
        cmd.Parameters.AddWithValue("ids", ids.ToArray());
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct)) result[rd.GetString(0)] = rd.GetString(1);
        return result;
    }

    public async Task<List<RelatedProduct>> GetRelatedAsync(string productId, int top, CancellationToken ct)
    {
        var result = new List<RelatedProduct>();
        await using var cmd = _source.CreateCommand(
            "SELECT product_id, name, category, cnt, confidence FROM analytics.related_products(@p, @t)");
        cmd.Parameters.AddWithValue("p", productId);
        cmd.Parameters.AddWithValue("t", top);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
            result.Add(new RelatedProduct(rd.GetString(0), rd.GetString(1),
                rd.IsDBNull(2) ? "" : rd.GetString(2), rd.GetInt32(3), rd.GetDecimal(4)));
        return result;
    }

    public async Task<List<SimilarCalc>> GetSimilarCalcsAsync(string productId, int top, CancellationToken ct)
    {
        var result = new List<SimilarCalc>();
        await using var cmd = _source.CreateCommand(
            "SELECT calc_id, calc_name, company_name, contract_number, start_date, end_date, " +
            "       duration_days, stages_cnt FROM ops.similar_calcs(@p, @t)");
        cmd.Parameters.AddWithValue("p", productId);
        cmd.Parameters.AddWithValue("t", top);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
            result.Add(new SimilarCalc(
                rd.GetString(0),
                rd.IsDBNull(1) ? "" : rd.GetString(1),
                rd.IsDBNull(2) ? null : rd.GetString(2),
                rd.IsDBNull(3) ? null : rd.GetString(3),
                rd.IsDBNull(4) ? null : rd.GetFieldValue<DateOnly>(4),
                rd.IsDBNull(5) ? null : rd.GetFieldValue<DateOnly>(5),
                rd.IsDBNull(6) ? null : rd.GetInt32(6),
                rd.GetInt32(7)));
        return result;
    }

    // ------------------------------------------------------------- аналитика
    /// <param name="recognized">
    /// Именно «агент понял запрос», а не «SQL что-то вернул». Раньше сюда шло
    /// hits > 0, из-за чего запрос про бурение, на который выдали пять
    /// юридических услуг, попадал в распознанные, а витрина «запросы, не
    /// распознанные агентом» оставалась пустой при любом качестве поиска.
    /// </param>
    public async Task LogSearchAsync(
        Guid sessionId, string query, string? topProductId, decimal? topScore, int hits,
        bool recognized, CancellationToken ct)
    {
        await using var cmd = _source.CreateCommand(
            "INSERT INTO analytics.search_log (session_id, query, top_product_id, top_score, hits, recognized) " +
            "VALUES (@s, @q, @p, @sc, @h, @r)");
        cmd.Parameters.AddWithValue("s", sessionId);
        cmd.Parameters.AddWithValue("q", query);
        cmd.Parameters.AddWithValue("p", (object?)topProductId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("sc", (object?)topScore ?? DBNull.Value);
        cmd.Parameters.AddWithValue("h", hits);
        cmd.Parameters.AddWithValue("r", recognized);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task LogEventAsync(Guid sessionId, string kind, string payloadJson, CancellationToken ct)
    {
        await using var cmd = _source.CreateCommand(
            "INSERT INTO analytics.event (session_id, kind, payload) VALUES (@s, @k, @p::jsonb)");
        cmd.Parameters.AddWithValue("s", sessionId);
        cmd.Parameters.AddWithValue("k", kind);
        cmd.Parameters.AddWithValue("p", payloadJson);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Витрина согласования ТЗ. Отдельным запросом, а не частью общего
    /// дашборда: миграция 08_review.sql могла быть не накачена на живой базе,
    /// и тогда весь экран аналитики падал бы из-за отсутствующей таблицы.
    /// null означает «раздела согласования на дашборде не будет».
    /// </summary>
    public async Task<string?> GetReviewStatsJsonAsync(CancellationToken ct)
    {
        const string sql = """
            SELECT json_build_object(
              'sent',     count(*)::int,
              'pending',  count(*) FILTER (WHERE status IN ('sent', 'viewed'))::int,
              'approved', count(*) FILTER (WHERE status = 'approved')::int,
              'revision', count(*) FILTER (WHERE status = 'revision')::int,
              'rejected', count(*) FILTER (WHERE status = 'rejected')::int,
              'avgDecisionHours', coalesce(round(avg(
                   extract(epoch FROM (decided_at - created_at)) / 3600
                 ) FILTER (WHERE decided_at IS NOT NULL))::int, 0)
            )::text
            FROM tz.assignment
            """;
        try
        {
            await using var cmd = _source.CreateCommand(sql);
            return (string?)await cmd.ExecuteScalarAsync(ct);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public async Task<string> GetAnalyticsJsonAsync(CancellationToken ct)
    {
        // Одним запросом собираем весь дашборд: пять витрин из требований к MVP
        const string sql = """
            SELECT json_build_object(
              'topSearchedProducts', (
                 SELECT coalesce(json_agg(x), '[]'::json) FROM (
                   SELECT p.name, count(*)::int AS cnt
                   FROM analytics.search_log l JOIN catalog.product p ON p.product_id = l.top_product_id
                   GROUP BY p.name ORDER BY cnt DESC LIMIT 8) x),
              'unrecognizedQueries', (
                 SELECT coalesce(json_agg(x), '[]'::json) FROM (
                   SELECT query, created_at FROM analytics.search_log
                   WHERE NOT recognized ORDER BY created_at DESC LIMIT 10) x),
              'topPairs', (
                 SELECT coalesce(json_agg(x), '[]'::json) FROM (
                   SELECT a.name AS product, b.name AS related, c.cnt
                   FROM analytics.product_cooccurrence c
                   JOIN catalog.product a ON a.product_id = c.product_id
                   JOIN catalog.product b ON b.product_id = c.related_product_id
                   WHERE a.product_id < b.product_id
                   ORDER BY c.cnt DESC LIMIT 8) x),
              'topExecutors', (
                 SELECT coalesce(json_agg(x), '[]'::json) FROM (
                   SELECT co.name, sum(s.calcs_cnt)::int AS works,
                          count(DISTINCT s.product_id)::int AS products
                   FROM analytics.company_product_stats s
                   JOIN catalog.company co ON co.company_id = s.company_id
                   GROUP BY co.name ORDER BY works DESC LIMIT 8) x),
              'tzCreated', (SELECT count(*)::int FROM tz.document),
              'tzAvgReadiness', (SELECT coalesce(round(avg(readiness))::int, 0) FROM tz.document),
              'tzByTemplate', (
                 SELECT coalesce(json_agg(x), '[]'::json) FROM (
                   SELECT t.name, count(*)::int AS cnt
                   FROM tz.document d JOIN tz.template t ON t.template_id = d.template_id
                   GROUP BY t.name ORDER BY cnt DESC) x),
              'topRisks', (
                 SELECT coalesce(json_agg(x), '[]'::json) FROM (
                   SELECT r->>'title' AS title, r->>'severity' AS severity, count(*)::int AS cnt
                   FROM tz.document d, jsonb_array_elements(d.risks) r
                   GROUP BY 1, 2 ORDER BY cnt DESC LIMIT 8) x),
              'topStages', (
                 SELECT coalesce(json_agg(x), '[]'::json) FROM (
                   SELECT s->>'name' AS name, count(*)::int AS cnt
                   FROM tz.document d, jsonb_array_elements(d.payload->'stages') s
                   GROUP BY 1 ORDER BY cnt DESC LIMIT 8) x),
              'productizationCandidates', (
                 SELECT coalesce(json_agg(x), '[]'::json) FROM (
                   SELECT p.name, s.calcs_cnt, s.companies_cnt, s.typical_duration_days
                   FROM analytics.product_stats s JOIN catalog.product p ON p.product_id = s.product_id
                   WHERE s.calcs_cnt >= 10 ORDER BY s.calcs_cnt DESC LIMIT 8) x),
              'requestsByDay', (
                 SELECT coalesce(json_agg(x), '[]'::json) FROM (
                   SELECT to_char(d.day, 'YYYY-MM-DD') AS day,
                          count(l.id) FILTER (WHERE l.recognized)::int      AS recognized,
                          count(l.id) FILTER (WHERE NOT l.recognized)::int  AS unrecognized
                   FROM generate_series(current_date - interval '29 days', current_date, interval '1 day') d(day)
                   LEFT JOIN analytics.search_log l ON l.created_at::date = d.day::date
                   GROUP BY d.day ORDER BY d.day) x),
              'tzByDay', (
                 SELECT coalesce(json_agg(x), '[]'::json) FROM (
                   SELECT to_char(d.day, 'YYYY-MM-DD') AS day,
                          count(doc.tz_id)::int AS cnt
                   FROM generate_series(current_date - interval '29 days', current_date, interval '1 day') d(day)
                   LEFT JOIN tz.document doc ON doc.created_at::date = d.day::date
                   GROUP BY d.day ORDER BY d.day) x)
            )::text
            """;
        await using var cmd = _source.CreateCommand(sql);
        return (string)(await cmd.ExecuteScalarAsync(ct))!;
    }
}

public sealed record CompanyRef(string CompanyId, string Code, string Name, int Rating);
