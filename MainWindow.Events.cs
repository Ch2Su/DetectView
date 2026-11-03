using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Media;

namespace YOLO11_Test
{
    /// <summary>이벤트 로그 목록의 한 줄 — 구역 이벤트(ZoneEvent)에 목록이 쓰는 글자·색을 붙인 것</summary>
    internal sealed class EventListItem
    {
        public EventListItem(ZoneEvent e)
        {
            Event = e;
            var ts = TimeSpan.FromMilliseconds(Math.Max(0, e.Ms));
            Time = $"{(int)ts.TotalMinutes:00}:{ts.Seconds:00}.{ts.Milliseconds / 100}";
            if (e.Kind == ZoneEventKind.ViewChanged)
            {
                LabelText = "화면 전환";
                LabelBrush = RoiTheme.Res<Brush>("WarnBrush", Brushes.Goldenrod);
                ZoneText = "";
                ZoneBrush = LabelBrush;
            }
            else
            {
                bool incident = e.Kind == ZoneEventKind.Incident;
                LabelText = $"{(incident ? "⚠ " : "")}{e.Label.ToUpperInvariant()} #{e.TrackId}";
                LabelBrush = incident ? RoiTheme.Res<Brush>("DangerBrush", Brushes.IndianRed) : RoiTheme.BoxBrushOf(e.Label);
                ZoneText = e.ZoneName;
                ZoneBrush = RoiTheme.SolidOf(e.ZoneKind);
            }
            // 유고 후보 행은 빨갛게, 해소 행은 초록으로 (그 밖의 이벤트는 기본 글자색)
            ActionBrush = e.Kind switch
            {
                ZoneEventKind.Incident => RoiTheme.Res<Brush>("DangerBrush", Brushes.IndianRed),
                ZoneEventKind.IncidentEnd => RoiTheme.Res<Brush>("OkBrush", Brushes.LightGreen),
                _ => RoiTheme.Res<Brush>("TextBrush", Brushes.White),
            };
            ActionText = e.ActionText;
            DisplayName = string.Join(" ", new[] { Time, LabelText, ZoneText, ActionText }.Where(s => s.Length > 0));
        }

        public ZoneEvent Event { get; }
        public string Time { get; }
        public string LabelText { get; }
        public Brush LabelBrush { get; }
        public string ZoneText { get; }
        public Brush ZoneBrush { get; }
        public string ActionText { get; }
        public Brush ActionBrush { get; }
        /// <summary>UIA 이름 — "00:11.2 CAR #40 Z1 차로 진입"</summary>
        public string DisplayName { get; }

        public override string ToString() => DisplayName;
    }

    /// <summary>
    /// 구역 진입·나감 이벤트 로그 (유고 감지 3단계, 2026-11-03). 감지는 재생 루프(ZoneEvents.cs)가 하고, 여기는 영상 아래 접이식 로그 패널이다
    /// (사용자가 시안 셋 중 '영상 아래 접이식 로그'와 '화면 + [CSV 저장] 버튼'을 골랐다). 최근 이벤트가 위. 영상(파일)이 열려 있을 때만 보이고
    /// 소스를 내리면 비운다 — 남기려면 [CSV 저장]. 이벤트가 많은 장면(신호대기 행렬)이 있어 <see cref="MaxEvents"/> 건까지만 두고 오래된 것부터 버린다
    /// </summary>
    public partial class MainWindow
    {
        private const int MaxEvents = 2000;

        private readonly ObservableCollection<EventListItem> _eventItems = [];
        private bool _eventsExpanded;

        // 재생 루프(다른 스레드)가 읽는 값 — 구역 스냅샷은 불변 참조를 바꿔 넘기고, 구역이 바뀌었다는 깃발은 루프가 꺼내며 감지기를 비운다 (MainWindow.Roi.cs)
        private volatile ZoneShape[] _zoneShapes = [];
        private int _zoneEventsReset;

        private void InitEvents()
        {
            EventList.ItemsSource = _eventItems;
            UpdateEventPanel();
        }

        // 구역이 바뀌었다 (읽음·편집·비움) — 루프가 새 구역으로 다시 시작하게 한다 (이미 안에 있던 물체는 '이미 안에 있음'으로 새 기준)
        private void PublishZoneShapes()
        {
            _zoneShapes = ZoneLayer.Model.Snapshot();
            Interlocked.Exchange(ref _zoneEventsReset, 1);
        }

        // UI 스레드. 재생 루프가 프레임을 내보내는 같은 디스패치에서 부른다
        private void AddZoneEvents(IReadOnlyList<ZoneEvent> events)
        {
            foreach (var e in events)
            {
                _eventItems.Insert(0, new EventListItem(e));
                if (_eventItems.Count > MaxEvents) _eventItems.RemoveAt(_eventItems.Count - 1);
            }
            UpdateEventPanel();
        }

        private void ClearZoneEvents()
        {
            _eventItems.Clear();
            ShowIncidentCount(0);
            UpdateEventPanel();
        }

        private int _incidentCount;

        // 지금 풀리지 않은 유고 후보 수 — 헤더에 빨간 글자로 (로그를 접어 둬도 보이게). 보이는 프레임의 상태라 일시정지하면 그대로 남는다.
        // 프레임마다 부르므로 같은 값이면 건드리지 않는다. 0 이면 숨긴다 (UIA 에서도 사라진다)
        private void ShowIncidentCount(int n)
        {
            if (n == _incidentCount) return;
            _incidentCount = n;
            Txt_EventIncident.Text = n > 0 ? $"⚠ 유고 후보 {n}" : "";
            Txt_EventIncident.Visibility = n > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        // 헤더 글자·버튼·접힘 — 영상(파일)일 때만 패널이 보인다 (ApplyModeToUi 도 부른다)
        private void UpdateEventPanel()
        {
            EventLogPanel.Visibility = _mode == SourceMode.File ? Visibility.Visible : Visibility.Collapsed;
            int n = _eventItems.Count(i => i.Event.Kind != ZoneEventKind.ViewChanged);
            Btn_EventToggle.Content = $"이벤트 {n}건";
            Ui.SetGlyph(Btn_EventToggle, _eventsExpanded ? "" : "");   // 펼쳤으면 ⌄ (접기), 접었으면 ⌃ (펼치기)
            EventList.Visibility = _eventsExpanded ? Visibility.Visible : Visibility.Collapsed;
            Btn_EventSave.IsEnabled = _eventItems.Count > 0;
            Btn_EventClear.IsEnabled = _eventItems.Count > 0;
            Btn_EventToggle.ToolTip = _eventsExpanded ? "이벤트 목록을 접습니다" : "이벤트 목록을 펼칩니다 — 구역에 들어오고 나간 물체가 최근 순으로 쌓입니다 (구역은 왼쪽 패널에서 그립니다)";
            Btn_EventSave.ToolTip = _eventItems.Count > 0 ? "지금까지의 이벤트를 영상 옆 <영상>.events.csv 로 저장합니다 (있으면 덮어씀)" : "저장할 이벤트가 없습니다";
        }

        private void Btn_EventToggle_Click(object sender, RoutedEventArgs e)
        {
            _eventsExpanded = !_eventsExpanded;
            UpdateEventPanel();
        }

        private void Btn_EventClear_Click(object sender, RoutedEventArgs e) => ClearZoneEvents();

        // 쉼표·따옴표·줄바꿈이 있으면 따옴표로 감싼다 (note 열 — 해소 사유·뷰 이름에 쓰이는 글자)
        private static string CsvText(string s) =>
            s.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

        private void Btn_EventSave_Click(object sender, RoutedEventArgs e)
        {
            if (_filePath == null || _eventItems.Count == 0) return;
            string path = _filePath + ".events.csv";
            try
            {
                var sb = new StringBuilder("ms,time,event,track,label,zone,zone_kind,dwell_ms,note\r\n");
                foreach (var it in _eventItems.Reverse())   // 시간 순(오래된 것부터)
                {
                    var ev = it.Event;
                    bool marker = ev.Kind == ZoneEventKind.ViewChanged;
                    sb.Append(ev.Ms.ToString("0", CultureInfo.InvariantCulture)).Append(',')
                      .Append(it.Time).Append(',')
                      .Append(ev.CsvEvent).Append(',')
                      .Append(marker ? "" : ev.TrackId.ToString(CultureInfo.InvariantCulture)).Append(',')
                      .Append(marker ? "" : ev.Label).Append(',')
                      .Append(marker ? "" : $"Z{ev.ZoneId}").Append(',')
                      .Append(marker ? "" : RoiKinds.Key(ev.ZoneKind)).Append(',')
                      .Append(ev.Kind is ZoneEventKind.Exit or ZoneEventKind.Incident or ZoneEventKind.IncidentEnd ? ev.DwellMs.ToString("0", CultureInfo.InvariantCulture) : "").Append(',')
                      .Append(CsvText(ev.Note))
                      .Append("\r\n");
                }
                File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
                Txt_Status.Text = $"이벤트 저장: {Path.GetFileName(path)} ({_eventItems.Count(i => i.Event.Kind != ZoneEventKind.ViewChanged)}건)";
            }
            catch (Exception ex)   // 이벤트 핸들러에서 새면 앱이 죽는다 — 저장 실패는 알리기만 하고 로그는 그대로 둔다
            {
                Txt_Status.Text = $"이벤트 저장 실패: {ex.Message}";
            }
        }
    }
}
