// jin-ji のエントリポイント。
//
//   JinJi.Host [--role all|api|mcp|worker]   サーバーとして起動する（既定は all）
//   JinJi.Host migrate                        SQL マイグレーションを適用して終了する
//   JinJi.Host import <mapping.json> <file> --executed-by <person-id>   開発用の取込（作業領域に提案を作る）
//   JinJi.Host sync-plan / sync-script / sync-result / sync-apply          Entra ID への同期計画・スクリプト出力・実行結果の取込・直接反映
//   JinJi.Host correction-report / notify-corrections                      修正依頼のレポート（CSV / xlsx）・通知の 1 回の実行
//   JinJi.Host create-local-account / create-break-glass-account / issue-reset-link / unlock-account / bind-login / list-unbound-logins / grant-role / revoke-role
//                                                                         認証の管理（ADR-0004）
//   JinJi.Host register-client / update-client / list-clients / suspend-client / ... / revoke-tokens
//                                                                         トークン発行のクライアントの管理（ADR-0004 の段階 B）
//
// 役割は環境変数 JINJI_ROLE でも指定できる。
using JinJi.Host;

return await HostCommand.RunAsync(args);
