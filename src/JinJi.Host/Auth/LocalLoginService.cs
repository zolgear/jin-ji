using System.Globalization;
using JinJi.Application.Access;
using JinJi.Application.Audit;
using JinJi.Application.Auth;
using JinJi.Domain;
using JinJi.Policy;
using Microsoft.AspNetCore.Identity;

namespace JinJi.Host.Auth;

/// <summary>ローカルのログイン・登録の結果の種類。</summary>
internal enum LocalLoginStatus
{
    SignedIn,
    TwoFactorRequired,

    /// <summary>失敗（理由は応答に出さない。ユーザー名の有無・ロックアウト・退職を区別させない）。</summary>
    Failed,

    /// <summary>試行のレート制限を超えた（429）。</summary>
    Throttled,

    /// <summary>管理系のロールを持つのに、パスキーか TOTP を使っていない（403）。</summary>
    MfaRequired,

    /// <summary>パスキーを使えない設定（PasskeyServerDomain が無い）。</summary>
    PasskeyUnavailable,
}

/// <summary>ローカルのログインの結果。</summary>
internal sealed record LocalLoginResult(LocalLoginStatus Status, SignedInSession? Session = null, TimeSpan? RetryAfter = null)
{
    public static LocalLoginResult Failed { get; } = new(LocalLoginStatus.Failed);
}

/// <summary>
/// ローカルアカウントのログイン（パスワード・TOTP・リカバリコード・パスキー）と登録（パスキー・TOTP）、管理者が発行したリンクの使用。
/// パスワード・パスキー・TOTP・ロックアウトは ASP.NET Core Identity の標準機能（UserManager・IPasskeyHandler・AuthenticatorTokenProvider）を使う。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>試行は接続元アドレス → 接続元アドレス × アカウントの順に数える（<see cref="LoginThrottle"/>）。ロックアウトは Identity（既定 5 回の失敗で 15 分）</item>
/// <item>失敗の応答はユーザー名の有無・ロックアウト・退職・一時停止を区別しない。ユーザー名が無い場合もパスワードのハッシュの検証を 1 回行い、時間差を抑える</item>
/// <item>管理系のロール（hr_admin）を持つ人は、パスワードだけではログインできない（パスキーか、パスワード + TOTP）</item>
/// <item>緊急用アカウントはパスキーだけでログインする</item>
/// <item>パスワード・パスキーをリンクで設定したら、ロックアウトを解き、そのアカウントの既存のセッションを終える</item>
/// </list>
/// </remarks>
internal sealed partial class LocalLoginService(
    UserManager<LocalAccount> users,
    IPasskeyHandler<LocalAccount> passkeys,
    IAuthStore store,
    SessionSignIn signIn,
    AuthAuditor audit,
    LoginOptions options,
    LoginThrottle throttle,
    PendingLoginCookie pending,
    TimeProvider timeProvider,
    ILogger<LocalLoginService> logger)
{
    /// <summary>ユーザー名が無いときに検証する固定のハッシュ（時間差を抑えるためだけ。どのパスワードとも一致しない）。</summary>
    private static readonly Lazy<string> DummyHash = new(() => new PasswordHasher<LocalAccount>().HashPassword(new LocalAccount(), Guid.NewGuid().ToString("N")));

    public bool PasskeysEnabled => options.PasskeyServerDomain is not null;

    // ---- パスワード・TOTP ----------------------------------------------------------------------------------------

    public async Task<LocalLoginResult> PasswordAsync(HttpContext http, string? userName, string? password, CancellationToken cancellationToken)
    {
        var normalized = LocalAccountNames.Normalize(userName ?? string.Empty);
        if (throttle.TryAcquire(http, normalized) is { } wait)
        {
            return new LocalLoginResult(LocalLoginStatus.Throttled, RetryAfter: wait);
        }

        var account = options.LocalPasswordEnabled && !string.IsNullOrEmpty(userName) && !string.IsNullOrEmpty(password)
            ? await users.FindByNameAsync(userName)
            : null;
        if (account is null || account.IsBreakGlass || account.DisabledAt is not null || account.PasswordHash is null)
        {
            _ = users.PasswordHasher.VerifyHashedPassword(new LocalAccount(), DummyHash.Value, password ?? string.Empty);
            await DeniedAsync(http, account, AuditOutcome.Denied, "unknown_or_unusable", cancellationToken);
            return LocalLoginResult.Failed;
        }

        if (await users.IsLockedOutAsync(account))
        {
            await DeniedAsync(http, account, AuditOutcome.Denied, "locked_out", cancellationToken);
            return LocalLoginResult.Failed;
        }

        if (!await users.CheckPasswordAsync(account, password!))
        {
            await users.AccessFailedAsync(account);
            await DeniedAsync(http, account, AuditOutcome.Failed, "wrong_password", cancellationToken);
            return LocalLoginResult.Failed;
        }

        if (await PersonStateAsync(account, cancellationToken) != PersonLoginState.Active)
        {
            await DeniedAsync(http, account, AuditOutcome.Denied, "person_inactive", cancellationToken);
            return LocalLoginResult.Failed;
        }

        if (account.TwoFactorEnabled)
        {
            pending.Write(http, new PendingState(PendingStep.TwoFactor, account.Id, account.SecurityStamp, null, null, null));
            return new LocalLoginResult(LocalLoginStatus.TwoFactorRequired);
        }

        if (await IsAdministratorAsync(account, cancellationToken))
        {
            // 管理系のロールはパスワードだけでは入れない（パスキーか TOTP。登録はリンクか、TOTP を有効にした後のログインで行う）
            await DeniedAsync(http, account, AuditOutcome.Denied, "mfa_required", cancellationToken);
            return new LocalLoginResult(LocalLoginStatus.MfaRequired);
        }

        await users.ResetAccessFailedCountAsync(account);
        return await SignInAsync(http, account, ["pwd"], cancellationToken);
    }

    public async Task<LocalLoginResult> TwoFactorAsync(HttpContext http, string? code, string? recoveryCode, CancellationToken cancellationToken)
    {
        if (pending.Take(http, PendingStep.TwoFactor) is not { AccountId: { } accountId } state
            || await users.FindByIdAsync(accountId.ToString("D")) is not { } account
            || account.DisabledAt is not null
            || !string.Equals(account.SecurityStamp, state.SecurityStamp, StringComparison.Ordinal))
        {
            return LocalLoginResult.Failed;
        }

        if (throttle.TryAcquire(http, account.NormalizedUserName) is { } wait)
        {
            pending.Write(http, state);
            return new LocalLoginResult(LocalLoginStatus.Throttled, RetryAfter: wait);
        }

        if (await users.IsLockedOutAsync(account))
        {
            await DeniedAsync(http, account, AuditOutcome.Denied, "locked_out", cancellationToken);
            return LocalLoginResult.Failed;
        }

        var method = recoveryCode is { Length: > 0 } ? "rc" : "otp";
        var verified = method == "rc"
            ? (await users.RedeemTwoFactorRecoveryCodeAsync(account, recoveryCode!.Trim())).Succeeded ? TotpCheck.Accepted : TotpCheck.Wrong
            : await VerifyTotpAsync(account, code?.Replace(" ", string.Empty, StringComparison.Ordinal).Replace("-", string.Empty, StringComparison.Ordinal), cancellationToken);
        if (verified != TotpCheck.Accepted)
        {
            await users.AccessFailedAsync(account);
            await DeniedAsync(http, account, AuditOutcome.Failed, verified == TotpCheck.Reused ? "totp_reused" : "wrong_second_factor", cancellationToken);
            if (!await users.IsLockedOutAsync(account))
            {
                pending.Write(http, state with { SecurityStamp = account.SecurityStamp });
            }

            return LocalLoginResult.Failed;
        }

        if (await PersonStateAsync(account, cancellationToken) != PersonLoginState.Active)
        {
            await DeniedAsync(http, account, AuditOutcome.Denied, "person_inactive", cancellationToken);
            return LocalLoginResult.Failed;
        }

        await users.ResetAccessFailedCountAsync(account);
        return await SignInAsync(http, account, ["pwd", method], cancellationToken);
    }

    // ---- パスキーでのログイン ------------------------------------------------------------------------------------

    public async Task<(LocalLoginStatus Status, string? OptionsJson, TimeSpan? RetryAfter)> PasskeyLoginOptionsAsync(HttpContext http)
    {
        if (!PasskeysEnabled)
        {
            return (LocalLoginStatus.PasskeyUnavailable, null, null);
        }

        if (throttle.TryAcquire(http, null) is { } wait)
        {
            return (LocalLoginStatus.Throttled, null, wait);
        }

        // 利用者を指定しない（発見可能な資格情報。ユーザー名の有無を明かさない）
        var result = await passkeys.MakeRequestOptionsAsync(null, http);
        pending.Write(http, new PendingState(PendingStep.PasskeyLogin, null, null, result.AssertionState, null, null));
        return (LocalLoginStatus.SignedIn, result.RequestOptionsJson, null);
    }

    public async Task<LocalLoginResult> PasskeyLoginAsync(HttpContext http, string credentialJson, CancellationToken cancellationToken)
    {
        if (!PasskeysEnabled)
        {
            return new LocalLoginResult(LocalLoginStatus.PasskeyUnavailable);
        }

        if (pending.Take(http, PendingStep.PasskeyLogin) is not { } state)
        {
            return LocalLoginResult.Failed;
        }

        if (throttle.TryAcquire(http, null) is { } wait)
        {
            return new LocalLoginResult(LocalLoginStatus.Throttled, RetryAfter: wait);
        }

        var result = await passkeys.PerformAssertionAsync(new PasskeyAssertionContext
        {
            HttpContext = http,
            CredentialJson = credentialJson,
            AssertionState = state.PasskeyState,
        });
        if (!result.Succeeded || result.User is not { } account)
        {
            LogPasskeyFailed(logger, result.Failure?.Message);
            await DeniedAsync(http, null, AuditOutcome.Failed, "passkey_assertion", cancellationToken);
            return LocalLoginResult.Failed;
        }

        if (account.DisabledAt is not null || await users.IsLockedOutAsync(account))
        {
            await DeniedAsync(http, account, AuditOutcome.Denied, "disabled_or_locked_out", cancellationToken);
            return LocalLoginResult.Failed;
        }

        // 署名の回数などを保存する（Identity の規約: 検証に成功したらパスキーを保存し直す）
        await users.AddOrUpdatePasskeyAsync(account, result.Passkey);
        if (!account.IsBreakGlass && await PersonStateAsync(account, cancellationToken) != PersonLoginState.Active)
        {
            await DeniedAsync(http, account, AuditOutcome.Denied, "person_inactive", cancellationToken);
            return LocalLoginResult.Failed;
        }

        await users.ResetAccessFailedCountAsync(account);
        return await SignInAsync(http, account, ["hwk"], cancellationToken);
    }

    // ---- 登録（ログイン中の本人） --------------------------------------------------------------------------------

    /// <summary>ログイン中の本人のローカルアカウント（人のアカウントだけ）。</summary>
    public async Task<LocalAccount?> CurrentAccountAsync(HttpContext http)
    {
        if (!SessionClaims.IsSession(http.User) || SessionClaims.IsBreakGlass(http.User)
            || !Guid.TryParse(http.User.FindFirst(SessionClaims.AccountId)?.Value, CultureInfo.InvariantCulture, out var accountId))
        {
            return null;
        }

        return await users.FindByIdAsync(accountId.ToString("D")) is { DisabledAt: null } account ? account : null;
    }

    public async Task<string?> PasskeyRegistrationOptionsAsync(HttpContext http, LocalAccount account, PendingStep step, Guid? linkId)
    {
        if (!PasskeysEnabled)
        {
            return null;
        }

        var result = await passkeys.MakeCreationOptionsAsync(
            new PasskeyUserEntity { Id = account.Id.ToString("D"), Name = account.UserName, DisplayName = account.UserName },
            http);
        pending.Write(http, new PendingState(step, account.Id, null, result.AttestationState, linkId, null));
        return result.CreationOptionsJson;
    }

    /// <summary>ログイン中の本人のパスキーの登録を完了する（登録の challenge を出したアカウントと一致するときだけ）。</summary>
    public async Task<bool> CompletePasskeyRegistrationAsync(HttpContext http, LocalAccount account, string credentialJson, string? name, CancellationToken cancellationToken)
    {
        if (await AttestAsync(http, account, PendingStep.PasskeyRegistration, null, credentialJson, name) is not { } passkey)
        {
            return false;
        }

        if (!(await users.AddOrUpdatePasskeyAsync(account, passkey)).Succeeded)
        {
            return false;
        }

        await audit.RecordAsync(AccountEntry(http, account, AuthAuditActions.PasskeyRegister, AuditOutcome.Success), cancellationToken);
        return true;
    }

    /// <summary>登録の challenge の状態でアテステーションを検証する（保存はしない）。</summary>
    private async Task<UserPasskeyInfo?> AttestAsync(HttpContext http, LocalAccount account, PendingStep step, Guid? linkId, string credentialJson, string? name)
    {
        if (!PasskeysEnabled || pending.Take(http, step) is not { } state || state.AccountId != account.Id || state.LinkId != linkId)
        {
            return null;
        }

        var result = await passkeys.PerformAttestationAsync(new PasskeyAttestationContext
        {
            HttpContext = http,
            CredentialJson = credentialJson,
            AttestationState = state.PasskeyState,
        });
        if (!result.Succeeded)
        {
            LogPasskeyFailed(logger, result.Failure?.Message);
            return null;
        }

        var passkey = result.Passkey;
        var trimmed = name?.Trim();
        passkey.Name = string.IsNullOrEmpty(trimmed) ? null : trimmed[..Math.Min(trimmed.Length, 200)];
        return passkey;
    }

    /// <summary>TOTP の共有鍵を作り直す（有効にするのは <see cref="EnableTwoFactorAsync"/> で確かめてから）。</summary>
    public async Task<(string SharedKey, string AuthenticatorUri)> ResetAuthenticatorAsync(LocalAccount account)
    {
        await users.ResetAuthenticatorKeyAsync(account);
        var key = await users.GetAuthenticatorKeyAsync(account) ?? throw new InvalidOperationException("TOTP の鍵を作れませんでした。");
        var label = Uri.EscapeDataString($"jin-ji:{account.UserName}");
        return (key, $"otpauth://totp/{label}?secret={key}&issuer=jin-ji&digits=6");
    }

    /// <summary>TOTP の符号を確かめて二要素認証を有効にし、リカバリコードを返す。符号が合わなければ null。</summary>
    public async Task<IReadOnlyList<string>?> EnableTwoFactorAsync(HttpContext http, LocalAccount account, string? code, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(code)
            || await VerifyTotpAsync(account, code.Replace(" ", string.Empty, StringComparison.Ordinal), cancellationToken) != TotpCheck.Accepted)
        {
            return null;
        }

        await users.SetTwoFactorEnabledAsync(account, true);
        var codes = await users.GenerateNewTwoFactorRecoveryCodesAsync(account, 10) ?? [];
        await audit.RecordAsync(AccountEntry(http, account, AuthAuditActions.TwoFactorEnable, AuditOutcome.Success), cancellationToken);
        return [.. codes];
    }

    /// <summary>ログイン中の本人のパスキー（登録の順）。</summary>
    public Task<IReadOnlyList<StoredPasskey>> ListPasskeysAsync(LocalAccount account, CancellationToken cancellationToken) =>
        store.GetPasskeysAsync(account.Id, cancellationToken);

    /// <summary>
    /// ログイン中の本人のパスキーを削除する。最後の 1 つを消すとログインの手段が無くなる（パスワードが無い）場合と、
    /// 管理系のロールを持つのに多要素が無くなる（TOTP が無い）場合は拒否する。
    /// </summary>
    public async Task<PasskeyDeletion> DeletePasskeyAsync(HttpContext http, LocalAccount account, byte[] credentialId, CancellationToken cancellationToken)
    {
        var passkeys = await store.GetPasskeysAsync(account.Id, cancellationToken);
        if (!passkeys.Any(p => p.CredentialId.AsSpan().SequenceEqual(credentialId)))
        {
            return PasskeyDeletion.NotFound;
        }

        var outcome = passkeys.Count > 1 ? PasskeyDeletion.Deleted
            : account.PasswordHash is null ? PasskeyDeletion.LastCredential
            : !account.TwoFactorEnabled && await IsAdministratorAsync(account, cancellationToken) ? PasskeyDeletion.MfaRequired
            : PasskeyDeletion.Deleted;
        if (outcome == PasskeyDeletion.Deleted)
        {
            await RequireAsync(users.RemovePasskeyAsync(account, credentialId));
        }

        await audit.RecordAsync(
            AccountEntry(http, account, AuthAuditActions.PasskeyDelete, outcome == PasskeyDeletion.Deleted ? AuditOutcome.Success : AuditOutcome.Denied),
            cancellationToken);
        return outcome;
    }

    /// <summary>
    /// TOTP の符号を確かめる（Identity の検証）。通った符号の時間窓を記録し、最後に受け付けた時間窓以前の符号は再利用として拒否する
    /// （同じ符号を 2 度受け付けない。RFC 6238 §5.2）。
    /// </summary>
    private async Task<TotpCheck> VerifyTotpAsync(LocalAccount account, string? code, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(code)
            || await users.GetAuthenticatorKeyAsync(account) is not { } key
            || !await users.VerifyTwoFactorTokenAsync(account, TokenOptions.DefaultAuthenticatorProvider, code))
        {
            return TotpCheck.Wrong;
        }

        // Identity はシステムの時計で検証するので、時間窓もシステムの時計で求める（TimeProvider は使わない）
        if (TotpTimeStep.Match(key, code, DateTimeOffset.UtcNow) is not { } step)
        {
            return TotpCheck.Wrong;
        }

        if (!await store.ConsumeTotpStepAsync(account.Id, step, cancellationToken))
        {
            LogTotpReused(logger, account.Id);
            return TotpCheck.Reused;
        }

        return TotpCheck.Accepted;
    }

    private enum TotpCheck
    {
        Accepted,
        Wrong,
        Reused,
    }

    // ---- 管理者が発行したリンク ----------------------------------------------------------------------------------

    /// <summary>リンクでパスワードを設定する。成功したら null、失敗したら応答に出す理由（リンクの無効・パスワードの要件）。</summary>
    public async Task<(bool Throttled, TimeSpan? RetryAfter, IReadOnlyList<string>? Errors)> ResetPasswordAsync(
        HttpContext http,
        string? token,
        string? newPassword,
        CancellationToken cancellationToken)
    {
        if (throttle.TryAcquire(http, null) is { } wait)
        {
            return (true, wait, null);
        }

        var invalid = (false, (TimeSpan?)null, (IReadOnlyList<string>?)["リンクが無効か、期限が切れています。管理者に新しいリンクの発行を依頼してください。"]);
        if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(newPassword))
        {
            return invalid;
        }

        var hash = AuthSecrets.Sha256(token);
        if (await store.FindUsableResetLinkAsync(hash, ResetLinkPurpose.Password, cancellationToken) is not { } link
            || await users.FindByIdAsync(link.AccountId.ToString("D")) is not { DisabledAt: null, IsBreakGlass: false } account)
        {
            await audit.RecordAsync(LinkEntry(http, null, null, AuditOutcome.Denied), cancellationToken);
            return invalid;
        }

        // 要件を満たさないパスワードではリンクを消費しない
        var errors = new List<string>();
        foreach (var validator in users.PasswordValidators)
        {
            var validation = await validator.ValidateAsync(users, account, newPassword);
            errors.AddRange(validation.Errors.Select(e => e.Description));
        }

        if (errors.Count > 0)
        {
            return (false, null, errors);
        }

        if (await store.ConsumeResetLinkAsync(hash, ResetLinkPurpose.Password, cancellationToken) is null)
        {
            return invalid;
        }

        if (await users.HasPasswordAsync(account))
        {
            await RequireAsync(users.RemovePasswordAsync(account));
        }

        await RequireAsync(users.AddPasswordAsync(account, newPassword));
        await RequireAsync(users.SetLockoutEndDateAsync(account, null));
        await RequireAsync(users.ResetAccessFailedCountAsync(account));
        await store.EndAccountSessionsAsync(account.Id, SessionEndKind.CredentialChanged, cancellationToken);
        await audit.RecordAsync(LinkEntry(http, account, link.LinkId, AuditOutcome.Success), cancellationToken);
        return (false, null, null);
    }

    /// <summary>リンクのパスキーの登録の対象（使えるリンクのアカウント）。</summary>
    public async Task<(ResetLinkTarget Link, LocalAccount Account)?> ResetPasskeyTargetAsync(HttpContext http, string? token, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(token)
            || await store.FindUsableResetLinkAsync(AuthSecrets.Sha256(token), ResetLinkPurpose.Passkey, cancellationToken) is not { } link
            || await users.FindByIdAsync(link.AccountId.ToString("D")) is not { DisabledAt: null } account)
        {
            await audit.RecordAsync(LinkEntry(http, null, null, AuditOutcome.Denied), cancellationToken);
            return null;
        }

        return (link, account);
    }

    /// <summary>リンクでパスキーを登録し、リンクを使用済みにする（検証 → リンクの消費 → 保存の順。リンクが先に使われていれば保存しない）。</summary>
    public async Task<bool> ResetPasskeyAsync(HttpContext http, string token, ResetLinkTarget link, LocalAccount account, string credentialJson, string? name, CancellationToken cancellationToken)
    {
        if (await AttestAsync(http, account, PendingStep.ResetPasskey, link.LinkId, credentialJson, name) is not { } passkey)
        {
            return false;
        }

        if (await store.ConsumeResetLinkAsync(AuthSecrets.Sha256(token), ResetLinkPurpose.Passkey, cancellationToken) is not { } consumed || consumed.LinkId != link.LinkId)
        {
            return false;
        }

        await RequireAsync(users.AddOrUpdatePasskeyAsync(account, passkey));
        await RequireAsync(users.SetLockoutEndDateAsync(account, null));
        await RequireAsync(users.ResetAccessFailedCountAsync(account));
        await audit.RecordAsync(AccountEntry(http, account, AuthAuditActions.PasskeyRegister, AuditOutcome.Success), cancellationToken);
        await audit.RecordAsync(LinkEntry(http, account, link.LinkId, AuditOutcome.Success), cancellationToken);
        return true;
    }

    public Throttle ThrottleFor(HttpContext http) => throttle.TryAcquire(http, null) is { } wait ? new Throttle(wait) : new Throttle(null);

    // ---- 共通 ----------------------------------------------------------------------------------------------------

    private async Task<LocalLoginResult> SignInAsync(HttpContext http, LocalAccount account, IReadOnlyList<string> methods, CancellationToken cancellationToken)
    {
        var session = new NewSession(
            account.IsBreakGlass ? SessionPrincipalKind.BreakGlass : SessionPrincipalKind.Person,
            account.PersonId,
            account.Id,
            null,
            LoginOptions.LocalProvider,
            methods);
        return new LocalLoginResult(LocalLoginStatus.SignedIn, await signIn.SignInAsync(http, session, cancellationToken));
    }

    private async Task<PersonLoginState> PersonStateAsync(LocalAccount account, CancellationToken cancellationToken) =>
        account.PersonId is { } person
            ? await store.GetPersonStateAsync(person, BusinessDate.Today(timeProvider), cancellationToken)
            : PersonLoginState.Inactive;

    private async Task<bool> IsAdministratorAsync(LocalAccount account, CancellationToken cancellationToken) =>
        account.PersonId is { } person && (await store.GetRolesAsync(person, cancellationToken)).Any(Roles.Known.Contains);

    private async Task DeniedAsync(HttpContext http, LocalAccount? account, AuditOutcome outcome, string reason, CancellationToken cancellationToken)
    {
        LogDenied(logger, account?.Id, reason);
        await audit.RecordAsync(LoginAudit.Entry(http, outcome, account?.IsBreakGlass == true, account?.PersonId, account?.Id, null), cancellationToken);
    }

    private static AuthAuditEntry AccountEntry(HttpContext http, LocalAccount account, string action, AuditOutcome outcome) => new()
    {
        Action = action,
        Outcome = outcome,
        ActorType = account.IsBreakGlass ? ActorType.BreakGlass : ActorType.User,
        Channel = Channel.Ui,
        ActorPersonId = account.PersonId,
        ActorAccountId = account.Id,
        ActorClientId = UiClient.Id,
        TargetType = AuthAuditTargets.LocalAccount,
        TargetId = account.Id,
        SubjectPersonIds = account.PersonId is { } person ? [person] : [],
        RequestId = http.TraceIdentifier,
    };

    private static AuthAuditEntry LinkEntry(HttpContext http, LocalAccount? account, Guid? linkId, AuditOutcome outcome) => new()
    {
        Action = AuthAuditActions.ResetLinkUse,
        Outcome = outcome,
        ActorType = account?.IsBreakGlass == true ? ActorType.BreakGlass : ActorType.User,
        Channel = Channel.Ui,
        ActorPersonId = account?.PersonId,
        ActorAccountId = account?.Id,
        ActorClientId = UiClient.Id,
        TargetType = AuthAuditTargets.ResetLink,
        TargetId = linkId,
        SubjectPersonIds = account?.PersonId is { } person ? [person] : [],
        RequestId = http.TraceIdentifier,
    };

    private static async Task RequireAsync(Task<IdentityResult> operation)
    {
        var result = await operation;
        if (!result.Succeeded)
        {
            throw new InvalidOperationException("アカウントを更新できませんでした: " + string.Join(", ", result.Errors.Select(e => e.Description)));
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "ローカルアカウントのログインを拒否しました（アカウント {AccountId}、理由 {Reason}）")]
    private static partial void LogDenied(ILogger logger, Guid? accountId, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "TOTP の符号の再利用を拒否しました（アカウント {AccountId}。既に受け付けた時間窓の符号）")]
    private static partial void LogTotpReused(ILogger logger, Guid accountId);

    [LoggerMessage(Level = LogLevel.Information, Message = "パスキーの検証に失敗しました: {Message}")]
    private static partial void LogPasskeyFailed(ILogger logger, string? message);
}

/// <summary>本人によるパスキーの削除の結果。</summary>
internal enum PasskeyDeletion
{
    Deleted,
    NotFound,

    /// <summary>最後のパスキーで、パスワードも無い（ログインの手段が無くなる）。</summary>
    LastCredential,

    /// <summary>最後のパスキーで、管理系のロールを持つのに TOTP が無い（多要素が無くなる）。</summary>
    MfaRequired,
}

/// <summary>試行のレート制限の結果（超えていれば待つ時間）。</summary>
internal readonly record struct Throttle(TimeSpan? RetryAfter);
