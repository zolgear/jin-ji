using System.Text;
using JinJi.Application;
using JinJi.Application.Corrections;
using JinJi.Application.Ingest;
using JinJi.Domain;
using JinJi.Infrastructure.Database;
using JinJi.IntegrationTests.Ingest;
using JinJi.Policy;
using Npgsql;

namespace JinJi.IntegrationTests.Corrections;

/// <summary>
/// 修正依頼（0015・アーキテクチャ概要 §8「修正依頼の実装の規則」）: 起票・賛同・可視範囲と伏せる列・状態の更新・自動の解消・取り込み直しの置き換え・
/// レポート・件数・RLS を実 PostgreSQL で確かめる。テストごとに一時データベースを作る。
/// </summary>
public sealed class CorrectionRequestTests : IAsyncLifetime
{
    private const string PeopleMapping = """
        {
          "name": "people-csv",
          "target": "people",
          "legalEntityCode": "A",
          "employeeNumber": { "padLength": null },
          "fields": { "employee_number": ["社員番号"], "full_name": ["氏名"], "extension_number": ["内線"], "hire_date": ["入社日"] }
        }
        """;

    private IngestDatabase _db = null!;
    private CorrectionRequestService _service = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>修正担当者（人事管理者ではない）。</summary>
    private ViewerContext Corrector => ViewerContext.ForUser(_db.Id("head"), [Roles.HrCorrector], Channel.Ui, FieldLimit.Unrestricted);

    /// <summary>一般の人 member の委任を受けたエージェント。</summary>
    private ViewerContext MemberAgent => ViewerContext.ForAgent("assistant", _db.Id("member"), [], Channel.Mcp, FieldLimit.Unrestricted);

    public async ValueTask InitializeAsync()
    {
        _db = await IngestDatabase.CreateAsync(Ct);
        _service = Create(new CorrectionOptions());
    }

    public ValueTask DisposeAsync() => _db.DisposeAsync();

    private CorrectionRequestService Create(CorrectionOptions options) =>
        new(new NpgsqlCorrectionStore(_db.DataSource), _db.Snapshots, new NpgsqlPolicyRuleStore(_db.DataSource, TimeProvider.System), _db.Audit, TimeProvider.System, options);

    private Task<CorrectionReportResult> ReportAsync(ViewerContext viewer, string person, string field = PersonFields.ExtensionNumber, string reason = "内線の番号が座席表と違います", string? suggested = null) =>
        _service.ReportAsync(viewer, new CorrectionReportInput(_db.Id(person), field, reason, suggested), Ct);

    private async Task<long> CountAuditAsync(string action, string outcome) =>
        (await _db.QueryAsync<long>(null, "SELECT count(*) FROM audit_event WHERE action = $1 AND outcome = $2", Ct, action, outcome)).Single();

    // ---- 起票と賛同 ---------------------------------------------------------------------------------------------------

    [Fact(DisplayName = "起票: 人が見える人・項目を報告でき、jin-ji の公開データは変わらない。同じ人・項目は賛同になり、自分の重ねての起票は何もしない")]
    public async Task Report_CreatesThenEndorses()
    {
        var created = await ReportAsync(_db.User("member"), "takahashi", suggested: "1234");
        Assert.Equal(CorrectionReportOutcome.Created, created.Outcome);
        var request = created.Request;
        Assert.Equal((CorrectionOrigin.User, CorrectionStatus.Open, CorrectionIssue.ReportedValue), (request.Origin, request.Status, request.Issue));
        Assert.Equal((_db.Id("takahashi"), PersonFields.ExtensionNumber, "1234"), (request.PersonId, request.PersonField, request.SuggestedValue));
        Assert.True(request.ReportedByViewer);
        Assert.Equal(_db.Id("member"), request.ReportedByPersonId);
        Assert.Equal(0, request.Endorsements);
        Assert.Equal(1, await CountAuditAsync(CorrectionRequestService.ReportAction, "success"));

        // 公開側の値は変わらない（人の版は増えない）
        Assert.Equal(1, (await _db.QueryAsync<long>(null, "SELECT count(*) FROM person_version WHERE person_id = $1", Ct, _db.Id("takahashi"))).Single());

        // 同じ人・項目は賛同（エージェントは委任元の名義）。理由・値の提案は賛同者に見せない
        var endorsed = await _service.ReportAsync(
            ViewerContext.ForAgent("assistant", _db.Id("hr1"), [], Channel.Mcp, FieldLimit.Unrestricted),
            new CorrectionReportInput(_db.Id("takahashi"), PersonFields.ExtensionNumber, "内線表と違う", null),
            Ct);
        Assert.Equal(CorrectionReportOutcome.Endorsed, endorsed.Outcome);
        Assert.Equal(request.Id, endorsed.Request.Id);
        Assert.Equal(1, endorsed.Request.Endorsements);
        Assert.True(endorsed.Request.EndorsedByViewer);
        Assert.Null(endorsed.Request.Reason);
        Assert.Null(endorsed.Request.SuggestedValue);
        Assert.Null(endorsed.Request.ReportedByPersonId);

        // 起票者・賛同者の重ねての起票は何もしない
        Assert.Equal(CorrectionReportOutcome.AlreadyReported, (await ReportAsync(_db.User("member"), "takahashi")).Outcome);
        Assert.Equal(CorrectionReportOutcome.AlreadyReported, (await ReportAsync(ViewerContext.ForAgent("other", _db.Id("hr1"), [], Channel.Mcp, FieldLimit.Unrestricted), "takahashi")).Outcome);
        Assert.Equal(1, (await _db.QueryAsync<long>(null, "SELECT count(*) FROM correction_request WHERE person_id = $1", Ct, _db.Id("takahashi"))).Single());
        Assert.Equal(1, (await _db.QueryAsync<long>(null, "SELECT count(*) FROM correction_endorsement", Ct)).Single());

        // 別の項目は別の依頼
        Assert.Equal(CorrectionReportOutcome.Created, (await ReportAsync(_db.User("member"), "takahashi", PersonFields.Site)).Outcome);
    }

    [Fact(DisplayName = "起票: エージェントは委任元の名義（origin agent・クライアント）。社内システムは起票できない")]
    public async Task Report_ByAgentAndService()
    {
        var result = await ReportAsync(MemberAgent, "takahashi", PersonFields.Site, "拠点が移転前のままです");
        Assert.Equal(CorrectionOrigin.Agent, result.Request.Origin);
        Assert.Equal(("assistant", _db.Id("member")), (result.Request.ReportedViaClientId, result.Request.ReportedByPersonId));
        Assert.Contains("AI エージェント", result.Request.Message, StringComparison.Ordinal);

        await Assert.ThrowsAsync<AccessDeniedException>(() => ReportAsync(ViewerContext.ForService("attendance", FieldLimit.Unrestricted), "takahashi"));
        Assert.Equal(1, await CountAuditAsync(CorrectionRequestService.ReportAction, "denied"));
    }

    [Theory(DisplayName = "起票の検査: 項目・理由・値の提案の誤り（保持しない情報の混入を含む）は保存しない")]
    [InlineData("name_history", "旧姓が違う", null)]
    [InlineData("birth_date", "誕生日が違う", null)]
    [InlineData("extension_number", "1990年1月1日生まれの人なので", null)]
    [InlineData("site", "自宅は東京都千代田区丸の内1-1 です", null)]
    [InlineData("site", "拠点が違う", "東京都千代田区丸の内1-1")]
    [InlineData("work_location", "勤務地が違う", null)]
    [InlineData("remote_work", "リモートワークの対象です", "対象")]
    [InlineData("assignments", "所属が違う", "営業二課")]
    [InlineData("extension_number", "内線が違う", "内線は1234")]
    [InlineData("hire_date", "入社日が違う", "2024/04/01")]
    [InlineData("work_email", "メールが違う", "not-an-email")]
    [InlineData("extension_number", " ", null)]
    public async Task Report_RejectsInvalidInput(string field, string reason, string? suggested)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => ReportAsync(_db.User("member"), "takahashi", field, reason, suggested));
        Assert.Equal(0, (await _db.QueryAsync<long>(null, "SELECT count(*) FROM correction_request", Ct)).Single());
    }

    [Fact(DisplayName = "起票の検査: 値の提案は項目の形で受け付け、理由の改行は空白にする。見えない人は見つからない扱い")]
    public async Task Report_AcceptsWellFormedValues()
    {
        var hire = await ReportAsync(_db.User("member"), "takahashi", PersonFields.HireDate, "入社日が\n一日ずれています", "2024-04-01");
        Assert.Equal(("入社日が 一日ずれています", "2024-04-01"), (hire.Request.Reason, hire.Request.SuggestedValue));
        var phone = await ReportAsync(_db.User("member"), "takahashi", PersonFields.WorkMobilePhone, "番号が古い", "090-1234-5678");
        Assert.Equal("090-1234-5678", phone.Request.SuggestedValue);

        await Assert.ThrowsAsync<CorrectionNotFoundException>(() => _service.ReportAsync(
            _db.User("member"), new CorrectionReportInput(EntityId.New(), PersonFields.ExtensionNumber, "違う", null), Ct));
    }

    [Fact(DisplayName = "起票の上限: 1 人あたり 24 時間の件数（起票と賛同）を超えたら拒否する")]
    public async Task Report_EnforcesDailyQuota()
    {
        _service = Create(new CorrectionOptions { MaxReportsPerDay = 2 });
        await ReportAsync(_db.User("member"), "takahashi");
        await ReportAsync(_db.User("member"), "head");
        var ex = await Assert.ThrowsAsync<CorrectionQuotaExceededException>(() => ReportAsync(_db.User("member"), "hr1"));
        Assert.True(ex.RetryAfter > TimeSpan.Zero);

        // 別の人は数が別
        Assert.Equal(CorrectionReportOutcome.Created, (await ReportAsync(_db.User("takahashi"), "hr1")).Outcome);
    }

    // ---- 可視範囲 -------------------------------------------------------------------------------------------------------

    [Fact(DisplayName = "可視範囲: 修正担当者・人事管理者はすべて、それ以外は自分が起票・賛同した依頼と自分についての依頼だけ。起票者は担当者と本人にだけ返す")]
    public async Task Visibility_ByRole()
    {
        var request = (await ReportAsync(_db.User("member"), "takahashi")).Request;

        // 修正担当者（人事管理者ではない）・人事管理者: すべて。起票者も見える
        foreach (var handler in new[] { Corrector, _db.HrUser("hr2"), ViewerContext.ForAgent("assistant", _db.Id("head"), [Roles.HrCorrector], Channel.Mcp, FieldLimit.Unrestricted) })
        {
            var seen = await _service.GetAsync(handler, request.Id, Ct);
            Assert.Equal((_db.Id("member"), "内線の番号が座席表と違います"), (seen.ReportedByPersonId, seen.Reason));
        }

        // 対象の本人: 理由は見えるが起票者は見えない
        var subject = await _service.GetAsync(_db.User("takahashi"), request.Id, Ct);
        Assert.True(subject.AboutViewer);
        Assert.NotNull(subject.Reason);
        Assert.Null(subject.ReportedByPersonId);

        // 関係の無い人・社内システム: 見えない（存在しないのと同じ）
        await Assert.ThrowsAsync<CorrectionNotFoundException>(() => _service.GetAsync(_db.User("hr1"), request.Id, Ct));
        Assert.Empty((await _service.ListAsync(_db.User("hr1"), new CorrectionQuery(), Ct)).Items);
        await Assert.ThrowsAsync<AccessDeniedException>(() => _service.ListAsync(ViewerContext.ForService("x", FieldLimit.Unrestricted), new CorrectionQuery(), Ct));

        // RLS でも同じ線（アプリのバグがあっても越えない）
        Assert.Equal(0, (await _db.QueryWithAsync<long>(_db.User("hr1"), "SELECT count(*) FROM correction_request", Ct)).Single());
        Assert.Equal(1, (await _db.QueryWithAsync<long>(Corrector, "SELECT count(*) FROM correction_request", Ct)).Single());
        Assert.Equal(1, (await _db.QueryWithAsync<long>(_db.User("takahashi"), "SELECT count(*) FROM correction_request", Ct)).Single());
        Assert.Equal(0, (await _db.QueryWithAsync<long>(ViewerContext.ForService("x", FieldLimit.Unrestricted), "SELECT count(*) FROM correction_request", Ct)).Single());
    }

    [Fact(DisplayName = "一覧: 人の要約（見える項目だけ）を添え、ID の順でページを分ける。状態と人で絞り込める")]
    public async Task List_PagesAndFilters()
    {
        foreach (var person in new[] { "takahashi", "head", "hr1" })
        {
            await ReportAsync(_db.User("member"), person);
        }

        var first = await _service.ListWithPeopleAsync(Corrector, new CorrectionQuery { Limit = 2 }, Ct);
        Assert.Equal(2, first.Page.Items.Count);
        Assert.NotNull(first.Page.NextAfter);
        Assert.Equal("E0002", first.People[_db.Id("takahashi")].EmployeeNumber);
        Assert.Equal("営業一課", first.People[_db.Id("takahashi")].PrimaryOrgName);
        var second = await _service.ListAsync(Corrector, new CorrectionQuery { Limit = 2, After = first.Page.NextAfter }, Ct);
        Assert.Single(second.Items);
        Assert.Null(second.NextAfter);

        var forHead = await _service.ListAsync(Corrector, new CorrectionQuery { PersonId = _db.Id("head") }, Ct);
        Assert.Equal(_db.Id("head"), Assert.Single(forHead.Items).PersonId);
        Assert.Empty((await _service.ListAsync(Corrector, new CorrectionQuery { Status = CorrectionStatusFilter.Closed }, Ct)).Items);
    }

    // ---- 状態の更新 -----------------------------------------------------------------------------------------------------

    [Fact(DisplayName = "状態の更新: 修正担当者・人事管理者の人だけが対応済み・対応不要にでき、1 回だけ。エージェント・一般の人は不可")]
    public async Task Close_OnlyHandlersOnce()
    {
        var request = (await ReportAsync(_db.User("member"), "takahashi")).Request;
        var input = new CorrectionCloseInput(CorrectionStatus.Resolved, "人事パッケージで直しました");

        await Assert.ThrowsAsync<AccessDeniedException>(() => _service.CloseAsync(_db.User("member"), request.Id, input, Ct));
        await Assert.ThrowsAsync<AccessDeniedException>(() => _service.CloseAsync(
            ViewerContext.ForAgent("assistant", _db.Id("head"), [Roles.HrCorrector], Channel.Mcp, FieldLimit.Unrestricted), request.Id, input, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => _service.CloseAsync(Corrector, request.Id, new CorrectionCloseInput(CorrectionStatus.AutoResolved, null), Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => _service.CloseAsync(Corrector, request.Id, new CorrectionCloseInput(CorrectionStatus.Resolved, "本人の携帯 090-1234-5678 に確認"), Ct));

        var closed = await _service.CloseAsync(Corrector, request.Id, input, Ct);
        Assert.Equal((CorrectionStatus.Resolved, CorrectionClosedKind.Person, _db.Id("head")), (closed.Status, closed.ClosedKind, closed.ClosedByPersonId));
        Assert.Equal("人事パッケージで直しました", closed.ClosedNote);
        Assert.NotNull(closed.ClosedAt);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.CloseAsync(_db.HrUser("hr2"), request.Id, new CorrectionCloseInput(CorrectionStatus.Dismissed, null), Ct));

        // 起票者には対応のメモが見え、閉じた人は見えない
        var mine = await _service.GetAsync(_db.User("member"), request.Id, Ct);
        Assert.Equal(("人事パッケージで直しました", (Guid?)null), (mine.ClosedNote, mine.ClosedByPersonId));

        // 閉じた後の同じ人・項目は新しい依頼
        var again = await ReportAsync(_db.User("member"), "takahashi");
        Assert.Equal(CorrectionReportOutcome.Created, again.Outcome);
        Assert.NotEqual(request.Id, again.Request.Id);
    }

    [Fact(DisplayName = "DB の守り: 一般の人・エージェントは状態を変えられず、起票者・経路を偽れず、中身は変えられない")]
    public async Task Database_Guards()
    {
        var request = (await ReportAsync(_db.User("member"), "takahashi")).Request;

        // 状態の更新は修正担当者・人事管理者の人（と jin-ji 自身）だけ（RLS で 0 行）
        const string close = "UPDATE correction_request SET status = 'dismissed', resolved_at = now(), closed_kind = 'person', closed_by_person_id = $2 WHERE id = $1";
        Assert.Equal(0, await _db.ExecuteAsync(_db.User("member"), close, Ct, request.Id, _db.Id("member")));
        Assert.Equal(0, await _db.ExecuteAsync(ViewerContext.ForAgent("a", _db.Id("head"), [Roles.HrCorrector], Channel.Mcp, FieldLimit.Unrestricted), close, Ct, request.Id, _db.Id("head")));

        // 閉じた人は閲覧者本人としてしか記録できない
        var ex = await Assert.ThrowsAsync<PostgresException>(() => _db.ExecuteAsync(Corrector, close, Ct, request.Id, _db.Id("hr2")));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);

        // 中身は変えられない
        ex = await Assert.ThrowsAsync<PostgresException>(() => _db.ExecuteAsync(Corrector, "UPDATE correction_request SET person_id = $2 WHERE id = $1", Ct, request.Id, _db.Id("head")));
        Assert.Contains(ex.SqlState, new[] { PostgresErrorCodes.InsufficientPrivilege, PostgresErrorCodes.IntegrityConstraintViolation });

        // 起票者・経路を偽れない（他人の名義、人の閲覧者が agent として）
        const string insert = """
            INSERT INTO correction_request (id, origin, issue, message, person_id, person_field, explanation, value_fingerprint, reported_by_person_id, reported_via_client_id)
            VALUES ($1, $2, 'reported_value', 'x', $3, 'work_location', '理由', $4, $5, 'c')
            """;
        var fingerprint = new string('0', 64);
        ex = await Assert.ThrowsAsync<PostgresException>(() => _db.ExecuteAsync(_db.User("member"), insert, Ct, EntityId.New(), "user", _db.Id("head"), fingerprint, _db.Id("hr1")));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
        ex = await Assert.ThrowsAsync<PostgresException>(() => _db.ExecuteAsync(_db.User("member"), insert, Ct, EntityId.New(), "agent", _db.Id("head"), fingerprint, _db.Id("member")));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);

        // 保持しない項目の名前・形の不正は CHECK 制約で止める
        ex = await Assert.ThrowsAsync<PostgresException>(() => _db.ExecuteAsync(
            _db.User("member"),
            insert.Replace("'work_location'", "'name_history'", StringComparison.Ordinal),
            Ct,
            EntityId.New(),
            "user",
            _db.Id("head"),
            fingerprint,
            _db.Id("member")));
        Assert.Equal(PostgresErrorCodes.CheckViolation, ex.SqlState);

        // 賛同は本人の名義だけ
        ex = await Assert.ThrowsAsync<PostgresException>(() => _db.ExecuteAsync(
            _db.User("hr1"),
            "INSERT INTO correction_endorsement (id, correction_request_id, endorsed_by_person_id, endorsed_via_actor) VALUES ($1, $2, $3, 'user')",
            Ct,
            EntityId.New(),
            request.Id,
            _db.Id("head")));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
    }

    // ---- 自動の解消・置き換え ---------------------------------------------------------------------------------------------

    [Fact(DisplayName = "自動の解消: 取込の提案の承認で値が変わったら解消になり、変わらない項目の依頼は残る。値が変わらなければ定期の判定でも閉じない")]
    public async Task AutoResolve_AfterApproval()
    {
        var extension = (await ReportAsync(_db.User("member"), "takahashi")).Request;
        var location = (await ReportAsync(_db.User("member"), "takahashi", PersonFields.Site, "拠点が違う")).Request;

        // 値が変わらなければ閉じない（起票した閲覧者と jin-ji 自身で指紋が一致する）
        Assert.Equal(0, await _service.ReconcileAsync(ViewerContext.ForSystem(Channel.Worker), null, Ct));

        var result = await _db.ImportCsvAsync("社員番号,内線\nE0002,1234\n", PeopleMapping, Ct);
        var proposal = Assert.Single(await _db.Store.ListProposalsAsync(_db.HrUser("hr2"), result.BatchId, Ct), p => p.Status == ProposalStatus.Proposed);
        var review = new ProposalReviewService(_db.Store, _db.Snapshots, _db.Audit, TimeProvider.System, _db.Notifier, _service);
        await review.ApproveAsync(_db.HrUser("hr2"), proposal.Id, Ct);

        var resolved = await _service.GetAsync(Corrector, extension.Id, Ct);
        Assert.Equal((CorrectionStatus.AutoResolved, CorrectionClosedKind.Auto, (Guid?)null), (resolved.Status, resolved.ClosedKind, resolved.ClosedByPersonId));
        Assert.Equal(CorrectionStatus.Open, (await _service.GetAsync(Corrector, location.Id, Ct)).Status);
        Assert.Equal(1, await CountAuditAsync(CorrectionRequestService.AutoResolveAction, "success"));
    }

    [Fact(DisplayName = "廃止した項目（0017 より前の勤務地 work_location）の依頼は残り、自動の解消で閉じない（新しい起票はできない）")]
    public async Task RetiredField_IsKeptAndNotAutoResolved()
    {
        var id = EntityId.New();
        await _db.ExecuteAsync(
            _db.User("member"),
            """
            INSERT INTO correction_request (id, origin, issue, message, person_id, person_field, explanation, value_fingerprint, reported_by_person_id)
            VALUES ($1, 'user', 'reported_value', 'x', $2, 'work_location', '勤務地が違う', $3, $4)
            """,
            Ct,
            id,
            _db.Id("takahashi"),
            new string('0', 64),
            _db.Id("member"));

        Assert.Equal(0, await _service.ReconcileAsync(ViewerContext.ForSystem(Channel.Worker), null, Ct));
        var request = await _service.GetAsync(Corrector, id, Ct);
        Assert.Equal(CorrectionStatus.Open, request.Status);
        Assert.Equal("勤務地（廃止。拠点に置き換え）", CorrectionFields.Label(request.PersonField!));
        await Assert.ThrowsAsync<ArgumentException>(() => ReportAsync(_db.User("member"), "takahashi", "work_location", "勤務地が違う"));
    }

    [Fact(DisplayName = "置き換え: 同じ項目マッピングの次の取込で、前の取込の未対応の依頼は置き換えになる（人の報告は対象外）")]
    public async Task Supersede_OnReimport()
    {
        var report = (await ReportAsync(_db.User("member"), "takahashi")).Request;
        var first = await _db.ImportCsvAsync("社員番号,内線\n,1234\n", PeopleMapping, Ct);
        var firstCorrections = await _db.Store.ListCorrectionsAsync(_db.HrUser("hr2"), first.BatchId, Ct);
        Assert.NotEmpty(firstCorrections);

        var second = await _db.ImportCsvAsync("社員番号,内線\n,1234\n", PeopleMapping, Ct);
        var page = await _service.ListAsync(Corrector, new CorrectionQuery { Status = CorrectionStatusFilter.All, Limit = 100 }, Ct);
        Assert.All(page.Items.Where(i => i.BatchId == first.BatchId), i => Assert.Equal(CorrectionStatus.Superseded, i.Status));
        Assert.All(page.Items.Where(i => i.BatchId == second.BatchId), i => Assert.Equal(CorrectionStatus.Open, i.Status));
        Assert.Equal(CorrectionStatus.Open, page.Items.Single(i => i.Id == report.Id).Status);

        // 取込の実行者は自分の取込の依頼を見られ、修正担当者はファイル名と行番号まで見られる
        var ingest = page.Items.First(i => i.BatchId == second.BatchId);
        Assert.Equal(("import.csv", 2), (ingest.FileName, ingest.SourceRowNumber));
        Assert.NotEmpty((await _service.ListAsync(_db.User("hr1"), new CorrectionQuery(), Ct)).Items);
    }

    // ---- 件数・レポート ---------------------------------------------------------------------------------------------------

    [Fact(DisplayName = "件数とレポート: 修正担当者・人事管理者だけ。CSV は UTF-8 BOM・見出し・社員番号と氏名・数式の注入の対策")]
    public async Task SummaryAndExport()
    {
        await ReportAsync(_db.User("member"), "takahashi", reason: "=HYPERLINK(\"x\") 内線が違う");
        await _db.ImportCsvAsync("社員番号,内線\n,1234\n", PeopleMapping, Ct);

        var summary = await _service.SummaryAsync(Corrector, Ct);
        Assert.True(summary.Open >= 2);
        Assert.Equal(summary.Open, summary.New);
        Assert.Equal(0, summary.Stale);
        await Assert.ThrowsAsync<AccessDeniedException>(() => _service.SummaryAsync(_db.User("member"), Ct));

        await Assert.ThrowsAsync<AccessDeniedException>(() => _service.ExportAsync(_db.User("member"), CorrectionStatusFilter.Open, Ct));
        await Assert.ThrowsAsync<AccessDeniedException>(() => _service.ExportAsync(
            ViewerContext.ForAgent("a", _db.Id("head"), [Roles.HrCorrector], Channel.Mcp, FieldLimit.Unrestricted), CorrectionStatusFilter.Open, Ct));

        var export = await _service.ExportAsync(Corrector, CorrectionStatusFilter.Open, Ct);
        var csv = CorrectionReport.ToCsv(export);
        Assert.Equal(Encoding.UTF8.GetPreamble(), csv[..3]);
        var text = Encoding.UTF8.GetString(csv[3..]);
        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(string.Join(',', CorrectionReport.Headers), lines[0]);
        var row = Assert.Single(lines, l => l.Contains("E0002", StringComparison.Ordinal));
        Assert.Contains("髙橋 一郎", row, StringComparison.Ordinal);
        Assert.Contains("\"'=HYPERLINK(\"\"x\"\") 内線が違う\"", row, StringComparison.Ordinal);
        Assert.Contains(lines, l => l.Contains("import.csv", StringComparison.Ordinal));
        Assert.Equal(1, await CountAuditAsync(CorrectionRequestService.ExportAction, "success"));
    }

    [Fact(DisplayName = "管理 CLI: correction-report は CSV（UTF-8 BOM）と xlsx（文字列のセル）を書き、notify-corrections は通知を 1 回実行する")]
    public async Task Cli_ReportAndNotify()
    {
        await ReportAsync(_db.User("member"), "takahashi", reason: "=1+1 の内線が違う");
        var directory = Directory.CreateTempSubdirectory("jinji-corrections-");
        try
        {
            var settings = new[] { $"--ConnectionStrings:App={_db.Database.AppConnectionString}", "--Logging:LogLevel:Default=Warning" };
            var csvPath = Path.Combine(directory.FullName, "report.csv");
            using var output = new StringWriter();
            Assert.Equal(0, await Host.CorrectionCommands.ReportAsync([.. settings, "--output", csvPath], output));
            Assert.Contains("rows: 1", output.ToString(), StringComparison.Ordinal);
            var csv = await File.ReadAllBytesAsync(csvPath, Ct);
            Assert.Equal(Encoding.UTF8.GetPreamble(), csv[..3]);
            Assert.Contains("E0002", Encoding.UTF8.GetString(csv), StringComparison.Ordinal);

            var xlsxPath = Path.Combine(directory.FullName, "report.xlsx");
            Assert.Equal(0, await Host.CorrectionCommands.ReportAsync([.. settings, "--output", xlsxPath, "--status", "all"], TextWriter.Null));
            await using var stream = File.OpenRead(xlsxPath);
            var table = Connectors.XlsxTableReader.Read(stream);
            Assert.Equal(CorrectionReport.Headers, table.Headers);
            var row = Assert.Single(table.Rows);
            Assert.Equal("E0002", row.Cells[2]);
            Assert.Equal("=1+1 の内線が違う", row.Cells[7]);

            Assert.Equal(2, await Host.CorrectionCommands.ReportAsync([.. settings, "--output", csvPath, "--status", "pending"], TextWriter.Null));
            Assert.Equal(2, await Host.CorrectionCommands.ReportAsync([.. settings], TextWriter.Null));

            using var notified = new StringWriter();
            Assert.Equal(0, await Host.CorrectionCommands.NotifyAsync([.. settings], notified));
            Assert.Contains("outcome: disabled", notified.ToString(), StringComparison.Ordinal);
            Assert.Contains("open: 1", notified.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact(DisplayName = "項目の一覧: DB の許可リスト（と廃止した項目）・人の投影の項目（氏名の履歴を除く）と一致する")]
    public async Task Fields_MatchDatabaseAndPolicy()
    {
        var database = (await _db.QueryAsync<string[]>(null, "SELECT jinji_correction_person_fields()", Ct)).Single();
        Assert.Equal(CorrectionFields.All.Concat(CorrectionFields.Retired.Keys).Order(StringComparer.Ordinal), database.Order(StringComparer.Ordinal));
        Assert.Empty(CorrectionFields.Retired.Keys.Intersect(PersonFields.All));
        var declared = (System.Reflection.CustomAttributeExtensions.GetCustomAttributes(typeof(global::JinJi.Api.CreateCorrectionRequest).GetProperty(nameof(global::JinJi.Api.CreateCorrectionRequest.Field))!, typeof(global::JinJi.Api.OpenApiEnumAttribute), true)
            .Cast<global::JinJi.Api.OpenApiEnumAttribute>().Single()).Values;
        Assert.Equal(CorrectionFields.All.Order(StringComparer.Ordinal), declared.Order(StringComparer.Ordinal));
        Assert.Equal(PersonFields.PolicyControllable.Order(StringComparer.Ordinal), CorrectionFields.All.Order(StringComparer.Ordinal));
        Assert.All(CorrectionFields.All, f => Assert.NotEqual(f, CorrectionFields.Label(f)));
    }
}
