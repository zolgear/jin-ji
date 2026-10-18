using System.Text.RegularExpressions;

namespace JinJi.Application.Auth;

/// <summary>
/// 人のログインの設定（appsettings の <c>JinJi:Auth:Login</c>。ADR-0004 の段階 A）。値は起動時に検査し、不正なら起動させない。
/// </summary>
public sealed partial class LoginOptions
{
    /// <summary>設定の節の名前。</summary>
    public const string SectionName = "JinJi:Auth:Login";

    /// <summary>ローカルアカウントのログイン方式の名前（セッションの provider）。外部 IdP の名前には使えない。</summary>
    public const string LocalProvider = "local";

    /// <summary>
    /// 利用者から見た jin-ji の公開 URL のオリジン（例 <c>https://jinji.example.co.jp</c>）。
    /// OIDC の redirect_uri・パスキーの origin の検証・リセットリンクの URL に使う（要求の Host ヘッダーからは推定しない）。
    /// https 必須（ループバックの http は開発・テスト用に許す）。外部 IdP・パスキー・リンクの URL を使うなら必須。
    /// </summary>
    public string? PublicOrigin { get; init; }

    /// <summary>パスキーの RP ID（<c>IdentityPasskeyOptions.ServerDomain</c>）。未設定ならパスキーを使わない（Host ヘッダーから推定させない）。</summary>
    public string? PasskeyServerDomain { get; init; }

    /// <summary>外部 IdP（Entra ID・汎用 OIDC）。複数を同時に有効にできる。</summary>
    public List<LoginProviderOptions> Providers { get; init; } = [];

    /// <summary>ローカルアカウントのパスワードでのログインを受け付けるか（既定 true。パスキーは <see cref="PasskeyServerDomain"/> で決まる）。</summary>
    public bool LocalPasswordEnabled { get; init; } = true;

    /// <summary>UI のセッション。</summary>
    public SessionLifetimeOptions Session { get; init; } = new();

    /// <summary>ロックアウト（Identity）。</summary>
    public LockoutPolicyOptions Lockout { get; init; } = new();

    /// <summary>ログインの試行のレート制限。</summary>
    public LoginRateLimitOptions RateLimit { get; init; } = new();

    /// <summary>パスワードの最小の長さ（既定 12。文字種の要件は Identity の既定）。</summary>
    public int PasswordMinLength { get; init; } = 12;

    /// <summary>リセットリンクの既定の有効期間（既定 1 時間、最長 24 時間）。</summary>
    public TimeSpan ResetLinkLifetime { get; init; } = TimeSpan.FromHours(1);

    /// <summary>値を検査する。</summary>
    public void Validate()
    {
        var origin = PublicOriginUri();
        if (Providers.Count > 0 && origin is null)
        {
            throw new ArgumentException("外部 IdP（JinJi:Auth:Login:Providers）を使うには JinJi:Auth:Login:PublicOrigin を設定してください。", nameof(PublicOrigin));
        }

        if (PasskeyServerDomain is not null)
        {
            if (origin is null)
            {
                throw new ArgumentException("パスキーを使うには JinJi:Auth:Login:PublicOrigin を設定してください（origin の検証に使います）。", nameof(PasskeyServerDomain));
            }

            if (!DomainPattern().IsMatch(PasskeyServerDomain)
                || !(string.Equals(origin.Host, PasskeyServerDomain, StringComparison.OrdinalIgnoreCase)
                     || origin.Host.EndsWith("." + PasskeyServerDomain, StringComparison.OrdinalIgnoreCase)))
            {
                throw new ArgumentException($"JinJi:Auth:Login:PasskeyServerDomain（{PasskeyServerDomain}）は PublicOrigin のホストかその上位のドメインにしてください。", nameof(PasskeyServerDomain));
            }
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var provider in Providers)
        {
            provider.Validate();
            if (!names.Add(provider.Name))
            {
                throw new ArgumentException($"ログイン方式の名前 {provider.Name} が重複しています。", nameof(Providers));
            }
        }

        Session.Validate();
        Lockout.Validate();
        RateLimit.Validate();
        if (PasswordMinLength is < 8 or > 128)
        {
            throw new ArgumentOutOfRangeException(nameof(PasswordMinLength), PasswordMinLength, "パスワードの最小の長さは 8〜128 で指定してください。");
        }

        if (ResetLinkLifetime <= TimeSpan.Zero || ResetLinkLifetime > TimeSpan.FromHours(24))
        {
            throw new ArgumentOutOfRangeException(nameof(ResetLinkLifetime), ResetLinkLifetime, "リセットリンクの有効期間は 24 時間以内で指定してください。");
        }
    }

    /// <summary>公開 URL のオリジン。未設定なら null。形が不正なら例外。</summary>
    public Uri? PublicOriginUri()
    {
        if (string.IsNullOrWhiteSpace(PublicOrigin))
        {
            return null;
        }

        if (!Uri.TryCreate(PublicOrigin, UriKind.Absolute, out var uri)
            || uri.PathAndQuery != "/" || !string.IsNullOrEmpty(uri.Fragment) || !string.IsNullOrEmpty(uri.UserInfo)
            || !(uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)))
        {
            throw new ArgumentException($"JinJi:Auth:Login:PublicOrigin（{PublicOrigin}）はパスを持たない https の URL にしてください（http はループバックだけ）。", nameof(PublicOrigin));
        }

        return uri;
    }

    /// <summary>公開 URL のオリジンの文字列（末尾の / なし。WebAuthn の origin と同じ形）。</summary>
    public string? OriginString() => PublicOriginUri()?.GetLeftPart(UriPartial.Authority);

    public LoginProviderOptions? FindProvider(string? name) =>
        name is null ? null : Providers.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.Ordinal));

    [GeneratedRegex("^(?=.{1,253}$)([a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?)(\\.[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?)*$")]
    private static partial Regex DomainPattern();
}

/// <summary>外部 IdP の種類。</summary>
public enum LoginProviderType
{
    /// <summary>Entra ID（シングルテナント。主体は tid + oid）。</summary>
    Entra,

    /// <summary>汎用 OIDC（Google Workspace・Keycloak・Okta など。主体は iss + sub）。</summary>
    Oidc,
}

/// <summary>外部 IdP の設定。</summary>
public sealed partial class LoginProviderOptions
{
    /// <summary>名前（URL の <c>/auth/login/{name}</c>・認証スキームの名前。英小文字で始まり英小文字・数字・<c>-</c>、32 文字まで。<c>local</c> は使えない）。</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>画面に出す名前。</summary>
    public string? DisplayName { get; init; }

    /// <summary>種類（<c>entra</c> / <c>oidc</c>）。</summary>
    public string Type { get; init; } = string.Empty;

    /// <summary>OIDC の Authority（発行者の URL）。Entra は省略でき、<c>https://login.microsoftonline.com/{TenantId}/v2.0</c> になる。</summary>
    public string? Authority { get; init; }

    /// <summary>発行者（id_token の iss）。省略すると Entra は Authority と同じ、汎用 OIDC も Authority と同じ。手動の紐付け（CLI）で使う。</summary>
    public string? Issuer { get; init; }

    /// <summary>Entra のテナント ID（Entra では必須。id_token の tid と照合する）。</summary>
    public string? TenantId { get; init; }

    public string ClientId { get; init; } = string.Empty;

    /// <summary>クライアントシークレット（環境変数 <c>JinJi__Auth__Login__Providers__0__ClientSecret</c> などで渡す）。公開クライアントなら省略。</summary>
    public string? ClientSecret { get; init; }

    /// <summary>要求するスコープ（openid は常に付ける）。</summary>
    public List<string> Scopes { get; init; } = ["profile", "email"];

    /// <summary>社員番号のクレームの名前（Entra は任意クレームで出した <c>employeeid</c> など）。省略すると社員番号では照合しない。</summary>
    public string? EmployeeNumberClaim { get; init; }

    /// <summary>社員番号のクレームを信頼して自動で紐付けるか（既定 false。IdP 側で利用者が値を変えられないことを確かめてから有効にする）。</summary>
    public bool TrustEmployeeNumberClaim { get; init; }

    /// <summary>社員番号を照合する法人のコード（社員番号は法人ごとに一意）。省略すると全法人で照合し、複数に当たれば紐付けない。</summary>
    public string? EmployeeNumberLegalEntity { get; init; }

    /// <summary>Entra の oid と照合する外部 ID の対応の名前（external_identity.source_system。既定 <c>entra_id</c>）。</summary>
    public string ExternalIdentitySource { get; init; } = "entra_id";

    public LoginProviderType ProviderType => Type switch
    {
        "entra" => LoginProviderType.Entra,
        "oidc" => LoginProviderType.Oidc,
        _ => throw new ArgumentException($"ログイン方式 {Name} の Type は entra か oidc にしてください: {Type}", nameof(Type)),
    };

    /// <summary>OIDC の Authority（Entra は既定を補う）。</summary>
    public string EffectiveAuthority => Authority ?? (ProviderType == LoginProviderType.Entra
        ? $"https://login.microsoftonline.com/{TenantId}/v2.0"
        : throw new InvalidOperationException($"ログイン方式 {Name} の Authority がありません。"));

    /// <summary>発行者（手動の紐付けで使う）。</summary>
    public string EffectiveIssuer => Issuer ?? EffectiveAuthority;

    /// <summary>値を検査する。</summary>
    public void Validate()
    {
        if (!NamePattern().IsMatch(Name) || Name == LoginOptions.LocalProvider)
        {
            throw new ArgumentException($"ログイン方式の名前 {Name} は英小文字で始まり英小文字・数字・- の 32 文字以内にしてください（local は使えません）。", nameof(Name));
        }

        _ = ProviderType;
        if (string.IsNullOrWhiteSpace(ClientId))
        {
            throw new ArgumentException($"ログイン方式 {Name} の ClientId がありません。", nameof(ClientId));
        }

        if (ProviderType == LoginProviderType.Entra && !Guid.TryParse(TenantId, out _))
        {
            throw new ArgumentException($"ログイン方式 {Name}（Entra）の TenantId はテナントの GUID にしてください（common・organizations は使えません）。", nameof(TenantId));
        }

        if (ProviderType == LoginProviderType.Oidc && string.IsNullOrWhiteSpace(Authority))
        {
            throw new ArgumentException($"ログイン方式 {Name}（汎用 OIDC）の Authority がありません。", nameof(Authority));
        }

        if (!Uri.TryCreate(EffectiveAuthority, UriKind.Absolute, out var authority) || !(authority.Scheme == Uri.UriSchemeHttps || authority.IsLoopback))
        {
            throw new ArgumentException($"ログイン方式 {Name} の Authority は https の URL にしてください。", nameof(Authority));
        }

        if (TrustEmployeeNumberClaim && string.IsNullOrWhiteSpace(EmployeeNumberClaim))
        {
            throw new ArgumentException($"ログイン方式 {Name} の TrustEmployeeNumberClaim には EmployeeNumberClaim が要ります。", nameof(EmployeeNumberClaim));
        }

        if (string.IsNullOrWhiteSpace(ExternalIdentitySource))
        {
            throw new ArgumentException($"ログイン方式 {Name} の ExternalIdentitySource が空です。", nameof(ExternalIdentitySource));
        }
    }

    [GeneratedRegex("^[a-z][a-z0-9-]{0,31}$")]
    private static partial Regex NamePattern();
}

/// <summary>UI のセッションの寿命（ADR-0004「セキュリティの既定値」）。</summary>
public sealed class SessionLifetimeOptions
{
    /// <summary>無操作の期限（既定 8 時間）。</summary>
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromHours(8);

    /// <summary>最長の期限（既定 12 時間。ログインからの時間）。</summary>
    public TimeSpan AbsoluteTimeout { get; init; } = TimeSpan.FromHours(12);

    /// <summary>最終利用日時を書き戻す間隔（既定 1 分。要求ごとに書かない）。</summary>
    public TimeSpan TouchInterval { get; init; } = TimeSpan.FromMinutes(1);

    public void Validate()
    {
        if (IdleTimeout <= TimeSpan.Zero || AbsoluteTimeout <= TimeSpan.Zero || IdleTimeout > AbsoluteTimeout || AbsoluteTimeout > TimeSpan.FromHours(24))
        {
            throw new ArgumentOutOfRangeException(nameof(AbsoluteTimeout), AbsoluteTimeout, "セッションの期限は 0 < 無操作 ≤ 最長 ≤ 24 時間で指定してください。");
        }

        if (TouchInterval < TimeSpan.Zero || TouchInterval >= IdleTimeout)
        {
            throw new ArgumentOutOfRangeException(nameof(TouchInterval), TouchInterval, "最終利用日時の書き戻しの間隔は無操作の期限より短くしてください。");
        }
    }
}

/// <summary>ロックアウト（Identity。既定 5 回の失敗で 15 分）。</summary>
public sealed class LockoutPolicyOptions
{
    public int MaxFailedAttempts { get; init; } = 5;

    public TimeSpan Duration { get; init; } = TimeSpan.FromMinutes(15);

    public void Validate()
    {
        if (MaxFailedAttempts is < 1 or > 100 || Duration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxFailedAttempts), MaxFailedAttempts, "ロックアウトの回数は 1〜100、期間は正の時間で指定してください。");
        }
    }
}

/// <summary>
/// ログインの試行のレート制限（窓あたりの回数。スライディングウィンドウ）。接続元アドレスごとの制限を先に効かせ、
/// 1 つの接続元からは 1 つのアカウントをロックアウトさせない（<see cref="PerAddressAndAccount"/> を Lockout の回数より小さくする）。
/// </summary>
public sealed class LoginRateLimitOptions
{
    public TimeSpan Window { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>接続元アドレスごと（既定 20 回）。</summary>
    public int PerAddress { get; init; } = 20;

    /// <summary>接続元アドレス × アカウント（ユーザー名）ごと（既定 4 回）。</summary>
    public int PerAddressAndAccount { get; init; } = 4;

    public void Validate()
    {
        if (Window <= TimeSpan.Zero || PerAddress < 1 || PerAddressAndAccount < 1 || PerAddressAndAccount > PerAddress)
        {
            throw new ArgumentOutOfRangeException(nameof(PerAddress), PerAddress, "ログインのレート制限は 1 ≤ アドレス × アカウント ≤ アドレスの回数で指定してください。");
        }
    }
}
