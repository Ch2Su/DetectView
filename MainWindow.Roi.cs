using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DetectView
{
    /// <summary>왼쪽 패널 구역 목록의 한 줄 — 구역(RoiZone)에 목록이 쓰는 색·이름을 붙인 것</summary>
    internal sealed class RoiListItem
    {
        public RoiListItem(RoiZone z)
        {
            Id = z.Id;
            Label = z.Label;
            KindName = RoiKinds.Name(z.Kind);
            DisplayName = z.DisplayName;
            Brush = RoiTheme.SolidOf(z.Kind);
            FillBrush = RoiTheme.FillOf(z.Kind);
        }

        public int Id { get; }
        public string Label { get; }
        public string KindName { get; }
        public string DisplayName { get; }
        public Brush Brush { get; }
        public Brush FillBrush { get; }

        public override string ToString() => DisplayName;
    }

    /// <summary>
    /// 관심 구역(ROI) — 왼쪽 패널 '구역' 절과 영상 위 RoiLayer 를 잇고 파일에 바로 저장한다 (2026-11-03, 시안 안 2).
    /// 파일(영상)에서만: 영상을 열면 <c>&lt;영상&gt;.roi.json</c> 을 읽고, 소스를 내리면 비운다. 편집은 파일을 멈춘 채일 때만 —
    /// 재생 중엔 프레임이 계속 바뀌고, SAHI 중엔 화면이 정해져 있다. 구역은 프레임 0~1 좌표라 창 크기·해상도와 무관하다.
    /// 목록·콤보·버튼은 UpdateRoiUi 한 곳에서 켜고 끈다 (ApplyModeToUi 와 같은 방식 — 재생·SAHI·소스가 바뀔 때마다 부른다)
    /// </summary>
    public partial class MainWindow
    {
        private string? _roiPath;
        private RoiKind _roiNewKind = RoiKind.Lane;   // [추가] 로 만드는 구역의 유형 — 마지막으로 고르거나 선택한 유형
        private bool _roiSyncing;                      // 목록·콤보를 코드로 맞추는 중 (SelectionChanged 가 되돌아오지 않게)

        // 알고 있는 뷰들 (화면 식별 2b — ViewIdentity.cs). 영상(파일)을 열 때 구역 파일의 뷰 목록으로 만들고 재생 루프가 식별·등록한다.
        // 소스를 내릴 때 Dispose 하지 않는다 — 루프가 아직 Identify 중일 수 있고 네이티브 객체는 가비지 수집이 해제한다
        private ViewRegistry? _viewRegistry;
        private int _shownViewId;                      // 지금 화면이 어느 뷰인지 (UI 가 알고 있는 값 — 루프가 프레임과 함께 알려 준다. 0 = 모름)

        private void InitRoi()
        {
            foreach (var k in RoiKinds.All)
                ZoneKindSelector.Items.Add(new ComboBoxItem { Content = RoiKinds.Name(k), Tag = k });

            ZoneLayer.ZonesChanged += OnRoiZonesChanged;
            ZoneLayer.SelectionChanged += OnRoiSelectionChanged;
            ZoneLayer.DraftingChanged += UpdateRoiUi;
            RefreshRoiList();
            UpdateRoiUi();
        }

        // 영상을 연 직후 — 영상 옆 구역 파일을 읽는다. 읽지 못하면 빈 구역으로 시작하고 사유를 돌려준다(파일은 .bad 로 복사해 둔다 —
        // 이어서 편집하면 저장이 그 파일을 덮어쓰므로)
        private string? LoadRoi(string videoPath, double frameW, double frameH)
        {
            _roiPath = RoiSet.PathFor(videoPath);
            RoiSet set;
            string? warn = null;
            try { set = RoiSet.Load(_roiPath); }
            catch (Exception ex)   // 구역은 부가 기능 — 어떤 이유로든 못 읽어도 영상 열기를 막지 않는다 (OpenVideoFile 의 catch 는 '영상 로드 실패' 로 소스를 내린다)
            {
                set = new RoiSet();
                warn = $"구역 파일을 읽지 못했습니다 ({Path.GetFileName(_roiPath)}): {ex.Message}";
                try { File.Copy(_roiPath, _roiPath + ".bad", true); } catch { /* 복사 실패는 무시 — 사유는 이미 알렸다 */ }
            }
            ZoneLayer.SetFrameSize(frameW, frameH);
            ZoneLayer.ViewId = 0;   // 어느 뷰인지는 재생 루프가 첫 화면을 알아본 뒤에 알려 준다
            _shownViewId = 0;
            ZoneLayer.SetZones(set);
            _viewRegistry = new ViewRegistry(set.SavedViews, set.NextViewId);
            PublishZoneShapes();
            RefreshRoiList();
            UpdateRoiUi();
            return warn;
        }

        // 소스를 내릴 때 — 구역은 열려 있는 영상의 것이다
        private void ClearRoi()
        {
            _roiPath = null;
            _viewRegistry = null;
            _shownViewId = 0;
            ZoneLayer.SetFrameSize(0, 0);
            ZoneLayer.ViewId = 0;
            ZoneLayer.SetZones(new RoiSet());
            PublishZoneShapes();
            RefreshRoiList();
            UpdateRoiUi();
        }

        /// <summary>
        /// 재생 루프가 알아본 지금 화면의 뷰(0 = 모름)를 받는다 — 영상 프레임과 같은 디스패치에서 부른다. 같은 값이면 아무것도 안 한다.
        /// 바뀌면 구역 목록·그림이 그 뷰의 구역으로 바뀌고, 뷰가 정해지지 않은 옛 구역은 처음 알아본 뷰의 것이 된다
        /// </summary>
        private void ShowCurrentView(int viewId)
        {
            if (viewId == _shownViewId) return;
            _shownViewId = viewId;
            ZoneLayer.ViewId = viewId;
            if (viewId > 0 && _viewRegistry != null && ZoneLayer.Model.AdoptUnassigned(_viewRegistry.FirstViewId) > 0)
                PublishZoneShapes();   // 루프의 구역 이벤트가 새 구역으로 다시 시작한다 (파일엔 다음 편집 때 저장된다)
            RefreshRoiList();
            UpdateRoiUi();
        }

        private void SaveRoi()
        {
            if (_roiPath == null) return;
            try { ZoneLayer.Model.Save(_roiPath, _viewRegistry?.Snapshot(), _viewRegistry?.NextId ?? 0); }
            catch (Exception ex)   // 이벤트 핸들러에서 새면 앱이 죽는다 — 저장 실패는 알리기만 하고 화면의 구역은 그대로 둔다
            {
                Txt_Status.Text = $"구역 저장 실패: {ex.Message}";   // 영상이 읽기 전용 폴더에 있을 때 등 — 화면 구역은 그대로 남는다
            }
        }

        private void OnRoiZonesChanged()
        {
            PublishZoneShapes();   // 구역 이벤트 감지가 새 구역으로 다시 시작한다
            SaveRoi();
            RefreshRoiList();
            UpdateRoiUi();
        }

        private void OnRoiSelectionChanged()
        {
            RefreshRoiList();
            UpdateRoiUi();
        }

        // 목록·개수·파일 이름·유형 콤보를 지금 구역들에 맞춘다
        private void RefreshRoiList()
        {
            var zones = ZoneLayer.VisibleZones.ToList();   // 지금 뷰의 구역만 — 구역은 그린 뷰에서만 보인다
            _roiSyncing = true;
            try
            {
                var items = zones.Select(z => new RoiListItem(z)).ToList();
                ZoneList.ItemsSource = items;
                ZoneList.SelectedItem = items.FirstOrDefault(i => i.Id == ZoneLayer.SelectedId);
                Txt_ZoneCount.Text = _roiPath == null ? "" : ZoneLayer.ViewId > 0 ? $"V{ZoneLayer.ViewId} · {zones.Count}개" : "뷰 확인 중";
                Txt_ZoneCount.ToolTip = _roiPath == null ? null : $"구역 파일: {_roiPath}";   // UIA HelpText — 구역은 영상 옆 이 파일에 바로 저장된다

                var sel = ZoneLayer.SelectedZone;
                if (sel != null) _roiNewKind = sel.Kind;
                ZoneKindSelector.SelectedIndex = sel == null ? -1 : Array.IndexOf(RoiKinds.All, sel.Kind);
            }
            finally { _roiSyncing = false; }
        }

        // 구역 편집은 파일 재생을 멈춘 채일 때만 (재생 중엔 프레임이 계속 바뀐다, SAHI 중엔 화면이 정해져 있다).
        // 목록·버튼·콤보를 켜고 끄고 사유를 툴팁(UIA HelpText)에 둔다. 재생·일시정지·EOF·SAHI·소스 전환 때마다 부른다
        private void UpdateRoiUi()
        {
            bool file = _mode == SourceMode.File;
            bool viewKnown = ZoneLayer.ViewId > 0;   // 어느 뷰의 구역인지 알아야 그릴 수 있다 (화면 식별 2b)
            // 카메라가 돌아가는 중(또는 스트림이 끊긴) 화면은 어느 뷰의 구역을 그리는지 말할 수 없다 — 멈추기 전의 옛 뷰 번호가 남아 있어도 편집하지 못하게
            bool settled = _sceneBadge is not (SceneBadgeKind.Moving or SceneBadgeKind.Blank);
            bool editable = file && !_isPlaying && !_sahiBusy && ZoneLayer.HasFrameSize && viewKnown && settled;
            ZoneLayer.Editable = editable;   // 끄면 그리던 것을 버리고 DraftingChanged 로 이 함수가 다시 불린다 — 값이 같아 안전하다

            bool drafting = ZoneLayer.Drafting;
            bool hasSel = ZoneLayer.SelectedId != 0;
            Btn_ZoneAdd.IsEnabled = editable;
            Btn_ZoneDelete.IsEnabled = editable && hasSel && !drafting;
            ZoneList.IsEnabled = editable && !drafting;
            ZoneKindSelector.IsEnabled = editable && hasSel && !drafting;

            string why = !file ? "파일을 열면 구역을 편집할 수 있습니다"
                : _sahiBusy ? "정밀 분석이 끝나면 구역을 편집할 수 있습니다"
                : !settled && !_isPlaying ? "카메라가 움직이는 동안은 구역을 편집할 수 없습니다 — 화면이 멈춘 곳으로 시크하세요"
                : _isPlaying || viewKnown ? "일시정지하면 구역을 편집할 수 있습니다"
                : "지금 화면이 어느 뷰인지 알아본 뒤 구역을 편집할 수 있습니다";
            Btn_ZoneAdd.ToolTip = editable ? "영상 위를 클릭해 점을 찍어 새 구역을 그립니다 (첫 점을 누르거나 Enter 로 닫기)" : why;
            Btn_ZoneDelete.ToolTip = editable ? (hasSel ? "고른 구역을 지웁니다" : "지울 구역을 목록이나 영상에서 고르세요") : why;
            ZoneList.ToolTip = editable ? null : why;
            ZoneKindSelector.ToolTip = editable ? (hasSel ? null : "구역을 고르면 유형을 바꿀 수 있습니다") : why;
        }

        private void Btn_ZoneAdd_Click(object sender, RoutedEventArgs e) => ZoneLayer.BeginAdd(_roiNewKind);

        private void Btn_ZoneDelete_Click(object sender, RoutedEventArgs e) => ZoneLayer.DeleteSelectedZone();

        private void ZoneList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_roiSyncing) return;
            ZoneLayer.SelectedId = (ZoneList.SelectedItem as RoiListItem)?.Id ?? 0;
            RefreshRoiComboOnly();
            UpdateRoiUi();
        }

        private void ZoneKindSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_roiSyncing || ZoneKindSelector.SelectedItem is not ComboBoxItem { Tag: RoiKind kind }) return;
            _roiNewKind = kind;
            ZoneLayer.SetSelectedKind(kind);   // 바뀌었으면 ZonesChanged → 저장·목록 갱신
        }

        // 목록에서 고른 구역의 유형을 콤보에만 맞춘다 (목록을 다시 만들면 방금 한 선택이 흔들린다)
        private void RefreshRoiComboOnly()
        {
            var sel = ZoneLayer.SelectedZone;
            _roiSyncing = true;
            try
            {
                if (sel != null) _roiNewKind = sel.Kind;
                ZoneKindSelector.SelectedIndex = sel == null ? -1 : Array.IndexOf(RoiKinds.All, sel.Kind);
            }
            finally { _roiSyncing = false; }
        }
    }
}
