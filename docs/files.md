# 파일 구조

2026-11-10 CLAUDE.md 에서 글자 그대로 옮겼다(세션마다 읽히지 않게). 규칙·금지 사항은 CLAUDE.md 에 남아 있다.

## 파일별 역할

`MainWindow` 는 partial class 로 기능별 분할돼 있다 (솔루션 탐색기에서는 `MainWindow.xaml` 아래에 묶여 보임).

| 파일 | 역할 |
|---|---|
| `MainWindow.xaml.cs` | 공용 상태(`_mode`, 재생 루프 제어) + 모드 공통 UI(정지·재생/일시정지·슬라이더·`ApplyModeToUi`(지금 소스 버튼·상태 점 포함)·제목 표시줄 색·종료) |
| `MainWindow.File.cs` | 파일 열기, VFR 메타데이터 분석(`AnalyzeAsync`/`ProbeSeekConsistent`/`ScanFrames`) |
| `MainWindow.Camera.cs` | 카메라 열거(DirectShow)·시작 |
| `MainWindow.Playback.cs` | 파일/카메라 공용 재생 루프, 재생 시계(원본 속도), 시크(`SeekToMs`→`ApplyPendingSeek`), EOF·카메라 유실. 보이는 프레임마다 추적기(`Tracker`)를 돌려 박스에 번호를 붙인다 |
| `MainWindow.Screen.cs` | 화면 HUD: 모니터 선택, 캡처 루프(`ScreenLoop` — 첫 캡처 결과 보고·캡처 복구·모니터 배치 감시·추적기), 오버레이 갱신, 상태바 무시 영역, FPS 칸 문제 표시, UIA 진단 값 |
| `MainWindow.Inference.cs` | 모델 로드, GPU 선택(`SelectBestGpu`·`CreateGpuYolo` — 판정은 `GpuAdapters`), `ProcessFrame`, `DetectTile`/`DetectBgra`(검출이 모두 여기로 — 레터박스는 `YoloLetterbox`, `_yoloLock` 안에서). YoloDotNet 의 축소 방식 `InputSampling`(쌍선형 — GPU·NPU 세션 모두 `YoloOptionsFor`, 레터박스 경로엔 듣지 않는 대비) |
| `YoloLetterbox.cs` | YOLO 입력 레터박스를 OpenCV 쌍선형(`INTER_LINEAR`)으로 직접 만들어 YoloDotNet 에 넘기고(배율 1), 박스를 +0.5 해 그린 자리 그대로의 여백·배율로 되돌린다(2026-11-05 — 아래 '화면 HUD' 성능 절의 '입력 축소'). 크기는 모델 입력(`InputSize` — 정사각형이 아닐 수 있다. SAHI 타일·창 제목도 이걸 읽는다). WPF 없음 — TrackerCheck·RuntimeParity 가 소스로 링크 |
| `MainWindow.Npu.cs` | 추론 장치 GPU · TensorRT · NPU 전환(장치 공통 상태 기계 — `_onDevice`·`_wantDevice`·`_prepDevice`)과 NPU: 왼쪽 패널 "추론 장치" 선택, 짝 모델 확인 → VitisAI EP 준비 → 백그라운드 컴파일 → `_yolo` 교체 (아래 "추론 장치") |
| `MainWindow.Trt.cs` | 추론 장치 TensorRT (2026-11-08): FP16 짝 확인(`CheckTrtModel`) → Windows ML 카탈로그의 TensorRT for RTX EP 준비 → 백그라운드 엔진 만들기(커널 캐시) — 전환은 `MainWindow.Npu.cs` 의 상태 기계 |
| `MainWindow.Roi.cs` | 왼쪽 패널 '구역' 절(목록·추가/지우기·유형)과 `RoiLayer` 를 잇고 구역 파일에 바로 저장. 편집 가능 여부는 `UpdateRoiUi` 한 곳 (파일 + 멈춤 + SAHI 아님) |
| `WindowsMlExecutionProvider.cs` | YoloDotNet 실행 공급자 — Windows ML 의 ONNX Runtime 으로 DirectML(나중에 NPU) 세션을 돌린다. `FindDmlDevice` 로 PCI ID 매칭 |
| `MainWindow.Sahi.cs` | SAHI 버튼·실행·결과 그리기 |
| `Sahi.cs` | SAHI 순수 알고리즘 (`TileOrigins`, 앱이 쓰는 `TouchesInnerEdge`·`MergeCutPieces`, 비교용 `MergeNms`·`MergeNmm`, IoU/IoS) |
| `Tracker.cs` | 객체 추적기 (시각 기반 탐욕적 IoU, 순수 알고리즘). 프레임 크기를 받으면(`Update(dets, ms, 폭, 높이)`) 테두리에 걸친 박스의 멈춤 판정을 바로잡는다. `UpdateTagged` 는 번호·정지 여부를 `Det` 에 붙여 돌려준다. 파일·카메라·화면 HUD 에 연결돼 있다(2026-11-02) — 아래 "추적기" |
| `Roi.cs` | 관심 구역(ROI) 순수 모델·기하·파일 — 구역(`RoiZone`, 프레임 0~1 좌표 점들 + 유형 `RoiKind` + 그린 뷰 `ViewId`)·`RoiSet`(번호·`<영상>.roi.json` 판 2 읽기/쓰기 — 뷰 목록 포함)·`RoiGeometry`(안·밖·넓이·점 집기). WPF 없음 — experiments 가 소스로 링크 (2026-11-03, 아래 "관심 구역"·"화면 식별") |
| `RoiLayer.cs` | 영상 위 구역 그리기·편집 요소(`RoiLayer`)와 유형별 색(`RoiTheme` — App.xaml 의 `Roi.*`). 앱 안 영상(파일)에서만, 편집 중에만 마우스를 받는다 |
| `SceneChange.cs` | 화면 전환 감지 순수 알고리즘(`SceneChangeDetector`) — 카메라가 돌거나 화면이 바뀌었는지 64×36 흑백 그림 열로 본다. WPF·OpenCV 없음 — experiments 가 소스로 링크 (2026-11-03, 아래 "화면 전환 감지") |
| `SceneThumbnail.cs` | 프레임 → 64×36 흑백 그림(면적 평균 축소 — 썸네일 + 감지기로 프레임당 약 0.8 ms). 앱과 실험 도구가 **같은 코드**로 만들어야 도구가 잰 임계값이 앱에서도 맞는다 (OpenCvSharp — TrackerCheck 도 소스로 링크) |
| `MainWindow.Scene.cs` | 영상 위 화면 전환 배지(`SceneBadge` — `ShowSceneBadge`). 감지·추적기 연결은 `PlaybackLoop` |
| `ZoneEvents.cs` | 구역 진입·나감 이벤트 순수 알고리즘(`ZoneEventDetector`) — 추적기를 거친 박스의 발 위치가 구역 안인지로 본다. WPF 없음 — experiments 가 소스로 링크 (2026-11-03, 아래 "구역 진입·나감 이벤트") |
| `MainWindow.Events.cs` | 영상 아래 접이식 이벤트 로그 패널(`EventLogPanel` — 목록·CSV 저장·비우기·헤더의 `⚠ 유고 후보 N`)과 구역 스냅샷을 루프에 넘기는 `PublishZoneShapes`. 유고 후보 행의 VLM 둘째 줄(`EventListItem` 의 VLM 속성 — 단서 칩·한 줄 설명, CSV 끝 `vlm`·`vlm_cues`·`vlm_desc`) |
| `MainWindow.Vlm.cs` | VLM 설명(2026-11-07 — 아래 "VLM 설명"): [VLM 설명] 토글·Ollama 확인·모델 올리고 내리기, 루프가 떼어 준 그림 영역을 큐(4칸)에 넣고 작업 하나가 VLM 을 불러 그 행에 붙인다 |
| `VlmAnnotator.cs` | VLM 질문·스키마·요청 JSON·답 읽기(`VlmPrompt` — 실험 4차와 글자까지 같게)와 Ollama HTTP 호출(`OllamaVlmClient`), 단서(`VlmCue`)·결과(`VlmResult`). WPF·OpenCV 없음 — TrackerCheck 가 소스로 링크 |
| `VlmCrop.cs` | VLM 에 보낼 그림 — 실험(`make_cases.crop_around`)과 같은 3배 정사각 크롭·빨간 박스·긴 변 512·JPEG 90. 자르기는 루프, 그리기·JPEG 은 작업 스레드 (OpenCvSharp — TrackerCheck 가 소스로 링크) |
| `ViewIdentity.cs` | 화면 식별(뷰 식별 2b) — `ViewMatcher`(ORB 특징점 + RANSAC 호모그래피로 두 화면이 같은 카메라 자세인지)·`ViewRegistry`(알고 있는 뷰들: 알아보고, 처음 보는 안정 화면은 새 뷰로 기억)·`ViewInfo`(번호 + 기준 화면 JPEG — 구역 파일에 들어감). OpenCvSharp 만 쓰고 WPF 없음 — experiments 가 소스로 링크 (2026-11-03, 아래 "화면 식별") |
| `IncidentRules.cs` | 유고 규칙 순수 알고리즘(`IncidentDetector`) — 차로에 멈춘 차량이 정지로 5초 이어지면 유고 후보. 이벤트는 `ZoneEvent`(Incident·IncidentEnd). WPF 없음 — experiments 가 소스로 링크 (2026-11-03, 아래 "유고 규칙") |
| `GpuAdapters.cs` | GPU 어댑터 목록(DXGI 하드웨어)과 고성능/저전력 판정 — **내장 여부(DXCore `IsIntegrated`)** 로 가르고 못 읽으면 전용 VRAM. 추론 GPU 선택(`SelectBestGpu`)과 HUD 안내(`DisplayTopology.FindMonitorGpu`)가 같은 함수(`IsHighPerformance`·`Best`)를 쓴다. WPF 없음 — experiments 가 소스로 링크 |
| `DisplayTopology.cs` | 모니터가 실제로 붙은 GPU(`QueryDisplayConfig`)와 이 exe 의 Windows 그래픽 설정(레지스트리 읽기) — GDI 대체 안내용. 저전력/고성능은 `GpuAdapters` 판정 |
| `ScreenCapture.cs` | `MonitorInfo`, `PixelRect`, `IScreenCapture` 인터페이스(`Interruption`·`Redelivered` 포함), `ScreenCaptureLostException`, `ScreenCapture.Create()`(DXGI 우선·GDI 대체)·`Fallback()`(사유 붙은 GDI)·`GetMonitors()` |
| `DxgiScreenCapture.cs` | DXGI Desktop Duplication 캡처 (기본 경로) |
| `GdiScreenCapture.cs` | GDI BitBlt 캡처 (DXGI 불가 시 대체 경로) |
| `OverlayWindow.xaml(.cs)` | 바탕화면 위 투명 HUD 창 (박스와 클래스별 개수 범례는 `BoxLayer` 가 그린다) |
| `BoxLayer.cs` | 검출 박스 렌더러(단일 비주얼) — HUD 오버레이와 앱 안 영상(파일·카메라·SAHI, `FrameBoxes`)이 같이 쓴다. 왼쪽 위 클래스별 개수 범례도 같이 그리고 그 글자(`LegendText`)는 검출 수 칸의 UIA `ItemStatus` 로 나간다. 추적기를 거친 박스는 라벨에 번호·'정지'가 붙고(`CAR #12 정지 0.95`) 그 글자(`TrackText`)는 UIA `ItemType` 으로 나간다. 모양·색은 `App.xaml` 의 `Box.*` (아래 "화면 디자인") |
| `Detection.cs` | `Det` 레코드 구조체 — SAHI와 HUD가 공유하는 검출 결과 타입. 추적기를 거친 박스는 `TrackId`·`Stationary`(init 속성)가 붙고, 유고 후보로 선언된 박스는 `Incident` 도 붙는다(아래 "유고 규칙"). `DuplicateBoxes` — 같은 자리 중복 박스 판정(앱 표시·추적기 공용, 아래 3번) |
| `App.xaml` | 화면 모양 전체를 한 곳에 — 색(색 조합 B2 스틸 블루)·글꼴·버튼/콤보/슬라이더/툴팁/스크롤바 스타일·박스 색(`Box.*`) (아래 "화면 디자인") |
| `Ui.cs` | 스타일이 읽는 첨부 속성 — 버튼 아이콘(`Glyph`·`GlyphBrush`)·`HoverBackground`·`IconOnly`·지금 소스 버튼(`IsActive`) |
| `TestFaults.cs` | Debug 빌드 전용 결함 주입 — 환경 변수 `DETECTVIEW_TEST_FAULTS`(쉼표 구분): `hud-create-fail` · `hud-capture-lost` · `hud-detect-fail`. 훅은 `[Conditional("DEBUG")]` 라 Release 엔 호출 자체가 없다(Release DLL 에 결함 이름 문자열 없음 확인). `Test-ScreenHud.ps1` 이 쓴다. 가짜 VLM `vlm-fake` · `vlm-down` · `vlm-absent`(값을 돌려줘야 해서 `#if DEBUG` — `Test-VlmAnnotation.ps1`) |
| `Directory.Build.props` | OpenCvSharp 판(`OpenCvSharpVersion`)을 두는 한 곳 — 본 앱·실험 프로젝트 넷이 같이 읽는다 ("절대 건드리면 안 되는 것들" 1번, 이슈 #9) |
| `old/` | **2026-11-06 지웠다** — 사용자가 클라우드에 백업한 뒤. 있던 것: 이전 CPU 버전 보관본·최적화 전 백업(`backup_20260911_*`)·학교 과제 제출본(폴더·zip)·`old/docs/`(SRS·아키텍처 설계서)·옛 모델(9.5 MB)·공개 전 git 기록 bundle(아래 '도구 사용 주의'의 원격 항목). `.csproj` 의 제외 글롭과 `.gitignore` 의 `/old/` 는 그대로 둔다 — 다시 생겨도 빌드·graft 가 안전하게(아래 5번·graft 함정 1) |
| `publish/` | 배포 산출물 + 테스트 영상(`test1~4.mp4`, 1280×720 30fps · `test5.mp4`, 아래 "테스트 영상 내용"). 원래의 VFR 테스트 파일 `recording.webm` 은 없어졌고, 대신 합성 파일 `tests/ui/media/vfr60_nodur.webm` 을 쓴다 |
| `tests/ui/` | UI 자동화 회귀 테스트 (PowerShell + UI Automation). 아래 "테스트" 참고 |
| `README.md` | GitHub 첫 화면 (2026-11-02 작성 — 기능·빌드·사용법·테스트·라이선스). 화면이 크게 바뀌면 같이 고칠 것. 스크린샷(test4 3초 장면, 실제 도로 CCTV 프레임)은 같은 날 넣었다가 영상 사용 권리를 확인하지 못해 뺐다(`docs/screenshot.png` 삭제 — 이 PC 의 옛 기록 `old-history` 의 `10e65db` 에만 남음). 다시 넣으려면 권리가 분명한 영상으로 |
| `docs/` | CLAUDE.md 에서 옮긴 긴 설명·실측·지난 기록(2026-11-05) — 아래 '자세한 문서' 표. 세션마다 자동으로 읽히지 않는다 |
| `LICENSE` · `THIRD-PARTY-NOTICES.md` | 저장소 라이선스 GNU AGPL-3.0 (2026-11-02 — 들어 있는 `best.onnx` 가 Ultralytics YOLO26 파인튜닝이라 Ultralytics 가 프로젝트 전체에 AGPL-3.0 을 요구)과 저장소 안 남의 저작물 고지(모델 AGPL-3.0 · `WindowsMlExecutionProvider.cs` 의 바탕 YoloDotNet MIT · `.claude/` 의 graft 연동 파일 MIT — 각 전문). 남의 코드·파일을 가져오면 여기에 더한다 |
| `tools/` | `Make-NpuModel.ps1`(+ `npu_quantize.py`): YOLO 모델의 NPU 용 짝 `<이름>.npu.onnx` 만들기 · `Make-Fp16Model.ps1`(+ `fp16_convert.py`, 2026-11-08): TensorRT 용 FP16 짝 `<이름>.fp16.onnx` 만들기 (아래 "추론 장치") |
| `best.npu.onnx` | 루트 `best.onnx` 의 NPU 짝 (A16W8·가중치 채널별 QDQ, 21.4 MB, git 추적 — 2026-11-08 8클래스 YOLO26m 640×384 로 다시 만듦, MD5 `546567CB…` · 그 전 7클래스 짝 `BD18D32C…`). 원본을 바꾸면 다시 만든다 |
| `best.fp16.onnx` | 루트 `best.onnx` 의 TensorRT 짝 (FP16 — 입출력은 FP32, 40.9 MB, git 추적 — 2026-11-08, MD5 `D7D760DC…`). DirectML 로는 못 연다. 원본을 바꾸면 다시 만든다 |
| `experiments/` | 따로 빌드하는 실험 프로젝트(각자 `.csproj`) — 설명은 `experiments/CLAUDE.md` |
| `graft/` · `.claude/` | 코드 인덱스(graft)와 그 에이전트 연동. graft 는 CLI 로만 쓴다 — MCP 서버 등록은 2026-11-02 사용자 결정으로 전역(`~/.claude.json`)과 이 저장소(`.mcp.json` 삭제) 모두 뺐다. `graft/` 는 재생성 가능한 캐시라 커밋하지 않는다. `.claude/helpers/graft-statusline.cjs`(상태줄)는 graft 설치 경로를 `%APPDATA%\npm` 에서 계산한다(2026-11-02 — graft 가 박아 넣은 `C:\Users\<이름>\…` 경로를 저장소에서 뺌. graft 를 다시 설치해 박힌 경로로 돌아오면 다시 고칠 것). graft 훅은 전역 `~/.claude/settings.json` 에만 둔다 — 프로젝트에도 있으면 둘 다 돌아 세션마다 graft 안내문이 두 번 들어가고 도구마다 훅이 두 번 돈다(2026-11-02 프로젝트 쪽 훅과 `graft-hooks.cjs` 를 뺌. graft 를 이 저장소에 다시 설치해 돌아오면 다시 뺄 것) |
