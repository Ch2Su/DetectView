# DetectView

YOLO ONNX 모델로 **동영상 파일 · 웹캠 · 바탕화면**을 실시간 객체 검출하는 Windows 데스크톱 앱입니다.
WPF(.NET 10)로 만들었고, 추론은 Windows ML 의 ONNX Runtime 으로 **GPU(DirectML)** 에서 돌립니다. AMD Ryzen AI **NPU** 로 바꿔 돌릴 수도 있습니다.

## 주요 기능

- **동영상 파일** — mp4 · avi · mkv · webm. 원본 속도로 재생하고, 메타데이터가 맞지 않는 화면 녹화(가변 프레임레이트) 파일도 정확한 위치로 시크합니다.
- **웹캠** — 연결된 카메라를 골라 실시간으로 검출합니다.
- **화면 HUD** — 모니터 화면을 캡처해 검출하고, 그 위에 클릭이 통과하는 투명 오버레이로 박스를 그립니다.
  - 캡처는 DXGI Desktop Duplication 이고, 안 되는 환경에선 GDI 로 대신합니다.
  - 오버레이는 화면 캡처에서 빠지므로 자기가 그린 박스를 다시 검출하지 않습니다.
  - 화면이 바뀌지 않으면 추론을 쉽니다.
- **검출 표시** — 클래스 색 모서리 꺾쇠와 라벨(`CAR #12 정지 0.95`), 영상 왼쪽 위에 클래스별 개수 범례를 그립니다. 화면에서 작은 물체는 라벨과 꺾쇠를 줄여 그립니다.
- **객체 추적** — 같은 물체에 프레임이 넘어가도 같은 번호(`#12`)를 붙이고, 1초 넘게 멈춘 물체에는 `정지`를 표시합니다.
- **화면 전환 감지** — 동영상 속에서 카메라가 돌아가거나 화면이 바뀌면 영상 오른쪽 위에 `화면 이동 중`을 띄우고, 박스에 번호를 붙이지 않다가 안정되면 번호를 새로 매깁니다(`화면 전환 · 번호 새로 매김`). 스트림이 잠깐 검게 끊겼다 같은 화면으로 돌아오는 것은 전환으로 보지 않습니다(`화면 끊김`). 동영상 파일에서만 동작합니다.
- **화면 식별(뷰)** — 카메라가 돌아간 뒤 화면이 안정되면 어느 뷰(카메라 자세)인지 알아봅니다. 아는 화면이면 `뷰 V1 · 아는 화면`, 처음 보는 화면이면 `새 뷰 V2` 로 기억하고(ORB 특징점 + 호모그래피), 구역은 그린 뷰에서만 보이고 쓰입니다. 같은 뷰로 돌아오면 그 뷰의 구역으로 이벤트·유고 감지를 이어 갑니다.
- **유고 후보** — 그려 둔 차로 구역에 차량이 정지한 채 5초 넘게 머물면 영상 속 그 차의 박스가 빨갛게(`TRUCK #14 유고?`) 바뀌고 이벤트 로그 헤더에 `⚠ 유고 후보 1` 이 뜨며 로그에 행이 남습니다. 정지선·대기 구역 안의 정차와 줄지어 선 정체는 후보로 보지 않습니다.
- **구역 진입·나감 로그** — 그려 둔 구역에 물체가 들어오고 나갈 때마다 영상 아래 이벤트 로그에 한 줄(`00:11.2 CAR #40 Z1 차로 진입`)이 쌓이고, [CSV 저장]으로 영상 옆 `<영상>.events.csv` 에 남길 수 있습니다. 화면이 전환되면 구역이 맞는지 알 수 없어 이벤트를 멈추고, 시크하면 다시 시작합니다.
- **관심 구역** — 동영상을 멈춘 채 영상 위를 클릭해 구역(다각형)을 그리고, 점을 끌어 고치고, 차로·갓길·정지선·대기·무시 유형을 붙입니다. 구역은 영상 옆 `<영상 파일>.roi.json` 에 바로 저장되고 다시 열면 돌아옵니다. (아직 편집·표시만 합니다 — 구역을 쓰는 유고 감지는 앞으로 붙일 예정입니다.)
- **정밀 분석(SAHI)** — 멈춘 프레임 한 장을 640px 타일로 나눠 다시 검출하고, 전체 프레임 검출과 합칩니다. 멀리 있는 작은 물체를 찾을 때 씁니다.
- **추론 장치 선택** — 기본은 GPU(DirectML)이고, AMD Ryzen AI NPU(VitisAI 실행 공급자)로 바꿀 수 있습니다. NPU 는 양자화한 짝 모델 `best.npu.onnx` 가 있어야 하고, 처음 한 번 약 1분 동안 컴파일합니다.

## 요구 사항

| 항목 | 내용 |
|---|---|
| OS | Windows 11 x64 (개발·테스트 환경. Windows 10 은 확인하지 않았습니다) |
| GPU | DirectX 12 를 지원하는 GPU (DirectML) |
| 빌드 | [.NET 10 SDK](https://dotnet.microsoft.com/download) |
| NPU (선택) | AMD Ryzen AI NPU (XDNA 2). VitisAI 실행 공급자는 Windows ML 이 필요할 때 내려받습니다 |
| NPU 짝 모델 만들기 (선택) | [uv](https://docs.astral.sh/uv/) — Python 을 따로 설치하지 않고 `tools/npu_quantize.py` 를 돌립니다 |

개발 PC 는 NVIDIA GeForce RTX 5060 Laptop GPU 와 AMD 내장 GPU 가 함께 있는 하이브리드 노트북(Ryzen AI 5 340)입니다.

## 빌드와 실행

```bash
dotnet build
```

`bin\Debug\net10.0-windows10.0.19041.0\DetectView.exe` 가 만들어지고, 모델(`best.onnx`, `best.npu.onnx`)이 그 옆에 복사됩니다.

배포용 단일 실행 파일:

```bash
dotnet publish DetectView.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o publish
```

- 프로젝트 파일(`DetectView.csproj`)을 꼭 지정합니다. 빼면 폴더의 `.slnx` 가 잡혀 `NETSDK1194` 경고가 납니다.
- 결과는 `publish\DetectView.exe`(약 126 MB, .NET 런타임 포함)와 `best.onnx` 입니다. **모델은 exe 에 들어가지 않으니 같은 폴더에 둡니다.**
- NPU 짝 모델은 publish 에 복사되지 않습니다. 배포 폴더 모델로 만듭니다: `pwsh tools\Make-NpuModel.ps1 -Model publish\best.onnx`
- 실행해서 창 제목이 `DetectView — YOLO26s (2026-09-11 학습) — NVIDIA GeForce RTX 5060 Laptop GPU (DirectML)` 처럼 바뀌면 GPU 초기화에 성공한 것입니다.

## 사용법

- **왼쪽 패널**
  - 소스: [파일 열기] · 카메라를 고르고 [카메라 시작] · 모니터를 고르고 [화면 HUD]
  - 분석: [정밀 분석(SAHI)] — 지금 보이는 한 장을 분석합니다. 재생 중이면 멈춥니다
  - 구역: 동영상을 일시정지한 뒤 [추가] → 영상 위를 클릭해 점을 찍고, 첫 점을 누르거나 Enter 로 닫습니다(Esc: 그만두기, 오른쪽 클릭: 한 점 취소). 구역을 눌러 고른 뒤 점을 끌면 옮겨지고, 변 가운데 ○ 를 끌면 점이 늘고, 점을 고르고 Delete 를 누르면 점이 지워집니다. 구역을 고르면 유형을 바꾸거나 [지우기] 할 수 있습니다
  - 추론 장치: GPU / NPU
  - 맨 아래 [정지]
- **재생 줄** — 재생·일시정지, 위치 슬라이더(끌거나 눌러서 시크), 시간
- **상태바** — 상태 · 해상도 · FPS · 검출 수. 화면 HUD 중에 FPS 자리에 `대기 (화면 변화 없음)` 이 뜨면 화면이 그대로라 추론을 쉬는 중입니다.
- `DetectView.exe --npu` 로 시작하면 NPU 를 쓸 수 있는지 확인한 뒤 바로 NPU 로 바꿉니다.

**화면 HUD 가 `캡처 GDI (느림 — …)` 로 뜰 때**: 하이브리드 GPU 노트북에서는 프로세스가 어느 GPU 모드로 뜨느냐에 따라 화면 캡처(DXGI)가 안 될 수 있습니다.
상태바 안내대로 *설정 → 시스템 → 디스플레이 → 그래픽* 에서 `DetectView.exe` 를 '절전' 또는 '고성능'으로 지정하고 앱을 다시 시작하세요.
이 설정은 실행 파일 경로마다 따로라서, 자세한 단계와 지금 실행 중인 exe 경로는 상태바 툴팁에 있습니다.

## 모델

- 루트의 `best.onnx` 는 Ultralytics **YOLO26s** 를 파인튜닝한 모델입니다 (36 MB, 7클래스: person · car · motorcycle · truck · bus · scooter · bicycle).
  입력은 640×640 고정이고 출력은 `[1, 300, 6]` — NMS 가 모델 안에 들어 있습니다(end-to-end).
- 같은 형식의 다른 모델로 바꿔 쓸 수 있습니다(COCO 80클래스 YOLO26x 도 써 봤습니다). 창 제목의 모델 이름과 학습일은 ONNX 메타데이터에서 읽고, 화면에는 위 7개 클래스만 보입니다.
- `best.npu.onnx` 는 `best.onnx` 의 NPU 짝(A16W8 양자화)입니다. 원본을 바꾸면 `pwsh tools\Make-NpuModel.ps1` 로 다시 만듭니다.
  앱은 짝 모델에 들어 있는 원본 MD5 로 짝이 맞는지 확인하고, 안 맞으면 NPU 를 막습니다.
  보정 데이터 기본값은 `publish\test1~5.mp4` 이고, 다른 영상을 쓰려면 `-Calib` 로 영상·이미지 폴더를 줍니다.

## 성능 (참고)

개발 PC(RTX 5060 Laptop GPU · Ryzen AI 5 340)에서 YOLO26s 로 잰 값입니다.

| 항목 | 값 |
|---|---|
| 파일 재생 처리 (720p) | GPU 약 96~98 FPS · NPU 약 52 FPS |
| 화면 HUD (2560×1440, DXGI) | 약 104 FPS — 캡처+검출 한 번 10.6 ms |
| 화면이 안 바뀔 때 HUD 추론 | 0회/초 (대기) |

## 테스트

```bash
pwsh tests/ui/Run-All.ps1
```

- 실제 앱을 띄워 UI Automation 으로 조작하는 회귀 테스트 10종입니다: 파일 재생 · 추적 · 구역 편집(실제 마우스를 움직입니다) · 화면 전환 감지 · 구역 이벤트 로그 · 유고 규칙 · 화면 식별(합성 영상 — 처음 한 번 `TrackerCheck mkrevisit` 이 만듭니다) · VFR 재생 · 화면 HUD · NPU 전환. 종료 코드 0 이면 모두 통과입니다.
- 도는 동안 DetectView 창이 여러 번 떴다 닫히고 바탕화면을 캡처합니다. 몇 분 걸립니다.
- 필요한 것: GPU, 테스트 영상 `publish\test1.mp4` · `test4.mp4`(저장소에 없습니다). 가변 프레임레이트 표본은 `tests/ui/media` 에 들어 있습니다. NPU 검사는 NPU 와 짝 모델이 있을 때만 돕니다.
- 배포본 검사: `pwsh tests/ui/Run-All.ps1 -ExeDir publish`
- 재생 FPS 비교: `pwsh tests/ui/Measure-Fps.ps1 -ExeDir <폴더>`
- 추적기 검증(영상·GPU 불필요): `dotnet run --project experiments/TrackerCheck/TrackerCheck.csproj -c Release -- selftest`

## 폴더 구조

| 경로 | 내용 |
|---|---|
| 루트 `*.cs` · `*.xaml` | 앱. `MainWindow` 는 기능별 partial 파일(`File` · `Camera` · `Playback` · `Screen` · `Inference` · `Npu` · `Sahi`)로 나뉩니다 |
| `tests/ui/` | UI 자동화 테스트 |
| `tools/` | NPU 짝 모델 만들기 |
| `experiments/` | 따로 빌드하는 실험 도구 — `NpuBench`(NPU 정확도·속도), `RuntimeParity`(런타임 교체 전후 검출 비교), `TrackerCheck`(추적기 검증) |
| `CLAUDE.md` | 설계 결정 · 함정 · 꼭 지킬 규칙. 코드를 고치기 전에 읽을 것 |
| `docs/` | 기능별 자세한 설명 · 실측 · 지난 테스트 기록 (CLAUDE.md 에서 옮김) |

폴더 · 프로젝트 · 네임스페이스 · 제품 · 실행 파일 이름이 모두 `DetectView` 입니다 (2026-11-03 정리 — 예전엔 폴더·프로젝트·네임스페이스가 옛 이름 `YOLO11_Test` 였습니다).

## 개발 메모

- ONNX Runtime 은 Windows ML(`Microsoft.Windows.AI.MachineLearning`) 것 하나만 씁니다. `Microsoft.ML.OnnxRuntime.*` 같은 다른 ONNX Runtime 패키지를 넣으면 이름이 겹쳐 로드되지 않습니다.
- OpenCvSharp 의 관리·네이티브 두 패키지는 같은 판이어야 합니다. 판은 `Directory.Build.props` 한 곳에서만 바꿉니다.
- 화면 모양(색 · 글꼴 · 박스 · 상태바 칸 폭)은 `App.xaml` 한 곳에 있습니다.
- 버그와 개선 거리는 GitHub 이슈로 관리합니다.

## 라이선스

[GNU AGPL-3.0](LICENSE) — Copyright (C) 2026 chisu0208

- 저장소에 든 모델(`best.onnx` · `best.npu.onnx`)은 Ultralytics YOLO26s 를 파인튜닝한 것입니다. Ultralytics 는 YOLO 로 학습한 모델과
  그 모델을 쓰는 프로젝트에 AGPL-3.0 을 요구해서([Ultralytics Licensing](https://www.ultralytics.com/license)) 프로젝트 전체를 AGPL-3.0 으로 공개합니다.
- 고친 판을 배포하거나 네트워크 서비스로 제공하면 그 소스도 AGPL-3.0 으로 공개해야 합니다.
- 저장소에 들어 있는 다른 사람의 코드(`WindowsMlExecutionProvider.cs` 의 바탕인 YoloDotNet, graft 연동 파일)와 그 라이선스 전문은
  [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) 에 있습니다.

빌드할 때 NuGet 으로 받는 외부 구성요소는 다음과 같습니다(저장소에는 들어 있지 않습니다). 빌드한 실행 파일을 배포할 땐 이 구성요소들의 고지도 함께 넣어야 합니다.

| 구성요소 | 판 | 라이선스 |
|---|---|---|
| [YoloDotNet](https://github.com/NickSwardh/YoloDotNet) | 4.2.0 | MIT |
| [OpenCvSharp4](https://github.com/shimat/opencvsharp) · runtime.win | 4.13.0.20260302 | Apache-2.0 |
| SkiaSharp | 3.119.2 | MIT |
| Vortice.DXGI · Vortice.Direct3D11 | 3.8.3 | MIT |
| [DirectShowLib.Standard](https://github.com/luthfiampas/DirectShowLib) | 2.1.0 | LGPL-2.1 |
| [Windows ML](https://github.com/microsoft/windowsml) (`Microsoft.Windows.AI.MachineLearning`) | 2.4.89 | Microsoft 소프트웨어 사용 조건 |
