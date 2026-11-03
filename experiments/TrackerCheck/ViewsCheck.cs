using OpenCvSharp;
using DetectView;

// views: 화면 식별(2b)의 ORB + 호모그래피 맞춤을 실제 영상에서 재 본다 — 같은 화면은 인라이어가 많고 다른 화면은 거의 없어야 한다.
// 사용: views  → test4 의 세 화면(0~10.5·11.5~20.4·21.3~30.5초)에서 프레임들을 뽑아 서로 맞춰 보고, 같은 화면에 억지로 흔들림을 줘 본다
internal static class ViewsCheck
{
    public static int Run(string videoDir)
    {
        using var matcher = new ViewMatcher();
        var frames = new List<(string Name, string Video, double Sec)>
        {
            ("A1", "test4", 1.0), ("A2", "test4", 5.0), ("A3", "test4", 9.5),
            ("B1", "test4", 12.5), ("B2", "test4", 16.0), ("B3", "test4", 20.0),
            ("C1", "test4", 22.5), ("C2", "test4", 26.0), ("C3", "test4", 30.0),
            ("t1", "test1", 3.0), ("t2", "test2", 3.0), ("t3", "test3", 3.0), ("t5", "test5", 10.0), ("t5b", "test5", 40.0),
        };
        var sigs = new List<(string Name, ViewSignature Sig, Mat Gray)>();
        foreach (var (name, video, sec) in frames)
        {
            using var cap = new VideoCapture(Path.Combine(videoDir, video + ".mp4"), VideoCaptureAPIs.FFMPEG);
            if (!cap.IsOpened()) { Console.WriteLine($"열 수 없음: {video}"); return 1; }
            double fps = cap.Get(VideoCaptureProperties.Fps);
            cap.Set(VideoCaptureProperties.PosFrames, Math.Round(sec * fps));
            using var f = new Mat();
            if (!cap.Read(f) || f.Empty()) { Console.WriteLine($"읽지 못함: {name}"); return 1; }
            var gray = ViewMatcher.PrepareGray(f);
            sigs.Add((name, matcher.Compute(gray), gray));
        }

        Console.WriteLine("특징점 수: " + string.Join(" ", sigs.Select(s => $"{s.Name}={s.Sig.Points.Length}")));
        Console.WriteLine();
        Console.WriteLine("기준(행) → 새 화면(열): 인라이어/칸수");
        Console.Write("      ");
        foreach (var s in sigs) Console.Write($"{s.Name,-9}");
        Console.WriteLine();
        foreach (var r in sigs)
        {
            Console.Write($"{r.Name,-6}");
            foreach (var p in sigs)
            {
                var m = matcher.Compare(r.Sig, p.Sig);
                Console.Write($"{m.Inliers + "/" + m.Cells,-9}");
            }
            Console.WriteLine();
        }

        Console.WriteLine();
        Console.WriteLine("A2 기준 상세:");
        var a2 = sigs.First(s => s.Name == "A2");
        foreach (var p in sigs)
        {
            var m = matcher.Compare(a2.Sig, p.Sig);
            Console.WriteLine($"  A2→{p.Name,-4} 특징점 {m.Features,4} 좋은 짝 {m.Good,4} 인라이어 {m.Inliers,4} 칸 {m.Cells,2} 이동 {m.Shift * 100,6:F2}% 배율 {m.Scale,5:F3} 원근 {m.Skew,6:F3}");
        }

        // 한 번 식별하는 데 드는 시간의 내역 (실제 프레임 — 1280×720 BGR): 흑백·축소 / 특징점 / 기준 하나와 맞추기
        {
            using var cap = new VideoCapture(Path.Combine(videoDir, "test4.mp4"), VideoCaptureAPIs.FFMPEG);
            cap.Set(VideoCaptureProperties.PosFrames, Math.Round(5.0 * cap.Get(VideoCaptureProperties.Fps)));
            using var bgr = new Mat();
            cap.Read(bgr);
            const int N = 20;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < N; i++) { using var g = ViewMatcher.PrepareGray(bgr); }
            double tPrep = sw.Elapsed.TotalMilliseconds / N;
            using var gray = ViewMatcher.PrepareGray(bgr);
            sw.Restart();
            for (int i = 0; i < N; i++) { using var s = matcher.Compute(gray); }
            double tComp = sw.Elapsed.TotalMilliseconds / N;
            sw.Restart();
            for (int i = 0; i < N; i++) matcher.Compare(a2.Sig, a2.Sig);
            double tCmp = sw.Elapsed.TotalMilliseconds / N;
            Console.WriteLine();
            Console.WriteLine($"식별 한 번의 내역(프레임 1280×720): 흑백·축소 {tPrep:0.0} ms · 특징점 {tComp:0.0} ms · 기준 하나와 맞추기 {tCmp:0.0} ms (기준이 N개면 맞추기 ×N)");
        }

        Console.WriteLine();
        Console.WriteLine("A2 에 흔들림을 준 것 → A2 기준");
        foreach (var (label, make) in Perturbations())
        {
            using var g = make(a2.Gray);
            using var sig = matcher.Compute(g);
            var self = matcher.Compare(a2.Sig, sig);
            Console.WriteLine($"  {label,-14} 인라이어 {self.Inliers,4} 칸 {self.Cells,2} 이동 {self.Shift * 100,6:F2}% 배율 {self.Scale,5:F3}");
        }
        foreach (var s in sigs) { s.Sig.Dispose(); s.Gray.Dispose(); }
        return 0;
    }

    // ---------- selftest ⑫: 화면 식별 레지스트리 (2026-11-03) — 합성 화면으로 논리를, test4·test1~5 가 있으면 실제 프레임으로 한 번 더 ----------

    // 화면 종류 kind: 같은 kind 는 늘 같은 배경(무늬 350개), traffic 이 다르면 지나가는 차(사각형 8개)만 다르다.
    // 모든 화면에 같은 자리에 같은 글자가 찍힌다 — CCTV 의 방향 안내·시각 표시처럼 다른 화면끼리도 일치하는 고정 요소
    private static Mat Scene(int kind, int traffic, int w = 1280, int h = 720)
    {
        var img = new Mat(h, w, MatType.CV_8UC3, new Scalar(90, 100, 110));
        var rng = new Random(1000 + kind);
        for (int i = 0; i < 350; i++)
        {
            var c = new Scalar(rng.Next(30, 230), rng.Next(30, 230), rng.Next(30, 230));
            int x = rng.Next(w), y = rng.Next(h), ww = rng.Next(10, 120), hh = rng.Next(10, 90);
            if (i % 3 == 0) Cv2.Circle(img, new Point(x, y), ww / 3, c, -1);
            else Cv2.Rectangle(img, new Rect(x, y, ww, hh), c, -1);
        }
        var tr = new Random(7000 + traffic);
        for (int i = 0; i < 8; i++)
            Cv2.Rectangle(img, new Rect(tr.Next(w - 100), tr.Next(h - 60), 80, 40), new Scalar(tr.Next(256), tr.Next(256), tr.Next(256)), -1);
        Cv2.PutText(img, "CCTV 01  NORTH", new Point(30, 50), HersheyFonts.HersheySimplex, 1.2, Scalar.White, 3);
        Cv2.PutText(img, "2026-11-03 10:00:00", new Point(w - 430, h - 30), HersheyFonts.HersheySimplex, 1.0, Scalar.White, 2);
        return img;
    }

    public static void SelfTest(Action<bool, string> check, string videoDir)
    {
        string Id(ViewIdentification v) => $"{v.Outcome}{(v.ViewId > 0 ? " V" + v.ViewId : "")}";

        // 1) 서로 다른 화면 셋 → 새 뷰 V1·V2·V3 (같은 글자가 찍혀 있어도 한 뷰로 합쳐지지 않는다), 같은 화면이 다른 차와 함께 돌아오면 그 뷰
        using var reg = new ViewRegistry();
        var first = new List<ViewIdentification>();
        for (int k = 0; k < 3; k++) { using var s = Scene(k, 0); first.Add(reg.Identify(s)); }
        check(first.Select(Id).SequenceEqual(["New V1", "New V2", "New V3"]),
              $"서로 다른 화면 셋: {string.Join(" · ", first.Select(Id))} — 같은 글자(고정 요소)가 찍힌 화면이 한 뷰로 합쳐지지 않는다");

        var again = new List<ViewIdentification>();
        for (int k = 0; k < 3; k++) { using var s = Scene(k, 1 + k); again.Add(reg.Identify(s)); }
        check(again.Select(Id).SequenceEqual(["Known V1", "Known V2", "Known V3"]) && again.All(a => a.Match.Inliers >= 100),
              $"차가 달라진 같은 화면: {string.Join(" · ", again.Select(Id))} (인라이어 {string.Join("/", again.Select(a => a.Match.Inliers))}) — 레지스트리가 새로 늘지 않음 {reg.Count}개");

        // 2) 카메라 자세가 조금 어긋난 같은 뷰 — 프리셋으로 돌아올 때의 오차: 옮김·확대·밝기·노이즈는 같은 뷰, 크게 옮기거나 확대하면 다른 뷰
        using var s0 = Scene(0, 5);
        var small = new List<(string, ViewIdentification)>();
        foreach (var (label, make) in new (string, Func<Mat, Mat>)[]
        {
            ("8px 옮김", g => Shift(g, 8, 4)), ("20px 옮김", g => Shift(g, 20, 10)), ("1.03배 확대", g => Zoom(g, 1.03)),
            ("밝기 +25", g => { var o = new Mat(); g.ConvertTo(o, -1, 1, 25); return o; }),
            ("밝기 x0.75", g => { var o = new Mat(); g.ConvertTo(o, -1, 0.75, 0); return o; }),
        })
        {
            using var g = make(s0);
            small.Add((label, reg.Identify(g)));
        }
        check(small.All(x => x.Item2.Outcome == ViewOutcome.Known && x.Item2.ViewId == 1),
              $"자세가 조금 어긋난 같은 뷰: {string.Join(" · ", small.Select(x => $"{x.Item1} → {Id(x.Item2)}"))}");

        int before = reg.Count;
        using var farShift = Shift(s0, 190, 60);   // 가로의 15% — 같은 무늬지만 같은 자리에서 찍은 화면이 아니다
        var far = reg.Identify(farShift);
        check(far.Outcome == ViewOutcome.New && far.ViewId == 4 && reg.Count == before + 1 && !reg.Accepts(far.Match),
              $"크게 옮긴 화면은 다른 뷰: {Id(far)} (이동 {far.Match.Shift * 100:0.#}% — 한계 {reg.Options.MaxShift * 100:0.#}%) — 구역은 이 어긋남을 보정하지 않으므로 같은 뷰로 치지 않는다");

        // 3) 알아볼 수 없는 화면: 특징이 없는 화면(회색 한 장·부드러운 그라데이션)은 새 뷰로 등록하지 않는다
        int cnt = reg.Count;
        using var flat = new Mat(720, 1280, MatType.CV_8UC3, new Scalar(120, 120, 120));
        using var grad = new Mat(720, 1280, MatType.CV_8UC3);
        for (int y = 0; y < 720; y++) grad.Row(y).SetTo(new Scalar(40 + y / 6, 40 + y / 6, 40 + y / 6));
        var idFlat = reg.Identify(flat);
        var idGrad = reg.Identify(grad);
        check(idFlat.Outcome == ViewOutcome.Unknown && idGrad.Outcome == ViewOutcome.Unknown && reg.Count == cnt,
              $"특징 없는 화면: 회색 {Id(idFlat)}(특징점 {idFlat.Match.Features}) · 그라데이션 {Id(idGrad)}(특징점 {idGrad.Match.Features}) — 등록 안 함 {reg.Count}개");

        // 4) 저장했다 다시 읽어도 같은 결과 — 기준 화면이 JPEG 라 읽은 뒤에도 같은 서명이고, 뷰 번호는 다시 쓰지 않는다
        var saved = reg.Snapshot();
        using var reg2 = new ViewRegistry(saved, reg.NextId);
        using var s1b = Scene(1, 9);
        var re = reg2.Identify(s1b);
        using var s9 = Scene(9, 0);
        var brandNew = reg2.Identify(s9);
        check(re is { Outcome: ViewOutcome.Known, ViewId: 2 } && brandNew is { Outcome: ViewOutcome.New } && brandNew.ViewId == reg.NextId && reg2.FirstViewId == 2,
              $"저장본으로 다시 시작: V2 를 {Id(re)} · 새 화면은 {Id(brandNew)}(번호 {reg.NextId} 이어서) · 처음 알아본 뷰 V{reg2.FirstViewId}");

        // 5) 뷰 수 한도: 넘으면 새 화면을 등록하지 않고 알 수 없음
        using var capped = new ViewRegistry(options: new ViewIdentityOptions { MaxViews = 2 });
        var cs = new List<ViewIdentification>();
        for (int k = 0; k < 3; k++) { using var s = Scene(20 + k, 0); cs.Add(capped.Identify(s)); }
        check(cs[0].Outcome == ViewOutcome.New && cs[1].Outcome == ViewOutcome.New && cs[2].Outcome == ViewOutcome.Unknown && capped.Count == 2,
              $"뷰 수 한도 2: {string.Join(" · ", cs.Select(Id))} (등록 {capped.Count}개)");

        // 6) 비용 (참고): 식별 한 번 — 영상을 열 때·전환 뒤에만 부른다
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var timeSrc = Scene(1, 33);
        for (int i = 0; i < 20; i++) reg.Identify(timeSrc);
        Console.WriteLine($"[INFO] 화면 식별 한 번(합성 1280×720 — 무늬가 많아 실제 영상보다 느리다, 실제는 `views` 의 내역 약 6~10 ms. 등록 뷰 {reg.Count}개): 평균 {sw.Elapsed.TotalMilliseconds / 20:0.0} ms");

        // 7) 구역 파일 판 2: 뷰 목록·구역의 뷰 번호가 왕복하고, 판 1(뷰 식별 전) 파일은 뷰 없이(0) 읽히며 처음 알아본 뷰의 것이 된다
        var set = new RoiSet();
        var za = set.Add(RoiKind.Lane, [new(0.1, 0.1), new(0.5, 0.1), new(0.5, 0.5)], viewId: 1);
        var zb = set.Add(RoiKind.Shoulder, [new(0.2, 0.2), new(0.9, 0.2), new(0.9, 0.9)], viewId: 3);
        string json = set.ToJson(saved, reg.NextId);
        var back = RoiSet.FromJson(json);
        check(back.Zones.Select(z => (z.Id, z.ViewId)).SequenceEqual([(za.Id, 1), (zb.Id, 3)]) && back.SavedViews.Count == saved.Count
              && back.SavedViews.Select(v => v.Id).SequenceEqual(saved.Select(v => v.Id)) && back.SavedViews.Zip(saved).All(p => p.First.RefJpeg.SequenceEqual(p.Second.RefJpeg))
              && back.NextViewId == reg.NextId && back.ToJson() == json,
              $"구역 파일 판 2 왕복: 구역의 뷰 {string.Join("·", back.Zones.Select(z => "V" + z.ViewId))} · 뷰 {back.SavedViews.Count}개(기준 화면 JPEG 그대로) · nextView {back.NextViewId} · 다시 쓰면 같은 글자");

        var legacy = RoiSet.FromJson("{\"version\": 1, \"nextId\": 3, \"zones\": [{\"id\": 1, \"kind\": \"lane\", \"points\": [[0.1,0.1],[0.5,0.1],[0.5,0.5]]}, {\"id\": 2, \"kind\": \"ignore\", \"points\": [[0.6,0.6],[0.9,0.6],[0.9,0.9]]}]}");
        int adopted = legacy.AdoptUnassigned(2);
        check(legacy.SavedViews.Count == 0 && adopted == 2 && legacy.Zones.All(z => z.ViewId == 2) && legacy.ZonesOf(2).Count() == 2 && legacy.ZonesOf(1).Count() == 0 && legacy.ZoneAt(0.3, 0.2, 1) == null && legacy.ZoneAt(0.3, 0.2, 2)?.Id == 1,
              $"판 1 파일: 뷰 없이 읽고({legacy.SavedViews.Count}개) 처음 알아본 뷰 V2 의 구역이 됨({adopted}개) — 다른 뷰에선 그 자리를 눌러도 안 골라짐");

        string good = "{\"version\": 2, \"nextView\": 3, \"views\": [{\"id\": 1, \"ref\": \"AAEC\"}, {\"id\": 2, \"ref\": \"AAEC\"}], \"zones\": [{\"id\": 1, \"view\": 2, \"kind\": \"lane\", \"points\": [[0,0],[1,0],[1,1]]}]}";
        string[] bad =
        [
            good.Replace("\"view\": 2", "\"view\": 7"),                                  // views 에 없는 뷰
            good.Replace("\"id\": 2, \"ref\"", "\"id\": 1, \"ref\""),                    // 뷰 번호 겹침
            good.Replace("\"id\": 2, \"ref\": \"AAEC\"", "\"id\": 2, \"ref\": \"***\""),  // base64 아님
            good.Replace("\"ref\": \"AAEC\"}, {\"id\": 2", "\"ref\": \"\"}, {\"id\": 2"), // 기준 화면 없음
            good.Replace("\"id\": 1, \"ref\"", "\"id\": 0, \"ref\""),                    // 뷰 번호 0
            good.Replace("\"nextView\": 3", "\"nextView\": 0"),
            good.Replace("\"views\": [", "\"views\": {").Replace("}], \"zones\"", "}}, \"zones\""),   // 배열이 아님
            good.Replace("\"view\": 2", "\"view\": -1"),
            good.Replace("\"version\": 2", "\"version\": 3"),
        ];
        int notRejected = 0; string firstGood = "";
        foreach (var b in bad)
        {
            try { RoiSet.FromJson(b); notRejected++; if (firstGood == "") firstGood = b; }
            catch (FormatException) { }
            catch (Exception ex) { notRejected++; if (firstGood == "") firstGood = $"{b} → {ex.GetType().Name}"; }
        }
        var okSet = RoiSet.FromJson(good);
        check(notRejected == 0 && okSet.Zones[0].ViewId == 2 && okSet.SavedViews.Count == 2 && okSet.NextViewId == 3,
              $"잘못된 판 2 파일 {bad.Length}가지가 FormatException 으로 거부되고 올바른 파일은 읽힘 {(notRejected == 0 ? "" : $"(통과해 버린 것: {firstGood})")}");

        // 8) 실제 영상 (있을 때만): test4 의 세 화면은 각각 새 뷰, 같은 화면의 다른 장면은 그 뷰, 다른 영상(test1~3·5)은 모두 새 뷰
        string t4 = Path.Combine(videoDir, "test4.mp4");
        if (!File.Exists(t4)) { Console.WriteLine("[INFO] 실제 영상 화면 식별 건너뜀 — publish\\test4.mp4 없음"); return; }
        using var real = new ViewRegistry();
        var results = new List<(string, ViewIdentification)>();
        foreach (var (name, video, sec) in new (string, string, double)[]
        {
            ("A1", "test4", 1.0), ("B1", "test4", 12.5), ("C1", "test4", 22.5),
            ("A3", "test4", 9.5), ("B3", "test4", 20.0), ("C3", "test4", 30.0), ("A2", "test4", 5.0), ("B2", "test4", 16.0), ("C2", "test4", 26.0),
            ("test1", "test1", 3.0), ("test2", "test2", 3.0), ("test3", "test3", 3.0), ("test5", "test5", 10.0), ("test5 후반", "test5", 40.0),
        })
        {
            string vp = Path.Combine(videoDir, video + ".mp4");
            if (!File.Exists(vp)) continue;
            using var cap = new VideoCapture(vp, VideoCaptureAPIs.FFMPEG);
            double fps = cap.Get(VideoCaptureProperties.Fps);
            cap.Set(VideoCaptureProperties.PosFrames, Math.Round(sec * fps));
            using var f = new Mat();
            if (!cap.Read(f) || f.Empty()) continue;
            results.Add((name, real.Identify(f)));
        }
        var exp = new Dictionary<string, string>
        {
            ["A1"] = "New V1", ["B1"] = "New V2", ["C1"] = "New V3", ["A3"] = "Known V1", ["B3"] = "Known V2", ["C3"] = "Known V3",
            ["A2"] = "Known V1", ["B2"] = "Known V2", ["C2"] = "Known V3",
        };
        var wrong = results.Where(r => exp.TryGetValue(r.Item1, out var e) && e != Id(r.Item2)).ToList();
        var others = results.Where(r => !exp.ContainsKey(r.Item1)).ToList();
        // 다른 영상 넷(test1·2·3·5)은 각각 새 뷰, 같은 영상 test5 의 다른 장면은 그 뷰 (같은 카메라 자세)
        int t5Id = results.FirstOrDefault(r => r.Item1 == "test5").Item2.ViewId;
        check(wrong.Count == 0 && results.Count(r => exp.ContainsKey(r.Item1)) == 9
              && others.Where(o => o.Item1 != "test5 후반").All(o => o.Item2.Outcome == ViewOutcome.New)
              && others.Where(o => o.Item1 == "test5 후반").All(o => o.Item2.Outcome == ViewOutcome.Known && o.Item2.ViewId == t5Id),
              $"실제 영상: {string.Join(" ", results.Where(r => exp.ContainsKey(r.Item1)).Select(r => $"{r.Item1}={Id(r.Item2)}"))} · 다른 영상 {string.Join(" ", others.Select(o => $"{o.Item1}={Id(o.Item2)}"))}" +
              (wrong.Count == 0 ? "" : $" (어긋난 것: {string.Join(", ", wrong.Select(w => w.Item1))})"));
    }

    // ---------- mkrevisit: test4 에서 같은 화면이 다시 나오는 합성 영상을 만든다 (UI 테스트용 — 만든 영상은 저작권이 있는 원본의 조각이라 저장소엔 넣지 않는다, out/ 은 gitignore) ----------
    // 순서(원본 초): A 0~8 → B 12~18 → A 1~9(차가 다름 · 멈춘 트럭이 그대로) → C 22~26 → B 14~18. 모두 하드 컷이라 화면 전환 감지가 컷으로 잡아야 한다
    public static int MakeRevisit(string videoDir, string outPath)
    {
        string src = Path.Combine(videoDir, "test4.mp4");
        if (!File.Exists(src)) { Console.WriteLine("test4.mp4 가 없습니다: " + src); return 1; }
        using var cap = new VideoCapture(src, VideoCaptureAPIs.FFMPEG);
        double fps = cap.Get(VideoCaptureProperties.Fps);
        int w = (int)cap.Get(VideoCaptureProperties.FrameWidth), h = (int)cap.Get(VideoCaptureProperties.FrameHeight);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        if (File.Exists(outPath)) File.Delete(outPath);
        using var writer = new VideoWriter(outPath, VideoWriter.FourCC('m', 'p', '4', 'v'), fps, new Size(w, h));
        if (!writer.IsOpened()) { Console.WriteLine("영상을 쓸 수 없습니다: " + outPath); return 1; }
        int frames = 0;
        foreach (var (from, to) in new (double, double)[] { (0, 8), (12, 18), (1, 9), (22, 26), (14, 18) })
        {
            cap.Set(VideoCaptureProperties.PosFrames, Math.Round(from * fps));
            int n = (int)Math.Round((to - from) * fps);
            using var f = new Mat();
            for (int i = 0; i < n && cap.Read(f) && !f.Empty(); i++) { writer.Write(f); frames++; }
        }
        Console.WriteLine($"{outPath}: {frames}프레임 ({frames / fps:0.0}초, {w}×{h} {fps:0.#}fps)");
        return 0;
    }

    private static IEnumerable<(string, Func<Mat, Mat>)> Perturbations()
    {
        yield return ("그대로", g => g.Clone());
        foreach (int dx in new[] { 3, 8, 20 })
            yield return ($"{dx}px 옮김", g => Shift(g, dx, dx / 2.0));
        foreach (double z in new[] { 1.03, 1.08 })
            yield return ($"{z:F2}배 확대", g => Zoom(g, z));
        yield return ("0.7도 회전", g => Rotate(g, 0.7));
        yield return ("밝기 +25", g => { var o = new Mat(); g.ConvertTo(o, -1, 1, 25); return o; });
        yield return ("밝기 x0.75", g => { var o = new Mat(); g.ConvertTo(o, -1, 0.75, 0); return o; });
        yield return ("노이즈 s8", g => Noise(g, 8));
        yield return ("JPEG 40", g => { Cv2.ImEncode(".jpg", g, out byte[] b, new ImageEncodingParam(ImwriteFlags.JpegQuality, 40)); return Cv2.ImDecode(b, ImreadModes.Grayscale); });
    }

    private static Mat Shift(Mat g, double dx, double dy)
    {
        using var m = Mat.FromPixelData(2, 3, MatType.CV_64FC1, new double[] { 1, 0, dx, 0, 1, dy });
        var o = new Mat(); Cv2.WarpAffine(g, o, m, g.Size(), InterpolationFlags.Linear, BorderTypes.Replicate); return o;
    }

    private static Mat Zoom(Mat g, double z)
    {
        using var m = Cv2.GetRotationMatrix2D(new Point2f(g.Width / 2f, g.Height / 2f), 0, z);
        var o = new Mat(); Cv2.WarpAffine(g, o, m, g.Size(), InterpolationFlags.Linear, BorderTypes.Replicate); return o;
    }

    private static Mat Rotate(Mat g, double deg)
    {
        using var m = Cv2.GetRotationMatrix2D(new Point2f(g.Width / 2f, g.Height / 2f), deg, 1);
        var o = new Mat(); Cv2.WarpAffine(g, o, m, g.Size(), InterpolationFlags.Linear, BorderTypes.Replicate); return o;
    }

    private static Mat Noise(Mat g, double sigma)
    {
        using var n = new Mat(g.Size(), MatType.CV_32FC1);
        Cv2.Randn(n, new Scalar(0), new Scalar(sigma));
        using var f = new Mat(); g.ConvertTo(f, MatType.CV_32FC1);
        using var sum = new Mat(); Cv2.Add(f, n, sum);
        var o = new Mat(); sum.ConvertTo(o, MatType.CV_8UC1); return o;
    }
}
