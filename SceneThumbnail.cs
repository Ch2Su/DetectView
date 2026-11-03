using System;
using OpenCvSharp;

namespace YOLO11_Test
{
    /// <summary>
    /// 화면 전환 감지(SceneChange.cs)에 넣는 작은 흑백 그림 — 프레임을 면적 평균으로 64×36 으로 줄인다.
    /// 앱 재생 루프와 실험 도구(experiments/TrackerCheck 가 소스로 링크)가 <b>같은 코드</b>로 만들어야 도구가 잰 임계값이 앱에서도 그대로 맞는다.
    /// 프레임 하나에 썸네일 + 감지기로 약 0.8 ms (1280×720, TrackerCheck scene 실측 — test5 1280×854·60fps 는 2.0 ms). Mat 두 개를 재사용하므로 한 스레드(재생 루프)에서만 쓴다
    /// </summary>
    internal sealed class SceneThumbnail : IDisposable
    {
        public const int Width = 64, Height = 36;
        public const int Length = Width * Height;

        private readonly Mat _small = new();
        private readonly Mat _gray = new();
        private readonly byte[] _buffer = new byte[Length];

        /// <summary>
        /// 프레임(BGR·BGRA·흑백) → 64×36 흑백. 돌려주는 배열은 다음 호출 때 덮어쓰이므로 필요하면 복사해 둔다
        /// </summary>
        public byte[] Compute(Mat frame)
        {
            Cv2.Resize(frame, _small, new Size(Width, Height), 0, 0, InterpolationFlags.Area);
            Mat gray = _small;
            if (_small.Channels() == 3) { Cv2.CvtColor(_small, _gray, ColorConversionCodes.BGR2GRAY); gray = _gray; }
            else if (_small.Channels() == 4) { Cv2.CvtColor(_small, _gray, ColorConversionCodes.BGRA2GRAY); gray = _gray; }
            gray.GetArray(out byte[] data);
            Buffer.BlockCopy(data, 0, _buffer, 0, Length);
            return _buffer;
        }

        public void Dispose()
        {
            _small.Dispose();
            _gray.Dispose();
        }
    }
}
