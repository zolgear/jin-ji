using System.Text;
using JinJi.Application.Ingest;
using JinJi.Connectors;
using JinJi.Domain;
using JinJi.Ingest;

namespace JinJi.IntegrationTests.Ingest;

/// <summary>
/// 辞令（発令）・組織図（階層・廃止・仮コード）・Entra ID のユーザーの取込を実 PostgreSQL で確かめる。テストごとに一時データベースを作る。
/// </summary>
public sealed class ActionsAndDirectoryTests : IAsyncLifetime
{
    private const string Aliases = """
        "legalEntityAliases": { "A": ["テスト株式会社", "(株)テスト"] },
        """;

    private const string ActionMapping = $$"""
        {
          "name": "jirei",
          "target": "personnel_actions",
          {{Aliases}}
          "fields": {
            "action_date": ["発令日"], "employee_number": ["社員番号"], "full_name": ["氏名"], "action_text": ["発令内容"], "action_note": ["備考"]
          }
        }
        """;

    private IngestDatabase _db = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DateOnly Today => IngestDatabase.Today;

    public async ValueTask InitializeAsync() => _db = await IngestDatabase.CreateAsync(Ct);

    public ValueTask DisposeAsync() => _db.DisposeAsync();

    [Fact(DisplayName = "辞令: 休職と復職・兼務と解除を順に承認すると、一時停止・兼務の期間ができる（承認時の状態から版を導出）")]
    public async Task Actions_DeriveSuspensionAndConcurrentPeriods()
    {
        var csv = Csv(
            (Today.AddDays(-30), "E0002", "髙橋 一郎", "休職を命ずる"),
            (Today.AddDays(-5), "E0002", "髙橋 一郎", "復職を命ずる"),
            (Today.AddDays(-20), "E0001", "山田 太郎", "人事部 部長を兼務させる"),
            (Today.AddDays(-3), "E0001", "山田 太郎", "兼務を解く"));

        var proposals = await ImportAndApproveAsync(csv, ActionMapping);

        Assert.Equal(4, proposals.Count(p => p.Status == ProposalStatus.Proposed));
        Assert.Equal(
            [$"[{Today.AddDays(-30):yyyy-MM-dd},{Today.AddDays(-5):yyyy-MM-dd})"],
            await _db.QueryAsync<string>(null, "SELECT valid_period::text FROM account_suspension WHERE person_id = $1 AND upper_inf(recorded_period)", Ct, _db.Id("takahashi")));
        Assert.Equal(
            [$"部長:[{Today.AddDays(-20):yyyy-MM-dd},{Today.AddDays(-3):yyyy-MM-dd})"],
            await _db.QueryAsync<string>(null, "SELECT position_title || ':' || valid_period::text FROM assignment WHERE person_id = $1 AND assignment_type = 'concurrent' AND upper_inf(recorded_period)", Ct, _db.Id("head")));
        Assert.Equal(
            ["concurrent_end", "concurrent_start", "suspension_end", "suspension_start"],
            (await _db.QueryAsync<string>(null, "SELECT action_type FROM personnel_action ORDER BY action_type", Ct)).Distinct());
    }

    [Fact(DisplayName = "辞令: 発令日前の異動（内示）は承認後も人事管理者以外には発令も将来の所属も見えない")]
    public async Task Actions_FutureTransferStaysHidden()
    {
        var date = Today.AddDays(12);
        await ImportAndApproveAsync(Csv((date, "E0005", "鈴木 次郎", "営業一課 主任を命ずる")), ActionMapping);

        Assert.Equal(["transfer"], await _db.QueryAsync<string>(null, "SELECT action_type FROM personnel_action WHERE person_id = $1", Ct, _db.Id("member")));
        Assert.Empty(await _db.QueryWithAsync<string>(_db.User("member"), "SELECT action_type FROM personnel_action WHERE person_id = $1", Ct, _db.Id("member")));
        Assert.Empty(await _db.QueryWithAsync<Guid>(_db.User("takahashi"), "SELECT id FROM assignment WHERE person_id = $1 AND lower(valid_period) > jinji_business_date()", Ct, _db.Id("member")));

        var general = await _db.Snapshots.LoadAsync(_db.User("takahashi"), Today, Today, Ct);
        Assert.Equal(_db.Id("S2"), general.AssignmentsOf(_db.Id("member")).Single().OrgUnitId);
        var future = await _db.Snapshots.LoadAsync(_db.HrUser("hr2"), date, Today, Ct);
        var moved = future.AssignmentsOf(_db.Id("member")).Single();
        Assert.Equal((_db.Id("S1"), "主任"), (moved.OrgUnitId, moved.PositionTitle));
    }

    [Fact(DisplayName = "辞令: 社員番号の誤記・欠落・解釈できない内容は要確認と修正依頼に、姓だけ違う行は受け入れて氏名を作業領域に残さない")]
    public async Task Actions_RoutesUncertainRowsToReview()
    {
        var csv = Csv(
            (Today.AddDays(-1), "E0005", "佐藤 花子", "営業一課に配置換えを命ずる"),
            (Today.AddDays(-1), "", "鈴木 次郎", "営業一課に配置換えを命ずる"),
            (Today.AddDays(-1), "E0002", "髙橋 一郎", "表彰する"),
            (Today.AddDays(-1), "E0002", "佐伯 一郎", "営業二課に配置換えを命ずる"),
            (Today.AddDays(-1), "E0001", "山田 太郎", "第一営業部 課長を命ずる"));

        var result = await _db.ImportCsvAsync(csv, ActionMapping, Ct);
        var proposals = await _db.Store.ListProposalsAsync(_db.HrUser("hr2"), result.BatchId, Ct);
        var corrections = await _db.Store.ListCorrectionsAsync(_db.HrUser("hr2"), result.BatchId, Ct);

        ChangeProposalRecord Row(int row) => proposals.Single(p => p.Basis.RowNumbers.Contains(row));
        Assert.Equal((ProposalOperation.Review, SupplementCodes.SuspectedEmployeeNumberTypo), (Row(2).Operation, Row(2).Basis.Code));
        Assert.Equal([_db.Id("member")], Row(3).Basis.Candidates.Select(c => c.PersonId));
        Assert.Equal(SupplementCodes.MissingEmployeeNumber, Row(3).Basis.Code);
        Assert.Equal(SupplementCodes.UninterpretableAction, Row(4).Basis.Code);
        Assert.Equal((ProposalStatus.Proposed, _db.Id("takahashi")), (Row(5).Status, Row(5).TargetId!.Value));
        Assert.Equal(SupplementCodes.AmbiguousOrg, Row(6).Basis.Code);
        Assert.Equal(
            [CorrectionIssue.MissingEmployeeNumber, CorrectionIssue.UninterpretableAction, CorrectionIssue.SuspectedEmployeeNumberTypo],
            corrections.Select(c => c.Issue).Distinct().Order());

        // 姓だけ違う行（戸籍姓・通称）の氏名は作業領域のどこにも残さない
        Assert.Empty(await _db.QueryAsync<string>(null, "SELECT data::text FROM staging_row WHERE data::text LIKE '%佐伯%' UNION ALL SELECT data::text FROM normalized_row WHERE data::text LIKE '%佐伯%'", Ct));
    }

    [Fact(DisplayName = "辞令: 自由記述に住所・電話番号らしき値があれば staging に保存せず、修正依頼に項目と種別だけを残す")]
    public async Task Actions_DropSensitiveFreeText()
    {
        var csv = "発令日,社員番号,氏名,発令内容,備考\n" +
                  $"{Today.AddDays(-1):yyyy/MM/dd},E0005,鈴木 次郎,営業一課に配置換えを命ずる,新住所 東京都千代田区丸の内9-9-9 090-9999-0000\n";

        var result = await _db.ImportCsvAsync(csv, ActionMapping, Ct);

        var correction = Assert.Single(await _db.Store.ListCorrectionsAsync(_db.HrUser("hr2"), result.BatchId, Ct));
        Assert.Equal((CorrectionIssue.SuspectedSensitiveValue, IngestFields.ActionNote), (correction.Issue, correction.FieldKey));
        Assert.DoesNotContain("千代田", correction.Message, StringComparison.Ordinal);
        var everything = string.Join('\n', await _db.QueryAsync<string>(null, "SELECT data::text FROM staging_row UNION ALL SELECT data::text FROM normalized_row UNION ALL SELECT message FROM correction_request", Ct));
        Assert.DoesNotContain("千代田", everything, StringComparison.Ordinal);
        Assert.DoesNotContain("090-9999", everything, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "組織図: 階層の空欄を補い、段を名称から推定し、コードの無い組織に仮コード、廃止済みは作らず既存なら廃止を提案する")]
    public async Task OrgChart_HierarchyAbolishedAndProvisionalCodes()
    {
        var mapping = $$"""
            {
              "name": "org", "target": "org_units",
              "source": { "hierarchy": { "entityColumn": "会社", "columns": ["本部", "部・室", "課・グループ"], "noCarrySuffixes": ["室"] } },
              {{Aliases}}
              "fields": { "legal_entity_code": ["会社"], "org_code": ["組織コード"], "org_head_name": ["責任者"], "org_remarks": ["備考"] }
            }
            """;
        var csv = string.Join("\n",
            "会社,本部,部・室,課・グループ,組織コード,責任者,備考",
            "(株)テスト,第一本部,,,D1,山田太郎,",
            ",,第一営業部,,DP1,山田 太郎,",
            ",,,営業一課,S1,鈴木　次郎,",
            ",,,営業二課,S2,,2025/03/31 廃止",
            ",,,営業三課,,,",
            ",,旧営業部,,X9,,2024/03/31 廃止",
            ",,,旧営業一課,X10,,");

        var proposals = await ImportAndApproveAsync(csv, mapping);

        var snapshot = await _db.Snapshots.LoadAsync(_db.HrUser("hr2"), Today, Today, Ct);
        var created = snapshot.Units.Single(u => u.Name == "営業三課");
        Assert.StartsWith(ProposalPlanner.ProvisionalCodePrefix, created.Code, StringComparison.Ordinal);
        Assert.Equal(("section", _db.Id("DP1")), (created.LevelKey, created.ParentId));
        Assert.Null(snapshot.FindUnit(_db.Id("S2")));
        Assert.DoesNotContain(snapshot.Units, u => u.Name.StartsWith("旧営業", StringComparison.Ordinal));
        Assert.Equal(_db.Id("member"), snapshot.FindUnit(_db.Id("S1"))!.HeadPersonId);
        Assert.Contains(proposals, p => p.Operation == ProposalOperation.Abolish && p.TargetId == _db.Id("S2") && p.EffectiveDate == new DateOnly(2025, 4, 1));

        var batch = proposals[0].BatchId;
        var corrections = await _db.Store.ListCorrectionsAsync(_db.HrUser("hr2"), batch, Ct);
        Assert.Equal(3, corrections.Count(c => c.Issue == CorrectionIssue.AbolishedOrg));
        Assert.Contains(corrections, c => c.Issue == CorrectionIssue.MissingRequired && c.FieldKey == IngestFields.OrgCode);

        // 取り込み直しても同じ仮コードの組織に当たり、新規の提案にならない
        var again = await _db.ImportCsvAsync(csv, mapping, Ct);
        Assert.DoesNotContain(await _db.Store.ListProposalsAsync(_db.HrUser("hr2"), again.BatchId, Ct), p => p.Operation == ProposalOperation.Create);
    }

    [Fact(DisplayName = "組織: 上位組織を変えると、配下の組織の版の ltree パスも作り直す")]
    public async Task OrgParentChange_RebasesDescendantPaths()
    {
        var mapping = """
            {"name":"org","target":"org_units","legalEntityCode":"A",
             "fields":{"org_code":["組織コード"],"org_name":["組織名"],"parent_org_code":["上位組織コード"]}}
            """;

        await ImportAndApproveAsync("組織コード,組織名,上位組織コード\nDP1,第一営業部,D2\n", mapping);

        var paths = await _db.QueryAsync<string>(null, "SELECT path::text FROM org_unit_version WHERE org_unit_id = ANY($1) AND upper_inf(recorded_period) ORDER BY 1", Ct, new[] { _db.Id("DP1"), _db.Id("S1"), _db.Id("S2") });
        var prefix = $"{_db.Id("D2"):N}.{_db.Id("DP1"):N}";
        Assert.Equal(3, paths.Count);
        Assert.All(paths, p => Assert.StartsWith(prefix, p, StringComparison.Ordinal));
    }

    [Fact(DisplayName = "Entra ID: 外部 ID の対応付けと、人ではないアカウント・退職者の有効なアカウント・employeeId の空・部署名の不一致・アカウントの無い在籍者の補完提案")]
    public async Task Directory_MatchesAndSupplements()
    {
        await _db.ExecuteAsync(_db.HrUser("hr2"), "INSERT INTO person (id, legal_entity_id, employee_number) VALUES ($1, $2, 'E0099')", Ct, EntityId.New(), _db.EntityA);
        var retired = (await _db.QueryAsync<Guid>(null, "SELECT id FROM person WHERE employee_number = 'E0099'", Ct)).Single();
        await _db.ExecuteAsync(
            _db.HrUser("hr2"),
            "INSERT INTO person_version (id, person_id, family_name, given_name, display_name, search_name, employment_type, hire_date, termination_date, valid_period) VALUES ($1, $2, '退職', '太郎', '退職 太郎', '退職太郎', 'regular', '2010-04-01', $3, daterange('2010-04-01', NULL))",
            Ct,
            EntityId.New(),
            retired,
            Today.AddYears(-1));

        var mapping = """
            {"name":"entra","target":"directory_users","source":{"kind":"graph_json"},"fullSnapshot":true,"legalEntityCode":"A",
             "fields":{"external_id":["id"],"employee_number":["employeeId"],"work_email":["mail","userPrincipalName"],"family_name":["surname"],
                       "given_name":["givenName"],"display_name":["displayName"],"org_name":["department"],"account_enabled":["accountEnabled"]},
             "ignore":["businessPhones"]}
            """;
        var json = """
            {"value":[
              {"id":"aaa-1","displayName":"髙橋 一郎","givenName":"一郎","surname":"髙橋","mail":"ichiro.takahashi@example.co.jp","employeeId":"E0002","department":"旧営業部","accountEnabled":true,"businessPhones":[]},
              {"id":"aaa-2","displayName":"Taro Yamada","givenName":"Taro","surname":"Yamada","mail":"Taro.Yamada@example.co.jp","employeeId":null,"department":"第１営業部","accountEnabled":true},
              {"id":"aaa-3","displayName":"会議室 5F","givenName":null,"surname":null,"mail":"room-5f@example.co.jp","employeeId":null,"department":null,"accountEnabled":true},
              {"id":"aaa-4","displayName":"退職 太郎","givenName":"太郎","surname":"退職","mail":"taro.taishoku@example.co.jp","employeeId":"E0099","department":null,"accountEnabled":true}
            ]}
            """;

        var mapped = ConnectorMapping.Parse(Encoding.UTF8.GetBytes(mapping));
        var bytes = Encoding.UTF8.GetBytes(json);
        var result = await _db.Importer.ImportAsync(
            IngestDatabase.Worker,
            new ImportRequest
            {
                Table = SourceReader.Read(bytes, "users.json", mapped),
                Profile = mapped.Profile,
                FileName = "users.json",
                FileSha256 = new string('a', 64),
                MappingName = mapped.Name,
                MappingSha256 = mapped.Sha256,
                ExecutedByPersonId = _db.Id("hr1"),
            },
            Ct);
        var proposals = await _db.Store.ListProposalsAsync(_db.HrUser("hr2"), result.BatchId, Ct);

        string?[] Codes(Guid person) => [.. proposals.Where(p => p.TargetId == person && p.Operation == ProposalOperation.Supplement).Select(p => p.Basis.Code).Order()];
        Assert.Equal([SupplementCodes.DirectoryDepartmentMismatch], Codes(_db.Id("takahashi")));
        Assert.Equal([SupplementCodes.DirectoryEmployeeIdEmpty], Codes(_db.Id("head")));
        Assert.Equal([SupplementCodes.DirectoryRetiredEnabled], Codes(retired));
        Assert.Equal([SupplementCodes.DirectoryAccountMissing], Codes(_db.Id("member")));
        Assert.Single(proposals, p => p.Basis.Code == SupplementCodes.NonPersonAccount);
        Assert.All(proposals.Where(p => p.Operation == ProposalOperation.Supplement), p => Assert.Equal(ProposalStatus.NeedsReview, p.Status));

        foreach (var proposal in proposals.Where(p => p.Status == ProposalStatus.Proposed))
        {
            await _db.Review.ApproveAsync(_db.HrUser("hr2"), proposal.Id, Ct);
        }

        Assert.Equal(
            ["aaa-1", "aaa-2", "aaa-4"],
            await _db.QueryAsync<string>(null, "SELECT external_id FROM external_identity WHERE source_system = 'entra_id' ORDER BY external_id", Ct));
    }

    private static string Csv(params (DateOnly Date, string Number, string Name, string Text)[] rows) =>
        "発令日,社員番号,氏名,発令内容,備考\n" + string.Join("\n", rows.Select(r => $"{r.Date:yyyy/MM/dd},{r.Number},{r.Name},{r.Text},")) + "\n";

    private async Task<IReadOnlyList<ChangeProposalRecord>> ImportAndApproveAsync(string csv, string mapping)
    {
        var result = await _db.ImportCsvAsync(csv, mapping, Ct);
        var proposals = await _db.Store.ListProposalsAsync(_db.HrUser("hr2"), result.BatchId, Ct);
        foreach (var proposal in proposals.Where(p => p.Status == ProposalStatus.Proposed).OrderBy(p => p.EffectiveDate ?? DateOnly.MinValue).ThenBy(p => p.TargetKind == ProposalTargetKind.OrgUnit ? 0 : 1))
        {
            await _db.Review.ApproveAsync(_db.HrUser("hr2"), proposal.Id, Ct);
        }

        return proposals;
    }
}
