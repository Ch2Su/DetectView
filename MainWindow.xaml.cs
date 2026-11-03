using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;

namespace YOLO11_Test
{
    /// <summary>
    /// 메인 창 — 공용 상태와 모드 공통 UI(정지·재생/일시정지·슬라이더·모드별 표시).
    /// 기능별 코드는 partial 파일에 있다:
    ///   MainWindow.File.cs      파일 열기 · VFR 메타데이터 분석
    ///   MainWindow.Camera.cs    카메라 열거 · 시작
    ///   MainWindow.Screen.cs    화면 HUD (캡처 루프 · 오버레이)
    ///   MainWindow.Playback.cs  파일/카메라 재생 루프 · 시크
    ///   MainWindow.Inference.cs 모델 로드 · GPU 선택 · 검출
    ///   MainWindow.Npu.cs       추론 장치(GPU ↔ NPU) 바꿔 끼우기 · 툴바 "추론" 선택
    ///   MainWindow.Sahi.cs      SAHI 정밀 분석 (알고리즘은 Sahi.cs)
    /// </summary>
    public partial class MainWindow : Window
    {
        private enum SourceMode { None, File, Camera, Screen }

        // 재생 루프(파일/카메라/화면 공통) 제어
        private Task? _playbackTask;
        private CancellationTokenSource? _cts;
        private readonly ManualResetEventSlim _playSignal = new(false);

        private SourceMode _mode = SourceMode.None;
        private double _smoothedFps;
        private int _loadGeneration;  // 소스 교체 시 증가 → 백그라운드 분석 결과 무효화에 사용
        private bool _isPlaying;

        public MainWindow()
        {
            InitializeComponent();
            InitializeYoloModel();
            InitHudTracking();
            InitRoi();
            FrameBoxes.LegendTextChanged += ShowClassCounts;   // HUD 오버레이의 범례는 StartHud 에서 잇는다
            FrameBoxes.TrackTextChanged += ShowTrackMarks;
            Loaded += (_, _) => { RefreshCameras(); RefreshMonitors(); };

            // 추론 장치: 창이 뜬 뒤 NPU 를 쓸 수 있는지 확인해 툴바 "추론" 항목을 연다. --npu 면 확인 뒤 바로 NPU 로 (테스트·바로가기용)
            bool startOnNpu = Environment.GetCommandLineArgs().Skip(1).Contains("--npu", StringComparer.OrdinalIgnoreCase);
            Loaded += async (_, _) => await InitInferenceDeviceAsync(startOnNpu);
        }

        private void Btn_RefreshCameras_Click(object sender, RoutedEventArgs e)
        {
            RefreshCameras();
            RefreshMonitors();
        }

        // ---------- 모드 전환 공통 ----------
        // 전환(파일·카메라·HUD 시작, 정지)은 이전 루프가 끝나길 await 한다. 그 사이 다른 모드 버튼이 눌려 두 전환이 겹치면,
        // 늦게 끝난 쪽의 StopPlaybackAsync 후반이 먼저 끝난 쪽이 새로 만든 _cts·_playbackTask 를 해제·null 로 덮어
        // 멈출 수 없는 루프가 남는다 → 전환 중 들어온 모드 클릭은 무시한다 (버튼 상태는 그대로 ApplyModeToUi 한 곳에서). UI 스레드 전용.
        // 창 닫기(OnClosed)는 막지 않는다
        private bool _switchingMode;

        // 지금 소스를 모두 내린다: 재생·HUD 루프 정지, 캡처 해제, HUD 오버레이 닫기.
        // 예전엔 HUD 에서 [정지] 없이 파일·카메라로 바꾸면 오버레이를 안 닫아 마지막 박스가 바탕화면에 남았다
        private async Task TearDownSourceAsync()
        {
            await StopPlaybackAsync();
            DisposeCapture();
            CloseOverlay();
            ClearRoi();   // 구역은 열려 있던 영상의 것
            ShowSceneBadge(SceneBadgeKind.None);
        }

        // 소스 없음 상태로. 정지뿐 아니라 새 소스를 못 열었을 때도 쓴다 — 예전엔 이전 루프를 이미 내린 뒤 열기에 실패하면
        // _mode·버튼이 옛 모드로 남아, 재생을 눌러도 아무 일이 없거나 HUD 처럼 보였다
        private void ResetToIdle(string status)
        {
            _mode = SourceMode.None;
            _durationMs = 0;
            _smoothedFps = 0;
            _loadGeneration++;
            MainImage.Source = null;
            FrameBoxes.Clear();
            ClearRoi();   // 열기에 실패한 경우(구역을 읽은 뒤 실패)에도 구역이 남지 않게
            ShowSceneBadge(SceneBadgeKind.None);
            PlaceholderText.Text = "파일을 열거나 카메라를 시작하세요";

            ApplyModeToUi();
            Txt_Status.Text = status;
            Txt_Resolution.Text = "";
            Txt_InferenceFps.Text = "";
            Txt_DetectionCount.Text = "";
        }

        // 지금 그려진 범례(클래스별 검출 수 "CAR 7 · TRUCK 1")를 검출 수 칸의 UIA ItemStatus 로 내보낸다 — 범례는 BoxLayer 가 그린
        // 그림이라 UIA 로 안 보인다. 앱 안 영상(FrameBoxes)과 HUD 오버레이 중 지금 그리는 쪽이 알린다. UI 테스트가 '검출 N'(HUD 는
        // 진단 값의 오버레이 박스 수)과 합을 맞춰 본다. 그리지 않는 속성이라 HUD 캡처에 새 화면을 만들지 않는다
        private void ShowClassCounts(string legendText) => AutomationProperties.SetItemStatus(Txt_DetectionCount, legendText);

        // 라벨에 붙은 추적 번호("#12 정지 · #15")도 같은 식으로 UIA ItemType 에 내보낸다 — 번호는 라벨 글자라 UIA 로 안 보인다.
        // UI 테스트가 박스마다 번호가 붙었는지·프레임이 넘어가도 이어지는지·멈춘 물체가 '정지'로 나오는지 본다
        private void ShowTrackMarks(string trackText) => AutomationProperties.SetItemType(Txt_DetectionCount, trackText);

        // ---------- 정지 ----------
        private async void Btn_Stop_Click(object sender, RoutedEventArgs e)
        {
            if (_switchingMode) return;
            _switchingMode = true;
            try
            {
                await TearDownSourceAsync();
                ResetToIdle("정지됨");
            }
            finally { _switchingMode = false; }
        }

        // ---------- 재생/일시정지 버튼 ----------
        private void Btn_PlayPause_Click(object sender, RoutedEventArgs e)
        {
            // 화면 HUD 모드는 VideoCapture 가 없으므로 별도로 허용
            if (_capture == null && _mode != SourceMode.Screen) return;

            if (_isPlaying)
            {
                _playSignal.Reset();
                _isPlaying = false;
                // 재생 중 시크를 루프가 아직 꺼내지 않았으면 루프는 옛 위치의 프레임을 들고 멈춘다 — 슬라이더만 새 위치고 화면은 옛 프레임.
                // 멈춘 채 시크와 같이 그 위치 한 장을 보이게 한다 (파일이 아니면 RequestStepFrame 이 거른다)
                if (SeekPending) RequestStepFrame();
                Btn_PlayPause.Content = "재생";
                _overlay?.ClearBoxes();   // 멈춘 박스가 화면에 남지 않도록
            }
            else
            {
                // HUD: 정지 화면이면 새 프레임이 오지 않으므로, 마지막 화면으로 박스를 다시 그리게 한다
                if (_mode == SourceMode.Screen) Interlocked.Exchange(ref _hudForceRefresh, 1);
                // 끝까지 재생한 파일: 그대로 두면 EOF 를 한 번 더 읽고 곧바로 다시 멈춘다 → 처음부터
                if (_mode == SourceMode.File && _atEof)
                {
                    SeekToMs(0);
                    Txt_Status.Text = "처음부터 다시 재생";   // "재생 완료"가 남아 있지 않도록
                }
                _playSignal.Set();
                _isPlaying = true;
                Btn_PlayPause.Content = "일시정지";
            }
            UpdateRoiUi();   // 구역 편집은 멈춘 파일에서만
        }

        // ---------- 모드에 따른 UI 상태 ----------
        private void ApplyModeToUi()
        {
            bool file = _mode == SourceMode.File;
            bool camera = _mode == SourceMode.Camera;
            bool screen = _mode == SourceMode.Screen;
            bool active = file || camera || screen;

            // 화면 HUD 는 앱 안에 영상을 그리지 않으므로 안내문을 계속 보여준다
            PlaceholderText.Visibility = (active && !screen) ? Visibility.Collapsed : Visibility.Visible;
            if (!screen) Txt_Status.ToolTip = null;   // HUD 캡처 안내 툴팁은 HUD 모드에서만

            // 지금 소스 버튼을 켜 두고, 상태바 점은 소스가 있으면 초록 (색은 App.xaml)
            Ui.SetIsActive(Btn_LoadVideo, file);
            Ui.SetIsActive(Btn_StartCamera, camera);
            Ui.SetIsActive(Btn_Screen, screen);
            StatusDot.SetResourceReference(TextBlock.ForegroundProperty, active ? "OkBrush" : "MutedTextBrush");
            // 상태바 FPS 칸 폭: HUD 는 그 자리에 '대기 (화면 변화 없음)'·'캡처 끊김 …' 문구가 숫자와 번갈아 떠서 문구 폭, 파일·카메라는 숫자 폭으로 고정한다 —
            // 값이 바뀌어도 해상도·NPU 진행 글자가 안 밀리게. 숫자 폭으로만 두면 HUD 에서 대기 문구가 뜰 때마다 밀렸고, 문구 폭으로만 두면 평소 FPS 앞이 비었다
            Txt_InferenceFps.SetResourceReference(MinWidthProperty, screen ? "StatusFpsHudWidth" : "StatusFpsWidth");

            // SAHI 분석 중(_sahiBusy)엔 재생·SAHI 버튼과 슬라이더를 끈다 — 분석이 끝나면 SAHI 가 이 함수로 되돌린다
            Btn_PlayPause.IsEnabled = active && !_sahiBusy;
            Btn_Stop.IsEnabled = active;
            Btn_Sahi.IsEnabled = (file || camera) && !_sahiBusy;   // SAHI 는 표시 중인 프레임 기준이라 HUD 모드엔 미적용
            // 실제 재생 상태를 따른다 → 어디서 불러도 버튼 글자가 어긋나지 않는다
            // (예전엔 active 일 때만 "일시정지"로 덮어써서, 정지 후에도 "일시정지"가 남았다)
            Btn_PlayPause.Content = _isPlaying ? "일시정지" : "재생";

            // 슬라이더 / 시간 표시: 파일 모드에서만 의미 있음
            VideoSlider.Visibility = (camera || screen) ? Visibility.Hidden : Visibility.Visible;
            VideoSlider.IsEnabled = file && _durationMs > 0 && !_sahiBusy;

            if (camera || screen)
            {
                Txt_Time.Text = screen ? "● HUD" : "● LIVE";
                Txt_Time.SetResourceReference(TextBlock.ForegroundProperty, "DangerBrush");
            }
            else
            {
                Txt_Time.ClearValue(TextBlock.ForegroundProperty);   // 창 글자색으로
                if (!file) Txt_Time.Text = "00:00 / 00:00";
            }

            UpdateRoiUi();   // 구역 편집 가능 여부는 모드·SAHI 중 여부를 따른다
        }

        // ---------- 제목 표시줄 ----------
        // 제목 표시줄과 창 테두리를 앱 색(App.xaml 의 BaseColor·WindowEdgeColor·TextColor)에 맞춘다 — 기본은 흰 제목 표시줄이라 어두운 창과 어긋났다.
        // 다크 모드는 Windows 10 20H1+, 색 지정은 Windows 11 부터 — 지원하지 않는 OS 에선 실패를 무시한다 (기본 모양 그대로)
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        private const int DWMWA_BORDER_COLOR = 34;
        private const int DWMWA_CAPTION_COLOR = 35;
        private const int DWMWA_TEXT_COLOR = 36;

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            var hwnd = new WindowInteropHelper(this).Handle;
            int dark = 1;
            _ = DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
            SetDwmColor(hwnd, DWMWA_CAPTION_COLOR, "BaseColor");
            SetDwmColor(hwnd, DWMWA_BORDER_COLOR, "WindowEdgeColor");
            SetDwmColor(hwnd, DWMWA_TEXT_COLOR, "TextColor");
        }

        private void SetDwmColor(IntPtr hwnd, int attribute, string colorKey)
        {
            if (TryFindResource(colorKey) is not Color c) return;
            int colorRef = c.R | (c.G << 8) | (c.B << 16);   // COLORREF = 0x00BBGGRR
            _ = DwmSetWindowAttribute(hwnd, attribute, ref colorRef, sizeof(int));
        }

        // ---------- 슬라이더 (파일 모드 전용) ----------
        // 썸을 끄는 동안(_isUserDraggingSlider)은 재생 루프가 슬라이더 값을 덮어쓰지 않고, 한 장 보기는 놓을 때 한 번만 한다.
        // 실제 seek 는 ValueChanged 에서 처리.
        // 드래그의 시작·끝은 마우스 이벤트가 아니라 썸의 DragStarted/DragCompleted 로 잡는다. 썸은 놓을 때뿐 아니라 버튼을 누른 채
        // 캡처를 잃어도(Alt+Tab, 다른 창·대화상자가 캡처를 가져감, 슬라이더가 꺼짐 등) 스스로 드래그를 취소하며 DragCompleted(Canceled)
        // 를 올린다. 예전엔 PreviewMouseLeftButtonUp 과 LostMouseCapture(버튼이 떼어졌을 때만)로 끝냈는데, 썸을 잡은 채 Alt+Tab 하면
        // 버튼이 눌린 채 캡처를 잃어 걸러지고 앱 밖에서 놓으면 MouseUp 도 오지 않아 깃발이 남았다 — 재생 중엔 진행 표시가 멈추고,
        // 멈춘 채 시크해도 한 장 보기가 안 나왔다 (이슈 #8).
        // 트랙 클릭은 드래그가 아니다: IsMoveToPointEnabled 면 Slider 가 누른 곳으로 값을 옮기고 그 PreviewMouseLeftButtonDown 을
        // 처리됨으로 표시해(XAML 로 단 Preview 핸들러에도 오지 않는다) 썸이 드래그를 시작하지 않는다 → ValueChanged 가 바로 한 장 보기를 요청한다
        private void VideoSlider_DragStarted(object sender, DragStartedEventArgs e) => _isUserDraggingSlider = true;

        private void VideoSlider_DragCompleted(object sender, DragCompletedEventArgs e) => EndSliderDrag();

        private void EndSliderDrag()
        {
            _isUserDraggingSlider = false;
            // 일시정지 중이면 놓은 위치의 프레임을 한 장 보인다 (끄는 동안은 매번 하지 않는다 — 시크 1회가 100ms 대라 무겁다)
            if (SeekPending) RequestStepFrame();
        }

        // 안전망: 창을 떠나면 끄던 드래그를 끝낸다. 보통은 그 전에 썸이 캡처를 잃고 DragCompleted 로 끝나 있지만, 캡처를 쥔 채
        // 비활성화되면 썸의 드래그도 같이 끝내야 깃발과 어긋나지 않는다(깃발만 내리면 끄는 동안 값마다 한 장 보기를 요청하고 재생 루프가 썸을 되돌린다).
        // CancelDrag 를 바로 부르지 않고 캡처부터 놓는다 — CancelDrag 안의 ReleaseMouseCapture 가 썸의 캡처 잃음 처리에서
        // CancelDrag 를 한 번 더 불러 DragCompleted 가 두 번 온다 (EndSliderDrag 는 두 번 불러도 되지만 군더더기)
        private void Window_Deactivated(object? sender, EventArgs e)
        {
            if (!_isUserDraggingSlider) return;
            // Track 은 전체 이름으로 — 같은 네임스페이스의 추적기 Track(Tracker.cs)이 using 보다 먼저 잡힌다
            var thumb = (VideoSlider.Template?.FindName("PART_Track", VideoSlider) as System.Windows.Controls.Primitives.Track)?.Thumb;
            if (thumb?.IsMouseCaptured == true) thumb.ReleaseMouseCapture();   // → 썸이 CancelDrag → DragCompleted(Canceled) → EndSliderDrag
            else thumb?.CancelDrag();                                           // 캡처 없이 드래그 중이면 바로 취소 (드래그가 아니면 아무 일 없음)
            if (_isUserDraggingSlider) EndSliderDrag();                         // 썸을 못 찾았거나 드래그 끝을 놓쳐 남은 깃발
        }

        private void VideoSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_mode != SourceMode.File) return;

            // 재생 루프가 진행 표시용으로 바꾼 값이면 무시
            if (_isPlaybackUpdatingSlider) return;

            // 사용자 조작(클릭/드래그/키보드) → 즉시 해당 시간대로 이동
            SeekToMs(VideoSlider.Value);
            UpdateTimeText(VideoSlider.Value);
            // 일시정지 중이면 그 위치 프레임을 한 장 보인다 (안 그러면 화면도 SAHI 대상도 옛 프레임에 남는다).
            // 드래그 중이면 놓을 때 한다. 트랙 클릭·키보드·UIA SetValue 는 드래그가 아니라 여기로만 온다
            if (!_isUserDraggingSlider) RequestStepFrame();
        }

        // ---------- 종료 ----------
        protected override async void OnClosed(EventArgs e)
        {
            await StopPlaybackAsync();
            DisposeCapture();
            CloseOverlay();
            _deviceTimer?.Stop();   // NPU 준비 경과 표시 (MainWindow.Npu.cs)

            // SAHI 는 Task.Run 에서 돌고 위 StopPlaybackAsync 가 기다려 주지 않는다. 분석 중에 창을 닫으면
            // 추론 도중 세션이 해제될 수 있으므로, 추론과 같은 락을 잡아 진행 중인 추론이 끝난 뒤 해제한다
            // (다음 타일은 _yolo 가 null 이라 예외 → Btn_Sahi_Click 의 catch 가 받는다)
            lock (_yoloLock)
            {
                _yolo?.Dispose();
                _yolo = null;
                _inferenceClosed = true;   // 아직 도는 NPU 준비가 끝나도 _yolo 를 되살리지 않는다 (MainWindow.Npu.cs)
            }

            _playSignal.Dispose();

            base.OnClosed(e);
        }
    }
}
