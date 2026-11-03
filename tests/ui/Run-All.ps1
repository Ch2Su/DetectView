# UI 테스트 전체 실행. 하나라도 실패하면 종료 코드 1
#   pwsh tests\ui\Run-All.ps1                            # bin\Debug 빌드 (먼저 dotnet build)
#   pwsh tests\ui\Run-All.ps1 -ExeDir publish    # 배포본 스모크 테스트
# 필요: GPU(DirectML), publish\test1.mp4. 테스트 중 DetectView 창이 떴다 닫히며 바탕화면을 캡처한다
param([string]$ExeDir)

$failed = 0
foreach ($test in 'Test-FilePlayback.ps1', 'Test-Tracking.ps1', 'Test-Roi.ps1', 'Test-SceneChange.ps1', 'Test-ZoneEvents.ps1', 'Test-Incident.ps1', 'Test-ViewIdentity.ps1', 'Test-VfrPlayback.ps1', 'Test-ScreenHud.ps1', 'Test-NpuSwitch.ps1') {
    "=== $test ==="
    # 테스트가 try 밖에서 throw 해도(영상·빌드 없음 등) 그 파일만 실패로 세고 다음으로 간다 —
    # 예전엔 Run-All 까지 끝나, 영상이 필요 없는 테스트도 돌지 않고 요약도 나오지 않았다
    try {
        & (Join-Path $PSScriptRoot $test) -ExeDir $ExeDir
        if ($LASTEXITCODE -ne 0) { $failed++ }
    }
    catch { "[FAIL] 실행 중단 — $($_.Exception.Message)"; $failed++ }
    ''
}
if ($failed) { "=== 실패한 테스트 파일 $failed 개 ==="; exit 1 }
'=== 모두 통과 ==='
exit 0
