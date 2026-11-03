using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Vortice.DXGI;

namespace DetectView
{
    /// <summary>
    /// GPU 어댑터 목록과 고성능/저전력 판정. 추론 GPU 선택(MainWindow.SelectBestGpu)과
    /// HUD 의 그래픽 설정 안내(DisplayTopology.FindMonitorGpu)가 이 판정 하나를 같이 쓴다.
    /// 예전엔 둘이 '전용 VRAM 이 가장 큰 쪽 = 고성능'을 따로 구현해 동률 처리부터 달랐고(>= 대 >), 그 기준 자체가
    /// 내장 GPU 의 전용 메모리를 외장 이상으로 잡은 PC(AMD 가변 그래픽 메모리 VGM·BIOS UMA 프레임 버퍼)에서 뒤집혔다 (이슈 #3)
    /// → 메모리 크기가 아니라 <b>내장 GPU 인지</b>로 가른다.
    /// WPF 를 쓰지 않는다 — experiments/TrackerCheck·RuntimeParity 가 이 파일을 소스로 링크한다
    /// </summary>
    internal static class GpuAdapters
    {
        /// <summary>DXGI 가 열거한 하드웨어 어댑터 하나</summary>
        /// <param name="Index">DXGI 열거 순번. 프로세스의 GPU 모드('절전'/'고성능')에 따라 순서가 바뀌므로, 추론 GPU 를 하나 고를 때 동률만 가른다</param>
        /// <param name="Integrated">내장 GPU 인가 (DXCore IsIntegrated). 못 읽으면 null</param>
        internal readonly record struct Adapter(int Index, string Name, uint VendorId, uint DeviceId, long Luid,
                                                ulong DedicatedVideoMemory, bool? Integrated);

        /// <summary>하드웨어 어댑터 목록 (소프트웨어 렌더러 제외), DXGI 열거 순서대로. DXGI 팩터리를 못 만들면 예외</summary>
        public static List<Adapter> Enumerate()
        {
            var list = new List<Adapter>();
            DXGI.CreateDXGIFactory1(out IDXGIFactory1? factory).CheckError();
            using (factory)
            {
                for (uint i = 0; factory!.EnumAdapters1(i, out IDXGIAdapter1 adapter).Success; i++)
                {
                    using (adapter)
                    {
                        var desc = adapter.Description1;
                        if ((desc.Flags & AdapterFlags.Software) != 0) continue;   // Microsoft Basic Render Driver
                        list.Add(new Adapter((int)i, desc.Description, desc.VendorId, desc.DeviceId,
                                             ((long)desc.Luid.HighPart << 32) | desc.Luid.LowPart,
                                             (ulong)desc.DedicatedVideoMemory,   // PointerUSize → ulong (long 캐스팅은 오버플로)
                                             null));
                    }
                }
            }
            ReadIntegrated(list);
            return list;
        }

        // 아래 판정은 LINQ 대신 반복문으로 쓴다: 값 형식(Adapter)으로 LINQ 를 처음 부르면 JIT 이 그 형식 전용 코드를 새로 만들어
        // 첫 호출이 약 10 ms 걸렸다(반복문은 1 ms 안팎, 콘솔 실측). 앱 시작 때 UI 스레드에서 도는 길이다

        /// <summary>
        /// 고성능 쪽 GPU 인가 (<paramref name="all"/> 에 <paramref name="adapter"/> 가 들어 있어야 한다).
        /// 모든 어댑터의 내장 여부를 읽었고 내장·외장이 섞여 있으면 <b>내장이 아닌 것</b>이 고성능, 내장은 저전력이다 — 메모리 크기는 보지 않는다.
        /// 그 밖(모두 같은 종류, 하나라도 못 읽음)엔 예전처럼 전용 VRAM 이 가장 큰 쪽이 고성능이다(같으면 모두).
        /// 열거 순서는 보지 않는다 — 순서는 이 exe 의 그래픽 설정을 따라 바뀐다(EnumAdapterByGpuPreference 를 기각한 이유, DisplayTopology)
        /// </summary>
        public static bool IsHighPerformance(Adapter adapter, IReadOnlyList<Adapter> all)
        {
            if (KindsKnownAndMixed(all)) return adapter.Integrated == false;
            for (int i = 0; i < all.Count; i++)
                if (all[i].DedicatedVideoMemory > adapter.DedicatedVideoMemory) return false;
            return true;
        }

        /// <summary>추론 GPU: 고성능 쪽 중 전용 VRAM 이 가장 큰 것, 같으면 먼저 열거된 것(Index). 어댑터가 없으면 null</summary>
        public static Adapter? Best(IReadOnlyList<Adapter> all)
        {
            Adapter? best = null;
            for (int i = 0; i < all.Count; i++)
            {
                var a = all[i];
                if (!IsHighPerformance(a, all)) continue;
                if (best is not { } b || a.DedicatedVideoMemory > b.DedicatedVideoMemory
                    || (a.DedicatedVideoMemory == b.DedicatedVideoMemory && a.Index < b.Index))
                    best = a;
            }
            return best;
        }

        // 내장 여부를 하나라도 못 읽었으면 쓰지 않는다 — 어댑터마다 다른 기준(내장 여부·VRAM)으로 견주면 순위가 꼬인다
        // (외장 4GB > 내장 8GB 는 내장 여부로, 내장 8GB > 모름 6GB 는 VRAM 으로 이기면 모름 6GB > 외장 4GB 가 돼 돌고 돈다)
        private static bool KindsKnownAndMixed(IReadOnlyList<Adapter> all)
        {
            bool anyIntegrated = false, anyDiscrete = false;
            for (int i = 0; i < all.Count; i++)
            {
                if (all[i].Integrated is not bool integrated) return false;
                if (integrated) anyIntegrated = true;
                else anyDiscrete = true;
            }
            return anyIntegrated && anyDiscrete;
        }

        // ---------- 내장 여부: DXCore (dxcore.dll, Windows 10 2004+) ----------
        // 후보 셋을 이 PC(AMD Radeon 840M 내장 + RTX 5060 Laptop, 하이브리드)에서 콘솔로 재 봤다 (2026-10-31). 답은 셋 다 AMD 내장 · NVIDIA 외장:
        //  - DXCore IsIntegrated: 장치를 만들지 않아 GPU 드라이버 DLL 이 올라오지 않고(dxcore.dll 은 dxgi.dll 이 이미 올려 둔다), 싸다 —
        //    이 판정 전체가 옛 VRAM 판정보다 첫 호출 약 8 ms(JIT 포함), 그 뒤 약 2 ms 더 든다.
        //    Windows ML(ONNX Runtime 1.27)의 장치 탐색도 'Discrete' 메타데이터로 같은 답(AMD 0 · NVIDIA 1)을 낸다
        //  - D3D11 OPTIONS2 의 UnifiedMemoryArchitecture: 어댑터마다 D3D11 장치를 만들어야 해서 NVIDIA 280~480 ms · AMD 65~117 ms —
        //    시작과 NPU→GPU 되돌리기 때마다 내기엔 비싸다
        //  - D3DKMT 어댑터 종류(KMTQAITYPE_ADAPTERTYPE)의 HybridIntegrated/HybridDiscrete: 비용은 거의 0 이지만 WDDM '하이브리드 짝' 표시라
        //    그렇게 묶이지 않은 구성(데스크톱의 내장+외장 등)에선 안 켜질 수 있다(확인 안 함) → '내장인가'를 직접 묻는 DXCore 를 쓴다
        // 패키지 없이 Windows SDK 의 dxcore_interface.h 선언 순서대로 vtable 을 부른다. 메서드의 C++ bool 반환과 IsIntegrated 값은 1바이트다
        private const uint DXCoreAdapterProperty_IsIntegrated = 12;
        private static readonly Guid IID_IDXCoreAdapterFactory = new("78ee5945-c36e-4b13-a669-005dd11c0f06");
        private static readonly Guid IID_IDXCoreAdapter = new("f0db4c7f-fe5a-42a2-bd62-f2a6cf6fc83e");

        // 시스템 DLL 이지만 user32·gdi32 같은 KnownDLL 이 아니라서, exe 폴더에 같은 이름 파일이 있어도 집지 않게 System32 에서만 찾는다
        [DllImport("dxcore.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern unsafe int DXCoreCreateAdapterFactory(Guid* riid, IntPtr* ppvFactory);

        [StructLayout(LayoutKind.Sequential)]
        private struct LUID { public uint LowPart; public int HighPart; }

        // 어댑터마다 Integrated 를 채운다(LUID 로 찾음). dxcore.dll 이 없거나(Windows 10 2004 이전) 그 어댑터를 못 찾거나
        // 속성이 없으면 null 로 둔다 → 판정이 예전 VRAM 기준으로 간다.
        // 못 읽은 경우는 Debug 출력에 남긴다 — 이 PC 처럼 VRAM 기준도 같은 답을 내는 곳에선 결과로 드러나지 않아서,
        // vtable 순번·IID 를 잘못 고쳐도 조용히 VRAM 기준으로 돈다. 확인: experiments/TrackerCheck 의 'gpu'
        private static unsafe void ReadIntegrated(List<Adapter> list)
        {
            if (list.Count == 0) return;
            IntPtr factory = IntPtr.Zero;
            try
            {
                Guid iidFactory = IID_IDXCoreAdapterFactory;
                if (DXCoreCreateAdapterFactory(&iidFactory, &factory) < 0 || factory == IntPtr.Zero)
                {
                    Debug.WriteLine("DXCore 팩터리를 못 만듦 — 내장 여부 없이 VRAM 기준으로 판정");
                    return;
                }
                // IDXCoreAdapterFactory: IUnknown(0~2) 뒤 CreateAdapterList(3) · GetAdapterByLuid(4)
                var getAdapterByLuid = (delegate* unmanaged[Stdcall]<IntPtr, LUID*, Guid*, IntPtr*, int>)(*(IntPtr**)factory)[4];
                for (int i = 0; i < list.Count; i++)
                {
                    var luid = new LUID { LowPart = (uint)list[i].Luid, HighPart = (int)(list[i].Luid >> 32) };
                    Guid iidAdapter = IID_IDXCoreAdapter;
                    IntPtr adapter = IntPtr.Zero;
                    if (getAdapterByLuid(factory, &luid, &iidAdapter, &adapter) < 0 || adapter == IntPtr.Zero)
                    {
                        Debug.WriteLine($"DXCore 에서 어댑터를 못 찾음: {list[i].Name} — 내장 여부 모름");
                        continue;
                    }
                    try
                    {
                        // IDXCoreAdapter: IsValid(3) · IsAttributeSupported(4) · IsPropertySupported(5) · GetProperty(6)
                        IntPtr* vtbl = *(IntPtr**)adapter;
                        var isPropertySupported = (delegate* unmanaged[Stdcall]<IntPtr, uint, byte>)vtbl[5];
                        var getProperty = (delegate* unmanaged[Stdcall]<IntPtr, uint, nuint, void*, int>)vtbl[6];
                        byte value = 0;
                        if (isPropertySupported(adapter, DXCoreAdapterProperty_IsIntegrated) != 0
                            && getProperty(adapter, DXCoreAdapterProperty_IsIntegrated, sizeof(byte), &value) >= 0)
                            list[i] = list[i] with { Integrated = value != 0 };
                        else
                            Debug.WriteLine($"DXCore IsIntegrated 를 못 읽음: {list[i].Name} — 내장 여부 모름");
                    }
                    finally
                    {
                        Marshal.Release(adapter);
                    }
                }
            }
            catch (Exception ex)
            {
                // dxcore.dll·진입점 없음 등 — 읽은 데까지만 쓴다 (KindsKnownAndMixed 가 하나라도 null 이면 VRAM 기준으로 간다)
                Debug.WriteLine($"DXCore 내장 여부 읽기 실패 — VRAM 기준으로 판정: {ex.Message}");
            }
            finally
            {
                if (factory != IntPtr.Zero) Marshal.Release(factory);
            }
        }
    }
}
