# CLAUDE.md — DetectView

WPF(.NET 10) 실시간 객체 검출 애플리케이션. YOLO ONNX 모델을 **DirectML(GPU)** 로 추론해
① 동영상 파일 ② 웹캠 ③ 바탕화면 실시간 캡처(투명 HUD 오버레이) 세 가지 소스를 처리한다.

## 이름 (2026-11-03 정리 — 이제 모두 `DetectView`)

| 항목 | 값 | 비고 |
|---|---|---|
| 폴더 | `E:\DetectView` | 예전 `E:\VS_E\YOLO11_Test` — 2026-11-03 옮김 (옛 폴더는 사용자가 백업한 뒤 지웠다 — 2026-11-04 무렵) |
| 프로젝트·솔루션 / 네임스페이스 / `AssemblyName` / 제품명 / 창 제목 | `DetectView` (`DetectView.csproj`·`DetectView.slnx`·`namespace DetectView`) | 예전엔 `YOLO11_Test` 였다 — 옛 이름은 git 기록(`old-history` 태그)과 클라우드에 백업한 옛 `old/` 안에만 남는다 |
| 배포 폴더 | `publish` | 예전 `publish_gpu_ver` |
| 모델 | YOLO**26m** 파인튜닝 ONNX, 입력 **640×384**(16:9 영상용) (`best.onnx`, 78 MB, **7클래스**) | 2026-11-06 교체 — 아래 "RF-DETR 검토" 3단계에서 사용자가 골랐다. 이전: YOLO26s 640×640(36 MB, 2026-09-11~ · 그 전엔 9.5 MB 모델). YOLO11 아님 |

원래 학교 과제로 시작해(제출 완료) 지금은 개인 취미 프로젝트다. CPU 추론 → GPU(DirectML) 전환이 끝난 상태.
**이 문서·`docs/`·메모의 옛 서술(날짜가 붙은 기록)에는 `YOLO11_Test`·`publish_gpu_ver` 대신 새 이름이 들어가 있다** — 이름만 일괄로 바꿨고 그때 폴더가 달랐다는 건 위 표가 말해 준다.
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
- 결과: `DetectView.exe` 126.4 MB (2026-10-24, Windows ML 런타임. 이전 DirectML 1.23 은 119.4 MB. 2026-10-30·10-31·11-01 다시 배포도 같은 크기 — 11-01 은 세 번: 이슈 수정판 `53db7ed`, 화면 개편판 `c274575`, 클래스별 검출 수 범례판 `fb06cf9` · 11-02 추적기 연결·라벨/꺾쇠·상태바판 `3939f40` · 11-03 관심 구역·화면 전환 감지·구역 이벤트 로그·유고 규칙·화면 식별판 `10f7b31` · 같은 날 폴더 이전·이름 정리판 — 위 '이름' · 11-05 YOLO 쌍선형 레터박스·NPU 짝 재보정판 `f863585` · 11-06 YOLO26m 640×384 판 `a378a58` — 이때는 사용자 승인으로 배포 폴더 모델도 바꿨다, 아래). 테스트 영상(`test1~4.mp4`)은 같은 폴더에 있어도 덮어쓰지 않는다
- publish 뒤에는 배포본으로 UI 테스트를 돌린다: `pwsh tests\ui\Run-All.ps1 -ExeDir publish`
- **`publish\best.onnx` 는 루트의 것과 다를 수 있고, 사용자가 수시로 바꾼다** (YOLO26s ↔ YOLO26x(COCO 80클래스),
  데이터셋이 다른 7클래스 모델 등 — 2026-09-23 한 세션 안에서도 바뀌었다). 건드리지 말 것.
  2026-11-06 엔 사용자 승인으로 루트와 같은 YOLO26m 640×384(`94A26C12…`)로 바꿨다 — 그 전 YOLO26s(09-17, `87D0E1CB…`)와 그 짝은
  `publish\best.yolo26s-0917.onnx` · `best.yolo26s-0917.npu.onnx` 로 이름을 바꿔 옆에 두었다(앱은 `best.onnx`·`best.npu.onnx` 만 읽는다. `Test-NpuSwitch` 의 '다른 모델의 짝'이 이것을 쓴다).
  **성능을 재거나 비교하기 전엔 그 순간 어떤 모델인지 확인한다** — 앱이 떠 있으면 창 제목에 보이고(아래),
  아니면 파일 끝의 Ultralytics 메타데이터를 읽는다:
  `uv run --no-project --python 3.12 python -c "import re,sys;f=open(sys.argv[1],'rb');f.seek(-65536,2);t=f.read().decode('utf-8','ignore');[print(k,'=',v.strip()[:80]) for k,v in re.findall(r'(description|names|date|imgsz)\x12.?([^\x00-\x08\x0e-\x1f]+)',t)]" publish/best.onnx`
  (`imgsz` 가 `[384, 640]` 이면 16:9 영상용 640×384 판 — 같은 가중치의 640×640 판과 이름·날짜가 같을 수 있다)
- 배포 폴더 모델을 바꾸면 NPU 짝도 다시 만든다: `pwsh tools\Make-NpuModel.ps1 -Model publish\best.onnx` (→ 그 옆 `best.npu.onnx`, 아래 "NPU")
- **앱용 ONNX 를 새로 내보낼 땐 end-to-end(`[1,300,6]`)인지 본다** — Ultralytics 8.4.142(라벨링 도구가 쓰는 판)의 `export(format="onnx")` 기본값은 NMS 전 원출력 `[1,84,8400]`(메타데이터 `end2end False`)이다.
  `nms=False`(또는 `end2end=True`)를 줄 것. 지금 루트 `best.onnx`(YOLO26m 640×384, 11-06)는 8.4.142 에서 `nms=False` 로 내보낸 end-to-end 다. 그 전 YOLO26s 는 8.4.31 기본값으로 end-to-end 였다 (2026-11-04 실측 — `experiments/RfDetrBench` README. 원출력 꼴을 앱이 어떻게 처리하는지는 확인 안 함 — 아래 3번의 전제가 깨진다).
  같은 판의 **val 도 `nms` 를 안 주면 one-to-many 출력 + NMS 로 잰다**(학습 중 검증·`best.pt` 고르기도) — 학습 로그의 mAP 는 앱이 쓰는 end-to-end 출력보다 0.007~0.010 높게 나온다(2026-11-05 실측). 앱과 같은 걸 재려면 val 에 `nms=False`.
  그 몫은 val 의 NMS 가 **multi-label**(한 박스가 여러 클래스로 남음)이라서다 — `nms=True` 로 내보낸 ONNX(박스마다 최고 클래스 하나)는 앱 경로에서 end-to-end 보다 AP 가 0.003~0.005 **낮다**(같은 날 — 아래 3번·"RF-DETR 검토")
- publish 는 `best.onnx` 를 `PreserveNewest` 로 복사한다 → **루트 `best.onnx` 가 배포 폴더의 것보다 새로우면 덮어쓴다**.
  **worktree 에서 배포하면 거의 늘 그렇다**(루트 `best.onnx` 의 시각이 체크아웃한 때다) — 그땐 임시 폴더로 `-o` 를 주고 `DetectView.exe` 만 배포 폴더로 옮긴다(2026-11-05).
  worktree 에서 UI 테스트를 돌릴 때도 테스트가 `<저장소>\publish\test*.mp4` 를 찾으니 worktree 에 `publish` → `E:\DetectView\publish` 연결(junction)을 두고, 끝나면 링크만 지운다
- `best.onnx` 는 단일 파일 exe에 포함되지 않는다. **exe 옆에 같이 놓아야** 실행된다.
  경로는 작업 디렉터리가 아니라 `AppContext.BaseDirectory`(exe 폴더) 기준이다. 예전엔 상대 경로라 바로가기의 "시작 위치"나
  다른 폴더에서 실행하면 모델을 못 찾고 엉뚱하게 "GPU 드라이버 확인" 오류를 냈다 (2026-09-23 수정, 실측 확인)
- `.csproj` 의 `ExcludeSymbolsFromPublish` 타깃이 publish 출력에서 pdb(~92 MB, exe 밖에 따로 생기던 것)와 `.lib` 를 뺀다. 배포물은 `DetectView.exe` + `best.onnx` 뿐이다.
- 실행 시 창 제목이 `DetectView — <모델> <입력 가로×세로> (<학습일> 학습) — <GPU 이름> (DirectML)` 로 바뀌면 GPU 초기화 성공이다.
  예: `DetectView — YOLO26m 640×384 (2026-11-05 학습) — NVIDIA GeForce RTX 5060 Laptop GPU (DirectML)`. 스모크 테스트는 이걸로 확인하면 된다.
  모델 이름은 ONNX 메타데이터 `description`("Ultralytics YOLO26m model …")에서, 입력 크기는 ONNX 입력 꼴에서, 학습일은 `date` 에서 뽑는다(`DescribeModel`).
  입력 크기는 2026-11-06 더했다 — 같은 가중치의 640×640 판과 640×384 판은 이름·학습일이 같은데 속도가 31% 다르다.
  `date` 는 Ultralytics 가 **내보낸 때**를 쓴다(지금 모델은 11-05 학습을 마치고 같은 날 내보냄 — 배포 YOLO26s 도 학습한 날 내보냈다)
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
| `MainWindow.Inference.cs` | 모델 로드, GPU 선택(`SelectBestGpu`·`CreateGpuYolo` — 판정은 `GpuAdapters`), `ProcessFrame`, `DetectTile`/`DetectBgra`(검출이 모두 여기로 — 레터박스는 `YoloLetterbox`, `_yoloLock` 안에서). YoloDotNet 의 축소 방식 `InputSampling`(쌍선형 — GPU·NPU 세션 모두 `YoloOptionsFor`, 레터박스 경로엔 듣지 않는 대비) |
| `YoloLetterbox.cs` | YOLO 입력 레터박스를 OpenCV 쌍선형(`INTER_LINEAR`)으로 직접 만들어 YoloDotNet 에 넘기고(배율 1), 박스를 +0.5 해 그린 자리 그대로의 여백·배율로 되돌린다(2026-11-05 — 아래 '화면 HUD' 성능 절의 '입력 축소'). 크기는 모델 입력(`InputSize` — 정사각형이 아닐 수 있다. SAHI 타일·창 제목도 이걸 읽는다). WPF 없음 — TrackerCheck·RuntimeParity 가 소스로 링크 |
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
| `old/` | **2026-11-06 지웠다** — 사용자가 클라우드에 백업한 뒤. 있던 것: 이전 CPU 버전 보관본·최적화 전 백업(`backup_20260911_*`)·학교 과제 제출본(폴더·zip)·`old/docs/`(SRS·아키텍처 설계서)·옛 모델(9.5 MB)·공개 전 git 기록 bundle(아래 '도구 사용 주의'의 원격 항목). `.csproj` 의 제외 글롭과 `.gitignore` 의 `/old/` 는 그대로 둔다 — 다시 생겨도 빌드·graft 가 안전하게(아래 5번·graft 함정 1) |
| `publish/` | 배포 산출물 + 테스트 영상(`test1~4.mp4`, 1280×720 30fps · `test5.mp4`, 아래 "테스트 영상 내용"). 원래의 VFR 테스트 파일 `recording.webm` 은 없어졌고, 대신 합성 파일 `tests/ui/media/vfr60_nodur.webm` 을 쓴다 |
| `tests/ui/` | UI 자동화 회귀 테스트 (PowerShell + UI Automation). 아래 "테스트" 참고 |
| `README.md` | GitHub 첫 화면 (2026-11-02 작성 — 기능·빌드·사용법·테스트·라이선스). 화면이 크게 바뀌면 같이 고칠 것. 스크린샷(test4 3초 장면, 실제 도로 CCTV 프레임)은 같은 날 넣었다가 영상 사용 권리를 확인하지 못해 뺐다(`docs/screenshot.png` 삭제 — 이 PC 의 옛 기록 `old-history` 의 `10e65db` 에만 남음). 다시 넣으려면 권리가 분명한 영상으로 |
| `docs/` | CLAUDE.md 에서 옮긴 긴 설명·실측·지난 기록(2026-11-05) — 아래 '자세한 문서' 표. 세션마다 자동으로 읽히지 않는다 |
| `LICENSE` · `THIRD-PARTY-NOTICES.md` | 저장소 라이선스 GNU AGPL-3.0 (2026-11-02 — 들어 있는 `best.onnx` 가 Ultralytics YOLO26 파인튜닝이라 Ultralytics 가 프로젝트 전체에 AGPL-3.0 을 요구)과 저장소 안 남의 저작물 고지(모델 AGPL-3.0 · `WindowsMlExecutionProvider.cs` 의 바탕 YoloDotNet MIT · `.claude/` 의 graft 연동 파일 MIT — 각 전문). 남의 코드·파일을 가져오면 여기에 더한다 |
| `tools/` | `Make-NpuModel.ps1`(+ `npu_quantize.py`): YOLO 모델의 NPU 용 짝 `<이름>.npu.onnx` 만들기 (아래 "NPU") |
| `best.npu.onnx` | 루트 `best.onnx` 의 NPU 짝 (A16W8·가중치 채널별 QDQ, 21.4 MB, git 추적 — 2026-11-06 YOLO26m 640×384 로 다시 만듦, MD5 `BD18D32C…`). 원본을 바꾸면 다시 만든다 |
| `experiments/` | 따로 빌드하는 실험 프로젝트(각자 `.csproj`). 본 `.csproj` 의 `Compile/Page/None Remove` 로 빠져 있다. `NpuBench/`: AMD NPU 실험 — 짝 확인(`npu_quant`·`npu_source_md5`) 뒤에만 NPU 에 넣는다 (아래 "NPU"). `RuntimeParity/`: 런타임 교체 전후 검출 결과 비교 (1번 항목, GPU 선택은 두 빌드 모두 `GpuAdapters.cs`, 레터박스는 `YoloLetterbox.cs` 링크 — 2026-11-05 부터 좌표가 실수라 그 전 CSV 와 견주지 말 것). `TrackerCheck/`: 추적기 검증 (아래 "추적기")·테두리 멈춤 판정 합성 장면(`selftest` — 관심 구역 `Roi.cs` 의 판정·파일 검사 ⑧, 화면 전환 감지 합성 장면 ⑨, 구역 진입·나감 이벤트 ⑩, 유고 규칙 ⑪, 화면 식별 ⑫도 여기)·화면 식별 맞춤 실측(`views`)·화면 전환이 있는 합성 영상 만들기(`mkrevisit`)·화면 전환 감지 임계값 실측(`scene`)·GPU 고성능/저전력 판정(`gpu`)·중복 박스 필터 전후 비교 (3번 항목)·YOLO 입력 축소 방식(`InputCheck.cs` — 데이터셋 시험 분할을 앱 경로로 검출하고 Skia·OpenCV 축소를 화소 비교 `testsplit`, 축소 시간·결정성 `inputbench`). `RfDetrBench/`: RF-DETR 를 앱 런타임(DirectML)에서 — 노드 배치·속도·결과 일치, 같은 COCO 정확도의 YOLO26 과 비교. `step2/` 는 같은 데이터로 학습(WSL)·내보내기·시험 분할 평가, YOLO 변형(NMS 내보내기·640×384 — 속도는 `yolo`)도 (아래 "RF-DETR 검토"). 산출물 `*/out/`·`*/bin/`·`*/obj/`·`*/.venv/` 는 gitignore |
| `graft/` · `.claude/` | 코드 인덱스(graft)와 그 에이전트 연동. graft 는 CLI 로만 쓴다 — MCP 서버 등록은 2026-11-02 사용자 결정으로 전역(`~/.claude.json`)과 이 저장소(`.mcp.json` 삭제) 모두 뺐다. `graft/` 는 재생성 가능한 캐시라 커밋하지 않는다. `.claude/helpers/graft-statusline.cjs`(상태줄)는 graft 설치 경로를 `%APPDATA%\npm` 에서 계산한다(2026-11-02 — graft 가 박아 넣은 `C:\Users\<이름>\…` 경로를 저장소에서 뺌. graft 를 다시 설치해 박힌 경로로 돌아오면 다시 고칠 것). graft 훅은 전역 `~/.claude/settings.json` 에만 둔다 — 프로젝트에도 있으면 둘 다 돌아 세션마다 graft 안내문이 두 번 들어가고 도구마다 훅이 두 번 돈다(2026-11-02 프로젝트 쪽 훅과 `graft-hooks.cjs` 를 뺌. graft 를 이 저장소에 다시 설치해 돌아오면 다시 뺄 것) |

## 자세한 문서 (`docs/` — 세션마다 자동으로 읽히지 않는다)

CLAUDE.md 가 매 세션 컨텍스트를 너무 많이 차지해서(13만 자) 2026-11-05 긴 설명·실측·지난 기록을 `docs/` 로 글자 그대로 옮겼다.
아래 절 가운데 링크가 있는 것은 요약과 꼭 지킬 규칙만 남겼으니 **그 영역을 고치기 전엔 해당 문서를 읽는다**.

| 문서 | 내용 |
|---|---|
| `docs/playback.md` | 파일 재생 · 시크 — VFR 판정 · 시크 전략 · 재생 시계 · 멈춘 채 시크·한 장 보기 · EOF |
| `docs/screen-hud.md` | 화면 HUD — 오버레이 · DXGI/GDI 캡처 · 하이브리드 GPU · 캡처 복구 · 상태바 칸 · 새 화면 고리 · 지연 처리 · 성능 실측 · 입력 축소 |
| `docs/incident-detection.md` | 유고 감지 — 추적기 · 관심 구역 · 화면 전환 감지 · 구역 진입·나감 이벤트 · 유고 규칙 · 화면 식별 |
| `docs/npu.md` | NPU — 장치 전환 · 짝 모델 · 컴파일 캐시 키 · 실측 |
| `docs/ui-design.md` | 화면 디자인 — 배치 · `App.xaml` · 박스 그리기 · 클래스별 개수 범례 |
| `docs/rf-detr.md` | RF-DETR 검토 — 단계별 결과(실험 자체는 `experiments/RfDetrBench/README.md`) |
| `docs/testing.md` | UI 테스트 — 파일별 설명 · 지난 전체 통과 기록 · 테스트가 확인하지 않는 것 |

새로 알게 된 실측·경과는 해당 `docs/` 문서에 쓰고, CLAUDE.md 에는 모든 세션이 알아야 할 규칙·함정만 더한다.

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
  `Directory.Build.props` 는 MSBuild 가 위로 올라가며 처음 찾은 하나만 읽어서 `.claude\worktrees\` 의 빌드(예전엔 `old\*.csproj` 도)도 가져간다 — 지금은 속성 하나라 무해하지만 타깃을 넣으면 그 빌드들에도 걸린다

### 2. `TargetClasses` 의 `"scooter"` 는 의도된 것
```csharp
["person", "car", "motorcycle", "truck", "bus", "scooter", "bicycle"]
```
COCO-80에 없는 클래스처럼 보여도 **사용자가 의도한 것**이다. 정리 대상이 아니다.
루트 모델의 클래스가 정확히 이 7개(`bicycle, bus, car, motorcycle, person, scooter, truck`)라서 그 모델로는 필터가 걸러내는 게 없다.
모델을 바꿔 쓸 때의 안전장치다 — 실제로 배포 폴더에 COCO 80클래스 YOLO26x 를 넣어 쓴 적이 있고, 그땐 이 필터가 6개 클래스만 남긴다(COCO 엔 scooter 없음).

### 3. 모델은 end-to-end NMS 내장
출력 `[1,300,6]`, 입력 고정(`dynamic:False`) — 지금 루트 모델은 `[1,3,384,640]`(16:9 영상용 640×384, 2026-11-06. 그 전 YOLO26s 는 `[1,3,640,640]`). 추론 해상도는 **모델이 결정**하며 코드로 바꿀 수 없다 —
레터박스(`YoloLetterbox`)·SAHI 타일·NPU 짝 보정은 모델 입력 크기를 읽어 따른다. 16:9 가 아닌 소스(test5 1280×854 · 4:3 웹캠)는 640×640 판보다 작게 들어간다(소스 비율로 세션을 고르지 않는다 — "RF-DETR 검토").
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
- **NMS 를 넣어 내보낸 모델(Ultralytics `nms=True`)로 바꿔도 이 필터는 그대로 필요하다** (2026-11-05, 2단계 학습 YOLO26s 로 test1~5 모든 프레임 — `experiments/RfDetrBench` README 'YOLO 남은 여지'):
  같은 클래스 IoU>0.7 쌍은 180~553 → 0~6 으로 거의 없어지지만, 클래스별 NMS 가 못 지우는 같은 자리 car+truck·범위만 다른 중복은 영상 5개 중 4개에서 늘고(test4 1,027 → 1,577쌍) 박스도 5~10% 많다.
  그 모델 자체도 권하지 않았다 — 시험 분할 AP 가 오히려 0.003~0.005 낮고(작은 물체·AR100 손해) `NonMaxSuppression` 이 DirectML 에 없어 CPU 로 가 `session.Run` 이 1 ms 느리다

### 4. DXGI GPU 선택 시 `PointerUSize`
어댑터 열거는 `GpuAdapters.Enumerate` 한 곳이다. `AdapterDescription1.DedicatedVideoMemory` 는 `SharpGen.Runtime.PointerUSize` 다.
`(long)` 캐스팅하면 **`OverflowException`** 이 나서 GPU 이름이 fallback("GPU")으로 표시된다. `(ulong)` 을 쓸 것.

### 5. `old/` · `publish*/` 빌드 제외 글롭
SDK 기본 글롭이 `old/**` 의 소스와 생성 `.g.cs` 까지 끌어와 **724개 컴파일 에러**가 났던 적이 있다.
`.csproj` 의 `Compile/Page/ApplicationDefinition/Resource/None Remove` 항목을 지우지 말 것. (`old/` 에 소스 백업을 넣어도 안전한 이유 — `old/` 는 2026-11-06 지웠지만 다시 생기면 같은 일이 나서 그대로 둔다)

## 도메인 지식 — 파일 재생 · 시크

자세한 설명·실측은 [docs/playback.md](docs/playback.md) — 이 영역을 고치기 전에 읽는다. 꼭 지킬 것:
- 화면 녹화 webm/mp4 는 **VFR 이고 메타데이터가 거짓**이다(fps=1000·`FRAME_COUNT` 음수 쓰레기값 등). `metaReliable` → `ProbeSeekConsistent` → 백그라운드 `ScanFrames`(`_loadGeneration` 으로 무효화) 순으로 판정한다
- 메타데이터를 못 믿는 파일(`_seekByGrab`)엔 **`Set(PosFrames)` 를 절대 쓰지 않는다**(길이 없는 파일은 스트림이 망가져 곧바로 EOF 로 보인다) — grab 정밀화로만 앞으로 가고 뒤로는 파일을 다시 연다.
  CFR 만 `PosFrames` 로 시크한다(`PosMsec` 은 못 믿는다)
- **시크는 UI 스레드에서 하지 않는다** — UI 는 `_pendingSeekMs` 만 쓰고 재생 루프가 다음 `Read` 직전에 `ApplyPendingSeek`. 슬라이더 값을 코드로 바꿀 땐 `_isPlaybackUpdatingSlider` 로 감싼다
- 재생 속도는 **재생 시계**(`WaitUntilDueAsync` — 각 프레임을 그 영상 시각에 보인다)로 맞춘다. 프레임마다 '1/fps − 처리 시간'만큼 기다리기로 되돌리지 말 것(0.82·0.41배속이 났었다)
- **재생 루프는 EOF·카메라 유실에서 절대 `break` 하지 않는다**(`continue` 로 `_playSignal` 에 파킹). EOF 뒤 재생은 처음부터(`_atEof`)
- 멈춘 채 시크의 한 장 보기(`_stepRequest`·`_stepping`)·SAHI 의 시크 대기(`WaitForStepFrameAsync`·`SeekPending`)·슬라이더 드래그 깃발(썸의 `DragStarted`/`DragCompleted`)은 경쟁을 고친 자리다 — 순서·조건을 바꾸기 전에 문서를 본다
- 디코딩 버퍼는 `_lastFrame` 과 맞바꿔 재사용한다(매 프레임 `Clone()` 금지), `BitmapSource.Create` 는 `IntPtr` 오버로드

## 도메인 지식 — 화면 HUD

자세한 설명·실측은 [docs/screen-hud.md](docs/screen-hud.md) — 오버레이 · 캡처(DXGI/GDI, 하이브리드 GPU 함정, 캡처 복구, 상태바 칸 폭) · '스스로 새 화면을 만드는' 고리 · 지연 처리 ·
성능 실측치(기각된 아이디어) · 입력 축소(`YoloLetterbox`). 이 영역을 고치기 전에 읽는다. 꼭 지킬 것:
- 오버레이는 `SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)` — **없으면 그린 박스가 다시 캡처돼 피드백 루프**. 클릭 통과(`WS_EX_TRANSPARENT`), 배치는 WPF `Left/Top` 이 아니라 `SetWindowPos` 물리 픽셀,
  박스는 요소를 만들지 않고 `BoxLayer.OnRender` 한 번에 그린다
- DXGI `DuplicateOutput` 은 **그 모니터가 붙은 어댑터의 장치로만** 된다. 하이브리드 GPU 에서 DXGI 가 안 되는 건 대개 프로세스 GPU 모드(Windows 그래픽 설정 — exe 경로별) 탓이지 코드 버그가 아니다.
  안내 문구는 `ScreenCapture.DescribeDxgiFailure` → `HybridGpuAdvice` 한 곳, 저전력/고성능 판정은 `GpuAdapters`
- 캡처 루프는 **전용 스레드에서 동기로**(GDI 의 `GetDC`/`ReleaseDC` 는 같은 스레드여야 한다). `TryCapture` 가 준 `Mat` 은 다음 호출 전까지만 유효하다. 실패는 `DxgiScreenCapture.OnFailure` 가
  '기다리면 된다(잠금 화면 등 — 던지지 말 것)'·'다시 만들어야 한다(장치 제거·회전)'로 가른다
- 정지 화면에서 GPU 가 쉬게 하는 장치: `BoxLayer.LooksSame`(보이는 결과가 같으면 다시 그리지 않음 — 결정적 추론에 기댄다), 상태바 영역을 캡처 무시 영역으로, 상태바 갱신은 초당 4회.
  **자주 바뀌는 표시는 `StatusBarPanel` 안에만** 둔다(그 밖에 두면 HUD 대상 모니터에서 추론을 깨운다)
- 루프 → UI 는 latest-wins(`_uiUpdatePending` — UI 콜백은 맨 앞에서 깃발을 내린다). HUD FPS 의 역수 ≈ 박스 지연
- **측정으로 기각된 아이디어 — 다시 제안하지 말 것**: GDI `CAPTUREBLT` 제거, 캡처 버퍼 알파 보정, (Nearest 때의) 긴 변 640 선축소. UI 테스트가 보고하는 HUD FPS 는 벤치마크로 쓰지 말 것
- YOLO 입력 레터박스는 `YoloLetterbox`(OpenCV 쌍선형, 박스 +0.5 — 빼면 AP 가 쌍선형 이득보다 크게 떨어진다). 남은 개선 후보: 모션 예측 외삽 (16:9 소스에 640×384 입력은 2026-11-06 루트 모델로 들어갔다 — YOLO26m)

## 도메인 지식 — SAHI (on-demand)

재생을 멈춘 상태에서 현재 프레임 1장에만 실행한다 (실시간 경로에는 넣지 않음 — 타일 수만큼 추론이 늘어남).
- 타일 = **모델 입력 크기**(지금 640×384), `overlap = 0.3`(가로·세로 각각). 2026-11-06 전엔 640 정사각형 고정이었다 — 640×384 모델에 640×640 타일을 넣으면 0.6배로 줄어 작은 물체를 원래 크기로 보려는 뜻이 사라진다
- 병합(2026-10-30 바꿈, 사용자 결정): **전체 프레임 검출 1회**(원본 SAHI 의 perform_standard_pred — 겹침(640 이면 192px · 384 면 116px)보다 큰 물체는 어느 타일에도 온전히 안 들어온다)와
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

자세한 설명·파라미터 근거·검증은 [docs/incident-detection.md](docs/incident-detection.md) '추적기'. 꼭 지킬 것:
- `Tracker.cs` 는 **시각 기준**(프레임 수 아님) 탐욕적 IoU 다. 차량 6종은 한 묶음, 중복 박스는 한 물체로 묶고(`DuplicateBoxes`), 정지 판정은 마주 보는 두 변 중 덜 움직인 쪽으로 잰다.
  `Reset()` 은 트랙만 비우고 번호는 이어서 센다
- 앱은 `UpdateTagged`(번호·정지를 `Det.TrackId`·`Det.Stationary` init 속성으로)를 쓰고 **프레임 크기를 넘긴다**(`Update(dets, ms, 폭, 높이)` — 테두리에 걸친 박스의 멈춤 판정)
- 파일은 영상 시각(`posMs`)으로 돌리고 **시크한 프레임은 먼저 `Reset()`** 한다(뒤로 가는 시각만 저절로 비워진다). 카메라는 멈췄다 재개하면 비운다
- HUD 는 **가상 시계**(새 화면 사이를 `HudTrackStepCapMs` 250 ms 까지만 흐른 것으로) — 실제 시간을 넘기면 정지 화면에서 트랙이 만료돼 번호가 바뀐다. 캡처 교체·모니터 배치 변경·재개 때 비운다
- 락이 없으니 루프 스레드 하나에서만 부른다. 번호는 라벨 글자라 테스트는 검출 수 칸의 UIA `ItemType`(`#12 정지 · #15`)으로 읽는다

## 도메인 지식 — 관심 구역 (ROI, 유고 감지 2단계 — 2026-11-03)

자세한 설명은 [docs/incident-detection.md](docs/incident-detection.md) '관심 구역'. 요점:
- 구역 = 번호 `Z1`…(지운 번호는 다시 쓰지 않는다) + 유형(차로 `lane` · 갓길 `shoulder` · 정지선·대기 `stopline` · 무시 `ignore`) + 프레임 0~1 좌표 점 3개 이상(`Roi.cs` — WPF 없음)
- 영상 옆 `<영상>.roi.json`(판 2 — 뷰 목록 포함)에 **바뀔 때마다 바로 저장**하고 구역이 없으면 파일을 지운다. 못 읽으면 상태바에 사유, 옛 파일은 `.bad` 로 복사하고 빈 구역으로 시작한다
- 편집은 **파일 재생을 멈춘 채일 때만**(`UpdateRoiUi` 한 곳 — 뷰를 모르거나 화면이 움직이는 중에도 막힌다). 구역 그림은 편집이 바뀔 때만 다시 그린다(HUD 고리 없음). HUD·카메라엔 구역이 없다

## 도메인 지식 — 화면 전환 감지 (유고 감지 2단계의 하나, 2026-11-03)

자세한 설명·실측은 [docs/incident-detection.md](docs/incident-detection.md) '화면 전환 감지'. 요점:
- `SceneChangeDetector`: 64×36 흑백(`SceneThumbnail` — **앱과 실험 도구가 같은 코드**여야 도구가 잰 임계값이 맞는다)의 연속 프레임 평균 절대차로 움직임(≥ 12)·잠잠(< 6)을 보고,
  안정된 화면이 직전 안정 화면과 15 이상 다르면 전환이다. 검은(끊긴) 프레임은 빼고 본다. 5초 넘게 안정되지 않으면 전환으로 친다
- **파일만** 쓴다(카메라·HUD 는 안 씀). 움직이는 동안은 추적기에 넣지 않고(박스는 번호 없이 보임) 전환이면 `tracker.Reset()`. 시크·재개 때 감지기도 `Reset()`. 배지는 `Txt_SceneBadge`

## 도메인 지식 — 구역 진입·나감 이벤트 (유고 감지 3단계, 2026-11-03)

자세한 설명은 [docs/incident-detection.md](docs/incident-detection.md) '구역 진입·나감 이벤트'. 요점:
- `ZoneEventDetector`: 추적된 박스의 **발 위치**(아래 가운데)로 진입 0.2초 · 나감 0.5초를 확정하고, 안에서 1.5초 넘게 끊기면 '나감 (추적 끝)'. Reset 직후 이미 안에 있으면 '이미 안에 있음'. 무시 구역은 이벤트가 없다
- 루프는 구역의 **불변 스냅샷**(`_zoneShapes`)만 읽고, 구역을 바꾸면 `_zoneEventsReset` 깃발로 감지기를 비운다. 로그는 영상 아래 접이식 패널(최대 2000건), [CSV 저장] → `<영상>.events.csv`

## 도메인 지식 — 유고 규칙 (유고 감지 4단계, 2026-11-03)

자세한 설명은 [docs/incident-detection.md](docs/incident-detection.md) '유고 규칙'. 요점:
- `IncidentDetector`: 차량의 발이 **차로 구역** 안이고 `Stationary` 가 5초 이어지면 유고 후보다. 정지선·대기·무시 구역과 정체(같은 차로 구역에 자신 말고 정지 차량 3대 이상)는 빼고,
  **이미 선언한 것은 정체로 풀지 않는다**. 판정이 1초 안에 돌아오면 이어진 것으로 본다
- 해소(움직임·차로 이탈 1초 · 추적 끝 · 화면 전환 · 시크 · 구역 변경)에는 꼭 해소 행을 남긴다. 선언된 박스는 `Det.Incident` 로 빨갛게, 로그 헤더에 `⚠ 유고 후보 N`

## 도메인 지식 — 화면 식별 (뷰 식별, 유고 감지 2b, 2026-11-03)

자세한 설명·실측은 [docs/incident-detection.md](docs/incident-detection.md) '화면 식별'. 요점:
- `ViewMatcher`: ORB 800 + RANSAC 호모그래피 — 같은 뷰 = 인라이어 ≥ 30 · 칸 ≥ 4 · 이동 ≤ 6% · 배율 0.9~1.12. 애매하면 알 수 없음(새 뷰로 쪼개지 않음), 처음 보는 안정 화면은 새 뷰 V1… 로 등록한다.
  구역은 어긋남을 보정하지 않는다(호모그래피로 옮기지 않음)
- 영상을 열 때·시크 뒤·전환이 안정된 뒤에만 식별한다. 첫 호출이 느려 **앱을 켤 때 `ViewMatcher.WarmUp`** 을 돌린다(빼면 세션 첫 재생이 0.8배속 안팎). 구역 파일은 구역을 편집할 때만 저장한다
- 뷰로 돌아올 때마다 번호·유고 5초를 새로 센다(방문 사이 이어 붙이기는 미구현)

## 도메인 지식 — RF-DETR 검토 (2026-11-04~06 — 3단계: 사용자가 YOLO26m 640×384 를 골라 루트 모델을 바꿨다)

경과·수치는 [docs/rf-detr.md](docs/rf-detr.md)와 `experiments/RfDetrBench/README.md`. 요점:
- 시험 분할 AP: RF-DETR-S 0.742(FP16 같음) · YOLO26m 0.708 · 배포 YOLO26s(앱 경로) 0.6658 · RF-DETR-N 0.665(11-06). **16:9 이미지만 보면 RF-DETR-S = YOLO26m(0.695 · 0.696)** — RF-DETR-S 는 재현율·사람·두 바퀴, YOLO26m 은 작은 물체·car·truck 이 낫다
- 3단계(2026-11-06): 후보 RF-DETR-S FP16(새 추론 경로) · YOLO26m 640×384(지금 경로) 중 **사용자가 YOLO26m 640×384 를 골랐다** — 루트 `best.onnx`·NPU 짝을 바꾸고 SAHI 타일을 모델 입력 크기로.
  앱 경로(C# — YoloDotNet 정수 좌표라 파이썬 평가기보다 0.006 낮다) 시험 분할 AP **0.694 · 16:9 0.687**(배포 YOLO26s 0.666 · 0.631). 소스 비율로 640×640 세션을 고르는 일(README 에 적었던 '세션 둘')은 하지 않았다.
  RF-DETR-N 은 YOLO26s 급이고 16:9 에선 더 낮아(0.611 vs 0.654) 권하지 않았다 — 이점은 속도뿐
- 학습·평가 데이터는 WSL 백업 `/home/chisu/datasets_backup/visible_2026-11-04` 를 쓴다(원본은 라벨링 도구의 'WSL 학습' 버튼이 덮어쓴다). 오래 도는 벤치·학습은 전원을 연결한다(배터리 20분 무입력이면 GPU 작업도 멈춘다)

## 도메인 지식 — NPU (AMD Ryzen AI — 왼쪽 패널 "추론 장치"에서 GPU/NPU 선택)

자세한 설명·실측은 [docs/npu.md](docs/npu.md) — 이 영역을 고치기 전에 읽는다. 꼭 지킬 것:
- 시작은 항상 GPU 다. NPU 는 짝 모델 `<이름>.npu.onnx`(`tools\Make-NpuModel.ps1` — A16W8·가중치 채널별 QDQ)가 있고 메타데이터 `npu_quant` 가 있으며 `npu_source_md5` 가 지금 `best.onnx` 의 MD5 와 같을 때만 쓴다.
  **FP32 를 NPU 에 넣지 말 것**(EP 의 BF16 변환이 실패하고 1.5시간 뒤 프로세스가 끝난다). 짝은 publish 에 복사되지 않는다 — 배포 폴더 모델을 바꾸면 그 짝도 도구로 다시 만든다
- 확인 · EP 준비 · 컴파일은 모두 스레드 풀에서(UI 스레드에서 EP 카탈로그를 조회하면 창이 수 초 멈췄다), 진행은 상태바 `Txt_Device`. 컴파일은 한 번에 하나 — 진행 중 다시 고르면 기다린다
- VitisAI `cache_key` = NPU 모델 MD5 `_` EP 패키지 버전 `_` ONNX Runtime 파일 버전 — 기본 키는 다른 모델에도 옛 컴파일 결과를 재사용한다(틀린 결과·크래시 실측).
  캐시는 `%LOCALAPPDATA%\DetectView\npu_cache`(Claude 도구로 띄우면 패키지 폴더로 돌려질 수 있다)
- `_yolo` 교체는 `SwapYolo`(락 안에서 바꾸고 옛 것은 백그라운드 해제). 속도는 GPU 가 2.3~2.5배 빠르다(지금 YOLO26m 640×384 `session.Run` 8.8 vs 20.4 ms — 11-06) — NPU 의 이점은 GPU 를 비워 두는 것과 전력

## 화면 디자인 (2026-11-01 개편 — 시안 B '관제 콘솔' · 색 조합 B2 '스틸 블루')

자세한 설명은 [docs/ui-design.md](docs/ui-design.md). 꼭 지킬 것:
- 모양은 **`App.xaml` 한 곳**(색·글꼴·스타일·박스 `Box.*`) — 코드는 색을 직접 쓰지 않고 키로 찾는다. **키 이름을 바꾸면 코드도 같이**
- 박스·라벨·클래스별 개수 범례는 모두 `BoxLayer`(앱 안 영상 `FrameBoxes`와 HUD 오버레이 공용). 범례 글자는 검출 수 칸의 UIA `ItemStatus`, 추적 번호는 `ItemType` 으로 나간다
- 테스트가 기대는 것(바꾸면 테스트도): x:Name 들, 버튼 `Content` 문자열(아이콘은 `Ui.Glyph`), 문자열 툴팁(UIA HelpText), 콤보 항목 `GPU`/`NPU`, 슬라이더 템플릿의 `PART_Track`, 오버레이 창 제목 `DetectView Overlay`
- 자주 바뀌는 표시는 `StatusBarPanel`(HUD 캡처 무시 영역) 안에만 두고, 그 밖엔 계속 도는 애니메이션을 두지 않는다

## 동시성 규칙

- `_captureLock`(VideoCapture), `_yoloLock`(추론 직렬화 — 재생 루프 vs SAHI), `_lastFrameLock`
- `_yoloLock` 안에서만: 레터박스 버퍼(`_letterbox` — 다시 쓴다)와 YoloDotNet 의 결과 리스트(`RunObjectDetection` 이 돌려주는 리스트는 모듈 것이라 다음 호출이 비우고 다시 채운다).
  `YoloLetterbox.Detect` 가 락 안에서 결과를 옮긴다 (2026-11-05 — 예전 `ProcessFrame`·`DetectBgra` 는 그 리스트를 락 밖에서 읽었다. UI 가 동시 호출을 막아 드러나진 않았다)
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
테스트 파일마다 무엇을 보는지 · 지난 전체 통과 기록 · 테스트가 확인하지 않는 것은 [docs/testing.md](docs/testing.md) — 테스트를 고치거나 결과를 견줄 땐 거기를 본다.

```bash
pwsh tests/ui/Run-All.ps1
```

- 기본 대상은 `bin\Debug` 아래 **가장 최근에 빌드된** `DetectView.exe`(먼저 `dotnet build`). `-ExeDir publish` 로 배포본도 본다(상대 경로는 저장소 루트 기준).
  필요: GPU(DirectML), `publish\test1~5.mp4`(gitignore — worktree 에선 `publish` 연결을 둔다, '빌드 · 배포'). 실행 중 창이 떴다 닫히고 바탕화면을 캡처한다.
  NPU 전환 검사는 AMD NPU 와 exe 옆 짝 모델이 있을 때만 돈다
- 요소는 **`x:Name`(UIA AutomationId)** 으로 찾고 [파일 열기]만 버튼 글자로 찾는다. 버튼 아이콘은 `Ui.Glyph` 로 그린다 — `Content` 를 문자열이 아닌 것으로 바꾸면 UIA 이름이 비어 버튼을 못 찾는다.
  툴팁도 문자열로(UIA HelpText). 상태 문구·버튼 글자·툴팁 문구·창 제목 형식도 검사 대상이다 — **이름·문구를 바꾸면 테스트도 같이 고칠 것**
- 파일: `Test-FilePlayback`(재생·시크·SAHI·EOF·범례·번호) · `Test-Tracking`(test4 덤프트럭의 번호·정지, 시크 뒤 새 번호) · `Test-Roi`(실제 마우스로 구역 편집·파일) ·
  `Test-SceneChange`(전환 배지·검은 끊김) · `Test-ZoneEvents`(이벤트 로그·CSV) · `Test-Incident`(유고 후보·해소) · `Test-ViewIdentity`(합성 재방문 영상의 뷰 식별) ·
  `Test-VfrPlayback`(VFR webm 두 개) · `Test-ScreenHud`(HUD·캡처 방식·결함 주입) · `Test-NpuSwitch`(NPU 전환·창 응답·실패 경로)
- `Measure-Fps.ps1 -ExeDir <폴더>` 는 테스트가 아니라 A/B 계측이다 — 실행 묶음끼리 수 FPS 흔들리니 기준선을 앞뒤로 두 번 잰다
- 종료 코드 = 실패 건수(`Run-All` 은 0/1). 스크립트는 UTF-8 **BOM** 으로 저장한다(Windows PowerShell 5.1 이 한국어를 깨뜨리지 않게). 파일 열기 대화상자 자동화 요령은 `Common.ps1` 머리말
- 마지막 전체 통과: 2026-11-06 아침, 배포본 `publish`(`a378a58` — 배포 폴더 모델도 YOLO26m 640×384) `Run-All` 237 항목 모두 통과. 같은 날 Debug 251 항목도 모두 통과.
  자세한 기록과 그 전 기록은 docs/testing.md

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
  → `old/` 를 한 번이라도 `git add` 하면 무시 규칙이 무력해진다. 절대 커밋하지 말 것(2026-11-06 지웠지만 다시 만들어도 같다).

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
- **Windows 경로의 백슬래시가 이스케이프로 풀려 제어 문자가 들어간 적이 있다** — 2026-11-05~06 `publish` 뒤 백슬래시 + `best…` 가 백스페이스(0x08) 한 글자로 바뀌어
  문서 셋·메모 하나에 들어갔다(화면엔 `publishest.onnx` 로 보인다 — 11-06 고침). Bash 도구는 명령을 큰따옴표로 한 번 더 감싸 heredoc 안에서도 백슬래시 둘을 하나로 줄인다.
  경로가 든 글을 도구로 쓴 뒤엔 제어 문자가 없는지 본다: `git grep -lP "\x08"`
- **git 저장소다** (2026-09-21 `git init`, 브랜치 `main`). 되돌리기 안전망이 생겼으니 백업 폴더를 따로 쌓지 않는다. git 이전 기록이 든 옛 `old/`(공개 전 기록 bundle 포함 — 아래 원격 항목)는
  2026-11-06 사용자가 클라우드에 백업하고 이 PC 에서 지웠다
- **원격은 `origin` 하나 = 공개 저장소 GitHub `chisu0208/DetectView`** (2026-11-02 공개 — 사용자 결정: 옛 기록은 공개하지 않는다).
  옛 기록의 마지막 상태(`f8d66f4`)를 그대로 담은 기록 없는 첫 커밋(`6c43790`)부터 시작했다. 올리는 건 `main` 하나다.
  - **옛 개발 기록(2026-09-21~11-02, 커밋 75개)은 공개하지 않았다** — 이 PC 의 로컬 태그 `old-history`(`f8d66f4`)와, 사용자가 클라우드에 백업한 bundle `DetectView-history-2026-11-02.bundle`
    (44.9 MB — 2026-11-06 까지 `old/` 안에 있었다. `git clone <파일>` 로 되살아난다 — 복원 확인함)에만 있다. 그 기록을 담았던 비공개 저장소(`DetectView` → `DetectView-history` 로 이름 바꿈)는 같은 날 사용자가 지웠고 이슈 #1~#11 도 함께 없어졌다.
    **이 문서·메모의 커밋 해시(`6d1f61a` 등)와 이슈 번호 #1~#11 은 모두 그 옛 기록의 것**이다 — 해시는 `git show <해시>`·`git log old-history` 로 이 PC 에서 본다.
    이슈 본문은 남아 있지 않지만 내용은 이 문서의 해당 서술(번호 옆)에 있다. 이 PC 엔 이제 태그만 있다 — 태그를 지우면 이 PC 에선 옛 기록이 사라진다
    (클라우드의 bundle 로 되살린다. `git gc` 는 태그가 가리키는 커밋은 지우지 않는다).
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
