using System.Globalization;
using JinJi.Application.Auth;
using JinJi.Host.Auth;

namespace JinJi.Host;

/// <summary>
/// 認証の管理の CLI（ADR-0004 の段階 A）。接続文字列は <c>ConnectionStrings:App</c>。操作者は管理 CLI（DB の閲覧者は jin-ji 自身・ロールなし）で、すべて監査ログに残す。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><c>create-local-account --person &lt;id&gt; --user-name &lt;名前&gt;</c>: 人のローカルアカウントを作り、パスワードを設定するリンクを出す</item>
/// <item><c>create-break-glass-account --user-name &lt;名前&gt;</c>: 緊急用の管理者アカウントを作り、パスキーを登録するリンクを出す</item>
/// <item><c>issue-reset-link --user-name &lt;名前&gt; [--purpose password|passkey] [--lifetime hh:mm:ss]</c>: 1 回限りのリンクを出す</item>
/// <item><c>unlock-account --user-name &lt;名前&gt;</c>: ロックアウトを解除する</item>
/// <item><c>list-local-accounts [--query &lt;部分一致&gt;]</c>: ローカルアカウントの一覧</item>
/// <item><c>disable-account --user-name &lt;名前&gt;</c> / <c>enable-account ...</c>: ローカルアカウントの無効化（セッションの終了・委任の失効）・再有効化</item>
/// <item><c>bind-login --person &lt;id&gt; (--unbound &lt;id&gt; | --provider &lt;名前&gt; --subject &lt;主体&gt; [--issuer &lt;iss&gt;])</c>: 外部 IdP の主体を人に紐付ける（人事管理者の操作に相当）</item>
/// <item><c>list-unbound-logins [--all true]</c>: 紐付けできずに拒否したログインの一覧</item>
/// <item><c>grant-role --person &lt;id&gt; --role hr_admin</c> / <c>revoke-role ...</c>: ロールの付与・取り消し</item>
/// </list>
/// リンクの鍵は出力にだけ現れる（DB にはハッシュだけ）。利用者に安全な経路で渡すこと。
/// </remarks>
internal static partial class AuthCommands
{
    public const string Usage = """
          JinJi.Host create-local-account --person <person-id> --user-name <名前>
          JinJi.Host create-break-glass-account --user-name <名前>
          JinJi.Host issue-reset-link --user-name <名前> [--purpose password|passkey] [--lifetime hh:mm:ss]
          JinJi.Host unlock-account --user-name <名前>
          JinJi.Host list-local-accounts [--query <ユーザー名の部分一致>]
          JinJi.Host disable-account --user-name <名前>
          JinJi.Host enable-account --user-name <名前>
          JinJi.Host bind-login --person <person-id> (--unbound <unbound-login-id> | --provider <名前> --subject <主体の識別子> [--issuer <iss>])
          JinJi.Host list-unbound-logins [--all true]
          JinJi.Host grant-role --person <person-id> --role hr_admin
          JinJi.Host revoke-role --person <person-id> --role hr_admin
                                                    認証の管理（ローカルアカウント・緊急用アカウント・リンク・紐付け・ロール）
        """;

    public static readonly IReadOnlySet<string> Commands = new HashSet<string>(StringComparer.Ordinal)
    {
        "create-local-account", "create-break-glass-account", "issue-reset-link", "unlock-account", "bind-login", "list-unbound-logins", "grant-role", "revoke-role",
        "list-local-accounts", "disable-account", "enable-account",
    };

    public static async Task<int> RunAsync(string command, string[] args, TextWriter? output = null)
    {
        output ??= Console.Out;
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(args);
        var options = builder.Configuration;
        string? Option(string name) => options[name] is { Length: > 0 } value ? value : null;
        Guid? Id(string name) => Guid.TryParseExact(Option(name), "D", out var id) ? id : null;

        Func<AuthAdministrationService, Task>? action = command switch
        {
            "create-local-account" when Id("person") is { } person && Option("user-name") is { } name => async admin =>
            {
                var (account, link) = await admin.CreatePersonAccountAsync(person, name, CancellationToken.None);
                await output.WriteLineAsync($"account_id: {account.Id}");
                await WriteLinkAsync(output, link);
            },
            "create-break-glass-account" when Option("user-name") is { } name => async admin =>
            {
                var (account, link) = await admin.CreateBreakGlassAccountAsync(name, CancellationToken.None);
                await output.WriteLineAsync($"account_id: {account.Id}");
                await WriteLinkAsync(output, link);
            },
            "issue-reset-link" when Option("user-name") is { } name && Lifetime(Option("lifetime"), out var lifetime) => async admin =>
            {
                var account = await admin.FindAccountByNameAsync(name, CancellationToken.None);
                var purpose = Option("purpose") is { } code
                    ? AuthAdministrationService.ParsePurpose(code)
                    : account.IsBreakGlass ? ResetLinkPurpose.Passkey : ResetLinkPurpose.Password;
                await WriteLinkAsync(output, await admin.IssueResetLinkAsync(AuthActor.Cli, account.Id, purpose, lifetime, null, CancellationToken.None));
            },
            "unlock-account" when Option("user-name") is { } name => async admin =>
            {
                var account = await admin.FindAccountByNameAsync(name, CancellationToken.None);
                await admin.UnlockAsync(AuthActor.Cli, account.Id, null, CancellationToken.None);
                await output.WriteLineAsync($"unlocked: {account.Id}");
            },
            "list-local-accounts" => async admin =>
            {
                var rows = await admin.ListAccountsAsync(AuthActor.Cli, Option("query"), 500, CancellationToken.None);
                await output.WriteLineAsync("id\tkind\tuser_name\tperson_id\tpassword\ttwo_factor\tpasskeys\tlocked_until\tdisabled_at");
                foreach (var row in rows)
                {
                    await output.WriteLineAsync(string.Join(
                        '\t',
                        row.Id,
                        row.Kind == LocalAccountKind.BreakGlass ? "break_glass" : "person",
                        row.UserName,
                        row.PersonId?.ToString() ?? "-",
                        row.HasPassword ? "true" : "false",
                        row.TwoFactorEnabled ? "true" : "false",
                        row.PasskeyCount.ToString(CultureInfo.InvariantCulture),
                        row.LockoutEnd?.ToString("O", CultureInfo.InvariantCulture) ?? "-",
                        row.DisabledAt?.ToString("O", CultureInfo.InvariantCulture) ?? "-"));
                }
            },
            "disable-account" when Option("user-name") is { } name => async admin =>
            {
                var account = await admin.FindAccountByNameAsync(name, CancellationToken.None);
                var (sessions, families) = await admin.DisableAccountAsync(AuthActor.Cli, account.Id, null, CancellationToken.None);
                await output.WriteLineAsync($"disabled: {account.Id}");
                await output.WriteLineAsync($"sessions_ended: {sessions}");
                await output.WriteLineAsync($"delegations_revoked: {families}");
            },
            "enable-account" when Option("user-name") is { } name => async admin =>
            {
                var account = await admin.FindAccountByNameAsync(name, CancellationToken.None);
                await admin.EnableAccountAsync(AuthActor.Cli, account.Id, null, CancellationToken.None);
                await output.WriteLineAsync($"enabled: {account.Id}");
            },
            "bind-login" when Id("person") is { } person && (Id("unbound") is not null || (Option("provider") is not null && Option("subject") is not null)) => async admin =>
            {
                var binding = await admin.CreateBindingAsync(
                    AuthActor.Cli,
                    new BindingRequest(person, Id("unbound"), Option("provider"), Option("subject"), Option("issuer")),
                    null,
                    CancellationToken.None);
                await output.WriteLineAsync($"binding_id: {binding.Id}");
                await output.WriteLineAsync($"provider: {binding.Provider}");
                await output.WriteLineAsync($"issuer: {binding.Issuer}");
                await output.WriteLineAsync($"subject: {binding.Subject}");
                await output.WriteLineAsync($"person_id: {binding.PersonId}");
            },
            "list-unbound-logins" => async admin =>
            {
                var rows = await admin.ListUnboundLoginsAsync(AuthActor.Cli, Option("all") == "true", 200, CancellationToken.None);
                await output.WriteLineAsync("id\tprovider\tsubject\temployee_number\temail\tattempts\tlast_attempted_at\tresolved\tcandidates");
                foreach (var row in rows)
                {
                    await output.WriteLineAsync(string.Join(
                        '\t',
                        row.Id,
                        row.Provider,
                        row.Subject,
                        row.EmployeeNumber ?? "-",
                        row.Email ?? "-",
                        row.AttemptCount.ToString(CultureInfo.InvariantCulture),
                        row.LastAttemptedAt.ToString("O", CultureInfo.InvariantCulture),
                        row.ResolvedAt is null ? "false" : "true",
                        row.CandidatePersonIds.Count == 0 ? "-" : string.Join(',', row.CandidatePersonIds)));
                }
            },
            "grant-role" when Id("person") is { } person && Option("role") is { } role => async admin =>
            {
                await admin.GrantRoleAsync(person, role, CancellationToken.None);
                await output.WriteLineAsync($"granted: {role} {person}");
            },
            "revoke-role" when Id("person") is { } person && Option("role") is { } role => async admin =>
            {
                await admin.RevokeRoleAsync(person, role, CancellationToken.None);
                await output.WriteLineAsync($"revoked: {role} {person}");
            },
            _ => null,
        };

        if (action is null)
        {
            await Console.Error.WriteLineAsync("使い方:");
            await Console.Error.WriteLineAsync(Usage);
            return 2;
        }

        LoginOptions login;
        try
        {
            login = AuthRegistration.Login(builder.Configuration);
        }
        catch (ArgumentException ex)
        {
            await Console.Error.WriteLineAsync(ex.Message);
            return 2;
        }

        builder.Services.AddJinJiSettings(builder.Configuration);
        builder.Services.AddJinJiDatabase(builder.Configuration);
        builder.Services.AddJinJiAuthStore(login);
        using var host = builder.Build();
        var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("JinJi.Auth");
        try
        {
            await action(host.Services.GetRequiredService<AuthAdministrationService>());
            return 0;
        }
        catch (AuthAdminException ex)
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

    private static async Task WriteLinkAsync(TextWriter output, IssuedResetLink link)
    {
        await output.WriteLineAsync($"link_id: {link.LinkId}");
        await output.WriteLineAsync($"purpose: {AuthAdministrationService.Code(link.Purpose)}");
        await output.WriteLineAsync($"expires_at: {link.ExpiresAt.ToString("O", CultureInfo.InvariantCulture)}");
        await output.WriteLineAsync(link.Url is { } url ? $"url: {url}" : $"token: {link.Token}");
    }

    private static bool Lifetime(string? value, out TimeSpan? lifetime)
    {
        lifetime = null;
        if (value is null)
        {
            return true;
        }

        if (TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var parsed))
        {
            lifetime = parsed;
            return true;
        }

        return false;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "{Command} に失敗しました")]
    private static partial void LogFailed(ILogger logger, string command, Exception exception);
}
