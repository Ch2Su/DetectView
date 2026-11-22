using OpenCvSharp;
using DetectView;

// pose: 화면 식별의 ORB 맞춤(ViewMatcher)으로 카메라 자세를 재 본다 — 천천히 하는 줌·팬(화면 전환 감지가 못 잡는 것)이 맞춤에 어떻게 보이는지.
// 사용: pose <영상> <기준 초> <초...>  → 기준 프레임과 각 프레임을 맞춘 인라이어·칸·이동·배율, 그리고 이웃한 두 프레임끼리
internal static class PoseCheck
{
    // ---------- selftest ⑬: 자세 지켜보기 (2026-11-21) — 합성 카메라: 큰 '세상' 그림을 잘라 화면을 만든다(팬 = 자르는 자리, 줌 = 자르는 크기) ----------
    // 화면 고정 글자 네 덩이(지사·방향·카메라 이름 꼴 — test6 처럼 화면 넷 귀퉁이)는 잘라 낸 뒤 덧씌워 카메라가 움직여도 제자리다. 차는 세상 안에서 움직인다.
    // 뷰 처리는 앱 재생 루프와 같은 ViewSession 으로 돌린다(2026-11-22 — 예전엔 루프의 순서를 여기 베껴 써서 앱과 달라질 수 있었다)

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

    /// <summary>무늬가 있는 사각형 (seed 로 늘 같게) — 로고(화면에 덧씌움)·큰 차(세상 안에서 움직임)</summary>
    private static Mat Textured(int w, int h, int seed, Scalar bg)
    {
        var img = new Mat(h, w, MatType.CV_8UC3, bg);
        var rng = new Random(seed);
        for (int i = 0; i < w * h / 180; i++)
        {
            var c = new Scalar(rng.Next(0, 256), rng.Next(0, 256), rng.Next(0, 256));
            int x = rng.Next(w), y = rng.Next(h), ww = rng.Next(3, 18), hh = rng.Next(3, 14);
            if (i % 3 == 0) Cv2.Circle(img, new Point(x, y), ww / 2, c, -1);
            else Cv2.Rectangle(img, new Rect(x, y, ww, hh), c, -1);
        }
        return img;
    }

    private static readonly Mat Logo = Textured(150, 80, 77, new Scalar(30, 30, 30));
    private static readonly Mat Truck = Textured(520, 260, 99, new Scalar(60, 140, 200));

    /// <summary>화면 고정 글자 자리(SW×SH 화면 좌표) — 맞는 짝이 여기 있으면 글자를 배운 것</summary>
    private static readonly Rect[] TextBoxes = [new(30, 20, 260, 50), new(20, 330, 210, 60), new(700, 300, 240, 70), new(220, 470, 560, 55)];

    /// <summary>
    /// 카메라: 세상 가운데 (cx, cy), 화면에 담는 세상 폭 fw(줌아웃 = 넓게). 차는 t(ms)에 따라 움직인다. smoke 면 가운데에 옅은 회색 띠(연기 같은 내용 변화).
    /// hazy 면 장면을 흐리고 대비를 낮춘다 — SD 를 키운 흐린 실제 CCTV 처럼 장면 특징점이 약하고 덧씌운 글자만 또렷하게(글자가 맞춤을 끌고 가는 test6 꼴).
    /// logo 면 무늬 있는 로고 둘을 글자와 함께 덧씌운다 — 글자만으로도 인라이어가 많이 맞는 test6 꼴(합성 글자는 같은 글자가 되풀이돼 비율 검사에서 많이 빠진다).
    /// heavySmoke 면 화면 전체를 짙은 연기로 덮는다(결이 천천히 흐른다 — 카메라는 그대로, 장면 맞춤만 무너진다). truckX 면 세상 x 에 큰 무늬 차를 둔다
    /// </summary>
    private static Mat Render(Mat world, double cx, double cy, double fw, double t, bool overlay = true, bool smoke = false, bool hazy = false,
                              bool logo = false, bool heavySmoke = false, Func<double, double>? truckX = null)
    {
        double fh = fw * SH / SW;
        using var w = world.Clone();
        for (int i = 0; i < 14; i++)   // 차 — 차로를 따라 움직인다
        {
            double speed = 0.08 + 0.02 * (i % 5);
            int x = (int)((i * 330 + t * speed) % 3200), y = 700 + (i % 6) * 70;
            Cv2.Rectangle(w, new Rect(x, y, 70, 34), new Scalar((i * 53) % 256, (i * 97) % 256, (i * 31) % 256), -1);
        }
        if (truckX != null)
        {
            var r = new Rect((int)Math.Round(truckX(t)), 760, Truck.Width, Truck.Height) & new Rect(0, 0, w.Width, w.Height);
            if (r.Width > 0 && r.Height > 0)
                using (var dst = new Mat(w, r))
                using (var src = new Mat(Truck, new Rect(0, 0, r.Width, r.Height)))
                    src.CopyTo(dst);
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
        if (heavySmoke)
        {
            // 짙기가 천천히 오르내리고 결(밝은 줄무늬)이 흘러간다 — 이웃한 확인끼리도 장면 특징점이 크게 바뀐다
            using var fog = new Mat(img.Size(), img.Type(), new Scalar(178, 178, 178));
            double a = 0.86 + 0.06 * Math.Sin(t / 1300.0);
            Cv2.AddWeighted(img, 1 - a, fog, a, 0, img);
            for (int k = 0; k < 6; k++)
            {
                int y = (int)((k * 97 + t * 0.04) % SH);
                using var streak = img[new Rect(0, y, SW, Math.Min(18, SH - y))];
                using var white = new Mat(streak.Size(), streak.Type(), new Scalar(215, 215, 215));
                Cv2.AddWeighted(streak, 0.5, white, 0.5, 0, streak);
            }
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
        if (logo)
        {
            foreach (var at in new[] { new Point(790, 18), new Point(18, 150) })
                using (var dst = new Mat(img, new Rect(at.X, at.Y, Logo.Width, Logo.Height)))
                    Logo.CopyTo(dst);
        }
        return img;
    }

    /// <summary>앱 재생 루프처럼 돌린다 — 뷰 처리는 앱과 같은 ViewSession. sceneAt(t) 는 그 시각의 화면 전환 감지 결과(주지 않으면 늘 '안정'),
    /// seeks 의 시각엔 시크한 것처럼 Reset, startMs 부터 시작. 일어난 일을 돌려준다(identify · Moving · Resumed · Moved · purge N · note …).
    /// watch 는 할 일이 있던 시각마다 불린다(그 시각의 결과와 세션)</summary>
    private static List<(double Ms, string What, PoseStep Step, ViewIdentification Id)> Drive(ViewRegistry reg, Func<double, Mat> frameAt, double endMs, double stepMs = 100,
        double startMs = 0, Func<double, SceneStep>? sceneAt = null, double[]? seeks = null, Action<double, ViewStep, ViewSession>? watch = null)
    {
        using var session = new ViewSession(reg);
        var log = new List<(double, string, PoseStep, ViewIdentification)>();
        int nextSeek = 0;
        for (int k = 0; ; k++)
        {
            double t = startMs + k * stepMs;   // 정수 배로 — 더해 가면 오차가 쌓여 확인 차례(1초)를 한 걸음씩 놓쳤다
            if (t > endMs) break;
            bool seek = seeks != null && nextSeek < seeks.Length && t >= seeks[nextSeek];
            if (seek) { session.Reset(); nextSeek++; }
            var sc = sceneAt?.Invoke(t) ?? default;
            if (!seek && !sc.Unsettled && !sc.ViewChanged && !session.Due(t)) continue;   // 할 일이 없으면 그리지도 않는다 (앱은 프레임마다 부른다)
            using var f = frameAt(t);
            var vs = session.Update(f, t, sc);
            if (vs.Pose.Outcome is PoseOutcome.Moving or PoseOutcome.Resumed or PoseOutcome.Moved) log.Add((t, vs.Pose.Outcome.ToString(), vs.Pose, default));
            if (vs.Purged > 0) log.Add((t, $"purge {vs.Purged}", default, default));
            if (vs.Identified is { } id) log.Add((t, "identify", default, id));
            if (vs.Note != null) log.Add((t, $"note {vs.Note}", default, default));
            watch?.Invoke(t, vs, session);
        }
        return log;
    }

    private static string Show(List<(double Ms, string What, PoseStep Step, ViewIdentification Id)> log)
    {
        var shown = log.Where(l => !l.What.StartsWith("note")).ToList();
        return string.Join(" · ", shown.Where((l, i) => l.What != "Moving" || i == 0 || shown[i - 1].What != "Moving")
                              .Select(l => l.What == "identify" ? $"{l.Ms / 1000:0.#}s {l.Id.Outcome} V{l.Id.ViewId}" : $"{l.Ms / 1000:0.#}s {l.What}{(l.What == "Moved" ? (l.Step.Measured ? $"(배율 {l.Step.Scale:0.00} · 이동 {l.Step.Shift * 100:0.0}%)" : "(못 잼)") : "")}"));
    }

    private static string Notes(List<(double Ms, string What, PoseStep Step, ViewIdentification Id)> log) =>
        string.Join(" / ", log.Where(l => l.What.StartsWith("note")).Select(l => $"{l.Ms / 1000:0.#}s {l.What[5..]}"));

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

        // 11) 글자 마스크: 가까운 짝은 한 줄로 묶어 사각형을 덮고, 먼 곳은 안 덮는다. 새로 덮는 칸이 없으면 같은 객체(식별이 서명을 다시 만들지 않게)
        var mk = OverlayMask.Empty.With([new(100, 50), new(110, 52), new(124, 50), new(300, 200)]);
        check(mk.Covers(new Point2f(105, 50)) && mk.Covers(new Point2f(118, 55)) && mk.Covers(new Point2f(300, 200)) && !mk.Covers(new Point2f(200, 50)) && !mk.Covers(new Point2f(300, 230))
              && ReferenceEquals(mk.With([new(105, 51)]), mk),
              $"글자 마스크: 한 줄(100~124) 사이도 덮음 · 떨어진 점은 그 둘레만 (칸 {mk.Cells}) · 이미 덮인 자리는 같은 마스크");

        // ----- 2026-11-22 검토에서 찾은 실패 — 고친 뒤에도 그대로인지 -----

        // 12) 시크한 직후의 줌: 0~19.8초를 지켜봐 평소 수준을 안 뒤 19.8초로 시크하면 첫 확인이 줌 뒤라 글자·로고만 맞는다 — 앞에서 안 수준으로 무너짐을 재 곧바로 잡는다
        using (var reg = new ViewRegistry())
        {
            var log = Drive(reg, t => Render(world, CX, CY, Smooth(FW, FW / 0.74, 20000, 20500, t), t, hazy: true, logo: true), 32000, seeks: [19800]);
            var moved = log.FirstOrDefault(l => l.What == "Moved");
            var ids = log.Where(l => l.What == "identify").ToList();
            var start = log.FirstOrDefault(l => l.What == "Moving" && l.Ms >= 19800);
            check(moved.What == "Moved" && moved.Step.Measured && moved.Ms is >= 20000 and <= 23000 && ids.Count >= 3 && ids[^1].Id is { Outcome: ViewOutcome.New, ViewId: 2 },
                  $"시크한 직후의 줌(앞에서 안 평소 수준으로): {Show(log)} · 움직임으로 본 까닭: {start.Step.Reason}");
        }

        // 13) 평소 수준을 모른 채 시작하자마자 줌: 첫 확인부터 글자·로고만 맞아 그것이 평소가 되어도, 기준을 새로 잡은 뒤 인라이어가 몇 배로 늘면 처음 화면과 SIFT 로 견줘 잡는다
        using (var reg = new ViewRegistry())
        {
            var log = Drive(reg, t => Render(world, CX, CY, Smooth(FW, FW / 0.74, 20000, 20500, t), t, hazy: true, logo: true), 34000, startMs: 19800);
            var moved = log.FirstOrDefault(l => l.What == "Moved");
            var ids = log.Where(l => l.What == "identify").ToList();
            check(moved.What == "Moved" && moved.Step.Measured && moved.Ms <= 33000 && Math.Abs(moved.Step.Scale - 0.74) < 0.05 && ids.Count >= 2 && ids[^1].Id is { Outcome: ViewOutcome.New, ViewId: 2 },
                  $"평소 수준을 모른 채 시작하자마자 줌: {Show(log)} · 판정: {moved.Step.Reason} · {Notes(log)}");
        }

        // 14) 짙은 연기로 맞춤이 무너진 사이 큰 차가 화면의 1/3 을 지나감: 카메라는 그대로 — 차의 움직임은 몇 칸에 몰려 카메라 움직임으로 재지 않고(잰 Moved 없음),
        //     서 있는 장면을 글자로 배우지 않고, 화면 전환으로 처리하지 않는다
        using (var reg = new ViewRegistry())
        {
            int changed = 0;
            var log = Drive(reg, t => Render(world, CX, CY, FW, t, heavySmoke: t is >= 20000 and < 24000, truckX: u => Smooth(1200, 1700, 20000, 24000, u)), 40000,
                            watch: (t, vs, s) => { if (vs.Changed) changed++; });
            check(log.Any(l => l.What == "Moving") && !log.Any(l => l.What == "Moved" && l.Step.Measured) && changed == 0 && reg.Count == 1 && reg.Overlay.Cells == 0,
                  $"짙은 연기 속에 큰 차가 지나감: {Show(log)} · 화면 전환 {changed} · 글자 칸 {reg.Overlay.Cells} — 카메라 움직임으로 보지 않음 · {Notes(log)}");
        }

        // 15) 짙은 연기 15초: 맞춤이 무너져도 카메라가 움직인 증거가 없으면 추적을 멈추지 않고(Moving 아님) 화면 전환으로도 보지 않는다
        //     (재지 못한 움직임으로 끝나도 다시 알아봐 같은 뷰면 조용히 잇는다 — 그래서 Moved 가 아니라 화면 전환 수를 본다)
        using (var reg = new ViewRegistry())
        {
            int movingSteps = 0, changed = 0;
            var log = Drive(reg, t => Render(world, CX, CY, FW, t, heavySmoke: t is >= 20000 and < 35000), 45000,
                            watch: (t, vs, s) => { if (s.Moving) movingSteps++; if (vs.Changed) changed++; });
            check(log.Any(l => l.What == "Moving") && changed == 0 && movingSteps == 0 && reg.Count == 1,
                  $"짙은 연기 15초: {Show(log)} · 추적을 멈춘 확인 {movingSteps} · 화면 전환 {changed} · {Notes(log)}");
        }

        // 16) 아주 느린 팬(1초에 가로의 0.08%, 글자 없음): 확인마다는 '서 있음'이어도 쌓인 자세가 허용 범위(이동 6%)를 넘으면 잡는다 — 예전엔 이동 13% 까지도 '서 있음'
        using (var reg = new ViewRegistry())
        {
            var log = Drive(reg, t => Render(world, CX + Math.Max(0, t - 20000) / 1000 * 0.0008 * FW, CY, FW, t, overlay: false), 130000);
            var moved = log.FirstOrDefault(l => l.What == "Moved");
            check(moved.What == "Moved" && moved.Step.Measured && moved.Ms is >= 85000 and <= 125000 && reg.Count == 2, $"아주 느린 팬 0.08%/초: {Show(log)}");
        }

        // 17) 작은 팬을 갔다 온 뒤 범위 안의 줌(흐린 장면 + 글자·로고 — ORB 맞춤에 글자 짝이 섞이는 test6 꼴): +3% 팬 → 제자리 → 1.08배 줌인 —
        //     ORB 로 이어 붙인 자세 어긋남이 쌓여도 판정은 처음 화면과 SIFT 로 다시 재므로 범위 안의 줌을 범위 밖으로 재지 않는다
        using (var reg = new ViewRegistry())
        {
            double Cx(double t) => Smooth(CX, CX + 0.03 * FW, 20000, 20500, t) - Smooth(0, 0.03 * FW, 40000, 40500, t);
            var log = Drive(reg, t => Render(world, Cx(t), CY, Smooth(FW, FW / 1.08, 60000, 60600, t), t, hazy: true, logo: true), 80000);
            check(!log.Any(l => l.What == "Moved") && reg.Count == 1, $"작은 팬을 갔다 온 뒤 범위 안의 줌 1.08배: {(log.Count > 1 ? Show(log) : "판정 없음")} (뷰 {reg.Count}개) · {Notes(log)}");
        }

        // 18) 글자·로고만으로 V1 로 알아본 자세에서 V1 의 자세로 줌: V1 은 줌아웃한 자세로 먼저 등록돼 있고, 줌인한 자세에서 재생하면 글자만으로 V1 로 알아본다.
        //     거기서 줌아웃(= V1 의 자세)하면 — 예전엔 V1 을 빼고 V1 자신의 자세를 새 뷰 V2 로 등록했다. 이제 V1 의 기준 화면이 받아들여 V1 그대로
        using (var reg = new ViewRegistry())
        {
            using (var a = Render(world, CX, CY, FW / 0.74, 0, hazy: true, logo: true)) reg.Identify(a);
            var log = Drive(reg, t => Render(world, CX, CY, Smooth(FW, FW / 0.74, 20000, 20500, t), t, hazy: true, logo: true), 30000);
            var ids = log.Where(l => l.What == "identify").ToList();
            check(reg.Count == 1 && ids.Count >= 2 && ids[0].Id is { Outcome: ViewOutcome.Known, ViewId: 1 } && ids[^1].Id is { Outcome: ViewOutcome.Known, ViewId: 1 } && log.Any(l => l.What == "Moved"),
                  $"글자만으로 V1 로 알아본 자세에서 V1 의 자세로 줌: {Show(log)} (뷰 {reg.Count}개) · {Notes(log)}");
        }

        // 19) 두 번에 걸친 팬: +4%(범위 안) → 1분 뒤 보조 기준 → +3% 더(7%, 범위 밖) — 잰 움직임 뒤 정리가 중간 자세(4%)의 멀쩡한 보조 기준(60초)을 버리지 않는다
        //     (식별과 같게 보조 기준의 자세를 이어 붙여 판정 — 날것의 '보조 기준 ↔ 움직인 뒤' 차이 3% 로 보면 버렸다)
        using (var reg = new ViewRegistry())
        {
            double Cx(double t) => CX + Smooth(0, 0.04 * FW, 20000, 20500, t) + Smooth(0, 0.03 * FW, 100000, 100500, t);
            var log = Drive(reg, t => Render(world, Cx(t), CY, FW, t), 110000);
            var times = reg.ExtraTimes(1);
            check(log.Any(l => l.What == "Moved") && reg.Count == 2 && times.Any(x => Math.Abs(x - 60000) < 1000),
                  $"두 번에 걸친 팬 4% + 3%: {Show(log)} · V1 보조 기준 시각 [{string.Join(", ", times.Select(x => $"{x / 1000:0}s"))}] — 60초(4% 자세)의 것이 남음");
        }

        // 23) 범위 밖으로 줌인(1.35배 — test6 줌의 거꾸로): 줌인은 앞 화면의 가운데만 뒤 화면 전체로 옮겨져 움직임이 앞 화면 쪽으론 몰려 보인다 —
        //     퍼짐을 앞뒤 중 큰 쪽으로 재므로 카메라 움직임으로 재고 새 뷰 V2 (앞 화면 쪽으로만 재던 판은 걸러 놓쳤다 — 검토 실측 test6 1417.5 → 1416초 8~11칸)
        using (var reg = new ViewRegistry())
        {
            var log = Drive(reg, t => Render(world, CX, CY, Smooth(FW, FW * 0.74, 20000, 20500, t), t), 32000);
            var moved = log.FirstOrDefault(l => l.What == "Moved");
            var ids = log.Where(l => l.What == "identify").ToList();
            check(moved.What == "Moved" && moved.Step.Measured && Math.Abs(moved.Step.Scale - 1 / 0.74) < 0.08 && ids.Count >= 2 && ids[^1].Id is { Outcome: ViewOutcome.New, ViewId: 2 },
                  $"범위 밖으로 줌인 1.35배: {Show(log)}");
        }

        // 24) 흔들리는 기둥 카메라(2초마다 ±1.5% 좌우로): 허용 범위 안의 움직임은 그 자리에서 다시 잴 뿐 추적을 멈추지 않고 다른 자세로 보지 않는다
        using (var reg = new ViewRegistry())
        {
            int movingSteps = 0;
            var log = Drive(reg, t => Render(world, CX + 0.015 * FW * Math.Sin(t / 2000.0 * Math.PI), CY, FW, t), 40000,
                            watch: (t, vs, s) => { if (s.Moving) movingSteps++; });
            check(!log.Any(l => l.What == "Moved") && reg.Count == 1 && movingSteps <= 2,
                  $"흔들리는 카메라 ±1.5%: {(log.Count > 1 ? Show(log) : "판정 없음")} · 추적을 멈춘 확인 {movingSteps} (뷰 {reg.Count}개)");
        }

        // 20) 보조 기준은 같은 때(30초 안)의 화면을 바꾼다: 1분마다 16장을 넣은 뒤 한 구간(27초 안)에서 16번 시크해 넣어도 1분마다의 보조 기준이 밀려나지 않는다
        using (var reg = new ViewRegistry())
        {
            using var f = Render(world, CX, CY, FW, 0);
            reg.Identify(f);
            for (int i = 1; i <= 16; i++) reg.AddRecent(1, f, ViewMatcher.Identity, i * 60000, 1);
            for (int i = 0; i < 16; i++) reg.AddRecent(1, f, ViewMatcher.Identity, 1385000 + i * 1800, 2 + i);
            var times = reg.ExtraTimes(1);
            int minute = times.Count(x => x <= 960000), seekZone = times.Count(x => x >= 1385000);
            check(times.Length == 16 && minute >= 14 && seekZone == 1, $"보조 기준 시각: 1분마다 16장 + 한 구간 시크 16번 → {times.Length}장 중 1분마다의 것 {minute}장 · 시크 구간 {seekZone}장");
        }

        // 21) 빠른 전환(컷)으로 다른 자세 — 글자·로고는 그대로: 전환 앞뒤의 제자리 짝을 글자로 배우고 떠난 뷰를 빼서, 글자만으로 앞 뷰로 알아보지 않고 새 뷰 V2
        using (var reg = new ViewRegistry())
        {
            static SceneStep Cut(double t) => t == 20000 ? new SceneStep(false, true, true, 40, 3, 19900) : default;
            var log = Drive(reg, t => Render(world, t < 20000 ? CX : CX + 0.5 * FW, CY, FW, t, hazy: true, logo: true), 30000, sceneAt: Cut);
            var ids = log.Where(l => l.What == "identify").ToList();
            check(ids.Count >= 2 && ids[1].Id is { Outcome: ViewOutcome.New, ViewId: 2 } && reg.Overlay.Cells > 0,
                  $"빠른 전환(컷) — 글자·로고 그대로: {Show(log)} · 배운 글자 칸 {reg.Overlay.Cells} · {Notes(log)}");
        }

        // 22) 글자 마스크가 하는 일(마스크를 끄면 실패해야 하는 검사): 줌으로 글자·로고를 배운 뒤 처음 보는 세 번째 자세(40% 팬)는 새 뷰 V3 — 글자만으로 V1·V2 로 오인하지 않는다
        using (var reg = new ViewRegistry())
        {
            var log = Drive(reg, t => Render(world, CX, CY, Smooth(FW, FW / 0.74, 20000, 20500, t), t, hazy: true, logo: true), 30000);
            using var third = Render(world, CX + 0.4 * FW, CY, FW, 31000, hazy: true, logo: true);
            var id = reg.Identify(third, atMs: 31000);
            check(reg.Count == 3 && reg.Overlay.Cells > 0 && id is { Outcome: ViewOutcome.New, ViewId: 3 },
                  $"글자를 배운 뒤 처음 보는 자세(40% 팬): {Show(log)} → {id.Outcome} V{id.ViewId} (인라이어 {id.Match.Inliers} · 글자 칸 {reg.Overlay.Cells})");
        }

        // ----- 2026-11-22 3차 고침 뒤 — 2차 검증에서 찾은 실패 -----

        // 25) 무늬가 한 줄에만 있는 장면의 느린 팬(초당 0.5%): 서 있어도 맞춤이 몇 칸에만 퍼지는 장면(역광 사고 영상 충훈 꼴)에서 퍼짐 기준을 그 장면에 맞춰 잡는다 —
        //     고정 12칸이면 팬 맞춤이 늘 '화면 안 물체'라 끝까지 못 잡았다
        using (var band = BandWorld())
        using (var reg = new ViewRegistry())
        {
            var log = Drive(reg, t => Render(band, CX + Math.Max(0, t - 20000) / 1000 * 0.005 * FW, CY, FW, t), 50000);
            var moved = log.FirstOrDefault(l => l.What == "Moved");
            var ids = log.Where(l => l.What == "identify").ToList();
            check(moved.What == "Moved" && moved.Step.Measured && moved.Ms <= 40000 && ids.Count >= 2 && ids[1].Id.Outcome == ViewOutcome.New,
                  $"무늬가 한 줄인 장면의 느린 팬 0.5%/초: {Show(log)} · {Notes(log)}");
        }

        // 26) 짙은 연기에 가려진 사이 카메라가 범위 밖으로 팬(8%): 가린 동안은 움직인 증거가 없어 그 자리로 이어도, 장면이 다시 보이면 처음 화면과 견줘 잡는다 — 새 뷰 V2
        using (var reg = new ViewRegistry())
        {
            var log = Drive(reg, t => Render(world, Smooth(CX, CX + 0.08 * FW, 25000, 26000, t), CY, FW, t, heavySmoke: t is >= 20000 and < 35000), 50000);
            var moved = log.FirstOrDefault(l => l.What == "Moved" && l.Step.Measured);
            var ids = log.Where(l => l.What == "identify").ToList();
            check(moved.What == "Moved" && moved.Ms is >= 35000 and <= 42000 && ids.Count >= 2 && ids[^1].Id is { Outcome: ViewOutcome.New, ViewId: 2 },
                  $"연기에 가려진 사이 팬 8%: {Show(log)} · {Notes(log)}");
        }

        // 27) 다른 뷰를 들렀다 시크해 돌아온 직후의 줌: 뷰마다 확인한 평소 수준을 기억해 돌아온 뷰의 첫 확인부터 무너짐을 잰다 —
        //     예전엔 다른 뷰로 가면 수준을 버려 첫 확인이 글자·로고만 맞은 '제자리'가 됐다(평소 수준을 처음 잡은 뒤의 확인으로 늦게 잡음)
        using (var reg = new ViewRegistry())
        {
            static SceneStep Cut10(double t) => t == 10000 ? new SceneStep(false, true, true, 40, 3, 9900) : default;
            var log = Drive(reg, t => Render(world, t is >= 10000 and < 20000 ? CX + 0.5 * FW : CX, CY, t < 20000 ? FW : Smooth(FW, FW / 0.74, 20300, 20800, t), t, hazy: true, logo: true),
                            32000, sceneAt: Cut10, seeks: [20000]);
            var moved = log.FirstOrDefault(l => l.What == "Moved" && l.Ms >= 20000);
            var ids = log.Where(l => l.What == "identify").ToList();
            var back = ids.FirstOrDefault(l => l.Ms >= 20000);
            check(back.Id is { Outcome: ViewOutcome.Known, ViewId: 1 } && moved.What == "Moved" && moved.Step.Measured && moved.Ms <= 22000 && ids[^1].Id is { Outcome: ViewOutcome.New, ViewId: 3 },
                  $"다른 뷰를 들렀다 돌아온 직후의 줌: {Show(log)} · {Notes(log)}");
        }
    }

    /// <summary>무늬가 가운데 가로 띠(화면 높이의 약 1/4)에만 있는 세상 — 나머지는 민무늬(역광으로 하늘·노면이 날아간 영상 꼴). 차는 Render 가 그린다</summary>
    private static Mat BandWorld()
    {
        var img = new Mat(1800, 3200, MatType.CV_8UC3, new Scalar(150, 160, 165));
        var rng = new Random(777);
        for (int i = 0; i < 900; i++)
        {
            var c = new Scalar(rng.Next(20, 235), rng.Next(20, 235), rng.Next(20, 235));
            int x = rng.Next(3200), y = rng.Next(800, 1000), ww = rng.Next(8, 70), hh = rng.Next(8, 50);
            switch (i % 4)
            {
                case 0: Cv2.Circle(img, new Point(x, y), ww / 3, c, -1); break;
                case 1: Cv2.Line(img, new Point(x, y), new Point(x + ww, Math.Min(1000, y + hh)), c, rng.Next(1, 4)); break;
                default: Cv2.Rectangle(img, new Rect(x, y, ww, Math.Min(hh, 1000 - y)), c, -1); break;
            }
        }
        return img;
    }

    /// <summary>
    /// posewatch: 앱 재생 루프의 화면 전환 감지 + 뷰 처리(ViewSession — 앱과 같은 것)로 영상을 처음부터(또는 [시작초]부터) 돌리고
    /// 전환·식별·움직임 판정을 시각과 함께 적는다 — 헛경보가 없는지, 천천히 한 줌·팬을 잡는지. 프레임마다 뷰 처리 시간도 잰다(재생이 멈칫하는 곳)
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
            if (cap.Read(ff) && !ff.Empty()) { var id0 = registry.Identify(ff, atMs: f0 * 1000); Console.WriteLine($"  {f0,8:0.00}초 (먼저) 뷰 V{id0.ViewId} {id0.Outcome}"); }
        }
        cap.Set(VideoCaptureProperties.PosFrames, Math.Round(from * fps));
        using var session = new ViewSession(registry);
        using var thumb = new SceneThumbnail();
        var scene = new SceneChangeDetector();
        int checks = 0, moving = 0, moved = 0, resumed = 0, sceneChanges = 0, identifies = 0, slow100 = 0, slow250 = 0;
        double checkMsSum = 0, identifyMsSum = 0;
        var slowest = new List<(double At, double Ms, string What)>();
        var sw = new System.Diagnostics.Stopwatch();
        using var frame = new Mat();
        double ms = 0;
        while (cap.Read(frame) && !frame.Empty())
        {
            ms = cap.Get(VideoCaptureProperties.PosMsec);
            if (ms / 1000 > to) break;
            var sc = scene.Update(thumb.Compute(frame), ms);
            bool wasFollowing = session.Pose.Following;
            sw.Restart();
            var vs = session.Update(frame, ms, sc);
            double t = sw.Elapsed.TotalMilliseconds;
            string what = vs.Identified != null ? "식별" : vs.Pose.Outcome != PoseOutcome.None ? $"확인 {vs.Pose.Outcome}" : vs.Purged > 0 ? "보조 기준 정리" : "";
            if (t > 100) slow100++;
            if (t > 250) slow250++;
            if (what != "") { slowest.Add((ms, t, what)); if (slowest.Count > 8) slowest.Remove(slowest.MinBy(s => s.Ms)); }
            if (vs.Pose.Outcome != PoseOutcome.None) { checks++; checkMsSum += t; }
            if (sc.ViewChanged) { sceneChanges++; Console.WriteLine($"  {ms / 1000,8:0.00}초 화면 전환 (밝기 차이 — 기준과의 거리 {sc.AnchorDistance:0.0})"); }
            if (vs.Pose.Outcome == PoseOutcome.Moving && !wasFollowing) { moving++; Console.WriteLine($"  {ms / 1000,8:0.00}초 움직임 시작{(session.Moving ? "" : " (증거 없음 — 추적은 계속)")} — {vs.Pose.Reason}"); }
            else if (vs.Pose.Outcome == PoseOutcome.Resumed) { resumed++; Console.WriteLine($"  {ms / 1000,8:0.00}초 멈춤 → 그 자리: {vs.Pose.Reason} ({t:0} ms)"); }
            else if (vs.Pose.Outcome == PoseOutcome.Moved) { moved++; Console.WriteLine($"  {ms / 1000,8:0.00}초 멈춤 → 다른 자세{(vs.Pose.Measured ? "" : "(재지 못함)")}: {vs.Pose.Reason} · 글자로 배울 짝 {vs.Pose.Overlay?.Count ?? 0} ({t:0} ms)"); }
            if (vs.Note != null) Console.WriteLine($"  {ms / 1000,8:0.00}초 · {vs.Note}");
            if (vs.Purged > 0) Console.WriteLine($"  {ms / 1000,8:0.00}초 보조 기준 {vs.Purged}장 버림 (움직인 뒤 자세를 떠난 뷰로 받아들이게 하던 것)");
            if (vs.Identified is { } vid)
            {
                identifies++; identifyMsSum += t;
                if (vid.Outcome == ViewOutcome.Unknown) Console.WriteLine($"  {ms / 1000,8:0.00}초 뷰를 알 수 없음 (인라이어 {vid.Match.Inliers} · 특징점 {vid.Match.Features}) ({t:0} ms)");
                else Console.WriteLine($"  {ms / 1000,8:0.00}초 뷰 V{vid.ViewId} {(vid.Outcome == ViewOutcome.New ? "새로 등록" : "알아봄")} (인라이어 {vid.Match.Inliers} · 칸 {vid.Match.Cells} · 이동 {vid.Match.Shift * 100:0.0}% · 배율 {vid.Match.Scale:0.000}) · 글자 칸 {registry.Overlay.Cells} ({t:0} ms)");
            }
        }
        Console.WriteLine($"=== {Path.GetFileName(args[0])} {from:0.#}~{ms / 1000:0.#}초: 화면 전환 {sceneChanges} · 자세 움직임 {moving} (다른 자세 {moved} · 그 자리 {resumed}) · 뷰 {registry.Count}개 · " +
                          $"확인 {checks}번 평균 {checkMsSum / Math.Max(1, checks):0.0} ms · 식별 {identifies}번 평균 {identifyMsSum / Math.Max(1, identifies):0.0} ms · 100 ms 넘은 프레임 {slow100} · 250 ms 넘은 프레임 {slow250} · 글자 칸 {registry.Overlay.Cells}");
        Console.WriteLine($"  가장 느린 뷰 처리: {string.Join(", ", slowest.OrderByDescending(s => s.Ms).Select(s => $"{s.At / 1000:0.0}초 {s.What} {s.Ms:0} ms"))}");
        foreach (double p in probes)
        {
            cap.Set(VideoCaptureProperties.PosFrames, Math.Round(p * fps));
            using var pf = new Mat();
            if (!cap.Read(pf) || pf.Empty()) continue;
            sw.Restart();
            var id = registry.Identify(pf, atMs: p * 1000);
            Console.WriteLine($"  식별 {p,8:0.00}초 → {id.Outcome} V{id.ViewId} (인라이어 {id.Match.Inliers} · 칸 {id.Match.Cells} · 이동 {id.Match.Shift * 100:0.0}% · 배율 {id.Match.Scale:0.000}) · 뷰 {registry.Count}개 ({sw.Elapsed.TotalMilliseconds:0} ms)");
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
