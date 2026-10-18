using System.Globalization;
using System.Text;
using JinJi.Application.Audit;
using JinJi.Domain;
using JinJi.Policy;

namespace JinJi.Application.Ingest;

/// <summary>
/// AI エージェントによる要確認の提案への推定（proposal_agent_suggestion の 1 行）。作業領域の情報で、提案の状態・値は変えない。
/// </summary>
/// <param name="Resolution">推定した解決の種類（人の解決と同じ 4 つ）。</param>
/// <param name="PersonId">候補から選ぶ人（<see cref="ResolutionChoice.SelectCandidate"/> のとき）。</param>
/// <param name="OrgUnitId">候補から選ぶ組織（<see cref="ResolutionChoice.SelectCandidate"/> のとき）。</param>
/// <param name="Rationale">エージェントが書いた理由（データとして表示し、指示として扱わない）。</param>
/// <param name="SuggestedByPersonId">推定を書いたエージェントの委任元。</param>
public sealed record AgentSuggestionRecord(
    Guid Id,
    Guid ProposalId,
    Guid BatchId,
    ResolutionChoice Resolution,
    Guid? PersonId,
    Guid? OrgUnitId,
    decimal Confidence,
    string Rationale,
    Guid SuggestedByPersonId,
    string SuggestedViaClientId,
    DateTimeOffset CreatedAt);

/// <summary>エージェントの推定の指定。</summary>
public sealed record AgentSuggestionRequest(ResolutionChoice Resolution, Guid? PersonId, Guid? OrgUnitId, decimal Confidence, string Rationale);

/// <summary>要確認の提案とその推定（エージェント向けの一覧の 1 件）。</summary>
public sealed record ReviewItem(ChangeProposalRecord Proposal, IReadOnlyList<AgentSuggestionRecord> Suggestions);

/// <summary>要確認の提案の一覧の 1 ページ。</summary>
/// <param name="NextAfter">続きがあれば、次のページの開始位置（提案の ID。この ID より後）。</param>
public sealed record ReviewPage(Guid BatchId, IReadOnlyList<ReviewItem> Items, Guid? NextAfter);

/// <summary>エージェントの推定の保存先（<see cref="IIngestStore"/> とは別の口。Infrastructure が実装する）。</summary>
public interface IAgentSuggestionStore
{
    /// <summary>推定を追加する（RLS: エージェントの閲覧者だけ、委任元は閲覧者本人、委任元は hr_admin かバッチの実行者本人）。</summary>
    Task InsertAsync(ViewerContext viewer, AgentSuggestionRecord suggestion, CancellationToken cancellationToken);

    /// <summary>提案ごとの推定（記録の古い順）。閲覧者に見えない提案の推定は含まない（RLS）。</summary>
    Task<IReadOnlyList<AgentSuggestionRecord>> ListAsync(ViewerContext viewer, IReadOnlyCollection<Guid> proposalIds, CancellationToken cancellationToken);
}

/// <summary>
/// AI エージェントが要確認の提案を読み、解決の推定を添える（MCP の list_review_proposals・suggest_review_resolution。アーキテクチャ概要 §7.1・§8）。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>推定は公開されない作業領域への書き込みで、承認は要らない。提案の状態・値は変えない（解決は人だけ。<see cref="ProposalResolutionService"/>）。
/// 公開側に出るのは、人が解決し、解決者とも取込の実行者とも別の人事管理者が承認したときだけ</item>
/// <item>読める・書けるのは、委任元が人事管理者か、その取込の実行者本人のときだけ（作業領域の RLS と同じ。見えない取込・提案は「見つからない」）</item>
/// <item>返すのは根拠（種別・行番号・候補の ID・確信度・LLM とエージェントの推定）だけで、提案の値（payload / diff）と取込元の行の値は返さない（REST と同じ）</item>
/// <item>選べるのは提案の候補（名寄せの候補・組織の候補・LLM が順位を付けた候補・提案の対象）だけ。理由は 500 文字まで、制御文字は空白にする</item>
/// <item>拒否・失敗も監査ログに残す（<see cref="SuggestAction"/>・<see cref="ReadAction"/>）</item>
/// </list>
/// </remarks>
public sealed class ProposalSuggestionService
{
    public const string ReadAction = "ingest.proposal.read";
    public const string SuggestAction = "ingest.proposal.suggest";

    /// <summary>1 ページの最大件数。</summary>
    public const int MaxLimit = 50;

    /// <summary>理由の最大の長さ（文字数）。</summary>
    public const int MaxRationaleLength = 500;

    private const string ProposalTarget = "change_proposal";
    private const string BatchTarget = "import_batch";

    private readonly IIngestStore _store;
    private readonly IAgentSuggestionStore _suggestions;
    private readonly AuditWriter _audit;
    private readonly TimeProvider _timeProvider;

    public ProposalSuggestionService(IIngestStore store, IAgentSuggestionStore suggestions, AuditWriter audit, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(suggestions);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _store = store;
        _suggestions = suggestions;
        _audit = audit;
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// 取込単位の要確認の提案を、提案の ID の順に <paramref name="limit"/> 件ずつ返す。取込単位が無い・見えなければ <see cref="ProposalNotFoundException"/>。
    /// </summary>
    public async Task<ReviewPage> ListReviewAsync(ViewerContext viewer, Guid batchId, Guid? after, int limit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, MaxLimit);
        await RequirePersonAsync(viewer, ReadAction, BatchTarget, batchId, cancellationToken);

        if (await _store.FindBatchAsync(viewer, batchId, cancellationToken) is null)
        {
            await _audit.RecordAsync(viewer, ReadAction, AuditOutcome.Failed, [], cancellationToken, BatchTarget, batchId);
            throw new ProposalNotFoundException($"取込単位 {batchId} がありません。");
        }

        var proposals = (await _store.ListProposalsAsync(viewer, batchId, cancellationToken))
            .Where(p => p.Status == ProposalStatus.NeedsReview)
            .Where(p => after is not { } position || p.Id.CompareTo(position) > 0)
            .OrderBy(p => p.Id)
            .Take(limit + 1)
            .ToList();
        var page = proposals.Take(limit).ToList();
        var suggestions = page.Count == 0 ? [] : await _suggestions.ListAsync(viewer, [.. page.Select(p => p.Id)], cancellationToken);
        var byProposal = suggestions.ToLookup(s => s.ProposalId);
        var items = page.Select(p => new ReviewItem(p, [.. byProposal[p.Id]])).ToList();

        await _audit.RecordAsync(viewer, ReadAction, AuditOutcome.Success, page.SelectMany(ProposalReviewService.Subjects), cancellationToken, BatchTarget, batchId);
        return new ReviewPage(batchId, items, proposals.Count > limit ? page[^1].Id : null);
    }

    /// <summary>提案ごとの推定（人の解決の画面・API 用。監査は提案の参照で記録する）。</summary>
    public Task<IReadOnlyList<AgentSuggestionRecord>> ListForProposalAsync(ViewerContext viewer, Guid proposalId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        return _suggestions.ListAsync(viewer, [proposalId], cancellationToken);
    }

    /// <summary>要確認の提案に推定を添える（AI エージェントだけ）。</summary>
    /// <exception cref="AccessDeniedException">AI エージェントでない（人は解決の API を使う）。</exception>
    /// <exception cref="ProposalNotFoundException">提案が無いか、見えない。</exception>
    /// <exception cref="InvalidOperationException">要確認の状態でない。</exception>
    /// <exception cref="ArgumentException">候補に無い人・組織、形の不正な指定。</exception>
    public async Task<AgentSuggestionRecord> SuggestAsync(ViewerContext viewer, Guid proposalId, AgentSuggestionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        ArgumentNullException.ThrowIfNull(request);
        if (viewer.Actor != ActorType.Agent || viewer.DelegatorPersonId is not { } delegator || viewer.ClientId is not { } clientId)
        {
            await _audit.RecordAsync(viewer, SuggestAction, AuditOutcome.Denied, [], cancellationToken, ProposalTarget, proposalId);
            throw new AccessDeniedException("推定を添えられるのは AI エージェントだけです（人は解決の操作を使います）。");
        }

        var proposal = await _store.FindProposalAsync(viewer, proposalId, cancellationToken);
        if (proposal is null)
        {
            await _audit.RecordAsync(viewer, SuggestAction, AuditOutcome.Failed, [], cancellationToken, ProposalTarget, proposalId);
            throw new ProposalNotFoundException($"提案 {proposalId} がありません。");
        }

        var subjects = ProposalReviewService.Subjects(proposal);
        if (proposal.Status != ProposalStatus.NeedsReview)
        {
            await _audit.RecordAsync(viewer, SuggestAction, AuditOutcome.Failed, subjects, cancellationToken, ProposalTarget, proposalId);
            throw new InvalidOperationException($"提案 {proposalId} は要確認の状態ではありません（解決・却下済みか、起案中の提案です）。");
        }

        string rationale;
        try
        {
            rationale = Validate(proposal, request);
        }
        catch (ArgumentException)
        {
            await _audit.RecordAsync(viewer, SuggestAction, AuditOutcome.Denied, subjects, cancellationToken, ProposalTarget, proposalId);
            throw;
        }

        var record = new AgentSuggestionRecord(
            EntityId.New(),
            proposal.Id,
            proposal.BatchId,
            request.Resolution,
            request.PersonId,
            request.OrgUnitId,
            Math.Round(request.Confidence, 3, MidpointRounding.AwayFromZero),
            rationale,
            delegator,
            clientId,
            _timeProvider.GetUtcNow());
        try
        {
            await _suggestions.InsertAsync(viewer, record, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await _audit.RecordAsync(viewer, SuggestAction, AuditOutcome.Failed, subjects, CancellationToken.None, ProposalTarget, proposalId);
            throw;
        }

        var chosen = request.PersonId is { } person ? subjects.Append(person) : subjects;
        await _audit.RecordAsync(viewer, SuggestAction, AuditOutcome.Success, chosen, cancellationToken, ProposalTarget, proposalId);
        return record;
    }

    /// <summary>指定を検査し、整えた理由を返す。</summary>
    private static string Validate(ChangeProposalRecord proposal, AgentSuggestionRequest request)
    {
        if (request.Confidence is < 0m or > 1m)
        {
            throw new ArgumentException("confidence は 0〜1 で指定してください。", nameof(request));
        }

        if (request.Resolution == ResolutionChoice.SelectCandidate)
        {
            if (request.PersonId is null && request.OrgUnitId is null)
            {
                throw new ArgumentException("select_candidate では person_id か org_unit_id を候補から指定してください。", nameof(request));
            }

            var people = proposal.Basis.Candidates.Select(c => c.PersonId)
                .Concat(proposal.Basis.Llm?.People.Select(p => p.Id) ?? [])
                .Concat(proposal.TargetId is { } target ? new[] { target } : [])
                .ToHashSet();
            var orgs = proposal.Basis.OrgCandidates
                .Concat(proposal.Basis.Llm?.Orgs.Select(o => o.Id) ?? [])
                .Concat(proposal.Payload.GetValueOrDefault(ProposalFields.OrgUnitId) is { } org && Guid.TryParse(org, CultureInfo.InvariantCulture, out var current) ? new[] { current } : [])
                .ToHashSet();
            if (request.PersonId is { } person && !people.Contains(person))
            {
                throw new ArgumentException("person_id は提案の人の候補から選んでください（候補に無い人は選べません）。", nameof(request));
            }

            if (request.OrgUnitId is { } unit && !orgs.Contains(unit))
            {
                throw new ArgumentException("org_unit_id は提案の組織の候補から選んでください（候補に無い組織は選べません）。", nameof(request));
            }
        }
        else if (request.PersonId is not null || request.OrgUnitId is not null)
        {
            throw new ArgumentException("person_id・org_unit_id は select_candidate のときだけ指定できます。", nameof(request));
        }

        var rationale = NormalizeRationale(request.Rationale);
        if (rationale.Length == 0 || rationale.Length > MaxRationaleLength)
        {
            throw new ArgumentException($"rationale は 1〜{MaxRationaleLength} 文字で書いてください。", nameof(request));
        }

        return rationale;
    }

    /// <summary>制御文字（改行を含む）を空白にし、前後の空白を除く。</summary>
    public static string NormalizeRationale(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            builder.Append(char.IsControl(c) ? ' ' : c);
        }

        return builder.ToString().Trim();
    }

    private async Task RequirePersonAsync(ViewerContext viewer, string action, string targetType, Guid targetId, CancellationToken cancellationToken)
    {
        if (viewer.Actor is ActorType.User or ActorType.Agent && viewer.EffectivePersonId is not null)
        {
            return;
        }

        await _audit.RecordAsync(viewer, action, AuditOutcome.Denied, [], cancellationToken, targetType, targetId);
        throw new AccessDeniedException("要確認の提案を読めるのは、人事管理者か取込の実行者（と、その委任を受けたエージェント）だけです。");
    }
}
