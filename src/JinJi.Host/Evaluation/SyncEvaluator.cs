using System.Globalization;
using System.Text;
using System.Text.Json;
using JinJi.Application.Audit;
using JinJi.Application.Sync;
using JinJi.Domain;
using JinJi.Infrastructure.Database;
using JinJi.Policy;
using JinJi.Sync;
using JinJi.Sync.DirectApply;
using Npgsql;

namespace JinJi.Host.Evaluation;

/// <summary>
/// 同期計画の評価: サンプル（messy-corp）を取り込み・承認した一時 DB（<see cref="IngestEvaluator"/>）から Entra ID への同期計画を作り、
/// 正解データ（ground_truth.json）の Entra の不整合に対して期待どおりの差分が出るかを測る。実テナントには接続しない。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>業務日付は正解データの基準日。起案者・承認者は評価用の人事管理者 2 人（取込の実行者・承認者とは別）</item>
/// <item>設定は <c>mappings/entra_sync.json</c>。評価用の人（EVAL*）は除外に足す（Entra にアカウントが無いので作成の推奨に出るため）</item>
/// <item>承認 → スクリプト一式の生成（<c>--script-output</c> で書き出す）→ メモリ上の Entra（<see cref="InMemoryDirectory"/>）へのドライランと直接反映 →
/// 反映後の一覧から計画を作り直し、差分が残らない（収束する）ことを確かめる。反映の前に 1 人の部署を変え、事前条件の不一致でスキップされることも確かめる</item>
/// </list>
/// </remarks>
internal sealed class SyncEvaluator
{
    private readonly string _directory;
    private readonly JsonElement _truth;
    private readonly DateOnly _asOf;

    public SyncEvaluator(string directory, JsonElement truth)
    {
        _directory = directory;
        _truth = truth;
        _asOf = DateOnly.ParseExact(truth.GetProperty("meta").GetProperty("as_of").GetString()!, "yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    public async Task<SyncEvaluationResult> RunAsync(NpgsqlDataSource dataSource, string? scriptOutput, CancellationToken cancellationToken)
    {
        await new IngestEvaluator(_directory, _truth).RunAsync(dataSource, cancellationToken);

        var time = new IngestEvaluator.FixedTimeProvider(_asOf);
        var audit = new AuditWriter(new NpgsqlAuditSink(dataSource), time);
        var service = new SyncPlanService(
            new NpgsqlSyncPlanStore(dataSource, time), new NpgsqlOrgSnapshotProvider(dataSource), new EntraSyncPlanner(), new EntraScriptRenderer(), new SyncLogParser(), audit, time);
        var proposer = ViewerContext.ForUser(await SeedHrAdminAsync(dataSource, "EVAL04", "同期起案者", cancellationToken), [Roles.HrAdmin], Channel.Ui, FieldLimit.Unrestricted);
        var approver = ViewerContext.ForUser(await SeedHrAdminAsync(dataSource, "EVAL05", "同期承認者", cancellationToken), [Roles.HrAdmin], Channel.Ui, FieldLimit.Unrestricted);

        var settingsPath = Path.Combine(_directory, "mappings", "entra_sync.json");
        var configured = SyncSettings.Parse(await File.ReadAllBytesAsync(settingsPath, cancellationToken));
        var settings = configured with
        {
            Exclude = [.. configured.Exclude, .. new[] { "EVAL01", "EVAL02", "EVAL03", "EVAL04", "EVAL05" }.Select(n => new SyncExclusion { EmployeeNumber = n, Note = "評価用の人（Entra にアカウントが無い）" })],
        };
        var content = await File.ReadAllBytesAsync(Path.Combine(_directory, "entra_users.json"), cancellationToken);
        var users = GraphDirectoryUsers.Parse(content);
        var created = await service.CreateAsync(
            proposer,
            new SyncPlanRequest(users, settings, "entra_users.json", Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(content))),
            cancellationToken);
        await service.ApproveAsync(approver, created.Plan.Id, cancellationToken);
        var bundle = await service.ExportScriptAsync(approver, created.Plan.Id, cancellationToken);
        if (scriptOutput is not null)
        {
            Directory.CreateDirectory(scriptOutput);
            foreach (var (name, bytes) in bundle.Files)
            {
                await File.WriteAllBytesAsync(Path.Combine(scriptOutput, name), bytes, cancellationToken);
            }
        }

        var result = new SyncEvaluationResult
        {
            AsOf = _asOf,
            Plan = created.Plan,
            Items = created.Items,
            Warnings = created.Warnings,
            ScriptFiles = [.. bundle.Files.Select(f => (f.Key, f.Value.Length))],
            DataSha256 = bundle.DataSha256,
        };
        Measure(result, users);

        // メモリ上の Entra へのドライラン → 1 人の部署を Entra 側で変える → 直接反映 → 作り直した計画が収束するか
        var memory = new InMemoryDirectory(users);
        var applier = new DirectApplier(service, memory, time);
        result.DryRun = await applier.ApplyAsync(approver, created.Plan.Id, whatIf: true, cancellationToken);
        if (created.Items.FirstOrDefault(i => i.InScript && i.Action == SyncAction.UpdateAttributes && i.Changes.Any(c => c.Field == EntraFields.Department)) is { } changed)
        {
            memory.Set(changed.ObjectId!, EntraFields.Department, "（計画の後に Entra で変えた部署）");
            result.ChangedAfterPlan = changed.Id;
        }

        result.Applied = await applier.ApplyAsync(approver, created.Plan.Id, whatIf: false, cancellationToken);
        result.Writes = memory.Writes;
        var replanned = await service.CreateAsync(
            proposer,
            new SyncPlanRequest(memory.Users, settings, "in-memory-after-apply.json", new string('0', 64)),
            cancellationToken);
        result.Replanned = replanned.Items;
        return result;
    }

    // -----------------------------------------------------------------------
    // 正解データとの比較
    // -----------------------------------------------------------------------

    private void Measure(SyncEvaluationResult result, IReadOnlyList<DirectoryUser> users)
    {
        var objectByIndex = _truth.GetProperty("mappings").GetProperty("entra_users.json").EnumerateArray()
            .ToDictionary(m => m.GetProperty("index").GetInt32(), m => m.GetProperty("object_id").GetString()!);
        var objectByPerson = _truth.GetProperty("mappings").GetProperty("entra_users.json").EnumerateArray()
            .Where(m => m.TryGetProperty("person_id", out var p) && p.ValueKind == JsonValueKind.String)
            .GroupBy(m => m.GetProperty("person_id").GetString()!)
            .ToDictionary(g => g.Key, g => g.Select(m => m.GetProperty("object_id").GetString()!).ToList());
        var persons = _truth.GetProperty("persons").EnumerateArray().ToDictionary(p => p.GetProperty("person_id").GetString()!);
        var orgs = _truth.GetProperty("orgs").EnumerateArray().ToDictionary(o => o.GetProperty("org_id").GetString()!);
        var anomalies = _truth.GetProperty("anomalies").EnumerateArray().Where(a => a.GetProperty("file").GetString() == "entra_users.json").ToList();
        var usersById = users.ToDictionary(u => u.ObjectId, StringComparer.Ordinal);
        var items = result.Items;

        HashSet<string> Expected(string category) =>
            [.. anomalies.Where(a => a.GetProperty("category").GetString() == category && a.TryGetProperty("index", out _))
                .Select(a => objectByIndex[a.GetProperty("index").GetInt32()])];

        HashSet<string> Found(Func<SyncPlanItem, bool> predicate) => [.. items.Where(i => i.ObjectId is not null && predicate(i)).Select(i => i.ObjectId!)];

        void Add(string label, string category, HashSet<string> expected, HashSet<string> found, string note) =>
            result.Checks.Add(new SyncCheck(label, category, expected.Count, expected.Count(found.Contains), found.Count(f => !expected.Contains(f)), note));

        // 退職者の無効化（ENTRA_RETIRED_ENABLED）
        Add("退職者のアカウントの無効化（critical）", "ENTRA_RETIRED_ENABLED", Expected("ENTRA_RETIRED_ENABLED"),
            Found(i => i.Action == SyncAction.DisableAccount && i.InScript), "無効化の項目（スクリプトに含める）");

        // 一時停止者（休職）のサインイン停止
        var leave = persons.Values.Where(p => p.GetProperty("status").GetString() == "leave")
            .SelectMany(p => objectByPerson.GetValueOrDefault(p.GetProperty("person_id").GetString()!) ?? [])
            .Where(o => usersById.TryGetValue(o, out var u) && u.AccountEnabled == true)
            .ToHashSet(StringComparer.Ordinal);
        Add("一時停止者のサインイン停止（critical）", "休職中（status = leave）でアカウントが有効", leave,
            Found(i => i.Action == SyncAction.BlockSignIn && i.InScript), "サインイン停止の項目（スクリプトに含める）");

        // 旧組織名（ENTRA_STALE_DEPT）: 部署の更新があり、あるべき値が正解の勤務先の組織名
        var stale = Expected("ENTRA_STALE_DEPT");
        var departmentCorrect = 0;
        foreach (var anomaly in anomalies.Where(a => a.GetProperty("category").GetString() == "ENTRA_STALE_DEPT"))
        {
            var objectId = objectByIndex[anomaly.GetProperty("index").GetInt32()];
            var person = persons[anomaly.GetProperty("person_id").GetString()!];
            var orgId = person.GetProperty("secondment").ValueKind == JsonValueKind.Object
                ? person.GetProperty("secondment").GetProperty("org_id").GetString()!
                : person.GetProperty("home_org_id").GetString()!;
            var expectedName = Key(orgs[orgId].GetProperty("name").GetString());
            if (items.Any(i => i.ObjectId == objectId && i.Action == SyncAction.UpdateAttributes
                && i.Changes.Any(c => c.Field == EntraFields.Department && Key(c.Desired) == expectedName)))
            {
                departmentCorrect++;
            }
        }

        var departmentFound = Found(i => i.Action == SyncAction.UpdateAttributes && i.Changes.Any(c => c.Field == EntraFields.Department));
        result.Checks.Add(new SyncCheck("旧組織名の部署の更新", "ENTRA_STALE_DEPT", stale.Count, departmentCorrect, 0,
            $"あるべき値が正解の勤務先の組織名と一致したもの。部署の更新は全体で {departmentFound.Count} 件（旧組織名以外は表記の違い: 半角カナ・末尾の空白・略記など）"));

        // employeeId が空（ENTRA_EMPLOYEEID_EMPTY）: 正解の社員番号を設定する
        var emptyExpected = Expected("ENTRA_EMPLOYEEID_EMPTY");
        var emptyFound = new HashSet<string>(StringComparer.Ordinal);
        foreach (var anomaly in anomalies.Where(a => a.GetProperty("category").GetString() == "ENTRA_EMPLOYEEID_EMPTY"))
        {
            var objectId = objectByIndex[anomaly.GetProperty("index").GetInt32()];
            var number = persons[anomaly.GetProperty("person_id").GetString()!].GetProperty("employee_no").GetString();
            if (items.Any(i => i.ObjectId == objectId && i.Changes.Any(c => c.Field == EntraFields.EmployeeId && c.Current is null && c.Desired == number)))
            {
                emptyFound.Add(objectId);
            }
        }

        var employeeIdFromEmpty = Found(i => i.Changes.Any(c => c.Field == EntraFields.EmployeeId && c.Current is null));
        result.Checks.Add(new SyncCheck("空の employeeId の設定", "ENTRA_EMPLOYEEID_EMPTY", emptyExpected.Count, emptyFound.Count,
            employeeIdFromEmpty.Count(o => !emptyExpected.Contains(o)), "現在値が空で、あるべき値が正解の社員番号のもの"));

        // 人ではないアカウント（ENTRA_NON_PERSON）: 除外し、スクリプトに含めない
        Add("人ではないアカウントの除外", "ENTRA_NON_PERSON", Expected("ENTRA_NON_PERSON"),
            Found(i => i.Action == SyncAction.ExcludedAccount && i.Basis.Code == "non_person" && !i.InScript), "参考の項目（スクリプトに含めない）");

        // アカウントの無い在籍者（ENTRA_MISSING_ACCOUNT）: 作成の推奨（スクリプトに含めない）
        var missing = anomalies.Where(a => a.GetProperty("category").GetString() == "ENTRA_MISSING_ACCOUNT")
            .Select(a => persons[a.GetProperty("person_id").GetString()!].GetProperty("employee_no").GetString()!)
            .ToHashSet(StringComparer.Ordinal);
        var creates = items.Where(i => i.Action == SyncAction.CreateUser).ToList();
        result.Checks.Add(new SyncCheck("アカウントの作成の推奨", "ENTRA_MISSING_ACCOUNT", missing.Count,
            creates.Count(c => c.EmployeeNumber is { } n && missing.Contains(n) && !c.InScript), creates.Count(c => c.EmployeeNumber is not { } n || !missing.Contains(n)),
            "作成の推奨（スクリプトに含めない）"));

        // 表示名の逆順・ローマ字（ENTRA_NAME_REVERSED・ENTRA_NAME_ROMAJI）: 表示名の更新
        foreach (var category in new[] { "ENTRA_NAME_REVERSED", "ENTRA_NAME_ROMAJI" })
        {
            var expected = Expected(category);
            var found = Found(i => i.Changes.Any(c => c.Field == EntraFields.DisplayName));
            found.IntersectWith(expected);
            Add("表示名の更新", category, expected, found, "表示名（姓 名）の更新があるもの（誤検出は数えない: 表記の違いの更新もあるため）");
        }

        // 内示（未来日付の辞令）を反映していない
        var violations = 0;
        var checkedOrders = 0;
        foreach (var order in _truth.GetProperty("pending_orders").EnumerateArray())
        {
            var personId = order.GetProperty("person_id").GetString()!;
            foreach (var objectId in objectByPerson.GetValueOrDefault(personId) ?? [])
            {
                checkedOrders++;
                var person = persons[personId];
                var currentOrg = person.GetProperty("secondment").ValueKind == JsonValueKind.Object
                    ? person.GetProperty("secondment").GetProperty("org_id").GetString()
                    : person.GetProperty("home_org_id").GetString();
                var target = order.TryGetProperty("target_org_id", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
                var title = order.TryGetProperty("title", out var tt) && tt.ValueKind == JsonValueKind.String ? tt.GetString() : null;
                foreach (var item in items.Where(i => i.ObjectId == objectId))
                {
                    if (target is not null && target != currentOrg
                        && item.Changes.Any(c => c.Field == EntraFields.Department && Key(c.Desired) == Key(orgs[target].GetProperty("name").GetString())))
                    {
                        violations++;
                    }

                    var currentTitle = person.GetProperty("title").ValueKind == JsonValueKind.String ? person.GetProperty("title").GetString() : null;
                    if (title is not null && title != currentTitle && item.Changes.Any(c => c.Field == EntraFields.JobTitle && c.Desired == title))
                    {
                        violations++;
                    }
                }
            }
        }

        result.PendingOrdersChecked = checkedOrders;
        result.PendingOrderViolations = violations;
    }

    /// <summary>組織名の比較用（NFKC・空白の除去）。</summary>
    private static string Key(string? value) =>
        new([.. (value ?? string.Empty).Normalize(NormalizationForm.FormKC).Where(c => !char.IsWhiteSpace(c))]);

    private static async Task<Guid> SeedHrAdminAsync(NpgsqlDataSource dataSource, string number, string given, CancellationToken cancellationToken)
    {
        var id = EntityId.New();
        await using var tx = await ViewerTransaction.BeginAsync(dataSource, ViewerContext.ForSystem(Channel.Cli), cancellationToken);
        await using (var command = tx.CreateCommand("INSERT INTO person (id, legal_entity_id, employee_number) SELECT $1, id, $2 FROM legal_entity ORDER BY code DESC LIMIT 1"))
        {
            command.Parameters.Add(new NpgsqlParameter { Value = id });
            command.Parameters.Add(new NpgsqlParameter { Value = number });
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var command = tx.CreateCommand(
            """
            INSERT INTO person_version (id, person_id, family_name, given_name, display_name, search_name, employment_type, hire_date, valid_period)
            VALUES ($1, $2, '評価', $3, $4, $5, 'other', '2000-01-01', daterange('2000-01-01', NULL))
            """))
        {
            command.Parameters.Add(new NpgsqlParameter { Value = EntityId.New() });
            command.Parameters.Add(new NpgsqlParameter { Value = id });
            command.Parameters.Add(new NpgsqlParameter { Value = given });
            command.Parameters.Add(new NpgsqlParameter { Value = $"評価 {given}" });
            command.Parameters.Add(new NpgsqlParameter { Value = $"評価{given}" });
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);
        return id;
    }
}

/// <summary>正解データとの比較の 1 行。</summary>
/// <param name="Expected">正解の件数。</param>
/// <param name="Found">正解のうち計画に期待どおりに出た件数。</param>
/// <param name="FalsePositives">計画に出たが正解に無い件数。</param>
internal sealed record SyncCheck(string Label, string Category, int Expected, int Found, int FalsePositives, string Note);

/// <summary>同期計画の評価の結果。</summary>
internal sealed class SyncEvaluationResult
{
    public DateOnly AsOf { get; init; }

    public required SyncPlanRecord Plan { get; init; }

    public required IReadOnlyList<SyncPlanItem> Items { get; init; }

    public required IReadOnlyList<string> Warnings { get; init; }

    public required IReadOnlyList<(string Name, int Bytes)> ScriptFiles { get; init; }

    public required string DataSha256 { get; init; }

    public List<SyncCheck> Checks { get; } = [];

    public int PendingOrdersChecked { get; set; }

    public int PendingOrderViolations { get; set; }

    public SyncResultImport? DryRun { get; set; }

    public SyncResultImport? Applied { get; set; }

    public Guid? ChangedAfterPlan { get; set; }

    public int Writes { get; set; }

    public IReadOnlyList<SyncPlanItem> Replanned { get; set; } = [];
}
