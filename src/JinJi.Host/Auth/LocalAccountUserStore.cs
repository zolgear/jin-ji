using System.Globalization;
using JinJi.Application.Auth;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;

namespace JinJi.Host.Auth;

/// <summary>
/// ASP.NET Core Identity の自前ストア（ADR-0004「ローカルアカウント」）。DB の読み書きは <see cref="IAuthStore"/>（Npgsql）に任せる。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>実装するのは Identity の標準機能（パスワード・security stamp・ロックアウト・二要素・TOTP・リカバリコード・パスキー）に要るインターフェースだけ。
/// ロールは Identity のものを使わず、jin-ji のロール（role_grant → GUC jinji.viewer_roles）に寄せる。メール（IUserEmailStore）は持たない
/// （jin-ji はメールを送らず、社用メールは人事データの側にある）</item>
/// <item>アカウントの作成・削除は管理 CLI（<see cref="AuthAdministrationService"/>）だけで行い、Identity からはさせない</item>
/// <item>TOTP の共有鍵は Data Protection で暗号化して保存する。リカバリコードは SHA-256 だけを保存する</item>
/// <item>更新は concurrency stamp で楽観的に排他する（他の要求が先に更新していれば失敗）</item>
/// </list>
/// </remarks>
internal sealed class LocalAccountUserStore(IAuthStore store, IDataProtectionProvider dataProtection) :
    IUserPasswordStore<LocalAccount>,
    IUserSecurityStampStore<LocalAccount>,
    IUserLockoutStore<LocalAccount>,
    IUserTwoFactorStore<LocalAccount>,
    IUserAuthenticatorKeyStore<LocalAccount>,
    IUserTwoFactorRecoveryCodeStore<LocalAccount>,
    IUserPasskeyStore<LocalAccount>
{
    private readonly IDataProtector _authenticatorKeys = dataProtection.CreateProtector("JinJi.Auth.AuthenticatorKey");

    public void Dispose()
    {
    }

    // ---- IUserStore ---------------------------------------------------------------------------------------------

    public Task<string> GetUserIdAsync(LocalAccount user, CancellationToken cancellationToken) =>
        Task.FromResult(user.Id.ToString("D"));

    public Task<string?> GetUserNameAsync(LocalAccount user, CancellationToken cancellationToken) => Task.FromResult<string?>(user.UserName);

    public Task SetUserNameAsync(LocalAccount user, string? userName, CancellationToken cancellationToken) =>
        throw new NotSupportedException("ユーザー名は変更できません。");

    public Task<string?> GetNormalizedUserNameAsync(LocalAccount user, CancellationToken cancellationToken) => Task.FromResult<string?>(user.NormalizedUserName);

    public Task SetNormalizedUserNameAsync(LocalAccount user, string? normalizedName, CancellationToken cancellationToken)
    {
        // Identity は更新のたびに正規化した名前を設定し直す。名前は変えないので、同じ値であることだけ確かめる
        if (!string.Equals(user.NormalizedUserName, normalizedName, StringComparison.Ordinal))
        {
            throw new NotSupportedException("ユーザー名は変更できません。");
        }

        return Task.CompletedTask;
    }

    public Task<IdentityResult> CreateAsync(LocalAccount user, CancellationToken cancellationToken) =>
        Task.FromResult(IdentityResult.Failed(new IdentityError { Code = "NotSupported", Description = "アカウントは管理 CLI で作ります。" }));

    public async Task<IdentityResult> UpdateAsync(LocalAccount user, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        return await store.UpdateAccountAsync(AuthActor.System, user, user.ConcurrencyStamp, cancellationToken)
            ? IdentityResult.Success
            : IdentityResult.Failed(new IdentityErrorDescriber().ConcurrencyFailure());
    }

    public Task<IdentityResult> DeleteAsync(LocalAccount user, CancellationToken cancellationToken) =>
        Task.FromResult(IdentityResult.Failed(new IdentityError { Code = "NotSupported", Description = "アカウントは削除しません（無効にします）。" }));

    public Task<LocalAccount?> FindByIdAsync(string userId, CancellationToken cancellationToken) =>
        Guid.TryParse(userId, CultureInfo.InvariantCulture, out var id) ? store.FindAccountAsync(AuthActor.System, id, cancellationToken) : Task.FromResult<LocalAccount?>(null);

    public Task<LocalAccount?> FindByNameAsync(string normalizedUserName, CancellationToken cancellationToken) =>
        store.FindAccountByNameAsync(AuthActor.System, normalizedUserName, cancellationToken);

    // ---- パスワード・security stamp ------------------------------------------------------------------------------

    public Task SetPasswordHashAsync(LocalAccount user, string? passwordHash, CancellationToken cancellationToken)
    {
        if (user.IsBreakGlass && passwordHash is not null)
        {
            throw new InvalidOperationException("緊急用アカウントはパスワードを持ちません（パスキーだけ）。");
        }

        user.PasswordHash = passwordHash;
        return Task.CompletedTask;
    }

    public Task<string?> GetPasswordHashAsync(LocalAccount user, CancellationToken cancellationToken) => Task.FromResult(user.PasswordHash);

    public Task<bool> HasPasswordAsync(LocalAccount user, CancellationToken cancellationToken) => Task.FromResult(user.PasswordHash is not null);

    public Task SetSecurityStampAsync(LocalAccount user, string stamp, CancellationToken cancellationToken)
    {
        user.SecurityStamp = stamp;
        return Task.CompletedTask;
    }

    public Task<string?> GetSecurityStampAsync(LocalAccount user, CancellationToken cancellationToken) => Task.FromResult<string?>(user.SecurityStamp);

    // ---- ロックアウト --------------------------------------------------------------------------------------------

    public Task<DateTimeOffset?> GetLockoutEndDateAsync(LocalAccount user, CancellationToken cancellationToken) => Task.FromResult(user.LockoutEnd);

    public Task SetLockoutEndDateAsync(LocalAccount user, DateTimeOffset? lockoutEnd, CancellationToken cancellationToken)
    {
        user.LockoutEnd = lockoutEnd;
        return Task.CompletedTask;
    }

    public Task<int> IncrementAccessFailedCountAsync(LocalAccount user, CancellationToken cancellationToken) =>
        Task.FromResult(++user.AccessFailedCount);

    public Task ResetAccessFailedCountAsync(LocalAccount user, CancellationToken cancellationToken)
    {
        user.AccessFailedCount = 0;
        return Task.CompletedTask;
    }

    public Task<int> GetAccessFailedCountAsync(LocalAccount user, CancellationToken cancellationToken) => Task.FromResult(user.AccessFailedCount);

    /// <summary>ロックアウトは常に有効（DB の CHECK 制約でも外せない）。</summary>
    public Task<bool> GetLockoutEnabledAsync(LocalAccount user, CancellationToken cancellationToken) => Task.FromResult(true);

    public Task SetLockoutEnabledAsync(LocalAccount user, bool enabled, CancellationToken cancellationToken) =>
        enabled ? Task.CompletedTask : throw new NotSupportedException("ロックアウトは無効にできません。");

    // ---- 二要素・TOTP・リカバリコード ----------------------------------------------------------------------------

    public Task SetTwoFactorEnabledAsync(LocalAccount user, bool enabled, CancellationToken cancellationToken)
    {
        if (user.IsBreakGlass && enabled)
        {
            throw new InvalidOperationException("緊急用アカウントは TOTP を持ちません（パスキーだけ）。");
        }

        user.TwoFactorEnabled = enabled;
        return Task.CompletedTask;
    }

    public Task<bool> GetTwoFactorEnabledAsync(LocalAccount user, CancellationToken cancellationToken) => Task.FromResult(user.TwoFactorEnabled);

    public Task SetAuthenticatorKeyAsync(LocalAccount user, string key, CancellationToken cancellationToken)
    {
        if (user.IsBreakGlass)
        {
            throw new InvalidOperationException("緊急用アカウントは TOTP を持ちません（パスキーだけ）。");
        }

        user.AuthenticatorKeyProtected = key is null ? null : _authenticatorKeys.Protect(key);
        return Task.CompletedTask;
    }

    public Task<string?> GetAuthenticatorKeyAsync(LocalAccount user, CancellationToken cancellationToken) =>
        Task.FromResult(user.AuthenticatorKeyProtected is { } value ? _authenticatorKeys.Unprotect(value) : null);

    public Task ReplaceCodesAsync(LocalAccount user, IEnumerable<string> recoveryCodes, CancellationToken cancellationToken) =>
        store.ReplaceRecoveryCodesAsync(user.Id, [.. recoveryCodes.Select(AuthSecrets.Sha256)], cancellationToken);

    public Task<bool> RedeemCodeAsync(LocalAccount user, string code, CancellationToken cancellationToken) =>
        store.RedeemRecoveryCodeAsync(user.Id, AuthSecrets.Sha256(code), cancellationToken);

    public Task<int> CountCodesAsync(LocalAccount user, CancellationToken cancellationToken) =>
        store.CountRecoveryCodesAsync(user.Id, cancellationToken);

    // ---- パスキー ------------------------------------------------------------------------------------------------

    public Task AddOrUpdatePasskeyAsync(LocalAccount user, UserPasskeyInfo passkey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(passkey);
        return store.AddOrUpdatePasskeyAsync(
            user.Id,
            new StoredPasskey(
                passkey.CredentialId,
                passkey.PublicKey,
                passkey.CreatedAt,
                passkey.SignCount,
                passkey.Transports,
                passkey.IsUserVerified,
                passkey.IsBackupEligible,
                passkey.IsBackedUp,
                passkey.AttestationObject,
                passkey.ClientDataJson,
                passkey.Name),
            cancellationToken);
    }

    public async Task<IList<UserPasskeyInfo>> GetPasskeysAsync(LocalAccount user, CancellationToken cancellationToken) =>
        [.. (await store.GetPasskeysAsync(user.Id, cancellationToken)).Select(ToInfo)];

    public async Task<LocalAccount?> FindByPasskeyIdAsync(byte[] credentialId, CancellationToken cancellationToken) =>
        await store.FindAccountByPasskeyAsync(credentialId, cancellationToken) is { } id
            ? await store.FindAccountAsync(AuthActor.System, id, cancellationToken)
            : null;

    public async Task<UserPasskeyInfo?> FindPasskeyAsync(LocalAccount user, byte[] credentialId, CancellationToken cancellationToken) =>
        (await store.GetPasskeysAsync(user.Id, cancellationToken)).FirstOrDefault(p => p.CredentialId.AsSpan().SequenceEqual(credentialId)) is { } passkey
            ? ToInfo(passkey)
            : null;

    public Task RemovePasskeyAsync(LocalAccount user, byte[] credentialId, CancellationToken cancellationToken) =>
        store.RemovePasskeyAsync(user.Id, credentialId, cancellationToken);

    private static UserPasskeyInfo ToInfo(StoredPasskey passkey) =>
        new(
            passkey.CredentialId,
            passkey.PublicKey,
            passkey.CreatedAt,
            passkey.SignCount,
            passkey.Transports?.ToArray(),
            passkey.IsUserVerified,
            passkey.IsBackupEligible,
            passkey.IsBackedUp,
            passkey.AttestationObject,
            passkey.ClientDataJson)
        {
            Name = passkey.Name,
        };
}

/// <summary>Identity の名前の正規化（<see cref="LocalAccountNames.Normalize"/> と同じ。管理 CLI の作成と照合を揃える）。</summary>
internal sealed class LocalAccountNameNormalizer : ILookupNormalizer
{
    public string? NormalizeName(string? name) => name is null ? null : LocalAccountNames.Normalize(name);

    public string? NormalizeEmail(string? email) => email is null ? null : LocalAccountNames.Normalize(email);
}
