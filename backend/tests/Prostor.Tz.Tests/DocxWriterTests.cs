using System.IO.Compression;
using System.Text;
using FluentAssertions;
using Prostor.Tz;
using Xunit;

namespace Prostor.Tz.Tests;

/// <summary>
/// Сборка .docx напрямую в WordprocessingML.
/// DocxWriter.Build возвращает zip-архив с тремя обязательными частями
/// (Content Types, rels, document.xml) + styles.xml. Проверяем структуру
/// архива и ключевые элементы разметки — без открытия в Word, только
/// детерминированные строковые/структурные ассерты.
/// </summary>
public class DocxWriterTests
{
    private static (Draft draft, byte[] bytes) BuildDoc(JsonObject state)
    {
        var draft = Drafting.Build(Fixtures.GenericTemplate(), state, typicalDays: 30);
        var bytes = DocxWriter.Build(draft, state);
        return (draft, bytes);
    }

    private static Dictionary<string, string> Unzip(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes);
        using var archive = new ZipArchive(ms, ZipArchiveMode.Read);
        return archive.Entries.ToDictionary(
            e => e.FullName,
            e =>
            {
                using var es = e.Open();
                using var reader = new StreamReader(es, Encoding.UTF8);
                return reader.ReadToEnd();
            });
    }

    [Fact]
    public void docx_contains_mandatory_zip_parts()
    {
        var (_, bytes) = BuildDoc(Fixtures.FilledState());
        var parts = Unzip(bytes);

        parts.Should().ContainKey("[Content_Types].xml");
        parts.Should().ContainKey("_rels/.rels");
        parts.Should().ContainKey("word/_rels/document.xml.rels");
        parts.Should().ContainKey("word/document.xml");
        parts.Should().ContainKey("word/styles.xml");
    }

    [Fact]
    public void document_xml_is_well_formed_wordprocessingml()
    {
        var (_, bytes) = BuildDoc(Fixtures.FilledState());
        var doc = Unzip(bytes)["word/document.xml"];

        doc.Should().StartWith("<?xml version=\"1.0\"");
        doc.Should().Contain("xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"");
        doc.Should().Contain("<w:document");
        doc.Should().Contain("<w:body>");
        doc.Should().Contain("<w:sectPr");
    }

    [Fact]
    public void header_has_appendix_number_and_contract_lines()
    {
        var (_, bytes) = BuildDoc(Fixtures.FilledState());
        var doc = Unzip(bytes)["word/document.xml"];

        doc.Should().Contain("Приложение №1");
        doc.Should().Contain("к Заказу");
        doc.Should().Contain("к Договору");
    }

    [Fact]
    public void title_contains_technical_zadanie_and_theme()
    {
        var state = Fixtures.FilledState();
        var (_, bytes) = BuildDoc(state);
        var doc = Unzip(bytes)["word/document.xml"];

        doc.Should().Contain("Техническое задание");
        // theme = object || productName; object задан → «Месторождение Северное»
        doc.Should().Contain("Месторождение Северное");
    }

    [Fact]
    public void theme_falls_back_to_product_name_when_object_absent()
    {
        var state = Fixtures.J("""
            {"productId":"p-1","productName":"Оценка запасов",
             "period":{"from":"2025-01-01","to":"2025-03-31"},
             "stages":[{"name":"Этап"}],"executors":[{"id":"c-1","name":"ООО"}]}
            """);
        var (_, bytes) = BuildDoc(state);
        var doc = Unzip(bytes)["word/document.xml"];

        doc.Should().Contain("Оценка запасов");
    }

    [Fact]
    public void customer_placeholder_when_not_set()
    {
        var (_, bytes) = BuildDoc(Fixtures.EmptyState());
        var doc = Unzip(bytes)["word/document.xml"];

        doc.Should().Contain("{Полное-Наименование-ДО-Заказчика}");
    }

    [Fact]
    public void customer_value_when_set()
    {
        var state = Fixtures.FilledState();
        state["customer"] = "ООР Нефть";
        var (_, bytes) = BuildDoc(state);
        var doc = Unzip(bytes)["word/document.xml"];

        doc.Should().Contain("ООР Нефть");
    }

    [Fact]
    public void filled_sections_render_as_headings_with_index()
    {
        var (_, bytes) = BuildDoc(Fixtures.FilledState());
        var doc = Unzip(bytes)["word/document.xml"];

        doc.Should().Contain("Heading2");
        // Нумерация разделов: «1. Предмет ТЗ», «2. Сроки выполнения» и т.д.
        doc.Should().Contain("1. Предмет ТЗ");
        doc.Should().Contain("2. Сроки выполнения");
    }

    [Fact]
    public void required_empty_section_shows_underscore_placeholder()
    {
        // Раздел с required=true, но body пуст → прочерки-плейсхолдер
        var (_, bytes) = BuildDoc(Fixtures.EmptyState());
        var doc = Unzip(bytes)["word/document.xml"];

        doc.Should().Contain("________");
    }

    [Fact]
    public void signature_table_present_with_role_headers()
    {
        var (_, bytes) = BuildDoc(Fixtures.FilledState());
        var doc = Unzip(bytes)["word/document.xml"];

        doc.Should().Contain("ЗАКАЗЧИК");
        doc.Should().Contain("ИСПОЛНИТЕЛЬ");
        doc.Should().Contain("М.П.");
        doc.Should().Contain("<w:tbl>");
    }

    [Fact]
    public void signature_uses_first_executor_name()
    {
        var state = Fixtures.FilledState();
        ((JsonArray)state["executors"]!).Add(new JsonObject
        {
            ["id"] = "c-2", ["name"] = "АО Недра", ["subcontract"] = false
        });
        var (_, bytes) = BuildDoc(state);
        var doc = Unzip(bytes)["word/document.xml"];

        doc.Should().Contain("ООО Гео"); // первый исполнитель
    }

    [Fact]
    public void signature_falls_back_to_placeholder_when_no_executors()
    {
        var (_, bytes) = BuildDoc(Fixtures.EmptyState());
        var doc = Unzip(bytes)["word/document.xml"];

        doc.Should().Contain("{Наименование-Исполнителя}");
    }

    [Fact]
    public void quality_appendix_after_page_break()
    {
        var (draft, bytes) = BuildDoc(Fixtures.FilledState());
        var doc = Unzip(bytes)["word/document.xml"];

        doc.Should().Contain("<w:br w:type=\"page\"/>");
        doc.Should().Contain("Оценка качества технического задания");
        doc.Should().Contain($"Готовность к согласованию: {draft.Readiness}%");
    }

    [Fact]
    public void risks_listed_in_quality_appendix_when_present()
    {
        var draft = Drafting.Build(Fixtures.GenericTemplate(), Fixtures.EmptyState(), null);
        var bytes = DocxWriter.Build(draft, Fixtures.EmptyState());
        var doc = Unzip(bytes)["word/document.xml"];

        doc.Should().Contain("Выявленные риски:");
        doc.Should().Contain("[критично]");
        doc.Should().Contain("Не указан объект работ");
    }

    [Fact]
    public void risks_not_listed_when_absent()
    {
        var (_, bytes) = BuildDoc(Fixtures.FilledState());
        var doc = Unzip(bytes)["word/document.xml"];

        doc.Should().NotContain("Выявленные риски:");
    }

    [Fact]
    public void special_characters_are_escaped()
    {
        var state = Fixtures.FilledState();
        state["object"] = "Объект <тест> & \"данные\"";
        var (_, bytes) = BuildDoc(state);
        var doc = Unzip(bytes)["word/document.xml"];

        doc.Should().Contain("&lt;тест&gt;");
        doc.Should().Contain("&amp;");
        doc.Should().NotContain("<тест>");
    }

    [Fact]
    public void support_type_code_changes_lead_in()
    {
        var supportTemplate = new TemplateDefinition("tpl-sup", "Услуга", "SUPPORT",
            Sections: new JsonArray
            {
                new JsonObject { ["key"] = "purpose", ["title"] = "Предмет", ["required"] = true }
            },
            Fields: new JsonArray(), Risks: new JsonArray());
        var draft = Drafting.Build(supportTemplate, Fixtures.J("""{"productName":"Консультация"}"""), null);
        var bytes = DocxWriter.Build(draft, Fixtures.J("""{"productName":"Консультация"}"""));
        var doc = Unzip(bytes)["word/document.xml"];

        doc.Should().Contain("на выполнение услуг по теме:");
    }

    [Fact]
    public void works_type_code_uses_default_lead_in()
    {
        var supportTemplate = new TemplateDefinition("tpl-w", "Работа", "WORKS",
            Sections: new JsonArray
            {
                new JsonObject { ["key"] = "purpose", ["title"] = "Предмет", ["required"] = true }
            },
            Fields: new JsonArray(), Risks: new JsonArray());
        var draft = Drafting.Build(supportTemplate, Fixtures.J("""{"productName":"Бурение"}"""), null);
        var bytes = DocxWriter.Build(draft, Fixtures.J("""{"productName":"Бурение"}"""));
        var doc = Unzip(bytes)["word/document.xml"];

        doc.Should().Contain("на выполнение работ по теме:");
    }

    [Fact]
    public void produced_bytes_are_valid_zip()
    {
        var (_, bytes) = BuildDoc(Fixtures.FilledState());

        using var ms = new MemoryStream(bytes);
        using var archive = new ZipArchive(ms, ZipArchiveMode.Read);
        archive.Entries.Should().NotBeEmpty();
    }
}
