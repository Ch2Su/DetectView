using System;
using System.Collections.Generic;
using System.Linq;
using OpenCvSharp;

namespace DetectView
{
    /// <summary>한 프레임의 화면 식별·자세 지켜보기 결과 (<see cref="ViewSession.Update"/>)</summary>
    /// <param name="Moving">카메라가 움직이는 중이라고 본다(자세 지켜보기가 움직이는 증거를 봤거나, 잰 움직임 뒤 화면이 자리 잡기를 기다리는 중) — 루프는 화면 전환 감지의 움직임처럼 추적기에 넣지 않는다</param>
    /// <param name="Changed">이 프레임에서 화면이 바뀌었다(빠른 전환 · 자세 지켜보기가 다른 자세로 움직였다고 본 것) — 추적기를 비우고 구역 이벤트·유고를 멈춘다</param>
    /// <param name="Identified">이 프레임에서 식별했으면 그 결과 (알 수 없음 포함) — 루프는 이걸 받으면 그 뷰의 구역으로 이벤트·유고를 다시 시작한다</param>
    /// <param name="Announce">Identified 가 화면이 바뀐 뒤 처음 알아본 것이라 배지·로그로 알릴 차례</param>
    /// <param name="Pose">자세 지켜보기의 이번 확인 결과 (실험 도구 표시)</param>
    /// <param name="Purged">이 프레임에서 버린 보조 기준 수 (실험 도구 표시)</param>
    /// <param name="Note">실험 도구·디버그 출력에 남길 일 (글자 배움 · 잘못 알아본 방문 · 오류)</param>
    internal readonly record struct ViewStep(bool Moving, bool Changed, ViewIdentification? Identified, bool Announce, PoseStep Pose, int Purged, string? Note);

    /// <summary>
    /// 파일 재생의 화면 식별(<see cref="ViewRegistry"/>) + 자세 지켜보기(<see cref="CameraPoseWatch"/>) — 언제 식별하고, 카메라가 움직였을 때 무엇을 배우고 버리는지를 한곳에 둔다
    /// (2026-11-22 — 예전엔 이 순서가 재생 루프에 흩어져 있고 실험 도구 TrackerCheck 가 그것을 베껴 써서, 셀프 테스트가 앱과 다른 순서를 시험할 수 있었다. 이제 둘 다 이 클래스를 쓴다).
    /// 프레임마다 화면 전환 감지 결과와 함께 <see cref="Update"/> 를 부르고, 시크처럼 앞뒤가 이어지지 않으면 <see cref="Reset"/>.
    /// WPF 없음. 한 스레드(재생 루프)에서만 쓴다
    /// </summary>
    internal sealed class ViewSession : IDisposable
    {
        /// <summary>떠난 뷰를 식별 후보에서 빼 두는 시간 — 식별이 될 때까지(알 수 없음이 이어져도) 빼 두되, 이만큼 지나면 카메라가 돌아왔을 수도 있으니 다시 넣는다(새 전환이 오면 바로 끝낸다).
        /// 예전엔 바로 다음 식별에서만 빼서, 그 식별이 알 수 없음이면 다시 볼 때마다 떠난 뷰의 15~29 인라이어가 '애매함'으로 새 뷰 등록을 막았다(2026-11-22 검토)</summary>
        private const double NotViewHoldMs = 10000;
        /// <summary>
        /// 자세 지켜보기가 다른 자세로 잰 뒤 이만큼 기다렸다 식별한다 — 줌 직후엔 초점을 다시 잡느라 화면이 흐려, 떠난 뷰가 사실 지금 자세였는지 볼 때 그 뷰의 기준 화면이
        /// 흐린 화면을 받아들이지 못했고(test6 1417.5초 — 1441초엔 인라이어 223 으로 받아들임, 2026-11-22 검토) 새 뷰의 기준 화면도 흐린 것으로 남았다. 기다리는 동안은 움직이는 중으로 둔다
        /// </summary>
        private const double SettleAfterMoveMs = 1000;
        /// <summary>조용한 프레임에서 미뤄 둔 보조 기준 정리 · 낡은 서명 다시 만들기를 영상 시각 이만큼마다 한 장씩 — 프레임마다 하면 정리가 남은 동안 60fps 영상의
        /// 프레임마다 10~20 ms 가 더 들었다(2026-11-22 검토). 정리가 다 안 끝났어도 그 뷰를 후보로 넣는 식별은 먼저 마저 끝낸다(Identify)</summary>
        private const double ChoreEveryMs = 100;
        private const int PurgePerChore = 1, RebuildsPerChore = 1;

        private readonly ViewRegistry _views;
        private readonly CameraPoseWatch _pose;
        private bool _needIdentify = true;
        private bool _announce;                            // 화면이 바뀐 뒤 알아본 뷰를 배지·로그로 알릴 차례
        private double _nextIdentifyMs = double.NegativeInfinity;
        private int _unknownStreak;                        // 연달아 알 수 없음 — 다시 볼 간격을 늘린다
        private int _viewNow;                              // 지금 화면의 뷰 번호 (0 = 모름)
        private int _lastWatched;                          // 마지막으로 지켜본 뷰 — 그 뷰의 평소 수준을 기억하고(_levels) 식별할 때 먼저 본다
        private int _notView;                              // 카메라가 떠났다고 볼 근거가 있는 뷰 — 식별 후보에서 뺀다
        private double _notViewUntilMs;
        private readonly Dictionary<int, int> _levels = []; // 뷰마다 확인된 자세 지켜보기 평소 수준(그 뷰에서 본 가장 큰 것) — 그 뷰로 돌아오면 첫 확인부터 무너짐을 잰다
        private int _levelSnapVisit = -1, _levelSnapView, _levelSnap;   // 지금 방문이 시작할 때의 그 뷰 수준(-1 = 없었음) — RestoreLevel
        private int _visit;                                // 방문 번호 — 식별에 성공할 때마다 하나씩. 보조 기준에 붙여 둔다(그 방문이 잘못 알아본 것이면 통째로 버린다)
        private double _nextRecentMs = double.PositiveInfinity;   // 서 있는 화면을 보조 기준으로 더할 다음 영상 시각
        private double _settleUntilMs = double.NaN;        // 잰 움직임 뒤 화면이 자리 잡기를 기다리는 끝
        private int _leftView, _leftVisit;                 // 그 움직임으로 떠난 뷰와 그 방문
        private bool _leftMasked;                          // 그 움직임의 글자 짝이 마스크로 덮였다 — 떠난 뷰의 기준 화면으로 '정말 떠났나'를 물어볼 수 있다
        private double _quietCheckAtMs = double.NaN;       // 조용히 다시 알아볼 시각 (지켜보기 오류 뒤) — 같은 뷰면 그대로 이어 간다
        private ViewRegistry.PurgeJob? _purge;             // 미뤄 둔 보조 기준 정리
        private double _nextChoreMs = double.NegativeInfinity;
        private Mat? _movedBefore;                         // 잰 움직임의 움직이기 전 화면(가로 480 흑백) — 떠난 뷰가 그 화면을 받아들였나 볼 때 (OnSettledAfterMove)
        private Mat? _settleFrame;                         // 잰 움직임 직후 화면(가로 480 흑백) — 자리 잡기 전에 전환·시크가 오면 이것으로 떠난 뷰를 확인한다

        public ViewSession(ViewRegistry views, PoseWatchOptions? pose = null)
        {
            _views = views;
            _pose = new CameraPoseWatch(pose, views.Options);
        }

        public ViewRegistry Views => _views;
        public CameraPoseWatch Pose => _pose;

        /// <summary>지금 화면의 뷰 번호 (0 = 모름)</summary>
        public int ViewNow => _viewNow;

        /// <summary>카메라가 움직이는 중 (자세 지켜보기가 움직이는 증거를 봤거나 잰 움직임 뒤 자리 잡기를 기다리는 중)</summary>
        public bool Moving => _pose.Moving || !double.IsNaN(_settleUntilMs);

        /// <summary>이 시각에 Update 가 꼭 할 일이 있나 — 실험 도구가 프레임을 만들지 않고 건너뛸 때(앱은 프레임마다 부른다). 낡은 서명 미리 만들기(Maintain)는 덤이라 보지 않는다 —
        /// 안 해 두면 식별이 그때 만든다(결과는 같다)</summary>
        public bool Due(double ms) =>
            _pose.Due(ms) || (_needIdentify && ms >= _nextIdentifyMs) || (_purge != null && ms >= _nextChoreMs)
            || (!double.IsNaN(_settleUntilMs) && ms >= _settleUntilMs) || (!double.IsNaN(_quietCheckAtMs) && ms >= _quietCheckAtMs);

        /// <summary>시크처럼 앞뒤 화면이 이어지지 않을 때 — 지켜보기를 멈추고 다음 프레임에서 어느 뷰인지 다시 본다 (지금 뷰 번호는 다시 알아볼 때까지 둔다)</summary>
        public void Reset()
        {
            SaveLevel();
            _pose.Stop();
            // 잰 움직임 뒤 자리 잡기를 기다리던 중이면 들고 있는 움직임 앞뒤 화면으로 떠난 뷰를 확인한다(OnSettleInterrupted)
            if (!double.IsNaN(_settleUntilMs) && _leftView > 0) Guard(() => OnSettleInterrupted(_leftView, _leftMasked, cut: false), "움직인 뒤 처리");
            DropMoveFrames();
            _nextChoreMs = double.NegativeInfinity;   // 뒤로 시크하면 영상 시각이 줄어 미뤄 둔 정리가 그 시각까지 멈췄다(3차 검증)
            _settleUntilMs = double.NaN;
            _leftView = 0;
            _quietCheckAtMs = double.NaN;
            _notView = 0;
            _needIdentify = true;
            _nextIdentifyMs = double.NegativeInfinity;
            _unknownStreak = 0;
            _announce = false;
        }

        /// <summary>
        /// 화면에 내보낼 프레임 하나(frame — 루프가 이 스레드에서만 쓰는 값)와 그 프레임의 화면 전환 감지 결과(sc)로 뷰를 따라간다.
        /// 지켜보기 확인 → (움직이는 중·자리 잡기를 기다리는 중이면 여기까지) → 빠른 전환·잰 움직임이면 글자를 배우고 떠난 뷰를 다룬다 → 식별할 차례면 식별하고 그 뷰에서 지켜보기를 시작한다
        /// </summary>
        public ViewStep Update(Mat frame, double ms, SceneStep sc)
        {
            string? note = null;
            int purged = 0;
            var ps = default(PoseStep);
            bool settling = !double.IsNaN(_settleUntilMs);
            if (!sc.Unsettled && !sc.ViewChanged && !settling && _pose.Due(ms))
            {
                // 배운 글자를 넘긴다 — 멈춘 뒤 '카메라가 그 자리'를 볼 때 글자로 제자리인 짝을 장면으로 세지 않게(맞춤에서 빼지는 않는다)
                try { ps = _pose.Check(frame, ms, _views.Overlay); }
                catch (Exception ex) { note = $"자세 지켜보기 오류: {ex.Message}"; _pose.Stop(); ps = default; _quietCheckAtMs = ms + 1000; }
                // 서 있는 화면을 1분마다 그 뷰의 보조 기준으로 — 연기가 걷히거나 정체가 생겨도 시크해 돌아오면 같은 뷰로 알아보게(글자를 빼고 맞추면 처음 기준 화면만으론 약하다).
                // 따로 감싼다 — 여기서 실패해도 멀쩡한 지켜보기를 끄지 않게
                if (ps.Outcome == PoseOutcome.Still && _viewNow > 0 && ms >= _nextRecentMs)
                {
                    try { _views.AddRecent(_viewNow, frame, _pose.KeyPose, ms, _visit); }
                    catch (Exception ex) { note = $"보조 기준 추가 오류: {ex.Message}"; }
                    _nextRecentMs = ms + _pose.Options.RecentEveryMs;
                }
            }
            // 움직이는 중 — 화면 전환 감지의 움직임과 같다. 이 프레임에 전환 알림이 왔으면 그쪽을 처리한다(전환은 이 한 프레임에만 오는 알림이라 움직임 쪽으로 빠지면 사라진다)
            if (sc.Unsettled || (_pose.Moving && !sc.ViewChanged) || (settling && !sc.ViewChanged && ms < _settleUntilMs))
                return new ViewStep(Moving, false, null, false, ps, 0, note);

            bool changed = false, quiet = false, afterMove = false;
            if (sc.ViewChanged)
            {
                int interrupted = settling ? _leftView : 0;   // 잰 움직임 뒤 자리 잡기를 기다리던 중이었다 — 떠난 뷰의 확인은 이 전환으로 못 한다
                bool interruptedMasked = _leftMasked;
                _settleUntilMs = double.NaN;
                _leftView = 0;
                _quietCheckAtMs = double.NaN;
                SaveLevel();
                note = Guard(() => OnCut(frame, ms), "빠른 전환 처리") ?? note;
                if (interrupted > 0) note = Guard(() => OnSettleInterrupted(interrupted, interruptedMasked, cut: true), "움직인 뒤 처리") ?? note;
                DropMoveFrames();
                _pose.Stop();
                changed = true;
            }
            else if (settling)
            {
                // 잰 움직임 뒤 화면이 자리 잡았다 — 떠난 뷰를 확인하고 다시 알아본다
                _settleUntilMs = double.NaN;
                note = Guard(() => OnSettledAfterMove(frame, ms), "움직인 뒤 처리") ?? note;
                changed = true;
            }
            else if (ps.Outcome == PoseOutcome.Moved)
            {
                SaveLevel();   // 지켜보기는 꺼졌지만 떠난 자세에서 잰 평소 수준은 그 뷰의 것이다
                if (ps.Measured)
                {
                    note = Guard(() => OnMeasuredMove(frame, ps, ms), "잰 움직임 처리") ?? note;
                    return new ViewStep(true, false, null, false, ps, 0, note);   // 화면이 자리 잡을 때까지 움직이는 중으로 (위 SettleAfterMoveMs)
                }
                quiet = true;   // 재지 못한 움직임 — 다시 알아보고 같은 뷰면 그대로 잇는다(연기 같은 내용 변화를 화면 전환으로 처리하지 않게)
                afterMove = true;
            }
            else if (!double.IsNaN(_quietCheckAtMs) && ms >= _quietCheckAtMs)
            {
                _quietCheckAtMs = double.NaN;
                quiet = true;   // 지켜보기 오류 뒤 — 오류 전 자세를 그대로 믿지 않고 다시 알아본다(그사이 카메라가 움직였을 수 있다)
            }

            ViewIdentification? id = null;
            bool announce = false;
            if (quiet && _viewNow > 0)
            {
                var vid = Identify(frame, 0, ms, ref note, ref purged);
                // 같은 뷰 — 이 화면에서 새로 지켜본다(방문은 그대로: 보조 기준 번호를 잇는다). 장면이 몇 덩이에만 맞았다고 화면 전환으로 돌리지는 않는다 —
                // 글자만 맞은 맞춤이어도 다시 알아본 결과가 같은 뷰라 알림·추적기 비움만 늘 뿐 뷰는 그대로였다(3차에 넣었다 뺌)
                if (vid.Outcome == ViewOutcome.Known && vid.ViewId == _viewNow)
                {
                    StartWatching(frame, vid, ms, ref note);
                    // 재지 못한 움직임 뒤면 그때 잰 지금 화면의 수준으로 — 맑을 때의 수준으로 시작하면 연기 낀 화면이 곧바로 또 '무너짐'이 돼 같은 일을 되풀이했다(2차 검증)
                    if (afterMove) _pose.AdoptSeedLevel();
                    return new ViewStep(false, false, null, false, ps, purged, note ?? $"다시 알아봐도 V{_viewNow} — 그대로 이어 감");
                }
                // 다른 화면이다 — 화면 전환으로 처리하고 이 식별 결과를 쓴다
                BeginChange();
                id = vid;
                OnIdentified(vid, frame, ms, ref announce, ref note);
                return new ViewStep(false, true, id, announce, ps, purged, note);
            }
            if (quiet || changed) BeginChange();

            if (_needIdentify && ms >= _nextIdentifyMs)
            {
                if (_notView != 0 && ms > _notViewUntilMs) _notView = 0;
                var vid = Identify(frame, _notView, ms, ref note, ref purged);
                id = vid;
                OnIdentified(vid, frame, ms, ref announce, ref note);
            }
            else if (ps.Outcome == PoseOutcome.None && ms >= _nextChoreMs)
            {
                // 조용한 프레임 — 미뤄 둔 정리 한 장, 낡은 서명 하나 (식별·자세 확인을 한 프레임과 겹치지 않게)
                _nextChoreMs = ms + ChoreEveryMs;
                purged += PurgeSome(PurgePerChore);
                try { _views.Maintain(RebuildsPerChore); } catch (Exception ex) { note = $"서명 다시 만들기 오류: {ex.Message}"; }
            }
            return new ViewStep(false, changed || quiet, id, announce, ps, purged, note);
        }

        /// <summary>화면이 바뀌었다 — 지금 뷰를 비우고 다시 알아볼 차례로</summary>
        private void BeginChange()
        {
            _viewNow = 0;
            _needIdentify = true;
            _nextIdentifyMs = double.NegativeInfinity;
            _unknownStreak = 0;
            _announce = true;
        }

        /// <summary>식별 — 그 뷰를 후보로 넣는 식별 전엔 미뤄 둔 그 뷰의 보조 기준 정리를 먼저 끝낸다. 지금(또는 마지막으로 지켜본) 뷰를 먼저 본다</summary>
        private ViewIdentification Identify(Mat frame, int notView, double ms, ref string? note, ref int purged)
        {
            if (_purge != null && _purge.ViewId != notView) purged += PurgeSome(int.MaxValue);
            int hint = _viewNow > 0 ? _viewNow : _lastWatched;
            try { return _views.Identify(frame, notView, ms, hint); }
            catch (Exception ex) { note = $"뷰 식별 오류: {ex.Message}"; return new ViewIdentification(ViewOutcome.Unknown, 0, default); }
        }

        private void OnIdentified(ViewIdentification vid, Mat frame, double ms, ref bool announce, ref string? note)
        {
            if (vid.Outcome == ViewOutcome.Unknown)
            {
                _viewNow = 0;
                SaveLevel();
                _pose.Stop();
                _quietCheckAtMs = double.NaN;
                // 1초 → 2초 → 4초마다 다시 — 일시적으로 애매했을 수 있다. 매초 다시 보면 보조 기준이 찬 영상에서 알 수 없는 동안 매초 재생이 멈칫했다(2026-11-22 검토)
                _unknownStreak++;
                _nextIdentifyMs = ms + 1000.0 * (1 << Math.Min(2, _unknownStreak - 1));
                return;
            }
            _needIdentify = false;
            _unknownStreak = 0;
            _notView = 0;
            _viewNow = vid.ViewId;
            _visit++;
            announce = _announce;
            _announce = false;
            StartWatching(frame, vid, ms, ref note);
            // 아는 뷰로 알아본 화면도 보조 기준으로 — 짧게 보고 시크해 돌아와도 그 뷰로 알아보게(처음 기준 화면과 내용이 많이 다를 수 있다 — Test-CameraPose 의 1400초 시크가
            // 1405초에 알아본 이 화면으로 V1 이 된다). 글자만으로 다른 자세를 이 뷰로 알아본 것이었으면 나중에 그 방문째로 버린다(DropVisit — OnSettledAfterMove · OnCut).
            // 맞춤이 화면에 퍼졌을 때만 넣으면 안 된다 — test6 1405초는 같은 자세인데도 연기 낀 0초 기준과 글자 위주로 맞아 빠졌고, 그 뒤 1400·1410·1300초 시크가 알 수 없음이 됐다(3차 실측)
            if (vid.Outcome == ViewOutcome.Known && vid.Match.H != null)
            {
                try { _views.AddRecent(_viewNow, frame, vid.Match.H, ms, _visit); }
                catch (Exception ex) { note = $"보조 기준 추가 오류: {ex.Message}"; }
            }
        }

        /// <summary>이 뷰에서 카메라 자세를 지켜본다 — 아는 뷰면 기준 화면에 대한 지금 자세(식별의 호모그래피)부터, 새 뷰면 제자리부터</summary>
        private void StartWatching(Mat frame, ViewIdentification vid, double ms, ref string? note)
        {
            SaveLevel();
            // 그 뷰에서 확인한 평소 수준으로 시작한다 — 시작하자마자 한 줌·팬도 첫 확인에서 무너짐으로 잡게. 처음 보는 뷰면 모름(0): 첫 확인들로 잡고 처음 화면과 한 번 견줘 확인한다.
            // 예전엔 마지막 뷰의 수준을 이어 쓰고 다른 뷰로 가면 버려서, 다른 뷰를 들렀다 돌아오면 수준을 잃었다(2차 검증)
            bool known = _levels.TryGetValue(vid.ViewId, out int level);
            if (_visit != _levelSnapVisit)
            {
                // 이 방문이 시작할 때의 수준을 적어 둔다 — 나중에 글자만으로 알아본 다른 자세였다고 드러나면 그 방문에서 기억한 수준을 되돌린다(RestoreLevel)
                _levelSnapVisit = _visit;
                _levelSnapView = vid.ViewId;
                _levelSnap = known ? level : -1;
            }
            if (known) _pose.SetLevel(level, true);
            else _pose.SetLevel(0, false);
            _lastWatched = vid.ViewId;
            try { _pose.Start(frame, vid.Outcome == ViewOutcome.Known ? vid.Match.H : null, ms); }
            catch (Exception ex) { note = $"자세 지켜보기 시작 오류: {ex.Message}"; _pose.Stop(); _quietCheckAtMs = ms + 1000; }
            _nextRecentMs = ms + _pose.Options.RecentEveryMs;
        }

        /// <summary>
        /// 자세 지켜보기가 지금 뷰의 허용 범위 밖으로 움직였다고 잰 직후 — 움직임 앞뒤에서 제자리였던 짝을 화면 고정 글자로 배우고(몇 덩이에 몰렸을 때만 —
        /// 화면 전체에 퍼진 제자리 짝은 서 있는 장면이다), 떠난 뷰를 기억하고 화면이 자리 잡기를 기다린다
        /// </summary>
        private string? OnMeasuredMove(Mat frame, PoseStep ps, double ms)
        {
            // 상태부터 — 아래 글자 배우기가 실패해도 떠난 뷰와 자리 잡기 기다림은 남게(예전엔 순서가 반대라, 배우다 실패하면 지켜보기가 꺼진 채 지금 뷰가 그대로 남아
            // 다시 알아보지도 않았다, 2차 검증). 떠난 뷰 확인은 글자가 덮였을 때만 하니 기본값은 '안 덮임'
            _leftView = _viewNow;
            _leftVisit = _visit;
            _leftMasked = false;
            _viewNow = 0;   // 떠났다 — 다시 알아볼 때까지 구역을 쓰지 않는다
            _settleUntilMs = ms + SettleAfterMoveMs;
            _quietCheckAtMs = double.NaN;
            DropMoveFrames();
            _movedBefore = _pose.TakeMovedBefore();
            _settleFrame = ViewMatcher.PrepareGray(frame);
            string? note = null;
            var (w, h) = ViewMatcher.BaseSize(frame);
            var pts = ps.Overlay ?? [];
            if (pts.Count > 0 && ViewMatcher.Spread(pts, w, h) <= _views.Options.OverlayMaxSpread && _views.LearnOverlay(pts, w, h))
                note = $"글자 배움 (칸 {_views.Overlay.Cells})";
            // 떠난 뷰의 기준 화면에 '정말 떠났나'를 물으려면 이 움직임의 글자 짝이 마스크로 덮여 있어야 한다 — 덮이지 않은 글자로는 기준 화면이 글자만으로 받아들인다.
            // 글자 짝이 없었으면(빈 목록 — 글자 없는 영상) 가릴 것이 없으니 물을 수 있다. 가르지 못했으면(null — 잰 움직임이 작아 제자리 짝이 글자인지 모른다) 묻지 않는다:
            // 예전엔 둘을 구분하지 않아, 배우지 못한 글자가 남은 채 물어 범위 밖 자세를 떠난 뷰가 글자만으로 다시 받아들일 수 있었다(2026-11-25 — CameraPoseWatch.PickOverlay)
            var overlay = _views.Overlay;
            _leftMasked = ps.Overlay != null && (pts.Count == 0 || pts.Count(p => overlay.Covers(new Point2f((float)p.X, (float)p.Y))) >= 0.8 * pts.Count);
            return note;
        }

        /// <summary>
        /// 잰 움직임 뒤 화면이 자리 잡았다 — 떠난 뷰의 기준 화면이 장면만으로(배운 글자를 넓혀 빼고 — MatchReferenceStrict) 지금 화면을 받아들이면 카메라는 오히려 그 뷰의 자리로 온 것이다: 글자를 배우기 전에
        /// 다른 자세를 글자만으로 그 뷰로 알아보고 거기서부터 지켜봤던 것(또는 쌓인 자세가 어긋난 것). 예전엔 그래도 그 뷰를 빼고 보조 기준을 버려 그 뷰 자신의 자세를
        /// 구역 없는 새 뷰로 등록했다(2026-11-22 검토) → 그 방문에 넣은 보조 기준을 버리고 그 뷰를 후보에 둔다. 움직이기 전 화면을 그 기준 화면이 받아들이지 않으면
        /// (글자만으로 알아본 다른 자세였다) 앞 방문들이 그 자세를 이 뷰로 담은 보조 기준도 버린다 — 예전엔 이번 방문 것만 버려, 두 번 잘못 알아봤으면 앞 방문 것이 남아
        /// 그 자세가 계속 이 뷰가 됐다(2차 검증). 받아들이면 둘 다 그 뷰 자리라 쌓인 자세가 어긋났던 것 — 이번 방문 것만 버린다(그 자세로 붙인 보조 기준이다).
        /// 받아들이지 않으면 떠난 뷰를 빼고(글자가 덜 배워졌어도 글자만으로 그 뷰로 돌아가지 않게) 그 뷰의 보조 기준 가운데 지금 자세를 그 뷰로 받아들이게 하는 것을 조용한 프레임마다 버린다
        /// </summary>
        private string? OnSettledAfterMove(Mat frame, double ms)
        {
            int left = _leftView;
            _leftView = 0;
            _notView = 0;
            var before = _movedBefore;
            _movedBefore = null;
            try
            {
                if (left <= 0) return null;
                if (_leftMasked && AskLeftView(left, frame, before) is { } verdict) return verdict;
                _notView = left;
                _notViewUntilMs = ms + NotViewHoldMs;
                SchedulePurge(left, frame);
                return null;
            }
            finally
            {
                before?.Dispose();
                DropMoveFrames();
            }
        }

        /// <summary>
        /// 잰 움직임으로 떠난 뷰에 '카메라가 정말 떠났나'를 장면만으로 묻는다(글자가 마스크로 덮였을 때만 부른다). after = 움직인 뒤 화면, before = 움직이기 전 화면(없을 수 있다).
        /// 그 뷰의 기준 화면이 after 를 받아들이면 카메라는 오히려 그 뷰의 자리로 왔다 — before 도 받아들이면 쌓인 자세가 어긋났던 것(이번 방문의 보조 기준만 버린다),
        /// 아니면 이번 방문은 글자만으로 알아본 다른 자세였다(이번 방문을 버리고, 앞 방문들이 그 자세를 이 뷰로 담은 보조 기준도 정리하고, 그 방문에서 기억한 평소 수준을 되돌린다).
        /// 받아들이지 않으면 null — 부르는 쪽이 떠난 뷰를 다룬다. 보조 기준에는 묻지 않는다: 글자만으로 알아본 방문이 넣은 보조 기준은 그 다른 자세를 장면째로 받아들인다
        /// (셀프 테스트 10 — 3차에 '다른 방문의 보조 기준이 받아들이면 기준 화면이 낡았을 뿐'으로 보게 했다가 그 보조 기준을 믿어 그 뷰로 되돌아가 뺐다)
        /// </summary>
        private string? AskLeftView(int left, Mat after, Mat? before)
        {
            var back = _views.MatchReferenceStrict(left, after);
            if (_views.Accepts(back))
            {
                bool beforeWasLeft = before != null && _views.Accepts(_views.MatchReferenceStrict(left, before));
                int dropped = _views.DropVisit(left, _leftVisit);
                if (beforeWasLeft)
                    return $"V{left} 의 기준 화면이 움직이기 전·뒤 화면을 모두 받아들임 (인라이어 {back.Inliers}) — 쌓인 자세가 어긋났던 것 (이번 방문의 보조 기준 {dropped}장 버림)";
                RestoreLevel(left, _leftVisit);
                if (before != null) SchedulePurge(left, before);
                return $"V{left} 의 기준 화면이 움직인 뒤 화면을 받아들임 (인라이어 {back.Inliers}) — 이번 방문은 다른 자세를 V{left} 로 알아본 것 (보조 기준 {dropped}장 버림 · 그 자세를 받아들이는 보조 기준 정리)";
            }
            return null;
        }

        /// <summary>
        /// 잰 움직임 뒤 자리 잡기를 기다리는 중에 빠른 전환(cut)·시크가 왔다 — 들고 있는 움직임 직후·직전 화면으로 자리 잡은 뒤와 같은 물음을 한다(<see cref="AskLeftView"/> —
        /// 움직인 직후 화면은 초점이 아직 흐릴 수 있어 받아들이지 못하면 아래처럼 다룬다). 3차 첫 판은 묻지 않고 움직인 직후 화면을 받아들이는 보조 기준을 모두 정리해,
        /// 글자만으로 알아본 방문 끝에 카메라가 그 뷰의 진짜 자리로 온 경우엔 그 뷰의 멀쩡한 보조 기준을 버렸다(3차 검증). 그 뷰의 자리로 온 게 아니면 그 화면을 받아들이는 보조 기준만 정리하되,
        /// 그 뷰의 기준 화면이 정리와 같은 보통 마스크로는 그 화면을 받아들이면 정리하지 않는다 — 넓힌 마스크의 거절은 초점이 아직 흐린 탓일 수 있어, 그 화면을 받아들이는 보조 기준이
        /// 잘못 든 것인지 그 뷰의 멀쩡한 것인지 가를 수 없다(최종 검증: test6 1405.1·1405.25초에서 재생해 1418.6초에 시크하면 넓힌 마스크 60/3·54/3 거절, 보통 마스크 60/6·50/4 수락 —
        /// 그대로 정리하면 1500·1550초의 멀쩡한 보조 기준이 버려졌다). 떠난 뷰를 후보에서 빼지는 않는다 — 전환 뒤 카메라가 어디인지 모른다(돌아왔을 수도 있다. 2차도 빼지 않았다)
        /// </summary>
        private string? OnSettleInterrupted(int left, bool masked, bool cut)
        {
            var settle = _settleFrame;
            _settleFrame = null;
            var before = _movedBefore;
            _movedBefore = null;
            try
            {
                if (settle == null) return null;
                if (masked && AskLeftView(left, settle, before) is { } verdict) return $"움직인 뒤 자리 잡기 전에 {(cut ? "빠른 전환" : "시크")} — {verdict}";
                if (masked && _views.Accepts(_views.MatchReference(left, settle))) return null;
                SchedulePurge(left, settle);
                return null;
            }
            finally
            {
                settle?.Dispose();
                before?.Dispose();
            }
        }

        /// <summary>그 방문이 글자만으로 알아본 다른 자세였다고 드러났다 — 그 방문에서 기억한 그 뷰의 평소 수준(다른 자세의 무늬로 잰 것)을 방문 전 값으로 되돌린다</summary>
        private void RestoreLevel(int view, int visit)
        {
            if (_levelSnapView != view || _levelSnapVisit != visit) return;
            if (_levelSnap < 0) _levels.Remove(view);
            else _levels[view] = _levelSnap;
            // 지켜보기의 수준도 되돌린다 — 멈춘 지켜보기가 그 방문의 수준을 아직 '확인된' 것으로 들고 있어, 같은 Update 의 다음 SaveLevel(StartWatching 첫 줄)이
            // 그대로 다시 적었다(최종 검증). 다음 StartWatching 이 어차피 그 뷰의 수준으로 다시 정한다
            _pose.SetLevel(_levelSnap < 0 ? 0 : _levelSnap, _levelSnap >= 0);
        }

        /// <summary>잰 움직임 앞뒤로 들고 있던 작은 흑백 그림을 놓는다</summary>
        private void DropMoveFrames()
        {
            _movedBefore?.Dispose();
            _movedBefore = null;
            _settleFrame?.Dispose();
            _settleFrame = null;
        }

        /// <summary>지켜보던 뷰의 평소 수준을 기억한다 — 확인된 것만(글자만 맞은 '제자리'일 수 있는 수준은 아니다), 그 뷰에서 본 가장 큰 것.
        /// 작게 기억하면(연기 낀 때) 다음 방문의 첫 확인이 글자만 맞아도 무너짐으로 못 잡고, 크게 기억해 맑은 날 수준이 연기 낀 방문에 쓰이면 한 번 따라갔다 그 자리로 이을 뿐이다</summary>
        private void SaveLevel()
        {
            if (_lastWatched <= 0 || !_pose.LevelTrusted) return;
            int level = _pose.Level;
            if (!_levels.TryGetValue(_lastWatched, out int old) || level > old) _levels[_lastWatched] = level;
        }

        /// <summary>
        /// 화면 전환 감지가 빠른 전환을 알렸다 — 지켜보기를 멈추기 전에 앞 화면과 이 화면을 맞춘다. 앞 화면과 평소만큼 제자리로 맞으면 카메라는 그대로다(가까이 선 큰 차 · 경광등 —
        /// 아무것도 배우지 않는다). 아니고 제자리 짝이 화면 고정 글자로 보이면(<see cref="ViewRegistry.TextLike"/>) 배운다 — 빠른 프리셋 이동에서도 글자를 배우게(예전엔 천천히 한
        /// 움직임을 잰 때만 배워, 모든 프리셋에 같은 글자가 있으면 빠른 이동 뒤 글자만으로 앞 뷰로 알아봤다). 글자가 덮였으면 떠난 뷰의 기준 화면에 장면만으로(넓힌 글자 마스크) 묻는다 —
        /// 이 화면을 받아들이는데 전환 앞 화면은 받아들이지 않으면 카메라는 그 뷰의 진짜 자리로 왔다(앞 방문이 글자만으로 그 뷰로 알아본 다른 자세였다 — 그 방문과 그 자세의 보조 기준을 버린다).
        /// 둘 다 받아들이면 앞 방문은 멀쩡했다(예전엔 이때도 버렸다, 2차 검증). 받아들이지 않거나 글자가 덮이지 않았으면 떠난 뷰를 다음 식별 후보에서 뺀다 —
        /// 그 뷰의 보조 기준은 믿지 않는다(글자만으로 알아본 방문이 넣은 것일 수 있다 — 셀프 테스트 10. 3차에 '후보로 두되 애매함에서만 빼기'로 바꿨다가 되돌림)
        /// </summary>
        private string? OnCut(Mat frame, double ms)
        {
            _notView = 0;   // 앞 움직임의 빼 두기는 새 전환으로 끝난다 — 돌아온 뷰를 빼지 않게
            if (!_pose.Active || _viewNow <= 0) return null;
            if (_pose.Cut(frame) is not { } c) return null;
            if (c.Still) return "빠른 전환이지만 앞 화면과 평소만큼 제자리로 맞음 — 카메라는 그대로(내용 변화)";
            if (!_views.TextLike(c.Pinned, c.Features, c.Width, c.Height)) return null;
            bool learned = _views.LearnOverlay(c.Pinned, c.Width, c.Height);
            var overlay = _views.Overlay;
            bool masked = c.Pinned.Count(p => overlay.Covers(new Point2f((float)p.X, (float)p.Y))) >= 0.8 * c.Pinned.Count;
            string what = $"빠른 전환 앞뒤의 제자리 짝 {c.Pinned.Count} 을 화면 고정 글자로 봄{(learned ? $" — 배움 (칸 {overlay.Cells})" : "")}";
            if (masked)
            {
                var back = _views.MatchReferenceStrict(_viewNow, frame);
                if (_views.Accepts(back))
                {
                    using var pre = _pose.KeyFrame480();
                    if (pre != null && _views.Accepts(_views.MatchReferenceStrict(_viewNow, pre)))
                        return $"{what} · 전환 앞뒤 화면을 모두 V{_viewNow} 의 기준 화면이 받아들임 — 그대로";
                    // 이번 방문만 버린다 — 앞 방문들의 보조 기준까지 정리하던 것은 3차에 넣었다 뺐다: 잰 움직임이 없는 빠른 전환에선 앞 화면을 거절한 까닭이
                    // 다른 자세인지 낡은 기준 화면인지 가를 수 없어, 낡은 기준 화면이면 그 뷰의 멀쩡한 보조 기준을 모든 방문에서 버릴 수 있었다(3차 검증)
                    int dropped = _views.DropVisit(_viewNow, _visit);
                    RestoreLevel(_viewNow, _visit);
                    return $"{what} · 전환 뒤 화면을 V{_viewNow} 의 기준 화면이 받아들임 — 앞 방문은 다른 자세였음 (보조 기준 {dropped}장 버림)";
                }
            }
            _notView = _viewNow;
            _notViewUntilMs = ms + NotViewHoldMs;
            return what;
        }

        private void SchedulePurge(int viewId, Mat frame)
        {
            PurgeSome(int.MaxValue);   // 앞에 남은 정리가 있으면 마저
            try { _purge = _views.BeginPurge(viewId, frame); }
            catch (Exception) { _purge = null; }
        }

        private int PurgeSome(int maxChecks)
        {
            if (_purge == null) return 0;
            int removed = 0;
            bool failed = false;
            try { removed = _views.PurgeStep(_purge, maxChecks); }
            catch (Exception) { failed = true; }   // 정리는 덤 — 실패해도 식별은 그대로 (잘못 든 보조 기준이 남을 뿐)
            if (failed || _purge.Done)
            {
                _purge.Dispose();
                _purge = null;
            }
            return removed;
        }

        /// <summary>뷰 처리의 한 단계를 감싼다 — 실패해도 Update 의 나머지(화면 전환 처리·다시 알아보기)는 그대로 가게. 실패하면 그 사유를 돌려준다</summary>
        private static string? Guard(Func<string?> step, string what)
        {
            try { return step(); }
            catch (Exception ex) { return $"{what} 오류: {ex.Message}"; }
        }

        public void Dispose()
        {
            _pose.Dispose();
            _purge?.Dispose();
            _purge = null;
            DropMoveFrames();
        }
    }
}
