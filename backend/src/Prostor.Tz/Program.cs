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

// pdf в ответе — не украшение: без шрифта с кириллицей выгрузка в PDF
// невозможна, и это должно быть видно снаружи, а не только по 503 в момент
// скачивания (см. PdfFonts).
app.MapGet("/health", () => Results.Ok(new
{
    status = "ok",
    pdf = PdfFonts.Available ? "ready" : "no-font"
}));

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
    var asDraft = body.AsDraft == true;
    var status = asDraft ? "draft" : "final";

    // Повторная валидация на сервере: фронт подсвечивает пробелы для удобства,
    // но источник правды о готовности — здесь. Черновик по определению может
    // быть неполным, поэтому гейт по рискам к нему не применяется — так же,
    // как и при явном force.
    var draft = Drafting.Build(template, state, typicalDays);
    if (!draft.CanGenerate && body.Force != true && !asDraft)
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
            payload, draft.Readiness, risks, stored ? key : "", body.ParentTzId, status, ct);
    }
    catch (Exception ex)
    {
        // Чаще всего сюда попадают, когда не накачены миграции
        // 06_document_versions.sql (колонка parent_tz_id) или
        // 07_document_status.sql (колонка status). Даём понятное
        // сообщение вместо голого 500.
        return Results.Json(new
        {
            error = "db_error",
            message = ex.Message,
            hint = "Возможно, не накачены миграции 06_document_versions.sql / " +
                   "07_document_status.sql. Выполните: docker compose exec -T db psql " +
                   "-U prostor -d prostor -f /docker-entrypoint-initdb.d/06_document_versions.sql " +
                   "&& docker compose exec -T db psql -U prostor -d prostor " +
                   "-f /docker-entrypoint-initdb.d/07_document_status.sql"
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
        pdfUrl = $"/api/v1/tz/documents/{saved.TzId}/file?format=pdf",
        stored,
        parentTzId = body.ParentTzId,
        version = saved.Version,
        status
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
        status = document.Status,
        risks = JsonNode.Parse(document.Risks),
        payload = JsonNode.Parse(document.Payload),
        downloadUrl = $"/api/v1/tz/documents/{tzId}/file",
        pdfUrl = $"/api/v1/tz/documents/{tzId}/file?format=pdf"
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
            status = v.Status,
            downloadUrl = $"/api/v1/tz/documents/{v.TzId}/file",
            pdfUrl = $"/api/v1/tz/documents/{v.TzId}/file?format=pdf"
        })
    }, jsonOptions);
});

// Отдаём файл через сервис, а не presigned-ссылкой: адрес MinIO во внутренней
// сети браузеру недоступен, а гонять пользователя через две системы имён —
// лишняя сложность для прототипа.
app.MapGet("/api/v1/tz/documents/{tzId:guid}/file", async (
    Guid tzId, string? format, TzDb db, DocumentStorage storage, CancellationToken ct) =>
{
    var document = await db.GetDocumentAsync(tzId, ct);
    if (document is null) return Results.NotFound();

    var pdf = string.Equals(format, "pdf", StringComparison.OrdinalIgnoreCase);
    var name = $"ТЗ_{tzId.ToString()[..8]}." + (pdf ? "pdf" : "docx");
    var contentType = pdf
        ? "application/pdf"
        : "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

    // В хранилище лежит только .docx: PDF — представление того же документа,
    // а не отдельная сущность, поэтому собирается на лету из payload. Так не
    // нужно ни версионировать два файла, ни решать, что делать со старыми
    // документами, созданными до появления PDF.
    if (!pdf && !string.IsNullOrEmpty(document.StorageKey))
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

    // Файл потерян, хранилище было недоступно или запрошен PDF — собираем
    // документ заново из сохранённого payload.
    var template = await db.GetTemplateAsync(document.TemplateId, ct);
    if (template is null) return Results.NotFound();

    var state = JsonNode.Parse(document.Payload)!.AsObject();
    var typicalDays = await db.GetTypicalDaysAsync(document.ProductId, ct);
    var draft = Drafting.Build(template, state, typicalDays);

    if (!pdf) return Results.File(DocxWriter.Build(draft, state), contentType, name);

    if (!PdfFonts.Available)
        return Results.Json(new { error = "pdf_unavailable", hint = PdfFonts.MissingFontHint },
            jsonOptions, statusCode: StatusCodes.Status503ServiceUnavailable);

    return Results.File(PdfWriter.Build(draft, state), contentType, name);
});

// ------------------------------------------------------------ согласование
// Роль приходит заголовком X-Prostor-Actor и НЕ проверяется — см. Actor.cs.
// Сверка компании ниже нужна для согласованности интерфейса, а не для
// разграничения доступа: авторизации в прототипе нет.

app.MapPost("/api/v1/tz/documents/{tzId:guid}/assignments", async (
    Guid tzId, SendRequest body, TzDb db, CancellationToken ct) =>
{
    var document = await db.GetDocumentAsync(tzId, ct);
    if (document is null) return Results.NotFound();

    var companyIds = (body.CompanyIds ?? Array.Empty<string>())
        .Where(id => !string.IsNullOrWhiteSpace(id))
        .Distinct()
        .ToArray();

    if (ReviewRules.ValidateSend(document.Status, companyIds) is { } error)
        return Results.Json(new { error = error.Code, message = error.Message },
            jsonOptions, statusCode: StatusCodes.Status422UnprocessableEntity);

    var rootTzId = document.ParentTzId ?? document.TzId;
    var created = await db.CreateAssignmentsAsync(tzId, rootTzId, companyIds, body.Note, ct);

    foreach (var companyId in companyIds)
        await db.LogEventAsync("tz_sent", new JsonObject
        {
            ["tzId"] = tzId.ToString(),
            ["companyId"] = companyId
        }, ct);

    return Results.Json(new
    {
        created,
        items = await db.GetAssignmentsAsync(rootTzId, ct)
    }, jsonOptions, statusCode: StatusCodes.Status201Created);
});

// Направления по всей цепочке версий: заказчик видит и то, что уходило на
// предыдущей версии ТЗ, а не только на текущей.
app.MapGet("/api/v1/tz/documents/{tzId:guid}/assignments", async (
    Guid tzId, TzDb db, CancellationToken ct) =>
{
    var rootTzId = await db.GetRootTzIdAsync(tzId, ct);
    if (rootTzId is null) return Results.NotFound();
    return Results.Json(new
    {
        rootTzId,
        items = await db.GetAssignmentsAsync(rootTzId.Value, ct)
    }, jsonOptions);
});

// Входящие ТЗ подрядчика. Компания берётся из актора, а не из query —
// иначе адрес экрана зависел бы от того, кем ты представился.
app.MapGet("/api/v1/tz/inbox", async (HttpContext http, TzDb db, CancellationToken ct) =>
{
    var actor = Actor.From(http);
    if (!actor.IsContractor)
        return Results.Json(new { error = "not_contractor", items = Array.Empty<object>() },
            jsonOptions, statusCode: StatusCodes.Status400BadRequest);

    return Results.Json(new { items = await db.GetInboxAsync(actor.Id, ct) }, jsonOptions);
});

// Отметка «открыл ТЗ» — отдельным вызовом, а не побочным эффектом GET.
app.MapPost("/api/v1/tz/assignments/{assignmentId:guid}/view", async (
    Guid assignmentId, HttpContext http, TzDb db, CancellationToken ct) =>
{
    var assignment = await db.GetAssignmentAsync(assignmentId, ct);
    if (assignment is null) return Results.NotFound();

    var actor = Actor.From(http);
    if (actor.IsContractor && actor.Id != assignment.CompanyId) return Results.Forbid();

    if (await db.MarkViewedAsync(assignmentId, ct))
        await db.LogEventAsync("tz_viewed", new JsonObject
        {
            ["tzId"] = assignment.TzId.ToString(),
            ["companyId"] = assignment.CompanyId
        }, ct);

    return Results.Ok(new { status = ReviewRules.IsDecided(assignment.Status) ? assignment.Status : "viewed" });
});

app.MapPost("/api/v1/tz/assignments/{assignmentId:guid}/decision", async (
    Guid assignmentId, DecisionRequest body, HttpContext http, TzDb db, CancellationToken ct) =>
{
    var assignment = await db.GetAssignmentAsync(assignmentId, ct);
    if (assignment is null) return Results.NotFound();

    var actor = Actor.From(http);
    if (actor.IsContractor && actor.Id != assignment.CompanyId) return Results.Forbid();

    var text = body.Text?.Trim() ?? "";
    if (ReviewRules.ValidateDecision(assignment.Status, body.Decision, text) is { } error)
        return Results.Json(new { error = error.Code, message = error.Message },
            jsonOptions, statusCode: StatusCodes.Status422UnprocessableEntity);

    var decision = body.Decision!;
    // Согласование без комментария — нормальный случай, но в ленте вердикт
    // должен быть виден строкой, а не пустотой.
    if (text.Length == 0) text = "ТЗ согласовано без замечаний.";

    await db.DecideAsync(assignment, decision, text, ct);
    await db.LogEventAsync($"tz_{decision}", new JsonObject
    {
        ["tzId"] = assignment.TzId.ToString(),
        ["companyId"] = assignment.CompanyId
    }, ct);

    return Results.Ok(new { status = decision });
});

// ------------------------------------------------------------ замечания
// Тред живёт по корню цепочки версий: правки по замечаниям создают новую
// версию документа, а обсуждение продолжается то же самое.
app.MapGet("/api/v1/tz/documents/{tzId:guid}/comments", async (
    Guid tzId, TzDb db, CancellationToken ct) =>
{
    var rootTzId = await db.GetRootTzIdAsync(tzId, ct);
    if (rootTzId is null) return Results.NotFound();
    return Results.Json(new
    {
        rootTzId,
        items = await db.GetCommentsAsync(rootTzId.Value, ct)
    }, jsonOptions);
});

app.MapPost("/api/v1/tz/documents/{tzId:guid}/comments", async (
    Guid tzId, CommentRequest body, HttpContext http, TzDb db, CancellationToken ct) =>
{
    var rootTzId = await db.GetRootTzIdAsync(tzId, ct);
    if (rootTzId is null) return Results.NotFound();

    var text = body.Text?.Trim() ?? "";
    if (text.Length == 0)
        return Results.Json(new { error = "empty_comment", message = "Замечание не может быть пустым." },
            jsonOptions, statusCode: StatusCodes.Status422UnprocessableEntity);

    var actor = Actor.From(http);
    var sectionKey = string.IsNullOrWhiteSpace(body.SectionKey) ? null : body.SectionKey.Trim();
    await db.AddCommentAsync(rootTzId.Value, tzId, body.AssignmentId, actor, sectionKey, text, ct);

    return Results.Json(new { items = await db.GetCommentsAsync(rootTzId.Value, ct) },
        jsonOptions, statusCode: StatusCodes.Status201Created);
});

app.Run();

public sealed record DraftRequest(Guid? SessionId, string? TemplateId, JsonObject? State);

public sealed record DocumentRequest(
    Guid? SessionId, string? TemplateId, JsonObject? State, bool? Force, Guid? ParentTzId,
    bool? AsDraft);

public sealed record SendRequest(string[]? CompanyIds, string? Note);

public sealed record DecisionRequest(string? Decision, string? Text);

public sealed record CommentRequest(string? SectionKey, string? Text, Guid? AssignmentId);
