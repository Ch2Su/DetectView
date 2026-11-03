# 추적기 UI 테스트 — 앱에 연결된 추적기(Tracker)가 번호를 이어 매기고, 멈춘 물체를 '정지'로 보고, 시크하면 번호를 새로 매기는지
#   test4.mp4 는 0~10.5초 차로 한가운데 고장 정차한 덤프트럭이 있다(CLAUDE.md '테스트 영상 내용', 10.5초에 카메라가 돌아간다).
#   번호는 박스 라벨 "CAR #12 정지 0.95" 이고 검출 수 칸(Txt_DetectionCount)의 UIA ItemType 으로 읽는다 (라벨은 그림이라 UIA 로 안 보인다).
#   1) 재생 중 2초 지점: '정지'가 붙은 번호가 있다 (덤프트럭 — 정지 판정이 라벨까지 간다)
#   2) 5초 지점: 2초 때 정지로 나온 번호가 그대로 정지 (같은 물체로 3초 동안 이어진다 — 추적기가 끊기지 않는다)
#   3) 일시정지 → 앞으로 시크(8초): 번호를 새로 매긴다. 앞뒤 화면이 이어지지 않는데 추적기는 정지 트랙을 10초 붙들기 때문에
#      비우지 않으면 시크 앞뒤의 같은 자리 차가 같은 번호를 받는다 (뒤로 가는 시각은 추적기가 저절로 비우니 앞으로 시크로 본다)
#   4) 재개: 시크 프레임에서 시작한 번호가 이어져 1초 뒤 정지로 나온다
#   5) 정지하면 번호가 지워진다
#
#   pwsh tests\ui\Test-Tracking.ps1                              # bin\Debug 빌드
#   pwsh tests\ui\Test-Tracking.ps1 -ExeDir publish      # 배포본
param(
    [string]$ExeDir,
    [string]$Video   # 생략 시 publish\test4.mp4 (0~10.5초에 멈춘 차가 있는 영상이어야 한다)
)
. (Join-Path $PSScriptRoot 'Common.ps1')

$ExeDir = Resolve-ExeDir $ExeDir
if (-not $Video) { $Video = Join-Path $RepoRoot 'publish\test4.mp4' }
if (-not (Test-Path $Video)) { throw "테스트 영상이 없습니다: $Video" }

$proc = $null
try {
    $proc = Start-DetectView $ExeDir
    $root = $AE::FromHandle($proc.MainWindowHandle)

    $play   = ById $root 'Btn_PlayPause'
    $stop   = ById $root 'Btn_Stop'
    $status = ById $root 'Txt_Status'
    $slider = ById $root 'VideoSlider'
    $count  = ById $root 'Txt_DetectionCount'

    Open-VideoFile $root $Video
    $ok = WaitFor { (NameOf $status) -like '파일:*' -and (NameOf $play) -eq '일시정지' } 10000
    Check '파일 재생 시작' $ok "상태='$(NameOf $status)' 버튼='$(NameOf $play)'"
    $range = $slider.GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern)
    $got = @{}   # WaitFor 조건 안에서 읽은 값을 남긴다 (참조형이라 조건 블록에서 채울 수 있다)

    # 1) 재생 중 2초 지점 — 정지로 판정된 물체가 있고, 그게 라벨까지 갔다
    $null = WaitFor { $range.Current.Value -ge 2000 } 15000
    $ok = WaitFor { $m = Get-TrackMarks $count; if ($m -and $m.Stationary.Count -gt 0) { $got.M1 = $m; $true } else { $false } } 5000
    Check '멈춘 차가 정지로 나옴 (2초)' $ok "위치=$([int]$range.Current.Value) ms 번호='$($got.M1.Text)'"
    $still1 = @($got.M1.Stationary)

    # 2) 5초 지점 — 2초 때 정지였던 번호가 그대로 정지 (같은 물체가 같은 번호로 이어졌다)
    $null = WaitFor { $range.Current.Value -ge 5000 } 15000
    $ok = WaitFor {
        $m = Get-TrackMarks $count
        if ($m -and @($m.Stationary | Where-Object { $still1 -contains $_ }).Count -gt 0) { $got.M2 = $m; $true } else { $false }
    } 3000
    Check '멈춘 차가 같은 번호로 이어짐 (5초)' $ok "2초 정지 번호=[$($still1 -join ' ')] → 번호='$($got.M2.Text)' 위치=$([int]$range.Current.Value) ms"

    # 3) 일시정지 → 앞으로 시크 → 번호를 새로 매긴다. 그때까지 본 번호보다 모두 커야 한다
    #    (추적기는 ID 를 이어서 센다 — 시크 앞뒤의 물체가 같은 번호를 받지 않게. 비우지 않으면 정지 트랙의 번호가 그대로 남아 작다)
    $seen = @($got.M1.Ids) + @($got.M2.Ids)
    $maxSeen = if ($seen.Count) { ($seen | Measure-Object -Maximum).Maximum } else { 0 }
    Click $play
    $null = WaitFor { (NameOf $play) -eq '재생' } 3000
    Start-Sleep -Milliseconds 300
    $range.SetValue(8000)
    $tm = @{}
    $ok = WaitFor {
        (Test-TrackMarksMatch $count $tm) -and $tm.N -gt 0 -and (($tm.Marks.Ids | Measure-Object -Minimum).Minimum -gt $maxSeen)
    } 5000
    Check '앞으로 시크 → 번호를 새로 매김' $ok "본 번호 최대 #$maxSeen → 시크 뒤 번호='$($tm.Marks.Text)'"
    $afterSeek = @($tm.Marks.Ids)

    # 4) 재개 — 시크 프레임에서 시작한 번호가 이어져, 1초 뒤 멈춘 차가 그 번호로 정지가 된다 (카메라가 돌아가는 10.5초 전에)
    Click $play
    $null = WaitFor { $range.Current.Value -ge 9000 } 8000
    $ok = WaitFor {
        $m = Get-TrackMarks $count
        if ($m -and @($m.Stationary | Where-Object { $afterSeek -contains $_ }).Count -gt 0) { $got.M3 = $m; $true } else { $false }
    } 1400
    Check '시크 뒤 재생 → 시크 프레임의 번호가 이어져 정지로 나옴' $ok "시크 뒤 번호=[$($afterSeek -join ' ')] → 번호='$($got.M3.Text)' 위치=$([int]$range.Current.Value) ms"

    # 5) 정지 → 번호가 지워진다
    Click $stop
    $ok = WaitFor { (NameOf $status) -eq '정지됨' } 5000
    Check '정지 → 추적 번호 지움' ($ok -and [string]::IsNullOrEmpty($count.Current.ItemType)) "상태='$(NameOf $status)' 번호='$($count.Current.ItemType)'"
}
catch { Check '예외' $false $_.Exception.Message }
finally { Stop-DetectView $proc }

Complete-Test
