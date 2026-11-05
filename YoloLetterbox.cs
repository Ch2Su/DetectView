using System;
using System.Collections.Generic;
using System.Linq;
using OpenCvSharp;
using SkiaSharp;
using YoloDotNet;
using YoloDotNet.Extensions;

namespace DetectView
{
    /// <summary>
    /// YOLO 입력 레터박스를 OpenCV 쌍선형(INTER_LINEAR)으로 직접 만들어 YoloDotNet 에 넘기고, 박스를 원래 좌표로 되돌린다 (2026-11-05).
    /// 앱(MainWindow.Inference.cs DetectBgra)과 실험 도구(TrackerCheck·RuntimeParity)가 이 파일을 같이 쓴다 — 실험이 잰 것이 곧 앱이 하는 것이게. WPF 없음.
    ///
    /// 왜: Ultralytics 는 학습·검증 모두 쌍선형으로 줄인다. YoloDotNet 에 맡기면(ResizeImageProportional — Skia DrawImage) 쌍선형이 한 번에 1.5~2.2 ms 이고,
    /// 박스 좌표를 정수로 내리며(`(int)((x − xPad) × gain)` — 평균 −0.5 px) 레터박스를 정수 위치에 그리고 실수 여백(xPad)으로 되돌려 어긋난다(1280×854 면 세로 1 px).
    /// 여기서 같은 크기·위치의 레터박스를 OpenCV 로 만들면(0.1~0.2 ms) 모델 입력은 Skia 쌍선형과 화소까지 같고(720p·1440p 다른 화소 0),
    /// YoloDotNet 은 모델 입력과 같은 크기라 배율 1 로 그대로 그린다. 그러면 박스가 레터박스 좌표의 정수로 오므로 +0.5 하고
    /// 그린 자리 그대로의 여백·배율로 되돌린다 — 데이터셋 시험 분할 AP 0.6602 → 0.6658, 검출 한 번 8.25 → 7.40 ms (TrackerCheck testsplit · inputbench).
    /// 대가: 정수 칸이 원래 크기에서 배율만큼 커진다(720p 2 px · 1440p 4 px). +0.5 를 빼면 AP 가 0.6535 로 떨어진다 — 빼지 말 것.
    ///
    /// 크기 규칙은 YoloDotNet 과 같다: 모델보다 작은 이미지(가로·세로 모두 작음)는 키우지 않고 가운데에 둔다, 아니면 비율 유지 (int)(w·s + 0.5), 가운데, 검은 여백.
    /// tools/npu_quantize.py(NPU 짝의 보정)·experiments/NpuBench 의 레터박스도 같은 규칙·같은 보간이다.
    /// 버퍼를 다시 쓰므로 한 번에 하나만 부른다(앱은 _yoloLock 안에서). 같은 입력은 같은 출력이다(HUD 의 '새 화면 고리'가 기대는 성질)
    /// </summary>
    public sealed class YoloLetterbox : IDisposable
    {
        /// <summary>검출 한 개 — 좌표는 넘긴 이미지 기준의 실수(잘라 내지 않는다 — 이미지 밖으로 조금 나갈 수 있다)</summary>
        public readonly record struct Box(string Label, double Conf, double X, double Y, double W, double H);

        private Mat? _canvas;            // 모델 입력 크기 BGRA — 여백은 검정
        private SKBitmap? _skia;         // _canvas 메모리를 감싼 것 (복사 없음)
        private readonly Mat _small = new();
        private OpenCvSharp.Rect _content;           // 지난번 내용 자리 — 바뀌면(SAHI 의 전체 프레임 ↔ 타일 등) 여백을 다시 칠한다

        /// <summary>
        /// BGRA 이미지(앱의 재생 프레임 변환본·HUD 캡처 버퍼·SAHI 타일)에서 검출한다. 알파는 보지 않는다(캡처 버퍼의 알파가 0 이어도 된다).
        /// confidence 미만은 YoloDotNet 이 버린다. iou 는 end-to-end 모델엔 듣지 않는다(CLAUDE.md 3번)
        /// </summary>
        public List<Box> Detect(Yolo yolo, Mat bgra, double confidence, double iou)
        {
            var (mw, mh) = InputSize(yolo);
            if (_canvas == null || _canvas.Width != mw || _canvas.Height != mh) CreateCanvas(mw, mh);

            int w = bgra.Width, h = bgra.Height, nw = w, nh = h;
            if (!(w < mw && h < mh))
            {
                float s = Math.Min((float)mw / w, (float)mh / h);   // YoloDotNet 과 같게 float 로
                (nw, nh) = ((int)(w * s + 0.5f), (int)(h * s + 0.5f));
            }
            var content = new OpenCvSharp.Rect((mw - nw) / 2, (mh - nh) / 2, nw, nh);
            if (content != _content)
            {
                _canvas!.SetTo(new Scalar(0, 0, 0, 255));
                _content = content;
            }
            using (var roi = new Mat(_canvas!, content))
            {
                if ((nw, nh) == (w, h)) bgra.CopyTo(roi);
                else
                {
                    Cv2.Resize(bgra, _small, new OpenCvSharp.Size(nw, nh), 0, 0, InterpolationFlags.Linear);
                    _small.CopyTo(roi);
                }
            }

            // YoloDotNet 은 결과 리스트를 다음 호출에서 비우고 다시 채운다 — 여기서 바로 옮긴다
            var results = yolo.RunObjectDetection(_skia!, confidence: confidence, iou: iou);
            double fx = (double)w / nw, fy = (double)h / nh;
            var list = new List<Box>(results.Count);
            foreach (var r in results)
            {
                var b = r.BoundingBox;   // 레터박스(모델 입력) 좌표를 YoloDotNet 이 (int) 로 내린 것 → +0.5 가 가장 가까운 값
                double x1 = (b.Left + 0.5 - content.X) * fx, y1 = (b.Top + 0.5 - content.Y) * fy;
                double x2 = (b.Right + 0.5 - content.X) * fx, y2 = (b.Bottom + 0.5 - content.Y) * fy;
                list.Add(new Box(r.Label.Name, r.Confidence, x1, y1, x2 - x1, y2 - y1));
            }
            return list;
        }

        /// <summary>
        /// 모델 입력 크기 (가로, 세로) — ONNX 입력 [1,3,H,W]. 사용자가 모델을 바꿔 쓰므로 고정하지 않는다.
        /// 정사각형이 아닐 수 있다 — 지금 루트 모델은 16:9 영상용 640×384 (2026-11-06, YOLO26m. YoloDotNet 이 직사각형 입력을 그대로 받는 것은 TrackerCheck testsplit 으로 확인)
        /// </summary>
        public static (int W, int H) InputSize(Yolo yolo)
        {
            var shape = yolo.OnnxModel.InputShapes.First().Value;
            return ((int)shape[3], (int)shape[2]);
        }

        private void CreateCanvas(int mw, int mh)
        {
            _skia?.Dispose();
            _canvas?.Dispose();
            _canvas = new Mat(mh, mw, MatType.CV_8UC4, new Scalar(0, 0, 0, 255));
            _skia = new SKBitmap();
            // Opaque — 알파를 보지 않는다. Premul 이면 알파 0 인 캡처 화소가 투명(검정)으로 그려진다
            if (!_skia.InstallPixels(new SKImageInfo(mw, mh, SKColorType.Bgra8888, SKAlphaType.Opaque), _canvas.Data, (int)_canvas.Step()))
                throw new InvalidOperationException("레터박스 버퍼를 SKBitmap 으로 감싸지 못했습니다");
            _content = default;
        }

        public void Dispose()
        {
            _skia?.Dispose();
            _skia = null;
            _canvas?.Dispose();
            _canvas = null;
            _small.Dispose();
        }
    }
}
