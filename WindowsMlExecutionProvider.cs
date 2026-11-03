// SPDX-License-Identifier: MIT
// 바탕: YoloDotNet.ExecutionProvider.DirectML 1.0 의 DirectMLExecutionProvider
//       (MIT, Copyright (c) Niklas Swärd, https://github.com/NickSwardh/YoloDotNet — 라이선스 전문은 THIRD-PARTY-NOTICES.md).
//       세션에 실행 공급자를 붙이는 부분만 Windows ML 의 EP 장치 API 로 바꿨다.

using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using YoloDotNet.Core;
using YoloDotNet.Enums;
using YoloDotNet.Extensions;
using YoloDotNet.Models;
using YoloDotNet.Models.Interfaces;

namespace DetectView
{
    /// <summary>
    /// Windows ML 에 들어 있는 ONNX Runtime 으로 도는 YoloDotNet 실행 공급자.
    /// 패키지 YoloDotNet.ExecutionProvider.DirectML 은 Microsoft.ML.OnnxRuntime.DirectML 1.23 에 묶여 있는데,
    /// Windows ML 은 같은 이름·같은 서명의 Microsoft.ML.OnnxRuntime(버전 0.0.0.0)과 onnxruntime.dll(1.27)을 가져와
    /// 한 프로세스에 둘 수 없다 → 같은 일을 하는 공급자를 직접 둔다.
    /// GPU 는 Windows ML 에 들어 있는 DirectML 을 쓴다 (EP 카탈로그에서 내려받을 필요 없음).
    /// YoloDotNet 의 ParseOnnx 는 세션을 리플렉션으로 읽으므로(InputMetadata 등 속성 이름만 맞으면 됨) 그대로 쓸 수 있다.
    /// </summary>
    public sealed class WindowsMlExecutionProvider : IExecutionProvider, IDisposable
    {
        private const string DmlEpName = "DmlExecutionProvider";

        public OnnxModel OnnxData { get; }
        public object Session => _session;

        private readonly InferenceSession _session;
        private readonly RunOptions _runOptions = new();
        private readonly long[] _inputShape;
        private readonly string[] _inputNames;
        private readonly string[] _outputNames;
        private readonly int _inputShapeSize;
        private float[] _outputBuffer0 = [];
        private float[] _outputBuffer1 = [];
        private IDisposableReadOnlyCollection<OrtValue>? _currentResult;

        public WindowsMlExecutionProvider(string modelPath, OrtEpDevice device,
                                          IReadOnlyDictionary<string, string>? epOptions = null)
        {
            EnsureEnv();
            using var options = new SessionOptions
            {
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                // DirectML 은 병렬 실행·메모리 패턴을 지원하지 않는다
                ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
                EnableMemoryPattern = device.EpName != DmlEpName,
                EnableCpuMemArena = true,
            };
            options.AppendExecutionProvider(OrtEnv.Instance(), [device], epOptions ?? new Dictionary<string, string>());
            _session = new InferenceSession(modelPath, options);

            // 세션을 만든 뒤 모델 해석에서 실패하면 세션(GPU·NPU 자원)을 놓고 넘긴다 — 생성자가 실패하면 Dispose 를 부를 곳이 없다
            try
            {
                OnnxData = _session.ParseOnnx();
                var firstInput = OnnxData.InputShapes.FirstOrDefault();
                if (firstInput.Key is null)
                    throw new InvalidOperationException("모델에 입력이 없습니다 (손상됐거나 호환되지 않는 모델).");
                _inputNames = [firstInput.Key];
                _inputShape = firstInput.Value;
                _inputShapeSize = OnnxData.InputShapeSize;
                _outputNames = [.. OnnxData.OutputShapes.Keys];
                AllocateOutputBuffers();
            }
            catch
            {
                _session.Dispose();
                _runOptions.Dispose();
                throw;
            }
        }

        /// <summary>
        /// DXGI 어댑터의 PCI 벤더·장치 ID 로 DirectML EP 장치를 찾는다.
        /// 하이브리드 노트북이라 DirectML 장치가 AMD 내장·NVIDIA 둘이다 — 어댑터 순번이 아니라 ID 로 맞춘다.
        /// 못 찾으면 null (호출 측이 대체 장치를 고른다)
        /// </summary>
        public static OrtEpDevice? FindDmlDevice(uint vendorId, uint deviceId)
        {
            EnsureEnv();
            return OrtEnv.Instance().GetEpDevices().FirstOrDefault(d =>
                d.EpName == DmlEpName && d.HardwareDevice.VendorId == vendorId && d.HardwareDevice.DeviceId == deviceId);
        }

        /// <summary>ID 로 못 찾았을 때의 대체: 첫 DirectML GPU (없으면 null)</summary>
        public static OrtEpDevice? FirstDmlDevice()
        {
            EnsureEnv();
            return OrtEnv.Instance().GetEpDevices().FirstOrDefault(d => d.EpName == DmlEpName);
        }

        /// <summary>등록된 실행 공급자의 장치 (예: VitisAIExecutionProvider · NPU). EP 카탈로그에서 등록한 뒤에야 보인다</summary>
        public static OrtEpDevice? FindEpDevice(string epName, OrtHardwareDeviceType type)
        {
            EnsureEnv();
            return OrtEnv.Instance().GetEpDevices().FirstOrDefault(d => d.EpName == epName && d.HardwareDevice.Type == type);
        }

        // 오류·치명만 로그로 남긴다 (YoloDotNet 공급자와 같게). 장치를 찾을 때 OrtEnv 가 기본 설정으로 먼저 만들어지지 않도록
        // 장치 조회·세션 생성·EP 카탈로그 등록 모두 여기를 먼저 거친다
        internal static void EnsureEnv()
        {
            try
            {
                var envOptions = new EnvironmentCreationOptions { logLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR };
                OrtEnv.CreateInstanceWithOptions(ref envOptions);
            }
            catch (OnnxRuntimeException ex) when (ex.Message.Contains("already exists"))
            {
                // 이미 만들어져 있으면 그대로 쓴다
            }
        }

        public unsafe InferenceResult Run<T>(T[] normalizedPixels) where T : unmanaged
        {
            fixed (T* pData = normalizedPixels)
            {
                var elementType = typeof(T) == typeof(float) ? TensorElementType.Float : TensorElementType.Float16;
                using var input = OrtValue.CreateTensorValueWithData(
                    OrtMemoryInfo.DefaultInstance, elementType, _inputShape, (IntPtr)pData, _inputShapeSize * sizeof(T));

                // 앞 결과의 스팬은 YoloDotNet 이 이미 다 읽었다 — 새 추론 전에 해제
                _currentResult?.Dispose();
                _currentResult = _session.Run(_runOptions, _inputNames, [input], _outputNames);

                if (elementType == TensorElementType.Float)
                {
                    var out0 = _currentResult[0].GetTensorDataAsSpan<float>();
                    var out1 = _currentResult.Count == 2 ? _currentResult[1].GetTensorDataAsSpan<float>() : ReadOnlySpan<float>.Empty;
                    return new InferenceResult(out0, out1);
                }

                // FP16 모델: 결과를 미리 잡아 둔 float 버퍼로 옮긴다
                var h0 = _currentResult[0].GetTensorDataAsSpan<Float16>();
                var h1 = _currentResult.Count == 2 ? _currentResult[1].GetTensorDataAsSpan<Float16>() : ReadOnlySpan<Float16>.Empty;
                for (int i = 0; i < h0.Length; i++) _outputBuffer0[i] = (float)h0[i];
                for (int i = 0; i < h1.Length; i++) _outputBuffer1[i] = (float)h1[i];
                return new InferenceResult(_outputBuffer0, _outputBuffer1);
            }
        }

        // FP16 모델일 때만 출력 버퍼를 미리 잡는다 (프레임마다 할당하지 않게). 크기는 모든 차원의 곱 —
        // YoloDotNet 원본 공급자처럼 앞 차원 두 개만 곱하면 분할 마스크 [1,32,160,160] 같은 4차원 출력에서 Run 이 범위를 넘는다
        private void AllocateOutputBuffers()
        {
            if (OnnxData.ModelDataType == ModelDataType.Float) return;

            _outputBuffer0 = new float[ElementCount(OnnxData.OutputShapes.ElementAt(0).Value)];
            if (OnnxData.OutputShapes.Count == 2)
                _outputBuffer1 = new float[ElementCount(OnnxData.OutputShapes.ElementAt(1).Value)];
        }

        // 동적 차원(-1·0)은 1 로 본다 — 이 앱의 모델은 입출력이 고정이다
        private static int ElementCount(int[] shape) => shape.Aggregate(1, (n, d) => n * Math.Max(1, d));

        public void Dispose()
        {
            _currentResult?.Dispose();
            _session.Dispose();
            _runOptions.Dispose();
        }
    }
}
