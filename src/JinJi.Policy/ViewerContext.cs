namespace JinJi.Policy;

/// <summary>操作者の種別（audit_event.actor_type と対応）。</summary>
public enum ActorType
{
    /// <summary>人（UI・API をユーザーとして使う）。</summary>
    User,

    /// <summary>社内システム（Client Credentials。人に紐付かない）。</summary>
    Service,

    /// <summary>AI エージェント（ユーザーからの委任で動く。単独の権限は持たない）。</summary>
    Agent,

    /// <summary>jin-ji 自身（取込・同期のワーカー、CLI）。</summary>
    System,

    /// <summary>
    /// 緊急用の管理者アカウント（person に紐付かない。権限は認証の復旧だけ）。監査ログの操作者を表すだけで、
    /// 人事情報を参照する閲覧者（<see cref="ViewerContext"/>）は作れない。
    /// </summary>
    BreakGlass,
}

/// <summary>経路（audit_event.channel と対応）。</summary>
public enum Channel
{
    Ui,
    Api,
    Mcp,
    Worker,
    Cli,
}

/// <summary>ロール名。</summary>
public static class Roles
{
    /// <summary>人事管理者。内示・氏名の履歴・監査ログ・ポリシーの変更を扱える。</summary>
    public const string HrAdmin = "hr_admin";

    /// <summary>
    /// 修正担当者（人事パッケージで直せる人）。修正依頼の参照・状態の更新（対応済み・対応不要）ができる。人事データの公開範囲・内示は人事管理者と違って広がらない。
    /// </summary>
    public const string HrCorrector = "hr_corrector";

    /// <summary>
    /// 組織管理者（IT 部門を想定。ADR-0013）。全社の組織と人の直接登録・担当の指名・管理元の起案ができる。氏名の履歴・取込の作業領域・監査ログ・内示は人事管理者と違って見えない。
    /// </summary>
    public const string OrgAdmin = "org_admin";

    /// <summary>既知のロール。</summary>
    public static readonly IReadOnlySet<string> Known = new HashSet<string>(StringComparer.Ordinal) { HrAdmin, HrCorrector, OrgAdmin };
}

/// <summary>
/// クライアント（社内システム・AI エージェント・UI）ごとの「渡してよい項目の上限」。
/// </summary>
public sealed class FieldLimit
{
    private readonly HashSet<string>? _fields;

    private FieldLimit(HashSet<string>? fields) => _fields = fields;

    /// <summary>上限なし（jin-ji 自身の UI など、項目を絞らないクライアント）。明示的に選ぶこと。</summary>
    public static FieldLimit Unrestricted { get; } = new(null);

    /// <summary>上限がないか。</summary>
    public bool IsUnrestricted => _fields is null;

    /// <summary>指定した項目だけを渡してよい上限。未知の項目名は拒否する。</summary>
    public static FieldLimit Of(IEnumerable<string> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        var set = fields.ToHashSet(StringComparer.Ordinal);
        var unknown = set.Where(f => !PersonFields.All.Contains(f)).ToList();
        if (unknown.Count > 0)
        {
            throw new ArgumentException($"未知の項目名: {string.Join(", ", unknown)}", nameof(fields));
        }

        return new FieldLimit(set);
    }

    /// <summary>
    /// クライアントの設定・登録の上限。<c>["*"]</c> を含めば、明示が要る項目（<see cref="PersonFields.ExplicitOnly"/>。当日の所在）を除く全項目と、
    /// 一緒に明示した項目。未知の項目名は拒否する。
    /// </summary>
    public static FieldLimit ForClient(IEnumerable<string> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        var list = fields.ToList();
        if (!list.Contains(PersonFields.Wildcard))
        {
            return Of(list);
        }

        var explicitFields = Of(list.Where(f => f != PersonFields.Wildcard))._fields!;
        return new FieldLimit(PersonFields.All.Where(f => !PersonFields.ExplicitOnly.Contains(f) || explicitFields.Contains(f)).ToHashSet(StringComparer.Ordinal));
    }

    /// <summary>項目を渡してよいか。</summary>
    public bool Allows(string field) => _fields is null || _fields.Contains(field);
}

/// <summary>
/// 閲覧者のコンテキスト。認可エンジンと RLS（DB の GUC）の両方に渡す。
/// </summary>
/// <remarks>
/// 生成はファクトリメソッドに限り、操作者の種別ごとの不変条件（人が要る・クライアント ID が要る等）を守る。
/// AI エージェントは単独の権限を持たず、委任元ユーザーの権限で判定する（アーキテクチャ概要 §6.2）。
/// </remarks>
public sealed class ViewerContext
{
    private ViewerContext(
        ActorType actor,
        Guid? personId,
        Guid? delegatorPersonId,
        string? clientId,
        IEnumerable<string> roles,
        FieldLimit fieldLimit,
        Channel channel,
        string? requestId,
        Guid? apiClientRef = null,
        int disclosureLeadDays = 0)
    {
        ArgumentNullException.ThrowIfNull(roles);
        ArgumentNullException.ThrowIfNull(fieldLimit);
        var roleSet = roles.ToHashSet(StringComparer.Ordinal);
        var unknown = roleSet.Where(r => !Policy.Roles.Known.Contains(r)).ToList();
        if (unknown.Count > 0)
        {
            throw new ArgumentException($"未知のロール: {string.Join(", ", unknown)}", nameof(roles));
        }

        if (clientId is not null && string.IsNullOrWhiteSpace(clientId))
        {
            throw new ArgumentException("クライアント ID が空です。", nameof(clientId));
        }

        Actor = actor;
        PersonId = personId;
        DelegatorPersonId = delegatorPersonId;
        ClientId = clientId;
        Roles = roleSet;
        FieldLimit = fieldLimit;
        Channel = channel;
        RequestId = requestId;
        ApiClientRef = apiClientRef;
        DisclosureLeadDays = disclosureLeadDays;
    }

    /// <summary>先行提供の日数の上限（DB の CHECK 制約 api_client_disclosure_lead_days_check と同じ）。</summary>
    public const int MaxDisclosureLeadDays = 90;

    public ActorType Actor { get; }

    /// <summary>閲覧者本人（操作者が人の場合）。</summary>
    public Guid? PersonId { get; }

    /// <summary>委任元のユーザー（操作者が AI エージェントの場合）。</summary>
    public Guid? DelegatorPersonId { get; }

    /// <summary>クライアント ID（社内システム・AI エージェント・UI のクライアント）。</summary>
    public string? ClientId { get; }

    public IReadOnlySet<string> Roles { get; }

    /// <summary>クライアントの項目上限。</summary>
    public FieldLimit FieldLimit { get; }

    public Channel Channel { get; }

    /// <summary>リクエスト ID（監査ログの突き合わせ用）。</summary>
    public string? RequestId { get; }

    /// <summary>jin-ji が発行したトークンの社内システムのクライアント（api_client.id）。先行提供の日数を持つときだけ RLS に渡す。</summary>
    public Guid? ApiClientRef { get; }

    /// <summary>
    /// 先行提供の日数（社内システムだけ。有効日のこの日数前から将来の予定を見せる。0 は例外なし）。
    /// 人の委任（AI エージェント）・人・jin-ji 自身には常に 0（アーキテクチャ概要 §4「将来の予定の秘匿」の例外）。
    /// </summary>
    public int DisclosureLeadDays { get; }

    /// <summary>関係スコープの判定に使う人（本人、または AI エージェントの委任元）。社内システムには無い。</summary>
    public Guid? EffectivePersonId => PersonId ?? DelegatorPersonId;

    public bool IsHrAdmin => Roles.Contains(Policy.Roles.HrAdmin);

    /// <summary>
    /// 人事管理者以外に将来の予定を見せてよい日の上限（業務日付 + 先行提供の日数。RLS の jinji_viewer_horizon() と同じ）。
    /// 人事管理者は上限なし（<see cref="DateOnly.MaxValue"/>）。時点指定（as_of）に指定できる最後の日でもある。
    /// </summary>
    public DateOnly DisclosureHorizon(DateOnly today) => IsHrAdmin ? DateOnly.MaxValue : today.AddDays(DisclosureLeadDays);

    /// <summary>
    /// 先行提供を外した閲覧者（今日以前の時点の参照・今日のスナップショットの読み込みに使う。日数 0 の閲覧者はそのまま返す）。
    /// 今日以前を見るときは、先行提供の有無で RLS の行も投影も変えない（キャッシュも従来の区分で共有できる）。
    /// </summary>
    public ViewerContext WithoutDisclosureLead() =>
        DisclosureLeadDays == 0 ? this : new ViewerContext(Actor, PersonId, DelegatorPersonId, ClientId, Roles, FieldLimit, Channel, RequestId);

    /// <summary>修正依頼のすべてを参照できるか（人事管理者か修正担当者。AI エージェントは委任元のロール）。</summary>
    public bool HandlesCorrections => IsHrAdmin || Roles.Contains(Policy.Roles.HrCorrector);

    /// <summary>全社の直接登録の範囲を持つか（人事管理者か組織管理者。AI エージェントは委任元のロール。ADR-0013）。</summary>
    public bool ManagesAllOrgs => IsHrAdmin || Roles.Contains(Policy.Roles.OrgAdmin);

    /// <summary>人のユーザー。</summary>
    public static ViewerContext ForUser(
        Guid personId,
        IEnumerable<string> roles,
        Channel channel,
        FieldLimit fieldLimit,
        string? clientId = null,
        string? requestId = null)
    {
        RequireChannel(channel, Channel.Ui, Channel.Api, Channel.Mcp);
        return new ViewerContext(ActorType.User, personId, null, clientId, roles, fieldLimit, channel, requestId);
    }

    /// <summary>AI エージェント（委任元ユーザーのロールをそのまま渡す）。</summary>
    public static ViewerContext ForAgent(
        string clientId,
        Guid delegatorPersonId,
        IEnumerable<string> delegatorRoles,
        Channel channel,
        FieldLimit fieldLimit,
        string? requestId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        RequireChannel(channel, Channel.Mcp, Channel.Api);
        return new ViewerContext(ActorType.Agent, null, delegatorPersonId, clientId, delegatorRoles, fieldLimit, channel, requestId);
    }

    /// <summary>社内システム（人に紐付かないので、関係スコープは group だけが成り立つ）。</summary>
    /// <param name="apiClientRef">jin-ji が発行したトークンのクライアント（api_client.id）。外部の発行者の JWT のクライアントは null。</param>
    /// <param name="disclosureLeadDays">先行提供の日数（承認済み。0〜<see cref="MaxDisclosureLeadDays"/>）。<paramref name="apiClientRef"/> が要る。</param>
    public static ViewerContext ForService(string clientId, FieldLimit fieldLimit, string? requestId = null, Guid? apiClientRef = null, int disclosureLeadDays = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentOutOfRangeException.ThrowIfNegative(disclosureLeadDays);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(disclosureLeadDays, MaxDisclosureLeadDays);
        if (disclosureLeadDays > 0 && apiClientRef is null)
        {
            throw new ArgumentException("先行提供は jin-ji が発行したトークンのクライアント（api_client）にだけ設定できます。", nameof(apiClientRef));
        }

        return new ViewerContext(ActorType.Service, null, null, clientId, [], fieldLimit, Channel.Api, requestId, apiClientRef, disclosureLeadDays);
    }

    /// <summary>jin-ji 自身（取込ワーカー・CLI）。書き込みと内示の参照のため人事管理者のロールを持つ。</summary>
    public static ViewerContext ForSystem(Channel channel, string? requestId = null)
    {
        RequireChannel(channel, Channel.Worker, Channel.Cli);
        return new ViewerContext(ActorType.System, null, null, null, [Policy.Roles.HrAdmin], FieldLimit.Unrestricted, channel, requestId);
    }

    private static void RequireChannel(Channel channel, params Channel[] allowed)
    {
        if (!allowed.Contains(channel))
        {
            throw new ArgumentException($"この操作者の種別では経路 {channel} を使えません。", nameof(channel));
        }
    }
}
