# 추론 장치(GPU ↔ NPU) 전환 UI 테스트
#   1) 최소 폭(900)에서 조작 요소가 모두 창 안에 보이는지 (2026-11-01 개편부터 왼쪽 패널 — 예전 가로 툴바는 줄바꿈)
#   2) 툴바 "추론"에서 NPU 선택 → 상태바 진행 표시 → 제목이 NPU 로 → 파일 재생(속도·검출)
#      → 재생 중 GPU 로 되돌리기 → 다시 NPU(캐시) — 재생은 끊기지 않아야 한다.
#      NPU 를 준비하는 동안(① 짝 모델 확인 · ② EP 준비) 창이 멈추지 않는지도 본다 — UI 스레드 응답 시간 (이슈 #4)
#   3) 실패 경로: 짝 모델 없음 · 다른 모델의 짝 · FP32 를 짝 이름으로 — NPU 항목이 막히고 사유가 툴팁에, GPU 에 머문다.
#      exe 폴더를 하드 링크로 본뜬 옆 폴더(<exe 폴더>.npu-test)에서 파일을 바꾼다 — 원래 폴더(배포 폴더 포함)는 건드리지 않는다
#   NPU(AMD Ryzen AI · VitisAI)가 없는 PC 거나 짝 모델이 없으면 2) 는 [INFO] 로 건너뛴다 (1)·3) 은 NPU 없이도 돈다)
#
#   pwsh tests\ui\Test-NpuSwitch.ps1                              # bin\Debug 빌드
#   pwsh tests\ui\Test-NpuSwitch.ps1 -ExeDir publish_gpu_ver      # 배포본 (짝은 tools\Make-NpuModel.ps1 -Model publish_gpu_ver\best.onnx)
#   pwsh tests\ui\Test-NpuSwitch.ps1 -ColdCancel                  # + NPU 컴파일 캐시를 지우고 컴파일 도중 취소 (2분 넘게 걸림)
param(
    [string]$ExeDir,
    [string]$Video,          # 생략 시 publish_gpu_ver\test1.mp4
    [switch]$ColdCancel
)
. (Join-Path $PSScriptRoot 'Common.ps1')

$ExeDir = Resolve-ExeDir $ExeDir
if (-not $Video) { $Video = Join-Path $RepoRoot 'publish_gpu_ver\test1.mp4' }
if (-not (Test-Path $Video)) { throw "테스트 영상이 없습니다: $Video" }

if (-not ('DetectViewTest.Window' -as [type])) {
    Add-Type -Namespace DetectViewTest -Name Window -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int ht, bool repaint);
'@
}

# UI 스레드 응답 시간 (이슈 #4). 스크립트는 한 스레드라 전환을 기다리며 따로 잴 수 없어서, C# 스레드 둘이 50 ms 마다 잰다:
#  - WM_NULL: SendMessageTimeout 왕복 — 창 스레드가 보낸 메시지를 처리할 때까지
#  - UIA: WPF 요소의 Name 읽기 — WPF 는 UIA 요청을 Dispatcher 로 UI 스레드에서 답하므로 Dispatcher 가 막힌 시간이 그대로 보인다
# 둘 다 보는 이유: COM 대기(EP 카탈로그 조회 등) 중에도 창 스레드는 보낸 메시지를 가끔 처리해서 WM_NULL 엔 멈춤의 일부만 보인다.
# Mark() 를 부르면 그때까지의 최대·횟수·실패를 따로 남긴다 — ①② 와 ③(컴파일, CPU 를 많이 씀)을 나눠 보려고
$latencyType = 'DetectViewTest.UiLatency' -as [type]
if ($latencyType -and -not $latencyType.GetField('MarkUiaCount')) {
    # Add-Type 으로 만든 형식은 세션이 끝날 때까지 바꿀 수 없다 — 옛 판이 남은 세션에선 새 필드가 $null 로 읽혀 판정이 엉뚱하게 실패한다
    throw '이 PowerShell 세션에 옛 DetectViewTest.UiLatency 가 남아 있음 — 새 세션(pwsh tests\ui\Test-NpuSwitch.ps1)에서 돌릴 것'
}
if (-not $latencyType) {
    Add-Type -TypeDefinition @'
using System;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;

namespace DetectViewTest
{
    // Windows PowerShell 5.1 의 Add-Type(C# 5)로도 컴파일되게 옛 문법만 쓴다. UIA 는 리플렉션으로 부른다 —
    // -ReferencedAssemblies 를 주면 PowerShell 7 은 기본 .NET 참조를 빼서(이름도 5.1 과 다르다) 두 판에 같이 맞추기 번거롭다
    public sealed class UiLatency
    {
        [DllImport("user32.dll")]
        private static extern IntPtr SendMessageTimeoutW(IntPtr h, uint msg, IntPtr w, IntPtr l, uint flags, uint timeoutMs, out IntPtr result);
        private const uint SMTO_ABORTIFHUNG = 0x0002;

        private readonly IntPtr _hwnd;
        private readonly object _probe;          // System.Windows.Automation.AutomationElement
        private readonly MethodInfo _getValue;   // AutomationElement.GetCurrentPropertyValue(AutomationProperty)
        private readonly object[] _nameArg;      // { AutomationElement.NameProperty }
        private readonly int _intervalMs;
        private readonly object _gate = new object();
        private readonly Stopwatch _clock = new Stopwatch();
        private volatile bool _stop;
        private long _wmSince, _uiaSince;   // 진행 중인 호출의 시작 (Stopwatch 틱, 0 = 없음). _gate 안에서
        private Thread _wmThread, _uiaThread;

        public double WmNullMaxMs, UiaMaxMs;
        public int WmNullCount, UiaCount, WmNullFailures, UiaFailures;
        public bool Marked;
        public double MarkAtMs, MarkWmNullMaxMs, MarkUiaMaxMs;
        public int MarkWmNullCount, MarkUiaCount, MarkWmNullFailures, MarkUiaFailures;

        public UiLatency(IntPtr hwnd, object probe, int intervalMs)
        {
            _hwnd = hwnd; _probe = probe; _intervalMs = intervalMs;
            Type t = probe.GetType();
            object nameProperty = t.GetField("NameProperty", BindingFlags.Public | BindingFlags.Static).GetValue(null);
            _getValue = t.GetMethod("GetCurrentPropertyValue", new Type[] { nameProperty.GetType() });
            _nameArg = new object[] { nameProperty };
        }

        public void Start()
        {
            _clock.Start();
            _wmThread = new Thread(LoopWmNull); _wmThread.IsBackground = true; _wmThread.Start();
            _uiaThread = new Thread(LoopUia); _uiaThread.IsBackground = true; _uiaThread.Start();
        }

        // 그때까지의 최대·횟수·실패를 남긴다 (처음 한 번만). 아직 답이 없는 호출은 지금까지 기다린 시간을 최대에 넣는다 —
        // 멈춤이 막 풀려 답이 오는 중일 때 불러도 그 멈춤을 놓치지 않게. 횟수는 끝난 호출만 센다 (0 이면 그 구간을 재지 못한 것)
        public void Mark()
        {
            lock (_gate)
            {
                if (Marked) return;
                Marked = true;
                long now = Stopwatch.GetTimestamp();
                MarkAtMs = _clock.Elapsed.TotalMilliseconds;
                MarkWmNullMaxMs = Math.Max(WmNullMaxMs, _wmSince != 0 ? Ms(now - _wmSince) : 0);
                MarkUiaMaxMs = Math.Max(UiaMaxMs, _uiaSince != 0 ? Ms(now - _uiaSince) : 0);
                MarkWmNullCount = WmNullCount;
                MarkUiaCount = UiaCount;
                MarkWmNullFailures = WmNullFailures;
                MarkUiaFailures = UiaFailures;
            }
        }

        // 끝나지 않는 호출(창이 아주 오래 멈춤)은 기다린 만큼을 실패로 센다 — 테스트가 여기서 멈추지 않게. 여러 번 불러도 된다
        public void Stop()
        {
            _stop = true;
            if (_wmThread != null) _wmThread.Join(15000);
            if (_uiaThread != null) _uiaThread.Join(15000);
            lock (_gate)
            {
                long now = Stopwatch.GetTimestamp();
                if (_wmSince != 0) { Add(true, Ms(now - _wmSince), false); _wmSince = 0; }
                if (_uiaSince != 0) { Add(false, Ms(now - _uiaSince), false); _uiaSince = 0; }
            }
            Mark();   // Mark 없이 끝났으면 전체가 곧 그 구간이다
        }

        private void LoopWmNull()
        {
            while (!_stop)
            {
                long t0 = Begin(true);
                IntPtr r;
                // 실패(0) = 10초 안에 답이 없거나, 5초 넘게 메시지를 안 받아 '응답 없음'으로 보여 바로 돌아옴(SMTO_ABORTIFHUNG)
                bool ok = SendMessageTimeoutW(_hwnd, 0, IntPtr.Zero, IntPtr.Zero, SMTO_ABORTIFHUNG, 10000, out r) != IntPtr.Zero;
                End(true, t0, ok);
                Thread.Sleep(_intervalMs);
            }
        }

        private void LoopUia()
        {
            while (!_stop)
            {
                long t0 = Begin(false);
                bool ok = true;
                try { _getValue.Invoke(_probe, _nameArg); }
                catch (Exception) { ok = false; }
                End(false, t0, ok);
                Thread.Sleep(_intervalMs);
            }
        }

        private long Begin(bool wmNull)
        {
            long t = Stopwatch.GetTimestamp();
            lock (_gate) { if (wmNull) _wmSince = t; else _uiaSince = t; }
            return t;
        }

        // 기록과 '진행 중' 지우기를 한 락 안에서 — 그 사이에 Mark 가 끼면 둘 다 못 보고 놓친다
        private void End(bool wmNull, long t0, bool ok)
        {
            double ms = Ms(Stopwatch.GetTimestamp() - t0);
            lock (_gate)
            {
                if (wmNull) { if (_wmSince != t0) return; _wmSince = 0; }   // Stop 이 이미 실패로 셌다
                else { if (_uiaSince != t0) return; _uiaSince = 0; }
                Add(wmNull, ms, ok);
            }
        }

        private void Add(bool wmNull, double ms, bool ok)
        {
            if (wmNull) { WmNullCount++; if (!ok) WmNullFailures++; if (ms > WmNullMaxMs) WmNullMaxMs = ms; }
            else { UiaCount++; if (!ok) UiaFailures++; if (ms > UiaMaxMs) UiaMaxMs = ms; }
        }

        private static double Ms(long ticks) { return ticks * 1000.0 / Stopwatch.Frequency; }
    }
}
'@
}

# 기준: 고치기 전엔 ② 가 UI 스레드에서 EP 카탈로그를 조회해(한 번에 수 초) 그동안 Dispatcher 가 내내 막혔다. 같은 조회를
# 보이지 않는 WPF 창의 Dispatcher 스레드에서 하고 이 도우미로 재 보면(2026-10-31, 콘솔·PowerShell 7/5.1) UI 스레드에서는 UIA 가 조회 시간
# 그대로 4~11초, WM_NULL 은 최대 0.7~3.2초, 스레드 풀로 옮기면 둘 다 최대 2~12 ms. 앱에서 이슈 #4 가 잰 창 응답은 0.4~0.5초였다.
# 선은 둘 다 250 ms — 이슈의 0.4~0.5초는 걸리고, 재생 중 프레임 그리기·장치 바꿔 끼우기(_yoloLock)가 섞이는 수십 ms 는 지나가게.
# 앱으로는 아직 재지 않은 선이다 — 옛·새 빌드로 이 테스트를 돌려 맞출 것
$MaxWmNullMs = 250
$MaxUiaMs    = 250

function Start-UiLatency($Proc, $Probe) {
    $l = [DetectViewTest.UiLatency]::new($Proc.MainWindowHandle, [System.Windows.Automation.AutomationElement]$Probe, 50)
    $l.Start()
    return $l
}

# -Mark: Mark() 까지(①②)의 최대·횟수. 없으면 잰 동안 전체
function Format-UiLatency($L, [switch]$Mark) {
    if ($Mark) {
        'WM_NULL 최대 {0:F0} ms ({1}회, 응답 없음 {2}) · UIA 최대 {3:F0} ms ({4}회, 실패 {5}) — 처음 {6:F1}초, 기준 {7} · {8} ms' -f `
            $L.MarkWmNullMaxMs, $L.MarkWmNullCount, $L.MarkWmNullFailures, $L.MarkUiaMaxMs, $L.MarkUiaCount, $L.MarkUiaFailures,
            ($L.MarkAtMs / 1000), $MaxWmNullMs, $MaxUiaMs
    }
    else {
        'WM_NULL 최대 {0:F0} ms ({1}회, 응답 없음 {2}) · UIA 최대 {3:F0} ms ({4}회, 실패 {5})' -f `
            $L.WmNullMaxMs, $L.WmNullCount, $L.WmNullFailures, $L.UiaMaxMs, $L.UiaCount, $L.UiaFailures
    }
}

# ①② 구간 판정: 둘 다 한 번 이상 재었고(0회면 그 구간을 재지 못한 것 — 통과로 치지 않는다) 실패·응답 없음이 없고 최대가 선 아래
function Test-UiResponsive($L) {
    $L.MarkWmNullCount -gt 0 -and $L.MarkUiaCount -gt 0 -and
    $L.MarkWmNullFailures -eq 0 -and $L.MarkUiaFailures -eq 0 -and
    $L.MarkWmNullMaxMs -lt $MaxWmNullMs -and $L.MarkUiaMaxMs -lt $MaxUiaMs
}

# 추론 콤보 툴팁(UIA HelpText)이 채워지면 NPU 가능 여부 확인이 끝난 것이다 (그 전엔 툴팁이 없다)
function Wait-NpuCheck($Root) {
    $combo = ById $Root 'InferenceSelector'
    $null = WaitFor { $h = $combo.Current.HelpText; $h -and $h -notmatch 'NPU 확인 중' } 20000
    return $combo.Current.HelpText
}

# 콤보를 펼쳐 항목을 고른다. 막힌 항목이면 고르지 않고 $false
function Select-Device($Root, [string]$Name) {
    $combo = ById $Root 'InferenceSelector'
    $ec = $combo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
    $ec.Expand(); Start-Sleep -Milliseconds 300
    $item = $combo.FindFirst($Tree::Descendants, (New-Object $PC ($AE::NameProperty, $Name)))
    $enabled = $null -ne $item -and $item.Current.IsEnabled
    if ($enabled) { $item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select() }
    $ec.Collapse(); Start-Sleep -Milliseconds 200
    return $enabled
}

function Get-SelectedDevice($Root) {
    $sel = (ById $Root 'InferenceSelector').GetCurrentPattern([System.Windows.Automation.SelectionPattern]::Pattern).Current.GetSelection()
    if ($sel.Count) { $sel[0].Current.Name } else { '' }
}

# 상태바 진행 표시 (평소엔 숨겨져 UIA 트리에 없다)
function Get-DeviceStatus($Root) {
    $e = ById $Root 'Txt_Device'
    if ($e) { NameOf $e } else { '' }
}

# 제목이 $Pattern 과 맞고 진행 표시가 사라질 때까지. 그동안 본 진행 문구를 모아 돌려준다.
# $Latency 를 주면 ①② 의 문구('NPU 확인 중' · '… 내려받는 중')가 아닌 것을 처음 볼 때 Mark() — 그 앞이 ①②.
# ③ 문구('준비 중'·'컴파일 중')만 기다리면 캐시로 ③ 이 폴링 간격(250 ms)보다 짧을 때 그 문구를 못 보고, 바꿔 끼우기·옛 세션 놓기까지
# 끝까지 ①② 로 잡혔다 → 빈 문구(끝남)·실패 문구에서도 끊는다
function Wait-Device($Proc, $Root, [string]$Pattern, [int]$TimeoutSec, $Latency = $null) {
    $seen = [System.Collections.Generic.List[string]]::new()
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($sw.Elapsed.TotalSeconds -lt $TimeoutSec) {
        $d = Get-DeviceStatus $Root
        if ($d) { $s = $d -replace '\s*\d+초$', ''; if (-not $seen.Contains($s)) { $seen.Add($s) } }
        if ($Latency -and $d -notlike 'NPU 확인 중*' -and $d -notlike '*내려받는 중*') { $Latency.Mark() }
        $Proc.Refresh()
        if ($Proc.MainWindowTitle -match $Pattern -and -not $d) {
            return @{ Ok = $true; Seconds = $sw.Elapsed.TotalSeconds; Seen = $seen }
        }
        Start-Sleep -Milliseconds 250
    }
    return @{ Ok = $false; Seconds = $sw.Elapsed.TotalSeconds; Seen = $seen }
}

# exe 폴더를 하드 링크로 본뜬다 (같은 볼륨이면 복사 없이 금방). 안 되면 복사. 바꿀 파일은 링크를 지우고 새로 놓는다
function New-ExeMirror([string]$Src) {
    $dst = "$Src.npu-test"
    if (Test-Path $dst) { Remove-Item $dst -Recurse -Force }
    foreach ($f in Get-ChildItem $Src -Recurse -File) {
        $target = Join-Path $dst $f.FullName.Substring($Src.Length).TrimStart('\')
        New-Item -ItemType Directory -Force (Split-Path $target) | Out-Null
        try { New-Item -ItemType HardLink -Path $target -Target $f.FullName -ErrorAction Stop | Out-Null }
        catch { Copy-Item $f.FullName $target }
    }
    return $dst
}

function Get-Md5([string]$Path) { (Get-FileHash $Path -Algorithm MD5).Hash }

# 앱의 KeyPart 와 같게 (캐시 키 조각: 영숫자·'.'·'-' 밖은 '-', 48자까지, 끝 '.' 뗌) — 파일 버전을 키 조각과 견주려고
function ConvertTo-KeyPart([string]$Version) {
    $s = $Version.Trim() -replace '[^A-Za-z0-9.\-]', '-'
    $s.Substring(0, [Math]::Min($s.Length, 48)).TrimEnd('.')
}

$proc = $null
$mirror = $null
$lat = $null
try {
    # ---------- 1) 최소 폭 툴바 + 2) 정상 짝으로 전환 ----------
    $proc = Start-DetectView $ExeDir
    $root = $AE::FromHandle($proc.MainWindowHandle)
    $play   = ById $root 'Btn_PlayPause'
    $status = ById $root 'Txt_Status'
    # 시작 때 NPU 확인(짝 모델 확인 → EP 카탈로그 조회)이 도는 동안의 창 응답 — 참고용 (창 크기 바꾸기·첫 화면 그리기가 섞인다)
    $lat = Start-UiLatency $proc $status

    # 최소 폭보다 좁게 요청하면 WPF 가 MinWidth(900)로 맞춘다 — 그 폭에서 조작 요소(왼쪽 패널)가 창 밖으로 나가거나 접히면 안 된다
    $null = [DetectViewTest.Window]::MoveWindow($proc.MainWindowHandle, 60, 60, 300, 700, $true)
    Start-Sleep -Milliseconds 800
    $win = $root.Current.BoundingRectangle
    $out = @()
    foreach ($id in 'CameraSelector', 'Btn_RefreshCameras', 'Btn_StartCamera', 'MonitorSelector', 'Btn_Screen', 'Btn_Stop', 'Btn_Sahi', 'InferenceSelector') {
        $r = (ById $root $id).Current.BoundingRectangle
        if ($r.Right -gt $win.Right -or $r.Width -le 0) { $out += $id }
    }
    Check '최소 폭에서 조작 요소가 창 안에' ($out.Count -eq 0) "창 폭 $([int]$win.Width)px, 밖으로 나간 것: $(if ($out) { $out -join ', ' } else { '없음' })"
    $null = [DetectViewTest.Window]::MoveWindow($proc.MainWindowHandle, 60, 60, 1200, 820, $true)
    Start-Sleep -Milliseconds 500

    $help = Wait-NpuCheck $root
    $lat.Stop()
    Note 'NPU 확인 동안 창 응답 (시작 때, 참고)' (Format-UiLatency $lat)
    $lat = $null
    Check 'NPU 가능 여부 확인' ($help -match 'NPU: ') "툴팁 끝='$(($help -split "`n")[-1])'"
    $npuReady = $false
    if ($help -match 'NPU 를 쓸 수 없음: (.+)') {
        $why = $Matches[1]
        # 건너뛰는 건 NPU(VitisAI 실행 공급자)가 없는 PC 와 짝 모델 파일이 없는 폴더뿐이다. 짝이 낡았거나(best.onnx 를 바꾸고
        # 짝을 다시 안 만듦)·양자화 모델이 아니거나·확인이 예외로 끝나면 실패 — 예전엔 사유를 가리지 않고 건너뛰어 '모두 통과'였다
        if ($why -match '실행 공급자\(VitisAI\)가 없음|\.npu\.onnx 없음') {
            Note 'NPU 전환 건너뜀' "$why — NPU 가 없는 PC 이거나 짝 모델이 없음 (짝은 tools\Make-NpuModel.ps1)"
        }
        else { Check 'NPU 사용 가능' $false $why }
    }
    else { $npuReady = $true }

    if ($npuReady) {
        Check 'NPU 선택' (Select-Device $root 'NPU') ''
        # 캐시가 없으면 첫 컴파일이 1분 넘게 걸린다
        $lat = Start-UiLatency $proc $status
        $r = Wait-Device $proc $root 'VitisAI' 240 $lat
        $lat.Stop()
        $proc.Refresh()
        Check 'NPU 로 전환' $r.Ok ("{0:F1}초 · 제목='{1}'" -f $r.Seconds, $proc.MainWindowTitle)
        Check '준비 진행 표시' ($r.Seen.Count -gt 0 -and ($r.Seen | Where-Object { $_ -like 'NPU*' })) "본 문구: $($r.Seen -join ' → ')"
        Check 'NPU 준비 동안 창 응답 (①②)' (Test-UiResponsive $lat) (Format-UiLatency $lat -Mark)
        Note 'NPU 전환 내내 창 응답 (③ 컴파일 포함)' (Format-UiLatency $lat)
        $lat = $null
        Check '선택 칸 = NPU' ((Get-SelectedDevice $root) -eq 'NPU') "'$(Get-SelectedDevice $root)'"

        # 컴파일 캐시 키 = NPU 모델 MD5 _ EP 버전 _ ONNX Runtime 버전 (이슈 #5 — 예전엔 런타임 버전이 없었다).
        # 앱이 이번에 쓴 키를 NPU 툴팁 끝('컴파일 캐시 키: …')에서 읽어 그 형식과 그 키 폴더(파일이 든)를 본다 — 캐시 폴더 목록만 보면
        # 예전 실행이 남긴 세 조각 폴더 하나로도 늘 통과했다. 버전을 못 읽으면 그 자리가 'unknown' 이고 '버전을 몰라' 안내가 붙는다 —
        # 이 PC 에선 둘 다 읽혀야 한다. ORT 조각은 앱 프로세스에 올라온 onnxruntime.dll 의 파일 버전과 같아야 한다(의미 버전으로 물러나지 않았는지).
        # Claude 도구에서 띄운 앱은 %LOCALAPPDATA% 쓰기가 Packages\Claude_*\LocalCache\Local 로 돌려져 캐시가 그쪽에 생긴다 → 두 곳 다 본다
        $md5 = Get-Md5 (Join-Path $ExeDir 'best.npu.onnx')
        $tip = (ById $root 'InferenceSelector').Current.HelpText
        $key = if ($tip -match '컴파일 캐시 키: (\S+)') { $Matches[1] } else { '' }
        $parts = @($key -split '_')
        # 다른 프로세스의 모듈 목록은 못 읽을 수도 있다(32비트 PowerShell 등) — 그땐 이 비교만 건너뛰고 내용에 '(못 읽음)'
        $ortFiles = @(try { $proc.Modules | Where-Object ModuleName -eq 'onnxruntime.dll' | ForEach-Object { $_.FileVersionInfo.FileVersion } |
                            Where-Object { $_ } } catch { })
        $keyOk = $parts.Count -eq 3 -and $parts[0] -eq $md5 -and $parts -notcontains 'unknown' -and
                 $parts[1] -match '^(\d+(\.\d+)+|lib\d{14}-\S+)$' -and $parts[2] -match '^\d+\.\d+' -and
                 ($ortFiles.Count -eq 0 -or @($ortFiles | ForEach-Object { ConvertTo-KeyPart $_ }) -contains $parts[2])
        $cacheRoots = @(Join-Path $env:LOCALAPPDATA 'DetectView\npu_cache') +
                      @(Get-ChildItem (Join-Path $env:LOCALAPPDATA 'Packages') -Directory -Filter 'Claude_*' -ErrorAction SilentlyContinue |
                        ForEach-Object { Join-Path $_.FullName 'LocalCache\Local\DetectView\npu_cache' })
        $keyDir = if ($key) {
            $cacheRoots | ForEach-Object { Join-Path $_ $key } |
                Where-Object { @(Get-ChildItem -LiteralPath $_ -Recurse -File -ErrorAction SilentlyContinue).Count -gt 0 } | Select-Object -First 1
        }
        Check '캐시 키에 EP·런타임 버전 (이번 실행)' ($keyOk -and $keyDir -and $tip -notmatch '버전을 몰라') `
            ("키='{0}' · 올라온 onnxruntime.dll {1} · 폴더: {2}" -f $key,
             $(if ($ortFiles) { $ortFiles -join ', ' } else { '(못 읽음)' }),
             $(if ($keyDir) { $keyDir } elseif ($key) { "파일이 든 폴더 없음 ($($cacheRoots -join ' · '))" } else { '툴팁에 키 없음' }))

        # NPU 로 파일 재생 — 원본 속도(30fps)를 따라가고 검출이 나와야 한다
        Open-VideoFile $root $Video
        $ok = WaitFor { (NameOf $status) -like '파일:*' -and (NameOf $play) -eq '일시정지' } 10000
        Check 'NPU 로 재생 시작' $ok "상태='$(NameOf $status)'"
        $range = (ById $root 'VideoSlider').GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern)
        Start-Sleep -Seconds 2
        $rate = Measure-PlaybackRate $range 2000
        Check 'NPU 재생 속도' ($rate -gt 0.9 -and $rate -lt 1.1) ('{0:N2} 배속' -f $rate)
        $det = ById $root 'Txt_DetectionCount'
        $ok = WaitFor { (NameOf $det) -match '검출 ([1-9]\d*)' } 10000
        Check 'NPU 검출' $ok "'$(NameOf $det)'"
        Note 'NPU 재생 처리 FPS (참고용)' (NameOf (ById $root 'Txt_InferenceFps'))

        # 재생 중 GPU 로 되돌리기 → 다시 NPU (이번엔 캐시) — 재생이 끊기면 안 된다
        Check 'GPU 선택 (재생 중)' (Select-Device $root 'GPU') ''
        $r = Wait-Device $proc $root '\(DirectML\)$' 30
        Check 'GPU 로 되돌림' $r.Ok ("{0:F1}초 · 본 문구: {1}" -f $r.Seconds, ($r.Seen -join ' → '))
        Start-Sleep -Seconds 1
        $rate = Measure-PlaybackRate $range 2000
        Check 'GPU 로 바꾼 뒤 재생 계속' ((NameOf $play) -eq '일시정지' -and $rate -gt 0.5) ('버튼={0} · {1:N2} 배속' -f (NameOf $play), $rate)
        Note 'GPU 재생 처리 FPS (참고용)' (NameOf (ById $root 'Txt_InferenceFps'))

        Check 'NPU 다시 선택 (재생 중)' (Select-Device $root 'NPU') ''
        $lat = Start-UiLatency $proc $status
        $r = Wait-Device $proc $root 'VitisAI' 60 $lat
        $lat.Stop()
        Check '다시 NPU (캐시)' $r.Ok ("{0:F1}초" -f $r.Seconds)
        # 재생 중엔 멈추면 화면이 서므로 사용자 눈에 가장 잘 띄는 경우다
        Check '재생 중 NPU 준비 동안 창 응답 (①②)' (Test-UiResponsive $lat) (Format-UiLatency $lat -Mark)
        Note '재생 중 NPU 전환 내내 창 응답' (Format-UiLatency $lat)
        $lat = $null
        Start-Sleep -Seconds 1
        $rate = Measure-PlaybackRate $range 2000
        Check 'NPU 로 바꾼 뒤 재생 계속' ((NameOf $play) -eq '일시정지' -and $rate -gt 0.5) ('버튼={0} · {1:N2} 배속' -f (NameOf $play), $rate)

        Click (ById $root 'Btn_Stop')
        $ok = WaitFor { (NameOf $status) -eq '정지됨' } 5000
        Check '정지' $ok "상태='$(NameOf $status)'"

        if ($ColdCancel) {
            # 컴파일 도중 GPU 로 되돌리면: 컴파일이 끝나도 GPU 에 머물고, 다시 고르면 그 컴파일이 남긴 캐시로 수 초
            Check 'GPU 로 (취소 시험 준비)' ((Select-Device $root 'GPU') -and (Wait-Device $proc $root '\(DirectML\)$' 30).Ok) ''
            Remove-Item (Join-Path $env:LOCALAPPDATA 'DetectView\npu_cache') -Recurse -Force -ErrorAction SilentlyContinue
            $null = Select-Device $root 'NPU'
            $ok = WaitFor { (Get-DeviceStatus $root) -like 'NPU 컴파일 중*' } 30000
            Check '캐시 없이 컴파일 시작' $ok "상태바='$(Get-DeviceStatus $root)'"
            $null = Select-Device $root 'GPU'
            Start-Sleep -Seconds 120
            $proc.Refresh()
            Check '컴파일 도중 취소 → GPU 유지' ($proc.MainWindowTitle -match '\(DirectML\)$' -and -not (Get-DeviceStatus $root)) "제목='$($proc.MainWindowTitle)'"
            $null = Select-Device $root 'NPU'
            $r = Wait-Device $proc $root 'VitisAI' 60
            Check '취소된 컴파일의 캐시로 다시 NPU' ($r.Ok -and $r.Seconds -lt 30) ("{0:F1}초" -f $r.Seconds)
        }
    }
    Stop-DetectView $proc; $proc.WaitForExit(10000) | Out-Null; $proc = $null

    # ---------- 3) 실패 경로 (본뜬 폴더에서) ----------
    $mirror = New-ExeMirror $ExeDir
    $twin = Join-Path $mirror 'best.npu.onnx'
    $model = Join-Path $mirror 'best.onnx'
    $hasTwin = Test-Path (Join-Path $ExeDir 'best.npu.onnx')

    # 짝과 다른 모델 하나 (저장소 루트·배포 폴더 모델 중 MD5 가 다른 것). 없으면 그 경우만 건너뛴다
    $other = @((Join-Path $RepoRoot 'best.onnx'), (Join-Path $RepoRoot 'publish_gpu_ver\best.onnx')) |
             Where-Object { (Test-Path $_) -and (Get-Md5 $_) -ne (Get-Md5 (Join-Path $ExeDir 'best.onnx')) } | Select-Object -First 1

    $cases = @(
        @{ Name = '짝 모델 없음'; Expect = 'best\.npu\.onnx 없음'; Prepare = { Remove-Item $twin -Force -ErrorAction SilentlyContinue } }
        @{ Name = '다른 모델의 짝'; Expect = '짝이 안 맞음'; Skip = if (-not $hasTwin) { '원래 폴더에 짝이 없음' } elseif (-not $other) { 'MD5 가 다른 모델이 없음' };
           Prepare = { Copy-Item (Join-Path $ExeDir 'best.npu.onnx') $twin -Force; Remove-Item $model -Force; Copy-Item $other $model } }
        @{ Name = 'FP32 를 짝 이름으로'; Expect = '양자화 모델이 아님';
           Prepare = { Remove-Item $model -Force; Copy-Item (Join-Path $ExeDir 'best.onnx') $model; Remove-Item $twin -Force -ErrorAction SilentlyContinue; Copy-Item $model $twin } }
    )
    foreach ($c in $cases) {
        if ($c.Skip) { Note "실패 경로: $($c.Name)" "건너뜀 — $($c.Skip)"; continue }
        & $c.Prepare
        $proc = Start-DetectView $mirror
        $root = $AE::FromHandle($proc.MainWindowHandle)
        $help = Wait-NpuCheck $root
        $enabled = Select-Device $root 'NPU'   # 막혀 있으면 고르지 못한다
        $proc.Refresh()
        Check "실패 경로: $($c.Name)" ((-not $enabled) -and $help -match $c.Expect -and $proc.MainWindowTitle -match '\(DirectML\)$') `
            "NPU 항목 열림=$enabled · 툴팁 끝='$(($help -split "`n")[-1])'"
        Stop-DetectView $proc; $proc.WaitForExit(10000) | Out-Null; $proc = $null
    }
}
catch { Check '예외' $false $_.Exception.Message }
finally {
    if ($lat) { $lat.Stop() }   # 재는 스레드가 Run-All 의 다음 테스트까지 남지 않게
    Stop-DetectView $proc
    if ($proc) { $proc.WaitForExit(10000) | Out-Null }
    if ($mirror -and (Test-Path $mirror)) { Remove-Item $mirror -Recurse -Force -ErrorAction SilentlyContinue }
}

Complete-Test
