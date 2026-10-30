namespace JinJi.Application.Retention;

/// <summary>
/// 保持期限の消去の設定（appsettings の <c>JinJi:Retention</c>。アーキテクチャ概要 §4.5）。期間は、行が終わった時点（終了・期限・解決）から数える。
/// </summary>
/// <remarks>
/// <para>
/// 既定値の考え方: 長く残す理由は「直近の事故の調査」だけで、長期の証跡は監査ログ（消さない）が持つ。
/// 認証の行は監査ログに無い情報（最終利用日時・IdP が示した氏名・メール）を持つので、月次・四半期の点検に間に合う長さにとどめる。
/// </para>
/// <para>
/// DB の RLS にも下限（終わった行だけ・一部は 1 日）がある（0014）。ここで短くしても、下限より先の行は消えない。
/// </para>
/// </remarks>
public sealed record RetentionOptions
{
    /// <summary>設定の節の名前。</summary>
    public const string SectionName = "JinJi:Retention";

    /// <summary>ワーカーの定期実行を行うか（既定 true。false でも管理 CLI の <c>purge-expired</c> は使える）。</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>定期実行の間隔（既定 1 時間。1 分〜1 日）。</summary>
    public TimeSpan Interval { get; init; } = TimeSpan.FromHours(1);

    /// <summary>ワーカーの起動から最初の実行までの待ち（既定 5 分。起動直後の負荷と重ねない）。</summary>
    public TimeSpan InitialDelay { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// UI のセッション（終了か最長の期限から。既定 30 日）。ログイン・ログアウト・終了の事実は監査ログに残るので、
    /// セッションの行（最終利用日時・認証の方法）は直近の乗っ取りの調査と月次の点検に要る間だけ持つ。
    /// </summary>
    public TimeSpan UiSessions { get; init; } = TimeSpan.FromDays(30);

    /// <summary>
    /// 紐付けできなかったログイン（最後の試行か解決から。既定 90 日）。IdP が示した氏名・メールを含み、jin-ji の利用者でない人のこともあるので短くしたいが、
    /// 人事管理者が四半期ごとの点検で未解決のものを見落とさない長さにする。再び試行されれば新しい行ができる。
    /// </summary>
    public TimeSpan UnboundLogins { get; init; } = TimeSpan.FromDays(90);

    /// <summary>管理者が発行したリンク（期限から。既定 30 日。リンクの寿命は 24 時間以内で、使用・発行の事実は監査ログに残る）。</summary>
    public TimeSpan ResetLinks { get; init; } = TimeSpan.FromDays(30);

    /// <summary>
    /// 認可の要求・認可コード・アクセストークン・リフレッシュトークン（期限から。既定 7 日）。寿命は分〜日の単位で数が多く（委任 1 件で 10 分ごとに 1 行）、
    /// 発行・失効の事実は監査ログに残る。生きている系列のコードは再利用の検知のため系列と一緒に消す。
    /// </summary>
    public TimeSpan OAuthTokens { get; init; } = TimeSpan.FromDays(7);

    /// <summary>
    /// トークンの系列と、その同意の記録（終了か最長の期限から。既定 30 日）。系列の最長は既定 30 日なので、付与から最長 60 日で消える。
    /// 「いつ、どのクライアントに委任していたか」は監査ログ（auth.consent・auth.token.*）に残る。
    /// </summary>
    public TimeSpan TokenFamilies { get; init; } = TimeSpan.FromDays(30);

    /// <summary>
    /// 承認されないままの CIMD のクライアント（記録から。既定 30 日）。未認証の要求で作られる行なので溜めない。
    /// 管理者が承認しないまま消えたら、次の認可の要求で改めて承認待ちとして記録する（却下したものは消さない）。
    /// </summary>
    public TimeSpan PendingClients { get; init; } = TimeSpan.FromDays(30);

    /// <summary>素案（最後の更新から。既定 365 日。ADR-0013 決定 8）。決める前に試す案なので、使われなくなったものを溜めない。</summary>
    public TimeSpan Plans { get; init; } = TimeSpan.FromDays(365);

    /// <summary>1 回の DELETE で消す行数の上限（既定 5,000）。ロックを短くするため、上限まで消したら次の DELETE を別のトランザクションで行う。</summary>
    public int BatchSize { get; init; } = 5000;

    /// <summary>値を検査する。</summary>
    public void Validate()
    {
        if (Interval < TimeSpan.FromMinutes(1) || Interval > TimeSpan.FromDays(1))
        {
            throw new ArgumentOutOfRangeException(nameof(Interval), Interval, "消去の間隔は 1 分〜1 日で指定してください。");
        }

        if (InitialDelay < TimeSpan.Zero || InitialDelay > TimeSpan.FromDays(1))
        {
            throw new ArgumentOutOfRangeException(nameof(InitialDelay), InitialDelay, "最初の実行までの待ちは 0〜1 日で指定してください。");
        }

        foreach (var (name, value) in new[]
                 {
                     (nameof(UiSessions), UiSessions), (nameof(UnboundLogins), UnboundLogins), (nameof(ResetLinks), ResetLinks),
                     (nameof(OAuthTokens), OAuthTokens), (nameof(TokenFamilies), TokenFamilies), (nameof(PendingClients), PendingClients),
                     (nameof(Plans), Plans),
                 })
        {
            // 1 日より短くしても DB の下限（0014）で消えない行があり、設定と実際が食い違うので受け付けない
            if (value < TimeSpan.FromDays(1) || value > TimeSpan.FromDays(3660))
            {
                throw new ArgumentOutOfRangeException(name, value, $"{name} は 1 日〜3660 日で指定してください。");
            }
        }

        if (BatchSize is < 100 or > 100_000)
        {
            throw new ArgumentOutOfRangeException(nameof(BatchSize), BatchSize, "1 回の DELETE の行数は 100〜100,000 で指定してください。");
        }
    }
}
