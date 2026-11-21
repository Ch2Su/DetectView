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
    /// H 는 그 호모그래피(3×3 행 우선, [8] = 1 로 맞춤 — 맞추지 못했으면 null). 자세 지켜보기(CameraPoseWatch)가 뷰 기준 화면에 대한 자세로 이어 쓴다</summary>
    internal readonly record struct ViewMatch(int Features, int Good, int Inliers, int Cells, double Shift, double Scale, double Skew, double[]? H = null)
    {
        public static ViewMatch None(int features = 0, int good = 0) => new(features, good, 0, 0, 0, 1, 0);
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
                if (r.Width > 0 && r.Height > 0) m[r].SetTo(Scalar.All(0));
            }
            return m;
        }

        /// <summary>제자리 짝들을 더한 새 마스크. 가까운 짝끼리 묶어(<see cref="LinkPx"/>) 묶음마다 사각형(+여백)을 덮는다 — 혼자 떨어진 짝은 그 둘레만</summary>
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
            return new OverlayMask(cells, Points + n);
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
            for (int i = 0; i < inlier.Length && i < good; i++)
            {
                if (inlier[i] == 0) continue;
                n++;
                int cx = Math.Clamp((int)(src[i].X / width * GridX), 0, GridX - 1);
                int cy = Math.Clamp((int)(src[i].Y / height * GridY), 0, GridY - 1);
                cells[cy * GridX + cx] = true;
            }

            var (shift, scale, skew) = Geometry(a, width, height);
            if (double.IsNaN(shift)) return ViewMatch.None(features, good);
            return new ViewMatch(features, good, n, cells.Count(c => c), shift, scale, skew, a);
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
        /// <summary>뷰마다 둘 보조 기준 화면 수(AddRecent — 오래된 것부터 버린다). 1분마다 하나면 최근 16분</summary>
        public int MaxExtras { get; init; } = 16;
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

        /// <summary>보조 기준 화면 — 같은 뷰를 지켜보는 동안 찍어 둔 것. Pose = 뷰 기준 화면 → 이 화면 호모그래피</summary>
        private sealed class Extra(byte[] jpeg, double[] pose)
        {
            public byte[] Jpeg { get; } = jpeg;
            public double[] Pose { get; } = pose;
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

        public bool Accepts(ViewMatch m) =>
            m.Inliers >= _o.MinInliers && m.Cells >= _o.MinCells && m.Shift <= _o.MaxShift
            && m.Scale >= _o.MinScale && m.Scale <= _o.MaxScale && m.Skew <= _o.MaxSkew;

        /// <summary>배운 화면 고정 글자 자리 — 식별·자세 지켜보기의 맞춤에서 뺀다 (루프 스레드가 바꾸고 읽는다)</summary>
        public OverlayMask Overlay => _overlay;

        /// <summary>카메라가 움직인 앞뒤 화면에서 제자리로 맞은 짝(= 화면 고정 글자)을 더한다 (CameraPoseWatch 가 판정한 때)</summary>
        public void LearnOverlay(IReadOnlyList<Point2d>? pinned)
        {
            if (pinned is { Count: > 0 }) _overlay = _overlay.With(pinned);
        }

        /// <summary>
        /// 지금 화면을 그 뷰의 보조 기준 화면으로 더한다 (루프 스레드 — 자세 지켜보기가 그 뷰에 서 있다고 본 화면, 1분마다). pose = 뷰 기준 화면 → 이 화면.
        /// 같은 자세라도 연기가 걷히고 정체가 생기면 처음 기준 화면과 장면이 달라, 글자를 빼고 맞추면 맞춤이 약해졌다(test6: 0초 기준 ↔ 800~1410초 인라이어 15~21 → 알 수 없음).
        /// 이 세션에서만 쓰고 파일엔 안 적는다(구역 파일 판 2 그대로). 뷰마다 최근 <see cref="ViewIdentityOptions.MaxExtras"/> 장
        /// </summary>
        public void AddRecent(int viewId, Mat frame, double[] pose)
        {
            Entry? e;
            lock (_gate) e = _entries.FirstOrDefault(x => x.Id == viewId);
            if (e == null) return;
            using var gray = ViewMatcher.PrepareGray(frame);
            Cv2.ImEncode(".jpg", gray, out byte[] jpeg, new ImageEncodingParam(ImwriteFlags.JpegQuality, _o.JpegQuality));
            e.Extras.Add(new Extra(jpeg, (double[])pose.Clone()));
            if (e.Extras.Count > _o.MaxExtras)
            {
                e.Extras[0].Sig?.Dispose();
                e.Extras.RemoveAt(0);
            }
        }

        /// <summary>그 뷰의 보조 기준 화면 수 (검사용)</summary>
        public int ExtraCount(int viewId)
        {
            lock (_gate) return _entries.FirstOrDefault(x => x.Id == viewId)?.Extras.Count ?? 0;
        }

        /// <summary>
        /// 카메라가 그 뷰에서 다른 자세로 움직였다고 잰 직후(루프 스레드): 그 뷰의 보조 기준 가운데 움직인 뒤 화면과 같은 자세로 맞는 것을 버린다 — 글자를 배우기 전에
        /// 다른 자세를 글자만으로 그 뷰로 알아봐 보조 기준으로 들어간 화면이다(그대로 두면 그 자세가 계속 그 뷰로 알아봐진다). 글자를 배운 뒤(LearnOverlay 다음)에 부른다. 버린 수
        /// </summary>
        public int PurgeExtrasLike(int viewId, Mat frame)
        {
            Entry? e;
            lock (_gate) e = _entries.FirstOrDefault(x => x.Id == viewId);
            if (e == null || e.Extras.Count == 0) return 0;
            var overlay = _overlay;
            using var gray = ViewMatcher.PrepareGray(frame);
            using var probe = _matcher.Compute(gray, overlay);
            int removed = 0;
            for (int i = e.Extras.Count - 1; i >= 0; i--)
            {
                var x = e.Extras[i];
                if (x.Sig == null || !ReferenceEquals(x.SigOverlay, overlay))
                {
                    x.Sig?.Dispose();
                    x.Sig = SignatureOf(x.Jpeg, overlay);
                    x.SigOverlay = overlay;
                }
                if (!Accepts(_matcher.Compare(x.Sig, probe, overlay))) continue;
                x.Sig?.Dispose();
                e.Extras.RemoveAt(i);
                removed++;
            }
            return removed;
        }

        /// <summary>
        /// 이 프레임이 어느 뷰인지. 알아보면 그 뷰, 어느 뷰와도 안 맞으면(애매하지 않을 때) 새 뷰로 등록해 돌려준다.
        /// 프레임은 안정된(카메라가 멈춘) 화면이어야 한다 — 움직이는 중의 흐린 화면을 기준으로 삼지 않게.
        /// notView: 이 뷰는 후보에서 뺀다 — 자세 지켜보기가 카메라가 그 뷰의 허용 범위 밖으로 움직였다고 판정한 직후(화면 고정 글자만으로 그 뷰에 맞는 것을 막는다)
        /// </summary>
        public ViewIdentification Identify(Mat frame, int notView = 0)
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
            Entry? mostInliers = null;
            ViewMatch mostMatch = default;
            foreach (var e in entries)
            {
                if (e.Id == notView) continue;
                if (e.Sig == null || !ReferenceEquals(e.SigOverlay, overlay))
                {
                    // 처음 쓰거나 글자를 더 배웠다 — 기준 화면의 서명을 지금 글자를 빼고 다시 만든다
                    e.Sig?.Dispose();
                    e.Sig = SignatureOf(e.Jpeg, overlay);
                    e.SigOverlay = overlay;
                }
                var m = _matcher.Compare(e.Sig, probe, overlay);
                // 보조 기준 화면들과도 맞춰 가장 나은 것 — 자세는 뷰 기준 화면에 대한 것으로 이어 붙여 같은 기준(이동·배율 한계)으로 판정한다
                foreach (var x in e.Extras)
                {
                    if (x.Sig == null || !ReferenceEquals(x.SigOverlay, overlay))
                    {
                        x.Sig?.Dispose();
                        x.Sig = SignatureOf(x.Jpeg, overlay);
                        x.SigOverlay = overlay;
                    }
                    var mx = _matcher.Compare(x.Sig, probe, overlay);
                    if (mx.H != null)
                    {
                        var h = ViewMatcher.Chain(x.Pose, mx.H);
                        var (sh, sc, sk) = ViewMatcher.Geometry(h, x.Sig.Width, x.Sig.Height);
                        mx = mx with { Shift = sh, Scale = sc, Skew = sk, H = h };
                    }
                    bool better = Accepts(mx) ? !Accepts(m) || mx.Inliers > m.Inliers : !Accepts(m) && mx.Inliers > m.Inliers;
                    if (better) m = mx;
                }
                if (Accepts(m) && m.Inliers > bestInliers) { best = e; bestMatch = m; bestInliers = m.Inliers; }
                if (mostInliers == null || m.Inliers > mostMatch.Inliers) { mostInliers = e; mostMatch = m; }
            }
            if (best != null)
            {
                if (_firstViewId == 0) _firstViewId = best.Id;
                return new ViewIdentification(ViewOutcome.Known, best.Id, bestMatch);
            }

            // 맞는 뷰가 없다. 어느 뷰와 애매하게 닮았으면(인라이어가 모자랄 뿐) 새 뷰로 쪼개 등록하지 않고 나중에 다시 본다
            if (mostInliers != null && mostMatch.Inliers >= _o.UnsureInliers && mostMatch.Inliers < _o.MinInliers)
                return new ViewIdentification(ViewOutcome.Unknown, 0, mostMatch);
            if (entries.Length >= _o.MaxViews)
                return new ViewIdentification(ViewOutcome.Unknown, 0, mostMatch);

            Cv2.ImEncode(".jpg", gray, out byte[] jpeg, new ImageEncodingParam(ImwriteFlags.JpegQuality, _o.JpegQuality));
            int id;
            lock (_gate)
            {
                id = _nextId++;
                _entries.Add(new Entry(id, jpeg) { Sig = SignatureOf(jpeg, overlay), SigOverlay = overlay });
            }
            if (_firstViewId == 0) _firstViewId = id;
            return new ViewIdentification(ViewOutcome.New, id, mostMatch);
        }

        private ViewSignature SignatureOf(byte[] jpeg, OverlayMask? overlay)
        {
            using var g = Cv2.ImDecode(jpeg, ImreadModes.Grayscale);
            return _matcher.Compute(g, overlay);
        }

        /// <summary>
        /// 뷰 기준 화면끼리 제자리로 맞은 짝을 화면 고정 글자로 배운다 — 다른 뷰(카메라 자세)의 기준 화면에서 같은 자리에 같은 특징점이 있으면 장면이 아니라
        /// 덧씌운 글자다. 구역 파일에서 뷰를 여럿 읽은 새 세션도 처음부터 글자를 빼고 맞추게 (자세 지켜보기가 움직임을 보기 전에도).
        /// 제자리 짝이 특징점의 <see cref="ViewIdentityOptions.OverlayMaxFraction"/> 를 넘는 두 뷰는 사실상 같은 자리라(장면까지 제자리) 배우지 않는다
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
                    double maxCells = 0.25 * si.Width * si.Height / (OverlayMask.CellPx * OverlayMask.CellPx);   // CameraPoseWatch 의 MaxOverlayArea 와 같은 한도
                    if (pinned.Count >= 8 && pinned.Count < _o.OverlayMaxFraction * features && _overlay.Cells <= maxCells) LearnOverlay(pinned);
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
