namespace JinJi.Host;

/// <summary>
/// 配備側の起動の入口（拡張点）。jin-ji のホストを参照する小さな起動プロジェクトから呼び、Web（API・MCP）の役割のサービスを差し替える。
/// </summary>
/// <remarks>
/// 当日の所在の提供（<see cref="Application.Presence.IPresenceProvider"/>）を登録する例:
/// <code>
/// return await JinJiHost.RunAsync(args, services =>
///     services.Replace(ServiceDescriptor.Singleton&lt;IPresenceProvider, 自前の提供&gt;()));
/// </code>
/// 既定（差し替えない）は「提供なし」で、所在の機能は無効になる。jin-ji は返った所在に必ず自分の認可を適用し、保持・配信・監査ログへの記録をしない。
/// 外部への問い合わせ（認証・タイムアウト・失敗時の扱い）は提供の実装の責任で、失敗したら例外ではなく空の結果を返すことを勧める（画面の他の部分を止めない）。
/// </remarks>
public static class JinJiHost
{
    /// <summary>コマンドラインどおりに起動する（<c>JinJi.Host</c> の実行ファイルと同じ）。</summary>
    /// <param name="args">コマンドライン。</param>
    /// <param name="configureServices">Web の役割で、jin-ji のサービスの登録の後に呼ぶ（差し替え）。</param>
    public static Task<int> RunAsync(string[] args, Action<IServiceCollection> configureServices)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(configureServices);
        return HostCommand.RunAsync(args, configureServices);
    }
}
