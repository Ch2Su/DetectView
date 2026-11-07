using System;
using OpenCvSharp;

namespace DetectView
{
    /// <summary>VLM 에 보낼 그림 영역 — 프레임에서 복제한 정사각형과 그 안의 후보 박스(정수 좌표)</summary>
    internal sealed class VlmRegion(Mat image, Point box1, Point box2) : IDisposable
    {
        public Mat Image { get; } = image;

        /// <summary>박스 왼쪽 위·오른쪽 아래 (영역 기준) — 실험처럼 원본 좌표를 반올림한 뒤 영역 원점만큼 옮긴 값</summary>
        public Point Box1 { get; } = box1;
        public Point Box2 { get; } = box2;

        public void Dispose() => Image.Dispose();
    }

    /// <summary>
    /// 유고 후보 주변을 VLM 에 보낼 그림으로 만든다. **실험(experiments/VlmCheck/make_cases.py 의 crop_around)과 같은 그림이어야
    /// 실험 결과가 앱에서도 맞는다**: 박스 중심에 한 변 = max(256, 박스 긴 변 × 3) 정사각형(프레임 밖은 자름), 박스를 빨간(BGR 0,0,255) 2px 로 그리고,
    /// 긴 변 512px 로(키우면 Cubic · 줄이면 Area), JPEG 품질 90. 바꾸면 VlmCheck 로 다시 재고 `TrackerCheck vlm` 으로 둘이 같은지 본다.
    /// 자르기(<see cref="Cut"/>)는 재생 루프에서 — 버퍼가 다음 프레임에 재사용되니 영역만 복제한다(0.1 ms 미만). 그리기·축소·JPEG(<see cref="Encode"/>)은 VLM 작업 스레드에서
    /// </summary>
    internal static class VlmCrop
    {
        public const double Factor = 3.0;
        public const int MinSide = 256;
        public const int LongSide = 512;
        public const int Thickness = 2;
        public const int JpegQuality = 90;

        /// <summary>[루프] 프레임(BGR)에서 그림 영역을 복제한다. 박스가 비었거나 영역이 너무 작으면 null</summary>
        public static VlmRegion? Cut(Mat frame, Det box)
        {
            if (frame.Empty() || box.W <= 0 || box.H <= 0) return null;
            double bx1 = box.X, by1 = box.Y, bx2 = box.X + box.W, by2 = box.Y + box.H;
            double cx = (bx1 + bx2) / 2, cy = (by1 + by2) / 2;
            double side = Math.Max(MinSide, Factor * Math.Max(bx2 - bx1, by2 - by1));
            // 실험은 파이썬 int() — 0 이상이라 버림과 같다
            int x1 = (int)Math.Max(0, cx - side / 2), y1 = (int)Math.Max(0, cy - side / 2);
            int x2 = (int)Math.Min(frame.Width, cx + side / 2), y2 = (int)Math.Min(frame.Height, cy + side / 2);
            if (x2 - x1 < 2 || y2 - y1 < 2) return null;

            using var view = new Mat(frame, new Rect(x1, y1, x2 - x1, y2 - y1));
            var image = view.Clone();
            // 실험은 박스를 원본 프레임에 그린 뒤 잘랐다 — 반올림(파이썬 round 와 같은 짝수 반올림)한 원본 좌표를 영역 원점만큼 옮겨 그리면 같은 화소다
            var p1 = new Point((int)Math.Round(bx1) - x1, (int)Math.Round(by1) - y1);
            var p2 = new Point((int)Math.Round(bx2) - x1, (int)Math.Round(by2) - y1);
            return new VlmRegion(image, p1, p2);
        }

        /// <summary>[작업] 박스를 그리고 긴 변 512px 로 바꾼 그림 (JPEG 전 — 실험 도구가 화소를 견준다). 부른 쪽이 해제한다</summary>
        public static Mat Render(VlmRegion region)
        {
            using var drawn = region.Image.Clone();   // 영역은 그대로 둔다 (여러 번 불러도 같게)
            Cv2.Rectangle(drawn, region.Box1, region.Box2, new Scalar(0, 0, 255), Thickness);
            double s = (double)LongSide / Math.Max(drawn.Width, drawn.Height);
            var size = new Size((int)Math.Round(drawn.Width * s), (int)Math.Round(drawn.Height * s));
            var resized = new Mat();
            Cv2.Resize(drawn, resized, size, 0, 0, s > 1 ? InterpolationFlags.Cubic : InterpolationFlags.Area);
            return resized;
        }

        /// <summary>[작업] VLM 에 보낼 JPEG</summary>
        public static byte[] Encode(VlmRegion region)
        {
            using var img = Render(region);
            Cv2.ImEncode(".jpg", img, out byte[] jpeg, new ImageEncodingParam(ImwriteFlags.JpegQuality, JpegQuality));
            return jpeg;
        }
    }
}
