using System.IO.Compression;
using System.Security;
using System.Text;
using System.Text.Json.Nodes;

namespace Prostor.Tz;

/// <summary>
/// Сборка .docx напрямую в WordprocessingML.
///
/// Внешняя библиотека здесь не нужна: docx — это zip с тремя обязательными
/// частями. Меньше зависимостей в образе, детерминированный результат
/// и полный контроль над разметкой шаблона ТЗ.
/// </summary>
public static class DocxWriter
{
    private const string MainNs = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private const string RelNs = "http://schemas.openxmlformats.org/package/2006/relationships";
    private const string DocRelNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    public static byte[] Build(Draft draft, JsonObject state)
    {
        var body = new StringBuilder();

        body.Append(Heading("ТЕХНИЧЕСКОЕ ЗАДАНИЕ", 1, center: true));
        body.Append(Paragraph(draft.TemplateName, center: true, bold: true));

        var product = Value(state["productName"]) ?? "—";
        body.Append(Paragraph($"Услуга: {product}", center: true));

        if (Value(state["object"]) is { } obj)
            body.Append(Paragraph($"Объект работ: {obj}", center: true));

        body.Append(Paragraph($"Заказчик: {Value(state["customer"]) ?? "{Полное-Наименование-ДО-Заказчика}"}"));
        body.Append(Paragraph(""));

        var index = 1;
        foreach (var node in draft.Sections)
        {
            if (node is not JsonObject section) continue;
            var title = section["title"]?.GetValue<string>() ?? "";
            var text = section["body"]?.GetValue<string>();
            var required = section["required"]?.GetValue<bool>() ?? false;

            if (string.IsNullOrWhiteSpace(text) && !required) continue;

            body.Append(Heading($"{index}. {title}", 2));
            if (string.IsNullOrWhiteSpace(text))
                body.Append(Paragraph("__________________________________________", italic: true));
            else
                foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                    body.Append(Paragraph(line.Trim()));

            index++;
        }

        // Служебный лист качества ТЗ — то, чего нет в бумажном шаблоне,
        // но что делает документ проверяемым объектом, а не файлом.
        body.Append(Heading("Приложение. Оценка качества технического задания", 2));
        body.Append(Paragraph($"Готовность к согласованию: {draft.Readiness}%.", bold: true));
        body.Append(Paragraph(draft.Recommendation));

        if (draft.Risks.Count > 0)
        {
            body.Append(Paragraph("Выявленные риски:", bold: true));
            foreach (var risk in draft.Risks)
                body.Append(Paragraph($"— [{SeverityRu(risk.Severity)}] {risk.Title}. {risk.Recommendation}"));
        }

        body.Append(Paragraph(""));
        body.Append(Paragraph("ПОДПИСИ СТОРОН:", bold: true));
        body.Append(Paragraph("Заказчик: ______________________        Исполнитель: ______________________"));

        body.Append("<w:sectPr><w:pgSz w:w=\"11906\" w:h=\"16838\"/>" +
                    "<w:pgMar w:top=\"1134\" w:right=\"850\" w:bottom=\"1134\" w:left=\"1701\" " +
                    "w:header=\"708\" w:footer=\"708\" w:gutter=\"0\"/></w:sectPr>");

        var document =
            $"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><w:document xmlns:w="{MainNs}"><w:body>{body}</w:body></w:document>""";

        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            Write(archive, "[Content_Types].xml", ContentTypes);
            Write(archive, "_rels/.rels", RootRels);
            Write(archive, "word/_rels/document.xml.rels", DocumentRels);
            Write(archive, "word/styles.xml", Styles);
            Write(archive, "word/document.xml", document);
        }
        return buffer.ToArray();
    }

    private static string SeverityRu(string severity) => severity switch
    {
        "blocking" => "критично",
        "warning" => "внимание",
        _ => "информация"
    };

    private static void Write(ZipArchive archive, string path, string content)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.Optimal);
        using var stream = entry.Open();
        var bytes = Encoding.UTF8.GetBytes(content);
        stream.Write(bytes, 0, bytes.Length);
    }

    private static string Heading(string text, int level, bool center = false)
    {
        var alignment = center ? "<w:jc w:val=\"center\"/>" : "";
        return $"<w:p><w:pPr><w:pStyle w:val=\"Heading{level}\"/>{alignment}</w:pPr>" +
               $"<w:r><w:t xml:space=\"preserve\">{Escape(text)}</w:t></w:r></w:p>";
    }

    private static string Paragraph(string text, bool center = false, bool bold = false, bool italic = false)
    {
        var alignment = center ? "<w:jc w:val=\"center\"/>" : "<w:jc w:val=\"both\"/>";
        var runProps = (bold ? "<w:b/>" : "") + (italic ? "<w:i/>" : "");
        var properties = runProps.Length > 0 ? $"<w:rPr>{runProps}</w:rPr>" : "";
        return $"<w:p><w:pPr>{alignment}<w:spacing w:after=\"120\"/></w:pPr>" +
               $"<w:r>{properties}<w:t xml:space=\"preserve\">{Escape(text)}</w:t></w:r></w:p>";
    }

    private static string Escape(string? text) => SecurityElement.Escape(text ?? "") ?? "";

    /// <summary>Значение поля состояния как текст; пустое и отсутствующее — одно и то же.</summary>
    private static string? Value(JsonNode? node)
    {
        var text = node?.ToString().Trim('"');
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private const string ContentTypes = $"""
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
        <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
        <Default Extension="xml" ContentType="application/xml"/>
        <Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/>
        <Override PartName="/word/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml"/>
        </Types>
        """;

    private const string RootRels = $"""
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <Relationships xmlns="{RelNs}">
        <Relationship Id="rId1" Type="{DocRelNs}/officeDocument" Target="word/document.xml"/>
        </Relationships>
        """;

    private const string DocumentRels = $"""
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <Relationships xmlns="{RelNs}">
        <Relationship Id="rId1" Type="{DocRelNs}/styles" Target="styles.xml"/>
        </Relationships>
        """;

    private const string Styles = $"""
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <w:styles xmlns:w="{MainNs}">
        <w:docDefaults><w:rPrDefault><w:rPr>
          <w:rFonts w:ascii="Times New Roman" w:hAnsi="Times New Roman" w:cs="Times New Roman"/>
          <w:sz w:val="24"/><w:szCs w:val="24"/><w:lang w:val="ru-RU"/>
        </w:rPr></w:rPrDefault></w:docDefaults>
        <w:style w:type="paragraph" w:default="1" w:styleId="Normal">
          <w:name w:val="Normal"/><w:qFormat/>
        </w:style>
        <w:style w:type="paragraph" w:styleId="Heading1">
          <w:name w:val="heading 1"/><w:basedOn w:val="Normal"/><w:qFormat/>
          <w:pPr><w:spacing w:before="240" w:after="120"/><w:outlineLvl w:val="0"/></w:pPr>
          <w:rPr><w:b/><w:sz w:val="32"/></w:rPr>
        </w:style>
        <w:style w:type="paragraph" w:styleId="Heading2">
          <w:name w:val="heading 2"/><w:basedOn w:val="Normal"/><w:qFormat/>
          <w:pPr><w:spacing w:before="200" w:after="100"/><w:outlineLvl w:val="1"/></w:pPr>
          <w:rPr><w:b/><w:sz w:val="26"/></w:rPr>
        </w:style>
        </w:styles>
        """;
}
