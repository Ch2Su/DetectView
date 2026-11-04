# CLAUDE.md — DetectView

WPF(.NET 10) 실시간 객체 검출 애플리케이션. YOLO ONNX 모델을 **DirectML(GPU)** 로 추론해
① 동영상 파일 ② 웹캠 ③ 바탕화면 실시간 캡처(투명 HUD 오버레이) 세 가지 소스를 처리한다.

## 이름 (2026-11-03 정리 — 이제 모두 `DetectView`)

| 항목 | 값 | 비고 |
|---|---|---|
| 폴더 | `E:\DetectView` | 예전 `E:\VS_E\YOLO11_Test` — 2026-11-03 옮김 (옛 폴더는 사용자가 확인한 뒤 직접 지운다) |
| 프로젝트·솔루션 / 네임스페이스 / `AssemblyName` / 제품명 / 창 제목 | `DetectView` (`DetectView.csproj`·`DetectView.slnx`·`namespace DetectView`) | 예전엔 `YOLO11_Test` 였다 — 옛 이름은 `old/`·git 기록·`old-history` 태그 안에만 남는다 |
| 배포 폴더 | `publish` | 예전 `publish_gpu_ver` |
| 모델 | YOLO**26s** 파인튜닝 ONNX (`best.onnx`, 36 MB, **7클래스**) | YOLO11 아님. 2026-09-11 교체 (이전: 9.5 MB 모델) |

원래 학교 과제로 시작해(제출 완료) 지금은 개인 취미 프로젝트다. CPU 추론 → GPU(DirectML) 전환이 끝난 상태.
**이 문서·메모의 옛 서술(날짜가 붙은 기록)에는 `YOLO11_Test`·`publish_gpu_ver` 대신 새 이름이 들어가 있다** — 이름만 일괄로 바꿨고 그때 폴더가 달랐다는 건 위 표가 말해 준다.
Windows 그래픽 설정(`HKCU\…\UserGpuPreferences`)은 exe 경로별이라 옛 `publish_gpu_ver\DetectView.exe` 의 `GpuPreference=0` 은 새 `E:\DetectView\publish\DetectView.exe` 에 없다 —
없으면 'Windows에서 결정'(0 과 같은 동작)이다. 필요하면 설정 → 디스플레이 → 그래픽에서 새 exe 를 다시 지정한다 (이 도구로는 건드리지 않는다).

## 빌드 · 배포

```bash
dotnet build
```

```bash
dotnet publish DetectView.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o publish
```

- 프로젝트 파일(`DetectView.csproj`)을 꼭 지정한다. 생략하면 폴더의 `.slnx` 가 잡혀 `NETSDK1194`(솔루션 + `--output`) 경고가 난다
- 결과: `DetectView.exe` 126.4 MB (2026-10-24, Windows ML 런타임. 이전 DirectML 1.23 은 119.4 MB. 2026-10-30·10-31·11-01 다시 배포도 같은 크기 — 11-01 은 세 번: 이슈 수정판 `53db7ed`, 화면 개편판 `c274575`, 클래스별 검출 수 범례판 `fb06cf9` · 11-02 추적기 연결·라벨/꺾쇠·상태바판 `3939f40` · 11-03 관심 구역·화면 전환 감지·구역 이벤트 로그·유고 규칙·화면 식별판 `10f7b31` · 같은 날 폴더 이전·이름 정리판 — 위 '이름'). 테스트 영상(`test1~4.mp4`)은 같은 폴더에 있어도 덮어쓰지 않는다
- publish 뒤에는 배포본으로 UI 테스트를 돌린다: `pwsh tests\ui\Run-All.ps1 -ExeDir publish`
- **`publish\best.onnx` 는 루트의 것과 다를 수 있고, 사용자가 수시로 바꾼다** (YOLO26s ↔ YOLO26x(COCO 80클래스),
  데이터셋이 다른 7클래스 모델 등 — 2026-09-23 한 세션 안에서도 바뀌었다). 건드리지 말 것.
  **성능을 재거나 비교하기 전엔 그 순간 어떤 모델인지 확인한다** — 앱이 떠 있으면 창 제목에 보이고(아래),
  아니면 파일 끝의 Ultralytics 메타데이터를 읽는다:
  `uv run --no-project --python 3.12 python -c "import re,sys;f=open(sys.argv[1],'rb');f.seek(-65536,2);t=f.read().decode('utf-8','ignore');[print(k,'=',v.strip()[:80]) for k,v in re.findall(r'(description|names|date)\x12.?([^\x00-\x08\x0e-\x1f]+)',t)]" publish/best.onnx`
- 배포 폴더 모델을 바꾸면 NPU 짝도 다시 만든다: `pwsh tools\Make-NpuModel.ps1 -Model publish\best.onnx` (→ 그 옆 `best.npu.onnx`, 아래 "NPU")
- **앱용 ONNX 를 새로 내보낼 땐 end-to-end(`[1,300,6]`)인지 본다** — Ultralytics 8.4.142(라벨링 도구가 쓰는 판)의 `export(format="onnx")` 기본값은 NMS 전 원출력 `[1,84,8400]`(메타데이터 `end2end False`)이다.
  `nms=False`(또는 `end2end=True`)를 줄 것. 지금 `best.onnx` 는 8.4.31 기본값으로 end-to-end 였다 (2026-11-04 실측 — `experiments/RfDetrBench` README. 원출력 꼴을 앱이 어떻게 처리하는지는 확인 안 함 — 아래 3번의 전제가 깨진다).
  같은 판의 **val 도 `nms` 를 안 주면 one-to-many 출력 + NMS 로 잰다**(학습 중 검증·`best.pt` 고르기도) — 학습 로그의 mAP 는 앱이 쓰는 end-to-end 출력보다 0.007~0.010 높게 나온다(2026-11-05 실측). 앱과 같은 걸 재려면 val 에 `nms=False`
- publish 는 `best.onnx` 를 `PreserveNewest` 로 복사한다 → **루트 `best.onnx` 가 배포 폴더의 것보다 새로우면 덮어쓴다**
- `best.onnx` 는 단일 파일 exe에 포함되지 않는다. **exe 옆에 같이 놓아야** 실행된다.
  경로는 작업 디렉터리가 아니라 `AppContext.BaseDirectory`(exe 폴더) 기준이다. 예전엔 상대 경로라 바로가기의 "시작 위치"나
  다른 폴더에서 실행하면 모델을 못 찾고 엉뚱하게 "GPU 드라이버 확인" 오류를 냈다 (2026-09-23 수정, 실측 확인)
- `.csproj` 의 `ExcludeSymbolsFromPublish` 타깃이 publish 출력에서 pdb(~92 MB, exe 밖에 따로 생기던 것)와 `.lib` 를 뺀다. 배포물은 `DetectView.exe` + `best.onnx` 뿐이다.
- 실행 시 창 제목이 `DetectView — <모델> (<학습일> 학습) — <GPU 이름> (DirectML)` 로 바뀌면 GPU 초기화 성공이다.
  예: `DetectView — YOLO26s (2026-09-17 학습) — NVIDIA GeForce RTX 5060 Laptop GPU (DirectML)`. 스모크 테스트는 이걸로 확인하면 된다.
  모델 이름은 ONNX 메타데이터 `description`("Ultralytics YOLO26s model …")에서, 학습일은 `date` 에서 뽑는다(`DescribeModel`).
  YoloDotNet 의 `ModelInfo` 에는 s/x 크기가 없어서 쓰지 않는다. 메타데이터가 없는 모델이면 `ModelInfo` 로 대신한다

## 파일 구조

`MainWindow` 는 partial class 로 기능별 분할돼 있다 (솔루션 탐색기에서는 `MainWindow.xaml` 아래에 묶여 보임).

| 파일 | 역할 |
|---|---|
| `MainWindow.xaml.cs` | 공용 상태(`_mode`, 재생 루프 제어) + 모드 공통 UI(정지·재생/일시정지·슬라이더·`ApplyModeToUi`(지금 소스 버튼·상태 점 포함)·제목 표시줄 색·종료) |
| `MainWindow.File.cs` | 파일 열기, VFR 메타데이터 분석(`AnalyzeAsync`/`ProbeSeekConsistent`/`ScanFrames`) |
| `MainWindow.Camera.cs` | 카메라 열거(DirectShow)·시작 |
| `MainWindow.Playback.cs` | 파일/카메라 공용 재생 루프, 재생 시계(원본 속도), 시크(`SeekToMs`→`ApplyPendingSeek`), EOF·카메라 유실. 보이는 프레임마다 추적기(`Tracker`)를 돌려 박스에 번호를 붙인다 |
| `MainWindow.Screen.cs` | 화면 HUD: 모니터 선택, 캡처 루프(`ScreenLoop` — 첫 캡처 결과 보고·캡처 복구·모니터 배치 감시·추적기), 오버레이 갱신, 상태바 무시 영역, FPS 칸 문제 표시, UIA 진단 값 |
| `MainWindow.Inference.cs` | 모델 로드, GPU 선택(`SelectBestGpu`·`CreateGpuYolo` — 판정은 `GpuAdapters`), `ProcessFrame`, `DetectTile`/`DetectBgra` |
| `MainWindow.Npu.cs` | 추론 장치 GPU ↔ NPU: 왼쪽 패널 "추론 장치" 선택, 짝 모델 확인 → VitisAI EP 준비 → 백그라운드 컴파일 → `_yolo` 교체 (아래 "NPU") |
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
| `MainWindow.Events.cs` | 영상 아래 접이식 이벤트 로그 패널(`EventLogPanel` — 목록·CSV 저장·비우기·헤더의 `⚠ 유고 후보 N`)과 구역 스냅샷을 루프에 넘기는 `PublishZoneShapes` |
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
| `TestFaults.cs` | Debug 빌드 전용 결함 주입 — 환경 변수 `DETECTVIEW_TEST_FAULTS`(쉼표 구분): `hud-create-fail` · `hud-capture-lost` · `hud-detect-fail`. 훅은 `[Conditional("DEBUG")]` 라 Release 엔 호출 자체가 없다(Release DLL 에 결함 이름 문자열 없음 확인). `Test-ScreenHud.ps1` 이 쓴다 |
| `Directory.Build.props` | OpenCvSharp 판(`OpenCvSharpVersion`)을 두는 한 곳 — 본 앱·실험 프로젝트 넷이 같이 읽는다 ("절대 건드리면 안 되는 것들" 1번, 이슈 #9) |
| `old/` | 이전 CPU 버전 전체 보관본 + 최적화 전 백업(`backup_20260911_before_opt/`, `backup_20260911_after_step1/`). **빌드 대상에서 제외됨**. `old/docs/` 에 SRS·아키텍처 설계서(.md/.pdf). `old/DetectView-history-2026-11-02.bundle` = 공개 전 git 기록(커밋 75개) 백업 — 아래 '도구 사용 주의'의 원격 항목 |
| `publish/` | 배포 산출물 + 테스트 영상(`test1~4.mp4`, 1280×720 30fps · `test5.mp4`, 아래 "테스트 영상 내용"). 원래의 VFR 테스트 파일 `recording.webm` 은 없어졌고, 대신 합성 파일 `tests/ui/media/vfr60_nodur.webm` 을 쓴다 |
| `tests/ui/` | UI 자동화 회귀 테스트 (PowerShell + UI Automation). 아래 "테스트" 참고 |
| `README.md` | GitHub 첫 화면 (2026-11-02 작성 — 기능·빌드·사용법·테스트·라이선스). 화면이 크게 바뀌면 같이 고칠 것. 스크린샷(test4 3초 장면, 실제 도로 CCTV 프레임)은 같은 날 넣었다가 영상 사용 권리를 확인하지 못해 뺐다(`docs/screenshot.png` 삭제 — 이 PC 의 옛 기록 `old-history` 의 `10e65db` 에만 남음). 다시 넣으려면 권리가 분명한 영상으로 |
| `LICENSE` · `THIRD-PARTY-NOTICES.md` | 저장소 라이선스 GNU AGPL-3.0 (2026-11-02 — 들어 있는 `best.onnx` 가 Ultralytics YOLO26 파인튜닝이라 Ultralytics 가 프로젝트 전체에 AGPL-3.0 을 요구)과 저장소 안 남의 저작물 고지(모델 AGPL-3.0 · `WindowsMlExecutionProvider.cs` 의 바탕 YoloDotNet MIT · `.claude/` 의 graft 연동 파일 MIT — 각 전문). 남의 코드·파일을 가져오면 여기에 더한다 |
| `tools/` | `Make-NpuModel.ps1`(+ `npu_quantize.py`): YOLO 모델의 NPU 용 짝 `<이름>.npu.onnx` 만들기 (아래 "NPU") |
| `best.npu.onnx` | 루트 `best.onnx` 의 NPU 짝 (A16W8·가중치 채널별 QDQ, 10.4 MB, git 추적). 원본을 바꾸면 다시 만든다 |
| `experiments/` | 따로 빌드하는 실험 프로젝트(각자 `.csproj`). 본 `.csproj` 의 `Compile/Page/None Remove` 로 빠져 있다. `NpuBench/`: AMD NPU 실험 — 짝 확인(`npu_quant`·`npu_source_md5`) 뒤에만 NPU 에 넣는다 (아래 "NPU"). `RuntimeParity/`: 런타임 교체 전후 검출 결과 비교 (1번 항목, GPU 선택은 두 빌드 모두 `GpuAdapters.cs` 링크). `TrackerCheck/`: 추적기 검증 (아래 "추적기")·테두리 멈춤 판정 합성 장면(`selftest` — 관심 구역 `Roi.cs` 의 판정·파일 검사 ⑧, 화면 전환 감지 합성 장면 ⑨, 구역 진입·나감 이벤트 ⑩, 유고 규칙 ⑪, 화면 식별 ⑫도 여기)·화면 식별 맞춤 실측(`views`)·화면 전환이 있는 합성 영상 만들기(`mkrevisit`)·화면 전환 감지 임계값 실측(`scene`)·GPU 고성능/저전력 판정(`gpu`)·중복 박스 필터 전후 비교 (3번 항목). `RfDetrBench/`: RF-DETR 를 앱 런타임(DirectML)에서 — 노드 배치·속도·결과 일치, 같은 COCO 정확도의 YOLO26 과 비교. `step2/` 는 같은 데이터로 학습(WSL)·내보내기·시험 분할 평가 (아래 "RF-DETR 검토"). 산출물 `*/out/`·`*/bin/`·`*/obj/`·`*/.venv/` 는 gitignore |
| `graft/` · `.claude/` | 코드 인덱스(graft)와 그 에이전트 연동. graft 는 CLI 로만 쓴다 — MCP 서버 등록은 2026-11-02 사용자 결정으로 전역(`~/.claude.json`)과 이 저장소(`.mcp.json` 삭제) 모두 뺐다. `graft/` 는 재생성 가능한 캐시라 커밋하지 않는다. `.claude/helpers/graft-statusline.cjs`(상태줄)는 graft 설치 경로를 `%APPDATA%\npm` 에서 계산한다(2026-11-02 — graft 가 박아 넣은 `C:\Users\<이름>\…` 경로를 저장소에서 뺌. graft 를 다시 설치해 박힌 경로로 돌아오면 다시 고칠 것). graft 훅은 전역 `~/.claude/settings.json` 에만 둔다 — 프로젝트에도 있으면 둘 다 돌아 세션마다 graft 안내문이 두 번 들어가고 도구마다 훅이 두 번 돈다(2026-11-02 프로젝트 쪽 훅과 `graft-hooks.cjs` 를 뺌. graft 를 이 저장소에 다시 설치해 돌아오면 다시 뺄 것) |

## 절대 건드리면 안 되는 것들 (실제로 한 번씩 터졌던 부분)

### 1. ONNX Runtime 은 Windows ML 것 하나만 (2026-10-24 `spike/windows-ml-runtime` 에서 교체)
```xml
<PackageReference Include="Microsoft.Windows.AI.MachineLearning" Version="2.4.89" />
```
- Windows ML 은 `Microsoft.ML.OnnxRuntime.dll`(관리 어셈블리, **버전 0.0.0.0**, 서명 `f27f157f0a5b7bb6`)과 `onnxruntime.dll`(1.27)·`DirectML.dll` 을 가져온다.
  `Microsoft.ML.OnnxRuntime.*` 나 `YoloDotNet.ExecutionProvider.*` 패키지를 넣으면 **이름이 겹친다**. 그 공급자들은 ORT 1.23.0.0 에 묶여 컴파일돼서 0.0.0.0 으로는 로드조차 안 된다
- 그래서 실행 공급자는 직접 둔다: `WindowsMlExecutionProvider.cs` — YoloDotNet `IExecutionProvider` 구현 (YoloDotNet 의 DirectML 공급자 MIT 소스 바탕).
  YoloDotNet 본체는 ORT 를 참조하지 않고 세션을 리플렉션으로 읽는다(`ParseOnnx`). GPU 는 Windows ML 에 들어 있는 DirectML 로 돈다 (EP 카탈로그 다운로드 없음).
  원본과 다른 점 두 가지(2026-10-25): FP16 출력 버퍼를 **모든 차원의 곱**으로 잡는다(원본은 앞 두 차원만 곱해 분할 마스크 같은 4차원 출력에서 범위를 넘음 — 지금 모델은 FP32 라 해당 없음),
  생성자에서 세션을 만든 뒤 실패하면 세션을 해제하고 넘긴다
- DirectML 장치는 DXGI 어댑터의 **PCI 벤더·장치 ID 로** 맞춘다(`SelectBestGpu` → `FindDmlDevice`). 하이브리드라 DirectML 장치가 AMD·NVIDIA 둘이다. 못 찾으면 첫 DirectML 장치로 돌고 제목에 `<벤더> GPU` 로 드러난다
- 검증법: 출력의 `runtimes\win-x64\native\onnxruntime.dll` 이 **22.0 MB · 파일 버전 1.27**, `DirectML.dll` 17.8 MB(1.15.4). 실행 중 프로세스 모듈에 이 둘이 올라와 있어야 한다
- TFM 은 `net10.0-windows10.0.19041.0` (Windows ML 의 WinRT API 때문에 SDK 버전 필요). **22621 이상으로 올리지 말 것** — `Vortice.DXGI` 가 그 TFM 전용 빌드로 바뀐다. Debug 출력도 `bin\Debug\net10.0-windows10.0.19041.0\` 로 바뀌었다
- 교체 전후 비교(2026-10-24, 단일 파일 배포본, test1.mp4 재생 처리 FPS 5회 중앙): 기존 DirectML 1.23 **98.5 / 98.6** vs Windows ML **99.1** — 차이 없음. exe 119.4 → 126.4 MB. UI 테스트 3종 통과
- **검출 결과는 비트 단위로 같다**: test1~5 각 40프레임(200장)·검출 2538개가 좌표·신뢰도까지 완전히 일치(결과 CSV 해시 동일). 같은 런타임 두 번 실행도 완전히 같다(DirectML 결정적).
  `experiments/RuntimeParity/`(같은 소스를 `Old/`·`WinML/` 로 두 번 빌드) — 런타임을 다시 바꿀 때 같은 방법으로 확인한다.
  참고로 옛 조합은 `DirectML.dll` 을 배포하지 않고 **OS 의 `System32\DirectML.dll`(1.15.6)** 을 썼다. Windows ML 은 자기 것(1.15.4)을 exe 에 넣는다
- 이전 조합(`Microsoft.ML.OnnxRuntime.DirectML`/`.Managed`/`ExcludeAssets` 세 줄, `onnxruntime.dll` 16.4 MB 검증)은 `main` 기록에 있다.
  그때의 교훈은 그대로다: 코드에서 쓰지도 않던 `YoloSharp` 6.1.0 이 `Microsoft.ML.OnnxRuntime >= 1.23.2` 를 끌어와 CPU판으로 되돌려놨었다. **ONNX 관련 패키지를 추가할 때 이 충돌을 먼저 의심할 것.**
- 미사용 패키지(`Compunet.ScreenCapture`, `SixLabors.ImageSharp`, `OpenCvSharp4.Extensions`/`.WpfExtensions`/`.Windows`)는 2026-09-11 제거했다. `Vortice.Direct3D11` 은 DXGI 캡처용으로 추가(`Vortice.DXGI` 와 같은 3.8.3 으로 맞출 것).
- OpenCvSharp 두 패키지(관리 `OpenCvSharp4`·네이티브 `OpenCvSharp4.runtime.win`)는 **같은 날짜 판**이어야 한다. 판은 저장소 루트 **`Directory.Build.props` 의 `OpenCvSharpVersion` 한 곳**이고,
  본 앱과 실험 프로젝트 넷(NpuBench·TrackerCheck·RuntimeParity/Old·WinML)의 두 `PackageReference` 가 모두 그 값을 쓴다. 지금 판은 4.13.0.**20260302**(두 nuspec 이 같은 원본 커밋 shimat/opencvsharp `4f66185` 을 가리키는 짝).
  초기 커밋부터 관리 20260526 + 네이티브 20260302 로 어긋나 있었다 — 관리 DLL 의 P/Invoke 진입점 2879개 중 117개(`aruco_*`·`saliency_*`·`shape_*`·`ximgproc_*` 등)가 네이티브에 없었고
  시그니처가 바뀐 것도 10개(LineSegmentDetector·MergeDebevec·MergeMertens) 있었다. 빌드는 되고 부르는 순간 터진다.
  runtime.win 에 20260526 판이 없어 관리 쪽을 내렸다(2026-10-31, 이슈 #9): 네이티브 DLL(`OpenCvSharpExtern.dll`·`opencv_videoio_ffmpeg4130_64.dll`)은 바이트 단위로 그대로,
  앱 컴파일 결과(IL·MemberRef 750개)도 같고, test1~5·`vfr60_nodur.webm` 7,696 프레임의 디코딩·시크·CvtColor·그리기와 RuntimeParity 검출 CSV(2538개)도 옛 짝과 완전히 같았다.
  **판은 `Directory.Build.props` 에서만 바꾼다** — `dotnet add package` 로 한쪽만 올리면 그 줄의 `$(OpenCvSharpVersion)` 이 숫자로 덮여 다시 어긋난다(속성이 비면 복원이 NU1015 로 멈춘다).
  바꿀 땐 ① NuGet 에 그 날짜의 runtime.win 이 있는지 먼저 보고 ② 네이티브가 바뀌면 아래 "파일 재생 · 시크"의 OpenCV 동작·검출 결과(RuntimeParity)·UI 테스트를 다시 본다.
  참고(10-31 평가만, 채택 안 함): 20260627 짝은 네이티브를 다시 빌드해 contrib 13개가 빠졌고(bioinspired·ccalib·dpm·stereo·videostab 등) 관리 쪽 AssemblyVersion 이 4.0.0.0 이다.
  `Directory.Build.props` 는 MSBuild 가 위로 올라가며 처음 찾은 하나만 읽어서 `old\*.csproj`·`.claude\worktrees\` 의 빌드도 가져간다 — 지금은 속성 하나라 무해하지만 타깃을 넣으면 그 빌드들에도 걸린다

### 2. `TargetClasses` 의 `"scooter"` 는 의도된 것
```csharp
["person", "car", "motorcycle", "truck", "bus", "scooter", "bicycle"]
```
COCO-80에 없는 클래스처럼 보여도 **사용자가 의도한 것**이다. 정리 대상이 아니다.
루트 모델의 클래스가 정확히 이 7개(`bicycle, bus, car, motorcycle, person, scooter, truck`)라서 그 모델로는 필터가 걸러내는 게 없다.
모델을 바꿔 쓸 때의 안전장치다 — 실제로 배포 폴더에 COCO 80클래스 YOLO26x 를 넣어 쓴 적이 있고, 그땐 이 필터가 6개 클래스만 남긴다(COCO 엔 scooter 없음).

### 3. 모델은 end-to-end NMS 내장
출력 `[1,300,6]`, 입력 고정 `[1,3,640,640]`(`dynamic:False`). 추론 해상도는 **모델이 결정**하며 코드로 바꿀 수 없다.
따라서 단일 프레임 추론 후 **일반 NMS(교차 클래스 포함)는 다시 돌리지 않는다**(SAHI 타일 병합은 별개).
단, **같은 자리 중복 박스는 실제로 나와서 그것만 뺀다** (2026-10-24 실측: test4 916 프레임 중 466 프레임에 같은 묶음 IoU>0.7 쌍, 대부분 **같은 클래스 IoU≈1.0** —
`RunObjectDetection` 의 `iou` 인자가 이 end-to-end 모델엔 걸리지 않는 것으로 보인다. 범위만 다른 중복(짐칸·탑승자 포함/제외, IoU 0.5~0.7)과 같은 자리 car+truck 도 있다).
예전엔 겹쳐 그려져 티가 안 났지만 라벨이 겹치고 상태바·HUD·SAHI 검출 수가 부풀었다.
- 규칙은 `Detection.cs` 의 **`DuplicateBoxes` 하나** — 앱 표시와 추적기가 같이 쓴다. 같은 묶음(차량 6종은 한 묶음)이고 작은 박스의 80% 이상이 큰 박스 안(IoS ≥ 0.8)
  **이고** 넓이비 ≥ 0.5 일 때만 중복, 신뢰도 높은 쪽을 라벨·좌표 그대로 남긴다. 일반 NMS 가 아니라서 나란히 일부만 겹친 두 차,
  버스 박스에 거의 다 들어가는 옆 차(넓이비 0.13~0.16)는 남는다. 이 방향(중복만 뺀다, 일반 NMS 아님)은 2026-10-25 사용자가 골랐다.
  판정은 신뢰도 순 대표와 **직접** 겹치는 박스만 흡수한다(흡수된 박스는 다른 박스를 흡수하지 않음 — 비이행). 그래서 사슬 중복 A⊃B⊃C 에서
  C 와 A 의 넓이비가 0.5 미만이면 박스 둘(추적기도 트랙 둘)이 남는다 (`Detection.cs` 주석, 2026-10-30 확인)
- 넣은 곳: `ProcessFrame`(그리기 전), HUD(`DetectBgra` 결과를 받자마자), SAHI(**타일 병합(`MergeCutPieces`) 뒤**). SAHI 를 타일마다 먼저 빼면
  병합 입력이 바뀌어 중복 아닌 박스가 빠지거나(test4 20.0초 원거리 car, test5 44.75초 타일 경계에 걸린 탱크로리 아랫부분) 병합이 지웠던 박스가
  되살아났다(test5 29.25초) → 병합 결과는 그대로 두고 그 위에서 뺀다. 이러면 다른 타일에서 나온 car/truck 중복(병합은 같은 라벨끼리만)도 빠진다
- 실측(2026-10-25, `experiments/TrackerCheck` 의 `dedup`): 프레임 검출 test4 18795 → **17892개(−4.8%)** (같은 라벨 IoU>0.7 553 · 같은 라벨 범위만 다름 294 ·
  차량 라벨 다름 56), test5 5451 → **5289개(−3.0%)**. 목표 정지 차량(test4 덤프트럭·test5 갓길 화물차) 자리에 박스가 둘 이상인 프레임 1·2 → 0.
  SAHI(15프레임에 1장) 병합 결과 test4 2054 → 2028, test5 468 → 427 — 빠진 것은 모두 남은 박스의 중복이고 새로 생긴 박스 없음.
  앱으로 test4 를 1.1·4.9·23.9초에서 멈춰(SAHI 포함) 눈으로 봤을 때 겹친 라벨이 사라지고 빠진 차 없음
- 한계(확인 안 함): 크기가 비슷한 두 물체가 앞뒤로 거의 다 겹치면(작은 쪽 80% 이상이 큰 쪽 안) 하나로 친다 — 사람 둘이 앞뒤로 선 경우 등

### 4. DXGI GPU 선택 시 `PointerUSize`
어댑터 열거는 `GpuAdapters.Enumerate` 한 곳이다. `AdapterDescription1.DedicatedVideoMemory` 는 `SharpGen.Runtime.PointerUSize` 다.
`(long)` 캐스팅하면 **`OverflowException`** 이 나서 GPU 이름이 fallback("GPU")으로 표시된다. `(ulong)` 을 쓸 것.

### 5. `old/` · `publish*/` 빌드 제외 글롭
SDK 기본 글롭이 `old/**` 의 소스와 생성 `.g.cs` 까지 끌어와 **724개 컴파일 에러**가 났던 적이 있다.
`.csproj` 의 `Compile/Page/ApplicationDefinition/Resource/None Remove` 항목을 지우지 말 것. (`old/` 에 소스 백업을 넣어도 안전한 이유)

## 도메인 지식 — 파일 재생 · 시크

화면 녹화 webm/mp4 는 **VFR(가변 프레임레이트)** 이고 메타데이터가 거짓이다. 두 종류를 봤다:
- 길이는 있지만 fps 가 타임베이스 값: `FPS=1000`, `FRAME_COUNT=33668`(실제 1011), `Set(POS_MSEC)` 가 `false` 반환 (예전 `recording.webm`).
  같은 꼴의 합성 표본 `tests/ui/media/vfr_fps1000_dur.webm`(FPS=1000·FRAME_COUNT=24007, 실제 550장, 6~12초 정지 구간)을 2026-10-31 만들었다(생성 스크립트 `make_vfr_fps1000_dur.py`, 판 고정)
- **길이 정보가 아예 없음**(브라우저 MediaRecorder 녹화 webm 이 대표적): `FRAME_COUNT` 가 음수 쓰레기값(-5.5e17).
  이런 파일에 `Set(PosFrames)` 를 하면 OpenCV 가 목표를 그 음수로 잘라 **스트림이 망가진다** — `true` 를 돌려주지만
  이후 `Read` 가 곧바로 실패해 EOF 로 보인다 (2026-09-23 실측, 이전부터 있던 버그)

적응형 전략이 구현되어 있다:
1. `metaReliable = fps > 0 && fps <= 240 && !NaN && frameCount > 0` 로 1차 판정
2. `ProbeSeekConsistent()` — 중간 프레임으로 시크해 실제/예상 오차를 보고 판정 (~0.1초). `frameCount ≤ 0` 이면 시크하지 않고 바로 false
3. 불일치 시 `ScanFrames()` 로 전체 grab 스캔 (수 초~15초). **반드시 백그라운드에서**, `_loadGeneration` 으로 결과 무효화
4. 시크는 메타데이터를 믿는 파일(CFR)만 `PosFrames` 사용(`PosMsec` 은 신뢰 불가). VFR이면 `_seekRefineTargetMs` 를 세팅해 키프레임 스냅 지점부터 목표 시각까지 `Grab()` 으로 따라잡고 마지막만 `Retrieve()`
5. **예외: 메타데이터를 못 믿는 파일(`_seekByGrab` = 열 때 `metaReliable` 이 false — 길이 없음이든 fps 가 0·NaN·240 초과든, 탐침 결과와 상관없이)은 `Set(PosFrames)` 를 절대 쓰지 않는다**
   (2026-10-31 이슈 #7 — 예전엔 `FRAME_COUNT ≤ 0` 일 때만이었다).
   앞으로는 4의 grab 정밀화로만 따라가고, 뒤로는 파일을 다시 열어(`_filePath`) 처음부터 따라간다. 긴 파일은 느리지만 정확하다.
   되감을지 판단은 캡처의 `PosMsec` 가 아니라 루프가 기억한 `_lastPosMs` 로 한다 — EOF 뒤엔 `PosMsec` 를 믿을 수 없어
   "끝난 뒤 재생 → 처음부터"가 안 됐다. grab 도중 새 시크가 오면 멈추고 새 목표로 간다 (슬라이더를 끄는 동안 옛 목표까지 다 가지 않게).
   정지·종료(`ct`)도 grab 을 멈춘다 — 안 그러면 `StopPlaybackAsync` 가 긴 grab 이 끝날 때까지 기다린다.
   grab 정밀화는 **횟수로 자르지 않는다** (예전 20만 회 상한은 60fps 55분 넘는 파일에서 처음부터 따라가는 시크를 중간에 세웠다).
   대신 시각이 1000번 연속 늘지 않으면 멈춘다 (타임스탬프 없는 스트림에서 끝없이 도는 것만 막음)

OpenCV(FFmpeg 백엔드)의 프레임 시크는 넘긴 번호를 자기 fps 로 시각에 바꿔 그 앞 키프레임으로 간 뒤, **키프레임 시각을 그 fps 의 프레임 번호로 놓고
실제 프레임 한 장마다 1씩 세며** 따라간다(Set 뒤 `PosFrames` 로 확인). 그래서 fps 가 실제 속도가 아닌 파일엔 어느 단위로 번호를 넘겨도 목표 시각에 서지 않는다.
fps=1000 형(길이 있음)은 예전엔 분석 뒤 실측 평균 fps 로 번호를 만들어 '처음부터 N번째 프레임'에 섰고, VFR 이라 프레임 수가 시각에 비례하지 않아
정지 구간 뒤 목표는 지나쳤으며 정밀화는 앞으로만 가서 위치가 틀렸다(표본: 16초 → 18.06, 13초 → 15.76, 9초 → 12.51, 멈춘 채 13.5초 → 16.23).
탐침이 우연히 통과해 fps 1000 을 쓰면 (목표 − 키프레임) ms 만큼 '프레임'을 따라가 대부분 EOF 였다(9번 중 7번).
2026-10-31 부터 5의 grab 경로로 간다(16.009 · 13.024 · 9.015 · 13.521초, 시크 한 번 3~39 ms — 360p 표본은 grab 이 장당 0.1 ms 안팎, 720p H.264 실영상은 약 1 ms).
긴 fps=1000 녹화의 되감기는 처음부터 디코딩이라 목표 시각에 비례해 느리다. 240fps 를 넘는 진짜 고속 촬영 CFR 파일도 이제 grab 으로 시크한다(느리지만 정확, 표본 없음)

**시크는 UI 스레드에서 하지 않는다.** `Set(PosFrames)` 1회가 100~200 ms 라서, UI(`SeekToMs`)는 `_pendingSeekMs` 에 값만 기록하고
재생 루프가 다음 `Read` 직전에 최신 요청 1건만 적용한다(`ApplyPendingSeek`). 시크 대기 중엔 루프가 슬라이더·시간 표시를 되돌리지 않는다.
슬라이더 값을 코드에서 바꿀 때는 `_isPlaybackUpdatingSlider` 로 감싸야 `ValueChanged` 가 시크를 요청하지 않는다.

**파일은 "재생 시계"로 원본 속도를 맞춘다** — 프레임마다 "(1/fps − 처리 시간)만큼 기다리기"로 되돌리지 말 것.
그 방식은 `Task.Delay` 가 Windows 타이머 단위(15.6 ms)로 올림돼 오차가 매 프레임 쌓여 **30fps mp4 가 0.82배속**이었고,
fps 를 루프 시작 때 한 번만 읽어 분석 전 임시값(30fps)으로 시작하는 VFR 은 **60fps 파일이 0.41배속**이었다 (2026-09-23 실측).
지금은 각 프레임의 영상 시각(`posMs`)이 벽시계와 같이 흐르도록 **그 프레임이 보일 시각까지 기다렸다 표시**한다(`WaitUntilDueAsync`).
올림 오차는 다음 프레임에서 상쇄되고, VFR 도 실제 타임스탬프대로 나온다. 시계는 시크·재개 때 다시 맞추고(멈춘 동안 흐른 시간을
따라잡으려 몰아 재생하지 않게), 처리가 250 ms 넘게 밀리면 따라잡지 않고 지금을 새 기준으로 삼는다.
기다림은 50 ms 조각으로 나눠 시크·일시정지가 오면 바로 끝낸다 (VFR 녹화는 한 프레임이 수 초씩 이어질 수 있음).
기다리는 사이 **일시정지·SAHI 로 멈췄으면 그 프레임을 보이지 않고 재개될 때까지 들고 있는다** — 바로 보이면 멈춘 화면이
다음 프레임(VFR 은 수 초 뒤)으로 넘어가고 SAHI 가 화면에 없던 프레임을 분석한다. 그래서 `_lastFrame` 맞바꾸기도 기다린 **뒤에** 한다.
멈춘 사이 시크했으면 들고 있던 프레임은 버린다

**멈춘 채 시크하면 그 위치 한 장만 보인다** (2026-10-25, 예전엔 슬라이더만 옮겨지고 화면·SAHI 대상이 옛 프레임 그대로였다).
`ValueChanged`(드래그 중이 아닐 때)·드래그 끝(`EndSliderDrag`)이 `RequestStepFrame` 으로 `_stepRequest` 를 세우면, 루프가 `_playSignal` 을 기다리는 곳
(루프 맨 위, 위의 "들고 있기" 둘 다)에서 50 ms 마다 보고 한 장을 읽어 기다림 없이 보인 뒤 다시 멈춘다. `_playSignal` 을 `Set/Reset` 하지 않는 별도 깃발이라
그 사이 사용자가 재생·일시정지를 눌러도 상태가 꼬이지 않는다.
'끄는 중'(`_isUserDraggingSlider`)은 썸의 `Thumb.DragStarted`/`DragCompleted` 로 세우고 내린다 (2026-10-31, 이슈 #8). 썸은 놓을 때뿐 아니라 버튼을 누른 채 캡처를 잃어도
(Alt+Tab·캡처를 가져가는 창·슬라이더가 꺼짐) 스스로 `CancelDrag` 해 `DragCompleted(Canceled)` 를 올린다. 예전엔 `PreviewMouseLeftButtonUp`·`LostMouseCapture`(버튼이 떼어졌을 때만)로
끝내서, 썸을 잡은 채 Alt+Tab 하고 앱 밖에서 놓으면 깃발이 남았다 — 재생 중 진행 표시가 멈추고, 멈춘 채 시크해도 한 장 보기가 안 나왔다.
안전망: 창 `Deactivated` 때 아직 끄는 중이면 썸의 캡처를 놓아 드래그를 끝내고(`CancelDrag` 를 바로 부르면 그 안의 `ReleaseMouseCapture` 가 `CancelDrag` 를 한 번 더 불러
`DragCompleted` 가 두 번 온다 — IL 로 확인), 파일을 열 때도 깃발을 내린다. 트랙 클릭은 드래그가 아니다 — `IsMoveToPointEnabled` 면 Slider 가 그 `PreviewMouseLeftButtonDown` 을
Handled 로 만들고 썸도 드래그를 시작하지 않는다 → `ValueChanged` 가 바로 한 장 보기를 요청한다. 실제 마우스 드래그·Alt+Tab 은 UIA 로 못 해 손으로만 확인한다 — 2026-11-01 사용자가 확인, 문제 없음.
SAHI 는 시크가 대기 중이면 한 장 보기를 요청하고, 그 프레임이나 재생 루프가 **처리 중인** 시크 프레임이 화면에 나올 때까지(`WaitForStepFrameAsync`, 최대 10초) 기다린 뒤 분석한다.
루프는 시크·한 장 보기 요청을 **꺼내기 전에** `_stepping` 을 세우고(꺼낸 뒤 세우면 그 틈에 SAHI 가 둘 다 '없음'으로 보고 옛 프레임을 집었다), 시크로 얻은 프레임은 멈췄어도
들고 있지 않고 보인다 (2026-10-30 — 예전엔 재생 중 시크를 처리하는 사이(CFR 수십 ms~0.2초, 길이 없는 webm 되감기·VFR 정밀화는 길면 수 초) 일시정지·SAHI 를 누르면
시크한 프레임이 '들고 있기'에 걸려 안 보이고 SAHI 는 시크 전 프레임을 분석했다).
SAHI 의 대기 조건엔 **시크 대기(`SeekPending`, 파일만)** 도 들어간다 — 재생 중이던 루프는 남은 한 장 보기 요청을 루프 맨 위에서 지운 뒤 시크를 꺼낼 때에야
`_stepping` 을 세워서, 그 몇 µs 틈에 보면 요청도 처리 중도 없었다(2026-10-30 Gemini 교차 검증, 10-31 반영. 파일만 보는 건 `_pendingSeekMs` 를 파일을 열 때만 비워
시크 직후 카메라로 바꾸면 남기 때문 — 그대로면 카메라 SAHI 가 10초 기다리다 실패한다). 재생 중 시크한 직후 [일시정지]를 눌러도(루프가 시크를 꺼내기 전) 그 위치 한 장을 보인다
(`Btn_PlayPause_Click` 이 `RequestStepFrame` — 예전엔 루프가 옛 위치 프레임을 들고 멈춰 슬라이더만 새 위치였다). 한 장 보기 요청인데 꺼낼 시크가 없으면 읽지 않고 맨 위로 돌아간다 —
요청은 시크를 걸 때만 하므로 앞 반복이 이미 꺼내 보인 시크에 남은 요청이고, 그대로 읽으면 한 프레임 더 나가 멈췄다(VFR 녹화면 수 초 뒤. 일시정지·SAHI·멈춘 채 연속 시크가 그 틈에 걸릴 때).
10초 안에 안 끝나면 SAHI 는 분석하지 않고 `시크한 위치를 아직 읽는 중 — 잠시 뒤 다시 누르세요` 를
띄운다(예전엔 1초 뒤 옛 프레임을 분석했다). SAHI 동안엔 재생·SAHI 버튼과 **슬라이더도** 끈다(`_sahiBusy` 를 `ApplyModeToUi` 가 본다 — 예전엔 분석 중 시크한 한 장 위를
옛 프레임의 SAHI 결과가 덮었다). SAHI 완료 문구 끝엔 분석한 프레임 위치가 붙는다(`정밀 분석 완료: N개 (mm:ss)`, 파일만 — `_lastFramePosMs` 를 `_lastFrame` 과 같이 바꾼다).
UI 테스트가 이걸로 시크한 위치를 분석했는지 본다

**재생 루프는 EOF/카메라 유실에서 절대 `break` 하지 않는다.** `continue` 로 `_playSignal` 에 파킹해 복구 가능 상태를 유지한다 (`break` 하면 재생이 영구히 죽는다).
EOF 로 멈춘 뒤 재생을 누르면 **처음부터** 다시 튼다(`_atEof` → `SeekToMs(0)`). 그냥 재개하면 EOF 를 한 번 더 읽고 곧바로 다시 멈춘다.
슬라이더로 위치를 옮기면 `SeekToMs` 가 `_atEof` 를 지워 그 위치부터 재생된다. 이때 '재생 완료' 문구도 `파일: <이름>` 으로 되돌린다
(2026-10-30 — 예전엔 그 위치부터 재생하는 내내 '재생 완료'가 남았다).
시간 표시(`UpdateTimeText`)는 길이나 위치가 1시간 이상이면 `h:mm:ss` 다 — `mm\:ss` 는 분 성분(0~59)뿐이라 65분이 `05:00` 으로 보였다(2026-10-30).

재생 루프의 디코딩 버퍼는 `_lastFrame`(SAHI용)과 **맞바꿔 재사용**한다 — 매 프레임 `Clone()` 하지 않는다. `ProcessFrame` 은 박스를 어디에도 그리지 않고 돌려주므로(화면에선 영상 위 `FrameBoxes` 가 그린다) 보관본은 깨끗하다.
`BitmapSource.Create` 는 `IntPtr` 오버로드로 네이티브 메모리에서 바로 만든다 (중간 `byte[]` 는 LOH 할당으로 Gen2 GC 를 부름).

## 도메인 지식 — 화면 HUD

### 오버레이
- 오버레이 창: `WS_EX_TRANSPARENT`(클릭 통과) + `WS_EX_LAYERED` + `WS_EX_TOOLWINDOW`(Alt+Tab 숨김)
- `SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)` — **이게 없으면 그린 박스가 다시 캡처되어 피드백 루프**가 생긴다. DXGI·GDI 캡처 모두에서 제외됨을 실측 확인
- 창 배치는 WPF `Left/Top`(DIP)이 아니라 **`SetWindowPos` 물리 픽셀**로 한다. 혼합 DPI 다중 모니터에서 어긋나기 때문. HWND 생성 전 호출에 대비해 `_pendingBounds` 로 지연 적용
- `AllowsTransparency=True` 창은 비싸다. 박스마다 요소를 만들지 않고 `BoxLayer.OnRender` **한 번에 전부** 그린다(모니터 왼쪽 위 클래스별 개수 범례 포함 — 아래 "화면 디자인"). Pen/Brush 는 `App.xaml` 의 `Box.*` 로 처음 그릴 때 한 번 만들어 얼려 둔다(`BoxLayer.Theme`, 화면 배율이 바뀌면 다시)

### 캡처 (DXGI Desktop Duplication, 대체 경로 GDI)
- `DuplicateOutput` 은 **그 모니터가 연결된 어댑터**의 D3D11 장치로만 호출 가능하다. 모니터 장치 이름(`\\.\DISPLAYn`)으로 출력을 찾아 그 어댑터에 장치를 만든다. 이 PC는 하이브리드라 노트북 화면은 **AMD 내장 GPU**, 외부 모니터는 **NVIDIA** 에 붙어 있다 — 추론 GPU(NVIDIA)와 캡처 GPU가 다를 수 있다 (두 경우 모두 실측 확인)
- **하이브리드 GPU 함정**: 출력이 어느 어댑터 아래로 열거되는지는 물리 연결이 아니라 **프로세스의 GPU 모드**가 정한다.
  프로세스가 고성능(외장) GPU 모드로 뜨면 노트북 화면(`DISPLAY1`)도 NVIDIA 아래로 보이고, `DuplicateOutput` 이
  `DXGI_ERROR_UNSUPPORTED`(0x887A0004)로 실패해 GDI 로 떨어진다. **코드 버그가 아니다.**
  - **해결: Windows 설정 → 디스플레이 → 그래픽에서 `DetectView.exe` 를 "절전"으로** (검증됨, 2026-09-23).
    캡처는 AMD 에서 DXGI 로 되고, 추론은 `SelectBestGpu` 가 고른 NVIDIA 에서 그대로 돈다
    (9/23 엔 어댑터 번호로 골랐고, 10/24 Windows ML 교체 뒤엔 PCI 벤더·장치 ID 로 DirectML 장치를 맞춘다 — 1번 항목)
    (성능 카운터: NVIDIA 11% · AMD 1.7% 는 UI 렌더링뿐, YOLO26s 파일 재생 ~98 FPS)
  - 이 설정은 **실행 파일 경로별**이다(`HKCU\Software\Microsoft\DirectX\UserGpuPreferences`, `GpuPreference=1` 절전 · `2` 고성능 · `0` Windows에서 결정).
    `publish\DetectView.exe` 는 9/23 부터 '절전'이었다가 **2026-10-24 저녁 사용자가 'Windows에서 결정'(`0`)으로 바꿨다**
    (외부 모니터 HUD 가 '절전'에서 GDI 로 떨어져서). exe 를 다른 폴더·PC 로 옮기면 다시 설정해야 한다
  - 설정이 없는 실행 파일이 9/11 엔 내장 GPU 모드(DXGI 정상), 9/23 엔 외장 GPU 모드로 떴다 — 기본 동작이 바뀐 이유는 미확인
  - **반대 사례 (2026-10-24 실측): HUD 대상이 NVIDIA 쪽 외부 모니터면 '절전'이 오히려 DXGI 를 깨뜨린다.**
    모니터 0 = 주 모니터 `DISPLAY6`(2560×1440, 외부)일 때 `publish`('절전')에서는 옛 런타임 exe·새 런타임 exe **둘 다** GDI(HUD 33 FPS).
    설정이 없는 exe(`bin\Debug`·scratchpad 배포본, 두 런타임 모두)는 같은 모니터에서 DXGI 였다 → 런타임과 무관하고 GPU 모드 탓이다.
    즉 '절전'은 노트북 화면(AMD)을 HUD 할 때의 해결책이고, '고성능'은 반대로 노트북 화면을 깨뜨린다(9/23).
    **설정이 없으면(Windows에서 결정) 10/24 엔 모니터마다 자기 GPU 아래로 열거돼 두 모니터 모두 DXGI 였다.**
    외부 모니터가 어느 GPU 에 붙는지는 꽂은 단자(USB-C/HDMI)에 따라 달라질 수 있다 — 9/11 과 9/23 의 차이도 이것일 수 있다 (추정, 미확인)
  - DXGI 가 안 되면 상태바에 **할 일**을 짧게 표시하고, 자세한 단계와 **지금 실행 중인 exe 경로**(설정이 경로별이라)는 상태바 툴팁(`FallbackHelp`)에 둔다.
    문구는 `ScreenCapture.DescribeDxgiFailure` → `HybridGpuAdvice` 한 곳에서 만든다. **권하는 설정은 모니터가 실제로 붙은 GPU 와 지금 설정에 따라 고른다** (2026-10-24):
    | 모니터가 붙은 GPU | 이 exe 의 지금 설정 | 상태바 |
    |---|---|---|
    | 저전력(노트북 화면) | '고성능' | `느림 — 그래픽 설정의 '고성능' 지정을 풀고 재시작` |
    | 저전력 | 없음 / Windows에서 결정 | `느림 — 그래픽 설정에서 이 앱을 '절전'으로 지정 후 재시작` |
    | 고성능(외부 모니터) | '절전' | `느림 — 그래픽 설정의 '절전' 지정을 풀고 재시작` (10/24 실제로 나온 안내) |
    | 고성능 | 없음 / Windows에서 결정 | `느림 — 그래픽 설정에서 이 앱을 '고성능'으로 지정 후 재시작` |
    | 이미 그 GPU 로 지정됨 · GPU 하나 · 판별 실패 | — | `DXGI 불가: 0x887A0004 (원인 미상)` (툴팁에 모니터·GPU·판별·설정) |
  - 판별은 `DisplayTopology.cs`: 모니터가 붙은 어댑터는 **`QueryDisplayConfig` 의 대상 어댑터 LUID**(시스템 구성이라 프로세스 GPU 모드와 무관 — '절전' 프로세스에서도 NVIDIA 로 맞게 나옴),
    저전력/고성능은 **내장 GPU 인지**로 가른다(`GpuAdapters.IsHighPerformance` — 추론 GPU 선택 `SelectBestGpu` → `GpuAdapters.Best` 와 같은 함수). 지금 설정은 레지스트리를 **읽기만** 한다.
    모든 어댑터의 내장 여부(DXCore `IsIntegrated`, LUID 로 맞춤)를 읽었고 내장·외장이 섞여 있으면 내장 = 저전력·외장 = 고성능(메모리 크기는 안 봄).
    그 밖(모두 같은 종류·하나라도 못 읽음 — dxcore.dll 은 Windows 10 2004+)엔 예전처럼 전용 VRAM 최대 = 고성능(같으면 모두). 추론 GPU 는 고성능 쪽 중 VRAM 최대, 같으면 먼저 열거된 것.
    예전엔 전용 VRAM 크기로만 갈라서, VGM·BIOS UMA 로 내장 GPU 의 `DedicatedVideoMemory` 를 외장 이상으로 잡으면 안내가 거꾸로 나오고 추론이 내장 GPU 로 옮겨 갔다
    (이슈 #3, 2026-10-31 수정). 점검은 `experiments/TrackerCheck gpu` — 이 PC 의 실제 어댑터에서 내장 여부를 못 읽으면 실패하고(vtable 순번·IID 를 잘못 고쳐도 이 PC 에선
    VRAM 기준이 같은 답을 내 결과로는 안 드러난다), VGM·동률·외장 둘·못 읽음 등 합성 사례를 열거 순서 양쪽으로 본다.
    DXCore 를 고른 이유(이 PC 콘솔 실측, 후보 셋 다 AMD 내장 · NVIDIA 외장): 판정 전체가 옛 코드보다 첫 호출 약 8 ms·이후 약 2 ms 더 들고 GPU 드라이버 DLL 을 안 올린다.
    D3D11 `UnifiedMemoryArchitecture` 는 어댑터마다 장치를 만들어야 해 NVIDIA 280~480 ms, D3DKMT 의 HybridIntegrated/HybridDiscrete 는 하이브리드 짝 표시라
    데스크톱 내장+외장에선 꺼질 수 있다(추정). 확인 안 함: VGM 을 실제로 키운 PC.
    `IDXGIFactory6.EnumAdapterByGpuPreference` 로 가르면 안 된다 — 설정 없는 프로세스에선 맞았지만 '절전' 프로세스에선 판별이 틀려 원인 미상으로 빠졌다(실측)
  - 상태바는 최소 창 폭(900)에서도 다 보이는 길이로 유지할 것 (넘치면 `TextTrimming` 으로 "…" 처리).
    **FPS·검출 칸은 폭을 고정한다**(2026-11-02 사용자 요청 — 'FPS 나 검출 숫자가 변할 때마다 다른 글자들도 이동'): 오른쪽 칸들은 오른쪽 끝에 붙은 StackPanel 이라 한 칸이 넓어지면
    그 왼쪽 칸(해상도·NPU 진행)이 모두 밀렸다. 이제 두 칸에 `MinWidth`(App.xaml `Status…Width`)를 주고, 숫자는 `Typography.NumeralAlignment="Tabular"`
    (이 글꼴은 12px Display 에서 '1' 이 '8' 보다 1px 좁아 자릿수가 같아도 FPS 글자 폭이 흔들렸다 — '검출 N' 처럼 한글 뒤 숫자는 맑은 고딕이라 안 듣지만 칸이 고정이라 상관없다).
    FPS 칸(오른쪽 맞춤)은 **모드에 따라 폭이 다르다**(`ApplyModeToUi`): 파일·카메라 50(숫자 '999.9 FPS' 49px), HUD 114(그 자리에 숫자와 번갈아 뜨는 '대기 (화면 변화 없음)'·'캡처 끊김 (재연결 중)' 등 113px).
    처음엔 113 하나로 고정했는데 파일 재생 중 해상도와 FPS 사이가 비어 사용자가 줄여 달라고 했다 — 숫자 폭 하나로만 두면 HUD 에서 대기 문구가 뜰 때마다 밀린다.
    검출 칸은 46('검출 999' 45px). 폭은 잰 값 + 화면 배율 반올림 여유 1px. 그보다 넓은 글자('검출 N (SAHI)' 76px)가 올 때만 칸이 넓어진다.
    UIA 로 4초 동안 0.2초마다 잰 칸 왼쪽 끝: 고치기 전 FPS 3~4가지·해상도 3~5가지·검출 3가지 → 고친 뒤 모두 1가지 (파일 재생·HUD 둘 다).
    최소 폭에서 상태 문구 칸은 HUD **575px**·파일 재생 645px(UIA 실측, HUD 는 1920×1080 — FPS 칸이 HUD 에선 늘 가장 넓을 때와 같다) — 가장 긴 HUD 문구
    `… · 캡처 GDI (느림 — 그래픽 설정에서 이 앱을 '고성능'으로 지정 후 재시작)`(568px)까지 들어간다. 11-01 개편 뒤엔 585px(FPS 칸이 가장 넓고 검출 두 자리일 때 — 검출 칸 고정으로 7px,
    해상도 숫자 Tabular·FPS 여유로 3px 줄었다), 개편 전엔 칸이 568px 라 이 문구만 598px 로 말줄임됐다.
    상태바 숫자 칸을 고정폭 글꼴로 바꾸면 16px 좁아져(552px) 다시 잘린다 — 그래서 숫자도 UI 글꼴이고 같은 폭은 Tabular 로 맞춘다
    테스트(`Test-ScreenHud.ps1`)는 GDI 를 [INFO] 로 보고하고, 안내가 있으면 툴팁(exe 경로·연결 GPU)과 **권한 설정이 레지스트리의 지금 설정과 앞뒤가 맞는지**(같은 설정을 다시 권하거나 없는 지정을 풀라고 하지 않는지) 확인한다
- DXGI 는 **화면이 바뀔 때만** 프레임을 준다 → `TryCapture` 가 `null` 이면 추론을 건너뛰고 이전 박스를 유지. `LastPresentTime == 0` 은 마우스만 움직인 것
- 반환 `Mat` 은 매핑된 스테이징 텍스처를 감싼 것이라 **다음 `TryCapture` 호출 전까지만 유효**. `ReleaseFrame` 은 복사 직후 바로 한다
- 일시정지 → 재개 시 `_hudForceRefresh` 로 마지막 화면을 다시 검출한다 (정지 화면이면 새 프레임이 오지 않아 지운 박스가 안 돌아옴).
  `TryCapture(forceLatest)` 는 새 화면을 기다리지 않고 매핑해 둔 마지막 화면을 곧바로 다시 준다(`Redelivered` = true). 예전엔 먼저 100 ms 동안 새 화면을 기다려
  그사이 바뀐 화면(재개 버튼 글자 등)을 돌려줘서 강제 경로가 깨져도 드러나지 않았다(이슈 #11). 마지막 화면이 아직 없으면(갓 만든 복제) 보통처럼 기다리고, GDI 는 매번 새로 캡처한다
- 회전 디스플레이·원격 데스크톱 등 DXGI 불가 환경은 `ScreenCapture.Create` 가 GDI 로 대체. 상태바에 `· 캡처 DXGI/GDI` 표시
- **캡처 복구 (2026-10-31, 이슈 #1)** — 예전엔 실행 중 복제를 잃으면 처음 만든 장치로 250 ms 마다 다시 복제만 하고 실패를 삼켜서, 화면 회전·장치 제거(TDR·드라이버 업데이트) 뒤
  `대기 (화면 변화 없음)` 에 머물렀고 추론 예외도 화면에 드러나지 않았다. 지금은 `DxgiScreenCapture.OnFailure` 가 실패를 가른다:
  - 같은 장치로는 못 살린다 → 곧바로 `ScreenCaptureLostException`: 장치 제거(`GetDeviceRemovedReason` 실패, DEVICE_REMOVED/HUNG/RESET·DRIVER_INTERNAL_ERROR·D3DDDIERR_DEVICEREMOVED), 회전(`DisplayRotatedException`)
  - 기다리면 된다 → 던지지 않고 같은 장치로 0.25초마다 다시 복제: ACCESS_LOST·E_ACCESSDENIED(잠금 화면·UAC 보안 데스크톱)·NOT_CURRENTLY_AVAILABLE·MODE_CHANGE_IN_PROGRESS·SESSION_DISCONNECTED.
    **던지지 말 것** — 잠긴 동안 장치째 새로 만들어도 DXGI 를 못 열어 장치 생성(0.2초)만 버리고, 같은 장치로 다시 복제하면 풀리자마자 이어진다
  - UNSUPPORTED·모르는 실패: 화면 모드·데스크톱 전환 중에도 잠깐 나서(DuplicateOutput 문서가 '예상되는 오류'로 다룬다) 프레임 없이 3초(`FailureGraceMs`) 넘게 이어질 때만 던진다
  - 던지면 `ScreenLoop` 이 같은 루프 스레드에서 해제하고 모니터를 장치 이름으로 다시 읽어 새로 만든다. **DXGI 를 쓴 적 있는 HUD 는 `DxgiScreenCapture` 를 바로 연다**
    (`ScreenCapture.Create` 는 DXGI 가 안 되면 곧바로 GDI 라, 잠긴 동안 다시 만들면 GDI 로 떨어졌다). 생성 실패는 `ClassifyCreateFailure` 로 가른다 —
    기다리면 될 것(Wait)은 캡처 없이 0.5·1·2초 뒤 2초마다 다시 열고, 잠깐일 수도 있는 것(Grace: UNSUPPORTED·장치 생성 실패·출력 못 찾음)은 3초까지 그렇게 기다리며,
    회전(Fallback)이나 Grace 초과면 사유가 붙은 GDI(`ScreenCapture.Fallback`). 처음부터 GDI 였던 HUD 는 `ScreenCapture.Create` 로 만든다(GDI 까지 실패하면 0.5·1·2·4초 뒤 5초마다)
  - GDI 로 떨어져도 DXGI 를 쓴 적 있으면 3초마다 DXGI 를 다시 열어 본다(TDR 끝, 0°↔180° 회전 되돌림). `D3D11CreateDevice` 가 한 번에 0.18~0.24초라 그동안 루프가 멈춰서
    장치까지 만든 시도마다 간격을 두 배로(30초까지). 처음부터 GDI 면(환경 탓) 다시 시도하지 않는다
  - 루프는 1초마다 `GetMonitors`(약 10 µs)로 대상 모니터를 장치 이름으로 확인한다. 영역(X·Y·W·H)이 바뀌면 오버레이·무시 영역·상태바를 새 좌표로 맞추고(예전엔 해상도를 바꾸면
    오버레이가 옛 크기로 남았다), 캡처는 GDI 만 새로 만든다 — DXGI 는 모드가 바뀌면 다시 복제하며 해상도를 따라가고 회전은 상실로 온다. 모니터가 없어지면 `모니터 연결 끊김` 을 띄우고 돌아오면 다시 만든다
  - **갓 만든 DXGI 복제의 첫 프레임은 검은 화면이다** (`LastPresentTime` 0·`AccumulatedFrames` 0·모든 픽셀 0 — 10-31 8회 실측) → 건너뛴다. 그래서 새 DXGI 캡처는 화면이 바뀌어야
    첫 화면을 준다. 기다리면 될 끊김 동안엔 오버레이 박스를 지우지 않는다(지우면 풀린 뒤 화면이 그대로일 때 안 돌아온다)
  - **FPS 칸 문제 표시**: 캡처가 끊긴 채(`IScreenCapture.Interruption`)·캡처 예외·검출 예외가 1초 넘게 이어지면 `캡처 끊김 (재연결 중)`·`캡처 오류 (재시도 중)`·`검출 오류 (재시도 중)`,
    캡처 자체가 없으면 곧바로 `모니터 연결 끊김`·`캡처 끊김 (재연결 중)`. 자세한 사유는 툴팁. 문구 폭은 최소 창 폭에서 HUD 상태를 가리지 않게 '대기 (화면 변화 없음)'(113px — 11-01 개편 뒤 상태바 글꼴, 옛 맑은 고딕은 116px) 이하.
    끊김·모니터 없음은 경고 색, 캡처·검출 오류는 위험 색으로 띄운다(`PostHudProblem` — 걷으면 원래 색).
    오류·모니터 없음은 옛 박스를 지우고, 떠 있는 동안 `RenderHudLatest` 는 밀려 온 옛 결과를 그리지 않는다
  - 검출이 실패한 화면은 물러났다가(0.5·1·2초, 그 뒤 5초마다) 마지막 화면으로 다시 검출한다(정지 화면이면 새 화면이 안 와서). 재개·재검출의 강제 캡처는 캡처가 끊긴 동안 미룬다
  - 남은 것: 추론 GPU 의 장치가 제거되면(TDR) `검출 오류` 로 드러나지만 추론 세션(`_yolo`)은 다시 만들지 않는다 — 앱을 다시 시작해야 한다.
    DXGI 다시 열기를 기다리는 동안(드문 경우 — 장치 제거와 잠금 화면이 겹침 등)엔 2초마다 장치를 새로 만든다(0.2초씩)
- GDI 경로는 `BitBlt` 가 실패하면(잠금 화면·UAC 보안 데스크톱) **`timeoutMs` 만큼 쉬고** `null`(새 화면 없음)을 돌려준다 — 옛 버퍼를 새 화면처럼 넘기지 않게.
  쉬지 않고 곧바로 돌려주면 HUD 루프가 대기 없이 다시 불러 잠금 화면 내내 코어 하나를 헛돌았다(2026-10-30 코드 검토로 발견. 잠금 화면에서 `BitBlt` 가 실제로 실패하는지는 미측정).
  GDI 는 변경 영역을 모르므로 아래 '고리' 장치 중 무시 영역과 `대기` 표시가 듣지 않는다 — **정지 화면에서도 매 반복 캡처·추론한다**(GPU 가 쉬지 않음. `LooksSame` 은 오버레이 재그리기만 막는다).
  해제는 DC 에 원래 비트맵을 되돌린 뒤 지운다(문서 규약). 선택된 채 지워도 이 PC Windows 11 에선 누수가 없었다(50회 실측) — 규약 때문에 맞춘 것
- 캡처 루프는 **전용 스레드(LongRunning)에서 동기로** 돈다. `await` 로 스레드가 바뀌면 GDI 대체 경로의 DC(`GetDC`/`ReleaseDC` 는 같은 스레드여야 함)가 깨진다

### "스스로 새 화면을 만드는" 고리 — 정지 화면에서 GPU 가 쉬게 하는 장치들
DXGI 는 캡처에서 제외된 오버레이나 앱 자신의 UI 가 바뀌어도 "새 화면"을 준다. 방치하면 추론 → 다시 그리기 → 새 화면 → 추론이 초당 100회 이상 무한 반복한다(실측). 그래서:
1. `BoxLayer.Update` 는 **보이는 결과가 같으면 다시 그리지 않는다** (`LooksSame`: 0.5px·신뢰도 소수 둘째 자리 기준. 클래스별 개수 범례도 박스에서만 나와서 같이 걸러진다)
   — 개수·배율이 같으면 **i번째끼리** 비교하므로 같은 박스라도 순서가 바뀌면 다시 그린다(0.5 는 오버레이 DIP 단위). 고리가 멈추는 건 같은 입력에
   같은 순서를 내는 결정적 추론(DirectML) 덕분이라, 결과 순서를 흔드는 처리(병렬 병합·정렬)나 비결정적 EP 를 넣으면 다시 돈다(NPU 가 결정적인지는 확인 안 함)
2. 상태바(`StatusBarPanel`) 영역을 `_hudIgnoreRegion` 으로 캡처에 넘겨, **변경 사각형(dirty/move rects)이 모두 그 안이면 새 화면으로 치지 않는다** — 메인 창이 HUD 대상 모니터에 있을 때(단일 모니터면 항상) 필요
3. HUD 상태바 텍스트는 **초당 4회**로 제한, 1초 넘게 새 화면이 없으면 `대기 (화면 변화 없음)` 표시

### 지연 처리
캡처 루프는 UI를 **기다리지 않는다**. `_latestDets` 등에 최신 결과만 덮어쓰고
`Interlocked.Exchange(ref _uiUpdatePending, 1)` 로 중복 디스패치를 막는 latest-wins 방식이다.
UI 콜백은 **맨 앞에서** 깃발을 0 으로 내린 뒤 `_latestDets` 를 읽는다 — 예전엔 다 그린 뒤 `finally` 에서 내려서, 읽은 뒤~내리기 전에 온 결과가
다음 새 화면까지 묻혔다(화면이 그대로 멈추면 낡은 박스가 계속 남음, 2026-10-30). 같은 결과를 두 번 그려도 `LooksSame` 이 거른다.
HUD FPS = 1 / (화면 획득 → 검출 완료) — 새 화면을 기다린 시간은 빼고 잰다. 즉 **FPS 의 역수 ≈ 박스 지연**.

### 성능 실측치 (2026-09-11, RTX 5060 Laptop, YOLO26s)
| 항목 | 최적화 전 | 최적화 후 |
|---|---|---|
| HUD (앱 실측, 2560×1440) | 29 FPS | **~104 FPS** (DXGI) |
| HUD 1회: 캡처+검출 (벤치, 2560×1440 / 1920×1080) | 29.6 / 24.1 ms | **10.6 / 8.4 ms** |
| 화면 캡처 1회 (2560×1440 / 1920×1080) | GDI 21.3 / 14.7 ms | DXGI 복사+매핑 **2.15 / 0.75 ms** |
| 정지 화면에서 추론 | 계속 (~30회/s) | **0회/s** (대기) |
| 슬라이더 값 변경 1회당 UI 멈춤 (720p mp4) | 117 ms (최대 210) | **2~3 ms** |
| 추론 (DirectML, 1회) | ~11 ms | (동일) |

위 수치는 YOLO26s 기준이다. 파일 재생 처리 속도는 YOLO26s(`visible` 데이터셋 모델)로 ~98 FPS (720p, 2026-09-23).
YOLO26x 는 제대로 잰 적이 없다 (사용자 체감: s 로 바꾸니 빨라짐).
**UI 테스트가 보고하는 HUD FPS 는 벤치마크로 쓰지 말 것**: 정지에 가까운 화면에선 새 프레임이 몇 장 안 와서
워밍업이 섞인 평균이 수렴하지 않는다 (같은 실행에서 14.8 → 31.5 처럼 들쭉날쭉).

DXGI 와 GDI 캡처는 같은 영역에서 **픽셀이 완전히 동일**했고 검출 결과도 11/11 일치(IoU 1.000).

**측정으로 기각된 아이디어 — 다시 제안하지 말 것:**
- 추론 전 긴 변 640으로 선축소: 추론 10.82 → 10.51 ms(효과 없음). YoloDotNet 기본 리사이즈가 `Proportional` + `Nearest` 라 이미 싸다. 오히려 검출 결과가 달라진다(10개 중 9개만 일치, IoU 0.899)
- GDI `CAPTUREBLT` 플래그 제거: 효과 없음
- 캡처 버퍼 알파 채널 보정: 불필요. 알파=0 버퍼를 넣어도 검출 결과 동일 (`SKAlphaType.Opaque` 로 넘김)

남은 개선 후보(미구현): 모션 예측 외삽(비용 거의 0, 방향 전환 시 오버슈트). **YOLO 입력 축소를 Nearest → 쌍선형으로** — 속도가 아니라 정확도:
데이터셋 시험 분할에서 AP +0.02~0.03(배포 YOLO26s 0.653 → 0.672, 2026-11-05 `experiments/RfDetrBench` 2단계). Ultralytics 는 학습·검증 모두 쌍선형으로 줄인다.
YoloDotNet 4.2.0 `YoloOptions.SamplingOptions` 로 바꿀 수 있어 보인다(확인 안 함). NPU 짝의 양자화 보정(`tools/npu_quantize.py`)도 Nearest 기준이라 같이 바꾸고 짝을 다시 만든다.
위 '기각' 항목의 선축소와는 다른 얘기다 — 그건 속도를 보고 기각했고 결과가 달라진다는 것만 봤다.

## 도메인 지식 — SAHI (on-demand)

재생을 멈춘 상태에서 현재 프레임 1장에만 실행한다 (실시간 경로에는 넣지 않음 — 타일 수만큼 추론이 늘어남).
- 타일 640, `overlap = 0.3`
- 병합(2026-10-30 바꿈, 사용자 결정): **전체 프레임 검출 1회**(원본 SAHI 의 perform_standard_pred — 겹침 192px 보다 넓은 큰 물체는 어느 타일에도 온전히 안 들어온다)와
  타일 검출을 `Sahi.MergeCutPieces(all, 0.45, 0.6)` 로 병합 → 그 결과에서 `DuplicateBoxes.Remove`(같은 자리 car/truck 등, 3번 항목. 타일마다 먼저 빼지 말 것).
  묶음은 예전 `MergeNms` 와 같다(같은 라벨, 신뢰도 순 대표와 IoU 0.45 또는 IoS 0.6 — 대표하고만 비교). 묶이는 두 박스 중 하나라도 **타일 안쪽 경계에 닿은 박스**
  (`TouchesInnerEdge`, 2px — 물체가 잘렸을 수 있음)면 대표를 합집합으로 넓히고, 둘 다 온전하면 예전처럼 대표만 남긴다. 전체 프레임 박스는 잘리지 않은 것으로 넣는다
- **IoS(Intersection over Smaller)가 핵심.** 타일 경계에서 잘린 작은 박스가 온전한 큰 박스 안에 들어앉는 경우 IoU만으로는 안 걸러진다.
  예전(지우기만 하는 `MergeNms`)엔 어느 쪽을 남길지를 신뢰도로만 정해, 잘린 박스의 신뢰도가 더 높으면 온전한 박스가 지워졌다 — test5 44.75초 탱크로리
  (잘린 truck 0.83 이 온전한 0.67 을 지움). 지금은 둘을 합쳐 온전한 박스(113×200)가 된다
- 기각: 원본 SAHI 처럼 **모든** 묶음을 합집합으로(`MergeNmm`, GREEDYNMM). 타일·전체 프레임 검출이 같은 물체에 몇 px 씩 다른 박스를 내서 test4 박스 2,028개 중
  817개가 1~4px 부풀었고(넓이 1.05~1.4배), 신뢰도 낮은 큰 박스가 옆 차 둘을 한 박스로 묶었다(62장 중 약 15곳). 경계 조각만 합치면 같은 비교에서 바뀐 박스가 205개로 줄고
  (대부분 전체 프레임 검출이 더 높은 신뢰도로 같은 차를 대신한 것 — 예: 타일 car 0.71 → 전체 truck 0.95) 옆 차 묶임은 없었다
  (2026-10-30, `experiments/TrackerCheck sahi [Variant=nmm]` — 15프레임에 1장, 루트 모델)

## 도메인 지식 — 추적기 (유고 감지 1단계, 2026-10-24 · 앱에 연결 2026-11-02)

`Tracker.cs`: `Update(dets, timeMs)` 가 같은 순서로 `TrackedDet(TrackId, Det)` 를 돌려준다(`Stationary` 속성 — 그 트랙이 이 프레임까지 반영해 정지로 판정됐는지). `Tracks` 는 안 보이는 트랙까지 포함(대표 `Label`·`IsStationary`·`FirstMs/LastMs`·`Hits`).
`Reset()` 은 트랙만 비우고 **ID 는 이어서 센다**(로그가 ID 로 물체를 가리킴). 다음 단계(ROI → 진입 이벤트 → 유고 규칙)가 이걸 쓴다.
앱은 `UpdateTagged`(번호·정지 여부를 `Det.TrackId`·`Det.Stationary` 에 붙여 돌려줌)를 쓴다 — 연결 방식은 아래 '앱 연결'.
- **시각 기준**(프레임 수 아님) — YoloDotNet `SortTracker` 는 maxAge 가 프레임 수·칼만이 중심점만이라 안 쓴다
- 매칭: 탐욕적 IoU. 1차 최근 트랙(움직이면 등속 예측 위치) → 2차 오래 안 보인 **정지** 트랙을 같은 자리(IoU 0.5)로 재연결.
  차량 6종(car·truck·bus·motorcycle·scooter·bicycle)은 한 묶음 — 실측 라벨 바뀜 car↔truck 110회, bus↔truck·bicycle↔motorcycle·car↔motorcycle 도 있음
- **중복 박스를 한 물체로 묶는다** (작은 박스의 80% 이상이 큰 박스 안 + 넓이비 0.5 이상 → 같은 ID). 위 3번의 같은 클래스 중복 외에 범위만 다른 중복
  (짐칸·탑승자 포함/제외, IoU 0.5~0.7)도 잦다. 안 묶으면 멈춘 차 한 대가 "줄지어 선 두 대"가 되고, 1프레임 중복(bus)이 만든 트랙이 0.87초 뒤
  덤프트럭 검출을 IoU 0.98 vs 0.96 차이로 가로채 트랙을 끊었다. 넓이비 조건은 버스 박스 안에 들어가는 옆 차(넓이비 0.13~0.16)를 살리려고.
  판정은 3번의 `DuplicateBoxes` 그대로다(2026-10-25 공용으로 뺌, `track` 결과 바이트 단위로 같음). 앱 검출은 이미 걸러져 오므로 앱에 연결하면 이 묶음은
  일어나지 않는다 — 매칭은 원래 대표 박스(신뢰도 최고)만으로 하므로 남는 박스의 ID 는 걸러진 입력이든 아니든 같다
- 정지 판정: 1초 동안 이동 ≤ 0.2×√(W·H) (관측이 짧으면 비례, 최소 0.2초 — 화면 전환 직후 0.2초 보이고 2초 가려진 정지 차도 잇게).
  이동은 중심이 아니라 **마주 보는 두 변 중 덜 움직인 쪽**으로 잰다 — test5 화물차가 지나가는 트럭에 한쪽부터 가려져 폭 64→40 이 되자 중심 기준으론 "움직임"이 돼 끊겼다

| 파라미터 | 값 | 근거 (스윕: 결과가 거의 안 바뀜) |
|---|---|---|
| `IouThreshold` | 0.3 | 0.2 는 트랙 160→148(합칠 여지), 0.4 는 끊김 후보 9→12 |
| `MovingGapMs` | 1000 | 500 이면 트랙 160→172·끊김 늘어남 (구상 때 본 "0.5초면 2~3개로 끊김"과 같음) |
| `StationaryGapMs` | 10000 | 실측 최대 재연결 공백 3.23초, 3~20초 결과 동일. PTZ 한 뷰 체류 ~10초 |
| `RelinkIouThreshold` | 0.5 | test5 가림 뒤 재연결이 IoU 0.58 — 0.7 이면 실패 |
| `DuplicateIos` / `DuplicateMinAreaRatio` | 0.8 / 0.5 | IoU>0.7 쌍은 IoS>0.82 라 함께 잡힌다 |

**검증** (`experiments/TrackerCheck` — 모든 프레임 검출을 CSV 로 남기고 추적만 다시 돌린다. 자세한 건 그 README).
루트 `Detection.cs`·`Tracker.cs`·`Sahi.cs`·`WindowsMlExecutionProvider.cs`·`GpuAdapters.cs`·`Roi.cs`·`SceneChange.cs`·`SceneThumbnail.cs`·`ZoneEvents.cs`·`IncidentRules.cs`·`ViewIdentity.cs` 를 소스로 링크해 WPF 없이 빌드하므로 이 열하나에 WPF 의존을 넣으면 빌드가 깨진다.
앱 상수(신뢰도 0.3·IoU 0.7·`TargetClasses`·타일 640·겹침 0.3·병합 0.45/0.6)와 `DetectTile` 은 **복사본**이라(`SelectBestGpu` 는 `GpuAdapters.cs` 를 링크해 앱과 같은 판정) 앱 쪽을 바꾸면 같이 맞춰야
비교가 조용히 어긋나지 않는다. 검출 CSV 는 `DuplicateBoxes` **전** 결과다(앱의 '검출 N' 보다 많다):
- 정지 판정(`IsStationary`)도 보고한다 — `tracks.csv` 마지막 열 `stationary`, `render` 는 정지면 ID 뒤 `*`, 보고서의 '테두리 멈춤 판정' 절(관측마다 판정과 따로 잰 중심 이동을 맞댐)
- 기준 충족: test4 0~10.5초 덤프트럭 **트랙 1개**(315/315 관측), test5 흰색 화물차 0~47초 **트랙 1개**(관측 97%, 최대 공백 1.25초, 검은 끊김 포함).
  프레임을 2·3·5장에 하나만 넣어도(6~30fps) 둘 다 1개
- 잘못 합침·ID 뒤바뀜: 의심 연결(재연결 10·튐/크기 급변 28)을 눈으로 확인한 범위에서 **0건** (튐은 스쿠터 탑승자·오토바이 같은 빠른 소형,
  크기 급변은 중복 범위 차이·화면 끝 진입/이탈·기둥 가림). **확인 불가**: 소실점 근처 12~15px 원거리 차(test4 뷰 0 위쪽 — 박스 하나가 두 대를 덮기도 함)
- 끊김(같은 물체가 새 ID): test4 9건 — 원거리 소형 차, 오검출(볼라드를 person 으로, 박스가 떨려 정지 판정 안 됨). test5 0건
- `Reset()` 없이 돌리면 test4 에서 트랙 56개가 화면 전환을 넘어 이어진다(돌아가는 카메라가 박스를 끌고 감) → 전환 감지 후 `Reset()` 필수 (2026-11-03 앱에 연결 — 아래 "화면 전환 감지")

**앱 연결 (2026-11-02)** — 사용자가 화면 표시를 시안 셋(라벨에 ID + 정지 · ID 만 · 화면 변화 없음) 중 'ID + 정지' 로 골랐다: 박스 라벨이 `CAR #12 0.95`,
1초 넘게 멈춘 물체는 `CAR #12 정지 0.95`. 파일·카메라·화면 HUD 를 추적하고, SAHI 결과는 한 장짜리 분석이라 추적하지 않는다(라벨에 번호 없음).
- 번호는 `Det.TrackId`·`Det.Stationary`(init 속성)로 박스와 **한 목록**에 실려 간다 — HUD 의 `_latestDets` 처럼 한 참조로 넘기는 값이라 번호와 박스가 어긋날 틈이 없고,
  `BoxLayer.LooksSame` 이 둘도 견줘서 번호·정지가 바뀌면 다시 그린다. 위치 인자가 아니라 init 속성인 이유는 `Det` 를 6개 인자로 만들고 풀어 쓰는 코드(검출기·SAHI·실험 도구)를 그대로 두려고.
  추적하지 않은 검출(SAHI 결과 등)은 0 / false 로 남아 라벨에 안 나온다
- 파일·카메라(`PlaybackLoop`): **화면에 내보낼 프레임에만** 돌린다 — 일시정지 중 옮긴 위치의 옛 프레임처럼 버려지는 프레임을 넣으면 시각이 어긋난다.
  시각은 파일이 영상 시각(`posMs`, VFR 도 실제 타임스탬프), 카메라가 루프의 벽시계. 소스를 열 때마다 루프가 새로 시작하고 `Tracker` 도 새로 만든다(번호가 1부터).
  시크를 처리한 프레임(`seeked`)은 먼저 `Reset()` 한다 — 뒤로 가는 시각은 추적기가 저절로 비우지만 앞으로 가는 시크는 안 비우고, 정지 트랙은 10초 남아서
  시크 앞뒤의 같은 자리 차가 같은 번호를 받는다. 그 깃발(`resetTracker`)은 루프 수준에 둬서 시크 프레임의 처리가 실패해도(`ProcessFrame` 예외) 다음 프레임에서 비운다.
  카메라는 멈췄다 재개하면 비운다(벽시계가 건너뛴다). 추적기가 예외를 내도 재생은 이어간다(번호 없는 박스)
- 화면 HUD(`ScreenLoop`): 검출 뒤, FPS(박스 지연) 계산 뒤에 돌린다. **시계는 가상 시계** — 새 화면 사이를 `HudTrackStepCapMs`(250 ms)까지만 흐른 것으로 친다.
  실제 시간을 그대로 넘기면 DXGI 는 화면이 안 바뀌면 프레임을 안 줘서 정지 화면이 1초(움직이는 트랙)·10초(멈춘 트랙) 넘게 이어질 때 물체가 그대로인데도 트랙이 만료돼 번호가 바뀐다.
  비우는 때: 캡처를 갈아 끼울 때(`Replace` — 회전·장치 제거·모니터 다시 연결)·모니터 배치가 바뀔 때(좌표계가 달라진다)·재개 때(`resumed` — 멈춘 사이 화면이 바뀌었을 수 있다 → 재개하면 번호가 새로 매겨진다)
- **HUD 고리는 새로 만들지 않는다**: 오버레이를 다시 그리면 DXGI 가 새 화면을 주고 → 재추론 → 다시 그리기가 도는 고리는 `LooksSame` 이 막는데, 번호·정지도 거기 들어간다.
  추적기 출력은 같은 입력이 계속 들어오면 ID 가 하나로 이어지고 정지로 한 번만 바뀐 채 수렴해서(`TrackerCheck selftest` ⑦ — 100fps 3초·5초 간격 모두) 다시 그리는 것도 그 한 번뿐이다.
  대가: 정지 화면에서 프레임이 안 오면 가상 시계도 안 가서 멈춘 물체가 '정지'로 바뀌지 못한다 — 번호는 붙지만 '정지'는 화면이 계속 오는 영상(CCTV 스트림은 노이즈로 늘 프레임이 온다)에서만 나온다
- 테스트가 읽는 법: 번호는 라벨 글자(그림)라 UIA 로 안 보여서 `BoxLayer.TrackText`(`#12 정지 · #15 · #21`, 박스 순서)를 **검출 수 칸의 UIA `ItemType`** 으로 내보낸다
  (`ShowTrackMarks` — 범례의 `ItemStatus` 와 같은 길: 앱 안은 `FrameBoxes`, HUD 는 오버레이가 알리고 `CloseOverlay` 가 걷는다). `Common.ps1` 의 `Get-TrackMarks`·`Test-TrackMarksMatch` 가 읽는다

연결할 때 주의(위에서 지켰다): 파일은 `posMs` 를 넘기고 시크 뒤 `Reset()` (뒤로 가는 시각만 자동 Reset). 프레임 크기도 함께 넘긴다(`Update(dets, posMs, frame.Width, frame.Height)`,
HUD 는 캡처 크기 — 크기 없는 판은 아래 '화면 가장자리' 문제가 그대로다). **HUD 는 화면이 바뀔 때만 프레임이 와서**
정지 화면이 1초·10초 넘게 이어지면 그 사이 Update 가 없어 트랙이 지워진다 — 새 프레임이 없을 때도 마지막 검출로 Update 하거나 그 시간을 공백으로 치지 말 것.
락이 없으니 루프 스레드 하나에서만 부른다. 출력에 중복 박스가 같은 ID 로 남으므로 개수는 ID 로 센다.
**화면 가장자리** (2026-10-31 고침, 이슈 #10): 들어오거나 나가는 물체는 보이는 부분까지만 박스가 잡혀(박스는 테두리 −2~+25px 에서 끝난다) 테두리 쪽 변이
테두리에 머물러, '덜 움직인 변' 규칙상 움직여도 정지가 됐다 — test4 실측 테두리 2px 안 박스 863개 중 움직이는데 정지 116개(트랙 17, 모두 오른쪽 테두리로 들어오는 차),
정지인 채 사라져 남은 트랙 1. `noreset` 에선 그런 유령 트랙이 7.03초 뒤 다른 차에 ID 를 넘겼다. 그래서 **앱에 연결할 땐 `Update(dets, ms, 프레임 폭, 높이)` 를 쓴다** —
테두리 `BorderMargin`(2px, SAHI 경계 여유와 같음) 안의 변을 지금·약 1초 전 기준 박스 어느 쪽에서든 빼고 그 축은 반대쪽 변으로만 잰다
(위 수치 모두 0, 아래 테두리에 잘린 채 서 있는 차는 정지 그대로, 트랙 ID 는 test4 Step 1·2·5 에서 예전과 바이트 단위로 같고 Step 3 에선 끊기던 트럭 하나가 이어짐).
한 축의 두 변이 다 닿으면 그 축은 잴 수 없어 판정을 그대로 두고, 잘린 변으로 잰 값(실제보다 작은 아래 한계)만으로 허용치를 넘을 때만 정지를 푼다.
크기 없는 `Update(dets, ms)` 는 예전 판정 그대로다(옛 코드와 프레임마다 추적기 상태가 바이트 단위로 같음). 여유 1·2·4·8px 는 결과가 같고 0px 이면 1~2px 안에서 끝나는 박스가 빠진다.
남은 한계: 한 축을 가로지른 채 처음부터 멈춘 박스는 정지로 판정되지 않고, 테두리에 걸쳐 멈춘 차가 안쪽부터 가려지면 가려진 동안 움직임으로 본다(박스만으로는 나가는 차와 같음).
고정 가림(기둥)·레터박스 경계는 추적기가 모른다 — ROI 단계에서 다룰 것(구역 편집은 11-03 에 생겼고, 무시 구역을 규칙이 쓰는 건 다음 단계). 확인: `TrackerCheck track`(보고서의 테두리 멈춤 절, `noframe` 이면 예전 판정)·`TrackerCheck selftest`(합성 장면, 종료 코드 = 실패 수)

## 도메인 지식 — 관심 구역 (ROI, 유고 감지 2단계 — 2026-11-03)

영상 위에 구역(다각형)을 그리고 유형을 붙인다. 지금은 **편집·표시·저장까지**이고, 구역을 쓰는 규칙(진입 이벤트·유고)은 다음 단계다. 화면 배치는 사용자가 시안 셋(편집 모드 + 영상 위 도구 막대 ·
**왼쪽 패널 목록** · 별도 편집 창, 시안 캔버스 'ROI 편집' 줄) 중 패널 목록을 골랐다.
- **데이터**(`Roi.cs`): 구역 = 번호 `Z1`… + 유형 + 점 3개 이상. 유형 넷 — 차로(`lane`)·갓길(`shoulder`)·정지선·대기(`stopline`)·무시(`ignore`, 규칙에서 빼는 자리 — 방향 안내 글자·기둥·레터박스). 점은 **프레임 크기를 1 로 본 0~1 좌표**라 해상도·창 크기와 무관하다
  (나중에 같은 뷰의 영상이 해상도가 달라도 그대로 쓴다). 번호는 **지운 것을 다시 쓰지 않는다**(나중에 이벤트 로그가 번호로 구역을 가리킨다 — 파일의 `nextId` 에도 적혀 다시 열어도 이어지고,
  구역이 하나도 없어 파일을 지우면 번호도 처음부터). 겹친 자리를 고를 땐 가장 작은 구역(차로 안의 정지선·대기 구역도 고를 수 있게).
- **파일**: 영상 옆 `<영상>.roi.json`(판 2 — `{"version":2,"nextId":N,"nextView":M,"views":[{"id","ref":"<기준 화면 JPEG base64>"}],"zones":[{"id","view","kind","points":[[x,y],…]}]}`. 판 1(뷰 식별 전)도 읽는다 — 구역의 뷰가 정해지지 않은(0) 채로, 아래 "화면 식별". UTF-8 BOM 없음, 임시 파일에 쓴 뒤 바꿔 끊겨도 옛 파일이 상하지 않는다). **바뀔 때마다 바로 저장**한다
  (점 끌기는 마우스를 놓을 때, 새 구역·지우기·유형 바꿈은 즉시). 구역이 하나도 없으면 파일을 지운다(빈 파일을 영상 옆에 남기지 않음). 영상을 열 때 읽고 소스를 내리면 비운다.
  읽지 못하면(깨짐·버전 모름·점 3개 미만·번호 겹침 등 `FormatException`) 상태바에 사유를 띄우고 빈 구역으로 시작하며 옛 파일을 `.bad` 로 복사해 둔다 — 이어서 편집하면 저장이 그 파일을 덮어쓰기 때문.
  읽기 전용 폴더 등 저장 실패는 상태바 '구역 저장 실패' (화면의 구역은 그대로). 0~1 밖 좌표는 안으로 맞춰 읽는다. `.gitignore` 에 `*.roi.json.bad`·`*.roi.json.tmp`
- **편집은 파일 재생을 멈춘 채일 때만**(`UpdateRoiUi`): `_mode == File && !_isPlaying && !_sahiBusy` — 재생 중엔 프레임이 계속 바뀌고 SAHI 중엔 화면이 정해져 있다. 켜고 끄는 곳은 `ApplyModeToUi`·`Btn_PlayPause_Click`·`StartPlayback`·`OnVideoEnded`
  (재생 상태가 바뀌는 곳마다 부른다). 막힌 동안 [추가]·목록·유형 콤보는 꺼지고 사유가 툴팁(UIA HelpText)에 있다(`ShowOnDisabled`). 켜졌다 꺼질 때 그리던 점·끌기는 버린다.
  패널은 '구역' 절: 헤더 `구역  N개`(툴팁에 구역 파일 경로) · 목록(`ZoneList`, 항목 UIA 이름 `Z1 차로`) · [추가]·[지우기] · '선택한 구역 유형' 콤보(`ZoneKindSelector`). [추가]가 만드는 구역의 유형은 마지막으로 고르거나 선택한 유형
- **마우스·키보드**(`RoiLayer`): [추가] → 클릭으로 점 → 첫 점을 누르거나 Enter 로 닫기(점 3개 이상·넓이가 거의 0 이 아닐 때만), 우클릭으로 마지막 점 되돌리기, Esc 로 그만두기. 구역을 누르면 선택, 선택한 구역의 점을 끌면 이동,
  변 가운데 ○ 를 끌면 그 자리에 점이 생겨 바로 끌린다(28 DIP 보다 짧은 변엔 ○ 없음), 점을 고른 뒤 Delete 로 점 지우기(3개 밑으론 못 지움 — 점을 안 고르고 Delete 는 아무 일도 없다, 구역은 [지우기]로만).
  손잡이·반경은 DIP 단위(점 9 · ○ 8 · 닫기 10). 프레임 밖(레터박스)을 누르면 아무 구역도 안 고른다.
- **그리기**: `MainWindow.xaml` 의 영상 칸에서 Image 와 BoxLayer **사이**(박스·라벨이 구역 위에 보인다). 편집 중에만 마우스를 받고(`IsHitTestVisible`) 나머지엔 통과시킨다. 구역 칠은 불투명도 16%·테두리 2px(고른 구역 3px),
  무시 구역은 점선. 색·불투명도는 App.xaml `Roi.*`(차로 = 강조색, 갓길 = 호박, 정지선·대기 = 보라, 무시 = 회청). 프레임 크기는 영상을 열 때 `VideoCapture` 의 `FrameWidth/Height` — 못 읽으면(0) 구역이 안 보이고 편집도 막힌다.
  구역은 정적 그림이라 BoxLayer 와 달리 프레임마다 다시 그리지 않고, 편집이 바뀔 때만 다시 그린다 → HUD 의 '새 화면 고리'를 만들 일이 없다. **HUD·카메라에는 아직 연결하지 않았다**
  (구역은 영상 파일의 것 — 카메라 뷰마다 두려면 화면 전환 감지로 뷰를 식별하는 일이 먼저다)
- **UIA**: `RoiLayer` 에 자동화 피어를 달았다(`ZoneLayer`, 이름 '구역 편집 영역') — 그리는 요소엔 기본 피어가 없고 같은 칸의 `Image` 는 UIA 영역이 비어 있어서, 테스트가 이 영역으로 화면 좌표를 프레임 좌표로 옮긴다
- **확인**: `TrackerCheck selftest` ⑧(안·밖·넓이·무게중심·점 집기·번호·JSON 왕복·잘못된 파일 14가지·파일 입출력, 영상·GPU 없이) + `tests/ui/Test-Roi.ps1`(실제 마우스로 그리고 끌고 지우고 파일의 좌표까지 확인 — 아래 '테스트').
  남은 것: 구역을 통째로 끌어 옮기기(지금은 점 하나씩), 구역 겹침 경고, 구역을 쓰는 규칙(진입 이벤트는 11-03 에 생겼다 — 다음은 유고 규칙), HUD·카메라 연결

## 도메인 지식 — 화면 전환 감지 (유고 감지 2단계의 하나, 2026-11-03)

설치 CCTV 는 방향이 바뀐다(PTZ 순회·사람 조작 — test4 는 10.5~11.4초·20.5~21.2초). 카메라가 도는 동안 박스가 끌려가 트랙이 화면 전환을 넘어 이어지고, 전환 뒤 같은 자리에 나타난 다른 차가 멈춘 트랙(10초 유지)의 번호를 물려받는다.
구역(ROI)도 뷰마다 달라야 한다. **2a — 전환을 감지해 추적기를 비우고 화면에 알리는 것**(여기)에 이어, 안정된 화면이 어느 뷰인지 알아내는 **2b 를 같은 날 더했다**(아래 "화면 식별" — 배지 글자·구역 이벤트의 이어짐이 그쪽으로 바뀌었다).
- **알고리즘**(`SceneChange.cs`): 프레임을 64×36 흑백(`SceneThumbnail`)으로 줄여 연속한 두 프레임의 **평균 절대차 d**(0~255)를 본다. d ≥ 12 = 움직임, d < 6 = 잠잠, 움직임 뒤 잠잠이 3장 이상·150 ms 이상 이어지면 안정.
  **전환 = 안정된 화면이 직전 안정 화면(기준)과 평균 절대차 15 이상.** '움직임이 있었다'가 아니라 '안정된 뒤 화면이 달라졌나'로 가른다 — 지나가는 큰 차·검은 끊김은 한두 프레임 크게 튀어도 안정 뒤 같은 화면으로 돌아오고,
  움직임 없이 한 프레임에 장면이 통째로 바뀌는 컷도 같은 규칙으로 잡힌다. 기준은 움직임 없는 프레임마다 현재 화면을 따라가므로 해 뜨는 것·자동 노출 같은 느린 변화는 전환이 아니다.
  빈(검은) 프레임(모든 픽셀 ≤ 24 = 스트림 끊김)은 기준·변화량 계산에서 빼고 안정될 때까지 움직이는 중으로 본다(`Blanked`). 5초 넘게 안정이 안 되면(계속 흔들림·검은 화면) 포기하고 전환으로 쳐 추적기를 비운다.
- **실측**(2026-11-03, `TrackerCheck scene` — 영상별 d 와 감지기 판정): test1~3·합성 webm 둘은 d 최대 0.6~4.9(움직임 없음). **test4**: d ≥ 12 가 10.53~11.30초 24장·20.53~21.13초 19장(최대 56.5·32.3)으로 알려진 회전 구간과 맞고,
  13.43·13.57·23.47초의 한 프레임 튐은 6.3~10.9 라 12 미만(무시) — 감지기는 두 구간을 안정 뒤(11.53·21.37초) 기준과의 거리 **58.6·51.8** 로 전환 처리. **test5**: 29.63·29.98초에 한 프레임씩 d ≈ 112(검은 끊김 21장, 밝기 0.0)인데
  감지기는 안정 뒤 기준과의 거리 **0.8** → 같은 화면(전환 아님). 전환(50 이상)과 비전환(1 미만) 사이가 넓어 15 는 여유가 크다. **못 잰 것**: 조금만 도는 카메라(작은 팬)의 거리.
- **앱 연결**(`PlaybackLoop`, **파일만**): 보이는 프레임마다 썸네일 → `SceneChangeDetector.Update(thumb, posMs)`. 움직이는 동안(`Unsettled`)은 **추적기에 넣지 않는다**(박스는 번호 없이 보임 — 돌아가는 카메라의 박스로 가짜 트랙을 만들지 않게).
  안정되고 전환이면 `tracker.Reset()`(번호는 이어서 센다). 시크·재개처럼 앞뒤가 이어지지 않는 곳에선 감지기도 같이 `Reset()`(추적기를 비우는 `resetTracker` 와 같은 자리). 프레임당 비용(썸네일 + 감지기, `TrackerCheck scene` 실측): 1280×720 약 0.8 ms, test5(1280×854·60fps) 2.0 ms — 화면의 FPS(처리 시간의 역수)는 이 앞에서 재서 영향이 없고 재생 시계의 여유만 그만큼 줄어든다.
  **카메라는 안 쓴다** — 웹캠은 가까이서 큰 동작만 해도 한 프레임의 d 가 12 를 넘어 번호가 깜빡일 수 있는데 이 PC 에선 실측할 수 없다(PTZ 소스로 d 분포를 잰 뒤 켠다). **HUD 도 아니다** — 바탕화면 전체라 창이 바뀔 때마다 화면이 달라진다.
- **배지**(`MainWindow.Scene.cs`, 사용자가 시안 셋(영상 위 배지 · 상태바 문구만 · 표시 없음) 중 배지를 골랐다): 영상 칸 **오른쪽 위**(왼쪽 위는 검출 범례)에 `● 화면 이동 중`(호박 — 움직이는 동안)·`● 화면 끊김`(호박 — 검은 끊김이 낀 구간)·
  `● 화면 전환 · 번호 새로 매김`(강조색 — 전환 직후 영상 시각 2초. 2b 이후 파일에선 어느 뷰인지 알린다: `● 뷰 V2 · 아는 화면`·`● 새 뷰 V3 · 구역 없음` — 위 글자는 식별기가 없을 때의 대체). 평소엔 숨김(UIA 에서도 사라진다, `Txt_SceneBadge` 이름 = 글자). 보이는 프레임의 상태라 일시정지하면 그대로 남는다.
- **검은 끊김은 전환이 아니다**: test5 의 끊김 앞뒤로 흰색 화물차가 같은 번호(`#2 정지`)로 이어진다(UI 테스트 확인) — 끊김 + 안정까지 약 0.5초 번호만 가려진다.
- **확인**: `TrackerCheck selftest` ⑨(합성 — 컷·회전·검은 끊김 뒤 같은/다른 화면·한 프레임 튐·느린 밝기 변화·계속 흔들림·검은 화면 지속·Reset·10/60fps·원인 표시, 그리고 전환 뒤 같은 자리의 다른 차가 비우지 않으면 옛 번호를 물려받는다는 것),
  `TrackerCheck scene`(실제 영상), `tests/ui/Test-SceneChange.ps1`(아래 '테스트'). **UI 테스트가 못 가르는 것**: 전환 때 `tracker.Reset()` 호출 자체 — 움직이는 동안 추적기에 안 넣어 옛 트랙이 대부분 만료되고 test4 의 회전 뒤 화면엔 옛 트럭 자리의 검출이 없어서
  그 줄을 뺀 변종 빌드도 통과했다(2026-11-03 확인). 비우기의 효과는 추적기 단위 selftest 가 본다.
- **남은 것**: 카메라(PTZ 소스로 d 분포 실측 뒤), 작은 팬 감지. (2b 뷰 식별은 11-03 에 생겼다 — 구역 규칙은 뷰를 알아본 뒤에만 돌고 모르는 동안은 꺼져 있다. 정지 시간·번호를 방문 사이에 이어 붙이는 건 사용자가 범위에서 뺐다)

## 도메인 지식 — 구역 진입·나감 이벤트 (유고 감지 3단계, 2026-11-03)

구역(ROI)에 물체가 들어오고 나갈 때마다 로그 한 줄 — 유고 규칙(정지 + 구역 유형 + 주변 흐름)이 쓸 재료다. 화면은 사용자가 시안(로그 위치 셋: 영상 아래 접이식 · 영상 위 최근 이벤트 · 왼쪽 패널 절 /
저장 방식 넷: 화면만 · 화면 + [CSV 저장] 버튼 · 자동 CSV · 자동 CSV + 스냅샷) 중 **영상 아래 접이식 로그 + 화면 + [CSV 저장] 버튼**을 골랐다.
- **감지**(`ZoneEvents.cs`, 순수): 추적기를 거친 박스(`TrackId > 0`)의 **발 위치**(박스 아래 가운데, 프레임 0~1 좌표)가 구역 안인지로 본다. 진입은 안에 **0.2초** 이상 이어서 있어야, 나감은 밖에 **0.5초** 이상 이어서 있어야 확정해
  경계에서 박스가 떨려도 이벤트가 반복되지 않는다. 이벤트 시각은 **실제로 넘어간 때**(확정은 그만큼 늦다 — 로그는 확정 순서라 시각이 조금 뒤섞일 수 있다). 무시 구역은 이벤트가 없고, 발이 무시 구역 안이면 다른 구역에서도 빠진다.
  겹친 구역(차로 안의 정지선·대기)은 각각 센다. 안에서 추적이 1.5초 넘게 끊기면 `나감 (추적 끝)`(`Lost`, 시각은 마지막으로 본 때) — 그보다 짧은 가림은 이벤트 없음. 오래 서 있는 물체도 이벤트는 한 번뿐.
- **이미 안에 있음**(`Initial`): 감지를 시작하거나 Reset 한 뒤 0.5초 안에 처음 본 물체가 이미 구역 안이면 '진입'이 아니라 이것으로 남긴다 — 시크·구역 편집·영상 열기 때마다 '진입'이 쏟아지지 않게(test4 의 멈춘 트럭 `#14` 가 이렇게 남는다).
- **화면 전환 뒤엔 어느 뷰인지 알아본 다음에 다시 시작한다**: 구역은 그린 뷰의 것이라 전환이 감지되면 이벤트를 멈추고 안에 있던 물체의 '나감'은 기록하지 않은 채, 안정된 화면을 식별한다(아래 "화면 식별").
  뷰를 알아보면(새 뷰든 아는 뷰든) 그 뷰의 구역으로 다시 시작하고(이미 안에 있는 물체는 `이미 안에 있음`) 로그에 `화면 전환 → 뷰 V1 · 구역 1개` / `새 뷰 V2 (구역 없음)` / `뷰 V2 (구역 없음)` 표시 한 줄(구역이 있는 영상만). 못 알아보면 예전처럼 `화면 전환 — 구역 이벤트 멈춤 (뷰를 알 수 없음)` 표시를 남기고 멈춘 채 1초마다 다시 본다.
  시크하면 그 위치의 화면을 다시 식별해 시작한다 (예전엔 '사용자가 지금 화면에 구역이 맞다고 보는 것'으로 믿었다).
- **앱 연결**(`PlaybackLoop`, 파일만): 추적기 뒤 `zoneEvents.Update(dets, _zoneShapes, posMs, w, h)` — 움직이는 동안(추적기에 안 넣는 프레임)은 부르지 않는다. 구역은 UI 스레드가 고치므로 루프는 **불변 스냅샷**(`RoiSet.Snapshot()` → `_zoneShapes`)을 읽고,
  구역을 읽거나 고치면 깃발(`_zoneEventsReset`)을 꺼내 감지기를 비운다(`PublishZoneShapes` — `LoadRoi`·`ClearRoi`·`OnRoiZonesChanged`). 시크는 `resetTracker` 와 같은 자리에서 비운다. 이벤트는 그 프레임과 같은 디스패치에서 로그에 더한다.
- **로그**(`MainWindow.Events.cs`): 영상 칸·재생 줄 아래 패널 — 헤더 `이벤트 N건`(N = 표시를 뺀 건수, 누르면 접고 펴기, 기본은 접힘) · `CSV 저장` · `비우기`, 펼치면 목록(112px, 최근이 위, 열: 시각 · `CAR #40`(클래스 색) · `Z1 차로`(구역 색) · 동작).
  항목 UIA 이름 `00:11.2 CAR #40 Z1 차로 진입`. 최대 2000건(넘으면 오래된 것부터 버림). 소스를 내리면 비운다 — 남기려면 [CSV 저장].
- **CSV**: `<영상>.events.csv`(있으면 덮어씀, UTF-8 BOM 없음, CRLF, 오래된 것부터) — 열 `ms,time,event,track,label,zone,zone_kind,dwell_ms,note`(note = 유고 해소 사유 `시크`·`화면 전환` 등, `view_changed` 행의 뷰 설명 `뷰 V1 · 구역 1개` — 2026-11-03 2b 에서 더한 열), event ∈ `enter` · `initial` · `exit` · `lost` · `view_changed`(표시 행은 track~zone_kind 가 빔), dwell_ms 는 나감·추적 끝 행만. 저장하면 상태바에 `이벤트 저장: …events.csv (N건)`. `.gitignore` 에 `*.events.csv`.
- **확인**: `TrackerCheck selftest` ⑩(십자 통과의 시각·머문 시간, 경계 흔들림, 나감 확정, 추적 끊김·가림, 무시 구역, 겹친 구역, 이미 안에 있음·Reset, 화면 전환 멈춤, 번호 없는 박스, 구역 삭제 — 10개) +
  `tests/ui/Test-ZoneEvents.ps1`(아래 '테스트'). 로그의 실제 모양은 스크린샷으로 봤다(접힘·펼침).
- **남은 것**: 스냅샷 저장(사용자가 '화면 + CSV 버튼'을 골라 안 함), 이벤트 필터(클래스·구역), 항목을 눌러 그 시각으로 시크, 카메라·HUD 연결

## 도메인 지식 — 유고 규칙 (유고 감지 4단계, 2026-11-03)

추적기의 '정지'와 구역 유형을 합쳐 **유고 후보**를 낸다. 범위는 사용자가 질문 셋(종류·시간·표시)에서 골랐다: **차로에 멈춘 차량만**(갓길 정차·차로 위 사람은 이번 규칙에 없다), **5초**, **로그 행 + 박스 강조**.
- **규칙**(`IncidentRules.cs`, 순수): 차량(car·truck·bus·motorcycle·scooter·bicycle)의 발 위치(프레임 0~1)가 **차로 구역** 안이고 `Det.Stationary` 인 상태가 5초(`HoldMs`) 이어지면 선언한다(`Incident` 이벤트 — 시각은 선언한 때, 멈춘 시간이 `DwellMs`).
  추적기의 '정지'는 1초 동안 거의 안 움직일 때(관측이 짧으면 0.2초부터)라 test4 의 트럭은 **5.2초**에 선언된다. 사람·갓길·차로 구역 밖은 후보가 아니다.
- **제외**: 발이 무시 구역 안 · 정지선·대기 구역 안(신호대기) · 같은 차로 구역에 (자신 말고) 정지한 차량이 3대 이상(`QueueOthers` — 줄지어 선 정체. 자신 말고 2대까지는 정체가 아니라 모두 후보).
  **이미 선언한 것은 정체 제외를 다시 보지 않는다** — 사고 차량 뒤로 차가 밀려 정체가 생기는 게 흔한 결과라, 그걸로 풀면 사고를 놓친다.
- **흔들림 흡수**: 정지 판정이 끊기거나 구역을 벗어나도 1초(`GraceMs`) 안에 돌아오면 이어진 것으로 본다(0.5초 끊김은 5.0초에 그대로 선언, 1.5초 끊기면 처음부터 다시 센다). 추적이 1.5초(`LostMs`) 끊기면 '추적 끝'으로 풀고 그보다 짧은 가림은 이어진다.
- **해소**: 정지가 아니거나 차로를 벗어난 채 1초 / 추적 끝 / **화면 전환(`Suspend`)·시크·구역 변경(`Reset`)** — 선언돼 있던 것은 끝내는 `IncidentEnd` 행(`유고 해소 (화면 전환|시크|구역 변경|추적 끝) · 멈춤 N.N초`)을 남겨 로그에 열린 채 안 남는다.
- **앱 연결**(`PlaybackLoop`, 파일만): 구역 이벤트 감지 바로 뒤 `incidents.Update(dets, _zoneShapes, …)`. 선언된 차의 `Det.Incident` 를 true 로 해 `BoxLayer` 가 클래스 색 대신 `Box.Incident`(빨강) 꺾쇠·라벨로 그리고(작아도 옅게·작게 하지 않는다 — 경고라서)
  라벨이 `TRUCK #14 유고? 0.96`, UIA 번호 글자는 `#14 정지 유고`(`Get-TrackMarks` 의 `Incidents`). 로그: 빨간 `⚠ TRUCK #14 Z1 차로 유고 후보 — 차로 정차 (높음)` 행, 해소 행은 초록 글자. 로그 헤더 오른쪽에 빨간 `⚠ 유고 후보 N`
  (`Txt_EventIncident` — 풀리지 않은 수, 로그를 접어 둬도 보이고 없으면 숨긴다). CSV event 열은 `incident`·`incident_end`(dwell_ms 포함). 움직이는 동안(추적기에 안 넣는 프레임)은 갱신하지 않는다.
- **한계**: 시크하면 그 위치에서 정지 판정부터 다시 세므로 선언이 그만큼 늦다(test4 를 2초로 시크하면 7.3초). 화면 전환이 있는 영상은 방문(한 뷰에 머무는 동안)마다 따로 센다 — 뷰로 돌아오면 5초를 처음부터 센다(test4 를 이은 합성 영상에선 A 로 돌아온 뒤 5.2초 만에 다시 선언, 아래 "화면 식별"). 방문 사이에 멈춘 시간을 이어 붙이지는 않는다.
  정체 안에서 난 사고(선언 전에 이미 정체였으면 제외됨)·갓길 정차·차로 위 사람은 못 잡는다. 임계값(5초·3대·1초)은 `IncidentOptions` 상수라 UI 에서 못 바꾼다.
- **확인**: `TrackerCheck selftest` ⑪(혼자 멈춘 차의 선언·해소 시각, 후보가 아닌 7가지, 정체 4대·3대·선언 뒤 정체, 정지 판정 끊김 0.5초·1.5초, 추적 끊김·가림, 차량 둘·Suspend·Reset, 구역 삭제 — 9개) + `tests/ui/Test-Incident.ps1`(아래 '테스트').
  스크린샷으로 봤다: 빨간 박스 `TRUCK #14 유고? 0.96`, 헤더 `⚠ 유고 후보 1`, 로그의 빨간 행.
- **남은 것**: 갓길 정차(심각도 낮음)·차로 위 사람 규칙(사용자가 이번엔 안 골랐다), 임계값을 UI 에서 바꾸기, 유고 후보를 LLM 으로 2차 확인(5단계), 로그 행을 눌러 그 시각으로 시크, 방문 사이에 멈춘 시간 이어 붙이기(뷰마다 추적기)

## 도메인 지식 — 화면 식별 (뷰 식별, 유고 감지 2b, 2026-11-03)

카메라가 돌아간 뒤 안정된 화면이 **어느 뷰(카메라 자세)** 인지 알아본다 — 같은 뷰로 돌아오면 그 뷰에 그려 둔 구역으로 구역 이벤트·유고 감지를 이어 가기 위해서다. 범위·표시는 사용자가 골랐다: **뷰별 구역 + 이벤트·유고 이어가기**(번호·멈춘 시간을 방문 사이에 이어 붙이는 건 뺐다), **영상 위 배지 + 구역 패널 머리**에 뷰 이름.
- **알고리즘**(`ViewIdentity.cs`): 프레임을 가로 480 흑백으로 줄여 ORB 특징점 800개 → 기준 화면과 BFMatcher(Hamming, Lowe 비율 0.8) → RANSAC 호모그래피(3px). 인라이어 수·인라이어가 놓인 4×3 격자 칸 수·화면 가운데 이동(가로 대비)·배율·원근 항으로 같은 자리인지 가린다.
  **같은 뷰** = 인라이어 ≥ 30 · 칸 ≥ 4 · 이동 ≤ 6% · 배율 0.9~1.12 · 원근 ≤ 1.0 (`ViewIdentityOptions`). 인라이어 15~29 는 '애매함'이라 새 뷰로 쪼개 등록하지 않고 알 수 없음(Unknown)으로 두며,
  새 화면의 특징점이 60개 미만(어둡거나 흐림)이어도 Unknown. 어느 뷰와도 안 맞으면 **새 뷰로 등록**한다(번호 V1, V2… — 지운·쓴 번호는 다시 쓰지 않는다. 뷰 수 한도 32).
  이동·배율 한계를 넘으면 인라이어가 많아도 같은 자리가 아니라 다른 뷰로 본다 — **구역은 어긋남을 보정하지 않는다**(호모그래피로 구역을 옮기는 건 안 했다).
- **실측**(2026-11-03, `TrackerCheck views` — test4 의 세 화면 A 0~10.5초·B 11.5~20.4초·C 21.3~30.5초와 test1~3·5): 같은 뷰끼리(다른 장면) 인라이어 **300~540**(칸 7~12), 다른 뷰·다른 영상끼리 **최대 8**(칸 2~5 — 호모그래피는 쓰레기로 이동 21~419%·원근 3.7~14).
  흔들림(3~20px 옮김·1.03/1.08배 확대·0.7° 회전·밝기 +25/×0.75·노이즈 σ8·JPEG 40)을 준 같은 화면은 365~800. 사이가 넓어 30 은 여유가 크다. 화면 고정 글자(방향 안내·시각)는 합성 화면(모든 화면에 같은 글자)에서도 뷰를 합치지 않았다.
  1280×720 프레임 한 장을 식별하는 데 흑백·축소 0.9 ms + 특징점 3.3 ms + 기준 하나와 맞추기 2.2 ms(기준 수만큼 곱) — 영상을 연 직후·시크 뒤·화면 전환 뒤에만 부른다.
- **기준 화면**은 흑백 JPEG(품질 75, 가로 480, 36~40 KB)로 두고 서명도 그 JPEG 를 읽어 만든다 — 구역 파일에 저장했다 다시 열어도 같은 서명이라 같은 결과다(테스트로 확인). 기준은 처음 본 화면 그대로고 갱신하지 않는다.
- **구역 파일 판 2**: `views`(번호 + 기준 화면 base64)·`nextView`·구역마다 `view`. **저장은 구역을 편집할 때만** — 그때까지 이 영상에서 알아본 뷰를 모두 적는다(구역 없는 뷰 포함, 번호가 이어지게). 영상을 보기만 해서는 파일을 만들지 않는다.
  구역이 하나도 없으면 파일을 지운다(뷰 목록도 같이 사라진다). 판 1 파일(뷰 식별 전)은 구역의 뷰가 0 으로 읽히고, 영상을 연 뒤 **처음 알아본 뷰의 구역**이 된다(`RoiSet.AdoptUnassigned` — UI 가 첫 뷰를 받을 때, 파일엔 다음 편집 때).
  읽을 때 views·view 가 맞지 않으면(없는 뷰·겹친 번호·base64 아님·기준 화면 없음·번호 0 등) `FormatException` — 구역 읽기 실패와 같게 상태바 사유·`.bad` 복사.
- **앱 연결**(`PlaybackLoop`, 파일만): `needIdentify` 가 서는 때 — 영상을 열 때·시크 뒤·화면 전환이 안정된 뒤 — 첫 안정 프레임에서 `ViewRegistry.Identify`. 전환은 먼저 `zoneEvents.Suspend`·`incidents.Suspend`(선언된 유고 후보는 `화면 전환` 으로 끝)로 멈추고 식별이 되면 `Reset` 으로 이 뷰의 구역에서 다시 시작한다.
  알아보지 못하면(Unknown) 구역 없이 1초마다 다시 보고 그동안 배지는 `● 뷰를 알 수 없음`(호박색). 루프는 `viewZones`(지금 뷰의 구역만 — 스냅샷이나 뷰가 바뀔 때만 다시 거름)를 감지기에 넘긴다. 식별에 쓰는 프레임은 방금 맞바꾼 `_lastFrame`(이 스레드만 쓰는 값).
  `ViewRegistry` 는 영상을 열 때 `LoadRoi` 가 구역 파일의 뷰 목록으로 만들고(저장본의 서명은 처음 쓸 때 루프 스레드에서 만든다) 소스를 내릴 때 Dispose 하지 않고 놓는다 — 루프가 아직 Identify 중일 수 있다.
- **첫 호출은 앱을 켤 때 미리 돌려 둔다**(`ViewMatcher.WarmUp`, `MainWindow` 생성자에서 `Task.Run`): ORB·매처·JPEG 의 첫 호출은 네이티브 초기화·JIT 로 수백 ms 걸려, 재생 루프에서 처음 부르면 처리가 250 ms 넘게 밀려 재생 시계를 다시 맞추느라
  세션 첫 재생이 0.80·0.86·0.89배속으로 나왔다(`Test-VfrPlayback` 의 첫 '재생 속도', 11-03 세 번 재현 — 화면 식별을 넣기 전엔 없던 실패). 워밍업 뒤엔 같은 테스트 세 번 모두 0.99~1.01.
- **화면**: 영상 오른쪽 위 배지 — 전환 직후 2초 `● 뷰 V2 · 아는 화면`(강조색)·`● 새 뷰 V3 · 구역 없음`(강조색), 알 수 없으면 `● 뷰를 알 수 없음`(호박색). 왼쪽 패널 '구역' 헤더 `V2 · 3개`(뷰를 모르면 `뷰 확인 중`) — 목록·영상 위 구역 그림은 **지금 뷰의 구역만**이고
  새 구역도 지금 뷰의 것이다. 뷰를 모르는 동안, 그리고 카메라가 돌아가는 중(`화면 이동 중`)·스트림이 끊긴 화면(`화면 끊김`)에서는 구역을 편집할 수 없다(툴팁에 사유 — `UpdateRoiUi`; 멈추기 전의 옛 뷰 번호가 남아 있어도 움직이는 흐린 화면에 구역을 그리지 못하게. 배지가 바뀔 때 `ShowSceneBadge` 가 `UpdateRoiUi` 를 부른다). 구역 이벤트 로그엔 구역이 있는 영상만 `화면 전환 → …` 표시 행이 남는다.
- **방문마다 새로 센다**: 추적기 번호(전환 때 비움)·구역 이벤트의 `이미 안에 있음` 기준·유고의 5초가 뷰로 돌아올 때마다 다시 시작한다. 멈춘 트럭 A 로 돌아오면 `이미 안에 있음` + 5.2초 뒤 유고 후보가 다시 선언된다 — 방문 사이에 번호·정지 시간을 이어 붙이려면 뷰마다 추적기를 두어야 한다(미구현).
- **한계**: 카메라가 같은 프리셋으로 돌아오는 자세 오차가 6% 를 넘으면 다른 뷰로 쌓이고(뷰가 늘 뿐 오작동은 없다 — 구역이 없어 이벤트가 없다), 밤낮·눈비로 화면이 크게 달라지면 같은 자리가 다른 뷰로 등록될 수 있다(실측 없음). 특징이 모자란 영상(어둡거나 흐림)은 뷰를 못 알아봐 구역 이벤트가 안 돈다. 카메라·HUD 는 구역이 없어 해당 없다.
  뷰 이름은 번호뿐이고 지울 수 없다. 이 PC 의 영상엔 **실제로 같은 자리로 돌아오는 PTZ 재방문이 없다**(test4 는 세 뷰를 한 번씩) — 실제 프레임 맞춤(`views`)과 test4 조각을 하드 컷으로 이은 합성 영상(`mkrevisit`)으로만 확인했다.
- **확인**: `TrackerCheck selftest` ⑫(합성 화면 셋 + 같은 글자 고정 요소 · 차가 달라진 같은 화면 · 자세 어긋남(8·20px·확대·밝기) · 크게 옮기면 다른 뷰 · 특징 없는 화면 · 저장본으로 다시 시작 · 뷰 수 한도 · 구역 파일 판 2 왕복·판 1 읽기·잘못된 판 2 9가지 · test4·test1~5 실제 프레임 — 11개),
  `TrackerCheck views`(맞춤 행렬·흔들림·비용 내역), `TrackerCheck mkrevisit`(test4 조각 A 0~8 → B 12~18 → A 1~9 → C 22~26 → B 14~18 을 이은 30초 영상 `out/test4_revisit.mp4` 33 MB — 원본의 조각이라 저장소엔 없다), `tests/ui/Test-ViewIdentity.ps1`(아래 '테스트').
- **남은 것**: 구역을 어긋남만큼 보정하기(호모그래피), 방문 사이에 번호·멈춘 시간 이어 붙이기, 기준 화면 갱신(느린 변화 따라가기), 뷰에 이름 붙이기·지우기·합치기, 카메라·HUD, 실제 PTZ 재방문 영상으로 임계값 확인

## 도메인 지식 — RF-DETR 검토 (2026-11-04~05, 2단계 끝 — 3단계는 사용자 결정 대기)

사용자가 RF-DETR(Roboflow, N·S·M·L 은 Apache 2.0 — XL·2XL 은 PML 1.0)을 써 보자고 해서 세 단계로 나눴다: ① 학습 없이 COCO 사전학습으로 앱 런타임에서 노드 배치·속도·결과 일치 →
② 같은 데이터셋으로 파인튜닝해 같은 시험 분할에서 비교 → ③ 이길 때만 앱에 '모델 종류'로 추가(YOLO 는 남긴다 — NPU 짝 모델은 YOLO 전용). 자세한 건 `experiments/RfDetrBench/README.md`.
- ① 결과(전원 연결 — 배터리면 모두 25% 안팎 느리다): **모든 노드가 DirectML** 에 올라간다(GridSample 도 — 다만 융합 그래프 밖이라 디코더 층마다 그래프가 쪼개진다).
  C# 전처리·후처리로 낸 검출이 PyTorch `predict()` 와 100% 같다. `session.Run` 중앙 FP32: 앱 YOLO26s 6.4 · RF-DETR N 9.7 · S 16.8 · M 21.7 · YOLO26m(COCO) 14.0 ms —
  **같은 COCO 정확도의 YOLO26 보다 20~50% 느리다**(N ≈ YOLO26s 1.5배, S ≈ YOLO26m 1.2배 — T4·TensorRT 발표값과 순서가 뒤집힘). FP16 이면 RF-DETR N 5.2 · S 9.1 · M 12.2 ms(검출 98~99.5% 일치). YOLO FP16 은 DirectML 그래프 융합 실패로 못 쟀다
- **오래 도는 벤치·학습은 전원을 연결하고** — 이 노트북은 배터리일 때 20분 동안 입력이 없으면 유휴 절전(Modern Standby)에 들어가 GPU 작업도 멈춘다(11-04 실제로 벤치 한 회차가 멈춤, Kernel-Power 506 'Idle Timeout').
  덮개 닫힘 동작은 사용자가 꺼 두었다. 멈춘 원인은 506(진입 이유)으로 볼 것 — 507(종료 이유 'Lid')을 보고 덮개 탓으로 짐작했다가 틀렸다
- 앱에 넣을 때: YoloDotNet 으로는 못 돌린다. 전처리는 **정사각형으로 늘이기**(레터박스 아님) + ImageNet 정규화이고 **float 에서 리사이즈해야 공식과 같다**(uint8 에서 줄이면 임계값 근처 검출 1~2% 바뀜 — 대신 float 경로는 720p 한 장 5~7 ms CPU).
  출력은 이름(`dets`·`labels`)으로 고르고, 시그모이드 → 쿼리×클래스 상위 300 → 임계값. 모델 파일 108~121 MB(FP16 54~61 MB — YOLO26s 36 MB)
- **데이터셋은 백업을 쓴다**: `/home/chisu/datasets_backup/visible_2026-11-04`(WSL — 원본 `~/datasets/visible` 의 09-18 동기화본 4,708 · 854 · 973장, SHA-256 으로 확인, 옆에 README).
  라벨링 도구의 'WSL 학습' 버튼이 원본을 DB 로 새로 만들어 `rsync --delete` 로 덮어써서 사용자가 백업해 쓰라고 했다. 백업 안 `data.yaml` 의 `path:` 는 원본 경로라 학습엔 path 를 바꾼 사본을 백업 밖에 만들어 쓴다
- ② 끝(11-05, `experiments/RfDetrBench/step2/`): RF-DETR-S·YOLO26s·YOLO26m 을 같은 백업의 작업 사본으로 각자 기본 조리법 100 epoch 학습했다(WSL — 5.8 · 1.6 · 3.3시간).
  앱과 같은 전처리의 ONNX 로 시험 분할 973장을 pycocotools 로 견줬다. AP 는 **RF-DETR-S 0.742**(FP16 도 0.742) · YOLO26m 0.708 · YOLO26s 0.660 이다.
  YOLO 값은 쌍선형 축소로 잰 것이다. 앱 그대로의 Nearest 면 0.677 · 0.640 이고, 배포 YOLO26s 는 0.653.
  각 라이브러리의 자체 평가기로 재면 0.751 · 0.725 · 0.674 로 순서가 같고 차이도 비슷하다. YOLO 쪽 평가기 차이(rect 배치·출력 경로)는 README 에서 다 나눴다.
  RF-DETR 의 이득은 두 바퀴·사람·버스와 재현율(임계 0.3 에서 0.920 vs 0.875)에서 나온다. **car 와 작은 물체(APs)는 YOLO26m 이 같거나 낫다**(0.832 vs 0.815 · 0.430 vs 0.403).
  다음 후보: 3단계(앱에 '모델 종류'로) · RF-DETR-N 도 학습(FP16 5.2 ms 로 YOLO26s 보다 빠름) · 앱 YOLO 쌍선형 축소('화면 HUD' 의 남은 개선 후보) — 사용자가 고른다

## 도메인 지식 — NPU (AMD Ryzen AI — 왼쪽 패널 "추론 장치"에서 GPU/NPU 선택)

이 PC 의 CPU 는 Ryzen AI 5 340 이라 **내장 XDNA 2 NPU** 가 있다. DEEPX(`.dxnn`)는 DEEPX 칩 전용이라 여기선 못 쓴다.
통합 진행: 0·1단계(런타임 Windows ML 통일) · 2단계(짝 모델 도구) · 3단계(NPU 준비·전환) · 4단계(UI) · 5단계(자동 UI 테스트 `Test-NpuSwitch.ps1`) 끝.

### 앱의 장치 전환 (`MainWindow.Npu.cs`)
- 시작은 **항상 GPU**. 창이 뜬 뒤 `InitInferenceDeviceAsync` 가 NPU 를 쓸 수 있는지만 미리 본다(짝 모델·EP 유무, 컴파일은 안 함) → 왼쪽 패널 **"추론 장치" 콤보**(`InferenceSelector`)의 NPU 항목을 열거나 닫는다.
  쓸 수 없으면 항목이 막히고 **사유는 항목·콤보 툴팁**에 (`ShowOnDisabled`). `--npu` 명령줄이면 확인 뒤 바로 NPU 를 고른다 (바로가기·테스트용 —
  UI 테스트는 `--npu` 를 쓰지 않고 콤보로 고른다. 이 경로는 자동 테스트가 없다).
  GPU 모델 로드가 실패했으면(`_yolo == null`) 확인 없이 NPU 항목을 닫는다('GPU 모델 로드에 실패해 쓸 수 없음' — 'NPU 를 쓸 수 없음:' 접두어 없음)
- 고르면 ①②③ 모두 스레드 풀에서 준비하고 그동안 지금 장치가 그대로 돈다. UI 스레드는 단계 문구만 맡는다 (2026-10-31 이슈 #4 — 예전엔 ② 와 시작 때의 EP 확인이
  UI 스레드라 EP 카탈로그 조회(`FindAllProviders`, 한 번에 3.8~11초, 대부분 기다림) 동안 Dispatcher 가 막혔다. 10-30 엔 창 응답 확인(WM_NULL)으로 재 0.4~0.5초로만 보였다.
  11-01 `Test-NpuSwitch`: ①② 동안 WM_NULL 최대 4 ms·UIA 최대 26 ms). 진행은 **상태바 오른쪽 `Txt_Device`** 에 경과 초와 함께
  (`NPU 확인 중` → `NPU 컴파일 중 (첫 실행 약 1분) 23초` / `NPU 준비 중` → 사라짐), 되돌릴 땐 `GPU 로 되돌리는 중`(진행은 경고 색), 실패하면 `추론 장치 전환 실패 (툴팁)`(위험 색).
  상태바에 두는 이유: HUD 캡처의 무시 영역이라 매초 바뀌어도 새 화면으로 치지 않는다(왼쪽 패널·옛 툴바에 두면 HUD 대상 모니터에서 추론을 깨운다)
- **제목**은 지금 장치를 보여 준다: GPU `… — <GPU 이름> (DirectML)`(테스트가 이걸로 GPU 확인), NPU `… — AMD NPU (VitisAI · a16w8-pc)`
- **NPU 컴파일은 한 번에 하나** — 네이티브 컴파일은 중간에 못 끊는다. 준비 도중 GPU 를 고르면 `_wantNpu` 의향만 바꾸고 끝났을 때 결과를 버린다
  (그 컴파일이 남긴 캐시로 다음엔 수 초). 다시 NPU 를 고르면 새로 컴파일하지 않고 진행 중인 것을 기다린다 — 같은 캐시 키로 두 컴파일이 겹치지 않게.
  GPU 로 되돌리는 중(`SwitchToGpuAsync`, GPU 세션을 새로 만듦)에 다시 NPU 를 고르면 GPU 쪽 결과를 버린다
- 순서: ① 짝 확인(`CheckNpuModel` — 파일 있음, 메타데이터 `npu_quant` 있음, `npu_source_md5` = 지금 `best.onnx` MD5. 메타데이터는 CPU·최적화 끔 세션으로만 읽는다)
  ② EP 카탈로그에서 **VitisAI 만** `EnsureReadyAsync`(필요할 때만 내려받음, 진행률 표시) → `TryRegister` → NPU 장치.
     ② 도 `Task.Run` 으로 돈다. EP 카탈로그의 WinRT 객체(IAgileObject)는 그 안에서만 쓰고 `NpuPrep`(장치·EP 옵션·키·캐시 유무·키 경고)만 UI 스레드로 돌려받는다.
     `Progress<double>` 은 UI 스레드에서 만들어야 내려받기 진행률이 `SetNpuStage` 로 온다. `EnsureReadyAsync` 를 기다린 뒤엔 `Task.Yield()` 로 CLR 스레드 풀로 돌아간다 —
     WinRT 작업을 스레드 풀에서 기다리면 그 작업을 끝낸, CLR 풀이 아닌 스레드에서 이어질 수 있다(11-01 콘솔 실측: WinRT 파일 작업 10번 중 7번).
     새 프로세스에선 이미 받은 EP 도 NotReady·PackageId 없음이라 첫 전환은 `EnsureReadyAsync` 를 지난다
  ③ 세션 생성 = 컴파일. `cache_dir` = `%LOCALAPPDATA%\DetectView\npu_cache`, **`cache_key` = NPU 모델 MD5 `_` EP 패키지 버전 `_` ONNX Runtime 파일 버전**
     (셋 중 하나가 바뀌면 한 번 다시 컴파일 — 이슈 #5, 2026-10-31~11-01). ORT 조각은 이 프로세스에 올라온 `onnxruntime.dll` 의 FileVersion(`1.27.20260711.1.7b7c0e2` —
     빌드 날짜·커밋. `GetVersionString()` 의 "1.27.1" 만으로는 같은 번호로 다시 빌드한 ORT 를 못 가린다)이고, 의미 버전과 앞 두 자리가 맞는 모듈만 본다
     (이 PC 엔 앱에 안 올라오는 `System32\onnxruntime.dll` 1.17 이 있다. 단일 파일 배포본은 `%TEMP%\.net\…` 에 풀린 것을 찾는다).
     이 PC 의 키: `46098AE1F2F1963EEF1459CADC229B65_1.8.80.0_1.27.20260711.1.7b7c0e2`(65자). 쓴 키는 NPU 툴팁 끝 `컴파일 캐시 키: …` 에 보인다(테스트가 읽는다)
     상태 문구 '준비 중'/'컴파일 중' 은 키 폴더에 **파일이 있는지**로 고른다 — 컴파일이 중단·실패하면 빈 키 폴더가 남는다
     (2026-10-30 실측: 그날 VitisAI EP 가 1.8.75 → 1.8.80 으로 바뀌었고, 새 키 폴더가 빈 채 남아 있었다. 폴더만 보던 때는 컴파일 중에도 '준비 중'이 떴다)
     빈 키 폴더가 있어도 VitisAI 는 그 폴더에 다시 컴파일한다(10-30 실측: 빈 폴더를 두고 골라 55초에 전환, 문구 '컴파일 중')
     EP 옵션은 이 둘에 `enable_cache_file_io_in_mem=0`(디스크에 남겨야 다음 실행에서 캐시를 쓴다)까지 셋.
     EP 패키지 ID 를 못 읽으면 EP 라이브러리 파일의 수정 시각·파일 버전(`lib<yyyyMMddHHmmss>-<버전>`)으로, ORT 파일 버전을 못 읽으면 의미 버전으로 대신하고,
     그것도 없으면 그 자리가 `unknown` 이며 NPU 툴팁·Debug 출력에 '버전을 몰라 … 지우세요' 안내가 붙는다(버전을 못 읽어도 전환은 계속). 조각은 `KeyPart` 로 정리(영숫자·`.`·`-` 밖은 `-`, 48자).
     드라이버·NPU 펌웨어는 키에 없다(ORT 장치 정보에 드라이버 버전이 없다 — 그것만 바뀌면 옛 컴파일 결과를 쓴다, 영향 미확인).
     ORT 가 바뀌면 컴파일 결과가 실제로 달라지는지는 확인 안 함 — 런타임을 올려 돌려 본 적이 없다
  ④ `SwapYolo`: `_yoloLock` 안에서 `_yolo` 를 바꾸고 옛 인스턴스는 락 밖에서 **백그라운드로** 해제(추론은 모두 락 안에서 `_yolo` 를 읽으므로
  바꾼 뒤엔 옛 것을 쓰는 곳이 없다. GPU 세션 해제가 0.5초쯤이라 UI 스레드에서 하면 창이 멈칫한다). 창이 먼저 닫혔으면(`_inferenceClosed`) 새 인스턴스를 버린다
- 실측(2026-10-24): 첫 컴파일 **약 84초**(EP 1.8.80 이 된 10-30: 업데이트 뒤 첫 컴파일 약 106초, 그 뒤 캐시 없이 고른 뒤 전환까지 53~55초, 캐시 있으면 5초), 캐시가 있으면 고른 뒤 **5~7초**(대부분 확인 단계 — 두 모델 MD5·메타데이터 세션·EP 등록, 그중 EP 카탈로그 조회가 한 번에 4초 안팎·바쁘면 11초). GPU 로 되돌리기 **0.5초 안팎**.
  11-01(세 조각 키로 바뀐 뒤 첫 선택, Debug, 사용자가 PC 를 쓰는 중): 컴파일 포함 85.1초, 다시 고르면 5.5초.
  재생 처리 FPS NPU **약 52~53** / GPU **약 96~98** (test1.mp4, 재생 중에 왕복해도 끊기지 않음).
  NPU 일 때 이 프로세스의 NVIDIA 전용 메모리 155 → 14~17 MB (DirectML 세션을 놓음), GPU 로 돌아오면 다시 250 MB 대. 재생 중 41~49 MB 는 WPF 그리기(주 모니터가 NVIDIA 쪽)
- 실패 경로 실측: 짝 없음 · 다른 모델의 짝 · FP32 를 `best.npu.onnx` 이름으로 — 셋 다 **컴파일 전에** 거부하고 GPU 에 머문다. 단일 파일 배포본에서도 EP 카탈로그(WinRT)가 돈다
- `best.npu.onnx` 는 빌드 출력에만 복사되고 **publish 에는 복사되지 않는다**(`CopyToPublishDirectory=Never`) — 배포 폴더의 짝은 그 폴더 모델로 도구를 돌려 만든다
- Claude 의 도구에서 띄운 실행은 `%LOCALAPPDATA%` 가 Claude 앱 패키지 폴더로 돌려질 수 있어(uv 캐시가 그랬음), 직접 처음 실행할 때 컴파일을 한 번 더 할 수 있다 (2026-10-25 사용자 터미널에서 돌린 배포본 테스트의 첫 전환이 87.8초 — 이 가설과 맞는다).
  **확인됨(2026-10-30)**: `%LOCALAPPDATA%\Packages\Claude_pzs8sxrjxfjjc\LocalCache\Local\DetectView\npu_cache` 에 10-24 에 도구로 컴파일한 캐시(xmodel 16 MB + context.json)가 있다
`experiments/NpuBench/` 에서 Windows ML + VitisAI EP 로 잰 결과(2026-10-24, 자세한 건 그 README):
- **GPU 가 2.5배 빠르다**: `session.Run` 기준 GPU(DirectML, FP32) 6.1~6.5 ms vs NPU 15.5~16.3 ms. NPU 이점은 속도가 아니라 GPU 를 비워 두는 것과 전력
- 정확도(기본 방식 A16W8·가중치 채널별): GPU 검출 대비 루트 모델 **96.0% / 99.1%**, 배포 모델(09-17 visible) **95.8% / 98.7%**(재현율 / 정밀도).
  어긋난 박스는 거의 전부 신뢰도 0.3 경계 근처. 가중치 텐서별이면 92.0% / 96.5%, A8W8 은 보정 방법에 따라 14~80%
- **짝 모델 `best.npu.onnx`**: `pwsh tools\Make-NpuModel.ps1 [-Model <경로>] [-Bench]` 로 원본 옆에 만든다(약 30~40초, uv 로 `tools/npu_quantize.py` 실행).
  메타데이터 `npu_source_md5`(원본 파일 MD5)·`npu_quant`·`npu_calib` 이 들어간다 — 앱은 이걸로 원본과 짝인지 확인한다(`CheckNpuModel`).
  **루트 `best.npu.onnx` 는 git 으로 추적한다**(루트 `best.onnx` 의 짝). `publish\best.onnx` 를 바꾸면 **그 짝도 다시 만든다**
  (`-Model publish\best.onnx`, gitignore 쪽이라 커밋 안 됨). 양자화는 결정적이라 같은 입력이면 바이트 단위로 같은 파일이 나온다
- **실험 도구도 짝을 확인한다** (이슈 #6, 2026-10-31): `NpuBench.exe [기준] [NPU 모델] [영상당 프레임 수] [--allow-fp32-npu] [--allow-mismatch]` 의 NPU 모델 기본값은
  기준 옆 `<이름>.npu.onnx`(없으면 NPU 단계를 건너뛰고 GPU·CPU 만). 벤치 전에(EP 준비보다도 먼저) 앱의 `CheckNpuModel` 과 같은 규칙으로 본다 —
  `npu_quant` 가 없으면(FP32) NPU 단계만 건너뛰고, `npu_source_md5` 가 기준 MD5 와 다르거나 없으면 아무것도 재지 않고 종료 코드 1.
  예전엔 NPU 모델 기본값이 기준 FP32 자체라 생략하면 FP32 가 NPU 로 가서 1.5시간 뒤 프로세스가 끝났다. `screen.py` 는 `--ref`(기본 루트 `best.onnx`)와 원본이 다른 변형을 건너뛴다.
  NpuBench 의 `cache_key` 는 `{NPU 모델 MD5}_{EP 패키지 버전}_{ORT 의미 버전}` — 앱과 달리 의미 버전이지만 캐시가 자기 `out/vaip_cache` 라 섞이지 않는다
- 보정 전처리는 앱 YoloDotNet 과 같게(비율 유지·가운데·Nearest·검은 여백) — 앱이 NPU 모델에 넣을 입력이 그것이라서. 보정 기본값은 `publish\test1~5.mp4` 각 20장,
  다른 현장 영상을 쓸 모델이면 `-Calib` 로 그 영상·이미지 폴더를 준다
- **FP32 를 그대로 넣으면 안 된다** — EP 의 BF16 자동 변환이 NPU 커널 빌드에서 잘린 경로(`C:\PROGRA~1\WindowsApps\`)로 실패하고 1.5시간 뒤 프로세스를 끝낸다
- **VitisAI `cache_key` 는 모델 파일 해시로 직접 줄 것.** 기본 키는 가중치·스케일이 달라도 같아서 옛 컴파일 결과를 재사용한다(틀린 결과·크래시 실측). 앱·NpuBench 는 여기에 EP·ORT 버전도 붙인다(위)
- Windows ML 은 자체 `onnxruntime.dll`(1.27)을 가져와 옛 DirectML 1.23 조합과 한 프로세스에 못 섞는다 → 본 앱 런타임을 Windows ML 로 바꿨다("절대 건드리면 안 되는 것들" 1번).
  NPU 도 같은 `WindowsMlExecutionProvider` 에 VitisAI 장치를 넘겨 돌린다 (전처리·후처리·그리기는 YoloDotNet 그대로)

## 화면 디자인 (2026-11-01 개편 — 시안 B '관제 콘솔' · 색 조합 B2 '스틸 블루')

사용자가 시안 셋(A Fluent · B 관제 콘솔 · C 미니멀) 중 B 를, B 의 색 조합 다섯 중 B2 를 골랐다 (시안 캔버스는 claude.ai 비공개 아티팩트 `SvZFt9Tv6q79nAoze7c1es`).
- **구역 패널 헤더·전환 배지의 뷰 이름**(2026-11-03, 사용자가 시안 둘(배지 + 구역 패널 머리 · 배지에만) 중 앞의 것을 골랐다): 헤더 `V2 · 3개`, 배지 `● 뷰 V2 · 아는 화면` — 위 "화면 식별"
- **이벤트 로그 패널**(2026-11-03): 영상 칸·재생 줄 **아래**, 영상(파일)일 때만 — 접으면 헤더 한 줄(`이벤트 N건` · `CSV 저장` · `비우기`, 영상 칸이 그만큼 줄어든다), 펼치면 목록 112px (아래 "구역 진입·나감 이벤트")
- **배치**: 왼쪽 패널 232px(소스: 파일 열기 · 카메라 콤보 + 새로고침(아이콘만) + 카메라 시작 · 모니터 콤보 + 화면 HUD / 분석: SAHI / 구역: 목록 · 추가·지우기 · 선택한 구역 유형 (2026-11-03 — 아래 "관심 구역") / 추론 장치 / 맨 아래 정지). 기본 창 높이 820 에서 패널 전체가 스크롤 없이 보인다(구역 목록은 3줄까지, 그 위로는 목록 안에서 스크롤 — 목록 아래에 파일 이름 줄을 두면 '추론 장치' 콤보가 스크롤 아래로 밀려 개수 칸 툴팁으로 옮겼다)
  | 영상 칸(모서리 눈금은 영상 뒤 장식) + 재생 줄(재생/일시정지 · 슬라이더 · 시간), 맨 아래 창 전체 폭 상태바(상태 점 · 문구 · NPU 진행 · 해상도 · FPS · 검출 수 —
  FPS·검출 칸은 폭 고정, 값이 바뀌어도 다른 글자가 안 밀린다: '화면 HUD' 절의 상태바 항목).
  예전 가로 툴바는 최소 폭(900)에서 두 줄로 넘어갔다. 낮은 창(최소 높이 600)에선 패널 위쪽만 스크롤되고 정지는 늘 아래
- **모양은 `App.xaml` 한 곳**: 색(`BaseColor`·`AccentColor`… 와 같은 이름의 `…Brush`, 얼려 둠), 글꼴(`UiFont` = Segoe UI Variable Text → 한글은 맑은 고딕,
  `MonoFont` = Cascadia Mono — 시간·박스 라벨, `IconFont` = Segoe Fluent Icons), 크기, 버튼·콤보·슬라이더·툴팁·스크롤바 스타일. 창은 `TextFormattingMode=Display`(작은 글자가 또렷).
  코드는 색을 직접 쓰지 않고 키로 찾는다 — 제목 표시줄(`OnSourceInitialized`: DWM 다크 모드 + 캡션·테두리·글자 색, 색 지정은 Windows 11 부터),
  상태 점·`● LIVE/HUD`·FPS 칸 문제 문구·NPU 진행(`SetResourceReference`), 박스(`Box.*`). **키 이름을 바꾸면 코드도 같이**
- 지금 켜진 소스 버튼(파일 열기·카메라 시작·화면 HUD)은 강조 바탕 — `ApplyModeToUi` 의 `Ui.IsActive`. 상태 점은 소스가 있으면 초록
- 색 의미: 경고(호박) = NPU 준비 진행·GPU 로 되돌리는 중·캡처 끊김·모니터 없음, 위험(빨강) = 캡처·검출 오류·NPU 전환 실패·`● LIVE/HUD`·정지 버튼
- **박스 그리기 하나로**: 예전엔 파일 재생(YoloDotNet `Draw` — 클래스 색 얇은 선과 흰 글씨 `car (94.76%)`, 영상에 구움)·SAHI(OpenCV 초록, 구움)·HUD(WPF 초록)가 제각각이었다.
  지금은 모두 `BoxLayer.cs`: 클래스 색 모서리 꺾쇠(장치 픽셀 2px, 불투명도 85%, 둘레에 어두운 테두리 35% — 밝은 배경용) + 꺾쇠를 잇는 옅은 1px 테두리(30%) + 상자 위 고정폭 라벨 `CAR 0.95`
  (바탕 72%, 글자는 불투명. 라벨은 모든 상자를 그린 뒤 — 이웃 상자 선이 글자를 가로지르지 않게). 투명도는 2026-11-02 사용자 요청('살짝 더 투명하게')으로 낮췄다
  (라벨 바탕 86→72% · 꺾쇠 100→85% · 테두리 40→30% · 꺾쇠 둘레 45→35% — `Box.LabelBack`·`Box.CornerOpacity`·`Box.FrameOpacity`·`Box.Halo`).
  **작은 물체의 라벨·꺾쇠는 줄인다**(같은 날 사용자 요청 — 라벨: '지금 크기를 기본으로, 물체가 어느 정도 작아지면 그 크기에 따라', 이어서 꺾쇠도): 화면에서 물체 크기 √(폭×높이)가
  `Box.FullSizeFrom`(48 DIP) 이상이면 기본 크기, 그보다 작으면 배율 k = 크기/48 (하한 `Box.MinScale` — 처음 0.6 이었다가 2026-11-03 사용자 요청으로 **0.45**: 글자 11 → 약 5 DIP, 읽으라는 게 아니라 자리 표시)로 줄인다(`Theme.SizeScale`).
  **작을수록 더 투명하게**(2026-11-03 사용자 요청 — 신호대기 행렬(test4 26초)에서 멀리 있는 작은 차 수십 대의 라벨이 배율만 줄여선 여전히 덩어리로 겹쳐서): 박스(테두리·꺾쇠)와 라벨(막대·바탕·글자)에 크기에 따른 불투명도 배율 `Theme.SizeOpacity` 를 `PushOpacity` 로 더한다 —
  크기 `Box.FullSizeFrom`(48) 이상이면 1, `Box.TinySize`(14 DIP) 이하면 `Box.TinyOpacity`(0.45), 그 사이는 직선. 같은 장면에서 전·후를 나란히 봤다: 멀리 있는 차의 라벨은 작고 옅어 영상을 덜 가리고 가까운 차·버스·트럭은 그대로. HUD 오버레이도 같은 `BoxLayer` 라 같이 바뀐다(HUD 에서의 모습은 못 봤다)
  라벨은 글자·여백·막대에 k 를 곱한다. 꺾쇠는 굵기(큰 물체의 장치 픽셀 굵기 × k 를 반올림, 1px 아래로는 안 줄임 — 100% 화면이면 k 0.75 이상 2px, 그 아래 1px, 둘레는 양쪽 1px 씩)와
  팔 길이의 하한(5 DIP × k — 팔 길이는 원래 상자 짧은 변의 1/3, 최대 14 DIP)에 곱한다(`Theme.CornerPens` — 굵기마다 붓을 한 번 만들어 둔다).
  예전엔 굵기 2px(둘레까지 4px)·팔 최소 5 DIP 가 늘 같아서 멀리 있는 작은 차가 꺾쇠에 덮여 안 보였다.
  넓이로 재는 이유는 서 있는 사람(좁고 높음)·버스(넓고 낮음)처럼 한 변만 작은 물체를 작은 물체로 치지 않으려고. test4 3초 장면(영상 칸 926×520)에서 트럭·가까운 차는 그대로,
  멀리 있는 차(8~25 DIP)는 0.6배 — 신호대기 행렬(26초)의 라벨 겹침이 줄었지만 아주 먼 곳은 여전히 겹친다. 앱 안에선 영상 위 `FrameBoxes`(같은 칸, `Show(dets, 프레임 폭, 높이)` 로 Uniform 맞춤)가 그려
  창·영상 크기와 상관없이 선·글자 크기가 같다(배율도 화면 DIP 기준이라 창을 키우면 물체가 커져 라벨·꺾쇠가 기본 크기로 돌아온다). `ProcessFrame`·`RunSahi` 는 박스를 그리지 않고 돌려준다. HUD 는 예전처럼 `Update(dets, 배율)` + `LooksSame`.
  박스 그리기가 재생 루프(Skia, 글자 그림자)에서 UI 스레드로 옮겨 갔지만 재생 처리 FPS 는 떨어지지 않았다 — `Measure-Fps`(test1.mp4, 5회 중앙, Debug, 사용자가 PC 를 쓰는 중):
  개편 전 77.6 → 개편 뒤 79.4 → 개편 전 74.4 (옛 판은 `c8f85c8` 를 따로 빌드)
- **클래스별 검출 수 범례** (2026-11-01 — 사용자가 시안 셋(영상 위 범례 · 왼쪽 패널 목록 · 상태바 위 줄, 같은 시안 캔버스 맨 아래 줄) 중 영상 위 범례를 골랐다.
  '출력'은 화면 표시만 — 기록(CSV)은 원하지 않았다). `BoxLayer` 가 박스·라벨을 다 그린 뒤 그 요소의 왼쪽 위 모서리(`LegendMargin` — 앱 안 영상 칸 8 DIP,
  HUD 오버레이는 모니터 모서리라 16)에 그린다: 머리 줄 `검출  N`(SAHI 결과면 `검출 · SAHI` — `Show(…, legendTitle)`) 아래로 클래스마다 한 줄,
  클래스 색 막대 · 클래스 색 이름(대문자) · 개수(오른쪽 맞춤). 바탕은 `Box.LegendBack`(86% — 11-02 라벨 바탕을 72% 로 낮출 때 줄이 여럿인 범례는 그대로 뒀다), 개수·머리 글자는 `Box.LegendText`·`Box.LegendSubText`,
  폭은 최소 128 DIP(자릿수가 바뀌어도 안 흔들리게), 박스가 없으면 안 그린다. 줄 순서는 고정(`LegendOrder` = `Box.*` 순서 car·truck·bus·motorcycle·scooter·bicycle·person,
  목록 밖 라벨은 그 뒤 이름순) — 개수순이면 재생 중 개수가 엇갈릴 때마다 줄이 자리를 바꾼다.
  영상 칸 모서리라 레터박스가 있으면 그 검은 띠 위(영상을 안 가림)고, 창이 넓어 띠가 없으면 영상 왼쪽 위를 가린다(영상 칸의 왼쪽 위 모서리 눈금은 범례 뒤에 깔린다).
  박스와 같은 렌더 패스라 늘 같은 프레임이고, HUD 오버레이에선 박스와 함께 `LooksSame` 에 묶여 정지 화면에서 다시 그리지 않는다 — 새 화면 고리가 없다.
  (다른 두 안은 HUD 때 걸렸다: 왼쪽 패널은 HUD 캡처 무시 영역 밖이라 HUD 동안 멈춰야 했고, 상태바 위 줄은 무시 영역 안이지만 영상 칸이 줄고 오버레이엔 안 보였다)
  그림이라 UIA 로 안 보여서 범례 글자 `CAR 7 · TRUCK 1`(`LegendText`·`LegendTextChanged`)을 **검출 수 칸(`Txt_DetectionCount`)의 UIA `ItemStatus`** 로 내보낸다
  (`ShowClassCounts`) — 앱 안은 `FrameBoxes`, HUD 는 오버레이가 알리고, 닫힌 오버레이는 알리지 않으니 HUD 를 내릴 때(`CloseOverlay`) 걷는다.
  그리지 않는 속성이라 HUD 캡처에 새 화면을 만들지 않는다. 범례 글자(클래스 이름·개수·머리)는 `Theme` 에 만들어 둔 `FormattedText` 를 다시 쓴다(상자 라벨은 신뢰도가 매번 달라 그때그때).
  재생 처리 FPS 는 떨어지지 않았다 — `Measure-Fps`(test1.mp4, 5회 중앙, Debug, 사용자가 PC 를 쓰는 중): 범례 전 80.3 → 범례 뒤 86.3 → 범례 전 87.9
  (기준선끼리도 7.6 흔들림. 옛 판은 `2cf4acf` 를 scratchpad 에 따로 빌드)
- 테스트가 기대는 것 (바꾸면 테스트도): x:Name 들, 버튼 `Content` 문자열(아이콘은 `Ui.Glyph` — 위 '테스트'), 문자열 툴팁(UIA HelpText), 콤보 항목 `GPU`/`NPU`,
  슬라이더 템플릿의 `PART_Track`(Slider 와 `Window_Deactivated` 가 씀), 오버레이 창 제목 `DetectView Overlay`, 검출 수 칸의 UIA `ItemStatus`(범례 글자 `CAR 7 · TRUCK 1` — 아래)와 `ItemType`(라벨의 추적 번호 `#12 정지 · #15` — 위 '추적기' 절)
- HUD 함정은 그대로다: 자주 바뀌는 표시는 `StatusBarPanel`(한 Border = HUD 무시 영역) 안에만. 그 밖엔 계속 도는 애니메이션을 두지 않는다
  (슬라이더 썸의 빛 번짐은 정적 효과이고 HUD 땐 슬라이더가 숨는다). 오버레이는 클릭 통과·캡처 제외·단일 비주얼 그대로

## 동시성 규칙

- `_captureLock`(VideoCapture), `_yoloLock`(추론 직렬화 — 재생 루프 vs SAHI), `_lastFrameLock`
- `_yolo` 해제(`OnClosed`)도 **`_yoloLock` 안에서** 한다. SAHI 는 `Task.Run` 에서 돌고 `StopPlaybackAsync` 가 기다려 주지 않아서,
  분석 중에 창을 닫으면 추론 도중 ONNX 세션이 해제될 수 있었다
- **`lock` 블록 안에서 `await` 금지.** 결과를 플래그(`ended`, `captureGone`)로 받아 락 밖에서 처리하는 패턴을 이미 쓰고 있다
- `System.Threading.Lock`(.NET 9+) 사용 — `object` 락 아님
- UI ↔ 루프 간 값 전달은 `Interlocked`/`volatile` + 불변 객체(`PixelRect` 등) 참조 교체로 한다
- 여러 값을 한 번에 넘길 땐 **`volatile` 필드를 마지막에** 쓴다(release). HUD 의 `_latestDets` 가 그 예 —
  먼저 쓰면 UI 가 새 박스를 옛 `_latestCapW/H` 로 스케일할 수 있다. 읽는 쪽은 `volatile` 필드를 먼저 읽는다(acquire)
- 버튼 상태 복원은 **`ApplyModeToUi()` 한 곳**으로 한다. 멱등이고 재생 버튼 글자는 `_isPlaying` 을 따른다.
  `IsEnabled` 를 직접 켜면 await 중 모드가 바뀐 경우(예: SAHI 중 HUD 시작) 어긋난다.
  오래 걸리는 작업 중 막을 컨트롤은 상태 깃발을 두고 `ApplyModeToUi` 가 보게 한다(SAHI 의 `_sahiBusy` — 직접 끄고 켜면 `AnalyzeAsync` 가 분석 중에 슬라이더를 다시 켰다).
  await 뒤 결과를 쓰기 전엔 `_loadGeneration` 이 그대로인지 확인한다 (모드 전환마다 증가)
- 소스 전환(파일 열기·카메라 시작·HUD 시작·정지)은 **`_switchingMode` 로 하나씩만** 한다. 정리(`StopPlaybackAsync`)를 await 하는 사이 다른 버튼을 누르면
  두 핸들러가 `_cts`·`_playbackTask` 를 서로 덮어써 재생 루프가 둘 도는 경쟁이 있었다 (2026-10-25)
- 이전 소스 정리는 **`TearDownSourceAsync()` 한 곳**(재생 정지 + 캡처 해제 + HUD 오버레이 닫기). 예전엔 HUD 에서 곧바로 파일·카메라로 가면 오버레이 창이 남았다.
  열기에 실패하면 `ResetToIdle(문구)` 로 `_mode = None` 까지 되돌린다 — 옛 모드가 남으면 버튼이 열리지 않은 소스 기준으로 켜졌다
  HUD 도 같다 (2026-10-31, 이슈 #2 — 예전엔 캡처 생성이 실패하면 MessageBox 만 뜨고 'HUD 실행 중'·빈 오버레이로 남았다): `StartHud` 가 모드·오버레이를 먼저 세우고
  캡처는 루프 스레드에서 만든다(GDI DC 규약). 루프가 첫 캡처 생성 결과를 `TaskCompletionSource` 로 알리고, `Btn_Screen_Click` 은 `_switchingMode` 를 쥔 채 루프 작업과 함께
  (`WhenAny`) 기다린다 — 루프가 알리지 않고 끝나거나 시작 전에 취소돼도 멈추지 않는다. 실패면 `TearDownSourceAsync` → `ResetToIdle("화면 캡처 시작 실패")` → 오류 창(메인 창이 주인).
  기다리는 사이 창이 닫혔으면(루프 토큰 취소) 아무것도 띄우지 않는다
  `OnClosed` 는 `TearDownSourceAsync` 를 부르지 않고 같은 세 단계를 직접 부른다 — 정리 단계를 더할 땐 둘 다 고칠 것
- 길이 분석(`ScanFrames`)·SAHI 타일 루프는 `_loadGeneration` 이 바뀌면 중간에 멈춘다 (결과는 어차피 버려지는데 수 초~15초 CPU·GPU 를 계속 썼다)
- 버리는 추론 세션(NPU·GPU 전환 실패·취소 때 만든 것)도 `SwapYolo` 처럼 백그라운드에서 해제한다 (세션 해제 0.5초쯤 UI 가 멈칫)

## 컨벤션

- **주석 · UI 문자열 · 사용자 응답 모두 한국어.** 주석은 "무엇"이 아니라 "왜 이렇게 했는지"(특히 위 함정들)를 남긴다
- 카메라 인덱스는 DirectShow 열거 순서와 일치시킨다 (`DsDevice.GetDevicesOfCat` + `VideoCaptureAPIs.DSHOW`). OpenCV 인덱스만 믿으면 항상 0번 카메라만 열린다
- OpenCvSharp: `new Mat(rows, cols, type, IntPtr, step)` 은 폐기됨 → `Mat.FromPixelData(...)`
- 열거형 이름은 `VideoCaptureAPIs` (`VideoCaptureApis` 아님)
- WPF 프로젝트의 암시적 using 에는 `System.IO` 가 없다 → `System.IO.Path` 처럼 전체 이름으로
- `Vortice.DXGI` 와 `Vortice.Direct3D11` 을 함께 쓰면 `MapFlags` 등이 모호하다 → 전체 이름으로
- WPF 의 `Track`(`System.Windows.Controls.Primitives`)은 추적기의 `Track`(`Tracker.cs`, 같은 네임스페이스 `DetectView`)과 이름이 겹쳐 using 보다 추적기 쪽이 먼저 잡힌다 → 전체 이름으로
- C# 14(.NET 10)에선 배열의 `.Reverse()` 가 LINQ 가 아니라 제자리 `MemoryExtensions.Reverse(Span)` 로 잡힌다(반환값 없음 — 문장으로 쓰면 원본을 뒤집는다) → 새 순서가 필요하면 `Enumerable.Reverse(arr)`

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
  처음 보인 위치 5.0~7.0초(실측 5.2초) → 카메라가 돈 뒤(11.7초~) 표시·강조가 사라짐 → 로그: 유고 후보 행 한 줄(5~9초)·`유고 해소 (화면 전환) · 멈춤 10.3초` 행 → CSV: `incident`(Z1 · lane · 멈춤 5000ms)·`incident_end` 각 한 행 →
  비우고 2초로 시크해 재생하면 7~9.3초(실측 7.3초)에 다시 선언 → 선언 중에 다시 2초로 시크하면 `유고 해소 (시크)` 행. 항목 수 16
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
  파일 재생(배속·검출) → 재생 중 GPU 로 → 다시 NPU(캐시) — 재생이 끊기지 않는지 ③ 실패 경로 3가지(짝 없음·다른 모델의 짝·FP32 를 짝 이름으로)에서
  NPU 항목이 막히고 툴팁에 사유, GPU 유지. ③ 은 exe 폴더를 **하드 링크로 본뜬 옆 폴더(`<exe 폴더>.npu-test`, gitignore)** 에서 파일을 바꾼다 —
  원래 폴더(배포 폴더 포함)는 건드리지 않고 끝나면 지운다. "다른 모델의 짝"은 MD5 가 다른 모델(루트·배포 폴더 `best.onnx`)이 있어야 해서 없으면 [INFO].
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
- 마지막 전체 통과: 2026-11-03 낮, **폴더 이전·이름 정리 뒤 새 위치의 배포본 `E:\DetectView\publish`** — `E:\VS_E\YOLO11_Test` → `E:\DetectView`, `YOLO11_Test.csproj`·`.slnx` → `DetectView.csproj`·`.slnx`, 네임스페이스 `YOLO11_Test` → `DetectView`,
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

### 테스트 영상 내용 (유고 감지 구상용 기준 사례, 2026-10-23 분석)

둘 다 실제 도로 CCTV 다. 시각은 각 파일 안의 시각.

- **`test4.mp4`** (30.5초) — **방향이 바뀌는 CCTV**(이어 붙인 영상 아님). 10.5~11.4초·20.5~21.2초에 카메라가 돌아간다(모션 블러, 방향 안내 오버레이 글자도 같이 움직임)
  - 0~10.5초: 2차로 한가운데 고장 정차한 **덮개 덮은 덤프트럭**(약 98×86 px) + 트럭 오른쪽 뒤에 서 있는 사람(8×23 px — 전체 프레임 검출로는 안 잡히고 2배 확대해야 잡힘). 다른 차는 옆 차로로 비켜 감 → 양성(차로 차단)
  - 21.2~30.5초: 교차로 정지선 앞 신호대기 행렬 21~32대가 내내 정지, 교차·반대 방향·우회전 차량은 움직임 → 음성(정상 정차)
- **`test5.mp4`** (47.0초, 1280×854 60fps, 26.6 MB) — [청주영덕선] 사일산터널(청주외부1). 흰색 소형 화물차가 **터널 출구 직후 갓길**에 내내 정차, 터널에서 나온 차가 옆 차로로 통과 → 양성(갓길). 카메라 회전 없음
  - 원본: 사용자가 UTIC 웹 뷰어를 OBS 로 녹화한 `2026-10-23 11-18-42.mkv`(OBS 녹화 폴더, 2560×1440) 의 18.0~65.0초를 영상 영역 (643,314) 1280×854 로 잘라 ffmpeg `mp4v` 로 저장. 검출 결과는 원본 크롭과 172/175 일치(IoU 0.98), `Set(PosFrames)` 시크 정확
  - 원본 스트림은 약 31fps 라 프레임의 약 49% 가 중복이다(60fps 로 저장해 타이밍 유지). **29.63~29.98초에 스트림이 검게 끊긴다**(원본의 실제 끊김, 일부러 남김)
  - MSMF H.264 로 쓰면 고정 비트레이트라 같은 길이가 200 MB 가 된다 — 다시 만들 땐 `VideoCaptureAPIs.FFMPEG` + `mp4v`

## 코드 인덱스 (graft)

`graft/` 에 파일별 카드 + 호출 그래프가 들어 있다 (2026-09-21 도입. 인덱싱 대상은 git 이 추적하는 소스 — 루트, `tests/`, `tools/`, `experiments/`).
소스를 통째로 읽는 대신 여기서 `file:line` 을 받아 그 지점만 열면 된다.

```bash
graft ask "화면 캡처 루프 흐름" --source
```

`ask`(위치+이해) · `grep`(빠짐없이 찾기) · `callers <sym>`(호출자, `--depth`) · `skeleton <file>`(파일 API) · `map`(전체 조감).
쿼리마다 그래프를 자동 갱신하므로 **코드를 고친 뒤 `graft build` 를 다시 돌릴 필요는 없다.**
`graft/` 안의 `.md` 카드만 턴 끝에 갱신되므로, 방금 고친 파일은 카드보다 위 도구를 믿을 것.
또 git 이 추적하는 `.ignore` 의 `!graft/` 때문에 Grep(ripgrep) 결과에 `graft/*.md` 카드(줄 범위 포함)가 섞여 나온다 — 소스 줄로 착각하지 말 것.

### 함정 1 — `old/` 를 커밋하면 인덱스가 망가진다
graft 는 무시 규칙을 **`git ls-files --exclude-standard` 로만** 적용한다. 그래서:
- git 저장소가 아니었을 때는 `.gitignore` 가 통째로 무시돼 `obj/` 생성 코드와 `old/` 백업까지 잡혔다 (15개 → **234개**).
  검색하면 `old/backup_.../MainWindow.xaml.cs` 의 옛 `InitializeYoloModel` 이 상위에 뜬다 — 실수로 백업본을 고칠 위험.
- git 은 **추적(tracked) 중인 파일은 `.gitignore` 와 무관하게 포함**시키고 graft 도 그 규칙을 따른다.
  → `old/` 를 한 번이라도 `git add` 하면 무시 규칙이 무력해진다. 절대 커밋하지 말 것.

### 함정 2 — C# 간선은 `?.` 호출과 메서드 그룹을 놓친다
`graft callers` 는 호출자를 **빠뜨릴 수 있다**. 실제로 확인된 두 형태:
```csharp
_overlay?.ClearBoxes();                 // null 조건부 호출 → 간선 없음
await Dispatcher.InvokeAsync(OnVideoEnded);   // 메서드 그룹 전달 → 간선 없음
```
둘 다 `callers` 결과가 0 이라 죽은 코드처럼 보이지만 멀쩡히 쓰인다.
이 코드베이스는 Dispatcher 람다와 `?.` 가 많아 특히 자주 걸린다.
**이름을 바꾸거나 지우기 전에는 `callers` 가 아니라 `graft grep "<이름>"` 으로 확인할 것.**

### 현재 모드
LLM 개념 맵(`graft build --deep`)은 API 키가 필요해서 돌리지 않았다. 지금은 렉시컬 검색 모드이고,
구조 파악·호출 추적에는 충분하다.

## 도구 사용 주의

- 이 환경의 **Bash 도구에 PowerShell cmdlet을 넣으면 "command not found"** 가 난다. PowerShell 도구를 따로 쓸 것
- **`python` 은 PATH 에 없다** (WindowsApps 스텁만 있음). `uv` 로 격리 실행한다: `uv run --no-project --python 3.12 --with <패키지> <스크립트>.py`
  (한 줄 명령은 `uv run --no-project --python 3.12 python -c "..."`). 다른 프로젝트의 가상 환경(같은 PC 의 다른 저장소 `.venv` 등)은 건드리지 않는다
- **git 저장소다** (2026-09-21 `git init`, 브랜치 `main`). 되돌리기 안전망이 생겼으니 `old/` 에 새 백업을 쌓을 필요는 없다 — 기존 `old/` 는 git 이전 기록이라 그대로 둔다
  (예외: 2026-11-02 공개 때 GitHub 에서 지운 옛 git 기록의 백업 `old/DetectView-history-2026-11-02.bundle` — 아래 원격 항목)
- **원격은 `origin` 하나 = 공개 저장소 GitHub `chisu0208/DetectView`** (2026-11-02 공개 — 사용자 결정: 옛 기록은 공개하지 않는다).
  옛 기록의 마지막 상태(`f8d66f4`)를 그대로 담은 기록 없는 첫 커밋(`6c43790`)부터 시작했다. 올리는 건 `main` 하나다.
  - **옛 개발 기록(2026-09-21~11-02, 커밋 75개)은 이 PC 에만 있다** — 로컬 태그 `old-history`(`f8d66f4`)와 백업 파일 `old/DetectView-history-2026-11-02.bundle`
    (44.9 MB, `git clone <파일>` 로 되살아난다 — 복원 확인함). 그 기록을 담았던 비공개 저장소(`DetectView` → `DetectView-history` 로 이름 바꿈)는 같은 날 사용자가 지웠고 이슈 #1~#11 도 함께 없어졌다.
    **이 문서·메모의 커밋 해시(`6d1f61a` 등)와 이슈 번호 #1~#11 은 모두 그 옛 기록의 것**이다 — 해시는 `git show <해시>`·`git log old-history` 로 이 PC 에서 본다.
    이슈 본문은 남아 있지 않지만 내용은 이 문서의 해당 서술(번호 옆)에 있다. 태그와 bundle 을 둘 다 지우면 옛 기록이 사라진다(`git gc` 는 태그가 가리키는 커밋은 지우지 않는다).
  - **옛 기록을 `origin` 에 올리지 말 것** — `old-history` 태그·`claude/*`·`spike/*` 브랜치·옛 worktree 가 다 그렇다. 올리면 옛 커밋 75개(실제 이메일이 작성자인 72개와 뺀 CCTV 스크린샷 포함)가 공개된다.
    로컬 `.git/hooks/pre-push`(git 이 추적하지 않음 — 지우면 보호가 사라진다)가 옛 기록의 뿌리 커밋 둘을 조상으로 가진 커밋·태그를 공개 저장소로 올리면 push 전체를 거부한다(그래서 `git push --tags` 도 막힌다).
    옛 기록의 커밋을 `main` 에 merge·cherry-pick 하지도 말 것 — merge 는 옛 기록 전체를, cherry-pick 은 옛 작성자(실제 이메일)를 끌고 온다. 필요한 건 파일 내용만 옮긴다.
  - **rebase·amend·force push 로 올린 기록을 고쳐 쓰지 말 것** (옛 기록 때도 웹에서 생긴 첫 커밋을 force push 로 지우지 않고 합쳤다 — `f7646a6`).
  - 공개 정리 (2026-11-02 점검 — 비밀값은 옛 기록 전체에 없었다): 저장소 전체 GNU AGPL-3.0(`LICENSE`), 남의 저작물 고지 `THIRD-PARTY-NOTICES.md`(README 라이선스 절엔 NuGet 외부 구성요소 표).
    커밋 작성자 이메일은 이 저장소 설정(`.git/config` 의 `user.email` — worktree 도 같이 쓴다)만 GitHub noreply `42445310+chisu0208@users.noreply.github.com` 이다(전역 `~/.gitconfig` 도 같은 날 사용자 승인으로 같은 noreply 로 바꿨다 — 이 PC 의 다른 저장소 push 가 GH007 로 막히지 않게).
    README 스크린샷(test4 3초 장면, 실제 도로 CCTV 프레임 — 출처 기록 없음)은 영상 사용 권리를 확인하지 못해 뺐다(옛 기록에만 있다).
    GitHub 계정은 'Keep my email addresses private'·'Block command line pushes that expose my email' 을 켰다(11-02 사용자) — 실제 주소가 작성자인 커밋을 push 하면 GitHub 가 막는다(GH007).
    모델 메타데이터 `description` 에 학습 데이터 폴더 경로가 들어 있지만 그대로 뒀다 — 모델 파일을 바꾸면 MD5 가 바뀌어 NPU 짝·컴파일 캐시를 다시 만들어야 한다
- GitHub CLI `gh` 2.103.3 은 2026-10-30 winget 으로 설치했다(`C:\Program Files\GitHub CLI\gh.exe`). 설치 전에 뜬 앱·셸에선 PATH 에 없으니 전체 경로로 부른다
- **남은 버그·개선 거리는 GitHub 이슈로 관리한다** — 2026-10-30 분석에서 남은 낮은 버그 11건을 #1~#11 로 등록했고, 2026-10-31~11-01 모두 고쳤다
  (이 문서의 해당 서술 옆에 그 번호가 남아 있다 — **그 이슈들은 옛 비공개 저장소와 함께 지워졌다**. 공개 저장소의 이슈는 #1 부터 다시 매겨지니
  옛 번호는 '옛 이슈 #7' 처럼 구분해 가리킨다). 고칠 땐 커밋 메시지에 `Fixes #N` 을 넣는다(`main` 에 push 되면 공개 저장소의 그 이슈가 닫힌다). 이 문서의 서술도 같이 고친다.
  여러 이슈를 한꺼번에 고칠 땐 이슈 묶음마다 worktree 를 따로 두고 고친 뒤 검토를 거쳐 `main` 에 cherry-pick 했다 — UI 테스트는 합친 뒤 한 번에 돌린다
- 추적 대상은 소스, `best.onnx`·`best.npu.onnx`, UI 테스트(`tests/ui/`, 합성 테스트 영상 `media/vfr60_nodur.webm` 포함), 도구(`tools/`), 실험 프로젝트 소스(`experiments/*/`), `README.md`(GitHub 첫 화면), `LICENSE`·`THIRD-PARTY-NOTICES.md` 다. `bin/`·`obj/`·`old/`·`publish/`·`graft/` 는 `.gitignore` 로 제외 — **`old/` 를 커밋하면 안 된다** (아래 graft 항목 참고)
