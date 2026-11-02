using OpenCvSharp;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace YOLO11_Test
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
                            if (!isCamera && Volatile.Read(ref _stepRequest) == 1)
                            {
                                // 처리 중 표시를 요청을 꺼내기 전에 세운다 — 꺼낸 뒤 세우면 그 틈에 SAHI 가 요청도 처리 중도 없는 것으로 보고
                                // 한 장 보기 전의 프레임을 집었다 (꺼내는 쪽은 이 루프뿐이라 읽은 1 이 그대로 꺼내진다)
                                _stepping = true;
                                Interlocked.Exchange(ref _stepRequest, 0);
                                step = true;
                                break;
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
                                    currentFrameIdx = _currentFrameIdx++;  // CFR: 수동 카운터(신뢰)
                            }
                        }
                    }

                    if (captureGone) break;

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
                        // → 슬라이더로 seek 후 재생 버튼으로 재개 가능
                        await Dispatcher.InvokeAsync(OnVideoEnded);
                        continue;
                    }
                    cameraFailures = 0;

                    if (!isCamera && !_vfr) posMs = currentFrameIdx / _fps * 1000.0;

                    int width = frame.Width;
                    int height = frame.Height;

                    BitmapSource? resultImage;
                    List<Det> dets;
                    try
                    {
                        resultImage = ProcessFrame(frame, out dets);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"프레임 처리 오류: {ex.Message}");
                        continue;
                    }
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
                            try { while (!_playSignal.Wait(50, ct)) if (Volatile.Read(ref _stepRequest) == 1) break; }
                            catch (OperationCanceledException) { break; }
                            if (ct.IsCancellationRequested) break;
                            // 멈춘 사이 위치를 옮겼다 → 옛 위치의 이 프레임은 버린다 (한 장 보기는 위로 돌아가 새 위치에서 읽는다)
                            if (SeekPending || !_playSignal.IsSet) continue;
                            clockOriginMs = clock.Elapsed.TotalMilliseconds;
                            mediaOriginMs = posMs;
                        }
                    }

                    // SAHI 정밀 분석에 쓸 원본 프레임 보관 (ProcessFrame 은 frame 에 그리지 않으므로 깨끗함).
                    // 화면에 내보내는 프레임과 같도록 기다린 뒤에 바꾼다. 복제 대신 버퍼를 맞바꾸고, 이전 보관 버퍼는 다음 Read 에 재사용
                    lock (_lastFrameLock)
                    {
                        (_lastFrame, frame) = (frame, _lastFrame ?? new Mat());
                        _lastFramePosMs = posMs;
                    }

                    // 화면에 내보낼 프레임에만 추적기를 돌린다 — 위에서 버린 프레임(일시정지 중 옮긴 위치의 옛 프레임)을 넣으면 시각이 어긋난다.
                    // 시각: 파일은 영상 시각(VFR 도 실제 타임스탬프), 카메라는 이 루프의 벽시계. 추적기가 실패해도 재생은 이어간다(박스는 번호 없이)
                    if (resetTracker) { tracker.Reset(); resetTracker = false; }
                    try { dets = tracker.UpdateTagged(dets, isCamera ? clock.Elapsed.TotalMilliseconds : posMs, width, height); }
                    catch (Exception ex) { Debug.WriteLine($"추적기 오류: {ex.Message}"); tracker.Reset(); }

                    await Dispatcher.InvokeAsync(() =>
                    {
                        if (ct.IsCancellationRequested) return;

                        MainImage.Source = resultImage;
                        FrameBoxes.Show(dets, width, height);   // 영상과 같은 디스패치에서 — 박스가 다른 프레임 위에 남지 않게

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
