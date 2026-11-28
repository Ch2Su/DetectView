using OpenCvSharp;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace DetectView
{
    /// <summary>파일/카메라 공용 재생 루프, 시크, 재생 종료·카메라 유실 처리.</summary>
    public partial class MainWindow
    {
        // .NET 9 의 System.Threading.Lock 사용
        private readonly Lock _captureLock = new();
        private VideoCapture? _capture;

        // 파일 위치/길이 상태
        private double _fps = 30.0;
        private double _durationMs;
        private int _totalFrames;
        private int _currentFrameIdx;
        private bool _vfr;   // 가변 프레임레이트(화면녹화 webm 등): 위치를 POS_MSEC로 추적
        private double _seekRefineTargetMs = -1;  // VFR seek 후 정밀 보정 목표(ms). -1=없음 (재생 루프 전용)
        // 슬라이더가 요청한 시크 위치(ms). NaN=요청 없음.
        // 시크 1회가 100ms 이상 걸리므로 UI 스레드는 값만 기록하고, 재생 루프가 최신 요청 1건만 적용한다
        private double _pendingSeekMs = double.NaN;
        private bool _isUserDraggingSlider;
        private bool _isPlaybackUpdatingSlider;
        // 파일 끝까지 재생해 멈춘 상태 (UI 스레드 전용). 이 상태에서 재생을 누르면 처음부터 다시 튼다
        private bool _atEof;
        // 프레임 시크(Set(PosFrames))를 쓰면 안 되는 파일 — 메타데이터를 믿을 수 없는 파일 전부(VFR 경로, OpenVideoFile):
        // 길이가 없어 FRAME_COUNT 가 쓰레기값이거나, fps 가 실제 속도가 아닌 경우(타임베이스 값 1000 등).
        // 이런 파일은 grab 으로만 이동하고, 뒤로 갈 땐 _filePath 로 다시 연다 (ApplyPendingSeek)
        private bool _seekByGrab;
        private string? _filePath;
        // 마지막으로 꺼낸 시크 목표(ms — 재생 루프 전용). 메타데이터를 믿던 파일이 시크 직후 VFR 로 드러나면 grab 경로로 이 목표에 다시 간다
        private double _lastSeekTargetMs;
        // 마지막으로 읽은 프레임의 영상 시각 (재생 루프 전용). EOF 뒤엔 캡처의 PosMsec 를 믿을 수 없어
        // (되감을지 판단에 쓰면 "처음부터 다시"가 안 됐다) 루프가 직접 기억한다
        private double _lastPosMs;
        // 일시정지 중 시크한 위치를 한 장 보여 달라는 요청 (UI → 재생 루프, Interlocked 1/0). 재생 신호 _playSignal 과 따로 둔다 —
        // 한 장 보인 뒤 루프가 _playSignal.Reset() 을 부르게 짜면, 그사이 눌린 '재생'의 Set() 을 덮어써 재생이 멈춘다
        // (Test-FilePlayback 이 시크 직후 곧바로 재생을 누른다)
        private int _stepRequest;
        // 한 장 보기·시크로 얻는 프레임을 처리 중 (재생 루프가 쓰고 SAHI 가 읽는다) — SAHI 가 시크 전 프레임을 분석하지 않게 끝나길 기다린다.
        // 요청을 꺼내기 **전에** 세우고 루프 맨 위에서 내린다 (꺼낸 뒤 세우면 그 틈엔 요청도 처리 중도 없는 것으로 보인다)
        private volatile bool _stepping;

        // ---------- 재생 시작/정지 ----------
        private void StartPlayback()
        {
            _cts = new CancellationTokenSource();
            _playSignal.Set();
            _isPlaying = true;
            Btn_PlayPause.Content = "일시정지";
            UpdateRoiUi();

            var token = _cts.Token;
            _playbackTask = Task.Run(() => PlaybackLoop(token), token);
        }

        private async Task StopPlaybackAsync()
        {
            _cts?.Cancel();
            _playSignal.Set(); // Wait()에서 깨어나 종료 가능하게

            if (_playbackTask != null)
            {
                try { await _playbackTask; }
                catch (OperationCanceledException) { /* 정상 종료 */ }
                catch (Exception ex) { Debug.WriteLine(ex); }
                _playbackTask = null;
            }

            _cts?.Dispose();
            _cts = null;
            _isPlaying = false;
        }

        private void DisposeCapture()
        {
            lock (_captureLock)
            {
                _capture?.Dispose();
                _capture = null;
            }
            lock (_lastFrameLock)
            {
                _lastFrame?.Dispose();
                _lastFrame = null;
            }
        }

        // 시크 요청만 기록한다 (실제 시크는 재생 루프의 ApplyPendingSeek).
        // 드래그 중 요청이 여러 번 와도 마지막 값만 남으므로 UI 가 막히지 않는다.
        private void SeekToMs(double ms)
        {
            if (_capture == null || _mode != SourceMode.File) return;
            if (_atEof)
            {
                _atEof = false;   // 사용자가 위치를 옮겼으면 재생 시 그 위치부터 튼다
                // '재생 완료'도 되돌린다 — 예전엔 그 위치부터 재생하는 내내 남았다
                Txt_Status.Text = $"파일: {System.IO.Path.GetFileName(_filePath)}";
            }
            Interlocked.Exchange(ref _pendingSeekMs, Math.Max(0, ms));
        }

        // 슬라이더가 요청한 시크가 아직 적용되지 않았는지 (재생 루프·UI 어느 쪽에서 읽어도 된다)
        private bool SeekPending => !double.IsNaN(Volatile.Read(ref _pendingSeekMs));

        // [UI] 일시정지 중이면 지금 시크 위치의 프레임을 한 장 디코딩·검출해 보이게 한다 (파일 전용).
        // 예전엔 일시정지 중 시크하면 요청만 쌓이고 화면은 그대로라, 그 상태로 SAHI 를 누르면 슬라이더 위치가 아닌 옛 프레임을 분석했다
        private void RequestStepFrame()
        {
            if (_mode != SourceMode.File || _isPlaying || _capture == null) return;
            Interlocked.Exchange(ref _stepRequest, 1);
        }

        // [UI] 한 장 보기·시크로 얻는 프레임이 화면에 나올 때까지 기다린다 (SAHI 가 지금 위치의 프레임을 쓰게).
        // 길이 없는 webm 을 되감거나 VFR 을 멀리 정밀화하면 수 초 걸릴 수 있어 넉넉히 기다리고, 그래도 안 끝나거나 소스가 바뀌면 false.
        // (예전엔 1초 뒤 그냥 넘어가 옛 프레임을 분석했고, 늦게 나온 프레임과 SAHI 결과가 서로 덮었다)
        // 시크 대기도 본다: 재생 중이던 루프는 남은 한 장 보기 요청을 지운 뒤(루프 맨 위) 시크를 꺼낼 때에야 _stepping 을 세워서,
        // 그 사이에 보면 요청도 처리 중도 없는 것으로 보였다. ApplyPendingSeek 는 _stepping 을 세운 뒤 시크를 꺼내므로 SeekPending 을 먼저 읽는다.
        // 파일일 때만 본다 — 시크 직후 카메라로 바꾸면 꺼낼 루프가 없는 시크 요청이 남는다(파일을 열 때만 비운다)
        private async Task<bool> WaitForStepFrameAsync(int gen, int timeoutMs)
        {
            var sw = Stopwatch.StartNew();
            while ((_mode == SourceMode.File && SeekPending) || Volatile.Read(ref _stepRequest) == 1 || _stepping)
            {
                if (gen != _loadGeneration || sw.ElapsedMilliseconds > timeoutMs) return false;
                await Task.Delay(25);
            }
            return true;
        }

        // [재생 루프, _captureLock 안에서 호출] 대기 중인 시크 요청을 적용한다.
        // 메타데이터를 믿는 파일(CFR/mp4)은 PosFrames 사용(가장 안정적) — 메타데이터를 못 믿는 파일(VFR 경로)은 예외(_seekByGrab, 아래).
        // grab 경로는 목표 시각까지 따라잡도록 _seekRefineTargetMs 를 남긴다 (프레임 시크 쪽의 _vfr 정밀화는 지금은 _seekByGrab 과 같은
        // 조건이라 쓰이지 않지만, VFR 에 프레임 시크를 다시 쓰게 되더라도 키프레임 스냅을 따라잡도록 남겨 둔다).
        // 시크를 적용했으면 true (재생 시계를 새 위치에 다시 맞춰야 한다). _capture 를 새로 열어 바꿀 수 있다
        private bool ApplyPendingSeek()
        {
            var capture = _capture!;
            if (!SeekPending) return false;
            // 요청을 꺼내기 전에 '보일 프레임을 만드는 중'으로 표시한다. 꺼낸 뒤 세우면 그 틈에 SAHI 가 시크 대기도 처리 중도 없는 것으로 보고
            // 시크 전 프레임을 분석했다. 루프 맨 위에서 다시 내린다 (꺼내는 쪽은 이 루프뿐이라 아래 Exchange 는 NaN 이 아니다)
            _stepping = true;
            double ms = Interlocked.Exchange(ref _pendingSeekMs, double.NaN);
            if (double.IsNaN(ms)) return false;
            _lastSeekTargetMs = Math.Max(0, ms);   // 프레임 시크가 VFR 에서 엉뚱한 곳에 섰으면 grab 으로 다시 간다 (재생 루프의 VFR 알아보기)

            if (_seekByGrab)
            {
                // 길이 정보가 없는 파일(브라우저 MediaRecorder 로 녹화한 webm 이 대표적)은 FRAME_COUNT 가 음수 쓰레기값이고,
                // OpenCV 가 시크 목표를 그 값으로 잘라 스트림이 망가진다: Set(PosFrames) 는 true 를 돌려주지만
                // 이후 Read 가 곧바로 실패해 EOF 로 보인다 (2026-09-23 실측). 길이는 있어도 fps 가 실제 속도가 아닌 파일
                // (타임베이스 값 1000 등)은 OpenCV 가 프레임을 그 fps 단위로 세어 엉뚱한 곳에 선다 (OpenVideoFile 주석, 2026-10-31 실측).
                // 그래서 프레임 시크를 아예 쓰지 않는다 — 앞으로는 아래 정밀화(grab)로 따라가고, 뒤로는 파일을 다시 열어
                // 처음부터 따라간다 (길면 느리지만 정확. fps=1000 파일엔 옛 프레임 시크도 목표 시각의 3% 쯤 되는 앞쪽 키프레임부터
                // 프레임을 하나씩 디코딩했으므로 되감기 비용은 비슷하고, 앞으로 갈 땐 지금 위치부터라 오히려 덜 든다)
                if (ms < _lastPosMs && _filePath != null)
                {
                    // 다시 열지 못하면(파일이 옮겨졌거나 지워짐 등) 지금 캡처로 계속한다.
                    // 예외가 재생 루프 밖으로 나가면 재생이 영구히 죽으므로 여기서 받는다
                    VideoCapture? reopened = null;
                    try { reopened = new VideoCapture(_filePath); }
                    catch (Exception ex) { Debug.WriteLine($"시크용 다시 열기 실패: {ex.Message}"); }
                    if (reopened != null && reopened.IsOpened()) { capture.Dispose(); _capture = reopened; }
                    else reopened?.Dispose();
                }
                _seekRefineTargetMs = ms;
                return true;
            }

            int frame = _fps > 0 ? (int)Math.Round(ms / 1000.0 * _fps) : 0;
            if (_totalFrames > 0 && frame > _totalFrames - 1) frame = _totalFrames - 1;
            if (frame < 0) frame = 0;

            capture.Set(VideoCaptureProperties.PosFrames, frame);
            _currentFrameIdx = frame;
            _seekRefineTargetMs = _vfr ? ms : -1;
            return true;
        }

        // [재생 루프] 프레임을 보일 시각까지 기다린다. 취소되면 false.
        // VFR 화면녹화는 한 프레임이 수 초씩 이어질 수 있어, 그동안 들어온 시크·일시정지에 바로 반응하도록
        // 짧게 나눠 기다리고, 그런 요청이 오면 기다림을 끝낸다
        private async Task<bool> WaitUntilDueAsync(double waitMs, CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();
            while (true)
            {
                double left = waitMs - sw.Elapsed.TotalMilliseconds;
                // Task.Delay 는 1ms 미만을 0 으로 잘라 곧바로 끝나므로, 그대로 두면 남은 시간 동안 헛돈다 → 1ms 이내면 그냥 보인다
                if (left < 1) return true;
                if (SeekPending || !_playSignal.IsSet) return true;
                try { await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(left, 50)), ct); }
                catch (OperationCanceledException) { return false; }
            }
        }

        private void UpdateTimeText(double posMs)
        {
            // 1시간이 넘는 영상은 시를 붙인다 — mm 은 분 성분(0~59)뿐이라 65분이 "05:00"으로 보였다
            bool hours = Math.Max(_durationMs, posMs) >= 3_600_000;
            string curStr = FormatTime(Math.Max(0, posMs), hours);
            string totStr = _durationMs > 0 ? FormatTime(_durationMs, hours) : "--:--";
            Txt_Time.Text = $"{curStr} / {totStr}";
        }

        private static string FormatTime(double ms, bool hours)
        {
            var t = TimeSpan.FromMilliseconds(ms);
            return hours ? $"{(int)t.TotalHours}:{t:mm\\:ss}" : t.ToString(@"mm\:ss");
        }

        // ---------- 백그라운드 재생 루프 ----------
        private async Task PlaybackLoop(CancellationToken ct)
        {
            bool isCamera = _mode == SourceMode.Camera;
            var sw = new Stopwatch();
            int cameraFailures = 0;
            int inferFails = 0;            // 이어서 실패한 검출(ProcessFrame) 수 — InferFailLimit 에 닿으면 멈추고 알린다
            // 메타데이터를 믿은 파일(CFR 판정)의 프레임 시각 확인 — 앞 프레임의 PTS(시크·열기 뒤 -1)와, PTS 가 번호로 센 시각과 이어서 어긋난 프레임 수
            double cfrLastPts = -1;
            int vfrSuspect = 0;
            double inferFailFromMs = 0;    // 그 연속 실패가 시작된 프레임의 영상 시각 (파일)

            // 재생 시계 (파일 전용): 영상 시각(posMs)이 벽시계와 같은 속도로 흐르도록 "이 프레임이 보일 시각"까지 기다린다.
            // 예전엔 프레임마다 (1/fps − 처리 시간)만큼 기다렸는데, Task.Delay 가 15.6ms 단위로 올림돼 그 오차가
            // 매 프레임 쌓였다 → 30fps mp4 가 0.82배속. 게다가 fps 를 루프 시작 때 한 번만 읽어서, 분석 전 임시값
            // 30fps 로 시작하는 VFR(60fps 녹화 등)은 끝까지 30fps 로 돌아 0.41배속이었다 (2026-09-23 실측).
            // 절대 시각 기준이면 올림 오차는 다음 프레임에서 상쇄되고, VFR 도 실제 타임스탬프대로 재생된다
            var clock = Stopwatch.StartNew();
            double clockOriginMs = 0;
            double mediaOriginMs = double.NaN;   // NaN = 다음 프레임을 "지금" 보이는 것으로 시계를 다시 맞춘다

            // 디코딩 버퍼. 처리 후 _lastFrame(SAHI용)과 맞바꿔 다음 Read 에 재사용한다
            // → 매 프레임 복제(Clone)도, 새 버퍼 할당도 없다
            var frame = new Mat();

            // 추적기: 박스에 번호와 정지 여부를 붙인다. 이 루프에서만 부른다(락 없음). 소스를 열 때마다 이 루프가 새로 시작해 번호도 새로 센다.
            // 시크하면 앞뒤 화면이 이어지지 않으므로 다음에 표시할 프레임에서 비운다 — 뒤로 가는 시각은 추적기가 저절로 비우지만
            // 앞으로 가는 시크는 안 비운다(정지 트랙을 10초 붙들어, 시크 앞뒤의 다른 물체가 같은 번호를 받을 수 있다). 카메라는 멈췄다 재개해도 비운다
            var tracker = new Tracker();
            bool resetTracker = false;

            // 화면 전환 감지 (파일 전용 — SceneChange.cs, 2026-11-03): 보이는 프레임마다 작은 흑백 그림을 만들어 카메라가 돌거나 화면이 바뀌었는지 본다.
            // 움직이는 동안은 추적기에 넣지 않고(돌아가는 카메라의 박스로 가짜 트랙을 만들지 않게) 안정된 뒤 직전과 다른 화면이면 추적기를 비운다 — 번호가 새로 매겨진다.
            // 시크하면 앞뒤가 이어지지 않으니 감지기도 같이 비운다(resetTracker). 카메라는 쓰지 않는다: 웹캠은 가까이서 큰 동작만 해도 한 프레임이 크게 바뀌어
            // 번호가 깜빡일 수 있는데 이 PC 에선 실측할 수 없다 — PTZ 소스로 확인한 뒤 켠다
            using var sceneThumb = isCamera ? null : new SceneThumbnail();
            var scene = new SceneChangeDetector();
            double sceneNoticeUntilMs = double.NegativeInfinity;   // '화면 전환' 배지를 보일 영상 시각(파일)의 끝
            var badge = SceneBadgeKind.None;

            // 구역 진입·나감 이벤트 (파일 전용 — ZoneEvents.cs, 2026-11-03): 추적기를 거친 박스의 발 위치가 구역 안인지로 본다. 구역은 UI 스레드가 고치므로
            // 불변 스냅샷(_zoneShapes)을 읽고, 구역이 바뀌었다는 깃발(_zoneEventsReset)을 꺼내면 감지기를 비운다. 시크·화면 전환 뒤엔 앞뒤가 이어지지 않아 비우거나 멈춘다
            var zoneEvents = new ZoneEventDetector();
            // 유고 규칙 (파일 전용 — IncidentRules.cs, 2026-11-03): 차로에 멈춘 차량이 정지로 5초 이어지면 유고 후보(박스 강조 + 로그 행). 구역 이벤트와 같은 자리에서 비우고·멈춘다
            var incidents = new IncidentDetector();
            // 화면에 새 프레임을 내보내지 않고 선언된 유고 후보를 끝낸다 — 멈춘 채 구역을 고쳤을 때(편집은 멈춘 채일 때만 된다)와 시크한 곳에 프레임이 없을 때(파일 끝).
            // 예전엔 다음 프레임이 나올 때에야(재개·다음 시크) 끝내서, 그때까지 화면의 빨간 박스·헤더의 '⚠ 유고 후보 N'·풀리지 않은 로그 행이 남았고 그대로 [CSV 저장]하면
            // 해소 행 없이 저장됐다(2026-11-25 Gemini 리뷰 6-3). 감지기는 이 루프만 쓰므로 여기(루프)서 비우고 표시만 UI 로 보낸다
            async Task EndIncidentsOffFrameAsync(string reason)
            {
                var ends = incidents.Reset(reason);
                if (ends.Count == 0) return;
                await Dispatcher.InvokeAsync(() =>
                {
                    if (ct.IsCancellationRequested) return;
                    AddZoneEvents(ends);
                    ShowIncidentCount(0);
                    FrameBoxes.ClearIncidents();   // 박스는 그대로, 빨간 강조·라벨의 '유고?'만 걷는다
                });
            }

            // 화면 식별 (파일 전용 — ViewIdentity.cs, 2026-11-03 2b): 안정된 화면이 어느 뷰인지 ORB 특징점으로 알아본다 — 영상을 연 직후·시크 뒤·화면 전환 뒤에.
            // 구역은 그린 뷰의 것이라 이벤트·유고 감지는 지금 뷰의 구역(viewZones)만 쓴다. 알아보지 못하면(애매·특징 부족) 구역 없이 1초 → 2초 → 4초마다 다시 본다.
            // 추적기·구역 이벤트·유고 상태는 뷰를 다시 만나도 이어 붙이지 않고 방문마다 새로 센다.
            // 자세 지켜보기 (CameraPoseWatch.cs, 2026-11-21): 뷰를 알아본 뒤 1초마다 ORB 로 카메라 자세를 본다. 화면 전환 감지(밝기 차이)가 못 잡는 천천히 하는 줌·팬으로
            // 뷰의 허용 범위를 벗어나면(test6 1416초 줌아웃) 화면 전환처럼 처리해 뷰를 다시 알아본다 — 예전엔 그 전 뷰의 구역이 어긋난 채 남았다. 움직이는 증거가 있는 동안은
            // 화면 전환 감지의 움직임처럼 추적기에 넣지 않는다. 언제 식별하고 무엇을 배우고 버리는지는 ViewSession 한곳에 있다(실험 도구 TrackerCheck 도 같은 것을 쓴다)
            var views = isCamera ? null : _viewRegistry;
            using var session = views != null ? new ViewSession(views) : null;
            string badgeText = "";                              // 화면 전환 배지의 뷰 글자
            ZoneShape[] viewZones = [];
            ZoneShape[]? viewZonesFrom = null;
            int viewZonesView = -1;
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    // 멈췄다 재개하면 멈춘 동안 흐른 벽시계 시간만큼 늦은 것으로 보여 몰아서 재생하게 되므로 다시 맞춘다
                    if (!_playSignal.IsSet) { mediaOriginMs = double.NaN; if (isCamera) resetTracker = true; }
                    // 일시정지 중엔 50ms 마다 깨어 '한 장 보기' 요청을 본다 (파일). 재생 중이면 남은 요청은 버린다 — 재생이 새 위치를 보인다
                    _stepping = false;
                    bool step = false;
                    try
                    {
                        while (!_playSignal.Wait(50, ct))
                        {
                            if (isCamera) continue;
                            // 멈춘 채 구역을 고쳤다 — 재개를 기다리지 않고 감지기를 비우고 선언된 유고 후보를 끝낸다 (EndIncidentsOffFrameAsync).
                            // 한 장 보기보다 먼저 본다: 같이 와도 해소 사유가 '시크'가 아니라 '구역 변경'으로 남는다
                            if (Interlocked.Exchange(ref _zoneEventsReset, 0) == 1) { zoneEvents.Reset(); await EndIncidentsOffFrameAsync("구역 변경"); }
                            if (Volatile.Read(ref _stepRequest) == 1)
                            {
                                // 처리 중 표시를 요청을 꺼내기 전에 세운다 — 꺼낸 뒤 세우면 그 틈에 SAHI 가 요청도 처리 중도 없는 것으로 보고
                                // 한 장 보기 전의 프레임을 집었다 (꺼내는 쪽은 이 루프뿐이라 읽은 1 이 그대로 꺼내진다)
                                _stepping = true;
                                Interlocked.Exchange(ref _stepRequest, 0);
                                step = true;
                                break;
                            }
                        }
                    }
                    catch (OperationCanceledException) { break; }
                    if (ct.IsCancellationRequested) break;
                    if (!step) Interlocked.Exchange(ref _stepRequest, 0);

                    sw.Restart();

                    double posMs = 0;
                    int currentFrameIdx = 0;
                    bool ended = false;
                    bool captureGone = false;
                    bool seeked = false;   // 이 프레임은 시크로 얻은 것 — 멈췄어도 들고 있지 않고 보인다 (아래)
                    bool vfrFound = false;   // 메타데이터를 믿은 파일이 이 프레임에서 VFR 로 드러났다
                    bool reseek = false;     // 그게 시크 직후라 grab 으로 다시 시크한다

                    // ★ lock 안에서는 await 금지 → 결과만 플래그로 받음
                    lock (_captureLock)
                    {
                        if (_capture == null)
                        {
                            captureGone = true;
                        }
                        else
                        {
                            if (!isCamera && ApplyPendingSeek()) { mediaOriginMs = double.NaN; seeked = true; resetTracker = true; }   // _capture 가 바뀔 수 있음
                            // 한 장 보기인데 꺼낼 시크가 없으면 앞 반복이 이미 꺼내 보인 시크에 남은 요청이다 (요청은 시크를 걸 때만 한다 —
                            // 루프 맨 위에서 요청을 지운 뒤 시크를 꺼내기 전에 일시정지·SAHI·멈춘 채 시크가 들어오면 남는다).
                            // 그대로 읽으면 시크 위치 다음 프레임으로 한 장 더 나가 멈춘다(VFR 녹화면 수 초 뒤) → 읽지 않고 위로 돌아간다
                            else if (step) continue;

                            bool ok = _capture.Read(frame) && !frame.Empty();
                            // 파일: 시킹 직후 일시적 디코드 실패를 한 번 더 시도로 흡수
                            if (!ok && !isCamera)
                                ok = _capture.Read(frame) && !frame.Empty();

                            if (!ok)
                                ended = true;
                            else if (!isCamera)
                            {
                                if (_vfr)
                                {
                                    posMs = _capture.Get(VideoCaptureProperties.PosMsec);  // VFR: 실제 타임스탬프

                                    // seek 정밀화: 키프레임 스냅 지점에서 목표 시각까지
                                    // 디코딩 없이 grab()으로 따라잡은 뒤 마지막 프레임만 디코딩
                                    double target = _seekRefineTargetMs;
                                    if (target >= 0)
                                    {
                                        bool grabbed = false;
                                        int stalled = 0;
                                        while (posMs + 1.0 < target)
                                        {
                                            // 멀리 가는 grab 은 수 초 걸릴 수 있다 (메타데이터를 못 믿는 파일(_seekByGrab)은 뒤로 갈 때 처음부터 따라간다).
                                            // 그 사이 새 시크(슬라이더를 끄는 중)가 오면 여기서 멈추고 다음 반복에서 새 목표로 간다 —
                                            // 옛 목표까지 다 따라가지 않도록. 정지·종료도 마찬가지 (StopPlaybackAsync 가 끝까지 기다리게 된다)
                                            if (SeekPending || ct.IsCancellationRequested) break;
                                            if (!_capture.Grab()) break;
                                            grabbed = true;
                                            double next = _capture.Get(VideoCaptureProperties.PosMsec);
                                            // 시각이 늘지 않는 스트림(타임스탬프 없음 등)에서 끝없이 돌지 않게 한다. 횟수로 자르면
                                            // (예전 20만 회) 긴 파일(60fps 55분 이상)에서 처음부터 따라가는 시크가 중간에 멈춰 엉뚱한 곳에 선다
                                            stalled = next > posMs ? 0 : stalled + 1;
                                            posMs = next;
                                            if (stalled > 1000) break;
                                        }
                                        // grab 을 안 했으면 frame 은 이미 Read 가 디코딩한 그 프레임이다 (다시 변환할 필요 없음)
                                        if (grabbed) _capture.Retrieve(frame);
                                        _seekRefineTargetMs = -1;
                                    }
                                    _lastPosMs = posMs;
                                }
                                else
                                {
                                    // 메타데이터를 믿은 파일(CFR 판정): 위치는 프레임의 실제 표시 시각(PTS — 앞 프레임보다 늘었을 때만 믿는다, 아니면 번호로 센 시각).
                                    // 고정 간격 파일은 둘이 같다(2026-11-28 실측: test1~6 · 사고 영상 6개 모두 어긋남 0 ms). 평균 fps·프레임 수는 정상인데 간격이
                                    // 일정하지 않은 파일(VFR — 휴대폰·NVR 녹화, 정지 구간이 드문 화면 녹화)은 번호로 센 시각이 실제와 몇 초씩 어긋나
                                    // 이벤트·CSV 시각이 틀렸고, 프레임 시크도 엉뚱한 곳에 섰다(OpenCV 는 프레임을 fps 간격으로 센다 — docs/playback.md).
                                    // 그래서 PTS 가 번호로 센 시각과 이어서(시크 직후는 한 번) 어긋나면 VFR 로 보고 메타데이터를 못 믿는 파일의 경로로 넘긴다:
                                    // 위치는 PTS(_vfr), 시크는 grab(_seekByGrab), 길이는 실측으로 다시 잰다(OnVfrDetectedAsync) — 2026-11-28 ChatGPT 리뷰 5번
                                    if (seeked) cfrLastPts = -1;
                                    currentFrameIdx = _currentFrameIdx++;
                                    double idxMs = currentFrameIdx / _fps * 1000.0;
                                    double pts = _capture.Get(VideoCaptureProperties.PosMsec);
                                    bool ptsOk = pts >= 0 && pts > cfrLastPts;
                                    posMs = ptsOk ? pts : idxMs;
                                    if (ptsOk) cfrLastPts = pts;
                                    vfrSuspect = ptsOk && Math.Abs(pts - idxMs) > Math.Max(100.0, 2500.0 / _fps) ? vfrSuspect + 1 : 0;
                                    if (vfrSuspect >= (seeked ? 1 : 3))
                                    {
                                        vfrSuspect = 0;
                                        _vfr = true;
                                        _seekByGrab = true;
                                        _lastPosMs = posMs;
                                        vfrFound = true;
                                        if (seeked)
                                        {
                                            // 프레임 시크가 선 곳은 목표가 아니다 — grab 경로로 목표에 다시 간다 (멈춘 채 시크였으면 한 장 보기도 다시)
                                            Interlocked.Exchange(ref _pendingSeekMs, _lastSeekTargetMs);
                                            if (step) Interlocked.Exchange(ref _stepRequest, 1);
                                            reseek = true;
                                        }
                                    }
                                }
                            }
                        }
                    }

                    if (captureGone) break;
                    if (vfrFound)
                    {
                        vfrFound = false;
                        int vfrGen = Volatile.Read(ref _loadGeneration);
                        _ = Dispatcher.InvokeAsync(() => _ = OnVfrDetectedAsync(vfrGen));
                    }
                    if (reseek) continue;   // 이 프레임은 버리고 다음 반복이 grab 으로 시크한다

                    if (ended)
                    {
                        if (isCamera)
                        {
                            // 카메라는 일시적으로 빈 프레임이 올 수 있음 → 잠시 후 재시도
                            if (++cameraFailures > 60)
                            {
                                cameraFailures = 0;
                                // 루프는 유지하고 일시정지 상태로 전환 (복구 가능)
                                await Dispatcher.InvokeAsync(OnCameraLost);
                            }
                            else
                            {
                                try { await Task.Delay(10, ct); }
                                catch (OperationCanceledException) { break; }
                            }
                            continue;
                        }

                        // 파일 EOF: 루프를 끝내지 않고 일시정지 상태로 전환
                        // → 슬라이더로 seek 후 재생 버튼으로 재개 가능.
                        // 시크한 곳에 프레임이 없었으면(끝으로 시크) 화면엔 시크 전 프레임이 남는다 — 추적기·감지기는 다음에 나올 프레임에서 비우되(resetTracker 가 남아 있다)
                        // 선언된 유고 후보는 지금 끝낸다: 예전엔 '재생 완료' 뒤에도 빨간 박스·헤더 표시·열린 로그 행이 남았다 (끝까지 재생해서 닿은 EOF 는 그대로 둔다 — 마지막 프레임의 상태다)
                        if (resetTracker) await EndIncidentsOffFrameAsync("시크");
                        await Dispatcher.InvokeAsync(OnVideoEnded);
                        continue;
                    }
                    cameraFailures = 0;

                    int width = frame.Width;
                    int height = frame.Height;

                    BitmapSource? resultImage;
                    List<Det> dets;
                    try
                    {
                        if (!isCamera) TestFaults.FileDetect(posMs);   // 시험용 검출 예외 (Debug 빌드 + DETECTVIEW_TEST_FAULTS=file-detect-once · file-detect-fail 일 때만)
                        resultImage = ProcessFrame(frame, out dets);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"프레임 처리 오류: {ex.Message}");
                        if (ct.IsCancellationRequested) break;   // 창을 닫으며 추론 세션을 내린 것 — 실패로 세지 않는다
                        // 한두 프레임은 건너뛰고 다음 프레임을 보인다. 이어서 실패하면(장치 제거·드라이버 오류 등) 멈추고 상태바에 위치·사유를 띄운다 —
                        // 예전엔 디버그 출력만 남기고 표시·기다림 없이 다음 프레임으로 넘어가, 마지막으로 그린 화면이 남은 채 파일 끝까지 내달려
                        // '재생 완료'로 끝났다(2026-11-28 ChatGPT 리뷰 6번). 파일은 실패가 시작된 곳으로 되돌려 둔다 — 다시 재생하면 그 구간부터 분석한다
                        if (inferFails++ == 0) inferFailFromMs = posMs;
                        if (inferFails >= InferFailLimit)
                        {
                            inferFails = 0;
                            double at = inferFailFromMs;
                            string why = ex.Message;
                            await Dispatcher.InvokeAsync(() => OnAnalysisStopped(at, why));
                        }
                        continue;
                    }
                    inferFails = 0;
                    if (ct.IsCancellationRequested) break;

                    var procElapsed = sw.Elapsed;
                    double instFps = procElapsed.TotalSeconds > 0 ? 1.0 / procElapsed.TotalSeconds : 0;
                    _smoothedFps = _smoothedFps <= 0 ? instFps : _smoothedFps * 0.85 + instFps * 0.15;
                    double fpsToShow = _smoothedFps;

                    // 파일은 원본 속도로 재생 (이 프레임이 보일 시각까지 기다렸다 표시), 카메라는 가능한 빠르게(지연 최소화).
                    // 한 장 보기(일시정지 중 시크)는 기다리지도, 들고 있지도 않고 곧바로 보인다
                    if (!isCamera && !step)
                    {
                        double nowMs = clock.Elapsed.TotalMilliseconds;
                        if (double.IsNaN(mediaOriginMs) || posMs < mediaOriginMs)
                        {
                            clockOriginMs = nowMs;
                            mediaOriginMs = posMs;
                        }
                        double waitMs = (posMs - mediaOriginMs) - (nowMs - clockOriginMs);
                        if (waitMs < -250)
                        {
                            // 처리가 영상보다 느려 많이 밀렸다: 따라잡으려 몰아서 재생하지 말고 지금을 새 기준으로 삼는다
                            clockOriginMs = nowMs;
                            mediaOriginMs = posMs;
                        }
                        else if (waitMs > 0 && !await WaitUntilDueAsync(waitMs, ct)) break;

                        // 기다리는 사이(또는 처리 중) 일시정지·SAHI 로 멈췄으면 이 프레임은 아직 화면에 나갈 때가 아니다.
                        // 지금 보이면 멈춘 화면이 다음 프레임(VFR 녹화는 수 초 뒤일 수 있음)으로 넘어가고, SAHI 는 화면에 없던
                        // 프레임을 분석한다 → 재개될 때까지 들고 있다가 보인다 (_lastFrame 도 아직 바꾸지 않았다).
                        // 단 시크로 얻은 프레임은 들고 있지 않는다 — 사용자가 옮긴 위치라 멈췄어도 보여야 하고, SAHI 도 이걸 기다린다
                        // (예전엔 재생 중 시크를 처리하는 사이 멈추면 화면은 시크 전 프레임에 남고 SAHI 는 시크 전 프레임을 분석했다)
                        if (!_playSignal.IsSet && !seeked)
                        {
                            // 들고 있는 동안에도 한 장 보기(멈춘 채 시크) 요청을 본다 — 그냥 Wait(ct) 하면 SAHI 로 멈춘 뒤의 시크가 화면에 안 나온다
                            try
                            {
                                while (!_playSignal.Wait(50, ct))
                                {
                                    // 프레임을 든 채 멈춘 사이 구역을 고쳤으면 여기서도 곧바로 (루프 맨 위의 대기와 같다)
                                    if (Interlocked.Exchange(ref _zoneEventsReset, 0) == 1) { zoneEvents.Reset(); await EndIncidentsOffFrameAsync("구역 변경"); }
                                    if (Volatile.Read(ref _stepRequest) == 1) break;
                                }
                            }
                            catch (OperationCanceledException) { break; }
                            if (ct.IsCancellationRequested) break;
                            // 멈춘 사이 위치를 옮겼다 → 옛 위치의 이 프레임은 버린다 (한 장 보기는 위로 돌아가 새 위치에서 읽는다)
                            if (SeekPending || !_playSignal.IsSet) continue;
                            clockOriginMs = clock.Elapsed.TotalMilliseconds;
                            mediaOriginMs = posMs;
                        }
                    }

                    // 화면에 내보낼 프레임의 작은 흑백 그림 — 아래에서 buffer 를 맞바꾸기 전에 만든다 (맞바꾼 뒤 frame 은 옛 버퍼)
                    byte[]? sceneThumbBytes = sceneThumb?.Compute(frame);

                    // SAHI 정밀 분석에 쓸 원본 프레임 보관 (ProcessFrame 은 frame 에 그리지 않으므로 깨끗함).
                    // 화면에 내보내는 프레임과 같도록 기다린 뒤에 바꾼다. 복제 대신 버퍼를 맞바꾸고, 이전 보관 버퍼는 다음 Read 에 재사용
                    lock (_lastFrameLock)
                    {
                        (_lastFrame, frame) = (frame, _lastFrame ?? new Mat());
                        _lastFramePosMs = posMs;
                    }

                    // 화면에 내보낼 프레임에만 추적기를 돌린다 — 위에서 버린 프레임(일시정지 중 옮긴 위치의 옛 프레임)을 넣으면 시각이 어긋난다.
                    // 시각: 파일은 영상 시각(VFR 도 실제 타임스탬프), 카메라는 이 루프의 벽시계. 추적기가 실패해도 재생은 이어간다(박스는 번호 없이)
                    List<ZoneEvent>? frameEvents = null;
                    List<VlmJob>? vlmJobs = null;   // VLM 설명이 켜져 있으면 이 프레임의 유고 후보 그림 (MainWindow.Vlm.cs)
                    if (resetTracker)
                    {
                        tracker.Reset();
                        scene.Reset();
                        zoneEvents.Reset();   // 시크는 사용자가 지금 화면에 구역이 맞다고 보는 것 — 멈췄던 이벤트도 다시 시작
                        var seekEnds = incidents.Reset("시크");   // 선언돼 있던 유고 후보는 끝내는 행으로 (로그에 열린 채 남지 않게)
                        if (seekEnds.Count > 0) (frameEvents ??= []).AddRange(seekEnds);
                        sceneNoticeUntilMs = double.NegativeInfinity;
                        badge = SceneBadgeKind.None;
                        resetTracker = false;
                        session?.Reset();   // 앞뒤가 이어지지 않는다 — 시크한 위치가 어느 뷰인지 다시 보고 그 뷰에서 새로 지켜본다
                    }
                    if (Interlocked.Exchange(ref _zoneEventsReset, 0) == 1)   // 구역을 읽었거나 고쳤다
                    {
                        zoneEvents.Reset();
                        var editEnds = incidents.Reset("구역 변경");
                        if (editEnds.Count > 0) (frameEvents ??= []).AddRange(editEnds);
                    }
                    bool feedTracker = true;
                    if (sceneThumbBytes != null)
                    {
                        var sc = scene.Update(sceneThumbBytes, posMs);
                        // 화면 식별 · 자세 지켜보기 (ViewSession) — 방금 내보낼 프레임은 위 맞바꾸기로 _lastFrame 에 있다 (이 스레드만 쓰는 값)
                        var vs = default(ViewStep);
                        if (session != null)
                        {
                            Mat current;
                            lock (_lastFrameLock) current = _lastFrame!;
                            try { vs = session.Update(current, posMs, sc); }
                            catch (Exception ex) { Debug.WriteLine($"뷰 처리 오류: {ex.Message}"); }
                            if (vs.Note != null) Debug.WriteLine($"뷰 {posMs / 1000:0.00}초: {vs.Note}");
                        }
                        // 자세 지켜보기가 움직이는 중이어도 이 프레임에서 화면 전환 감지가 전환을 알렸으면 그쪽을 처리한다 — 전환은 이 한 프레임에만 오는 알림이라
                        // 움직임 쪽으로 빠지면 사라진다(지켜보기는 ViewSession 이 멈추고 식별 뒤 다시 시작한다)
                        if (sc.Unsettled || (vs.Moving && !sc.ViewChanged))
                        {
                            feedTracker = false;   // 움직이는 중 — 박스는 그대로 보이되 번호는 안 붙인다
                            badge = sc.Unsettled && sc.Blanked ? SceneBadgeKind.Blank : SceneBadgeKind.Moving;
                        }
                        else
                        {
                            bool changedNow = sc.ViewChanged || vs.Changed;
                            if (changedNow)
                            {
                                tracker.Reset();   // 직전과 다른 화면 — 이전 트랙은 이어지지 않는다 (번호는 이어서 센다)
                                sceneNoticeUntilMs = posMs + SceneNoticeMs;
                                // 구역은 그 구역을 그린 뷰의 것 — 어느 뷰인지 알아볼 때까지(ViewSession) 이벤트를 멈추고 선언된 유고 후보를 끝낸다
                                var marker = zoneEvents.Suspend(posMs);
                                var viewEnds = incidents.Suspend();
                                if (viewEnds.Count > 0) (frameEvents ??= []).AddRange(viewEnds);
                                badgeText = "";
                                if (session == null && marker != null && _zoneShapes.Length > 0) (frameEvents ??= []).Add(marker);   // 식별기가 없으면 예전처럼 멈춘 채 둔다
                            }
                            if (vs.Identified is { } vid)
                            {
                                if (vid.Outcome == ViewOutcome.Unknown)
                                {
                                    if (changedNow && _zoneShapes.Length > 0)
                                        (frameEvents ??= []).Add(new ZoneEvent(ZoneEventKind.ViewChanged, posMs, 0, "", 0, default));   // 뷰를 알 수 없어 이벤트를 멈췄다는 표시
                                }
                                else
                                {
                                    int viewId = vid.ViewId;
                                    if (vs.Announce)
                                    {
                                        sceneNoticeUntilMs = posMs + SceneNoticeMs;
                                        bool isNew = vid.Outcome == ViewOutcome.New;
                                        int zonesHere = _zoneShapes.Count(z => z.ViewId == viewId);
                                        badgeText = isNew ? $"새 뷰 V{viewId} · 구역 없음" : $"뷰 V{viewId} · 아는 화면";
                                        if (_zoneShapes.Length > 0)   // 구역이 있는 영상의 로그에만 (구역이 없는 영상의 로그를 채우지 않는다)
                                        {
                                            string note = isNew ? $"새 뷰 V{viewId} (구역 없음)" : zonesHere > 0 ? $"뷰 V{viewId} · 구역 {zonesHere}개" : $"뷰 V{viewId} (구역 없음)";
                                            (frameEvents ??= []).Add(new ZoneEvent(ZoneEventKind.ViewChanged, posMs, 0, "", 0, default, Note: note));
                                        }
                                    }
                                    zoneEvents.Reset();   // 멈춘 상태를 풀고 이 뷰의 구역으로 시작한다 (이미 안에 있는 물체는 '이미 안에 있음')
                                    incidents.Reset("화면 전환");
                                }
                            }
                            // 이 프레임의 구역 — 지금 뷰의 것만 (구역 스냅샷이나 뷰가 바뀔 때만 다시 거른다). 식별기가 없으면 전체
                            int viewNow = session?.ViewNow ?? 0;
                            var shapes = _zoneShapes;
                            if (!ReferenceEquals(shapes, viewZonesFrom) || viewNow != viewZonesView)
                            {
                                viewZones = session == null ? shapes : viewNow > 0 ? shapes.Where(z => z.ViewId == viewNow).ToArray() : [];
                                viewZonesFrom = shapes;
                                viewZonesView = viewNow;
                            }
                            badge = session != null && viewNow == 0 ? SceneBadgeKind.UnknownView
                                  : posMs < sceneNoticeUntilMs ? SceneBadgeKind.Changed : SceneBadgeKind.None;
                        }
                    }
                    if (feedTracker)
                    {
                        try { dets = tracker.UpdateTagged(dets, isCamera ? clock.Elapsed.TotalMilliseconds : posMs, width, height); }
                        catch (Exception ex) { Debug.WriteLine($"추적기 오류: {ex.Message}"); tracker.Reset(); }

                        if (!isCamera)
                        {
                            try
                            {
                                var ze = zoneEvents.Update(dets, viewZones, posMs, width, height);
                                if (ze.Count > 0) (frameEvents ??= []).AddRange(ze);
                                var ie = incidents.Update(dets, viewZones, posMs, width, height);
                                if (ie.Count > 0) (frameEvents ??= []).AddRange(ie);
                                // VLM 설명: 유고 후보가 나온 이 프레임의 그림 영역만 떼어 둔다 — 행을 넣는 디스패치 뒤에 큐에 넣고 기다리지 않는다
                                if (ie.Count > 0 && _vlmOn) vlmJobs = PrepareVlmJobs(ie, dets);
                                // 유고 후보로 선언된 차량의 박스를 강조한다 (BoxLayer 가 빨갛게 그리고 라벨에 유고? 가 붙는다)
                                if (incidents.ActiveCount > 0)
                                    dets = dets.Select(d => d.TrackId > 0 && incidents.IsActive(d.TrackId) ? d with { Incident = true } : d).ToList();
                            }
                            catch (Exception ex) { Debug.WriteLine($"구역 이벤트 오류: {ex.Message}"); zoneEvents.Reset(); incidents.Reset("오류"); }
                        }
                    }
                    var badgeNow = badge;
                    string badgeTextNow = badgeText;
                    int viewShow = session?.ViewNow ?? 0;
                    int incidentNow = incidents.ActiveCount;

                    await Dispatcher.InvokeAsync(() =>
                    {
                        if (ct.IsCancellationRequested) return;

                        MainImage.Source = resultImage;
                        FrameBoxes.Show(dets, width, height);   // 영상과 같은 디스패치에서 — 박스가 다른 프레임 위에 남지 않게
                        ShowSceneBadge(badgeNow, badgeTextNow);
                        if (views != null) ShowCurrentView(viewShow);   // 구역 패널·그림이 이 뷰의 구역으로 — 프레임과 같은 디스패치에서
                        if (frameEvents != null) AddZoneEvents(frameEvents, vlmJobs);   // 이벤트 로그 — 이 프레임과 같은 디스패치에서 (VLM 에 보낼 행은 '분석 중')
                        ShowIncidentCount(incidentNow);

                        // 시크가 대기 중이면 이 프레임은 이전 위치의 것 → 진행 표시를 되돌리지 않는다
                        if (_mode == SourceMode.File && !_isUserDraggingSlider && !SeekPending)
                        {
                            if (_durationMs > 0)
                            {
                                _isPlaybackUpdatingSlider = true;
                                VideoSlider.Value = Math.Min(posMs, VideoSlider.Maximum);
                                _isPlaybackUpdatingSlider = false;
                            }
                            UpdateTimeText(posMs);
                        }

                        Txt_Resolution.Text = $"{width}×{height}";
                        Txt_InferenceFps.Text = $"{fpsToShow:F1} FPS";
                        Txt_DetectionCount.Text = $"검출 {dets.Count}";
                    });

                    // VLM 일은 행이 생긴 뒤에 큐에 넣는다 (결과가 와도 붙일 행이 없는 일이 없게). 소스를 내리는 중이면 버린다
                    if (vlmJobs != null)
                    {
                        if (ct.IsCancellationRequested) foreach (var job in vlmJobs) job.Dispose();
                        else EnqueueVlmJobs(vlmJobs);
                    }
                }
            }
            finally
            {
                frame.Dispose();
                _stepping = false;   // 처리 중에 루프가 끝났으면(정지·종료) 기다리던 SAHI 가 묶이지 않게
            }
        }

        private void OnVideoEnded()
        {
            _playSignal.Reset();
            _isPlaying = false;
            _atEof = true;
            Btn_PlayPause.Content = "재생";
            // 표시만 끝으로 옮긴다 (가드가 없으면 ValueChanged 가 불필요한 시크를 요청함)
            _isPlaybackUpdatingSlider = true;
            VideoSlider.Value = VideoSlider.Maximum;
            _isPlaybackUpdatingSlider = false;
            Txt_Status.Text = "재생 완료";
            UpdateRoiUi();   // 끝까지 재생했으면 멈춘 것 — 구역을 편집할 수 있다
        }

        // 검출이 이만큼 이어서 실패하면 재생을 멈추고 알린다 (30fps 로 0.33초) — 한두 번의 실패는 그 프레임만 건너뛴다
        private const int InferFailLimit = 10;
        // 검출이 이어서 실패해 멈춘 상태 (UI 스레드 전용) — 다시 재생할 때 상태바의 알림을 거둔다(카메라는 멈추기 전 문구로 되돌린다)
        private bool _analysisStopped;
        private string _statusBeforeAnalysisStop = "";

        // [UI] 재생 루프가 검출이 계속 실패해 멈춰 달라고 했다. 파일은 실패가 시작된 위치로 되돌리고(멈춘 채 시크 — 그 위치 한 장 보기)
        // 상태바에 그 위치와 사유를 띄운다. 다시 재생하면 그 구간부터 분석한다 — 그사이 장치를 바꾸거나(추론 장치) 일시적인 실패였다면 이어 간다
        private void OnAnalysisStopped(double atMs, string why)
        {
            if (_mode is not (SourceMode.File or SourceMode.Camera)) return;
            _playSignal.Reset();
            _isPlaying = false;
            if (!_analysisStopped) _statusBeforeAnalysisStop = Txt_Status.Text;
            _analysisStopped = true;
            Btn_PlayPause.Content = "재생";
            string shortWhy = why.Length > 160 ? why[..160] + "…" : why;
            if (_mode == SourceMode.File)
            {
                Txt_Status.Text = $"분석 중단 — {TimeSpan.FromMilliseconds(Math.Max(0, atMs)):mm\\:ss\\.f} 부터 검출이 계속 실패했습니다: {shortWhy}";
                _isPlaybackUpdatingSlider = true;
                VideoSlider.Value = Math.Min(Math.Max(0, atMs), VideoSlider.Maximum);
                _isPlaybackUpdatingSlider = false;
                UpdateTimeText(atMs);
                SeekToMs(atMs);
                RequestStepFrame();
            }
            else Txt_Status.Text = $"분석 중단 — 검출이 계속 실패했습니다: {shortWhy}";
            Txt_Status.ToolTip = why;   // 잘린 사유의 전문 (UIA HelpText)
            UpdateRoiUi();   // 멈췄다 — 파일이면 구역을 편집할 수 있다
        }

        private void OnCameraLost()
        {
            _playSignal.Reset();
            _isPlaying = false;
            Btn_PlayPause.Content = "재생";
            Txt_Status.Text = "카메라 연결이 끊어졌습니다";
        }
    }
}
