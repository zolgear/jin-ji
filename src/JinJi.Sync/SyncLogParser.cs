using System.Globalization;
using System.Text.Json;
using JinJi.Application.Sync;

namespace JinJi.Sync;

/// <summary>
/// 生成したスクリプトの実行ログ（JSON。<see cref="EntraScriptRenderer.LogFormat"/>）を読む。ログは信頼しない入力として扱う。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>形式・計画 ID・データの SHA-256 の形が合わなければ拒否する（計画・データとの照合は <see cref="SyncPlanService"/> が行う）</item>
/// <item>結果は既知の種別だけ。observed は同期の項目（<see cref="EntraFields.All"/>）の文字列だけを残し、他は捨てる</item>
/// <item>メッセージは制御文字を除いて 1,000 文字までにする（DB の制約と同じ）</item>
/// <item>UTF-8 の BOM（Windows PowerShell 5.1 が書く）を受け付ける</item>
/// </list>
/// </remarks>
public sealed class SyncLogParser : ISyncLogParser
{
    public const int MaxMessageLength = 1000;

    /// <summary>ログの大きさの上限（1 万人規模で 1 人あたり 3 項目でも十分な大きさ）。</summary>
    public const int MaxBytes = 64 * 1024 * 1024;

    private static readonly Dictionary<string, SyncOutcome> Outcomes = new(StringComparer.Ordinal)
    {
        ["succeeded"] = SyncOutcome.Succeeded,
        ["already_applied"] = SyncOutcome.AlreadyApplied,
        ["skipped_precondition"] = SyncOutcome.SkippedPrecondition,
        ["failed"] = SyncOutcome.Failed,
        ["would_apply"] = SyncOutcome.WouldApply,
    };

    public SyncExecutionLog Parse(ReadOnlySpan<byte> content)
    {
        if (content.Length > MaxBytes)
        {
            throw new SyncLogFormatException("実行ログが大きすぎます。");
        }

        if (content.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
        {
            content = content[3..];
        }

        try
        {
            using var document = JsonDocument.Parse(content.ToArray(), new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || String(root, "format") != EntraScriptRenderer.LogFormat)
            {
                throw new SyncLogFormatException($"実行ログの形式が {EntraScriptRenderer.LogFormat} ではありません。");
            }

            if (!Guid.TryParseExact(String(root, "planId"), "D", out var planId))
            {
                throw new SyncLogFormatException("実行ログの planId が UUID ではありません。");
            }

            var dataSha = String(root, "dataSha256");
            if (dataSha is null || dataSha.Length != 64 || !dataSha.All(Uri.IsHexDigit))
            {
                throw new SyncLogFormatException("実行ログの dataSha256 の形が不正です。");
            }

            var whatIf = root.TryGetProperty("whatIf", out var w) && w.ValueKind == JsonValueKind.True;
            var entries = new List<SyncLogEntry>();
            if (root.TryGetProperty("entries", out var list))
            {
                if (list.ValueKind == JsonValueKind.Object)
                {
                    // PowerShell 5.1 は要素が 1 つの配列をオブジェクトにすることがある
                    entries.Add(Entry(list));
                }
                else if (list.ValueKind == JsonValueKind.Array)
                {
                    entries.AddRange(list.EnumerateArray().Select(Entry));
                }
                else if (list.ValueKind != JsonValueKind.Null)
                {
                    throw new SyncLogFormatException("実行ログの entries が配列ではありません。");
                }
            }

            return new SyncExecutionLog(planId, dataSha.ToLowerInvariant(), whatIf, Time(root, "startedAt"), Time(root, "finishedAt"), entries);
        }
        catch (JsonException ex)
        {
            throw new SyncLogFormatException($"実行ログを JSON として読めません: {ex.Message}", ex);
        }
    }

    private static SyncLogEntry Entry(JsonElement entry)
    {
        if (entry.ValueKind != JsonValueKind.Object)
        {
            throw new SyncLogFormatException("実行ログの entries の要素がオブジェクトではありません。");
        }

        if (!Guid.TryParseExact(String(entry, "itemId"), "D", out var itemId))
        {
            throw new SyncLogFormatException("実行ログの itemId が UUID ではありません。");
        }

        if (String(entry, "outcome") is not { } code || !Outcomes.TryGetValue(code, out var outcome))
        {
            throw new SyncLogFormatException($"実行ログの outcome が不正です（項目 {itemId}）。");
        }

        var observed = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (entry.TryGetProperty("observed", out var values) && values.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in values.EnumerateObject().Where(p => EntraFields.All.Contains(p.Name)))
            {
                observed[property.Name] = property.Value.ValueKind switch
                {
                    JsonValueKind.String => Clean(property.Value.GetString(), 400),
                    JsonValueKind.True => "true",
                    JsonValueKind.False => "false",
                    _ => null,
                };
            }
        }

        return new SyncLogEntry(itemId, outcome, observed, Clean(String(entry, "message"), MaxMessageLength), Time(entry, "at"));
    }

    private static string? String(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static DateTimeOffset? Time(JsonElement element, string name) =>
        String(element, name) is { } text && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time) ? time : null;

    private static string? Clean(string? text, int max)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        var cleaned = new string([.. text.Select(c => char.IsControl(c) ? ' ' : c)]).Trim();
        return cleaned.Length == 0 ? null : cleaned[..Math.Min(cleaned.Length, max)];
    }
}
