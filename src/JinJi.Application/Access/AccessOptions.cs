using JinJi.Policy;

namespace JinJi.Application.Access;

/// <summary>
/// トークンの検証と、トークンから閲覧者を作る規則の設定（appsettings の <c>JinJi:Auth</c>）。
/// </summary>
/// <remarks>
/// <para>
/// jin-ji の API・MCP はトークン（JWT）を検証するだけで、発行はしない（発行の方式は ADR-0004 で決める）。
/// 発行者は複数登録でき、発行者ごとに audience・署名の公開鍵・受け付ける操作者の種別・ロールを信用するかを決める。
/// </para>
/// <para>
/// クライアント（社内システム・AI エージェント・UI）は <see cref="Clients"/> に登録したものだけを受け付ける。
/// 渡してよい項目の上限（§5.2）はトークンではなく、この登録で決める（トークンの値で上限を広げさせない）。
/// </para>
/// </remarks>
public sealed class AccessOptions
{
    /// <summary>設定の節の名前。</summary>
    public const string SectionName = "JinJi:Auth";

    /// <summary>受け付ける発行者。空なら、どのトークンも受け付けない（API の認証が要るエンドポイントはすべて 401）。</summary>
    public List<TokenIssuerOptions> Issuers { get; init; } = [];

    /// <summary>登録済みのクライアント。</summary>
    public List<ApiClientOptions> Clients { get; init; } = [];

    /// <summary>有効期限・発効日時の検査で許す時計のずれ（既定 30 秒）。</summary>
    public TimeSpan ClockSkew { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>値を検査する。不正なら起動させない。</summary>
    public void Validate()
    {
        if (ClockSkew < TimeSpan.Zero || ClockSkew > TimeSpan.FromMinutes(5))
        {
            throw new ArgumentOutOfRangeException(nameof(ClockSkew), ClockSkew, "時計のずれは 0〜5 分で指定してください。");
        }

        var issuers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var issuer in Issuers)
        {
            issuer.Validate();
            if (!issuers.Add(issuer.Issuer))
            {
                throw new ArgumentException($"発行者 {issuer.Issuer} が重複しています。", nameof(Issuers));
            }
        }

        var clients = new HashSet<string>(StringComparer.Ordinal);
        foreach (var client in Clients)
        {
            client.Validate();
            if (!clients.Add(client.ClientId))
            {
                throw new ArgumentException($"クライアント {client.ClientId} が重複しています。", nameof(Clients));
            }
        }
    }

    /// <summary>発行者の設定。無ければ null。</summary>
    public TokenIssuerOptions? FindIssuer(string? issuer) =>
        issuer is null ? null : Issuers.FirstOrDefault(i => string.Equals(i.Issuer, issuer, StringComparison.Ordinal));

    /// <summary>クライアントの登録。無ければ null。</summary>
    public ApiClientOptions? FindClient(string? clientId) =>
        clientId is null ? null : Clients.FirstOrDefault(c => string.Equals(c.ClientId, clientId, StringComparison.Ordinal));
}

/// <summary>トークンの発行者の設定。</summary>
public sealed class TokenIssuerOptions
{
    /// <summary>発行者（iss の値と完全に一致させる）。</summary>
    public string Issuer { get; init; } = string.Empty;

    /// <summary>受け付ける audience（aud）。1 つ以上。</summary>
    public List<string> Audiences { get; init; } = [];

    /// <summary>署名の検証に使う公開鍵（RSA か EC の SubjectPublicKeyInfo の PEM）。1 つ以上。鍵の入れ替えのため複数持てる。</summary>
    public List<SigningKeyOptions> SigningKeys { get; init; } = [];

    /// <summary>この発行者のトークンで受け付ける操作者の種別（<c>user</c> / <c>service</c> / <c>agent</c>）。1 つ以上。</summary>
    public List<string> ActorTypes { get; init; } = [];

    /// <summary>
    /// トークンのロール（<c>roles</c> クレーム）を信用するか（既定 false）。jin-ji 自身の発行者のように、ロールの付与を jin-ji が管理する発行者だけ true にする。
    /// 外部の IdP のロールをそのまま人事管理者として扱わないため。
    /// </summary>
    public bool TrustRoles { get; init; }

    /// <summary>値を検査する。</summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Issuer))
        {
            throw new ArgumentException("発行者（Issuer）が空です。", nameof(Issuer));
        }

        if (Audiences.Count == 0 || Audiences.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException($"発行者 {Issuer} の Audiences を 1 つ以上指定してください。", nameof(Audiences));
        }

        if (SigningKeys.Count == 0)
        {
            throw new ArgumentException($"発行者 {Issuer} の SigningKeys を 1 つ以上指定してください。", nameof(SigningKeys));
        }

        foreach (var key in SigningKeys)
        {
            if (string.IsNullOrWhiteSpace(key.PublicKeyPem))
            {
                throw new ArgumentException($"発行者 {Issuer} の署名鍵（PublicKeyPem）が空です。", nameof(SigningKeys));
            }
        }

        if (ActorTypes.Count == 0)
        {
            throw new ArgumentException($"発行者 {Issuer} の ActorTypes を 1 つ以上指定してください（user / service / agent）。", nameof(ActorTypes));
        }

        foreach (var type in ActorTypes)
        {
            _ = AccessCodes.ParseActorType(type);
        }
    }

    /// <summary>操作者の種別を受け付けるか。</summary>
    public bool Allows(ActorType actor) => ActorTypes.Any(t => AccessCodes.ParseActorType(t) == actor);
}

/// <summary>署名の公開鍵。</summary>
public sealed class SigningKeyOptions
{
    /// <summary>鍵 ID（JWT のヘッダーの kid と照合する。省略したらこの発行者の全鍵で試す）。</summary>
    public string? KeyId { get; init; }

    /// <summary>公開鍵の PEM（<c>-----BEGIN PUBLIC KEY-----</c>）。</summary>
    public string PublicKeyPem { get; init; } = string.Empty;
}

/// <summary>登録済みのクライアント（社内システム・AI エージェント・UI）。</summary>
public sealed class ApiClientOptions
{
    /// <summary>クライアント ID（トークンの <c>client_id</c>、無ければ <c>azp</c> / <c>appid</c> と照合する）。</summary>
    public string ClientId { get; init; } = string.Empty;

    /// <summary>このクライアントで受け付ける操作者の種別（<c>user</c> / <c>service</c> / <c>agent</c>）。1 つ以上。</summary>
    public List<string> ActorTypes { get; init; } = [];

    /// <summary>渡してよい項目の上限（<see cref="PersonFields"/> の名前）。<c>["*"]</c> は上限なし（jin-ji 自身の UI など。明示的に選ぶ）。</summary>
    public List<string> Fields { get; init; } = [];

    /// <summary>値を検査する。</summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ClientId))
        {
            throw new ArgumentException("クライアント ID が空です。", nameof(ClientId));
        }

        if (string.Equals(ClientId, UiClient.Id, StringComparison.Ordinal))
        {
            throw new ArgumentException($"クライアント ID {UiClient.Id} は jin-ji 自身の UI の予約語です。", nameof(ClientId));
        }

        if (ActorTypes.Count == 0)
        {
            throw new ArgumentException($"クライアント {ClientId} の ActorTypes を 1 つ以上指定してください。", nameof(ActorTypes));
        }

        foreach (var type in ActorTypes)
        {
            _ = AccessCodes.ParseActorType(type);
        }

        if (Fields.Count == 0)
        {
            throw new ArgumentException($"クライアント {ClientId} の Fields を指定してください（上限なしは [\"*\"]）。", nameof(Fields));
        }

        _ = FieldLimit();
    }

    /// <summary>操作者の種別を受け付けるか。</summary>
    public bool Allows(ActorType actor) => ActorTypes.Any(t => AccessCodes.ParseActorType(t) == actor);

    /// <summary>項目の上限。未知の項目名なら例外。</summary>
    public FieldLimit FieldLimit() =>
        Fields is [PersonFields.Wildcard] ? Policy.FieldLimit.Unrestricted : Policy.FieldLimit.Of(Fields);
}

/// <summary>設定・トークンのコード値。</summary>
public static class AccessCodes
{
    /// <summary>操作者の種別のコード値（user / service / agent）。jin-ji 自身（system）はトークンでは表せない。</summary>
    public static ActorType ParseActorType(string? code) => code switch
    {
        "user" => ActorType.User,
        "service" => ActorType.Service,
        "agent" => ActorType.Agent,
        _ => throw new ArgumentException($"未知の操作者の種別です: {code}（user / service / agent）", nameof(code)),
    };

    public static string ToCode(ActorType actor) => actor switch
    {
        ActorType.User => "user",
        ActorType.Service => "service",
        ActorType.Agent => "agent",
        ActorType.System => "system",
        ActorType.BreakGlass => "break_glass",
        _ => throw new ArgumentOutOfRangeException(nameof(actor)),
    };

    public static string ToCode(Channel channel) => channel switch
    {
        Channel.Ui => "ui",
        Channel.Api => "api",
        Channel.Mcp => "mcp",
        Channel.Worker => "worker",
        Channel.Cli => "cli",
        _ => throw new ArgumentOutOfRangeException(nameof(channel)),
    };
}
