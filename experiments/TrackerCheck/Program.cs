// 추적기(루트 Tracker.cs) 검증. 기준 영상 test4·test5 의 모든 프레임을 본 앱 ProcessFrame 과 같은 길로 검출해 CSV 로 남기고(detect),
// 그 검출로 추적기를 돌려 트랙별 시작·끝·관측 비율·클래스 흔들림과 의심스러운 연결을 보고하고(track),
// 트랙 ID 를 그린 프레임을 뽑아 눈으로 확인한다(render). 검출은 GPU 로 한 번만 하고, 파라미터는 track 만 다시 돌려 고른다.
// 본 앱 표시 경로의 중복 박스 필터(Detection.cs DuplicateBoxes) 전후도 여기서 본다(dedup). SAHI 병합 방식 전후도 본다(sahi).
// 화면 가장자리에 걸친 박스의 멈춤 판정(이슈 #10)은 track 이 함께 재고(noframe: 프레임 크기를 안 줘 예전 판정), 합성 장면으로도 본다(selftest).
//
// 실행: TrackerCheck detect
//       TrackerCheck track [MovingGapMs=1000 StationaryGapMs=10000 IouThreshold=0.3 RelinkIouThreshold=0.5 BorderMargin=2 ...] [noreset] [noframe]
//       TrackerCheck selftest
//       TrackerCheck gpu        (추적기와 별개: 루트 GpuAdapters.cs 의 내장/외장 판정 점검 — 이슈 #3. 종료 코드 = 실패 수)
//       TrackerCheck dedup [Every=15]
//       TrackerCheck sahi [Every=15] [Variant=nmm] [test5@44.75 ...]
//       TrackerCheck render <test4|test5> <x,y,w,h|full> <배율> <초> [<초> ...]

using System.Globalization;
using System.Text;
using OpenCvSharp;
using SkiaSharp;
using Vortice.DXGI;
using YoloDotNet;
using YoloDotNet.Extensions;
using YoloDotNet.Models;
using YOLO11_Test;
using Tracker = YOLO11_Test.Tracker;   // OpenCvSharp.Tracker 와 겹친다

const double Confidence = 0.3, Iou = 0.7;   // 본 앱 ConfidenceThreshold · IouThreshold
string[] TargetClasses = ["person", "car", "motorcycle", "truck", "bus", "scooter", "bicycle"];   // 본 앱 TargetClasses

// 테스트 영상은 gitignore 대상이라 워크트리에는 없다 — 원래 저장소의 배포 폴더에서 읽는다
const string VideoDir = @"E:\VS_E\YOLO11_Test\publish_gpu_ver";

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

string repo = FindRepoRoot();
string outDir = Path.Combine(repo, "experiments", "TrackerCheck", "out");
Directory.CreateDirectory(outDir);
Console.OutputEncoding = Encoding.UTF8;

return args.FirstOrDefault() switch
{
    "detect" => Detect(),
    "track" => TrackAll(args.Skip(1).ToArray()),
    "selftest" => SelfTest(),
    "gpu" => GpuCheck(),
    "dedup" => Dedup(args.Skip(1).ToArray()),
    "sahi" => SahiCompare(args.Skip(1).ToArray()),
    "render" when args.Length >= 5 => Render(args[1], args[2], double.Parse(args[3], CultureInfo.InvariantCulture),
                                             args.Skip(4).Select(s => double.Parse(s, CultureInfo.InvariantCulture)).ToArray()),
    _ => Usage(),
};

static int Usage()
{
    Console.WriteLine("사용법: detect | track [이름=값 ...] [noreset] [noframe] | selftest | gpu | dedup [Every=N] | sahi [Every=N] [영상@초 ...] | render <영상> <x,y,w,h|full> <배율> <초...>");
    return 2;
}

// ---------- detect: 모든 프레임 검출 → out/<영상>.dets.csv ----------
Yolo OpenModel(Action<string> log)
{
    string modelPath = Path.Combine(repo, "best.onnx");
    var (gpuName, vendorId, deviceId) = SelectBestGpu();
    var device = WindowsMlExecutionProvider.FindDmlDevice(vendorId, deviceId)
                 ?? throw new InvalidOperationException("DirectML 장치를 PCI ID 로 찾지 못함");
    var yolo = new Yolo(new YoloOptions { ExecutionProvider = new WindowsMlExecutionProvider(modelPath, device) });
    var meta = yolo.OnnxModel?.CustomMetaData;
    log($"모델: {meta?.GetValueOrDefault("description")} · {meta?.GetValueOrDefault("date")} · {gpuName}");
    return yolo;
}

int Detect()
{
    using var yolo = OpenModel(Console.WriteLine);

    foreach (var (name, _) in videos)
    {
        string path = Path.Combine(VideoDir, name + ".mp4");
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
            var info = new SKImageInfo(bgra.Width, bgra.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var sk = new SKBitmap();
            sk.InstallPixels(info, bgra.Data, (int)bgra.Step());
            foreach (var r in yolo.RunObjectDetection(sk, confidence: Confidence, iou: Iou))
            {
                if (!TargetClasses.Contains(r.Label.Name)) continue;
                var b = r.BoundingBox;
                writer.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"{n},{ms:R},{r.Label.Name},{r.Confidence:R},{b.Left},{b.Top},{b.Width},{b.Height}"));
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
        using var cap = new VideoCapture(Path.Combine(VideoDir, name + ".mp4"));
        using var frame = new Mat();
        int f = -1, n = 0, rawTotal = 0, oldTotal = 0, newTotal = 0, droppedDup = 0, droppedOther = 0, added = 0, leftDup = 0, leftDupFrames = 0, postTotal = 0;
        while (cap.Read(frame) && !frame.Empty())
        {
            if (++f % every != 0) continue;
            n++;
            double ms = cap.Get(VideoCaptureProperties.PosMsec);
            var raw = new List<Det>();
            var filtered = new List<Det>();
            int step = (int)(640 * (1 - 0.3));
            foreach (int oy in Sahi.TileOrigins(frame.Height, 640, step))
                foreach (int ox in Sahi.TileOrigins(frame.Width, 640, step))
                {
                    using var roi = new Mat(frame, new Rect(ox, oy, Math.Min(640, frame.Width - ox), Math.Min(640, frame.Height - oy)));
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
        using var cap = new VideoCapture(Path.Combine(VideoDir, name + ".mp4"));
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
    const int tile = 640;
    int step = (int)(tile * (1 - 0.3));
    var tiles = new List<(Det D, bool Cut)>();
    var sources = new List<(Det D, string From)>();
    foreach (int oy in Sahi.TileOrigins(frame.Height, tile, step))
        foreach (int ox in Sahi.TileOrigins(frame.Width, tile, step))
        {
            int tw = Math.Min(tile, frame.Width - ox), th = Math.Min(tile, frame.Height - oy);
            using var roi = new Mat(frame, new Rect(ox, oy, tw, th));
            foreach (var d in DetectTile(yolo, roi, ox, oy))
            {
                bool cut = Sahi.TouchesInnerEdge(d, ox, oy, tw, th, frame.Width, frame.Height);
                tiles.Add((d, cut));
                sources.Add((d, $"타일({ox},{oy}){(cut ? " 잘림" : "")}"));
            }
        }
    List<Det> full = frame.Width > tile || frame.Height > tile ? DetectTile(yolo, frame, 0, 0) : new List<Det>();
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
    using var cap = new VideoCapture(Path.Combine(VideoDir, name + ".mp4"));
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
    var info = new SKImageInfo(bgra.Width, bgra.Height, SKColorType.Bgra8888, SKAlphaType.Opaque);
    using var sk = new SKBitmap();
    sk.InstallPixels(info, bgra.Data, (int)bgra.Step());
    var list = new List<Det>();
    foreach (var r in yolo.RunObjectDetection(sk, confidence: Confidence, iou: Iou))
    {
        if (!TargetClasses.Contains(r.Label.Name)) continue;
        var b = r.BoundingBox;
        list.Add(new Det(b.Left + ox, b.Top + oy, b.Width, b.Height, r.Label.Name, r.Confidence));
    }
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
    using var cap = new VideoCapture(Path.Combine(VideoDir, name + ".mp4"));
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
        if (File.Exists(Path.Combine(dir.FullName, "YOLO11_Test.csproj"))) return dir.FullName;
    throw new DirectoryNotFoundException("저장소 루트(YOLO11_Test.csproj)를 찾지 못했습니다.");
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
