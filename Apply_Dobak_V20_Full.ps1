$ErrorActionPreference = "Stop"
$Utf8Bom = New-Object System.Text.UTF8Encoding($true)

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
    throw 'Unity 프로젝트 루트를 찾지 못했습니다. 이 ZIP의 내용물을 Assets와 ProjectSettings가 보이는 위치에 풀어주세요.'
}

$ProjectRoot = Find-ProjectRoot
if (@(Get-Process -Name Unity -ErrorAction SilentlyContinue).Count -gt 0) {
    throw 'Unity Editor가 실행 중입니다. 완전히 종료한 뒤 다시 실행하세요.'
}

$Timestamp = Get-Date -Format 'yyyyMMdd_HHmmss'
$BackupRoot = Join-Path $ProjectRoot ('_Dobak_V20_Backup_' + $Timestamp)
$StatusPath = Join-Path $ProjectRoot 'DOBak_V20_PATCH_STATUS.txt'
$Targets = @(
 'Assets\Tablet\Script\ScenarioV3Director.cs',
 'Assets\Tablet\Script\GameFlowManager.cs',
 'Assets\Tablet\Script\DialogueManager.cs',
 'Assets\Junsang\Scripts\Bank\BankUI.cs',
 'Assets\Junsang\Scripts\Bank\CoinManager.cs',
 'Assets\Junsang\Scripts\Casino\CasinoUIManager.cs',
 'Assets\Junsang\Scripts\SlotMachine\SlotMachineManager.cs',
 'Assets\Resources\ScenarioV3.csv',
 'Assets\Resources\ScenarioV3Flow.csv',
 'Assets\Junsang\Scripts\Bank\BankHistoryScrollFix.cs',
 'Assets\Tablet\Script\ScenarioV3HistoryRuntimeFix.cs',
 'Assets\Tablet\Script\ScenarioV3FinalRuntimeFix.cs',
 'Assets\Tablet\Script\ScenarioV3FinalRuntimeFix.cs.meta',
 'Assets\Editor\DobakV3FlowHotfixInstaller.cs',
 'Assets\Editor\DobakV3FlowHotfixInstaller.cs.meta',
 'Assets\Editor\ScenarioV3FlowHotfixInstaller.cs',
 'Assets\Editor\ScenarioV3FlowHotfixInstaller.cs.meta'
)

New-Item -ItemType Directory -Path $BackupRoot -Force | Out-Null
$Manifest = New-Object System.Collections.Generic.List[string]
foreach ($relative in $Targets) {
    $source = Join-Path $ProjectRoot $relative
    if (Test-Path $source) {
        $Manifest.Add('EXIST|' + $relative)
        $dest = Join-Path $BackupRoot $relative
        New-Item -ItemType Directory -Path (Split-Path -Parent $dest) -Force | Out-Null
        Copy-Item -LiteralPath $source -Destination $dest -Force
    } else {
        $Manifest.Add('MISSING|' + $relative)
    }
}
[IO.File]::WriteAllLines((Join-Path $BackupRoot 'manifest.txt'), [string[]]$Manifest, $Utf8Bom)

function Restore-All {
    foreach ($item in $Manifest) {
        $parts = $item.Split('|',2)
        $relative = $parts[1]
        $target = Join-Path $ProjectRoot $relative
        if ($parts[0] -eq 'EXIST') {
            $backup = Join-Path $BackupRoot $relative
            New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
            Copy-Item -LiteralPath $backup -Destination $target -Force
        } elseif (Test-Path $target) {
            Remove-Item -LiteralPath $target -Force
        }
    }
}

try {
    $env:DOBAK_V20_PROJECT_ROOT = $ProjectRoot

    # Base flow fixes. Re-running is safe because V15 checks its marker.
    $base = Join-Path $PSScriptRoot 'PatchCore\Apply_Dobak_V15_Base.ps1'
    $process = Start-Process powershell.exe -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-File',('"' + $base + '"')) -Wait -PassThru
    if ($process.ExitCode -ne 0) { throw "기본 흐름 패치가 실패했습니다. 종료 코드: $($process.ExitCode)" }

    # Full dialogue polish, now corrected to use won rather than points.
    $textPatch = Join-Path $PSScriptRoot 'PatchCore\Apply_ScenarioV3_Text_V20.ps1'
    $process = Start-Process powershell.exe -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-File',('"' + $textPatch + '"')) -Wait -PassThru
    if ($process.ExitCode -ne 0) { throw "문체 패치가 실패했습니다. 종료 코드: $($process.ExitCode)" }

    # Copy runtime UI repairs.
    $patchAssets = Join-Path $PSScriptRoot 'PatchFiles\Assets'
    Copy-Item -LiteralPath $patchAssets -Destination $ProjectRoot -Recurse -Force

    # Latest conversation-derived source/CSV corrections.
    $post = Join-Path $PSScriptRoot 'PatchCore\Apply_Dobak_V20_Post.ps1'
    $process = Start-Process powershell.exe -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-File',('"' + $post + '"')) -Wait -PassThru
    if ($process.ExitCode -ne 0) { throw "최종 통합 보정이 실패했습니다. 종료 코드: $($process.ExitCode)" }

    $status = @(
        'DOBak V20.1 INTEGRATED PATCH: PASS',
        "적용 시각: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')",
        "프로젝트: $ProjectRoot",
        "백업: $BackupRoot",
        '',
        '반영 완료:',
        '- P/포인트 표시 제거, 원 단위 문장과 UI로 통일(내부 환산/자동 환전 로직 유지)',
        '- 최초 링크 이후 앱 표현으로 통일',
        '- 스케줄러에 이미 메모된 것으로 대사 수정',
        '- 학교 종료 후 집에 간 다음 남은 일정 확인',
        '- 도박 앱 차단 시 메시지/차용/상환 등 선행 행동 안내',
        '- 실제 밤샘 다음 날 오전 10시 아침 배경',
        '- 평일 지각 학교 유도 / 주말 카페 결근 처리',
        '- 지각 안내를 지도 위에서 한 번만 표시하고 지도 유지',
        '- VN 지난 대화 RectMask2D + 세로 스크롤 + 하루 단위 초기화',
        '- 채팅/은행 기록은 날짜 변경으로 초기화하지 않음',
        '- 민재 첫 차용 문장의 이번엔 제거',
        '- 3/6/10일차 노트북 수리비 조급함 보강',
        '- 은행 거래 내역 최신순, 최상단 고정, 스크롤/마스크',
        '',
        'Unity Play Mode 자동 검증은 수행하지 않았습니다. Intro 새 게임으로 QA하세요.'
    )
    [IO.File]::WriteAllLines($StatusPath, [string[]]$status, $Utf8Bom)
    Write-Host ''
    Write-Host 'DOBak V20.1 통합 패치 적용 완료' -ForegroundColor Green
    Write-Host "상태 파일: $StatusPath"
    Write-Host "백업 폴더: $BackupRoot"
}
catch {
    $message = $_.Exception.Message
    Restore-All
    [IO.File]::WriteAllLines($StatusPath, [string[]]@(
        'DOBak V20.1 INTEGRATED PATCH: FAIL',
        "원인: $message",
        "원본 복원 완료: $BackupRoot"
    ), $Utf8Bom)
    Write-Host ''
    Write-Host '패치 실패. 원본 파일을 복원했습니다.' -ForegroundColor Red
    Write-Host $message -ForegroundColor Red
    exit 1
}
