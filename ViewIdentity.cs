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

    /// <summary>두 화면을 맞춰 본 결과. 기준 화면 → 새 화면 호모그래피의 크기·이동으로 '같은 자리에서 찍은 화면'인지 가린다</summary>
    internal readonly record struct ViewMatch(int Features, int Good, int Inliers, int Cells, double Shift, double Scale, double Skew)
    {
        public static ViewMatch None(int features = 0, int good = 0) => new(features, good, 0, 0, 0, 1, 0);
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

        /// <summary>흑백 그림(PrepareGray 의 결과 또는 그것을 JPEG 로 저장했다 읽은 것) → 서명</summary>
        public ViewSignature Compute(Mat gray)
        {
            using var desc = new Mat();
            _orb.DetectAndCompute(gray, null, out KeyPoint[] kps, desc);
            var pts = kps.Select(k => k.Pt).ToArray();
            return new ViewSignature(gray.Width, gray.Height, pts, desc.Clone());
        }

        /// <summary>기준 화면 → 새 화면 호모그래피를 구해 어긋난 정도를 잰다. 일치가 모자라면 인라이어 0</summary>
        public ViewMatch Compare(ViewSignature reference, ViewSignature probe)
        {
            int features = Math.Min(reference.Points.Length, probe.Points.Length);
            if (reference.Descriptors.Empty() || probe.Descriptors.Empty() || reference.Points.Length < 8 || probe.Points.Length < 8)
                return ViewMatch.None(features);

            DMatch[][] knn = _bf.KnnMatch(probe.Descriptors, reference.Descriptors, 2);
            var src = new List<Point2d>();   // 기준 화면 좌표
            var dst = new List<Point2d>();   // 새 화면 좌표
            foreach (var m in knn)
            {
                if (m.Length < 2 || m[0].Distance >= 0.8f * m[1].Distance) continue;   // Lowe 비율 검사 — 애매한 짝은 버린다
                var r = reference.Points[m[0].TrainIdx];
                var p = probe.Points[m[0].QueryIdx];
                src.Add(new Point2d(r.X, r.Y));
                dst.Add(new Point2d(p.X, p.Y));
            }
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
            for (int i = 0; i < inlier.Length && i < src.Count; i++)
            {
                if (inlier[i] == 0) continue;
                n++;
                int cx = Math.Clamp((int)(src[i].X / reference.Width * GridX), 0, GridX - 1);
                int cy = Math.Clamp((int)(src[i].Y / reference.Height * GridY), 0, GridY - 1);
                cells[cy * GridX + cx] = true;
            }

            double cxm = reference.Width / 2.0, cym = reference.Height / 2.0;
            double w = a[6] * cxm + a[7] * cym + 1;
            if (Math.Abs(w) < 1e-9) return ViewMatch.None(features, good);
            double mx = (a[0] * cxm + a[1] * cym + a[2]) / w, my = (a[3] * cxm + a[4] * cym + a[5]) / w;
            double shift = Math.Sqrt((mx - cxm) * (mx - cxm) + (my - cym) * (my - cym)) / reference.Width;
            double det = a[0] * a[4] - a[1] * a[3];
            double scale = det > 0 ? Math.Sqrt(det) : 0;
            double skew = (Math.Abs(a[6]) + Math.Abs(a[7])) * reference.Width;   // 원근 항 — 크면 호모그래피가 무너진 것
            return new ViewMatch(features, good, n, cells.Count(c => c), shift, scale, skew);
        }

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
        }

        private readonly ViewIdentityOptions _o;
        private readonly ViewMatcher _matcher = new();
        private readonly System.Threading.Lock _gate = new();
        private readonly List<Entry> _entries = [];
        private int _nextId;
        private volatile int _firstViewId;

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

        /// <summary>
        /// 이 프레임이 어느 뷰인지. 알아보면 그 뷰, 어느 뷰와도 안 맞으면(애매하지 않을 때) 새 뷰로 등록해 돌려준다.
        /// 프레임은 안정된(카메라가 멈춘) 화면이어야 한다 — 움직이는 중의 흐린 화면을 기준으로 삼지 않게
        /// </summary>
        public ViewIdentification Identify(Mat frame)
        {
            using var gray = ViewMatcher.PrepareGray(frame);
            using var probe = _matcher.Compute(gray);
            if (probe.Points.Length < _o.MinProbeFeatures)
                return new ViewIdentification(ViewOutcome.Unknown, 0, ViewMatch.None(probe.Points.Length));

            Entry[] entries;
            lock (_gate) entries = _entries.ToArray();

            Entry? best = null;
            ViewMatch bestMatch = default;
            int bestInliers = -1;
            Entry? mostInliers = null;
            ViewMatch mostMatch = default;
            foreach (var e in entries)
            {
                e.Sig ??= SignatureOf(e.Jpeg);
                var m = _matcher.Compare(e.Sig, probe);
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
                _entries.Add(new Entry(id, jpeg) { Sig = SignatureOf(jpeg) });
            }
            if (_firstViewId == 0) _firstViewId = id;
            return new ViewIdentification(ViewOutcome.New, id, mostMatch);
        }

        private ViewSignature SignatureOf(byte[] jpeg)
        {
            using var g = Cv2.ImDecode(jpeg, ImreadModes.Grayscale);
            return _matcher.Compute(g);
        }

        public void Dispose()
        {
            foreach (var e in _entries) e.Sig?.Dispose();
            _matcher.Dispose();
        }
    }
}
