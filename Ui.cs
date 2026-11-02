using System.Windows;
using System.Windows.Media;

namespace YOLO11_Test
{
    /// <summary>
    /// App.xaml 의 버튼·콤보 스타일이 읽는 첨부 속성.
    /// 버튼 글자(Content)는 UI 테스트가 UIA 이름으로 찾고 읽으므로 문자열 그대로 두고, 아이콘은 템플릿이 <see cref="GlyphProperty"/> 로 그린다
    /// (아이콘을 Content 에 함께 넣으면 Content 가 문자열이 아니게 되어 UIA 이름이 빈다)
    /// </summary>
    public static class Ui
    {
        /// <summary>글자 앞 아이콘 — Segoe Fluent Icons 글리프 한 글자 (XAML 에선 &amp;#xED25; 처럼). 없으면 글자만</summary>
        public static readonly DependencyProperty GlyphProperty = DependencyProperty.RegisterAttached(
            "Glyph", typeof(string), typeof(Ui), new FrameworkPropertyMetadata(null));
        public static string? GetGlyph(DependencyObject o) => (string?)o.GetValue(GlyphProperty);
        public static void SetGlyph(DependencyObject o, string? value) => o.SetValue(GlyphProperty, value);

        /// <summary>아이콘 색 — 스타일이 정한다 (보통 강조색, 정지 버튼은 위험색)</summary>
        public static readonly DependencyProperty GlyphBrushProperty = DependencyProperty.RegisterAttached(
            "GlyphBrush", typeof(Brush), typeof(Ui), new FrameworkPropertyMetadata(null));
        public static Brush? GetGlyphBrush(DependencyObject o) => (Brush?)o.GetValue(GlyphBrushProperty);
        public static void SetGlyphBrush(DependencyObject o, Brush? value) => o.SetValue(GlyphBrushProperty, value);

        /// <summary>마우스를 올렸을 때의 바탕 — 스타일이 정한다</summary>
        public static readonly DependencyProperty HoverBackgroundProperty = DependencyProperty.RegisterAttached(
            "HoverBackground", typeof(Brush), typeof(Ui), new FrameworkPropertyMetadata(null));
        public static Brush? GetHoverBackground(DependencyObject o) => (Brush?)o.GetValue(HoverBackgroundProperty);
        public static void SetHoverBackground(DependencyObject o, Brush? value) => o.SetValue(HoverBackgroundProperty, value);

        /// <summary>아이콘만 보이는 버튼. 글자는 UIA 이름용으로 Content 에 그대로 두고 툴팁으로 알린다</summary>
        public static readonly DependencyProperty IconOnlyProperty = DependencyProperty.RegisterAttached(
            "IconOnly", typeof(bool), typeof(Ui), new FrameworkPropertyMetadata(false));
        public static bool GetIconOnly(DependencyObject o) => (bool)o.GetValue(IconOnlyProperty);
        public static void SetIconOnly(DependencyObject o, bool value) => o.SetValue(IconOnlyProperty, value);

        /// <summary>지금 켜진 소스의 버튼 (파일 열기·카메라 시작·화면 HUD 중 하나) — ApplyModeToUi 가 정한다</summary>
        public static readonly DependencyProperty IsActiveProperty = DependencyProperty.RegisterAttached(
            "IsActive", typeof(bool), typeof(Ui), new FrameworkPropertyMetadata(false));
        public static bool GetIsActive(DependencyObject o) => (bool)o.GetValue(IsActiveProperty);
        public static void SetIsActive(DependencyObject o, bool value) => o.SetValue(IsActiveProperty, value);
    }
}
