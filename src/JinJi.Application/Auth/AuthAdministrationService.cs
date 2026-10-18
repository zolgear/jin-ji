using JinJi.Application.Audit;
using JinJi.Domain;
using JinJi.Policy;

namespace JinJi.Application.Auth;

/// <summary>認証の管理の拒否の種類。</summary>
public enum AuthAdminError
{
    NotFound,
    Conflict,
    Invalid,
    Forbidden,
}

/// <summary>認証の管理の操作を拒否した（理由は応答に出してよい範囲の文言）。</summary>
public sealed class AuthAdminException : Exception
{
    public AuthAdminException()
    {
    }

    public AuthAdminException(string message)
        : base(message)
    {
    }

    public AuthAdminException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public AuthAdminException(AuthAdminError error, string message)
        : base(message)
    {
        Error = error;
    }

    public AuthAdminError Error { get; }
}

/// <summary>発行したリンク。<see cref="Token"/> はこの場でだけ見える（DB にはハッシュだけを持つ）。</summary>
public sealed record IssuedResetLink(Guid LinkId, Guid AccountId, ResetLinkPurpose Purpose, string Token, DateTimeOffset ExpiresAt, string? Url);

/// <summary>手動の紐付けの指定。未紐付けのログインから作るか、ログイン方式と主体の識別子を直接指定する。</summary>
public sealed record BindingRequest(Guid PersonId, Guid? UnboundLoginId, string? Provider, string? Subject, string? Issuer);

/// <summary>
/// 認証の管理（ローカルアカウント・緊急用アカウントの作成、リンクの発行、ロックの解除、手動の紐付け、未紐付けのログイン、ロールの付与）。
/// 管理 CLI と、Web の認証の管理（人事管理者の人・緊急用アカウント）が使う。すべて監査ログに残す（拒否も）。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>アカウントの作成・ロールの付与は管理 CLI だけ（Web からはさせない）</item>
/// <item>リンクの発行・ロックの解除・紐付け・紐付けの取り消し・未紐付けのログインの参照は、管理 CLI・人事管理者の人・緊急用アカウント。
/// ただし自分のアカウント・自分の人への操作はできない（乗っ取ったセッションで自分の資格情報やログイン経路を足させない）</item>
/// <item>緊急用アカウントには、未紐付けのログインの候補（社用メールが一致した人）を見せない（人事情報を参照させない）</item>
/// <item>緊急用アカウントのリンクはパスキーの登録だけ（パスワードを持たない）</item>
/// <item>ローカルアカウントの無効化は、そのアカウントのセッションを終え、人のアカウントならその人の委任（トークンの系列）もすべて失効する
/// （どのログインで同意したかは記録していないので、広げない側に倒して人ごとに止める）。再有効化ではセッション・委任は戻らない</item>
/// </list>
/// </remarks>
public sealed class AuthAdministrationService
{
    /// <summary>リセットリンクの画面のパス（SPA。鍵は URL のフラグメントに入れ、サーバーのログ・Referer に残さない）。</summary>
    public const string ResetLinkPath = "/account-recovery";

    private readonly IAuthStore _store;
    private readonly IOAuthStore _oauth;
    private readonly AuthAuditor _audit;
    private readonly LoginOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly AccessTokenValidator? _validator;

    /// <param name="validator">参照トークンの検証（同じプロセスにあれば、無効化のときにキャッシュを捨てる。管理 CLI・認可サーバーを使わない構成では無い）。</param>
    public AuthAdministrationService(
        IAuthStore store,
        IOAuthStore oauth,
        AuthAuditor audit,
        LoginOptions options,
        TimeProvider timeProvider,
        AccessTokenValidator? validator = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(oauth);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _store = store;
        _oauth = oauth;
        _audit = audit;
        _options = options;
        _timeProvider = timeProvider;
        _validator = validator;
    }

    /// <summary>ローカルアカウントの一覧（ユーザー名の順。<paramref name="query"/> はユーザー名の部分一致）。</summary>
    public Task<IReadOnlyList<LocalAccountSummary>> ListAccountsAsync(AuthActor actor, string? query, int limit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        RequireOperator(actor);
        return _store.ListAccountsAsync(actor, string.IsNullOrWhiteSpace(query) ? null : query.Trim(), limit, cancellationToken);
    }

    /// <summary>
    /// ローカルアカウントを無効にする（ログインできなくする）。そのアカウントのセッションを終え、人のアカウントならその人の委任をすべて失効する。
    /// 終えたセッションと失効した系列の数を返す。
    /// </summary>
    public async Task<(int Sessions, int Families)> DisableAccountAsync(AuthActor actor, Guid accountId, string? requestId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        RequireOperator(actor);
        var account = await UpdateAccountStateAsync(actor, accountId, AuthAuditActions.AccountDisable, requestId, cancellationToken, account =>
        {
            if (account.DisabledAt is not null)
            {
                throw new AuthAdminException(AuthAdminError.Conflict, "このアカウントは既に無効です。");
            }

            account.DisabledAt = _timeProvider.GetUtcNow();
            return Task.CompletedTask;
        });

        // 無効化を先に確定させてから止める（止めている間に新しいセッション・トークンが出ても、アカウントの無効化で検証に通らない）
        var sessions = await _store.EndAccountSessionsAsync(account.Id, SessionEndKind.AccountDisabled, cancellationToken);
        var families = account.PersonId is { } person
            ? await _oauth.EndFamiliesAsync(null, person, null, TokenFamilyEndKind.Admin, cancellationToken)
            : 0;
        _validator?.Invalidate();
        await _audit.RecordAsync(
            AuthAuditor.ForOperator(actor, AuthAuditActions.AccountDisable, AuditOutcome.Success, AuthAuditTargets.LocalAccount, account.Id, Subjects(account), requestId),
            cancellationToken);
        return (sessions, families);
    }

    /// <summary>無効にしたローカルアカウントを有効に戻す（終えたセッション・失効した委任は戻らない）。</summary>
    public async Task EnableAccountAsync(AuthActor actor, Guid accountId, string? requestId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        RequireOperator(actor);
        var account = await UpdateAccountStateAsync(actor, accountId, AuthAuditActions.AccountEnable, requestId, cancellationToken, async account =>
        {
            if (account.DisabledAt is null)
            {
                throw new AuthAdminException(AuthAdminError.Conflict, "このアカウントは無効になっていません。");
            }

            // 有効な人のアカウントは 1 人に 1 つ（無効にした後に作り直していれば戻せない）
            if (account.PersonId is { } person && await _store.FindAccountByPersonAsync(actor, person, cancellationToken) is not null)
            {
                throw new AuthAdminException(AuthAdminError.Conflict, "この人には別の有効なローカルアカウントがあります。");
            }

            account.DisabledAt = null;
        });
        await _audit.RecordAsync(
            AuthAuditor.ForOperator(actor, AuthAuditActions.AccountEnable, AuditOutcome.Success, AuthAuditTargets.LocalAccount, account.Id, Subjects(account), requestId),
            cancellationToken);
    }

    /// <summary>アカウントを読み、自分のアカウントでないことを確かめ、変更して保存する（他の要求と重なったら読み直す）。</summary>
    private async Task<LocalAccount> UpdateAccountStateAsync(
        AuthActor actor,
        Guid accountId,
        string action,
        string? requestId,
        CancellationToken cancellationToken,
        Func<LocalAccount, Task> change)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var account = await _store.FindAccountAsync(actor, accountId, cancellationToken)
                ?? throw new AuthAdminException(AuthAdminError.NotFound, "アカウントがありません。");
            await RequireNotSelfAsync(actor, account, action, requestId, cancellationToken);
            var stamp = account.ConcurrencyStamp;
            await change(account);
            if (await _store.UpdateAccountAsync(actor, account, stamp, cancellationToken))
            {
                return account;
            }
        }

        throw new AuthAdminException(AuthAdminError.Conflict, "アカウントが同時に更新されました。やり直してください。");
    }

    /// <summary>人のローカルアカウントを作り、パスワードを設定するリンクを発行する（管理 CLI）。</summary>
    public async Task<(LocalAccount Account, IssuedResetLink Link)> CreatePersonAccountAsync(Guid personId, string userName, CancellationToken cancellationToken)
    {
        if (!await _store.PersonExistsAsync(personId, cancellationToken))
        {
            throw new AuthAdminException(AuthAdminError.NotFound, $"人 {personId} がありません。");
        }

        if (await _store.FindAccountByPersonAsync(AuthActor.Cli, personId, cancellationToken) is not null)
        {
            throw new AuthAdminException(AuthAdminError.Conflict, $"人 {personId} には有効なローカルアカウントがあります。");
        }

        var account = await CreateAccountAsync(LocalAccountKind.Person, personId, userName, cancellationToken);
        var link = await IssueAsync(AuthActor.Cli, account, ResetLinkPurpose.Password, null, null, cancellationToken);
        return (account, link);
    }

    /// <summary>緊急用の管理者アカウントを作り、パスキーを登録するリンクを発行する（管理 CLI）。</summary>
    public async Task<(LocalAccount Account, IssuedResetLink Link)> CreateBreakGlassAccountAsync(string userName, CancellationToken cancellationToken)
    {
        var account = await CreateAccountAsync(LocalAccountKind.BreakGlass, null, userName, cancellationToken);
        var link = await IssueAsync(AuthActor.Cli, account, ResetLinkPurpose.Passkey, null, null, cancellationToken);
        return (account, link);
    }

    /// <summary>リンクを発行する。</summary>
    public async Task<IssuedResetLink> IssueResetLinkAsync(
        AuthActor actor,
        Guid accountId,
        ResetLinkPurpose purpose,
        TimeSpan? lifetime,
        string? requestId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        RequireOperator(actor);
        var account = await _store.FindAccountAsync(actor, accountId, cancellationToken)
            ?? throw new AuthAdminException(AuthAdminError.NotFound, "アカウントがありません。");
        await RequireNotSelfAsync(actor, account, AuthAuditActions.ResetLinkIssue, requestId, cancellationToken);
        return await IssueAsync(actor, account, purpose, lifetime, requestId, cancellationToken);
    }

    /// <summary>名前でアカウントを引く（管理 CLI）。</summary>
    public async Task<LocalAccount> FindAccountByNameAsync(string userName, CancellationToken cancellationToken) =>
        await _store.FindAccountByNameAsync(AuthActor.Cli, LocalAccountNames.Normalize(userName), cancellationToken)
            ?? throw new AuthAdminException(AuthAdminError.NotFound, $"アカウント {userName} がありません。");

    /// <summary>ロックアウトを解除する（失敗の回数も 0 に戻す）。</summary>
    public async Task UnlockAsync(AuthActor actor, Guid accountId, string? requestId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        RequireOperator(actor);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var account = await _store.FindAccountAsync(actor, accountId, cancellationToken)
                ?? throw new AuthAdminException(AuthAdminError.NotFound, "アカウントがありません。");
            await RequireNotSelfAsync(actor, account, AuthAuditActions.AccountUnlock, requestId, cancellationToken);
            var stamp = account.ConcurrencyStamp;
            account.LockoutEnd = null;
            account.AccessFailedCount = 0;
            if (await _store.UpdateAccountAsync(actor, account, stamp, cancellationToken))
            {
                await _audit.RecordAsync(
                    AuthAuditor.ForOperator(actor, AuthAuditActions.AccountUnlock, AuditOutcome.Success, AuthAuditTargets.LocalAccount, account.Id, Subjects(account), requestId),
                    cancellationToken);
                return;
            }
        }

        throw new AuthAdminException(AuthAdminError.Conflict, "アカウントが同時に更新されました。やり直してください。");
    }

    /// <summary>外部 IdP の主体を人に紐付ける（手動）。</summary>
    public async Task<LoginBinding> CreateBindingAsync(AuthActor actor, BindingRequest request, string? requestId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(request);
        RequireOperator(actor);
        if (actor.Kind == AuthActorKind.HrAdmin && actor.PersonId == request.PersonId)
        {
            await DenyAsync(actor, AuthAuditActions.BindingCreate, AuthAuditTargets.LoginBinding, null, [request.PersonId], requestId, cancellationToken);
            throw new AuthAdminException(AuthAdminError.Forbidden, "自分の人への紐付けは作れません（別の人事管理者が行います）。");
        }

        string provider, issuer, subject;
        if (request.UnboundLoginId is { } unboundId)
        {
            var unbound = await _store.FindUnboundLoginAsync(actor, unboundId, cancellationToken)
                ?? throw new AuthAdminException(AuthAdminError.NotFound, "未紐付けのログインがありません。");
            (provider, issuer, subject) = (unbound.Provider, unbound.Issuer, unbound.Subject);
        }
        else
        {
            var options = _options.FindProvider(request.Provider)
                ?? throw new AuthAdminException(AuthAdminError.Invalid, $"ログイン方式 {request.Provider} は設定にありません。");
            if (string.IsNullOrWhiteSpace(request.Subject))
            {
                throw new AuthAdminException(AuthAdminError.Invalid, "主体の識別子（Entra は oid、汎用 OIDC は sub）を指定してください。");
            }

            provider = options.Name;
            issuer = string.IsNullOrWhiteSpace(request.Issuer) ? options.EffectiveIssuer : request.Issuer.Trim();
            subject = options.ProviderType == LoginProviderType.Entra ? request.Subject.Trim().ToLowerInvariant() : request.Subject.Trim();
        }

        if (!await _store.PersonExistsAsync(request.PersonId, cancellationToken))
        {
            throw new AuthAdminException(AuthAdminError.NotFound, "人がありません。");
        }

        var binding = new LoginBinding(EntityId.New(), provider, issuer, subject, request.PersonId, BindingBasis.Manual, _timeProvider.GetUtcNow());
        var created = await _store.CreateBindingAsync(actor, binding, request.UnboundLoginId, cancellationToken)
            ?? throw new AuthAdminException(AuthAdminError.Conflict, "この主体には既に有効な紐付けがあります（変更するには先に取り消してください）。");
        await _audit.RecordAsync(
            AuthAuditor.ForOperator(actor, AuthAuditActions.BindingCreate, AuditOutcome.Success, AuthAuditTargets.LoginBinding, created.Id, [created.PersonId], requestId),
            cancellationToken);
        return created;
    }

    /// <summary>紐付けを取り消す（その紐付けのセッションは次の要求から無効）。</summary>
    public async Task RevokeBindingAsync(AuthActor actor, Guid bindingId, string? requestId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        RequireOperator(actor);
        var binding = await _store.FindBindingAsync(actor, bindingId, cancellationToken)
            ?? throw new AuthAdminException(AuthAdminError.NotFound, "紐付けがありません。");
        if (actor.Kind == AuthActorKind.HrAdmin && actor.PersonId == binding.PersonId)
        {
            await DenyAsync(actor, AuthAuditActions.BindingRevoke, AuthAuditTargets.LoginBinding, binding.Id, [binding.PersonId], requestId, cancellationToken);
            throw new AuthAdminException(AuthAdminError.Forbidden, "自分の人の紐付けは取り消せません。");
        }

        if (!await _store.RevokeBindingAsync(actor, bindingId, cancellationToken))
        {
            throw new AuthAdminException(AuthAdminError.NotFound, "有効な紐付けがありません。");
        }

        await _audit.RecordAsync(
            AuthAuditor.ForOperator(actor, AuthAuditActions.BindingRevoke, AuditOutcome.Success, AuthAuditTargets.LoginBinding, binding.Id, [binding.PersonId], requestId),
            cancellationToken);
    }

    /// <summary>未紐付けのログインの一覧（新しい試行の順）。緊急用アカウントには候補を見せない。</summary>
    public async Task<IReadOnlyList<UnboundLogin>> ListUnboundLoginsAsync(AuthActor actor, bool includeResolved, int limit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        RequireOperator(actor);
        var rows = await _store.ListUnboundLoginsAsync(actor, includeResolved, limit, cancellationToken);
        return actor.Kind == AuthActorKind.BreakGlass ? [.. rows.Select(r => r with { CandidatePersonIds = [] })] : rows;
    }

    /// <summary>ロールを付与する（管理 CLI）。</summary>
    public async Task GrantRoleAsync(Guid personId, string role, CancellationToken cancellationToken)
    {
        RequireKnownRole(role);
        if (!await _store.PersonExistsAsync(personId, cancellationToken))
        {
            throw new AuthAdminException(AuthAdminError.NotFound, $"人 {personId} がありません。");
        }

        var id = EntityId.New();
        if (!await _store.GrantRoleAsync(AuthActor.Cli, id, personId, role, cancellationToken))
        {
            throw new AuthAdminException(AuthAdminError.Conflict, $"人 {personId} は既に {role} を持っています。");
        }

        await _audit.RecordAsync(
            AuthAuditor.ForOperator(AuthActor.Cli, AuthAuditActions.RoleGrant, AuditOutcome.Success, AuthAuditTargets.RoleGrant, id, [personId], null),
            cancellationToken);
    }

    /// <summary>ロールを取り消す（管理 CLI）。</summary>
    public async Task RevokeRoleAsync(Guid personId, string role, CancellationToken cancellationToken)
    {
        RequireKnownRole(role);
        if (!await _store.RevokeRoleAsync(AuthActor.Cli, personId, role, cancellationToken))
        {
            throw new AuthAdminException(AuthAdminError.NotFound, $"人 {personId} の {role} の有効な付与がありません。");
        }

        await _audit.RecordAsync(
            AuthAuditor.ForOperator(AuthActor.Cli, AuthAuditActions.RoleRevoke, AuditOutcome.Success, AuthAuditTargets.RoleGrant, null, [personId], null),
            cancellationToken);
    }

    private async Task<LocalAccount> CreateAccountAsync(LocalAccountKind kind, Guid? personId, string userName, CancellationToken cancellationToken)
    {
        if (!LocalAccountNames.IsValid(userName))
        {
            throw new AuthAdminException(AuthAdminError.Invalid, "ユーザー名は英数字と . _ - @ の 3〜64 文字にしてください。");
        }

        var account = new LocalAccount
        {
            Id = EntityId.New(),
            Kind = kind,
            PersonId = personId,
            UserName = userName,
            NormalizedUserName = LocalAccountNames.Normalize(userName),
            SecurityStamp = AuthSecrets.NewToken(),
            ConcurrencyStamp = Guid.NewGuid().ToString("D"),
        };
        if (!await _store.CreateAccountAsync(account, cancellationToken))
        {
            throw new AuthAdminException(AuthAdminError.Conflict, $"ユーザー名 {userName} は使われています。");
        }

        await _audit.RecordAsync(
            AuthAuditor.ForOperator(AuthActor.Cli, AuthAuditActions.AccountCreate, AuditOutcome.Success, AuthAuditTargets.LocalAccount, account.Id, Subjects(account), null),
            cancellationToken);
        return account;
    }

    private async Task<IssuedResetLink> IssueAsync(
        AuthActor actor,
        LocalAccount account,
        ResetLinkPurpose purpose,
        TimeSpan? lifetime,
        string? requestId,
        CancellationToken cancellationToken)
    {
        if (account.IsBreakGlass && purpose != ResetLinkPurpose.Passkey)
        {
            throw new AuthAdminException(AuthAdminError.Invalid, "緊急用アカウントのリンクはパスキーの登録だけです（パスワードを持ちません）。");
        }

        if (account.DisabledAt is not null)
        {
            throw new AuthAdminException(AuthAdminError.Invalid, "無効にしたアカウントのリンクは発行できません。");
        }

        var duration = lifetime ?? _options.ResetLinkLifetime;
        if (duration <= TimeSpan.Zero || duration > TimeSpan.FromHours(24))
        {
            throw new AuthAdminException(AuthAdminError.Invalid, "リンクの有効期間は 24 時間以内で指定してください。");
        }

        var token = AuthSecrets.NewToken();
        var linkId = EntityId.New();
        var expiresAt = _timeProvider.GetUtcNow() + duration;
        await _store.IssueResetLinkAsync(actor, linkId, account.Id, purpose, AuthSecrets.Sha256(token), expiresAt, cancellationToken);
        await _audit.RecordAsync(
            AuthAuditor.ForOperator(actor, AuthAuditActions.ResetLinkIssue, AuditOutcome.Success, AuthAuditTargets.ResetLink, linkId, Subjects(account), requestId),
            cancellationToken);
        var origin = _options.OriginString();
        var url = origin is null ? null : $"{origin}{ResetLinkPath}?purpose={Code(purpose)}#token={token}";
        return new IssuedResetLink(linkId, account.Id, purpose, token, expiresAt, url);
    }

    private async Task RequireNotSelfAsync(AuthActor actor, LocalAccount account, string action, string? requestId, CancellationToken cancellationToken)
    {
        var self = (actor.Kind == AuthActorKind.BreakGlass && actor.AccountId == account.Id)
            || (actor.Kind == AuthActorKind.HrAdmin && account.PersonId is { } person && actor.PersonId == person);
        if (self)
        {
            await DenyAsync(actor, action, AuthAuditTargets.LocalAccount, account.Id, Subjects(account), requestId, cancellationToken);
            throw new AuthAdminException(AuthAdminError.Forbidden, "自分のアカウントには使えません（別の管理者が行います）。");
        }
    }

    private Task DenyAsync(AuthActor actor, string action, string targetType, Guid? targetId, IReadOnlyList<Guid> subjects, string? requestId, CancellationToken cancellationToken) =>
        _audit.RecordAsync(AuthAuditor.ForOperator(actor, action, AuditOutcome.Denied, targetType, targetId, subjects, requestId), cancellationToken);

    private static void RequireOperator(AuthActor actor)
    {
        if (actor.Kind == AuthActorKind.System)
        {
            throw new InvalidOperationException("認証の管理は管理 CLI・人事管理者の人・緊急用アカウントとして行います。");
        }
    }

    private static void RequireKnownRole(string role)
    {
        if (!Roles.Known.Contains(role))
        {
            throw new AuthAdminException(AuthAdminError.Invalid, $"未知のロールです: {role}（{string.Join(", ", Roles.Known)}）");
        }
    }

    private static IReadOnlyList<Guid> Subjects(LocalAccount account) => account.PersonId is { } person ? [person] : [];

    public static string Code(ResetLinkPurpose purpose) => purpose switch
    {
        ResetLinkPurpose.Password => "password",
        ResetLinkPurpose.Passkey => "passkey",
        _ => throw new ArgumentOutOfRangeException(nameof(purpose)),
    };

    public static ResetLinkPurpose ParsePurpose(string? code) => code switch
    {
        "password" => ResetLinkPurpose.Password,
        "passkey" => ResetLinkPurpose.Passkey,
        _ => throw new AuthAdminException(AuthAdminError.Invalid, $"リンクの用途は password か passkey です: {code}"),
    };
}
