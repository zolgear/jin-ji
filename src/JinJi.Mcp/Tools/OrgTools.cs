using System.Text.Json;
using JinJi.Application.Access;
using JinJi.Application.Changes;
using JinJi.Application.Organization;
using JinJi.Application.People;
using JinJi.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace JinJi.Mcp.Tools;

/// <summary>組織の参照（上位・直下・組織長）。REST の <c>GET /api/v1/orgs/{id}</c> と同じサービスで、組織長は人の参照と同じ投影を通す。</summary>
internal sealed class GetOrgTool : McpTool
{
    private static readonly JsonElement Schema = McpTexts.Schema(
        $$"""
        {
          "type": "object",
          "properties": {
            "org_unit_id": { "type": "string", "format": "uuid", "description": "組織の jin-ji ID（search_orgs・get_org_tree で探す）" },
            "include_head": { "type": "boolean", "default": true, "description": "true なら組織長の人の情報（委任元に見える項目だけ）も返す。false なら組織長は head_person_id だけ" },
            "as_of": { "type": "string", "pattern": "^\\d{4}-\\d{2}-\\d{2}$", "description": "{{McpTexts.AsOf}}" }
          },
          "required": ["org_unit_id"],
          "additionalProperties": false
        }
        """);

    public override string Name => McpToolNames.GetOrg;

    public override string Title => "組織を参照する";

    public override string Description =>
        "組織 1 つと、その上位組織（ancestors。直近の上位が先頭で、最上位まで）・直下の組織（children）・組織長（head。委任元に見える人だけ）を返す。"
        + "その時点に無い組織は not_found（人事管理者以外には組織改編の予定は見えない）。組織の情報は全社公開。"
        + $"組織長を返したら返却人数に 1 人と数える。{McpTexts.OrgShape}。";

    public override JsonElement InputSchema => Schema;

    public override RequestKindMetadata Kind { get; } = new(RequestKind.PersonRead, true);

    public override async Task<McpToolOutput> InvokeAsync(McpToolContext context, McpArguments arguments, CancellationToken cancellationToken)
    {
        var unitId = arguments.RequiredId("org_unit_id");
        var withHead = arguments.Flag("include_head", defaultValue: true);
        var asOf = arguments.Date("as_of");

        var unit = await context.Services.GetRequiredService<OrgDirectoryService>().GetUnitContextAsync(context.Viewer, unitId, asOf, cancellationToken)
            ?? throw McpToolException.NotFound("その時点の組織 ");
        McpPerson? head = null;
        if (withHead && unit.Unit.HeadPersonId is { } headId)
        {
            var views = await context.Services.GetRequiredService<PersonDirectoryService>().GetPeopleAsync(context.Viewer, [headId], asOf, cancellationToken);
            head = views.Count > 0 ? McpPerson.From(views[0]) : null;
        }

        var timeProvider = context.Services.GetRequiredService<TimeProvider>();
        return new McpToolOutput(
            new
            {
                as_of = asOf ?? BusinessDate.Today(timeProvider),
                org = McpOrgUnit.From(unit.Unit),
                ancestors = unit.Ancestors.Select(McpOrgUnit.From).ToList(),
                children = unit.Children.Select(McpOrgUnit.From).ToList(),
                head,
            },
            $"組織 1 つと、上位組織 {unit.Ancestors.Count} 件・直下の組織 {unit.Children.Count} 件" + (head is null ? "を返しました。" : "・組織長を返しました。"),
            head is null ? [] : [head.Id],
            head is null ? 0 : 1);
    }
}

/// <summary>組織図の部分木（REST の <c>GET /api/v1/orgs/tree</c> と同じサービス）。</summary>
internal sealed class GetOrgTreeTool : McpTool
{
    public const int DefaultDepth = 3;
    public const int MaxDepth = 10;
    public const int MaxUnits = 500;

    private static readonly JsonElement Schema = McpTexts.Schema(
        $$"""
        {
          "type": "object",
          "properties": {
            "root_org_unit_id": { "type": "string", "format": "uuid", "description": "部分木の根にする組織の jin-ji ID。省略すると全法人の最上位の組織から" },
            "depth": { "type": "integer", "minimum": 1, "maximum": {{MaxDepth}}, "default": {{DefaultDepth}}, "description": "根から何段まで返すか（1 なら根だけ、2 なら根と直下）" },
            "as_of": { "type": "string", "pattern": "^\\d{4}-\\d{2}-\\d{2}$", "description": "{{McpTexts.AsOf}}" }
          },
          "additionalProperties": false
        }
        """);

    public override string Name => McpToolNames.GetOrgTree;

    public override string Title => "組織図の部分木を参照する";

    public override string Description =>
        $"組織図の部分木を、parent_id（上位組織）と depth（根からの深さ。根 = 0）で木を組める平らな一覧で返す（法人・深さ・組織コードの順）。最大 {MaxUnits} 組織で、超えたら truncated が true"
        + "（root_org_unit_id を下の組織にするか depth を浅くして取り直す）。人は返さない（組織長は head_person_id だけ。名前は get_people で取る）。"
        + $"{McpTexts.OrgShape}。";

    public override JsonElement InputSchema => Schema;

    public override RequestKindMetadata Kind { get; } = new(RequestKind.OrgRead, false);

    public override async Task<McpToolOutput> InvokeAsync(McpToolContext context, McpArguments arguments, CancellationToken cancellationToken)
    {
        var rootId = arguments.Id("root_org_unit_id");
        var depth = arguments.Integer("depth", DefaultDepth, 1, MaxDepth);
        var asOf = arguments.Date("as_of");
        var subtree = await context.Services.GetRequiredService<OrgDirectoryService>().GetSubtreeAsync(context.Viewer, rootId, depth, MaxUnits, asOf, cancellationToken)
            ?? throw McpToolException.NotFound("その時点の組織 root_org_unit_id ");
        var units = subtree.Units.Select(McpOrgTreeUnit.From).ToList();
        return new McpToolOutput(
            new { as_of = subtree.AsOf, root_org_unit_id = rootId, depth, units, truncated = subtree.Truncated },
            $"{units.Count} 組織を返しました。" + (subtree.Truncated ? $"上限 {MaxUnits} で打ち切りました（根を下にするか depth を浅くしてください）。" : string.Empty),
            []);
    }
}

/// <summary>組織の検索（REST の <c>GET /api/v1/orgs?query=</c>）。</summary>
internal sealed class SearchOrgsTool : McpTool
{
    public const int DefaultLimit = 20;

    private static readonly JsonElement Schema = McpTexts.Schema(
        $$"""
        {
          "type": "object",
          "properties": {
            "query": { "type": "string", "minLength": 1, "maxLength": {{OrgSearch.MaxQueryLength}},
              "description": "組織名・略称・組織コードの部分一致（異体字・全角半角・「第一」と「1」の違いは同じとみなす）。ワイルドカード（* % ?）は使えない" },
            "as_of": { "type": "string", "pattern": "^\\d{4}-\\d{2}-\\d{2}$", "description": "{{McpTexts.AsOf}}" },
            "limit": { "type": "integer", "minimum": 1, "maximum": {{OrgSearch.MaxLimit}}, "default": {{DefaultLimit}}, "description": "返す組織の数の上限" }
          },
          "required": ["query"],
          "additionalProperties": false
        }
        """);

    public override string Name => McpToolNames.SearchOrgs;

    public override string Title => "組織を検索する";

    public override string Description =>
        $"組織を名前・略称・コードで検索する。完全一致・前方一致・部分一致の順。limit（最大 {OrgSearch.MaxLimit}、既定 {DefaultLimit}）で打ち切ったら has_more が true（続きは無いので語を絞り込む）。"
        + $"{McpTexts.OrgShape}。";

    public override JsonElement InputSchema => Schema;

    public override RequestKindMetadata Kind { get; } = new(RequestKind.OrgRead, false);

    public override async Task<McpToolOutput> InvokeAsync(McpToolContext context, McpArguments arguments, CancellationToken cancellationToken)
    {
        var search = new OrgSearch
        {
            Query = arguments.RequiredString("query", OrgSearch.MaxQueryLength),
            Limit = arguments.Integer("limit", DefaultLimit, 1, OrgSearch.MaxLimit),
        };
        var asOf = arguments.Date("as_of");
        try
        {
            search.Validate();
        }
        catch (ArgumentException ex)
        {
            throw McpToolException.Invalid(ex.Message.Split(" (Parameter", 2)[0]);
        }

        var result = await context.Services.GetRequiredService<OrgDirectoryService>().SearchAsync(context.Viewer, search, asOf, cancellationToken);
        var timeProvider = context.Services.GetRequiredService<TimeProvider>();
        var items = result.Items.Select(McpOrgUnit.From).ToList();
        return new McpToolOutput(
            new { as_of = asOf ?? BusinessDate.Today(timeProvider), items, has_more = result.HasMore },
            $"{items.Count} 組織を返しました。" + (result.HasMore ? "ほかにも当たる組織があります（語を絞り込んでください）。" : string.Empty),
            []);
    }
}

/// <summary>変更フィード（REST の <c>GET /api/v1/changes</c>）。</summary>
internal sealed class GetChangesTool : McpTool
{
    public const int DefaultLimit = 100;
    private const string Purpose = "mcp:changes";

    private static readonly JsonElement Schema = McpTexts.Schema(
        $$"""
        {
          "type": "object",
          "properties": {
            "since": { "type": "string", "maxLength": 2048, "description": "前回の next_cursor。省略すると今の位置だけを返す（items は空）" },
            "limit": { "type": "integer", "minimum": 1, "maximum": {{ChangeFeedService.MaxLimit}}, "default": {{DefaultLimit}}, "description": "返す件数の上限" }
          },
          "additionalProperties": false
        }
        """);

    public override string Name => McpToolNames.GetChanges;

    public override string Title => "変更フィードを読む";

    public override string Description =>
        "since（前回の next_cursor）より後に変わった人・組織の種別（person / org_unit）と jin-ji ID を返す。値は含まないので、get_people・get_org で取り直す。"
        + "since を省略すると今の位置（next_cursor）だけを返す。同じ ID が何度も現れ得る。人事管理者以外には、内示・将来の予定は効力を持つ日まで現れない。"
        + $"has_more が false なら、しばらく待ってから next_cursor で読む。limit は 1〜{ChangeFeedService.MaxLimit}（既定 {DefaultLimit}）。";

    public override JsonElement InputSchema => Schema;

    public override RequestKindMetadata Kind { get; } = new(RequestKind.Changes, false);

    public override async Task<McpToolOutput> InvokeAsync(McpToolContext context, McpArguments arguments, CancellationToken cancellationToken)
    {
        var since = arguments.String("since", 2048);
        var limit = arguments.Integer("limit", DefaultLimit, 1, ChangeFeedService.MaxLimit);
        var cursors = context.Services.GetRequiredService<CursorProtector>();
        ChangeFeedPosition? position = null;
        if (since is not null && !cursors.TryUnprotect(Purpose, context.Viewer, since, out position))
        {
            throw McpToolException.Invalid("since が不正です（改ざん・別の利用者のカーソルは使えません）。since を省略して今の位置から読み直してください。");
        }

        var page = await context.Services.GetRequiredService<ChangeFeedService>().ReadAsync(context.Viewer, position, limit, cancellationToken);
        var items = page.Items.Select(McpChange.From).ToList();
        return new McpToolOutput(
            new { items, next_cursor = cursors.Protect(Purpose, context.Viewer, page.Next), has_more = page.HasMore },
            $"{items.Count} 件の変更を返しました。" + (page.HasMore ? "続きがあります。" : "今は続きがありません（しばらく待ってから next_cursor で読む）。"),
            []);
    }
}

/// <summary>メタ情報（REST の <c>GET /api/v1/meta</c>。業務日付・段の定義・時点の目盛り）。</summary>
internal sealed class GetDirectoryInfoTool : McpTool
{
    private static readonly JsonElement Schema = McpTexts.Schema("""{ "type": "object", "properties": {}, "additionalProperties": false }""");

    public override string Name => McpToolNames.GetDirectoryInfo;

    public override string Title => "業務日付と組織の段の定義を確かめる";

    public override string Description =>
        "業務日付（日本時間の今日。as_of の既定値）、未来の時点を指定できるか（can_view_future）、法人ごとの組織の段の定義（会社・本部・部・課など。level の key と rank）、"
        + "時点の目盛り（組織改編 org_change・一斉異動 mass_action の日。人事管理者以外には今日以前だけ）を返す。人は返さない。";

    public override JsonElement InputSchema => Schema;

    public override RequestKindMetadata Kind { get; } = new(RequestKind.OrgRead, false);

    public override async Task<McpToolOutput> InvokeAsync(McpToolContext context, McpArguments arguments, CancellationToken cancellationToken)
    {
        var meta = await context.Services.GetRequiredService<DirectoryMetaService>().GetAsync(context.Viewer, cancellationToken);
        return new McpToolOutput(
            new
            {
                business_date = meta.BusinessDate,
                time_zone = DirectoryMetaService.TimeZone,
                can_view_future = meta.CanViewFuture,
                max_as_of = meta.MaxAsOf,
                legal_entities = meta.LegalEntities.Select(e => new
                {
                    id = e.LegalEntity.Id,
                    code = e.LegalEntity.Code,
                    name = e.LegalEntity.Name,
                    uses_default_levels = e.UsesDefault,
                    levels = e.Levels.Select(l => new McpOrgLevel(l.Key, l.Name, l.Rank)).ToList(),
                }).ToList(),
                default_levels = meta.DefaultLevels.Select(l => new McpOrgLevel(l.Key, l.Name, l.Rank)).ToList(),
                milestones = meta.Milestones.Select(m => new { date = m.Date, kinds = m.Kinds.Select(McpCodes.Of).ToList() }).ToList(),
            },
            $"業務日付は {meta.BusinessDate:yyyy-MM-dd}（日本時間）です。",
            []);
    }
}

/// <summary>拠点の一覧と人数（REST の <c>GET /api/v1/sites</c> と同じサービス・同じ規則）。</summary>
internal sealed class ListSitesTool : McpTool
{
    private static readonly JsonElement Schema = McpTexts.Schema(
        $$"""
        {
          "type": "object",
          "properties": {
            "query": { "type": "string", "minLength": 1, "maxLength": {{OrgSearch.MaxQueryLength}}, "description": "拠点名（異体字・全角半角をそろえる）・拠点コードの部分一致。ワイルドカード（* % ?）は使えない" },
            "legal_entity_ids": { "type": "array", "items": { "type": "string", "format": "uuid" }, "maxItems": {{PersonSearch.MaxValuesPerFilter}}, "uniqueItems": true, "description": "法人の ID" },
            "site_types": { "type": "array", "items": { "type": "string", "enum": [{{string.Join(", ", PersonSearchCodes.SiteTypes.Select(t => $"\"{t}\""))}}] }, "maxItems": 8, "uniqueItems": true, "description": "拠点の種別" },
            "prefectures": { "type": "array", "items": { "type": "string", "pattern": "^\\d{2}$" }, "maxItems": {{PersonSearch.MaxValuesPerFilter}}, "uniqueItems": true, "description": "所在の都道府県のコード（JIS X 0401。例: 13 東京都）" },
            "as_of": { "type": "string", "pattern": "^\\d{4}-\\d{2}-\\d{2}$", "description": "{{McpTexts.AsOf}}" }
          },
          "additionalProperties": false
        }
        """);

    public override string Name => McpToolNames.ListSites;

    public override string Title => "拠点の一覧";

    public override string Description =>
        "拠点（本社・支社・工場など）の一覧を、法人のコード・拠点コードの順に返す。拠点はコード・名称・種別・所在の都道府県・所在地（事業所の住所）・拠点を持つ組織と、"
        + "member_count（拠点の人の数）・remote_work_count（そのうちリモートワークの対象の人の数）を持つ。人数は委任元に見える人の見える値だけを数える（search_people の件数と同じ規則）。"
        + "人は返さない。拠点の人の一覧は search_people の site_ids で引く。拠点の情報は全社公開。";

    public override JsonElement InputSchema => Schema;

    public override RequestKindMetadata Kind { get; } = new(RequestKind.OrgStats, false);

    public override async Task<McpToolOutput> InvokeAsync(McpToolContext context, McpArguments arguments, CancellationToken cancellationToken)
    {
        var asOf = arguments.Date("as_of");
        var search = new SiteSearch
        {
            Query = arguments.String("query", OrgSearch.MaxQueryLength),
            LegalEntityIds = arguments.Strings("legal_entity_ids", PersonSearch.MaxValuesPerFilter, 64)
                .Select(v => Guid.TryParseExact(v, "D", out var id) ? id : throw McpToolException.Invalid("legal_entity_ids の要素は法人の ID（UUID）にしてください。"))
                .ToHashSet(),
            Types = arguments.ChoiceList("site_types", PersonSearchCodes.SiteTypes.Count, PersonSearchCodes.SiteTypes).ToHashSet(StringComparer.Ordinal),
            Prefectures = arguments.ChoiceList("prefectures", PersonSearch.MaxValuesPerFilter, PersonSearchCodes.Prefectures).ToHashSet(StringComparer.Ordinal),
        };
        try
        {
            search.Validate();
        }
        catch (ArgumentException ex)
        {
            throw McpToolException.Invalid(ex.Message.Split(" (Parameter", 2)[0]);
        }

        var sites = await context.Services.GetRequiredService<OrgDirectoryService>().ListSitesAsync(context.Viewer, search, asOf, cancellationToken);
        var timeProvider = context.Services.GetRequiredService<TimeProvider>();
        return new McpToolOutput(
            new { as_of = asOf ?? BusinessDate.Today(timeProvider), items = sites.Select(McpSite.From).ToList() },
            $"拠点 {sites.Count} 件を返しました。",
            [],
            0);
    }
}
