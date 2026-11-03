using System;
using System.Collections.Generic;

namespace DetectView
{
    /// <summary>
    /// 검출 결과 1건. 좌표는 원본 프레임 픽셀 기준.
    /// (SAHI 병합과 화면 HUD 오버레이가 공유)
    /// </summary>
    internal readonly record struct Det(
        double X, double Y, double W, double H, string Label, double Conf)
    {
        // 아래 둘은 추적기를 거친 화면 표시용이다 (Tracker.UpdateTagged). 위치 인자가 아니라 init 속성인 이유: 검출기·SAHI·실험 도구가
        // 6개 인자로 만들고 풀어 쓰는 코드를 그대로 두려고. 추적하지 않은 검출(SAHI 결과 등)은 0 / false 로 남아 라벨에 안 나온다.
        // 같은 필드가 박스와 함께 흘러가므로(HUD 의 _latestDets 처럼 한 참조로 넘기는 값) 번호와 박스가 어긋나는 일이 없다

        /// <summary>추적 번호 (1부터, 0 = 추적하지 않음)</summary>
        public int TrackId { get; init; }

        /// <summary>추적기가 멈춘 물체로 본다 (<see cref="Track.IsStationary"/>)</summary>
        public bool Stationary { get; init; }

        /// <summary>유고 후보로 선언된 물체 (IncidentRules.cs) — 화면에서 박스를 빨갛게 강조한다. 추적하지 않은 검출은 false</summary>
        public bool Incident { get; init; }
    }

    /// <summary>
    /// 한 프레임 안의 같은 자리 중복 박스 판정 (UI·모델과 무관한 순수 함수). 화면 표시·검출 수와 추적기가 같은 규칙을 쓴다.
    ///
    /// 모델은 end-to-end NMS 내장인데도 같은 물체에 박스를 둘 이상 낸다 — 대부분 같은 클래스 IoU≈1.0 이고
    /// (test4 916 프레임 중 466 프레임, <c>RunObjectDetection</c> 의 <c>iou</c> 인자가 이 모델엔 안 걸린다),
    /// car+truck 처럼 차량 라벨만 다르거나 범위만 다른 박스(짐칸·탑승자 포함/제외, IoU 0.5~0.7)도 있다.
    /// 일반 NMS 가 아니다: 같은 묶음끼리, 작은 박스가 큰 박스 안에 거의 다 들어 있고 크기도 비슷할 때만 한 물체로 본다 —
    /// 나란히 서서 일부만 겹친 두 차나 버스에 가려진 옆 차는 그대로 남아야 한다.
    /// </summary>
    internal static class DuplicateBoxes
    {
        // 한 물체로 볼 수 있는 클래스 묶음. 같은 차가 truck↔car 로 흔들리고, 멀리 있는 이륜차도 서로·승용차와 흔들린다
        private static readonly HashSet<string> VehicleLabels =
            ["car", "truck", "bus", "motorcycle", "scooter", "bicycle"];

        /// <summary>
        /// 같은 묶음의 작은 박스가 큰 박스 안에 이 비율 이상 들어 있으면(IoS) 중복이다.
        /// IoU 가 아니라 IoS 인 이유: 범위만 다른 중복(IoU 0.5~0.7)도 잡아야 한다. IoU 0.7 넘는 쌍은 IoS 0.82 를 넘으므로 함께 잡힌다.
        /// </summary>
        public const double DefaultIos = 0.8;

        /// <summary>
        /// 중복으로 볼 최소 넓이 비(작은/큰). 버스 박스에 거의 다 들어가는 옆 차(넓이 비 0.13~0.16)처럼
        /// 크기가 크게 다르면 겹쳐 보여도 다른 물체다.
        /// </summary>
        public const double DefaultMinAreaRatio = 0.5;

        /// <summary>클래스 묶음. 차량 6종은 "vehicle", 나머지는 라벨 그대로.</summary>
        public static string GroupOf(string label) => VehicleLabels.Contains(label) ? "vehicle" : label;

        /// <summary>한 프레임 안의 두 박스가 한 물체의 중복 출력인지.</summary>
        public static bool IsDuplicate(Det a, Det b, double ios = DefaultIos, double minAreaRatio = DefaultMinAreaRatio)
        {
            if (GroupOf(a.Label) != GroupOf(b.Label)) return false;
            double areaA = a.W * a.H, areaB = b.W * b.H;
            double smaller = Math.Min(areaA, areaB), larger = Math.Max(areaA, areaB);
            return smaller > 0 && smaller >= minAreaRatio * larger && Intersection(a, b) >= ios * smaller;
        }

        /// <summary>
        /// 중복 박스마다 대표(신뢰도가 가장 높은 박스)의 번호, 대표 자신은 -1.
        /// 신뢰도 순으로 대표를 정하고 대표와 겹치는 것만 묶는다 — 버려진 박스는 다른 박스를 버리지 않는다.
        /// </summary>
        public static int[] FindDuplicates(IReadOnlyList<Det> dets, double ios = DefaultIos, double minAreaRatio = DefaultMinAreaRatio)
        {
            var dupOf = new int[dets.Count];
            Array.Fill(dupOf, -1);
            var order = new int[dets.Count];
            for (int i = 0; i < order.Length; i++) order[i] = i;
            Array.Sort(order, (a, b) => dets[b].Conf.CompareTo(dets[a].Conf));
            for (int a = 0; a < order.Length; a++)
            {
                int i = order[a];
                if (dupOf[i] >= 0) continue;
                for (int b = a + 1; b < order.Length; b++)
                {
                    int j = order[b];
                    if (dupOf[j] < 0 && IsDuplicate(dets[i], dets[j], ios, minAreaRatio)) dupOf[j] = i;
                }
            }
            return dupOf;
        }

        /// <summary>
        /// 중복을 뺀 목록 (원래 순서 유지). 남는 박스는 신뢰도 높은 쪽 그대로다 — 라벨·좌표를 섞지 않는다.
        /// </summary>
        public static List<Det> Remove(IReadOnlyList<Det> dets)
        {
            var dupOf = FindDuplicates(dets);
            var keep = new List<Det>(dets.Count);
            for (int i = 0; i < dets.Count; i++)
                if (dupOf[i] < 0) keep.Add(dets[i]);
            return keep;
        }

        internal static double Intersection(Det a, Det b)
        {
            double w = Math.Min(a.X + a.W, b.X + b.W) - Math.Max(a.X, b.X);
            double h = Math.Min(a.Y + a.H, b.Y + b.H) - Math.Max(a.Y, b.Y);
            return w > 0 && h > 0 ? w * h : 0;
        }
    }
}
