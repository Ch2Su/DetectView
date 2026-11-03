using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Input;
using System.Windows.Media;

namespace YOLO11_Test
{
    /// <summary>구역 유형별 색 — App.xaml 의 Roi.* 키 (처음 쓸 때 한 번 읽는다). 목록 항목과 RoiLayer 가 같이 쓴다</summary>
    internal static class RoiTheme
    {
        private sealed record Paint(Color Color, Brush Solid, Brush Fill, Pen Pen, Pen PenSelected);

        private static readonly Dictionary<RoiKind, Paint> Cache = [];

        public static Color ColorOf(RoiKind k) => For(k).Color;
        public static Brush SolidOf(RoiKind k) => For(k).Solid;
        public static Brush FillOf(RoiKind k) => For(k).Fill;
        public static Pen PenOf(RoiKind k, bool selected) => selected ? For(k).PenSelected : For(k).Pen;

        private static Paint For(RoiKind k)
        {
            if (Cache.TryGetValue(k, out var p)) return p;
            Color c = Res("Roi." + RoiKinds.Key(k), Color.FromRgb(0x6C, 0xB2, 0xFF));
            double fillOpacity = Res("Roi.FillOpacity", 0.16);
            var solid = Frozen(new SolidColorBrush(c));
            var fill = Frozen(new SolidColorBrush(c) { Opacity = fillOpacity });
            // 무시 구역은 점선 — 규칙에서 빼는 자리라 다른 구역과 한눈에 갈린다
            var dash = k == RoiKind.Ignore ? new DashStyle([5, 4], 0) : DashStyles.Solid;
            p = new Paint(c, solid, fill, MakePen(solid, 2, dash), MakePen(solid, 3, dash));
            Cache[k] = p;
            return p;
        }

        private static Pen MakePen(Brush b, double thickness, DashStyle dash)
        {
            var pen = new Pen(b, thickness) { LineJoin = PenLineJoin.Round, DashStyle = dash };
            pen.Freeze();
            return pen;
        }

        private static Brush Frozen(Brush b) { b.Freeze(); return b; }

        public static T Res<T>(string key, T fallback) => Application.Current?.TryFindResource(key) is T v ? v : fallback;

        private static readonly Dictionary<string, Brush> BoxBrushes = [];

        /// <summary>검출 클래스 색(App.xaml 의 Box.&lt;라벨&gt;, 목록에 없으면 Box.Other) — 이벤트 로그의 'CAR #40' 글자색</summary>
        public static Brush BoxBrushOf(string label)
        {
            if (BoxBrushes.TryGetValue(label, out var b)) return b;
            var app = Application.Current;
            Color c = app?.TryFindResource("Box." + label) is Color found ? found
                : app?.TryFindResource("Box.Other") is Color other ? other : Colors.White;
            var brush = new SolidColorBrush(c);
            brush.Freeze();
            return BoxBrushes[label] = brush;
        }
    }

    /// <summary>
    /// 영상 위 관심 구역(ROI) 그리기·편집 — 앱 안 영상(파일)의 구역. 프레임(_fw×_fh)이 이 요소 안에 Uniform 으로 놓였다고 보고
    /// 같은 칸의 Image·BoxLayer 와 같은 자리에 겹친다. 구역은 프레임 0~1 좌표라 창 크기·해상도와 무관하다.
    /// 편집은 파일을 멈춘 채일 때만 (<see cref="Editable"/> — 재생 중엔 보이기만 하고 마우스를 통과시킨다):
    /// 새 구역 = 점 클릭(첫 점·Enter 로 닫기, 오른쪽 클릭으로 마지막 점 되돌리기, Esc 취소), 점 끌기로 옮기기,
    /// 선택한 구역의 변 가운데 ○ 끌기로 점 넣기, 점 선택 뒤 Delete 로 점 지우기(3개까지). 바뀌면 <see cref="ZonesChanged"/> — 저장은 MainWindow.Roi.cs.
    /// 한 번 그린 뒤 다시 그리지 않는 정적 그림이라 BoxLayer 처럼 새 화면 고리를 만들 일이 없다 (HUD 에는 아직 연결하지 않았다)
    /// </summary>
    internal sealed class RoiLayer : FrameworkElement
    {
        private const double VertexHit = 9;         // DIP — 점을 집는 반경
        private const double MidHit = 8;            // 변 가운데 ○ 를 집는 반경
        private const double CloseHit = 10;         // 그리는 중 첫 점 근처를 눌러 닫는 반경
        private const double MinEdgeForMid = 28;    // 이보다 짧은 변엔 ○ 를 안 그린다
        private const double Handle = 8;            // 점 손잡이(정사각형) 한 변
        private const double MinArea = 1e-4;        // 이보다 좁은(점이 한 줄로 놓인) 구역은 닫지 않는다

        private RoiSet _model = new();
        private double _fw, _fh;
        private bool _editable;
        private int _selectedId;
        private int _selVertex = -1;
        private List<RoiPoint>? _draft;
        private RoiKind _draftKind;
        private Point _mouse;
        private int _dragVertex = -1;
        private bool _dragChanged;

        public RoiLayer()
        {
            IsHitTestVisible = false;   // 편집 중일 때만 마우스를 받는다 (Editable)
            Focusable = true;           // Esc·Enter·Delete
            FocusVisualStyle = null;
        }

        // UIA 에 영상 칸 영역(AutomationId = x:Name 'ZoneLayer')으로 보이게 한다 — 그리는 요소엔 기본 피어가 없다. UI 테스트(Test-Roi.ps1)가 이 영역으로
        // 화면 위 좌표를 프레임 좌표로 옮긴다 (같은 칸의 Image 는 UIA 영역이 비어 읽을 수 없었다). 그림 자체는 UIA 로 안 보인다
        protected override AutomationPeer OnCreateAutomationPeer() => new RoiLayerPeer(this);

        private sealed class RoiLayerPeer(RoiLayer owner) : FrameworkElementAutomationPeer(owner)
        {
            protected override string GetClassNameCore() => nameof(RoiLayer);
            protected override string GetNameCore() => "구역 편집 영역";
            protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Pane;
        }

        /// <summary>지금 구역들 (바꿀 땐 이 클래스의 메서드로 — 그려 줘야 하고 ZonesChanged 로 저장하게)</summary>
        public RoiSet Model => _model;

        public bool HasFrameSize => _fw > 0 && _fh > 0;

        /// <summary>그리는 중 (새 구역의 점을 찍는 중)</summary>
        public bool Drafting => _draft != null;

        public int SelectedId
        {
            get => _selectedId;
            set
            {
                if (_selectedId == value) return;
                _selectedId = value;
                _selVertex = -1;
                InvalidateVisual();
            }
        }

        public RoiZone? SelectedZone => _selectedId == 0 ? null : _model.Find(_selectedId);

        /// <summary>점·구역을 바꿨다 (끌기가 끝났을 때·새 구역·지우기·유형 바꿈) — 저장할 때</summary>
        public event Action? ZonesChanged;

        /// <summary>사용자가 구역을 골랐다·고른 것이 사라졌다 (그림 위 클릭·새 구역 시작·지우기)</summary>
        public event Action? SelectionChanged;

        /// <summary>새 구역 그리기가 시작·끝났다</summary>
        public event Action? DraftingChanged;

        /// <summary>
        /// 편집할 수 있나. 끄면 그리는 중이던 것과 끌기를 버리고 마우스를 통과시킨다 (재생 중엔 프레임이 계속 바뀌니 편집하지 않는다)
        /// </summary>
        public bool Editable
        {
            get => _editable;
            set
            {
                if (_editable == value) return;
                _editable = value;
                IsHitTestVisible = value;
                if (!value)
                {
                    EndDrag();
                    _selVertex = -1;
                    CancelDraft();
                    Cursor = null;
                }
                InvalidateVisual();
            }
        }

        public void SetFrameSize(double w, double h)
        {
            if (w == _fw && h == _fh) return;
            _fw = w;
            _fh = h;
            InvalidateVisual();
        }

        /// <summary>구역들을 통째로 바꾼다 (파일을 열었을 때·내릴 때) — 선택·그리던 것·끌기는 버린다. 이벤트는 내지 않는다</summary>
        public void SetZones(RoiSet set)
        {
            EndDrag();
            bool wasDrafting = _draft != null;
            _draft = null;
            _model = set;
            _selectedId = 0;
            _selVertex = -1;
            InvalidateVisual();
            if (wasDrafting) DraftingChanged?.Invoke();
        }

        // ---------- 편집 동작 (패널 버튼·키보드) ----------

        /// <summary>새 구역 그리기 시작 — 클릭으로 점을 찍는다. 편집할 수 없으면 false</summary>
        public bool BeginAdd(RoiKind kind)
        {
            if (!_editable || !HasFrameSize) return false;
            EndDrag();
            bool hadSelection = _selectedId != 0;
            _draft = [];
            _draftKind = kind;
            _selectedId = 0;
            _selVertex = -1;
            Cursor = Cursors.Cross;
            Focus();   // Esc·Enter 가 바로 듣게
            InvalidateVisual();
            DraftingChanged?.Invoke();
            if (hadSelection) SelectionChanged?.Invoke();
            return true;
        }

        public void CancelDraft()
        {
            if (_draft == null) return;
            _draft = null;
            Cursor = null;
            InvalidateVisual();
            DraftingChanged?.Invoke();
        }

        /// <summary>고른 구역을 지운다. 그리는 중이거나 고른 것이 없으면 false</summary>
        public bool DeleteSelectedZone()
        {
            var z = SelectedZone;
            if (z == null || _draft != null || !_editable) return false;
            _model.Remove(z.Id);
            _selectedId = 0;
            _selVertex = -1;
            InvalidateVisual();
            ZonesChanged?.Invoke();
            SelectionChanged?.Invoke();
            return true;
        }

        /// <summary>고른 구역의 유형을 바꾼다</summary>
        public void SetSelectedKind(RoiKind kind)
        {
            var z = SelectedZone;
            if (z == null || z.Kind == kind) return;
            z.Kind = kind;
            InvalidateVisual();
            ZonesChanged?.Invoke();
        }

        /// <summary>고른 점을 지운다 (구역엔 점이 3개 이상 남아야 한다). 지웠으면 true</summary>
        public bool DeleteSelectedVertex()
        {
            var z = SelectedZone;
            if (z == null || _selVertex < 0 || _selVertex >= z.Points.Count || z.Points.Count <= RoiSet.MinPoints || !_editable) return false;
            z.Points.RemoveAt(_selVertex);
            _selVertex = -1;
            InvalidateVisual();
            ZonesChanged?.Invoke();
            return true;
        }

        private void CommitDraft()
        {
            var d = _draft;
            if (d == null || d.Count < RoiSet.MinPoints || RoiGeometry.Area(d) < MinArea) return;   // 아직 닫을 수 없다 — 계속 그린다
            var z = _model.Add(_draftKind, d);
            _draft = null;
            _selectedId = z.Id;
            _selVertex = -1;
            Cursor = null;
            InvalidateVisual();
            DraftingChanged?.Invoke();
            ZonesChanged?.Invoke();
            SelectionChanged?.Invoke();
        }

        // ---------- 좌표 ----------

        private readonly record struct FrameRect(double X, double Y, double W, double H)
        {
            public Point ToDip(RoiPoint p) => new(X + p.X * W, Y + p.Y * H);
            public (double X, double Y) Raw(Point d) => ((d.X - X) / W, (d.Y - Y) / H);
            public RoiPoint ToNorm(Point d)
            {
                var (x, y) = Raw(d);
                return new RoiPoint(RoiGeometry.Clamp01(x), RoiGeometry.Clamp01(y));
            }
        }

        // 프레임이 이 요소 안에 Uniform 으로 가운데 놓인 사각형 (같은 칸의 Image 와 같은 자리)
        private bool TryFit(out FrameRect f)
        {
            f = default;
            if (_fw <= 0 || _fh <= 0 || ActualWidth <= 0 || ActualHeight <= 0) return false;
            double s = Math.Min(ActualWidth / _fw, ActualHeight / _fh);
            double w = _fw * s, h = _fh * s;
            f = new FrameRect((ActualWidth - w) / 2, (ActualHeight - h) / 2, w, h);
            return true;
        }

        private static List<(double X, double Y)> DipPoints(RoiZone z, FrameRect f)
        {
            var list = new List<(double X, double Y)>(z.Points.Count);
            foreach (var p in z.Points)
            {
                var d = f.ToDip(p);
                list.Add((d.X, d.Y));
            }
            return list;
        }

        private static double Dist(Point a, Point b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

        // ---------- 마우스·키보드 ----------

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);
            if (!_editable || !TryFit(out var f)) return;
            e.Handled = true;
            Focus();
            var p = e.GetPosition(this);

            if (_draft != null)
            {
                if (_draft.Count >= RoiSet.MinPoints && Dist(f.ToDip(_draft[0]), p) <= CloseHit)
                {
                    CommitDraft();
                    return;
                }
                _draft.Add(f.ToNorm(p));
                _mouse = p;
                InvalidateVisual();
                return;
            }

            var sel = SelectedZone;
            if (sel != null)
            {
                var pts = DipPoints(sel, f);
                int v = RoiGeometry.NearestVertex(pts, p.X, p.Y, VertexHit);
                if (v < 0)
                {
                    int edge = RoiGeometry.NearestEdgeMidpoint(pts, p.X, p.Y, MidHit, MinEdgeForMid);
                    if (edge >= 0)
                    {
                        // 변 가운데 ○ 를 집으면 그 자리에 점을 넣고 바로 끈다
                        var a = sel.Points[edge];
                        var b = sel.Points[(edge + 1) % sel.Points.Count];
                        v = edge + 1;
                        sel.Points.Insert(v, new RoiPoint((a.X + b.X) / 2, (a.Y + b.Y) / 2));
                        _dragChanged = true;
                    }
                }
                if (v >= 0)
                {
                    _selVertex = v;
                    _dragVertex = v;
                    CaptureMouse();
                    InvalidateVisual();
                    return;
                }
            }

            var (rx, ry) = f.Raw(p);
            int hitId = _model.ZoneAt(rx, ry)?.Id ?? 0;
            if (hitId != _selectedId)
            {
                _selectedId = hitId;
                _selVertex = -1;
                InvalidateVisual();
                SelectionChanged?.Invoke();
            }
            else if (_selVertex >= 0)
            {
                _selVertex = -1;
                InvalidateVisual();
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!_editable || !TryFit(out var f)) return;
            var p = e.GetPosition(this);

            if (_draft != null)
            {
                _mouse = p;
                InvalidateVisual();   // 마지막 점에서 마우스까지의 미리보기 선
                return;
            }

            var sel = SelectedZone;
            if (_dragVertex >= 0 && IsMouseCaptured && sel != null && _dragVertex < sel.Points.Count)
            {
                var np = f.ToNorm(p);
                if (sel.Points[_dragVertex] != np)
                {
                    sel.Points[_dragVertex] = np;
                    _dragChanged = true;
                    InvalidateVisual();
                }
                return;
            }

            bool over = false;
            if (sel != null)
            {
                var pts = DipPoints(sel, f);
                over = RoiGeometry.NearestVertex(pts, p.X, p.Y, VertexHit) >= 0
                       || RoiGeometry.NearestEdgeMidpoint(pts, p.X, p.Y, MidHit, MinEdgeForMid) >= 0;
            }
            Cursor = over ? Cursors.SizeAll : null;
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonUp(e);
            EndDrag();
        }

        // 버튼을 누른 채 캡처를 잃어도(Alt+Tab 등) 끌던 자리에서 끝낸다
        protected override void OnLostMouseCapture(MouseEventArgs e)
        {
            base.OnLostMouseCapture(e);
            EndDrag();
        }

        private void EndDrag()
        {
            if (_dragVertex < 0) return;
            bool changed = _dragChanged;
            _dragVertex = -1;       // 아래 ReleaseMouseCapture 가 OnLostMouseCapture 로 다시 들어와도 여기서 끝난다
            _dragChanged = false;
            if (IsMouseCaptured) ReleaseMouseCapture();
            InvalidateVisual();
            if (changed) ZonesChanged?.Invoke();
        }

        protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseRightButtonDown(e);
            if (!_editable || _draft == null) return;
            e.Handled = true;
            if (_draft.Count > 0) _draft.RemoveAt(_draft.Count - 1);
            if (_draft.Count == 0) CancelDraft();   // 되돌릴 점이 없으면 그리기를 그만둔다
            else InvalidateVisual();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (!_editable) return;
            switch (e.Key)
            {
                case Key.Escape when _draft != null:
                    CancelDraft();
                    e.Handled = true;
                    break;
                case Key.Enter when _draft != null:
                    CommitDraft();
                    e.Handled = true;
                    break;
                case Key.Delete or Key.Back when _draft == null:
                    if (DeleteSelectedVertex()) e.Handled = true;
                    break;
            }
        }

        // ---------- 그리기 ----------

        protected override void OnRender(DrawingContext dc)
        {
            // 편집 중엔 빈 곳도 마우스를 받게 투명한 바탕을 깐다 (OnRender 로 그린 것만 히트 테스트가 된다)
            if (_editable) dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
            if (!TryFit(out var f)) return;
            double ppd = VisualTreeHelper.GetDpi(this).PixelsPerDip;

            var sel = SelectedZone;
            foreach (var z in _model.Zones)
                if (z.Id != _selectedId) DrawZone(dc, z, f, false);
            if (sel != null) DrawZone(dc, sel, f, true);   // 고른 구역이 맨 위
            foreach (var z in _model.Zones) DrawLabel(dc, z, f, ppd);

            if (sel != null && _editable && _draft == null) DrawHandles(dc, sel, f);
            if (_draft != null) DrawDraft(dc, f);
            if (_editable) DrawHint(dc, ppd);
        }

        private static StreamGeometry Polygon(IReadOnlyList<Point> pts)
        {
            var g = new StreamGeometry();
            using (var c = g.Open())
            {
                c.BeginFigure(pts[0], true, true);
                c.PolyLineTo(pts.Skip(1).ToList(), true, true);
            }
            g.Freeze();
            return g;
        }

        private static void DrawZone(DrawingContext dc, RoiZone z, FrameRect f, bool selected)
        {
            var pts = z.Points.Select(f.ToDip).ToList();
            dc.DrawGeometry(RoiTheme.FillOf(z.Kind), RoiTheme.PenOf(z.Kind, selected), Polygon(pts));
        }

        private FormattedText Text(string s, Brush brush, double ppd, double maxWidth = 0)
        {
            var face = new Typeface(RoiTheme.Res("Box.Font", new FontFamily("Consolas")), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
            var t = new FormattedText(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face,
                RoiTheme.Res("Box.FontSize", 11.0), brush, null, TextFormattingMode.Display, ppd);
            if (maxWidth > 0)
            {
                t.MaxTextWidth = maxWidth;
                t.MaxLineCount = 1;
                t.Trimming = TextTrimming.CharacterEllipsis;
            }
            return t;
        }

        private void DrawLabel(DrawingContext dc, RoiZone z, FrameRect f, double ppd)
        {
            var c = f.ToDip(RoiGeometry.Centroid(z.Points));
            var text = Text(z.DisplayName, RoiTheme.SolidOf(z.Kind), ppd);
            const double padX = 6, padY = 1;
            double w = text.WidthIncludingTrailingWhitespace + 2 * padX, h = text.Height + 2 * padY;
            double x = Math.Clamp(c.X - w / 2, 0, Math.Max(0, ActualWidth - w));
            double y = Math.Clamp(c.Y - h / 2, 0, Math.Max(0, ActualHeight - h));
            dc.DrawRectangle(Backing, null, new Rect(x, y, w, h));
            dc.DrawText(text, new Point(x + padX, y + padY));
        }

        private static Brush Backing { get; } = MakeBacking();
        private static Brush MakeBacking()
        {
            var b = new SolidColorBrush(RoiTheme.Res("Box.LabelBack", Color.FromArgb(0xB8, 0x0B, 0x11, 0x18)));
            b.Freeze();
            return b;
        }

        private void DrawHandles(DrawingContext dc, RoiZone z, FrameRect f)
        {
            var dark = new Pen(Backing, 1.5);
            var accent = RoiTheme.Res("AccentBrush", Brushes.White);
            var pts = z.Points.Select(f.ToDip).ToList();

            // 변 가운데 ○ — 끌면 점이 하나 생긴다 (짧은 변엔 그리지 않는다)
            for (int i = 0; i < pts.Count; i++)
            {
                var a = pts[i];
                var b = pts[(i + 1) % pts.Count];
                if (Dist(a, b) < MinEdgeForMid) continue;
                dc.DrawEllipse(Backing, new Pen(Brushes.White, 1.5), new Point((a.X + b.X) / 2, (a.Y + b.Y) / 2), 4, 4);
            }
            for (int i = 0; i < pts.Count; i++)
            {
                var r = new Rect(pts[i].X - Handle / 2, pts[i].Y - Handle / 2, Handle, Handle);
                dc.DrawRectangle(i == _selVertex ? accent : Brushes.White, dark, r);
            }
        }

        private void DrawDraft(DrawingContext dc, FrameRect f)
        {
            var d = _draft!;
            var pts = d.Select(f.ToDip).ToList();
            var solid = RoiTheme.SolidOf(_draftKind);
            var pen = RoiTheme.PenOf(_draftKind, true);
            if (pts.Count >= 3) dc.DrawGeometry(RoiTheme.FillOf(_draftKind), null, Polygon(pts));
            for (int i = 1; i < pts.Count; i++) dc.DrawLine(pen, pts[i - 1], pts[i]);
            if (pts.Count > 0)
            {
                var preview = new Pen(solid, 1.5) { DashStyle = new DashStyle([4, 3], 0) };
                dc.DrawLine(preview, pts[^1], _mouse);
            }
            var dark = new Pen(Backing, 1.5);
            for (int i = 0; i < pts.Count; i++)
            {
                // 첫 점은 닫을 수 있게 되면(점 3개 이상) 크게 — 눌러서 닫는 자리
                bool closeTarget = i == 0 && pts.Count >= RoiSet.MinPoints;
                double r = closeTarget ? CloseHit / 2 + 1 : Handle / 2;
                dc.DrawRectangle(closeTarget ? solid : Brushes.White, dark, new Rect(pts[i].X - r, pts[i].Y - r, 2 * r, 2 * r));
            }
        }

        private void DrawHint(DrawingContext dc, double ppd)
        {
            // 구역이 없고 그리는 중도 아니면 안내하지 않는다 — 구역을 안 쓰는 사람도 일시정지할 때마다 이 줄을 보게 된다 ([추가] 버튼이 시작점)
            if (_draft == null && _model.Zones.Count == 0) return;
            string s = _draft != null ? "클릭: 점 추가 · 첫 점·Enter: 닫기 · 우클릭: 한 점 취소 · Esc: 그만두기"
                : "구역 클릭: 선택 · 점 끌기: 이동 · ○ 끌기: 점 추가 · 점 선택 후 Delete: 점 삭제";
            var text = Text(s, RoiTheme.Res<Brush>("SubTextBrush", Brushes.LightGray), ppd, Math.Max(40, ActualWidth - 40));
            const double padX = 8, padY = 3;
            double w = text.WidthIncludingTrailingWhitespace + 2 * padX, h = text.Height + 2 * padY;
            dc.DrawRectangle(Backing, null, new Rect((ActualWidth - w) / 2, ActualHeight - h - 8, w, h));
            dc.DrawText(text, new Point((ActualWidth - w) / 2 + padX, ActualHeight - h - 8 + padY));
        }
    }
}
