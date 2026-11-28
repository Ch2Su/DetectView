# 자세 지켜보기 UI 테스트 — 화면 전환 감지(밝기 차이)가 못 잡는 천천히 한 줌아웃을 알아채 새 뷰로 넘어가는지 (2026-11-21, CameraPoseWatch.cs · MainWindow.Playback.cs)
#   publish\test6.mp4 (26분 터널 화재 현장 — docs/testing.md '테스트 영상 내용')의 1416~1418초 줌아웃(물체가 약 0.74배로 작아짐, 연속 프레임 차이 d 최대 3.4)으로 본다.
#   이 영상은 화면 네 귀퉁이에 고정 글자(지사·방향·카메라 이름)가 있어, 예전엔 줌아웃 뒤에도 그 글자만으로 원래 뷰(V1)로 알아봤다.
#   영상 옆에 구역 파일(판 1 — 차로 Z1 하나)을 써 두고 연다 → 처음 알아본 뷰(V1)의 구역이 된다.
#
#   1) 1405초로 시크(멈춘 채): 헤더 'V1 · 1개'
#   2) 재생 1405~1425초: 줌 전엔 헤더 V1 · 1개 · 배지 없음(헛경보 없음) → 줌 동안 '● 화면 이동 중' → '● 새 뷰 V2 · 구역 없음' → 헤더 'V2 · 0개'
#   3) 로그(CSV): 화면 전환 표시 '새 뷰 V2 (구역 없음)' 한 줄 (줌 시각)
#   4) 멈춘 채 1400초로 시크하면 V1 · 1개, 1440초로 시크하면 V2 · 0개 — 두 자세가 각자 자기 뷰의 기준 화면과 장면까지 더 많이 맞는지.
#      배운 화면 고정 글자를 빼는 일(마스크)은 이 단계로는 드러나지 않는다(마스크를 꺼도 통과 — 2026-11-22 검토) — TrackerCheck ⑬ '글자를 배운 뒤 처음 보는 자세'가 본다
#
#   publish\test6.mp4 가 없으면(저장소에 없는 2.2 GB 영상) [INFO] 로 건너뛴다
#
#   pwsh tests\ui\Test-CameraPose.ps1                              # bin\Debug 빌드
#   pwsh tests\ui\Test-CameraPose.ps1 -ExeDir publish      # 배포본
param(
    [string]$ExeDir,
    [string]$Video   # 생략 시 publish\test6.mp4
)
. (Join-Path $PSScriptRoot 'Common.ps1')

$ExeDir = Resolve-ExeDir $ExeDir
if (-not $Video) { $Video = Join-Path $RepoRoot 'publish\test6.mp4' }
if (-not (Test-Path $Video)) {
    Note '자세 지켜보기 건너뜀' "테스트 영상이 없음: $Video (docs/testing.md 'test6.mp4')"
    Complete-Test
    return
}
$Video = New-TestVideo $Video   # 임시 폴더의 하드 링크 — 구역 파일·CSV 를 원본 영상 옆이 아니라 거기에 쓰고 지운다 (Common.ps1 New-TestVideo) — 2.2 GB 라도 같은 드라이브면 하드 링크라 복사하지 않는다
$roiFile = "$Video.roi.json"
$csvFile = "$Video.events.csv"

function Get-Name($Root, [string]$Id) {
    $e = ById $Root $Id
    if ($e) { try { $e.Current.Name } catch { $null } } else { '' }
}
# 재생하며 0.03초 간격으로 표본: Pos · Badge('' 이면 없음) · Header(구역 칸). 값을 읽는 사이 프레임이 넘어갈 수 있어 위치 앞뒤로 읽어 같은 표본만 쓴다
function Sample-Playback($Root, $Range, [double]$ToMs, [int]$TimeoutMs) {
    $samples = [System.Collections.Generic.List[object]]::new()
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $TimeoutMs) {
        $b1 = Get-Name $Root 'Txt_SceneBadge'; $h1 = Get-Name $Root 'Txt_ZoneCount'
        $pos = $Range.Current.Value
        $b2 = Get-Name $Root 'Txt_SceneBadge'; $h2 = Get-Name $Root 'Txt_ZoneCount'
        if ($null -ne $b1 -and $b1 -eq $b2 -and $h1 -eq $h2) { $samples.Add([pscustomobject]@{ Pos = $pos; Badge = $b1; Header = $h1 }) }
        if ($pos -ge $ToMs) { break }
        Start-Sleep -Milliseconds 30
    }
    , $samples
}
function In-Range($S, [double]$From, [double]$To) { @($S | Where-Object { $_.Pos -ge $From -and $_.Pos -lt $To }) }
function Share($Set, [scriptblock]$Pred) { if ($Set.Count -eq 0) { 0 } else { @($Set | Where-Object $Pred).Count / $Set.Count } }
function Groups($Set, [string]$Prop) { (@($Set | Group-Object $Prop | ForEach-Object { "$(if ($_.Name) { $_.Name } else { '(없음)' })×$($_.Count)" }) -join ' | ') }

# 판 1 구역 파일 — 뷰 식별 전 형식. 막힌 방향 차로 쪽 사각형 하나 (모양은 상관없다 — 어느 뷰의 구역인지만 본다)
'{ "version": 1, "nextId": 2, "zones": [ { "id": 1, "kind": "lane", "points": [[0.45, 0.50], [0.62, 0.50], [0.70, 0.95], [0.42, 0.95]] } ] }' |
    Set-Content -Encoding utf8 $roiFile

$NEW2 = '● 새 뷰 V2 · 구역 없음'
$ZoomMs = 1416500   # 줌아웃이 일어나는 영상 시각(대략 — docs/testing.md: 1415.9~1417.8초, 자세 지켜보기 실측 1417.0초에 움직임)
$proc = $null
try {
    $proc = Start-DetectView $ExeDir
    $root = $proc.Root
    $play   = Need $root 'Btn_PlayPause'
    $stop   = Need $root 'Btn_Stop'
    $status = Need $root 'Txt_Status'
    $range  = (Need $root 'VideoSlider').GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern)

    Open-VideoFile $root $Video
    $ok = WaitFor { (NameOf $status) -like '파일:*' -and (NameOf $play) -eq '일시정지' } 10000
    Check '파일 재생 시작 (옛 형식 구역 파일을 읽음)' $ok "상태='$(NameOf $status)'"
    Click $play
    $null = WaitFor { (NameOf $play) -eq '재생' } 3000

    # 1) 줌 12초 전으로 시크 — 처음 알아본 뷰(V1)와 같은 자세라 V1 의 구역
    $range.SetValue(1405000)
    $ok = WaitFor { (Get-Name $root 'Txt_ZoneCount') -eq 'V1 · 1개' -and [Math]::Abs($range.Current.Value - 1405000) -lt 1000 } 8000
    Check '1405초로 시크(멈춘 채): 헤더 V1 · 1개' $ok "헤더='$(Get-Name $root 'Txt_ZoneCount')' 위치=$([int]$range.Current.Value) ms"

    # 2) 재생해 줌을 지난다
    Click $play
    $s = Sample-Playback $root $range 1425000 45000
    # 표본은 UIA 읽기가 느려 초당 5~6개쯤 모인다(2026-11-21 — 20초에 111개)
    Check '표본을 충분히 모음' ($s.Count -ge 60 -and $s[-1].Pos -ge 1424000) "표본 $($s.Count)개 · 마지막 위치 $([int]$s[-1].Pos) ms"

    $before = In-Range $s 1406000 ($ZoomMs - 300)
    Check '줌 전(1406~1416초): 헤더 V1 · 1개 · 배지 없음 — 서 있는 카메라를 움직임으로 보지 않음' ($before.Count -ge 30 -and (Share $before { $_.Header -eq 'V1 · 1개' }) -ge 0.95 -and (Share $before { $_.Badge -eq '' }) -ge 0.99) `
        "표본 $($before.Count)개 · 헤더 [$(Groups $before 'Header')] · 배지 [$(Groups $before 'Badge')]"
    # '화면 이동 중' 은 움직임을 본 확인부터 멈춘 것을 본 확인까지(0.5초 남짓)라 표본에 안 걸릴 수 있다 — 실패로 치지 않고 적어만 둔다
    $during = In-Range $s ($ZoomMs - 300) ($ZoomMs + 3000)
    Note '줌 동안 배지' "표본 $($during.Count)개 · [$(Groups $during 'Badge')] (● 화면 이동 중 = 추적기에 넣지 않는 동안, 0.5초 남짓)"
    $newSeen = @($s | Where-Object { $_.Badge -eq $NEW2 })
    $firstNew = if ($newSeen.Count) { $newSeen[0].Pos } else { -1 }
    Check '줌 뒤: 배지 ● 새 뷰 V2 · 구역 없음 (줌 0~3초 안)' ($newSeen.Count -ge 5 -and $firstNew -ge $ZoomMs -and $firstNew -le $ZoomMs + 3000) "처음 보인 위치 $([int]$firstNew) ms · 표본 $($newSeen.Count)개"
    $after = In-Range $s ($ZoomMs + 3500) 1425000
    Check '줌 뒤(1420~1425초): 헤더 V2 · 0개 — V1 의 구역을 줌아웃한 화면에 쓰지 않음' ($after.Count -ge 15 -and (Share $after { $_.Header -eq 'V2 · 0개' }) -ge 0.95) "표본 $($after.Count)개 · 헤더 [$(Groups $after 'Header')]"

    # 3) 로그 — 멈추고 [CSV 저장] 으로 읽는다
    Click $play
    $null = WaitFor { (NameOf $play) -eq '재생' } 3000
    Click (Need $root 'Btn_EventSave')
    $okCsv = WaitFor { Test-Path $csvFile } 3000
    $lines = if ($okCsv) { @(Get-Content $csvFile -Encoding utf8) } else { @() }
    $rows = @($lines | Select-Object -Skip 1 | ForEach-Object { $f = $_ -split ','; [pscustomobject]@{ Ms = [double]$f[0]; Event = $f[2]; Note = $f[8] } })
    $views = @($rows | Where-Object { $_.Event -eq 'view_changed' } | Sort-Object Ms)
    Check '로그(CSV): 화면 전환 표시 한 줄 — 새 뷰 V2 (구역 없음), 줌 시각' ($views.Count -eq 1 -and $views[0].Note -eq '새 뷰 V2 (구역 없음)' -and $views[0].Ms -ge $ZoomMs -and $views[0].Ms -le $ZoomMs + 3000) `
        "표시 $($views.Count)줄: $(($views | ForEach-Object { "$([int]$_.Ms)ms $($_.Note)" }) -join ' | ')"

    # 4) 멈춘 채 줌 앞뒤로 시크 — 각 자세가 자기 뷰로 (글자를 빼는 일 자체는 TrackerCheck ⑬ 이 본다 — 위 머리말)
    $range.SetValue(1400000)
    $ok = WaitFor { (Get-Name $root 'Txt_ZoneCount') -eq 'V1 · 1개' } 8000
    Check '1400초(줌 전)로 시크: 헤더 V1 · 1개' $ok "헤더='$(Get-Name $root 'Txt_ZoneCount')'"
    $range.SetValue(1440000)
    $ok = WaitFor { (Get-Name $root 'Txt_ZoneCount') -eq 'V2 · 0개' } 8000
    Check '1440초(줌 뒤)로 시크: 헤더 V2 · 0개' $ok "헤더='$(Get-Name $root 'Txt_ZoneCount')'"

    Click $stop
    $null = WaitFor { (NameOf $status) -eq '정지됨' } 5000
}
catch { Check '예외' $false (Format-Error $_) }
finally {
    Stop-DetectView $proc
    Remove-TestVideos
}
Complete-Test
