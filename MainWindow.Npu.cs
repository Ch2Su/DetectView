using Microsoft.ML.OnnxRuntime;
using Microsoft.Windows.AI.MachineLearning;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using YoloDotNet;
using YoloDotNet.Models;

namespace DetectView
{
    /// <summary>
    /// 추론 장치(GPU · TensorRT · NPU) 바꿔 끼우기와 왼쪽 패널 "추론 장치" 선택. 가속 장치 준비 — ① 짝 모델 확인 → ② EP 준비·등록과
    /// 캐시 확인 → ③ 세션 생성(= NPU 컴파일 · TensorRT 엔진 만들기) — 는 모두 스레드 풀에서 하고, UI 스레드는 단계 문구(SetPrepStage·UpdateDeviceUi)만 맡는다.
    /// 그동안은 지금 장치가 그대로 돈다. 다 되면 _yoloLock 안에서 _yolo 를 바꾸고 옛 세션을 놓는다.
    /// 예전엔 ② 와 시작 때의 EP 확인을 UI 스레드에서 해서 그동안 창이 멈췄다 (이슈 #4) — EP 카탈로그 조회(FindAllProviders)가
    /// 한 번에 수 초다 (2026-10-31 콘솔 실측 3.8~11초, PC 가 바쁠수록 길다. 그 스레드의 CPU 는 0.2초 미만 — 거의 기다리는 시간).
    /// 이 파일은 장치 공통 상태 기계와 NPU(AMD Ryzen AI · VitisAI EP)를, MainWindow.Trt.cs 는 TensorRT(NVIDIA TensorRT for RTX — 2026-11-08)를 맡는다.
    /// 측정·함정은 CLAUDE.md "추론 장치" · docs/npu.md 와 experiments/NpuBench/README.md
    /// </summary>
    public partial class MainWindow
    {
        private const string NpuEpName = "VitisAIExecutionProvider";

        /// <summary>추론 장치 — GPU(DirectML) · TensorRT(같은 NVIDIA GPU 를 FP16 짝으로) · NPU(AMD, 양자화 짝)</summary>
        private enum InferenceDevice { Gpu, TensorRt, Npu }

        // 아래 상태는 모두 UI 스레드 전용 (_inferenceClosed 만 _yoloLock 안에서).
        // 예전(GPU ↔ NPU 둘뿐)엔 _onNpu·_wantNpu·_npuBusy 였다 — 장치가 셋이 되며 '지금 장치'·'고른 장치'·'준비 중인 가속 장치'로 일반화했다
        private InferenceDevice _onDevice = InferenceDevice.Gpu;     // 지금 _yolo 가 도는 장치
        private InferenceDevice _wantDevice = InferenceDevice.Gpu;   // 사용자가 고른 장치 — 준비가 끝났을 때 이걸 보고 바꿀지 버릴지 정한다
        private InferenceDevice? _prepDevice;   // 준비 중인 가속 장치 — 한 번에 하나 (네이티브 컴파일·엔진 만들기는 중간에 못 끊는다)
        private bool _gpuBusy;                  // GPU 로 되돌리는 중
        private bool? _npuAvailable;            // 시작 때 확인 결과 (null = 확인 중)
        private string _npuInfo = "NPU 확인 중";   // NPU 항목 툴팁 (설명 또는 쓸 수 없는 사유)
        private string? _npuCacheKey;           // 마지막으로 준비한 NPU 컴파일 캐시 키 (NPU 툴팁 끝에 보인다)
        private bool? _trtAvailable;            // TensorRT 쪽 — MainWindow.Trt.cs
        private string _trtInfo = "TensorRT 확인 중";
        private string? _trtCacheKey;           // 마지막으로 준비한 TensorRT 엔진 캐시 키 (TensorRT 툴팁 끝)
        private string _prepStage = "";         // 준비 단계 문구 (상태바 Txt_Device)
        private string? _deviceError;           // 마지막 전환 실패 사유 — 다음에 장치를 고를 때까지 상태바에 표시
        private InferenceDevice _deviceErrorOf; // 그 실패가 어느 장치의 것인지 (상태바 툴팁에 그 장치 설명을 붙인다)
        private readonly Stopwatch _prepStageWatch = new();
        private DispatcherTimer? _deviceTimer;  // 준비 중 경과 초 표시
        private bool _updatingSelector; // 코드에서 선택을 바꿀 땐 SelectionChanged 가 전환을 다시 부르지 않게
        private bool _inferenceClosed;  // 창이 닫힘 — 늦게 끝난 준비가 _yolo 를 되살리지 않게 (_yoloLock 안에서 읽고 쓴다)
        private string _gpuName = "GPU";

        /// <summary>짝 모델 확인 결과. Ok 면 Quant(양자화 방식)·NpuModelMd5(캐시 키)가 차 있다</summary>
        private sealed record NpuModelCheck(bool Ok, string Message, string NpuModelPath, string? Quant = null, string? NpuModelMd5 = null);

        // best.onnx → best.npu.onnx (tools/Make-NpuModel.ps1 이 원본 옆에 만드는 이름)
        private static string NpuModelPath =>
            System.IO.Path.Combine(AppContext.BaseDirectory, System.IO.Path.ChangeExtension(ModelFileName, ".npu.onnx"));

        private static string ModelPath => System.IO.Path.Combine(AppContext.BaseDirectory, ModelFileName);

        private static string DeviceLabel(InferenceDevice d) => d switch
        {
            InferenceDevice.Npu => "NPU",
            InferenceDevice.TensorRt => "TensorRT",
            _ => "GPU",
        };

        /// <summary>
        /// 창이 뜬 뒤: TensorRT·NPU 를 쓸 수 있는지 미리 확인해 항목을 열고 닫는다 (짝 모델·EP 유무만 — 컴파일·엔진은 고를 때).
        /// 짝 모델은 장치마다 따로 보고(하나가 실패해도 다른 쪽은 연다) EP 카탈로그는 한 번만 조회한다 (한 번에 수 초 — 클래스 주석).
        /// 명령줄 --npu 면 확인 뒤 바로 NPU 를 고른다
        /// </summary>
        private async Task InitInferenceDeviceAsync(bool startOnNpu)
        {
            if (_yolo == null)
            {
                _npuAvailable = false; _npuInfo = "GPU 모델 로드에 실패해 쓸 수 없음";
                _trtAvailable = false; _trtInfo = "GPU 모델 로드에 실패해 쓸 수 없음";
                UpdateDeviceUi();
                return;
            }
            // EP 객체(WinRT)는 넘겨받지 않고 준비 상태 값만 받는다 (FindProvider 주석)
            var r = await Task.Run(() =>
            {
                NpuModelCheck? npu = null; TrtModelCheck? trt = null;
                string? npuError = null, trtError = null, catalogError = null;
                ExecutionProviderReadyState? npuState = null, trtState = null;
                try { npu = CheckNpuModel(ModelPath); } catch (Exception ex) { npuError = ex.Message; }
                try { trt = CheckTrtModel(ModelPath); } catch (Exception ex) { trtError = ex.Message; }
                if (npu?.Ok == true || trt?.Ok == true)
                {
                    try
                    {
                        var all = FindProviders();
                        npuState = all.FirstOrDefault(p => p.Name == NpuEpName)?.ReadyState;
                        trtState = all.FirstOrDefault(p => p.Name == TrtEpName)?.ReadyState;
                    }
                    catch (Exception ex) { catalogError = ex.Message; }
                }
                return (npu, npuError, trt, trtError, npuState, trtState, catalogError);
            });

            if ((r.npuError ?? (r.npu?.Ok == true ? r.catalogError : null)) is { } npuFail) SetNpuUnavailable($"확인 실패: {npuFail}");
            else if (!r.npu!.Ok) SetNpuUnavailable(r.npu.Message);
            else if (r.npuState == null) SetNpuUnavailable("이 PC 에서 쓸 수 있는 AMD NPU 실행 공급자(VitisAI)가 없음");
            else
            {
                _npuAvailable = true;
                _npuInfo = $"AMD NPU (VitisAI · {r.npu.Quant}) — GPU 보다 느리지만 외장 GPU 를 비워 둔다.\n" +
                           "처음 고르면 약 1분 컴파일한다 (다음부터는 수 초). 그동안은 GPU 가 그대로 돈다" +
                           (r.npuState == ExecutionProviderReadyState.NotPresent
                               ? ".\n실행 공급자를 먼저 내려받는다 (처음 한 번, 수 분)" : "");
            }
            ApplyTrtCheck(r.trt, r.trtError ?? (r.trt?.Ok == true ? r.catalogError : null), r.trtState);
            UpdateDeviceUi();
            if (startOnNpu) await SwitchToAsync(InferenceDevice.Npu);
        }

        private void SetNpuUnavailable(string reason)
        {
            _npuAvailable = false;
            _npuInfo = $"NPU 를 쓸 수 없음: {reason}";
            _npuCacheKey = null;   // 지난 준비의 키는 더 뜻이 없다 — 툴팁 끝이 사유가 되게
        }

        private async void InferenceSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_updatingSelector || !IsLoaded) return;
            var item = InferenceSelector.SelectedItem;
            await SwitchToAsync(item == Item_Npu ? InferenceDevice.Npu : item == Item_Trt ? InferenceDevice.TensorRt : InferenceDevice.Gpu);
        }

        /// <summary>
        /// 고른 장치로 바꾼다. 가속 장치(TensorRT·NPU)는 준비가 끝날 때까지 지금 장치가 그대로 돌고, 실패하면 지금 장치에 머문다 (사유는 툴팁·상태바).
        /// 준비 도중 다른 장치를 고르면 의향만 바꾸고, 준비가 끝났을 때 결과를 버린다. 준비는 한 번에 하나 — 그사이 다른 가속 장치를 골랐으면
        /// 앞 준비가 끝난 뒤 그쪽을 준비한다 (상태바 '… 준비 대기')
        /// </summary>
        private async Task SwitchToAsync(InferenceDevice target)
        {
            if (target == InferenceDevice.Gpu) { await SwitchToGpuAsync(); return; }
            _wantDevice = target;
            _deviceError = null;
            // 이미 그 장치이거나 준비 중이면 의향만 (같은 장치를 다시 고르면 새로 준비하지 않고 진행 중인 것을 기다린다 — 같은 캐시 키로 둘이 겹치지 않게)
            if (_onDevice == target || _prepDevice != null || _yolo == null) { UpdateDeviceUi(); return; }

            _prepDevice = target;
            Yolo? next = null;
            try
            {
                (next, string title) = target == InferenceDevice.Npu ? await PrepareNpuYoloAsync() : await PrepareTrtYoloAsync();
                if (_wantDevice != target) return;   // 준비 도중 다른 장치를 골랐다 — finally 에서 버린다
                if (!SwapYolo(next)) return;
                next = null;   // 이제 _yolo 가 갖는다
                _onDevice = target;
                Title = title;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"{DeviceLabel(target)} 전환 실패: {ex}");
                _wantDevice = _onDevice;   // 지금 장치에 머문다
                _deviceError = $"{DeviceLabel(target)} 로 바꾸지 못함: {ex.Message}";
                _deviceErrorOf = target;
            }
            finally
            {
                // 버리는 세션도 SwapYolo 처럼 백그라운드로 해제 (세션 해제가 0.5초쯤이라 UI 가 멈칫한다)
                if (next != null) _ = Task.Run(next.Dispose);
                _prepDevice = null;
                UpdateDeviceUi();
                // 준비하는 동안 다른 가속 장치를 골랐으면 이제 그쪽을 준비한다 (창이 닫혔으면 하지 않는다 — 준비가 끝없이 되풀이되지 않게)
                bool closed;
                lock (_yoloLock) closed = _inferenceClosed;
                if (!closed && _wantDevice != target && _wantDevice != InferenceDevice.Gpu && _wantDevice != _onDevice)
                    _ = SwitchToAsync(_wantDevice);
            }
        }

        /// <summary>NPU 준비 ①②③ — 새 Yolo 와 그때의 창 제목. 실패는 예외로 (SwitchToAsync 가 지금 장치에 머문다)</summary>
        private async Task<(Yolo Yolo, string Title)> PrepareNpuYoloAsync()
        {
            SetPrepStage("NPU 확인 중");
            var check = await Task.Run(() => CheckNpuModel(ModelPath));
            if (!check.Ok)
            {
                SetNpuUnavailable(check.Message);   // 짝 모델 문제는 고치기 전까진 다시 해도 같다 → 항목을 닫는다
                throw new InvalidOperationException(check.Message);
            }

            // ② EP 준비·등록과 캐시 확인도 스레드 풀에서 — 예전엔 UI 스레드라 카탈로그 조회 수 초 동안 창이 멈췄다 (이슈 #4).
            // Progress 는 여기(UI 스레드)서 만든다: 만든 스레드의 SynchronizationContext 로 보고하므로 내려받기 진행률이 UI 스레드로 온다.
            // 그사이 다른 장치를 골라도 ③ 은 그대로 한다 — 예전과 같다(끝났을 때 결과를 버리고, 남은 캐시로 다음엔 수 초)
            var progress = new Progress<double>(p => SetPrepStage($"NPU 실행 공급자 내려받는 중 {p:F0}%", resetClock: false));
            var prep = await Task.Run(() => PrepareVitisAiAsync(check.NpuModelMd5!, progress));
            _npuCacheKey = prep.CacheKey;   // 툴팁에 보인다 (아래 SetPrepStage 가 갱신)
            if (prep.KeyWarning != null)
            {
                Debug.WriteLine($"NPU 캐시 키 {prep.CacheKey}: {prep.KeyWarning}");
                if (!_npuInfo.Contains(prep.KeyWarning)) _npuInfo += $"\n※ {prep.KeyWarning}";   // NPU 항목 툴팁 (아래 SetPrepStage 가 갱신)
            }
            SetPrepStage(prep.Cached ? "NPU 준비 중" : "NPU 컴파일 중 (첫 실행 약 1분)");

            var sw = Stopwatch.StartNew();
            // 입력은 GPU 와 같은 YoloLetterbox(OpenCV 쌍선형 레터박스 — DetectBgra)로 들어간다. 짝 모델의 양자화 보정도 같은 규칙·같은 보간이다 (tools/npu_quantize.py)
            var npuYolo = await Task.Run(() => new Yolo(YoloOptionsFor(
                new WindowsMlExecutionProvider(check.NpuModelPath, prep.Device, prep.EpOptions))));
            Debug.WriteLine($"NPU 세션 {sw.Elapsed.TotalSeconds:F1}초 (캐시 {(prep.Cached ? "있음" : "없음")}, 키 {prep.CacheKey})");
            return (npuYolo, $"DetectView — {DescribeModel(npuYolo)} — {prep.Device.HardwareDevice.Vendor} NPU (VitisAI · {check.Quant})");
        }

        /// <summary>GPU 로 되돌린다 (GPU 세션을 다시 만든다, 1초 안팎). 가속 장치 준비 중이면 의향만 바꾼다</summary>
        private async Task SwitchToGpuAsync()
        {
            _wantDevice = InferenceDevice.Gpu;
            _deviceError = null;
            if (_onDevice == InferenceDevice.Gpu || _gpuBusy) { UpdateDeviceUi(); return; }

            _gpuBusy = true;
            UpdateDeviceUi();
            Yolo? gpuYolo = null;
            try
            {
                string gpuName;
                (gpuYolo, gpuName) = await Task.Run(() => CreateGpuYolo(ModelPath));
                if (_wantDevice != InferenceDevice.Gpu) return;   // 그새 다른 장치를 골랐다 — 지금 장치 그대로, GPU 는 finally 에서 버린다
                if (!SwapYolo(gpuYolo)) return;
                var installed = gpuYolo;
                gpuYolo = null;
                _onDevice = InferenceDevice.Gpu;
                _gpuName = gpuName;
                Title = GpuTitle(installed);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"GPU 전환 실패: {ex}");
                _wantDevice = _onDevice;   // 지금 장치(가속 장치)에 머문다
                _deviceError = $"GPU 로 되돌리지 못함: {ex.Message}";
                _deviceErrorOf = InferenceDevice.Gpu;
            }
            finally
            {
                if (gpuYolo != null) _ = Task.Run(gpuYolo.Dispose);   // 버리는 세션도 백그라운드로 (위와 같은 이유)
                _gpuBusy = false;
                UpdateDeviceUi();
            }
        }

        // 진행 중인 추론(재생 루프·HUD·SAHI)이 끝난 뒤 바꿔 끼운다. 추론은 모두 락 안에서 _yolo 를 읽으므로 바꾼 뒤엔
        // 옛 인스턴스를 쓰는 곳이 없다 → 락 밖에서, UI 를 멈추지 않게 백그라운드로 해제한다 (GPU 세션 해제는 0.5초쯤).
        // GPU 세션을 곧바로 놓아야 외장 GPU 가 쉴 수 있다 (DirectML 장치를 잡고 있으면 깨어 있다).
        // 창이 먼저 닫혔으면 false — 호출 측이 새 인스턴스를 버린다
        private bool SwapYolo(Yolo next)
        {
            Yolo? old;
            lock (_yoloLock)
            {
                if (_inferenceClosed) return false;
                old = _yolo;
                _yolo = next;
            }
            if (old != null) _ = Task.Run(old.Dispose);
            return true;
        }

        private void SetPrepStage(string text, bool resetClock = true)
        {
            _prepStage = text;
            if (resetClock) _prepStageWatch.Restart();
            UpdateDeviceUi();
        }

        /// <summary>"추론 장치" 선택·항목 툴팁·상태바 진행 표시를 지금 상태에 맞춘다 (멱등)</summary>
        private void UpdateDeviceUi()
        {
            _updatingSelector = true;
            Item_Npu.IsEnabled = _npuAvailable == true || _onDevice == InferenceDevice.Npu || _prepDevice == InferenceDevice.Npu;
            Item_Trt.IsEnabled = _trtAvailable == true || _onDevice == InferenceDevice.TensorRt || _prepDevice == InferenceDevice.TensorRt;
            InferenceSelector.SelectedItem = _wantDevice switch
            {
                InferenceDevice.Npu => Item_Npu,
                InferenceDevice.TensorRt => Item_Trt,
                _ => Item_Gpu,
            };
            _updatingSelector = false;

            Item_Gpu.ToolTip = $"{_gpuName} (DirectML) — 기본. 어느 GPU 에서나 돈다";
            // 쓴 캐시 키를 보인다 — 키가 바뀌면(EP·ORT 업데이트) 다시 컴파일하므로 어느 키 폴더인지 보여야 그 1분을 설명할 수 있다.
            // UI 테스트(Test-NpuSwitch)도 이 줄로 이번 실행이 쓴 키 폴더를 찾는다 — 문구를 바꾸면 테스트도 고칠 것 ('컴파일 캐시 키:' · '엔진 캐시 키:')
            string npuTip = _npuCacheKey == null ? _npuInfo : $"{_npuInfo}\n컴파일 캐시 키: {_npuCacheKey}";
            string trtTip = _trtCacheKey == null ? _trtInfo : $"{_trtInfo}\n엔진 캐시 키: {_trtCacheKey}";
            Item_Npu.ToolTip = npuTip;
            Item_Trt.ToolTip = trtTip;
            string now = _onDevice switch
            {
                InferenceDevice.Npu => "지금: AMD NPU",
                InferenceDevice.TensorRt => $"지금: {_gpuName} (TensorRT)",
                _ => $"지금: {_gpuName}",
            };
            // NPU 를 끝 줄에 둔다 — 장치가 둘이던 때와 같게 (테스트 기록의 '툴팁 끝'이 NPU 줄)
            InferenceSelector.ToolTip = $"{now}\n\nGPU: {Item_Gpu.ToolTip}\nTensorRT: {trtTip}\nNPU: {npuTip}";

            // 상태바: 준비 진행·대기(경고 색)·실패(위험 색)만 보인다
            string? text = null, tip = null;
            bool failed = false, tick = false;
            if (_prepDevice is { } prep && _wantDevice == prep)
            {
                text = $"{_prepStage} {_prepStageWatch.Elapsed.TotalSeconds:F0}초";
                tip = $"{DeviceLabel(prep)} 를 준비하는 동안은 지금 장치가 그대로 검출한다. 다른 장치를 고르면 준비 결과를 버린다";
                tick = true;
            }
            else if (_prepDevice is { } busy && _wantDevice != InferenceDevice.Gpu && _wantDevice != _onDevice)
            {
                // 준비는 한 번에 하나 — 앞 준비가 끝나면 고른 장치를 준비한다 (SwitchToAsync 의 finally)
                text = $"{DeviceLabel(_wantDevice)} 준비 대기 — {DeviceLabel(busy)} 준비가 끝나면 시작";
                tip = $"{DeviceLabel(busy)} 의 컴파일·엔진 만들기는 중간에 못 끊는다. 그동안은 지금 장치가 그대로 검출한다";
            }
            else if (_gpuBusy) { text = "GPU 로 되돌리는 중"; }
            else if (_deviceError != null)
            {
                text = "추론 장치 전환 실패 (툴팁)";
                string info = _deviceErrorOf switch { InferenceDevice.TensorRt => _trtInfo, InferenceDevice.Npu => _npuInfo, _ => "" };
                tip = info.Length == 0 ? _deviceError : _deviceError + $"\n\n{info}";
                failed = true;
            }
            Txt_Device.Text = text ?? "";
            Txt_Device.ToolTip = tip;
            Txt_Device.Visibility = text == null ? Visibility.Collapsed : Visibility.Visible;
            Txt_Device.SetResourceReference(TextBlock.ForegroundProperty, failed ? "DangerBrush" : "WarnBrush");

            // 준비 중엔 경과 초를 1초마다 갱신
            if (tick && _deviceTimer == null)
            {
                _deviceTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                _deviceTimer.Tick += (_, _) => UpdateDeviceUi();
                _deviceTimer.Start();
            }
            else if (!tick && _deviceTimer != null)
            {
                _deviceTimer.Stop();
                _deviceTimer = null;
            }
        }

        /// <summary>
        /// best.npu.onnx 가 지금 best.onnx 의 짝인지 확인한다 (tools/Make-NpuModel.ps1 이 넣은 메타데이터로).
        /// 배포 폴더의 best.onnx 를 수시로 바꿔 쓰므로, 옛 짝이 남아 있으면 엉뚱한 모델이 NPU 에서 돈다.
        /// 양자화 표시(npu_quant)가 없는 모델도 거부한다 — FP32 를 NPU 에 넣으면 EP 가 BF16 컴파일에 실패하고 프로세스를 끝냈다
        /// </summary>
        private static NpuModelCheck CheckNpuModel(string modelPath)
        {
            string npuPath = NpuModelPath;
            string name = System.IO.Path.GetFileName(npuPath);
            if (!System.IO.File.Exists(npuPath))
                return new(false, $"{name} 없음 — tools\\Make-NpuModel.ps1 로 만드세요", npuPath);

            // 메타데이터만 읽는다 (CPU·그래프 최적화 끔 — 여기서는 NPU 컴파일을 하지 않는다)
            Dictionary<string, string> meta;
            using (var so = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_DISABLE_ALL })
            using (var probe = new InferenceSession(npuPath, so))
                meta = new Dictionary<string, string>(probe.ModelMetadata.CustomMetadataMap);

            if (!meta.TryGetValue("npu_quant", out var quant))
                return new(false, $"{name} 가 양자화 모델이 아님 — tools\\Make-NpuModel.ps1 로 만드세요", npuPath);
            if (!meta.TryGetValue("npu_source_md5", out var pair) ||
                !string.Equals(pair, Md5(modelPath), StringComparison.OrdinalIgnoreCase))
                return new(false, $"{ModelFileName} 가 바뀌어 {name} 와 짝이 안 맞음 — tools\\Make-NpuModel.ps1 로 다시 만드세요", npuPath);
            return new(true, quant, npuPath, quant, Md5(npuPath));
        }

        private static string Md5(string path)
        {
            using var fs = System.IO.File.OpenRead(path);
            return Convert.ToHexString(MD5.HashData(fs));
        }

        // 이 PC 에 맞는 실행 공급자 목록 (카탈로그가 장치에 맞는 것만 돌려준다 — AMD NPU·NVIDIA RTX 가 없으면 그 EP 는 없다). 내려받거나 등록하지는 않는다.
        // 스레드 풀에서만 부른다: 한 번에 수 초 걸린다(클래스 주석). 카탈로그·EP 객체는 아파트에 묶이지 않지만(IAgileObject, 콘솔 실측)
        // 부른 쪽 밖으로는 상태·버전 같은 평범한 값만 내보낸다 — WinRT 객체를 여러 스레드가 나눠 쓰는 경우를 만들지 않게
        private static ExecutionProvider[] FindProviders()
        {
            WindowsMlExecutionProvider.EnsureEnv();   // EP 를 등록할 OrtEnv 를 우리 설정(로그 수준)으로 먼저 만든다
            return ExecutionProviderCatalog.GetDefault().FindAllProviders();
        }

        private static ExecutionProvider? FindProvider(string epName) => FindProviders().FirstOrDefault(p => p.Name == epName);

        private static ExecutionProvider? FindVitisAiProvider() => FindProvider(NpuEpName);

        /// <summary>
        /// ② 의 결과 — NPU 장치와 EP 옵션(컴파일 캐시). 스레드 풀에서 만든 평범한 값만 담는다 (EP 의 WinRT 객체는 넣지 않는다).
        /// KeyWarning: 캐시 키에 넣을 버전을 못 읽었을 때의 안내 (없으면 null)
        /// </summary>
        private sealed record NpuPrep(OrtEpDevice Device, IReadOnlyDictionary<string, string> EpOptions, string CacheKey, bool Cached,
                                      string? KeyWarning);

        /// <summary>
        /// ② VitisAI EP 만 준비·등록해 NPU 장치를 찾고 컴파일 캐시 옵션을 정한다. 스레드 풀에서 부른다 — UI 상태는 건드리지 않고
        /// 진행은 progress(0~100)로만 알린다 (내려받을 게 있을 때만 온다).
        /// EnsureAndRegisterCertifiedAsync(전부)는 쓰지 않는다 — TensorRT-RTX·MIGraphX 까지 내려받느라 이 PC 에서 8분 걸렸다
        /// </summary>
        private static async Task<NpuPrep> PrepareVitisAiAsync(string npuModelMd5, IProgress<double> progress)
        {
            var ep = FindVitisAiProvider()
                     ?? throw new InvalidOperationException("이 PC 에 맞는 AMD NPU 실행 공급자(VitisAI)가 없음");
            await EnsureProviderReadyAsync(ep, progress);
            ep.TryRegister();   // 이미 등록돼 있으면 false 일 수 있다 — 장치가 보이는지로 판단한다
            var device = WindowsMlExecutionProvider.FindEpDevice(NpuEpName, OrtHardwareDeviceType.NPU)
                         ?? throw new InvalidOperationException("NPU 장치를 찾지 못함 (실행 공급자 등록 실패)");

            // 컴파일 결과는 사용자별 폴더에 남겨 두 번째부터 1초 안팎에 연다. 키 = NPU 모델 MD5 + EP 버전 + ONNX Runtime 버전 —
            // 이 가운데 하나가 바뀌면 키가 달라져 한 번 다시 컴파일한다:
            //  · NPU 모델: 짝을 다시 만들면 (MD5)
            //  · EP: Windows Update 로 바뀌면 (2026-10-30 1.8.75 → 1.8.80) — 패키지 버전, 못 읽으면 EP 라이브러리 파일의 시각·버전 (EpVersionForKey)
            //  · ORT: Windows ML 패키지를 올리면 — 올라온 onnxruntime.dll 의 파일 버전(빌드 날짜·커밋)이라 같은 번호로 다시 빌드한 것도 갈린다 (OrtVersionForKey)
            // 드라이버·NPU 펌웨어는 키에 없다(ORT 의 장치 정보에 드라이버 버전이 없다) — 그것만 바뀌면 옛 컴파일 결과를 그대로 쓴다 (영향은 확인 안 함).
            // 키를 직접 주는 건 기본 키가 가중치·스케일이 달라도 같아서 옛 컴파일 결과를 재사용했기 때문이다(틀린 결과·크래시 실측).
            // ORT 를 넣은 건 패키지를 올리면 EP 에 넘기는 그래프(최적화·분할)가 달라질 수 있는데 예전 키엔 없어 옛 컴파일 결과를 그대로 쓸 수
            // 있었기 때문이다 (이슈 #5 — 실제로 결과가 달라지는지는 확인 안 함. 런타임을 올려 돌려 본 적이 없다).
            // 그래서 이 판으로 처음 NPU 를 고르면 키가 바뀌어 한 번 다시 컴파일한다 (이 PC 약 1분, 옛 키 폴더는 남는다)
            var (cacheDir, cacheKey, keyWarning) = CacheKeyFor("npu_cache", npuModelMd5, ep, "NPU 컴파일 캐시");
            var epOptions = new Dictionary<string, string>
            {
                ["cache_dir"] = cacheDir,
                ["cache_key"] = cacheKey,
                ["enable_cache_file_io_in_mem"] = "0",   // 디스크에 남겨야 다음 실행에서 캐시를 쓴다
            };
            // 키 폴더가 있어도 비어 있으면 캐시가 없는 것이다 — 컴파일이 중단·실패하면 빈 폴더가 남는다
            // (2026-10-30 실측: EP 가 1.8.80 으로 바뀐 날 생긴 빈 키 폴더). 폴더만 보면 1분 넘게 컴파일하는 동안 '준비 중'이 떴다
            return new(device, epOptions, cacheKey, HasFiles(System.IO.Path.Combine(cacheDir, cacheKey)), keyWarning);
        }

        // 이미 내려받아 둔 EP 도 프로세스마다 처음엔 NotReady 다(2026-10-31 콘솔 실측) → 프로세스에서 처음 고를 땐 여기를 지난다
        private static async Task EnsureProviderReadyAsync(ExecutionProvider ep, IProgress<double> progress)
        {
            if (ep.ReadyState == ExecutionProviderReadyState.Ready) return;
            var op = ep.EnsureReadyAsync();
            op.Progress = (_, p) => progress.Report(p);
            var result = await op;
            // WinRT 작업을 기다리면 그 작업을 끝낸 스레드에서 곧바로 이어진다 — 스레드 풀엔 SynchronizationContext 가 없어 돌아갈 곳이 없다.
            // 그 스레드는 CLR 스레드 풀이 아닐 수 있다 (2026-11-01 콘솔 실측: 스레드 풀에서 WinRT 파일 읽기를 기다리면 대개 CLR 풀이 아닌
            // 스레드에서 이어졌다. EnsureReadyAsync 자체는 재 보지 않음 — EP 상태를 바꾸므로). 뒤의 등록(EP 라이브러리를 올린다)·장치 찾기·
            // 버전·폴더 확인을 그 스레드에 얹지 않게 스레드 풀로 넘긴다 — 컨텍스트가 없으니 Yield 는 스레드 풀에 다시 줄을 선다
            await Task.Yield();
            if (result.Status != ExecutionProviderReadyResultState.Success)
                throw new InvalidOperationException($"실행 공급자 준비 실패: {result.DiagnosticText}");
        }

        /// <summary>
        /// 가속 장치 캐시의 폴더·키 (NPU 컴파일 캐시 · TensorRT 엔진 커널 캐시): %LOCALAPPDATA%\DetectView\&lt;folder&gt; 아래, 키 = 짝 모델 MD5 _ EP 버전 _ ORT 버전.
        /// 버전을 못 읽어 'unknown' 으로 굳은 부분은 그 업데이트를 키가 알아채지 못한다 — 조용히 넘어가지 않고 툴팁·Debug 로 알린다 (KeyWarning)
        /// </summary>
        private static (string CacheDir, string CacheKey, string? KeyWarning) CacheKeyFor(string folder, string modelMd5, ExecutionProvider ep, string what)
        {
            string epPart = KeyPart(EpVersionForKey(ep));
            string ortPart = KeyPart(OrtVersionForKey());
            string cacheDir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DetectView", folder);
            System.IO.Directory.CreateDirectory(cacheDir);
            var unknown = new List<string>();
            if (epPart == UnknownKeyPart) unknown.Add("실행 공급자");
            if (ortPart == UnknownKeyPart) unknown.Add("ONNX Runtime");
            string? keyWarning = unknown.Count == 0 ? null :
                $"{string.Join("·", unknown)} 버전을 몰라 {what}가 그 업데이트를 알아채지 못함 — 업데이트 뒤엔 {cacheDir} 를 지우세요";
            return (cacheDir, $"{modelMd5}_{epPart}_{ortPart}", keyWarning);
        }

        // 폴더에 파일이 하나라도 있는가 — 없거나 못 읽으면 false (문구만 달라진다)
        private static bool HasFiles(string dir)
        {
            try { return System.IO.Directory.EnumerateFiles(dir, "*", System.IO.SearchOption.AllDirectories).Any(); }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) { return false; }
        }

        // 캐시 키의 EP 버전: 보통은 EP 패키지 버전(예: 1.8.80.0). 패키지 ID 를 못 읽으면 EP 라이브러리 파일의 버전·수정 시각으로 대신한다 —
        // 'unknown' 으로 굳으면 EP 가 바뀌어도 키가 그대로라 옛 컴파일 결과를 쓴다(이슈 #5). 패키지 ID·LibraryPath 는 준비 전엔
        // 이 PC 에서도 비어 있다(2026-10-31 콘솔 실측: 새 프로세스의 NotReady EP 6개 모두) → 준비·등록 뒤에 부른다. 둘 다 없으면 null
        private static string? EpVersionForKey(ExecutionProvider ep)
        {
            try
            {
                if (ep.PackageId?.Version is { } v) return $"{v.Major}.{v.Minor}.{v.Build}.{v.Revision}";
            }
            catch (Exception ex) { Debug.WriteLine($"EP 패키지 ID 를 못 읽음: {ex.Message}"); }
            try
            {
                // EP 폴더는 C:\Program Files\WindowsApps\…NPU.EP… 지만 앱이 그 DLL 을 올리므로(읽기 권한이 있다) 버전·시각도 읽힌다
                // (2026-10-31 이 PC 에서 그 폴더 DLL 들의 파일 버전·수정 시각을 읽어 봄 — 시각은 EP 가 설치된 때였다).
                // 시각을 앞에 둔다: KeyPart 가 길이를 자를 때 업데이트마다 바뀌는 쪽이 남게
                string lib = ep.LibraryPath;
                if (!string.IsNullOrEmpty(lib) && System.IO.File.Exists(lib))
                    return "lib" + System.IO.File.GetLastWriteTimeUtc(lib).ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture) +
                           $"-{FileVersionInfo.GetVersionInfo(lib).FileVersion}";
            }
            catch (Exception ex) { Debug.WriteLine($"EP 라이브러리 정보를 못 읽음: {ex.Message}"); }
            return null;
        }

        // 캐시 키의 ORT 버전: 이 프로세스에 올라온 onnxruntime.dll 의 파일 버전 (Windows ML 2.4.89 는 1.27.20260711.1.7b7c0e2 — 빌드 날짜·커밋).
        // GetVersionString() 은 의미 버전("1.27.1")뿐이라 Windows ML 패키지가 같은 번호로 다시 빌드한 ORT 를 넣으면 키가 그대로였다.
        // 파일 경로가 아니라 올라온 모듈에서 찾는다 — 단일 파일 배포본은 onnxruntime.dll 을 임시 폴더(%TEMP%\.net\DetectView\…)에 풀어 올린다.
        // 이름이 같은 다른 ORT 가 올라와도 섞이지 않게(이 PC 엔 Windows 구성 요소용 System32\onnxruntime.dll 1.17 이 있다) 의미 버전과
        // 앞 두 자리가 맞는 것만 본다. 못 찾거나 읽다 실패하면 의미 버전, 그것도 실패하면 null → 키 그 자리가 'unknown' 이 되고
        // KeyWarning 으로 알린다 — 버전을 못 읽었다고 NPU 전환까지 실패하지 않게 (예외를 밖으로 내지 않는다)
        private static string? OrtVersionForKey()
        {
            string? semantic = null;
            try { semantic = OrtEnv.Instance().GetVersionString(); }   // Windows ML 의 관리 어셈블리(0.0.0.0)에도 있다 — 2.4.89 는 "1.27.1"
            catch (Exception ex) { Debug.WriteLine($"ONNX Runtime 버전을 못 읽음: {ex.Message}"); }
            try
            {
                string? prefix = semantic?.Split('.') is [var major, var minor, ..] ? $"{major}.{minor}." : null;
                using var self = Process.GetCurrentProcess();
                foreach (ProcessModule m in self.Modules)
                {
                    if (!string.Equals(m.ModuleName, "onnxruntime.dll", StringComparison.OrdinalIgnoreCase)) continue;
                    string? file = m.FileVersionInfo.FileVersion?.Trim();
                    if (!string.IsNullOrEmpty(file) && (prefix == null || file.StartsWith(prefix, StringComparison.Ordinal))) return file;
                }
                Debug.WriteLine($"올라온 onnxruntime.dll ({prefix}*) 을 못 찾음 — 캐시 키에 의미 버전을 쓴다");
            }
            catch (Exception ex) { Debug.WriteLine($"onnxruntime.dll 파일 버전을 못 읽음 — 캐시 키에 의미 버전을 쓴다: {ex.Message}"); }
            return semantic;
        }

        private const string UnknownKeyPart = "unknown";

        // 키는 VitisAI 가 캐시 폴더 이름으로 쓴다 — 버전 문자열에 섞일 수 있는 공백·괄호·경로 문자를 '-' 로 바꾸고 길이를 묶는다.
        // 끝의 '.' 은 Windows 가 폴더 이름에서 떼어 버려 앱이 보는 키 폴더와 어긋날 수 있어 뗀다
        private static string KeyPart(string? version)
        {
            if (string.IsNullOrWhiteSpace(version)) return UnknownKeyPart;
            var safe = new string(version.Trim().Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' ? c : '-').ToArray());
            safe = safe[..Math.Min(safe.Length, 48)].TrimEnd('.');
            return safe.Length == 0 ? UnknownKeyPart : safe;
        }
    }
}
