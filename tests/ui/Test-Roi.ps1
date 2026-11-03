# 관심 구역(ROI) 편집 UI 테스트 — 왼쪽 패널 '구역' 절 + 영상 위 RoiLayer (2026-11-03, 시안 안 2)
#   파일을 멈춘 채로 영상 위를 마우스로 눌러 구역을 그리고, 점을 끌고, 변 가운데 ○ 를 끌어 점을 넣고, Delete 로 점을 지우고,
#   유형을 바꾸고, 구역을 지운다. 바뀔 때마다 영상 옆 <영상>.roi.json 에 바로 저장되므로 그 파일을 읽어 점 좌표까지 확인한다.
#   마우스는 실제 입력(SetCursorPos + mouse_event)이라 이 테스트 동안 포인터가 움직인다 — 사용자가 PC 를 쓰는 중이면 흔들릴 수 있다.
#   그림 자체(색·모양)는 UIA 로 안 보여서 보지 않는다 — 그리기가 예외 없이 돌고 마우스가 구역에 닿는지만 본다.
#
#   1) 재생 중엔 편집이 막힌다 (추가·목록·지우기 꺼짐, 사유는 툴팁) — 구역 파일 이름은 보인다
#   2) 일시정지 → 추가 → 점 4개 → 첫 점을 눌러 닫기: 목록 'Z1 차로'·개수·파일(점 4개)
#   3) 점 끌기 / 변 가운데 ○ 끌기(점이 5개) / Delete(점이 4개로) — 매번 파일의 좌표로 확인
#   4) 유형 바꾸기 → 파일·목록 이름이 바뀜
#   5) 그리다가 Esc · 재생하면 그리던 것을 버림
#   6) 구역 지우기 → 목록·파일이 비고 파일이 지워짐
#   7) 다시 그려 두고(번호는 Z2 — 지운 번호를 안 씀) 정지 → 패널이 비고 편집이 막힘 → 같은 영상을 다시 열면 구역이 돌아옴
#   8) 깨진 구역 파일: 상태바에 사유, 빈 구역으로 시작, 옛 파일은 .bad 로 남김
#
#   pwsh tests\ui\Test-Roi.ps1                              # bin\Debug 빌드
#   pwsh tests\ui\Test-Roi.ps1 -ExeDir publish      # 배포본
param(
    [string]$ExeDir,
    [string]$Video,        # 생략 시 publish\test4.mp4
    [int]$FrameW = 1280,   # 영상 해상도 (화면 위 좌표를 프레임 0~1 좌표로 옮기는 데 쓴다)
    [int]$FrameH = 720
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
if (-not $Video) { $Video = Join-Path $RepoRoot 'publish\test4.mp4' }
if (-not (Test-Path $Video)) { throw "테스트 영상이 없습니다: $Video" }
$roiFile = "$Video.roi.json"

# ---- 마우스·키보드 도우미 ----
function Move-Mouse([double]$X, [double]$Y) { $null = $In::SetCursorPos([int][Math]::Round($X), [int][Math]::Round($Y)); Start-Sleep -Milliseconds 40 }
function Click-At([double]$X, [double]$Y, [switch]$Right) {
    Move-Mouse $X $Y
    $down, $up = if ($Right) { 0x0008, 0x0010 } else { 0x0002, 0x0004 }
    $In::mouse_event($down, 0, 0, 0, [UIntPtr]::Zero); Start-Sleep -Milliseconds 30
    $In::mouse_event($up, 0, 0, 0, [UIntPtr]::Zero); Start-Sleep -Milliseconds 120
}
# 누른 채 여러 걸음으로 옮겼다 놓는다 (한 번에 건너뛰면 MouseMove 가 한 번뿐이다)
function Drag-Mouse([double]$X0, [double]$Y0, [double]$X1, [double]$Y1) {
    Move-Mouse $X0 $Y0
    $In::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero); Start-Sleep -Milliseconds 60
    for ($i = 1; $i -le 8; $i++) { Move-Mouse ($X0 + ($X1 - $X0) * $i / 8) ($Y0 + ($Y1 - $Y0) * $i / 8) }
    $In::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero); Start-Sleep -Milliseconds 150
}
function Press-Key([byte]$Vk) { $In::keybd_event($Vk, 0, 0, [UIntPtr]::Zero); $In::keybd_event($Vk, 0, 2, [UIntPtr]::Zero); Start-Sleep -Milliseconds 150 }
$VK_DELETE = 0x2E; $VK_ESCAPE = 0x1B

# ---- 구역 상태 읽기 ----
function Get-ZoneNames($List) {
    @($List.FindAll($Tree::Descendants, (New-Object $PC ($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem))) | ForEach-Object { $_.Current.Name })
}
function Read-RoiFile {
    if (-not (Test-Path $roiFile)) { return $null }
    try { Get-Content -Raw -Encoding utf8 $roiFile | ConvertFrom-Json } catch { $null }
}
function Select-ComboItem($Combo, [string]$Name) {
    $ec = $Combo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
    $ec.Expand()
    $found = @{}
    if (-not (WaitFor { $found.Item = ByName $Combo $Name; $null -ne $found.Item } 3000)) { $ec.Collapse(); return $false }
    $found.Item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Start-Sleep -Milliseconds 100
    $ec.Collapse()
    return $true
}
function Get-ComboSelection($Combo) {
    $sel = @($Combo.GetCurrentPattern([System.Windows.Automation.SelectionPattern]::Pattern).Current.GetSelection())
    if ($sel.Count -eq 0) { '' } else { $sel[0].Current.Name }
}
# 영상 칸(RoiLayer 의 UIA 영역) 안에서 프레임이 Uniform 으로 가운데 놓인다 — RoiLayer 와 같은 식으로 프레임 0~1 좌표를 화면 좌표로 옮긴다
function Get-FrameMap($Layer) {
    $r = $Layer.Current.BoundingRectangle
    if ($r.IsEmpty -or $r.Width -le 0) { throw '구역 편집 영역(ZoneLayer)의 화면 영역을 읽지 못함' }
    $s = [Math]::Min($r.Width / $FrameW, $r.Height / $FrameH)
    $w = $FrameW * $s; $h = $FrameH * $s
    @{ Ox = $r.X + ($r.Width - $w) / 2; Oy = $r.Y + ($r.Height - $h) / 2; W = $w; H = $h }
}
function To-Screen($Map, [double]$Nx, [double]$Ny) { , @(($Map.Ox + $Nx * $Map.W), ($Map.Oy + $Ny * $Map.H)) }
function Near($P, [double]$Nx, [double]$Ny, [double]$Tol = 0.012) { [Math]::Abs($P[0] - $Nx) -le $Tol -and [Math]::Abs($P[1] - $Ny) -le $Tol }

Remove-Item $roiFile, "$roiFile.bad", "$roiFile.tmp" -ErrorAction SilentlyContinue
$proc = $null
try {
    $proc = Start-DetectView $ExeDir
    $root = $AE::FromHandle($proc.MainWindowHandle)

    $play    = ById $root 'Btn_PlayPause'
    $stop    = ById $root 'Btn_Stop'
    $status  = ById $root 'Txt_Status'
    $layer   = ById $root 'ZoneLayer'
    $add     = ById $root 'Btn_ZoneAdd'
    $del     = ById $root 'Btn_ZoneDelete'
    $list    = ById $root 'ZoneList'
    $kind    = ById $root 'ZoneKindSelector'
    $zcount  = ById $root 'Txt_ZoneCount'
    $got = @{}

    # 시작 — 소스가 없으면 편집할 수 없고 사유는 툴팁에
    Check '소스 없음: 구역 편집이 막힘' (-not $add.Current.IsEnabled -and -not $list.Current.IsEnabled -and -not $kind.Current.IsEnabled -and $add.Current.HelpText -like '*파일을 열면*') "추가 툴팁='$($add.Current.HelpText)'"

    Open-VideoFile $root $Video
    $ok = WaitFor { (NameOf $status) -like '파일:*' -and (NameOf $play) -eq '일시정지' } 10000
    Check '파일 재생 시작' $ok "상태='$(NameOf $status)'"

    # 1) 재생 중엔 편집이 막힌다
    Check '재생 중: 추가·목록·유형이 꺼지고 사유는 툴팁에' (-not $add.Current.IsEnabled -and -not $list.Current.IsEnabled -and -not $kind.Current.IsEnabled -and $add.Current.HelpText -like '*일시정지*') "추가 툴팁='$($add.Current.HelpText)'"
    $null = WaitFor { (NameOf $zcount) -like 'V*' } 5000   # 첫 화면을 알아보는 데 잠깐 걸린다 ('뷰 확인 중' → 'V1 · 0개')
    Check '구역 개수가 보이고 툴팁에 구역 파일 경로' ($zcount.Current.HelpText -eq "구역 파일: $roiFile" -and (NameOf $zcount) -eq 'V1 · 0개') "툴팁='$($zcount.Current.HelpText)' 개수='$(NameOf $zcount)'"

    # 2) 일시정지 → 구역 그리기
    Click $play
    $ok = WaitFor { (NameOf $play) -eq '재생' -and $add.Current.IsEnabled } 3000
    Check '일시정지: 추가가 켜짐 (목록·지우기·유형은 구역을 고를 때까지 꺼짐)' ($ok -and $list.Current.IsEnabled -and -not $del.Current.IsEnabled -and -not $kind.Current.IsEnabled) "추가=$($add.Current.IsEnabled) 목록=$($list.Current.IsEnabled)"

    $null = $In::SetForegroundWindow($proc.MainWindowHandle)
    Start-Sleep -Milliseconds 400
    $fg = $In::GetForegroundWindow()
    Check '앱 창이 맨 앞 (마우스 입력이 앱으로 간다)' ($fg -eq $proc.MainWindowHandle) "앞 창=$fg 앱=$($proc.MainWindowHandle)"
    $map = Get-FrameMap $layer
    Note '영상 사각형(화면)' ("{0:N0},{1:N0} {2:N0}x{3:N0}" -f $map.Ox, $map.Oy, $map.W, $map.H)

    $quad = @(@(0.30, 0.80), @(0.60, 0.80), @(0.55, 0.40), @(0.35, 0.40))
    Click $add
    Start-Sleep -Milliseconds 200
    foreach ($q in $quad) { $s = To-Screen $map $q[0] $q[1]; Click-At $s[0] $s[1] }
    $s = To-Screen $map $quad[0][0] $quad[0][1]
    Click-At $s[0] $s[1]   # 첫 점을 눌러 닫기
    $ok = WaitFor { (Get-ZoneNames $list) -contains 'Z1 차로' } 3000
    Check '점 4개 + 첫 점 클릭 → 목록에 Z1 차로' $ok "목록=[$((Get-ZoneNames $list) -join ', ')] 개수='$(NameOf $zcount)'"
    Check '새 구역이 선택됨: 지우기·유형이 켜지고 유형 콤보가 차로' ($del.Current.IsEnabled -and $kind.Current.IsEnabled -and (Get-ComboSelection $kind) -eq '차로' -and (NameOf $zcount) -eq 'V1 · 1개') "유형='$(Get-ComboSelection $kind)' 개수='$(NameOf $zcount)'"

    $ok = WaitFor { $j = Read-RoiFile; $null -ne $j -and @($j.zones).Count -eq 1 } 3000
    $j = Read-RoiFile
    $pts = if ($j) { @($j.zones[0].points) } else { @() }
    $okPts = $pts.Count -eq 4
    for ($i = 0; $okPts -and $i -lt 4; $i++) { $okPts = Near $pts[$i] $quad[$i][0] $quad[$i][1] }
    Check '구역 파일에 바로 저장: lane · 점 4개가 찍은 자리' ($ok -and $j.zones[0].kind -eq 'lane' -and $okPts) "kind=$($j.zones[0].kind) 점=$($pts.Count) nextId=$($j.nextId)"

    # 3) 점 끌기 → 변 가운데 ○ 끌기(점 추가) → Delete(점 지우기)
    $a = To-Screen $map 0.30 0.80; $b = To-Screen $map 0.25 0.85
    Drag-Mouse $a[0] $a[1] $b[0] $b[1]
    $ok = WaitFor { $j = Read-RoiFile; $null -ne $j -and (Near @($j.zones[0].points)[0] 0.25 0.85) } 3000
    Check '점 끌기: 첫 점이 끈 자리로 (저장됨)' $ok "첫 점=$((Read-RoiFile).zones[0].points[0] -join ',')"

    $m = To-Screen $map ((0.25 + 0.60) / 2) ((0.85 + 0.80) / 2)   # 변 0(점 0 → 점 1)의 가운데 ○
    $t = To-Screen $map 0.43 0.95
    Drag-Mouse $m[0] $m[1] $t[0] $t[1]
    $ok = WaitFor { $j = Read-RoiFile; $null -ne $j -and @($j.zones[0].points).Count -eq 5 } 3000
    $j = Read-RoiFile
    Check '변 가운데 ○ 끌기: 점이 하나 늘고(5개) 새 점이 끈 자리' ($ok -and (Near @($j.zones[0].points)[1] 0.43 0.95)) "점=$(@($j.zones[0].points).Count) 새 점=$(@($j.zones[0].points)[1] -join ',')"

    Press-Key $VK_DELETE   # 방금 끈 점이 선택돼 있다
    $ok = WaitFor { $j = Read-RoiFile; $null -ne $j -and @($j.zones[0].points).Count -eq 4 } 3000
    Check 'Delete: 고른 점이 지워져 다시 4개' $ok "점=$(@((Read-RoiFile).zones[0].points).Count)"

    Press-Key $VK_DELETE   # 고른 점이 없으니 아무 일도 없어야 한다 (구역을 지우지 않는다)
    Start-Sleep -Milliseconds 300
    Check '점을 안 고르고 Delete: 구역·점이 그대로' ((Get-ZoneNames $list) -contains 'Z1 차로' -and @((Read-RoiFile).zones[0].points).Count -eq 4) "목록=[$((Get-ZoneNames $list) -join ', ')]"

    # 4) 유형 바꾸기
    $ok = Select-ComboItem $kind '갓길'
    $ok2 = WaitFor { $j = Read-RoiFile; $null -ne $j -and $j.zones[0].kind -eq 'shoulder' -and (Get-ZoneNames $list) -contains 'Z1 갓길' } 3000
    Check '유형을 갓길로: 파일 kind·목록 이름이 바뀜' ($ok -and $ok2) "kind=$((Read-RoiFile).zones[0].kind) 목록=[$((Get-ZoneNames $list) -join ', ')] 콤보='$(Get-ComboSelection $kind)'"

    # 5) 그리다가 Esc · 재생
    Click $add
    $s = To-Screen $map 0.10 0.20; Click-At $s[0] $s[1]
    $s = To-Screen $map 0.20 0.20; Click-At $s[0] $s[1]
    Press-Key $VK_ESCAPE
    Start-Sleep -Milliseconds 300
    Check '그리다가 Esc: 새 구역이 안 생김 (목록·파일 그대로)' ((Get-ZoneNames $list).Count -eq 1 -and @((Read-RoiFile).zones).Count -eq 1 -and $add.Current.IsEnabled) "목록=[$((Get-ZoneNames $list) -join ', ')]"

    Click $add
    $s = To-Screen $map 0.10 0.20; Click-At $s[0] $s[1]
    Click $play
    $ok = WaitFor { (NameOf $play) -eq '일시정지' -and -not $add.Current.IsEnabled } 3000
    Check '그리는 중 재생: 편집이 막히고 그리던 점은 버려짐' ($ok -and (Get-ZoneNames $list).Count -eq 1 -and @((Read-RoiFile).zones).Count -eq 1) "추가=$($add.Current.IsEnabled) 목록 $((Get-ZoneNames $list).Count)개"
    Click $play
    $null = WaitFor { (NameOf $play) -eq '재생' -and $add.Current.IsEnabled } 3000

    # 6) 구역 지우기 → 목록·파일이 빔 (파일은 지워진다)
    $null = ($list.FindFirst($Tree::Descendants, (New-Object $PC ($AE::NameProperty, 'Z1 갓길')))).GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    $ok = WaitFor { $del.Current.IsEnabled } 2000
    Click $del
    $ok2 = WaitFor { (Get-ZoneNames $list).Count -eq 0 -and (NameOf $zcount) -eq 'V1 · 0개' -and -not (Test-Path $roiFile) } 3000
    Check '지우기: 목록이 비고 개수 0개, 구역 파일이 지워짐' ($ok -and $ok2) "목록 $((Get-ZoneNames $list).Count)개 개수='$(NameOf $zcount)' 파일=$(Test-Path $roiFile)"

    # 7) 다시 그려 두고 정지 → 패널이 빔 → 같은 영상을 다시 열면 구역이 돌아온다
    $tri = @(@(0.20, 0.70), @(0.50, 0.70), @(0.35, 0.30))
    Click $add
    Start-Sleep -Milliseconds 200
    foreach ($q in $tri) { $s = To-Screen $map $q[0] $q[1]; Click-At $s[0] $s[1] }
    Press-Key 0x0D   # Enter 로 닫기
    # 번호는 지운 Z1 을 다시 쓰지 않고(Z2), 유형은 마지막으로 쓴 갓길을 기억한다 ([추가] 가 만드는 구역의 유형)
    $ok = WaitFor { (Get-ZoneNames $list) -contains 'Z2 갓길' -and (Test-Path $roiFile) } 3000
    $j = Read-RoiFile
    Check 'Enter 로 닫기: 점 3개 구역이 생기고 저장됨 — 번호는 지운 Z1 을 다시 쓰지 않고(Z2) 유형은 마지막에 쓴 갓길' ($ok -and @($j.zones[0].points).Count -eq 3 -and $j.zones[0].id -eq 2 -and $j.nextId -eq 3 -and $j.zones[0].kind -eq 'shoulder') "목록=[$((Get-ZoneNames $list) -join ', ')] nextId=$($j.nextId)"

    Click $stop
    $ok = WaitFor { (NameOf $status) -eq '정지됨' } 5000
    Check '정지: 구역 목록·개수·파일 경로가 비고 편집이 막힘' ($ok -and (Get-ZoneNames $list).Count -eq 0 -and (NameOf $zcount) -eq '' -and $zcount.Current.HelpText -eq '' -and -not $add.Current.IsEnabled -and $add.Current.HelpText -like '*파일을 열면*') "목록 $((Get-ZoneNames $list).Count)개 개수='$(NameOf $zcount)' 툴팁='$($zcount.Current.HelpText)'"
    Check '정지해도 구역 파일은 영상 옆에 남음' (Test-Path $roiFile) ''

    Open-VideoFile $root $Video
    $ok = WaitFor { (NameOf $status) -like '파일:*' } 10000
    $ok2 = WaitFor { (Get-ZoneNames $list) -contains 'Z2 갓길' -and (NameOf $zcount) -eq 'V1 · 1개' } 3000
    Check '같은 영상을 다시 열면 구역이 돌아옴' ($ok -and $ok2) "목록=[$((Get-ZoneNames $list) -join ', ')] 개수='$(NameOf $zcount)'"

    # 8) 깨진 구역 파일
    Click $stop
    $null = WaitFor { (NameOf $status) -eq '정지됨' } 5000
    [IO.File]::WriteAllText($roiFile, '{ 깨진 구역 파일')
    Open-VideoFile $root $Video
    $ok = WaitFor { (NameOf $status) -like '구역 파일을 읽지 못했습니다*' } 10000
    $null = WaitFor { (NameOf $zcount) -eq 'V1 · 0개' } 5000
    Check '깨진 구역 파일: 상태바에 사유, 빈 구역으로 시작' ($ok -and (Get-ZoneNames $list).Count -eq 0 -and (NameOf $zcount) -eq 'V1 · 0개') "상태='$(NameOf $status)' 목록 $((Get-ZoneNames $list).Count)개"
    Check '옛 파일은 .bad 로 복사해 둠' ((Test-Path "$roiFile.bad") -and (Get-Content -Raw "$roiFile.bad").Contains('깨진')) ''
}
catch { Check '예외' $false $_.Exception.Message }
finally {
    Stop-DetectView $proc
    Remove-Item $roiFile, "$roiFile.bad", "$roiFile.tmp" -ErrorAction SilentlyContinue   # 영상 옆에 파일을 남기지 않는다
}

Complete-Test
