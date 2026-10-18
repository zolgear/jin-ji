using JinJi.Application.Ingest;
using JinJi.Ingest;

namespace JinJi.Host.Evaluation;

/// <summary>取込 1 回分の結果。</summary>
internal sealed record StepResult(
    string Label,
    string File,
    ImportResult Import,
    IReadOnlyList<ChangeProposalRecord> Proposals,
    IReadOnlyList<CorrectionRequestRecord> Corrections,
    int Applied,
    IReadOnlyDictionary<string, int> Failures);

/// <summary>組織 1 件の照合（正解の組織 → jin-ji の組織）。</summary>
internal sealed class OrgCheck(string id, string name, bool active)
{
    public string Id { get; } = id;

    public string Name { get; } = name;

    public bool Active { get; } = active;

    public bool Exists { get; set; }

    public bool ParentCorrect { get; set; }

    public bool LevelCorrect { get; set; }

    public bool HeadCorrect { get; set; }

    /// <summary>有効な組織は存在・上位組織・段が一致。廃止済みの組織は jin-ji に（基準日に有効なものとして）無い。</summary>
    public bool Correct { get; set; }
}

/// <summary>人 1 人の最終状態の照合。</summary>
internal sealed class PersonCheck(string id, string status)
{
    public string Id { get; } = id;

    public string Status { get; } = status;

    public bool Exists { get; set; }

    public bool HomeOrgCorrect { get; set; }

    public bool TitleCorrect { get; set; }

    public bool ConcurrentCorrect { get; set; }

    public bool SecondmentCorrect { get; set; }

    public bool LeaveCorrect { get; set; }

    public bool RetiredCorrect { get; set; }
}

/// <summary>名寄せの集計（ファイルごと）。</summary>
internal sealed class MatchStats(string file)
{
    public string File { get; } = file;

    /// <summary>正解の人がいる行。</summary>
    public int Rows { get; set; }

    /// <summary>自動で人に対応付けた行（要確認でない提案の対象が人）。</summary>
    public int Auto { get; set; }

    public int AutoCorrect { get; set; }

    /// <summary>要確認に回った行。</summary>
    public int Review { get; set; }

    /// <summary>要確認のうち、候補に正しい人を含む行。</summary>
    public int ReviewWithCorrectCandidate { get; set; }

    /// <summary>要確認に回るべき行（社員番号の誤記・欠落、所属のあいまいな辞令）。</summary>
    public int Hard { get; set; }

    /// <summary>要確認に回るべき行のうち、要確認に回った行。</summary>
    public int ReviewHard { get; set; }

    public List<string> Wrong { get; } = [];

    public List<string> Missed { get; } = [];
}

/// <summary>辞令 1 行の解釈の照合。</summary>
internal sealed class ActionCheck(int row, string orderType, IReadOnlyList<string> categories)
{
    public int Row { get; } = row;

    public string OrderType { get; } = orderType;

    public IReadOnlyList<string> Categories { get; } = categories;

    public bool Interpreted { get; set; }

    public bool TypeCorrect { get; set; }

    public bool OrgCorrect { get; set; }

    public bool TitleCorrect { get; set; }

    public bool FutureCorrect { get; set; }

    public bool Review { get; set; }

    public string Detail { get; set; } = string.Empty;

    public bool Correct => Interpreted && TypeCorrect && OrgCorrect && TitleCorrect && FutureCorrect;
}

/// <summary>Entra ID の不整合の検出（カテゴリごと）。</summary>
internal sealed record DetectionCheck(string Category, int Expected, int Detected, int FalsePositives);

/// <summary>LLM による候補提示の 1 件の照合（要確認の提案 1 件）。</summary>
/// <param name="Category">対象の種類（LLM に回すべきケース）。</param>
/// <param name="Reference">行・組織（表示用）。</param>
/// <param name="Truth">正解の ID（正解データの ID。複数なら どれでも正解）。</param>
internal sealed class LlmCaseCheck(string category, string reference, IReadOnlyCollection<string> truth)
{
    public string Category { get; } = category;

    public string Reference { get; } = reference;

    public IReadOnlyCollection<string> Truth { get; } = truth;

    /// <summary>LLM の推定が付いたか。</summary>
    public bool Suggested { get; set; }

    /// <summary>LLM の順位のうち正解の順位（1 始まり。無ければ null）。</summary>
    public int? TruthRank { get; set; }

    /// <summary>LLM の第 1 候補（正解データの ID。対応が無ければ "?"）と確信度。</summary>
    public string? Top { get; set; }

    public decimal? TopConfidence { get; set; }

    /// <summary>ルール（LLM なし）の第 1 候補が正解か。ルールが候補を示さない種類は null。</summary>
    public bool? RuleTopCorrect { get; set; }

    public bool TopCorrect => TruthRank == 1;

    public bool InTop3 => TruthRank is >= 1 and <= 3;

    /// <summary>第 1 候補が誤りで、確信度が高い（0.8 以上）。</summary>
    public bool WrongHighConfidence => Suggested && !TopCorrect && TopConfidence >= LlmCaseThresholds.HighConfidence;
}

/// <summary>LLM の評価のしきい値。</summary>
internal static class LlmCaseThresholds
{
    public const decimal HighConfidence = 0.8m;
}

/// <summary>評価用の解決者による要確認の解決の集計（辞令の名寄せ・所属の要確認）。</summary>
internal sealed class ResolutionStats
{
    public int Reviewed { get; set; }

    /// <summary>候補から選んだ（正解の人・組織が候補にあった）。</summary>
    public int Selected { get; set; }

    /// <summary>候補から選んだもののうち、正解が LLM の候補にしか無かった（ルールの候補だけでは選べなかった）。</summary>
    public int SelectedOnlyWithLlm { get; set; }

    /// <summary>修正依頼に回した（正解が候補に無い・発令内容を解釈できない）。</summary>
    public int Referred { get; set; }

    public int Derived { get; set; }

    public int DerivedApplied { get; set; }

    public Dictionary<string, int> Failures { get; } = new(StringComparer.Ordinal);
}

/// <summary>評価の結果。</summary>
internal sealed class EvaluationResult
{
    public DateOnly AsOf { get; init; }

    public List<StepResult> Steps { get; } = [];

    public int PersonsExpected { get; set; }

    public int PersonsCreated { get; set; }

    public int PersonsMatchedToTruth { get; set; }

    public int PersonsDuplicated { get; set; }

    public List<OrgCheck> Orgs { get; } = [];

    public int UnmatchedJinjiOrgs { get; set; }

    public List<PersonCheck> People { get; } = [];

    public List<MatchStats> Matching { get; } = [];

    public List<ActionCheck> Actions { get; } = [];

    public int PendingOrders { get; set; }

    /// <summary>実行日より後の発令のうち、人事管理者に見える行（承認・反映済みの内示）。</summary>
    public int PendingVisibleToAdmin { get; set; }

    /// <summary>実行日より後の発令のうち、一般の閲覧者に見える行（0 であるべき）。</summary>
    public int PendingVisibleToGeneral { get; set; }

    public List<DetectionCheck> Directory { get; } = [];

    public int ForbiddenValuesChecked { get; set; }

    public int ForbiddenValuesFound { get; set; }

    public int LegalNamesChecked { get; set; }

    public int LegalNamesFound { get; set; }

    /// <summary>戸籍名が DB に見つかった人（正解データの ID）。</summary>
    public List<string> LegalNamesFoundIn { get; } = [];

    /// <summary>要確認の解決の後に、戸籍名が DB に見つかった人（正解データの ID）。</summary>
    public List<string> LegalNamesFoundAfterResolution { get; } = [];

    /// <summary>LLM を使ったか（--llm）。</summary>
    public bool LlmUsed { get; init; }

    public List<LlmCaseCheck> LlmCases { get; } = [];

    public ResolutionStats Resolution { get; } = new();

    /// <summary>jin-ji の人 → 正解データの人。</summary>
    public Dictionary<Guid, string> GtOfPerson { get; } = [];

    /// <summary>jin-ji の組織 → 正解データの組織。</summary>
    public Dictionary<Guid, string> GtOfOrg { get; } = [];

    /// <summary>拠点（拠点マスタ・組織の拠点・人の拠点とリモートワーク）。</summary>
    public SiteStats Sites { get; } = new();

    /// <summary>最後の取込（同じファイルの取込が 2 回あれば後のもの）。</summary>
    public StepResult Step(string file) => Steps.Last(s => s.File == file);
}

/// <summary>拠点の評価（0017）。</summary>
internal sealed class SiteStats
{
    /// <summary>拠点マスタの正解の拠点の数と、コード・名称・種別・都道府県・所在地が一致した数。</summary>
    public int SitesExpected { get; set; }

    public int SitesCorrect { get; set; }

    /// <summary>閉鎖済みで jin-ji に無い拠点を作らなかったか。</summary>
    public bool ClosedNotCreated { get; set; }

    /// <summary>有効な組織の数と、自分の拠点（最上位の組織だけが持つ）が正解と一致した数。</summary>
    public int OrgsExpected { get; set; }

    public int OrgsCorrect { get; set; }

    /// <summary>在籍者の数と、人の拠点（上書き → 主務の組織の拠点）が実際の勤務先の拠点と一致した数。</summary>
    public int PeopleExpected { get; set; }

    public int PeopleCorrect { get; set; }

    /// <summary>人ごとの拠点の上書きを持つべき人の数と、持っている数（と、持つべきでないのに持っている数）。</summary>
    public int OverridesExpected { get; set; }

    public int OverridesFound { get; set; }

    public int OverridesUnexpected { get; set; }

    /// <summary>在籍者のリモートワークの有無が正解と一致した数。</summary>
    public int RemoteCorrect { get; set; }

    /// <summary>拠点マスタに無い勤務地（SITE_UNKNOWN）の行と、修正依頼（unknown_site）になった行の数。</summary>
    public int UnknownExpected { get; set; }

    public int UnknownDetected { get; set; }

    /// <summary>一致しなかった人・組織（正解データの ID と理由）。</summary>
    public List<string> Wrong { get; } = [];
}
