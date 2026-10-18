using System.Security.Cryptography;
using System.Text.Json;
using JinJi.Application.Ingest;
using JinJi.Ingest;
using JinJi.Ingest.Normalization;

namespace JinJi.Connectors;

/// <summary>
/// 列が階層になっている表（組織図: 会社 / 本部 / 部・室 / 課・グループ / チーム）の読み方。1 行 1 組織で、上位の列は結合セル由来で空欄のことが多い。
/// </summary>
/// <param name="EntityColumn">会社（法人）の列。空欄なら上の行の値を補う。</param>
/// <param name="Columns">階層の列（上から順）。</param>
/// <param name="NoCarrySuffixes">
/// この語で終わる組織（「室」など）は、上の列に置かれていても下の行の上位組織として補わない（同じ行に明示されたときだけ上位組織にする）。
/// 会社直下の室の次の行にある部を、その室の配下と誤らないため。
/// </param>
/// <param name="SkipCandidateColumns">
/// 結合セルの補いで上位組織を決めたとき、段飛ばし（補った上位組織を飛ばして、さらに上の組織の直下）の可能性も残す列（例: チーム）。
/// その行には代わりの上位組織の候補（<c>$parent_org_alt_name</c>）を出し、取込で要確認（上位組織の候補）にする。
/// </param>
public sealed record HierarchySpec(string? EntityColumn, IReadOnlyList<string> Columns, IReadOnlyList<string> NoCarrySuffixes, IReadOnlyList<string>? SkipCandidateColumns = null);

/// <summary>取込元のファイルの読み方。</summary>
public sealed record SourceFormat
{
    /// <summary>取込元の種類。null ならファイルの拡張子（.csv / .tsv / .txt / .xlsx）で決める。</summary>
    public ImportSourceKind? Kind { get; init; }

    /// <summary>CSV の文字コード: auto（UTF-8 / UTF-8 BOM / UTF-16 BOM / Shift_JIS(CP932) を判定）、utf-8、shift_jis。</summary>
    public string Encoding { get; init; } = "auto";

    /// <summary>CSV の区切り文字（既定はカンマ。.tsv はタブ）。</summary>
    public char? Delimiter { get; init; }

    /// <summary>見出しの行番号（1 始まり）。表題の行がある Excel などで 2 以上を指定する。</summary>
    public int HeaderRow { get; init; } = 1;

    /// <summary>Excel のシート名。null なら最初のシート。</summary>
    public string? Sheet { get; init; }

    /// <summary>Excel の結合セルの値を、結合範囲のすべてのセルに補完する（組織図の Excel で上位組織の列が結合されている場合など）。</summary>
    public bool FillMergedCells { get; init; }

    /// <summary>列が階層になっている組織図の読み方（null なら通常の表）。</summary>
    public HierarchySpec? Hierarchy { get; init; }
}

/// <summary>
/// 項目マッピング（取込元の列名 → jin-ji の取込項目）。設定データ（JSON）として持つ。変更は承認の対象（§4.6・§5.5。承認の仕組みは後の手順）。
/// </summary>
/// <remarks>
/// <code>
/// {
///   "name": "hr-export-people",
///   "target": "people",                       // people | org_units | personnel_actions | directory_users
///   "source": { "kind": "csv", "encoding": "auto", "headerRow": 1 },
///   "legalEntityCode": "A",
///   "employeeNumber": { "padLength": 6 },
///   "stagingRetentionDays": 90,
///   "unidentifiedStagingRetentionDays": 14,  // 人に対応付けられなかった行（氏名が戸籍名の可能性）
///   "fields": { "employee_number": ["社員番号", "社員No"], "full_name": ["氏名"] },
///   "ignore": ["備考"],
///   "defaults": { "employment_type": "正社員" }
/// }
/// </code>
/// <list type="bullet">
/// <item>fields のキーは取込項目の許可リスト（<see cref="IngestFields.Mappable"/>）にあるものだけ。値は候補の列名（先に書いたものを優先）</item>
/// <item>候補の列名・無視する列名に保持しない項目を表す名前（生年月日・性別・住所など）は書けない（読み込み時に拒否する）</item>
/// <item>列名の比較は NFKC・空白の除去・小文字で行う（「社員 No.」と「社員No.」は同じ）</item>
/// </list>
/// </remarks>
public sealed class ConnectorMapping
{
    private static readonly JsonDocumentOptions DocumentOptions = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    private ConnectorMapping(
        string name,
        ImportProfile profile,
        SourceFormat source,
        IReadOnlyDictionary<string, IReadOnlyList<string>> fields,
        IReadOnlyList<string> ignore,
        IReadOnlyDictionary<string, IReadOnlyList<string>> discardWhenPresent,
        string sha256)
    {
        DiscardWhenPresent = discardWhenPresent;
        Name = name;
        Profile = profile;
        Source = source;
        Fields = fields;
        Ignore = ignore;
        Sha256 = sha256;
    }

    public string Name { get; }

    /// <summary>コネクタに依らない取込の設定（対象・法人・社員番号の規則・既定値・保持日数）。</summary>
    public ImportProfile Profile { get; }

    public SourceFormat Source { get; }

    /// <summary>取込項目 → 候補の列名（優先順）。</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Fields { get; }

    /// <summary>無視する列名（捨てた列として「ignored」で記録する）。</summary>
    public IReadOnlyList<string> Ignore { get; }

    /// <summary>
    /// 列 → 取込項目の一覧: その列に値がある行では、一覧の項目を捨てる（例: 通称（旧姓使用）がある行では、戸籍名の読みである氏名カナを捨てる）。
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> DiscardWhenPresent { get; }

    /// <summary>マッピングの JSON の SHA-256（小文字の 16 進。取込単位に記録する）。</summary>
    public string Sha256 { get; }

    /// <summary>列名の比較用の表記（NFKC・空白の除去・小文字）。</summary>
    public static string HeaderKey(string header)
    {
        ArgumentNullException.ThrowIfNull(header);
        return TextNormalizer.ToSearchKey(header) ?? string.Empty;
    }

    /// <summary>JSON から読み込み、検査する。不正なら <see cref="FormatException"/>。</summary>
    public static ConnectorMapping Parse(ReadOnlySpan<byte> json)
    {
        var sha256 = Convert.ToHexStringLower(SHA256.HashData(json));
        using var document = JsonDocument.Parse(json.ToArray(), DocumentOptions);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new FormatException("項目マッピングは JSON のオブジェクトで書いてください。");
        }

        var name = RequiredString(root, "name");
        var target = RequiredString(root, "target") switch
        {
            "people" => ImportTargetKind.People,
            "org_units" => ImportTargetKind.OrgUnits,
            "personnel_actions" => ImportTargetKind.PersonnelActions,
            "directory_users" => ImportTargetKind.DirectoryUsers,
            "sites" => ImportTargetKind.Sites,
            "floor_layouts" => ImportTargetKind.FloorLayouts,
            "workplace_assignments" => ImportTargetKind.WorkplaceAssignments,
            var other => throw new FormatException($"target は people / org_units / personnel_actions / directory_users / sites / floor_layouts / workplace_assignments のいずれかです: {other}"),
        };

        var fields = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        if (!root.TryGetProperty("fields", out var fieldsElement) || fieldsElement.ValueKind != JsonValueKind.Object)
        {
            throw new FormatException("fields（取込項目 → 候補の列名）がありません。");
        }

        var usedHeaders = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in fieldsElement.EnumerateObject())
        {
            if (!IngestFields.Mappable.Contains(property.Name))
            {
                throw new FormatException(IngestFields.Retired.Contains(property.Name)
                    ? $"取込項目 {property.Name} は廃止しました（勤務地は site_name に対応させ、拠点マスタに照合します）。"
                    : $"取込項目の許可リストに無い項目です: {property.Name}");
            }

            // 拠点の所在地（事業所の住所）などは拠点マスタの取込だけ。拠点マスタの取込は拠点の項目だけ
            if (target != ImportTargetKind.Sites && IngestFields.SiteMasterOnly.Contains(property.Name))
            {
                throw new FormatException($"取込項目 {property.Name} は拠点マスタの取込（target: sites）にだけ書けます。");
            }

            if (target == ImportTargetKind.Sites && !IngestFields.SiteMaster.Contains(property.Name))
            {
                throw new FormatException($"拠点マスタの取込（target: sites）に書けない取込項目です: {property.Name}");
            }

            // 配置図・割り当ての取込は、それぞれの項目だけ（人の属性を配置図の取込で書かせない・席の座標を人の取込で書かせない）
            var workplaceFields = target switch
            {
                ImportTargetKind.FloorLayouts => IngestFields.FloorLayout,
                ImportTargetKind.WorkplaceAssignments => IngestFields.WorkplaceAssignment,
                _ => null,
            };
            if (workplaceFields is not null && !workplaceFields.Contains(property.Name))
            {
                throw new FormatException($"この取込の対象に書けない取込項目です: {property.Name}");
            }

            if (workplaceFields is null && IngestFields.WorkplaceOnly.Contains(property.Name))
            {
                throw new FormatException($"取込項目 {property.Name} は配置図・割り当ての取込（target: floor_layouts / workplace_assignments）にだけ書けます。");
            }

            var candidates = StringArray(property.Value, $"fields.{property.Name}");
            if (candidates.Count == 0)
            {
                throw new FormatException($"fields.{property.Name} に候補の列名がありません。");
            }

            foreach (var candidate in candidates)
            {
                RejectForbidden(candidate, $"fields.{property.Name}");
                if (!usedHeaders.TryAdd(HeaderKey(candidate), property.Name))
                {
                    throw new FormatException($"列名 {candidate} が複数の取込項目の候補になっています。");
                }
            }

            fields[property.Name] = candidates;
        }

        var ignore = root.TryGetProperty("ignore", out var ignoreElement) ? StringArray(ignoreElement, "ignore") : [];
        foreach (var column in ignore)
        {
            RejectForbidden(column, "ignore");
            if (usedHeaders.ContainsKey(HeaderKey(column)))
            {
                throw new FormatException($"列名 {column} は取込項目の候補と無視する列の両方にあります。");
            }
        }

        var defaults = new Dictionary<string, string>(StringComparer.Ordinal);
        if (root.TryGetProperty("defaults", out var defaultsElement))
        {
            if (defaultsElement.ValueKind != JsonValueKind.Object)
            {
                throw new FormatException("defaults はオブジェクトで書いてください。");
            }

            foreach (var property in defaultsElement.EnumerateObject())
            {
                defaults[property.Name] = property.Value.ValueKind == JsonValueKind.String
                    ? property.Value.GetString()!
                    : throw new FormatException($"defaults.{property.Name} は文字列で書いてください。");
            }
        }

        var numbers = new EmployeeNumberRules();
        if (root.TryGetProperty("employeeNumber", out var numberElement))
        {
            numbers = new EmployeeNumberRules
            {
                PadLength = numberElement.TryGetProperty("padLength", out var pad) && pad.ValueKind == JsonValueKind.Number ? pad.GetInt32() : null,
                PrefixedPadLength = numberElement.TryGetProperty("prefixedPadLength", out var prefixed) && prefixed.ValueKind == JsonValueKind.Number ? prefixed.GetInt32() : null,
                RemoveSeparators = !numberElement.TryGetProperty("removeSeparators", out var separators) || separators.GetBoolean(),
                UpperCase = !numberElement.TryGetProperty("upperCase", out var upper) || upper.GetBoolean(),
            };
        }

        var profile = new ImportProfile
        {
            Target = target,
            LegalEntityCode = OptionalString(root, "legalEntityCode"),
            EmployeeNumber = numbers,
            Defaults = defaults,
            StagingRetentionDays = root.TryGetProperty("stagingRetentionDays", out var days) ? days.GetInt32() : ImportProfile.DefaultStagingRetentionDays,
            UnidentifiedStagingRetentionDays = root.TryGetProperty("unidentifiedStagingRetentionDays", out var unidentifiedDays) ? unidentifiedDays.GetInt32() : ImportProfile.DefaultUnidentifiedStagingRetentionDays,
            AllowExcelSerialDates = !root.TryGetProperty("allowExcelSerialDates", out var serial) || serial.GetBoolean(),
            LegalEntityAliases = ParseAliases(root),
            SourceSystem = OptionalString(root, "sourceSystem") ?? "entra_id",
            FullSnapshot = root.TryGetProperty("fullSnapshot", out var full) && full.GetBoolean(),
        };

        var discard = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        if (root.TryGetProperty("discardWhenPresent", out var discardElement))
        {
            foreach (var property in discardElement.EnumerateObject())
            {
                var targets = StringArray(property.Value, $"discardWhenPresent.{property.Name}");
                var unknown = targets.Where(t => !IngestFields.Mappable.Contains(t)).ToList();
                if (unknown.Count > 0)
                {
                    throw new FormatException($"discardWhenPresent.{property.Name} に取込項目ではない名前があります: {string.Join(", ", unknown)}");
                }

                discard[property.Name] = targets;
            }
        }

        try
        {
            profile.Validate();
        }
        catch (ArgumentException ex)
        {
            throw new FormatException(ex.Message, ex);
        }

        return new ConnectorMapping(name, profile, ParseSource(root), fields, ignore, discard, sha256);
    }

    private static SourceFormat ParseSource(JsonElement root)
    {
        if (!root.TryGetProperty("source", out var source))
        {
            return new SourceFormat();
        }

        var kind = OptionalString(source, "kind") switch
        {
            null => (ImportSourceKind?)null,
            "csv" => ImportSourceKind.Csv,
            "xlsx" => ImportSourceKind.Xlsx,
            "graph_json" => ImportSourceKind.GraphJson,
            "layout_json" => ImportSourceKind.LayoutJson,
            var other => throw new FormatException($"source.kind は csv / xlsx / graph_json / layout_json のいずれかです: {other}"),
        };
        var encoding = OptionalString(source, "encoding") ?? "auto";
        if (encoding is not ("auto" or "utf-8" or "shift_jis"))
        {
            throw new FormatException($"source.encoding は auto / utf-8 / shift_jis のいずれかです: {encoding}");
        }

        var delimiter = OptionalString(source, "delimiter") switch
        {
            null => (char?)null,
            "\\t" or "\t" => '\t',
            { Length: 1 } d => d[0],
            var other => throw new FormatException($"source.delimiter は 1 文字です: {other}"),
        };
        var headerRow = source.TryGetProperty("headerRow", out var header) ? header.GetInt32() : 1;
        if (headerRow < 1)
        {
            throw new FormatException("source.headerRow は 1 以上です。");
        }

        return new SourceFormat
        {
            Kind = kind,
            Encoding = encoding,
            Delimiter = delimiter,
            HeaderRow = headerRow,
            Sheet = OptionalString(source, "sheet"),
            FillMergedCells = source.TryGetProperty("fillMergedCells", out var fill) && fill.GetBoolean(),
            Hierarchy = source.TryGetProperty("hierarchy", out var hierarchy) ? ParseHierarchy(hierarchy) : null,
        };
    }

    private static HierarchySpec ParseHierarchy(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty("columns", out var columns))
        {
            throw new FormatException("source.hierarchy には columns（階層の列。上から順）が要ります。");
        }

        var list = StringArray(columns, "source.hierarchy.columns");
        if (list.Count == 0)
        {
            throw new FormatException("source.hierarchy.columns が空です。");
        }

        return new HierarchySpec(
            OptionalString(element, "entityColumn"),
            list,
            element.TryGetProperty("noCarrySuffixes", out var suffixes) ? StringArray(suffixes, "source.hierarchy.noCarrySuffixes") : [],
            element.TryGetProperty("skipCandidateColumns", out var skip) ? StringArray(skip, "source.hierarchy.skipCandidateColumns") : []);
    }

    private static Dictionary<string, string> ParseAliases(JsonElement root)
    {
        var aliases = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!root.TryGetProperty("legalEntityAliases", out var element))
        {
            return aliases;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new FormatException("legalEntityAliases は「法人のコード → 別名の配列」で書いてください。");
        }

        foreach (var property in element.EnumerateObject())
        {
            foreach (var alias in StringArray(property.Value, $"legalEntityAliases.{property.Name}"))
            {
                aliases[alias] = property.Name;
            }
        }

        return aliases;
    }

    private static void RejectForbidden(string column, string where)
    {
        if (ForbiddenColumnNames.IsForbidden(column))
        {
            throw new FormatException($"{where} に保持しない項目（ADR-0002）を表す列名があります: {column}");
        }
    }

    private static string RequiredString(JsonElement element, string name) =>
        OptionalString(element, name) is { Length: > 0 } value ? value : throw new FormatException($"{name} がありません。");

    private static string? OptionalString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static List<string> StringArray(JsonElement element, string where)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            return [element.GetString()!];
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            throw new FormatException($"{where} は文字列か文字列の配列で書いてください。");
        }

        return [.. element.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(e.GetString())
            ? e.GetString()!
            : throw new FormatException($"{where} に空の列名か文字列でない値があります。"))];
    }
}
