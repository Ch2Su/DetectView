using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace YOLO11_Test
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
            ZoneLayer.SetZones(set);
            RefreshRoiList();
            UpdateRoiUi();
            return warn;
        }

        // 소스를 내릴 때 — 구역은 열려 있는 영상의 것이다
        private void ClearRoi()
        {
            _roiPath = null;
            ZoneLayer.SetFrameSize(0, 0);
            ZoneLayer.SetZones(new RoiSet());
            RefreshRoiList();
            UpdateRoiUi();
        }

        private void SaveRoi()
        {
            if (_roiPath == null) return;
            try { ZoneLayer.Model.Save(_roiPath); }
            catch (Exception ex)   // 이벤트 핸들러에서 새면 앱이 죽는다 — 저장 실패는 알리기만 하고 화면의 구역은 그대로 둔다
            {
                Txt_Status.Text = $"구역 저장 실패: {ex.Message}";   // 영상이 읽기 전용 폴더에 있을 때 등 — 화면 구역은 그대로 남는다
            }
        }

        private void OnRoiZonesChanged()
        {
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
            var zones = ZoneLayer.Model.Zones;
            _roiSyncing = true;
            try
            {
                var items = zones.Select(z => new RoiListItem(z)).ToList();
                ZoneList.ItemsSource = items;
                ZoneList.SelectedItem = items.FirstOrDefault(i => i.Id == ZoneLayer.SelectedId);
                Txt_ZoneCount.Text = _roiPath == null ? "" : $"{zones.Count}개";
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
            bool editable = file && !_isPlaying && !_sahiBusy && ZoneLayer.HasFrameSize;
            ZoneLayer.Editable = editable;   // 끄면 그리던 것을 버리고 DraftingChanged 로 이 함수가 다시 불린다 — 값이 같아 안전하다

            bool drafting = ZoneLayer.Drafting;
            bool hasSel = ZoneLayer.SelectedId != 0;
            Btn_ZoneAdd.IsEnabled = editable;
            Btn_ZoneDelete.IsEnabled = editable && hasSel && !drafting;
            ZoneList.IsEnabled = editable && !drafting;
            ZoneKindSelector.IsEnabled = editable && hasSel && !drafting;

            string why = file ? (_sahiBusy ? "정밀 분석이 끝나면 구역을 편집할 수 있습니다" : "일시정지하면 구역을 편집할 수 있습니다")
                              : "파일을 열면 구역을 편집할 수 있습니다";
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
