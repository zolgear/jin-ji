using JinJi.Policy;

namespace JinJi.Application.People;

/// <summary>
/// 人の参照の設定（appsettings の <c>JinJi:Directory</c>）。
/// </summary>
public sealed record PersonDirectoryOptions
{
    /// <summary>設定の節の名前。</summary>
    public const string SectionName = "JinJi:Directory";

    /// <summary>
    /// 退職者を人事管理者以外の検索・参照に出す日数（退職年月日の翌日から数える。既定 90 日。ADR-0002「退職後の一定期間で一般公開をやめる」）。
    /// 過ぎた人は人事管理者だけが参照できる。保持期限を過ぎた後の匿名化は別の手順で行う。
    /// </summary>
    public int RetiredPublicDays { get; init; } = PersonProjector.DefaultRetiredPublicDays;

    /// <summary>
    /// 一斉異動とみなす発令の件数の下限（既定 10）。同じ発令日の異動・役職の任免・兼務・出向の発令がこの件数以上あれば、
    /// メタ情報（<c>GET /api/v1/meta</c>）の時点の目盛りに出す。件数そのものは返さない。
    /// </summary>
    public int MassActionThreshold { get; init; } = 10;

    /// <summary>
    /// 当日の所在（項目 presence。外部の提供 <c>IPresenceProvider</c> を注入したときだけ）の公開範囲（関係スコープの表記。既定は本人と同じ部 <c>same_level(department)</c>）。
    /// ポリシー（policy_rule）でさらに狭められる。
    /// </summary>
    public string PresenceVisibilityScope { get; init; } = PresencePolicy.DefaultScope;

    /// <summary>値を検査する。</summary>
    public void Validate()
    {
        if (MassActionThreshold < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(MassActionThreshold), MassActionThreshold, "一斉異動とみなす発令の件数は 1 以上で指定してください。");
        }

        if (RetiredPublicDays < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(RetiredPublicDays), RetiredPublicDays, "退職者の公開日数は 0 以上で指定してください。");
        }

        if (RelationScopes.Parse(PresenceVisibilityScope).Kind == RelationScopeKind.None)
        {
            throw new ArgumentException("当日の所在の公開範囲に none は使えません（所在を出さないなら IPresenceProvider を登録しないでください）。", nameof(PresenceVisibilityScope));
        }
    }
}
