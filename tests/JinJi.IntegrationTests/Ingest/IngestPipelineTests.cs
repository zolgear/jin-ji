using JinJi.Application;
using JinJi.Application.Ingest;
using JinJi.Domain;
using JinJi.Domain.Organization;
using JinJi.Ingest;
using JinJi.Policy;
using Npgsql;

namespace JinJi.IntegrationTests.Ingest;

/// <summary>
/// 取込のパイプライン（staging → 正規化 → 名寄せ → 提案 → 承認 → 反映）を実 PostgreSQL で確かめる。テストごとに一時データベースを作る。
/// </summary>
public sealed class IngestPipelineTests : IAsyncLifetime
{
    private const string PeopleMapping = """
        {
          "name": "people-csv",
          "target": "people",
          "legalEntityCode": "A",
          "employeeNumber": { "padLength": null },
          "fields": {
            "employee_number": ["社員番号"],
            "full_name": ["氏名"],
            "full_name_kana": ["フリガナ"],
            "work_email": ["社用メール"],
            "extension_number": ["内線"],
            "employment_type": ["雇用区分"],
            "hire_date": ["入社日"],
            "org_code": ["所属コード"],
            "position_title": ["役職"]
          },
          "ignore": ["備考"],
          "defaults": { "employment_type": "正社員" }
        }
        """;

    private const string OrgMapping = """
        {
          "name": "org-csv",
          "target": "org_units",
          "legalEntityCode": "A",
          "fields": {
            "org_code": ["組織コード"],
            "org_name": ["組織名"],
            "org_level": ["段"],
            "parent_org_code": ["上位組織コード"],
            "org_head_employee_number": ["組織長"]
          }
        }
        """;

    private IngestDatabase _db = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => _db = await IngestDatabase.CreateAsync(Ct);

    public ValueTask DisposeAsync() => _db.DisposeAsync();

    [Fact(DisplayName = "保持禁止の列・マッピングにない列の値は作業領域のどこにも残らず、取込結果には列名と件数だけが残る")]
    public async Task ForbiddenColumns_NeverReachTheDatabase()
    {
        var csv = """
            社員番号,氏名,生年月日,性別,現住所,基本給,社用メール,備考,趣味
            E0002,髙橋 一郎,1980/01/02,男,東京都千代田区丸の内9-9-9,987654,ichiro.takahashi@example.co.jp,特記なし,釣り
            E0100,佐藤 花子,1991/12/31,女,大阪府大阪市北区8-8-8,876543,hanako@example.co.jp,,
            """;

        var result = await _db.ImportCsvAsync(csv, PeopleMapping, Ct);

        Assert.Equal(
            [("生年月日", 2, DroppedColumnKind.Forbidden), ("性別", 2, DroppedColumnKind.Forbidden), ("現住所", 2, DroppedColumnKind.Forbidden),
             ("基本給", 2, DroppedColumnKind.Forbidden), ("備考", 1, DroppedColumnKind.Ignored), ("趣味", 1, DroppedColumnKind.Unmapped)],
            result.DroppedColumns.Select(d => (d.Name, d.Count, d.Kind)));

        // DB を直接検索: 作業領域のすべての jsonb・文字列の列に、捨てた列の値が無い
        var everything = string.Join('\n', await _db.QueryAsync<string>(
            null,
            """
            SELECT data::text FROM staging_row
            UNION ALL SELECT data::text || issues::text FROM normalized_row
            UNION ALL SELECT payload::text || diff::text || basis::text FROM change_proposal
            UNION ALL SELECT message FROM correction_request
            UNION ALL SELECT dropped_columns::text || file_name FROM import_batch
            """,
            Ct));
        foreach (var value in new[] { "1980", "1991", "男", "女", "千代田", "大阪", "987654", "876543", "特記なし", "釣り" })
        {
            Assert.DoesNotContain(value, everything, StringComparison.Ordinal);
        }

        Assert.Contains("生年月日", everything, StringComparison.Ordinal);
        var keys = await _db.QueryAsync<string>(null, "SELECT DISTINCT jsonb_object_keys(data) FROM staging_row UNION SELECT DISTINCT jsonb_object_keys(data) FROM normalized_row", Ct);
        Assert.All(keys, k => Assert.Contains(k, IngestFields.All));
        Assert.Equal([2L], await _db.QueryAsync<long>(null, "SELECT count(*) FROM staging_row WHERE import_batch_id = $1", Ct, result.BatchId));
    }

    [Theory(DisplayName = "DB: staging の jsonb は許可リストのキー・文字列の値だけ（保持禁止の項目名・未知のキー・入れ子を拒否する）")]
    [InlineData("""{"birth_date": "1980-01-02"}""")]
    [InlineData("""{"gender": "M"}""")]
    [InlineData("""{"home_address": "x"}""")]
    [InlineData("""{"salary": "1"}""")]
    [InlineData("""{"nickname": "x"}""")]
    [InlineData("""{"employee_number": {"birth_date": "1980-01-02"}}""")]
    [InlineData("""{"employee_number": 123}""")]
    [InlineData("""["employee_number"]""")]
    public async Task Database_RejectsKeysOutsideAllowlist(string data)
    {
        var batch = await InsertBatchAsync();

        foreach (var table in new[] { "staging_row", "normalized_row" })
        {
            var ex = await Assert.ThrowsAsync<PostgresException>(() => table == "staging_row"
                ? _db.ExecuteAsync(_db.HrUser("hr2"), "INSERT INTO staging_row (id, import_batch_id, source_row_number, row_sha256, data, expires_at) VALUES ($1, $2, 1, repeat('a', 64), $3::jsonb, now() + interval '1 day')", Ct, EntityId.New(), batch, data)
                : InsertNormalizedAsync(batch, data));
            Assert.Equal(PostgresErrorCodes.CheckViolation, ex.SqlState);
            Assert.Equal($"{table}_data_allowed", ex.ConstraintName);
        }
    }

    [Fact(DisplayName = "DB: 許可リスト（取込項目・提案の列）は C# の定義と一致し、保持禁止の名前を含まない")]
    public async Task Allowlists_MatchCode()
    {
        var ingest = await _db.QueryAsync<string[]>(null, "SELECT jinji_ingest_field_keys()", Ct);
        var proposal = await _db.QueryAsync<string[]>(null, "SELECT jinji_proposal_field_keys()", Ct);

        Assert.Equal(IngestFields.All.Order(StringComparer.Ordinal), ingest.Single().Order(StringComparer.Ordinal));
        Assert.Equal(ProposalFields.All.Order(StringComparer.Ordinal), proposal.Single().Order(StringComparer.Ordinal));
        Assert.Equal([false], await _db.QueryAsync<bool>(null, "SELECT bool_or(jinji_ingest_forbidden_key(k)) FROM unnest(jinji_ingest_field_keys() || jinji_proposal_field_keys()) k", Ct));
        Assert.Equal([true], await _db.QueryAsync<bool>(null, "SELECT bool_and(jinji_ingest_forbidden_key(k)) FROM unnest(ARRAY['birth_date', 'age', 'gender', 'sex', 'home_address', 'salary_grade', 'my_number', 'nationality', 'health_status', 'legal_name']) k", Ct));
    }

    [Fact(DisplayName = "訂正: 承認すると旧行の記録期間を閉じ、同じ有効期間の新しい行を追加する。実行者・ワーカーは承認できない")]
    public async Task Approve_Correction_IsBitemporal()
    {
        var result = await _db.ImportCsvAsync("社員番号,氏名,内線\nE0002,髙橋 一郎,1234\n", PeopleMapping, Ct);
        var proposal = Assert.Single(await _db.Store.ListProposalsAsync(_db.HrUser("hr2"), result.BatchId, Ct), p => p.Status == ProposalStatus.Proposed);
        Assert.Equal(ProposalTargetKind.PersonVersion, proposal.TargetKind);
        Assert.Equal(ProposalOperation.Correct, proposal.Operation);

        // 取込の実行者（人事管理者でも）・ワーカー・AI エージェントは承認できない
        await Assert.ThrowsAsync<AccessDeniedException>(() => _db.Review.ApproveAsync(_db.HrUser("hr1"), proposal.Id, Ct));
        await Assert.ThrowsAsync<AccessDeniedException>(() => _db.Review.ApproveAsync(IngestDatabase.Worker, proposal.Id, Ct));
        await Assert.ThrowsAsync<AccessDeniedException>(() => _db.Review.ApproveAsync(
            ViewerContext.ForAgent("assistant", _db.Id("hr2"), [Roles.HrAdmin], Channel.Mcp, FieldLimit.Unrestricted), proposal.Id, Ct));
        await Assert.ThrowsAsync<AccessDeniedException>(() => _db.Review.ApproveAsync(_db.User("member"), proposal.Id, Ct));
        Assert.Equal(0, _db.Notifier.Count);

        await _db.Review.ApproveAsync(_db.HrUser("hr2"), proposal.Id, Ct);

        var rows = await VersionRowsAsync(_db.Id("takahashi"));
        Assert.Equal(2, rows.Count);
        var (closed, open) = (rows.Single(r => !r.Open), rows.Single(r => r.Open));
        Assert.Null(closed.Extension);
        Assert.Equal("1234", open.Extension);
        Assert.Equal(closed.Valid, open.Valid);
        Assert.Equal(1, _db.Notifier.Count);

        var stored = await _db.Store.FindProposalAsync(_db.HrUser("hr2"), proposal.Id, Ct);
        Assert.Equal(ProposalStatus.Applied, stored!.Status);
        Assert.Equal(_db.Id("hr2"), stored.DecidedByPersonId);

        // 二度目の承認はできない
        await Assert.ThrowsAsync<InvalidOperationException>(() => _db.Review.ApproveAsync(_db.HrUser("hr2"), proposal.Id, Ct));

        var audit = await _db.QueryAsync<string>(null, "SELECT action || ':' || channel || ':' || outcome FROM audit_event ORDER BY occurred_at, id", Ct);
        Assert.Equal(
            ["ingest.import:worker:success", "ingest.proposal.approve:ui:denied", "ingest.proposal.approve:worker:denied", "ingest.proposal.approve:mcp:denied",
             "ingest.proposal.approve:ui:denied", "ingest.proposal.approve:ui:success", "ingest.proposal.approve:ui:failed"],
            audit);
    }

    [Fact(DisplayName = "変更（有効日あり）: 版を有効日で分け、異動は発令と所属の版に反映する")]
    public async Task Approve_ChangeWithEffectiveDate_SplitsVersions()
    {
        var date = IngestDatabase.Today.AddDays(10);
        var result = await _db.ImportCsvAsync("社員番号,氏名,所属コード,役職\nE0002,高橋 一郎,S2,主任\n", PeopleMapping, Ct, date);
        var proposals = (await _db.Store.ListProposalsAsync(_db.HrUser("hr2"), result.BatchId, Ct)).Where(p => p.Status == ProposalStatus.Proposed).ToList();
        Assert.Equal([ProposalTargetKind.PersonVersion, ProposalTargetKind.Assignment], proposals.Select(p => p.TargetKind).Order());
        Assert.All(proposals, p => Assert.Equal(ProposalOperation.Change, p.Operation));

        foreach (var proposal in proposals)
        {
            await _db.Review.ApproveAsync(_db.HrUser("hr2"), proposal.Id, Ct);
        }

        var versions = (await VersionRowsAsync(_db.Id("takahashi"))).Where(r => r.Open).OrderBy(r => r.Valid).ToList();
        Assert.Equal([("髙橋", $"[{IngestDatabase.LongAgo:yyyy-MM-dd},{date:yyyy-MM-dd})"), ("高橋", $"[{date:yyyy-MM-dd},)")], versions.Select(v => (v.Family, v.Valid)));
        Assert.Equal(["高橋一郎"], await _db.QueryAsync<string>(null, "SELECT search_name FROM person_version WHERE person_id = $1 AND upper_inf(recorded_period) AND upper_inf(valid_period)", Ct, _db.Id("takahashi")));

        var assignments = await _db.QueryAsync<string>(
            null,
            "SELECT o.code || ':' || coalesce(a.position_title, '-') || ':' || a.valid_period::text || ':' || (a.supersedes_id IS NOT NULL)::text FROM assignment a JOIN org_unit_version o ON o.org_unit_id = a.org_unit_id AND upper_inf(o.recorded_period) WHERE a.person_id = $1 AND upper_inf(a.recorded_period) ORDER BY lower(a.valid_period)",
            Ct,
            _db.Id("takahashi"));
        Assert.Equal([$"S1:-:[{IngestDatabase.LongAgo:yyyy-MM-dd},{date:yyyy-MM-dd}):true", $"S2:主任:[{date:yyyy-MM-dd},):true"], assignments);
        Assert.Equal(
            [$"transfer:{date:yyyy-MM-dd}"],
            await _db.QueryAsync<string>(null, "SELECT action_type || ':' || effective_date::text FROM personnel_action WHERE person_id = $1", Ct, _db.Id("takahashi")));

        // 一般の閲覧者には、有効日前の異動（内示）は見えない
        Assert.Empty(await _db.QueryWithAsync<string>(_db.User("member"), "SELECT action_type FROM personnel_action WHERE person_id = $1", Ct, _db.Id("takahashi")));
    }

    [Fact(DisplayName = "新規の人: 承認で人・人の版・主務の所属・入社の発令を作る。同姓同名の既存の人とは結合しない")]
    public async Task Approve_CreatePerson()
    {
        var result = await _db.ImportCsvAsync(
            "社員番号,氏名,フリガナ,社用メール,入社日,所属コード\nE0200,鈴木 次郎,すずき じろう,jiro2@example.co.jp,R8.4.1,S2\n",
            PeopleMapping,
            Ct);
        var create = Assert.Single(await _db.Store.ListProposalsAsync(_db.HrUser("hr2"), result.BatchId, Ct), p => p.Operation == ProposalOperation.Create);
        Assert.Equal([_db.Id("member")], create.Basis.Candidates.Select(c => c.PersonId));

        await _db.Review.ApproveAsync(_db.HrUser("hr2"), create.Id, Ct);

        var snapshot = await _db.Snapshots.LoadAsync(_db.HrUser("hr2"), IngestDatabase.Today, IngestDatabase.Today, Ct);
        var person = snapshot.FindPerson(create.TargetId!.Value);
        Assert.NotNull(person);
        Assert.Equal("E0200", person.EmployeeNumber);
        Assert.Equal("スズキ", person.Current.FamilyNameKana);
        Assert.Equal(new DateOnly(2026, 4, 1), person.Current.HireDate);
        Assert.Equal(_db.Id("S2"), Assert.Single(snapshot.AssignmentsOf(person.Id)).OrgUnitId);
        Assert.Equal(PersonnelActionType.Hire, Assert.Single(person.Actions).ActionType);
        Assert.NotNull(snapshot.FindPerson(_db.Id("member")));
    }

    [Fact(DisplayName = "組織: 新規の組織は段の検査を通して反映する。子の段が親より上位になる組織は拒否する")]
    public async Task Approve_OrgUnits_ChecksLevelOrder()
    {
        var result = await _db.ImportCsvAsync(
            """
            組織コード,組織名,段,上位組織コード,組織長
            S3,営業三課,課,DP1,E0001
            BAD,誤った部,department,S1,
            U1,第一係,unit,S3,
            DP1,第１営業部,部,D1,E0001
            """,
            OrgMapping,
            Ct);
        var proposals = await _db.Store.ListProposalsAsync(_db.HrUser("hr2"), result.BatchId, Ct);
        var s3 = proposals.Single(p => p.Operation == ProposalOperation.Create && p.Payload[ProposalFields.Code] == "S3");
        var bad = proposals.Single(p => p.Operation == ProposalOperation.Create && p.Payload[ProposalFields.Code] == "BAD");
        var u1 = proposals.Single(p => p.Operation == ProposalOperation.Create && p.Payload[ProposalFields.Code] == "U1");
        Assert.Equal([s3.Id], u1.Basis.DependsOn);

        // 名称の表記ゆれ（第一 / 第１）だけでも、組織名は差分として提案する
        var rename = proposals.Single(p => p.TargetKind == ProposalTargetKind.OrgUnitVersion && p.Operation == ProposalOperation.Correct);
        Assert.Equal(new FieldChange("第一営業部", "第1営業部"), rename.Diff[ProposalFields.Name]);

        // 補完提案: 組織長の無い新規の組織（BAD・U1）
        Assert.Equal(
            new[] { bad.TargetId, u1.TargetId }.Order(),
            proposals.Where(p => p.Operation == ProposalOperation.Supplement).Select(p => p.TargetId).Order());

        // 上位組織が未承認のうちは反映できない
        await Assert.ThrowsAsync<ProposalConflictException>(() => _db.Review.ApproveAsync(_db.HrUser("hr2"), u1.Id, Ct));

        await _db.Review.ApproveAsync(_db.HrUser("hr2"), s3.Id, Ct);
        await _db.Review.ApproveAsync(_db.HrUser("hr2"), u1.Id, Ct);
        var ex = await Assert.ThrowsAsync<OrgLevelValidationException>(() => _db.Review.ApproveAsync(_db.HrUser("hr2"), bad.Id, Ct));
        Assert.Contains("BAD", ex.Message, StringComparison.Ordinal);

        var snapshot = await _db.Snapshots.LoadAsync(_db.HrUser("hr2"), IngestDatabase.Today, IngestDatabase.Today, Ct);
        Assert.Empty(snapshot.LevelOrderViolations());
        var created = snapshot.FindUnit(s3.TargetId!.Value)!;
        Assert.Equal(("section", _db.Id("DP1"), _db.Id("head")), (created.LevelKey, created.ParentId, created.HeadPersonId));
        Assert.Equal(s3.TargetId, snapshot.FindUnit(u1.TargetId!.Value)!.ParentId);
        Assert.Null(snapshot.FindUnit(bad.TargetId!.Value));
        Assert.Equal(
            [$"{_db.Id("D1"):N}.{_db.Id("DP1"):N}.{s3.TargetId:N}.{u1.TargetId:N}"],
            await _db.QueryAsync<string>(null, "SELECT path::text FROM org_unit_version WHERE org_unit_id = $1", Ct, u1.TargetId!.Value));
        Assert.Equal(ProposalStatus.Proposed, (await _db.Store.FindProposalAsync(_db.HrUser("hr2"), bad.Id, Ct))!.Status);
    }

    [Fact(DisplayName = "提案の作成後に対象のデータが変わっていたら反映しない")]
    public async Task Approve_StaleProposal_IsRejected()
    {
        var first = await _db.ImportCsvAsync("社員番号,内線\nE0002,1111\n", PeopleMapping, Ct);
        var second = await _db.ImportCsvAsync("社員番号,内線\nE0002,2222\n", PeopleMapping, Ct);
        var p1 = Assert.Single(await _db.Store.ListProposalsAsync(_db.HrUser("hr2"), first.BatchId, Ct));
        var p2 = Assert.Single(await _db.Store.ListProposalsAsync(_db.HrUser("hr2"), second.BatchId, Ct));

        await _db.Review.ApproveAsync(_db.HrUser("hr2"), p1.Id, Ct);
        await Assert.ThrowsAsync<ProposalConflictException>(() => _db.Review.ApproveAsync(_db.HrUser("hr2"), p2.Id, Ct));

        Assert.Equal(["1111"], (await VersionRowsAsync(_db.Id("takahashi"))).Where(r => r.Open).Select(r => r.Extension));
        Assert.Equal(ProposalStatus.Proposed, (await _db.Store.FindProposalAsync(_db.HrUser("hr2"), p2.Id, Ct))!.Status);
    }

    [Fact(DisplayName = "要確認の提案は承認できず、却下だけできる。却下も実行者はできない")]
    public async Task NeedsReview_CanOnlyBeRejected()
    {
        var result = await _db.ImportCsvAsync("社員番号,社用メール\nE0999,ichiro.takahashi@example.co.jp\n", PeopleMapping, Ct);
        var proposals = await _db.Store.ListProposalsAsync(_db.HrUser("hr2"), result.BatchId, Ct);
        var review = Assert.Single(proposals, p => p.Operation == ProposalOperation.Review);
        Assert.Equal(ProposalStatus.NeedsReview, review.Status);
        Assert.Contains(await _db.Store.ListCorrectionsAsync(_db.HrUser("hr2"), result.BatchId, Ct), c => c.Issue == CorrectionIssue.ConflictingIdentity);

        await Assert.ThrowsAsync<InvalidOperationException>(() => _db.Review.ApproveAsync(_db.HrUser("hr2"), review.Id, Ct));
        await Assert.ThrowsAsync<AccessDeniedException>(() => _db.Review.RejectAsync(_db.HrUser("hr1"), review.Id, Ct));
        await _db.Review.RejectAsync(_db.HrUser("hr2"), review.Id, Ct);

        Assert.Equal(ProposalStatus.Rejected, (await _db.Store.FindProposalAsync(_db.HrUser("hr2"), review.Id, Ct))!.Status);
    }

    [Fact(DisplayName = "DB: 承認者＝取込の実行者は CHECK 制約でも拒否し、承認者は閲覧者本人としてしか記録できない")]
    public async Task Database_EnforcesSegregationOfDuties()
    {
        var result = await _db.ImportCsvAsync("社員番号,内線\nE0002,1234\n", PeopleMapping, Ct);
        var proposal = Assert.Single(await _db.Store.ListProposalsAsync(_db.HrUser("hr2"), result.BatchId, Ct));

        var ex = await Assert.ThrowsAsync<PostgresException>(() => _db.ExecuteAsync(
            _db.HrUser("hr1"),
            "UPDATE change_proposal SET status = 'applied', decided_by_person_id = $2, decided_at = now(), applied_at = now() WHERE id = $1",
            Ct,
            proposal.Id,
            _db.Id("hr1")));
        Assert.Equal(PostgresErrorCodes.CheckViolation, ex.SqlState);
        Assert.Equal("change_proposal_segregation_of_duties", ex.ConstraintName);

        ex = await Assert.ThrowsAsync<PostgresException>(() => _db.ExecuteAsync(
            _db.HrUser("hr2"),
            "UPDATE change_proposal SET status = 'rejected', decided_by_person_id = $2, decided_at = now() WHERE id = $1",
            Ct,
            proposal.Id,
            _db.Id("head")));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);

        // 反映していない提案に反映日時は書けない
        ex = await Assert.ThrowsAsync<PostgresException>(() => _db.ExecuteAsync(
            _db.HrUser("hr2"),
            "UPDATE change_proposal SET status = 'rejected', decided_by_person_id = $2, decided_at = now(), applied_at = now() WHERE id = $1",
            Ct,
            proposal.Id,
            _db.Id("hr2")));
        Assert.Equal(PostgresErrorCodes.CheckViolation, ex.SqlState);
    }

    [Fact(DisplayName = "RLS: 作業領域は hr_admin とバッチの実行者本人だけが見られ、実行者本人（エージェントを含む）は自分のバッチに書き込める")]
    public async Task Rls_WorkspaceIsVisibleOnlyToHrAdminAndExecutor()
    {
        var result = await _db.ImportCsvAsync("社員番号,氏名,内線,入社日\nE0002,髙橋 一郎,1234,不明\n", PeopleMapping, Ct);
        string[] tables = ["import_batch", "staging_row", "normalized_row", "match_candidate", "change_proposal", "correction_request"];

        var executorWithoutRole = _db.User("hr1");
        var executorAgent = ViewerContext.ForAgent("assistant", _db.Id("hr1"), [], Channel.Mcp, FieldLimit.Unrestricted);
        var otherAgent = ViewerContext.ForAgent("assistant", _db.Id("member"), [], Channel.Mcp, FieldLimit.Unrestricted);
        var service = ViewerContext.ForService("attendance", FieldLimit.Unrestricted);
        foreach (var table in tables)
        {
            var sql = $"SELECT count(*) FROM {table}";
            var all = (await _db.QueryAsync<long>(_db.HrUser("hr2"), sql, Ct)).Single();
            Assert.True(all > 0, table);
            Assert.Equal(all, (await _db.QueryWithAsync<long>(executorWithoutRole, sql, Ct)).Single());
            Assert.Equal(all, (await _db.QueryWithAsync<long>(executorAgent, sql, Ct)).Single());
            Assert.Equal(0, (await _db.QueryWithAsync<long>(_db.User("member"), sql, Ct)).Single());
            Assert.Equal(0, (await _db.QueryWithAsync<long>(otherAgent, sql, Ct)).Single());
            Assert.Equal(0, (await _db.QueryWithAsync<long>(service, sql, Ct)).Single());
        }

        // 公開されない作業領域: 実行者本人（委任元）のエージェントは、承認なしで自分のバッチに書き込める（§7.1）
        var insert = "INSERT INTO correction_request (id, import_batch_id, issue, message) VALUES ($1, $2, 'invalid_value', 'エージェントのメモ')";
        Assert.Equal(1, await _db.ExecuteAsync(executorAgent, insert, Ct, EntityId.New(), result.BatchId));
        var ex = await Assert.ThrowsAsync<PostgresException>(() => _db.ExecuteAsync(otherAgent, insert, Ct, EntityId.New(), result.BatchId));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);

        // 公開側（人の版）と提案の承認は、実行者本人でも hr_admin でなければ書けない
        var proposal = (await _db.Store.ListProposalsAsync(_db.HrUser("hr2"), result.BatchId, Ct)).First(p => p.Status == ProposalStatus.Proposed);
        Assert.Equal(0, await _db.ExecuteAsync(executorAgent, "UPDATE change_proposal SET status = 'rejected', decided_by_person_id = $2, decided_at = now() WHERE id = $1", Ct, proposal.Id, _db.Id("hr1")));
        ex = await Assert.ThrowsAsync<PostgresException>(() => _db.ExecuteAsync(executorAgent, "INSERT INTO legal_entity (id, code, name) VALUES ($1, 'X', 'X')", Ct, EntityId.New()));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
    }

    [Fact(DisplayName = "取込の実行は人事管理者だけ。人・エージェントは自分以外を実行者にできない")]
    public async Task Import_RequiresHrAdmin()
    {
        await Assert.ThrowsAsync<AccessDeniedException>(() => _db.ImportCsvAsync("社員番号\nE0002\n", PeopleMapping, Ct, viewer: _db.User("member")));

        var agent = ViewerContext.ForAgent("assistant", _db.Id("hr1"), [Roles.HrAdmin], Channel.Mcp, FieldLimit.Unrestricted);
        var result = await _db.ImportCsvAsync("社員番号,内線\nE0002,1234\n", PeopleMapping, Ct, viewer: agent);
        var batch = await _db.Store.FindBatchAsync(_db.HrUser("hr2"), result.BatchId, Ct);
        Assert.Equal((_db.Id("hr1"), ActorType.Agent, "assistant"), (batch!.ExecutedByPersonId, batch.ExecutedViaActor, batch.ExecutedViaClientId));
        Assert.All(await _db.Store.ListProposalsAsync(_db.HrUser("hr2"), result.BatchId, Ct), p => Assert.Equal("assistant", p.ProposedViaClientId));

        // 委任元は自分のバッチの提案を承認できない（エージェントの起案は委任元の起案として扱う。§7.1）
        var proposal = Assert.Single(await _db.Store.ListProposalsAsync(_db.HrUser("hr2"), result.BatchId, Ct));
        await Assert.ThrowsAsync<AccessDeniedException>(() => _db.Review.ApproveAsync(_db.HrUser("hr1"), proposal.Id, Ct));
    }

    [Fact(DisplayName = "staging の保持期限: 期限を過ぎた行だけを削除し、正規化結果・名寄せ候補も消え、提案は残る")]
    public async Task Purge_RemovesExpiredStagingRows()
    {
        var expired = await _db.ImportCsvAsync("社員番号,内線\nE0002,1234\n", PeopleMapping, Ct);
        var kept = await _db.ImportCsvAsync("社員番号,内線\nE0005,5678\n", PeopleMapping, Ct);
        await using (var owner = await _db.Database.OpenOwnerAsync(Ct))
        {
            // 期限切れを作るため、所有者で RLS と更新の制限（0008: 項目を消す更新だけ）を一時的に外して日時をずらす（テスト専用）
            await Support.SampleData.ExecuteAsync(owner, "ALTER TABLE staging_row NO FORCE ROW LEVEL SECURITY", Ct);
            await Support.SampleData.ExecuteAsync(owner, "ALTER TABLE staging_row DISABLE TRIGGER staging_row_scrub_guard", Ct);
            await Support.SampleData.ExecuteAsync(owner, "UPDATE staging_row SET created_at = now() - interval '200 days', expires_at = now() - interval '1 day' WHERE import_batch_id = $1", Ct, expired.BatchId);
            await Support.SampleData.ExecuteAsync(owner, "ALTER TABLE staging_row ENABLE TRIGGER staging_row_scrub_guard", Ct);
            await Support.SampleData.ExecuteAsync(owner, "ALTER TABLE staging_row FORCE ROW LEVEL SECURITY", Ct);
        }

        Assert.Equal(0, await _db.Store.PurgeExpiredStagingAsync(_db.User("member"), Ct));
        Assert.Equal(1, await _db.Store.PurgeExpiredStagingAsync(_db.HrUser("hr2"), Ct));

        Assert.Equal([0L, 0L, 0L], await CountsAsync(expired.BatchId));
        Assert.Equal([1L, 1L, 1L], await CountsAsync(kept.BatchId));
        var proposal = Assert.Single(await _db.Store.ListProposalsAsync(_db.HrUser("hr2"), expired.BatchId, Ct));
        Assert.Equal([2], proposal.Basis.RowNumbers);

        Task<List<long>> CountsAsync(Guid batch) => _db.QueryAsync<long>(
            null,
            "SELECT count(*) FROM staging_row WHERE import_batch_id = $1 UNION ALL SELECT count(*) FROM normalized_row WHERE import_batch_id = $1 UNION ALL SELECT count(*) FROM match_candidate WHERE import_batch_id = $1",
            Ct,
            batch);
    }

    private async Task<Guid> InsertBatchAsync()
    {
        var id = EntityId.New();
        await _db.ExecuteAsync(
            _db.HrUser("hr2"),
            """
            INSERT INTO import_batch (id, source_kind, target_kind, file_name, file_sha256, mapping_name, mapping_sha256, executed_by_person_id, executed_via_actor, status)
            VALUES ($1, 'csv', 'people', 'x.csv', repeat('a', 64), 'x', repeat('b', 64), $2, 'user', 'completed')
            """,
            Ct,
            id,
            _db.Id("hr2"));
        return id;
    }

    private async Task<int> InsertNormalizedAsync(Guid batch, string data)
    {
        var staging = EntityId.New();
        await _db.ExecuteAsync(
            _db.HrUser("hr2"),
            "INSERT INTO staging_row (id, import_batch_id, source_row_number, row_sha256, data, expires_at) VALUES ($1, $2, 1, repeat('a', 64), '{}', now() + interval '1 day')",
            Ct,
            staging,
            batch);
        return await _db.ExecuteAsync(_db.HrUser("hr2"), "INSERT INTO normalized_row (staging_row_id, import_batch_id, data) VALUES ($1, $2, $3::jsonb)", Ct, staging, batch, data);
    }

    private async Task<List<(bool Open, string Family, string? Extension, string Valid)>> VersionRowsAsync(Guid person)
    {
        var rows = await _db.QueryAsync<string>(
            null,
            "SELECT json_build_array(upper_inf(recorded_period), family_name, extension_number, valid_period::text)::text FROM person_version WHERE person_id = $1",
            Ct,
            person);
        return [.. rows.Select(r =>
        {
            using var json = System.Text.Json.JsonDocument.Parse(r);
            var a = json.RootElement;
            return (a[0].GetBoolean(), a[1].GetString()!, a[2].ValueKind == System.Text.Json.JsonValueKind.Null ? null : a[2].GetString(), a[3].GetString()!);
        })];
    }
}
