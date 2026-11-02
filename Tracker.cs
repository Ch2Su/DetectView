using System;
using System.Collections.Generic;

namespace YOLO11_Test
{
    /// <summary>트랙 ID 가 붙은 검출 1건. Stationary: 그 트랙이 이 프레임까지 반영해 멈춘 물체로 판정됐는지 (<see cref="Track.IsStationary"/>).</summary>
    internal readonly record struct TrackedDet(int TrackId, Det Det)
    {
        // 위치 인자가 아니라 init 속성 — 두 인자로 풀어 쓰는 쪽(검증 도구)을 그대로 두려고
        public bool Stationary { get; init; }
    }

    /// <summary>프레임 테두리에 닿은 박스 변 (<see cref="Tracker.BorderEdges"/>).</summary>
    [Flags]
    internal enum BoxEdges { None = 0, Left = 1, Top = 2, Right = 4, Bottom = 8 }

    /// <summary>
    /// 프레임마다 들어오는 검출에 트랙 ID 를 붙인다 (UI·모델과 무관한 순수 알고리즘, 스레드 하나에서만 쓴다).
    /// ROI 진입·이벤트 로그·유고 감지가 모두 "같은 물체"를 알아야 해서 그 바탕이 된다.
    ///
    /// 시간은 프레임 수가 아니라 프레임의 시각(ms)으로 센다. VFR 파일은 프레임 간격이 들쭉날쭉하고, HUD 는 화면이
    /// 바뀔 때만 프레임이 와서 "N 프레임 공백"이 몇 초인지 정해지지 않는다 — YoloDotNet 의 SortTracker 를 쓰지 않은
    /// 이유다(maxAge·tail 이 프레임 수 기준이고 칼만이 중심점만 본다).
    ///
    /// 매칭은 탐욕적 IoU 로 충분하다(30fps 에서 정지·이동 차량 모두 끊김 없음). 대신 세 가지를 더한다:
    /// <list type="bullet">
    /// <item>차량 클래스는 한 묶음으로 본다 — 같은 차가 프레임마다 truck↔car 로 흔들린다.</item>
    /// <item>한 프레임 안의 중복 박스는 한 물체로 본다 — 모델이 같은 차를 같은 자리에 두 번(car 둘, car+truck) 내는 일이 잦고
    ///   (test4 916 프레임 중 466 프레임), 범위만 다른 박스(짐칸 포함·제외, 뒤차 일부 포함)도 낸다.
    ///   그대로 두면 중복 박스가 따로 트랙을 만들어 멈춘 차 한 대가 "줄지어 선 두 대"로 보이고,
    ///   그 트랙이 나중에 원래 트랙의 검출을 가로채기도 한다(test4 덤프트럭이 끊긴 원인).
    ///   판정은 앱의 화면 표시와 같은 <see cref="DuplicateBoxes"/> 규칙이다 (앱 검출은 이미 걸러져 오지만, 직접 넣는 검출도 받으려고 둔다).</item>
    /// <item>멈춘 물체는 공백을 길게 허용해 같은 자리에 다시 나오면 같은 트랙으로 잇는다. 가림·신뢰도 0.3 미만 누락·
    ///   스트림 끊김으로 수 초씩 안 보이는데, 0.5초만 허용하면 한 대가 트랙 2~3개로 끊겼다.
    ///   움직이는 물체에 같은 공백을 주면 뒤따라 같은 길을 지나는 차가 앞 차의 ID 를 물려받으므로 짧게 둔다.</item>
    /// </list>
    /// 시각이 뒤로 가면(시크) 저절로 비운다. 화면 전환(PTZ 회전)·앞으로 시크는 부르는 쪽이 <see cref="Reset"/> 한다.
    /// 프레임 크기를 아는 쪽은 <see cref="Update(IReadOnlyList{Det}, double, int, int)"/> 를 쓴다 — 크기를 모르면 화면 가장자리에 걸쳐
    /// 들어오거나 나가는 물체를 멈춘 것으로 볼 수 있다(<see cref="Track.IsStationary"/>).
    /// </summary>
    internal sealed class Tracker
    {
        /// <summary>최근에 보인 트랙과 짝지을 최소 IoU (움직이는 트랙은 예측 위치 기준).</summary>
        public double IouThreshold { get; init; } = 0.3;

        /// <summary>중복 판정 IoS (<see cref="DuplicateBoxes.DefaultIos"/>). 검증 도구가 스윕하려고 바꿀 수 있게 둔다.</summary>
        public double DuplicateIos { get; init; } = DuplicateBoxes.DefaultIos;

        /// <summary>중복 판정 최소 넓이 비 (<see cref="DuplicateBoxes.DefaultMinAreaRatio"/>).</summary>
        public double DuplicateMinAreaRatio { get; init; } = DuplicateBoxes.DefaultMinAreaRatio;

        /// <summary>움직이는(또는 아직 판정 전인) 트랙을 안 보여도 남겨 두는 시간.</summary>
        public double MovingGapMs { get; init; } = 1000;

        /// <summary>멈춘 트랙을 안 보여도 남겨 두는 시간.</summary>
        public double StationaryGapMs { get; init; } = 10000;

        /// <summary>
        /// <see cref="MovingGapMs"/> 보다 오래 안 보인 멈춘 트랙을 다시 이을 최소 IoU.
        /// "같은 자리"여야 하므로 높게 둔다 — 옆을 스쳐 가는 차의 박스는 대개 이보다 덜 겹친다.
        /// </summary>
        public double RelinkIouThreshold { get; init; } = 0.5;

        /// <summary>멈춤 판정 구간 — 이 시간 동안의 이동량을 본다.</summary>
        public double StationaryWindowMs { get; init; } = 1000;

        /// <summary>
        /// 멈춤 판정에서 <see cref="StationaryWindowMs"/> 동안 허용하는 이동량 (박스 크기 √(W·H) 대비).
        /// 관측이 그보다 짧으면 비례해서 줄인다 — 화면 전환 직후 생긴 트랙도 금방 판정돼야 짧게 가려졌다 나와도 이어진다.
        /// </summary>
        public double StationaryMaxShift { get; init; } = 0.2;

        /// <summary>멈춤 판정에 필요한 최소 관측 시간. 이보다 짧으면 박스 떨림과 움직임을 구분할 수 없다.</summary>
        public double StationaryMinSpanMs { get; init; } = 200;

        /// <summary>
        /// 프레임 테두리에서 이만큼(px) 안에 있는 박스 변은 테두리에 닿은 것으로 보고 멈춤 판정에서 뺀다 (프레임 크기를 줄 때만).
        /// SAHI 의 타일 경계 여유(<see cref="Sahi.TouchesInnerEdge"/>)와 같은 2px. 실측 박스는 테두리에 걸친 변이 프레임 밖으로
        /// 몇 px 나가기도 하고(test4 x=-2) 테두리 1~2px 안에서 끝나기도 한다(test5 아래 테두리 — 대부분 1~2px 안)
        /// </summary>
        public double BorderMargin { get; init; } = 2;

        private readonly List<Track> _tracks = [];
        private int _nextId = 1;
        private double _lastMs = double.NegativeInfinity;

        /// <summary>살아 있는 트랙 (이번 프레임에 안 보였어도 공백 허용 시간 안이면 포함).</summary>
        public IReadOnlyList<Track> Tracks => _tracks;

        /// <summary>매칭할 때 쓰는 클래스 묶음 — 중복 판정과 같은 묶음이다 (<see cref="DuplicateBoxes.GroupOf"/>).</summary>
        public static string GroupOf(string label) => DuplicateBoxes.GroupOf(label);

        /// <summary>
        /// 한 프레임의 검출을 넣고, 같은 순서로 트랙 ID 를 붙여 돌려준다. 중복 박스는 같은 ID 를 받는다.
        /// <paramref name="timeMs"/> 는 그 프레임의 시각 — 파일은 영상 시각(PosMsec), 카메라·HUD 는 벽시계.
        /// 프레임 크기를 모르는 판이라 테두리 처리를 하지 않는다(예전 판정 그대로) — 아는 쪽은 크기를 받는 판을 쓴다.
        /// </summary>
        public List<TrackedDet> Update(IReadOnlyList<Det> dets, double timeMs) => UpdateCore(dets, timeMs, null);

        /// <summary>
        /// <see cref="Update(IReadOnlyList{Det}, double)"/> 에 프레임 크기(px)를 더한 판. 테두리에 닿은 박스 변(<see cref="BorderMargin"/>)을
        /// 멈춤 판정에서 뺀다 — 화면에 들어오거나 나가는 물체는 보이는 부분까지만 박스가 잡혀 테두리 쪽 변이 테두리에 머물러서,
        /// 움직여도 '덜 움직인 변'이 0 이 되어 멈춘 것으로 보였다(이슈 #10).
        /// 닿았는지는 관측마다 그때 받은 크기로 정해 둔다 — 비교하는 약 1초 전 박스도 그때의 크기로 본다.
        /// </summary>
        public List<TrackedDet> Update(IReadOnlyList<Det> dets, double timeMs, int frameWidth, int frameHeight)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frameWidth);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frameHeight);
            return UpdateCore(dets, timeMs, (frameWidth, frameHeight));
        }

        /// <summary>
        /// 박스가 프레임(<paramref name="frameWidth"/>×<paramref name="frameHeight"/>) 테두리에서 <paramref name="margin"/>(px) 안에 있는 변.
        /// 프레임 밖으로 나간 변도 닿은 것이다 (박스는 프레임으로 잘려 나오지 않는다 — test4 오른쪽 끝 1305)
        /// </summary>
        internal static BoxEdges BorderEdges(Det d, int frameWidth, int frameHeight, double margin)
        {
            var e = BoxEdges.None;
            if (d.X <= margin) e |= BoxEdges.Left;
            if (d.Y <= margin) e |= BoxEdges.Top;
            if (d.X + d.W >= frameWidth - margin) e |= BoxEdges.Right;
            if (d.Y + d.H >= frameHeight - margin) e |= BoxEdges.Bottom;
            return e;
        }

        private List<TrackedDet> UpdateCore(IReadOnlyList<Det> dets, double timeMs, (int W, int H)? frame)
        {
            if (timeMs < _lastMs) Reset();   // 뒤로 시크: 안 보인 시간이 음수가 되어 옛 트랙이 안 지워지고 엉뚱한 검출과 이어진다
            _lastMs = timeMs;

            _tracks.RemoveAll(t => timeMs - t.LastMs > (t.IsStationary ? StationaryGapMs : MovingGapMs));

            var dupOf = DuplicateBoxes.FindDuplicates(dets, DuplicateIos, DuplicateMinAreaRatio);
            var owner = new Track?[dets.Count];
            var taken = new HashSet<Track>();
            // 1차: 최근에 보인 트랙. 2차: 오래 안 보인 멈춘 트랙 — 1차를 먼저 해야 가려진 정지 차 자리를 지나가는 차가
            // 자기 트랙 대신 정지 차의 트랙을 가져가지 않는다
            MatchGreedy(dets, dupOf, timeMs, owner, taken, relink: false);
            MatchGreedy(dets, dupOf, timeMs, owner, taken, relink: true);

            for (int i = 0; i < dets.Count; i++)
            {
                if (dupOf[i] >= 0) continue;
                var border = frame is { } f ? BorderEdges(dets[i], f.W, f.H, BorderMargin) : BoxEdges.None;
                if (owner[i] is { } t)
                {
                    t.Observe(dets[i], timeMs, border, this);
                }
                else
                {
                    owner[i] = new Track(_nextId++, dets[i], timeMs, border);
                    _tracks.Add(owner[i]!);
                }
            }

            var result = new List<TrackedDet>(dets.Count);
            for (int i = 0; i < dets.Count; i++)
            {
                var t = owner[dupOf[i] >= 0 ? dupOf[i] : i]!;
                result.Add(new TrackedDet(t.Id, dets[i]) { Stationary = t.IsStationary });
            }
            return result;
        }

        /// <summary>
        /// <see cref="Update(IReadOnlyList{Det}, double, int, int)"/> 와 같되, 번호와 정지 여부를 <see cref="Det"/> 에 붙여 돌려준다 —
        /// 화면 표시(BoxLayer)가 박스와 함께 한 목록으로 받는다. 입력과 같은 순서.
        /// </summary>
        public List<Det> UpdateTagged(IReadOnlyList<Det> dets, double timeMs, int frameWidth, int frameHeight)
        {
            var tracked = Update(dets, timeMs, frameWidth, frameHeight);
            var list = new List<Det>(tracked.Count);
            foreach (var t in tracked)
                list.Add(t.Det with { TrackId = t.TrackId, Stationary = t.Stationary });
            return list;
        }

        /// <summary>
        /// 트랙을 모두 비운다 (화면 전환·시크 뒤). ID 는 이어서 센다 — 이벤트 로그가 ID 로 물체를 가리키므로
        /// 전환 앞뒤의 다른 물체가 같은 번호를 받으면 안 된다.
        /// </summary>
        public void Reset()
        {
            _tracks.Clear();
            _lastMs = double.NegativeInfinity;
        }

        /// <summary>한 프레임 안의 두 박스가 한 물체의 중복 출력인지 (이 추적기의 파라미터로 <see cref="DuplicateBoxes.IsDuplicate"/>).</summary>
        internal bool IsDuplicate(Det a, Det b) => DuplicateBoxes.IsDuplicate(a, b, DuplicateIos, DuplicateMinAreaRatio);

        private void MatchGreedy(IReadOnlyList<Det> dets, int[] dupOf, double timeMs, Track?[] owner, HashSet<Track> taken, bool relink)
        {
            var pairs = new List<(double Iou, Track Track, int Det)>();
            foreach (var t in _tracks)
            {
                if (taken.Contains(t)) continue;
                bool longLost = timeMs - t.LastMs > MovingGapMs;   // 남아 있다면 멈춘 트랙뿐이다
                if (longLost != relink) continue;
                double threshold = relink ? RelinkIouThreshold : IouThreshold;
                var predicted = t.Predict(timeMs, MovingGapMs);
                for (int i = 0; i < dets.Count; i++)
                {
                    if (owner[i] != null || dupOf[i] >= 0 || GroupOf(dets[i].Label) != t.Group) continue;
                    double iou = IoU(predicted, dets[i]);
                    if (iou >= threshold) pairs.Add((iou, t, i));
                }
            }
            pairs.Sort((a, b) => b.Iou.CompareTo(a.Iou));
            foreach (var (_, t, i) in pairs)
            {
                if (owner[i] != null || taken.Contains(t)) continue;
                owner[i] = t;
                taken.Add(t);
            }
        }

        internal static double IoU(Det a, Det b)
        {
            double inter = DuplicateBoxes.Intersection(a, b);
            if (inter <= 0) return 0;
            double uni = a.W * a.H + b.W * b.H - inter;
            return uni > 0 ? inter / uni : 0;
        }
    }

    /// <summary>추적 중인 물체 1개.</summary>
    internal sealed class Track
    {
        // 멈춤 판정·속도 추정용 최근 관측. 판정 구간보다 오래된 것은 하나만 남긴다 (구간 전체를 덮는지 알아야 해서).
        // Border 는 그 관측 때 프레임 테두리에 닿은 변 (프레임 크기를 안 받았으면 None)
        private readonly List<(double Ms, Det Box, BoxEdges Border)> _recent = [];
        private readonly Dictionary<string, double> _labelScore = [];
        private double _vx, _vy;   // 중심 속도 px/ms

        internal Track(int id, Det det, double timeMs, BoxEdges border)
        {
            Id = id;
            Group = Tracker.GroupOf(det.Label);
            FirstMs = timeMs;
            Label = det.Label;
            Observe(det, timeMs, border, null);
        }

        public int Id { get; }

        /// <summary>매칭 묶음 (<see cref="Tracker.GroupOf"/>). 처음 검출로 정하고 바꾸지 않는다.</summary>
        public string Group { get; }

        /// <summary>신뢰도 합이 가장 큰 라벨 — 프레임마다 흔들리는 라벨 대신 보여 줄 값.</summary>
        public string Label { get; private set; }

        /// <summary>마지막으로 관측된 박스.</summary>
        public Det Last { get; private set; }

        public double FirstMs { get; }
        public double LastMs { get; private set; }

        /// <summary>관측된 프레임 수.</summary>
        public int Hits { get; private set; }

        /// <summary>
        /// 최근 판정 구간 동안 거의 안 움직였는지. 안 보이는 동안은 마지막 판정을 유지한다.
        /// 박스가 프레임 테두리에 걸쳐 움직임을 잴 수 없을 때도(한 축의 두 변이 다 테두리에 닿음) 그대로 둔다 — 보이는 움직임만으로
        /// 허용치를 넘을 때만 정지를 푼다.
        /// </summary>
        public bool IsStationary { get; private set; }

        internal void Observe(Det det, double timeMs, BoxEdges border, Tracker? p)
        {
            Last = det;
            LastMs = timeMs;
            Hits++;

            double score = _labelScore.GetValueOrDefault(det.Label) + det.Conf;
            _labelScore[det.Label] = score;
            if (score > _labelScore.GetValueOrDefault(Label)) Label = det.Label;

            _recent.Add((timeMs, det, border));
            if (p == null) return;   // 첫 관측
            int drop = 0;
            while (_recent.Count - drop > 1 && timeMs - _recent[drop + 1].Ms >= p.StationaryWindowMs) drop++;
            _recent.RemoveRange(0, drop);

            var (oldMs, old, oldBorder) = _recent[0];
            double span = timeMs - oldMs;
            // 속도는 0.1초 넘게 본 뒤에만 낸다 — 연속 두 프레임 차이는 박스 떨림이 대부분이다
            (_vx, _vy) = span >= 100
                ? ((det.X + det.W / 2 - old.X - old.W / 2) / span, (det.Y + det.H / 2 - old.Y - old.H / 2) / span)
                : (0, 0);

            if (span >= p.StationaryMinSpanMs)
            {
                // 이동량은 중심이 아니라 마주 보는 두 변 중 덜 움직인 쪽으로 잰다. 멈춘 차가 한쪽부터 가려지면 박스가 좁아져
                // 중심이 밀리지만 반대쪽 변은 그대로다 (test5 화물차: 지나가는 트럭에 가려 폭 64→40, 중심 0.21×크기 이동).
                // 정말 움직이는 물체는 두 변이 같이 움직인다.
                // 단 프레임 테두리에 닿은 변(지금·기준 박스 어느 쪽이든)은 뺀다 — 들어오거나 나가는 물체는 보이는 부분까지만 박스가 잡혀
                // 그 변이 테두리에 머문다. 그대로 재면 움직여도 덜 움직인 변이 0 이라 멈춘 것이 됐다(이슈 #10: 정지면 예측이 꺼지고
                // 화면을 나간 뒤에도 10초 남아 같은 자리에 새로 들어온 차가 그 ID 를 물려받는다). 그 축은 반대쪽 변으로만 잰다 —
                // 정말 멈춘 채 걸친 차는 반대쪽 변도 안 움직여 정지로 남는다
                double left = Math.Abs(det.X - old.X), right = Math.Abs(det.X + det.W - old.X - old.W);
                double top = Math.Abs(det.Y - old.Y), bottom = Math.Abs(det.Y + det.H - old.Y - old.H);
                var pinned = border | oldBorder;
                double? dx = LessMoved(left, right, pinned.HasFlag(BoxEdges.Left), pinned.HasFlag(BoxEdges.Right));
                double? dy = LessMoved(top, bottom, pinned.HasFlag(BoxEdges.Top), pinned.HasFlag(BoxEdges.Bottom));
                double size = Math.Sqrt(Math.Max(det.W * det.H, old.W * old.H));
                double allowed = p.StationaryMaxShift * size * Math.Min(span, p.StationaryWindowMs) / p.StationaryWindowMs;
                if (dx is { } x && dy is { } y)
                {
                    IsStationary = Math.Sqrt(x * x + y * y) <= allowed;
                }
                else
                {
                    // 한 축의 두 변이 다 테두리에 닿으면(박스가 그 축을 가로지름) 그 축은 잴 수 없다. 테두리에 잘린 변은 실제보다 덜
                    // 움직여 보이므로 그 축을 그대로 잰 값은 아래 한계일 뿐이다 — 그것만으로도 허용치를 넘으면 움직인 것이고,
                    // 아니면 멈췄는지 알 수 없어 판정을 그대로 둔다 (정지로 바꾸지는 않는다)
                    double bx = dx ?? Math.Min(left, right), by = dy ?? Math.Min(top, bottom);
                    if (Math.Sqrt(bx * bx + by * by) > allowed) IsStationary = false;
                }
            }
        }

        // 한 축의 이동량: 마주 보는 두 변의 이동 중 작은 쪽. 테두리에 닿은 변은 빼고, 둘 다 닿았으면 잴 수 없다(null)
        private static double? LessMoved(double low, double high, bool lowPinned, bool highPinned) => (lowPinned, highPinned) switch
        {
            (false, false) => Math.Min(low, high),
            (true, false) => high,
            (false, true) => low,
            _ => null,
        };

        /// <summary>
        /// 그 시각에 있을 것으로 보이는 박스. 멈춘 트랙은 마지막 자리, 움직이는 트랙은 등속으로 민다
        /// (예측 시간은 <paramref name="maxDtMs"/> 까지만 — 그보다 오래 안 보인 움직이는 트랙은 이미 지워졌다).
        /// </summary>
        internal Det Predict(double timeMs, double maxDtMs)
        {
            if (IsStationary) return Last;
            double dt = Math.Min(timeMs - LastMs, maxDtMs);
            return Last with { X = Last.X + _vx * dt, Y = Last.Y + _vy * dt };
        }
    }
}
