using System.Globalization;
using JinJi.Application.Auth;
using JinJi.Host.Auth;

namespace JinJi.Host;

/// <summary>
/// トークン発行のクライアントの管理の CLI（ADR-0004 の段階 B）。接続文字列は <c>ConnectionStrings:App</c>。操作者は管理 CLI（DB の閲覧者は jin-ji 自身）で、すべて監査ログに残す。
/// </summary>
/// <remarks>
/// 一覧の値（--grant-types・--redirect-uris・--resources・--scopes・--fields・--mcp-tools）はカンマ区切り。client_id には事前登録の ID、CIMD の URL、api_client.id のどれでも指定できる。
/// client_secret は出力にだけ現れる（DB には SHA-256 だけ）。安全な経路で渡すこと。
/// </remarks>
internal static partial class OAuthCommands
{
    public const string Usage = """
          JinJi.Host register-client --client-id <id> --name <表示名> [--auth none|private_key_jwt] --grant-types <付与,...> [--redirect-uris <URI,...>]
                                     --resources api|mcp[,...] [--scopes jinji.read[,jinji.propose]] --fields <項目,...|*> [--mcp-tools <名前,...>] [--rate-limit <回数>]
          JinJi.Host update-client --client-id <id> [--name ..] [--redirect-uris ..] [--resources ..] [--scopes ..] [--fields ..] [--mcp-tools ..] [--rate-limit <回数>|none]
          JinJi.Host list-clients [--status active|pending_approval|suspended|rejected]
          JinJi.Host suspend-client | activate-client | approve-client | reject-client --client-id <id>
          JinJi.Host add-client-key --client-id <id> --kid <鍵 ID> --key-file <公開鍵の PEM>
          JinJi.Host revoke-client-key --client-id <id> --kid <鍵 ID>
          JinJi.Host issue-client-secret --client-id <id> [--lifetime <日数.hh:mm:ss>]
          JinJi.Host revoke-client-secrets --client-id <id>
          JinJi.Host allow-cimd-domain | disallow-cimd-domain --domain <ホスト名>
          JinJi.Host list-cimd-domains
          JinJi.Host revoke-tokens (--client-id <id> [--person <person-id>] | --person <person-id> | --family <系列 ID>)
          JinJi.Host propose-disclosure-lead --client-id <id> --days <0〜90> --proposed-by <人事管理者の person-id> [--note <メモ>]
          JinJi.Host list-disclosure-leads [--client-id <id>]
                                                    トークン発行のクライアント・鍵・シークレット・CIMD の許可ドメイン・トークンの失効・
                                                    先行提供の日数の起案と一覧（承認・却下は UI でログインした別の人事管理者）
        """;

    public static readonly IReadOnlySet<string> Commands = new HashSet<string>(StringComparer.Ordinal)
    {
        "register-client", "update-client", "list-clients", "suspend-client", "activate-client", "approve-client", "reject-client",
        "add-client-key", "revoke-client-key", "issue-client-secret", "revoke-client-secrets",
        "allow-cimd-domain", "disallow-cimd-domain", "list-cimd-domains", "revoke-tokens",
        "propose-disclosure-lead", "list-disclosure-leads",
    };

    public static async Task<int> RunAsync(string command, string[] args, TextWriter? output = null)
    {
        output ??= Console.Out;
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(args);
        var configuration = builder.Configuration;
        string? Option(string name) => configuration[name] is { Length: > 0 } value ? value : null;
        IReadOnlyList<string>? List(string name) => Option(name)?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Guid? Id(string name) => Guid.TryParseExact(Option(name), "D", out var id) ? id : null;
        var actor = AuthActor.Cli;
        var ct = CancellationToken.None;
        DisclosureLeadService? leads = null;

        Func<OAuthClientAdministrationService, Task>? action = command switch
        {
            "register-client" when Option("client-id") is { } clientId => async admin =>
            {
                var client = await admin.RegisterAsync(
                    actor,
                    new ClientRegistrationRequest(
                        clientId,
                        Option("name"),
                        OAuthCodes.ParseAuthMethod(Option("auth") ?? "none"),
                        List("grant-types") ?? [],
                        List("redirect-uris") ?? [],
                        [.. (List("resources") ?? []).Select(OAuthCodes.ParseResource)],
                        List("scopes") ?? [OAuthScopes.Read],
                        List("fields") ?? [],
                        List("mcp-tools") ?? [],
                        Option("rate-limit") is { } limit ? int.Parse(limit, CultureInfo.InvariantCulture) : null),
                    null,
                    ct);
                await WriteClientAsync(output, client);
            },
            "update-client" when Option("client-id") is { } clientId => async admin =>
            {
                var current = await admin.FindAsync(clientId, ct);
                var limit = Option("rate-limit");
                var client = await admin.UpdateAsync(
                    actor,
                    current.Id,
                    new ClientUpdateRequest(
                        Option("name"),
                        List("redirect-uris"),
                        List("resources") is { } resources ? [.. resources.Select(OAuthCodes.ParseResource)] : null,
                        List("scopes"),
                        List("fields"),
                        List("mcp-tools"),
                        limit is null or "none" ? null : int.Parse(limit, CultureInfo.InvariantCulture),
                        limit == "none"),
                    null,
                    ct);
                await WriteClientAsync(output, client);
            },
            "list-clients" => async admin =>
            {
                var clients = await admin.ListAsync(actor, Option("status") is { } status ? OAuthCodes.ParseStatus(status) : null, ct);
                await output.WriteLineAsync("id\tclient_id\tregistration\tstatus\tauth\tresources\tscopes\tname");
                foreach (var client in clients)
                {
                    await output.WriteLineAsync(string.Join(
                        '\t',
                        client.Id,
                        client.ClientId,
                        OAuthCodes.ToCode(client.Registration),
                        OAuthCodes.ToCode(client.Status),
                        OAuthCodes.ToCode(client.AuthMethod),
                        string.Join(',', client.Resources.Select(OAuthCodes.ToCode)),
                        string.Join(',', client.Scopes),
                        client.DisplayName ?? "-"));
                }
            },
            "suspend-client" or "activate-client" or "approve-client" or "reject-client" when Option("client-id") is { } clientId => async admin =>
            {
                var current = await admin.FindAsync(clientId, ct);
                var status = command switch
                {
                    "suspend-client" => OAuthClientStatus.Suspended,
                    "reject-client" => OAuthClientStatus.Rejected,
                    _ => OAuthClientStatus.Active,
                };
                var client = await admin.SetStatusAsync(actor, current.Id, status, null, ct);
                await output.WriteLineAsync($"status: {OAuthCodes.ToCode(client.Status)}");
            },
            "add-client-key" when Option("client-id") is { } clientId && Option("kid") is { } kid && Option("key-file") is { } file => async admin =>
            {
                var current = await admin.FindAsync(clientId, ct);
                await admin.AddKeyAsync(actor, current.Id, kid, await File.ReadAllTextAsync(file, ct), null, ct);
                await output.WriteLineAsync($"key_added: {kid}");
            },
            "revoke-client-key" when Option("client-id") is { } clientId && Option("kid") is { } kid => async admin =>
            {
                var current = await admin.FindAsync(clientId, ct);
                await admin.RevokeKeyAsync(actor, current.Id, kid, null, ct);
                await output.WriteLineAsync($"key_revoked: {kid}");
            },
            "issue-client-secret" when Option("client-id") is { } clientId => async admin =>
            {
                var current = await admin.FindAsync(clientId, ct);
                TimeSpan? lifetime = Option("lifetime") is { } value ? TimeSpan.Parse(value, CultureInfo.InvariantCulture) : null;
                var secret = await admin.IssueSecretAsync(actor, current.Id, lifetime, null, ct);
                await output.WriteLineAsync($"secret_id: {secret.Id}");
                await output.WriteLineAsync($"expires_at: {secret.ExpiresAt.ToString("O", CultureInfo.InvariantCulture)}");
                await output.WriteLineAsync($"client_secret: {secret.Secret}");
            },
            "revoke-client-secrets" when Option("client-id") is { } clientId => async admin =>
            {
                var current = await admin.FindAsync(clientId, ct);
                await output.WriteLineAsync($"revoked: {await admin.RevokeSecretsAsync(actor, current.Id, null, ct)}");
            },
            "allow-cimd-domain" when Option("domain") is { } domain => async admin =>
            {
                await admin.AddDomainAsync(actor, domain, null, ct);
                await output.WriteLineAsync($"allowed: {domain}");
            },
            "disallow-cimd-domain" when Option("domain") is { } domain => async admin =>
            {
                await admin.RemoveDomainAsync(actor, domain, null, ct);
                await output.WriteLineAsync($"disallowed: {domain}");
            },
            "list-cimd-domains" => async admin =>
            {
                var (configured, added) = await admin.ListDomainsAsync(actor, ct);
                foreach (var domain in configured)
                {
                    await output.WriteLineAsync($"{domain}\tconfigured");
                }

                foreach (var domain in added)
                {
                    await output.WriteLineAsync($"{domain.Domain}\tadded");
                }
            },
            "revoke-tokens" when Option("client-id") is not null || Id("person") is not null || Id("family") is not null => async admin =>
            {
                Guid? client = Option("client-id") is { } clientId ? (await admin.FindAsync(clientId, ct)).Id : null;
                await output.WriteLineAsync($"revoked: {await admin.RevokeTokensAsync(actor, client, Id("person"), Id("family"), null, ct)}");
            },
            "propose-disclosure-lead" when Option("client-id") is { } clientId && Option("days") is { } days && Id("proposed-by") is { } proposer => async admin =>
            {
                var current = await admin.FindAsync(clientId, ct);
                var proposal = await leads!.ProposeAsync(
                    actor, current.Id, int.Parse(days, NumberStyles.None, CultureInfo.InvariantCulture), Option("note"), proposer, null, ct);
                await output.WriteLineAsync($"proposal_id: {proposal.Id}");
                await output.WriteLineAsync($"client: {current.ClientId}");
                await output.WriteLineAsync($"lead_days: {proposal.LeadDays} (current {current.DisclosureLeadDays})");
                await output.WriteLineAsync("status: proposed（UI でログインした別の人事管理者の承認で有効になります）");
            },
            "list-disclosure-leads" => async admin =>
            {
                Guid? client = Option("client-id") is { } clientId ? (await admin.FindAsync(clientId, ct)).Id : null;
                await output.WriteLineAsync("id\tclient_ref\tlead_days\tstatus\tproposed_by\tproposed_at\tdecided_by\tdecided_at");
                foreach (var lead in await leads!.ListAsync(actor, client, ct))
                {
                    await output.WriteLineAsync(string.Join(
                        '\t',
                        lead.Id,
                        lead.ClientRef,
                        lead.LeadDays,
                        System.Text.Json.JsonNamingPolicy.SnakeCaseLower.ConvertName(lead.Status.ToString()),
                        lead.ProposedBy,
                        lead.ProposedAt.ToString("O", CultureInfo.InvariantCulture),
                        lead.DecidedBy?.ToString() ?? "-",
                        lead.DecidedAt?.ToString("O", CultureInfo.InvariantCulture) ?? "-"));
                }
            },
            _ => null,
        };

        if (action is null)
        {
            await Console.Error.WriteLineAsync("使い方:");
            await Console.Error.WriteLineAsync(Usage);
            return 2;
        }

        AuthorizationServerOptions server;
        LoginOptions login;
        try
        {
            server = OAuthRegistration.Server(configuration);
            login = AuthRegistration.Login(configuration);
        }
        catch (ArgumentException ex)
        {
            await Console.Error.WriteLineAsync(ex.Message);
            return 2;
        }

        builder.Services.AddJinJiSettings(configuration);
        builder.Services.AddJinJiDatabase(configuration);
        builder.Services.AddJinJiAuthStore(login);
        builder.Services.AddJinJiOAuthAdministration(server);
        using var host = builder.Build();
        var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("JinJi.OAuth");
        try
        {
            leads = host.Services.GetRequiredService<DisclosureLeadService>();
            await action(host.Services.GetRequiredService<OAuthClientAdministrationService>());
            return 0;
        }
        catch (Exception ex) when (ex is AuthAdminException or ArgumentException or FormatException or OverflowException or IOException)
        {
            await Console.Error.WriteLineAsync(ex.Message);
            return 1;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LogFailed(logger, command, ex);
            return 1;
        }
    }

    private static async Task WriteClientAsync(TextWriter output, OAuthClient client)
    {
        await output.WriteLineAsync($"id: {client.Id}");
        await output.WriteLineAsync($"client_id: {client.ClientId}");
        await output.WriteLineAsync($"status: {OAuthCodes.ToCode(client.Status)}");
        await output.WriteLineAsync($"auth_method: {OAuthCodes.ToCode(client.AuthMethod)}");
        await output.WriteLineAsync($"grant_types: {string.Join(',', client.GrantTypes)}");
        await output.WriteLineAsync($"resources: {string.Join(',', client.Resources.Select(OAuthCodes.ToCode))}");
        await output.WriteLineAsync($"scopes: {string.Join(',', client.Scopes)}");
        await output.WriteLineAsync($"fields: {string.Join(',', client.Fields)}");
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "{Command} に失敗しました")]
    private static partial void LogFailed(ILogger logger, string command, Exception exception);
}
