using System.Globalization;
using JinJi.Api;
using JinJi.Application.Access;
using JinJi.Application.Auth;
using Microsoft.AspNetCore.Mvc;

namespace JinJi.Host.Auth;

/// <summary>
/// 認証の管理（<c>/auth/admin</c>）。未紐付けのログインの一覧、手動の紐付けと取り消し、ローカルアカウントの一覧・ロックの解除・無効化と再有効化、リンクの発行。
/// </summary>
/// <remarks>
/// 使えるのは UI のセッションの人事管理者の人と、緊急用の管理者アカウント（認証の復旧。人事情報は見えない）だけ。
/// トークン（Bearer）・AI エージェントは使えない（認証の管理はエージェントにさせない）。操作はすべて監査ログに残す。
/// </remarks>
internal static class AuthAdminEndpoints
{
    public static void Map(RouteGroupBuilder admin)
    {
        admin.MapGet("/unbound-logins", ListUnboundAsync)
            .WithSummary("紐付けできずに拒否したログインの一覧（人事管理者・緊急用アカウント）").Produces<IReadOnlyList<UnboundLoginResponse>>();
        admin.MapPost("/login-bindings", BindAsync)
            .WithSummary("外部 IdP の主体を人に紐付ける（未紐付けのログインから、またはログイン方式と主体の識別子で）").Produces<LoginBindingResponse>();
        admin.MapPost("/login-bindings/{bindingId}/revoke", RevokeAsync)
            .WithSummary("紐付けを取り消す（その紐付けのセッションも終える）").Produces(StatusCodes.Status204NoContent);
        admin.MapGet("/local-accounts", ListAccountsAsync)
            .WithSummary("ローカルアカウント（人のアカウント・緊急用アカウント）の一覧。query はユーザー名の部分一致").Produces<IReadOnlyList<LocalAccountResponse>>();
        admin.MapPost("/local-accounts/{accountId}/disable", DisableAsync)
            .WithSummary("ローカルアカウントを無効にする（セッションを終え、人のアカウントならその人の委任をすべて失効する）").Produces<DisableAccountResponse>();
        admin.MapPost("/local-accounts/{accountId}/enable", EnableAsync)
            .WithSummary("無効にしたローカルアカウントを有効に戻す（終えたセッション・失効した委任は戻らない）").Produces(StatusCodes.Status204NoContent);
        admin.MapPost("/local-accounts/{accountId}/unlock", UnlockAsync)
            .WithSummary("ローカルアカウントのロックアウトを解除する").Produces(StatusCodes.Status204NoContent);
        admin.MapPost("/local-accounts/{accountId}/reset-links", IssueLinkAsync)
            .WithSummary("パスワードの設定・パスキーの登録の 1 回限りのリンクを発行する").Produces<ResetLinkResponse>();
    }

    private static async Task<IResult> ListUnboundAsync(
        HttpContext http,
        AuthAdministrationService admin,
        [FromQuery(Name = "include_resolved")] bool? includeResolved,
        [FromQuery] int? limit)
    {
        if (Operator(http) is not { } actor)
        {
            return Forbidden(http);
        }

        if (limit is < 1 or > 200)
        {
            return Problems.BadRequest(http, "limit は 1〜200 で指定してください。");
        }

        var rows = await admin.ListUnboundLoginsAsync(actor, includeResolved ?? false, limit ?? 100, http.RequestAborted);
        return Results.Ok(rows.Select(UnboundLoginResponse.From).ToList());
    }

    private static Task<IResult> BindAsync(CreateLoginBindingRequest request, HttpContext http, AuthAdministrationService admin) =>
        RunAsync(http, async actor =>
        {
            var binding = await admin.CreateBindingAsync(actor, new BindingRequest(request.PersonId, request.UnboundLoginId, request.Provider, request.Subject, null), http.TraceIdentifier, http.RequestAborted);
            return Results.Ok(LoginBindingResponse.From(binding));
        });

    private static Task<IResult> RevokeAsync(string bindingId, HttpContext http, AuthAdministrationService admin) =>
        RunAsync(http, async actor =>
        {
            if (!Guid.TryParseExact(bindingId, "D", out var id))
            {
                return Problems.NotFound(http);
            }

            await admin.RevokeBindingAsync(actor, id, http.TraceIdentifier, http.RequestAborted);
            return Results.NoContent();
        });

    private static async Task<IResult> ListAccountsAsync(
        HttpContext http,
        AuthAdministrationService admin,
        [FromQuery] string? query,
        [FromQuery] int? limit)
    {
        if (Operator(http) is not { } actor)
        {
            return Forbidden(http);
        }

        if (limit is < 1 or > 500)
        {
            return Problems.BadRequest(http, "limit は 1〜500 で指定してください。");
        }

        if (query is { Length: > 64 })
        {
            return Problems.BadRequest(http, "query は 64 文字以内で指定してください。");
        }

        var rows = await admin.ListAccountsAsync(actor, query, limit ?? 200, http.RequestAborted);
        return Results.Ok(rows.Select(LocalAccountResponse.From).ToList());
    }

    private static Task<IResult> DisableAsync(string accountId, HttpContext http, AuthAdministrationService admin) =>
        RunAsync(http, async actor =>
        {
            if (!Guid.TryParseExact(accountId, "D", out var id))
            {
                return Problems.NotFound(http);
            }

            var (sessions, families) = await admin.DisableAccountAsync(actor, id, http.TraceIdentifier, http.RequestAborted);
            return Results.Ok(new DisableAccountResponse(sessions, families));
        });

    private static Task<IResult> EnableAsync(string accountId, HttpContext http, AuthAdministrationService admin) =>
        RunAsync(http, async actor =>
        {
            if (!Guid.TryParseExact(accountId, "D", out var id))
            {
                return Problems.NotFound(http);
            }

            await admin.EnableAccountAsync(actor, id, http.TraceIdentifier, http.RequestAborted);
            return Results.NoContent();
        });

    private static Task<IResult> UnlockAsync(string accountId, HttpContext http, AuthAdministrationService admin) =>
        RunAsync(http, async actor =>
        {
            if (!Guid.TryParseExact(accountId, "D", out var id))
            {
                return Problems.NotFound(http);
            }

            await admin.UnlockAsync(actor, id, http.TraceIdentifier, http.RequestAborted);
            return Results.NoContent();
        });

    private static Task<IResult> IssueLinkAsync(string accountId, IssueResetLinkRequest request, HttpContext http, AuthAdministrationService admin) =>
        RunAsync(http, async actor =>
        {
            if (!Guid.TryParseExact(accountId, "D", out var id))
            {
                return Problems.NotFound(http);
            }

            var link = await admin.IssueResetLinkAsync(actor, id, AuthAdministrationService.ParsePurpose(request.Purpose), null, http.TraceIdentifier, http.RequestAborted);
            return Results.Ok(new ResetLinkResponse(link.LinkId, link.AccountId, AuthAdministrationService.Code(link.Purpose), link.Url, link.Token, link.ExpiresAt));
        });

    /// <summary>操作者（UI のセッションの人事管理者の人、または緊急用アカウント）。それ以外は null。</summary>
    private static AuthActor? Operator(HttpContext http)
    {
        if (SessionClaims.BreakGlassAccountOf(http.User) is { } account)
        {
            return AuthActor.BreakGlass(account);
        }

        return http.Features.Get<AccessContext>()?.Viewer is { Channel: Policy.Channel.Ui, PersonId: { } person } viewer && AdminAccess.IsHrAdminUser(viewer)
            ? AuthActor.HrAdmin(person)
            : null;
    }

    private static async Task<IResult> RunAsync(HttpContext http, Func<AuthActor, Task<IResult>> action)
    {
        if (Operator(http) is not { } actor)
        {
            return Forbidden(http);
        }

        try
        {
            return await action(actor);
        }
        catch (AuthAdminException ex)
        {
            return ex.Error switch
            {
                AuthAdminError.NotFound => Problems.NotFound(http),
                AuthAdminError.Conflict => Problems.Conflict(http, ex.Message),
                AuthAdminError.Forbidden => Problems.Forbidden(http, ex.Message),
                _ => Problems.BadRequest(http, ex.Message),
            };
        }
    }

    private static IResult Forbidden(HttpContext http) =>
        Problems.Forbidden(http, "認証の管理は、UI でログインした人事管理者か緊急用の管理者アカウントだけが使えます。");
}

internal sealed record CreateLoginBindingRequest(Guid PersonId, Guid? UnboundLoginId, string? Provider, string? Subject);

internal sealed record IssueResetLinkRequest(string? Purpose);

/// <summary>ローカルアカウントの一覧の 1 行（資格情報の中身は返さない）。</summary>
internal sealed record LocalAccountResponse(
    Guid Id,
    [property: OpenApiEnum("person", "break_glass")] string Kind,
    Guid? PersonId,
    string UserName,
    bool HasPassword,
    bool TwoFactorEnabled,
    int PasskeyCount,
    DateTimeOffset? LockoutEnd,
    DateTimeOffset? DisabledAt,
    DateTimeOffset CreatedAt)
{
    public static LocalAccountResponse From(LocalAccountSummary row) => new(
        row.Id, row.Kind == LocalAccountKind.BreakGlass ? "break_glass" : "person", row.PersonId, row.UserName, row.HasPassword, row.TwoFactorEnabled,
        row.PasskeyCount, row.LockoutEnd, row.DisabledAt, row.CreatedAt);
}

/// <summary>無効化で終えたセッションと失効した委任（トークンの系列）の数。</summary>
internal sealed record DisableAccountResponse(int SessionsEnded, int DelegationsRevoked);

internal sealed record UnboundLoginResponse(
    Guid Id,
    string Provider,
    string Issuer,
    string Subject,
    string? TenantId,
    string? EmployeeNumber,
    string? Email,
    string? DisplayName,
    IReadOnlyList<Guid> CandidatePersonIds,
    int AttemptCount,
    DateTimeOffset FirstAttemptedAt,
    DateTimeOffset LastAttemptedAt,
    DateTimeOffset? ResolvedAt,
    Guid? ResolvedBindingId)
{
    public static UnboundLoginResponse From(UnboundLogin row) => new(
        row.Id, row.Provider, row.Issuer, row.Subject, row.TenantId, row.EmployeeNumber, row.Email, row.DisplayName, row.CandidatePersonIds,
        row.AttemptCount, row.FirstAttemptedAt, row.LastAttemptedAt, row.ResolvedAt, row.ResolvedBindingId);
}

internal sealed record LoginBindingResponse(
    Guid Id,
    string Provider,
    string Issuer,
    string Subject,
    Guid PersonId,
    [property: OpenApiEnum(typeof(BindingBasis))] string Basis,
    DateTimeOffset CreatedAt)
{
    public static LoginBindingResponse From(LoginBinding binding) => new(
        binding.Id, binding.Provider, binding.Issuer, binding.Subject, binding.PersonId,
        System.Text.Json.JsonNamingPolicy.SnakeCaseLower.ConvertName(binding.Basis.ToString()), binding.CreatedAt);
}

/// <summary>発行したリンク。token はこの応答でだけ返る（DB にはハッシュだけ）。</summary>
internal sealed record ResetLinkResponse(
    Guid LinkId,
    Guid AccountId,
    [property: OpenApiEnum("password", "passkey")] string Purpose,
    string? Url,
    string Token,
    DateTimeOffset ExpiresAt)
{
    public override string ToString() => $"ResetLinkResponse {{ LinkId = {LinkId.ToString("D", CultureInfo.InvariantCulture)} }}";
}
