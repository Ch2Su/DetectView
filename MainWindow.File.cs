using Microsoft.Win32;
using OpenCvSharp;
using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace YOLO11_Test
{
    /// <summary>
    /// 동영상 파일 열기. 화면녹화 webm 처럼 메타데이터(FPS·프레임 수)가 거짓인 VFR 파일은
    /// 재생부터 시작하고 길이는 백그라운드에서 확정한다.
    /// </summary>
    public partial class MainWindow
    {
        private async void Btn_LoadVideo_Click(object sender, RoutedEventArgs e)
        {
            if (_switchingMode) return;   // 다른 모드 전환이 진행 중 (MainWindow.xaml.cs)
            if (_yolo == null)
            {
                MessageBox.Show("YOLO 모델이 준비되지 않았습니다.");
                return;
            }

            var dlg = new OpenFileDialog
            {
                Filter = "Video files (*.mp4;*.avi;*.mkv;*.webm)|*.mp4;*.avi;*.mkv;*.webm|All files (*.*)|*.*"
            };
            if (dlg.ShowDialog() != true) return;   // 취소면 전환 표시를 남기지 않도록 대화상자 뒤에서 잠근다

            _switchingMode = true;
            try
            {
                await TearDownSourceAsync();
                OpenVideoFile(dlg.FileName);
            }
            finally { _switchingMode = false; }
        }

        // 이전 소스는 이미 내렸다. 못 열면 소스 없음 상태로 둔다 (옛 모드가 남지 않게)
        private void OpenVideoFile(string path)
        {
            try
            {
                var cap = new VideoCapture(path);
                if (!cap.IsOpened())
                {
                    cap.Dispose();
                    ResetToIdle("영상을 열 수 없습니다");
                    MessageBox.Show("영상을 열 수 없습니다.");
                    return;
                }

                double fps = cap.Get(VideoCaptureProperties.Fps);
                double frameCount = cap.Get(VideoCaptureProperties.FrameCount);

                _capture = cap;
                _mode = SourceMode.File;
                _currentFrameIdx = 0;
                _seekRefineTargetMs = -1;
                _filePath = path;
                _lastPosMs = 0;
                // 안전망: 드래그 끝을 어떤 이유로 놓쳐 '끄는 중' 표시가 남았어도 다음 파일까지 끌고 가지 않는다
                // (남으면 재생 중 진행 표시가 멈추고 멈춘 채 시크해도 한 장 보기가 안 나온다 — MainWindow.xaml.cs 슬라이더 절)
                _isUserDraggingSlider = false;
                int gen = ++_loadGeneration;

                string fileName = path;
                string shortName = System.IO.Path.GetFileName(fileName);

                // 메타데이터 신뢰성 판단: fps가 정상 범위이고 프레임 수가 있으면 그대로 사용(CFR/mp4)
                bool metaReliable = fps > 0 && fps <= 240 && !double.IsNaN(fps) && frameCount > 0;
                // 메타데이터를 못 믿는 파일은 모두 프레임 시크(Set(PosFrames)) 없이 grab 으로만 이동한다 (ApplyPendingSeek).
                //  - 길이 정보가 없으면(FRAME_COUNT ≤ 0·NaN) OpenCV 프레임 시크가 스트림을 망가뜨린다 (2026-09-23 실측)
                //  - 길이는 있어도 fps 가 실제 속도가 아닌 파일(타임베이스 값 1000 이 fps 로 읽히는 VFR 녹화 등): OpenCV 는 넘긴
                //    프레임 번호를 자기 fps 로 시각에 바꿔 그 앞 키프레임으로 간 뒤, 키프레임 시각을 그 fps 의 프레임 번호로 놓고
                //    실제 프레임 한 장마다 1씩 세며 따라간다 → 어느 단위로 번호를 넘겨도 목표 시각에 서지 않는다.
                //    지금까지처럼 분석 뒤 실측 평균 fps 로 번호를 만들면 '처음부터 N번째 프레임'에 서서, 프레임이 드문 정지 구간
                //    뒤에선 목표를 지나친다 — 정밀화는 앞으로만 따라가므로 되돌아오지 못한다
                //    (2026-10-31 tests/ui/media/vfr_fps1000_dur.webm — FPS=1000·FRAME_COUNT=24007, 실제 550장·평균 22.9fps:
                //     16초 → 18.06초, 13초 → 15.76초, 9초 → 12.51초에 섰다. grab 으로는 16.009·13.024·9.015초, 시크 한 번 3~39 ms).
                //    분석의 시크 탐침(ProbeSeekConsistent)이 통과해 fps 1000 을 그대로 쓰면 (목표 − 키프레임) ms 만큼의 '프레임'을
                //    따라가 측정 9번 중 7번은 파일 끝까지 갔다. 키프레임이 목표 바로 앞이면 탐침 한 번은 우연히 맞을 수 있으므로
                //    탐침 결과와 상관없이 grab 으로 간다 (탐침은 길이를 어떻게 구할지만 정한다 — AnalyzeAsync)
                _seekByGrab = !metaReliable;
                if (metaReliable)
                {
                    // mp4 등: 분석 불필요, 즉시 재생
                    _vfr = false;
                    _fps = fps;
                    _totalFrames = (int)frameCount;
                    _durationMs = _totalFrames / _fps * 1000.0;

                    VideoSlider.Maximum = _durationMs > 0 ? _durationMs : 1;
                    VideoSlider.Value = 0;
                    ApplyModeToUi();
                    Txt_Status.Text = $"파일: {shortName}";
                }
                else
                {
                    // 의심스러운 메타데이터(화면녹화 fps=1000 등): 재생은 즉시 시작하고
                    // 길이/슬라이더는 백그라운드 분석 완료 후 활성화 (대기 시간 제거)
                    _vfr = true;          // 위치는 POS_MSEC로 추적(mp4·webm 모두 read 중엔 정확)
                    _totalFrames = 0;
                    _durationMs = 0;
                    _fps = 30.0;          // 임시값 (분석 후 보정)

                    VideoSlider.Maximum = 1;
                    VideoSlider.Value = 0;
                    ApplyModeToUi();      // _durationMs==0 → 슬라이더 비활성
                    Txt_Status.Text = $"분석 중(재생 가능): {shortName}";

                    _ = AnalyzeAsync(fileName, fps, frameCount, gen);
                }

                // 이전 파일의 시크 요청, 위의 슬라이더 초기화(Value=0)가 남긴 요청을 버린다
                _pendingSeekMs = double.NaN;
                _atEof = false;

                // 영상 옆 구역 파일(<영상>.roi.json)을 읽는다 (재생 시작 전에 — 시작하면 편집이 꺼진다). 못 읽으면 사유를 상태바에
                string? roiWarning = LoadRoi(path, cap.Get(VideoCaptureProperties.FrameWidth), cap.Get(VideoCaptureProperties.FrameHeight));
                if (roiWarning != null) Txt_Status.Text = roiWarning;

                StartPlayback();
            }
            catch (Exception ex)
            {
                DisposeCapture();
                ResetToIdle("영상 로드 실패");
                MessageBox.Show($"영상 로드 실패:\n{ex.Message}");
            }
        }

        // 의심스러운 메타데이터(fps=1000 등) 파일을 백그라운드로 분석해 길이/슬라이더를 확정.
        //  - 프레임 seek가 일관적이면(mp4 변환본 등) 메타데이터를 그대로 사용 → 느린 스캔 생략
        //  - 일관적이지 않으면(화면녹화 webm) grab-scan으로 실제 프레임 수/길이 측정
        private async Task AnalyzeAsync(string path, double fps, double frameCount, int gen)
        {
            bool consistent = await Task.Run(() => ProbeSeekConsistent(path, fps, frameCount));
            if (gen != _loadGeneration || _mode != SourceMode.File) return;

            string shortName = System.IO.Path.GetFileName(path);
            int frames;
            double durationMs;
            double realFps;

            if (consistent)
            {
                // 메타데이터 신뢰 가능 → 스캔 불필요 (빠름)
                frames = (int)frameCount;
                durationMs = frameCount / fps * 1000.0;
                realFps = fps;
            }
            else
            {
                // 메타데이터 불일치 → 실제 프레임 수/길이 측정 (느릴 수 있음). 그새 다른 소스로 바꾸면 중간에 멈춘다
                // (예전엔 끝까지 훑은 뒤에야 결과를 버려, 긴 파일이면 수 초~15초 동안 파일·디스크를 붙잡았다)
                var (f, d) = await Task.Run(() => ScanFrames(path, () => gen != Volatile.Read(ref _loadGeneration)));
                if (gen != _loadGeneration || _mode != SourceMode.File) return;
                if (f <= 0 || d <= 0)
                {
                    Txt_Status.Text = $"파일: {shortName} (길이 분석 실패, 재생만 가능)";
                    return;
                }
                frames = f;
                durationMs = d;
                realFps = f / (d / 1000.0);
            }

            // 이 경로의 파일은 열 때부터 _seekByGrab 이라 시크는 _fps·_totalFrames 를 쓰지 않는다 (위치는 PosMsec, 시크는 grab).
            // 쓰이는 건 슬라이더 길이뿐 — 탐침이 우연히 통과한 fps=1000 녹화도 frameCount/fps 가 컨테이너 길이라 맞다
            _totalFrames = frames;
            _durationMs = durationMs;
            _fps = realFps;

            VideoSlider.Maximum = _durationMs;
            ApplyModeToUi();   // 슬라이더를 켠다 (SAHI 분석 중이면 끝날 때까지 꺼 둔다 — 직접 켜면 분석 중에 살아났다)
            Txt_Status.Text = $"파일: {shortName}";
        }

        // 보고된 frameCount의 중간 지점으로 프레임 seek 했을 때 타임스탬프가 일관적인지 확인.
        // 일관적이면 frameCount/fps 메타데이터를 신뢰할 수 있어 느린 grab-scan을 생략한다.
        private static bool ProbeSeekConsistent(string path, double fps, double frameCount)
        {
            if (frameCount <= 0 || fps <= 0) return false;
            try
            {
                using var p = new VideoCapture(path);
                if (!p.IsOpened()) return false;

                int target = (int)(frameCount / 2);
                p.Set(VideoCaptureProperties.PosFrames, target);
                if (!p.Grab()) return false;

                double actual = p.Get(VideoCaptureProperties.PosMsec);
                double expected = target / fps * 1000.0;
                return Math.Abs(actual - expected) <= Math.Max(2000.0, expected * 0.1);
            }
            catch
            {
                return false;
            }
        }

        // VFR/메타데이터 불량 파일: grab()으로 디코딩 없이 끝까지 훑어
        // 실제 총 프레임 수와 길이(ms)를 실측한다. (demux만 하므로 비교적 빠름)
        // stale 이 true 가 되면(다른 소스로 바뀜) 중간에 그만둔다 — 결과는 어차피 버려진다
        private static (int frames, double durationMs) ScanFrames(string path, Func<bool> stale)
        {
            try
            {
                using var scan = new VideoCapture(path);
                if (!scan.IsOpened()) return (0, 0);

                int count = 0;
                double last = 0;
                while (scan.Grab())
                {
                    count++;
                    double m = scan.Get(VideoCaptureProperties.PosMsec);
                    if (m > last) last = m;
                    if ((count & 255) == 0 && stale()) return (0, 0);
                }
                return (count, last);
            }
            catch
            {
                return (0, 0);
            }
        }
    }
}
