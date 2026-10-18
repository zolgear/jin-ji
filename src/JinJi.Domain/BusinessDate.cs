namespace JinJi.Domain;

/// <summary>
/// 業務日付（日本時間の日付）。発令日の到来・内示の判定・時点指定の既定値に使う。
/// </summary>
/// <remarks>
/// DB 側の <c>jinji_business_date()</c>（RLS が使う）と同じく、日本時間（UTC+9、夏時間なし）で日付を決める。
/// DB の <c>current_date</c> はセッションのタイムゾーンに依存し、UTC のサーバーでは 0:00〜9:00 の間に日付がずれるため使わない。
/// tzdata に依存しないよう、固定の時差で計算する。
/// </remarks>
public static class BusinessDate
{
    /// <summary>日本時間の UTC からの時差。</summary>
    public static readonly TimeSpan UtcOffset = TimeSpan.FromHours(9);

    /// <summary>現在の業務日付。</summary>
    public static DateOnly Today(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        return DateOnly.FromDateTime(timeProvider.GetUtcNow().ToOffset(UtcOffset).DateTime);
    }
}
