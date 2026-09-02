using System.Text;
using System.Text.Json.Nodes;

namespace Prostor.Chat;

/// <summary>
/// Намерение хода. Решает модель, выполняет — код: идентификаторы услуг,
/// компаний и SQL модель не видит и придумать не может.
/// </summary>
public static class Intent
{
    /// <summary>Просто разговор: вопрос, обсуждение, уточнение, small talk.</summary>
    public const string Answer = "answer";

    /// <summary>Пользователь описал работы или попросил подобрать услугу.</summary>
    public const string SearchServices = "search_services";

    /// <summary>Пользователь ищет исполнителя/подрядчика вне контекста заявки.</summary>
    public const string SearchExecutors = "search_executors";

    /// <summary>«Давай первый», «бери второй вариант» — выбор из показанного списка.</summary>
    public const string PickOption = "pick_option";

    /// <summary>«Начнём заново», «забудь эту услугу».</summary>
    public const string Restart = "restart";
}

/// <summary>
/// Что уместно показать карточкой прямо сейчас. Это ПРЕДЛОЖЕНИЕ модели —
/// разрешение выдаёт код (TurnPipeline.OfferAsync): гейты по заполненным
/// слотам и защита от повтора одного и того же предложения.
/// </summary>
public static class Offer
{
    public const string None = "none";
    public const string Period = "period";
    public const string Executors = "executors";
    public const string Stages = "stages";
    public const string Conditions = "conditions";
    public const string Similar = "similar";
    public const string Tz = "tz";
}

/// <summary>
/// Сущности, вытащенные из разговора. Это данные для ТЗ, а не команды:
/// каждое поле применяется кодом с проверкой (даты — парсингом, флаги — по
/// белому списку), и ни одно из них не меняет каталог или выбор услуги.
/// </summary>
public sealed class BrainFacts
{
    public string? Object { get; init; }
    public string? Purpose { get; init; }
    public string? Customer { get; init; }
    public string? Perimeter { get; init; }
    public string? SourceData { get; init; }
    public string? Documentation { get; init; }
    public string? Acceptance { get; init; }
    public string? Other { get; init; }
    public string? PeriodFrom { get; init; }
    public string? PeriodTo { get; init; }
    public IReadOnlyList<string> Flags { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Номера этапов из последнего показанного списка, названные словами.
    /// Номера, а не ключи: идентификаторов модель не видит и придумать не
    /// может — их подставляет код (TurnPipeline.ResolveStages).
    /// </summary>
    public IReadOnlyList<int> StageNumbers { get; init; } = Array.Empty<int>();

    public static readonly BrainFacts Empty = new();
}

/// <summary>Решение «мозга» диалога на один ход.</summary>
public sealed record BrainDecision(
    string Reply,
    string Intent,
    string? Query,
    int? OptionIndex,
    BrainFacts Facts,
    string Offer,
    IReadOnlyList<string> Suggestions);

/// <summary>
/// Мозг диалога: один структурированный вызов LLM на ход свободного текста.
///
/// Раньше ход разбивался на два вызова — роутер (куда направить) и
/// консультация (что ответить), — и каждый из них видел только часть картины:
/// роутер не знал деталей услуги, консультация не умела предлагать следующий
/// шаг, а сущности для ТЗ вытаскивались отдельной кнопкой в конце. Отсюда
/// «одна заготовленная фраза» на любой вопрос после выбора услуги.
///
/// Теперь один вызов отвечает сразу на четыре вопроса хода:
///   reply       — что сказать человеку (в контексте всего диалога и данных заявки);
///   intent      — нужен ли поиск, выбор из списка или просто разговор;
///   facts       — какие данные для ТЗ прозвучали в разговоре;
///   offer       — какую карточку уместно показать следующей;
///   suggestions — чем можно продолжить (чипы под ответом).
///
/// Модель по-прежнему не ходит в базу и не выбирает идентификаторы: поиск,
/// подбор исполнителей и запись слотов выполняет детерминированный код
/// (см. docs/architecture.md §5). Отказ модели — не отказ хода: есть
/// детерминированный <see cref="Fallback"/>.
/// </summary>
public static class Brain
{
    public const string SystemPrompt =
        "Ты — ведущий диалога в платформе ПРОСТОР: подбор нефтесервисных и инжиниринговых " +
        "услуг и подготовка технического задания (ТЗ). Ты одновременно собеседник, аналитик " +
        "и навигатор.\n\n" +
        "Собеседник: отвечаешь по существу на ЛЮБУЮ реплику — вопрос об услуге, о сроках, о " +
        "платформе, сомнение, возражение, приветствие, отвлечённый вопрос. Никогда не " +
        "отвечай шаблонной отпиской вроде «мы уже работаем с услугой X, спросите о ней» — " +
        "у тебя есть контекст, отвечай содержательно и по-разному.\n\n" +
        "Аналитик: из разговора вытаскиваешь данные для ТЗ (объект работ, цель, заказчик, " +
        "периметр, исходные данные, требования к документации, порядок приёмки, сроки, " +
        "особые условия). Берёшь только то, что человек реально сказал или подтвердил, — " +
        "ничего не додумываешь и не подставляешь «стандартные» формулировки.\n\n" +
        "Навигатор: сам понимаешь, что логично сделать дальше, и предлагаешь это словами в " +
        "reply и полем offer. Не гони человека по шагам: если он задал вопрос или просто " +
        "рассуждает — отвечай, offer = \"none\".\n\n" +
        "Ответ — СТРОГО один JSON-объект со всеми полями:\n" +
        "- reply: ответ человеку по-русски, 1–4 предложения, деловым, но живым тоном, без " +
        "markdown и без списков. Заполняется ВСЕГДА, в том числе при поиске (тогда это " +
        "короткое подтверждение: что именно ты понял и что сейчас подберёшь).\n" +
        "- intent: \"search_services\" — человек описал вид работ или просит подобрать/сменить " +
        "услугу; \"search_executors\" — ищет подрядчика вне заявки («кто это может сделать»); " +
        "\"pick_option\" — просит взять вариант из показанного списка («давай первый», «бери " +
        "второй»); \"restart\" — просит начать заново; \"answer\" — всё остальное, включая " +
        "вопросы, обсуждение и сообщение данных для ТЗ.\n" +
        "- query: для intent=search_* — самодостаточная формулировка запроса на русском, " +
        "собранная из диалога (если человек сказал «а найди подешевле», подставь, о каких " +
        "работах речь). Иначе null.\n" +
        "- optionIndex: для intent=pick_option — номер варианта в последнем показанном " +
        "списке, начиная с 1. Иначе null.\n" +
        "- facts: объект с полями object, purpose, customer, perimeter, sourceData, " +
        "documentation, acceptance, other, periodFrom, periodTo, flags. Указывай ТОЛЬКО то, " +
        "что прозвучало в диалоге и ещё не записано в состоянии заявки; остальное — null. " +
        "periodFrom/periodTo — даты в формате ГГГГ-ММ-ДД (переводи «с сентября», «три " +
        "месяца с начала октября» в конкретные даты, опираясь на сегодняшнюю дату из " +
        "контекста); если срок назван неоднозначно — null и переспроси в reply. flags — " +
        "массив из значений \"model3d\" (нужна 3D геологическая модель), \"subcontract\" " +
        "(допускается субподряд), \"urgent\" (срочно); пустой массив, если ничего такого не " +
        "прозвучало. stageNumbers — номера этапов из показанного списка этапов, которые " +
        "человек назвал словами («давай этап 1 Уточнение и этап 3.1»); сопоставляй по " +
        "названию, даже если он назвал его неточно или с опечаткой, а если не уверен, какой " +
        "именно этап имеется в виду — пустой массив и переспроси в reply. Названия этапов " +
        "НИКОГДА не пиши в other и другие текстовые поля: этапы выбираются только номерами.\n" +
        "- offer: что показать карточкой прямо сейчас — \"period\" (спросить сроки), " +
        "\"executors\" (показать исполнителей), \"stages\" (этапы работ), \"conditions\" " +
        "(условия выполнения), \"similar\" (похожие выполненные работы), \"tz\" (готовность " +
        "ТЗ и переход в конструктор) или \"none\". Ставь \"none\", если человек задал вопрос " +
        "или ещё не готов двигаться дальше: карточка не должна перебивать разговор.\n" +
        "- suggestions: 2–4 очень коротких (до 6 слов) варианта следующей реплики ОТ ЛИЦА " +
        "ПОЛЬЗОВАТЕЛЯ — то, что ему уместно спросить или сказать дальше. Пустой массив, если " +
        "подсказки неуместны.\n\n" +
        "Жёсткие ограничения: не выдумывай названия услуг, компании, цены, сроки и цифры — " +
        "пользуйся только данными из контекста; чего в них нет — честно скажи, что не " +
        "знаешь, и предложи, что уточнить. Не повторяй дословно свои прошлые реплики.";

    private static readonly string[] FactKeys =
    {
        "object", "purpose", "customer", "perimeter",
        "sourceData", "documentation", "acceptance", "other",
        "periodFrom", "periodTo"
    };

    /// <summary>
    /// Строгая схема ответа: все ключи required, additionalProperties запрещены —
    /// иначе провайдеры со строгим json_schema отвергают запрос. Отсутствие
    /// данных выражается через null, а не через пропуск ключа.
    /// </summary>
    public static readonly JsonReplySchema Schema = new("dialogue_turn", new JsonObject
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["reply"] = new JsonObject { ["type"] = "string" },
            ["intent"] = new JsonObject
            {
                ["type"] = "string",
                ["enum"] = new JsonArray(
                    Chat.Intent.Answer, Chat.Intent.SearchServices, Chat.Intent.SearchExecutors,
                    Chat.Intent.PickOption, Chat.Intent.Restart)
            },
            ["query"] = new JsonObject { ["type"] = new JsonArray("string", "null") },
            ["optionIndex"] = new JsonObject { ["type"] = new JsonArray("integer", "null") },
            ["facts"] = FactsSchema(),
            ["offer"] = new JsonObject
            {
                ["type"] = "string",
                ["enum"] = new JsonArray(
                    Chat.Offer.None, Chat.Offer.Period, Chat.Offer.Executors, Chat.Offer.Stages,
                    Chat.Offer.Conditions, Chat.Offer.Similar, Chat.Offer.Tz)
            },
            ["suggestions"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject { ["type"] = "string" }
            }
        },
        ["required"] = new JsonArray("reply", "intent", "query", "optionIndex", "facts", "offer", "suggestions"),
        ["additionalProperties"] = false
    });

    private static JsonObject FactsSchema()
    {
        var properties = new JsonObject();
        var required = new JsonArray();
        foreach (var key in FactKeys)
        {
            properties[key] = new JsonObject { ["type"] = new JsonArray("string", "null") };
            required.Add((JsonNode)key);
        }
        properties["flags"] = new JsonObject
        {
            ["type"] = "array",
            ["items"] = new JsonObject
            {
                ["type"] = "string",
                ["enum"] = new JsonArray("model3d", "subcontract", "urgent")
            }
        };
        required.Add((JsonNode)"flags");
        properties["stageNumbers"] = new JsonObject
        {
            ["type"] = "array",
            ["items"] = new JsonObject { ["type"] = "integer" }
        };
        required.Add((JsonNode)"stageNumbers");

        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = required,
            ["additionalProperties"] = false
        };
    }

    /// <summary>
    /// Контекст хода для модели: сегодняшняя дата (без неё «с сентября» не
    /// превратить в дату), состояние заявки, карточка услуги с фактами из
    /// базы, уже собранные поля ТЗ, последний показанный список (для
    /// pick_option) и хвост диалога.
    /// </summary>
    public static string BuildPrompt(
        ChatState state, string text, ProductCard? card, List<StageInfo> stages,
        List<SimilarCalc> similar, string? transcriptTail, DateOnly today)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Сегодня: {today:yyyy-MM-dd}.");
        sb.AppendLine();
        sb.AppendLine($"Новое сообщение пользователя: {text}");
        sb.AppendLine();

        sb.AppendLine("Состояние заявки:");
        if (state.ProductId is null)
        {
            sb.AppendLine("- услуга ещё не выбрана");
        }
        else
        {
            sb.AppendLine($"- услуга: «{state.ProductName}» (категория: {state.ProductCategory ?? "—"})");
            if (card is not null)
            {
                if (card.TypicalDays is > 0) sb.AppendLine($"- типовой срок услуги: {card.TypicalDays} дн.");
                if (card.CalcsCount > 0) sb.AppendLine($"- выполнено работ по услуге в системе: {card.CalcsCount}");
                if (card.CompaniesCount > 0) sb.AppendLine($"- компаний с опытом: {card.CompaniesCount}");
            }
        }

        sb.AppendLine(state.Period.IsSet
            ? $"- сроки: {state.Period.From} — {state.Period.To} ({state.Period.Days} дн.)"
            : "- сроки: не указаны");
        sb.AppendLine(state.Executors.Count > 0
            ? "- исполнители: " + string.Join(", ", state.Executors.Select(e =>
                e.Name + (e.Subcontract ? " (субподряд)" : "")))
            : "- исполнители: не выбраны");
        sb.AppendLine(state.Stages.Count > 0
            ? "- выбранные этапы: " + string.Join("; ", state.Stages.Select(s => s.Name))
            : "- этапы: не выбраны");

        var flags = state.Flags.Where(f => f.Value).Select(f => f.Key).ToList();
        if (flags.Count > 0) sb.AppendLine("- отмеченные условия: " + string.Join(", ", flags));

        var filled = FilledFields(state);
        sb.AppendLine(filled.Count > 0
            ? "Уже записанные поля ТЗ (повторно в facts не присылай, если человек их не менял):\n" +
              string.Join("\n", filled.Select(f => $"- {f.Key}: {f.Value}"))
            : "Поля ТЗ пока не заполнены ни одного.");

        if (stages.Count > 0)
            sb.AppendLine("Типовые этапы услуги по истории работ: " + string.Join("; ",
                stages.Take(8).Select(s => s.Name + (s.MedianDays is > 0 ? $" (~{s.MedianDays} дн.)" : ""))));

        if (similar.Count > 0)
            sb.AppendLine("Похожие выполненные работы: " + string.Join("; ",
                similar.Take(3).Select(c => $"«{c.Name}»" +
                    (c.CompanyName is not null ? $", {c.CompanyName}" : "") +
                    (c.DurationDays is > 0 ? $", {c.DurationDays} дн." : ""))));

        if (state.LastOptions.Count > 0)
            sb.AppendLine("Последний показанный список услуг (нумерация для optionIndex): " +
                string.Join("; ", state.LastOptions.Select((o, i) => $"{i + 1}) {o.Title}")));

        // Ровно то, что человек видит в карточке этапов, и в том же порядке —
        // иначе номер из facts.stageNumbers указал бы не на тот этап.
        if (state.LastStages.Count > 0)
            sb.AppendLine("Показанный список этапов (нумерация для facts.stageNumbers): " +
                string.Join("; ", state.LastStages.Select((o, i) => $"{i + 1}) {o.Title}")));

        if (!string.IsNullOrWhiteSpace(transcriptTail))
            sb.AppendLine("\nХод диалога (последняя реплика заказчика — текущее сообщение):\n" + transcriptTail);

        return sb.ToString();
    }

    public static List<KeyValuePair<string, string>> FilledFields(ChatState state)
    {
        var pairs = new List<KeyValuePair<string, string>>();
        void Add(string key, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value)) pairs.Add(new KeyValuePair<string, string>(key, value!));
        }

        Add("object", state.Object);
        Add("purpose", state.Purpose);
        Add("customer", state.Customer);
        Add("perimeter", state.Perimeter);
        Add("sourceData", state.SourceData);
        Add("documentation", state.Documentation);
        Add("acceptance", state.Acceptance);
        Add("other", state.Other);
        return pairs;
    }

    /// <summary>Разбор ответа модели. Что не разобралось — то и не применится.</summary>
    public static BrainDecision? Parse(JsonObject? result)
    {
        if (result is null) return null;

        var reply = Str(result["reply"]);
        var intent = Str(result["intent"]) ?? Chat.Intent.Answer;
        var offer = Str(result["offer"]) ?? Chat.Offer.None;

        // Пустой JSON-объект — это заглушка без ключа (StubLlm), а не решение:
        // такой ход должен уйти в детерминированный Fallback.
        if (reply is null && result["intent"] is null) return null;

        var facts = ParseFacts(result["facts"] as JsonObject);
        var suggestions = (result["suggestions"] as JsonArray)?
            .Select(n => Str(n))
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s!.Trim())
            .Take(4)
            .ToList() ?? new List<string>();

        int? optionIndex = result["optionIndex"] is JsonValue ov && ov.TryGetValue<int>(out var oi) ? oi : null;

        return new BrainDecision(reply?.Trim() ?? "", intent, Str(result["query"]), optionIndex,
            facts, offer, suggestions);
    }

    private static BrainFacts ParseFacts(JsonObject? facts)
    {
        if (facts is null) return BrainFacts.Empty;
        return new BrainFacts
        {
            Object = Str(facts["object"]),
            Purpose = Str(facts["purpose"]),
            Customer = Str(facts["customer"]),
            Perimeter = Str(facts["perimeter"]),
            SourceData = Str(facts["sourceData"]),
            Documentation = Str(facts["documentation"]),
            Acceptance = Str(facts["acceptance"]),
            Other = Str(facts["other"]),
            PeriodFrom = Str(facts["periodFrom"]),
            PeriodTo = Str(facts["periodTo"]),
            Flags = (facts["flags"] as JsonArray)?
                .Select(n => Str(n))
                .Where(s => s is "model3d" or "subcontract" or "urgent")
                .Select(s => s!)
                .Distinct()
                .ToList() ?? (IReadOnlyList<string>)Array.Empty<string>(),
            StageNumbers = (facts["stageNumbers"] as JsonArray)?
                .Select(n => n is JsonValue v && v.TryGetValue<int>(out var i) ? i : 0)
                .Where(i => i > 0)
                .Distinct()
                .ToList() ?? (IReadOnlyList<int>)Array.Empty<int>()
        };
    }

    private static string? Str(JsonNode? node)
    {
        if (node is not JsonValue value) return null;
        if (!value.TryGetValue<string>(out var s)) return null;
        return string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    }

    /// <summary>
    /// Детерминированное решение, когда модели нет (нет ключа) или она не
    /// ответила. Не «одна заготовленная фраза»: ответ собирается из реального
    /// состояния заявки, поэтому в демо без ключей диалог остаётся осмысленным.
    /// </summary>
    public static BrainDecision Fallback(ChatState state, string text)
    {
        if (state.ProductId is null)
            return new BrainDecision("", Chat.Intent.SearchServices, text, null,
                BrainFacts.Empty, Chat.Offer.None, Array.Empty<string>());

        if (!TurnPipeline.LooksLikeQuestion(text) && string.IsNullOrWhiteSpace(state.Object))
            return new BrainDecision(
                "Записал это как объект работ. Если я не так понял — скажите, поправлю.",
                Chat.Intent.Answer, null, null,
                new BrainFacts { Object = text }, Chat.Offer.Tz, DefaultSuggestions(state));

        var sb = new StringBuilder();
        sb.Append($"По заявке сейчас так: услуга «{state.ProductName}»");
        sb.Append(state.Period.IsSet
            ? $", сроки {state.Period.From} — {state.Period.To}"
            : ", сроки не указаны");
        sb.Append(state.Executors.Count > 0
            ? $", исполнители: {string.Join(", ", state.Executors.Select(e => e.Name))}"
            : ", исполнители не выбраны");
        sb.Append(state.Stages.Count > 0 ? $", этапов отмечено {state.Stages.Count}." : ", этапы не отмечены.");

        var missing = state.Missing();
        if (missing.Count > 0)
            sb.Append(" Чтобы собрать ТЗ, не хватает: " + string.Join(", ", missing.Select(FieldTitle)) + ".");

        return new BrainDecision(sb.ToString(), Chat.Intent.Answer, null, null,
            BrainFacts.Empty, NextOffer(state), DefaultSuggestions(state));
    }

    /// <summary>Какой шаг напрашивается по состоянию заявки — без участия модели.</summary>
    public static string NextOffer(ChatState state)
    {
        if (state.ProductId is null) return Chat.Offer.None;
        if (!state.Period.IsSet) return Chat.Offer.Period;
        if (state.Executors.Count == 0) return Chat.Offer.Executors;
        if (state.Stages.Count == 0) return Chat.Offer.Stages;
        return Chat.Offer.Tz;
    }

    private static List<string> DefaultSuggestions(ChatState state)
    {
        var items = new List<string>();
        if (state.ProductId is not null)
        {
            if (!state.Period.IsSet) items.Add("Какие сроки обычно у этой услуги?");
            if (state.Stages.Count == 0) items.Add("Какие этапы входят в услугу?");
            if (state.Executors.Count == 0) items.Add("Кто может это выполнить?");
            items.Add("Что ещё нужно для ТЗ?");
        }
        return items.Take(3).ToList();
    }

    public static string FieldTitle(string key) => key switch
    {
        "productId" => "услуга",
        "period" => "сроки",
        "executors" => "исполнители",
        "stages" => "этапы",
        "object" => "объект работ",
        "purpose" => "цель работ",
        "customer" => "заказчик",
        "perimeter" => "периметр работ",
        "sourceData" => "исходные данные",
        "documentation" => "требования к документации",
        "acceptance" => "порядок приёмки",
        "other" => "особые условия",
        // Условия выполнения складываются в один ключ: значением идёт название
        // самого условия (FlagTitle), поэтому подпись здесь общая.
        "flag" => "условие",
        _ => key
    };
}
