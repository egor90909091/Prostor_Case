using System.Text.Json.Nodes;

namespace Prostor.Tz;

public enum DocAlign { Justify, Left, Center, Right }

public enum DocBlockKind { Paragraph, Heading, PageBreak, Signatures }

/// <summary>
/// Один элемент документа ТЗ. Общий язык для всех форматов выгрузки: и .docx,
/// и .pdf собираются из одного и того же списка блоков, поэтому содержимое
/// физически не может разойтись между форматами.
/// </summary>
public sealed record DocBlock(
    DocBlockKind Kind,
    string Text = "",
    DocAlign Align = DocAlign.Justify,
    bool Bold = false,
    bool Italic = false,
    int Level = 0,
    SignatureParty? Left = null,
    SignatureParty? Right = null)
{
    public static DocBlock P(string text, DocAlign align = DocAlign.Justify, bool bold = false, bool italic = false) =>
        new(DocBlockKind.Paragraph, text, align, bold, italic);

    public static DocBlock H(string text, int level) => new(DocBlockKind.Heading, text, Level: level);

    public static readonly DocBlock Break = new(DocBlockKind.PageBreak);
}

/// <summary>
/// Сторона в блоке подписей. Должность и ФИО подписанта система не собирает —
/// это плейсхолдеры под заполнение от руки, как в бумажном бланке.
/// </summary>
public sealed record SignatureParty(string Role, string Name, string Position, string Signatory);

/// <summary>
/// Содержание документа ТЗ: шапка бланка, титул, разделы шаблона, подписи и
/// служебное приложение с оценкой качества.
///
/// Вынесено из DocxWriter, когда появилась выгрузка в PDF: раньше структура
/// документа была вплавлена в генерацию WordprocessingML, и второй формат
/// означал бы второй экземпляр той же логики — с гарантией разъехаться.
/// </summary>
public static class TzLayout
{
    public static List<DocBlock> Build(Draft draft, JsonObject state)
    {
        var blocks = new List<DocBlock>();

        // Шапка «Приложение к Заказу/Договору» — как в реальных бланках
        // компании: номер и дата проставляются от руки при подписании,
        // поэтому здесь всегда прочерк, а не выдуманные значения.
        blocks.Add(DocBlock.P("Приложение №1", DocAlign.Right));
        blocks.Add(DocBlock.P("к Заказу № _________ от «____» ________ 20__ г.", DocAlign.Right));
        blocks.Add(DocBlock.P("к Договору № _________ от «____» ________ 20__ г.", DocAlign.Right));
        blocks.Add(DocBlock.P(""));

        var product = Value(state["productName"]) ?? draft.TemplateName;
        var theme = Value(state["object"]) ?? product;

        blocks.Add(DocBlock.P("Техническое задание", DocAlign.Center, bold: true));
        blocks.Add(DocBlock.P(ThemeLeadIn(draft.TypeCode), DocAlign.Center));
        blocks.Add(DocBlock.P($"«{theme}»", DocAlign.Center, bold: true));
        if (Value(state["object"]) is not null)
            blocks.Add(DocBlock.P($"(продукт: {product})", DocAlign.Center));
        blocks.Add(DocBlock.P(""));

        blocks.Add(DocBlock.P(
            $"Заказчик: {Value(state["customer"]) ?? "{Полное-Наименование-ДО-Заказчика}"}", bold: true));
        blocks.Add(DocBlock.P(""));

        var index = 1;
        foreach (var node in draft.Sections)
        {
            if (node is not JsonObject section) continue;
            var title = section["title"]?.GetValue<string>() ?? "";
            var text = section["body"]?.GetValue<string>();
            var required = section["required"]?.GetValue<bool>() ?? false;

            if (string.IsNullOrWhiteSpace(text) && !required) continue;

            blocks.Add(DocBlock.H($"{index}. {title}", 2));
            if (string.IsNullOrWhiteSpace(text))
                blocks.Add(DocBlock.P("__________________________________________", italic: true));
            else
                foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                    blocks.Add(DocBlock.P(line.Trim()));

            index++;
        }

        blocks.Add(DocBlock.P(""));
        // Заголовок — часть блока подписей, а не отдельный абзац: иначе при
        // переносе таблицы на следующую страницу он остаётся сиротой внизу.
        blocks.Add(Signatures(state));

        // Служебный лист качества ТЗ — то, чего нет в бумажном шаблоне,
        // но что делает документ проверяемым объектом, а не файлом.
        // Явно вынесен на отдельную страницу после подписей, чтобы
        // основной текст документа один в один повторял бумажный бланк.
        blocks.Add(DocBlock.Break);
        blocks.Add(DocBlock.H("Приложение. Оценка качества технического задания", 2));
        blocks.Add(DocBlock.P($"Готовность к согласованию: {draft.Readiness}%.", bold: true));
        blocks.Add(DocBlock.P(draft.Recommendation));

        if (draft.Risks.Count > 0)
        {
            blocks.Add(DocBlock.P("Выявленные риски:", bold: true));
            foreach (var risk in draft.Risks)
                blocks.Add(DocBlock.P($"— [{SeverityRu(risk.Severity)}] {risk.Title}. {risk.Recommendation}"));
        }

        return blocks;
    }

    private static DocBlock Signatures(JsonObject state)
    {
        var customer = Value(state["customer"]) ?? "{Наименование-Заказчика}";
        var executor = (state["executors"] as JsonArray)?
            .Select(e => Value(e?["name"]))
            .FirstOrDefault(n => n is not null) ?? "{Наименование-Исполнителя}";

        return new DocBlock(
            DocBlockKind.Signatures,
            "ПОДПИСИ СТОРОН:",
            Left: new SignatureParty("ЗАКАЗЧИК", customer,
                "{Должность-Подписанта-Заказчика}", "{ФИО-Подписанта-Заказчика}"),
            Right: new SignatureParty("ИСПОЛНИТЕЛЬ", executor,
                "{Должность-Подписанта-Исполнителя}", "{ФИО-Подписанта-Исполнителя}"));
    }

    public static string SeverityRu(string severity) => severity switch
    {
        "blocking" => "критично",
        "warning" => "внимание",
        _ => "информация"
    };

    /// <summary>Формулировка предмета ТЗ зависит от вида работ — так же, как в бумажных бланках.</summary>
    private static string ThemeLeadIn(string typeCode) => typeCode switch
    {
        "SUPPORT" => "на выполнение услуг по теме:",
        _ => "на выполнение работ по теме:"
    };

    /// <summary>Значение поля состояния как текст; пустое и отсутствующее — одно и то же.</summary>
    public static string? Value(JsonNode? node)
    {
        var text = node?.ToString().Trim('"');
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }
}
