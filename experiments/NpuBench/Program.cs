// best.onnx 를 AMD NPU(Windows ML · VitisAI EP)로 돌려 본다.
// 재는 것: ① NPU 에 실제로 어떤 노드가 올라가는지 ② 추론 시간(NPU·GPU·CPU) ③ GPU 결과와 검출 일치도.
// 모든 백엔드에 같은 입력 텐서를 넣으므로 차이는 순수하게 모델(양자화)·백엔드(정밀도·연산 배치) 탓이다.
// 전처리는 직접 하되 본 앱의 YoloDotNet(ResizeImageProportional)과 같게 한다 — 비율 유지·가운데·Nearest·검은 여백.
// (2026-10-24 오전 측정은 Ultralytics 식 — 선형 보간·회색 114 여백 — 이었다. README 의 첫 결과표가 그것)
//
// 실행: NpuBench.exe [기준 모델] [NPU 모델] [영상당 프레임 수(기본 20)] [--allow-fp32-npu] [--allow-mismatch]
//   기준 모델(GPU)의 기본값은 저장소 루트 best.onnx, NPU 모델의 기본값은 기준 옆 <이름>.npu.onnx(tools/Make-NpuModel.ps1 이 만드는
//   양자화 짝 — 없으면 NPU 는 건너뛴다). CPU 도 NPU 모델로 돌려 "양자화 때문에 달라진 것"(CPU 양자화 vs GPU)과
//   "NPU 라서 달라진 것"(NPU vs CPU 양자화)을 나눠 본다.
//   벤치 전에 NPU 모델 메타데이터를 앱의 CheckNpuModel 과 같은 규칙으로 본다 (이슈 #6 — 예전엔 NPU 모델 기본값이 기준 FP32 자체였다):
//   - npu_quant 가 없으면(FP32) NPU 단계를 건너뛴다. FP32 를 NPU 에 넣으면 EP 가 BF16 으로 바꿔 컴파일하다 실패하고, 약 1.5시간 뒤
//     EP 가 프로세스를 끝낸다(README 함정 1 — 예외가 아니라서 Bench 의 try/catch 로도 못 잡는다). 일부러 시험할 때만 --allow-fp32-npu
//   - npu_source_md5(원본 파일 MD5)가 기준 모델 MD5 와 다르면 멈춘다 — 양자화 영향에 모델 차이가 섞인다. 일부러 비교할 때만 --allow-mismatch
// NPU 노드 배치 보고서를 받으려면 실행 전에 XLNX_ONNX_EP_REPORT_FILE 환경 변수를 셸에서 지정한다
// (네이티브 EP 가 프로세스 시작 때의 환경을 읽으므로 코드에서 지정하면 늦을 수 있다).

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Microsoft.Windows.AI.MachineLearning;
using OpenCvSharp;

const int Side = 640;          // 모델 입력 고정 [1,3,640,640] (CLAUDE.md 3번)
const InterpolationFlags NearestExact = (InterpolationFlags)6;   // INTER_NEAREST_EXACT — OpenCvSharp 열거형엔 이름이 없다

string repo = FindRepoRoot();
// 위치 인자(기준 모델·NPU 모델·영상당 프레임 수)는 예전 그대로다 — tools/Make-NpuModel.ps1 -Bench 가 셋 다 넘긴다. -- 옵션은 어디에 둬도 된다
var positional = new List<string>();
bool allowFp32Npu = false, allowMismatch = false;
foreach (var a in args)
{
    if (a == "--allow-fp32-npu") allowFp32Npu = true;
    else if (a == "--allow-mismatch") allowMismatch = true;
    else if (a is "-h" or "--help" or "/?") { PrintUsage(); return 0; }
    else if (a.StartsWith("--")) { Console.WriteLine($"모르는 옵션: {a}\n"); PrintUsage(); return 2; }
    else positional.Add(a);
}
int perVideo = 20;
if (positional.Count > 3 || (positional.Count > 2 && (!int.TryParse(positional[2], out perVideo) || perVideo <= 0)))
{
    Console.WriteLine($"인자가 잘못됐습니다: {string.Join(" ", args)}\n");
    PrintUsage();
    return 2;
}
string refModel = positional.Count > 0 ? Path.GetFullPath(positional[0]) : Path.Combine(repo, "best.onnx");
// NPU 모델 기본값 = 기준 옆 짝 (tools/Make-NpuModel.ps1·앱이 쓰는 이름). 예전 기본값(기준 자체)은 FP32 를 NPU 에 넣었다 (이슈 #6)
string pairModel = Path.ChangeExtension(refModel, ".npu.onnx");
bool npuGiven = positional.Count > 1;
string? npuModel = npuGiven ? Path.GetFullPath(positional[1]) : File.Exists(pairModel) ? pairModel : null;
if (!File.Exists(refModel)) { Console.WriteLine($"기준 모델이 없습니다: {refModel}"); return 1; }
if (npuModel != null && !File.Exists(npuModel)) { Console.WriteLine($"NPU 모델이 없습니다: {npuModel}"); return 1; }
string outDir = Path.Combine(repo, "experiments", "NpuBench", "out");
Directory.CreateDirectory(outDir);
// VitisAI EP 가 작업 디렉터리에 서명 파일(original-*-signature.txt)을 떨군다 — 저장소 루트를 어지럽히지 않게
Environment.CurrentDirectory = outDir;
var runOptions = new RunOptions();

// ── 1. 모델 정보 · 짝 확인 ──
// EP 준비(처음엔 내려받느라 수 분)보다 먼저 본다 — 모델을 잘못 줬으면 기다리지 않고 바로 알린다
string refMd5 = Md5(refModel);
if (DescribeModel(refModel, refMd5) is not { } refMeta) return 1;
if (refMeta.TryGetValue("npu_quant", out var refQuant))
    Console.WriteLine($"  ※ 기준 모델이 양자화 모델입니다 (npu_quant = {refQuant}). 첫 인자는 FP32 원본, NPU 모델은 두 번째 인자입니다");
string npuMd5 = "";
Dictionary<string, string> npuMeta = [];
if (npuModel != null)
{
    npuMd5 = Md5(npuModel);
    if (npuMd5 == refMd5) npuMeta = refMeta;   // 같은 파일이면 한 번만 보인다
    else if (DescribeModel(npuModel, npuMd5) is { } meta) npuMeta = meta;
    else return 1;
}

string? npuSkip = null;   // NPU 단계를 건너뛰는 사유 — 요약 뒤에 한 번 더 찍는다
Console.WriteLine($"\n[짝 확인] NPU 모델 {(npuModel == null ? "없음" : Path.GetFileName(npuModel))}{(npuGiven ? "" : " (기본값: 기준 옆 짝)")}");
if (npuModel == null)
{
    npuSkip = $"NPU 모델 없음 ({Path.GetFileName(pairModel)})";
    Console.WriteLine($"  ※ NPU 단계를 건너뜁니다: 기준 모델 옆에 {Path.GetFileName(pairModel)} 가 없습니다 — GPU·CPU(기준 FP32)만 잽니다.");
    Console.WriteLine($"     짝 만들기: pwsh tools\\Make-NpuModel.ps1 -Model \"{refModel}\" (아니면 NPU 모델을 두 번째 인자로 준다)");
}
else
{
    bool quantized = npuMeta.TryGetValue("npu_quant", out var quant);
    if (npuMd5 == refMd5)
        Console.WriteLine("  기준 모델과 같은 파일입니다 (MD5 같음)");
    else if (!npuMeta.TryGetValue("npu_source_md5", out var source) ||
             !string.Equals(source, refMd5, StringComparison.OrdinalIgnoreCase))
    {
        // 앱의 CheckNpuModel 과 같은 규칙(MD5 는 대소문자 무시). 다른 원본과 비교하면 양자화·NPU 영향에 모델 차이가 섞이고,
        // 클래스 구성이 다르면(예: COCO 80클래스) 같은 클래스 번호가 다른 물체를 가리킨다
        Console.WriteLine(source == null
            ? "  ※ 짝인지 알 수 없습니다: 메타데이터에 원본 MD5(npu_source_md5)가 없습니다 (tools\\npu_quantize.py 로 만든 모델에만 들어 있다)"
            : $"  ※ 짝이 아닙니다: 다른 원본에서 만든 NPU 모델입니다 (npu_source_md5 {Short(source)} ≠ 기준 모델 MD5 {Short(refMd5)})");
        Console.WriteLine("     다른 모델끼리 비교하면 양자화·NPU 의 영향과 모델 차이가 섞인 숫자가 나옵니다.");
        if (source != null && FindSourceModel(npuModel, source) is { } found)
            Console.WriteLine($"     이 NPU 모델의 원본: {found} — 이것을 첫 인자(기준)로 주세요");
        Console.WriteLine($"     기준 모델의 짝 만들기: pwsh tools\\Make-NpuModel.ps1 -Model \"{refModel}\" · 일부러 다른 모델과 비교하려면 --allow-mismatch");
        if (!allowMismatch) return 1;
        Console.WriteLine("  --allow-mismatch: 그대로 비교합니다");
    }
    else
        Console.WriteLine($"  원본 MD5 가 기준 모델과 같습니다 → 짝이 맞습니다{(quantized ? $" (양자화 {quant})" : "")}");

    if (!quantized && allowFp32Npu)
        Console.WriteLine("  ※ --allow-fp32-npu: 양자화 모델이 아닌데(npu_quant 없음) NPU 에 넣습니다 — EP 1.8.75 에선 BF16 컴파일이 실패하고 약 1.5시간 뒤 프로세스가 끝났습니다");
    else if (!quantized)
    {
        npuSkip = "양자화 모델이 아님 (npu_quant 없음 — FP32)";
        Console.WriteLine("  ※ NPU 단계를 건너뜁니다: 양자화 모델이 아닙니다 (메타데이터에 npu_quant 없음 — FP32 로 본다).");
        Console.WriteLine("     FP32 를 NPU 에 넣으면 EP 가 BF16 으로 바꿔 컴파일하다 실패하고 약 1.5시간 뒤 프로세스를 끝냅니다 (README 함정 1).");
        Console.WriteLine("     NPU 모델은 tools\\Make-NpuModel.ps1 로 만든 짝(<이름>.npu.onnx)을 주세요. 일부러 시험하려면 --allow-fp32-npu");
    }
}

// ── 2. Windows ML 실행 공급자 준비 ──
var env = OrtEnv.Instance();
var catalog = ExecutionProviderCatalog.GetDefault();
Console.WriteLine("\n[EP 카탈로그]");
foreach (var p in catalog.FindAllProviders())
    Console.WriteLine($"  {p.Name}: {p.ReadyState}");
var swInit = Stopwatch.StartNew();
await catalog.EnsureAndRegisterCertifiedAsync();   // 처음엔 EP 를 내려받아 수 분 걸린다
Console.WriteLine($"  등록 {swInit.Elapsed.TotalSeconds:F1}초");
// NPU 컴파일 캐시 키에 넣을 버전 (이 절 끝). EP 패키지 ID 는 준비가 끝난 뒤의 목록에서 읽는다
var vaiProvider = catalog.FindAllProviders().FirstOrDefault(p => p.Name == "VitisAIExecutionProvider");
string epVersion = vaiProvider == null ? "unknown" : EpVersion(vaiProvider);
string ortVersion = OrtVersion(env);
Console.WriteLine($"  ONNX Runtime {ortVersion} · VitisAI EP {(vaiProvider == null ? "없음" : epVersion)}");

Console.WriteLine("\n[EP 장치]");
var devices = env.GetEpDevices();
foreach (var d in devices)
    Console.WriteLine($"  {d.EpName,-26} {d.HardwareDevice.Type,-4} {d.HardwareDevice.Vendor} " +
                      $"(vendor 0x{d.HardwareDevice.VendorId:X4}, device 0x{d.HardwareDevice.DeviceId:X4})");

// 하이브리드 노트북이라 DirectML 장치가 AMD 내장·NVIDIA 둘이다. 본 앱과 같이 NVIDIA 를 기준으로 삼는다
var dml = devices.FirstOrDefault(d => d.EpName == "DmlExecutionProvider" && d.HardwareDevice.VendorId == 0x10DE)
          ?? devices.FirstOrDefault(d => d.EpName == "DmlExecutionProvider");
var vai = devices.FirstOrDefault(d => d.EpName == "VitisAIExecutionProvider" && d.HardwareDevice.Type == OrtHardwareDeviceType.NPU);
if (dml == null) { Console.WriteLine("DirectML 장치가 없어 기준(GPU) 결과를 만들 수 없습니다."); return 1; }
if (vai == null) Console.WriteLine("  ※ VitisAI NPU 장치가 없습니다 — NPU 는 건너뜁니다.");

// NPU 컴파일 캐시 키를 직접 준다 = NPU 모델 MD5 + VitisAI EP 패키지 버전 + ONNX Runtime 버전.
// MD5: 기본 키는 그래프 구조만 보는지 가중치·스케일만 다른 양자화 변형끼리 같은 키가 나와, 처음 컴파일한 모델을 그대로
// 재사용했다 (2026-10-24 실측: a8w8-pct 를 넣어도 a8w8 결과가 나옴, a16w8 은 INT8 로 컴파일된 옛 모델에 16비트가 들어가
// "Not support data type (0,16) to (0,8)" 로 종료). 버전(이슈 #5): EP 가 Windows Update 로 바뀌거나 런타임(Windows ML 의 ORT)을
// 올리면 EP 가 받는 그래프·컴파일러가 달라질 수 있는데, 키가 같으면 VitisAI 는 다시 컴파일하지 않고 옛 결과를 쓴다
// (키가 MD5 뿐이던 때의 out/vaip_cache/<MD5> 폴더는 이제 쓰이지 않는다 — 지워도 된다)
string npuCacheDir = Path.Combine(outDir, "vaip_cache");
string npuCacheKey = $"{npuMd5}_{KeyPart(epVersion)}_{KeyPart(ortVersion)}";
if (vai != null && npuModel != null && npuSkip == null)
{
    bool cached;
    try { cached = Directory.EnumerateFiles(Path.Combine(npuCacheDir, npuCacheKey), "*", SearchOption.AllDirectories).Any(); }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { cached = false; }   // 키 폴더 없음 — 문구만 달라진다
    Console.WriteLine($"  NPU 컴파일 캐시 키 {npuCacheKey} — {(cached ? "캐시 있음" : "캐시 없음 (첫 NPU 세션에서 컴파일 — 양자화 모델은 약 1분)")}");
    if (epVersion == "unknown" || ortVersion == "unknown")
        Console.WriteLine("  ※ 버전을 몰라 키에 unknown 이 들어갔습니다 — EP·런타임이 바뀐 뒤엔 out\\vaip_cache 를 지우고 재세요");
}

// ── 3. 테스트 프레임 (영상마다 고르게 뽑는다. tools/npu_quantize.py 의 보정 프레임(i/n 위치)과 겹치지 않게 (i+0.5)/n 위치) ──
// test4·test5 가 유고 감지 기준 영상이라 앞에 둔다 (작은 사람·갓길 정차 화물차가 들어 있음)
var frames = new List<(string Video, int Index, Mat Img)>();
foreach (var v in new[] { "test4.mp4", "test5.mp4", "test1.mp4", "test2.mp4", "test3.mp4" })
{
    string path = Path.Combine(repo, "publish", v);
    if (!File.Exists(path)) { Console.WriteLine($"  {v} 없음 — 건너뜀"); continue; }
    using var cap = new VideoCapture(path);
    int count = (int)cap.Get(VideoCaptureProperties.FrameCount);
    using var raw = new Mat();
    for (int i = 0; i < perVideo; i++)
    {
        int idx = (int)((i + 0.5) * count / perVideo);
        cap.Set(VideoCaptureProperties.PosFrames, idx);
        if (cap.Read(raw) && !raw.Empty()) frames.Add((v, idx, Letterbox(raw)));
    }
}
Console.WriteLine($"\n[프레임] {frames.Count}장 ({string.Join(", ", frames.GroupBy(f => f.Video).Select(g => $"{g.Key} {g.Count()}"))})");
if (frames.Count == 0) return 1;

// ── 4. 백엔드별 실행 ──
// NPU 는 마지막에 돈다: 컴파일이 길고, 실패하면 EP 가 프로세스째 끝내 버린 적이 있어서(종료 코드 255)
// 앞 백엔드 결과는 끝날 때마다 바로 찍어 둔다.
// NPU 모델이 없으면 CPU 는 기준 FP32 로 돈다 (GPU 와 같은 모델 — 백엔드 차이만 보인다)
string cpuModel = npuModel ?? refModel;
bool cpuIsRef = npuModel == null || npuMd5 == refMd5;
var results = new List<RunResult>();

var gpu = Bench($"GPU (DirectML) · {Path.GetFileName(refModel)}", "gpu", refModel, () =>
{
    // DirectML EP 는 메모리 패턴·병렬 실행을 지원하지 않는다
    var so = new SessionOptions { EnableMemoryPattern = false, ExecutionMode = ExecutionMode.ORT_SEQUENTIAL };
    so.AppendExecutionProvider(env, [dml], new Dictionary<string, string>());
    return so;
}, passes: 3);
if (gpu == null) return 1;   // 기준이 없으면 비교할 수 없다

Bench($"CPU · {Path.GetFileName(cpuModel)}", "cpu", cpuModel, () => new SessionOptions(), passes: 1);

// 1절 짝 확인을 통과했을 때만 (FP32 는 --allow-fp32-npu, 원본이 다른 짝은 --allow-mismatch 일 때만 여기 온다)
if (vai != null && npuModel != null && npuSkip == null)
    Bench($"NPU (VitisAI) · {Path.GetFileName(npuModel)}", "npu", npuModel, () =>
    {
        var so = new SessionOptions();
        // 컴파일 결과를 디스크에 남겨야(=0) 두 번째 세션부터 캐시를 쓰고, 노드 배치 보고서도 생긴다
        so.AppendExecutionProvider(env, [vai], new Dictionary<string, string>
        {
            ["cache_dir"] = npuCacheDir,
            ["cache_key"] = npuCacheKey,   // 2절 끝 주석
            ["enable_cache_file_io_in_mem"] = "0",
        });
        return so;
    }, passes: 3);

// ── 5. 요약 ──
Console.WriteLine("\n================ 추론 시간 요약 (session.Run 만, 전처리 제외) ================");
Console.WriteLine($"{"세션 첫 생성",10} {"재생성",8} {"평균",8} {"중앙",8} {"p95",8} {"같은 입력 100회",14} {"CPU 시간/회",12}  백엔드");
foreach (var r in results) Console.WriteLine(r.TimingLine());

var cpuInt8 = results.FirstOrDefault(r => r.Tag == "cpu");
var npu = results.FirstOrDefault(r => r.Tag == "npu");
if (npu != null && cpuInt8 != null && !cpuIsRef)
{
    Console.WriteLine($"\n[NPU vs CPU — 같은 INT8 모델: NPU 실행 자체의 차이]");
    PrintCompare(cpuInt8, npu);
}
// 건너뛴 사유는 1·2절에 찍었지만 그 뒤 벤치 로그에 묻히므로 끝에 한 번 더
if ((npuSkip ?? (vai == null ? "VitisAI NPU 장치 없음" : null)) is { } skipped)
    Console.WriteLine($"\n※ NPU 단계는 건너뛰었습니다: {skipped}");
return 0;

// 프로파일 세션(노드 배치 확인 — NPU 는 이때 처음 컴파일된다)과 측정 세션(NPU 는 캐시에서 로드)을 따로 만든다.
// 프로파일링을 켠 채로 시간을 재면 오버헤드가 섞이기 때문
RunResult? Bench(string name, string tag, string model, Func<SessionOptions> make, int passes)
{
    Console.WriteLine($"\n[{name}]");
    var r = new RunResult { Name = name, Tag = tag, Outputs = new float[frames.Count][] };
    var input = new float[3 * Side * Side];
    try
    {
        var sw = Stopwatch.StartNew();
        using (var so = make())
        {
            // EnableProfiling 이 켜질 때의 접두사를 쓰므로 접두사를 먼저 지정해야 한다 (반대로 하면 작업 디렉터리에 떨어진다)
            so.ProfileOutputPathPrefix = Path.Combine(outDir, "prof_" + tag);
            so.EnableProfiling = true;
            using var s = new InferenceSession(model, so);
            r.LoadSec = sw.Elapsed.TotalSeconds;
            Console.WriteLine($"  세션 생성 {r.LoadSec:F1}초");
            using var v = OrtValue.CreateTensorValueFromMemory(input, [1, 3, Side, Side]);
            Fill(frames[0].Img, input);
            for (int i = 0; i < 5; i++) RunOnce(s, v);
            r.Placement = SummarizeProfile(s.EndProfiling(), 5);
        }
        Console.WriteLine(r.Placement);

        sw.Restart();
        using var so2 = make();
        using var session = new InferenceSession(model, so2);
        r.ReloadSec = sw.Elapsed.TotalSeconds;
        using var value = OrtValue.CreateTensorValueFromMemory(input, [1, 3, Side, Side]);

        Fill(frames[0].Img, input);
        for (int i = 0; i < 10; i++) RunOnce(session, value);   // 워밍업

        for (int pass = 0; pass < passes; pass++)
            for (int f = 0; f < frames.Count; f++)
            {
                Fill(frames[f].Img, input);
                sw.Restart();
                var o = RunOnce(session, value);
                r.Ms.Add(sw.Elapsed.TotalMilliseconds);
                if (pass == 0) r.Outputs[f] = o;
            }

        // 같은 입력을 연달아 돌려 처리량과 CPU 사용량을 본다 (NPU 에서 CPU 로 넘어간 연산이 있으면 CPU 시간에 드러난다)
        Fill(frames[0].Img, input);
        var proc = Process.GetCurrentProcess();
        proc.Refresh();
        var cpu0 = proc.TotalProcessorTime;
        sw.Restart();
        for (int i = 0; i < 100; i++) RunOnce(session, value);
        r.RepeatMs = sw.Elapsed.TotalMilliseconds / 100;
        proc.Refresh();
        r.RepeatCpuMs = (proc.TotalProcessorTime - cpu0).TotalMilliseconds / 100;

        Console.WriteLine(r.TimingLine());
        results.Add(r);
        if (tag != "gpu")
        {
            Console.WriteLine($"  [GPU 대비 검출 일치도]");
            PrintCompare(results[0], r);   // GPU 가 실패하면 여기까지 오지 않는다
        }
        return r;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  실패: {ex.GetType().Name}: {ex.Message}");
        return null;
    }
}

void PrintCompare(RunResult reference, RunResult test)
{
    var all = new Stats();
    foreach (var g in Enumerable.Range(0, frames.Count).GroupBy(i => frames[i].Video))
    {
        var st = new Stats();
        foreach (int i in g) st.Add(reference.Outputs[i], test.Outputs[i]);
        all.Merge(st);
        Console.WriteLine($"    {g.Key,-10} {st}");
    }
    Console.WriteLine($"    {"전체",-10} {all}");
    Console.WriteLine($"    {"",-10} {all.Detail()}");
}

float[] RunOnce(InferenceSession s, OrtValue input)
{
    using var outs = s.Run(runOptions, [s.InputNames[0]], [input], [s.OutputNames[0]]);
    return outs[0].GetTensorDataAsSpan<float>().ToArray();
}

// YoloDotNet ResizeImageProportional 과 같게: 크기는 (int)(w·s + 0.5), 가운데, Nearest, 검은 여백(캔버스를 SKColors.Empty 로 지움).
// OpenCV 의 Nearest 는 화소 중심을 맞추지 않아 Skia 와 한 칸씩 어긋난다 → INTER_NEAREST_EXACT (위의 NearestExact).
// tools/npu_quantize.py 의 letterbox 와 같아야 한다
static Mat Letterbox(Mat src)
{
    int w = src.Width, h = src.Height;
    if (w >= Side || h >= Side)
    {
        float s = Math.Min((float)Side / src.Width, (float)Side / src.Height);
        (w, h) = ((int)(src.Width * s + 0.5f), (int)(src.Height * s + 0.5f));
    }
    var dst = new Mat(Side, Side, MatType.CV_8UC3, Scalar.All(0));
    using var resized = (w, h) == (src.Width, src.Height) ? src.Clone() : src.Resize(new OpenCvSharp.Size(w, h), 0, 0, NearestExact);
    using var roi = new Mat(dst, new Rect((Side - w) / 2, (Side - h) / 2, w, h));
    resized.CopyTo(roi);
    return dst;
}

// BGR HWC byte → RGB CHW float(0~1). tools/npu_quantize.py 의 letterbox 와 같아야 한다
static unsafe void Fill(Mat bgr, float[] dst)
{
    const int plane = Side * Side;
    byte* p = (byte*)bgr.DataPointer;
    for (int i = 0; i < plane; i++, p += 3)
    {
        dst[i] = p[2] / 255f;
        dst[plane + i] = p[1] / 255f;
        dst[2 * plane + i] = p[0] / 255f;
    }
}

// ORT 프로파일 JSON 에서 노드별 실행 공급자를 센다. VitisAI 는 NPU 로 보낸 부분을 노드 하나로 묶어 보인다
static string SummarizeProfile(string file, int runs)
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
        $"  {kv.Key,-26} 노드 {kv.Value.Nodes.Count,3}개, 1회당 {kv.Value.Us / 1000.0 / runs,7:F2} ms — " +
        string.Join(", ", kv.Value.Ops.OrderByDescending(o => o.Value).Take(12).Select(o => $"{o.Key}×{o.Value}"))));
}

// 모델 정보를 보이고 메타데이터를 돌려준다 (열지 못하면 사유를 찍고 null).
// 메타데이터만 읽으므로 CPU·그래프 최적화 끔 — 앱의 CheckNpuModel 과 같게
static Dictionary<string, string>? DescribeModel(string path, string md5)
{
    Console.WriteLine($"\n[모델] {path} ({new FileInfo(path).Length / 1e6:F1} MB, MD5 {md5})");
    try
    {
        using var so = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_DISABLE_ALL };
        using var probe = new InferenceSession(path, so);
        var meta = new Dictionary<string, string>(probe.ModelMetadata.CustomMetadataMap);
        // npu_* 는 tools/npu_quantize.py 가 넣는 짝 확인용 값 (1절)
        foreach (var key in new[] { "description", "date", "names", "npu_quant", "npu_source_md5", "npu_calib", "npu_quantizer" })
            if (meta.TryGetValue(key, out var v)) Console.WriteLine($"  {key} = {v}");
        foreach (var (n, m) in probe.InputMetadata) Console.WriteLine($"  입력 {n} [{string.Join(",", m.Dimensions)}] {m.ElementDataType}");
        foreach (var (n, m) in probe.OutputMetadata) Console.WriteLine($"  출력 {n} [{string.Join(",", m.Dimensions)}] {m.ElementDataType}");
        return meta;
    }
    catch (OnnxRuntimeException ex)
    {
        Console.WriteLine($"  모델을 열지 못했습니다: {ex.Message}");
        return null;
    }
}

// 짝이 안 맞을 때 무엇을 기준으로 줘야 하는지 알려 준다 — 원본 후보 가운데 MD5 가 npu_source_md5 와 같은 것.
// 후보: 같은 폴더의 짝 이름 원본(<이름>.npu.onnx → <이름>.onnx)·best.onnx, 저장소 루트·배포 폴더의 best.onnx
string? FindSourceModel(string npu, string sourceMd5)
{
    string dir = Path.GetDirectoryName(npu) ?? "";
    string name = Path.GetFileName(npu);
    var candidates = new List<string>();
    if (name.EndsWith(".npu.onnx", StringComparison.OrdinalIgnoreCase))
        candidates.Add(Path.Combine(dir, name[..^".npu.onnx".Length] + ".onnx"));
    candidates.AddRange([Path.Combine(dir, "best.onnx"), Path.Combine(repo, "best.onnx"), Path.Combine(repo, "publish", "best.onnx")]);
    return candidates.Distinct(StringComparer.OrdinalIgnoreCase)
        .FirstOrDefault(c => File.Exists(c) && string.Equals(Md5(c), sourceMd5, StringComparison.OrdinalIgnoreCase));
}

// tools/npu_quantize.py 의 npu_source_md5·앱의 CheckNpuModel 과 같은 형식 (대문자 16진수)
static string Md5(string path)
{
    using var fs = File.OpenRead(path);
    return Convert.ToHexString(MD5.HashData(fs));
}

static string Short(string md5) => md5.Length > 8 ? md5[..8] + "…" : md5;

// ONNX Runtime 버전 (캐시 키용): 관리 API 가 알려 주는 값. 못 얻으면 실제로 올라온 onnxruntime.dll 의 파일 버전
static string OrtVersion(OrtEnv env)
{
    try
    {
        string v = env.GetVersionString();
        if (!string.IsNullOrWhiteSpace(v)) return v;
    }
    catch (Exception) { }   // 아래 파일 버전으로
    var dll = Process.GetCurrentProcess().Modules.Cast<ProcessModule>()
        .FirstOrDefault(m => string.Equals(m.ModuleName, "onnxruntime.dll", StringComparison.OrdinalIgnoreCase));
    return dll?.FileVersionInfo.FileVersion is { Length: > 0 } fv ? fv : "unknown";
}

// VitisAI EP 버전 (캐시 키용): 카탈로그의 패키지 ID (앱과 같은 값). 못 읽으면 EP 라이브러리 파일의 버전·수정 시각 —
// unknown 으로 굳으면 EP 가 바뀌어도 키가 같아 옛 컴파일 결과를 쓴다 (이슈 #5)
static string EpVersion(ExecutionProvider ep)
{
    try
    {
        if (ep.PackageId?.Version is { } v) return $"{v.Major}.{v.Minor}.{v.Build}.{v.Revision}";
        if (ep.LibraryPath is { Length: > 0 } lib && File.Exists(lib))
            return $"{FileVersionInfo.GetVersionInfo(lib).FileVersion}-{File.GetLastWriteTimeUtc(lib):yyyyMMddHHmmss}";
    }
    catch (Exception ex) { Console.WriteLine($"  ※ VitisAI EP 버전을 못 읽었습니다: {ex.Message}"); }
    return "unknown";
}

// 캐시 키는 VitisAI 가 폴더 이름으로 쓴다 — 버전 문자열에 섞일 수 있는 공백·'+' 등은 '-' 로 (부분을 가르는 '_' 도 안에선 '-')
static string KeyPart(string s) => new string(s.Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' ? c : '-').ToArray());

static void PrintUsage() => Console.WriteLine(
    "사용법: NpuBench.exe [기준 모델] [NPU 모델] [영상당 프레임 수] [--allow-fp32-npu] [--allow-mismatch]\n" +
    "  기준 모델         GPU(DirectML) 로 돌릴 FP32 원본 (기본: 저장소 루트 best.onnx)\n" +
    "  NPU 모델          CPU·NPU 로 돌릴 양자화 짝 (기본: 기준 옆 <이름>.npu.onnx — 없으면 NPU 를 건너뛴다)\n" +
    "  영상당 프레임 수  publish\\test1~5.mp4 에서 뽑을 장 수 (기본 20)\n" +
    "  --allow-fp32-npu  양자화 모델이 아니어도(npu_quant 없음) NPU 에 넣는다 — BF16 컴파일 실패 경로를 일부러 볼 때만\n" +
    "  --allow-mismatch  NPU 모델의 원본(npu_source_md5)이 기준 모델과 달라도 비교한다");

static string FindRepoRoot()
{
    for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        if (File.Exists(Path.Combine(dir.FullName, "DetectView.csproj"))) return dir.FullName;
    throw new DirectoryNotFoundException("저장소 루트(DetectView.csproj)를 찾지 못했습니다.");
}

sealed class RunResult
{
    public string Name = "", Tag = "";
    public double LoadSec, ReloadSec, RepeatMs, RepeatCpuMs;
    public List<double> Ms = [];
    public float[][] Outputs = [];
    public string Placement = "";

    public string TimingLine()
    {
        var s = Ms.OrderBy(x => x).ToArray();
        return $"{LoadSec,9:F1}s {ReloadSec,7:F1}s {s.Average(),6:F2}ms {s[s.Length / 2],6:F2}ms " +
               $"{s[(int)(s.Length * 0.95)],6:F2}ms {RepeatMs,12:F2}ms {RepeatCpuMs,10:F2}ms  {Name}";
    }
}

readonly record struct Det(float X1, float Y1, float X2, float Y2, float Score, int Cls)
{
    public float Area => Math.Max(0, X2 - X1) * Math.Max(0, Y2 - Y1);

    public static List<Det> Decode(float[] o, float min)
    {
        // end-to-end 출력 [1,300,6] = x1,y1,x2,y2,score,class (640 입력 좌표)
        var list = new List<Det>();
        for (int i = 0; i + 5 < o.Length; i += 6)
            if (o[i + 4] >= min) list.Add(new Det(o[i], o[i + 1], o[i + 2], o[i + 3], o[i + 4], (int)o[i + 5]));
        return list;
    }

    public static float IoU(Det a, Det b)
    {
        float w = Math.Min(a.X2, b.X2) - Math.Max(a.X1, b.X1), h = Math.Min(a.Y2, b.Y2) - Math.Max(a.Y1, b.Y1);
        if (w <= 0 || h <= 0) return 0;
        float inter = w * h;
        return inter / (a.Area + b.Area - inter);
    }
}

// 같은 클래스·IoU ≥ 0.5 를 IoU 큰 순으로 1:1 짝짓는다. 신뢰도 기준은 본 앱 ConfidenceThreshold(0.3)와 같게
sealed class Stats
{
    const float Conf = 0.3f, LowConf = 0.05f, MatchIou = 0.5f, SmallArea = 32 * 32;
    int _ref, _test, _matched, _smallRef, _smallMatched, _missNear, _extraNear;
    double _sumIou, _sumDScore, _maxDScore;

    public void Add(float[] refOut, float[] testOut)
    {
        var r = Det.Decode(refOut, Conf);
        var t = Det.Decode(testOut, Conf);
        var pairs = new List<(float Iou, int I, int J)>();
        for (int i = 0; i < r.Count; i++)
            for (int j = 0; j < t.Count; j++)
                if (r[i].Cls == t[j].Cls && Det.IoU(r[i], t[j]) is var iou && iou >= MatchIou) pairs.Add((iou, i, j));
        var usedR = new bool[r.Count];
        var usedT = new bool[t.Count];
        foreach (var (iou, i, j) in pairs.OrderByDescending(p => p.Iou))
        {
            if (usedR[i] || usedT[j]) continue;
            usedR[i] = usedT[j] = true;
            _matched++;
            _sumIou += iou;
            double d = Math.Abs(r[i].Score - t[j].Score);
            _sumDScore += d;
            _maxDScore = Math.Max(_maxDScore, d);
            if (r[i].Area < SmallArea) _smallMatched++;
        }
        _ref += r.Count;
        _test += t.Count;
        _smallRef += r.Count(d => d.Area < SmallArea);

        // 한쪽에만 있는 박스가 다른 쪽에서 임계값 바로 아래(0.05~0.3)로 떨어진 것이면 "경계 근처"로 센다
        var tLow = Det.Decode(testOut, LowConf);
        var rLow = Det.Decode(refOut, LowConf);
        for (int i = 0; i < r.Count; i++)
            if (!usedR[i] && tLow.Any(x => x.Cls == r[i].Cls && Det.IoU(x, r[i]) >= MatchIou)) _missNear++;
        for (int j = 0; j < t.Count; j++)
            if (!usedT[j] && rLow.Any(x => x.Cls == t[j].Cls && Det.IoU(x, t[j]) >= MatchIou)) _extraNear++;
    }

    public void Merge(Stats o)
    {
        _ref += o._ref; _test += o._test; _matched += o._matched;
        _smallRef += o._smallRef; _smallMatched += o._smallMatched;
        _missNear += o._missNear; _extraNear += o._extraNear;
        _sumIou += o._sumIou; _sumDScore += o._sumDScore; _maxDScore = Math.Max(_maxDScore, o._maxDScore);
    }

    public override string ToString() =>
        $"기준 {_ref,4}개 · 이쪽 {_test,4}개 · 일치 {_matched,4} (재현율 {Pct(_matched, _ref)}, 정밀도 {Pct(_matched, _test)}) · " +
        $"평균 IoU {(_matched > 0 ? _sumIou / _matched : 0):F3}";

    public string Detail() =>
        $"신뢰도 차 평균 {(_matched > 0 ? _sumDScore / _matched : 0):F3} / 최대 {_maxDScore:F3} · " +
        $"작은 물체(640 입력 32×32 미만) {_smallMatched}/{_smallRef} ({Pct(_smallMatched, _smallRef)}) · " +
        $"놓친 {_ref - _matched}개 중 임계값 바로 아래 {_missNear} · 더 잡은 {_test - _matched}개 중 기준 쪽 임계값 바로 아래 {_extraNear}";

    static string Pct(int a, int b) => b > 0 ? $"{100.0 * a / b:F1}%" : "-";
}
