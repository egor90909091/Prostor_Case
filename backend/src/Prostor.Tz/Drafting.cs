using System.Text;
using System.Text.Json.Nodes;

namespace Prostor.Tz;

public sealed record TemplateDefinition(
    string TemplateId, string Name, string TypeCode,
    JsonArray Sections, JsonArray Fields, JsonArray Risks);

public sealed record Risk(string Code, string Severity, string Title, string Recommendation);

public sealed record FieldStatus(
    string Key, string Section, string Title, int Weight, bool Blocking, bool Filled, string? Hint);

public sealed record Draft(
    string TemplateId, string TemplateName, int Readiness, bool CanGenerate,
    List<FieldStatus> Fields, List<Risk> Risks, JsonArray Sections, string Recommendation);

/// <summary>
/// Сборка черновика ТЗ, расчёт процента готовности и проверка рисков.
///
/// Правила не зашиты в код: они лежат в tz.template (required_fields и
/// risk_rules) и интерпретируются здесь. Поэтому добавить новый риск —
/// это изменение строки в БД, а не релиз сервиса.
/// </summary>
public static class Drafting
{
    public static Draft Build(TemplateDefinition template, JsonObject state, int? typicalDays)
    {
        var fields = EvaluateFields(template.Fields, state);
        var readiness = fields.Where(f => f.Filled).Sum(f => f.Weight);
        var totalWeight = fields.Sum(f => f.Weight);
        if (totalWeight > 0 && totalWeight != 100)
            readiness = (int)Math.Round(readiness * 100.0 / totalWeight);

        var risks = EvaluateRisks(template.Risks, state, typicalDays);
        var canGenerate = risks.All(r => r.Severity != "blocking");

        return new Draft(
            template.TemplateId,
            template.Name,
            Math.Clamp(readiness, 0, 100),
            canGenerate,
            fields,
            risks,
            BuildSections(template, state),
            Recommend(risks));
    }

    // ------------------------------------------------------------ поля
    private static List<FieldStatus> EvaluateFields(JsonArray definitions, JsonObject state)
    {
        var result = new List<FieldStatus>();
        foreach (var node in definitions)
        {
            if (node is not JsonObject definition) continue;
            var key = definition["key"]?.GetValue<string>() ?? "";
            result.Add(new FieldStatus(
                key,
                definition["section"]?.GetValue<string>() ?? "",
                definition["title"]?.GetValue<string>() ?? key,
                definition["weight"]?.GetValue<int>() ?? 0,
                definition["blocking"]?.GetValue<bool>() ?? false,
                IsFilled(state, key),
                definition["hint"]?.GetValue<string>()));
        }
        return result;
    }

    private static bool IsFilled(JsonObject state, string key) => key switch
    {
        "period" => NotEmpty(state["period"]?["from"]) && NotEmpty(state["period"]?["to"]),
        "stages" => (state["stages"] as JsonArray)?.Count > 0,
        "operations" => (state["operationIds"] as JsonArray)?.Count > 0,
        "executors" => (state["executors"] as JsonArray)?.Count > 0,
        "source_data" => NotEmpty(state["sourceData"]),
        _ => NotEmpty(state[key])
    };

    private static bool NotEmpty(JsonNode? node) =>
        node is not null && !string.IsNullOrWhiteSpace(node.ToString().Trim('"'));

    // ------------------------------------------------------------ риски
    private static List<Risk> EvaluateRisks(JsonArray rules, JsonObject state, int? typicalDays)
    {
        var result = new List<Risk>();
        foreach (var node in rules)
        {
            if (node is not JsonObject rule) continue;
            if (!Evaluate(rule["when"], state, typicalDays)) continue;

            result.Add(new Risk(
                rule["code"]?.GetValue<string>() ?? "",
                rule["severity"]?.GetValue<string>() ?? "info",
                rule["title"]?.GetValue<string>() ?? "",
                rule["recommendation"]?.GetValue<string>() ?? ""));
        }

        return result
            .OrderBy(r => r.Severity switch { "blocking" => 0, "warning" => 1, _ => 2 })
            .ToList();
    }

    /// <summary>Интерпретатор мини-DSL из risk_rules.when.</summary>
    private static bool Evaluate(JsonNode? condition, JsonObject state, int? typicalDays)
    {
        if (condition is not JsonObject node) return false;
        var op = node["op"]?.GetValue<string>() ?? "";
        var arg = node["arg"]?.GetValue<string>();

        switch (op)
        {
            case "and":
                return node["args"] is JsonArray all && all.All(a => Evaluate(a, state, typicalDays));

            case "or":
                return node["args"] is JsonArray any && any.Any(a => Evaluate(a, state, typicalDays));

            case "not":
                return !Evaluate(node["args"]?[0], state, typicalDays);

            case "empty":
                return arg switch
                {
                    "period" => !(NotEmpty(state["period"]?["from"]) && NotEmpty(state["period"]?["to"])),
                    "source_data" => !NotEmpty(state["sourceData"]),
                    _ => !NotEmpty(state[arg ?? ""])
                };

            case "empty_list":
                return arg switch
                {
                    "stages" => (state["stages"] as JsonArray)?.Count is null or 0,
                    "executors" => (state["executors"] as JsonArray)?.Count is null or 0,
                    "operations" => (state["operationIds"] as JsonArray)?.Count is null or 0,
                    _ => true
                };

            case "flag":
                return state["flags"]?[arg ?? ""]?.GetValue<bool>() ?? false;

            case "missing_stage":
                {
                    var needle = (arg ?? "").ToLowerInvariant();
                    var stages = state["stages"] as JsonArray;
                    if (stages is null || stages.Count == 0) return true;
                    return !stages.Any(s =>
                        (s?["name"]?.GetValue<string>() ?? "").ToLowerInvariant().Contains(needle));
                }

            case "duration_below_typical":
                {
                    if (typicalDays is not > 0) return false;
                    if (!double.TryParse(arg, System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out var factor))
                        factor = 0.8;

                    var from = state["period"]?["from"]?.GetValue<string>();
                    var to = state["period"]?["to"]?.GetValue<string>();
                    if (!DateOnly.TryParse(from, out var f) || !DateOnly.TryParse(to, out var t)) return false;

                    var days = t.DayNumber - f.DayNumber + 1;
                    return days < typicalDays.Value * factor;
                }

            case "stages_without_docs":
                {
                    var stages = state["stages"] as JsonArray;
                    if (stages is null || stages.Count == 0) return false;
                    return stages.Any(s => !NotEmpty(s?["documentation"]));
                }

            default:
                return false;
        }
    }

    private static string Recommend(List<Risk> risks)
    {
        var blocking = risks.Where(r => r.Severity == "blocking").ToList();
        var warnings = risks.Where(r => r.Severity == "warning").ToList();

        if (blocking.Count == 0 && warnings.Count == 0)
            return "ТЗ готово к согласованию: обязательные разделы заполнены, критичных рисков нет.";

        var sb = new StringBuilder();
        if (blocking.Count > 0)
        {
            sb.Append("Перед согласованием необходимо устранить: ");
            sb.Append(string.Join("; ", blocking.Select(r => r.Title.ToLowerInvariant())));
            sb.Append('.');
        }
        if (warnings.Count > 0)
        {
            if (sb.Length > 0) sb.Append(' ');
            sb.Append("Рекомендуется дополнительно: ");
            sb.Append(string.Join("; ", warnings.Select(r => r.Recommendation.TrimEnd('.').ToLowerInvariant())));
            sb.Append('.');
        }
        return sb.ToString();
    }

    // ------------------------------------------------------------ секции
    /// <summary>Собирает содержимое разделов документа из состояния диалога.</summary>
    private static JsonArray BuildSections(TemplateDefinition template, JsonObject state)
    {
        var result = new JsonArray();
        foreach (var node in template.Sections)
        {
            if (node is not JsonObject section) continue;
            var key = section["key"]?.GetValue<string>() ?? "";
            var body = key switch
            {
                "purpose" => Text(state["purpose"])
                             ?? $"Выполнение работ по услуге «{Text(state["productName"]) ?? "—"}».",
                "abbreviations" => "ТЗ — техническое задание; ДО — дочернее общество; " +
                                   "ГМ — геологическая модель; ПТД — проектно-технический документ.",
                "perimeter" => Join(
                    Text(state["object"]) is { } o ? $"Объект работ: {o}." : null,
                    Text(state["perimeter"])),
                "schedule" => Schedule(state),
                "kpi" => Text(state["kpi"]),
                "content" => Content(state),
                "conditions" => Join(
                    Text(state["sourceData"]) is { } sd ? $"Требования к исходным данным: {sd}" : null,
                    Flags(state)),
                "documentation" => Text(state["documentation"])
                                   ?? "Результаты работ передаются информационными отчётами: " +
                                      "текстовая часть в DOC/DOCX и PDF, табличные приложения в XLSX, " +
                                      "графические приложения в векторном PDF.",
                "quality" => Text(state["acceptance"])
                             ?? "Приёмка результатов осуществляется заказчиком по каждому этапу " +
                                "на основании отчётных материалов, предусмотренных настоящим ТЗ.",
                "subcontract" => Executors(state),
                "other" => Text(state["other"]),
                _ => null
            };

            result.Add(new JsonObject
            {
                ["key"] = key,
                ["title"] = section["title"]?.GetValue<string>(),
                ["required"] = section["required"]?.GetValue<bool>() ?? false,
                ["body"] = body,
                ["filled"] = !string.IsNullOrWhiteSpace(body)
            });
        }
        return result;
    }

    private static string? Text(JsonNode? node)
    {
        var value = node?.ToString().Trim('"');
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static string? Join(params string?[] parts)
    {
        var filtered = parts.Where(p => !string.IsNullOrWhiteSpace(p)).ToArray();
        return filtered.Length == 0 ? null : string.Join(" ", filtered);
    }

    private static string? Schedule(JsonObject state)
    {
        var from = Text(state["period"]?["from"]);
        var to = Text(state["period"]?["to"]);
        if (from is null || to is null) return null;

        var days = DateOnly.TryParse(from, out var f) && DateOnly.TryParse(to, out var t)
            ? t.DayNumber - f.DayNumber + 1
            : 0;
        return $"Начало работ: {Format(from)}. Окончание работ: {Format(to)}. " +
               (days > 0 ? $"Общая продолжительность: {days} календарных дней." : "");
    }

    private static string Format(string iso) =>
        DateOnly.TryParse(iso, out var date) ? date.ToString("dd.MM.yyyy") : iso;

    private static string? Content(JsonObject state)
    {
        if (state["stages"] is not JsonArray stages || stages.Count == 0) return null;

        var sb = new StringBuilder();
        var index = 1;
        foreach (var node in stages)
        {
            if (node is not JsonObject stage) continue;
            sb.Append($"Этап {index}. {stage["name"]?.GetValue<string>()}");
            if (stage["days"] is JsonValue value && value.TryGetValue<int>(out var days) && days > 0)
                sb.Append($" Продолжительность по типовому расчёту: {days} дн.");
            if (Text(stage["documentation"]) is { } docs)
                sb.Append($" Отчётные материалы: {docs}.");
            sb.AppendLine();
            index++;
        }
        return sb.ToString().TrimEnd();
    }

    private static string? Flags(JsonObject state)
    {
        if (state["flags"] is not JsonObject flags) return null;
        var parts = new List<string>();
        if (flags["model3d"]?.GetValue<bool>() == true)
            parts.Add("В составе работ предусмотрено построение трёхмерной геологической модели.");
        if (flags["urgent"]?.GetValue<bool>() == true)
            parts.Add("Работы выполняются в сокращённые сроки.");
        return parts.Count == 0 ? null : string.Join(" ", parts);
    }

    private static string? Executors(JsonObject state)
    {
        if (state["executors"] is not JsonArray executors || executors.Count == 0) return null;

        var names = executors
            .Select(e => e?["name"]?.GetValue<string>() ?? "")
            .Where(n => n.Length > 0)
            .ToList();
        var sub = executors.Any(e => e?["subcontract"]?.GetValue<bool>() == true);

        var text = $"Исполнители работ: {string.Join(", ", names)}.";
        if (sub)
            text += " Часть работ выполняется на условиях субподряда; привлечение субподрядчиков " +
                    "согласуется с заказчиком в письменном виде.";
        return text;
    }
}
