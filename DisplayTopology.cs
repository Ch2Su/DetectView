using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace DetectView
{
    /// <summary>
    /// 모니터가 <b>실제로</b> 어느 GPU 에 연결돼 있는지와, 이 앱의 Windows 그래픽 설정(GPU 기본 설정)을 알아낸다.
    /// DXGI 의 출력 열거는 프로세스의 GPU 모드에 따라 바뀌어서(하이브리드 노트북) 그걸로는 알 수 없다 —
    /// 시스템 디스플레이 구성(QueryDisplayConfig)에서 모니터(GDI 장치 이름)가 걸린 대상 어댑터의 LUID 를 읽는다.
    /// HUD 가 GDI 로 떨어졌을 때 '절전'/'고성능' 중 맞는 쪽을 권하는 데 쓴다 (ScreenCapture.DescribeDxgiFailure)
    /// </summary>
    internal static class DisplayTopology
    {
        /// <summary>
        /// LowPower = 저전력(내장) GPU, HighPerformance = 고성능(외장) GPU, Only = GPU 가 하나뿐.
        /// 판정은 추론 GPU 선택과 같은 GpuAdapters.IsHighPerformance (내장 여부를 못 읽었거나 모두 같은 종류면 예전처럼 전용 VRAM 최대 = 고성능)
        /// </summary>
        internal enum GpuKind { Unknown, LowPower, HighPerformance, Only }

        internal readonly record struct MonitorGpu(string Name, GpuKind Kind);

        /// <summary>그래픽 설정 값 (HKCU UserGpuPreferences 의 GpuPreference). 항목이 없으면 null</summary>
        internal enum GpuPreferenceSetting { Default = 0, PowerSaving = 1, HighPerformance = 2 }

        /// <param name="deviceName">모니터 장치 이름 (예: \\.\DISPLAY6)</param>
        /// <returns>못 알아내면 null</returns>
        public static MonitorGpu? FindMonitorGpu(string deviceName)
        {
            try
            {
                long? target = null;
                foreach (var p in ActivePaths())
                    if (string.Equals(p.Device, deviceName, StringComparison.OrdinalIgnoreCase)) { target = p.TargetLuid; break; }
                if (target == null) return null;
                return Classify(GpuAdapters.Enumerate(), target.Value);
            }
            catch
            {
                return null;   // 안내 문구를 고르는 용도라, 실패하면 일반 안내로 간다
            }
        }

        /// <summary>모니터가 붙은 어댑터(<paramref name="targetLuid"/>)의 종류. 하드웨어 어댑터 목록만으로 정하므로 가짜 목록으로도 확인할 수 있다</summary>
        internal static MonitorGpu Classify(IReadOnlyList<GpuAdapters.Adapter> adapters, long targetLuid)
        {
            // 저전력/고성능은 하드웨어 종류로 가른다 — 내장이면 저전력, 외장이면 고성능 (GpuAdapters.IsHighPerformance, 추론 GPU 선택과 같은 판정).
            // 예전엔 전용 VRAM 이 가장 큰 쪽을 고성능으로 봐서, 내장 GPU 의 전용 메모리를 크게 잡으면(AMD VGM·BIOS UMA) 안내가 거꾸로 나왔다 (이슈 #3).
            // IDXGIFactory6.EnumAdapterByGpuPreference 는 이 exe 에 '절전'이 걸려 있으면 판별이 틀렸다
            // (설정 없는 프로세스에선 맞고 '절전' 프로세스에서만 원인 미상으로 빠짐 — 순서가 그 설정을 따르는 것으로 보인다)
            for (int i = 0; i < adapters.Count; i++)
                if (adapters[i].Luid == targetLuid)
                    return new MonitorGpu(adapters[i].Name,
                        adapters.Count <= 1 ? GpuKind.Only
                        : GpuAdapters.IsHighPerformance(adapters[i], adapters) ? GpuKind.HighPerformance
                        : GpuKind.LowPower);
            return new MonitorGpu("GPU", GpuKind.Unknown);
        }

        /// <summary>
        /// 이 실행 파일의 그래픽 설정. Windows 설정 → 디스플레이 → 그래픽에서 지정한 값으로, 실행 파일 <b>경로별</b>로 저장된다
        /// (HKCU\Software\Microsoft\DirectX\UserGpuPreferences, 값 이름 = exe 전체 경로, 데이터 "GpuPreference=1;")
        /// </summary>
        public static GpuPreferenceSetting? ReadGpuPreference(string? exePath)
        {
            if (string.IsNullOrEmpty(exePath)) return null;
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\DirectX\UserGpuPreferences");
                if (key?.GetValue(exePath) is not string data) return null;
                var m = Regex.Match(data, @"GpuPreference=(\d)");
                return m.Success ? (GpuPreferenceSetting)int.Parse(m.Groups[1].Value) : null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>활성 디스플레이 경로마다 (GDI 장치 이름, 원본 어댑터 LUID, 대상 어댑터 LUID)</summary>
        internal static List<(string Device, long SourceLuid, long TargetLuid)> ActivePaths()
        {
            var list = new List<(string, long, long)>();
            if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out uint numPaths, out uint numModes) != 0) return list;
            var paths = new DISPLAYCONFIG_PATH_INFO[numPaths];
            var modes = new DISPLAYCONFIG_MODE_INFO[numModes];
            if (QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref numPaths, paths, ref numModes, modes, IntPtr.Zero) != 0) return list;

            for (int i = 0; i < numPaths; i++)
            {
                var src = paths[i].sourceInfo;
                var req = new DISPLAYCONFIG_SOURCE_DEVICE_NAME
                {
                    header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
                    {
                        type = DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME,
                        size = (uint)Marshal.SizeOf<DISPLAYCONFIG_SOURCE_DEVICE_NAME>(),
                        adapterId = src.adapterId,
                        id = src.id,
                    }
                };
                if (DisplayConfigGetDeviceInfo(ref req) != 0) continue;
                list.Add((req.viewGdiDeviceName,
                          ToLong(src.adapterId.LowPart, src.adapterId.HighPart),
                          ToLong(paths[i].targetInfo.adapterId.LowPart, paths[i].targetInfo.adapterId.HighPart)));
            }
            return list;
        }

        private static long ToLong(uint low, int high) => ((long)high << 32) | low;

        // ---------- QueryDisplayConfig (CCD) ----------
        private const uint QDC_ONLY_ACTIVE_PATHS = 0x2;
        private const uint DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME = 1;

        [DllImport("user32.dll")]
        private static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPathArrayElements, out uint numModeInfoArrayElements);

        [DllImport("user32.dll")]
        private static extern int QueryDisplayConfig(uint flags, ref uint numPathArrayElements, [Out] DISPLAYCONFIG_PATH_INFO[] pathArray,
            ref uint numModeInfoArrayElements, [Out] DISPLAYCONFIG_MODE_INFO[] modeInfoArray, IntPtr currentTopologyId);

        [DllImport("user32.dll")]
        private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_SOURCE_DEVICE_NAME requestPacket);

        [StructLayout(LayoutKind.Sequential)]
        private struct LUID { public uint LowPart; public int HighPart; }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_PATH_SOURCE_INFO { public LUID adapterId; public uint id; public uint modeInfoIdx; public uint statusFlags; }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_PATH_TARGET_INFO
        {
            public LUID adapterId;
            public uint id, modeInfoIdx, outputTechnology, rotation, scaling;
            public uint refreshRateNumerator, refreshRateDenominator;
            public uint scanLineOrdering;
            public int targetAvailable;
            public uint statusFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_PATH_INFO
        {
            public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo;
            public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo;
            public uint flags;
        }

        // 내용은 읽지 않는다 — QueryDisplayConfig 가 요구하는 배열 크기(64바이트)만 맞춘다
        [StructLayout(LayoutKind.Sequential, Size = 64)]
        private struct DISPLAYCONFIG_MODE_INFO { public uint infoType; }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_DEVICE_INFO_HEADER { public uint type; public uint size; public LUID adapterId; public uint id; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DISPLAYCONFIG_SOURCE_DEVICE_NAME
        {
            public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string viewGdiDeviceName;
        }
    }
}
