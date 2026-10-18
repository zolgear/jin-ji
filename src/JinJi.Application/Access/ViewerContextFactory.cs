using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using JinJi.Policy;

namespace JinJi.Application.Access;

/// <summary>トークンのクレームから閲覧者を作れなかった（未登録のクライアント・受け付けない操作者の種別など）。</summary>
public sealed class ViewerRejectedException : Exception
{
    public ViewerRejectedException()
    {
    }

    public ViewerRejectedException(string message)
        : base(message)
    {
    }

    public ViewerRejectedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// UI（BFF）のセッションの閲覧者のクレーム。Cookie の認証ハンドラが、要求ごとに DB のセッション・ロールから作る（Cookie には持たせない）。
/// </summary>
public static class SessionClaims
{
    /// <summary>認証の種類（ClaimsIdentity.AuthenticationType）。この種類の閲覧者だけをセッションとして扱う。</summary>
    public const string AuthenticationType = "jinji.session";

    /// <summary>主体（人は person ID、緊急用アカウントは <c>break_glass:&lt;アカウント ID&gt;</c>）。</summary>
    public const string Subject = "sub";

    /// <summary>種類（<c>person</c> / <c>break_glass</c>）。</summary>
    public const string Kind = "jinji_kind";

    public const string SessionId = "jinji_session";

    public const string AccountId = "jinji_account";

    public const string Provider = "jinji_provider";

    /// <summary>認証の方法（RFC 8176 の amr に倣う）。</summary>
    public const string Methods = "amr";

    public const string Roles = "roles";

    public const string PersonKind = "person";

    public const string BreakGlassKind = "break_glass";

    /// <summary>UI のセッションの閲覧者か。</summary>
    public static bool IsSession(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        return principal.Identity is { IsAuthenticated: true, AuthenticationType: AuthenticationType };
    }

    /// <summary>緊急用アカウントのセッションか。</summary>
    public static bool IsBreakGlass(ClaimsPrincipal principal) =>
        IsSession(principal) && principal.FindFirst(Kind)?.Value == BreakGlassKind;

    /// <summary>緊急用アカウントのセッションのアカウント。それ以外は null。</summary>
    public static Guid? BreakGlassAccountOf(ClaimsPrincipal principal) =>
        IsBreakGlass(principal) && Guid.TryParse(principal.FindFirst(AccountId)?.Value, out var id) ? id : null;
}

/// <summary>トークンのクレーム名。</summary>
public static class TokenClaims
{
    public const string Issuer = "iss";
    public const string Subject = "sub";

    /// <summary>クライアント ID（RFC 9068）。</summary>
    public const string ClientId = "client_id";

    /// <summary>認可された相手（OIDC。client_id が無いときに使う）。</summary>
    public const string AuthorizedParty = "azp";

    /// <summary>Entra ID の v1 トークンのアプリ ID（client_id が無いときに使う）。</summary>
    public const string AppId = "appid";

    /// <summary>委任の主体（RFC 8693 の act。AI エージェントのトークン）。</summary>
    public const string Actor = "act";

    /// <summary>ロール（発行者の設定 TrustRoles が true のときだけ使う）。</summary>
    public const string Roles = "roles";

    /// <summary>Entra ID のトークンの種別（<c>app</c> ならアプリのトークン = 社内システム。<c>sub</c> がサービスプリンシパルの UUID になるため）。</summary>
    public const string IdentityType = "idtyp";
}

/// <summary>
/// 検証済みのトークンのクレームから閲覧者（<see cref="ViewerContext"/>）を作る。REST・MCP の両方がこれを使う（対応付けはここだけ）。
/// </summary>
/// <remarks>
/// <list type="table">
/// <item><term>クライアント</term><description><c>client_id</c>（無ければ <c>azp</c>、<c>appid</c>）。<see cref="AccessOptions.Clients"/> に登録されていなければ拒否</description></item>
/// <item><term>AI エージェント</term><description><c>act</c> クレームがある（RFC 8693 の委任）。<c>sub</c> が委任元の person ID。エージェント単独の権限は持たない</description></item>
/// <item><term>社内システム</term><description><c>act</c> が無く、<c>idtyp</c> が <c>app</c>（Entra ID のアプリのトークン）か、<c>sub</c> が UUID でないかクライアント ID と同じ（Client Credentials）</description></item>
/// <item><term>人</term><description>上のどちらでもない（<c>sub</c> が person ID）</description></item>
/// <item><term>ロール</term><description>発行者の設定で信用する場合だけ <c>roles</c> を使い、既知のロールに限る。社内システムはロールを持たない</description></item>
/// <item><term>項目の上限</term><description>クライアントの登録から（トークンの値では決めない）</description></item>
/// </list>
/// 発行者・クライアントの両方がその操作者の種別を受け付けるときだけ通す。
/// </remarks>
public sealed class ViewerContextFactory
{
    private readonly AccessOptions _options;

    public ViewerContextFactory(AccessOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    /// <summary>クレームからクライアント ID を読む（閲覧者を作る前のレート制限の区分けにも使う）。無ければ null。</summary>
    public static string? ClientIdOf(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        return Value(principal, TokenClaims.ClientId) ?? Value(principal, TokenClaims.AuthorizedParty) ?? Value(principal, TokenClaims.AppId);
    }

    /// <summary>クレームから利用者の区分け（人・委任元の ID、無ければクライアント自身）を読む。</summary>
    public static string? SubjectOf(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        return Value(principal, TokenClaims.Subject);
    }

    /// <summary>
    /// UI のセッションから人の閲覧者を作る（経路は UI、クライアントは <see cref="UiClient.Id"/>、項目の上限なし）。
    /// ロールはセッションの検証が DB（role_grant）から読んだもので、既知のロールに限る。緊急用アカウントのセッションからは作らない（人事情報を見せない）。
    /// </summary>
    public static ViewerContext CreateForSession(ClaimsPrincipal principal, string? requestId)
    {
        ArgumentNullException.ThrowIfNull(principal);
        if (!SessionClaims.IsSession(principal) || principal.FindFirst(SessionClaims.Kind)?.Value != SessionClaims.PersonKind)
        {
            throw new ViewerRejectedException("人のセッションではありません。");
        }

        var roles = principal.FindAll(SessionClaims.Roles).Select(c => c.Value).Where(Roles.Known.Contains).Distinct(StringComparer.Ordinal).ToList();
        return ViewerContext.ForUser(ParsePerson(Value(principal, SessionClaims.Subject) ?? string.Empty), roles, Channel.Ui, FieldLimit.Unrestricted, UiClient.Id, requestId);
    }

    /// <summary>
    /// jin-ji が発行したトークン（参照トークン）から閲覧者を作る。認可コードのトークンは AI エージェント（委任元の人。ロールは要求ごとに role_grant から読んだもの）、
    /// client credentials のトークンは社内システム（REST API だけ）。項目の上限はクライアントの今の登録から。スコープは入口の共通処理が確かめる。
    /// </summary>
    public static ViewerContext CreateForAccessToken(Auth.ValidatedAccessToken token, Channel channel, string? requestId)
    {
        ArgumentNullException.ThrowIfNull(token);
        FieldLimit limit;
        try
        {
            limit = FieldLimit.ForClient(token.Fields);
        }
        catch (ArgumentException ex)
        {
            throw new ViewerRejectedException("クライアントの項目の上限が不正です。", ex);
        }

        if (token.PersonId is { } person)
        {
            var roles = token.Roles.Where(Roles.Known.Contains).Distinct(StringComparer.Ordinal).ToList();
            return ViewerContext.ForAgent(token.ClientId, person, roles, channel, limit, requestId);
        }

        // 先行提供の日数は社内システム（client credentials）にだけ効かせる（人の委任には適用しない。§4「将来の予定の秘匿」の例外）
        return channel == Channel.Api
            ? ViewerContext.ForService(token.ClientId, limit, requestId, token.ClientRef, token.DisclosureLeadDays)
            : throw new ViewerRejectedException("社内システムはこの経路を使えません。");
    }

    /// <summary>閲覧者を作る。作れなければ <see cref="ViewerRejectedException"/>（理由はログ用。応答には出さない）。</summary>
    public ViewerContext Create(ClaimsPrincipal principal, Channel channel, string? requestId)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var issuer = _options.FindIssuer(Value(principal, TokenClaims.Issuer))
            ?? throw new ViewerRejectedException("登録されていない発行者です。");
        var clientId = ClientIdOf(principal) ?? throw new ViewerRejectedException("クライアント ID のクレームがありません。");
        var client = _options.FindClient(clientId) ?? throw new ViewerRejectedException($"登録されていないクライアントです: {clientId}");
        var subject = SubjectOf(principal) ?? throw new ViewerRejectedException("sub クレームがありません。");

        var actor = ActorOf(principal, subject, clientId);
        if (!issuer.Allows(actor) || !client.Allows(actor))
        {
            throw new ViewerRejectedException($"この発行者・クライアントでは操作者の種別 {AccessCodes.ToCode(actor)} を受け付けません。");
        }

        var limit = client.FieldLimit();
        var roles = issuer.TrustRoles
            ? principal.FindAll(TokenClaims.Roles).Select(c => c.Value).Where(Roles.Known.Contains).Distinct(StringComparer.Ordinal).ToList()
            : [];

        try
        {
            return actor switch
            {
                ActorType.Agent => ViewerContext.ForAgent(clientId, ParsePerson(subject), roles, channel, limit, requestId),
                ActorType.User => ViewerContext.ForUser(ParsePerson(subject), roles, channel, limit, clientId, requestId),
                _ when channel == Channel.Api => ViewerContext.ForService(clientId, limit, requestId),
                _ => throw new ViewerRejectedException("社内システムはこの経路を使えません。"),
            };
        }
        catch (ArgumentException ex)
        {
            throw new ViewerRejectedException("トークンから閲覧者を作れません。", ex);
        }
    }

    private static ActorType ActorOf(ClaimsPrincipal principal, string subject, string clientId)
    {
        if (principal.FindFirst(TokenClaims.Actor) is { } act)
        {
            // act は JSON のオブジェクト（{"sub": "<エージェント>"}）。中身は監査に使わず、委任であることだけを見る
            if (!IsJsonObject(act.Value))
            {
                throw new ViewerRejectedException("act クレームの形が不正です。");
            }

            return ActorType.Agent;
        }

        if (string.Equals(Value(principal, TokenClaims.IdentityType), "app", StringComparison.Ordinal))
        {
            return ActorType.Service;
        }

        return !string.Equals(subject, clientId, StringComparison.Ordinal) && Guid.TryParse(subject, CultureInfo.InvariantCulture, out _)
            ? ActorType.User
            : ActorType.Service;
    }

    private static Guid ParsePerson(string subject) =>
        Guid.TryParse(subject, CultureInfo.InvariantCulture, out var id) ? id : throw new ViewerRejectedException("sub が person ID ではありません。");

    private static bool IsJsonObject(string value)
    {
        try
        {
            using var document = JsonDocument.Parse(value);
            return document.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? Value(ClaimsPrincipal principal, string type) =>
        principal.FindFirst(type)?.Value is { Length: > 0 } value ? value : null;
}
