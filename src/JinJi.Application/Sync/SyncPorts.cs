using JinJi.Policy;

namespace JinJi.Application.Sync;

/// <summary>
/// 同期計画の保存先（Infrastructure が実装する）。権限の最終的な線は DB が引く（0009: 参照・追加は hr_admin だけ、起案者 ≠ 承認者、
/// 状態遷移と中身の不変、changes のキーの許可リスト）。
/// </summary>
public interface ISyncPlanStore
{
    /// <summary>外部 ID の対応（同期先の名前の行）。</summary>
    Task<IReadOnlyList<ExternalIdentityLink>> LoadIdentitiesAsync(ViewerContext viewer, string sourceSystem, CancellationToken cancellationToken);

    /// <summary>
    /// 計画と項目を保存する（1 トランザクション）。同じ同期先の起案中・承認済みの計画は置き換え済み（superseded）にする。置き換えた計画の ID を返す。
    /// </summary>
    Task<IReadOnlyList<Guid>> InsertPlanAsync(ViewerContext viewer, SyncPlanRecord plan, IReadOnlyList<SyncPlanItem> items, CancellationToken cancellationToken);

    /// <summary>計画の一覧（作成の新しい順）。</summary>
    Task<IReadOnlyList<SyncPlanRecord>> ListPlansAsync(ViewerContext viewer, int limit, CancellationToken cancellationToken);

    Task<SyncPlanRecord?> FindPlanAsync(ViewerContext viewer, Guid planId, CancellationToken cancellationToken);

    Task<IReadOnlyList<SyncPlanItem>> ListItemsAsync(ViewerContext viewer, Guid planId, CancellationToken cancellationToken);

    /// <summary>起案中の計画を承認・却下する（決定者は閲覧者の人）。起案中でなければ false。</summary>
    Task<bool> DecideAsync(ViewerContext viewer, Guid planId, bool approve, CancellationToken cancellationToken);

    /// <summary>起案中・承認済みの計画を古い計画（stale）にする。対象の状態でなければ false。</summary>
    Task<bool> MarkStaleAsync(ViewerContext viewer, Guid planId, CancellationToken cancellationToken);

    /// <summary>実行の記録と項目ごとの結果を保存する（本番の実行で計画が承認済みなら実行済みにする。1 トランザクション）。</summary>
    Task InsertRunAsync(ViewerContext viewer, SyncRunRecord run, CancellationToken cancellationToken);

    /// <summary>計画の実行の記録（記録の古い順）。</summary>
    Task<IReadOnlyList<SyncRunRecord>> ListRunsAsync(ViewerContext viewer, Guid planId, CancellationToken cancellationToken);
}

/// <summary>差分計算（JinJi.Sync が実装する）。</summary>
public interface ISyncPlanner
{
    /// <summary>jin-ji の公開データと Entra のユーザー一覧から計画の項目を作る。</summary>
    SyncPlanDraft Plan(SyncPlanInput input);

    /// <summary>jin-ji 側のあるべき値の SHA-256（<see cref="SyncPlanDraft.JinjiFingerprint"/> と同じ計算。Entra のユーザー一覧は使わない）。</summary>
    string Fingerprint(Domain.Organization.OrgSnapshot snapshot, IReadOnlyList<ExternalIdentityLink> identities, SyncSettings settings, DateOnly today);
}

/// <summary>スクリプト一式の生成（JinJi.Sync が実装する）。</summary>
public interface ISyncScriptRenderer
{
    /// <summary>承認済みの計画から、PowerShell のスクリプト・データ（JSON）・差分レポート（Markdown・CSV）を作る。</summary>
    SyncScriptBundle Render(SyncPlanRecord plan, IReadOnlyList<SyncPlanItem> items);

    /// <summary>スクリプトが読むデータ（JSON）の SHA-256。実行ログの照合に使う（生成は決定的）。</summary>
    string DataSha256(SyncPlanRecord plan, IReadOnlyList<SyncPlanItem> items);
}

/// <summary>スクリプトの実行ログの解釈（JinJi.Sync が実装する）。</summary>
public interface ISyncLogParser
{
    /// <summary>実行ログ（JSON）を読む。形が不正なら <see cref="SyncLogFormatException"/>。</summary>
    SyncExecutionLog Parse(ReadOnlySpan<byte> content);
}
