using System;
using System.Collections.Generic;
using System.Linq;

namespace DetectView
{
    /// <summary>유고 규칙 파라미터</summary>
    internal sealed record IncidentOptions
    {
        /// <summary>
        /// 차로에서 '정지'가 이만큼(ms) 이어지면 유고 후보 — '정지' 판정(추적기: 1초 동안 거의 안 움직임, 관측이 짧으면 0.2초부터) 위에 더하는 값이다.
        /// 사용자가 5초를 골랐다 (3·5·10초 중): 신호대기 안의 정지를 덜 잡고 사고 차량을 너무 늦게 잡지도 않는 중간값
        /// </summary>
        public double HoldMs { get; init; } = 5000;
        /// <summary>정지 판정이 끊기거나 구역을 벗어나도 이 시간(ms) 안에 돌아오면 이어진 것으로 본다 — 박스 떨림·잠깐의 판정 흔들림을 흡수</summary>
        public double GraceMs { get; init; } = 1000;
        /// <summary>추적이 이만큼(ms) 끊기면 '추적 끝' (이보다 짧은 가림은 추적기가 이어 준다)</summary>
        public double LostMs { get; init; } = 1500;
        /// <summary>같은 차로 구역에 (자신 말고) 정지한 차량이 이만큼 이상이면 줄지어 선 정체로 보고 유고 후보로 치지 않는다 — 단 이미 선언한 것은 풀지 않는다</summary>
        public int QueueOthers { get; init; } = 3;
    }

    /// <summary>
    /// 유고 규칙 (유고 감지 4단계, 2026-11-03) — 순수 알고리즘. 지금 규칙은 하나다(사용자가 고름): <b>차로 구역에 멈춘 차량</b>.
    /// 추적기를 거친 차량 박스(<see cref="DuplicateBoxes.GroupOf"/> 가 vehicle — car·truck·bus·motorcycle·scooter·bicycle·pickup)가
    /// <see cref="Det.Stationary"/> 인 채 발 위치(박스 아래 가운데, 프레임 0~1 좌표)가 차로 구역 안에서 <see cref="IncidentOptions.HoldMs"/> 이어지면 유고 후보를 선언한다.
    /// <list type="bullet">
    /// <item>제외: 발이 무시 구역 안, 발이 정지선·대기 구역 안(신호대기), 같은 차로 구역에 정지한 차량이 자신 말고 <see cref="IncidentOptions.QueueOthers"/> 대 이상(줄지어 선 정체)</item>
    /// <item>선언한 뒤엔 정체 제외를 다시 보지 않는다 — 사고 차량 뒤로 차가 밀려 정체가 생기는 게 흔한 결과라, 그걸로 유고를 풀면 사고를 놓친다</item>
    /// <item>풀림: 정지가 아니거나 차로를 벗어난 채 <see cref="IncidentOptions.GraceMs"/> 이어짐 / 추적이 <see cref="IncidentOptions.LostMs"/> 끊김 / 화면 전환·시크·구역 변경(<see cref="Suspend"/>·<see cref="Reset"/>)</item>
    /// <item>갓길 정차·차로 위 사람은 이번 규칙에 없다 (사용자가 차로 정차만 골랐다)</item>
    /// </list>
    /// 이벤트는 <see cref="ZoneEvent"/>(Incident·IncidentEnd)라 구역 이벤트와 같은 로그·CSV 로 간다. 한 스레드(재생 루프)에서만 쓴다
    /// </summary>
    internal sealed class IncidentDetector
    {
        private sealed class State
        {
            public string Label = "";
            public double LastSeenMs;
            public double Since = -1;       // 조건이 처음 참이 된 때
            public double LastTrueMs;       // 조건이 마지막으로 참이던 때
            public bool Declared;
            public int ZoneId;
        }

        private readonly IncidentOptions _o;
        private readonly Dictionary<int, State> _tracks = [];
        private readonly HashSet<int> _seen = [];
        private bool _suspended;
        private double _lastMs;

        public IncidentDetector(IncidentOptions? options = null) => _o = options ?? new IncidentOptions();

        /// <summary>지금 선언된(풀리지 않은) 유고 후보 수</summary>
        public int ActiveCount => _tracks.Values.Count(s => s.Declared);

        /// <summary>이 추적 번호가 지금 유고 후보인가 (화면 박스 강조용)</summary>
        public bool IsActive(int trackId) => _tracks.TryGetValue(trackId, out var s) && s.Declared;

        /// <summary>멈춘 상태 (화면 전환 뒤 Reset 까지 — 구역 이벤트와 같다)</summary>
        public bool Suspended => _suspended;

        /// <summary>
        /// 상태를 버리고 다시 시작한다 — 시크·구역 편집·소스 전환. 선언돼 있던 유고 후보는 끝내는 이벤트(Note)로 돌려준다
        /// (로그에 열린 채 남지 않게). 멈춘 상태도 풀린다
        /// </summary>
        public List<ZoneEvent> Reset(string note = "시크·구역 변경")
        {
            var ends = EndAll(note);
            _tracks.Clear();
            _suspended = false;
            return ends;
        }

        /// <summary>화면 전환 — 이후 아무것도 내지 않는다. 선언돼 있던 유고 후보는 끝내는 이벤트로 돌려준다</summary>
        public List<ZoneEvent> Suspend()
        {
            var ends = EndAll("화면 전환");
            _tracks.Clear();
            _suspended = true;
            return ends;
        }

        private List<ZoneEvent> EndAll(string note)
        {
            var ends = new List<ZoneEvent>();
            foreach (var (id, s) in _tracks)
                if (s.Declared) ends.Add(End(id, s, s.LastTrueMs, note, lost: false, zoneKind: RoiKind.Lane));
            return ends;
        }

        private static ZoneEvent End(int id, State s, double ms, string note, bool lost, RoiKind zoneKind) =>
            new(ZoneEventKind.IncidentEnd, ms, id, s.Label, s.ZoneId, zoneKind, DwellMs: ms - s.Since, Lost: lost, Note: note);

        /// <summary>이번 프레임에서 확정된 유고 후보 선언·해소 이벤트들 (없으면 빈 목록)</summary>
        public List<ZoneEvent> Update(IReadOnlyList<Det> dets, IReadOnlyList<ZoneShape> zones, double ms, int frameW, int frameH)
        {
            var events = new List<ZoneEvent>();
            if (_suspended) return events;
            _lastMs = ms;
            if (zones.Count == 0 || frameW <= 0 || frameH <= 0)
            {
                events.AddRange(EndAll("구역 없음"));
                _tracks.Clear();
                return events;
            }

            // 1) 차량마다 발 위치 → (조건에 맞는 차로 구역, 정지선·대기 안인가, 무시 안인가). 정체 제외를 위해 차로 구역별 정지 차량 수를 센다
            var cands = new List<(Det D, ZoneShape? Lane, bool Cond)>();
            var stationaryIn = new Dictionary<int, int>();
            foreach (var d in dets)
            {
                if (d.TrackId <= 0 || DuplicateBoxes.GroupOf(d.Label) != "vehicle") continue;
                double fx = RoiGeometry.Clamp01((d.X + d.W / 2) / frameW);
                double fy = RoiGeometry.Clamp01((d.Y + d.H) / frameH);
                if (zones.Any(z => z.Kind == RoiKind.Ignore && z.Contains(fx, fy))) { cands.Add((d, null, false)); continue; }
                ZoneShape? lane = null;
                double laneArea = double.MaxValue;
                bool stopline = false;
                foreach (var z in zones)
                {
                    if (!z.Contains(fx, fy)) continue;
                    if (z.Kind == RoiKind.Stopline) stopline = true;
                    else if (z.Kind == RoiKind.Lane)
                    {
                        double a = RoiGeometry.Area(z.Points);
                        if (a < laneArea) { laneArea = a; lane = z; }
                    }
                }
                if (lane != null && d.Stationary) stationaryIn[lane.Id] = stationaryIn.GetValueOrDefault(lane.Id) + 1;
                cands.Add((d, lane, d.Stationary && lane != null && !stopline));
            }

            // 2) 조건이 참인 차량의 후보·선언, 거짓인 차량의 풀림
            _seen.Clear();
            foreach (var (d, lane, cond) in cands)
            {
                _seen.Add(d.TrackId);
                _tracks.TryGetValue(d.TrackId, out var s);
                if (s != null) { s.LastSeenMs = ms; s.Label = d.Label; }

                bool declared = s?.Declared == true;
                bool queue = lane != null && stationaryIn.GetValueOrDefault(lane.Id) - 1 >= _o.QueueOthers;
                bool holds = cond && (declared || !queue);

                if (holds)
                {
                    s ??= _tracks[d.TrackId] = new State { Label = d.Label, LastSeenMs = ms };
                    if (s.Since < 0) { s.Since = ms; s.ZoneId = lane!.Id; }
                    s.LastTrueMs = ms;
                    if (!s.Declared && ms - s.Since >= _o.HoldMs)
                    {
                        s.Declared = true;
                        events.Add(new ZoneEvent(ZoneEventKind.Incident, ms, d.TrackId, s.Label, s.ZoneId, RoiKind.Lane, DwellMs: ms - s.Since));
                    }
                }
                else if (s != null && s.Since >= 0 && ms - s.LastTrueMs >= _o.GraceMs)
                {
                    if (s.Declared) events.Add(End(d.TrackId, s, s.LastTrueMs, "", lost: false, RoiKind.Lane));
                    _tracks.Remove(d.TrackId);
                }
            }

            // 3) 이번 프레임에 안 보인 트랙: 오래 안 보이면 후보를 버리고 선언돼 있으면 '추적 끝'으로 푼다
            foreach (var (id, s) in _tracks.ToList())
            {
                if (_seen.Contains(id) || ms - s.LastSeenMs < _o.LostMs) continue;
                if (s.Declared) events.Add(End(id, s, s.LastSeenMs, "추적 끝", lost: true, RoiKind.Lane));
                _tracks.Remove(id);
            }
            return events;
        }
    }
}
