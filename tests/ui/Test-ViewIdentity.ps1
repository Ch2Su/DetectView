# 화면 식별(2b) UI 테스트 — 카메라가 돌아간 뒤 같은 화면(뷰)으로 돌아오면 알아보고, 구역은 그린 뷰에서만 보이고 쓰인다 (2026-11-03, ViewIdentity.cs · MainWindow.Roi.cs)
#   test4.mp4 의 조각을 하드 컷으로 이은 합성 영상 experiments\TrackerCheck\out\test4_revisit.mp4 (`TrackerCheck mkrevisit` 이 없으면 만든다 — 원본의 조각이라 저장소엔 없다)로 본다.
#   순서(영상 시각): A 0~8초(차로에 멈춘 덤프트럭) → B 8~14초 → A 14~22초(같은 화면·같은 멈춘 트럭, 차는 다름) → C 22~26초 → B 26~30초.
#   영상 옆에 구역 파일(판 1 — 뷰 식별 전 파일: 차로 Z1 하나, 트럭의 발 위치를 품음)을 써 두고 연다 → 처음 알아본 뷰(A = V1)의 구역이 된다.
#
#   1) 처음: 헤더 'V1 · 1개', 배지 없음 · 5초 지나 유고 후보 선언 (헤더 '⚠ 유고 후보 1')
#   2) B 로 컷: 배지 '● 새 뷰 V2 · 구역 없음', 헤더 'V2 · 0개', 유고 표시가 사라짐
#   3) A 로 돌아옴: 배지 '● 뷰 V1 · 아는 화면', 헤더 'V1 · 1개', 구역이 돌아오고 유고 후보가 다시 선언됨
#   4) C: 새 뷰 V3 · B 로 다시: '● 뷰 V2 · 아는 화면'
#   5) 로그: 화면 전환 표시 네 줄(새 뷰 V2 · 뷰 V1 · 구역 1개 · 새 뷰 V3 · 뷰 V2), 유고 후보 둘·해소(화면 전환) 둘
#   6) 파일에 저장 → 다시 열기: B 에서 실제 마우스로 구역 하나를 그리면 파일(판 2)에 뷰 목록 셋과 구역의 view 가 적히고,
#      다시 열면 같은 뷰로 알아본다 — 구역은 그린 뷰(V2)에서만 보이고 V1 에선 안 보인다 (저장한 기준 화면 JPEG 로 알아봄)
#
#   pwsh tests\ui\Test-ViewIdentity.ps1                              # bin\Debug 빌드
#   pwsh tests\ui\Test-ViewIdentity.ps1 -ExeDir publish      # 배포본
param(
    [string]$ExeDir,
    [string]$Video   # 생략 시 experiments\TrackerCheck\out\test4_revisit.mp4 (없으면 만든다)
)
. (Join-Path $PSScriptRoot 'Common.ps1')

if (-not ('DetectViewTest.Input' -as [type])) {
    Add-Type -Namespace DetectViewTest -Name Input -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
[DllImport("user32.dll")] public static extern void mouse_event(uint flags, int dx, int dy, uint data, UIntPtr extra);
[DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
[DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
[DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
'@
}
$In = [DetectViewTest.Input]

$ExeDir = Resolve-ExeDir $ExeDir
if (-not $Video) {
    $Video = Join-Path $RepoRoot 'experiments\TrackerCheck\out\test4_revisit.mp4'
    if (-not (Test-Path $Video)) {
        if (-not (Test-Path (Join-Path $RepoRoot 'publish\test4.mp4'))) { throw "테스트 영상이 없습니다: publish\test4.mp4 (합성 영상의 원본)" }
        & dotnet run --project (Join-Path $RepoRoot 'experiments\TrackerCheck') -- mkrevisit $Video | Out-Host
    }
}
if (-not (Test-Path $Video)) { throw "테스트 영상이 없습니다: $Video" }
$roiFile = "$Video.roi.json"
$csvFile = "$Video.events.csv"
if ((Test-Path $roiFile) -or (Test-Path $csvFile)) { throw "테스트 영상 옆에 구역·이벤트 파일이 이미 있습니다 (지우고 다시): $roiFile / $csvFile" }
$FrameW = 1280; $FrameH = 720

function Move-Mouse([double]$X, [double]$Y) { $null = $In::SetCursorPos([int][Math]::Round($X), [int][Math]::Round($Y)); Start-Sleep -Milliseconds 40 }
function Click-At([double]$X, [double]$Y) {
    Move-Mouse $X $Y
    $In::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero); Start-Sleep -Milliseconds 30
    $In::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero); Start-Sleep -Milliseconds 120
}
# 영상 칸(RoiLayer 의 UIA 영역) 안에서 프레임이 Uniform 으로 가운데 놓인다 — 프레임 0~1 좌표를 화면 좌표로 옮긴다
function Get-FrameMap($Layer) {
    $r = $Layer.Current.BoundingRectangle
    if ($r.IsEmpty -or $r.Width -le 0) { throw '구역 편집 영역(ZoneLayer)의 화면 영역을 읽지 못함' }
    $s = [Math]::Min($r.Width / $FrameW, $r.Height / $FrameH)
    $w = $FrameW * $s; $h = $FrameH * $s
    @{ Ox = $r.X + ($r.Width - $w) / 2; Oy = $r.Y + ($r.Height - $h) / 2; W = $w; H = $h }
}
function To-Screen($Map, [double]$Nx, [double]$Ny) { , @(($Map.Ox + $Nx * $Map.W), ($Map.Oy + $Ny * $Map.H)) }

function Get-ZoneNames($List) {
    @($List.FindAll($Tree::Descendants, (New-Object $PC ($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem))) | ForEach-Object { $_.Current.Name })
}
function Read-RoiFile {
    if (-not (Test-Path $roiFile)) { return $null }
    try { Get-Content -Raw -Encoding utf8 $roiFile | ConvertFrom-Json } catch { $null }
}
function Get-Name($Root, [string]$Id) {
    $e = ById $Root $Id
    if ($e) { try { $e.Current.Name } catch { $null } } else { '' }
}

# 판 1 구역 파일 — 뷰 식별 전 형식 (view·views 가 없다)
'{ "version": 1, "nextId": 2, "zones": [ { "id": 1, "kind": "lane", "points": [[0.30, 0.45], [0.55, 0.45], [0.55, 0.62], [0.30, 0.62]] } ] }' |
    Set-Content -Encoding utf8 $roiFile

$INC = '⚠ 유고 후보 1'
$NEW2 = '● 새 뷰 V2 · 구역 없음'; $KNOWN1 = '● 뷰 V1 · 아는 화면'; $NEW3 = '● 새 뷰 V3 · 구역 없음'; $KNOWN2 = '● 뷰 V2 · 아는 화면'

# 처음부터 끝(또는 시간 제한)까지 재생하며 0.03초 간격으로 표본을 모은다. 표본: Pos · Badge('' 이면 없음) · Header(구역 칸) · Indicator(유고)
# 값을 읽는 사이 프레임이 넘어갈 수 있어 세 값을 위치 앞뒤로 읽어 같은 표본만 쓴다
function Sample-Playback($Root, $Range, [double]$ToMs, [int]$TimeoutMs) {
    $samples = [System.Collections.Generic.List[object]]::new()
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $TimeoutMs) {
        $b1 = Get-Name $Root 'Txt_SceneBadge'; $h1 = Get-Name $Root 'Txt_ZoneCount'; $i1 = Get-Name $Root 'Txt_EventIncident'
        $pos = $Range.Current.Value
        $b2 = Get-Name $Root 'Txt_SceneBadge'; $h2 = Get-Name $Root 'Txt_ZoneCount'; $i2 = Get-Name $Root 'Txt_EventIncident'
        if ($null -ne $b1 -and $b1 -eq $b2 -and $h1 -eq $h2 -and $i1 -eq $i2) {
            $samples.Add([pscustomobject]@{ Pos = $pos; Badge = $b1; Header = $h1; Indicator = $i1 })
        }
        if ($pos -ge $ToMs) { break }
        Start-Sleep -Milliseconds 30
    }
    , $samples
}
function In-Range($S, [double]$From, [double]$To) { @($S | Where-Object { $_.Pos -ge $From -and $_.Pos -lt $To }) }
function Share($Set, [scriptblock]$Pred) { if ($Set.Count -eq 0) { 0 } else { @($Set | Where-Object $Pred).Count / $Set.Count } }
$proc = $null
try {
    $proc = Start-DetectView $ExeDir
    $root = $AE::FromHandle($proc.MainWindowHandle)
    $play   = ById $root 'Btn_PlayPause'
    $stop   = ById $root 'Btn_Stop'
    $status = ById $root 'Txt_Status'
    $layer  = ById $root 'ZoneLayer'
    $list   = ById $root 'ZoneList'
    $add    = ById $root 'Btn_ZoneAdd'
    $range  = (ById $root 'VideoSlider').GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern)

    Open-VideoFile $root $Video
    $ok = WaitFor { (NameOf $status) -like '파일:*' -and (NameOf $play) -eq '일시정지' } 10000
    Check '파일 재생 시작 (옛 형식 구역 파일을 읽음)' $ok "상태='$(NameOf $status)'"

    $s = Sample-Playback $root $range 29800 60000
    Check '표본을 충분히 모음' ($s.Count -ge 100 -and $s[-1].Pos -ge 29000) "표본 $($s.Count)개 · 마지막 위치 $([int]$s[-1].Pos) ms"

    # 1) 처음 — 옛 구역이 처음 알아본 뷰(V1)의 것이 된다
    $a1 = In-Range $s 2000 7500
    Check '처음(A): 헤더 V1 · 1개, 알림 배지 없음 — 옛 구역이 처음 알아본 뷰의 것이 됨' ($a1.Count -ge 20 -and (Share $a1 { $_.Header -eq 'V1 · 1개' }) -ge 0.9 -and (Share $a1 { $_.Badge -eq '' }) -ge 0.99) "표본 $($a1.Count)개 · 헤더 $(@($a1 | Group-Object Header | ForEach-Object { "$($_.Name)×$($_.Count)" }) -join ', ') · 배지 [$((@($a1 | Group-Object Badge | ForEach-Object { $_.Name }) -join ' | '))]"
    $inc1 = In-Range $s 6800 7900
    Check '처음(A): 5초 넘게 멈춘 트럭이 유고 후보 (헤더 ⚠ 유고 후보 1)' ($inc1.Count -ge 5 -and (Share $inc1 { $_.Indicator -eq $INC }) -ge 0.8) "표본 $($inc1.Count)개 중 $(@($inc1 | Where-Object { $_.Indicator -eq $INC }).Count)개"

    # 2) B 로 컷 — 처음 보는 화면이라 새 뷰, 구역 없음, 유고 표시가 사라짐
    $b1 = In-Range $s 8600 10200
    Check 'B 로 컷: 배지 ● 새 뷰 V2 · 구역 없음' ((Share $b1 { $_.Badge -eq $NEW2 }) -ge 0.7 -and $b1.Count -ge 5) "표본 $($b1.Count)개 · 배지 [$((@($b1 | Group-Object Badge | ForEach-Object { "$($_.Name)×$($_.Count)" }) -join ' | '))]"
    $b1late = In-Range $s 9000 13800
    Check 'B: 헤더 V2 · 0개 (구역은 V1 의 것) · 유고 표시 없음' ($b1late.Count -ge 20 -and (Share $b1late { $_.Header -eq 'V2 · 0개' }) -ge 0.9 -and (Share $b1late { $_.Indicator -eq '' }) -ge 0.95) "표본 $($b1late.Count)개 · 헤더 $(@($b1late | Group-Object Header | ForEach-Object { "$($_.Name)×$($_.Count)" }) -join ', ')"
    $b1badgeOff = In-Range $s 10900 13800
    Check 'B: 알림 배지는 2초 뒤 사라짐' ($b1badgeOff.Count -ge 10 -and (Share $b1badgeOff { $_.Badge -eq '' }) -ge 0.95) "표본 $($b1badgeOff.Count)개 중 배지 있던 것 $(@($b1badgeOff | Where-Object { $_.Badge -ne '' }).Count)개"

    # 3) A 로 돌아옴 — 알아보고 구역이 돌아온다
    $a2 = In-Range $s 14600 16200
    Check 'A 로 돌아옴: 배지 ● 뷰 V1 · 아는 화면' ((Share $a2 { $_.Badge -eq $KNOWN1 }) -ge 0.7 -and $a2.Count -ge 5) "표본 $($a2.Count)개 · 배지 [$((@($a2 | Group-Object Badge | ForEach-Object { "$($_.Name)×$($_.Count)" }) -join ' | '))]"
    $a2late = In-Range $s 15000 21900
    Check 'A 로 돌아옴: 헤더 V1 · 1개 — 구역이 돌아옴' ($a2late.Count -ge 30 -and (Share $a2late { $_.Header -eq 'V1 · 1개' }) -ge 0.9) "표본 $($a2late.Count)개 · 헤더 $(@($a2late | Group-Object Header | ForEach-Object { "$($_.Name)×$($_.Count)" }) -join ', ')"
    $inc2 = In-Range $s 20800 21900
    Check '돌아온 뒤: 멈춘 트럭이 5초 넘게 서 있어 유고 후보가 다시 선언됨' ($inc2.Count -ge 5 -and (Share $inc2 { $_.Indicator -eq $INC }) -ge 0.8) "표본 $($inc2.Count)개 중 $(@($inc2 | Where-Object { $_.Indicator -eq $INC }).Count)개"

    # 4) C(새 뷰) · B 로 다시(아는 뷰)
    $c = In-Range $s 22600 24200
    Check 'C: 배지 ● 새 뷰 V3 · 구역 없음, 헤더 V3 · 0개' ((Share $c { $_.Badge -eq $NEW3 }) -ge 0.6 -and (Share (In-Range $s 23000 25900) { $_.Header -eq 'V3 · 0개' }) -ge 0.9) "배지 표본 $($c.Count)개 · 헤더 $(@((In-Range $s 23000 25900) | Group-Object Header | ForEach-Object { "$($_.Name)×$($_.Count)" }) -join ', ')"
    $b2 = In-Range $s 26600 28200
    Check 'B 로 다시: 배지 ● 뷰 V2 · 아는 화면, 헤더 V2 · 0개' ((Share $b2 { $_.Badge -eq $KNOWN2 }) -ge 0.6 -and (Share (In-Range $s 27000 29600) { $_.Header -eq 'V2 · 0개' }) -ge 0.9) "배지 표본 $($b2.Count)개 · 헤더 $(@((In-Range $s 27000 29600) | Group-Object Header | ForEach-Object { "$($_.Name)×$($_.Count)" }) -join ', ')"

    # 5) 로그 — 재생이 끝났으니 [CSV 저장] 으로 읽는다 (목록은 UIA 로 오래된 행이 안 보일 수 있다)
    $null = WaitFor { (NameOf $play) -eq '재생' } 8000
    Click (ById $root 'Btn_EventSave')
    $okCsv = WaitFor { Test-Path $csvFile } 3000
    $lines = if ($okCsv) { @(Get-Content $csvFile -Encoding utf8) } else { @() }
    $rows = @($lines | Select-Object -Skip 1 | ForEach-Object { $f = $_ -split ','; [pscustomobject]@{ Ms = [double]$f[0]; Event = $f[2]; Note = $f[8] } })
    $views = @($rows | Where-Object { $_.Event -eq 'view_changed' } | Sort-Object Ms)
    $expected = @('새 뷰 V2 (구역 없음)', '뷰 V1 · 구역 1개', '새 뷰 V3 (구역 없음)', '뷰 V2 (구역 없음)')
    $okViews = $views.Count -eq 4
    if ($okViews) { for ($k = 0; $k -lt 4; $k++) { if ($views[$k].Note -ne $expected[$k]) { $okViews = $false } } }
    Check '로그(CSV): 화면 전환 표시 네 줄 (새 뷰 V2 · 뷰 V1 · 새 뷰 V3 · 뷰 V2)' $okViews "표시 $($views.Count)줄: $(($views | ForEach-Object { "$([int]$_.Ms)ms $($_.Note)" }) -join ' | ')"
    $incRows = @($rows | Where-Object { $_.Event -eq 'incident' })
    $endRows = @($rows | Where-Object { $_.Event -eq 'incident_end' -and $_.Note -eq '화면 전환' })
    Check '로그(CSV): 유고 후보 두 번(A 를 볼 때마다) · 해소(화면 전환) 두 번' ($incRows.Count -eq 2 -and $endRows.Count -eq 2) "후보 $($incRows.Count)줄 @$(($incRows | ForEach-Object { [int]$_.Ms }) -join ',') · 해소 $($endRows.Count)줄 @$(($endRows | ForEach-Object { [int]$_.Ms }) -join ',')"

    # 6) 구역을 그려 저장 → 다시 열기: 뷰별로 구역이 갈린다
    $range.SetValue(28000)   # B(V2) 의 한 장
    $okV2 = WaitFor { (Get-Name $root 'Txt_ZoneCount') -eq 'V2 · 0개' -and $add.Current.IsEnabled } 5000
    Check '멈춘 채 B 의 한 장으로 시크: 헤더 V2 · 0개, 구역 편집이 켜짐' $okV2 "헤더='$(Get-Name $root 'Txt_ZoneCount')' 추가=$($add.Current.IsEnabled)"
    Click $add
    $map = Get-FrameMap $layer
    $quad = @(@(0.15, 0.55), @(0.85, 0.55), @(0.85, 0.85), @(0.15, 0.85))
    foreach ($q in $quad) { $p = To-Screen $map $q[0] $q[1]; Click-At $p[0] $p[1] }
    $p = To-Screen $map $quad[0][0] $quad[0][1]; Click-At $p[0] $p[1]   # 첫 점을 눌러 닫기
    $ok = WaitFor { (Get-ZoneNames $list) -contains 'Z2 차로' -and (Get-Name $root 'Txt_ZoneCount') -eq 'V2 · 1개' } 3000
    Check 'B 에 그린 구역: 목록에 Z2 차로 · 헤더 V2 · 1개 (V1 의 Z1 은 이 뷰에서 안 보임)' ($ok -and (Get-ZoneNames $list).Count -eq 1) "목록=[$((Get-ZoneNames $list) -join ', ')] 헤더='$(Get-Name $root 'Txt_ZoneCount')'"
    $null = WaitFor { $r = Read-RoiFile; $null -ne $r -and $r.version -eq 2 } 3000
    $j = Read-RoiFile
    $z1 = @(); $z2 = @()
    if ($j) { $z1 = @($j.zones | Where-Object { $_.id -eq 1 }); $z2 = @($j.zones | Where-Object { $_.id -eq 2 }) }
    function ViewOf($z) { if ($z.Count -gt 0) { $z[0].view } else { '(구역 없음)' } }
    Check '파일(판 2): 구역마다 view · 뷰 목록 셋(기준 화면) · nextView' ([bool]($j -and $j.version -eq 2 -and $z1.Count -eq 1 -and $z1[0].view -eq 1 -and $z2.Count -eq 1 -and $z2[0].view -eq 2 -and @($j.views).Count -eq 3 -and $j.nextView -ge 4 -and (@($j.views | Where-Object { $_.ref.Length -gt 1000 }).Count -eq 3))) `
        "version=$($j.version) Z1.view=$(ViewOf $z1) Z2.view=$(ViewOf $z2) views=$(@($j.views).Count) (ref $(@($j.views | ForEach-Object { $_.ref.Length }) -join '/')자) nextView=$($j.nextView)"

    Click $stop
    $null = WaitFor { (NameOf $status) -eq '정지됨' } 5000
    Open-VideoFile $root $Video
    $ok = WaitFor { (NameOf $status) -like '파일:*' -and (Get-Name $root 'Txt_ZoneCount') -eq 'V1 · 1개' } 10000
    Check '다시 열기: 처음 화면(A)을 저장한 기준 화면으로 알아봄 — 헤더 V1 · 1개, 목록 Z1' ($ok -and (Get-ZoneNames $list) -contains 'Z1 차로') "헤더='$(Get-Name $root 'Txt_ZoneCount')' 목록=[$((Get-ZoneNames $list) -join ', ')]"
    if ((NameOf $play) -eq '일시정지') { Click $play; $null = WaitFor { (NameOf $play) -eq '재생' } 3000 }

    $range.SetValue(11000)
    $ok = WaitFor { (Get-Name $root 'Txt_ZoneCount') -eq 'V2 · 1개' } 5000
    Check '11초(B)로 시크: 헤더 V2 · 1개, 목록 Z2 만 (그린 뷰에서만 보임)' ($ok -and (Get-ZoneNames $list).Count -eq 1 -and (Get-ZoneNames $list) -contains 'Z2 차로') "헤더='$(Get-Name $root 'Txt_ZoneCount')' 목록=[$((Get-ZoneNames $list) -join ', ')]"
    $range.SetValue(4000)
    $ok = WaitFor { (Get-Name $root 'Txt_ZoneCount') -eq 'V1 · 1개' } 5000
    Check '4초(A)로 시크: 헤더 V1 · 1개, 목록 Z1 만' ($ok -and (Get-ZoneNames $list).Count -eq 1 -and (Get-ZoneNames $list) -contains 'Z1 차로') "헤더='$(Get-Name $root 'Txt_ZoneCount')' 목록=[$((Get-ZoneNames $list) -join ', ')]"
    $range.SetValue(24000)
    $ok = WaitFor { (Get-Name $root 'Txt_ZoneCount') -eq 'V3 · 0개' } 5000
    Check '24초(C)로 시크: 헤더 V3 · 0개 (저장된 세 번째 뷰), 목록 비어 있음' ($ok -and (Get-ZoneNames $list).Count -eq 0) "헤더='$(Get-Name $root 'Txt_ZoneCount')' 목록 $((Get-ZoneNames $list).Count)개"

    Click $stop
    $null = WaitFor { (NameOf $status) -eq '정지됨' } 5000
    $okStop = WaitFor { (Get-Name $root 'Txt_ZoneCount') -eq '' } 3000
    Check '정지: 구역 헤더가 빔' $okStop "헤더='$(Get-Name $root 'Txt_ZoneCount')'"
}
finally {
    Stop-DetectView $proc
    Remove-Item $roiFile, "$roiFile.bad", "$roiFile.tmp", $csvFile -ErrorAction SilentlyContinue
}
Complete-Test
