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
        await using var cmd = _source.CreateCommand("""
            SELECT d.tz_id, d.created_at, d.readiness, t.name,
                   coalesce(p.name, '—'),
                   coalesce(d.payload->>'object', '—'),
                   jsonb_array_length(d.risks),
                   d.status
            FROM tz.document d
            JOIN tz.template t ON t.template_id = d.template_id
            LEFT JOIN catalog.product p ON p.product_id = d.product_id
            ORDER BY d.created_at DESC
            LIMIT @l
            """);
        cmd.Parameters.AddWithValue("l", limit);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
            result.Add(new TzListItem(rd.GetGuid(0), rd.GetDateTime(1), rd.GetInt32(2),
                rd.GetString(3), rd.GetString(4), rd.GetString(5), rd.GetInt32(6), rd.GetString(7)));
        return result;
    }
}

public sealed record TzDocument(
    Guid TzId, Guid? SessionId, string TemplateId, string? ProductId, string Payload,
    int Readiness, string Risks, int Version, string? StorageKey, DateTime CreatedAt,
    Guid? ParentTzId, string Status);

public sealed record TzListItem(
    Guid TzId, DateTime CreatedAt, int Readiness, string TemplateName,
    string ProductName, string ObjectName, int RisksCount, string Status);

public sealed record TzVersionItem(
    Guid TzId, int Version, int Readiness, DateTime CreatedAt,
    string ProductName, string ObjectName, string Status);

public sealed record SavedDocument(Guid TzId, int Version);
