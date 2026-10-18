using JinJi.Application;
using JinJi.Application.Audit;
using JinJi.Application.People;
using JinJi.Application.Policies;
using JinJi.Infrastructure.Database;
using JinJi.IntegrationTests.Support;
using JinJi.Policy;

namespace JinJi.IntegrationTests;

/// <summary>Application 層のサービスを PostgreSQL の実装（RLS・監査ログ）とつないだ結合テスト。</summary>
public sealed class DatabaseServiceTests(FixtureDatabase db) : IClassFixture<FixtureDatabase>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private OrgFixture F => db.Fixture;

    private PersonDirectoryService Directory => new(
        new NpgsqlOrgSnapshotProvider(db.AppDataSource),
        new NpgsqlPolicyRuleStore(db.AppDataSource, TimeProvider.System),
        new AuditWriter(new NpgsqlAuditSink(db.AppDataSource), TimeProvider.System),
        TimeProvider.System);

    private PolicyAdministrationService Administration => new(
        new NpgsqlPolicyRuleStore(db.AppDataSource, TimeProvider.System),
        new NpgsqlOrgSnapshotProvider(db.AppDataSource),
        new AuditWriter(new NpgsqlAuditSink(db.AppDataSource), TimeProvider.System),
        TimeProvider.System);

    [Fact(DisplayName = "一般の閲覧者: 内示・入社予定者・氏名の履歴が見えず、ポリシーでマスクされ、監査ログに参照した人の集合が残る")]
    public async Task NonAdminRead_IsMaskedAndAudited()
    {
        var requestId = Guid.NewGuid().ToString();
        var viewer = ViewerContext.ForUser(F.Person("sec1_head"), [], Channel.Api, FieldLimit.Unrestricted, "portal", requestId);

        var views = await Directory.ListPeopleAsync(viewer, null, Ct);

        Assert.DoesNotContain(views, v => v.PersonId == F.Person("future_hire"));
        var s1 = views.Single(v => v.PersonId == F.Person("s1_member"));
        Assert.DoesNotContain(s1.PersonnelActions!, a => a.Id == F.FutureTransferActionId);
        Assert.Equal("新姓", views.Single(v => v.PersonId == F.Person("u1_member")).FamilyName);
        Assert.All(views, v => Assert.Null(v.NameHistory));
        Assert.Null(views.Single(v => v.PersonId == F.Person("u2_member")).ExtensionNumber);
        Assert.Null(views.Single(v => v.PersonId == F.Person("r1_member")).ExtensionNumber);
        Assert.NotNull(views.Single(v => v.PersonId == F.Person("u1_member")).WorkMobilePhone);

        var audit = await ReadAuditAsync(requestId);
        var (action, outcome, actorType, clientId, subjects) = Assert.Single(audit);
        Assert.Equal(PersonDirectoryService.ListAction, action);
        Assert.Equal("success", outcome);
        Assert.Equal("user", actorType);
        Assert.Equal("portal", clientId);
        Assert.Equal(views.Select(v => v.PersonId).Order(), subjects.Order());
    }

    [Fact(DisplayName = "人事管理者: 未来の時点で内示と入社予定者を見られる")]
    public async Task AdminFutureRead_SeesPlannedChanges()
    {
        var views = await Directory.GetPeopleAsync(F.HrAdmin(), [F.Person("future_hire"), F.Person("s1_member")], F.Today.AddDays(20), Ct);

        Assert.Equal(2, views.Count);
        Assert.Equal([F.Unit("S2")], views.Single(v => v.PersonId == F.Person("s1_member")).Assignments!.Select(a => a.OrgUnitId));
        Assert.Contains(views.Single(v => v.PersonId == F.Person("future_hire")).PersonnelActions!, a => a.Id == F.FutureHireActionId);
    }

    [Fact(DisplayName = "未来の時点の拒否は denied として監査ログ（DB）に残る")]
    public async Task DeniedRead_IsAuditedInDatabase()
    {
        var requestId = Guid.NewGuid().ToString();
        var viewer = ViewerContext.ForUser(F.Person("u1_member"), [], Channel.Ui, FieldLimit.Unrestricted, requestId: requestId);

        await Assert.ThrowsAsync<AccessDeniedException>(() => Directory.GetPeopleAsync(viewer, [F.Person("s1_member")], F.Today.AddDays(30), Ct));

        var (_, outcome, _, _, subjects) = Assert.Single(await ReadAuditAsync(requestId));
        Assert.Equal("denied", outcome);
        Assert.Equal([F.Person("s1_member")], subjects);
    }

    [Fact(DisplayName = "ポリシー: 起案 → 別人の承認で有効になり、以後の参照に効く")]
    public async Task Policy_ProposeApproveTakesEffect()
    {
        var rule = PolicyEvaluationTests.Rule(PolicyTargetKind.Person, "s2_member", [PersonFields.WorkEmail], RelationScope.Self);
        var viewer = F.User("u1_member");
        Assert.NotNull(Assert.Single(await Directory.GetPeopleAsync(viewer, [F.Person("s2_member")], null, Ct)).WorkEmail);

        await Administration.ProposeAsync(F.HrAdmin(), rule, Ct);
        await Assert.ThrowsAsync<AccessDeniedException>(() => Administration.ApproveAsync(F.HrAdmin(), rule.Id, Ct));
        Assert.NotNull(Assert.Single(await Directory.GetPeopleAsync(viewer, [F.Person("s2_member")], null, Ct)).WorkEmail);

        await Administration.ApproveAsync(db.Approver, rule.Id, Ct);
        Assert.Null(Assert.Single(await Directory.GetPeopleAsync(viewer, [F.Person("s2_member")], null, Ct)).WorkEmail);
    }

    [Fact(DisplayName = "ポリシーの廃止: 起案 → 別人の承認で廃止され、承認までは効き続ける（DB の RLS・制約を通す）")]
    public async Task Policy_RetirementTakesEffectOnlyAfterApproval()
    {
        var rule = PolicyEvaluationTests.Rule(PolicyTargetKind.Person, "s3_member", [PersonFields.WorkEmail], RelationScope.Self);
        var viewer = F.User("u1_member");
        await Administration.ProposeAsync(F.HrAdmin(), rule, Ct);
        await Administration.ApproveAsync(db.Approver, rule.Id, Ct);
        Assert.Null(Assert.Single(await Directory.GetPeopleAsync(viewer, [F.Person("s3_member")], null, Ct)).WorkEmail);

        await Administration.ProposeRetirementAsync(F.HrAdmin(), rule.Id, Ct);
        Assert.Null(Assert.Single(await Directory.GetPeopleAsync(viewer, [F.Person("s3_member")], null, Ct)).WorkEmail);
        await Assert.ThrowsAsync<AccessDeniedException>(() => Administration.ApproveRetirementAsync(F.HrAdmin(), rule.Id, Ct));

        await Administration.ApproveRetirementAsync(db.Approver, rule.Id, Ct);
        Assert.NotNull(Assert.Single(await Directory.GetPeopleAsync(viewer, [F.Person("s3_member")], null, Ct)).WorkEmail);

        var stored = await new NpgsqlPolicyRuleStore(db.AppDataSource, TimeProvider.System).FindAsync(F.HrAdmin(), rule.Id, Ct);
        Assert.Equal(PolicyRuleStatus.Retired, stored!.Status);
        Assert.Equal(F.Person("hr_admin"), stored.RetirementProposedByPersonId);
    }

    [Fact(DisplayName = "退職者: 公開期間を過ぎた人は一般の閲覧者の一覧に出ず、人事管理者には出る（DB）")]
    public async Task LongRetired_IsHiddenFromNonAdmins()
    {
        Assert.DoesNotContain(await Directory.ListPeopleAsync(F.User("u1_member"), null, Ct), v => v.PersonId == F.Person("long_retired"));
        Assert.Contains(await Directory.ListPeopleAsync(F.HrAdmin(), null, Ct), v => v.PersonId == F.Person("long_retired"));
    }

    private async Task<List<(string Action, string Outcome, string ActorType, string? ClientId, Guid[] Subjects)>> ReadAuditAsync(string requestId)
    {
        await using var connection = await db.Database.OpenAppAsync(Ct);
        await SampleData.ActAsHrAdminAsync(connection, Ct);
        await using var command = new Npgsql.NpgsqlCommand(
            "SELECT action, outcome, actor_type, actor_client_id, subject_person_ids FROM audit_event WHERE request_id = $1",
            connection);
        command.Parameters.Add(new Npgsql.NpgsqlParameter { Value = requestId });
        var rows = new List<(string, string, string, string?, Guid[])>();
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            rows.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetFieldValue<Guid[]>(4)));
        }

        return rows;
    }
}
