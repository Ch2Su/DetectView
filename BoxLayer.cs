using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Media;

namespace YOLO11_Test
{
    /// <summary>
    /// 검출 박스 전체를 <b>단일 비주얼의 OnRender 한 번</b>으로 그린다 — HUD 오버레이와 앱 안 영상(파일·카메라·SAHI)이 같이 쓴다.
    /// (박스마다 Rectangle/TextBlock 요소를 만들면 투명 창에선 레이아웃+소프트웨어 래스터화 비용이 커져 표시가 눈에 띄게 밀린다)
    /// 모양: 클래스 색 모서리 꺾쇠(어두운 둘레) + 꺾쇠를 잇는 옅은 1px 테두리 + 상자 위 고정폭 라벨 "CAR 0.95"
    /// (추적기를 거친 박스는 "CAR #12 0.95", 멈춘 물체는 "CAR #12 정지 0.95"). 화면에서 작은 물체의 라벨과 꺾쇠는 크기에 비례해 줄인다(Box.FullSizeFrom),
    /// 이 요소의 왼쪽 위에 클래스별 개수 범례 (앱 안에선 영상 칸 모서리, HUD 에선 모니터 모서리).
    /// 색·글꼴은 App.xaml 의 Box.* 키 — 처음 그릴 때 한 번 읽는다.
    /// 예전엔 파일 재생(YoloDotNet 기본 그리기)·SAHI(OpenCV)·HUD(이 클래스)가 박스를 제각각 그렸고, 앞의 둘은 영상에 구워져
    /// 창 크기에 따라 선과 글자가 같이 커졌다 줄었다
    /// </summary>
    internal sealed class BoxLayer : FrameworkElement
    {
        private const string DefaultLegendTitle = "검출";
        // 범례 줄 순서: 도로 CCTV 에서 흔한 것부터 (App.xaml 의 Box.* 순서와 같게), 목록에 없는 라벨은 그 뒤 이름순.
        // 개수순으로 줄을 세우면 재생 중 개수가 엇갈릴 때마다 줄이 자리를 바꿔 읽기 어렵다
        private static readonly string[] LegendOrder = ["car", "truck", "bus", "motorcycle", "scooter", "bicycle", "person"];
        private const double LegendMinWidth = 128;   // DIP — 개수 자릿수가 바뀌어도 범례 폭이 흔들리지 않게

        private IReadOnlyList<Det> _dets = Array.Empty<Det>();
        // HUD: 좌표 × (_sx, _sy) (Update). 앱 안 영상: 프레임 _fitW×_fitH 가 이 요소 안에 Uniform 으로 놓였다고 보고 그린다 (Show, _fitW > 0)
        private double _sx = 1, _sy = 1;
        private double _fitW, _fitH;
        // 범례: 클래스별 개수(그릴 순서)와 머리 글자. 박스를 바꿀 때 한 번 센다 — 박스에서만 나오므로 LooksSame 이 박스와 함께 거른다
        private (string Label, int Count)[] _legend = [];
        private string _legendTitle = DefaultLegendTitle;

        public BoxLayer()
        {
            IsHitTestVisible = false;
        }

        /// <summary>지금 그려진(다음 렌더에 그려질) 박스 수</summary>
        public int Count => _dets.Count;

        /// <summary>범례를 이 요소의 왼쪽 위 모서리에서 띄우는 거리 (DIP)</summary>
        public double LegendMargin { get; set; } = 8;

        /// <summary>
        /// 범례 글자 "CAR 7 · TRUCK 1" (그리는 줄 순서, 박스가 없으면 빈 문자열). 범례는 그림이라 UIA 로 안 보여서
        /// 메인 창이 이걸 검출 수 칸의 UIA 속성으로 내보낸다 — UI 테스트가 '검출 N'·오버레이 박스 수와 합을 맞춰 본다
        /// </summary>
        public string LegendText { get; private set; } = "";

        /// <summary>LegendText 가 바뀌었다 (UI 스레드)</summary>
        public event Action<string>? LegendTextChanged;

        /// <summary>
        /// 라벨에 붙은 추적 번호 "#12 정지 · #15 · #21" (박스 순서 그대로, 추적하지 않은 박스는 건너뛰고 없으면 빈 문자열).
        /// 범례처럼 그림이라 UIA 로 안 보여서 메인 창이 검출 수 칸의 UIA 속성으로 내보낸다 — UI 테스트가 번호가 박스마다 붙었는지,
        /// 이어지는지, 멈춘 물체가 '정지'로 나오는지 본다
        /// </summary>
        public string TrackText { get; private set; } = "";

        /// <summary>TrackText 가 바뀌었다 (UI 스레드)</summary>
        public event Action<string>? TrackTextChanged;

        /// <summary>HUD: 좌표(캡처 픽셀) × 배율 = 이 요소의 DIP</summary>
        public void Update(IReadOnlyList<Det> dets, double scaleX, double scaleY)
        {
            // 보이는 결과가 같으면 다시 그리지 않는다. 오버레이는 캡처에서 제외되지만, 다시 그릴 때마다
            // DWM 이 화면을 갱신해 DXGI 캡처가 "새 화면"을 받게 되고 → 재추론 → 다시 그리기가
            // 끝없이 반복된다 (정지 화면에서도 GPU 가 쉬지 못함). 범례도 박스에서만 나오므로 같이 걸러진다
            if (_fitW == 0 && scaleX == _sx && scaleY == _sy && _legendTitle == DefaultLegendTitle && LooksSame(dets, scaleX, scaleY)) return;
            _fitW = _fitH = 0;
            Set(dets, scaleX, scaleY, DefaultLegendTitle);
        }

        /// <summary>
        /// 앱 안 영상: 프레임(frameW×frameH 픽셀)이 이 요소 안에 Stretch=Uniform 으로 가운데 놓였다고 보고 그린다
        /// (같은 칸의 Image 와 같은 자리). 창 크기가 바뀌면 다시 맞춘다. legendTitle: 범례 머리 글자 (null = "검출")
        /// </summary>
        public void Show(IReadOnlyList<Det> dets, double frameW, double frameH, string? legendTitle = null)
        {
            string title = legendTitle ?? DefaultLegendTitle;
            double s = FitScale(frameW, frameH);
            if (frameW == _fitW && frameH == _fitH && title == _legendTitle && LooksSame(dets, s, s)) return;
            _fitW = frameW;
            _fitH = frameH;
            Set(dets, 1, 1, title);
        }

        public void Clear()
        {
            if (_dets.Count == 0) return;
            _dets = Array.Empty<Det>();
            _legend = [];
            SetLegendText("");
            SetTrackText("");
            InvalidateVisual();
        }

        private void Set(IReadOnlyList<Det> dets, double sx, double sy, string legendTitle)
        {
            _dets = dets;
            _sx = sx;
            _sy = sy;
            _legendTitle = legendTitle;
            _legend = CountByLabel(dets);
            SetLegendText(_legend.Length == 0 ? ""
                : string.Join(" · ", Array.ConvertAll(_legend, l => $"{l.Label.ToUpperInvariant()} {l.Count}")));
            SetTrackText(BuildTrackText(dets));
            InvalidateVisual();
        }

        private void SetLegendText(string text)
        {
            if (text == LegendText) return;
            LegendText = text;
            LegendTextChanged?.Invoke(text);
        }

        private void SetTrackText(string text)
        {
            if (text == TrackText) return;
            TrackText = text;
            TrackTextChanged?.Invoke(text);
        }

        private static string BuildTrackText(IReadOnlyList<Det> dets)
        {
            StringBuilder? sb = null;
            foreach (var d in dets)
            {
                if (d.TrackId <= 0) continue;
                sb ??= new StringBuilder();
                sb.Append(sb.Length > 0 ? " · #" : "#").Append(d.TrackId);
                if (d.Stationary) sb.Append(" 정지");
            }
            return sb?.ToString() ?? "";
        }

        // 라벨 글자: "CAR 0.95" — 추적기를 거친 박스는 번호를, 멈춘 물체는 '정지'를 클래스 이름 뒤에 붙인다 (TrackText 의 한 항목과 같은 꼴)
        private static string LabelText(Det d)
        {
            string name = d.Label.ToUpperInvariant();
            if (d.TrackId <= 0) return $"{name} {d.Conf:0.00}";
            return d.Stationary ? $"{name} #{d.TrackId} 정지 {d.Conf:0.00}" : $"{name} #{d.TrackId} {d.Conf:0.00}";
        }

        private static (string Label, int Count)[] CountByLabel(IReadOnlyList<Det> dets)
        {
            if (dets.Count == 0) return [];
            var counts = new Dictionary<string, int>();
            foreach (var d in dets)
                counts[d.Label] = counts.GetValueOrDefault(d.Label) + 1;
            var list = new (string Label, int Count)[counts.Count];
            int i = 0;
            foreach (var kv in counts) list[i++] = (kv.Key, kv.Value);
            Array.Sort(list, (a, b) =>
            {
                int c = LegendRank(a.Label).CompareTo(LegendRank(b.Label));
                return c != 0 ? c : string.CompareOrdinal(a.Label, b.Label);
            });
            return list;
        }

        private static int LegendRank(string label)
        {
            int i = Array.IndexOf(LegendOrder, label);
            return i < 0 ? LegendOrder.Length : i;
        }

        private double FitScale(double fw, double fh) =>
            fw > 0 && fh > 0 && ActualWidth > 0 && ActualHeight > 0 ? Math.Min(ActualWidth / fw, ActualHeight / fh) : 1;

        // 화면상 0.5px 미만의 차이와 표시 자릿수(소수 둘째 자리) 밖의 신뢰도 차이는 같은 것으로 본다.
        // i번째끼리 비교하므로 같은 박스라도 순서가 바뀌면 다시 그린다 (결정적 추론이라 같은 화면이면 같은 순서)
        private bool LooksSame(IReadOnlyList<Det> dets, double sx, double sy)
        {
            if (dets.Count != _dets.Count) return false;
            for (int i = 0; i < dets.Count; i++)
            {
                Det a = dets[i], b = _dets[i];
                if (a.Label != b.Label || a.TrackId != b.TrackId || a.Stationary != b.Stationary
                    || Math.Abs(a.X - b.X) * sx >= 0.5 || Math.Abs(a.Y - b.Y) * sy >= 0.5
                    || Math.Abs(a.W - b.W) * sx >= 0.5 || Math.Abs(a.H - b.H) * sy >= 0.5
                    || Math.Round(a.Conf, 2) != Math.Round(b.Conf, 2))
                    return false;
            }
            return true;
        }

        protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
        {
            base.OnDpiChanged(oldDpi, newDpi);
            InvalidateVisual();   // 선 굵기를 장치 픽셀에 맞춰 다시 잡는다
        }

        protected override void OnRender(DrawingContext dc)
        {
            var dets = _dets;
            if (dets.Count == 0) return;

            double ppd = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            var theme = Theme.Get(ppd);
            double sx = _sx, sy = _sy, ox = 0, oy = 0;
            if (_fitW > 0)
            {
                double s = FitScale(_fitW, _fitH);
                sx = sy = s;
                ox = (ActualWidth - _fitW * s) / 2;
                oy = (ActualHeight - _fitH * s) / 2;
            }

            // 상자를 모두 그린 뒤 라벨을 그린다 — 이웃 상자의 꺾쇠·테두리가 라벨 글자를 가로지르지 않게
            for (int i = 0; i < dets.Count; i++)
            {
                var d = dets[i];
                var paint = theme.For(d.Label);
                var (x0, y0, x1, y1) = BoxEdges(d, sx, sy, ox, oy, ppd);

                // 꺾쇠를 잇는 옅은 테두리 (상자 바깥 줄 픽셀)
                double f = theme.FrameThickness;
                dc.DrawRectangle(null, paint.Frame, new Rect(x0 + f / 2, y0 + f / 2, x1 - x0 - f, y1 - y0 - f));

                // 모서리 꺾쇠 — 어두운 둘레를 먼저 깔아 밝은 배경(흰 차·하늘)에서도 보이게.
                // 작은 물체는 꺾쇠도 라벨과 같은 배율로 줄인다(굵기와 팔 길이의 하한) — 예전엔 굵기 2px(둘레까지 4px)·팔 최소 5 DIP 가 늘 같아서
                // 멀리 있는 작은 차는 굵은 꺾쇠에 덮여 물체가 안 보였다. 팔 길이는 원래 상자 짧은 변의 1/3 이라 크기를 따르고, 하한만 배율을 따른다
                double k = theme.SizeScale(x1 - x0, y1 - y0);
                var (cornerPen, haloPen, c) = theme.CornerPens(d.Label, k);
                double arm = Snap(Math.Clamp(Math.Min(x1 - x0, y1 - y0) / 3, 5 * k, 14), ppd);
                var corners = Corners(x0 + c / 2, y0 + c / 2, x1 - c / 2, y1 - c / 2, arm);
                dc.DrawGeometry(null, haloPen, corners);
                dc.DrawGeometry(null, cornerPen, corners);
            }

            double bar = Snap(3, ppd);   // 범례 막대 (라벨 막대는 라벨 배율을 따른다)
            for (int i = 0; i < dets.Count; i++)
            {
                var d = dets[i];
                var paint = theme.For(d.Label);
                var (x0, y0, x1, y1) = BoxEdges(d, sx, sy, ox, oy, ppd);

                // 라벨: 상자 위 (위에 자리가 없으면 아래, 그것도 없으면 상자 안 위쪽).
                // 화면에서 작은 물체는 라벨(글자·여백·막대)도 크기에 비례해 줄인다 — 멀리 있는 작은 차에 기본 크기 라벨이 붙으면
                // 라벨이 물체보다 몇 배 커서 이웃 라벨과 겹치고 영상을 가렸다(test4 신호대기 행렬). 큰 물체는 지금 크기 그대로
                double k = theme.SizeScale(x1 - x0, y1 - y0);
                double lbar = Math.Max(1 / ppd, Snap(3 * k, ppd)), pad = Snap(5 * k, ppd), gap = Snap(3 * k, ppd);
                var text = new FormattedText(
                    LabelText(d), CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    theme.Face, theme.FontSize * k, paint.Brush, null, TextFormattingMode.Display, ppd);
                double lw = Snap(lbar + pad + text.WidthIncludingTrailingWhitespace + pad, ppd);
                double lh = Snap(text.Height + 2 * k, ppd);
                double ly = y0 - lh - gap;
                if (ly < 0) ly = y1 + gap + lh <= ActualHeight ? y1 + gap : Math.Max(0, y0 + gap);
                dc.DrawRectangle(theme.LabelBack, null, new Rect(x0, ly, lw, lh));
                dc.DrawRectangle(paint.Brush, null, new Rect(x0, ly, lbar, lh));
                dc.DrawText(text, new Point(x0 + lbar + pad, ly + Snap((lh - text.Height) / 2, ppd)));
            }

            DrawLegend(dc, theme, ppd, bar);
        }

        // 범례: 이 요소의 왼쪽 위 모서리 — 앱 안에선 영상 칸 모서리(레터박스가 있으면 그 검은 띠 위라 영상을 안 가린다), HUD 에선 모니터 모서리.
        // 박스·라벨 위에 그린다. 머리 줄 "검출  N" 아래로 클래스마다 한 줄: 클래스 색 막대 · 클래스 색 이름 · 개수(오른쪽 맞춤).
        // 박스와 같은 렌더 패스라 영상·박스와 늘 같은 프레임이다
        private void DrawLegend(DrawingContext dc, Theme theme, double ppd, double bar)
        {
            var legend = _legend;
            if (legend.Length == 0) return;

            double m = Snap(LegendMargin, ppd), padL = Snap(6, ppd), padR = Snap(8, ppd), padY = Snap(4, ppd), gap = Snap(18, ppd);
            var title = theme.Text(_legendTitle, theme.LegendSubBrush);
            var total = theme.Text(_dets.Count.ToString(CultureInfo.InvariantCulture), theme.LegendTextBrush);
            var names = new FormattedText[legend.Length];
            var counts = new FormattedText[legend.Length];
            double w = bar + padL + title.Width + gap + total.Width + padR;
            for (int i = 0; i < legend.Length; i++)
            {
                names[i] = theme.Text(legend[i].Label.ToUpperInvariant(), theme.For(legend[i].Label).Brush);
                counts[i] = theme.Text(legend[i].Count.ToString(CultureInfo.InvariantCulture), theme.LegendTextBrush);
                w = Math.Max(w, bar + padL + names[i].Width + gap + counts[i].Width + padR);
            }
            w = Snap(Math.Max(w, LegendMinWidth), ppd);
            double rowH = Snap(title.Height + 4, ppd);

            dc.DrawRectangle(theme.LegendBack, null, new Rect(m, m, w, padY + rowH * (legend.Length + 1) + padY));
            double y = m + padY;
            DrawRow(title, total);
            for (int i = 0; i < legend.Length; i++)
            {
                dc.DrawRectangle(theme.For(legend[i].Label).Brush, null, new Rect(m, y, bar, rowH));
                DrawRow(names[i], counts[i]);
            }

            void DrawRow(FormattedText left, FormattedText right)
            {
                double ty = y + Snap((rowH - left.Height) / 2, ppd);
                dc.DrawText(left, new Point(m + bar + padL, ty));
                dc.DrawText(right, new Point(Snap(m + w - padR - right.Width, ppd), ty));
                y += rowH;
            }
        }

        // 상자 가장자리 (DIP) — 장치 픽셀에 맞춘다: 1·2px 선이 이웃 픽셀로 번져 흐려지지 않게
        private static (double X0, double Y0, double X1, double Y1) BoxEdges(Det d, double sx, double sy, double ox, double oy, double ppd)
        {
            double x0 = Snap(ox + d.X * sx, ppd), y0 = Snap(oy + d.Y * sy, ppd);
            double x1 = Math.Max(x0 + 4 / ppd, Snap(ox + (d.X + d.W) * sx, ppd));
            double y1 = Math.Max(y0 + 4 / ppd, Snap(oy + (d.Y + d.H) * sy, ppd));
            return (x0, y0, x1, y1);
        }

        private static double Snap(double dip, double ppd) => Math.Round(dip * ppd) / ppd;

        // 네 모서리의 ㄱ자 꺾쇠 (선 중심 좌표 — 펜 굵기의 절반만큼 안쪽)
        private static StreamGeometry Corners(double l, double t, double r, double b, double arm)
        {
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                Ell(ctx, l, t + arm, l, t, l + arm, t);
                Ell(ctx, r - arm, t, r, t, r, t + arm);
                Ell(ctx, r, b - arm, r, b, r - arm, b);
                Ell(ctx, l + arm, b, l, b, l, b - arm);
            }
            g.Freeze();
            return g;
        }

        private static void Ell(StreamGeometryContext ctx, double ax, double ay, double bx, double by, double cx, double cy)
        {
            ctx.BeginFigure(new Point(ax, ay), false, false);
            ctx.LineTo(new Point(bx, by), true, false);
            ctx.LineTo(new Point(cx, cy), true, false);
        }

        /// <summary>클래스 하나의 붓 — 라벨 글자·막대, 옅은 테두리, 꺾쇠 색 (꺾쇠 굵기는 물체 크기를 따라 Theme.CornerPens 가 정한다)</summary>
        private sealed class Paint(Brush brush, Pen frame, Color corner)
        {
            public readonly Brush Brush = brush;
            public readonly Pen Frame = frame;
            public readonly Color Corner = corner;
            // 꺾쇠 붓 — 굵기(장치 픽셀)마다 처음 쓸 때 만들어 둔다 (화면 배율 하나에 1~2 가지)
            public readonly Dictionary<int, Pen> CornerPens = new();
        }

        /// <summary>
        /// App.xaml 의 Box.* 를 읽어 얼려 둔 붓들. 오버레이와 메인 창 모두 UI 스레드에서 그리므로 잠금 없이 하나를 같이 쓴다.
        /// 선 굵기는 장치 픽셀 단위로 정해 화면 배율이 바뀌면(모니터 이동) 다시 만든다
        /// </summary>
        private sealed class Theme
        {
            private static Theme? s_current;

            public readonly Typeface Face;
            public readonly double FontSize;
            public readonly Brush LabelBack, LegendBack;
            public readonly Brush LegendTextBrush, LegendSubBrush;   // 범례 개수 · 머리 글자
            public readonly double FrameThickness;   // DIP (장치 픽셀 정수배)
            private readonly double _ppd, _frameOpacity, _cornerOpacity, _fullSizeFrom, _minScale;
            private readonly int _cornerPx;   // 큰 물체의 꺾쇠 굵기 — 장치 픽셀 (100% 2px, 125% 3px)
            private readonly Color _other, _halo;
            private readonly Dictionary<string, Paint> _paints = new();
            private readonly Dictionary<int, Pen> _halos = new();   // 꺾쇠 둘레 붓 — 꺾쇠 굵기(장치 픽셀)마다
            // 범례 글자 (클래스 이름·개수·머리) — 매 프레임 같은 글자가 대부분이라 만들어 둔 것을 쓴다. 상자 라벨은 신뢰도가 매번 달라 두지 않는다
            private readonly Dictionary<(string, Brush), FormattedText> _texts = new();

            private Theme(double ppd)
            {
                _ppd = ppd;
                var family = Res("Box.Font", new FontFamily("Cascadia Mono, Consolas, Malgun Gothic"));
                Face = new Typeface(family, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
                FontSize = Res("Box.FontSize", 11.0);
                LabelBack = Frozen(new SolidColorBrush(Res("Box.LabelBack", Color.FromArgb(0xB8, 0x0B, 0x11, 0x18))));
                LegendBack = Frozen(new SolidColorBrush(Res("Box.LegendBack", Color.FromArgb(0xDB, 0x0B, 0x11, 0x18))));
                LegendTextBrush = Frozen(new SolidColorBrush(Res("Box.LegendText", Color.FromRgb(0xE2, 0xE8, 0xF0))));
                LegendSubBrush = Frozen(new SolidColorBrush(Res("Box.LegendSubText", Color.FromRgb(0x9A, 0xA8, 0xBA))));
                _frameOpacity = Res("Box.FrameOpacity", 0.3);
                _cornerOpacity = Res("Box.CornerOpacity", 0.85);
                _fullSizeFrom = Res("Box.FullSizeFrom", 48.0);
                _minScale = Math.Clamp(Res("Box.MinScale", 0.6), 0.1, 1);   // Clamp(값, 하한, 1) 이 하한 > 1 로 던지지 않게
                _other = Res("Box.Other", Colors.White);
                _halo = Res("Box.Halo", Color.FromArgb(0x59, 0, 0, 0));
                // 2px·1px 를 화면 배율에 맞춰 장치 픽셀 정수로 (125% 면 꺾쇠 3px) — 반 픽셀이면 흐려진다
                _cornerPx = (int)Math.Max(2, Math.Round(2 * ppd, MidpointRounding.AwayFromZero));
                FrameThickness = Math.Max(1, Math.Round(ppd, MidpointRounding.AwayFromZero)) / ppd;
            }

            /// <summary>
            /// 작은 물체의 라벨·꺾쇠 배율: 화면에서 물체 크기 √(폭×높이)(DIP)가 Box.FullSizeFrom 이상이면 1(기본 크기), 그보다 작으면 비례해 줄이되
            /// Box.MinScale 아래로는 안 줄인다. 폭·높이 중 하나가 아니라 넓이로 재는 이유: 서 있는 사람(좁고 높음)·버스(넓고 낮음)처럼
            /// 한 변만 작은 물체를 작은 물체로 치지 않으려고
            /// </summary>
            public double SizeScale(double w, double h) =>
                _fullSizeFrom > 0 ? Math.Clamp(Math.Sqrt(Math.Max(0, w * h)) / _fullSizeFrom, _minScale, 1) : 1;

            /// <summary>
            /// 배율 k 인 물체의 꺾쇠 붓·둘레 붓·꺾쇠 굵기(DIP). 굵기는 큰 물체의 굵기(장치 픽셀)에 k 를 곱해 장치 픽셀 정수로 반올림하고
            /// 1px 아래로는 안 줄인다 — 반 픽셀이면 흐려진다 (100% 화면: k 0.75 이상 2px, 그 아래 1px). 둘레는 양쪽으로 1px 씩 더 굵다
            /// </summary>
            public (Pen Corner, Pen Halo, double Thickness) CornerPens(string label, double k)
            {
                int px = Math.Max(1, (int)Math.Round(_cornerPx * k, MidpointRounding.AwayFromZero));
                var paint = For(label);
                if (!paint.CornerPens.TryGetValue(px, out var corner))
                    paint.CornerPens[px] = corner = MakePen(paint.Corner, px / _ppd);
                if (!_halos.TryGetValue(px, out var halo))
                    _halos[px] = halo = MakePen(_halo, (px + 2) / _ppd);
                return (corner, halo, px / _ppd);
            }

            public static Theme Get(double ppd)
            {
                if (s_current == null || s_current._ppd != ppd) s_current = new Theme(ppd);
                return s_current;
            }

            public Paint For(string label)
            {
                if (_paints.TryGetValue(label, out var p)) return p;
                var c = Application.Current?.TryFindResource("Box." + label) is Color color ? color : _other;
                // 라벨 글자·막대는 클래스 색 그대로(읽혀야 해서), 테두리·꺾쇠는 영상이 비쳐 보이게 옅게
                var faint = Color.FromArgb((byte)Math.Round(c.A * _frameOpacity), c.R, c.G, c.B);
                var corner = Color.FromArgb((byte)Math.Round(c.A * _cornerOpacity), c.R, c.G, c.B);
                p = new Paint(Frozen(new SolidColorBrush(c)), MakePen(faint, FrameThickness), corner);
                _paints[label] = p;
                return p;
            }

            public FormattedText Text(string s, Brush brush)
            {
                if (_texts.TryGetValue((s, brush), out var t)) return t;
                if (_texts.Count >= 512) _texts.Clear();   // 개수 글자가 끝없이 쌓이지 않게 (보통은 수십 개)
                t = new FormattedText(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    Face, FontSize, brush, null, TextFormattingMode.Display, _ppd);
                _texts[(s, brush)] = t;
                return t;
            }

            private static T Res<T>(string key, T fallback) => Application.Current?.TryFindResource(key) is T v ? v : fallback;

            private static Brush Frozen(Brush b)
            {
                b.Freeze();
                return b;
            }

            private static Pen MakePen(Color c, double thickness)
            {
                // 꺾쇠의 꺾인 곳은 직각, 끝은 잘린 그대로
                var pen = new Pen(Frozen(new SolidColorBrush(c)), thickness)
                {
                    LineJoin = PenLineJoin.Miter,
                    StartLineCap = PenLineCap.Flat,
                    EndLineCap = PenLineCap.Flat
                };
                pen.Freeze();
                return pen;
            }
        }
    }
}
