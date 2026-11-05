// RF-DETR(COCO 사전학습 Nano·Small·Medium)를 본 앱의 런타임 — Windows ML 의 ONNX Runtime + DirectML — 에서 돌려 본다 (README).
// 모델·기준 이미지·파이썬 기준값은 export_rfdetr.py 가 out/ 에 만든다.
//   ① 노드 배치: 모든 노드가 DirectML 에 올라가는지 — ORT 프로파일의 provider. CPU 로 떨어진 노드가 있으면 그 연산·시간
//   ② 추론 시간: session.Run 만. 앱의 YOLO26s(루트 best.onnx)와 같은 세션 설정·같은 GPU 로 잰다
//   ③ 결과가 맞는지: (a) 같은 입력 텐서 → DirectML 출력 vs 파이썬 ONNX Runtime(CPU) 출력
//                   (b) C# 전처리(OpenCV)·후처리로 낸 검출 vs PyTorch predict() 검출 (rfdetr 공식 추론)
// 사용: RfDetrBench.exe [회차=2] — 회차마다 ② 를 다시 잰다 (GPU 클럭·다른 부하로 흔들리는지 보려고)
//       RfDetrBench.exe pre [영상당 프레임 수] — 3단계 준비: 전처리 비용·DirectML 결정성 (PreBench.cs)
//       RfDetrBench.exe yolo <모델.onnx>... — YOLO 변형(NMS 를 넣어 내보냄 · 640×384)의 노드 배치·session.Run (YoloBench.cs)
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.ML.OnnxRuntime;
using OpenCvSharp;

// 1단계를 넘길지 가르는 기준 (2026-11-04 사용자에게 제안한 것): 12 ms 안이면 60fps 영상(프레임당 16.7 ms)에 디코딩·전처리 여유가 남고,
// 25 ms 를 넘으면 HUD 가 40 FPS 아래로 떨어진다 (HUD 는 FPS 의 역수가 곧 박스 지연)
const double PassMs = 12, StopMs = 25;

int rounds = args.Length > 0 && int.TryParse(args[0], out var n0) && n0 > 0 ? n0 : 2;
string here = FindHere();
if (args.Length > 0 && args[0] == "pre") return PreBench.Run(here, args[1..]);
if (args.Length > 0 && args[0] == "yolo") return YoloBench.Run(here, args[1..]);
string outDir = Path.Combine(here, "out");
string repo = Path.GetFullPath(Path.Combine(here, "..", ".."));
string manifestPath = Path.Combine(outDir, "ref", "manifest.json");
if (!File.Exists(manifestPath))
{
    Console.WriteLine($"{manifestPath} 가 없습니다 — 먼저 export_rfdetr.py 를 돌리세요 (README)");
    return 1;
}
using var manifestDoc = JsonDocument.Parse(File.ReadAllText(manifestPath));
var manifest = manifestDoc.RootElement;

var names = manifest.GetProperty("samples").EnumerateArray().Select(s => s.GetProperty("name").GetString()!).ToList();
var images = names.Select(nm => Cv2.ImRead(Path.Combine(outDir, "samples", nm + ".png"), ImreadModes.Color)).ToList();
Console.WriteLine($"[기준 이미지] {images.Count}장 — {string.Join(", ", names)}");
if (images.Count == 0 || images.Any(m => m.Empty())) { Console.WriteLine("기준 이미지를 읽지 못했습니다."); return 1; }

// 하이브리드 노트북이라 DirectML 장치가 AMD 내장·NVIDIA 둘이다. 본 앱의 추론 GPU 와 같게 NVIDIA 로 잰다
var env = OrtEnv.Instance();
var dml = env.GetEpDevices().FirstOrDefault(d => d.EpName == "DmlExecutionProvider" && d.HardwareDevice.VendorId == 0x10DE)
          ?? env.GetEpDevices().FirstOrDefault(d => d.EpName == "DmlExecutionProvider");
if (dml == null) { Console.WriteLine("DirectML 장치가 없습니다."); return 1; }
Console.WriteLine($"[장치] DirectML · {dml.HardwareDevice.Vendor} (vendor 0x{dml.HardwareDevice.VendorId:X4}, device 0x{dml.HardwareDevice.DeviceId:X4}) · ONNX Runtime {env.GetVersionString()}");

var models = new List<ModelSpec>();
string yolo = Path.Combine(repo, "best.onnx");
if (File.Exists(yolo)) models.Add(new ModelSpec("YOLO26s (앱 기준, 루트 best.onnx)", yolo, 640, null, default));
else Console.WriteLine($"  {yolo} 없음 — 기준 없이 잰다");
var rfModels = new List<ModelSpec>();
foreach (var m in manifest.GetProperty("models").EnumerateObject())
    rfModels.Add(new ModelSpec($"RF-DETR {m.Name}", OutFile(m.Value.GetProperty("file").GetString()!),
        m.Value.GetProperty("resolution").GetInt32(), m.Name, m.Value));
// variants.py 가 만든 추가 비교 (RF-DETR FP16 · 앱 YOLO26s FP16 · YOLO26 COCO n/s/m) — 있으면 YOLO 는 앱 모델 뒤, RF-DETR 는 FP32 뒤에 둔다
string variantsPath = Path.Combine(outDir, "ref", "variants.json");
if (File.Exists(variantsPath))
{
    using var vdoc = JsonDocument.Parse(File.ReadAllText(variantsPath));
    foreach (var v in vdoc.RootElement.GetProperty("models").EnumerateArray())
    {
        string name = v.GetProperty("name").GetString()!, file = OutFile(v.GetProperty("file").GetString()!);
        int res = v.GetProperty("resolution").GetInt32();
        if (v.GetProperty("kind").GetString() == "rfdetr")
        {
            string size = v.GetProperty("ref").GetString()!;   // 기준값(입력 텐서·CPU 출력·PyTorch 검출)은 같은 크기의 FP32 것
            rfModels.Add(new ModelSpec(name, file, res, size, manifest.GetProperty("models").GetProperty(size)));
        }
        else models.Add(new ModelSpec(name, file, res, null, default));
    }
}
models.AddRange(rfModels);

// 모델별 입력 — YOLO 는 앱(YoloDotNet)과 같은 레터박스, RF-DETR 는 공식과 같은 float 경로 (③ 에서 시간·차이를 따로 본다)
var inputs = models.ToDictionary(m => m.Name,
    m => images.Select(img => m.Size == null ? Prep.Letterbox(img, m.Res) : Prep.RfDetr(img, m.Res, viaFloat: true)).ToList());

using var runOptions = new RunOptions();
var timings = new Dictionary<string, List<Timing>>();
for (int round = 1; round <= rounds; round++)
{
    Console.WriteLine($"\n================ {round}회차 ================");
    foreach (var m in models)
    {
        Console.WriteLine($"\n[{m.Name}] 입력 {m.Res}×{m.Res}");
        try
        {
            if (round == 1) Console.WriteLine(Profile(m));
            var t = Time(m);
            Console.WriteLine($"  ② {t}");
            if (!timings.TryGetValue(m.Name, out var list)) timings[m.Name] = list = [];
            list.Add(t);
            if (round == 1 && m.Size != null) Parity(m);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  실패: {ex.GetType().Name}: {ex.Message}");
        }
    }
}

Console.WriteLine("\n================ 요약: session.Run (입력 복사·출력 읽기 포함, 전처리·후처리 제외) ================");
Console.WriteLine($"{"모델",-30} 회차마다 [세션 생성 · 이미지별 중앙 · p95 · 같은 입력 200회 평균]");
foreach (var m in models)
    if (timings.TryGetValue(m.Name, out var ts))
        Console.WriteLine($"{m.Name,-30} " + string.Join("  |  ", ts.Select(t => t.Short())));
var baseline = models.FirstOrDefault(m => m.Size == null) is { } y && timings.TryGetValue(y.Name, out var yt) ? yt.Min(t => t.Median) : (double?)null;
foreach (var m in models.Where(m => m.Size != null))
{
    if (!timings.TryGetValue(m.Name, out var ts)) continue;
    double med = ts.Min(t => t.Median);   // 회차 가운데 가장 빠른 중앙값 — 다른 부하가 끼어든 회차를 덜 탄다
    string verdict = med <= PassMs ? "통과 — 다음 단계(파인튜닝) 후보" : med <= StopMs ? "애매 — FP16·작은 크기를 더 볼 것" : "접기 — 앱 실시간 경로에 무겁다";
    Console.WriteLine($"  {m.Name,-16} 중앙 {med,6:F2} ms{(baseline is { } b ? $" (YOLO26s 의 {med / b:F1}배)" : "")} → {verdict}");
}
return 0;

SessionOptions AppOptions()
{
    // 본 앱(WindowsMlExecutionProvider)과 같은 설정 — DirectML 은 병렬 실행·메모리 패턴을 지원하지 않는다
    var so = new SessionOptions
    {
        GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
        ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
        EnableMemoryPattern = false,
        EnableCpuMemArena = true,
    };
    so.AppendExecutionProvider(env, [dml], new Dictionary<string, string>());
    return so;
}

// ① 프로파일 세션은 시간을 재는 세션과 따로 만든다 — 프로파일링을 켠 채로 재면 오버헤드가 섞인다
string Profile(ModelSpec m)
{
    using var so = AppOptions();
    // EnableProfiling 이 켜질 때의 접두사를 쓰므로 접두사를 먼저 지정해야 한다 (NpuBench README 함정 7)
    so.ProfileOutputPathPrefix = Path.Combine(outDir, "prof_" + Regex.Replace(m.Name, "[^A-Za-z0-9]+", "_").Trim('_'));
    so.EnableProfiling = true;
    using var s = new InferenceSession(m.Path, so);
    var buf = new float[3 * m.Res * m.Res];
    inputs[m.Name][0].CopyTo(buf, 0);
    using var v = OrtValue.CreateTensorValueFromMemory(buf, [1, 3, m.Res, m.Res]);
    for (int i = 0; i < 5; i++) RunAll(s, v);
    return "  ① 노드 배치 (실행 5회 프로파일)\n" + Prep.SummarizeProfile(s.EndProfiling(), 5);
}

Timing Time(ModelSpec m)
{
    var sw = Stopwatch.StartNew();
    using var so = AppOptions();
    using var s = new InferenceSession(m.Path, so);
    double load = sw.Elapsed.TotalSeconds;
    var buf = new float[3 * m.Res * m.Res];
    using var v = OrtValue.CreateTensorValueFromMemory(buf, [1, 3, m.Res, m.Res]);   // buf 를 고정해 감싼다 — 내용을 바꿔 넣으면 다음 실행에 쓰인다
    var ins = inputs[m.Name];
    ins[0].CopyTo(buf, 0);
    for (int i = 0; i < 20; i++) RunAll(s, v);   // 워밍업 (GPU 클럭·DirectML 첫 실행)
    var ms = new List<double>();
    for (int pass = 0; pass < 5; pass++)
        foreach (var x in ins)
        {
            x.CopyTo(buf, 0);
            sw.Restart();
            RunAll(s, v);
            ms.Add(sw.Elapsed.TotalMilliseconds);
        }
    ins[0].CopyTo(buf, 0);
    sw.Restart();
    for (int i = 0; i < 200; i++) RunAll(s, v);
    double repeat = sw.Elapsed.TotalMilliseconds / 200;
    ms.Sort();
    return new Timing(load, ms[ms.Count / 2], ms[Math.Min(ms.Count - 1, (int)(ms.Count * 0.95))], ms.Average(), repeat);
}

float[][] RunAll(InferenceSession s, OrtValue v)
{
    using var outs = s.Run(runOptions, [s.InputNames[0]], [v], s.OutputNames);
    return outs.Select(o => o.GetTensorDataAsSpan<float>().ToArray()).ToArray();
}

// 출력은 이름으로 고른다 — 클래스가 3개면 logits 폭(4)이 박스 폭과 같아 모양으로 고르면 뒤바뀐다 (rfdetr ONNX 문서의 함정 1)
(float[] Dets, float[] Logits) RunRf(InferenceSession s, OrtValue v, string detsName, string labelsName)
{
    using var outs = s.Run(runOptions, [s.InputNames[0]], [v], [detsName, labelsName]);
    return (outs[0].GetTensorDataAsSpan<float>().ToArray(), outs[1].GetTensorDataAsSpan<float>().ToArray());
}

void Parity(ModelSpec m)
{
    var e = m.Entry;
    string refDir = Path.Combine(outDir, "ref", m.Size!);
    // COCO 사전학습 가중치는 배경 칸이 없는 희소 COCO 번호(마지막 칸이 90번 클래스)라 null. 파인튜닝한 모델은 마지막 칸(-1)이 배경
    int? background = e.TryGetProperty("background_class_id", out var b) && b.ValueKind == JsonValueKind.Number ? b.GetInt32() : null;
    using var so = AppOptions();
    using var s = new InferenceSession(m.Path, so);
    string detsName = s.OutputNames.First(nm => nm.Contains("dets"));
    string labelsName = s.OutputNames.First(nm => nm.Contains("labels"));
    var dims = s.OutputMetadata[labelsName].Dimensions;
    int q = dims[1], c = dims[2];
    var buf = new float[3 * m.Res * m.Res];
    using var v = OrtValue.CreateTensorValueFromMemory(buf, [1, 3, m.Res, m.Res]);

    var raw = new Agree();
    var pipeF = new Agree();
    var pipe8 = new Agree();
    double maxBox = 0, maxBoxAnyOrder = 0, maxScore = 0, maxInF = 0, maxIn8 = 0, preF = 0, pre8 = 0, post = 0;
    double preFVid = 0, pre8Vid = 0;
    int nVid = 0;
    Prep.RfDetr(images[0], m.Res, viaFloat: true);   // 첫 호출(OpenCV 초기화·JIT)을 시간에서 뺀다
    Prep.RfDetr(images[0], m.Res, viaFloat: false);
    var sw = new Stopwatch();
    for (int i = 0; i < names.Count; i++)
    {
        var img = images[i];
        int w = img.Width, h = img.Height;
        var refImg = e.GetProperty("images").GetProperty(names[i]);
        var torchDets = Rf.ParseDets(refImg.GetProperty("torch"));
        var cpuDets = Rf.ParseDets(refImg.GetProperty("onnx_cpu"));

        // (a) 파이썬이 만든 입력 텐서 그대로 → 런타임 차이만 본다
        var x = Rf.ReadF32(Path.Combine(refDir, names[i] + ".in.f32"));
        x.CopyTo(buf, 0);
        var (d, l) = RunRf(s, v, detsName, labelsName);
        var refD = Rf.ReadF32(Path.Combine(refDir, names[i] + ".dets.f32"));
        maxBox = Math.Max(maxBox, Rf.MaxAbs(d, refD));
        maxBoxAnyOrder = Math.Max(maxBoxAnyOrder, Rf.MaxNearestBoxDiff(d, refD));
        maxScore = Math.Max(maxScore, Rf.MaxSigmoidDiff(l, Rf.ReadF32(Path.Combine(refDir, names[i] + ".labels.f32"))));
        raw.Add(cpuDets, Rf.Decode(d, l, q, c, w, h, background));

        // (b) C# 전처리 → DirectML → C# 후처리 — 앱에 넣을 경로 전체
        sw.Restart();
        var xf = Prep.RfDetr(img, m.Res, viaFloat: true);
        double tf = sw.Elapsed.TotalMilliseconds;
        sw.Restart();
        var x8 = Prep.RfDetr(img, m.Res, viaFloat: false);
        double t8 = sw.Elapsed.TotalMilliseconds;
        preF += tf;
        pre8 += t8;
        if (names[i].StartsWith("vid_")) { preFVid += tf; pre8Vid += t8; nVid++; }   // 영상 프레임(1280×720·854) = 앱이 실제로 받는 크기
        maxInF = Math.Max(maxInF, Rf.MaxAbs(xf, x));
        maxIn8 = Math.Max(maxIn8, Rf.MaxAbs(x8, x));
        xf.CopyTo(buf, 0);
        var (df, lf) = RunRf(s, v, detsName, labelsName);
        sw.Restart();
        var detsF = Rf.Decode(df, lf, q, c, w, h, background);
        post += sw.Elapsed.TotalMilliseconds;
        pipeF.Add(torchDets, detsF);
        x8.CopyTo(buf, 0);
        var (d8, l8) = RunRf(s, v, detsName, labelsName);
        pipe8.Add(torchDets, Rf.Decode(d8, l8, q, c, w, h, background));
    }
    int k = names.Count, kv = Math.Max(1, nVid);
    // 쿼리 번호별 비교는 인코더의 상위 300 제안(TopK) 순서가 조금만 바뀌어도 커진다 — 순서를 무시한 값(가장 가까운 박스와의 차)을 같이 본다
    Console.WriteLine($"  ③(a) 같은 입력 텐서 → DirectML vs 파이썬 ONNX Runtime(CPU): 쿼리 번호별 박스 차 최대 {maxBox:G3} · 순서 무시 {maxBoxAnyOrder:G3} (정규화 좌표) · 점수 차 최대 {maxScore:G3}");
    Console.WriteLine($"       검출(임계 {Rf.Threshold}) {raw}");
    Console.WriteLine("  ③(b) C# 전처리(OpenCV 쌍선형) → DirectML → C# 후처리 vs PyTorch predict()");
    Console.WriteLine($"       float 에서 리사이즈: 입력 차 최대 {maxInF:G3} (정규화 뒤) · 전처리 {preF / k:F2} ms (영상 프레임 {preFVid / kv:F2}) · 검출 {pipeF}");
    Console.WriteLine($"       uint8 에서 리사이즈: 입력 차 최대 {maxIn8:G3} (정규화 뒤) · 전처리 {pre8 / k:F2} ms (영상 프레임 {pre8Vid / kv:F2}) · 검출 {pipe8}");
    Console.WriteLine($"       후처리(시그모이드 · 쿼리×클래스 상위 {q} · 박스 변환) {post / k:F2} ms");
}

string OutFile(string relative) => Path.Combine(outDir, relative.Replace('/', Path.DirectorySeparatorChar));

static string FindHere()
{
    for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        if (File.Exists(Path.Combine(dir.FullName, "export_rfdetr.py"))) return dir.FullName;
    throw new DirectoryNotFoundException("experiments/RfDetrBench(export_rfdetr.py 가 있는 폴더)를 찾지 못했습니다.");
}

record ModelSpec(string Name, string Path, int Res, string? Size, JsonElement Entry);

sealed record Timing(double LoadSec, double Median, double P95, double Mean, double RepeatMean)
{
    public override string ToString() =>
        $"세션 생성 {LoadSec:F1}초 · 이미지별 중앙 {Median:F2} ms · p95 {P95:F2} · 평균 {Mean:F2} · 같은 입력 200회 평균 {RepeatMean:F2} ms";
    public string Short() => $"{LoadSec,4:F1}초 {Median,6:F2} {P95,6:F2} {RepeatMean,6:F2}";
}

record struct Box(float X1, float Y1, float X2, float Y2, int Cls, float Score);

/// <summary>검출 맞춤 — 같은 클래스·IoU 0.9 이상을 신뢰도 높은 기준부터 탐욕적으로 짝짓는다 (export_rfdetr.py 의 match 와 같게)</summary>
sealed class Agree
{
    int _ref, _test, _matched;
    double _maxScoreDiff;

    public void Add(List<Box> reference, List<Box> test)
    {
        _ref += reference.Count;
        _test += test.Count;
        var used = new bool[test.Count];
        foreach (var r in reference.OrderByDescending(r => r.Score))
        {
            int best = -1;
            double bestIou = 0.9;
            for (int j = 0; j < test.Count; j++)
            {
                if (used[j] || test[j].Cls != r.Cls) continue;
                double v = Rf.Iou(r, test[j]);
                if (v >= bestIou) (best, bestIou) = (j, v);
            }
            if (best < 0) continue;
            used[best] = true;
            _matched++;
            _maxScoreDiff = Math.Max(_maxScoreDiff, Math.Abs(r.Score - test[best].Score));
        }
    }

    public override string ToString() =>
        $"기준 {_ref} · 이쪽 {_test} · 짝 {_matched} ({100.0 * _matched / Math.Max(1, _ref):F1}%) · 신뢰도 차 최대 {_maxScoreDiff:F4}";
}

static class Rf
{
    public const float Threshold = 0.3f;   // 앱의 ConfidenceThreshold

    /// <summary>
    /// rfdetr 공식 디코더(export/_runtime/decode.py)와 같게: 클래스마다 시그모이드(±88 로 자름) → 쿼리×클래스 쌍 전체에서 점수 상위 Q 개 →
    /// 임계값 초과만 → 정규화 cxcywh 를 화소 xyxy 로 (이미지 안으로 자름). 상위 Q 개를 고른 뒤 임계값으로 거르는 것은
    /// 임계값을 넘는 쌍에서 상위 Q 개를 고르는 것과 같다
    /// </summary>
    public static List<Box> Decode(float[] dets, float[] logits, int q, int c, int w, int h, int? background)
    {
        int bg = background is int bgi ? (bgi < 0 ? c + bgi : bgi) : -1;
        var cand = new List<(float Score, int Q, int Cls)>();
        for (int i = 0; i < q; i++)
            for (int j = 0; j < c; j++)
            {
                if (j == bg) continue;
                float s = 1f / (1f + MathF.Exp(-Math.Clamp(logits[i * c + j], -88f, 88f)));
                if (s > Threshold) cand.Add((s, i, j));
            }
        cand.Sort((a, b) => b.Score != a.Score ? b.Score.CompareTo(a.Score) : (a.Q * c + a.Cls).CompareTo(b.Q * c + b.Cls));
        var list = new List<Box>(Math.Min(q, cand.Count));
        foreach (var (score, i, cls) in cand.Take(q))
        {
            float cx = dets[i * 4], cy = dets[i * 4 + 1], bw = dets[i * 4 + 2], bh = dets[i * 4 + 3];
            list.Add(new Box(Math.Clamp((cx - bw / 2) * w, 0, w), Math.Clamp((cy - bh / 2) * h, 0, h),
                             Math.Clamp((cx + bw / 2) * w, 0, w), Math.Clamp((cy + bh / 2) * h, 0, h), cls, score));
        }
        return list;
    }

    public static List<Box> ParseDets(JsonElement arr) =>
        arr.EnumerateArray().Select(d =>
        {
            var b = d.GetProperty("xyxy").EnumerateArray().Select(v => v.GetSingle()).ToArray();
            return new Box(b[0], b[1], b[2], b[3], d.GetProperty("class_id").GetInt32(), d.GetProperty("score").GetSingle());
        }).ToList();

    public static double Iou(Box a, Box b)
    {
        double ix = Math.Max(0, Math.Min(a.X2, b.X2) - Math.Max(a.X1, b.X1));
        double iy = Math.Max(0, Math.Min(a.Y2, b.Y2) - Math.Max(a.Y1, b.Y1));
        double inter = ix * iy;
        double union = (a.X2 - a.X1) * (a.Y2 - a.Y1) + (b.X2 - b.X1) * (b.Y2 - b.Y1) - inter;
        return union > 0 ? inter / union : 0;
    }

    public static float[] ReadF32(string path) => MemoryMarshal.Cast<byte, float>(File.ReadAllBytes(path)).ToArray();

    public static double MaxAbs(float[] a, float[] b)
    {
        if (a.Length != b.Length) throw new InvalidDataException($"길이가 다름 {a.Length} vs {b.Length}");
        double m = 0;
        for (int i = 0; i < a.Length; i++) m = Math.Max(m, Math.Abs(a[i] - b[i]));
        return m;
    }

    /// <summary>a 의 박스마다 b 에서 가장 가까운 박스(좌표 4개 중 가장 큰 차가 가장 작은 것)와의 차 — 그 가운데 최대. 쿼리 순서만 다르면 0 에 가깝다</summary>
    public static double MaxNearestBoxDiff(float[] a, float[] b)
    {
        int n = a.Length / 4;
        double worst = 0;
        for (int i = 0; i < n; i++)
        {
            double best = double.MaxValue;
            for (int j = 0; j < n && best > 0; j++)
            {
                double dmax = 0;
                for (int k = 0; k < 4; k++) dmax = Math.Max(dmax, Math.Abs(a[i * 4 + k] - b[j * 4 + k]));
                best = Math.Min(best, dmax);
            }
            worst = Math.Max(worst, best);
        }
        return worst;
    }

    public static double MaxSigmoidDiff(float[] a, float[] b)
    {
        if (a.Length != b.Length) throw new InvalidDataException($"길이가 다름 {a.Length} vs {b.Length}");
        double m = 0;
        for (int i = 0; i < a.Length; i++)
            m = Math.Max(m, Math.Abs(1 / (1 + Math.Exp(-a[i])) - 1 / (1 + Math.Exp(-b[i]))));
        return m;
    }
}

static class Prep
{
    static readonly float[] Mean = [0.485f, 0.456f, 0.406f];
    static readonly float[] Std = [0.229f, 0.224f, 0.225f];

    /// <summary>
    /// RF-DETR 전처리: BGR → RGB → 정사각형으로 그냥 늘이기(레터박스 없음, 쌍선형·반 화소 중심·antialias 없음) → ImageNet 정규화 → CHW.
    /// 공식(torchvision)은 0~1 float 로 바꾼 뒤 리사이즈한다(viaFloat). uint8 에서 먼저 줄이면 빠르지만 고정소수점 보간이라 값이 조금 다르다
    /// </summary>
    public static unsafe float[] RfDetr(Mat bgr, int res, bool viaFloat)
    {
        using var rgb = new Mat();
        Cv2.CvtColor(bgr, rgb, ColorConversionCodes.BGR2RGB);
        using var resized = new Mat();
        if (viaFloat)
        {
            using var f = new Mat();
            rgb.ConvertTo(f, MatType.CV_32FC3, 1.0 / 255);
            Cv2.Resize(f, resized, new OpenCvSharp.Size(res, res), 0, 0, InterpolationFlags.Linear);
        }
        else
        {
            using var r8 = new Mat();
            Cv2.Resize(rgb, r8, new OpenCvSharp.Size(res, res), 0, 0, InterpolationFlags.Linear);
            r8.ConvertTo(resized, MatType.CV_32FC3, 1.0 / 255);
        }
        int plane = res * res;
        var dst = new float[3 * plane];
        float* p = (float*)resized.DataPointer;
        for (int i = 0; i < plane; i++, p += 3)
        {
            dst[i] = (p[0] - Mean[0]) / Std[0];
            dst[plane + i] = (p[1] - Mean[1]) / Std[1];
            dst[2 * plane + i] = (p[2] - Mean[2]) / Std[2];
        }
        return dst;
    }

    /// <summary>YOLO26s 기준 입력 — 앱(YoloDotNet ResizeImageProportional)과 같게 비율 유지·가운데·쌍선형·검은 여백, RGB 0~1 CHW (NpuBench 와 같음).
    /// 2026-11-05 앱이 쌍선형으로 바꿀 때 같이 바꿨다(그 전엔 INTER_NEAREST_EXACT) — 이 실험은 YOLO 를 시간 기준으로만 써서 1단계 숫자는 그대로다</summary>
    public static unsafe float[] Letterbox(Mat src, int side)
    {
        int w = src.Width, h = src.Height;
        if (w >= side || h >= side)
        {
            float s = Math.Min((float)side / src.Width, (float)side / src.Height);
            (w, h) = ((int)(src.Width * s + 0.5f), (int)(src.Height * s + 0.5f));
        }
        using var canvas = new Mat(side, side, MatType.CV_8UC3, Scalar.All(0));
        using (var resized = (w, h) == (src.Width, src.Height) ? src.Clone() : src.Resize(new OpenCvSharp.Size(w, h), 0, 0, InterpolationFlags.Linear))
        using (var roi = new Mat(canvas, new Rect((side - w) / 2, (side - h) / 2, w, h)))
            resized.CopyTo(roi);
        int plane = side * side;
        var dst = new float[3 * plane];
        byte* p = (byte*)canvas.DataPointer;
        for (int i = 0; i < plane; i++, p += 3)
        {
            dst[i] = p[2] / 255f;
            dst[plane + i] = p[1] / 255f;
            dst[2 * plane + i] = p[0] / 255f;
        }
        return dst;
    }

    /// <summary>ORT 프로파일 JSON 에서 노드를 실행 공급자별로 센다 (NpuBench 의 SummarizeProfile 과 같음). DirectML 은 맡은 부분을 노드 몇 개로 묶어 보인다</summary>
    public static string SummarizeProfile(string file, int runs)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(file));
        var byProv = new SortedDictionary<string, (HashSet<string> Nodes, long Us, SortedDictionary<string, int> Ops)>();
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            if (!e.TryGetProperty("cat", out var cat) || cat.GetString() != "Node") continue;
            string name = e.GetProperty("name").GetString() ?? "";
            if (!name.EndsWith("_kernel_time")) continue;
            if (!e.TryGetProperty("args", out var a) || !a.TryGetProperty("provider", out var p)) continue;
            string prov = p.GetString() ?? "?";
            string op = a.TryGetProperty("op_name", out var on) ? on.GetString() ?? "?" : "?";
            if (!byProv.TryGetValue(prov, out var entry))
                entry = (new HashSet<string>(), 0, new SortedDictionary<string, int>());
            if (entry.Nodes.Add(name)) entry.Ops[op] = entry.Ops.GetValueOrDefault(op) + 1;
            entry.Us += e.GetProperty("dur").GetInt64();
            byProv[prov] = entry;
        }
        return string.Join("\n", byProv.Select(kv =>
            $"     {kv.Key,-26} 노드 {kv.Value.Nodes.Count,4}개, 1회당 {kv.Value.Us / 1000.0 / runs,7:F2} ms — " +
            string.Join(", ", kv.Value.Ops.OrderByDescending(o => o.Value).Take(12).Select(o => $"{o.Key}×{o.Value}"))));
    }
}
