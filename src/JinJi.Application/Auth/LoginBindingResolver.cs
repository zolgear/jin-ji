using JinJi.Application.Audit;
using JinJi.Domain;
using JinJi.Policy;

namespace JinJi.Application.Auth;

/// <summary>外部 IdP が認証した主体（id_token の検証の後）。</summary>
/// <param name="Issuer">id_token の iss。</param>
/// <param name="Subject">主体の識別子（Entra は oid、汎用 OIDC は sub）。</param>
/// <param name="TenantId">Entra の tid（Entra 以外は null）。</param>
/// <param name="EmployeeNumber">設定した社員番号のクレームの値（無ければ null）。</param>
/// <param name="Email">email クレーム（候補の提示だけに使う）。</param>
/// <param name="DisplayName">name クレーム（未紐付けのログインの表示だけに使う）。</param>
public sealed record ExternalPrincipal(
    LoginProviderOptions Provider,
    string Issuer,
    string Subject,
    string? TenantId,
    string? EmployeeNumber,
    string? Email,
    string? DisplayName);

/// <summary>外部 IdP のログインの照合の結果の種類。</summary>
public enum ExternalLoginOutcome
{
    /// <summary>紐付いた（既存の紐付け、または自動で作った紐付け）。ログインしてよい。</summary>
    Bound,

    /// <summary>紐付く人が退職済み・入社前・一時停止中。ログインさせない。</summary>
    Inactive,

    /// <summary>紐付けできない。拒否して未紐付けのログインとして記録した。</summary>
    Unbound,

    /// <summary>主体の形が不正（Entra の tid が設定と違う・oid が無いなど）。拒否する。</summary>
    Rejected,
}

/// <summary>外部 IdP のログインの照合の結果。</summary>
public sealed record ExternalLoginResult(
    ExternalLoginOutcome Outcome,
    Guid? PersonId = null,
    Guid? BindingId = null,
    BindingBasis? Basis = null,
    bool BindingCreated = false,
    Guid? UnboundLoginId = null,
    string? Detail = null);

/// <summary>
/// 外部 IdP の主体を person に紐付ける（ADR-0004「外部 IdP のユーザーと person の紐付け」）。
/// </summary>
/// <remarks>
/// <list type="number">
/// <item>既存の <c>login_binding</c>（発行者 + 主体。Entra は tid を含む発行者 + oid、汎用 OIDC は iss + sub）</item>
/// <item>Entra: oid が取込の外部 ID の対応（<c>external_identity</c>、既定 <c>entra_id</c>）の 1 人と一致し、tid が設定のテナントと一致すれば自動で紐付ける</item>
/// <item>IdP ごとに信頼すると設定した社員番号のクレームが 1 人の社員番号と一致すれば自動で紐付ける（法人を設定すればその法人だけ）</item>
/// <item>メールアドレスでは紐付けない（Microsoft も email・preferred_username・upn を認可に使うなと明記）。社用メールが一致する人は候補として記録するだけ</item>
/// </list>
/// 紐付けできなければ拒否し、未紐付けのログイン（<c>unbound_login</c>）として人事管理者に示す。person は作らない（jin-ji は正ではない）。
/// 紐付く人が退職済み・入社前・一時停止中ならログインさせない（自動の紐付けも作らない）。複数の人に当たる照合は使わない（広げない側に倒す）。
/// </remarks>
public sealed class LoginBindingResolver
{
    private const int MaxCandidates = 10;

    private readonly IAuthStore _store;
    private readonly AuthAuditor _audit;
    private readonly TimeProvider _timeProvider;

    public LoginBindingResolver(IAuthStore store, AuthAuditor audit, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _store = store;
        _audit = audit;
        _timeProvider = timeProvider;
    }

    public async Task<ExternalLoginResult> ResolveAsync(ExternalPrincipal principal, string? requestId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var provider = principal.Provider;
        if (string.IsNullOrWhiteSpace(principal.Issuer) || string.IsNullOrWhiteSpace(principal.Subject))
        {
            return new ExternalLoginResult(ExternalLoginOutcome.Rejected, Detail: "発行者か主体の識別子がありません。");
        }

        var isEntra = provider.ProviderType == LoginProviderType.Entra;
        if (isEntra && !string.Equals(principal.TenantId, provider.TenantId, StringComparison.OrdinalIgnoreCase))
        {
            return new ExternalLoginResult(ExternalLoginOutcome.Rejected, Detail: "Entra のテナント（tid）が設定と一致しません。");
        }

        var today = BusinessDate.Today(_timeProvider);

        // 1. 既存の紐付け
        if (await _store.FindActiveBindingAsync(AuthActor.System, principal.Issuer, principal.Subject, cancellationToken) is { } existing)
        {
            return await _store.GetPersonStateAsync(existing.PersonId, today, cancellationToken) == PersonLoginState.Active
                ? new ExternalLoginResult(ExternalLoginOutcome.Bound, existing.PersonId, existing.Id, existing.Basis)
                : new ExternalLoginResult(ExternalLoginOutcome.Inactive, existing.PersonId, existing.Id, existing.Basis);
        }

        // 2. Entra の oid と取込の外部 ID の対応 / 3. 信頼する社員番号のクレーム
        (Guid Person, BindingBasis Basis)? match = null;
        if (isEntra && await _store.FindPersonsByExternalIdAsync(provider.ExternalIdentitySource, principal.Subject.ToLowerInvariant(), cancellationToken) is [var byObjectId])
        {
            match = (byObjectId, BindingBasis.EntraObjectId);
        }
        else if (provider.TrustEmployeeNumberClaim
                 && !string.IsNullOrWhiteSpace(principal.EmployeeNumber)
                 && await _store.FindPersonsByEmployeeNumberAsync(principal.EmployeeNumber.Trim(), provider.EmployeeNumberLegalEntity, cancellationToken) is [var byEmployeeNumber])
        {
            match = (byEmployeeNumber, BindingBasis.EmployeeNumber);
        }

        if (match is var (person, basis))
        {
            if (await _store.GetPersonStateAsync(person, today, cancellationToken) != PersonLoginState.Active)
            {
                return new ExternalLoginResult(ExternalLoginOutcome.Inactive, person, Basis: basis);
            }

            var binding = new LoginBinding(EntityId.New(), provider.Name, principal.Issuer, principal.Subject, person, basis, _timeProvider.GetUtcNow());
            if (await _store.CreateBindingAsync(AuthActor.System, binding, null, cancellationToken) is not { } created)
            {
                // 同じ主体の紐付けが同時に作られた。作られた方に従う
                return await ResolveAsync(principal, requestId, cancellationToken);
            }

            await _audit.RecordAsync(
                new AuthAuditEntry
                {
                    Action = AuthAuditActions.BindingCreate,
                    Outcome = AuditOutcome.Success,
                    ActorType = ActorType.System,
                    Channel = Channel.Ui,
                    TargetType = AuthAuditTargets.LoginBinding,
                    TargetId = created.Id,
                    SubjectPersonIds = [person],
                    RequestId = requestId,
                },
                cancellationToken);
            return new ExternalLoginResult(ExternalLoginOutcome.Bound, person, created.Id, basis, BindingCreated: true);
        }

        // 4. 紐付けない。社用メールの一致は候補として記録するだけ
        var candidates = string.IsNullOrWhiteSpace(principal.Email)
            ? []
            : await _store.FindPersonsByWorkEmailAsync(principal.Email.Trim(), MaxCandidates, cancellationToken);
        var unboundId = await _store.RecordUnboundLoginAsync(
            new UnboundLoginAttempt(
                provider.Name,
                principal.Issuer,
                principal.Subject,
                principal.TenantId,
                Truncate(principal.EmployeeNumber, 64),
                Truncate(principal.Email, 256),
                Truncate(principal.DisplayName, 256),
                candidates),
            cancellationToken);
        return new ExternalLoginResult(ExternalLoginOutcome.Unbound, UnboundLoginId: unboundId);
    }

    private static string? Truncate(string? value, int length) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim() is var trimmed && trimmed.Length > length ? trimmed[..length] : value.Trim();
}
