using OpenCvSharp;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace YOLO11_Test
{
    /// <summary>SAHI 정밀 분석 — 현재 프레임 1장을 타일로 나눠 검출 후 병합해 표시 (on-demand).</summary>
    public partial class MainWindow
    {
        // SAHI 정밀 분석용: 가장 최근 표시된 원본 프레임(BGR) 보관 (재생 루프가 버퍼를 맞바꿔 채움)
        private Mat? _lastFrame;
        // 그 프레임의 영상 시각(ms, 파일만 의미 있음). _lastFrameLock 안에서 _lastFrame 과 같이 바꾼다 — SAHI 가 분석한 위치를 상태 문구에 보인다
        private double _lastFramePosMs;
        private readonly Lock _lastFrameLock = new();
        // SAHI 분석 중 (UI 스레드 전용). ApplyModeToUi 가 이걸 보고 재생·SAHI 버튼과 슬라이더를 끈다
        private bool _sahiBusy;

        // 현재 표시 중인 프레임을 타일로 나눠 정밀 검출 (재생은 멈춘 상태에서 1회 실행)
        private async void Btn_Sahi_Click(object sender, RoutedEventArgs e)
        {
            if (_yolo == null || _mode == SourceMode.None || _sahiBusy) return;

            // 재생 중이면 일시정지 (프레임 고정 + _yolo 단독 사용 보장)
            if (_isPlaying)
            {
                _playSignal.Reset();
                _isPlaying = false;
                Btn_PlayPause.Content = "재생";
            }

            // 끝날 때까지 SAHI·재생 버튼과 슬라이더를 끈다 (두 번 눌러 겹치지 않게). 슬라이더를 켜 두면 분석 중 시크한 한 장이
            // 화면에 나온 뒤 옛 프레임의 SAHI 결과가 그 위를 덮었다. 되돌리기는 ApplyModeToUi 한 곳 (finally)
            _sahiBusy = true;
            ApplyModeToUi();
            int gen = _loadGeneration;
            Mat? snap = null;
            try
            {
                // 방금 시크했거나 재생 루프가 시크한 위치를 읽는 중이면 그 프레임이 화면에 나올 때까지 기다린다 — 안 그러면
                // 슬라이더 위치가 아닌 옛 프레임을 분석한다 (시크가 아직 대기 중이면 여기서 한 장 보기를 요청한다)
                if (SeekPending) RequestStepFrame();
                bool ready = await WaitForStepFrameAsync(gen, 10_000);
                if (gen != _loadGeneration) return;   // 기다리는 사이 정지·다른 소스로 바꿨다
                if (!ready)
                {
                    // 길이 없는 긴 파일을 되감는 중 등 — 엉뚱한 프레임을 분석하느니 알리고 그만둔다
                    Txt_Status.Text = "시크한 위치를 아직 읽는 중 — 잠시 뒤 다시 누르세요";
                    return;
                }

                double posMs;
                lock (_lastFrameLock)
                {
                    if (_lastFrame != null && !_lastFrame.Empty())
                        snap = _lastFrame.Clone();
                    posMs = _lastFramePosMs;
                }
                if (snap == null)
                {
                    MessageBox.Show("분석할 프레임이 없습니다.");
                    return;
                }

                Txt_Status.Text = "정밀 분석(SAHI) 중...";
                var (image, dets) = await Task.Run(() => RunSahi(snap, gen));
                // 분석 중 다른 파일·카메라·HUD 로 바꿨거나 정지했으면 옛 프레임 결과를 덮어쓰지 않는다
                if (gen != _loadGeneration) return;
                MainImage.Source = image;
                FrameBoxes.Show(dets, image.PixelWidth, image.PixelHeight, "검출 · SAHI");   // 범례 머리에 SAHI 결과임을 (상태바 '(SAHI)'와 같이)
                int count = dets.Count;
                Txt_DetectionCount.Text = $"검출 {count} (SAHI)";
                // 파일이면 분석한 프레임의 위치도 보인다 — 어느 장면을 분석했는지 (UI 테스트도 이걸로 시크한 위치를 분석했는지 본다)
                Txt_Status.Text = _mode == SourceMode.File
                    ? $"정밀 분석 완료: {count}개 ({FormatTime(posMs, Math.Max(_durationMs, posMs) >= 3_600_000)})"
                    : $"정밀 분석 완료: {count}개";
            }
            catch (OperationCanceledException)
            {
                // 분석 중 다른 소스로 바꿔 타일 루프를 멈췄다 — 새 모드의 상태 문구를 덮지 않는다
            }
            catch (Exception ex)
            {
                Txt_Status.Text = "정밀 분석 실패";
                Debug.WriteLine(ex);
            }
            finally
            {
                snap?.Dispose();
                // 분석 중(await) 사용자가 정지·HUD 등으로 모드를 바꿨을 수 있으므로 현재 모드 기준으로 복원한다.
                // (직접 IsEnabled 를 켜면 HUD 모드에서도 SAHI 버튼이 살아나는 등 어긋난다)
                _sahiBusy = false;
                ApplyModeToUi();
            }
        }

        // 전체 프레임 1회 + 640 타일(겹침 30%) 추론 → 좌표 역변환 → 같은 라벨끼리 합쳐 병합 → 영상과 박스 반환
        // gen 이 바뀌면(정지·다른 소스) 남은 추론을 돌리지 않고 OperationCanceledException — 결과는 어차피 버려진다
        private (BitmapSource image, List<Det> dets) RunSahi(Mat frameBgr, int gen)
        {
            int w = frameBgr.Width, h = frameBgr.Height;
            const int tile = 640;
            const double overlap = 0.3;   // 겹침↑ → 객체가 최소 한 타일엔 온전히 들어오도록
            int step = Math.Max(1, (int)(tile * (1 - overlap)));

            var all = new List<(Det D, bool Cut)>();
            // 원본 SAHI(perform_standard_pred)처럼 전체 프레임 검출도 함께 넣는다 — 겹침(192px)보다 넓은 큰 물체는 어느 타일에도
            // 온전히 들어오지 않아 타일 검출로는 조각뿐이다. 프레임이 타일 하나 크기 이하면 타일 검출과 같으니 건너뛴다
            if (w > tile || h > tile)
            {
                if (gen != Volatile.Read(ref _loadGeneration)) throw new OperationCanceledException();
                foreach (var d in DetectTile(frameBgr, 0, 0)) all.Add((d, false));
            }
            foreach (int oy in Sahi.TileOrigins(h, tile, step))
                foreach (int ox in Sahi.TileOrigins(w, tile, step))
                {
                    if (gen != Volatile.Read(ref _loadGeneration)) throw new OperationCanceledException();
                    int tw = Math.Min(tile, w - ox);
                    int th = Math.Min(tile, h - oy);
                    using var roi = new Mat(frameBgr, new OpenCvSharp.Rect(ox, oy, tw, th));
                    // 타일 안쪽 경계에 닿은 박스는 물체가 잘렸을 수 있다 — 병합에서 그런 조각이 낀 묶음만 합친다
                    foreach (var d in DetectTile(roi, ox, oy)) all.Add((d, Sahi.TouchesInnerEdge(d, ox, oy, tw, th, w, h)));
                }

            // 같은 라벨끼리 IoU(0.45)·IoS(0.6)로 묶고, 타일 경계에 잘린 조각이 낀 묶음만 합집합으로 합친다(나머지는 신뢰도 높은 박스 그대로).
            // 지우기만 하던 때(MergeNms)는 잘린 박스의 신뢰도가 더 높으면 온전한 박스가 지워졌고(test5 44.75초), 원본 SAHI 처럼 모두 합치면(MergeNmm)
            // 타일마다 몇 px 씩 다른 박스가 겹쳐 test4 박스의 40% 가 부풀고 옆 차들이 한 박스로 묶였다
            // (2026-10-30 실측·사용자 결정, experiments/TrackerCheck sahi). 병합은 같은 라벨끼리만이라, 같은 자리 car/truck 같은 중복은
            // 그 뒤 화면 재생과 같은 규칙(DuplicateBoxes)으로 뺀다
            var merged = DuplicateBoxes.Remove(Sahi.MergeCutPieces(all, iouThreshold: 0.45, iosThreshold: 0.6));

            // 박스는 영상에 그리지 않는다 — 화면에선 재생 때와 같은 BoxLayer 가 그 위에 그린다
            return (MatToBitmapSource(frameBgr), merged);
        }

        private static BitmapSource MatToBitmapSource(Mat bgr)
        {
            using var bgra = new Mat();
            Cv2.CvtColor(bgr, bgra, ColorConversionCodes.BGR2BGRA);
            int stride = (int)bgra.Step();
            int h = bgra.Height;

            var bs = BitmapSource.Create(bgra.Width, h, 96, 96, PixelFormats.Bgra32, null, bgra.Data, h * stride, stride);
            bs.Freeze();
            return bs;
        }
    }
}
