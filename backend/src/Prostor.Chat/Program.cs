using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;
using Prostor.Chat;

var builder = WebApplication.CreateBuilder(args);
var config = AppConfig.FromEnvironment(builder.Configuration);

builder.Services.AddSingleton(config);
builder.Services.AddSingleton(_ =>
{
    var dataSourceBuilder = new NpgsqlDataSourceBuilder(config.ConnectionString);
    return dataSourceBuilder.Build();
});
builder.Services.AddSingleton<Db>();

// Эмбеддинги и LLM переключаются на заглушки независимо друг от друга —
// это два разных провайдера (сейчас: bge-m3 через Ollama и DeepSeek).
if (config.UseStubEmbedder)
{
    builder.Services.AddSingleton<IEmbedder>(sp =>
        new CachingEmbedder(new StubEmbedder(), sp.GetRequiredService<Db>(),
            sp.GetRequiredService<ILogger<CachingEmbedder>>()));
}
else
{
    builder.Services.AddHttpClient<OpenAiEmbedder>();
    builder.Services.AddSingleton<IEmbedder>(sp =>
        new CachingEmbedder(sp.GetRequiredService<OpenAiEmbedder>(), sp.GetRequiredService<Db>(),
            sp.GetRequiredService<ILogger<CachingEmbedder>>()));
}

if (config.UseStubLlm)
{
    builder.Services.AddSingleton<ILlm, StubLlm>();
}
else
{
    builder.Services.AddHttpClient<OpenAiLlm>();
    builder.Services.AddSingleton<ILlm>(sp => sp.GetRequiredService<OpenAiLlm>());
}

builder.Services.AddHttpClient<TzClient>();
builder.Services.AddSingleton<TurnPipeline>();
builder.Services.AddHostedService<Indexer>();
builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
    policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();
app.UseCors();

// Индексатор заполняет только строки с embedding IS NULL, поэтому смена модели
// эмбеддингов сама по себе индекс не пересчитывает: старые векторы останутся
// несовместимыми с новыми запросами, и поиск будет тихо выдавать ерунду вместо
// того, чтобы упасть. Покрытие индекса показываем здесь, чтобы это состояние
// было видно сразу, а не диагностировалось по странной выдаче в чате.
app.MapGet("/health", async (Db db, CancellationToken ct) =>
{
    EmbeddingCoverage? coverage = null;
    string? dbError = null;
    try
    {
        coverage = await db.GetEmbeddingCoverageAsync(ct);
    }
    catch (Exception ex)
    {
        dbError = ex.Message;
    }

    return Results.Ok(new
    {
        status = dbError is null ? "ok" : "degraded",
        llm = config.UseStubLlm ? "stub" : config.LlmModel,
        embeddings = config.UseStubEmbedder ? "stub" : config.EmbeddingModel,
        router = config.EnableRouter ? "on" : "off",
        index = coverage is null ? null : new
        {
            chunks = coverage.Total,
            missing = coverage.Missing,
            ready = coverage.Complete,
            hint = coverage.Complete
                ? null
                : "часть каталога не проиндексирована — семантический поиск работает не полностью. " +
                  "После смены модели эмбеддингов индекс надо пересчитать: " +
                  "UPDATE search.product_chunk SET embedding = NULL; " +
                  "UPDATE search.company_chunk SET embedding = NULL; " +
                  "TRUNCATE search.embedding_cache; и перезапустить chat-сервис."
        },
        db = dbError
    });
});

// ---------------------------------------------------------------- сессии
app.MapPost("/api/v1/chat/sessions", async (Db db, CreateSessionRequest? body, CancellationToken ct) =>
{
    var (sessionId, state) = await db.CreateSessionAsync(body?.CustomerName, ct);
    return Results.Created($"/api/v1/chat/sessions/{sessionId}", new
    {
        sessionId,
        state = TurnPipeline.Snapshot(state)
    });
});

app.MapGet("/api/v1/chat/sessions/{sessionId:guid}", async (Guid sessionId, Db db, CancellationToken ct) =>
{
    var state = await db.GetStateAsync(sessionId, ct);
    if (state is null) return Results.NotFound();

    var history = await db.GetHistoryAsync(sessionId, ct);
    return Results.Ok(new
    {
        sessionId,
        state = TurnPipeline.Snapshot(state),
        fields = new
        {
            customer = state.Customer,
            @object = state.Object,
            purpose = state.Purpose,
            perimeter = state.Perimeter,
            sourceData = state.SourceData,
            documentation = state.Documentation,
            acceptance = state.Acceptance,
            other = state.Other
        },
        messages = history.Select(m => new
        {
            seq = m.Seq,
            role = m.Role,
            blocks = JsonNode.Parse(m.Blocks),
            createdAt = m.CreatedAt
        })
    });
});

// Полное состояние диалога — им инициализируется конструктор ТЗ (Кейс 1)
app.MapGet("/api/v1/chat/sessions/{sessionId:guid}/state", async (
    Guid sessionId, Db db, CancellationToken ct) =>
{
    var json = await db.GetStateJsonAsync(sessionId, ct);
    return json is null ? Results.NotFound() : Results.Content(json, "application/json");
});

// ------------------------------------------------- ход диалога: долгий HTTP со стримом
app.MapPost("/api/v1/chat/sessions/{sessionId:guid}/turns", async (
    Guid sessionId, HttpContext http, Db db, TurnPipeline pipeline, AppConfig cfg,
    ILoggerFactory loggerFactory) =>
{
    var log = loggerFactory.CreateLogger("turns");
    var ct = http.RequestAborted;

    TurnRequest? request;
    try
    {
        request = await JsonSerializer.DeserializeAsync<TurnRequest>(http.Request.Body, Json.Options, ct);
    }
    catch (JsonException)
    {
        http.Response.StatusCode = StatusCodes.Status400BadRequest;
        await http.Response.WriteAsJsonAsync(new { error = "bad_request" }, ct);
        return;
    }
    if (request is null)
    {
        http.Response.StatusCode = StatusCodes.Status400BadRequest;
        await http.Response.WriteAsJsonAsync(new { error = "bad_request" }, ct);
        return;
    }

    var state = await db.GetStateAsync(sessionId, ct);
    if (state is null)
    {
        http.Response.StatusCode = StatusCodes.Status404NotFound;
        await http.Response.WriteAsJsonAsync(new { error = "session_not_found" }, ct);
        return;
    }

    var running = await db.GetRunningTurnAsync(sessionId, ct);
    if (running is { } activeTurn)
    {
        http.Response.StatusCode = StatusCodes.Status409Conflict;
        await http.Response.WriteAsJsonAsync(new { error = "turn_in_progress", activeTurnId = activeTurn }, ct);
        return;
    }

    var idempotencyKey =
        http.Request.Headers["Idempotency-Key"].FirstOrDefault()
        ?? request.ClientMessageId
        ?? Guid.NewGuid().ToString();

    var (turnId, isNew) = await db.StartTurnAsync(sessionId, idempotencyKey, ct);

    var writer = new SseWriter(http.Response);
    await writer.StartAsync(ct);

    // Повтор с тем же ключом не выполняет ход второй раз — отдаём сохранённые события
    if (!isNew)
    {
        var stored = await db.GetTurnAsync(turnId, ct);
        if (stored is { } prior)
        {
            foreach (var node in JsonNode.Parse(prior.Events)?.AsArray() ?? new JsonArray())
                await writer.WriteRawAsync(node?["event"]?.GetValue<string>() ?? "delta",
                    node?["data"]?.ToJsonString() ?? "{}", ct);
            await writer.WriteRawAsync("done", Json.Write(new { turnId, replayed = true }), ct);
            return;
        }
    }

    using var turnCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
    turnCts.CancelAfter(TimeSpan.FromSeconds(cfg.TurnTimeoutSeconds));
    var turnCt = turnCts.Token;

    var seq = 0;
    async Task EmitAsync(string name, object payload, CancellationToken token)
    {
        var data = Json.Write(payload);
        var envelope = new JsonObject
        {
            ["seq"] = ++seq,
            ["event"] = name,
            ["data"] = JsonNode.Parse(data)
        };
        await db.AppendTurnEventAsync(turnId, envelope.ToJsonString(), CancellationToken.None);
        await writer.WriteRawAsync(name, data, token);
    }

    using var heartbeat = new Heartbeat(writer, cfg.HeartbeatSeconds, turnCt);

    try
    {
        await EmitAsync("meta", new { turnId, sessionId }, turnCt);

        // Пользовательское сообщение в ленте — до всякой обработки,
        // чтобы оно не потерялось при обрыве соединения
        var userBlocks = request.Action is { } act
            ? new JsonArray(new JsonObject
            {
                ["type"] = "action",
                ["text"] = act.Type,
                ["meta"] = JsonNode.Parse(Json.Write(act))
            })
            : new JsonArray(new JsonObject { ["type"] = "text", ["text"] = request.Text ?? "" });
        await db.AppendMessageAsync(sessionId, "user", userBlocks.ToJsonString(), turnCt);

        var collected = new List<Block>();
        Task Collect(string name, object payload, CancellationToken token)
        {
            if (name == "block" && payload is not null)
            {
                var node = JsonNode.Parse(Json.Write(payload))?["block"];
                if (node is not null) collected.Add(JsonSerializer.Deserialize<Block>(node.ToJsonString(), Json.Options)!);
            }
            return EmitAsync(name, payload!, token);
        }

        state = await pipeline.RunAsync(sessionId, state, request, Collect, turnCt);

        await db.SaveStateAsync(sessionId, state, CancellationToken.None);
        await db.AppendMessageAsync(sessionId, "assistant", Json.Write(collected), CancellationToken.None);
        await db.FinishTurnAsync(turnId, "done", CancellationToken.None);
        await EmitAsync("done", new { turnId }, turnCt);
    }
    catch (OperationCanceledException)
    {
        // Ход дописан в БД независимо от состояния соединения: клиент
        // переподключится на /stream?fromSeq=N и доберёт хвост
        await db.FinishTurnAsync(turnId, ct.IsCancellationRequested ? "cancelled" : "timeout",
            CancellationToken.None);
        log.LogInformation("ход {TurnId} прерван", turnId);
    }
    catch (Exception ex)
    {
        log.LogError(ex, "ошибка хода {TurnId}", turnId);
        await db.FinishTurnAsync(turnId, "failed", CancellationToken.None);
        try
        {
            await writer.WriteRawAsync("error", Json.Write(new { message = "internal_error" }), CancellationToken.None);
        }
        catch
        {
            // соединение уже закрыто
        }
    }
});

// ----------------------------------------- дострим после обрыва соединения
app.MapGet("/api/v1/chat/sessions/{sessionId:guid}/turns/{turnId:guid}/stream", async (
    Guid sessionId, Guid turnId, int? fromSeq, HttpContext http, Db db) =>
{
    var turn = await db.GetTurnAsync(turnId, http.RequestAborted);
    if (turn is null)
    {
        http.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }

    var writer = new SseWriter(http.Response);
    await writer.StartAsync(http.RequestAborted);

    var start = fromSeq ?? 0;
    foreach (var node in JsonNode.Parse(turn.Value.Events)?.AsArray() ?? new JsonArray())
    {
        var seq = node?["seq"]?.GetValue<int>() ?? 0;
        if (seq <= start) continue;
        await writer.WriteRawAsync(node?["event"]?.GetValue<string>() ?? "delta",
            node?["data"]?.ToJsonString() ?? "{}", http.RequestAborted);
    }

    if (turn.Value.Status != "running")
        await writer.WriteRawAsync("done", Json.Write(new { turnId, resumed = true }), http.RequestAborted);
});

// ---------------------------------------------------------------- каталог
app.MapPost("/api/v1/catalog/products/search", async (
    SearchRequest body, Db db, IEmbedder embedder, AppConfig cfg, CancellationToken ct) =>
{
    var query = body.Query ?? "";
    var embedding = await embedder.EmbedAsync(query, ct);
    var hits = await db.FindProductsAsync(embedding, query, body.TopK ?? cfg.TopProducts, ct);
    return Results.Ok(new { items = hits, embedder = embedder.Kind });
});

app.MapGet("/api/v1/catalog/products/{productId}", async (string productId, Db db, CancellationToken ct) =>
    await db.GetProductAsync(productId, ct) is { } card ? Results.Ok(card) : Results.NotFound());

app.MapGet("/api/v1/catalog/products/{productId}/stages", async (
    string productId, int? top, Db db, CancellationToken ct) =>
    Results.Ok(new { items = await db.GetStagesAsync(productId, top ?? 12, ct) }));

app.MapGet("/api/v1/catalog/products/{productId}/operations", async (
    string productId, Db db, CancellationToken ct) =>
    Results.Ok(new { items = await db.GetOperationsAsync(productId, ct) }));

app.MapGet("/api/v1/catalog/products/{productId}/related", async (
    string productId, Db db, CancellationToken ct) =>
    Results.Ok(new { items = await db.GetRelatedAsync(productId, 6, ct) }));

app.MapGet("/api/v1/catalog/products/{productId}/similar", async (
    string productId, Db db, CancellationToken ct) =>
    Results.Ok(new { items = await db.GetSimilarCalcsAsync(productId, 6, ct) }));

// Подбор исполнителей по способностям — без выбранной услуги и без периода.
// Отвечает на вопрос «кто вообще это умеет»; занятости здесь нет, потому что
// без периода она не определена (для неё — /api/v1/executors/search ниже).
app.MapPost("/api/v1/executors/discover", async (
    SearchRequest body, Db db, IEmbedder embedder, AppConfig cfg, CancellationToken ct) =>
{
    var query = body.Query ?? "";
    float[]? embedding;
    try
    {
        embedding = await embedder.EmbedAsync(query, ct);
    }
    catch
    {
        embedding = null;   // деградация до полнотекстового канала, как и в чате
    }

    var items = await db.FindCompaniesAsync(embedding, query, body.TopK ?? cfg.TopCompanies, ct);
    return Results.Ok(new { items, embedder = embedder.Kind });
});

app.MapPost("/api/v1/executors/search", async (
    ExecutorSearchRequest body, Db db, AppConfig cfg, CancellationToken ct) =>
{
    if (!DateOnly.TryParse(body.From, out var from) || !DateOnly.TryParse(body.To, out var to))
        return Results.BadRequest(new { error = "bad_period" });

    var items = await db.FindExecutorsAsync(
        body.ProductId, from, to, body.AllowSubcontract ?? true, body.Top ?? cfg.TopExecutors, ct);

    return Results.Ok(new
    {
        items,
        weights = new { experience = 0.35, availability = 0.25, load = 0.15, rating = 0.25 }
    });
});

app.MapGet("/api/v1/analytics/overview", async (Db db, CancellationToken ct) =>
    Results.Content(await db.GetAnalyticsJsonAsync(ct), "application/json"));

app.Run();

// ---------------------------------------------------------------- вспомогательное
public sealed record CreateSessionRequest(string? CustomerName);
public sealed record SearchRequest(string? Query, int? TopK);
public sealed record ExecutorSearchRequest(
    string ProductId, string? From, string? To, bool? AllowSubcontract, int? Top);

/// <summary>
/// Поток SSE поверх обычного POST. WebSocket не нужен: сервер ничего
/// не инициирует вне хода пользователя.
/// </summary>
public sealed class SseWriter
{
    private readonly HttpResponse _response;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public SseWriter(HttpResponse response) => _response = response;

    public async Task StartAsync(CancellationToken ct)
    {
        _response.StatusCode = StatusCodes.Status200OK;
        _response.ContentType = "text/event-stream; charset=utf-8";
        _response.Headers["Cache-Control"] = "no-cache, no-transform";
        _response.Headers["X-Accel-Buffering"] = "no";   // чтобы nginx не буферизовал поток
        _response.Headers["Connection"] = "keep-alive";
        await _response.Body.FlushAsync(ct);
    }

    public async Task WriteRawAsync(string eventName, string json, CancellationToken ct)
    {
        var frame = Encoding.UTF8.GetBytes($"event: {eventName}\ndata: {json}\n\n");
        await _lock.WaitAsync(ct);
        try
        {
            await _response.Body.WriteAsync(frame, ct);
            await _response.Body.FlushAsync(ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task WriteCommentAsync(CancellationToken ct)
    {
        var frame = Encoding.UTF8.GetBytes(": hb\n\n");
        await _lock.WaitAsync(ct);
        try
        {
            await _response.Body.WriteAsync(frame, ct);
            await _response.Body.FlushAsync(ct);
        }
        finally
        {
            _lock.Release();
        }
    }
}

/// <summary>Комментарий-heartbeat раз в N секунд, чтобы прокси не рвали долгое соединение.</summary>
public sealed class Heartbeat : IDisposable
{
    private readonly CancellationTokenSource _cts;
    private readonly Task _loop;

    public Heartbeat(SseWriter writer, int seconds, CancellationToken ct)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = _cts.Token;
        _loop = Task.Run(async () =>
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(seconds), token);
                    await writer.WriteCommentAsync(token);
                }
            }
            catch (OperationCanceledException)
            {
                // штатное завершение вместе с ходом
            }
            catch (ObjectDisposedException)
            {
                // соединение закрыто клиентом
            }
        }, token);
    }

    public void Dispose()
    {
        _cts.Cancel();
        try
        {
            _loop.Wait(TimeSpan.FromSeconds(1));
        }
        catch
        {
            // heartbeat не должен ломать завершение хода
        }
        _cts.Dispose();
    }
}
