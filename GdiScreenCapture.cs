using OpenCvSharp;
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DetectView
{
    /// <summary>
    /// GDI 기반 화면 캡처 (DXGI 를 쓸 수 없을 때의 대체 경로).
    /// DIB 섹션에 BitBlt 한 뒤 그 메모리를 Mat 으로 감싸므로 프레임마다 추가 복사가 없다.
    /// 화면 변화 여부를 알 수 없어 매번 새로 캡처한다(1080p 기준 14~21ms).
    /// GDI 핸들(GetDC/ReleaseDC)은 스레드에 묶이므로 <b>같은 스레드에서 생성·해제</b>해야 한다.
    /// </summary>
    internal sealed class GdiScreenCapture : IScreenCapture
    {
        [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr h);
        [DllImport("gdi32.dll", SetLastError = true)] private static extern bool BitBlt(   // 실패 사유를 Interruption 에 남긴다
            IntPtr hdcDest, int x, int y, int w, int h, IntPtr hdcSrc, int x1, int y1, uint rop);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(
            IntPtr hdc, ref BITMAPINFO bmi, uint usage, out IntPtr bits, IntPtr hSection, uint offset);

        private const uint SRCCOPY = 0x00CC0020;
        private const uint CAPTUREBLT = 0x40000000;   // 레이어드 윈도우 포함

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFOHEADER
        {
            public uint biSize;
            public int biWidth, biHeight;
            public ushort biPlanes, biBitCount;
            public uint biCompression, biSizeImage;
            public int biXPelsPerMeter, biYPelsPerMeter;
            public uint biClrUsed, biClrImportant;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFO
        {
            public BITMAPINFOHEADER bmiHeader;
            public uint bmiColors;
        }

        private IntPtr _screenDc, _memDc, _hBitmap, _bits;
        private IntPtr _oldBitmap;   // _memDc 에 원래 선택돼 있던 비트맵 — 해제 전에 되돌려 놓는다
        private readonly int _srcX, _srcY;
        private bool _disposed;

        public int Width { get; }
        public int Height { get; }
        public string Method => FallbackReason == null ? "GDI" : $"GDI ({FallbackReason})";

        /// <summary>DXGI 대신 이 경로를 쓰게 된 이유 (상태바 표시용). 직접 GDI 를 고른 경우 null</summary>
        public string? FallbackReason { get; init; }

        /// <summary>자세한 해결 방법 (상태바 툴팁용). 안내할 방법이 없으면 null</summary>
        public string? FallbackHelp { get; init; }
        public long FrameTimestamp { get; private set; }
        public PixelRect? IgnoreRegion { get; set; }   // GDI 는 변경 영역을 알 수 없어 사용하지 않음

        /// <summary>마지막 캡처가 화면을 못 읽었으면 그 이유 (다음에 읽으면 null)</summary>
        public string? Interruption { get; private set; }

        /// <summary>늘 false — GDI 는 forceLatest 와 관계없이 매번 새로 캡처한다 (마지막 화면을 다시 주지 않음)</summary>
        public bool Redelivered => false;

        /// <summary>가상 화면 좌표계의 지정 영역(보통 모니터 1대)을 캡처한다.</summary>
        public GdiScreenCapture(int srcX, int srcY, int width, int height)
        {
            _srcX = srcX;
            _srcY = srcY;
            Width = Math.Max(1, width);
            Height = Math.Max(1, height);

            _screenDc = GetDC(IntPtr.Zero);
            _memDc = CreateCompatibleDC(_screenDc);

            var bmi = new BITMAPINFO();
            bmi.bmiHeader.biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>();
            bmi.bmiHeader.biWidth = Width;
            bmi.bmiHeader.biHeight = -Height;   // 음수 = top-down (Mat 과 같은 행 순서)
            bmi.bmiHeader.biPlanes = 1;
            bmi.bmiHeader.biBitCount = 32;      // BGRA
            bmi.bmiHeader.biCompression = 0;    // BI_RGB

            _hBitmap = CreateDIBSection(_memDc, ref bmi, 0, out _bits, IntPtr.Zero, 0);
            if (_hBitmap == IntPtr.Zero || _bits == IntPtr.Zero)
            {
                Dispose();
                throw new InvalidOperationException("화면 캡처 버퍼(DIB) 생성에 실패했습니다.");
            }

            _oldBitmap = SelectObject(_memDc, _hBitmap);
        }

        /// <summary>항상 새로 캡처한다 (GDI 는 화면 변화 여부를 알 수 없음). 화면을 못 읽으면 timeoutMs 만큼 쉬고 null.</summary>
        public Mat? TryCapture(int timeoutMs, bool forceLatest = false)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            FrameTimestamp = Stopwatch.GetTimestamp();
            // 잠금 화면(Win+L)·UAC 보안 데스크톱 등에선 실패한다 — 그때 옛 버퍼를 새 화면처럼 넘기면 같은 화면을 계속 추론한다.
            // 실패하면 DXGI 의 '기다렸지만 새 화면 없음'처럼 timeoutMs 만큼 쉬고 null — 곧바로 돌려주면 HUD 루프가
            // 대기 없이 다시 불러 잠금 화면 내내 코어 하나를 헛돌렸다 (2026-10-30 코드 검토로 발견)
            if (!BitBlt(_memDc, 0, 0, Width, Height, _screenDc, _srcX, _srcY, SRCCOPY | CAPTUREBLT))
            {
                // '화면 변화 없음'이 아니라 끊김이다 — HUD 가 1초 넘게 이어지면 FPS 칸에 띄운다 (GDI 는 원래 매번 새 화면이라 null 이 곧 끊김)
                Interruption = $"화면을 읽지 못함 (BitBlt 실패, 오류 {Marshal.GetLastPInvokeError()} — 잠금 화면·UAC 보안 데스크톱 등)";
                if (timeoutMs > 0) System.Threading.Thread.Sleep(timeoutMs);
                return null;
            }
            Interruption = null;
            return Mat.FromPixelData(Height, Width, MatType.CV_8UC4, _bits, Width * 4);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            // 문서상 DC 에 선택된 비트맵은 DeleteObject 가 실패한다 → 선택을 되돌린 뒤 지운다.
            // (이 PC Windows 11 에선 선택된 채 지워도 실패·누수가 없었다 — CreateCompatibleBitmap 50회로 확인. 그래도 규약대로)
            if (_memDc != IntPtr.Zero && _oldBitmap != IntPtr.Zero) { SelectObject(_memDc, _oldBitmap); _oldBitmap = IntPtr.Zero; }
            if (_hBitmap != IntPtr.Zero) { DeleteObject(_hBitmap); _hBitmap = IntPtr.Zero; }
            if (_memDc != IntPtr.Zero) { DeleteDC(_memDc); _memDc = IntPtr.Zero; }
            if (_screenDc != IntPtr.Zero) { ReleaseDC(IntPtr.Zero, _screenDc); _screenDc = IntPtr.Zero; }
        }
    }
}
