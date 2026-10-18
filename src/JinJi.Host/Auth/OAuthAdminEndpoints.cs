using JinJi.Api;
using JinJi.Application.Access;
using JinJi.Application.Auth;
using Microsoft.AspNetCore.Mvc;

namespace JinJi.Host.Auth;

/// <summary>
/// トークン発行のクライアントの管理（<c>/auth/admin/oauth</c>。API のポート）。クライアントの登録・変更・停止と再開・承認待ちの CIMD の承認と却下・
/// 公開鍵・移行用の client_secret・CIMD の許可ドメイン・トークンの系列の失効・先行提供の日数の起案と承認（0016）・社内システムへの Webhook（0018。
/// <c>OAuthAdminEndpoints.Webhooks.cs</c>）。
/// </summary>
/// <remarks>
/// 使えるのは UI のセッションの人事管理者の人だけ。トークン（AI エージェント・社内システム）と緊急用アカウント（認証の復旧ではない）は使えない。
/// 状態を変える要求は CSRF のトークンが要る（<c>/auth</c> の下）。操作はすべて監査ログに残す。管理 CLI でも同じことができる。
/// </remarks>
internal static partial class OAuthAdminEndpoints
{
    public static IEndpointRouteBuilder MapJinJiOAuthAdmin(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var admin = endpoints.MapGroup(AuthEndpoints.Prefix + "/admin/oauth")
            .WithTags("auth")
            .RequireAuthorization()
            .WithMetadata(new RequestKindMetadata(RequestKind.Admin, false));

        admin.MapGet("/clients", ListAsync)
            .WithSummary("トークン発行のクライアントの一覧（status で絞れる。承認待ちは pending_approval）").Produces<IReadOnlyList<OAuthClientResponse>>();
        admin.MapPost("/clients", RegisterAsync)
            .WithSummary("クライアントを事前登録する").Produces<OAuthClientResponse>();
        admin.MapPost("/clients/{clientRef}/update", UpdateAsync)
            .WithSummary("クライアントの登録を変える（指定した項目だけ）").Produces<OAuthClientResponse>();
        admin.MapPost("/clients/{clientRef}/{action:regex(^(suspend|activate|approve|reject)$)}", SetStatusAsync)
            .WithSummary("クライアントを停止・再開する、承認待ちの CIMD のクライアントを承認・却下する").Produces<OAuthClientResponse>();
        admin.MapPost("/clients/{clientRef}/keys", AddKeyAsync)
            .WithSummary("private_key_jwt の公開鍵を登録する").Produces(StatusCodes.Status204NoContent);
        admin.MapPost("/clients/{clientRef}/keys/{keyId}/revoke", RevokeKeyAsync)
            .WithSummary("公開鍵を取り消す").Produces(StatusCodes.Status204NoContent);
        admin.MapPost("/clients/{clientRef}/secrets", IssueSecretAsync)
            .WithSummary("移行用の client_secret を発行する（secret はこの応答でだけ返る）").Produces<ClientSecretResponse>();
        admin.MapPost("/clients/{clientRef}/secrets/revoke", RevokeSecretsAsync)
            .WithSummary("client_secret をすべて取り消す").Produces(StatusCodes.Status204NoContent);
        admin.MapPost("/clients/{clientRef}/revoke-tokens", RevokeTokensAsync)
            .WithSummary("クライアントのトークンの系列を失効する（person_id を指定すればその人の分だけ）").Produces<RevokedTokensResponse>();
        admin.MapGet("/disclosure-leads", ListDisclosureLeadsAsync)
            .WithSummary("先行提供の日数の起案の一覧（新しい順。client でクライアントを絞れる）").Produces<IReadOnlyList<DisclosureLeadResponse>>();
        admin.MapPost("/clients/{clientRef}/disclosure-leads", ProposeDisclosureLeadAsync)
            .WithSummary("社内システム（client credentials）の先行提供の日数（有効日の何日前から将来の予定を見せるか。0〜90）を起案する。別の人事管理者の承認で有効になる")
            .Produces<DisclosureLeadResponse>();
        admin.MapPost("/disclosure-leads/{leadId}/{action:regex(^(approve|reject)$)}", DecideDisclosureLeadAsync)
            .WithSummary("先行提供の日数の起案を承認・却下する（起案者本人は不可）").Produces<DisclosureLeadResponse>();
        admin.MapGet("/cimd-domains", ListDomainsAsync)
            .WithSummary("CIMD の取得を許すドメイン（設定の既定と、管理者が足したもの）").Produces<CimdDomainsResponse>();
        admin.MapPost("/cimd-domains", AddDomainAsync)
            .WithSummary("CIMD の取得を許すドメインを足す").Produces(StatusCodes.Status204NoContent);
        admin.MapPost("/cimd-domains/{domain}/remove", RemoveDomainAsync)
            .WithSummary("CIMD の取得を許すドメインを外す（そのドメインから登録済みのクライアントは停止しない）").Produces(StatusCodes.Status204NoContent);
        MapWebhooks(admin);
        return endpoints;
    }

    private static Task<IResult> ListAsync(HttpContext http, OAuthClientAdministrationService admin, [FromQuery] string? status) =>
        RunAsync(http, async actor =>
        {
            OAuthClientStatus? filter = null;
            if (status is not null)
            {
                try
                {
                    filter = OAuthCodes.ParseStatus(status);
                }
                catch (ArgumentException)
                {
                    return Problems.BadRequest(http, "status は active / pending_approval / suspended / rejected のいずれかにしてください。");
                }
            }

            var clients = await admin.ListAsync(actor, filter, http.RequestAborted);
            return Results.Ok(clients.Select(OAuthClientResponse.From).ToList());
        });

    private static Task<IResult> RegisterAsync(RegisterClientRequest request, HttpContext http, OAuthClientAdministrationService admin) =>
        RunAsync(http, async actor =>
        {
            var client = await admin.RegisterAsync(actor, request.ToRegistration(), http.TraceIdentifier, http.RequestAborted);
            return Results.Ok(OAuthClientResponse.From(client));
        });

    private static Task<IResult> UpdateAsync(string clientRef, UpdateClientRequest request, HttpContext http, OAuthClientAdministrationService admin) =>
        WithClientAsync(http, clientRef, async (actor, id) =>
            Results.Ok(OAuthClientResponse.From(await admin.UpdateAsync(actor, id, request.ToUpdate(), http.TraceIdentifier, http.RequestAborted))));

    private static Task<IResult> SetStatusAsync(string clientRef, string action, HttpContext http, OAuthClientAdministrationService admin) =>
        WithClientAsync(http, clientRef, async (actor, id) =>
        {
            var status = action switch
            {
                "suspend" => OAuthClientStatus.Suspended,
                "reject" => OAuthClientStatus.Rejected,
                _ => OAuthClientStatus.Active,
            };
            return Results.Ok(OAuthClientResponse.From(await admin.SetStatusAsync(actor, id, status, http.TraceIdentifier, http.RequestAborted)));
        });

    private static Task<IResult> AddKeyAsync(string clientRef, AddClientKeyRequest request, HttpContext http, OAuthClientAdministrationService admin) =>
        WithClientAsync(http, clientRef, async (actor, id) =>
        {
            await admin.AddKeyAsync(actor, id, request.KeyId ?? string.Empty, request.PublicKeyPem ?? string.Empty, http.TraceIdentifier, http.RequestAborted);
            return Results.NoContent();
        });

    private static Task<IResult> RevokeKeyAsync(string clientRef, string keyId, HttpContext http, OAuthClientAdministrationService admin) =>
        WithClientAsync(http, clientRef, async (actor, id) =>
        {
            await admin.RevokeKeyAsync(actor, id, keyId, http.TraceIdentifier, http.RequestAborted);
            return Results.NoContent();
        });

    private static Task<IResult> IssueSecretAsync(string clientRef, IssueClientSecretRequest request, HttpContext http, OAuthClientAdministrationService admin) =>
        WithClientAsync(http, clientRef, async (actor, id) =>
        {
            TimeSpan? lifetime = request.LifetimeDays is { } days ? TimeSpan.FromDays(days) : null;
            var secret = await admin.IssueSecretAsync(actor, id, lifetime, http.TraceIdentifier, http.RequestAborted);
            return Results.Ok(new ClientSecretResponse(secret.Id, secret.Secret, secret.ExpiresAt));
        });

    private static Task<IResult> RevokeSecretsAsync(string clientRef, HttpContext http, OAuthClientAdministrationService admin) =>
        WithClientAsync(http, clientRef, async (actor, id) =>
        {
            await admin.RevokeSecretsAsync(actor, id, http.TraceIdentifier, http.RequestAborted);
            return Results.NoContent();
        });

    private static Task<IResult> RevokeTokensAsync(string clientRef, RevokeClientTokensRequest request, HttpContext http, OAuthClientAdministrationService admin) =>
        WithClientAsync(http, clientRef, async (actor, id) =>
            Results.Ok(new RevokedTokensResponse(await admin.RevokeTokensAsync(actor, id, request.PersonId, null, http.TraceIdentifier, http.RequestAborted))));

    private static Task<IResult> ListDisclosureLeadsAsync(HttpContext http, DisclosureLeadService leads, [FromQuery] string? client) =>
        RunAsync(http, async actor =>
        {
            Guid? clientRef = null;
            if (client is not null)
            {
                if (!Guid.TryParseExact(client, "D", out var parsed))
                {
                    return Problems.BadRequest(http, "client は api_client の ID（UUID）で指定してください。");
                }

                clientRef = parsed;
            }

            var list = await leads.ListAsync(actor, clientRef, http.RequestAborted);
            return Results.Ok(list.Select(DisclosureLeadResponse.From).ToList());
        });

    private static Task<IResult> ProposeDisclosureLeadAsync(string clientRef, ProposeDisclosureLeadRequest request, HttpContext http, DisclosureLeadService leads) =>
        WithClientAsync(http, clientRef, async (actor, id) =>
        {
            if (request.LeadDays is not { } days)
            {
                return Problems.BadRequest(http, "lead_days（0〜90）を指定してください。");
            }

            var proposal = await leads.ProposeAsync(actor, id, days, request.Note, null, http.TraceIdentifier, http.RequestAborted);
            return Results.Ok(DisclosureLeadResponse.From(proposal));
        });

    private static Task<IResult> DecideDisclosureLeadAsync(string leadId, string action, HttpContext http, DisclosureLeadService leads) =>
        RunAsync(http, async actor => Guid.TryParseExact(leadId, "D", out var id)
            ? Results.Ok(DisclosureLeadResponse.From(await leads.DecideAsync(actor, id, action == "approve", http.TraceIdentifier, http.RequestAborted)))
            : Problems.NotFound(http));

    private static Task<IResult> ListDomainsAsync(HttpContext http, OAuthClientAdministrationService admin) =>
        RunAsync(http, async actor =>
        {
            var (configured, added) = await admin.ListDomainsAsync(actor, http.RequestAborted);
            return Results.Ok(new CimdDomainsResponse(configured, [.. added.Select(d => d.Domain)]));
        });

    private static Task<IResult> AddDomainAsync(AddCimdDomainRequest request, HttpContext http, OAuthClientAdministrationService admin) =>
        RunAsync(http, async actor =>
        {
            await admin.AddDomainAsync(actor, request.Domain ?? string.Empty, http.TraceIdentifier, http.RequestAborted);
            return Results.NoContent();
        });

    private static Task<IResult> RemoveDomainAsync(string domain, HttpContext http, OAuthClientAdministrationService admin) =>
        RunAsync(http, async actor =>
        {
            await admin.RemoveDomainAsync(actor, domain, http.TraceIdentifier, http.RequestAborted);
            return Results.NoContent();
        });

    private static Task<IResult> WithClientAsync(HttpContext http, string clientRef, Func<AuthActor, Guid, Task<IResult>> action) =>
        RunAsync(http, actor => Guid.TryParseExact(clientRef, "D", out var id) ? action(actor, id) : Task.FromResult(Problems.NotFound(http)));

    /// <summary>操作者（UI のセッションの人事管理者の人）。それ以外は 403。</summary>
    private static async Task<IResult> RunAsync(HttpContext http, Func<AuthActor, Task<IResult>> action)
    {
        if (!SessionClaims.IsSession(http.User)
            || http.Features.Get<AccessContext>()?.Viewer is not { Channel: Policy.Channel.Ui, PersonId: { } person } viewer
            || !AdminAccess.IsHrAdminUser(viewer))
        {
            return Problems.Forbidden(http, "クライアントの管理は、UI でログインした人事管理者だけが使えます。");
        }

        try
        {
            return await action(AuthActor.HrAdmin(person));
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
}

/// <summary>クライアントの事前登録。</summary>
internal sealed record RegisterClientRequest(
    string? ClientId,
    string? DisplayName,
    [property: OpenApiEnum("none", "private_key_jwt")] string? AuthMethod,
    IReadOnlyList<string>? GrantTypes,
    IReadOnlyList<string>? RedirectUris,
    IReadOnlyList<string>? Resources,
    IReadOnlyList<string>? Scopes,
    IReadOnlyList<string>? Fields,
    IReadOnlyList<string>? McpTools,
    int? RateLimitPerClient)
{
    public ClientRegistrationRequest ToRegistration()
    {
        try
        {
            return new ClientRegistrationRequest(
                ClientId ?? string.Empty,
                DisplayName,
                OAuthCodes.ParseAuthMethod(AuthMethod ?? "none"),
                GrantTypes ?? [],
                RedirectUris ?? [],
                [.. (Resources ?? []).Select(OAuthCodes.ParseResource)],
                Scopes ?? [OAuthScopes.Read],
                Fields ?? [],
                McpTools ?? [],
                RateLimitPerClient);
        }
        catch (ArgumentException ex)
        {
            throw new AuthAdminException(AuthAdminError.Invalid, ex.Message);
        }
    }
}

/// <summary>クライアントの登録の変更（指定した項目だけ変える）。</summary>
internal sealed record UpdateClientRequest(
    string? DisplayName,
    IReadOnlyList<string>? RedirectUris,
    IReadOnlyList<string>? Resources,
    IReadOnlyList<string>? Scopes,
    IReadOnlyList<string>? Fields,
    IReadOnlyList<string>? McpTools,
    int? RateLimitPerClient,
    bool? ClearRateLimit)
{
    public ClientUpdateRequest ToUpdate()
    {
        try
        {
            return new ClientUpdateRequest(
                DisplayName,
                RedirectUris,
                Resources is null ? null : [.. Resources.Select(OAuthCodes.ParseResource)],
                Scopes,
                Fields,
                McpTools,
                RateLimitPerClient,
                ClearRateLimit ?? false);
        }
        catch (ArgumentException ex)
        {
            throw new AuthAdminException(AuthAdminError.Invalid, ex.Message);
        }
    }
}

internal sealed record AddClientKeyRequest(string? KeyId, string? PublicKeyPem);

internal sealed record IssueClientSecretRequest(int? LifetimeDays);

internal sealed record RevokeClientTokensRequest(Guid? PersonId);

internal sealed record AddCimdDomainRequest(string? Domain);

/// <summary>先行提供の日数の起案。</summary>
/// <param name="LeadDays">有効日の何日前から将来の予定を見せるか（0〜90。0 は例外なし = 既定に戻す）。</param>
/// <param name="Note">理由・用途のメモ（任意、500 文字まで。人事データの値を書かない）。</param>
internal sealed record ProposeDisclosureLeadRequest(int? LeadDays, string? Note);

/// <summary>先行提供の日数の起案（1 行）。</summary>
/// <param name="ClientRef">クライアント（api_client.id）。</param>
/// <param name="ProposedByKind">person（UI の人事管理者）/ cli（管理 CLI。起案者は指定された人事管理者）。</param>
internal sealed record DisclosureLeadResponse(
    Guid Id,
    Guid ClientRef,
    int LeadDays,
    [property: OpenApiEnum(typeof(DisclosureLeadStatus))] string Status,
    string? Note,
    [property: OpenApiEnum("person", "cli")] string ProposedByKind,
    Guid ProposedBy,
    DateTimeOffset ProposedAt,
    Guid? DecidedBy,
    DateTimeOffset? DecidedAt,
    DateTimeOffset? SupersededAt)
{
    public static DisclosureLeadResponse From(DisclosureLead lead) => new(
        lead.Id,
        lead.ClientRef,
        lead.LeadDays,
        System.Text.Json.JsonNamingPolicy.SnakeCaseLower.ConvertName(lead.Status.ToString()),
        lead.Note,
        lead.ProposedByKind,
        lead.ProposedBy,
        lead.ProposedAt,
        lead.DecidedBy,
        lead.DecidedAt,
        lead.SupersededAt);
}

/// <summary>トークン発行のクライアント。</summary>
/// <param name="DisclosureLeadDays">先行提供の日数（承認済み。0 は例外なし）。変えるときは起案と別の人事管理者の承認（/disclosure-leads）。</param>
internal sealed record OAuthClientResponse(
    Guid Id,
    string ClientId,
    [property: OpenApiEnum("registered", "cimd")] string Registration,
    [property: OpenApiEnum("active", "pending_approval", "suspended", "rejected")] string Status,
    string? DisplayName,
    [property: OpenApiEnum("none", "private_key_jwt")] string AuthMethod,
    IReadOnlyList<string> GrantTypes,
    IReadOnlyList<string> RedirectUris,
    IReadOnlyList<string> Resources,
    IReadOnlyList<string> Scopes,
    IReadOnlyList<string> Fields,
    IReadOnlyList<string> McpTools,
    int? RateLimitPerClient,
    DateTimeOffset? MetadataExpiresAt,
    DateTimeOffset CreatedAt,
    int DisclosureLeadDays)
{
    public static OAuthClientResponse From(OAuthClient client) => new(
        client.Id,
        client.ClientId,
        OAuthCodes.ToCode(client.Registration),
        OAuthCodes.ToCode(client.Status),
        client.DisplayName,
        OAuthCodes.ToCode(client.AuthMethod),
        client.GrantTypes,
        client.RedirectUris,
        [.. client.Resources.Select(OAuthCodes.ToCode)],
        client.Scopes,
        client.Fields,
        client.McpTools,
        client.RateLimitPerClient,
        client.MetadataExpiresAt,
        client.CreatedAt,
        client.DisclosureLeadDays);
}

/// <summary>発行した client_secret。secret はこの応答でだけ返る（DB には SHA-256 だけ）。</summary>
internal sealed record ClientSecretResponse(Guid Id, string Secret, DateTimeOffset ExpiresAt)
{
    public override string ToString() => $"ClientSecretResponse {{ Id = {Id:D} }}";
}

internal sealed record RevokedTokensResponse(int Revoked);

internal sealed record CimdDomainsResponse(IReadOnlyList<string> Configured, IReadOnlyList<string> Added);
