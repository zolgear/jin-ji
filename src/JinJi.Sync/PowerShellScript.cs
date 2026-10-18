using System.Globalization;
using System.Text.RegularExpressions;
using JinJi.Application.Sync;

namespace JinJi.Sync;

/// <summary>
/// Microsoft Graph PowerShell のスクリプト本体（テンプレート）。埋め込むのは形を確かめた定数（計画 ID・SHA-256・日付・スコープ・件数・ファイル名）だけ。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Windows PowerShell 5.1 と PowerShell 7 の両方で動く書き方に限る（三項演算子・?? ・ -Parallel などは使わない）</item>
/// <item><c>-WhatIf</c>: <c>SupportsShouldProcess</c>。読み取りのスコープ（User.Read.All）だけで接続し、現在値を読んで事前条件を確かめ、反映する予定を記録する</item>
/// <item>エラーは項目ごとに捕まえて記録し、次の項目へ続ける。ログ（JSON・CSV、UTF-8 BOM）は finally で必ず書く（中断しても残る）</item>
/// <item>データのファイルの SHA-256・計画 ID・形式を確かめてから始める（取り違え・改ざんの防止）</item>
/// </list>
/// </remarks>
internal static partial class PowerShellScript
{
    public static string Render(SyncPlanRecord plan, IReadOnlyList<SyncPlanItem> items, string dataSha256, string dataFileName)
    {
        var scriptItems = items.Where(i => i.InScript).ToList();
        var writeScopes = EntraScriptRenderer.WriteScopes(items);
        var constants = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["PLAN_ID"] = plan.Id.ToString("D"),
            ["DATA_SHA256"] = Require(dataSha256, Sha256Pattern()),
            ["DATA_FILE"] = Require(dataFileName, FileNamePattern()),
            ["BUSINESS_DATE"] = plan.BusinessDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["APPROVED_AT"] = plan.DecidedAt?.ToUniversalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? "-",
            ["APPROVER"] = plan.DecidedByPersonId?.ToString("D") ?? "-",
            ["PROPOSER"] = plan.ProposedByPersonId.ToString("D"),
            ["EXPIRES_AT"] = plan.ExpiresAt.ToUniversalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
            ["WRITE_SCOPES"] = string.Join(", ", writeScopes.Select(s => $"'{Require(s, ScopePattern())}'")),
            ["WRITE_SCOPES_TEXT"] = string.Join(" + ", writeScopes),
            ["COUNT_TOTAL"] = Count(scriptItems.Count),
            ["COUNT_DISABLE"] = Count(scriptItems.Count(i => i.Action == SyncAction.DisableAccount)),
            ["COUNT_BLOCK"] = Count(scriptItems.Count(i => i.Action == SyncAction.BlockSignIn)),
            ["COUNT_ENABLE"] = Count(scriptItems.Count(i => i.Action == SyncAction.EnableAccount)),
            ["COUNT_UPDATE"] = Count(scriptItems.Count(i => i.Action == SyncAction.UpdateAttributes)),
            ["COUNT_MANAGER"] = Count(scriptItems.Count(i => i.Action == SyncAction.SetManager)),
            ["COUNT_CREATE"] = Count(scriptItems.Count(i => i.Action == SyncAction.CreateUser)),
            ["LOG_FORMAT"] = EntraScriptRenderer.LogFormat,
            ["DATA_FORMAT"] = EntraScriptRenderer.DataFormat,
        };

        var text = Placeholder().Replace(Template, m => constants.TryGetValue(m.Groups[1].Value, out var value)
            ? value
            : throw new InvalidOperationException($"スクリプトのテンプレートに未知の差し込み {m.Value} があります。"));
        return text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal);
    }

    private static string Count(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Require(string value, Regex pattern) =>
        pattern.IsMatch(value) ? value : throw new InvalidOperationException($"スクリプトに埋め込めない値です: {value}");

    [GeneratedRegex(@"__([A-Z0-9_]+)__", RegexOptions.CultureInvariant)]
    private static partial Regex Placeholder();

    [GeneratedRegex(@"^[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Pattern();

    [GeneratedRegex(@"^[A-Za-z0-9._-]{1,120}$", RegexOptions.CultureInvariant)]
    private static partial Regex FileNamePattern();

    [GeneratedRegex(@"^[A-Za-z]+(\.[A-Za-z]+)+$", RegexOptions.CultureInvariant)]
    private static partial Regex ScopePattern();

    private const string Template = """
        #Requires -Version 5.1
        #Requires -Modules Microsoft.Graph.Authentication, Microsoft.Graph.Users
        <#
        .SYNOPSIS
            jin-ji が生成した Entra ID の同期スクリプト（計画 __PLAN_ID__）。手で編集しないこと。

        .DESCRIPTION
            jin-ji の承認済みの同期計画を Microsoft Graph PowerShell で反映する。
            値（氏名・部署など）はこのファイルに埋め込まず、同じフォルダの __DATA_FILE__ から読む。
            データのファイルの SHA-256 が下の値と一致しなければ、何もせずに止まる（取り違え・改ざんの防止）。

            計画: __PLAN_ID__
            業務日付: __BUSINESS_DATE__ / 承認: __APPROVED_AT__ UTC（承認者 __APPROVER__、起案者 __PROPOSER__）/ 期限: __EXPIRES_AT__ UTC
            データの SHA-256: __DATA_SHA256__
            項目: __COUNT_TOTAL__ 件（退職者の無効化 __COUNT_DISABLE__・一時停止者のサインイン停止 __COUNT_BLOCK__・再有効化 __COUNT_ENABLE__・
                  属性の更新 __COUNT_UPDATE__・上長の設定 __COUNT_MANAGER__・作成 __COUNT_CREATE__）

            必要な最小権限（Microsoft Graph の委任されたアクセス許可。Connect-MgGraph -Scopes でこれだけを要求する）:
                本番の実行: __WRITE_SCOPES_TEXT__
                    - User.ReadWrite.All: 属性（displayName・department など）・上長（manager）の更新、作成。accountEnabled の変更も含む
                    - User.EnableDisableAccount.All + User.Read.All: accountEnabled の変更だけの計画のとき
                ドライラン（-WhatIf）: User.Read.All（読み取りだけ。何も変更しない）
            実行するアカウントには Entra のロール「ユーザー管理者」が要る（管理者ロールを持つユーザーの変更には、より強いロールが要る）。

            冪等: 各ユーザーの更新前に現在値を読み、項目ごとに「計画時の値」か「あるべき値」かを確かめる。
                - すべてがあるべき値なら何もしない（already_applied）
                - どちらでもない項目があれば、計画の後に Entra 側が変わったとみなしてスキップし記録する（skipped_precondition）
                - あるべき値でない項目だけを更新する（succeeded）。エラーは記録して次へ進む（failed）
            実行ログ（JSON・CSV、UTF-8 BOM）は -LogDirectory に書く。JSON のログを jin-ji に取り込んで照合する:
                JinJi.Host sync-result __PLAN_ID__ <ログの JSON>

        .PARAMETER DataPath
            データのファイル（既定はこのスクリプトと同じフォルダの __DATA_FILE__）。

        .PARAMETER LogDirectory
            実行ログを書くフォルダ（既定はこのスクリプトと同じフォルダ）。

        .PARAMETER TenantId
            接続するテナント（省略時は Connect-MgGraph の既定）。

        .PARAMETER SkipConnect
            接続済みの Microsoft Graph のセッションをそのまま使う（スコープは呼び出し側の責任）。

        .EXAMPLE
            .\jinji-entra-sync-__PLAN_ID__.ps1 -WhatIf
            ドライラン（読み取りだけ）。反映する予定の項目と、事前条件の不一致をログに書く。

        .EXAMPLE
            .\jinji-entra-sync-__PLAN_ID__.ps1 -TenantId contoso.onmicrosoft.com
        #>
        [CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'Medium')]
        param(
            [string] $DataPath = (Join-Path -Path $PSScriptRoot -ChildPath '__DATA_FILE__'),
            [string] $LogDirectory = $PSScriptRoot,
            [string] $TenantId,
            [switch] $SkipConnect
        )

        Set-StrictMode -Version 2.0
        $ErrorActionPreference = 'Stop'

        $PlanId = '__PLAN_ID__'
        $ExpectedDataSha256 = '__DATA_SHA256__'
        $WriteScopes = @(__WRITE_SCOPES__)
        $ReadScopes = @('User.Read.All')
        $AllowedFields = @('displayName', 'givenName', 'surname', 'department', 'jobTitle', 'companyName', 'officeLocation', 'employeeId', 'accountEnabled', 'manager', 'userPrincipalName', 'mailNickname')
        $GuidPattern = '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$'
        $IsDryRun = [bool]$WhatIfPreference

        # ---------------------------------------------------------------------
        # 補助の関数（値は変数として扱い、式・コマンドとして評価しない）
        # ---------------------------------------------------------------------

        # Graph の値を計画と同じ文字列にする（真偽値は true / false、空は null）
        function ConvertTo-JinjiText {
            param([AllowNull()] $Value)
            if ($null -eq $Value) { return $null }
            if ($Value -is [bool]) {
                if ($Value) { return 'true' } else { return 'false' }
            }
            $text = [string]$Value
            if ($text.Length -eq 0) { return $null }
            return $text
        }

        # 計画の値と比べる（null と空は同じ。上長のオブジェクト ID は大文字小文字を区別しない）
        function Test-JinjiEqual {
            param([AllowNull()] $Left, [AllowNull()] $Right, [string] $Field)
            $a = [string](ConvertTo-JinjiText $Left)
            $b = [string](ConvertTo-JinjiText $Right)
            if ($Field -eq 'manager') {
                return [string]::Equals($a, $b, [System.StringComparison]::OrdinalIgnoreCase)
            }
            return [string]::Equals($a, $b, [System.StringComparison]::Ordinal)
        }

        # 404（対象が無い）のエラーか
        function Test-JinjiNotFound {
            param($ErrorRecord)
            $text = [string]$ErrorRecord.Exception.Message + ' ' + [string]$ErrorRecord.FullyQualifiedErrorId
            return ($text -match 'Request_ResourceNotFound|ResourceNotFound|NotFound|404')
        }

        # ログに書くメッセージ（改行を詰め、長さを抑える）
        function Limit-JinjiText {
            param([AllowNull()] [string] $Text)
            if ([string]::IsNullOrEmpty($Text)) { return $null }
            $single = ($Text -replace '[\r\n\t]+', ' ').Trim()
            if ($single.Length -gt 900) { return $single.Substring(0, 900) }
            return $single
        }

        # 現在値を読む（計画の項目に出てくる項目だけ）
        function Get-JinjiCurrent {
            param($Item)
            $current = @{}
            $fields = @($Item.changes | ForEach-Object { [string]$_.field })
            $properties = @($fields | Where-Object { $_ -ne 'manager' })
            if ($properties.Count -gt 0) {
                $user = Get-MgUser -UserId $Item.objectId -Property ((@('id') + $properties) -join ',') -ErrorAction Stop
                foreach ($field in $properties) {
                    $current[$field] = ConvertTo-JinjiText ($user.$field)
                }
            }
            if ($fields -contains 'manager') {
                try {
                    $manager = Get-MgUserManager -UserId $Item.objectId -ErrorAction Stop
                    $managerId = ([string]$manager.Id).ToLowerInvariant()
                    $current['manager'] = ConvertTo-JinjiText $managerId
                }
                catch {
                    if (Test-JinjiNotFound $_) { $current['manager'] = $null } else { throw }
                }
            }
            return $current
        }

        # 初期パスワード（作成するアカウントは無効の状態で作り、管理者が設定し直して有効にする。ログには書かない）
        function New-JinjiPassword {
            $bytes = New-Object byte[] 24
            $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
            try { $rng.GetBytes($bytes) } finally { $rng.Dispose() }
            return ([Convert]::ToBase64String($bytes) + 'aA1!')
        }

        # 1 項目を反映する（あるべき値でない項目だけ）
        function Invoke-JinjiChange {
            param($Item, [object[]] $Pending)
            switch ([string]$Item.action) {
                'set_manager' {
                    $managerId = [string]$Pending[0].desired
                    if ($managerId -notmatch $GuidPattern) { throw '上長のオブジェクト ID の形が不正です。' }
                    $reference = @{ '@odata.id' = ('https://graph.microsoft.com/v1.0/users/' + $managerId) }
                    Set-MgUserManagerByRef -UserId $Item.objectId -BodyParameter $reference -ErrorAction Stop
                }
                'create_user' {
                    $body = @{ passwordProfile = @{ forceChangePasswordNextSignIn = $true; password = (New-JinjiPassword) } }
                    foreach ($change in $Pending) {
                        if ($change.field -eq 'accountEnabled') { $body['accountEnabled'] = ([string]$change.desired -eq 'true') }
                        else { $body[[string]$change.field] = [string]$change.desired }
                    }
                    $created = New-MgUser -BodyParameter $body -ErrorAction Stop
                    return [string]$created.Id
                }
                default {
                    $body = @{}
                    foreach ($change in $Pending) {
                        if ($change.field -eq 'accountEnabled') { $body['accountEnabled'] = ([string]$change.desired -eq 'true') }
                        else { $body[[string]$change.field] = $change.desired }
                    }
                    Update-MgUser -UserId $Item.objectId -BodyParameter $body -ErrorAction Stop
                }
            }
            return $null
        }

        # ---------------------------------------------------------------------
        # データの確認
        # ---------------------------------------------------------------------

        $actualSha256 = (Get-FileHash -LiteralPath $DataPath -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actualSha256 -ne $ExpectedDataSha256) {
            throw ('データのファイルの SHA-256 が計画と一致しません（' + $actualSha256 + '）。取り違えか改ざんの可能性があるので中止します。')
        }
        $data = [System.IO.File]::ReadAllText($DataPath, [System.Text.Encoding]::UTF8) | ConvertFrom-Json
        if ([string]$data.format -ne '__DATA_FORMAT__' -or [string]$data.planId -ne $PlanId) {
            throw 'データのファイルの形式か計画 ID が一致しません。中止します。'
        }
        $items = @($data.items)

        if (-not (Test-Path -LiteralPath $LogDirectory -PathType Container)) {
            New-Item -ItemType Directory -Path $LogDirectory | Out-Null
        }
        $mode = 'apply'
        if ($IsDryRun) { $mode = 'whatif' }
        $stamp = (Get-Date).ToUniversalTime().ToString('yyyyMMddTHHmmssZ')
        $logJsonPath = Join-Path -Path $LogDirectory -ChildPath ('jinji-entra-sync-' + $PlanId + '-' + $mode + '-' + $stamp + '.log.json')
        $logCsvPath = Join-Path -Path $LogDirectory -ChildPath ('jinji-entra-sync-' + $PlanId + '-' + $mode + '-' + $stamp + '.log.csv')
        $startedAt = (Get-Date).ToUniversalTime().ToString('o')
        $entries = New-Object System.Collections.Generic.List[object]
        $account = $null

        function Write-JinjiLog {
            $utf8Bom = New-Object System.Text.UTF8Encoding($true)
            $log = [ordered]@{
                format     = '__LOG_FORMAT__'
                planId     = $PlanId
                dataSha256 = $ExpectedDataSha256
                whatIf     = $IsDryRun
                startedAt  = $startedAt
                finishedAt = (Get-Date).ToUniversalTime().ToString('o')
                executedBy = $account
                entries    = $entries.ToArray()
            }
            $json = ConvertTo-Json -InputObject $log -Depth 6
            [System.IO.File]::WriteAllText($logJsonPath, $json, $utf8Bom)
            $rows = @($entries | ForEach-Object {
                [pscustomobject][ordered]@{ sequence = $_.sequence; itemId = $_.itemId; action = $_.action; objectId = $_.objectId; outcome = $_.outcome; message = $_.message; at = $_.at }
            })
            $csv = @($rows | ConvertTo-Csv -NoTypeInformation)
            [System.IO.File]::WriteAllLines($logCsvPath, [string[]]$csv, $utf8Bom)
        }

        # ---------------------------------------------------------------------
        # 実行
        # ---------------------------------------------------------------------

        try {
            if (-not $SkipConnect) {
                $scopes = $WriteScopes
                if ($IsDryRun) { $scopes = $ReadScopes }
                $connect = @{ Scopes = $scopes; NoWelcome = $true }
                if ($TenantId) { $connect['TenantId'] = $TenantId }
                Connect-MgGraph @connect
            }
            $context = Get-MgContext
            if ($null -ne $context) { $account = [string]$context.Account }

            foreach ($item in $items) {
                $entry = [ordered]@{
                    itemId   = [string]$item.itemId
                    sequence = $item.sequence
                    action   = [string]$item.action
                    objectId = $item.objectId
                    outcome  = $null
                    observed = @{}
                    message  = $null
                    at       = $null
                }
                try {
                    foreach ($change in @($item.changes)) {
                        if ($AllowedFields -notcontains [string]$change.field) { throw ('許可されていない項目です: ' + [string]$change.field) }
                    }
                    if ([string]$item.action -eq 'create_user') {
                        $upnChange = @($item.changes | Where-Object { $_.field -eq 'userPrincipalName' })
                        if ($upnChange.Count -ne 1 -or [string]$upnChange[0].desired -notmatch '^[^@\s]+@[^@\s]+$') { throw 'UPN の形が不正です。' }
                        $upn = [string]$upnChange[0].desired
                        $existing = $null
                        try { $existing = Get-MgUser -UserId $upn -Property 'id' -ErrorAction Stop }
                        catch { if (-not (Test-JinjiNotFound $_)) { throw } }
                        if ($null -ne $existing) {
                            $entry.outcome = 'skipped_precondition'
                            $entry.message = '同じ UPN のアカウントが既にあります（取込で対応付けてください）。'
                        }
                        elseif (-not $PSCmdlet.ShouldProcess($upn, 'アカウントを無効の状態で作成')) {
                            $entry.outcome = 'would_apply'
                        }
                        else {
                            $createdId = Invoke-JinjiChange -Item $item -Pending @($item.changes)
                            $entry.outcome = 'succeeded'
                            $entry.message = ('作成したオブジェクト ID: ' + $createdId)
                        }
                    }
                    else {
                        if ([string]$item.objectId -notmatch $GuidPattern) { throw 'オブジェクト ID の形が不正です。' }
                        $current = Get-JinjiCurrent -Item $item
                        $entry.observed = $current
                        $pending = @()
                        $conflicts = @()
                        foreach ($change in @($item.changes)) {
                            $now = $current[[string]$change.field]
                            if (Test-JinjiEqual $now $change.desired ([string]$change.field)) { continue }
                            if ($change.currentKnown -and -not (Test-JinjiEqual $now $change.current ([string]$change.field))) {
                                $conflicts += [string]$change.field
                            }
                            else {
                                $pending += $change
                            }
                        }
                        if ($conflicts.Count -gt 0) {
                            $entry.outcome = 'skipped_precondition'
                            $entry.message = ('計画の後に Entra 側の値が変わったためスキップしました: ' + ($conflicts -join ', '))
                        }
                        elseif ($pending.Count -eq 0) {
                            $entry.outcome = 'already_applied'
                        }
                        elseif (-not $PSCmdlet.ShouldProcess([string]$item.userPrincipalName, ([string]$item.action + ': ' + (($pending | ForEach-Object { [string]$_.field }) -join ', ')))) {
                            $entry.outcome = 'would_apply'
                        }
                        else {
                            Invoke-JinjiChange -Item $item -Pending $pending | Out-Null
                            $entry.outcome = 'succeeded'
                        }
                    }
                }
                catch {
                    $entry.outcome = 'failed'
                    $entry.message = Limit-JinjiText ([string]$_.Exception.Message)
                }
                $entry.at = (Get-Date).ToUniversalTime().ToString('o')
                $entries.Add([pscustomobject]$entry)
                Write-Progress -Activity 'jin-ji Entra ID 同期' -Status ([string]$item.sequence) -PercentComplete ([int](100 * $entries.Count / [Math]::Max(1, $items.Count)))
            }
        }
        finally {
            Write-Progress -Activity 'jin-ji Entra ID 同期' -Completed
            Write-JinjiLog
            Write-Host ('実行ログ: ' + $logJsonPath)
        }

        $summary = @($entries | Group-Object -Property outcome | ForEach-Object { $_.Name + ' ' + $_.Count })
        Write-Host ('結果: ' + ($summary -join ' / '))
        if (@($entries | Where-Object { $_.outcome -eq 'failed' }).Count -gt 0) { exit 1 }
        """;
}
