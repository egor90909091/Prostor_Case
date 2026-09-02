using System.Text;
using System.Text.Json.Nodes;

namespace Prostor.Chat;

public delegate Task Emit(string eventName, object payload, CancellationToken ct);

/// <summary>
/// Один ход диалога.
///
/// Свободный текст  -> 1 структурированный вызов «мозга» (см. Brain.cs): он
///                      одновременно отвечает пользователю, вытаскивает из
///                      разговора данные для ТЗ, называет намерение и
///                      предлагает следующий шаг. Дальше — только код:
///                      ветка поиска добавляет 1 вызов эмбеддинга + 1 SQL +
///                      1 стримящийся вызов LLM на формулировку выдачи,
///                      ветка разговора не добавляет ничего.
/// Действие (клик)  ->  0 вызовов моделей, только редьюсер и SQL.
///
/// Ключевое разделение (docs/architecture.md §5): модель решает, ЧТО сказать и
/// КУДА направить ход, но не ЧТО искать в базе и не какие идентификаторы
/// использовать — их она не видит. Выбор услуги словами («давай первый») идёт
/// через номер в последнем показанном списке (ChatState.LastOptions), сроки —
/// через разбор дат кодом, флаги — по белому списку. Карточки (услуги, сроки,
/// исполнители, этапы) показываются как предложение поверх разговора и
/// пропускают гейты кода: нельзя спросить исполнителей без сроков, нельзя
/// показать одно и то же предложение два хода подряд.
///
/// При ENABLE_ROUTER=false, отсутствии ключа или отказе модели ход не падает:
/// Brain.Fallback принимает то же решение детерминированно — без услуги любой
/// текст считается поиском, с услугой ответ собирается из состояния заявки.
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
        productCategory = state.ProductCategory,
        templateId = state.TemplateId,
        period = state.Period,
        stages = state.Stages.Count,
        executors = state.Executors.Count,
        executorNames = state.Executors.Select(e => e.Name).ToList(),
        // Идентификаторы выбранного, а не только счётчики: по ним карточка в
        // ленте диалога отмечает выбранное — где бы в истории она ни стояла и
        // как бы выбор ни был сделан, кликом или словами. Иначе выбор виден
        // только в боковой панели, а карточка рядом с репликой выглядит
        // нетронутой (см. Blocks.tsx, useAppliedSelection).
        stageIds = state.Stages.Select(s => s.Key).ToList(),
        executorIds = state.Executors.Select(e => e.Id).ToList(),
        operationIds = state.OperationIds.ToList(),
        tzId = state.TzId,
        // Собранные из разговора поля ТЗ едут в снапшот, чтобы боковая панель
        // показывала их сразу же: человек видит, что именно услышал ассистент,
        // и может поправить словом, не дожидаясь конструктора.
        fields = Brain.FilledFields(state).ToDictionary(f => f.Key, f => f.Value),
        flags = state.Flags.Where(f => f.Value).Select(f => f.Key).ToList()
    };

    // =============================================================== текст
    /// <summary>
    /// Свободный текст. Один структурированный вызов «мозга» (см. Brain) даёт
    /// сразу ответ, намерение, вытащенные из разговора данные для ТЗ, уместное
    /// предложение следующего шага и подсказки. Дальше работает только код:
    /// применяет факты, выполняет поиск, показывает карточки.
    ///
    /// Порядок внутри хода важен для ощущения диалога: сперва человек видит
    /// ответ (реплика в его контексте), затем — что записалось, и только потом
    /// карточки. Карточка никогда не заменяет ответ и никогда не обязательна:
    /// её можно проигнорировать и продолжить разговор словами.
    /// </summary>
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

        var decision = await ThinkAsync(sessionId, state, text, ct);

        // Факты применяем ДО ответа: если человек назвал сроки словами, ответ
        // должен звучать уже с учётом записанного периода.
        var captured = await ApplyFactsAsync(sessionId, state, decision.Facts, blocks, ct);

        var spoke = !string.IsNullOrWhiteSpace(decision.Reply);
        if (spoke) await EmitTextAsync(decision.Reply, emit, ct);

        // Реплика и текст по результатам поиска приходят дельтами в один и тот
        // же блок — без разделителя они склеиваются в одно предложение.
        Task SeparateAsync() => spoke ? emit("delta", new { text = "\n\n" }, ct) : Task.CompletedTask;

        // Первым блоком, а не последним: «что записалось» относится к реплике
        // пользователя и должно стоять перед карточками, которые эта запись
        // могла вызвать (названные словами сроки сразу дают список исполнителей).
        if (captured.Count > 0)
            blocks.Insert(0, new Block
            {
                Type = "captured",
                Text = "Записал в заявку",
                Items = new JsonArray(captured.Select(c => (JsonNode)new JsonObject
                {
                    ["key"] = c.Key,
                    ["label"] = Brain.FieldTitle(c.Key),
                    ["value"] = c.Value
                }).ToArray())
            });

        var offer = decision.Offer;

        // Сроки и этапы, названные словами, уже привели к следующей карточке
        // внутри ApplyFactsAsync (список исполнителей, готовность ТЗ) —
        // предлагать что-то ещё в этом же ходу незачем.
        if (captured.Any(c => c.Key is "period" or "stages")) offer = Offer.None;

        switch (decision.Intent)
        {
            case Intent.SearchServices:
                await SeparateAsync();
                await RunSemanticSearchAsync(sessionId, state, decision.Query ?? text, blocks, emit, ct);
                offer = Offer.None; // карточки услуг уже показаны — второе предложение в том же ходу лишнее
                break;

            case Intent.SearchExecutors when state.ProductId is null || !state.Period.IsSet:
                await SeparateAsync();
                await RunExecutorSearchAsync(sessionId, state, decision.Query ?? text, blocks, emit, ct);
                offer = Offer.None;
                break;

            case Intent.SearchExecutors:
                // Услуга и сроки известны — «кто это сделает» это уже подбор
                // исполнителей по заявке с учётом занятости, а не общий поиск.
                offer = Offer.Executors;
                break;

            case Intent.PickOption when ResolveOption(state, decision.OptionIndex) is { } picked:
                await SelectProductAsync(sessionId, state, new TurnAction { Id = picked }, blocks, ct);
                offer = Offer.None;
                break;

            case Intent.Restart:
                ResetState(state);
                offer = Offer.None;
                break;
        }

        await OfferAsync(sessionId, state, offer, blocks, ct);

        if (decision.Suggestions.Count > 0)
            blocks.Add(new Block
            {
                Type = "suggestions",
                Items = new JsonArray(decision.Suggestions
                    .Select(s => (JsonNode)new JsonObject { ["text"] = s }).ToArray())
            });
    }

    /// <summary>
    /// Один вызов модели на ход. Контекст собирается из базы (карточка услуги,
    /// этапы, похожие работы, хвост диалога) — модель не ходит в базу сама.
    /// Любой сбой означает не отказ хода, а детерминированное решение
    /// <see cref="Brain.Fallback"/>: демо без ключей работает так же.
    /// </summary>
    private async Task<BrainDecision> ThinkAsync(
        Guid sessionId, ChatState state, string text, CancellationToken ct)
    {
        if (!_config.EnableRouter) return Brain.Fallback(state, text);

        string? transcript = null;
        try
        {
            transcript = await _db.GetDialogueTranscriptAsync(sessionId, ct, maxChars: 4000);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Транскрипт — обогащение, а не обязательные данные: без него мозг
            // работает по состоянию заявки и текущему сообщению.
            _log.LogWarning(ex, "транскрипт диалога недоступен, ход обрабатывается без истории");
        }

        ProductCard? card = null;
        var stages = new List<StageInfo>();
        var similar = new List<SimilarCalc>();
        if (state.ProductId is not null)
        {
            try
            {
                card = await _db.GetProductAsync(state.ProductId, ct);
                stages = await _db.GetStagesAsync(state.ProductId, _config.TopStages, ct);
                similar = await _db.GetSimilarCalcsAsync(state.ProductId, 3, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                _log.LogWarning(ex, "контекст услуги недоступен, ход обрабатывается без него");
            }
        }

        var prompt = Brain.BuildPrompt(
            state, text, card, stages, similar, transcript, DateOnly.FromDateTime(DateTime.UtcNow));

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(_config.LlmTimeoutSeconds));
            var raw = await _llm.CompleteJsonAsync(Brain.SystemPrompt, prompt, Brain.Schema, cts.Token);
            var decision = Brain.Parse(raw);
            if (decision is not null)
            {
                await _db.LogEventAsync(sessionId, "brain",
                    Json.Write(new { intent = decision.Intent, offer = decision.Offer }), ct);
                return decision;
            }
            _log.LogWarning("мозг диалога вернул неразобранный ответ, ход идёт по детерминированной ветке");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _log.LogWarning(ex, "мозг диалога недоступен, ход идёт по детерминированной ветке");
        }

        return Brain.Fallback(state, text);
    }

    /// <summary>
    /// Запись вытащенных из разговора сущностей. Код, а не модель, решает,
    /// что считать валидным: даты разбираются, флаги берутся по белому списку,
    /// уже заполненные поля не затираются молча — перезапись только явная
    /// (модель присылает поле повторно, лишь когда человек его поправил).
    /// Возвращает то, что реально записалось: это показывается пользователю.
    /// </summary>
    private async Task<List<KeyValuePair<string, string>>> ApplyFactsAsync(
        Guid sessionId, ChatState state, BrainFacts facts, List<Block> blocks, CancellationToken ct)
    {
        var captured = new List<KeyValuePair<string, string>>();

        void Set(string key, string? value, Func<string?> get, Action<string> set)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            var trimmed = value.Trim();
            if (string.Equals(get(), trimmed, StringComparison.OrdinalIgnoreCase)) return;
            set(trimmed);
            captured.Add(new KeyValuePair<string, string>(key, trimmed));
        }

        Set("object", facts.Object, () => state.Object, v => state.Object = v);
        Set("purpose", facts.Purpose, () => state.Purpose, v => state.Purpose = v);
        Set("customer", facts.Customer, () => state.Customer, v => state.Customer = v);
        Set("perimeter", facts.Perimeter, () => state.Perimeter, v => state.Perimeter = v);
        Set("sourceData", facts.SourceData, () => state.SourceData, v => state.SourceData = v);
        Set("documentation", facts.Documentation, () => state.Documentation, v => state.Documentation = v);
        Set("acceptance", facts.Acceptance, () => state.Acceptance, v => state.Acceptance = v);
        Set("other", facts.Other, () => state.Other, v => state.Other = v);

        foreach (var flag in facts.Flags)
        {
            if (state.Flags.TryGetValue(flag, out var current) && current) continue;
            state.Flags[flag] = true;
            captured.Add(new KeyValuePair<string, string>("flag", FlagTitle(flag)));
        }

        // Сроки словами («с 1 октября на три месяца») — тот же путь, что и
        // карточка периода: одна и та же проверка дат и один и тот же подбор
        // исполнителей, иначе диалог и кнопки разошлись бы в поведении.
        if (DateOnly.TryParse(facts.PeriodFrom, out var from) && DateOnly.TryParse(facts.PeriodTo, out var to))
        {
            if (to < from) (from, to) = (to, from);
            var same = state.Period.From == from.ToString("yyyy-MM-dd") &&
                       state.Period.To == to.ToString("yyyy-MM-dd");
            if (!same)
            {
                captured.Add(new KeyValuePair<string, string>(
                    "period", $"{from:yyyy-MM-dd} — {to:yyyy-MM-dd}"));
                await ApplyPeriodAsync(sessionId, state, from, to, blocks, ct);
            }
        }

        // Этапы, названные словами («давай этап 1 Уточнение и этап 1 Экспертиза»), —
        // тот же путь, что и галочки в карточке: модель называет только номера
        // из показанного списка, ключи подставляет код. Прежде такого пути не
        // было вовсе, и названия этапов оседали текстом в «особых условиях», а
        // в карточке не отмечались — выбор словами выглядел непринятым.
        var named = ResolveStages(state, facts.StageNumbers);
        if (named.Count > 0)
        {
            // Добавление, а не замена: модель присылает только то, что прозвучало
            // сейчас, а уже выбранное она видит в состоянии заявки. Снять этап
            // по-прежнему можно галочкой — там выбор задаётся целиком.
            var keys = state.Stages.Select(s => s.Key)
                .Concat(named.Select(s => s.Id))
                .Distinct()
                .ToList();
            if (keys.Count != state.Stages.Count)
            {
                captured.Add(new KeyValuePair<string, string>(
                    "stages", string.Join("; ", named.Select(s => s.Title))));
                await SelectStagesAsync(sessionId, state, new TurnAction { Ids = keys }, blocks, ct);
            }
        }

        if (captured.Count > 0)
        {
            await _db.LogEventAsync(sessionId, "facts_captured",
                Json.Write(new { keys = captured.Select(c => c.Key).ToArray() }), ct);
        }

        return captured;
    }

    /// <summary>
    /// Показ карточки следующего шага. Разрешение даёт код: предложение
    /// модели проходит гейты по заполненным слотам (нельзя спрашивать
    /// исполнителей без сроков) и защиту от повтора — одно и то же
    /// предложение два хода подряд не показывается, чтобы диалог не
    /// превращался в анкету.
    /// </summary>
    private async Task OfferAsync(
        Guid sessionId, ChatState state, string offer, List<Block> blocks, CancellationToken ct)
    {
        if (offer is Offer.None || state.ProductId is null) return;

        // Ровно эту карточку уже показывали, и она всё ещё висит в ленте выше —
        // второй такой же вопрос превращает диалог в анкету. Карточка снова
        // появится, когда слот изменится или сценарий уйдёт на другой шаг.
        if (offer == state.LastOffer) return;

        var shown = Offer.None;
        switch (offer)
        {
            case Offer.Period when !state.Period.IsSet:
                var similar = await _db.GetSimilarCalcsAsync(state.ProductId, 3, ct);
                blocks.Add(PeriodRequestBlock(state, similar));
                shown = Offer.Period;
                break;

            case Offer.Executors when state.Period.IsSet:
                var executors = await _db.FindExecutorsAsync(
                    state.ProductId, DateOnly.Parse(state.Period.From!), DateOnly.Parse(state.Period.To!),
                    allowSubcontract: true, _config.TopExecutors, ct);
                if (executors.Count > 0)
                {
                    blocks.Add(ExecutorListBlock(executors));
                    shown = Offer.Executors;
                }
                break;

            case Offer.Executors:
                // Исполнителей просят до сроков — без периода занятость не
                // определена, поэтому сначала период.
                blocks.Add(PeriodRequestBlock(state, await _db.GetSimilarCalcsAsync(state.ProductId, 3, ct)));
                shown = Offer.Period;
                break;

            case Offer.Stages:
                var stages = await _db.GetStagesAsync(state.ProductId, _config.TopStages, ct);
                if (stages.Count > 0)
                {
                    blocks.Add(StageListBlock(stages, state));
                    shown = Offer.Stages;
                }
                break;

            case Offer.Conditions:
                blocks.Add(ConditionsBlock(state));
                shown = Offer.Conditions;
                break;

            case Offer.Similar:
                var calcs = await _db.GetSimilarCalcsAsync(state.ProductId, 4, ct);
                if (calcs.Count > 0)
                {
                    blocks.Add(SimilarCalcsBlock(calcs));
                    shown = Offer.Similar;
                }
                break;

            case Offer.Tz:
                await AppendDraftAsync(sessionId, state, blocks, ct);
                shown = Offer.Tz;
                break;
        }

        // Запоминаем только то, что реально отрисовалось: гейт мог отклонить
        // предложение модели, и тогда прошлое состояние важнее её пожелания.
        if (shown != Offer.None) state.LastOffer = shown;
    }

    /// <summary>Номер варианта из реплики -> идентификатор услуги из последнего списка.</summary>
    private static string? ResolveOption(ChatState state, int? index)
    {
        if (index is null) return null;
        var i = index.Value - 1;
        return i >= 0 && i < state.LastOptions.Count ? state.LastOptions[i].Id : null;
    }

    /// <summary>Номера этапов из последнего показанного списка -> сами этапы.</summary>
    private static List<OptionRef> ResolveStages(ChatState state, IReadOnlyList<int> numbers)
    {
        var picked = new List<OptionRef>();
        foreach (var number in numbers)
        {
            var i = number - 1;
            if (i < 0 || i >= state.LastStages.Count) continue;
            var option = state.LastStages[i];
            if (picked.All(p => p.Id != option.Id)) picked.Add(option);
        }
        return picked;
    }

    private static void ResetState(ChatState state)
    {
        state.ResetFrom("product");
        state.ProductId = null;
        state.ProductName = null;
        state.ProductCategory = null;
        state.TemplateId = null;
        state.TypicalDays = null;
        state.LastOptions.Clear();
        state.LastStages.Clear();
        state.LastOffer = null;
        state.StepName = Step.Idle;
    }

    // Вопросные слова, с которых обычно начинается вопрос ассистенту («а какие
    // этапы?», «сколько это длится»). Используется детерминированной веткой
    // (Brain.Fallback), когда модель недоступна: вопрос нельзя записать в поле
    // «объект работ» — это реплика, а не ответ.
    private static readonly HashSet<string> QuestionStarts = new(StringComparer.OrdinalIgnoreCase)
    {
        "что", "как", "какой", "какая", "какое", "какие", "сколько", "почему", "зачем",
        "когда", "где", "кто", "кого", "кому", "чем", "чему", "какую", "каким",
        "можно", "нельзя", "подскажи", "расскажи", "покажи", "объясни", "уточни", "а",
    };

    /// <summary>Похоже ли сообщение на вопрос к ассистенту, а не на ответ боту.</summary>
    internal static bool LooksLikeQuestion(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.EndsWith('?')) return true;
        var first = trimmed
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault()?
            .Trim('?', '!', '.', ',', ';', '«', '»', '"');
        return !string.IsNullOrEmpty(first) && QuestionStarts.Contains(first!);
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
        // иначе доля распознанных запросов в аналитике равна 100 % при любом
        // качестве поиска.
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
            RememberOptions(state, hits);
            blocks.Add(ProductListBlock(hits, tentative: true));
            blocks.Add(ClarifyBlock());
            return;
        }

        // --- ровно один вызов LLM: данные уже найдены, модель только формулирует
        await StreamAnswerAsync(BuildSearchPrompt(text, hits, degraded), emit, ct);
        RememberOptions(state, hits);
        blocks.Add(ProductListBlock(hits, tentative: false));
    }

    /// <summary>
    /// Список показанных вариантов запоминается в состоянии, чтобы выбор
    /// работал словами: «давай первый» — это номер в этом списке, а не
    /// идентификатор, который модель могла бы выдумать.
    /// </summary>
    private static void RememberOptions(ChatState state, List<ProductHit> hits)
    {
        state.LastOptions = hits
            .Select(h => new OptionRef { Id = h.ProductId, Title = h.Name })
            .ToList();
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

            case "extract_tz":
            case "suggest_fields": // legacy-алиас: кнопка из ранее сохранённых ходов
                await ExtractTzFromDialogueAsync(sessionId, state, blocks, ct);
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
                state.ProductCategory = null;
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
        state.ProductCategory = card.Category;
        state.TemplateId = card.TemplateId;
        state.TypicalDays = card.TypicalDays;
        state.StepName = Step.ProductPicked;

        await _db.LogEventAsync(sessionId, "product_selected",
            Json.Write(new { productId, name = state.ProductName }), ct);

        // Сроки могли прозвучать в разговоре ещё до выбора услуги («концепт
        // обустройства, старт в октябре на полгода») — тогда спрашивать их
        // заново нельзя: сразу подбираем исполнителей на уже названный период.
        var periodKnown = state.Period.IsSet;

        blocks.Add(Block.TextBlock(
            $"Выбрана услуга «{state.ProductName}». " +
            (similar.Count > 0
                ? $"По ней в системе {similar.Count}+ выполненных работ — ниже похожие. "
                : "") +
            (periodKnown
                ? $"Сроки уже известны: {state.Period.From} — {state.Period.To}, подбираю исполнителей."
                : "Укажите желаемые сроки — подберу исполнителей, свободных в этот период.")));

        if (periodKnown)
        {
            await ApplyPeriodAsync(sessionId, state,
                DateOnly.Parse(state.Period.From!), DateOnly.Parse(state.Period.To!), blocks, ct);
        }
        else
        {
            blocks.Add(PeriodRequestBlock(state, similar));
            state.LastOffer = Offer.Period;
        }
        if (similar.Count > 0) blocks.Add(SimilarCalcsBlock(similar));
        if (related.Count > 0) blocks.Add(RelatedBlock(related));
        blocks.Add(RecommendationsBlock(state, stages, operations));

        // Явная кнопка, а не тихий вызов LLM здесь же: SelectProductAsync не
        // получает emit (блоки флашатся только после возврата из
        // HandleActionAsync), поэтому синхронный вызов CompleteJsonAsync
        // держал бы период-карточку в подвешенном состоянии до 45 секунд при
        // недоступности LLM. extract_tz — отдельное действие со своим ходом,
        // таймаутом и стримингом статуса.
        blocks.Add(ExtractTzButton());
    }

    /// <summary>
    /// Кнопка ручного запуска экстракции полей ТЗ из всего диалога. Показываем
    /// сразу после выбора услуги и потом на каждом пересчёте черновика — юзер
    /// сам решает, когда «уже наговорил достаточно, разложи по ТЗ».
    /// </summary>
    private static Block ExtractTzButton() => new()
    {
        Type = "actions",
        Items = new JsonArray
        {
            new JsonObject
            {
                ["action"] = "extract_tz",
                ["title"] = "Генерация технического задания на основании диалога"
            }
        }
    };

    private async Task SetPeriodAsync(
        Guid sessionId, ChatState state, TurnAction action, List<Block> blocks, CancellationToken ct)
    {
        if (!DateOnly.TryParse(action.From, out var from) || !DateOnly.TryParse(action.To, out var to))
        {
            blocks.Add(Block.TextBlock("Не понял даты. Укажите период в формате ГГГГ-ММ-ДД."));
            return;
        }
        if (to < from) (from, to) = (to, from);
        await ApplyPeriodAsync(sessionId, state, from, to, blocks, ct);
    }

    /// <summary>
    /// Единственное место, где записывается период: и карточка, и сроки,
    /// названные словами в диалоге, приходят сюда. Иначе поведение кнопок и
    /// разговора разъехалось бы — например, подбор исполнителей запускался бы
    /// только по клику.
    /// </summary>
    private async Task ApplyPeriodAsync(
        Guid sessionId, ChatState state, DateOnly from, DateOnly to, List<Block> blocks, CancellationToken ct)
    {
        state.ResetFrom("period");
        state.Period = new Period { From = from.ToString("yyyy-MM-dd"), To = to.ToString("yyyy-MM-dd") };
        state.StepName = Step.PeriodSet;

        // Услуга ещё не выбрана — период просто записан как факт заявки:
        // подбирать исполнителей не по чему, но и одёргивать человека
        // («сначала выберите услугу») незачем, он назвал сроки по своей воле.
        // Список исполнителей появится сразу после выбора услуги.
        if (state.ProductId is null) return;

        var executors = await _db.FindExecutorsAsync(
            state.ProductId, from, to, allowSubcontract: true, _config.TopExecutors, ct);

        var days = state.Period.Days;
        var warning = state.TypicalDays is > 0 && days < state.TypicalDays * 0.8
            ? $" Обратите внимание: заявленный срок {days} дн. меньше типового ({state.TypicalDays} дн.) — это попадёт в риски ТЗ."
            : "";

        // Кандидаты в ops.find_executors не зависят от периода — дата влияет только
        // на загрузку в рамках уже отобранного списка, а не на то, кто в него попал
        // (перегруженных функция не отбрасывает). Поэтому пустой список — это всегда
        // отсутствие опыта по услуге/категории у всех компаний, а не «неудачный срок»,
        // и функция сама подставляет общий список активных компаний вместо тупика
        // (см. is_fallback в ops.find_executors) — сюда мы попадаем, только если в
        // каталоге вовсе нет активных компаний.
        var fallback = executors.Count > 0 && executors.All(e => e.IsFallback);
        blocks.Add(Block.TextBlock(
            executors.Count == 0
                ? "В каталоге нет ни одной активной компании — подобрать исполнителей нечем. " +
                  "Это ограничение данных, сроки здесь не помогут."
                : fallback
                    ? "Подтверждённого опыта по этой услуге или её категории ни у одной компании нет — " +
                      "показываю общий список активных компаний по рейтингу и загрузке, без гарантии, " +
                      "что они умеют делать именно эту работу. Сроки на этот список не влияют — дело не " +
                      "в датах. Выберите исполнителя вручную или уточните позже, когда появится история."
                    : $"Нашёл {executors.Count} исполнителей на период {state.Period.From} — {state.Period.To}. " +
                      "Список отсортирован по опыту, доступности и рейтингу." + warning));

        if (executors.Count > 0)
        {
            blocks.Add(ExecutorListBlock(executors));
            // Список уже показан — OfferAsync в этом же ходу его не продублирует.
            state.LastOffer = Offer.Executors;
        }
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

    private const string ExtractTzSystemPromptHead =
        "Ты извлекаешь данные для технического задания (ТЗ) на нефтесервисные и " +
        "инжиниринговые услуги из переписки заказчика с ассистентом подбора. Тебе " +
        "дан транскрипт ВСЕГО диалога и список полей ТЗ с пояснениями. Разложи по " +
        "полям только то, что заказчик реально сказал или подтвердил по ходу " +
        "диалога — учитывай все его реплики, а не только последнюю. Ничего не " +
        "придумывай, не обобщай и не подставляй «стандартные» формулировки. Если " +
        "по полю в диалоге нет явных данных — не включай его ключ в ответ.\n\n" +
        "Поля ТЗ (ключ — назначение):";

    private const string ExtractTzSystemPromptTail =
        "\nОтветь СТРОГО одним JSON-объектом: ключи — идентификаторы полей выше " +
        "(только те, для которых нашлись данные), значения — короткие деловые " +
        "формулировки на русском языке. Без markdown, без пояснений.";

    private static string BuildExtractTzSystemPrompt(IReadOnlyList<TzTextField> fields)
    {
        var sb = new StringBuilder(ExtractTzSystemPromptHead);
        sb.AppendLine();
        foreach (var field in fields)
        {
            sb.Append("- ").Append(field.Key).Append(" — ").Append(field.Title);
            if (!string.IsNullOrWhiteSpace(field.Hint)) sb.Append(" (").Append(field.Hint).Append(')');
            sb.AppendLine();
        }
        sb.Append(ExtractTzSystemPromptTail);
        return sb.ToString();
    }

    /// <summary>
    /// Схема ответа: объект со строковым (или null) значением на каждый ключ
    /// поля. required + additionalProperties:false — чтобы строгий json_schema
    /// на поддерживающих провайдерах не отвергался; null-значения потом
    /// отсеиваются как «поле не найдено».
    /// </summary>
    private static JsonReplySchema BuildExtractTzSchema(IReadOnlyList<TzTextField> fields)
    {
        var properties = new JsonObject();
        var required = new JsonArray();
        foreach (var field in fields)
        {
            properties[field.Key] = new JsonObject
            {
                ["type"] = new JsonArray("string", "null"),
                ["description"] = field.Title
            };
            required.Add((JsonNode)field.Key);
        }

        return new JsonReplySchema("tz_fields", new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = required,
            ["additionalProperties"] = false
        });
    }

    /// <summary>
    /// Ручная экстракция текстовых полей ТЗ из всего диалога (кнопка
    /// «Генерация технического задания на основании диалога»). Явное действие
    /// пользователя со своим ходом и таймаутом. Список полей и подсказки
    /// берутся из шаблона конструктора (TzClient.GetTextFieldsAsync) — модель
    /// сама раскладывает переписку по реальным полям. Ничего не применяется в
    /// ChatState автоматически: каждое поле пользователь принимает отдельно
    /// обычным set_field на фронте (Blocks.tsx, SuggestedFields).
    /// </summary>
    private async Task ExtractTzFromDialogueAsync(
        Guid sessionId, ChatState state, List<Block> blocks, CancellationToken ct)
    {
        var transcript = await _db.GetDialogueTranscriptAsync(sessionId, ct);
        if (string.IsNullOrWhiteSpace(transcript))
        {
            blocks.Add(Block.TextBlock(
                "Диалог пока пустой — опишите потребность, тогда будет что разложить по ТЗ."));
            return;
        }

        var fields = await _tz.GetTextFieldsAsync(state.TemplateId, ct);
        if (fields.Count == 0)
        {
            blocks.Add(Block.TextBlock(
                "Не удалось получить список полей ТЗ из конструктора — заполните их вручную."));
            return;
        }

        JsonObject? result;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(_config.LlmTimeoutSeconds));
            result = await _llm.CompleteJsonAsync(
                BuildExtractTzSystemPrompt(fields), transcript, BuildExtractTzSchema(fields), cts.Token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _log.LogWarning(ex, "экстракция полей ТЗ из диалога недоступна");
            blocks.Add(Block.TextBlock(
                "Не удалось разобрать диалог — заполните поля ТЗ вручную в конструкторе."));
            return;
        }

        var items = new JsonArray();
        if (result is not null)
        {
            foreach (var field in fields)
            {
                var value = result[field.Key] is JsonValue v && v.TryGetValue<string>(out var s)
                    ? s.Trim()
                    : null;
                if (string.IsNullOrWhiteSpace(value)) continue;
                items.Add(new JsonObject
                {
                    ["key"] = field.Key,
                    ["label"] = field.Title,
                    ["value"] = value
                });
            }
        }

        if (items.Count == 0)
        {
            blocks.Add(Block.TextBlock(
                "В диалоге не нашлось данных, которые можно разложить по полям ТЗ — заполните вручную в конструкторе."));
            return;
        }

        await _db.LogEventAsync(sessionId, "tz_extracted",
            Json.Write(new { fields = items.Count }), ct);

        blocks.Add(new Block
        {
            Type = "suggested_fields",
            Text = "Вот что удалось собрать из диалога для ТЗ — проверьте каждое поле и примените нужные",
            Items = items
        });
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
                new JsonObject
                {
                    ["action"] = "extract_tz",
                    ["title"] = "Генерация технического задания на основании диалога"
                },
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
            ["isFallback"] = e.IsFallback,
            ["reasons"] = new JsonArray(e.Reasons.Select(r => (JsonNode)r!).ToArray())
        }).ToArray())
    };

    private static Block StageListBlock(List<StageInfo> stages, ChatState state)
    {
        var chosen = state.Stages.Select(s => s.Key).ToHashSet();

        // Список запоминается там же, где строится: нумерация в промпте модели
        // и порядок пунктов на экране обязаны совпадать, иначе «выбери этап 3»
        // отметило бы не тот этап. Отдельным вызовом рядом они бы разъехались.
        state.LastStages = stages
            .Select(s => new OptionRef { Id = s.Key, Title = s.Name })
            .ToList();

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
