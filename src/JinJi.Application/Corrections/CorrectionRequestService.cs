using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using JinJi.Application.Audit;
using JinJi.Application.Ingest;
using JinJi.Application.People;
using JinJi.Domain;
using JinJi.Domain.Organization;
using JinJi.Policy;

namespace JinJi.Application.Corrections;

/// <summary>レポート・一覧に添える人の要約（閲覧者に見える項目だけ。見えなければ null）。</summary>
/// <param name="PrimaryOrgName">主務の組織名（所属が見えるときだけ）。</param>
public sealed record CorrectionPersonSummary(Guid PersonId, string? DisplayName, string? EmployeeNumber, string? LegalEntityName, string? PrimaryOrgName);

/// <summary>人の要約を添えた一覧の 1 ページ。</summary>
public sealed record CorrectionPageWithPeople(CorrectionPage Page, IReadOnlyDictionary<Guid, CorrectionPersonSummary> People);

/// <summary>レポート（CSV・xlsx）の材料。</summary>
/// <param name="Truncated">上限（<see cref="CorrectionOptions.MaxExportRows"/>）で打ち切ったか。</param>
public sealed record CorrectionExport(
    DateOnly Today,
    IReadOnlyList<CorrectionView> Items,
    IReadOnlyDictionary<Guid, CorrectionPersonSummary> People,
    bool Truncated);

/// <summary>
/// 修正依頼（アーキテクチャ概要 §8「修正依頼の実装の規則」）。人事パッケージの情報を直せる人（修正担当者・人事管理者）への情報提供で、
/// 起票しても jin-ji の公開データは変わらない（公開側への入口は取込の変更提案の承認だけ。§7.1）。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>起票（<see cref="ReportAsync"/>）は人か AI エージェント（委任元の名義）。対象の人は閲覧者に見える人、項目は jin-ji が保持する項目で閲覧者に見えるものだけ。
/// 理由・値の提案は保持しない情報の混入を検査し（ADR-0002）、当てはまれば保存しない。同じ人・項目の未対応の依頼があれば重複して起票せず、賛同として数える</item>
/// <item>起票時の値の指紋（依頼 ID を塩にした SHA-256。値そのものは持たない）を残し、値が変わったら自動で「解消」にする（<see cref="ReconcileAsync"/>。
/// 取込の提案の承認の直後と、ワーカーの定期ジョブ）。指紋は本人から見た今日の値（閲覧者によらない）で作る</item>
/// <item>可視範囲: 修正担当者・人事管理者はすべて、それ以外の人は自分が起票・賛同した依頼と自分についての依頼だけ（RLS）。
/// 修正担当者・人事管理者以外には起票者・対応した人を返さず、賛同しただけの依頼の理由・値の提案・対応のメモも返さない（<see cref="Mask"/>）</item>
/// <item>状態の更新（対応済み・対応不要）は修正担当者・人事管理者の「人」だけ（AI エージェントは不可）</item>
/// <item>拒否・失敗も監査ログに残す（値・理由は残さない）</item>
/// </list>
/// </remarks>
public sealed class CorrectionRequestService
{
    public const string ReportAction = "correction.report";
    public const string EndorseAction = "correction.endorse";
    public const string ReadAction = "correction.read";
    public const string ListAction = "correction.list";
    public const string CloseAction = "correction.close";
    public const string AutoResolveAction = "correction.auto_resolve";
    public const string ExportAction = "correction.export";
    public const string TargetType = "correction_request";

    /// <summary>指紋の材料の投影では退職後の公開期間で人を消さない（閲覧者・日付によって「値が無い」に変わらないように。100 年）。</summary>
    private const int SelfViewRetiredDays = 36500;

    private readonly ICorrectionStore _store;
    private readonly IOrgSnapshotProvider _snapshots;
    private readonly IPolicyRuleStore _policies;
    private readonly AuditWriter _audit;
    private readonly TimeProvider _timeProvider;
    private readonly CorrectionOptions _options;
    private readonly PersonDirectoryOptions _directory;

    public CorrectionRequestService(
        ICorrectionStore store,
        IOrgSnapshotProvider snapshots,
        IPolicyRuleStore policies,
        AuditWriter audit,
        TimeProvider timeProvider,
        CorrectionOptions? options = null,
        PersonDirectoryOptions? directory = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(snapshots);
        ArgumentNullException.ThrowIfNull(policies);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _options = options ?? new CorrectionOptions();
        _options.Validate();
        _directory = directory ?? new PersonDirectoryOptions();
        _store = store;
        _snapshots = snapshots;
        _policies = policies;
        _audit = audit;
        _timeProvider = timeProvider;
    }

    public CorrectionOptions Options => _options;

    /// <summary>修正依頼のすべてを参照できる閲覧者か（人事管理者・修正担当者。AI エージェントは委任元のロール。jin-ji 自身）。</summary>
    public static bool Handles(ViewerContext viewer)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        return viewer.HandlesCorrections || viewer.Actor == ActorType.System;
    }

    /// <summary>修正依頼の状態を変えられる閲覧者か（人事管理者・修正担当者の人）。</summary>
    public static bool CanClose(ViewerContext viewer)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        return viewer.Actor == ActorType.User && viewer.HandlesCorrections && viewer.PersonId is not null;
    }

    // -----------------------------------------------------------------------
    // 起票
    // -----------------------------------------------------------------------

    /// <summary>
    /// 人・AI エージェントが値の誤りを報告する。同じ人・項目の未対応の依頼があれば賛同として数え、自分が起票・賛同済みなら何もしない。
    /// </summary>
    /// <exception cref="AccessDeniedException">人・エージェント以外（社内システム）。</exception>
    /// <exception cref="CorrectionNotFoundException">対象の人が閲覧者に見えない（存在しないのか見えないのかは区別しない）。</exception>
    /// <exception cref="ArgumentException">項目・理由・値の提案の誤り（保持しない情報の混入を含む）。</exception>
    /// <exception cref="CorrectionQuotaExceededException">1 日の上限を超えた。</exception>
    public async Task<CorrectionReportResult> ReportAsync(ViewerContext viewer, CorrectionReportInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        ArgumentNullException.ThrowIfNull(input);
        if (viewer.Actor is not (ActorType.User or ActorType.Agent) || viewer.EffectivePersonId is not { } reporter)
        {
            await _audit.RecordAsync(viewer, ReportAction, AuditOutcome.Denied, [], cancellationToken, TargetType);
            throw new AccessDeniedException("修正依頼を起票できるのは人と、人から委任された AI エージェントだけです。");
        }

        string reason;
        string? suggested;
        try
        {
            if (!CorrectionFields.All.Contains(input.Field ?? string.Empty))
            {
                throw new ArgumentException($"field は jin-ji が保持する人の項目（{string.Join(", ", CorrectionFields.All.Order(StringComparer.Ordinal))}）から指定してください。", nameof(input));
            }

            reason = CorrectionFields.CleanFreeText(input.Reason, "reason", required: true);
            suggested = CorrectionFields.CleanSuggestedValue(input.Field!, input.SuggestedValue);
        }
        catch (ArgumentException)
        {
            await _audit.RecordAsync(viewer, ReportAction, AuditOutcome.Denied, [input.PersonId], cancellationToken, TargetType);
            throw;
        }

        var today = BusinessDate.Today(_timeProvider);
        var snapshot = await _snapshots.LoadAsync(viewer, today, today, cancellationToken);
        var policies = await _policies.LoadActiveAsync(viewer, cancellationToken);
        var projector = new PersonProjector(new PolicyEvaluator(policies, new RelationEvaluator(snapshot)), snapshot, today, _directory.RetiredPublicDays);
        if (projector.Project(viewer, input.PersonId) is not { } seen)
        {
            await _audit.RecordAsync(viewer, ReportAction, AuditOutcome.Failed, [], cancellationToken, TargetType);
            throw new CorrectionNotFoundException($"人 {input.PersonId} は存在しないか、参照できません。");
        }

        if (!seen.CanSee(input.Field!))
        {
            await _audit.RecordAsync(viewer, ReportAction, AuditOutcome.Denied, [input.PersonId], cancellationToken, TargetType);
            throw new ArgumentException($"項目 {input.Field} は見えない項目なので報告できません（見えている項目だけを報告できます）。", nameof(input));
        }

        var since = _timeProvider.GetUtcNow().AddDays(-1);
        if (await _store.CountRecentReportsAsync(viewer, since, cancellationToken) >= _options.MaxReportsPerDay)
        {
            await _audit.RecordAsync(viewer, ReportAction, AuditOutcome.Denied, [input.PersonId], cancellationToken, TargetType);
            throw new CorrectionQuotaExceededException(
                $"修正依頼の起票・賛同は 1 人あたり 24 時間に {_options.MaxReportsPerDay} 件までです。時間をおいてください。",
                TimeSpan.FromHours(1));
        }

        // 自分が起票・賛同した未対応の依頼が既にあれば何もしない
        var mine = (await _store.ListAsync(viewer, new CorrectionQuery { PersonId = input.PersonId, Limit = CorrectionQuery.MaxLimit }, cancellationToken))
            .FirstOrDefault(r => r.PersonField == input.Field && (r.ReportedByPersonId == reporter || r.EndorsedByViewer));
        if (mine is not null)
        {
            await _audit.RecordAsync(viewer, ReportAction, AuditOutcome.Success, [input.PersonId], cancellationToken, TargetType, mine.Id);
            return new CorrectionReportResult(CorrectionReportOutcome.AlreadyReported, Mask(viewer, mine, today));
        }

        var id = EntityId.New();
        var record = new CorrectionRecord
        {
            Id = id,
            Origin = viewer.Actor == ActorType.Agent ? CorrectionOrigin.Agent : CorrectionOrigin.User,
            Issue = CorrectionIssue.ReportedValue,
            Status = CorrectionStatus.Open,
            Message = viewer.Actor == ActorType.Agent
                ? $"AI エージェントが委任元の名義で、項目「{CorrectionFields.Label(input.Field!)}」の値の誤りを報告しました。"
                : $"jin-ji の利用者が、項目「{CorrectionFields.Label(input.Field!)}」の値の誤りを報告しました。",
            PersonId = input.PersonId,
            PersonField = input.Field,
            Reason = reason,
            SuggestedValue = suggested,
            ValueFingerprint = Fingerprint(id, input.Field!, SelfView(snapshot, today, input.PersonId)),
            ReportedByPersonId = reporter,
            ReportedViaClientId = viewer.Actor == ActorType.Agent ? viewer.ClientId : null,
            CreatedAt = _timeProvider.GetUtcNow(),
        };

        // 同じ人・項目の未対応の依頼（部分一意索引）に当たったら賛同にする。閉じられた直後なら起票し直す（1 回だけ）
        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (await _store.TryInsertReportAsync(viewer, record, cancellationToken))
            {
                await _audit.RecordAsync(viewer, ReportAction, AuditOutcome.Success, [input.PersonId], cancellationToken, TargetType, id);
                var created = await _store.FindAsync(viewer, id, cancellationToken) ?? record;
                return new CorrectionReportResult(CorrectionReportOutcome.Created, Mask(viewer, created, today));
            }

            if (await _store.FindOpenReportAsync(input.PersonId, input.Field!, cancellationToken) is not { } open)
            {
                continue;
            }

            var endorsed = open.ReportedByPersonId != reporter && await _store.TryEndorseAsync(viewer, open.Id, EntityId.New(), cancellationToken);
            await _audit.RecordAsync(viewer, EndorseAction, AuditOutcome.Success, [input.PersonId], cancellationToken, TargetType, open.Id);
            var current = await _store.FindAsync(viewer, open.Id, cancellationToken)
                ?? throw new InvalidOperationException($"修正依頼 {open.Id} を読めませんでした。");
            return new CorrectionReportResult(endorsed ? CorrectionReportOutcome.Endorsed : CorrectionReportOutcome.AlreadyReported, Mask(viewer, current, today));
        }

        await _audit.RecordAsync(viewer, ReportAction, AuditOutcome.Failed, [input.PersonId], cancellationToken, TargetType);
        throw new InvalidOperationException("同じ人・項目の依頼が同時に起票・更新されたため、起票できませんでした。もう一度試してください。");
    }

    // -----------------------------------------------------------------------
    // 参照
    // -----------------------------------------------------------------------

    /// <summary>閲覧者に見える依頼の一覧（ID の順 = 起票の古い順）。</summary>
    public async Task<CorrectionPage> ListAsync(ViewerContext viewer, CorrectionQuery query, CancellationToken cancellationToken) =>
        (await ListCoreAsync(viewer, query, includePeople: false, cancellationToken)).Page;

    /// <summary>閲覧者に見える依頼の一覧に、対象の人の要約（閲覧者に見える項目だけ）を添える（REST・画面）。</summary>
    public Task<CorrectionPageWithPeople> ListWithPeopleAsync(ViewerContext viewer, CorrectionQuery query, CancellationToken cancellationToken) =>
        ListCoreAsync(viewer, query, includePeople: true, cancellationToken);

    /// <summary>1 件。見えなければ <see cref="CorrectionNotFoundException"/>。</summary>
    public async Task<CorrectionView> GetAsync(ViewerContext viewer, Guid id, CancellationToken cancellationToken)
    {
        await RequirePersonViewerAsync(viewer, ReadAction, id, cancellationToken);
        var record = await _store.FindAsync(viewer, id, cancellationToken);
        if (record is null)
        {
            await _audit.RecordAsync(viewer, ReadAction, AuditOutcome.Failed, [], cancellationToken, TargetType, id);
            throw new CorrectionNotFoundException($"修正依頼 {id} は存在しないか、参照できません。");
        }

        await _audit.RecordAsync(viewer, ReadAction, AuditOutcome.Success, Subjects([record]), cancellationToken, TargetType, id);
        return Mask(viewer, record, BusinessDate.Today(_timeProvider));
    }

    /// <summary>画面内の通知の件数（修正担当者・人事管理者だけ）。</summary>
    public async Task<CorrectionSummary> SummaryAsync(ViewerContext viewer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        if (!Handles(viewer))
        {
            throw new AccessDeniedException("修正依頼の件数を見られるのは、修正担当者と人事管理者だけです。");
        }

        var counts = await CountAsync(viewer, null, cancellationToken);
        return new CorrectionSummary(counts.Open, counts.New, counts.Stale, _options.NewWithinDays, _options.StaleAfterDays, counts.OldestCreatedAt);
    }

    /// <summary>未対応の件数（新しい依頼は <paramref name="newSince"/> より後。null なら <see cref="CorrectionOptions.NewWithinDays"/> 日以内）。</summary>
    public Task<CorrectionCounts> CountAsync(ViewerContext viewer, DateTimeOffset? newSince, CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        return _store.CountOpenAsync(viewer, newSince ?? now.AddDays(-_options.NewWithinDays), now.AddDays(-_options.StaleAfterDays), cancellationToken);
    }

    /// <summary>
    /// レポートの材料（修正担当者・人事管理者の人だけ。AI エージェントは不可）。起票の古い順に <see cref="CorrectionOptions.MaxExportRows"/> 件まで。
    /// </summary>
    public async Task<CorrectionExport> ExportAsync(ViewerContext viewer, CorrectionStatusFilter status, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        if (!(CanClose(viewer) || viewer.Actor == ActorType.System))
        {
            await _audit.RecordAsync(viewer, ExportAction, AuditOutcome.Denied, [], cancellationToken, TargetType);
            throw new AccessDeniedException("修正依頼のレポートを出せるのは、修正担当者と人事管理者（人）だけです。");
        }

        var rows = await _store.ListAsync(viewer, new CorrectionQuery { Status = status, Limit = _options.MaxExportRows }, cancellationToken);
        var truncated = rows.Count > _options.MaxExportRows;
        var records = rows.Take(_options.MaxExportRows).ToList();
        var today = BusinessDate.Today(_timeProvider);
        var people = await SummarizePeopleAsync(viewer, records, today, cancellationToken);
        await _audit.RecordAsync(viewer, ExportAction, AuditOutcome.Success, Subjects(records), cancellationToken, TargetType);
        return new CorrectionExport(today, [.. records.Select(r => Mask(viewer, r, today))], people, truncated);
    }

    // -----------------------------------------------------------------------
    // 状態の更新
    // -----------------------------------------------------------------------

    /// <summary>未対応の依頼を対応済み・対応不要にする（修正担当者・人事管理者の人だけ）。</summary>
    public async Task<CorrectionView> CloseAsync(ViewerContext viewer, Guid id, CorrectionCloseInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        ArgumentNullException.ThrowIfNull(input);
        if (!CanClose(viewer))
        {
            await _audit.RecordAsync(viewer, CloseAction, AuditOutcome.Denied, [], cancellationToken, TargetType, id);
            throw new AccessDeniedException("修正依頼の状態を変えられるのは、修正担当者と人事管理者（人）だけです。");
        }

        string note;
        try
        {
            if (input.Status is not (CorrectionStatus.Resolved or CorrectionStatus.Dismissed))
            {
                throw new ArgumentException("status は resolved（対応済み）か dismissed（対応不要）で指定してください。", nameof(input));
            }

            note = CorrectionFields.CleanFreeText(input.Note, "note", required: false);
        }
        catch (ArgumentException)
        {
            await _audit.RecordAsync(viewer, CloseAction, AuditOutcome.Denied, [], cancellationToken, TargetType, id);
            throw;
        }

        var record = await _store.FindAsync(viewer, id, cancellationToken);
        if (record is null)
        {
            await _audit.RecordAsync(viewer, CloseAction, AuditOutcome.Failed, [], cancellationToken, TargetType, id);
            throw new CorrectionNotFoundException($"修正依頼 {id} は存在しないか、参照できません。");
        }

        var subjects = Subjects([record]);
        if (record.Status != CorrectionStatus.Open
            || !await _store.CloseAsync(viewer, id, input.Status, CorrectionClosedKind.Person, note.Length == 0 ? null : note, cancellationToken))
        {
            await _audit.RecordAsync(viewer, CloseAction, AuditOutcome.Failed, subjects, cancellationToken, TargetType, id);
            throw new InvalidOperationException($"修正依頼 {id} は未対応ではありません（既に閉じています）。");
        }

        await _audit.RecordAsync(viewer, CloseAction, AuditOutcome.Success, subjects, cancellationToken, TargetType, id);
        var closed = await _store.FindAsync(viewer, id, cancellationToken) ?? record;
        return Mask(viewer, closed, BusinessDate.Today(_timeProvider));
    }

    /// <summary>
    /// 自動の解消: 値の指紋を持つ未対応の依頼について、今日の値の指紋が起票時と違えば「解消」にする。解消にした件数を返す。
    /// 取込の提案の承認の直後（対象の人だけ）と、ワーカーの定期ジョブ（すべて。将来の日付で効く変更を拾う）から呼ぶ。
    /// </summary>
    /// <param name="viewer">状態を変えられる閲覧者（人事管理者・修正担当者の人、jin-ji 自身）。</param>
    /// <param name="personIds">対象の人（null ならすべて）。</param>
    public async Task<int> ReconcileAsync(ViewerContext viewer, IReadOnlyCollection<Guid>? personIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        if (!(CanClose(viewer) || (viewer.Actor == ActorType.System && viewer.IsHrAdmin)))
        {
            throw new AccessDeniedException("自動の解消は、修正担当者・人事管理者（人）か jin-ji 自身だけが行えます。");
        }

        if (personIds is { Count: 0 })
        {
            return 0;
        }

        var open = await _store.ListOpenReportsAsync(viewer, personIds, cancellationToken);
        if (open.Count == 0)
        {
            return 0;
        }

        var today = BusinessDate.Today(_timeProvider);
        var snapshot = await _snapshots.LoadAsync(viewer, today, today, cancellationToken);
        var resolved = new List<CorrectionRecord>();
        foreach (var record in open)
        {
            if (record.PersonId is not { } person || record.PersonField is not { } field || record.ValueFingerprint is not { } before
                || CorrectionFields.Retired.ContainsKey(field))
            {
                continue;
            }

            if (Fingerprint(record.Id, field, SelfView(snapshot, today, person)) != before
                && await _store.CloseAsync(viewer, record.Id, CorrectionStatus.AutoResolved, CorrectionClosedKind.Auto, null, cancellationToken))
            {
                resolved.Add(record);
            }
        }

        if (resolved.Count > 0)
        {
            await _audit.RecordAsync(viewer, AutoResolveAction, AuditOutcome.Success, Subjects(resolved), cancellationToken, TargetType);
        }

        return resolved.Count;
    }

    // -----------------------------------------------------------------------
    // 伏せる・指紋
    // -----------------------------------------------------------------------

    /// <summary>
    /// 閲覧者に見せてよい形にする。修正担当者・人事管理者（と jin-ji 自身）はすべて。それ以外は、起票者・対応した人・クライアントを伏せ
    /// （起票者本人には自分を返す）、自分が起票したものでも自分についてのものでもない依頼（賛同しただけ）の理由・値の提案・対応のメモも伏せる。
    /// </summary>
    public static CorrectionView Mask(ViewerContext viewer, CorrectionRecord record, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        ArgumentNullException.ThrowIfNull(record);
        var handler = Handles(viewer);
        var me = viewer.EffectivePersonId;
        var reporter = me is not null && record.ReportedByPersonId == me;
        var about = me is not null && record.PersonId == me;
        var content = handler || reporter || about || record.Origin == CorrectionOrigin.Ingest;
        return new CorrectionView
        {
            Id = record.Id,
            Origin = record.Origin,
            Issue = record.Issue,
            Status = record.Status,
            Message = record.Message,
            BatchId = record.BatchId,
            FileName = record.FileName,
            SourceRowNumber = record.SourceRowNumber,
            SourceField = record.SourceField,
            PersonId = record.PersonId,
            PersonField = record.PersonField,
            Reason = content ? record.Reason : null,
            SuggestedValue = content ? record.SuggestedValue : null,
            ReportedByPersonId = handler || reporter ? record.ReportedByPersonId : null,
            ReportedViaClientId = handler || reporter ? record.ReportedViaClientId : null,
            ReportedByViewer = reporter,
            EndorsedByViewer = record.EndorsedByViewer,
            AboutViewer = about,
            Endorsements = record.Endorsements,
            CreatedAt = record.CreatedAt,
            AgeDays = Math.Max(0, today.DayNumber - DateOnly.FromDateTime(record.CreatedAt.ToOffset(BusinessDate.UtcOffset).DateTime).DayNumber),
            ClosedAt = record.ClosedAt,
            ClosedKind = record.ClosedKind,
            ClosedByPersonId = handler ? record.ClosedByPersonId : null,
            ClosedNote = content ? record.ClosedNote : null,
        };
    }

    /// <summary>
    /// 値の指紋: SHA-256（依頼 ID の 16 バイト + 項目名 + 正規化した値）の 16 進。値そのものを持たずに「値が変わったか」だけを判定する。
    /// 依頼 ID を塩にするので、同じ値でも依頼ごとに違う指紋になる（表を作って値を引けない）。
    /// </summary>
    /// <param name="view">本人から見た今日の投影（人に見えなければ null）。</param>
    public static string Fingerprint(Guid requestId, string field, PersonView? view)
    {
        ArgumentNullException.ThrowIfNull(field);
        var canonical = field + "\n" + (view is null ? "\0absent" : CanonicalValue(field, view));
        var bytes = requestId.ToByteArray().Concat(Encoding.UTF8.GetBytes(canonical)).ToArray();
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    /// <summary>
    /// 本人から見た今日の投影（閲覧者によらない値。人事管理者以外と同じく、将来の終了日・内示は含まない）。指紋の材料にだけ使い、応答には出さない。
    /// </summary>
    private PersonView? SelfView(OrgSnapshot snapshot, DateOnly today, Guid personId)
    {
        var self = ViewerContext.ForUser(personId, [], Channel.Ui, FieldLimit.Unrestricted);
        var projector = new PersonProjector(new PolicyEvaluator(PolicySet.Empty, new RelationEvaluator(snapshot)), snapshot, today, SelfViewRetiredDays);
        return projector.Project(self, personId);
    }

    /// <summary>項目の値の正規化（順序によらない・表示名の変化に左右されない形）。</summary>
    private static string CanonicalValue(string field, PersonView v)
    {
        static string S(string? value) => value ?? "\0null";
        static string D(DateOnly? value) => value?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "\0null";
        return field switch
        {
            PersonFields.EmployeeNumber => S(v.EmployeeNumber),
            PersonFields.LegalEntity => S(v.LegalEntity?.Id.ToString("D")),
            PersonFields.DisplayName => S(v.DisplayName),
            PersonFields.FamilyName => S(v.FamilyName),
            PersonFields.GivenName => S(v.GivenName),
            PersonFields.FamilyNameKana => S(v.FamilyNameKana),
            PersonFields.GivenNameKana => S(v.GivenNameKana),
            PersonFields.FamilyNameLatin => S(v.FamilyNameLatin),
            PersonFields.GivenNameLatin => S(v.GivenNameLatin),
            PersonFields.WorkEmail => S(v.WorkEmail),
            PersonFields.ExtensionNumber => S(v.ExtensionNumber),
            PersonFields.WorkMobilePhone => S(v.WorkMobilePhone),
            PersonFields.Site => S(v.Site?.Id.ToString("D")),
            PersonFields.RemoteWork => v.RemoteWork switch { true => "true", false => "false", null => "\0null" },
            PersonFields.Workplace => v.Workplace is { } w ? string.Join('|', w.FloorId.ToString("D"), w.ZoneKey?.ToString("D") ?? "\0null") : "\0null",
            PersonFields.Seat => S(v.Seat?.SeatId.ToString("D")),
            PersonFields.EmploymentType => S(v.EmploymentType),
            PersonFields.JobType => S(v.JobType),
            PersonFields.HireDate => D(v.HireDate),
            PersonFields.TerminationDate => D(v.TerminationDate),
            PersonFields.EmploymentStatus => S(v.EmploymentStatus),
            PersonFields.AccountStatus => S(v.AccountStatus),
            PersonFields.AccountSuspension => v.AccountSuspension is { } s ? D(s.From) + "|" + D(s.Until) : "\0null",
            PersonFields.Assignments => string.Join(
                "\n",
                (v.Assignments ?? []).Select(a => string.Join(
                    '|', a.OrgUnitId.ToString("D"), a.Type, S(a.PositionTitle), a.Secondment, D(a.ValidFrom))).Order(StringComparer.Ordinal)),
            PersonFields.Managers => string.Join(",", (v.Managers ?? []).Select(m => m.ToString("D")).Order(StringComparer.Ordinal)),
            PersonFields.PersonnelActions => string.Join(",", (v.PersonnelActions ?? []).Select(a => a.Id.ToString("D")).Order(StringComparer.Ordinal)),
            _ => throw new ArgumentOutOfRangeException(nameof(field), field, "未知の項目です。"),
        };
    }

    // -----------------------------------------------------------------------
    // 内部
    // -----------------------------------------------------------------------

    private async Task<CorrectionPageWithPeople> ListCoreAsync(ViewerContext viewer, CorrectionQuery query, bool includePeople, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        await RequirePersonViewerAsync(viewer, ListAction, null, cancellationToken);
        if (query.Limit is < 1 or > CorrectionQuery.MaxLimit)
        {
            throw new ArgumentOutOfRangeException(nameof(query), query.Limit, $"件数は 1〜{CorrectionQuery.MaxLimit} で指定してください。");
        }

        var rows = await _store.ListAsync(viewer, query, cancellationToken);
        var page = rows.Take(query.Limit).ToList();
        var today = BusinessDate.Today(_timeProvider);
        var people = includePeople ? await SummarizePeopleAsync(viewer, page, today, cancellationToken) : new Dictionary<Guid, CorrectionPersonSummary>();
        await _audit.RecordAsync(viewer, ListAction, AuditOutcome.Success, [.. Subjects(page), .. people.Keys], cancellationToken, TargetType);
        return new CorrectionPageWithPeople(
            new CorrectionPage([.. page.Select(r => Mask(viewer, r, today))], rows.Count > query.Limit ? page[^1].Id : null),
            people);
    }

    /// <summary>対象の人の要約（閲覧者に見える項目だけ。見えない人は含めない）。</summary>
    private async Task<Dictionary<Guid, CorrectionPersonSummary>> SummarizePeopleAsync(
        ViewerContext viewer,
        IReadOnlyCollection<CorrectionRecord> records,
        DateOnly today,
        CancellationToken cancellationToken)
    {
        var ids = records.Select(r => r.PersonId).OfType<Guid>().Distinct().ToList();
        var result = new Dictionary<Guid, CorrectionPersonSummary>();
        if (ids.Count == 0)
        {
            return result;
        }

        var snapshot = await _snapshots.LoadAsync(viewer, today, today, cancellationToken);
        var policies = await _policies.LoadActiveAsync(viewer, cancellationToken);
        var projector = new PersonProjector(new PolicyEvaluator(policies, new RelationEvaluator(snapshot)), snapshot, today, _directory.RetiredPublicDays);
        foreach (var id in ids)
        {
            if (projector.Project(viewer, id) is not { } view)
            {
                continue;
            }

            var primary = view.Assignments?
                .Where(a => a.Type == AssignmentType.Primary)
                .OrderBy(a => a.ValidFrom)
                .Select(a => a.OrgUnitName)
                .FirstOrDefault();
            result[id] = new CorrectionPersonSummary(id, view.DisplayName, view.EmployeeNumber, view.LegalEntity?.Name, primary);
        }

        return result;
    }

    private async Task RequirePersonViewerAsync(ViewerContext viewer, string action, Guid? id, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        if ((viewer.Actor is ActorType.User or ActorType.Agent && viewer.EffectivePersonId is not null) || viewer.Actor == ActorType.System)
        {
            return;
        }

        await _audit.RecordAsync(viewer, action, AuditOutcome.Denied, [], cancellationToken, TargetType, id);
        throw new AccessDeniedException("修正依頼を参照できるのは人と、人から委任された AI エージェントだけです。");
    }

    private static IEnumerable<Guid> Subjects(IEnumerable<CorrectionRecord> records) =>
        records.Select(r => r.PersonId).OfType<Guid>().Distinct();
}
