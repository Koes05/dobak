$ErrorActionPreference = "Stop"
$Utf8Bom = New-Object System.Text.UTF8Encoding($true)
$CurrentStep = "초기화"
$Warnings = New-Object System.Collections.Generic.List[string]

function Find-ProjectRoot {
    $candidates = @($PSScriptRoot, (Split-Path -Parent $PSScriptRoot))
    foreach ($candidate in $candidates) {
        if ($candidate -and (Test-Path (Join-Path $candidate 'Assets')) -and
            (Test-Path (Join-Path $candidate 'ProjectSettings'))) {
            return (Resolve-Path $candidate).Path
        }
    }
    $children = @(Get-ChildItem -LiteralPath $PSScriptRoot -Directory -ErrorAction SilentlyContinue |
        Where-Object { (Test-Path (Join-Path $_.FullName 'Assets')) -and (Test-Path (Join-Path $_.FullName 'ProjectSettings')) })
    if ($children.Count -eq 1) { return $children[0].FullName }
    throw 'Unity 프로젝트 루트를 찾지 못했습니다. ZIP 내용물을 Assets와 ProjectSettings가 보이는 프로젝트 최상위 폴더에 풀어주세요.'
}

function Normalize-Lf([string]$Text) {
    if ($null -eq $Text) { return '' }
    return $Text.Replace("`r`n", "`n").Replace("`r", "`n")
}

function Read-Utf8([string]$Path) {
    return [IO.File]::ReadAllText($Path, [Text.Encoding]::UTF8)
}

function Write-Utf8Bom([string]$Path, [string]$Text) {
    [IO.File]::WriteAllText($Path, (Normalize-Lf $Text), $Utf8Bom)
}

function Assert-Unlocked([string]$Path) {
    if (-not (Test-Path $Path)) { return }
    try {
        $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
        $stream.Close()
    }
    catch {
        throw "파일이 다른 프로그램에서 사용 중입니다: $Path`nExcel, Unity, VS Code의 CSV 편집기를 닫고 다시 실행하세요."
    }
}

function Indent-Replacement([string]$Replacement, [string]$Indent) {
    $lines = (Normalize-Lf $Replacement).TrimEnd("`n").Split("`n")
    $output = New-Object System.Collections.Generic.List[string]
    foreach ($line in $lines) {
        if ($line.Length -eq 0) { $output.Add('') }
        else { $output.Add($Indent + $line) }
    }
    return ($output -join "`n")
}

function Find-CSharpMethodRange([string]$Text, [string]$Signature, [string]$Label) {
    $pattern = '(?m)^(?<indent>[ \t]*)' + [regex]::Escape($Signature) + '[ \t]*$'
    $matches = [regex]::Matches($Text, $pattern)
    if ($matches.Count -ne 1) {
        throw "$Label 메서드 서명을 정확히 찾지 못했습니다. 발견 수: $($matches.Count)"
    }
    $match = $matches[0]
    $open = $Text.IndexOf('{', $match.Index + $match.Length)
    if ($open -lt 0) { throw "$Label 여는 중괄호를 찾지 못했습니다." }

    $depth = 0
    $inString = $false; $verbatim = $false; $inChar = $false
    $lineComment = $false; $blockComment = $false; $escaped = $false
    $close = -1
    for ($i = $open; $i -lt $Text.Length; $i++) {
        $c = $Text[$i]
        $n = if ($i + 1 -lt $Text.Length) { $Text[$i + 1] } else { [char]0 }
        if ($lineComment) { if ($c -eq "`n") { $lineComment = $false }; continue }
        if ($blockComment) { if ($c -eq '*' -and $n -eq '/') { $blockComment = $false; $i++ }; continue }
        if ($inString) {
            if ($verbatim) {
                if ($c -eq '"' -and $n -eq '"') { $i++; continue }
                if ($c -eq '"') { $inString = $false; $verbatim = $false }
                continue
            }
            if ($escaped) { $escaped = $false; continue }
            if ($c -eq '\') { $escaped = $true; continue }
            if ($c -eq '"') { $inString = $false }
            continue
        }
        if ($inChar) {
            if ($escaped) { $escaped = $false; continue }
            if ($c -eq '\') { $escaped = $true; continue }
            if ($c -eq "'") { $inChar = $false }
            continue
        }
        if ($c -eq '/' -and $n -eq '/') { $lineComment = $true; $i++; continue }
        if ($c -eq '/' -and $n -eq '*') { $blockComment = $true; $i++; continue }
        if ($c -eq '"') {
            $inString = $true
            $verbatim = $i -gt 0 -and $Text[$i - 1] -eq '@'
            continue
        }
        if ($c -eq "'") { $inChar = $true; continue }
        if ($c -eq '{') { $depth++ }
        elseif ($c -eq '}') {
            $depth--
            if ($depth -eq 0) { $close = $i; break }
            if ($depth -lt 0) { throw "$Label 중괄호 깊이가 음수가 됐습니다." }
        }
    }
    if ($close -lt 0) { throw "$Label 닫는 중괄호를 찾지 못했습니다." }
    $end = $close + 1
    if ($end -lt $Text.Length -and $Text[$end] -eq "`n") { $end++ }
    return [pscustomobject]@{ Start=$match.Index; End=$end; Indent=[string]$match.Groups['indent'].Value }
}

function Replace-CSharpMethod([string]$Text, [string]$Signature, [string]$Replacement, [string]$Label) {
    $range = Find-CSharpMethodRange $Text $Signature $Label
    $replacementText = Indent-Replacement $Replacement $range.Indent
    return $Text.Substring(0, $range.Start) + $replacementText + "`n" + $Text.Substring($range.End)
}

function New-CsvRow([string[]]$Headers, [hashtable]$Values) {
    $ordered = [ordered]@{}
    foreach ($header in $Headers) {
        $ordered[$header] = if ($Values.ContainsKey($header)) { [string]$Values[$header] } else { '' }
    }
    return [pscustomobject]$ordered
}

function Get-CsvRow([object[]]$Rows, [string]$LineId, [bool]$Required = $false) {
    $matches = @($Rows | Where-Object { [string]$_.line_id -eq $LineId })
    if ($matches.Count -eq 1) { return $matches[0] }
    if ($matches.Count -gt 1) { throw "line_id가 중복되었습니다: $LineId" }
    if ($Required) { throw "필수 line_id를 찾지 못했습니다: $LineId" }
    return $null
}

function Set-IfPresent($Row, [string]$Name, [string]$Value) {
    if ($null -eq $Row) { return }
    $property = $Row.PSObject.Properties[$Name]
    if ($null -eq $property) { throw "CSV 열을 찾지 못했습니다: $Name" }
    $property.Value = $Value
}

function Assert-CSharpBalance([string]$Path) {
    $text = Read-Utf8 $Path
    $brace = 0; $paren = 0; $bracket = 0
    $inString = $false; $inChar = $false; $lineComment = $false; $blockComment = $false; $escaped = $false; $verbatim = $false
    for ($i = 0; $i -lt $text.Length; $i++) {
        $c = $text[$i]
        $n = if ($i + 1 -lt $text.Length) { $text[$i + 1] } else { [char]0 }
        if ($lineComment) { if ($c -eq "`n") { $lineComment = $false }; continue }
        if ($blockComment) { if ($c -eq '*' -and $n -eq '/') { $blockComment = $false; $i++ }; continue }
        if ($inString) {
            if ($verbatim) {
                if ($c -eq '"' -and $n -eq '"') { $i++; continue }
                if ($c -eq '"') { $inString = $false; $verbatim = $false }
                continue
            }
            if ($escaped) { $escaped = $false; continue }
            if ($c -eq '\') { $escaped = $true; continue }
            if ($c -eq '"') { $inString = $false }
            continue
        }
        if ($inChar) {
            if ($escaped) { $escaped = $false; continue }
            if ($c -eq '\') { $escaped = $true; continue }
            if ($c -eq "'") { $inChar = $false }
            continue
        }
        if ($c -eq '/' -and $n -eq '/') { $lineComment = $true; $i++; continue }
        if ($c -eq '/' -and $n -eq '*') { $blockComment = $true; $i++; continue }
        if ($c -eq '"') { $inString = $true; $verbatim = $i -gt 0 -and $text[$i - 1] -eq '@'; continue }
        if ($c -eq "'") { $inChar = $true; continue }
        switch ($c) {
            '{' { $brace++ }
            '}' { $brace-- }
            '(' { $paren++ }
            ')' { $paren-- }
            '[' { $bracket++ }
            ']' { $bracket-- }
        }
        if ($brace -lt 0 -or $paren -lt 0 -or $bracket -lt 0) { throw "괄호가 먼저 닫혔습니다: $Path" }
    }
    if ($brace -ne 0 -or $paren -ne 0 -or $bracket -ne 0 -or $inString -or $inChar -or $blockComment) {
        throw "C# 괄호/문자열 균형이 맞지 않습니다: $Path"
    }
}

$ProjectRoot = Find-ProjectRoot
$StatusPath = Join-Path $ProjectRoot 'DOBak_V20_4_PATCH_STATUS.txt'
$Timestamp = Get-Date -Format 'yyyyMMdd_HHmmss'
$BackupRoot = Join-Path $ProjectRoot ('_Dobak_V20_4_Backup_' + $Timestamp)

$Required = @(
    'Assets\Tablet\Script\ScenarioV3Director.cs',
    'Assets\Tablet\Script\GameFlowManager.cs',
    'Assets\Tablet\Script\DialogueManager.cs',
    'Assets\Junsang\Scripts\Bank\CoinManager.cs',
    'Assets\Junsang\Scripts\Casino\CasinoUIManager.cs',
    'Assets\Junsang\Scripts\SlotMachine\SlotMachineManager.cs',
    'Assets\Resources\ScenarioV3.csv'
)

$Managed = $Required + @(
    'Assets\Resources\ScenarioV3Flow.csv',
    'Assets\Tablet\Script\ScenarioV3FinalRuntimeFix.cs',
    'Assets\Tablet\Script\ScenarioV3FinalRuntimeFix.cs.meta',
    'Assets\Tablet\Script\ScenarioV3HistoryRuntimeFix.cs',
    'Assets\Tablet\Script\ScenarioV3HistoryRuntimeFix.cs.meta',
    'Assets\Tablet\Script\ScenarioV3WonDisplayRuntimeFix.cs',
    'Assets\Tablet\Script\ScenarioV3WonDisplayRuntimeFix.cs.meta',
    'Assets\Junsang\Scripts\Bank\BankHistoryScrollFix.cs',
    'Assets\Junsang\Scripts\Bank\BankHistoryScrollFix.cs.meta',
    'Assets\Editor\DobakV3FlowHotfixInstaller.cs',
    'Assets\Editor\DobakV3FlowHotfixInstaller.cs.meta',
    'Assets\Editor\ScenarioV3FlowHotfixInstaller.cs',
    'Assets\Editor\ScenarioV3FlowHotfixInstaller.cs.meta'
)

$Manifest = New-Object System.Collections.Generic.List[string]
function Restore-All {
    foreach ($item in $Manifest) {
        $parts = $item -split '\|', 2
        $relative = $parts[1]
        $target = Join-Path $ProjectRoot $relative
        if ($parts[0] -eq 'EXIST') {
            $backup = Join-Path $BackupRoot $relative
            New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
            Copy-Item -LiteralPath $backup -Destination $target -Force
        }
        elseif (Test-Path $target) {
            Remove-Item -LiteralPath $target -Force
        }
    }
}

try {
    $CurrentStep = '사전 검사'
    if (@(Get-Process -Name Unity -ErrorAction SilentlyContinue).Count -gt 0) {
        throw 'Unity Editor가 실행 중입니다. 완전히 종료한 뒤 다시 실행하세요.'
    }
    foreach ($relative in $Required) {
        $path = Join-Path $ProjectRoot $relative
        if (-not (Test-Path $path)) { throw "필수 파일이 없습니다: $relative" }
        Assert-Unlocked $path
    }
    $existingFlow = Join-Path $ProjectRoot 'Assets\Resources\ScenarioV3Flow.csv'
    if (Test-Path $existingFlow) { Assert-Unlocked $existingFlow }

    $CurrentStep = '원본 백업'
    New-Item -ItemType Directory -Path $BackupRoot -Force | Out-Null
    foreach ($relative in $Managed) {
        $source = Join-Path $ProjectRoot $relative
        if (Test-Path $source) {
            $Manifest.Add('EXIST|' + $relative)
            $dest = Join-Path $BackupRoot $relative
            New-Item -ItemType Directory -Path (Split-Path -Parent $dest) -Force | Out-Null
            Copy-Item -LiteralPath $source -Destination $dest -Force
        }
        else { $Manifest.Add('MISSING|' + $relative) }
    }
    [IO.File]::WriteAllLines((Join-Path $BackupRoot 'manifest.txt'), [string[]]$Manifest, $Utf8Bom)

    $CurrentStep = '연결표 준비'
    $FlowPath = Join-Path $ProjectRoot 'Assets\Resources\ScenarioV3Flow.csv'
    if (-not (Test-Path $FlowPath)) {
        $fallback = Join-Path $PSScriptRoot 'PatchData\ScenarioV3FlowFallback.csv'
        Copy-Item -LiteralPath $fallback -Destination $FlowPath -Force
        $Warnings.Add('ScenarioV3Flow.csv가 없어 패키지의 호환 연결표를 생성했습니다.')
    }

    $CurrentStep = '문체·표시 문장 적용'
    $TextPatch = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'PatchData\ScenarioV3TextPatchV20_4.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    $ScenarioPath = Join-Path $ProjectRoot 'Assets\Resources\ScenarioV3.csv'
    $Rows = @(Import-Csv -LiteralPath $ScenarioPath)
    if ($Rows.Count -eq 0) { throw 'ScenarioV3.csv가 비어 있습니다.' }
    $duplicates = @($Rows | Where-Object { $_.line_id } | Group-Object line_id | Where-Object { $_.Count -gt 1 })
    if ($duplicates.Count -gt 0) { throw "ScenarioV3 line_id 중복: $($duplicates[0].Name)" }

    foreach ($entry in $TextPatch.entries) {
        $row = Get-CsvRow $Rows ([string]$entry.line_id) $false
        if ($null -eq $row) { continue }
        foreach ($property in $entry.PSObject.Properties) {
            if ($property.Name -eq 'line_id') { continue }
            if (@('text','choice_a_text','choice_b_text','choice_c_text') -notcontains $property.Name) {
                throw "문체 패치가 허용되지 않은 열을 요청했습니다: $($entry.line_id)/$($property.Name)"
            }
            Set-IfPresent $row $property.Name ([string]$property.Value)
        }
    }

    $VisibleColumns = @('text','choice_a_text','choice_b_text','choice_c_text')
    foreach ($row in $Rows) {
        foreach ($column in $VisibleColumns) {
            $value = [string]$row.$column
            $value = $value.Replace('5,000P','5만 원').Replace('5천P','5만 원').Replace('5천 포인트','5만 원').Replace('오천 포인트','5만 원')
            $value = $value.Replace('20,000P','20만 원').Replace('2만P','20만 원').Replace('2만 포인트','20만 원').Replace('이만 포인트','20만 원')
            $value = $value.Replace('신규 가입 포인트','가입 보너스').Replace('무료 포인트','가입 보너스').Replace('공짜 포인트','가입 보너스').Replace('추천 포인트','추천 보너스')
            $row.$column = $value
        }
    }

    $CurrentStep = '시나리오 구조 보정'
    $headers = @($Rows[0].PSObject.Properties.Name)

    $weekdaySceneRows = @($Rows | Where-Object { $_.scene_id -eq 'sys_late_gamble_morning' })
    foreach ($row in $weekdaySceneRows) {
        Set-IfPresent $row 'arc' 'main'
        Set-IfPresent $row 'condition' 'flag.gambled_late=true;flag.borrow_deferred!=true;day!=4;day!=5;day!=11;day!=12'
    }
    $row = Get-CsvRow $Rows 'sys_late_gamble_morning_01' $false
    Set-IfPresent $row 'text' '언제 잠든 거지.... 도박 앱을 켜둔 채 그대로 잠든 모양이다.'
    $row = Get-CsvRow $Rows 'sys_late_gamble_morning_02' $false
    Set-IfPresent $row 'text' '벌써 오전 10시다.... 학교에 늦었다. 그래도 지금이라도 가는 편이 낫겠다.'

    foreach ($row in @($Rows | Where-Object { $_.scene_id -eq 'sys_borrow_late_morning' })) {
        Set-IfPresent $row 'condition' 'day<0'
    }

    $row = Get-CsvRow $Rows 'borrow_defer_night_01' $false
    Set-IfPresent $row 'enter_effects' 'pending.borrow_menu:set=true|flag.borrow_deferred:set=true|tutorial:set=sleep'
    $row = Get-CsvRow $Rows 'borrow_morning_cue_01' $false
    Set-IfPresent $row 'delivery' 'router'; Set-IfPresent $row 'text' ''; Set-IfPresent $row 'enter_effects' 'pending.borrow_menu:set=true'; Set-IfPresent $row 'auto_next' ''
    $row = Get-CsvRow $Rows 'borrow_choice_01' $true
    Set-IfPresent $row 'arc' 'main'; Set-IfPresent $row 'delivery' 'overlay'; Set-IfPresent $row 'choice_a_next' ''; Set-IfPresent $row 'choice_b_next' ''

    $row = Get-CsvRow $Rows 'd10_seojun_followup_01' $true
    Set-IfPresent $row 'condition' 'borrowed.seojun=true;debt_owner=seojun;debt>0'
    Set-IfPresent $row 'choice_a_id' 'd10_repay_now'; Set-IfPresent $row 'choice_a_text' '지금 갚을 수 있는 만큼 보낸다'; Set-IfPresent $row 'choice_a_effects' ''; Set-IfPresent $row 'choice_a_next' 'd10_seojun_repay_router'
    Set-IfPresent $row 'choice_b_id' 'd10_delay_repay'; Set-IfPresent $row 'choice_b_text' '조금만 더 기다려 달라고 한다'; Set-IfPresent $row 'choice_b_effects' ''; Set-IfPresent $row 'choice_b_next' 'd10_seojun_delay_thought'; Set-IfPresent $row 'auto_next' ''

    $row = Get-CsvRow $Rows 'd10_minjae_debt_02' $true
    Set-IfPresent $row 'choice_a_id' 'd10_minjae_repay_now'; Set-IfPresent $row 'choice_a_text' '지금 갚을 수 있는 만큼 보낸다'; Set-IfPresent $row 'choice_a_effects' ''; Set-IfPresent $row 'choice_a_next' 'd10_minjae_repay_router'
    Set-IfPresent $row 'choice_b_id' 'd10_minjae_delay_repay'; Set-IfPresent $row 'choice_b_text' '조금만 더 기다려 달라고 한다'; Set-IfPresent $row 'choice_b_effects' ''; Set-IfPresent $row 'choice_b_next' 'd10_minjae_delay_thought'; Set-IfPresent $row 'auto_next' ''

    $row = Get-CsvRow $Rows 'd14_no_help_messages_03' $false; Set-IfPresent $row 'delivery' 'overlay'
    $row = Get-CsvRow $Rows 'd14_recovery_minjae_02' $false; Set-IfPresent $row 'portrait' 'minjae_angry'; Set-IfPresent $row 'enter_effects' ''

    $Generated = @(
        'sys_late_gamble_morning_weekend_01','sys_late_gamble_morning_weekend_02',
        'd10_seojun_repay_choice_01',
        'd10_minjae_repay_router_01','d10_minjae_repaid_01','d10_minjae_repaid_message_router_01',
        'd10_minjae_repaid_full_message_01','d10_minjae_repaid_partial_message_01',
        'd10_minjae_cannot_repay_01','d10_minjae_delay_thought_01','d10_minjae_delay_message_01',
        'gamble_7_01','gamble_7_02','gamble_8_01','gamble_8_02'
    )
    $Rows = @($Rows | Where-Object { $Generated -notcontains [string]$_.line_id -and [string]$_.line_id -ne 'd10_minjae_debt_03' })

    $Rows += New-CsvRow $headers @{
        schema_version='4'; scene_id='sys_late_gamble_morning_weekend'; line_id='sys_late_gamble_morning_weekend_01'; arc='main'; day='2..14'; time_window='7:00'; trigger='day_start'; condition='flag.gambled_late=true;flag.borrow_deferred!=true;day=4|day=5|day=11|day=12'; priority='203'; once_scope='day'; sequence='1'; speaker='Protagonist'; contact='나'; delivery='narration'; text='언제 잠든 거지.... 도박 앱을 켜둔 채 그대로 잠든 모양이다.'; purpose='주말 밤샘 뒤 아침 배경으로 전환한다.'
    }
    $Rows += New-CsvRow $headers @{
        schema_version='4'; scene_id='sys_late_gamble_morning_weekend'; line_id='sys_late_gamble_morning_weekend_02'; arc='main'; day='2..14'; time_window='7:00'; trigger='day_start'; condition='flag.gambled_late=true;flag.borrow_deferred!=true;day=4|day=5|day=11|day=12'; priority='203'; once_scope='day'; sequence='2'; speaker='Protagonist'; contact='나'; delivery='narration'; text='벌써 오전 10시다. 카페 근무 시간은 이미 지나 있었다.... 결국 오늘 알바를 놓쳤다.'; enter_effects='fatigue:add=1|counter.short_sleep_days:add=1|flag.gambled_late:set=false'; purpose='주말 밤샘 결과를 카페 결근으로 명확히 안내한다.'
    }
    $Rows += New-CsvRow $headers @{
        schema_version='4'; scene_id='d10_minjae_repay_router'; line_id='d10_minjae_repay_router_01'; arc='debt'; day='10'; time_window='morning'; priority='132'; once_scope='game'; sequence='1'; speaker='System'; delivery='router'; enter_effects='route:d10_minjae_repaid if last_repayment>0 else d10_minjae_cannot_repay'; purpose='실제 상환액 유무에 따라 민재 답장을 분기한다.'
    }
    $Rows += New-CsvRow $headers @{
        schema_version='4'; scene_id='d10_minjae_repaid'; line_id='d10_minjae_repaid_01'; arc='debt'; day='10'; time_window='morning'; priority='132'; once_scope='game'; sequence='1'; speaker='Protagonist'; contact='나'; delivery='overlay'; text='민재가 뭐라고 하든 빌린 돈은 갚아야 한다. 지금 보낼 수 있는 것부터 보내자.'; auto_next='d10_minjae_repaid_message_router'; purpose='민재 채팅 화면 위에서 상환 결심을 보여 준다.'
    }
    $Rows += New-CsvRow $headers @{
        schema_version='4'; scene_id='d10_minjae_repaid_message_router'; line_id='d10_minjae_repaid_message_router_01'; arc='debt'; day='10'; time_window='morning'; priority='132'; once_scope='game'; sequence='1'; speaker='System'; delivery='router'; enter_effects='route:d10_minjae_repaid_full_message if debt=0 else d10_minjae_repaid_partial_message'; purpose='민재 빚 완납 여부에 따라 실제 답장을 고른다.'
    }
    $Rows += New-CsvRow $headers @{
        schema_version='4'; scene_id='d10_minjae_repaid_full_message'; line_id='d10_minjae_repaid_full_message_01'; arc='debt'; day='10'; time_window='morning'; priority='132'; once_scope='game'; sequence='1'; speaker='Protagonist'; contact='민재'; delivery='message'; text='방금 빌린 돈 전부 보냈어. 확인해.'; purpose='민재 빚을 전부 갚았을 때 실제 답장만 남긴다.'
    }
    $Rows += New-CsvRow $headers @{
        schema_version='4'; scene_id='d10_minjae_repaid_partial_message'; line_id='d10_minjae_repaid_partial_message_01'; arc='debt'; day='10'; time_window='morning'; priority='132'; once_scope='game'; sequence='1'; speaker='Protagonist'; contact='민재'; delivery='message'; text='지금 가진 돈부터 보냈어. 남은 금액도 날짜 정해서 갚을게.'; purpose='민재 빚을 일부 갚았을 때 실제 답장만 남긴다.'
    }
    $Rows += New-CsvRow $headers @{
        schema_version='4'; scene_id='d10_minjae_cannot_repay'; line_id='d10_minjae_cannot_repay_01'; arc='debt'; day='10'; time_window='morning'; priority='132'; once_scope='game'; sequence='1'; speaker='Protagonist'; contact='나'; delivery='overlay'; text='갚고 싶지만 지금은 보낼 돈이 없다.... 주말 알바비가 들어오면 먼저 갚겠다고 해야겠다.'; auto_next='d10_minjae_delay_message'; purpose='잔액이 없으면 상환 연기 답장으로 합류한다.'
    }
    $Rows += New-CsvRow $headers @{
        schema_version='4'; scene_id='d10_minjae_delay_thought'; line_id='d10_minjae_delay_thought_01'; arc='debt'; day='10'; time_window='morning'; priority='132'; once_scope='game'; sequence='1'; speaker='Protagonist'; contact='나'; delivery='overlay'; text='또 미룬다는 말을 쓰려니 손이 멈췄다.... 그래도 답장은 해야 한다.'; auto_next='d10_minjae_delay_message'; purpose='상환 연기를 고른 심리를 메시지 화면 위에서 보여 준다.'
    }
    $Rows += New-CsvRow $headers @{
        schema_version='4'; scene_id='d10_minjae_delay_message'; line_id='d10_minjae_delay_message_01'; arc='debt'; day='10'; time_window='morning'; priority='132'; once_scope='game'; sequence='1'; speaker='Protagonist'; contact='민재'; delivery='message'; text='지금은 보낼 돈이 없어. 주말 알바비가 들어오면 먼저 갚을게.'; purpose='민재에게 실제로 보낸 상환 연기 답장만 남긴다.'
    }
    $Rows += New-CsvRow $headers @{
        schema_version='4'; scene_id='gamble_7'; line_id='gamble_7_01'; arc='gambling'; day='1..14'; time_window='cinematic'; trigger='gamble_7'; priority='300'; once_scope='day'; sequence='1'; speaker='Narrator'; delivery='cinematic'; text='다시 넣은 돈에서 20,000원이 늘었다. 바닥까지 갔던 잔액이 오르자 방금 전 손실이 잠깐 잊혔다.'; enter_effects='clock:add=120|cash:add=20000|temptation:add=1'; purpose='전액 손실 뒤 작은 적중이 다시 확신을 만드는 과정을 보여 준다.'
    }
    $Rows += New-CsvRow $headers @{
        schema_version='4'; scene_id='gamble_7'; line_id='gamble_7_02'; arc='gambling'; day='1..14'; time_window='cinematic'; trigger='gamble_7'; priority='300'; once_scope='day'; sequence='2'; speaker='Protagonist'; contact='나'; delivery='dialogue'; text='....한 번만 더 맞으면 이번엔 진짜 되찾을 수 있을 것 같은데.'; purpose='작은 수익을 다음 도박의 근거로 오해한다.'
    }
    $Rows += New-CsvRow $headers @{
        schema_version='4'; scene_id='gamble_8'; line_id='gamble_8_01'; arc='gambling'; day='1..14'; time_window='cinematic'; trigger='gamble_8'; priority='300'; once_scope='day'; sequence='1'; speaker='Narrator'; delivery='cinematic'; text='그 확신을 따라 금액을 키웠지만 결과는 40,000원 손실이었다. 남아 있던 돈도 함께 줄었다.'; enter_effects='clock:add=120|cash:add=-40000|temptation:add=2'; purpose='작은 적중 뒤 더 큰 금액을 걸어 손실이 커지는 흐름을 보여 준다.'
    }
    $Rows += New-CsvRow $headers @{
        schema_version='4'; scene_id='gamble_8'; line_id='gamble_8_02'; arc='gambling'; day='1..14'; time_window='cinematic'; trigger='gamble_8'; priority='300'; once_scope='day'; sequence='2'; speaker='Protagonist'; contact='나'; delivery='dialogue'; text='방금 번 것보다 더 크게 잃었다. 그런데도 또 다음 판부터 생각났다.'; purpose='작은 적중 뒤 다음 판을 떠올리는 심리를 보여 준다.'
    }

    [IO.File]::WriteAllLines($ScenarioPath, [string[]]@($Rows | ConvertTo-Csv -NoTypeInformation), $Utf8Bom)

    $CurrentStep = '시나리오 연결표 보정'
    $FlowRows = @(Import-Csv -LiteralPath $FlowPath)
    if ($FlowRows.Count -eq 0) { throw 'ScenarioV3Flow.csv가 비어 있습니다.' }
    $FlowHeaders = @($FlowRows[0].PSObject.Properties.Name)
    $RemoveFlow = @('sys_late_gamble_morning_weekend','d10_seojun_repay_choice','d10_minjae_repay_router','d10_minjae_repaid','d10_minjae_repaid_message_router','d10_minjae_repaid_full_message','d10_minjae_repaid_partial_message','d10_minjae_cannot_repay','d10_minjae_delay_thought','d10_minjae_delay_message','gamble_7','gamble_8')
    $FlowRows = @($FlowRows | Where-Object { $RemoveFlow -notcontains [string]$_.scene_id })
    foreach ($flowRow in $FlowRows) {
        if ($flowRow.scene_id -eq 'd10_seojun_followup' -or $flowRow.scene_id -eq 'd10_minjae_debt') {
            $flowRow.return_to_tablet = 'false'
        }
    }
    $FlowRows += New-CsvRow $FlowHeaders @{ scene_id='sys_late_gamble_morning_weekend'; extra_trigger=''; return_to_tablet='false' }
    foreach ($sceneId in @('d10_minjae_repay_router','d10_minjae_repaid','d10_minjae_repaid_message_router','d10_minjae_cannot_repay','d10_minjae_delay_thought')) {
        $FlowRows += New-CsvRow $FlowHeaders @{ scene_id=$sceneId; extra_trigger=''; return_to_tablet='false' }
    }
    foreach ($sceneId in @('d10_minjae_repaid_full_message','d10_minjae_repaid_partial_message','d10_minjae_delay_message','gamble_7','gamble_8')) {
        $FlowRows += New-CsvRow $FlowHeaders @{ scene_id=$sceneId; extra_trigger=''; return_to_tablet='true' }
    }
    [IO.File]::WriteAllLines($FlowPath, [string[]]@($FlowRows | ConvertTo-Csv -NoTypeInformation), $Utf8Bom)

    $CurrentStep = 'C# 표시 호환 보정'
    $SourcePatch = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'PatchData\SourcePatchesV20_4.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    $Files = @{}
    foreach ($patch in $SourcePatch.methods) {
        $relative = [string]$patch.path
        $full = Join-Path $ProjectRoot $relative
        if (-not (Test-Path $full)) { $Warnings.Add("선택 C# 파일 없음: $relative"); continue }
        if (-not $Files.ContainsKey($relative)) { $Files[$relative] = Normalize-Lf (Read-Utf8 $full) }
        try {
            $Files[$relative] = Replace-CSharpMethod $Files[$relative] ([string]$patch.signature) ([string]$patch.replacement) "$relative / $($patch.signature)"
        }
        catch {
            if ([bool]$patch.required) { throw }
            $Warnings.Add("선택 메서드 보정 건너뜀: $relative / $($patch.signature) / $($_.Exception.Message)")
        }
    }
    foreach ($patch in $SourcePatch.literals) {
        $relative = [string]$patch.path
        $full = Join-Path $ProjectRoot $relative
        if (-not (Test-Path $full)) { $Warnings.Add("선택 C# 파일 없음: $relative"); continue }
        if (-not $Files.ContainsKey($relative)) { $Files[$relative] = Normalize-Lf (Read-Utf8 $full) }
        $old = Normalize-Lf ([string]$patch.old)
        $new = Normalize-Lf ([string]$patch.new)
        if ($Files[$relative].Contains($old)) { $Files[$relative] = $Files[$relative].Replace($old, $new) }
        elseif ([bool]$patch.required -and -not $Files[$relative].Contains($new)) { throw "필수 문자열 수정 지점을 찾지 못했습니다: $relative / $old" }
    }
    foreach ($relative in $Files.Keys) { Write-Utf8Bom (Join-Path $ProjectRoot $relative) $Files[$relative] }

    $CurrentStep = '런타임 보정 파일 설치'
    $PatchAssets = Join-Path $PSScriptRoot 'PatchFiles\Assets'
    Copy-Item -Path (Join-Path $PatchAssets '*') -Destination (Join-Path $ProjectRoot 'Assets') -Recurse -Force

    foreach ($relative in @(
        'Assets\Editor\DobakV3FlowHotfixInstaller.cs','Assets\Editor\DobakV3FlowHotfixInstaller.cs.meta',
        'Assets\Editor\ScenarioV3FlowHotfixInstaller.cs','Assets\Editor\ScenarioV3FlowHotfixInstaller.cs.meta')) {
        $path = Join-Path $ProjectRoot $relative
        if (Test-Path $path) { Remove-Item -LiteralPath $path -Force }
    }

    $CurrentStep = '최종 검증'
    foreach ($relative in @(
        'Assets\Tablet\Script\ScenarioV3Director.cs','Assets\Tablet\Script\GameFlowManager.cs',
        'Assets\Junsang\Scripts\Bank\CoinManager.cs','Assets\Junsang\Scripts\Casino\CasinoUIManager.cs',
        'Assets\Junsang\Scripts\SlotMachine\SlotMachineManager.cs',
        'Assets\Tablet\Script\ScenarioV3FinalRuntimeFix.cs','Assets\Tablet\Script\ScenarioV3HistoryRuntimeFix.cs',
        'Assets\Tablet\Script\ScenarioV3WonDisplayRuntimeFix.cs','Assets\Junsang\Scripts\Bank\BankHistoryScrollFix.cs')) {
        Assert-CSharpBalance (Join-Path $ProjectRoot $relative)
    }

    $CheckRows = @(Import-Csv -LiteralPath $ScenarioPath)
    $dupLines = @($CheckRows | Where-Object { $_.line_id } | Group-Object line_id | Where-Object { $_.Count -gt 1 })
    if ($dupLines.Count -gt 0) { throw "저장 후 line_id 중복: $($dupLines[0].Name)" }
    $badVisible = New-Object System.Collections.Generic.List[string]
    foreach ($checkRow in $CheckRows) {
        foreach ($column in $VisibleColumns) {
            $value = [string]$checkRow.$column
            if ($value -match '포인트|\d[\d,]*\s*P(?:\s|$)') { $badVisible.Add("$($checkRow.line_id)/${column}: $value") }
            if ($checkRow.scene_id -ne 'd1_minjae_invite' -and $value -match '링크') { $badVisible.Add("$($checkRow.line_id)/$column 링크 잔존: $value") }
            if ($value -match '메모해 두자|적어 두자|기록해 두자') { $badVisible.Add("$($checkRow.line_id)/$column 수동 메모 오해: $value") }
        }
    }
    if ($badVisible.Count -gt 0) { throw "표시 문구 검증 실패:`n$($badVisible -join "`n")" }

    foreach ($requiredLine in @('sys_late_gamble_morning_weekend_01','sys_late_gamble_morning_weekend_02','gamble_7_01','gamble_8_01','d10_minjae_repay_router_01')) {
        if ($null -eq (Get-CsvRow $CheckRows $requiredLine $false)) { throw "추가 장면 저장 검증 실패: $requiredLine" }
    }

    $FlowCheck = @(Import-Csv -LiteralPath $FlowPath)
    $dupFlow = @($FlowCheck | Where-Object { $_.scene_id } | Group-Object scene_id | Where-Object { $_.Count -gt 1 })
    if ($dupFlow.Count -gt 0) { throw "ScenarioV3Flow scene_id 중복: $($dupFlow[0].Name)" }

    $RuntimeCheck = Read-Utf8 (Join-Path $ProjectRoot 'Assets\Tablet\Script\ScenarioV3FinalRuntimeFix.cs')
    $WonCheck = Read-Utf8 (Join-Path $ProjectRoot 'Assets\Tablet\Script\ScenarioV3WonDisplayRuntimeFix.cs')
    $HistoryCheck = Read-Utf8 (Join-Path $ProjectRoot 'Assets\Tablet\Script\ScenarioV3HistoryRuntimeFix.cs')
    $BankCheck = Read-Utf8 (Join-Path $ProjectRoot 'Assets\Junsang\Scripts\Bank\BankHistoryScrollFix.cs')
    if ($RuntimeCheck -notmatch 'V20\.3-0903' -or $RuntimeCheck -notmatch 'TrackLateMapCue' -or $RuntimeCheck -notmatch 'V20 Gambling Guard') { throw '통합 흐름 런타임 파일 검증에 실패했습니다.' }
    if ($WonCheck -notmatch 'WonPerPoint = 10' -or $WonCheck -notmatch 'ConvertVisibleText') { throw '원화 표시 런타임 파일 검증에 실패했습니다.' }
    if ($HistoryCheck -notmatch 'RectMask2D' -or $BankCheck -notmatch 'verticalNormalizedPosition = 1f') { throw '스크롤/마스크 런타임 파일 검증에 실패했습니다.' }

    $Status = New-Object System.Collections.Generic.List[string]
    $Status.Add('DOBak V20.4 INTEGRATED PATCH: PASS')
    $Status.Add("적용 시각: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')")
    $Status.Add("프로젝트: $ProjectRoot")
    $Status.Add("백업: $BackupRoot")
    $Status.Add('')
    $Status.Add('적용 완료: 문체/금액/스케줄러/귀가 순서/도박 제한 안내/밤샘 분기/지도 지각 안내/VN 기록/채팅 되감기/차용·상환/도박 7·8회차/수리비 압박/은행 최신순 스크롤')
    if ($Warnings.Count -gt 0) {
        $Status.Add('')
        $Status.Add('[호환 보정 경고 — 런타임 보정이 대신 처리함]')
        foreach ($warning in $Warnings) { $Status.Add('- ' + $warning) }
    }
    $Status.Add('')
    $Status.Add('Unity Play Mode 자동 검증은 수행하지 않았습니다. Intro 새 게임으로 QA하세요.')
    [IO.File]::WriteAllLines($StatusPath, [string[]]$Status, $Utf8Bom)

    Write-Host ''
    Write-Host 'DOBak V20.4 통합 패치 적용 완료' -ForegroundColor Green
    Write-Host "상태 파일: $StatusPath"
    Write-Host "백업 폴더: $BackupRoot"
    if ($Warnings.Count -gt 0) { Write-Host "호환 보정 경고 $($Warnings.Count)건은 상태 파일에 기록했습니다." -ForegroundColor Yellow }
}
catch {
    $message = $_.Exception.Message
    $stack = $_.ScriptStackTrace
    try { Restore-All } catch { $message += "`n복원 중 추가 오류: $($_.Exception.Message)" }
    $Failure = @(
        'DOBak V20.4 INTEGRATED PATCH: FAIL',
        "실패 단계: $CurrentStep",
        "원인: $message",
        "스크립트 위치: $stack",
        "원본 복원 폴더: $BackupRoot"
    )
    [IO.File]::WriteAllLines($StatusPath, [string[]]$Failure, $Utf8Bom)
    Write-Host ''
    Write-Host "패치 실패 단계: $CurrentStep" -ForegroundColor Red
    Write-Host $message -ForegroundColor Red
    Write-Host $stack -ForegroundColor DarkRed
    Write-Host '백업본으로 복원했습니다.' -ForegroundColor Yellow
    exit 1
}
