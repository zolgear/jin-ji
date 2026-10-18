using System.Globalization;
using System.Text.Json;

namespace JinJi.Mcp.Tools;

/// <summary>
/// ツールの引数の読み取り。形・範囲が合わなければ <see cref="McpToolException"/>（invalid_argument）で、どの引数をどう直せばよいかを書く。
/// </summary>
/// <remarks>
/// 入力スキーマ（<c>additionalProperties: false</c>）と同じ規則で、知らない引数も誤りにする（書き間違いを黙って無視しない）。
/// 数値・真偽値を文字列で受け付けない（REST API の JSON と同じ）。
/// </remarks>
internal sealed class McpArguments
{
    private readonly IReadOnlyDictionary<string, JsonElement> _values;

    public McpArguments(IDictionary<string, JsonElement>? values, JsonElement schema)
    {
        _values = values is null ? new Dictionary<string, JsonElement>() : new Dictionary<string, JsonElement>(values, StringComparer.Ordinal);
        var known = schema.TryGetProperty("properties", out var properties)
            ? properties.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal)
            : [];
        var unknown = _values.Keys.Where(k => !known.Contains(k)).Order(StringComparer.Ordinal).ToList();
        if (unknown.Count > 0)
        {
            throw McpToolException.Invalid(
                $"知らない引数があります: {string.Join(", ", unknown)}。使える引数: {(known.Count == 0 ? "（なし）" : string.Join(", ", known.Order(StringComparer.Ordinal)))}。");
        }
    }

    private bool TryGet(string name, out JsonElement value) =>
        _values.TryGetValue(name, out value) && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);

    /// <summary>文字列（省略・null なら null）。前後の空白は除き、空なら null。</summary>
    public string? String(string name, int maxLength)
    {
        if (!TryGet(name, out var value))
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            throw McpToolException.Invalid($"{name} は文字列で指定してください。");
        }

        var text = value.GetString()!.Trim();
        if (text.Length > maxLength)
        {
            throw McpToolException.Invalid($"{name} は {maxLength} 文字以内で指定してください。");
        }

        return text.Length == 0 ? null : text;
    }

    /// <summary>必須の文字列。</summary>
    public string RequiredString(string name, int maxLength) =>
        String(name, maxLength) ?? throw McpToolException.Invalid($"{name} を指定してください。");

    /// <summary>jin-ji ID（UUID）。省略なら null。</summary>
    public Guid? Id(string name)
    {
        if (String(name, 64) is not { } text)
        {
            return null;
        }

        return Guid.TryParseExact(text, "D", out var id)
            ? id
            : throw McpToolException.Invalid($"{name} は jin-ji ID（UUID。例: 0192f0c4-7a3e-7c2d-9b1a-3c5d7e9f1a2b）で指定してください。");
    }

    /// <summary>必須の jin-ji ID。</summary>
    public Guid RequiredId(string name) => Id(name) ?? throw McpToolException.Invalid($"{name} を指定してください（jin-ji ID）。");

    /// <summary>jin-ji ID の配列（重複は除いて指定の順）。</summary>
    public IReadOnlyList<Guid> Ids(string name, int min, int max)
    {
        if (!TryGet(name, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            throw McpToolException.Invalid($"{name} に jin-ji ID（UUID）の配列を指定してください（{min}〜{max} 件）。");
        }

        var ids = new List<Guid>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || !Guid.TryParseExact(item.GetString(), "D", out var id))
            {
                throw McpToolException.Invalid($"{name} の要素は jin-ji ID（UUID）の文字列にしてください。");
            }

            if (!ids.Contains(id))
            {
                ids.Add(id);
            }
        }

        return ids.Count < min || ids.Count > max
            ? throw McpToolException.Invalid($"{name} は重複を除いて {min}〜{max} 件で指定してください。")
            : ids;
    }

    /// <summary>文字列の配列（省略・null なら空。前後の空白を除き、重複を除く）。空の要素・長すぎる要素・件数の超過は誤りにする。</summary>
    public IReadOnlyList<string> Strings(string name, int maxItems, int maxLength)
    {
        if (!TryGet(name, out var value))
        {
            return [];
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            throw McpToolException.Invalid($"{name} は文字列の配列で指定してください（{maxItems} 件まで）。");
        }

        var items = new List<string>();
        foreach (var item in value.EnumerateArray())
        {
            var text = item.ValueKind == JsonValueKind.String ? item.GetString()!.Trim() : null;
            if (string.IsNullOrEmpty(text) || text.Length > maxLength)
            {
                throw McpToolException.Invalid($"{name} の要素は空でない {maxLength} 文字以内の文字列にしてください。");
            }

            if (!items.Contains(text, StringComparer.Ordinal))
            {
                items.Add(text);
            }
        }

        return items.Count > maxItems ? throw McpToolException.Invalid($"{name} は重複を除いて {maxItems} 件までです。") : items;
    }

    /// <summary>決まった値の配列（省略なら空）。</summary>
    public IReadOnlyList<string> ChoiceList(string name, int maxItems, IReadOnlyList<string> choices)
    {
        var items = Strings(name, maxItems, 64);
        return items.FirstOrDefault(i => !choices.Contains(i, StringComparer.Ordinal)) is { } bad
            ? throw McpToolException.Invalid($"{name} の要素は {string.Join(" / ", choices)} のどれかにしてください（{bad} は使えません）。")
            : items;
    }

    /// <summary>引数が指定されたか（null は指定なしと同じ）。</summary>
    public bool Has(string name) => TryGet(name, out _);

    /// <summary>真偽値（省略なら既定値）。</summary>
    public bool Flag(string name, bool defaultValue = false)
    {
        if (!TryGet(name, out var value))
        {
            return defaultValue;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw McpToolException.Invalid($"{name} は true か false で指定してください。"),
        };
    }

    /// <summary>整数（省略なら既定値）。範囲外は切り詰めずに誤りにする。</summary>
    public int Integer(string name, int defaultValue, int min, int max)
    {
        if (!TryGet(name, out var value))
        {
            return defaultValue;
        }

        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) && number >= min && number <= max
            ? number
            : throw McpToolException.Invalid($"{name} は {min}〜{max} の整数で指定してください。");
    }

    /// <summary>0〜1 の数。</summary>
    public decimal Fraction(string name)
    {
        if (!TryGet(name, out var value) || value.ValueKind != JsonValueKind.Number || !value.TryGetDecimal(out var number))
        {
            throw McpToolException.Invalid($"{name} に 0〜1 の数を指定してください。");
        }

        return number is >= 0m and <= 1m ? number : throw McpToolException.Invalid($"{name} は 0〜1 で指定してください。");
    }

    /// <summary>時点（yyyy-MM-dd）。省略なら null（今日）。</summary>
    public DateOnly? Date(string name)
    {
        if (String(name, 10) is not { } text)
        {
            return null;
        }

        return DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : throw McpToolException.Invalid($"{name} は yyyy-MM-dd（例: 2026-04-01）で指定してください。");
    }

    /// <summary>決まった値のどれか（省略なら null）。</summary>
    public string? Choice(string name, params string[] choices)
    {
        if (String(name, 64) is not { } text)
        {
            return null;
        }

        return choices.Contains(text, StringComparer.Ordinal)
            ? text
            : throw McpToolException.Invalid($"{name} は {string.Join(" / ", choices)} のどれかで指定してください。");
    }
}
