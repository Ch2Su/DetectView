using Microsoft.ML.OnnxRuntime;
using Microsoft.Windows.AI.MachineLearning;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using YoloDotNet;

namespace DetectView
{
    /// <summary>
    /// 추론 장치 TensorRT — 같은 NVIDIA GPU 를 DirectML 대신 NVIDIA TensorRT for RTX 로 쓴다 (2026-11-08).
    /// Windows ML EP 카탈로그에 NPU 의 VitisAI 처럼 들어오는 NvTensorRTRTXExecutionProvider 라 새 NuGet 이 없다(CLAUDE.md '절대 건드리면 안 되는 것들' 1번).
    /// 길은 NPU 와 같다(MainWindow.Npu.cs 의 장치 전환): ① FP16 짝 모델 확인(best.fp16.onnx — tools/Make-Fp16Model.ps1) →
    /// ② EP 준비·등록과 엔진 커널 캐시 확인 → ③ 세션 생성(= 엔진 만들기 — 처음 약 10초, 캐시가 있으면 수 초). 모두 스레드 풀에서.
    /// TensorRT for RTX 는 모델에 적힌 정밀도대로 돈다 — FP32 원본 그대로면 1.5배, FP16 짝이면 약 3.5배 빠르다
    /// (session.Run 10.0 → 6.6 · 2.8 ms, 검출은 DirectML 대비 99.6% / 99.9% — experiments/NpuBench README 결과 6). 그래서 짝으로 돈다.
    /// 대가: session.Run 동안 그 스레드가 GPU 를 기다리며 돈다(1회당 CPU 시간 ≈ 추론 시간 — DirectML 은 1 ms 안팎) ·
    /// 처음 엔진 만들기 · 짝 파일(41 MB, DirectML 로는 못 연다) · NVIDIA RTX(Ampere 이후)에서만 EP 가 나온다.
    /// 같은 입력이면 출력이 비트까지 같다(세션 안 — NpuBench) — HUD 의 '결과가 같으면 다시 그리지 않음'(BoxLayer.LooksSame)이 그대로 된다
    /// </summary>
    public partial class MainWindow
    {
        private const string TrtEpName = "NvTensorRTRTXExecutionProvider";

        /// <summary>FP16 짝 확인 결과. Ok 면 ModelMd5(캐시 키)가 차 있다</summary>
        private sealed record TrtModelCheck(bool Ok, string Message, string ModelPath, string? ModelMd5 = null);

        // best.onnx → best.fp16.onnx (tools/Make-Fp16Model.ps1 이 원본 옆에 만드는 이름)
        private static string TrtModelPath =>
            System.IO.Path.Combine(AppContext.BaseDirectory, System.IO.Path.ChangeExtension(ModelFileName, ".fp16.onnx"));

        /// <summary>
        /// best.fp16.onnx 가 지금 best.onnx 를 FP16 으로 바꾼 짝인지 확인한다 (tools/fp16_convert.py 가 넣은 메타데이터 fp16_source_md5).
        /// 배포 폴더의 best.onnx 를 수시로 바꿔 쓰므로, 옛 짝이 남아 있으면 엉뚱한 모델이 TensorRT 로 돈다 — NPU 짝(CheckNpuModel)과 같은 까닭
        /// </summary>
        private static TrtModelCheck CheckTrtModel(string modelPath)
        {
            string path = TrtModelPath;
            string name = System.IO.Path.GetFileName(path);
            if (!System.IO.File.Exists(path))
                return new(false, $"{name} 없음 — tools\\Make-Fp16Model.ps1 로 만드세요", path);

            // 메타데이터만 읽는다 (CPU·그래프 최적화 끔 — 여기서는 엔진을 만들지 않는다. FP16 그래프도 CPU 로 열린다 — tools/fp16_convert.py 가 확인)
            Dictionary<string, string> meta;
            using (var so = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_DISABLE_ALL })
            using (var probe = new InferenceSession(path, so))
                meta = new Dictionary<string, string>(probe.ModelMetadata.CustomMetadataMap);

            if (!meta.TryGetValue("fp16_source_md5", out var pair))
                return new(false, $"{name} 가 FP16 짝 모델이 아님 — tools\\Make-Fp16Model.ps1 로 만드세요", path);
            if (!string.Equals(pair, Md5(modelPath), StringComparison.OrdinalIgnoreCase))
                return new(false, $"{ModelFileName} 가 바뀌어 {name} 와 짝이 안 맞음 — tools\\Make-Fp16Model.ps1 로 다시 만드세요", path);
            return new(true, "FP16", path, Md5(path));
        }

        /// <summary>시작 때 확인 결과를 항목·툴팁에 (InitInferenceDeviceAsync). error = 확인이 예외로 끝났을 때의 사유</summary>
        private void ApplyTrtCheck(TrtModelCheck? check, string? error, ExecutionProviderReadyState? epState)
        {
            if (error != null) SetTrtUnavailable($"확인 실패: {error}");
            else if (check == null || !check.Ok) SetTrtUnavailable(check?.Message ?? "확인 실패");
            else if (epState == null) SetTrtUnavailable("이 PC 에서 쓸 수 있는 NVIDIA TensorRT 실행 공급자가 없음 (NVIDIA RTX 30 이후 GPU 에서만)");
            else
            {
                _trtAvailable = true;
                _trtInfo = "NVIDIA TensorRT (FP16 짝 모델) — 같은 GPU 를 DirectML 보다 약 3배 빠르게 쓴다. 대신 추론하는 동안 CPU 코어 하나가 더 돈다.\n" +
                           "처음 고르면 엔진을 만드느라 약 10초 걸린다 (다음부터는 수 초). 그동안은 GPU 가 그대로 돈다" +
                           (epState == ExecutionProviderReadyState.NotPresent
                               ? ".\n실행 공급자를 먼저 내려받는다 (처음 한 번, 수 분)" : "");
            }
        }

        private void SetTrtUnavailable(string reason)
        {
            _trtAvailable = false;
            _trtInfo = $"TensorRT 를 쓸 수 없음: {reason}";
            _trtCacheKey = null;   // 지난 준비의 키는 더 뜻이 없다 — 툴팁 끝이 사유가 되게
        }

        /// <summary>TensorRT 준비 ①②③ — 새 Yolo 와 그때의 창 제목. 실패는 예외로 (SwitchToAsync 가 지금 장치에 머문다)</summary>
        private async Task<(Yolo Yolo, string Title)> PrepareTrtYoloAsync()
        {
            SetPrepStage("TensorRT 확인 중");
            var check = await Task.Run(() => CheckTrtModel(ModelPath));
            if (!check.Ok)
            {
                SetTrtUnavailable(check.Message);   // 짝 모델 문제는 고치기 전까진 다시 해도 같다 → 항목을 닫는다
                throw new InvalidOperationException(check.Message);
            }

            // ② 는 NPU 와 같은 까닭으로 스레드 풀에서 (Progress 는 UI 스레드에서 만든다 — PrepareNpuYoloAsync 주석)
            var progress = new Progress<double>(p => SetPrepStage($"TensorRT 실행 공급자 내려받는 중 {p:F0}%", resetClock: false));
            var prep = await Task.Run(() => PrepareTensorRtAsync(check.ModelMd5!, progress));
            _trtCacheKey = prep.CacheKey;   // 툴팁에 보인다 (아래 SetPrepStage 가 갱신)
            if (prep.KeyWarning != null)
            {
                Debug.WriteLine($"TensorRT 캐시 키 {prep.CacheKey}: {prep.KeyWarning}");
                if (!_trtInfo.Contains(prep.KeyWarning)) _trtInfo += $"\n※ {prep.KeyWarning}";
            }
            SetPrepStage(prep.Cached ? "TensorRT 준비 중" : "TensorRT 엔진 만드는 중 (첫 실행 약 10초)");

            var sw = Stopwatch.StartNew();
            // 입력은 GPU 와 같은 YoloLetterbox(DetectBgra) — FP16 짝도 입출력은 FP32 라(keep_io_types) YoloDotNet 이 그대로 넣고 읽는다
            var trtYolo = await Task.Run(() => new Yolo(YoloOptionsFor(
                new WindowsMlExecutionProvider(check.ModelPath, prep.Device, prep.EpOptions))));
            Debug.WriteLine($"TensorRT 세션 {sw.Elapsed.TotalSeconds:F1}초 (캐시 {(prep.Cached ? "있음" : "없음")}, 키 {prep.CacheKey})");
            return (trtYolo, $"DetectView — {DescribeModel(trtYolo)} — {prep.GpuName} (TensorRT · FP16)");
        }

        /// <summary>② 의 결과 — TensorRT 장치·EP 옵션(엔진 커널 캐시)·키·캐시 유무·키 경고·그 GPU 의 이름 (평범한 값만 — NpuPrep 과 같게)</summary>
        private sealed record TrtPrep(OrtEpDevice Device, IReadOnlyDictionary<string, string> EpOptions, string CacheKey, bool Cached,
                                      string? KeyWarning, string GpuName);

        /// <summary>
        /// ② TensorRT for RTX EP 만 준비·등록해 GPU 장치를 찾고 엔진 커널 캐시 옵션을 정한다. 스레드 풀에서 부른다 (PrepareVitisAiAsync 와 같은 규칙)
        /// </summary>
        private static async Task<TrtPrep> PrepareTensorRtAsync(string fp16Md5, IProgress<double> progress)
        {
            var ep = FindProvider(TrtEpName)
                     ?? throw new InvalidOperationException("이 PC 에 맞는 NVIDIA TensorRT 실행 공급자가 없음 (NVIDIA RTX 30 이후 GPU 에서만)");
            await EnsureProviderReadyAsync(ep, progress);
            ep.TryRegister();   // 이미 등록돼 있으면 false 일 수 있다 — 장치가 보이는지로 판단한다
            var device = WindowsMlExecutionProvider.FindEpDevice(TrtEpName, OrtHardwareDeviceType.GPU)
                         ?? throw new InvalidOperationException("TensorRT 장치를 찾지 못함 (실행 공급자 등록 실패)");

            // 엔진 커널 캐시(nv_runtime_cache_path — JIT 로 만든 커널, 2026-11-08 NpuBench: 첫 세션 약 10초 → 캐시 뒤 1.5~3초).
            // 키 폴더 = FP16 짝 MD5 _ EP 버전 _ ORT 버전 — NPU 컴파일 캐시와 같은 까닭으로 셋 중 하나가 바뀌면 새 폴더에 다시 만든다
            // (EP 가 캐시를 엔진마다 나누긴 하지만, 옛 결과를 다른 모델·판에 쓰는 일을 VitisAI 에서 겪어 여기서도 폴더로 가른다)
            var (cacheRoot, cacheKey, keyWarning) = CacheKeyFor("trt_cache", fp16Md5, ep, "TensorRT 엔진 캐시");
            string keyDir = System.IO.Path.Combine(cacheRoot, cacheKey);
            System.IO.Directory.CreateDirectory(keyDir);
            var epOptions = new Dictionary<string, string>
            {
                ["enable_cuda_graph"] = "1",   // EP 기본값 — 이 모델에선 켜는 편이 조금 빨랐다 (6.6 vs 7.1 ms, 실행마다 흔들림이 그만큼)
                ["nv_runtime_cache_path"] = keyDir,
            };

            // 제목의 GPU 이름 — DXGI 어댑터를 PCI ID 로 맞춘다 (EP 장치 정보엔 제조사뿐이다)
            var hw = device.HardwareDevice;
            string gpuName = GpuAdapters.Enumerate().FirstOrDefault(a => a.VendorId == hw.VendorId && a.DeviceId == hw.DeviceId).Name is { Length: > 0 } n
                ? n : $"{hw.Vendor} GPU";
            return new(device, epOptions, cacheKey, HasFiles(keyDir), keyWarning, gpuName);
        }
    }
}
