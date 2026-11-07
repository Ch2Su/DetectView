# VLM 설명 UI 테스트 — 유고 후보 행에 VLM 단서·한 줄 설명 (2026-11-07, MainWindow.Vlm.cs · docs/vlm-annotation.md)
#   진짜 Ollama 없이 Debug 빌드의 가짜 VLM(TestFaults.cs — 환경 변수 DETECTVIEW_TEST_FAULTS)으로 돈다. test4.mp4 옆에 Test-Incident 와 같은 구역 파일
#   (차로 Z1 — 0~10.5초에 고장 정차한 덤프트럭의 발 위치를 품는다)을 써 두면 약 5.2초에 유고 후보가 선언된다.
#
#   1) [VLM 설명] 은 기본 꺼짐(UIA ItemStatus '꺼짐'), 툴팁에 '참고용'
#   2) vlm-fake: 켜면 '켜짐' + 상태바 → 재생 중 유고 후보 행이 '분석 중'으로 들어왔다가 0.6초 뒤
#      '단서: 출동 차량, 차로 위 사람, 앞에 선 차 — 시험용 가짜 VLM 답 — 그림 512×512'. 그림이 긴 변 512 = 실험과 같은 크롭이 VLM 에 갔다.
#      행의 UIA 이름은 Test-Incident 와 같고(VLM 줄은 이름에 안 들어감), 행 툴팁(HelpText)에 모델·설명 전문·'참고용'
#   3) CSV: 헤더 끝 vlm,vlm_cues,vlm_desc — incident 행이 done · 단서 키 셋 · 설명
#   4) 끄면 '꺼짐' + 상태바 (끝난 행의 설명은 남는다). 비우고 시크해 다시 선언된 유고 후보 행엔 VLM 줄이 없다(ItemStatus 빈칸)
#   5) vlm-down: 켜지지만 유고 후보 행은 '실패 — Ollama 연결 안 됨 …'
#   6) vlm-absent: 켜기가 실패해 '꺼짐' 그대로, 상태바에 사유
#   Release(배포본)엔 가짜 VLM 이 없어 1) 만 본다 — 진짜 Ollama 로는 손으로 본다(docs/vlm-annotation.md)
#
#   pwsh tests\ui\Test-VlmAnnotation.ps1                  # bin\Debug 빌드
#   pwsh tests\ui\Test-VlmAnnotation.ps1 -ExeDir publish  # 배포본 (1 만)
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

$IsDebugBuild = $ExeDir -like '*\bin\Debug\*'
$OrigFaults = $env:DETECTVIEW_TEST_FAULTS
$veh = '(CAR|TRUCK|BUS|MOTORCYCLE|SCOOTER|BICYCLE)'   # 차량 6종 — 로그 라벨은 그 순간의 클래스라 같은 차라도 바뀔 수 있다 (Test-Incident 머리말 4)
$IncPattern = "^\d\d:\d\d\.\d ⚠ $veh #(\d+) Z1 차로 유고 후보 — 차로 정차 \(높음\)$"
$FakeStatus = '단서: 출동 차량, 차로 위 사람, 앞에 선 차 — 시험용 가짜 VLM 답 — 그림 512×512'

# 결함(가짜 VLM)을 건 채 앱을 띄운다 — 환경 변수는 띄우는 순간만 바꾸고 되돌린다 (Test-ScreenHud 와 같은 방식)
function Start-WithFault([string]$Fault) {
    if ($Fault) { $env:DETECTVIEW_TEST_FAULTS = $Fault } else { Remove-Item Env:DETECTVIEW_TEST_FAULTS -ErrorAction SilentlyContinue }
    try { Start-DetectView $ExeDir }
    finally {
        if ($OrigFaults) { $env:DETECTVIEW_TEST_FAULTS = $OrigFaults } else { Remove-Item Env:DETECTVIEW_TEST_FAULTS -ErrorAction SilentlyContinue }
    }
}

# 앱을 띄워 영상을 열고 로그를 펼쳐 둔다 (펼쳐야 행이 UIA 에 보인다). 영상은 열자마자 재생된다 — 유고 후보는 약 5.2초
function Open-Session([string]$Fault) {
    $proc = Start-WithFault $Fault
    $root = $AE::FromHandle($proc.MainWindowHandle)
    $s = @{ Proc = $proc; Root = $root; Play = (ById $root 'Btn_PlayPause'); Status = (ById $root 'Txt_Status')
            Range = (ById $root 'VideoSlider').GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern) }
    Open-VideoFile $root $Video
    $ok = WaitFor { (NameOf $s.Status) -like '파일:*' -and (NameOf $s.Play) -eq '일시정지' } 10000
    Check "[$(if ($Fault) { $Fault } else { '결함 없음' })] 파일 재생 시작 (구역 파일을 읽음)" $ok "상태='$(NameOf $s.Status)'"
    $s.Vlm = ById $root 'Btn_EventVlm'   # 이벤트 로그 패널은 파일이 열려 있을 때만 보인다 — 열기 전엔 UIA 에 없다
    Click (ById $root 'Btn_EventToggle')
    $null = WaitFor { $null -ne (ById $root 'EventList') } 3000
    $s.List = ById $root 'EventList'
    $s
}

function Get-IncidentRows($List) {
    @($List.FindAll($Tree::Descendants, (New-Object $PC ($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem))) |
      Where-Object { $_.Current.Name -match $IncPattern })
}
function Status-Of($El) { try { $El.Current.ItemStatus } catch { $null } }
function Help-Of($El) { try { $El.Current.HelpText } catch { $null } }

# 유고 후보 행이 나타난 뒤 그 행의 ItemStatus 가 바뀌는 차례를 모은다 (같은 값이 이어지면 한 번) — 끝난 값(단서·실패)이 나오면 멈춘다
function Watch-IncidentStatus($S, [int]$TimeoutMs) {
    $seen = [System.Collections.Generic.List[string]]::new()
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $TimeoutMs) {
        $row = Get-IncidentRows $S.List | Select-Object -First 1
        if ($row) {
            $st = Status-Of $row
            if ($null -ne $st -and ($seen.Count -eq 0 -or $seen[-1] -ne $st)) { $seen.Add($st) }
            if ($st -like '단서:*' -or $st -like '실패*') { break }
        }
        Start-Sleep -Milliseconds 30
    }
    , $seen
}

function Pause-Playback($S) {
    if ((NameOf $S.Play) -eq '일시정지') { Click $S.Play; $null = WaitFor { (NameOf $S.Play) -eq '재생' } 3000 }
}

$sess = $null
try {
    # ---- 1) 기본 꺼짐 (배포본도) — 2)~4) vlm-fake 는 Debug 만 ----
    $sess = Open-Session ($(if ($IsDebugBuild) { 'vlm-fake' } else { '' }))
    $vlm = $sess.Vlm
    Check '[VLM 설명] 버튼이 있고 기본 꺼짐' ($null -ne $vlm -and (NameOf $vlm) -eq 'VLM 설명' -and (Status-Of $vlm) -eq '꺼짐') "이름='$(if ($vlm) { NameOf $vlm })' · 상태='$(if ($vlm) { Status-Of $vlm })'"
    Check '[VLM 설명] 툴팁: 로컬 VLM · 참고용 · 판정은 바꾸지 않음' ((Help-Of $vlm) -like '*참고용*' -and (Help-Of $vlm) -like '*판정은 바꾸지 않습니다*') "툴팁='$(Help-Of $vlm)'"

    if (-not $IsDebugBuild) {
        Note '가짜 VLM' "Debug 빌드가 아니라 건너뜀 — 가짜 VLM(TestFaults.cs)은 Release 에선 컴파일되지 않는다 ($ExeDir)"
    }
    else {
        # 2) 켜기 → 유고 후보 행: 분석 중 → 단서·설명
        Click $vlm
        $ok = WaitFor { (Status-Of $vlm) -eq '켜짐' } 3000
        Check '켜면 켜짐 + 상태바 안내' ($ok -and (NameOf $sess.Status) -like 'VLM 설명 켜짐*') "상태='$(Status-Of $vlm)' · 상태바='$(NameOf $sess.Status)'"
        $seen = Watch-IncidentStatus $sess 15000
        Check '유고 후보 행이 분석 중으로 들어옴' ($seen.Count -ge 1 -and $seen[0] -eq '분석 중') "차례: $($seen -join ' → ')"
        Check '0.6초 뒤 단서·설명이 붙음 (그림 512×512 — 실험과 같은 크롭)' ($seen.Count -ge 1 -and $seen[-1] -eq $FakeStatus) "마지막='$(if ($seen.Count) { $seen[-1] })'"
        Pause-Playback $sess
        $rows = Get-IncidentRows $sess.List
        $help = if ($rows.Count) { Help-Of $rows[0] } else { '' }
        Check '행의 UIA 이름은 그대로 (VLM 줄은 이름에 없음)' ($rows.Count -eq 1) "행 $($rows.Count)개 · $($rows | ForEach-Object { $_.Current.Name })"
        Check '행 툴팁: 모델 · 설명 전문 · 참고용' ($help -like 'VLM 설명 · 가짜 VLM (시험용) · *초 — 참고용*' -and $help -like '*시험용 가짜 VLM 답 — 그림 512×512*') "툴팁='$help'"

        # 3) CSV
        Click (ById $sess.Root 'Btn_EventSave')
        $ok = WaitFor { Test-Path $csvFile } 3000
        $lines = if ($ok) { @(Get-Content $csvFile -Encoding utf8) } else { @() }
        $incCsv = @($lines | Where-Object { $_ -match ',incident,' })
        Check 'CSV: 헤더 끝에 vlm,vlm_cues,vlm_desc' ($lines.Count -ge 2 -and $lines[0] -eq 'ms,time,event,track,label,zone,zone_kind,dwell_ms,note,vlm,vlm_cues,vlm_desc') "헤더='$(if ($lines.Count) { $lines[0] })'"
        Check 'CSV: incident 행이 done · 단서 키 셋 · 설명' ($incCsv.Count -eq 1 -and $incCsv[0] -match ',Z1,lane,\d+,,done,emergency_vehicle_near;people_on_road;vehicles_stopped_ahead,시험용 가짜 VLM 답 — 그림 512×512$') "$(if ($incCsv.Count) { $incCsv[0] })"
        Remove-Item $csvFile -ErrorAction SilentlyContinue

        # 4) 끄기 → 끝난 행은 남고, 그 뒤 새 유고 후보엔 VLM 줄이 없다
        Click $vlm
        $ok = WaitFor { (Status-Of $vlm) -eq '꺼짐' } 3000
        Check '끄면 꺼짐 + 상태바 (모델을 내림)' ($ok -and (NameOf $sess.Status) -like 'VLM 설명 꺼짐*') "상태='$(Status-Of $vlm)' · 상태바='$(NameOf $sess.Status)'"
        $rows = Get-IncidentRows $sess.List
        Check '끈 뒤에도 끝난 행의 설명은 남음' ($rows.Count -eq 1 -and (Status-Of $rows[0]) -eq $FakeStatus) "상태='$(if ($rows.Count) { Status-Of $rows[0] })'"
        Click (ById $sess.Root 'Btn_EventClear')
        $null = WaitFor { (Get-IncidentRows $sess.List).Count -eq 0 } 3000
        $sess.Range.SetValue(2000)
        Click $sess.Play
        $ok = WaitFor { (Get-IncidentRows $sess.List).Count -ge 1 } 12000
        Start-Sleep -Milliseconds 1500   # 켜져 있었다면 분석 중 → 끝(0.6초)까지 넉넉히
        $rows = Get-IncidentRows $sess.List
        Check '끈 뒤 새 유고 후보 행엔 VLM 줄이 없음' ($ok -and $rows.Count -ge 1 -and (Status-Of $rows[0]) -eq '' -and -not (Help-Of $rows[0])) "행 $($rows.Count)개 · 상태='$(if ($rows.Count) { Status-Of $rows[0] })'"
        Stop-DetectView $sess.Proc
        $sess = $null

        # 5) vlm-down — 켜지지만 부를 때마다 실패
        $sess = Open-Session 'vlm-down'
        Click $sess.Vlm
        $ok = WaitFor { (Status-Of $sess.Vlm) -eq '켜짐' } 3000
        $seen = Watch-IncidentStatus $sess 15000
        Check '[vlm-down] 켜지고, 유고 후보 행은 실패 — 연결 안 됨' ($ok -and $seen.Count -ge 1 -and $seen[-1] -like '실패 — Ollama 연결 안 됨 (시험용 결함*') "차례: $($seen -join ' → ')"
        $rows = Get-IncidentRows $sess.List
        Check '[vlm-down] 실패 행 툴팁: 다음 후보 때 다시 시도' ($rows.Count -eq 1 -and (Help-Of $rows[0]) -like 'VLM 실패 — *다시 시도*') "툴팁='$(if ($rows.Count) { Help-Of $rows[0] })'"
        Stop-DetectView $sess.Proc
        $sess = $null

        # 6) vlm-absent — 켜기가 실패해 꺼진 채
        $sess = Open-Session 'vlm-absent'
        Click $sess.Vlm
        $ok = WaitFor { (NameOf $sess.Status) -like 'VLM 설명을 켜지 못함*' } 3000
        Check '[vlm-absent] 켜지 못하고 꺼진 채, 상태바에 사유' ($ok -and (Status-Of $sess.Vlm) -eq '꺼짐' -and (NameOf $sess.Status) -like '*Ollama 가 꺼져 있음*') "상태='$(Status-Of $sess.Vlm)' · 상태바='$(NameOf $sess.Status)'"
    }
}
catch { Check '예외' $false $_.Exception.Message }
finally {
    if ($sess) { Stop-DetectView $sess.Proc }
    if ($OrigFaults) { $env:DETECTVIEW_TEST_FAULTS = $OrigFaults } else { Remove-Item Env:DETECTVIEW_TEST_FAULTS -ErrorAction SilentlyContinue }
    Remove-Item $roiFile, "$roiFile.bad", "$roiFile.tmp", $csvFile -ErrorAction SilentlyContinue   # 영상 옆에 파일을 남기지 않는다
}

Complete-Test
