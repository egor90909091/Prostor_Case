using System.Buffers.Binary;
using System.Text;

namespace Prostor.Tz;

/// <summary>
/// Минимальный разбор TrueType-шрифта: ровно то, что нужно PDF-writer'у —
/// таблица символов (unicode -> глиф), ширины глифов и метрики для
/// FontDescriptor. Сам файл шрифта встраивается в PDF целиком.
///
/// Своя реализация вместо библиотеки — по той же причине, что и DocxWriter:
/// это несколько таблиц с фиксированным бинарным форматом, а лишняя
/// зависимость в образе стоит дороже, чем 150 строк разбора.
/// </summary>
public sealed class TrueTypeFont
{
    public byte[] Data { get; }
    public string PostScriptName { get; }
    public ushort UnitsPerEm { get; }
    public short Ascent { get; }
    public short Descent { get; }
    public short[] BBox { get; }
    public bool IsBold { get; }

    private readonly ushort[] _advanceWidths;
    private readonly Dictionary<int, ushort> _cmap;

    private TrueTypeFont(byte[] data, string name, ushort unitsPerEm, short ascent, short descent,
                         short[] bbox, ushort[] advanceWidths, Dictionary<int, ushort> cmap, bool isBold)
    {
        Data = data;
        PostScriptName = name;
        UnitsPerEm = unitsPerEm == 0 ? (ushort)1000 : unitsPerEm;
        Ascent = ascent;
        Descent = descent;
        BBox = bbox;
        _advanceWidths = advanceWidths;
        _cmap = cmap;
        IsBold = isBold;
    }

    /// <summary>Глиф для символа; для отсутствующего — .notdef (0).</summary>
    public ushort Glyph(int codepoint) => _cmap.TryGetValue(codepoint, out var gid) ? gid : (ushort)0;

    /// <summary>Ширина глифа в тысячных долях em — единица PDF.</summary>
    public int Width(ushort glyph)
    {
        if (_advanceWidths.Length == 0) return 500;
        var raw = glyph < _advanceWidths.Length ? _advanceWidths[glyph] : _advanceWidths[^1];
        return (int)Math.Round(raw * 1000.0 / UnitsPerEm);
    }

    /// <summary>Метрики в тысячных долях em.</summary>
    public int Scaled(short value) => (int)Math.Round(value * 1000.0 / UnitsPerEm);

    public static TrueTypeFont Load(string path)
    {
        var data = File.ReadAllBytes(path);
        var tables = ReadTableDirectory(data);

        var head = Offset(tables, "head");
        var unitsPerEm = U16(data, head + 18);
        var bbox = new[]
        {
            S16(data, head + 36), S16(data, head + 38),
            S16(data, head + 40), S16(data, head + 42)
        };
        var macStyle = U16(data, head + 44);

        var hhea = Offset(tables, "hhea");
        var ascent = S16(data, hhea + 4);
        var descent = S16(data, hhea + 6);
        var numberOfHMetrics = U16(data, hhea + 34);

        var maxp = Offset(tables, "maxp");
        var numGlyphs = U16(data, maxp + 4);

        var hmtx = Offset(tables, "hmtx");
        var widths = new ushort[numGlyphs];
        ushort last = 0;
        for (var i = 0; i < numGlyphs; i++)
        {
            if (i < numberOfHMetrics) last = U16(data, hmtx + i * 4);
            widths[i] = last;
        }

        var cmap = ReadCmap(data, Offset(tables, "cmap"));
        var name = Path.GetFileNameWithoutExtension(path).Replace(" ", "");

        return new TrueTypeFont(data, name, unitsPerEm, ascent, descent, bbox, widths, cmap,
            isBold: (macStyle & 1) != 0);
    }

    private static Dictionary<string, int> ReadTableDirectory(byte[] data)
    {
        var count = U16(data, 4);
        var tables = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < count; i++)
        {
            var record = 12 + i * 16;
            var tag = Encoding.ASCII.GetString(data, record, 4);
            tables[tag] = (int)U32(data, record + 8);
        }
        return tables;
    }

    private static int Offset(Dictionary<string, int> tables, string tag) =>
        tables.TryGetValue(tag, out var offset)
            ? offset
            : throw new InvalidDataException($"в шрифте нет обязательной таблицы '{tag}'");

    /// <summary>
    /// Из всех подтаблиц cmap берём unicode-BMP формата 4 — этого достаточно
    /// для русского и латиницы. Формат 12 (за пределами BMP) не нужен: в ТЗ
    /// не встречаются символы вне базовой плоскости.
    /// </summary>
    private static Dictionary<int, ushort> ReadCmap(byte[] data, int cmap)
    {
        var count = U16(data, cmap + 2);
        var best = -1;
        for (var i = 0; i < count; i++)
        {
            var record = cmap + 4 + i * 8;
            var platform = U16(data, record);
            var encoding = U16(data, record + 2);
            var subtable = cmap + (int)U32(data, record + 4);
            if (U16(data, subtable) != 4) continue;

            // (3,1) — windows unicode BMP, самый распространённый; (0,*) — unicode.
            if (platform == 3 && encoding == 1) return ReadFormat4(data, subtable);
            if (platform == 0 && best < 0) best = subtable;
        }
        if (best < 0) throw new InvalidDataException("в шрифте нет unicode-таблицы cmap формата 4");
        return ReadFormat4(data, best);
    }

    private static Dictionary<int, ushort> ReadFormat4(byte[] data, int subtable)
    {
        var segCount = U16(data, subtable + 6) / 2;
        var endCodes = subtable + 14;
        var startCodes = endCodes + segCount * 2 + 2;
        var idDeltas = startCodes + segCount * 2;
        var idRangeOffsets = idDeltas + segCount * 2;

        var map = new Dictionary<int, ushort>(segCount * 8);
        for (var segment = 0; segment < segCount; segment++)
        {
            var end = U16(data, endCodes + segment * 2);
            var start = U16(data, startCodes + segment * 2);
            var delta = S16(data, idDeltas + segment * 2);
            var rangeOffsetAt = idRangeOffsets + segment * 2;
            var rangeOffset = U16(data, rangeOffsetAt);
            if (start == 0xFFFF) continue;

            for (int code = start; code <= end && code != 0xFFFF; code++)
            {
                ushort glyph;
                if (rangeOffset == 0)
                {
                    glyph = (ushort)((code + delta) & 0xFFFF);
                }
                else
                {
                    var glyphAt = rangeOffsetAt + rangeOffset + (code - start) * 2;
                    if (glyphAt + 1 >= data.Length) continue;
                    glyph = U16(data, glyphAt);
                    if (glyph != 0) glyph = (ushort)((glyph + delta) & 0xFFFF);
                }
                if (glyph != 0) map[code] = glyph;
            }
        }
        return map;
    }

    private static ushort U16(byte[] data, int offset) =>
        BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset, 2));

    private static short S16(byte[] data, int offset) =>
        BinaryPrimitives.ReadInt16BigEndian(data.AsSpan(offset, 2));

    private static uint U32(byte[] data, int offset) =>
        BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset, 4));
}
