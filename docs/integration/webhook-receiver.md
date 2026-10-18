# Webhook の受け手の実装（社内システム向け）

jin-ji は、登録した社内システム（client credentials の API クライアント）に「人・組織・拠点が変わった」ことを Webhook で知らせる。本文は種別と ID だけで、氏名などの値は載せない。受け手は通知を受けたら、自分のトークンで REST API（`GET /api/v1/people?ids=`・`/orgs/{id}`・`/sites/{id}`）か変更フィード（`GET /api/v1/changes?since=`）から取り直す。設計の規則は [アーキテクチャ概要 §9「Webhook の実装の規則」](../architecture/overview.md#webhook-の実装の規則)。

## 受け取るもの

POST（`Content-Type: application/json; charset=utf-8`、`User-Agent: jin-ji-webhook/1`）。ヘッダーと署名は [Standard Webhooks](https://www.standardwebhooks.com/) の形に合わせている（その検証のライブラリもそのまま使える）。

| ヘッダー | 内容 |
| --- | --- |
| `webhook-id` | イベント ID（UUID）。本文の `id` と同じ。**冪等キー**: 再送でも同じ値で届く |
| `webhook-timestamp` | 送った時刻（Unix 秒）。試行ごとに新しくなる |
| `webhook-signature` | `v1,<Base64 の HMAC-SHA256>`。鍵のローテーション中は新旧の鍵の署名が空白で区切って 2 つ並ぶ |

本文の種類:

```json
{ "id": "0199e1c2-…", "type": "changes", "created_at": "2026-10-12T03:00:00Z", "client_id": "attendance",
  "events": [
    { "type": "person",   "id": "0199…", "effective_date": "2026-10-12", "withdrawn": false },
    { "type": "org_unit", "id": "0199…", "effective_date": "2027-04-01", "withdrawn": false },
    { "type": "person",   "id": "0199…", "effective_date": "2027-04-01", "withdrawn": true }
  ],
  "changes_since": "eyJ4aWQiOj…" }
```

```json
{ "id": "…", "type": "changes.summary", "created_at": "…", "client_id": "attendance",
  "summary": { "total": 4210, "person": 4000, "org_unit": 209, "site": 1, "withdrawn": 0,
               "first_effective_date": "2026-10-12", "last_effective_date": "2026-10-12" },
  "changes_since": "eyJ4aWQiOj…" }
```

```json
{ "id": "…", "type": "ping", "created_at": "…", "client_id": "attendance" }
```

- `events[].type`: `person` / `org_unit` / `site`。拠点の変更は、その拠点を使う組織・人の変更としても届く（拠点の改称 1 件で数千人分になり得る）
- `effective_date`: 変更として見せてよい日。先行提供（有効日の N 日前から将来の予定を見せる設定）を承認された受け手には、業務日付より後の日が届く。その日の時点（`as_of`）で取り直す
- `withdrawn: true`: 将来の予定の取り消し・訂正（先行提供の受け手にだけ届く）。ID で取り直し、予定が消えた・変わったことを知る（取り消された入社予定の人は 404 になる）
- `changes.summary`: 1 回にまとめた変更が登録の件数（既定 1,000）を超えたときの要約。1 件ずつは届かないので、`changes_since` から変更フィードを読む（または一覧から取り直す）
- `changes_since`: この通知の変更を含む位置から読める変更フィードのカーソル（受け手のクライアントのトークンでだけ使える）。カーソルの署名鍵が jin-ji 側で変わっていると 400 になるので、そのときは一覧から取り直す。作れない構成では `null`
- `ping`: 管理画面の「送信の確かめ」。署名の検証の確かめに使う
- 同じ ID が何度も届き得る（少なくとも 1 回は届ける）。受け手が見えない人（ポリシー・退職後の公開期間）の変更は届かない。項目の上限に無い項目だけが変わった人も届く（取り直して何も変わっていないだけ）

## 検証の手順

1. `webhook-timestamp` が今から ±5 分以内であること（古い要求の再送〔リプレイ〕を拒む）
2. `{webhook-id}.{webhook-timestamp}.{受け取った本文のまま}` を UTF-8 にして、登録の鍵（`whsec_` の後ろを Base64 で戻した 32 バイト）で HMAC-SHA256 を計算し、`webhook-signature` のどれかの `v1,` の値と**定数時間で**比べる。本文は JSON として読み直したものではなく、受け取ったバイト列のまま使う
3. 本文の `id` が `webhook-id` と同じであること
4. イベント ID を少なくとも許容幅（5 分）より長く覚えておき、同じ ID はもう一度処理しない（重複・リプレイの除去。DB なら一意制約）
5. すぐに 2xx を返し、取り直しは受け付けの後に行う（応答の時間の上限は既定 10 秒。2xx 以外・時間切れ・リダイレクトは失敗として再送される）

再送は指数バックオフ（既定: 30 秒・1 分・2 分 … の 8 回で約 1 時間）。上限で dead になったものは、jin-ji の人事管理者が管理画面で確かめて再送する（同じイベント ID・同じ本文で届く）。

## 鍵のローテーション

jin-ji の管理画面で鍵を入れ替えると、新しい鍵（`whsec_…`）が 1 回だけ表示される。既定で 24 時間（0〜168 時間で選べる）は旧鍵でも署名が付くので、その間に受け手の設定を新しい鍵に替える。受け手は切り替えの間、新旧の両方の鍵で検証してよい（下の例の `JINJI_WEBHOOK_SECRET_PREVIOUS`）。

## 例（C#・ASP.NET Core の Minimal API。追加の依存なし）

```csharp
using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

// 鍵は環境変数で渡す（ローテーションの間だけ旧鍵も）
byte[][] secrets = [.. new[] { "JINJI_WEBHOOK_SECRET", "JINJI_WEBHOOK_SECRET_PREVIOUS" }
    .Select(Environment.GetEnvironmentVariable)
    .Where(s => !string.IsNullOrEmpty(s))
    .Select(s => Convert.FromBase64String(s!.StartsWith("whsec_", StringComparison.Ordinal) ? s[6..] : s))];
var tolerance = TimeSpan.FromMinutes(5);
// 処理済みのイベント ID（例なのでメモリ。本番は DB の一意制約で覚え、許容幅より長く残す）
var seen = new ConcurrentDictionary<string, DateTimeOffset>(StringComparer.Ordinal);

app.MapPost("/jinji/webhook", async (HttpRequest request) =>
{
    using var reader = new StreamReader(request.Body, Encoding.UTF8);
    var body = await reader.ReadToEndAsync();
    var id = request.Headers["webhook-id"].ToString();
    var timestamp = request.Headers["webhook-timestamp"].ToString();
    var signatures = request.Headers["webhook-signature"].ToString();

    // 1. 時刻（リプレイの拒否）
    if (!long.TryParse(timestamp, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
        || (DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(seconds)).Duration() > tolerance)
    {
        return Results.Unauthorized();
    }

    // 2. 署名（受け取った本文のまま。定数時間で比べる）
    var content = Encoding.UTF8.GetBytes($"{id}.{timestamp}.{body}");
    var valid = secrets.Any(secret =>
    {
        var expected = HMACSHA256.HashData(secret, content);
        return signatures.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(s => s.StartsWith("v1,", StringComparison.Ordinal))
            .Any(s =>
            {
                var actual = new byte[32];
                return Convert.TryFromBase64String(s[3..], actual, out var written) && written == 32
                    && CryptographicOperations.FixedTimeEquals(actual, expected);
            });
    });
    if (!valid)
    {
        return Results.Unauthorized();
    }

    // 3. 本文の id とヘッダーの一致
    using var json = JsonDocument.Parse(body);
    if (json.RootElement.GetProperty("id").GetString() != id)
    {
        return Results.BadRequest();
    }

    // 4. 重複（再送・リプレイ）は処理せずに 2xx
    if (!seen.TryAdd(id, DateTimeOffset.UtcNow))
    {
        return Results.Ok();
    }

    // 5. 取り直しは別の処理に回して、すぐ 2xx を返す（例: キューに積む）
    //    changes         → events の ID を GET /api/v1/people?ids=…（100 件ずつ）・/orgs/{id}・/sites/{id} で取り直す
    //                       （effective_date が業務日付より後なら ?as_of= にその日を付ける）
    //    changes.summary → changes_since から GET /api/v1/changes?since=… を読み進める
    //    ping            → 何もしない
    return Results.Ok();
});

app.Run();
```

jin-ji 側の実装（`JinJi.Application.Webhooks.WebhookSignature.Verify`）も同じ手順で、テストはこの手順で受け手の検証を確かめている。
