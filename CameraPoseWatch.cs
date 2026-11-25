using System;
using System.Collections.Generic;
using System.Linq;
using OpenCvSharp;

namespace DetectView
{
    /// <summary>자세 지켜보기 파라미터 — 값은 TrackerCheck <c>poseseries</c>·<c>posewatch</c> 실측(2026-11-21, test1~6·사고 영상)과 2026-11-22 검토 뒤 실측으로 골랐다</summary>
    internal sealed record PoseWatchOptions
    {
        /// <summary>지켜볼 때 이만큼(영상 ms)마다 한 번 맞춰 본다 — 한 번에 특징점·맞춤 약 6~11 ms(1280×720~1620×1080)라 프레임마다는 못 한다</summary>
        public double CheckMs { get; init; } = 1000;
        /// <summary>움직이는 중엔 이만큼마다 — 멈췄는지 본다</summary>
        public double MovingCheckMs { get; init; } = 500;
        /// <summary>비교 기준(최근 화면)을 새로 잡는 간격. 오래 두면 연기·정체 같은 내용 변화로 맞춤이 약해지고, 자주 바꾸면 자세 오차가 쌓인다</summary>
        public double KeyRefreshMs { get; init; } = 10000;
        /// <summary>기준을 새로 잡는 건 인라이어가 평소의 이 비율 이상인 성한 화면일 때만 — 연기 띠가 막 덮은 화면을 기준으로 잡으면 연기가 걷힐 때 헛경보가 났다(합성 검사).
        /// 내용이 오래 바뀌어 있으면 평소 수준(가운데값)이 따라 내려와 결국 새로 잡힌다</summary>
        public double RefreshMinRatio { get; init; } = 0.8;
        /// <summary>두 화면의 이동·배율 차이가 이 안쪽이면 카메라가 서 있다 (서 있는 영상의 1초 간격 확인은 0.0~0.7%)</summary>
        public double StillShift { get; init; } = 0.01;
        public double StillScale { get; init; } = 0.01;
        /// <summary>
        /// 인라이어가 앞 확인들의 가운데값에 대해 이 비율 아래면 장면 맞춤이 무너진 것 — 움직이는 중으로 본다. 카메라가 서 있으면 압축 잡음까지 픽셀이 같아
        /// 약한 무늬도 맞아 인라이어가 많은데(test6 가운데 597) 조금만 움직여도 그것들이 사라지고 화면 고정 글자만 제자리로 남는다(팬 26% · 되돌림 33% · 줌 25%).
        /// 서 있는 영상의 최저는 57% (1초 간격, 2026-11-21 poseseries — test1~5·사고 영상 넷·test6).
        /// 비율이라 영상마다 평소 수준을 알아야 한다 — 인라이어 수·비율·덮는 넓이·흑백 그림 차이 같은 절대 기준은 영상끼리 겹쳐 쓸 수 없었다
        /// (서 있는 상계-노원 영상이 특징점의 13% · 글자만 맞은 test6 이 16%, 2026-11-22). 그래서 다시 시작해도 마지막으로 안 평소 수준을 이어 쓴다
        /// </summary>
        public double CollapseRatio { get; init; } = 0.4;
        /// <summary>가운데값을 낼 앞 확인 수</summary>
        public int BaselineChecks { get; init; } = 5;
        /// <summary>이만큼 지나도 멈추지 않으면 그 자리에서 판정한다 (계속 도는 카메라에서 상태가 안 풀리지 않게). 추적기가 멈춘 트랙을 버리는 10초(Tracker.StationaryGapMs)보다
        /// 넉넉히 짧게 — 움직이는 증거가 있는 동안은 추적기에 넣지 않으므로 10초를 채우면 멈춘 차의 트랙이 끝나 '나감 (추적 끝)'·유고 해소가 났다(2026-11-22 검토)</summary>
        public double MaxMovingMs { get; init; } = 8000;
        /// <summary>
        /// 멈춘 뒤 움직임 앞뒤를 견줄 때의 그림 폭·특징점(SIFT). 지켜보기의 ORB(가로 480)로는 카메라가 움직인 앞뒤의 장면 짝이 수십 개뿐이라
        /// test6 의 2.6% 팬을 배율 0.894 로 잘못 재고 줌은 못 쟀다. 가로 960 SIFT 는 팬을 인라이어 296 · 이동 2.6% · 배율 1.003, 줌을 51~55 · 배율 0.72~0.73 으로 쟀고
        /// 서 있는 앞뒤에선 헛움직임이 없었다(인라이어 14). 무거워(그림 하나에 수십 ms) 움직이기 시작할 때·멈춘 뒤에만 쓴다
        /// </summary>
        public int ConfirmWidth { get; init; } = 960;
        public int ConfirmFeatures { get; init; } = 2000;
        /// <summary>앞뒤 견주기에서 움직임으로 믿을 맞춤 — 제자리 짝을 뺀 나머지의 이 비율 이상을 설명해야 한다 (줌 28~30%, 서 있는 앞뒤의 우연한 맞춤 16%)</summary>
        public double ConfirmMinExplained { get; init; } = 0.2;
        /// <summary>
        /// 카메라 움직임으로 믿으려면 움직임 인라이어가 8×6 격자(<see cref="ViewMatcher.Spread"/>)의 이 칸 수 이상에 퍼지고, 제자리 짝이 퍼진 칸 수의 <see cref="MotionSpreadRatio"/> 배 이상이어야 한다.
        /// 카메라가 움직이면 장면 전체가 같이 움직여 움직임이 화면에 퍼지고 제자리로 남는 건 화면 고정 글자 몇 덩이뿐이다. 서 있는 카메라에서도 차·사람·소방차의 움직임이
        /// '믿을 만한 움직임'으로 재지는데(test6 이 서 있는 5초 간격 53쌍 중 11쌍 — 배율 0.67~1.34 · 이동 4.1% 까지, 관설3교 사고 영상 둘도) 그 움직임은 7~13칸에 몰리고
        /// 그동안 서 있는 장면은 21~39칸에 걸쳐 제자리다. 진짜 줌·팬은 움직임 15~35칸 · 제자리(글자) 16~17칸 (2026-11-22, SIFT 960). 거르지 않으면 차의 움직임을 카메라 움직임으로 잰 뒤
        /// 서 있는 장면 전체를 화면 고정 글자로 배워 화면 대부분을 식별에서 뺄 수 있었다. 움직임이 퍼진 정도는 앞뒤 화면 중 큰 쪽(<see cref="ViewMatch.SpreadMax"/>)으로 잰다 —
        /// 줌인은 앞 화면의 가운데만 뒤 화면 전체로 옮겨져 앞 화면 쪽으로만 재면 test6 의 거꾸로 된 줌(1417.5 → 1416초)이 8~11칸으로 걸러졌다(검토 실측)
        /// </summary>
        public int MinMotionSpread { get; init; } = 12;
        public double MotionSpreadRatio { get; init; } = 0.6;
        /// <summary>앞뒤 견주기에서 카메라 움직임이 아니고 제자리 짝이 이 칸 수(8×6 중) 이상에 퍼졌으면 카메라는 그 자리다(장면이 통째로 제자리 — 서 있는 앞뒤는 대개 25~39칸)</summary>
        public int StillSpread { get; init; } = 24;
        /// <summary>
        /// 서 있다는 맞춤의 인라이어가 평소의 이 배를 넘으면 평소 수준이 화면 고정 글자만 맞은 '제자리'였을 수 있다(지켜보기를 시작하자마자 카메라가 움직여 첫 확인부터 글자만 맞았다) —
        /// 처음 화면과 SIFT 로 견줘 본다. test6: 글자만 맞은 '제자리' 103~132, 장면까지 맞으면 600 안팎
        /// </summary>
        public double JumpRatio { get; init; } = 2.5;
        /// <summary>
        /// ORB(가로 480) 맞춤이 카메라 움직임 꼴인지 볼 때의 퍼짐 기준 — 그 장면이 서 있을 때 맞춤이 퍼지는 칸 수(8×6, 앞 확인들의 가운데값)의 이 배 이상(최소 OrbMinSpreadFloor 칸).
        /// 고정 12칸(SIFT 960 실측값)을 ORB 에 그대로 쓰면 무늬가 몇 줄에만 있는 장면(역광 사고 영상 충훈 — 서 있어도 10~16칸)에선 진짜 팬도 '화면 안 물체'가 돼
        /// 0.5%/초 팬을 끝까지 못 잡았다(2차 검증 실측 — 예전 판은 6% 에서 잡음). 장면을 아직 모르면 MinMotionSpread
        /// </summary>
        public double OrbSpreadRatio { get; init; } = 0.6;
        public int OrbMinSpreadFloor { get; init; } = 6;
        /// <summary>연기 같은 내용 변화로 '멈추지 않은 채 시간 초과' 끝에 그 자리로 이었으면, 맞춤이 그 전 평소 수준의 이 비율까지 돌아왔을 때(장면이 다시 보일 때) 처음 화면과 SIFT 로 한 번 견준다 —
        /// 연기에 가려진 동안 카메라가 움직였어도 잡게(예전엔 인라이어가 2.5배로 뛰지 않으면 영영 못 잡았다, 2차 검증)</summary>
        public double ClearRatio { get; init; } = 0.7;
        /// <summary>글자 짝을 뺀 나머지만 카메라처럼 옮겨졌을 때 처음 화면과 SIFT 로 견주는 최소 간격(영상 ms) — 서 있는 카메라의 차 행렬·잡음에서도 드물게 생긴다(test6 18분에 7번).
        /// 무늬가 몇 줄인 장면의 느린 팬(초당 0.5%)은 이 간격이어도 범위(6%)를 넘은 뒤 몇 초 안에 잡힌다</summary>
        public double SceneCheckGapMs { get; init; } = 5000;
        /// <summary>서 있는 동안 이만큼(영상 ms)마다 지금 화면을 그 뷰의 보조 기준 화면으로 더한다(ViewRegistry.AddRecent) — 시크해 돌아왔을 때 내용이 바뀐 같은 자세를 알아보게</summary>
        public double RecentEveryMs { get; init; } = 60000;
    }

    internal enum PoseOutcome
    {
        /// <summary>확인할 때가 아니었다 · 지켜보지 않는다</summary>
        None,
        /// <summary>카메라가 그 자리다</summary>
        Still,
        /// <summary>움직이는 중(일 수 있다) — 멈출 때까지 따라간다. 추적기를 멈출지는 <see cref="CameraPoseWatch.Moving"/>(움직이는 증거가 있을 때만)</summary>
        Moving,
        /// <summary>멈췄고 다른 자세다 — 화면 전환으로 처리하고 뷰를 다시 알아본다(지켜보기는 꺼진다 — 식별한 뒤 Start)</summary>
        Moved,
        /// <summary>멈췄고 그 자리다(되돌아왔거나 허용 범위 안의 작은 팬·줌, 장면 내용만 바뀐 것) — 이어서 지켜본다</summary>
        Resumed,
    }

    /// <param name="Measured">Moved 일 때 움직임을 재서 뷰의 허용 범위 밖임을 확인했다 — 그때만 Overlay 를 글자로 배우고 떠난 뷰를 다음 식별 후보에서 뺀다(ViewSession).
    /// 재지 못한 Moved(앞 화면과 견줄 수 없을 만큼 바뀜)는 보통의 화면 전환처럼 다시 식별만 한다</param>
    /// <param name="Overlay">Measured Moved 일 때 움직임 앞뒤에서 제자리로 맞은 짝(= 화면 고정 글자, 가로 480 좌표) — <see cref="ViewRegistry.LearnOverlay"/> 에 넘긴다.
    /// 빈 목록 = 글자 짝이 없었다(글자 없는 영상), null = 가르지 못했다 — 잰 움직임이 작아 제자리 짝이 글자인지 장면인지 알 수 없다(글자가 덮였다고 보면 안 된다 — ViewSession)</param>
    /// <param name="Reason">움직임으로 본 까닭 · 판정 (디버그·실험 도구 표시용)</param>
    internal readonly record struct PoseStep(PoseOutcome Outcome, int Inliers = 0, int Baseline = 0, double Shift = 0, double Scale = 1,
                                             bool Measured = false, IReadOnlyList<Point2d>? Overlay = null, string Reason = "");

    /// <summary>빠른 전환 앞뒤의 ORB 맞춤 (<see cref="CameraPoseWatch.Cut"/>). Pinned = 두 화면에서 같은 자리에 맞은 짝(가로 480 앞 화면 좌표 — 화면 고정 글자 후보), Features = 두 화면 특징점 수 중 작은 것,
    /// Width·Height = 가로 480 그림 크기, Still = 앞 화면과 이 화면이 '서 있는' 맞춤이다(평소 수준만큼 제자리로 맞음 — 카메라는 그대로이고 내용만 크게 바뀐 것: 가까이 선 큰 차 · 번쩍이는 경광등)</summary>
    internal readonly record struct CutInfo(List<Point2d> Pinned, int Features, int Width, int Height, bool Still);

    /// <summary>
    /// 자세 지켜보기 (2026-11-21, 2026-11-22 검토 뒤 고침) — 뷰를 알아본 뒤 카메라가 그 뷰의 허용 범위(이동 6% · 배율 0.9~1.12) 밖으로 움직였는지 1초마다 ORB 맞춤으로 본다.
    /// 화면 전환 감지(<see cref="SceneChangeDetector"/>)는 연속한 두 프레임의 밝기 차이로 보므로 천천히 하는 줌·팬은 못 잡고(test6 1416초 줌아웃 — d 최대 3.4),
    /// 뷰 식별도 영상을 열 때·시크·전환 뒤에만 해서 그 사이 카메라가 옮겨 가면 구역이 어긋난 채 남았다.
    /// <para>
    /// 지켜보기: 비교 기준은 최근 화면(10초마다 새로)이고 그 화면의 자세(뷰 기준 화면 → 비교 기준 호모그래피)를 둔다. 1초마다 (글자 짝을 뺀 나머지만 카메라처럼 옮겨졌으면 먼저 처음 화면(닻)과 SIFT 로 견주고 — 아래)
    /// ① 제자리이고(또는 맞춤이 4×3 칸 4개 미만에 몰림) 인라이어가 평소만큼이면 서 있다 — 기준을 새로 잡을 때 쌓인 자세가 허용 범위 밖이면 움직임으로 본다
    /// ② 장면이 화면에 퍼진 채(그 장면이 서 있을 때만큼) 움직였으면 자세를 이어 붙여 허용 범위 밖이면 움직이는 중, 안이면 그 자리에서 다시 잰다(추적은 멈추지 않는다)
    /// ③ 인라이어가 평소만큼인데 몇 칸에 몰려 움직였으면 화면 안의 물체 — 자세·기준을 그대로 둔다
    /// ④ 인라이어가 평소의 40% 아래로 무너졌거나 맞춤이 안 되면 움직이는 중 '일 수 있다' — 화면 고정 글자는 카메라가 움직여도 제자리라, 장면이 바뀌면
    /// 전체 맞춤이 글자만으로 '제자리'가 된다(test6 줌: 597 → 131, 칸 7). 연기·큰 차도 맞춤을 무너뜨리니 이것만으론 카메라가 움직인 증거가 아니다.
    /// 맞춤은 글자를 빼지 않고 한다 — 빼면 남은 장면이 몇 칸에 몰린 연기 낀 화면에서 흔들려 헛경보가 났다.
    /// 평소 수준을 처음 잡을 때(그 뷰에서 믿을 만한 수준을 모를 때)·연기가 걷힐 때·인라이어가 몇 배로 뛸 때·글자를 뺀 나머지만 카메라처럼 옮겨졌을 때는 처음 화면(닻)과 SIFT 로 한 번 견준다.
    /// </para>
    /// <para>
    /// 움직이는 중엔 0.5초마다 이웃한 두 화면이 '서 있는' 맞춤이 되는지 보고(장면이 퍼진 채 같이 옮겨졌으면 움직이는 증거로 적는다), 멈추면 가로 960 SIFT 로
    /// 처음 화면(닻) · 앞 움직임 전에 서 있던 화면(집) · 움직이기 전 화면과 견줘(제자리 짝을 뺀 나머지로 움직임을 잰다 — 움직임이 화면에 퍼졌을 때만 카메라 움직임, 장면이 통째로
    /// 제자리면 '그 자리') 판정한다: 자세는 직접 잰 것(닻 › 집)을 먼저 쓴다(이어 붙이기만 하면 어긋남이 쌓였다). 허용 범위 밖이면 Moved(Measured — 제자리 짝을 글자로 돌려준다), 안이면 Resumed.
    /// 재지 못하면 앞 화면과 다시 평소만큼 맞을 때 Resumed, 멈추지 못한 채 시간이 다 됐고 움직이는 증거도 없으면(연기 같은 내용 변화) Resumed, 그 밖엔 재지 못한 Moved.
    /// </para>
    /// ORB·SIFT·매처를 재사용하므로 한 스레드(재생 루프)에서만 쓴다. 프레임은 쥐고 있지 않는다(서명과 작은 흑백 그림만 남긴다)
    /// </summary>
    internal sealed class CameraPoseWatch : IDisposable
    {
        private enum State { Off, Watching, Moving }

        /// <summary>한 화면의 SIFT 특징점 (가로 ConfirmWidth 흑백에서) — 움직임 앞뒤를 견줄 때. 기술자 Mat 을 쥐고 있으니 다 쓰면 Dispose</summary>
        private sealed class SiftSet(KeyPoint[] points, Mat descriptors, int width, int height) : IDisposable
        {
            public KeyPoint[] Points { get; } = points;
            public Mat Descriptors { get; } = descriptors;
            public int Width { get; } = width;
            public int Height { get; } = height;
            public void Dispose() => Descriptors.Dispose();
        }

        /// <summary>SIFT 앞뒤 견주기의 결과. Motion = 카메라 움직임으로 믿는 호모그래피(가로 480 좌표, 아니면 null), NoMove = 카메라가 그 자리다(글자 마스크 밖의 장면이 통째로 제자리),
        /// Pinned = 제자리 짝(가로 480 앞 화면 좌표), Explained = 움직임 인라이어 수</summary>
        private readonly record struct MoveResult(double[]? Motion, bool NoMove, List<Point2d> Pinned, int Explained);

        private readonly PoseWatchOptions _o;
        private readonly ViewIdentityOptions _v;
        private readonly ViewMatcher _m = new();
        private OpenCvSharp.Features2D.SIFT? _sift;   // OpenCvSharp 4.13 은 SIFT 를 Features2D 이름공간에 둔다
        private BFMatcher? _l2;
        private State _state;
        private OverlayMask? _overlay;                   // 이번 확인 때의 배운 글자 — '그 자리'를 볼 때 글자로 제자리인 짝은 빼고 센다
        private ViewSignature? _key;                     // 비교 기준 (최근 화면)
        private Mat? _keyGray;                           // 그 화면의 가로 ConfirmWidth 흑백 — 움직임 앞뒤를 SIFT 로 견줄 때
        private double[] _keyPose = ViewMatcher.Identity;   // 뷰 기준 화면 → 비교 기준
        private double _keySinceMs;
        private bool _keyIsAnchor;                       // 비교 기준이 아직 지켜보기를 시작한 화면(닻) 그대로다
        private Mat? _anchorGray;                        // 닻: 지켜보기를 시작한 화면 — 자세를 앞 자세에 이어 붙이기만 하면 어긋남이 쌓여서 멈출 때마다 이것과 직접 견준다
        private SiftSet? _anchorSift;
        private double[] _anchorPose = ViewMatcher.Identity;   // 뷰 기준 화면 → 닻 (식별의 호모그래피 — 새 뷰면 제자리)
        // 집: 앞 움직임 바로 전에 서 있던 화면(그 자세와 함께) — 카메라가 갔다가 돌아오면 이것과 직접 견줘 자세를 바로잡는다(닻이 오래돼 장면이 달라 못 잴 때)
        private Mat? _homeGray;
        private SiftSet? _homeSift;
        private double[] _homePose = ViewMatcher.Identity;
        private readonly List<int> _history = [];       // 서 있을 때의 인라이어 — 가운데값이 맞춤의 평소 수준
        private readonly List<int> _standSpreads = [];  // 서 있는(제자리) 맞춤이 퍼진 칸 수(8×6) — 그 장면에서 '화면에 퍼짐'의 기준
        private int _level;                              // 평소 수준 — 다시 시작한 직후(앞 확인이 없을 때)에도 무너짐을 잰다. Start·Stop 으로 지우지 않는다(SetLevel)
        private bool _levelTrusted;                      // 평소 수준을 처음 화면과 SIFT 로 확인했다(또는 확인된 수준을 넘겨받았다) — 아니면 글자만 맞은 '제자리'가 평소일 수 있다
        private int _peakTrusted;                        // 확인된 평소 수준 가운데 가장 큰 것(넘겨받은 것 포함) — 연기가 걷혔나를 잴 바탕(ClearRatio)
        private double _nextSceneCheckMs;                // 글자를 뺀 장면이 옮겨졌다고 처음 화면과 견줄 수 있는 다음 시각 (SceneCheckGapMs)
        private double _nextMs;
        private bool _verifyWhenClear;                   // 연기 같은 내용 변화로 시간 초과 끝에 그 자리로 이었다 — 장면이 다시 보이면 처음 화면과 견준다
        private int _clearLevel;                         // 그 전 평소 수준
        private ViewSignature? _before;                  // 움직이기 전 마지막 비교 기준 — 멈춘 뒤 이것과 견준다
        private Mat? _beforeGray;
        private SiftSet? _beforeSift;                    // 움직이기 전 화면의 SIFT (그 화면이 닻이면 닻의 것을 쓰고 이건 비워 둔다 — 둘을 같은 객체로 두지 않는다)
        private double[] _beforePose = ViewMatcher.Identity;
        private int _beforeBaseline;
        private bool _beforeIsAnchor;
        private ViewSignature? _last;                    // 움직이는 중 바로 앞 확인
        private double _movingSinceMs;
        private string _reason = "";
        private bool _motionSeen;                        // 이번 움직임에서 카메라가 움직인 증거를 봤다 (장면이 화면에 퍼진 채 같이 옮겨짐)
        private readonly List<int> _stepInliers = [];   // 움직이는 중 이웃한 화면끼리의 인라이어
        private int _lastSeed;                           // 마지막 움직임 판정 때의 새 평소 수준 (AdoptSeedLevel)
        private Mat? _movedBefore;                       // 마지막 잰 Moved 의 움직이기 전 화면(가로 480 흑백) — ViewSession 이 떠난 뷰를 확인할 때 가져간다

        public CameraPoseWatch(PoseWatchOptions? options = null, ViewIdentityOptions? view = null)
        {
            _o = options ?? new PoseWatchOptions();
            _v = view ?? new ViewIdentityOptions();
        }

        public PoseWatchOptions Options => _o;

        /// <summary>지켜보는 중 (Start 뒤 Moved·Stop 전)</summary>
        public bool Active => _state != State.Off;

        /// <summary>
        /// 카메라가 움직이는 중 — 움직이는 증거(장면이 화면에 퍼진 채 같이 옮겨짐)를 봤을 때만. 루프는 이 동안 화면 전환 감지의 '움직임'처럼 추적기에 넣지 않는다.
        /// 맞춤이 무너지기만 한 동안은 아니다(연기·큰 차일 수 있다) — 예전엔 그런 동안에도 최대 10초 추적·구역 이벤트·유고가 멈춰, 멈춘 차의 트랙이 끝나고 유고가 다시 선언됐다(2026-11-22 검토)
        /// </summary>
        public bool Moving => _state == State.Moving && _motionSeen;

        /// <summary>움직임을 따라가는 중 — 증거와 상관없이 (실험 도구 표시용)</summary>
        public bool Following => _state == State.Moving;

        /// <summary>이 시각에 확인할 차례인가 (아니면 프레임을 흑백으로 줄이는 비용도 들지 않게 Check 를 부르지 않는다)</summary>
        public bool Due(double ms) => _state != State.Off && ms >= _nextMs;

        /// <summary>지금 비교 기준의 자세 — 뷰 기준 화면 → 비교 기준 호모그래피 (복사본). 서 있는 화면을 보조 기준으로 더할 때 함께 넘긴다</summary>
        public double[] KeyPose => (double[])_keyPose.Clone();

        /// <summary>평소 수준과 그것을 믿을 수 있는지 (ViewSession 이 뷰마다 기억했다 그 뷰로 돌아오면 넘겨준다)</summary>
        public int Level => _level;
        public bool LevelTrusted => _levelTrusted && _level > 0;

        /// <summary>
        /// 다음 지켜보기의 평소 수준을 정한다(Start 전에) — 같은 뷰로 돌아오면 그 뷰에서 확인한 수준을, 처음 보는 뷰면 0(모름). 다른 뷰의 수준을 이어 쓰면 무늬가 다른 화면에서
        /// 첫 확인부터 무너진 것으로 봤고, 수준을 버리면 시작하자마자 한 줌·팬을 글자만 맞은 '제자리'로 받아 놓쳤다(2026-11-22 검토 — test6 1416초 근처로 시크)
        /// </summary>
        public void SetLevel(int level, bool trusted)
        {
            _level = Math.Max(0, level);
            _levelTrusted = trusted && _level > 0;
            _peakTrusted = _levelTrusted ? _level : 0;
        }

        /// <summary>마지막 움직임 판정에서 잰 새 평소 수준을 쓴다(믿지는 않는다) — 재지 못한 움직임 뒤 같은 뷰로 조용히 다시 시작할 때, 연기 낀 지금 수준으로(맑을 때 수준이면 곧바로 또 무너졌다)</summary>
        public void AdoptSeedLevel()
        {
            if (_lastSeed <= 0) return;
            _level = _lastSeed;
            _levelTrusted = false;
        }

        /// <summary>마지막 잰 Moved 의 움직이기 전 화면(가로 480 흑백)을 넘긴다 — 받은 쪽이 해제한다. 없으면 null</summary>
        public Mat? TakeMovedBefore()
        {
            var m = _movedBefore;
            _movedBefore = null;
            return m;
        }

        /// <summary>지금 비교 기준(움직이는 중이면 움직이기 전 화면)의 가로 480 흑백 복사본 — 빠른 전환 앞 화면을 뷰 기준 화면과 견줄 때. 지켜보지 않으면 null. 받은 쪽이 해제한다</summary>
        public Mat? KeyFrame480()
        {
            var g = _state == State.Moving ? _beforeGray : _keyGray;
            return g == null ? null : Gray(g, ViewMatcher.BaseWidth);
        }

        /// <summary>뷰를 알아본 화면에서 시작한다. refToFrame = 뷰 기준 화면 → 이 화면 호모그래피(식별 결과 — 새 뷰면 null = 제자리). 이 화면이 닻이 된다</summary>
        public void Start(Mat frame, double[]? refToFrame, double ms)
        {
            Stop();
            using (var g = ViewMatcher.PrepareGray(frame)) _key = _m.Compute(g);
            _keyGray = Gray(frame, _o.ConfirmWidth);
            _keyPose = (double[])(refToFrame ?? ViewMatcher.Identity).Clone();
            _keySinceMs = ms;
            _keyIsAnchor = true;
            _nextSceneCheckMs = ms;
            _anchorGray = _keyGray.Clone();
            _anchorPose = (double[])_keyPose.Clone();
            _state = State.Watching;
            _nextMs = ms + _o.CheckMs;
        }

        /// <summary>시크·화면 전환·뷰를 모를 때 — 다시 Start 할 때까지 아무것도 보지 않는다 (평소 수준만 기억한다)</summary>
        public void Stop()
        {
            _state = State.Off;
            _key?.Dispose(); _key = null;
            _keyGray?.Dispose(); _keyGray = null;
            _anchorGray?.Dispose(); _anchorGray = null;
            _anchorSift?.Dispose(); _anchorSift = null;
            _homeGray?.Dispose(); _homeGray = null;
            _homeSift?.Dispose(); _homeSift = null;
            DisposeMoving();
            _history.Clear();
            _standSpreads.Clear();
            _verifyWhenClear = false;
            _reason = "";
            _motionSeen = false;
        }

        /// <summary>확인할 차례면 이 프레임으로 맞춰 본다. overlay = 배운 화면 고정 글자 — '카메라가 그 자리'를 볼 때 글자로 제자리인 짝을 장면으로 세지 않으려고만 쓴다(맞춤에서 빼지는 않는다)</summary>
        public PoseStep Check(Mat frame, double ms, OverlayMask? overlay = null)
        {
            if (!Due(ms)) return default;
            _overlay = overlay;
            ViewSignature probe;
            using (var g = ViewMatcher.PrepareGray(frame)) probe = _m.Compute(g);
            bool taken = false;   // probe 를 기준(_key·_last)으로 넘겼으면 여기서 해제하지 않는다
            try
            {
                return _state == State.Watching ? Watch(frame, probe, ms, ref taken) : Follow(frame, probe, ms, ref taken);
            }
            finally
            {
                if (!taken) probe.Dispose();
            }
        }

        /// <summary>
        /// 화면 전환 감지가 빠른 전환을 알린 프레임에서(지켜보기를 멈추기 전에) 앞 화면(비교 기준 — 움직이는 중이면 움직이기 전 화면)과 이 화면을 ORB 로 맞춘다 —
        /// 장면이 통째로 바뀌었는데 같은 자리에 남은 짝은 화면 고정 글자다(빠른 프리셋 이동에서도 글자를 배우게 — 예전엔 천천히 한 움직임을 잰 때만 배웠다, 2026-11-22 검토).
        /// 앞 화면과 평소 수준만큼 제자리로 맞으면 카메라는 그대로다(Still — 가까이 선 큰 차 · 경광등처럼 내용만 크게 바뀐 것). 글자로 볼지는 부르는 쪽이 정한다. 지켜보지 않으면 null
        /// </summary>
        public CutInfo? Cut(Mat after)
        {
            var before = _state == State.Moving ? _before : _key;
            if (before == null) return null;
            int level = _state == State.Moving ? _beforeBaseline : Baseline();
            using var g = ViewMatcher.PrepareGray(after);
            using var sig = _m.Compute(g);
            var (r, p) = _m.Pairs(before, sig);
            int features = Math.Min(before.Points.Length, sig.Points.Length);
            var all = ViewMatcher.Measure(r, p, before.Width, before.Height, features);
            bool still = level > 0 && Standing(all, level);   // 평소 수준을 모르면(0) 글자만 맞은 '제자리'와 가르지 못하니 서 있다고 하지 않는다
            return new CutInfo(ViewMatcher.Pinned(r, p, _v.PinnedPx), features, sig.Width, sig.Height, still);
        }

        private PoseStep Watch(Mat frame, ViewSignature probe, double ms, ref bool taken)
        {
            _nextMs = ms + _o.CheckMs;
            int baseline = Baseline();
            var (a, rest, pinnedSpread) = _m.CompareSplit(_key!, probe, _v.PinnedPx);
            // 글자 짝을 뺀 나머지(장면)만 카메라처럼 옮겨졌다 — 글자가 전체 맞춤을 끌고 간 느린 팬일 수도(무늬가 몇 줄뿐이고 글자가 또렷한 장면 — 역광 사고 영상 충훈 꼴:
            // 전체 맞춤만 보면 '서 있음'이라 기준을 새로 잡을 때마다 그동안의 팬을 잃어, 셀프 테스트 25 의 0.5%/초 팬을 6% 를 넘고도 13.5% 에서야 잡았다), 서 있는 카메라의
            // 차 행렬·가까운 큰 차나 압축 잡음일 수도 있다(test6 1075초: 전체 575 · 배율 0.999 인데 나머지 37 · 12칸 · 배율 0.831 — 3차 검증). 그래서 ORB 나머지로 자세를 잇지 않고
            // 처음 화면과 SIFT 로 견줘 정한다: 범위 밖이면 움직이는 중으로 따라가 멈춘 뒤 판정하고(지나가는 한 장으로 끝내지 않게), 안이거나 그 자리면 직접 잰 자세로 다시 잡고,
            // 못 재면 2차처럼 전체 맞춤으로 본다.
            // 서 있는 카메라에서도 드물게 생겨(test6 18분에 7번 — 3차 검증 실측) SIFT 비용이 쌓이지 않게 SceneCheckGapMs 에 한 번만
            if (!taken && ms >= _nextSceneCheckMs && Moves(rest, pinnedSpread))
            {
                _nextSceneCheckMs = ms + _o.SceneCheckGapMs;
                var (moved, conclusive) = CheckAnchor(frame, probe, ms, ref taken, "글자를 뺀 장면이 카메라처럼 옮겨져 처음 화면과 견줌", follow: true);
                if (moved is { } m0) return m0;
                if (conclusive) return new PoseStep(PoseOutcome.Still, a.Inliers, baseline);
            }
            if (Standing(a, baseline))
            {
                if (IsStill(a)) RememberSpread(a.SpreadMax);
                // 서 있다. 다만 평소 수준이 화면 고정 글자만 맞은 '제자리'였다면 기준을 새로 잡은 뒤엔 장면까지 맞아 인라이어가 몇 배로 는다 — 지켜보기를 시작하자마자
                // 카메라가 줌·팬하면 첫 확인부터 글자만 맞아 그 움직임을 영영 못 잡았다(test6 1415.7~1417.8초로 시크, 2026-11-22 검토). 그러면 처음 화면과 SIFT 로 견줘 본다
                if (_history.Count >= 2 && a.Inliers > _o.JumpRatio * baseline)
                {
                    _history.Clear();   // 늘어난 쪽이 진짜 평소 수준 — 지금부터 다시 모은다
                    _verifyWhenClear = false;   // 이 확인이 연기가 걷힌 뒤의 확인도 맡는다
                    var (moved, conclusive) = CheckAnchor(frame, probe, ms, ref taken, $"인라이어가 평소 {baseline} → {a.Inliers} 로 늘어 처음 화면과 견줌");
                    if (moved is { } m1) return m1;
                    if (conclusive) Trust();   // 확인했으니 아래 세 번째 확인에서 같은 SIFT 를 또 하지 않는다
                }
                // 연기 같은 내용 변화 끝에 그 자리로 이었는데 장면이 다시 보인다 — 가려진 동안 카메라가 움직였을 수 있으니 처음 화면과 견줘 본다
                else if (_verifyWhenClear && !taken && a.Inliers >= _o.ClearRatio * _clearLevel)
                {
                    _verifyWhenClear = false;
                    var (moved, conclusive) = CheckAnchor(frame, probe, ms, ref taken, $"가렸던 장면이 다시 보여 처음 화면과 견줌 (인라이어 {a.Inliers} — 가리기 전 {_clearLevel})");
                    if (moved is { } m2) return m2;
                    if (conclusive) Trust();
                }
                Remember(a.Inliers);
                // 아래 확인에 쓸 처음 화면의 SIFT 는 한 확인 앞서 만들어 둔다 — 한 프레임에 SIFT 둘(처음 화면 · 지금)을 몰면 170 ms 안팎이었다(3차 실측, 다른 일과 함께 돌림)
                if (!_levelTrusted && !taken && _history.Count == 2) AnchorSift();
                // 평소 수준을 처음 잡았는데(이 뷰에서 확인한 수준이 없었다) 그것이 글자만 맞은 '제자리'일 수 있다 — 지켜보기를 시작하자마자 카메라가 움직였으면 그렇다.
                // 한 번 처음 화면과 SIFT 로 견줘 확인한다(예전엔 10초 뒤 기준을 새로 잡은 다음에야 인라이어가 뛰어 잡혔다 — 그동안 앞 뷰의 구역이 움직인 화면에 남았다)
                if (!_levelTrusted && !taken && _history.Count == 3)
                {
                    var (moved, conclusive) = CheckAnchor(frame, probe, ms, ref taken, "평소 수준을 처음 잡아 처음 화면과 견줌");
                    if (moved is { } m3) return m3;
                    if (conclusive) Trust();
                }
                if (!taken && ms - _keySinceMs >= _o.KeyRefreshMs && (baseline == 0 || a.Inliers >= _o.RefreshMinRatio * baseline))
                {
                    var pose = StandPose(_keyPose, a);
                    var (shift, scale, skew) = Geometry(pose, probe);
                    // 서 있는 맞춤의 작은 어긋남도 쌓이면 허용 범위를 넘는다(아주 느린 팬 — 예전엔 확인 없이 쌓아 이동 13% 까지도 '서 있음'이었다, 2026-11-22 검토) →
                    // 기준을 새로 잡지 말고 움직임으로 보고, 멈춘 뒤 처음 화면과 SIFT 로 견줘 판정한다
                    if (Beyond(shift, scale, skew))
                        return BeginMoving(probe, ms, baseline, false, ref taken, $"쌓인 자세가 허용 범위 밖 (이동 {shift * 100:0.0}% · 배율 {scale:0.000})");
                    Rekey(frame, probe, pose, ms, ref taken);
                }
                return new PoseStep(PoseOutcome.Still, a.Inliers, baseline);
            }
            if (Reliable(a) && !IsStill(a) && OrbCameraLike(a, pinnedSpread))
            {
                // 장면이 화면에 퍼진 채 움직였다
                var pose = ViewMatcher.Chain(_keyPose, a.H!);
                var (shift, scale, skew) = Geometry(pose, probe);
                if (!Beyond(shift, scale, skew))
                {
                    // 허용 범위 안 — 여기서부터 다시 잰다. 추적은 멈추지 않는다(흔들리는 기둥 카메라가 1~3% 씩 움직일 때마다 멈추면 추적이 거의 안 된다).
                    // 이 ORB 맞춤의 어긋남(글자 짝이 섞이면 배율이 3% 넘게 틀렸다 — test6 732.5초)은 쌓일 수 있지만, 움직였다고 판정할 땐 늘 처음 화면(닻)·집과 SIFT 로
                    // 다시 재므로(Follow · CheckAnchor) 그때 바로잡힌다
                    Rekey(frame, probe, pose, ms, ref taken);
                    return new PoseStep(PoseOutcome.Still, a.Inliers, baseline, shift, scale);
                }
                return BeginMoving(probe, ms, baseline, true, ref taken, $"자세가 허용 범위 밖 (ORB — 이동 {shift * 100:0.0}% · 배율 {scale:0.000})");
            }
            // 인라이어는 평소만큼인데 몇 칸에 몰려 움직였다(그 장면이 서 있을 때보다 훨씬 좁게) — 화면 안의 물체(차 무리 · 큰 차)로 보고 자세·기준을 그대로 둔다.
            // 카메라가 정말 움직이는 중이면 기준이 그대로라 어긋남이 커져 곧 맞춤이 무너지고(아래) 멈춘 뒤 SIFT 로 판정된다
            if (Matched(a) && (baseline == 0 || a.Inliers >= _o.CollapseRatio * baseline))
                return new PoseStep(PoseOutcome.Still, a.Inliers, baseline);
            // 맞춤이 무너졌거나 안 된다 — 글자 짝을 뺀 나머지가 화면에 퍼진 채 같이 옮겨졌을 때만 카메라가 움직인 증거로 친다(연기·큰 차는 아니다)
            return BeginMoving(probe, ms, baseline, Moves(rest, pinnedSpread), ref taken,
                Matched(a) ? $"장면 맞춤이 무너짐 (인라이어 {a.Inliers} — 평소 {baseline})" : $"맞춤 실패 (인라이어 {a.Inliers})");
        }

        /// <summary>
        /// 지금 화면을 처음 화면(닻)과 SIFT 로 견준다. 허용 범위 밖으로 재지면 Moved(잰 것). 범위 안이거나 그 자리면 지금 화면을 직접 잰 자세로 비교 기준을 새로 잡는다.
        /// 돌려주는 conclusive = 견줘서 자세를 알았다(못 재면 false — 그대로 둔다)
        /// </summary>
        /// <param name="follow">범위 밖이면 곧바로 Moved 로 끝내지 않고 움직이는 중으로 따라간다(멈춘 뒤 판정) — 카메라가 아직 움직이는 중일 수 있는 확인(글자를 뺀 장면이 옮겨짐)에서.
        /// 곧바로 끝내면 0.3초 만에 돌아오는 8% 팬을 지나가는 한 장으로 재 화면 전환으로 처리했다(3차 셀프 테스트 5)</param>
        private (PoseStep? Step, bool Conclusive) CheckAnchor(Mat frame, ViewSignature probe, double ms, ref bool taken, string why, bool follow = false)
        {
            using var after = Gray(frame, _o.ConfirmWidth);
            using var afterSift = SiftOf(after);
            var m = MeasureMove(AnchorSift(), afterSift);
            if (m.Motion == null && !m.NoMove) return (null, false);
            var pose = m.Motion != null ? ViewMatcher.Chain(_anchorPose, m.Motion) : (double[])_anchorPose.Clone();
            var (shift, scale, skew) = Geometry(pose, probe);
            if (Beyond(shift, scale, skew))
            {
                string reason = $"{why} → 이동 {shift * 100:0.0}% · 배율 {scale:0.000}";
                if (follow && !taken) return (BeginMoving(probe, ms, Baseline(), true, ref taken, $"{reason} — 멈출 때까지 따라감"), true);
                using (var before = Gray(_anchorGray!, ViewMatcher.BaseWidth)) KeepMovedBefore(before);
                return (Moved(true, m.Explained, shift, scale, OverlayPoints(m), Baseline(), reason), true);
            }
            if (!taken) Rekey(frame, probe, pose, ms, ref taken);
            return (null, true);
        }

        private PoseStep BeginMoving(ViewSignature probe, double ms, int baseline, bool motion, ref bool taken, string reason)
        {
            _state = State.Moving;
            _before = _key;           // 움직이기 전 화면 — 멈춘 뒤 이것과 견준다
            _beforeGray = _keyGray;
            _beforePose = _keyPose;
            _beforeBaseline = baseline;
            _beforeIsAnchor = _keyIsAnchor;
            _key = null;
            _keyGray = null;
            _last = probe;
            taken = true;
            _movingSinceMs = ms;
            _nextMs = ms + _o.MovingCheckMs;
            _reason = reason;
            _motionSeen = motion;
            _stepInliers.Clear();
            // 멈춘 뒤 견줄 SIFT 특징점은 지금 만들어 둔다 — 멈춘 프레임에서 앞·처음·뒤 화면의 SIFT 를 한꺼번에 하면 한 프레임이 250 ms 를 넘겨 재생 시계가 다시 맞춰진다
            BeforeSift();
            if (!_beforeIsAnchor) AnchorSift();
            return new PoseStep(PoseOutcome.Moving, 0, baseline, Reason: reason);
        }

        private PoseStep Follow(Mat frame, ViewSignature probe, double ms, ref bool taken)
        {
            _nextMs = ms + _o.MovingCheckMs;
            // 멈췄나: 이웃한 두 화면이 '서 있는' 맞춤인가 — 전체 맞춤의 제자리만 보면 화면 고정 글자 탓에 도는 중에도 멈춘 것으로 보였다(test6 622초 팬)
            var (step, rest, pinnedSpread) = _m.CompareSplit(_last!, probe, _v.PinnedPx);
            // 카메라가 움직이는 증거: 이웃한 두 화면의 장면(또는 글자 짝을 뺀 나머지)이 화면에 퍼진 채 같이 옮겨졌다
            if (Moves(step, pinnedSpread) || Moves(rest, pinnedSpread)) _motionSeen = true;
            if (Matched(step)) _stepInliers.Add(step.Inliers);
            // (나머지가 아직 옮겨지는 중이면 멈춘 게 아니라고 보던 것은 3차에 넣었다 뺐다 — 서 있는 카메라의 차·잡음에서도 나머지가 카메라처럼 맞아 멈춤을 늦췄다)
            bool stopped = Standing(step, _beforeBaseline);
            if (!stopped && ms - _movingSinceMs < _o.MaxMovingMs)
            {
                _last!.Dispose();
                _last = probe;
                taken = true;
                return new PoseStep(PoseOutcome.Moving, step.Inliers, _beforeBaseline, Reason: _reason);
            }

            // 멈췄다(또는 너무 오래 움직였다) — 처음 화면(닻) · 앞서 서 있던 화면(집) · 움직이기 전 화면과 SIFT 로 견줘 판정한다
            using var after = Gray(frame, _o.ConfirmWidth);
            using var afterSift = SiftOf(after);
            var mb = MeasureMove(BeforeSift(), afterSift);
            var ma = _beforeIsAnchor ? mb : MeasureMove(AnchorSift(), afterSift);
            var mh = _homeGray != null ? MeasureMove(HomeSift(), afterSift) : new MoveResult(null, false, [], 0);
            double[]? poseB = mb.Motion != null ? ViewMatcher.Chain(_beforePose, mb.Motion) : mb.NoMove ? (double[])_beforePose.Clone() : null;
            double[]? poseA = ma.Motion != null ? ViewMatcher.Chain(_anchorPose, ma.Motion) : ma.NoMove ? (double[])_anchorPose.Clone() : null;
            double[]? poseH = mh.Motion != null ? ViewMatcher.Chain(_homePose, mh.Motion) : mh.NoMove ? (double[])_homePose.Clone() : null;
            // 자세는 직접 잰 것을 먼저 — 처음 화면(닻), 그다음 앞 움직임 전에 서 있던 화면(집). '그 자리'(글자 마스크 밖 장면이 통째로 제자리)도 직접 잰 것이다.
            // 움직일 때마다 앞 자세에 이어 붙이기만 하면 어긋남이 쌓여, 제자리로 돌아온 카메라를 배율 1.033~1.04 로 보고 그 뒤 범위 안의 줌(1.08~1.09)을 범위 밖으로 재
            // 같은 자리를 새 뷰로 등록했다(test6 734초 — 닻은 그 자리 791쌍 · 33칸이었는데 이어 붙인 쪽을 썼다, 2·3차 검증). 둘 다 못 재면 앞 화면에 이어 붙인다
            var pose = poseA ?? poseH ?? poseB;
            // 새 화면의 평소 수준: 멈춘 맞춤(0.5초 간격이라 1초 간격보다 많이 맞아 0.8 배) — 멈추지 못하고 시간이 다 됐으면 그동안의 가운데값(연기 낀 수준 — 다시 무너진 것으로 보지 않게)
            int seed = stopped ? (int)(0.8 * step.Inliers) : Median(_stepInliers);
            _lastSeed = seed;
            if (pose != null)
            {
                var (shift, scale, skew) = Geometry(pose, probe);
                string how = ReferenceEquals(pose, poseA) ? "처음 화면과" : ReferenceEquals(pose, poseH) ? "앞서 서 있던 화면과" : "움직이기 전 화면과";
                if (!Beyond(shift, scale, skew))
                {
                    if (ReferenceEquals(pose, poseA) || ReferenceEquals(pose, poseH)) Trust();   // 직접 잰 자세 — 새 화면에서 잡을 평소 수준도 믿을 만하다
                    return Resume(frame, probe, pose, ms, ref taken, Math.Max(mb.Explained, ma.Explained), shift, scale, seed,
                                  $"{_reason} → 멈춘 뒤 {how} 견줘 이동 {shift * 100:0.0}% · 배율 {scale:0.000} (허용 범위 안)");
                }
                // 글자 짝은 카메라 움직임을 잰 쪽에서 (그 움직임으로도 제자리인 짝은 거른다) — 움직이기 전 화면부터, 그 움직임이 작아 가르지 못하면 처음 화면 · 집 (PickOverlay)
                var (src, overlay) = PickOverlay(mb, ma, mh);
                using (var before = Gray(_beforeGray!, ViewMatcher.BaseWidth)) KeepMovedBefore(before);
                return Moved(true, src.Explained, shift, scale, overlay, _beforeBaseline,
                             $"{_reason} → 멈춘 뒤 {how} 견줘 이동 {shift * 100:0.0}% · 배율 {scale:0.000}");
            }
            // 움직임을 재지 못했다 — 앞 화면과 다시 평소만큼 맞으면 그 자리(연기·큰 차 같은 내용 변화)
            var back = _m.Compare(_before!, probe);
            if (Standing(back, _beforeBaseline))
            {
                var p = StandPose(_beforePose, back);
                var (shift, scale, skew) = Geometry(p, probe);
                if (!Beyond(shift, scale, skew))
                    return Resume(frame, probe, p, ms, ref taken, back.Inliers, shift, scale, seed, $"{_reason} → 멈춘 뒤 앞 화면과 다시 맞음");
                // 앞 화면과는 맞지만 쌓인 자세가 범위 밖 — 재서 확인하지 못했으니 글자를 배우거나 떠난 뷰를 빼지 않고 다시 식별만 한다
                return Moved(false, back.Inliers, shift, scale, null, _beforeBaseline,
                             $"{_reason} → 앞 화면과 맞지만 쌓인 자세가 허용 범위 밖 (이동 {shift * 100:0.0}% · 배율 {scale:0.000})");
            }
            // 멈추지 못한 채 시간이 다 됐고 카메라가 움직인 증거도 없다 — 연기처럼 내용이 바뀌어 이웃한 화면끼리도 평소만큼 맞지 않은 것. 그 자리로 보고 이어서 지켜본다
            // (예전엔 재지 못한 Moved 로 화면 전환처럼 처리해 선언된 유고를 끝내고 연기 낀 화면을 새 뷰로 등록하기도 했다, 2026-11-22 검토). 가려진 동안 카메라가 움직였을 수 있으니
            // 장면이 다시 보이면 처음 화면과 견줘 본다(_verifyWhenClear). 또렷이 멈췄는데 앞 화면과 안 맞고 움직임도 못 쟀으면 정말 옮겨 갔을 수 있다 — 다시 식별해 본다
            if (!stopped && !_motionSeen)
            {
                int clear = _beforeBaseline;
                var r = Resume(frame, probe, (double[])_beforePose.Clone(), ms, ref taken, back.Inliers, 0, 1, seed,
                               $"{_reason} → 멈추지 않은 채 {_o.MaxMovingMs / 1000:0}초 — 카메라가 움직인 증거가 없어 그 자리로 봄 (인라이어 {back.Inliers} — 내용 변화)");
                // '장면이 다시 보인다'는 가리기 전의 확인된 수준으로 잰다 — 연기가 천천히 짙어지면 움직이기 전 수준(가운데값)이 이미 연기 낀 수준이라 아직 연기 속에서 확인해 버렸다(3차 검증)
                _clearLevel = Math.Max(clear, _peakTrusted);
                _verifyWhenClear = _clearLevel > 0;
                _levelTrusted = false;   // 연기 낀 수준은 확인하지 않았다 — 뷰의 평소 수준으로 기억하지 않게(ViewSession)
                return r;
            }
            return Moved(false, back.Inliers, 0, 1, null, _beforeBaseline,
                         $"{_reason} → 멈춘 뒤에도 앞 화면과 안 맞고 움직임도 못 잼 (인라이어 {back.Inliers} — 평소 {_beforeBaseline})");
        }

        private PoseStep Resume(Mat frame, ViewSignature probe, double[] pose, double ms, ref bool taken, int inliers, double shift, double scale, int seed, string reason)
        {
            int baseline = _beforeBaseline;
            _state = State.Watching;
            KeepHome();
            DisposeMoving();
            Rekey(frame, probe, pose, ms, ref taken);
            _history.Clear();
            // 새 화면의 평소 수준으로 시작한다 — 비워 두면 첫 확인이 아무 맞춤이나 '서 있음'으로 받아, 바로 이어진 움직임에서 글자만 맞은 수준이 평소가 됐다
            if (seed > 0) Remember(seed);
            _nextMs = ms + _o.CheckMs;
            _reason = "";
            _motionSeen = false;
            return new PoseStep(PoseOutcome.Resumed, inliers, baseline, shift, scale, Reason: reason);
        }

        private PoseStep Moved(bool measured, int inliers, double shift, double scale, IReadOnlyList<Point2d>? overlay, int baseline, string reason)
        {
            Stop();
            return new PoseStep(PoseOutcome.Moved, inliers, baseline, shift, scale, measured, overlay, reason);
        }

        private void KeepMovedBefore(Mat gray480)
        {
            _movedBefore?.Dispose();
            _movedBefore = gray480.Clone();
        }

        private void Rekey(Mat frame, ViewSignature probe, double[] pose, double ms, ref bool taken)
        {
            _key?.Dispose();
            _key = probe;
            taken = true;
            _keyGray?.Dispose();
            _keyGray = Gray(frame, _o.ConfirmWidth);
            _keyPose = pose;
            _keySinceMs = ms;
            _keyIsAnchor = false;
        }

        /// <summary>이번 움직임 전에 서 있던 화면(과 그 SIFT·자세)을 집으로 넘긴다 — 다음 움직임에서 돌아왔는지 직접 견줄 수 있게. 그 화면이 닻이면 닻이 이미 맡으니 집은 비운다</summary>
        private void KeepHome()
        {
            _homeGray?.Dispose(); _homeGray = null;
            _homeSift?.Dispose(); _homeSift = null;
            if (_beforeIsAnchor || _beforeGray == null) return;
            _homeGray = _beforeGray; _beforeGray = null;
            _homeSift = _beforeSift; _beforeSift = null;
            _homePose = _beforePose;
        }

        private void DisposeMoving()
        {
            _before?.Dispose(); _before = null;
            _beforeGray?.Dispose(); _beforeGray = null;
            _beforeSift?.Dispose(); _beforeSift = null;
            _last?.Dispose(); _last = null;
            _stepInliers.Clear();
        }

        private void Remember(int inliers)
        {
            _history.Add(inliers);
            if (_history.Count > _o.BaselineChecks) _history.RemoveAt(0);
            if (_history.Count < 3) return;
            _level = Baseline();
            if (_levelTrusted) _peakTrusted = Math.Max(_peakTrusted, _level);
        }

        /// <summary>평소 수준을 처음 화면(닻)·집과 직접 견줘 확인했다</summary>
        private void Trust()
        {
            _levelTrusted = true;
            _peakTrusted = Math.Max(_peakTrusted, _level);
        }

        private void RememberSpread(int spread)
        {
            _standSpreads.Add(spread);
            if (_standSpreads.Count > _o.BaselineChecks) _standSpreads.RemoveAt(0);
        }

        /// <summary>맞춤의 평소 수준 — 앞 확인들의 가운데값. 다시 시작한 직후라 앞 확인이 없으면 넘겨받은 수준(이 뷰에서 처음이면 0 — 무너짐을 재지 못한다)</summary>
        private int Baseline()
        {
            if (_history.Count == 0) return _level;
            var s = _history.OrderBy(x => x).ToArray();
            return s[s.Length / 2];
        }

        private static int Median(List<int> xs)
        {
            if (xs.Count == 0) return 0;
            var s = xs.OrderBy(x => x).ToArray();
            return s[s.Length / 2];
        }

        /// <summary>움직임 앞뒤에서 제자리였던 짝 가운데 화면 고정 글자로 배울 것 — 그 움직임으로도 제자리인 짝(줌의 가운데 근처)은 글자가 아니니 뺀다</summary>
        private List<Point2d> OverlayPoints(in MoveResult m)
        {
            if (m.Motion == null) return [];
            var motion = m.Motion;
            return m.Pinned.Where(p => Displacement(motion, p) > 2 * _v.PinnedPx).ToList();
        }

        /// <summary>
        /// 글자 짝을 고를 견줌을 정한다 — 앞에 준 것부터(움직이기 전 화면이 가장 최근이라 내용이 가장 비슷하다), 카메라 움직임을 쟀고 그 움직임이 제자리 짝의 절반 이상을
        /// '글자'로 가를 수 있는 첫 견줌(<see cref="OverlayPoints"/> — 제자리 짝이 아예 없으면 글자 없는 영상이라 그대로 빈 목록).
        /// 범위 안에서 한 번 옮긴 뒤(비교 기준이 그 자리로 새로 잡힌다) 조금 더 밀려 범위를 넘으면 움직이기 전 화면과의 움직임이 가로 480 에서 3px(제자리 한계의 2배)도 안 돼
        /// 글자(제자리)와 장면을 가를 수 없다. 예전엔 늘 움직이기 전 화면 쪽만 봐서 그런 움직임에선 글자를 하나도 못 배웠고(셀프 테스트 28: 5.9% 팬 뒤 0.57% — 글자 짝 0),
        /// 빈 목록을 '글자 짝이 없다'로 읽은 ViewSession 이 떠난 뷰의 기준 화면에 글자를 가리지 않은 채 '정말 떠났나'를 물었다(2026-11-25 Gemini 리뷰 6-2).
        /// 어느 견줌으로도 가르지 못하면 Overlay = null(모름)
        /// </summary>
        private (MoveResult Src, List<Point2d>? Overlay) PickOverlay(params MoveResult[] moves)
        {
            MoveResult first = default;
            bool any = false;
            foreach (var m in moves)
            {
                if (m.Motion == null) continue;
                if (!any) { first = m; any = true; }
                var pts = OverlayPoints(m);
                if (2 * pts.Count >= m.Pinned.Count) return (m, pts);
            }
            return (first, null);
        }

        /// <summary>
        /// 움직임 앞뒤(가로 ConfirmWidth 흑백의 SIFT)를 맞춰, 제자리 짝을 뺀 나머지로 카메라 움직임을 잰다. 돌려주는 호모그래피와 제자리 짝은 가로 480(ViewMatcher) 좌표.
        /// 나머지의 ConfirmMinExplained 이상을 설명하고 인라이어 30 · 칸 4 를 넘고, 인라이어가 화면에 퍼져야(<see cref="CameraLike"/>) 카메라 움직임으로 믿는다 —
        /// 서 있는 앞뒤의 나머지는 달리는 차라 한 호모그래피로 설명돼도 몇 칸에 몰린다. 카메라 움직임이 아니고 글자 마스크 밖의 제자리 짝이 StillSpread 칸 이상에 퍼졌으면 '그 자리'
        /// (마스크 안의 글자는 카메라가 움직여도 제자리라 세지 않는다 — 글자 띠가 넓은 영상에서 움직인 카메라를 '그 자리'로 보지 않게)
        /// </summary>
        private MoveResult MeasureMove(SiftSet before, SiftSet after)
        {
            var pinned = new List<Point2d>();
            if (before.Descriptors.Empty() || after.Descriptors.Empty() || before.Points.Length < 8 || after.Points.Length < 8) return new MoveResult(null, false, pinned, 0);
            _l2 ??= new BFMatcher(NormTypes.L2, crossCheck: false);
            double f = (double)ViewMatcher.BaseWidth / before.Width;   // ConfirmWidth → 480 좌표
            double pinLim = _v.PinnedPx / f;                            // 제자리 판정도 같은 화면 비율로
            var src = new List<Point2d>();
            var dst = new List<Point2d>();
            foreach (var m in _l2.KnnMatch(after.Descriptors, before.Descriptors, 2))
            {
                if (m.Length < 2 || m[0].Distance >= 0.8f * m[1].Distance) continue;
                var a = before.Points[m[0].TrainIdx].Pt;
                var b = after.Points[m[0].QueryIdx].Pt;
                double dx = b.X - a.X, dy = b.Y - a.Y;
                if (dx * dx + dy * dy <= pinLim * pinLim) { pinned.Add(new Point2d(a.X * f, a.Y * f)); continue; }
                src.Add(new Point2d(a.X * f, a.Y * f));
                dst.Add(new Point2d(b.X * f, b.Y * f));
            }
            int w = ViewMatcher.BaseWidth, h = (int)Math.Round(before.Height * f);
            var mov = ViewMatcher.Measure(src, dst, w, h, Math.Min(before.Points.Length, after.Points.Length));
            int pinnedSpread = ViewMatcher.Spread(pinned, w, h);
            bool camera = Reliable(mov) && mov.Inliers >= _o.ConfirmMinExplained * src.Count && CameraLike(mov, pinnedSpread);
            var overlay = _overlay;
            int sceneSpread = overlay is { IsEmpty: false } ? ViewMatcher.Spread(pinned.Where(p => !overlay.Covers(new Point2f((float)p.X, (float)p.Y))), w, h) : pinnedSpread;
            bool noMove = !camera && sceneSpread >= _o.StillSpread;
            return new MoveResult(camera ? mov.H : null, noMove, pinned, mov.Inliers);
        }

        private SiftSet SiftOf(Mat gray)
        {
            _sift ??= OpenCvSharp.Features2D.SIFT.Create(_o.ConfirmFeatures);
            var desc = new Mat();
            try
            {
                _sift.DetectAndCompute(gray, null, out KeyPoint[] kps, desc);
                return new SiftSet(kps, desc, gray.Width, gray.Height);
            }
            catch
            {
                desc.Dispose();
                throw;
            }
        }

        private SiftSet AnchorSift() => _anchorSift ??= SiftOf(_anchorGray!);

        private SiftSet HomeSift() => _homeSift ??= SiftOf(_homeGray!);

        /// <summary>움직이기 전 화면의 SIFT — 그 화면이 닻이면 닻의 것(같은 화면을 두 번 재지 않는다)</summary>
        private SiftSet BeforeSift() => _beforeIsAnchor ? AnchorSift() : (_beforeSift ??= SiftOf(_beforeGray!));

        /// <summary>
        /// 카메라가 서 있는 맞춤 — 인라이어가 평소(baseline)의 CollapseRatio 이상이고, 제자리이거나 맞춤이 몇 칸(4×3 의 MinCells 미만)에만 몰렸다.
        /// 카메라가 움직이면 화면 전체가 같이 움직이니 몇 칸에 몰린 '움직임'은 화면 안의 물체다(합성 화면 녹화 webm 의 글자·도형이 헛경보 7건을 냈다, 2026-11-21).
        /// 연기 낀 화면은 서 있어도 맞춤이 몇 칸에 몰리므로 칸 수로 '서 있음'을 거르지는 않는다. 글자만 맞은 '제자리'는 인라이어가 평소보다 크게 적어 걸러진다
        /// </summary>
        private bool Standing(ViewMatch a, int baseline) =>
            Matched(a) && (IsStill(a) || a.Cells < _v.MinCells) && (baseline == 0 || a.Inliers >= _o.CollapseRatio * baseline);

        /// <summary>서 있는 맞춤 다음의 자세 — 제자리 맞춤이면 그 작은 어긋남까지 이어 붙이고, 몇 칸에 몰린 맞춤(화면 안 물체의 움직임)이면 자세를 그대로 둔다
        /// (물체의 움직임을 카메라 자세로 쌓지 않게)</summary>
        private double[] StandPose(double[] pose, ViewMatch a) => IsStill(a) ? ViewMatcher.Chain(pose, a.H!) : (double[])pose.Clone();

        /// <summary>SIFT 앞뒤 견주기가 카메라 움직임 꼴 — 인라이어가 앞뒤 화면 중 큰 쪽으로 8×6 의 MinMotionSpread 칸 이상, 제자리 짝이 퍼진 칸의 MotionSpreadRatio 배 이상
        /// (차·사람의 움직임은 몇 칸에 몰리고 그동안 서 있는 장면이 화면 전체에서 제자리다)</summary>
        private bool CameraLike(ViewMatch m, int pinnedSpread) => m.SpreadMax >= _o.MinMotionSpread && m.SpreadMax >= _o.MotionSpreadRatio * pinnedSpread;

        /// <summary>ORB 맞춤이 카메라 움직임 꼴 — 퍼짐 기준을 그 장면이 서 있을 때의 퍼짐에 맞춘다(<see cref="PoseWatchOptions.OrbSpreadRatio"/>). 장면을 아직 모르면 SIFT 와 같은 고정 기준</summary>
        private bool OrbCameraLike(ViewMatch m, int pinnedSpread) => m.SpreadMax >= OrbMinSpread() && m.SpreadMax >= _o.MotionSpreadRatio * pinnedSpread;

        private int OrbMinSpread()
        {
            if (_standSpreads.Count == 0) return _o.MinMotionSpread;
            int typical = Median(_standSpreads);
            return Math.Min(_o.MinMotionSpread, Math.Max(_o.OrbMinSpreadFloor, (int)Math.Ceiling(_o.OrbSpreadRatio * typical)));
        }

        /// <summary>카메라가 움직인 증거가 되는 (ORB) 맞춤 — 믿을 만하고 제자리가 아니며 화면에 퍼진 카메라 움직임 꼴</summary>
        private bool Moves(ViewMatch m, int pinnedSpread) => Reliable(m) && !IsStill(m) && OrbCameraLike(m, pinnedSpread);

        /// <summary>맞춤이 됐다 (호모그래피 + 인라이어 30 + 원근 정상)</summary>
        private bool Matched(ViewMatch m) => m.H != null && m.Inliers >= _v.MinInliers && m.Skew <= _v.MaxSkew;

        /// <summary>움직임으로 믿을 만한 맞춤 — 칸도 4개 이상 (한 구석의 지나가는 차 무리가 카메라 움직임으로 보이지 않게)</summary>
        private bool Reliable(ViewMatch m) => Matched(m) && m.Cells >= _v.MinCells;

        private bool IsStill(ViewMatch m) => m.Shift <= _o.StillShift && Math.Abs(m.Scale - 1) <= _o.StillScale;

        private bool Beyond(double shift, double scale, double skew) =>
            double.IsNaN(shift) || shift > _v.MaxShift || scale < _v.MinScale || scale > _v.MaxScale || skew > _v.MaxSkew;

        private static (double Shift, double Scale, double Skew) Geometry(double[] pose, ViewSignature probe) => ViewMatcher.Geometry(pose, probe.Width, probe.Height);

        private static Mat Gray(Mat frame, int width)
        {
            var gray = new Mat();
            if (frame.Channels() == 3) Cv2.CvtColor(frame, gray, ColorConversionCodes.BGR2GRAY);
            else if (frame.Channels() == 4) Cv2.CvtColor(frame, gray, ColorConversionCodes.BGRA2GRAY);
            else frame.CopyTo(gray);
            if (gray.Width != width)
            {
                var small = new Mat();
                Cv2.Resize(gray, small, new Size(width, Math.Max(1, (int)Math.Round(gray.Height * (double)width / gray.Width))), 0, 0, InterpolationFlags.Area);
                gray.Dispose();
                gray = small;
            }
            return gray;
        }

        private static double Displacement(double[] h, Point2d p)
        {
            double w = h[6] * p.X + h[7] * p.Y + h[8];
            if (Math.Abs(w) < 1e-9) return double.PositiveInfinity;
            double x = (h[0] * p.X + h[1] * p.Y + h[2]) / w, y = (h[3] * p.X + h[4] * p.Y + h[5]) / w;
            return Math.Sqrt((x - p.X) * (x - p.X) + (y - p.Y) * (y - p.Y));
        }

        public void Dispose()
        {
            Stop();
            _movedBefore?.Dispose();
            _movedBefore = null;
            _m.Dispose();
            _sift?.Dispose();
            _l2?.Dispose();
        }
    }
}
