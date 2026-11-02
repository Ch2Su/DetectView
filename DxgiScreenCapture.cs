using OpenCvSharp;
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Vortice;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace YOLO11_Test
{
    /// <summary>
    /// DXGI Desktop Duplication 기반 화면 캡처.
    /// - DWM 이 합성한 화면을 GPU 에서 복사 → GDI BitBlt 보다 훨씬 빠르다
    /// - 화면이 실제로 바뀐 경우에만 프레임을 준다 → 정지 화면에서는 추론을 건너뛸 수 있다
    /// - WDA_EXCLUDEFROMCAPTURE 창(HUD 오버레이)은 결과에 포함되지 않는다
    /// 주의: DuplicateOutput 은 <b>그 모니터가 연결된 어댑터</b>의 D3D11 장치로만 호출할 수 있다.
    ///       하이브리드 노트북은 화면이 내장 GPU 에 붙어 있는 경우가 많아, 추론용 GPU 와 다를 수 있다.
    /// 복제가 끊기면(접근 상실) 같은 장치·출력으로 0.25초마다 다시 복제한다. 그걸로는 못 살리는 상실(장치 제거·회전 등)은
    /// <see cref="ScreenCaptureLostException"/> 으로 알려 호출 측이 캡처를 새로 만들게 한다 (예전엔 같은 장치로 끝없이 다시 복제만 했다 — 이슈 #1)
    /// </summary>
    internal sealed class DxgiScreenCapture : IScreenCapture
    {
        private const int DXGI_ERROR_WAIT_TIMEOUT = unchecked((int)0x887A0027);
        private const int DXGI_ERROR_ACCESS_LOST = unchecked((int)0x887A0026);
        private const int DXGI_ERROR_MORE_DATA = unchecked((int)0x887A0003);

        // 같은 장치로 기다리면 다시 복제된다 → 0.25초마다 다시 복제 (끝없이 기다려도 된다). HUD 가 캡처를 새로 만들다 이게 나도 기다린다
        private const int E_ACCESSDENIED = unchecked((int)0x80070005);                       // 잠금 화면·UAC 보안 데스크톱이 떠 있는 동안
        private const int DXGI_ERROR_NOT_CURRENTLY_AVAILABLE = unchecked((int)0x887A0022);   // 화면을 복제하는 다른 앱이 많음 등
        private const int DXGI_ERROR_MODE_CHANGE_IN_PROGRESS = unchecked((int)0x887A0025);   // 해상도·모드 바꾸는 중
        private const int DXGI_ERROR_SESSION_DISCONNECTED = unchecked((int)0x887A0028);      // 원격 세션 끊김
        // 화면 모드·데스크톱 전환 중에도 잠깐 난다 — DuplicateOutput 문서와 DesktopDuplication 예제가 '예상되는 오류'로 다루고 전환 뒤
        // 다시 복제하라고 한다. 출력이 다른 어댑터 아래로 옮겨갔으면(하이브리드 GPU) 계속 난다 → 모르는 실패처럼 FailureGraceMs 동안은 기다린다
        private const int DXGI_ERROR_UNSUPPORTED = unchecked((int)0x887A0004);
        // 장치가 제거됐다 — 같은 장치로는 못 살린다 → 곧바로 던져서 새로 만들게 한다
        private const int DXGI_ERROR_DEVICE_REMOVED = unchecked((int)0x887A0005);   // TDR·드라이버 재시작·업데이트
        private const int DXGI_ERROR_DEVICE_HUNG = unchecked((int)0x887A0006);
        private const int DXGI_ERROR_DEVICE_RESET = unchecked((int)0x887A0007);
        private const int DXGI_ERROR_DRIVER_INTERNAL_ERROR = unchecked((int)0x887A0020);
        private const int D3DDDIERR_DEVICEREMOVED = unchecked((int)0x88760870);

        /// <summary>
        /// UNSUPPORTED·모르는 실패를 일시적인 것으로 보고 기다리는 시간 (ms). 프레임 없이 이만큼 넘게 이어지면 같은 장치로는 못 살리는 것으로
        /// 보고 던진다. HUD 가 DXGI 를 다시 만들 때도 같은 시간만큼 기다리다 GDI 로 넘어간다
        /// </summary>
        internal const int FailureGraceMs = 3000;

        private RawRect[] _dirtyRects = new RawRect[32];
        private OutduplMoveRect[] _moveRects = new OutduplMoveRect[8];

        private readonly IDXGIAdapter1 _adapter;
        private readonly IDXGIOutput1 _output;
        private readonly ID3D11Device _device;
        private readonly ID3D11DeviceContext _context;
        private IDXGIOutputDuplication? _duplication;
        private ID3D11Texture2D? _staging;        // CPU 읽기용 복사본
        private MappedSubresource _map;
        private bool _mapped;                     // _staging 이 CPU 에 매핑돼 있음 = 마지막 화면 보유
        private long _lastRecreateTicks;
        private long _unknownSinceTicks;          // UNSUPPORTED·모르는 실패가 처음 난 시각 (0 = 없음). 복제가 다시 살아 있으면 지운다
        private string? _lostReason;              // 복제가 끊긴 이유 (Interruption 으로 내보낸다)
        private bool _disposed;

        public int Width { get; private set; }
        public int Height { get; private set; }
        public string Method => "DXGI";
        public long FrameTimestamp { get; private set; }
        public PixelRect? IgnoreRegion { get; set; }
        public string? Interruption => _duplication == null ? _lostReason ?? "화면 복제 끊김" : null;
        public bool Redelivered { get; private set; }

        /// <summary>캡처에 쓰는 어댑터 이름 (디스플레이가 연결된 GPU)</summary>
        public string AdapterName { get; }

        /// <param name="deviceName">모니터 장치 이름 (예: \\.\DISPLAY1)</param>
        public DxgiScreenCapture(string deviceName)
        {
            // 1) 해당 모니터를 소유한 어댑터·출력 찾기
            IDXGIAdapter1? foundAdapter = null;
            IDXGIOutput1? foundOutput = null;
            ModeRotation rotation = ModeRotation.Unspecified;
            DXGI.CreateDXGIFactory1(out IDXGIFactory1? factory).CheckError();
            using (factory)
            {
                for (uint a = 0; foundOutput == null && factory!.EnumAdapters1(a, out IDXGIAdapter1 adapter).Success; a++)
                {
                    for (uint o = 0; foundOutput == null && adapter.EnumOutputs(o, out IDXGIOutput output).Success; o++)
                    {
                        var desc = output.Description;
                        if (string.Equals(desc.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase))
                        {
                            foundOutput = output.QueryInterface<IDXGIOutput1>();
                            rotation = desc.Rotation;
                        }
                        output.Dispose();
                    }
                    if (foundOutput != null) foundAdapter = adapter;
                    else adapter.Dispose();
                }
            }
            if (foundOutput == null || foundAdapter == null)
                throw new NotSupportedException($"DXGI 출력을 찾을 수 없습니다: {deviceName}");
            // 회전은 장치를 만들기 전에 거른다 — D3D11CreateDevice 가 한 번에 0.2초쯤 걸려서(10-31 실측, RTX 5060), 회전된 채 GDI 로 돌며
            // 몇 초마다 DXGI 로 돌아가 보는 HUD 루프가 그때마다 멈칫하지 않게. 복제 설명의 회전도 CreateDuplication 에서 다시 본다
            if (rotation is not (ModeRotation.Identity or ModeRotation.Unspecified))
            {
                foundOutput.Dispose();
                foundAdapter.Dispose();
                throw new DisplayRotatedException(rotation);
            }

            _adapter = foundAdapter;
            _output = foundOutput;
            AdapterName = _adapter.Description1.Description;

            try
            {
                // 2) 그 어댑터에 D3D11 장치 생성 (어댑터를 지정할 때 DriverType 은 Unknown 이어야 함)
                D3D11.D3D11CreateDevice(_adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport,
                    [FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0],
                    out ID3D11Device device, out ID3D11DeviceContext context).CheckError();
                _device = device;
                _context = context;

                // 3) 복제 시작 — 실패하면 예외 → 호출 측에서 GDI 로 대체
                CreateDuplication();
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        private void CreateDuplication()
        {
            _duplication?.Dispose();
            _duplication = null;

            var dup = _output.DuplicateOutput(_device);
            var desc = dup.Description;
            // 회전된 디스플레이는 이미지가 회전 전 방향으로 오므로 지원하지 않음 (GDI 로 대체)
            if (desc.Rotation is not (ModeRotation.Identity or ModeRotation.Unspecified))
            {
                dup.Dispose();
                throw new DisplayRotatedException(desc.Rotation);
            }

            int w = (int)desc.ModeDescription.Width, h = (int)desc.ModeDescription.Height;
            if (_staging == null || w != Width || h != Height)
            {
                Unmap();
                _staging?.Dispose();
                var td = new Texture2DDescription
                {
                    Width = (uint)w,
                    Height = (uint)h,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = Format.B8G8R8A8_UNorm,   // DuplicateOutput 결과는 항상 BGRA8
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Staging,
                    CPUAccessFlags = CpuAccessFlags.Read,
                    BindFlags = BindFlags.None,
                    MiscFlags = ResourceOptionFlags.None,
                };
                _staging = _device.CreateTexture2D(in td);
                Width = w;
                Height = h;
            }
            _duplication = dup;
        }

        public Mat? TryCapture(int timeoutMs, bool forceLatest = false)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Redelivered = false;
            TestFaults.DxgiTryCapture(this);   // 시험용 복제 상실 (Debug 빌드 + DETECTVIEW_TEST_FAULTS=hud-capture-lost 일 때만)

            // 강제(재개·검출 재시도)면 새 화면을 기다리지 않고 마지막 화면을 곧바로 다시 준다. 예전엔 먼저 timeoutMs 동안 새 화면을 기다려,
            // 재개 버튼 글자처럼 그사이 바뀐 화면이 오면 그걸 돌려줘서 강제 경로가 깨져도 드러나지 않았다 (이슈 #11).
            // 기다리던 새 화면은 버리지 않는다 — 복제에 쌓여 있다가 다음 호출에서 온다. 마지막 화면이 없으면 아래 보통 경로로
            if (forceLatest && Latest() is { } again)
            {
                Redelivered = true;
                return again;
            }

            if (_duplication == null)
            {
                // 접근 상실(UAC 보안 데스크톱·잠금 화면·해상도 변경 등) 후 재생성. 너무 자주 시도하지 않는다
                if (Stopwatch.GetElapsedTime(_lastRecreateTicks).TotalMilliseconds < 250)
                {
                    Thread.Sleep(Math.Clamp(timeoutMs, 0, 50));
                    return null;
                }
                _lastRecreateTicks = Stopwatch.GetTimestamp();
                try { CreateDuplication(); }
                catch (Exception ex)
                {
                    Debug.WriteLine($"DXGI 복제 재생성 실패: {ex.Message}");
                    OnFailure(ex.HResult, "다시 복제", ex);   // 같은 장치로 못 살리면(장치 제거·회전 등) 여기서 던진다
                    return null;
                }
            }

            var result = _duplication!.AcquireNextFrame((uint)Math.Max(0, timeoutMs),
                out OutduplFrameInfo info, out IDXGIResource resource);

            if (result.Code == DXGI_ERROR_WAIT_TIMEOUT)
            {
                _unknownSinceTicks = 0;   // 복제는 살아 있다
                return null;              // 화면 변화 없음
            }

            if (result.Failure)
            {
                // ACCESS_LOST 등: 복제를 버리고 다음 호출에서 재생성. 장치 제거 등 그걸로 못 살리는 실패면 OnFailure 가 던진다
                resource?.Dispose();
                if (result.Code != DXGI_ERROR_ACCESS_LOST)
                    Debug.WriteLine($"AcquireNextFrame 실패: 0x{result.Code:X8}");
                _duplication.Dispose();
                _duplication = null;
                OnFailure(result.Code, "AcquireNextFrame");
                return null;
            }
            _unknownSinceTicks = 0;

            bool newImage = false;
            SharpGen.Runtime.Result released;
            try
            {
                // LastPresentTime == 0 → 마우스 포인터만 움직임 (화면 내용은 그대로)
                // 바뀐 곳이 무시 영역(앱 자신의 상태바) 안뿐이면 역시 새 화면으로 치지 않는다
                if (info.LastPresentTime != 0 && !ChangedOnlyInIgnoreRegion(info))
                {
                    FrameTimestamp = Stopwatch.GetTimestamp();
                    Unmap();   // 이전 화면의 CPU 매핑을 풀고 새 화면을 복사
                    using var texture = resource.QueryInterface<ID3D11Texture2D>();
                    _context.CopyResource(_staging!, texture);
                    newImage = true;
                }
            }
            finally
            {
                resource.Dispose();
                released = _duplication.ReleaseFrame();   // 오래 쥐고 있으면 DWM 의 화면 갱신을 막으므로 즉시 반납
            }

            if (released.Failure)
            {
                // 반납이 실패한 복제는 못 쓴다(보통 접근 상실) → 버리고 다음 호출에서 다시 복제. 이미 복사한 화면은 그대로 돌려준다
                Debug.WriteLine($"ReleaseFrame 실패: 0x{released.Code:X8}");
                _duplication.Dispose();
                _duplication = null;
                OnFailure(released.Code, "ReleaseFrame");
            }

            if (!newImage) return null;

            // ReleaseFrame 뒤에 매핑 (Map 은 GPU 복사 완료까지 대기). CopyResource 는 반환값이 없어 장치 제거가 여기서야 드러난다
            var mapped = _context.Map(_staging!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None, out _map);
            if (mapped.Failure)
            {
                OnFailure(mapped.Code, "Map");   // 장치 제거면 던진다
                mapped.CheckError();             // 그 밖의 실패는 일반 예외로 (HUD 루프가 이어지면 FPS 칸에 띄운다)
            }
            _mapped = true;
            TestFaults.DxgiFrame(this);
            return Wrap();
        }

        // 실패를 같은 장치·출력으로 다시 복제해 살릴 수 있는지 가른다. 못 살리면 ScreenCaptureLostException — 호출 측(HUD 루프)이 새로 만든다.
        // - 장치 제거·회전: 다시 복제해도 매번 같다 → 곧바로 던진다
        // - 잠금 화면·UAC 보안 데스크톱처럼 기다리면 되는 접근 상실(E_ACCESSDENIED 등): 던지지 않고 0.25초마다 같은 장치로 다시 복제한다.
        //   장치째 새로 만들어도 그동안은 DXGI 를 못 열어 장치 생성(0.2초)만 버리고, 같은 장치로 다시 복제하면 풀리자마자 이어진다
        // - UNSUPPORTED·모르는 실패: 화면 모드·데스크톱 전환 중에도 잠깐 나므로 프레임 없이 FailureGraceMs 넘게 이어질 때만 던진다
        private void OnFailure(int hr, string where, Exception? ex = null)
        {
            // 제거된 장치는 다시 쓸 수 없다(D3D 규약 — 새로 만들어야 함). 실패 코드가 ACCESS_LOST 여도 장치가 제거됐을 수 있어 직접 묻는다
            int removed = _device.DeviceRemovedReason.Code;
            if (removed < 0 || IsDeviceLost(hr))
            {
                _lostReason = $"캡처 GPU 장치가 제거됨 (0x{(removed < 0 ? removed : hr):X8} — GPU 드라이버 재시작·업데이트 등)";
                throw new ScreenCaptureLostException(_lostReason, ex);
            }

            if (ex is DisplayRotatedException)
            {
                _lostReason = ex.Message;
                throw new ScreenCaptureLostException(_lostReason, ex);
            }
            _lostReason = Describe(hr, where);
            if (IsWaitable(hr))
                return;   // 기다리면 된다 — 0.25초마다 다시 복제

            if (_unknownSinceTicks == 0) _unknownSinceTicks = Stopwatch.GetTimestamp();
            else if (Stopwatch.GetElapsedTime(_unknownSinceTicks).TotalMilliseconds > FailureGraceMs)
                throw new ScreenCaptureLostException($"{_lostReason} — {FailureGraceMs / 1000}초 넘게 이어짐", ex);
        }

        private static string Describe(int hr, string where) => hr switch
        {
            DXGI_ERROR_ACCESS_LOST => "화면 복제 끊김 (DXGI_ERROR_ACCESS_LOST — 화면 전환·해상도 변경·전체 화면 앱 등)",
            E_ACCESSDENIED => "화면에 접근할 수 없음 (E_ACCESSDENIED — 잠금 화면·UAC 보안 데스크톱 등)",
            DXGI_ERROR_NOT_CURRENTLY_AVAILABLE => "지금은 화면 복제를 쓸 수 없음 (DXGI_ERROR_NOT_CURRENTLY_AVAILABLE)",
            DXGI_ERROR_MODE_CHANGE_IN_PROGRESS => "화면 모드를 바꾸는 중 (DXGI_ERROR_MODE_CHANGE_IN_PROGRESS)",
            DXGI_ERROR_SESSION_DISCONNECTED => "세션 연결 끊김 (DXGI_ERROR_SESSION_DISCONNECTED — 원격 데스크톱 등)",
            DXGI_ERROR_UNSUPPORTED => "이 출력은 지금 복제할 수 없음 (DXGI_ERROR_UNSUPPORTED — 화면 모드 전환 중·출력이 다른 GPU 아래로 옮겨감 등)",
            _ => $"화면 복제 실패 ({where}: 0x{hr:X8})",
        };

        private static bool IsDeviceLost(int hr) =>
            hr is DXGI_ERROR_DEVICE_REMOVED or DXGI_ERROR_DEVICE_HUNG or DXGI_ERROR_DEVICE_RESET
                or DXGI_ERROR_DRIVER_INTERNAL_ERROR or D3DDDIERR_DEVICEREMOVED;

        /// <summary>같은 장치로 기다리면 다시 복제되는 실패 (잠금 화면·UAC 보안 데스크톱·모드 변경 중·원격 세션 끊김·다른 복제 앱이 많음)</summary>
        internal static bool IsWaitable(int hr) =>
            hr is DXGI_ERROR_ACCESS_LOST or E_ACCESSDENIED or DXGI_ERROR_NOT_CURRENTLY_AVAILABLE
                or DXGI_ERROR_MODE_CHANGE_IN_PROGRESS or DXGI_ERROR_SESSION_DISCONNECTED;

        /// <summary>
        /// DXGI 캡처를 (다시) 만들다 난 실패를 HUD 가 어떻게 다룰지 가른다 (생성자 예외 — 출력 찾기·장치 생성·DuplicateOutput).
        /// 잠긴 동안·모드 전환 중에 GDI 로 넘어가면 풀린 뒤에도 GDI→DXGI 재시도를 기다려야 해서(장치까지 만든 시도마다 간격이 두 배, 30초까지)
        /// 기다리면 될 실패는 캡처 없이 기다렸다가 DXGI 로 다시 연다
        /// </summary>
        internal static CreateFailureKind ClassifyCreateFailure(Exception ex) =>
            ex is DisplayRotatedException ? CreateFailureKind.Fallback
            : IsWaitable(ex.HResult) ? CreateFailureKind.Wait
            : CreateFailureKind.Grace;

        // 이번 프레임의 변경 영역(이동·갱신 사각형)이 모두 IgnoreRegion 안에 있으면 true.
        // 정보를 못 얻으면 false (= 새 화면으로 처리) 로 안전하게 넘어간다. ReleaseFrame 전에 호출해야 한다
        private bool ChangedOnlyInIgnoreRegion(in OutduplFrameInfo info)
        {
            if (IgnoreRegion is not { } ig || info.TotalMetadataBufferSize == 0) return false;

            int moveSize = Marshal.SizeOf<OutduplMoveRect>();
            var r = _duplication!.GetFrameMoveRects((uint)(_moveRects.Length * moveSize), _moveRects, out uint need);
            if (r.Code == DXGI_ERROR_MORE_DATA)
            {
                _moveRects = new OutduplMoveRect[need / moveSize + 8];
                r = _duplication.GetFrameMoveRects((uint)(_moveRects.Length * moveSize), _moveRects, out need);
            }
            if (r.Failure) return false;
            for (int i = 0; i < need / moveSize; i++)
            {
                var d = _moveRects[i].DestinationRect;
                int sx = _moveRects[i].SourcePoint.X, sy = _moveRects[i].SourcePoint.Y;
                if (!ig.Contains(d.Left, d.Top, d.Right, d.Bottom)
                    || !ig.Contains(sx, sy, sx + (d.Right - d.Left), sy + (d.Bottom - d.Top)))
                    return false;
            }

            int rectSize = Marshal.SizeOf<RawRect>();
            r = _duplication.GetFrameDirtyRects((uint)(_dirtyRects.Length * rectSize), _dirtyRects, out need);
            if (r.Code == DXGI_ERROR_MORE_DATA)
            {
                _dirtyRects = new RawRect[need / rectSize + 16];
                r = _duplication.GetFrameDirtyRects((uint)(_dirtyRects.Length * rectSize), _dirtyRects, out need);
            }
            if (r.Failure) return false;
            for (int i = 0; i < need / rectSize; i++)
            {
                var d = _dirtyRects[i];
                if (!ig.Contains(d.Left, d.Top, d.Right, d.Bottom)) return false;
            }
            return true;
        }

        // 매핑해 둔 마지막 화면 (없으면 null — 갓 만든 복제의 첫 프레임은 검은 화면이라 건너뛰므로 화면이 바뀌기 전엔 없다)
        private Mat? Latest()
        {
            if (!_mapped) return null;
            FrameTimestamp = Stopwatch.GetTimestamp();
            return Wrap();
        }

        // 매핑된 GPU 복사본을 복제 없이 Mat 으로 감싼다 (행 간격 RowPitch 는 Width*4 보다 클 수 있음)
        private Mat Wrap() =>
            Mat.FromPixelData(Height, Width, MatType.CV_8UC4, _map.DataPointer, (long)_map.RowPitch);

        private void Unmap()
        {
            if (!_mapped || _staging == null) return;
            _context.Unmap(_staging, 0);
            _mapped = false;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_context != null) Unmap();
            _staging?.Dispose();
            _duplication?.Dispose();
            _context?.Dispose();
            _device?.Dispose();
            _output?.Dispose();
            _adapter?.Dispose();
        }
    }

    /// <summary>
    /// 회전된 디스플레이 — DXGI 복제는 이미지를 회전 전 방향으로 줘서 지원하지 않는다 (GDI 로 대체).
    /// 다시 복제·다시 만들어도 회전을 되돌리기 전엔 매번 같아서, HUD 는 기다리지 않고 곧바로 GDI 로 넘어간다 (출력을 못 찾음 등 다른 NotSupportedException 과 가른다)
    /// </summary>
    internal sealed class DisplayRotatedException(ModeRotation rotation)
        : NotSupportedException($"회전된 디스플레이({rotation})는 DXGI 캡처를 지원하지 않습니다.");

    /// <summary>DXGI 캡처를 (다시) 만들다 난 실패의 종류 — <see cref="DxgiScreenCapture.ClassifyCreateFailure"/></summary>
    internal enum CreateFailureKind
    {
        /// <summary>기다리면 된다 (잠금 화면·UAC 보안 데스크톱·모드 변경 중·원격 세션 끊김 등) — 캡처 없이 기다렸다가 DXGI 로 다시 연다</summary>
        Wait,
        /// <summary>잠깐일 수도 있다 (UNSUPPORTED·장치를 못 만듦·출력을 못 찾음·모르는 실패) — <see cref="DxgiScreenCapture.FailureGraceMs"/> 동안은 기다리고, 넘으면 GDI</summary>
        Grace,
        /// <summary>기다려도 안 된다 (회전) — 곧바로 GDI</summary>
        Fallback,
    }
}
