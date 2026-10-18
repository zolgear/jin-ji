using JinJi.Application.Retention;
using JinJi.Policy;
using Npgsql;

namespace JinJi.Infrastructure.Database;

/// <summary>
/// 保持期限の消去（0014）。閲覧者は jin-ji 自身（system、ロールなし）で、RLS が「終わった行だけ」に絞る（DB の下限）。
/// 条件には DB の下限も重ねて書く（下限で消せない行を選ぶと、上限の行数に満たずに繰り返しが止まり、その先の行が残るため）。
/// </summary>
public sealed class NpgsqlRetentionStore : IRetentionStore
{
    /// <summary>系列が保持期間を過ぎた（$2 = 系列の保持期間の境目）。</summary>
    private const string FamilyPast = "(f.ended_at < $2 OR f.expires_at < $2)";

    private readonly NpgsqlDataSource _dataSource;

    public NpgsqlRetentionStore(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _dataSource = dataSource;
    }

    public async Task<int> PurgeAsync(RetentionTarget target, RetentionCutoffs cutoffs, int batchSize, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cutoffs);
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);
        var (table, where, first, second) = Statement(target, cutoffs);
        var sql =
            $"""
            DELETE FROM {table}
            WHERE ctid = ANY (ARRAY (SELECT t.ctid FROM {table} t WHERE {where} LIMIT $3))
            """;

        var total = 0;
        while (true)
        {
            await using var tx = await ViewerTransaction.BeginRawAsync(_dataSource, "system", string.Empty, null, cancellationToken);
            await using var command = tx.CreateCommand(sql);
            command.Parameters.Add(new NpgsqlParameter { Value = first });
            command.Parameters.Add(new NpgsqlParameter { Value = second });
            command.Parameters.Add(new NpgsqlParameter { Value = batchSize });
            var deleted = await command.ExecuteNonQueryAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
            total += deleted;
            if (deleted < batchSize)
            {
                return total;
            }
        }
    }

    public async Task RecordRunAsync(RetentionResult result, Channel trigger, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(result);
        int Count(RetentionTarget target) => result.Deleted.TryGetValue(target, out var count) ? count : 0;
        await using var tx = await ViewerTransaction.BeginRawAsync(_dataSource, "system", string.Empty, null, cancellationToken);
        await using var command = tx.CreateCommand(
            """
            INSERT INTO retention_run (id, trigger, outcome, started_at, finished_at, ui_session, unbound_login, account_reset_link,
                                       oauth_authorization_request, oauth_authorization_code, oauth_access_token, oauth_refresh_token,
                                       oauth_token_family, oauth_consent, oauth_client_assertion, pending_client, staging_row)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14, $15, $16, $17)
            """);
        object[] values =
        [
            result.RunId, trigger == Channel.Cli ? "cli" : "worker", result.Succeeded ? "success" : "failed", result.StartedAt, result.FinishedAt,
            Count(RetentionTarget.UiSession), Count(RetentionTarget.UnboundLogin), Count(RetentionTarget.ResetLink),
            Count(RetentionTarget.AuthorizationRequest), Count(RetentionTarget.AuthorizationCode), Count(RetentionTarget.AccessToken),
            Count(RetentionTarget.RefreshToken), Count(RetentionTarget.TokenFamily), Count(RetentionTarget.Consent),
            Count(RetentionTarget.ClientAssertion), Count(RetentionTarget.PendingClient), Count(RetentionTarget.StagingRow),
        ];
        foreach (var value in values)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = value });
        }

        await command.ExecuteNonQueryAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
    }

    /// <summary>対象の表と条件（$1・$2 は境目の時刻、$3 は上限の行数）。</summary>
    private static (string Table, string Where, DateTimeOffset First, DateTimeOffset Second) Statement(RetentionTarget target, RetentionCutoffs c) => target switch
    {
        // 期限から保持期間を過ぎたトークン。系列ごと消す系列のトークンも（系列を消す前に）
        RetentionTarget.AccessToken => ("oauth_access_token",
            $"t.expires_at <= now() AND (t.expires_at < $1 OR EXISTS (SELECT 1 FROM oauth_token_family f WHERE f.id = t.family_id AND {FamilyPast}))",
            c.OAuthTokens, c.TokenFamilies),
        RetentionTarget.RefreshToken => ("oauth_refresh_token",
            $"t.expires_at < $1 OR EXISTS (SELECT 1 FROM oauth_token_family f WHERE f.id = t.family_id AND {FamilyPast})",
            c.OAuthTokens, c.TokenFamilies),

        // 認可コード: 系列を発行していないか、その系列ごと消すもの（生きている系列のコードは、再利用の検知のため残す）
        RetentionTarget.AuthorizationCode => ("oauth_authorization_code",
            $"t.expires_at < $1 AND (t.family_id IS NULL OR EXISTS (SELECT 1 FROM oauth_token_family f WHERE f.id = t.family_id AND {FamilyPast}))",
            c.OAuthTokens, c.TokenFamilies),

        // 系列: 参照するトークン・コードが残っていないもの
        RetentionTarget.TokenFamily => ("oauth_token_family",
            """
            (t.ended_at < $2 OR t.expires_at < $2)
            AND NOT EXISTS (SELECT 1 FROM oauth_access_token a WHERE a.family_id = t.id)
            AND NOT EXISTS (SELECT 1 FROM oauth_refresh_token r WHERE r.family_id = t.id)
            AND NOT EXISTS (SELECT 1 FROM oauth_authorization_code k WHERE k.family_id = t.id)
            """,
            c.OAuthTokens, c.TokenFamilies),

        // 同意: 要求の期限から保持期間を過ぎ、その要求のコードが残っていない（= 系列も消えた）もの
        RetentionTarget.Consent => ("oauth_consent",
            """
            EXISTS (SELECT 1 FROM oauth_authorization_request r WHERE r.id = t.request_id AND r.expires_at < $1 AND r.expires_at <= now())
            AND NOT EXISTS (SELECT 1 FROM oauth_authorization_code k WHERE k.request_id = t.request_id)
            """,
            c.OAuthTokens, c.TokenFamilies),
        RetentionTarget.AuthorizationRequest => ("oauth_authorization_request",
            """
            t.expires_at < $1 AND t.expires_at <= now()
            AND NOT EXISTS (SELECT 1 FROM oauth_authorization_code k WHERE k.request_id = t.id)
            AND NOT EXISTS (SELECT 1 FROM oauth_consent s WHERE s.request_id = t.id)
            """,
            c.OAuthTokens, c.TokenFamilies),

        // jti は assertion の期限まで要るだけ
        RetentionTarget.ClientAssertion => ("oauth_client_assertion", "t.expires_at < $1", c.Now, c.Now),

        // 承認されないままの CIMD のクライアント（却下したものは拒否のまま残す）
        RetentionTarget.PendingClient => ("api_client",
            """
            t.status = 'pending_approval' AND t.registration = 'cimd' AND t.created_at < $1 AND t.created_at <= now() - interval '1 day'
            AND NOT EXISTS (SELECT 1 FROM oauth_authorization_request r WHERE r.client_ref = t.id)
            """,
            c.PendingClients, c.PendingClients),

        RetentionTarget.UiSession => ("ui_session", "(t.ended_at < $1 OR t.expires_at < $1) AND (t.ended_at IS NOT NULL OR t.expires_at <= now())", c.UiSessions, c.UiSessions),
        RetentionTarget.UnboundLogin => ("unbound_login",
            """
            greatest(t.last_attempted_at, coalesce(t.resolved_at, t.last_attempted_at)) < $1
            AND greatest(t.last_attempted_at, coalesce(t.resolved_at, t.last_attempted_at)) <= now() - interval '1 day'
            """,
            c.UnboundLogins, c.UnboundLogins),
        RetentionTarget.ResetLink => ("account_reset_link", "t.expires_at < $1 AND t.expires_at <= now()", c.ResetLinks, c.ResetLinks),
        _ => throw new ArgumentOutOfRangeException(nameof(target), target, "この保存先では消さない対象です（staging は取込の保存先が消す）。"),
    };
}
