using OpenCvSharp;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Vortice.DXGI;
using YoloDotNet;
using YoloDotNet.Extensions;
using YoloDotNet.Models;

namespace DetectView
{
    /// <summary>모델 로드(DirectML·GPU 자동 선택)와 프레임 검출.</summary>
    public partial class MainWindow
    {
        // 검출 임계값
        private const double ConfidenceThreshold = 0.3;
        private const double IouThreshold = 0.7;

        // 관심 클래스 (현재 모델의 7클래스와 동일 — 모델 교체 대비 안전장치)
        private static readonly HashSet<string> TargetClasses =
            ["person", "car", "motorcycle", "truck", "bus", "scooter", "bicycle"];

        private const string ModelFileName = "best.onnx";

        // YOLO 입력(640×640 레터박스)은 YoloLetterbox 가 OpenCV 쌍선형으로 직접 만들어 넘긴다 — 그 파일 머리말 (2026-11-05).
        // 그래서 YoloDotNet 은 모델 입력과 같은 크기를 받아 배율 1 로 그대로 그리고, 아래 축소 방식은 듣지 않는다. 그래도 쌍선형으로 둔다 —
        // 다른 크기를 YoloDotNet 에 바로 넘기는 코드가 생기면 학습(Ultralytics INTER_LINEAR)과 같은 쪽이게.
        // YoloDotNet 기본값은 Nearest 다(문서 주석은 Linear 라고 잘못 적혀 있다). 앱이 Nearest 로 줄이던 때보다 시험 분할 AP 가
        // 0.6555 → 0.6602(YoloDotNet 쌍선형) → 0.6658(지금, YoloLetterbox)로 올랐다 (TrackerCheck testsplit · RfDetrBench README '앱의 쌍선형 축소')
        internal static readonly SKSamplingOptions InputSampling = new(SKFilterMode.Linear, SKMipmapMode.None);

        private static YoloOptions YoloOptionsFor(WindowsMlExecutionProvider provider) =>
            new() { ExecutionProvider = provider, SamplingOptions = InputSampling };

        private Yolo? _yolo;
        private readonly Lock _yoloLock = new();   // 재생 루프와 SAHI의 동시 추론 직렬화
        // 레터박스 버퍼 — 다시 쓰므로 _yoloLock 안에서만 쓴다 (NPU 세션도 같은 크기라 같이 쓴다)
        private readonly YoloLetterbox _letterbox = new();

        private void InitializeYoloModel()
        {
            // 모델은 exe 와 같은 폴더에서 찾는다. 상대 경로("best.onnx")는 작업 디렉터리 기준이라,
            // 바로가기의 "시작 위치"나 다른 폴더에서 실행하면 exe 옆에 있어도 못 찾는다(실측 확인).
            // 단일 파일 배포에서도 AppContext.BaseDirectory 는 exe 가 있는 폴더다
            string modelPath = System.IO.Path.Combine(AppContext.BaseDirectory, ModelFileName);
            if (!System.IO.File.Exists(modelPath))
            {
                // GPU 오류 안내로 넘어가면 원인을 오해하게 되므로 따로 알린다
                MessageBox.Show($"모델 파일을 찾을 수 없습니다:\n{modelPath}\n\n" +
                                $"{ModelFileName} 을 DetectView.exe 와 같은 폴더에 두세요.");
                return;
            }

            try
            {
                (_yolo, _gpuName) = CreateGpuYolo(modelPath);
                Title = GpuTitle(_yolo);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"YOLO 모델 로드(GPU/DirectML) 중 오류가 발생했습니다:\n{ex.Message}\n\n" +
                                "GPU 드라이버와 DirectX 12 지원 여부를 확인하세요.");
            }
        }

        // GPU 로 돌 때의 창 제목. 테스트가 "(DirectML)" 로 GPU 초기화 성공을 확인하므로 형식을 바꾸지 말 것
        private string GpuTitle(Yolo yolo) => $"DetectView — {DescribeModel(yolo)} — {_gpuName} (DirectML)";

        // DirectML(GPU) 추론 — 외장 GPU 를(가릴 수 없으면 전용 VRAM 이 가장 큰 GPU) 골라, Windows ML 의 DirectML 장치와 PCI ID 로 맞춘다
        private static (Yolo Yolo, string GpuName) CreateGpuYolo(string modelPath)
        {
            var (gpuName, vendorId, deviceId) = SelectBestGpu();
            var device = WindowsMlExecutionProvider.FindDmlDevice(vendorId, deviceId);
            if (device == null)
            {
                device = WindowsMlExecutionProvider.FirstDmlDevice()
                         ?? throw new InvalidOperationException("DirectML 장치를 찾지 못했습니다.");
                gpuName = $"{device.HardwareDevice.Vendor} GPU";   // 고른 것과 다른 장치로 돈다는 걸 제목에 드러낸다
            }
            var yolo = new Yolo(YoloOptionsFor(new WindowsMlExecutionProvider(modelPath, device)));
            return (yolo, gpuName);
        }

        // 제목에 표시할 모델 이름. 배포 폴더의 best.onnx 를 수시로 바꿔 쓰므로(YOLO26s ↔ YOLO26x 등)
        // 제목만 보고도 지금 어느 모델로 도는지 알 수 있어야 한다 (모르고 잰 속도를 엉뚱한 모델 탓으로 돌린 적이 있음).
        // YoloDotNet 이 이미 읽어 둔 ONNX 메타데이터를 쓰므로 모델을 다시 읽지 않는다
        private static string DescribeModel(Yolo yolo)
        {
            var meta = yolo.OnnxModel?.CustomMetaData;

            // Ultralytics 내보내기: description = "Ultralytics YOLO26s model trained on .../data.yaml".
            // ModelInfo("ObjectDetection (yolo v26)")에는 s/x 같은 크기가 없어서 이쪽을 쓴다
            string name = meta != null && meta.TryGetValue("description", out var desc)
                          && Regex.Match(desc, @"Ultralytics\s+(\S+)\s+model") is { Success: true } m
                ? m.Groups[1].Value
                : yolo.ModelInfo;   // 메타데이터가 없는 모델

            // 크기가 같은 모델이 여럿이라(데이터셋만 다른 YOLO26s 등) 학습일로 구분한다
            if (meta != null && meta.TryGetValue("date", out var date)
                && DateTime.TryParse(date, CultureInfo.InvariantCulture, DateTimeStyles.None, out var trained))
                name += $" ({trained:yyyy-MM-dd} 학습)";
            return name;
        }

        // 추론 GPU: 내장이 아닌 GPU 중 전용 VRAM 이 가장 큰 것 (GpuAdapters.Best — HUD 안내의 고성능/저전력과 같은 판정).
        // 전용 VRAM 만 보던 예전엔 내장 GPU 의 전용 메모리를 외장 이상으로 잡은 PC(AMD VGM·BIOS UMA)에서 내장 GPU 로 옮겨 갔다 (이슈 #3).
        // 순번이 아니라 PCI 벤더·장치 ID 를 돌려준다 — Windows ML 의 EP 장치 목록은 DXGI 와 순서가 같다는 보장이 없다
        private static (string name, uint vendorId, uint deviceId) SelectBestGpu()
        {
            try
            {
                if (GpuAdapters.Best(GpuAdapters.Enumerate()) is { } best)
                    return (best.Name, best.VendorId, best.DeviceId);
            }
            catch { /* 실패하면 ID 0 → 호출 측이 첫 DirectML 장치로 대체 */ }
            return ("GPU", 0, 0);
        }

        // ---------- 프레임 처리 (BGR Mat → 검출 → BitmapSource + 박스) ----------
        // 박스는 영상에 그리지 않고 돌려준다 — 화면에선 영상 위 BoxLayer(FrameBoxes)가 HUD 와 같은 모양으로 그린다
        private BitmapSource ProcessFrame(Mat frame, out List<Det> dets)
        {
            int width = frame.Width;
            int height = frame.Height;

            // 1) BGR → BGRA (화면 표시용 BitmapSource 와 검출이 같이 쓴다)
            using var bgra = new Mat();
            Cv2.CvtColor(frame, bgra, ColorConversionCodes.BGR2BGRA);
            int stride = (int)bgra.Step();

            // 2) YOLO 추론 (레터박스는 YoloLetterbox — DetectBgra). 관심 클래스만 남기고 같은 자리 중복 박스를 뺀다.
            //    모델이 end-to-end NMS 라 일반 NMS 는 다시 돌리지 않지만, 같은 물체에 박스를 둘 이상 내는 일이 잦아(DuplicateBoxes)
            //    그대로 두면 겹쳐 그려지고 검출 수가 부푼다
            dets = DuplicateBoxes.Remove(DetectBgra(bgra, 0, 0));

            // 3) Mat 메모리 → BitmapSource (네이티브 메모리에서 바로 1회 복사).
            //    중간 byte[] 를 두면 프레임마다 수 MB 가 LOH 에 할당돼 Gen2 GC 가 잦아진다
            var bs = BitmapSource.Create(
                width, height, 96, 96, PixelFormats.Pbgra32, null, bgra.Data, height * stride, stride);
            bs.Freeze();
            return bs;
        }

        private List<Det> DetectTile(Mat tileBgr, int offsetX, int offsetY)
        {
            using var bgra = new Mat();
            Cv2.CvtColor(tileBgr, bgra, ColorConversionCodes.BGR2BGRA);
            return DetectBgra(bgra, offsetX, offsetY);
        }

        // BGRA(4채널) 이미지를 검출한다 — 파일·카메라(ProcessFrame)·화면 HUD(캡처 버퍼가 이미 BGRA)·SAHI(DetectTile) 가 모두 여기로 온다.
        // 레터박스(OpenCV 쌍선형)와 박스 되돌리기는 YoloLetterbox. 알파는 보지 않는다 — 캡처 버퍼의 알파 값과 무관하게 결과가 같다.
        // 레터박스 버퍼와 YoloDotNet 의 결과 리스트(다음 호출이 다시 쓴다)를 함께 쓰므로 둘 다 락 안에서 다룬다
        private List<Det> DetectBgra(Mat bgra, int offsetX, int offsetY)
        {
            List<YoloLetterbox.Box> boxes;
            lock (_yoloLock)
            {
                boxes = _letterbox.Detect(_yolo!, bgra, ConfidenceThreshold, IouThreshold);
            }

            // 같은 자리 중복 박스는 여기서 빼지 않는다 — HUD 는 받자마자, SAHI 는 타일 병합(MergeCutPieces) 뒤에 뺀다.
            // SAHI 타일마다 먼저 빼면 병합 입력이 바뀌어, 중복이 아닌 박스가 빠지거나 병합이 지웠던 박스가 되살아났다
            // (test4·test5 실측 각 1건씩. experiments/TrackerCheck dedup)
            var list = new List<Det>(boxes.Count);
            foreach (var b in boxes)
                if (TargetClasses.Contains(b.Label)) list.Add(new Det(b.X + offsetX, b.Y + offsetY, b.W, b.H, b.Label, b.Conf));
            return list;
        }
    }
}
