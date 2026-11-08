# 유고 규칙 UI 테스트 — 차로에 멈춘 차량이 정지로 5초 이어지면 유고 후보 (2026-11-03, IncidentRules.cs · MainWindow.Events.cs)
#   test4.mp4 옆에 구역 파일(차로 Z1 하나 — 0~10.5초에 차로 한가운데 고장 정차한 덤프트럭의 발 위치를 품는다)을 써 두고 연다.
#   추적기가 멈춘 물체를 '정지'로 판정하고(관측이 짧으면 0.2초부터) 그 위에 5초가 더해져 약 5.2초에 유고 후보가 선언된다. 그 뒤 10.5초에 카메라가 돌아 화면이 전환된다.
#   재생하면서 0.03초 간격으로 위치·헤더의 유고 표시(Txt_EventIncident — 없으면 UIA 에서 사라짐)·박스 번호(검출 수 칸 UIA ItemType 의 '정지 유고')를 기록한다.
#
#   1) 선언 전(5초 전): 유고 표시·박스 강조 없음 (멈춘 트럭에 '정지'만)
#   2) 선언 뒤(약 5.2초): 헤더에 '⚠ 유고 후보 1', 그 차의 박스 번호에 '유고'가 붙음 — 같은 번호가 '정지'이기도 하다
#   3) 로그(펼침): '⚠ TRUCK #14 Z1 차로 유고 후보 — 차로 정차 (높음)' 행 (5~9초 사이)
#   4) 카메라가 돈 뒤(11.7초~): 유고 표시·박스 강조가 사라지고 로그에 그 차(같은 번호)의 '유고 해소 (화면 전환)' 행 — 열린 채 남지 않는다
#      로그 라벨은 그 순간의 클래스라 같은 차가 TRUCK → BUS 처럼 바뀔 수 있다(추적기는 차량 7종을 한 묶음으로 본다) — 라벨이 아니라 번호로 맞춘다.
#      2026-11-05 배포 모델(09-17)에 쌍선형 입력을 넣자 카메라가 돌기 시작하는 10.5초의 덤프트럭이 BUS 로 잡혀 'BUS #10 … 유고 해소' 가 됐다
#   5) CSV: incident 행(Z1 · lane · 멈춤 5000ms 이상)과 incident_end 행
#   6) 시크(2초): 그 위치에서 다시 세어 8초 안에 유고 후보가 다시 선언되고, 선언 중에 다시 시크하면 '유고 해소 (시크)' 행
#
#   pwsh tests\ui\Test-Incident.ps1                              # bin\Debug 빌드
#   pwsh tests\ui\Test-Incident.ps1 -ExeDir publish      # 배포본
param(
    [string]$ExeDir,
    [string]$Video   # 생략 시 publish\test4.mp4
)
. (Join-Path $PSScriptRoot 'Common.ps1')

$ExeDir = Resolve-ExeDir $ExeDir
if (-not $Video) { $Video = Join-Path $RepoRoot 'publish\test4.mp4' }
if (-not (Test-Path $Video)) { throw "테스트 영상이 없습니다: $Video" }
$roiFile = "$Video.roi.json"
$csvFile = "$Video.events.csv"
if ((Test-Path $roiFile) -or (Test-Path $csvFile)) { throw "테스트 영상 옆에 구역·이벤트 파일이 이미 있습니다 (지우고 다시): $roiFile / $csvFile" }

'{ "version": 1, "nextId": 2, "zones": [ { "id": 1, "kind": "lane", "points": [[0.30, 0.45], [0.55, 0.45], [0.55, 0.62], [0.30, 0.62]] } ] }' |
    Set-Content -Encoding utf8 $roiFile

$INC = '⚠ 유고 후보 1'

function Sample-Playback($Root, $Range, $Count, [double]$FromMs, [double]$ToMs, [int]$TimeoutMs) {
    if ($FromMs -gt 0) { $Range.SetValue($FromMs) }
    $samples = [System.Collections.Generic.List[object]]::new()
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $TimeoutMs) {
        $i1 = ById $Root 'Txt_EventIncident'
        $ind1 = if ($i1) { try { $i1.Current.Name } catch { $null } } else { '' }
        $pos = $Range.Current.Value
        $marks = Get-TrackMarks $Count
        $i2 = ById $Root 'Txt_EventIncident'
        $ind2 = if ($i2) { try { $i2.Current.Name } catch { $null } } else { '' }
        if ($null -ne $marks -and $null -ne $ind1 -and $ind1 -eq $ind2) {
            $samples.Add([pscustomobject]@{ Pos = $pos; Indicator = $ind1; Ids = @($marks.Ids); Stationary = @($marks.Stationary); Incidents = @($marks.Incidents) })
        }
        if ($pos -ge $ToMs) { break }
        Start-Sleep -Milliseconds 30
    }
    , $samples
}
function Get-LogItems($List) {
    @($List.FindAll($Tree::Descendants, (New-Object $PC ($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem))) | ForEach-Object {
        $n = $_.Current.Name
        $ms = if ($n -match '^(\d+):(\d\d)\.(\d) ') { ([int]$Matches[1] * 60 + [int]$Matches[2]) * 1000 + [int]$Matches[3] * 100 } else { -1 }
        [pscustomobject]@{ Ms = $ms; Name = $n }
    })
}

$proc = $null
try {
    $proc = Start-DetectView $ExeDir
    $root = $AE::FromHandle($proc.MainWindowHandle)
    $play   = ById $root 'Btn_PlayPause'
    $status = ById $root 'Txt_Status'
    $count  = ById $root 'Txt_DetectionCount'
    $range  = (ById $root 'VideoSlider').GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern)

    Open-VideoFile $root $Video
    $ok = WaitFor { (NameOf $status) -like '파일:*' -and (NameOf $play) -eq '일시정지' } 10000
    Check '파일 재생 시작 (구역 파일을 읽음)' $ok "상태='$(NameOf $status)'"

    $s = Sample-Playback $root $range $count 0 12600 30000
    Check '표본을 충분히 모음' ($s.Count -ge 80 -and $s[-1].Pos -ge 12500) "표본 $($s.Count)개 · 마지막 위치 $([int]$s[-1].Pos) ms"

    # 1) 선언 전 — 정지는 붙지만 유고는 아직
    $early = @($s | Where-Object { $_.Pos -ge 1500 -and $_.Pos -lt 4800 })
    Check '5초 전: 유고 표시·박스 강조 없음' ($early.Count -ge 10 -and @($early | Where-Object { $_.Indicator -ne '' -or $_.Incidents.Count -gt 0 }).Count -eq 0) "표본 $($early.Count)개 · 유고 있던 표본 $(@($early | Where-Object { $_.Indicator -ne '' -or $_.Incidents.Count -gt 0 }).Count)개"
    Check '멈춘 트럭엔 정지 번호가 붙어 있음' (@($early | Where-Object { $_.Stationary.Count -gt 0 }).Count -gt 0) "정지 표본 $(@($early | Where-Object { $_.Stationary.Count -gt 0 }).Count)/$($early.Count)"

    # 2) 선언 뒤 — 헤더 표시와 박스 강조
    $during = @($s | Where-Object { $_.Pos -ge 7000 -and $_.Pos -lt 10300 })
    $withInd = @($during | Where-Object { $_.Indicator -eq $INC })
    $withBox = @($during | Where-Object { $_.Incidents.Count -gt 0 })
    $incId = if ($withBox.Count) { $withBox[0].Incidents[0] } else { 0 }
    $boxAlsoStill = @($withBox | Where-Object { $_.Stationary -contains $incId })
    Check '선언 뒤(7~10.3초): 헤더에 ⚠ 유고 후보 1' ($during.Count -ge 10 -and $withInd.Count -ge [Math]::Floor($during.Count * 0.9)) "표본 $($during.Count)개 중 $($withInd.Count)개 · 표시='$($withInd[0].Indicator)'"
    Check '선언 뒤: 멈춘 트럭의 박스 번호에 유고가 붙음 (같은 번호가 정지이기도 함)' ($withBox.Count -ge [Math]::Floor($during.Count * 0.9) -and $boxAlsoStill.Count -eq $withBox.Count) "유고 번호 #$incId · 박스 강조 표본 $($withBox.Count)/$($during.Count)"
    $firstAt = @($s | Where-Object { $_.Indicator -eq $INC } | Select-Object -First 1)
    Check '선언 시각이 약 5.2초 (정지 판정이 0.2초쯤에 나오고 그 위에 5초)' ($firstAt.Count -eq 1 -and $firstAt[0].Pos -ge 5000 -and $firstAt[0].Pos -le 7000) "처음 보인 위치 $(if ($firstAt.Count) { [int]$firstAt[0].Pos }) ms"

    # 4) 카메라가 돈 뒤 — 표시·강조가 사라진다
    $after = @($s | Where-Object { $_.Pos -ge 11700 })
    Check '카메라가 돈 뒤(11.7초~): 유고 표시·박스 강조가 사라짐' ($after.Count -ge 8 -and @($after | Where-Object { $_.Indicator -ne '' -or $_.Incidents.Count -gt 0 }).Count -eq 0) "표본 $($after.Count)개 · 남은 표본 $(@($after | Where-Object { $_.Indicator -ne '' -or $_.Incidents.Count -gt 0 }).Count)개"

    # 3)·4) 로그
    Click $play
    $null = WaitFor { (NameOf $play) -eq '재생' } 3000
    $toggle = ById $root 'Btn_EventToggle'
    Click $toggle
    $null = WaitFor { $null -ne (ById $root 'EventList') } 3000
    $list = ById $root 'EventList'
    $items = Get-LogItems $list
    $veh = '(CAR|TRUCK|BUS|MOTORCYCLE|SCOOTER|BICYCLE|PICKUP)'   # 차량 7종 — 로그 라벨은 그 순간의 클래스라 같은 차라도 바뀔 수 있다(머리말 4)
    $incRow = @($items | Where-Object { $_.Name -match "^\d\d:\d\d\.\d ⚠ $veh #(\d+) Z1 차로 유고 후보 — 차로 정차 \(높음\)$" })
    Check '로그: 유고 후보 행 한 줄 (5~9초 사이)' ($incRow.Count -eq 1 -and $incRow[0].Ms -ge 5000 -and $incRow[0].Ms -le 9000) "행 $($incRow.Count)개 · $($incRow | ForEach-Object { $_.Name })"
    $incNo = if ($incRow.Count -eq 1 -and $incRow[0].Name -match ' #(\d+) ') { $Matches[1] } else { 'x' }
    $endRow = @($items | Where-Object { $_.Name -match "^\d\d:\d\d\.\d $veh #$incNo Z1 차로 유고 해소 \(화면 전환\) · 멈춤 \d+\.\d초$" })
    # 실패하면 해소 사유가 달랐는지(추적 끝 등)·번호가 달랐는지·목록에서 안 보였는지 가르게 '유고' 가 든 행을 모두 보인다
    $incLines = @($items | Where-Object { $_.Name -like '*유고*' } | ForEach-Object { $_.Name }) -join ' | '
    Check '로그: 화면 전환에서 그 차(같은 번호)의 유고 해소 행이 남음 (열린 채 안 남는다)' ($endRow.Count -eq 1) "행 $($endRow.Count)개 · $($endRow | ForEach-Object { $_.Name })$(if ($endRow.Count -ne 1) { " — 목록 $($items.Count)행, 유고 행: $incLines" })"

    # 5) CSV
    Click (ById $root 'Btn_EventSave')
    $ok = WaitFor { Test-Path $csvFile } 3000
    $lines = if ($ok) { @(Get-Content $csvFile -Encoding utf8) } else { @() }
    # 끝의 빈 열 넷 — note · vlm · vlm_cues · vlm_desc (VLM 설명은 꺼져 있다 — Test-VlmAnnotation)
    $incCsv = @($lines | Where-Object { $_ -match '^\d+,[\d:.]+,incident,\d+,(car|truck|bus|motorcycle|scooter|bicycle|pickup),Z1,lane,(\d+),,,,$' })
    $dwellOk = $incCsv.Count -eq 1 -and [int]($incCsv[0] -split ',')[7] -ge 5000
    Check 'CSV: incident 행(Z1 · lane · 멈춤 5000ms 이상)과 incident_end 행' ($ok -and $dwellOk -and @($lines | Where-Object { $_ -match ',incident_end,' }).Count -eq 1) "incident $($incCsv.Count)행 · incident_end $(@($lines | Where-Object { $_ -match ',incident_end,' }).Count)행 · $(if ($incCsv.Count) { $incCsv[0] })"

    # 6) 시크: 다시 세어 선언하고, 선언 중에 다시 시크하면 '시크' 해소 행이 남는다
    Click (ById $root 'Btn_EventClear')
    $null = WaitFor { (Get-LogItems $list).Count -eq 0 } 3000
    Click $play
    $s2 = Sample-Playback $root $range $count 2000 9300 20000
    $declared = @($s2 | Where-Object { $_.Indicator -eq $INC })
    Check '시크(2초) 뒤 다시 세어 8초 안에 유고 후보가 다시 선언됨' ($declared.Count -gt 0 -and $declared[0].Pos -ge 7000 -and $declared[0].Pos -le 9300) "처음 보인 위치 $(if ($declared.Count) { [int]$declared[0].Pos }) ms · 표본 $($s2.Count)개"
    $range.SetValue(2000)   # 선언돼 있는 중에 시크
    $ok = WaitFor { @(Get-LogItems $list | Where-Object { $_.Name -like '*유고 해소 (시크)*' }).Count -eq 1 } 5000
    Check '선언 중에 시크하면 유고 해소 (시크) 행이 남음' $ok "로그: $((Get-LogItems $list | ForEach-Object { $_.Name }) -join ' | ')"
}
catch { Check '예외' $false $_.Exception.Message }
finally {
    Stop-DetectView $proc
    Remove-Item $roiFile, "$roiFile.bad", "$roiFile.tmp", $csvFile -ErrorAction SilentlyContinue   # 영상 옆에 파일을 남기지 않는다
}

Complete-Test
