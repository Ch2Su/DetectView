using System;
using System.Collections.Generic;
using System.Linq;

namespace YOLO11_Test
{
    internal enum ZoneEventKind
    {
        /// <summary>물체가 구역에 들어왔다 (<see cref="ZoneEvent.Initial"/> 이면 감지를 시작할 때 이미 안에 있었다)</summary>
        Enter,
        /// <summary>구역에서 나갔다 (<see cref="ZoneEvent.Lost"/> 이면 안에서 추적이 끝났다 — 화면 밖으로 나갔거나 가려졌다)</summary>
        Exit,
        /// <summary>표시: 화면이 전환돼 구역 이벤트를 멈췄다 (어느 뷰인지 몰라 구역이 맞는지 알 수 없다)</summary>
        ViewChanged,
    }

    /// <summary>구역 이벤트 한 건 — 로그 한 줄. Ms 는 실제로 넘어간 영상 시각이다(확인은 조금 늦다)</summary>
    internal sealed record ZoneEvent(ZoneEventKind Kind, double Ms, int TrackId, string Label, int ZoneId, RoiKind ZoneKind,
                                     double DwellMs = 0, bool Initial = false, bool Lost = false)
    {
        public string ZoneName => ZoneId == 0 ? "" : $"Z{ZoneId} {RoiKinds.Name(ZoneKind)}";

        /// <summary>화면의 동작 글자</summary>
        public string ActionText => Kind switch
        {
            ZoneEventKind.Enter => Initial ? "이미 안에 있음" : "진입",
            ZoneEventKind.Exit => Lost ? "나감 (추적 끝)" : "나감",
            _ => "화면 전환 — 구역 이벤트 멈춤 (뷰를 알 수 없음)",
        };

        /// <summary>CSV 의 event 열 (기계가 읽는 이름)</summary>
        public string CsvEvent => Kind switch
        {
            ZoneEventKind.Enter => Initial ? "initial" : "enter",
            ZoneEventKind.Exit => Lost ? "lost" : "exit",
            _ => "view_changed",
        };
    }

    /// <summary>구역 이벤트 감지 파라미터</summary>
    internal sealed record ZoneEventOptions
    {
        /// <summary>구역 안에 이만큼(ms) 이어서 있어야 '진입' — 경계에서 한두 프레임 걸쳤다 빠지는 것은 이벤트가 아니다</summary>
        public double EnterMs { get; init; } = 200;
        /// <summary>구역 밖에 이만큼(ms) 이어서 있어야 '나감' — 박스가 경계에서 떨려 들락거려도 나감·진입이 반복되지 않는다</summary>
        public double ExitMs { get; init; } = 500;
        /// <summary>추적이 이만큼(ms) 끊기면 안에 있던 구역에서 '나감(추적 끝)' — 이보다 짧은 가림은 추적기가 이어 준다(움직이는 물체의 이음 간격 1초)</summary>
        public double LostMs { get; init; } = 1500;
        /// <summary>감지를 시작(또는 Reset)한 뒤 이 시간(ms) 안에 처음 본 물체가 이미 구역 안이면 '이미 안에 있음' — 시크·편집 뒤 재개마다 '진입'이 쏟아지지 않게</summary>
        public double BaselineMs { get; init; } = 500;
    }

    /// <summary>
    /// 구역 진입·나감 이벤트 (유고 감지 3단계, 2026-11-03) — 순수 알고리즘(WPF·OpenCV 없음). 추적기를 거친 박스(<see cref="Det.TrackId"/> &gt; 0)를
    /// 프레임마다 받아 물체의 <b>발 위치</b>(박스 아래 가운데, 프레임 0~1 좌표)가 구역 안인지로 본다.
    /// <list type="bullet">
    /// <item>유형이 '무시'인 구역은 이벤트를 내지 않고, 발이 무시 구역 안에 있는 물체는 다른 구역에서도 빠진다 (방향 안내 글자·기둥·레터박스 자리)</item>
    /// <item>겹친 구역은 각각 따로 센다 (차로 안의 정지선·대기 구역)</item>
    /// <item>진입은 <see cref="ZoneEventOptions.EnterMs"/> 이상 안에 있어야, 나감은 <see cref="ZoneEventOptions.ExitMs"/> 이상 밖에 있어야 확정한다(경계 떨림 흡수). 이벤트 시각은 실제로 넘어간 때</item>
    /// <item>안에서 추적이 끝나면(<see cref="ZoneEventOptions.LostMs"/>) '나감(추적 끝)'. 같은 물체가 다시 보이면 새로 '진입'</item>
    /// </list>
    /// <see cref="Suspend"/> 뒤에는 <see cref="Reset"/> 까지 아무것도 내지 않는다 — 화면이 전환되면 구역이 그려진 뷰인지 알 수 없다(2b 에서 뷰를 알아본다).
    /// 한 스레드(재생 루프)에서만 쓴다
    /// </summary>
    internal sealed class ZoneEventDetector
    {
        private sealed class InZone
        {
            public double CandidateSince = -1;   // 안에 있기 시작한 시각 (확정 전)
            public bool Confirmed;
            public double EnteredMs;
            public bool Initial;
            public double OutsideSince = -1;     // 확정된 뒤 밖에 있기 시작한 시각
        }

        private sealed class TrackState
        {
            public string Label = "";
            public double LastSeenMs;
            public readonly Dictionary<int, InZone> Zones = [];
        }

        private readonly ZoneEventOptions _o;
        private readonly Dictionary<int, TrackState> _tracks = [];
        private readonly HashSet<int> _seen = [];
        private bool _suspended;
        private bool _needBaseline = true;
        private double _baselineUntilMs;

        public ZoneEventDetector(ZoneEventOptions? options = null) => _o = options ?? new ZoneEventOptions();

        /// <summary>멈춘 상태 (화면이 전환된 뒤 Reset 까지)</summary>
        public bool Suspended => _suspended;

        /// <summary>
        /// 상태를 조용히 버리고(이벤트 없이) 다시 시작한다 — 시크·구역 편집·소스 전환처럼 앞뒤가 이어지지 않는 곳에서.
        /// 멈춘 상태도 풀린다 (시크는 사용자가 지금 화면에 구역이 맞다고 보는 것)
        /// </summary>
        public void Reset()
        {
            _tracks.Clear();
            _suspended = false;
            _needBaseline = true;
        }

        /// <summary>
        /// 화면이 전환됐다 — 이후 이벤트를 멈추고 진행 중이던 상태를 버린다(안에 있던 물체의 '나감'은 기록하지 않는다: 뷰가 바뀌어 알 수 없다).
        /// 로그에 남길 표시 이벤트를 돌려준다. 이미 멈춘 상태면 null
        /// </summary>
        public ZoneEvent? Suspend(double ms)
        {
            if (_suspended) return null;
            _suspended = true;
            _tracks.Clear();
            return new ZoneEvent(ZoneEventKind.ViewChanged, ms, 0, "", 0, default);
        }

        /// <summary>이번 프레임에서 확정된 이벤트들 (없으면 빈 목록). 프레임 크기는 박스 좌표(픽셀)를 0~1 로 바꾸는 데 쓴다</summary>
        public List<ZoneEvent> Update(IReadOnlyList<Det> dets, IReadOnlyList<ZoneShape> zones, double ms, int frameW, int frameH)
        {
            var events = new List<ZoneEvent>();
            if (_suspended) return events;
            if (zones.Count == 0 || frameW <= 0 || frameH <= 0) { _tracks.Clear(); return events; }

            if (_needBaseline) { _baselineUntilMs = ms + _o.BaselineMs; _needBaseline = false; }
            _seen.Clear();

            foreach (var d in dets)
            {
                if (d.TrackId <= 0) continue;
                _seen.Add(d.TrackId);
                if (!_tracks.TryGetValue(d.TrackId, out var t)) _tracks[d.TrackId] = t = new TrackState();
                t.Label = d.Label;
                t.LastSeenMs = ms;

                double fx = RoiGeometry.Clamp01((d.X + d.W / 2) / frameW);
                double fy = RoiGeometry.Clamp01((d.Y + d.H) / frameH);
                bool ignored = false;
                foreach (var z in zones)
                    if (z.Kind == RoiKind.Ignore && z.Contains(fx, fy)) { ignored = true; break; }

                // 지워진 구역의 상태는 조용히 버린다
                if (t.Zones.Count > 0)
                    foreach (int id in t.Zones.Keys.Where(id => zones.All(z => z.Id != id)).ToList()) t.Zones.Remove(id);

                foreach (var z in zones)
                {
                    if (z.Kind == RoiKind.Ignore) continue;
                    bool inside = !ignored && z.Contains(fx, fy);
                    if (!t.Zones.TryGetValue(z.Id, out var s))
                    {
                        if (!inside) continue;
                        t.Zones[z.Id] = s = new InZone();
                    }

                    if (inside)
                    {
                        s.OutsideSince = -1;
                        if (s.Confirmed) continue;
                        if (s.CandidateSince < 0) s.CandidateSince = ms;
                        if (ms - s.CandidateSince >= _o.EnterMs)
                        {
                            s.Confirmed = true;
                            s.EnteredMs = s.CandidateSince;
                            s.Initial = s.CandidateSince < _baselineUntilMs;
                            events.Add(new ZoneEvent(ZoneEventKind.Enter, s.EnteredMs, d.TrackId, t.Label, z.Id, z.Kind, Initial: s.Initial));
                        }
                    }
                    else
                    {
                        s.CandidateSince = -1;
                        if (!s.Confirmed) { t.Zones.Remove(z.Id); continue; }
                        if (s.OutsideSince < 0) s.OutsideSince = ms;
                        if (ms - s.OutsideSince >= _o.ExitMs)
                        {
                            events.Add(new ZoneEvent(ZoneEventKind.Exit, s.OutsideSince, d.TrackId, t.Label, z.Id, z.Kind, DwellMs: s.OutsideSince - s.EnteredMs));
                            t.Zones.Remove(z.Id);
                        }
                    }
                }
            }

            // 이번 프레임에 안 보인 트랙: 오래 안 보이면 안에 있던 구역에서 '나감(추적 끝)'
            foreach (var (id, t) in _tracks.ToList())
            {
                if (_seen.Contains(id) || ms - t.LastSeenMs < _o.LostMs) continue;
                foreach (var (zid, s) in t.Zones)
                {
                    if (!s.Confirmed) continue;
                    var z = zones.FirstOrDefault(q => q.Id == zid);
                    if (z == null) continue;
                    events.Add(new ZoneEvent(ZoneEventKind.Exit, t.LastSeenMs, id, t.Label, zid, z.Kind, DwellMs: t.LastSeenMs - s.EnteredMs, Lost: true));
                }
                _tracks.Remove(id);
            }
            return events;
        }
    }
}
