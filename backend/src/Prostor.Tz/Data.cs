using System.Text.Json.Nodes;
using Npgsql;
using NpgsqlTypes;

namespace Prostor.Tz;

public sealed class TzDb
{
    private readonly NpgsqlDataSource _source;

    public TzDb(NpgsqlDataSource source) => _source = source;

    public async Task<List<TemplateDefinition>> GetTemplatesAsync(CancellationToken ct)
    {
        var result = new List<TemplateDefinition>();
        await using var cmd = _source.CreateCommand(
            "SELECT template_id, name, type_code, sections::text, required_fields::text, risk_rules::text " +
            "FROM tz.template ORDER BY name");
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
            result.Add(Map(rd));
        return result;
    }

    public async Task<TemplateDefinition?> GetTemplateAsync(string templateId, CancellationToken ct)
    {
        await using var cmd = _source.CreateCommand(
            "SELECT template_id, name, type_code, sections::text, required_fields::text, risk_rules::text " +
            "FROM tz.template WHERE template_id = @id");
        cmd.Parameters.AddWithValue("id", templateId);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        return await rd.ReadAsync(ct) ? Map(rd) : null;
    }

    private static TemplateDefinition Map(NpgsqlDataReader rd) => new(
        rd.GetString(0), rd.GetString(1), rd.GetString(2),
        JsonNode.Parse(rd.GetString(3))!.AsArray(),
        JsonNode.Parse(rd.GetString(4))!.AsArray(),
        JsonNode.Parse(rd.GetString(5))!.AsArray());

    /// <summary>Типовой срок по истории — база для риска «срок ниже типового».</summary>
    public async Task<int?> GetTypicalDaysAsync(string? productId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(productId)) return null;
        await using var cmd = _source.CreateCommand(
            "SELECT typical_duration_days FROM analytics.product_stats WHERE product_id = @p");
        cmd.Parameters.AddWithValue("p", productId);
        var value = await cmd.ExecuteScalarAsync(ct);
        return value is int days ? days : null;
    }

    public async Task<SavedDocument> SaveDocumentAsync(
        Guid? sessionId, string templateId, string? productId, string[] companyIds,
        JsonObject payload, int readiness, JsonArray risks, string storageKey,
        Guid? parentTzId, string status, CancellationToken ct)
    {
        // Версия считается по двум ключевым группировкам:
        //   * parent_tz_id — ручные правки в конструкторе: каждая новая
        //     версия ссылается на исходный tz_id, поэтому ищем max(version)
        //     по всем строкам с тем же parent_tz_id плюс по самому корню.
        //   * session_id — ТЗ, собранные из чата: parent_tz_id = NULL,
        //     обратная совместимость со старым поведением сохраняется.
        var versionFilter = parentTzId is { } root
            ? "WHERE parent_tz_id = @root OR tz_id = @root"
            : "WHERE session_id IS NOT DISTINCT FROM @s";

        await using var cmd = _source.CreateCommand($$"""
            INSERT INTO tz.document
                (session_id, template_id, product_id, company_ids, payload, readiness,
                 risks, storage_key, version, parent_tz_id, status)
            VALUES (@s, @t, @p, @c, @pl::jsonb, @r, @rs::jsonb, @k,
                    (SELECT coalesce(max(version), 0) + 1 FROM tz.document {{versionFilter}}),
                    @pt, @st)
            RETURNING tz_id, version
            """);
        cmd.Parameters.AddWithValue("s", (object?)sessionId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("t", templateId);
        cmd.Parameters.AddWithValue("p", (object?)productId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("c", companyIds);
        cmd.Parameters.Add(new NpgsqlParameter("pl", NpgsqlDbType.Jsonb) { Value = payload.ToJsonString() });
        cmd.Parameters.AddWithValue("r", readiness);
        cmd.Parameters.Add(new NpgsqlParameter("rs", NpgsqlDbType.Jsonb) { Value = risks.ToJsonString() });
        cmd.Parameters.AddWithValue("k", storageKey);
        cmd.Parameters.AddWithValue("pt", (object?)parentTzId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("st", status);
        if (parentTzId is { } rootId)
            cmd.Parameters.AddWithValue("root", rootId);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        await rd.ReadAsync(ct);
        return new SavedDocument(rd.GetGuid(0), rd.GetInt32(1));
    }

    public async Task<TzDocument?> GetDocumentAsync(Guid tzId, CancellationToken ct)
    {
        await using var cmd = _source.CreateCommand(
            "SELECT tz_id, session_id, template_id, product_id, payload::text, readiness, " +
            "       risks::text, version, storage_key, created_at, parent_tz_id, status " +
            "FROM tz.document WHERE tz_id = @id");
        cmd.Parameters.AddWithValue("id", tzId);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        if (!await rd.ReadAsync(ct)) return null;

        return new TzDocument(
            rd.GetGuid(0),
            rd.IsDBNull(1) ? null : rd.GetGuid(1),
            rd.GetString(2),
            rd.IsDBNull(3) ? null : rd.GetString(3),
            rd.GetString(4),
            rd.GetInt32(5),
            rd.GetString(6),
            rd.GetInt32(7),
            rd.IsDBNull(8) ? null : rd.GetString(8),
            rd.GetDateTime(9),
            rd.IsDBNull(10) ? null : rd.GetGuid(10),
            rd.GetString(11));
    }

    /// <summary>Все версии одного ТЗ: корневой документ и его потомки.</summary>
    public async Task<List<TzVersionItem>> GetDocumentVersionsAsync(Guid tzId, CancellationToken ct)
    {
        var result = new List<TzVersionItem>();
        await using var cmd = _source.CreateCommand("""
            SELECT tz_id, version, readiness, created_at,
                   coalesce(p.name, '—'),
                   coalesce(payload->>'object', '—'),
                   status
            FROM tz.document d
            LEFT JOIN catalog.product p ON p.product_id = d.product_id
            WHERE d.tz_id = @id OR d.parent_tz_id = @id
            ORDER BY d.version ASC
            """);
        cmd.Parameters.AddWithValue("id", tzId);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
            result.Add(new TzVersionItem(
                rd.GetGuid(0), rd.GetInt32(1), rd.GetInt32(2), rd.GetDateTime(3),
                rd.GetString(4), rd.GetString(5), rd.GetString(6)));
        return result;
    }

    public async Task<List<TzListItem>> ListDocumentsAsync(int limit, CancellationToken ct)
    {
        var result = new List<TzListItem>();
        // Статусы направлений подтягиваются одним LATERAL-подзапросом: список
        // «Мои заявки» показывает сводный статус согласования прямо в строке,
        // без отдельного запроса на каждый документ. Тред считается по корню
        // цепочки версий — обсуждение переживает правки ТЗ.
        await using var cmd = _source.CreateCommand("""
            SELECT d.tz_id, d.created_at, d.readiness, t.name,
                   coalesce(p.name, '—'),
                   coalesce(d.payload->>'object', '—'),
                   jsonb_array_length(d.risks),
                   d.status,
                   coalesce(a.statuses, ARRAY[]::text[]),
                   coalesce(c.cnt, 0)
            FROM tz.document d
            JOIN tz.template t ON t.template_id = d.template_id
            LEFT JOIN catalog.product p ON p.product_id = d.product_id
            LEFT JOIN LATERAL (
                SELECT array_agg(status) AS statuses
                FROM tz.assignment WHERE tz_id = d.tz_id
            ) a ON true
            LEFT JOIN LATERAL (
                SELECT count(*) AS cnt
                FROM tz.comment WHERE root_tz_id = coalesce(d.parent_tz_id, d.tz_id)
            ) c ON true
            ORDER BY d.created_at DESC
            LIMIT @l
            """);
        cmd.Parameters.AddWithValue("l", limit);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
            result.Add(new TzListItem(
                rd.GetGuid(0), rd.GetDateTime(1), rd.GetInt32(2), rd.GetString(3),
                rd.GetString(4), rd.GetString(5), rd.GetInt32(6), rd.GetString(7),
                ReviewRules.Summarize(rd.GetFieldValue<string[]>(8)),
                (int)rd.GetInt64(9)));
        return result;
    }

    // ------------------------------------------------------- согласование

    /// <summary>
    /// Корень цепочки версий. Тред замечаний и история согласования живут
    /// по нему, а не по tz_id: после правок по замечаниям создаётся новая
    /// версия документа, а обсуждение должно остаться тем же.
    /// </summary>
    public async Task<Guid?> GetRootTzIdAsync(Guid tzId, CancellationToken ct)
    {
        await using var cmd = _source.CreateCommand(
            "SELECT coalesce(parent_tz_id, tz_id) FROM tz.document WHERE tz_id = @id");
        cmd.Parameters.AddWithValue("id", tzId);
        var value = await cmd.ExecuteScalarAsync(ct);
        return value is Guid root ? root : null;
    }

    /// <summary>Направляет версию ТЗ компаниям. Возвращает число новых направлений.</summary>
    public async Task<int> CreateAssignmentsAsync(
        Guid tzId, Guid rootTzId, string[] companyIds, string? note, CancellationToken ct)
    {
        await using var cmd = _source.CreateCommand("""
            INSERT INTO tz.assignment (tz_id, root_tz_id, company_id, note)
            SELECT @tz, @root, x, @note
            FROM unnest(@companies::text[]) AS x
            WHERE EXISTS (SELECT 1 FROM catalog.company c WHERE c.company_id = x)
            ON CONFLICT (tz_id, company_id) DO NOTHING
            """);
        cmd.Parameters.AddWithValue("tz", tzId);
        cmd.Parameters.AddWithValue("root", rootTzId);
        cmd.Parameters.AddWithValue("companies", companyIds);
        cmd.Parameters.AddWithValue("note", (object?)note ?? DBNull.Value);
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Направления по всей цепочке версий одного ТЗ — для заказчика.</summary>
    public async Task<List<AssignmentItem>> GetAssignmentsAsync(Guid rootTzId, CancellationToken ct)
    {
        var result = new List<AssignmentItem>();
        await using var cmd = _source.CreateCommand("""
            SELECT a.assignment_id, a.tz_id, d.version, a.company_id, c.name, c.code,
                   a.status, a.note, a.created_at, a.viewed_at, a.decided_at
            FROM tz.assignment a
            JOIN catalog.company c ON c.company_id = a.company_id
            JOIN tz.document d ON d.tz_id = a.tz_id
            WHERE a.root_tz_id = @root
            ORDER BY a.created_at DESC
            """);
        cmd.Parameters.AddWithValue("root", rootTzId);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
            result.Add(new AssignmentItem(
                rd.GetGuid(0), rd.GetGuid(1), rd.GetInt32(2), rd.GetString(3),
                rd.GetString(4), rd.GetString(5), rd.GetString(6),
                rd.IsDBNull(7) ? null : rd.GetString(7),
                rd.GetDateTime(8),
                rd.IsDBNull(9) ? null : rd.GetDateTime(9),
                rd.IsDBNull(10) ? null : rd.GetDateTime(10)));
        return result;
    }

    /// <summary>Входящие ТЗ подрядчика.</summary>
    public async Task<List<InboxItem>> GetInboxAsync(string companyId, CancellationToken ct)
    {
        var result = new List<InboxItem>();
        await using var cmd = _source.CreateCommand("""
            SELECT a.assignment_id, a.tz_id, a.root_tz_id, a.status, a.note,
                   a.created_at, a.viewed_at, a.decided_at,
                   d.version, d.readiness, t.name,
                   coalesce(p.name, '—'),
                   coalesce(d.payload->>'object', '—'),
                   coalesce(nullif(d.payload->>'customer', ''), 'НТЦ'),
                   (SELECT count(*) FROM tz.comment cm WHERE cm.root_tz_id = a.root_tz_id)
            FROM tz.assignment a
            JOIN tz.document d ON d.tz_id = a.tz_id
            JOIN tz.template t ON t.template_id = d.template_id
            LEFT JOIN catalog.product p ON p.product_id = d.product_id
            WHERE a.company_id = @c
            ORDER BY a.created_at DESC
            """);
        cmd.Parameters.AddWithValue("c", companyId);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
            result.Add(new InboxItem(
                rd.GetGuid(0), rd.GetGuid(1), rd.GetGuid(2), rd.GetString(3),
                rd.IsDBNull(4) ? null : rd.GetString(4),
                rd.GetDateTime(5),
                rd.IsDBNull(6) ? null : rd.GetDateTime(6),
                rd.IsDBNull(7) ? null : rd.GetDateTime(7),
                rd.GetInt32(8), rd.GetInt32(9), rd.GetString(10), rd.GetString(11),
                rd.GetString(12), rd.GetString(13), (int)rd.GetInt64(14)));
        return result;
    }

    public async Task<AssignmentRow?> GetAssignmentAsync(Guid assignmentId, CancellationToken ct)
    {
        await using var cmd = _source.CreateCommand(
            "SELECT assignment_id, tz_id, root_tz_id, company_id, status " +
            "FROM tz.assignment WHERE assignment_id = @id");
        cmd.Parameters.AddWithValue("id", assignmentId);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        if (!await rd.ReadAsync(ct)) return null;
        return new AssignmentRow(
            rd.GetGuid(0), rd.GetGuid(1), rd.GetGuid(2), rd.GetString(3), rd.GetString(4));
    }

    /// <summary>Отметка «подрядчик открыл ТЗ». Решённые направления не трогает.</summary>
    public async Task<bool> MarkViewedAsync(Guid assignmentId, CancellationToken ct)
    {
        await using var cmd = _source.CreateCommand(
            "UPDATE tz.assignment SET status = 'viewed', viewed_at = now() " +
            "WHERE assignment_id = @id AND status = 'sent'");
        cmd.Parameters.AddWithValue("id", assignmentId);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    /// <summary>
    /// Решение подрядчика: статус направления и запись в тред меняются одной
    /// транзакцией — вердикт без причины в ленте выглядел бы как обрыв.
    /// </summary>
    public async Task DecideAsync(
        AssignmentRow assignment, string decision, string text, CancellationToken ct)
    {
        await using var connection = await _source.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);

        await using (var update = new NpgsqlCommand(
            "UPDATE tz.assignment SET status = @s, decided_at = now(), " +
            "viewed_at = coalesce(viewed_at, now()) WHERE assignment_id = @id",
            connection, tx))
        {
            update.Parameters.AddWithValue("s", decision);
            update.Parameters.AddWithValue("id", assignment.AssignmentId);
            await update.ExecuteNonQueryAsync(ct);
        }

        await using (var insert = new NpgsqlCommand("""
            INSERT INTO tz.comment
                (root_tz_id, tz_id, assignment_id, author_kind, author_id,
                 section_key, kind, decision, text)
            VALUES (@root, @tz, @a, 'contractor', @author, NULL, 'decision', @d, @t)
            """, connection, tx))
        {
            insert.Parameters.AddWithValue("root", assignment.RootTzId);
            insert.Parameters.AddWithValue("tz", assignment.TzId);
            insert.Parameters.AddWithValue("a", assignment.AssignmentId);
            insert.Parameters.AddWithValue("author", assignment.CompanyId);
            insert.Parameters.AddWithValue("d", decision);
            insert.Parameters.AddWithValue("t", text);
            await insert.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
    }

    /// <summary>Тред по корню цепочки версий: все замечания и решения по ТЗ.</summary>
    public async Task<List<CommentRow>> GetCommentsAsync(Guid rootTzId, CancellationToken ct)
    {
        var result = new List<CommentRow>();
        await using var cmd = _source.CreateCommand("""
            SELECT c.comment_id, c.tz_id, c.assignment_id, c.author_kind, c.author_id,
                   coalesce(co.name, CASE WHEN c.author_kind = 'customer' THEN 'НТЦ'
                                          ELSE c.author_id END),
                   c.section_key, c.kind, c.decision, c.text, c.created_at
            FROM tz.comment c
            LEFT JOIN catalog.company co ON co.company_id = c.author_id
            WHERE c.root_tz_id = @root
            ORDER BY c.created_at ASC
            """);
        cmd.Parameters.AddWithValue("root", rootTzId);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
            result.Add(new CommentRow(
                rd.GetGuid(0), rd.GetGuid(1),
                rd.IsDBNull(2) ? null : rd.GetGuid(2),
                rd.GetString(3), rd.GetString(4), rd.GetString(5),
                rd.IsDBNull(6) ? null : rd.GetString(6),
                rd.GetString(7),
                rd.IsDBNull(8) ? null : rd.GetString(8),
                rd.GetString(9), rd.GetDateTime(10)));
        return result;
    }

    public async Task<Guid> AddCommentAsync(
        Guid rootTzId, Guid tzId, Guid? assignmentId, Actor actor,
        string? sectionKey, string text, CancellationToken ct)
    {
        await using var cmd = _source.CreateCommand("""
            INSERT INTO tz.comment
                (root_tz_id, tz_id, assignment_id, author_kind, author_id,
                 section_key, kind, text)
            VALUES (@root, @tz, @a, @kind, @author, @section, 'comment', @t)
            RETURNING comment_id
            """);
        cmd.Parameters.AddWithValue("root", rootTzId);
        cmd.Parameters.AddWithValue("tz", tzId);
        cmd.Parameters.AddWithValue("a", (object?)assignmentId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("kind", actor.IsContractor ? "contractor" : "customer");
        cmd.Parameters.AddWithValue("author", actor.Id);
        cmd.Parameters.AddWithValue("section", (object?)sectionKey ?? DBNull.Value);
        cmd.Parameters.AddWithValue("t", text);
        return (Guid)(await cmd.ExecuteScalarAsync(ct))!;
    }

    /// <summary>
    /// События согласования для витрины аналитики. Пишем «мимо» Prostor.Chat,
    /// в ту же таблицу analytics.event: сервисы общаются по HTTP, но лог
    /// событий — общая витрина, и заводить ради него отдельный вызов между
    /// сервисами для прототипа избыточно.
    /// </summary>
    public async Task LogEventAsync(string kind, JsonObject payload, CancellationToken ct)
    {
        try
        {
            await using var cmd = _source.CreateCommand(
                "INSERT INTO analytics.event (kind, payload) VALUES (@k, @p::jsonb)");
            cmd.Parameters.AddWithValue("k", kind);
            cmd.Parameters.Add(new NpgsqlParameter("p", NpgsqlDbType.Jsonb)
            {
                Value = payload.ToJsonString()
            });
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (Exception)
        {
            // Аналитика не должна ронять основное действие пользователя
        }
    }
}

public sealed record TzDocument(
    Guid TzId, Guid? SessionId, string TemplateId, string? ProductId, string Payload,
    int Readiness, string Risks, int Version, string? StorageKey, DateTime CreatedAt,
    Guid? ParentTzId, string Status);

public sealed record TzListItem(
    Guid TzId, DateTime CreatedAt, int Readiness, string TemplateName,
    string ProductName, string ObjectName, int RisksCount, string Status,
    string? ReviewStatus, int CommentsCount);

public sealed record TzVersionItem(
    Guid TzId, int Version, int Readiness, DateTime CreatedAt,
    string ProductName, string ObjectName, string Status);

public sealed record SavedDocument(Guid TzId, int Version);

public sealed record AssignmentRow(
    Guid AssignmentId, Guid TzId, Guid RootTzId, string CompanyId, string Status);

public sealed record AssignmentItem(
    Guid AssignmentId, Guid TzId, int Version, string CompanyId, string CompanyName,
    string CompanyCode, string Status, string? Note, DateTime CreatedAt,
    DateTime? ViewedAt, DateTime? DecidedAt);

public sealed record InboxItem(
    Guid AssignmentId, Guid TzId, Guid RootTzId, string Status, string? Note,
    DateTime CreatedAt, DateTime? ViewedAt, DateTime? DecidedAt, int Version,
    int Readiness, string TemplateName, string ProductName, string ObjectName,
    string CustomerName, int CommentsCount);

public sealed record CommentRow(
    Guid CommentId, Guid TzId, Guid? AssignmentId, string AuthorKind, string AuthorId,
    string AuthorName, string? SectionKey, string Kind, string? Decision, string Text,
    DateTime CreatedAt);
