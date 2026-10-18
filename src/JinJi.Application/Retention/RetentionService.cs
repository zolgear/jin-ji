using System.Diagnostics.Metrics;
using JinJi.Application.Audit;
using JinJi.Application.Ingest;
using JinJi.Domain;
using JinJi.Policy;

namespace JinJi.Application.Retention;

/// <summary>消去の対象（retention_run の列名と同じ名前をメトリクス・CLI の出力に使う）。</summary>
public enum RetentionTarget
{
    AccessToken,
    RefreshToken,
    AuthorizationCode,
    TokenFamily,
    Consent,
    AuthorizationRequest,
    ClientAssertion,
    PendingClient,
    UiSession,
    UnboundLogin,
    ResetLink,
    StagingRow,
}

/// <summary>対象ごとの「これより前に終わった行を消す」時刻。</summary>
public sealed record RetentionCutoffs(
    DateTimeOffset Now,
    DateTimeOffset UiSessions,
    DateTimeOffset UnboundLogins,
    DateTimeOffset ResetLinks,
    DateTimeOffset OAuthTokens,
    DateTimeOffset TokenFamilies,
    DateTimeOffset PendingClients)
{
    public static RetentionCutoffs From(RetentionOptions options, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new RetentionCutoffs(
            now,
            now - options.UiSessions,
            now - options.UnboundLogins,
            now - options.ResetLinks,
            now - options.OAuthTokens,
            now - options.TokenFamilies,
            now - options.PendingClients);
    }
}

/// <summary>1 回の消去の結果。</summary>
public sealed record RetentionResult(Guid RunId, DateTimeOffset StartedAt, DateTimeOffset FinishedAt, bool Succeeded, IReadOnlyDictionary<RetentionTarget, int> Deleted)
{
    public int Total => Deleted.Values.Sum();
}

/// <summary>
/// 消去の保存先（Infrastructure が実装する）。認証・トークン発行の表は jin-ji 自身（system、ロールなし）の閲覧者で消し、
/// RLS（0014）が「終わった行だけ」に絞る。
/// </summary>
public interface IRetentionStore
{
    /// <summary>対象の表から、保持期間を過ぎた行を消す（上限の行数ずつ別のトランザクションで、残りが無くなるまで）。消した行数。</summary>
    Task<int> PurgeAsync(RetentionTarget target, RetentionCutoffs cutoffs, int batchSize, CancellationToken cancellationToken);

    /// <summary>実行の記録（retention_run）を追記する。</summary>
    Task RecordRunAsync(RetentionResult result, Channel trigger, CancellationToken cancellationToken);
}

/// <summary>消去のメトリクス（Meter <c>JinJi.Retention</c>。API の役割と同じプロセスならメトリクスのポートに出る）。</summary>
public sealed class RetentionMetrics
{
    public const string MeterName = "JinJi.Retention";

    private readonly Counter<long> _deleted;
    private readonly Counter<long> _runs;

    public RetentionMetrics(IMeterFactory meterFactory)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);
        Meter = meterFactory.Create(MeterName);
        _deleted = Meter.CreateCounter<long>("jinji.retention.deleted", "{row}", "保持期限の消去で消した行数（表別）");
        _runs = Meter.CreateCounter<long>("jinji.retention.runs", "{run}", "保持期限の消去の実行回数（契機・結果別）");
    }

    public Meter Meter { get; }

    public void Record(RetentionResult result, Channel trigger)
    {
        ArgumentNullException.ThrowIfNull(result);
        foreach (var (target, count) in result.Deleted)
        {
            if (count > 0)
            {
                _deleted.Add(count, new KeyValuePair<string, object?>("table", RetentionService.TableName(target)));
            }
        }

        _runs.Add(
            1,
            new KeyValuePair<string, object?>("trigger", trigger == Channel.Cli ? "cli" : "worker"),
            new KeyValuePair<string, object?>("outcome", result.Succeeded ? "success" : "failed"));
    }
}

/// <summary>
/// 保持期限の消去（アーキテクチャ概要 §4.5）。ワーカーの定期実行と管理 CLI の <c>purge-expired</c> から呼ぶ。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>消すのは、終わった認証の行（UI のセッション・未紐付けのログイン・リンク）、トークン発行の行（認可の要求・コード・トークン・系列・同意・jti）、
/// 承認されないままの CIMD のクライアント、取込の staging（行ごとの保持期限。正規化結果・名寄せ候補は連鎖して消える）</item>
/// <item>消さないもの: 監査ログ（J-SOX の IT 全般統制の証跡。§4.6）、人事データ（退職者の匿名化は別の手順。§4.5）、紐付け・ロールの付与・ローカルアカウント・
/// クライアントの登録・鍵・シークレット（取り消しの記録そのものが証跡）、Data Protection の鍵（古いデータの復号に要る）</item>
/// <item>参照の順（トークン → コード → 系列 → 同意 → 要求）に消す。外部キーの連鎖は RLS を受けないので使わない</item>
/// <item>表ごとの件数を retention_run に残し、監査ログ（retention.purge。対象は retention_run）とメトリクスに出す。途中で失敗しても、それまでの件数を残す</item>
/// </list>
/// </remarks>
public sealed class RetentionService
{
    /// <summary>監査ログの操作。</summary>
    public const string AuditAction = "retention.purge";

    /// <summary>監査ログの対象の種類。</summary>
    public const string AuditTarget = "retention_run";

    /// <summary>消す順（参照する側から先に）。</summary>
    private static readonly RetentionTarget[] Order =
    [
        RetentionTarget.AccessToken, RetentionTarget.RefreshToken, RetentionTarget.AuthorizationCode, RetentionTarget.TokenFamily,
        RetentionTarget.Consent, RetentionTarget.AuthorizationRequest, RetentionTarget.ClientAssertion, RetentionTarget.PendingClient,
        RetentionTarget.UiSession, RetentionTarget.UnboundLogin, RetentionTarget.ResetLink,
    ];

    private readonly IRetentionStore _store;
    private readonly IIngestStore _ingest;
    private readonly AuditWriter _audit;
    private readonly RetentionOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly RetentionMetrics? _metrics;

    public RetentionService(
        IRetentionStore store,
        IIngestStore ingest,
        AuditWriter audit,
        RetentionOptions options,
        TimeProvider timeProvider,
        RetentionMetrics? metrics = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(ingest);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _store = store;
        _ingest = ingest;
        _audit = audit;
        _options = options;
        _timeProvider = timeProvider;
        _metrics = metrics;
    }

    /// <summary>1 回消す。<paramref name="trigger"/> はワーカー（定期実行）か管理 CLI。</summary>
    public async Task<RetentionResult> RunAsync(Channel trigger, CancellationToken cancellationToken)
    {
        if (trigger is not (Channel.Worker or Channel.Cli))
        {
            throw new ArgumentException("消去はワーカーか管理 CLI から実行します。", nameof(trigger));
        }

        var runId = EntityId.New();
        var startedAt = _timeProvider.GetUtcNow();
        var cutoffs = RetentionCutoffs.From(_options, startedAt);
        var deleted = new Dictionary<RetentionTarget, int>();
        Exception? failure = null;
        try
        {
            foreach (var target in Order)
            {
                deleted[target] = await _store.PurgeAsync(target, cutoffs, _options.BatchSize, cancellationToken);
            }

            // staging は行ごとの保持期限（項目マッピングの stagingRetentionDays）。0005 の RLS で hr_admin を付けた jin-ji 自身が消す
            deleted[RetentionTarget.StagingRow] = await _ingest.PurgeExpiredStagingAsync(ViewerContext.ForSystem(trigger), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            failure = ex;
        }

        var result = new RetentionResult(runId, startedAt, _timeProvider.GetUtcNow(), failure is null, deleted);

        // 記録は取り消しの要求に関わらず残す（消した事実を失わない）
        await _store.RecordRunAsync(result, trigger, CancellationToken.None);
        await _audit.RecordAsync(
            ViewerContext.ForSystem(trigger),
            AuditAction,
            failure is null ? AuditOutcome.Success : AuditOutcome.Failed,
            [],
            CancellationToken.None,
            AuditTarget,
            runId);
        _metrics?.Record(result, trigger);
        if (failure is not null)
        {
            throw new RetentionException(result, failure);
        }

        return result;
    }

    /// <summary>対象の名前（retention_run の列名・メトリクスのラベル・CLI の出力）。</summary>
    public static string TableName(RetentionTarget target) => target switch
    {
        RetentionTarget.AccessToken => "oauth_access_token",
        RetentionTarget.RefreshToken => "oauth_refresh_token",
        RetentionTarget.AuthorizationCode => "oauth_authorization_code",
        RetentionTarget.TokenFamily => "oauth_token_family",
        RetentionTarget.Consent => "oauth_consent",
        RetentionTarget.AuthorizationRequest => "oauth_authorization_request",
        RetentionTarget.ClientAssertion => "oauth_client_assertion",
        RetentionTarget.PendingClient => "pending_client",
        RetentionTarget.UiSession => "ui_session",
        RetentionTarget.UnboundLogin => "unbound_login",
        RetentionTarget.ResetLink => "account_reset_link",
        RetentionTarget.StagingRow => "staging_row",
        _ => throw new ArgumentOutOfRangeException(nameof(target)),
    };
}

/// <summary>消去が途中で失敗した（それまでの件数は記録済み）。</summary>
public sealed class RetentionException : Exception
{
    public RetentionException()
    {
    }

    public RetentionException(string message)
        : base(message)
    {
    }

    public RetentionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public RetentionException(RetentionResult result, Exception innerException)
        : base("保持期限の消去が途中で失敗しました（それまでの件数は retention_run に記録しました）。", innerException)
    {
        Result = result;
    }

    public RetentionResult? Result { get; }
}
