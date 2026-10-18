using JinJi.Policy;

namespace JinJi.Application.Access;

/// <summary>
/// レート制限の設定（appsettings の <c>JinJi:RateLimit</c>。アーキテクチャ概要 §7）。経路（API・MCP）ごとに別の値を持つ。
/// </summary>
/// <remarks>
/// MCP は別ポートで待ち受け、レート制限も MCP 専用に持つ（§2）。MCP のエンドポイント種別はツールごとに入口の共通処理が決める（§7.3）。
/// </remarks>
public sealed class RateLimitOptions
{
    /// <summary>設定の節の名前。</summary>
    public const string SectionName = "JinJi:RateLimit";

    /// <summary>REST API の制限。</summary>
    public RateLimitProfile Api { get; init; } = new();

    /// <summary>MCP の制限（既定は API より厳しい）。</summary>
    public RateLimitProfile Mcp { get; init; } = new()
    {
        PerClient = 600,
        PerUser = 120,
        ThrottledPerClient = 30,
        ReturnedPeoplePerUser = 1000,
        ReturnedPeoplePerClient = 5000,
    };

    /// <summary>経路の制限。</summary>
    public RateLimitProfile For(Channel channel) => channel == Channel.Mcp ? Mcp : Api;

    /// <summary>値を検査する。不正なら起動させない。</summary>
    public void Validate()
    {
        Api.Validate(nameof(Api));
        Mcp.Validate(nameof(Mcp));
    }
}

/// <summary>1 つの経路のレート制限。リクエスト数は <see cref="Window"/> あたり、返却人数は <see cref="ReturnedPeopleWindow"/> あたり。</summary>
public sealed class RateLimitProfile
{
    /// <summary>リクエスト数を数える窓（既定 1 分。スライディングウィンドウ）。</summary>
    public TimeSpan Window { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>クライアントあたりのリクエスト数（全利用者・全エンドポイントの合計）。</summary>
    public int PerClient { get; init; } = 1200;

    /// <summary>クライアント × 利用者あたりのリクエスト数。</summary>
    public int PerUser { get; init; } = 300;

    /// <summary>
    /// クライアント × 利用者 × エンドポイント種別あたりのリクエスト数（キーは <see cref="RequestKinds.ToCode"/>）。
    /// 無い種別は <see cref="PerUser"/> だけで制限する。
    /// </summary>
    public Dictionary<string, int> PerEndpoint { get; init; } = new(StringComparer.Ordinal)
    {
        ["person_read"] = 120,
        ["person_list"] = 30,
        ["org_read"] = 120,
        ["org_stats"] = 30,
        ["changes"] = 60,
        ["admin"] = 30,
        ["ingest"] = 60,
        ["sync"] = 30,
        ["correction"] = 30,
    };

    /// <summary>不正検知で自動スロットル中のクライアントのリクエスト数（クライアントの合計）。</summary>
    public int ThrottledPerClient { get; init; } = 60;

    /// <summary>未認証のリクエストの、接続元アドレスあたりのリクエスト数（ヘルスチェック・OpenAPI 文書・401 になる要求）。</summary>
    public int AnonymousPerAddress { get; init; } = 120;

    /// <summary>返却人数を数える窓（既定 1 時間）。</summary>
    public TimeSpan ReturnedPeopleWindow { get; init; } = TimeSpan.FromHours(1);

    /// <summary>クライアント × 利用者あたりの返却人数（大量抽出対策）。</summary>
    public int ReturnedPeoplePerUser { get; init; } = 3000;

    /// <summary>クライアントあたりの返却人数。</summary>
    public int ReturnedPeoplePerClient { get; init; } = 20000;

    /// <summary>エンドポイント種別の制限。無ければ null。</summary>
    public int? PerEndpointFor(RequestKind kind) => PerEndpoint.TryGetValue(RequestKinds.ToCode(kind), out var limit) ? limit : null;

    /// <summary>値を検査する。</summary>
    public void Validate(string name)
    {
        if (Window <= TimeSpan.Zero || ReturnedPeopleWindow <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(name, "レート制限の窓は正の時間で指定してください。");
        }

        foreach (var (label, value) in new[]
                 {
                     (nameof(PerClient), PerClient), (nameof(PerUser), PerUser), (nameof(ThrottledPerClient), ThrottledPerClient),
                     (nameof(AnonymousPerAddress), AnonymousPerAddress), (nameof(ReturnedPeoplePerUser), ReturnedPeoplePerUser),
                     (nameof(ReturnedPeoplePerClient), ReturnedPeoplePerClient),
                 })
        {
            if (value <= 0)
            {
                throw new ArgumentOutOfRangeException(name, value, $"{name}:{label} は 1 以上で指定してください。");
            }
        }

        var known = Enum.GetValues<RequestKind>().Select(RequestKinds.ToCode).ToHashSet(StringComparer.Ordinal);
        foreach (var (kind, value) in PerEndpoint)
        {
            if (!known.Contains(kind))
            {
                throw new ArgumentException($"{name}:PerEndpoint に未知のエンドポイント種別があります: {kind}", name);
            }

            if (value <= 0)
            {
                throw new ArgumentOutOfRangeException(name, value, $"{name}:PerEndpoint:{kind} は 1 以上で指定してください。");
            }
        }
    }
}
