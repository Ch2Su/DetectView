using System;
using System.Collections.Generic;
using System.Linq;
using OpenCvSharp;

namespace DetectView
{
    /// <summary>
    /// 한 화면(뷰)의 특징점 서명 — ORB 특징점과 기술자. 가로 <see cref="ViewMatcher.BaseWidth"/> 로 줄인 흑백 그림에서 만든다
    /// (프레임 크기·화면 배율과 무관하게 같은 값을 내려고). 네이티브 Mat 을 쥐고 있으니 다 쓰면 Dispose
    /// </summary>
    internal sealed class ViewSignature : IDisposable
    {
        public int Width { get; }
        public int Height { get; }
        public Point2f[] Points { get; }
        public Mat Descriptors { get; }

        public ViewSignature(int width, int height, Point2f[] points, Mat descriptors)
        {
            Width = width; Height = height; Points = points; Descriptors = descriptors;
        }

        public void Dispose() => Descriptors.Dispose();
    }

    /// <summary>두 화면을 맞춰 본 결과. 기준 화면 → 새 화면 호모그래피의 크기·이동으로 '같은 자리에서 찍은 화면'인지 가린다.
    /// H 는 그 호모그래피(3×3 행 우선, [8] = 1 로 맞춤 — 맞추지 못했으면 null). 자세 지켜보기(CameraPoseWatch)가 뷰 기준 화면에 대한 자세로 이어 쓴다.
    /// Spread · SpreadTo 는 인라이어가 놓인 8×6 격자 칸 수(<see cref="ViewMatcher.Spread"/>) — 기준 화면 쪽 · 새 화면 쪽. 줌인은 기준 화면의 가운데만 새 화면 전체로 옮겨져
    /// 기준 쪽은 몰리고 새 화면 쪽은 퍼진다(줌아웃은 그 반대) — 맞춤이 화면에 얼마나 퍼졌는지는 둘 중 큰 쪽(<see cref="SpreadMax"/>)으로 본다</summary>
    internal readonly record struct ViewMatch(int Features, int Good, int Inliers, int Cells, double Shift, double Scale, double Skew, double[]? H = null, int Spread = 0, int SpreadTo = 0)
    {
        public static ViewMatch None(int features = 0, int good = 0) => new(features, good, 0, 0, 0, 1, 0);

        public int SpreadMax => Math.Max(Spread, SpreadTo);
    }

    /// <summary>
    /// 화면 고정 글자(지사·노선·방향 안내처럼 영상에 덧씌운 글자) 자리 — 가로 <see cref="ViewMatcher.BaseWidth"/> 그림 좌표의 4px 칸 집합.
    /// 이런 글자는 카메라가 줌·팬해도 화면의 같은 곳에 그대로라, 맞춤에 넣으면 그 특징점들이 '안 움직였다'(이동 0 · 배율 1)로 맞아
    /// 줌아웃한 화면이 원래 뷰로 알아봐졌다(test6 1417초 — 장면은 안 맞고 글자만 107~131개가 칸 5~7에 맞음, 2026-11-21).
    /// 겉모습(흰 글씨 + 검은 테두리)으로 찾는 규칙은 장면(차·간판·구조물)까지 넓게 가리고 test4 의 글자를 놓쳐 쓰지 않았다 — 카메라가 움직인 것을 안 때
    /// 움직임 앞뒤 화면에서 제자리로 맞은 짝으로 배운다(<see cref="With"/>). 불변 — 배울 때마다 새 것을 만든다
    /// </summary>
    internal sealed class OverlayMask
    {
        public const int CellPx = 4;
        /// <summary>제자리 짝끼리 이만큼(px) 안이면 한 글자 줄로 묶어 그 사각형을 통째로 덮는다 — 짝이 안 생긴 같은 줄의 다른 글자도 가리려고</summary>
        public const double LinkPx = 16;
        /// <summary>사각형 둘레 여백(px)</summary>
        public const int MarginPx = 6;

        private readonly HashSet<int> _cells;
        private OverlayMask(HashSet<int> cells, int points) { _cells = cells; Points = points; }

        public static readonly OverlayMask Empty = new([], 0);

        /// <summary>지금까지 배운 제자리 짝 수 (표시·검사용)</summary>
        public int Points { get; }
        public bool IsEmpty => _cells.Count == 0;
        /// <summary>덮은 칸 수 (검사용)</summary>
        public int Cells => _cells.Count;

        public bool Covers(Point2f p) => p.X >= 0 && p.Y >= 0 && _cells.Contains(Key((int)(p.X / CellPx), (int)(p.Y / CellPx)));

        /// <summary>특징점을 뽑을 곳(255)과 뽑지 않을 글자 자리(0)의 그림 — ORB DetectAndCompute 의 mask. 호출자가 해제한다</summary>
        public Mat DetectionMask(int width, int height)
        {
            var m = new Mat(height, width, MatType.CV_8UC1, Scalar.All(255));
            foreach (int k in _cells)
            {
                int cx = k % 4096, cy = k / 4096;
                var r = new Rect(cx * CellPx, cy * CellPx, CellPx, CellPx) & new Rect(0, 0, width, height);
                // 사각형을 채운다 — 예전 m[r].SetTo(...) 는 칸마다 해제하지 않는 Mat 머리를 만들어 서명 하나에 종료자 대기 객체를 수백~수천 개 남겼다(2026-11-22 검토). 칠해지는 화소는 같다
                if (r.Width > 0 && r.Height > 0) Cv2.Rectangle(m, r, Scalar.All(0), -1);
            }
            return m;
        }

        /// <summary>덮은 자리를 둘레로 px 만큼 넓힌 마스크 (새 객체 — 비었으면 그대로). 글자 바로 옆 특징점은 기술자(ORB 의 31px 조각)에 글자가 섞여, 카메라가 움직여도
        /// 제자리로 맞는다 — 흐린 장면에선 그런 짝만으로 줌아웃한 화면을 줌 전 기준 화면이 받아들였다(인라이어 92, 2026-11-22 셀프 테스트). '장면만으로 받아들이나'를 물을 때 쓴다</summary>
        public OverlayMask Dilated(int px)
        {
            if (_cells.Count == 0 || px <= 0) return this;
            int r = (int)Math.Ceiling((double)px / CellPx);
            var cells = new HashSet<int>();
            foreach (int k in _cells)
            {
                int cx = k % 4096, cy = k / 4096;
                for (int y = Math.Max(0, cy - r); y <= cy + r; y++)
                    for (int x = Math.Max(0, cx - r); x <= cx + r; x++)
                        cells.Add(Key(x, y));
            }
            return new OverlayMask(cells, Points);
        }

        /// <summary>제자리 짝들을 더한 마스크. 가까운 짝끼리 묶어(<see cref="LinkPx"/>) 묶음마다 사각형(+여백)을 덮는다 — 혼자 떨어진 짝은 그 둘레만.
        /// 새로 덮는 칸이 없으면 같은 객체를 돌려준다 — 식별은 글자 객체가 바뀌면 기준·보조 기준 화면의 서명을 모두 다시 만들어서(하나에 4~5 ms), 이미 배운 글자를 다시 배울 때마다 재생이 멈칫했다</summary>
        public OverlayMask With(IReadOnlyList<Point2d> pts)
        {
            if (pts.Count == 0) return this;
            var cells = new HashSet<int>(_cells);
            int n = pts.Count;
            var parent = new int[n];
            for (int i = 0; i < n; i++) parent[i] = i;
            int Find(int i) { while (parent[i] != i) i = parent[i] = parent[parent[i]]; return i; }
            for (int i = 0; i < n; i++)
                for (int j = i + 1; j < n; j++)
                {
                    double dx = pts[i].X - pts[j].X, dy = pts[i].Y - pts[j].Y;
                    if (dx * dx + dy * dy <= LinkPx * LinkPx) parent[Find(i)] = Find(j);
                }
            foreach (var group in Enumerable.Range(0, n).GroupBy(Find))
            {
                double x0 = group.Min(i => pts[i].X) - MarginPx, x1 = group.Max(i => pts[i].X) + MarginPx;
                double y0 = group.Min(i => pts[i].Y) - MarginPx, y1 = group.Max(i => pts[i].Y) + MarginPx;
                for (int cy = Math.Max(0, (int)(y0 / CellPx)); cy <= (int)(y1 / CellPx); cy++)
                    for (int cx = Math.Max(0, (int)(x0 / CellPx)); cx <= (int)(x1 / CellPx); cx++)
                        cells.Add(Key(cx, cy));
            }
            return cells.Count == _cells.Count ? this : new OverlayMask(cells, Points + n);
        }

        private static int Key(int cx, int cy) => cy * 4096 + cx;
    }

    /// <summary>
    /// 화면 식별(2b)의 바탕 — ORB 특징점을 맞추고 RANSAC 호모그래피로 두 화면이 같은 카메라 자세인지 본다.
    /// 인스턴스는 ORB·매처를 재사용하므로 한 스레드에서만 쓴다
    /// </summary>
    internal sealed class ViewMatcher : IDisposable
    {
        /// <summary>서명을 만들 때 가로를 이 크기로 줄인다 (세로는 비율대로)</summary>
        public const int BaseWidth = 480;
        /// <summary>격자(가로 4 × 세로 3) 중 인라이어가 있는 칸 수를 센다 — 한 구석(방향 안내 글자·로고)에 몰린 일치를 걸러내려고</summary>
        private const int GridX = 4, GridY = 3;

        private readonly ORB _orb = ORB.Create(nFeatures: 800, scaleFactor: 1.2f, nLevels: 8, edgeThreshold: 15, fastThreshold: 12);
        private readonly BFMatcher _bf = new(NormTypes.Hamming, crossCheck: false);

        /// <summary>프레임(BGR·BGRA·흑백) → 가로 BaseWidth 흑백. 돌려주는 Mat 은 호출자가 해제한다</summary>
        public static Mat PrepareGray(Mat frame)
        {
            var gray = new Mat();
            if (frame.Channels() == 3) Cv2.CvtColor(frame, gray, ColorConversionCodes.BGR2GRAY);
            else if (frame.Channels() == 4) Cv2.CvtColor(frame, gray, ColorConversionCodes.BGRA2GRAY);
            else frame.CopyTo(gray);
            if (gray.Width != BaseWidth)
            {
                int h = Math.Max(1, (int)Math.Round(gray.Height * (double)BaseWidth / gray.Width));
                var small = new Mat();
                Cv2.Resize(gray, small, new Size(BaseWidth, h), 0, 0, InterpolationFlags.Area);
                gray.Dispose();
                gray = small;
            }
            return gray;
        }

        /// <summary>
        /// 흑백 그림(PrepareGray 의 결과 또는 그것을 JPEG 로 저장했다 읽은 것) → 서명. overlay 가 있으면 화면 고정 글자 자리에서는 특징점을 뽑지 않는다 —
        /// 맞출 때만 빼면 특징점 800개의 상당수를 글자가 차지해 장면 짝이 모자랐다(test6 같은 자세 23초 차이에 인라이어 11). 뽑을 때 빼면 같은 자세끼리 50~213 ·
        /// 다른 자세끼리 13~21 로 갈렸다(2026-11-21, 줌 전후·연기 낀 0초 기준 포함)
        /// </summary>
        public ViewSignature Compute(Mat gray, OverlayMask? overlay = null)
        {
            using var desc = new Mat();
            KeyPoint[] kps;
            if (overlay is { IsEmpty: false })
            {
                using var allow = overlay.DetectionMask(gray.Width, gray.Height);
                _orb.DetectAndCompute(gray, allow, out kps, desc);
            }
            else _orb.DetectAndCompute(gray, null, out kps, desc);
            var pts = kps.Select(k => k.Pt).ToArray();
            return new ViewSignature(gray.Width, gray.Height, pts, desc.Clone());
        }

        /// <summary>기준 화면 → 새 화면 호모그래피를 구해 어긋난 정도를 잰다. 일치가 모자라면 인라이어 0.
        /// overlay 가 있으면 화면 고정 글자 자리의 짝은 빼고 맞춘다(<see cref="OverlayMask"/>)</summary>
        public ViewMatch Compare(ViewSignature reference, ViewSignature probe, OverlayMask? overlay = null)
        {
            int features = Math.Min(reference.Points.Length, probe.Points.Length);
            if (reference.Descriptors.Empty() || probe.Descriptors.Empty() || reference.Points.Length < 8 || probe.Points.Length < 8)
                return ViewMatch.None(features);
            var (src, dst) = Pairs(reference, probe, overlay);
            return Measure(src, dst, reference.Width, reference.Height, features);
        }

        /// <summary>
        /// <see cref="Compare"/> 와 같은 맞춤(All)과 함께, 두 화면에서 같은 자리(pinnedPx 안)에 맞은 짝을 뺀 나머지만의 맞춤(Remainder)과 그 제자리 짝이 퍼진 칸 수(8×6)를 한 번의 짝 찾기로 낸다.
        /// 화면 고정 글자는 카메라가 움직여도 제자리라 전체 맞춤을 '제자리'로 끌고 가는데(test6 팬·줌), 나머지가 화면에 퍼진 채 같이 움직였으면 카메라가 움직인 것이다 — 자세 지켜보기가 그 증거로 쓴다
        /// </summary>
        public (ViewMatch All, ViewMatch Remainder, int PinnedSpread) CompareSplit(ViewSignature reference, ViewSignature probe, double pinnedPx)
        {
            int features = Math.Min(reference.Points.Length, probe.Points.Length);
            if (reference.Descriptors.Empty() || probe.Descriptors.Empty() || reference.Points.Length < 8 || probe.Points.Length < 8)
                return (ViewMatch.None(features), ViewMatch.None(features), 0);
            var (src, dst) = Pairs(reference, probe);
            var all = Measure(src, dst, reference.Width, reference.Height, features);
            var rs = new List<Point2d>();
            var rd = new List<Point2d>();
            var pinned = new List<Point2d>();
            for (int i = 0; i < src.Length; i++)
            {
                double dx = dst[i].X - src[i].X, dy = dst[i].Y - src[i].Y;
                if (dx * dx + dy * dy > pinnedPx * pinnedPx) { rs.Add(src[i]); rd.Add(dst[i]); }
                else pinned.Add(src[i]);
            }
            return (all, Measure(rs, rd, reference.Width, reference.Height, features), Spread(pinned, reference.Width, reference.Height));
        }

        /// <summary>격자(가로 SpreadX × 세로 SpreadY) 중 점이 있는 칸 수 — 점들이 화면에 얼마나 퍼졌나. 화면 고정 글자는 몇 덩이(test6 16~17칸),
        /// 카메라가 움직였을 때의 장면 맞춤과 서 있는 장면의 제자리 짝은 화면 전체에 퍼진다(2026-11-22 실측 — <c>CameraPoseWatch</c>·<c>ViewRegistry.TextLike</c>)</summary>
        public const int SpreadX = 8, SpreadY = 6;

        public static int Spread(IEnumerable<Point2d> pts, int width, int height)
        {
            var occ = new bool[SpreadX * SpreadY];
            int n = 0;
            foreach (var p in pts)
            {
                int i = Math.Clamp((int)(p.Y / height * SpreadY), 0, SpreadY - 1) * SpreadX + Math.Clamp((int)(p.X / width * SpreadX), 0, SpreadX - 1);
                if (!occ[i]) { occ[i] = true; n++; }
            }
            return n;
        }

        /// <summary>프레임을 <see cref="PrepareGray"/> 로 줄였을 때의 크기 (가로 BaseWidth)</summary>
        public static (int Width, int Height) BaseSize(Mat frame) => (BaseWidth, Math.Max(1, (int)Math.Round(frame.Height * (double)BaseWidth / frame.Width)));

        /// <summary>Lowe 비율 검사를 통과한 짝 — 기준 화면 점(Ref)과 새 화면 점(Probe). overlay 가 덮는 자리의 짝은 뺀다</summary>
        public (Point2d[] Ref, Point2d[] Probe) Pairs(ViewSignature reference, ViewSignature probe, OverlayMask? overlay = null)
        {
            if (reference.Descriptors.Empty() || probe.Descriptors.Empty() || reference.Points.Length < 2 || probe.Points.Length < 2)
                return ([], []);
            DMatch[][] knn = _bf.KnnMatch(probe.Descriptors, reference.Descriptors, 2);
            var src = new List<Point2d>();   // 기준 화면 좌표
            var dst = new List<Point2d>();   // 새 화면 좌표
            bool mask = overlay is { IsEmpty: false };
            foreach (var m in knn)
            {
                if (m.Length < 2 || m[0].Distance >= 0.8f * m[1].Distance) continue;   // Lowe 비율 검사 — 애매한 짝은 버린다
                var r = reference.Points[m[0].TrainIdx];
                var p = probe.Points[m[0].QueryIdx];
                if (mask && (overlay!.Covers(r) || overlay.Covers(p))) continue;     // 화면 고정 글자 — 카메라 자세를 말해 주지 않는다
                src.Add(new Point2d(r.X, r.Y));
                dst.Add(new Point2d(p.X, p.Y));
            }
            return (src.ToArray(), dst.ToArray());
        }

        /// <summary>짝으로 호모그래피(기준 → 새 화면)를 구해 인라이어·칸·이동·배율·원근을 잰다. 짝이 8개 미만이거나 못 구하면 인라이어 0</summary>
        public static ViewMatch Measure(IReadOnlyList<Point2d> src, IReadOnlyList<Point2d> dst, int width, int height, int features)
        {
            int good = src.Count;
            if (good < 8) return ViewMatch.None(features, good);

            using var mask = new Mat();
            using var h = Cv2.FindHomography(src, dst, HomographyMethods.Ransac, 3.0, mask);
            if (h.Empty() || h.Rows != 3 || h.Cols != 3) return ViewMatch.None(features, good);
            mask.GetArray(out byte[] inlier);

            h.GetArray(out double[] a);
            if (Math.Abs(a[8]) < 1e-12) return ViewMatch.None(features, good);
            for (int i = 0; i < 9; i++) a[i] /= a[8];

            int n = 0;
            var cells = new bool[GridX * GridY];
            var inlierFrom = new List<Point2d>();
            var inlierTo = new List<Point2d>();
            for (int i = 0; i < inlier.Length && i < good; i++)
            {
                if (inlier[i] == 0) continue;
                n++;
                int cx = Math.Clamp((int)(src[i].X / width * GridX), 0, GridX - 1);
                int cy = Math.Clamp((int)(src[i].Y / height * GridY), 0, GridY - 1);
                cells[cy * GridX + cx] = true;
                inlierFrom.Add(src[i]);
                inlierTo.Add(dst[i]);
            }

            var (shift, scale, skew) = Geometry(a, width, height);
            if (double.IsNaN(shift)) return ViewMatch.None(features, good);
            return new ViewMatch(features, good, n, cells.Count(c => c), shift, scale, skew, a, Spread(inlierFrom, width, height), Spread(inlierTo, width, height));
        }

        /// <summary>호모그래피([8] = 1 로 맞춘 3×3)가 화면을 얼마나 옮기나 — 가운데의 이동(가로 대비)·배율(왼쪽 위 2×2 의 행렬식 제곱근)·원근 항(가로 대비).
        /// 가운데가 무한대로 가면(원근이 무너짐) 이동은 NaN</summary>
        public static (double Shift, double Scale, double Skew) Geometry(double[] a, int width, int height)
        {
            double cxm = width / 2.0, cym = height / 2.0;
            double w = a[6] * cxm + a[7] * cym + 1;
            if (Math.Abs(w) < 1e-9) return (double.NaN, 0, double.PositiveInfinity);
            double mx = (a[0] * cxm + a[1] * cym + a[2]) / w, my = (a[3] * cxm + a[4] * cym + a[5]) / w;
            double shift = Math.Sqrt((mx - cxm) * (mx - cxm) + (my - cym) * (my - cym)) / width;
            double det = a[0] * a[4] - a[1] * a[3];
            double scale = det > 0 ? Math.Sqrt(det) : 0;
            double skew = (Math.Abs(a[6]) + Math.Abs(a[7])) * width;   // 원근 항 — 크면 호모그래피가 무너진 것
            return (shift, scale, skew);
        }

        /// <summary>짝 가운데 두 화면에서 거의 같은 자리(px 안)에 맞은 것의 기준 화면 점 — 카메라가 움직인 앞뒤라면 화면 고정 글자다</summary>
        public static List<Point2d> Pinned(IReadOnlyList<Point2d> src, IReadOnlyList<Point2d> dst, double px)
        {
            var list = new List<Point2d>();
            for (int i = 0; i < src.Count && i < dst.Count; i++)
            {
                double dx = dst[i].X - src[i].X, dy = dst[i].Y - src[i].Y;
                if (dx * dx + dy * dy <= px * px) list.Add(src[i]);
            }
            return list;
        }

        /// <summary>두 호모그래피를 잇는다: 먼저 first, 그다음 then (then × first, [8] = 1 로 맞춤)</summary>
        public static double[] Chain(double[] first, double[] then)
        {
            var r = new double[9];
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++)
                    r[i * 3 + j] = then[i * 3] * first[j] + then[i * 3 + 1] * first[3 + j] + then[i * 3 + 2] * first[6 + j];
            if (Math.Abs(r[8]) > 1e-12)
                for (int i = 0; i < 9; i++) r[i] /= r[8];
            return r;
        }

        /// <summary>제자리 호모그래피 (부를 때마다 새 배열 — 받은 쪽이 고쳐도 다른 곳에 번지지 않게)</summary>
        public static double[] Identity => [1, 0, 0, 0, 1, 0, 0, 0, 1];

        /// <summary>
        /// ORB·매처·JPEG 처리의 첫 호출은 네이티브 초기화와 JIT 로 수백 ms 걸린다 — 재생 루프에서 처음 부르면 처리가 250 ms 넘게 밀려 재생 시계를 다시 맞추느라
        /// 세션 첫 재생이 0.8~0.9배속으로 보였다(2026-11-03 Test-VfrPlayback 에서 재현). 앱을 켤 때 다른 스레드에서 한 번 돌려 둔다
        /// </summary>
        public static void WarmUp()
        {
            using var m = new ViewMatcher();
            using var img = new Mat(270, 480, MatType.CV_8UC1);
            Cv2.Randu(img, new Scalar(0), new Scalar(255));   // 특징점이 잡히는 무늬
            using var sig = m.Compute(img);
            m.Compare(sig, sig);
            Cv2.ImEncode(".jpg", img, out byte[] jpeg, new ImageEncodingParam(ImwriteFlags.JpegQuality, 75));
            using var back = Cv2.ImDecode(jpeg, ImreadModes.Grayscale);
            // 자세 지켜보기(CameraPoseWatch)가 카메라가 멈춘 뒤 한 번 쓰는 SIFT 도 — 처음 움직임에서 재생이 밀리지 않게
            using var sift = OpenCvSharp.Features2D.SIFT.Create(200);
            using var d = new Mat();
            sift.DetectAndCompute(img, null, out _, d);
        }

        public void Dispose()
        {
            _orb.Dispose();
            _bf.Dispose();
        }
    }

    /// <summary>저장·표시용 한 뷰 — 번호와 기준 화면(흑백 JPEG). 구역 파일에 들어간다</summary>
    internal sealed record ViewInfo(int Id, byte[] RefJpeg)
    {
        public string Name => $"V{Id}";
    }

    /// <summary>알아봤다(Known) · 처음 보는 화면이라 새로 등록했다(New) · 알 수 없다(Unknown — 애매하거나 특징이 모자람, 나중에 다시 본다)</summary>
    internal enum ViewOutcome { Known, New, Unknown }

    internal readonly record struct ViewIdentification(ViewOutcome Outcome, int ViewId, ViewMatch Match);

    /// <summary>
    /// 화면 식별 기준. 값은 test4 의 세 화면에서 잰 것이다 — 같은 화면끼리 인라이어 300~540(칸 7~12), 다른 화면·다른 영상끼리 최대 8(2026-11-03, TrackerCheck views).
    /// 사이가 넓어 30 은 여유가 크다
    /// </summary>
    internal sealed record ViewIdentityOptions
    {
        /// <summary>이만큼 이상 일치하고 아래 조건을 지키면 같은 화면</summary>
        public int MinInliers { get; init; } = 30;
        /// <summary>인라이어가 놓인 격자 칸(12칸 중) — 한 구석(방향 안내 글자·로고)에만 몰린 일치는 같은 화면이 아니다</summary>
        public int MinCells { get; init; } = 4;
        /// <summary>화면 가운데가 옮겨진 정도(가로 대비)·배율 — 이보다 어긋나면 같은 자리가 아니라 다른 프리셋으로 본다. 구역은 이 어긋남을 보정하지 않는다</summary>
        public double MaxShift { get; init; } = 0.06;
        public double MinScale { get; init; } = 0.9;
        public double MaxScale { get; init; } = 1.12;
        /// <summary>호모그래피의 원근 항(가로 대비) — 크면 일치가 우연히 맞춰진 것</summary>
        public double MaxSkew { get; init; } = 1.0;
        /// <summary>MinInliers 에 못 미치지만 이만큼은 맞은 '애매함' — 새 뷰로 등록하지 않고 나중에 다시 본다(잘못 쪼개 등록하지 않으려고)</summary>
        public int UnsureInliers { get; init; } = 15;
        /// <summary>새 화면에서 이만큼의 특징점도 못 뽑으면(어둡거나 흐림) 식별하지 않는다</summary>
        public int MinProbeFeatures { get; init; } = 60;
        /// <summary>등록할 수 있는 뷰 수 — 넘으면 새 화면은 알 수 없음으로 둔다 (자세가 조금씩 어긋나는 카메라가 뷰를 끝없이 늘리지 않게)</summary>
        public int MaxViews { get; init; } = 32;
        public int JpegQuality { get; init; } = 75;
        /// <summary>두 화면에서 이만큼(px, 가로 480 기준) 안쪽 같은 자리에 맞은 짝 = 제자리 짝 (카메라가 움직인 앞뒤라면 화면 고정 글자)</summary>
        public double PinnedPx { get; init; } = 1.5;
        /// <summary>뷰 기준 화면끼리의 제자리 짝이 특징점의 이 비율 이상이면 두 뷰가 사실상 같은 자리라(장면까지 제자리) 글자로 배우지 않는다.
        /// test6 의 줌 앞뒤는 글자만 14~19%(굵은 글자의 합성 화면은 그보다 많다), 같은 자리 화면끼리는 대개 그 이상 제자리다(1초 간격 맞춤의 하위 5% 가 57~94%)</summary>
        public double OverlayMaxFraction { get; init; } = 0.5;
        /// <summary>글자로 배울 제자리 짝은 8×6 격자(<see cref="ViewMatcher.Spread"/>)의 이 칸 수 이하에 몰려 있어야 한다 — 글자는 몇 덩이(test6 16~17칸),
        /// 제자리로 남은 서 있는 장면은 화면 전체다(대개 25~39칸 — SIFT 960 · 5초 간격, 2026-11-22). 장면을 글자로 잘못 배우면 그 자리의 특징점을 영영 못 쓴다</summary>
        public int OverlayMaxSpread { get; init; } = 24;
        /// <summary>
        /// 화면 고정 글자가 화면의 이 비율 넘게 덮이게 되면 배우지 않는다 — 배운 '뒤'의 넓이로 본다(예전엔 배우기 전 넓이만 봐서 한 번에 0 → 70% 를 덮었다, 2026-11-22 검토).
        /// test6 글자는 약 13%(4px 칸 · 16px 묶음 · 6px 여백). 위아래 띠 글자가 있는 영상도 첫 배움이 막히지 않게 35% — 장면을 글자로 배우는 일은 이 한도보다
        /// 움직임이 화면에 퍼졌는지(CameraPoseWatch)·글자로 보이는 짝인지(<see cref="ViewRegistry.TextLike"/>)로 먼저 거른다
        /// </summary>
        public double OverlayMaxArea { get; init; } = 0.35;
        /// <summary>뷰마다 둘 보조 기준 화면 수(AddRecent — 넘으면 가장 오래 손대지 않은 것부터 버린다). 1분마다 하나면 16분</summary>
        public int MaxExtras { get; init; } = 16;
        /// <summary>보조 기준 화면 사이의 영상 시각 최소 간격 — 이보다 가까운 화면을 더하면 옛 것을 바꾼다. 시크·한 장 보기마다 더하면 한 구간의 화면들이 1분마다 더한
        /// 서 있는 화면들을 밀어내 다른 때로 시크하면 알 수 없음·새 뷰가 됐다(2026-11-22 검토)</summary>
        public double RecentWindowMs { get; init; } = 30000;
        /// <summary>식별 한 번에 뷰마다 맞춰 볼 보조 기준 수 — 영상 시각이 가까운 것부터(받아들여지면 거기서 멈춘다). 기준·보조 기준을 모두 맞추면 보조 기준이 찬 영상에서
        /// 시크·전환마다 0.2~0.4초, 알 수 없는 동안 매초 0.3초씩 재생이 멈췄다(2026-11-22 검토)</summary>
        public int ExtrasPerIdentify { get; init; } = 6;
        /// <summary>글자를 배운 뒤 한 뷰가 이만큼 넘게, 8×6 격자의 <see cref="SureSpread"/> 칸 넘게 퍼져 받아들여지면 다른 뷰는 보지 않는다(단서 뷰부터 본다).
        /// 넉넉히 잡는다 — 글자 바로 옆 특징점만으로 받아들인 맞춤(<see cref="StrictMarginPx"/>)이 단서 뷰면 진짜 뷰를 보지도 않고 끝내지 않게 (test6 같은 자세끼리 50~213)</summary>
        public int SureInliers { get; init; } = 150;
        public int SureSpread { get; init; } = 30;
        /// <summary>'장면만으로 이 뷰인가'를 기준 화면에 물을 때(카메라가 정말 떠났나 · 앞 방문이 글자만으로 알아본 다른 자세였나 — ViewSession) 배운 글자를 둘레로 이만큼(px, 가로 480) 더 넓혀 뺀다
        /// — 글자 바로 옆 특징점이 글자를 담아 제자리로 맞는다(<see cref="OverlayMask.Dilated"/>). 식별 자체는 넓히지 않는다(같은 자세의 장면 짝이 줄어든다)</summary>
        public int StrictMarginPx { get; init; } = 12;
    }

    /// <summary>
    /// 알고 있는 뷰들 — 새 화면이 어느 뷰인지 알아보고, 처음 보는 화면은 새 뷰로 기억한다 (화면 식별 2b, 2026-11-03).
    /// 기준 화면은 JPEG 로 저장했다 읽은 것에서 서명을 만든다 — 구역 파일에 저장했다 다시 열어도 같은 서명이라 같은 결과가 나온다.
    /// <see cref="Identify"/>(ORB·매처를 쓴다)는 한 스레드(재생 루프)에서만, <see cref="Snapshot"/>·<see cref="Count"/> 는 어느 스레드에서나 부를 수 있다.
    /// 앱에선 Dispose 하지 않는다 — 소스를 내릴 때 루프가 아직 Identify 중일 수 있어서(네이티브 객체라 가비지 수집이 알아서 해제)
    /// </summary>
    internal sealed class ViewRegistry : IDisposable
    {
        private sealed class Entry(int id, byte[] jpeg)
        {
            public int Id { get; } = id;
            public byte[] Jpeg { get; } = jpeg;
            public ViewSignature? Sig { get; set; }   // 저장본은 처음 쓸 때 만든다 — 루프 스레드에서만
            public OverlayMask? SigOverlay { get; set; }   // Sig 를 만들 때 뺀 글자 — 글자를 더 배우면 Sig 를 다시 만든다
            public ViewSignature? RawSig { get; set; }     // 글자를 빼지 않은 서명 — 기준 화면끼리 글자를 배울 때만
            public List<Extra> Extras { get; } = [];       // 보조 기준 화면 (AddRecent — 이 세션에서만, 파일엔 안 적는다)
        }

        /// <summary>보조 기준 화면 — 같은 뷰를 지켜보는 동안 찍어 둔 것. Pose = 뷰 기준 화면 → 이 화면 호모그래피, AtMs = 영상 시각(모르면 NaN),
        /// Visit = 넣은 방문 번호(ViewSession — 0 은 모름). 방문의 출발점(식별)이 틀렸다고 드러나면 그 방문의 것을 통째로 버린다(<see cref="DropVisit"/>)</summary>
        private sealed class Extra(byte[] jpeg, double[] pose, double atMs, int visit)
        {
            public byte[] Jpeg { get; } = jpeg;
            public double[] Pose { get; } = pose;
            public double AtMs { get; } = atMs;
            public int Visit { get; } = visit;
            public ViewSignature? Sig { get; set; }
            public OverlayMask? SigOverlay { get; set; }
        }

        private readonly ViewIdentityOptions _o;
        private readonly ViewMatcher _matcher = new();
        private readonly System.Threading.Lock _gate = new();
        private readonly List<Entry> _entries = [];
        private int _nextId;
        private volatile int _firstViewId;
        private volatile OverlayMask _overlay = OverlayMask.Empty;
        private int _overlayPairsFrom;   // 기준 화면끼리 맞춰 글자를 배운 뷰 수 — 그 뒤에 생긴 뷰만 앞의 뷰들과 맞춰 본다 (루프 스레드)
        private OverlayMask? _strictFor, _strict;   // 넓힌 글자 마스크와 그것을 만든 글자 (MatchReferenceStrict — 루프 스레드)

        public ViewRegistry(IEnumerable<ViewInfo>? saved = null, int nextId = 1, ViewIdentityOptions? options = null)
        {
            _o = options ?? new ViewIdentityOptions();
            int max = 0;
            foreach (var v in saved ?? [])
            {
                _entries.Add(new Entry(v.Id, v.RefJpeg));
                max = Math.Max(max, v.Id);
            }
            _nextId = Math.Max(nextId, max + 1);
        }

        public ViewIdentityOptions Options => _o;

        public int Count { get { lock (_gate) return _entries.Count; } }

        /// <summary>다음에 붙일 뷰 번호 — 지운 번호는 다시 쓰지 않는다 (파일에도 적는다)</summary>
        public int NextId { get { lock (_gate) return _nextId; } }

        /// <summary>이 영상을 연 뒤 처음 알아본(또는 등록한) 뷰 — 뷰가 없던 옛 구역 파일의 구역들이 이 뷰의 것이 된다. 아직 없으면 0</summary>
        public int FirstViewId => _firstViewId;

        /// <summary>지금 알고 있는 뷰들 (구역 파일에 저장할 때)</summary>
        public IReadOnlyList<ViewInfo> Snapshot()
        {
            lock (_gate) return _entries.Select(e => new ViewInfo(e.Id, e.Jpeg)).ToList();
        }

        /// <summary>
        /// 구역 파일에서 읽은 뷰 가운데 기준 화면(JPEG)을 그림으로 풀 수 없는 첫 뷰의 번호 (모두 풀리면 0). base64 는 맞는데 그림이 아닌 값은 예외 없이 빈 그림이 돼,
        /// 그 뷰는 어느 화면과도 맞지 않고(특징점 0) 그 뷰에 그린 구역이 주인 없이 남는다 — 읽을 때 걸러 '구역 파일을 읽지 못함'으로 다룬다 (2026-11-25 Gemini 리뷰 6-6)
        /// </summary>
        public static int FirstUnreadable(IReadOnlyList<ViewInfo> views)
        {
            foreach (var v in views)
            {
                using var g = Cv2.ImDecode(v.RefJpeg, ImreadModes.Grayscale);
                if (g.Empty()) return v.Id;
            }
            return 0;
        }

        public bool Accepts(ViewMatch m) =>
            m.Inliers >= _o.MinInliers && m.Cells >= _o.MinCells && m.Shift <= _o.MaxShift
            && m.Scale >= _o.MinScale && m.Scale <= _o.MaxScale && m.Skew <= _o.MaxSkew;

        /// <summary>인라이어만 모자란 맞춤 — 칸·이동·배율·원근은 같은 뷰의 범위 안 (보조 기준의 맞춤을 '애매함'에 넣을지)</summary>
        private bool Plausible(ViewMatch m) =>
            m.H != null && m.Cells >= _o.MinCells && m.Shift <= _o.MaxShift && m.Scale >= _o.MinScale && m.Scale <= _o.MaxScale && m.Skew <= _o.MaxSkew;

        /// <summary>배운 화면 고정 글자 자리 — 식별의 맞춤에서 뺀다 (루프 스레드가 바꾸고 읽는다)</summary>
        public OverlayMask Overlay => _overlay;

        /// <summary>
        /// 제자리 짝(= 화면 고정 글자 — width × height 크기의 가로 480 그림 좌표)을 더한다. 배운 '뒤'의 넓이가 <see cref="ViewIdentityOptions.OverlayMaxArea"/> 를 넘으면 배우지 않는다.
        /// 새로 덮은 칸이 있으면 true. 자세 지켜보기가 잰 움직임 · 빠른 전환 앞뒤 · 기준 화면끼리에서 부른다 (루프 스레드)
        /// </summary>
        public bool LearnOverlay(IReadOnlyList<Point2d>? pinned, int width, int height)
        {
            if (pinned is not { Count: > 0 }) return false;
            var next = _overlay.With(pinned);
            if (ReferenceEquals(next, _overlay)) return false;
            double cells = (double)width * height / (OverlayMask.CellPx * OverlayMask.CellPx);
            if (next.Cells > _o.OverlayMaxArea * cells) return false;
            _overlay = next;
            return true;
        }

        /// <summary>
        /// 두 화면(width × height 의 가로 480 그림)의 제자리 짝이 화면 고정 글자로 보이나 — 8개 이상이고, 특징점(features)의 <see cref="ViewIdentityOptions.OverlayMaxFraction"/> 아래이고
        /// (넘으면 장면까지 제자리 = 같은 자리), 8×6 격자의 <see cref="ViewIdentityOptions.OverlayMaxSpread"/> 칸 이하에 몰렸다(화면 전체에 퍼진 제자리 짝은 서 있는 장면이다).
        /// 기준 화면끼리와 빠른 전환 앞뒤에서 글자를 배울 때 (자세 지켜보기가 잰 움직임은 움직임이 화면에 퍼졌는지로 따로 거른다 — CameraPoseWatch)
        /// </summary>
        public bool TextLike(IReadOnlyList<Point2d> pinned, int features, int width, int height) =>
            pinned.Count >= 8 && pinned.Count < _o.OverlayMaxFraction * features && ViewMatcher.Spread(pinned, width, height) <= _o.OverlayMaxSpread;

        /// <summary>
        /// 지금 화면을 그 뷰의 보조 기준 화면으로 더한다 (루프 스레드). pose = 뷰 기준 화면 → 이 화면, atMs = 영상 시각, visit = 방문 번호(ViewSession).
        /// 같은 자세라도 연기가 걷히고 정체가 생기면 처음 기준 화면과 장면이 달라, 글자를 빼고 맞추면 맞춤이 약해졌다(test6: 0초 기준 ↔ 800~1410초 인라이어 15~21 → 알 수 없음).
        /// 영상 시각이 <see cref="ViewIdentityOptions.RecentWindowMs"/> 안인 보조 기준이 있으면 그것을 바꾼다(한 구간의 화면이 다른 때의 화면을 밀어내지 않게).
        /// 이 세션에서만 쓰고 파일엔 안 적는다(구역 파일 판 2 그대로). 뷰마다 <see cref="ViewIdentityOptions.MaxExtras"/> 장 — 넘으면 가장 오래 손대지 않은 것부터 버린다
        /// </summary>
        public void AddRecent(int viewId, Mat frame, double[] pose, double atMs = double.NaN, int visit = 0)
        {
            Entry? e;
            lock (_gate) e = _entries.FirstOrDefault(x => x.Id == viewId);
            if (e == null) return;
            using var gray = ViewMatcher.PrepareGray(frame);
            Cv2.ImEncode(".jpg", gray, out byte[] jpeg, new ImageEncodingParam(ImwriteFlags.JpegQuality, _o.JpegQuality));
            if (!double.IsNaN(atMs))
            {
                int near = -1;
                double best = _o.RecentWindowMs;
                for (int i = 0; i < e.Extras.Count; i++)
                {
                    double d = Math.Abs(e.Extras[i].AtMs - atMs);   // 시각을 모르는 것(NaN)은 비교가 거짓이라 건너뛴다
                    if (d < best) { best = d; near = i; }
                }
                if (near >= 0) { e.Extras[near].Sig?.Dispose(); e.Extras.RemoveAt(near); }   // 같은 때의 화면은 새것으로 — 맨 뒤로 가니 늦게 버려진다
            }
            e.Extras.Add(new Extra(jpeg, (double[])pose.Clone(), atMs, visit));
            if (e.Extras.Count > _o.MaxExtras)
            {
                e.Extras[0].Sig?.Dispose();
                e.Extras.RemoveAt(0);
            }
        }

        /// <summary>그 뷰의 보조 기준 화면 수 (검사용 — 보조 기준 목록은 루프 스레드가 바꾸므로 루프 스레드에서만 부른다)</summary>
        public int ExtraCount(int viewId)
        {
            lock (_gate) return _entries.FirstOrDefault(x => x.Id == viewId)?.Extras.Count ?? 0;
        }

        /// <summary>그 뷰의 보조 기준 화면들의 영상 시각 — 넣은 순서 (검사용 — 루프 스레드에서만)</summary>
        public double[] ExtraTimes(int viewId)
        {
            lock (_gate) return _entries.FirstOrDefault(x => x.Id == viewId)?.Extras.Select(x => x.AtMs).ToArray() ?? [];
        }

        /// <summary>
        /// 그 방문에 넣은 보조 기준을 모두 버린다 — 방문의 출발점(식별)이 틀렸다고 드러났을 때(ViewSession: 카메라가 움직인 뒤 화면을 떠난 뷰의 기준 화면이 받아들였다 —
        /// 글자를 배우기 전 다른 자세를 글자만으로 그 뷰로 알아보고 지켜본 것). 그 방문의 보조 기준은 다른 자세를 그 뷰의 것으로 담고 있다. 버린 수
        /// </summary>
        public int DropVisit(int viewId, int visit)
        {
            if (visit == 0) return 0;
            Entry? e;
            lock (_gate) e = _entries.FirstOrDefault(x => x.Id == viewId);
            if (e == null) return 0;
            int removed = 0;
            for (int i = e.Extras.Count - 1; i >= 0; i--)
            {
                if (e.Extras[i].Visit != visit) continue;
                e.Extras[i].Sig?.Dispose();
                e.Extras.RemoveAt(i);
                removed++;
            }
            return removed;
        }

        /// <summary>
        /// 그 뷰의 기준 화면 하나와만 이 프레임을 맞춘다 — 보조 기준은 보지 않는다(잘못 들어간 보조 기준일 수 있다). overlay 를 주면 그 글자를 빼고(아직 배우지 않은 '배울 셈'의 글자로
        /// 미리 볼 때 — 서명을 임시로 만든다), 안 주면 지금 배운 글자를 빼고. 뷰가 없으면 맞춤 없음
        /// </summary>
        public ViewMatch MatchReference(int viewId, Mat frame, OverlayMask? overlay = null)
        {
            Entry? e;
            lock (_gate) e = _entries.FirstOrDefault(x => x.Id == viewId);
            if (e == null) return ViewMatch.None();
            using var gray = ViewMatcher.PrepareGray(frame);
            if (overlay == null || ReferenceEquals(overlay, _overlay))
            {
                var current = _overlay;
                EnsureSig(e, current);
                using var probe = _matcher.Compute(gray, current);
                return _matcher.Compare(e.Sig!, probe, current);
            }
            using var reference = SignatureOf(e.Jpeg, overlay);
            using var trial = _matcher.Compute(gray, overlay);
            return _matcher.Compare(reference, trial, overlay);
        }

        /// <summary>
        /// 그 뷰의 기준 화면이 이 프레임을 장면만으로 받아들이나 — 배운 글자를 <see cref="ViewIdentityOptions.StrictMarginPx"/> 만큼 넓혀 빼고 맞춘다(<see cref="MatchReference"/>).
        /// 카메라가 정말 떠났는지, 앞 방문이 글자만으로 알아본 다른 자세였는지 물을 때 (ViewSession). 글자를 아직 배우지 않았으면 그냥 맞춘다
        /// </summary>
        public ViewMatch MatchReferenceStrict(int viewId, Mat frame)
        {
            var strict = StrictMask();
            return strict.IsEmpty ? MatchReference(viewId, frame) : MatchReference(viewId, frame, strict);
        }

        /// <summary>배운 글자를 <see cref="ViewIdentityOptions.StrictMarginPx"/> 만큼 넓힌 마스크(글자를 아직 배우지 않았으면 빈 마스크) — 글자가 같은 동안 다시 만들지 않는다</summary>
        private OverlayMask StrictMask()
        {
            var overlay = _overlay;
            if (overlay.IsEmpty) return overlay;
            if (!ReferenceEquals(_strictFor, overlay))
            {
                _strict = overlay.Dilated(_o.StrictMarginPx);
                _strictFor = overlay;
            }
            return _strict!;
        }


        /// <summary>
        /// 보조 기준 정리 한 건 — 움직인 뒤 화면(가로 480 흑백)과 그 뷰. 루프가 조용한 프레임마다 몇 장씩 본다(<see cref="PurgeStep"/>) — 한 번에 모두 보면 보조 기준 16장이
        /// (글자를 막 배워 서명을 모두 다시 만들어야 해서) 0.1~0.24초 걸려, 움직임 판정 프레임과 이어져 재생 시계가 다시 맞춰졌다(2026-11-22 검토)
        /// </summary>
        public sealed class PurgeJob : IDisposable
        {
            internal PurgeJob(int viewId, Mat gray, int next) { ViewId = viewId; Gray = gray; Next = next; }
            public int ViewId { get; }
            internal Mat Gray { get; }
            internal int Next { get; set; }                    // 다음에 볼 보조 기준 (끝에서부터 — 지우면 뒤쪽 번호만 바뀐다)
            internal ViewSignature? Probe { get; set; }
            internal OverlayMask? ProbeOverlay { get; set; }
            public bool Done => Next < 0;
            public void Dispose() { Probe?.Dispose(); Probe = null; Gray.Dispose(); }
        }

        /// <summary>
        /// 카메라가 그 뷰에서 다른 자세로 움직였다고 잰 직후(루프 스레드): 그 뷰의 보조 기준 가운데 움직인 뒤 화면을 그 뷰로 받아들이게 하는 것을 버릴 일을 만든다 — 글자를 배우기 전에
        /// 다른 자세를 글자만으로 그 뷰로 알아봐 보조 기준으로 들어간 화면이다(그대로 두면 그 자세가 계속 그 뷰로 알아봐진다). 글자를 배운 뒤(LearnOverlay 다음)에 만든다.
        /// 보조 기준이 없으면 null
        /// </summary>
        public PurgeJob? BeginPurge(int viewId, Mat frame)
        {
            Entry? e;
            lock (_gate) e = _entries.FirstOrDefault(x => x.Id == viewId);
            if (e == null || e.Extras.Count == 0) return null;
            return new PurgeJob(viewId, ViewMatcher.PrepareGray(frame), e.Extras.Count - 1);
        }

        /// <summary>정리할 일을 보조 기준 maxChecks 장만큼 이어 한다 (루프 스레드). 버린 수</summary>
        public int PurgeStep(PurgeJob job, int maxChecks)
        {
            Entry? e;
            lock (_gate) e = _entries.FirstOrDefault(x => x.Id == job.ViewId);
            if (e == null) { job.Next = -1; return 0; }
            var overlay = _overlay;
            if (job.Probe == null || !ReferenceEquals(job.ProbeOverlay, overlay))
            {
                job.Probe?.Dispose();
                job.Probe = _matcher.Compute(job.Gray, overlay);
                job.ProbeOverlay = overlay;
            }
            int removed = 0;
            for (int n = 0; n < maxChecks && job.Next >= 0; n++)
            {
                int i = Math.Min(job.Next, e.Extras.Count - 1);
                job.Next = i - 1;
                if (i < 0) break;
                var x = e.Extras[i];
                // 식별과 똑같이 판정한다 — 보조 기준의 자세를 이어 붙인 '뷰 기준 화면에 대한' 자세로. 날것의 '보조 기준 ↔ 움직인 뒤' 차이로 보면 두 번에 걸쳐 움직였을 때
                // 중간 자세의 멀쩡한 보조 기준(뷰 기준에서 4% → 움직인 뒤와는 3% 차이)까지 버려, 그 구간으로 시크하면 새 뷰가 됐다(2026-11-22 검토)
                if (!Accepts(MatchExtra(x, job.Probe, overlay))) continue;
                x.Sig?.Dispose();
                e.Extras.RemoveAt(i);
                removed++;
            }
            return removed;
        }

        /// <summary>정리를 한 번에 끝까지 (실험 도구·검사용). 버린 수</summary>
        public int PurgeExtrasLike(int viewId, Mat frame)
        {
            using var job = BeginPurge(viewId, frame);
            return job == null ? 0 : PurgeStep(job, int.MaxValue);
        }

        /// <summary>
        /// 글자를 더 배운 뒤 낡은 서명(기준·보조 기준 — 한 번이라도 쓴 것)을 maxRebuilds 개까지 다시 만든다 (루프 스레드 — 조용한 프레임마다). 다시 만든 수.
        /// 글자 객체가 바뀌면 식별이 쓰는 서명을 모두 다시 만들어야 해서(하나에 4~5 ms), 그 뒤 첫 식별 한 번이 뷰·보조 기준 수만큼 길어졌다 — 미리 조금씩 해 둔다
        /// </summary>
        public int Maintain(int maxRebuilds)
        {
            var overlay = _overlay;
            Entry[] entries;
            lock (_gate) entries = _entries.ToArray();
            int done = 0;
            foreach (var e in entries)
            {
                if (done >= maxRebuilds) break;
                if (e.Sig != null && !ReferenceEquals(e.SigOverlay, overlay)) { EnsureSig(e, overlay); done++; }
                foreach (var x in e.Extras)
                {
                    if (done >= maxRebuilds) break;
                    if (x.Sig != null && !ReferenceEquals(x.SigOverlay, overlay)) { EnsureSig(x, overlay); done++; }
                }
            }
            return done;
        }

        /// <summary>
        /// 이 프레임이 어느 뷰인지. 알아보면 그 뷰, 어느 뷰와도 안 맞으면(애매하지 않을 때) 새 뷰로 등록해 돌려준다.
        /// 프레임은 안정된(카메라가 멈춘) 화면이어야 한다 — 움직이는 중의 흐린 화면을 기준으로 삼지 않게.
        /// notView: 이 뷰는 후보에서 뺀다 — 카메라가 그 뷰를 떠났다고 볼 근거가 있을 때(자세 지켜보기가 잰 움직임 · 글자만 제자리인 빠른 전환 — ViewSession).
        /// atMs: 이 프레임의 영상 시각 — 보조 기준을 시각이 가까운 것부터 맞춘다(모르면 최근에 넣은 것부터).
        /// hint: 먼저 볼 뷰(지금·마지막으로 본 뷰 — 넉넉히 받아들이면 나머지 뷰를 보지 않는다)
        /// </summary>
        public ViewIdentification Identify(Mat frame, int notView = 0, double atMs = double.NaN, int hint = 0)
        {
            Entry[] entries;
            lock (_gate) entries = _entries.ToArray();
            LearnOverlayFromReferences(entries);
            var overlay = _overlay;   // 화면 고정 글자 — 기준 화면·새 화면 모두 그 자리에선 특징점을 뽑지 않는다(Compute)

            using var gray = ViewMatcher.PrepareGray(frame);
            using var probe = _matcher.Compute(gray, overlay);
            if (probe.Points.Length < _o.MinProbeFeatures)
                return new ViewIdentification(ViewOutcome.Unknown, 0, ViewMatch.None(probe.Points.Length));

            Entry? best = null;
            ViewMatch bestMatch = default;
            int bestInliers = -1;
            // 받아들이지 않은 뷰마다 가장 많이 맞은 것 — 기준 화면의 맞춤, 그리고 자세가 그 뷰의 범위 안이거나 인라이어가 넉넉한 보조 기준의 맞춤 가운데 (애매함 판정 — 아래)
            var rejected = new List<(Entry View, ViewMatch Top)>();
            // 단서 뷰를 먼저 — 그 뷰가 넉넉히 받아들이면 나머지는 보지 않는다 (OrderBy 는 안정 정렬이라 나머지는 번호 순 그대로)
            var order = hint != 0 ? entries.OrderBy(x => x.Id == hint ? 0 : 1).ToArray() : entries;
            foreach (var e in order)
            {
                if (e.Id == notView) continue;
                EnsureSig(e, overlay);   // 처음 쓰거나 글자를 더 배웠다 — 기준 화면의 서명을 지금 글자를 빼고 다시 만든다
                var m = _matcher.Compare(e.Sig!, probe, overlay);
                var top = m;
                // 보조 기준: 영상 시각이 가까운 것부터 몇 장만, 처음 받아들여지는 것에서 멈춘다. 자세는 뷰 기준 화면에 대한 것으로 이어 붙여 같은 기준(이동·배율 한계)으로 판정한다.
                // 기준 화면이 거의 안 맞아도 본다 — 낡은 기준 화면(연기 낀 0초)은 글자를 넓게 배운 뒤 인라이어 9 아래로 떨어져, 그런 뷰의 보조 기준을 건너뛰게 했더니(3차에 넣었다 뺌)
                // test6 1200초가 1200초 근처 보조 기준을 두고 새 뷰가 됐다(3차 실측 — 2차까지는 그 보조 기준으로 V1, 인라이어 425)
                foreach (var x in NearestFirst(e.Extras, atMs))
                {
                    var mx = MatchExtra(x, probe, overlay);
                    if (Accepts(mx))
                    {
                        if (!Accepts(m) || mx.Inliers > m.Inliers) m = mx;
                        break;
                    }
                    // 받아들여지지 않은 보조 기준은 자세가 그 뷰의 범위 안이거나(인라이어만 모자람) 인라이어가 넉넉할 때(범위 밖의 다른 자세라는 확실한 증거 — 애매함이 아니다)만
                    // 애매함 판정에 넣는다 — 같은 때의 차·연기만 닮은 화면이 처음 보는 자세를 인라이어 15~29 의 '애매함'에 묶어 새 뷰 등록을 몇 초씩 막았다
                    // (test6 처음 보는 자세 64장 중 42장, 2026-11-22 검토)
                    if ((Plausible(mx) || mx.Inliers >= _o.MinInliers) && mx.Inliers > top.Inliers) top = mx;
                }
                if (!Accepts(m)) rejected.Add((e, top));
                else if (m.Inliers > bestInliers) { best = e; bestMatch = m; bestInliers = m.Inliers; }
                // 글자를 뺀 장면이 화면에 퍼져 넉넉히 맞았다 — 다른 뷰가 더 잘 맞을 리 없다(뷰끼리는 허용 범위 밖의 다른 자세다)
                if (best == e && !overlay.IsEmpty && m.Inliers >= _o.SureInliers && m.SpreadMax >= _o.SureSpread) break;
            }
            if (best != null)
            {
                if (_firstViewId == 0) _firstViewId = best.Id;
                return new ViewIdentification(ViewOutcome.Known, best.Id, bestMatch);
            }

            // 맞는 뷰가 없다. 어느 뷰와 애매하게 닮았으면(그 뷰에서 가장 많이 맞은 것이 15~29 — 인라이어가 모자랄 뿐) 새 뷰로 쪼개 등록하지 않고 나중에 다시 본다.
            // 애매함은 뷰마다 본다 — 다른 뷰가 넉넉히 맞았다고(범위 밖의 다른 자세) 이 뷰의 애매함이 지워지지는 않는다. 그 맞춤으로 이 뷰의 자세가 아님이 드러날 때만 뺀다(RuledOutByOthers)
            ViewMatch most = default, unsure = default;
            foreach (var (e, top) in rejected)
            {
                if (top.Inliers > most.Inliers) most = top;
                if (top.Inliers >= _o.UnsureInliers && top.Inliers < _o.MinInliers && top.Inliers > unsure.Inliers && !RuledOutByOthers(e, rejected, overlay)) unsure = top;
            }
            if (unsure.Inliers > 0)
                return new ViewIdentification(ViewOutcome.Unknown, 0, unsure);
            if (entries.Length >= _o.MaxViews)
                return new ViewIdentification(ViewOutcome.Unknown, 0, most);

            Cv2.ImEncode(".jpg", gray, out byte[] jpeg, new ImageEncodingParam(ImwriteFlags.JpegQuality, _o.JpegQuality));
            int id;
            lock (_gate)
            {
                id = _nextId++;
                _entries.Add(new Entry(id, jpeg) { Sig = SignatureOf(jpeg, overlay), SigOverlay = overlay });
            }
            if (_firstViewId == 0) _firstViewId = id;
            return new ViewIdentification(ViewOutcome.New, id, most);
        }

        /// <summary>
        /// 애매한 뷰 e(가장 많이 맞은 것이 인라이어 15~29)가 다른 뷰의 넉넉한 맞춤으로 '이 자세가 아님'이 드러나나.
        /// 다른 뷰 b 가 이 화면과 넉넉히 맞았고(<see cref="Solid"/> — 자세가 b 의 범위 밖이라 받아들이지 않았다) 두 기준 화면끼리도 그만큼 맞으면, e 의 기준 화면 → 이 화면의 자세를
        /// b 를 거쳐 이어 붙일 수 있다. 그 자세가 e 의 범위 밖이면 e 가 아니다 — e 의 애매한 짝은 덜 가려진 글자 둘레나 겹친 장면이다. 범위 안이면 오히려 e 의 자리일 수 있으니 애매한 채로 두고,
        /// 두 기준 화면을 이을 수 없으면(겹치지 않거나 한쪽이 낡았다) b 의 맞춤은 e 에 대해 말해 주는 것이 없으니 역시 그대로 둔다.
        /// <para>
        /// 예전엔 애매함을 '어느 뷰든 가장 많이 맞은 것' 하나로 봐서, 한 뷰가 범위 밖 자세로 30 이상 맞으면 다른 뷰의 15~29 가 묻혔다: 기준 화면이 낡은(흐리고 연기 낀) 줌아웃 뷰 V2 의
        /// 자리로 돌아온 화면이 V2 와는 29 로 애매한데, 겹치는 V1 과 배율 0.73 으로 33 맞았다고 곧바로 새 뷰 V3 으로 쪼개졌다(2026-11-25 Gemini 리뷰 6-5 · 셀프 테스트 29).
        /// 리뷰의 권장대로 뷰마다 따로만 보면 반대로, 처음 보는 자세가 다른 뷰의 글자 둘레 짝 15~29 에 묶여 새 뷰가 되지 못한다(셀프 테스트 8·22 의 장면 — 40% 팬한 흐린 화면이
        /// V1 과 이동 40% 로 41 · V2 와 제자리 25: 두 기준 화면이 배율 0.74 로 57 맞아 이어 붙이면 V2 의 범위 밖 → 새 뷰 V3, 셀프 테스트 30)
        /// </para>
        /// </summary>
        private bool RuledOutByOthers(Entry e, List<(Entry View, ViewMatch Top)> rejected, OverlayMask overlay)
        {
            bool ruledOut = false;
            foreach (var (b, mb) in rejected)
            {
                if (ReferenceEquals(b, e) || !Solid(mb)) continue;
                var between = _matcher.Compare(e.Sig!, b.Sig!, overlay);   // e 의 기준 화면 → b 의 기준 화면
                if (!Solid(between)) continue;
                var h = ViewMatcher.Chain(between.H!, mb.H!);               // → 이 화면
                var (shift, scale, skew) = ViewMatcher.Geometry(h, e.Sig!.Width, e.Sig.Height);
                if (shift <= _o.MaxShift && scale >= _o.MinScale && scale <= _o.MaxScale && skew <= _o.MaxSkew) return false;   // b 를 거쳐 재도 e 의 자리다 (NaN 이면 범위 밖)
                ruledOut = true;
            }
            return ruledOut;
        }

        /// <summary>자세를 믿을 만한 맞춤 — 인라이어·칸이 같은 뷰로 받아들일 만큼이고 원근이 정상이다 (이동·배율은 보지 않는다 — 다른 자세여도 된다)</summary>
        private bool Solid(ViewMatch m) => m.H != null && m.Inliers >= _o.MinInliers && m.Cells >= _o.MinCells && m.Skew <= _o.MaxSkew;

        /// <summary>식별에서 맞춰 볼 보조 기준 — 영상 시각이 가까운 것부터(모르면 최근에 넣은 것부터) <see cref="ViewIdentityOptions.ExtrasPerIdentify"/> 장</summary>
        private List<Extra> NearestFirst(List<Extra> extras, double atMs)
        {
            if (extras.Count == 0) return [];
            IEnumerable<Extra> ordered = double.IsNaN(atMs)
                ? Enumerable.Reverse(extras)   // List.Reverse() 는 제자리 뒤집기라 쓰지 않는다
                : extras.OrderBy(x => double.IsNaN(x.AtMs) ? double.PositiveInfinity : Math.Abs(x.AtMs - atMs));
            return ordered.Take(_o.ExtrasPerIdentify).ToList();
        }

        /// <summary>보조 기준과 맞춘 결과를 뷰 기준 화면에 대한 자세로 이어 붙인다 (식별·보조 기준 정리가 같은 기준으로 판정하게)</summary>
        private ViewMatch MatchExtra(Extra x, ViewSignature probe, OverlayMask overlay)
        {
            EnsureSig(x, overlay);
            var mx = _matcher.Compare(x.Sig!, probe, overlay);
            if (mx.H == null) return mx;
            var h = ViewMatcher.Chain(x.Pose, mx.H);
            var (sh, sc, sk) = ViewMatcher.Geometry(h, x.Sig!.Width, x.Sig.Height);
            return mx with { Shift = sh, Scale = sc, Skew = sk, H = h };
        }

        private void EnsureSig(Entry e, OverlayMask overlay)
        {
            if (e.Sig != null && ReferenceEquals(e.SigOverlay, overlay)) return;
            e.Sig?.Dispose();
            e.Sig = SignatureOf(e.Jpeg, overlay);
            e.SigOverlay = overlay;
        }

        private void EnsureSig(Extra x, OverlayMask overlay)
        {
            if (x.Sig != null && ReferenceEquals(x.SigOverlay, overlay)) return;
            x.Sig?.Dispose();
            x.Sig = SignatureOf(x.Jpeg, overlay);
            x.SigOverlay = overlay;
        }

        private ViewSignature SignatureOf(byte[] jpeg, OverlayMask? overlay)
        {
            using var g = Cv2.ImDecode(jpeg, ImreadModes.Grayscale);
            return _matcher.Compute(g, overlay);
        }

        /// <summary>
        /// 뷰 기준 화면끼리 제자리로 맞은 짝을 화면 고정 글자로 배운다 — 다른 뷰(카메라 자세)의 기준 화면에서 같은 자리에 같은 특징점이 있으면 장면이 아니라
        /// 덧씌운 글자다. 구역 파일에서 뷰를 여럿 읽은 새 세션도 처음부터 글자를 빼고 맞추게 (자세 지켜보기가 움직임을 보기 전에도).
        /// 글자로 보이는 짝만(<see cref="TextLike"/> — 특징점의 절반을 넘거나 화면 전체에 퍼진 제자리 짝은 장면까지 제자리라 안 배운다)
        /// </summary>
        private void LearnOverlayFromReferences(Entry[] entries)
        {
            if (_overlayPairsFrom >= entries.Length) return;
            for (int j = Math.Max(1, _overlayPairsFrom); j < entries.Length; j++)
            {
                // 글자를 빼지 않은 서명으로 맞춘다 — 글자 짝을 찾는 것이니(식별용 Sig 는 글자를 빼고 만든다)
                var sj = entries[j].RawSig ??= SignatureOf(entries[j].Jpeg, null);
                for (int i = 0; i < j; i++)
                {
                    var si = entries[i].RawSig ??= SignatureOf(entries[i].Jpeg, null);
                    var (r, p) = _matcher.Pairs(si, sj);
                    var pinned = ViewMatcher.Pinned(r, p, _o.PinnedPx);
                    int features = Math.Min(si.Points.Length, sj.Points.Length);
                    if (TextLike(pinned, features, si.Width, si.Height)) LearnOverlay(pinned, si.Width, si.Height);   // 배운 뒤 넓이 한도 안에서만
                }
            }
            _overlayPairsFrom = entries.Length;
        }

        public void Dispose()
        {
            foreach (var e in _entries)
            {
                e.Sig?.Dispose();
                e.RawSig?.Dispose();
                foreach (var x in e.Extras) x.Sig?.Dispose();
            }
            _matcher.Dispose();
        }
    }
}
