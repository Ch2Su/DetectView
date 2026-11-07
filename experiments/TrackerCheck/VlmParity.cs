using System.Globalization;
using OpenCvSharp;
using DetectView;

// vlm: 앱의 VLM 설명(루트 VlmCrop.cs · VlmAnnotator.cs)이 실험(experiments/VlmCheck — make_cases.crop_around · run_vlm 4차)과 같은 그림·요청을 만드는지
// 견줄 재료를 쓴다. 견주기는 experiments/VlmCheck/check_app_parity.py 가 한다 — 파이썬이 원본 프레임(PNG)과 박스를 주면 이 명령이
//   <출력>.render.png   VLM 에 보낼 그림 (JPEG 전 — 화소를 실험의 crop_around 와 견준다)
//   <출력>.request.json /api/chat 요청 본문 (질문·스키마·옵션을 실험 4차의 본문과 견준다)
// 을 쓴다. 사용: vlm <프레임.png> <x1> <y1> <x2> <y2> <출력 앞부분>
internal static class VlmParity
{
    public static int Run(string[] args)
    {
        if (args.Length < 6)
        {
            Console.WriteLine("사용법: vlm <프레임.png> <x1> <y1> <x2> <y2> <출력 앞부분>");
            return 2;
        }
        using var frame = Cv2.ImRead(args[0], ImreadModes.Color);
        if (frame.Empty()) { Console.WriteLine($"그림을 못 읽음: {args[0]}"); return 1; }
        double[] b = args[1..5].Select(s => double.Parse(s, CultureInfo.InvariantCulture)).ToArray();
        var det = new Det(b[0], b[1], b[2] - b[0], b[3] - b[1], "truck", 1);
        using var region = VlmCrop.Cut(frame, det);
        if (region == null) { Console.WriteLine("영역이 비었다"); return 1; }
        string prefix = args[5];
        using (var img = VlmCrop.Render(region)) Cv2.ImWrite(prefix + ".render.png", img);
        byte[] jpeg = VlmCrop.Encode(region);
        File.WriteAllBytes(prefix + ".request.json", VlmPrompt.ChatRequest(VlmPrompt.Model, jpeg, "20m"));
        Console.WriteLine($"{prefix}.render.png · .request.json — 영역 {region.Image.Width}×{region.Image.Height}, 박스 {region.Box1}–{region.Box2}, JPEG {jpeg.Length} B");
        return 0;
    }
}
