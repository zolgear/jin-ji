using JinJi.Policy;

namespace JinJi.Application.Access;

/// <summary>
/// エンドポイントの種別。レート制限の段（クライアント × ユーザー × エンドポイント種別）・メトリクス・不正検知の区分けに使う。
/// </summary>
public enum RequestKind
{
    /// <summary>種別なし（ヘルスチェック・OpenAPI 文書など。人の情報を返さない）。</summary>
    Other,

    /// <summary>人の個別の参照（1 人・上長）。</summary>
    PersonRead,

    /// <summary>人の一覧・検索・組織の所属者（大量に返し得る）。</summary>
    PersonList,

    /// <summary>組織の参照・組織ツリー・組織の検索・メタ情報（人の情報を返さない）。</summary>
    OrgRead,

    /// <summary>組織ごとの人数の集計（人は返さないが、閲覧者に見える人を数える。返却人数の制限とは別枠で回数を制限する）。</summary>
    OrgStats,

    /// <summary>変更フィード。</summary>
    Changes,

    /// <summary>管理操作（一時停止の解除など）。</summary>
    Admin,

    /// <summary>取込の作業領域（要確認の提案の参照・解決）。一時停止中のクライアントは使えない（管理操作の例外に含めない）。</summary>
    Ingest,

    /// <summary>同期計画（一覧・承認・スクリプトの取得）。一時停止中のクライアントは使えない（管理操作の例外に含めない）。</summary>
    Sync,

    /// <summary>修正依頼（起票・一覧・状態の更新・レポート）。一時停止中のクライアントは使えない（管理操作の例外に含めない）。</summary>
    Correction,
}

/// <summary>エンドポイントのメタデータ: 種別。</summary>
/// <param name="ReturnsPeople">人の情報を返すか（「返却した人数」の制限の対象）。</param>
public sealed record RequestKindMetadata(RequestKind Kind, bool ReturnsPeople);

/// <summary>コード値。</summary>
public static class RequestKinds
{
    public static string ToCode(RequestKind kind) => kind switch
    {
        RequestKind.Other => "other",
        RequestKind.PersonRead => "person_read",
        RequestKind.PersonList => "person_list",
        RequestKind.OrgRead => "org_read",
        RequestKind.OrgStats => "org_stats",
        RequestKind.Changes => "changes",
        RequestKind.Admin => "admin",
        RequestKind.Ingest => "ingest",
        RequestKind.Sync => "sync",
        RequestKind.Correction => "correction",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}

/// <summary>jin-ji 自身の UI（BFF の Cookie のセッション）のクライアント。</summary>
public static class UiClient
{
    /// <summary>UI のクライアント ID（予約語。設定のクライアントには使えない）。</summary>
    public const string Id = "jinji-ui";

    private const string KeyPrefix = Id + "/";

    /// <summary>UI のセッションのクライアントの段の単位（人ごと）。</summary>
    public static string KeyFor(Guid personId) => KeyPrefix + personId.ToString("D");

    /// <summary>
    /// メトリクスのラベル（person ID をラベルにしないため、UI の単位は UI のクライアント ID に、jin-ji が発行したトークンの委任元ごとの単位は
    /// クライアントの単位にまとめる。トークンエンドポイントの接続元アドレスは 1 つのラベルにまとめる）。
    /// </summary>
    public static string LabelOf(string clientKey)
    {
        ArgumentNullException.ThrowIfNull(clientKey);
        if (clientKey.StartsWith(KeyPrefix, StringComparison.Ordinal))
        {
            return Id;
        }

        if (clientKey.StartsWith(TokenEndpointAbuseKeys.Prefix, StringComparison.Ordinal))
        {
            return TokenEndpointAbuseKeys.Prefix.TrimEnd(':');
        }

        var separator = clientKey.IndexOf(Auth.ClientKeys.PersonSeparator, StringComparison.Ordinal);
        return separator < 0 ? clientKey : clientKey[..separator];
    }
}

/// <summary>拒否の理由（メトリクスの reason ラベル）。</summary>
public static class RejectReasons
{
    public const string Unauthenticated = "unauthenticated";
    public const string ClientRejected = "client_rejected";
    public const string Suspended = "suspended";
    public const string RateLimited = "rate_limited";
    public const string ReturnedPeopleLimited = "returned_people_limited";
    public const string NotFound = "not_found";
    public const string Forbidden = "forbidden";
    public const string BadRequest = "bad_request";
    public const string Conflict = "conflict";

    /// <summary>MCP の停止スイッチで止めている（503）。</summary>
    public const string McpDisabled = "mcp_disabled";

    /// <summary>要求の本文が大きすぎる（413）。</summary>
    public const string TooLarge = "too_large";
}

/// <summary>
/// 1 リクエストの閲覧者と観測値。入口の共通処理（Host）が作って HTTP のフィーチャーに置き、エンドポイントが観測値を書き込む。
/// 応答の後に、メトリクス・返却人数の制限・不正検知がこれを読む。
/// </summary>
public sealed class AccessContext
{
    /// <summary>閲覧者（認証済みで、閲覧者を作れたときだけ）。</summary>
    public ViewerContext? Viewer { get; set; }

    /// <summary>レート制限・不正検知の区分けに使うクライアント（未認証は null）。</summary>
    public string? ClientId { get; set; }

    /// <summary>
    /// クライアントの段のレート制限・不正検知の一時停止の単位（閲覧者を作れたときだけ）。トークンはクライアント ID、
    /// UI のセッションは人ごと（<see cref="UiClient.KeyFor"/>）。UI は全員が同じクライアントなので、1 人の振る舞いで UI 全体を止めないため。
    /// </summary>
    public string? ClientKey { get; set; }

    /// <summary>レート制限の区分けに使う利用者（人・委任元の ID。社内システムはクライアント自身）。</summary>
    public string? Subject { get; set; }

    /// <summary>返した人数（重複なし）。</summary>
    public int PeopleReturned { get; set; }

    /// <summary>絞り込みの無い一覧（全社横断の取得）だったか。</summary>
    public bool IsBroadListing { get; set; }

    /// <summary>拒否の理由（<see cref="RejectReasons"/>）。応答の状態コードから決まらないときにエンドポイントが書く。</summary>
    public string? RejectReason { get; set; }

    /// <summary>
    /// jin-ji が発行したトークン（参照トークン）のスコープ。null ならスコープで絞らない閲覧者（UI のセッション・外部の発行者の JWT）。
    /// 参照（GET・HEAD）は jinji.read、それ以外は jinji.propose が要る（入口の共通処理が確かめる）。
    /// </summary>
    public IReadOnlySet<string>? Scopes { get; set; }

    /// <summary>jin-ji が発行したトークンの検証の結果（MCP ツールの許可リストなど、閲覧者に入らない材料）。</summary>
    public Auth.ValidatedAccessToken? Token { get; set; }

    /// <summary>クライアントの登録で決めたクライアントの段のレート制限（null なら経路の設定の既定）。</summary>
    public int? ClientRateLimit { get; set; }

    /// <summary>
    /// 要求の種別（レート制限の段・返却人数・メトリクスの区分け）。null ならエンドポイントのメタデータ（<see cref="RequestKindMetadata"/>）。
    /// MCP は 1 つのエンドポイントで全ツールを受けるので、入口の共通処理が JSON-RPC の本文（ツール名）から決めてここに置く。
    /// </summary>
    public RequestKindMetadata? Kind { get; set; }

    /// <summary>
    /// メトリクス・不正検知に渡す状態コード（null なら応答の状態コード）。MCP はツールの誤り（見つからない・拒否）も HTTP では 200 で返すので、
    /// ツールの結果を REST の状態コードに読み替えた値を置く（403/404 の急増の検知を REST と揃えるため）。
    /// </summary>
    public int? EffectiveStatus { get; set; }
}
