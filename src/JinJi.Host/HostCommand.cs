namespace JinJi.Host;

/// <summary>コマンドラインを解釈し、サーバー起動かマイグレーションに振り分ける。</summary>
internal static class HostCommand
{
    private static readonly string Usage = $"""
        使い方:
          JinJi.Host [--role all|api|mcp|worker]   サーバーとして起動する（既定は all。環境変数 JINJI_ROLE でも指定可）
          JinJi.Host migrate                        SQL マイグレーションを適用して終了する（接続文字列 ConnectionStrings:Owner）
          JinJi.Host import <mapping.json> <file> --executed-by <person-id> [--effective-date yyyy-MM-dd] [--llm true]
                                                    開発用: CSV / xlsx を取り込み、作業領域に変更提案・修正依頼を作る（接続文字列 ConnectionStrings:App）
          JinJi.Host reconcile-work-locations --executed-by <person-id>
                                                    取込済みの自由記述の勤務地（0017 で廃止）を拠点マスタに照合し、人ごとの拠点の変更提案・修正依頼を作る
          JinJi.Host evaluate-ingest <サンプルのディレクトリ> [--output <path>] [--llm true --llm-model <モデル名>]
                                                    取込の精度の評価（一時 DB にサンプルを取り込み、正解データと比べる）
          JinJi.Host sync-plan <entra_users.json> --executed-by <person-id> [--settings <sync.json>]
                                                    Entra ID への同期計画を作る（承認は別の人事管理者が REST API で行う）
          JinJi.Host sync-script <plan-id> <出力先ディレクトリ>
                                                    承認済みの計画の PowerShell スクリプト・データ・差分レポートを書き出す
          JinJi.Host sync-result <plan-id> <実行ログの JSON>
                                                    スクリプトの実行ログを取り込み、計画の項目ごとに照合する
          JinJi.Host sync-apply <plan-id> [--what-if true]
                                                    Graph への直接反映（既定は無効。JinJi:Sync:DirectApply）
          JinJi.Host evaluate-sync <サンプルのディレクトリ> [--output <path>] [--script-output <dir>]
                                                    同期計画の評価（サンプルを取り込んだ一時 DB から計画を作り、正解データと比べる）
          JinJi.Host purge-expired                  保持期限を過ぎた行（セッション・トークン・リンク・staging など）を消す（設定 JinJi:Retention。ワーカーの定期実行と同じ）
        {CorrectionCommands.Usage}
        {AuthCommands.Usage}
        {OAuthCommands.Usage}

        主な設定（環境変数の例）:
          JinJi__Listen__Address   待ち受けるアドレス（既定 127.0.0.1。コンテナ内では 0.0.0.0 を指定する）
          JinJi__Listen__ApiPort   API のポート（既定 28080）
          JinJi__Listen__McpPort   MCP のポート（既定 28090）
          ConnectionStrings__Owner マイグレーション用の接続文字列（jinji_owner）
          ConnectionStrings__App   アプリ用の接続文字列（jinji_app。取込・ワーカー）
          JinJi__Auth__Login__PublicOrigin            利用者から見た公開 URL（外部 IdP の redirect_uri・パスキー・リンクの URL）
          JinJi__DataProtection__Certificate__Path    Data Protection の鍵を暗号化する証明書（または __Base64 で PKCS#12 を渡す）
        """;

    /// <param name="configureServices">配備側の拡張点（<see cref="JinJiHost"/>）: Web の役割のサービスの登録の後に差し替える。</param>
    public static async Task<int> RunAsync(string[] args, Action<IServiceCollection>? configureServices = null)
    {
        if (args.Length > 0 && args[0] is "-h" or "--help" or "help")
        {
            Console.WriteLine(Usage);
            return 0;
        }

        if (args.Length > 0 && args[0] == "migrate")
        {
            return await MigrateCommand.RunAsync(args[1..]);
        }

        if (args.Length > 0 && args[0] == "import")
        {
            return await ImportCommand.RunAsync(args[1..]);
        }

        if (args.Length > 0 && args[0] == "reconcile-work-locations")
        {
            return await WorkLocationCommand.RunAsync(args[1..]);
        }

        if (args.Length > 0 && args[0] == "evaluate-ingest")
        {
            return await Evaluation.EvaluateIngestCommand.RunAsync(args[1..]);
        }

        if (args.Length > 0 && args[0] == "purge-expired")
        {
            return await RetentionCommand.RunAsync(args[1..]);
        }

        if (args.Length > 0 && args[0] is "correction-report" or "notify-corrections")
        {
            return args[0] == "correction-report" ? await CorrectionCommands.ReportAsync(args[1..]) : await CorrectionCommands.NotifyAsync(args[1..]);
        }

        if (args.Length > 0 && AuthCommands.Commands.Contains(args[0]))
        {
            return await AuthCommands.RunAsync(args[0], args[1..]);
        }

        if (args.Length > 0 && OAuthCommands.Commands.Contains(args[0]))
        {
            return await OAuthCommands.RunAsync(args[0], args[1..]);
        }

        if (args.Length > 0 && args[0] is "sync-plan" or "sync-script" or "sync-result" or "sync-apply" or "evaluate-sync")
        {
            return args[0] switch
            {
                "sync-plan" => await SyncCommands.PlanAsync(args[1..]),
                "sync-script" => await SyncCommands.ScriptAsync(args[1..]),
                "sync-result" => await SyncCommands.ResultAsync(args[1..]),
                "sync-apply" => await SyncCommands.ApplyAsync(args[1..]),
                _ => await Evaluation.EvaluateSyncCommand.RunAsync(args[1..]),
            };
        }

        HostRole role;
        try
        {
            role = HostRoles.Resolve(args);
        }
        catch (ArgumentException ex)
        {
            await Console.Error.WriteLineAsync(ex.Message);
            await Console.Error.WriteLineAsync(Usage);
            return 2;
        }

        if (role == HostRole.Worker)
        {
            using var workerHost = ServerHost.BuildWorkerOnly(args);
            await workerHost.RunAsync();
            return 0;
        }

        await using var app = ServerHost.BuildWeb(role, args, configureServices);
        await app.RunAsync();
        return 0;
    }
}
