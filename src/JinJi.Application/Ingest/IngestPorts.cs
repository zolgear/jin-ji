using JinJi.Policy;

namespace JinJi.Application.Ingest;

/// <summary>
/// 取り込みの作業領域（import_batch / staging_row / normalized_row / match_candidate / change_proposal / correction_request）の読み書き
/// （Infrastructure が実装する）。
/// </summary>
/// <remarks>
/// 権限の最終的な線は DB が引く: 作業領域の参照・追加は hr_admin かバッチの実行者本人（RLS）、jsonb のキーの許可リスト（CHECK 制約）、
/// 承認者 ≠ 起案者（= 取込の実行者。CHECK 制約）、提案の状態遷移と中身の不変（トリガー）。
/// </remarks>
public interface IIngestStore
{
    /// <summary>取込単位と作業領域の行を 1 トランザクションで保存する。</summary>
    Task SaveImportAsync(ViewerContext viewer, ImportWorkspace workspace, CancellationToken cancellationToken);

    Task<ImportBatchRecord?> FindBatchAsync(ViewerContext viewer, Guid batchId, CancellationToken cancellationToken);

    Task<ChangeProposalRecord?> FindProposalAsync(ViewerContext viewer, Guid proposalId, CancellationToken cancellationToken);

    Task<IReadOnlyList<ChangeProposalRecord>> ListProposalsAsync(ViewerContext viewer, Guid batchId, CancellationToken cancellationToken);

    Task<IReadOnlyList<CorrectionRequestRecord>> ListCorrectionsAsync(ViewerContext viewer, Guid batchId, CancellationToken cancellationToken);

    /// <summary>
    /// 起案中の提案を承認し、公開側（人・人の版・組織・組織の版・所属・発令）へ反映する（1 トランザクション。承認者は閲覧者の人）。
    /// 起案中でなければ false。提案の作成後に対象のデータが変わっていたら <see cref="ProposalConflictException"/>。
    /// </summary>
    /// <param name="today">業務日付（訂正の対象の行を選ぶ基準日）。</param>
    Task<bool> ApplyProposalAsync(ViewerContext viewer, ChangeProposalRecord proposal, DateOnly today, CancellationToken cancellationToken);

    /// <summary>
    /// 起案中・要確認の提案を却下する（却下した人は閲覧者の人）。起案中・要確認でなければ false。
    /// <paramref name="scrubFields"/> があれば、同じトランザクションで提案の根拠の staging の行・正規化結果からその項目を消す（氏名の消去）。
    /// </summary>
    Task<bool> RejectProposalAsync(ViewerContext viewer, Guid proposalId, IReadOnlyCollection<string> scrubFields, CancellationToken cancellationToken);

    /// <summary>
    /// 要確認の提案を解決する（1 トランザクション。解決した人は閲覧者の人）: 提案を resolved にし、解決の結果の提案（起案中）と修正依頼を追加し、
    /// 根拠の staging の行・正規化結果から指定の項目を消す。要確認でなければ false。
    /// </summary>
    Task<bool> ResolveProposalAsync(ViewerContext viewer, ProposalResolutionWrite resolution, CancellationToken cancellationToken);

    /// <summary>保持期限を過ぎた staging の行を削除する（正規化結果・名寄せ候補も消える）。削除した行数を返す。</summary>
    Task<int> PurgeExpiredStagingAsync(ViewerContext viewer, CancellationToken cancellationToken);
}

/// <summary>要確認の提案の解決の書き込み（<see cref="IIngestStore.ResolveProposalAsync"/>）。</summary>
/// <param name="Derived">解決の結果の提案（起案中。<see cref="ChangeProposalRecord.ResolvedFromProposalId"/> と解決者を持つ）。</param>
/// <param name="Correction">修正依頼に回したときの修正依頼。</param>
/// <param name="ScrubFields">根拠の staging の行・正規化結果から消す項目（氏名など）。</param>
public sealed record ProposalResolutionWrite(
    Guid ProposalId,
    ProposalResolution Resolution,
    IReadOnlyList<ChangeProposalRecord> Derived,
    CorrectionRequestRecord? Correction,
    IReadOnlyCollection<string> ScrubFields);

/// <summary>
/// 作業領域から消す項目（ADR-0002）。名寄せで人に対応付けられなかった行の氏名は、戸籍名（旧姓使用者の戸籍姓）の可能性がある。
/// 人の確認（要確認の提案の解決・却下）に使い終わったら作業領域から消す。
/// </summary>
public static class WorkspaceScrub
{
    /// <summary>氏名の取込項目（staging / 正規化結果のキー）。</summary>
    public static readonly IReadOnlyList<string> NameFields =
    [
        "full_name", "family_name", "given_name", "full_name_kana", "family_name_kana", "given_name_kana",
        "family_name_latin", "given_name_latin", "display_name", "search_name", "search_name_kana",
    ];

    /// <summary>
    /// 提案を決めた（解決・却下した）後に作業領域から消す項目。名寄せの要確認（人が決まらなかった行）の氏名だけを消す。
    /// 補完提案（人は規則で決まっている）の行は、業務上の氏名なので消さない（保持期限で消える）。
    /// </summary>
    public static IReadOnlyCollection<string> FieldsAfterDecision(ChangeProposalRecord proposal)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        return proposal.Operation == ProposalOperation.Review ? NameFields : [];
    }
}

/// <summary>提案の作成後に対象のデータが変わっていた（旧値が一致しない・対象の行が無い・上位組織がまだ無い）。提案は反映しない。</summary>
public sealed class ProposalConflictException : Exception
{
    public ProposalConflictException()
    {
    }

    public ProposalConflictException(string message)
        : base(message)
    {
    }

    public ProposalConflictException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
