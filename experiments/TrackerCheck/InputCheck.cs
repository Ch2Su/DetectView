// YOLO 입력 축소 방식 확인 (2026-11-05 — 본 앱이 YoloDotNet 기본값 Nearest 에서 쌍선형으로, 이어서 OpenCV 레터박스(YoloLetterbox)로 바꿀 때).
// 지금 앱의 검출: BGR → BGRA → 루트 YoloLetterbox.cs(OpenCV 쌍선형 레터박스 → YoloDotNet RunObjectDetection 배율 1 → 박스 +0.5 로 되돌림, DirectML).
// 예전 앱의 검출(nearest·linear): BGRA → SKBitmap → YoloDotNet 이 YoloOptions.SamplingOptions 로 Skia DrawImage 해 640×640 RGBX 버퍼를 만든다
// (ImageResizeExtension.ResizeImageProportional — 모델보다 작은 이미지는 키우지 않고 가운데에 그대로 둔다 — YoloLetterbox 도 같은 규칙).
//
//   testsplit <데이터셋 폴더> <출력 폴더> <이름=모델.onnx> [<이름=모델.onnx> ...]
//     YOLO 형식 데이터셋의 images/test 를 읽기만 한다. 모델마다 세 방식으로 한 번씩 검출해 <출력>/app_<이름>_<방식>.json
//     (COCO 결과 형식 + file_name — experiments/RfDetrBench/step2/eval_test.py --app-json 이 채점)을 쓰고, 같은 이미지에서
//     YoloDotNet 의 축소 결과(Skia)를 OpenCV 레터박스(INTER_NEAREST_EXACT · INTER_LINEAR, 같은 크기·위치)와 화소 단위로 견준다.
//     앞의 몇 장은 두 번 넣어 검출이 비트 단위로 같은지도 본다 (HUD 의 '새 화면 고리'가 기대는 결정성).
//     app_<이름>_prelb.json 은 지금 앱(루트 YoloLetterbox.cs — OpenCV 레터박스를 넘기고 박스를 직접 되돌림). nearest·linear 는 YoloDotNet 이 직접 줄이던 예전 앱
//   inputbench [반복=200]
//     축소만의 시간(Nearest vs Skia 쌍선형 vs 지금 앱 YoloLetterbox — test1 1280×720 프레임과 그걸 키운 2560×1440)과 검출 한 번 전체 시간,
//     같은 프레임을 거듭 넣었을 때 결과가 같은지(세 방식 모두), 앱 경로의 모델 입력이 Skia 쌍선형 입력과 같은지 (모델은 저장소 루트 best.onnx)

using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using OpenCvSharp;
using SkiaSharp;
using YoloDotNet;
using YoloDotNet.Extensions;
using YoloDotNet.Handlers;
using YoloDotNet.Models;
using DetectView;

static class InputCheck
{
    /// <summary>본 앱 MainWindow.Inference.cs 의 InputSampling 과 같은 값 — 복사본이라 앱을 바꾸면 같이 바꾼다</summary>
    public static readonly SKSamplingOptions AppSampling = new(SKFilterMode.Linear, SKMipmapMode.None);

    /// <summary>2026-11-05 전의 앱 = YoloDotNet 기본값 (ImageConfig.DefaultSamplingOptions)</summary>
    public static readonly SKSamplingOptions OldSampling = new(SKFilterMode.Nearest, SKMipmapMode.None);

    const InterpolationFlags NearestExact = (InterpolationFlags)6;   // INTER_NEAREST_EXACT — OpenCvSharp 열거형엔 이름이 없다
    const int Side = 640;                                            // 모델 입력 [1,3,640,640]
    const double EvalConfidence = 0.001;                             // mAP 는 PR 곡선 전체가 필요하다 (eval_test.py 와 같음)
    const double Iou = 0.7;                                          // 본 앱 IouThreshold (end-to-end 모델이라 YoloDotNet V26 모듈은 안 쓴다)
    static readonly string[] ImageExts = [".jpg", ".jpeg", ".png", ".bmp", ".webp", ".tif", ".tiff"];   // eval_test.py 의 IMG_EXTS
    static readonly HashSet<string> TargetClasses = ["person", "car", "motorcycle", "truck", "bus", "scooter", "bicycle", "pickup"];   // 본 앱 TargetClasses

    // (이름, Skia 축소, 같은 방식의 OpenCV 보간). Skia 의 Nearest 는 화소 중심을 맞춰서 OpenCV 의 INTER_NEAREST 가 아니라 NEAREST_EXACT 와 같다
    static readonly (string Name, SKSamplingOptions Sampling, InterpolationFlags Cv)[] Modes =
        [("nearest", OldSampling, NearestExact), ("linear", AppSampling, InterpolationFlags.Linear)];

    /// <summary>본 앱 CreateGpuYolo 와 같은 GPU(GpuAdapters.Best → PCI ID 로 DirectML 장치)에 세션을 만든다. options 의 축소 방식은 실행 중에 바꿔도 다음 검출부터 듣는다</summary>
    public static (Yolo Yolo, YoloOptions Options, string Gpu) Open(string modelPath, SKSamplingOptions sampling)
    {
        var best = GpuAdapters.Best(GpuAdapters.Enumerate());
        var device = WindowsMlExecutionProvider.FindDmlDevice(best?.VendorId ?? 0, best?.DeviceId ?? 0)
                     ?? throw new InvalidOperationException("DirectML 장치를 PCI ID 로 찾지 못함");
        var options = new YoloOptions
        {
            ExecutionProvider = new WindowsMlExecutionProvider(modelPath, device),
            SamplingOptions = sampling,
        };
        return (new Yolo(options), options, best?.Name ?? "GPU");
    }

    // 검출 한 개 (YoloDotNet 은 결과 리스트를 다음 호출에서 비우고 다시 채운다 — 받자마자 이걸로 옮긴다)
    readonly record struct D(string Label, double Conf, SKRectI Box);

    static List<D> Detect(Yolo yolo, SKBitmap sk, double confidence) =>
        yolo.RunObjectDetection(sk, confidence: confidence, iou: Iou)
            .Where(r => TargetClasses.Contains(r.Label.Name))
            .Select(r => new D(r.Label.Name, r.Confidence, r.BoundingBox)).ToList();

    /// <summary>본 앱 ProcessFrame 처럼 BGR Mat 을 복사 없이 SKBitmap 으로 (bgra 가 살아 있는 동안만 유효)</summary>
    static SKBitmap ToSkia(Mat bgr, Mat bgra)
    {
        Cv2.CvtColor(bgr, bgra, ColorConversionCodes.BGR2BGRA);
        var sk = new SKBitmap();
        sk.InstallPixels(new SKImageInfo(bgra.Width, bgra.Height, SKColorType.Bgra8888, SKAlphaType.Premul), bgra.Data, (int)bgra.Step());
        return sk;
    }

    /// <summary>YoloDotNet ResizeImageProportional 과 같은 크기·위치의 OpenCV 레터박스 (BGR, 검은 여백). 모델보다 작으면 키우지 않는다</summary>
    static Mat CvLetterbox(Mat bgr, InterpolationFlags interp, out Rect content)
    {
        int w = bgr.Width, h = bgr.Height, nw = w, nh = h;
        if (!(w < Side && h < Side))
        {
            float s = Math.Min((float)Side / w, (float)Side / h);   // YoloDotNet 과 같게 float 로
            (nw, nh) = ((int)(w * s + 0.5f), (int)(h * s + 0.5f));
        }
        content = new Rect((Side - nw) / 2, (Side - nh) / 2, nw, nh);
        var canvas = new Mat(Side, Side, MatType.CV_8UC3, Scalar.All(0));
        using var resized = (nw, nh) == (w, h) ? bgr.Clone() : bgr.Resize(new Size(nw, nh), 0, 0, interp);
        using var roi = new Mat(canvas, content);
        resized.CopyTo(roi);
        return canvas;
    }

    /// <summary>
    /// 지금 앱의 검출 — 루트 YoloLetterbox.cs(본 앱 DetectBgra 가 쓰는 것과 같은 파일)로: OpenCV INTER_LINEAR 레터박스를 640×640 으로 넘기고
    /// YoloDotNet 이 레터박스 좌표의 정수로 준 박스를 +0.5 해 그린 자리 그대로의 여백·배율로 되돌린다 (2026-11-05 포크 세션이 낸 길을 앱에 넣음).
    /// 관심 클래스만 (본 앱 TargetClasses)
    /// </summary>
    static IEnumerable<(string Label, double Conf, double X1, double Y1, double X2, double Y2)> DetectApp(Yolo yolo, YoloLetterbox letterbox, Mat bgra, double confidence) =>
        letterbox.Detect(yolo, bgra, confidence, Iou).Where(b => TargetClasses.Contains(b.Label))
                 .Select(b => (b.Label, b.Conf, b.X, b.Y, b.X + b.W, b.Y + b.H)).ToList();

    /// <summary>화소 비교 누적: 화소마다 세 채널 중 가장 큰 |차| 의 분포 (내용 영역만 — 여백은 둘 다 0)</summary>
    sealed class PixelStats
    {
        public readonly long[] Hist = new long[256];
        public long Pixels, SumAbs;     // SumAbs = 채널 |차| 합
        public int Images, ImagesDiffer, ImagesOver2, MaxDiff;
        public string? WorstFile;

        public unsafe void Add(PinnedMemoryBuffer skia, Mat cv, Rect content, string file)
        {
            byte* s = (byte*)skia.Pointer;   // Rgb888x: R G B X
            int imgMax = 0;
            for (int y = content.Y; y < content.Bottom; y++)
            {
                byte* c = (byte*)cv.Ptr(y);
                for (int x = content.X; x < content.Right; x++)
                {
                    int i = (y * Side + x) * 4, j = x * 3;
                    int dr = Math.Abs(s[i] - c[j + 2]), dg = Math.Abs(s[i + 1] - c[j + 1]), db = Math.Abs(s[i + 2] - c[j]);
                    int m = Math.Max(dr, Math.Max(dg, db));
                    Hist[m]++;
                    SumAbs += dr + dg + db;
                    if (m > imgMax) imgMax = m;
                }
            }
            Pixels += (long)content.Width * content.Height;
            Images++;
            if (imgMax > 0) ImagesDiffer++;
            if (imgMax > 2) ImagesOver2++;
            if (imgMax > MaxDiff) { MaxDiff = imgMax; WorstFile = file; }
        }

        public string Summary()
        {
            double P(int from, int to) => Pixels == 0 ? 0 : (double)Hist[from..(to + 1)].Sum() / Pixels * 100;
            return string.Create(CultureInfo.InvariantCulture,
                $"화소 {Pixels:N0}개 — 같음 {P(0, 0):F3}% · 1 {P(1, 1):F3}% · 2 {P(2, 2):F3}% · 3+ {P(3, 255):F4}% · 채널 평균 |차| {(Pixels == 0 ? 0 : SumAbs / (3.0 * Pixels)):F4} · " +
                $"최대 {MaxDiff}{(WorstFile != null ? $" ({WorstFile})" : "")} · 다른 이미지 {ImagesDiffer}/{Images} (3 이상 {ImagesOver2})");
        }
    }

    static string[] ReadNames(string dataYaml)
    {
        // eval_test.py read_names 와 같게 — names: 아래 '  0: person' 꼴
        var names = new SortedDictionary<int, string>();
        bool inside = false;
        foreach (var line in File.ReadLines(dataYaml))
        {
            if (line.StartsWith("names:")) { inside = true; continue; }
            if (!inside) continue;
            if (!line.StartsWith(' ')) break;
            var kv = line.Trim().Split(':', 2);
            names[int.Parse(kv[0], CultureInfo.InvariantCulture)] = kv[1].Trim().Trim('\'', '"');
        }
        return [.. names.Values];
    }

    public static int TestSplit(string[] args)
    {
        if (args.Length < 3 || args.Skip(2).Any(a => !a.Contains('=')))
        {
            Console.WriteLine("사용법: testsplit <데이터셋 폴더> <출력 폴더> <이름=모델.onnx> [<이름=모델.onnx> ...]");
            return 2;
        }
        string data = args[0], outDir = Path.GetFullPath(args[1]);
        Directory.CreateDirectory(outDir);
        var log = new StringBuilder();
        void Log(string s) { Console.WriteLine(s); log.AppendLine(s); }

        var names = ReadNames(Path.Combine(data, "data.yaml"));
        var files = Directory.EnumerateFiles(Path.Combine(data, "images", "test"))
            .Where(f => ImageExts.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase).ToList();
        Log($"데이터: {data} — 시험 분할 {files.Count}장 · 클래스 {string.Join(", ", names)}");

        var models = new List<(string Name, Yolo Yolo, YoloOptions Options)>();
        foreach (var spec in args.Skip(2))
        {
            var p = spec.Split('=', 2);
            string path = Path.GetFullPath(p[1]);
            var (yolo, options, gpu) = Open(path, AppSampling);
            var meta = yolo.OnnxModel?.CustomMetaData;
            string md5 = Convert.ToHexString(System.Security.Cryptography.MD5.HashData(File.ReadAllBytes(path)));
            Log($"모델 {p[0]}: {path} (MD5 {md5}) · {meta?.GetValueOrDefault("description")} · {meta?.GetValueOrDefault("date")} · {gpu}");
            var unknown = yolo.OnnxModel!.Labels.Select(l => l.Name).Where(n => TargetClasses.Contains(n) && Array.IndexOf(names, n) < 0).ToList();
            if (unknown.Count > 0) throw new InvalidDataException($"{p[0]}: 데이터셋에 없는 클래스 {string.Join(", ", unknown)}");
            models.Add((p[0], yolo, options));
        }

        // 모델 × 방식마다 JSON 을 흘려 쓴다 (모델·방식마다 수십만 개라 다 들고 있지 않는다).
        // prelb = 지금 앱(YoloLetterbox — OpenCV INTER_LINEAR 레터박스를 640×640 으로 넘기고 박스를 직접 되돌린다, DetectApp).
        // nearest·linear 는 YoloDotNet 이 직접 줄이던 예전 앱 (2026-11-05 전 · 그날 잠깐)
        var writers = new Dictionary<(string, string), (FileStream Stream, Utf8JsonWriter Json, long Count)>();
        foreach (var m in models)
            foreach (var mode in Modes.Select(md => md.Name).Append("prelb"))
            {
                var fs = File.Create(Path.Combine(outDir, $"app_{m.Name}_{mode}.json"));
                var w = new Utf8JsonWriter(fs);
                w.WriteStartArray();
                writers[(m.Name, mode)] = (fs, w, 0);
            }
        void Write(string model, string mode, string fileName, IEnumerable<(string Label, double Conf, double X1, double Y1, double X2, double Y2)> dets)
        {
            var (fs, w, count) = writers[(model, mode)];
            foreach (var d in dets)
            {
                w.WriteStartObject();
                w.WriteString("file_name", fileName);
                w.WriteNumber("category_id", Array.IndexOf(names, d.Label) + 1);   // eval_test.py: 클래스 번호 + 1
                w.WriteStartArray("bbox");
                w.WriteNumberValue(d.X1); w.WriteNumberValue(d.Y1); w.WriteNumberValue(d.X2 - d.X1); w.WriteNumberValue(d.Y2 - d.Y1);
                w.WriteEndArray();
                w.WriteNumber("score", d.Conf);
                w.WriteEndObject();
                count++;
            }
            writers[(model, mode)] = (fs, w, count);
        }

        var pixel = Modes.ToDictionary(md => md.Name, _ => new PixelStats());
        using var skiaBuf = new PinnedMemoryBuffer(new SKImageInfo(Side, Side, SKColorType.Rgb888x, SKAlphaType.Opaque));
        const int RepeatCheck = 25;   // 앞의 이 장 수만큼 같은 입력을 한 번 더 넣는다
        int repeatSame = 0, repeatDiff = 0, small = 0, skipped = 0;
        var sw = Stopwatch.StartNew();
        using var bgra = new Mat();
        using var appLetterbox = new YoloLetterbox();
        for (int k = 0; k < files.Count; k++)
        {
            string file = files[k], fileName = Path.GetFileName(file);
            using var bgr = Cv2.ImRead(file, ImreadModes.Color);   // eval_test.py 의 cv2.imread(IMREAD_COLOR) 와 같다
            if (bgr.Empty()) { Log($"  읽지 못함: {fileName}"); skipped++; continue; }
            if (bgr.Width < Side && bgr.Height < Side) small++;
            using var sk = ToSkia(bgr, bgra);

            foreach (var md in Modes)
            {
                // YoloDotNet 이 버퍼 풀에서 하는 것처럼 지운 뒤 그린다 (빌려줄 때 투명 검정으로 지운다)
                skiaBuf.TargetBitmap.Erase(SKColors.Empty);
                sk.ResizeImageProportional(md.Sampling, skiaBuf);
                using var cv = CvLetterbox(bgr, md.Cv, out var content);
                pixel[md.Name].Add(skiaBuf, cv, content, fileName);
            }

            foreach (var m in models)
            {
                foreach (var md in Modes)
                {
                    m.Options.SamplingOptions = md.Sampling;
                    var dets = Detect(m.Yolo, sk, EvalConfidence);
                    if (k < RepeatCheck && md.Name == "linear")
                    {
                        // 같은 입력을 다시 — 박스(정수)·라벨·신뢰도(double 비트)까지 같아야 한다
                        var again = Detect(m.Yolo, sk, EvalConfidence);
                        if (again.SequenceEqual(dets)) repeatSame++; else repeatDiff++;
                    }
                    // 박스는 YoloDotNet 이 준 정수 그대로 (본 앱 ToDet 과 같음)
                    Write(m.Name, md.Name, fileName, dets.Select(d => (d.Label, d.Conf, (double)d.Box.Left, (double)d.Box.Top, (double)d.Box.Right, (double)d.Box.Bottom)));
                }
                m.Options.SamplingOptions = AppSampling;   // 레터박스는 배율 1 로 그려져 축소 방식과 상관없다
                Write(m.Name, "prelb", fileName, DetectApp(m.Yolo, appLetterbox, bgra, EvalConfidence));
            }
            if ((k + 1) % 100 == 0) Console.WriteLine($"  {k + 1}/{files.Count} · {sw.Elapsed.TotalSeconds:F0}초");
        }
        foreach (var (key, (fs, w, count)) in writers)
        {
            w.WriteEndArray();
            w.Flush();
            w.Dispose();
            fs.Dispose();
            Log($"검출 {key.Item1} {key.Item2}: {count:N0}개 → app_{key.Item1}_{key.Item2}.json");
        }
        Log($"이미지 {files.Count - skipped}장 (모델보다 작아 축소 안 함 {small}장, 못 읽음 {skipped}) · {sw.Elapsed.TotalSeconds:F0}초");
        foreach (var md in Modes)
            Log($"축소 {md.Name} — YoloDotNet(Skia {md.Sampling.Filter}) vs OpenCV {(md.Cv == NearestExact ? "INTER_NEAREST_EXACT" : md.Cv.ToString())}: {pixel[md.Name].Summary()}");
        Log($"결정성 (쌍선형, 앞 {RepeatCheck}장 × 모델 {models.Count}개 — 같은 입력 두 번): 같음 {repeatSame} · 다름 {repeatDiff}");
        File.WriteAllText(Path.Combine(outDir, "testsplit.txt"), log.ToString());
        foreach (var m in models) m.Yolo.Dispose();
        return repeatDiff;
    }

    public static int InputBench(string[] args)
    {
        int reps = args.Length > 0 && int.TryParse(args[0], out var r) && r > 0 ? r : 200;
        string video = Path.Combine(RepoPaths.VideoDir, "test1.mp4");
        using var cap = new VideoCapture(video);
        if (!cap.IsOpened()) throw new FileNotFoundException(video);
        cap.Set(VideoCaptureProperties.PosFrames, 300);
        using var f720 = new Mat();
        if (!cap.Read(f720) || f720.Empty()) throw new InvalidDataException("test1.mp4 300번째 프레임을 못 읽음");
        // HUD 의 2560×1440 화면 대신 — 축소 비용은 출력 화소 수(640×360)로 정해지지만 원본이 크면 읽는 자리가 흩어진다
        using var f1440 = f720.Resize(new Size(2560, 1440), 0, 0, InterpolationFlags.Linear);

        static (double Median, double P90) Time(int n, Action a)
        {
            for (int i = 0; i < 20; i++) a();   // 워밍업
            var t = new double[n];
            for (int i = 0; i < n; i++)
            {
                long s = Stopwatch.GetTimestamp();
                a();
                t[i] = Stopwatch.GetElapsedTime(s).TotalMilliseconds;
            }
            Array.Sort(t);
            return (t[n / 2], t[(int)(n * 0.9)]);
        }

        using var buf = new PinnedMemoryBuffer(new SKImageInfo(Side, Side, SKColorType.Rgb888x, SKAlphaType.Opaque));
        using var appLetterbox = new YoloLetterbox();   // 본 앱 DetectBgra 와 같은 파일
        var (yolo, options, gpu) = Open(Path.Combine(RepoPaths.Root, "best.onnx"), AppSampling);
        Console.WriteLine($"모델: 저장소 루트 best.onnx · {gpu} · 반복 {reps}회 (중앙 / 90%)");
        using var bgra = new Mat();
        int failures = 0;
        foreach (var (label, frame) in new[] { ("1280×720", f720), ("2560×1440", f1440) })
        {
            using var sk = ToSkia(frame, bgra);
            foreach (var md in Modes)
            {
                // 축소만 — YoloDotNet 의 버퍼 풀처럼 지우고 그린다 (지우기 포함)
                var resize = Time(reps, () => { buf.TargetBitmap.Erase(SKColors.Empty); sk.ResizeImageProportional(md.Sampling, buf); });
                options.SamplingOptions = md.Sampling;
                var full = Time(reps, () => Detect(yolo, sk, 0.3));
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"  {label} {md.Name,-7}: 축소 {resize.Median:F3} / {resize.P90:F3} ms · 검출 전체(축소·정규화·추론·후처리) {full.Median:F2} / {full.P90:F2} ms"));

                // 결정성 — 같은 프레임 50번이 모두 첫 번째와 같아야 한다 (HUD 는 같은 화면에 같은 박스가 나와야 다시 그리지 않는다)
                var first = Detect(yolo, sk, 0.001);
                int differ = Enumerable.Range(0, 50).Count(_ => !Detect(yolo, sk, 0.001).SequenceEqual(first));
                Console.WriteLine($"    결정성: 같은 프레임 50번 중 첫 번째와 다른 결과 {differ}번 (검출 {first.Count}개, 임계 0.001)");
                failures += differ;
            }

            // 지금 앱(YoloLetterbox): OpenCV INTER_LINEAR 로 줄여 640×640 검은 캔버스에 넣는다(여백은 내용 자리가 바뀔 때만 칠한다).
            // YoloDotNet 은 배율 1 로 그대로 그린다. 아래 앞부분은 그 단계를 따로 떼어 잰 것, '앱 경로'는 YoloLetterbox 자체.
            // OpenCV 는 기본이 여러 스레드라 한 스레드로도 잰다
            {
                int w = frame.Width, h = frame.Height;
                float s = Math.Min((float)Side / w, (float)Side / h);
                int nw = (int)(w * s + 0.5f), nh = (int)(h * s + 0.5f);
                using var canvas = new Mat(Side, Side, MatType.CV_8UC4, new Scalar(0, 0, 0, 255));
                using var roi = new Mat(canvas, new Rect((Side - nw) / 2, (Side - nh) / 2, nw, nh));
                using var small = new Mat();
                using var skC = new SKBitmap();
                skC.InstallPixels(new SKImageInfo(Side, Side, SKColorType.Bgra8888, SKAlphaType.Premul), canvas.Data, (int)canvas.Step());
                void Pre() { Cv2.Resize(bgra, small, new Size(nw, nh), 0, 0, InterpolationFlags.Linear); small.CopyTo(roi); }
                options.SamplingOptions = AppSampling;
                int threads = Cv2.GetNumThreads();
                foreach (int t in new[] { threads, 1 })
                {
                    Cv2.SetNumThreads(t);
                    var cvOnly = Time(reps, Pre);
                    var pre = Time(reps, () => { Pre(); buf.TargetBitmap.Erase(SKColors.Empty); skC.ResizeImageProportional(AppSampling, buf); });
                    var appFull = Time(reps, () => appLetterbox.Detect(yolo, bgra, 0.3, Iou));
                    Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                        $"  {label} 앱 경로 YoloLetterbox (OpenCV {t}스레드): OpenCV 축소·복사 {cvOnly.Median:F3} / {cvOnly.P90:F3} ms · YoloDotNet 그리기까지 {pre.Median:F3} / {pre.P90:F3} ms · " +
                        $"검출 전체 {appFull.Median:F2} / {appFull.P90:F2} ms"));
                }
                Cv2.SetNumThreads(threads);

                // 결정성 — 앱 경로도 같은 프레임 50번이 모두 첫 번째와 같아야 한다
                var firstApp = appLetterbox.Detect(yolo, bgra, 0.001, Iou);
                int differApp = Enumerable.Range(0, 50).Count(_ => !appLetterbox.Detect(yolo, bgra, 0.001, Iou).SequenceEqual(firstApp));
                Console.WriteLine($"    결정성 (앱 경로): 같은 프레임 50번 중 첫 번째와 다른 결과 {differApp}번 (검출 {firstApp.Count}개, 임계 0.001)");
                failures += differApp;

                // 선축소로 YoloDotNet 버퍼에 들어간 화소 vs Skia 쌍선형 버퍼 (같은 자리, R·G·B)
                Pre();
                buf.TargetBitmap.Erase(SKColors.Empty);
                skC.ResizeImageProportional(AppSampling, buf);
                var preBytes = buf.Buffer.ToArray();
                buf.TargetBitmap.Erase(SKColors.Empty);
                sk.ResizeImageProportional(AppSampling, buf);
                long diff = 0; int maxDiff = 0;
                for (int i = 0; i < preBytes.Length; i += 4)
                {
                    int m = Math.Max(Math.Abs(preBytes[i] - buf.Buffer[i]), Math.Max(Math.Abs(preBytes[i + 1] - buf.Buffer[i + 1]), Math.Abs(preBytes[i + 2] - buf.Buffer[i + 2])));
                    if (m > 0) diff++;
                    if (m > maxDiff) maxDiff = m;
                }
                Console.WriteLine($"    선축소 입력 vs Skia 쌍선형 입력 (640×640 버퍼): 다른 화소 {diff}개 · 최대 차 {maxDiff}");
            }
        }
        // Skia 의 Nearest 가 정확히 2배 축소(1280×720 → 640×360)에서 고르는 원본 화소 — 표본 위치 2x+1.0 이 두 화소 중심의 한가운데라
        // 반올림 방향에 따라 2x 와 2x+1 이 갈린다. OpenCV INTER_NEAREST_EXACT 는 2x+1, INTER_NEAREST 는 2x 를 고른다 (2026-11-05 실측: Skia 는 2x — 100%)
        using (var sk = ToSkia(f720, bgra))
        {
            buf.TargetBitmap.Erase(SKColors.Empty);
            sk.ResizeImageProportional(OldSampling, buf);
            // 후보 넷((가로 2x 또는 2x+1) × (세로 2y 또는 2y+1))마다 출력 화소와 같은 수 — 평탄한 곳은 여럿과 같으니 따로 센다. 고른 쪽은 100% 다
            var match = new long[4];
            unsafe
            {
                byte* s = (byte*)buf.Pointer;
                for (int y = 0; y < 360; y++)
                    for (int x = 0; x < Side; x++)
                    {
                        byte* o = s + ((y + 140) * Side + x) * 4;   // 640×360 이 세로 가운데(140)에 그려진다
                        for (int c = 0; c < 4; c++)
                        {
                            byte* p = (byte*)f720.Ptr(2 * y + c / 2) + (2 * x + c % 2) * 3;
                            if (o[0] == p[2] && o[1] == p[1] && o[2] == p[0]) match[c]++;
                        }
                    }
            }
            double n = 360.0 * Side / 100;
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  1280×720 Nearest 출력이 원본 후보와 같은 비율: (2x,2y) {match[0] / n:F1}% · (2x+1,2y) {match[1] / n:F1}% · (2x,2y+1) {match[2] / n:F1}% · (2x+1,2y+1) {match[3] / n:F1}%"));
        }

        // SAHI 타일(640×640 — 모델 입력과 같은 크기)은 배율 1 로 그려진다 → 쌍선형이어도 원본 화소 그대로여야 한다
        using (var tile = new Mat(f720, new Rect(320, 40, Side, Side)).Clone())
        using (var sk = ToSkia(tile, bgra))
        {
            buf.TargetBitmap.Erase(SKColors.Empty);
            sk.ResizeImageProportional(AppSampling, buf);
            long differ = 0;
            unsafe
            {
                byte* s = (byte*)buf.Pointer;
                for (int y = 0; y < Side; y++)
                {
                    byte* c = (byte*)tile.Ptr(y);
                    for (int x = 0; x < Side; x++)
                        if (s[(y * Side + x) * 4] != c[x * 3 + 2] || s[(y * Side + x) * 4 + 1] != c[x * 3 + 1] || s[(y * Side + x) * 4 + 2] != c[x * 3]) differ++;
                }
            }
            Console.WriteLine($"  640×640 (SAHI 타일) 쌍선형: 원본과 다른 화소 {differ}개");
            failures += differ > 0 ? 1 : 0;
        }
        yolo.Dispose();
        return failures;
    }
}
