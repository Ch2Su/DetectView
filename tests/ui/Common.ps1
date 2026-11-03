# DetectView UI 자동화 테스트 — 공용 도우미 (각 테스트 스크립트가 dot-source 한다)
#
# 다시 알아내기 번거로웠던 요령 (2026-09-23):
#  - WPF 버튼의 InvokePattern.Invoke() 는 클릭을 Dispatcher 로 넘기고 곧바로 돌아온다.
#    모달 대화상자를 여는 버튼이어도 호출 측이 막히지 않으므로, 결과는 화면 상태가 바뀌길 기다려 확인한다
#  - 모달 대화상자는 UIA 트리에서 바탕화면 루트가 아니라 소유 창(메인 창)의 자식으로 붙는다
#  - 파일 열기 대화상자 하단(파일 이름 1148 · 열기 1)은 UIA 에 패턴 없는 Pane 으로만 보이고,
#    열기 버튼은 창 핸들도 없다(0) → 파일 이름 Edit 에 WM_SETTEXT, 대화상자에 WM_COMMAND(IDOK)
#  - 대화상자 창이 먼저 뜨고 하단 컨트롤은 나중에 붙는다 → 생길 때까지 기다린다
#  - 다른 프로세스 컨트롤의 글자는 GetWindowText 로 못 읽는다 → WM_GETTEXT
#  - 도우미 이름을 Cls 로 짓지 말 것. Clear-Host 별칭이 함수보다 먼저 잡혀 "핸들이 잘못되었습니다" 가 난다

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes

$AE   = [System.Windows.Automation.AutomationElement]
$Tree = [System.Windows.Automation.TreeScope]
$PC   = [System.Windows.Automation.PropertyCondition]
$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path

# 결과는 참조형에 모은다 (dot-source 된 함수에서 스코프 한정자 없이 고칠 수 있게)
$Results = [System.Collections.Generic.List[string]]::new()
$State   = @{ Failures = 0 }

if (-not ('DetectViewTest.Win32' -as [type])) {
    Add-Type -Namespace DetectViewTest -Name Win32 -MemberDefinition @'
[DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr SendMessageW(IntPtr h, uint m, IntPtr w, string l);
[DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SendMessageW")] public static extern IntPtr SendMessageSb(IntPtr h, uint m, IntPtr w, System.Text.StringBuilder l);
[DllImport("user32.dll")] public static extern bool PostMessageW(IntPtr h, uint m, IntPtr w, IntPtr l);
[DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassNameW(IntPtr h, System.Text.StringBuilder s, int n);
'@
}
$Win32 = [DetectViewTest.Win32]

function Check([string]$Label, [bool]$Ok, [string]$Detail = '') {
    if (-not $Ok) { $State.Failures++ }
    $mark = if ($Ok) { 'PASS' } else { 'FAIL' }
    $Results.Add("[$mark] $Label — $Detail")
}

# 실패로 치지 않는 참고 정보 (환경에 따라 달라지는 것)
function Note([string]$Label, [string]$Detail) { $Results.Add("[INFO] $Label — $Detail") }

function ById($Root, [string]$Id) { $Root.FindFirst($Tree::Descendants, (New-Object $PC ($AE::AutomationIdProperty, $Id))) }
function ByName($Root, [string]$Name) { $Root.FindFirst($Tree::Descendants, (New-Object $PC ($AE::NameProperty, $Name))) }
function NameOf($El) { $El.Current.Name }
function Click($El) { $El.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }

function WaitFor([scriptblock]$Cond, [int]$TimeoutMs, [int]$StepMs = 100) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $TimeoutMs) {
        if (& $Cond) { return $true }
        Start-Sleep -Milliseconds $StepMs
    }
    return $false
}

# -ExeDir 생략 → bin\Debug 빌드. 상대 경로는 저장소 루트 기준 (예: publish_gpu_ver)
# Debug 출력 폴더 이름은 TFM 을 따라 바뀐다(net10.0-windows → net10.0-windows10.0.19041.0) → 가장 최근에 빌드된 것을 고른다
function Resolve-ExeDir([string]$ExeDir) {
    if (-not $ExeDir) {
        # Test-NpuSwitch 가 하드 링크로 본뜬 폴더(*.npu-test)는 시각까지 같아서 남아 있으면 잘못 골릴 수 있다 → 뺀다
        $exe = Get-ChildItem (Join-Path $RepoRoot 'bin\Debug') -Recurse -Filter DetectView.exe -ErrorAction SilentlyContinue |
               Where-Object { $_.DirectoryName -notlike '*.npu-test*' } |
               Sort-Object LastWriteTime -Descending | Select-Object -First 1
        if (-not $exe) { throw 'bin\Debug 에 DetectView.exe 가 없습니다 (먼저 dotnet build)' }
        return $exe.DirectoryName
    }
    # '.\publish_gpu_ver' 처럼 '.' 이 섞이거나 끝에 '\' 가 붙어도 같은 경로가 되게 정규화한다 — 예전엔 그대로 써서
    # Test-NpuSwitch 미러의 파일 이름이 잘리고(경로 길이로 Substring) Test-ScreenHud 의 exe 경로 비교가 틀렸다
    $p = if ([IO.Path]::IsPathRooted($ExeDir)) { $ExeDir } else { Join-Path $RepoRoot $ExeDir }
    $full = [IO.Path]::GetFullPath($p)
    if ($full.Length -gt 3) { $full = $full.TrimEnd('\', '/') }   # 'C:\' 는 그대로
    return $full
}

# 앱을 띄우고 모델(GPU) 로드까지 기다린다. 창 제목이 "... (DirectML)" 로 바뀌면 성공
function Start-DetectView([string]$ExeDir, [string]$WorkingDirectory = $ExeDir) {
    $exe = Join-Path $ExeDir 'DetectView.exe'
    if (-not (Test-Path $exe)) { throw "실행 파일이 없습니다: $exe (먼저 빌드하세요)" }
    Note '대상' "$exe ($((Get-Item $exe).LastWriteTime.ToString('yyyy-MM-dd HH:mm')))"

    $proc = Start-Process $exe -WorkingDirectory $WorkingDirectory -PassThru
    $ok = WaitFor { $proc.Refresh(); $proc.MainWindowTitle -like '*DirectML*' } 20000
    Check '모델 로드 (GPU)' $ok "제목='$($proc.MainWindowTitle)'"
    if (-not $ok) {
        Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
        throw '메인 창이 뜨지 않음 — 모델 또는 GPU 초기화 실패 (오류 대화상자에서 멈췄을 수 있음)'
    }
    return $proc
}

function Stop-DetectView($Proc) {
    if ($Proc) { Stop-Process -Id $Proc.Id -Force -ErrorAction SilentlyContinue }
}

# "파일 열기" → 대화상자에 경로 입력 → 열기. 대화상자가 닫힐 때까지 기다린다
function Open-VideoFile($Root, [string]$Path) {
    Click (ByName $Root '파일 열기')

    # 대기 블록에서 찾은 값은 해시테이블로 받는다 (스코프 한정자 없이 안전하게)
    $found = @{}
    $dialogCond = New-Object $PC ($AE::ClassNameProperty, '#32770')
    if (-not (WaitFor { $found.Dlg = $Root.FindFirst($Tree::Children, $dialogCond); $null -ne $found.Dlg } 10000)) {
        throw '파일 열기 대화상자가 뜨지 않음'
    }
    $dlg = $found.Dlg

    # 1148 은 ComboBoxEx32 → ComboBox → Edit 3단. 클래스가 Edit 인 핸들에 입력한다
    $ok = WaitFor {
        foreach ($e in $dlg.FindAll($Tree::Descendants, (New-Object $PC ($AE::AutomationIdProperty, '1148')))) {
            $h = [IntPtr]$e.Current.NativeWindowHandle
            $cls = [Text.StringBuilder]::new(64)
            $null = $Win32::GetClassNameW($h, $cls, 64)
            if ($cls.ToString() -eq 'Edit') { $found.Edit = $h; return $true }
        }
        $false
    } 10000
    if (-not $ok) { throw '대화상자의 파일 이름 칸을 찾지 못함' }
    $edit = $found.Edit

    $null = $Win32::SendMessageW($edit, 0x000C, [IntPtr]::Zero, $Path)                  # WM_SETTEXT
    $sb = [Text.StringBuilder]::new(1024)
    $null = $Win32::SendMessageSb($edit, 0x000D, [IntPtr]1024, $sb)                       # WM_GETTEXT
    Check '파일 이름 입력' ($sb.ToString() -eq $Path) "읽은 값='$sb'"

    $dlgHwnd = [IntPtr]$dlg.Current.NativeWindowHandle
    $null = $Win32::PostMessageW($dlgHwnd, 0x0111, [IntPtr]1, [IntPtr]::Zero)             # WM_COMMAND(IDOK)
    $closed = WaitFor { $null -eq $Root.FindFirst($Tree::Children, $dialogCond) } 5000
    Check '파일 대화상자 닫힘' $closed ''
}

# 클래스별 검출 수 — 검출 수 칸(Txt_DetectionCount)의 UIA ItemStatus "CAR 7 · TRUCK 1". 지금 그려진 범례(BoxLayer — 앱 안 영상이면
# FrameBoxes, HUD 면 오버레이)의 글자이고 박스가 없으면 빈 문자열. 형식이 다르면(소문자·0개·구분자) $null
function Get-ClassCounts($El) {
    $s = [string]$El.Current.ItemStatus
    $items = [ordered]@{}
    $total = 0
    foreach ($part in ($s -split ' · ' | Where-Object { $_ })) {
        if ($part -cnotmatch '^([A-Z][A-Z ]*) ([1-9]\d*)$') { return $null }
        $items[$Matches[1]] = [int]$Matches[2]
        $total += [int]$Matches[2]
    }
    [pscustomobject]@{ Text = $s; Total = $total; Items = $items }
}

# 검출 수 칸 '검출 N' (SAHI 결과면 '검출 N (SAHI)') 의 N. 비었으면 -1
function Get-DetectionCount($El) { if ((NameOf $El) -match '^검출 (\d+)') { [int]$Matches[1] } else { -1 } }

# 범례의 클래스별 수를 더하면 '검출 N' 인가. 재생 중엔 두 값을 읽는 사이 다음 프레임이 보일 수 있어 N 을 앞뒤로 읽어 같을 때만 견준다
# (어긋나면 $false — 재생 중엔 WaitFor 로 다시 읽는다). 읽은 값은 $Out 에 남긴다 (상세 표시용)
function Test-ClassCountsMatch($El, [hashtable]$Out) {
    $n1 = Get-DetectionCount $El
    $Out.Counts = Get-ClassCounts $El
    $Out.N = Get-DetectionCount $El
    $null -ne $Out.Counts -and $n1 -ge 0 -and $n1 -eq $Out.N -and $Out.Counts.Total -eq $n1
}

# 라벨에 붙은 추적 번호 — 검출 수 칸(Txt_DetectionCount)의 UIA ItemType "#12 정지 · #15 · #21" (박스 순서, 박스가 없거나
# 추적하지 않은 결과(SAHI)면 빈 문자열). Ids 는 박스마다 하나(박스 수와 같다), Stationary 는 그중 '정지'가 붙은 번호. 형식이 다르면 $null
function Get-TrackMarks($El) {
    $s = [string]$El.Current.ItemType
    $ids = [System.Collections.Generic.List[int]]::new()
    $still = [System.Collections.Generic.List[int]]::new()
    $inc = [System.Collections.Generic.List[int]]::new()
    foreach ($part in ($s -split ' · ' | Where-Object { $_ })) {
        if ($part -notmatch '^#([1-9]\d*)( 정지)?( 유고)?$') { return $null }
        $id = [int]$Matches[1]
        $ids.Add($id)
        if ($Matches[2]) { $still.Add($id) }
        if ($Matches[3]) { $inc.Add($id) }
    }
    [pscustomobject]@{ Text = $s; Ids = $ids; Stationary = $still; Incidents = $inc }
}

# 박스마다 번호가 하나씩 붙었는가(번호 수 = '검출 N', 번호는 겹치지 않음). 재생 중엔 두 값을 읽는 사이 프레임이 바뀔 수 있어
# N 을 앞뒤로 읽어 같을 때만 견준다 (어긋나면 $false — WaitFor 로 다시 읽는다). 읽은 값은 $Out 에 남긴다 (Marks, N)
function Test-TrackMarksMatch($El, [hashtable]$Out) {
    $n1 = Get-DetectionCount $El
    $Out.Marks = Get-TrackMarks $El
    $Out.N = Get-DetectionCount $El
    $null -ne $Out.Marks -and $n1 -ge 0 -and $n1 -eq $Out.N -and $Out.Marks.Ids.Count -eq $n1 -and
        @($Out.Marks.Ids | Select-Object -Unique).Count -eq $n1
}

# 재생 배속: 벽시계 $Ms 동안 슬라이더(영상 시각)가 얼마나 나아갔는지. 슬라이더는 길이를 안 뒤에만 움직인다
function Measure-PlaybackRate($Range, [int]$Ms = 2000) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $v0 = $Range.Current.Value
    Start-Sleep -Milliseconds $Ms
    ($Range.Current.Value - $v0) / $sw.Elapsed.TotalMilliseconds
}

# 결과를 출력하고 실패 건수를 종료 코드로 돌려준다 (0 = 모두 통과)
function Complete-Test {
    $Results
    "--- 실패 $($State.Failures) 건 ---"
    exit $State.Failures
}
