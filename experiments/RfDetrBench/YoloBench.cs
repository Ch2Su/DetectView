// YOLO 변형(NMS 를 넣어 내보냄 · 640×384 입력)을 앱 런타임(Windows ML ORT + DirectML)에서 — 노드 배치와 session.Run 시간 (README 'YOLO 남은 여지').
// 모델은 step2/export_trained.py 로 만든다(--nms · --imgsz). 입력은 test4 영상 프레임 30장을 지금 앱처럼(OpenCV 쌍선형 레터박스, 검은 여백) 모델 크기로.
// NMS 는 후보 수(신뢰도 임계값을 넘는 박스)에 따라 시간이 달라서 차가 많은 장면(신호대기 행렬)까지 고르게 넣는다.
// 사용: RfDetrBench.exe yolo <모델.onnx>...
using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.ML.OnnxRuntime;
using OpenCvSharp;

static class YoloBench
{
    public static int Run(string here, string[] paths)
    {
        if (paths.Length == 0) { Console.WriteLine("사용: RfDetrBench.exe yolo <모델.onnx>..."); return 2; }
        string? pub = null;
        for (var d = new DirectoryInfo(here); d != null && pub == null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "publish", "test4.mp4"))) pub = Path.Combine(d.FullName, "publish");
        if (pub == null) { Console.WriteLine("publish/test4.mp4 를 찾지 못했습니다"); return 1; }
        var frames = new List<Mat>();
        using (var cap = new VideoCapture(Path.Combine(pub, "test4.mp4")))
        {
            int total = (int)cap.Get(VideoCaptureProperties.FrameCount), every = Math.Max(1, total / 30);
            using var m = new Mat();
            for (int i = 0; frames.Count < 30 && cap.Read(m) && !m.Empty(); i++)
                if (i % every == every / 2) frames.Add(m.Clone());
        }

        var env = OrtEnv.Instance();
        var dml = env.GetEpDevices().FirstOrDefault(d => d.EpName == "DmlExecutionProvider" && d.HardwareDevice.VendorId == 0x10DE)
                  ?? env.GetEpDevices().FirstOrDefault(d => d.EpName == "DmlExecutionProvider");
        if (dml == null) { Console.WriteLine("DirectML 장치가 없습니다."); return 1; }
        Console.WriteLine($"[입력] test4 {frames.Count}장 ({frames[0].Width}×{frames[0].Height}) · [장치] DirectML · {dml.HardwareDevice.Vendor} · ONNX Runtime {env.GetVersionString()}");
        SessionOptions Opts()
        {
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

        using var ro = new RunOptions();
        foreach (var path in paths)
        {
            string name = Path.GetFileNameWithoutExtension(path);
            using (var so = Opts())
            {
                so.ProfileOutputPathPrefix = Path.Combine(here, "out", "prof_yolo_" + Regex.Replace(name, "[^A-Za-z0-9]+", "_"));
                so.EnableProfiling = true;
                using var sp = new InferenceSession(path, so);
                var (hp, wp) = InputSize(sp);
                var xs = frames.Take(5).Select(f => Letterbox(f, wp, hp)).ToList();
                foreach (var x in xs) Once(sp, x, hp, wp, ro);
                Console.WriteLine($"\n[{name}] 입력 {wp}×{hp} — 노드 배치 (실행 5회 프로파일)\n{Prep.SummarizeProfile(sp.EndProfiling(), 5)}");
            }
            using var so2 = Opts();
            using var s = new InferenceSession(path, so2);
            var (h, w) = InputSize(s);
            var inputs = frames.Select(f => Letterbox(f, w, h)).ToList();
            for (int i = 0; i < 20; i++) Once(s, inputs[i % inputs.Count], h, w, ro);   // 워밍업
            var ms = new List<double>();
            int dets = 0;
            var sw = new Stopwatch();
            for (int pass = 0; pass < 5; pass++)
                foreach (var x in inputs)
                {
                    sw.Restart();
                    var o = Once(s, x, h, w, ro);
                    ms.Add(sw.Elapsed.TotalMilliseconds);
                    if (pass == 0) for (int k = 0; k < o.Length / 6; k++) if (o[k * 6 + 4] > 0.3f) dets++;
                }
            ms.Sort();
            Console.WriteLine($"  session.Run(입력 복사·출력 읽기 포함) 중앙 {ms[ms.Count / 2]:F2} ms · p95 {ms[(int)(ms.Count * 0.95)]:F2} · 프레임당 검출(> 0.3) {dets / (double)inputs.Count:F1}");
        }
        return 0;
    }

    static (int H, int W) InputSize(InferenceSession s)
    {
        var d = s.InputMetadata[s.InputNames[0]].Dimensions;
        return (d[2], d[3]);
    }

    static float[] Once(InferenceSession s, float[] x, int h, int w, RunOptions ro)
    {
        using var v = OrtValue.CreateTensorValueFromMemory((float[])x.Clone(), [1, 3, h, w]);
        using var outs = s.Run(ro, [s.InputNames[0]], [v], s.OutputNames);
        return outs[0].GetTensorDataAsSpan<float>().ToArray();
    }

    /// <summary>지금 앱(YoloLetterbox)과 같게 — 비율 유지 · 가운데 · OpenCV 쌍선형 · 검은 여백 · RGB 0~1 CHW. 입력은 w×h 직사각형일 수 있다</summary>
    static unsafe float[] Letterbox(Mat bgr, int w, int h)
    {
        float s = Math.Min((float)w / bgr.Width, (float)h / bgr.Height);
        int nw = (int)(bgr.Width * s + 0.5f), nh = (int)(bgr.Height * s + 0.5f);
        using var canvas = new Mat(h, w, MatType.CV_8UC3, Scalar.All(0));
        using (var resized = bgr.Resize(new OpenCvSharp.Size(nw, nh), 0, 0, InterpolationFlags.Linear))
        using (var roi = new Mat(canvas, new Rect((w - nw) / 2, (h - nh) / 2, nw, nh)))
            resized.CopyTo(roi);
        int plane = w * h;
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
}
