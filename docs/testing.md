# UI 테스트 — 파일별 설명 · 지난 전체 통과 기록 · 테스트가 확인하지 않는 것

> CLAUDE.md 에서 옮긴 절이다(2026-11-05 — CLAUDE.md 가 매 세션 컨텍스트를 너무 많이 차지해서 내용은 글자 그대로 옮기고, CLAUDE.md 에는 같은 제목으로 요약·꼭 지킬 규칙만 남겼다).
> 본문의 '위'·'아래'·'○○ 절'이 가리키는 곳은 이 문서나 CLAUDE.md, 또는 다른 `docs/` 문서다 — CLAUDE.md 의 '자세한 문서' 표에서 찾는다. 새로 알게 된 실측·경과는 여기에 더한다.

## 테스트

`tests/ui/` — 실제 앱을 띄워 UI Automation 으로 조작하는 회귀 테스트. 단위 테스트는 아직 없다.

```bash
pwsh tests/ui/Run-All.ps1
```

- 기본 대상은 `bin\Debug` 아래 **가장 최근에 빌드된** `DetectView.exe`(먼저 `dotnet build`). TFM 이 바뀌면 출력 폴더 이름도 바뀌어서 고정 경로를 쓰지 않는다. `-ExeDir publish` 로 배포본도 검사한다.
  `-ExeDir` 의 상대 경로는 저장소 루트 기준이고 `.\`·끝 `\` 는 정규화한다(2026-10-30 — 예전엔 `.\publish` 에서 `Test-NpuSwitch` 미러의 파일 이름이 잘리고 `Test-ScreenHud` 의 exe 경로 비교가 틀렸다)
- 테스트는 요소를 **`x:Name`(UIA AutomationId)** 으로 찾고, [파일 열기]만 버튼 글자로 찾는다(11-01 개편부터 `Btn_LoadVideo` 이름이 있지만 테스트는 글자 그대로).
  버튼 아이콘은 `Content` 가 아니라 `Ui.Glyph` 로 그린다 — `Content` 를 문자열이 아닌 것으로 바꾸면 UIA 이름이 비어 버튼을 못 찾는다. 툴팁도 문자열로 둘 것(그래야 UIA HelpText 로 읽힌다). 상태 문구·버튼 글자·툴팁 문구·창 제목 형식도
  검사 대상이다 — 이름·문구를 바꾸면 테스트도 같이 고칠 것 (`Btn_RefreshCameras` 처럼 코드에선 안 쓰고 테스트만 쓰는 이름도 있다)
- `Measure-Fps.ps1 -ExeDir <폴더>`: 테스트가 아니라 A/B 계측. test1.mp4 재생 중 `Txt_InferenceFps`(프레임 읽기 + 처리 시간의 역수)를 워밍업 3초 뒤 15초 동안 읽어 중앙값을 낸다. 5회 반복.
  같은 조건에서도 실행 묶음끼리 수 FPS 흔들려서(한 번은 86~93, 다음엔 96~99) **기준선을 앞뒤로 두 번** 잰다
- 필요: GPU(DirectML), `publish\test1.mp4`(gitignore 대상이라 저장소엔 없음). 실행 중 DetectView 창이 떴다 닫히고 바탕화면을 캡처한다.
  NPU 전환 검사는 AMD NPU 와 exe 옆 짝 모델(`best.npu.onnx`)이 있을 때만 돈다 (Debug 는 빌드가 루트 짝을 복사, 배포 폴더는 `tools\Make-NpuModel.ps1`)
- `Test-FilePlayback.ps1`: **일부러 exe 폴더가 아닌 작업 디렉터리로** 띄워 모델 경로 회귀를 막는다. 파일 열기 → **재생 속도(0.9~1.1배속)** → SAHI → **멈춘 채 시크 → 한 장 표시(검출 수에서 "(SAHI)" 가 빠짐) → 그 프레임으로 SAHI** → SAHI 뒤 슬라이더 활성 →
  **재생 중 시크 직후 SAHI → 그 위치를 분석했는지(완료 문구 끝 (mm:ss))**(2026-10-30 추가 — 시크 뒤 35ms 에 누른다, 아래 '테스트가 확인하지 않는 것') → EOF → 재생 시 처음부터 →
  **다시 EOF 에서 슬라이더로 옮기면 '재생 완료'가 사라지고 그 위치부터 재생**(2026-10-30 추가) → 정지, 단계마다 버튼 글자·활성 상태.
  **클래스별 검출 수 범례**(2026-11-01 추가 — 검출 수 칸의 UIA `ItemStatus` 를 `Get-ClassCounts` 로 읽어 더한 값이 '검출 N' 인지): 재생 중(두 값을 읽는 사이 프레임이 바뀔 수 있어
  N 을 앞뒤로 읽어 같을 때 견주고 맞을 때까지 다시 읽는다 — `Test-ClassCountsMatch`) · SAHI 뒤 · 멈춘 채 시크한 한 장 · 그 SAHI 뒤, 정지하면 빈다. 항목 수 24 → 29.
  **추적 번호**(2026-11-02 추가 — 검출 수 칸의 UIA `ItemType` 을 `Get-TrackMarks` 로 읽는다): 재생 중 번호가 박스마다 하나씩(번호 수 = '검출 N', 서로 다름 — `Test-TrackMarksMatch`) ·
  0.25초 간격으로 읽은 두 프레임에 같은 번호가 있다(이어짐) · SAHI 뒤엔 번호가 없다 · 멈춘 채 시크한 한 장도 박스마다 번호 · 정지하면 비는 것. 항목 수 29 → 34
- `Test-Tracking.ps1`(2026-11-02): test4.mp4(0~10.5초 차로 한가운데 고장 정차한 덤프트럭)로 추적기를 본다 — 재생 2초 지점에 '정지'가 붙은 번호가 있다 → 5초 지점에 그 번호가 그대로 정지(3초 동안 같은 번호로 이어짐) →
  일시정지 → **앞으로 시크(8초) 뒤 번호가 모두 새로 매겨진다**(그때까지 본 번호보다 큼 — 시크할 때 추적기를 비우지 않으면 정지 트랙의 번호가 그대로 남아 작다) →
  재개하면 시크 프레임에서 시작한 번호가 이어져 1초 뒤 정지로 나온다 → 정지하면 번호가 빈다. 항목 수 9.
  **판별 확인(A/B)**: 시크 처리 프레임의 `Reset()` 만 뺀 변종 빌드는 '앞으로 시크 → 번호를 새로 매김' 하나만 실패한다(시크 뒤 번호 `#14 정지 · #31 …` — 멈춘 트럭이 시크 앞뒤로 #14 를 그대로 가졌다), 나머지 8개는 통과
- `Test-Roi.ps1`(2026-11-03): 관심 구역 편집(위 "관심 구역"). test4.mp4 로 **실제 마우스 입력**(`SetCursorPos`+`mouse_event` — 이 테스트 동안 포인터가 움직인다)과 키(Delete·Esc·Enter)로 조작하고, 바뀔 때마다 영상 옆
  `test4.mp4.roi.json` 을 읽어 점 좌표(±0.012)까지 본다 — 소스 없음·재생 중엔 편집이 막히고 사유가 툴팁에 → 일시정지 → [추가] → 점 4개 + 첫 점 클릭으로 닫기(`Z1 차로`, 파일 lane · 점 4개가 찍은 자리) →
  점 끌기 → 변 가운데 ○ 끌기(점 5개) → Delete(4개) → 점을 안 고르고 Delete(그대로) → 유형 갓길(파일·목록 이름) → 그리다가 Esc · 그리다가 재생(편집이 막히고 그리던 점 버림) → 구역 지우기(목록·파일이 빔, 파일 삭제) →
  다시 그리면 번호 Z2(지운 번호를 안 씀 · 유형은 마지막에 쓴 갓길 · nextId 3)·Enter 로 닫기(점 3개) → 정지(패널이 비고 편집이 막힘, 파일은 남음) → 같은 영상을 다시 열면 구역이 돌아옴 → 깨진 파일(상태바 사유·빈 구역·`.bad`).
  끝나면 영상 옆 구역 파일을 지운다(`publish` 는 gitignore). 그림(색·모양)은 UIA 로 안 보여 보지 않는다 — 스크린샷으로 손으로 봤다. 점 끌기는 `ZoneLayer` 의 UIA 영역(영상 칸)으로 좌표를 옮긴다. 항목 수 30
- `Test-SceneChange.ps1`(2026-11-03): 화면 전환 감지(위 "화면 전환 감지"). 재생하면서 0.03초 간격으로 배지(`Txt_SceneBadge` — 숨겨져 있으면 UIA 에서 사라짐)·위치·번호(검출 수 칸 UIA `ItemType`)를 기록해 그 열을 본다
  (두 값을 읽는 사이 프레임이 넘어갈 수 있어 배지를 번호 앞뒤로 읽어 같은 표본만 센다). **test4**(7초로 시크해 15초까지): 돌기 전(10.4초 전) 배지 없음 + 멈춘 덤프트럭에 정지 번호 → 도는 동안 `화면 이동 중` 배지
  (10.6~11.4초), 박스는 보이되 번호 없음 → 안정된 뒤 `● 새 뷰 V2 · 구역 없음` 배지(11.5~13.5초 — 2b 이후, 그전엔 `화면 전환 · 번호 새로 매김`)와 돌기 전 최대 번호보다 큰 새 번호 → 13.9초부터 배지 없음·번호 있음(13.43초의 한 프레임 튐은 전환 아님) → 정지하면 배지 사라짐.
  **test5**(27초로 시크해 32.6초까지): 검은 끊김 동안(29.7~30.1초) `화면 끊김` 배지 — 이동 중·전환 배지는 한 번도 안 뜸, 끊김 앞뒤로 정지 번호(`#2`)가 그대로, 끊김 뒤 배지 없이 번호가 다시 붙음. 항목 수 21.
  전환 때 추적기를 비우는 줄 자체는 이 테스트로 못 가른다(위 '확인' — 그 줄을 뺀 변종도 통과) — 추적기 단위 selftest ⑨ 가 본다
- `Test-ZoneEvents.ps1`(2026-11-03): 구역 진입·나감 이벤트 로그(위 "구역 진입·나감 이벤트"). test4.mp4 옆에 구역 파일(차로 Z1 하나 — 멈춘 덤프트럭의 발 위치를 품는다)을 미리 써 두고(끝나면 지움) 연다 — 소스 없음엔 패널이 없고 열면 접힌 헤더 한 줄
  → 펼치면 목록 → 처음 1초 안에 `Z1 차로 이미 안에 있음` 으로 잡힌 트랙 중 도는 구간(10.5초)까지 나가지 않은 것이 있다(멈춘 트럭 `#14`; 같이 잡힌 지나가던 차는 곧 `나감`) → 카메라가 돈 뒤 `화면 전환 → 새 뷰 V2 (구역 없음)` 표시가 한 번(11.5초) 남고
  그 뒤 이벤트는 없다 → 헤더 개수 = 표시를 뺀 항목 수 → `CSV 저장`: 영상 옆 events.csv(헤더·줄 수 = 항목 + 1·initial 행·view_changed 한 행)와 상태바 문구 → 5초로 시크하면 멈춘 트럭이 다시 기록된다(멈췄던 이벤트가 풀림) →
  `비우기`(목록·개수 0, 저장 꺼짐) → 접으면 목록이 사라짐 → 정지하면 패널이 사라짐 → 구역 없는 영상(test1)은 4초 재생해도 0건. 로그 항목의 시각 범위·개수는 영상·검출에 따라 달라 성질만 본다. 항목 수 21
- `Test-Incident.ps1`(2026-11-03): 유고 규칙(위 "유고 규칙"). 구역 파일(차로 Z1 — 0~10.5초 멈춘 덤프트럭의 발 위치를 품음)을 test4 옆에 써 두고(끝나면 지움) 12.6초까지 재생하며 0.03초 간격으로 위치·헤더의 유고 표시(`Txt_EventIncident`)·박스 번호의 `정지 유고` 를 기록한다
  (두 값을 읽는 사이 프레임이 넘어갈 수 있어 표시를 번호 앞뒤로 읽어 같은 표본만 센다): 5초 전 표시·강조 없음 + 멈춘 트럭엔 `정지` → 선언 뒤(7~10.3초) 헤더 `⚠ 유고 후보 1`·박스 번호 `#14 정지 유고`(같은 번호가 정지이기도 함) →
  처음 보인 위치 5.0~7.0초(실측 5.2초) → 카메라가 돈 뒤(11.7초~) 표시·강조가 사라짐 → 로그: 유고 후보 행 한 줄(5~9초)·같은 번호의 `유고 해소 (화면 전환) · 멈춤 10.3초` 행
  (라벨은 그 순간의 클래스라 같은 차가 TRUCK → BUS 로 바뀔 수 있어 번호로 맞춘다 — 11-05 배포 모델에서 실제로 그랬다) → CSV: `incident`(Z1 · lane · 멈춤 5000ms)·`incident_end` 각 한 행 →
  비우고 2초로 시크해 재생하면 7~9.3초(실측 7.3초)에 다시 선언 → 선언 중에 다시 2초로 시크하면 `유고 해소 (시크)` 행. 항목 수 16.
  2026-11-07 CSV 끝에 VLM 열 셋이 붙어 incident 행 끝이 빈 열 넷(`,,,,` — note · vlm · vlm_cues · vlm_desc)이 됐다 (`Test-ZoneEvents` 의 헤더 검사도 같이)
- `Test-VlmAnnotation.ps1`(2026-11-07): VLM 설명(위 "VLM 설명" — docs/vlm-annotation.md). 진짜 Ollama 없이 **Debug 빌드의 가짜 VLM**(`DETECTVIEW_TEST_FAULTS` — TestFaults.cs)으로 앱을 세 번 띄운다.
  `Test-Incident` 와 같은 구역 파일을 test4 옆에 써 두고(끝나면 지움) 연 뒤 로그를 펼친다(접힌 목록은 UIA 에 없다). ① 결함 `vlm-fake`: [VLM 설명] 이 있고 기본 꺼짐(버튼 ItemStatus `꺼짐`)·툴팁에 '참고용' →
  켜면 `켜짐` + 상태바 → 유고 후보 행(약 5.2초)의 ItemStatus 가 `분석 중` → 0.6초 뒤 `단서: 출동 차량, 차로 위 사람, 앞에 선 차 — 시험용 가짜 VLM 답 — 그림 512×512`(가짜가 받은 JPEG 를 풀어 크기를 적는다 —
  실험과 같은 크롭이 갔다는 뜻) → 행의 UIA 이름은 `Test-Incident` 와 같음 · 행 툴팁(HelpText)에 모델·설명·'참고용' → CSV 헤더 끝 `vlm,vlm_cues,vlm_desc` · incident 행 `done` · 단서 키 셋 · 설명 →
  끄면 `꺼짐` + 상태바, 끝난 행의 설명은 남음 → 비우고 2초로 시크해 다시 선언된 행엔 VLM 줄 없음(ItemStatus 빈칸) ② `vlm-down`: 켜지고 유고 후보 행은 `분석 중 → 실패 — Ollama 연결 안 됨 …`, 툴팁 '다시 시도'
  ③ `vlm-absent`: 켜기가 실패해 `꺼짐` 그대로 + 상태바 'VLM 설명을 켜지 못함 — Ollama 가 꺼져 있음'. 항목 수 27(Debug — 앱을 세 번 띄우는 모델 로드·파일 열기 12 포함).
  배포본(Release)은 가짜 VLM 이 컴파일되지 않아 ① 의 기본 꺼짐·툴팁만(6). 진짜 Ollama 는 손으로 본다(docs/vlm-annotation.md '구현과 확인')
- `Test-ViewIdentity.ps1`(2026-11-03): 화면 식별(위 "화면 식별"). `TrackerCheck mkrevisit` 이 test4 조각을 이어 만든 `experiments\TrackerCheck\out\test4_revisit.mp4`(없으면 테스트가 만든다 — `publish\test4.mp4` 가 필요)에 옛 형식 구역 파일(판 1, 차로 Z1 — 트럭의 발 위치)을 옆에 써 두고 30초를 재생하며
  0.03초 간격으로 배지·구역 헤더(`Txt_ZoneCount`)·유고 표시를 기록한다(세 값을 위치 앞뒤로 읽어 같은 표본만 쓴다): A 0~8초 헤더 `V1 · 1개`·배지 없음·유고 후보 → B 8초 컷 `● 새 뷰 V2 · 구역 없음`·헤더 `V2 · 0개`·유고 표시 없음·배지는 2초 뒤 사라짐 →
  A 14초 `● 뷰 V1 · 아는 화면`·헤더 `V1 · 1개`·유고 후보가 다시 선언 → C 22초 `● 새 뷰 V3 · 구역 없음` → B 26초 `● 뷰 V2 · 아는 화면`. [CSV 저장]으로 로그를 읽는다(UIA 목록은 오래된 행이 안 보일 수 있다): 화면 전환 표시 네 줄(8.2·14.2·22.2·26.2초)·유고 후보 둘(5.2·19.4초)·해소(화면 전환) 둘.
  이어서 멈춘 채 B 의 한 장(28초)에서 실제 마우스로 구역을 그려 파일(판 2 — 구역마다 view·뷰 목록 셋과 기준 화면 JPEG·nextView 4)을 확인하고, 정지 → 다시 열면 같은 뷰로 알아보고 구역이 그린 뷰에서만 보인다(11초 `V2 · 1개`·4초 `V1 · 1개`·24초 `V3 · 0개`). 항목 수 27
- `Test-VfrPlayback.ps1`: 길이 정보 없는 60fps webm(`media/vfr60_nodur.webm`, 0.9 MB, 저장소에 포함)으로 VFR 경로 전체 —
  백그라운드 길이 분석, 재생 속도, 앞·뒤 시크(grab·다시 열기), **재생 중 뒤로 시크 직후 SAHI → 그 위치 분석**(2026-10-30 추가 — 14초를 넘긴 뒤 12.5초로 되감고 35ms 뒤에 누른다), EOF 뒤 처음부터. 이 파일은 OpenCV 로 만든 60fps VP8 webm 의
  Segment Info `Duration`(0x4489) 요소를 같은 길이의 `Void`(0xEC)로 덮은 것이다 (필요하면 같은 방법으로 다시 만든다).
  두 번째 파일 `media/vfr_fps1000_dur.webm`(0.86 MB, 저장소에 포함 — 길이는 있고 FPS=1000 으로 읽히는 VFR, 6~12초 정지 구간)으로 이슈 #7 경로를 본다:
  분석 완료·길이·배속, 앞(16초)·뒤(13초) 시크 위치와 그 뒤 배속, 정지 구간(9초)으로 시크(9.4~10.5초 — 9.4 아래면 CFR 경로로 열린 것), 멈춘 채 13.5초 시크 → SAHI `(00:13)`,
  EOF 뒤 처음부터. 만든 방법은 `media/make_vfr_fps1000_dur.py` 머리말(PyAV VP8, 누적 지터 시각, TrackEntry DefaultDuration 을 Void 로 덮음, 판 고정 명령). 항목 수 13 → 26
- 배속은 슬라이더(영상 시각)가 벽시계 2초 동안 나아간 양으로 잰다(`Measure-PlaybackRate`). 슬라이더는 길이를 안 뒤에만 움직인다
- `Test-ScreenHud.ps1`: HUD 시작 → 캡처 방식 → 화면 받음 → 일시정지/재개 → 카메라 [새로고침]에도 HUD 상태 문구 유지 → 정지 → **HUD 에서 정지 없이 곧바로 파일 열기(오버레이가 닫히는지)**.
  메인 창을 주 모니터(HUD 기본 대상)로 옮겨 둔다. 오버레이는 UIA 로 믿을 만하게 안 보여서 이 프로세스의 **보이는 클릭 통과(`WS_EX_TRANSPARENT`) 최상위 창 중 제목이 `DetectView Overlay` 인 것**을
  Win32 로 센다 — 열린 WPF 툴팁도 클릭 통과 최상위 창이라(11-01 프로브), 제목을 안 보던 때는 사용자가 PC 를 쓰는 중 포인터가 메인 창 위에 머물면 '2개'로 실패했다. 영상은 `-Video`(기본 `publish\test1.mp4`).
  진단 값: `Txt_DetectionCount` 의 HelpText `오버레이 박스 N · 재개 재검출 M · 캡처 재연결 K · 받은 화면 F`(그리지 않는 UIA 속성이라 DXGI 새 화면을 만들지 않는다).
  일시정지 → 박스 0, 재개 → **M 증가**(DXGI 만 — 캡처가 새 화면이 아니라 마지막 화면을 다시 준 것만 세므로 재개 버튼 글자가 만든 새 화면으로는 안 늘어난다. GDI 는 [INFO])·박스 복귀
  (일시정지 전 박스가 없으면 박스 검사는 [INFO]). 다시 줄 화면이 있어야 해서 먼저 F > 0 을 확인한다(없으면 일시정지·재개로 버튼 글자를 바꿔 한 장 받게 한다).
  클래스별 검출 수 범례(2026-11-01 추가): 오버레이는 UIA·스크린샷으로 안 보여 검출 수 칸의 UIA `ItemStatus`(오버레이가 알린 범례 글자)로 본다 — 그 합 = 진단 값의 오버레이 박스 수
  (`Test-HudLegendMatch`, 박스 수를 앞뒤로 읽어 견줌), 일시정지 → 빔·재개 → 돌아옴(일시정지 전 박스가 있을 때, 없으면 [INFO]), 정지 → 빔(정지 전 범례가 있을 때).
  추적 번호(2026-11-02 추가, 검출 수 칸의 UIA `ItemType`): 번호 수 = 진단 값의 오버레이 박스 수·서로 다름(`Test-HudTrackMatch`), 일시정지 → 빔·재개 → 돌아옴(박스가 있을 때), 정지 → 빔(정지 전 번호가 있을 때).
  HUD 대상 모니터에 물체가 없으면 범례는 빈 채로만 보인다 — 오버레이 범례 경로까지 보려면 검출할 장면을 주 모니터에 띄워 두고 돌린다
  (11-01: test4 4초에서 멈춘 DetectView 를 주 모니터에 최대화 — 테스트의 메인 창이 (40,40) 1200×820 이라 영상 오른쪽 절반이 보인다).
  Debug 빌드면 결함 주입(`TestFaults.cs`) 셋을 앱을 따로 띄워 돌린다: hud-create-fail → 오류 창 → 닫힘 → 소스 없음 · hud-capture-lost → '캡처 재연결' 1·DXGI 그대로·FPS 이어짐·오버레이 그대로·
  다시 만든 캡처가 새 화면을 줌(F 증가) · hud-detect-fail → FPS 칸 `검출 오류 (재시도 중)`(툴팁에 사유) → 풀림. 환경 변수는 처음 값을 기억해 결함 없는 실행엔 지우고 띄울 때마다 되돌린다.
  GDI 대체는 환경 문제일 수 있어 실패가 아닌 [INFO] (사유가 없으면 실패).
  하이브리드 GPU 안내면 툴팁에 exe 경로·연결 GPU 가 있는지와 권한 설정이 지금 설정(레지스트리)과 맞는지, DXGI 면 툴팁이 비었는지도 본다. 툴팁은 UIA 에서 `HelpText` 로 읽힌다.
  절전 설정이 `publish` 경로에만 있어서 경로에 따라 검사되는 쪽이 다르고, 어느 쪽이 DXGI 인지는 HUD 대상 모니터에 따라 뒤집힌다(위 "반대 사례").
  2026-10-24(모니터 0 = 외부 2560×1440)엔 Debug·scratchpad 가 DXGI, '절전'이 걸린 `publish` 가 GDI 였다.
  같은 날 저녁 사용자가 배포본 설정을 'Windows에서 결정'(`GpuPreference=0`)으로 바꾼 뒤엔 배포본도 외부 모니터에서 DXGI(HUD 108 FPS)
- `Test-NpuSwitch.ps1`: ① 최소 폭(900)에서 조작 요소(11-01 부터 왼쪽 패널)가 모두 창 안에 ② "추론 장치" 콤보에서 NPU 선택 → 상태바 진행 표시 → 제목 NPU →
  파일 재생(배속·검출) → 재생 중 GPU 로 → 다시 NPU(캐시) — 재생이 끊기지 않는지
  ②b **TensorRT**(2026-11-08 — FP16 짝 `best.fp16.onnx`): 지금 장치(NPU 를 거쳤으면 NPU)에서 곧바로 TensorRT → 진행 표시·창 응답(①② 250 ms 선) → 엔진 캐시 키(FP16 짝 MD5·EP·런타임) →
  재생(배속·검출) → 멈춘 프레임 두 곳(3.0·6.5초)의 검출 수를 GPU 와 견줌(±1 — 두 곳을 보는 건 엔진이 옛 입력을 다시 쓰는 잘못을 가리려고) → SAHI(작업 스레드의 추론) →
  키 폴더에 엔진 캐시 파일 → 재생 중 다시 TensorRT(캐시 — '엔진 만드는 중'이 없어야). NVIDIA RTX 가 아니거나 짝이 없으면 [INFO] 로 건너뛴다
  ③ 실패 경로 6가지 — NPU·TensorRT 각각 짝 없음·다른 모델의 짝·FP32 를 짝 이름으로 — 에서 그 항목이 막히고 툴팁에 사유, GPU 유지. ③ 은 exe 폴더를 **하드 링크로 본뜬 옆 폴더(`<exe 폴더>.npu-test`, gitignore)** 에서 파일을 바꾼다 —
  원래 폴더(배포 폴더 포함)는 건드리지 않고 끝나면 지운다. "다른 모델의 짝"은 MD5 가 다른 모델(루트·배포 폴더 `best.onnx`, 그다음 배포 폴더의 다른 `*.onnx` — 짝 `*.npu.onnx` 는 빼고)이 있어야 해서 없으면 [INFO].
  배포 폴더의 다른 모델을 후보에 넣은 건 2026-11-06 — 루트와 배포 폴더가 같은 YOLO26m 640×384 가 되어 옛 YOLO26s 를 `best.yolo26s-0917.onnx` 로 보관했는데, 후보가 둘뿐이면 이 경우가 늘 건너뛰어졌다.
  **NPU(VitisAI 실행 공급자)가 없는 PC 거나 짝 모델 파일이 없으면 ② 를 [INFO] 로 건너뛴다**(①·③ 은 NPU 없이도 돈다).
  짝이 낡았거나(`best.onnx` 를 바꾸고 짝을 다시 안 만듦)·양자화 모델이 아니거나·확인이 예외로 끝나면 **FAIL** —
  예전엔 사유를 가리지 않고 건너뛰어, 짝이 낡은 배포본도 '모두 통과'였다(2026-10-30). 캐시가 없으면 ② 의 첫 전환이 1분 넘게 걸린다(제한 240초).
  `-ColdCancel` 을 주면 NPU 컴파일 캐시를 지우고 컴파일 도중 취소까지 본다(2분 넘게 걸려 `Run-All` 엔 넣지 않음).
  ② 는 **①② 동안 창 응답**도 본다(이슈 #4): `UiLatency` 가 50 ms 마다 WM_NULL 왕복(`SendMessageTimeout`)과 UIA 읽기(WPF 는 UIA 요청을 UI 스레드에서 답한다)를 재고,
  ③ 이 아닌 첫 문구('NPU 확인 중'·'… 내려받는 중'이 아닌 것 — 끝남·실패 포함)에서 `Mark()`. 둘 다 1회 이상 재었고 실패 0·최대 < 250 ms 여야 통과(첫 전환·재생 중 다시 전환 둘 다,
  시작 때와 ③ 포함 전체는 [INFO]). 또 NPU 툴팁의 `컴파일 캐시 키:` 를 읽어 세 조각(MD5 = exe 옆 짝, EP 버전 또는 lib 대체, ORT = 앱에 올라온 `onnxruntime.dll` 파일 버전)과
  그 키 폴더에 파일이 있는지 본다(`%LOCALAPPDATA%` 와 Claude 패키지 쪽 둘 다) — 예전 검사는 캐시 폴더 목록만 봐서 한 번 생긴 폴더로 늘 통과했다.
  옛 판 `UiLatency` 형식이 남은 PowerShell 세션에선 분명한 메시지로 멈춘다(Add-Type 형식은 못 바꿈 — 새 pwsh 에서 돌릴 것)
- 종료 코드 = 실패 건수(`Run-All` 은 0/1). 파일 열기 대화상자 자동화 요령 등은 `Common.ps1` 머리말에 정리해 두었다.
  `Run-All` 은 테스트 파일이 try 밖에서 throw 해도(영상·빌드 없음) 그 파일만 실패로 세고 다음 파일을 돈다(2026-10-30 — 예전엔 거기서 통째로 멈췄다)
- 마지막 전체 실행: 2026-11-08 저녁, **Debug** — **추론 장치에 TensorRT 를 넣은 뒤**(`MainWindow.Trt.cs` · 장치 전환 상태 기계 일반화 · FP16 짝 `best.fp16.onnx` `D7D760DC…`).
  worktree 에 `publish` 연결을 두고 `Run-All` 한 번에 305 항목 중 304 통과(5.2분): `Test-FilePlayback` 34 · `Test-Tracking` 9 · `Test-Roi` 30 · `Test-SceneChange` 21 · `Test-ZoneEvents` 21 ·
  `Test-Incident` 15/16 · `Test-VlmAnnotation` 27 · `Test-ViewIdentity` 27 · `Test-VfrPlayback` 26 · `Test-ScreenHud` 39 · **`Test-NpuSwitch` 55**(TensorRT 2b 와 실패 경로 셋을 더함 — 그 전 28).
  남은 1건은 `Test-Incident` 의 '카메라가 돈 뒤 유고 표시가 사라짐' — 남은 표본은 0개(맞음)인데 11.7초 뒤 표본이 7개로 기준(8)에 하나 모자랐다(그 실행의 전체 표본 101개 — 평소 118~119).
  그 파일만 두 번 다시 돌리니 표본 10개로 16 항목 모두 통과 (이 테스트는 GPU 로만 돈다 — 11-07 `Test-ViewIdentity` 와 같은 표본 수 흔들림).
  TensorRT: NPU 에서 곧바로 6.2초(앞서 따로 돌린 실행이 엔진 캐시를 남김 — 캐시 없는 첫 전환은 그 실행에서 16.9초), ①② 창 응답 WM_NULL 최대 1 ms · UIA 최대 23~32 ms,
  멈춘 프레임 3.0·6.5초의 검출이 GPU 와 클래스까지 같음(11 · 13개), SAHI 19개, 다시 고르면 6.2초(캐시). 참고용 재생 처리 GPU 65.5 · **TensorRT 137.6** · NPU 36.2 FPS
- 그 전 전체 통과: 2026-11-08 오후, **배포본 `publish`** — 사용자 "배포하기"로 8클래스 모델판 `79a041a` 를 배포(작업 트리가 깨끗한 worktree 에서 임시 폴더로 내보내 exe 만 옮김 —
  132,590,524 바이트, Release DLL 에 결함·가짜 VLM 이름 없음 — 남은 건 읽히지 않는 환경 변수 이름 상수 `DETECTVIEW_TEST_FAULTS` 뿐으로 11-07 판과 같은 소스). 배포 폴더 모델도 8클래스(`AB56CED7…`)로 바꾸고
  짝을 `Make-NpuModel.ps1 -Model publish\best.onnx` 로 만들어(39초) 루트 짝과 바이트까지 같다(`546567CB…`). 옛 7클래스 YOLO26m(`94A26C12…`)과 짝(`BD18D32C…`)은 `best.yolo26m-1105.onnx` · `.npu.onnx` 로 보관.
  `Run-All -ExeDir publish` 한 번에 243 항목 모두 통과(5.2분): `Test-FilePlayback` 34 · `Test-Tracking` 9 · `Test-Roi` 30 · `Test-SceneChange` 21 · `Test-ZoneEvents` 21 · `Test-Incident` 16 ·
  `Test-VlmAnnotation` 6(Release — 기본 꺼짐·툴팁만) · `Test-ViewIdentity` 27 · `Test-VfrPlayback` 26 · `Test-ScreenHud` 25(DXGI 1920×1080, 결함 주입은 Release 라 빠짐) · `Test-NpuSwitch` 28.
  NPU 전환 4.9초(아래 Debug 실행이 남긴 캐시), GPU 로 되돌리기 0.7초, ①② 창 응답 WM_NULL 최대 1 ms · UIA 최대 10~23 ms. 참고용 재생 처리 GPU 66.5 · NPU 36.8 FPS, HUD 90.0 FPS
- 그 전: 같은 날 오후, **Debug** — **루트 모델을 8클래스 YOLO26m 640×384 로 바꾼 뒤**(pickup 추가 — CLAUDE.md "RF-DETR 검토", NPU 짝 `546567CB…`).
  worktree 에 `publish` 연결을 두고 `Run-All` 한 번에 278 항목 모두 통과(6.8분): `Test-FilePlayback` 34 · `Test-Tracking` 9 · `Test-Roi` 30 · `Test-SceneChange` 21 · `Test-ZoneEvents` 21 ·
  `Test-Incident` 16 · `Test-VlmAnnotation` 27 · `Test-ViewIdentity` 27 · `Test-VfrPlayback` 26 · `Test-ScreenHud` 39 · `Test-NpuSwitch` 28. 테스트는 차량 라벨 패턴에 PICKUP 만 더했다(`$veh`·CSV 패턴) —
  창 제목 `YOLO26m 640×384 (2026-11-08 학습)`, 덤프트럭은 그대로 차로 유고 후보(`TRUCK`). 새 짝이라 NPU 첫 전환은 컴파일해 65.1초, 캐시 키 `546567CB…_1.8.80.0_1.27.20260711.1.7b7c0e2`,
  ①② 창 응답 WM_NULL 최대 1 ms · UIA 최대 6~57 ms. 참고용 재생 처리 GPU 76.8 · NPU 39.0 FPS. 테스트 영상엔 pickup 이 범례에 잡히는 장면이 없어,
  test5 를 31.5·34.3·42.5초에 멈춰 눈으로 봤다 — 범례 `TRUCK 2 · PICKUP 1` 처럼 truck 아래 줄, 박스·라벨은 자홍 `Box.pickup`(scratchpad 캡처 — 실제 CCTV 라 저장소엔 없음)
- 그 전: 2026-11-07 저녁, **배포본 `publish`** — VLM 설명판 `4c05f5f` 로 다시 배포(작업 트리가 깨끗한 worktree 에서 임시 폴더로 내보내 exe 만 옮김 — 132,590,495 바이트,
  배포 폴더 모델·NPU 짝은 그대로 `94A26C12…` · `BD18D32C…`, Release DLL 에 가짜 VLM·결함 이름 없음). worktree 에 `publish` 연결을 두고 `Run-All -ExeDir publish` 한 번에 251 항목 모두 통과:
  `Test-FilePlayback` 34 · `Test-Tracking` 9 · `Test-Roi` 30 · `Test-SceneChange` 21 · `Test-ZoneEvents` 21 · `Test-Incident` 16 · **`Test-VlmAnnotation` 6(새 파일 — Release 라 기본 꺼짐·툴팁만)** ·
  `Test-ViewIdentity` 27 · `Test-VfrPlayback` 26 · `Test-ScreenHud` 33(DXGI 2560×1440, 결함 주입은 Release 라 빠짐) · `Test-NpuSwitch` 28.
  NPU 전환 5.1초(캐시), 준비 동안 창 응답 WM_NULL 최대 1~2 ms · UIA 최대 13~15 ms. 참고용 재생 처리 GPU 69.0 · NPU 40.9 FPS, HUD 84.4 FPS (UI 테스트의 FPS 는 한 번 읽은 값이라 벤치마크가 아니다).
  배포본으로 진짜 Ollama 도 손으로 봤다 — 관설3교 사고 장면에서 Debug 와 같은 단서 넷·같은 설명(7.3초), 창을 닫으니 모델이 내려감 (docs/vlm-annotation.md '구현과 확인')
- 그 전: 같은 날 저녁, **Debug** — VLM 설명(`MainWindow.Vlm.cs` 등 — docs/vlm-annotation.md)을 넣은 뒤 worktree 에 `publish` 연결을 두고 `Run-All` 한 번에 278 항목 중 277 통과:
  `Test-FilePlayback` 34 · `Test-Tracking` 9 · `Test-Roi` 30 · `Test-SceneChange` 21 · `Test-ZoneEvents` 21 · `Test-Incident` 16 · **`Test-VlmAnnotation` 27(새 파일)** · `Test-ViewIdentity` 27 ·
  `Test-VfrPlayback` 26 · `Test-ScreenHud` 39 · `Test-NpuSwitch` 28. 남은 1건은 `Test-ViewIdentity` 의 'A 로 돌아옴: 헤더 V1 · 1개' — 값은 표본 28개 모두 맞았는데 표본 수 기준(30)에 2개 모자랐다
  (같은 실행 초반에 TrackerCheck 를 빌드해 PC 가 바빴다). 그 파일만 다시 돌리니 표본 38개로 27 항목 모두 통과
- 그 전 전체 통과: 2026-11-06 아침, **배포본 `publish`** — `a378a58`(루트 모델 YOLO26m 640×384 · SAHI 타일 = 모델 입력 크기 · 창 제목에 입력 크기)로 다시 배포하고,
  사용자 승인으로 **배포 폴더 모델도** 같은 YOLO26m 640×384(`94A26C12…`)로 바꿨다. 옛 YOLO26s(09-17, `87D0E1CB…`)와 그 짝(`D889EA23…`)은 `best.yolo26s-0917.onnx` · `best.yolo26s-0917.npu.onnx` 로
  이름을 바꿔 옆에 두었다. 짝은 `Make-NpuModel.ps1 -Model publish\best.onnx` 로 만들어(33초) 루트 짝과 바이트까지 같다(`BD18D32C…`).
  exe 126.4 MB(132,573,874 바이트 — 작업 트리가 깨끗한 `a378a58` 에서 임시 폴더로 내보내 exe 만 옮김), Release DLL 에 결함 이름 없음.
  `Run-All -ExeDir publish` 한 번에 237 항목 모두 통과: `Test-FilePlayback` 34 · `Test-Tracking` 9 · `Test-Roi` 30 · `Test-SceneChange` 21 · `Test-ZoneEvents` 21 · `Test-Incident` 16 ·
  `Test-ViewIdentity` 27 · `Test-VfrPlayback` 26 · `Test-ScreenHud` 25(DXGI 1920×1080, 결함 주입은 Release 라 빠짐) · `Test-NpuSwitch` 28.
  '다른 모델의 짝'은 루트와 배포 폴더가 같은 모델이 되어 보관한 옛 YOLO26s 로 돌았다(이날 후보에 넣음 — 위 `Test-NpuSwitch` 설명). NPU 전환은 아래 Debug 실행이 남긴 캐시로 6.7초,
  다시 고르면 6.9초, ①② 창 응답 WM_NULL 최대 1~2 ms · UIA 최대 11~18 ms. 참고용 재생 처리 GPU 59.7 · NPU 37.1 FPS, HUD 69.2 FPS
  (UI 테스트의 FPS 는 한 번 읽은 값이라 벤치마크가 아니다 — 같은 날 `Measure-Fps` 로 잰 같은 코드의 Release 는 77.5 · 78.0)
- 그 전: 2026-11-06 아침, Debug — **루트 모델을 YOLO26m 640×384 로 바꾼 뒤**(CLAUDE.md "RF-DETR 검토" 3단계 — SAHI 타일을 모델 입력 크기로, 창 제목에 입력 크기, NPU 짝 `BD18D32C…`).
  `Run-All` 한 번에 `Test-FilePlayback` 34 · `Test-Tracking` 9 · `Test-Roi` 30 · `Test-SceneChange` 21 · `Test-ZoneEvents` 21 · `Test-Incident` 16 · `Test-ViewIdentity` 27 · `Test-VfrPlayback` 26 ·
  `Test-ScreenHud` 39(DXGI — 이번엔 모니터 0 이 1920×1080, 결함 주입 포함) · `Test-NpuSwitch` 28 항목 = 251 모두 통과. 테스트는 고치지 않았다 — 모델이 바뀌어도 멈춘 덤프트럭은 차로 유고 후보로 잡혔고
  (`⚠ TRUCK #2 Z1 차로 유고 후보`), SAHI 는 640×384 타일로 '정밀 분석 완료: 16개'. `Test-NpuSwitch` 의 '다른 모델의 짝' 경로는 배포 폴더 모델을 써서 제목이 `YOLO26s 640×640 (2026-09-17 학습)` 으로 보였다(640×640 모델의 제목 꼴도 확인).
  새 짝이라 NPU 첫 전환은 컴파일해 64.2초, 다시 고르면 4.7초, GPU 로 되돌리기 0.6초, 캐시 키 `BD18D32C…_1.8.80.0_1.27.20260711.1.7b7c0e2`, ①② 창 응답 WM_NULL 최대 1 ms · UIA 최대 13~22 ms.
  참고용 재생 처리 GPU 75.9 · NPU 39.5 FPS, HUD 91.3 FPS(1920×1080). 앱·TrackerCheck·NpuBench·RfDetrBench·RuntimeParity(Old·WinML) 빌드 경고 0.
  배포본(`publish`)은 이때 아직 바꾸기 전이었다(배포 폴더 모델은 사용자가 바꿔 쓰는 것이라 물어본 뒤 위처럼 바꿨다)
- 그 전: 2026-11-05 낮, **배포본 `publish`** — `f863585`(앱 YOLO 레터박스를 OpenCV 쌍선형으로 — `YoloLetterbox`, 위 '입력 축소' · RF-DETR 실험)로 다시 배포.
  exe 126.4 MB(132,573,736 바이트) — worktree 의 루트 `best.onnx` 가 배포 폴더 것보다 새것이라 임시 폴더로 내보내고 exe 만 바꿨다(위 '빌드 · 배포'). 배포 폴더 `best.onnx`(09-17 visible, `87D0E1CB…`)는 그대로,
  `best.npu.onnx` 는 쌍선형 보정으로 다시 만들었다(`5CE87F90…` → `D889EA23…`, 100장 27초). Release DLL 에 `YoloLetterbox` 가 있고 결함 이름은 없다.
  worktree 에 `publish` 연결을 두고 `Run-All -ExeDir E:\DetectView\publish` 한 번에 237 항목 중 236 통과 — `Test-Incident` 1건 실패: 멈춘 덤프트럭이 카메라가 돌기 시작하는 10.5초에 BUS 로 잡혀
  해소 행이 `BUS #10 … 유고 해소 (화면 전환)` 였는데 테스트가 라벨을 CAR·TRUCK 만 받았다(앱은 맞다 — 추적기는 차량 6종을 한 묶음으로 본다). 테스트를 라벨 대신 같은 번호로 맞추게 고친 뒤
  `Test-Incident` 만 배포본·Debug 로 다시 돌려 둘 다 16 항목 통과(Debug 루트 모델은 끝까지 `TRUCK #9`). 나머지: `Test-FilePlayback` 34 · `Test-Tracking` 9 · `Test-Roi` 30 · `Test-SceneChange` 21 · `Test-ZoneEvents` 21 ·
  `Test-ViewIdentity` 27 · `Test-VfrPlayback` 26 · `Test-ScreenHud` 25(DXGI, 결함 주입은 Release 라 빠짐) · `Test-NpuSwitch` 28. 새 짝이라 NPU 첫 전환은 컴파일해 55.1초, 다시 고르면 5.1초,
  캐시 키 `D889EA23…_1.8.80.0_1.27.20260711.1.7b7c0e2`, ①② 창 응답 WM_NULL 최대 1 ms · UIA 최대 6 ms. 참고용 재생 처리 GPU 98.9 · NPU 47.6 FPS, HUD 117.1 FPS. 앱·RfDetrBench·TrackerCheck 빌드 경고 0
- 그 전: 2026-11-05 오전, Debug — **레터박스를 OpenCV 쌍선형으로 직접 만드는 `YoloLetterbox` 를 넣은 뒤**(위 '화면 HUD' 의 '입력 축소' ②). 아래 판과 같게 `publish` 연결을 두고 돌렸다.
  `Run-All` 한 번에 `Test-FilePlayback` 34 · `Test-Tracking` 9 · `Test-Roi` 30 · `Test-SceneChange` 21 · `Test-ZoneEvents` 21 · `Test-Incident` 16 · `Test-ViewIdentity` 27 · `Test-VfrPlayback` 26 ·
  `Test-ScreenHud` 39(DXGI, 박스 없음, 결함 주입 포함) · `Test-NpuSwitch` 28 항목 = 251. 멈춘 덤프트럭은 앱에서도 `#9`(유고 후보 행 `⚠ TRUCK #9 Z1 차로 유고 후보`, 선언 5.2초 그대로).
  NPU 전환 4.7초(캐시)·다시 고르면 5.1초, 캐시 키 `0B58A84E…`, ①② 창 응답 WM_NULL 최대 1 ms · UIA 최대 6~12 ms. 참고용 재생 처리 GPU 99.7 · NPU 50.2 FPS, HUD 114.2 FPS.
  앱·TrackerCheck·NpuBench·RuntimeParity(Old·WinML) 빌드 경고 0. 스크린샷으로 모양을 보지 않았다(박스 좌표만 반 화소 안팎 바뀐다). 배포본(`publish`)은 이 변경 전 판 그대로다
- 그 전: 2026-11-05 아침, Debug — **YOLO 입력 축소를 쌍선형으로 바꾸고 NPU 짝을 다시 만든 뒤**(위 '입력 축소' ①). worktree 에서 돌려 `publish` 를 본 checkout 의 배포 폴더로 잇는
  디렉터리 연결(junction)을 두고 돌렸다(테스트가 `<저장소>\publish\test*.mp4` 를 찾는다 — 끝나고 연결만 지움). `Run-All` 한 번에 `Test-FilePlayback` 34 · `Test-Tracking` 9 · `Test-Roi` 30 · `Test-SceneChange` 21 ·
  `Test-ZoneEvents` 21 · `Test-Incident` 16 · `Test-ViewIdentity` 27 · `Test-VfrPlayback` 26 · `Test-ScreenHud` 39(DXGI, 박스 없음, 결함 주입 포함) · `Test-NpuSwitch` 28 항목 = 251 — 항목 수가 바꾸기 전과 같다
  (멈춘 덤프트럭 번호는 루트 모델에서 14 → 9 로 바뀌었지만 테스트는 번호를 박지 않는다). 새 짝이라 NPU 첫 전환은 컴파일해 67.6초, 다시 고르면 4.8초, 캐시 키 `0B58A84E…_1.8.80.0_1.27.20260711.1.7b7c0e2`,
  ①② 창 응답 WM_NULL 최대 1 ms · UIA 최대 11~12 ms. 참고용 재생 처리 GPU 91.1 · NPU 47.7 FPS, HUD 103.4 FPS. `TrackerCheck selftest` 통과, 앱·TrackerCheck·NpuBench·RuntimeParity(Old·WinML) 빌드 경고 0.
  스크린샷으로 모양을 보지 않았다(축소 방식은 화면 그리기와 상관없다). 배포본(`publish`)은 이 변경 전 판 그대로다
- 그 전(배포본): 2026-11-03 낮, **폴더 이전·이름 정리 뒤 새 위치의 배포본 `E:\DetectView\publish`** — `E:\VS_E\YOLO11_Test` → `E:\DetectView`, `YOLO11_Test.csproj`·`.slnx` → `DetectView.csproj`·`.slnx`, 네임스페이스 `YOLO11_Test` → `DetectView`,
  `publish_gpu_ver` → `publish`(위 '이름')로 다시 publish. 기능은 그대로고 이름·경로만 바뀌었다 (exe 126.4 MB(132,572,351 바이트), 모델 두 개 MD5 그대로(`87D0E1CB…`·`5CE87F90…`), Release DLL 에 옛 네임스페이스 문자열·결함 이름 없음).
  `Run-All -ExeDir publish` 한 번에 `Test-FilePlayback` 34 · `Test-Tracking` 9 · `Test-Roi` 30 · `Test-SceneChange` 21 · `Test-ZoneEvents` 21 · `Test-Incident` 16 · `Test-ViewIdentity` 27 · `Test-VfrPlayback` 26 · `Test-ScreenHud` 25 · `Test-NpuSwitch` 28 항목 = 237
  (재생 처리 GPU 99.4 · NPU 48.8 FPS·HUD 122.0 FPS 는 참고용, NPU 캐시 키 `5CE87F90…_1.8.80.0_1.27.20260711.1.7b7c0e2` — 경로와 상관없어 옛 컴파일 캐시를 그대로 썼다). 이어서 Debug `Test-ScreenHud` 39(결함 주입 포함) 통과.
  콘솔: 앱·TrackerCheck·NpuBench·RuntimeParity(Old·WinML) 빌드 경고 0, `TrackerCheck selftest` 통과(실제 영상은 새 `RepoPaths.VideoDir` 로 저장소 루트의 `publish` 에서 읽는다 — 예전엔 절대 경로가 박혀 있었다).
  **옮긴 것**: 소스·`.git`(기록 전체·`old-history` 태그·`pre-push` 훅·noreply 작성자 설정 — 복사 뒤 `git status` 가 옛 저장소와 같았다)·`old/`(숙제 폴더·zip·bundle·docs. bin/obj/publish 는 뺌)·배포 폴더(모델·테스트 영상 포함)·`experiments/TrackerCheck/out` 의 CSV.
  **안 옮긴 것**: bin/obj·실험 도구 빌드 결과·`experiments/NpuBench/out`(519 MB, 다시 만들 수 있음)·`graft/` 캐시(첫 질의 때 다시 만든다)·옛 worktree 둘(`.claude/worktrees/…` 변경 없음 확인 — 브랜치는 새 저장소에도 있고 작업 폴더는 옛 폴더에만 남는다)·합성 revisit 영상(`TrackerCheck mkrevisit`).
  옛 폴더 `E:\VS_E\YOLO11_Test` 는 그때 세션의 작업 폴더라 지우지 않았다 — 확인한 뒤 사용자가 지운다. 전역 `~/.claude/settings.json` 의 신뢰 저장소 경로 4줄과 Claude 메모리(`E--DetectView` 프로젝트 키)를 새 경로로 옮겼다(사용자 승인).
  Windows 그래픽 설정은 새 exe 경로에 없다(위 '이름' — 'Windows에서 결정'과 같은 동작이고 새 `publish` 에서 HUD 는 DXGI 로 돌았다). 스크린샷으로 모양을 보지 않았다.
- 그 전(배포본): 2026-11-03 오후, **배포본 `publish`** — `main`(`10f7b31`, 관심 구역·화면 전환 감지·구역 이벤트 로그·작은 물체 박스 조정·유고 규칙·화면 식별 — 앞 배포 `3939f40` 은 옛 기록의 해시라 `main` 의 조상이 아니다)으로 다시 배포
  (exe 126.4 MB(132,572,382 바이트), pdb·lib·dll 없음, 배포 폴더 `best.onnx`(09-17 visible, MD5 `87D0E1CB…` — 루트(09-11)보다 새것이라 `PreserveNewest` 가 덮어쓰지 않음)·`best.npu.onnx`(`5CE87F90…`)·테스트 영상 그대로, 그래픽 설정 `GpuPreference=0` 그대로.
  Release DLL 에 새 코드(`ViewRegistry`·`WarmUp`·`IncidentDetector`·`SceneChangeDetector`)가 있고 결함 이름(`hud-*-fail`)은 없음 — `strings` 가 없어 파이썬으로 바이트를 찾았다).
  `Run-All -ExeDir publish` 한 번에 `Test-FilePlayback` 34 · `Test-Tracking` 9 · `Test-Roi` 30 · `Test-SceneChange` 21 · `Test-ZoneEvents` 21 · `Test-Incident` 16 · `Test-ViewIdentity` 27 · `Test-VfrPlayback` 26 ·
  `Test-ScreenHud` 25(DXGI, 박스 없음, 결함 주입은 Release 라 빠짐) · `Test-NpuSwitch` 28 항목 = 237 (NPU 첫 전환 4.0초(캐시), ①② 창 응답 WM_NULL 최대 1 ms · UIA 최대 7~9 ms,
  캐시 키 `5CE87F90…_1.8.80.0_1.27.20260711.1.7b7c0e2` — 올라온 `onnxruntime.dll` 과 같음). 테스트가 찍은 재생 처리 GPU 95.3 · NPU 45.8 FPS, HUD 114.2 FPS(참고용).
  HUD 대상 모니터에 물체가 없어 오버레이 범례·번호는 빈 채로만 봤다(이번 변경은 HUD 를 건드리지 않아 test4 장면을 띄운 재검사는 안 했다). 테스트가 남긴 구역·이벤트 파일 없음.
- 그 전: 2026-11-03 오후, Debug — 화면 식별(2b, 위 "화면 식별")을 더한 뒤. 사용자가 PC 를 쓰는 중에 돌렸다(`Test-Roi`·`Test-ViewIdentity` 는 실제 마우스를 움직인다). `Run-All` 한 번에 `Test-FilePlayback` 34 · `Test-Tracking` 9 · `Test-Roi` 30 ·
  `Test-SceneChange` 21 · `Test-ZoneEvents` 21 · `Test-Incident` 16 · **`Test-ViewIdentity` 27(새 파일)** · `Test-VfrPlayback` 26 · `Test-ScreenHud` 39(DXGI, 박스 없음, 결함 주입 포함) · `Test-NpuSwitch` 28 항목 = 251
  (재생 처리 NPU 49.1 · GPU 96.9 FPS·HUD 112.4 FPS 는 참고용, ①② 창 응답 WM_NULL 최대 1 ms · UIA 최대 11 ms). `TrackerCheck selftest` 통과(새 ⑫ 화면 식별 11개 포함). Debug 빌드 경고 0.
  **고친 과정**: 첫 전체 실행은 2건 실패 — `Test-ViewIdentity` 표본 수 기준(150)이 빡빡했고(147개, 기준을 100 으로) `Test-VfrPlayback` 첫 '재생 속도'가 0.86배(두 번 더 돌려도 0.80·0.89 — 화면 식별을 넣기 전엔 안 나던 것, 첫 `Identify` 의 네이티브 초기화 지연 — 앱을 켤 때 워밍업하도록 고침, 위).
  2b 로 문구가 바뀐 기존 테스트 기대값은 고쳤다(구역 개수 칸 `V1 · 1개`, 전환 배지·표시 행 글자, CSV 머리 `…dwell_ms,note` 와 incident 행 끝의 빈 note 열). 합성 revisit 영상의 화면 전환 표시는 8.2·14.2·22.2·26.2초, 돌아온 A 의 유고는 19.4초(= 14.2 + 5.2)에 다시 선언됐다.
  스크린샷으로 모양을 보지 않았다(UI 테스트는 UIA 글자만 본다). (배포본은 같은 날 `10f7b31` 로 다시 배포 — 위)
- 그 전: 2026-11-03, Debug — 유고 규칙(위 "유고 규칙")을 더한 뒤. 사용자가 PC 를 쓰는 중에 돌렸다(`Test-Roi` 는 실제 마우스를 움직인다). `Run-All` 한 번에 `Test-FilePlayback` 34 · `Test-Tracking` 9 · `Test-Roi` 30 ·
  `Test-SceneChange` 21 · `Test-ZoneEvents` 21 · **`Test-Incident` 16(새 파일)** · `Test-VfrPlayback` 26 · `Test-ScreenHud` 39(DXGI, 박스 없음, 결함 주입 포함) · `Test-NpuSwitch` 28 항목 = 224
  (재생 처리 NPU 49.5 · GPU 93.8 FPS 는 참고용, ①② 창 응답 WM_NULL 최대 1 ms · UIA 최대 7 ms). `TrackerCheck selftest` 통과(새 ⑪ 유고 규칙 9개 포함). Debug 빌드 경고 0.
  시험한 빌드와 이 기록 사이에 바뀐 건 `IncidentRules.cs` 의 문서 주석뿐이고 다시 빌드해 경고 0 을 봤다. `Test-ZoneEvents` 의 로그엔 이제 유고 후보·해소 행이 섞여 나오지만(예: `유고 해소 (화면 전환)`) 그 검사들은 고치지 않고 통과한다.
  스크린샷으로 빨간 박스(`TRUCK #14 유고? 0.96`)·헤더 `⚠ 유고 후보 1`·로그의 빨간 행을 봤다. (배포본은 같은 날 `10f7b31` 로 다시 배포 — 위)
- 그 전: 2026-11-03, Debug — 구역 진입·나감 이벤트 로그(위 "구역 진입·나감 이벤트")를 더한 뒤. 사용자가 PC 를 쓰는 중에 돌렸다. `Run-All` 한 번에 `Test-FilePlayback` 34 · `Test-Tracking` 9 · `Test-Roi` 30 · `Test-SceneChange` 21 ·
  `Test-VfrPlayback` 26 · `Test-ScreenHud` 39 · `Test-NpuSwitch` 28 통과, **`Test-ZoneEvents`(새 파일)는 21 중 '비우기' 1건 실패** — 시크 뒤 재생 중에 비웠더니 비운 직후에도 새 이벤트가 쌓여 0건을 못 봤다(테스트의 경쟁 조건 —
  앱 동작은 맞다). 멈춘 채 비우도록 고쳐 따로 3번 연속 통과(21). 합계 208. `TrackerCheck selftest` 통과(새 ⑩ 구역 이벤트 10개 포함). Debug 빌드 경고 0.
  스크린샷으로 로그 모양을 봤다(접힘: 재생 줄 아래 헤더 한 줄, 펼침: 시각 · `CAR #6`(클래스 색) · `Z1 차로`(구역 색) · 동작). 스크린샷 캡처는 `CopyFromScreen` 이 창을 흰색으로 찍는 일이 자주 있었다(앱은 응답 중, 일시정지·앞으로 올리기·재시도로도 안 풀림 — **사용자가 짚었다: 노트북 덮개를 닫은 상태일 때 그렇다**. 화면이 꺼진 상태에선 데스크톱을 베끼는 캡처가 창 내용을 못 얻는다. 덮개가 열려 있을 때의 정상 캡처와 맞아떨어진다)
  `PrintWindow(hwnd, hdc, PW_RENDERFULLCONTENT)` 로 바꾸니 매번 정상이다. (배포본은 같은 날 `10f7b31` 로 다시 배포 — 위)
- 그 전: 2026-11-03, Debug — 화면 전환 감지(위 "화면 전환 감지")를 더하고 구역 안내 문구(구역이 없으면 일시정지해도 안내를 안 띄움)를 정리한 뒤. 사용자가 PC 를 쓰는 중에 돌렸다(`Test-Roi` 는 실제 마우스를 움직인다).
  `Run-All` 한 번에 `Test-FilePlayback` 34 · `Test-Tracking` 9 · `Test-Roi` 30 · **`Test-SceneChange` 21(새 파일)** · `Test-VfrPlayback` 26 · `Test-ScreenHud` 39(DXGI, 박스 없음, 결함 주입 포함) · `Test-NpuSwitch` 28 항목 = 187
  (다시 NPU(캐시) 6.1초, ①② 창 응답 WM_NULL 최대 1 ms · UIA 최대 44 ms). `TrackerCheck selftest` 통과(새 ⑨ 화면 전환 감지 14개 포함). Debug 빌드 경고 0.
  스크린샷으로 배지 모양을 봤다(일시정지해 찍음 — 도는 중엔 호박색 `● 화면 이동 중`에 박스는 번호 없이, 안정된 직후엔 파란 `● 화면 전환 · 번호 새로 매김`에 새 번호 `#40`…). 재생 중 캡처는 창이 흰색으로 찍혔다(앱은 응답 중 — 캡처 쪽 문제, 일시정지 뒤 창을 앞으로 올리고 찍으면 정상).
  (배포본은 같은 날 `10f7b31` 로 다시 배포 — 위)
- 그 전: 2026-11-03, Debug — 관심 구역(ROI) 편집을 더한 뒤(위 "관심 구역"). 사용자가 PC 를 쓰는 중에 돌렸다(`Test-Roi` 는 실제 마우스를 움직인다). `Run-All` 한 번에
  `Test-FilePlayback` 34 · `Test-Tracking` 9 · **`Test-Roi` 30(새 파일)** · `Test-VfrPlayback` 26 · `Test-ScreenHud` 39(DXGI, 박스 없음, 결함 주입 포함) · `Test-NpuSwitch` 28 항목 = 166
  (NPU 전환 7.2초(캐시), 다시 고르면 5.5초, ①② 창 응답 WM_NULL 최대 1 ms · UIA 최대 20 ms). `TrackerCheck selftest` 통과(새 ⑧ 관심 구역 22개 포함).
  이어서 구역 읽기·저장의 예외 처리를 넓힌 최종 빌드(구역은 부가 기능이라 어떤 이유로든 영상 열기를 막지 않게)로 `Test-Roi` 30 · `Test-FilePlayback` 34 를 다시 돌려 통과. Debug 빌드 경고 0.
  스크린샷(구역 4개 · 하나 선택 · 새 구역 그리는 중)으로 모양을 봤다 — 시안 그대로이고 기본 창 높이 820 에서 패널 전체('추론 장치' 콤보까지)가 스크롤 없이 보인다
  (처음엔 목록 아래 파일 이름 줄 때문에 그 콤보가 스크롤 아래로 밀려 줄였다). (배포본은 같은 날 `10f7b31` 로 다시 배포 — 위)
- 그 전(배포본): 2026-11-02 오후, **배포본 `publish`** — `main`(`3939f40`, 추적기 연결 · 박스·라벨 투명도와 작은 물체 라벨·꺾쇠 축소 · 상태바 칸 고정)으로 다시 배포
  (exe 126.4 MB, pdb·lib·dll 없음, 배포 폴더 `best.onnx`(09-17 visible, MD5 `87D0E1CB…`)·`best.npu.onnx`(`5CE87F90…`) 그대로, 그래픽 설정 `GpuPreference=0` 그대로.
  Release DLL 에 새 코드(`UpdateTagged`·`CornerPens`·`StatusFpsHudWidth`)가 있고 결함 이름 문자열은 없음).
  `Run-All -ExeDir publish` 한 번에 `Test-FilePlayback` 34 · `Test-Tracking` 9(배포 모델로도 2초 정지 번호 → 5초 같은 번호 → 시크 뒤 새 번호 → 재개 뒤 정지) ·
  `Test-VfrPlayback` 26 · `Test-ScreenHud` 25(DXGI, 박스 없음, 결함 주입은 Release 라 [INFO]) · `Test-NpuSwitch` 28 항목 = 122
  (NPU 전환 6.1초(캐시), 다시 고르면 5.5초, ①② 창 응답 WM_NULL 최대 1 ms · UIA 최대 3 ms, 재생 중 다시 전환 3 · 21 ms, 캐시 키 `5CE87F90…_1.8.80.0_1.27.20260711.1.7b7c0e2`).
  HUD 대상 모니터에 물체가 없어 test4 4초 장면(Debug DetectView 를 멈춰 최대화)을 띄우고 `Test-ScreenHud -ExeDir publish` 를 다시 돌려 33 항목 통과
  (오버레이 범례 `CAR 5` = 오버레이 박스 5, 번호 `#1~#5` → 일시정지에 빔 → 재개하면 `#6~#10` 으로 새로 매긴 뒤 정지 표시 → 정지에 빔).
  테스트가 찍은 재생 처리 GPU 97.1 · NPU 50.4 FPS, HUD 123.6 FPS(참고용)
- 그 전: 2026-11-02 오후, Debug — 상태바 FPS·검출 칸 폭을 고정한 뒤(위 '화면 HUD' 절의 상태바 항목). `Run-All` 한 번에
  `Test-FilePlayback` 34 · `Test-Tracking` 9 · `Test-VfrPlayback` 26 · `Test-ScreenHud` 39(DXGI, 박스 없음, 결함 주입 포함) · `Test-NpuSwitch` 28 항목 = 136
  (NpuSwitch ① '최소 폭에서 조작 요소가 창 안에' 포함). 칸 위치는 따로 UIA 로 쟀다 — 4초 동안 0.2초마다 FPS·해상도·검출 칸 왼쪽 끝이 고치기 전 3~5가지 → 1가지,
  최소 폭 상태 문구 칸 577px(가장 긴 HUD 문구 568px). 이어서 FPS 칸 폭을 모드별로(파일·카메라 50 · HUD 114) 바꾼 뒤에도 136 통과, 칸 위치 1가지, 최소 폭 상태 문구 칸 HUD 575px·파일 645px.
  Debug 빌드 경고 0. (배포본은 같은 날 `3939f40` 으로 다시 배포 — 위)
- 그 전: 2026-11-02 오후, Debug — 작은 물체의 꺾쇠도 줄인 뒤(위 "화면 디자인"). `Run-All` 한 번에
  `Test-FilePlayback` 34 · `Test-Tracking` 9 · `Test-VfrPlayback` 26 · `Test-ScreenHud` 39(DXGI, 박스 없음, 결함 주입 포함) · `Test-NpuSwitch` 28 항목 = 136 (UI 테스트는 그림을 보지 않는다 —
  그리기가 예외 없이 도는지만 본다). 모양은 바꾸기 전 판(`815e847` 을 scratchpad 에 빌드)과 **같은 프레임**(일시정지한 채 test4 3초·26초로 옮겨 한 장 보기)의 스크린샷을 확대해 견줬다
  — 먼 차의 꺾쇠가 1px·짧아져 차가 보인다. 그 앞(투명도·라벨 축소, `815e847`)도 같은 136 통과, test4 3초·26초 재생 중 스크린샷으로 견줌. Debug 빌드 경고 0.
  `Measure-Fps` 는 재지 않았다 — 그리는 횟수·글자 만들기는 같고 박스마다 배율 계산 한 번만 더했다. (배포본은 같은 날 `3939f40` 으로 다시 배포 — 위)
- 그 전: 2026-11-02, Debug — 추적기를 파일·카메라·화면 HUD 에 연결한 뒤(위 "추적기" 절). 사용자가 PC 를 쓰는 중에 돌렸다.
  `Run-All` 한 번에 `Test-FilePlayback` 34(추적 번호 `#1 · #2 · #3 정지 …` = 검출 10 등) · `Test-Tracking` 9(새 파일 — 2초 `#14 정지` → 5초 같은 번호 → 8초 시크 뒤 `#31 …` 새 번호 → 재개 뒤 정지) ·
  `Test-VfrPlayback` 26 · `Test-ScreenHud` 39(DXGI, 박스 없음, 결함 주입 포함) · `Test-NpuSwitch` 28 항목 = 136(NPU 전환 내내 창 응답 WM_NULL 최대 2~4 ms · UIA 최대 17~42 ms).
  HUD 대상 모니터에 물체가 없어 번호가 빈 채로만 봤기에, test4 4초에서 멈춘 DetectView 를 주 모니터에 최대화해 두고 `Test-ScreenHud` 를 다시 돌려 47 항목 통과
  (오버레이 번호 `#1 · #2 · #3` = 오버레이 박스 3, 일시정지에 빔·재개하면 `#4 · #5 · #6` 으로 새로 매김(재개 때 비움)·정지에 빔).
  `Measure-Fps`(test1.mp4, 5회 중앙, Debug, 사용자가 PC 를 쓰는 중): 추적 전 90.2 → 후 89.3 → 전 82.8 — 새 판이 앞뒤 기준선(7.4 흔들림) 사이라 떨어지지 않았다 (옛 판은 `471b6d4` 를 scratchpad 에 따로 빌드).
  `TrackerCheck selftest` 통과(새 장면 ⑦: `UpdateTagged` 의 번호·정지가 `Update` 와 같고 좌표·라벨은 그대로 · 같은 화면이 100fps 3초·5초 간격이어도 ID 1개에 정지로 한 번만 바뀐 채 수렴 ·
  시계를 안 자르면 프레임마다 트랙이 만료돼 번호 60개). Debug 빌드 경고 0. 스크린샷으로 라벨 모양 확인 — 멈춘 트럭 `TRUCK #14 정지 0.97`, 움직이는 차는 번호만, SAHI 결과엔 번호 없음.
  빽빽한 장면(test4 25초 신호대기 행렬 39대)에선 라벨이 길어져 서로 겹친다 — 시안을 고를 때 예고한 대가다.
  **HUD 정지 화면 고리는 직접 못 쟀다**: 이 PC 는 화면이 늘 움직여(이전 판·새 판 모두 받은 화면이 초당 27~32장) '정지 화면에서 받은 화면이 안 느는지'로는 재지 못했다 — 위 selftest(추적기 출력 수렴)로 대신 봤다.
  (배포본은 같은 날 `3939f40` 으로 다시 배포 — 위)
- 그 전: 2026-11-01 오후, **배포본 `publish`** — `main`(`fb06cf9`, 클래스별 검출 수 범례)으로 다시 배포
  (exe 126.4 MB, pdb·lib·dll 없음, 배포 폴더 `best.onnx`(09-17 visible, MD5 `87D0E1CB…`)·`best.npu.onnx`(`5CE87F90…`) 그대로, 그래픽 설정 `GpuPreference=0` 그대로.
  Release DLL 에 범례 코드(`LegendTextChanged`·`검출 · SAHI`)가 있고 결함 이름 문자열은 없음).
  `Run-All -ExeDir publish` 한 번에 `Test-FilePlayback` 29(범례 `CAR 2 · PERSON 15` = `검출 17 (SAHI)` 등) · `Test-VfrPlayback` 26 ·
  `Test-ScreenHud` 24(DXGI, 박스 없음, 결함 주입은 Release 라 [INFO]) · `Test-NpuSwitch` 28 항목(NPU 전환 4.7초(캐시), 다시 고르면 5.2초,
  ①② 창 응답 WM_NULL 최대 2 ms · UIA 최대 20 ms, 재생 중 다시 전환 2 · 20 ms, 캐시 키 `5CE87F90…_1.8.80.0_1.27.20260711.1.7b7c0e2` — 올라온 `onnxruntime.dll` 과 같음).
  HUD 범례가 빈 채로만 보여 test4 4초 장면(Debug DetectView 를 멈춰 최대화)을 주 모니터에 띄우고 `Test-ScreenHud -ExeDir publish` 를 다시 돌려 29 항목 통과
  (오버레이 범례 `CAR 9 · BUS 1` = 오버레이 박스 10, 일시정지에 빔·재개에 돌아옴·정지에 빔). 테스트가 찍은 재생 처리 GPU 86.4 · NPU 49.5 FPS, HUD 102.1 FPS(참고용)
- 그 전: 2026-11-01, Debug — 클래스별 검출 수 범례(위 "화면 디자인") 뒤. 사용자가 PC 를 쓰는 중에 돌렸다.
  `Run-All` 한 번에 `Test-FilePlayback` 29(범례 `CAR 1 · PERSON 11` = 검출 12 등) · `Test-VfrPlayback` 26 · `Test-ScreenHud` 38(DXGI, 박스 없음, 결함 주입 포함) ·
  `Test-NpuSwitch` 28 항목(NPU 전환 7.7초(캐시), 다시 고르면 6.2초, ①② 창 응답 WM_NULL 최대 1 ms · UIA 최대 25 ms, 재생 중 다시 전환 3 · 19 ms).
  HUD 대상 모니터에 물체가 없어 HUD 범례는 빈 채로만 봤기에, test4 4초에서 멈춘 DetectView 를 주 모니터에 최대화해 두고 `Test-ScreenHud` 를 다시 돌려 43 항목 통과
  (오버레이 범례 `CAR 6 · TRUCK 3` = 오버레이 박스 9, 일시정지에 빔·재개에 돌아옴·정지에 빔). (배포본은 같은 날 `fb06cf9` 로 다시 배포 — 위)
- 그 전: 2026-11-01 아침, **배포본 `publish`** — `main`(`c274575`, 화면 디자인 개편)으로 다시 배포
  (exe 126.4 MB, pdb·lib·dll 없음, 배포 폴더 `best.onnx`(09-17 visible, MD5 `87D0E1CB…`)·`best.npu.onnx`(`5CE87F90…`) 그대로, 그래픽 설정 `GpuPreference=0` 그대로,
  런타임 패키지는 바뀌지 않음 — `Test-NpuSwitch` 가 올라온 `onnxruntime.dll` 1.27.20260711.1 확인). 사용자가 PC 를 쓰는 중에 돌렸다.
  `Run-All -ExeDir publish` 한 번에 `Test-FilePlayback` 24 · `Test-VfrPlayback` 26 · `Test-ScreenHud` 23(DXGI, 박스 없음, 결함 주입은 Release 라 [INFO]) ·
  `Test-NpuSwitch` 28 항목(NPU 전환 6.9초(캐시), 다시 고르면 6.5초, ①② 창 응답 WM_NULL 최대 2 ms · UIA 최대 37 ms, 재생 중 다시 전환 2 · 26 ms).
  테스트가 찍은 재생 처리 GPU 48.3 · NPU 42.9 FPS, HUD 51.0 FPS(참고용 — PC 부하)
- 그 전: 2026-11-01, Debug — 화면 디자인 개편(시안 B · 색 조합 B2, 위 "화면 디자인") 뒤. 사용자가 PC 를 쓰는 중에 돌렸다.
  `Run-All` 한 번에 `Test-FilePlayback` 24 · `Test-VfrPlayback` 26 · `Test-ScreenHud` 39(DXGI, 결함 주입 포함 — 이번엔 HUD 대상 모니터에 검출된 물체가 있어
  새 `BoxLayer` 로 그린 오버레이 박스 1개가 일시정지에 지워지고 재개에 돌아오는 검사 +2) · `Test-NpuSwitch` 28 항목(NPU 전환 8.4초(캐시), 다시 고르면 6.5초,
  ①② 창 응답 WM_NULL 최대 2 ms · UIA 최대 19 ms, 재생 중 다시 전환 2 · 24 ms). 같은 개편의 앞선 판(라벨 순서·HUD 아이콘 고치기 전)도 24 · 26 · 37(박스 없음) · 28 통과.
  (배포본은 같은 날 `c274575` 로 다시 배포 — 위)
- 그 전: 2026-11-01 새벽, **배포본 `publish`** — `main`(`53db7ed`, 이슈 #1~#11 수정 포함)으로 다시 배포
  (exe 126.4 MB, pdb·lib·dll 없음, 배포 폴더 `best.onnx`(09-17 visible)·`best.npu.onnx` MD5 그대로, 그래픽 설정 `GpuPreference=0` 그대로).
  `Run-All` 한 번에 `Test-FilePlayback` 24 · `Test-VfrPlayback` 26 · `Test-ScreenHud` 23(DXGI, HUD 97.9 FPS, 결함 주입은 Release 라 [INFO]) · `Test-NpuSwitch` 28 항목.
  NPU 첫 전환은 배포 짝의 세 조각 새 키(`5CE87F905F2828DB87035570DC8B2220_1.8.80.0_1.27.20260711.1.7b7c0e2`)로 컴파일해 97.9초, 다시 고르면 7.8초.
  ①② 창 응답 WM_NULL 최대 3 ms·UIA 최대 8 ms(재생 중 다시 전환 2·19 ms). 실행 중 모듈 `onnxruntime.dll` 22.0 MB(1.27.20260711.1) · `DirectML.dll` 17.8 MB(1.15.4) 확인
  (단일 파일 자기 추출 `%TEMP%\.net\DetectView\…`). 테스트가 찍은 재생 처리 NPU 46.8 · GPU 76.4 FPS(참고용 — 사용자가 PC 를 쓰는 중)
- 그 전: 2026-11-01 새벽, Debug — 이슈 #1~#11 수정(`41e893f`~`eb67a64`, OpenCvSharp 20260302 짝) 뒤. 사용자가 PC 를 쓰는 중에 돌렸다.
  `Run-All` 한 번에 `Test-FilePlayback` 24 · `Test-VfrPlayback` 26(새 fps1000 파일 13) · `Test-NpuSwitch` 28 항목(세 조각 새 키라 첫 전환이 컴파일 포함 85.1초, 다시 고르면 5.5초,
  ①② 창 응답 WM_NULL 최대 4 ms·UIA 최대 26 ms) 통과. `Test-ScreenHud` 는 결함 주입 '복제 상실 → 오버레이 그대로'가 '클릭 통과 창 2개'로 1건 실패 — 열린 툴팁까지 센
  테스트 쪽 문제라(위 `Test-ScreenHud` 설명) 오버레이 제목으로 세게 고친 뒤 따로 돌려 37 항목(DXGI, 박스 없음, 결함 주입 포함) 통과.
  판별 확인(A/B): 강제 경로를 끈 변종 빌드는 '재개 → 마지막 화면 다시 검출' 1건만 실패(재개 재검출 0, 받은 화면 7 → 92). 고치기 전 `d62887c` 빌드는 `Test-VfrPlayback` 의
  fps1000 위치 검사 4건(16초 → 18.11 · 13초 → 15.81 · 9초 → 12.54 · 멈춘 채 13.5초 → SAHI `(00:16)`)과 `Test-NpuSwitch` 의 ①② 창 응답 2건(WM_NULL 최대 874·768 ms,
  UIA 최대 4,297·3,885 ms)·캐시 키(툴팁에 키 없음)가 실패했다. 콘솔: `TrackerCheck selftest`·`gpu` 통과, Debug·Release 빌드 경고 0.
  (배포본은 같은 날 `53db7ed` 로 다시 배포 — 위)
- 그 전: 2026-10-31 낮, **배포본 `publish`** — `main`(`9158b2b`, `6d1f61a` 시크·SAHI 정합과 SAHI 경계 조각 병합 · Gemini 교차 검증 반영 포함)으로 다시 배포
  (exe 126.4 MB, pdb·lib·dll 없음, 배포 폴더 `best.onnx`(09-17 visible)·`best.npu.onnx` MD5 그대로, 그래픽 설정 `GpuPreference=0` 그대로).
  `Run-All` 한 번에 `Test-FilePlayback` 24 · `Test-VfrPlayback` 13 · `Test-ScreenHud` 20(DXGI, HUD 104.9 FPS) · `Test-NpuSwitch` 25 항목(캐시 있는 NPU 전환 7.3초, 다시 고르면 5.5초).
  실행 중 모듈 `onnxruntime.dll` 22.0 MB(1.27) · `DirectML.dll` 17.8 MB(1.15.4) 확인. 테스트가 찍은 재생 처리 NPU 45.6 · GPU 80.3 FPS(참고용 — 벤치마크는 `Measure-Fps`)
- 그 전: 2026-10-31 새벽, Debug — Gemini 교차 검증 반영(SAHI 대기의 `SeekPending` 항 · 시크 직후 일시정지의 한 장 보기 · 남은 한 장 보기 건너뛰기 ·
  테스트 35ms 대기) 뒤. `Run-All` 한 번에 `Test-FilePlayback` 24('재생 중 시크 직후 SAHI' 7,742 ms → `(00:07)`, 판별 못 함 [INFO] 없음) ·
  `Test-VfrPlayback` 13(바뀐 되감기 12,500 ms → `(00:12)`) · `Test-ScreenHud` 20(DXGI, HUD 108.9 FPS) · `Test-NpuSwitch` 25 항목(캐시 있는 NPU 전환 5.4초).
  (그때 배포본은 아직 `7e7b697`)
- 그 전: 2026-10-30 밤, Debug — 재생 중 시크·SAHI 정합과 SAHI 경계 조각 병합 뒤. `Run-All` 한 번에 `Test-FilePlayback` 24(새 'SAHI 후 슬라이더 활성'·
  '재생 중 시크 직후 SAHI → 그 위치 분석' — 7,742 ms 시크 → `(00:07)`) · `Test-VfrPlayback` 13(새 '뒤로 시크 직후 SAHI → 그 위치 분석' — 1,500 ms → `(00:01)`) ·
  `Test-ScreenHud` 20(DXGI) · `Test-NpuSwitch` 25 항목. **배포본은 이 변경 전(`7e7b697`) 그대로**다
- 그 전: 2026-10-30 저녁, **배포본 `publish`** — `main`(`7e7b697`, 작은 버그 수정 `6691ffd` 포함)으로 다시 배포
  (exe 126.4 MB, pdb·lib·dll 없음, 배포 폴더 `best.onnx`(09-17 visible)·`best.npu.onnx` MD5 그대로, 그래픽 설정 `GpuPreference=0` 그대로).
  `Run-All` 한 번에 `Test-FilePlayback` 22 · `Test-VfrPlayback` 12 · `Test-ScreenHud` 20(DXGI, HUD 107.6 FPS) · `Test-NpuSwitch` 25 항목.
  실행 중 모듈 `onnxruntime.dll` 22.0 MB(1.27) · `DirectML.dll` 17.8 MB(1.15.4) 확인. NPU 첫 전환은 10:40 에 빈 채 남았던 배포 짝의 1.8.80 키 폴더에
  다시 컴파일해 56.2초(문구 '컴파일 중'), 다시 고르면 4.8초. 재생 처리 NPU 49 FPS · GPU 92 FPS
- 그 전: 2026-10-30, Debug — 작은 버그 수정 뒤: `Test-FilePlayback` 22(새 'EOF 뒤 시크' 2개 포함) · `Test-VfrPlayback` 12 · `Test-ScreenHud` 20(DXGI) 통과. **`Test-NpuSwitch` 는 실패** —
  ② 에서 NPU 를 고른 뒤 240초 동안 진행 문구가 'NPU 확인 중'에서 바뀌지 않았고, 끝나고 보니 선택이 GPU 로 돌아가 있었으며, 이어진 파일 열기 대화상자도 뜨지 않았다(③ 은 그 예외로 못 돎).
  그런데 컴파일은 18:24:07 에 시작해 18:25:53 에 끝났다(캐시에 xmodel·context.json. 10-24 의 EP 1.8.75 는 약 53초) — UI 스레드가 묶여 문구를 못 읽은 것으로 보인다(추정).
  같은 날 10:38~10:39 에 Windows ML EP 패키지 3개가 새 판으로 바뀌었고(`Microsoft.WinML.AMD.NPU.EP.Framework.2` 1.8.75 → 1.8.80 · TRT-RTX · AMD GPU EP), 10:40 사용자가 띄운 배포본의 짝 컴파일은
  빈 키 폴더만 남겼다(이번 수정 중 NPU 경로에 닿는 건 컴파일 시작 전의 문구 판정뿐). 그때 사용자가 PC 를 쓰는 중이었다.
  **같은 날 19:35~19:41 다시 해 보니 재현되지 않았다**: 캐시 있음 5.2초, 캐시를 치우고 53.5초(컴파일 약 49초), 빈 키 폴더를 둬도 55.2초에 전환 —
  세 번 모두 창이 계속 응답했고(`Process.Responding` 감시), 차가운 캐시에서 `Test-NpuSwitch` 25 항목 통과(첫 전환 53.1초, 진행 문구 '컴파일 중'까지 봄).
  → 이 빌드로 4종 모두 통과(NpuSwitch 는 따로 돌림). 18:24 실패는 EP 업데이트 직후 첫 컴파일(106초)·PC 부하와 겹친 일시적 현상으로 본다(원인은 못 밝힘).
  다시 나면 `Process.Responding` 으로 창이 묶였는지부터 볼 것 — 11-01 부터는 `Test-NpuSwitch` 의 '창 응답' 검사·참고 줄이 그걸 잰다
  (이슈 #4 — 그때 ② 가 UI 스레드에서 EP 카탈로그를 조회했다)
- 그 전: 2026-10-25 오후, **배포본 `publish`** — `main`(`82afb2d`, 리뷰 수정 + 중복 박스 필터)으로 다시 배포(exe 126.4 MB, pdb·lib·dll 없음,
  배포 폴더 `best.onnx`(09-17 visible)·`best.npu.onnx` 는 MD5 그대로). `Test-FilePlayback` 20 · `Test-VfrPlayback` 12 · `Test-ScreenHud` 20(DXGI, HUD 105 FPS) · `Test-NpuSwitch` 25 항목.
  이번엔 사용자가 **자기 터미널에서 직접** 돌렸는데(Claude 도구의 자동 모드 확인이 고장 나서) NPU 첫 전환이 **87.8초(컴파일)** 였다 — 같은 모델·EP 로 전날 Claude 도구에서 컴파일해 둔
  캐시를 못 쓴 것이라, 위 NPU 절의 "`%LOCALAPPDATA%` 가 돌려져 직접 실행 때 다시 컴파일" 가설과 맞는다(캐시 경로는 확인 안 함). 다시 고르면 4.8초.
  곧이어 같은 배포본을 Claude 도구로 다시 돌리니 NPU 첫 전환이 컴파일 없이 8.6초 — 도구 쪽에는 전날 캐시가 있었다는 뜻이라 가설이 한층 더 맞는다.
  그 실행에선 `Test-VfrPlayback` 이 `파일 열기 대화상자가 뜨지 않음`(10초 대기)으로 한 번 실패했고, 바로 다시 돌리니 12 항목 통과.
  그때 PC 가 바빴다(GPU 재생 60 FPS · HUD 첫 표시 11 FPS, 평소 95·105) — 코드가 아니라 부하 탓으로 본다
- 그 전: 2026-10-25, Debug(HUD 는 DXGI) — 중복 박스 필터(3번 항목)를 리뷰 수정 위에 합친 뒤. `Test-FilePlayback` 20 · `Test-VfrPlayback` 12 · `Test-ScreenHud` 20(DXGI) · `Test-NpuSwitch` 25 항목.
  (필터만 넣은 리뷰 수정 전 판도 17 · 12 · 10 · 25 통과.)
- 그 전: 2026-10-25, Debug(HUD 는 DXGI) — 리뷰 수정(소스 전환·멈춘 채 시크·GDI·공급자) 뒤. `Test-FilePlayback` 20 · `Test-VfrPlayback` 12 · `Test-ScreenHud` 20(DXGI) · `Test-NpuSwitch` 25 항목
- 그 전: 2026-10-24 저녁, **배포본 `publish`**(NPU UI 포함으로 다시 배포, exe 126.4 MB — `best.onnx`·`best.npu.onnx`·영상은 그대로)와 Debug 둘 다 —
  `Test-FilePlayback` 17 · `Test-VfrPlayback` 12 · `Test-ScreenHud` 10(DXGI) · `Test-NpuSwitch` 25 항목. 배포본의 NPU 첫 전환은 이 앱에서 처음 컴파일이라 56.6초, 다시 고르면 4.8초.
  `Test-NpuSwitch -ColdCancel` 도 통과(Debug, 취소 뒤 GPU 유지, 다시 고르면 4.8초)
- `Test-ScreenHud` 항목 수(PASS/FAIL 줄, 2026-11-02 추적 번호 검사 추가 뒤): 결함 없는 실행 DXGI 25 · GDI(그래픽 설정 안내 — 하이브리드 안내 검사 2개, DXGI 툴팁 검사 빠짐) 24 ·
  GDI(그 밖 — 원인 미상 0x887A0004·회전/출력 없음·기타 HRESULT, 툴팁 `UnexplainedHelp` 는 검사 안 함) 22, 일시정지 전 박스가 있으면 각각 +6(박스·범례·번호 지움과 다시 그림),
  정지 전 범례가 있으면 +2(범례·번호 지움). Debug 빌드는 결함 주입이 더해진다 — 생성 실패 4 · 복제 상실 6(첫 캡처가 GDI 면 1) · 검출 예외 4.
  예: Debug·DXGI·박스 없음 = **39**, 박스 있음 = **47**(둘 다 11-02 실측). 배포본은 결함 주입이 빠진 25 · 33.
  HUD 대상은 기본 선택인 **주 모니터**다(이 PC 에선 모니터 0). (추적 번호 검사 전엔 DXGI 24 · GDI 23 · 21, 박스 있으면 +4, 정지 전 범례 +1 — Debug·DXGI 박스 없음 38·있음 43, 배포본 24·29(11-01). 범례 검사 전 11-01 엔 DXGI 23 · GDI 22 · 20 에 박스 있으면 +2 — Debug·DXGI·박스 없음 37. 10-25~10-31 엔 DXGI·GDI 20, 원인 미상 GDI 18)
- 그 전: 2026-10-24, Windows ML 런타임(`spike/windows-ml-runtime`) — Debug 와 단일 파일 배포본(scratchpad 경로) 모두. `Test-FilePlayback` 17 · `Test-VfrPlayback` 12 · `Test-ScreenHud` 통과.
  같은 날 main 에 합친 뒤 `publish` 에 다시 배포(exe 126.4 MB, `best.onnx`·테스트 영상은 그대로)하고 배포본으로 17 · 12 · 9(GDI 경로) 항목 통과.
  HUD 안내를 모니터별로 고른 뒤 다시 배포해 배포본 3종 실패 0 (`Test-ScreenHud` 는 GDI 경로에 "권하는 설정이 지금 설정과 맞음" 검사가 하나 늘었다). Debug HUD 는 DXGI.
  배포본으로 노트북 화면(모니터 1)을 고르면 '절전' 그대로 DXGI 인 것도 손으로 확인 (테스트는 모니터 0 만 본다)
  같은 날 교체 전(`main`) Debug 에서 `Test-VfrPlayback` 의 시크 후 배속이 0.90·0.81 로 한 번 떨어졌다가 다시 돌리니 통과 — 부하에 흔들리는 항목이다
- **테스트가 확인하지 않는 것** (코드로만 확인 — 이 부분을 고치면 손으로 볼 것):
  재생 시계를 기다리는 중 일시정지·SAHI 하면 그 프레임을 들고 있는지(멈춘 화면이 넘어가지 않는지, SAHI 가 보이는 프레임을 분석하는지),
  긴 grab 도중 정지·종료가 바로 되는지, 60fps 55분 넘는 길이 없는 파일의 뒤로 시크, 시크용 다시 열기 실패(파일 이동·삭제),
  종료 시 SAHI 와 겹치는 `_yolo` 해제. 테스트 영상이 짧아(20~31초) 이런 경우가 생기지 않는다.
  추론 장치 전환 중엔 컴파일 도중 창 닫기, HUD·SAHI 도중 바꿔 끼우기, EP 를 처음 내려받는 경로(이 PC 는 이미 받아 둠)를 코드로만 확인했다.
  2026-10-25 리뷰 수정 중엔 소스 전환 버튼 연타(`_switchingMode`), 열기 실패 뒤 `ResetToIdle`, 재생 중 시크 직후 SAHI(한 장 보기 대기), 창 밖에서 놓는 슬라이더 드래그,
  GDI `BitBlt` 실패(잠금 화면), FP16 모델의 출력 버퍼·공급자 생성자 실패 정리를 코드로만 확인했다.
  2026-10-30 작은 버그 수정 중엔 GDI `BitBlt` 실패 때 쉬기, 1시간 넘는 영상의 시간 표시(서식만 따로 확인), HUD 결과 전달 깃발 순서(동시성 검토)를
  코드로만 확인했다(NPU 캐시 문구는 같은 날 빈 키 폴더를 두고 실행해 '컴파일 중'이 뜨는 것까지 확인).
  HUD 재개 때 마지막 화면 재검출(`_hudForceRefresh`)은 이제 진단 값으로 본다(이슈 #11 — 강제 경로를 끈 변종 빌드로 판별 확인, 위 '마지막 전체 통과').
  2026-10-31~11-01 이슈 수정 중엔 이것들을 확인하지 않았다: HUD 캡처 복구의 실제 회전·TDR·드라이버 업데이트·잠금 화면·UAC·모니터 뽑기(결함 주입은 루프의 복구 경로만 본다),
  DXGI 다시 열기 대기·Grace 초과 뒤 GDI·DXGI 인 채 배치 변경의 UI 갱신·끊김 중 박스 유지·검출 재시도 물러남(창이 있어야 도는 경로라 코드 검토와 콘솔 하네스로만),
  썸을 잡은 채 Alt+Tab 하는 슬라이더 드래그(UIA 로 못 함 — 이슈 #8. 11-01 사용자가 손으로 확인해 문제 없음, 고칠 땐 다시 손으로 볼 것), EP 패키지 ID 를 못 읽을 때의 LibraryPath·unknown 키 경로(이 PC 는 패키지 ID 가 있음),
  EP 를 처음 내려받을 때 진행률이 스레드 풀에서 UI 로 오는지, VGM 을 실제로 키운 PC 의 GPU 판정.
  '재생 중 시크 직후 SAHI' 검사(FilePlayback·VFR)는 누르는 때가 루프의 시크 처리 중에 걸릴 때만 옛 버그를 잡는다 — 곧바로 누르면 루프가 시크를 꺼내기 전이고,
  늦게 누르면 처리가 끝난 뒤라 둘 다 옛 코드도 통과한다. 그래서 시크 뒤 35ms 에 누른다. 대기 논리만 옛것으로 되돌린 변종 빌드로 5번씩 재 보니(10-31)
  FilePlayback(7.742초 시크)은 35ms 에서 5/5 를 잡았고 70ms 는 1/5, 120ms 는 0/5. VFR 은 되감기(다시 열기 + 750장 grab)가 수십 ms 로 짧아 35ms 에서도 1/5 —
  회귀 판별은 FilePlayback 쪽이 맡는다. 새 빌드는 같은 측정에서 12/12 시크 위치를 분석했다. 누르기 전에 처리가 끝난 실행은 [INFO] 로 드러난다
  (슬라이더가 시크 값에서 벗어나 있음 — VFR 은 시크한 프레임 자체가 12,500 이라 그 한 장만 보인 때는 못 가린다). 타이밍 측정이라 다른 PC 에선 달라질 수 있다.
  10-31 반영한 SAHI 대기의 `SeekPending` 항, 시크 직후 일시정지의 한 장 보기, 남은 한 장 보기 건너뛰기는 몇 µs 틈이 걸려야 드러나서 UI 테스트로는 재현되지 않는다 —
  코드로만 확인했다(독립 검토 3관점 + 반박 검증)
  SAHI 의 10초 대기 초과 문구는 그만큼 긴 시크(길이 없는 긴 파일 되감기)가 필요해 코드로만 확인했다.
  2026-11-01 화면 개편에선 이것들을 확인하지 않았다: HUD 오버레이 박스의 실제 모습(오버레이는 캡처 제외라 스크린샷·PrintWindow 에 검게 나온다 —
  같은 `BoxLayer` 인 앱 안 박스로만 봤다), 100% 가 아닌 화면 배율(노트북 화면 125% 등)에서의 박스 선·라벨, Windows 10 의 제목 표시줄(색 지정은 무시되고 다크 모드만),
  키보드 포커스 표시, 카메라 재생 화면, 정지 버튼·지금 소스 버튼의 마우스 올림 색(스크린샷엔 안 잡힘).
  같은 날 클래스별 검출 수 범례에선 이것들을 확인하지 않았다: HUD 오버레이 범례의 실제 모습(캡처 제외 — 글자는 UIA `ItemStatus` 로, 모습은 같은 `BoxLayer` 인
  앱 안 범례로만 봤다), 100% 가 아닌 화면 배율에서의 범례, 카메라 화면의 범례, bus·motorcycle·scooter·bicycle 줄의 모습(테스트 영상엔 거의 car·truck·person 만 나온다 —
  bus 는 같은 날 배포본 HUD 재검사의 오버레이 범례 글자 `CAR 9 · BUS 1` 로만 봤다).
  2026-11-02 추적기 연결에선 이것들을 확인하지 않았다: 카메라 소스의 추적(카메라가 없어도 되는 자동 테스트가 없다 — 코드 검토뿐, 벽시계 시각과 멈춤·재개 때 비움),
  HUD 의 오버레이 라벨 모양(캡처 제외 — 번호 글자는 UIA 로, 모양은 같은 `BoxLayer` 인 앱 안 라벨로만 봤다), HUD 에서 정지 화면이 오래 이어진 뒤에도 번호가 이어지는지(가상 시계 — selftest 로만),
  100% 가 아닌 배율에서 긴 라벨, 모니터 가장자리에서 라벨이 잘리는지.
  같은 날 투명도·작은 물체 라벨·꺾쇠 축소에선 이것들을 확인하지 않았다: HUD 오버레이에서의 모습(캡처 제외 — 같은 `BoxLayer` 인 앱 안 영상으로만 봤다), 아주 밝은 배경(흰 차·하늘)에서
  옅어지고 가늘어진(1px) 꺾쇠·라벨 바탕이 잘 보이는지, 100% 가 아닌 배율에서 0.6배 라벨(글자 6.6 DIP)이 읽히는지·꺾쇠 굵기(125% 면 큰 물체 3px → 작은 물체 2px)
  2026-11-03 관심 구역에선 이것들을 확인하지 않았다: 100% 가 아닌 화면 배율(노트북 화면 125% 등)에서의 점 손잡이·집기 반경·힌트 글자, 사람 손의 끌기 감각(테스트는 8걸음으로 끌고 손잡이는 8px 정사각형 —
  너무 작거나 크게 느껴질 수 있다), 키보드 포커스 표시, 우클릭으로 한 점 되돌리기·점이 한 줄이라 닫히지 않는 경우(넓이만 selftest 로 봤다), 점 3개 구역에서 점 지우기가 막히는지, SAHI 중 편집이 막히는지(코드로만),
  `FrameWidth/Height` 가 0 으로 읽히는 파일(구역이 안 보이고 편집이 막힌다 — 길이 없는 webm 도 test 에선 열어 본 적 없다), 읽기 전용 폴더의 '구역 저장 실패' 문구, 구역이 많을 때(목록 3줄 넘김) 스크롤 모양,
  다른 PC·Windows 10. HUD·카메라에는 구역을 연결하지 않았다(없는 기능)
  2026-11-03 화면 전환 감지에선 이것들을 확인하지 않았다: 카메라 소스(적용하지 않음 — 웹캠의 d 분포), 조금만 도는 카메라(작은 팬)의 기준 거리, 밝기가 크게 흔들리는 영상(역광·터널 진출입·야간 — test1~5 는 낮 장면이고
  test5 는 터널 출구 근처지만 카메라가 고정이다), 아주 어두운 영상(모든 픽셀이 24 이하면 빈 프레임으로 봐 안정이 안 돼 번호가 안 붙을 수 있다 — `BlankMax`), 10fps 이하 파일의 회전, 60fps 를 넘는 소스에서 안정 3장이 너무 짧은지,
  시크 직후 첫 프레임의 배지, 100% 가 아닌 배율에서의 배지 모양, 전환 때 `tracker.Reset()` 호출 자체(위 '확인')
  2026-11-03 구역 이벤트 로그에선 이것들을 확인하지 않았다: 구역이 여럿이거나 복잡한 다각형일 때의 경계 판정(selftest 는 직사각형), 이벤트가 수백 건 쌓였을 때의 목록 스크롤·성능(최대 2000건 — 항목마다 UIA 노드가 생긴다),
  발 위치가 프레임 가장자리에 걸친 큰 물체(박스 아래가 잘려 발이 위로 보인다), 100% 가 아닌 배율에서의 로그 모양, CSV 를 엑셀로 열었을 때(ASCII 만 써서 BOM 없이도 열릴 것으로 보지만 확인 안 함), 읽기 전용 폴더의 'CSV 저장 실패' 문구,
  클래스가 흔들리는 물체(car↔truck)의 로그 라벨(이벤트 시점의 라벨이라 같은 물체가 진입·나감에 다른 클래스로 찍힐 수 있다)
  2026-11-03 화면 식별(2b)에선 이것들을 확인하지 않았다: **실제로 같은 자리로 돌아오는 PTZ 재방문 영상**(이 PC 엔 없다 — 합성 영상과 실제 프레임 맞춤으로만), 카메라 자세가 6% 넘게 어긋나 돌아올 때의 모습(다른 뷰로 쌓인다는 건 합성 확대·옮김으로만),
  밤낮·역광·비로 화면이 크게 변한 같은 뷰(같은 뷰로 알아볼지), 어둡거나 흐려 특징점이 60개 미만인 영상(구역 이벤트가 안 돈다 — 합성 회색·그라데이션으로만), 뷰가 수십 개로 늘어난 파일(상한 32 — 합성으로 상한 2 만), 구역 파일이 수 MB 가 됐을 때 저장 속도,
  카메라가 움직이는 중에 일시정지하면 구역 편집이 막히는지(코드로만 — 움직이는 순간에 멈추는 타이밍을 UI 테스트로 못 맞췄다), 100% 가 아닌 배율에서 배지 글자, 식별이 오래 걸리는 PC(첫 호출 워밍업 전에 영상을 연 경우의 재생 시계),
  구역을 뷰가 정해지지 않은 옛 파일(판 1)로 가진 채 첫 식별이 늦게 되는 경우의 이벤트 시작(UI 가 첫 뷰를 받아 구역을 다시 알리는 한 프레임 동안은 구역 이벤트가 안 돈다)
  2026-11-03 유고 규칙에선 이것들을 확인하지 않았다: test4 의 멈춘 덤프트럭 하나 말고는 실제 영상으로 돌려 보지 않았다(test5 갓길 화물차가 갓길 구역에서 후보가 안 되는지, test4 신호대기 행렬이 정체로 제외되는지는 합성 장면으로만 — 영상에 구역을 그려 돌린 적 없음),
  5초·정체 3대·흔들림 1초가 현장에 맞는지(사용자가 5초를 골랐고 나머지는 구현 때 정했다), 선언된 유고 박스가 아주 멀어 작을 때의 모습(작아도 옅게·작게 안 한다는 건 코드로만), 100% 가 아닌 배율에서 빨간 라벨,
  선언 직전·직후 화면 전환·시크가 겹치는 경우, 로그가 2000건을 넘어 오래된 행을 버릴 때 열린 유고의 해소 행이 남는지, CSV 의 `incident`·`incident_end` 행을 엑셀로 열었을 때, HUD·카메라(연결 안 함)
- 스크립트는 UTF-8 **BOM** 으로 저장한다 (Windows PowerShell 5.1 이 한국어 문자열을 깨뜨리지 않게). PowerShell 7 에서 확인

(테스트 영상 내용 — test4·test5 에 무엇이 찍혀 있는지 — 은 CLAUDE.md '테스트' 절에 그대로 남겼다)
