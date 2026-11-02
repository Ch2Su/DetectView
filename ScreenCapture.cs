using OpenCvSharp;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace YOLO11_Test
{
    /// <summary>모니터 1대의 정보(좌표·크기는 가상 화면 기준 물리 픽셀).</summary>
    internal readonly record struct MonitorInfo(
        int Index, string Device, int X, int Y, int Width, int Height, bool IsPrimary)
    {
        public string Display =>
            $"{Index}: {Width}×{Height}{(IsPrimary ? " (주)" : "")}";
    }

    /// <summary>모니터 기준 물리 픽셀 사각형 (Right·Bottom 은 포함하지 않음). 불변이라 스레드 간에 참조째 넘긴다.</summary>
    internal sealed record PixelRect(int Left, int Top, int Right, int Bottom)
    {
        public bool Contains(int left, int top, int right, int bottom) =>
            left >= Left && top >= Top && right <= Right && bottom <= Bottom;
    }

    /// <summary>
    /// 화면 캡처 공통 인터페이스. 구현체는 스레드 친화적일 수 있으므로
    /// <b>사용할 스레드에서 생성하고 같은 스레드에서 Dispose</b> 한다.
    /// </summary>
    internal interface IScreenCapture : IDisposable
    {
        /// <summary>캡처 폭·높이(물리 픽셀)</summary>
        int Width { get; }
        int Height { get; }

        /// <summary>상태 표시용 방식 이름 (예: "DXGI", "GDI")</summary>
        string Method { get; }

        /// <summary>마지막으로 반환한 화면을 얻은 시각(Stopwatch 타임스탬프) — 지연 측정용</summary>
        long FrameTimestamp { get; }

        /// <summary>
        /// 이 영역 안에서만 바뀐 화면은 "변화 없음"으로 본다 (예: 앱 자신의 상태바).
        /// 변경 영역 정보를 주는 구현(DXGI)만 사용하고, 나머지는 무시한다.
        /// </summary>
        PixelRect? IgnoreRegion { get; set; }

        /// <summary>
        /// 화면을 못 받고 있는 이유 (null = 정상). DXGI 는 복제가 끊겨 다시 복제하기를 기다리는 동안, GDI 는 화면을 못 읽은 동안.
        /// <see cref="TryCapture"/> 의 null 은 '화면 변화 없음'과 '끊김'을 가리지 않아 둔다 — HUD 가 1초 넘게 이어지면 FPS 칸에 띄운다
        /// (예전엔 끊겨도 '대기 (화면 변화 없음)'으로만 보였다 — 이슈 #1)
        /// </summary>
        string? Interruption { get; }

        /// <summary>
        /// 마지막 <see cref="TryCapture"/> 가 돌려준 화면이 새 화면이 아니라 마지막 화면을 다시 준 것(forceLatest)인지.
        /// HUD 는 이걸로 '재개 재검출'(다시 준 화면)과 '받은 화면'(새 화면)을 따로 센다 — 재개 버튼 글자가 바뀌어 새 화면이 와도
        /// 재개 재검출로 치지 않아야 UI 테스트가 강제 경로가 깨진 걸 잡는다 (이슈 #11). GDI 는 매번 새로 캡처해 늘 false
        /// </summary>
        bool Redelivered { get; }

        /// <summary>
        /// 새 화면이 있으면 BGRA Mat 을 반환하고, 화면 변화가 없으면 null 을 반환한다.
        /// 반환 Mat 은 내부 버퍼를 <b>공유</b>하므로 다음 호출 전까지만 유효하다.
        /// 같은 장치·출력으로는 다시 살릴 수 없게 끊기면 <see cref="ScreenCaptureLostException"/> 을 던진다 — 이 객체를 버리고 새로 만들 것.
        /// </summary>
        /// <param name="timeoutMs">새 화면을 기다릴 최대 시간. GDI 는 변화를 알 수 없어 화면을 못 읽었을 때만 이만큼 쉰다
        /// (null 을 곧바로 돌려주면 호출하는 루프가 헛돈다)</param>
        /// <param name="forceLatest">새 화면을 기다리지 않고 마지막 화면을 곧바로 다시 반환한다 (재개·검출 재시도 —
        /// <see cref="Redelivered"/> = true). 아직 받은 화면이 없으면(갓 만든 DXGI 복제) 보통처럼 새 화면을 기다린다. GDI 는 무시하고 새로 캡처한다</param>
        Mat? TryCapture(int timeoutMs, bool forceLatest = false);
    }

    /// <summary>
    /// 같은 장치·출력으로는 다시 살릴 수 없는 캡처 상실 — GPU 장치 제거(TDR·드라이버 재시작·업데이트), 회전,
    /// UNSUPPORTED·모르는 실패가 프레임 없이 몇 초 넘게 이어짐(출력이 다른 어댑터 아래로 옮겨감 등). <see cref="IScreenCapture.TryCapture"/> 가
    /// 던지면 호출 측(HUD 루프)이 캡처를 버리고 새로 만든다 — DXGI 를 쓰던 HUD 는 DXGI 로 바로 다시 열고, 잠금 화면처럼 기다리면 될 실패면
    /// 캡처 없이 기다렸다가 다시 열며, 기다려도 안 될 실패(회전 등)만 GDI 로 넘어간다. GDI 로 넘어가도 몇 초마다 DXGI 를 다시 열어 본다.
    /// 잠금 화면·UAC 보안 데스크톱처럼 기다리면 같은 장치로 다시 복제되는 접근 상실엔 던지지 않는다 — 장치째 새로 만들어도 그동안은
    /// DXGI 를 못 열어 장치 생성(0.2초)만 버리고, 같은 장치로 다시 복제하면 풀리자마자(0.25초 안) 이어진다
    /// </summary>
    internal sealed class ScreenCaptureLostException(string message, Exception? inner = null) : Exception(message, inner);

    internal static class ScreenCapture
    {
        /// <summary>
        /// 모니터용 캡처를 만든다. DXGI Desktop Duplication(GPU 복사, 화면이 바뀔 때만 프레임)을
        /// 우선 쓰고, 지원되지 않는 환경(원격 데스크톱, 회전 디스플레이 등)에서는 GDI 로 대체한다.
        /// HUD 루프는 DXGI 를 한 번도 못 쓴 HUD 가 캡처를 새로 만들 때도 이걸 쓴다 (DXGI 를 쓰던 HUD 는 <see cref="DxgiScreenCapture"/> 를
        /// 바로 만들고, 기다려도 안 될 실패만 <see cref="Fallback"/> 으로 GDI 로 넘어간다).
        /// </summary>
        public static IScreenCapture Create(MonitorInfo mon)
        {
            try
            {
                return new DxgiScreenCapture(mon.Device);
            }
            catch (Exception ex)
            {
                return Fallback(mon, ex);
            }
        }

        /// <summary>DXGI 를 못 열었을 때(<paramref name="dxgiFailure"/>) 쓰는 GDI 캡처 — 상태바에 붙는 사유·툴팁 안내가 들어 있다</summary>
        public static GdiScreenCapture Fallback(MonitorInfo mon, Exception dxgiFailure)
        {
            Debug.WriteLine($"DXGI 캡처 사용 불가 → GDI 로 대체: {dxgiFailure.Message}");
            // 사유를 상태바에 보여 준다. GDI 는 DXGI 보다 몇 배 느린데, 예전엔 사유가 Debug 출력에만 남아
            // 사용자는 "왜 HUD 가 느린지" 알 수 없었다
            var (reason, help) = DescribeDxgiFailure(dxgiFailure, mon.Device);
            return new GdiScreenCapture(mon.X, mon.Y, mon.Width, mon.Height)
            {
                FallbackReason = reason,
                FallbackHelp = help
            };
        }

        // Reason: 상태바에 붙는 짧은 문구 (창 폭이 좁으면 잘리므로 할 일만 짧게)
        // Help:   상태바 툴팁에 띄울 자세한 해결 방법 (없으면 null)
        private static (string Reason, string? Help) DescribeDxgiFailure(Exception ex, string deviceName) => ex switch
        {
            // DXGI_ERROR_UNSUPPORTED. 하이브리드 노트북에서 출력이 실제 연결과 다른 어댑터 아래로 열거되면(프로세스 GPU 모드 탓)
            // 복제가 지원되지 않는다. 고칠 설정은 모니터가 어느 GPU 에 붙었는지에 따라 반대다 — 노트북 화면(내장)은 '절전'(9/23 확인),
            // 외장 GPU 에 붙은 외부 모니터는 '절전'이면 오히려 실패했다(10/24 확인) → 실제 연결과 지금 설정을 보고 고른다
            SharpGen.Runtime.SharpGenException { HResult: unchecked((int)0x887A0004) } => HybridGpuAdvice(deviceName),
            NotSupportedException => (ex.Message, null),   // 회전 디스플레이·출력 없음 (DxgiScreenCapture 의 한국어 메시지)
            _ => ($"DXGI 불가: 0x{ex.HResult:X8}", null),
        };

        private static (string Reason, string? Help) HybridGpuAdvice(string deviceName)
        {
            var gpu = DisplayTopology.FindMonitorGpu(deviceName);
            var pref = DisplayTopology.ReadGpuPreference(Environment.ProcessPath);
            // 다른 GPU 로 강제돼 있으면 강제를 푼다('Windows에서 결정' — 이때는 모니터마다 자기 GPU 로 열거돼 두 모니터 모두 됐다).
            // 강제가 없는데 실패했으면 이 모니터의 GPU 로 강제한다. 이미 그쪽으로 강제됐는데 실패하면 이 원인이 아니다
            (string Setting, string Reason)? advice = (gpu?.Kind, pref) switch
            {
                (DisplayTopology.GpuKind.LowPower, DisplayTopology.GpuPreferenceSetting.HighPerformance) or
                (DisplayTopology.GpuKind.HighPerformance, DisplayTopology.GpuPreferenceSetting.PowerSaving) =>
                    ("'Windows에서 결정'", $"느림 — 그래픽 설정의 {SettingName(pref)} 지정을 풀고 재시작"),
                (DisplayTopology.GpuKind.LowPower, not DisplayTopology.GpuPreferenceSetting.PowerSaving) =>
                    ("'절전'", "느림 — 그래픽 설정에서 이 앱을 '절전'으로 지정 후 재시작"),
                (DisplayTopology.GpuKind.HighPerformance, not DisplayTopology.GpuPreferenceSetting.HighPerformance) =>
                    ("'고성능'", "느림 — 그래픽 설정에서 이 앱을 '고성능'으로 지정 후 재시작"),
                _ => null,
            };
            return advice is { } a
                ? (a.Reason, HybridGpuHelp(deviceName, gpu!.Value, pref, a.Setting))
                : ("DXGI 불가: 0x887A0004 (원인 미상)", UnexplainedHelp(deviceName, gpu, pref));
        }

        private static string SettingName(DisplayTopology.GpuPreferenceSetting? pref) => pref switch
        {
            DisplayTopology.GpuPreferenceSetting.PowerSaving => "'절전'",
            DisplayTopology.GpuPreferenceSetting.HighPerformance => "'고성능'",
            _ => "'Windows에서 결정'",
        };

        // 설정은 실행 파일 "경로"별로 저장되므로 지금 실행 중인 exe 경로를 그대로 보여 준다
        // (Debug 빌드와 배포본은 경로가 달라 한쪽만 설정된 채 헷갈린 적이 있다)
        private static string HybridGpuHelp(string deviceName, DisplayTopology.MonitorGpu gpu,
                                            DisplayTopology.GpuPreferenceSetting? pref, string setting) =>
            "DXGI 화면 캡처를 쓸 수 없어 느린 GDI 로 캡처하고 있습니다.\n" +
            $"이 모니터({deviceName})는 {gpu.Name} 에 연결돼 있는데, 이 앱의 그래픽 설정({SettingName(pref)})으로는\n" +
            "다른 GPU 쪽에서 화면을 복제하게 돼 지원되지 않습니다 (하이브리드 GPU 노트북).\n\n" +
            "해결 방법\n" +
            " 1. Windows 설정 → 시스템 → 디스플레이 → 그래픽\n" +
            $" 2. 아래 실행 파일의 GPU 기본 설정을 {setting}으로\n" +
            $"    {Environment.ProcessPath}\n" +
            " 3. DetectView 를 다시 시작\n\n" +
            (setting == "'Windows에서 결정'"
                ? "'Windows에서 결정'이면 보통 모니터마다 자기 GPU 로 복제돼 노트북 화면·외부 모니터 모두 됩니다.\n" +
                  $"그래도 안 되면 {(gpu.Kind == DisplayTopology.GpuKind.LowPower ? "'절전'" : "'고성능'")}으로 지정하세요.\n"
                : "이렇게 지정하면 다른 GPU 에 붙은 모니터(노트북 화면 ↔ 외부 모니터)는 반대로 GDI 가 될 수 있습니다.\n") +
            "검출(추론)은 이 설정과 관계없이 외장 GPU 에서 실행됩니다.\n" +
            "설정은 실행 파일 경로별이라, 다른 폴더의 exe 는 따로 지정해야 합니다.";

        // 하이브리드 GPU 로 설명이 안 될 때 (GPU 가 하나·판별 실패·이미 맞는 쪽으로 지정됨): 확인할 것만 알린다
        private static string UnexplainedHelp(string deviceName, DisplayTopology.MonitorGpu? gpu,
                                              DisplayTopology.GpuPreferenceSetting? pref) =>
            "DXGI 화면 캡처를 쓸 수 없어(DXGI_ERROR_UNSUPPORTED) 느린 GDI 로 캡처하고 있습니다.\n" +
            $"모니터: {deviceName} · 연결된 GPU: {gpu?.Name ?? "알 수 없음"} ({gpu?.Kind.ToString() ?? "판별 실패"}) · " +
            $"이 앱의 그래픽 설정: {SettingName(pref)}\n\n" +
            "하이브리드 GPU 노트북이면 Windows 설정 → 시스템 → 디스플레이 → 그래픽에서\n" +
            "아래 실행 파일의 GPU 기본 설정을 바꿔 보고 DetectView 를 다시 시작하세요.\n" +
            $"    {Environment.ProcessPath}\n" +
            "원격 데스크톱 등 DXGI 복제를 지원하지 않는 환경에서도 생깁니다.";

        [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(
            IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFOEX info);

        private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data);

        private const uint MONITORINFOF_PRIMARY = 0x1;

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct MONITORINFOEX
        {
            public uint cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string szDevice;
        }

        /// <summary>연결된 모니터 목록(물리 픽셀 좌표). 실패 시 빈 목록.</summary>
        public static List<MonitorInfo> GetMonitors()
        {
            var list = new List<MonitorInfo>();
            try
            {
                int idx = 0;
                EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr h, IntPtr _, ref RECT _, IntPtr _) =>
                {
                    var mi = new MONITORINFOEX { cbSize = (uint)Marshal.SizeOf<MONITORINFOEX>() };
                    if (GetMonitorInfoW(h, ref mi))
                    {
                        var r = mi.rcMonitor;
                        list.Add(new MonitorInfo(
                            idx++, mi.szDevice ?? "",
                            r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top,
                            (mi.dwFlags & MONITORINFOF_PRIMARY) != 0));
                    }
                    return true;
                }, IntPtr.Zero);
            }
            catch { /* 실패 시 빈 목록 */ }
            return list;
        }
    }
}
