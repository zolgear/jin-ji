using System.Text.Json;
using JinJi.Application.Access;
using JinJi.Application.People;
using Microsoft.Extensions.DependencyInjection;

namespace JinJi.Mcp.Tools;

/// <summary>人の検索（REST の <c>GET /api/v1/people</c>・<c>/orgs/{id}/members</c> と同じサービス・同じ規則）。</summary>
internal sealed class SearchPeopleTool : McpTool
{
    public const int DefaultLimit = 20;

    private static readonly JsonElement Schema = McpTexts.Schema(
        $$"""
        {
          "type": "object",
          "properties": {
            "query": { "type": "string", "minLength": 1, "maxLength": {{PersonSearch.MaxQueryLength}},
              "description": "氏名・フリガナ・ローマ字・社員番号・社用メールの部分一致（異体字・全角半角・ひらがなとカタカナの違い、読みのローマ字の綴りの揺れ・長音の有無は同じとみなす）。ワイルドカード（* % ?）は使えない。正規化して {{PersonSearch.MinQueryKeyLength}} 文字以上" },
            "org_unit_id": { "type": "string", "format": "uuid", "description": "所属組織（主務・兼務・出向）で絞り込む組織の jin-ji ID（search_orgs で探す）" },
            "include_descendants": { "type": "boolean", "default": false, "description": "true なら org_unit_id の配下の組織の所属者も含める" },
            "legal_entity_ids": { "type": "array", "items": { "type": "string", "format": "uuid" }, "maxItems": {{PersonSearch.MaxValuesPerFilter}}, "uniqueItems": true, "description": "雇用元の法人の ID（get_directory_info・組織の legal_entity で分かる）。どれかに当たる人" },
            "level_keys": { "type": "array", "items": { "type": "string", "maxLength": 63 }, "maxItems": {{PersonSearch.MaxValuesPerFilter}}, "uniqueItems": true, "description": "所属組織の段のキー（get_directory_info の段の定義。例: department）。その段の組織に所属する人" },
            "assignment_kinds": { "type": "array", "items": { "type": "string", "enum": ["primary", "concurrent", "secondment"] }, "maxItems": 3, "uniqueItems": true, "description": "所属の種類（primary 主務・concurrent 兼務・secondment 出向）" },
            "positions": { "type": "array", "items": { "type": "string", "maxLength": {{PersonSearch.MaxValueLength}} }, "maxItems": {{PersonSearch.MaxValuesPerFilter}}, "uniqueItems": true, "description": "役職名（完全一致。例: 部長）" },
            "employment_types": { "type": "array", "items": { "type": "string", "maxLength": {{PersonSearch.MaxValueLength}} }, "maxItems": {{PersonSearch.MaxValuesPerFilter}}, "uniqueItems": true, "description": "雇用区分のコード（regular・contract・entrusted・dispatched・seconded_in・part_time・other）" },
            "job_types": { "type": "array", "items": { "type": "string", "maxLength": {{PersonSearch.MaxValueLength}} }, "maxItems": {{PersonSearch.MaxValuesPerFilter}}, "uniqueItems": true, "description": "職種（完全一致）" },
            "site_ids": { "type": "array", "items": { "type": "string", "format": "uuid" }, "maxItems": {{PersonSearch.MaxValuesPerFilter}}, "uniqueItems": true, "description": "拠点の ID（list_sites で探す）。人の拠点（人ごとの拠点か主務の組織の拠点）がどれかに当たる人" },
            "site_types": { "type": "array", "items": { "type": "string", "enum": [{{string.Join(", ", PersonSearchCodes.SiteTypes.Select(t => $"\"{t}\""))}}] }, "maxItems": 8, "uniqueItems": true, "description": "拠点の種別（head_office 本社・branch 支社/支店・sales_office 営業所・factory 工場・laboratory 研究所・logistics 物流拠点・store 店舗・other）" },
            "prefectures": { "type": "array", "items": { "type": "string", "pattern": "^\\d{2}$" }, "maxItems": {{PersonSearch.MaxValuesPerFilter}}, "uniqueItems": true, "description": "拠点の所在の都道府県のコード（JIS X 0401。例: 13 東京都、27 大阪府）" },
            "remote_work": { "type": "boolean", "description": "true ならリモートワークの対象者だけ、false なら対象でない人だけ（省略は絞り込まない）" },
            "hired_from": { "type": "string", "pattern": "^\\d{4}-\\d{2}-\\d{2}$", "description": "入社年月日の範囲の開始（yyyy-MM-dd、この日を含む）" },
            "hired_to": { "type": "string", "pattern": "^\\d{4}-\\d{2}-\\d{2}$", "description": "入社年月日の範囲の終了（yyyy-MM-dd、この日を含む）" },
            "employment_statuses": { "type": "array", "items": { "type": "string", "enum": ["active", "terminated"] }, "maxItems": 2, "uniqueItems": true, "description": "在籍状態" },
            "account_statuses": { "type": "array", "items": { "type": "string", "enum": ["active", "suspended"] }, "maxItems": 2, "uniqueItems": true, "description": "アカウント状態（suspended は一時停止中。理由は持たない）" },
            "sort": { "type": "string", "enum": ["id", "kana", "org", "hire_date", "hire_date_desc", "site", "remote_work"], "default": "id", "description": "並び順（id・フリガナ・組織図の順・入社年月日の古い順・新しい順・拠点ごと・リモートワークの対象者が先。見えない値の人は最後）" },
            "include_facets": { "type": "boolean", "default": false, "description": "true なら一致した人数（total）と条件ごとの件数（facets）も返す。最初のページだけ（cursor と併用不可）" },
            "as_of": { "type": "string", "pattern": "^\\d{4}-\\d{2}-\\d{2}$", "description": "{{McpTexts.AsOf}}" },
            "limit": { "type": "integer", "minimum": 1, "maximum": {{PersonSearch.MaxLimit}}, "default": {{DefaultLimit}}, "description": "1 ページの人数" },
            "cursor": { "type": "string", "maxLength": 2048, "description": "前の結果の next_cursor（続きを取るときだけ。他の引数は前と同じにする）" }
          },
          "additionalProperties": false
        }
        """);

    public override string Name => McpToolNames.SearchPeople;

    public override string Title => "人を検索する";

    public override string Description =>
        "人を氏名・社員番号・メールなどで検索する、または組織・法人・段・所属の種類・役職・雇用区分・職種・拠点（拠点・拠点の種別・都道府県）・リモートワークの有無・入社年月日・在籍状態で絞り込む。条件同士は AND、配列の中は OR、所属の条件（org_unit_id・level_keys・assignment_kinds・positions）は同じ 1 つの所属で満たす。"
        + "何も指定しないと全員の一覧になり、全社横断の連続取得は不正検知の対象。照合・絞り込み・件数に使うのは委任元に見える項目だけ。"
        + $"1 ページ最大 {PersonSearch.MaxLimit} 人（既定 {DefaultLimit}）。next_cursor があれば cursor に渡して続きを取る。include_facets で条件ごとの人数（見える人の見える値だけ。その条件以外に一致する人で数える）も返す。"
        + $"返した人数は返却人数の上限（時間あたり）に数える。{McpTexts.PersonShape}。";

    public override JsonElement InputSchema => Schema;

    public override RequestKindMetadata Kind { get; } = new(RequestKind.PersonList, true);

    public override async Task<McpToolOutput> InvokeAsync(McpToolContext context, McpArguments arguments, CancellationToken cancellationToken)
    {
        var limit = arguments.Integer("limit", DefaultLimit, 1, PersonSearch.MaxLimit);
        var cursor = arguments.String("cursor", 2048);
        var asOf = arguments.Date("as_of");
        var sortCode = arguments.Choice("sort", [.. PersonSearchCodes.Sorts]) ?? "id";
        PersonSearchCodes.TryParseSort(sortCode, out var sort);
        var criteria = new PersonSearch
        {
            Query = arguments.String("query", PersonSearch.MaxQueryLength),
            OrgUnitId = arguments.Id("org_unit_id"),
            IncludeDescendants = arguments.Flag("include_descendants"),
            LegalEntityIds = arguments.Strings("legal_entity_ids", PersonSearch.MaxValuesPerFilter, 64)
                .Select(v => Guid.TryParseExact(v, "D", out var id) ? id : throw McpToolException.Invalid("legal_entity_ids の要素は法人の ID（UUID）にしてください。"))
                .ToHashSet(),
            LevelKeys = arguments.Strings("level_keys", PersonSearch.MaxValuesPerFilter, 63).ToHashSet(StringComparer.Ordinal),
            AssignmentKinds = arguments.ChoiceList("assignment_kinds", 3, PersonSearchCodes.AssignmentKinds)
                .Select(v => PersonSearchCodes.TryParseAssignmentKind(v, out var kind) ? kind : AssignmentKind.Primary)
                .ToHashSet(),
            Positions = arguments.Strings("positions", PersonSearch.MaxValuesPerFilter, PersonSearch.MaxValueLength).ToHashSet(StringComparer.Ordinal),
            EmploymentTypes = arguments.Strings("employment_types", PersonSearch.MaxValuesPerFilter, PersonSearch.MaxValueLength).ToHashSet(StringComparer.Ordinal),
            JobTypes = arguments.Strings("job_types", PersonSearch.MaxValuesPerFilter, PersonSearch.MaxValueLength).ToHashSet(StringComparer.Ordinal),
            SiteIds = arguments.Strings("site_ids", PersonSearch.MaxValuesPerFilter, 64)
                .Select(v => Guid.TryParseExact(v, "D", out var id) ? id : throw McpToolException.Invalid("site_ids の要素は拠点の ID（UUID）にしてください。"))
                .ToHashSet(),
            SiteTypes = arguments.ChoiceList("site_types", PersonSearchCodes.SiteTypes.Count, PersonSearchCodes.SiteTypes).ToHashSet(StringComparer.Ordinal),
            Prefectures = arguments.ChoiceList("prefectures", PersonSearch.MaxValuesPerFilter, PersonSearchCodes.Prefectures).ToHashSet(StringComparer.Ordinal),
            RemoteWork = arguments.Has("remote_work") ? arguments.Flag("remote_work") : null,
            HiredFrom = arguments.Date("hired_from"),
            HiredTo = arguments.Date("hired_to"),
            EmploymentStatuses = arguments.ChoiceList("employment_statuses", 2, PersonSearchCodes.EmploymentStatuses).ToHashSet(StringComparer.Ordinal),
            AccountStatuses = arguments.ChoiceList("account_statuses", 2, PersonSearchCodes.AccountStatuses).ToHashSet(StringComparer.Ordinal),
            Sort = sort,
            IncludeFacets = arguments.Flag("include_facets"),
        };

        var cursors = context.Services.GetRequiredService<CursorProtector>();
        var purpose = "mcp:people:" + CursorProtector.Hash(criteria.Fingerprint(), asOf?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
        Guid? after = null;
        if (cursor is not null)
        {
            if (!cursors.TryUnprotect<PeopleCursor>(purpose, context.Viewer, cursor, out var position))
            {
                throw McpToolException.Invalid("cursor が不正です（改ざん・別の検索条件・別の利用者のカーソルは使えません）。cursor を省略して最初から取り直してください。");
            }

            after = position!.After;
        }

        var search = criteria with { After = after, Limit = limit };
        try
        {
            search.Validate();
        }
        catch (ArgumentException ex)
        {
            throw McpToolException.Invalid(ex.Message.Split(" (Parameter", 2)[0]);
        }

        PersonPage page;
        try
        {
            page = await context.Services.GetRequiredService<PersonDirectoryService>().SearchPeopleAsync(context.Viewer, search, asOf, cancellationToken)
                ?? throw McpToolException.NotFound("その時点の組織 org_unit_id ");
        }
        catch (StaleCursorException ex)
        {
            throw McpToolException.Invalid(ex.Message + "cursor を省略して最初から取り直してください。");
        }

        var next = page.NextAfter is { } nextAfter ? cursors.Protect(purpose, context.Viewer, new PeopleCursor(nextAfter)) : null;
        var items = page.Items.Select(McpPerson.From).ToList();
        object structured = page.Facets is { } facets
            ? new { items, next_cursor = next, total = page.Total, facets = McpFacets(facets) }
            : new { items, next_cursor = next };
        return new McpToolOutput(
            structured,
            $"{items.Count} 人を返しました。" + (page.Total is { } total ? $"条件に一致したのは {total} 人です。" : string.Empty)
                + (next is null ? "続きはありません。" : "続きがあります（next_cursor を cursor に渡す）。"),
            [.. page.Items.Select(v => v.PersonId)],
            items.Count,
            search.IsBroad);
    }

    /// <summary>条件ごとの件数（REST の facets と同じ名前・値）。</summary>
    private static Dictionary<string, object> McpFacets(PersonFacets f)
    {
        static List<object> Map(IReadOnlyList<FacetValue> values) => [.. values.Select(v => (object)new { value = v.Value, label = v.Label, count = v.Count })];
        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["legal_entities"] = Map(f.LegalEntities),
            ["levels"] = Map(f.Levels),
            ["assignment_kinds"] = Map(f.AssignmentKinds),
            ["positions"] = Map(f.Positions),
            ["employment_types"] = Map(f.EmploymentTypes),
            ["job_types"] = Map(f.JobTypes),
            ["sites"] = Map(f.Sites),
            ["site_types"] = Map(f.SiteTypes),
            ["prefectures"] = Map(f.Prefectures),
            ["remote_work"] = Map(f.RemoteWork),
            ["employment_statuses"] = Map(f.EmploymentStatuses),
            ["account_statuses"] = Map(f.AccountStatuses),
        };
    }

    /// <summary>人の一覧のカーソルの中身。</summary>
    internal sealed record PeopleCursor(Guid After);
}

/// <summary>人の参照（REST の <c>GET /api/v1/people/{id}</c> と <c>/people/{id}/manager</c>）。</summary>
internal sealed class GetPersonTool : McpTool
{
    private static readonly JsonElement Schema = McpTexts.Schema(
        $$"""
        {
          "type": "object",
          "properties": {
            "person_id": { "type": "string", "format": "uuid", "description": "人の jin-ji ID（search_people で探す）" },
            "include_managers": { "type": "boolean", "default": false, "description": "true なら直属の上長（委任元に見える人だけ）も返す" },
            "as_of": { "type": "string", "pattern": "^\\d{4}-\\d{2}-\\d{2}$", "description": "{{McpTexts.AsOf}}" }
          },
          "required": ["person_id"],
          "additionalProperties": false
        }
        """);

    public override string Name => McpToolNames.GetPerson;

    public override string Title => "人を参照する";

    public override string Description =>
        "1 人の詳細（見える項目だけ）と、指定すれば直属の上長を返す。存在しない人と参照できない人（公開範囲の外・退職後の公開期間を過ぎた人・入社前の人）は区別せず not_found。"
        + $"上長の項目が見えない人の上長は返さない（managers は空）。{McpTexts.PersonShape}。";

    public override JsonElement InputSchema => Schema;

    public override RequestKindMetadata Kind { get; } = new(RequestKind.PersonRead, true);

    public override async Task<McpToolOutput> InvokeAsync(McpToolContext context, McpArguments arguments, CancellationToken cancellationToken)
    {
        var personId = arguments.RequiredId("person_id");
        var withManagers = arguments.Flag("include_managers");
        var asOf = arguments.Date("as_of");
        var people = context.Services.GetRequiredService<PersonDirectoryService>();

        var views = await people.GetPeopleAsync(context.Viewer, [personId], asOf, cancellationToken);
        if (views.Count == 0)
        {
            throw McpToolException.NotFound("その人");
        }

        var person = McpPerson.From(views[0]);
        if (!withManagers)
        {
            return new McpToolOutput(new { person }, "1 人の情報を返しました。", [personId], 1);
        }

        var managers = await people.GetManagersAsync(context.Viewer, personId, asOf, cancellationToken) ?? [];
        var subjects = new List<Guid> { personId };
        subjects.AddRange(managers.Select(m => m.PersonId).Where(id => !subjects.Contains(id)));
        return new McpToolOutput(
            new { person, managers = managers.Select(McpPerson.From).ToList() },
            $"1 人の情報と、上長 {managers.Count} 人を返しました。",
            subjects,
            subjects.Count);
    }
}

/// <summary>人の一括取得（REST の <c>GET /api/v1/people?ids=</c>）。</summary>
internal sealed class GetPeopleTool : McpTool
{
    public const int MaxIds = 100;

    private static readonly JsonElement Schema = McpTexts.Schema(
        $$"""
        {
          "type": "object",
          "properties": {
            "person_ids": { "type": "array", "items": { "type": "string", "format": "uuid" }, "minItems": 1, "maxItems": {{MaxIds}}, "uniqueItems": true,
              "description": "人の jin-ji ID（組織長・上長・変更フィードの ID など）。重複を除いて {{MaxIds}} 件まで" },
            "as_of": { "type": "string", "pattern": "^\\d{4}-\\d{2}-\\d{2}$", "description": "{{McpTexts.AsOf}}" }
          },
          "required": ["person_ids"],
          "additionalProperties": false
        }
        """);

    public override string Name => McpToolNames.GetPeople;

    public override string Title => "人を ID でまとめて参照する";

    public override string Description =>
        $"複数の人を jin-ji ID でまとめて参照する（{MaxIds} 件まで、指定の順）。存在しない・参照できない ID は単に含めない（何件が見えなかったかも返さない）。"
        + $"返した人数は返却人数の上限に数える。{McpTexts.PersonShape}。";

    public override JsonElement InputSchema => Schema;

    public override RequestKindMetadata Kind { get; } = new(RequestKind.PersonList, true);

    public override async Task<McpToolOutput> InvokeAsync(McpToolContext context, McpArguments arguments, CancellationToken cancellationToken)
    {
        var ids = arguments.Ids("person_ids", 1, MaxIds);
        var asOf = arguments.Date("as_of");
        var views = await context.Services.GetRequiredService<PersonDirectoryService>().GetPeopleAsync(context.Viewer, ids, asOf, cancellationToken);
        var items = views.Select(McpPerson.From).ToList();
        return new McpToolOutput(new { items }, $"{ids.Count} 件の指定のうち、参照できる {items.Count} 人を返しました。", [.. views.Select(v => v.PersonId)], items.Count);
    }
}
