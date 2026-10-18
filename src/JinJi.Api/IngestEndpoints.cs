using JinJi.Application.Access;
using JinJi.Application.Ingest;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace JinJi.Api;

/// <summary>
/// 取込の作業領域（<c>/api/v1/ingest</c>）。要確認の提案の参照と解決（人の操作。アーキテクチャ概要 §7.1 の HITL）。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>使えるのは人事管理者の人だけ（AI エージェント・社内システムは不可）。取込の実行者も解決してよい</item>
/// <item>解決の結果は通常の提案（起案中）になり、解決者・取込の実行者とは別の人事管理者が承認したときだけ公開側に出る</item>
/// <item>提案の値（payload / diff）は返さない。返すのは根拠（候補の ID・組織の候補・LLM による推定・種別・行番号）だけ</item>
/// </list>
/// </remarks>
internal static class IngestEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/ingest/proposals/{proposalId}", GetAsync)
            .WithName("GetProposal")
            .WithSummary("変更提案の根拠（候補・LLM による推定）を参照する（人事管理者）")
            .WithMetadata(new RequestKindMetadata(RequestKind.Ingest, false))
            .Produces<ProposalResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        api.MapPost("/ingest/proposals/{proposalId}/resolve", ResolveAsync)
            .WithName("ResolveProposal")
            .WithSummary("要確認の提案を解決する（人事管理者。取込の実行者は不可）")
            .WithDescription(
                "resolution: select_candidate（person_id / org_unit_id を候補から選ぶ）・register_new（新規として登録）・refer_to_correction（修正依頼に回す）・reject（却下）。" +
                "解決の結果は起案中の提案になり、解決者とは別の人事管理者が承認する。要確認でない提案は 409、候補に無い選択は 400。")
            .WithMetadata(new RequestKindMetadata(RequestKind.Ingest, false))
            .Produces<ResolveProposalResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
    }

    private static async Task<IResult> GetAsync(
        string proposalId,
        HttpContext http,
        ProposalResolutionService service,
        ProposalSuggestionService suggestions,
        CancellationToken cancellationToken)
    {
        var viewer = http.Viewer();
        if (!AdminAccess.IsHrAdminUser(viewer))
        {
            return Problems.Forbidden(http, "人事管理者だけが使えます。");
        }

        if (!ApiRequest.TryParseId(proposalId, out var id))
        {
            return Problems.NotFound(http);
        }

        try
        {
            var proposal = await service.GetAsync(viewer, id, cancellationToken);
            return Results.Ok(ProposalResponse.From(proposal, await suggestions.ListForProposalAsync(viewer, id, cancellationToken)));
        }
        catch (ProposalNotFoundException)
        {
            return Problems.NotFound(http);
        }
    }

    private static async Task<IResult> ResolveAsync(
        string proposalId,
        ResolveProposalRequest body,
        HttpContext http,
        ProposalResolutionService service,
        CancellationToken cancellationToken)
    {
        var viewer = http.Viewer();
        if (!AdminAccess.IsHrAdminUser(viewer))
        {
            return Problems.Forbidden(http, "人事管理者だけが使えます。");
        }

        if (!ApiRequest.TryParseId(proposalId, out var id))
        {
            return Problems.NotFound(http);
        }

        ResolutionChoice? choice = body?.Resolution switch
        {
            "select_candidate" => ResolutionChoice.SelectCandidate,
            "register_new" => ResolutionChoice.RegisterNew,
            "refer_to_correction" => ResolutionChoice.ReferToCorrection,
            "reject" => ResolutionChoice.Reject,
            _ => null,
        };
        if (choice is null)
        {
            return Problems.BadRequest(http, "resolution は select_candidate / register_new / refer_to_correction / reject のどれかで指定してください。");
        }

        try
        {
            var result = await service.ResolveAsync(viewer, id, new ResolutionRequest(choice.Value, body!.PersonId, body.OrgUnitId), cancellationToken);
            return Results.Ok(ResolveProposalResponse.From(result));
        }
        catch (ProposalNotFoundException)
        {
            return Problems.NotFound(http);
        }
        catch (ArgumentException ex)
        {
            return Problems.BadRequest(http, ex.Message.Split(" (Parameter", 2)[0]);
        }
        catch (InvalidOperationException ex)
        {
            return Problems.Conflict(http, ex.Message);
        }
    }
}

/// <summary>要確認の提案の解決の要求。</summary>
public sealed record ResolveProposalRequest
{
    /// <summary>select_candidate / register_new / refer_to_correction / reject。</summary>
    public string? Resolution { get; init; }

    /// <summary>候補から選ぶ人（select_candidate）。</summary>
    public Guid? PersonId { get; init; }

    /// <summary>候補から選ぶ組織（select_candidate。所属のあいまいな辞令・段飛ばしの可能性のある組織の上位組織）。</summary>
    public Guid? OrgUnitId { get; init; }
}

/// <summary>要確認の提案の解決の結果。</summary>
public sealed record ResolveProposalResponse(
    Guid ProposalId,
    [property: OpenApiEnum(typeof(ProposalStatus))] string Status,
    [property: OpenApiEnum(typeof(ProposalResolution))] string? Resolution,
    IReadOnlyList<Guid> DerivedProposalIds, Guid? CorrectionId)
{
    public static ResolveProposalResponse From(ResolutionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return new ResolveProposalResponse(result.ProposalId, Codes.Of(result.Status), result.Resolution is { } r ? Codes.Of(r) : null, result.DerivedProposalIds, result.CorrectionId);
    }
}

/// <summary>変更提案の根拠（値は返さない）。</summary>
public sealed record ProposalResponse
{
    public required Guid Id { get; init; }

    public required Guid BatchId { get; init; }

    [OpenApiEnum(typeof(ProposalTargetKind))]
    public required string TargetKind { get; init; }

    [OpenApiEnum(typeof(ProposalOperation))]
    public required string Operation { get; init; }

    [OpenApiEnum(typeof(ProposalStatus))]
    public required string Status { get; init; }

    public Guid? TargetId { get; init; }

    public DateOnly? EffectiveDate { get; init; }

    public string? Code { get; init; }

    public string? Note { get; init; }

    public required IReadOnlyList<int> RowNumbers { get; init; }

    public required decimal Confidence { get; init; }

    public required IReadOnlyList<CandidateResponse> Candidates { get; init; }

    public required IReadOnlyList<Guid> OrgCandidates { get; init; }

    /// <summary>LLM による推定（作業領域の情報。人が解決し、別の人が承認するまで公開側には出ない）。</summary>
    public LlmSuggestionResponse? Llm { get; init; }

    [OpenApiEnum(typeof(ProposalResolution))]
    public string? Resolution { get; init; }

    public Guid? ResolvedFromProposalId { get; init; }

    /// <summary>
    /// AI エージェント（MCP）が添えた推定（記録の古い順。作業領域の情報）。理由（rationale）はエージェントが書いた自由記述で、
    /// 画面ではデータとして表示し、指示として扱わない。人が解決し、別の人が承認するまで公開側には出ない。
    /// </summary>
    public required IReadOnlyList<AgentSuggestionResponse> AgentSuggestions { get; init; }

    public static ProposalResponse From(ChangeProposalRecord proposal, IReadOnlyList<AgentSuggestionRecord>? suggestions = null)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        return new ProposalResponse
        {
            Id = proposal.Id,
            BatchId = proposal.BatchId,
            TargetKind = Codes.Of(proposal.TargetKind),
            Operation = Codes.Of(proposal.Operation),
            Status = Codes.Of(proposal.Status),
            TargetId = proposal.TargetId,
            EffectiveDate = proposal.EffectiveDate,
            Code = proposal.Basis.Code,
            Note = proposal.Basis.Note,
            RowNumbers = proposal.Basis.RowNumbers,
            Confidence = proposal.Confidence,
            Candidates = [.. proposal.Basis.Candidates.Select(c => new CandidateResponse(c.PersonId, Codes.Of(c.Rule), c.Confidence))],
            OrgCandidates = proposal.Basis.OrgCandidates,
            Llm = proposal.Basis.Llm is { } llm
                ? new LlmSuggestionResponse(
                    llm.Kind,
                    llm.Model,
                    llm.Endpoint,
                    [.. llm.People.Select(p => new LlmRankResponse(p.Id, p.Rank, p.Confidence, p.Reason))],
                    [.. llm.Orgs.Select(o => new LlmRankResponse(o.Id, o.Rank, o.Confidence, o.Reason))],
                    llm.DiscardedIds)
                : null,
            Resolution = proposal.Resolution is { } r ? Codes.Of(r) : null,
            ResolvedFromProposalId = proposal.ResolvedFromProposalId,
            AgentSuggestions = [.. (suggestions ?? []).Select(AgentSuggestionResponse.From)],
        };
    }
}

/// <summary>AI エージェントが添えた推定。</summary>
/// <param name="Resolution">推定した解決の種類（select_candidate / register_new / refer_to_correction / reject）。</param>
/// <param name="SuggestedByPersonId">推定を書いたエージェントの委任元。</param>
public sealed record AgentSuggestionResponse(
    Guid Id,
    [property: OpenApiEnum(typeof(ResolutionChoice))] string Resolution,
    Guid? PersonId,
    Guid? OrgUnitId,
    decimal Confidence,
    string Rationale,
    Guid SuggestedByPersonId,
    string SuggestedViaClientId,
    DateTimeOffset CreatedAt)
{
    public static AgentSuggestionResponse From(AgentSuggestionRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return new AgentSuggestionResponse(
            record.Id,
            Codes.Of(record.Resolution),
            record.PersonId,
            record.OrgUnitId,
            record.Confidence,
            record.Rationale,
            record.SuggestedByPersonId,
            record.SuggestedViaClientId,
            record.CreatedAt);
    }
}

/// <summary>名寄せの候補。</summary>
public sealed record CandidateResponse(Guid PersonId, [property: OpenApiEnum(typeof(MatchRule))] string Rule, decimal Confidence);

/// <summary>LLM による推定（モデル名・確信度・理由）。</summary>
public sealed record LlmSuggestionResponse(string Kind, string Model, string Endpoint, IReadOnlyList<LlmRankResponse> People, IReadOnlyList<LlmRankResponse> Orgs, int DiscardedIds);

/// <summary>LLM が順位を付けた候補。</summary>
public sealed record LlmRankResponse(Guid Id, int Rank, decimal Confidence, string Reason);

/// <summary>列挙値のコード（snake_case）。</summary>
internal static class Codes
{
    public static string Of<T>(T value)
        where T : struct, Enum => System.Text.Json.JsonNamingPolicy.SnakeCaseLower.ConvertName(value.ToString());
}
