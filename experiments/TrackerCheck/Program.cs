// 추적기(루트 Tracker.cs) 검증. 기준 영상 test4·test5 의 모든 프레임을 본 앱 ProcessFrame 과 같은 길로 검출해 CSV 로 남기고(detect),
// 그 검출로 추적기를 돌려 트랙별 시작·끝·관측 비율·클래스 흔들림과 의심스러운 연결을 보고하고(track),
// 트랙 ID 를 그린 프레임을 뽑아 눈으로 확인한다(render). 검출은 GPU 로 한 번만 하고, 파라미터는 track 만 다시 돌려 고른다.
// 본 앱 표시 경로의 중복 박스 필터(Detection.cs DuplicateBoxes) 전후도 여기서 본다(dedup). SAHI 병합 방식 전후도 본다(sahi).
// 화면 가장자리에 걸친 박스의 멈춤 판정(이슈 #10)은 track 이 함께 재고(noframe: 프레임 크기를 안 줘 예전 판정), 합성 장면으로도 본다(selftest).
// YOLO 입력 축소 방식(2026-11-05 Nearest → 쌍선형 → OpenCV 레터박스)은 InputCheck.cs 가 본다(testsplit · inputbench).
// 검출(detect·dedup·sahi)은 본 앱 DetectBgra 처럼 루트 YoloLetterbox.cs 로 한다(detect nearest 면 2026-11-05 전의 앱).
//
// 실행: TrackerCheck detect [nearest]
//       TrackerCheck track [MovingGapMs=1000 StationaryGapMs=10000 IouThreshold=0.3 RelinkIouThreshold=0.5 BorderMargin=2 ...] [noreset] [noframe]
//       TrackerCheck selftest
//       TrackerCheck gpu        (추적기와 별개: 루트 GpuAdapters.cs 의 내장/외장 판정 점검 — 이슈 #3. 종료 코드 = 실패 수)
//       TrackerCheck dedup [Every=15]
//       TrackerCheck sahi [Every=15] [Variant=nmm] [test5@44.75 ...]
//       TrackerCheck render <test4|test5> <x,y,w,h|full> <배율> <초> [<초> ...]
//       TrackerCheck testsplit <데이터셋 폴더> <출력 폴더> <이름=모델.onnx> [...]   (InputCheck.cs)
//       TrackerCheck inputbench [반복=200]                                          (InputCheck.cs)
//       TrackerCheck vlm <프레임.png> <x1> <y1> <x2> <y2> <출력 앞부분>            (VlmParity.cs — VLM 그림·요청이 실험과 같은지, check_app_parity.py 가 부른다)

using System.Globalization;
using System.Text;
using OpenCvSharp;
using SkiaSharp;
using Vortice.DXGI;
using YoloDotNet;
using YoloDotNet.Extensions;
using YoloDotNet.Models;
using DetectView;
using Tracker = DetectView.Tracker;   // OpenCvSharp.Tracker 와 겹친다

const double Confidence = 0.3, Iou = 0.7;   // 본 앱 ConfidenceThreshold · IouThreshold
string[] TargetClasses = ["person", "car", "motorcycle", "truck", "bus", "scooter", "bicycle", "pickup"];   // 본 앱 TargetClasses

// 테스트 영상은 gitignore 대상이라 워크트리에는 없다 — 원래 저장소의 배포 폴더에서 읽는다
// 영상 폴더는 RepoPaths.VideoDir (저장소 루트의 publish — 절대 경로를 박지 않는다)

// 뷰 구간(ms). test4 는 10.5~11.4초·20.5~21.2초에 카메라가 돌아간다 (CLAUDE.md "테스트 영상 내용").
// 전환 감지는 나중 단계라, 여기선 알려진 구간에서 Reset() 하고 그동안은 추적기에 넣지 않는다 (전환 감지기가 할 일을 흉내)
var videos = new (string Name, (double From, double To)[] Views)[]
{
    ("test4", [(0, 10500), (11400, 20500), (21200, double.PositiveInfinity)]),
    ("test5", [(0, double.PositiveInfinity)]),
};

// 프레임 크기 — track 이 추적기에 넘겨 테두리에 닿은 박스 변을 멈춤 판정에서 뺀다(이슈 #10). 검출 CSV 엔 없어서 여기 두고 detect 가 영상과 맞춰 본다
var frameSizes = new Dictionary<string, (int W, int H)> { ["test4"] = (1280, 720), ["test5"] = (1280, 854) };

// 끝났다는 기준: 이 정지 차량들이 그 구간 내내 트랙 하나여야 한다. 상자는 첫 실행에서 잰 평균 박스(눈으로 확인)
var targets = new (string Video, string Name, double FromMs, double ToMs, Det Box)[]
{
    ("test4", "차로 정차 덤프트럭 (0~10.5초)", 0, 10500, new Det(491, 287, 97, 85, "truck", 1)),
    ("test5", "갓길 정차 흰색 화물차 (0~47초, 검은 끊김 포함)", 0, double.PositiveInfinity, new Det(649, 519, 64, 84, "truck", 1)),
};

// 검출은 본 앱 DetectBgra 와 같은 길 — 루트 YoloLetterbox.cs 를 링크한다(OpenCV 쌍선형 레터박스 → YoloDotNet 배율 1 → 박스 +0.5 로 되돌림).
// detect nearest 면 2026-11-05 전의 앱(YoloDotNet 이 Nearest 로 직접 줄이고 정수 박스 그대로)
var letterbox = new YoloLetterbox();
bool oldYoloPath = false;
string repo = FindRepoRoot();
string outDir = Path.Combine(repo, "experiments", "TrackerCheck", "out");
Directory.CreateDirectory(outDir);
Console.OutputEncoding = Encoding.UTF8;

return args.FirstOrDefault() switch
{
    "detect" => Detect(args.Contains("nearest")),
    "track" => TrackAll(args.Skip(1).ToArray()),
    "selftest" => SelfTest(),
    "gpu" => GpuCheck(),
    "scene" => SceneScan(args.Skip(1).ToArray()),
    "views" => ViewsCheck.Run(RepoPaths.VideoDir),
    "mkrevisit" => ViewsCheck.MakeRevisit(RepoPaths.VideoDir, args.Length > 1 ? args[1] : Path.Combine(outDir, "test4_revisit.mp4")),
    "dedup" => Dedup(args.Skip(1).ToArray()),
    "sahi" => SahiCompare(args.Skip(1).ToArray()),
    "render" when args.Length >= 5 => Render(args[1], args[2], double.Parse(args[3], CultureInfo.InvariantCulture),
                                             args.Skip(4).Select(s => double.Parse(s, CultureInfo.InvariantCulture)).ToArray()),
    "testsplit" => InputCheck.TestSplit(args.Skip(1).ToArray()),
    "inputbench" => InputCheck.InputBench(args.Skip(1).ToArray()),
    "vlm" => VlmParity.Run(args.Skip(1).ToArray()),
    _ => Usage(),
};

static int Usage()
{
    Console.WriteLine("사용법: detect [nearest] | track [이름=값 ...] [noreset] [noframe] | selftest | gpu | dedup [Every=N] | sahi [Every=N] [영상@초 ...] | render <영상> <x,y,w,h|full> <배율> <초...> | " +
                      "testsplit <데이터셋> <출력 폴더> <이름=모델.onnx> ... | inputbench [반복] | vlm <프레임.png> <x1> <y1> <x2> <y2> <출력 앞부분>");
    return 2;
}

// ---------- scene: 프레임 간 변화량 실측 (화면 전환 감지의 임계값을 감으로 정하지 않으려고) → out/<영상>.scene.csv ----------
// 앱 재생 루프와 같은 SceneThumbnail(64×36 흑백)로 연속한 두 프레임의 평균 절대차 d 와 평균 밝기를 재고,
// d 가 임계값(6 · 12 · 25)을 넘는 연속 구간(에피소드)을 시각과 함께 보인다. 인자: 영상 경로들 (생략하면 배포 폴더 test*.mp4 + tests/ui/media/*.webm)
int SceneScan(string[] paths)
{
    if (paths.Length == 0)
        paths = Directory.GetFiles(RepoPaths.VideoDir, "test*.mp4").OrderBy(p => p).Concat(Directory.GetFiles(Path.Combine(repo, "tests", "ui", "media"), "*.webm")).ToArray();

    foreach (string path in paths)
    {
        using var cap = new VideoCapture(path);
        if (!cap.IsOpened()) { Console.WriteLine($"[건너뜀] 열 수 없음: {path}"); continue; }
        string name = Path.GetFileNameWithoutExtension(path);
        using var thumb = new SceneThumbnail();
        using var frame = new Mat();
        byte[]? prev = null;
        var rows = new List<(int N, double Ms, double D, double Mean)>();
        int n = 0;
        while (cap.Read(frame) && !frame.Empty())
        {
            double ms = cap.Get(VideoCaptureProperties.PosMsec);
            byte[] cur = thumb.Compute(frame);
            double sum = 0, diff = 0;
            for (int i = 0; i < cur.Length; i++) { sum += cur[i]; if (prev != null) diff += Math.Abs(cur[i] - prev[i]); }
            rows.Add((n++, ms, prev == null ? 0 : diff / cur.Length, sum / cur.Length));
            prev = (byte[])cur.Clone();
        }

        using (var w = new StreamWriter(Path.Combine(outDir, name + ".scene.csv")))
        {
            w.WriteLine("frame,ms,d,mean");
            foreach (var r in rows) w.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{r.N},{r.Ms:0.###},{r.D:0.###},{r.Mean:0.##}"));
        }

        // 같은 썸네일 열로 감지기를 돌려, 움직임 구간마다 안정된 뒤 직전 안정 화면과의 거리(AnchorDistance)와 전환 판정을 본다 —
        // ViewDiff 는 전환의 거리와 전환이 아닌 구간(검은 끊김·지나가는 차)의 거리 사이에 놓여야 한다
        {
            var det = new SceneChangeDetector();
            using var cap2 = new VideoCapture(path);
            using var f2 = new Mat();
            using var t2 = new SceneThumbnail();
            var lines = new List<string>();
            int unsettledFrames = 0, changes = 0, blanks = 0, timed = 0;
            long ticks = 0;
            while (cap2.Read(f2) && !f2.Empty())
            {
                double ms = cap2.Get(VideoCaptureProperties.PosMsec);
                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                byte[] th = t2.Compute(f2);
                var s = det.Update(th, ms);
                ticks += System.Diagnostics.Stopwatch.GetTimestamp() - t0;   // 앱이 프레임마다 더 쓰는 시간 = 썸네일 + 감지기
                timed++;
                if (th.All(b => b <= 24)) blanks++;
                if (s.Unsettled) unsettledFrames++;
                if (s.Settled)
                {
                    if (s.ViewChanged) changes++;
                    lines.Add($"{s.EpisodeStartMs / 1000:0.00}→{ms / 1000:0.00}초 움직임 {s.HotFrames}장 · 기준과의 거리 {s.AnchorDistance:0.0} → {(s.ViewChanged ? "전환" : "같은 화면")}");
                }
            }
            Console.WriteLine($"\n=== {name} 감지기: 전환 {changes}번 · 움직이는 중으로 본 프레임 {unsettledFrames}장 · 빈(검은) 프레임 {blanks}장 · 프레임당 썸네일+감지 {ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency / Math.Max(1, timed):0.000} ms");
            foreach (var l in lines) Console.WriteLine("  " + l);
        }

        var ds = rows.Skip(1).Select(r => r.D).OrderBy(x => x).ToArray();
        double Pct(double p) => ds.Length == 0 ? 0 : ds[Math.Min(ds.Length - 1, (int)(p * ds.Length))];
        Console.WriteLine($"\n=== {name}: {rows.Count}프레임 · {rows[^1].Ms / 1000:0.0}초 · d 중앙 {Pct(0.5):0.00} · 95% {Pct(0.95):0.00} · 99% {Pct(0.99):0.00} · 최대 {ds[^1]:0.0} · 밝기 {rows.Min(r => r.Mean):0.0}~{rows.Max(r => r.Mean):0.0}");
        foreach (double t in new[] { 6.0, 12.0, 25.0 })
        {
            var eps = new List<(double From, double To, int Count, double Peak)>();
            double from = 0, last = 0, peak = 0; int cnt = 0;
            foreach (var r in rows.Skip(1))
            {
                if (r.D >= t)
                {
                    if (cnt == 0) { from = r.Ms; peak = 0; }
                    cnt++; last = r.Ms; peak = Math.Max(peak, r.D);
                }
                else if (cnt > 0) { eps.Add((from, last, cnt, peak)); cnt = 0; }
            }
            if (cnt > 0) eps.Add((from, last, cnt, peak));
            Console.WriteLine($"  d ≥ {t,2}: 에피소드 {eps.Count}개 (프레임 {eps.Sum(e => e.Count)}장)" +
                              (eps.Count > 0 && eps.Count <= 14 ? " — " + string.Join(" · ", eps.Select(e => $"{e.From / 1000:0.00}~{e.To / 1000:0.00}초 {e.Count}장 최대 {e.Peak:0.#}")) : ""));
        }
    }
    return 0;
}

// ---------- detect: 모든 프레임 검출 → out/<영상>.dets.csv ----------
// YoloDotNet 의 축소 방식은 본 앱과 같은 쌍선형(InputCheck.AppSampling — 앱 InputSampling 복사본). 검출은 YoloLetterbox 가 레터박스를 직접 만들어
// 배율 1 로 그려져 이 값이 듣지 않는다. nearest = 2026-11-05 전의 앱(YoloDotNet 이 Nearest 로 직접 줄임 — DetectBgra 의 oldYoloPath)
Yolo OpenModel(Action<string> log, bool nearest = false)
{
    string modelPath = Path.Combine(repo, "best.onnx");
    var (gpuName, vendorId, deviceId) = SelectBestGpu();
    var device = WindowsMlExecutionProvider.FindDmlDevice(vendorId, deviceId)
                 ?? throw new InvalidOperationException("DirectML 장치를 PCI ID 로 찾지 못함");
    var yolo = new Yolo(new YoloOptions
    {
        ExecutionProvider = new WindowsMlExecutionProvider(modelPath, device),
        SamplingOptions = nearest ? InputCheck.OldSampling : InputCheck.AppSampling,
    });
    var meta = yolo.OnnxModel?.CustomMetaData;
    log($"모델: {meta?.GetValueOrDefault("description")} · {meta?.GetValueOrDefault("date")} · {gpuName} · " +
        (nearest ? "YoloDotNet 이 Nearest 로 직접 줄임 (2026-11-05 전의 앱)" : "YoloLetterbox — OpenCV 쌍선형 레터박스 (본 앱)"));
    return yolo;
}

int Detect(bool nearest)
{
    oldYoloPath = nearest;
    using var yolo = OpenModel(Console.WriteLine, nearest);

    foreach (var (name, _) in videos)
    {
        string path = Path.Combine(RepoPaths.VideoDir, name + ".mp4");
        using var cap = new VideoCapture(path);
        if (!cap.IsOpened()) throw new FileNotFoundException(path);
        if ((cap.FrameWidth, cap.FrameHeight) != frameSizes[name])
            throw new InvalidDataException($"{name}: 영상이 {cap.FrameWidth}×{cap.FrameHeight} 인데 frameSizes 는 {frameSizes[name]} — track 이 추적기에 넘기는 크기를 고칠 것");
        using var writer = new StreamWriter(Path.Combine(outDir, name + ".dets.csv"));
        // 프레임마다 라벨 없는 행을 하나 둔다 — 검출이 없는 프레임도 시각이 남아야 공백을 잴 수 있다
        writer.WriteLine("frame,ms,label,conf,x,y,w,h");
        using var frame = new Mat();
        using var bgra = new Mat();
        int n = 0, total = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (cap.Read(frame) && !frame.Empty())
        {
            double ms = cap.Get(VideoCaptureProperties.PosMsec);   // 본 앱 재생 루프와 같은 시각
            writer.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{n},{ms:R},,,,,,"));
            Cv2.CvtColor(frame, bgra, ColorConversionCodes.BGR2BGRA);
            foreach (var d in DetectBgra(yolo, bgra, 0, 0))
            {
                writer.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"{n},{ms:R},{d.Label},{d.Conf:R},{d.X:R},{d.Y:R},{d.W:R},{d.H:R}"));
                total++;
            }
            n++;
        }
        Console.WriteLine($"{name}: {cap.FrameWidth}×{cap.FrameHeight} 프레임 {n}장 · 검출 {total}개 · {sw.Elapsed.TotalSeconds:F1}초");
    }
    return 0;
}

// ---------- track: 추적기 돌리고 보고 → out/<영상>.tracks.csv, out/report.txt ----------
int TrackAll(string[] opts)
{
    var p = opts.Where(o => o.Contains('=')).ToDictionary(o => o.Split('=')[0], o => double.Parse(o.Split('=')[1], CultureInfo.InvariantCulture));
    bool reset = !opts.Contains("noreset");
    bool withFrame = !opts.Contains("noframe");   // noframe: 프레임 크기를 안 줘 테두리 처리 전(예전) 판정 — 이슈 #10 전후 비교용
    var proto = new Tracker();
    Tracker NewTracker() => new()
    {
        IouThreshold = p.GetValueOrDefault("IouThreshold", proto.IouThreshold),
        MovingGapMs = p.GetValueOrDefault("MovingGapMs", proto.MovingGapMs),
        StationaryGapMs = p.GetValueOrDefault("StationaryGapMs", proto.StationaryGapMs),
        RelinkIouThreshold = p.GetValueOrDefault("RelinkIouThreshold", proto.RelinkIouThreshold),
        StationaryWindowMs = p.GetValueOrDefault("StationaryWindowMs", proto.StationaryWindowMs),
        StationaryMaxShift = p.GetValueOrDefault("StationaryMaxShift", proto.StationaryMaxShift),
        StationaryMinSpanMs = p.GetValueOrDefault("StationaryMinSpanMs", proto.StationaryMinSpanMs),
        DuplicateIos = p.GetValueOrDefault("DuplicateIos", proto.DuplicateIos),
        DuplicateMinAreaRatio = p.GetValueOrDefault("DuplicateMinAreaRatio", proto.DuplicateMinAreaRatio),
        BorderMargin = p.GetValueOrDefault("BorderMargin", proto.BorderMargin),
    };
    var t0 = NewTracker();
    var report = new StringBuilder();
    void Log(string s) { Console.WriteLine(s); report.AppendLine(s); }
    Log($"파라미터: IouThreshold={t0.IouThreshold} DuplicateIos={t0.DuplicateIos} DuplicateMinAreaRatio={t0.DuplicateMinAreaRatio} MovingGapMs={t0.MovingGapMs} StationaryGapMs={t0.StationaryGapMs} " +
        $"RelinkIouThreshold={t0.RelinkIouThreshold} StationaryWindowMs={t0.StationaryWindowMs} StationaryMaxShift={t0.StationaryMaxShift} " +
        $"StationaryMinSpanMs={t0.StationaryMinSpanMs} BorderMargin={t0.BorderMargin}" + (reset ? "" : " · 전환 구간 Reset 안 함") +
        (withFrame ? "" : " · 프레임 크기 안 줌(테두리 처리 전 판정)"));

    // Step=N: N 프레임에 하나만 넣는다 — 추적기가 시각 기준이라 프레임레이트가 바뀌어도(HUD·VFR) 같은 결과가 나오는지 본다
    int step = (int)p.GetValueOrDefault("Step", 1);
    if (step > 1) Log($"프레임 {step}장에 하나만 넣음");

    foreach (var (name, views) in videos)
    {
        var (fw, fh) = frameSizes[name];
        var frames = LoadDets(Path.Combine(outDir, name + ".dets.csv")).Where((_, i) => i % step == 0).ToList();
        var tracker = NewTracker();   // ID 는 영상 안에서 이어서 센다 (Reset 해도)
        var obs = new Dictionary<int, List<Obs>>();
        var judged = new List<Judged>();               // 프레임마다 관측된 트랙의 박스·정지 판정 (테두리 측정용)
        var segEndMs = new Dictionary<int, double>();  // Reset 사이 구간마다 마지막으로 넣은 프레임 시각
        int lastView = -1, fed = 0, seg = 0;
        using var w = new StreamWriter(Path.Combine(outDir, name + ".tracks.csv"));
        w.WriteLine("frame,ms,view,id,label,conf,x,y,w,h,stationary");
        foreach (var (f, ms, dets) in frames)
        {
            fed++;   // 관측 비율은 넣은 프레임 기준
            int view = Array.FindIndex(views, v => ms >= v.From && ms < v.To);
            if (reset && view < 0) { tracker.Reset(); lastView = -1; continue; }   // 전환 중: 규칙 중단
            if (reset && view != lastView) { tracker.Reset(); seg++; }
            lastView = view;
            var tracked = withFrame ? tracker.Update(dets, ms, fw, fh) : tracker.Update(dets, ms);
            // 이번 프레임에 관측된 트랙 (중복 박스는 대표와 같은 트랙이라 트랙마다 한 번)
            var seen = tracker.Tracks.Where(t => t.LastMs == ms).ToDictionary(t => t.Id);
            foreach (var t in seen.Values) judged.Add(new Judged(t.Id, ms, seg, t.Last, t.IsStationary));
            segEndMs[seg] = ms;
            foreach (var td in tracked)
            {
                var d = td.Det;
                w.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"{f},{ms:R},{view},{td.TrackId},{d.Label},{d.Conf:R},{d.X},{d.Y},{d.W},{d.H},{(seen[td.TrackId].IsStationary ? 1 : 0)}"));
            }
            // 중복 박스는 같은 ID 를 받으므로 통계는 트랙마다 프레임당 하나(신뢰도 최대)만 센다
            foreach (var g in tracked.GroupBy(td => td.TrackId))
            {
                if (!obs.TryGetValue(g.Key, out var list)) obs[g.Key] = list = [];
                list.Add(new Obs(fed, ms, view, g.MaxBy(td => td.Det.Conf).Det));
            }
        }
        Report(name, views, frames, obs, t0, Log);
        BorderReport(fw, fh, withFrame, judged, segEndMs, t0, Log);
        foreach (var target in targets.Where(t => t.Video == name)) CheckTarget(target, obs, Log);
    }
    File.WriteAllText(Path.Combine(outDir, "report.txt"), report.ToString());
    return 0;
}

// 테두리 멈춤 판정 (이슈 #10): 관측마다 추적기의 정지 판정과, 테두리 처리와 상관없이 따로 잰 '움직임'을 맞대 본다.
// 움직임 = 기준 관측(추적기처럼 판정 구간보다 오래된 것 중 가장 새것, 없으면 처음)에서 박스 중심이 추적기 허용치
// (StationaryMaxShift×√넓이, 구간이 짧으면 비례)보다 많이 움직임. 테두리에 걸쳐 들어오고 나가는 차는 한 변이 테두리에 붙어 있어도 중심이
// (반 속도로) 움직이고, 정말 멈춘 차는 중심도 그대로다. 테두리 띠는 추적기 여유와 따로 고정한다(2px 안·3~8px·안쪽) — 여유를 바꿔도 같은 잣대로 보려고
static void BorderReport(int fw, int fh, bool withFrame, List<Judged> judged, Dictionary<int, double> segEndMs, Tracker prm, Action<string> log)
{
    string[] bands = ["테두리 2px 안", "테두리 3~8px", "안쪽"];
    int Band(Det b)
    {
        double d = Math.Min(Math.Min(b.X, b.Y), Math.Min(fw - b.X - b.W, fh - b.Y - b.H));
        return d <= 2 ? 0 : d <= 8 ? 1 : 2;
    }
    int[] n = new int[3], stat = new int[3], movingStat = new int[3], notStat = new int[3], stillNotStat = new int[3];
    var bugTracks = new[] { new HashSet<int>(), new HashSet<int>(), new HashSet<int>() };
    var examples = new List<string>();
    int ghosts = 0, movingGhosts = 0, borderRelinks = 0;
    foreach (var g in judged.GroupBy(j => j.Id))
    {
        var l = g.ToList();   // 시각 순
        int r = 0;
        bool lastMoving = false;
        for (int i = 0; i < l.Count; i++)
        {
            if (i > 0 && l[i].Ms - l[i - 1].Ms > prm.MovingGapMs && Band(l[i - 1].Box) == 0) borderRelinks++;
            while (r + 1 < i && l[i].Ms - l[r + 1].Ms >= prm.StationaryWindowMs) r++;
            var (cur, old) = (l[i], l[r]);
            double span = cur.Ms - old.Ms;
            lastMoving = false;
            if (span < prm.StationaryMinSpanMs) continue;   // 추적기도 판정하지 않는 구간
            double size = Math.Sqrt(Math.Max(cur.Box.W * cur.Box.H, old.Box.W * old.Box.H));
            double allowed = prm.StationaryMaxShift * size * Math.Min(span, prm.StationaryWindowMs) / prm.StationaryWindowMs;
            double moved = Dist(cur.Box, old.Box);
            lastMoving = moved > allowed;
            int b = Band(cur.Box);
            n[b]++;
            if (cur.Stationary)
            {
                stat[b]++;
                if (!lastMoving) continue;
                movingStat[b]++;
                if (bugTracks[b].Add(cur.Id) && b == 0)
                    examples.Add(string.Create(CultureInfo.InvariantCulture,
                        $"    ID {cur.Id,-5} {cur.Ms / 1000,6:F2}초 {cur.Box.Label}({cur.Box.X:F0},{cur.Box.Y:F0},{cur.Box.W:F0}×{cur.Box.H:F0}) " +
                        $"{Tracker.BorderEdges(cur.Box, fw, fh, 2)} · 중심 {moved:F1}px / 허용 {allowed:F1}px ({span / 1000:F2}초)"));
            }
            else
            {
                notStat[b]++;
                if (!lastMoving) stillNotStat[b]++;
            }
        }
        // 안 보이게 된 뒤 정지로 남은 트랙: 마지막 박스가 테두리에 닿았고 정지였으며 그 뒤로 1초(MovingGapMs) 넘게 더 봤다 —
        // 움직이는 트랙이면 1초 뒤 지워지지만 정지라서 10초(StationaryGapMs) 남아 같은 자리의 새 검출과 다시 이어질 수 있다
        var last = l[^1];
        if (Band(last.Box) == 0 && last.Stationary && segEndMs[last.Segment] - last.Ms > prm.MovingGapMs)
        {
            ghosts++;
            if (lastMoving) movingGhosts++;
        }
    }
    log("");
    log($"테두리 멈춤 판정 (이슈 #10 · 프레임 {fw}×{fh} {(withFrame ? "을 추적기에 줌" : "을 추적기에 안 줌 — 예전 판정")} · 판정 구간 {prm.StationaryMinSpanMs / 1000:F1}초 이상인 관측):");
    for (int b = 0; b < 3; b++)
        log($"  {bands[b]} 박스 {n[b]}개 — 정지 {stat[b]} (그중 중심이 허용치보다 움직임 {movingStat[b]}개 · 트랙 {bugTracks[b].Count}개) · " +
            $"정지 아님 {notStat[b]} (그중 중심이 허용치 안 {stillNotStat[b]}개)");
    log($"  안 보이게 된 뒤 정지로 남은 트랙 (마지막 박스가 테두리 2px 안, 그 뒤로 {prm.MovingGapMs / 1000:F0}초 넘게 더 봄): {ghosts}개 (마지막 관측 때 움직이던 것 {movingGhosts}개)");
    log($"  테두리 2px 안 박스 다음 {prm.MovingGapMs / 1000:F0}초 넘는 공백 뒤 다시 이음: {borderRelinks}건");
    log("  움직이는데 정지로 본 테두리 2px 안 박스 (트랙마다 첫 관측):" + (examples.Count == 0 ? " 없음" : ""));
    foreach (var s in examples.Take(15)) log(s);
    if (examples.Count > 15) log($"    … 외 {examples.Count - 15}개 트랙");
}

// ---------- selftest: 합성 장면으로 테두리에 걸친 박스의 멈춤 판정(이슈 #10) — 영상·GPU 없이, 결정적. 종료 코드 = 실패 수 ----------
// 30fps. 물체는 보이는 부분까지만 박스가 잡히고(프레임으로 자름 — 보이는 폭·높이 10px 미만이면 검출 없음) 네 변이 프레임마다 ±1px 떨린다
// (고정 씨앗 난수). 예전 판정(프레임 크기 안 줌)에서 버그가 실제로 나는지도 함께 본다 — 안 나면 그 장면은 아무것도 시험하지 못한다
int SelfTest()
{
    const double Fps = 30;
    const int W = 1280, H = 720;   // test4 크기
    int fails = 0;
    void Check(bool ok, string what) { Console.WriteLine($"[{(ok ? "통과" : "실패")}] {what}"); if (!ok) fails++; }
    uint rng = 12345;
    int Jitter() { rng = rng * 1664525 + 1013904223; return (int)((rng >> 16) % 3) - 1; }

    List<Det>[] NewScene(double ms) => Enumerable.Range(0, (int)(ms * Fps / 1000) + 1).Select(_ => new List<Det>()).ToArray();
    // 참 박스 (x,y,w,h) 가 초당 (vx,vy) px 로 움직이는 물체를 fromMs~toMs 프레임에 넣는다. shrinkRight: 시각 → 오른쪽 변이 가려진 px
    void Add(List<Det>[] scene, int fw, int fh, double x, double y, double w, double h, double vx, double vy,
             double fromMs, double toMs, Func<double, double>? shrinkRight = null)
    {
        for (int i = 0; i < scene.Length; i++)
        {
            double ms = i * 1000 / Fps;
            if (ms < fromMs || ms > toMs) continue;
            double t = (ms - fromMs) / 1000, cx = x + vx * t, cy = y + vy * t;
            double x1 = Math.Max(0, cx), x2 = Math.Min(fw, cx + w - (shrinkRight?.Invoke(ms) ?? 0));
            double y1 = Math.Max(0, cy), y2 = Math.Min(fh, cy + h);
            if (x2 - x1 < 10 || y2 - y1 < 10) continue;
            x1 += Jitter(); x2 += Jitter(); y1 += Jitter(); y2 += Jitter();
            scene[i].Add(new Det(x1, y1, x2 - x1, y2 - y1, "car", 0.9));
        }
    }
    // 장면을 새 추적기에 넣고 프레임마다 (검출마다 (ID, 정지 판정), 살아 있는 트랙 수) — frame: 프레임 크기를 줄지
    List<SimFrame> Run(List<Det>[] scene, int fw, int fh, bool frame)
    {
        var tracker = new Tracker();
        var list = new List<SimFrame>();
        for (int i = 0; i < scene.Length; i++)
        {
            double ms = i * 1000 / Fps;
            var tracked = frame ? tracker.Update(scene[i], ms, fw, fh) : tracker.Update(scene[i], ms);
            list.Add(new SimFrame(ms, tracked.Select(td => (td.TrackId, tracker.Tracks.Single(t => t.Id == td.TrackId).IsStationary)).ToList(),
                              tracker.Tracks.Count));
        }
        return list;
    }
    static bool Same(List<SimFrame> a, List<SimFrame> b) =>
        a.Count == b.Count && a.Zip(b).All(z => z.First.Alive == z.Second.Alive && z.First.Dets.SequenceEqual(z.Second.Dets));
    static string Stat(List<SimFrame> run) =>
        $"정지 {run.Count(s => s.Dets.Count > 0 && s.Dets[0].Stat)}/{run.Count(s => s.Dets.Count > 0)} 프레임";

    // ① 테두리로 들어오고 나가는 차 (보이는 부분만 박스) — 프레임 크기를 주면 트랙 하나로 이어진 채 한 번도 정지가 아니어야 한다.
    //    예전 판정은 테두리에 붙은 변이 안 움직여 정지가 됐다(나가는 차는 1초 넘게 붙어 있으면) — 장면이 그 버그를 건드리는지 같이 본다
    var slides = new (string Name, double X, double Y, double W, double H, double Vx, double Vy, bool OldBug)[]
    {
        ("왼쪽 테두리로 들어옴", -80, 400, 80, 40, 30, 0, true),
        ("오른쪽 테두리로 들어옴", W, 400, 80, 40, -30, 0, true),
        ("위 테두리로 들어옴", 600, -80, 60, 80, 0, 30, true),
        ("아래 테두리로 들어옴", 600, H, 60, 80, 0, -30, true),
        ("왼쪽 테두리로 나감", 40, 400, 80, 40, -30, 0, true),
        ("오른쪽 테두리로 나감", W - 120, 400, 80, 40, 30, 0, true),
        ("위 테두리로 나감", 600, 40, 60, 80, 0, -30, true),
        ("아래 테두리로 나감", 600, H - 120, 60, 80, 0, 30, true),
        ("왼쪽 아래 모서리로 들어옴", -80, H, 80, 40, 30, -15, true),
        ("아래 테두리를 따라 지나감(아랫부분 잘림)", 400, H - 20, 80, 40, 30, 0, false),
    };
    foreach (var s in slides)
    {
        var scene = NewScene(5000);
        Add(scene, W, H, s.X, s.Y, s.W, s.H, s.Vx, s.Vy, 0, 5000);
        var now = Run(scene, W, H, frame: true);
        var old = Run(scene, W, H, frame: false);
        int ids = now.SelectMany(f => f.Dets).Select(d => d.Id).Distinct().Count();
        Check(ids == 1 && now.All(f => f.Dets.All(d => !d.Stat)), $"{s.Name}: {Stat(now)} · 트랙 {ids}개 (예전 판정 {Stat(old)})");
        if (s.OldBug) Check(old.Any(f => f.Dets.Any(d => d.Stat)), "  └ 예전 판정은 정지가 됨 — 장면이 버그를 건드림");
    }

    // ② 테두리에 걸친 채 정말 멈춘 차 — 반대쪽 변도 안 움직이므로 정지 (1초 관측 뒤 모든 프레임)
    var parked = new (string Name, double X, double Y, double W, double H)[]
    {
        ("왼쪽", -40, 400, 80, 40), ("오른쪽", W - 40, 400, 80, 40), ("위", 600, -40, 60, 80), ("아래", 600, H - 40, 60, 80),
        ("왼쪽 아래 모서리", -40, H - 40, 80, 80),
    };
    foreach (var s in parked)
    {
        var scene = NewScene(4000);
        Add(scene, W, H, s.X, s.Y, s.W, s.H, 0, 0, 0, 4000);
        var now = Run(scene, W, H, frame: true);
        var after = now.Where(f => f.Ms >= 1000).ToList();
        double first = now.FirstOrDefault(f => f.Dets.Count > 0 && f.Dets[0].Stat)?.Ms ?? double.NaN;
        int ids = now.SelectMany(f => f.Dets).Select(d => d.Id).Distinct().Count();
        Check(ids == 1 && after.All(f => f.Dets.Count == 1 && f.Dets[0].Stat),
              $"{s.Name} 테두리에 걸쳐 멈춘 차: 1초 뒤 {Stat(after)} · 트랙 {ids}개 (처음 정지 {first / 1000:F2}초)");
    }

    // ③ 테두리에 안 닿는 장면 — 프레임 크기를 줘도 예전 판정과 프레임마다 같아야 한다
    {
        // test5 갓길 화물차: 지나가는 트럭에 오른쪽부터 가려져 폭 64→40 (1.0~1.3초), 2.3~2.6초에 돌아옴
        static double Occlude(double ms) => ms < 1000 ? 0 : ms < 1300 ? 24 * (ms - 1000) / 300 : ms < 2300 ? 24 : ms < 2600 ? 24 * (2600 - ms) / 300 : 0;
        var scene = NewScene(4000);
        Add(scene, 1280, 854, 649, 519, 64, 84, 0, 0, 0, 4000, Occlude);
        var now = Run(scene, 1280, 854, frame: true);
        var after = now.Where(f => f.Ms >= 1000).ToList();
        Check(after.All(f => f.Dets[0].Stat), $"test5 화물차처럼 한쪽부터 가려져 폭 64→40: 1초 뒤 {Stat(after)}");
        Check(Same(now, Run(scene, 1280, 854, frame: false)), "  └ 프레임 크기를 안 준 예전 판정과 프레임마다 같음");
    }
    {
        var scene = NewScene(4000);
        Add(scene, W, H, 400, 300, 80, 40, 30, 0, 0, 4000);
        var now = Run(scene, W, H, frame: true);
        Check(now.All(f => !f.Dets[0].Stat), $"가운데서 움직이는 차: {Stat(now)}");
        Check(Same(now, Run(scene, W, H, frame: false)), "  └ 프레임 크기를 안 준 예전 판정과 프레임마다 같음");
    }

    // ④ 한 축의 두 변이 다 테두리에 닿음(가로를 다 덮은 박스) — 그 축은 잴 수 없다. 멈춰 있던 트랙은 판정을 그대로 두고,
    //    남은 축만으로 허용치를 넘게 움직이면 정지가 풀린다. 0~2초 왼쪽만 닿은 채 멈춤 → 2초부터 오른쪽도 닿음
    foreach (double vy in new[] { 0.0, 150 })
    {
        var scene = NewScene(4000);
        Add(scene, W, H, -10, 300, W, 100, 0, 0, 0, 2000);            // 왼쪽만 닿음 (오른쪽 변 1270)
        Add(scene, W, H, -10, 300, W + 20, 100, 0, vy, 2001, 4000);   // 오른쪽도 닿음
        var now = Run(scene, W, H, frame: true);
        bool before = now.Where(f => f.Ms >= 1000 && f.Ms < 2000).All(f => f.Dets[0].Stat);
        var tail = now.Where(f => f.Ms >= 2600).ToList();
        Check(before && (vy == 0 ? tail.All(f => f.Dets[0].Stat) : tail.All(f => !f.Dets[0].Stat)),
              vy == 0 ? $"가로를 다 덮은 채 그대로: 정지 유지 (2.6초 뒤 {Stat(tail)})"
                      : $"가로를 다 덮은 채 세로로 {vy}px/초: 정지가 풀림 (2.6초 뒤 {Stat(tail)})");
    }

    // ⑤ 화면을 나간 차의 트랙 — 움직이는 트랙이라 1초 뒤 지워져야 한다. 예전 판정은 정지로 남아 10초 동안 '멈춘 차'가 됐다
    {
        var scene = NewScene(8000);
        Add(scene, W, H, W - 120, 400, 80, 40, 30, 0, 0, 8000);   // 3.67초쯤 화면에서 사라진다
        var now = Run(scene, W, H, frame: true);
        var old = Run(scene, W, H, frame: false);
        double lastSeen = now.Last(f => f.Dets.Count > 0).Ms;
        Check(now.Where(f => f.Ms > lastSeen + 1100).All(f => f.Alive == 0), $"오른쪽으로 나간 차(마지막 검출 {lastSeen / 1000:F2}초): 1초 뒤 트랙이 지워짐");
        Check(old.Where(f => f.Ms > lastSeen + 1100).All(f => f.Alive == 1), "  └ 예전 판정은 정지로 남아 장면 끝(8초)까지 트랙이 남음 — 장면이 버그를 건드림");
    }
    // ⑥ 그 자리로 3초 뒤 다른 차가 들어오면 새 ID — 예전 판정은 남아 있던 정지 트랙과 다시 이어져 나간 차의 ID 를 물려받았다
    {
        var scene = NewScene(9000);
        Add(scene, W, H, W - 120, 400, 80, 40, 30, 0, 0, 3700);   // 나가는 차 (3.67초쯤 사라짐)
        Add(scene, W, H, W, 400, 80, 40, -30, 0, 6700, 9000);     // 3초 뒤 같은 자리로 들어오는 차
        int Id(List<SimFrame> run, double from, double to) => run.First(f => f.Ms >= from && f.Ms < to && f.Dets.Count > 0).Dets[0].Id;
        var now = Run(scene, W, H, frame: true);
        var old = Run(scene, W, H, frame: false);
        Check(Id(now, 0, 3700) != Id(now, 6700, 9000), $"나간 자리로 3초 뒤 들어온 차: 새 ID ({Id(now, 0, 3700)} → {Id(now, 6700, 9000)})");
        Check(Id(old, 0, 3700) == Id(old, 6700, 9000), $"  └ 예전 판정은 나간 차의 ID {Id(old, 0, 3700)} 를 물려받음 — 장면이 버그를 건드림");
    }

    // ⑦ 앱 연결 (2026-11-02): UpdateTagged 가 번호·정지 여부를 Det 에 그대로 붙인다 (좌표·라벨·신뢰도는 그대로)
    {
        var tagged = new Tracker();
        var plain = new Tracker();
        bool same = true;
        Det lastStill = default, lastMoving = default;
        for (int i = 0; i < 90; i++)
        {
            double ms = i * 1000 / Fps;
            var dets = new List<Det> { new(400, 300, 80, 40, "car", 0.9), new(100 + 6 * i, 100, 60, 30, "car", 0.8) };   // 그대로 / 매 프레임 6px
            var a = tagged.UpdateTagged(dets, ms, W, H);
            var b = plain.Update(dets, ms, W, H);
            for (int k = 0; k < dets.Count; k++)
                same &= a[k].TrackId == b[k].TrackId && a[k].Stationary == b[k].Stationary && a[k].TrackId > 0
                        && a[k] with { TrackId = 0, Stationary = false } == dets[k];
            (lastStill, lastMoving) = (a[0], a[1]);
        }
        Check(same, "UpdateTagged: 번호·정지가 Update 결과와 같고 좌표·라벨·신뢰도는 그대로");
        Check(lastStill.Stationary && !lastMoving.Stationary && lastStill.TrackId != lastMoving.TrackId,
              $"  └ 3초 뒤 멈춘 박스는 정지(#{lastStill.TrackId}), 움직이는 박스는 아님(#{lastMoving.TrackId})");
    }
    // 화면 HUD: DXGI 는 화면이 바뀔 때만 프레임을 줘서 정지 화면이면 프레임 사이가 몇 초~몇 분이다. 앱은 추적기 시계를 새 화면 사이
    // HudTrackStepCapMs(250ms, 앱 상수의 복사본)까지만 흐르게 한다. 같은 검출이 계속 들어올 때: ID 가 하나로 이어지고, 정지로 한 번만 바뀐 채
    // 수렴해야 한다 (바뀔 때마다 오버레이를 다시 그리고, 다시 그리면 DXGI 가 새 화면을 주므로 계속 흔들리면 추론 고리가 돈다)
    {
        static (int Ids, int Flips, bool Last) Static(double gapMs, double capMs, int frames)
        {
            var t = new Tracker();
            var ids = new HashSet<int>();
            double virtualMs = 0;
            int flips = 0;
            bool prev = false;
            for (int i = 0; i < frames; i++)
            {
                if (i > 0) virtualMs += Math.Min(gapMs, capMs);
                var r = t.UpdateTagged([new Det(400, 300, 80, 40, "car", 0.9)], virtualMs, 1280, 720);
                ids.Add(r[0].TrackId);
                if (i > 0 && r[0].Stationary != prev) flips++;
                prev = r[0].Stationary;
            }
            return (ids.Count, flips, prev);
        }
        var fast = Static(10, 250, 300);                    // GDI 처럼 매번 새로 캡처 (100fps)
        Check(fast is { Ids: 1, Flips: 1, Last: true }, $"HUD 같은 화면 100fps 3초: ID {fast.Ids}개 · 정지로 {fast.Flips}번 바뀜 · 끝 상태 {(fast.Last ? "정지" : "움직임")}");
        var idle = Static(5000, 250, 60);                   // 정지 화면 — 프레임 사이가 5초
        Check(idle is { Ids: 1, Flips: 1, Last: true }, $"HUD 같은 화면이 5초 간격: ID {idle.Ids}개 · 정지로 {idle.Flips}번 바뀜 · 끝 상태 {(idle.Last ? "정지" : "움직임")}");
        var raw = Static(5000, double.PositiveInfinity, 60);   // 시계를 자르지 않으면
        Check(raw.Ids == 60 && !raw.Last, $"  └ 시계를 안 자르면 프레임마다 트랙이 만료돼 새 번호 {raw.Ids}개 · 끝 상태 {(raw.Last ? "정지" : "움직임")} — 장면이 문제를 건드림");
    }

    // ⑧ 관심 구역(ROI, 2026-11-03): Roi.cs — 순수 모델·기하·파일. 앱의 편집 화면(RoiLayer)은 UI 테스트 Test-Roi.ps1 이 본다
    {
        // 사다리꼴 차로(아래가 넓고 위로 좁아짐) · 갓길 · 차로 안의 작은 정지선·대기 구역 · 맨 위 무시 띠 (프레임 0~1 좌표)
        RoiPoint[] lane = [new(0.40, 1.0), new(0.64, 1.0), new(0.52, 0.30), new(0.48, 0.30)];
        var set = new RoiSet();
        var z1 = set.Add(RoiKind.Lane, lane);
        var z2 = set.Add(RoiKind.Shoulder, [new(0.80, 1.0), new(1.0, 1.0), new(1.0, 0.40), new(0.85, 0.40)]);
        var z3 = set.Add(RoiKind.Stopline, [new(0.46, 0.90), new(0.58, 0.90), new(0.58, 0.95), new(0.46, 0.95)]);
        var z4 = set.Add(RoiKind.Ignore, [new(0, 0), new(1, 0), new(1, 0.10), new(0, 0.10)]);

        Check(z1.Contains(0.5, 0.8) && !z1.Contains(0.2, 0.8) && !z1.Contains(0.5, 0.2) && !z1.Contains(0.9, 0.8),
              "구역 안·밖 판정 (사다리꼴 차로)");
        double area = RoiGeometry.Area(lane);
        Check(Math.Abs(area - 0.098) < 1e-9, $"넓이: 사다리꼴 {area:0.#####} (손계산 0.098)");
        var cen = RoiGeometry.Centroid(lane);
        Check(z1.Contains(cen.X, cen.Y), $"무게중심 ({cen.X:0.###}, {cen.Y:0.###}) 이 구역 안 — 라벨이 구역 밖에 놓이지 않는다");
        Check(RoiGeometry.Area([new(0, 0), new(0.5, 0.5), new(1, 1)]) < 1e-9, "한 줄로 놓인 점은 넓이 0 (닫을 수 없는 구역을 거른다)");

        Check(set.ZoneAt(0.5, 0.92)?.Id == z3.Id && set.ZoneAt(0.5, 0.70)?.Id == z1.Id && set.ZoneAt(0.9, 0.7)?.Id == z2.Id
              && set.ZoneAt(0.5, 0.05)?.Id == z4.Id && set.ZoneAt(0.1, 0.5) == null,
              "점이 놓인 구역 고르기: 겹치면 가장 작은 것 (차로 안 정지선·대기), 빈 곳은 없음");
        Check(set.ZoneAt(-0.1, 0.5) == null && set.ZoneAt(0.5, 1.2) == null, "프레임 밖(0~1 밖)은 구역이 없음 — 레터박스를 눌러도 고르지 않는다");

        // 화면 DIP 에서 점·변 가운데 ○ 를 집는 판정 (RoiLayer 가 같은 함수를 쓴다)
        List<(double X, double Y)> dip = [(100, 100), (300, 100), (300, 200), (100, 200)];
        Check(RoiGeometry.NearestVertex(dip, 104, 97, 9) == 0 && RoiGeometry.NearestVertex(dip, 299, 201, 9) == 2
              && RoiGeometry.NearestVertex(dip, 150, 150, 9) == -1 && RoiGeometry.NearestVertex(dip, 112, 100, 9) == -1,
              "점 집기: 반경 안의 가장 가까운 점, 밖이면 없음");
        Check(RoiGeometry.NearestEdgeMidpoint(dip, 202, 103, 8, 28) == 0 && RoiGeometry.NearestEdgeMidpoint(dip, 300, 152, 8, 28) == 1
              && RoiGeometry.NearestEdgeMidpoint(dip, 200, 200, 8, 28) == 2 && RoiGeometry.NearestEdgeMidpoint(dip, 100, 150, 8, 28) == 3
              && RoiGeometry.NearestEdgeMidpoint(dip, 150, 150, 8, 28) == -1,
              "변 가운데 ○ 집기: 변 번호(점 i → i+1), 밖이면 없음");
        List<(double X, double Y)> tiny = [(0, 0), (10, 0), (10, 10), (0, 10)];
        Check(RoiGeometry.NearestEdgeMidpoint(tiny, 5, 0, 8, 28) == -1, "짧은 변(28 미만)엔 ○ 가 없어 집히지 않는다");

        // 번호: 지운 번호를 다시 쓰지 않고, 파일에 적어 다시 읽어도 이어진다
        Check(z1.Id == 1 && z2.Id == 2 && z3.Id == 3 && z4.Id == 4 && set.NextId == 5, "구역 번호는 1 부터 차례로");
        set.Remove(z2.Id);
        var z5 = set.Add(RoiKind.Lane, [new(0.1, 0.5), new(0.3, 0.5), new(0.2, 0.7)]);
        Check(z5.Id == 5 && set.Find(2) == null, $"지운 번호(2)를 다시 쓰지 않음: 새 구역 Z{z5.Id}");
        bool threw = false;
        try { set.Add(RoiKind.Lane, [new(0, 0), new(1, 1)]); } catch (ArgumentException) { threw = true; }
        Check(threw && set.Zones.Count == 4, "점이 3개 미만인 구역은 만들 수 없음");

        // JSON 왕복
        set.Remove(z5.Id);   // 가장 큰 번호를 지워도 nextId(6)가 파일에 남아야 한다
        string json = set.ToJson();
        var back = RoiSet.FromJson(json);
        bool same = back.Zones.Count == set.Zones.Count && back.NextId == set.NextId;
        for (int i = 0; same && i < set.Zones.Count; i++)
        {
            var a = set.Zones[i];
            var b = back.Zones[i];
            same = a.Id == b.Id && a.Kind == b.Kind && a.Points.Count == b.Points.Count
                   && a.Points.Zip(b.Points).All(p => Math.Abs(p.First.X - p.Second.X) < 1e-5 && Math.Abs(p.First.Y - p.Second.Y) < 1e-5);
        }
        Check(same && back.NextId == 6, $"JSON 왕복: 번호·유형·점이 그대로, nextId {back.NextId} 도 이어짐");
        Check(json.Contains("\"kind\": \"stopline\"") && json.Contains("\"version\": 2"), "파일에 적히는 이름: version·kind(lane·shoulder·stopline·ignore)");

        // 읽지 못하는 파일은 모두 FormatException (사유가 메시지에) — 앱은 이걸 받아 .bad 로 복사하고 빈 구역으로 시작한다
        string[] bad =
        [
            "", "not json", "[]", "{}",
            "{\"version\": 3, \"zones\": []}",
            "{\"version\": 1}",
            "{\"version\": 1, \"zones\": {}}",
            "{\"version\": 1, \"nextId\": 0, \"zones\": []}",
            "{\"version\": 1, \"zones\": [{\"id\": 0, \"kind\": \"lane\", \"points\": [[0,0],[1,0],[1,1]]}]}",
            "{\"version\": 1, \"zones\": [{\"id\": 1, \"kind\": \"road\", \"points\": [[0,0],[1,0],[1,1]]}]}",
            "{\"version\": 1, \"zones\": [{\"id\": 1, \"kind\": \"lane\", \"points\": [[0,0],[1,0]]}]}",
            "{\"version\": 1, \"zones\": [{\"id\": 1, \"kind\": \"lane\", \"points\": [[0,0],[1,0],[1,1,1]]}]}",
            "{\"version\": 1, \"zones\": [{\"id\": 1, \"kind\": \"lane\", \"points\": [[0,0],[1,0],[1,\"a\"]]}]}",
            "{\"version\": 1, \"zones\": [{\"id\": 1, \"kind\": \"lane\", \"points\": [[0,0],[1,0],[1,1]]}, {\"id\": 1, \"kind\": \"lane\", \"points\": [[0,0],[1,0],[1,1]]}]}",
        ];
        int notRejected = 0;
        string first = "";
        foreach (var b in bad)
        {
            try { RoiSet.FromJson(b); notRejected++; first = first == "" ? b : first; }
            catch (FormatException) { }
            catch (Exception ex) { notRejected++; first = first == "" ? $"{b} → {ex.GetType().Name}" : first; }
        }
        Check(notRejected == 0, $"잘못된 구역 파일 {bad.Length}가지가 모두 FormatException 으로 거부됨 {(notRejected == 0 ? "" : $"(통과해 버린 것: {first})")}");
        var clamped = RoiSet.FromJson("{\"version\": 1, \"zones\": [{\"id\": 3, \"kind\": \"ignore\", \"points\": [[-0.5,0],[2,0],[1,1]]}]}");
        Check(clamped.Zones[0].Points.All(p => p.X is >= 0 and <= 1 && p.Y is >= 0 and <= 1) && clamped.NextId == 4,
              "손으로 고친 0~1 밖 좌표는 안으로 맞추고, nextId 가 없으면 가장 큰 번호 + 1");

        // 파일 입출력 (임시 폴더)
        string dir = Path.Combine(Path.GetTempPath(), "roi-selftest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string path = RoiSet.PathFor(Path.Combine(dir, "clip.mp4"));
            Check(Path.GetFileName(path) == "clip.mp4.roi.json", $"파일 이름: {Path.GetFileName(path)}");
            Check(RoiSet.Load(path).Zones.Count == 0, "구역 파일이 없으면 빈 구역들");
            set.Save(path);
            byte[] head = File.ReadAllBytes(path).Take(3).ToArray();
            Check(File.Exists(path) && !File.Exists(path + ".tmp") && !(head.Length == 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF),
                  "저장: 파일이 생기고 임시 파일은 남지 않으며 UTF-8 BOM 이 없음");
            Check(RoiSet.Load(path).Zones.Count == set.Zones.Count, "저장한 파일을 다시 읽음");
            File.WriteAllText(path, "{ 깨진 파일");
            bool broke = false;
            try { RoiSet.Load(path); } catch (FormatException) { broke = true; }
            Check(broke, "깨진 파일을 읽으면 FormatException");
            new RoiSet().Save(path);
            Check(!File.Exists(path), "구역이 하나도 없으면 저장이 파일을 지움 (빈 파일을 영상 옆에 남기지 않는다)");
        }
        finally { Directory.Delete(dir, true); }
    }

    // ⑨ 화면 전환 감지 (2026-11-03): SceneChange.cs — 64×36 흑백 합성 그림으로. 실제 영상(test4 회전 둘·test5 검은 끊김)은 `scene` 명령이 본다
    {
        const int TW = SceneThumbnail.Width, TH = SceneThumbnail.Height;
        // 화면 종류 kind 를 가로로 shift 만큼 민 그림 (+ 흔들림 ±noise). 무늬가 달라 kind 가 다르면 평균 절대차가 크다
        byte[] Pic(int kind, int shift = 0, int noise = 0, double gain = 1)
        {
            var t = new byte[TW * TH];
            for (int y = 0; y < TH; y++)
                for (int x = 0; x < TW; x++)
                {
                    int sx = x + shift;
                    double v = kind == 0 ? (sx * 3 + y * 5) % 200 + 30 : (sx * 7 + y * 2 + 90) % 200 + 30;
                    v = v * gain + (noise == 0 ? 0 : Jitter() * noise);
                    t[y * TW + x] = (byte)Math.Clamp(v, 25, 255);   // 25 이상 — 빈(검은) 프레임이 아니게
                }
            return t;
        }
        byte[] Black() => new byte[TW * TH];
        double Dist(byte[] a, byte[] b) => SceneChangeDetector.MeanAbsDiff(a, b);

        Check(Dist(Pic(0), Pic(1)) > 30 && Dist(Pic(0), Pic(0, 6)) > 30 && Dist(Pic(0), Pic(0, 0, 2)) < 3,
              $"합성 그림이 쓸 만함: 다른 화면 {Dist(Pic(0), Pic(1)):0.#} · 6칸 민 화면 {Dist(Pic(0), Pic(0, 6)):0.#} · 흔들림만 {Dist(Pic(0), Pic(0, 0, 2)):0.#}");

        // 프레임 열을 감지기에 넣고 결과를 모은다 (30fps)
        (int Changes, int Settles, int Unsettled, List<int> ChangeAt) Feed(IEnumerable<byte[]> frames, SceneChangeOptions? opt = null, double fps = 30)
        {
            var d = new SceneChangeDetector(opt);
            int changes = 0, settles = 0, unsettled = 0, i = 0;
            var at = new List<int>();
            foreach (var f in frames)
            {
                var s = d.Update(f, i * 1000 / fps);
                if (s.Unsettled) unsettled++;
                if (s.Settled) settles++;
                if (s.ViewChanged) { changes++; at.Add(i); }
                i++;
            }
            return (changes, settles, unsettled, at);
        }
        IEnumerable<byte[]> Repeat(Func<int, byte[]> make, int n) => Enumerable.Range(0, n).Select(make);

        var still = Feed(Repeat(_ => Pic(0, 0, 2), 300));
        Check(still is { Changes: 0, Settles: 0, Unsettled: 0 }, $"가만한 화면(흔들림 ±2) 10초: 움직임 없음 — 전환 {still.Changes} · 움직이는 중 {still.Unsettled}프레임");

        var cut = Feed(Repeat(i => i < 60 ? Pic(0, 0, 2) : Pic(1, 0, 2), 150));
        Check(cut.Changes == 1 && cut.ChangeAt[0] >= 60 && cut.ChangeAt[0] <= 60 + 8 && cut.Unsettled is >= 3 and <= 8,
              $"컷(한 프레임에 다른 화면): 전환 {cut.Changes}번 · 프레임 {cut.ChangeAt.FirstOrDefault()} 에서 (바뀐 프레임 60) · 움직이는 중 {cut.Unsettled}프레임");

        // 회전: 60 프레임 가만 → 12 프레임 동안 한 프레임에 6칸씩 밀림(연속 큰 변화) → 다시 가만
        var pan = Feed(Repeat(i => i < 60 ? Pic(0, 0, 2) : i < 72 ? Pic(0, (i - 59) * 6, 2) : Pic(0, 72, 2), 160));
        Check(pan.Changes == 1 && pan.Unsettled >= 12 && pan.ChangeAt[0] > 72,
              $"회전(12프레임 동안 계속 밀림): 전환 {pan.Changes}번 · 프레임 {pan.ChangeAt.FirstOrDefault()} 에서 (끝난 프레임 72) · 움직이는 중 {pan.Unsettled}프레임");

        // 검은 끊김(test5 꼴): 21 프레임 검은 뒤 같은 화면 → 전환 아님, 움직이는 중으로는 본다 (그동안 추적기에 안 넣음)
        var blackSame = Feed(Repeat(i => i < 60 ? Pic(0, 0, 2) : i < 81 ? Black() : Pic(0, 0, 2), 160));
        Check(blackSame.Changes == 0 && blackSame.Settles == 1 && blackSame.Unsettled >= 21,
              $"검은 끊김 21프레임 뒤 같은 화면: 전환 {blackSame.Changes}번 · 안정 {blackSame.Settles}번 · 움직이는 중 {blackSame.Unsettled}프레임");
        var blackOther = Feed(Repeat(i => i < 60 ? Pic(0, 0, 2) : i < 81 ? Black() : Pic(1, 0, 2), 160));
        Check(blackOther.Changes == 1, $"검은 끊김 뒤 다른 화면: 전환 {blackOther.Changes}번");

        // 원인 표시(Blanked): 검은 끊김이 낀 구간은 true(화면엔 '화면 끊김'), 카메라가 도는 구간은 false('화면 이동 중')
        {
            bool blackBlanked = true, panBlanked = false, blackSettleBlanked = false, panSettleBlanked = true;
            var dBlack = new SceneChangeDetector();
            var dPan = new SceneChangeDetector();
            for (int i = 0; i < 160; i++)
            {
                var sb = dBlack.Update(i < 60 ? Pic(0, 0, 2) : i < 81 ? Black() : Pic(0, 0, 2), i * 1000 / 30.0);
                if (sb.Unsettled) blackBlanked &= sb.Blanked;
                if (sb.Settled) blackSettleBlanked = sb.Blanked;
                var sp = dPan.Update(i < 60 ? Pic(0, 0, 2) : i < 72 ? Pic(0, (i - 59) * 6, 2) : Pic(0, 72, 2), i * 1000 / 30.0);
                if (sp.Unsettled) panBlanked |= sp.Blanked;
                if (sp.Settled) panSettleBlanked = sp.Blanked;
            }
            Check(blackBlanked && blackSettleBlanked && !panBlanked && !panSettleBlanked,
                  $"원인 표시: 검은 끊김 구간 Blanked(움직이는 중 {blackBlanked} · 안정 {blackSettleBlanked}), 회전 구간은 아님({panBlanked} · {panSettleBlanked})");
        }

        // 지나가는 큰 차(한 프레임만 크게 튐) 뒤 같은 화면 → 전환 아님
        var glitch = Feed(Repeat(i => i == 60 ? Pic(1, 0, 2) : Pic(0, 0, 2), 150));
        Check(glitch.Changes == 0 && glitch.Settles == 1, $"한 프레임만 크게 튀고 같은 화면: 전환 {glitch.Changes}번 · 안정 {glitch.Settles}번");

        // 느린 변화(해 뜨는 것·자동 노출): 밝기가 서서히 1.5배 — 기준이 따라가므로 전환 아님
        var drift = Feed(Repeat(i => Pic(0, 0, 2, 1 + 0.5 * i / 599.0), 600));
        Check(drift is { Changes: 0, Settles: 0, Unsettled: 0 }, $"느린 밝기 변화(20초에 1.5배): 전환 {drift.Changes} · 움직이는 중 {drift.Unsettled}프레임");

        // 계속 흔들려 안정이 안 됨: 프레임마다 다른 그림 → 5초에 포기하고 전환으로 침 (그 뒤에도 5초마다)
        var shaking = Feed(Repeat(i => Pic(i % 2, i * 5, 0), 450));
        Check(shaking.Changes >= 2 && shaking.ChangeAt[0] is >= 148 and <= 152, $"계속 흔들림 15초: 5초(150프레임)마다 포기하고 전환 처리 — 전환 {shaking.Changes}번, 첫 번째 프레임 {shaking.ChangeAt.FirstOrDefault()}");

        // 검은 화면이 계속됨: 5초에 포기하고 전환으로 침 (기준은 버림)
        var dead = Feed(Repeat(i => i < 30 ? Pic(0, 0, 2) : Black(), 240));
        Check(dead.Changes >= 1 && dead.ChangeAt[0] is >= 178 and <= 182, $"검은 화면 8초: 5초 뒤 포기하고 전환 처리 — 프레임 {dead.ChangeAt.FirstOrDefault()}");

        // Reset(시크): 앞뒤 화면이 다르지만 Reset 하면 전환이 아니다. 움직이는 중이던 상태도 버린다
        var det = new SceneChangeDetector();
        for (int i = 0; i < 30; i++) det.Update(Pic(0, 0, 2), i * 33.3);
        det.Update(Pic(1, 0, 2), 1000);   // 움직임 시작
        bool wasUnsettled = det.Unsettled;
        det.Reset();
        int resetChanges = 0; bool anyUnsettled = false;
        for (int i = 0; i < 60; i++)
        {
            var s = det.Update(Pic(1, 0, 2), 1100 + i * 33.3);
            if (s.ViewChanged) resetChanges++;
            anyUnsettled |= s.Unsettled;
        }
        Check(wasUnsettled && !det.Unsettled && resetChanges == 0 && !anyUnsettled, $"Reset(시크): 움직이는 중이던 상태를 버리고 새 화면을 그대로 기준으로 — 전환 {resetChanges}번");

        // 왜 전환에서 추적기를 비우나: 멈춘 트랙은 10초 남아서, 움직이는 동안 추적기에 안 넣어도(앱) 전환 뒤 같은 자리에 나타난 다른 차가 그 번호를 물려받는다.
        // UI 테스트(Test-SceneChange)는 test4 의 회전 뒤 화면에 옛 트럭 자리의 검출이 없어 이 비우기가 빠져도 못 잡는다 — 그래서 추적기 단위로 가른다
        {
            Det Car() => new(400, 300, 80, 40, "car", 0.9);
            int Run3s(Tracker t) { int id = 0; for (int i = 0; i < 90; i++) id = t.UpdateTagged([Car()], i * 1000 / 30.0, W, H)[0].TrackId; return id; }
            var keep = new Tracker();
            var fresh = new Tracker();
            int before = Run3s(keep);
            Run3s(fresh);
            fresh.Reset();   // 전환 → 앱이 하는 일
            // 움직이는 동안(1.2초)은 둘 다 넣지 않고, 안정된 뒤 같은 자리에 다른 차가 나타난다
            int inherited = keep.UpdateTagged([Car()], 4200, W, H)[0].TrackId;
            int renumbered = fresh.UpdateTagged([Car()], 4200, W, H)[0].TrackId;
            Check(inherited == before && renumbered != before,
                  $"전환 뒤 같은 자리의 다른 차: 비우지 않으면 옛 번호 #{inherited} 를 물려받고(옛 #{before}), 비우면 새 번호 #{renumbered}");
        }

        // 프레임 간격이 다른 소스: 10fps 카메라·60fps 파일에서도 같다
        var cut10 = Feed(Repeat(i => i < 20 ? Pic(0, 0, 2) : Pic(1, 0, 2), 60), fps: 10);
        var cut60 = Feed(Repeat(i => i < 120 ? Pic(0, 0, 2) : Pic(1, 0, 2), 300), fps: 60);
        Check(cut10.Changes == 1 && cut60.Changes == 1, $"프레임 간격이 달라도 컷 하나는 전환 하나: 10fps {cut10.Changes}번 · 60fps {cut60.Changes}번");
    }

    // ⑩ 구역 진입·나감 이벤트 (2026-11-03): ZoneEvents.cs — 추적기를 거친 박스(발 위치)로. 앱의 로그·CSV 화면은 UI 테스트 Test-ZoneEvents.ps1 이 본다
    {
        // 구역 (프레임 0~1): 차로 Z1 · 갓길 Z2 · 차로 안의 정지선·대기 Z3 · 차로 위쪽 모서리를 덮는 무시 Z4
        ZoneShape Rect(int id, RoiKind k, double x0, double y0, double x1, double y1) =>
            new(id, k, [new(x0, y0), new(x1, y0), new(x1, y1), new(x0, y1)]);
        ZoneShape[] zs =
        [
            Rect(1, RoiKind.Lane, 0.30, 0.40, 0.70, 0.90),
            Rect(2, RoiKind.Shoulder, 0.80, 0.40, 1.00, 0.90),
            Rect(3, RoiKind.Stopline, 0.40, 0.80, 0.60, 0.85),
            Rect(4, RoiKind.Ignore, 0.30, 0.40, 0.50, 0.50),
        ];
        // 발 위치 (fx, fy)(프레임 0~1)가 되도록 놓은 80×40 박스 — 발 = 박스 아래 가운데
        Det Car(int id, double fx, double fy, string label = "car") =>
            new Det(fx * W - 40, fy * H - 40, 80, 40, label, 0.9) with { TrackId = id };
        // 30fps 로 frames 프레임: make(i) 가 그 프레임의 박스들. 이벤트를 모아 돌려준다 (시각 ms 와 함께)
        List<ZoneEvent> ZeRun(Func<int, Det[]> make, int frames, ZoneShape[]? zones = null, ZoneEventDetector? det = null, int from = 0)
        {
            det ??= new ZoneEventDetector();
            var all = new List<ZoneEvent>();
            for (int i = from; i < from + frames; i++) all.AddRange(det.Update(make(i), zones ?? zs, i * 1000 / 30.0, W, H));
            return all;
        }
        string Ev(ZoneEvent e) => $"{e.CsvEvent} #{e.TrackId} Z{e.ZoneId} @{e.Ms:0}ms" + (e.Kind == ZoneEventKind.Exit ? $" 머문 {e.DwellMs:0}ms" : "");

        // 1) 차로를 가로질러 갓길로: x 0.1 → 0.9 (6초), y 0.7. Z1 은 x 0.3~0.7 (1.5~4.5초), Z2 는 x 0.8~ (5.25초). Z4(무시)는 y 0.4~0.5 라 못 닿는다
        var cross = ZeRun(i => [Car(1, 0.1 + 0.8 * i / 180.0, 0.7)], 200);
        var c = cross.Select(e => (e.Kind, e.ZoneId)).ToList();
        bool orderOk = c.SequenceEqual([(ZoneEventKind.Enter, 1), (ZoneEventKind.Exit, 1), (ZoneEventKind.Enter, 2)]);
        Check(orderOk && Math.Abs(cross[0].Ms - 1500) <= 40 && Math.Abs(cross[1].Ms - 4500) <= 40 && Math.Abs(cross[1].DwellMs - 3000) <= 60
              && Math.Abs(cross[2].Ms - 5250) <= 40 && cross.All(e => !e.Initial && !e.Lost),
              $"차로를 가로지름: {string.Join(" · ", cross.Select(Ev))} (손계산 1500 · 4500 머문 3000 · 5250)");

        // 2) 경계 흔들림: 안에 100ms(3프레임)만 걸쳤다 빠지면 진입이 아니고, 확정된 뒤 300ms 밖에 있다 돌아오면 나감이 아니다
        var grazed = ZeRun(i => [Car(1, i is >= 60 and < 63 ? 0.32 : 0.25, 0.7)], 150);
        var wobble = ZeRun(i => [Car(1, i is >= 120 and < 129 ? 0.75 : 0.5, 0.7)], 200);   // 안에 4초 → 밖에 0.3초 → 다시 안
        Check(grazed.Count == 0 && wobble.Count(e => e.Kind == ZoneEventKind.Exit) == 0 && wobble.Count(e => e.Kind == ZoneEventKind.Enter) == 1,
              $"경계 흔들림: 3프레임 걸침 → 이벤트 {grazed.Count}건 · 0.3초 나갔다 돌아옴 → 진입 {wobble.Count(e => e.Kind == ZoneEventKind.Enter)} 나감 {wobble.Count(e => e.Kind == ZoneEventKind.Exit)}");
        var left = ZeRun(i => [Car(1, i < 60 ? 0.5 : 0.75, 0.7)], 150);   // 2초 안 → 밖에서 계속
        Check(left.Count(e => e.Kind == ZoneEventKind.Exit && e.ZoneId == 1) == 1 && Math.Abs(left.First(e => e.Kind == ZoneEventKind.Exit).Ms - 2000) <= 40,
              $"0.5초 넘게 밖에 있으면 나감, 시각은 나간 때: {string.Join(" · ", left.Select(Ev))}");

        // 3) 추적이 끊김: 안에 있다 1초 가려졌다 같은 번호로 돌아오면 이벤트 없음 / 그대로 사라지면 마지막으로 본 때로 '나감(추적 끝)'
        var occluded = ZeRun(i => i is >= 60 and < 90 ? [] : [Car(1, 0.5, 0.7)], 200);
        var gone = ZeRun(i => i < 60 ? [Car(1, 0.5, 0.7)] : [], 200);
        var lost = gone.FirstOrDefault(e => e.Kind == ZoneEventKind.Exit);
        Check(occluded.Count(e => e.Kind == ZoneEventKind.Exit) == 0 && lost is { Lost: true } && Math.Abs(lost.Ms - 59 * 1000 / 30.0) <= 40,
              $"추적 끊김: 1초 가림 → 나감 {occluded.Count(e => e.Kind == ZoneEventKind.Exit)}건, 사라짐 → {(lost == null ? "없음" : Ev(lost) + " 추적 끝")}");

        // 4) 무시 구역: 발이 무시 Z4 안이면(차로 Z1 과 겹친 자리) Z1 도 이벤트를 안 낸다. 무시 구역 자체는 이벤트가 없다
        var ignored = ZeRun(i => [Car(1, 0.4, 0.45)], 120);
        var leaveIgnore = ZeRun(i => [Car(1, i < 60 ? 0.4 : 0.6, i < 60 ? 0.45 : 0.6)], 150);   // 무시 구역에서 차로로 → 진입은 차로로 들어온 때
        Check(ignored.Count == 0 && leaveIgnore.Count(e => e.Kind == ZoneEventKind.Enter && e.ZoneId == 1) == 1 && Math.Abs(leaveIgnore[0].Ms - 2000) <= 40 && leaveIgnore.All(e => e.ZoneId != 4),
              $"무시 구역: 안에서는 이벤트 {ignored.Count}건, 벗어나 차로로 가면 그때 진입: {string.Join(" · ", leaveIgnore.Select(Ev))}");

        // 5) 겹친 구역: 차로 Z1 과 그 안의 정지선·대기 Z3 에 동시에 들어오면 둘 다 센다
        var both = ZeRun(i => [Car(1, 0.5, 0.82)], 60);
        Check(both.Count == 2 && both.Select(e => e.ZoneId).OrderBy(x => x).SequenceEqual([1, 3]), $"겹친 구역: {string.Join(" · ", both.Select(Ev))}");

        // 6) 처음부터 안에 있던 물체는 '이미 안에 있음'(Initial), 나중에 들어온 물체는 진입. Reset 뒤에도 다시 Initial
        var det6 = new ZoneEventDetector();
        var start = ZeRun(i => i < 60 ? [Car(1, 0.5, 0.7)] : [Car(1, 0.5, 0.7), Car(2, 0.5, 0.65)], 120, det: det6);
        det6.Reset();
        var afterReset = ZeRun(i => [Car(1, 0.5, 0.7)], 30, det: det6, from: 200);
        Check(start.Count == 2 && start[0] is { TrackId: 1, Initial: true } && start[1] is { TrackId: 2, Initial: false } && afterReset.Count == 1 && afterReset[0].Initial,
              $"처음부터 안 / 나중에 들어옴 / Reset 뒤 다시 '이미 안': {string.Join(" · ", start.Concat(afterReset).Select(e => Ev(e) + (e.Initial ? "(이미 안)" : "")))}");

        // 7) Suspend(화면 전환): 표시 이벤트를 한 번 내고 Reset 까지 아무것도 안 낸다 (안에 있던 물체의 '나감'도 기록하지 않는다)
        var det7 = new ZoneEventDetector();
        ZeRun(i => [Car(1, 0.5, 0.7)], 60, det: det7);
        var marker = det7.Suspend(2000);
        var again = det7.Suspend(2100);
        var whileSuspended = ZeRun(i => [Car(1, 0.5, 0.7), Car(2, 0.75, 0.7)], 90, det: det7, from: 60);
        det7.Reset();
        var resumed = ZeRun(i => [Car(1, 0.5, 0.7)], 30, det: det7, from: 200);
        Check(marker is { Kind: ZoneEventKind.ViewChanged } && again == null && det7 != null && whileSuspended.Count == 0 && resumed.Count == 1,
              $"화면 전환: 표시 {marker?.CsvEvent} · 두 번째 Suspend {(again == null ? "없음" : "있음")} · 멈춘 동안 이벤트 {whileSuspended.Count}건 · Reset 뒤 {resumed.Count}건");

        // 8) 번호 없는 박스(TrackId 0 — 움직이는 동안·SAHI)는 무시하고, 한 물체가 오래 서 있어도 이벤트는 한 번뿐이다
        var untracked = ZeRun(i => [new Det(0.5 * W - 40, 0.7 * H - 40, 80, 40, "car", 0.9)], 60);
        var stoodLong = ZeRun(i => [Car(1, 0.5, 0.7)], 900);   // 30초
        Check(untracked.Count == 0 && stoodLong.Count == 1, $"번호 없는 박스 {untracked.Count}건 · 30초 서 있는 차 {stoodLong.Count}건");

        // 9) 구역을 지우면 상태를 조용히 버린다 (나감을 만들지 않음). 구역이 없으면 아무 일도 없다
        var det9 = new ZoneEventDetector();
        ZeRun(i => [Car(1, 0.5, 0.7)], 60, det: det9);
        var removed = ZeRun(i => [Car(1, 0.5, 0.7)], 60, zones: [zs[1], zs[2], zs[3]], det: det9, from: 60);   // Z1 이 지워짐
        var noZones = ZeRun(i => [Car(1, 0.5, 0.7)], 30, zones: [], det: det9, from: 120);
        Check(removed.Count(e => e.ZoneId == 1) == 0 && noZones.Count == 0, $"구역을 지움: Z1 이벤트 {removed.Count(e => e.ZoneId == 1)}건 · 구역 없음 {noZones.Count}건");
    }

    // ⑪ 유고 규칙 (2026-11-03): IncidentRules.cs — 차로에 멈춘 차량이 정지로 5초 이어지면 유고 후보. 정지(Det.Stationary)는 추적기가 정하므로 여기선 값으로 준다
    {
        ZoneShape Rc(int id, RoiKind k, double x0, double y0, double x1, double y1) => new(id, k, [new(x0, y0), new(x1, y0), new(x1, y1), new(x0, y1)]);
        ZoneShape[] zs = [Rc(1, RoiKind.Lane, 0.30, 0.40, 0.70, 0.90), Rc(2, RoiKind.Shoulder, 0.80, 0.40, 1.00, 0.90), Rc(3, RoiKind.Stopline, 0.40, 0.80, 0.60, 0.85), Rc(4, RoiKind.Ignore, 0.30, 0.40, 0.50, 0.50)];
        Det V(int id, double fx, double fy, bool still = true, string label = "car") =>
            new Det(fx * W - 40, fy * H - 40, 80, 40, label, 0.9) with { TrackId = id, Stationary = still };
        // 30fps 로 frames 프레임. make(i) 가 그 프레임의 박스들. 이벤트를 모은다
        List<ZoneEvent> IncRun(Func<int, Det[]> make, int frames, IncidentDetector det, ZoneShape[]? zones = null, int from = 0)
        {
            var all = new List<ZoneEvent>();
            for (int i = from; i < from + frames; i++) all.AddRange(det.Update(make(i), zones ?? zs, i * 1000 / 30.0, W, H));
            return all;
        }
        string Ie(ZoneEvent e) => $"{e.CsvEvent} #{e.TrackId} Z{e.ZoneId} @{e.Ms:0}ms" + (e.Kind == ZoneEventKind.IncidentEnd ? $" 멈춤 {e.DwellMs:0}ms{(e.Note.Length > 0 ? " (" + e.Note + ")" : "")}" : "");
        bool Near(double a, double b, double tol) => Math.Abs(a - b) <= tol;

        // 1) 차로에 혼자 멈춘 차: 8초 멈춤 뒤 움직임 → 5초에 유고 후보, 움직임을 멈춘 1초 뒤에 해소 (시각은 마지막으로 멈춰 있던 때)
        var d1 = new IncidentDetector();
        var one = IncRun(i => [V(1, 0.5, 0.7, still: i < 240)], 330, d1);
        Check(one.Count == 2 && one[0] is { Kind: ZoneEventKind.Incident } && Near(one[0].Ms, 5000, 40) && one[1] is { Kind: ZoneEventKind.IncidentEnd } && Near(one[1].Ms, 239 * 1000 / 30.0, 40) && Near(one[1].DwellMs, 7967, 80) && d1.ActiveCount == 0,
              $"차로에 혼자 멈춘 차: {string.Join(" · ", one.Select(Ie))} (손계산 5000 · 멈춤 끝 7967)");

        // 2) 후보가 아닌 것들: 갓길·차로 밖 · 정지선·대기 구역 안 · 무시 구역 안 · 사람 · 움직이는 차 · 구역 없음
        int Count(Func<int, Det[]> make, ZoneShape[]? zones = null) => IncRun(make, 300, new IncidentDetector(), zones).Count;
        int shoulder = Count(i => [V(1, 0.9, 0.7)]);
        int stopline = Count(i => [V(1, 0.5, 0.82)]);
        int ignore = Count(i => [V(1, 0.4, 0.45)]);
        int person = Count(i => [V(1, 0.5, 0.7, label: "person")]);
        int moving = Count(i => [V(1, 0.5, 0.7, still: false)]);
        int noZones = Count(i => [V(1, 0.5, 0.7)], zones: []);
        int onlyShoulderZone = Count(i => [V(1, 0.5, 0.7)], zones: [zs[1]]);
        Check(shoulder == 0 && stopline == 0 && ignore == 0 && person == 0 && moving == 0 && noZones == 0 && onlyShoulderZone == 0,
              $"후보 아님: 갓길 {shoulder} · 정지선·대기 안 {stopline} · 무시 안 {ignore} · 사람 {person} · 움직이는 차 {moving} · 구역 없음 {noZones} · 차로 구역 없음 {onlyShoulderZone} (모두 0 이어야)");

        // 3) 줄지어 선 정체: 같은 차로 구역에 정지한 차 4대(서로 자신 말고 3대 이상)는 후보가 아니다. 단 이미 선언한 뒤 뒤로 차가 밀려도 풀지 않는다
        var queue = IncRun(i => [V(1, 0.40, 0.55), V(2, 0.45, 0.60), V(3, 0.50, 0.65), V(4, 0.55, 0.70)], 300, new IncidentDetector());
        var d3 = new IncidentDetector();
        var behind = IncRun(i => i < 240 ? [V(1, 0.5, 0.7)] : [V(1, 0.5, 0.7), V(2, 0.45, 0.62), V(3, 0.5, 0.62), V(4, 0.55, 0.62)], 480, d3);
        Check(queue.Count == 0 && behind.Count == 1 && behind[0].Kind == ZoneEventKind.Incident && d3.ActiveCount == 1,
              $"정체: 정지한 4대 → 후보 {queue.Count}건 · 먼저 서 있다 뒤로 차가 밀림 → {string.Join(" · ", behind.Select(Ie))}, 지금도 후보 {d3.ActiveCount}건");
        var queue2 = IncRun(i => [V(1, 0.40, 0.55), V(2, 0.45, 0.60), V(3, 0.50, 0.65)], 300, new IncidentDetector());   // 3대는 정체가 아니다(자신 말고 2대)
        Check(queue2.Count == 3 && queue2.All(e => e.Kind == ZoneEventKind.Incident), $"정지 차량 3대(자신 말고 2대)는 정체가 아니다 → 후보 {queue2.Count}건");

        // 4) 정지 판정의 흔들림: 0.5초 끊겨도 이어짐(5초에 선언) / 1.5초 끊기면 처음부터 다시 센다(끊긴 뒤 3.5초에 다시 시작해 8.5초에 선언)
        var brief = IncRun(i => [V(1, 0.5, 0.7, still: i is < 90 or >= 105)], 300, new IncidentDetector());
        var longGap = IncRun(i => [V(1, 0.5, 0.7, still: i is < 60 or >= 105)], 400, new IncidentDetector());
        Check(brief.Count == 1 && Near(brief[0].Ms, 5000, 40) && longGap.Count == 1 && Near(longGap[0].Ms, 105 * 1000 / 30.0 + 5000, 80),
              $"정지 판정 흔들림: 0.5초 끊김 → {string.Join(" · ", brief.Select(Ie))} / 1.5초 끊김 → {string.Join(" · ", longGap.Select(Ie))} (손계산 5000 · 8500)");

        // 5) 추적이 끊김: 선언한 뒤 1초 가려졌다 돌아오면 이어지고, 그대로 사라지면 마지막으로 본 때로 '추적 끝' 해소
        var occl = IncRun(i => i is >= 240 and < 270 ? [] : [V(1, 0.5, 0.7)], 400, new IncidentDetector());
        var gone = IncRun(i => i < 240 ? [V(1, 0.5, 0.7)] : [], 400, new IncidentDetector());
        Check(occl.Count == 1 && gone.Count == 2 && gone[1] is { Kind: ZoneEventKind.IncidentEnd, Lost: true, Note: "추적 끝" } && Near(gone[1].Ms, 239 * 1000 / 30.0, 40),
              $"추적 끊김: 1초 가림 → {occl.Count}건 · 사라짐 → {string.Join(" · ", gone.Select(Ie))}");

        // 6) 차량 둘은 따로, Suspend(화면 전환)·Reset(시크)은 선언돼 있던 것을 끝내는 이벤트를 내고 상태를 비운다
        var d6 = new IncidentDetector();
        var two = IncRun(i => [V(1, 0.5, 0.7), V(2, 0.6, 0.65, label: "truck")], 200, d6);
        int active = d6.ActiveCount;
        bool flagged = d6.IsActive(1) && d6.IsActive(2) && !d6.IsActive(3);
        var sus = d6.Suspend();
        var whileSus = IncRun(i => [V(1, 0.5, 0.7)], 300, d6, from: 200);
        var rst = d6.Reset();
        var rst2 = d6.Reset();
        var after = IncRun(i => [V(1, 0.5, 0.7)], 200, d6, from: 500);
        Check(two.Count == 2 && active == 2 && flagged && sus.Count == 2 && sus.All(e => e is { Kind: ZoneEventKind.IncidentEnd, Note: "화면 전환" }) && whileSus.Count == 0 && rst.Count == 0 && rst2.Count == 0 && after.Count == 1,
              $"차량 둘: 후보 {two.Count}건·활성 {active}·번호별 판정 {flagged} → Suspend {sus.Count}건 종료 · 멈춘 동안 {whileSus.Count}건 · Reset 뒤 다시 {after.Count}건");
        var d6b = new IncidentDetector();
        IncRun(i => [V(1, 0.5, 0.7)], 200, d6b);
        var rstEnd = d6b.Reset();
        Check(rstEnd.Count == 1 && rstEnd[0] is { Kind: ZoneEventKind.IncidentEnd } && rstEnd[0].Note.Length > 0 && d6b.ActiveCount == 0, $"Reset(시크): 선언돼 있던 후보를 끝내는 이벤트 {rstEnd.Count}건 ({rstEnd.FirstOrDefault()?.Note})");

        // 7) 구역이 사라지면(지움) 끝낸다
        var d7 = new IncidentDetector();
        IncRun(i => [V(1, 0.5, 0.7)], 200, d7);
        var cleared = IncRun(i => [V(1, 0.5, 0.7)], 10, d7, zones: [], from: 200);
        Check(cleared.Count == 1 && cleared[0] is { Kind: ZoneEventKind.IncidentEnd, Note: "구역 없음" } && d7.ActiveCount == 0, $"구역이 없어짐: {string.Join(" · ", cleared.Select(Ie))}");
    }

    // 한계 (통과·실패 없이 보이기만): 박스만으로는 움직임을 잴 수 없거나 나가는 차와 구분되지 않는 경우
    {
        var scene = NewScene(3000);
        Add(scene, W, H, -10, 300, W + 20, 100, 0, 0, 0, 3000);
        Console.WriteLine($"[INFO] 처음부터 가로를 다 덮은 채 멈춘 박스: {Stat(Run(scene, W, H, frame: true))} — 그 축은 잴 수 없어 정지로 판정되지 않음 " +
                          $"(예전 판정 {Stat(Run(scene, W, H, frame: false))})");
    }
    {
        // 왼쪽 테두리에 걸쳐 멈춘 차가 안쪽(오른쪽)부터 가려짐 (1.5~1.8초에 폭 40→20, 2.8~3.1초에 돌아옴) — 박스가 왼쪽으로 나가는 차와 같다
        static double Occlude(double ms) => ms < 1500 ? 0 : ms < 1800 ? 20 * (ms - 1500) / 300 : ms < 2800 ? 20 : ms < 3100 ? 20 * (3100 - ms) / 300 : 0;
        var scene = NewScene(4500);
        Add(scene, W, H, -40, 400, 80, 40, 0, 0, 0, 4500, Occlude);
        var after = Run(scene, W, H, frame: true).Where(f => f.Ms >= 1000).ToList();
        var oldAfter = Run(scene, W, H, frame: false).Where(f => f.Ms >= 1000).ToList();
        Console.WriteLine($"[INFO] 왼쪽 테두리에 걸쳐 멈춘 차가 안쪽부터 가려짐: 1초 뒤 {Stat(after)} — 가려지는 동안 움직임으로 봄 (예전 판정 {Stat(oldAfter)})");
    }

    ViewsCheck.SelfTest(Check, RepoPaths.VideoDir);   // ⑫ 화면 식별 (2026-11-03)

    Console.WriteLine(fails == 0 ? "모두 통과" : $"실패 {fails}건");
    return fails;
}

// ---------- dedup: 본 앱 표시 경로의 중복 박스 필터(DuplicateBoxes) 전후 비교 → out/dedup.txt ----------
// ① 저장한 프레임 검출로 박스 수 전후와 뺀 박스의 종류 ② SAHI: 필터를 어디에 넣어야 병합(MergeNms) 결과에서 중복만 줄어드는지 —
// A 타일마다 필터 → 병합, B 병합 → 필터(본 앱이 쓰는 쪽). A 는 병합 입력이 바뀌어 중복 아닌 박스가 빠지거나 되살아나는 일이 있었다.
// ② 는 타일 검출을 GPU 로 다시 한다 (본 앱 RunSahi 와 같은 타일 640·겹침 0.3·병합 0.45/0.6). Every=N: N 프레임에 하나 (기본 15)
int Dedup(string[] opts)
{
    var report = new StringBuilder();
    void Log(string s) { Console.WriteLine(s); report.AppendLine(s); }
    static string Box(Det d) => string.Create(CultureInfo.InvariantCulture, $"{d.Label} {d.Conf:F2} ({d.X:F0},{d.Y:F0},{d.W:F0}×{d.H:F0})");

    Log("① 프레임 검출 (out/<영상>.dets.csv — 본 앱 ProcessFrame 과 같은 길)");
    foreach (var (name, _) in videos)
    {
        var frames = LoadDets(Path.Combine(outDir, name + ".dets.csv"));
        int before = 0, after = 0, dupFrames = 0, maxRemoved = 0;
        var kinds = new Dictionary<string, List<string>> { ["같은 라벨 IoU>0.7"] = [], ["같은 라벨 범위만 다름"] = [], ["차량 라벨 다름"] = [] };
        foreach (var (f, ms, dets) in frames)
        {
            var dupOf = DuplicateBoxes.FindDuplicates(dets);
            int removed = 0;
            for (int i = 0; i < dets.Count; i++)
            {
                if (dupOf[i] < 0) continue;
                removed++;
                var keep = dets[dupOf[i]];
                string kind = keep.Label != dets[i].Label ? "차량 라벨 다름" : Tracker.IoU(keep, dets[i]) > 0.7 ? "같은 라벨 IoU>0.7" : "같은 라벨 범위만 다름";
                kinds[kind].Add($"{ms / 1000,6:F2}초  남김 {Box(keep)}  뺌 {Box(dets[i])}  IoU {Tracker.IoU(keep, dets[i]):F2}");
            }
            before += dets.Count;
            after += dets.Count - removed;
            if (removed > 0) dupFrames++;
            maxRemoved = Math.Max(maxRemoved, removed);
        }
        Log($"{name}: 프레임 {frames.Count}장 · 박스 {before} → {after}개 (−{before - after}, {(double)(before - after) / before:P1}) · " +
            $"중복이 있던 프레임 {dupFrames}장 · 한 프레임에서 최대 {maxRemoved}개 뺌");
        foreach (var (kind, list) in kinds)
        {
            Log($"  {kind}: {list.Count}개 — 처음 몇 개:");
            foreach (var s in list.Take(4)) Log($"    {s}");
        }

        // 목표 정지 차량 자리의 박스 수 (프레임당) — 필터 뒤엔 1개여야 한다
        foreach (var t in targets.Where(t => t.Video == name))
        {
            var inRange = frames.Where(fr => fr.Ms >= t.FromMs && fr.Ms < t.ToMs).ToList();
            int Multi(Func<List<Det>, List<Det>> pick) => inRange.Count(fr => pick(fr.Dets).Count(d => Tracker.IoU(d, t.Box) >= 0.5) > 1);
            Log($"  {t.Name}: 그 자리에 박스가 둘 이상인 프레임 {Multi(d => d)}장 → {Multi(d => DuplicateBoxes.Remove(d))}장 (구간 {inRange.Count}장)");
        }
    }

    Log("");
    int every = (int)opts.Where(o => o.StartsWith("Every=")).Select(o => double.Parse(o[6..], CultureInfo.InvariantCulture)).DefaultIfEmpty(15).First();
    Log($"② SAHI 병합 (프레임 {every}장에 하나, 타일 검출은 GPU 로 다시)");
    using var yolo = OpenModel(Log);
    foreach (var (name, _) in videos)
    {
        using var cap = new VideoCapture(Path.Combine(RepoPaths.VideoDir, name + ".mp4"));
        using var frame = new Mat();
        int f = -1, n = 0, rawTotal = 0, oldTotal = 0, newTotal = 0, droppedDup = 0, droppedOther = 0, added = 0, leftDup = 0, leftDupFrames = 0, postTotal = 0;
        while (cap.Read(frame) && !frame.Empty())
        {
            if (++f % every != 0) continue;
            n++;
            double ms = cap.Get(VideoCaptureProperties.PosMsec);
            var raw = new List<Det>();
            var filtered = new List<Det>();
            var (tileW, tileH) = YoloLetterbox.InputSize(yolo);   // 본 앱 RunSahi 처럼 타일 = 모델 입력 크기 (2026-11-06 — 그 전엔 640 정사각형)
            int stepX = (int)(tileW * (1 - 0.3)), stepY = (int)(tileH * (1 - 0.3));
            foreach (int oy in Sahi.TileOrigins(frame.Height, tileH, stepY))
                foreach (int ox in Sahi.TileOrigins(frame.Width, tileW, stepX))
                {
                    using var roi = new Mat(frame, new Rect(ox, oy, Math.Min(tileW, frame.Width - ox), Math.Min(tileH, frame.Height - oy)));
                    var tile = DetectTile(yolo, roi, ox, oy);
                    raw.AddRange(tile);
                    filtered.AddRange(DuplicateBoxes.Remove(tile));
                }
            var oldMerged = Sahi.MergeNms(raw, 0.45, 0.6);
            var newMerged = Sahi.MergeNms(filtered, 0.45, 0.6);
            rawTotal += raw.Count;
            oldTotal += oldMerged.Count;
            newTotal += newMerged.Count;
            // A(타일마다 필터 → 병합): 빠진 박스는 모두 남은 박스의 중복이어야 하고, 새로 생긴 박스는 없어야 한다.
            // 병합 입력이 바뀌어 어긋날 수 있다 — 그러면 그 자리의 타일 검출까지 보고한다
            void Explain(Det d)
            {
                foreach (var r in raw.Where(r => DuplicateBoxes.Intersection(r, d) > 0).OrderByDescending(r => r.Conf))
                    Log($"      타일 검출 {Box(r)} — 필터 {(filtered.Contains(r) ? "남김" : "뺌")} · 병합 전 {(oldMerged.Contains(r) ? "남음" : "빠짐")} / 후 {(newMerged.Contains(r) ? "남음" : "빠짐")}");
            }
            foreach (var d in oldMerged.Where(d => !newMerged.Contains(d)))
            {
                if (newMerged.Any(k => DuplicateBoxes.IsDuplicate(k, d))) { droppedDup++; continue; }
                droppedOther++;
                Log($"  [A 빠짐·중복 아님] {name} {ms / 1000:F2}초 {Box(d)}");
                Explain(d);
            }
            foreach (var d in newMerged.Where(d => !oldMerged.Contains(d)))
            {
                added++;
                Log($"  [A 새로 생김] {name} {ms / 1000:F2}초 {Box(d)}");
                Explain(d);
            }
            // A 의 병합 뒤에도 남은 중복 — 다른 타일에서 나온 car/truck 처럼 MergeNms(같은 라벨만)가 못 묶는 것
            int left = DuplicateBoxes.FindDuplicates(newMerged).Count(x => x >= 0);
            leftDup += left;
            if (left > 0) leftDupFrames++;
            // B(병합 → 필터): 병합 결과는 그대로이고 그 위에서 중복만 뺀다 (뺀 박스는 정의상 남은 박스의 중복)
            postTotal += DuplicateBoxes.Remove(oldMerged).Count;
        }
        Log($"{name}: 프레임 {n}장 · 타일 검출 {rawTotal}개 · 병합만 {oldTotal}개");
        Log($"  A 타일마다 필터 → 병합: {newTotal}개 · 빠진 것 중 중복 {droppedDup} / 중복 아님 {droppedOther} · 새로 생김 {added} · 남은 중복 {leftDup}개({leftDupFrames}장)");
        Log($"  B 병합 → 필터: {postTotal}개 (−{oldTotal - postTotal}) · 남은 중복 0");
    }
    File.WriteAllText(Path.Combine(outDir, "dedup.txt"), report.ToString());
    return 0;
}

// ---------- sahi: SAHI 병합 방식 전후 비교 → out/sahi_<variant>.txt, 고른 장면은 out/sahi_<variant>_<영상>_<초>.png ----------
// 옛: 타일만 → MergeNms(지움) → DuplicateBoxes / 새(본 앱, 2026-10-30): 전체 프레임 + 타일 → MergeCutPieces(경계 조각만 합침) → DuplicateBoxes.
// Variant=nmm 이면 새 쪽을 기각한 원본 SAHI 방식(MergeNmm, 모두 합침)으로 바꿔 본다.
// 새 박스마다 같은 라벨 옛 박스와 IoU 로 짝지어 같음(≥0.95)·바뀜(0.5~0.95)·새로 생김을, 짝 없는 옛 박스는 사라짐으로 센다.
// 그림은 옛 박스 빨강(가는 선)·새 박스 초록(굵은 선). 장면은 "영상@초" 로 고르고, 안 고르면 영상마다 차이가 가장 큰 3장
int SahiCompare(string[] opts)
{
    var report = new StringBuilder();
    void Log(string s) { Console.WriteLine(s); report.AppendLine(s); }
    static string Box(Det d) => string.Create(CultureInfo.InvariantCulture, $"{d.Label} {d.Conf:F2} ({d.X:F0},{d.Y:F0},{d.W:F0}×{d.H:F0})");
    int every = (int)opts.Where(o => o.StartsWith("Every=")).Select(o => double.Parse(o[6..], CultureInfo.InvariantCulture)).DefaultIfEmpty(15).First();
    var picks = opts.Where(o => o.Contains('@')).Select(o => o.Split('@'))
                    .Select(a => (Video: a[0], Sec: double.Parse(a[1], CultureInfo.InvariantCulture))).ToList();
    string variant = opts.Contains("Variant=nmm") ? "nmm" : "cut";   // 기본은 본 앱과 같은 cut, nmm 은 기각한 원본 SAHI 방식
    string newDesc = variant == "cut" ? "MergeCutPieces(경계 조각만 합침)" : "MergeNmm(모두 합침)";
    Log($"SAHI 병합 전후 (프레임 {every}장에 하나 · 옛: 타일 → MergeNms → 중복 필터 / 새: 전체 프레임 + 타일 → {newDesc} → 중복 필터)");
    using var yolo = OpenModel(Log);
    foreach (var (name, _) in videos)
    {
        using var cap = new VideoCapture(Path.Combine(RepoPaths.VideoDir, name + ".mp4"));
        using var frame = new Mat();
        int f = -1, n = 0, oldTotal = 0, newTotal = 0, same = 0, changed = 0, grown = 0, added = 0, removed = 0;
        var diffs = new List<(double Sec, double Score, string Text)>();
        while (cap.Read(frame) && !frame.Empty())
        {
            if (++f % every != 0) continue;
            n++;
            double ms = cap.Get(VideoCaptureProperties.PosMsec);
            var (oldList, newList, _) = SahiBoth(yolo, frame, variant);
            oldTotal += oldList.Count;
            newTotal += newList.Count;
            double score = 0;
            var lines = new List<string>();
            var matchedOld = new HashSet<int>();
            foreach (var nb in newList)
            {
                int best = -1;
                double bestIou = 0;
                for (int i = 0; i < oldList.Count; i++)
                {
                    if (oldList[i].Label != nb.Label) continue;
                    double iou = BoxIoU(oldList[i], nb);
                    if (iou > bestIou) { bestIou = iou; best = i; }
                }
                if (bestIou >= 0.95) { same++; matchedOld.Add(best); continue; }
                if (bestIou >= 0.5)
                {
                    changed++;
                    matchedOld.Add(best);
                    double ratio = nb.W * nb.H / (oldList[best].W * oldList[best].H);
                    if (ratio > 1.2) grown++;
                    score += 1 - bestIou;
                    lines.Add($"바뀜 {Box(oldList[best])} → {Box(nb)} (IoU {bestIou:F2}, 넓이 ×{ratio:F2})");
                    continue;
                }
                added++;
                score += 1;
                lines.Add($"새로 생김 {Box(nb)}");
            }
            for (int i = 0; i < oldList.Count; i++)
                if (!matchedOld.Contains(i)) { removed++; score += 1; lines.Add($"사라짐 {Box(oldList[i])}"); }
            if (lines.Count > 0) diffs.Add((ms / 1000, score, string.Join(" / ", lines)));
        }
        Log($"{name}: 프레임 {n}장 · 옛 {oldTotal}개 → 새 {newTotal}개 · 같음 {same} · 바뀜 {changed}(넓어짐 {grown}) · 새로 생김 {added} · 사라짐 {removed} · 차이 난 프레임 {diffs.Count}장");
        foreach (var d in diffs.OrderByDescending(d => d.Score).Take(12))
            Log(string.Create(CultureInfo.InvariantCulture, $"  {d.Sec:F2}초: {d.Text}"));
        var secs = picks.Where(p => p.Video == name).Select(p => p.Sec).ToList();
        if (secs.Count == 0) secs = diffs.OrderByDescending(d => d.Score).Take(3).Select(d => d.Sec).ToList();
        foreach (double sec in secs) DrawSahiCompare(yolo, name, sec, variant);
    }
    File.WriteAllText(Path.Combine(outDir, $"sahi_{variant}.txt"), report.ToString());
    return 0;
}

// 한 프레임에 옛·새 SAHI 를 둘 다 돌린다 (본 앱 RunSahi 와 같은 타일 640·겹침 0.3·묶음 0.45/0.6).
// 새 쪽은 variant 로 고른다: "nmm" = 전체 + 타일 → MergeNmm(모두 합집합), "cut" = 전체 + 타일 → MergeCutPieces(경계 조각만 합집합).
// Sources: 병합 전 입력 (어디서 나왔는지 — "전체" 또는 타일 원점, 경계에 잘렸는지) — 새 박스가 왜 그렇게 생겼는지 풀어 보이는 데 쓴다
(List<Det> Old, List<Det> New, List<(Det D, string From)> Sources) SahiBoth(Yolo yolo, Mat frame, string variant)
{
    var (tileW, tileH) = YoloLetterbox.InputSize(yolo);   // 본 앱 RunSahi 처럼 타일 = 모델 입력 크기 (2026-11-06 — 그 전엔 640 정사각형)
    int stepX = (int)(tileW * (1 - 0.3)), stepY = (int)(tileH * (1 - 0.3));
    var tiles = new List<(Det D, bool Cut)>();
    var sources = new List<(Det D, string From)>();
    foreach (int oy in Sahi.TileOrigins(frame.Height, tileH, stepY))
        foreach (int ox in Sahi.TileOrigins(frame.Width, tileW, stepX))
        {
            int tw = Math.Min(tileW, frame.Width - ox), th = Math.Min(tileH, frame.Height - oy);
            using var roi = new Mat(frame, new Rect(ox, oy, tw, th));
            foreach (var d in DetectTile(yolo, roi, ox, oy))
            {
                bool cut = Sahi.TouchesInnerEdge(d, ox, oy, tw, th, frame.Width, frame.Height);
                tiles.Add((d, cut));
                sources.Add((d, $"타일({ox},{oy}){(cut ? " 잘림" : "")}"));
            }
        }
    List<Det> full = frame.Width > tileW || frame.Height > tileH ? DetectTile(yolo, frame, 0, 0) : new List<Det>();
    sources.AddRange(full.Select(d => (d, "전체")));
    var oldList = DuplicateBoxes.Remove(Sahi.MergeNms(tiles.Select(t => t.D).ToList(), 0.45, 0.6));
    var newList = variant == "cut"
        ? DuplicateBoxes.Remove(Sahi.MergeCutPieces([.. full.Select(d => (d, false)), .. tiles], 0.45, 0.6))
        : DuplicateBoxes.Remove(Sahi.MergeNmm([.. full, .. tiles.Select(t => t.D)], 0.45, 0.6));
    return (oldList, newList, sources);
}

// 그 장면의 옛(빨강)·새(초록) SAHI 결과를 그려 PNG 로
void DrawSahiCompare(Yolo yolo, string name, double sec, string variant)
{
    using var cap = new VideoCapture(Path.Combine(RepoPaths.VideoDir, name + ".mp4"));
    cap.Set(VideoCaptureProperties.PosFrames, Math.Round(sec * cap.Get(VideoCaptureProperties.Fps)));   // mp4 는 PosFrames 시크가 정확하다(CLAUDE.md)
    using var img = new Mat();
    if (!cap.Read(img) || img.Empty()) return;
    var (oldList, newList, sources) = SahiBoth(yolo, img, variant);
    // 옛 박스와 거의 같지 않은 새 박스는 무엇을 합쳐 생겼는지 (같은 라벨, 그 박스 안에 90% 이상 들어간 입력) 보인다
    foreach (var nb in newList.Where(nb => !oldList.Any(o => o.Label == nb.Label && BoxIoU(o, nb) >= 0.95)))
    {
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  {name} {sec:F2}s 새 {nb.Label} {nb.Conf:F2} ({nb.X:F0},{nb.Y:F0},{nb.W:F0}×{nb.H:F0}) ← 입력:"));
        foreach (var (d, from) in sources.Where(s => s.D.Label == nb.Label && DuplicateBoxes.Intersection(s.D, nb) >= 0.9 * s.D.W * s.D.H)
                                         .OrderByDescending(s => s.D.Conf))
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"      {from} {d.Conf:F2} ({d.X:F0},{d.Y:F0},{d.W:F0}×{d.H:F0})"));
    }
    foreach (var d in oldList) Cv2.Rectangle(img, new Rect((int)d.X, (int)d.Y, (int)d.W, (int)d.H), new Scalar(40, 40, 230), 1);
    foreach (var d in newList)
    {
        var r = new Rect((int)d.X, (int)d.Y, (int)d.W, (int)d.H);
        Cv2.Rectangle(img, r, new Scalar(60, 220, 60), 2);
        Cv2.PutText(img, string.Create(CultureInfo.InvariantCulture, $"{d.Label} {d.Conf:0.00}"), new Point(r.X, Math.Max(10, r.Y - 3)),
                    HersheyFonts.HersheyPlain, 0.9, new Scalar(60, 220, 60), 1);
    }
    Cv2.PutText(img, string.Create(CultureInfo.InvariantCulture, $"{name} {sec:F2}s  red=old(MergeNms)  green=new(full+{(variant == "cut" ? "MergeCutPieces" : "MergeNmm")})"),
                new Point(6, 18), HersheyFonts.HersheyPlain, 1.2, Scalar.Yellow, 1);
    string file = Path.Combine(outDir, $"sahi_{variant}_{name}_{sec.ToString("F2", CultureInfo.InvariantCulture)}.png");
    img.SaveImage(file);
    Console.WriteLine(file);
}

static double BoxIoU(Det a, Det b)
{
    double i = DuplicateBoxes.Intersection(a, b);
    double u = a.W * a.H + b.W * b.H - i;
    return u > 0 ? i / u : 0;
}

// 본 앱 DetectTile → DetectBgra 와 같은 길 (필터 전 — 필터는 부르는 쪽이 넣어 전후를 비교한다)
List<Det> DetectTile(Yolo yolo, Mat tileBgr, int ox, int oy)
{
    using var bgra = new Mat();
    Cv2.CvtColor(tileBgr, bgra, ColorConversionCodes.BGR2BGRA);
    return DetectBgra(yolo, bgra, ox, oy);
}

// 본 앱 DetectBgra 와 같은 길 (관심 클래스만, 중복 박스 필터 전). oldYoloPath(detect nearest)면 2026-11-05 전의 앱 —
// YoloDotNet 이 직접 줄이고(OpenModel 의 SamplingOptions) 박스는 YoloDotNet 의 정수 그대로
List<Det> DetectBgra(Yolo yolo, Mat bgra, int ox, int oy)
{
    var list = new List<Det>();
    if (oldYoloPath)
    {
        var info = new SKImageInfo(bgra.Width, bgra.Height, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using var sk = new SKBitmap();
        sk.InstallPixels(info, bgra.Data, (int)bgra.Step());
        foreach (var r in yolo.RunObjectDetection(sk, confidence: Confidence, iou: Iou))
        {
            if (!TargetClasses.Contains(r.Label.Name)) continue;
            var b = r.BoundingBox;
            list.Add(new Det(b.Left + ox, b.Top + oy, b.Width, b.Height, r.Label.Name, r.Confidence));
        }
        return list;
    }
    foreach (var b in letterbox.Detect(yolo, bgra, Confidence, Iou))
        if (TargetClasses.Contains(b.Label)) list.Add(new Det(b.X + ox, b.Y + oy, b.W, b.H, b.Label, b.Conf));
    return list;
}

static void Report(string name, (double From, double To)[] views, List<(int F, double Ms, List<Det> Dets)> frames,
                   Dictionary<int, List<Obs>> obs, Tracker prm, Action<string> log)
{
    log("");
    log($"===== {name} — 프레임 {frames.Count}장, 검출 {frames.Sum(x => x.Dets.Count)}개, 트랙 {obs.Count}개 =====");

    // 한 프레임 안의 중복 박스 (end-to-end NMS 모델이라 없을 것 같지만 실제로는 잦다 — 추적기가 한 물체로 묶는다)
    int dupFrames = frames.Count(fr => fr.Dets.Where((a, i) => fr.Dets.Skip(i + 1).Any(b => prm.IsDuplicate(a, b))).Any());
    int iouFrames = frames.Count(fr => fr.Dets.Where((a, i) => fr.Dets.Skip(i + 1).Any(b =>
        Tracker.GroupOf(a.Label) == Tracker.GroupOf(b.Label) && Tracker.IoU(a, b) > 0.7)).Any());
    log($"중복 박스가 있는 프레임: {dupFrames}장 (그중 IoU>0.7 쌍이 있는 프레임 {iouFrames}장)");

    var stats = obs.Select(kv => TrackStats.From(kv.Key, kv.Value)).OrderBy(s => s.FirstMs).ToList();
    for (int v = 0; v < views.Length; v++)
    {
        var vs = stats.Where(s => s.View == v).ToList();
        if (vs.Count == 0) continue;
        log("");
        log($"--- 뷰 {v} ({views[v].From / 1000:F1}~{(double.IsInfinity(views[v].To) ? "끝" : (views[v].To / 1000).ToString("F1"))}초): " +
            $"트랙 {vs.Count}개 · 3초 이상 {vs.Count(s => s.Duration >= 3000)}개 · 관측 1~2회 {vs.Count(s => s.Hits <= 2)}개");
        log("  ID     시작~끝(초)    길이   관측/구간(비율)  최대공백  정지  이동/크기  평균박스(x,y,w,h)       라벨(관측 수)  흔들림");
        foreach (var s in vs.Where(s => s.Duration >= 3000))
            log($"  {s.Id,-5} {s.FirstMs / 1000,6:F2}~{s.LastMs / 1000,6:F2} {s.Duration / 1000,5:F1}s  {s.Hits,5}/{s.Span,-5}({s.Ratio,4:P0})  " +
                $"{s.MaxGapMs / 1000,5:F2}s  {(s.Moved < 0.5 ? "정지" : "    ")}  {s.Moved,6:F1}    " +
                $"{s.MeanBox.X,5:F0},{s.MeanBox.Y,4:F0},{s.MeanBox.W,4:F0},{s.MeanBox.H,4:F0}   {s.Labels,-20} {s.Flips}");
    }

    // 뷰를 넘나든 트랙 (noreset 일 때만 생긴다 — 화면 전환 뒤 다른 물체가 옛 ID 를 물려받은 것)
    var cross = obs.Where(kv => kv.Value.Select(o => o.View).Distinct().Count() > 1).OrderBy(kv => kv.Key).ToList();
    log("");
    log($"뷰를 넘나든 트랙: {cross.Count}개");
    foreach (var (id, l) in cross.Take(8))
        log($"  ID {id}: 뷰 {string.Join("→", l.Select(o => o.View).Distinct())} · {l[0].Ms / 1000:F2}~{l[^1].Ms / 1000:F2}초 · " +
            $"{l[0].Det.Label}({l[0].Det.X:F0},{l[0].Det.Y:F0},{l[0].Det.W:F0}×{l[0].Det.H:F0}) → {l[^1].Det.Label}({l[^1].Det.X:F0},{l[^1].Det.Y:F0},{l[^1].Det.W:F0}×{l[^1].Det.H:F0})");

    // 라벨 흔들림: 같은 트랙의 연속 관측에서 라벨이 바뀐 쌍
    var flips = obs.Values.SelectMany(l => l.Zip(l.Skip(1)).Where(z => z.First.Det.Label != z.Second.Det.Label)
                                             .Select(z => string.Join("↔", new[] { z.First.Det.Label, z.Second.Det.Label }.Order())))
                   .GroupBy(x => x).OrderByDescending(g => g.Count()).Select(g => $"{g.Key} {g.Count()}");
    log("");
    log($"라벨 바뀜(연속 관측 쌍): {string.Join(", ", flips)}");

    // 의심스러운 연결: ① 오래 안 보인 멈춘 트랙을 다시 이음(정상 동작이지만 잘못 이을 수 있는 곳) ② 박스가 튐 ③ 크기가 급변
    log("");
    log("의심스러운 연결 (같은 ID 의 연속 관측 사이):");
    int relinks = 0, jumps = 0;
    foreach (var (id, l) in obs.OrderBy(kv => kv.Key))
        foreach (var (a, b) in l.Zip(l.Skip(1)))
        {
            double gap = b.Ms - a.Ms;
            double size = Math.Sqrt(a.Det.W * a.Det.H);
            double shift = Dist(a.Det, b.Det) / size;
            double area = (b.Det.W * b.Det.H) / (a.Det.W * a.Det.H);
            string? why = gap > prm.MovingGapMs ? "다시 이음" : (shift > 0.5 && gap < 200) ? "튐" : (area > 2 || area < 0.5) ? "크기 급변" : null;
            if (why == null) continue;
            if (why == "다시 이음") relinks++; else jumps++;
            log($"  ID {id,-5} {why,-6} {a.Ms / 1000,6:F2}→{b.Ms / 1000,6:F2}초 (공백 {gap / 1000:F2}s) 이동 {shift:F2}×크기 · 넓이 {area:F2}배 · IoU {Tracker.IoU(a.Det, b.Det):F2} · " +
                $"{a.Det.Label}({a.Det.X:F0},{a.Det.Y:F0},{a.Det.W:F0}×{a.Det.H:F0}) → {b.Det.Label}({b.Det.X:F0},{b.Det.Y:F0},{b.Det.W:F0}×{b.Det.H:F0})");
        }
    log($"  → 다시 이음 {relinks}건 · 튐/크기 급변 {jumps}건");

    // 끊김 후보: 트랙이 끝난 뒤 3초 안에 같은 자리(IoU≥0.3)에서 같은 묶음의 새 트랙이 시작 — 같은 물체가 ID 를 바꿨을 수 있다
    log("");
    log("끊김 후보 (끝난 트랙 자리에서 3초 안에 새 트랙 시작, 둘 다 관측 5회 이상):");
    int splits = 0;
    foreach (var s in stats.Where(s => s.Hits >= 5))
        foreach (var n in stats.Where(n => n.Hits >= 5 && n.View == s.View && n.FirstMs > s.LastMs && n.FirstMs - s.LastMs < 3000
                                           && Tracker.GroupOf(n.FirstDet.Label) == Tracker.GroupOf(s.LastDet.Label)
                                           && Tracker.IoU(n.FirstDet, s.LastDet) >= 0.3))
        {
            splits++;
            log($"  ID {s.Id} (~{s.LastMs / 1000:F2}초, {s.Moved:F1}×크기 이동) → ID {n.Id} ({n.FirstMs / 1000:F2}초~) 공백 {(n.FirstMs - s.LastMs) / 1000:F2}s · " +
                $"IoU {Tracker.IoU(n.FirstDet, s.LastDet):F2} · ({s.LastDet.X:F0},{s.LastDet.Y:F0},{s.LastDet.W:F0}×{s.LastDet.H:F0})");
        }
    log($"  → {splits}건");
}

// 목표 정지 차량: 구간 안에서 목표 상자와 IoU 0.5 이상인 관측을 10번 넘게 가진 트랙 수 — 1 이어야 한다.
// 지나가는 차는 크기가 달라 IoU 0.5 를 넘기 어렵고, 넘어도 몇 프레임뿐이라 10번 기준으로 거른다
static void CheckTarget((string Video, string Name, double FromMs, double ToMs, Det Box) t, Dictionary<int, List<Obs>> obs, Action<string> log)
{
    var hits = obs.Select(kv => (Id: kv.Key, L: kv.Value.Where(o => o.Ms >= t.FromMs && o.Ms < t.ToMs && Tracker.IoU(o.Det, t.Box) >= 0.5).ToList()))
                  .Where(x => x.L.Count >= 10).ToList();
    string ids = string.Join(", ", hits.Select(h => $"ID {h.Id} ({h.L[0].Ms / 1000:F2}~{h.L[^1].Ms / 1000:F2}초, 관측 {h.L.Count}회, 최대 공백 " +
        $"{h.L.Zip(h.L.Skip(1)).Select(z => z.Second.Ms - z.First.Ms).DefaultIfEmpty(0).Max() / 1000:F2}s)"));
    log($"[{(hits.Count == 1 ? "통과" : "실패")}] {t.Video} {t.Name}: 트랙 {hits.Count}개 — {ids}");
}

static double Dist(Det a, Det b)
{
    double dx = (a.X + a.W / 2) - (b.X + b.W / 2), dy = (a.Y + a.H / 2) - (b.Y + b.H / 2);
    return Math.Sqrt(dx * dx + dy * dy);
}

static List<(int F, double Ms, List<Det> Dets)> LoadDets(string path)
{
    var list = new List<(int, double, List<Det>)>();
    foreach (var line in File.ReadLines(path).Skip(1))
    {
        var c = line.Split(',');
        if (c[2].Length == 0) { list.Add((int.Parse(c[0]), double.Parse(c[1], CultureInfo.InvariantCulture), [])); continue; }
        double D(int i) => double.Parse(c[i], CultureInfo.InvariantCulture);
        list[^1].Item3.Add(new Det(D(4), D(5), D(6), D(7), c[2], D(3)));
    }
    return list;
}

// ---------- render: 트랙 ID 를 그린 프레임을 세로로 이어 붙여 PNG 로 ----------
int Render(string name, string crop, double scale, double[] secs)
{
    var byFrame = File.ReadLines(Path.Combine(outDir, name + ".tracks.csv")).Skip(1).Select(l => l.Split(','))
        .GroupBy(c => int.Parse(c[0])).ToDictionary(g => g.Key, g => g.ToList());
    var frameMs = LoadDets(Path.Combine(outDir, name + ".dets.csv")).Select(x => x.Ms).ToArray();
    using var cap = new VideoCapture(Path.Combine(RepoPaths.VideoDir, name + ".mp4"));
    var tiles = new List<Mat>();
    foreach (double sec in secs)
    {
        int f = Array.FindIndex(frameMs, m => m >= sec * 1000);
        if (f < 0) f = frameMs.Length - 1;
        cap.Set(VideoCaptureProperties.PosFrames, f);   // mp4 는 메타데이터가 맞아 PosFrames 시크가 정확하다(CLAUDE.md)
        using var img = new Mat();
        if (!cap.Read(img) || img.Empty()) continue;
        foreach (var c in byFrame.GetValueOrDefault(f) ?? [])
        {
            int id = int.Parse(c[3]);
            var r = new Rect((int)double.Parse(c[6], CultureInfo.InvariantCulture), (int)double.Parse(c[7], CultureInfo.InvariantCulture),
                             (int)double.Parse(c[8], CultureInfo.InvariantCulture), (int)double.Parse(c[9], CultureInfo.InvariantCulture));
            var color = Palette(id);
            Cv2.Rectangle(img, r, color, 1);
            // 정지 판정이면 * (stationary 열 — 그 열이 없던 옛 tracks.csv 도 그린다)
            Cv2.PutText(img, $"{id}{c[4][0]}{(c.Length > 10 && c[10] == "1" ? "*" : "")}", new Point(r.X, r.Y - 2), HersheyFonts.HersheyPlain, 0.9, color, 1);
        }
        var roi = crop == "full" ? new Rect(0, 0, img.Width, img.Height)
                                 : crop.Split(',').Select(int.Parse).ToArray() is var a ? new Rect(a[0], a[1], a[2], a[3]) : default;
        var tile = new Mat();
        Cv2.Resize(new Mat(img, roi), tile, new Size(), scale, scale, InterpolationFlags.Nearest);
        Cv2.PutText(tile, $"{name} {frameMs[f] / 1000:F2}s f{f}", new Point(4, 16), HersheyFonts.HersheyPlain, 1.2, Scalar.Yellow, 1);
        tiles.Add(tile);
    }
    using var sheet = new Mat();
    Cv2.VConcat(tiles.ToArray(), sheet);
    string file = Path.Combine(outDir, $"render_{name}_{string.Join("_", secs.Select(s => s.ToString("F1", CultureInfo.InvariantCulture)))}.png");
    sheet.SaveImage(file);
    Console.WriteLine(file);
    return 0;
}

static Scalar Palette(int id)
{
    // 이웃 ID 끼리 색이 잘 갈리게 황금비로 색상환을 돈다
    double h = (id * 0.618034) % 1.0 * 6;
    int i = (int)h;
    double f = h - i, q = 1 - f;
    var (r, g, b) = i switch { 0 => (1.0, f, 0.0), 1 => (q, 1.0, 0.0), 2 => (0.0, 1.0, f), 3 => (0.0, q, 1.0), 4 => (f, 0.0, 1.0), _ => (1.0, 0.0, q) };
    return new Scalar(b * 255, g * 255, r * 255);
}

// 본 앱 SelectBestGpu 와 같은 판정 — 루트 GpuAdapters.cs 를 그대로 링크한다 (내장이 아닌 GPU 우선, 그중 전용 VRAM 최대)
static (string name, uint vendorId, uint deviceId) SelectBestGpu() =>
    GpuAdapters.Best(GpuAdapters.Enumerate()) is { } a ? (a.Name, a.VendorId, a.DeviceId) : ("GPU", 0u, 0u);

// ---------- gpu: 내장/외장 판정 점검 (이슈 #3) ----------
// 이 PC 처럼 VRAM 기준도 같은 답(외장 7.9GB > 내장 0.45GB)을 내는 곳에선 DXCore 읽기가 깨져도 창 제목·HUD 안내·UI 테스트가
// 그대로라 아무것도 드러나지 않는다 → 실제 어댑터의 내장 여부를 찍어 못 읽었으면 실패로 치고, 내장 쪽 VRAM 이 더 큰 PC(AMD VGM 등)는
// 만든 어댑터 목록으로 판정을 확인한다. 하이브리드가 아닌 PC·Windows 10 2004 이전에선 '못 읽음'이 정상일 수 있다
static int GpuCheck()
{
    int failures = 0;
    void Expect(bool ok, string what)
    {
        Console.WriteLine($"[{(ok ? "통과" : "실패")}] {what}");
        if (!ok) failures++;
    }

    var real = GpuAdapters.Enumerate();
    Console.WriteLine("[이 PC 의 어댑터] (DXGI 열거 순서, 소프트웨어 제외)");
    foreach (var a in real)
        Console.WriteLine($"  #{a.Index} {a.Name} · {a.VendorId:X4}:{a.DeviceId:X4} · 전용 {a.DedicatedVideoMemory / (1024 * 1024)} MB · " +
                          $"내장 {(a.Integrated is { } i ? (i ? "예" : "아니오") : "못 읽음")} · {(GpuAdapters.IsHighPerformance(a, real) ? "고성능" : "저전력")}");
    Console.WriteLine($"  추론 GPU: {(GpuAdapters.Best(real) is { } best ? best.Name : "없음")}");
    if (real.Count >= 2)
        Expect(real.TrueForAll(a => a.Integrated != null), "DXCore 로 모든 어댑터의 내장 여부를 읽음 (못 읽으면 VRAM 기준으로 판정한다)");

    // 만든 목록: (순번, 전용 VRAM GB, 내장 여부)
    static GpuAdapters.Adapter A(int index, double gb, bool? integrated, string name) =>
        new(index, name, 0, 0, index + 1, (ulong)(gb * 1024 * 1024 * 1024), integrated);
    void Case(string what, GpuAdapters.Adapter[] list, string? best, string[] high)
    {
        // 열거 순서는 이 exe 의 그래픽 설정에 따라 바뀐다 — 거꾸로 넣어도 같아야 한다
        foreach (var order in new[] { list, Enumerable.Reverse(list).ToArray() })   // list.Reverse() 는 C# 14 에서 Span 의 제자리 뒤집기로 묶인다
        {
            var got = GpuAdapters.Best(order)?.Name;
            var gotHigh = order.Where(a => GpuAdapters.IsHighPerformance(a, order)).Select(a => a.Name).OrderBy(n => n).ToArray();
            bool reversed = !ReferenceEquals(order, list);
            Expect(got == best && gotHigh.SequenceEqual(high.OrderBy(n => n)),
                   $"{what}{(reversed ? " (열거 거꾸로)" : "")}: 추론 {got ?? "없음"} · 고성능 [{string.Join(", ", gotHigh)}]");
        }
    }
    Case("VGM — 내장 16GB · 외장 8GB", [A(0, 16, true, "내장"), A(1, 8, false, "외장")], "외장", ["외장"]);
    Case("전용 VRAM 같음 8GB", [A(0, 8, true, "내장"), A(1, 8, false, "외장")], "외장", ["외장"]);
    Case("외장 둘 + 내장", [A(0, 4, false, "외장4"), A(1, 16, true, "내장"), A(2, 8, false, "외장8")], "외장8", ["외장4", "외장8"]);
    Case("외장 둘 (VRAM 기준)", [A(0, 4, false, "외장4"), A(1, 8, false, "외장8")], "외장8", ["외장8"]);
    Case("내장 여부 하나라도 못 읽음 → VRAM 기준", [A(0, 16, null, "모름"), A(1, 8, false, "외장")], "모름", ["모름"]);
    Case("GPU 하나", [A(0, 0.5, true, "내장")], "내장", ["내장"]);
    Expect(GpuAdapters.Best([]) == null, "어댑터 없음 → null");

    Console.WriteLine(failures == 0 ? "모두 통과" : $"실패 {failures}건");
    return failures;
}

static string FindRepoRoot()
{
    for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        if (File.Exists(Path.Combine(dir.FullName, "DetectView.csproj"))) return dir.FullName;
    throw new DirectoryNotFoundException("저장소 루트(DetectView.csproj)를 찾지 못했습니다.");
}

record struct Obs(int F, double Ms, int View, Det Det);

// 한 프레임에서 관측된 트랙 하나: 추적기가 본 박스(중복 중 대표)와 그 뒤의 정지 판정. Segment = Reset 사이 구간 번호
record struct Judged(int Id, double Ms, int Segment, Det Box, bool Stationary);

// selftest 의 한 프레임: 검출마다 (트랙 ID, 정지 판정), 살아 있는 트랙 수
record SimFrame(double Ms, List<(int Id, bool Stat)> Dets, int Alive);

record TrackStats(int Id, int View, double FirstMs, double LastMs, int Hits, int Span, double MaxGapMs, double Moved,
                  Det MeanBox, Det FirstDet, Det LastDet, string Labels, int Flips)
{
    public double Duration => LastMs - FirstMs;
    public double Ratio => (double)Hits / Span;

    public static TrackStats From(int id, List<Obs> l)
    {
        double maxGap = l.Zip(l.Skip(1)).Select(z => z.Second.Ms - z.First.Ms).DefaultIfEmpty(0).Max();
        var mean = new Det(l.Average(o => o.Det.X), l.Average(o => o.Det.Y), l.Average(o => o.Det.W), l.Average(o => o.Det.H), "", 0);
        // 이동량: 처음·끝 중심 거리 / 평균 크기 — 0.5 미만이면 내내 그 자리에 있었던 것
        double cx0 = l[0].Det.X + l[0].Det.W / 2, cy0 = l[0].Det.Y + l[0].Det.H / 2;
        double moved = l.Max(o => Math.Sqrt(Math.Pow(o.Det.X + o.Det.W / 2 - cx0, 2) + Math.Pow(o.Det.Y + o.Det.H / 2 - cy0, 2)))
                       / Math.Sqrt(mean.W * mean.H);
        string labels = string.Join(" ", l.GroupBy(o => o.Det.Label).OrderByDescending(g => g.Count()).Select(g => $"{g.Key}({g.Count()})"));
        int flips = l.Zip(l.Skip(1)).Count(z => z.First.Det.Label != z.Second.Det.Label);
        return new TrackStats(id, l[0].View, l[0].Ms, l[^1].Ms, l.Count, l[^1].F - l[0].F + 1, maxGap, moved,
                              mean, l[0].Det, l[^1].Det, labels, flips);
    }
}
