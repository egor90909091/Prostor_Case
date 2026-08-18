using System.Text;
using System.Text.Json.Nodes;

namespace Prostor.Chat;

public delegate Task Emit(string eventName, object payload, CancellationToken ct);

/// <summary>
/// Один ход диалога.
///
/// Свободный текст  -> 1 нестримящийся вызов-роутер (см. TryRouteAsync) и дальше
///                      либо консультация без обращения к БД, либо ветка поиска:
///                      1 вызов эмбеддинга + 1 SQL + 1 стримящийся вызов LLM.
/// Действие (клик)  -> 0 вызовов моделей, только редьюсер и SQL.
///
/// Роутер — единственный tool call во всей системе, без аргументов и без побочных
/// эффектов: модель решает только КОГДА передать ход детерминированному поиску,
/// а не ЧТО искать и не ходит в базу сама — идентификатор она по-прежнему не видит
/// и не может выдумать. Это узкая, точечная вещь, а не agentic tool-calling (см.
/// docs/architecture.md §5). При ENABLE_ROUTER=false или отказе роутера ход
/// деградирует на прежнее поведение: любой свободный текст — это поиск.
/// </summary>
public sealed class TurnPipeline
{
    private readonly Db _db;
    private readonly IEmbedder _embedder;
    private readonly ILlm _llm;
    private readonly TzClient _tz;
    private readonly AppConfig _config;
    private readonly ILogger<TurnPipeline> _log;

    public TurnPipeline(Db db, IEmbedder embedder, ILlm llm, TzClient tz,
                        AppConfig config, ILogger<TurnPipeline> log)
    {
        _db = db;
        _embedder = embedder;
        _llm = llm;
        _tz = tz;
        _config = config;
        _log = log;
    }

    public async Task<ChatState> RunAsync(
        Guid sessionId, ChatState state, TurnRequest request, Emit emit, CancellationToken ct)
    {
        var blocks = new List<Block>();

        if (request.Action is { } action)
        {
            await HandleActionAsync(sessionId, state, action, blocks, emit, ct);
        }
        else
        {
            await HandleTextAsync(sessionId, state, request.Text ?? "", blocks, emit, ct);
        }

        foreach (var block in blocks)
            await emit("block", new { block }, ct);

        await emit("state", Snapshot(state), ct);
        return state;
    }

    public static object Snapshot(ChatState state) => new
    {
        step = state.StepName,
        missing = state.Missing(),
        productId = state.ProductId,
        productName = state.ProductName,
        templateId = state.TemplateId,
        period = state.Period,
        stages = state.Stages.Count,
        executors = state.Executors.Count,
        tzId = state.TzId
    };

    // =============================================================== текст
    private async Task HandleTextAsync(
        Guid sessionId, ChatState state, string text, List<Block> blocks, Emit emit, CancellationToken ct)
    {
        text = text.Trim();
        if (text.Length == 0)
        {
            blocks.Add(Block.TextBlock("Опишите, какие работы нужны — например, «нужно оценить запасы по объекту»."));
            return;
        }

        state.LastQuery = text;
        state.StepName = state.ProductId is null ? Step.ProductSearch : state.StepName;

        // Свободный текст на шаге заполнения полей трактуем как значение поля,
        // а не как новый поисковый запрос: пользователь отвечает на вопрос бота.
        // Это узкий частный случай, роутер его не видит — короткое замыкание
        // отрабатывает раньше любого вызова LLM.
        if (state.ProductId is not null && TryFillFreeField(state, text, out var filled))
        {
            blocks.Add(Block.TextBlock($"Записал: {filled}."));
            await AppendDraftAsync(sessionId, state, blocks, ct);
            return;
        }

        if (_config.EnableRouter && await TryRouteAsync(sessionId, state, text, blocks, emit, ct))
            return;

        // Роутер выключен флагом или не смог принять решение (недоступен, таймаут,
        // непонятный ответ провайдера) — деградируем на прежнее поведение: любой
        // свободный текст трактуем как поисковый запрос.
        await RunSemanticSearchAsync(sessionId, state, text, blocks, emit, ct);
    }

    private static bool TryFillFreeField(ChatState state, string text, out string label)
    {
        label = "";
        if (string.IsNullOrWhiteSpace(state.Object) && state.StepName is Step.Review or Step.StagesPicked)
        {
            state.Object = text;
            label = $"объект работ — {text}";
            return true;
        }
        return false;
    }

    // =============================================================== роутер
    private const string RouterSystemPrompt =
        "Ты — ассистент подбора нефтесервисных услуг в платформе ПРОСТОР. Разговаривай " +
        "свободно и по-деловому: отвечай на вопросы о процессе, помогай сформулировать " +
        "потребность, уточняй детали. Когда пользователь описал вид работ и готов увидеть " +
        "подходящие услуги — вызови search_services. Когда он ищет подрядчика или " +
        "компанию («кто может это сделать», «найди исполнителя») — вызови search_executors. " +
        "Обе функции без аргументов: поисковый запрос система возьмёт из его сообщения сама. " +
        "Не вызывай ничего, если ещё уточняешь потребность или отвечаешь на общий вопрос. " +
        "Не придумывай названия услуг, цены, сроки, компании — этого ты не знаешь, факты " +
        "придут из базы после вызова функции. Отвечай по-русски, 1-3 предложения, без списков.";

    /// <summary>
    /// Один нестримящийся вызов-роутер: модель решает, консультировать пользователя
    /// текстом или передать ход детерминированному поиску услуг (см. класс-докстринг).
    ///
    /// Возвращает true, если ход этим закрыт (консультация или поиск уже выполнены).
    /// Возвращает false при отказе/ошибке роутера — вызывающий код должен деградировать
    /// на прежнее поведение сам.
    /// </summary>
    private async Task<bool> TryRouteAsync(
        Guid sessionId, ChatState state, string text, List<Block> blocks, Emit emit, CancellationToken ct)
    {
        LlmDecision decision;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(_config.LlmTimeoutSeconds));
            decision = await _llm.DecideAsync(RouterSystemPrompt, BuildRouterUserPrompt(state, text), cts.Token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _log.LogWarning(ex, "роутер недоступен, ход обрабатывается как поиск услуг");
            return false;
        }

        if (decision.ToolName == RouterTool.SearchExecutors)
        {
            await RunExecutorSearchAsync(sessionId, state, text, blocks, emit, ct);
            return true;
        }

        if (decision.ToolCalled)
        {
            await RunSemanticSearchAsync(sessionId, state, text, blocks, emit, ct);
            return true;
        }

        if (string.IsNullOrWhiteSpace(decision.ReplyText))
        {
            // Модель ничего не вызвала и ничего не сказала — это отказ роутера,
            // а не осмысленное «пользователю нечего ответить».
            _log.LogWarning("роутер вернул пустой ответ без вызова инструмента, обрабатываем как поиск услуг");
            return false;
        }

        await EmitTextAsync(decision.ReplyText, emit, ct);
        return true;
    }

    private static string BuildRouterUserPrompt(ChatState state, string text)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Сообщение пользователя: {text}");
        sb.AppendLine();
        sb.AppendLine("Состояние диалога:");
        sb.AppendLine(state.ProductId is null
            ? "- услуга ещё не выбрана"
            : $"- уже выбрана услуга «{state.ProductName}»");
        if (state.Period.IsSet)
            sb.AppendLine($"- сроки согласованы: {state.Period.From} — {state.Period.To}");
        if (state.Executors.Count > 0)
            sb.AppendLine($"- исполнителей выбрано: {state.Executors.Count}");
        return sb.ToString();
    }

    /// <summary>
    /// Ответ роутера приходит целиком за один нестримящийся вызов. Нарезаем на части
    /// ради того же ощущения «печатает», что и у StreamAnswerAsync/StubLlm — это
    /// чисто косметика, не влияет на число вызовов моделей.
    /// </summary>
    private async Task EmitTextAsync(string text, Emit emit, CancellationToken ct)
    {
        foreach (var chunk in SplitForStreaming(text, 42))
        {
            ct.ThrowIfCancellationRequested();
            await emit("delta", new { text = chunk }, ct);
        }
    }

    private static IEnumerable<string> SplitForStreaming(string text, int size)
    {
        for (var i = 0; i < text.Length; i += size)
            yield return text.Substring(i, Math.Min(size, text.Length - i));
    }

    // =============================================================== поиск услуг
    /// <summary>Насколько выдаче можно верить. Решение принимает сервис, не модель.</summary>
    private enum Confidence
    {
        /// <summary>Есть доказательства и отрыв — показываем как находку.</summary>
        Confident,

        /// <summary>Что-то похожее есть, но уверенности нет — показываем с оговоркой.</summary>
        Tentative,

        /// <summary>Шум. Карточки не показываем вообще.</summary>
        None
    }

    /// <summary>
    /// Оценка выдачи по трём независимым сигналам сразу.
    ///
    /// Одного порога по score недостаточно: score — сумма разнородных величин,
    /// и «0.34» само по себе ничего не значит. Показательный случай: на запрос
    /// «кто пробурит скважину» пять юридических услуг с дословно одинаковым
    /// описанием набирали одинаковый балл выше прежней отсечки 0.15 и подавались
    /// пользователю как уверенная находка с рангами и обоснованиями.
    ///
    /// Поэтому проверяются: абсолютный уровень, наличие лексических доказательств
    /// (matched_terms) и отрыв лидера от второго места — плоская выдача означает,
    /// что ранжирование ничего не различило.
    /// </summary>
    private Confidence Assess(List<ProductHit> hits)
    {
        if (hits.Count == 0) return Confidence.None;

        var top = hits[0];
        if (top.Score < _config.SearchMinScore) return Confidence.None;

        // Доказательств нет — верить можно только вектору, и планка выше.
        var hasEvidence = top.MatchedTerms.Length > 0;
        if (!hasEvidence && top.Similarity < _config.SearchSemanticFloor)
            return Confidence.Tentative;

        if (top.Score < _config.SearchConfidentScore) return Confidence.Tentative;

        // Отрыв от второго места. Единственный результат сравнивать не с чем —
        // тогда достаточно абсолютного уровня и доказательств.
        if (hits.Count > 1 && top.Score - hits[1].Score < _config.SearchMinMargin)
            return Confidence.Tentative;

        return hasEvidence ? Confidence.Confident : Confidence.Tentative;
    }

    private async Task RunSemanticSearchAsync(
        Guid sessionId, ChatState state, string text, List<Block> blocks, Emit emit, CancellationToken ct)
    {
        var (embedding, degraded) = await EmbedAsync(text, ct);

        // --- ровно один SQL: гибридный поиск с ранжированием
        var hits = await _db.FindProductsAsync(embedding, text, _config.TopProducts, ct);
        var confidence = Assess(hits);

        // recognized — это «агент понял запрос», а не «SQL что-то вернул»:
        // иначе витрина нераспознанных запросов всегда пустая.
        await _db.LogSearchAsync(sessionId, text, hits.FirstOrDefault()?.ProductId,
            hits.FirstOrDefault()?.Score, hits.Count,
            recognized: confidence == Confidence.Confident, ct);

        if (confidence == Confidence.None)
        {
            await EmitTextAsync(NothingFoundText(text, degraded), emit, ct);
            blocks.Add(ClarifyBlock());
            return;
        }

        if (confidence == Confidence.Tentative)
        {
            // Честная формулировка вместо «нашёл 5 подходящих услуг»: точного
            // соответствия нет, показываем варианты как догадку и просим уточнить.
            await EmitTextAsync(
                "Точного соответствия вашему запросу в каталоге я не нашёл. " +
                (degraded ? "К тому же семантический поиск сейчас недоступен, работал только текстовый. " : "") +
                "Возможно, подойдёт что-то из этого — но проверьте, а лучше уточните вид работ.",
                emit, ct);
            blocks.Add(ProductListBlock(hits, tentative: true));
            blocks.Add(ClarifyBlock());
            return;
        }

        // --- ровно один вызов LLM: данные уже найдены, модель только формулирует
        await StreamAnswerAsync(BuildSearchPrompt(text, hits, degraded), emit, ct);
        blocks.Add(ProductListBlock(hits, tentative: false));
    }

    // =============================================================== поиск исполнителей
    /// <summary>
    /// «Кто вообще может это сделать» — до выбора услуги и без периода.
    /// Прежде такой вопрос не имел пути вообще: ops.find_executors требует
    /// product_id и даты, поэтому пользователю приходилось сначала угадать
    /// услугу. Занятость здесь не показывается — без периода она не определена.
    /// </summary>
    private async Task RunExecutorSearchAsync(
        Guid sessionId, ChatState state, string text, List<Block> blocks, Emit emit, CancellationToken ct)
    {
        var (embedding, _) = await EmbedAsync(text, ct);
        var companies = await _db.FindCompaniesAsync(embedding, text, _config.TopCompanies, ct);

        await _db.LogEventAsync(sessionId, "executor_search",
            Json.Write(new { query = text, found = companies.Count }), ct);

        if (companies.Count == 0)
        {
            await EmitTextAsync(
                "Подходящих исполнителей по этому описанию не нашлось. Опишите вид работ " +
                "конкретнее — например, «строительство скважин» или «подсчёт запасов».",
                emit, ct);
            blocks.Add(ClarifyBlock());
            return;
        }

        var confident = companies[0].MatchedTerms.Length > 0;
        await EmitTextAsync(
            (confident
                ? $"Нашёл {companies.Count} компаний, чей профиль и история работ подходят под запрос. "
                : $"Точного совпадения нет, но по профилю ближе всего эти {companies.Count} компаний. ") +
            "Это подбор по способностям: занятость в конкретные сроки здесь не учитывается — " +
            "выберите услугу и укажите период, тогда проверю загрузку и предложу ранжированный список.",
            emit, ct);

        blocks.Add(CompanyListBlock(companies, tentative: !confident));
        blocks.Add(ClarifyBlock("Уточните вид работ, чтобы подобрать услугу и проверить занятость"));
    }

    /// <summary>Ровно один вызов эмбеддинг-модели на ход. Отказ — деградация, а не ошибка.</summary>
    private async Task<(float[]? Embedding, bool Degraded)> EmbedAsync(string text, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(_config.EmbeddingTimeoutSeconds));
            return (await _embedder.EmbedAsync(text, cts.Token), false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Деградация, а не отказ: остаётся полнотекстовый канал поиска.
            // SQL сам отключит векторный канал, получив NULL.
            _log.LogWarning(ex, "эмбеддинг недоступен, работаем без семантического канала");
            return (null, true);
        }
    }

    private static string NothingFoundText(string query, bool degraded) =>
        "По этому запросу в каталоге ничего похожего не нашлось" +
        (degraded ? " (семантический поиск сейчас недоступен, работал только текстовый)" : "") +
        ". Попробуйте назвать вид работ — например, «подсчёт запасов», " +
        "«строительство скважин», «концепт обустройства» — или объект работ.";

    // =============================================================== действия
    private async Task HandleActionAsync(
        Guid sessionId, ChatState state, TurnAction action, List<Block> blocks, Emit emit, CancellationToken ct)
    {
        switch (action.Type)
        {
            case "select_product":
                await SelectProductAsync(sessionId, state, action, blocks, ct);
                break;

            case "set_period":
                await SetPeriodAsync(sessionId, state, action, blocks, ct);
                break;

            case "select_executors":
                await SelectExecutorsAsync(sessionId, state, action, blocks, ct);
                break;

            case "select_stages":
                await SelectStagesAsync(sessionId, state, action, blocks, ct);
                break;

            case "select_operations":
                state.OperationIds = action.Ids ?? new List<string>();
                blocks.Add(Block.TextBlock($"Отмечено операций: {state.OperationIds.Count}."));
                await AppendDraftAsync(sessionId, state, blocks, ct);
                break;

            case "set_field":
                ApplyField(state, action.Key, action.Value);
                blocks.Add(Block.TextBlock($"Поле «{action.Key}» обновлено."));
                await AppendDraftAsync(sessionId, state, blocks, ct);
                break;

            case "set_flag":
                state.Flags[action.Key ?? ""] = action.Flag ?? true;
                blocks.Add(Block.TextBlock(
                    (action.Flag ?? true ? "Учёл условие: " : "Условие снято: ") + FlagTitle(action.Key)));
                await AppendDraftAsync(sessionId, state, blocks, ct);
                break;

            case "tz_created":
                state.TzId = action.Id;
                state.StepName = Step.TzReady;
                await _db.LogEventAsync(sessionId, "tz_created",
                    Json.Write(new { tzId = action.Id, productId = state.ProductId }), ct);
                blocks.Add(new Block
                {
                    Type = "tz_ready",
                    Text = "Техническое задание сформировано.",
                    Meta = new JsonObject { ["tzId"] = action.Id }
                });
                break;

            case "reset":
                state.ResetFrom("product");
                state.ProductId = null;
                state.ProductName = null;
                state.StepName = Step.Idle;
                blocks.Add(Block.TextBlock("Начинаем заново. Опишите, какие работы нужны."));
                break;

            default:
                blocks.Add(Block.TextBlock($"Неизвестное действие «{action.Type}»."));
                break;
        }
    }

    private async Task SelectProductAsync(
        Guid sessionId, ChatState state, TurnAction action, List<Block> blocks, CancellationToken ct)
    {
        var productId = action.Id ?? "";
        // Карточку берём точечным запросом, а не из результатов поиска:
        // имя и шаблон не должны зависеть от того, что прислал клиент.
        var card = await _db.GetProductAsync(productId, ct);
        if (card is null)
        {
            blocks.Add(Block.TextBlock("Услуга не найдена в каталоге."));
            return;
        }

        var stages = await _db.GetStagesAsync(productId, _config.TopStages, ct);
        var related = await _db.GetRelatedAsync(productId, 4, ct);
        var similar = await _db.GetSimilarCalcsAsync(productId, 4, ct);
        var operations = await _db.GetOperationsAsync(productId, ct);

        state.ResetFrom("product");
        state.ProductId = card.ProductId;
        state.ProductName = card.Name;
        state.TemplateId = card.TemplateId;
        state.TypicalDays = card.TypicalDays;
        state.StepName = Step.ProductPicked;

        await _db.LogEventAsync(sessionId, "product_selected",
            Json.Write(new { productId, name = state.ProductName }), ct);

        blocks.Add(Block.TextBlock(
            $"Выбрана услуга «{state.ProductName}». " +
            (similar.Count > 0
                ? $"По ней в системе {similar.Count}+ выполненных работ — ниже похожие. "
                : "") +
            "Укажите желаемые сроки — подберу исполнителей, свободных в этот период."));

        blocks.Add(PeriodRequestBlock(state, similar));
        if (similar.Count > 0) blocks.Add(SimilarCalcsBlock(similar));
        if (related.Count > 0) blocks.Add(RelatedBlock(related));
        blocks.Add(RecommendationsBlock(state, stages, operations));
    }

    private async Task SetPeriodAsync(
        Guid sessionId, ChatState state, TurnAction action, List<Block> blocks, CancellationToken ct)
    {
        if (!DateOnly.TryParse(action.From, out var from) || !DateOnly.TryParse(action.To, out var to))
        {
            blocks.Add(Block.TextBlock("Не понял даты. Укажите период в формате ГГГГ-ММ-ДД."));
            return;
        }
        if (to < from) (from, to) = (to, from);

        state.ResetFrom("period");
        state.Period = new Period { From = from.ToString("yyyy-MM-dd"), To = to.ToString("yyyy-MM-dd") };
        state.StepName = Step.PeriodSet;

        if (state.ProductId is null)
        {
            blocks.Add(Block.TextBlock("Сначала выберите услугу."));
            return;
        }

        var executors = await _db.FindExecutorsAsync(
            state.ProductId, from, to, allowSubcontract: true, _config.TopExecutors, ct);

        var days = state.Period.Days;
        var warning = state.TypicalDays is > 0 && days < state.TypicalDays * 0.8
            ? $" Обратите внимание: заявленный срок {days} дн. меньше типового ({state.TypicalDays} дн.) — это попадёт в риски ТЗ."
            : "";

        blocks.Add(Block.TextBlock(
            executors.Count > 0
                ? $"Нашёл {executors.Count} исполнителей на период {state.Period.From} — {state.Period.To}. " +
                  "Список отсортирован по опыту, доступности и рейтингу." + warning
                : "На этот период свободных исполнителей с подтверждённым опытом нет. " +
                  "Сдвиньте даты или разрешите субподряд." + warning));

        if (executors.Count > 0) blocks.Add(ExecutorListBlock(executors));
    }

    private async Task SelectExecutorsAsync(
        Guid sessionId, ChatState state, TurnAction action, List<Block> blocks, CancellationToken ct)
    {
        var ids = action.Ids ?? new List<string>();
        var subcontract = action.Subcontract ?? new List<string>();
        if (ids.Count == 0)
        {
            blocks.Add(Block.TextBlock("Выберите хотя бы одного исполнителя."));
            return;
        }

        var names = await _db.GetCompanyNamesAsync(ids, ct);
        state.Executors = ids.Select(id => new ExecutorRef
        {
            Id = id,
            Name = names.TryGetValue(id, out var n) ? n : id,
            Subcontract = subcontract.Contains(id)
        }).ToList();
        if (subcontract.Count > 0) state.Flags["subcontract"] = true;
        state.StepName = Step.ExecutorsPicked;

        await _db.LogEventAsync(sessionId, "executors_selected", Json.Write(new { ids }), ct);

        var stages = await _db.GetStagesAsync(state.ProductId!, _config.TopStages, ct);
        var operations = await _db.GetOperationsAsync(state.ProductId!, ct);

        blocks.Add(Block.TextBlock(
            $"Исполнителей выбрано: {ids.Count}. Теперь отметьте этапы работ — " +
            "они попадут в раздел «Содержание работ» технического задания. " +
            "Состав этапов собран из реальных расчётов по этой услуге."));
        blocks.Add(StageListBlock(stages, state));
        if (operations.Count > 0) blocks.Add(OperationListBlock(operations));
        blocks.Add(ConditionsBlock(state));
    }

    private async Task SelectStagesAsync(
        Guid sessionId, ChatState state, TurnAction action, List<Block> blocks, CancellationToken ct)
    {
        var keys = action.Ids ?? new List<string>();
        var stages = await _db.GetStagesAsync(state.ProductId!, _config.TopStages, ct);

        state.Stages = stages
            .Where(s => keys.Contains(s.Key))
            .Select(s => new StageRef
            {
                Key = s.Key,
                Name = s.Name,
                Days = s.MedianDays,
                Documentation = s.Documentation
            })
            .ToList();
        state.StepName = Step.Review;

        await _db.LogEventAsync(sessionId, "stages_selected",
            Json.Write(new { count = state.Stages.Count }), ct);

        blocks.Add(Block.TextBlock($"Этапов выбрано: {state.Stages.Count}."));
        await AppendDraftAsync(sessionId, state, blocks, ct);
    }

    private static void ApplyField(ChatState state, string? key, string? value)
    {
        switch (key)
        {
            case "customer": state.Customer = value; break;
            case "object": state.Object = value; break;
            case "purpose": state.Purpose = value; break;
            case "perimeter": state.Perimeter = value; break;
            case "source_data": state.SourceData = value; break;
            case "documentation": state.Documentation = value; break;
            case "acceptance": state.Acceptance = value; break;
            case "other": state.Other = value; break;
        }
    }

    private static string FlagTitle(string? key) => key switch
    {
        "model3d" => "построение 3D геологической модели",
        "subcontract" => "привлечение субподряда",
        "urgent" => "срочное выполнение",
        _ => key ?? ""
    };

    /// <summary>
    /// Синхронный вызов генератора ТЗ: он считает готовность и риски.
    /// Очередь здесь не нужна — операция занимает десятки миллисекунд.
    /// </summary>
    private async Task AppendDraftAsync(Guid sessionId, ChatState state, List<Block> blocks, CancellationToken ct)
    {
        if (state.ProductId is null) return;

        var draft = await _tz.DraftAsync(sessionId, state, ct);
        if (draft is null)
        {
            blocks.Add(Block.TextBlock(
                "Сервис ТЗ временно недоступен — проверку готовности покажу позже, " +
                "остальной сценарий работает."));
            return;
        }

        blocks.Add(new Block
        {
            Type = "tz_gaps",
            Text = $"Готовность ТЗ: {draft["readiness"]?.GetValue<int>() ?? 0}%",
            Items = draft["risks"]?.AsArray().DeepClone().AsArray(),
            Meta = new JsonObject
            {
                ["readiness"] = draft["readiness"]?.GetValue<int>() ?? 0,
                ["canGenerate"] = draft["canGenerate"]?.GetValue<bool>() ?? false,
                ["templateId"] = state.TemplateId,
                ["recommendation"] = draft["recommendation"]?.GetValue<string>()
            }
        });

        blocks.Add(new Block
        {
            Type = "actions",
            Items = new JsonArray
            {
                new JsonObject { ["action"] = "open_constructor", ["title"] = "Сформировать ТЗ в конструкторе" }
            }
        });
    }

    // =============================================================== LLM
    private async Task StreamAnswerAsync(string prompt, Emit emit, CancellationToken ct)
    {
        const string system =
            "Ты — консультант по подбору нефтесервисных услуг в платформе ПРОСТОР. " +
            "Тебе уже передан отранжированный список найденных услуг с фактами из системы. " +
            "Не придумывай услуги, идентификаторы и цифры сверх переданных. " +
            "Ответь 2–3 предложениями: что нашлось, чем варианты отличаются и какой следующий шаг. " +
            "Пиши по-русски, деловым тоном, без списков — список карточек пользователь видит отдельно.";

        var buffer = new StringBuilder();
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(_config.LlmTimeoutSeconds));
            await foreach (var delta in _llm.StreamAsync(system, prompt, cts.Token))
            {
                buffer.Append(delta);
                await emit("delta", new { text = delta }, ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _log.LogWarning(ex, "LLM недоступна, отдаём результат поиска без сопроводительного текста");
            if (buffer.Length == 0)
                await emit("delta", new { text = "Вот что нашлось по вашему запросу:" }, ct);
        }
    }

    private static string BuildSearchPrompt(string query, List<ProductHit> hits, bool degraded)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Запрос пользователя: {query}");
        if (degraded) sb.AppendLine("(семантический поиск недоступен, работал только полнотекстовый)");
        sb.AppendLine("Найденные услуги (уже отранжированы, менять порядок нельзя):");
        foreach (var hit in hits)
        {
            sb.Append($"{hit.Rank}. {hit.Name} [{hit.Category}] score={hit.Score:0.00}");
            if (hit.CalcsCnt > 0) sb.Append($", выполнено работ: {hit.CalcsCnt}");
            if (hit.TypicalDays is > 0) sb.Append($", типовой срок: {hit.TypicalDays} дн.");
            if (hit.CompaniesCnt > 0) sb.Append($", исполнителей: {hit.CompaniesCnt}");
            sb.AppendLine();
            if (!string.IsNullOrWhiteSpace(hit.Snippet))
                sb.AppendLine($"   состав работ: {hit.Snippet}");
        }

        var top = hits[0];
        sb.AppendLine();
        sb.Append("ОТВЕТ_ЗАГЛУШКИ: ");
        sb.Append($"Нашёл {hits.Count} подходящих услуг. Наиболее близкая — «{top.Name}»");
        if (top.CalcsCnt > 0) sb.Append($", по ней в системе {top.CalcsCnt} выполненных работ");
        if (top.TypicalDays is > 0) sb.Append($", типовой срок {top.TypicalDays} дн");
        sb.Append(". Выберите подходящий вариант — дальше уточню сроки и подберу исполнителей.");
        return sb.ToString();
    }

    // =============================================================== блоки
    private static Block ProductListBlock(List<ProductHit> hits, bool tentative) => new()
    {
        Type = "product_list",
        SelectMode = "single",
        Meta = new JsonObject { ["tentative"] = tentative },
        Items = new JsonArray(hits.Select(h => (JsonNode)new JsonObject
        {
            ["rank"] = h.Rank,
            ["id"] = h.ProductId,
            ["title"] = h.Name,
            ["category"] = h.Category,
            ["snippet"] = h.Snippet,
            ["score"] = (double)h.Score,
            ["similarity"] = (double)h.Similarity,
            ["lexical"] = (double)h.Lexical,
            ["fuzzy"] = (double)h.Fuzzy,
            ["matchedTerms"] = new JsonArray(h.MatchedTerms.Select(t => (JsonNode)t!).ToArray()),
            ["weak"] = h.MatchedTerms.Length == 0,
            ["templateId"] = h.TemplateId,
            ["calcsCnt"] = h.CalcsCnt,
            ["companiesCnt"] = h.CompaniesCnt,
            ["operationsCnt"] = h.OperationsCnt,
            ["typicalDays"] = h.TypicalDays,
            ["reasons"] = new JsonArray(Reasons(h).Select(r => (JsonNode)r!).ToArray())
        }).ToArray())
    };

    /// <summary>
    /// Обоснования строятся из доказательств, а не из порогов.
    ///
    /// Прежняя версия писала «совпадение по составу работ» при любом lexical > 0
    /// и «смысловая близость запросу» при similarity > 0.3 — то есть на уровне
    /// шума. В результате все пять нерелевантных карточек получали одинаковый
    /// набор причин, который ничего не различал и вводил пользователя в
    /// заблуждение. Теперь: нет совпавших слов — так и написано.
    /// </summary>
    private static IEnumerable<string> Reasons(ProductHit hit)
    {
        if (hit.MatchedTerms.Length > 0)
            yield return "совпали слова: " + string.Join(", ", hit.MatchedTerms.Take(4));
        else if (hit.Fuzzy >= 0.5m)
            yield return "похоже по написанию — возможно, в запросе опечатка";
        else
            yield return "точных совпадений по словам нет, только смысловая близость";

        if (hit.CalcsCnt > 0) yield return $"выполнено работ: {hit.CalcsCnt}";
        if (hit.CompaniesCnt > 0) yield return $"исполнителей с опытом: {hit.CompaniesCnt}";
        if (hit.TypicalDays is > 0) yield return $"типовой срок {hit.TypicalDays} дн.";
    }

    private static Block CompanyListBlock(List<CompanyHit> companies, bool tentative) => new()
    {
        Type = "company_list",
        Text = "Исполнители, подходящие по профилю и истории работ",
        Meta = new JsonObject { ["tentative"] = tentative },
        Items = new JsonArray(companies.Select(c => (JsonNode)new JsonObject
        {
            ["rank"] = c.Rank,
            ["id"] = c.CompanyId,
            ["name"] = c.Name,
            ["rating"] = c.Rating,
            ["score"] = (double)c.Score,
            ["snippet"] = c.Snippet,
            ["calcsCnt"] = c.CalcsCnt,
            ["productsCnt"] = c.ProductsCnt,
            ["lastEndDate"] = c.LastEndDate?.ToString("yyyy-MM-dd"),
            ["topProducts"] = new JsonArray(c.TopProducts.Select(p => (JsonNode)p!).ToArray()),
            ["matchedTerms"] = new JsonArray(c.MatchedTerms.Select(t => (JsonNode)t!).ToArray()),
            ["reasons"] = new JsonArray(CompanyReasons(c).Select(r => (JsonNode)r!).ToArray())
        }).ToArray())
    };

    private static IEnumerable<string> CompanyReasons(CompanyHit c)
    {
        if (c.MatchedTerms.Length > 0)
            yield return "в профиле есть: " + string.Join(", ", c.MatchedTerms.Take(4));
        else
            yield return "прямых совпадений по словам нет, только смысловая близость";

        if (c.CalcsCnt > 0) yield return $"выполнено работ по истории: {c.CalcsCnt}";
        if (c.ProductsCnt > 0) yield return $"услуг в портфеле: {c.ProductsCnt}";
        if (c.LastEndDate is { } d) yield return $"последняя работа завершена {d:dd.MM.yyyy}";
        yield return $"рейтинг {c.Rating} из 5";
    }

    /// <summary>Уточняющий блок вместо молчаливой выдачи шума.</summary>
    private static Block ClarifyBlock(string? text = null) => new()
    {
        Type = "clarify",
        Text = text ?? "Уточните запрос — так подбор будет точнее",
        Items = new JsonArray(
            new JsonObject { ["text"] = "Назовите вид работ: «подсчёт запасов», «строительство скважин», «концепт обустройства»" },
            new JsonObject { ["text"] = "Укажите объект или месторождение" },
            new JsonObject { ["text"] = "Скажите, что должно получиться на выходе: отчёт, модель, проектный документ" })
    };

    private static Block PeriodRequestBlock(ChatState state, List<SimilarCalc> similar)
    {
        var suggested = state.TypicalDays is > 0
            ? state.TypicalDays
            : similar.FirstOrDefault()?.DurationDays;
        var from = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(7);
        var to = from.AddDays(suggested is > 0 ? suggested.Value : 90);

        return new Block
        {
            Type = "period_request",
            Text = "Укажите сроки выполнения работ",
            Meta = new JsonObject
            {
                ["suggestedFrom"] = from.ToString("yyyy-MM-dd"),
                ["suggestedTo"] = to.ToString("yyyy-MM-dd"),
                ["typicalDays"] = suggested
            }
        };
    }

    private static Block ExecutorListBlock(List<ExecutorHit> executors) => new()
    {
        Type = "executor_list",
        SelectMode = "multi",
        Items = new JsonArray(executors.Select(e => (JsonNode)new JsonObject
        {
            ["rank"] = e.Rank,
            ["id"] = e.CompanyId,
            ["name"] = e.Name,
            ["score"] = (double)e.Score,
            ["rating"] = e.Rating,
            ["experience"] = e.Experience,
            ["availability"] = e.Availability,
            ["loadPct"] = e.LoadPct,
            ["busyDays"] = e.BusyDays,
            ["periodDays"] = e.PeriodDays,
            ["subcontract"] = e.Subcontract,
            ["reasons"] = new JsonArray(e.Reasons.Select(r => (JsonNode)r!).ToArray())
        }).ToArray())
    };

    private static Block StageListBlock(List<StageInfo> stages, ChatState state)
    {
        var chosen = state.Stages.Select(s => s.Key).ToHashSet();
        return new Block
        {
            Type = "stage_list",
            SelectMode = "multi",
            Text = "Этапы работ",
            Items = new JsonArray(stages.Select(s => (JsonNode)new JsonObject
            {
                ["id"] = s.Key,
                ["title"] = s.Name,
                ["usedCnt"] = s.UsedCount,
                ["medianDays"] = s.MedianDays,
                ["documentation"] = s.Documentation,
                ["preselected"] = chosen.Count > 0 ? chosen.Contains(s.Key) : s.UsedCount > 1
            }).ToArray())
        };
    }

    private static Block OperationListBlock(List<OperationInfo> operations) => new()
    {
        Type = "operation_list",
        SelectMode = "multi",
        Text = "Операции в составе услуги",
        Items = new JsonArray(operations.Take(20).Select(o => (JsonNode)new JsonObject
        {
            ["id"] = o.OperationId,
            ["title"] = o.Name,
            ["required"] = o.Required,
            ["preselected"] = o.Required
        }).ToArray())
    };

    private static Block RelatedBlock(List<RelatedProduct> related) => new()
    {
        Type = "related_products",
        Text = "Часто заказывают вместе",
        Items = new JsonArray(related.Select(r => (JsonNode)new JsonObject
        {
            ["id"] = r.ProductId,
            ["title"] = r.Name,
            ["category"] = r.Category,
            ["cnt"] = r.Count,
            ["confidence"] = (double)r.Confidence
        }).ToArray())
    };

    private static Block SimilarCalcsBlock(List<SimilarCalc> calcs) => new()
    {
        Type = "similar_calcs",
        Text = "Аналогичные выполненные работы",
        Items = new JsonArray(calcs.Select(c => (JsonNode)new JsonObject
        {
            ["id"] = c.CalcId,
            ["title"] = c.Name,
            ["company"] = c.CompanyName,
            ["contract"] = c.ContractNumber,
            ["from"] = c.StartDate?.ToString("yyyy-MM-dd"),
            ["to"] = c.EndDate?.ToString("yyyy-MM-dd"),
            ["days"] = c.DurationDays,
            ["stages"] = c.StagesCount
        }).ToArray())
    };

    private static Block ConditionsBlock(ChatState state) => new()
    {
        Type = "conditions",
        Text = "Условия выполнения работ",
        Items = new JsonArray(
            new JsonObject
            {
                ["key"] = "model3d",
                ["title"] = "Требуется построение 3D геологической модели",
                ["value"] = state.Flags.TryGetValue("model3d", out var m) && m
            },
            new JsonObject
            {
                ["key"] = "subcontract",
                ["title"] = "Допускается привлечение субподряда",
                ["value"] = state.Flags.TryGetValue("subcontract", out var s) && s
            },
            new JsonObject
            {
                ["key"] = "urgent",
                ["title"] = "Срочное выполнение",
                ["value"] = state.Flags.TryGetValue("urgent", out var u) && u
            })
    };

    private static Block RecommendationsBlock(
        ChatState state, List<StageInfo> stages, List<OperationInfo> operations)
    {
        var items = new JsonArray();
        if (stages.Count > 0)
            items.Add(new JsonObject
            {
                ["text"] = $"В истории по этой услуге устойчиво повторяются {stages.Count} этапов — " +
                           "конструктор подставит их автоматически"
            });
        if (operations.Count > 0)
            items.Add(new JsonObject
            {
                ["text"] = $"В составе услуги {operations.Count} операций; обязательные будут отмечены заранее"
            });
        items.Add(new JsonObject { ["text"] = "Подготовьте название объекта работ — без него ТЗ не пройдёт проверку" });
        items.Add(new JsonObject { ["text"] = "Уточните, нужна ли 3D геологическая модель: от этого зависит состав этапов" });

        return new Block { Type = "recommendations", Text = "Что понадобится для заявки", Items = items };
    }
}
