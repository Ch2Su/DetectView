using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace YOLO11_Test
{
    /// <summary>화면 HUD — 모니터를 반복 캡처·검출해 바탕화면 위 투명 오버레이에 박스를 그린다.</summary>
    public partial class MainWindow
    {
        // 화면 HUD 모드: 바탕화면 위 투명 오버레이
        private OverlayWindow? _overlay;
        private readonly List<MonitorInfo> _monitors = [];
        // HUD 대상 모니터 (UI 스레드 전용). 캡처 루프는 시작할 때 사본을 받아 쓰고, 배치가 바뀌면(회전·해상도·배치 변경) 새 값을 UI 로 보낸다
        private MonitorInfo _hudMonitor;

        // HUD 최신 결과 (캡처 루프가 쓰고 UI 스레드가 읽음).
        // 루프가 UI 렌더링을 기다리지 않도록 "최신 값 우선" 방식으로 전달한다.
        private volatile List<Det>? _latestDets;
        private int _latestCapW, _latestCapH;
        private double _latestFps;
        private int _uiUpdatePending;
        // 1이면 다음 캡처에서 새 화면을 기다리지 않고 마지막 화면으로 다시 검출한다 (재개 — 정지 화면이면 새 프레임이 안 와서 박스가 안 돌아옴)
        private int _hudForceRefresh;
        private long _lastHudStatusTicks;
        // 상태바의 화면 영역(HUD 모니터 기준 물리 픽셀). 캡처 루프는 이 안에서만 바뀐 화면을 무시한다
        private volatile PixelRect? _hudIgnoreRegion;
        // FPS 칸에 띄운 HUD 문제 문구 (UI 스레드 전용, null = 정상). 떠 있는 동안엔 밀려 온 옛 결과로 박스·FPS 를 되살리지 않는다
        private string? _hudProblem;

        // UIA 로만 보이는 진단 값 — Txt_DetectionCount 의 HelpText "오버레이 박스 N · 재개 재검출 M · 캡처 재연결 K · 받은 화면 F".
        // 화면에 아무것도 안 그려(DXGI 새 화면을 만들지 않음) UI 테스트가 재개 재검출·캡처 재연결을 확인하게 한다 (이슈 #11·#1).
        // 일시정지·재개는 FPS·검출 수 글자를 안 바꿔서 그것만으로는 재개 재검출이 깨져도 테스트가 통과했다
        private int _hudOverlayBoxes;      // [UI] 오버레이에 지금 그려진 박스 수 (OverlayWindow.BoxCountChanged)
        // [루프] 재개 요청(_hudForceRefresh)에 캡처가 마지막 화면을 다시 줘(IScreenCapture.Redelivered) 검출로 넘긴 횟수. 새 화면은 세지 않는다 —
        // 메인 창이 HUD 대상 모니터에 있으면 재개 버튼 글자가 바뀌어 DXGI 가 새 화면을 주는데, 그것까지 세면 강제 경로가 깨져도 테스트가 통과했다
        private int _hudResumeRedetects;
        private int _hudRecreates;         // [루프] 캡처를 새로 만든 횟수 (복구 불가 상실·모니터 배치 변경(GDI)·다시 연결·DXGI 로 돌아감)
        private int _hudFramesReceived;    // [루프] 캡처가 준 새 화면 수 (다시 준 마지막 화면은 빼고) — 다시 만든 캡처가 실제로 화면을 주는지 테스트가 본다
        private int _hudDiagPending;       // [루프→UI] 진단 값 갱신이 디스패치돼 있음 — 받은 화면은 매 프레임 바뀌므로 걸린 것이 있으면 더 걸지 않는다
        private string? _hudDiagShown;

        // FPS 칸에 띄우는 문제 문구. 상태바는 최소 창 폭(900)에서도 HUD 상태(Txt_Status)가 다 보여야 해서 '대기 (화면 변화 없음)'보다
        // 넓게 쓰지 않고 자세한 건 툴팁에. 상태바 글꼴(Segoe UI Variable + 맑은 고딕 12px, Display)로 잰 폭: '대기 (화면 변화 없음)'·아래 세 문구 113px,
        // '모니터 연결 끊김' 90px (11-01 WPF FormattedText — 옛 맑은 고딕 12px 은 116·93px). 예전 '캡처 끊김 — 다시 연결 중' 등은 141px 라
        // 최소 폭에서 HUD 상태를 말줄임시켰다. 끊김·모니터 없음은 경고 색, 오류는 위험 색으로 띄운다 (PostHudProblem)
        private const string HudCaptureCutText = "캡처 끊김 (재연결 중)";
        private const string HudMonitorGoneText = "모니터 연결 끊김";
        private const string HudCaptureErrorText = "캡처 오류 (재시도 중)";
        private const string HudDetectErrorText = "검출 오류 (재시도 중)";
        // 캡처가 끊긴 채(Interruption)·캡처/검출 예외가 이만큼 이어져야 띄운다 — 해상도 변경·UAC 창 등의 짧은 끊김(다시 복제 0.25초)까지
        // 띄우면 깜빡인다. 캡처가 아예 없을 때(모니터 없음·다시 만들기 대기·실패)는 곧바로 띄운다
        private const double HudProblemDelayMs = 1000;
        // 모니터 배치(회전·해상도·배치 변경·연결 끊김) 확인 주기. EnumDisplayMonitors 라 싸다
        private const double HudGeometryCheckMs = 1000;
        // 복구하다 GDI 로 떨어졌을 때(DXGI 를 쓴 적 있음) DXGI 를 다시 열어 보는 첫 주기 — TDR·드라이버 재시작·회전 되돌림 뒤 돌아오게.
        // 시도가 비싸면(장치 생성 0.2초) 30초까지 늘린다
        private const double HudDxgiRetryMs = 3000;
        // 캡처 없이 DXGI 를 다시 열기를 기다리는 동안(잠금 화면·모드 전환 중 등) 다시 열어 보는 간격의 상한 — 풀리면 길어도 이만큼 안에 이어진다
        private const double HudDxgiWaitMaxMs = 2000;
        // 추적기 시계는 새 화면 사이를 이만큼까지만 흐른 것으로 친다. DXGI 는 화면이 바뀔 때만 프레임을 줘서 정지 화면이면 몇 초~몇 분씩 비는데,
        // 그 시간을 그대로 넘기면 물체가 그대로 있는데도 트랙이 만료돼(움직이는 1초·멈춘 10초) 번호가 바뀐다 (CLAUDE.md '추적기' 절의 HUD 주의)
        private const double HudTrackStepCapMs = 250;

        // 연결된 모니터 목록 갱신 (HUD 대상 선택용)
        private void RefreshMonitors()
        {
            _monitors.Clear();
            _monitors.AddRange(ScreenCapture.GetMonitors());

            MonitorSelector.Items.Clear();
            foreach (var m in _monitors)
                MonitorSelector.Items.Add(m.Display);

            if (MonitorSelector.Items.Count > 0)
            {
                int primary = _monitors.FindIndex(m => m.IsPrimary);
                MonitorSelector.SelectedIndex = primary >= 0 ? primary : 0;
            }
            Btn_Screen.IsEnabled = _monitors.Count > 0;
        }

        private async void Btn_Screen_Click(object sender, RoutedEventArgs e)
        {
            if (_switchingMode) return;   // 다른 모드 전환이 진행 중 (MainWindow.xaml.cs)
            if (_yolo == null)
            {
                MessageBox.Show("YOLO 모델이 준비되지 않았습니다.");
                return;
            }

            int sel = MonitorSelector.SelectedIndex;
            if (sel < 0 || sel >= _monitors.Count)
            {
                MessageBox.Show("사용할 모니터를 선택하세요.");
                return;
            }

            _switchingMode = true;
            try
            {
                await TearDownSourceAsync();
                _hudMonitor = _monitors[sel];   // 옛 HUD 루프가 다 내려간 뒤에 바꾼다
                var (started, loop, token) = StartHud();

                // 캡처는 루프 스레드에서 만든다(GDI DC 는 만든 스레드에서 쓰고 해제해야 함). 그 결과를 기다려 HUD 를 확정한다 —
                // 예전엔 생성이 실패하면 오류 창만 뜨고 'HUD 실행 중'·빈 오버레이·눌러도 소용없는 [일시정지]가 남았다 (이슈 #2).
                // 루프가 알리지 않고 끝나거나 아예 못 떠도(시작 전에 취소) 여기서 멈추지 않게 루프 작업도 같이 기다린다
                await Task.WhenAny(started, loop);
                // 기다리는 사이 창이 닫혔다 (전환 중엔 다른 소스 버튼이 막혀 있어 루프를 취소하는 건 OnClosed 뿐) — 정리는 OnClosed 가 하고 오류 창도 띄우지 않는다
                if (token.IsCancellationRequested) return;
                string? error = started.IsCompletedSuccessfully ? started.Result : "화면 캡처 루프가 시작되지 못했습니다.";
                if (error == null) return;

                // 파일·카메라 열기 실패와 같게: [정지]와 같은 정리 → 소스 없음 → 사유. ResetToIdle 은 _isPlaying 을 안 내리므로
                // StopPlaybackAsync(TearDownSourceAsync 안)를 먼저 거쳐야 꺼진 재생 버튼에 '일시정지'가 남지 않는다
                await TearDownSourceAsync();
                ResetToIdle("화면 캡처 시작 실패");
                // 주인 창을 준다 — 앱이 앞에 없을 때도 메인 창의 모달이 되게 (UI 테스트가 메인 창의 자식으로 찾는다)
                MessageBox.Show(this, $"화면 캡처를 시작할 수 없습니다:\n{error}");
            }
            finally { _switchingMode = false; }
        }

        // 반환: 루프의 첫 캡처 생성 결과(null = 성공, 아니면 사유) · 루프 작업 · 루프 토큰
        private (Task<string?> Started, Task Loop, CancellationToken Token) StartHud()
        {
            _mode = SourceMode.Screen;
            _durationMs = 0;
            _vfr = false;
            _smoothedFps = 0;
            _latestDets = null;
            _hudProblem = null;
            Txt_InferenceFps.ToolTip = null;
            _hudResumeRedetects = 0;   // 진단 값은 HUD 를 시작할 때마다 0 부터 (옛 루프는 이미 내려갔다)
            _hudRecreates = 0;
            _hudFramesReceived = 0;
            // 옛 HUD 에서 재개하자마자(루프가 꺼내기 전에) 정지했으면 요청이 남아 있다 — 새 HUD 의 첫 화면을 재개로 치지 않게 지운다
            _hudForceRefresh = 0;
            _loadGeneration++;

            // 선택한 모니터를 정확히 덮는 투명 오버레이 (물리 픽셀로 배치)
            _overlay = new OverlayWindow();
            _overlay.BoxCountChanged += OnOverlayBoxCountChanged;
            _overlay.LegendTextChanged += ShowClassCounts;
            _overlay.TrackTextChanged += ShowTrackMarks;
            _overlay.SetBoundsPhysical(_hudMonitor.X, _hudMonitor.Y, _hudMonitor.Width, _hudMonitor.Height);
            _overlay.Show();
            _hudOverlayBoxes = 0;
            UpdateHudDiag();

            ApplyModeToUi();
            MainImage.Source = null;
            FrameBoxes.Clear();
            PlaceholderText.Text = "화면 HUD 실행 중 — 바탕화면 위에 검출 박스가 표시됩니다";
            PlaceholderText.Visibility = Visibility.Visible;

            UpdateHudStatus(null);
            UpdateHudIgnoreRegion();
            return StartScreenPlayback();
        }

        private (Task<string?> Started, Task Loop, CancellationToken Token) StartScreenPlayback()
        {
            _cts = new CancellationTokenSource();
            _playSignal.Set();
            _isPlaying = true;
            Btn_PlayPause.Content = "일시정지";

            // 루프가 첫 캡처를 만든 뒤 결과를 알린다. 기다리는 쪽(UI)의 이어지는 일을 루프 스레드에서 곧바로 돌리지 않게 비동기로
            var started = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            // 캡처 루프는 수명 내내 블로킹하므로 스레드 풀 대신 전용 스레드(LongRunning)에서 돌린다
            var token = _cts.Token;
            _playbackTask = Task.Factory.StartNew(() => ScreenLoop(token, started), token,
                TaskCreationOptions.LongRunning, TaskScheduler.Default);
            return (started.Task, _playbackTask, token);
        }

        // 메인 창이 HUD 대상 모니터에 있으면, 상태바 글자(FPS 등)가 바뀔 때마다 캡처가 "새 화면"을 받아
        // 재추론하는 고리가 생긴다. 상태바 영역을 캡처 루프에 알려 그 안에서만 바뀐 화면은 무시하게 한다
        private void InitHudTracking()
        {
            LocationChanged += (_, _) => UpdateHudIgnoreRegion();
            SizeChanged += (_, _) => UpdateHudIgnoreRegion();
            StateChanged += (_, _) => UpdateHudIgnoreRegion();
            // FPS 칸의 문제 툴팁(캡처 끊김·검출 오류 사유)은 여러 줄이라 기본 5초로는 다 못 읽는다 (Txt_Status 와 같게 1분)
            ToolTipService.SetShowDuration(Txt_InferenceFps, 60000);
        }

        private void UpdateHudIgnoreRegion()
        {
            if (_mode != SourceMode.Screen || WindowState == WindowState.Minimized || !StatusBarPanel.IsVisible)
            {
                _hudIgnoreRegion = null;
                return;
            }
            // PointToScreen 은 물리 픽셀을 돌려준다
            var tl = StatusBarPanel.PointToScreen(new System.Windows.Point(0, 0));
            var br = StatusBarPanel.PointToScreen(new System.Windows.Point(StatusBarPanel.ActualWidth, StatusBarPanel.ActualHeight));
            _hudIgnoreRegion = new PixelRect(
                (int)Math.Floor(tl.X) - _hudMonitor.X, (int)Math.Floor(tl.Y) - _hudMonitor.Y,
                (int)Math.Ceiling(br.X) - _hudMonitor.X, (int)Math.Ceiling(br.Y) - _hudMonitor.Y);
        }

        // HUD 상태 표시 (캡처 방식이 정해지면 루프가 다시 호출)
        private void UpdateHudStatus(string? captureMethod, string? captureHelp = null)
        {
            if (_overlay == null) return;
            string m = captureMethod == null ? "" : $" · 캡처 {captureMethod}";
            Txt_Status.Text = _overlay.ExcludedFromCapture
                ? $"화면 HUD — 모니터 {_hudMonitor.Index} ({_hudMonitor.Width}×{_hudMonitor.Height}){m}"
                : $"화면 HUD — 모니터 {_hudMonitor.Index} (주의: 오버레이 캡처 제외 미지원 OS){m}";
            // 자세한 해결 방법은 툴팁으로. 상태바는 폭을 넘으면 말줄임되므로 긴 안내를 넣을 수 없다
            Txt_Status.ToolTip = captureHelp;
        }

        // UI 스레드에서 "가장 최신" 검출 결과만 그린다 (밀린 프레임은 자연스럽게 버려짐)
        private void RenderHudLatest()
        {
            // FPS 칸에 문제(캡처 끊김·검출 오류)를 띄운 동안엔 밀려 온 옛 결과로 박스·FPS 를 되살리지 않는다.
            // 루프는 문제를 걷는 알림을 다음 결과보다 먼저 보낸다 (같은 스레드에서 Normal 이 Render 보다 먼저 처리됨)
            if (_hudProblem != null) return;
            var dets = _latestDets;
            if (dets == null) return;

            if (_overlay != null && _latestCapW > 0 && _latestCapH > 0)
            {
                // 캡처(물리 픽셀) → 오버레이(DIP) 변환. DPI 배율도 이걸로 흡수됨
                double ow = _overlay.ActualWidth, oh = _overlay.ActualHeight;
                if (ow > 0 && oh > 0)
                    _overlay.UpdateBoxes(dets, ow / _latestCapW, oh / _latestCapH);
            }

            // 상태바는 초당 4회까지만 갱신한다. 메인 창이 HUD 대상 모니터에 있으면 글자가 바뀔 때마다
            // 캡처가 "새 화면"을 받아 재추론하게 되므로, 정지 화면에서 추론이 쉬도록 고리를 끊는다
            if (Stopwatch.GetElapsedTime(_lastHudStatusTicks).TotalMilliseconds < 250) return;
            _lastHudStatusTicks = Stopwatch.GetTimestamp();

            Txt_Resolution.Text = $"{_latestCapW}×{_latestCapH}";
            Txt_InferenceFps.Text = $"{_latestFps:F1} FPS";
            Txt_DetectionCount.Text = $"검출 {dets.Count}";
        }

        private void CloseOverlay()
        {
            if (_overlay == null) return;
            _overlay.BoxCountChanged -= OnOverlayBoxCountChanged;
            _overlay.LegendTextChanged -= ShowClassCounts;
            _overlay.TrackTextChanged -= ShowTrackMarks;
            try { _overlay.Close(); } catch (Exception ex) { Debug.WriteLine(ex); }
            _overlay = null;
            // HUD 를 내리면 FPS 칸의 문제 표시(툴팁)·진단 값·범례 글자도 걷는다 — ResetToIdle 은 글자만 비운다.
            // 범례는 닫힌 오버레이의 것이라 지우는 알림이 오지 않는다
            _hudProblem = null;
            Txt_InferenceFps.ToolTip = null;
            Txt_InferenceFps.ClearValue(TextBlock.ForegroundProperty);
            _hudDiagShown = null;
            Txt_DetectionCount.ClearValue(AutomationProperties.HelpTextProperty);
            Txt_DetectionCount.ClearValue(AutomationProperties.ItemStatusProperty);
            Txt_DetectionCount.ClearValue(AutomationProperties.ItemTypeProperty);
        }

        private void OnOverlayBoxCountChanged(int count)
        {
            _hudOverlayBoxes = count;
            UpdateHudDiag();
        }

        // [UI] 진단 값을 Txt_DetectionCount 의 HelpText 로 내보낸다 (바뀔 때만). UIA 속성이라 그리지 않는다 → 캡처에 새 화면이 생기지 않는다
        private void UpdateHudDiag()
        {
            string s = $"오버레이 박스 {_hudOverlayBoxes} · 재개 재검출 {Volatile.Read(ref _hudResumeRedetects)} · " +
                       $"캡처 재연결 {Volatile.Read(ref _hudRecreates)} · 받은 화면 {Volatile.Read(ref _hudFramesReceived)}";
            if (s == _hudDiagShown) return;
            _hudDiagShown = s;
            AutomationProperties.SetHelpText(Txt_DetectionCount, s);
        }

        // [루프] 진단 값이 바뀌었다 (재개 재검출·받은 화면 수). 받은 화면은 매 프레임 바뀌므로 디스패치는 하나만 걸어 두고 그 콜백이 최신 값을 읽는다
        private void PostHudDiag(CancellationToken ct)
        {
            if (Interlocked.Exchange(ref _hudDiagPending, 1) == 1) return;
            Dispatcher.InvokeAsync(() =>
            {
                // 깃발을 먼저 내리고 읽는다 — 읽은 뒤에 바뀐 값은 다시 걸린 디스패치가 보인다 (RenderHudLatest 와 같은 이유)
                Interlocked.Exchange(ref _hudDiagPending, 0);
                if (!ct.IsCancellationRequested) UpdateHudDiag();
            });
        }

        // [루프] 캡처를 (다시) 만들었거나(recreated) DXGI 캡처인 채 모니터 배치가 바뀌었다 — UI 의 모니터 정보·오버레이 위치·무시 영역·상태바를
        // 지금 캡처·모니터에 맞춘다. 캡처 객체는 루프 스레드 것이라 필요한 값만 여기서 읽어 넘긴다
        private void PostHudCapture(CancellationToken ct, MonitorInfo mon, IScreenCapture capturer, bool recreated)
        {
            string method = capturer.Method;
            string? help = (capturer as GdiScreenCapture)?.FallbackHelp;
            Dispatcher.InvokeAsync(() =>
            {
                if (ct.IsCancellationRequested || _overlay == null) return;
                bool moved = mon != _hudMonitor;
                if (moved || recreated)
                {
                    // 회전·해상도·배치 변경이면 오버레이를 새 영역으로 옮긴다(예전엔 해상도를 바꾸면 옛 크기로 남았다). 영역이 같아도 다시 만들 땐
                    // 다시 놓는다 — 모니터가 빠졌다 돌아오면 Windows 가 그동안 오버레이를 다른 모니터로 옮겨 놓는다.
                    // UpdateLayout: 박스 배율을 오버레이 ActualWidth 로 잡는데 그건 레이아웃 뒤에야 바뀌므로, 새 캡처의 첫 결과
                    // (Render 우선순위 — 이 Normal 콜백 뒤에 처리됨)보다 먼저 맞춰 둔다
                    _hudMonitor = mon;
                    _overlay.SetBoundsPhysical(mon.X, mon.Y, mon.Width, mon.Height);
                    _overlay.UpdateLayout();
                }
                if (moved)
                {
                    UpdateHudIgnoreRegion();   // 모니터 기준 좌표라 원점이 바뀌면 다시 잰다
                    _overlay.ClearBoxes();     // 옛 좌표의 박스. 영역이 같으면 지우지 않는다 — 새 DXGI 복제는 화면이 바뀌어야 첫 화면을 줘서
                                               // (갓 만든 복제의 첫 프레임은 검은 화면이라 건너뛴다 — 10-31 실측), 정지 화면이면 지운 박스가 안 돌아온다
                }
                UpdateHudStatus(method, help);
                if (recreated) UpdateHudDiag();
            });
        }

        // [루프] FPS 칸의 문제 문구·툴팁을 바꾼다 (text = null 이면 걷는다). 문구나 툴팁이 바뀔 때만 부른다.
        // 상태바는 캡처의 무시 영역이라 이 글자가 바뀌어도 새 화면으로 치지 않는다 (스스로 새 화면을 만드는 고리가 생기지 않음)
        private void PostHudProblem(CancellationToken ct, string? text, string? detail, bool clearBoxes)
        {
            Dispatcher.InvokeAsync(() =>
            {
                if (ct.IsCancellationRequested) return;
                _hudProblem = text;
                Txt_InferenceFps.ToolTip = text == null ? null : detail;
                if (text != null)
                {
                    Txt_InferenceFps.Text = text;
                    // 오류(캡처·검출 예외)는 위험 색, 기다리면 될 끊김·모니터 없음은 경고 색 (App.xaml)
                    Txt_InferenceFps.SetResourceReference(TextBlock.ForegroundProperty,
                        text is HudCaptureErrorText or HudDetectErrorText ? "DangerBrush" : "WarnBrush");
                    // 캡처·검출 오류·모니터 없음은 옛 박스를 지운다 (지금 화면의 검출처럼 남지 않게). 기다리면 되는 끊김(UAC·잠금 화면·다시 복제·
                    // DXGI 다시 열기 대기)은 둔다 — 풀린 뒤 화면이 그대로면 새 복제는 화면을 주지 않아(첫 프레임은 검은 화면이라 건너뜀)
                    // 지운 박스가 화면이 바뀔 때까지 돌아오지 않았다
                    if (clearBoxes) _overlay?.ClearBoxes();
                }
                else
                {
                    Txt_InferenceFps.ClearValue(TextBlock.ForegroundProperty);   // 상태바 글자색으로
                    _lastHudStatusTicks = 0;   // 다음 결과는 4Hz 제한에 걸리지 않고 곧바로 FPS 를 쓴다 (안 오면 루프가 1초 뒤 '대기')
                }
            });
        }

        // 장치 이름으로 지금 모니터 정보를 찾는다. 목록이 비면(열거 실패) known = false — 모니터가 없어진 것으로 치지 않는다
        private static MonitorInfo? FindMonitor(string device, out bool known)
        {
            var list = ScreenCapture.GetMonitors();
            known = list.Count > 0;
            foreach (var m in list)
                if (string.Equals(m.Device, device, StringComparison.OrdinalIgnoreCase)) return m;
            return null;
        }

        // 화면을 반복 캡처 → 검출 → 오버레이에 박스 갱신.
        // 전용 스레드에서 동기로 돈다: 캡처 객체(GDI 대체 경로의 DC 등)는 만든 스레드에서 쓰고
        // 해제해야 하므로, await 로 스레드가 바뀌지 않게 한다. 캡처를 잃거나 모니터 배치가 바뀌어 새로 만들 때도 이 스레드에서 한다
        private void ScreenLoop(CancellationToken ct, TaskCompletionSource<string?> started)
        {
            var mon = _hudMonitor;
            IScreenCapture? capturer = null;
            try
            {
                try
                {
                    // 모니터 목록은 [새로고침] 때만 읽는다 → 그 뒤 해상도·배치가 바뀌었으면 옛 좌표라, 장치 이름으로 지금 값을 다시 읽는다
                    if (FindMonitor(mon.Device, out _) is { } current) mon = current;
                    TestFaults.HudCreate();   // 시험용 생성 실패 (Debug 빌드 + DETECTVIEW_TEST_FAULTS=hud-create-fail 일 때만)
                    capturer = ScreenCapture.Create(mon);   // DXGI 우선, 불가 시 GDI
                }
                catch (Exception ex)
                {
                    started.TrySetResult(ex.Message);   // Btn_Screen_Click 이 소스 없음으로 되돌리고 사유를 알린다 (이슈 #2)
                    return;
                }
                // DXGI 를 쓴 적 있는 HUD 만 캡처를 다시 만들 때 DXGI 를 바로 열고(기다리면 될 실패면 캡처 없이 기다림), GDI 로 떨어져도 DXGI 로
                // 돌아가 본다. 처음부터 GDI 면 환경 탓(하이브리드 GPU 모드·원격 데스크톱 등)이라 대개 앱을 다시 띄우기 전엔 안 바뀐다.
                // 나중에라도 DXGI 가 되면(회전을 되돌려 GDI 를 다시 만들다 DXGI 가 열림 등) 그때부터 같다 (Replace)
                bool usedDxgi = capturer is DxgiScreenCapture;
                PostHudCapture(ct, mon, capturer, recreated: false);
                started.TrySetResult(null);

                long lastFrameTicks = Stopwatch.GetTimestamp();
                bool idleShown = false;
                long geometryTicks = lastFrameTicks;   // 마지막 모니터 배치 확인
                long dxgiRetryTicks = lastFrameTicks;  // 마지막 GDI→DXGI 재시도 (캡처를 갈아 끼울 때마다 다시 잰다)
                double dxgiRetryMs = HudDxgiRetryMs;   // 비싼 시도마다 두 배 (HUD 를 다시 시작하면 처음부터)
                long createTicks = 0;                  // 캡처가 없을 때 마지막으로 만들어 본 시각
                int createFailures = 0;
                bool waitingDxgi = false;              // 캡처가 없는 게 기다리면 될 DXGI 실패 때문 → 짧은 간격(HudDxgiWaitMaxMs 까지)으로 다시 연다
                long dxgiGraceSince = 0;               // 다시 열다 '잠깐일 수도 있는' DXGI 실패(UNSUPPORTED·장치 생성 실패 등)가 처음 난 시각 (0 = 없음)
                string? outageWhy = null;              // 캡처를 다시 만들기 시작한 이유 (다시 시도해도 툴팁에 그대로 남긴다)
                string? downText = null, downDetail = null;   // 캡처가 없는 이유 (모니터 없음 · DXGI 다시 열기 대기 · 다시 만들기 실패)
                bool downClears = false;                      // 그동안 오버레이 박스를 지울지 (대기는 두고 모니터 없음·실패는 지운다 — PostHudProblem)
                long interruptedSince = 0;                    // 캡처가 끊긴(Interruption) 채 이어진 시작 시각 (0 = 정상)
                long captureErrorSince = 0, detectErrorSince = 0;
                string? captureError = null, detectError = null;
                string? shownProblem = null, shownDetail = null;   // FPS 칸에 띄운 문제와 툴팁 (어느 쪽이든 바뀔 때만 UI 로 보낸다)
                // 검출이 실패한 화면을 물러났다가(0.5·1·2초, 그 뒤 5초마다) 다시 검출한다 — DXGI 는 화면이 바뀔 때만 프레임을 줘서, 정지 화면이면
                // 오류가 풀려도 새 화면이 올 때까지 박스가 돌아오지 않는다. 물러나지 않으면 검출이 계속 실패할 때 정지 화면에서도 같은 화면을
                // 끝없이 다시 검출해(초당 6회쯤) GPU 가 쉬지 못했다
                bool retryLast = false;
                int detectFailures = 0;                // 이어진 검출 실패 수 (검출이 되면 0)
                long detectFailTicks = 0;              // 마지막 검출 실패 시각
                // 추적기: 박스에 번호와 정지 여부를 붙인다 (이 루프 스레드에서만 — 락이 없다). 비우는 때: 캡처를 갈아 끼울 때(Replace)·모니터 배치가
                // 바뀔 때(좌표계가 달라진다)·재개 때(멈춘 사이 화면이 바뀌었을 수 있다). trackMs 는 HudTrackStepCapMs 로 자른 가상 시계
                var tracker = new Tracker();
                long trackTicks = 0;
                double trackMs = 0;

                while (!ct.IsCancellationRequested)
                {
                    try { _playSignal.Wait(ct); }
                    catch (OperationCanceledException) { break; }
                    if (ct.IsCancellationRequested) break;

                    // 1초마다 모니터 배치 확인 — 회전·해상도·배치가 바뀌었으면 오버레이·무시 영역·상태바를 새 좌표로 맞춘다 (예전엔 옛 좌표에 남았다).
                    // 캡처는 GDI 만 새로 만든다(만든 크기·좌표로만 읽는다). DXGI 는 모드가 바뀌면 다시 복제하며 해상도를 따라가고, 회전은 상실
                    // (ScreenCaptureLostException)로 온다. 모니터가 없어졌으면 돌아올 때까지 캡처 없이 기다린다
                    if (Stopwatch.GetElapsedTime(geometryTicks).TotalMilliseconds >= HudGeometryCheckMs)
                    {
                        geometryTicks = Stopwatch.GetTimestamp();
                        var fresh = FindMonitor(mon.Device, out bool known);
                        if (fresh is { } m)
                        {
                            bool moved = m.X != mon.X || m.Y != mon.Y || m.Width != mon.Width || m.Height != mon.Height;
                            if (downText == HudMonitorGoneText) Recreate(m, "모니터 다시 연결됨");
                            else if (moved && capturer is GdiScreenCapture) Recreate(m, "모니터 배치 바뀜");
                            else if (moved)
                            {
                                mon = m;   // 캡처가 없으면(다시 열기 대기) 다음 다시 만들기가 이 값을 쓰고 UI 도 그때 맞춘다
                                tracker.Reset();   // 해상도·위치가 바뀌어 박스 좌표계가 달라졌다
                                if (capturer != null) PostHudCapture(ct, mon, capturer, recreated: false);
                            }
                        }
                        else if (known && downText != HudMonitorGoneText) MarkMonitorGone();
                    }

                    if (capturer == null)
                    {
                        // 캡처가 없다: 물러났다가 다시 만든다 (DXGI 다시 열기 대기면 0.5·1초 뒤 2초마다, 만들기 실패면 0.5·1·2·4초 뒤 5초마다).
                        // 모니터가 없을 땐 위 배치 확인이 돌아온 걸 본다
                        if (downText != HudMonitorGoneText &&
                            Stopwatch.GetElapsedTime(createTicks).TotalMilliseconds >= RecreateDelayMs(createFailures, waitingDxgi))
                            RecreateFresh("다시 시도");
                    }
                    else if (usedDxgi && capturer is GdiScreenCapture
                             && Stopwatch.GetElapsedTime(dxgiRetryTicks).TotalMilliseconds >= dxgiRetryMs)
                    {
                        // GDI 로 떨어졌다 — TDR·드라이버 재시작이 끝났거나 회전을 되돌렸으면 DXGI 가 다시 된다
                        // (0°↔180° 처럼 가로·세로가 그대로인 회전은 위 배치 확인으로는 못 알아챈다)
                        dxgiRetryTicks = Stopwatch.GetTimestamp();
                        DxgiScreenCapture? dxgi = null;
                        try { dxgi = new DxgiScreenCapture(mon.Device); }
                        catch (Exception ex) { Debug.WriteLine($"DXGI 다시 시도 실패 (GDI 로 계속): {ex.Message}"); }
                        // 장치까지 만들어 본 시도(D3D11CreateDevice 0.2초 — 그동안 이 루프가 멈춘다)는 간격을 늘린다(3·6·12·24·30초).
                        // 회전처럼 장치 전에 걸러지면 몇 ms 라 3초 그대로. 되자마자 또 잃는 일이 되풀이돼도 30초에 한 번으로 준다
                        if (Stopwatch.GetElapsedTime(dxgiRetryTicks).TotalMilliseconds > 50)
                            dxgiRetryMs = Math.Min(30000, dxgiRetryMs * 2);
                        if (dxgi != null) Replace(dxgi, "DXGI 다시 됨");
                    }

                    if (capturer == null)
                    {
                        UpdateProblem();
                        if (ct.WaitHandle.WaitOne(100)) break;
                        continue;
                    }

                    List<Det> dets;
                    int capW, capH;
                    long frameTs;
                    bool detecting = false;
                    bool resumed = Interlocked.Exchange(ref _hudForceRefresh, 0) == 1;
                    bool retryDue = retryLast &&
                                    Stopwatch.GetElapsedTime(detectFailTicks).TotalMilliseconds >= DetectRetryDelayMs(detectFailures);
                    // 재개·검출 재시도는 새 화면을 기다리지 않고 마지막 화면을 다시 받아 검출한다. 캡처가 끊긴 동안엔 미룬다 — 끊김을 FPS 칸에
                    // 띄운 동안 나온 결과는 그리지 않아서(RenderHudLatest) 요청만 사라지고, 풀린 뒤 정지 화면이면 박스가 안 돌아온다
                    bool force = (resumed || retryDue) && capturer.Interruption == null;
                    try
                    {
                        // 새 화면을 최대 100ms 기다린다. 변화가 없으면 null → 추론을 건너뛰고 이전 박스 유지
                        capturer.IgnoreRegion = _hudIgnoreRegion;
                        using var bgra = capturer.TryCapture(100, force);
                        captureErrorSince = 0;   // 예외 없이 돌아왔으면 캡처는 돈다 (화면 변화가 없을 뿐일 수 있음)
                        if (capturer.Interruption == null) interruptedSince = 0;
                        else if (interruptedSince == 0) interruptedSince = Stopwatch.GetTimestamp();

                        if (bgra == null)
                        {
                            // 다시 줄 화면이 아직 없거나(갓 만든 복제 — 첫 프레임은 검은 화면이라 건너뛴다) 끊긴 동안이다 → 재개 요청은
                            // 다음 화면까지 남긴다 (그 화면이 새 화면이면 재개 재검출로 세지 않는다)
                            if (resumed) Interlocked.Exchange(ref _hudForceRefresh, 1);
                            UpdateProblem();
                            // 1초 넘게 화면 변화가 없으면 '대기' 표시 (멈춘 FPS 값이 느린 것처럼 보이지 않도록). 문제를 띄운 동안엔 그대로 둔다
                            if (shownProblem == null && !idleShown && Stopwatch.GetElapsedTime(lastFrameTicks).TotalMilliseconds > 1000)
                            {
                                idleShown = true;
                                Dispatcher.InvokeAsync(() =>
                                {
                                    if (ct.IsCancellationRequested || !_isPlaying || _hudProblem != null) return;
                                    Txt_InferenceFps.Text = "대기 (화면 변화 없음)";
                                    // 상태바는 4Hz 로 제한되므로 마지막 검출 수가 늦게 반영됐을 수 있어 여기서 맞춘다
                                    if (_latestDets is { } last) Txt_DetectionCount.Text = $"검출 {last.Count}";
                                });
                            }
                            continue;
                        }
                        // 재개 요청은 다시 준 화면이든 새 화면이든 이 화면으로 끝난다. 진단 값은 둘을 가른다 — 다시 준 마지막 화면만 '재개 재검출',
                        // 새 화면은 '받은 화면'. 재개 버튼 글자가 바뀌어 온 새 화면을 재개 재검출로 세면 강제 경로가 깨져도 테스트가 통과했다 (이슈 #11)
                        if (!capturer.Redelivered)
                        {
                            Interlocked.Increment(ref _hudFramesReceived);
                            PostHudDiag(ct);
                        }
                        else if (resumed)
                        {
                            Interlocked.Increment(ref _hudResumeRedetects);
                            PostHudDiag(ct);
                        }
                        lastFrameTicks = Stopwatch.GetTimestamp();
                        idleShown = false;

                        frameTs = capturer.FrameTimestamp;
                        capW = bgra.Width;
                        capH = bgra.Height;
                        // 캡처 버퍼(BGRA)를 그대로 추론 — BGR 왕복 변환 생략. 같은 자리 중복 박스는 파일 재생과 같은 규칙으로 뺀다
                        detecting = true;
                        TestFaults.HudDetect();   // 시험용 검출 예외 (Debug 빌드 + DETECTVIEW_TEST_FAULTS=hud-detect-fail 일 때만)
                        dets = DuplicateBoxes.Remove(DetectBgra(bgra, 0, 0));
                        detectErrorSince = 0;
                        retryLast = false;
                        detectFailures = 0;
                    }
                    catch (ScreenCaptureLostException ex)
                    {
                        // 같은 장치·출력으로는 못 살린다(장치 제거·회전·UNSUPPORTED 가 이어짐 등) → 새로 만든다 (Recreate — DXGI 를 쓰던 HUD 는 DXGI 를
                        // 바로 다시 열고 기다리면 될 실패면 기다린다). 예전엔 같은 장치로 0.25초마다 다시 복제만 해서 끝없이 실패했고, 상태는 '대기'에 머물렀다 (이슈 #1)
                        Debug.WriteLine($"화면 캡처 상실 → 새로 만듦: {ex.Message}");
                        if (resumed) Interlocked.Exchange(ref _hudForceRefresh, 1);
                        RecreateFresh(ex.Message);
                        continue;
                    }
                    catch (Exception ex)
                    {
                        // 예전엔 Debug 출력뿐이라 추론 GPU 가 죽어도(장치 제거 등) 박스·FPS 가 마지막 값에 멈춘 채 드러나지 않았다 (이슈 #1)
                        Debug.WriteLine($"화면 캡처/검출 오류: {ex.Message}");
                        if (detecting)
                        {
                            if (detectErrorSince == 0) detectErrorSince = Stopwatch.GetTimestamp();
                            detectError = ex.Message;
                            retryLast = true;   // 그 화면을 물러났다가 다시 검출한다 (새 화면이 오면 그걸 곧바로 검출)
                            detectFailures++;
                            detectFailTicks = Stopwatch.GetTimestamp();
                        }
                        else
                        {
                            if (captureErrorSince == 0) captureErrorSince = Stopwatch.GetTimestamp();
                            captureError = ex.Message;
                            if (resumed) Interlocked.Exchange(ref _hudForceRefresh, 1);
                        }
                        UpdateProblem();
                        if (ct.WaitHandle.WaitOne(50)) break;   // 스레드를 유지한 채 잠시 대기
                        continue;
                    }
                    UpdateProblem();   // 검출이 됐으니 문제를 걷는다 — 걷는 알림이 아래 결과보다 먼저 UI 에 간다

                    if (ct.IsCancellationRequested) break;

                    // 화면을 얻은 순간 → 검출 완료까지가 박스 지연. FPS 는 그 역수 (새 화면 대기 시간 제외)
                    double sec = Stopwatch.GetElapsedTime(frameTs).TotalSeconds;
                    double instFps = sec > 0 ? 1.0 / sec : 0;
                    _smoothedFps = _smoothedFps <= 0 ? instFps : _smoothedFps * 0.85 + instFps * 0.15;

                    // 번호·정지 여부를 붙인다. FPS(박스 지연)에는 안 넣으려고 위 계산 뒤에 — 추적은 검출이 끝난 뒤의 일이다.
                    // 정지 화면이 오래 이어졌다 새 화면이 와도 트랙이 만료되지 않게 시계는 HudTrackStepCapMs 로 자른다.
                    // 추적기가 실패해도 박스는 번호 없이 그대로 간다
                    long nowTicks = Stopwatch.GetTimestamp();
                    if (resumed) tracker.Reset();
                    if (trackTicks != 0)
                        trackMs += Math.Min(Stopwatch.GetElapsedTime(trackTicks, nowTicks).TotalMilliseconds, HudTrackStepCapMs);
                    trackTicks = nowTicks;
                    try { dets = tracker.UpdateTagged(dets, trackMs, capW, capH); }
                    catch (Exception ex) { Debug.WriteLine($"추적기 오류: {ex.Message}"); tracker.Reset(); }

                    // 최신 결과만 남기고, UI 렌더링을 기다리지 않는다.
                    // (await 하면 UI 시간이 캡처 주기에 더해져 박스가 눈에 띄게 밀린다)
                    // volatile 인 _latestDets 는 반드시 마지막에 쓴다: volatile 쓰기는 "앞의 쓰기"만 먼저 보이게
                    // 보장하므로, 순서가 반대면 UI 가 새 박스를 옛 캡처 크기로 스케일할 수 있다
                    _latestCapW = capW;
                    _latestCapH = capH;
                    _latestFps = _smoothedFps;
                    _latestDets = dets;

                    if (Interlocked.Exchange(ref _uiUpdatePending, 1) == 0)
                    {
                        _ = Dispatcher.InvokeAsync(() =>
                        {
                            // 깃발을 먼저 내리고 나서 최신 결과를 읽는다. 그리는 사이 도착한 결과는 디스패치를 다시 건다
                            // (같은 결과를 두 번 그려도 LooksSame 이 거른다). 예전엔 다 그린 뒤 finally 에서 내려서,
                            // 읽은 뒤~내리기 전에 온 결과가 다음 새 화면까지 묻혔다 — 화면이 그대로 멈추면 낡은 박스가 계속 남았다
                            Interlocked.Exchange(ref _uiUpdatePending, 0);
                            // 일시정지 직전에 끝난 프레임이 지운 박스를 다시 그리지 않도록 _isPlaying 도 확인
                            if (ct.IsCancellationRequested || !_isPlaying) return;
                            RenderHudLatest();
                        }, System.Windows.Threading.DispatcherPriority.Render);
                    }
                }

                // ---- 루프 스레드 전용 도우미 (위 지역 변수를 같이 쓴다) ----

                // 지금 모니터 값을 다시 읽어 새로 만든다 (회전이면 가로·세로가 바뀌었다). 모니터가 없어졌으면 돌아올 때까지 기다린다
                void RecreateFresh(string why)
                {
                    var fresh = FindMonitor(mon.Device, out bool known);
                    if (fresh is { } m) Recreate(m, why);
                    else if (known) MarkMonitorGone();
                    else Recreate(mon, why);   // 모니터 열거 실패 — 아는 값으로
                }

                // 캡처를 버리고 target 으로 새로 만든다 — 해제·생성 모두 이 루프 스레드에서 (GDI DC 규약).
                // DXGI 를 쓴 적 있으면 DXGI 를 바로 연다: ScreenCapture.Create 는 DXGI 가 안 되면 곧바로 GDI 로 떨어지는데, 잠금 화면·UAC 보안 데스크톱·
                // 모드 전환 중에 다시 만들면 DuplicateOutput 이 잠깐 실패해(E_ACCESSDENIED 등) GDI 로 떨어졌고, 풀린 뒤에도 GDI→DXGI 재시도를
                // 기다려야 했다(장치까지 만든 시도마다 간격이 두 배, 30초까지). 그래서 기다리면 될 실패면 캡처 없이(FPS 칸 '캡처 끊김', 박스는 그대로)
                // 2초 안 간격으로 다시 열고, 기다려도 안 될 실패(회전·UNSUPPORTED 나 장치 생성 실패가 이어짐)만 사유가 붙은 GDI 로 넘어간다
                void Recreate(MonitorInfo target, string why)
                {
                    capturer?.Dispose();
                    capturer = null;
                    mon = target;
                    createTicks = Stopwatch.GetTimestamp();
                    outageWhy ??= why;
                    IScreenCapture? created = null;
                    Exception? failure = null;
                    if (usedDxgi)
                    {
                        try { created = new DxgiScreenCapture(target.Device); }
                        catch (Exception ex)
                        {
                            var kind = DxgiScreenCapture.ClassifyCreateFailure(ex);
                            if (kind == CreateFailureKind.Grace && dxgiGraceSince == 0) dxgiGraceSince = Stopwatch.GetTimestamp();
                            bool inGrace = kind == CreateFailureKind.Grace &&
                                           Stopwatch.GetElapsedTime(dxgiGraceSince).TotalMilliseconds <= DxgiScreenCapture.FailureGraceMs;
                            if (kind == CreateFailureKind.Wait || inGrace)
                            {
                                createFailures++;
                                waitingDxgi = true;
                                downText = HudCaptureCutText;
                                downClears = false;   // 기다리면 되는 끊김 — 풀린 뒤 정지 화면이면 새 캡처는 화면을 주지 않아 지운 박스가 안 돌아온다
                                downDetail = $"화면 캡처를 다시 여는 중입니다 ({outageWhy}).\n사유: {ex.Message}\n" +
                                             (kind == CreateFailureKind.Wait
                                                 ? "잠금 화면·UAC 보안 데스크톱·화면 모드 변경 등이 끝나면 이어서 캡처합니다."
                                                 : $"{DxgiScreenCapture.FailureGraceMs / 1000}초 넘게 안 되면 느린 GDI 로 캡처합니다.");
                                Debug.WriteLine($"HUD 캡처 DXGI 다시 열기 대기 ({kind}): {ex.Message}");
                                return;
                            }
                            // 기다려도 안 될 실패 → 사유가 붙은 GDI (GDI→DXGI 재시도가 이어서 DXGI 를 다시 열어 본다)
                            try { created = ScreenCapture.Fallback(target, ex); }
                            catch (Exception gex) { failure = gex; }
                        }
                    }
                    else
                    {
                        try { created = ScreenCapture.Create(target); }   // 새 장치의 DXGI, 안 되면 사유가 붙은 GDI
                        catch (Exception ex) { failure = ex; }
                    }

                    if (created == null)
                    {
                        // GDI 까지 못 만들었다: 캡처 없이 두고 물러났다가 다시 만든다 (그동안 FPS 칸에 '캡처 끊김', 옛 박스는 지운다)
                        createFailures++;
                        waitingDxgi = false;
                        downText = HudCaptureCutText;
                        downClears = true;
                        downDetail = $"화면 캡처를 다시 만들지 못했습니다 ({outageWhy}): {failure?.Message}\n" +
                                     "계속 다시 시도합니다. 풀리지 않으면 [정지] 후 [화면 HUD] 를 다시 누르세요.";
                        Debug.WriteLine($"HUD 캡처 다시 만들기 실패: {failure?.Message}");
                        return;
                    }
                    Replace(created, why);
                }

                // 새 캡처로 갈아 끼우고 UI(오버레이 위치·무시 영역·상태바)를 맞춘다. 끊김·오류는 새 캡처로 다시 잰다 —
                // 옛 캡처의 것을 이어 세면 갈아 끼우자마자 문제로 띄운다
                void Replace(IScreenCapture created, string why)
                {
                    capturer?.Dispose();
                    capturer = created;
                    usedDxgi |= created is DxgiScreenCapture;
                    createFailures = 0;
                    waitingDxgi = false;
                    dxgiGraceSince = 0;
                    outageWhy = null;
                    downText = downDetail = null;
                    downClears = false;
                    interruptedSince = captureErrorSince = detectErrorSince = 0;
                    retryLast = false;   // 새 캡처엔 다시 줄 화면이 없다
                    detectFailures = 0;
                    tracker.Reset();     // 새 캡처는 앞 캡처와 이어지는 화면이 아니다 (회전·장치 제거·모니터 다시 연결)
                    lastFrameTicks = dxgiRetryTicks = Stopwatch.GetTimestamp();
                    idleShown = false;
                    Interlocked.Increment(ref _hudRecreates);
                    PostHudCapture(ct, mon, created, recreated: true);
                    Debug.WriteLine($"HUD 캡처 새로 만듦 ({why}) → {created.Method}");
                }

                void MarkMonitorGone()
                {
                    capturer?.Dispose();
                    capturer = null;
                    waitingDxgi = false;
                    outageWhy ??= "모니터 연결 끊김";
                    downText = HudMonitorGoneText;
                    downClears = true;   // 모니터가 빠지면 Windows 가 오버레이를 다른 모니터로 옮겨 놓는다 — 옛 박스가 엉뚱한 화면에 남지 않게
                    downDetail = $"HUD 대상 모니터({mon.Device})를 찾을 수 없습니다. 다시 연결되면 이어서 캡처합니다.\n" +
                                 "다른 모니터로 바꾸려면 [정지] 후 [새로고침]하고 다시 시작하세요.";
                }

                // FPS 칸의 문제 표시를 지금 상태에 맞춘다 (문구나 툴팁이 바뀔 때만 UI 로 보낸다). 우선순위: 캡처 없음 > 끊김 > 캡처 예외 > 검출 예외.
                // 캡처 없음(모니터 없음·DXGI 다시 열기 대기·다시 만들기 실패)은 곧바로, 끊김·예외는 1초 넘게 이어져야 띄운다.
                // 오류·모니터 없음은 오버레이의 옛 박스를 지우고, 기다리면 되는 끊김(Interruption·DXGI 다시 열기 대기)은 둔다 (PostHudProblem)
                void UpdateProblem()
                {
                    string? text = null, detail = null;
                    bool clear = false;
                    if (downText != null)
                        (text, detail, clear) = (downText, downDetail, downClears);
                    else if (Lasted(interruptedSince))
                        (text, detail) = (HudCaptureCutText, $"화면 캡처가 끊겨 다시 연결하고 있습니다.\n사유: {capturer?.Interruption}");
                    else if (Lasted(captureErrorSince))
                        (text, detail, clear) = (HudCaptureErrorText, $"화면 캡처가 계속 실패해 다시 시도하고 있습니다: {captureError}", true);
                    else if (Lasted(detectErrorSince))
                        (text, detail, clear) = (HudDetectErrorText, $"검출(추론)이 계속 실패해 다시 시도하고 있습니다: {detectError}\n" +
                                                                     "오래 이어지면 앱을 다시 시작하세요 (추론 GPU 의 드라이버가 다시 시작된 경우 등).", true);
                    // 툴팁도 비교한다 — 짧은 문구가 같아도 사유가 바뀌면(DXGI 다시 열기 대기 중 실패 코드가 바뀜 등) 툴팁을 새로 써야 한다
                    if (text == shownProblem && detail == shownDetail) return;
                    shownProblem = text;
                    shownDetail = detail;
                    PostHudProblem(ct, text, detail, clear);
                    if (text == null) { idleShown = false; lastFrameTicks = Stopwatch.GetTimestamp(); }   // 걷은 뒤 1초 안에 새 화면이 없으면 '대기'
                }

                static bool Lasted(long since) => since != 0 && Stopwatch.GetElapsedTime(since).TotalMilliseconds > HudProblemDelayMs;
            }
            finally
            {
                capturer?.Dispose();
                // 알리기 전에 예외로 끝났어도 기다리는 쪽(Btn_Screen_Click)이 멈추지 않게. 이미 알렸으면 아무 일도 없다
                started.TrySetResult("화면 캡처 루프가 예기치 않게 끝났습니다.");
            }
        }

        // 캡처가 없을 때 다시 만들어 보는 간격 (실패 수 1부터): 0.5·1·2초 … — DXGI 다시 열기를 기다리는 중이면 2초에서(풀리면 그 안에 이어지게),
        // 그 밖(GDI 까지 못 만듦)이면 4초 뒤 5초에서 멈춘다
        private static double RecreateDelayMs(int failures, bool waitingDxgi) =>
            Math.Min(waitingDxgi ? HudDxgiWaitMaxMs : 5000, 500 * Math.Pow(2, Math.Max(0, failures - 1)));

        // 검출이 실패한 화면을 다시 검출해 보는 간격 (이어진 실패 수 1부터): 0.5·1·2초, 그 뒤 5초마다
        private static double DetectRetryDelayMs(int failures) => failures switch
        {
            <= 1 => 500,
            2 => 1000,
            3 => 2000,
            _ => 5000,
        };
    }
}
