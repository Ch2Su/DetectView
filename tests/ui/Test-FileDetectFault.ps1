# 파일 재생 중 검출(추론)이 실패할 때 (2026-11-28 ChatGPT 리뷰 6번) — Debug 빌드의 결함 주입(TestFaults.cs, 환경 변수 DETECTVIEW_TEST_FAULTS)으로 본다.
# 예전엔 검출 예외가 나면 디버그 출력만 남기고 표시·기다림 없이 다음 프레임으로 넘어가, 이어서 실패하면(장치 제거·드라이버 오류 등) 마지막으로 그린 화면이
# 남은 채 파일 끝까지 내달려 '재생 완료'로 끝났다.
#   1) file-detect-once: 영상 3초 넘어 한 프레임의 검출이 실패 → 그 프레임만 건너뛰고 이어 재생한다 (멈추지 않고 상태바 그대로)
#   2) file-detect-fail: 5초 넘어 12번 이어서 실패 → 10번째에 재생이 멈추고(버튼 '재생') 상태바에 '분석 중단 — 00:05.0 부터 …' 와 사유(툴팁에 전문),
#      슬라이더는 실패가 시작된 5.0초로 돌아간다. 그대로 두어도 파일 끝으로 가지 않는다
#   3) 다시 재생 → 결함이 풀려(12번 뒤) 5초 구간부터 이어 분석한다: 슬라이더 5~7.5초, 상태바 '파일: …'
# Release(배포본)엔 결함 주입이 없어 건너뛴다 ([INFO])
#
#   pwsh tests\ui\Test-FileDetectFault.ps1
param(
    [string]$ExeDir,
    [string]$Video   # 생략 시 publish\test1.mp4 (30fps — 3초·5초 지점이 프레임 90·150)
)
. (Join-Path $PSScriptRoot 'Common.ps1')

$ExeDir = Resolve-ExeDir $ExeDir
if ($ExeDir -notlike '*\bin\Debug\*') {
    Note '검출 실패 처리' "Debug 빌드가 아니라 건너뜀 — 결함 주입(TestFaults.cs)은 Release 에선 컴파일되지 않는다 ($ExeDir)"
    Complete-Test
    return
}
if (-not $Video) { $Video = Join-Path $RepoRoot 'publish\test1.mp4' }
if (-not (Test-Path $Video)) { throw "테스트 영상이 없습니다: $Video" }
$Video = New-TestVideo $Video   # 임시 폴더의 하드 링크 — 원본 영상 옆의 파일을 읽거나 건드리지 않는다 (Common.ps1)
$name = Split-Path $Video -Leaf

$proc = $null
$origFaults = $env:DETECTVIEW_TEST_FAULTS
try {
    $env:DETECTVIEW_TEST_FAULTS = 'file-detect-once,file-detect-fail'   # 앱은 띄울 때만 읽는다 — 띄운 뒤 곧바로 되돌린다
    try { $proc = Start-DetectView $ExeDir }
    finally { if ($origFaults) { $env:DETECTVIEW_TEST_FAULTS = $origFaults } else { Remove-Item Env:DETECTVIEW_TEST_FAULTS -ErrorAction SilentlyContinue } }
    $root = $proc.Root
    $status = Need $root 'Txt_Status'
    $play = Need $root 'Btn_PlayPause'
    $count = Need $root 'Txt_DetectionCount'
    $range = (Need $root 'VideoSlider').GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern)

    Open-VideoFile $root $Video
    $ok = WaitFor { (NameOf $status) -eq "파일: $name" } 10000
    Check '재생 시작' $ok "상태='$(NameOf $status)'"

    # 1) 3초 넘어 한 프레임 실패 — 4.2초까지 멈추지 않고 이어 간다
    $ok = WaitFor { $range.Current.Value -gt 4200 } 8000
    Check '한 프레임 검출 실패 → 건너뛰고 이어 재생' ($ok -and (NameOf $play) -eq '일시정지' -and (NameOf $status) -eq "파일: $name") `
        ('위치 {0:N0} ms · 버튼 {1} · 상태 {2}' -f $range.Current.Value, (NameOf $play), (NameOf $status))

    # 2) 5초 넘어 이어서 실패 — 멈추고 알린다
    $ok = WaitFor { (NameOf $status) -like '분석 중단*' } 5000
    $st = NameOf $status
    Check '이어서 실패 → 재생이 멈추고 위치·사유를 알림' ($ok -and $st -like '분석 중단 — 00:05.0 부터 검출이 계속 실패했습니다: 시험용 결함: 검출 예외 (*' -and (NameOf $play) -eq '재생') `
        "상태='$st' · 버튼 $(NameOf $play)"
    Check '사유 전문은 상태바 툴팁' ($status.Current.HelpText -like '*file-detect-fail*') "툴팁='$($status.Current.HelpText)'"
    $ok = WaitFor { [Math]::Abs($range.Current.Value - 5000) -le 1 } 2000
    Check '슬라이더는 실패가 시작된 곳(5.0초)으로' $ok ('{0:N0} ms' -f $range.Current.Value)
    Start-Sleep -Milliseconds 1500
    Check '멈춘 채 그대로 — 파일 끝까지 가지 않음' ((NameOf $status) -like '분석 중단*' -and $range.Current.Value -lt 5100 -and (NameOf $play) -eq '재생') `
        ('1.5초 뒤 위치 {0:N0} ms · 상태 {1}' -f $range.Current.Value, (NameOf $status))

    # 3) 다시 재생 — 결함이 풀려 5초 구간부터 이어 분석한다
    Click $play
    $ok = WaitFor { $range.Current.Value -gt 5500 } 5000
    Start-Sleep -Milliseconds 300
    $v = $range.Current.Value
    Check '다시 재생 → 그 구간부터 이어 분석' ($ok -and $v -lt 7500 -and (NameOf $status) -eq "파일: $name" -and (NameOf $play) -eq '일시정지' -and (NameOf $count) -match '^검출 \d+') `
        ('위치 {0:N0} ms · 상태 {1} · {2}' -f $v, (NameOf $status), (NameOf $count))
}
catch { Check '예외' $false (Format-Error $_) }
finally { Stop-DetectView $proc; Remove-TestVideos }

Complete-Test
