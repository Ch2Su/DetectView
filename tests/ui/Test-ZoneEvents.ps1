# 구역 진입·나감 이벤트 로그 UI 테스트 — 영상 아래 접이식 로그와 [CSV 저장] (2026-11-03, ZoneEvents.cs · MainWindow.Events.cs)
#   test4.mp4 옆에 구역 파일(<영상>.roi.json — 차로 구역 Z1 하나, 0~10.5초에 멈춰 선 덤프트럭의 발 위치를 품는다)을 미리 써 두고 연다.
#   로그 항목의 UIA 이름은 "00:11.2 CAR #40 Z1 차로 진입" 이고 헤더 버튼 글자는 "이벤트 N건"이다.
#   멈춰 선 트럭은 0.5초 안에 처음 보여 '이미 안에 있음'으로 남는다. 그 밖에 구역을 지나가는 차는 영상에 따라 달라서 항목 수는 세지 않고 성질만 본다.
#
#   1) 접어 둔 패널(헤더 한 줄, 목록 안 보임) → 펼치면 목록이 보임
#   2) 재생 3초 안에 멈춘 트럭이 'Z1 차로 이미 안에 있음' 으로 기록됨
#   3) 도는 구간(10.5~11.4초)을 지나면 '화면 전환 → 새 뷰 V2 (구역 없음)' 표시가 남고, 그 뒤로는 이벤트가 없다 (구역은 V1 의 것 — 새 뷰엔 구역이 없다)
#   4) 헤더 개수 = 표시를 뺀 항목 수
#   5) [CSV 저장]: 영상 옆 <영상>.events.csv — 헤더·행 수·initial 행·view_changed 행, 상태바 문구
#   6) 시크하면 감지가 다시 시작해 멈춘 트럭이 다시 기록됨 (멈췄던 이벤트도 풀림)
#   7) [비우기]: 목록·개수가 비고 저장이 꺼짐
#   8) 구역이 없는 영상(test1)은 이벤트 0건 · 정지하면 패널이 사라짐
#
#   pwsh tests\ui\Test-ZoneEvents.ps1                              # bin\Debug 빌드
#   pwsh tests\ui\Test-ZoneEvents.ps1 -ExeDir publish_gpu_ver      # 배포본
param(
    [string]$ExeDir,
    [string]$Video,       # 생략 시 publish_gpu_ver\test4.mp4
    [string]$PlainVideo   # 구역 없는 영상. 생략 시 publish_gpu_ver\test1.mp4
)
. (Join-Path $PSScriptRoot 'Common.ps1')

$ExeDir = Resolve-ExeDir $ExeDir
if (-not $Video) { $Video = Join-Path $RepoRoot 'publish_gpu_ver\test4.mp4' }
if (-not $PlainVideo) { $PlainVideo = Join-Path $RepoRoot 'publish_gpu_ver\test1.mp4' }
foreach ($v in $Video, $PlainVideo) { if (-not (Test-Path $v)) { throw "테스트 영상이 없습니다: $v" } }
$roiFile = "$Video.roi.json"
$csvFile = "$Video.events.csv"
$plainRoi = "$PlainVideo.roi.json"
if (Test-Path $plainRoi) { throw "구역 없는 영상 옆에 구역 파일이 있습니다: $plainRoi" }   # 이 테스트의 전제 — 남의 파일을 지우지 않는다
if ((Test-Path $roiFile) -or (Test-Path $csvFile)) { throw "테스트 영상 옆에 구역·이벤트 파일이 이미 있습니다 (지우고 다시): $roiFile / $csvFile" }

# 멈춘 덤프트럭(발 위치 ≈ 0.42, 0.52)을 품는 차로 구역 하나 — 파일 이름·형식은 Roi.cs
'{ "version": 1, "nextId": 2, "zones": [ { "id": 1, "kind": "lane", "points": [[0.30, 0.45], [0.55, 0.45], [0.55, 0.62], [0.30, 0.62]] } ] }' |
    Set-Content -Encoding utf8 $roiFile

# 로그 항목 이름 "mm:ss.f ..." → (시각 ms, 이름)
function Get-LogItems($List) {
    @($List.FindAll($Tree::Descendants, (New-Object $PC ($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem))) | ForEach-Object {
        $n = $_.Current.Name
        $ms = if ($n -match '^(\d+):(\d\d)\.(\d) ') { ([int]$Matches[1] * 60 + [int]$Matches[2]) * 1000 + [int]$Matches[3] * 100 } else { -1 }
        [pscustomobject]@{ Ms = $ms; Name = $n; Marker = ($n -like '*화면 전환 →*') }
    })
}
function Header-Count($Toggle) { if ((NameOf $Toggle) -match '^이벤트 (\d+)건$') { [int]$Matches[1] } else { -1 } }

$proc = $null
try {
    $proc = Start-DetectView $ExeDir
    $root = $AE::FromHandle($proc.MainWindowHandle)
    $play   = ById $root 'Btn_PlayPause'
    $stop   = ById $root 'Btn_Stop'
    $status = ById $root 'Txt_Status'
    $range  = (ById $root 'VideoSlider').GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern)

    Check '소스 없음: 이벤트 패널이 안 보임' ($null -eq (ById $root 'Btn_EventToggle')) ''

    Open-VideoFile $root $Video
    $ok = WaitFor { (NameOf $status) -like '파일:*' -and (NameOf $play) -eq '일시정지' } 10000
    Check '파일 재생 시작' $ok "상태='$(NameOf $status)'"
    $toggle = ById $root 'Btn_EventToggle'
    $save   = ById $root 'Btn_EventSave'
    $clear  = ById $root 'Btn_EventClear'
    Check '영상을 열면 이벤트 패널이 보임 (접힌 채 헤더 한 줄)' ($null -ne $toggle -and (NameOf $toggle) -match '^이벤트 \d+건$' -and $null -eq (ById $root 'EventList')) "헤더='$(if ($toggle) { NameOf $toggle })'"

    # 2) 멈춘 트럭이 기록된다 — 접어 둔 채 개수가 늘 때까지 기다린 뒤 펼쳐 읽는다
    $null = WaitFor { $range.Current.Value -ge 3000 -and (Header-Count $toggle) -ge 1 } 15000
    Click $toggle
    $ok = WaitFor { $null -ne (ById $root 'EventList') } 3000
    Check '펼치면 목록이 보임' $ok "헤더='$(NameOf $toggle)'"
    $list = ById $root 'EventList'
    $items = Get-LogItems $list
    # 처음 1초 안에 '이미 안에 있음' 으로 잡힌 트랙 중 도는 구간(10.5초) 전까지 나가지 않은 것 = 멈춰 선 트럭 (같이 잡힌 지나가던 차는 곧 나간다)
    $initial = @($items | Where-Object { $_.Name -match '^\d\d:\d\d\.\d (CAR|TRUCK) #(\d+) Z1 차로 이미 안에 있음$' -and $_.Ms -le 1000 })
    $stays = @($initial | Where-Object { $id = [regex]::Match($_.Name, '#(\d+)').Groups[1].Value; @($items | Where-Object { $_.Name -match "#$id Z1 차로 나감" -and $_.Ms -lt 10500 }).Count -eq 0 })
    Check '멈춘 트럭이 Z1 차로 이미 안에 있음으로 기록되고 도는 구간까지 나가지 않음' ($initial.Count -ge 1 -and $stays.Count -ge 1) "처음 1초 안 '이미 안에 있음' $($initial.Count)건, 그중 남은 것 $($stays.Count)건: $((($items | Select-Object -Last 4).Name) -join ' | ')"

    # 3) 도는 구간을 지나면 표시가 남고 그 뒤 이벤트는 없다
    $null = WaitFor { $range.Current.Value -ge 13500 } 15000
    $items = Get-LogItems $list
    $markers = @($items | Where-Object { $_.Marker })
    Check '카메라가 돈 뒤 화면 전환 표시가 한 번 남음' ($markers.Count -eq 1 -and $markers[0].Ms -ge 10500 -and $markers[0].Ms -le 12500) "표시 $($markers.Count)개 $(if ($markers.Count) { "@$($markers[0].Ms) ms" })"
    $after = @($items | Where-Object { -not $_.Marker -and $markers.Count -and $_.Ms -gt $markers[0].Ms + 100 })
    Check '표시 뒤로는 이벤트가 없음 (구역이 그려진 뷰인지 알 수 없어 멈춤)' ($after.Count -eq 0) "표시 뒤 이벤트 $($after.Count)개 $((($after | Select-Object -First 3).Name) -join ' | ')"
    $real = @($items | Where-Object { -not $_.Marker })
    Check '헤더 개수 = 표시를 뺀 항목 수' ((Header-Count $toggle) -eq $real.Count) "헤더='$(NameOf $toggle)' 항목 $($items.Count)개(표시 $($markers.Count))"

    # 5) CSV 저장
    Click $play
    $null = WaitFor { (NameOf $play) -eq '재생' } 3000
    $expect = Header-Count $toggle
    Click $save
    $ok = WaitFor { Test-Path $csvFile } 3000
    $lines = if ($ok) { @(Get-Content $csvFile -Encoding utf8) } else { @() }
    $ok2 = WaitFor { (NameOf $status) -like '이벤트 저장:*' } 3000
    Check 'CSV 저장: 영상 옆 events.csv, 헤더와 행 수' ($ok -and $lines.Count -ge 2 -and $lines[0] -eq 'ms,time,event,track,label,zone,zone_kind,dwell_ms,note' -and $lines.Count -eq $items.Count + 1) "줄 $($lines.Count) (항목 $($items.Count) + 헤더) · $((Split-Path $csvFile -Leaf))"
    Check 'CSV 내용: initial 행(Z1 · lane)과 view_changed 행' (@($lines | Where-Object { $_ -match '^\d+,[\d:.]+,initial,\d+,(car|truck),Z1,lane,' }).Count -ge 1 -and @($lines | Where-Object { $_ -match ',view_changed,' }).Count -eq 1) "initial $(@($lines | Where-Object { $_ -match ',initial,' }).Count)행 · view_changed $(@($lines | Where-Object { $_ -match ',view_changed,' }).Count)행"
    Check '저장 뒤 상태바 문구' ($ok2 -and (NameOf $status) -match "^이벤트 저장: test4\.mp4\.events\.csv \(${expect}건\)$") "상태='$(NameOf $status)'"

    # 6) 시크하면 감지가 다시 시작한다 (멈췄던 것도 풀림)
    $before = (Get-LogItems $list).Count
    Click $play
    $range.SetValue(5000)
    $ok = WaitFor { @(Get-LogItems $list | Where-Object { -not $_.Marker -and $_.Ms -ge 4900 -and $_.Ms -lt 6500 -and $_.Name -like '*이미 안에 있음' }).Count -ge 1 } 8000
    Check '시크(5초) 뒤 멈춘 트럭이 다시 기록됨 (이벤트가 풀림)' $ok "항목 $before → $((Get-LogItems $list).Count)개 · 새 항목: $((((Get-LogItems $list) | Select-Object -First 3).Name) -join ' | ')"

    # 7) 비우기 — 멈춘 채로 (재생 중엔 비운 직후에도 이벤트가 쌓여 0건을 볼 수 없다)
    Click $play
    $null = WaitFor { (NameOf $play) -eq '재생' } 3000
    Click $clear
    $ok = WaitFor { (Header-Count $toggle) -eq 0 -and (Get-LogItems $list).Count -eq 0 } 3000
    Check '비우기: 목록·개수가 비고 CSV 저장이 꺼짐' ($ok -and -not $save.Current.IsEnabled -and -not $clear.Current.IsEnabled) "헤더='$(NameOf $toggle)' 저장 켜짐=$($save.Current.IsEnabled)"
    Click $toggle
    $ok = WaitFor { $null -eq (ById $root 'EventList') } 3000
    Check '접으면 목록이 사라짐' $ok ''

    # 8) 구역 없는 영상 · 정지
    Click $stop
    $null = WaitFor { (NameOf $status) -eq '정지됨' } 5000
    Check '정지: 이벤트 패널이 사라짐' ($null -eq (ById $root 'Btn_EventToggle')) ''
    Open-VideoFile $root $PlainVideo
    $ok = WaitFor { (NameOf $status) -like '파일:test1*' -or (NameOf $status) -eq '파일: test1.mp4' } 10000
    $toggle = ById $root 'Btn_EventToggle'
    $null = WaitFor { $range.Current.Value -ge 4000 } 15000
    Check '구역 없는 영상: 4초 재생해도 이벤트 0건' ($ok -and (Header-Count $toggle) -eq 0 -and -not $save.Current.IsEnabled) "상태='$(NameOf $status)' 헤더='$(NameOf $toggle)'"
}
catch { Check '예외' $false $_.Exception.Message }
finally {
    Stop-DetectView $proc
    Remove-Item $roiFile, "$roiFile.bad", "$roiFile.tmp", $csvFile -ErrorAction SilentlyContinue   # 영상 옆에 파일을 남기지 않는다
}

Complete-Test
