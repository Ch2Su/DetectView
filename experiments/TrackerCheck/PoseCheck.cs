using OpenCvSharp;
using DetectView;

// pose: 화면 식별의 ORB 맞춤(ViewMatcher)으로 카메라 자세를 재 본다 — 천천히 하는 줌·팬(화면 전환 감지가 못 잡는 것)이 맞춤에 어떻게 보이는지.
// 사용: pose <영상> <기준 초> <초...>  → 기준 프레임과 각 프레임을 맞춘 인라이어·칸·이동·배율, 그리고 이웃한 두 프레임끼리
internal static class PoseCheck
{
    // ---------- selftest ⑬: 자세 지켜보기 (2026-11-21) — 합성 카메라: 큰 '세상' 그림을 잘라 화면을 만든다(팬 = 자르는 자리, 줌 = 자르는 크기) ----------
    // 화면 고정 글자 네 덩이(지사·방향·카메라 이름 꼴 — test6 처럼 화면 넷 귀퉁이)는 잘라 낸 뒤 덧씌워 카메라가 움직여도 제자리다. 차는 세상 안에서 움직인다

    private const int SW = 960, SH = 540;   // 합성 화면 크기

    private static Mat World()
    {
        var img = new Mat(1800, 3200, MatType.CV_8UC3, new Scalar(80, 95, 90));
        var rng = new Random(4242);
        for (int i = 0; i < 2600; i++)
        {
            var c = new Scalar(rng.Next(20, 235), rng.Next(20, 235), rng.Next(20, 235));
            int x = rng.Next(3200), y = rng.Next(1800), ww = rng.Next(8, 90), hh = rng.Next(8, 70);
            switch (i % 4)
            {
                case 0: Cv2.Circle(img, new Point(x, y), ww / 3, c, -1); break;
                case 1: Cv2.Line(img, new Point(x, y), new Point(x + ww, y + hh), c, rng.Next(1, 4)); break;
                default: Cv2.Rectangle(img, new Rect(x, y, ww, hh), c, -1); break;
            }
        }
        return img;
    }

    /// <summary>화면 고정 글자 자리(SW×SH 화면 좌표) — 맞는 짝이 여기 있으면 글자를 배운 것</summary>
    private static readonly Rect[] TextBoxes = [new(30, 20, 260, 50), new(20, 330, 210, 60), new(700, 300, 240, 70), new(220, 470, 560, 55)];

    /// <summary>카메라: 세상 가운데 (cx, cy), 화면에 담는 세상 폭 fw(줌아웃 = 넓게). 차는 t(ms)에 따라 움직인다. smoke 면 가운데에 옅은 회색 띠(연기 같은 내용 변화).
    /// hazy 면 장면을 흐리고 대비를 낮춘다 — SD 를 키운 흐린 실제 CCTV 처럼 장면 특징점이 약하고 덧씌운 글자만 또렷하게(글자가 맞춤을 끌고 가는 test6 꼴)</summary>
    private static Mat Render(Mat world, double cx, double cy, double fw, double t, bool overlay = true, bool smoke = false, bool hazy = false)
    {
        double fh = fw * SH / SW;
        using var w = world.Clone();
        for (int i = 0; i < 14; i++)   // 차 — 차로를 따라 움직인다
        {
            double speed = 0.08 + 0.02 * (i % 5);
            int x = (int)((i * 330 + t * speed) % 3200), y = 700 + (i % 6) * 70;
            Cv2.Rectangle(w, new Rect(x, y, 70, 34), new Scalar((i * 53) % 256, (i * 97) % 256, (i * 31) % 256), -1);
        }
        var crop = new Rect((int)Math.Round(cx - fw / 2), (int)Math.Round(cy - fh / 2), (int)Math.Round(fw), (int)Math.Round(fh));
        var img = new Mat();
        using (var roi = new Mat(w, crop)) Cv2.Resize(roi, img, new Size(SW, SH), 0, 0, InterpolationFlags.Area);
        if (hazy)
        {
            Cv2.GaussianBlur(img, img, new Size(0, 0), 3.0);
            img.ConvertTo(img, -1, 0.45, 70);
        }
        if (smoke)
        {
            using var band = img[new Rect(0, 120, SW, 280)];
            using var gray = new Mat(band.Size(), band.Type(), new Scalar(170, 170, 170));
            Cv2.AddWeighted(band, 0.35, gray, 0.65, 0, band);
        }
        if (overlay)
        {
            var texts = new (string, Point, double)[] { ("BRANCH 07", new(36, 60), 1.3), ("v DAEGU", new(26, 375), 1.3), ("GWANGJU 12.8 ^", new(706, 345), 1.1), ("LINE 90  BRIDGE 3", new(226, 510), 1.4) };
            foreach (var (s, p, sc) in texts)
            {
                Cv2.PutText(img, s, p, HersheyFonts.HersheySimplex, sc, Scalar.Black, 7, LineTypes.AntiAlias);
                Cv2.PutText(img, s, p, HersheyFonts.HersheySimplex, sc, Scalar.White, 2, LineTypes.AntiAlias);
            }
        }
        return img;
    }

    /// <summary>앱 루프처럼 돌린다: 처음 식별 → 지켜보기(서 있으면 1분마다 보조 기준), Moved 면 (잰 것이면 글자 배우고 그 뷰의 잘못 든 보조 기준을 버리고 그 뷰를 빼고) 다시 식별.
    /// 아는 뷰로 알아본 화면은 보조 기준으로. 일어난 일을 돌려준다</summary>
    private static List<(double Ms, string What, PoseStep Step, ViewIdentification Id)> Drive(ViewRegistry reg, Func<double, Mat> frameAt, double endMs, double stepMs = 100)
    {
        using var pose = new CameraPoseWatch(view: reg.Options);
        var log = new List<(double, string, PoseStep, ViewIdentification)>();
        int viewNow = 0;
        double nextRecent = double.PositiveInfinity;
        void Identify(Mat f, double ms, int notView)
        {
            var id = reg.Identify(f, notView);
            log.Add((ms, "identify", default, id));
            if (id.Outcome == ViewOutcome.Unknown) { pose.Stop(); viewNow = 0; return; }
            viewNow = id.ViewId;
            pose.Start(f, id.Outcome == ViewOutcome.Known ? id.Match.H : null, ms);
            if (id.Outcome == ViewOutcome.Known && id.Match.H != null) reg.AddRecent(viewNow, f, id.Match.H);
            nextRecent = ms + pose.Options.RecentEveryMs;
        }
        using (var f0 = frameAt(0)) Identify(f0, 0, 0);
        for (double t = stepMs; t <= endMs; t += stepMs)
        {
            if (!pose.Due(t)) continue;
            using var f = frameAt(t);
            var s = pose.Check(f, t, reg.Overlay);
            if (s.Outcome is PoseOutcome.Moving or PoseOutcome.Resumed or PoseOutcome.Moved) log.Add((t, s.Outcome.ToString(), s, default));
            if (s.Outcome == PoseOutcome.Still && viewNow > 0 && t >= nextRecent) { reg.AddRecent(viewNow, f, pose.KeyPose); nextRecent = t + pose.Options.RecentEveryMs; }
            if (s.Outcome != PoseOutcome.Moved) continue;
            int not = 0;
            if (s.Measured)
            {
                reg.LearnOverlay(s.Overlay);
                not = viewNow;
                int purged = reg.PurgeExtrasLike(viewNow, f);
                if (purged > 0) log.Add((t, $"purge {purged}", default, default));
            }
            Identify(f, t, not);
        }
        return log;
    }

    private static string Show(List<(double Ms, string What, PoseStep Step, ViewIdentification Id)> log) =>
        string.Join(" · ", log.Where(l => l.What != "Moving" || log.IndexOf(l) == 0 || log[log.IndexOf(l) - 1].What != "Moving")
                              .Select(l => l.What == "identify" ? $"{l.Ms / 1000:0.#}s {l.Id.Outcome} V{l.Id.ViewId}" : $"{l.Ms / 1000:0.#}s {l.What}{(l.What == "Moved" ? (l.Step.Measured ? $"(배율 {l.Step.Scale:0.00} · 이동 {l.Step.Shift * 100:0.0}%)" : "(못 잼)") : "")}"));

    public static void SelfTest(Action<bool, string> check)
    {
        using var world = World();
        const double CX = 1600, CY = 900, FW = 1400;
        static double Smooth(double a, double b, double t0, double t1, double t) => t <= t0 ? a : t >= t1 ? b : a + (b - a) * (t - t0) / (t1 - t0);

        // 1) 서 있는 카메라 + 지나가는 차 60초 — 움직임으로 보지 않는다
        using (var reg = new ViewRegistry())
        {
            var log = Drive(reg, t => Render(world, CX, CY, FW, t), 60000);
            check(!log.Any(l => l.What is "Moving" or "Moved"), $"서 있는 카메라 60초(차가 지나감): {Show(log)} — 움직임 판정 없음");
        }

        // 2) 빠른 줌아웃(0.5초에 0.74배) — 잰 움직임으로 Moved, 화면 고정 글자를 배우고 새 뷰. 그 뒤 같은 줌 화면을 다시 식별해도 V1 이 아니다
        using (var reg = new ViewRegistry())
        {
            var log = Drive(reg, t => Render(world, CX, CY, Smooth(FW, FW / 0.74, 20000, 20500, t), t), 40000);
            var moved = log.FirstOrDefault(l => l.What == "Moved");
            var ids = log.Where(l => l.What == "identify").ToList();
            var ov = moved.Step.Overlay ?? [];
            int inText = ov.Count(p => TextBoxes.Any(b => new Rect(b.X / 2 - 4, b.Y / 2 - 4, b.Width / 2 + 8, b.Height / 2 + 8).Contains(new Point((int)p.X, (int)p.Y))));
            using var later = Render(world, CX, CY, FW / 0.74, 39000);
            var again = reg.Identify(later);
            check(moved.What == "Moved" && moved.Step.Measured && Math.Abs(moved.Step.Scale - 0.74) < 0.05 && moved.Ms is >= 20000 and <= 23000
                  && ids.Count == 2 && ids[1].Id is { Outcome: ViewOutcome.New, ViewId: 2 } && ov.Count >= 20 && inText >= 0.8 * ov.Count
                  && again is { Outcome: ViewOutcome.Known, ViewId: 2 },
                  $"빠른 줌아웃 0.74배: {Show(log)} · 글자로 배운 짝 {ov.Count}(글자 자리 {inText}) · 줌 화면 다시 식별 {again.Outcome} V{again.ViewId} — V1 로 오인하지 않음");
        }

        // 3) 작은 팬(0.5초에 가로 2.5%) — 허용 범위(6%) 안이라 뷰가 바뀌지 않는다
        using (var reg = new ViewRegistry())
        {
            var log = Drive(reg, t => Render(world, Smooth(CX, CX + 0.025 * FW, 20000, 20500, t), CY, FW, t), 40000);
            check(!log.Any(l => l.What == "Moved") && reg.Count == 1, $"작은 팬 2.5%: {(log.Count > 1 ? Show(log) : "판정 없음")} — 다른 자세로 보지 않음 (뷰 {reg.Count}개)");
        }

        // 4) 느린 줌(초당 1%씩 20초 — 0.8배) — 이웃한 확인끼리는 작아도 쌓인 자세가 허용 범위(0.9배)를 넘으면 Moved
        using (var reg = new ViewRegistry())
        {
            var log = Drive(reg, t => Render(world, CX, CY, Smooth(FW, FW / 0.8, 10000, 30000, t), t), 45000);
            var moved = log.FirstOrDefault(l => l.What == "Moved");
            check(moved.What == "Moved" && moved.Step.Measured && moved.Ms is >= 15000 and <= 33000 && reg.Count >= 2,
                  $"느린 줌 20초에 0.8배: {Show(log)} — 쌓인 자세로 잡음 (뷰 {reg.Count}개)");
        }

        // 5) 크게 갔다가 바로 돌아오는 팬(8% → 0.3초 뒤 제자리) — 멈춘 뒤 앞 화면과 같으니 뷰가 바뀌지 않는다
        using (var reg = new ViewRegistry())
        {
            double Cx(double t) => t < 20000 ? CX : t < 20300 ? CX + 0.08 * FW : t < 20600 ? CX + 0.08 * FW * (20600 - t) / 300 : CX;
            var log = Drive(reg, t => Render(world, Cx(t), CY, FW, t), 40000);
            check(!log.Any(l => l.What == "Moved") && reg.Count == 1, $"갔다 오는 팬 8%: {(log.Count > 1 ? Show(log) : "판정 없음")} — 다른 자세로 보지 않음 (뷰 {reg.Count}개)");
        }

        // 6) 내용 변화(가운데에 연기 같은 옅은 띠 3초) — 카메라는 그대로라 뷰가 바뀌지 않는다
        using (var reg = new ViewRegistry())
        {
            var log = Drive(reg, t => Render(world, CX, CY, FW, t, smoke: t is >= 20000 and < 23000), 40000);
            check(!log.Any(l => l.What == "Moved") && reg.Count == 1, $"연기 같은 띠 3초: {(log.Count > 1 ? Show(log) : "판정 없음")} — 다른 자세로 보지 않음 (뷰 {reg.Count}개)");
        }

        // 7) 화면 고정 글자가 없는 카메라의 줌아웃 — 똑같이 잡는다(배울 글자는 없다)
        using (var reg = new ViewRegistry())
        {
            var log = Drive(reg, t => Render(world, CX, CY, Smooth(FW, FW / 0.74, 20000, 20500, t), t, overlay: false), 40000);
            var moved = log.FirstOrDefault(l => l.What == "Moved");
            check(moved.What == "Moved" && moved.Step.Measured && reg.Count == 2 && (moved.Step.Overlay?.Count ?? 0) < 10,
                  $"글자 없는 줌아웃: {Show(log)} · 배운 짝 {moved.Step.Overlay?.Count ?? 0}");
        }

        // 8) 저장본으로 새 세션(흐린 장면): 줌 전·후 두 뷰를 구역 파일에서 읽으면 기준 화면끼리 글자를 배워, 처음 보는 세 번째 자세(1.35배 줌인)를
        //    글자만으로 아는 뷰로 오인하거나 애매함(Unknown)에 묶이지 않고 새 뷰로 등록한다. 글자를 모르는 레지스트리는 같은 화면을 새 뷰로 등록하지 못한다
        //    — 글자 짝이 V1 과 맞아 '알아봄'(test6 실제: 인라이어 107~121 · 칸 5~7)이나 '애매함'(이 합성 화면: 반복되는 영문 글자라 비율 검사에서 줄어 15~29)
        using (var reg = new ViewRegistry())
        {
            using (var a = Render(world, CX, CY, FW, 0, hazy: true)) reg.Identify(a);
            using (var b = Render(world, CX, CY, FW / 0.74, 0, hazy: true)) reg.Identify(b, notView: 1);
            using var fresh = new ViewRegistry(reg.Snapshot(), reg.NextId);
            using var third = Render(world, CX, CY, FW * 0.74, 5000, hazy: true);
            var id = fresh.Identify(third);
            using var plain = new ViewRegistry();   // 글자를 모르는 레지스트리 — 비교용
            using (var a = Render(world, CX, CY, FW, 0, hazy: true)) plain.Identify(a);
            var naive = plain.Identify(third);
            check(reg.Count == 2 && id.Outcome == ViewOutcome.New && fresh.Overlay.Cells > 0 && naive.Outcome != ViewOutcome.New,
                  $"저장본 새 세션의 세 번째 자세(흐린 장면 · 1.35배 줌인): {id.Outcome} V{id.ViewId} (기준 화면끼리 배운 글자 칸 {fresh.Overlay.Cells}) — " +
                  $"글자를 모르면 {naive.Outcome} V{naive.ViewId}(인라이어 {naive.Match.Inliers} · 칸 {naive.Match.Cells} · 배율 {naive.Match.Scale:0.00})");
        }

        // 9) 흐린 장면의 빠른 줌아웃 — 장면 짝이 약해 ORB 전체 맞춤이 글자만으로 '제자리'가 되는 test6 꼴도 잡는다
        using (var reg = new ViewRegistry())
        {
            var log = Drive(reg, t => Render(world, CX, CY, Smooth(FW, FW / 0.74, 20000, 20500, t), t, hazy: true), 40000);
            var moved = log.FirstOrDefault(l => l.What == "Moved");
            check(moved.What == "Moved" && reg.Count == 2, $"흐린 장면의 빠른 줌아웃: {Show(log)} · 배운 짝 {moved.Step.Overlay?.Count ?? 0}");
        }

        // 10) 글자를 배우기 전에 다른 자세(줌아웃 화면)가 V1 의 보조 기준으로 잘못 들어가 있어도 — 글자만으로 그 자세를 V1 로 알아본 때 —
        //     카메라가 V1 에서 그 자세로 움직였다고 잰 순간 버려지고, 그 자세는 새 뷰 V2 로 알아봐진다
        using (var reg = new ViewRegistry())
        {
            using (var a = Render(world, CX, CY, FW, 0)) reg.Identify(a);
            using (var wrongly = Render(world, CX, CY, FW / 0.74, 3000)) reg.AddRecent(1, wrongly, ViewMatcher.Identity);
            int before = reg.ExtraCount(1);
            var log = Drive(reg, t => Render(world, CX, CY, Smooth(FW, FW / 0.74, 20000, 20500, t), t), 30000);
            var purge = log.FirstOrDefault(l => l.What.StartsWith("purge"));
            using var later = Render(world, CX, CY, FW / 0.74, 29000);
            var again = reg.Identify(later);
            check(purge.What != null && reg.Count == 2 && again is { Outcome: ViewOutcome.Known, ViewId: 2 },
                  $"잘못 들어간 보조 기준: 처음 V1 보조 기준 {before}장 · {Show(log)} — 줌 화면 다시 식별 {again.Outcome} V{again.ViewId}");
        }

        // 11) 글자 마스크: 가까운 짝은 한 줄로 묶어 사각형을 덮고, 먼 곳은 안 덮는다
        var mk = OverlayMask.Empty.With([new(100, 50), new(110, 52), new(124, 50), new(300, 200)]);
        check(mk.Covers(new Point2f(105, 50)) && mk.Covers(new Point2f(118, 55)) && mk.Covers(new Point2f(300, 200)) && !mk.Covers(new Point2f(200, 50)) && !mk.Covers(new Point2f(300, 230)),
              $"글자 마스크: 한 줄(100~124) 사이도 덮음 · 떨어진 점은 그 둘레만 (칸 {mk.Cells})");
    }

    /// <summary>
    /// posewatch: 앱 재생 루프의 화면 전환 감지 + 뷰 식별 + 자세 지켜보기를 그대로 흉내 내 영상을 처음부터(또는 [시작초]부터) 돌리고
    /// 전환·식별·움직임 판정을 시각과 함께 적는다 — 헛경보가 없는지, 천천히 한 줌·팬을 잡는지. 확인 한 번의 시간도 잰다
    /// </summary>
    public static int Replay(string[] args)
    {
        if (args.Length < 1) { Console.WriteLine("사용: posewatch <영상> [시작초] [끝초] [first=초] [@초 ...]  — first: 시작 전에 그 프레임으로 첫 뷰를 등록(앱이 0초에 열고 시크한 것처럼), @초: 다 돌린 뒤 그 프레임들을 식별"); return 2; }
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var nums = args.Skip(1).Where(a => !a.StartsWith('@') && !a.StartsWith("first=")).Select(a => double.Parse(a, inv)).ToArray();
        double from = nums.Length > 0 ? nums[0] : 0;
        double to = nums.Length > 1 ? nums[1] : double.PositiveInfinity;
        double? first = args.Where(a => a.StartsWith("first=")).Select(a => (double?)double.Parse(a[6..], inv)).FirstOrDefault();
        var probes = args.Where(a => a.StartsWith('@')).Select(a => double.Parse(a[1..], inv)).ToArray();
        using var cap = new VideoCapture(args[0], VideoCaptureAPIs.FFMPEG);
        if (!cap.IsOpened()) { Console.WriteLine($"열 수 없음: {args[0]}"); return 1; }
        double fps = cap.Get(VideoCaptureProperties.Fps);
        using var registry = new ViewRegistry();
        if (first is double f0)
        {
            cap.Set(VideoCaptureProperties.PosFrames, Math.Round(f0 * fps));
            using var ff = new Mat();
            if (cap.Read(ff) && !ff.Empty()) { var id0 = registry.Identify(ff); Console.WriteLine($"  {f0,8:0.00}초 (먼저) 뷰 V{id0.ViewId} {id0.Outcome}"); }
        }
        cap.Set(VideoCaptureProperties.PosFrames, Math.Round(from * fps));
        using var pose = new CameraPoseWatch();
        using var thumb = new SceneThumbnail();
        var scene = new SceneChangeDetector();
        bool needIdentify = true;
        double nextIdentifyMs = double.NegativeInfinity;
        int viewNow = 0, notView = 0;
        double nextRecentMs = double.PositiveInfinity;   // 앱 루프와 같게: 서 있는 화면을 1분마다 그 뷰의 보조 기준으로
        int checks = 0, moving = 0, moved = 0, resumed = 0, sceneChanges = 0;
        double checkMsSum = 0, checkMsMax = 0, identifyMsSum = 0; int identifies = 0;
        var sw = new System.Diagnostics.Stopwatch();
        using var frame = new Mat();
        double ms = 0;
        while (cap.Read(frame) && !frame.Empty())
        {
            ms = cap.Get(VideoCaptureProperties.PosMsec);
            if (ms / 1000 > to) break;
            var sc = scene.Update(thumb.Compute(frame), ms);
            PoseStep ps = default;
            if (!sc.Unsettled && !sc.ViewChanged && pose.Due(ms))
            {
                bool wasMoving = pose.Moving;
                sw.Restart();
                ps = pose.Check(frame, ms, registry.Overlay);
                if (ps.Outcome == PoseOutcome.Still && viewNow > 0 && ms >= nextRecentMs) { registry.AddRecent(viewNow, frame, pose.KeyPose); nextRecentMs = ms + pose.Options.RecentEveryMs; }
                double t = sw.Elapsed.TotalMilliseconds;
                checks++; checkMsSum += t; checkMsMax = Math.Max(checkMsMax, t);
                if (ps.Outcome == PoseOutcome.Moving && !wasMoving) { moving++; Console.WriteLine($"  {ms / 1000,8:0.00}초 움직임 시작 — {ps.Reason}"); }
                else if (ps.Outcome == PoseOutcome.Resumed) { resumed++; Console.WriteLine($"  {ms / 1000,8:0.00}초 멈춤 → 그 자리: {ps.Reason} ({t:0} ms)"); }
                else if (ps.Outcome == PoseOutcome.Moved) { moved++; Console.WriteLine($"  {ms / 1000,8:0.00}초 멈춤 → 다른 자세{(ps.Measured ? "" : "(재지 못함)")}: {ps.Reason} · 글자로 배운 짝 {ps.Overlay?.Count ?? 0} ({t:0} ms)"); }
            }
            if (sc.Unsettled || (pose.Moving && !sc.ViewChanged)) continue;   // 앱 루프와 같게: 이 프레임의 전환 알림은 놓치지 않는다
            bool changed = sc.ViewChanged || ps.Outcome == PoseOutcome.Moved;
            if (sc.ViewChanged) { pose.Stop(); sceneChanges++; Console.WriteLine($"  {ms / 1000,8:0.00}초 화면 전환 (밝기 차이 — 기준과의 거리 {sc.AnchorDistance:0.0})"); }
            if (ps.Outcome == PoseOutcome.Moved && ps.Measured)
            {
                registry.LearnOverlay(ps.Overlay);
                notView = viewNow;
                int purged = registry.PurgeExtrasLike(viewNow, frame);
                if (purged > 0) Console.WriteLine($"  {ms / 1000,8:0.00}초 V{viewNow} 의 보조 기준 {purged}장 버림 (움직인 뒤 자세와 같음)");
            }
            if (changed) { viewNow = 0; needIdentify = true; nextIdentifyMs = double.NegativeInfinity; }
            if (needIdentify && ms >= nextIdentifyMs)
            {
                sw.Restart();
                var vid = registry.Identify(frame, notView);
                identifyMsSum += sw.Elapsed.TotalMilliseconds; identifies++;
                notView = 0;
                if (vid.Outcome == ViewOutcome.Unknown)
                {
                    nextIdentifyMs = ms + 1000;
                    pose.Stop();
                    Console.WriteLine($"  {ms / 1000,8:0.00}초 뷰를 알 수 없음 (인라이어 {vid.Match.Inliers} · 특징점 {vid.Match.Features})");
                }
                else
                {
                    needIdentify = false;
                    viewNow = vid.ViewId;
                    pose.Start(frame, vid.Outcome == ViewOutcome.Known ? vid.Match.H : null, ms);
                    if (vid.Outcome == ViewOutcome.Known && vid.Match.H != null) registry.AddRecent(viewNow, frame, vid.Match.H);
                    nextRecentMs = ms + pose.Options.RecentEveryMs;
                    Console.WriteLine($"  {ms / 1000,8:0.00}초 뷰 V{vid.ViewId} {(vid.Outcome == ViewOutcome.New ? "새로 등록" : "알아봄")} (인라이어 {vid.Match.Inliers} · 칸 {vid.Match.Cells} · 이동 {vid.Match.Shift * 100:0.0}% · 배율 {vid.Match.Scale:0.000}) · 글자 칸 {registry.Overlay.Cells}");
                }
            }
        }
        Console.WriteLine($"=== {Path.GetFileName(args[0])} {from:0.#}~{ms / 1000:0.#}초: 화면 전환 {sceneChanges} · 자세 움직임 {moving} (다른 자세 {moved} · 그 자리 {resumed}) · 뷰 {registry.Count}개 · " +
                          $"확인 {checks}번 평균 {checkMsSum / Math.Max(1, checks):0.0} ms · 최대 {checkMsMax:0.0} ms · 식별 {identifies}번 평균 {identifyMsSum / Math.Max(1, identifies):0.0} ms · 글자 칸 {registry.Overlay.Cells}");
        foreach (double p in probes)
        {
            cap.Set(VideoCaptureProperties.PosFrames, Math.Round(p * fps));
            using var pf = new Mat();
            if (!cap.Read(pf) || pf.Empty()) continue;
            var id = registry.Identify(pf);
            Console.WriteLine($"  식별 {p,8:0.00}초 → {id.Outcome} V{id.ViewId} (인라이어 {id.Match.Inliers} · 칸 {id.Match.Cells} · 이동 {id.Match.Shift * 100:0.0}% · 배율 {id.Match.Scale:0.000}) · 뷰 {registry.Count}개");
        }
        return 0;
    }

    /// <summary>
    /// series: 영상 전체를 step 초마다 한 장씩, 바로 앞 장과 맞춰 인라이어·이동·배율의 흐름을 본다 — 카메라가 그대로일 때 맞춤이 얼마나 흔들리는지(헛경보 기준)와
    /// 움직였을 때 어떻게 보이는지. 인라이어가 앞 다섯 번의 가운데값보다 크게 떨어진 곳·배율/이동이 튄 곳을 적는다
    /// </summary>
    public static int Series(string[] args)
    {
        if (args.Length < 1) { Console.WriteLine("사용: poseseries <영상> [간격초=1] [특징점=800]"); return 2; }
        double step = args.Length > 1 ? double.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture) : 1.0;
        int nf = args.Length > 2 ? int.Parse(args[2]) : 800;
        using var cap = new VideoCapture(args[0], VideoCaptureAPIs.FFMPEG);
        if (!cap.IsOpened()) { Console.WriteLine($"열 수 없음: {args[0]}"); return 1; }
        double fps = cap.Get(VideoCaptureProperties.Fps);
        int frames = (int)cap.Get(VideoCaptureProperties.FrameCount);
        using var orb = ORB.Create(nFeatures: nf, scaleFactor: 1.2f, nLevels: 8, edgeThreshold: 15, fastThreshold: 12);
        using var m = new ViewMatcher();
        ViewSignature? prev = null;
        var inl = new List<int>();
        var lines = new List<string>();
        int stride = Math.Max(1, (int)Math.Round(step * fps));
        double worstRatio = 9; double worstAt = 0; double maxDev = 0; double maxDevAt = 0; int n = 0;
        var sw = new System.Diagnostics.Stopwatch();
        double featMs = 0, cmpMs = 0;
        using var f = new Mat();
        for (int i = 0; i < frames; i++)
        {
            if (!cap.Grab()) break;
            if (i % stride != 0) continue;
            if (!cap.Retrieve(f) || f.Empty()) break;
            double t = i / fps;
            sw.Restart();
            using var g = ViewMatcher.PrepareGray(f);
            using var desc = new Mat();
            orb.DetectAndCompute(g, null, out KeyPoint[] kps, desc);
            var sig = new ViewSignature(g.Width, g.Height, kps.Select(k => k.Pt).ToArray(), desc.Clone());
            featMs += sw.Elapsed.TotalMilliseconds;
            if (prev != null)
            {
                sw.Restart();
                var r = m.Compare(prev, sig);
                cmpMs += sw.Elapsed.TotalMilliseconds;
                n++;
                double med = inl.Count == 0 ? r.Inliers : inl.TakeLast(5).OrderBy(x => x).ElementAt(Math.Min(4, inl.Count) / 2);
                double ratio = med > 0 ? r.Inliers / med : 1;
                double dev = Math.Max(Math.Abs(r.Scale - 1), r.Shift);
                if (ratio < worstRatio) { worstRatio = ratio; worstAt = t; }
                if (dev > maxDev && r.Inliers >= 30) { maxDev = dev; maxDevAt = t; }
                if (ratio < 0.5 || (r.Inliers >= 30 && dev > 0.02) || r.Inliers < 30)
                    lines.Add($"  {t,8:0.0}초: 인라이어 {r.Inliers,3} (앞 가운데 {med,4:0}, {ratio * 100,3:0}%) 칸 {r.Cells,2} 이동 {r.Shift * 100,4:0.0}% 배율 {r.Scale:0.000}");
                inl.Add(r.Inliers);
            }
            prev?.Dispose();
            prev = sig;
        }
        prev?.Dispose();
        var sorted = inl.OrderBy(x => x).ToArray();
        Console.WriteLine($"=== {Path.GetFileName(args[0])}: {n}번 ({step:0.##}초 간격, 특징점 {nf}) · 인라이어 최소 {sorted.FirstOrDefault()} · 5% {(sorted.Length > 0 ? sorted[sorted.Length / 20] : 0)} · 가운데 {(sorted.Length > 0 ? sorted[sorted.Length / 2] : 0)}" +
                          $" · 앞 다섯 번 대비 최저 {worstRatio * 100:0}% ({worstAt:0.0}초) · 이동/배율 최대 {maxDev * 100:0.0}% ({maxDevAt:0.0}초) · 한 번에 특징 {featMs / Math.Max(1, n + 1):0.0} ms + 맞춤 {cmpMs / Math.Max(1, n):0.0} ms");
        foreach (var l in lines.Take(40)) Console.WriteLine(l);
        if (lines.Count > 40) Console.WriteLine($"  … 외 {lines.Count - 40}곳");
        return 0;
    }

    public static int Probe(string[] args)
    {
        if (args.Length < 3) { Console.WriteLine("사용: pose <영상> <기준 초> <초...>"); return 2; }
        using var cap = new VideoCapture(args[0], VideoCaptureAPIs.FFMPEG);
        if (!cap.IsOpened()) { Console.WriteLine($"열 수 없음: {args[0]}"); return 1; }
        double fps = cap.Get(VideoCaptureProperties.Fps);
        var secs = args.Skip(1).Select(s => double.Parse(s, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        using var m = new ViewMatcher();
        var sigs = new List<ViewSignature>();
        foreach (double s in secs)
        {
            cap.Set(VideoCaptureProperties.PosFrames, Math.Round(s * fps));
            using var f = new Mat();
            if (!cap.Read(f) || f.Empty()) { Console.WriteLine($"읽지 못함: {s}초"); return 1; }
            using var g = ViewMatcher.PrepareGray(f);
            sigs.Add(m.Compute(g));
        }
        static string Fmt(ViewMatch r) => $"특징 {r.Features,3} 짝 {r.Good,3} 인라이어 {r.Inliers,3} 칸 {r.Cells,2} 이동 {r.Shift * 100,5:0.0}% 배율 {r.Scale:0.000} 원근 {r.Skew:0.00}";
        for (int i = 1; i < secs.Length; i++)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var a = m.Compare(sigs[0], sigs[i]);
            double ms = sw.Elapsed.TotalMilliseconds;
            var b = m.Compare(sigs[i - 1], sigs[i]);
            Console.WriteLine($"{secs[i],8:0.0}초  기준({secs[0]:0.0}) {Fmt(a)} ({ms:0.0} ms) | 직전({secs[i - 1]:0.0}) {Fmt(b)}");
        }
        foreach (var s in sigs) s.Dispose();
        return 0;
    }
}
