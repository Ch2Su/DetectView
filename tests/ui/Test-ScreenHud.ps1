# 화면 HUD UI 테스트
#   - HUD 시작 → 캡처 방식 확인 → 추론 표시 → 화면 받음 → 일시정지/재개 → 카메라 [새로고침] → 정지
#   - 진단 값: Txt_DetectionCount 의 HelpText "오버레이 박스 N · 재개 재검출 M · 캡처 재연결 K · 받은 화면 F"
#     (그리지 않는 UIA 속성이라 DXGI 에 새 화면을 만들지 않는다. F 는 매 프레임 바뀐다)
#   - 재개 재검출 (이슈 #11): 일시정지·재개는 FPS·검출 수 글자를 안 바꿔서 그것만으로는 재검출이 깨져도 통과했다 →
#     일시정지 → 박스 0, 재개 → M 증가·박스 복귀를 본다.
#     M 은 재개 요청에 캡처가 새 화면이 아니라 마지막 화면을 다시 준 경우(TryCapture(forceLatest) — 새 화면을 기다리지 않고 곧바로)만 센다.
#     메인 창이 HUD 대상 모니터에 있어 재개 버튼 글자가 바뀌면 DXGI 가 새 화면을 주지만 그건 F 로만 세므로, 강제 경로(_hudForceRefresh →
#     forceLatest)가 깨지면 M 이 늘지 않아 실패한다 — 예전엔 재개 뒤 받은 아무 화면이나 M 으로 세서 버튼 글자가 만든 새 화면만으로도 통과했다.
#     다시 줄 마지막 화면이 있어야 하므로 먼저 F > 0 을 확인한다(없으면 일시정지·재개로 버튼 글자를 바꿔 한 장 받게 한다).
#     GDI 는 매번 새로 캡처해 다시 줄 화면이 없어(강제 경로가 필요 없음) M 검사는 [INFO]
#   - 클래스별 검출 수 범례(오버레이의 모니터 왼쪽 위): 그 글자(검출 수 칸의 UIA ItemStatus)의 합이 진단 값의 오버레이 박스 수와
#     같은지, 일시정지에 지워지고 재개에 돌아오는지(박스가 있을 때), 정지하면 비는지. 오버레이는 UIA·스크린샷으로 안 보여 그 글자로 본다
#   - 추적 번호(오버레이 라벨 "CAR #12 0.95", 검출 수 칸의 UIA ItemType): 번호 수가 오버레이 박스 수와 같은지, 일시정지에 지워지고
#     재개에 돌아오는지(박스가 있을 때), 정지하면 비는지 — 범례와 같은 방식
#   - HUD 에서 정지 없이 곧바로 파일 열기 → 오버레이가 닫히는지 (예전엔 바탕화면에 박스 창이 남았다)
#   - 결함 주입 (Debug 빌드만 — TestFaults.cs 의 훅은 Release 에선 컴파일되지 않는다). 결함마다 앱을 따로 띄운다
#       hud-create-fail  → 오류 창 → 닫힘 → 소스 없음으로 되돌아옴 (이슈 #2 — 예전엔 'HUD 실행 중'·빈 오버레이가 남았다)
#       hud-capture-lost → 캡처를 새로 만들어 DXGI 로 이어 감(진단 값 '캡처 재연결' 1), 일시정지·재개로 화면을 바꾸면 다시 만든 캡처가
#                          새 화면을 줌(F 증가 — 상태·FPS 칸만으로는 화면을 한 장도 못 받는 캡처도 '대기'로 통과한다) (이슈 #1)
#       hud-detect-fail  → FPS 칸에 '검출 오류 …'(툴팁에 사유) → 풀리면 FPS/대기로 돌아옴 (이슈 #1)
#   - 환경 변수 DETECTVIEW_TEST_FAULTS: 처음 값을 기억해 두고, 결함 없는 실행은 지운 채, 결함 실행은 그 결함만 둔 채 앱을 띄운 뒤
#     매번 처음 값으로 되돌린다 (앱은 시작할 때만 읽는다. Run-All 은 테스트 파일들을 한 프로세스에서 돌리고, 셸에 남은 값이
#     결함 없는 실행에 섞이면 안 된다)
#   - 박스가 0개이던 상태에서 '박스 지움'을 보는 검사는 실패할 수 없어 [INFO] 로 보고한다 (HUD 대상 모니터에 검출할 물체가 없을 때 —
#     hud-detect-fail 은 첫 검출부터 실패하므로 늘 [INFO])
#   - 캡처가 GDI 로 떨어지면 실패가 아니라 [INFO] 로 사유를 보여 준다 (하이브리드 GPU 모드 등 환경 문제).
#     단, 사유 없이 GDI 로 떨어졌으면 실패 (사유 표시가 사라진 회귀)
#   - HUD 대상은 기본 선택인 주 모니터. 메인 창을 주 모니터로 옮겨 둔다 — DXGI 는 화면이 바뀔 때만 프레임을 주고 갓 만든 복제의
#     첫 프레임은 검은 화면이라, 메인 창이 다른 모니터에 있으면 쉬는 PC 에선 HUD 가 화면을 한 장도 못 받을 수 있다
#   - 항목 수 (PASS/FAIL 줄): 결함 없는 실행 DXGI 25 · GDI(그래픽 설정 안내) 24 · GDI(그 밖) 22, 일시정지 전 박스가 있으면 각각 +6
#     (박스·범례·추적 번호 지움과 다시 그림), 정지 전 범례가 있으면 +2. Debug 빌드는 결함 주입이 더해진다 — 생성 실패 4 · 복제 상실 6(첫 캡처가 GDI 면 1) ·
#     검출 예외 4. 예: Debug·DXGI·박스 없음 = 39
#
#   pwsh tests\ui\Test-ScreenHud.ps1
#   pwsh tests\ui\Test-ScreenHud.ps1 -ExeDir publish_gpu_ver
param(
    [string]$ExeDir,
    [string]$Video   # HUD → 파일 전환에 쓸 영상. 생략 시 publish_gpu_ver\test1.mp4
)
. (Join-Path $PSScriptRoot 'Common.ps1')

$ExeDir = Resolve-ExeDir $ExeDir
if (-not $Video) { $Video = Join-Path $RepoRoot 'publish_gpu_ver\test1.mp4' }
if (-not (Test-Path $Video)) { throw "테스트 영상이 없습니다: $Video" }

# 오버레이 창 찾기: 이 프로세스의 보이는 최상위 창 중 클릭 통과(WS_EX_TRANSPARENT)이고 제목이 오버레이(OverlayWindow.xaml 의 Title)인 것.
# UIA 는 클릭 통과·레이어드 창을 믿을 만하게 보여 주지 않아 Win32 로 직접 센다.
# 제목까지 보는 이유: 열린 WPF 툴팁도 보이는 클릭 통과 최상위 창이다(StaysOpen 기본값 — 2026-11-01 프로브: 제목 없음, 확장 스타일 0x080800A8).
# 마우스 포인터가 메인 창의 툴팁 있는 컨트롤 위에 머물면 그 툴팁까지 세어 '오버레이 2개'로 실패했다(사용자가 PC 를 쓰는 중에 돌린 Run-All)
if (-not ('DetectViewTest.HudWindows' -as [type])) {
    Add-Type -Namespace DetectViewTest -Name HudWindows -MemberDefinition @'
public delegate bool EnumProc(IntPtr h, IntPtr l);
[DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc f, IntPtr l);
[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
[DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
[DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] public static extern IntPtr GetWindowLongPtr(IntPtr h, int i);
[DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, System.Text.StringBuilder s, int n);
public static int CountVisibleOverlays(uint processId, string title) {
    int n = 0;
    EnumWindows((h, l) => {
        uint pid; GetWindowThreadProcessId(h, out pid);
        if (pid != processId || !IsWindowVisible(h) || ((long)GetWindowLongPtr(h, -20) & 0x20) == 0) return true;   // GWL_EXSTYLE · WS_EX_TRANSPARENT
        var sb = new System.Text.StringBuilder(64);
        GetWindowText(h, sb, sb.Capacity);
        if (sb.ToString() == title) n++;
        return true;
    }, IntPtr.Zero);
    return n;
}
'@
}
function OverlayCount($Proc) { [DetectViewTest.HudWindows]::CountVisibleOverlays([uint32]$Proc.Id, 'DetectView Overlay') }

# 이 exe 의 Windows 그래픽 설정 (HKCU UserGpuPreferences, exe 경로별). 0=Windows에서 결정 · 1=절전 · 2=고성능 · 없으면 $null
function Get-GpuPreference([string]$ExePath) {
    $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Software\Microsoft\DirectX\UserGpuPreferences')
    $v = if ($key) { $key.GetValue($ExePath) } else { $null }
    if ($v -match 'GpuPreference=(\d)') { [int]$Matches[1] } else { $null }
}

# HUD 진단 값 (Txt_DetectionCount 의 HelpText). HUD 가 아니거나 형식이 다르면 $null
function Get-HudDiag($El) {
    $h = $El.Current.HelpText
    if ($h -match '오버레이 박스 (\d+) · 재개 재검출 (\d+) · 캡처 재연결 (\d+) · 받은 화면 (\d+)') {
        [pscustomobject]@{ Boxes = [int]$Matches[1]; Resume = [int]$Matches[2]; Recreate = [int]$Matches[3]; Frames = [int]$Matches[4]; Text = $h }
    }
    else { $null }
}

# 범례의 클래스별 수를 더하면 오버레이에 그려진 박스 수(진단 값)인가. 둘은 오버레이를 바꿀 때 같이 바뀌지만 두 번에 나눠 읽으므로
# 박스 수를 앞뒤로 읽어 같을 때만 견준다 (어긋나면 $false — WaitFor 로 다시 읽는다). 읽은 값은 $Out 에 남긴다 (상세 표시용)
function Test-HudLegendMatch($El, [hashtable]$Out) {
    $a = Get-HudDiag $El
    $Out.C = Get-ClassCounts $El
    $Out.D = Get-HudDiag $El
    $null -ne $a -and $null -ne $Out.D -and $null -ne $Out.C -and $a.Boxes -eq $Out.D.Boxes -and $Out.C.Total -eq $Out.D.Boxes
}

# 추적 번호가 박스마다 하나씩(번호 수 = 오버레이에 그려진 박스 수) 붙었는가 — Test-HudLegendMatch 와 같은 방식 (박스 수를 앞뒤로 읽어 견줌)
function Test-HudTrackMatch($El, [hashtable]$Out) {
    $a = Get-HudDiag $El
    $Out.T = Get-TrackMarks $El
    $Out.D = Get-HudDiag $El
    $null -ne $a -and $null -ne $Out.D -and $null -ne $Out.T -and $a.Boxes -eq $Out.D.Boxes -and $Out.T.Ids.Count -eq $Out.D.Boxes -and
        @($Out.T.Ids | Select-Object -Unique).Count -eq $Out.D.Boxes
}

# 일시정지 → 재개. 메인 창(HUD 대상 모니터)의 재생 버튼 글자가 두 번 바뀌어 DXGI 가 새 화면을 준다
function Invoke-PauseResume($PlayButton) {
    Click $PlayButton
    Start-Sleep -Milliseconds 300
    Click $PlayButton
}

# 메인 창을 주 모니터(HUD 기본 대상)로 옮긴다 (머리말 참고). 못 옮겨도 테스트는 계속한다
function Move-MainToPrimary($Root) {
    try {
        $tp = $Root.GetCurrentPattern([System.Windows.Automation.TransformPattern]::Pattern)
        if ($tp.Current.CanMove) { $tp.Move(40, 40) } else { Note '메인 창 옮기기' '옮길 수 없음 (CanMove=false)' }
    }
    catch { Note '메인 창 옮기기' "실패 — $($_.Exception.Message)" }
}

# 모달 대화상자(메인 창의 자식 #32770)의 글자를 모은다 / 닫는다
function Get-DialogText($Dlg) {
    $names = foreach ($e in $Dlg.FindAll($Tree::Descendants, [System.Windows.Automation.Condition]::TrueCondition)) { $e.Current.Name }
    (($names | Where-Object { $_ }) -join ' / ') -replace '\s+', ' '
}
function Close-Dialog($Dlg) {
    $btn = $Dlg.FindFirst($Tree::Descendants, (New-Object $PC ($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)))
    try { if ($btn) { Click $btn; return } } catch { }
    $null = $Win32::PostMessageW([IntPtr]$Dlg.Current.NativeWindowHandle, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)   # WM_CLOSE
}

# 결함 주입 환경 변수. 처음 값을 기억해 두고 앱을 띄울 때만 바꾼다 (머리말 참고)
$OrigFaults = $env:DETECTVIEW_TEST_FAULTS
function Set-TestFaults([string]$Value) {
    if ($Value) { $env:DETECTVIEW_TEST_FAULTS = $Value }
    else { Remove-Item Env:DETECTVIEW_TEST_FAULTS -ErrorAction SilentlyContinue }
}
# 결함을 건 앱 띄우기 ('' = 결함 없이). 띄우고 나면 곧바로 처음 값으로 되돌린다 — 앱은 시작할 때 받은 환경만 본다
function Start-WithFault([string]$Fault) {
    Set-TestFaults $Fault
    try { Start-DetectView $ExeDir }
    finally { Set-TestFaults $OrigFaults }
}

$proc = $null
try {
    $proc = Start-WithFault ''
    $root = $AE::FromHandle($proc.MainWindowHandle)
    Move-MainToPrimary $root

    $play   = ById $root 'Btn_PlayPause'
    $sahi   = ById $root 'Btn_Sahi'
    $status = ById $root 'Txt_Status'
    $fps    = ById $root 'Txt_InferenceFps'
    $count  = ById $root 'Txt_DetectionCount'

    # 1) HUD 시작 — 캡처 루프가 방식을 정하면 상태바에 "· 캡처 ..." 가 붙는다
    Click (ById $root 'Btn_Screen')
    $ok = WaitFor { (NameOf $status) -like '*캡처 *' } 10000
    $s = NameOf $status
    Check 'HUD 시작' $ok "상태='$s'"

    # 상태바 툴팁은 UIA 에서 HelpText 로 보인다
    $help = $status.Current.HelpText
    if ($s -like '*캡처 DXGI*') {
        Check '캡처 방식' $true 'DXGI'
        Check 'DXGI 일 땐 안내 툴팁 없음' ([string]::IsNullOrEmpty($help)) "툴팁='$help'"
    }
    elseif ($s -match '캡처 GDI \((.+)\)') {
        $reason = $Matches[1]
        Note '캡처 방식' "GDI 로 대체됨 — $reason. 환경 문제일 수 있음 (CLAUDE.md '하이브리드 GPU 함정')"
        if ($reason -match '그래픽 설정') {
            # 하이브리드 GPU 안내: 설정은 exe 경로별이라 지금 실행 중인 exe 경로가 툴팁에 있어야 하고,
            # 권하는 설정이 지금 설정과 앞뒤가 맞아야 한다 (같은 설정을 다시 권하거나, 없는 지정을 풀라고 하면 잘못)
            $exePath = Join-Path $ExeDir 'DetectView.exe'
            $pref = Get-GpuPreference $exePath
            $consistent = switch -Regex ($reason) {
                "'절전' 지정을 풀고"   { $pref -eq 1; break }
                "'고성능' 지정을 풀고" { $pref -eq 2; break }
                "'절전'으로 지정"      { $pref -ne 1; break }
                "'고성능'으로 지정"    { $pref -ne 2; break }
                default                { $false }
            }
            Check '해결 방법 툴팁' ($help.Contains($exePath) -and $help -match '에 연결돼 있는데') "exe 경로 포함=$($help.Contains($exePath))"
            Check '권하는 설정이 지금 설정과 맞음' $consistent "지금 그래픽 설정=$(if ($null -eq $pref) { '없음' } else { $pref })"
        }
    }
    else { Check '캡처 방식' $false "사유 없이 GDI 이거나 알 수 없는 방식: '$s'" }

    $ok = WaitFor { (NameOf $fps) -match 'FPS|대기' } 10000
    Check 'HUD 추론 표시' $ok "'$(NameOf $fps)'"
    Check 'HUD 오버레이 창 보임' ((OverlayCount $proc) -eq 1) "클릭 통과 창 $(OverlayCount $proc)개"
    Check 'HUD 재생 버튼 글자' ((NameOf $play) -eq '일시정지') "'$(NameOf $play)'"
    Check 'HUD 에서 SAHI 비활성' (-not $sahi.Current.IsEnabled) ''

    # 2) 화면 받음 → 일시정지 → 재개. 재개하면 루프가 새 화면을 기다리지 않고 마지막 화면으로 한 번 다시 검출해야 한다(_hudForceRefresh —
    #    DXGI 는 화면이 바뀔 때만 프레임을 줘서, 이게 깨지면 정지 화면에선 재개해도 박스가 안 돌아온다). 진단 값의 '재개 재검출'(M)은
    #    캡처가 마지막 화면을 다시 준 횟수라 재개 버튼 글자가 만든 새 화면으로는 늘지 않는다 (머리말 참고)
    $d0 = Get-HudDiag $count
    Check 'HUD 진단 값' ($null -ne $d0) "HelpText='$($count.Current.HelpText)'"
    if ($d0 -and $d0.Frames -eq 0) {
        # 아직 받은 화면이 없다(쉬는 PC — 갓 만든 복제의 첫 프레임은 검은 화면이라 건너뛴다). 다시 줄 마지막 화면이 없으면 재개 재검출이
        # 생기지 않으므로, 일시정지·재개로 메인 창의 버튼 글자를 바꿔 새 화면을 한 장 받게 한다
        Invoke-PauseResume $play
        $null = WaitFor { ($d = Get-HudDiag $count) -and $d.Frames -gt 0 } 3000
        Start-Sleep -Milliseconds 300   # 받은 화면의 검출·그리기가 끝나게 (아래 '일시정지 전 박스 수'가 그 결과를 보게)
    }
    $db = Get-HudDiag $count   # 일시정지 직전
    Check 'HUD 화면 받음' ($null -ne $db -and $db.Frames -gt 0) "진단='$($count.Current.HelpText)'"
    # 클래스별 검출 수 범례 — 박스와 같은 BoxLayer 가 같은 박스로 그린다 (박스가 없으면 범례도 없다)
    $hc = @{}
    $ok = WaitFor { Test-HudLegendMatch $count $hc } 3000
    Check 'HUD 범례 = 오버레이 박스 수' $ok "범례='$($hc.C.Text)' 진단='$($hc.D.Text)'"
    # 추적 번호 — 캡처 루프가 검출 뒤 추적기를 거쳐 박스에 번호를 붙인다
    $ht = @{}
    $ok = WaitFor { Test-HudTrackMatch $count $ht } 3000
    Check 'HUD 추적 번호 = 오버레이 박스 수' $ok "번호='$($ht.T.Text)' 진단='$($ht.D.Text)'"
    Click $play
    Start-Sleep -Milliseconds 300
    Check '일시정지' ((NameOf $play) -eq '재생') "'$(NameOf $play)'"
    $d1 = Get-HudDiag $count
    if ($db -and $db.Boxes -gt 0) {
        Check '일시정지 → 오버레이 박스 지움' ($null -ne $d1 -and $d1.Boxes -eq 0) "일시정지 전 $($db.Boxes)개 → 진단='$($count.Current.HelpText)'"
        Check '일시정지 → 범례 지움' ([string]::IsNullOrEmpty($count.Current.ItemStatus)) "범례='$($count.Current.ItemStatus)'"
        Check '일시정지 → 추적 번호 지움' ([string]::IsNullOrEmpty($count.Current.ItemType)) "번호='$($count.Current.ItemType)'"
    }
    else { Note '일시정지 → 오버레이 박스·범례 지움' "일시정지 전에 그려진 박스가 없어 확인 못 함 (HUD 대상 모니터에 검출할 물체 없음) — 진단='$($count.Current.HelpText)'" }
    Click $play
    if ((NameOf $status) -like '*캡처 DXGI*') {
        $ok = WaitFor { ($d = Get-HudDiag $count) -and $db -and $d.Resume -gt $db.Resume } 2000
        Check '재개 → 마지막 화면 다시 검출' $ok "전='$($db.Text)' 후='$($count.Current.HelpText)'"
    }
    else { Note '재개 → 마지막 화면 다시 검출' "GDI 는 매번 새로 캡처해 다시 줄 화면이 없다 (강제 경로가 필요 없음) — 진단='$($count.Current.HelpText)'" }
    if ($db -and $db.Boxes -gt 0) {
        $ok = WaitFor { ($d = Get-HudDiag $count) -and $d.Boxes -gt 0 } 2000
        Check '재개 → 박스 다시 그림' $ok "일시정지 전 $($db.Boxes)개 → '$($count.Current.HelpText)'"
        $hc = @{}
        $ok = WaitFor { (Test-HudLegendMatch $count $hc) -and $hc.D.Boxes -gt 0 } 2000
        Check '재개 → 범례 다시 그림' $ok "범례='$($hc.C.Text)' 진단='$($hc.D.Text)'"
        $ht = @{}
        $ok = WaitFor { (Test-HudTrackMatch $count $ht) -and $ht.D.Boxes -gt 0 } 2000
        Check '재개 → 추적 번호 다시 그림' $ok "번호='$($ht.T.Text)' 진단='$($ht.D.Text)'"
    }
    else { Note '재개 → 박스·범례 다시 그림' '일시정지 전에 그려진 박스가 없어 확인 못 함 (HUD 대상 모니터에 검출할 물체 없음)' }
    Start-Sleep -Milliseconds 1000
    Check '재개' ((NameOf $play) -eq '일시정지' -and (NameOf $fps) -match 'FPS|대기') "버튼='$(NameOf $play)' FPS='$(NameOf $fps)'"
    # 정지에 가까운 화면에선 프레임이 몇 장뿐이라 이 값은 수렴하지 않는다 — 벤치마크로 쓰지 말 것
    Note 'HUD FPS (참고용)' (NameOf $fps)

    # 3) HUD 중 카메라 [새로고침] → 상태바는 HUD 문구 그대로 (예전엔 "카메라 N대 감지됨"으로 덮여 GDI 안내가 사라졌다)
    $before = NameOf $status
    Click (ById $root 'Btn_RefreshCameras')
    Start-Sleep -Milliseconds 1500   # 장치 열거가 끝날 시간. HUD 는 상태를 시작할 때만 쓰므로 덮였으면 그대로 남는다
    $after = NameOf $status
    Check 'HUD 중 새로고침 → 상태 유지' ($after -like '화면 HUD*') "전='$before' 후='$after'"

    # 4) 정지
    $legendBeforeStop = $count.Current.ItemStatus
    $marksBeforeStop = $count.Current.ItemType
    Click (ById $root 'Btn_Stop')
    $ok = WaitFor { (NameOf $status) -eq '정지됨' } 5000
    Check '정지' ($ok -and (NameOf $play) -eq '재생') "버튼='$(NameOf $play)' 상태='$(NameOf $status)'"
    Check '정지 → 오버레이 닫힘' ((OverlayCount $proc) -eq 0) "클릭 통과 창 $(OverlayCount $proc)개"
    # 닫힌 오버레이는 범례를 지우는 알림을 보내지 않는다 — 메인 창이 HUD 를 내릴 때 직접 걷는다 (CloseOverlay)
    if ($legendBeforeStop) {
        Check '정지 → 범례 지움' ([string]::IsNullOrEmpty($count.Current.ItemStatus)) "정지 전 '$legendBeforeStop' → '$($count.Current.ItemStatus)'"
    }
    else { Note '정지 → 범례 지움' "정지 전에 범례가 비어 있어 확인 못 함 (HUD 대상 모니터에 검출할 물체 없음) — 범례='$($count.Current.ItemStatus)'" }
    if ($marksBeforeStop) {
        Check '정지 → 추적 번호 지움' ([string]::IsNullOrEmpty($count.Current.ItemType)) "정지 전 '$marksBeforeStop' → '$($count.Current.ItemType)'"
    }
    else { Note '정지 → 추적 번호 지움' "정지 전에 번호가 비어 있어 확인 못 함 (HUD 대상 모니터에 검출할 물체 없음) — 번호='$($count.Current.ItemType)'" }

    # 5) HUD 에서 정지 없이 곧바로 파일 열기 → 오버레이가 닫히고 파일이 재생된다
    Click (ById $root 'Btn_Screen')
    $ok = WaitFor { (NameOf $status) -like '*캡처 *' -and (OverlayCount $proc) -eq 1 } 10000
    Check 'HUD 다시 시작' $ok "상태='$(NameOf $status)'"
    Open-VideoFile $root $Video
    $ok = WaitFor { (NameOf $status) -like '파일:*' -and (NameOf $play) -eq '일시정지' } 10000
    Check 'HUD → 파일 전환' $ok "상태='$(NameOf $status)' 버튼='$(NameOf $play)'"
    Check 'HUD → 파일: 오버레이 닫힘' ((OverlayCount $proc) -eq 0) "클릭 통과 창 $(OverlayCount $proc)개"
    Check 'HUD → 파일: SAHI 활성' $sahi.Current.IsEnabled ''

    Click (ById $root 'Btn_Stop')
    $ok = WaitFor { (NameOf $status) -eq '정지됨' } 5000
    Check '파일 정지' $ok "상태='$(NameOf $status)'"
}
catch { Check '예외' $false $_.Exception.Message }
finally { Stop-DetectView $proc }

# 6) 결함 주입 — 실제로 일으키기 어려운 실패의 복구·표시 경로를 끝까지 본다 (결함마다 앱을 따로 띄운다)
if ($ExeDir -notlike '*\bin\Debug\*') {
    Note '결함 주입' "Debug 빌드가 아니라 건너뜀 — 훅(TestFaults.cs)은 Release 에선 컴파일되지 않는다 ($ExeDir)"
}
else {
    $dlgCond = New-Object $PC ($AE::ClassNameProperty, '#32770')

    # (1) 캡처 생성 실패 → 오류 창 → 소스 없음 (이슈 #2)
    $fp = $null
    try {
        $fp = Start-WithFault 'hud-create-fail'
        $froot = $AE::FromHandle($fp.MainWindowHandle)
        Move-MainToPrimary $froot
        $fplay = ById $froot 'Btn_PlayPause'
        $fstatus = ById $froot 'Txt_Status'
        $fscreen = ById $froot 'Btn_Screen'
        Click $fscreen
        $found = @{}
        $dlgFound = WaitFor { $found.Dlg = $froot.FindFirst($Tree::Children, $dlgCond); $null -ne $found.Dlg } 10000
        $text = if ($dlgFound) { Get-DialogText $found.Dlg } else { '' }
        Check '결함: 생성 실패 → 오류 창' ($dlgFound -and $text -like '*화면 캡처를 시작할 수 없습니다*' -and $text -like '*hud-create-fail*') "글자='$text'"
        # 오류 창이 떠 있는 동안 이미 소스 없음이어야 한다 (파일·카메라 열기 실패와 같은 순서: 되돌린 뒤 알림)
        $idleText = NameOf $fstatus
        if ($dlgFound) { Close-Dialog $found.Dlg }
        # 창을 찾았고 그 창이 닫혀야 통과 — 찾지 못했으면 '없음'이 곧바로 참이라 닫힘만으로는 실패할 수 없었다
        $closed = $dlgFound -and (WaitFor { $null -eq $froot.FindFirst($Tree::Children, $dlgCond) } 5000)
        Check '결함: 생성 실패 → 오류 창 닫힘' $closed "오류 창 찾음=$dlgFound"
        $ok = WaitFor { (NameOf $fstatus) -eq '화면 캡처 시작 실패' } 3000
        $n = OverlayCount $fp
        Check '결함: 생성 실패 → 소스 없음으로 되돌아옴' ($ok -and $idleText -eq '화면 캡처 시작 실패' -and -not $fplay.Current.IsEnabled -and
                (NameOf $fplay) -eq '재생' -and $fscreen.Current.IsEnabled -and $n -eq 0) `
            "상태='$(NameOf $fstatus)'(오류 창 동안 '$idleText') 재생 버튼 활성=$($fplay.Current.IsEnabled) 글자='$(NameOf $fplay)' HUD 버튼 활성=$($fscreen.Current.IsEnabled) 클릭 통과 창 $n개"
    }
    catch { Check '결함: 생성 실패 (예외)' $false $_.Exception.Message }
    finally { Stop-DetectView $fp }

    # (2) 복구 불가 복제 상실 → 캡처를 새로 만들어 이어 감 (이슈 #1 — 예전엔 같은 장치로 다시 복제만 해서 '대기'에 멈췄다)
    $fp = $null
    try {
        $fp = Start-WithFault 'hud-capture-lost'
        $froot = $AE::FromHandle($fp.MainWindowHandle)
        Move-MainToPrimary $froot
        $fplay = ById $froot 'Btn_PlayPause'
        $fstatus = ById $froot 'Txt_Status'
        $ffps = ById $froot 'Txt_InferenceFps'
        $fcount = ById $froot 'Txt_DetectionCount'
        Click (ById $froot 'Btn_Screen')
        $ok = WaitFor { (NameOf $fstatus) -like '*캡처 *' } 10000
        $s0 = NameOf $fstatus
        if (-not $ok) { Check '결함: 복제 상실 → HUD 시작' $false "상태='$s0'" }
        elseif ($s0 -notlike '*캡처 DXGI*') { Note '결함: 복제 상실' "처음 캡처가 DXGI 가 아니라 건너뜀 (주입은 DXGI 캡처에만 걸린다) — '$s0'" }
        else {
            # 첫 화면 1.5초 뒤(화면이 그대로면 3초 뒤) 상실 → 루프가 곧바로 새로 만든다
            $ok = WaitFor { ($d = Get-HudDiag $fcount) -and $d.Recreate -ge 1 } 10000
            Check '결함: 복제 상실 → 캡처 다시 만듦' $ok "진단='$($fcount.Current.HelpText)'"
            Start-Sleep -Milliseconds 1500   # 다시 만든 뒤 자리를 잡는지 (또 다시 만들지 않는지, FPS 칸이 '끊김'에 머물지 않는지)
            $d = Get-HudDiag $fcount
            Check '결함: 복제 상실 → DXGI 로 이어 감' ((NameOf $fstatus) -like '*캡처 DXGI*' -and $null -ne $d -and $d.Recreate -eq 1) `
                "상태='$(NameOf $fstatus)' 진단='$($fcount.Current.HelpText)'"
            Check '결함: 복제 상실 → FPS 표시 이어짐' ((NameOf $ffps) -match 'FPS|대기') "'$(NameOf $ffps)'"
            Check '결함: 복제 상실 → 오버레이 그대로' ((OverlayCount $fp) -eq 1) "클릭 통과 창 $(OverlayCount $fp)개"
            # 다시 만든 캡처가 실제로 화면을 주는지. 갓 만든 복제는 화면이 바뀌어야 첫 화면을 주므로(첫 프레임은 검은 화면이라 건너뜀),
            # 일시정지·재개로 메인 창의 버튼 글자를 바꾸고 받은 화면(F)이 느는지 본다
            $f0 = Get-HudDiag $fcount
            Invoke-PauseResume $fplay
            $ok = WaitFor { ($d = Get-HudDiag $fcount) -and $f0 -and $d.Frames -gt $f0.Frames } 2000
            Check '결함: 복제 상실 → 다시 만든 캡처가 화면을 줌' $ok "전='$($f0.Text)' 후='$($fcount.Current.HelpText)'"
        }
    }
    catch { Check '결함: 복제 상실 (예외)' $false $_.Exception.Message }
    finally { Stop-DetectView $fp }

    # (3) 검출 예외가 이어짐 → FPS 칸에 사유 → 풀리면 FPS/대기로 (이슈 #1 — 예전엔 Debug 출력뿐이라 박스·FPS 가 마지막 값에 멈췄다)
    $fp = $null
    try {
        $fp = Start-WithFault 'hud-detect-fail'
        $froot = $AE::FromHandle($fp.MainWindowHandle)
        Move-MainToPrimary $froot
        $fplay = ById $froot 'Btn_PlayPause'
        $fstatus = ById $froot 'Txt_Status'
        $ffps = ById $froot 'Txt_InferenceFps'
        $fcount = ById $froot 'Txt_DetectionCount'
        Click (ById $froot 'Btn_Screen')
        $ok = WaitFor { (NameOf $fstatus) -like '*캡처 *' } 10000
        Check '결함: 검출 예외 → HUD 시작' $ok "상태='$(NameOf $fstatus)'"
        $dPre = Get-HudDiag $fcount   # 오류가 드러나기 전 박스 수 — 주입은 첫 검출부터라 보통 0
        # 주입은 첫 검출부터 3초. 화면이 그대로면 첫 화면(= 첫 검출)이 안 와서, 일시정지·재개로 메인 창(HUD 대상 모니터)의 버튼 글자를 바꿔 새 화면을 만든다
        Invoke-PauseResume $fplay
        $ok = WaitFor { (NameOf $ffps) -like '검출 오류*' } 6000
        $tip = $ffps.Current.HelpText
        $d = Get-HudDiag $fcount
        Check '결함: 검출 예외 → FPS 칸에 사유' ($ok -and $tip -like '*hud-detect-fail*') "FPS='$(NameOf $ffps)' 툴팁='$tip'"
        if ($dPre -and $dPre.Boxes -gt 0) {
            Check '결함: 검출 예외 → 오버레이 박스 지움' ($null -ne $d -and $d.Boxes -eq 0) "오류 전 $($dPre.Boxes)개 → 진단='$($fcount.Current.HelpText)'"
        }
        else { Note '결함: 검출 예외 → 오버레이 박스 지움' "오류 전에 그려진 박스가 없어 확인 못 함 (주입이 첫 검출부터) — 진단='$($fcount.Current.HelpText)'" }
        $ok = WaitFor { (NameOf $ffps) -match 'FPS|대기' } 8000
        Check '결함: 검출 예외 풀림 → FPS 표시로 돌아옴' ($ok -and [string]::IsNullOrEmpty($ffps.Current.HelpText)) `
            "FPS='$(NameOf $ffps)' 툴팁='$($ffps.Current.HelpText)'"
    }
    catch { Check '결함: 검출 예외 (예외)' $false $_.Exception.Message }
    finally { Stop-DetectView $fp }
}

Set-TestFaults $OrigFaults   # 띄울 때마다 되돌렸지만, 끝에서 한 번 더 (위에서 예외로 빠져나왔어도 셸 값이 처음과 같게)
Complete-Test
