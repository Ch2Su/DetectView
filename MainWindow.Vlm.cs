using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;

namespace DetectView
{
    /// <summary>VLM 에 보낼 일 한 건 — 유고 후보 이벤트(로그 행을 찾는 열쇠 — 참조로 비교)와 그 순간의 그림 영역</summary>
    internal sealed class VlmJob(ZoneEvent ev, VlmRegion region, int epoch) : IDisposable
    {
        public ZoneEvent Event { get; } = ev;
        public VlmRegion Region { get; } = region;

        /// <summary>일을 만든 때의 <c>_vlmEpoch</c> — 그 사이 끄거나 비웠으면 버린다</summary>
        public int Epoch { get; } = epoch;

        public void Dispose() => Region.Dispose();
    }

    /// <summary>
    /// VLM 설명 (2026-11-07 — docs/vlm-annotation.md): 유고 후보 행에 이 PC 의 로컬 VLM(Ollama)이 그 차 주변 그림을 보고 적은 단서와 한 줄 설명을 붙인다.
    /// **판정은 바꾸지 않는다**(후보를 지우거나 덮어쓰지 않음 — 혼자 멈춘 차가 사고·고장인지는 VLM 이 판단하지 못했다, VlmCheck 1~4차). 기본 꺼짐,
    /// 이벤트 로그 머리줄의 [VLM 설명] 으로 켠다(앱을 다시 켜면 꺼짐).
    ///
    /// 흐름: 재생 루프가 유고 후보가 나온 그 프레임에서 그림 영역만 복제하고(<see cref="PrepareVlmJobs"/>) → 같은 디스패치에서 행을 '분석 중'으로 넣은 뒤
    /// (<see cref="AddZoneEvents"/>) → 디스패치가 끝나면 큐(4칸)에 넣는다(<see cref="EnqueueVlmJobs"/> — 결과가 왔는데 붙일 행이 없는 일이 없게) →
    /// 작업 하나(<see cref="VlmWorkerAsync"/>)가 한 번에 하나씩 그림을 만들어 VLM 을 부르고 → UI 스레드에서 그 행에 붙인다(<see cref="ApplyVlmResult"/>).
    /// 루프는 VLM 을 기다리지 않는다. 끄기·비우기·소스 전환은 <c>_vlmEpoch</c> 를 올려 기다리던 일과 진행 중 요청을 버린다(<see cref="ForgetVlmWork"/>)
    /// </summary>
    public partial class MainWindow
    {
        private const int VlmQueueSize = 4;                                       // 한 번에 3~4초라 후보가 한꺼번에 몰릴 때만 찬다
        private static readonly TimeSpan VlmCallTimeout = TimeSpan.FromSeconds(60);   // 첫 호출은 모델 올리기까지 6~8초, GPU 를 나눠 쓰면 3.4~4.3초

        private IVlmClient? _vlmClient;
        private volatile Channel<VlmJob>? _vlmQueue;   // 처음 켤 때 만든다 (루프가 읽는다)
        private volatile bool _vlmOn;                   // 루프가 읽는다 — 켜져 있을 때만 그림 영역을 떼어 둔다
        private bool _vlmChecking;                      // 켜는 중(Ollama 확인) — 버튼을 막는다
        private bool _vlmLoaded;                        // 이 앱이 모델을 올렸다 — 끄거나 닫을 때 내린다

        // 끄기·비우기·소스 전환마다 하나씩 오른다 — 그 전의 일·결과는 버린다. 바꾸는 쪽은 UI 스레드(락 안), 작업은 락 안에서 읽는다
        private int _vlmEpoch;
        private readonly Lock _vlmLock = new();
        private CancellationTokenSource _vlmCallCts = new();         // 진행 중 요청 취소 — _vlmEpoch 와 함께 바꾼다 (옛 것은 해제하지 않는다: 작업이 아직 쥐고 있을 수 있다)
        private readonly CancellationTokenSource _vlmLife = new();   // 앱 수명 — 창을 닫으면 작업이 끝난다

        private IVlmClient VlmClient => _vlmClient ??= CreateVlmClient();

        private static IVlmClient CreateVlmClient()
        {
#if DEBUG
            if (TestFaults.VlmClient() is { } fake) return fake;   // UI 테스트의 가짜 VLM (DETECTVIEW_TEST_FAULTS=vlm-fake · vlm-down · vlm-absent)
#endif
            return new OllamaVlmClient();
        }

        private void InitVlm() => UpdateVlmButton();

        // [VLM 설명] — 켤 때는 Ollama·모델이 있는지 먼저 본다(없으면 사유를 상태바에 내고 꺼진 채). 끌 때는 기다리던 일을 버리고 모델을 내린다
        private async void Btn_EventVlm_Click(object sender, RoutedEventArgs e)
        {
            if (_vlmChecking) return;
            if (_vlmOn)
            {
                TurnVlmOff();
                Txt_Status.Text = "VLM 설명 꺼짐 — 모델을 내렸습니다 (GPU 메모리를 돌려줌)";
                return;
            }

            _vlmChecking = true;
            UpdateVlmButton();
            string? reason;
            try { reason = await VlmClient.CheckAsync(_vlmLife.Token); }
            catch (OperationCanceledException) { return; }   // 창을 닫는 중
            catch (Exception ex) { reason = ex.Message; }    // 이벤트 핸들러에서 새면 앱이 죽는다
            finally { _vlmChecking = false; }

            if (reason != null)
            {
                UpdateVlmButton();
                Txt_Status.Text = $"VLM 설명을 켜지 못함 — {reason}";
                return;
            }
            StartVlmWorker();
            _vlmOn = true;
            _vlmLoaded = true;
            UpdateVlmButton();
            Txt_Status.Text = $"VLM 설명 켜짐 — 유고 후보마다 {VlmClient.Model} 이 그 차 주변 그림을 보고 단서·설명을 붙입니다 (참고용, 판정은 그대로)";
            _ = LoadVlmModelAsync();   // 첫 후보도 3초 안팎이 되게 미리 올린다 (실패해도 첫 후보 때 올라간다)
        }

        private void TurnVlmOff()
        {
            _vlmOn = false;
            ForgetVlmWork();
            UpdateVlmButton();
            if (_vlmLoaded) { _vlmLoaded = false; _ = UnloadVlmModelAsync(); }
        }

        private async Task LoadVlmModelAsync()
        {
            CancellationToken token;
            lock (_vlmLock) token = _vlmCallCts.Token;   // 그 사이 끄면 같이 취소된다
            try { await VlmClient.LoadAsync(token); }
            catch (Exception ex) { Debug.WriteLine($"VLM 모델 올리기 실패: {ex.Message}"); }
        }

        private async Task UnloadVlmModelAsync()
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await VlmClient.UnloadAsync(cts.Token);
            }
            catch (Exception ex) { Debug.WriteLine($"VLM 모델 내리기 실패: {ex.Message}"); }
        }

        private void UpdateVlmButton()
        {
            Ui.SetIsActive(Btn_EventVlm, _vlmOn);
            Btn_EventVlm.IsEnabled = !_vlmChecking;
            // 상태는 UIA ItemStatus 로 (UI 테스트가 읽는다) — 버튼 글자(UIA 이름)는 그대로 둔다
            AutomationProperties.SetItemStatus(Btn_EventVlm, _vlmChecking ? "확인 중" : _vlmOn ? "켜짐" : "꺼짐");
            string model = _vlmClient?.Model ?? VlmPrompt.Model;
            Btn_EventVlm.ToolTip = _vlmChecking ? $"Ollama 와 모델 {model} 을 확인하는 중…"
                : _vlmOn ? $"VLM 설명 켜짐 ({model}) — 누르면 끄고 모델을 내립니다 (GPU 메모리를 돌려줌)"
                : $"유고 후보마다 이 PC 의 로컬 VLM(Ollama {model})이 그 차 주변 그림을 보고 단서와 한 줄 설명을 붙입니다 — " +
                  "참고용이고 유고 판정은 바꾸지 않습니다.\n켜면 GPU 메모리를 약 4 GB 쓰고, 한 번에 3~4초 걸리는 동안 재생이 조금 느려질 수 있습니다. " +
                  "Ollama 가 떠 있어야 합니다 (그림은 이 PC 밖으로 나가지 않음)";
        }

        // [UI] 기다리던 일·진행 중 요청을 버린다 — 끄기·비우기·소스 전환(ClearZoneEvents). 분석 중이던 행은 VLM 줄을 지운다(끝난 행은 그대로)
        private void ForgetVlmWork()
        {
            lock (_vlmLock)
            {
                _vlmEpoch++;
                _vlmCallCts.Cancel();
                _vlmCallCts = new CancellationTokenSource();
            }
            var queue = _vlmQueue;
            if (queue != null) while (queue.Reader.TryRead(out var job)) job.Dispose();
            foreach (var it in _eventItems) if (it.VlmState == VlmRowState.Pending) it.ClearVlm();
        }

        private void StartVlmWorker()
        {
            if (_vlmQueue != null) return;
            var queue = Channel.CreateBounded<VlmJob>(new BoundedChannelOptions(VlmQueueSize) { FullMode = BoundedChannelFullMode.Wait });
            var client = VlmClient;
            var life = _vlmLife.Token;
            _ = Task.Run(() => VlmWorkerAsync(queue.Reader, client, life));
            _vlmQueue = queue;
        }

        // [루프] 유고 후보가 나온 그 프레임에서 그림 영역만 복제한다 — 박스는 같은 프레임의 추적된 박스(번호로). 원본은 방금 내보낼 프레임(_lastFrame —
        // 이 루프만 맞바꾸는 버퍼라 뷰 식별처럼 락 밖에서 읽는다). 큐에는 행을 넣는 디스패치 뒤에 넣는다(EnqueueVlmJobs)
        private List<VlmJob>? PrepareVlmJobs(List<ZoneEvent> events, List<Det> dets)
        {
            OpenCvSharp.Mat? frame;
            lock (_lastFrameLock) frame = _lastFrame;
            if (frame == null || frame.Empty()) return null;
            int epoch = Volatile.Read(ref _vlmEpoch);
            List<VlmJob>? jobs = null;
            foreach (var e in events)
            {
                if (e.Kind != ZoneEventKind.Incident) continue;
                int i = dets.FindIndex(d => d.TrackId == e.TrackId);
                if (i < 0) continue;
                try
                {
                    var region = VlmCrop.Cut(frame, dets[i]);
                    if (region != null) (jobs ??= []).Add(new VlmJob(e, region, epoch));
                }
                catch (Exception ex) { Debug.WriteLine($"VLM 그림 자르기 실패: {ex.Message}"); }   // 재생은 이어간다
            }
            return jobs;
        }

        // [루프] 행을 넣은 디스패치 뒤 — 큐가 차 있으면 그 행은 '건너뜀'
        private void EnqueueVlmJobs(List<VlmJob> jobs)
        {
            var queue = _vlmQueue;
            foreach (var job in jobs)
            {
                if (queue != null && queue.Writer.TryWrite(job)) continue;
                job.Dispose();
                var ev = job.Event;
                int epoch = job.Epoch;
                _ = Dispatcher.InvokeAsync(() => ApplyVlmResult(ev, epoch, VlmResult.Skip($"대기열 가득 ({VlmQueueSize}건 — 한 번에 3~4초씩 차례로)")));
            }
        }

        // [작업] 앱 수명 동안 하나 — 한 번에 하나씩 그림을 만들어 VLM 을 부르고, 결과를 UI 스레드에서 그 행에 붙인다
        private async Task VlmWorkerAsync(ChannelReader<VlmJob> reader, IVlmClient client, CancellationToken life)
        {
            try
            {
                while (await reader.WaitToReadAsync(life))
                {
                    while (reader.TryRead(out var job))
                    {
                        using (job)
                        {
                            CancellationToken call;
                            lock (_vlmLock)
                            {
                                if (job.Epoch != _vlmEpoch) continue;   // 끄거나 비운 뒤의 일
                                call = _vlmCallCts.Token;
                            }
                            var result = await RunVlmJobAsync(job, client, call, life);
                            if (result.Outcome == VlmOutcome.Cancelled) continue;
                            var ev = job.Event;
                            int epoch = job.Epoch;
                            _ = Dispatcher.InvokeAsync(() => ApplyVlmResult(ev, epoch, result));
                        }
                    }
                }
            }
            catch (OperationCanceledException) { }   // 창을 닫았다
            catch (Exception ex) { Debug.WriteLine($"VLM 작업이 멈춤: {ex}"); }
        }

        private static async Task<VlmResult> RunVlmJobAsync(VlmJob job, IVlmClient client, CancellationToken call, CancellationToken life)
        {
            var sw = Stopwatch.StartNew();
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(call, life);
            limit.CancelAfter(VlmCallTimeout);
            try
            {
                byte[] jpeg = VlmCrop.Encode(job.Region);
                var observation = await client.ObserveAsync(jpeg, limit.Token);
                return VlmResult.Ok(observation, sw.Elapsed.TotalSeconds);
            }
            catch (OperationCanceledException) when (call.IsCancellationRequested || life.IsCancellationRequested) { return VlmResult.Cancelled; }
            catch (OperationCanceledException) { return VlmResult.Fail($"응답 없음 ({VlmCallTimeout.TotalSeconds:0}초)"); }
            catch (VlmException ex) { return VlmResult.Fail(ex.Message); }
            catch (Exception ex)
            {
                Debug.WriteLine($"VLM 오류: {ex}");
                return VlmResult.Fail($"오류 — {ex.Message}");
            }
        }

        // [UI] 그 행(같은 이벤트 참조)에 결과를 붙인다. 그 사이 끄거나 비웠으면(세대가 다르면) 버린다 — 비운 뒤엔 행도 없다
        private void ApplyVlmResult(ZoneEvent ev, int epoch, VlmResult result)
        {
            if (epoch != _vlmEpoch) return;
            var item = _eventItems.FirstOrDefault(i => ReferenceEquals(i.Event, ev));
            item?.SetVlm(result, VlmClient.Model);
        }

        // [UI] 창을 닫는다 — 작업을 끝내고, 모델을 올렸으면 내린다(0.5초까지만 기다림 — 못 내리면 Ollama 가 30분 뒤 내린다)
        private void ShutdownVlm()
        {
            _vlmOn = false;
            lock (_vlmLock) _vlmCallCts.Cancel();
            _vlmLife.Cancel();
            if (!_vlmLoaded) return;
            _vlmLoaded = false;
            var client = VlmClient;
            try
            {
                Task.Run(async () =>
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
                    await client.UnloadAsync(cts.Token);
                }).Wait(600);
            }
            catch (Exception ex) { Debug.WriteLine($"VLM 모델 내리기 실패: {ex.Message}"); }
        }
    }
}
