using System.Globalization;
using System.Text.Json;

namespace JinJi.IntegrationTests.Support;

/// <summary>
/// 応答の JSON を OpenAPI 3.1 の文書のスキーマで検証する（テスト用の最小の JSON Schema の検証器。依存を増やさないため自前で持つ）。
/// </summary>
/// <remarks>
/// 扱うキーワード: <c>$ref</c>（文書内の components/schemas）、<c>type</c>（文字列・配列）、<c>enum</c>、<c>properties</c>、<c>required</c>、
/// <c>items</c>、<c>oneOf</c> / <c>anyOf</c> / <c>allOf</c>、<c>additionalProperties</c>、<c>format</c>（uuid / date / date-time）。
/// 文書より厳しく、応答のオブジェクトに文書に無い項目があれば誤りにする（文書に載っていない項目を UI が受け取らないように）。
/// ただし <c>additionalProperties</c> を持つスキーマ（辞書）は除く。
/// </remarks>
internal sealed class OpenApiResponseValidator(JsonElement document)
{
    private readonly JsonElement _schemas = document.GetProperty("components").GetProperty("schemas");

    /// <summary>パスと操作の 200 の応答（application/json）のスキーマ。</summary>
    public JsonElement ResponseSchema(string path, string method = "get", string status = "200") =>
        document.GetProperty("paths").GetProperty(path).GetProperty(method).GetProperty("responses").GetProperty(status)
            .GetProperty("content").GetProperty("application/json").GetProperty("schema");

    /// <summary>検証し、誤りの一覧を返す（空なら一致）。</summary>
    public List<string> Validate(JsonElement schema, JsonElement value)
    {
        var errors = new List<string>();
        Check(schema, value, "$", errors);
        return errors;
    }

    private void Check(JsonElement schema, JsonElement value, string at, List<string> errors)
    {
        if (schema.ValueKind == JsonValueKind.True)
        {
            return;
        }

        if (schema.TryGetProperty("$ref", out var reference))
        {
            Check(Resolve(reference.GetString()!), value, at, errors);
            return;
        }

        if (schema.TryGetProperty("allOf", out var allOf))
        {
            foreach (var sub in allOf.EnumerateArray())
            {
                Check(sub, value, at, errors);
            }
        }

        foreach (var keyword in new[] { "oneOf", "anyOf" })
        {
            if (schema.TryGetProperty(keyword, out var options))
            {
                var matches = options.EnumerateArray().Count(sub => Validate(sub, value).Count == 0);
                if (keyword == "oneOf" ? matches != 1 : matches == 0)
                {
                    errors.Add($"{at}: {keyword} の {matches} 個に一致（{value.ValueKind}）");
                }
            }
        }

        if (schema.TryGetProperty("type", out var type))
        {
            var types = type.ValueKind == JsonValueKind.Array ? type.EnumerateArray().Select(t => t.GetString()!).ToList() : [type.GetString()!];
            if (!types.Any(t => IsType(t, value)))
            {
                errors.Add($"{at}: 型が {string.Join("|", types)} ではない（{value.ValueKind}: {Short(value)}）");
                return;
            }
        }

        if (schema.TryGetProperty("enum", out var values) && !values.EnumerateArray().Any(v => JsonElement.DeepEquals(v, value)))
        {
            errors.Add($"{at}: enum に無い値 {Short(value)}（{string.Join(", ", values.EnumerateArray().Select(Short))}）");
        }

        if (value.ValueKind == JsonValueKind.String && schema.TryGetProperty("format", out var format) && !IsFormat(format.GetString()!, value.GetString()!))
        {
            errors.Add($"{at}: 形式 {format.GetString()} ではない（{value.GetString()}）");
        }

        if (value.ValueKind == JsonValueKind.Object)
        {
            CheckObject(schema, value, at, errors);
        }

        if (value.ValueKind == JsonValueKind.Array && schema.TryGetProperty("items", out var items))
        {
            var index = 0;
            foreach (var item in value.EnumerateArray())
            {
                Check(items, item, $"{at}[{index++}]", errors);
            }
        }
    }

    private void CheckObject(JsonElement schema, JsonElement value, string at, List<string> errors)
    {
        var declared = schema.TryGetProperty("properties", out var properties) ? properties : default;
        if (schema.TryGetProperty("required", out var required))
        {
            foreach (var name in required.EnumerateArray().Select(r => r.GetString()!))
            {
                if (!value.TryGetProperty(name, out _))
                {
                    errors.Add($"{at}: required の {name} が無い（省略された）");
                }
            }
        }

        var hasAdditional = schema.TryGetProperty("additionalProperties", out var additional);
        foreach (var property in value.EnumerateObject())
        {
            if (declared.ValueKind == JsonValueKind.Object && declared.TryGetProperty(property.Name, out var propertySchema))
            {
                Check(propertySchema, property.Value, $"{at}.{property.Name}", errors);
            }
            else if (hasAdditional)
            {
                if (additional.ValueKind == JsonValueKind.False)
                {
                    errors.Add($"{at}: 文書に無い項目 {property.Name}");
                }
                else if (additional.ValueKind == JsonValueKind.Object)
                {
                    Check(additional, property.Value, $"{at}.{property.Name}", errors);
                }
            }
            else if (!schema.TryGetProperty("allOf", out _) && !schema.TryGetProperty("oneOf", out _) && !schema.TryGetProperty("anyOf", out _))
            {
                errors.Add($"{at}: 文書に無い項目 {property.Name}");
            }
        }
    }

    private JsonElement Resolve(string reference)
    {
        const string prefix = "#/components/schemas/";
        return reference.StartsWith(prefix, StringComparison.Ordinal)
            ? _schemas.GetProperty(reference[prefix.Length..])
            : throw new NotSupportedException($"未対応の $ref: {reference}");
    }

    private static bool IsType(string type, JsonElement value) => type switch
    {
        "null" => value.ValueKind == JsonValueKind.Null,
        "string" => value.ValueKind == JsonValueKind.String,
        "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
        "number" => value.ValueKind == JsonValueKind.Number,
        "array" => value.ValueKind == JsonValueKind.Array,
        "object" => value.ValueKind == JsonValueKind.Object,
        _ => throw new NotSupportedException($"未対応の型: {type}"),
    };

    private static bool IsFormat(string format, string value) => format switch
    {
        "uuid" => Guid.TryParseExact(value, "D", out _),
        "date" => DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _),
        "date-time" => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out _),
        _ => true,
    };

    private static string Short(JsonElement value)
    {
        var text = value.GetRawText();
        return text.Length > 80 ? text[..80] + "…" : text;
    }
}
