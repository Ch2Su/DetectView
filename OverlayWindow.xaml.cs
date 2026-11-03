using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace DetectView
{
    /// <summary>
    /// 바탕화면 위에 검출 박스만 그리는 투명 HUD 창.
    /// - 클릭 통과(WS_EX_TRANSPARENT): 아래 앱 조작을 방해하지 않음
    /// - 화면 캡처에서 제외(WDA_EXCLUDEFROMCAPTURE): 그린 박스가 다시 캡처되는 피드백 방지
    /// </summary>
    public partial class OverlayWindow : Window
    {
        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr GetWindowLongPtrW(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowLongPtrW(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(
            IntPtr hWnd, IntPtr hWndAfter, int x, int y, int cx, int cy, uint flags);

        private const int GWL_EXSTYLE = -20;
        private const long WS_EX_TRANSPARENT = 0x00000020;
        private const long WS_EX_LAYERED = 0x00080000;
        private const long WS_EX_TOOLWINDOW = 0x00000080;   // Alt+Tab 목록에서 숨김
        private const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;
        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_NOACTIVATE = 0x0010;

        private readonly BoxLayer _layer = new();
        private (int X, int Y, int W, int H)? _pendingBounds;

        /// <summary>캡처 제외가 적용되었는지(피드백 차단 성공 여부)</summary>
        public bool ExcludedFromCapture { get; private set; }

        public OverlayWindow()
        {
            InitializeComponent();
            // 범례는 모니터 왼쪽 위 — 앱 안 영상 칸(8)보다 조금 더 띄운다 (화면 끝에 붙은 것처럼 보이지 않게)
            _layer.LegendMargin = 16;
            RootGrid.Children.Add(_layer);
        }

        /// <summary>범례 글자(클래스별 검출 수)가 바뀌었다 — BoxLayer.LegendTextChanged 그대로 (UI 스레드)</summary>
        public event Action<string>? LegendTextChanged
        {
            add => _layer.LegendTextChanged += value;
            remove => _layer.LegendTextChanged -= value;
        }

        /// <summary>라벨의 추적 번호 글자가 바뀌었다 — BoxLayer.TrackTextChanged 그대로 (UI 스레드)</summary>
        public event Action<string>? TrackTextChanged
        {
            add => _layer.TrackTextChanged += value;
            remove => _layer.TrackTextChanged -= value;
        }

        /// <summary>
        /// 창을 <b>물리 픽셀</b> 좌표로 정확히 배치한다.
        /// (WPF 의 Left/Top 은 DIP 라서 다중 모니터·혼합 DPI 에서 어긋날 수 있음)
        /// </summary>
        public void SetBoundsPhysical(int x, int y, int width, int height)
        {
            _pendingBounds = (x, y, width, height);
            ApplyBounds();
        }

        private void ApplyBounds()
        {
            if (_pendingBounds is not { } b) return;
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;   // 핸들 생성 후 다시 적용
            SetWindowPos(hwnd, IntPtr.Zero, b.X, b.Y, b.W, b.H, SWP_NOZORDER | SWP_NOACTIVATE);
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);

            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;

            // 클릭 통과 + 레이어드 + 작업 전환 목록에서 숨김
            long ex = GetWindowLongPtrW(hwnd, GWL_EXSTYLE).ToInt64();
            ex |= WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_TOOLWINDOW;
            SetWindowLongPtrW(hwnd, GWL_EXSTYLE, new IntPtr(ex));

            // 이 창은 화면 캡처 결과에 포함되지 않게 (Windows 10 2004+)
            ExcludedFromCapture = SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE);

            ApplyBounds();
        }

        /// <summary>
        /// 그려진 박스 수가 바뀌었다 (그리기·지우기 모두, UI 스레드). HUD 가 UIA 진단 값으로 내보내 UI 테스트가 일시정지 → 재개 뒤
        /// 박스가 돌아오는지 본다 (이슈 #11). 일시정지의 ClearBoxes 는 MainWindow.xaml.cs 에서 직접 부르므로 여기서 알린다
        /// </summary>
        public event Action<int>? BoxCountChanged;

        /// <summary>
        /// 검출 박스를 갱신한다. 좌표는 캡처 픽셀 기준이므로
        /// 오버레이(DIP) 좌표로 스케일 변환한다.
        /// </summary>
        internal void UpdateBoxes(IReadOnlyList<Det> detections, double scaleX, double scaleY)
        {
            int before = _layer.Count;
            _layer.Update(detections, scaleX, scaleY);
            if (_layer.Count != before) BoxCountChanged?.Invoke(_layer.Count);
        }

        /// <summary>모든 박스를 지운다(일시정지 등).</summary>
        public void ClearBoxes()
        {
            if (_layer.Count == 0) return;
            _layer.Clear();
            BoxCountChanged?.Invoke(0);
        }
    }
}
