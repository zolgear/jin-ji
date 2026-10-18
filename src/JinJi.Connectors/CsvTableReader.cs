using System.Text;

namespace JinJi.Connectors;

/// <summary>
/// 取込元の表（見出しと行）。コネクタの内部の中間表現で、保存しない。項目マッピング（<see cref="ColumnMapper"/>）を通してから staging に渡す。
/// </summary>
/// <param name="Headers">見出し（列の順）。</param>
/// <param name="Rows">見出しより下の行（行番号と、列の順のセルの値。足りないセルは空文字）。</param>
public sealed record RawTable(IReadOnlyList<string> Headers, IReadOnlyList<RawRow> Rows);

/// <summary>取込元の 1 行。</summary>
/// <param name="SourceRowNumber">行番号（CSV はレコードの番号、Excel はシートの行番号。1 始まり）。</param>
public sealed record RawRow(int SourceRowNumber, IReadOnlyList<string> Cells);

/// <summary>CSV の文字コードの判定。</summary>
public static class TextEncodingDetector
{
    static TextEncodingDetector()
    {
        // Shift_JIS（CP932）は .NET の CodePagesEncodingProvider で使う（追加のパッケージは要らない）
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    /// <summary>Shift_JIS（Windows の CP932。NEC 特殊文字・IBM 拡張文字を含む）。</summary>
    public static Encoding ShiftJis => Encoding.GetEncoding(932, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);

    /// <summary>
    /// バイト列を文字列にする。<paramref name="encoding"/> が auto なら、BOM（UTF-8・UTF-16）→ 厳密な UTF-8 として読めるか → Shift_JIS（CP932）の順に判定する。
    /// </summary>
    /// <returns>文字列（BOM は除く）と、判定した文字コードの名前。</returns>
    public static (string Text, string EncodingName) Decode(ReadOnlySpan<byte> bytes, string encoding = "auto")
    {
        switch (encoding)
        {
            case "utf-8":
                return (DecodeStrict(new UTF8Encoding(false, true), SkipUtf8Bom(bytes)), "utf-8");
            case "shift_jis":
                return (DecodeShiftJis(bytes), "shift_jis");
            case "auto":
                break;
            default:
                throw new ArgumentException($"未知の文字コード: {encoding}", nameof(encoding));
        }

        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
        {
            return (DecodeStrict(new UTF8Encoding(false, true), bytes[3..]), "utf-8-bom");
        }

        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE]))
        {
            return (DecodeStrict(new UnicodeEncoding(false, false, true), bytes[2..]), "utf-16le");
        }

        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF]))
        {
            return (DecodeStrict(new UnicodeEncoding(true, false, true), bytes[2..]), "utf-16be");
        }

        try
        {
            return (new UTF8Encoding(false, true).GetString(bytes), "utf-8");
        }
        catch (DecoderFallbackException)
        {
            // 厳密な UTF-8 として読めない → Shift_JIS（CP932）とみなす
            return (DecodeShiftJis(bytes), "shift_jis");
        }
    }

    /// <summary>
    /// Shift_JIS（CP932）として読む。.NET のコードページ 932 は NEC 選定 IBM 拡張文字（0xED40〜0xEEFC。Excel が書き出す 﨑・髙 などが入る）を
    /// 読めないので、同じ文字の IBM 拡張文字（0xFA40〜0xFC4B）のバイトに置き換えてから読む。
    /// </summary>
    /// <remarks>
    /// NEC 選定 IBM 拡張文字の漢字 360 字（0xED40〜0xEEEC）は IBM 拡張文字の漢字（0xFA5C〜0xFC4B）と同じ文字が同じ順に並ぶ。
    /// 0xEEEF〜0xEEFC（小文字のローマ数字・￢・￤・＇・＂）は個別に対応させる。
    /// </remarks>
    public static string DecodeShiftJis(ReadOnlySpan<byte> bytes)
    {
        var buffer = bytes.ToArray();
        for (var i = 0; i < buffer.Length; i++)
        {
            var lead = buffer[i];
            if (lead is < 0x81 or (> 0x9F and < 0xE0) or > 0xFC)
            {
                continue;
            }

            if (i + 1 >= buffer.Length)
            {
                break;
            }

            var trail = buffer[i + 1];
            if (lead is 0xED or 0xEE && trail is (>= 0x40 and <= 0x7E) or (>= 0x80 and <= 0xFC))
            {
                var index = ((lead - 0xED) * 188) + (trail < 0x7F ? trail - 0x40 : trail - 0x41);
                var target = index < 360 ? 28 + index : NecSymbol(lead, trail);
                if (target >= 0)
                {
                    var t = target % 188;
                    buffer[i] = (byte)(0xFA + (target / 188));
                    buffer[i + 1] = (byte)(t < 0x3F ? 0x40 + t : 0x41 + t);
                }
            }

            i++;
        }

        return DecodeStrict(ShiftJis, buffer);
    }

    /// <summary>0xEEEF〜0xEEFC の IBM 拡張文字での位置（0xFA40 からの番号）。対応が無ければ -1。</summary>
    private static int NecSymbol(byte lead, byte trail) => (lead, trail) switch
    {
        (0xEE, >= 0xEF and <= 0xF8) => trail - 0xEF,
        (0xEE, >= 0xF9 and <= 0xFC) => 0x14 + (trail - 0xF9),
        _ => -1,
    };

    private static ReadOnlySpan<byte> SkipUtf8Bom(ReadOnlySpan<byte> bytes) =>
        bytes.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]) ? bytes[3..] : bytes;

    private static string DecodeStrict(Encoding encoding, ReadOnlySpan<byte> bytes)
    {
        try
        {
            return encoding.GetString(bytes);
        }
        catch (DecoderFallbackException ex)
        {
            throw new FormatException($"文字コード {encoding.WebName} として読めないバイトがあります。", ex);
        }
    }
}

/// <summary>
/// CSV の読み込み（RFC 4180 + 実務のゆれ）。改行は CRLF / LF / CR、引用符で囲んだ値の中の区切り・改行・二重の引用符（""）を扱う。
/// </summary>
public static class CsvTableReader
{
    /// <summary>CSV を読む。<paramref name="headerRow"/> より上の行（表題など）は読み飛ばす。空の行は除く。</summary>
    public static RawTable Read(string text, char delimiter = ',', int headerRow = 1)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentOutOfRangeException.ThrowIfLessThan(headerRow, 1);

        var records = Parse(text, delimiter);
        if (records.Count < headerRow)
        {
            return new RawTable([], []);
        }

        var headers = records[headerRow - 1].Select(h => h.Trim()).ToList();
        var rows = new List<RawRow>();
        for (var i = headerRow; i < records.Count; i++)
        {
            var cells = records[i];
            if (cells.All(string.IsNullOrWhiteSpace))
            {
                continue;
            }

            var padded = Enumerable.Range(0, Math.Max(headers.Count, cells.Count)).Select(c => c < cells.Count ? cells[c] : string.Empty).ToList();
            rows.Add(new RawRow(i + 1, padded));
        }

        return new RawTable(headers, rows);
    }

    private static List<List<string>> Parse(string text, char delimiter)
    {
        var records = new List<List<string>>();
        var record = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        var fieldStarted = false;
        var line = 1;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        field.Append('"');
                        i++;
                    }
                    else
                    {
                        quoted = false;
                    }
                }
                else
                {
                    if (c == '\n')
                    {
                        line++;
                    }

                    field.Append(c);
                }

                continue;
            }

            if (c == '"' && field.Length == 0 && !fieldStarted)
            {
                quoted = true;
                fieldStarted = true;
            }
            else if (c == delimiter)
            {
                record.Add(field.ToString());
                field.Clear();
                fieldStarted = false;
            }
            else if (c is '\r' or '\n')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }

                record.Add(field.ToString());
                records.Add(record);
                record = [];
                field.Clear();
                fieldStarted = false;
                line++;
            }
            else
            {
                field.Append(c);
                fieldStarted = true;
            }
        }

        if (quoted)
        {
            throw new FormatException($"CSV の引用符が閉じていません（{line} 行目付近）。");
        }

        if (field.Length > 0 || record.Count > 0 || fieldStarted)
        {
            record.Add(field.ToString());
            records.Add(record);
        }

        return records;
    }
}
