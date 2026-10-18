using System.Globalization;
using System.Text.Json;
using JinJi.Api;
using JinJi.Application.Access;
using JinJi.Application.Audit;
using JinJi.Application.Auth;
using JinJi.Host.Web;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;

namespace JinJi.Host.Auth;

/// <summary>
/// 人のログインの JSON API（<c>/auth</c>。API のポートだけ）。画面は SPA が持ち、ここは SPA から呼ぶ。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>外部 IdP: <c>GET /auth/login/{provider}</c> で IdP へ転送し、コールバック（<c>/auth/callback/{provider}</c>）は OIDC ハンドラが受ける</item>
/// <item>ローカル: パスワード（→ TOTP / リカバリコード）、パスキー。どれもログインのたびに新しいセッション（Cookie）を作る</item>
/// <item>状態を変える要求（POST）はすべて CSRF のトークン（ヘッダー <c>X-JinJi-CSRF</c>）が要る（<see cref="CsrfMiddleware"/>）。トークンは <c>GET /auth/csrf</c></item>
/// <item>失敗の応答は理由を区別しない（401 <c>urn:jinji:problem:login-failed</c>）。理由は監査ログ・ログだけに残す</item>
/// <item>認証の管理（<c>/auth/admin</c>）は UI のセッションの人事管理者と緊急用アカウントだけ（トークンでは使えない）</item>
/// </list>
/// </remarks>
internal static class AuthEndpoints
{
    public const string Prefix = "/auth";

    public const string LoginFailedType = "urn:jinji:problem:login-failed";
    public const string MfaRequiredType = "urn:jinji:problem:mfa-required";
    public const string UnavailableType = "urn:jinji:problem:unavailable";
    public const string InvalidLinkType = "urn:jinji:problem:invalid-link";
    public const string LastFactorType = "urn:jinji:problem:last-factor";

    public static IEndpointRouteBuilder MapJinJiAuth(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var auth = endpoints.MapGroup(Prefix)
            .WithTags("auth")
            .WithMetadata(new RequestKindMetadata(RequestKind.Other, false));

        auth.MapGet("/providers", Providers).AllowAnonymous().WithMetadata(BreakGlassAllowedMetadata.Instance)
            .WithSummary("使えるログイン方式").Produces<LoginMethodsResponse>();
        auth.MapGet("/csrf", Csrf).AllowAnonymous().WithMetadata(BreakGlassAllowedMetadata.Instance)
            .WithSummary("CSRF の要求トークン（状態を変える要求の X-JinJi-CSRF ヘッダーに付ける）").Produces<CsrfResponse>();
        auth.MapGet("/me", Me).AllowAnonymous().WithMetadata(BreakGlassAllowedMetadata.Instance)
            .WithSummary("ログイン中のセッション（UI の Cookie のセッションだけ）").Produces<SessionResponse>().ProducesProblem(StatusCodes.Status401Unauthorized);
        auth.MapPost("/logout", LogoutAsync).AllowAnonymous().WithMetadata(BreakGlassAllowedMetadata.Instance)
            .WithSummary("ログアウト（セッションを終える）").Produces(StatusCodes.Status204NoContent);

        // 外部 IdP（ブラウザの遷移。JSON ではない）
        auth.MapGet("/login/{provider}", Challenge).AllowAnonymous().ExcludeFromDescription();

        // ローカルアカウント
        auth.MapPost("/login/password", PasswordAsync).AllowAnonymous()
            .WithSummary("パスワードでログイン（TOTP が有効なら two_factor_required）").Produces<LoginResponse>().ProducesProblem(StatusCodes.Status401Unauthorized);
        auth.MapPost("/login/two-factor", TwoFactorAsync).AllowAnonymous()
            .WithSummary("TOTP かリカバリコードでログインを完了する").Produces<LoginResponse>().ProducesProblem(StatusCodes.Status401Unauthorized);
        auth.MapPost("/login/passkey/options", PasskeyOptionsAsync).AllowAnonymous()
            .WithSummary("パスキーでのログインの options（navigator.credentials.get に渡す JSON）").Produces<JsonElement>();
        auth.MapPost("/login/passkey", PasskeyAsync).AllowAnonymous()
            .WithSummary("パスキーでログイン").Produces<LoginResponse>().ProducesProblem(StatusCodes.Status401Unauthorized);

        // ログイン中の本人のローカルアカウント
        auth.MapPost("/account/passkeys/options", AccountPasskeyOptionsAsync).RequireAuthorization()
            .WithSummary("パスキーの登録の options（navigator.credentials.create に渡す JSON）").Produces<JsonElement>();
        auth.MapPost("/account/passkeys", AccountPasskeyAsync).RequireAuthorization()
            .WithSummary("パスキーを登録する").Produces(StatusCodes.Status204NoContent);
        auth.MapGet("/account/passkeys", ListPasskeysAsync).RequireAuthorization()
            .WithSummary("登録したパスキーの一覧（ローカルアカウントでログイン中の本人）").Produces<IReadOnlyList<PasskeyResponse>>();
        auth.MapPost("/account/passkeys/{credentialId}/delete", DeletePasskeyAsync).RequireAuthorization()
            .WithSummary("パスキーを削除する（最後の 1 つでログインの手段か管理系のロールの多要素が無くなる場合は 409）")
            .Produces(StatusCodes.Status204NoContent).ProducesProblem(StatusCodes.Status409Conflict);
        auth.MapPost("/account/two-factor/setup", TwoFactorSetupAsync).RequireAuthorization()
            .WithSummary("TOTP の共有鍵を作り直す（有効にするのは enable で確かめてから）").Produces<TwoFactorSetupResponse>();
        auth.MapPost("/account/two-factor/enable", TwoFactorEnableAsync).RequireAuthorization()
            .WithSummary("TOTP を有効にしてリカバリコードを受け取る").Produces<RecoveryCodesResponse>();

        // 管理者が発行したリンク
        auth.MapPost("/reset/password", ResetPasswordAsync).AllowAnonymous()
            .WithSummary("リンクでパスワードを設定する").Produces(StatusCodes.Status204NoContent);
        auth.MapPost("/reset/passkey/options", ResetPasskeyOptionsAsync).AllowAnonymous()
            .WithSummary("リンクでのパスキーの登録の options").Produces<JsonElement>();
        auth.MapPost("/reset/passkey", ResetPasskeyAsync).AllowAnonymous()
            .WithSummary("リンクでパスキーを登録する").Produces(StatusCodes.Status204NoContent);

        AuthAdminEndpoints.Map(auth.MapGroup("/admin").RequireAuthorization().WithMetadata(new RequestKindMetadata(RequestKind.Admin, false), BreakGlassAllowedMetadata.Instance));
        return endpoints;
    }

    private static IResult Providers(LoginOptions options, LocalLoginService local) =>
        Results.Ok(new LoginMethodsResponse(
            [.. options.Providers.Select(p => new LoginProviderResponse(p.Name, p.DisplayName ?? p.Name, p.Type, $"{Prefix}/login/{p.Name}"))],
            options.LocalPasswordEnabled,
            local.PasskeysEnabled));

    private static IResult Csrf(HttpContext http, IAntiforgery antiforgery) =>
        http.Request.IsHttps
            ? Results.Ok(new CsrfResponse(antiforgery.GetAndStoreTokens(http).RequestToken!, CsrfMiddleware.HeaderName))
            : Results.Problem(
                statusCode: StatusCodes.Status404NotFound,
                title: "使えません",
                detail: "UI のログインは https でだけ使えます（JinJi:Auth:Login:PublicOrigin を https の公開 URL にしてください）。",
                type: UnavailableType);

    private static IResult Me(HttpContext http) =>
        SessionClaims.IsSession(http.User) ? Results.Ok(SessionResponse.From(http.User)) : Unauthorized(http);

    private static async Task<IResult> LogoutAsync(HttpContext http, AuthAuditor audit)
    {
        if (SessionClaims.IsSession(http.User))
        {
            var account = Guid.TryParse(http.User.FindFirst(SessionClaims.AccountId)?.Value, CultureInfo.InvariantCulture, out var a) ? a : (Guid?)null;
            var person = SessionClaims.IsBreakGlass(http.User) ? null : Guid.TryParse(http.User.FindFirst(SessionClaims.Subject)?.Value, CultureInfo.InvariantCulture, out var p) ? p : (Guid?)null;
            await http.SignOutAsync(AuthSchemes.Session);
            await audit.RecordAsync(
                LoginAudit.Entry(http, AuditOutcome.Success, SessionClaims.IsBreakGlass(http.User), person, account, null) with { Action = AuthAuditActions.Logout },
                http.RequestAborted);
        }

        return Results.NoContent();
    }

    private static IResult Challenge(string provider, [Microsoft.AspNetCore.Mvc.FromQuery(Name = "return_url")] string? returnUrl, LoginOptions options) =>
        options.FindProvider(provider) is null
            ? Results.NotFound()
            : Results.Challenge(new AuthenticationProperties { RedirectUri = SafeReturnUrl(returnUrl) }, [provider]);

    private static async Task<IResult> PasswordAsync(PasswordLoginRequest request, HttpContext http, LocalLoginService local) =>
        ToResult(http, await local.PasswordAsync(http, request.UserName, request.Password, http.RequestAborted));

    private static async Task<IResult> TwoFactorAsync(TwoFactorLoginRequest request, HttpContext http, LocalLoginService local) =>
        ToResult(http, await local.TwoFactorAsync(http, request.Code, request.RecoveryCode, http.RequestAborted));

    private static async Task<IResult> PasskeyOptionsAsync(HttpContext http, LocalLoginService local)
    {
        var (status, json, wait) = await local.PasskeyLoginOptionsAsync(http);
        return status switch
        {
            LocalLoginStatus.PasskeyUnavailable => Unavailable(http),
            LocalLoginStatus.Throttled => Throttled(http, wait),
            _ => Results.Content(json, "application/json"),
        };
    }

    private static async Task<IResult> PasskeyAsync(PasskeyCredentialRequest request, HttpContext http, LocalLoginService local) =>
        request.Credential.ValueKind != JsonValueKind.Object
            ? Problems.BadRequest(http, "credential に navigator.credentials.get の結果（JSON）を指定してください。")
            : ToResult(http, await local.PasskeyLoginAsync(http, request.Credential.GetRawText(), http.RequestAborted));

    private static async Task<IResult> AccountPasskeyOptionsAsync(HttpContext http, LocalLoginService local)
    {
        if (!local.PasskeysEnabled)
        {
            return Unavailable(http);
        }

        return await local.CurrentAccountAsync(http) is { } account
            ? Results.Content(await local.PasskeyRegistrationOptionsAsync(http, account, PendingStep.PasskeyRegistration, null), "application/json")
            : NoLocalAccount(http);
    }

    private static async Task<IResult> AccountPasskeyAsync(PasskeyCredentialRequest request, HttpContext http, LocalLoginService local)
    {
        if (await local.CurrentAccountAsync(http) is not { } account)
        {
            return NoLocalAccount(http);
        }

        return request.Credential.ValueKind == JsonValueKind.Object
               && await local.CompletePasskeyRegistrationAsync(http, account, request.Credential.GetRawText(), request.Name, http.RequestAborted)
            ? Results.NoContent()
            : Problems.BadRequest(http, "パスキーを登録できませんでした。登録の options を取り直してやり直してください。");
    }

    private static async Task<IResult> ListPasskeysAsync(HttpContext http, LocalLoginService local)
    {
        if (await local.CurrentAccountAsync(http) is not { } account)
        {
            return NoLocalAccount(http);
        }

        var passkeys = await local.ListPasskeysAsync(account, http.RequestAborted);
        return Results.Ok(passkeys.Select(PasskeyResponse.From).ToList());
    }

    private static async Task<IResult> DeletePasskeyAsync(string credentialId, HttpContext http, LocalLoginService local)
    {
        if (await local.CurrentAccountAsync(http) is not { } account)
        {
            return NoLocalAccount(http);
        }

        if (!PasskeyResponse.TryParseId(credentialId, out var id))
        {
            return Problems.NotFound(http);
        }

        return await local.DeletePasskeyAsync(http, account, id, http.RequestAborted) switch
        {
            PasskeyDeletion.Deleted => Results.NoContent(),
            PasskeyDeletion.NotFound => Problems.NotFound(http),
            PasskeyDeletion.LastCredential => LastFactor(http, "最後のパスキーは削除できません（パスワードが無いので、ログインできなくなります）。先に別のパスキーを登録してください。"),
            _ => LastFactor(http, "管理者の権限を持つアカウントは、パスキーか認証アプリ（確認コード）が必要です。最後のパスキーを削除する前に、別のパスキーを登録するか認証アプリを設定してください。"),
        };
    }

    private static async Task<IResult> TwoFactorSetupAsync(HttpContext http, LocalLoginService local)
    {
        if (await local.CurrentAccountAsync(http) is not { } account)
        {
            return NoLocalAccount(http);
        }

        var (key, uri) = await local.ResetAuthenticatorAsync(account);
        return Results.Ok(new TwoFactorSetupResponse(key, uri));
    }

    private static async Task<IResult> TwoFactorEnableAsync(EnableTwoFactorRequest request, HttpContext http, LocalLoginService local)
    {
        if (await local.CurrentAccountAsync(http) is not { } account)
        {
            return NoLocalAccount(http);
        }

        return await local.EnableTwoFactorAsync(http, account, request.Code, http.RequestAborted) is { } codes
            ? Results.Ok(new RecoveryCodesResponse(codes))
            : Problems.BadRequest(http, "確認の符号が合いません。認証アプリの 6 桁の符号を入力してください。");
    }

    private static async Task<IResult> ResetPasswordAsync(ResetPasswordRequest request, HttpContext http, LocalLoginService local)
    {
        var (throttled, wait, errors) = await local.ResetPasswordAsync(http, request.Token, request.NewPassword, http.RequestAborted);
        if (throttled)
        {
            return Throttled(http, wait);
        }

        return errors is null
            ? Results.NoContent()
            : Problems.BadRequest(http, string.Join(" ", errors), InvalidLinkType);
    }

    private static async Task<IResult> ResetPasskeyOptionsAsync(ResetPasskeyOptionsRequest request, HttpContext http, LocalLoginService local)
    {
        if (!local.PasskeysEnabled)
        {
            return Unavailable(http);
        }

        if (local.ThrottleFor(http).RetryAfter is { } wait)
        {
            return Throttled(http, wait);
        }

        return await local.ResetPasskeyTargetAsync(http, request.Token, http.RequestAborted) is var (link, account)
            ? Results.Content(await local.PasskeyRegistrationOptionsAsync(http, account, PendingStep.ResetPasskey, link.LinkId), "application/json")
            : InvalidLink(http);
    }

    private static async Task<IResult> ResetPasskeyAsync(ResetPasskeyRequest request, HttpContext http, LocalLoginService local)
    {
        if (!local.PasskeysEnabled)
        {
            return Unavailable(http);
        }

        if (local.ThrottleFor(http).RetryAfter is { } wait)
        {
            return Throttled(http, wait);
        }

        if (request.Credential.ValueKind != JsonValueKind.Object
            || await local.ResetPasskeyTargetAsync(http, request.Token, http.RequestAborted) is not var (link, account))
        {
            return InvalidLink(http);
        }

        return await local.ResetPasskeyAsync(http, request.Token!, link, account, request.Credential.GetRawText(), request.Name, http.RequestAborted)
            ? Results.NoContent()
            : InvalidLink(http);
    }

    // ---- 応答 ----------------------------------------------------------------------------------------------------

    private static IResult ToResult(HttpContext http, LocalLoginResult result) => result.Status switch
    {
        LocalLoginStatus.SignedIn => Results.Ok(new LoginResponse(LoginStatus.SignedIn, SessionResponse.From(http.User), result.Session!.CsrfToken)),
        LocalLoginStatus.TwoFactorRequired => Results.Ok(new LoginResponse(LoginStatus.TwoFactorRequired, null, null)),
        LocalLoginStatus.Throttled => Throttled(http, result.RetryAfter),
        LocalLoginStatus.MfaRequired => Results.Problem(
            statusCode: StatusCodes.Status403Forbidden,
            title: "多要素の認証が必要です",
            detail: "管理系のロールを持つアカウントはパスキーか、パスワードと認証アプリ（TOTP）でログインしてください。パスキーの登録リンクは管理者に発行を依頼してください。",
            type: MfaRequiredType),
        LocalLoginStatus.PasskeyUnavailable => Unavailable(http),
        _ => Failed(http),
    };

    private static IResult Failed(HttpContext http)
    {
        Problems.Observe(http).RejectReason = RejectReasons.Unauthenticated;
        return Results.Problem(
            statusCode: StatusCodes.Status401Unauthorized,
            title: "ログインできません",
            detail: "ユーザー名・パスワード・確認の符号のいずれかが違うか、このアカウントは現在ログインできません。",
            type: LoginFailedType);
    }

    private static IResult Unauthorized(HttpContext http)
    {
        Problems.Observe(http).RejectReason = RejectReasons.Unauthenticated;
        return Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "ログインしていません", detail: "ログインしてください。", type: LoginFailedType);
    }

    private static IResult Throttled(HttpContext http, TimeSpan? wait)
    {
        Problems.Observe(http).RejectReason = RejectReasons.RateLimited;
        http.Response.Headers.RetryAfter = AccessGuardMiddleware.Seconds(wait ?? TimeSpan.FromMinutes(1));
        return Results.Problem(
            statusCode: StatusCodes.Status429TooManyRequests,
            title: "試行が多すぎます",
            detail: "ログインの試行の上限に達しました。Retry-After の秒数の後に再試行してください。",
            type: Problems.RateLimitedType);
    }

    private static IResult Unavailable(HttpContext http) =>
        Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "使えません", detail: "この方式は設定で有効になっていません。", type: UnavailableType);

    private static IResult InvalidLink(HttpContext http) =>
        Problems.BadRequest(http, "リンクが無効か、期限が切れています。管理者に新しいリンクの発行を依頼してください。", InvalidLinkType);

    private static IResult LastFactor(HttpContext http, string detail)
    {
        Problems.Observe(http).RejectReason = RejectReasons.Conflict;
        return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "削除できません", detail: detail, type: LastFactorType);
    }

    private static IResult NoLocalAccount(HttpContext http) =>
        Problems.Conflict(http, "ローカルアカウントでログインしているときだけ使えます。");

    /// <summary>ログインの後に戻る先。同じオリジンのパスだけ（オープンリダイレクトの対策）。</summary>
    public static string SafeReturnUrl(string? value) =>
        value is { Length: > 0 and <= 2048 } && value[0] == '/' && !value.StartsWith("//", StringComparison.Ordinal) && !value.StartsWith("/\\", StringComparison.Ordinal)
        && !value.Contains('\\', StringComparison.Ordinal) && !value.Any(char.IsControl) && !value.StartsWith(Prefix + "/", StringComparison.OrdinalIgnoreCase)
            ? value
            : "/";
}

/// <summary>ログインの結果の状態。</summary>
internal enum LoginStatus
{
    SignedIn,
    TwoFactorRequired,
}

/// <summary>パスワードでのログイン。</summary>
internal sealed record PasswordLoginRequest(string? UserName, string? Password);

/// <summary>TOTP（code）かリカバリコード（recovery_code）のどちらか。</summary>
internal sealed record TwoFactorLoginRequest(string? Code, string? RecoveryCode);

/// <summary>パスキーの資格情報（navigator.credentials.get / create の結果の JSON）。</summary>
internal sealed record PasskeyCredentialRequest(JsonElement Credential, string? Name);

internal sealed record EnableTwoFactorRequest(string? Code);

internal sealed record ResetPasswordRequest(string? Token, string? NewPassword);

internal sealed record ResetPasskeyOptionsRequest(string? Token);

internal sealed record ResetPasskeyRequest(string? Token, JsonElement Credential, string? Name);

/// <summary>ログインの結果。サインインしたら新しいセッションと、新しい閲覧者の CSRF の要求トークン。</summary>
internal sealed record LoginResponse(
    [property: OpenApiEnum(typeof(LoginStatus))] string Status,
    SessionResponse? Session,
    string? CsrfToken)
{
    public LoginResponse(LoginStatus status, SessionResponse? session, string? csrfToken)
        : this(JsonNamingPolicy.SnakeCaseLower.ConvertName(status.ToString()), session, csrfToken)
    {
    }
}

/// <summary>ログイン中のセッション。</summary>
internal sealed record SessionResponse(
    [property: OpenApiEnum("person", "break_glass")] string Kind,
    Guid? PersonId,
    Guid? AccountId,
    Guid SessionId,
    string Provider,
    IReadOnlyList<string> Methods,
    IReadOnlyList<string> Roles)
{
    public static SessionResponse From(System.Security.Claims.ClaimsPrincipal principal)
    {
        var breakGlass = SessionClaims.IsBreakGlass(principal);
        return new SessionResponse(
            breakGlass ? SessionClaims.BreakGlassKind : SessionClaims.PersonKind,
            breakGlass ? null : Guid.Parse(principal.FindFirst(SessionClaims.Subject)!.Value, CultureInfo.InvariantCulture),
            Guid.TryParse(principal.FindFirst(SessionClaims.AccountId)?.Value, CultureInfo.InvariantCulture, out var account) ? account : null,
            Guid.Parse(principal.FindFirst(SessionClaims.SessionId)!.Value, CultureInfo.InvariantCulture),
            principal.FindFirst(SessionClaims.Provider)!.Value,
            [.. principal.FindAll(SessionClaims.Methods).Select(c => c.Value)],
            [.. principal.FindAll(SessionClaims.Roles).Select(c => c.Value)]);
    }
}

internal sealed record CsrfResponse(string Token, string HeaderName);

internal sealed record LoginMethodsResponse(IReadOnlyList<LoginProviderResponse> Providers, bool Password, bool Passkey);

internal sealed record LoginProviderResponse(string Name, string DisplayName, [property: OpenApiEnum("entra", "oidc")] string Type, string LoginUrl);

internal sealed record TwoFactorSetupResponse(string SharedKey, string AuthenticatorUri);

/// <summary>登録したパスキー（id は資格情報 ID の base64url。公開鍵などの中身は返さない）。</summary>
internal sealed record PasskeyResponse(string Id, string? Name, DateTimeOffset CreatedAt, bool IsBackedUp)
{
    public static PasskeyResponse From(StoredPasskey passkey) =>
        new(System.Buffers.Text.Base64Url.EncodeToString(passkey.CredentialId), passkey.Name, passkey.CreatedAt, passkey.IsBackedUp);

    public static bool TryParseId(string value, out byte[] credentialId)
    {
        credentialId = [];
        if (value.Length is 0 or > 1400)
        {
            return false;
        }

        try
        {
            credentialId = System.Buffers.Text.Base64Url.DecodeFromChars(value);
            return credentialId.Length > 0;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

internal sealed record RecoveryCodesResponse(IReadOnlyList<string> RecoveryCodes);
