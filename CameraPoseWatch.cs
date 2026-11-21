using System;
using System.Collections.Generic;
using System.Linq;
using OpenCvSharp;

namespace DetectView
{
    /// <summary>자세 지켜보기 파라미터 — 값은 TrackerCheck <c>poseseries</c>·<c>posewatch</c> 실측(2026-11-21, test1~6·사고 영상)으로 골랐다</summary>
    internal sealed record PoseWatchOptions
    {
        /// <summary>지켜볼 때 이만큼(영상 ms)마다 한 번 맞춰 본다 — 한 번에 특징점·맞춤 약 6~11 ms(1280×720~1620×1080)라 프레임마다는 못 한다</summary>
        public double CheckMs { get; init; } = 1000;
        /// <summary>움직이는 중엔 이만큼마다 — 멈췄는지 본다</summary>
        public double MovingCheckMs { get; init; } = 500;
        /// <summary>비교 기준(최근 화면)을 새로 잡는 간격. 오래 두면 연기·정체 같은 내용 변화로 맞춤이 약해지고, 자주 바꾸면 자세 오차가 쌓인다</summary>
        public double KeyRefreshMs { get; init; } = 10000;
        /// <summary>기준을 새로 잡는 건 인라이어가 평소의 이 비율 이상인 성한 화면일 때만 — 연기 띠가 막 덮은 화면을 기준으로 잡으면 연기가 걷힐 때 헛경보가 났다(합성 검사).
        /// 내용이 오래 바뀌어 있으면 평소 수준(가운데값)이 따라 내려와 결국 새로 잡힌다</summary>
        public double RefreshMinRatio { get; init; } = 0.8;
        /// <summary>두 화면의 이동·배율 차이가 이 안쪽이면 카메라가 서 있다 (서 있는 영상의 1초 간격 확인은 0.0~0.7%)</summary>
        public double StillShift { get; init; } = 0.01;
        public double StillScale { get; init; } = 0.01;
        /// <summary>
        /// 인라이어가 앞 확인들의 가운데값에 대해 이 비율 아래면 장면 맞춤이 무너진 것 — 움직이는 중으로 본다. 카메라가 서 있으면 압축 잡음까지 픽셀이 같아
        /// 약한 무늬도 맞아 인라이어가 많은데(test6 가운데 597) 조금만 움직여도 그것들이 사라지고 화면 고정 글자만 제자리로 남는다(팬 26% · 되돌림 33% · 줌 25%).
        /// 서 있는 영상의 최저는 57% (1초 간격, 2026-11-21 poseseries — test1~5·사고 영상 넷·test6)
        /// </summary>
        public double CollapseRatio { get; init; } = 0.4;
        /// <summary>가운데값을 낼 앞 확인 수</summary>
        public int BaselineChecks { get; init; } = 5;
        /// <summary>이만큼 지나도 멈추지 않으면 그 자리에서 판정한다 (계속 도는 카메라에서 상태가 안 풀리지 않게)</summary>
        public double MaxMovingMs { get; init; } = 10000;
        /// <summary>
        /// 멈춘 뒤 움직임 앞뒤를 견줄 때의 그림 폭·특징점(SIFT). 지켜보기의 ORB(가로 480)로는 카메라가 움직인 앞뒤의 장면 짝이 수십 개뿐이라
        /// test6 의 2.6% 팬을 배율 0.894 로 잘못 재고 줌은 못 쟀다. 가로 960 SIFT 는 팬을 인라이어 296 · 이동 2.6% · 배율 1.003, 줌을 51~55 · 배율 0.72~0.73 으로 쟀고
        /// 서 있는 앞뒤에선 헛움직임이 없었다(인라이어 14). 무거워(견주기 한 번 120~250 ms — 두 그림의 SIFT 와 맞춤) 움직임이 멈춘 뒤 한 번만 쓴다
        /// </summary>
        public int ConfirmWidth { get; init; } = 960;
        public int ConfirmFeatures { get; init; } = 2000;
        /// <summary>앞뒤 견주기에서 움직임으로 믿을 맞춤 — 제자리 짝을 뺀 나머지의 이 비율 이상을 설명해야 한다 (줌 28~30%, 서 있는 앞뒤의 우연한 맞춤 16%)</summary>
        public double ConfirmMinExplained { get; init; } = 0.2;
        /// <summary>화면 고정 글자가 화면의 이 비율 넘게 덮이면 더 배우지 않는다 (test6 의 글자는 약 4.5%) — 잘못 배운 장면이 맞춤을 줄여 헛경보를 부르는 고리를 끊으려고</summary>
        public double MaxOverlayArea { get; init; } = 0.25;
        /// <summary>서 있는 동안 이만큼(영상 ms)마다 지금 화면을 그 뷰의 보조 기준 화면으로 더한다(ViewRegistry.AddRecent) — 시크해 돌아왔을 때 내용이 바뀐 같은 자세를 알아보게</summary>
        public double RecentEveryMs { get; init; } = 60000;
    }

    internal enum PoseOutcome
    {
        /// <summary>확인할 때가 아니었다 · 지켜보지 않는다</summary>
        None,
        /// <summary>카메라가 그 자리다 (허용 범위 안에서 조금 움직였어도)</summary>
        Still,
        /// <summary>움직이는 중 — 멈출 때까지 추적기에 넣지 않는다</summary>
        Moving,
        /// <summary>멈췄고 다른 자세다 — 화면 전환으로 처리하고 뷰를 다시 알아본다(지켜보기는 꺼진다 — 식별한 뒤 Start)</summary>
        Moved,
        /// <summary>멈췄고 그 자리다(되돌아왔거나 허용 범위 안의 작은 팬·줌, 장면 내용만 바뀐 것) — 이어서 지켜본다</summary>
        Resumed,
    }

    /// <param name="Measured">Moved 일 때 움직임을 재서 뷰의 허용 범위 밖임을 확인했다 — 그때만 지금 뷰를 식별 후보에서 빼고(Identify notView) Overlay 를 글자로 배운다.
    /// 재지 못한 Moved(앞 화면과 견줄 수 없을 만큼 바뀜)는 보통의 화면 전환처럼 다시 식별만 한다</param>
    /// <param name="Overlay">Measured Moved 일 때 움직임 앞뒤에서 제자리로 맞은 짝(= 화면 고정 글자, 가로 480 좌표) — <see cref="ViewRegistry.LearnOverlay"/> 에 넘긴다</param>
    /// <param name="Reason">움직임으로 본 까닭 · 판정 (디버그·실험 도구 표시용)</param>
    internal readonly record struct PoseStep(PoseOutcome Outcome, int Inliers = 0, int Baseline = 0, double Shift = 0, double Scale = 1,
                                             bool Measured = false, IReadOnlyList<Point2d>? Overlay = null, string Reason = "");

    /// <summary>
    /// 자세 지켜보기 (2026-11-21) — 뷰를 알아본 뒤 카메라가 그 뷰의 허용 범위(이동 6% · 배율 0.9~1.12) 밖으로 움직였는지 1초마다 ORB 맞춤으로 본다.
    /// 화면 전환 감지(<see cref="SceneChangeDetector"/>)는 연속한 두 프레임의 밝기 차이로 보므로 천천히 하는 줌·팬은 못 잡고(test6 1416초 줌아웃 — d 최대 3.4),
    /// 뷰 식별도 영상을 열 때·시크·전환 뒤에만 해서 그 사이 카메라가 옮겨 가면 구역이 어긋난 채 남았다.
    /// <para>
    /// 지켜보기: 비교 기준은 최근 화면(10초마다 새로)이고 그 화면의 자세(뷰 기준 화면 → 비교 기준 호모그래피)를 이어 붙여 둔다. 1초마다
    /// ① 제자리이고 인라이어가 평소만큼이면 서 있다 ② 맞춤이 제자리가 아니면 자세를 이어 붙여 허용 범위 밖이면 움직이는 중(안이면 그 자리에서 다시 잰다)
    /// ③ 인라이어가 평소의 40% 아래로 무너졌거나 맞춤이 안 되면 움직이는 중 — ③이 필요한 까닭: 화면 고정 글자는 카메라가 움직여도 제자리라, 장면이 바뀌면
    /// 전체 맞춤이 글자만으로 '제자리'가 된다(test6 줌: 597 → 131, 칸 7). 맞춤은 글자를 빼지 않고 한다 — 빼면 남은 장면이 몇 칸에 몰린 화면에서 흔들려 헛경보가 났다.
    /// </para>
    /// <para>
    /// 움직이는 중엔 0.5초마다 이웃한 두 화면이 '서 있는' 맞춤이 되는지 보고, 멈추면 움직이기 전 화면과 가로 960 SIFT 로 견줘(제자리 짝을 뺀 나머지로 움직임을 잰다) 판정한다:
    /// 잰 움직임이 허용 범위 밖이면 Moved(Measured — 제자리 짝을 글자로 돌려준다), 안이면 Resumed. 재지 못하면 앞 화면과 다시 평소만큼 맞을 때 Resumed, 아니면 재지 못한 Moved.
    /// </para>
    /// ORB·SIFT·매처를 재사용하므로 한 스레드(재생 루프)에서만 쓴다. 프레임은 쥐고 있지 않는다(서명과 작은 흑백 그림만 남긴다)
    /// </summary>
    internal sealed class CameraPoseWatch : IDisposable
    {
        private enum State { Off, Watching, Moving }

        private readonly PoseWatchOptions _o;
        private readonly ViewIdentityOptions _v;
        private readonly ViewMatcher _m = new();
        private OpenCvSharp.Features2D.SIFT? _sift;   // OpenCvSharp 4.13 은 SIFT 를 Features2D 이름공간에 둔다
        private BFMatcher? _l2;
        private State _state;
        private ViewSignature? _key;                     // 비교 기준 (최근 화면)
        private Mat? _keyGray;                           // 그 화면의 가로 ConfirmWidth 흑백 — 움직임 앞뒤를 SIFT 로 견줄 때
        private double[] _keyPose = ViewMatcher.Identity;   // 뷰 기준 화면 → 비교 기준
        private double _keySinceMs;
        private readonly List<int> _history = [];       // 서 있을 때의 인라이어 — 가운데값이 맞춤의 평소 수준
        private double _nextMs;
        private ViewSignature? _before;                  // 움직이기 전 마지막 비교 기준 — 멈춘 뒤 이것과 견준다
        private Mat? _beforeGray;
        private double[] _beforePose = ViewMatcher.Identity;
        private int _beforeBaseline;
        private ViewSignature? _last;                    // 움직이는 중 바로 앞 확인
        private double _movingSinceMs;
        private string _reason = "";

        public CameraPoseWatch(PoseWatchOptions? options = null, ViewIdentityOptions? view = null)
        {
            _o = options ?? new PoseWatchOptions();
            _v = view ?? new ViewIdentityOptions();
        }

        public PoseWatchOptions Options => _o;

        /// <summary>지켜보는 중 (Start 뒤 Moved·Stop 전)</summary>
        public bool Active => _state != State.Off;

        /// <summary>움직이는 중 — 루프는 이 동안 화면 전환 감지의 '움직임'처럼 추적기에 넣지 않는다</summary>
        public bool Moving => _state == State.Moving;

        /// <summary>이 시각에 확인할 차례인가 (아니면 프레임을 흑백으로 줄이는 비용도 들지 않게 Check 를 부르지 않는다)</summary>
        public bool Due(double ms) => _state != State.Off && ms >= _nextMs;

        /// <summary>지금 비교 기준의 자세 — 뷰 기준 화면 → 비교 기준 호모그래피 (복사본). 서 있는 화면을 보조 기준으로 더할 때 함께 넘긴다</summary>
        public double[] KeyPose => (double[])_keyPose.Clone();

        /// <summary>뷰를 알아본 화면에서 시작한다. refToFrame = 뷰 기준 화면 → 이 화면 호모그래피(식별 결과 — 새 뷰면 null = 제자리)</summary>
        public void Start(Mat frame, double[]? refToFrame, double ms)
        {
            Stop();
            using (var g = ViewMatcher.PrepareGray(frame)) _key = _m.Compute(g);
            _keyGray = Gray(frame, _o.ConfirmWidth);
            _keyPose = refToFrame ?? ViewMatcher.Identity;
            _keySinceMs = ms;
            _state = State.Watching;
            _nextMs = ms + _o.CheckMs;
        }

        /// <summary>시크·화면 전환·뷰를 모를 때 — 다시 Start 할 때까지 아무것도 보지 않는다</summary>
        public void Stop()
        {
            _state = State.Off;
            _key?.Dispose(); _key = null;
            _keyGray?.Dispose(); _keyGray = null;
            _before?.Dispose(); _before = null;
            _beforeGray?.Dispose(); _beforeGray = null;
            _last?.Dispose(); _last = null;
            _history.Clear();
            _reason = "";
        }

        /// <summary>확인할 차례면 이 프레임으로 맞춰 본다. overlay 는 이미 배운 글자의 넓이를 보고 더 배울지 정하는 데만 쓴다(맞춤에서 글자를 빼지 않는다 — 클래스 설명)</summary>
        public PoseStep Check(Mat frame, double ms, OverlayMask? overlay = null)
        {
            if (!Due(ms)) return default;
            ViewSignature probe;
            using (var g = ViewMatcher.PrepareGray(frame)) probe = _m.Compute(g);
            bool taken = false;   // probe 를 기준(_key·_last)으로 넘겼으면 여기서 해제하지 않는다
            try
            {
                return _state == State.Watching ? Watch(frame, probe, ms, ref taken) : Follow(frame, probe, ms, overlay, ref taken);
            }
            finally
            {
                if (!taken) probe.Dispose();
            }
        }

        private PoseStep Watch(Mat frame, ViewSignature probe, double ms, ref bool taken)
        {
            _nextMs = ms + _o.CheckMs;
            int baseline = Baseline();
            var a = Compare(_key!, probe);
            if (Standing(a, baseline))
            {
                // 서 있다
                _history.Add(a.Inliers);
                if (_history.Count > _o.BaselineChecks) _history.RemoveAt(0);
                if (ms - _keySinceMs >= _o.KeyRefreshMs && (baseline == 0 || a.Inliers >= _o.RefreshMinRatio * baseline))
                    Rekey(frame, probe, StandPose(_keyPose, a), ms, ref taken);
                return new PoseStep(PoseOutcome.Still, a.Inliers, baseline);
            }
            if (Reliable(a) && !IsStill(a))
            {
                // 장면이 움직인 게 재진다 (천천히 하는 줌·팬 — 장면 짝이 글자보다 많을 때)
                var pose = ViewMatcher.Chain(_keyPose, a.H!);
                var (shift, scale, skew) = ViewMatcher.Geometry(pose, probe.Width, probe.Height);
                if (!Beyond(shift, scale, skew))
                {
                    // 허용 범위 안에서 움직였다 — 여기서부터 다시 잰다. 평소 인라이어도 새 화면에서 다시 모은다
                    Rekey(frame, probe, pose, ms, ref taken);
                    _history.Clear();
                    return new PoseStep(PoseOutcome.Still, a.Inliers, baseline, shift, scale);
                }
                return BeginMoving(probe, ms, baseline, ref taken, $"자세가 허용 범위 밖 (이동 {shift * 100:0.0}% · 배율 {scale:0.000})");
            }
            return BeginMoving(probe, ms, baseline, ref taken,
                Matched(a) ? $"장면 맞춤이 무너짐 (인라이어 {a.Inliers} — 평소 {baseline})" : $"맞춤 실패 (인라이어 {a.Inliers})");
        }

        private PoseStep BeginMoving(ViewSignature probe, double ms, int baseline, ref bool taken, string reason)
        {
            _state = State.Moving;
            _before = _key;           // 움직이기 전 화면 — 멈춘 뒤 이것과 견준다
            _beforeGray = _keyGray;
            _beforePose = _keyPose;
            _beforeBaseline = baseline;
            _key = null;
            _keyGray = null;
            _last = probe;
            taken = true;
            _movingSinceMs = ms;
            _nextMs = ms + _o.MovingCheckMs;
            _reason = reason;
            return new PoseStep(PoseOutcome.Moving, 0, baseline, Reason: reason);
        }

        private PoseStep Follow(Mat frame, ViewSignature probe, double ms, OverlayMask? overlay, ref bool taken)
        {
            _nextMs = ms + _o.MovingCheckMs;
            // 멈췄나: 이웃한 두 화면이 '서 있는' 맞춤인가 — 전체 맞춤의 제자리만 보면 화면 고정 글자 탓에 도는 중에도 멈춘 것으로 보였다(test6 622초 팬)
            var step = Compare(_last!, probe);
            if (!Standing(step, _beforeBaseline) && ms - _movingSinceMs < _o.MaxMovingMs)
            {
                _last!.Dispose();
                _last = probe;
                taken = true;
                return new PoseStep(PoseOutcome.Moving, step.Inliers, _beforeBaseline, Reason: _reason);
            }

            // 멈췄다(또는 너무 오래 움직였다) — 움직이기 전 화면과 견줘 판정한다
            using var after = Gray(frame, _o.ConfirmWidth);
            var (motion, pinned, explained) = MeasureMove(_beforeGray!, after);
            if (motion != null)
            {
                var pose = ViewMatcher.Chain(_beforePose, motion);
                var (shift, scale, skew) = ViewMatcher.Geometry(pose, probe.Width, probe.Height);
                if (!Beyond(shift, scale, skew))
                    return Resume(frame, probe, pose, ms, ref taken, explained, shift, scale, $"{_reason} → 멈춘 뒤 이동 {shift * 100:0.0}% · 배율 {scale:0.000} (허용 범위 안)");
                // 그 움직임으로도 제자리인 짝(줌의 가운데 근처)은 글자가 아니니 뺀다
                var overlayPts = pinned.Where(p => Displacement(motion, p) > 2 * _v.PinnedPx).ToList();
                return Moved(true, explained, shift, scale, CapOverlay(overlayPts, overlay, probe),
                             $"{_reason} → 멈춘 뒤 이동 {shift * 100:0.0}% · 배율 {scale:0.000}");
            }
            // 움직임을 재지 못했다 — 앞 화면과 다시 평소만큼 맞으면 그 자리(연기·큰 차 같은 내용 변화), 아니면 견줄 수 없을 만큼 바뀐 화면
            var back = Compare(_before!, probe);
            if (Standing(back, _beforeBaseline))
                return Resume(frame, probe, StandPose(_beforePose, back), ms, ref taken, back.Inliers, 0, 1, $"{_reason} → 멈춘 뒤 앞 화면과 다시 맞음");
            return Moved(false, back.Inliers, 0, 1, null, $"{_reason} → 멈춘 뒤에도 앞 화면과 안 맞고 움직임도 못 잼 (인라이어 {back.Inliers} — 평소 {_beforeBaseline})");
        }

        private PoseStep Resume(Mat frame, ViewSignature probe, double[] pose, double ms, ref bool taken, int inliers, double shift, double scale, string reason)
        {
            int baseline = _beforeBaseline;
            _state = State.Watching;
            _before?.Dispose(); _before = null;
            _beforeGray?.Dispose(); _beforeGray = null;
            _last?.Dispose(); _last = null;
            Rekey(frame, probe, pose, ms, ref taken);
            _history.Clear();   // 움직인 뒤의 화면에서 평소 수준을 다시 모은다
            _nextMs = ms + _o.CheckMs;
            _reason = "";
            return new PoseStep(PoseOutcome.Resumed, inliers, baseline, shift, scale, Reason: reason);
        }

        private PoseStep Moved(bool measured, int inliers, double shift, double scale, IReadOnlyList<Point2d>? overlay, string reason)
        {
            int baseline = _beforeBaseline;
            Stop();
            return new PoseStep(PoseOutcome.Moved, inliers, baseline, shift, scale, measured, overlay, reason);
        }

        private void Rekey(Mat frame, ViewSignature probe, double[] pose, double ms, ref bool taken)
        {
            _key?.Dispose();
            _key = probe;
            taken = true;
            _keyGray?.Dispose();
            _keyGray = Gray(frame, _o.ConfirmWidth);
            _keyPose = pose;
            _keySinceMs = ms;
        }

        /// <summary>배울 글자 짝 — 이미 덮인 넓이가 한도를 넘었으면 더 배우지 않는다</summary>
        private IReadOnlyList<Point2d> CapOverlay(IReadOnlyList<Point2d> pts, OverlayMask? overlay, ViewSignature probe)
        {
            double cells = (double)probe.Width * probe.Height / (OverlayMask.CellPx * OverlayMask.CellPx);
            return (overlay?.Cells ?? 0) > _o.MaxOverlayArea * cells ? [] : pts;
        }

        /// <summary>
        /// 움직임 앞뒤(가로 ConfirmWidth 흑백)를 SIFT 로 맞춰, 제자리 짝을 뺀 나머지로 카메라 움직임을 잰다. 돌려주는 호모그래피와 제자리 짝은 가로 480(ViewMatcher) 좌표.
        /// 나머지의 ConfirmMinExplained 이상을 설명하고 인라이어 30 · 칸 4 를 넘어야 움직임으로 믿는다(서 있는 앞뒤의 나머지는 달리는 차 — 한 호모그래피로 설명되지 않는다)
        /// </summary>
        private (double[]? Motion, List<Point2d> Pinned, int Explained) MeasureMove(Mat before, Mat after)
        {
            _sift ??= OpenCvSharp.Features2D.SIFT.Create(_o.ConfirmFeatures);
            _l2 ??= new BFMatcher(NormTypes.L2, crossCheck: false);
            using var d1 = new Mat();
            using var d2 = new Mat();
            _sift.DetectAndCompute(before, null, out KeyPoint[] k1, d1);
            _sift.DetectAndCompute(after, null, out KeyPoint[] k2, d2);
            var pinned = new List<Point2d>();
            if (d1.Empty() || d2.Empty() || k1.Length < 8 || k2.Length < 8) return (null, pinned, 0);

            double f = (double)ViewMatcher.BaseWidth / before.Width;   // ConfirmWidth → 480 좌표
            double pinLim = _v.PinnedPx / f;                            // 제자리 판정도 같은 화면 비율로
            var src = new List<Point2d>();
            var dst = new List<Point2d>();
            foreach (var m in _l2.KnnMatch(d2, d1, 2))
            {
                if (m.Length < 2 || m[0].Distance >= 0.8f * m[1].Distance) continue;
                var a = k1[m[0].TrainIdx].Pt;
                var b = k2[m[0].QueryIdx].Pt;
                double dx = b.X - a.X, dy = b.Y - a.Y;
                if (dx * dx + dy * dy <= pinLim * pinLim) { pinned.Add(new Point2d(a.X * f, a.Y * f)); continue; }
                src.Add(new Point2d(a.X * f, a.Y * f));
                dst.Add(new Point2d(b.X * f, b.Y * f));
            }
            int w = ViewMatcher.BaseWidth, h = (int)Math.Round(before.Height * f);
            var mov = ViewMatcher.Measure(src, dst, w, h, Math.Min(k1.Length, k2.Length));
            bool ok = Reliable(mov) && mov.Inliers >= _o.ConfirmMinExplained * src.Count;
            return (ok ? mov.H : null, pinned, mov.Inliers);
        }

        private ViewMatch Compare(ViewSignature from, ViewSignature to) => _m.Compare(from, to);

        /// <summary>
        /// 카메라가 서 있는 맞춤 — 인라이어가 평소(baseline)의 CollapseRatio 이상이고, 제자리이거나 맞춤이 몇 칸(MinCells 미만)에만 몰렸다.
        /// 카메라가 움직이면 화면 전체가 같이 움직이니 몇 칸에 몰린 '움직임'은 화면 안의 물체다(합성 화면 녹화 webm 의 글자·도형이 헛경보 7건을 냈다, 2026-11-21).
        /// 연기 낀 화면은 서 있어도 맞춤이 몇 칸에 몰리므로 칸 수로 '서 있음'을 거르지는 않는다. 글자만 맞은 '제자리'는 인라이어가 평소보다 크게 적어 걸러진다
        /// </summary>
        private bool Standing(ViewMatch a, int baseline) =>
            Matched(a) && (IsStill(a) || a.Cells < _v.MinCells) && (baseline == 0 || a.Inliers >= _o.CollapseRatio * baseline);

        /// <summary>서 있는 맞춤 다음의 자세 — 제자리 맞춤이면 그 작은 어긋남까지 이어 붙이고, 몇 칸에 몰린 맞춤(화면 안 물체의 움직임)이면 자세를 그대로 둔다
        /// (물체의 움직임을 카메라 자세로 쌓지 않게)</summary>
        private double[] StandPose(double[] pose, ViewMatch a) => IsStill(a) ? ViewMatcher.Chain(pose, a.H!) : (double[])pose.Clone();

        private int Baseline()
        {
            if (_history.Count == 0) return 0;
            var s = _history.OrderBy(x => x).ToArray();
            return s[s.Length / 2];
        }

        /// <summary>맞춤이 됐다 (호모그래피 + 인라이어 30 + 원근 정상)</summary>
        private bool Matched(ViewMatch m) => m.H != null && m.Inliers >= _v.MinInliers && m.Skew <= _v.MaxSkew;

        /// <summary>움직임으로 믿을 만한 맞춤 — 칸도 4개 이상 (한 구석의 지나가는 차 무리가 카메라 움직임으로 보이지 않게)</summary>
        private bool Reliable(ViewMatch m) => Matched(m) && m.Cells >= _v.MinCells;

        private bool IsStill(ViewMatch m) => m.Shift <= _o.StillShift && Math.Abs(m.Scale - 1) <= _o.StillScale;

        private bool Beyond(double shift, double scale, double skew) =>
            double.IsNaN(shift) || shift > _v.MaxShift || scale < _v.MinScale || scale > _v.MaxScale || skew > _v.MaxSkew;

        private static Mat Gray(Mat frame, int width)
        {
            var gray = new Mat();
            if (frame.Channels() == 3) Cv2.CvtColor(frame, gray, ColorConversionCodes.BGR2GRAY);
            else if (frame.Channels() == 4) Cv2.CvtColor(frame, gray, ColorConversionCodes.BGRA2GRAY);
            else frame.CopyTo(gray);
            if (gray.Width != width)
            {
                var small = new Mat();
                Cv2.Resize(gray, small, new Size(width, Math.Max(1, (int)Math.Round(gray.Height * (double)width / gray.Width))), 0, 0, InterpolationFlags.Area);
                gray.Dispose();
                gray = small;
            }
            return gray;
        }

        private static double Displacement(double[] h, Point2d p)
        {
            double w = h[6] * p.X + h[7] * p.Y + h[8];
            if (Math.Abs(w) < 1e-9) return double.PositiveInfinity;
            double x = (h[0] * p.X + h[1] * p.Y + h[2]) / w, y = (h[3] * p.X + h[4] * p.Y + h[5]) / w;
            return Math.Sqrt((x - p.X) * (x - p.X) + (y - p.Y) * (y - p.Y));
        }

        public void Dispose()
        {
            Stop();
            _m.Dispose();
            _sift?.Dispose();
            _l2?.Dispose();
        }
    }
}
