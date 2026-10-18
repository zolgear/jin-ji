using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace JinJi.Api;

/// <summary>
/// 文字列の項目が取り得る値（コード値）。OpenAPI の文書で <c>enum</c> として宣言する（UI の型を文字列のユニオンにするため）。
/// </summary>
/// <remarks>
/// 列挙型を渡すと、その名前を snake_case にした値（<see cref="Codes.Of{T}"/> と同じ規則）を使う。
/// 文書と実際の応答が一致することは、応答を文書のスキーマで検証するテストで確かめる。
/// </remarks>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
public sealed class OpenApiEnumAttribute : Attribute
{
    public OpenApiEnumAttribute(Type enumType)
    {
        ArgumentNullException.ThrowIfNull(enumType);
        if (!enumType.IsEnum)
        {
            throw new ArgumentException("列挙型を指定してください。", nameof(enumType));
        }

        Values = [.. Enum.GetNames(enumType).Select(JsonNamingPolicy.SnakeCaseLower.ConvertName)];
    }

    public OpenApiEnumAttribute(params string[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        Values = values;
    }

    /// <summary>取り得る値。</summary>
    public IReadOnlyList<string> Values { get; }
}

/// <summary>OpenAPI の文書を実際の応答に合わせる変換。</summary>
/// <remarks>
/// <list type="bullet">
/// <item>応答の型（名前が Response で終わる型）は、値が null の項目も省略せずに null を書く（<see cref="ApiModule"/> の JSON の設定）。
/// そのため、応答の型のすべての項目を required として宣言する（null になり得る項目は型に null を含む）</item>
/// <item><see cref="OpenApiEnumAttribute"/> の付いた項目に enum を付ける（配列なら要素に）。null になり得る項目は enum に null も含める</item>
/// </list>
/// </remarks>
internal static class OpenApiConventions
{
    /// <summary>応答の型の名前の接尾辞。</summary>
    public const string ResponseSuffix = "Response";

    public static Task TransformSchemaAsync(OpenApiSchema schema, OpenApiSchemaTransformerContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(context);

        if (context.JsonPropertyInfo is { } property)
        {
            if (property.AttributeProvider?.GetCustomAttributes(typeof(OpenApiEnumAttribute), inherit: true).OfType<OpenApiEnumAttribute>().FirstOrDefault() is { } values)
            {
                var target = schema.Type is { } type && type.HasFlag(JsonSchemaType.Array) && schema.Items is OpenApiSchema items ? items : schema;
                target.Enum = [.. values.Values.Select(v => (JsonNode)JsonValue.Create(v))];
                if (target.Type is { } targetType && targetType.HasFlag(JsonSchemaType.Null))
                {
                    target.Enum.Add(null!);
                }
            }

            return Task.CompletedTask;
        }

        var clrType = context.JsonTypeInfo.Type;
        if (clrType.Name.EndsWith(ResponseSuffix, StringComparison.Ordinal) && schema.Properties is { Count: > 0 } properties)
        {
            schema.Required = new HashSet<string>(properties.Keys, StringComparer.Ordinal);
        }

        return Task.CompletedTask;
    }
}
