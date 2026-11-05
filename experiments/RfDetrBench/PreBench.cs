// 3단계 준비 — RF-DETR 를 앱에 넣기 전에 확인할 두 가지 (README '3단계 준비'):
//   ① 전처리: 영상 프레임을 RF-DETR 입력으로 만드는 CPU 시간과, 결과가 공식 경로(float 에서 쌍선형 — 1단계에서 PyTorch 와 100% 같음)와 같은지.
//      A 공식 경로(1단계) · B uint8 에서 쌍선형(1단계) · C1 A 를 버퍼 재사용·한 번에 정규화 · C2 원본에서 바로 쌍선형(1스레드·병렬) ·
//      D 전처리를 ONNX 그래프 앞에 붙여 GPU 에서 (prep_graph.py 의 *-gpuprep.onnx — 앱은 프레임 메모리를 그대로 넘긴다)
//   ② 프레임당 시간: 전처리 + session.Run + 후처리
//   ③ DirectML 결정성: 같은 입력을 같은 세션으로 여러 번·새 세션으로 돌려 출력이 비트 단위로 같은지 — HUD 의 '새 화면 고리'가 기대는 성질 (CLAUDE.md '화면 HUD')
// 사용: RfDetrBench.exe pre [영상당 프레임 수=60]   (prep_graph.py 를 먼저)
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;

static class PreBench
{
    const string U8Input = "image_bgr_u8";
    static readonly float[] Mean = [0.485f, 0.456f, 0.406f];
    static readonly float[] Std = [0.229f, 0.224f, 0.225f];
    static readonly RunOptions Ro = new();

    sealed record Model(string Name, string Plain, string Gpu, int Res, int? Background);

    public static int Run(string here, string[] args)
    {
        int perVideo = args.Length > 0 && int.TryParse(args[0], out var n0) && n0 > 0 ? n0 : 60;
        string outDir = Path.Combine(here, "out");
        string? pub = FindPublish(here);
        if (pub == null) { Console.WriteLine("publish/test4.mp4 를 찾지 못했습니다 — 이 폴더에서 위로 올라가며 찾는다"); return 1; }
        var t4 = ReadFrames(Path.Combine(pub, "test4.mp4"), perVideo);
        var t5 = ReadFrames(Path.Combine(pub, "test5.mp4"), perVideo);
        var video = t4.Concat(t5).ToList();
        var stills = Directory.GetFiles(Path.Combine(outDir, "samples"), "ds_*.png").Order()
                              .Select(f => Cv2.ImRead(f, ImreadModes.Color)).ToList();
        // HUD 는 모니터 크기(이 PC 의 주 모니터 2560×1440)·BGRA 4채널 프레임을 받는다 — 비용이 입력 크기에 따라 어떻게 느는지 보려고 합성
        var hud = new Mat();
        Cv2.Resize(t4[0], hud, new OpenCvSharp.Size(2560, 1440), 0, 0, InterpolationFlags.Linear);
        var hudBgra = new Mat();
        Cv2.CvtColor(hud, hudBgra, ColorConversionCodes.BGR2BGRA);
        Console.WriteLine($"[프레임] test4 {t4.Count}장({t4[0].Width}×{t4[0].Height}) · test5 {t5.Count}장({t5[0].Width}×{t5[0].Height}) · " +
                          $"데이터셋 시험 분할 {stills.Count}장 · HUD 크기 합성 2560×1440 (BGR·BGRA)");

        var env = OrtEnv.Instance();
        var dml = env.GetEpDevices().FirstOrDefault(d => d.EpName == "DmlExecutionProvider" && d.HardwareDevice.VendorId == 0x10DE)
                  ?? env.GetEpDevices().FirstOrDefault(d => d.EpName == "DmlExecutionProvider");
        if (dml == null) { Console.WriteLine("DirectML 장치가 없습니다."); return 1; }
        Console.WriteLine($"[장치] DirectML · {dml.HardwareDevice.Vendor} · ONNX Runtime {env.GetVersionString()}");

        // 본 앱과 같은 세션 설정. D 는 프레임 크기를 알 때 세션을 만들며 H·W·C 를 고정한다 — 고정하지 않으면 크기가 제각각인 이미지도 한 세션으로 돈다
        SessionOptions Opts(Mat? fix = null)
        {
            var so = new SessionOptions
            {
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
                EnableMemoryPattern = false,
                EnableCpuMemArena = true,
            };
            if (fix != null)
            {
                so.AddFreeDimensionOverrideByName("H", fix.Height);
                so.AddFreeDimensionOverrideByName("W", fix.Width);
                so.AddFreeDimensionOverrideByName("C", fix.Channels());
            }
            so.AppendExecutionProvider(env, [dml], new Dictionary<string, string>());
            return so;
        }

        string M(string rel) => Path.Combine(outDir, rel.Replace('/', Path.DirectorySeparatorChar));
        var models = new[]
        {
            new Model("RF-DETR-S FP32 (학습)", M("step2_models/rfdetr_s.onnx"), M("step2_models/rfdetr_s-gpuprep.onnx"), 512, -1),
            new Model("RF-DETR-S FP16 (학습)", M("step2_models/rfdetr_s-fp16.onnx"), M("step2_models/rfdetr_s-fp16-gpuprep.onnx"), 512, -1),
            new Model("RF-DETR-N FP16 (COCO)", M("models/rfdetr-nano-fp16.onnx"), M("models/rfdetr-nano-fp16-gpuprep.onnx"), 384, null),
        };
        foreach (var m in models)
            if (!File.Exists(m.Plain) || !File.Exists(m.Gpu)) { Console.WriteLine($"{m.Plain} 또는 짝 *-gpuprep.onnx 가 없습니다 — README '3단계 준비'"); return 1; }

        // ① 전처리 CPU 시간 · 입력 값 차 · 검출 맞춤 -------------------------------------------------------------
        int res = 512;
        var c1 = new FloatFused();
        var c2 = new Direct();
        var buf = new float[3 * res * res];
        var cands = new (string Name, Func<Mat, float[]> F)[]
        {
            ("A 공식: float 로 바꾼 뒤 쌍선형", m => Prep.RfDetr(m, res, viaFloat: true)),
            ("B uint8 에서 쌍선형", m => Prep.RfDetr(m, res, viaFloat: false)),
            ("C1 A + 버퍼 재사용·한 번에 정규화", m => { c1.Run(m, res, buf, parallel: true); return buf; }),
            ("C2 원본에서 바로 쌍선형 · 1스레드", m => { c2.Run(m, res, buf, parallel: false); return buf; }),
            ("C2 원본에서 바로 쌍선형 · 병렬", m => { c2.Run(m, res, buf, parallel: true); return buf; }),
        };
        Console.WriteLine($"\n① 전처리 CPU 시간 — {res}×{res} 입력 하나 (ms: 영상 프레임 중앙 · p95 · HUD 크기 2560×1440 중앙)");
        foreach (var (name, f) in cands)
        {
            var (med, p95) = TimeIt(video, m => f(m));
            var (hmed, _) = TimeIt(Enumerable.Repeat(hud, 20).ToList(), m => f(m));
            Console.WriteLine($"  {name,-30} {med,6:F2} · {p95,6:F2} · {hmed,6:F2}");
        }

        double dB = 0, dC1 = 0, dC2 = 0, dPar = 0;
        var single = new float[buf.Length];
        foreach (var img in video.Concat(stills))
        {
            var xa = Prep.RfDetr(img, res, viaFloat: true);
            dB = Math.Max(dB, Rf.MaxAbs(Prep.RfDetr(img, res, viaFloat: false), xa));
            c1.Run(img, res, buf, parallel: true);
            dC1 = Math.Max(dC1, Rf.MaxAbs(buf, xa));
            c2.Run(img, res, single, parallel: false);
            dC2 = Math.Max(dC2, Rf.MaxAbs(single, xa));
            c2.Run(img, res, buf, parallel: true);
            dPar = Math.Max(dPar, Rf.MaxAbs(buf, single));
        }
        Console.WriteLine($"  입력 값 차 (A 기준, 정규화 뒤 최대 — 밝기 한 칸 ≈ 0.017): B {dB:G3} · C1 {dC1:G3} · C2 {dC2:G3} (병렬 vs 1스레드 {dPar:G3}) — 영상 {video.Count} + 시험 분할 {stills.Count}장");

        var S = models[0];
        using (var soP = Opts())
        using (var sP = new InferenceSession(S.Plain, soP))
        using (var soG = Opts())
        using (var sG = new InferenceSession(S.Gpu, soG))
        {
            var (dn, ln, q, c) = OutInfo(sP);
            var (dnG, lnG, _, _) = OutInfo(sG);
            var xbuf = new float[3 * res * res];
            using var xv = OrtValue.CreateTensorValueFromMemory(xbuf, [1, 3, res, res]);
            var ag = new Dictionary<string, Agree> { ["B"] = new(), ["C1"] = new(), ["C2"] = new(), ["D"] = new() };
            foreach (var img in video.Concat(stills))
            {
                List<Box> Det(float[] x)
                {
                    x.CopyTo(xbuf, 0);
                    var (d, l) = RunF(sP, xv, dn, ln);
                    return Rf.Decode(d, l, q, c, img.Width, img.Height, S.Background);
                }
                var refD = Det(Prep.RfDetr(img, res, viaFloat: true));
                ag["B"].Add(refD, Det(Prep.RfDetr(img, res, viaFloat: false)));
                c1.Run(img, res, buf, parallel: true);
                ag["C1"].Add(refD, Det(buf));
                c2.Run(img, res, buf, parallel: true);
                ag["C2"].Add(refD, Det(buf));
                var (dg, lg) = RunU8(sG, img, dnG, lnG);
                ag["D"].Add(refD, Rf.Decode(dg, lg, q, c, img.Width, img.Height, S.Background));
            }
            Console.WriteLine($"  검출 맞춤 — A 기준, {S.Name} · DirectML · 임계 {Rf.Threshold} (D 는 모양을 고정하지 않은 세션 하나로):");
            foreach (var (k, v) in ag) Console.WriteLine($"    {k,-3} {v}");
        }

        Console.WriteLine($"\n  D 노드 배치 ({S.Name}, 1280×720×3 으로 고정한 세션, 실행 5회 프로파일):");
        using (var so = Opts(t4[0]))
        {
            so.ProfileOutputPathPrefix = Path.Combine(outDir, "prof_pre_gpuprep");
            so.EnableProfiling = true;
            using var s = new InferenceSession(S.Gpu, so);
            var (dn, ln, _, _) = OutInfo(s);
            for (int i = 0; i < 5; i++) RunU8(s, t4[0], dn, ln);
            Console.WriteLine(Prep.SummarizeProfile(s.EndProfiling(), 5));
        }

        // ② 프레임당 시간 ------------------------------------------------------------------------------------------
        Console.WriteLine($"\n② 프레임당 시간 — test4 {t4.Count}장 (ms 중앙: 전처리 · session.Run(입력 복사·출력 읽기 포함) · 후처리 · 합계)");
        foreach (var m in models)
        {
            using var soP = Opts();
            using var sP = new InferenceSession(m.Plain, soP);
            using var soG = Opts(t4[0]);
            using var sG = new InferenceSession(m.Gpu, soG);
            var (dn, ln, q, c) = OutInfo(sP);
            var (dnG, lnG, _, _) = OutInfo(sG);
            var xbuf = new float[3 * m.Res * m.Res];
            var mbuf = new float[3 * m.Res * m.Res];
            using var xv = OrtValue.CreateTensorValueFromMemory(xbuf, [1, 3, m.Res, m.Res]);
            for (int i = 0; i < 20; i++) { RunF(sP, xv, dn, ln); RunU8(sG, t4[0], dnG, lnG); }   // 워밍업 (GPU 클럭·DirectML 첫 실행)
            var rows = new (string Name, Func<Mat, float[]?> Pre)[]
            {
                ("A", img => Prep.RfDetr(img, m.Res, viaFloat: true)),
                ("C2", img => { c2.Run(img, m.Res, mbuf, parallel: true); return mbuf; }),
                ("D", _ => null),
            };
            foreach (var (name, pre) in rows)
            {
                var (tp, tr, td, tt) = (new List<double>(), new List<double>(), new List<double>(), new List<double>());
                var sw = new Stopwatch();
                foreach (var img in t4)
                {
                    sw.Restart();
                    var x = pre(img);
                    double a = sw.Elapsed.TotalMilliseconds;
                    float[] d, l;
                    if (x == null) (d, l) = RunU8(sG, img, dnG, lnG);
                    else { x.CopyTo(xbuf, 0); (d, l) = RunF(sP, xv, dn, ln); }
                    double b = sw.Elapsed.TotalMilliseconds;
                    Rf.Decode(d, l, q, c, img.Width, img.Height, m.Background);
                    double e = sw.Elapsed.TotalMilliseconds;
                    tp.Add(a); tr.Add(b - a); td.Add(e - b); tt.Add(e);
                }
                Console.WriteLine($"  {m.Name,-22} {name,-3} {Med(tp),5:F2} · {Med(tr),5:F2} · {Med(td),4:F2} · 합계 {Med(tt),5:F2}");
            }
        }
        Console.WriteLine("  HUD 크기 2560×1440 BGRA 를 D 로 (세션을 그 크기로 고정, 20회 중앙 — CPU 전처리는 ① 의 HUD 크기 열):");
        foreach (var m in models.Skip(1))
        {
            using var so = Opts(hudBgra);
            using var s = new InferenceSession(m.Gpu, so);
            var (dn, ln, _, _) = OutInfo(s);
            for (int i = 0; i < 10; i++) RunU8(s, hudBgra, dn, ln);
            var (med, _) = TimeIt(Enumerable.Repeat(hudBgra, 20).ToList(), img => RunU8(s, img, dn, ln));
            Console.WriteLine($"  {m.Name,-22} D   session.Run {med,5:F2}");
        }

        // ③ DirectML 결정성 --------------------------------------------------------------------------------------
        Console.WriteLine("\n③ DirectML 결정성 — test4 첫 프레임을 같은 세션으로 30회 + 새 세션으로 1회, 출력(박스·로짓)을 비트 단위로");
        var first = t4[0];
        foreach (var m in models)
            foreach (bool gpu in new[] { false, true })
            {
                var x = gpu ? null : Prep.RfDetr(first, m.Res, viaFloat: true);
                float[][] Once(InferenceSession s)
                {
                    var (dn, ln, _, _) = OutInfo(s);
                    if (x == null) { var (d, l) = RunU8(s, first, dn, ln); return [d, l]; }
                    using var v = OrtValue.CreateTensorValueFromMemory((float[])x.Clone(), [1, 3, m.Res, m.Res]);
                    var (d2, l2) = RunF(s, v, dn, ln);
                    return [d2, l2];
                }
                float[][] baseOut;
                int diffRuns = 0;
                double maxDiff = 0;
                using (var so = gpu ? Opts(first) : Opts())
                using (var s = new InferenceSession(gpu ? m.Gpu : m.Plain, so))
                {
                    baseOut = Once(s);
                    for (int i = 0; i < 29; i++)
                    {
                        var o = Once(s);
                        if (!SameBits(o, baseOut)) { diffRuns++; maxDiff = Math.Max(maxDiff, MaxDiff(o, baseOut)); }
                    }
                }
                float[][] fresh;
                using (var so = gpu ? Opts(first) : Opts())
                using (var s = new InferenceSession(gpu ? m.Gpu : m.Plain, so))
                    fresh = Once(s);
                string same = diffRuns == 0 ? "29회 모두 첫 회와 비트 단위로 같음" : $"{diffRuns}회 다름 (최대 차 {maxDiff:G3})";
                string again = SameBits(fresh, baseOut) ? "새 세션도 같음" : $"새 세션은 다름 (최대 차 {MaxDiff(fresh, baseOut):G3})";
                Console.WriteLine($"  {m.Name,-22} {(gpu ? "D" : "A"),-2} {same} · {again}");
            }
        return 0;
    }

    static (string Dets, string Labels, int Q, int C) OutInfo(InferenceSession s)
    {
        string dn = s.OutputNames.First(n => n.Contains("dets")), ln = s.OutputNames.First(n => n.Contains("labels"));
        var dims = s.OutputMetadata[ln].Dimensions;
        return (dn, ln, dims[1], dims[2]);
    }

    static (float[] D, float[] L) RunF(InferenceSession s, OrtValue v, string dn, string ln)
    {
        using var outs = s.Run(Ro, [s.InputNames[0]], [v], [dn, ln]);
        return (outs[0].GetTensorDataAsSpan<float>().ToArray(), outs[1].GetTensorDataAsSpan<float>().ToArray());
    }

    /// <summary>D: 프레임(BGR·BGRA uint8)의 메모리를 복사하지 않고 그대로 입력 텐서로 감싼다 — 앱에선 디코딩한 Mat·캡처 버퍼를 그대로 넘길 수 있다</summary>
    static (float[] D, float[] L) RunU8(InferenceSession s, Mat img, string dn, string ln)
    {
        if (!img.IsContinuous()) throw new InvalidOperationException("연속 메모리 프레임만 그대로 넘길 수 있다");
        long[] shape = [1, img.Height, img.Width, img.Channels()];
        using var v = OrtValue.CreateTensorValueWithData(OrtMemoryInfo.DefaultInstance, TensorElementType.UInt8, shape, img.Data,
                                                         (long)img.Height * img.Width * img.Channels());
        using var outs = s.Run(Ro, [U8Input], [v], [dn, ln]);
        return (outs[0].GetTensorDataAsSpan<float>().ToArray(), outs[1].GetTensorDataAsSpan<float>().ToArray());
    }

    static (double Med, double P95) TimeIt(IReadOnlyList<Mat> imgs, Action<Mat> f)
    {
        for (int i = 0; i < 3; i++) f(imgs[i % imgs.Count]);   // 첫 호출(OpenCV 초기화·JIT·스레드 풀)을 시간에서 뺀다
        var ms = new double[imgs.Count];
        var sw = new Stopwatch();
        for (int i = 0; i < imgs.Count; i++)
        {
            sw.Restart();
            f(imgs[i]);
            ms[i] = sw.Elapsed.TotalMilliseconds;
        }
        Array.Sort(ms);
        return (ms[ms.Length / 2], ms[Math.Min(ms.Length - 1, (int)(ms.Length * 0.95))]);
    }

    static double Med(List<double> v)
    {
        var a = v.ToArray();
        Array.Sort(a);
        return a[a.Length / 2];
    }

    static bool SameBits(float[][] a, float[][] b) =>
        a.Zip(b).All(p => MemoryMarshal.AsBytes(p.First.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(p.Second.AsSpan())));

    static double MaxDiff(float[][] a, float[][] b) => a.Zip(b).Max(p => Rf.MaxAbs(p.First, p.Second));

    static List<Mat> ReadFrames(string path, int count)
    {
        using var cap = new VideoCapture(path);
        int total = Math.Max(1, (int)cap.Get(VideoCaptureProperties.FrameCount));
        int every = Math.Max(1, total / count);
        var list = new List<Mat>();
        using var m = new Mat();
        for (int i = 0; list.Count < count && cap.Read(m) && !m.Empty(); i++)
            if (i % every == every / 2) list.Add(m.Clone());
        return list;
    }

    static string? FindPublish(string here)
    {
        for (var d = new DirectoryInfo(here); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "publish", "test4.mp4"))) return Path.Combine(d.FullName, "publish");
        return null;
    }

    /// <summary>C1: A 와 같은 OpenCV 단계(float 로 바꾸기 → 쌍선형)를 버퍼를 재사용하며 하고, RGB 바꾸기·정규화·CHW 를 한 번에 (곱하기·더하기로)</summary>
    sealed class FloatFused
    {
        readonly Mat _f = new(), _r = new();

        public unsafe void Run(Mat bgr, int res, float[] dst, bool parallel)
        {
            bgr.ConvertTo(_f, MatType.CV_32FC3, 1.0 / 255);
            Cv2.Resize(_f, _r, new OpenCvSharp.Size(res, res), 0, 0, InterpolationFlags.Linear);
            nint sp = (nint)_r.DataPointer;
            int plane = res * res;
            float kr = 1f / Std[0], kg = 1f / Std[1], kb = 1f / Std[2];
            float br = -Mean[0] / Std[0], bg = -Mean[1] / Std[1], bb = -Mean[2] / Std[2];
            fixed (float* d0 = dst)
            {
                nint dp = (nint)d0;
                void Row(int y)
                {
                    float* p = (float*)sp + (long)y * res * 3;
                    float* oR = (float*)dp + (long)y * res, oG = oR + plane, oB = oG + plane;
                    for (int x = 0; x < res; x++, p += 3)
                    {
                        oR[x] = p[2] * kr + br;
                        oG[x] = p[1] * kg + bg;
                        oB[x] = p[0] * kb + bb;
                    }
                }
                if (parallel) Parallel.For(0, res, Row);
                else for (int y = 0; y < res; y++) Row(y);
            }
        }
    }

    /// <summary>
    /// C2: 원본 uint8(BGR·BGRA)에서 바로 쌍선형 — OpenCV INTER_LINEAR(float)와 같은 탭·가중치(반 화소 중심 · 가장자리는 끝 화소 · 가로 먼저 세로 나중)로
    /// 보간하며 RGB 바꾸기·정규화·CHW 를 한 번에. 전체 프레임을 float 로 바꾸지 않아 비용이 입력 크기가 아니라 출력(res²)에 비례한다
    /// </summary>
    sealed class Direct
    {
        int _sw = -1, _sh = -1, _res = -1;
        int[] _x0 = [], _x1 = [], _y0 = [], _y1 = [];
        float[] _ax = [], _ay = [];

        // OpenCV resize 의 탭 계산과 같게: inv_scale = 출력/입력, scale = 1/inv_scale, f = (d+0.5)·scale − 0.5, s = floor(f)
        static void Taps(int src, int dst, int[] i0, int[] i1, float[] a)
        {
            double scale = 1.0 / ((double)dst / src);
            for (int d = 0; d < dst; d++)
            {
                float f = (float)((d + 0.5) * scale - 0.5);
                int s = (int)MathF.Floor(f);
                f -= s;
                if (s < 0) { f = 0; s = 0; }
                if (s >= src - 1) { f = 0; s = src - 1; }
                i0[d] = s;
                i1[d] = Math.Min(s + 1, src - 1);
                a[d] = f;
            }
        }

        public unsafe void Run(Mat bgr, int res, float[] dst, bool parallel)
        {
            int sw = bgr.Width, sh = bgr.Height, cn = bgr.Channels();
            if ((sw, sh, res) != (_sw, _sh, _res))
            {
                (_x0, _x1, _ax) = (new int[res], new int[res], new float[res]);
                (_y0, _y1, _ay) = (new int[res], new int[res], new float[res]);
                Taps(sw, res, _x0, _x1, _ax);
                Taps(sh, res, _y0, _y1, _ay);
                (_sw, _sh, _res) = (sw, sh, res);
            }
            long step = bgr.Step();
            nint sp = (nint)bgr.DataPointer;
            int plane = res * res;
            float kr = 1f / (255f * Std[0]), kg = 1f / (255f * Std[1]), kb = 1f / (255f * Std[2]);
            float br = -Mean[0] / Std[0], bg = -Mean[1] / Std[1], bb = -Mean[2] / Std[2];
            int[] x0 = _x0, x1 = _x1, y0 = _y0, y1 = _y1;
            float[] ax = _ax, ay = _ay;
            fixed (float* d0 = dst)
            {
                nint dp = (nint)d0;
                void Row(int y)
                {
                    byte* r0 = (byte*)sp + y0[y] * step, r1 = (byte*)sp + y1[y] * step;
                    float fy = ay[y], gy = 1f - fy;
                    float* oR = (float*)dp + (long)y * res, oG = oR + plane, oB = oG + plane;
                    for (int x = 0; x < res; x++)
                    {
                        int i0 = x0[x] * cn, i1 = x1[x] * cn;
                        float fx = ax[x], gx = 1f - fx;
                        float b = (r0[i0] * gx + r0[i1] * fx) * gy + (r1[i0] * gx + r1[i1] * fx) * fy;
                        float g = (r0[i0 + 1] * gx + r0[i1 + 1] * fx) * gy + (r1[i0 + 1] * gx + r1[i1 + 1] * fx) * fy;
                        float r = (r0[i0 + 2] * gx + r0[i1 + 2] * fx) * gy + (r1[i0 + 2] * gx + r1[i1 + 2] * fx) * fy;
                        oR[x] = r * kr + br;
                        oG[x] = g * kg + bg;
                        oB[x] = b * kb + bb;
                    }
                }
                if (parallel) Parallel.For(0, res, Row);
                else for (int y = 0; y < res; y++) Row(y);
            }
        }
    }
}
