using System.Text.Json;
using JinJi.Application.Access;
using JinJi.Application.People;
using Microsoft.Extensions.DependencyInjection;

namespace JinJi.Mcp.Tools;

/// <summary>承認ルート（REST の <c>GET /api/v1/people/{id}/approval-route</c> と同じサービス・同じ規則・同じ項目名）。</summary>
internal sealed class GetApprovalRouteTool : McpTool
{
    private static readonly JsonElement Schema = McpTexts.Schema(
        $$"""
        {
          "type": "object",
          "properties": {
            "person_id": { "type": "string", "format": "uuid", "description": "申請者（起点の人）の jin-ji ID（search_people で探す）" },
            "org_unit_id": { "type": "string", "format": "uuid", "description": "起点にする所属組織の jin-ji ID（兼務・出向先での申請のとき。その人の所属でなければ not_found）。省略すると主務" },
            "vacancy": { "type": "string", "enum": ["promote", "vacant"], "default": "promote",
              "description": "組織長が空席の組織の扱い。promote は上位の組織長へ繰り上げ、飛ばした組織を次の段の skipped_units（reason=vacant）に記録する。vacant は空席の段（status=vacant）として返す" },
            "level_keys": { "type": "array", "items": { "type": "string", "maxLength": 63 }, "maxItems": {{PersonSearch.MaxValuesPerFilter}}, "uniqueItems": true,
              "description": "段のキー（get_directory_info の法人ごとの段の定義。例: department）。その段の組織の組織長だけを返す（繰り上げ・本人のために飛ばした組織の段にも当たる）" },
            "positions": { "type": "array", "items": { "type": "string", "maxLength": {{PersonSearch.MaxValueLength}} }, "maxItems": {{PersonSearch.MaxValuesPerFilter}}, "uniqueItems": true,
              "description": "承認者のその組織での役職（完全一致。例: 部長）。当たる段だけを返す" },
            "as_of": { "type": "string", "pattern": "^\\d{4}-\\d{2}-\\d{2}$", "description": "{{McpTexts.AsOf}}" }
          },
          "required": ["person_id"],
          "additionalProperties": false
        }
        """);

    public override string Name => McpToolNames.GetApprovalRoute;

    public override string Title => "承認ルートを参照する";

    public override string Description =>
        "人の申請を誰が承認するか（承認ルート）を返す。起点の人の所属組織から上位の組織へたどり、各組織の組織長を下の段から順に steps に並べる。"
        + "本人が組織長の組織は飛ばす（reason=self）。組織長が空席（いない・退職済み・見えない）の組織は既定で上位へ繰り上げる（reason=vacant）。"
        + "一時停止中の承認者は承認者のまま返す（approver の account_status が suspended。代わりの人・代理承認は jin-ji には無い）。"
        + "steps が空なら承認者を決められない。起点の人が見えない・所属か上長の項目が見えない人は not_found。"
        + $"返した人数（起点の人と承認者）は返却人数の上限に数える。approver は人（{McpTexts.PersonShape}）。";

    public override JsonElement InputSchema => Schema;

    public override RequestKindMetadata Kind { get; } = new(RequestKind.PersonRead, true);

    public override async Task<McpToolOutput> InvokeAsync(McpToolContext context, McpArguments arguments, CancellationToken cancellationToken)
    {
        var personId = arguments.RequiredId("person_id");
        var asOf = arguments.Date("as_of");
        ApprovalRouteCodes.TryParseVacancy(arguments.Choice("vacancy", [.. ApprovalRouteCodes.Vacancies]) ?? "promote", out var vacancy);
        var query = new ApprovalRouteQuery
        {
            PersonId = personId,
            StartOrgUnitId = arguments.Id("org_unit_id"),
            Vacancy = vacancy,
            LevelKeys = arguments.Strings("level_keys", PersonSearch.MaxValuesPerFilter, 63).ToHashSet(StringComparer.Ordinal),
            Positions = arguments.Strings("positions", PersonSearch.MaxValuesPerFilter, PersonSearch.MaxValueLength).ToHashSet(StringComparer.Ordinal),
        };

        ApprovalRoute route;
        try
        {
            route = await context.Services.GetRequiredService<PersonDirectoryService>().GetApprovalRouteAsync(context.Viewer, query, asOf, cancellationToken)
                ?? throw McpToolException.NotFound("その人（またはその人の所属 org_unit_id）");
        }
        catch (ArgumentException ex)
        {
            throw McpToolException.Invalid(ex.Message.Split(" (Parameter", 2)[0]);
        }

        var subjects = new List<Guid> { personId };
        subjects.AddRange(route.ApproverIds.Where(id => id != personId));
        var structured = new
        {
            person_id = route.PersonId,
            as_of = route.AsOf,
            vacancy = ApprovalRouteCodes.Vacancies[(int)route.Vacancy],
            start = route.Start is { } s
                ? new { org_unit = Org(s.OrgUnit), assignment_type = Code(s.Type), secondment = Code(s.Secondment), position_title = s.PositionTitle }
                : null,
            steps = route.Steps.Select(step => new
            {
                order = step.Order,
                org_unit = Org(step.OrgUnit),
                status = Code(step.Status),
                approver = step.Approver is { } approver ? McpPerson.From(approver) : null,
                approver_position = step.ApproverPosition,
                skipped_units = Skipped(step.SkippedUnits),
            }).ToList(),
            unresolved_units = Skipped(route.UnresolvedUnits),
        };
        return new McpToolOutput(
            structured,
            route.Steps.Count == 0
                ? "承認者が見つかりませんでした（steps は空。承認者を決められないので、自動で承認しないでください）。"
                : $"承認ルートの {route.Steps.Count} 段を返しました。",
            subjects,
            subjects.Count);
    }

    private static object Org(ApprovalOrgUnit unit) => new
    {
        id = unit.Id,
        legal_entity_id = unit.LegalEntityId,
        code = unit.Code,
        name = unit.Name,
        level_key = unit.LevelKey,
        level_name = unit.LevelName,
        level_rank = unit.LevelRank,
    };

    private static List<object> Skipped(IEnumerable<ApprovalSkippedUnit> units) =>
        [.. units.Select(u => (object)new { org_unit = Org(u.OrgUnit), reason = Code(u.Reason) })];

    private static string Code<T>(T value)
        where T : struct, Enum => JsonNamingPolicy.SnakeCaseLower.ConvertName(value.ToString());
}
