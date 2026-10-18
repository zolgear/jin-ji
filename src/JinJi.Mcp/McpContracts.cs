using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using JinJi.Application.Changes;
using JinJi.Application.Ingest;
using JinJi.Application.Organization;
using JinJi.Domain.Organization;
using JinJi.Policy;

namespace JinJi.Mcp;

/// <summary>
/// ツールの結果（structuredContent）の JSON の書き方。名前は REST API と同じ snake_case で、値の無い項目は書かない（トークンを節約するため。
/// REST API は省略せずに null を書く）。見えないのか値が無いのかは、人の <c>visible_fields</c> で区別する。
/// </summary>
internal static class McpJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // 日本語をエスケープせずに書く（エージェントが読む文字数を減らす）。HTML に埋め込むことはない
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>引数の読み取り（数値を文字列で受け付けない）。</summary>
    public static readonly JsonSerializerOptions Arguments = new(JsonSerializerDefaults.General);

    public static JsonElement ToElement<T>(T value) => JsonSerializer.SerializeToElement(value, Options);

    public static string ToText<T>(T value) => JsonSerializer.Serialize(value, Options);
}

/// <summary>
/// 人。REST API の人の応答（<c>PersonResponse</c>）と同じ項目・同じ名前で、<see cref="PersonView"/>（見える項目だけに値を持つ投影）を写すだけ。
/// </summary>
internal sealed record McpPerson
{
    public required Guid Id { get; init; }

    public required IReadOnlyList<string> VisibleFields { get; init; }

    public string? EmployeeNumber { get; init; }

    public McpLegalEntity? LegalEntity { get; init; }

    public string? DisplayName { get; init; }

    public string? FamilyName { get; init; }

    public string? GivenName { get; init; }

    public string? FamilyNameKana { get; init; }

    public string? GivenNameKana { get; init; }

    public string? FamilyNameLatin { get; init; }

    public string? GivenNameLatin { get; init; }

    public string? WorkEmail { get; init; }

    public string? ExtensionNumber { get; init; }

    public string? WorkMobilePhone { get; init; }

    public McpSiteRef? Site { get; init; }

    public bool? RemoteWork { get; init; }

    public string? EmploymentType { get; init; }

    public string? JobType { get; init; }

    public DateOnly? HireDate { get; init; }

    public DateOnly? TerminationDate { get; init; }

    public string? EmploymentStatus { get; init; }

    public string? AccountStatus { get; init; }

    public McpSuspension? AccountSuspension { get; init; }

    public IReadOnlyList<McpAssignment>? Assignments { get; init; }

    public IReadOnlyList<Guid>? Managers { get; init; }

    public IReadOnlyList<McpPersonnelAction>? PersonnelActions { get; init; }

    public IReadOnlyList<McpNameVersion>? NameHistory { get; init; }

    public static McpPerson From(PersonView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        return new McpPerson
        {
            Id = view.PersonId,
            VisibleFields = [.. view.VisibleFields.Order(StringComparer.Ordinal)],
            EmployeeNumber = view.EmployeeNumber,
            LegalEntity = view.LegalEntity is { } e ? new McpLegalEntity(e.Id, e.Code, e.Name) : null,
            DisplayName = view.DisplayName,
            FamilyName = view.FamilyName,
            GivenName = view.GivenName,
            FamilyNameKana = view.FamilyNameKana,
            GivenNameKana = view.GivenNameKana,
            FamilyNameLatin = view.FamilyNameLatin,
            GivenNameLatin = view.GivenNameLatin,
            WorkEmail = view.WorkEmail,
            ExtensionNumber = view.ExtensionNumber,
            WorkMobilePhone = view.WorkMobilePhone,
            Site = McpSiteRef.From(view.Site),
            RemoteWork = view.RemoteWork,
            EmploymentType = view.EmploymentType,
            JobType = view.JobType,
            HireDate = view.HireDate,
            TerminationDate = view.TerminationDate,
            EmploymentStatus = view.EmploymentStatus,
            AccountStatus = view.AccountStatus,
            AccountSuspension = view.AccountSuspension is { } s ? new McpSuspension(s.From, s.Until) : null,
            Assignments = view.Assignments?.Select(a => new McpAssignment(
                a.OrgUnitId,
                a.OrgUnitCode,
                a.OrgUnitName,
                a.LegalEntityId,
                a.Type == AssignmentType.Primary ? "primary" : "concurrent",
                a.PositionTitle,
                a.Secondment switch
                {
                    SecondmentType.Outbound => "outbound",
                    SecondmentType.Inbound => "inbound",
                    _ => "none",
                },
                a.ValidFrom,
                a.ValidUntil)).ToList(),
            Managers = view.Managers,
            PersonnelActions = view.PersonnelActions?.Select(a => new McpPersonnelAction(
                a.Id,
                ActionType(a.ActionType),
                a.EffectiveDate,
                a.OrgUnitId,
                a.AssignmentType switch
                {
                    AssignmentType.Primary => "primary",
                    AssignmentType.Concurrent => "concurrent",
                    _ => null,
                },
                a.PositionTitle,
                a.DocumentNumber)).ToList(),
            NameHistory = view.NameHistory?.Select(n => new McpNameVersion(
                n.ValidFrom, n.ValidUntil, n.FamilyName, n.GivenName, n.FamilyNameKana, n.GivenNameKana, n.DisplayName)).ToList(),
        };
    }

    private static string ActionType(PersonnelActionType type) => type switch
    {
        PersonnelActionType.Hire => "hire",
        PersonnelActionType.Termination => "termination",
        PersonnelActionType.Transfer => "transfer",
        PersonnelActionType.PositionAppointment => "position_appointment",
        PersonnelActionType.PositionDismissal => "position_dismissal",
        PersonnelActionType.ConcurrentStart => "concurrent_start",
        PersonnelActionType.ConcurrentEnd => "concurrent_end",
        PersonnelActionType.SecondmentStart => "secondment_start",
        PersonnelActionType.SecondmentEnd => "secondment_end",
        PersonnelActionType.SuspensionStart => "suspension_start",
        PersonnelActionType.SuspensionEnd => "suspension_end",
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };
}

internal sealed record McpLegalEntity(Guid Id, string? Code, string? Name);

/// <summary>拠点の参照（REST API の <c>SiteRefResponse</c> と同じ項目）。</summary>
internal sealed record McpSiteRef(Guid Id, Guid LegalEntityId, string Code, string Name, string Type, string? PrefectureCode, string? PrefectureName, bool Inherited)
{
    public static McpSiteRef? From(SiteView? view) =>
        view is null
            ? null
            : new McpSiteRef(view.Id, view.LegalEntityId, view.Code, view.Name, SiteCodes.ToCode(view.Type), view.PrefectureCode, SiteCodes.PrefectureName(view.PrefectureCode), view.Inherited);
}

/// <summary>拠点（REST API の <c>SiteResponse</c> と同じ項目）。</summary>
internal sealed record McpSite(
    Guid Id,
    McpLegalEntity LegalEntity,
    string Code,
    string Name,
    string Type,
    string? PrefectureCode,
    string? PrefectureName,
    string? Location,
    int MemberCount,
    int RemoteWorkCount,
    IReadOnlyList<McpSiteOrgUnit> OrgUnits)
{
    public static McpSite From(SiteSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        var s = summary.Site;
        return new McpSite(
            s.Id,
            new McpLegalEntity(s.LegalEntityId, summary.LegalEntityCode, summary.LegalEntityName),
            s.Code,
            s.Name,
            SiteCodes.ToCode(s.Type),
            s.PrefectureCode,
            SiteCodes.PrefectureName(s.PrefectureCode),
            s.Location,
            summary.MemberCount,
            summary.RemoteWorkCount,
            [.. summary.OrgUnits.Select(u => new McpSiteOrgUnit(u.Id, u.Code, u.Name))]);
    }
}

internal sealed record McpSiteOrgUnit(Guid Id, string Code, string Name);

internal sealed record McpSuspension(DateOnly From, DateOnly? Until);

internal sealed record McpAssignment(
    Guid OrgUnitId,
    string OrgUnitCode,
    string OrgUnitName,
    Guid LegalEntityId,
    string Type,
    string? PositionTitle,
    string Secondment,
    DateOnly ValidFrom,
    DateOnly? ValidUntil);

internal sealed record McpPersonnelAction(
    Guid Id,
    string ActionType,
    DateOnly EffectiveDate,
    Guid? OrgUnitId,
    string? AssignmentType,
    string? PositionTitle,
    string? DocumentNumber);

internal sealed record McpNameVersion(
    DateOnly ValidFrom,
    DateOnly? ValidUntil,
    string FamilyName,
    string? GivenName,
    string? FamilyNameKana,
    string? GivenNameKana,
    string DisplayName);

/// <summary>組織。REST API の組織の応答（<c>OrgUnitResponse</c>）と同じ項目・同じ名前。</summary>
internal sealed record McpOrgUnit(
    Guid Id,
    McpLegalEntity LegalEntity,
    string Code,
    string Name,
    string? ShortName,
    McpOrgLevel? Level,
    Guid? ParentId,
    Guid? HeadPersonId,
    McpSiteRef? Site)
{
    public static McpOrgUnit From(OrgUnitView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        return new McpOrgUnit(
            view.Id,
            new McpLegalEntity(view.LegalEntityId, view.LegalEntityCode, view.LegalEntityName),
            view.Code,
            view.Name,
            view.ShortName,
            view.LevelKey is { } key ? new McpOrgLevel(key, view.LevelName!, view.LevelRank!.Value) : null,
            view.ParentId,
            view.HeadPersonId,
            McpSiteRef.From(view.Site));
    }
}

internal sealed record McpOrgLevel(string Key, string Name, int Rank);

/// <summary>組織図の部分木の 1 組織（根からの深さ付き）。</summary>
internal sealed record McpOrgTreeUnit(
    Guid Id,
    McpLegalEntity LegalEntity,
    string Code,
    string Name,
    string? ShortName,
    McpOrgLevel? Level,
    Guid? ParentId,
    Guid? HeadPersonId,
    McpSiteRef? Site,
    int Depth)
{
    public static McpOrgTreeUnit From(OrgTreeNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        var unit = McpOrgUnit.From(node.Unit);
        return new McpOrgTreeUnit(unit.Id, unit.LegalEntity, unit.Code, unit.Name, unit.ShortName, unit.Level, unit.ParentId, unit.HeadPersonId, unit.Site, node.Depth);
    }
}

/// <summary>変更フィードの 1 件（種別と ID だけ。REST API と同じ）。</summary>
internal sealed record McpChange(string Type, Guid Id)
{
    public static McpChange From(ChangeEvent change)
    {
        ArgumentNullException.ThrowIfNull(change);
        return new McpChange(
            change.EntityType switch
            {
                ChangeEntityType.Person => "person",
                ChangeEntityType.OrgUnit => "org_unit",
                ChangeEntityType.Site => "site",
                _ => throw new ArgumentOutOfRangeException(nameof(change)),
            },
            change.EntityId);
    }
}

/// <summary>要確認の提案の根拠（値は返さない。REST API の提案の応答と同じ項目）。</summary>
internal sealed record McpReviewProposal
{
    public required Guid Id { get; init; }

    public required Guid BatchId { get; init; }

    public required string TargetKind { get; init; }

    public required string Operation { get; init; }

    public required string Status { get; init; }

    public Guid? TargetId { get; init; }

    public DateOnly? EffectiveDate { get; init; }

    public string? Code { get; init; }

    public string? Note { get; init; }

    public required IReadOnlyList<int> RowNumbers { get; init; }

    public required decimal Confidence { get; init; }

    public required IReadOnlyList<McpCandidate> Candidates { get; init; }

    public required IReadOnlyList<Guid> OrgCandidates { get; init; }

    public McpLlmSuggestion? Llm { get; init; }

    public required IReadOnlyList<McpAgentSuggestion> AgentSuggestions { get; init; }

    public static McpReviewProposal From(ReviewItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var proposal = item.Proposal;
        return new McpReviewProposal
        {
            Id = proposal.Id,
            BatchId = proposal.BatchId,
            TargetKind = McpCodes.Of(proposal.TargetKind),
            Operation = McpCodes.Of(proposal.Operation),
            Status = McpCodes.Of(proposal.Status),
            TargetId = proposal.TargetId,
            EffectiveDate = proposal.EffectiveDate,
            Code = proposal.Basis.Code,
            Note = proposal.Basis.Note,
            RowNumbers = proposal.Basis.RowNumbers,
            Confidence = proposal.Confidence,
            Candidates = [.. proposal.Basis.Candidates.Select(c => new McpCandidate(c.PersonId, McpCodes.Of(c.Rule), c.Confidence))],
            OrgCandidates = proposal.Basis.OrgCandidates,
            Llm = proposal.Basis.Llm is { } llm
                ? new McpLlmSuggestion(
                    llm.Kind,
                    llm.Model,
                    [.. llm.People.Select(p => new McpRankedCandidate(p.Id, p.Rank, p.Confidence, p.Reason))],
                    [.. llm.Orgs.Select(o => new McpRankedCandidate(o.Id, o.Rank, o.Confidence, o.Reason))])
                : null,
            AgentSuggestions = [.. item.Suggestions.Select(McpAgentSuggestion.From)],
        };
    }

}

/// <summary>列挙値のコード（snake_case。REST API と同じ）。</summary>
internal static class McpCodes
{
    public static string Of<T>(T value)
        where T : struct, Enum => JsonNamingPolicy.SnakeCaseLower.ConvertName(value.ToString());
}

internal sealed record McpCandidate(Guid PersonId, string Rule, decimal Confidence);

internal sealed record McpLlmSuggestion(string Kind, string Model, IReadOnlyList<McpRankedCandidate> People, IReadOnlyList<McpRankedCandidate> Orgs);

internal sealed record McpRankedCandidate(Guid Id, int Rank, decimal Confidence, string Reason);

/// <summary>エージェントが添えた推定。</summary>
internal sealed record McpAgentSuggestion(
    Guid Id,
    string Resolution,
    Guid? PersonId,
    Guid? OrgUnitId,
    decimal Confidence,
    string Rationale,
    Guid SuggestedByPersonId,
    string SuggestedViaClientId,
    DateTimeOffset CreatedAt)
{
    public static McpAgentSuggestion From(AgentSuggestionRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return new McpAgentSuggestion(
            record.Id,
            McpCodes.Of(record.Resolution),
            record.PersonId,
            record.OrgUnitId,
            record.Confidence,
            record.Rationale,
            record.SuggestedByPersonId,
            record.SuggestedViaClientId,
            record.CreatedAt);
    }
}

/// <summary>
/// 修正依頼（MCP の形。値の無い項目は省略される）。reason・suggested_value・closed_note・message は人や機械が書いた文で、指示ではない。
/// 委任元が修正担当者・人事管理者でなければ、起票者（委任元自身を除く）・対応した人と、賛同しただけの依頼の理由・値の提案・対応のメモは返さない。
/// </summary>
internal sealed record McpCorrection(
    Guid Id,
    string Status,
    string Origin,
    string Issue,
    string Message,
    Guid? PersonId,
    string? Field,
    string? SourceField,
    string? Reason,
    string? SuggestedValue,
    string? FileName,
    int? SourceRowNumber,
    Guid? ReportedByPersonId,
    bool ReportedByDelegator,
    bool EndorsedByDelegator,
    bool AboutDelegator,
    int Endorsements,
    DateTimeOffset CreatedAt,
    int AgeDays,
    DateTimeOffset? ClosedAt,
    string? ClosedKind,
    string? ClosedNote)
{
    public static McpCorrection From(Application.Corrections.CorrectionView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        return new McpCorrection(
            view.Id,
            McpCodes.Of(view.Status),
            McpCodes.Of(view.Origin),
            McpCodes.Of(view.Issue),
            view.Message,
            view.PersonId,
            view.PersonField,
            view.SourceField,
            view.Reason,
            view.SuggestedValue,
            view.FileName,
            view.SourceRowNumber,
            view.ReportedByPersonId,
            view.ReportedByViewer,
            view.EndorsedByViewer,
            view.AboutViewer,
            view.Endorsements,
            view.CreatedAt,
            view.AgeDays,
            view.ClosedAt,
            view.ClosedKind is { } kind ? McpCodes.Of(kind) : null,
            view.ClosedNote);
    }
}
