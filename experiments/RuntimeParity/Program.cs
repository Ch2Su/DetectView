// 옛 런타임(ONNX Runtime DirectML 1.23 + YoloDotNet DirectML 공급자)과 Windows ML 런타임(WindowsMlExecutionProvider)의
// 검출 결과를 같은 프레임에서 비교한다. 두 런타임은 한 프로세스에 둘 수 없어서 이 소스를 Old/ · WinML/ 로 두 번 빌드한다.
// 검출은 본 앱 ProcessFrame 과 같은 길로 한다: BGR → BGRA → SKBitmap(Premul) → RunObjectDetection(0.3, 0.7).
//
// 실행: Old.exe detect <결과.csv> · WinML.exe detect <결과.csv> · (둘 중 아무거나) compare <기준.csv> <비교.csv>

using System.Globalization;
using OpenCvSharp;
using SkiaSharp;
using DetectView;   // GpuAdapters (두 빌드 모두), WindowsMlExecutionProvider (WinML 만)
using YoloDotNet;
using YoloDotNet.Extensions;
using YoloDotNet.Models;
#if OLD_RUNTIME
using YoloDotNet.ExecutionProvider.DirectML;
#endif

const double Confidence = 0.3, Iou = 0.7;   // 본 앱 ConfidenceThreshold · IouThreshold
const int PerVideo = 40;

string repo = FindRepoRoot();
if (args.Length == 3 && args[0] == "compare") return Compare(args[1], args[2]);
if (args.Length != 2 || args[0] != "detect") { Console.WriteLine("사용법: detect <결과.csv> | compare <기준.csv> <비교.csv>"); return 2; }

string modelPath = Path.Combine(repo, "best.onnx");
var (gpuIndex, gpuName, vendorId, deviceId) = SelectBestGpu();
#if OLD_RUNTIME
var provider = new DirectMLExecutionProvider(modelPath, gpuIndex);
string runtime = "ONNX Runtime DirectML 1.23 (YoloDotNet 공급자)";
#else
var device = WindowsMlExecutionProvider.FindDmlDevice(vendorId, deviceId)
             ?? throw new InvalidOperationException("DirectML 장치를 PCI ID 로 찾지 못함");
var provider = new WindowsMlExecutionProvider(modelPath, device);
string runtime = "Windows ML (WindowsMlExecutionProvider)";
#endif
using var yolo = new Yolo(new YoloOptions { ExecutionProvider = provider });
Console.WriteLine($"{runtime} · {gpuName}");

using var writer = new StreamWriter(args[1]);
writer.WriteLine("frame,label,conf,x,y,w,h");
int frameNo = 0, total = 0;
foreach (var v in new[] { "test1.mp4", "test2.mp4", "test3.mp4", "test4.mp4", "test5.mp4" })
{
    string path = Path.Combine(repo, "publish", v);
    if (!File.Exists(path)) { Console.WriteLine($"  {v} 없음 — 건너뜀"); continue; }
    using var cap = new VideoCapture(path);
    int count = (int)cap.Get(VideoCaptureProperties.FrameCount);
    using var frame = new Mat();
    using var bgra = new Mat();
    for (int i = 0; i < PerVideo; i++)
    {
        cap.Set(VideoCaptureProperties.PosFrames, (int)((i + 0.5) * count / PerVideo));
        if (!cap.Read(frame) || frame.Empty()) continue;
        Cv2.CvtColor(frame, bgra, ColorConversionCodes.BGR2BGRA);
        var info = new SKImageInfo(bgra.Width, bgra.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var sk = new SKBitmap();
        sk.InstallPixels(info, bgra.Data, (int)bgra.Step());
        foreach (var r in yolo.RunObjectDetection(sk, confidence: Confidence, iou: Iou))
        {
            var b = r.BoundingBox;
            writer.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{frameNo},{r.Label.Name},{r.Confidence:R},{b.Left},{b.Top},{b.Width},{b.Height}"));
            total++;
        }
        frameNo++;
    }
}
Console.WriteLine($"프레임 {frameNo}장, 검출 {total}개 → {args[1]}");
return 0;

// 본 앱 SelectBestGpu 와 같은 판정 — 루트 GpuAdapters.cs 를 두 빌드 모두 링크한다 (내장이 아닌 GPU 우선, 그중 전용 VRAM 최대).
// 옛 공급자는 DXGI 열거 순번, Windows ML 은 PCI ID 를 쓴다
static (int index, string name, uint vendorId, uint deviceId) SelectBestGpu() =>
    GpuAdapters.Best(GpuAdapters.Enumerate()) is { } a ? (a.Index, a.Name, a.VendorId, a.DeviceId) : (0, "GPU", 0u, 0u);

// 프레임마다 같은 라벨·IoU ≥ 0.5 를 IoU 큰 순으로 1:1 짝짓고, 짝의 좌표·신뢰도 차이와 한쪽에만 있는 검출을 센다
static int Compare(string refPath, string testPath)
{
    var a = Load(refPath);
    var b = Load(testPath);
    int frames = Math.Max(a.Keys.DefaultIfEmpty(-1).Max(), b.Keys.DefaultIfEmpty(-1).Max()) + 1;
    int nA = 0, nB = 0, matched = 0, identical = 0, framesSame = 0;
    double maxDConf = 0, sumDConf = 0;
    int maxDBox = 0;
    var unmatched = new List<string>();
    for (int f = 0; f < frames; f++)
    {
        var ra = a.GetValueOrDefault(f) ?? [];
        var rb = b.GetValueOrDefault(f) ?? [];
        nA += ra.Count;
        nB += rb.Count;
        var pairs = new List<(double Iou, int I, int J)>();
        for (int i = 0; i < ra.Count; i++)
            for (int j = 0; j < rb.Count; j++)
                if (ra[i].Label == rb[j].Label && IoU(ra[i], rb[j]) is var v && v >= 0.5) pairs.Add((v, i, j));
        var usedA = new bool[ra.Count];
        var usedB = new bool[rb.Count];
        int frameIdentical = 0;
        foreach (var (_, i, j) in pairs.OrderByDescending(p => p.Iou))
        {
            if (usedA[i] || usedB[j]) continue;
            usedA[i] = usedB[j] = true;
            matched++;
            double dc = Math.Abs(ra[i].Conf - rb[j].Conf);
            int db = new[] { ra[i].X - rb[j].X, ra[i].Y - rb[j].Y, ra[i].W - rb[j].W, ra[i].H - rb[j].H }.Max(Math.Abs);
            sumDConf += dc;
            maxDConf = Math.Max(maxDConf, dc);
            maxDBox = Math.Max(maxDBox, db);
            if (db == 0 && dc < 1e-6) { identical++; frameIdentical++; }
        }
        for (int i = 0; i < ra.Count; i++) if (!usedA[i]) unmatched.Add($"  프레임 {f}: 기준에만 {ra[i].Label} {ra[i].Conf:F3}");
        for (int j = 0; j < rb.Count; j++) if (!usedB[j]) unmatched.Add($"  프레임 {f}: 비교에만 {rb[j].Label} {rb[j].Conf:F3}");
        if (ra.Count == rb.Count && frameIdentical == ra.Count) framesSame++;
    }
    Console.WriteLine($"프레임 {frames}장 · 기준 {nA}개 · 비교 {nB}개 · 짝 {matched}개 (완전히 같음 {identical}개) · 모든 검출이 완전히 같은 프레임 {framesSame}장");
    Console.WriteLine($"짝의 차이: 좌표 최대 {maxDBox} px · 신뢰도 최대 {maxDConf:F6} / 평균 {(matched > 0 ? sumDConf / matched : 0):F6}");
    Console.WriteLine(unmatched.Count == 0 ? "한쪽에만 있는 검출 없음" : $"한쪽에만 있는 검출 {unmatched.Count}개:\n{string.Join("\n", unmatched)}");
    return 0;
}

static Dictionary<int, List<(string Label, double Conf, int X, int Y, int W, int H)>> Load(string path)
{
    var map = new Dictionary<int, List<(string, double, int, int, int, int)>>();
    foreach (var line in File.ReadLines(path).Skip(1))
    {
        var p = line.Split(',');
        int f = int.Parse(p[0]);
        if (!map.TryGetValue(f, out var list)) map[f] = list = [];
        list.Add((p[1], double.Parse(p[2], CultureInfo.InvariantCulture), int.Parse(p[3]), int.Parse(p[4]), int.Parse(p[5]), int.Parse(p[6])));
    }
    return map;
}

static double IoU((string Label, double Conf, int X, int Y, int W, int H) a, (string Label, double Conf, int X, int Y, int W, int H) b)
{
    int w = Math.Min(a.X + a.W, b.X + b.W) - Math.Max(a.X, b.X);
    int h = Math.Min(a.Y + a.H, b.Y + b.H) - Math.Max(a.Y, b.Y);
    if (w <= 0 || h <= 0) return 0;
    double inter = (double)w * h;
    return inter / ((double)a.W * a.H + (double)b.W * b.H - inter);
}

static string FindRepoRoot()
{
    for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        if (File.Exists(Path.Combine(dir.FullName, "DetectView.csproj"))) return dir.FullName;
    throw new DirectoryNotFoundException("저장소 루트(DetectView.csproj)를 찾지 못했습니다.");
}
