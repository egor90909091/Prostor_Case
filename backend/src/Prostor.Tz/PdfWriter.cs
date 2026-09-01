using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;

namespace Prostor.Tz;

/// <summary>
/// Шрифты для PDF. Кириллица требует встроенного TrueType — «стандартные 14»
/// шрифтов PDF её не содержат, поэтому без файла шрифта выгрузка невозможна.
///
/// В образ ставится fonts-dejavu-core (см. backend/Dockerfile); для локального
/// запуска без докера подхватываются системные шрифты macOS/Linux, а путь
/// можно задать явно через PDF_FONT_REGULAR / PDF_FONT_BOLD.
/// </summary>
public static class PdfFonts
{
    private static readonly Lazy<(TrueTypeFont Regular, TrueTypeFont Bold)?> Resolved = new(Resolve);

    public static bool Available => Resolved.Value is not null;

    public static (TrueTypeFont Regular, TrueTypeFont Bold)? Current => Resolved.Value;

    public const string MissingFontHint =
        "Для PDF нужен TrueType-шрифт с кириллицей. В докер-образе он ставится пакетом " +
        "fonts-dejavu-core; при локальном запуске укажите путь в PDF_FONT_REGULAR " +
        "(и, при желании, PDF_FONT_BOLD).";

    private static readonly string[][] Candidates =
    {
        new[]
        {
            "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf",
            "/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf"
        },
        new[]
        {
            "/usr/share/fonts/truetype/liberation/LiberationSerif-Regular.ttf",
            "/usr/share/fonts/truetype/liberation/LiberationSerif-Bold.ttf"
        },
        new[]
        {
            "/System/Library/Fonts/Supplemental/Arial.ttf",
            "/System/Library/Fonts/Supplemental/Arial Bold.ttf"
        },
        new[] { "/System/Library/Fonts/Supplemental/Arial Unicode.ttf", "" },
    };

    private static (TrueTypeFont Regular, TrueTypeFont Bold)? Resolve()
    {
        var explicitRegular = Environment.GetEnvironmentVariable("PDF_FONT_REGULAR");
        var explicitBold = Environment.GetEnvironmentVariable("PDF_FONT_BOLD");

        var pairs = new List<string[]>();
        if (!string.IsNullOrWhiteSpace(explicitRegular))
            pairs.Add(new[] { explicitRegular!, explicitBold ?? "" });
        pairs.AddRange(Candidates);

        foreach (var pair in pairs)
        {
            if (!File.Exists(pair[0])) continue;
            try
            {
                var regular = TrueTypeFont.Load(pair[0]);
                // Жирного начертания может не быть — тогда заголовки рисуются
                // обычным: это хуже на вид, но лучше, чем отказ от выгрузки.
                var bold = pair.Length > 1 && pair[1].Length > 0 && File.Exists(pair[1])
                    ? TrueTypeFont.Load(pair[1])
                    : regular;
                return (regular, bold);
            }
            catch (Exception)
            {
                // Битый или нестандартный файл — пробуем следующий кандидат.
            }
        }
        return null;
    }
}

/// <summary>
/// Сборка PDF напрямую, без внешних библиотек — по тем же соображениям, что и
/// DocxWriter: формат простой, зависимость в образе стоит дороже.
///
/// Что здесь есть: A4, поля как в бланке ТЗ, перенос по словам, выключка по
/// ширине, разрывы страниц, две колонки подписей. Текст пишется встроенным
/// TrueType-шрифтом в кодировке Identity-H (глифы напрямую), плюс ToUnicode —
/// чтобы из готового PDF можно было копировать русский текст и искать по нему.
///
/// Содержание документа берётся из TzLayout — того же источника, что и .docx,
/// поэтому форматы не могут разойтись.
/// </summary>
public static class PdfWriter
{
    private const double PageWidth = 595.28;   // A4
    private const double PageHeight = 841.89;
    private const double MarginLeft = 85.0;    // 3 см — как w:left=1701 twips в .docx
    private const double MarginRight = 42.5;   // 1.5 см
    private const double MarginTop = 56.7;     // 2 см
    private const double MarginBottom = 56.7;
    private const double BodySize = 12.0;
    private const double HeadingSize = 13.0;
    private const double LineFactor = 1.32;
    private const double ParagraphGap = 6.0;
    private const double HeadingBefore = 10.0;
    private const double HeadingAfter = 5.0;

    private static double ContentWidth => PageWidth - MarginLeft - MarginRight;

    public static byte[] Build(Draft draft, JsonObject state)
    {
        if (PdfFonts.Current is not { } fonts)
            throw new InvalidOperationException(PdfFonts.MissingFontHint);

        var renderer = new Renderer(fonts.Regular, fonts.Bold);
        renderer.Render(TzLayout.Build(draft, state));
        return renderer.Finish();
    }

    // =============================================================== разметка
    private sealed class Renderer
    {
        private readonly FontUse _regular;
        private readonly FontUse _bold;
        private readonly List<string> _pages = new();
        private StringBuilder _content = new();
        private double _y = PageHeight - MarginTop;

        public Renderer(TrueTypeFont regular, TrueTypeFont bold)
        {
            _regular = new FontUse(regular, "F1");
            _bold = new FontUse(bold, "F2");
        }

        public void Render(IEnumerable<DocBlock> blocks)
        {
            foreach (var block in blocks)
            {
                switch (block.Kind)
                {
                    case DocBlockKind.PageBreak:
                        NewPage();
                        break;

                    case DocBlockKind.Heading:
                        _y -= HeadingBefore;
                        DrawParagraph(block.Text, DocAlign.Left, _bold, HeadingSize, MarginLeft, ContentWidth);
                        _y -= HeadingAfter;
                        break;

                    case DocBlockKind.Signatures:
                        DrawSignatures(block.Text, block.Left!, block.Right!);
                        break;

                    default:
                        DrawParagraph(block.Text, block.Align, block.Bold ? _bold : _regular,
                            BodySize, MarginLeft, ContentWidth);
                        break;
                }
            }
            Flush();
        }

        /// <summary>
        /// Абзац с переносом по словам. Пустая строка — это тоже абзац: в
        /// бланке ТЗ пустые строки несут вертикальный ритм, и выбрасывать их
        /// нельзя, иначе PDF перестанет совпадать с .docx по виду.
        /// </summary>
        private void DrawParagraph(string text, DocAlign align, FontUse font, double size, double x, double width)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                Advance(size * LineFactor);
                return;
            }

            var lines = Wrap(text, font, size, width);
            for (var i = 0; i < lines.Count; i++)
            {
                var line = lines[i];
                var lineWidth = Measure(string.Join(' ', line), font, size);
                var justify = align == DocAlign.Justify && i < lines.Count - 1 && line.Count > 1;

                var offset = align switch
                {
                    DocAlign.Center => (width - lineWidth) / 2,
                    DocAlign.Right => width - lineWidth,
                    _ => 0
                };

                Advance(size * LineFactor);
                DrawLine(line, x + Math.Max(offset, 0), _y, font, size,
                    justify ? (width - lineWidth) / (line.Count - 1) : 0);
            }
            _y -= ParagraphGap;
        }

        /// <summary>
        /// Подписи сторон — две колонки рядом. Строки колонок рисуются
        /// параллельно, поэтому блок не рвётся посередине: если он не влезает
        /// в остаток страницы, целиком переносится на следующую.
        /// </summary>
        private void DrawSignatures(string title, SignatureParty left, SignatureParty right)
        {
            var column = (ContentWidth - 20) / 2;
            var leftLines = PartyLines(left, column);
            var rightLines = PartyLines(right, column);
            var rows = Math.Max(leftLines.Count, rightLines.Count);

            // Заголовок считается вместе с таблицей: блок либо целиком влезает
            // в остаток страницы, либо целиком переезжает на следующую.
            var needed = (rows + 2) * BodySize * LineFactor + ParagraphGap;
            if (_y - needed < MarginBottom) NewPage();

            if (title.Length > 0)
                DrawParagraph(title, DocAlign.Center, _bold, BodySize, MarginLeft, ContentWidth);

            for (var i = 0; i < rows; i++)
            {
                Advance(BodySize * LineFactor);
                var y = _y;
                if (i < leftLines.Count)
                    DrawLine(leftLines[i].Words, MarginLeft, y, leftLines[i].Bold ? _bold : _regular, BodySize, 0);
                if (i < rightLines.Count)
                    DrawLine(rightLines[i].Words, MarginLeft + column + 20, y,
                        rightLines[i].Bold ? _bold : _regular, BodySize, 0);
            }
            _y -= ParagraphGap;
        }

        private List<(List<string> Words, bool Bold)> PartyLines(SignatureParty party, double width)
        {
            var lines = new List<(List<string>, bool)>();
            void Add(string text, bool bold)
            {
                if (text.Length == 0)
                {
                    lines.Add((new List<string> { " " }, false));
                    return;
                }
                foreach (var line in Wrap(text, bold ? _bold : _regular, BodySize, width))
                    lines.Add((line, bold));
            }

            Add(party.Role, true);
            Add(party.Name, true);
            Add(party.Position, false);
            Add("", false);
            Add($"____________________ / {party.Signatory}", false);
            Add("М.П.", false);
            return lines;
        }

        private List<List<string>> Wrap(string text, FontUse font, double size, double width)
        {
            var lines = new List<List<string>>();
            var current = new List<string>();
            double currentWidth = 0;
            var space = Measure(" ", font, size);

            foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var wordWidth = Measure(word, font, size);
                var extra = current.Count == 0 ? 0 : space;
                if (current.Count > 0 && currentWidth + extra + wordWidth > width)
                {
                    lines.Add(current);
                    current = new List<string>();
                    currentWidth = 0;
                    extra = 0;
                }
                current.Add(word);
                currentWidth += extra + wordWidth;
            }
            if (current.Count > 0) lines.Add(current);
            if (lines.Count == 0) lines.Add(new List<string> { " " });
            return lines;
        }

        private double Measure(string text, FontUse font, double size)
        {
            double total = 0;
            foreach (var rune in text.EnumerateRunes())
                total += font.Font.Width(font.Font.Glyph(rune.Value));
            return total * size / 1000.0;
        }

        /// <summary>
        /// Строка текста. Между словами вставляется корректировка TJ, а не
        /// пробельный символ с Tw: при Identity-H (двухбайтовые коды глифов)
        /// оператор Tw не работает вовсе, поэтому выключка по ширине делается
        /// только через сдвиги в массиве TJ.
        /// </summary>
        private void DrawLine(List<string> words, double x, double y, FontUse font, double size, double extraGap)
        {
            var space = Measure(" ", font, size);
            var tj = new StringBuilder("[");
            for (var i = 0; i < words.Count; i++)
            {
                if (i > 0)
                {
                    var gap = space + extraGap;
                    tj.Append(Num(-gap / size * 1000.0)).Append(' ');
                }
                tj.Append('<').Append(font.Encode(words[i])).Append('>');
            }
            tj.Append("] TJ");

            _content.Append("BT /").Append(font.Resource).Append(' ').Append(Num(size)).Append(" Tf 1 0 0 1 ")
                    .Append(Num(x)).Append(' ').Append(Num(y)).Append(" Tm ")
                    .Append(tj).Append(" ET\n");
        }

        private void Advance(double lineHeight)
        {
            if (_y - lineHeight < MarginBottom) NewPage();
            _y -= lineHeight;
        }

        private void NewPage()
        {
            Flush();
            _content = new StringBuilder();
            _y = PageHeight - MarginTop;
        }

        private void Flush()
        {
            if (_content.Length > 0) _pages.Add(_content.ToString());
            _content = new StringBuilder();
        }

        public byte[] Finish()
        {
            Flush();
            if (_pages.Count == 0) _pages.Add("");
            return Assemble(_pages, _regular, _bold);
        }
    }

    /// <summary>Шрифт в документе: сам файл, имя ресурса и глифы, которые реально использованы.</summary>
    private sealed class FontUse
    {
        public TrueTypeFont Font { get; }
        public string Resource { get; }

        /// <summary>Глиф -> символ. Нужен и для /W (ширины), и для ToUnicode (поиск, копирование).</summary>
        public SortedDictionary<ushort, int> Used { get; } = new();

        public FontUse(TrueTypeFont font, string resource)
        {
            Font = font;
            Resource = resource;
        }

        public string Encode(string text)
        {
            var sb = new StringBuilder(text.Length * 4);
            foreach (var rune in text.EnumerateRunes())
            {
                var glyph = Font.Glyph(rune.Value);
                Used[glyph] = rune.Value;
                sb.Append(glyph.ToString("X4", CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }
    }

    // =============================================================== файл PDF
    private static byte[] Assemble(List<string> pages, FontUse regular, FontUse bold)
    {
        var objects = new List<byte[]>();
        int Add(byte[] body)
        {
            objects.Add(body);
            return objects.Count; // номера объектов начинаются с 1
        }
        int AddText(string body) => Add(Encoding.ASCII.GetBytes(body));

        // Порядок объектов: сначала резервируем номера каталога и дерева
        // страниц — на них ссылаются страницы, а на страницы ссылается дерево.
        var catalogId = AddText("");
        var pagesId = AddText("");

        var fontIds = new Dictionary<string, int>();
        foreach (var font in new[] { regular, bold })
        {
            if (fontIds.ContainsKey(font.Resource)) continue;
            fontIds[font.Resource] = AddFont(font, Add, AddText);
        }

        var resources = "<< /Font << " +
            string.Join(" ", fontIds.Select(f => $"/{f.Key} {f.Value} 0 R")) +
            " >> >>";

        var pageIds = new List<int>();
        foreach (var content in pages)
        {
            var streamId = Add(Stream(Encoding.UTF8.GetBytes(content)));
            var pageId = AddText(
                $"<< /Type /Page /Parent {pagesId} 0 R /MediaBox [0 0 {Num(PageWidth)} {Num(PageHeight)}] " +
                $"/Resources {resources} /Contents {streamId} 0 R >>");
            pageIds.Add(pageId);
        }

        objects[catalogId - 1] = Encoding.ASCII.GetBytes(
            $"<< /Type /Catalog /Pages {pagesId} 0 R >>");
        objects[pagesId - 1] = Encoding.ASCII.GetBytes(
            $"<< /Type /Pages /Count {pageIds.Count} /Kids [{string.Join(" ", pageIds.Select(id => $"{id} 0 R"))}] >>");

        return Serialize(objects, catalogId);
    }

    /// <summary>
    /// Композитный шрифт: Type0 (Identity-H) -> CIDFontType2 -> дескриптор ->
    /// встроенный файл шрифта. Кириллица иначе в PDF не живёт: у «стандартных
    /// 14» шрифтов её просто нет.
    /// </summary>
    private static int AddFont(FontUse font, Func<byte[], int> add, Func<string, int> addText)
    {
        var file = add(Stream(font.Font.Data, extra: $"/Length1 {font.Font.Data.Length}"));

        var flags = 4 | (font.Font.IsBold ? 1 << 18 : 0); // 4 = symbolic, 262144 = force bold
        var bbox = string.Join(" ", font.Font.BBox.Select(v => font.Font.Scaled(v)));
        var descriptor = addText(
            $"<< /Type /FontDescriptor /FontName /{font.Font.PostScriptName} /Flags {flags} " +
            $"/FontBBox [{bbox}] /ItalicAngle 0 /Ascent {font.Font.Scaled(font.Font.Ascent)} " +
            $"/Descent {font.Font.Scaled(font.Font.Descent)} /CapHeight {font.Font.Scaled(font.Font.Ascent)} " +
            $"/StemV 80 /FontFile2 {file} 0 R >>");

        var widths = new StringBuilder();
        foreach (var (glyph, _) in font.Used)
            widths.Append(glyph).Append(" [").Append(font.Font.Width(glyph)).Append("] ");

        var cid = addText(
            $"<< /Type /Font /Subtype /CIDFontType2 /BaseFont /{font.Font.PostScriptName} " +
            "/CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> " +
            $"/FontDescriptor {descriptor} 0 R /DW 1000 /W [{widths.ToString().TrimEnd()}] " +
            "/CIDToGIDMap /Identity >>");

        var toUnicode = add(Stream(Encoding.ASCII.GetBytes(ToUnicodeCMap(font))));

        return addText(
            $"<< /Type /Font /Subtype /Type0 /BaseFont /{font.Font.PostScriptName} " +
            $"/Encoding /Identity-H /DescendantFonts [{cid} 0 R] /ToUnicode {toUnicode} 0 R >>");
    }

    /// <summary>
    /// Обратная таблица «глиф -> символ». Без неё PDF выглядит правильно, но
    /// текст из него копируется и ищется как мусор — для документа, который
    /// уходит контрагенту, это неприемлемо.
    /// </summary>
    private static string ToUnicodeCMap(FontUse font)
    {
        var sb = new StringBuilder();
        sb.Append("/CIDInit /ProcSet findresource begin\n12 dict begin\nbegincmap\n");
        sb.Append("/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def\n");
        sb.Append("/CMapName /Adobe-Identity-UCS def\n/CMapType 2 def\n");
        sb.Append("1 begincodespacerange\n<0000> <FFFF>\nendcodespacerange\n");

        // Ограничение формата: не больше 100 пар в одной секции bfchar.
        var entries = font.Used.Where(e => e.Key != 0).ToList();
        for (var i = 0; i < entries.Count; i += 100)
        {
            var chunk = entries.Skip(i).Take(100).ToList();
            sb.Append(chunk.Count).Append(" beginbfchar\n");
            foreach (var (glyph, codepoint) in chunk)
                sb.Append('<').Append(glyph.ToString("X4")).Append("> <")
                  .Append(codepoint.ToString("X4")).Append(">\n");
            sb.Append("endbfchar\n");
        }

        sb.Append("endcmap\nCMapName currentdict /CMap defineresource pop\nend\nend");
        return sb.ToString();
    }

    /// <summary>Поток со сжатием: и содержимое страниц, и файл шрифта ужимаются вдвое-втрое.</summary>
    private static byte[] Stream(byte[] payload, string extra = "")
    {
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            zlib.Write(payload, 0, payload.Length);
        var body = compressed.ToArray();

        var header = Encoding.ASCII.GetBytes(
            $"<< /Length {body.Length} /Filter /FlateDecode {extra}>>\nstream\n");
        var footer = Encoding.ASCII.GetBytes("\nendstream");

        var result = new byte[header.Length + body.Length + footer.Length];
        header.CopyTo(result, 0);
        body.CopyTo(result, header.Length);
        footer.CopyTo(result, header.Length + body.Length);
        return result;
    }

    private static byte[] Serialize(List<byte[]> objects, int rootId)
    {
        using var output = new MemoryStream();
        void Write(string text) => output.Write(Encoding.ASCII.GetBytes(text));

        Write("%PDF-1.7\n%âãÏÓ\n");

        var offsets = new int[objects.Count + 1];
        for (var i = 0; i < objects.Count; i++)
        {
            offsets[i + 1] = (int)output.Length;
            Write($"{i + 1} 0 obj\n");
            output.Write(objects[i]);
            Write("\nendobj\n");
        }

        var xref = (int)output.Length;
        Write($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        for (var i = 1; i <= objects.Count; i++)
            Write($"{offsets[i]:D10} 00000 n \n");

        Write($"trailer\n<< /Size {objects.Count + 1} /Root {rootId} 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return output.ToArray();
    }

    private static string Num(double value) =>
        Math.Round(value, 2).ToString("0.##", CultureInfo.InvariantCulture);
}
