using System.Text;
using System.Text.Json;
using JinJi.Application;
using JinJi.Application.Sync;
using JinJi.Domain;
using JinJi.Host.Evaluation;
using JinJi.Policy;
using JinJi.Sync;
using JinJi.Sync.DirectApply;
using Npgsql;

namespace JinJi.IntegrationTests.Sync;

/// <summary>同期計画の保存・承認・古い計画の拒否・スクリプト出力・実行結果の取込・直接反映（実 PostgreSQL。Graph はモック）。</summary>
public sealed class SyncPlanServiceTests : IAsyncLifetime
{
    private SyncDatabase _sync = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => _sync = await SyncDatabase.CreateAsync(Ct);

    public async ValueTask DisposeAsync() => await _sync.DisposeAsync();

    [Fact(DisplayName = "計画を作業領域に保存し、IT 全般統制の項目を先頭にする。新しい計画を作ると前の計画は置き換え済みになる")]
    public async Task Create_SavesPlan()
    {
        var created = await _sync.CreatePlanAsync(Ct, _sync.UsersJson(new() { ["member"] = new() { ["department"] = "旧組織" } }));

        var plan = (await _sync.Store.FindPlanAsync(_sync.Hr("hr2"), created.Plan.Id, Ct))!;
        Assert.Equal((SyncPlanStatus.Proposed, _sync.Id("hr1"), 5, SyncDatabase.Today), (plan.Status, plan.ProposedByPersonId, plan.DirectoryUserCount, plan.BusinessDate));
        Assert.Equal(SyncDatabase.Settings.Sha256(), plan.SettingsSha256);
        Assert.Equal(SyncDatabase.Settings.Fields, plan.Settings.Fields);
        var items = await _sync.Store.ListItemsAsync(_sync.Hr("hr2"), plan.Id, Ct);
        Assert.Equal(created.Items, items, new ItemComparer());
        Assert.Equal(
            [
                (1, SyncAction.DisableAccount, true, "E0009"),
                (2, SyncAction.UpdateAttributes, true, "E0005"),
                (3, SyncAction.CreateUser, false, "E9001"),
                (4, SyncAction.CreateUser, false, "E9002"),
                (5, SyncAction.ExcludedAccount, false, null),
            ],
            items.Select(i => (i.Sequence, i.Action, i.InScript, i.EmployeeNumber)).ToList());
        Assert.Equal(new SyncChange("department", "旧組織", "営業二課"), Assert.Single(items[1].Changes));
        Assert.Equal((1, 2), (plan.Summary["priority:critical"], plan.Summary["in_script"]));
        Assert.Equal(["user:success"], await Audit(SyncPlanService.CreateAction));

        var next = await _sync.CreatePlanAsync(Ct);
        Assert.Equal([plan.Id], next.Superseded);
        Assert.Equal(SyncPlanStatus.Superseded, (await _sync.Store.FindPlanAsync(_sync.Hr("hr2"), plan.Id, Ct))!.Status);
    }

    [Fact(DisplayName = "承認は起案者以外の人事管理者の人だけ（起案者・一般・エージェント・jin-ji 自身は不可。DB の CHECK 制約でも止める）")]
    public async Task Approve_SegregationOfDuties()
    {
        var created = await _sync.CreatePlanAsync(Ct);
        var id = created.Plan.Id;

        await Assert.ThrowsAsync<AccessDeniedException>(() => _sync.Service.ApproveAsync(_sync.Hr("hr1"), id, Ct));
        await Assert.ThrowsAsync<AccessDeniedException>(() => _sync.Service.ApproveAsync(_sync.Db.User("takahashi"), id, Ct));
        await Assert.ThrowsAsync<AccessDeniedException>(() => _sync.Service.ApproveAsync(ViewerContext.ForAgent("agent-x", _sync.Id("hr2"), [Roles.HrAdmin], Channel.Mcp, FieldLimit.Unrestricted), id, Ct));
        await Assert.ThrowsAsync<AccessDeniedException>(() => _sync.Service.ApproveAsync(ViewerContext.ForSystem(Channel.Cli), id, Ct));

        // アプリを通さずに起案者を決定者にしても DB が拒否する
        var ex = await Assert.ThrowsAsync<PostgresException>(() => _sync.Db.ExecuteAsync(
            _sync.Hr("hr1"), "UPDATE sync_plan SET status = 'approved', decided_by_person_id = $1, decided_at = now() WHERE id = $2", Ct, _sync.Id("hr1"), id));
        Assert.Equal("sync_plan_segregation_of_duties", ex.ConstraintName);

        // 決定者は閲覧者本人としてしか記録できない（RLS）
        var rls = await Assert.ThrowsAsync<PostgresException>(() => _sync.Db.ExecuteAsync(
            _sync.Hr("head"), "UPDATE sync_plan SET status = 'approved', decided_by_person_id = $1, decided_at = now() WHERE id = $2", Ct, _sync.Id("hr2"), id));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, rls.SqlState);

        var approved = await _sync.Service.ApproveAsync(_sync.Hr("hr2"), id, Ct);
        Assert.Equal((SyncPlanStatus.Approved, _sync.Id("hr2")), (approved.Status, approved.DecidedByPersonId));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _sync.Service.ApproveAsync(_sync.Hr("head"), id, Ct));
        Assert.Equal(["user:denied", "user:denied", "agent:denied", "system:denied", "user:success", "user:failed"], await Audit(SyncPlanService.ApproveAction));
    }

    [Fact(DisplayName = "計画の作成後に jin-ji 側が変わったら、承認・出力の時点で古い計画にして拒否する。期限切れも同じ")]
    public async Task Stale_Plans()
    {
        // 承認前に一時停止が登録された
        var first = await _sync.CreatePlanAsync(Ct);
        await _sync.Db.ExecuteAsync(
            _sync.Hr("hr2"), "INSERT INTO account_suspension (id, person_id, valid_period) VALUES ($1, $2, daterange($3, NULL))", Ct, EntityId.New(), _sync.Id("member"), SyncDatabase.Today.AddDays(-1));
        await Assert.ThrowsAsync<SyncPlanStaleException>(() => _sync.Service.ApproveAsync(_sync.Hr("hr2"), first.Plan.Id, Ct));
        Assert.Equal(SyncPlanStatus.Stale, (await _sync.Store.FindPlanAsync(_sync.Hr("hr2"), first.Plan.Id, Ct))!.Status);

        // 承認後・出力前に外部 ID の対応が増えた
        var second = await _sync.CreatePlanAsync(Ct);
        Assert.Contains(second.Items, i => i.Action == SyncAction.BlockSignIn && i.EmployeeNumber == "E0005");
        await _sync.Service.ApproveAsync(_sync.Hr("hr2"), second.Plan.Id, Ct);
        await _sync.Db.ExecuteAsync(
            _sync.Hr("hr2"), "INSERT INTO external_identity (id, person_id, source_system, external_id) VALUES ($1, $2, 'entra_id', $3)", Ct, EntityId.New(), _sync.Id("hr1"), Guid.NewGuid().ToString("D"));
        await Assert.ThrowsAsync<SyncPlanStaleException>(() => _sync.Service.ExportScriptAsync(_sync.Hr("hr2"), second.Plan.Id, Ct));
        Assert.Equal(SyncPlanStatus.Stale, (await _sync.Store.FindPlanAsync(_sync.Hr("hr2"), second.Plan.Id, Ct))!.Status);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _sync.Service.ExportScriptAsync(_sync.Hr("hr2"), second.Plan.Id, Ct));

        // 期限切れ（既定 7 日）
        var third = await _sync.CreatePlanAsync(Ct);
        _sync.Time.Now = _sync.Time.Now.AddDays(7).AddMinutes(1);
        var ex = await Assert.ThrowsAsync<SyncPlanStaleException>(() => _sync.Service.ApproveAsync(_sync.Hr("hr2"), third.Plan.Id, Ct));
        Assert.Contains("期限", ex.Message, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "内示（将来の異動・一時停止・入社）は計画に含めない")]
    public async Task FutureChanges_AreNotPlanned()
    {
        var hr = _sync.Hr("hr2");
        await _sync.Db.ExecuteAsync(
            hr,
            "INSERT INTO assignment (id, person_id, org_unit_id, assignment_type, position_title, secondment_type, valid_period) VALUES ($1, $2, $3, 'concurrent', '課長', 'outbound', daterange($4, NULL))",
            Ct, EntityId.New(), _sync.Id("takahashi"), _sync.Id("S2"), SyncDatabase.Today.AddDays(10));
        await _sync.Db.ExecuteAsync(
            hr, "INSERT INTO account_suspension (id, person_id, valid_period) VALUES ($1, $2, daterange($3, NULL))", Ct, EntityId.New(), _sync.Id("member"), SyncDatabase.Today.AddDays(5));
        var newcomer = EntityId.New();
        await _sync.Db.ExecuteAsync(hr, "INSERT INTO person (id, legal_entity_id, employee_number) VALUES ($1, $2, 'E0010')", Ct, newcomer, _sync.Db.EntityA);
        await _sync.Db.ExecuteAsync(
            hr,
            """
            INSERT INTO person_version (id, person_id, family_name, given_name, display_name, search_name, work_email, employment_type, hire_date, valid_period)
            VALUES ($1, $2, '新人', '一', '新人 一', '新人一', 'new@example.co.jp', 'regular', $3, daterange($3, NULL))
            """,
            Ct, EntityId.New(), newcomer, SyncDatabase.Today.AddDays(3));

        var created = await _sync.CreatePlanAsync(Ct);

        Assert.DoesNotContain(created.Items, i => i.Action == SyncAction.BlockSignIn);
        Assert.DoesNotContain(created.Items, i => i.EmployeeNumber is "E0002" or "E0010");
        Assert.Equal([SyncAction.DisableAccount], created.Items.Where(i => i.InScript).Select(i => i.Action));
    }

    [Fact(DisplayName = "スクリプトは承認済みの計画だけ。出力は監査ログに残す")]
    public async Task Export_OnlyApproved()
    {
        var created = await _sync.CreatePlanAsync(Ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _sync.Service.ExportScriptAsync(_sync.Hr("hr2"), created.Plan.Id, Ct));
        await Assert.ThrowsAsync<AccessDeniedException>(() => _sync.Service.ExportScriptAsync(_sync.Db.User("takahashi"), created.Plan.Id, Ct));
        await Assert.ThrowsAsync<SyncPlanNotFoundException>(() => _sync.Service.ExportScriptAsync(_sync.Hr("hr2"), Guid.NewGuid(), Ct));

        await _sync.Service.ApproveAsync(_sync.Hr("hr2"), created.Plan.Id, Ct);
        var bundle = await _sync.Service.ExportScriptAsync(_sync.Hr("hr1"), created.Plan.Id, Ct);
        Assert.Equal(4, bundle.Files.Count);
        Assert.Contains(SyncPlanService.ExportAction, await _sync.Db.QueryAsync<string>(null, "SELECT action FROM audit_event WHERE outcome = 'success'", Ct));

        var rejected = await _sync.CreatePlanAsync(Ct);
        await _sync.Service.RejectAsync(_sync.Hr("hr2"), rejected.Plan.Id, Ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _sync.Service.ExportScriptAsync(_sync.Hr("hr2"), rejected.Plan.Id, Ct));
    }

    [Fact(DisplayName = "実行ログの取込: 計画 ID とデータの SHA-256 が一致するものだけ。項目ごとの結果を記録し、本番の実行で計画は実行済みになる")]
    public async Task ImportResults()
    {
        var created = await _sync.CreatePlanAsync(Ct, _sync.UsersJson(new() { ["member"] = new() { ["department"] = "旧組織" } }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _sync.Service.ImportResultsAsync(_sync.Hr("hr2"), created.Plan.Id, Log(created.Plan.Id, new string('0', 64), true, []), Ct));
        await _sync.Service.ApproveAsync(_sync.Hr("hr2"), created.Plan.Id, Ct);
        var bundle = await _sync.Service.ExportScriptAsync(_sync.Hr("hr2"), created.Plan.Id, Ct);
        var disable = created.Items.Single(i => i.Action == SyncAction.DisableAccount);
        var update = created.Items.Single(i => i.Action == SyncAction.UpdateAttributes);

        var dry = await _sync.Service.ImportResultsAsync(_sync.Hr("hr2"), created.Plan.Id, Log(created.Plan.Id, bundle.DataSha256, true, [(disable.Id, "would_apply"), (update.Id, "would_apply")]), Ct);
        Assert.Equal((true, 2, 0), (dry.WhatIf, dry.Counts[SyncOutcome.WouldApply], dry.MissingCount));
        Assert.Equal(SyncPlanStatus.Approved, (await _sync.Store.FindPlanAsync(_sync.Hr("hr2"), created.Plan.Id, Ct))!.Status);

        var result = await _sync.Service.ImportResultsAsync(
            ViewerContext.ForSystem(Channel.Cli),
            created.Plan.Id,
            Log(created.Plan.Id, bundle.DataSha256, false, [(disable.Id, "succeeded"), (Guid.NewGuid(), "succeeded")]),
            Ct);
        Assert.Equal((1, 1, 1), (result.Counts[SyncOutcome.Succeeded], result.MissingCount, result.UnknownEntryCount));
        Assert.Equal(
            [(SyncAction.DisableAccount, SyncOutcome.Succeeded), (SyncAction.UpdateAttributes, SyncOutcome.WouldApply)],
            result.Items.Select(i => (i.Action, i.Outcome!.Value)).ToList());
        var plan = (await _sync.Store.FindPlanAsync(_sync.Hr("hr2"), created.Plan.Id, Ct))!;
        Assert.Equal(SyncPlanStatus.Applied, plan.Status);
        Assert.NotNull(plan.AppliedAt);

        var update2 = await _sync.Service.ImportResultsAsync(_sync.Hr("hr2"), created.Plan.Id, Log(created.Plan.Id, bundle.DataSha256, false, [(update.Id, "skipped_precondition")]), Ct);
        Assert.Equal(SyncOutcome.SkippedPrecondition, update2.Items.Single(i => i.ItemId == update.Id).Outcome);

        await Assert.ThrowsAsync<SyncLogFormatException>(() => _sync.Service.ImportResultsAsync(_sync.Hr("hr2"), created.Plan.Id, Log(Guid.NewGuid(), bundle.DataSha256, false, []), Ct));
        await Assert.ThrowsAsync<SyncLogFormatException>(() => _sync.Service.ImportResultsAsync(_sync.Hr("hr2"), created.Plan.Id, Log(created.Plan.Id, new string('e', 64), false, []), Ct));
        Assert.Equal(3L, (await _sync.Db.QueryAsync<long>(null, "SELECT count(*) FROM sync_run WHERE log_sha256 IS NOT NULL", Ct)).Single());
        Assert.Equal(["user:failed", "user:success", "system:success", "user:success", "user:failed", "user:failed"], await Audit(SyncPlanService.ResultAction));

        var detail = await _sync.Service.GetAsync(_sync.Hr("hr2"), created.Plan.Id, Ct);
        Assert.Equal(SyncOutcome.SkippedPrecondition, detail.Reconciliation.Single(r => r.ItemId == update.Id).Outcome);
    }

    [Fact(DisplayName = "直接反映（モック）: ドライラン → 事前条件の不一致はスキップ・エラーは記録して続行・あるべき値なら何もしない。結果は direct の実行として残る")]
    public async Task DirectApply_WithMock()
    {
        var users = _sync.UsersJson(new()
        {
            ["member"] = new() { ["department"] = "旧組織" },
            ["takahashi"] = new() { ["surname"] = "高橋" },
            ["head"] = new() { ["employeeId"] = null },
        });
        var created = await _sync.CreatePlanAsync(Ct, users);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new DirectApplier(_sync.Service, new InMemoryDirectory([]), _sync.Time).ApplyAsync(_sync.Hr("hr2"), created.Plan.Id, false, Ct));
        await _sync.Service.ApproveAsync(_sync.Hr("hr2"), created.Plan.Id, Ct);

        var memory = new InMemoryDirectory(GraphDirectoryUsers.Parse(users));
        var dry = await new DirectApplier(_sync.Service, memory, _sync.Time).ApplyAsync(ViewerContext.ForSystem(Channel.Cli), created.Plan.Id, whatIf: true, Ct);
        Assert.Equal((4, 0), (dry.Counts[SyncOutcome.WouldApply], memory.Writes));

        memory.Set(_sync.Object("member"), "department", "Entra で変えた部署");
        memory.Set(_sync.Object("head"), "employeeId", "E0001");
        var failing = new FailingDirectory(memory, _sync.Object("takahashi"));
        var applied = await new DirectApplier(_sync.Service, failing, _sync.Time).ApplyAsync(ViewerContext.ForSystem(Channel.Cli), created.Plan.Id, whatIf: false, Ct);

        Assert.Equal(
            [("E0009", SyncOutcome.Succeeded), ("E0001", SyncOutcome.AlreadyApplied), ("E0002", SyncOutcome.Failed), ("E0005", SyncOutcome.SkippedPrecondition)],
            created.Items.Where(i => i.InScript).Select(i => (i.EmployeeNumber!, applied.Items.Single(r => r.ItemId == i.Id).Outcome!.Value)).ToList());
        Assert.Equal("false", memory.Users.Single(u => u.ObjectId == _sync.Object("retiree"))["accountEnabled"]);
        Assert.Equal("Entra で変えた部署", memory.Users.Single(u => u.ObjectId == _sync.Object("member"))["department"]);
        Assert.Equal(1, memory.Writes);
        Assert.Equal(SyncPlanStatus.Applied, (await _sync.Store.FindPlanAsync(_sync.Hr("hr2"), created.Plan.Id, Ct))!.Status);
        Assert.Equal(["direct:true", "direct:false"], await _sync.Db.QueryAsync<string>(null, "SELECT mode || ':' || what_if FROM sync_run ORDER BY recorded_at, what_if DESC", Ct));
        Assert.Equal(
            "Entra で変えた部署",
            (await _sync.Db.QueryAsync<string>(null, "SELECT observed ->> 'department' FROM sync_item_result WHERE outcome = 'skipped_precondition'", Ct)).Single());
    }

    [Fact(DisplayName = "作業領域の守り: 人事管理者以外には見えない。項目・実行の記録は追記だけ。計画の中身は変えられない。保持しない項目は changes に入らない")]
    public async Task Workspace_Guards()
    {
        var created = await _sync.CreatePlanAsync(Ct);
        var id = created.Plan.Id;

        Assert.Equal([0L], await _sync.Db.QueryWithAsync<long>(_sync.Db.User("takahashi"), "SELECT count(*) FROM sync_plan", Ct));
        Assert.Equal([0L], await _sync.Db.QueryWithAsync<long>(_sync.Db.User("takahashi"), "SELECT count(*) FROM sync_plan_item", Ct));
        await Assert.ThrowsAsync<AccessDeniedException>(() => _sync.Service.ListAsync(_sync.Db.User("takahashi"), 10, Ct));
        await Assert.ThrowsAsync<AccessDeniedException>(() => _sync.Service.CreateAsync(
            ViewerContext.ForService("sync", FieldLimit.Unrestricted), new SyncPlanRequest([], SyncSettings.Default, "x.json", new string('0', 64)), Ct));

        foreach (var sql in new[] { "UPDATE sync_plan_item SET note = 'x'", "DELETE FROM sync_plan", "DELETE FROM sync_item_result", "UPDATE sync_run SET what_if = true" })
        {
            var denied = await Assert.ThrowsAsync<PostgresException>(() => _sync.Db.ExecuteAsync(_sync.Hr("hr2"), sql, Ct));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, denied.SqlState);
        }

        var immutable = await Assert.ThrowsAsync<PostgresException>(() => _sync.Db.ExecuteAsync(_sync.Hr("hr2"), "UPDATE sync_plan SET status = 'stale', closed_at = now(), applied_at = now() WHERE id = $1", Ct, id));
        Assert.Equal("sync_plan_decision_check", immutable.ConstraintName);
        await using (var owner = await _sync.Db.Database.OpenOwnerAsync(Ct))
        {
            await Support.SampleData.ActAsHrAdminAsync(owner, Ct);
            var changed = await Assert.ThrowsAsync<PostgresException>(() => Support.SampleData.ExecuteAsync(owner, "UPDATE sync_plan SET settings = '{}' WHERE id = $1", Ct, id));
            Assert.Equal("sync_plan_immutable", changed.ConstraintName);
            var forbidden = await Assert.ThrowsAsync<PostgresException>(() => Support.SampleData.ExecuteAsync(
                owner,
                """INSERT INTO sync_plan_item (id, sync_plan_id, sequence, priority, action, directory_object_id, in_script, changes) VALUES ($1, $2, 999, 'normal', 'update_attributes', $3, true, '[{"field":"birthday","current":null,"desired":"1980-01-01","current_known":true}]')""",
                Ct,
                EntityId.New(),
                id,
                Guid.NewGuid().ToString("D")));
            Assert.Equal("sync_plan_item_changes_allowed", forbidden.ConstraintName);
        }

        Assert.Equal(
            EntraFields.All.Order(StringComparer.Ordinal),
            (await _sync.Db.QueryAsync<string[]>(null, "SELECT jinji_sync_field_keys()", Ct)).Single().Order(StringComparer.Ordinal));
    }

    [Fact(DisplayName = "AI エージェントは計画を起案できる（起案者は委任元）が、承認できない")]
    public async Task Agent_CanProposeOnly()
    {
        var agent = ViewerContext.ForAgent("agent-x", _sync.Id("hr1"), [Roles.HrAdmin], Channel.Mcp, FieldLimit.Unrestricted);
        var created = await _sync.CreatePlanAsync(Ct, viewer: agent);

        Assert.Equal((_sync.Id("hr1"), ActorType.Agent), (created.Plan.ProposedByPersonId, created.Plan.ProposedViaActor));
        await Assert.ThrowsAsync<AccessDeniedException>(() => _sync.Service.ExportScriptAsync(agent, created.Plan.Id, Ct));
        await Assert.ThrowsAsync<AccessDeniedException>(() => _sync.Service.ApproveAsync(_sync.Hr("hr1"), created.Plan.Id, Ct));
        await _sync.Service.ApproveAsync(_sync.Hr("hr2"), created.Plan.Id, Ct);
    }

    /// <summary>監査ログ（操作者の種別:結果。記録順）。</summary>
    private Task<List<string>> Audit(string action) =>
        _sync.Db.QueryAsync<string>(null, "SELECT actor_type || ':' || outcome FROM audit_event WHERE action = $1 ORDER BY occurred_at, id", Ct, action);

    private static byte[] Log(Guid planId, string sha, bool whatIf, IReadOnlyList<(Guid Item, string Outcome)> entries) =>
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            format = EntraScriptRenderer.LogFormat,
            planId,
            dataSha256 = sha,
            whatIf,
            startedAt = "2026-10-01T00:00:00Z",
            entries = entries.Select(e => new { itemId = e.Item, outcome = e.Outcome, observed = new Dictionary<string, object?>(), message = (string?)null, at = "2026-10-01T00:00:01Z" }),
        }));

    /// <summary>1 人の書き込みだけを失敗させる（エラーでも続行するかの確認）。</summary>
    private sealed class FailingDirectory(IDirectoryWriteClient inner, string failingObjectId) : IDirectoryWriteClient
    {
        public Task<IReadOnlyDictionary<string, string?>> GetUserAsync(string objectId, IReadOnlyList<string> properties, CancellationToken cancellationToken) =>
            inner.GetUserAsync(objectId, properties, cancellationToken);

        public Task<string?> GetManagerIdAsync(string objectId, CancellationToken cancellationToken) => inner.GetManagerIdAsync(objectId, cancellationToken);

        public Task UpdateUserAsync(string objectId, IReadOnlyDictionary<string, object?> changes, CancellationToken cancellationToken) =>
            objectId == failingObjectId
                ? throw new GraphRequestException(403, "Authorization_RequestDenied", "Graph の要求が失敗しました。")
                : inner.UpdateUserAsync(objectId, changes, cancellationToken);

        public Task SetManagerAsync(string objectId, string managerObjectId, CancellationToken cancellationToken) => inner.SetManagerAsync(objectId, managerObjectId, cancellationToken);
    }

    private sealed class ItemComparer : IEqualityComparer<SyncPlanItem>
    {
        public bool Equals(SyncPlanItem? x, SyncPlanItem? y) =>
            x is not null && y is not null && x.Id == y.Id && x.Sequence == y.Sequence && x.Action == y.Action && x.Priority == y.Priority && x.InScript == y.InScript
            && x.ObjectId == y.ObjectId && x.PersonId == y.PersonId && x.EmployeeNumber == y.EmployeeNumber && x.Changes.SequenceEqual(y.Changes)
            && x.Basis.Code == y.Basis.Code && x.Basis.Jinji.SequenceEqual(y.Basis.Jinji) && x.Note == y.Note;

        public int GetHashCode(SyncPlanItem obj) => obj.Id.GetHashCode();
    }
}
