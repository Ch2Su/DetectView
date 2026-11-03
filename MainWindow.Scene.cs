using System.Windows;

namespace DetectView
{
    /// <summary>영상 위 배지가 알리는 상태 (화면 전환 감지 — 아래 MainWindow 의 ShowSceneBadge)</summary>
    internal enum SceneBadgeKind
    {
        None,
        /// <summary>카메라가 돌아가거나 장면이 바뀌는 중 — 박스에 번호를 붙이지 않는다</summary>
        Moving,
        /// <summary>스트림이 끊겨 검은 화면이다 (카메라가 도는 것과 다르다) — 역시 번호를 붙이지 않는다</summary>
        Blank,
        /// <summary>안정됐고 직전과 다른 화면이라 추적기를 비우고 번호를 새로 매겼다 (몇 초만). 어느 뷰인지는 글자로 알린다(뷰 식별 2b)</summary>
        Changed,
        /// <summary>안정됐지만 어느 뷰인지 알아보지 못했다(특징이 모자라거나 애매함) — 알아볼 때까지 구역 이벤트는 멈춰 있다</summary>
        UnknownView,
    }

    /// <summary>
    /// 화면 전환 감지의 화면 쪽 (2026-11-03). 감지·추적기 연결은 PlaybackLoop(MainWindow.Playback.cs), 알고리즘은 SceneChange.cs.
    /// 배지는 영상 칸 오른쪽 위(왼쪽 위는 검출 범례) — 사용자가 시안 셋(영상 위 배지 · 상태바 문구만 · 표시 없음) 중 배지를 골랐다.
    /// 앱 안 영상(파일)에서만 쓴다 — HUD 는 바탕화면 전체라 창이 바뀔 때마다 화면이 달라져 전환 감지와 맞지 않는다
    /// </summary>
    public partial class MainWindow
    {
        private SceneBadgeKind _sceneBadge;
        private string _sceneBadgeText = "";

        // '화면 전환 · 번호 새로 매김' 배지를 보이는 길이 (영상 시각 ms)
        private const double SceneNoticeMs = 2000;

        // UI 스레드에서만. 같은 상태면 건드리지 않는다 (프레임마다 부른다). 글자 앞 ● 는 상태점과 같은 말투
        private void ShowSceneBadge(SceneBadgeKind kind, string? viewText = null)
        {
            // 같은 종류라도 뷰 글자가 바뀌면(V2 → V3) 다시 쓴다
            if (kind == _sceneBadge && (kind != SceneBadgeKind.Changed || (viewText ?? "") == _sceneBadgeText)) return;
            bool movingChanged = (kind is SceneBadgeKind.Moving or SceneBadgeKind.Blank) != (_sceneBadge is SceneBadgeKind.Moving or SceneBadgeKind.Blank);
            _sceneBadge = kind;
            _sceneBadgeText = viewText ?? "";
            if (movingChanged) UpdateRoiUi();   // 움직이는 동안은 구역 편집을 막는다
            if (kind == SceneBadgeKind.None)
            {
                SceneBadge.Visibility = Visibility.Collapsed;
                return;
            }
            (string text, string brush) = kind switch
            {
                SceneBadgeKind.Moving => ("● 화면 이동 중", "WarnBrush"),
                SceneBadgeKind.Blank => ("● 화면 끊김", "WarnBrush"),
                SceneBadgeKind.UnknownView => ("● 뷰를 알 수 없음", "WarnBrush"),
                _ => (string.IsNullOrEmpty(viewText) ? "● 화면 전환 · 번호 새로 매김" : "● " + viewText, "AccentBrush"),
            };
            Txt_SceneBadge.Text = text;
            Txt_SceneBadge.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, brush);
            SceneBadge.Visibility = Visibility.Visible;
        }
    }
}
