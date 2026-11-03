using System;
using System.Collections.Generic;
using System.Linq;

namespace DetectView
{
    /// <summary>
    /// SAHI 알고리즘 (UI·모델과 무관한 순수 함수): 타일 원점 계산, 타일 간 중복 박스 병합.
    /// 병합은 IoU(나란히 겹침)와 IoS(작은 박스가 큰 박스에 포함)를 함께 본다.
    /// 앱은 경계 조각만 합치는 <see cref="MergeCutPieces"/> 를 쓴다. 지우기만 하는 <see cref="MergeNms"/>(옛 방식)와 모두 합치는
    /// <see cref="MergeNmm"/>(원본 SAHI — 박스가 부풀어 기각)은 전후 비교용(experiments/TrackerCheck sahi)으로 남긴다.
    /// </summary>
    internal static class Sahi
    {
        public static List<int> TileOrigins(int total, int tile, int step)
        {
            var list = new List<int>();
            if (total <= tile) { list.Add(0); return list; }
            for (int o = 0; ; o += step)
            {
                if (o + tile >= total) { list.Add(total - tile); break; }
                list.Add(o);
            }
            return list;
        }

        public static List<Det> MergeNms(List<Det> dets, double iouThreshold, double iosThreshold)
        {
            var keep = new List<Det>();
            foreach (var d in dets.OrderByDescending(x => x.Conf))
            {
                // 같은 클래스에서 IoU가 크거나(나란히 겹침) IoS가 크면(작은 박스가 큰 박스에 포함) 중복으로 보고 제거
                bool dup = keep.Any(k => k.Label == d.Label &&
                                         (IoU(d, k) > iouThreshold || IoS(d, k) > iosThreshold));
                if (!dup) keep.Add(d);
            }
            return keep;
        }

        /// <summary>
        /// 원본 SAHI 의 GREEDYNMM 처럼 겹치는 같은 라벨 박스를 모두 합친다 — **앱은 쓰지 않는다(비교용)**.
        /// 묶음은 <see cref="MergeNms"/> 와 같다 — 신뢰도 순 대표와 IoU·IoS 가 넘는 박스(대표하고만 비교, 비이행).
        /// 대표를 묶인 박스들의 합집합으로 넓히고 라벨·신뢰도는 대표 것 그대로 둔다.
        /// 기각 사유(2026-10-30 실측, TrackerCheck sahi): 타일·전체 프레임 검출이 같은 물체에 몇 px 씩 다른 박스를 내서 test4 박스의 40% 가
        /// 1~4px 부풀었고(넓이 1.05~1.4배), 신뢰도 낮은 큰 박스가 옆 차들을 한 박스로 묶었다 → <see cref="MergeCutPieces"/>
        /// </summary>
        public static List<Det> MergeNmm(List<Det> dets, double iouThreshold, double iosThreshold)
        {
            var reps = new List<Det>();     // 묶음 판정용 대표 (원래 박스 그대로 — 넓어진 박스로 판정하면 사슬처럼 번진다)
            var merged = new List<Det>();   // 내보낼 박스 (대표를 합집합으로 넓힌 것), reps 와 같은 순서
            foreach (var d in dets.OrderByDescending(x => x.Conf))
            {
                int k = reps.FindIndex(r => r.Label == d.Label && (IoU(d, r) > iouThreshold || IoS(d, r) > iosThreshold));
                if (k < 0) { reps.Add(d); merged.Add(d); continue; }
                var m = merged[k];
                double x1 = Math.Min(m.X, d.X), y1 = Math.Min(m.Y, d.Y);
                double x2 = Math.Max(m.X + m.W, d.X + d.W), y2 = Math.Max(m.Y + m.H, d.Y + d.H);
                merged[k] = m with { X = x1, Y = y1, W = x2 - x1, H = y2 - y1 };
            }
            return merged;
        }

        /// <summary>
        /// 타일 검출 박스가 그 타일의 안쪽 경계(프레임 가장자리가 아닌 변)에 닿았는지 — 닿았으면 물체가 타일에서 잘렸을 수 있다.
        /// (ox, oy, tw, th) 는 타일, margin 은 경계에서 이만큼(px) 안이면 닿은 것으로 본다
        /// </summary>
        public static bool TouchesInnerEdge(Det d, int ox, int oy, int tw, int th, int frameW, int frameH, double margin = 2)
        {
            bool left = ox > 0 && d.X <= ox + margin;
            bool top = oy > 0 && d.Y <= oy + margin;
            bool right = ox + tw < frameW && d.X + d.W >= ox + tw - margin;
            bool bottom = oy + th < frameH && d.Y + d.H >= oy + th - margin;
            return left || top || right || bottom;
        }

        /// <summary>
        /// 경계 조각만 합친다: 묶음은 <see cref="MergeNms"/> 와 같고, 묶이는 두 박스 중 하나라도 타일 경계에 잘린 박스(Cut)면
        /// 대표를 합집합으로 넓히고, 둘 다 온전하면 지금처럼 대표만 남긴다. 합집합을 모든 묶음에 쓰면(<see cref="MergeNmm"/>)
        /// 타일마다 몇 px 씩 다른 박스가 겹쳐 대부분의 박스가 부풀고, 신뢰도 낮은 큰 박스가 옆 차들을 한 박스로 묶었다
        /// </summary>
        public static List<Det> MergeCutPieces(List<(Det D, bool Cut)> dets, double iouThreshold, double iosThreshold)
        {
            var reps = new List<(Det D, bool Cut)>();   // 묶음 판정용 대표 (원래 박스 그대로)
            var merged = new List<Det>();               // 내보낼 박스, reps 와 같은 순서
            foreach (var (d, cut) in dets.OrderByDescending(x => x.D.Conf))
            {
                int k = reps.FindIndex(r => r.D.Label == d.Label && (IoU(d, r.D) > iouThreshold || IoS(d, r.D) > iosThreshold));
                if (k < 0) { reps.Add((d, cut)); merged.Add(d); continue; }
                if (!cut && !reps[k].Cut) continue;   // 둘 다 온전한 박스 — 지운다 (MergeNms 와 같음)
                var m = merged[k];
                double x1 = Math.Min(m.X, d.X), y1 = Math.Min(m.Y, d.Y);
                double x2 = Math.Max(m.X + m.W, d.X + d.W), y2 = Math.Max(m.Y + m.H, d.Y + d.H);
                merged[k] = m with { X = x1, Y = y1, W = x2 - x1, H = y2 - y1 };
            }
            return merged;
        }

        private static double Inter(Det a, Det b)
        {
            double x1 = Math.Max(a.X, b.X), y1 = Math.Max(a.Y, b.Y);
            double x2 = Math.Min(a.X + a.W, b.X + b.W), y2 = Math.Min(a.Y + a.H, b.Y + b.H);
            return Math.Max(0, x2 - x1) * Math.Max(0, y2 - y1);
        }

        private static double IoU(Det a, Det b)
        {
            double inter = Inter(a, b);
            if (inter <= 0) return 0;
            double uni = a.W * a.H + b.W * b.H - inter;
            return uni > 0 ? inter / uni : 0;
        }

        // Intersection over Smaller: 작은 박스가 큰 박스에 얼마나 포함됐는지 (포함 관계 중복 탐지)
        private static double IoS(Det a, Det b)
        {
            double inter = Inter(a, b);
            if (inter <= 0) return 0;
            double smaller = Math.Min(a.W * a.H, b.W * b.H);
            return smaller > 0 ? inter / smaller : 0;
        }
    }
}
