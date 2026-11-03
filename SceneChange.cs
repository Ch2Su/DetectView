using System;

namespace YOLO11_Test
{
    /// <summary>화면 전환 감지 파라미터 — 값은 experiments/TrackerCheck 의 <c>scene</c> 실측으로 골랐다 (CLAUDE.md '화면 전환 감지')</summary>
    internal sealed record SceneChangeOptions
    {
        /// <summary>연속한 두 프레임의 평균 절대차가 이 이상이면 '움직임'(카메라가 돈다·장면이 바뀐다) — 지나가는 차의 한두 프레임 튐은 이보다 작다</summary>
        public double MoveDiff { get; init; } = 12;
        /// <summary>이 미만이면 '잠잠' (평소 흔들림은 중앙 0.5 이하, 99% 3 이하)</summary>
        public double CalmDiff { get; init; } = 6;
        /// <summary>움직임이 끝난 뒤 잠잠함이 이만큼(ms) 이어지고 <see cref="SettleFrames"/> 장 이상이면 안정된 것으로 본다</summary>
        public double SettleMs { get; init; } = 150;
        public int SettleFrames { get; init; } = 3;
        /// <summary>안정된 직후 화면과 직전 안정 화면(기준)의 평균 절대차가 이 이상이면 다른 화면(전환)이다. 같은 화면으로 돌아오는 검은 끊김·지나가는 큰 차는 이보다 훨씬 작다</summary>
        public double ViewDiff { get; init; } = 15;
        /// <summary>모든 픽셀이 이 값 이하면 빈(검은) 프레임 — 스트림 끊김. 기준·움직임 계산에서 뺀다</summary>
        public int BlankMax { get; init; } = 24;
        /// <summary>안정되지 못한 채 이만큼(ms) 이어지면 포기하고 전환으로 친다 (계속 흔들리는 화면에서 상태가 영영 안 풀리지 않게)</summary>
        public double MaxUnsettledMs { get; init; } = 5000;
    }

    /// <summary>한 프레임을 넣은 결과</summary>
    /// <param name="Unsettled">이 프레임은 움직이는(또는 막 끝난) 중이다 — 추적을 믿을 수 없으니 추적기에 넣지 않는다</param>
    /// <param name="ViewChanged">이 프레임에서 안정을 되찾았고 직전 안정 화면과 다르다 (추적기를 비울 때). 안정되지 못해 포기한 경우도 true</param>
    /// <param name="Settled">움직임 구간이 이 프레임에서 끝났다 (전환이 아니어도 true — <see cref="AnchorDistance"/> 를 볼 수 있다)</param>
    /// <param name="AnchorDistance">Settled 일 때 직전 안정 화면과의 평균 절대차 (기준이 없으면 0)</param>
    /// <param name="HotFrames">Settled 일 때 그 구간에서 MoveDiff 이상이었던 프레임 수</param>
    /// <param name="EpisodeStartMs">Settled 일 때 움직임 구간이 시작된 시각</param>
    /// <param name="Blanked">움직임 구간에 빈(검은) 프레임이 있었다 — 카메라가 돈 게 아니라 스트림이 끊긴 것 (화면에 다르게 알린다)</param>
    internal readonly record struct SceneStep(bool Unsettled, bool ViewChanged, bool Settled, double AnchorDistance, int HotFrames, double EpisodeStartMs, bool Blanked = false);

    /// <summary>
    /// 화면 전환 감지 (유고 감지 2단계의 하나, 2026-11-03) — 순수 알고리즘(OpenCV·WPF 없음). 프레임을 줄인 흑백 그림(SceneThumbnail, 64×36)을 한 장씩 받아
    /// 카메라가 돌아가거나(PTZ·사람 조작) 화면이 다른 장면으로 바뀌었는지 본다. 바뀌면 추적기를 비워야 한다 — 비우지 않으면 돌아가는 카메라가 박스를 끌고 가
    /// 트랙이 화면 전환을 넘어 이어졌다 (test4 에서 56개).
    /// <para>
    /// 판정은 '움직임 → 안정 → 안정된 화면이 직전 안정 화면과 다른가'다. 움직임이 있었다는 것만으론 전환이 아니다:
    /// 지나가는 큰 차나 검은 끊김(test5, 같은 화면으로 돌아옴)도 한두 프레임 크게 튀지만 안정된 뒤의 화면은 그대로다.
    /// 반대로 움직임 없이 한 프레임에 장면이 통째로 바뀌는 컷도 같은 규칙으로 잡힌다.
    /// 움직이는 동안(<see cref="SceneStep.Unsettled"/>)은 추적기에 넣지 않는다 — 돌아가는 카메라의 박스로 가짜 트랙을 만들지 않게.
    /// 기준(직전 안정 화면)은 움직임이 없는 프레임마다 따라가므로 해 뜨는 것·자동 노출 같은 느린 변화는 전환으로 치지 않는다
    /// </para>
    /// 한 스레드(재생 루프)에서만 쓴다. 시크·정지 뒤 이어서 쓰면 안 되는 자리에선 <see cref="Reset"/>
    /// </summary>
    internal sealed class SceneChangeDetector
    {
        private readonly SceneChangeOptions _o;
        private byte[]? _prev;        // 바로 앞 프레임 (빈 프레임 제외)
        private byte[]? _anchor;      // 직전 안정 화면
        private bool _prevBlank;      // 바로 앞 프레임이 빈 프레임이었다 — 이 프레임의 변화량은 비교할 수 없다
        private bool _unsettled;
        private double _episodeStartMs;
        private int _hotFrames;
        private int _calmFrames;
        private bool _episodeBlank;   // 이번 움직임 구간에 빈 프레임이 있었다
        private double _calmSinceMs;

        public SceneChangeDetector(SceneChangeOptions? options = null) => _o = options ?? new SceneChangeOptions();

        /// <summary>지금 움직이는 중 (마지막 Update 의 Unsettled)</summary>
        public bool Unsettled => _unsettled;

        /// <summary>
        /// 시크·재개처럼 앞뒤 화면이 이어지지 않는 곳에서 — 기준과 움직임 상태를 버리고 다음 프레임부터 새로 시작한다
        /// (이어서 쓰면 시크 앞뒤 화면의 차이를 전환으로 오인한다. 시크는 추적기도 비운다)
        /// </summary>
        public void Reset()
        {
            _prev = _anchor = null;
            _prevBlank = false;
            _unsettled = false;
            _hotFrames = _calmFrames = 0;
        }

        public SceneStep Update(ReadOnlySpan<byte> thumb, double ms)
        {
            bool blank = IsBlank(thumb);
            if (blank)
            {
                // 검은 끊김: 기준을 그대로 두고 안정될 때까지 움직이는 중으로 본다 (끊김 뒤 같은 화면이면 전환이 아니다)
                if (!_unsettled) Begin(ms);
                _hotFrames++;   // 끊김 자체도 움직임 구간의 한 프레임
                _calmFrames = 0;
                _episodeBlank = true;
                _prevBlank = true;
                if (ms - _episodeStartMs >= _o.MaxUnsettledMs)
                {
                    // 검은 화면이 계속된다 — 포기하고 전환으로 쳐 추적기를 비운다. 빈 프레임은 기준이 못 되니 기준도 버린다
                    int hotFrames = _hotFrames;
                    double start = _episodeStartMs;
                    _unsettled = false;
                    _hotFrames = 0;
                    _anchor = null;
                    return new SceneStep(false, true, true, 0, hotFrames, start, true);
                }
                return Pending(true);
            }

            double d = 0;
            bool comparable = _prev != null && !_prevBlank;
            if (comparable) d = MeanAbsDiff(_prev!, thumb);
            bool hot = comparable && d >= _o.MoveDiff;
            bool calm = comparable && d < _o.CalmDiff;
            // 끊김 직후 첫 프레임은 앞 프레임(빈 것)과 비교할 수 없다 — 움직임도 잠잠함도 아닌 것으로 두고 다음 프레임부터 본다
            bool resume = _prev != null && _prevBlank;

            Store(ref _prev, thumb);
            _prevBlank = false;

            if (!_unsettled)
            {
                if (hot)
                {
                    Begin(ms);
                    _hotFrames = 1;
                    return Pending(false);
                }
                Store(ref _anchor, thumb);   // 움직임이 없으면 기준이 현재 화면을 따라간다 (느린 변화는 전환이 아니다)
                return default;
            }

            // 움직이는 중
            if (hot) { _hotFrames++; _calmFrames = 0; }
            else if (calm)
            {
                if (_calmFrames == 0) _calmSinceMs = ms;
                _calmFrames++;
            }
            else if (!resume) _calmFrames = 0;

            if (_calmFrames >= _o.SettleFrames && ms - _calmSinceMs >= _o.SettleMs)
            {
                double dist = _anchor == null ? 0 : MeanAbsDiff(_anchor, thumb);
                int hotFrames = _hotFrames;
                double start = _episodeStartMs;
                bool blanked = _episodeBlank;
                _unsettled = false;
                _hotFrames = _calmFrames = 0;
                bool changed = _anchor != null && dist >= _o.ViewDiff;
                Store(ref _anchor, thumb);
                return new SceneStep(false, changed, true, dist, hotFrames, start, blanked);
            }

            if (ms - _episodeStartMs >= _o.MaxUnsettledMs)
            {
                // 계속 흔들려 안정되지 못한다 — 포기하고 전환으로 쳐 추적기를 비운다. 다음 움직임부터 다시 센다
                int hotFrames = _hotFrames;
                double start = _episodeStartMs;
                bool blanked = _episodeBlank;
                _unsettled = false;
                _hotFrames = _calmFrames = 0;
                Store(ref _anchor, thumb);
                return new SceneStep(false, true, true, 0, hotFrames, start, blanked);
            }
            return Pending(_episodeBlank);
        }

        private void Begin(double ms)
        {
            _unsettled = true;
            _episodeStartMs = ms;
            _hotFrames = 0;
            _calmFrames = 0;
            _episodeBlank = false;
        }

        private static SceneStep Pending(bool blanked) => new(true, false, false, 0, 0, 0, blanked);

        private bool IsBlank(ReadOnlySpan<byte> t)
        {
            foreach (byte b in t)
                if (b > _o.BlankMax) return false;
            return true;
        }

        public static double MeanAbsDiff(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
        {
            int n = Math.Min(a.Length, b.Length);
            long sum = 0;
            for (int i = 0; i < n; i++) sum += Math.Abs(a[i] - b[i]);
            return n == 0 ? 0 : (double)sum / n;
        }

        private static void Store(ref byte[]? dst, ReadOnlySpan<byte> src)
        {
            if (dst == null || dst.Length != src.Length) dst = new byte[src.Length];
            src.CopyTo(dst);
        }
    }
}
