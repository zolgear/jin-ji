using System.Globalization;
using System.Text;
using System.Text.Json;
using JinJi.Application.Changes;

namespace JinJi.Application.Webhooks;

/// <summary>通知する変更の 1 件（種別・ID・見せてよい日・取り消しか。値は持たない）。</summary>
public sealed record WebhookEvent(ChangeEntityType Type, Guid Id, DateOnly EffectiveDate, bool Withdrawn);

/// <summary>
/// Webhook の本文（JSON）。種別と ID だけを載せ、氏名などの値は載せない（受け手は自分のトークンで REST・変更フィードから取り直す）。
/// </summary>
/// <remarks>
/// <code>
/// { "id": "&lt;イベント ID&gt;", "type": "changes", "created_at": "…", "client_id": "attendance",
///   "events": [ { "type": "person", "id": "…", "effective_date": "2027-04-01", "withdrawn": false } ],
///   "changes_since": "&lt;変更フィードのカーソル&gt;" }
/// { "id": "…", "type": "changes.summary", "created_at": "…", "client_id": "…",
///   "summary": { "total": 4200, "person": 4000, "org_unit": 199, "site": 1, "withdrawn": 0,
///                "first_effective_date": "…", "last_effective_date": "…" },
///   "changes_since": "…" }
/// { "id": "…", "type": "ping", "created_at": "…", "client_id": "…" }
/// </code>
/// <list type="bullet">
/// <item><c>changes_since</c>: この通知の変更を含む位置から読める変更フィード（<c>GET /api/v1/changes?since=</c>）のカーソル。受け手のクライアントの
/// カーソルとして署名してあり、受け手のトークンでだけ使える。要約を受けたら、このカーソルから変更フィードを読む（カーソルの署名鍵が
/// 変わっていたら 400 になるので、その場合は一覧から取り直す）。カーソルを作れない構成では null</item>
/// <item><c>effective_date</c>: 変更として見せてよい日（変更フィードと同じ）。先行提供の受け手には業務日付より後の日が来る（その日の時点で取り直す）</item>
/// <item><c>withdrawn</c>: 将来の予定の取り消し・訂正（先行提供の受け手にだけ届く）。受け手は ID で取り直し、予定が消えた・変わったことを知る</item>
/// </list>
/// </remarks>
public static class WebhookPayload
{
    public const string ChangesType = "changes";
    public const string SummaryType = "changes.summary";
    public const string PingType = "ping";

    public static string Changes(Guid id, DateTimeOffset createdAt, string clientId, IReadOnlyList<WebhookEvent> events, string? changesSince)
    {
        ArgumentNullException.ThrowIfNull(events);
        return Write(writer =>
        {
            Header(writer, id, ChangesType, createdAt, clientId);
            writer.WriteStartArray("events");
            foreach (var e in events)
            {
                writer.WriteStartObject();
                writer.WriteString("type", WebhookCodes.ToCode(e.Type));
                writer.WriteString("id", e.Id.ToString("D"));
                writer.WriteString("effective_date", Date(e.EffectiveDate));
                writer.WriteBoolean("withdrawn", e.Withdrawn);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            Cursor(writer, changesSince);
        });
    }

    public static string Summary(Guid id, DateTimeOffset createdAt, string clientId, IReadOnlyList<WebhookEvent> events, string? changesSince)
    {
        ArgumentNullException.ThrowIfNull(events);
        return Write(writer =>
        {
            Header(writer, id, SummaryType, createdAt, clientId);
            writer.WriteStartObject("summary");
            writer.WriteNumber("total", events.Count);
            writer.WriteNumber("person", events.Count(e => e.Type == ChangeEntityType.Person));
            writer.WriteNumber("org_unit", events.Count(e => e.Type == ChangeEntityType.OrgUnit));
            writer.WriteNumber("site", events.Count(e => e.Type == ChangeEntityType.Site));
            writer.WriteNumber("withdrawn", events.Count(e => e.Withdrawn));
            if (events.Count > 0)
            {
                writer.WriteString("first_effective_date", Date(events.Min(e => e.EffectiveDate)));
                writer.WriteString("last_effective_date", Date(events.Max(e => e.EffectiveDate)));
            }

            writer.WriteEndObject();
            Cursor(writer, changesSince);
        });
    }

    public static string Ping(Guid id, DateTimeOffset createdAt, string clientId) =>
        Write(writer => Header(writer, id, PingType, createdAt, clientId));

    /// <summary>本文から人の ID を取り出す（送信の監査ログの対象者）。要約・ping は空。</summary>
    public static IReadOnlyList<Guid> PersonIds(string body)
    {
        ArgumentNullException.ThrowIfNull(body);
        using var document = JsonDocument.Parse(body);
        if (!document.RootElement.TryGetProperty("events", out var events) || events.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return [.. events.EnumerateArray()
            .Where(e => e.GetProperty("type").GetString() == "person")
            .Select(e => e.GetProperty("id").GetGuid())
            .Distinct()];
    }

    private static void Header(Utf8JsonWriter writer, Guid id, string type, DateTimeOffset createdAt, string clientId)
    {
        writer.WriteString("id", id.ToString("D"));
        writer.WriteString("type", type);
        writer.WriteString("created_at", createdAt.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
        writer.WriteString("client_id", clientId);
    }

    private static void Cursor(Utf8JsonWriter writer, string? changesSince)
    {
        if (changesSince is null)
        {
            writer.WriteNull("changes_since");
        }
        else
        {
            writer.WriteString("changes_since", changesSince);
        }
    }

    private static string Date(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string Write(Action<Utf8JsonWriter> write)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            write(writer);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
