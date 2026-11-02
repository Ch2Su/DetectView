# 파일 재생 UI 테스트
#   - 다른 작업 디렉터리에서 실행해도 모델을 찾는지 (예전엔 exe 옆에 있어도 못 찾았다)
#   - 파일 열기 → 재생 → SAHI → 멈춘 채 시크 → SAHI → 재생 중 시크 직후 SAHI(그 위치를 분석하는지)
#     → 끝 근처 시크 → EOF → 재생 시 처음부터 → 다시 EOF 뒤 시크하면 그 위치부터 → 정지
#   - 각 단계의 재생/일시정지 버튼 글자와 활성 상태
#   - 클래스별 검출 수 범례(영상 칸 왼쪽 위): 그 글자(검출 수 칸의 UIA ItemStatus)의 합이 '검출 N' 과 같은지 —
#     재생 중 · SAHI 뒤 · 멈춘 채 시크한 한 장 · 그 SAHI 뒤, 정지하면 비는지
#   - 추적 번호(라벨 "CAR #12 0.95", 검출 수 칸의 UIA ItemType): 박스마다 번호가 하나씩 붙는지(재생 중 · 멈춘 채 시크한 한 장),
#     재생 중 프레임이 넘어가도 번호가 이어지는지, SAHI 결과엔 안 붙는지, 정지하면 비는지. 멈춘 물체가 '정지'로 나오는지·시크 뒤 번호가
#     새로 매겨지는지는 test4 로 Test-Tracking.ps1 이 본다
#
#   pwsh tests\ui\Test-FilePlayback.ps1                              # bin\Debug 빌드
#   pwsh tests\ui\Test-FilePlayback.ps1 -ExeDir publish_gpu_ver      # 배포본
param(
    [string]$ExeDir,
    [string]$Video   # 생략 시 publish_gpu_ver\test1.mp4 (30초 이상인 CFR 영상이어야 EOF 단계가 의미 있다)
)
. (Join-Path $PSScriptRoot 'Common.ps1')

$ExeDir = Resolve-ExeDir $ExeDir
if (-not $Video) { $Video = Join-Path $RepoRoot 'publish_gpu_ver\test1.mp4' }
if (-not (Test-Path $Video)) { throw "테스트 영상이 없습니다: $Video" }

$proc = $null
try {
    # 일부러 exe 폴더가 아닌 곳을 작업 디렉터리로 띄운다 — 모델 경로 회귀 방지
    $proc = Start-DetectView $ExeDir -WorkingDirectory ([IO.Path]::GetTempPath())
    $root = $AE::FromHandle($proc.MainWindowHandle)

    $play   = ById $root 'Btn_PlayPause'
    $stop   = ById $root 'Btn_Stop'
    $sahi   = ById $root 'Btn_Sahi'
    $status = ById $root 'Txt_Status'
    $res    = ById $root 'Txt_Resolution'
    $slider = ById $root 'VideoSlider'
    $count  = ById $root 'Txt_DetectionCount'

    Check '시작 시 재생 버튼 글자' ((NameOf $play) -eq '재생') "'$(NameOf $play)'"

    # 1) 파일 열기 → 재생 시작
    Open-VideoFile $root $Video
    $ok = WaitFor { (NameOf $status) -like '파일:*' -and (NameOf $play) -eq '일시정지' } 10000
    Check '파일 재생 시작' $ok "상태='$(NameOf $status)' 버튼='$(NameOf $play)'"
    $range = $slider.GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern)
    $max = $range.Current.Maximum

    # 첫 프레임이 처리돼야 SAHI 대상이 생긴다 (그 전엔 "분석할 프레임이 없습니다" 가 정상)
    $ok = WaitFor { (NameOf $res) -match '\d+×\d+' } 10000
    Check '첫 프레임 표시' $ok "해상도='$(NameOf $res)'"

    # 클래스별 검출 수 범례 — 박스와 같은 BoxLayer 가 같은 프레임의 박스로 그린다. 재생 중이라 같은 프레임의 두 값을 읽을 때까지 다시 읽는다
    $cc = @{}
    $ok = WaitFor { (Test-ClassCountsMatch $count $cc) -and $cc.N -gt 0 } 10000
    Check '클래스별 검출 수 = 검출 N (재생 중)' $ok "검출='$(NameOf $count)' 범례='$($cc.Counts.Text)'"

    # 추적 번호 — 재생 루프가 보이는 프레임마다 추적기를 거쳐 박스에 번호를 붙인다. 박스마다 하나씩 붙었고, 0.25초 간격으로 읽은 두 프레임에
    # 같은 번호가 있어야 한다(번호가 이어진다 — 프레임마다 새로 매기면 겹치지 않는다. 사람이 많은 장면이라 이어지는 물체가 늘 있다)
    $tm = @{}
    $ok = WaitFor { (Test-TrackMarksMatch $count $tm) -and $tm.N -gt 0 } 10000
    Check '추적 번호 = 검출 수 (재생 중)' $ok "검출='$(NameOf $count)' 번호='$($tm.Marks.Text)'"
    $kept = $false
    $prev = $null
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while (-not $kept -and $sw.ElapsedMilliseconds -lt 5000) {
        $cur = Get-TrackMarks $count
        if ($prev -and $cur -and @($cur.Ids | Where-Object { $prev.Ids -contains $_ }).Count -gt 0) { $kept = $true }
        $prev = $cur
        Start-Sleep -Milliseconds 250
    }
    Check '추적 번호 이어짐 (재생 중)' $kept "앞 번호='$($prev.Text)'"

    # 원본 속도로 재생되는지 (예전엔 Task.Delay 올림 오차가 쌓여 30fps mp4 가 0.82배속이었다)
    $rate = Measure-PlaybackRate $range 2000
    Check '재생 속도' ($rate -gt 0.9 -and $rate -lt 1.1) ('{0:N2} 배속' -f $rate)

    # 2) SAHI: 끝나면 버튼이 현재 모드 기준으로 복원 (SAHI 가 재생을 멈추므로 '재생')
    Click $sahi
    $ok = WaitFor { (NameOf $status) -like '정밀 분석 완료*' } 30000
    Check 'SAHI 완료' $ok "상태='$(NameOf $status)'"
    Start-Sleep -Milliseconds 200
    Check 'SAHI 후 SAHI 버튼 활성' $sahi.Current.IsEnabled ''
    Check 'SAHI 후 재생 버튼 활성' $play.Current.IsEnabled ''
    Check 'SAHI 후 재생 버튼 글자' ((NameOf $play) -eq '재생') "'$(NameOf $play)'"
    Check 'SAHI 후 슬라이더 활성' $slider.Current.IsEnabled ''   # 분석 중엔 꺼 둔다 (_sahiBusy) — 끝나면 돌아와야 한다
    # 멈춘 상태라 범례·검출 수가 더 바뀌지 않는다 → 한 번에 본다 (SAHI 결과가 범례에도 갔는지)
    $cc = @{}
    Check 'SAHI → 클래스별 검출 수 = 검출 N' ((Test-ClassCountsMatch $count $cc) -and (NameOf $count) -like '*(SAHI)*') "검출='$(NameOf $count)' 범례='$($cc.Counts.Text)'"
    # SAHI 는 한 장짜리 분석이라 추적하지 않는다 — 라벨에 번호가 없다
    Check 'SAHI → 추적 번호 없음' ([string]::IsNullOrEmpty($count.Current.ItemType)) "번호='$($count.Current.ItemType)'"

    # 2-1) 멈춘 채 시크 → 그 위치 한 장이 처리되고, SAHI 는 그 프레임을 분석한다
    #      (예전엔 화면·SAHI 대상이 옛 프레임 그대로였다). 한 장이 처리되면 검출 수에서 "(SAHI)" 가 빠진다
    $mid = [Math]::Round($max / 2)
    $range.SetValue($mid)
    $ok = WaitFor { (NameOf $count) -notlike '*(SAHI)*' } 5000
    $v = $range.Current.Value
    Check '멈춘 채 시크 → 한 장 표시' ($ok -and [Math]::Abs($v - $mid) -lt 1000) "검출='$(NameOf $count)' 위치=$([int]$v) ms (목표 $mid)"
    Check '멈춘 채 시크 → 계속 멈춤' ((NameOf $play) -eq '재생') "'$(NameOf $play)'"
    $cc = @{}
    Check '멈춘 채 시크 → 클래스별 검출 수 = 검출 N' (Test-ClassCountsMatch $count $cc) "검출='$(NameOf $count)' 범례='$($cc.Counts.Text)'"
    $tm = @{}
    Check '멈춘 채 시크 → 추적 번호 = 검출 수' ((Test-TrackMarksMatch $count $tm) -and $tm.N -gt 0) "검출='$(NameOf $count)' 번호='$($tm.Marks.Text)'"
    Click $sahi
    $ok = WaitFor { (NameOf $count) -like '*(SAHI)*' -and (NameOf $status) -like '정밀 분석 완료*' } 30000
    Check '시크 뒤 SAHI 완료' $ok "상태='$(NameOf $status)' 검출='$(NameOf $count)'"
    $cc = @{}
    Check '시크 뒤 SAHI → 클래스별 검출 수 = 검출 N' (Test-ClassCountsMatch $count $cc) "검출='$(NameOf $count)' 범례='$($cc.Counts.Text)'"
    $null = WaitFor { $play.Current.IsEnabled } 30000   # 위가 실패했어도 아래 단계가 꺼진 버튼을 누르지 않게

    # 2-2) 재생 중 시크 직후 SAHI → 시크한 위치의 프레임을 분석한다. 예전엔 재생 루프가 시크를 처리하는 사이(Set(PosFrames)·디코딩·검출 —
    #      이 위치는 수십 ms) 누르면 시크 전 프레임을 분석했다. 분석한 프레임의 위치는 상태 문구 끝 (mm:ss)
    Click $play
    $null = WaitFor { (NameOf $count) -notlike '*(SAHI)*' } 5000   # 재생이 다시 돌아 검출 수가 일반 표시로 바뀔 때까지
    Start-Sleep -Milliseconds 800
    $q = [Math]::Round($max / 4)
    $range.SetValue($q)
    # 곧바로 누르면 루프가 시크를 꺼내기 전이라 옛 코드도 '시크 대기' 경로로 통과했고, 오래 기다리면 시크 처리가 끝난 뒤라 역시 통과한다.
    # 루프가 시크를 꺼내 처리하는 도중에 누르도록 35ms 기다린다 — 대기 논리만 옛것으로 되돌린 빌드로 재 보니(10-31, 5번씩)
    # 35ms 는 5번 모두 옛 버그를 잡았고 70ms 는 1번, 120ms 는 0번(누르기 전에 처리가 끝남). 타이밍에 기대므로 PC 가 다르면 달라질 수 있다
    Start-Sleep -Milliseconds 35
    # 누르기 직전 슬라이더 — 시크한 프레임이 이미 보였으면 재생 루프가 슬라이더를 그 프레임 시각(시크 값과 조금 다름)으로 옮겨 둔다.
    # 그런 실행은 누른 때가 시크 처리 뒤라 옛 코드도 통과했을 것이다 → 실패가 아니라 [INFO] 로 남긴다
    $pre = $range.Current.Value
    Click $sahi
    $ok = WaitFor { (NameOf $count) -like '*(SAHI)*' -and (NameOf $status) -like '정밀 분석 완료*' } 30000
    $st = NameOf $status
    $sec = if ($st -match '\((\d+):(\d\d)\)$') { [int]$Matches[1] * 60 + [int]$Matches[2] } else { -1 }
    $v = $range.Current.Value
    Check '재생 중 시크 직후 SAHI → 그 위치 분석' ($ok -and [Math]::Abs($sec - $q / 1000) -le 1.5 -and [Math]::Abs($v - $q) -lt 1500) `
        "상태='$st' · 시크 $([int]$q) ms · 슬라이더 $([int]$v) ms"
    if ([Math]::Abs($pre - $q) -gt 0.5) { Note '시크 직후 SAHI 판별력' "누르기 전에 시크 처리가 끝남(슬라이더 $([int]$pre) ms) — 이번 실행은 옛 버그를 가리지 못함" }
    $null = WaitFor { $play.Current.IsEnabled } 30000

    # 3) 끝 근처로 시크 → 재생 → EOF
    $range.SetValue([Math]::Max(0, $max - 1500))
    Click $play
    $ok = WaitFor { (NameOf $status) -eq '재생 완료' } 15000
    Check 'EOF 도달' $ok "상태='$(NameOf $status)' (영상 길이 $([int]($max / 1000))초)"
    Check 'EOF 후 재생 버튼 글자' ((NameOf $play) -eq '재생') "'$(NameOf $play)'"

    # 4) EOF 에서 재생 → 처음부터 다시, 계속 재생 (예전엔 EOF 를 다시 읽고 곧바로 멈췄다)
    Click $play
    Start-Sleep -Milliseconds 2500
    $v = $range.Current.Value
    Check 'EOF 후 재생 → 계속 재생' ((NameOf $play) -eq '일시정지') "버튼='$(NameOf $play)'"
    Check 'EOF 후 재생 → 처음부터' ($v -lt 6000) "위치=$([int]$v) ms"
    Check 'EOF 후 재생 → 상태 문구' ((NameOf $status) -ne '재생 완료') "상태='$(NameOf $status)'"

    # 4-1) 다시 EOF 에서 슬라이더로 옮기면 '재생 완료' 문구가 사라지고, 재생은 처음이 아니라 그 위치부터
    #      (예전엔 그 위치부터 재생하는 내내 '재생 완료'가 남았다)
    $range.SetValue([Math]::Max(0, $max - 1500))
    $ok = WaitFor { (NameOf $status) -eq '재생 완료' } 15000
    $range.SetValue($mid)
    Start-Sleep -Milliseconds 300
    Check 'EOF 뒤 시크 → 상태 문구' ($ok -and (NameOf $status) -like '파일:*') "상태='$(NameOf $status)'"
    Click $play
    Start-Sleep -Milliseconds 1500
    $v = $range.Current.Value
    # 하한을 시크 위치보다 뒤로 둔다 — 버튼 글자·슬라이더 값은 재생 루프가 멈춰 있어도 맞게 보이므로, 실제로 나아갔는지 본다 (1배속 1.5초 ≈ +1500)
    Check 'EOF 뒤 시크 → 그 위치부터 재생' ((NameOf $play) -eq '일시정지' -and $v -gt $mid + 500 -and $v -lt $mid + 5000 -and (NameOf $status) -ne '재생 완료') "버튼='$(NameOf $play)' 위치=$([int]$v) ms (시크 $mid) 상태='$(NameOf $status)'"

    # 5) 정지 → 버튼은 '재생' (예전엔 '일시정지' 가 남았다)
    Click $stop
    $ok = WaitFor { (NameOf $status) -eq '정지됨' } 5000
    Check '정지 후 재생 버튼 글자' ($ok -and (NameOf $play) -eq '재생') "버튼='$(NameOf $play)' 상태='$(NameOf $status)'"
    Check '정지 → 범례 지움' ([string]::IsNullOrEmpty($count.Current.ItemStatus)) "범례='$($count.Current.ItemStatus)'"
    Check '정지 → 추적 번호 지움' ([string]::IsNullOrEmpty($count.Current.ItemType)) "번호='$($count.Current.ItemType)'"
}
catch { Check '예외' $false $_.Exception.Message }
finally { Stop-DetectView $proc }

Complete-Test
