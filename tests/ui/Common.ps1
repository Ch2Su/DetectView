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
#  - 창이 뜬 직후에 잡은 UIA 루트는 WPF 요소가 안 보이는 Win32 프록시일 수 있다 → 루트는 Start-DetectView 가 돌려준 프로세스의 .Root 를 쓴다 (2026-11-17)

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

# 꼭 있어야 하는 요소 — 없으면 그 자리에서 무엇을 못 찾았는지(AutomationId · 루트 종류) 담아 던진다.
# ById 는 없으면 $null 이라 실패가 멀리서 이름 없는 'null 값 식에서 메서드를 호출할 수 없습니다' 로만 드러났다 (2026-11-17)
function Need($Root, [string]$Id) {
    $e = ById $Root $Id
    if ($null -ne $e) { return $e }
    $kind = '루트를 읽지 못함'
    try { $kind = "$($Root.Current.ClassName) / $($Root.Current.FrameworkId)" } catch { }
    throw "UIA 요소 '$Id' 를 찾지 못함 (루트: $kind)"
}

# 예외를 결과 한 줄로 — 메시지에 난 곳(스크립트:줄)과 그 줄을 붙인다. 메시지만 남기면 'null 값 식…' 처럼 어디서 났는지 모른다.
# 함수 안에서 났으면 그것을 부른 테스트 쪽 자리와 그 줄도 두 단계까지 붙인다 — 공용 도우미 줄(예: Click)만으론 어느 호출·어느 요소인지 모른다
function Format-Error($Err) {
    $ii = $Err.InvocationInfo
    if (-not $ii -or -not $ii.ScriptName) { return $Err.Exception.Message }
    $out = "$($Err.Exception.Message) @ $(Split-Path $ii.ScriptName -Leaf):$($ii.ScriptLineNumber) — $(Format-SourceLine $ii.Line)"
    # 스택 글은 언어에 따라 'at f, <경로>: line N' · 'f에서 <경로>: 줄 N' 이라 경로와 번호만 뽑는다. 공용 파일(Common·Run-All) 단계는 건너뛴다
    $n = 0
    foreach ($fr in ("$($Err.ScriptStackTrace)" -split "`r?`n")) {
        if ($n -ge 2 -or $fr -notmatch '([A-Za-z]:\\.*?\.ps1): \S+ (\d+)\s*$') { continue }
        $path = $Matches[1]; $num = [int]$Matches[2]; $leaf = Split-Path $path -Leaf
        if ($leaf -in 'Common.ps1', 'Run-All.ps1' -or ($path -eq $ii.ScriptName -and $num -eq $ii.ScriptLineNumber)) { continue }
        $src = ''
        try { $src = [IO.File]::ReadAllLines($path)[$num - 1] } catch { }
        $out += " ← $($leaf):$num — $(Format-SourceLine $src)"
        $n++
    }
    $out
}

function Format-SourceLine([string]$Line) {
    $s = "$Line".Trim()
    if ($s.Length -gt 120) { $s.Substring(0, 120) + '…' } else { $s }
}
function Click($El) { $El.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }

function WaitFor([scriptblock]$Cond, [int]$TimeoutMs, [int]$StepMs = 100) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $TimeoutMs) {
        if (& $Cond) { return $true }
        Start-Sleep -Milliseconds $StepMs
    }
    return $false
}

# -ExeDir 생략 → bin\Debug 빌드. 상대 경로는 저장소 루트 기준 (예: publish)
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
    # '.\publish' 처럼 '.' 이 섞이거나 끝에 '\' 가 붙어도 같은 경로가 되게 정규화한다 — 예전엔 그대로 써서
    # Test-NpuSwitch 미러의 파일 이름이 잘리고(경로 길이로 Substring) Test-ScreenHud 의 exe 경로 비교가 틀렸다
    $p = if ([IO.Path]::IsPathRooted($ExeDir)) { $ExeDir } else { Join-Path $RepoRoot $ExeDir }
    $full = [IO.Path]::GetFullPath($p)
    if ($full.Length -gt 3) { $full = $full.TrimEnd('\', '/') }   # 'C:\' 는 그대로
    return $full
}

# 앱을 띄우고 모델(GPU) 로드까지 기다린다. 창 제목이 "... (DirectML)" 로 바뀌면 성공.
# 돌려주는 프로세스에는 WPF 트리까지 확인한 UIA 루트가 .Root 로 붙어 있다 — 루트는 이것을 쓴다($AE::FromHandle 로 다시 잡지 말 것 — Wait-AppRoot)
function Start-DetectView([string]$ExeDir, [string]$WorkingDirectory = $ExeDir) {
    $exe = Join-Path $ExeDir 'DetectView.exe'
    if (-not (Test-Path $exe)) { throw "실행 파일이 없습니다: $exe (먼저 빌드하세요)" }
    Note '대상' "$exe ($((Get-Item $exe).LastWriteTime.ToString('yyyy-MM-dd HH:mm')))"

    $proc = Start-Process $exe -WorkingDirectory $WorkingDirectory -PassThru
    $null = $proc.Handle   # 핸들을 잡아 둔다 — 안 그러면 앱이 끝난 뒤 ExitCode 를 읽지 못한다
    # 앱이 그사이 끝나면(시작 중 예외) 20초를 다 기다리지 않고 종료 코드를 남긴다
    $ok = WaitFor { $proc.Refresh(); $proc.HasExited -or $proc.MainWindowTitle -like '*DirectML*' } 20000
    $exited = $proc.HasExited   # 한 번만 읽는다 — 두 번 읽으면 그사이 끝난 앱이 'PASS · 앱이 끝남' 처럼 어긋나게 찍힌다
    $ok = $ok -and -not $exited
    Check '모델 로드 (GPU)' $ok $(if ($exited) { "제목을 띄우기 전에 앱이 끝남 (종료 코드 $($proc.ExitCode))" } else { "제목='$($proc.MainWindowTitle)'" })
    if (-not $ok) {
        $why = if ($exited) { "앱이 끝남 (종료 코드 $($proc.ExitCode))" } else { '모델 또는 GPU 초기화 실패 (오류 대화상자에서 멈췄을 수 있음)' }
        Stop-DetectView $proc
        throw "메인 창이 뜨지 않음 — $why"
    }
    try { $root = Wait-AppRoot $proc }
    catch { Stop-DetectView $proc; throw }
    Add-Member -InputObject $proc -NotePropertyName Root -NotePropertyValue $root -Force
    return $proc
}

# 창 제목이 뜬 프로세스에서 WPF 트리까지 붙은 UIA 루트를 얻는다 — 볼 때마다 FromHandle 로 루트를 새로 잡는다.
# 창이 뜬 직후에 잡은 루트는 WPF 요소가 안 보이는 Win32 창 프록시(FrameworkId 'Win32')일 수 있고 그 루트로는 끝까지 못 찾는다
# (docs/testing.md 2026-11-17). 확인한 바로 그 루트를 돌려준다. 앱이 그사이 끝나면 기다리지 않고 종료 코드를 담아 던진다
function Wait-AppRoot($Proc, [int]$TimeoutMs = 10000) {
    $got = @{ Kind = '루트를 잡지 못함' }
    $null = WaitFor {
        if ($Proc.HasExited) { return $true }
        try {
            $r = $AE::FromHandle($Proc.MainWindowHandle)
            $got.Kind = "$($r.Current.ClassName) / $($r.Current.FrameworkId)"
            if ($r.Current.FrameworkId -eq 'WPF' -and $null -ne (ById $r 'Btn_PlayPause')) { $got.Root = $r; return $true }
        }
        catch { $got.Kind = "오류: $($_.Exception.Message)" }
        $false
    } $TimeoutMs 50
    if ($Proc.HasExited) { throw "UIA 트리가 준비되기 전에 앱이 끝남 (종료 코드 $($Proc.ExitCode))" }
    if (-not $got.Root) {
        throw "메인 창의 UIA 트리가 준비되지 않음 — $([int]($TimeoutMs / 1000))초 안에 WPF 루트·Btn_PlayPause 를 찾지 못함 (마지막 루트: $($got.Kind))"
    }
    $got.Root
}

# 앱을 끄고 실제로 끝날 때까지(최대 10초) 기다린다 — Stop-Process 는 기다리지 않아서, 곧바로 다음 테스트를 띄우거나 exe 폴더를 지우면
# 내려가는 중인 앱이 GPU·파일을 쥐고 있을 수 있다. 예전엔 호출부마다 WaitForExit 을 붙이거나(Test-NpuSwitch) 빠뜨렸고,
# Start-DetectView 가 실패하며 죽인 앱은 호출부가 프로세스를 받지 못해 기다릴 수도 없었다
function Stop-DetectView($Proc) {
    if (-not $Proc) { return }
    Stop-Process -Id $Proc.Id -Force -ErrorAction SilentlyContinue
    try { $null = $Proc.WaitForExit(10000) } catch { }
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
