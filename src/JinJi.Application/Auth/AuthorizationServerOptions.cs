using System.Text.RegularExpressions;
using JinJi.Policy;

namespace JinJi.Application.Auth;

/// <summary>
/// トークン発行（自前の最小の認可サーバー。ADR-0004 の段階 B）の設定（appsettings の <c>JinJi:Auth:Server</c>）。値は起動時に検査し、不正なら起動させない。
/// </summary>
/// <remarks>
/// <see cref="Issuer"/> が未設定なら、認可サーバー（<c>/oauth/*</c>・メタデータ）も参照トークンの受け付けも無効（API は従来の JWT だけ、MCP はどのトークンも受け付けない）。
/// </remarks>
public sealed partial class AuthorizationServerOptions
{
    /// <summary>設定の節の名前。</summary>
    public const string SectionName = "JinJi:Auth:Server";

    /// <summary>
    /// 発行者（RFC 8414 の issuer。API の公開 URL。例 <c>https://jinji.example.co.jp</c>）。パスを持たない https の URL（ループバックの http は開発・テスト用）。
    /// API の役割では人のログインの公開 URL（<c>JinJi:Auth:Login:PublicOrigin</c>）と同じにする（同意画面とログインが同じオリジンにあるため）。
    /// </summary>
    public string? Issuer { get; init; }

    /// <summary>REST API の resource（RFC 8707）。省略すると <c>{Issuer}/api/v1</c>。</summary>
    public string? ApiResource { get; init; }

    /// <summary>MCP サーバーの resource（MCP サーバーの正規の URI。例 <c>https://jinji.example.co.jp/mcp</c>）。未設定なら MCP 用のトークンを発行しない。</summary>
    public string? McpResource { get; init; }

    /// <summary>アクセストークンの寿命（既定 10 分。最長 1 時間）。</summary>
    public TimeSpan AccessTokenLifetime { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>リフレッシュトークンの無操作の期限（既定 7 日）。</summary>
    public TimeSpan RefreshTokenIdleLifetime { get; init; } = TimeSpan.FromDays(7);

    /// <summary>リフレッシュトークンの系列の最長の期限（既定 30 日。認可コードの付与から）。</summary>
    public TimeSpan RefreshTokenMaxLifetime { get; init; } = TimeSpan.FromDays(30);

    /// <summary>認可コードの寿命（既定 60 秒）。</summary>
    public TimeSpan AuthorizationCodeLifetime { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>認可の要求（同意画面・ログインの間）の寿命（既定 10 分）。</summary>
    public TimeSpan AuthorizationRequestLifetime { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>client assertion の exp の上限（既定 5 分。今からこの時間より先の exp は拒否する）。</summary>
    public TimeSpan ClientAssertionMaxLifetime { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>参照トークンの検証結果のメモリキャッシュの時間（既定 5 秒）。失効・退職・一時停止・クライアントの停止はこの秒数以内に反映される。</summary>
    public TimeSpan TokenCacheDuration { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>client_secret（移行用）の既定の有効期間（既定 90 日。最長 366 日）。</summary>
    public TimeSpan ClientSecretLifetime { get; init; } = TimeSpan.FromDays(90);

    /// <summary>有効期限・発効日時の検査で許す時計のずれ（client assertion。既定 30 秒）。</summary>
    public TimeSpan ClockSkew { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>CIMD（Client ID Metadata Document）。</summary>
    public CimdOptions Cimd { get; init; } = new();

    /// <summary>トークンエンドポイント（失効を含む）のレート制限。</summary>
    public TokenEndpointRateLimitOptions RateLimit { get; init; } = new();

    /// <summary>認可サーバーを有効にしたか。</summary>
    public bool Enabled => !string.IsNullOrWhiteSpace(Issuer);

    /// <summary>値を検査する。</summary>
    public void Validate()
    {
        if (!Enabled)
        {
            return;
        }

        var issuer = IssuerUri()!;
        _ = ResourceUri(ApiResourceValue, nameof(ApiResource));
        if (McpResource is not null)
        {
            _ = ResourceUri(McpResource, nameof(McpResource));
            if (string.Equals(Normalize(McpResource), Normalize(ApiResourceValue), StringComparison.Ordinal))
            {
                throw new ArgumentException("JinJi:Auth:Server の McpResource と ApiResource は別の URI にしてください（トークンを resource に束縛するため）。", nameof(McpResource));
            }
        }

        _ = issuer;
        Check(AccessTokenLifetime > TimeSpan.Zero && AccessTokenLifetime <= TimeSpan.FromHours(1), nameof(AccessTokenLifetime), "アクセストークンの寿命は 1 時間以内");
        Check(RefreshTokenIdleLifetime > TimeSpan.Zero && RefreshTokenIdleLifetime <= RefreshTokenMaxLifetime && RefreshTokenMaxLifetime <= TimeSpan.FromDays(366),
            nameof(RefreshTokenMaxLifetime), "リフレッシュトークンの期限は 0 < 無操作 ≤ 最長 ≤ 366 日");
        Check(AuthorizationCodeLifetime > TimeSpan.Zero && AuthorizationCodeLifetime <= TimeSpan.FromMinutes(10), nameof(AuthorizationCodeLifetime), "認可コードの寿命は 10 分以内");
        Check(AuthorizationRequestLifetime >= TimeSpan.FromMinutes(1) && AuthorizationRequestLifetime <= TimeSpan.FromHours(1), nameof(AuthorizationRequestLifetime), "認可の要求の寿命は 1 分〜1 時間");
        Check(ClientAssertionMaxLifetime > TimeSpan.Zero && ClientAssertionMaxLifetime <= TimeSpan.FromMinutes(10), nameof(ClientAssertionMaxLifetime), "client assertion の exp の上限は 10 分以内");
        Check(TokenCacheDuration >= TimeSpan.Zero && TokenCacheDuration <= TimeSpan.FromSeconds(60), nameof(TokenCacheDuration), "トークンのキャッシュは 0〜60 秒");
        Check(ClientSecretLifetime > TimeSpan.Zero && ClientSecretLifetime <= TimeSpan.FromDays(366), nameof(ClientSecretLifetime), "client_secret の有効期間は 366 日以内");
        Check(ClockSkew >= TimeSpan.Zero && ClockSkew <= TimeSpan.FromMinutes(5), nameof(ClockSkew), "時計のずれは 0〜5 分");
        Cimd.Validate();
        RateLimit.Validate();
    }

    /// <summary>発行者の URL（パスなし、末尾の / なし）。未設定なら null。</summary>
    public Uri? IssuerUri()
    {
        if (!Enabled)
        {
            return null;
        }

        if (!Uri.TryCreate(Issuer, UriKind.Absolute, out var uri)
            || uri.PathAndQuery != "/" || !string.IsNullOrEmpty(uri.Fragment) || !string.IsNullOrEmpty(uri.UserInfo) || Issuer!.EndsWith('/')
            || !(uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)))
        {
            throw new ArgumentException($"JinJi:Auth:Server:Issuer（{Issuer}）はパスと末尾の / を持たない https の URL にしてください（http はループバックだけ）。", nameof(Issuer));
        }

        return uri;
    }

    /// <summary>発行者の文字列（メタデータ・iss・client assertion の aud と完全に一致させる値）。</summary>
    public string IssuerValue => Issuer ?? throw new InvalidOperationException("認可サーバーが無効です（JinJi:Auth:Server:Issuer）。");

    public string ApiResourceValue => ApiResource ?? IssuerValue + "/api/v1";

    /// <summary>種類の resource の URI。MCP が未設定なら null。</summary>
    public string? ResourceValue(OAuthResource resource) => resource == OAuthResource.Mcp ? McpResource : ApiResourceValue;

    /// <summary>要求の resource（URI）を種類に読み替える。末尾の / の有無だけは同じとみなす。知らない URI なら null。</summary>
    public OAuthResource? ParseResourceUri(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        var normalized = Normalize(value);
        if (McpResource is not null && string.Equals(normalized, Normalize(McpResource), StringComparison.Ordinal))
        {
            return OAuthResource.Mcp;
        }

        return string.Equals(normalized, Normalize(ApiResourceValue), StringComparison.Ordinal) ? OAuthResource.Api : null;
    }

    /// <summary>要求で使えるスコープか。</summary>
    public static bool IsKnownScope(string scope) => OAuthScopes.All.Contains(scope);

    private static string Normalize(string uri) => uri.TrimEnd('/');

    private static Uri ResourceUri(string value, string name)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || !string.IsNullOrEmpty(uri.Fragment) || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !(uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)))
        {
            throw new ArgumentException($"JinJi:Auth:Server:{name}（{value}）はクエリ・フラグメントを持たない https の URL にしてください（http はループバックだけ）。", name);
        }

        return uri;
    }

    private static void Check(bool condition, string name, string message)
    {
        if (!condition)
        {
            throw new ArgumentOutOfRangeException(name, $"JinJi:Auth:Server:{name}: {message}で指定してください。");
        }
    }
}

/// <summary>
/// CIMD（Client ID Metadata Document）の設定。取得するのは許可したドメインと、管理者が承認したクライアントだけ（SSRF の対策）。
/// </summary>
public sealed partial class CimdOptions
{
    /// <summary>CIMD を受け付けるか（既定 true。false ならどの URL の client_id も拒否する）。</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>取得を許すドメイン（ホスト名の完全一致。既定の許可リスト。管理者は DB に足せる）。</summary>
    public List<string> AllowedDomains { get; init; } = [];

    /// <summary>文書の大きさの上限（既定 5 KiB。CIMD の仕様の目安）。</summary>
    public int MaxDocumentBytes { get; init; } = 5 * 1024;

    /// <summary>取得の時間の上限（既定 5 秒。接続から本文の読み終わりまで）。</summary>
    public TimeSpan FetchTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>HTTP キャッシュの指定が無いときに文書を使い回す時間（既定 1 時間）。</summary>
    public TimeSpan DefaultCacheDuration { get; init; } = TimeSpan.FromHours(1);

    /// <summary>HTTP キャッシュの指定を受け入れる上限（既定 24 時間）。</summary>
    public TimeSpan MaxCacheDuration { get; init; } = TimeSpan.FromHours(24);

    /// <summary>CIMD のクライアントに与えるスコープ（既定 jinji.read。クライアントごとに管理者が変えられる）。</summary>
    public List<string> DefaultScopes { get; init; } = [OAuthScopes.Read];

    /// <summary>CIMD のクライアントの項目の上限（既定 ["*"] = 上限なし。委任元の権限とスコープの範囲を超えることはない）。</summary>
    public List<string> DefaultFields { get; init; } = [PersonFields.Wildcard];

    /// <summary>CIMD のクライアントの MCP ツールの許可リスト（既定は空 = 何も許さない。<c>*</c> はすべて。名前は <see cref="Access.McpToolNames"/>）。</summary>
    public List<string> DefaultMcpTools { get; init; } = [];

    public void Validate()
    {
        foreach (var domain in AllowedDomains)
        {
            if (!IsDomain(domain))
            {
                throw new ArgumentException($"JinJi:Auth:Server:Cimd:AllowedDomains の {domain} はホスト名（小文字）にしてください。", nameof(AllowedDomains));
            }
        }

        if (MaxDocumentBytes is < 256 or > 64 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxDocumentBytes), MaxDocumentBytes, "CIMD の文書の上限は 256〜65536 バイトで指定してください。");
        }

        if (FetchTimeout <= TimeSpan.Zero || FetchTimeout > TimeSpan.FromSeconds(30)
            || DefaultCacheDuration < TimeSpan.Zero || MaxCacheDuration < DefaultCacheDuration || MaxCacheDuration > TimeSpan.FromDays(7))
        {
            throw new ArgumentOutOfRangeException(nameof(FetchTimeout), "CIMD の取得の時間は 30 秒以内、キャッシュは 0 ≤ 既定 ≤ 上限 ≤ 7 日で指定してください。");
        }

        if (DefaultScopes.Count == 0 || DefaultScopes.Any(s => !OAuthScopes.All.Contains(s)))
        {
            throw new ArgumentException("JinJi:Auth:Server:Cimd:DefaultScopes は jinji.read / jinji.propose から 1 つ以上指定してください。", nameof(DefaultScopes));
        }

        if (DefaultFields.Count == 0)
        {
            throw new ArgumentException("JinJi:Auth:Server:Cimd:DefaultFields を指定してください（上限なしは [\"*\"]）。", nameof(DefaultFields));
        }

        _ = FieldLimit.ForClient(DefaultFields);

        if (DefaultMcpTools.FirstOrDefault(t => !Access.McpToolNames.IsKnown(t)) is { } unknown)
        {
            throw new ArgumentException($"JinJi:Auth:Server:Cimd:DefaultMcpTools の {unknown} は MCP ツールの名前ではありません（すべては *）。", nameof(DefaultMcpTools));
        }
    }

    /// <summary>ホスト名（小文字・ドットを含む）か。</summary>
    public static bool IsDomain(string? value) => value is { Length: <= 253 } && DomainPattern().IsMatch(value);

    [GeneratedRegex("^([a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?)(\\.[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?)+$")]
    private static partial Regex DomainPattern();
}

/// <summary>
/// トークンエンドポイントのレート制限（スライディングウィンドウ）。接続元アドレスを先に数え、次に接続元 × client_id（ADR-0004「ブルートフォース」）。
/// </summary>
public sealed class TokenEndpointRateLimitOptions
{
    public TimeSpan Window { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>接続元アドレスごと（既定 120 回）。</summary>
    public int PerAddress { get; init; } = 120;

    /// <summary>接続元アドレス × client_id ごと（既定 30 回）。</summary>
    public int PerAddressAndClient { get; init; } = 30;

    /// <summary>不正検知で自動スロットル中の接続元アドレスの回数（既定 10 回）。</summary>
    public int ThrottledPerAddress { get; init; } = 10;

    public void Validate()
    {
        if (Window <= TimeSpan.Zero || PerAddress < 1 || PerAddressAndClient < 1 || PerAddressAndClient > PerAddress || ThrottledPerAddress < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(PerAddress), PerAddress, "トークンエンドポイントのレート制限は 1 ≤ アドレス × client_id ≤ アドレスの回数で指定してください。");
        }
    }
}
