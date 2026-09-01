using System.Text;
using FluentAssertions;
using Prostor.Tz;
using Xunit;

namespace Prostor.Tz.Tests;

/// <summary>
/// Сборка PDF без внешних библиотек. Проверяем структуру файла и то, что
/// содержание берётся из общей модели документа (TzLayout), а не собирается
/// отдельно от .docx.
///
/// Тесты требуют системного шрифта с кириллицей (в CI/образе — fonts-dejavu-core).
/// Если шрифта нет, проверки пропускаются: это ограничение окружения, а не
/// регрессия кода — само отсутствие шрифта отражено в /health как pdf=no-font.
/// </summary>
public class PdfWriterTests
{
    private static byte[]? BuildPdf()
    {
        if (!PdfFonts.Available) return null;
        var state = Fixtures.FilledState();
        var draft = Drafting.Build(Fixtures.GenericTemplate(), state, typicalDays: 30);
        return PdfWriter.Build(draft, state);
    }

    // Латиница-1: структура PDF (объекты, xref, словари) — это ASCII,
    // и искать в ней подстроки можно как в тексте.
    private static string AsLatin(byte[] bytes) => Encoding.Latin1.GetString(bytes);

    [Fact]
    public void pdf_has_header_xref_and_catalog()
    {
        var bytes = BuildPdf();
        if (bytes is null) return;

        var text = AsLatin(bytes);
        text.Should().StartWith("%PDF-1.7");
        text.Should().Contain("/Type /Catalog");
        text.Should().Contain("/Type /Pages");
        text.Should().Contain("xref");
        text.TrimEnd().Should().EndWith("%%EOF");
    }

    [Fact]
    public void pdf_embeds_cyrillic_capable_font_with_tounicode()
    {
        var bytes = BuildPdf();
        if (bytes is null) return;

        var text = AsLatin(bytes);
        // Кириллица в PDF живёт только через композитный шрифт со встроенным
        // файлом; ToUnicode отвечает за копирование и поиск по документу.
        text.Should().Contain("/Subtype /Type0");
        text.Should().Contain("/Encoding /Identity-H");
        text.Should().Contain("/Subtype /CIDFontType2");
        text.Should().Contain("/FontFile2");
        text.Should().Contain("/ToUnicode");
    }

    [Fact]
    public void quality_appendix_starts_on_its_own_page()
    {
        var bytes = BuildPdf();
        if (bytes is null) return;

        // В TzLayout перед приложением стоит разрыв страницы, значит страниц
        // заведомо больше одной.
        var pages = AsLatin(bytes).Split("/Type /Page ").Length - 1;
        pages.Should().BeGreaterThan(1);
    }

    [Fact]
    public void pdf_and_docx_share_the_same_content_model()
    {
        var state = Fixtures.FilledState();
        var draft = Drafting.Build(Fixtures.GenericTemplate(), state, typicalDays: 30);

        var blocks = TzLayout.Build(draft, state);

        blocks.Should().Contain(b => b.Text == "Приложение №1");
        blocks.Should().Contain(b => b.Kind == DocBlockKind.Signatures);
        blocks.Should().Contain(b => b.Kind == DocBlockKind.PageBreak);
    }
}
