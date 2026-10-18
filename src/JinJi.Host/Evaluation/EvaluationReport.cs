using System.Globalization;
using System.Text;
using System.Text.Json;
using JinJi.Application.Ingest;

namespace JinJi.Host.Evaluation;

/// <summary>評価の結果を Markdown の表にする（docs/reports/ingest-accuracy.md）。</summary>
internal static class EvaluationReport
{
    public static string Render(EvaluationResult r, JsonElement truth, string commit, DateTimeOffset runAt)
    {
        var s = new StringBuilder();
        s.AppendLine("# 取込の精度（messy-corp サンプル）");
        s.AppendLine();
        s.AppendLine(r.LlmUsed
            ? "`JinJi.Host evaluate-ingest samples/messy-corp --llm true` の出力（手で編集しない）。LLM は要確認の提案の根拠に推定を付けるだけで提案の状態・値を変えないので、「取込と一括承認」「指標」「残った誤り」は LLM なしの実行と同じ（LLM なしの結果）。"
            : "`JinJi.Host evaluate-ingest samples/messy-corp` の出力（手で編集しない）。");
        s.AppendLine();
        s.AppendLine("| 項目 | 値 |");
        s.AppendLine("| --- | --- |");
        s.AppendLine(CultureInfo.InvariantCulture, $"| 実行日時 | {runAt.ToOffset(TimeSpan.FromHours(9)):yyyy-MM-dd HH:mm} (JST) |");
        s.AppendLine(CultureInfo.InvariantCulture, $"| コミット | `{commit}`（作業ツリーの変更を含む場合あり） |");
        s.AppendLine(CultureInfo.InvariantCulture, $"| 基準日（業務日付） | {r.AsOf:yyyy-MM-dd} |");
        s.AppendLine(CultureInfo.InvariantCulture, $"| 正解の人 / 組織 / 汚れ | {r.PersonsExpected} / {r.Orgs.Count} / {truth.GetProperty("anomalies").GetArrayLength()} |");
        s.AppendLine();

        s.AppendLine("## 取込と一括承認");
        s.AppendLine();
        s.AppendLine("取込の順に実行し、起案（`proposed`）の提案を評価用の承認者がすべて承認した。要確認（`needs_review`）は承認しない。");
        s.AppendLine();
        s.AppendLine("| 取込 | 行 | 捨てた列 | 提案 | 要確認 | 修正依頼 | 承認・反映 | 反映できず |");
        s.AppendLine("| --- | --- | --- | --- | --- | --- | --- | --- |");
        foreach (var step in r.Steps)
        {
            var dropped = step.Import.DroppedColumns.Count == 0 ? "-" : string.Join("、", step.Import.DroppedColumns.Select(d => $"{d.Name}({Kind(d.Kind)})"));
            s.AppendLine(CultureInfo.InvariantCulture, $"| {step.Label} | {step.Import.RowCount} | {dropped} | {step.Import.ProposalCount} | {step.Import.NeedsReviewCount} | {step.Import.CorrectionCount} | {step.Applied} | {step.Failures.Values.Sum()} |");
        }

        var failures = r.Steps.SelectMany(st => st.Failures.Select(f => (st.Label, f.Key, f.Value))).ToList();
        if (failures.Count > 0)
        {
            s.AppendLine();
            s.AppendLine("反映できなかった提案:");
            s.AppendLine();
            s.AppendLine("| 取込 | 理由 | 件数 |");
            s.AppendLine("| --- | --- | --- |");
            foreach (var (label, reason, count) in failures)
            {
                s.AppendLine(CultureInfo.InvariantCulture, $"| {label} | {Escape(reason)} | {count} |");
            }
        }

        s.AppendLine();
        s.AppendLine("## 指標");
        s.AppendLine();
        s.AppendLine("| 指標 | 値 | 内訳 |");
        s.AppendLine("| --- | --- | --- |");
        s.AppendLine(CultureInfo.InvariantCulture, $"| 人の作成（社員番号の正規形で正解と一致） | {Pct(r.PersonsMatchedToTruth, r.PersonsExpected)} | 作成 {r.PersonsCreated}、重複 {r.PersonsDuplicated} |");
        foreach (var m in r.Matching)
        {
            s.AppendLine(CultureInfo.InvariantCulture, $"| 名寄せ（{m.File}）適合率 | {Pct(m.AutoCorrect, m.Auto)} | 自動で対応付け {m.Auto} 行のうち正しい {m.AutoCorrect} |");
            s.AppendLine(CultureInfo.InvariantCulture, $"| 名寄せ（{m.File}）再現率 | {Pct(m.AutoCorrect, m.Rows)} | 正解の人がいる {m.Rows} 行 |");
            s.AppendLine(CultureInfo.InvariantCulture, $"| 要確認（{m.File}） | {m.Review} 行 | 候補に正しい人を含む {m.ReviewWithCorrectCandidate}、要確認に回るべき行 {m.Hard} のうち要確認 {m.ReviewHard}（{Pct(m.ReviewHard, m.Hard)}） |");
        }

        var activeOrgs = r.Orgs.Where(o => o.Active).ToList();
        var abolished = r.Orgs.Where(o => !o.Active).ToList();
        s.AppendLine(CultureInfo.InvariantCulture, $"| 組織ツリーの一致率 | {Pct(r.Orgs.Count(o => o.Correct), r.Orgs.Count)} | 有効な組織 {activeOrgs.Count}: 存在 {activeOrgs.Count(o => o.Exists)}・上位組織 {activeOrgs.Count(o => o.ParentCorrect)}・段 {activeOrgs.Count(o => o.LevelCorrect)}・組織長 {activeOrgs.Count(o => o.HeadCorrect)}／廃止済み {abolished.Count} のうち作らなかった {abolished.Count(o => o.Correct)}／正解に無い組織 {r.UnmatchedJinjiOrgs} |");
        s.AppendLine(CultureInfo.InvariantCulture, $"| 発令の解釈の正解率 | {Pct(r.Actions.Count(a => a.Correct), r.Actions.Count)} | 種類 {r.Actions.Count(a => a.TypeCorrect)}・組織 {r.Actions.Count(a => a.Interpreted && a.OrgCorrect)}・役職 {r.Actions.Count(a => a.Interpreted && a.TitleCorrect)}・内示の判定 {r.Actions.Count(a => a.Interpreted && a.FutureCorrect)}／解釈できず {r.Actions.Count(a => !a.Interpreted)}／要確認 {r.Actions.Count(a => a.Review)} |");
        s.AppendLine(CultureInfo.InvariantCulture, $"| 内示（発令日前の発令）の秘匿 | {(r.PendingVisibleToGeneral == 0 ? "OK" : "NG")} | 正解の内示 {r.PendingOrders} 件。反映済みで実行日より後の発令: 人事管理者に {r.PendingVisibleToAdmin} 件、一般の閲覧者に {r.PendingVisibleToGeneral} 件（RLS） |");
        var living = r.People.Where(p => p.Exists).ToList();
        s.AppendLine(CultureInfo.InvariantCulture, $"| 人の最終状態（基準日） | {Pct(living.Count(p => p.HomeOrgCorrect && p.TitleCorrect && p.ConcurrentCorrect && p.SecondmentCorrect && p.LeaveCorrect && p.RetiredCorrect), r.People.Count)} | 主務 {living.Count(p => p.HomeOrgCorrect)}・役職 {living.Count(p => p.TitleCorrect)}・兼務 {living.Count(p => p.ConcurrentCorrect)}・出向 {living.Count(p => p.SecondmentCorrect)}・休職 {living.Count(p => p.LeaveCorrect)}・退職 {living.Count(p => p.RetiredCorrect)}（{r.People.Count} 人中） |");
        s.AppendLine(CultureInfo.InvariantCulture, $"| 保持禁止の列の値が DB に無い | {(r.ForbiddenValuesFound == 0 ? "OK" : "NG")} | 4 文字以上の値 {r.ForbiddenValuesChecked} 種のうち DB で見つかった {r.ForbiddenValuesFound} |");
        s.AppendLine(CultureInfo.InvariantCulture, $"| 旧姓使用者の戸籍名が DB に無い（要確認の解決前） | {(r.LegalNamesFound == 0 ? "OK" : "NG")} | {r.LegalNamesChecked} 人のうち見つかった {r.LegalNamesFound}{(r.LegalNamesFoundIn.Count == 0 ? string.Empty : "（" + string.Join("、", r.LegalNamesFoundIn) + "。人に対応付けられなかった行。staging の保持期限を短くしてある）")} |");
        s.AppendLine(CultureInfo.InvariantCulture, $"| 旧姓使用者の戸籍名が DB に無い（要確認の解決後） | {(r.LegalNamesFoundAfterResolution.Count == 0 ? "OK" : "NG")} | {r.LegalNamesChecked} 人のうち見つかった {r.LegalNamesFoundAfterResolution.Count}{(r.LegalNamesFoundAfterResolution.Count == 0 ? string.Empty : "（" + string.Join("、", r.LegalNamesFoundAfterResolution) + "）")} |");
        foreach (var d in r.Directory)
        {
            s.AppendLine(CultureInfo.InvariantCulture, $"| Entra の不整合の検出（{d.Category}） | {Pct(d.Detected, d.Expected)} | {d.Expected} 件中 {d.Detected}、誤検出 {d.FalsePositives} |");
        }

        var st = r.Sites;
        s.AppendLine(CultureInfo.InvariantCulture, $"| 拠点マスタ | {Pct(st.SitesCorrect, st.SitesExpected)} | コード・名称・種別・都道府県・所在地が一致／閉鎖済みの拠点を作らなかった {(st.ClosedNotCreated ? "OK" : "NG")} |");
        s.AppendLine(CultureInfo.InvariantCulture, $"| 組織の拠点 | {Pct(st.OrgsCorrect, st.OrgsExpected)} | 有効な組織の自分の拠点（最上位の組織だけ持ち、配下は引き継ぐ） |");
        s.AppendLine(CultureInfo.InvariantCulture, $"| 人の拠点（在籍者） | {Pct(st.PeopleCorrect, st.PeopleExpected)} | 人ごとの拠点 {st.OverridesExpected} 人のうち持っている {st.OverridesFound}・持つべきでないのに持っている {st.OverridesUnexpected}（拠点は正しいが、主務の組織と同じ拠点を重ねて持つ） |");
        s.AppendLine(CultureInfo.InvariantCulture, $"| リモートワークの有無（在籍者） | {Pct(st.RemoteCorrect, st.PeopleExpected)} | 表記の揺れ（○/可/はい/有/対象・×/いいえ/無/-） |");
        s.AppendLine(CultureInfo.InvariantCulture, $"| 拠点マスタに無い勤務地の修正依頼 | {Pct(st.UnknownDetected, st.UnknownExpected)} | SITE_UNKNOWN の行が unknown_site の修正依頼になった数 |");

        s.AppendLine();
        s.AppendLine("指標の定義:");
        s.AppendLine();
        s.AppendLine("- 名寄せ: 辞令・Entra の各行について、要確認でない提案の対象の人を「自動で対応付けた人」とし、正解の人と比べる（Entra の人ではないアカウントは除く）");
        s.AppendLine("- 要確認に回るべき行: 正解データの汚れのうち JIREI_EMPNO_TYPO・JIREI_EMPNO_MISSING・JIREI_AMBIGUOUS_ORG の行");
        s.AppendLine("- 組織ツリー: 有効な組織は、コード（無ければ観測された名称）で対応付け、上位組織と段（本部=division・部=department・課=section・チーム=team・グループ=group・室=段なし）が一致すれば正解。廃止済みの組織は基準日に有効な組織として作っていなければ正解");
        s.AppendLine("- 発令の解釈: 提案（要確認を含む）の発令の種類・組織・役職・発令日前（内示）かが正解と一致すれば正解。出向で本文に組織が無いものは組織を問わない");
        s.AppendLine("- 人の最終状態: 5 ファイルを取り込み起案をすべて承認した後、基準日のスナップショットの主務・役職・兼務・出向・休職・退職を正解と比べる");
        s.AppendLine("- 拠点: 拠点マスタを最初に取り込む。人の拠点は jin-ji の解決（人ごとの拠点 → 主務の組織〔無ければ上位組織〕の拠点）の結果を、正解の実際の勤務先の拠点と比べる。人ごとの拠点は主務の組織の拠点と違う人だけが持つ");

        AppendLlm(s, r);
        AppendResolution(s, r);
        AppendResiduals(s, r, truth);
        return s.ToString();
    }

    private static void AppendLlm(StringBuilder s, EvaluationResult r)
    {
        s.AppendLine();
        s.AppendLine("## LLM による候補提示");
        s.AppendLine();
        var usages = r.Steps.Where(st => st.Import.Llm is not null).Select(st => (st.Label, Usage: st.Import.Llm!)).ToList();
        if (!r.LlmUsed || usages.Count == 0)
        {
            s.AppendLine("LLM は使っていない（`--llm true` で実行すると、要確認の提案に LLM の候補提示を行い、ここに精度を出す）。下の表の「ルール」の列は LLM なしの第 1 候補。");
        }
        else
        {
            // 問いの無かった取込は応答が無く、設定のモデル名になるので除く
            var model = string.Join("、", usages.Where(u => u.Usage.Calls > 0).Select(u => u.Usage.Model).DefaultIfEmpty(usages[0].Usage.Model).Distinct());
            var input = usages.Sum(u => u.Usage.InputTokens);
            var outputTokens = usages.Sum(u => u.Usage.OutputTokens);
            var costs = usages.Select(u => u.Usage.EstimatedCostUsd).ToList();
            var cost = costs.All(c => c is not null) ? string.Create(CultureInfo.InvariantCulture, $"約 {costs.Sum(c => c!.Value):0.0000} USD（設定の単価から計算）") : "単価未設定";
            s.AppendLine("| 項目 | 値 |");
            s.AppendLine("| --- | --- |");
            s.AppendLine(CultureInfo.InvariantCulture, $"| モデル / 送信先 | {model} / {string.Join("、", usages.Select(u => u.Usage.EndpointHost).Distinct())} |");
            s.AppendLine(CultureInfo.InvariantCulture, $"| 問い / 呼び出し（リトライを含む） | {usages.Sum(u => u.Usage.Questions)} / {usages.Sum(u => u.Usage.Calls)} |");
            s.AppendLine(CultureInfo.InvariantCulture, $"| トークン（入力 / 出力） | {input} / {outputTokens} |");
            s.AppendLine(CultureInfo.InvariantCulture, $"| 料金の目安 | {cost} |");
            s.AppendLine(CultureInfo.InvariantCulture, $"| 候補外の ID を捨てた / スキーマ違反 / 失敗 / 上限で回さなかった | {usages.Sum(u => u.Usage.DiscardedIds)} / {usages.Sum(u => u.Usage.SchemaViolations)} / {usages.Sum(u => u.Usage.Failed)} / {usages.Sum(u => u.Usage.Skipped)} |");
            s.AppendLine(CultureInfo.InvariantCulture, $"| 取込ごとの問い | {string.Join("、", usages.Select(u => $"{u.Label} {u.Usage.Questions}"))} |");
            var sent = usages.SelectMany(u => u.Usage.SentFields).GroupBy(f => f.Key, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => $"{g.Key} {g.Sum(f => f.Value)}");
            s.AppendLine(CultureInfo.InvariantCulture, $"| 送った項目（項目名と件数。値は記録しない） | {string.Join("、", sent)} |");
        }

        s.AppendLine();
        s.AppendLine("| ケース | 対象 | LLM の推定あり | LLM の第 1 候補が正解 | 正解が LLM の上位 3 | 誤った高確信度（0.8 以上） | ルールの第 1 候補が正解（LLM なし） |");
        s.AppendLine("| --- | --- | --- | --- | --- | --- | --- |");
        foreach (var group in r.LlmCases.GroupBy(c => c.Category))
        {
            var cases = group.ToList();
            var suggested = cases.Count(c => c.Suggested);
            var rule = cases.Where(c => c.RuleTopCorrect is not null).ToList();
            s.AppendLine(CultureInfo.InvariantCulture, $"| {group.Key} | {cases.Count} | {suggested} | {(r.LlmUsed ? Pct(cases.Count(c => c.TopCorrect), cases.Count) : "-")} | {(r.LlmUsed ? Pct(cases.Count(c => c.InTop3), cases.Count) : "-")} | {(r.LlmUsed ? cases.Count(c => c.WrongHighConfidence).ToString(CultureInfo.InvariantCulture) : "-")} | {(rule.Count == 0 ? "-（ルールは候補を示さない）" : Pct(rule.Count(c => c.RuleTopCorrect == true), cases.Count))} |");
        }

        var all = r.LlmCases;
        if (r.LlmUsed && all.Count > 0)
        {
            s.AppendLine(CultureInfo.InvariantCulture, $"| 合計 | {all.Count} | {all.Count(c => c.Suggested)} | {Pct(all.Count(c => c.TopCorrect), all.Count)} | {Pct(all.Count(c => c.InTop3), all.Count)} | {all.Count(c => c.WrongHighConfidence)} | {Pct(all.Count(c => c.RuleTopCorrect == true), all.Count(c => c.RuleTopCorrect is not null))}（ルールが候補を示すケースだけ） |");
        }

        s.AppendLine();
        s.AppendLine("定義:");
        s.AppendLine();
        s.AppendLine("- 対象: 取込の精度で「ルールでは決まらない」とした 4 種。社員番号の誤記・欠落（JIREI_EMPNO_TYPO・JIREI_EMPNO_MISSING の行の人）、所属のあいまいな辞令（JIREI_AMBIGUOUS_ORG の行の組織）、段飛ばしの組織の位置（結合セルの補いで上位組織を決めたチームの要確認。正解は正解データの上位組織）、Entra の旧組織名（ENTRA_STALE_DEPT。正解は廃止組織の後継組織か、本人の現在の主務の組織）");
        s.AppendLine("- 割合の分母は対象の件数（LLM の推定が無い・失敗したものは不正解として数える）。LLM が並べた候補のうち正解の順位で数える");
        s.AppendLine("- ルールの第 1 候補: 辞令の人は名寄せの候補の先頭、所属のあいまいな辞令は本人の現在の所属から決めた組織、段飛ばしは結合セルの補いで決めた上位組織");

        var misses = all.Where(c => r.LlmUsed && !c.TopCorrect).ToList();
        if (misses.Count > 0)
        {
            s.AppendLine();
            s.AppendLine("LLM の第 1 候補が正解でなかったもの:");
            s.AppendLine();
            s.AppendLine("| ケース | 対象 | 第 1 候補（確信度） | 正解の順位 | 正解 |");
            s.AppendLine("| --- | --- | --- | --- | --- |");
            foreach (var c in misses)
            {
                var top = c.Top is null ? "-" : string.Create(CultureInfo.InvariantCulture, $"{c.Top}（{c.TopConfidence:0.00}）");
                s.AppendLine(CultureInfo.InvariantCulture, $"| {c.Category} | {Escape(c.Reference)} | {top} | {(c.TruthRank?.ToString(CultureInfo.InvariantCulture) ?? (c.Suggested ? "圏外" : "推定なし"))} | {string.Join("・", c.Truth)} |");
            }
        }
    }

    private static void AppendResolution(StringBuilder s, EvaluationResult r)
    {
        var x = r.Resolution;
        s.AppendLine();
        s.AppendLine("## 要確認の解決（評価用の解決者）");
        s.AppendLine();
        s.AppendLine("辞令の名寄せ・所属の要確認を、評価用の解決者（実行者・承認者とは別の人事管理者）が正解データに従って解決した（人の確認の代わり。正解の人・組織が候補にあれば「候補から選ぶ」、無ければ「修正依頼に回す」）。解決の結果の提案は承認者が承認した。");
        s.AppendLine();
        s.AppendLine("| 項目 | 値 |");
        s.AppendLine("| --- | --- |");
        s.AppendLine(CultureInfo.InvariantCulture, $"| 要確認 | {x.Reviewed} |");
        s.AppendLine(CultureInfo.InvariantCulture, $"| 候補から選んだ | {x.Selected}（うち正解が LLM の候補にしか無かった {x.SelectedOnlyWithLlm}） |");
        s.AppendLine(CultureInfo.InvariantCulture, $"| 修正依頼に回した | {x.Referred} |");
        s.AppendLine(CultureInfo.InvariantCulture, $"| 解決の結果の提案 / 承認・反映 | {x.Derived} / {x.DerivedApplied} |");
        foreach (var (reason, count) in x.Failures)
        {
            s.AppendLine(CultureInfo.InvariantCulture, $"| 失敗: {Escape(reason)} | {count} |");
        }
    }

    private static void AppendResiduals(StringBuilder s, EvaluationResult r, JsonElement truth)
    {
        var orgCategories = truth.GetProperty("anomalies").EnumerateArray()
            .Where(a => a.GetProperty("org_id").ValueKind == JsonValueKind.String && a.GetProperty("file").GetString() == "org_chart_2026-04.csv")
            .GroupBy(a => a.GetProperty("org_id").GetString()!)
            .ToDictionary(g => g.Key, g => g.Select(a => a.GetProperty("category").GetString()!).Distinct().ToList());

        s.AppendLine();
        s.AppendLine("## 残った誤り");
        s.AppendLine();
        s.AppendLine("### 組織");
        s.AppendLine();
        s.AppendLine("組織ツリー（上位組織・段）の誤りと、組織長の不一致（組織長は一致率に含めない）。");
        s.AppendLine();
        s.AppendLine("| 組織 | 名称 | 存在 | 上位組織 | 段 | 組織長 | 汚れのカテゴリ |");
        s.AppendLine("| --- | --- | --- | --- | --- | --- | --- |");
        foreach (var o in r.Orgs.Where(o => !o.Correct || (o.Active && !o.HeadCorrect)))
        {
            s.AppendLine(CultureInfo.InvariantCulture, $"| {o.Id} | {o.Name}{(o.Active ? string.Empty : "（廃止済み）")} | {Mark(o.Exists)} | {Mark(o.ParentCorrect)} | {Mark(o.LevelCorrect)} | {Mark(o.HeadCorrect)} | {string.Join("、", orgCategories.GetValueOrDefault(o.Id) ?? [])} |");
        }

        s.AppendLine();
        s.AppendLine("### 辞令");
        s.AppendLine();
        s.AppendLine("| 行 | 解釈（正解） | 要確認 | 汚れのカテゴリ |");
        s.AppendLine("| --- | --- | --- | --- |");
        foreach (var a in r.Actions.Where(a => !a.Correct))
        {
            s.AppendLine(CultureInfo.InvariantCulture, $"| {a.Row} | {Escape(a.Detail)} | {Mark(a.Review)} | {string.Join("、", a.Categories)} |");
        }

        s.AppendLine();
        s.AppendLine("### 名寄せ");
        s.AppendLine();
        foreach (var m in r.Matching)
        {
            s.AppendLine(CultureInfo.InvariantCulture, $"- {m.File}: 誤った対応付け {m.Wrong.Count} 件{(m.Wrong.Count == 0 ? string.Empty : "（" + string.Join("、", m.Wrong) + "）")}、提案も要確認も無い行 {m.Missed.Count} 件{(m.Missed.Count == 0 ? string.Empty : "（" + string.Join("、", m.Missed.Take(10)) + (m.Missed.Count > 10 ? " ほか" : string.Empty) + "）")}");
        }

        if (r.Sites.Wrong.Count > 0)
        {
            s.AppendLine();
            s.AppendLine("### 拠点");
            s.AppendLine();
            foreach (var wrong in r.Sites.Wrong)
            {
                s.AppendLine(CultureInfo.InvariantCulture, $"- {wrong}");
            }
        }

        s.AppendLine();
        s.AppendLine("### 人の最終状態");
        s.AppendLine();
        s.AppendLine("| 人 | 状態 | 主務 | 役職 | 兼務 | 出向 | 休職 | 退職 |");
        s.AppendLine("| --- | --- | --- | --- | --- | --- | --- | --- |");
        foreach (var p in r.People.Where(p => !(p.Exists && p.HomeOrgCorrect && p.TitleCorrect && p.ConcurrentCorrect && p.SecondmentCorrect && p.LeaveCorrect && p.RetiredCorrect)))
        {
            s.AppendLine(CultureInfo.InvariantCulture, $"| {p.Id} | {p.Status}{(p.Exists ? string.Empty : "（無し）")} | {Mark(p.HomeOrgCorrect)} | {Mark(p.TitleCorrect)} | {Mark(p.ConcurrentCorrect)} | {Mark(p.SecondmentCorrect)} | {Mark(p.LeaveCorrect)} | {Mark(p.RetiredCorrect)} |");
        }
    }

    private static string Kind(DroppedColumnKind kind) => kind switch
    {
        DroppedColumnKind.Forbidden => "保持禁止",
        DroppedColumnKind.Ignored => "無視",
        _ => "対応なし",
    };

    private static string Mark(bool value) => value ? "○" : "×";

    private static string Pct(int numerator, int denominator) =>
        denominator == 0 ? "-" : string.Create(CultureInfo.InvariantCulture, $"{100.0 * numerator / denominator:0.0}%（{numerator}/{denominator}）");

    private static string Escape(string text) => text.Replace("|", "\\|", StringComparison.Ordinal);
}
