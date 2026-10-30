using JinJi.Application.FieldManagement;
using JinJi.Application.Ingest;
using JinJi.Domain;
using JinJi.Domain.Organization;
using JinJi.Infrastructure.Database;
using JinJi.IntegrationTests.Ingest;
using JinJi.Policy;
using Npgsql;

namespace JinJi.IntegrationTests.FieldManagement;

/// <summary>
/// 現場・IT 部門の直接登録（0021・ADR-0013 決定 1・3〜7）: 範囲の中の即時反映、管理元、範囲の外の拒否（アプリと DB）、相手側の同意、
/// 権限を生む変更、人の版の列、AI エージェントの下書き、取り消し、未来日の行の秘匿を実 PostgreSQL で確かめる。
/// </summary>
/// <remarks>
/// 組織（<see cref="IngestDatabase"/>）: 第一本部 D1（長 head）> 第一営業部 DP1（長 head）> 営業一課 S1（長なし）・営業二課 S2（長 head）、
/// 管理本部 D2（長 hr2）> 人事部 HR（長 hr2）。人: head（DP1）・takahashi（S1）・member（S2）・hr1・hr2（HR）。
/// </remarks>
public sealed class FieldRegistrationTests : IAsyncLifetime
{
    private IngestDatabase _db = null!;
    private FieldManagementService _management = null!;
    private FieldRegistrationService _service = null!;
    private NpgsqlFieldRegistrationStore _store = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DateOnly Today => IngestDatabase.Today;

    private ViewerContext OrgAdmin => ViewerContext.ForUser(_db.Id("takahashi"), [Roles.OrgAdmin], Channel.Ui, FieldLimit.Unrestricted);

    private ViewerContext Head => _db.User("head");

    public async ValueTask InitializeAsync()
    {
        _db = await IngestDatabase.CreateAsync(Ct);
        _management = new FieldManagementService(new NpgsqlFieldManagementStore(_db.DataSource), _db.Snapshots, _db.Audit, TimeProvider.System);
        _store = new NpgsqlFieldRegistrationStore(_db.DataSource, TimeProvider.System);
        _service = new FieldRegistrationService(_store, _management, _db.Snapshots, _db.Audit, TimeProvider.System, _db.Notifier);
    }

    public ValueTask DisposeAsync() => _db.DisposeAsync();

    /// <summary>領域の管理元を field にする（組織管理者が起案し、人事管理者が承認する）。</summary>
    private async Task EnableAsync(params FieldDomain[] domains)
    {
        foreach (var domain in domains)
        {
            var setting = await _management.ProposeModeAsync(OrgAdmin, domain, FieldManagementMode.Field, null, Ct);
            await _management.DecideModeAsync(_db.HrUser("hr2"), setting.Id, true, Ct);
        }
    }

    private Task EnableAllAsync() => EnableAsync([.. FieldDomains.All]);

    private FieldRegistrationInput Transfer(string person, string to, DateOnly date, string? title = null)
    {
        var values = new Dictionary<string, string?> { [ProposalFields.OrgUnitId] = _db.Id(to).ToString("D") };
        if (title is not null)
        {
            values[ProposalFields.PositionTitle] = title;
        }

        return new FieldRegistrationInput(date, "来月の体制", [new FieldChangeInput(FieldChangeKind.Assignment, _db.Id(person), values)]);
    }

    /// <summary>その日の主務の組織（人事管理者の見え方）。</summary>
    private async Task<Guid?> PrimaryOrgAsync(string person, DateOnly date) =>
        (await _db.QueryAsync<Guid>(
            null,
            "SELECT org_unit_id FROM assignment WHERE person_id = $1 AND assignment_type = 'primary' AND upper_inf(recorded_period) AND valid_period @> $2::date",
            Ct,
            _db.Id(person),
            date)).Cast<Guid?>().SingleOrDefault();

    /// <summary>閲覧者に見える、その人の主務の所属の行の数（未来日の行の秘匿の確認）。</summary>
    private async Task<long> VisibleFutureAssignmentsAsync(ViewerContext viewer, string person) =>
        (await _db.QueryWithAsync<long>(
            viewer,
            "SELECT count(*) FROM assignment WHERE person_id = $1 AND upper_inf(recorded_period) AND lower(valid_period) > jinji_business_date()",
            Ct,
            _db.Id(person))).Single();

    private async Task<long> CountAuditAsync(string action, string outcome) =>
        (await _db.QueryAsync<long>(null, "SELECT count(*) FROM audit_event WHERE action = $1 AND outcome = $2", Ct, action, outcome)).Single();

    private async Task<FieldManagementError> ErrorOfAsync(Func<Task> action) =>
        (await Assert.ThrowsAsync<FieldManagementException>(action)).Error;

    // ---- 範囲の中の即時反映 -------------------------------------------------------------------------------------------

    [Fact(DisplayName = "登録: 組織長は配下の異動をすぐに反映し、発令・変更提案・監査ログが残る。未来日の行は関係者にだけ見える")]
    public async Task Register_InScopeTransferIsApplied()
    {
        await EnableAllAsync();
        var date = Today.AddDays(7);

        var registration = await _service.RegisterAsync(Head, Transfer("takahashi", "S2", date, "主任"), Ct);

        Assert.Equal(FieldRegistrationStatus.Applied, registration.Status);
        Assert.Equal(_db.Id("S1"), await PrimaryOrgAsync("takahashi", Today));
        Assert.Equal(_db.Id("S2"), await PrimaryOrgAsync("takahashi", date));
        Assert.Equal(1, _db.Notifier.Count);

        var stored = await _service.GetAsync(Head, registration.Id, Ct);
        var proposal = Assert.Single(stored.Proposals);
        Assert.Equal(ProposalStatus.Applied, proposal.Status);
        Assert.Equal(_db.Id("head"), proposal.ProposedByPersonId);
        Assert.Equal("transfer", (await _db.QueryAsync<string>(
            null, "SELECT action_type FROM personnel_action WHERE person_id = $1 AND effective_date = $2", Ct, _db.Id("takahashi"), date)).Single());
        Assert.Equal(1, await CountAuditAsync(FieldRegistrationService.RegisterAction, "success"));

        // 未来日の行: 登録者・組織管理者には見え、対象の本人・他の人には見えない（内示と同じ秘匿）
        Assert.Equal(1, await VisibleFutureAssignmentsAsync(Head, "takahashi"));
        Assert.Equal(1, await VisibleFutureAssignmentsAsync(OrgAdmin, "takahashi"));
        Assert.Equal(0, await VisibleFutureAssignmentsAsync(_db.User("takahashi"), "takahashi"));
        Assert.Equal(0, await VisibleFutureAssignmentsAsync(_db.User("member"), "takahashi"));

        // 一覧: 登録者と、関係する組織の範囲を持つ人に見える。範囲の外の人には見えない
        Assert.Single(await _service.ListAsync(Head, new FieldRegistrationQuery(true, []), Ct));
        Assert.Empty(await _service.ListAsync(_db.User("hr2"), new FieldRegistrationQuery(false, []), Ct));

        // 取込の承認の経路からは直接登録の提案を扱えない
        Assert.Null(await _db.Store.FindProposalAsync(_db.HrUser("hr1"), proposal.Id, Ct));
    }

    [Fact(DisplayName = "登録: 管理元が hr の領域は直接登録できない（アプリと RLS）")]
    public async Task Register_RequiresFieldMode()
    {
        Assert.Equal(FieldManagementError.Forbidden, await ErrorOfAsync(() => _service.RegisterAsync(Head, Transfer("takahashi", "S2", Today), Ct)));

        // DB も、管理元が hr なら範囲の中でも公開側の表に書かせない
        await Assert.ThrowsAsync<PostgresException>(() => InsertAssignmentDirectlyAsync(Head, "takahashi", "S2"));
        await EnableAsync(FieldDomain.Assignment);
        await InsertAssignmentDirectlyAsync(Head, "takahashi", "S2");
        Assert.Equal(1, await CountAuditAsync(FieldRegistrationService.RegisterAction, "denied"));
    }

    [Fact(DisplayName = "登録: 範囲を持たない人・他の本部の異動は拒否する（アプリと RLS）")]
    public async Task Register_RejectsOutOfScope()
    {
        await EnableAllAsync();

        Assert.Equal(FieldManagementError.Forbidden, await ErrorOfAsync(() => _service.RegisterAsync(_db.User("member"), Transfer("takahashi", "S2", Today), Ct)));
        Assert.Equal(FieldManagementError.Forbidden, await ErrorOfAsync(() => _service.RegisterAsync(Head, Transfer("hr1", "D2", Today), Ct)));

        // DB: 範囲の外の組織への所属の行は追加できない
        await Assert.ThrowsAsync<PostgresException>(() => InsertAssignmentDirectlyAsync(Head, "hr1", "HR"));
        await Assert.ThrowsAsync<PostgresException>(() => InsertAssignmentDirectlyAsync(_db.User("member"), "takahashi", "S2"));
        Assert.Equal(_db.Id("HR"), await PrimaryOrgAsync("hr1", Today));
    }

    [Fact(DisplayName = "登録: 有効日は今日以降。遡及は組織管理者・人事管理者だけ")]
    public async Task Register_PastDateOnlyForAdmins()
    {
        await EnableAllAsync();
        Assert.Equal(FieldManagementError.Invalid, await ErrorOfAsync(() => _service.RegisterAsync(Head, Transfer("takahashi", "S2", Today.AddDays(-3)), Ct)));

        var registration = await _service.RegisterAsync(OrgAdmin, Transfer("member", "S1", Today.AddDays(-3)), Ct);
        Assert.Equal(FieldRegistrationStatus.Applied, registration.Status);
        Assert.Equal(_db.Id("S1"), await PrimaryOrgAsync("member", Today));
    }

    // ---- 相手側の同意 -------------------------------------------------------------------------------------------------

    [Fact(DisplayName = "同意: 片側の範囲しか持たない異動は同意待ちになり、相手側の範囲を持つ人の同意で反映する")]
    public async Task Consent_CrossOrgTransfer()
    {
        await EnableAllAsync();
        var date = Today.AddDays(10);

        var registration = await _service.RegisterAsync(Head, Transfer("takahashi", "HR", date), Ct);
        Assert.Equal(FieldRegistrationStatus.PendingConsent, registration.Status);
        Assert.Equal([_db.Id("HR")], registration.ConsentOrgUnitIds);
        Assert.Equal(_db.Id("S1"), await PrimaryOrgAsync("takahashi", date));

        // 登録者自身・範囲を持たない人は同意できない
        Assert.Equal(FieldManagementError.Forbidden, await ErrorOfAsync(() => _service.ConsentAsync(Head, registration.Id, Ct)));
        Assert.Equal(FieldManagementError.NotFound, await ErrorOfAsync(() => _service.ConsentAsync(_db.User("member"), registration.Id, Ct)));

        // 相手側（人事部の長）は自分の範囲の登録として見え、同意すると反映する
        Assert.Single(await _service.ListAsync(_db.User("hr2"), new FieldRegistrationQuery(false, [FieldRegistrationStatus.PendingConsent]), Ct));
        var consented = await _service.ConsentAsync(_db.User("hr2"), registration.Id, Ct);
        Assert.Equal(FieldRegistrationStatus.Applied, consented.Status);
        Assert.Equal(_db.Id("HR"), await PrimaryOrgAsync("takahashi", date));
        Assert.Equal(1, await VisibleFutureAssignmentsAsync(_db.User("hr2"), "takahashi"));
        Assert.Equal(1, await CountAuditAsync(FieldRegistrationService.ConsentAction, "success"));
    }

    // ---- 権限を生む変更・人の版の列 ------------------------------------------------------------------------------------

    [Fact(DisplayName = "権限: 組織長の任免は一つ上の組織の範囲を要する（担当はできない。DB のトリガーも止める）")]
    public async Task Privilege_HeadChangeNeedsParentScope()
    {
        await EnableAllAsync();
        await _management.GrantEditorAsync(Head, _db.Id("S1"), _db.Id("member"), Today, Today.AddDays(30), Ct);

        // 担当（S1 の範囲だけ）は S1 の組織長を任命できない。名称は変えられる
        var appoint = OrgUpdate("S1", ProposalFields.HeadPersonId, _db.Id("member").ToString("D"));
        Assert.Equal(FieldManagementError.Forbidden, await ErrorOfAsync(() => _service.RegisterAsync(_db.User("member"), appoint, Ct)));
        var rename = await _service.RegisterAsync(_db.User("member"), OrgUpdate("S1", ProposalFields.Name, "営業第一課"), Ct);
        Assert.Equal(FieldRegistrationStatus.Applied, rename.Status);

        // 部長（DP1 の長）は配下の S1 の組織長を任命できる
        Assert.Equal(FieldRegistrationStatus.Applied, (await _service.RegisterAsync(Head, appoint, Ct)).Status);

        // アプリの検査を通さずに作った登録でも、DB が一つ上の範囲の無い組織長の変更を拒否する（担当が S1 の組織長を外す）
        var crafted = Crafted(_db.Id("member"), ProposalTargetKind.OrgUnitVersion, _db.Id("S1"),
            new Dictionary<string, FieldChange> { [ProposalFields.HeadPersonId] = new(_db.Id("member").ToString("D"), null) });
        var denied = await ErrorOfAsync(() => _store.SaveAsync(_db.User("member"), crafted, FieldRegistrationStatus.Applied, Today, Ct));
        Assert.Equal(FieldManagementError.Forbidden, denied);
    }

    [Fact(DisplayName = "人の版: 内線・拠点は変えられ、氏名・社用メールは DB が拒否する")]
    public async Task PersonVersion_OnlyFieldColumns()
    {
        await EnableAllAsync();
        var input = new FieldRegistrationInput(Today, null,
        [
            new FieldChangeInput(FieldChangeKind.Person, _db.Id("takahashi"), new Dictionary<string, string?>
            {
                [ProposalFields.ExtensionNumber] = "1234",
                [ProposalFields.RemoteWork] = "true",
            }),
        ]);
        Assert.Equal(FieldRegistrationStatus.Applied, (await _service.RegisterAsync(Head, input, Ct)).Status);
        Assert.Equal("1234", (await _db.QueryAsync<string>(
            null, "SELECT extension_number FROM person_version WHERE person_id = $1 AND upper_inf(recorded_period) AND valid_period @> $2::date", Ct, _db.Id("takahashi"), Today)).Single());

        // アプリは人の版で直せる項目を限る
        var name = new FieldRegistrationInput(Today, null,
            [new FieldChangeInput(FieldChangeKind.Person, _db.Id("takahashi"), new Dictionary<string, string?> { [ProposalFields.WorkEmail] = "x@example.co.jp" })]);
        Assert.Equal(FieldManagementError.Invalid, await ErrorOfAsync(() => _service.RegisterAsync(Head, name, Ct)));

        // DB も、アプリを通さずに作った社用メールの変更を拒否する
        var crafted = Crafted(_db.Id("head"), ProposalTargetKind.PersonVersion, _db.Id("takahashi"),
            new Dictionary<string, FieldChange> { [ProposalFields.WorkEmail] = new("ichiro.takahashi@example.co.jp", "x@example.co.jp") });
        Assert.Equal(FieldManagementError.Forbidden, await ErrorOfAsync(() => _store.SaveAsync(Head, crafted, FieldRegistrationStatus.Applied, Today, Ct)));
    }

    // ---- AI エージェントの下書き・取り消し・新設 -----------------------------------------------------------------------

    [Fact(DisplayName = "下書き: AI エージェントは委任元の名義の下書きまで。委任元の人の確定で反映する")]
    public async Task Draft_AgentNeedsDelegatorConfirmation()
    {
        await EnableAllAsync();
        var agent = ViewerContext.ForAgent("assistant", _db.Id("head"), [], Channel.Mcp, FieldLimit.Unrestricted);

        var draft = await _service.RegisterAsync(agent, Transfer("takahashi", "S2", Today), Ct);
        Assert.Equal(FieldRegistrationStatus.Draft, draft.Status);
        Assert.Equal(_db.Id("S1"), await PrimaryOrgAsync("takahashi", Today));
        Assert.Equal(0, _db.Notifier.Count);

        // エージェント自身・別の人は確定できない
        Assert.Equal(FieldManagementError.Forbidden, await ErrorOfAsync(() => _service.ConfirmAsync(agent, draft.Id, Ct)));
        Assert.Equal(FieldManagementError.Forbidden, await ErrorOfAsync(() => _service.ConfirmAsync(OrgAdmin, draft.Id, Ct)));

        var confirmed = await _service.ConfirmAsync(Head, draft.Id, Ct);
        Assert.Equal(FieldRegistrationStatus.Applied, confirmed.Status);
        Assert.Equal(_db.Id("S2"), await PrimaryOrgAsync("takahashi", Today));

        // 破棄は下書き・同意待ちだけ
        Assert.Equal(FieldManagementError.Conflict, await ErrorOfAsync(() => _service.DiscardAsync(Head, draft.Id, Ct)));
    }

    [Fact(DisplayName = "取り消し: 有効日の前なら取り消せて元に戻る。有効日を迎えた登録は取り消せない")]
    public async Task Withdraw_RestoresBeforeEffectiveDate()
    {
        await EnableAllAsync();
        var date = Today.AddDays(5);
        var future = await _service.RegisterAsync(Head, Transfer("takahashi", "S2", date, "主任"), Ct);
        Assert.Equal(_db.Id("S2"), await PrimaryOrgAsync("takahashi", date));

        // 範囲を持たない人は取り消せない
        Assert.Equal(FieldManagementError.NotFound, await ErrorOfAsync(() => _service.WithdrawAsync(_db.User("member"), future.Id, Ct)));

        var withdrawn = await _service.WithdrawAsync(Head, future.Id, Ct);
        Assert.Equal(FieldRegistrationStatus.Withdrawn, withdrawn.Status);
        Assert.Equal(_db.Id("S1"), await PrimaryOrgAsync("takahashi", date));
        Assert.Equal(0, await VisibleFutureAssignmentsAsync(_db.HrUser("hr1"), "takahashi"));
        Assert.Equal(0L, (await _db.QueryAsync<long>(
            null, "SELECT count(*) FROM personnel_action WHERE person_id = $1 AND effective_date = $2 AND upper_inf(recorded_period)", Ct, _db.Id("takahashi"), date)).Single());

        // 今日が有効日の登録は取り消せない
        var today = await _service.RegisterAsync(Head, Transfer("member", "S1", Today), Ct);
        Assert.Equal(FieldManagementError.Conflict, await ErrorOfAsync(() => _service.WithdrawAsync(Head, today.Id, Ct)));
    }

    [Fact(DisplayName = "新設: 配下に組織を新設し、同じ登録で異動できる。所属者が残る組織は廃止できない")]
    public async Task OrgCreate_AndTransferIntoIt()
    {
        await EnableAllAsync();
        var unit = EntityId.New();
        var input = new FieldRegistrationInput(Today, "営業三課を新設",
        [
            new FieldChangeInput(FieldChangeKind.OrgCreate, unit, new Dictionary<string, string?>
            {
                [ProposalFields.Code] = "S3",
                [ProposalFields.Name] = "営業三課",
                [ProposalFields.LevelKey] = "section",
                [ProposalFields.ParentOrgUnitId] = _db.Id("DP1").ToString("D"),
            }),
            new FieldChangeInput(FieldChangeKind.Assignment, _db.Id("member"), new Dictionary<string, string?> { [ProposalFields.OrgUnitId] = unit.ToString("D") }),
        ]);

        Assert.Equal(FieldRegistrationStatus.Applied, (await _service.RegisterAsync(Head, input, Ct)).Status);
        Assert.Equal(unit, await PrimaryOrgAsync("member", Today));

        // 他の本部の下には新設できない。所属者のいる組織は廃止できない
        var outside = input with
        {
            Changes = [new FieldChangeInput(FieldChangeKind.OrgCreate, null, new Dictionary<string, string?>
            {
                [ProposalFields.Code] = "X1", [ProposalFields.Name] = "別室", [ProposalFields.ParentOrgUnitId] = _db.Id("D2").ToString("D"),
            })],
        };
        Assert.Equal(FieldManagementError.Forbidden, await ErrorOfAsync(() => _service.RegisterAsync(Head, outside, Ct)));
        var abolish = new FieldRegistrationInput(Today.AddDays(1), null, [new FieldChangeInput(FieldChangeKind.OrgAbolish, unit, new Dictionary<string, string?>())]);
        Assert.Equal(FieldManagementError.Invalid, await ErrorOfAsync(() => _service.RegisterAsync(Head, abolish, Ct)));
    }

    // ---- 取込との関係 -------------------------------------------------------------------------------------------------

    private const string PeopleMapping = """
        {
          "name": "people-field",
          "target": "people",
          "legalEntityCode": "A",
          "employeeNumber": { "padLength": null },
          "fields": { "employee_number": ["社員番号"], "extension_number": ["内線"], "org_code": ["組織コード"] }
        }
        """;

    [Fact(DisplayName = "取込: 管理元が field の領域の差分は承認できない「人事パッケージとの差」になり、hr の領域の差分は起案中のまま")]
    public async Task Ingest_FieldManagedDomainBecomesDifference()
    {
        await EnableAsync(FieldDomain.Assignment);

        var result = await _db.ImportCsvAsync("社員番号,内線,組織コード\nE0002,9999,S2\n", PeopleMapping, Ct);

        var proposals = await _db.Store.ListProposalsAsync(_db.HrUser("hr2"), result.BatchId, Ct);
        var assignment = Assert.Single(proposals, p => p.TargetKind == ProposalTargetKind.Assignment);
        Assert.Equal(ProposalStatus.NeedsReview, assignment.Status);
        Assert.Equal(SupplementCodes.FieldManaged, assignment.Basis.Code);
        var contact = Assert.Single(proposals, p => p.TargetKind == ProposalTargetKind.PersonVersion);
        Assert.Equal(ProposalStatus.Proposed, contact.Status);
        Assert.Null(contact.Basis.Code);
    }

    [Fact(DisplayName = "取込: 管理元が both の領域で直接登録した値を変える差分は、起案中のまま印が付く")]
    public async Task Ingest_BothModeMarksFieldRegisteredValues()
    {
        var setting = await _management.ProposeModeAsync(OrgAdmin, FieldDomain.Assignment, FieldManagementMode.Both, null, Ct);
        await _management.DecideModeAsync(_db.HrUser("hr2"), setting.Id, true, Ct);
        await _service.RegisterAsync(Head, Transfer("takahashi", "S2", Today), Ct);

        var result = await _db.ImportCsvAsync("社員番号,組織コード\nE0002,S1\n", PeopleMapping, Ct);

        var assignment = Assert.Single(await _db.Store.ListProposalsAsync(_db.HrUser("hr2"), result.BatchId, Ct), p => p.TargetKind == ProposalTargetKind.Assignment);
        Assert.Equal(ProposalStatus.Proposed, assignment.Status);
        Assert.Equal(SupplementCodes.FieldRegistered, assignment.Basis.Code);
    }

    private FieldRegistrationInput OrgUpdate(string unit, string key, string? value) =>
        new(Today, null, [new FieldChangeInput(FieldChangeKind.OrgUpdate, _db.Id(unit), new Dictionary<string, string?> { [key] = value })]);

    /// <summary>アプリの範囲の検査を通さずに作った登録（DB の線を確かめる）。</summary>
    private static FieldRegistration Crafted(Guid registrant, ProposalTargetKind kind, Guid target, IReadOnlyDictionary<string, FieldChange> diff) =>
        new()
        {
            Id = EntityId.New(),
            Status = FieldRegistrationStatus.Draft,
            EffectiveDate = Today,
            RegisteredBy = registrant,
            RegisteredVia = ActorType.User,
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(1),
            Proposals =
            [
                new ChangeProposalRecord
                {
                    Id = EntityId.New(),
                    BatchId = Guid.Empty,
                    TargetKind = kind,
                    Operation = ProposalOperation.Change,
                    TargetId = target,
                    EffectiveDate = Today,
                    Diff = diff,
                    Basis = new ProposalBasis(),
                    Confidence = 1m,
                    Status = ProposalStatus.Proposed,
                    ProposedByPersonId = registrant,
                },
            ],
        };

    private Task<int> InsertAssignmentDirectlyAsync(ViewerContext viewer, string person, string unit) =>
        _db.ExecuteAsync(
            viewer,
            "INSERT INTO assignment (id, person_id, org_unit_id, assignment_type, valid_period) VALUES ($1, $2, $3, 'concurrent', daterange($4, NULL))",
            Ct,
            EntityId.New(),
            _db.Id(person),
            _db.Id(unit),
            Today.AddDays(1));
}
