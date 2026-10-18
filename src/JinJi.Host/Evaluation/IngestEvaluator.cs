using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using JinJi.Application;
using JinJi.Application.Audit;
using JinJi.Application.Ingest;
using JinJi.Connectors;
using JinJi.Domain;
using JinJi.Domain.Organization;
using JinJi.Infrastructure.Database;
using JinJi.Ingest;
using JinJi.Ingest.Normalization;
using JinJi.Ingest.Suggestions;
using JinJi.Policy;
using Npgsql;

namespace JinJi.Host.Evaluation;

/// <summary>
/// 取込の精度の評価: 空の一時 DB にサンプル（messy-corp）の 5 ファイルを順に取り込み、起案の提案を評価用に一括承認し、結果を正解データ（ground_truth.json）と比べる。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>取込の順: 組織図 → 人事パッケージ → 組織図（2 回目。人が揃ってから責任者を引き当てる）→ 辞令 → Entra ID</item>
/// <item>業務日付は正解データの基準日（meta.as_of）に固定する。承認は実行者とは別の評価用の人事管理者が行う。要確認の提案は承認しない</item>
/// <item>一括承認は有効日の順に行い、上位組織の未承認などで反映できなかった提案は、進まなくなるまで繰り返す</item>
/// <item>LLM（--llm）を使うときは、どの取込も LLM の候補提示にオプトインする。LLM は提案の状態・値を変えないので、ルールの指標は LLM の有無で変わらない</item>
/// <item>最後に、評価用の解決者（実行者・承認者とは別の人事管理者）が辞令の要確認を正解データに従って解決し（正解が候補にあれば選び、無ければ修正依頼に回す）、
/// 解決の結果の提案を承認者が承認する。人の確認の代わりで、解決の流れと作業領域の氏名の消去を確かめる</item>
/// </list>
/// </remarks>
internal sealed class IngestEvaluator
{
    private static readonly (string Label, string Mapping, string File)[] Steps =
    [
        ("拠点マスタ", "sites.json", "sites.csv"),
        ("組織図", "org_chart.json", "org_chart_2026-04.csv"),
        ("人事パッケージ", "employees_hr_package.json", "employees_hr_package.csv"),
        ("組織図（2 回目）", "org_chart.json", "org_chart_2026-04.csv"),
        ("辞令", "jirei_2026.json", "jirei_2026.csv"),
        ("Entra ID", "entra_users.json", "entra_users.json"),
    ];

    private readonly string _directory;
    private readonly JsonElement _truth;
    private readonly DateOnly _asOf;
    private readonly Dictionary<string, JsonElement> _gtPersons;
    private readonly Dictionary<string, JsonElement> _gtOrgs;
    private readonly ProposalSuggester? _suggester;

    /// <param name="suggester">LLM による候補提示（--llm のときだけ）。</param>
    public IngestEvaluator(string directory, JsonElement truth, ProposalSuggester? suggester = null)
    {
        _directory = directory;
        _suggester = suggester;
        _truth = truth;
        _asOf = DateOnly.ParseExact(truth.GetProperty("meta").GetProperty("as_of").GetString()!, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        _gtPersons = truth.GetProperty("persons").EnumerateArray().ToDictionary(p => p.GetProperty("person_id").GetString()!);
        _gtOrgs = truth.GetProperty("orgs").EnumerateArray().ToDictionary(o => o.GetProperty("org_id").GetString()!);
    }

    public DateOnly AsOf => _asOf;

    public async Task<EvaluationResult> RunAsync(NpgsqlDataSource dataSource, CancellationToken cancellationToken)
    {
        var time = new FixedTimeProvider(_asOf);
        var store = new NpgsqlIngestStore(dataSource, time);
        var snapshots = new NpgsqlOrgSnapshotProvider(dataSource);
        var audit = new AuditWriter(new NpgsqlAuditSink(dataSource), time);
        var importer = new ImportService(store, snapshots, audit, time, _suggester);
        var review = new ProposalReviewService(store, snapshots, audit, time);
        var resolution = new ProposalResolutionService(store, snapshots, audit, time);

        var actors = await SeedAsync(dataSource, time, cancellationToken);
        var approver = ViewerContext.ForUser(actors.Approver, [Roles.HrAdmin], Channel.Ui, FieldLimit.Unrestricted);
        var result = new EvaluationResult { AsOf = _asOf, LlmUsed = _suggester is not null };

        foreach (var (label, mappingFile, file) in Steps)
        {
            var mappingBytes = await File.ReadAllBytesAsync(Path.Combine(_directory, "mappings", mappingFile), cancellationToken);
            var mapping = ConnectorMapping.Parse(mappingBytes);
            var content = await File.ReadAllBytesAsync(Path.Combine(_directory, file), cancellationToken);
            var imported = await importer.ImportAsync(
                ViewerContext.ForSystem(Channel.Cli),
                new ImportRequest
                {
                    Table = SourceReader.Read(content, file, mapping),
                    Profile = mapping.Profile,
                    FileName = file,
                    FileSha256 = Convert.ToHexStringLower(SHA256.HashData(content)),
                    MappingName = mapping.Name,
                    MappingSha256 = mapping.Sha256,
                    ExecutedByPersonId = actors.Executor,
                    UseLlm = _suggester is not null,
                },
                cancellationToken);

            var proposals = await store.ListProposalsAsync(approver, imported.BatchId, cancellationToken);
            var (applied, failures) = await ApproveAllAsync(review, approver, proposals, cancellationToken);
            var corrections = await store.ListCorrectionsAsync(approver, imported.BatchId, cancellationToken);
            result.Steps.Add(new StepResult(label, file, imported, proposals, corrections, applied, failures));
        }

        var snapshot = await snapshots.LoadAsync(approver, _asOf, _asOf, cancellationToken);
        Measure(result, snapshot, actors);
        var (_, _, legalBefore) = await MeasureForbiddenAsync(result, dataSource, approver, cancellationToken);
        result.LegalNamesFound = legalBefore.Count;
        result.LegalNamesFoundIn.AddRange(legalBefore);
        await MeasurePendingAsync(result, dataSource, approver, snapshot, cancellationToken);
        MeasureLlm(result);

        // 評価用の解決者による要確認の解決（人の確認の代わり）と、その後の作業領域の氏名。
        // 解決者は取込の後に登録する（先に登録すると、Entra ID の全件の取込で「アカウントの無い在籍者」に数えられるため）
        var resolverId = await SeedPersonAsync(dataSource, _truth.GetProperty("companies")[0].GetProperty("id").GetString()!, "EVAL03", "解決者", cancellationToken);
        var resolver = ViewerContext.ForUser(resolverId, [Roles.HrAdmin], Channel.Ui, FieldLimit.Unrestricted);
        await ResolveForEvaluationAsync(result, store, resolution, review, resolver, approver, cancellationToken);
        var (_, _, legalAfter) = await MeasureForbiddenAsync(result, dataSource, approver, cancellationToken);
        result.LegalNamesFoundAfterResolution.AddRange(legalAfter);
        return result;
    }

    // -----------------------------------------------------------------------
    // 準備と一括承認
    // -----------------------------------------------------------------------

    private sealed record Actors(Guid Executor, Guid Approver);

    /// <summary>法人（正解データの会社）・評価用の人事管理者 2 人（実行者・承認者）・法人ごとの段の定義を登録する。</summary>
    private async Task<Actors> SeedAsync(NpgsqlDataSource dataSource, TimeProvider time, CancellationToken cancellationToken)
    {
        var system = ViewerContext.ForSystem(Channel.Cli);
        var companies = _truth.GetProperty("companies").EnumerateArray().Select(c => (Code: c.GetProperty("id").GetString()!, Name: c.GetProperty("name").GetString()!)).ToList();
        var entities = companies.ToDictionary(c => c.Code, _ => EntityId.New());
        var executor = EntityId.New();
        var approver = EntityId.New();
        await using (var tx = await ViewerTransaction.BeginAsync(dataSource, system, cancellationToken))
        {
            foreach (var (code, name) in companies)
            {
                await ExecAsync(tx, "INSERT INTO legal_entity (id, code, name) VALUES ($1, $2, $3)", cancellationToken, entities[code], code, name);
            }

            var first = entities[companies[0].Code];
            foreach (var (id, number, given) in new[] { (executor, "EVAL01", "実行者"), (approver, "EVAL02", "承認者") })
            {
                await ExecAsync(tx, "INSERT INTO person (id, legal_entity_id, employee_number) VALUES ($1, $2, $3)", cancellationToken, id, first, number);
                await ExecAsync(
                    tx,
                    """
                    INSERT INTO person_version (id, person_id, family_name, given_name, display_name, search_name, employment_type, hire_date, valid_period)
                    VALUES ($1, $2, '評価', $3, $4, $5, 'other', '2000-01-01', daterange('2000-01-01', NULL))
                    """,
                    cancellationToken,
                    EntityId.New(),
                    id,
                    given,
                    $"評価 {given}",
                    $"評価{given}");
            }

            await tx.CommitAsync(cancellationToken);
        }

        // 段の定義（正解データの notes: 親会社 本部 > 部・室 > 課 > チーム、子会社 部 > グループ）
        var levels = new NpgsqlOrgLevelStore(dataSource, time);
        var proposer = ViewerContext.ForUser(executor, [Roles.HrAdmin], Channel.Ui, FieldLimit.Unrestricted);
        var decider = ViewerContext.ForUser(approver, [Roles.HrAdmin], Channel.Ui, FieldLimit.Unrestricted);
        var schemes = new Dictionary<string, OrgLevel[]>
        {
            ["P"] = [new("division", "本部", 1), new("department", "部", 2), new("section", "課", 3), new("team", "チーム", 4)],
            ["L"] = [new("department", "部", 1), new("group", "グループ", 2)],
        };
        foreach (var (code, scheme) in schemes.Where(s => entities.ContainsKey(s.Key)))
        {
            var id = EntityId.New();
            await levels.InsertProposalAsync(proposer, id, new OrgLevelScheme(entities[code], scheme), cancellationToken);
            await levels.ApproveAsync(decider, id, cancellationToken);
        }

        return new Actors(executor, approver);
    }

    /// <summary>評価用の人事管理者（人）を 1 人登録する。</summary>
    private static async Task<Guid> SeedPersonAsync(NpgsqlDataSource dataSource, string entityCode, string number, string given, CancellationToken cancellationToken)
    {
        var id = EntityId.New();
        await using var tx = await ViewerTransaction.BeginAsync(dataSource, ViewerContext.ForSystem(Channel.Cli), cancellationToken);
        await ExecAsync(tx, "INSERT INTO person (id, legal_entity_id, employee_number) SELECT $1, id, $2 FROM legal_entity WHERE code = $3", cancellationToken, id, number, entityCode);
        await ExecAsync(
            tx,
            """
            INSERT INTO person_version (id, person_id, family_name, given_name, display_name, search_name, employment_type, hire_date, valid_period)
            VALUES ($1, $2, '評価', $3, $4, $5, 'other', '2000-01-01', daterange('2000-01-01', NULL))
            """,
            cancellationToken,
            EntityId.New(),
            id,
            given,
            $"評価 {given}",
            $"評価{given}");
        await tx.CommitAsync(cancellationToken);
        return id;
    }

    private static async Task<(int Applied, Dictionary<string, int> Failures)> ApproveAllAsync(
        ProposalReviewService review,
        ViewerContext approver,
        IReadOnlyList<ChangeProposalRecord> proposals,
        CancellationToken cancellationToken)
    {
        var pending = proposals
            .Where(p => p.Status == ProposalStatus.Proposed)
            .OrderBy(p => p.EffectiveDate ?? DateOnly.MinValue)
            .ThenBy(p => p.TargetKind == ProposalTargetKind.OrgUnit ? 0 : 1)
            .ThenBy(p => p.Id)
            .ToList();
        var applied = 0;
        var failures = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var pass = 0; pass < 10 && pending.Count > 0; pass++)
        {
            var next = new List<ChangeProposalRecord>();
            var lastErrors = new Dictionary<Guid, string>();
            foreach (var proposal in pending)
            {
                try
                {
                    await review.ApproveAsync(approver, proposal.Id, cancellationToken);
                    applied++;
                }
                catch (Exception ex) when (ex is ProposalConflictException or OrgLevelValidationException or InvalidOperationException or PostgresException or AccessDeniedException)
                {
                    next.Add(proposal);
                    lastErrors[proposal.Id] = $"{ex.GetType().Name}: {Shorten(ex.Message)}";
                }
            }

            if (next.Count == pending.Count)
            {
                foreach (var error in lastErrors.Values)
                {
                    failures[error] = failures.GetValueOrDefault(error) + 1;
                }

                break;
            }

            pending = next;
            if (pending.Count > 0 && pass == 9)
            {
                foreach (var error in lastErrors.Values)
                {
                    failures[error] = failures.GetValueOrDefault(error) + 1;
                }
            }
        }

        return (applied, failures);
    }

    /// <summary>エラーメッセージから ID・日付を除いて分類しやすくする。</summary>
    private static string Shorten(string message)
    {
        var text = System.Text.RegularExpressions.Regex.Replace(message, "[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}", "<id>");
        text = System.Text.RegularExpressions.Regex.Replace(text, "[0-9]{4}-[0-9]{2}-[0-9]{2}", "<date>");
        return text.Length > 120 ? text[..120] : text;
    }

    private static async Task ExecAsync(ViewerTransaction tx, string sql, CancellationToken cancellationToken, params object[] parameters)
    {
        await using var command = tx.CreateCommand(sql);
        foreach (var parameter in parameters)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = parameter });
        }

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    // -----------------------------------------------------------------------
    // 測定
    // -----------------------------------------------------------------------

    private static readonly EmployeeNumberNormalizer Numbers = new(new EmployeeNumberRules { PadLength = 5, PrefixedPadLength = 4 });

    private static readonly Dictionary<string, string?> KindLevels = new(StringComparer.Ordinal)
    {
        ["本部"] = "division",
        ["部"] = "department",
        ["課"] = "section",
        ["チーム"] = "team",
        ["グループ"] = "group",
        ["室"] = null,
    };

    private void Measure(EvaluationResult result, OrgSnapshot snapshot, Actors actors)
    {
        var entityIds = snapshot.LegalEntities.ToDictionary(e => e.Code, e => e.Id);

        // 人: 社員番号の正規形で対応付ける
        var personByNumber = snapshot.People.Where(p => p.Id != actors.Executor && p.Id != actors.Approver)
            .GroupBy(p => Numbers.Normalize(p.EmployeeNumber)!)
            .ToDictionary(g => g.Key, g => g.ToList());
        var gtOfPerson = new Dictionary<Guid, string>();
        foreach (var (gtId, gt) in _gtPersons)
        {
            if (personByNumber.TryGetValue(gt.GetProperty("employee_no").GetString()!, out var found) && found.Count == 1)
            {
                gtOfPerson[found[0].Id] = gtId;
            }
        }

        result.PersonsExpected = _gtPersons.Count;
        result.PersonsCreated = personByNumber.Sum(g => g.Value.Count);
        result.PersonsMatchedToTruth = gtOfPerson.Count;
        result.PersonsDuplicated = personByNumber.Count(g => g.Value.Count > 1);

        // 組織: コード（無ければ観測された名称）で対応付ける
        var orgOfGt = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var (gtId, gt) in _gtOrgs)
        {
            if (!entityIds.TryGetValue(gt.GetProperty("company").GetString()!, out var entity))
            {
                continue;
            }

            var units = snapshot.Units.Where(u => u.LegalEntityId == entity).ToList();
            OrgUnit? found = null;
            if (gt.GetProperty("code").GetString() is { } code)
            {
                found = units.SingleOrDefault(u => string.Equals(TextNormalizer.Nfkc(u.Code), TextNormalizer.Nfkc(code), StringComparison.OrdinalIgnoreCase));
            }
            else
            {
                var keys = gt.GetProperty("observed_names").EnumerateArray().Select(n => OrgNameNormalizer.ToSearchKey(n.GetString())).Append(OrgNameNormalizer.ToSearchKey(gt.GetProperty("name").GetString())).ToHashSet();
                var byName = units.Where(u => keys.Contains(OrgNameNormalizer.ToSearchKey(u.Name))).ToList();
                found = byName.Count == 1 ? byName[0] : null;
            }

            if (found is not null)
            {
                orgOfGt[gtId] = found.Id;
            }
        }

        var gtOfOrg = orgOfGt.ToDictionary(kv => kv.Value, kv => kv.Key);
        foreach (var (id, gt) in gtOfPerson)
        {
            result.GtOfPerson[id] = gt;
        }

        foreach (var (id, gt) in gtOfOrg)
        {
            result.GtOfOrg[id] = gt;
        }

        foreach (var (gtId, gt) in _gtOrgs)
        {
            var active = gt.GetProperty("active").GetBoolean();
            var check = new OrgCheck(gtId, gt.GetProperty("name").GetString()!, active);
            if (!active)
            {
                check.Exists = orgOfGt.ContainsKey(gtId);
                check.Correct = !check.Exists;
            }
            else if (orgOfGt.TryGetValue(gtId, out var unitId))
            {
                var unit = snapshot.FindUnit(unitId)!;
                check.Exists = true;
                var gtParent = gt.GetProperty("parent_id").GetString();
                Guid? expectedParent = gtParent is null ? null : orgOfGt.TryGetValue(gtParent, out var p) ? p : Guid.Empty;
                check.ParentCorrect = unit.ParentId == expectedParent;
                var kind = gt.GetProperty("kind").GetString()!;
                check.LevelCorrect = KindLevels.TryGetValue(kind, out var level) && unit.LevelKey == level;
                var gtHead = gt.GetProperty("head_person_id").GetString();
                check.HeadCorrect = gtHead is null ? unit.HeadPersonId is null : unit.HeadPersonId is { } h && gtOfPerson.GetValueOrDefault(h) == gtHead;
                check.Correct = check.ParentCorrect && check.LevelCorrect;
            }

            result.Orgs.Add(check);
        }

        result.UnmatchedJinjiOrgs = snapshot.Units.Count(u => !gtOfOrg.ContainsKey(u.Id));

        // 人の最終状態（基準日時点）
        foreach (var (gtId, gt) in _gtPersons)
        {
            var person = gtOfPerson.FirstOrDefault(kv => kv.Value == gtId).Key;
            var record = person == Guid.Empty ? null : snapshot.FindPerson(person);
            var check = new PersonCheck(gtId, gt.GetProperty("status").GetString()!) { Exists = record is not null };
            if (record is not null)
            {
                var assignments = snapshot.AssignmentsOf(record.Id);
                var primary = assignments.FirstOrDefault(a => a.Type == AssignmentType.Primary);
                var retired = check.Status == "retired";
                check.RetiredCorrect = retired == record.IsTerminatedOn(_asOf.AddDays(1)) && (!retired || assignments.Count == 0);
                check.LeaveCorrect = (check.Status == "leave") == record.IsSuspended;
                if (!retired)
                {
                    var home = gt.GetProperty("home_org_id").GetString();
                    check.HomeOrgCorrect = primary is not null && home is not null && gtOfOrg.GetValueOrDefault(primary.OrgUnitId) == home;
                    var title = gt.GetProperty("title").GetString();
                    check.TitleCorrect = primary is not null && (primary.PositionTitle ?? string.Empty) == (title ?? string.Empty);
                    var gtConcurrent = gt.GetProperty("concurrent").EnumerateArray().Select(c => c.GetProperty("org_id").GetString()!).Order().ToList();
                    var ourConcurrent = assignments.Where(a => a.Type == AssignmentType.Concurrent && a.Secondment == SecondmentType.None)
                        .Select(a => gtOfOrg.GetValueOrDefault(a.OrgUnitId) ?? "?").Order().ToList();
                    check.ConcurrentCorrect = gtConcurrent.SequenceEqual(ourConcurrent);
                    var gtSecondment = gt.GetProperty("secondment").ValueKind == JsonValueKind.Object ? gt.GetProperty("secondment").GetProperty("org_id").GetString() : null;
                    var ourSecondment = assignments.Where(a => a.Secondment == SecondmentType.Outbound).Select(a => gtOfOrg.GetValueOrDefault(a.OrgUnitId) ?? "?").ToList();
                    check.SecondmentCorrect = gtSecondment is null ? ourSecondment.Count == 0 : ourSecondment.SequenceEqual([gtSecondment]);
                }
                else
                {
                    check.HomeOrgCorrect = check.TitleCorrect = check.ConcurrentCorrect = check.SecondmentCorrect = true;
                }
            }

            result.People.Add(check);
        }

        MeasureMatching(result, gtOfPerson);
        MeasureActions(result, gtOfOrg);
        MeasureDirectory(result, gtOfPerson);
        MeasureSites(result, snapshot, entityIds, orgOfGt, gtOfPerson);
    }

    /// <summary>拠点マスタ・組織の拠点・人の拠点（上書き → 主務の組織の拠点）・リモートワークを正解と比べる。</summary>
    private void MeasureSites(EvaluationResult result, OrgSnapshot snapshot, Dictionary<string, Guid> entityIds, Dictionary<string, Guid> orgOfGt, Dictionary<Guid, string> gtOfPerson)
    {
        var stats = result.Sites;
        var sitesByCode = new Dictionary<string, Site>(StringComparer.Ordinal);
        foreach (var gt in _truth.GetProperty("sites").EnumerateArray())
        {
            stats.SitesExpected++;
            var code = gt.GetProperty("code").GetString()!;
            var entity = entityIds.GetValueOrDefault(gt.GetProperty("company").GetString()!);
            if (snapshot.Sites.SingleOrDefault(s => s.LegalEntityId == entity && s.Code == code) is not { } site)
            {
                stats.Wrong.Add($"拠点 {code}: 無い");
                continue;
            }

            sitesByCode[code] = site;
            if (site.Name == gt.GetProperty("name").GetString() && SiteCodes.ToCode(site.Type) == gt.GetProperty("type").GetString()
                && site.PrefectureCode == gt.GetProperty("prefecture_code").GetString() && site.Location == gt.GetProperty("location").GetString())
            {
                stats.SitesCorrect++;
            }
            else
            {
                stats.Wrong.Add($"拠点 {code}: 値が違う");
            }
        }

        var closed = _truth.GetProperty("closed_sites").EnumerateArray().Select(c => c.GetProperty("code").GetString()).ToHashSet();
        stats.ClosedNotCreated = !snapshot.Sites.Any(s => closed.Contains(s.Code));

        foreach (var (gtId, gt) in _gtOrgs.Where(o => o.Value.GetProperty("active").GetBoolean()))
        {
            stats.OrgsExpected++;
            var expected = gt.GetProperty("site_code").GetString();
            var actual = orgOfGt.TryGetValue(gtId, out var unitId) && snapshot.FindUnit(unitId)?.SiteId is { } siteId ? snapshot.FindSite(siteId)?.Code : null;
            if (expected == actual)
            {
                stats.OrgsCorrect++;
            }
            else
            {
                stats.Wrong.Add($"組織 {gtId}: 拠点 {actual ?? "なし"}（正: {expected ?? "なし"}）");
            }
        }

        foreach (var (personId, gtId) in gtOfPerson)
        {
            var gt = _gtPersons[gtId];
            if (gt.GetProperty("status").GetString() == "retired" || snapshot.FindPerson(personId) is not { } person)
            {
                continue;
            }

            stats.PeopleExpected++;
            var expected = gt.GetProperty("site_code").GetString();
            var actual = snapshot.SiteOf(personId, person.Current)?.Site.Code;
            if (expected == actual)
            {
                stats.PeopleCorrect++;
            }
            else
            {
                stats.Wrong.Add($"人 {gtId}: 拠点 {actual ?? "なし"}（正: {expected}）");
            }

            var shouldOverride = gt.GetProperty("site_override").GetBoolean();
            var overridden = person.Current.SiteId is not null;
            stats.OverridesExpected += shouldOverride ? 1 : 0;
            stats.OverridesFound += shouldOverride && overridden ? 1 : 0;
            if (!shouldOverride && overridden)
            {
                stats.OverridesUnexpected++;
                stats.Wrong.Add($"人 {gtId}: 主務の組織と同じ拠点を人ごとにも持つ");
            }
            stats.RemoteCorrect += person.Current.RemoteWork == gt.GetProperty("remote_work").GetBoolean() ? 1 : 0;
        }

        // 正解データの行番号はヘッダーを除く 1 始まり、取込の行番号は CSV の行（ヘッダーが 1 行目）
        var unknownRows = _truth.GetProperty("anomalies").EnumerateArray()
            .Where(a => a.GetProperty("category").GetString() == "SITE_UNKNOWN")
            .Select(a => a.GetProperty("row").GetInt32() + 1)
            .ToList();
        var hr = result.Steps.First(s => s.File == "employees_hr_package.csv");
        stats.UnknownExpected = unknownRows.Count;
        stats.UnknownDetected = unknownRows.Count(row => hr.Corrections.Any(c => c.SourceRowNumber == row && c.Issue == CorrectionIssue.UnknownSite));
    }

    /// <summary>人の名寄せ（辞令・Entra ID の行 → 人）の適合率・再現率と、要確認に回った行。</summary>
    private void MeasureMatching(EvaluationResult result, Dictionary<Guid, string> gtOfPerson)
    {
        var hard = HardRows();
        foreach (var (file, step) in new[] { ("jirei_2026.csv", result.Step("jirei_2026.csv")), ("entra_users.json", result.Step("entra_users.json")) })
        {
            var mappings = _truth.GetProperty("mappings").GetProperty(file).EnumerateArray().ToList();
            var stats = new MatchStats(file);
            foreach (var mapping in mappings)
            {
                var row = (mapping.TryGetProperty("row", out var r) ? r.GetInt32() : mapping.GetProperty("index").GetInt32() + 1) + (file.EndsWith(".csv", StringComparison.Ordinal) ? 1 : 0);
                var gtPerson = mapping.TryGetProperty("person_id", out var gp) && gp.ValueKind == JsonValueKind.String ? gp.GetString() : null;
                var rowProposals = step.Proposals.Where(p => p.Basis.RowNumbers.Contains(row)).ToList();
                var auto = rowProposals.FirstOrDefault(p => p.Status == ProposalStatus.Proposed && p.TargetId is { } t && gtOfPerson.ContainsKey(t));
                var reviewed = rowProposals.Any(p => p.Operation == ProposalOperation.Review);
                var isHard = hard.Contains((file, row - (file.EndsWith(".csv", StringComparison.Ordinal) ? 1 : 0)));
                if (gtPerson is null)
                {
                    continue;
                }

                stats.Rows++;
                if (auto is not null)
                {
                    stats.Auto++;
                    if (gtOfPerson[auto.TargetId!.Value] == gtPerson)
                    {
                        stats.AutoCorrect++;
                    }
                    else
                    {
                        stats.Wrong.Add($"行 {row - (file.EndsWith(".csv", StringComparison.Ordinal) ? 1 : 0)}: {gtOfPerson[auto.TargetId!.Value]}（正: {gtPerson}）");
                    }
                }

                if (reviewed)
                {
                    stats.Review++;
                    var candidates = rowProposals.Where(p => p.Operation == ProposalOperation.Review).SelectMany(p => p.Basis.Candidates.Select(c => c.PersonId).Append(p.TargetId ?? Guid.Empty));
                    if (candidates.Any(c => gtOfPerson.GetValueOrDefault(c) == gtPerson))
                    {
                        stats.ReviewWithCorrectCandidate++;
                    }

                    if (isHard)
                    {
                        stats.ReviewHard++;
                    }
                }

                if (isHard)
                {
                    stats.Hard++;
                }

                if (auto is null && !reviewed)
                {
                    stats.Missed.Add($"行 {row - (file.EndsWith(".csv", StringComparison.Ordinal) ? 1 : 0)}: {gtPerson}");
                }
            }

            result.Matching.Add(stats);
        }
    }

    /// <summary>名寄せが難しい（要確認に回るべき）行: 社員番号の誤記・欠落、所属のあいまいな辞令。</summary>
    private HashSet<(string File, int Row)> HardRows() =>
        _truth.GetProperty("anomalies").EnumerateArray()
            .Where(a => a.GetProperty("category").GetString() is "JIREI_EMPNO_TYPO" or "JIREI_EMPNO_MISSING" or "JIREI_AMBIGUOUS_ORG"
                        && a.TryGetProperty("row", out var r) && r.ValueKind == JsonValueKind.Number)
            .Select(a => (a.GetProperty("file").GetString()!, a.GetProperty("row").GetInt32()))
            .ToHashSet();

    private static readonly Dictionary<string, string[]> OrderTypes = new(StringComparer.Ordinal)
    {
        ["assign"] = ["transfer", "position_appointment"],
        ["transfer"] = ["transfer"],
        ["promote"] = ["transfer", "position_appointment"],
        ["promote_head"] = ["transfer", "position_appointment"],
        ["hire_assign"] = ["hire"],
        ["concurrent_start"] = ["concurrent_start"],
        ["concurrent_end"] = ["concurrent_end"],
        ["concurrent_end_future"] = ["concurrent_end"],
        ["leave"] = ["suspension_start"],
        ["return"] = ["suspension_end"],
        ["secondment"] = ["secondment_start"],
        ["secondment_future"] = ["secondment_start"],
    };

    /// <summary>辞令の解釈（発令の種類・組織・役職・発令日前か）の正解率。</summary>
    private void MeasureActions(EvaluationResult result, Dictionary<Guid, string> gtOfOrg)
    {
        var step = result.Step("jirei_2026.csv");
        var categories = AnomalyCategoriesByRow("jirei_2026.csv");
        foreach (var mapping in _truth.GetProperty("mappings").GetProperty("jirei_2026.csv").EnumerateArray())
        {
            var gtRow = mapping.GetProperty("row").GetInt32();
            var proposal = step.Proposals.FirstOrDefault(p => p.TargetKind == ProposalTargetKind.PersonnelAction && p.Basis.RowNumbers.Contains(gtRow + 1));
            var orderType = mapping.GetProperty("order_type").GetString()!;
            var gtOrg = mapping.GetProperty("org_id").GetString();
            var gtTitle = mapping.GetProperty("title").GetString() ?? string.Empty;
            var check = new ActionCheck(gtRow, orderType, categories.GetValueOrDefault(gtRow) ?? []) { Interpreted = proposal?.Payload.ContainsKey(ProposalFields.ActionType) == true };
            if (proposal is not null && check.Interpreted)
            {
                var type = proposal.Payload[ProposalFields.ActionType]!;
                check.TypeCorrect = OrderTypes.TryGetValue(orderType, out var types) && types.Contains(type);
                var org = proposal.Payload.GetValueOrDefault(ProposalFields.OrgUnitId) is { } o ? gtOfOrg.GetValueOrDefault(Guid.Parse(o, CultureInfo.InvariantCulture)) : null;
                check.OrgCorrect = gtOrg is null || org == gtOrg || (org is null && orderType.StartsWith("secondment", StringComparison.Ordinal));
                check.TitleCorrect = (proposal.Payload.GetValueOrDefault(ProposalFields.PositionTitle) ?? string.Empty) == gtTitle || orderType is "concurrent_end" or "concurrent_end_future" or "leave" or "return" or "secondment" or "secondment_future";
                var effective = mapping.GetProperty("effective").GetBoolean();
                check.FutureCorrect = effective == (proposal.EffectiveDate <= _asOf);
                check.Review = proposal.Status == ProposalStatus.NeedsReview;
                check.Detail = $"{type} / {(org ?? "-")} / {proposal.Payload.GetValueOrDefault(ProposalFields.PositionTitle) ?? "-"}（正: {orderType} / {gtOrg ?? "-"} / {(gtTitle.Length == 0 ? "-" : gtTitle)}）";
            }
            else
            {
                check.Detail = $"解釈なし（正: {orderType} / {gtOrg ?? "-"} / {(gtTitle.Length == 0 ? "-" : gtTitle)}）";
            }

            result.Actions.Add(check);
        }

        // 内示（発令日前）の発令: 承認後も基準日のスナップショット（現在の所属）に反映されていないこと
        result.PendingOrders = _truth.GetProperty("pending_orders").GetArrayLength();
    }

    private Dictionary<int, List<string>> AnomalyCategoriesByRow(string file) =>
        _truth.GetProperty("anomalies").EnumerateArray()
            .Where(a => a.GetProperty("file").GetString() == file && a.TryGetProperty("row", out var r) && r.ValueKind == JsonValueKind.Number)
            .GroupBy(a => a.GetProperty("row").GetInt32())
            .ToDictionary(g => g.Key, g => g.Select(a => a.GetProperty("category").GetString()!).Distinct().ToList());

    /// <summary>Entra ID の不整合の検出率。</summary>
    private void MeasureDirectory(EvaluationResult result, Dictionary<Guid, string> gtOfPerson)
    {
        var step = result.Step("entra_users.json");
        var supplements = step.Proposals.Where(p => p.Basis.Code is not null).ToList();
        var anomalies = _truth.GetProperty("anomalies").EnumerateArray().Where(a => a.GetProperty("file").GetString() == "entra_users.json").ToList();
        var nonPersonRows = _truth.GetProperty("mappings").GetProperty("entra_users.json").EnumerateArray()
            .Where(m => m.GetProperty("kind").GetString() == "non_person")
            .Select(m => m.GetProperty("index").GetInt32() + 1)
            .ToHashSet();

        HashSet<string> Persons(string category) => anomalies
            .Where(a => a.GetProperty("category").GetString() == category && a.GetProperty("person_id").ValueKind == JsonValueKind.String)
            .Select(a => a.GetProperty("person_id").GetString()!)
            .ToHashSet(StringComparer.Ordinal);

        HashSet<string> Found(string code) => supplements
            .Where(p => p.Basis.Code == code && p.TargetId is { } t && gtOfPerson.ContainsKey(t))
            .Select(p => gtOfPerson[p.TargetId!.Value])
            .ToHashSet(StringComparer.Ordinal);

        foreach (var (category, code) in new[]
                 {
                     ("ENTRA_RETIRED_ENABLED", SupplementCodes.DirectoryRetiredEnabled),
                     ("ENTRA_STALE_DEPT", SupplementCodes.DirectoryDepartmentMismatch),
                     ("ENTRA_EMPLOYEEID_EMPTY", SupplementCodes.DirectoryEmployeeIdEmpty),
                     ("ENTRA_MISSING_ACCOUNT", SupplementCodes.DirectoryAccountMissing),
                 })
        {
            var expected = Persons(category);
            var found = Found(code);
            result.Directory.Add(new DetectionCheck(category, expected.Count, expected.Count(found.Contains), found.Count(f => !expected.Contains(f))));
        }

        var nonPersonFound = supplements.Where(p => p.Basis.Code == SupplementCodes.NonPersonAccount).SelectMany(p => p.Basis.RowNumbers).ToHashSet();
        result.Directory.Add(new DetectionCheck("ENTRA_NON_PERSON", nonPersonRows.Count, nonPersonRows.Count(nonPersonFound.Contains), nonPersonFound.Count(r => !nonPersonRows.Contains(r))));
    }

    /// <summary>
    /// 内示（基準日より後の発令）: 承認後、発令の行は人事管理者には見え、一般の閲覧者（RLS）には見えないこと。
    /// RLS は DB の現在の業務日付で判定するので、実行日が発令日を過ぎると一般にも見えるようになる。
    /// </summary>
    private async Task MeasurePendingAsync(EvaluationResult result, NpgsqlDataSource dataSource, ViewerContext admin, OrgSnapshot snapshot, CancellationToken cancellationToken)
    {
        var someone = snapshot.People.First(p => !p.IsTerminatedOn(_asOf)).Id;
        var general = ViewerContext.ForUser(someone, [], Channel.Ui, FieldLimit.Unrestricted);
        async Task<long> CountAsync(ViewerContext viewer)
        {
            await using var tx = await ViewerTransaction.BeginAsync(dataSource, viewer, cancellationToken, readOnly: true);
            await using var command = tx.CreateCommand("SELECT count(*) FROM personnel_action WHERE effective_date > $1 AND effective_date > jinji_business_date()");
            command.Parameters.Add(new NpgsqlParameter { Value = _asOf });
            return (long)(await command.ExecuteScalarAsync(cancellationToken))!;
        }

        result.PendingVisibleToAdmin = (int)await CountAsync(admin);
        result.PendingVisibleToGeneral = (int)await CountAsync(general);
    }

    /// <summary>保持禁止の列の値・旧姓使用者の戸籍名が DB のどこにも無いこと。戸籍名が見つかった人（正解データの ID）を返す。</summary>
    private async Task<(int Checked, int Found, List<string> LegalNamesFoundIn)> MeasureForbiddenAsync(EvaluationResult result, NpgsqlDataSource dataSource, ViewerContext viewer, CancellationToken cancellationToken)
    {
        var (text, _) = TextEncodingDetector.Decode(await File.ReadAllBytesAsync(Path.Combine(_directory, "employees_hr_package.csv"), cancellationToken));
        var table = CsvTableReader.Read(text);
        var forbiddenColumns = _truth.GetProperty("meta").GetProperty("forbidden_columns_in_hr_package").EnumerateArray().Select(c => c.GetString()!).ToList();
        var values = new HashSet<string>(StringComparer.Ordinal);
        foreach (var column in forbiddenColumns)
        {
            var index = table.Headers.ToList().IndexOf(column);
            foreach (var row in table.Rows)
            {
                // 取り得る値の少ない短い値（「有」「男」「A」など）は他の列にも普通に現れるので、4 文字以上の値だけを探す
                if (index >= 0 && row.Cells[index].Trim() is { Length: >= 4 } value)
                {
                    values.Add(value);
                }
            }
        }

        var legalNames = _gtPersons
            .Where(p => p.Value.GetProperty("uses_maiden_name").GetBoolean())
            .Select(p => (Id: p.Key, Name: p.Value.GetProperty("legal_name").GetProperty("family").GetString() + p.Value.GetProperty("legal_name").GetProperty("given").GetString()))
            .ToList();

        var dump = new System.Text.StringBuilder();
        await using (var tx = await ViewerTransaction.BeginAsync(dataSource, viewer, cancellationToken, readOnly: true))
        {
            var tables = new List<string>();
            await using (var list = tx.CreateCommand("SELECT table_name FROM information_schema.tables WHERE table_schema = 'public' AND table_type = 'BASE TABLE' AND table_name <> 'schema_migration'"))
            await using (var reader = await list.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    tables.Add(reader.GetString(0));
                }
            }

            foreach (var name in tables)
            {
                await using var command = tx.CreateCommand($"SELECT coalesce(string_agg(t::text, E'\\n'), '') FROM \"{name}\" t");
                dump.Append(await command.ExecuteScalarAsync(cancellationToken) as string).Append('\n');
            }
        }

        var all = dump.ToString();
        var compact = all.Replace(" ", string.Empty, StringComparison.Ordinal).Replace("　", string.Empty, StringComparison.Ordinal);
        result.ForbiddenValuesChecked = values.Count;
        result.ForbiddenValuesFound = values.Count(v => all.Contains(v, StringComparison.Ordinal));
        result.LegalNamesChecked = legalNames.Count;
        var found = legalNames.Where(n => compact.Contains(n.Name, StringComparison.Ordinal)).Select(n => n.Id).ToList();
        return (legalNames.Count, found.Count, found);
    }

    // -----------------------------------------------------------------------
    // LLM による候補提示（--llm）と、ルールの第 1 候補との比較
    // -----------------------------------------------------------------------

    private const string CategoryPerson = "社員番号の誤記・欠落（辞令）";
    private const string CategoryAmbiguousOrg = "所属のあいまいな辞令";
    private const string CategoryParentOrg = "段飛ばしの組織の位置（組織図）";
    private const string CategorySuccessor = "Entra の旧組織名 → 後継組織";

    /// <summary>LLM に回すべきケースの数え上げ。LLM を使わないときも、ルールの第 1 候補の正解率は出す。</summary>
    private void MeasureLlm(EvaluationResult result)
    {
        var anomalies = _truth.GetProperty("anomalies").EnumerateArray().ToList();
        var jirei = result.Step("jirei_2026.csv");
        var mappings = _truth.GetProperty("mappings").GetProperty("jirei_2026.csv").EnumerateArray().ToDictionary(m => m.GetProperty("row").GetInt32());

        string Person(Guid id) => result.GtOfPerson.GetValueOrDefault(id) ?? "?";
        string Org(Guid id) => result.GtOfOrg.GetValueOrDefault(id) ?? "?";

        // 辞令: 社員番号の誤記・欠落（人の候補）と、所属のあいまいな辞令（組織の候補）
        foreach (var (category, codes) in new[] { (CategoryPerson, new[] { "JIREI_EMPNO_TYPO", "JIREI_EMPNO_MISSING" }), (CategoryAmbiguousOrg, ["JIREI_AMBIGUOUS_ORG"]) })
        {
            var rows = anomalies.Where(a => a.GetProperty("file").GetString() == "jirei_2026.csv" && codes.Contains(a.GetProperty("category").GetString()))
                .Select(a => a.GetProperty("row").GetInt32()).Distinct().Order();
            foreach (var row in rows)
            {
                var mapping = mappings[row];
                var proposal = jirei.Proposals.FirstOrDefault(p => p.TargetKind == ProposalTargetKind.PersonnelAction && p.Basis.RowNumbers.Contains(row + 1));
                var isPerson = category == CategoryPerson;
                var truth = isPerson ? mapping.GetProperty("person_id").GetString() : mapping.GetProperty("org_id").GetString();
                if (truth is null)
                {
                    continue;
                }

                var check = new LlmCaseCheck(category, $"辞令 行 {row}", [truth]);
                if (proposal is not null)
                {
                    Guid? ruleTop = isPerson
                        ? proposal.Basis.Candidates.Count > 0 ? proposal.Basis.Candidates[0].PersonId : proposal.TargetId
                        : proposal.Payload.GetValueOrDefault(ProposalFields.OrgUnitId) is { } o ? Guid.Parse(o, CultureInfo.InvariantCulture) : null;
                    check.RuleTopCorrect = ruleTop is { } r && (isPerson ? Person(r) : Org(r)) == truth;
                    var ranks = isPerson ? proposal.Basis.Llm?.People : proposal.Basis.Llm?.Orgs;
                    Fill(check, ranks, isPerson ? Person : Org);
                }

                result.LlmCases.Add(check);
            }
        }

        // 組織図: 結合セルの補いの上位組織（1 回目の組織図の取込の要確認）
        var orgStep = result.Steps.First(st => st.File == "org_chart_2026-04.csv");
        foreach (var proposal in orgStep.Proposals.Where(p => p.Basis.Code == SupplementCodes.OrgParentAmbiguous && p.TargetId is not null).OrderBy(p => p.Basis.RowNumbers.FirstOrDefault()))
        {
            var gtOrg = Org(proposal.TargetId!.Value);
            if (!_gtOrgs.TryGetValue(gtOrg, out var gt) || gt.GetProperty("parent_id").GetString() is not { } truth)
            {
                continue;
            }

            var check = new LlmCaseCheck(CategoryParentOrg, $"組織図 行 {proposal.Basis.RowNumbers.FirstOrDefault()}（{gt.GetProperty("name").GetString()}）", [truth]);
            var carried = proposal.Payload.GetValueOrDefault(ProposalFields.ParentOrgUnitId) is { } c ? Guid.Parse(c, CultureInfo.InvariantCulture) : (Guid?)null;
            check.RuleTopCorrect = carried is { } carriedId && Org(carriedId) == truth;
            Fill(check, proposal.Basis.Llm?.Orgs, Org);
            result.LlmCases.Add(check);
        }

        // Entra ID: 旧組織名（廃止済み）→ 後継組織。正解は後継組織（successor_org_ids）か、本人の現在の主務の組織
        var entra = result.Step("entra_users.json");
        foreach (var anomaly in anomalies.Where(a => a.GetProperty("category").GetString() == "ENTRA_STALE_DEPT"))
        {
            var index = anomaly.GetProperty("index").GetInt32();
            var personId = anomaly.GetProperty("person_id").GetString()!;
            var oldName = System.Text.RegularExpressions.Regex.Match(anomaly.GetProperty("description").GetString()!, "「(.+?)」").Groups[1].Value;
            var oldOrg = _gtOrgs.Values.FirstOrDefault(o => !o.GetProperty("active").GetBoolean()
                && (o.GetProperty("name").GetString() == oldName || o.GetProperty("observed_names").EnumerateArray().Any(n => n.GetString()?.Trim() == oldName)));
            var truth = (oldOrg.ValueKind == JsonValueKind.Object ? oldOrg.GetProperty("successor_org_ids").EnumerateArray().Select(x => x.GetString()!) : [])
                .Append(_gtPersons[personId].GetProperty("home_org_id").GetString()!)
                .ToHashSet(StringComparer.Ordinal);
            var check = new LlmCaseCheck(CategorySuccessor, $"Entra index {index}（{oldName}）", truth);
            var proposal = entra.Proposals.FirstOrDefault(p => p.Basis.Code == SupplementCodes.DirectoryDepartmentMismatch && p.Basis.RowNumbers.Contains(index + 1));
            Fill(check, proposal?.Basis.Llm?.Orgs, Org);
            result.LlmCases.Add(check);
        }
    }

    private static void Fill(LlmCaseCheck check, IReadOnlyList<LlmRankedCandidate>? ranks, Func<Guid, string> toTruthId)
    {
        if (ranks is null)
        {
            return;
        }

        check.Suggested = true;
        if (ranks.Count > 0)
        {
            check.Top = toTruthId(ranks[0].Id);
            check.TopConfidence = ranks[0].Confidence;
        }

        check.TruthRank = ranks.FirstOrDefault(r => check.Truth.Contains(toTruthId(r.Id)))?.Rank;
    }

    // -----------------------------------------------------------------------
    // 要確認の解決（評価用の解決者。人の確認の代わり）
    // -----------------------------------------------------------------------

    /// <summary>
    /// 辞令の名寄せ・所属の要確認を正解データに従って解決する（正解の人・組織が候補（ルール・LLM）にあれば選び、無ければ修正依頼に回す）。
    /// 解決の結果の提案は承認者が承認する（解決者 ≠ 承認者 ≠ 実行者）。
    /// </summary>
    private async Task ResolveForEvaluationAsync(
        EvaluationResult result,
        NpgsqlIngestStore store,
        ProposalResolutionService resolution,
        ProposalReviewService review,
        ViewerContext resolver,
        ViewerContext approver,
        CancellationToken cancellationToken)
    {
        var step = result.Step("jirei_2026.csv");
        var mappings = _truth.GetProperty("mappings").GetProperty("jirei_2026.csv").EnumerateArray().ToDictionary(m => m.GetProperty("row").GetInt32());
        var current = await store.ListProposalsAsync(approver, step.Import.BatchId, cancellationToken);
        var derived = new List<Guid>();
        foreach (var proposal in current.Where(p => p is { Status: ProposalStatus.NeedsReview, Operation: ProposalOperation.Review, TargetKind: ProposalTargetKind.PersonnelAction }))
        {
            result.Resolution.Reviewed++;
            var row = proposal.Basis.RowNumbers.FirstOrDefault() - 1;
            var mapping = mappings.GetValueOrDefault(row);
            var truthPerson = mapping.ValueKind == JsonValueKind.Object ? mapping.GetProperty("person_id").GetString() : null;
            var truthOrg = mapping.ValueKind == JsonValueKind.Object ? mapping.GetProperty("org_id").GetString() : null;

            var rulePeople = proposal.Basis.Candidates.Select(c => c.PersonId).Concat(proposal.TargetId is { } t ? [t] : []).ToList();
            var llmPeople = proposal.Basis.Llm?.People.Select(p => p.Id).ToList() ?? [];
            Guid? person = rulePeople.Concat(llmPeople).FirstOrDefault(id => result.GtOfPerson.GetValueOrDefault(id) == truthPerson) is var found && found != Guid.Empty ? found : null;
            var onlyWithLlm = person is { } chosenPerson && !rulePeople.Contains(chosenPerson);

            Guid? org = null;
            var needsOrg = proposal.Basis.Code == SupplementCodes.AmbiguousOrg || proposal.Basis.OrgCandidates.Count > 0;
            if (needsOrg)
            {
                var ruleOrgs = proposal.Basis.OrgCandidates.ToList();
                var llmOrgs = proposal.Basis.Llm?.Orgs.Select(o => o.Id).ToList() ?? [];
                org = ruleOrgs.Concat(llmOrgs).FirstOrDefault(id => result.GtOfOrg.GetValueOrDefault(id) == truthOrg) is var foundOrg && foundOrg != Guid.Empty ? foundOrg : null;
                onlyWithLlm |= org is { } chosenOrg && !ruleOrgs.Contains(chosenOrg);
            }

            var interpretable = proposal.Payload.ContainsKey(ProposalFields.ActionType);
            try
            {
                if (interpretable && person is not null && (!needsOrg || org is not null))
                {
                    var resolved = await resolution.ResolveAsync(resolver, proposal.Id, new ResolutionRequest(ResolutionChoice.SelectCandidate, person, org), cancellationToken);
                    derived.AddRange(resolved.DerivedProposalIds);
                    result.Resolution.Selected++;
                    if (onlyWithLlm)
                    {
                        result.Resolution.SelectedOnlyWithLlm++;
                    }
                }
                else
                {
                    await resolution.ResolveAsync(resolver, proposal.Id, new ResolutionRequest(ResolutionChoice.ReferToCorrection), cancellationToken);
                    result.Resolution.Referred++;
                }
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or AccessDeniedException)
            {
                var key = $"{ex.GetType().Name}: {Shorten(ex.Message)}";
                result.Resolution.Failures[key] = result.Resolution.Failures.GetValueOrDefault(key) + 1;
            }
        }

        result.Resolution.Derived = derived.Count;
        var proposals = (await store.ListProposalsAsync(approver, step.Import.BatchId, cancellationToken)).Where(p => derived.Contains(p.Id)).ToList();
        var (applied, failures) = await ApproveAllAsync(review, approver, proposals, cancellationToken);
        result.Resolution.DerivedApplied = applied;
        foreach (var (key, count) in failures)
        {
            result.Resolution.Failures[key] = result.Resolution.Failures.GetValueOrDefault(key) + count;
        }
    }

    /// <summary>業務日付を固定する（日本時間の正午）。</summary>
    internal sealed class FixedTimeProvider(DateOnly today) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() =>
            new DateTimeOffset(today.ToDateTime(new TimeOnly(12, 0)), BusinessDate.UtcOffset).ToUniversalTime();
    }
}
