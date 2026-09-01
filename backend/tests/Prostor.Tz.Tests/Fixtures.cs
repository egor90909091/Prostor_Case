using System.Text.Json.Nodes;
using Prostor.Tz;

namespace Prostor.Tz.Tests;

/// <summary>
/// Конструкторы тестовых шаблонов ТЗ и состояний диалога.
/// Логика правил лежит в tz.template (required_fields + risk_rules) и
/// интерпретируется Drafting — поэтому здесь собираем те же структуры,
/// что схема БД подаёт в сервис.
/// </summary>
internal static class Fixtures
{
    /// <summary>Шаблон с 4 полями суммарным весом 100 и тремя типами рисков.</summary>
    public static TemplateDefinition GenericTemplate() => new(
        "tpl-generic", "Типовое ТЗ", "WORKS",
        Sections: new JsonArray
        {
            new JsonObject { ["key"] = "purpose", ["title"] = "Предмет ТЗ", ["required"] = true },
            new JsonObject { ["key"] = "schedule", ["title"] = "Сроки выполнения", ["required"] = true },
            new JsonObject { ["key"] = "content", ["title"] = "Состав работ", ["required"] = true },
            new JsonObject { ["key"] = "subcontract", ["title"] = "Исполнители", ["required"] = false },
            new JsonObject { ["key"] = "documentation", ["title"] = "Отчётность", ["required"] = false },
            // Раздел, на который ссылается поле object ниже: без него шаблон
            // внутренне противоречив — поле есть, а раздела для него нет.
            // В конце списка, чтобы не сдвигать нумерацию разделов в тестах docx.
            new JsonObject { ["key"] = "perimeter", ["title"] = "Периметр работ", ["required"] = false }
        },
        Fields: new JsonArray
        {
            new JsonObject { ["key"] = "productId", ["section"] = "purpose", ["title"] = "Услуга", ["weight"] = 20 },
            new JsonObject { ["key"] = "period", ["section"] = "schedule", ["title"] = "Период", ["weight"] = 20, ["blocking"] = true },
            new JsonObject { ["key"] = "stages", ["section"] = "content", ["title"] = "Этапы", ["weight"] = 30 },
            new JsonObject { ["key"] = "executors", ["section"] = "subcontract", ["title"] = "Исполнители", ["weight"] = 15 },
            new JsonObject { ["key"] = "object", ["section"] = "perimeter", ["title"] = "Объект работ", ["weight"] = 15 }
        },
        Risks: new JsonArray
        {
            new JsonObject
            {
                ["code"] = "no_object", ["severity"] = "blocking",
                ["title"] = "Не указан объект работ",
                ["recommendation"] = "Укажите объект работ.",
                ["when"] = new JsonObject { ["op"] = "empty", ["arg"] = "object" }
            },
            new JsonObject
            {
                ["code"] = "model3d_no_source", ["severity"] = "warning",
                ["title"] = "Выбрана 3D-модель без этапа подготовки исходных данных",
                ["recommendation"] = "Добавьте этап подготовки исходных данных.",
                ["when"] = new JsonObject
                {
                    ["op"] = "and",
                    ["args"] = new JsonArray
                    {
                        new JsonObject { ["op"] = "flag", ["arg"] = "model3d" },
                        new JsonObject { ["op"] = "missing_stage", ["arg"] = "исходн" }
                    }
                }
            },
            new JsonObject
            {
                ["code"] = "short_period", ["severity"] = "warning",
                ["title"] = "Срок меньше типового",
                ["recommendation"] = "Проверьте длительность периода.",
                ["when"] = new JsonObject { ["op"] = "duration_below_typical", ["arg"] = "0.8" }
            }
        });

    /// <summary>Пустое состояние диалога.</summary>
    public static JsonObject EmptyState() => new();

    /// <summary>Полностью заполненное состояние — готовность 100%, рисков нет.</summary>
    public static JsonObject FilledState()
    {
        var state = new JsonObject
        {
            ["productId"] = "p-1",
            ["productName"] = "Оценка запасов",
            ["object"] = "Месторождение Северное",
            ["period"] = new JsonObject { ["from"] = "2025-01-01", ["to"] = "2025-03-31" },
            ["stages"] = new JsonArray
            {
                new JsonObject { ["name"] = "Подготовка исходных данных", ["days"] = 10, ["documentation"] = "Отчёт" },
                new JsonObject { ["name"] = "Моделирование", ["days"] = 50 }
            },
            ["executors"] = new JsonArray
            {
                new JsonObject { ["id"] = "c-1", ["name"] = "ООО Гео", ["subcontract"] = false }
            },
            ["flags"] = new JsonObject { ["model3d"] = false }
        };
        return state;
    }

    /// <summary>Парсит JSON-строку в JsonObject (для компактных фикстур в тестах DSL).</summary>
    public static JsonObject J(string json) =>
        JsonNode.Parse(json)?.AsObject() ?? new JsonObject();

    /// <summary>Собирает TemplateDefinition только из risk_rules — для изоляции DSL.</summary>
    public static TemplateDefinition TemplateWithRisks(params string[] riskJsons)
    {
        var risks = new JsonArray();
        foreach (var json in riskJsons)
            risks.Add(JsonNode.Parse(json)!.AsObject());
        return new TemplateDefinition("tpl-test", "Тест", "WORKS",
            Sections: new JsonArray(),
            Fields: new JsonArray(),
            Risks: risks);
    }
}
