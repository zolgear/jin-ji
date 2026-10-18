using JinJi.Application;
using JinJi.Application.Ingest;
using JinJi.Domain;
using JinJi.Policy;
using Npgsql;

namespace JinJi.IntegrationTests.Ingest;

/// <summary>
/// 要確認の提案の解決（候補から選ぶ・新規として登録する・修正依頼に回す・却下）を実 PostgreSQL で確かめる。テストごとに一時データベースを作る。
/// 実行者は hr1、承認者は hr2、解決者は head（人事管理者として振る舞う）。実行者が解決する 2 人体制の場合も確かめる。
/// </summary>
public sealed class ResolutionTests : IAsyncLifetime
{
    private const string ActionMapping = """
        {
          "name": "jirei",
          "target": "personnel_actions",
          "legalEntityAliases": { "A": ["テスト株式会社"] },
          "fields": { "action_date": ["発令日"], "employee_number": ["社員番号"], "full_name": ["氏名"], "action_text": ["発令内容"] }
        }
        """;

    private const string PeopleMapping = """
        {
          "name": "people",
          "target": "people",
          "legalEntityCode": "A",
          "fields": {
            "employee_number": ["社員番号"], "full_name": ["氏名"], "work_email": ["社用メール"], "employment_type": ["雇用区分"],
            "hire_date": ["入社日"], "org_code": ["所属コード"]
          }
        }
        """;

    private IngestDatabase _db = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DateOnly Today => IngestDatabase.Today;

    private ViewerContext Resolver => _db.HrUser("head");

    public async ValueTask InitializeAsync() => _db = await IngestDatabase.CreateAsync(Ct);

    public ValueTask DisposeAsync() => _db.DisposeAsync();

    [Fact(DisplayName = "解決: 候補から選ぶと通常の提案（起案中）になり、解決者・実行者とは別の人事管理者が承認して反映する")]
    public async Task SelectCandidate_CreatesProposedProposal_ApprovedByAnotherAdmin()
    {
        var review = await ImportReviewAsync($"{Today.AddDays(-1):yyyy/MM/dd},,鈴木 次郎,営業一課に配置換えを命ずる");
        Assert.Equal([_db.Id("member")], review.Basis.Candidates.Select(c => c.PersonId));

        var result = await _db.Resolution.ResolveAsync(Resolver, review.Id, new ResolutionRequest(ResolutionChoice.SelectCandidate, _db.Id("member")), Ct);

        Assert.Equal((ProposalStatus.Resolved, ProposalResolution.SelectedCandidate), (result.Status, result.Resolution));
        var source = (await _db.Store.FindProposalAsync(_db.HrUser("hr2"), review.Id, Ct))!;
        Assert.Equal((ProposalStatus.Resolved, ProposalResolution.SelectedCandidate, _db.Id("head")), (source.Status, source.Resolution, source.DecidedByPersonId));
        var derived = (await _db.Store.FindProposalAsync(_db.HrUser("hr2"), Assert.Single(result.DerivedProposalIds), Ct))!;
        Assert.Equal(
            (ProposalStatus.Proposed, ProposalTargetKind.PersonnelAction, ProposalOperation.Create, _db.Id("member"), review.Id, _db.Id("head"), _db.Id("hr1")),
            (derived.Status, derived.TargetKind, derived.Operation, derived.TargetId!.Value, derived.ResolvedFromProposalId!.Value, derived.ResolvedByPersonId!.Value, derived.ProposedByPersonId));
        Assert.Equal(_db.Id("member").ToString("D"), derived.Payload[ProposalFields.PersonId]);
        Assert.Equal(review.Basis.RowNumbers, derived.Basis.RowNumbers);

        // 解決者は自分の解決の結果を承認できない。実行者も承認できない。別の人事管理者が承認して反映する
        await Assert.ThrowsAsync<AccessDeniedException>(() => _db.Review.ApproveAsync(Resolver, derived.Id, Ct));
        await Assert.ThrowsAsync<AccessDeniedException>(() => _db.Review.ApproveAsync(_db.HrUser("hr1"), derived.Id, Ct));
        await _db.Review.ApproveAsync(_db.HrUser("hr2"), derived.Id, Ct);
        Assert.Equal(["transfer"], await _db.QueryAsync<string>(null, "SELECT action_type FROM personnel_action WHERE person_id = $1", Ct, _db.Id("member")));
        Assert.Contains(
            "head:success",
            await _db.QueryAsync<string>(null, "SELECT CASE WHEN actor_person_id = $1 THEN 'head' ELSE 'other' END || ':' || outcome FROM audit_event WHERE action = 'ingest.proposal.resolve'", Ct, _db.Id("head")));
    }

    [Fact(DisplayName = "解決: 取込の実行者が解決してもよい（2 人体制）。承認は実行者とは別の人事管理者だけ")]
    public async Task Executor_MayResolve_ApprovedByAnotherAdmin()
    {
        var review = await ImportReviewAsync($"{Today.AddDays(-1):yyyy/MM/dd},,鈴木 次郎,営業一課に配置換えを命ずる");

        var result = await _db.Resolution.ResolveAsync(_db.HrUser("hr1"), review.Id, new ResolutionRequest(ResolutionChoice.SelectCandidate, _db.Id("member")), Ct);
        var derived = (await _db.Store.FindProposalAsync(_db.HrUser("hr2"), Assert.Single(result.DerivedProposalIds), Ct))!;
        Assert.Equal((_db.Id("hr1"), _db.Id("hr1")), (derived.ResolvedByPersonId!.Value, derived.ProposedByPersonId));

        // 実行者（= 解決者）は承認できない。別の人事管理者が承認して反映する
        await Assert.ThrowsAsync<AccessDeniedException>(() => _db.Review.ApproveAsync(_db.HrUser("hr1"), derived.Id, Ct));
        var selfApproval = await Assert.ThrowsAsync<PostgresException>(() => _db.ExecuteAsync(
            _db.HrUser("hr1"),
            "UPDATE change_proposal SET status = 'applied', decided_by_person_id = $2, decided_at = now(), applied_at = now() WHERE id = $1",
            Ct,
            derived.Id,
            _db.Id("hr1")));
        Assert.Equal(PostgresErrorCodes.CheckViolation, selfApproval.SqlState);
        await _db.Review.ApproveAsync(_db.HrUser("hr2"), derived.Id, Ct);
        Assert.Equal(["transfer"], await _db.QueryAsync<string>(null, "SELECT action_type FROM personnel_action WHERE person_id = $1", Ct, _db.Id("member")));
    }

    [Fact(DisplayName = "解決: 取込の実行者は要確認の提案を却下してもよい（公開側は変わらない）")]
    public async Task Executor_MayReject()
    {
        var review = await ImportReviewAsync($"{Today.AddDays(-1):yyyy/MM/dd},,鈴木 次郎,営業一課に配置換えを命ずる");

        var result = await _db.Resolution.ResolveAsync(_db.HrUser("hr1"), review.Id, new ResolutionRequest(ResolutionChoice.Reject), Ct);

        Assert.Equal(ProposalStatus.Rejected, result.Status);
        Assert.Empty(await _db.QueryAsync<string>(null, "SELECT action_type FROM personnel_action WHERE person_id = $1", Ct, _db.Id("member")));
    }

    [Fact(DisplayName = "解決: 人事管理者の人だけ。一般の人・AI エージェント・ワーカーは解決できない")]
    public async Task Resolve_RequiresHrAdminUser()
    {
        var review = await ImportReviewAsync($"{Today.AddDays(-1):yyyy/MM/dd},,鈴木 次郎,営業一課に配置換えを命ずる");
        var request = new ResolutionRequest(ResolutionChoice.SelectCandidate, _db.Id("member"));

        await Assert.ThrowsAsync<AccessDeniedException>(() => _db.Resolution.ResolveAsync(_db.User("takahashi"), review.Id, request, Ct));
        await Assert.ThrowsAsync<AccessDeniedException>(() => _db.Resolution.ResolveAsync(
            ViewerContext.ForAgent("agent-x", _db.Id("hr2"), [Roles.HrAdmin], Channel.Mcp, FieldLimit.Unrestricted), review.Id, request, Ct));
        await Assert.ThrowsAsync<AccessDeniedException>(() => _db.Resolution.ResolveAsync(IngestDatabase.Worker, review.Id, request, Ct));
        Assert.Equal(ProposalStatus.NeedsReview, (await _db.Store.FindProposalAsync(_db.HrUser("hr2"), review.Id, Ct))!.Status);
        Assert.Equal(3, (await _db.QueryAsync<string>(null, "SELECT outcome FROM audit_event WHERE action = 'ingest.proposal.resolve' AND outcome = 'denied'", Ct)).Count);
    }

    [Fact(DisplayName = "解決: 候補に無い人・組織は選べない。所属のあいまいな辞令は組織を選ばないと解決できない。解決済みは解決し直せない")]
    public async Task Select_ValidatesCandidates()
    {
        var review = await ImportReviewAsync($"{Today.AddDays(-1):yyyy/MM/dd},E0001,山田 太郎,第一営業部 課長を命ずる");
        Assert.Equal(SupplementCodes.AmbiguousOrg, review.Basis.Code);
        Assert.Equal(new[] { _db.Id("S1"), _db.Id("S2") }.Order(), review.Basis.OrgCandidates.Order());

        await Assert.ThrowsAsync<ArgumentException>(() => _db.Resolution.ResolveAsync(Resolver, review.Id, new ResolutionRequest(ResolutionChoice.SelectCandidate), Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => _db.Resolution.ResolveAsync(Resolver, review.Id, new ResolutionRequest(ResolutionChoice.SelectCandidate, OrgUnitId: _db.Id("HR")), Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => _db.Resolution.ResolveAsync(Resolver, review.Id, new ResolutionRequest(ResolutionChoice.SelectCandidate, _db.Id("takahashi"), _db.Id("S1")), Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => _db.Resolution.ResolveAsync(Resolver, review.Id, new ResolutionRequest(ResolutionChoice.RegisterNew), Ct));

        var result = await _db.Resolution.ResolveAsync(Resolver, review.Id, new ResolutionRequest(ResolutionChoice.SelectCandidate, OrgUnitId: _db.Id("S1")), Ct);
        var derived = (await _db.Store.FindProposalAsync(_db.HrUser("hr2"), Assert.Single(result.DerivedProposalIds), Ct))!;
        Assert.Equal((_db.Id("S1").ToString("D"), "primary", "課長"), (derived.Payload[ProposalFields.OrgUnitId], derived.Payload[ProposalFields.AssignmentType], derived.Payload[ProposalFields.PositionTitle]));

        await Assert.ThrowsAsync<InvalidOperationException>(() => _db.Resolution.ResolveAsync(Resolver, review.Id, new ResolutionRequest(ResolutionChoice.Reject), Ct));
        await _db.Review.ApproveAsync(_db.HrUser("hr2"), derived.Id, Ct);
        var snapshot = await _db.Snapshots.LoadAsync(_db.HrUser("hr2"), Today, Today, Ct);
        var primary = snapshot.AssignmentsOf(_db.Id("head")).Single(a => a.Type == Domain.Organization.AssignmentType.Primary);
        Assert.Equal((_db.Id("S1"), "課長"), (primary.OrgUnitId, primary.PositionTitle));
    }

    [Fact(DisplayName = "解決: 修正依頼に回すと修正依頼（値を書かない）を作り、却下・修正依頼のどちらでも人に対応付けられなかった行の氏名を作業領域から消す")]
    public async Task ReferAndReject_ScrubNames()
    {
        var csv = string.Join(
            "\n",
            "発令日,社員番号,氏名,発令内容",
            $"{Today.AddDays(-1):yyyy/MM/dd},,佐伯 花子,営業一課に配置換えを命ずる",
            $"{Today.AddDays(-1):yyyy/MM/dd},,佐伯 一子,営業二課に配置換えを命ずる");
        var result = await _db.ImportCsvAsync(csv, ActionMapping, Ct);
        var reviews = (await _db.Store.ListProposalsAsync(_db.HrUser("hr2"), result.BatchId, Ct)).OrderBy(p => p.Basis.RowNumbers[0]).ToList();
        Assert.All(reviews, r => Assert.Equal((ProposalOperation.Review, (Guid?)null), (r.Operation, r.TargetId)));

        // 人に対応付けられなかった行は、staging の保持期限を短くしてある（既定 14 日）
        var expires = await _db.QueryAsync<DateTime>(null, "SELECT expires_at FROM staging_row WHERE import_batch_id = $1", Ct, result.BatchId);
        Assert.All(expires, e => Assert.InRange(e, DateTime.UtcNow.AddDays(13), DateTime.UtcNow.AddDays(15)));

        var referred = await _db.Resolution.ResolveAsync(Resolver, reviews[0].Id, new ResolutionRequest(ResolutionChoice.ReferToCorrection), Ct);
        Assert.Equal((ProposalStatus.Resolved, ProposalResolution.ReferredToCorrection), (referred.Status, referred.Resolution));
        var rejected = await _db.Resolution.ResolveAsync(Resolver, reviews[1].Id, new ResolutionRequest(ResolutionChoice.Reject), Ct);
        Assert.Equal(ProposalStatus.Rejected, rejected.Status);

        var correction = Assert.Single(await _db.Store.ListCorrectionsAsync(_db.HrUser("hr2"), result.BatchId, Ct), c => c.Issue == CorrectionIssue.ReferredFromReview);
        Assert.Equal(referred.CorrectionId, correction.Id);
        Assert.Equal(2, correction.SourceRowNumber);
        Assert.DoesNotContain("佐伯", correction.Message, StringComparison.Ordinal);

        var everything = string.Join('\n', await _db.QueryAsync<string>(null, "SELECT data::text FROM staging_row UNION ALL SELECT data::text FROM normalized_row", Ct));
        Assert.DoesNotContain("佐伯", everything, StringComparison.Ordinal);
        Assert.DoesNotContain("花子", everything, StringComparison.Ordinal);
        Assert.Contains("営業一課", everything, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "解決: 人の取込の要確認は、候補から選ぶと属性の差分の提案に、新規として登録すると新しい人の提案になる")]
    public async Task PeopleReview_SelectOrRegisterNew()
    {
        // 社員番号は jin-ji に無いが、社用メールが既存の人（髙橋）のもの → 規則の食い違いで要確認
        var csv = string.Join(
            "\n",
            "社員番号,氏名,社用メール,雇用区分,入社日,所属コード",
            "E0100,髙橋 一郎太,ichiro.takahashi@example.co.jp,正社員,2020/04/01,S1",
            "E0101,新井 二郎,taro.yamada@example.co.jp,正社員,2021/04/01,S2");
        var result = await _db.ImportCsvAsync(csv, PeopleMapping, Ct);
        var reviews = (await _db.Store.ListProposalsAsync(_db.HrUser("hr2"), result.BatchId, Ct))
            .Where(p => p.Operation == ProposalOperation.Review).OrderBy(p => p.Basis.RowNumbers[0]).ToList();
        Assert.Equal(2, reviews.Count);
        Assert.Equal(["2020-04-01", "regular"], new[] { reviews[0].Payload[ProposalFields.HireDate], reviews[0].Payload[ProposalFields.EmploymentType] });

        var selected = await _db.Resolution.ResolveAsync(Resolver, reviews[0].Id, new ResolutionRequest(ResolutionChoice.SelectCandidate, _db.Id("takahashi")), Ct);
        var versions = await Task.WhenAll(selected.DerivedProposalIds.Select(id => _db.Store.FindProposalAsync(_db.HrUser("hr2"), id, Ct)));
        var version = Assert.Single(versions, p => p!.TargetKind == ProposalTargetKind.PersonVersion)!;
        Assert.Equal(("一郎", "一郎太"), (version.Diff[ProposalFields.GivenName].Old, version.Diff[ProposalFields.GivenName].New));

        var registered = await _db.Resolution.ResolveAsync(Resolver, reviews[1].Id, new ResolutionRequest(ResolutionChoice.RegisterNew), Ct);
        var created = (await _db.Store.FindProposalAsync(_db.HrUser("hr2"), Assert.Single(registered.DerivedProposalIds), Ct))!;
        Assert.Equal((ProposalTargetKind.Person, ProposalOperation.Create, "E0101"), (created.TargetKind, created.Operation, created.Payload[ProposalFields.EmployeeNumber]));
        Assert.Equal(created.TargetId!.Value.ToString("D"), created.Payload[ProposalFields.PersonId]);
        Assert.Equal(new DateOnly(2021, 4, 1), created.EffectiveDate);
    }

    [Fact(DisplayName = "解決: 段飛ばしの可能性のある組織は上位組織の候補を要確認にし、選んだ上位組織への訂正を提案する")]
    public async Task OrgParentAmbiguous_SelectAlternative()
    {
        var mapping = """
            {
              "name": "org-chart",
              "target": "org_units",
              "source": { "hierarchy": { "entityColumn": "会社", "columns": ["本部", "部・室", "課・グループ", "チーム"], "noCarrySuffixes": ["室"], "skipCandidateColumns": ["チーム"] } },
              "legalEntityAliases": { "A": ["テスト株式会社", "(株)テスト"] },
              "fields": { "legal_entity_code": ["会社"], "org_code": ["組織コード"] }
            }
            """;
        var csv = string.Join(
            "\n",
            "会社,本部,部・室,課・グループ,チーム,組織コード",
            "(株)テスト,第一本部,,,,D1",
            ",,第一営業部,,,DP1",
            ",,,営業一課,,S1",
            ",,,,新規チーム,T1");
        var result = await _db.ImportCsvAsync(csv, mapping, Ct);
        var proposals = await _db.Store.ListProposalsAsync(_db.HrUser("hr2"), result.BatchId, Ct);
        var create = Assert.Single(proposals, p => p.TargetKind == ProposalTargetKind.OrgUnit);
        var supplement = Assert.Single(proposals, p => p.Basis.Code == SupplementCodes.OrgParentAmbiguous);
        Assert.Equal((create.TargetId, ProposalStatus.NeedsReview), (supplement.TargetId, supplement.Status));
        Assert.Equal([_db.Id("S1"), _db.Id("DP1")], supplement.Basis.OrgCandidates);
        Assert.Equal(_db.Id("S1").ToString("D"), create.Payload[ProposalFields.ParentOrgUnitId]);

        // 補いのままの上位組織を選べば、変える提案は作らない（解決だけを記録する）。ここでは段飛ばし（部の直下）を選ぶ
        var resolved = await _db.Resolution.ResolveAsync(Resolver, supplement.Id, new ResolutionRequest(ResolutionChoice.SelectCandidate, OrgUnitId: _db.Id("DP1")), Ct);
        var correction = (await _db.Store.FindProposalAsync(_db.HrUser("hr2"), Assert.Single(resolved.DerivedProposalIds), Ct))!;
        Assert.Equal((ProposalTargetKind.OrgUnitVersion, ProposalOperation.Correct), (correction.TargetKind, correction.Operation));
        Assert.Equal((_db.Id("S1").ToString("D"), _db.Id("DP1").ToString("D")), (correction.Diff[ProposalFields.ParentOrgUnitId].Old, correction.Diff[ProposalFields.ParentOrgUnitId].New));
        Assert.Equal([create.Id], correction.Basis.DependsOn);

        await _db.Review.ApproveAsync(_db.HrUser("hr2"), create.Id, Ct);
        await _db.Review.ApproveAsync(_db.HrUser("hr2"), correction.Id, Ct);
        var snapshot = await _db.Snapshots.LoadAsync(_db.HrUser("hr2"), Today, Today, Ct);
        Assert.Equal(_db.Id("DP1"), snapshot.FindUnit(create.TargetId!.Value)!.ParentId);
    }

    [Fact(DisplayName = "DB: 解決者は解決の結果を承認できず（CHECK）、解決の結果の提案は解決済みの元の提案からしか作れない（トリガー）")]
    public async Task Database_GuardsResolution()
    {
        var review = await ImportReviewAsync($"{Today.AddDays(-1):yyyy/MM/dd},,鈴木 次郎,営業一課に配置換えを命ずる");

        // 要確認のままの提案を元にした「解決の結果」は作れない
        var insert = """
            INSERT INTO change_proposal (id, import_batch_id, target_kind, operation, target_id, payload, confidence, status, proposed_by_person_id,
                                         resolved_from_proposal_id, resolved_by_person_id)
            SELECT $1, import_batch_id, 'personnel_action', 'create', $2, '{}', 1, 'proposed', proposed_by_person_id, id, $3 FROM change_proposal WHERE id = $4
            """;
        var forged = await Assert.ThrowsAsync<PostgresException>(() => _db.ExecuteAsync(Resolver, insert, Ct, EntityId.New(), _db.Id("member"), _db.Id("head"), review.Id));
        Assert.Equal("change_proposal_resolved_source", forged.ConstraintName);

        // 他人の名前で解決者を記録できない（RLS）
        var result = await _db.Resolution.ResolveAsync(Resolver, review.Id, new ResolutionRequest(ResolutionChoice.SelectCandidate, _db.Id("member")), Ct);
        await Assert.ThrowsAsync<PostgresException>(() => _db.ExecuteAsync(_db.HrUser("hr2"), insert, Ct, EntityId.New(), _db.Id("member"), _db.Id("head"), review.Id));

        // 解決者を承認者として記録できない（CHECK）
        var derived = Assert.Single(result.DerivedProposalIds);
        var selfApproval = await Assert.ThrowsAsync<PostgresException>(() => _db.ExecuteAsync(
            Resolver, "UPDATE change_proposal SET status = 'rejected', decided_by_person_id = $2, decided_at = now() WHERE id = $1", Ct, derived, _db.Id("head")));
        Assert.Equal("change_proposal_approver_not_resolver", selfApproval.ConstraintName);

        // 作業領域の行は項目を消すことしかできない（値の書き換え・保持期限の延長は不可）
        var rewrite = await Assert.ThrowsAsync<PostgresException>(() => _db.ExecuteAsync(
            _db.HrUser("hr2"), "UPDATE staging_row SET data = data || '{\"full_name\": \"別人\"}' WHERE import_batch_id = $1", Ct, review.BatchId));
        Assert.Equal("staging_row_scrub_only", rewrite.ConstraintName);
    }

    private async Task<ChangeProposalRecord> ImportReviewAsync(string line)
    {
        var result = await _db.ImportCsvAsync("発令日,社員番号,氏名,発令内容\n" + line, ActionMapping, Ct);
        var proposals = await _db.Store.ListProposalsAsync(_db.HrUser("hr2"), result.BatchId, Ct);
        return Assert.Single(proposals, p => p.Status == ProposalStatus.NeedsReview);
    }
}
