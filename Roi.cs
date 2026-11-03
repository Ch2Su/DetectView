using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace YOLO11_Test
{
    /// <summary>
    /// 관심 구역(ROI) 유형 — 나중에 유고 규칙이 '갓길에 멈춤'과 '신호대기 줄에 멈춤'을 가르는 데 쓴다
    /// (CLAUDE.md '관심 구역'). 무시 구역은 규칙에서 빼는 자리(방향 안내 글자·기둥·레터박스 등).
    /// </summary>
    internal enum RoiKind { Lane, Shoulder, Stopline, Ignore }

    internal static class RoiKinds
    {
        public static readonly RoiKind[] All = [RoiKind.Lane, RoiKind.Shoulder, RoiKind.Stopline, RoiKind.Ignore];

        /// <summary>화면에 보이는 이름 (UI 테스트가 목록 항목·콤보 항목 이름으로 읽는다)</summary>
        public static string Name(RoiKind k) => k switch
        {
            RoiKind.Lane => "차로",
            RoiKind.Shoulder => "갓길",
            RoiKind.Stopline => "정지선·대기",
            _ => "무시",
        };

        /// <summary>구역 파일(.roi.json)에 적히는 이름</summary>
        public static string Key(RoiKind k) => k switch
        {
            RoiKind.Lane => "lane",
            RoiKind.Shoulder => "shoulder",
            RoiKind.Stopline => "stopline",
            _ => "ignore",
        };

        public static bool TryParse(string? key, out RoiKind kind)
        {
            foreach (var k in All)
                if (Key(k) == key) { kind = k; return true; }
            kind = default;
            return false;
        }
    }

    /// <summary>프레임 안의 점 — 프레임 크기를 1 로 본 0~1 좌표 (해상도·창 크기와 무관하다)</summary>
    internal readonly record struct RoiPoint(double X, double Y);

    internal sealed class RoiZone
    {
        public RoiZone(int id, RoiKind kind, IEnumerable<RoiPoint> points)
        {
            Id = id;
            Kind = kind;
            Points = points.Select(p => new RoiPoint(RoiGeometry.Clamp01(p.X), RoiGeometry.Clamp01(p.Y))).ToList();
        }

        /// <summary>1 부터, 지운 번호는 다시 쓰지 않는다 (나중에 이벤트 로그가 번호로 구역을 가리킨다)</summary>
        public int Id { get; }
        public RoiKind Kind { get; set; }
        public List<RoiPoint> Points { get; }

        /// <summary>"Z1"</summary>
        public string Label => $"Z{Id}";
        /// <summary>"Z1 차로"</summary>
        public string DisplayName => $"{Label} {RoiKinds.Name(Kind)}";

        public bool Contains(double x, double y) => RoiGeometry.Contains(Points, x, y);
    }

    /// <summary>
    /// 구역 하나의 모양 스냅샷 — 재생 루프 스레드가 읽는 불변 값. UI 스레드가 구역을 고치는 동안 루프가 같은 목록을 읽지 않게
    /// (구역은 멈춘 뒤에만 편집되지만 멈추는 순간 루프가 한 프레임을 더 처리 중일 수 있다) 바뀔 때마다 새로 만들어 참조를 바꾼다
    /// </summary>
    internal sealed record ZoneShape(int Id, RoiKind Kind, RoiPoint[] Points)
    {
        public string Name => $"Z{Id} {RoiKinds.Name(Kind)}";
        public bool Contains(double x, double y) => RoiGeometry.Contains(Points, x, y);
    }

    /// <summary>
    /// 구역 순수 알고리즘 (WPF 없음 — experiments/TrackerCheck 가 소스로 링크해 selftest 로 본다)
    /// </summary>
    internal static class RoiGeometry
    {
        public static double Clamp01(double v) => double.IsNaN(v) ? 0 : Math.Clamp(v, 0, 1);

        /// <summary>다각형 넓이 (신발끈 공식, 절댓값)</summary>
        public static double Area(IReadOnlyList<RoiPoint> p)
        {
            double a = 0;
            for (int i = 0, j = p.Count - 1; i < p.Count; j = i++)
                a += p[j].X * p[i].Y - p[i].X * p[j].Y;
            return Math.Abs(a) / 2;
        }

        /// <summary>점이 다각형 안에 있는가 (반직선 교차 — 변 위의 점은 어느 쪽이든 될 수 있다)</summary>
        public static bool Contains(IReadOnlyList<RoiPoint> p, double x, double y)
        {
            bool inside = false;
            for (int i = 0, j = p.Count - 1; i < p.Count; j = i++)
            {
                double xi = p[i].X, yi = p[i].Y, xj = p[j].X, yj = p[j].Y;
                if ((yi > y) != (yj > y) && x < (xj - xi) * (y - yi) / (yj - yi) + xi) inside = !inside;
            }
            return inside;
        }

        /// <summary>라벨을 놓을 자리 — 넓이 가중 무게중심 (넓이가 거의 0 이면 점 평균)</summary>
        public static RoiPoint Centroid(IReadOnlyList<RoiPoint> p)
        {
            double a = 0, cx = 0, cy = 0;
            for (int i = 0, j = p.Count - 1; i < p.Count; j = i++)
            {
                double cross = p[j].X * p[i].Y - p[i].X * p[j].Y;
                a += cross;
                cx += (p[j].X + p[i].X) * cross;
                cy += (p[j].Y + p[i].Y) * cross;
            }
            if (Math.Abs(a) < 1e-12) return new RoiPoint(p.Average(q => q.X), p.Average(q => q.Y));
            return new RoiPoint(cx / (3 * a), cy / (3 * a));
        }

        /// <summary>점들 중 (x, y) 에서 maxDist 안에서 가장 가까운 것의 번호 (없으면 -1). 좌표는 화면 DIP 처럼 같은 단위면 된다</summary>
        public static int NearestVertex(IReadOnlyList<(double X, double Y)> p, double x, double y, double maxDist)
        {
            int best = -1;
            double bestD = maxDist * maxDist;
            for (int i = 0; i < p.Count; i++)
            {
                double dx = p[i].X - x, dy = p[i].Y - y, d = dx * dx + dy * dy;
                if (d <= bestD) { bestD = d; best = i; }
            }
            return best;
        }

        /// <summary>
        /// 변 가운데 ○ 중 (x, y) 에서 maxDist 안에서 가장 가까운 것의 변 번호 i (변 i = 점 i → 점 i+1, 없으면 -1).
        /// minEdge 보다 짧은 변은 ○ 를 그리지 않으므로 건너뛴다
        /// </summary>
        public static int NearestEdgeMidpoint(IReadOnlyList<(double X, double Y)> p, double x, double y, double maxDist, double minEdge)
        {
            int best = -1;
            double bestD = maxDist * maxDist;
            for (int i = 0; i < p.Count; i++)
            {
                var a = p[i];
                var b = p[(i + 1) % p.Count];
                if (Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y)) < minEdge) continue;
                double dx = (a.X + b.X) / 2 - x, dy = (a.Y + b.Y) / 2 - y, d = dx * dx + dy * dy;
                if (d <= bestD) { bestD = d; best = i; }
            }
            return best;
        }
    }

    /// <summary>
    /// 영상 하나의 구역들 + 파일 입출력. 파일은 영상 옆 <c>&lt;영상&gt;.roi.json</c> (프레임 0~1 좌표라 해상도가 달라도 그대로 쓴다).
    /// 순수 코드 — 그리기·편집은 RoiLayer, 패널은 MainWindow.Roi.cs
    /// </summary>
    internal sealed class RoiSet
    {
        public const int Version = 1;
        public const int MinPoints = 3;

        private readonly List<RoiZone> _zones = [];

        public IReadOnlyList<RoiZone> Zones => _zones;

        /// <summary>다음에 붙일 번호 — 지운 번호는 다시 쓰지 않는다 (파일에도 적어 다시 열어도 이어진다)</summary>
        public int NextId { get; private set; } = 1;

        public static string PathFor(string videoPath) => videoPath + ".roi.json";

        public RoiZone Add(RoiKind kind, IEnumerable<RoiPoint> points)
        {
            var z = new RoiZone(NextId, kind, points);
            if (z.Points.Count < MinPoints) throw new ArgumentException($"구역은 점이 {MinPoints}개 이상이어야 한다");
            NextId++;
            _zones.Add(z);
            return z;
        }

        public bool Remove(int id) => _zones.RemoveAll(z => z.Id == id) > 0;

        public RoiZone? Find(int id) => _zones.FirstOrDefault(z => z.Id == id);

        /// <summary>
        /// 지금 구역들의 불변 복사본 — 재생 루프 스레드가 구역 이벤트 감지(ZoneEvents.cs)에 쓴다. 이 객체의 점 목록은 UI 스레드가 고치므로
        /// 루프는 원본을 읽지 않고 이 복사본을 읽는다 (바뀔 때마다 새로 만들어 참조를 바꾼다)
        /// </summary>
        public ZoneShape[] Snapshot() => _zones.Select(z => new ZoneShape(z.Id, z.Kind, z.Points.ToArray())).ToArray();

        /// <summary>
        /// (x, y) 를 품은 구역 중 가장 작은 것 — 큰 구역 안의 작은 구역(차로 안의 정지선·대기 등)도 고를 수 있게.
        /// 좌표가 프레임 밖(0~1 밖)이면 없음
        /// </summary>
        public RoiZone? ZoneAt(double x, double y)
        {
            if (x < 0 || x > 1 || y < 0 || y > 1) return null;
            RoiZone? best = null;
            double bestArea = double.MaxValue;
            foreach (var z in _zones)
            {
                if (!z.Contains(x, y)) continue;
                double a = RoiGeometry.Area(z.Points);
                if (a < bestArea) { bestArea = a; best = z; }
            }
            return best;
        }

        // ---------- 파일 ----------

        public string ToJson()
        {
            var sb = new StringBuilder();
            sb.Append("{\n  \"version\": ").Append(Version).Append(",\n  \"nextId\": ").Append(NextId).Append(",\n  \"zones\": [");
            for (int i = 0; i < _zones.Count; i++)
            {
                var z = _zones[i];
                sb.Append(i == 0 ? "\n" : ",\n");
                sb.Append("    { \"id\": ").Append(z.Id).Append(", \"kind\": \"").Append(RoiKinds.Key(z.Kind)).Append("\", \"points\": [");
                for (int k = 0; k < z.Points.Count; k++)
                {
                    if (k > 0) sb.Append(", ");
                    sb.Append('[').Append(Num(z.Points[k].X)).Append(", ").Append(Num(z.Points[k].Y)).Append(']');
                }
                sb.Append("] }");
            }
            sb.Append(_zones.Count == 0 ? "]\n}\n" : "\n  ]\n}\n");
            return sb.ToString();
        }

        private static string Num(double v) => Math.Round(v, 5).ToString("0.#####", CultureInfo.InvariantCulture);

        // 숫자가 아닌 값에 TryGetDouble 을 부르면 false 가 아니라 InvalidOperationException 이라, 종류부터 본다
        private static bool TryNumber(JsonElement e, out double value)
        {
            value = 0;
            return e.ValueKind == JsonValueKind.Number && e.TryGetDouble(out value);
        }

        /// <summary>읽지 못하면 <see cref="FormatException"/> (사유가 메시지에). 좌표가 0~1 밖이면 안으로 맞춘다</summary>
        public static RoiSet FromJson(string json)
        {
            JsonDocument doc;
            try { doc = JsonDocument.Parse(json); }
            catch (JsonException ex) { throw new FormatException("JSON 형식이 아님: " + ex.Message, ex); }

            using (doc)
            {
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) throw new FormatException("최상위가 객체가 아님");
                if (!root.TryGetProperty("version", out var ver) || ver.ValueKind != JsonValueKind.Number || !ver.TryGetInt32(out int v) || v != Version)
                    throw new FormatException($"지원하지 않는 판 (version {Version} 만 읽는다)");

                int nextId = 1;
                if (root.TryGetProperty("nextId", out var ni)
                    && !(ni.ValueKind == JsonValueKind.Number && ni.TryGetInt32(out nextId) && nextId >= 1))
                    throw new FormatException("nextId 가 1 이상의 정수가 아님");

                if (!root.TryGetProperty("zones", out var zs) || zs.ValueKind != JsonValueKind.Array)
                    throw new FormatException("zones 배열이 없음");

                var set = new RoiSet();
                var seen = new HashSet<int>();
                int maxId = 0, index = 0;
                foreach (var ze in zs.EnumerateArray())
                {
                    string at = $"zones[{index++}]";
                    if (ze.ValueKind != JsonValueKind.Object) throw new FormatException($"{at} 가 객체가 아님");

                    if (!ze.TryGetProperty("id", out var ide) || ide.ValueKind != JsonValueKind.Number || !ide.TryGetInt32(out int id) || id < 1)
                        throw new FormatException($"{at}.id 가 1 이상의 정수가 아님");
                    if (!seen.Add(id)) throw new FormatException($"{at}.id {id} 가 겹침");

                    string? ks = ze.TryGetProperty("kind", out var ke) && ke.ValueKind == JsonValueKind.String ? ke.GetString() : null;
                    if (!RoiKinds.TryParse(ks, out var kind)) throw new FormatException($"{at}.kind 를 모름: {ks ?? "(없음)"}");

                    if (!ze.TryGetProperty("points", out var pe) || pe.ValueKind != JsonValueKind.Array)
                        throw new FormatException($"{at}.points 배열이 없음");
                    var pts = new List<RoiPoint>();
                    foreach (var pt in pe.EnumerateArray())
                    {
                        double x = 0, y = 0;
                        if (pt.ValueKind != JsonValueKind.Array || pt.GetArrayLength() != 2
                            || !TryNumber(pt[0], out x) || !TryNumber(pt[1], out y)
                            || !double.IsFinite(x) || !double.IsFinite(y))
                            throw new FormatException($"{at}.points 의 점이 [x, y] 숫자 둘이 아님");
                        pts.Add(new RoiPoint(x, y));
                    }
                    if (pts.Count < MinPoints) throw new FormatException($"{at}.points 가 {MinPoints}개 미만");

                    set._zones.Add(new RoiZone(id, kind, pts));
                    maxId = Math.Max(maxId, id);
                }
                set.NextId = Math.Max(nextId, maxId + 1);
                return set;
            }
        }

        /// <summary>파일이 없으면 빈 구역들. 읽지 못하면 예외 (FormatException·IOException 등)</summary>
        public static RoiSet Load(string path) =>
            File.Exists(path) ? FromJson(File.ReadAllText(path, Encoding.UTF8)) : new RoiSet();

        /// <summary>구역이 하나도 없으면 파일을 지운다 (빈 파일을 영상 옆에 남기지 않는다). 쓰다 끊겨도 옛 파일이 상하지 않게 임시 파일에 쓴 뒤 바꾼다</summary>
        public void Save(string path)
        {
            if (_zones.Count == 0)
            {
                if (File.Exists(path)) File.Delete(path);
                return;
            }
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, ToJson(), new UTF8Encoding(false));
            File.Move(tmp, path, overwrite: true);
        }
    }
}
