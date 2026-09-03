$ErrorActionPreference = "Stop"

$Utf8Bom = New-Object System.Text.UTF8Encoding($true)

function Normalize-Lf([string]$Text) {
    if ($null -eq $Text) { return "" }
    return $Text.Replace("`r`n", "`n").Replace("`r", "`n")
}

function Read-Utf8([string]$Path) {
    return [IO.File]::ReadAllText($Path, [Text.Encoding]::UTF8)
}

function Write-Utf8Bom([string]$Path, [string]$Text) {
    [IO.File]::WriteAllText($Path, (Normalize-Lf $Text), $Utf8Bom)
}

function Decode-Text([string]$Value) {
    return [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($Value))
}

function Replace-LiteralOnce(
    [string]$Text,
    [string]$Old,
    [string]$New,
    [string]$Label
) {
    $Text = Normalize-Lf $Text
    $Old = Normalize-Lf $Old
    $New = Normalize-Lf $New
    $first = $Text.IndexOf($Old, [StringComparison]::Ordinal)
    if ($first -lt 0) {
        throw "수정 지점을 찾지 못했습니다: $Label"
    }
    $second = $Text.IndexOf($Old, $first + $Old.Length, [StringComparison]::Ordinal)
    if ($second -ge 0) {
        throw "수정 지점이 둘 이상입니다: $Label"
    }
    return $Text.Substring(0, $first) + $New + $Text.Substring($first + $Old.Length)
}

function Replace-RegexOnce(
    [string]$Text,
    [string]$Pattern,
    [string]$Replacement,
    [string]$Label
) {
    $options = [Text.RegularExpressions.RegexOptions]::Singleline -bor [Text.RegularExpressions.RegexOptions]::Multiline
    $regex = New-Object Text.RegularExpressions.Regex($Pattern, $options)
    $matches = $regex.Matches($Text)
    if ($matches.Count -ne 1) {
        throw "$Label 수정 지점 수가 $($matches.Count)개입니다. 예상값은 1개입니다."
    }
    $match = $matches[0]
    return $Text.Substring(0, $match.Index) + (Normalize-Lf $Replacement) + $Text.Substring($match.Index + $match.Length)
}

function Replace-CSharpMethod(
    [string]$Text,
    [string]$Signature,
    [string]$Replacement,
    [string]$Label
) {
    $pattern = '(?ms)^(?<indent>[ \t]+)' + [regex]::Escape($Signature) + '\r?\n\k<indent>\{.*?^\k<indent>\}\r?\n'
    return Replace-RegexOnce $Text $pattern ((Normalize-Lf $Replacement) + "`n") $Label
}

function Get-ProjectRoot {
    $candidates = New-Object System.Collections.Generic.List[string]
    $candidates.Add($PSScriptRoot)
    $parent = Split-Path -Parent $PSScriptRoot
    if (-not [string]::IsNullOrWhiteSpace($parent)) { $candidates.Add($parent) }

    foreach ($candidate in $candidates) {
        if ((Test-Path (Join-Path $candidate "Assets")) -and
            (Test-Path (Join-Path $candidate "ProjectSettings"))) {
            return (Resolve-Path $candidate).Path
        }
    }

    $children = @(Get-ChildItem -LiteralPath $PSScriptRoot -Directory -ErrorAction SilentlyContinue |
        Where-Object {
            (Test-Path (Join-Path $_.FullName "Assets")) -and
            (Test-Path (Join-Path $_.FullName "ProjectSettings"))
        })
    if ($children.Count -eq 1) {
        return $children[0].FullName
    }

    throw "Unity 프로젝트 루트를 찾지 못했습니다. 패치 파일을 Assets와 ProjectSettings가 있는 프로젝트 최상위 폴더에 옮겨 실행하세요."
}

function New-RowFromHeaders([string[]]$Headers, [hashtable]$Values) {
    $ordered = [ordered]@{}
    foreach ($header in $Headers) {
        $ordered[$header] = if ($Values.ContainsKey($header)) { [string]$Values[$header] } else { "" }
    }
    return [pscustomobject]$ordered
}

function Write-CsvUtf8([string]$Path, [object[]]$Rows) {
    $lines = @($Rows | ConvertTo-Csv -NoTypeInformation)
    [IO.File]::WriteAllLines($Path, $lines, $Utf8Bom)
}

function Get-OneRow([object[]]$Rows, [string]$LineId) {
    $found = @($Rows | Where-Object { $_.line_id -eq $LineId })
    if ($found.Count -ne 1) {
        throw "line_id '$LineId' 행 수가 $($found.Count)개입니다."
    }
    return $found[0]
}

function Assert-Unique([object[]]$Rows, [string]$Property, [string]$Label) {
    $duplicates = @($Rows |
        Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_.$Property) } |
        Group-Object -Property $Property |
        Where-Object { $_.Count -gt 1 })
    if ($duplicates.Count -gt 0) {
        throw "$Label 중복: $($duplicates[0].Name)"
    }
}

function Assert-ScenarioCsv([string]$ScenarioPath, [string]$FlowPath) {
    $rows = @(Import-Csv -LiteralPath $ScenarioPath)
    if ($rows.Count -lt 1) { throw "ScenarioV3.csv가 비어 있습니다." }

    Assert-Unique $rows "line_id" "line_id"

    $choiceIds = New-Object System.Collections.Generic.List[string]
    foreach ($row in $rows) {
        foreach ($name in @("choice_a_id", "choice_b_id", "choice_c_id")) {
            $id = [string]$row.$name
            if (-not [string]::IsNullOrWhiteSpace($id)) { $choiceIds.Add($id) }
        }
    }
    $dupChoice = @($choiceIds | Group-Object | Where-Object { $_.Count -gt 1 })
    if ($dupChoice.Count -gt 0) { throw "choice_id 중복: $($dupChoice[0].Name)" }

    $sceneSet = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($row in $rows) {
        if (-not [string]::IsNullOrWhiteSpace([string]$row.scene_id)) {
            [void]$sceneSet.Add([string]$row.scene_id)
        }
    }

    foreach ($row in $rows) {
        foreach ($name in @("auto_next", "choice_a_next", "choice_b_next", "choice_c_next")) {
            $target = [string]$row.$name
            if (-not [string]::IsNullOrWhiteSpace($target) -and -not $sceneSet.Contains($target)) {
                throw "$($row.line_id)의 $name 대상 장면이 없습니다: $target"
            }
        }
    }

    $flowRows = @(Import-Csv -LiteralPath $FlowPath)
    $flowDuplicates = @($flowRows | Group-Object -Property scene_id | Where-Object { $_.Count -gt 1 })
    if ($flowDuplicates.Count -gt 0) {
        throw "ScenarioV3Flow.csv scene_id 중복: $($flowDuplicates[0].Name)"
    }
    foreach ($flowRow in $flowRows) {
        if (-not $sceneSet.Contains([string]$flowRow.scene_id)) {
            throw "ScenarioV3Flow.csv가 없는 장면을 참조합니다: $($flowRow.scene_id)"
        }
    }

    foreach ($required in @(
        "d10_seojun_repay_choice_01",
        "d10_minjae_repay_choice_01",
        "d10_minjae_repaid_message_01",
        "gamble_7_01",
        "gamble_8_01"
    )) {
        if (@($rows | Where-Object { $_.line_id -eq $required }).Count -ne 1) {
            throw "필수 대사가 없거나 중복되었습니다: $required"
        }
    }
}

function Assert-Braces([string]$Path) {
    $text = Read-Utf8 $Path
    $brace = 0
    $paren = 0
    $bracket = 0
    $inString = $false
    $inChar = $false
    $lineComment = $false
    $blockComment = $false
    $escaped = $false

    for ($i = 0; $i -lt $text.Length; $i++) {
        $c = $text[$i]
        $n = if ($i + 1 -lt $text.Length) { $text[$i + 1] } else { [char]0 }

        if ($lineComment) {
            if ($c -eq "`n") { $lineComment = $false }
            continue
        }
        if ($blockComment) {
            if ($c -eq '*' -and $n -eq '/') {
                $blockComment = $false
                $i++
            }
            continue
        }
        if ($inString) {
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
        if ($c -eq '"') { $inString = $true; continue }
        if ($c -eq "'") { $inChar = $true; continue }

        switch ($c) {
            '{' { $brace++ }
            '}' { $brace-- }
            '(' { $paren++ }
            ')' { $paren-- }
            '[' { $bracket++ }
            ']' { $bracket-- }
        }
        if ($brace -lt 0 -or $paren -lt 0 -or $bracket -lt 0) {
            throw "괄호가 먼저 닫혔습니다: $Path"
        }
    }

    if ($inString -or $inChar -or $blockComment) {
        throw "문자열/주석이 닫히지 않았습니다: $Path"
    }
    if ($brace -ne 0 -or $paren -ne 0 -or $bracket -ne 0) {
        throw "괄호 균형이 맞지 않습니다: $Path"
    }
}

$ProjectRoot = Get-ProjectRoot
$UnityProcesses = @(Get-Process -Name "Unity" -ErrorAction SilentlyContinue)
if ($UnityProcesses.Count -gt 0) {
    throw "Unity Editor가 실행 중입니다. Play Mode를 끝내고 Unity를 완전히 종료한 뒤 다시 실행하세요."
}
$Timestamp = Get-Date -Format "yyyyMMdd_HHmmss"
$BackupRoot = Join-Path $ProjectRoot ("_Dobak_Final_Backup_" + $Timestamp)
$LogPath = Join-Path $ProjectRoot "DOBak_FINAL_PATCH_STATUS.txt"

$RelativeTargets = @(
    "Assets\Tablet\Script\ScenarioV3Director.cs",
    "Assets\Tablet\Script\GameFlowManager.cs",
    "Assets\Tablet\Script\DialogueManager.cs",
    "Assets\Junsang\Scripts\Bank\BankUI.cs",
    "Assets\Resources\ScenarioV3.csv",
    "Assets\Resources\ScenarioV3Flow.csv"
)

New-Item -ItemType Directory -Path $BackupRoot -Force | Out-Null
foreach ($relative in $RelativeTargets) {
    $source = Join-Path $ProjectRoot $relative
    if (-not (Test-Path $source)) {
        throw "필수 파일을 찾지 못했습니다: $relative"
    }
    $destination = Join-Path $BackupRoot $relative
    New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
    Copy-Item -LiteralPath $source -Destination $destination -Force
}

try {

    # ================================================================
    # ScenarioV3Director.cs
    # ================================================================
    $DirectorPath = Join-Path $ProjectRoot "Assets\Tablet\Script\ScenarioV3Director.cs"
    $Director = Normalize-Lf (Read-Utf8 $DirectorPath)

    if (-not $Director.Contains("DOBak V15 FINAL")) {
        $Director = Replace-LiteralOnce $Director (Decode-Text 'cHVibGljIHNlYWxlZCBjbGFzcyBTY2VuYXJpb1YzQ2hlY2twb2ludERhdGEKewogICAgcHVibGljIHN0cmluZyBsYWJlbDsKICAgIHB1YmxpYyBzdHJpbmcgc2NlbmVJZDsKICAgIHB1YmxpYyBzdHJpbmcgbGluZUlkOwogICAgcHVibGljIGludCBsaW5lSW5kZXg7CiAgICBwdWJsaWMgaW50IGRheTsKICAgIHB1YmxpYyBpbnQgaG91cjsKICAgIHB1YmxpYyBzdHJpbmcgbG9jYXRpb247CiAgICBwdWJsaWMgaW50IGNhc2g7CiAgICBwdWJsaWMgaW50IGRlYnQ7CiAgICBwdWJsaWMgaW50IGNob2ljZUNvdW50OwogICAgcHVibGljIExpc3Q8U2NlbmFyaW9WM1N0YXRlRW50cnk+IHN0YXRlID0gbmV3IExpc3Q8U2NlbmFyaW9WM1N0YXRlRW50cnk+KCk7CiAgICBwdWJsaWMgTGlzdDxzdHJpbmc+IHNlZW5TY2VuZXMgPSBuZXcgTGlzdDxzdHJpbmc+KCk7Cn0=') (Decode-Text 'cHVibGljIHNlYWxlZCBjbGFzcyBTY2VuYXJpb1YzQ2hlY2twb2ludERhdGEKewogICAgcHVibGljIHN0cmluZyBsYWJlbDsKICAgIHB1YmxpYyBzdHJpbmcgc2NlbmVJZDsKICAgIHB1YmxpYyBzdHJpbmcgbGluZUlkOwogICAgcHVibGljIGludCBsaW5lSW5kZXg7CiAgICBwdWJsaWMgaW50IGRheTsKICAgIHB1YmxpYyBpbnQgaG91cjsKICAgIHB1YmxpYyBzdHJpbmcgbG9jYXRpb247CiAgICBwdWJsaWMgaW50IGNhc2g7CiAgICBwdWJsaWMgaW50IGRlYnQ7CiAgICBwdWJsaWMgaW50IGNob2ljZUNvdW50OwogICAgcHVibGljIExpc3Q8U2NlbmFyaW9WM1N0YXRlRW50cnk+IHN0YXRlID0gbmV3IExpc3Q8U2NlbmFyaW9WM1N0YXRlRW50cnk+KCk7CiAgICBwdWJsaWMgTGlzdDxzdHJpbmc+IHNlZW5TY2VuZXMgPSBuZXcgTGlzdDxzdHJpbmc+KCk7CiAgICBwdWJsaWMgTGlzdDxzdHJpbmc+IGRpYWxvZ3VlTG9nID0gbmV3IExpc3Q8c3RyaW5nPigpOwogICAgcHVibGljIHN0cmluZyBjaGF0U25hcHNob3QgPSBzdHJpbmcuRW1wdHk7Cn0=') "체크포인트 대화 스냅샷 필드"

        $Director = Replace-LiteralOnce $Director (Decode-Text 'cHVibGljIHNlYWxlZCBjbGFzcyBTY2VuYXJpb1YzRGlyZWN0b3IgOiBNb25vQmVoYXZpb3VyCnsKICAgIHByaXZhdGUgY29uc3QgaW50IEZpbmFsRGF5ID0gMTQ7') (Decode-Text 'cHVibGljIHNlYWxlZCBjbGFzcyBTY2VuYXJpb1YzRGlyZWN0b3IgOiBNb25vQmVoYXZpb3VyCnsKICAgIHByaXZhdGUgY29uc3QgaW50IEZpbmFsRGF5ID0gMTQ7CiAgICBwcml2YXRlIGNvbnN0IHN0cmluZyBGaW5hbEhvdGZpeE1hcmtlciA9ICJET0JhayBWMTUgRklOQUwiOw==') "Director 최종 패치 마커"

        $Director = Replace-LiteralOnce $Director (Decode-Text 'ICAgIHByaXZhdGUgQ29yb3V0aW5lIGhvbWVUaW1lVHJhbnNpdGlvbkNvcm91dGluZTsKICAgIHByaXZhdGUgUmF3SW1hZ2UgaG9tZVRyYW5zaXRpb25PdmVybGF5OwoKICAgIHByaXZhdGUgR2FtZU9iamVjdCBub3ZlbFBhbmVsOw==') (Decode-Text 'ICAgIHByaXZhdGUgQ29yb3V0aW5lIGhvbWVUaW1lVHJhbnNpdGlvbkNvcm91dGluZTsKICAgIHByaXZhdGUgUmF3SW1hZ2UgaG9tZVRyYW5zaXRpb25PdmVybGF5OwogICAgcHJpdmF0ZSBib29sIG5vdmVsQmFja2Ryb3BDb2xvckNhcHR1cmVkOwogICAgcHJpdmF0ZSBDb2xvciBub3ZlbEJhY2tkcm9wQ29sb3I7CgogICAgcHJpdmF0ZSBHYW1lT2JqZWN0IG5vdmVsUGFuZWw7') "투명 선택 오버레이 상태"

        $Director = Replace-LiteralOnce $Director (Decode-Text 'ICAgIHB1YmxpYyBib29sIEhhc1BlbmRpbmdNZXNzYWdlQWN0aW9uID0+IHBlbmRpbmdPdXRnb2luZ0xpbmUgIT0gbnVsbCB8fCB3YWl0aW5nRm9yTWVzc2FnZUNob2ljZSB8fCBHZXRJbnQoInVucmVhZF9jb3VudCIpID4gMDs=') (Decode-Text 'ICAgIHB1YmxpYyBib29sIEhhc1BlbmRpbmdNZXNzYWdlQWN0aW9uID0+CiAgICAgICAgcGVuZGluZ091dGdvaW5nTGluZSAhPSBudWxsIHx8CiAgICAgICAgd2FpdGluZ0Zvck1lc3NhZ2VDaG9pY2UgfHwKICAgICAgICB3YWl0aW5nRm9ySW5jb21pbmdNZXNzYWdlUmVhZCB8fAogICAgICAgIEdldEludCgidW5yZWFkX2NvdW50IikgPiAwIHx8CiAgICAgICAgIXN0cmluZy5FcXVhbHMoR2V0U3RhdGUoInBlbmRpbmcuYm9ycm93X3RhcmdldCIpLCAibm9uZSIsIFN0cmluZ0NvbXBhcmlzb24uT3JkaW5hbElnbm9yZUNhc2UpOw==') "메시지 빨간 점 조건"

        $Director = Replace-CSharpMethod $Director "public void TryStartGambleFromHome()" (Decode-Text 'ICAgIHB1YmxpYyB2b2lkIFRyeVN0YXJ0R2FtYmxlRnJvbUhvbWUoKQogICAgewogICAgICAgIGlmICghSXNSZWFkeSB8fCBmbG93LklzR2FtZUVuZGVkIHx8ICFJc0dhbWJsaW5nQXBwVW5sb2NrZWQgfHwgYWN0aXZlU2NlbmUgIT0gbnVsbCkKICAgICAgICAgICAgcmV0dXJuOwoKICAgICAgICBpZiAocGVuZGluZ091dGdvaW5nTGluZSAhPSBudWxsKQogICAgICAgIHsKICAgICAgICAgICAgc3RyaW5nIGNvbnRhY3QgPSBzdHJpbmcuSXNOdWxsT3JXaGl0ZVNwYWNlKHBlbmRpbmdPdXRnb2luZ0NvbnRhY3QpCiAgICAgICAgICAgICAgICA/ICLrs7TrgrTquLDroZwg7ZWcIOyDgeuMgCIKICAgICAgICAgICAgICAgIDogcGVuZGluZ091dGdvaW5nQ29udGFjdDsKICAgICAgICAgICAgc3RyaW5nIHByb21wdCA9IHBlbmRpbmdPdXRnb2luZ1NwZWFrZXIgPT0gU3BlYWtlclR5cGUuU2VveWVvbgogICAgICAgICAgICAgICAgPyAiKOyEnOyXsOyXkOqyjCDrs7TrgrTquLDroZwg7ZWcIOuplOyLnOyngOu2gO2EsCDsoJXrpqztlZjripQg7Y647J20IOyii+qyoOuLpC4pIgogICAgICAgICAgICAgICAgOiAkIih7Y29udGFjdH3sl5Dqsowg67O064K06riw66GcIO2VnCDrqZTsi5zsp4DrtoDthLAg7KCV66as7ZWY64qUIO2OuOydtCDsoovqsqDri6QuKSI7CiAgICAgICAgICAgIGZsb3cuVjNTaG93RGlhbG9ndWUoIuuCmCIsIHByb21wdCwgKCkgPT4gZmxvdy5WM01hcmtBcHBBdHRlbnRpb24oQXBwVHlwZS5NZXNzYWdlKSk7CiAgICAgICAgICAgIHJldHVybjsKICAgICAgICB9CgogICAgICAgIGlmICh3YWl0aW5nRm9yTWVzc2FnZUNob2ljZSB8fCB3YWl0aW5nRm9ySW5jb21pbmdNZXNzYWdlUmVhZCkKICAgICAgICB7CiAgICAgICAgICAgIGZsb3cuVjNTaG93RGlhbG9ndWUoIuuCmCIsCiAgICAgICAgICAgICAgICAiKO2ZleyduO2VmOqxsOuCmCDri7XtlbTslbwg7ZWgIOuplOyLnOyngOu2gO2EsCDsoJXrpqztlZjripQg7Y647J20IOyii+qyoOuLpC4pIiwKICAgICAgICAgICAgICAgICgpID0+IGZsb3cuVjNNYXJrQXBwQXR0ZW50aW9uKEFwcFR5cGUuTWVzc2FnZSkpOwogICAgICAgICAgICByZXR1cm47CiAgICAgICAgfQoKICAgICAgICBpZiAoIWZsb3cuSXNEYWlseVNjaGVkdWxlQ29tcGxldGUpCiAgICAgICAgewogICAgICAgICAgICBmbG93LlYzU2hvd0RpYWxvZ3VlKCLrgpgiLAogICAgICAgICAgICAgICAgIijslYTsp4Eg7Jik64qYIOydvOygleydtCDrgqjslYQg7J6I64ukLiDrqLzsoIAg66eI66y066as7ZWY64qUIO2OuOydtCDsoovqsqDri6QuKSIsCiAgICAgICAgICAgICAgICBudWxsKTsKICAgICAgICAgICAgcmV0dXJuOwogICAgICAgIH0KCiAgICAgICAgU2V0U3RhdGUoInBlbmRpbmcuZ2FtYmxlX2F0dGVudGlvbiIsICJmYWxzZSIpOwogICAgICAgIGZsb3cuVjNTZXRHYW1ibGluZ0F0dGVudGlvbihmYWxzZSk7CiAgICAgICAgQXBwbHlFZmZlY3QoImdhbWJsZTphZHZhbmNlIik7CiAgICAgICAgc3RyaW5nIHRhcmdldCA9IGltbWVkaWF0ZVJvdXRlOwogICAgICAgIGltbWVkaWF0ZVJvdXRlID0gc3RyaW5nLkVtcHR5OwogICAgICAgIGlmICghc3RyaW5nLklzTnVsbE9yV2hpdGVTcGFjZSh0YXJnZXQpKQogICAgICAgICAgICBQbGF5U2NlbmUodGFyZ2V0KTsKICAgICAgICBTYXZlKCk7CiAgICB9') "도박 실행 전 메시지 유도"

        $Director = Replace-CSharpMethod $Director "private bool WillContinueInsideMessage(string nextScene)" (Decode-Text 'ICAgIHByaXZhdGUgYm9vbCBXaWxsQ29udGludWVJbnNpZGVNZXNzYWdlKHN0cmluZyBuZXh0U2NlbmUpCiAgICB7CiAgICAgICAgc3RyaW5nIHRhcmdldCA9ICFzdHJpbmcuSXNOdWxsT3JXaGl0ZVNwYWNlKGltbWVkaWF0ZVJvdXRlKSA/IGltbWVkaWF0ZVJvdXRlIDogbmV4dFNjZW5lOwogICAgICAgIGlmICghc3RyaW5nLklzTnVsbE9yV2hpdGVTcGFjZSh0YXJnZXQpKQogICAgICAgIHsKICAgICAgICAgICAgU2NlbmFyaW9WM1NjZW5lIHNjZW5lID0gZGF0YWJhc2UuR2V0U2NlbmUodGFyZ2V0KTsKICAgICAgICAgICAgaWYgKHNjZW5lICE9IG51bGwgJiYgc2NlbmUubGluZXMuQW55KGNhbmRpZGF0ZSA9PgogICAgICAgICAgICAgICAgICAgIHN0cmluZy5FcXVhbHMoY2FuZGlkYXRlLmRlbGl2ZXJ5LCAibWVzc2FnZSIsIFN0cmluZ0NvbXBhcmlzb24uT3JkaW5hbElnbm9yZUNhc2UpIHx8CiAgICAgICAgICAgICAgICAgICAgc3RyaW5nLkVxdWFscyhjYW5kaWRhdGUuZGVsaXZlcnksICJvdmVybGF5IiwgU3RyaW5nQ29tcGFyaXNvbi5PcmRpbmFsSWdub3JlQ2FzZSkgfHwKICAgICAgICAgICAgICAgICAgICBzdHJpbmcuRXF1YWxzKGNhbmRpZGF0ZS5kZWxpdmVyeSwgInJvdXRlciIsIFN0cmluZ0NvbXBhcmlzb24uT3JkaW5hbElnbm9yZUNhc2UpKSkKICAgICAgICAgICAgICAgIHJldHVybiB0cnVlOwogICAgICAgIH0KCiAgICAgICAgaWYgKCFzdHJpbmcuSXNOdWxsT3JXaGl0ZVNwYWNlKHJlYWN0aXZlVHJpZ2dlcikpCiAgICAgICAgewogICAgICAgICAgICByZXR1cm4gZGF0YWJhc2UuR2V0QnlUcmlnZ2VyKHJlYWN0aXZlVHJpZ2dlcikuQW55KHNjZW5lID0+IHNjZW5lLmxpbmVzLkFueShjYW5kaWRhdGUgPT4KICAgICAgICAgICAgICAgIHN0cmluZy5FcXVhbHMoY2FuZGlkYXRlLmRlbGl2ZXJ5LCAibWVzc2FnZSIsIFN0cmluZ0NvbXBhcmlzb24uT3JkaW5hbElnbm9yZUNhc2UpIHx8CiAgICAgICAgICAgICAgICBzdHJpbmcuRXF1YWxzKGNhbmRpZGF0ZS5kZWxpdmVyeSwgIm92ZXJsYXkiLCBTdHJpbmdDb21wYXJpc29uLk9yZGluYWxJZ25vcmVDYXNlKSB8fAogICAgICAgICAgICAgICAgc3RyaW5nLkVxdWFscyhjYW5kaWRhdGUuZGVsaXZlcnksICJyb3V0ZXIiLCBTdHJpbmdDb21wYXJpc29uLk9yZGluYWxJZ25vcmVDYXNlKSkpOwogICAgICAgIH0KICAgICAgICByZXR1cm4gZmFsc2U7CiAgICB9') "메시지 화면 유지 판정"

        $Director = Replace-CSharpMethod $Director "public void RestorePreviousCheckpoint()" (Decode-Text 'ICAgIHB1YmxpYyB2b2lkIFJlc3RvcmVQcmV2aW91c0NoZWNrcG9pbnQoKQogICAgewogICAgICAgIFNjZW5hcmlvVjNDaGVja3BvaW50RGF0YSBjaGVja3BvaW50ID0gRmluZFJld2luZENoZWNrcG9pbnQoKTsKICAgICAgICBpZiAoY2hlY2twb2ludCA9PSBudWxsKQogICAgICAgICAgICByZXR1cm47CgogICAgICAgIHNjZW5lUXVldWUuQ2xlYXIoKTsKICAgICAgICBxdWV1ZUNvbXBsZXRlZCA9IG51bGw7CiAgICAgICAgd2FpdGluZ0ZvckluY29taW5nTWVzc2FnZVJlYWQgPSBmYWxzZTsKICAgICAgICB3YWl0aW5nSW5jb21pbmdTcGVha2VyID0gU3BlYWtlclR5cGUuVW5rbm93bjsKICAgICAgICB3YWl0aW5nSW5jb21pbmdMaW5lID0gbnVsbDsKICAgICAgICBpZiAoaW5jb21pbmdNZXNzYWdlQ29yb3V0aW5lICE9IG51bGwpCiAgICAgICAgICAgIFN0b3BDb3JvdXRpbmUoaW5jb21pbmdNZXNzYWdlQ29yb3V0aW5lKTsKICAgICAgICBpbmNvbWluZ01lc3NhZ2VDb3JvdXRpbmUgPSBudWxsOwogICAgICAgIHdhaXRpbmdGb3JNZXNzYWdlU2NlbmVDbG9zZSA9IGZhbHNlOwogICAgICAgIHBlbmRpbmdBZnRlck1lc3NhZ2VDbG9zZSA9IG51bGw7CiAgICAgICAgd2FpdGluZ0Zvck1lc3NhZ2VDaG9pY2UgPSBmYWxzZTsKICAgICAgICB3YWl0aW5nTWVzc2FnZVNwZWFrZXIgPSBTcGVha2VyVHlwZS5Vbmtub3duOwogICAgICAgIHdhaXRpbmdNZXNzYWdlU2NlbmUgPSBudWxsOwogICAgICAgIHdhaXRpbmdNZXNzYWdlTGluZUluZGV4ID0gLTE7CiAgICAgICAgQ2xlYXJQZW5kaW5nT3V0Z29pbmdNZXNzYWdlKCk7CiAgICAgICAgZGVsaXZlcmVkT3V0Z29pbmdMaW5lSWRzLkNsZWFyKCk7CiAgICAgICAgZGVsaXZlcmVkSW5jb21pbmdMaW5lSWRzLkNsZWFyKCk7CiAgICAgICAgcGVuZGluZ0RheUFkdmFuY2UgPSBmYWxzZTsKICAgICAgICBwZW5kaW5nTGF0ZVdha2VBZnRlckdhbWJsaW5nID0gZmFsc2U7CiAgICAgICAgcGVuZGluZ0JvcnJvd01vcm5pbmdBZHZhbmNlID0gZmFsc2U7CiAgICAgICAgcmVhY3RpdmVUcmlnZ2VyID0gc3RyaW5nLkVtcHR5OwogICAgICAgIGltbWVkaWF0ZVJvdXRlID0gc3RyaW5nLkVtcHR5OwoKICAgICAgICBzdGF0ZS5DbGVhcigpOwogICAgICAgIGZvcmVhY2ggKFNjZW5hcmlvVjNTdGF0ZUVudHJ5IGVudHJ5IGluIGNoZWNrcG9pbnQuc3RhdGUpCiAgICAgICAgICAgIHN0YXRlW2VudHJ5LmtleV0gPSBlbnRyeS52YWx1ZTsKCiAgICAgICAgc2VlblNjZW5lcy5DbGVhcigpOwogICAgICAgIGZvcmVhY2ggKHN0cmluZyBzZWVuIGluIGNoZWNrcG9pbnQuc2VlblNjZW5lcykKICAgICAgICAgICAgc2VlblNjZW5lcy5BZGQoc2Vlbik7CgogICAgICAgIGlmIChjaG9pY2VIaXN0b3J5LkNvdW50ID4gY2hlY2twb2ludC5jaG9pY2VDb3VudCkKICAgICAgICAgICAgY2hvaWNlSGlzdG9yeS5SZW1vdmVSYW5nZShjaGVja3BvaW50LmNob2ljZUNvdW50LCBjaG9pY2VIaXN0b3J5LkNvdW50IC0gY2hlY2twb2ludC5jaG9pY2VDb3VudCk7CiAgICAgICAgY2hlY2twb2ludHMuUmVtb3ZlQWxsKGNhbmRpZGF0ZSA9PgogICAgICAgICAgICBjYW5kaWRhdGUuZGF5ID4gY2hlY2twb2ludC5kYXkgfHwKICAgICAgICAgICAgKGNhbmRpZGF0ZS5kYXkgPT0gY2hlY2twb2ludC5kYXkgJiYgY2FuZGlkYXRlLmNob2ljZUNvdW50ID4gY2hlY2twb2ludC5jaG9pY2VDb3VudCkpOwoKICAgICAgICAvLyDsspjsnYzrtoDthLAg64uk7IucIOyLnOyeke2VoCDrlYzrp4wg7KCE7LK0IOq4sOuhneydhCDsp4DsmrTri6QuCiAgICAgICAgLy8g67aE6riw7KCQIOuzteybkOydgCDssrTtgaztj6zsnbjtirjquYzsp4DsnZggVk4v7LGE7YyFIOq4sOuhneydhCDqt7jrjIDroZwg65CY7IK066aw64ukLgogICAgICAgIGRpYWxvZ3VlTG9nLkNsZWFyKCk7CiAgICAgICAgaWYgKGNoZWNrcG9pbnQuZGlhbG9ndWVMb2cgIT0gbnVsbCkKICAgICAgICAgICAgZGlhbG9ndWVMb2cuQWRkUmFuZ2UoY2hlY2twb2ludC5kaWFsb2d1ZUxvZyk7CiAgICAgICAgaWYgKGRpYWxvZ3VlICE9IG51bGwgJiYgIXN0cmluZy5Jc051bGxPcldoaXRlU3BhY2UoY2hlY2twb2ludC5jaGF0U25hcHNob3QpKQogICAgICAgICAgICBkaWFsb2d1ZS5SZXN0b3JlU2NlbmFyaW9TbmFwc2hvdChjaGVja3BvaW50LmNoYXRTbmFwc2hvdCk7CgogICAgICAgIG5vdGlmaWNhdGlvbnM/LkNsZWFyKCk7CiAgICAgICAgYXBwV2luZG93Py5DbG9zZUN1cnJlbnRBcHAoKTsKICAgICAgICBmbG93LlYzUmVzdG9yZVJ1bihjaGVja3BvaW50LmRheSwgY2hlY2twb2ludC5ob3VyLCBjaGVja3BvaW50LmxvY2F0aW9uLAogICAgICAgICAgICBjaGVja3BvaW50LmNhc2gsIGNoZWNrcG9pbnQuZGVidCk7CiAgICAgICAgZmxvdy5WM1NldFNjaGVkdWxlKCJzY2hvb2wiLCBHZXRTdGF0ZSgic2NoZWR1bGUuc2Nob29sIikpOwogICAgICAgIGZsb3cuVjNTZXRTY2hlZHVsZSgiaG9tZXdvcmsiLCBHZXRTdGF0ZSgic2NoZWR1bGUuaG9tZXdvcmsiKSk7CiAgICAgICAgZmxvdy5WM1NldFNjaGVkdWxlKCJqb2IiLCBHZXRTdGF0ZSgic2NoZWR1bGUuam9iIikpOwogICAgICAgIGZsb3cuVjNTZXRTY2hlZHVsZSgic2xlZXAiLCBHZXRTdGF0ZSgic2NoZWR1bGUuc2xlZXAiKSk7CiAgICAgICAgZmxvdy5WM1NldEdhbWJsaW5nVW5sb2NrZWQoSXNHYW1ibGluZ0FwcFVubG9ja2VkKTsKICAgICAgICBmbG93LlYzU2V0R2FtYmxpbmdBdHRlbnRpb24oSGFzUGVuZGluZ0dhbWJsZU9mZmVyKTsKCiAgICAgICAgYWN0aXZlU2NlbmUgPSBkYXRhYmFzZS5HZXRTY2VuZShjaGVja3BvaW50LnNjZW5lSWQpOwogICAgICAgIGFjdGl2ZUxpbmVJbmRleCA9IGNoZWNrcG9pbnQubGluZUluZGV4OwogICAgICAgIEhpZGVOb3ZlbCgpOwogICAgICAgIFNhdmUoKTsKICAgICAgICBQcmVzZW50TGluZSgpOwogICAgfQ==') "분기점 대화 기록 복원"

        $Director = Replace-CSharpMethod $Director "private bool WasSeen(ScenarioV3Scene scene)" (Decode-Text 'ICAgIHByaXZhdGUgYm9vbCBXYXNTZWVuKFNjZW5hcmlvVjNTY2VuZSBzY2VuZSkKICAgIHsKICAgICAgICAvLyDrr7zsnqzsnZgg64yA7LacIOygnOyViOydgCDsi6TsoJzroZwg64+I7J2EIOuwm+ydgCDsiJzqsITsl5Drp4wg7KKF66OM65Cc64ukLgogICAgICAgIC8vIO2VnCDrsogg6rGw7KCI7ZaI642U652864+EIOuLpOyLnCDsnpDquIjsnbQg67CU64ul64KY66m0IOqwmeydgCDsobDqsbTsnLzroZwg7KCc7JWI67Cb7J2EIOyImCDsnojri6QuCiAgICAgICAgaWYgKChzdHJpbmcuRXF1YWxzKHNjZW5lLmlkLCAibWluamFlX2xvYW5fb2ZmZXIiLCBTdHJpbmdDb21wYXJpc29uLk9yZGluYWxJZ25vcmVDYXNlKSB8fAogICAgICAgICAgICAgc3RyaW5nLkVxdWFscyhzY2VuZS5pZCwgIm1pbmphZV9sb2FuX3JlamVjdGVkIiwgU3RyaW5nQ29tcGFyaXNvbi5PcmRpbmFsSWdub3JlQ2FzZSkpICYmCiAgICAgICAgICAgIEdldFN0YXRlKCJib3Jyb3dlZC5taW5qYWUiKSAhPSAidHJ1ZSIpCiAgICAgICAgICAgIHJldHVybiBmYWxzZTsKCiAgICAgICAgcmV0dXJuIHNlZW5TY2VuZXMuQ29udGFpbnMoU2NlbmVTZWVuS2V5KHNjZW5lKSk7CiAgICB9') "민재 대출 거절 후 재진입"

        $Director = Replace-CSharpMethod $Director "private void FinishLine(ScenarioV3Line line)" (Decode-Text 'ICAgIHByaXZhdGUgdm9pZCBGaW5pc2hMaW5lKFNjZW5hcmlvVjNMaW5lIGxpbmUpCiAgICB7CiAgICAgICAgaWYgKGFjdGl2ZVNjZW5lID09IG51bGwgfHwgYWN0aXZlTGluZUluZGV4IDwgMCB8fCBhY3RpdmVMaW5lSW5kZXggPj0gYWN0aXZlU2NlbmUubGluZXMuQ291bnQpCiAgICAgICAgICAgIHJldHVybjsKCiAgICAgICAgU2NlbmFyaW9WM0xpbmUgY3VycmVudExpbmUgPSBhY3RpdmVTY2VuZS5saW5lc1thY3RpdmVMaW5lSW5kZXhdOwogICAgICAgIGlmICghUmVmZXJlbmNlRXF1YWxzKGN1cnJlbnRMaW5lLCBsaW5lKSAmJgogICAgICAgICAgICAhc3RyaW5nLkVxdWFscyhjdXJyZW50TGluZS5pZCwgbGluZT8uaWQsIFN0cmluZ0NvbXBhcmlzb24uT3JkaW5hbElnbm9yZUNhc2UpKQogICAgICAgICAgICByZXR1cm47CgogICAgICAgIHN0cmluZyBuZXh0ID0gbGluZS5hdXRvTmV4dDsKICAgICAgICBhY3RpdmVMaW5lSW5kZXgrKzsKICAgICAgICBpZiAocGVuZGluZ0xhdGVXYWtlQWZ0ZXJHYW1ibGluZyAmJgogICAgICAgICAgICAoYWN0aXZlTGluZUluZGV4ID49IGFjdGl2ZVNjZW5lLmxpbmVzLkNvdW50IHx8ICFzdHJpbmcuSXNOdWxsT3JXaGl0ZVNwYWNlKG5leHQpKSkKICAgICAgICB7CiAgICAgICAgICAgIEJlZ2luRm9yY2VkTGF0ZU1vcm5pbmdBZHZhbmNlKCk7CiAgICAgICAgICAgIHJldHVybjsKICAgICAgICB9CgogICAgICAgIGlmICghZGF0YWJhc2UuU2hvdWxkUmV0dXJuVG9UYWJsZXQoYWN0aXZlU2NlbmU/LmlkKSAmJiAhc3RyaW5nLklzTnVsbE9yV2hpdGVTcGFjZShuZXh0KSkKICAgICAgICB7CiAgICAgICAgICAgIEFjdGlvbiBwbGF5TmV4dCA9ICgpID0+CiAgICAgICAgICAgIHsKICAgICAgICAgICAgICAgIGlmIChUcnlSZXR1cm5Ib21lQmVmb3JlTmV4dFNjZW5lKG5leHQsICgpID0+IFBsYXlTY2VuZShuZXh0KSkpCiAgICAgICAgICAgICAgICAgICAgcmV0dXJuOwogICAgICAgICAgICAgICAgYWN0aXZlU2NlbmUgPSBudWxsOwogICAgICAgICAgICAgICAgUGxheVNjZW5lKG5leHQpOwogICAgICAgICAgICB9OwoKICAgICAgICAgICAgaWYgKHN0cmluZy5FcXVhbHMobGluZS5kZWxpdmVyeSwgIm1lc3NhZ2UiLCBTdHJpbmdDb21wYXJpc29uLk9yZGluYWxJZ25vcmVDYXNlKSAmJgogICAgICAgICAgICAgICAgZGlhbG9ndWUgIT0gbnVsbCAmJiBkaWFsb2d1ZS5Jc0RpYWxvZ3VlT3BlbiAmJiAhV2lsbENvbnRpbnVlSW5zaWRlTWVzc2FnZShuZXh0KSkKICAgICAgICAgICAgewogICAgICAgICAgICAgICAgd2FpdGluZ0Zvck1lc3NhZ2VTY2VuZUNsb3NlID0gdHJ1ZTsKICAgICAgICAgICAgICAgIHBlbmRpbmdBZnRlck1lc3NhZ2VDbG9zZSA9IHBsYXlOZXh0OwogICAgICAgICAgICAgICAgcmV0dXJuOwogICAgICAgICAgICB9CgogICAgICAgICAgICBwbGF5TmV4dCgpOwogICAgICAgICAgICByZXR1cm47CiAgICAgICAgfQoKICAgICAgICBQcmVzZW50TGluZSgpOwogICAgfQ==') "메시지 위 오버레이 연결"

        $Director = Replace-LiteralOnce $Director (Decode-Text 'ICAgICAgICBpZiAocGVuZGluZ0xhdGVXYWtlQWZ0ZXJHYW1ibGluZyB8fCBwZW5kaW5nQm9ycm93TW9ybmluZ0FkdmFuY2UpCiAgICAgICAgewogICAgICAgICAgICBCZWdpbkZvcmNlZExhdGVNb3JuaW5nQWR2YW5jZSgpOwogICAgICAgICAgICByZXR1cm47CiAgICAgICAgfQ==') (Decode-Text 'ICAgICAgICBpZiAocGVuZGluZ0xhdGVXYWtlQWZ0ZXJHYW1ibGluZykKICAgICAgICB7CiAgICAgICAgICAgIEJlZ2luRm9yY2VkTGF0ZU1vcm5pbmdBZHZhbmNlKCk7CiAgICAgICAgICAgIHJldHVybjsKICAgICAgICB9') "차용 상태의 강제 아침 전환 제거"

        $Director = Replace-CSharpMethod $Director "private void BeginForcedLateMorningAdvance()" (Decode-Text 'ICAgIHByaXZhdGUgdm9pZCBCZWdpbkZvcmNlZExhdGVNb3JuaW5nQWR2YW5jZSgpCiAgICB7CiAgICAgICAgLy8g6rCV7KCcIOuLpOydjCDrgqAg7KCE7ZmY7J2AIOyLpOygnCDrj4TrsJXsnLzroZwg7Jik7KCEIDfsi5wg6rK96rOE66W8IOuEmOq4tCDqsr3smrDsl5Drp4wg7IKs7Jqp7ZWc64ukLgogICAgICAgIGJvb2wgc2hvd0JvcnJvd01lbnUgPSBHZXRTdGF0ZSgicGVuZGluZy5ib3Jyb3dfbWVudSIpID09ICJ0cnVlIjsKICAgICAgICBwZW5kaW5nTGF0ZVdha2VBZnRlckdhbWJsaW5nID0gZmFsc2U7CiAgICAgICAgcGVuZGluZ0JvcnJvd01vcm5pbmdBZHZhbmNlID0gZmFsc2U7CgogICAgICAgIEZpbmFsaXplQ3VycmVudERheVN0YXR1cygpOwogICAgICAgIGlmIChmbG93LkN1cnJlbnREYXkgPj0gRmluYWxEYXkpCiAgICAgICAgewogICAgICAgICAgICBhY3RpdmVTY2VuZSA9IG51bGw7CiAgICAgICAgICAgIGFjdGl2ZUxpbmVJbmRleCA9IDA7CiAgICAgICAgICAgIEhpZGVOb3ZlbCgpOwogICAgICAgICAgICByZXR1cm47CiAgICAgICAgfQoKICAgICAgICBzY2VuZVF1ZXVlLkNsZWFyKCk7CiAgICAgICAgcXVldWVDb21wbGV0ZWQgPSBudWxsOwogICAgICAgIGFjdGl2ZVNjZW5lID0gbnVsbDsKICAgICAgICBhY3RpdmVMaW5lSW5kZXggPSAwOwogICAgICAgIHdhaXRpbmdGb3JNZXNzYWdlQ2hvaWNlID0gZmFsc2U7CiAgICAgICAgd2FpdGluZ0Zvck1lc3NhZ2VTY2VuZUNsb3NlID0gZmFsc2U7CiAgICAgICAgcGVuZGluZ0FmdGVyTWVzc2FnZUNsb3NlID0gbnVsbDsKICAgICAgICBDbGVhclBlbmRpbmdPdXRnb2luZ01lc3NhZ2UoKTsKICAgICAgICBIaWRlTm92ZWwoKTsKICAgICAgICBhcHBXaW5kb3c/LkNsb3NlQ3VycmVudEFwcCgpOwoKICAgICAgICBmbG93LlYzQmVnaW5OZXh0RGF5KCk7CiAgICAgICAgc3RhdGVbInNjaGVkdWxlLnNjaG9vbCJdID0gInBlbmRpbmciOwogICAgICAgIHN0YXRlWyJzY2hlZHVsZS5ob21ld29yayJdID0gInBlbmRpbmciOwogICAgICAgIHN0YXRlWyJzY2hlZHVsZS5qb2IiXSA9ICJwZW5kaW5nIjsKICAgICAgICBzdGF0ZVsic2NoZWR1bGUuc2xlZXAiXSA9ICJwZW5kaW5nIjsKICAgICAgICBzdGF0ZVsiZXZlbmluZ19maWxsZWQiXSA9ICIwIjsKICAgICAgICBzdGF0ZVsiYmVkdGltZV9jdWVkIl0gPSAiMCI7CiAgICAgICAgc3RhdGVbImRheV9maW5hbGl6ZWQiXSA9ICIwIjsKICAgICAgICBzdGF0ZVsicGVuZGluZy5nYW1ibGVfYXR0ZW50aW9uIl0gPSAiZmFsc2UiOwogICAgICAgIHN0YXRlWyJwZW5kaW5nLmJvcnJvd19tZW51Il0gPSBzaG93Qm9ycm93TWVudSA/ICJ0cnVlIiA6ICJmYWxzZSI7CiAgICAgICAgc3RhdGVbImZsYWcubGF0ZV93YWtlX3RvZGF5Il0gPSAidHJ1ZSI7CiAgICAgICAgc3RhdGVbImZsYWcuYm9ycm93X2RlZmVycmVkIl0gPSAiZmFsc2UiOwogICAgICAgIHN0YXRlWyJmbGFnLmdhbWJsZWRfbGF0ZSJdID0gInRydWUiOwogICAgICAgIHN0YXRlWyJkYXlfY2FzaF9zdGFydCJdID0gZmxvdy5WM0JhbmtDYXNoLlRvU3RyaW5nKEN1bHR1cmVJbmZvLkludmFyaWFudEN1bHR1cmUpOwogICAgICAgIGZsb3cuVjNTZXRMb2NhdGlvbigi7KeRIik7CiAgICAgICAgZmxvdy5WM1NldENsb2NrKCIxMDowMCIpOwogICAgICAgIFNhdmUoKTsKCiAgICAgICAgUXVldWVUcmlnZ2VyKCJkYXlfc3RhcnQiLCAoKSA9PgogICAgICAgIHsKICAgICAgICAgICAgU2V0U3RhdGUoImZsYWcubGF0ZV93YWtlX3RvZGF5IiwgImZhbHNlIik7CiAgICAgICAgICAgIFNldFN0YXRlKCJmbGFnLmdhbWJsZWRfbGF0ZSIsICJmYWxzZSIpOwogICAgICAgICAgICBTZXRTdGF0ZSgiZmxhZy5ib3Jyb3dfZGVmZXJyZWQiLCAiZmFsc2UiKTsKCiAgICAgICAgICAgIGlmIChHZXRTdGF0ZSgicGVuZGluZy5ib3Jyb3dfbWVudSIpICE9ICJ0cnVlIikKICAgICAgICAgICAgewogICAgICAgICAgICAgICAgU2F2ZSgpOwogICAgICAgICAgICAgICAgcmV0dXJuOwogICAgICAgICAgICB9CgogICAgICAgICAgICBTZXRTdGF0ZSgicGVuZGluZy5ib3Jyb3dfbWVudSIsICJmYWxzZSIpOwogICAgICAgICAgICBhcHBXaW5kb3c/LkNsb3NlQ3VycmVudEFwcCgpOwogICAgICAgICAgICBTYXZlKCk7CiAgICAgICAgICAgIFBsYXlTY2VuZSgiYm9ycm93X2Nob2ljZSIpOwogICAgICAgIH0pOwogICAgICAgIFN0YXJ0UXVldWVkU2NlbmUoKTsKICAgIH0=') "실제 밤샘 전용 다음 날 전환"

        $Director = Replace-CSharpMethod $Director "private void AdvanceToNextDay()" (Decode-Text 'ICAgIHByaXZhdGUgdm9pZCBBZHZhbmNlVG9OZXh0RGF5KCkKICAgIHsKICAgICAgICBSZXNvbHZlUGVuZGluZ0dhbWJsZUF0dGVudGlvbkFzRGVjbGluZWQoKTsKICAgICAgICBGaW5hbGl6ZUN1cnJlbnREYXlTdGF0dXMoKTsKCiAgICAgICAgaWYgKFF1ZXVlVHJpZ2dlcigiY29sbGFwc2VfY2hlY2siLCBudWxsKSA+IDApCiAgICAgICAgewogICAgICAgICAgICBTdGFydFF1ZXVlZFNjZW5lKCk7CiAgICAgICAgICAgIHJldHVybjsKICAgICAgICB9CgogICAgICAgIGlmIChmbG93LkN1cnJlbnREYXkgPj0gRmluYWxEYXkpCiAgICAgICAgICAgIHJldHVybjsKCiAgICAgICAgYm9vbCBzaG93Qm9ycm93TWVudSA9IEdldFN0YXRlKCJwZW5kaW5nLmJvcnJvd19tZW51IikgPT0gInRydWUiOwoKICAgICAgICBmbG93LlYzQmVnaW5OZXh0RGF5KCk7CiAgICAgICAgc3RhdGVbInNjaGVkdWxlLnNjaG9vbCJdID0gInBlbmRpbmciOwogICAgICAgIHN0YXRlWyJzY2hlZHVsZS5ob21ld29yayJdID0gInBlbmRpbmciOwogICAgICAgIHN0YXRlWyJzY2hlZHVsZS5qb2IiXSA9ICJwZW5kaW5nIjsKICAgICAgICBzdGF0ZVsic2NoZWR1bGUuc2xlZXAiXSA9ICJwZW5kaW5nIjsKICAgICAgICBzdGF0ZVsiZXZlbmluZ19maWxsZWQiXSA9ICIwIjsKICAgICAgICBzdGF0ZVsiYmVkdGltZV9jdWVkIl0gPSAiMCI7CiAgICAgICAgc3RhdGVbImRheV9maW5hbGl6ZWQiXSA9ICIwIjsKICAgICAgICBzdGF0ZVsicGVuZGluZy5nYW1ibGVfYXR0ZW50aW9uIl0gPSAiZmFsc2UiOwogICAgICAgIHN0YXRlWyJwZW5kaW5nLmJvcnJvd19tZW51Il0gPSBzaG93Qm9ycm93TWVudSA/ICJ0cnVlIiA6ICJmYWxzZSI7CiAgICAgICAgc3RhdGVbImZsYWcubGF0ZV93YWtlX3RvZGF5Il0gPSAiZmFsc2UiOwogICAgICAgIHN0YXRlWyJmbGFnLmJvcnJvd19kZWZlcnJlZCJdID0gImZhbHNlIjsKICAgICAgICBzdGF0ZVsiZmxhZy5nYW1ibGVkX2xhdGUiXSA9ICJmYWxzZSI7CiAgICAgICAgc3RhdGVbImRheV9jYXNoX3N0YXJ0Il0gPSBmbG93LlYzQmFua0Nhc2guVG9TdHJpbmcoQ3VsdHVyZUluZm8uSW52YXJpYW50Q3VsdHVyZSk7CiAgICAgICAgU2F2ZSgpOwoKICAgICAgICBRdWV1ZVRyaWdnZXIoImRheV9zdGFydCIsICgpID0+CiAgICAgICAgewogICAgICAgICAgICBpZiAoR2V0U3RhdGUoInBlbmRpbmcuYm9ycm93X21lbnUiKSAhPSAidHJ1ZSIpCiAgICAgICAgICAgIHsKICAgICAgICAgICAgICAgIFNhdmUoKTsKICAgICAgICAgICAgICAgIHJldHVybjsKICAgICAgICAgICAgfQoKICAgICAgICAgICAgU2V0U3RhdGUoInBlbmRpbmcuYm9ycm93X21lbnUiLCAiZmFsc2UiKTsKICAgICAgICAgICAgYXBwV2luZG93Py5DbG9zZUN1cnJlbnRBcHAoKTsKICAgICAgICAgICAgU2F2ZSgpOwogICAgICAgICAgICBQbGF5U2NlbmUoImJvcnJvd19jaG9pY2UiKTsKICAgICAgICB9KTsKICAgICAgICBTdGFydFF1ZXVlZFNjZW5lKCk7CiAgICB9') "정상 취침 뒤 차용 선택 재개"

        $Director = Replace-RegexOnce $Director (Decode-Text 'KD9tcykgICAgICAgICAgICBpbnQgc2Vzc2lvbiA9IEdldEludFwoImNvdW50ZXJcLmdhbWJsZV9zZXNzaW9ucyJcKSBcKyAxO1xyP1xuICAgICAgICAgICAgaWYgXChzZXNzaW9uID49IDYgJiYgZmxvd1wuVjNCYW5rQ2FzaCA8PSAwXClccj9cbiAgICAgICAgICAgIFx7Lio/ICAgICAgICAgICAgaW1tZWRpYXRlUm91dGUgPSAiZ2FtYmxlXyIgXCsgc2Vzc2lvblwuVG9TdHJpbmdcKEN1bHR1cmVJbmZvXC5JbnZhcmlhbnRDdWx0dXJlXCk7XHI/XG4gICAgICAgICAgICByZXR1cm47') (Decode-Text 'ICAgICAgICAgICAgaW50IHNlc3Npb24gPSBHZXRJbnQoImNvdW50ZXIuZ2FtYmxlX3Nlc3Npb25zIikgKyAxOwogICAgICAgICAgICBpZiAoc2Vzc2lvbiA+PSA2ICYmIGZsb3cuVjNCYW5rQ2FzaCA8PSAwKQogICAgICAgICAgICB7CiAgICAgICAgICAgICAgICBBZGRJbnQoImNvdW50ZXIubm9fZnVuZHNfYXR0ZW1wdHMiLCAxKTsKICAgICAgICAgICAgICAgIGJvb2wgYWxsQm9ycm93U291cmNlc1VzZWQgPSBHZXRTdGF0ZSgiYm9ycm93ZWQubW9tIikgPT0gInRydWUiICYmCiAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgR2V0U3RhdGUoImJvcnJvd2VkLnNlb2p1biIpID09ICJ0cnVlIiAmJgogICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgIEdldFN0YXRlKCJib3Jyb3dlZC5taW5qYWUiKSA9PSAidHJ1ZSI7CiAgICAgICAgICAgICAgICBpbW1lZGlhdGVSb3V0ZSA9IGFsbEJvcnJvd1NvdXJjZXNVc2VkID8gImdhbWJsZV9ub19mdW5kc19leGhhdXN0ZWQiIDogImdhbWJsZV9ub19mdW5kcyI7CiAgICAgICAgICAgICAgICByZXR1cm47CiAgICAgICAgICAgIH0KCiAgICAgICAgICAgIGlmIChzZXNzaW9uID4gOCkKICAgICAgICAgICAgewogICAgICAgICAgICAgICAgU2V0U3RhdGUoImNvdW50ZXIuZ2FtYmxlX3Nlc3Npb25zIiwgc2Vzc2lvbi5Ub1N0cmluZyhDdWx0dXJlSW5mby5JbnZhcmlhbnRDdWx0dXJlKSk7CiAgICAgICAgICAgICAgICBpbW1lZGlhdGVSb3V0ZSA9ICJnYW1ibGVfcmVwZWF0X2xvc3MiOwogICAgICAgICAgICAgICAgcmV0dXJuOwogICAgICAgICAgICB9CgogICAgICAgICAgICBTZXRTdGF0ZSgiY291bnRlci5nYW1ibGVfc2Vzc2lvbnMiLCBzZXNzaW9uLlRvU3RyaW5nKEN1bHR1cmVJbmZvLkludmFyaWFudEN1bHR1cmUpKTsKICAgICAgICAgICAgU2V0U3RhdGUoImZsYWcuZ2FtYmxpbmdfc3RhcnRlZCIsICJ0cnVlIik7CiAgICAgICAgICAgIGltbWVkaWF0ZVJvdXRlID0gImdhbWJsZV8iICsgc2Vzc2lvbi5Ub1N0cmluZyhDdWx0dXJlSW5mby5JbnZhcmlhbnRDdWx0dXJlKTsKICAgICAgICAgICAgcmV0dXJuOw==') "추가 도박 7·8회차 라우팅"

        $Director = Replace-RegexOnce $Director (Decode-Text 'KD9tcykgICAgICAgICAgICBpZiBcKG9wZXJhdGlvblwuRXF1YWxzXCgiZGVmZXIiLCBTdHJpbmdDb21wYXJpc29uXC5PcmRpbmFsSWdub3JlQ2FzZVwpXClccj9cbiAgICAgICAgICAgIFx7XHI/XG4uKj8gICAgICAgICAgICAgICAgcmV0dXJuO1xyP1xuICAgICAgICAgICAgXH0=') (Decode-Text 'ICAgICAgICAgICAgaWYgKG9wZXJhdGlvbi5FcXVhbHMoImRlZmVyIiwgU3RyaW5nQ29tcGFyaXNvbi5PcmRpbmFsSWdub3JlQ2FzZSkpCiAgICAgICAgICAgIHsKICAgICAgICAgICAgICAgIC8vIOywqOyaqSDsmpTssq3snYAg7JiI7JW966eMIO2VmOqzoCwg64Kg7Kec64qUIOy3qOy5qCDslbHsnYQg64iM66CA7J2EIOuVjOunjCDrhJjslrTqsITri6QuCiAgICAgICAgICAgICAgICBTZXRTdGF0ZSgicGVuZGluZy5ib3Jyb3dfbWVudSIsICJ0cnVlIik7CiAgICAgICAgICAgICAgICBTZXRTdGF0ZSgiZmxhZy5ib3Jyb3dfZGVmZXJyZWQiLCAidHJ1ZSIpOwogICAgICAgICAgICAgICAgcGVuZGluZ0JvcnJvd01vcm5pbmdBZHZhbmNlID0gZmFsc2U7CiAgICAgICAgICAgICAgICBmbG93LlYzTWFya0FwcEF0dGVudGlvbihBcHBUeXBlLlNsZWVwKTsKICAgICAgICAgICAgICAgIHJldHVybjsKICAgICAgICAgICAgfQ==') "차용 선택 후 강제 날짜 이동 제거"

        $Director = Replace-RegexOnce $Director (Decode-Text 'KD9tcykgICAgICAgIGlmIFwoa2V5XC5FcXVhbHNcKCJyZXBheSIsIFN0cmluZ0NvbXBhcmlzb25cLk9yZGluYWxJZ25vcmVDYXNlXCkgJiZccj9cbiAgICAgICAgICAgIG9wZXJhdGlvblwuRXF1YWxzXCgiYXZhaWxhYmxlIiwgU3RyaW5nQ29tcGFyaXNvblwuT3JkaW5hbElnbm9yZUNhc2VcKVwpXHI/XG4gICAgICAgIFx7XHI/XG4uKj8gICAgICAgICAgICByZXR1cm47XHI/XG4gICAgICAgIFx9') (Decode-Text 'ICAgICAgICBpZiAoa2V5LkVxdWFscygicmVwYXkiLCBTdHJpbmdDb21wYXJpc29uLk9yZGluYWxJZ25vcmVDYXNlKSAmJgogICAgICAgICAgICBvcGVyYXRpb24uU3RhcnRzV2l0aCgiYXZhaWxhYmxlIiwgU3RyaW5nQ29tcGFyaXNvbi5PcmRpbmFsSWdub3JlQ2FzZSkpCiAgICAgICAgewogICAgICAgICAgICBzdHJpbmcgY3JlZGl0b3IgPSAic2VvanVuIjsKICAgICAgICAgICAgaW50IGVxdWFsc0luZGV4ID0gb3BlcmF0aW9uLkluZGV4T2YoJz0nKTsKICAgICAgICAgICAgaWYgKGVxdWFsc0luZGV4ID49IDAgJiYgZXF1YWxzSW5kZXggKyAxIDwgb3BlcmF0aW9uLkxlbmd0aCkKICAgICAgICAgICAgICAgIGNyZWRpdG9yID0gb3BlcmF0aW9uLlN1YnN0cmluZyhlcXVhbHNJbmRleCArIDEpLlRyaW0oKS5Ub0xvd2VySW52YXJpYW50KCk7CgogICAgICAgICAgICBzdHJpbmcgZGVzY3JpcHRpb24gPSBjcmVkaXRvciA9PSAibWluamFlIgogICAgICAgICAgICAgICAgPyAi66+87J6s7JeQ6rKMIOu5jOumsCDrj4gg7IOB7ZmYIgogICAgICAgICAgICAgICAgOiBjcmVkaXRvciA9PSAibW9tIgogICAgICAgICAgICAgICAgICAgID8gIuyXhOuniOyXkOqyjCDruYzrprAg64+IIOyDge2ZmCIKICAgICAgICAgICAgICAgICAgICA6ICLshJzspIDsl5Dqsowg67mM66awIOuPiCDsg4HtmZgiOwoKICAgICAgICAgICAgaW50IHJlcGFpZCA9IGZsb3cuVjNSZXBheUF2YWlsYWJsZURlYnQoZGVzY3JpcHRpb24pOwogICAgICAgICAgICBTZXRTdGF0ZSgibGFzdF9yZXBheW1lbnQiLCByZXBhaWQuVG9TdHJpbmcoQ3VsdHVyZUluZm8uSW52YXJpYW50Q3VsdHVyZSkpOwogICAgICAgICAgICBpZiAoZmxvdy5DdXJyZW50RGVidCA8PSAwKQogICAgICAgICAgICAgICAgU2V0U3RhdGUoImRlYnRfb3duZXIiLCAibm9uZSIpOwogICAgICAgICAgICByZXR1cm47CiAgICAgICAgfQ==') "채권자별 거래 내역"

        $Director = Replace-CSharpMethod $Director "private void ShowTabletOverlayLine(ScenarioV3Line line)" (Decode-Text 'ICAgIHByaXZhdGUgdm9pZCBTaG93VGFibGV0T3ZlcmxheUxpbmUoU2NlbmFyaW9WM0xpbmUgbGluZSkKICAgIHsKICAgICAgICBpZiAoR2V0QXZhaWxhYmxlQ2hvaWNlcyhsaW5lKS5Db3VudCA+IDApCiAgICAgICAgewogICAgICAgICAgICBTaG93VGFibGV0Q2hvaWNlTGluZShsaW5lKTsKICAgICAgICAgICAgcmV0dXJuOwogICAgICAgIH0KCiAgICAgICAgSGlkZU5vdmVsKCk7CiAgICAgICAgc3RyaW5nIHRpdGxlID0gc3RyaW5nLkVxdWFscyhsaW5lLnNwZWFrZXIsICJOYXJyYXRvciIsIFN0cmluZ0NvbXBhcmlzb24uT3JkaW5hbElnbm9yZUNhc2UpIHx8CiAgICAgICAgICAgICAgICAgICAgICAgc3RyaW5nLkVxdWFscyhsaW5lLnNwZWFrZXIsICJTeXN0ZW0iLCBTdHJpbmdDb21wYXJpc29uLk9yZGluYWxJZ25vcmVDYXNlKQogICAgICAgICAgICA/ICLslYjrgrQiCiAgICAgICAgICAgIDogQ29udGFjdE5hbWUobGluZS5zcGVha2VyKTsKICAgICAgICBzdHJpbmcgdGV4dCA9IEZvcm1hdFByb3RhZ29uaXN0TW9ub2xvZ3VlKGxpbmUsIEV4cGFuZFRleHQobGluZS50ZXh0KSk7CiAgICAgICAgaWYgKCFmbG93LlYzU2hvd0RpYWxvZ3VlKHRpdGxlLCB0ZXh0LCAoKSA9PiBGaW5pc2hMaW5lKGxpbmUpKSkKICAgICAgICAgICAgU2hvd05vdmVsTGluZShsaW5lKTsKICAgIH0KCiAgICBwcml2YXRlIHZvaWQgU2hvd1RhYmxldENob2ljZUxpbmUoU2NlbmFyaW9WM0xpbmUgbGluZSkKICAgIHsKICAgICAgICBpZiAobm92ZWxQYW5lbCA9PSBudWxsKQogICAgICAgICAgICByZXR1cm47CgogICAgICAgIG5vdGlmaWNhdGlvbnM/LkhpZGVQb3B1cCgpOwogICAgICAgIFNldE5vdmVsQmFja2Ryb3BWaXNpYmxlKGZhbHNlKTsKICAgICAgICBub3ZlbFBhbmVsLlNldEFjdGl2ZSh0cnVlKTsKICAgICAgICBub3ZlbFBhbmVsLnRyYW5zZm9ybS5TZXRBc0xhc3RTaWJsaW5nKCk7CiAgICAgICAgaWYgKGhpc3RvcnlQYW5lbCAhPSBudWxsKQogICAgICAgICAgICBoaXN0b3J5UGFuZWwuU2V0QWN0aXZlKGZhbHNlKTsKCiAgICAgICAgR2FtZU9iamVjdCBkaWFsb2d1ZUJveCA9IGJvZHlUZXh0ICE9IG51bGwgJiYgYm9keVRleHQudHJhbnNmb3JtLnBhcmVudCAhPSBudWxsCiAgICAgICAgICAgID8gYm9keVRleHQudHJhbnNmb3JtLnBhcmVudC5nYW1lT2JqZWN0CiAgICAgICAgICAgIDogbnVsbDsKICAgICAgICBpZiAoZGlhbG9ndWVCb3ggIT0gbnVsbCkKICAgICAgICAgICAgZGlhbG9ndWVCb3guU2V0QWN0aXZlKHRydWUpOwoKICAgICAgICBzdHJpbmcgZGlzcGxheVNwZWFrZXIgPSBzdHJpbmcuRXF1YWxzKGxpbmUuc3BlYWtlciwgIk5hcnJhdG9yIiwgU3RyaW5nQ29tcGFyaXNvbi5PcmRpbmFsSWdub3JlQ2FzZSkgfHwKICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICBzdHJpbmcuRXF1YWxzKGxpbmUuc3BlYWtlciwgIlN5c3RlbSIsIFN0cmluZ0NvbXBhcmlzb24uT3JkaW5hbElnbm9yZUNhc2UpCiAgICAgICAgICAgID8gc3RyaW5nLkVtcHR5CiAgICAgICAgICAgIDogQ29udGFjdE5hbWUobGluZS5zcGVha2VyKTsKICAgICAgICBzcGVha2VyVGV4dC50ZXh0ID0gZGlzcGxheVNwZWFrZXI7CgogICAgICAgIHN0cmluZyByYXdUZXh0ID0gRXhwYW5kVGV4dChsaW5lLnRleHQpOwogICAgICAgIGN1cnJlbnREaWFsb2d1ZVBhZ2VzID0gUGFnaW5hdGVEaWFsb2d1ZShyYXdUZXh0KTsKICAgICAgICBpZiAoSXNQcm90YWdvbmlzdE1vbm9sb2d1ZShsaW5lKSkKICAgICAgICB7CiAgICAgICAgICAgIGZvciAoaW50IGluZGV4ID0gMDsgaW5kZXggPCBjdXJyZW50RGlhbG9ndWVQYWdlcy5Db3VudDsgaW5kZXgrKykKICAgICAgICAgICAgICAgIGN1cnJlbnREaWFsb2d1ZVBhZ2VzW2luZGV4XSA9IEZvcm1hdFByb3RhZ29uaXN0TW9ub2xvZ3VlKGxpbmUsIGN1cnJlbnREaWFsb2d1ZVBhZ2VzW2luZGV4XSk7CiAgICAgICAgfQoKICAgICAgICBjdXJyZW50RGlhbG9ndWVQYWdlSW5kZXggPSAwOwogICAgICAgIGN1cnJlbnRGdWxsVGV4dCA9IGN1cnJlbnREaWFsb2d1ZVBhZ2VzWzBdOwogICAgICAgIHN0cmluZyBleHBhbmRlZFRleHQgPSBzdHJpbmcuSm9pbigiICIsIGN1cnJlbnREaWFsb2d1ZVBhZ2VzKTsKICAgICAgICBzdHJpbmcgbG9nU3BlYWtlciA9IHN0cmluZy5Jc051bGxPcldoaXRlU3BhY2UoZGlzcGxheVNwZWFrZXIpID8gIuuCmCIgOiBkaXNwbGF5U3BlYWtlcjsKICAgICAgICBBcHBlbmREaWFsb2d1ZUxvZyhsb2dTcGVha2VyLCBleHBhbmRlZFRleHQpOwoKICAgICAgICBMaXN0PFNjZW5hcmlvVjNDaG9pY2U+IGNob2ljZXMgPSBHZXRBdmFpbGFibGVDaG9pY2VzKGxpbmUpOwogICAgICAgIGNvbnRpbnVlQnV0dG9uLmludGVyYWN0YWJsZSA9IHRydWU7CiAgICAgICAgY29udGludWVCdXR0b24uZ2FtZU9iamVjdC5TZXRBY3RpdmUoZmFsc2UpOwogICAgICAgIENvbmZpZ3VyZUNob2ljZUJ1dHRvbihjaG9pY2VBQnV0dG9uLCBjaG9pY2VzLkNvdW50ID4gMCA/IGNob2ljZXNbMF0gOiBudWxsKTsKICAgICAgICBDb25maWd1cmVDaG9pY2VCdXR0b24oY2hvaWNlQkJ1dHRvbiwgY2hvaWNlcy5Db3VudCA+IDEgPyBjaG9pY2VzWzFdIDogbnVsbCk7CiAgICAgICAgQ29uZmlndXJlQ2hvaWNlQnV0dG9uKGNob2ljZUNCdXR0b24sIGNob2ljZXMuQ291bnQgPiAyID8gY2hvaWNlc1syXSA6IG51bGwpOwogICAgICAgIFNldENob2ljZUJ1dHRvbnNWaXNpYmxlKGZhbHNlKTsKICAgICAgICBTZXRDaG9pY2VCdXR0b25zSW50ZXJhY3RhYmxlKHRydWUpOwogICAgICAgIGNvbnRpbnVlQnV0dG9uLm9uQ2xpY2suUmVtb3ZlQWxsTGlzdGVuZXJzKCk7CiAgICAgICAgY29udGludWVCdXR0b24ub25DbGljay5BZGRMaXN0ZW5lcigoKSA9PgogICAgICAgIHsKICAgICAgICAgICAgaWYgKGlzVHlwaW5nKQogICAgICAgICAgICAgICAgQ29tcGxldGVUeXBld3JpdGVyKElzTGFzdERpYWxvZ3VlUGFnZSAmJiBjaG9pY2VzLkNvdW50ID4gMCk7CiAgICAgICAgICAgIGVsc2UgaWYgKCFJc0xhc3REaWFsb2d1ZVBhZ2UpCiAgICAgICAgICAgICAgICBTaG93TmV4dERpYWxvZ3VlUGFnZShjaG9pY2VzLkNvdW50ID4gMCk7CiAgICAgICAgfSk7CgogICAgICAgIGlmICh0eXBld3JpdGVyQ29yb3V0aW5lICE9IG51bGwpCiAgICAgICAgICAgIFN0b3BDb3JvdXRpbmUodHlwZXdyaXRlckNvcm91dGluZSk7CiAgICAgICAgdHlwZXdyaXRlckNvcm91dGluZSA9IFN0YXJ0Q29yb3V0aW5lKFR5cGVMaW5lKElzTGFzdERpYWxvZ3VlUGFnZSAmJiBjaG9pY2VzLkNvdW50ID4gMCkpOwogICAgfQoKICAgIHByaXZhdGUgdm9pZCBTZXROb3ZlbEJhY2tkcm9wVmlzaWJsZShib29sIHZpc2libGUpCiAgICB7CiAgICAgICAgaWYgKG5vdmVsUGFuZWwgPT0gbnVsbCkKICAgICAgICAgICAgcmV0dXJuOwoKICAgICAgICBJbWFnZSBwYW5lbEltYWdlID0gbm92ZWxQYW5lbC5HZXRDb21wb25lbnQ8SW1hZ2U+KCk7CiAgICAgICAgaWYgKHBhbmVsSW1hZ2UgIT0gbnVsbCkKICAgICAgICB7CiAgICAgICAgICAgIGlmICghbm92ZWxCYWNrZHJvcENvbG9yQ2FwdHVyZWQpCiAgICAgICAgICAgIHsKICAgICAgICAgICAgICAgIG5vdmVsQmFja2Ryb3BDb2xvciA9IHBhbmVsSW1hZ2UuY29sb3I7CiAgICAgICAgICAgICAgICBub3ZlbEJhY2tkcm9wQ29sb3JDYXB0dXJlZCA9IHRydWU7CiAgICAgICAgICAgIH0KICAgICAgICAgICAgcGFuZWxJbWFnZS5jb2xvciA9IHZpc2libGUgPyBub3ZlbEJhY2tkcm9wQ29sb3IgOiBDb2xvci5jbGVhcjsKICAgICAgICAgICAgcGFuZWxJbWFnZS5yYXljYXN0VGFyZ2V0ID0gdHJ1ZTsKICAgICAgICB9CgogICAgICAgIGlmIChub3ZlbEJhY2tncm91bmQgIT0gbnVsbCkKICAgICAgICAgICAgbm92ZWxCYWNrZ3JvdW5kLmdhbWVPYmplY3QuU2V0QWN0aXZlKHZpc2libGUpOwogICAgICAgIGlmIChjaGFyYWN0ZXJQb3J0cmFpdCAhPSBudWxsICYmICF2aXNpYmxlKQogICAgICAgICAgICBjaGFyYWN0ZXJQb3J0cmFpdC5nYW1lT2JqZWN0LlNldEFjdGl2ZShmYWxzZSk7CgogICAgICAgIFRyYW5zZm9ybSBzaGFkZSA9IG5vdmVsUGFuZWwudHJhbnNmb3JtLkZpbmQoIlJlYWRhYmlsaXR5IFNoYWRlIik7CiAgICAgICAgaWYgKHNoYWRlICE9IG51bGwpCiAgICAgICAgICAgIHNoYWRlLmdhbWVPYmplY3QuU2V0QWN0aXZlKHZpc2libGUpOwoKICAgICAgICBpZiAoY2hhcHRlclRleHQgIT0gbnVsbCAmJiBjaGFwdGVyVGV4dC50cmFuc2Zvcm0ucGFyZW50ICE9IG51bGwpCiAgICAgICAgICAgIGNoYXB0ZXJUZXh0LnRyYW5zZm9ybS5wYXJlbnQuZ2FtZU9iamVjdC5TZXRBY3RpdmUodmlzaWJsZSk7CgogICAgICAgIFRyYW5zZm9ybSBoaXN0b3J5QnV0dG9uID0gbm92ZWxQYW5lbC50cmFuc2Zvcm0uRmluZCgiSGlzdG9yeSBCdXR0b24iKTsKICAgICAgICBpZiAoaGlzdG9yeUJ1dHRvbiAhPSBudWxsKQogICAgICAgICAgICBoaXN0b3J5QnV0dG9uLmdhbWVPYmplY3QuU2V0QWN0aXZlKHZpc2libGUpOwogICAgfQ==') "태블릿·메시지 화면 위 선택 다이얼로그"

        $Director = Replace-LiteralOnce $Director (Decode-Text 'ICAgIHByaXZhdGUgdm9pZCBTaG93Tm92ZWxMaW5lKFNjZW5hcmlvVjNMaW5lIGxpbmUsIEFjdGlvbiBjb21wbGV0ZWQsIHN0cmluZyB2aXN1YWxBcmMgPSBudWxsKQogICAgewogICAgICAgIGlmIChub3ZlbFBhbmVsID09IG51bGwpCiAgICAgICAgICAgIHJldHVybjsKCiAgICAgICAgc3RyaW5nIHJlc29sdmVkQXJjID0gdmlzdWFsQXJjID8/IGFjdGl2ZVNjZW5lPy5hcmMgPz8gImhvbWUiOw==') (Decode-Text 'ICAgIHByaXZhdGUgdm9pZCBTaG93Tm92ZWxMaW5lKFNjZW5hcmlvVjNMaW5lIGxpbmUsIEFjdGlvbiBjb21wbGV0ZWQsIHN0cmluZyB2aXN1YWxBcmMgPSBudWxsKQogICAgewogICAgICAgIGlmIChub3ZlbFBhbmVsID09IG51bGwpCiAgICAgICAgICAgIHJldHVybjsKCiAgICAgICAgU2V0Tm92ZWxCYWNrZHJvcFZpc2libGUodHJ1ZSk7CiAgICAgICAgc3RyaW5nIHJlc29sdmVkQXJjID0gdmlzdWFsQXJjID8/IGFjdGl2ZVNjZW5lPy5hcmMgPz8gImhvbWUiOw==') "일반 VN 배경 복원"

        $Director = Replace-LiteralOnce $Director (Decode-Text 'ICAgICAgICBjb250aW51ZUJ1dHRvbi5nYW1lT2JqZWN0LlNldEFjdGl2ZShmYWxzZSk7CiAgICAgICAgQ29uZmlndXJlQ2hvaWNlQnV0dG9uKGNob2ljZUFCdXR0b24sIGNob2ljZXMuQ291bnQgPiAwID8gY2hvaWNlc1swXSA6IG51bGwpOw==') (Decode-Text 'ICAgICAgICBjb250aW51ZUJ1dHRvbi5pbnRlcmFjdGFibGUgPSB0cnVlOwogICAgICAgIGNvbnRpbnVlQnV0dG9uLmdhbWVPYmplY3QuU2V0QWN0aXZlKGZhbHNlKTsKICAgICAgICBDb25maWd1cmVDaG9pY2VCdXR0b24oY2hvaWNlQUJ1dHRvbiwgY2hvaWNlcy5Db3VudCA+IDAgPyBjaG9pY2VzWzBdIDogbnVsbCk7') "계속 버튼 입력 잠금 초기화"

        $Director = Replace-LiteralOnce $Director (Decode-Text 'ICAgICAgICAgICAgZWxzZQogICAgICAgICAgICAgICAgY29tcGxldGVkPy5JbnZva2UoKTs=') (Decode-Text 'ICAgICAgICAgICAgZWxzZQogICAgICAgICAgICB7CiAgICAgICAgICAgICAgICBjb250aW51ZUJ1dHRvbi5pbnRlcmFjdGFibGUgPSBmYWxzZTsKICAgICAgICAgICAgICAgIFNldENob2ljZUJ1dHRvbnNJbnRlcmFjdGFibGUoZmFsc2UpOwogICAgICAgICAgICAgICAgY29tcGxldGVkPy5JbnZva2UoKTsKICAgICAgICAgICAgfQ==') "계속 버튼 중복 입력 차단"

        $Director = Replace-CSharpMethod $Director "private void ConfigureChoiceButton(Button button, ScenarioV3Choice choice)" (Decode-Text 'ICAgIHByaXZhdGUgdm9pZCBDb25maWd1cmVDaG9pY2VCdXR0b24oQnV0dG9uIGJ1dHRvbiwgU2NlbmFyaW9WM0Nob2ljZSBjaG9pY2UpCiAgICB7CiAgICAgICAgYnV0dG9uLmdhbWVPYmplY3QuU2V0QWN0aXZlKGNob2ljZSAhPSBudWxsKTsKICAgICAgICBidXR0b24uaW50ZXJhY3RhYmxlID0gY2hvaWNlICE9IG51bGw7CiAgICAgICAgaWYgKGNob2ljZSA9PSBudWxsKQogICAgICAgIHsKICAgICAgICAgICAgYnV0dG9uLkdldENvbXBvbmVudEluQ2hpbGRyZW48VE1QX1RleHQ+KCkudGV4dCA9IHN0cmluZy5FbXB0eTsKICAgICAgICAgICAgcmV0dXJuOwogICAgICAgIH0KCiAgICAgICAgYnV0dG9uLkdldENvbXBvbmVudEluQ2hpbGRyZW48VE1QX1RleHQ+KCkudGV4dCA9IGNob2ljZS50ZXh0OwogICAgICAgIGJ1dHRvbi5vbkNsaWNrLlJlbW92ZUFsbExpc3RlbmVycygpOwogICAgICAgIHN0cmluZyBjaG9pY2VJZCA9IGNob2ljZS5pZDsKICAgICAgICBidXR0b24ub25DbGljay5BZGRMaXN0ZW5lcigoKSA9PgogICAgICAgIHsKICAgICAgICAgICAgaWYgKCFidXR0b24uaW50ZXJhY3RhYmxlKQogICAgICAgICAgICAgICAgcmV0dXJuOwogICAgICAgICAgICBTZXRDaG9pY2VCdXR0b25zSW50ZXJhY3RhYmxlKGZhbHNlKTsKICAgICAgICAgICAgY29udGludWVCdXR0b24uaW50ZXJhY3RhYmxlID0gZmFsc2U7CiAgICAgICAgICAgIEhhbmRsZUNob2ljZShjaG9pY2VJZCk7CiAgICAgICAgfSk7CiAgICB9CgogICAgcHJpdmF0ZSB2b2lkIFNldENob2ljZUJ1dHRvbnNJbnRlcmFjdGFibGUoYm9vbCBpbnRlcmFjdGFibGUpCiAgICB7CiAgICAgICAgZm9yZWFjaCAoQnV0dG9uIGJ1dHRvbiBpbiBuZXdbXSB7IGNob2ljZUFCdXR0b24sIGNob2ljZUJCdXR0b24sIGNob2ljZUNCdXR0b24gfSkKICAgICAgICB7CiAgICAgICAgICAgIGlmIChidXR0b24gIT0gbnVsbCkKICAgICAgICAgICAgICAgIGJ1dHRvbi5pbnRlcmFjdGFibGUgPSBpbnRlcmFjdGFibGU7CiAgICAgICAgfQogICAgfQ==') "선택지 중복 입력 차단"

        $Director = Replace-CSharpMethod $Director "private void HideNovel()" (Decode-Text 'ICAgIHByaXZhdGUgdm9pZCBIaWRlTm92ZWwoKQogICAgewogICAgICAgIGlmICh0eXBld3JpdGVyQ29yb3V0aW5lICE9IG51bGwpCiAgICAgICAgewogICAgICAgICAgICBTdG9wQ29yb3V0aW5lKHR5cGV3cml0ZXJDb3JvdXRpbmUpOwogICAgICAgICAgICB0eXBld3JpdGVyQ29yb3V0aW5lID0gbnVsbDsKICAgICAgICB9CiAgICAgICAgaXNUeXBpbmcgPSBmYWxzZTsKICAgICAgICBTZXRDaG9pY2VCdXR0b25zSW50ZXJhY3RhYmxlKGZhbHNlKTsKICAgICAgICBTZXROb3ZlbEJhY2tkcm9wVmlzaWJsZSh0cnVlKTsKICAgICAgICBpZiAoaGlzdG9yeVBhbmVsICE9IG51bGwpCiAgICAgICAgICAgIGhpc3RvcnlQYW5lbC5TZXRBY3RpdmUoZmFsc2UpOwogICAgICAgIGlmIChub3ZlbFBhbmVsICE9IG51bGwpCiAgICAgICAgICAgIG5vdmVsUGFuZWwuU2V0QWN0aXZlKGZhbHNlKTsKICAgIH0=') "VN 닫기 상태 정리"

        $Director = Replace-CSharpMethod $Director "private void AppendDialogueLog(string speaker, string text)" (Decode-Text 'ICAgIHByaXZhdGUgdm9pZCBBcHBlbmREaWFsb2d1ZUxvZyhzdHJpbmcgc3BlYWtlciwgc3RyaW5nIHRleHQpCiAgICB7CiAgICAgICAgaWYgKHN0cmluZy5Jc051bGxPcldoaXRlU3BhY2UodGV4dCkpCiAgICAgICAgICAgIHJldHVybjsKCiAgICAgICAgc3RyaW5nIGVudHJ5ID0gJCJ7c3BlYWtlcn1cbnt0ZXh0fSI7CiAgICAgICAgaWYgKGRpYWxvZ3VlTG9nLkNvdW50ID4gMCAmJgogICAgICAgICAgICBzdHJpbmcuRXF1YWxzKGRpYWxvZ3VlTG9nW2RpYWxvZ3VlTG9nLkNvdW50IC0gMV0sIGVudHJ5LCBTdHJpbmdDb21wYXJpc29uLk9yZGluYWwpKQogICAgICAgICAgICByZXR1cm47CgogICAgICAgIGRpYWxvZ3VlTG9nLkFkZChlbnRyeSk7CiAgICAgICAgaWYgKGRpYWxvZ3VlTG9nLkNvdW50ID4gMjQwKQogICAgICAgICAgICBkaWFsb2d1ZUxvZy5SZW1vdmVBdCgwKTsKICAgIH0=') "대화 기록 중복 방지"

        $Director = Replace-CSharpMethod $Director "private void RebuildHistoryViewport()" (Decode-Text 'ICAgIHByaXZhdGUgdm9pZCBSZWJ1aWxkSGlzdG9yeVZpZXdwb3J0KCkKICAgIHsKICAgICAgICBpZiAoaGlzdG9yeVBhbmVsID09IG51bGwpCiAgICAgICAgICAgIHJldHVybjsKCiAgICAgICAgVHJhbnNmb3JtIHJ1bnRpbWVWaWV3cG9ydCA9IGhpc3RvcnlQYW5lbC50cmFuc2Zvcm0uRmluZCgiSGlzdG9yeSBWaWV3cG9ydCBSdW50aW1lIik7CiAgICAgICAgaWYgKHJ1bnRpbWVWaWV3cG9ydCAhPSBudWxsKQogICAgICAgICAgICBEZXN0cm95KHJ1bnRpbWVWaWV3cG9ydC5nYW1lT2JqZWN0KTsKCiAgICAgICAgVHJhbnNmb3JtIHZpZXdwb3J0VHJhbnNmb3JtID0gaGlzdG9yeVBhbmVsLnRyYW5zZm9ybS5GaW5kKCJIaXN0b3J5IFZpZXdwb3J0Iik7CiAgICAgICAgaWYgKHZpZXdwb3J0VHJhbnNmb3JtID09IG51bGwpCiAgICAgICAgICAgIHJldHVybjsKCiAgICAgICAgdmlld3BvcnRUcmFuc2Zvcm0uZ2FtZU9iamVjdC5TZXRBY3RpdmUodHJ1ZSk7CiAgICAgICAgaGlzdG9yeVZpZXdwb3J0UmVjdCA9IHZpZXdwb3J0VHJhbnNmb3JtLkdldENvbXBvbmVudDxSZWN0VHJhbnNmb3JtPigpOwogICAgICAgIGhpc3RvcnlWaWV3cG9ydFJlY3QuYW5jaG9yTWluID0gbmV3IFZlY3RvcjIoMC4wNmYsIDAuMDhmKTsKICAgICAgICBoaXN0b3J5Vmlld3BvcnRSZWN0LmFuY2hvck1heCA9IG5ldyBWZWN0b3IyKDAuOTRmLCAwLjg2Zik7CiAgICAgICAgaGlzdG9yeVZpZXdwb3J0UmVjdC5vZmZzZXRNaW4gPSBWZWN0b3IyLnplcm87CiAgICAgICAgaGlzdG9yeVZpZXdwb3J0UmVjdC5vZmZzZXRNYXggPSBWZWN0b3IyLnplcm87CiAgICAgICAgaGlzdG9yeVZpZXdwb3J0UmVjdC5waXZvdCA9IG5ldyBWZWN0b3IyKDAuNWYsIDAuNWYpOwoKICAgICAgICBJbWFnZSB2aWV3cG9ydEltYWdlID0gdmlld3BvcnRUcmFuc2Zvcm0uR2V0Q29tcG9uZW50PEltYWdlPigpOwogICAgICAgIGlmICh2aWV3cG9ydEltYWdlID09IG51bGwpCiAgICAgICAgICAgIHZpZXdwb3J0SW1hZ2UgPSB2aWV3cG9ydFRyYW5zZm9ybS5nYW1lT2JqZWN0LkFkZENvbXBvbmVudDxJbWFnZT4oKTsKICAgICAgICB2aWV3cG9ydEltYWdlLmNvbG9yID0gbmV3IENvbG9yKDAuMDRmLCAwLjA2NWYsIDAuMTBmLCAxZik7CiAgICAgICAgdmlld3BvcnRJbWFnZS5yYXljYXN0VGFyZ2V0ID0gdHJ1ZTsKCiAgICAgICAgTWFzayBsZWdhY3lNYXNrID0gdmlld3BvcnRUcmFuc2Zvcm0uR2V0Q29tcG9uZW50PE1hc2s+KCk7CiAgICAgICAgaWYgKGxlZ2FjeU1hc2sgIT0gbnVsbCkKICAgICAgICAgICAgbGVnYWN5TWFzay5lbmFibGVkID0gZmFsc2U7CiAgICAgICAgUmVjdE1hc2syRCByZWN0TWFzayA9IHZpZXdwb3J0VHJhbnNmb3JtLkdldENvbXBvbmVudDxSZWN0TWFzazJEPigpOwogICAgICAgIGlmIChyZWN0TWFzayA9PSBudWxsKQogICAgICAgICAgICByZWN0TWFzayA9IHZpZXdwb3J0VHJhbnNmb3JtLmdhbWVPYmplY3QuQWRkQ29tcG9uZW50PFJlY3RNYXNrMkQ+KCk7CiAgICAgICAgcmVjdE1hc2suZW5hYmxlZCA9IHRydWU7CiAgICAgICAgcmVjdE1hc2sucGFkZGluZyA9IFZlY3RvcjQuemVybzsKCiAgICAgICAgVHJhbnNmb3JtIGNvbnRlbnRUcmFuc2Zvcm0gPSB2aWV3cG9ydFRyYW5zZm9ybS5GaW5kKCJIaXN0b3J5IENvbnRlbnQiKTsKICAgICAgICBpZiAoY29udGVudFRyYW5zZm9ybSA9PSBudWxsKQogICAgICAgIHsKICAgICAgICAgICAgR2FtZU9iamVjdCBjb250ZW50T2JqZWN0ID0gbmV3IEdhbWVPYmplY3QoIkhpc3RvcnkgQ29udGVudCIsIHR5cGVvZihSZWN0VHJhbnNmb3JtKSk7CiAgICAgICAgICAgIGNvbnRlbnRPYmplY3QubGF5ZXIgPSBoaXN0b3J5UGFuZWwubGF5ZXI7CiAgICAgICAgICAgIGNvbnRlbnRPYmplY3QudHJhbnNmb3JtLlNldFBhcmVudCh2aWV3cG9ydFRyYW5zZm9ybSwgZmFsc2UpOwogICAgICAgICAgICBjb250ZW50VHJhbnNmb3JtID0gY29udGVudE9iamVjdC50cmFuc2Zvcm07CiAgICAgICAgfQoKICAgICAgICBoaXN0b3J5Q29udGVudFJlY3QgPSBjb250ZW50VHJhbnNmb3JtLkdldENvbXBvbmVudDxSZWN0VHJhbnNmb3JtPigpOwogICAgICAgIGhpc3RvcnlDb250ZW50UmVjdC5hbmNob3JNaW4gPSBuZXcgVmVjdG9yMigwZiwgMWYpOwogICAgICAgIGhpc3RvcnlDb250ZW50UmVjdC5hbmNob3JNYXggPSBuZXcgVmVjdG9yMigxZiwgMWYpOwogICAgICAgIGhpc3RvcnlDb250ZW50UmVjdC5waXZvdCA9IG5ldyBWZWN0b3IyKDAuNWYsIDFmKTsKICAgICAgICBoaXN0b3J5Q29udGVudFJlY3QuYW5jaG9yZWRQb3NpdGlvbiA9IFZlY3RvcjIuemVybzsKCiAgICAgICAgVmVydGljYWxMYXlvdXRHcm91cCBsYXlvdXQgPSBjb250ZW50VHJhbnNmb3JtLkdldENvbXBvbmVudDxWZXJ0aWNhbExheW91dEdyb3VwPigpOwogICAgICAgIGlmIChsYXlvdXQgIT0gbnVsbCkKICAgICAgICAgICAgbGF5b3V0LmVuYWJsZWQgPSBmYWxzZTsKICAgICAgICBDb250ZW50U2l6ZUZpdHRlciBjb250ZW50Rml0dGVyID0gY29udGVudFRyYW5zZm9ybS5HZXRDb21wb25lbnQ8Q29udGVudFNpemVGaXR0ZXI+KCk7CiAgICAgICAgaWYgKGNvbnRlbnRGaXR0ZXIgIT0gbnVsbCkKICAgICAgICAgICAgY29udGVudEZpdHRlci5lbmFibGVkID0gZmFsc2U7CgogICAgICAgIFRNUF9UZXh0IGJvdW5kVGV4dCA9IGNvbnRlbnRUcmFuc2Zvcm0uRmluZCgiSGlzdG9yeSBUZXh0Iik/LkdldENvbXBvbmVudDxUTVBfVGV4dD4oKTsKICAgICAgICBpZiAoYm91bmRUZXh0ID09IG51bGwpCiAgICAgICAgICAgIGJvdW5kVGV4dCA9IGhpc3RvcnlUZXh0OwogICAgICAgIGlmIChib3VuZFRleHQgPT0gbnVsbCkKICAgICAgICB7CiAgICAgICAgICAgIEdhbWVPYmplY3QgdGV4dE9iamVjdCA9IG5ldyBHYW1lT2JqZWN0KCJIaXN0b3J5IFRleHQiLAogICAgICAgICAgICAgICAgdHlwZW9mKFJlY3RUcmFuc2Zvcm0pLCB0eXBlb2YoQ2FudmFzUmVuZGVyZXIpLCB0eXBlb2YoVGV4dE1lc2hQcm9VR1VJKSk7CiAgICAgICAgICAgIHRleHRPYmplY3QubGF5ZXIgPSBoaXN0b3J5UGFuZWwubGF5ZXI7CiAgICAgICAgICAgIHRleHRPYmplY3QudHJhbnNmb3JtLlNldFBhcmVudChjb250ZW50VHJhbnNmb3JtLCBmYWxzZSk7CiAgICAgICAgICAgIFRleHRNZXNoUHJvVUdVSSBjcmVhdGVkID0gdGV4dE9iamVjdC5HZXRDb21wb25lbnQ8VGV4dE1lc2hQcm9VR1VJPigpOwogICAgICAgICAgICBjcmVhdGVkLmZvbnQgPSBib2R5VGV4dCAhPSBudWxsID8gYm9keVRleHQuZm9udCA6IG51bGw7CiAgICAgICAgICAgIGNyZWF0ZWQuZm9udFNpemUgPSAyN2Y7CiAgICAgICAgICAgIGNyZWF0ZWQuZm9udFN0eWxlID0gRm9udFN0eWxlcy5Cb2xkOwogICAgICAgICAgICBjcmVhdGVkLmNvbG9yID0gbmV3IENvbG9yKDAuOWYsIDAuOTNmLCAwLjk4Zik7CiAgICAgICAgICAgIGJvdW5kVGV4dCA9IGNyZWF0ZWQ7CiAgICAgICAgfQogICAgICAgIGVsc2UgaWYgKGJvdW5kVGV4dC50cmFuc2Zvcm0ucGFyZW50ICE9IGNvbnRlbnRUcmFuc2Zvcm0pCiAgICAgICAgewogICAgICAgICAgICBib3VuZFRleHQudHJhbnNmb3JtLlNldFBhcmVudChjb250ZW50VHJhbnNmb3JtLCBmYWxzZSk7CiAgICAgICAgfQoKICAgICAgICBoaXN0b3J5VGV4dCA9IGJvdW5kVGV4dDsKICAgICAgICBoaXN0b3J5VGV4dC5nYW1lT2JqZWN0LlNldEFjdGl2ZSh0cnVlKTsKICAgICAgICBoaXN0b3J5VGV4dC5hbGlnbm1lbnQgPSBUZXh0QWxpZ25tZW50T3B0aW9ucy5Ub3BMZWZ0OwogICAgICAgIGhpc3RvcnlUZXh0LnRleHRXcmFwcGluZ01vZGUgPSBUZXh0V3JhcHBpbmdNb2Rlcy5Ob3JtYWw7CiAgICAgICAgaGlzdG9yeVRleHQub3ZlcmZsb3dNb2RlID0gVGV4dE92ZXJmbG93TW9kZXMuT3ZlcmZsb3c7CiAgICAgICAgaGlzdG9yeVRleHQucmF5Y2FzdFRhcmdldCA9IGZhbHNlOwogICAgICAgIGhpc3RvcnlUZXh0Lm1hc2thYmxlID0gdHJ1ZTsKCiAgICAgICAgQ29udGVudFNpemVGaXR0ZXIgdGV4dEZpdHRlciA9IGhpc3RvcnlUZXh0LkdldENvbXBvbmVudDxDb250ZW50U2l6ZUZpdHRlcj4oKTsKICAgICAgICBpZiAodGV4dEZpdHRlciAhPSBudWxsKQogICAgICAgICAgICB0ZXh0Rml0dGVyLmVuYWJsZWQgPSBmYWxzZTsKCiAgICAgICAgZm9yZWFjaCAoVE1QX1RleHQgbGVnYWN5IGluIGhpc3RvcnlQYW5lbC5HZXRDb21wb25lbnRzSW5DaGlsZHJlbjxUTVBfVGV4dD4odHJ1ZSkpCiAgICAgICAgewogICAgICAgICAgICBpZiAobGVnYWN5ID09IGhpc3RvcnlUZXh0KQogICAgICAgICAgICAgICAgY29udGludWU7CiAgICAgICAgICAgIGlmIChsZWdhY3kuZ2FtZU9iamVjdC5uYW1lLlN0YXJ0c1dpdGgoIkhpc3RvcnkgVGV4dCIsIFN0cmluZ0NvbXBhcmlzb24uT3JkaW5hbElnbm9yZUNhc2UpKQogICAgICAgICAgICAgICAgbGVnYWN5LmdhbWVPYmplY3QuU2V0QWN0aXZlKGZhbHNlKTsKICAgICAgICB9CgogICAgICAgIGhpc3RvcnlTY3JvbGwgPSB2aWV3cG9ydFRyYW5zZm9ybS5HZXRDb21wb25lbnQ8U2Nyb2xsUmVjdD4oKTsKICAgICAgICBpZiAoaGlzdG9yeVNjcm9sbCA9PSBudWxsKQogICAgICAgICAgICBoaXN0b3J5U2Nyb2xsID0gdmlld3BvcnRUcmFuc2Zvcm0uZ2FtZU9iamVjdC5BZGRDb21wb25lbnQ8U2Nyb2xsUmVjdD4oKTsKICAgICAgICBoaXN0b3J5U2Nyb2xsLnZpZXdwb3J0ID0gaGlzdG9yeVZpZXdwb3J0UmVjdDsKICAgICAgICBoaXN0b3J5U2Nyb2xsLmNvbnRlbnQgPSBoaXN0b3J5Q29udGVudFJlY3Q7CiAgICAgICAgaGlzdG9yeVNjcm9sbC5ob3Jpem9udGFsID0gZmFsc2U7CiAgICAgICAgaGlzdG9yeVNjcm9sbC52ZXJ0aWNhbCA9IHRydWU7CiAgICAgICAgaGlzdG9yeVNjcm9sbC5tb3ZlbWVudFR5cGUgPSBTY3JvbGxSZWN0Lk1vdmVtZW50VHlwZS5DbGFtcGVkOwogICAgICAgIGhpc3RvcnlTY3JvbGwuc2Nyb2xsU2Vuc2l0aXZpdHkgPSA0NWY7CiAgICAgICAgaGlzdG9yeVNjcm9sbC5pbmVydGlhID0gdHJ1ZTsKICAgICAgICBoaXN0b3J5U2Nyb2xsLmRlY2VsZXJhdGlvblJhdGUgPSAwLjEyZjsKCiAgICAgICAgUmVmcmVzaEhpc3RvcnlMYXlvdXQoKTsKICAgIH0=') "대화 기록 Viewport 마스킹"

        $Director = Replace-CSharpMethod $Director "private void RefreshHistoryLayout()" (Decode-Text 'ICAgIHByaXZhdGUgdm9pZCBSZWZyZXNoSGlzdG9yeUxheW91dCgpCiAgICB7CiAgICAgICAgaWYgKGhpc3RvcnlUZXh0ID09IG51bGwgfHwgaGlzdG9yeVZpZXdwb3J0UmVjdCA9PSBudWxsIHx8IGhpc3RvcnlDb250ZW50UmVjdCA9PSBudWxsKQogICAgICAgICAgICByZXR1cm47CgogICAgICAgIENhbnZhcy5Gb3JjZVVwZGF0ZUNhbnZhc2VzKCk7CiAgICAgICAgZmxvYXQgdmlld3BvcnRXaWR0aCA9IE1hdGhmLk1heCg0ODBmLCBoaXN0b3J5Vmlld3BvcnRSZWN0LnJlY3Qud2lkdGgpOwogICAgICAgIGZsb2F0IHZpZXdwb3J0SGVpZ2h0ID0gTWF0aGYuTWF4KDMyMGYsIGhpc3RvcnlWaWV3cG9ydFJlY3QucmVjdC5oZWlnaHQpOwoKICAgICAgICBjb25zdCBmbG9hdCBzaWRlUGFkZGluZyA9IDMyZjsKICAgICAgICBjb25zdCBmbG9hdCB2ZXJ0aWNhbFBhZGRpbmcgPSAyNGY7CiAgICAgICAgZmxvYXQgdGV4dFdpZHRoID0gTWF0aGYuTWF4KDMyMGYsIHZpZXdwb3J0V2lkdGggLSBzaWRlUGFkZGluZyAqIDJmKTsKICAgICAgICBmbG9hdCBwcmVmZXJyZWRIZWlnaHQgPSBNYXRoZi5NYXgoODBmLAogICAgICAgICAgICBoaXN0b3J5VGV4dC5HZXRQcmVmZXJyZWRWYWx1ZXMoaGlzdG9yeVRleHQudGV4dCwgdGV4dFdpZHRoLCAwZikueSk7CiAgICAgICAgZmxvYXQgY29udGVudEhlaWdodCA9IE1hdGhmLk1heCh2aWV3cG9ydEhlaWdodCwgcHJlZmVycmVkSGVpZ2h0ICsgdmVydGljYWxQYWRkaW5nICogMmYpOwoKICAgICAgICBoaXN0b3J5Q29udGVudFJlY3QuYW5jaG9yTWluID0gbmV3IFZlY3RvcjIoMGYsIDFmKTsKICAgICAgICBoaXN0b3J5Q29udGVudFJlY3QuYW5jaG9yTWF4ID0gbmV3IFZlY3RvcjIoMWYsIDFmKTsKICAgICAgICBoaXN0b3J5Q29udGVudFJlY3QucGl2b3QgPSBuZXcgVmVjdG9yMigwLjVmLCAxZik7CiAgICAgICAgaGlzdG9yeUNvbnRlbnRSZWN0LmFuY2hvcmVkUG9zaXRpb24gPSBWZWN0b3IyLnplcm87CiAgICAgICAgaGlzdG9yeUNvbnRlbnRSZWN0LnNpemVEZWx0YSA9IG5ldyBWZWN0b3IyKDBmLCBjb250ZW50SGVpZ2h0KTsKCiAgICAgICAgUmVjdFRyYW5zZm9ybSB0ZXh0UmVjdCA9IGhpc3RvcnlUZXh0LnJlY3RUcmFuc2Zvcm07CiAgICAgICAgdGV4dFJlY3QuYW5jaG9yTWluID0gbmV3IFZlY3RvcjIoMGYsIDFmKTsKICAgICAgICB0ZXh0UmVjdC5hbmNob3JNYXggPSBuZXcgVmVjdG9yMigxZiwgMWYpOwogICAgICAgIHRleHRSZWN0LnBpdm90ID0gbmV3IFZlY3RvcjIoMC41ZiwgMWYpOwogICAgICAgIHRleHRSZWN0LmFuY2hvcmVkUG9zaXRpb24gPSBuZXcgVmVjdG9yMigwZiwgLXZlcnRpY2FsUGFkZGluZyk7CiAgICAgICAgdGV4dFJlY3Quc2l6ZURlbHRhID0gbmV3IFZlY3RvcjIoLXNpZGVQYWRkaW5nICogMmYsIHByZWZlcnJlZEhlaWdodCk7CgogICAgICAgIGhpc3RvcnlUZXh0LnRleHRXcmFwcGluZ01vZGUgPSBUZXh0V3JhcHBpbmdNb2Rlcy5Ob3JtYWw7CiAgICAgICAgaGlzdG9yeVRleHQub3ZlcmZsb3dNb2RlID0gVGV4dE92ZXJmbG93TW9kZXMuT3ZlcmZsb3c7CiAgICAgICAgaGlzdG9yeVRleHQubWFza2FibGUgPSB0cnVlOwoKICAgICAgICBpZiAoaGlzdG9yeVNjcm9sbCAhPSBudWxsKQogICAgICAgIHsKICAgICAgICAgICAgaGlzdG9yeVNjcm9sbC52aWV3cG9ydCA9IGhpc3RvcnlWaWV3cG9ydFJlY3Q7CiAgICAgICAgICAgIGhpc3RvcnlTY3JvbGwuY29udGVudCA9IGhpc3RvcnlDb250ZW50UmVjdDsKICAgICAgICAgICAgaGlzdG9yeVNjcm9sbC5ob3Jpem9udGFsID0gZmFsc2U7CiAgICAgICAgICAgIGhpc3RvcnlTY3JvbGwudmVydGljYWwgPSB0cnVlOwogICAgICAgICAgICBoaXN0b3J5U2Nyb2xsLm1vdmVtZW50VHlwZSA9IFNjcm9sbFJlY3QuTW92ZW1lbnRUeXBlLkNsYW1wZWQ7CiAgICAgICAgICAgIGhpc3RvcnlTY3JvbGwuU3RvcE1vdmVtZW50KCk7CiAgICAgICAgfQoKICAgICAgICBMYXlvdXRSZWJ1aWxkZXIuRm9yY2VSZWJ1aWxkTGF5b3V0SW1tZWRpYXRlKHRleHRSZWN0KTsKICAgICAgICBMYXlvdXRSZWJ1aWxkZXIuRm9yY2VSZWJ1aWxkTGF5b3V0SW1tZWRpYXRlKGhpc3RvcnlDb250ZW50UmVjdCk7CiAgICAgICAgQ2FudmFzLkZvcmNlVXBkYXRlQ2FudmFzZXMoKTsKICAgIH0=') "대화 기록 스크롤 레이아웃"

        $Director = Replace-CSharpMethod $Director "private void CaptureCheckpointIfNeeded(ScenarioV3Line line)" (Decode-Text 'ICAgIHByaXZhdGUgdm9pZCBDYXB0dXJlQ2hlY2twb2ludElmTmVlZGVkKFNjZW5hcmlvVjNMaW5lIGxpbmUpCiAgICB7CiAgICAgICAgaWYgKCFkYXRhYmFzZS5UcnlHZXRDaGVja3BvaW50TGFiZWwobGluZSwgb3V0IHN0cmluZyBsYWJlbCkpCiAgICAgICAgICAgIHJldHVybjsKICAgICAgICBpZiAoY2hlY2twb2ludHMuQW55KGNoZWNrcG9pbnQgPT4gY2hlY2twb2ludC5kYXkgPT0gZmxvdy5DdXJyZW50RGF5ICYmIGNoZWNrcG9pbnQubGluZUlkID09IGxpbmUuaWQpKQogICAgICAgICAgICByZXR1cm47CgogICAgICAgIGNoZWNrcG9pbnRzLkFkZChuZXcgU2NlbmFyaW9WM0NoZWNrcG9pbnREYXRhCiAgICAgICAgewogICAgICAgICAgICBsYWJlbCA9IGxhYmVsLAogICAgICAgICAgICBzY2VuZUlkID0gYWN0aXZlU2NlbmUuaWQsCiAgICAgICAgICAgIGxpbmVJZCA9IGxpbmUuaWQsCiAgICAgICAgICAgIGxpbmVJbmRleCA9IGFjdGl2ZUxpbmVJbmRleCwKICAgICAgICAgICAgZGF5ID0gZmxvdy5DdXJyZW50RGF5LAogICAgICAgICAgICBob3VyID0gZmxvdy5DdXJyZW50SG91ciwKICAgICAgICAgICAgbG9jYXRpb24gPSBmbG93LkN1cnJlbnRMb2NhdGlvbiwKICAgICAgICAgICAgY2FzaCA9IGZsb3cuVjNCYW5rQ2FzaCwKICAgICAgICAgICAgZGVidCA9IGZsb3cuQ3VycmVudERlYnQsCiAgICAgICAgICAgIGNob2ljZUNvdW50ID0gY2hvaWNlSGlzdG9yeS5Db3VudCwKICAgICAgICAgICAgc3RhdGUgPSBzdGF0ZS5PcmRlckJ5KHBhaXIgPT4gcGFpci5LZXkpCiAgICAgICAgICAgICAgICAuU2VsZWN0KHBhaXIgPT4gbmV3IFNjZW5hcmlvVjNTdGF0ZUVudHJ5IHsga2V5ID0gcGFpci5LZXksIHZhbHVlID0gcGFpci5WYWx1ZSB9KS5Ub0xpc3QoKSwKICAgICAgICAgICAgc2VlblNjZW5lcyA9IHNlZW5TY2VuZXMuT3JkZXJCeSh2YWx1ZSA9PiB2YWx1ZSkuVG9MaXN0KCksCiAgICAgICAgICAgIGRpYWxvZ3VlTG9nID0gbmV3IExpc3Q8c3RyaW5nPihkaWFsb2d1ZUxvZyksCiAgICAgICAgICAgIGNoYXRTbmFwc2hvdCA9IGRpYWxvZ3VlICE9IG51bGwgPyBkaWFsb2d1ZS5DYXB0dXJlU2NlbmFyaW9TbmFwc2hvdCgpIDogc3RyaW5nLkVtcHR5CiAgICAgICAgfSk7CiAgICAgICAgU2F2ZSgpOwogICAgfQ==') "체크포인트 VN·채팅 스냅샷 저장"

        # 새 게임에서만 전체 채팅/알림을 비운다. 분기 복원에서는 위 스냅샷을 사용한다.
        if (-not $Director.Contains("dialogue?.ResetScenarioConversations();")) {
            $Director = Replace-LiteralOnce $Director (Decode-Text 'ICAgICAgICBzdGF0ZVsicmVsYXRpb24uc2VveWVvbiJdID0gIjAiOwogICAgICAgIHN0YXRlWyJyZWxhdGlvbi5tYW5hZ2VyIl0gPSAiMCI7CiAgICAgICAgZmxvdy5WM1Jlc2V0UnVuKDUwMDAwKTs=') (Decode-Text 'ICAgICAgICBzdGF0ZVsicmVsYXRpb24uc2VveWVvbiJdID0gIjAiOwogICAgICAgIHN0YXRlWyJyZWxhdGlvbi5tYW5hZ2VyIl0gPSAiMCI7CiAgICAgICAgZGlhbG9ndWU/LlJlc2V0U2NlbmFyaW9Db252ZXJzYXRpb25zKCk7CiAgICAgICAgbm90aWZpY2F0aW9ucz8uQ2xlYXIoKTsKICAgICAgICBhcHBXaW5kb3c/LkNsb3NlQ3VycmVudEFwcCgpOwogICAgICAgIGZsb3cuVjNSZXNldFJ1big1MDAwMCk7') "새 게임 전체 채팅 초기화"
        }

        Write-Utf8Bom $DirectorPath $Director
    }

    # ================================================================
    # GameFlowManager.cs
    # ================================================================
    $FlowManagerPath = Join-Path $ProjectRoot "Assets\Tablet\Script\GameFlowManager.cs"
    $FlowManager = Normalize-Lf (Read-Utf8 $FlowManagerPath)

    if (-not $FlowManager.Contains("DOBak V15 FINAL")) {
        $FlowManager = Replace-LiteralOnce $FlowManager (Decode-Text 'cHVibGljIHNlYWxlZCBjbGFzcyBHYW1lRmxvd01hbmFnZXIgOiBNb25vQmVoYXZpb3VyCnsKICAgIHB1YmxpYyBzdGF0aWMgR2FtZUZsb3dNYW5hZ2VyIEluc3RhbmNlIHsgZ2V0OyBwcml2YXRlIHNldDsgfQ==') (Decode-Text 'cHVibGljIHNlYWxlZCBjbGFzcyBHYW1lRmxvd01hbmFnZXIgOiBNb25vQmVoYXZpb3VyCnsKICAgIHByaXZhdGUgY29uc3Qgc3RyaW5nIEZpbmFsSG90Zml4TWFya2VyID0gIkRPQmFrIFYxNSBGSU5BTCI7CiAgICBwdWJsaWMgc3RhdGljIEdhbWVGbG93TWFuYWdlciBJbnN0YW5jZSB7IGdldDsgcHJpdmF0ZSBzZXQ7IH0=') "GameFlow 최종 패치 마커"

        $FlowManager = Replace-CSharpMethod $FlowManager "public void TravelTo(string rawLocation)" (Decode-Text 'ICAgIHB1YmxpYyB2b2lkIFRyYXZlbFRvKHN0cmluZyByYXdMb2NhdGlvbikKICAgIHsKICAgICAgICBpZiAoZ2FtZUVuZGVkIHx8IGlzVHJhbnNpdGlvbmluZykKICAgICAgICAgICAgcmV0dXJuOwoKICAgICAgICBzdHJpbmcgbG9jYXRpb24gPSBOb3JtYWxpemVMb2NhdGlvbihyYXdMb2NhdGlvbik7CiAgICAgICAgaW50IHRyYXZlbEhvdXJzID0gR2V0VHJhdmVsSG91cnMobG9jYXRpb24pOwoKICAgICAgICBBdWRpb0NsaXAgdHJhdmVsQ2xpcCA9IGxvY2F0aW9uID09ICLtlZnqtZAiID8gc2Nob29sQXJyaXZhbENsaXAKICAgICAgICAgICAgOiBsb2NhdGlvbiA9PSAi7Lm07Y6YIiA/IGNhZmVBcnJpdmFsQ2xpcAogICAgICAgICAgICA6IG51bGw7CiAgICAgICAgZmxvYXQgdHJhdmVsVm9sdW1lID0gbG9jYXRpb24gPT0gIu2Vmeq1kCIgPyAwLjI4ZiA6IDAuM2Y7CgogICAgICAgIGlmIChsb2NhdGlvbiA9PSAi7ZWZ6rWQIiAmJiAhSXNXZWVrZW5kICYmICFzY2hvb2xEb25lKQogICAgICAgIHsKICAgICAgICAgICAgaWYgKGN1cnJlbnRIb3VyID49IFNjaG9vbEVuZEhvdXIpCiAgICAgICAgICAgIHsKICAgICAgICAgICAgICAgIGFwcFdpbmRvdz8uQ2xvc2VDdXJyZW50QXBwKCk7CiAgICAgICAgICAgICAgICBTaG93RmVlZGJhY2soIuyYpOuKmCDsiJjsl4XsnYAg7J2066+4IOuBneuCrOuLpC4g7KCE64us65CcIOuCtOyaqeydgCDrqZTsi5zsp4Dsl5DshJwg7ZmV7J247ZWY64qUIO2OuOydtCDsoovqsqDri6QuIik7CiAgICAgICAgICAgICAgICBzY2VuYXJpb1YzPy5IYW5kbGVFeHRlcm5hbEFjdGlvbigic2Nob29sX21pc3NlZCIpOwogICAgICAgICAgICAgICAgcmV0dXJuOwogICAgICAgICAgICB9CgogICAgICAgICAgICBpbnQgYXJyaXZhbEhvdXIgPSBjdXJyZW50SG91ciArIHRyYXZlbEhvdXJzOwogICAgICAgICAgICBpZiAoYXJyaXZhbEhvdXIgPCBTY2hvb2xPcGVuaW5nSG91cikKICAgICAgICAgICAgewogICAgICAgICAgICAgICAgYXBwV2luZG93Py5DbG9zZUN1cnJlbnRBcHAoKTsKICAgICAgICAgICAgICAgIFNob3dGZWVkYmFjaygi7ZWZ6rWQ64qUIOyYpOyghCA47Iuc67aA7YSwIOuTpOyWtOqwiCDsiJgg7J6I64ukLiIpOwogICAgICAgICAgICAgICAgcmV0dXJuOwogICAgICAgICAgICB9CgogICAgICAgICAgICBpZiAoYXJyaXZhbEhvdXIgPiBTY2hvb2xBcnJpdmFsRGVhZGxpbmUpCiAgICAgICAgICAgIHsKICAgICAgICAgICAgICAgIGFwcFdpbmRvdz8uQ2xvc2VDdXJyZW50QXBwKCk7CiAgICAgICAgICAgICAgICBBY3Rpb24gY29udGludWVUcmF2ZWwgPSAoKSA9PgogICAgICAgICAgICAgICAgICAgIFN0YXJ0Q29yb3V0aW5lKFRyYXZlbFRyYW5zaXRpb24obG9jYXRpb24sIHRyYXZlbEhvdXJzLCB0cmF2ZWxDbGlwLCB0cmF2ZWxWb2x1bWUpKTsKICAgICAgICAgICAgICAgIGlmIChzY2VuYXJpb1YzICE9IG51bGwgJiYKICAgICAgICAgICAgICAgICAgICBWM1Nob3dEaWFsb2d1ZSgi64KYIiwKICAgICAgICAgICAgICAgICAgICAgICAgIijsnbTrr7gg64qm7JeI7KeA66eMLCDsp4DquIjsnbTrnbzrj4Qg7ZWZ6rWQ7JeQIOqwgOuKlCDtjrjsnbQg64Kr6rKg64ukLikiLAogICAgICAgICAgICAgICAgICAgICAgICBjb250aW51ZVRyYXZlbCkpCiAgICAgICAgICAgICAgICB7CiAgICAgICAgICAgICAgICAgICAgcmV0dXJuOwogICAgICAgICAgICAgICAgfQoKICAgICAgICAgICAgICAgIFNob3dGZWVkYmFjaygi7J2066+4IOuKpuyXiOyngOunjCDsp4DquIjsnbTrnbzrj4Qg7ZWZ6rWQ7JeQIOqwgOuKlCDtjrjsnbQg64Kr6rKg64ukLiIpOwogICAgICAgICAgICAgICAgY29udGludWVUcmF2ZWwoKTsKICAgICAgICAgICAgICAgIHJldHVybjsKICAgICAgICAgICAgfQogICAgICAgIH0KICAgICAgICBlbHNlIGlmIChsb2NhdGlvbiA9PSAi7Lm07Y6YIiAmJiBJc1dlZWtlbmQgJiYgIWpvYkRvbmUpCiAgICAgICAgewogICAgICAgICAgICBpbnQgYXJyaXZhbEhvdXIgPSBjdXJyZW50SG91ciArIHRyYXZlbEhvdXJzOwogICAgICAgICAgICBpZiAoYXJyaXZhbEhvdXIgPCBKb2JTdGFydEhvdXIpCiAgICAgICAgICAgIHsKICAgICAgICAgICAgICAgIGFwcFdpbmRvdz8uQ2xvc2VDdXJyZW50QXBwKCk7CiAgICAgICAgICAgICAgICBTaG93RmVlZGJhY2soIuyVjOuwlOuKlCDsmKTsoIQgOOyLnOyXkCDsi5zsnpHtlZzri6QuIik7CiAgICAgICAgICAgICAgICByZXR1cm47CiAgICAgICAgICAgIH0KCiAgICAgICAgICAgIGlmIChhcnJpdmFsSG91ciA+IEpvYlN0YXJ0SG91cikKICAgICAgICAgICAgewogICAgICAgICAgICAgICAgYXBwV2luZG93Py5DbG9zZUN1cnJlbnRBcHAoKTsKICAgICAgICAgICAgICAgIGlmIChzY2VuYXJpb1YzICE9IG51bGwpCiAgICAgICAgICAgICAgICB7CiAgICAgICAgICAgICAgICAgICAgVjNTaG93RGlhbG9ndWUoIuuCmCIsCiAgICAgICAgICAgICAgICAgICAgICAgICIo7Lac6re8IOyLnOqwhOydhCDsnbTrr7gg64aT7LOk64ukLiDsoJDsnqXri5jqu5gg66i87KCAIOyXsOudve2VmOuKlCDtjrjsnbQg7KKL6rKg64ukLikiLAogICAgICAgICAgICAgICAgICAgICAgICAoKSA9PiBzY2VuYXJpb1YzLkhhbmRsZUV4dGVybmFsQWN0aW9uKCJqb2JfbWlzc2VkIikpOwogICAgICAgICAgICAgICAgfQogICAgICAgICAgICAgICAgZWxzZQogICAgICAgICAgICAgICAgewogICAgICAgICAgICAgICAgICAgIFNob3dGZWVkYmFjaygi7Jik7KCEIDjsi5wg7Lac6re8IOyLnOqwhOydhCDsp4Drgpgg7Jik64qY7J2AIOq3vOustO2VoCDsiJgg7JeG64ukLiIpOwogICAgICAgICAgICAgICAgICAgIFRyaWdnZXJTY2VuYXJpbygiam9iX2xhdGUiKTsKICAgICAgICAgICAgICAgIH0KICAgICAgICAgICAgICAgIHJldHVybjsKICAgICAgICAgICAgfQogICAgICAgIH0KCiAgICAgICAgaWYgKGxvY2F0aW9uID09IGN1cnJlbnRMb2NhdGlvbikKICAgICAgICB7CiAgICAgICAgICAgIFNob3dGZWVkYmFjaygkIu2YhOyerCB7bG9jYXRpb2597JeQIOyeiOyKteuLiOuLpC4iKTsKICAgICAgICAgICAgYXBwV2luZG93Py5DbG9zZUN1cnJlbnRBcHAoKTsKICAgICAgICAgICAgcmV0dXJuOwogICAgICAgIH0KCiAgICAgICAgU3RhcnRDb3JvdXRpbmUoVHJhdmVsVHJhbnNpdGlvbihsb2NhdGlvbiwgdHJhdmVsSG91cnMsIHRyYXZlbENsaXAsIHRyYXZlbFZvbHVtZSkpOwogICAgfQ==') "지각 안내 종료 후 이동"

        $FlowManager = Replace-CSharpMethod $FlowManager "private void StartScenarioGambling()" (Decode-Text 'ICAgIHByaXZhdGUgdm9pZCBTdGFydFNjZW5hcmlvR2FtYmxpbmcoKQogICAgewogICAgICAgIGlmICghZ2FtYmxpbmdVbmxvY2tlZCB8fCBnYW1lRW5kZWQgfHwgaXNUcmFuc2l0aW9uaW5nIHx8IHNjZW5hcmlvVjMgPT0gbnVsbCkKICAgICAgICAgICAgcmV0dXJuOwoKICAgICAgICBQbGF5TG9jYXRpb25TZngodWlCdXR0b25DbGlwLCAwLjIyZik7CgogICAgICAgIGlmIChJc1dlZWtlbmQgJiYgIWpvYkRvbmUpCiAgICAgICAgewogICAgICAgICAgICBWM1Nob3dEaWFsb2d1ZSgi64KYIiwKICAgICAgICAgICAgICAgICIo7Lm07Y6YIOy2nOq3vCDsi5zqsITsnYQg66i87KCAIOunnuy2lOuKlCDtjrjsnbQg7KKL6rKg64ukLikiLAogICAgICAgICAgICAgICAgKCkgPT4gVjNNYXJrQXBwQXR0ZW50aW9uKEFwcFR5cGUuTWFwKSk7CiAgICAgICAgICAgIHJldHVybjsKICAgICAgICB9CgogICAgICAgIGlmICghSXNXZWVrZW5kICYmICFzY2hvb2xEb25lKQogICAgICAgIHsKICAgICAgICAgICAgVjNTaG93RGlhbG9ndWUoIuuCmCIsCiAgICAgICAgICAgICAgICAiKO2Vmeq1kCDsnbzsoJXsnYQg66i87KCAIOyxmeq4sOuKlCDtjrjsnbQg7KKL6rKg64ukLikiLAogICAgICAgICAgICAgICAgKCkgPT4gVjNNYXJrQXBwQXR0ZW50aW9uKEFwcFR5cGUuTWFwKSk7CiAgICAgICAgICAgIHJldHVybjsKICAgICAgICB9CgogICAgICAgIGlmICghSXNXZWVrZW5kICYmIFYzSGFzU3R1ZHlUb2RheSAmJiAhaG9tZXdvcmtEb25lKQogICAgICAgIHsKICAgICAgICAgICAgVjNTaG93RGlhbG9ndWUoIuuCmCIsCiAgICAgICAgICAgICAgICAiKOyYpOuKmCDtlZjquLDroZwg7ZWcIOqzteu2gOu2gO2EsCDsoJXrpqztlZjripQg7Y647J20IOyii+qyoOuLpC4pIiwKICAgICAgICAgICAgICAgICgpID0+IFYzTWFya0FwcEF0dGVudGlvbihBcHBUeXBlLlN0dWR5KSk7CiAgICAgICAgICAgIHJldHVybjsKICAgICAgICB9CgogICAgICAgIGFwcFdpbmRvdz8uQ2xvc2VDdXJyZW50QXBwKCk7CiAgICAgICAgc2NlbmFyaW9WMy5UcnlTdGFydEdhbWJsZUZyb21Ib21lKCk7CiAgICAgICAgUmVmcmVzaEF0dGVudGlvbkRvdHMoKTsKICAgIH0=') "명령형 도박 차단 안내 완화"

        $FlowManager = Replace-CSharpMethod $FlowManager "private void RefreshAttentionDots()" (Decode-Text 'ICAgIHByaXZhdGUgYm9vbCBDYW5Vc2VNYXBOb3coKQogICAgewogICAgICAgIGlmIChnYW1lRW5kZWQgfHwgaXNUcmFuc2l0aW9uaW5nKQogICAgICAgICAgICByZXR1cm4gZmFsc2U7CgogICAgICAgIC8vIOyZuOy2nCDspJHsl5DripQg7KeR7Jy866GcIOuPjOyVhOyYpOuKlCDsnbTrj5nsnbQg6rCA64ql7ZWY64ukLgogICAgICAgIGlmIChjdXJyZW50TG9jYXRpb24gIT0gIuynkSIpCiAgICAgICAgICAgIHJldHVybiB0cnVlOwoKICAgICAgICBpZiAoSXNXZWVrZW5kKQogICAgICAgICAgICByZXR1cm4gIWpvYkRvbmUgJiYgY3VycmVudEhvdXIgPD0gRGF5U3RhcnRIb3VyOwoKICAgICAgICByZXR1cm4gIXNjaG9vbERvbmUgJiYgY3VycmVudEhvdXIgPCBTY2hvb2xFbmRIb3VyOwogICAgfQoKICAgIHByaXZhdGUgdm9pZCBSZWZyZXNoQXR0ZW50aW9uRG90cygpCiAgICB7CiAgICAgICAgZm9yZWFjaCAoS2V5VmFsdWVQYWlyPEFwcFR5cGUsIEdhbWVPYmplY3Q+IHBhaXIgaW4gYXBwQXR0ZW50aW9uRG90cykKICAgICAgICB7CiAgICAgICAgICAgIGJvb2wgdmlzaWJsZSA9IHBhaXIuS2V5IHN3aXRjaAogICAgICAgICAgICB7CiAgICAgICAgICAgICAgICBBcHBUeXBlLkJyb3dzZXIgPT4gZ2FtYmxpbmdVbmxvY2tlZCAmJiAhZ2FtZUVuZGVkLAogICAgICAgICAgICAgICAgQXBwVHlwZS5NZXNzYWdlID0+CiAgICAgICAgICAgICAgICAgICAgKHNjZW5hcmlvVjMgIT0gbnVsbCAmJiBzY2VuYXJpb1YzLkhhc1BlbmRpbmdNZXNzYWdlQWN0aW9uKSB8fAogICAgICAgICAgICAgICAgICAgIChkaWFsb2d1ZU1hbmFnZXIgIT0gbnVsbCAmJiBkaWFsb2d1ZU1hbmFnZXIuVG90YWxVbnJlYWRDb3VudCA+IDApLAogICAgICAgICAgICAgICAgQXBwVHlwZS5TbGVlcCA9PiBDYW5TbGVlcE5vdyAmJiAhc2xlZXBEb25lICYmICFnYW1lRW5kZWQsCiAgICAgICAgICAgICAgICBBcHBUeXBlLk1hcCA9PiBDYW5Vc2VNYXBOb3coKSwKICAgICAgICAgICAgICAgIEFwcFR5cGUuU3R1ZHkgPT4KICAgICAgICAgICAgICAgICAgICAhZ2FtZUVuZGVkICYmICFJc1dlZWtlbmQgJiYgc2Nob29sRG9uZSAmJgogICAgICAgICAgICAgICAgICAgIFYzSGFzU3R1ZHlUb2RheSAmJiAhaG9tZXdvcmtEb25lLAogICAgICAgICAgICAgICAgXyA9PiBwZW5kaW5nQXBwQXR0ZW50aW9uLkNvbnRhaW5zKHBhaXIuS2V5KQogICAgICAgICAgICB9OwoKICAgICAgICAgICAgcGFpci5WYWx1ZS5TZXRBY3RpdmUodmlzaWJsZSk7CiAgICAgICAgfQogICAgfQ==') "앱 빨간 점 실제 사용 가능 조건"

        # 첫 메시지 확인 안내도 명령문 대신 주인공 판단으로 보이게 한다.
        $FlowManager = $FlowManager.Replace(
            'ShowFeedback("엄마와 민재의 메시지를 먼저 확인하자.");',
            'ShowFeedback("엄마와 민재의 메시지부터 확인하는 편이 좋겠다.");'
        )

        Write-Utf8Bom $FlowManagerPath $FlowManager
    }

    # ================================================================
    # DialogueManager.cs
    # ================================================================
    $DialoguePath = Join-Path $ProjectRoot "Assets\Tablet\Script\DialogueManager.cs"
    $Dialogue = Normalize-Lf (Read-Utf8 $DialoguePath)

    if (-not $Dialogue.Contains("DOBak V15 FINAL")) {
        $Dialogue = Replace-LiteralOnce $Dialogue (Decode-Text 'cHVibGljIGNsYXNzIENoYXRNZXNzYWdlRW50cnkKewogICAgcHVibGljIGJvb2wgaXNQbGF5ZXI7CiAgICBwdWJsaWMgc3RyaW5nIHRleHQ7Cn0=') (Decode-Text 'W1N5c3RlbS5TZXJpYWxpemFibGVdCnB1YmxpYyBjbGFzcyBDaGF0TWVzc2FnZUVudHJ5CnsKICAgIHB1YmxpYyBib29sIGlzUGxheWVyOwogICAgcHVibGljIHN0cmluZyB0ZXh0Owp9') "채팅 메시지 JSON 직렬화"

        $Dialogue = Replace-LiteralOnce $Dialogue (Decode-Text 'Ly8gPT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09Ci8vIFvrjIDtmZQg66ek64uI7KCAIOyLnOyKpO2FnF0KLy8gPT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09CnB1YmxpYyBjbGFzcyBEaWFsb2d1ZU1hbmFnZXIgOiBNb25vQmVoYXZpb3Vy') (Decode-Text 'W1N5c3RlbS5TZXJpYWxpemFibGVdCnB1YmxpYyBjbGFzcyBTY2VuYXJpb0NoYXRDaGFubmVsU25hcHNob3QKewogICAgcHVibGljIGludCBzcGVha2VyOwogICAgcHVibGljIHN0cmluZyBzcGVha2VyTmFtZTsKICAgIHB1YmxpYyBzdHJpbmcgbGFzdE1lc3NhZ2U7CiAgICBwdWJsaWMgaW50IHVucmVhZENvdW50OwogICAgcHVibGljIExpc3Q8Q2hhdE1lc3NhZ2VFbnRyeT4gbWVzc2FnZXMgPSBuZXcgTGlzdDxDaGF0TWVzc2FnZUVudHJ5PigpOwp9CgpbU3lzdGVtLlNlcmlhbGl6YWJsZV0KcHVibGljIGNsYXNzIFNjZW5hcmlvQ2hhdFNuYXBzaG90CnsKICAgIHB1YmxpYyBpbnQgY3VycmVudFNwZWFrZXI7CiAgICBwdWJsaWMgaW50IG1vc3RSZWNlbnRTcGVha2VyOwogICAgcHVibGljIExpc3Q8U2NlbmFyaW9DaGF0Q2hhbm5lbFNuYXBzaG90PiBjaGFubmVscyA9IG5ldyBMaXN0PFNjZW5hcmlvQ2hhdENoYW5uZWxTbmFwc2hvdD4oKTsKfQoKLy8gPT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09Ci8vIFvrjIDtmZQg66ek64uI7KCAIOyLnOyKpO2FnF0KLy8gPT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09CnB1YmxpYyBjbGFzcyBEaWFsb2d1ZU1hbmFnZXIgOiBNb25vQmVoYXZpb3Vy') "채팅 체크포인트 DTO"

        $Dialogue = Replace-LiteralOnce $Dialogue (Decode-Text 'cHVibGljIGNsYXNzIERpYWxvZ3VlTWFuYWdlciA6IE1vbm9CZWhhdmlvdXIKewogICAgW0hlYWRlcigiVUkgVG9nZ2xlIFBhbmVsIild') (Decode-Text 'cHVibGljIGNsYXNzIERpYWxvZ3VlTWFuYWdlciA6IE1vbm9CZWhhdmlvdXIKewogICAgcHJpdmF0ZSBjb25zdCBzdHJpbmcgRmluYWxIb3RmaXhNYXJrZXIgPSAiRE9CYWsgVjE1IEZJTkFMIjsKCiAgICBbSGVhZGVyKCJVSSBUb2dnbGUgUGFuZWwiKV0=') "Dialogue 최종 패치 마커"

        $Dialogue = Replace-LiteralOnce $Dialogue (Decode-Text 'ICAgICAgICBpZiAocHJvZmlsZVNsb3RzQnlTcGVha2VyLlRyeUdldFZhbHVlKHNwZWFrZXIsIG91dCBQcm9maWxlU2xvdCBleGlzdGluZykpCiAgICAgICAgICAgIHJldHVybiBleGlzdGluZzs=') (Decode-Text 'ICAgICAgICBpZiAocHJvZmlsZVNsb3RzQnlTcGVha2VyLlRyeUdldFZhbHVlKHNwZWFrZXIsIG91dCBQcm9maWxlU2xvdCBleGlzdGluZykpCiAgICAgICAgewogICAgICAgICAgICBleGlzdGluZy5nYW1lT2JqZWN0LlNldEFjdGl2ZSh0cnVlKTsKICAgICAgICAgICAgZXhpc3RpbmcuQ29uZmlndXJlKHNwZWFrZXIsIHNwZWFrZXJOYW1lLCAoKSA9PiBPcGVuRGlhbG9ndWUoc3BlYWtlcikpOwogICAgICAgICAgICBpZiAoIWNvbnRhY3RPcmRlci5Db250YWlucyhzcGVha2VyKSkKICAgICAgICAgICAgICAgIGNvbnRhY3RPcmRlci5BZGQoc3BlYWtlcik7CiAgICAgICAgICAgIFJlZmxvd1Byb2ZpbGVTbG90cygpOwogICAgICAgICAgICByZXR1cm4gZXhpc3Rpbmc7CiAgICAgICAgfQ==') "복원된 연락처 슬롯 재활성화"

        $SnapshotMethods = (Decode-Text 'ICAgIHB1YmxpYyBzdHJpbmcgQ2FwdHVyZVNjZW5hcmlvU25hcHNob3QoKQogICAgewogICAgICAgIEVuc3VyZUluaXRpYWxpemVkKCk7CiAgICAgICAgdmFyIHNuYXBzaG90ID0gbmV3IFNjZW5hcmlvQ2hhdFNuYXBzaG90CiAgICAgICAgewogICAgICAgICAgICBjdXJyZW50U3BlYWtlciA9IChpbnQpY3VycmVudFNwZWFrZXIsCiAgICAgICAgICAgIG1vc3RSZWNlbnRTcGVha2VyID0gKGludCltb3N0UmVjZW50U3BlYWtlcgogICAgICAgIH07CgogICAgICAgIElFbnVtZXJhYmxlPFNwZWFrZXJUeXBlPiBvcmRlcmVkU3BlYWtlcnMgPSBjb250YWN0T3JkZXIKICAgICAgICAgICAgLkNvbmNhdChjaGFubmVscy5LZXlzLldoZXJlKHNwZWFrZXIgPT4gIWNvbnRhY3RPcmRlci5Db250YWlucyhzcGVha2VyKSkpOwogICAgICAgIGZvcmVhY2ggKFNwZWFrZXJUeXBlIHNwZWFrZXIgaW4gb3JkZXJlZFNwZWFrZXJzKQogICAgICAgIHsKICAgICAgICAgICAgaWYgKCFjaGFubmVscy5UcnlHZXRWYWx1ZShzcGVha2VyLCBvdXQgQ2hhdENoYW5uZWwgY2hhbm5lbCkpCiAgICAgICAgICAgICAgICBjb250aW51ZTsKICAgICAgICAgICAgaWYgKHNwZWFrZXIgPT0gU3BlYWtlclR5cGUuU3RyYW5nZXIgfHwgc3BlYWtlciA9PSBTcGVha2VyVHlwZS5TY2FtbWVyKQogICAgICAgICAgICAgICAgY29udGludWU7CgogICAgICAgICAgICB2YXIgY2hhbm5lbFNuYXBzaG90ID0gbmV3IFNjZW5hcmlvQ2hhdENoYW5uZWxTbmFwc2hvdAogICAgICAgICAgICB7CiAgICAgICAgICAgICAgICBzcGVha2VyID0gKGludClzcGVha2VyLAogICAgICAgICAgICAgICAgc3BlYWtlck5hbWUgPSBjaGFubmVsLnNwZWFrZXJOYW1lLAogICAgICAgICAgICAgICAgbGFzdE1lc3NhZ2UgPSBjaGFubmVsLmxhc3RNZXNzYWdlLAogICAgICAgICAgICAgICAgdW5yZWFkQ291bnQgPSBjaGFubmVsLnVucmVhZENvdW50CiAgICAgICAgICAgIH07CiAgICAgICAgICAgIGZvcmVhY2ggKENoYXRNZXNzYWdlRW50cnkgZW50cnkgaW4gY2hhbm5lbC5tZXNzYWdlSGlzdG9yeSkKICAgICAgICAgICAgewogICAgICAgICAgICAgICAgaWYgKGVudHJ5ID09IG51bGwpCiAgICAgICAgICAgICAgICAgICAgY29udGludWU7CiAgICAgICAgICAgICAgICBjaGFubmVsU25hcHNob3QubWVzc2FnZXMuQWRkKG5ldyBDaGF0TWVzc2FnZUVudHJ5CiAgICAgICAgICAgICAgICB7CiAgICAgICAgICAgICAgICAgICAgaXNQbGF5ZXIgPSBlbnRyeS5pc1BsYXllciwKICAgICAgICAgICAgICAgICAgICB0ZXh0ID0gZW50cnkudGV4dAogICAgICAgICAgICAgICAgfSk7CiAgICAgICAgICAgIH0KICAgICAgICAgICAgc25hcHNob3QuY2hhbm5lbHMuQWRkKGNoYW5uZWxTbmFwc2hvdCk7CiAgICAgICAgfQoKICAgICAgICByZXR1cm4gSnNvblV0aWxpdHkuVG9Kc29uKHNuYXBzaG90KTsKICAgIH0KCiAgICBwdWJsaWMgdm9pZCBSZXN0b3JlU2NlbmFyaW9TbmFwc2hvdChzdHJpbmcganNvbikKICAgIHsKICAgICAgICBFbnN1cmVJbml0aWFsaXplZCgpOwogICAgICAgIGlmIChzdHJpbmcuSXNOdWxsT3JXaGl0ZVNwYWNlKGpzb24pKQogICAgICAgICAgICByZXR1cm47CgogICAgICAgIFNjZW5hcmlvQ2hhdFNuYXBzaG90IHNuYXBzaG90ID0gSnNvblV0aWxpdHkuRnJvbUpzb248U2NlbmFyaW9DaGF0U25hcHNob3Q+KGpzb24pOwogICAgICAgIGlmIChzbmFwc2hvdCA9PSBudWxsKQogICAgICAgICAgICByZXR1cm47CgogICAgICAgIENsZWFyQ2hvaWNlcygpOwogICAgICAgIGZvcmVhY2ggKENoYXRDaGFubmVsIGNoYW5uZWwgaW4gY2hhbm5lbHMuVmFsdWVzKQogICAgICAgIHsKICAgICAgICAgICAgZm9yZWFjaCAoR2FtZU9iamVjdCBidWJibGUgaW4gY2hhbm5lbC5zcGF3bmVkQnViYmxlcykKICAgICAgICAgICAgewogICAgICAgICAgICAgICAgaWYgKGJ1YmJsZSAhPSBudWxsKQogICAgICAgICAgICAgICAgICAgIERlc3Ryb3koYnViYmxlKTsKICAgICAgICAgICAgfQogICAgICAgICAgICBpZiAoY2hhbm5lbC50eXBpbmdCdWJibGUgIT0gbnVsbCkKICAgICAgICAgICAgICAgIERlc3Ryb3koY2hhbm5lbC50eXBpbmdCdWJibGUpOwogICAgICAgICAgICBjaGFubmVsLnR5cGluZ0J1YmJsZSA9IG51bGw7CiAgICAgICAgICAgIGNoYW5uZWwuc3Bhd25lZEJ1YmJsZXMuQ2xlYXIoKTsKICAgICAgICAgICAgY2hhbm5lbC5yZWNlaXZlZE1lc3NhZ2VzLkNsZWFyKCk7CiAgICAgICAgICAgIGNoYW5uZWwubWVzc2FnZUhpc3RvcnkuQ2xlYXIoKTsKICAgICAgICAgICAgY2hhbm5lbC5yZW5kZXJlZFJlY2VpdmVkQ291bnQgPSAwOwogICAgICAgICAgICBjaGFubmVsLmV2ZW50Q2hvaWNlcy5DbGVhcigpOwogICAgICAgICAgICBjaGFubmVsLnBlbmRpbmdDaG9pY2VTZXRzLkNsZWFyKCk7CiAgICAgICAgICAgIGNoYW5uZWwudW5yZWFkQ291bnQgPSAwOwogICAgICAgICAgICBjaGFubmVsLmxhc3RNZXNzYWdlID0gc3RyaW5nLkVtcHR5OwogICAgICAgIH0KCiAgICAgICAgdmFyIHJlc3RvcmVkU3BlYWtlcnMgPSBuZXcgSGFzaFNldDxTcGVha2VyVHlwZT4oKTsKICAgICAgICBjb250YWN0T3JkZXIuQ2xlYXIoKTsKICAgICAgICBmb3JlYWNoIChTY2VuYXJpb0NoYXRDaGFubmVsU25hcHNob3Qgc2F2ZWQgaW4gc25hcHNob3QuY2hhbm5lbHMpCiAgICAgICAgewogICAgICAgICAgICBTcGVha2VyVHlwZSBzcGVha2VyID0gKFNwZWFrZXJUeXBlKXNhdmVkLnNwZWFrZXI7CiAgICAgICAgICAgIGlmIChzcGVha2VyID09IFNwZWFrZXJUeXBlLlN0cmFuZ2VyIHx8IHNwZWFrZXIgPT0gU3BlYWtlclR5cGUuU2NhbW1lcikKICAgICAgICAgICAgICAgIGNvbnRpbnVlOwoKICAgICAgICAgICAgaWYgKCFjaGFubmVscy5UcnlHZXRWYWx1ZShzcGVha2VyLCBvdXQgQ2hhdENoYW5uZWwgY2hhbm5lbCkpCiAgICAgICAgICAgIHsKICAgICAgICAgICAgICAgIGNoYW5uZWwgPSBDcmVhdGVDaGFubmVsKHNwZWFrZXIsIHNhdmVkLnNwZWFrZXJOYW1lKTsKICAgICAgICAgICAgICAgIGNoYW5uZWxzW3NwZWFrZXJdID0gY2hhbm5lbDsKICAgICAgICAgICAgfQoKICAgICAgICAgICAgY2hhbm5lbC5zcGVha2VyVHlwZSA9IHNwZWFrZXI7CiAgICAgICAgICAgIGNoYW5uZWwuc3BlYWtlck5hbWUgPSBHZXRDb250YWN0TmFtZShzcGVha2VyLCBzYXZlZC5zcGVha2VyTmFtZSk7CiAgICAgICAgICAgIGNoYW5uZWwubGFzdE1lc3NhZ2UgPSBzYXZlZC5sYXN0TWVzc2FnZSA/PyBzdHJpbmcuRW1wdHk7CiAgICAgICAgICAgIGNoYW5uZWwudW5yZWFkQ291bnQgPSBNYXRoZi5NYXgoMCwgc2F2ZWQudW5yZWFkQ291bnQpOwogICAgICAgICAgICBjaGFubmVsLm1lc3NhZ2VIaXN0b3J5LkNsZWFyKCk7CiAgICAgICAgICAgIGNoYW5uZWwucmVjZWl2ZWRNZXNzYWdlcy5DbGVhcigpOwoKICAgICAgICAgICAgaWYgKHNhdmVkLm1lc3NhZ2VzICE9IG51bGwpCiAgICAgICAgICAgIHsKICAgICAgICAgICAgICAgIGZvcmVhY2ggKENoYXRNZXNzYWdlRW50cnkgZW50cnkgaW4gc2F2ZWQubWVzc2FnZXMpCiAgICAgICAgICAgICAgICB7CiAgICAgICAgICAgICAgICAgICAgaWYgKGVudHJ5ID09IG51bGwgfHwgc3RyaW5nLklzTnVsbE9yV2hpdGVTcGFjZShlbnRyeS50ZXh0KSkKICAgICAgICAgICAgICAgICAgICAgICAgY29udGludWU7CiAgICAgICAgICAgICAgICAgICAgdmFyIHJlc3RvcmVkID0gbmV3IENoYXRNZXNzYWdlRW50cnkKICAgICAgICAgICAgICAgICAgICB7CiAgICAgICAgICAgICAgICAgICAgICAgIGlzUGxheWVyID0gZW50cnkuaXNQbGF5ZXIsCiAgICAgICAgICAgICAgICAgICAgICAgIHRleHQgPSBlbnRyeS50ZXh0CiAgICAgICAgICAgICAgICAgICAgfTsKICAgICAgICAgICAgICAgICAgICBjaGFubmVsLm1lc3NhZ2VIaXN0b3J5LkFkZChyZXN0b3JlZCk7CiAgICAgICAgICAgICAgICAgICAgaWYgKCFyZXN0b3JlZC5pc1BsYXllcikKICAgICAgICAgICAgICAgICAgICAgICAgY2hhbm5lbC5yZWNlaXZlZE1lc3NhZ2VzLkFkZChyZXN0b3JlZC50ZXh0KTsKICAgICAgICAgICAgICAgIH0KICAgICAgICAgICAgfQoKICAgICAgICAgICAgY2hhbm5lbC5yZW5kZXJlZFJlY2VpdmVkQ291bnQgPSBjaGFubmVsLnJlY2VpdmVkTWVzc2FnZXMuQ291bnQ7CiAgICAgICAgICAgIGlmIChzdHJpbmcuSXNOdWxsT3JXaGl0ZVNwYWNlKGNoYW5uZWwubGFzdE1lc3NhZ2UpICYmIGNoYW5uZWwubWVzc2FnZUhpc3RvcnkuQ291bnQgPiAwKQogICAgICAgICAgICAgICAgY2hhbm5lbC5sYXN0TWVzc2FnZSA9IGNoYW5uZWwubWVzc2FnZUhpc3RvcnlbY2hhbm5lbC5tZXNzYWdlSGlzdG9yeS5Db3VudCAtIDFdLnRleHQ7CgogICAgICAgICAgICByZXN0b3JlZFNwZWFrZXJzLkFkZChzcGVha2VyKTsKICAgICAgICAgICAgRW5zdXJlUHJvZmlsZVNsb3Qoc3BlYWtlciwgY2hhbm5lbC5zcGVha2VyTmFtZSk7CiAgICAgICAgICAgIGlmICghY29udGFjdE9yZGVyLkNvbnRhaW5zKHNwZWFrZXIpKQogICAgICAgICAgICAgICAgY29udGFjdE9yZGVyLkFkZChzcGVha2VyKTsKICAgICAgICB9CgogICAgICAgIGZvcmVhY2ggKFNwZWFrZXJUeXBlIHJlcXVpcmVkIGluIG5ld1tdIHsgU3BlYWtlclR5cGUuRnJpZW5kLCBTcGVha2VyVHlwZS5Nb20gfSkKICAgICAgICB7CiAgICAgICAgICAgIGlmICghY2hhbm5lbHMuVHJ5R2V0VmFsdWUocmVxdWlyZWQsIG91dCBDaGF0Q2hhbm5lbCBjaGFubmVsKSkKICAgICAgICAgICAgewogICAgICAgICAgICAgICAgY2hhbm5lbCA9IENyZWF0ZUNoYW5uZWwocmVxdWlyZWQsIHJlcXVpcmVkID09IFNwZWFrZXJUeXBlLkZyaWVuZCA/ICLrr7zsnqwiIDogIuyXhOuniCIpOwogICAgICAgICAgICAgICAgY2hhbm5lbHNbcmVxdWlyZWRdID0gY2hhbm5lbDsKICAgICAgICAgICAgfQogICAgICAgICAgICByZXN0b3JlZFNwZWFrZXJzLkFkZChyZXF1aXJlZCk7CiAgICAgICAgICAgIEVuc3VyZVByb2ZpbGVTbG90KHJlcXVpcmVkLCBjaGFubmVsLnNwZWFrZXJOYW1lKTsKICAgICAgICAgICAgaWYgKCFjb250YWN0T3JkZXIuQ29udGFpbnMocmVxdWlyZWQpKQogICAgICAgICAgICAgICAgY29udGFjdE9yZGVyLkFkZChyZXF1aXJlZCk7CiAgICAgICAgfQoKICAgICAgICBmb3JlYWNoIChLZXlWYWx1ZVBhaXI8U3BlYWtlclR5cGUsIFByb2ZpbGVTbG90PiBwYWlyIGluIHByb2ZpbGVTbG90c0J5U3BlYWtlcikKICAgICAgICB7CiAgICAgICAgICAgIGlmIChwYWlyLlZhbHVlID09IG51bGwpCiAgICAgICAgICAgICAgICBjb250aW51ZTsKICAgICAgICAgICAgYm9vbCB2aXNpYmxlID0gcmVzdG9yZWRTcGVha2Vycy5Db250YWlucyhwYWlyLktleSk7CiAgICAgICAgICAgIHBhaXIuVmFsdWUuZ2FtZU9iamVjdC5TZXRBY3RpdmUodmlzaWJsZSk7CiAgICAgICAgfQoKICAgICAgICBjdXJyZW50U3BlYWtlciA9IEVudW0uSXNEZWZpbmVkKHR5cGVvZihTcGVha2VyVHlwZSksIHNuYXBzaG90LmN1cnJlbnRTcGVha2VyKQogICAgICAgICAgICA/IChTcGVha2VyVHlwZSlzbmFwc2hvdC5jdXJyZW50U3BlYWtlcgogICAgICAgICAgICA6IFNwZWFrZXJUeXBlLkZyaWVuZDsKICAgICAgICBtb3N0UmVjZW50U3BlYWtlciA9IEVudW0uSXNEZWZpbmVkKHR5cGVvZihTcGVha2VyVHlwZSksIHNuYXBzaG90Lm1vc3RSZWNlbnRTcGVha2VyKQogICAgICAgICAgICA/IChTcGVha2VyVHlwZSlzbmFwc2hvdC5tb3N0UmVjZW50U3BlYWtlcgogICAgICAgICAgICA6IFNwZWFrZXJUeXBlLkZyaWVuZDsKICAgICAgICBwcmVmZXJyZWRTcGVha2VyID0gU3BlYWtlclR5cGUuVW5rbm93bjsKICAgICAgICBzdWJtaXR0ZWRTY2VuYXJpb0FjdGlvbnMuQ2xlYXIoKTsKCiAgICAgICAgaWYgKGRpYWxvZ3VlUGFuZWwgIT0gbnVsbCkKICAgICAgICAgICAgZGlhbG9ndWVQYW5lbC5TZXRBY3RpdmUoZmFsc2UpOwogICAgICAgIFJlZmxvd1Byb2ZpbGVTbG90cygpOwogICAgICAgIFVwZGF0ZUFsbFByb2ZpbGVVSSgpOwogICAgfQo=')

        $Dialogue = Replace-LiteralOnce $Dialogue (Decode-Text 'ICAgIHB1YmxpYyB2b2lkIFJlc2V0U2NlbmFyaW9Db252ZXJzYXRpb25zKCk=') ($SnapshotMethods + '    public void ResetScenarioConversations()' + "`n") "채팅 스냅샷 메서드 삽입"

        $Dialogue = Replace-CSharpMethod $Dialogue "public void ResetScenarioConversations()" (Decode-Text 'ICAgIHB1YmxpYyB2b2lkIFJlc2V0U2NlbmFyaW9Db252ZXJzYXRpb25zKCkKICAgIHsKICAgICAgICBFbnN1cmVJbml0aWFsaXplZCgpOwogICAgICAgIENsZWFyQ2hvaWNlcygpOwoKICAgICAgICBmb3JlYWNoIChDaGF0Q2hhbm5lbCBjaGFubmVsIGluIGNoYW5uZWxzLlZhbHVlcykKICAgICAgICB7CiAgICAgICAgICAgIGZvcmVhY2ggKEdhbWVPYmplY3QgYnViYmxlIGluIGNoYW5uZWwuc3Bhd25lZEJ1YmJsZXMpCiAgICAgICAgICAgIHsKICAgICAgICAgICAgICAgIGlmIChidWJibGUgIT0gbnVsbCkKICAgICAgICAgICAgICAgICAgICBEZXN0cm95KGJ1YmJsZSk7CiAgICAgICAgICAgIH0KICAgICAgICAgICAgaWYgKGNoYW5uZWwudHlwaW5nQnViYmxlICE9IG51bGwpCiAgICAgICAgICAgICAgICBEZXN0cm95KGNoYW5uZWwudHlwaW5nQnViYmxlKTsKCiAgICAgICAgICAgIGNoYW5uZWwudHlwaW5nQnViYmxlID0gbnVsbDsKICAgICAgICAgICAgY2hhbm5lbC5zcGF3bmVkQnViYmxlcy5DbGVhcigpOwogICAgICAgICAgICBjaGFubmVsLnJlY2VpdmVkTWVzc2FnZXMuQ2xlYXIoKTsKICAgICAgICAgICAgY2hhbm5lbC5tZXNzYWdlSGlzdG9yeS5DbGVhcigpOwogICAgICAgICAgICBjaGFubmVsLnJlbmRlcmVkUmVjZWl2ZWRDb3VudCA9IDA7CiAgICAgICAgICAgIGNoYW5uZWwuZXZlbnRDaG9pY2VzLkNsZWFyKCk7CiAgICAgICAgICAgIGNoYW5uZWwucGVuZGluZ0Nob2ljZVNldHMuQ2xlYXIoKTsKICAgICAgICAgICAgY2hhbm5lbC51bnJlYWRDb3VudCA9IDA7CiAgICAgICAgICAgIGNoYW5uZWwubGFzdE1lc3NhZ2UgPSBzdHJpbmcuRW1wdHk7CiAgICAgICAgfQoKICAgICAgICBjb250YWN0T3JkZXIuQ2xlYXIoKTsKICAgICAgICBmb3JlYWNoIChLZXlWYWx1ZVBhaXI8U3BlYWtlclR5cGUsIFByb2ZpbGVTbG90PiBwYWlyIGluIHByb2ZpbGVTbG90c0J5U3BlYWtlcikKICAgICAgICB7CiAgICAgICAgICAgIGlmIChwYWlyLlZhbHVlID09IG51bGwpCiAgICAgICAgICAgICAgICBjb250aW51ZTsKICAgICAgICAgICAgYm9vbCBkZWZhdWx0Q29udGFjdCA9IHBhaXIuS2V5ID09IFNwZWFrZXJUeXBlLkZyaWVuZCB8fCBwYWlyLktleSA9PSBTcGVha2VyVHlwZS5Nb207CiAgICAgICAgICAgIHBhaXIuVmFsdWUuZ2FtZU9iamVjdC5TZXRBY3RpdmUoZGVmYXVsdENvbnRhY3QpOwogICAgICAgICAgICBpZiAoZGVmYXVsdENvbnRhY3QpCiAgICAgICAgICAgICAgICBjb250YWN0T3JkZXIuQWRkKHBhaXIuS2V5KTsKICAgICAgICB9CgogICAgICAgIGN1cnJlbnRTcGVha2VyID0gU3BlYWtlclR5cGUuRnJpZW5kOwogICAgICAgIG1vc3RSZWNlbnRTcGVha2VyID0gU3BlYWtlclR5cGUuRnJpZW5kOwogICAgICAgIHByZWZlcnJlZFNwZWFrZXIgPSBTcGVha2VyVHlwZS5Vbmtub3duOwogICAgICAgIHN1Ym1pdHRlZFNjZW5hcmlvQWN0aW9ucy5DbGVhcigpOwoKICAgICAgICBpZiAoZGlhbG9ndWVQYW5lbCAhPSBudWxsKQogICAgICAgICAgICBkaWFsb2d1ZVBhbmVsLlNldEFjdGl2ZShmYWxzZSk7CiAgICAgICAgUmVmbG93UHJvZmlsZVNsb3RzKCk7CiAgICAgICAgVXBkYXRlQWxsUHJvZmlsZVVJKCk7CiAgICB9') "새 게임 전체 채팅 초기화"

        Write-Utf8Bom $DialoguePath $Dialogue
    }

    # ================================================================
    # BankUI.cs
    # ================================================================
    $BankPath = Join-Path $ProjectRoot "Assets\Junsang\Scripts\Bank\BankUI.cs"
    $Bank = Normalize-Lf (Read-Utf8 $BankPath)

    if ($false) { # V20: BankUI 직접 패치는 전용 런타임 보정으로 대체
        $Bank = Replace-LiteralOnce $Bank (Decode-Text 'dXNpbmcgVW5pdHlFbmdpbmU7') (Decode-Text 'dXNpbmcgU3lzdGVtLkNvbGxlY3Rpb25zOwp1c2luZyBVbml0eUVuZ2luZTs=') "BankUI 코루틴 using"

        $Bank = Replace-LiteralOnce $Bank (Decode-Text 'ICAgIHB1YmxpYyBjbGFzcyBCYW5rVUkgOiBNb25vQmVoYXZpb3VyCiAgICB7CiAgICAgICAgW1NlcmlhbGl6ZUZpZWxkXSBwcml2YXRlIFRNUF9UZXh0IGNhc2hUZXh0Ow==') (Decode-Text 'ICAgIHB1YmxpYyBjbGFzcyBCYW5rVUkgOiBNb25vQmVoYXZpb3VyCiAgICB7CiAgICAgICAgcHJpdmF0ZSBjb25zdCBzdHJpbmcgRmluYWxIb3RmaXhNYXJrZXIgPSAiRE9CYWsgVjE1IEZJTkFMIjsKCiAgICAgICAgW1NlcmlhbGl6ZUZpZWxkXSBwcml2YXRlIFRNUF9UZXh0IGNhc2hUZXh0Ow==') "BankUI 최종 패치 마커"

        $Bank = Replace-LiteralOnce $Bank (Decode-Text 'ICAgICAgICBbU2VyaWFsaXplRmllbGRdIHByaXZhdGUgVHJhbnNhY3Rpb25FbnRyeVVJIGVudHJ5UHJlZmFiOyAvLyDqsbDrnpggMeqxtCDtkZzsi5zsmqkg7ZSE66as7Yy5CgogICAgICAgIHByaXZhdGUgdm9pZCBPbkVuYWJsZSgp') (Decode-Text 'ICAgICAgICBbU2VyaWFsaXplRmllbGRdIHByaXZhdGUgVHJhbnNhY3Rpb25FbnRyeVVJIGVudHJ5UHJlZmFiOyAvLyDqsbDrnpggMeqxtCDtkZzsi5zsmqkg7ZSE66as7Yy5CgogICAgICAgIHByaXZhdGUgU2Nyb2xsUmVjdCBoaXN0b3J5U2Nyb2xsOwogICAgICAgIHByaXZhdGUgQ29yb3V0aW5lIHNjcm9sbFRvcENvcm91dGluZTsKCiAgICAgICAgcHJpdmF0ZSB2b2lkIE9uRW5hYmxlKCk=') "BankUI 스크롤 상태"

        $Bank = Replace-CSharpMethod $Bank "private void RefreshFullList()" (Decode-Text 'ICAgICAgICBwcml2YXRlIHZvaWQgUmVmcmVzaEZ1bGxMaXN0KCkKICAgICAgICB7CiAgICAgICAgICAgIGZvcmVhY2ggKFRyYW5zZm9ybSBjaGlsZCBpbiBlbnRyeUNvbnRhaW5lcikKICAgICAgICAgICAgICAgIERlc3Ryb3koY2hpbGQuZ2FtZU9iamVjdCk7CgogICAgICAgICAgICAvLyDstZzsi6Ag6rGw656Y67aA7YSwIOychOyXkOyEnCDslYTrnpgg7Iic7ISc66GcIOyDneyEse2VnOuLpC4KICAgICAgICAgICAgaW50IHZpc2libGVDb3VudCA9IDA7CiAgICAgICAgICAgIGZvciAoaW50IGluZGV4ID0gQ29pbk1hbmFnZXIuSW5zdGFuY2UuSGlzdG9yeS5Db3VudCAtIDE7IGluZGV4ID49IDA7IGluZGV4LS0pCiAgICAgICAgICAgIHsKICAgICAgICAgICAgICAgIHZhciByZWNvcmQgPSBDb2luTWFuYWdlci5JbnN0YW5jZS5IaXN0b3J5W2luZGV4XTsKICAgICAgICAgICAgICAgIGlmICghSXNWaXNpYmxlQmFua1JlY29yZChyZWNvcmQuc2NvcGUpKQogICAgICAgICAgICAgICAgICAgIGNvbnRpbnVlOwoKICAgICAgICAgICAgICAgIENyZWF0ZUVudHJ5KHJlY29yZCwgZmFsc2UpOwogICAgICAgICAgICAgICAgdmlzaWJsZUNvdW50Kys7CiAgICAgICAgICAgIH0KCiAgICAgICAgICAgIGlmICh2aXNpYmxlQ291bnQgPT0gMCkKICAgICAgICAgICAgICAgIENyZWF0ZUVtcHR5U3RhdGUoKTsKCiAgICAgICAgICAgIFNjcm9sbFRvTmV3ZXN0KCk7CiAgICAgICAgfQ==') "거래 내역 최신순 전체 갱신"

        $Bank = Replace-CSharpMethod $Bank "private void HandleTransactionAdded(TransactionRecord record)" (Decode-Text 'ICAgICAgICBwcml2YXRlIHZvaWQgSGFuZGxlVHJhbnNhY3Rpb25BZGRlZChUcmFuc2FjdGlvblJlY29yZCByZWNvcmQpCiAgICAgICAgewogICAgICAgICAgICBpZiAoIUlzVmlzaWJsZUJhbmtSZWNvcmQocmVjb3JkLnNjb3BlKSkKICAgICAgICAgICAgICAgIHJldHVybjsKCiAgICAgICAgICAgIFJlbW92ZUVtcHR5U3RhdGUoKTsKICAgICAgICAgICAgQ3JlYXRlRW50cnkocmVjb3JkLCB0cnVlKTsKICAgICAgICAgICAgU2Nyb2xsVG9OZXdlc3QoKTsKICAgICAgICB9') "새 거래 최상단 배치"

        $Bank = Replace-LiteralOnce $Bank (Decode-Text 'ICAgICAgICAgICAgU2Nyb2xsUmVjdCBzY3JvbGwgPSBlbnRyeUNvbnRhaW5lci5HZXRDb21wb25lbnRJblBhcmVudDxTY3JvbGxSZWN0Pih0cnVlKTsKICAgICAgICAgICAgaWYgKHNjcm9sbCAhPSBudWxsKQogICAgICAgICAgICB7') (Decode-Text 'ICAgICAgICAgICAgU2Nyb2xsUmVjdCBzY3JvbGwgPSBlbnRyeUNvbnRhaW5lci5HZXRDb21wb25lbnRJblBhcmVudDxTY3JvbGxSZWN0Pih0cnVlKTsKICAgICAgICAgICAgaWYgKHNjcm9sbCAhPSBudWxsKQogICAgICAgICAgICB7CiAgICAgICAgICAgICAgICBoaXN0b3J5U2Nyb2xsID0gc2Nyb2xsOw==') "거래 ScrollRect 저장"

        $Bank = Replace-LiteralOnce $Bank (Decode-Text 'ICAgICAgICAgICAgICAgIGlmIChzY3JvbGwudmlld3BvcnQgIT0gbnVsbCkKICAgICAgICAgICAgICAgIHsKICAgICAgICAgICAgICAgICAgICBJbWFnZSB2aWV3cG9ydEltYWdlID0gc2Nyb2xsLnZpZXdwb3J0LkdldENvbXBvbmVudDxJbWFnZT4oKTsKICAgICAgICAgICAgICAgICAgICBpZiAodmlld3BvcnRJbWFnZSAhPSBudWxsKQogICAgICAgICAgICAgICAgICAgICAgICB2aWV3cG9ydEltYWdlLnJheWNhc3RUYXJnZXQgPSB0cnVlOwogICAgICAgICAgICAgICAgfQ==') (Decode-Text 'ICAgICAgICAgICAgICAgIGlmIChzY3JvbGwudmlld3BvcnQgIT0gbnVsbCkKICAgICAgICAgICAgICAgIHsKICAgICAgICAgICAgICAgICAgICBJbWFnZSB2aWV3cG9ydEltYWdlID0gc2Nyb2xsLnZpZXdwb3J0LkdldENvbXBvbmVudDxJbWFnZT4oKTsKICAgICAgICAgICAgICAgICAgICBpZiAodmlld3BvcnRJbWFnZSA9PSBudWxsKQogICAgICAgICAgICAgICAgICAgICAgICB2aWV3cG9ydEltYWdlID0gc2Nyb2xsLnZpZXdwb3J0LmdhbWVPYmplY3QuQWRkQ29tcG9uZW50PEltYWdlPigpOwogICAgICAgICAgICAgICAgICAgIGlmICh2aWV3cG9ydEltYWdlLmNvbG9yLmEgPD0gMGYpCiAgICAgICAgICAgICAgICAgICAgICAgIHZpZXdwb3J0SW1hZ2UuY29sb3IgPSBuZXcgQ29sb3IoMWYsIDFmLCAxZiwgMC4wMDFmKTsKICAgICAgICAgICAgICAgICAgICB2aWV3cG9ydEltYWdlLnJheWNhc3RUYXJnZXQgPSB0cnVlOwoKICAgICAgICAgICAgICAgICAgICBNYXNrIGxlZ2FjeU1hc2sgPSBzY3JvbGwudmlld3BvcnQuR2V0Q29tcG9uZW50PE1hc2s+KCk7CiAgICAgICAgICAgICAgICAgICAgaWYgKGxlZ2FjeU1hc2sgIT0gbnVsbCkKICAgICAgICAgICAgICAgICAgICAgICAgbGVnYWN5TWFzay5lbmFibGVkID0gZmFsc2U7CiAgICAgICAgICAgICAgICAgICAgUmVjdE1hc2syRCByZWN0TWFzayA9IHNjcm9sbC52aWV3cG9ydC5HZXRDb21wb25lbnQ8UmVjdE1hc2syRD4oKTsKICAgICAgICAgICAgICAgICAgICBpZiAocmVjdE1hc2sgPT0gbnVsbCkKICAgICAgICAgICAgICAgICAgICAgICAgcmVjdE1hc2sgPSBzY3JvbGwudmlld3BvcnQuZ2FtZU9iamVjdC5BZGRDb21wb25lbnQ8UmVjdE1hc2syRD4oKTsKICAgICAgICAgICAgICAgICAgICByZWN0TWFzay5lbmFibGVkID0gdHJ1ZTsKICAgICAgICAgICAgICAgICAgICByZWN0TWFzay5wYWRkaW5nID0gVmVjdG9yNC56ZXJvOwogICAgICAgICAgICAgICAgfQ==') "거래 내역 Viewport 마스크"

        $BankHelpers = (Decode-Text 'ICAgICAgICBwcml2YXRlIHZvaWQgU2Nyb2xsVG9OZXdlc3QoKQogICAgICAgIHsKICAgICAgICAgICAgaWYgKHNjcm9sbFRvcENvcm91dGluZSAhPSBudWxsKQogICAgICAgICAgICAgICAgU3RvcENvcm91dGluZShzY3JvbGxUb3BDb3JvdXRpbmUpOwogICAgICAgICAgICBzY3JvbGxUb3BDb3JvdXRpbmUgPSBTdGFydENvcm91dGluZShTY3JvbGxUb05ld2VzdE5leHRGcmFtZXMoKSk7CiAgICAgICAgfQoKICAgICAgICBwcml2YXRlIElFbnVtZXJhdG9yIFNjcm9sbFRvTmV3ZXN0TmV4dEZyYW1lcygpCiAgICAgICAgewogICAgICAgICAgICB5aWVsZCByZXR1cm4gbnVsbDsKICAgICAgICAgICAgQ2FudmFzLkZvcmNlVXBkYXRlQ2FudmFzZXMoKTsKICAgICAgICAgICAgaWYgKGVudHJ5Q29udGFpbmVyIGlzIFJlY3RUcmFuc2Zvcm0gY29udGVudCkKICAgICAgICAgICAgICAgIExheW91dFJlYnVpbGRlci5Gb3JjZVJlYnVpbGRMYXlvdXRJbW1lZGlhdGUoY29udGVudCk7CgogICAgICAgICAgICBpZiAoaGlzdG9yeVNjcm9sbCAhPSBudWxsKQogICAgICAgICAgICB7CiAgICAgICAgICAgICAgICBoaXN0b3J5U2Nyb2xsLlN0b3BNb3ZlbWVudCgpOwogICAgICAgICAgICAgICAgaGlzdG9yeVNjcm9sbC52ZWxvY2l0eSA9IFZlY3RvcjIuemVybzsKICAgICAgICAgICAgICAgIGhpc3RvcnlTY3JvbGwudmVydGljYWxOb3JtYWxpemVkUG9zaXRpb24gPSAxZjsKICAgICAgICAgICAgfQoKICAgICAgICAgICAgeWllbGQgcmV0dXJuIG51bGw7CiAgICAgICAgICAgIENhbnZhcy5Gb3JjZVVwZGF0ZUNhbnZhc2VzKCk7CiAgICAgICAgICAgIGlmIChoaXN0b3J5U2Nyb2xsICE9IG51bGwpCiAgICAgICAgICAgIHsKICAgICAgICAgICAgICAgIGhpc3RvcnlTY3JvbGwuU3RvcE1vdmVtZW50KCk7CiAgICAgICAgICAgICAgICBoaXN0b3J5U2Nyb2xsLnZlbG9jaXR5ID0gVmVjdG9yMi56ZXJvOwogICAgICAgICAgICAgICAgaGlzdG9yeVNjcm9sbC52ZXJ0aWNhbE5vcm1hbGl6ZWRQb3NpdGlvbiA9IDFmOwogICAgICAgICAgICB9CiAgICAgICAgICAgIHNjcm9sbFRvcENvcm91dGluZSA9IG51bGw7CiAgICAgICAgfQo=')

        $Bank = Replace-LiteralOnce $Bank (Decode-Text 'ICAgICAgICBwdWJsaWMgdm9pZCBBcHBseVZpc3VhbERlc2lnbigp') ($BankHelpers + '        public void ApplyVisualDesign()' + "`n") "거래 내역 상단 고정 메서드"

        Write-Utf8Bom $BankPath $Bank
    }

    # ================================================================
    # ScenarioV3.csv
    # ================================================================
    $ScenarioPath = Join-Path $ProjectRoot "Assets\Resources\ScenarioV3.csv"
    $ScenarioRows = @(Import-Csv -LiteralPath $ScenarioPath)
    $ScenarioHeaders = @($ScenarioRows[0].PSObject.Properties.Name)

    # 재실행 시 새로 추가한 행을 먼저 제거하고 동일한 최종본으로 다시 만든다.
    $GeneratedLineIds = @(
        "d10_seojun_repay_choice_01",
        "d10_minjae_repay_choice_01",
        "d10_minjae_repay_router_01",
        "d10_minjae_repaid_01",
        "d10_minjae_repaid_message_01",
        "d10_minjae_cannot_repay_01",
        "d10_minjae_delay_thought_01",
        "d10_minjae_delay_message_01",
        "gamble_7_01",
        "gamble_7_02",
        "gamble_8_01",
        "gamble_8_02"
    )
    $RemoveLineIds = @(
        "sys_borrow_late_morning_01",
        "sys_borrow_late_morning_02",
        "borrow_morning_cue_01",
        "borrow_prepare_mom_01",
        "borrow_prepare_seojun_01",
        "minjae_loan_rejected_02",
        "d10_minjae_debt_03"
    ) + $GeneratedLineIds
    $ScenarioRows = @($ScenarioRows | Where-Object { $RemoveLineIds -notcontains $_.line_id })

    $row = Get-OneRow $ScenarioRows "sys_late_gamble_morning_02"
    $row.text = "벌써 오전 열 시다. 오늘 아침 일정은 이미 놓쳤다."

    $row = Get-OneRow $ScenarioRows "d1_intro_03"
    $row.speaker = "Protagonist"
    $row.contact = "나"
    $row.text = "지도 앱에서 학교를 선택하면 출발할 수 있겠다."

    $row = Get-OneRow $ScenarioRows "d5_job_02"
    $row.text = "오늘 번 오만 원만큼 수리비에 가까워졌다. 하루를 꼬박 일해서 번 돈이라는 게 숫자로 보니 더 또렷했다."

    $row = Get-OneRow $ScenarioRows "d5_evening_02"
    $row.text = "몸은 무겁지만 오늘 해야 할 일은 끝냈다. 이제 씻고 쉬자."

    $row = Get-OneRow $ScenarioRows "gamble_no_funds_02"
    $row.text = "계좌 잔액은 0원이다. 더 하려면 누군가에게 돈을 빌려야 한다. 돈을 구할지, 여기서 멈출지 정해야 한다."
    $row.choice_a_text = "돈을 빌릴 방법을 찾는다"

    $row = Get-OneRow $ScenarioRows "borrow_defer_night_01"
    $row.text = "이 시간에 돈을 빌려 달라고 연락하긴 늦었다. 오늘은 자고, 아침에 부탁할 사람을 정하자."

    $row = Get-OneRow $ScenarioRows "borrow_choice_01"
    $row.arc = "main"
    $row.delivery = "overlay"
    $row.text = "어젯밤에는 시간이 늦어 연락하지 못했다. 누구에게 부탁할까."
    $row.choice_a_next = ""
    $row.choice_b_next = ""
    $row.purpose = "다음 날 아침 태블릿 홈 화면 위에서 차용 대상을 바로 고른다."

    $row = Get-OneRow $ScenarioRows "minjae_loan_rejected_01"
    $row.portrait = "minjae_angry"
    $row.text = "그래. 마음 바뀌면 말해. 돈 필요해지면 그때 연락하고."
    $row.purpose = "거절해도 실제 차용 전까지 민재의 제안은 다시 발생할 수 있다."

    $row = Get-OneRow $ScenarioRows "d10_seojun_followup_01"
    $row.condition = "borrowed.seojun=true;debt_owner=seojun;debt>0"
    $row.choice_a_id = ""
    $row.choice_a_text = ""
    $row.choice_a_effects = ""
    $row.choice_a_next = ""
    $row.choice_b_id = ""
    $row.choice_b_text = ""
    $row.choice_b_effects = ""
    $row.choice_b_next = ""
    $row.auto_next = "d10_seojun_repay_choice"
    $row.purpose = "서준 메시지를 읽은 뒤 같은 채팅 화면 위에서 상환 선택 다이얼로그로 이어진다."

    $row = Get-OneRow $ScenarioRows "d10_seojun_followup_02"
    $row.text = "미루겠다고 쓰려니 손이 멈췄다. 그래도 답장까지 피할 수는 없다."

    $row = Get-OneRow $ScenarioRows "d10_seojun_repaid_01"
    $row.text = "약속을 더 미루기 전에 지금 보낼 수 있는 돈부터 보내자."

    $row = Get-OneRow $ScenarioRows "d10_seojun_cannot_repay_01"
    $row.text = "갚고 싶지만 지금은 통장에 보낼 돈이 없다. 결국 조금만 더 기다려 달라고 해야겠다."

    $row = Get-OneRow $ScenarioRows "d10_minjae_debt_01"
    $row.text = "주말 알바비 들어오면 내 돈부터 갚을 거지?"

    $row = Get-OneRow $ScenarioRows "d10_minjae_debt_02"
    $row.text = "상담이든 뭐든 네가 알아서 하고, 지금 보낼 수 있으면 먼저 보내."
    $row.auto_next = "d10_minjae_repay_choice"
    $row.purpose = "민재의 상환 압박 뒤 같은 채팅 화면에서 실제 상환 여부를 고른다."

    $row = Get-OneRow $ScenarioRows "d14_no_help_messages_03"
    $row.delivery = "overlay"
    $row.text = "민재를 차단하고 도박 링크를 지우면 끝날 일인데, 손가락이 화면 위에서 움직이지 않았다."
    $row.purpose = "민재 메시지 화면 위 독백으로 차단 대상과 망설임을 명확히 한다."

    $row = Get-OneRow $ScenarioRows "d14_recovery_minjae_02"
    $row.portrait = "minjae_angry"
    $row.text = "됐고, 끊든 말든 네 사정이야. 빌린 오만 원이나 약속한 날짜에 제대로 갚아."
    $row.enter_effects = ""
    $row.purpose = "민재는 반성하거나 관계를 회복하지 않고 채무 상환만 요구한다."

    $row = Get-OneRow $ScenarioRows "d14_recovery_minjae_03"
    $row.text = "알겠어. 갚는 날짜부터 정해서 알려줄게. 네가 보낸 링크는 이제 열지 않을 거야."
    $row.purpose = "주인공은 상환 책임을 인정하되 민재의 도박 권유에는 선을 긋는다."

    $row = Get-OneRow $ScenarioRows "d14_prevented_school_01"
    $row.portrait = "minjae_angry"

    $row = Get-OneRow $ScenarioRows "d14_prevented_school_03"
    $row.portrait = "minjae_angry"
    $row.text = "그래, 됐어. 나중에 아쉽다고 해도 난 모른다."
    $row.purpose = "민재가 반성하지 않은 채 압박에서 물러난다."

    $row = Get-OneRow $ScenarioRows "bedtime_cue_01"
    $row.text = "오늘 할 일은 끝났다. 이제 취침 앱으로 하루를 마무리하면 되겠다."

    $row = Get-OneRow $ScenarioRows "gamble_repeat_loss_01"
    $row.purpose = "9회 이후 재접속은 다시 전액 손실 흐름으로 고정한다."

    # 서준 상환 선택: 채팅 버튼이 아니라 열린 메시지 화면 위의 다이얼로그
    $ScenarioRows += New-RowFromHeaders $ScenarioHeaders @{
        schema_version = "4.0"; scene_id = "d10_seojun_repay_choice"; line_id = "d10_seojun_repay_choice_01";
        arc = "debt"; day = "10"; time_window = "morning"; trigger = ""; condition = "debt_owner=seojun;debt>0";
        priority = "131"; once_scope = "game"; sequence = "1"; speaker = "Protagonist"; contact = "나";
        delivery = "overlay"; portrait = "";
        text = "서준에게 뭐라고 답해야 할까.";
        choice_a_id = "d10_repay_now"; choice_a_text = "갚을 수 있는 만큼 먼저 갚는다";
        choice_a_effects = "repay:available=seojun|relation.seojun:add=1";
        choice_a_next = "d10_seojun_repay_router";
        choice_b_id = "d10_delay_repay"; choice_b_text = "조금만 더 기다려 달라고 한다";
        choice_b_effects = "relation.seojun:add=-1"; choice_b_next = "d10_seojun_delay_thought";
        enter_effects = ""; auto_next = "";
        purpose = "서준 채팅 화면을 닫지 않고 다이얼로그 선택지를 표시한다."
    }

    # 민재 상환 선택과 결과
    $ScenarioRows += New-RowFromHeaders $ScenarioHeaders @{
        schema_version = "4.0"; scene_id = "d10_minjae_repay_choice"; line_id = "d10_minjae_repay_choice_01";
        arc = "debt"; day = "10"; time_window = "morning"; trigger = ""; condition = "debt_owner=minjae;debt>0";
        priority = "132"; once_scope = "game"; sequence = "1"; speaker = "Protagonist"; contact = "나";
        delivery = "overlay"; portrait = "";
        text = "민재 말대로 다시 도박할 생각은 없다. 빌린 돈을 지금 얼마나 갚을 수 있는지부터 정하자.";
        choice_a_id = "d10_minjae_repay_now"; choice_a_text = "갚을 수 있는 만큼 먼저 갚는다";
        choice_a_effects = "repay:available=minjae"; choice_a_next = "d10_minjae_repay_router";
        choice_b_id = "d10_minjae_delay_repay"; choice_b_text = "조금만 더 기다려 달라고 한다";
        choice_b_effects = "relation.minjae:add=-1"; choice_b_next = "d10_minjae_delay_thought";
        enter_effects = ""; auto_next = "";
        purpose = "민재 채팅 화면에서 상환 여부를 다이얼로그로 고른다."
    }
    $ScenarioRows += New-RowFromHeaders $ScenarioHeaders @{
        schema_version = "4.0"; scene_id = "d10_minjae_repay_router"; line_id = "d10_minjae_repay_router_01";
        arc = "debt"; day = "10"; time_window = "morning"; trigger = ""; condition = "";
        priority = "132"; once_scope = "game"; sequence = "1"; speaker = "System"; contact = "";
        delivery = "router"; portrait = ""; text = "";
        enter_effects = "route:d10_minjae_repaid if last_repayment>0 else d10_minjae_cannot_repay";
        auto_next = ""; purpose = "실제 상환액 유무에 따라 민재 답장을 분기한다."
    }
    $ScenarioRows += New-RowFromHeaders $ScenarioHeaders @{
        schema_version = "4.0"; scene_id = "d10_minjae_repaid"; line_id = "d10_minjae_repaid_01";
        arc = "debt"; day = "10"; time_window = "morning"; trigger = ""; condition = "";
        priority = "132"; once_scope = "game"; sequence = "1"; speaker = "Protagonist"; contact = "나";
        delivery = "overlay"; portrait = ""; text = "민재가 뭐라고 하든 빌린 돈은 정리해야 한다. 지금 보낼 수 있는 돈부터 갚자.";
        enter_effects = ""; auto_next = "d10_minjae_repaid_message";
        purpose = "민재 채팅 화면 위에서 상환 결심을 보여 준다."
    }
    $ScenarioRows += New-RowFromHeaders $ScenarioHeaders @{
        schema_version = "4.0"; scene_id = "d10_minjae_repaid_message"; line_id = "d10_minjae_repaid_message_01";
        arc = "debt"; day = "10"; time_window = "morning"; trigger = ""; condition = "";
        priority = "132"; once_scope = "game"; sequence = "1"; speaker = "Protagonist"; contact = "민재";
        delivery = "message"; portrait = ""; text = "지금 가진 돈부터 보냈어. 남은 금액도 날짜를 정해서 갚을게.";
        enter_effects = ""; auto_next = "";
        purpose = "실제 상환 뒤 민재에게 보낼 답장만 채팅 기록에 남긴다."
    }
    $ScenarioRows += New-RowFromHeaders $ScenarioHeaders @{
        schema_version = "4.0"; scene_id = "d10_minjae_cannot_repay"; line_id = "d10_minjae_cannot_repay_01";
        arc = "debt"; day = "10"; time_window = "morning"; trigger = ""; condition = "";
        priority = "132"; once_scope = "game"; sequence = "1"; speaker = "Protagonist"; contact = "나";
        delivery = "overlay"; portrait = ""; text = "갚고 싶지만 지금은 통장에 보낼 돈이 없다. 주말 알바비가 들어오면 먼저 갚겠다고 해야겠다.";
        enter_effects = ""; auto_next = "d10_minjae_delay_message";
        purpose = "잔액이 없으면 거짓 상환 메시지 없이 연기 답장으로 합류한다."
    }
    $ScenarioRows += New-RowFromHeaders $ScenarioHeaders @{
        schema_version = "4.0"; scene_id = "d10_minjae_delay_thought"; line_id = "d10_minjae_delay_thought_01";
        arc = "debt"; day = "10"; time_window = "morning"; trigger = ""; condition = "";
        priority = "132"; once_scope = "game"; sequence = "1"; speaker = "Protagonist"; contact = "나";
        delivery = "overlay"; portrait = ""; text = "또 미루는 말부터 쓰게 됐다. 그래도 답장을 피하면 독촉만 더 거세질 것 같다.";
        enter_effects = ""; auto_next = "d10_minjae_delay_message";
        purpose = "상환 연기를 고른 심리를 메시지 화면 위에서 보여 준다."
    }
    $ScenarioRows += New-RowFromHeaders $ScenarioHeaders @{
        schema_version = "4.0"; scene_id = "d10_minjae_delay_message"; line_id = "d10_minjae_delay_message_01";
        arc = "debt"; day = "10"; time_window = "morning"; trigger = ""; condition = "";
        priority = "132"; once_scope = "game"; sequence = "1"; speaker = "Protagonist"; contact = "민재";
        delivery = "message"; portrait = ""; text = "지금은 보낼 돈이 없어. 주말 알바비가 들어오면 먼저 갚을게.";
        enter_effects = ""; auto_next = "";
        purpose = "민재에게 실제로 보낸 상환 연기 답장만 채팅 기록에 남긴다."
    }

    # 전액 손실 뒤 작은 이익 1회와 더 큰 손실 1회
    $ScenarioRows += New-RowFromHeaders $ScenarioHeaders @{
        schema_version = "4.0"; scene_id = "gamble_7"; line_id = "gamble_7_01";
        arc = "gambling"; day = "1..14"; time_window = "cinematic"; trigger = "gamble_7"; condition = "";
        priority = "300"; once_scope = "day"; sequence = "1"; speaker = "Narrator"; contact = "";
        delivery = "cinematic"; portrait = "";
        text = "다시 넣은 돈에서 20,000원이 늘었다. 바닥까지 갔던 잔액이 오르자 방금 전 손실이 잠깐 잊혔다.";
        enter_effects = "clock:add=120|cash:add=20000|temptation:add=1"; auto_next = "";
        purpose = "전액 손실 뒤 작은 적중이 다시 확신을 만드는 과정을 보여 준다."
    }
    $ScenarioRows += New-RowFromHeaders $ScenarioHeaders @{
        schema_version = "4.0"; scene_id = "gamble_7"; line_id = "gamble_7_02";
        arc = "gambling"; day = "1..14"; time_window = "cinematic"; trigger = "gamble_7"; condition = "";
        priority = "300"; once_scope = "day"; sequence = "2"; speaker = "Protagonist"; contact = "나";
        delivery = "dialogue"; portrait = "";
        text = "한 번만 더 맞으면 이번에는 정말 복구할 수 있을 것 같았다.";
        enter_effects = ""; auto_next = "";
        purpose = "작은 수익을 다음 도박의 근거로 오해한다."
    }
    $ScenarioRows += New-RowFromHeaders $ScenarioHeaders @{
        schema_version = "4.0"; scene_id = "gamble_8"; line_id = "gamble_8_01";
        arc = "gambling"; day = "1..14"; time_window = "cinematic"; trigger = "gamble_8"; condition = "";
        priority = "300"; once_scope = "day"; sequence = "1"; speaker = "Narrator"; contact = "";
        delivery = "cinematic"; portrait = "";
        text = "그 확신을 따라 금액을 키웠지만 결과는 40,000원 손실이었다. 남아 있던 돈도 함께 줄었다.";
        enter_effects = "clock:add=120|cash:add=-40000|temptation:add=2"; auto_next = "";
        purpose = "작은 적중 뒤 더 큰 금액을 걸어 손실이 커지는 흐름을 보여 준다."
    }
    $ScenarioRows += New-RowFromHeaders $ScenarioHeaders @{
        schema_version = "4.0"; scene_id = "gamble_8"; line_id = "gamble_8_02";
        arc = "gambling"; day = "1..14"; time_window = "cinematic"; trigger = "gamble_8"; condition = "";
        priority = "300"; once_scope = "day"; sequence = "2"; speaker = "Protagonist"; contact = "나";
        delivery = "dialogue"; portrait = "";
        text = "방금 번 돈보다 더 크게 잃었다. 작은 적중 하나가 또 다음 판을 누르게 만들었다.";
        enter_effects = ""; auto_next = "";
        purpose = "간헐적 보상이 플레이 시간을 늘리는 심리를 정리한다."
    }

    Write-CsvUtf8 $ScenarioPath $ScenarioRows

    # ================================================================
    # ScenarioV3Flow.csv
    # ================================================================
    $ScenarioFlowPath = Join-Path $ProjectRoot "Assets\Resources\ScenarioV3Flow.csv"
    $ScenarioFlowRows = @(Import-Csv -LiteralPath $ScenarioFlowPath)
    $ScenarioFlowHeaders = @($ScenarioFlowRows[0].PSObject.Properties.Name)

    $RemoveFlowScenes = @(
        "sys_borrow_late_morning",
        "borrow_morning_cue",
        "borrow_prepare_mom",
        "borrow_prepare_seojun",
        "d10_seojun_repay_choice",
        "d10_minjae_repay_choice",
        "d10_minjae_repay_router",
        "d10_minjae_repaid",
        "d10_minjae_repaid_message",
        "d10_minjae_cannot_repay",
        "d10_minjae_delay_thought",
        "d10_minjae_delay_message",
        "gamble_7",
        "gamble_8"
    )
    $ScenarioFlowRows = @($ScenarioFlowRows |
        Where-Object { $RemoveFlowScenes -notcontains $_.scene_id })

    foreach ($flowRow in $ScenarioFlowRows) {
        if ($flowRow.scene_id -eq "d10_seojun_followup")
            $flowRow.return_to_tablet = "false"
        elseif ($flowRow.scene_id -eq "d10_minjae_debt")
            $flowRow.return_to_tablet = "false"
    }

    foreach ($entry in @(
        @{ scene_id = "d10_seojun_repay_choice"; extra_trigger = ""; return_to_tablet = "false" },
        @{ scene_id = "d10_minjae_repay_choice"; extra_trigger = ""; return_to_tablet = "false" },
        @{ scene_id = "d10_minjae_repay_router"; extra_trigger = ""; return_to_tablet = "false" },
        @{ scene_id = "d10_minjae_repaid"; extra_trigger = ""; return_to_tablet = "false" },
        @{ scene_id = "d10_minjae_repaid_message"; extra_trigger = ""; return_to_tablet = "true" },
        @{ scene_id = "d10_minjae_cannot_repay"; extra_trigger = ""; return_to_tablet = "false" },
        @{ scene_id = "d10_minjae_delay_thought"; extra_trigger = ""; return_to_tablet = "false" },
        @{ scene_id = "d10_minjae_delay_message"; extra_trigger = ""; return_to_tablet = "true" },
        @{ scene_id = "gamble_7"; extra_trigger = ""; return_to_tablet = "true" },
        @{ scene_id = "gamble_8"; extra_trigger = ""; return_to_tablet = "true" }
    )) {
        $ScenarioFlowRows += New-RowFromHeaders $ScenarioFlowHeaders $entry
    }

    Write-CsvUtf8 $ScenarioFlowPath $ScenarioFlowRows


    # ================================================================
    # 최종 정적 검증
    # ================================================================
    Assert-Braces $DirectorPath
    Assert-Braces $FlowManagerPath
    Assert-Braces $DialoguePath
    Assert-Braces $BankPath
    Assert-ScenarioCsv $ScenarioPath $ScenarioFlowPath

    $DirectorCheck = Normalize-Lf (Read-Utf8 $DirectorPath)
    $FlowCheck = Normalize-Lf (Read-Utf8 $FlowManagerPath)
    $DialogueCheck = Normalize-Lf (Read-Utf8 $DialoguePath)
    $BankCheck = Normalize-Lf (Read-Utf8 $BankPath)
    $ScenarioCheck = @(Import-Csv -LiteralPath $ScenarioPath)

    foreach ($requiredText in @(
        "DOBak V15 FINAL",
        "CaptureScenarioSnapshot()",
        "RestoreScenarioSnapshot(checkpoint.chatSnapshot)",
        'PlayScene("borrow_choice")',
        'operation.StartsWith("available"',
        'immediateRoute = "gamble_repeat_loss"'
    )) {
        if (-not $DirectorCheck.Contains($requiredText)) {
            throw "Director 최종 검증 실패: $requiredText"
        }
    }

    if ($DirectorCheck.Contains("pendingBorrowMorningAdvance = true;")) {
        throw "차용 선택의 강제 날짜 이동 코드가 남아 있습니다."
    }
    if ($DirectorCheck.Contains('(flow.V3BankCash <= 0 && GetInt("counter.gamble_sessions") >= 5)')) {
        throw "플레이어가 선택하지 않은 자동 차용 조건이 남아 있습니다."
    }
    if (-not $FlowCheck.Contains("(이미 늦었지만, 지금이라도 학교에 가는 편이 낫겠다.)")) {
        throw "학교 지각 안내 콜백 수정이 적용되지 않았습니다."
    }
    if (-not $DialogueCheck.Contains("public string CaptureScenarioSnapshot()")) {
        throw "채팅 스냅샷 코드가 적용되지 않았습니다."
    }
foreach ($removedLine in @(
        "sys_borrow_late_morning_01",
        "sys_borrow_late_morning_02",
        "borrow_morning_cue_01",
        "borrow_prepare_mom_01",
        "borrow_prepare_seojun_01",
        "minjae_loan_rejected_02",
        "d10_minjae_debt_03"
    )) {
        if (@($ScenarioCheck | Where-Object { $_.line_id -eq $removedLine }).Count -gt 0) {
            throw "삭제 대상 대사가 남아 있습니다: $removedLine"
        }
    }

    $MinjaeRecovery = Get-OneRow $ScenarioCheck "d14_recovery_minjae_02"
    if ($MinjaeRecovery.enter_effects -match "relation\.minjae:add=1" -or
        $MinjaeRecovery.text -match "미안") {
        throw "민재 회복 엔딩에 사과·관계 회복 표현이 남아 있습니다."
    }

    # 과거 임시 자동 패처가 Unity 재실행 때 다시 코드를 덮지 않도록 성공 후에만 치운다.
    $LegacyPatchFiles = @(
        "Assets\Editor\DobakV3FlowHotfixInstaller.cs",
        "Assets\Editor\DobakV3FlowHotfixInstaller.cs.meta",
        "Assets\Editor\ScenarioV3FlowHotfixInstaller.cs",
        "Assets\Editor\ScenarioV3FlowHotfixInstaller.cs.meta",
        "Assets\DobakV3_FLOW_HOTFIX_v13_README.txt",
        "Assets\DobakV3_FLOW_HOTFIX_v13_README.txt.meta",
        "Assets\README_0903_v13_flow_hotfix.txt",
        "Assets\README_0903_v13_flow_hotfix.txt.meta"
    )
    foreach ($relative in $LegacyPatchFiles) {
        $legacyPath = Join-Path $ProjectRoot $relative
        if (-not (Test-Path $legacyPath))
            continue

        $backupPath = Join-Path $BackupRoot $relative
        New-Item -ItemType Directory -Path (Split-Path -Parent $backupPath) -Force | Out-Null
        Copy-Item -LiteralPath $legacyPath -Destination $backupPath -Force
        Remove-Item -LiteralPath $legacyPath -Force
    }

    $StatusLines = @(
        "DOBak V15 FINAL PATCH: PASS",
        "적용 시각: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')",
        "프로젝트: $ProjectRoot",
        "백업: $BackupRoot",
        "",
        "적용 완료:",
        "- 학교 지각 안내를 닫은 뒤에만 실제 이동",
        "- 대화 기록 Viewport 클리핑 및 세로 스와이프",
        "- 거래 내역 최신순 및 세로 스와이프",
        "- 차용 선택만으로 강제 날짜 이동하지 않음",
        "- 정상 취침 뒤 태블릿 홈 위 차용 대상 선택",
        "- 서준·민재 상환 선택을 메시지 화면 위 다이얼로그로 표시",
        "- 실제 답장만 채팅 말풍선으로 기록",
        "- 민재 거절 후 실제 차용 전까지 재제안 가능",
        "- 민재 악역 성격과 회복 엔딩 대사 정합성",
        "- 분기점 이전 VN·채팅 기록 보존",
        "- 도박 7회차 +20,000원 / 8회차 -40,000원",
        "- 앱 빨간 점 실제 행동 가능 조건",
        "- 중복 대사·중복 입력 방지",
        "",
        "Unity Editor Play Mode 실행 검증은 이 스크립트가 수행하지 않습니다.",
        "Intro에서 새 게임으로 1회, 엔딩의 분기점 복원으로 1회 QA하세요."
    )
    [IO.File]::WriteAllLines($LogPath, $StatusLines, $Utf8Bom)

    Write-Host ""
    Write-Host "============================================" -ForegroundColor Green
    Write-Host " DOBak V15 FINAL PATCH 적용 완료" -ForegroundColor Green
    Write-Host "============================================" -ForegroundColor Green
    Write-Host "백업 폴더: $BackupRoot"
    Write-Host "상태 파일: $LogPath"
    Write-Host ""
    Write-Host "Unity를 다시 열고 Intro에서 새 게임을 시작하세요."
}
catch {
    $failure = $_.Exception.Message
    Write-Host ""
    Write-Host "패치 적용 실패: $failure" -ForegroundColor Red
    Write-Host "변경한 파일을 자동 복원합니다." -ForegroundColor Yellow

    foreach ($relative in $RelativeTargets) {
        $backup = Join-Path $BackupRoot $relative
        $target = Join-Path $ProjectRoot $relative
        if (Test-Path $backup) {
            Copy-Item -LiteralPath $backup -Destination $target -Force
        }
    }

    $FailureLines = @(
        "DOBak V15 FINAL PATCH: FAIL",
        "실패 시각: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')",
        "원인: $failure",
        "원본 파일 자동 복원 완료",
        "백업: $BackupRoot"
    )
    [IO.File]::WriteAllLines($LogPath, $FailureLines, $Utf8Bom)
    exit 1
}
