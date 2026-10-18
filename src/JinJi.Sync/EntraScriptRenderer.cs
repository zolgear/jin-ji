using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using JinJi.Application.Sync;

namespace JinJi.Sync;

/// <summary>
/// 承認済みの同期計画から、Microsoft Graph PowerShell（<c>Microsoft.Graph</c> モジュール）のスクリプト一式を作る（overview §8「同期」のスクリプト出力）。
/// </summary>
/// <remarks>
/// <para>出力するファイル（<c>jinji-entra-sync-{計画 ID}</c> + 拡張子）</para>
/// <list type="bullet">
/// <item><c>.ps1</c>: スクリプト本体（UTF-8 BOM・CRLF。Windows PowerShell 5.1 と PowerShell 7 の両方で読める）。計画の値は埋め込まない</item>
/// <item><c>.data.json</c>: スクリプトが読む値（ASCII だけの JSON。非 ASCII と記号は \u でエスケープするので、文字コードの解釈の違いが起きない）。
/// スクリプトはこのファイルの SHA-256 を埋め込みの値と照合し、違えば何もせずに止まる</item>
/// <item><c>.report.md</c> / <c>.report.csv</c>: 差分レポート（IT 全般統制の項目を先頭に。CSV は UTF-8 BOM で Excel でも読める）</item>
/// </list>
/// <para><b>インジェクション対策</b>: スクリプトに埋め込むのは生成側が形を保証した値（計画 ID・SHA-256・日付・権限のスコープ・件数）だけ。
/// 氏名・組織名などの値はデータのファイルから読み、変数として Graph のコマンドレットの引数・本文に渡すだけで、式やコマンドとして評価しない
/// （Invoke-Expression・文字列からのスクリプトブロックは使わない）。オブジェクト ID は UUID の形をスクリプトでも確かめてから URL に入れる。
/// レポートの値は Markdown・HTML の記号をエスケープし、CSV は数式として解釈される先頭の文字（= + - @ タブ CR）を無害化する。</para>
/// <para><b>冪等性と事前条件</b>: スクリプトは各ユーザーの更新前に現在値を読み、項目ごとに「計画時の値」か「あるべき値」かを確かめる。
/// すべてがあるべき値なら何もしない（already_applied）。どちらでもない項目が 1 つでもあれば、計画の後に Entra 側が変わったとみなしてスキップし記録する
/// （skipped_precondition）。あるべき値でない項目だけを更新する。</para>
/// </remarks>
public sealed class EntraScriptRenderer : ISyncScriptRenderer
{
    public const string DataFormat = "jinji-entra-sync-data/1";
    public const string LogFormat = "jinji-entra-sync-log/1";

    private static readonly UTF8Encoding Utf8Bom = new(encoderShouldEmitUTF8Identifier: true);
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    // 既定のエンコーダは非 ASCII・HTML の記号（< > & ' " ` +）を \u でエスケープする（データのファイルを ASCII だけにする）
    private static readonly JsonSerializerOptions DataJson = new() { WriteIndented = true };

    /// <summary>ファイル名の共通部分。</summary>
    public static string BaseName(Guid planId) => $"jinji-entra-sync-{planId:D}";

    public SyncScriptBundle Render(SyncPlanRecord plan, IReadOnlyList<SyncPlanItem> items)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(items);
        var data = Data(plan, items);
        var sha = Convert.ToHexStringLower(SHA256.HashData(data));
        var name = BaseName(plan.Id);
        var files = new SortedDictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [name + ".ps1"] = Encode(PowerShellScript.Render(plan, items, sha, name + ".data.json"), Utf8Bom),
            [name + ".data.json"] = data,
            [name + ".report.md"] = Encode(SyncReport.Markdown(plan, items, sha), Utf8),
            [name + ".report.csv"] = Encode(SyncReport.Csv(items), Utf8Bom),
        };
        return new SyncScriptBundle(files, sha);
    }

    public string DataSha256(SyncPlanRecord plan, IReadOnlyList<SyncPlanItem> items)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(items);
        return Convert.ToHexStringLower(SHA256.HashData(Data(plan, items)));
    }

    /// <summary>スクリプトが読むデータ（決定的: 同じ計画なら同じバイト列）。スクリプトに含める項目だけ。</summary>
    public static byte[] Data(SyncPlanRecord plan, IReadOnlyList<SyncPlanItem> items)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(items);
        var document = new
        {
            format = DataFormat,
            planId = plan.Id.ToString("D"),
            businessDate = plan.BusinessDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            items = items.Where(i => i.InScript).OrderBy(i => i.Sequence).Select(i => new
            {
                itemId = i.Id.ToString("D"),
                sequence = i.Sequence,
                priority = SyncPlanService.Code(i.Priority),
                action = SyncPlanService.Code(i.Action),
                objectId = i.ObjectId,
                userPrincipalName = i.UserPrincipalName,
                employeeNumber = i.EmployeeNumber,
                changes = i.Changes.Select(c => new { field = c.Field, current = c.Current, desired = c.Desired, currentKnown = c.CurrentKnown }),
            }),
        };
        var json = JsonSerializer.Serialize(document, DataJson).Replace("\r\n", "\n", StringComparison.Ordinal) + "\n";
        if (json.Any(c => c > '\u007e'))
        {
            throw new InvalidOperationException("データの JSON に ASCII 以外の文字が残っています（エンコーダの設定を確かめてください）。");
        }

        return Encoding.ASCII.GetBytes(json);
    }

    /// <summary>
    /// 計画の実行に要る最小のスコープ（Microsoft Graph の委任されたアクセス許可）。
    /// 属性・上長・作成があれば User.ReadWrite.All（accountEnabled の変更も含む）、無効化・有効化だけなら User.EnableDisableAccount.All と User.Read.All。
    /// </summary>
    public static IReadOnlyList<string> WriteScopes(IEnumerable<SyncPlanItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var actions = items.Where(i => i.InScript).Select(i => i.Action).ToHashSet();
        if (actions.Overlaps([SyncAction.UpdateAttributes, SyncAction.SetManager, SyncAction.CreateUser]))
        {
            return ["User.ReadWrite.All"];
        }

        return actions.Count == 0 ? ["User.Read.All"] : ["User.EnableDisableAccount.All", "User.Read.All"];
    }

    private static byte[] Encode(string text, UTF8Encoding encoding)
    {
        var body = encoding.GetBytes(text);
        var preamble = encoding.GetPreamble();
        return [.. preamble, .. body];
    }
}
