# UI 테스트 전체 실행. 하나라도 실패하면 종료 코드 1
#   pwsh tests\ui\Run-All.ps1                            # bin\Debug 빌드 (먼저 dotnet build)
#   pwsh tests\ui\Run-All.ps1 -ExeDir publish    # 배포본 스모크 테스트
# 필요: GPU(DirectML), publish\test1.mp4. 테스트 중 DetectView 창이 떴다 닫히며 바탕화면을 캡처한다
param([string]$ExeDir)

$failed = 0

# 스크립트는 UTF-8 BOM 으로 저장한다 — Windows PowerShell 5.1 은 BOM 없는 UTF-8 을 ANSI(이 PC 는 949)로 읽어 한국어 문자열이 깨진다.
# 도구가 다시 쓰며 BOM 을 빼먹어도 pwsh 에선 멀쩡히 돌아 모르고 지나간다(Common.ps1 이 2026-11-03~17 그랬다) → 돌릴 때마다 본다
# 파일마다 따로 잡는다 — 하나를 못 열어도(편집기·백신이 쓰는 중) 검사 전체가 조용히 빠지지 않게, 못 읽은 파일도 실패로 적는다
$noBom = [System.Collections.Generic.List[string]]::new()
foreach ($f in Get-ChildItem (Join-Path $PSScriptRoot '*.ps1'), (Join-Path $PSScriptRoot '..\..\tools\*.ps1') -File) {
    try {
        $fs = [IO.File]::Open($f.FullName, 'Open', 'Read', 'ReadWrite')
        try { $b = [byte[]]::new(3); $n = $fs.Read($b, 0, 3) } finally { $fs.Dispose() }
        if (-not ($n -eq 3 -and $b[0] -eq 0xEF -and $b[1] -eq 0xBB -and $b[2] -eq 0xBF)) { $noBom.Add($f.Name) }
    }
    catch { $noBom.Add("$($f.Name)(읽지 못함: $($_.Exception.Message))") }
}
if ($noBom.Count) { "[FAIL] UTF-8 BOM 이 없는 스크립트 — $($noBom -join ', ')"; ''; $failed++ }

foreach ($test in 'Test-FilePlayback.ps1', 'Test-Tracking.ps1', 'Test-Roi.ps1', 'Test-SceneChange.ps1', 'Test-ZoneEvents.ps1', 'Test-Incident.ps1', 'Test-VlmAnnotation.ps1', 'Test-ViewIdentity.ps1', 'Test-CameraPose.ps1', 'Test-VfrPlayback.ps1', 'Test-FileDetectFault.ps1', 'Test-ScreenHud.ps1', 'Test-NpuSwitch.ps1') {
    "=== $test ==="
    # 테스트가 try 밖에서 throw 해도(영상·빌드 없음 등) 그 파일만 실패로 세고 다음으로 간다 —
    # 예전엔 Run-All 까지 끝나, 영상이 필요 없는 테스트도 돌지 않고 요약도 나오지 않았다
    try {
        & (Join-Path $PSScriptRoot $test) -ExeDir $ExeDir
        if ($LASTEXITCODE -ne 0) { $failed++ }
    }
    catch {
        "[FAIL] 실행 중단 — $($_.Exception.Message)"
        # 난 곳을 붙인다(스크립트 스택 위 세 단계) — 메시지만으론 'null 값 식…' 처럼 어디서 났는지 모른다
        "$($_.ScriptStackTrace)" -split "`r?`n" | Where-Object { $_ -match '\.ps1: ' } | Select-Object -First 3 | ForEach-Object { "    $_" }
        $failed++
    }
    ''
}
if ($failed) { "=== 실패한 테스트 파일 $failed 개 ==="; exit 1 }
'=== 모두 통과 ==='
exit 0
