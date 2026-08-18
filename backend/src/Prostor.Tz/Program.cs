using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Npgsql;
using Prostor.Tz;

var builder = WebApplication.CreateBuilder(args);
var config = TzConfig.FromEnvironment(builder.Configuration);

builder.Services.AddSingleton(config);
builder.Services.AddSingleton(_ => new NpgsqlDataSourceBuilder(config.ConnectionString).Build());
builder.Services.AddSingleton<TzDb>();
builder.Services.AddSingleton<DocumentStorage>();
builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
    policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();
app.UseCors();

var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
{
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
};

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.MapGet("/api/v1/tz/templates", async (TzDb db, CancellationToken ct) =>
{
    var templates = await db.GetTemplatesAsync(ct);
    return Results.Ok(new
    {
        items = templates.Select(t => new
        {
            id = t.TemplateId,
            name = t.Name,
            typeCode = t.TypeCode,
            sections = t.Sections.Select(s => s?["title"]?.GetValue<string>()),
            fields = t.Fields
        })
    });
});

// ---------------------------------------------------------------- черновик
// Без побочных эффектов: считает готовность и риски, ничего не сохраняет.
// Вызывается и чатом (через /internal), и конструктором на каждое изменение.
var draftHandler = async (DraftRequest body, TzDb db, CancellationToken ct) =>
{
    var templateId = body.TemplateId ?? "tpl-generic";
    var template = await db.GetTemplateAsync(templateId, ct)
                   ?? await db.GetTemplateAsync("tpl-generic", ct);
    if (template is null) return Results.Problem("шаблоны ТЗ не загружены");

    var state = body.State ?? new JsonObject();
    var typicalDays = await db.GetTypicalDaysAsync(state["productId"]?.GetValue<string>(), ct);
    var draft = Drafting.Build(template, state, typicalDays);

    return Results.Json(new
    {
        templateId = draft.TemplateId,
        templateName = draft.TemplateName,
        readiness = draft.Readiness,
        canGenerate = draft.CanGenerate,
        recommendation = draft.Recommendation,
        typicalDays,
        fields = draft.Fields,
        risks = draft.Risks,
        sections = draft.Sections
    }, jsonOptions);
};

app.MapPost("/internal/tz/draft", draftHandler);
app.MapPost("/api/v1/tz/drafts", draftHandler);

// ---------------------------------------------------------------- документ
app.MapPost("/api/v1/tz/documents", async (
    DocumentRequest body, TzDb db, DocumentStorage storage, CancellationToken ct) =>
{
    var templateId = body.TemplateId ?? "tpl-generic";
    var template = await db.GetTemplateAsync(templateId, ct)
                   ?? await db.GetTemplateAsync("tpl-generic", ct);
    if (template is null) return Results.Problem("шаблоны ТЗ не загружены");

    var state = body.State ?? new JsonObject();
    var productId = state["productId"]?.GetValue<string>();
    var typicalDays = await db.GetTypicalDaysAsync(productId, ct);

    // Повторная валидация на сервере: фронт подсвечивает пробелы для удобства,
    // но источник правды о готовности — здесь.
    var draft = Drafting.Build(template, state, typicalDays);
    if (!draft.CanGenerate && body.Force != true)
    {
        return Results.Json(new
        {
            error = "validation_failed",
            readiness = draft.Readiness,
            risks = draft.Risks,
            recommendation = draft.Recommendation
        }, jsonOptions, statusCode: StatusCodes.Status422UnprocessableEntity);
    }

    var bytes = DocxWriter.Build(draft, state);

    var companyIds = (state["executors"] as JsonArray)?
        .Select(e => e?["id"]?.GetValue<string>() ?? "")
        .Where(id => id.Length > 0)
        .ToArray() ?? Array.Empty<string>();

    var risks = JsonNode.Parse(JsonSerializer.Serialize(draft.Risks, jsonOptions))!.AsArray();
    var payload = state.DeepClone().AsObject();
    payload["sections"] = draft.Sections.DeepClone();
    payload["readiness"] = draft.Readiness;

    var tzId = Guid.NewGuid();
    var key = $"tz/{tzId}.docx";

    var stored = true;
    try
    {
        await storage.PutAsync(key, bytes, ct);
    }
    catch (Exception)
    {
        // Хранилище недоступно — документ всё равно фиксируем в БД,
        // файл можно перегенерировать из payload
        stored = false;
    }

    SavedDocument saved;
    try
    {
        saved = await db.SaveDocumentAsync(
            body.SessionId, template.TemplateId, productId, companyIds,
            payload, draft.Readiness, risks, stored ? key : "", body.ParentTzId, ct);
    }
    catch (Exception ex)
    {
        // Чаще всего сюда попадают, когда не накачена миграция
        // 06_document_versions.sql (колонка parent_tz_id отсутствует).
        // Даём понятное сообщение вместо голого 500.
        return Results.Json(new
        {
            error = "db_error",
            message = ex.Message,
            hint = "Возможно, не накачена миграция 06_document_versions.sql. " +
                   "Выполните: docker compose exec -T db psql -U prostor -d prostor " +
                   "-f /docker-entrypoint-initdb.d/06_document_versions.sql"
        }, jsonOptions, statusCode: StatusCodes.Status500InternalServerError);
    }

    return Results.Json(new
    {
        tzId = saved.TzId,
        readiness = draft.Readiness,
        risks = draft.Risks,
        recommendation = draft.Recommendation,
        storageKey = stored ? key : null,
        downloadUrl = $"/api/v1/tz/documents/{saved.TzId}/file",
        stored,
        parentTzId = body.ParentTzId,
        version = saved.Version
    }, jsonOptions, statusCode: StatusCodes.Status201Created);
});

app.MapGet("/api/v1/tz/documents", async (int? limit, TzDb db, CancellationToken ct) =>
    Results.Ok(new { items = await db.ListDocumentsAsync(limit ?? 50, ct) }));

// ---------------------------------------------------------------- документы
// Отдаём файл через сервис, а не presigned-ссылкой: адрес MinIO во внутренней

app.MapGet("/api/v1/tz/documents/{tzId:guid}", async (Guid tzId, TzDb db, CancellationToken ct) =>
{
    var document = await db.GetDocumentAsync(tzId, ct);
    if (document is null) return Results.NotFound();

    return Results.Json(new
    {
        tzId = document.TzId,
        templateId = document.TemplateId,
        productId = document.ProductId,
        readiness = document.Readiness,
        version = document.Version,
        createdAt = document.CreatedAt,
        parentTzId = document.ParentTzId,
        risks = JsonNode.Parse(document.Risks),
        payload = JsonNode.Parse(document.Payload),
        downloadUrl = $"/api/v1/tz/documents/{tzId}/file"
    }, jsonOptions);
});

// Все версии одного ТЗ: корневой документ плюс строки, ссылающиеся на
// него через parent_tz_id. Используется в конструкторе и на странице
// «Мои заявки», чтобы показать историю правок.
app.MapGet("/api/v1/tz/documents/{tzId:guid}/versions", async (
    Guid tzId, TzDb db, CancellationToken ct) =>
{
    var versions = await db.GetDocumentVersionsAsync(tzId, ct);
    return Results.Json(new
    {
        rootTzId = tzId,
        items = versions.Select(v => new
        {
            tzId = v.TzId,
            version = v.Version,
            readiness = v.Readiness,
            createdAt = v.CreatedAt,
            productName = v.ProductName,
            objectName = v.ObjectName,
            downloadUrl = $"/api/v1/tz/documents/{v.TzId}/file"
        })
    }, jsonOptions);
});

// Отдаём файл через сервис, а не presigned-ссылкой: адрес MinIO во внутренней
// сети браузеру недоступен, а гонять пользователя через две системы имён —
// лишняя сложность для прототипа.
app.MapGet("/api/v1/tz/documents/{tzId:guid}/file", async (
    Guid tzId, TzDb db, DocumentStorage storage, CancellationToken ct) =>
{
    var document = await db.GetDocumentAsync(tzId, ct);
    if (document is null) return Results.NotFound();

    var name = $"ТЗ_{tzId.ToString()[..8]}.docx";
    const string contentType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

    if (!string.IsNullOrEmpty(document.StorageKey))
    {
        try
        {
            var stream = await storage.GetAsync(document.StorageKey, ct);
            return Results.Stream(stream, contentType, name);
        }
        catch (Exception)
        {
            // падаем в перегенерацию ниже
        }
    }

    // Файл потерян или хранилище было недоступно — собираем документ заново
    var template = await db.GetTemplateAsync(document.TemplateId, ct);
    if (template is null) return Results.NotFound();

    var state = JsonNode.Parse(document.Payload)!.AsObject();
    var typicalDays = await db.GetTypicalDaysAsync(document.ProductId, ct);
    var draft = Drafting.Build(template, state, typicalDays);
    return Results.File(DocxWriter.Build(draft, state), contentType, name);
});

app.Run();

public sealed record DraftRequest(Guid? SessionId, string? TemplateId, JsonObject? State);

public sealed record DocumentRequest(
    Guid? SessionId, string? TemplateId, JsonObject? State, bool? Force, Guid? ParentTzId);
