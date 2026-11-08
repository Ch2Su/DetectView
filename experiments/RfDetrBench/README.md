# RfDetrBench — RF-DETR 를 앱의 런타임(DirectML)에서 돌려 보기

RF-DETR(Roboflow, Nano·Small·Medium·Large 는 Apache 2.0)로 YOLO26s 를 바꾸거나 모델 종류로 더할 만한지 보는 실험이다. 2단계(같은 데이터로 학습해 시험 분할에서 비교, 2026-11-04~05 · RF-DETR-N 은 11-06)는 아래 '2단계' 절,
YOLO 쪽 남은 여지(NMS 를 넣어 내보내기 · 16:9 영상에 640×384 입력 — 16:9 이미지에서의 RF-DETR-S 비교 포함, 11-05)는 'YOLO 남은 여지' 절,
**3단계**(11-06 — 사용자가 YOLO26m 640×384 를 골라 앱의 루트 모델로 넣음)는 맨 끝 '3단계' 절.
**1단계**(2026-11-04)는 학습 없이 COCO 사전학습 가중치로, 앱과 같은 런타임 — Windows ML 의 ONNX Runtime 1.27 + DirectML(NVIDIA) — 에서

1. **모든 노드가 GPU 에 올라가는지** — RF-DETR 디코더의 변형 어텐션(MSDeformAttn)은 GridSample 을 쓴다. DirectML EP 는 GridSample 을 지원(5D 만 빠짐)하지만 실제 배치는 프로파일로 본다
2. **얼마나 빠른지** — `session.Run` 만, 앱의 YOLO26s(루트 `best.onnx`)와 같은 세션 설정·같은 GPU 에서
3. **결과가 맞는지** — (a) 같은 입력 텐서로 DirectML 출력 vs 파이썬 ONNX Runtime(CPU) 출력, (b) C# 전처리·후처리로 낸 검출 vs rfdetr 의 PyTorch `predict()`

를 잰다. 1단계를 넘길지는 Small·Medium 의 `session.Run` 중앙값으로 정했다 — **12 ms 안이면 다음 단계(같은 데이터셋으로 파인튜닝해 YOLO26s 와 비교)**,
25 ms 를 넘으면 접는다 (60fps 영상은 프레임당 16.7 ms 안에 디코딩·전처리까지 끝나야 하고, HUD 는 FPS 의 역수가 곧 박스 지연이다).

## 실행

```powershell
# 1) 내보내기 + 파이썬 기준값 (저장소 루트에서. 약 1분 — 처음엔 PyTorch(CPU)·rfdetr 를 uv 캐시에, 가중치를 %USERPROFILE%\.roboflow\models 에 받는다)
uv run --no-project --python 3.12 --with "rfdetr[onnx]==1.11.2" --with opencv-python `
    experiments/RfDetrBench/export_rfdetr.py --dataset "\\wsl.localhost\Ubuntu\home\chisu\datasets_backup\visible_2026-11-04"

# 2) DirectML 실측 (회차 2 — 회차마다 ② 를 다시 잰다)
dotnet run --project experiments\RfDetrBench -c Release -- 2
```

`--dataset` 은 YOLO 형식 데이터셋 폴더이고 `images/test` 를 **읽기만** 한다(12장을 골라 무손실 PNG 로 `out/samples` 에 옮긴다). 주지 않으면 시험 영상 프레임 6장만 쓴다.
시험 영상은 이 폴더에서 위로 올라가며 처음 만나는 `publish/test1.mp4` 의 폴더에서 읽는다 — worktree 에서 돌려도 본 checkout 의 `publish` 를 찾는다.

산출물은 `out/`(gitignore, 약 0.5 GB): `models/rfdetr-<크기>.onnx`(108~121 MB — YOLO26s `best.onnx` 는 36 MB) · `samples/*.png` · `ref/<크기>/*.f32`(입력 텐서·CPU 출력) ·
`ref/manifest.json`(PyTorch 검출·클래스 이름·연산 개수) · `prof_*.json`(노드 배치 프로파일) · `export.log`.
사전학습 가중치는 rfdetr 가 `%USERPROFILE%\.roboflow\models` 에 받는다(학습 체크포인트 그대로라 크기당 350~390 MB, 셋이 1.1 GB — 저장소 밖이다. 필요 없으면 지워도 다시 받는다).

## 데이터셋 백업 (2026-11-04)

라벨링 도구(`E:\local_auto_labeling`)의 'WSL 학습' 버튼은 DB 로 데이터셋을 새로 만들어 WSL `~/datasets/visible` 의 `images/`·`labels/` 를 `rsync --delete` 로 덮어쓴다.
그 전 상태(2026-09-18 동기화본, 학습 4,708 · 검증 854 · 시험 973장, 7클래스)를 **`/home/chisu/datasets_backup/visible_2026-11-04`** 에 그대로 복사해 두었다
(파일 13,074개 · 4.2 GB, 원본에서 만든 SHA-256 `../visible_2026-11-04.SHA256SUMS` 와 전부 일치, 옆에 `README.txt`). 이 실험과 다음 단계(파인튜닝)는 이 백업을 쓴다.
**함정**: 백업 안의 `data.yaml` 은 `path: /home/chisu/datasets/visible`(원본 경로) 그대로라, 그대로 학습에 넘기면 원본 경로의 — 덮어써졌을 수 있는 — 이미지를 읽는다.
백업으로 학습할 땐 `path` 를 백업으로 바꾼 `data.yaml` 을 백업 밖에 만들어 쓴다(백업 안은 고치지 않는다).

## 전처리·후처리 (앱에 넣을 때 그대로 쓸 것)

- **전처리**: BGR → RGB → 0~1 float → **정사각형으로 그냥 늘이기**(레터박스 없음, 쌍선형·반 화소 중심·antialias 없음) → ImageNet 정규화(평균 0.485/0.456/0.406, 표준편차 0.229/0.224/0.225) → NCHW.
  rfdetr 의 ONNX 추론(`export/_runtime/preprocess.py`)과 `predict()` 가 이렇게 한다. OpenCV `INTER_LINEAR` 를 **float 에서** 하면 공식과 같고(0~1 값 차이 최대 8e-5),
  uint8 에서 먼저 줄이면 고정소수점 보간이라 최대 0.003(약 0.8 단계) 다르다
- **출력은 이름으로 고른다** — `dets` [1,300,4](정규화 cxcywh) · `labels` [1,300,클래스+1](로짓). 클래스가 3개면 로짓 폭이 박스 폭(4)과 같아 모양으로 고르면 뒤바뀐다
- **후처리**: 클래스마다 시그모이드(±88 로 자름) → 쿼리×클래스 쌍 전체에서 상위 300 → 임계값 → 화소 xyxy(이미지 안으로 자름). NMS 없음
- **배경 칸**: COCO 사전학습 가중치는 배경 칸 없이 희소 COCO 번호(1 = person, 3 = car, 8 = truck …, 마지막 칸 = 90번 클래스)다. 파인튜닝한 모델은 마지막 칸이 배경이라 뺀다(rfdetr 의 `background_class_id=-1`)
- rfdetr 1.11.2 는 `__version__` 이 없다(버전은 `importlib.metadata`). `export()` 는 기본이 `verbose=True` 라 그래프 전체(수 MB)를 찍고,
  노드마다 소스 위치 문자열이 들어가 ONNX 도 약 8 MB 커진다(가중치·검출은 같다) — 스크립트는 `verbose=False`

### 3단계 준비 — 전처리 비용과 DirectML 결정성 (2026-11-05)

앱에 넣기 전에 걸리던 두 가지를 쟀다: 1단계의 float 전처리가 비싸다는 것(아래 ③ — 720p 한 장 CPU 5~7 ms), 그리고 HUD 의 '새 화면 고리'가 기대는
"같은 입력 → 같은 출력"(CLAUDE.md '화면 HUD')이 RF-DETR·DirectML 에서도 성립하는지. 학습한 S(2단계)와 COCO N 으로 쟀다(N 은 아직 학습 전).

```powershell
# GPU 전처리 짝 모델(*-gpuprep.onnx) 만들기 + 파이썬 ORT(CPU)로 공식 전처리와 같은지 확인 — 약 1분
uv run --no-project --python 3.12 --with onnx --with onnxruntime --with opencv-python --with numpy experiments/RfDetrBench/prep_graph.py
# 재기 — test4·test5 각 60장(publish 는 위로 올라가며 찾는다) + 시험 분할 12장, 약 1분
dotnet run --project experiments\RfDetrBench -c Release -- pre 60
```

| 후보 | 방법 (`PreBench.cs`) |
|---|---|
| A 공식 | BGR → RGB → float(÷255) → 쌍선형 → (x − mean)/std → CHW — 1단계의 `Prep.RfDetr`, PyTorch 와 100% 같음 |
| B uint8 | uint8 에서 쌍선형 뒤 float (1단계) |
| C1 | A 와 같은 OpenCV 단계를 버퍼를 다시 쓰며 하고, RGB 바꾸기·정규화·CHW 를 한 번에(나누기 대신 곱하기·더하기) |
| C2 | **원본 uint8 에서 바로 쌍선형** — OpenCV float `INTER_LINEAR` 와 같은 탭·가중치(반 화소 중심 · 가장자리는 끝 화소 · 가로 먼저)로 보간하며 정규화·CHW 를 한 번에, 행마다 병렬. 전체 프레임을 float 로 바꾸지 않는다 |
| D | 전처리를 ONNX 앞에 붙여 GPU 에서 (`prep_graph.py`): 입력 uint8 [1,H,W,C] BGR·BGRA → Cast → Gather(2,1,0) → Transpose → Resize(linear · half_pixel) → Mul → Add. 프레임 메모리를 복사 없이 넘기고 H·W·C 는 세션을 만들 때 free dimension override 로 고정 |

`prep_graph.py` 의 CPU 확인: 붙인 전처리의 값 차는 공식 전처리 대비 최대 2.6e-4(밝기 한 칸 ≈ 0.017), 검출(임계 0.3) 짝 S 81/81 · N 55/55.

**결과** (RTX 5060 Laptop · 전원 연결 · Windows ML ORT 1.27.1)

| 전처리 (512×512 입력 하나) | 영상 프레임 120장 중앙 · p95 ms | HUD 크기 2560×1440 | 입력 값 차 (A 기준) | 검출 맞춤 (A 기준 — 학습한 S FP32 · DirectML · 132장 1,545개) |
|---|---|---|---|---|
| A 공식 | 3.86 · 4.86 | 9.03 | — | — |
| B uint8 | 2.64 · 3.70 | 2.60 | 0.0132 | 99.0% · 신뢰도 차 최대 0.104 |
| C1 | 1.02 · 1.47 | 2.84 | 4.8e-7 | 100% · 0.0017 |
| C2 1스레드 | 1.07 · 2.87 | 1.05 | 1.2e-6 | (병렬과 비트 단위로 같음) |
| **C2 병렬** | **0.29 · 0.54** | **0.25** | 1.2e-6 | **100% · 0.0065** |
| D (GPU) | 0 (CPU 일 없음) | 0 | — | 100% · 0.0065 |

| 프레임당 (test4 60장 1280×720, ms 중앙) | A: 전처리 · Run · 후처리 = 합계 | C2 | D |
|---|---|---|---|
| S FP32 (학습) | 3.75 · 15.56 · 0.01 = 19.31 | 0.47 · 15.23 · 0.01 = 15.74 | 0 · 15.42 · 0.01 = 15.44 |
| S FP16 (학습) | 4.30 · 8.81 · 0.02 = 13.10 | 0.47 · 8.61 · 0.02 = 9.24 | 0 · 8.23 · 0.02 = 8.26 |
| N FP16 (COCO) | 3.65 · 5.24 · 0.13 = 9.12 | 0.38 · 4.95 · 0.12 = 5.53 | 0 · 5.12 · 0.19 = 5.36 |

- **전처리 비용은 풀렸다 — C2**: 0.29 ms(공식 경로의 13분의 1)이고 검출은 공식 경로와 100% 같다. 비용이 입력이 아니라 출력 크기(512²)에 비례해 HUD 크기도 0.25 ms 다.
  모델을 바꾸지 않고 세션 하나로 어느 크기든 받는다. A 가 1단계(5.1~7.1 ms)보다 낮게 나온 건 측정 조건 차이로 보인다(그땐 18장 평균, 이번엔 영상 120장 중앙값)
- **D 도 된다**: 모든 노드가 DirectML 에 올라가고(GridSample 3개 + 융합 덩어리 6개, CPU 노드 없음) CPU 비용이 0 이며 session.Run 도 늘지 않는다
  (720p uint8 2.7 MB 를 올리는 양이 float 입력 3.1 MB 와 비슷하다). 대신 세션을 프레임 크기마다 고정해야 하고(크기를 정하지 않으면 DirectML 이 그래프를 묶지 못한다),
  HUD 크기 BGRA(11 MB)는 S FP16 10.32 · N FP16 7.40 ms 로 720p 보다 2.1~2.3 ms 늘어 C2 보다 느리다 → **앱엔 C2** 가 낫다
- **DirectML 출력은 결정적이다**: S FP32 · S FP16 · N FP16 을 A 입력으로, 또 D 로 — 여섯 가지 모두 같은 입력 30회와 새 세션까지 박스·로짓이 비트 단위로 같았다.
  HUD 의 '새 화면 고리'(같은 결과면 다시 그리지 않음)가 RF-DETR 에서도 성립한다
- 프레임당 합계(C2): S FP16 9.2 ms · N FP16 5.5 ms — 앱 YOLO26s 의 session.Run 6.4 ms(1단계)와 견줄 값. 학습한 N 은 로짓 폭이 8(COCO 91)이라 후처리가 더 싸다
- FP16 모델로 세션을 만들 때 ORT 가 "constant folding 할 CPU 커널이 없다" 경고를 수십 줄 찍는다 — 결과와 상관없다
- 확인 안 한 것: C2 의 SIMD 화(지금 스칼라 + 행 병렬), 앱의 재생 루프·UI 스레드와 병렬 전처리가 겹칠 때의 흔들림, 학습한 N

## 추가 비교 (`variants.py`)

```powershell
# export_rfdetr.py 뒤에. RF-DETR FP16 · 앱 YOLO26s FP16 · YOLO26 COCO n/s/m 을 out/models 에 만들고 out/ref/variants.json 에 적는다 — 벤치가 읽어 더한다
uv run --no-project --python 3.12 --with "ultralytics==8.4.31" --with onnx --with onnxslim --with onnxruntime `
    experiments/RfDetrBench/variants.py --yolo-weights E:\local_auto_labeling\weights
```

COCO 정확도가 비슷한 것끼리 같은 GPU 에서 맞대려고 넣었다 — Roboflow 표에서 RF-DETR-S(53.0)가 YOLO26m 과 같은 급이다. `.pt` 는 `out/` 으로 복사해서 내보낸다(Ultralytics 는 `.pt` 옆에 ONNX 를 쓴다).

- **Ultralytics 8.4.142 의 `export(format="onnx")` 기본값은 end-to-end 가 아니다** — 출력 `[1,84,8400]`, 메타데이터 `end2end False`. `nms=False`(또는 `end2end=True`)를 줘야 앱과 같은 `[1,300,6]` 이 나온다.
  앱의 `best.onnx` 를 내보낸 8.4.31 은 기본값이 end-to-end 였다(메타데이터 `args` 에 `nms: False`). 그래서 여기선 8.4.31 로 고정했다
- **FP16 변환**: onnxconverter-common 의 `convert_float_to_float16(keep_io_types=True)` 는 RF-DETR(패치 임베딩 Conv)·YOLO26(Resize 출력 Cast) 모두 ORT 가 열지 못하는 그래프를 냈다
  (`value_info` 를 지우거나 Resize 를 막아도 다른 Cast 에서 같은 오류). onnxruntime.transformers 의 `OnnxModel.convert_float_to_float16(keep_io_types=True, use_symbolic_shape_infer=True)` 는
  RF-DETR 를 제대로 바꾼다. YOLO26 은 이것도 DirectML 그래프 융합에서 `80070057 매개 변수가 틀립니다`(DmlGraphFusionHelper.cpp 614)로 세션을 못 만든다 — **YOLO FP16 은 재지 못했다**

## 결과 (2026-11-04, RTX 5060 Laptop · Windows ML ORT 1.27.1 · DirectML)

**조건**: 처음엔 노트북이 **배터리**(균형 조정 전원 구성, 잔량 67 → 59%)라 YOLO26s 가 8.2~8.9 ms 로 나왔다(10-24 NpuBench 는 6.1~6.5 ms).
같은 날 **전원을 연결하고 다시 재니** 6.4~6.5 ms 로 NpuBench 와 같았다 — 모든 모델이 25% 안팎 빨라졌고 모델끼리의 비율은 거의 같다. 아래 표의 기준은 전원 연결 값이다.
배터리 측정 중 한 회차는 PC 가 **20분 동안 입력이 없어 유휴 절전(Modern Standby)에 들어가** 16분 넘게 멈춰서 뺐다 — 시스템 로그 Kernel-Power 506 '이유: Idle Timeout'(17:21).
GPU 를 쓰고 있어도 사람 입력이 없으면 Windows 는 유휴로 본다(배터리일 때 유휴 절전 20분, 전원 연결 땐 꺼져 있음). 깨어난 이유는 'Lid'(18:01)였지만 잠든 이유는 덮개가 아니다.

### ② 추론 시간 — `session.Run` 중앙값 (입력 복사·출력 읽기 포함, 전처리·후처리 제외. 회차 두 번)

| 모델 | 입력 | COCO mAP (발표) | 전원 연결 ms | 배터리 ms | 앱 YOLO26s 대비 (전원) |
|---|---|---|---|---|---|
| YOLO26n COCO | 640 레터박스 | 40.1 | 3.89 · 3.98 | 5.26 · 5.13 | 0.6배 |
| **YOLO26s 앱** (7클래스, 루트 `best.onnx`) | 640 레터박스 | — | **6.48 · 6.42** | 8.20 · 8.29 | 1 |
| YOLO26s COCO | 640 레터박스 | 47.8 | 6.54 · 6.86 | 8.82 · 8.86 | 1.0배 |
| YOLO26m COCO | 640 레터박스 | 52.5 | 13.95 · 14.22 | 18.04 · 18.34 | 2.2배 |
| RF-DETR Nano | 384 정사각형 | 48.4 | 9.70 · 9.93 | 11.86 · 12.02 | 1.5배 |
| RF-DETR Small | 512 정사각형 | 53.0 | 16.78 · 16.85 | 24.37¹ · 20.84 | 2.6배 |
| RF-DETR Medium | 576 정사각형 | 54.7 | 21.70 · 21.88 | 27.15 · 27.10 | 3.4배 |
| RF-DETR Nano FP16 | 384 | | 5.16 · 5.82 | 6.33 · 6.24 | 0.8~0.9배 |
| RF-DETR Small FP16 | 512 | | 9.11 · 9.24 | 10.97 · 10.77 | 1.4배 |
| RF-DETR Medium FP16 | 576 | | 12.27 · 12.21 | 15.84 · 16.13 | 1.9배 |

¹ 그 회차만 세션 생성 3.5초·p95 42.7 ms 로 다른 부하가 끼었다(앞선 두 실행은 21.2~21.4). COCO mAP 는 RF-DETR README 와 Ultralytics YOLO26 문서의 end-to-end 열(앱이 end-to-end 출력을 쓴다).

- **같은 COCO 정확도끼리 보면 RF-DETR 가 20~50% 느리다**(전원 연결 — 배터리에선 15~35%): RF-DETR-N(48.4) 9.7~9.9 ms vs YOLO26s(47.8) 6.5~6.9 ms = 약 1.5배,
  RF-DETR-S(53.0) 16.8 ms vs YOLO26m(52.5) 14.0~14.2 ms = 1.2배. T4·TensorRT 발표값(N 2.3 vs YOLO26s 2.5 · S 3.5 vs YOLO26m 4.7 ms)과 순서가 뒤집힌다 — DirectML FP32 에선 트랜스포머 쪽이 덜 빠르다
- **FP16 이면 RF-DETR 가 1.7~1.9배 빨라진다**(N 5.2~5.8 · S 9.1~9.2 · M 12.2~12.3 ms). YOLO 의 FP16 은 못 재서(위) FP16 끼리의 비교는 없다
- 1단계 기준(12 ms 안 통과 · 25 ms 초과 접기, 전원 연결 값): FP32 는 N 통과(9.7) · S 애매(16.8) · M 애매(21.7), **FP16 은 N·S 통과**(5.2 · 9.1) · M 애매(12.2)

### ① 노드 배치

RF-DETR 는 세 크기 모두 **CPU 로 떨어진 노드가 없다**(전부 DmlExecutionProvider). GridSample 은 DirectML 의 융합 그래프에 들어가지 못하고 따로 돈다 — 디코더 층마다 하나(N 2 · S 3 · M 4개)라
그래프가 융합 덩어리 4 · 6 · 8개로 쪼개진다(YOLO26 은 2개). YOLO26 은 모양 계산 `Mod` 노드 하나가 CPU 다(0.04~0.08 ms — ORT 가 일부러 CPU 에 둔다).

### ③ 결과가 맞는지 (18장: 시험 영상 프레임 6 + 데이터셋 백업의 시험 분할 12, 임계 0.3)

- 내보낸 ONNX(파이썬 CPU) = PyTorch `predict()`: 191/191 · 209/209 · 218/218, 신뢰도 차 최대 0.0002 (N · S · M 순)
- **같은 입력 텐서 → DirectML vs CPU**: 검출 100% 일치(신뢰도 차 ≤ 0.0002). 원출력을 쿼리 번호별로 견주면 N·S 에서 박스가 0.31~0.36(정규화) 다른 쿼리가 있지만,
  순서를 무시하고 가장 가까운 박스와 견주면 0.086 · 0.008 로 준다 — 인코더가 고르는 상위 300 제안(TopK)의 순서가 조금 바뀐 것이고 임계값을 넘는 검출엔 영향이 없다. 원출력은 번호별로 견주지 말 것
- **C# 전체 경로(OpenCV float 리사이즈 → DirectML → C# 후처리) vs PyTorch**: 100% (신뢰도 차 ≤ 0.0003). **uint8 에서 리사이즈하면 99.5 · 99.5 · 98.2%** —
  임계값 근처 검출이 1~2% 바뀌고 신뢰도가 최대 0.05~0.10 흔들린다 → float 경로(또는 그만큼 정밀한 경로)를 쓴다
- FP16 vs PyTorch FP32: 99.5 · 99.5 · 98.2% (신뢰도 차 최대 0.04~0.09)
- **C# 전처리가 싸지 않다**: float 경로가 720p 영상 프레임 한 장에 5.1~7.1 ms(CPU), uint8 경로 2.5~4.1 ms, 후처리 0.2~0.3 ms. 앱에 넣으려면 전처리를 줄여야 한다
  (uint8 로 중간 크기까지 줄인 뒤 float, 16비트 리사이즈, 리사이즈를 모델 그래프에 넣어 GPU 에서 등) — 11-05 풀었다: 위 '3단계 준비'(원본에서 바로 쌍선형 0.29 ms · 검출 100% 같음)

### 판단

- 앱의 런타임에서 **돌아가고 결과가 맞다** — 막힌 것은 없다(전처리는 레터박스가 아니라 정사각형 늘이기라 앱의 YoloDotNet 경로와 따로 써야 한다)
- 같은 COCO 정확도의 YOLO26 보다 FP32 로 20~50% 느리다 → 바꿀 이유는 **우리 데이터로 파인튜닝했을 때의 정확도**에서 나와야 한다 (2단계)
- 2단계 후보는 **RF-DETR-S**(FP16 9.1 ms). 비교 상대는 앱의 YOLO26s 와 같은 정확도 급인 **YOLO26m** — 셋 다 같은 데이터셋 백업으로 학습해야 공정하다(배포 YOLO26s 는 09-17 에 조금 다른 판으로 학습)
- 확인 안 한 것: NPU(VitisAI — FP32 는 이 PC 에서 BF16 변환이 실패하는 경로(NpuBench README 함정 1)라 시도하지 않았고, 양자화한 트랜스포머도 안 해 봤다) ·
  같은 입력에서 DirectML 출력이 매번 같은지(HUD 의 '새 화면 고리'가 기대는 성질 — CLAUDE.md '화면 HUD' — 11-05 확인: 비트 단위로 같다, 위 '3단계 준비') · YOLO FP16 · Large(704)

## 2단계 — 같은 데이터로 학습해 시험 분할에서 비교 (2026-11-04~05)

RF-DETR-S · YOLO26s · YOLO26m 을 데이터셋 백업(위)으로 **각자의 기본 조리법대로 100 epoch** 학습하고, 앱과 같은 전처리의 ONNX 로 시험 분할 973장을
같은 평가기(pycocotools)로 견준다. 배포 YOLO26s(09-17 학습 — 조금 다른 판의 데이터)도 기준으로 같이 잰다. 학습은 WSL(Ubuntu · RTX 5060 Laptop 8 GB), 내보내기·평가는 Windows 에서 한다.

| 모델 | 학습 환경 | 조리법 |
|---|---|---|
| RF-DETR-S | WSL `~/rfdetr_work/.venv` — rfdetr 1.11.2 · torch 2.11.0+cu130 | rfdetr 기본값: COCO 사전학습에서 · lr 1e-4(인코더 1.5e-4) · EMA · 배치마다 다른 입력 크기 352~672(평가 512) · bf16. 실제 배치 4 × 누적 4 = 실효 16 |
| YOLO26s · YOLO26m | 라벨링 도구의 학습 환경(`yolo_final` — Ultralytics 8.4.142)과 `train_wsl.py` 를 **고치지 않고 실행만** | 웹 UI 'WSL 학습' 버튼과 같은 인자(cos_lr · box 7.5 · cls 1.5 · dfl 1.5 · HSV · 좌우 뒤집기)를 100 epoch 로(close_mosaic 은 끝 10% 라 10) · 640 · 배치 s 20 · m 10 · 이미지 RAM 캐시 |

```powershell
# 학습 — 몇 시간 걸려서 숨은 창으로 띄운다(띄운 쪽이 닫혀도 이어진다. 도는 동안 유휴 절전을 막는다). 이름은 rfdetr_s · y26s · y26m · rfdetr_n 중 고른다
Start-Process pwsh -WindowStyle Hidden -ArgumentList '-NoProfile','-File','experiments\RfDetrBench\step2\Start-Step2.ps1','rfdetr_s','y26s','y26m'
```

- 진행은 WSL `~/rfdetr_work/runs/logs/`(`driver.log` 에 시작·끝, 모델마다 `<이름>.log`), 결과는 `~/rfdetr_work/runs/{rfdetr_s,yolo/<이름>}`.
  `touch ~/rfdetr_work/runs/HALT` 면 지금 것이 끝난 뒤 멈추고, `touch ~/rfdetr_work/runs/yolo/STOP` 은 도는 YOLO 학습을 정상 중단한다(`train_wsl.py` 의 정지 파일)
- 데이터는 백업의 작업 사본 `~/rfdetr_work/data/visible`(`data.yaml` 의 `path` 를 이 폴더로 바꿈) — `train_rfdetr.py` 가 path 가 이 폴더인지 확인한다
- YOLO 결과를 라벨링 도구의 `runs_wsl` 에 두지 않는다 — 거기 있으면 웹 UI 'WSL 이어하기' 가 집어 가 운영 가중치(`models/best.pt`)를 바꿀 수 있다.
  `YOLO_AUTOINSTALL=false` 로 Ultralytics 가 그 환경에 패키지를 깔지 못하게 한다
- 이 WSL 에서 만난 함정 (모두 고침):
  - **rfdetr 학습 매처(헝가리안)가 Triton 커널을 쓰는데 Triton 이 그 자리에서 C 모듈을 컴파일하려고 C 컴파일러를 찾는다** — gcc 가 없어(sudo 없이 못 깖) 첫 배치에서 멈췄다.
    `train_rfdetr.py` 가 컴파일러가 없으면 그 라이브러리의 SciPy(CPU) 경로로 보낸다(DETR 원래 방식, 같은 최적 짝). RF-DETR-S 와 11-05 21:21 의 RF-DETR-N 은 이 경로로 학습했다.
    11-05 밤 사용자가 WSL 에 gcc 13.3 을 깔아 이제는 Triton 경로로 간다(첫 호출 컴파일 9.4초) — 빨라지는 몫은 아래 RF-DETR-N 항목의 매처 실측
  - **`batch_size='auto'` 가 WSL 의 CUDA 를 죽인다** — 배치를 2, 4, 8… 로 키우며 메모리가 넘칠 때까지 시험하는데, 한계에서 메모리 부족 대신 `CUDA error: device not ready` 가 나고
    컨텍스트가 망가져 뒤이은 `empty_cache` 도 out of memory 로 죽었다(첫 시도, 3분). rfdetr 의 'auto' 가 맞추는 실효 배치 16 을 고정 4 × 누적 4 로 준다
  - 런처가 띄운 `wsl.exe` 는 Windows 의 현재 폴더(저장소)에서 시작해서, Ultralytics 의 AMP 점검이 `yolo26n.pt` 를 저장소의 `weights/` 에 내려받았다 → 드라이버가 `~/rfdetr_work` 로 옮겨 간다
  - Windows 런처는 `wsl.exe` 출력을 드라이버가 끝날 때에야 받는다(`out/step2_driver.log`) — 도는 동안의 진행은 WSL 쪽 `driver.log` 를 본다
- 드라이버는 한 모델이 실패해도 다음 모델로 넘어간다(밤새 돌 때 하나 때문에 다 서지 않게)
- **RF-DETR-N**(`rfdetr_n` = `train_rfdetr.py --size n`, 2단계 뒤에 더함)은 같은 조리법(배치 4 × 누적 4)에서 epoch 당 약 2.6분(검증 포함 157~161초)이다.
  S 는 3.5분이었다. 100 epoch 이면 약 4.4시간. GPU 사용률이 45~50%(메모리 3.4 GB)라 처음엔 'CPU 매칭이 병목'이라고 적었는데 **재 보니 매칭은 작은 몫이다**
  (11-05 — 학습 라벨의 객체 수로 한 스텝의 문제 156개 = (마지막 층 + 보조 1 + 인코더) × 4장 × 13그룹을 `_solve_to_indices` 와 같은 꼴 [156, 300, K] 로 풀었다.
  K 는 배치 4장 중 가장 많은 객체 수 — 중앙 10 · p90 19 · p99 38): SciPy 9.4~14 ms · Triton 1.0~1.8 ms, 고른 짝의 총비용은 같다.
  학습 스텝(약 120 ms)의 7% 안팎이라 Triton 으로 가도 epoch 이 6% 쯤 줄 뿐이다. 남은 GPU 유휴가 어디서 오는지는 재지 않았다.
  11-05 10:48 에 처음 시작했다가 사용자 요청으로 첫 epoch(검증 mAP50-95 0.564) 뒤 멈췄다(Ctrl+C — Lightning 이 정리하고 끝냈다).
  그 결과는 `~/rfdetr_work/runs/rfdetr_n_stopped_1105`(로그 `logs/rfdetr_n_stopped_1105.log`)로 옮겨 두었다. 다시 할 땐 위 명령에 `rfdetr_n` 을 주고 처음부터 돌린다.
  같은 날 21:21 에 처음부터 다시 돌렸다(SciPy 매처 — gcc 는 그 뒤에 깔았다). 같은 시드·조리법인데 첫 epoch 검증이 0.596 으로 멈춘 실행(0.564)과 달랐다 — GPU 학습이 결정적이지 않다.
  **11-06 04:00 에 끝났다 — 6.65시간**(제 속도인 epoch 2.5~2.7분이면 4.2~4.5시간). 11-06 00:05 무렵부터 epoch 이 7.6분까지 늘었는데, 그동안 Windows CPU 가 100%(반디집 압축)였고
  GPU 사용률은 6~34%, 학습 프로세스의 CPU 스레드는 75~92% 였다 — CPU 를 빼앗겨 학습의 CPU 쪽 일(데이터 준비·매칭·커널 실행)이 밀린 것으로 본다(추정). 속도만의 일이고 계산은 같다. 결과는 아래 '결과' 절
  이어서 하려면 `train_rfdetr.py --size n --resume <그 폴더>/last.ckpt` 를 직접 실행한다(rfdetr 1.11.2 가 Lightning `Trainer.fit(ckpt_path=...)` 로 넘기고 `metrics.csv` 기록도 이어 쓴다)

```powershell
# 내보내기 (Windows, CPU) — out/step2_models/<이름>.onnx, RF-DETR 는 <이름>-fp16.onnx 도
uv run --no-project --python 3.12 --with "rfdetr[onnx]==1.11.2" --with onnxruntime experiments/RfDetrBench/step2/export_trained.py rfdetr "\\wsl.localhost\Ubuntu\home\chisu\rfdetr_work\runs\rfdetr_s\checkpoint_best_total.pth" rfdetr_s
uv run --no-project --python 3.12 --with "ultralytics==8.4.142" --with onnx --with onnxslim --with onnxruntime experiments/RfDetrBench/step2/export_trained.py yolo "\\wsl.localhost\Ubuntu\home\chisu\rfdetr_work\runs\yolo\y26s\weights\best.pt" y26s
# (y26m 도 같게 — runs\yolo\y26m\weights\best.pt · rfdetr_n 은 첫 줄의 rfdetr_s 를 경로·이름 모두 rfdetr_n 으로)
# 평가 — 사용법은 eval_test.py 머리말 (모델을 나눠 돌려도 out/eval/results.json 에 쌓인다. --yolo-resize linear 는 YOLO 를 쌍선형으로 줄인다,
#        --ep dml 은 DirectML(고성능 GPU), --ultra-json 은 Ultralytics val 의 검출을 같은 평가기로 채점)
# 교차 확인 — 각 라이브러리의 자체 평가기로 (WSL, CPU): 사용법은 crosscheck_native.py 머리말
```

### 결과 (2026-11-05)

**학습** (WSL, 전원 연결)

| 모델 | 학습 시간 | 검증 최고 — 그 라이브러리의 평가기 | 쓴 가중치 |
|---|---|---|---|
| YOLO26s | 1.6시간 (배치 20) | 49 epoch: mAP50-95 0.688 · mAP50 0.868 (Ultralytics. 100 epoch 째 0.676) | `best.pt` |
| YOLO26m | 3.3시간 (배치 10) | 75 epoch: mAP50-95 0.721 · mAP50 0.888 (Ultralytics. 100 epoch 째 0.717) | `best.pt` |
| RF-DETR-S | 5.8시간 (배치 4 × 누적 4, 매처는 CPU) | 33 epoch: EMA mAP50-95 0.769 · mAP50 0.925 (rfdetr. 100 epoch 째 0.747) | `checkpoint_best_total.pth` |
| RF-DETR-N (11-06) | 6.65시간 (같음 — 다른 CPU 작업과 겹쳐 늘었다. 제 속도면 4.2~4.5시간, 위 RF-DETR-N 항목) | 12 epoch: EMA mAP50-95 0.701 · mAP50 0.892 (rfdetr. 100 epoch 째 0.665) | `checkpoint_best_total.pth` |

YOLO26s·RF-DETR-S 는 중간(49·33 epoch)에 검증 최고를 찍고 그 뒤로는 학습 손실만 내려갔다(YOLO26m 은 75 epoch). RF-DETR-N 은 더 일찍(12 epoch) 찍고 100 epoch 째엔 0.036 내려갔다(S 0.022).
rfdetr 1.11.2 는 정규 모델을 따로 고르지 않는다 — `metrics.csv` 의 정규·EMA 열이 모든 epoch 에서 같고 끝에 'Best total checkpoint saved from EMA (regular=0.0000, …)' 라서 `checkpoint_best_total.pth` 는 검증 최고 epoch 의 EMA 가중치다.
Ultralytics 8.4.142 의 `best.pt` 는 검증 mAP50-95 가 가장 높은 epoch 다
— 그 검증은 앱이 쓰는 end-to-end 출력이 아니라 one-to-many 출력 + NMS 로 잰 값이다(아래 '두 평가기의 차이 나누기').
rfdetr 기본 조리법은 학습률을 `lr_drop`(기본 100) epoch 째에야 1/10 로 내려서
100 epoch 학습에선 끝까지 1e-4 다(`metrics.csv` 의 `train/lr` · rfdetr `training/module_model.py`). 일찍 내리면 나아질 수 있지만 해 보지 않았다

**시험 분할 973장** (정답 6,471개 · 32² 미만 451개) — `eval_test.py`: ONNX · 앱과 같은 전처리 · pycocotools(이미지당 100개). 정밀도·재현율은 앱 임계 0.3 · IoU 0.5

| 모델 | 전처리 | AP | AP50 | AP75 | APs | APm | APl | 임계 0.3 정밀도 | 재현율 |
|---|---|---|---|---|---|---|---|---|---|
| 배포 YOLO26s (09-17) | 레터박스 Nearest (앱 그대로) | 0.653 | 0.822 | 0.702 | 0.392 | 0.558 | 0.728 | 0.882 | 0.804 |
| 배포 YOLO26s | 레터박스 쌍선형 | 0.672 | 0.845 | 0.722 | 0.416 | 0.594 | 0.734 | 0.894 | 0.817 |
| YOLO26s (재학습) | 레터박스 Nearest | 0.640 | 0.827 | 0.684 | 0.357 | 0.559 | 0.715 | 0.878 | 0.831 |
| YOLO26s (재학습) | 레터박스 쌍선형 | 0.660 | 0.849 | 0.710 | 0.402 | 0.590 | 0.722 | 0.881 | 0.841 |
| YOLO26m (재학습) | 레터박스 Nearest | 0.677 | 0.842 | 0.727 | 0.390 | 0.590 | 0.758 | 0.897 | 0.855 |
| YOLO26m (재학습) | 레터박스 쌍선형 | 0.708 | 0.875 | 0.763 | **0.430** | 0.640 | 0.767 | **0.902** | 0.875 |
| **RF-DETR-S** FP32 | 정사각형 늘이기 쌍선형 (공식) | **0.742** | **0.921** | **0.790** | 0.403 | **0.665** | **0.832** | 0.886 | **0.920** |
| RF-DETR-S FP16 (DirectML) | 같음 | 0.742 | 0.921 | 0.792 | 0.408 | 0.665 | 0.832 | 0.886 | 0.919 |
| RF-DETR-N FP32 (11-06) | 정사각형 늘이기 쌍선형 (공식 — 384) | 0.665 | 0.871 | 0.719 | 0.327 | 0.562 | 0.775 | 0.828 | 0.890 |
| RF-DETR-N FP16 (DirectML) | 같음 | 0.665 | 0.870 | 0.720 | 0.326 | 0.563 | 0.774 | 0.828 | 0.889 |

FP32 모델은 CPU 로 쟀다. DirectML(`--ep dml`, NVIDIA)로 다시 재도 RF-DETR-S · YOLO26s · YOLO26m(Nearest) · RF-DETR-N 모두 **넷째 자리까지 같았다**.
(RF-DETR-N 은 11-06 같은 평가기로 쟀다 — 그때 S·YOLO26m 의 검출 파일을 다시 채점해 0.7417 · 0.7081 이 그대로 나오는 것을 봤다. 결과 `out/eval*` — 앞의 것들은 worktree 에서 돌려 그쪽 `out/` 에 있다)

클래스별 AP (YOLO 는 쌍선형):

| 클래스 (시험 정답 수) | YOLO26s | YOLO26m | RF-DETR-S | RF-DETR-N |
|---|---|---|---|---|
| car (1,591) | 0.799 | **0.832** | 0.815 | 0.739 |
| truck (244) | 0.716 | 0.792 | **0.802** | 0.716 |
| bus (149) | 0.849 | 0.868 | **0.903** | 0.881 |
| motorcycle (205) | 0.532 | 0.610 | **0.614** | 0.560 |
| scooter (156) | 0.537 | 0.556 | **0.675** | 0.562 |
| bicycle (198) | 0.548 | 0.619 | **0.667** | 0.576 |
| person (3,928) | 0.643 | 0.679 | **0.715** | 0.620 |

- **RF-DETR-S 가 전체로는 가장 정확하다**: AP 0.742 — 쌍선형으로 잰 YOLO26m 보다 +0.034, YOLO26s 보다 +0.082(앱 그대로의 Nearest 와 견주면 +0.065 · +0.102).
  앱 임계 0.3 에서 재현율 0.920(YOLO26m 0.875 · YOLO26s 0.841), 정밀도는 0.886 으로 YOLO(0.88~0.90)와 비슷하다
- **앞서는 몫은 두 바퀴·사람·버스에서 나온다**(YOLO26m 대비 scooter +0.12 · bicycle +0.05 · person +0.04 · bus +0.035). **car 는 YOLO26m 이 낫고**(0.832 vs 0.815) truck·motorcycle 은 비슷하다.
  **작은 물체(APs)도 YOLO26m 쌍선형이 낫다**(0.430 vs 0.403) — 도로 CCTV 의 먼 차가 여기 든다
- **FP16 은 정확도를 잃지 않는다**: DirectML 에서 FP32 0.7417 → FP16 0.7416(재현율 0.920 → 0.919). 1단계의 FP16 속도(9.1 ms)를 그대로 쓸 수 있다
- **RF-DETR-N(11-06)은 YOLO26s 급이다**: AP 0.665 — 쌍선형 YOLO26s(0.660)와 같고 YOLO26m 보다 −0.043, RF-DETR-S 보다 −0.077(FP16 도 0.6648 → 0.6647). 재현율은 높고(0.890 vs YOLO26s 0.841) 정밀도는 낮다(0.828 vs 0.881).
  약한 곳은 작은·중간 물체(APs 0.327 · APm 0.562 vs YOLO26s 0.402 · 0.590)와 car(−0.06)·person(−0.02), 나은 곳은 큰 물체(APl 0.775 vs 0.722)·두 바퀴·bus(+0.03).
  입력이 384 라 먼 물체가 작게 들어가는 탓으로 보인다(추정 — 따로 재지 않았다)
- **앱의 Nearest 축소가 YOLO 의 AP 를 0.02~0.03 깎는다**: YOLO26s 0.640 → 0.660 · 배포 0.653 → 0.672 · YOLO26m 0.677 → 0.708(작은 물체 AP +0.02~0.045). Ultralytics 8.4.142 는 학습·검증 모두 쌍선형(`INTER_LINEAR`)으로 줄이는데
  앱(YoloDotNet 기본값)은 Nearest 라 학습 때와 다른 입력을 넣는 셈이다. YoloDotNet 4.2.0 `YoloOptions.SamplingOptions` 로 바꿀 수 있어 보인다 — 앱엔 안 해 봤다(NPU 짝의 양자화 보정도 Nearest 기준이라 같이 다뤄야 한다)
  — **고침(11-05, 아래 '앱의 쌍선형 축소')**: 이 차이의 대부분은 이 평가기가 640 보다 작은 111장을 **키울 때** 생긴 것이다. 앱(YoloDotNet)은 작은 이미지를 키우지 않아,
  앱이 실제로 하는 축소에서의 이득은 +0.005~0.008 이다
- **교차 확인 — 각 라이브러리의 자체 평가기**(`crosscheck_native.py`, WSL CPU): Ultralytics val(test) YOLO26s **0.674** · YOLO26m **0.725**(mAP50 0.870 · 0.897), rfdetr `evaluate(split="test")` RF-DETR-S **0.751**(mAP50 0.925) · RF-DETR-N **0.676**(0.885 — 11-06).
  RF-DETR-S 와의 차이는 0.077 · 0.026 — 우리 평가기(쌍선형)의 0.082 · 0.034 와 같은 쪽·비슷한 크기다. 우리 평가기는 자체 평가기보다 YOLO26s 0.014 · YOLO26m 0.017 · RF-DETR-S 0.010 · RF-DETR-N 0.011 낮게 잰다
  (RF-DETR-N 이 낮게 나온 것은 우리 평가기 탓이 아니다 — 두 RF-DETR 를 같은 만큼 낮게 잰다)
  — YOLO 몫은 아래처럼 다 나눴다(RF-DETR 몫은 나누지 않았다)
- RF-DETR 는 학습 분할만 썼다(epoch 당 295 step × 실효 배치 16 = 4,708장). 로짓의 마지막 칸(클래스 수 + 1)은 rfdetr 의 YOLO 로더가 쓰지 않는 칸이라 뺀다 — rfdetr 자체 ONNX 추론도 같다(`background_class_id=-1`)
- 평가의 시간(장당 CPU 210~390 · DirectML 130~280 ms — UNC 경로로 이미지 읽기·전처리 포함)은 속도 비교에 쓰지 않는다 — 속도는 1단계 표

**YOLO — 두 평가기의 차이 나누기.** Ultralytics 의 검출(`save_json`)을 우리 평가기로 채점하면 Ultralytics 점수와 0.002 안에서 같다(0.673 · 0.723 — `--ultra-json`).
그러니 차이는 점수 계산이 아니라 추론 경로에서 온다. 한 가지씩 바꿔 가며 쟀다(mAP50-95, 시험 분할):

| 추론 경로 | YOLO26s | YOLO26m | 바꾼 것의 몫 |
|---|---|---|---|
| Ultralytics val 기본값 — rect 배치 · **one-to-many 출력 + NMS**(iou 0.7) | 0.674 | 0.725 | |
| 〃 640×640 정사각형 입력 (`crosscheck_native.py --square`) | 0.672 | 0.717 | rect −0.002 · −0.008 |
| 〃 + end-to-end 출력 (`--square --e2e` — 앱의 ONNX 와 같은 one-to-one 출력) | 0.662 | 0.710 | 출력 경로 −0.010 · −0.007 |
| 우리 ONNX — 정사각 · 쌍선형 · 회색 여백 (`--yolo-pad 114`, DirectML) | 0.662 | 0.708 | (위와 같음) |
| 〃 검은 여백 (앱) | 0.660 | 0.708 | 여백 색 ≈ 0 |
| 〃 Nearest 축소 (이 평가기 — 640 보다 작은 이미지도 키운다. 앱과 다르다: 아래 '앱의 쌍선형 축소') | 0.640 | 0.677 | 축소 −0.020 · −0.031 |

- **Ultralytics 8.4.142 의 val 은 `nms` 를 안 주면 YOLO26 의 one-to-many 출력에 NMS 를 걸어 잰다** — `engine/validator.py` 가 `end2end=self.args.nms is False` 이고 기본값이 None 이다.
  학습 중 검증과 `best.pt` 고르기도 그렇다. 그래서 **학습 로그의 mAP 는 앱이 쓰는 end-to-end 출력보다 0.007~0.010 높게 나온다**. 앱과 같은 것을 재려면 `nms=False`.
  그 몫은 val 의 NMS 가 multi-label(한 박스가 여러 클래스로 남는다)이라서 생긴다 — single-label 로 바꾸면 end-to-end 보다 오히려 낮다(아래 'YOLO 남은 여지')
- 이 평가기로 흉내 낸 앱 경로가 Ultralytics 기본 val 보다 잃는 몫은 YOLO26s 0.034 · YOLO26m 0.048 이다. 그중 'Nearest 축소' 몫은 작은 이미지를 키운 이 평가기의 것이 대부분이었다(아래 '앱의 쌍선형 축소'). 되찾을 여지는 셋이다.
  ① 쌍선형 축소 — 앱 코드로 재면 +0.005~0.006, OpenCV 로 레터박스를 만들고 박스 +0.5 px 까지 하면 +0.010(11-05 앱에 넣었다 — 배포 YOLO26s 0.6555 → 0.6658).
  ② NMS 를 넣어 내보내기(Ultralytics `nms=True`) — 처음엔 +0.007~0.010 으로 봤지만 그건 val 의 multi-label NMS 몫이었다. 내보낸 NMS 는 single-label 이라 앱 경로에서 AP 가 오히려 0.003~0.005 낮다(11-05, 아래 'YOLO 남은 여지').
  ③ 16:9 영상에 640×384 입력 — 처음 적은 +0.002~0.008 은 val 의 rect 배치 몫이었고 16:9 이미지에선 AP 가 같다(±0.002). 대신 `session.Run` 이 31% 빠르다(같은 절 — 정확도가 아니라 속도)

### 판단 (2단계)

- 3단계 기준(이길 때만 앱에)으로는 **RF-DETR-S 가 이긴다** — 같은 데이터·각자의 기본 조리법에서 전체 AP·재현율이 넷 중(11-06 의 RF-DETR-N 까지) 가장 높고, FP16 이면 1단계 속도로 YOLO26m(FP32 14.0 ms — YOLO FP16 은 DirectML 에서 못 돌림)보다 빠르다(9.1 ms).
  지금 앱 경로(배포 YOLO26s — 11-05 쌍선형 레터박스로 바꾼 앱 코드로 잰 0.6658, 아래 '앱의 쌍선형 축소')보다 AP +0.076
- 다만 이득이 고르지 않다: car·작은 물체는 YOLO26m(쌍선형)이 같거나 낫다 — 유고 규칙이 보는 차량만 보면 YOLO26m 과의 차이가 작다
- **16:9 이미지(앱의 영상·화면)만 보면 AP 는 YOLO26m 과 같다**(0.695 vs 0.696 — 11-05, 아래 'YOLO 남은 여지' 의 덤). 앞서는 몫은 16:9 가 아닌 이미지에서 나온다.
  16:9 에서도 RF-DETR-S 는 IoU 0.5 재현율·사람·bicycle 이 낫고, 작은 물체(APs 0.379 vs 0.519)·car·truck 은 YOLO26m 이 낫다.
  RF-DETR-N 은 16:9 에서 0.611 로 YOLO26s(0.654)보다도 낮다(11-06 — 같은 절의 표)
- 앱에 넣으려면(1단계에서 정리): YoloDotNet 밖의 새 추론 경로(정사각형 늘이기 전처리 — float 경로가 CPU 5~7 ms 라 줄여야 한다 · 후처리), 모델 파일 57 MB(FP16), NPU 는 YOLO 만(짝 모델이 YOLO 전용),
  HUD 가 기대는 결정성(같은 입력에 같은 출력) 확인 — 둘 다 11-05 확인했다(위 '3단계 준비': 원본에서 바로 쌍선형 0.29 ms · 출력이 비트 단위로 같음)
- 더 싼 길도 있다: ① 앱의 YOLO 축소를 쌍선형으로 — 11-05 넣었다. 앱 경로로 0.6555 → 0.6658(+0.010)이라 처음 적은 +0.02~0.03 보다 작았다(아래 '앱의 쌍선형 축소')
  ② YOLO26m 으로 바꾸기(쌍선형이면 0.708, 추론은 2.2배 — 16:9 소스에 640×384 로 내보내면 1.44배(9.1 ms)로 RF-DETR-S FP16 과 같은 속도, 아래 'YOLO 남은 여지') ③ RF-DETR-N — **11-06 학습해 보니 YOLO26s 급이라 권하지 않는다**: 전체 AP 0.665(YOLO26s 쌍선형 0.660 · 배포 YOLO26s 앱 경로 0.6658), 16:9 에선 0.611 로 YOLO26s(0.654)보다 낮다.
  남는 이점은 속도(FP16 5.2 ms vs 지금 YOLO26s 6.4 ms — 1단계의 COCO 모델 값. 클래스 칸 수만 다른 학습한 N 은 재지 않았다)뿐인데, 그것도 YoloDotNet 밖의 새 추론 경로를 만들어야 얻는다
- 확인 안 한 것: 학습을 한 번씩만 했다(시드 차이 모름) · 학습률을 일찍 내리는 조리법(rfdetr `lr_drop` — N 은 12 epoch 에서 꺾여 더 그럴 수 있다) · 시험 분할은 학습 데이터와 같은 출처라 **앱의 CCTV 영상(test1~5)에서의 차이** ·
  추적기·유고 규칙 위에서의 차이

## 앱의 쌍선형 축소 (2026-11-05 — 앱을 Nearest 에서 쌍선형으로 바꾸며 잰 것)

앱은 먼저 YoloDotNet `YoloOptions.SamplingOptions = SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None)` 으로 줄이게 했고(`MainWindow.Inference.cs` 의 `InputSampling` — 아래 표의 '쌍선형' 열),
같은 날 레터박스를 OpenCV 쌍선형으로 직접 만들어 넘기게 바꿨다(루트 `YoloLetterbox.cs` — 맨 아래 'OpenCV 선축소').
위 표의 'Nearest (앱 그대로)' 행은 이 평가기(OpenCV)로 앱을 흉내 낸 것이라, 앱과 같은 코드로 다시 쟀다 — `TrackerCheck testsplit`(YoloDotNet + DirectML, 본 앱 `ProcessFrame` 과 같은 길)이
시험 분할 973장을 두 방식으로 검출해 JSON 을 쓰고, 이 평가기가 `--app-json` 으로 채점한다(`--subset`·`--compare`·`--yolo-no-upscale` 도 이때 더했다 — `eval_test.py` 머리말).

**앞의 평가와 다른 점 둘**
- **YoloDotNet 은 모델보다 작은 이미지(가로·세로 모두 640 미만 — 시험 분할 111장)를 키우지 않고 가운데에 그대로 둔다**(`ResizeImageProportional`). 이 평가기는 모든 이미지를 640 에 맞춰 키웠다.
  그래서 작은 이미지에선 앱의 두 방식이 비트 단위로 같고, 이 평가기의 Nearest 는 작은 이미지를 **키우며** 크게 손해를 봤다
- **OpenCV `INTER_NEAREST_EXACT` 는 Skia 의 Nearest 와 화소를 똑같이 고르지 않는다** — 973장 중 540장이 달랐다(같은 화소 80.5%, 3 이상 차이 13%, 최대 255 — 표본 위치가 두 화소 중심의 한가운데에 걸리는 배율에서 반올림이 갈린다.
  정확히 2배 축소면 Skia 는 2×2 묶음의 왼쪽 위, `NEAREST_EXACT` 는 오른쪽 아래 화소 — `TrackerCheck inputbench`).
  예전 기록(NpuBench README·`tools/npu_quantize.py`)은 둘이 같다고 적었다. 쌍선형은 Skia 와 OpenCV `INTER_LINEAR` 이 화소 87.5% 같고 나머지는 **정확히 ±1**(채널 평균 |차| 0.057)이라 같은 방식으로 봐도 된다

**배포 YOLO26s (09-17, `publish\best.onnx`) — 시험 분할 AP** (임계 0.3 정밀도 / 재현율)

| 이미지 | 이 평가기 Nearest → 쌍선형 (CPU, 작은 이미지는 키움 — 위 표) | **앱 Nearest → 쌍선형** (YoloDotNet · DirectML) |
|---|---|---|
| 전체 973장 | 0.653 → 0.672 (+0.019) | **0.6555 → 0.6602 (+0.005)** · 0.885/0.813 → 0.894/0.817 |
| 앱이 줄이는 862장 (`--subset resized`) | 0.6555 → 0.6638 (+0.008) · APs 0.428 → 0.440 | **0.6472 → 0.6533 (+0.006)** · APs 0.383 → 0.399 · AP50 0.826 → 0.836 |
| 작은 111장 (`--subset small`) | 0.646 → 0.736 (+0.09 — 키우며 생긴 차이) | 0.7106 → 0.7106 (같음 — 줄이지 않는다) |

루트 `best.onnx`(09-11 — 이 데이터셋의 시험 분할을 학습에 썼을 수 있어 절대값은 의미가 없다)도 앱 경로로 0.8080 → 0.8134(+0.005), 줄이는 862장 0.7992 → 0.8055(+0.006).

- **앱이 실제로 얻는 이득은 +0.005~0.006 AP**(작은 물체 AP +0.009~0.015, 정밀도 +0.009) — 앞서 적은 +0.02~0.03 은 작은 이미지를 키우는 이 평가기의 몫이 섞인 값이었다
- **앱 경로와 이 평가기의 검출은 거의 같다** — `--yolo-resize linear --yolo-no-upscale --ep dml`(앱과 같은 전처리)로 돌린 검출과 앱 검출을 임계 0.3 에서 맞대면(`--compare`)
  5,919 · 5,917개 중 5,910개가 같은 클래스 IoU 0.5 이상으로 짝지어지고(99.85% · 99.88%, 모든 검출이 짝지어진 이미지 957/973), 신뢰도 차 중앙 0.0000 · 99% 0.040, 좌표 차(네 변 중 최대) 중앙 0.92 px · 99% 2.9 px
- 그래도 AP 는 앱이 0.012 낮다(0.6602 vs 0.6725) — **YoloDotNet 이 박스 좌표를 정수로 내린다**(`ObjectDetectionModuleV26` 의 `(int)((x - xPad) * gain)` — 평균 −0.5 px).
  확인: 이 평가기의 박스를 앱처럼 내리면 줄이는 862장 0.6638 → 0.6593, 앱 박스에 +0.5 px 를 더하면 0.6533 → 0.6573(작은 물체 AP 0.399 → 0.416). 남는 몫은 레터박스 여백을 정수로 그리고 실수로 되돌리는 YoloDotNet 의 반올림·DirectML/CPU 차이로 보인다(나누지 않았다).
  앱에서 +0.5 px 보정은 하지 않았다 — 축소 방식과 별개다.
  같은 날 잰 **OpenCV 선축소**(OpenCV 로 640×640 레터박스를 만들어 넘기고 박스를 +0.5·정확한 여백으로 직접 되돌림)는 0.6658 로 +0.5 px 보정과 같은 정확도에
  검출 한 번이 8.25 → 7.40 ms 라 **앱에 넣었다**(같은 날, 루트 `YoloLetterbox.cs` — `TrackerCheck` README 'YOLO 입력 축소' 의 선축소 시험). 그래서 지금 앱은 위 표의 쌍선형 열이 아니라 0.6658 이다.
  넣기 전에 흉내로 먼저 쟀다 — `--yolo-no-upscale` 실수 좌표 검출(0.6725)을 줄인 이미지 좌표로 옮겨 정수로 내린 뒤 키우면 **0.6535**(작은 물체 0.349), 내린 뒤 +0.5 하고 키우면 0.6658 —
  실제 앱 경로와 같은 값이다. 그러니 +0.5 는 꼭 필요하다(빼면 쌍선형 이득보다 크게 잃는다). 원래 크기에서 내리는 지금까지의 앱 흉내는 0.6676(+0.5 하면 0.6685)
- 비용: YoloDotNet 의 축소가 프레임당 0.5~1.0 → 1.5~2.2 ms(`TrackerCheck inputbench` 두 번 — 1280×720·2560×1440 모두, 추가 비용 1.0~1.4 ms). 앱의 파일 재생 처리 FPS 는 92.4 · 94.5 → 86.0 · 85.7
  (`tests/ui/Measure-Fps.ps1`, test1.mp4, Debug, 기준선을 앞뒤로 두 번). 같은 입력은 같은 출력이다(50번 반복 · 시험 분할 50장 두 번 — 모두 비트 단위로 같음)

```powershell
# 앱 경로 검출 (GPU, 약 2.5분 — 데이터는 읽기만 한다). UNC 경로라 Git Bash 가 아니라 PowerShell 에서
experiments\TrackerCheck\bin\Release\net10.0-windows10.0.19041.0\TrackerCheck.exe testsplit '\\wsl.localhost\Ubuntu\home\chisu\rfdetr_work\data\visible' `
    experiments\TrackerCheck\out\testsplit 'deployed=publish\best.onnx' 'root=best.onnx'
# 채점 — 전체·줄이는 이미지·작은 이미지 (--subset), 두 방식 비교 (--compare)
uv run --no-project --python 3.12 --with onnxruntime --with pycocotools --with opencv-python --with numpy experiments/RfDetrBench/step2/eval_test.py `
    --data '\\wsl.localhost\Ubuntu\home\chisu\rfdetr_work\data\visible' --out experiments\RfDetrBench\out\eval_app --subset resized `
    --app-json 'app_nearest:experiments\TrackerCheck\out\testsplit\app_deployed_nearest.json' --app-json 'app_linear:experiments\TrackerCheck\out\testsplit\app_deployed_linear.json' `
    --compare app_nearest:app_linear
```

## YOLO 남은 여지 — NMS 를 넣어 내보내기 · 16:9 영상에 640×384 입력 (2026-11-05)

2단계 '두 평가기의 차이 나누기' 끝에 적은 되찾을 여지 중 ②·③ 을 앱 경로로 쟀다(① 쌍선형 축소는 앞 절 — 앱에 넣었다). 학습은 하지 않았다 — 2단계에서 학습한 YOLO26s·YOLO26m 의 `best.pt` 를 다르게 내보내기만 했다.

- **NMS 를 넣어 내보내기** (`export_trained.py yolo <best.pt> <이름> --nms <conf>` = Ultralytics `nms=True` — exporter 의 `NMSModel`): one-to-many 출력 → 박스마다 최고 클래스 하나(single-label) → 신뢰도 > conf → 클래스별 NMS(IoU 0.7, ONNX `NonMaxSuppression`) → 상위 300개를 0 으로 채운 `[1,300,6]`.
  출력 꼴은 지금의 end-to-end 와 같지만 메타데이터는 `end2end False` 다. 신뢰도 임계값이 모델에 박혀서 AP 를 잴 판(0.001)과 앱에 넣을 판(0.25)을 따로 만들었다
- **640×384 입력** (`--imgsz 384 640`): 같은 가중치를 입력 크기만 바꿔 내보낸다(앵커 8,400 → 5,040개). 16:9 영상(1280×720 → 640×360)은 640×640 과 같은 배율 0.5 로 들어가고 위아래 여백만 준다

평가는 앱 경로 그대로다 — `eval_test.py --ep dml --yolo-resize linear --yolo-no-upscale`(OpenCV 쌍선형 레터박스 · 검은 여백 · 640 보다 작은 이미지는 키우지 않음 · DirectML).
그래서 YOLO26s 가 2단계 표(작은 이미지를 키운 0.660)와 조금 다른 0.664 다. **16:9** 는 가로/세로 1.65 이상인 356장(`--subset wide` — 정답 2,103개, 32² 미만 229개. 그중 640 보다 작아 키우지 않는 이미지는 10장)이다.
정밀도·재현율은 앱 임계 0.3 · IoU 0.5 에서 **앱의 중복 박스 필터(`DuplicateBoxes`)를 건 뒤** — 앱 화면·추적기가 받는 박스다(`eval_test.py` 가 이제 걸기 전·뒤 둘 다 낸다.
필터는 같은 자리 중복만 빼서 정밀도가 0.02~0.035 오르고 재현율은 0.003~0.008 내린다).

| 모델 | 출력 | 입력 | 전체 973장 AP (APs · AR100) | 정밀도 / 재현율 | 16:9 356장 AP (APs · AR100) | 정밀도 / 재현율 |
|---|---|---|---|---|---|---|
| YOLO26s | end-to-end (지금 앱 꼴) | 640×640 | **0.664** (0.412 · 0.777) | 0.909 / 0.838 | 0.654 (0.453 · 0.778) | 0.908 / 0.828 |
| YOLO26s | NMS | 640×640 | 0.660 (0.368 · 0.736) | 0.895 / 0.860 | 0.651 (0.384 · 0.728) | 0.901 / 0.849 |
| YOLO26s | end-to-end | 640×384 | 0.645 (0.378 · 0.768) | 0.893 / 0.834 | **0.654** (0.481 · 0.779) | 0.911 / 0.828 |
| YOLO26s | NMS | 640×384 | 0.646 (0.345 · 0.724) | 0.879 / 0.858 | 0.650 (0.381 · 0.723) | 0.902 / 0.850 |
| YOLO26m | end-to-end | 640×640 | **0.708** (0.412 · 0.785) | 0.927 / 0.872 | **0.696** (0.519 · 0.778) | 0.928 / 0.873 |
| YOLO26m | NMS | 640×640 | 0.703 (0.401 · 0.760) | 0.912 / 0.884 | 0.691 (0.504 · 0.755) | 0.918 / 0.883 |
| YOLO26m | end-to-end | 640×384 | 0.700 (0.352 · 0.779) | 0.918 / 0.871 | 0.694 (0.520 · 0.776) | 0.927 / 0.869 |
| YOLO26m | NMS | 640×384 | 0.697 (0.350 · 0.756) | 0.908 / 0.885 | 0.694 (0.498 · 0.759) | 0.912 / 0.882 |
| RF-DETR-S FP16 (참고 — 2단계 검출을 다시 채점) | 쿼리 상위 300 | 512×512 늘이기 | 0.742 (0.408 · 0.832) | 0.905 / 0.913 | 0.695 (0.379 · 0.804) | 0.907 / 0.912 |
| RF-DETR-N FP16 (참고 — 11-06 학습) | 쿼리 상위 300 | 384×384 늘이기 | 0.665 (0.326 · 0.774) | 0.857 / 0.881 | 0.611 (0.327 · 0.735) | 0.836 / 0.854 |

**속도** — `RfDetrBench yolo`(`YoloBench.cs`): 앱 런타임(Windows ML ORT 1.27.1 + DirectML, NVIDIA, 전원 연결)에서 test4 프레임 30장을 앱처럼 레터박스해 워밍업 20회 뒤 150회. `session.Run`(입력 복사·출력 읽기 포함):

| 모델 | 입력 | 중앙 | p95 | 노드 배치 |
|---|---|---|---|---|
| YOLO26s end-to-end | 640×640 | 6.34 ms | 7.06 | 모두 DirectML(융합 노드 2개 — CPU 는 모양 계산 Mod 1개) |
| YOLO26s NMS (conf 0.25 · 0.001) | 640×640 | 7.30 · 7.11 | 8.42 · 8.21 | `NonMaxSuppression` 은 **CPU**(DirectML EP 에 없다)와 그 앞뒤 작은 연산 13개. DirectML 은 융합 노드 4개로 쪼개지고 NMS 앞의 동적 크기 연산(NonZero·GatherND·Pad 등)이 따로 돈다 |
| YOLO26s end-to-end | 640×384 | **4.39** | 4.76 | 모두 DirectML |
| YOLO26s NMS (conf 0.25) | 640×384 | 6.12 | 6.97 | 위 NMS 판과 같다 |
| YOLO26m end-to-end | 640×640 | 13.16 | 15.68 | 모두 DirectML |
| YOLO26m end-to-end | 640×384 | **9.14** | 10.24 | 모두 DirectML |

**실제 영상** — `yolo_video_check.py`(test1~5 모든 프레임, 앱 전처리, 임계 0.3, YOLO26s — 정답이 없어 정확도가 아니라 무엇이 달라지는지를 본다):

| 영상 | 프레임 | 박스 end-to-end → NMS(0.25) | `DuplicateBoxes` 규칙에 걸리는 쌍 | 그런 쌍이 있는 프레임 | 같은 클래스 IoU>0.7 쌍 | 640×384 박스 · 640 검출과 짝 |
|---|---|---|---|---|---|---|
| test1 | 929 | 7,019 → 7,478 | 259 → 350 | 212 → 252 | 180 → 0 | 7,134 · 99.2% |
| test2 | 916 | 17,170 → 18,327 | 778 → 1,275 | 519 → 654 | 553 → 1 | 17,382 · 98.7% |
| test3 | 914 | 14,567 → 15,996 | 461 → 408 | 386 → 323 | 380 → 6 | 14,321 · 95.3% |
| test4 | 916 | 17,417 → 18,417 | 1,027 → 1,577 | 518 → 651 | 303 → 1 | 17,958 · 98.5% |
| test5 | 2,821 | 6,199 → 6,531 | 260 → 375 | 238 → 318 | 38 → 0 | (16:9 아님) |

(짝은 640 검출 중 같은 클래스·IoU 0.5 이상인 640×384 검출을 찾은 몫. NMS 판에 남은 같은 클래스 IoU>0.7 쌍 0~6 은 화면 밖으로 나간 박스를 프레임에 맞춰 자른 뒤 겹침이 커진 것으로 보인다)

**읽은 것**
- **NMS 판은 AP 를 올리지 않는다** — 전체 −0.003 · −0.005, 16:9 −0.004 · −0.005. 잃는 곳은 작은 물체(APs −0.010~−0.070)와 AR100(−0.023~−0.050)이다.
  클래스별 NMS 는 IoU 0.7 넘게 겹친 서로 다른 물체(원근으로 겹친 먼 차 줄 등)도 지워서 임계값을 내려도 되찾지 못한다. end-to-end 출력은 NMS 없이 그런 물체를 따로 낸다
- **앱 임계값 부근에선 YOLO26s 만 조금 낫다** — 중복 필터 뒤 임계 0.15~0.40 을 훑어 같은 정밀도끼리 맞대면(정밀도/재현율):
  YOLO26s end-to-end 0.25: 0.897/0.846 · 0.30: 0.909/0.838 · 0.35: 0.917/0.828, NMS 0.30: 0.895/0.860 · 0.35: 0.905/0.851 · 0.40: 0.917/0.842 → **같은 정밀도에서 재현율 +0.013~0.014**(16:9 는 +0.008~0.013).
  YOLO26m 은 end-to-end 0.20: 0.912/0.883 · 0.30: 0.927/0.872, NMS 0.30: 0.912/0.884 · 0.40: 0.927/0.873 → 같다. 임계 0.3 을 그대로 두고 바꿔 끼우면 재현율이 오르고 정밀도가 내린다(end-to-end 의 임계값을 0.05 쯤 낮춘 것과 비슷하다)
- **NMS 판이어도 `DuplicateBoxes` 는 그대로 필요하다** — 같은 클래스 IoU 0.7 넘는 중복(CLAUDE.md 3번 항목에서 본 것)은 거의 없어지지만, 클래스별 NMS 가 못 지우는 같은 자리 car+truck·범위만 다른 중복은 5개 영상 중 4개에서 늘었고 박스도 5~10% 많다
- NMS 판은 `session.Run` 이 1.0 ms(640) · 1.7 ms(640×384) 느리다 — `NonMaxSuppression` 이 CPU 라 GPU → CPU 복사가 끼고 그래프가 쪼개진다
- **640×384 는 16:9 이미지에서 정확도가 같고**(AP ±0.002, 중복 필터 뒤 정밀도·재현율 ±0.004) **`session.Run` 이 31% 빠르다**(YOLO26s 6.34 → 4.39 ms · YOLO26m 13.16 → 9.14 ms). 실제 영상의 검출도 95~99% 같다.
  16:9 가 아닌 이미지엔 손해다 — 전체 973장 AP 0.664 → 0.645 · 0.708 → 0.700(세로가 긴 이미지일수록 더 작게 들어간다). 앱에 넣으려면 소스 비율로 입력 크기를 골라야 한다(16:9 영상·HUD 화면 → 640×384, SAHI 타일·다른 비율 → 640×640)
- **덤 — 16:9 이미지만 보면 RF-DETR-S 와 YOLO26m 의 AP 가 같다**(0.695 · 0.696). 2단계의 RF-DETR-S 우위(전체 0.742 vs 0.708)는 16:9 가 아닌 이미지(사람·두 바퀴를 가까이 찍은 세로·4:3 사진 등)에서 나온다.
  16:9 에서 RF-DETR-S 는 IoU 0.5 기준 재현율이 높고(AP50 0.897 vs 0.875, 중복 필터 뒤 같은 정밀도 0.92 에서 재현율 0.906 vs 0.878 쯤) 사람·bicycle 이 낫지만,
  작은 물체(APs 0.379 vs 0.519)와 car(0.784 vs 0.814)·truck(0.756 vs 0.785)·motorcycle(0.589 vs 0.613) AP 는 YOLO26m 이 낫다.
  RF-DETR 는 이미지를 512×512 로 늘이므로 16:9 는 가로가 0.4배(YOLO 는 0.5배)로 줄어 먼 차가 더 작아진다(추정 — 따로 재지 않았다)
- **RF-DETR-N(11-06)은 16:9 에서 더 처진다** — 전체로는 YOLO26s 와 같은데(0.665 vs 0.664) 16:9 에선 0.611 vs 0.654. 384×384 로 늘이면 16:9 의 가로가 0.3배다.
  car(0.688 vs 0.780)·scooter(0.522 vs 0.620)·bicycle(0.380 vs 0.462)·person(0.585 vs 0.614)이 낮고 truck·motorcycle·bus 는 같다(FP16, `out/eval_wide` — 검출 파일을 `--subset wide` 로 다시 채점)

**왜 Ultralytics val 에선 NMS 가 end-to-end 보다 높았나** (2단계에서 0.007~0.010 높다고 적었다)
- 같은 전처리(정사각 · 회색 여백 · 작은 이미지도 키움 — `--yolo-pad 114`, `--yolo-no-upscale` 없이)로 우리 ONNX 를 돌리면 end-to-end 는 Ultralytics 와 같다(YOLO26s 0.6615 vs 0.6607 · YOLO26m 0.7077 vs 0.7083 — 둘 다 이 평가기로 채점, 임계 0.3 검출 99% 짝).
  그런데 NMS 판은 **0.6585 vs 0.6708 · 0.7040 vs 0.7149** 로 0.011 낮다 — 이 몫이 앱 경로에서 NMS 와 end-to-end 의 순서를 뒤집었다
- val 의 NMS 는 **multi-label** 이다 — `models/yolo/detect/val.py` 가 `multi_label=True` 를 박아 둬서 신뢰도 0.001 을 넘는 클래스마다 따로 후보가 되고 한 박스가 여러 클래스로 남는다(YOLO26s 검출 36,048개 중 2,155개 박스가 2~6개 클래스로 나왔다).
  `nms=True` 로 내보낸 `NMSModel` 은 박스마다 최고 클래스 하나다. 그 밖(신뢰도 > 0.001 · IoU 0.7 · 상한 300 · 클래스별 · 앞에서 자르는 TopK 없음)은 그래프 상수로 확인해 val 과 같았다
- **확인 — val 을 single-label 로 바꿔 돌리면**(`crosscheck_native.py --square --single-label`) **0.6551 · 0.7036**(Ultralytics 자체 점수 0.656 · 0.705)으로 우리 NMS 판과 같다 — 검출 수까지 31,331개로 같고 임계 0.3 검출이 99% 짝지어진다.
  그러니 val 의 +0.007~0.010 은 모두 multi-label 몫이고(multi-label → single-label −0.016 · −0.011), single-label NMS 는 end-to-end 보다 낮다(Ultralytics 자체 점수로도 0.656 vs 0.662 · 0.705 vs 0.710)
- 처음엔 multi-label 몫이 작아 보였다 — multi-label 검출에서 같은 박스의 여러 클래스 중 최고 하나만 남겨 채점하면 −0.001 뿐이다. 하지만 사후에 접는 것은 single-label NMS 와 다르다:
  multi-label 에선 최고 클래스로는 NMS 에 지워진 박스가 둘째 클래스 후보로 살아남는다(옆 anchor 의 truck 박스에 지워진 박스가 car 로 남는 식)
- multi-label 을 앱에 넣어도 이 이득은 거의 남지 않을 것이다 — 앱 임계값에서 같은 자리에 남는 다른 클래스 박스는 `DuplicateBoxes`(차량 6종은 한 묶음)가 신뢰도 높은 하나로 줄인다(추정 — 재지 않았다)

**판단**
- **NMS 판은 앱에 넣지 않는 쪽을 권한다** — AP·작은 물체·AR100 을 잃고, 얻는 건 YOLO26s 의 임계값 부근 재현율 +0.01 정도(YOLO26m 은 없음)인데 `session.Run` +1 ms(CPU NMS), `DuplicateBoxes` 는 그대로 필요하다.
  넣으려면 배포 모델을 그 `.pt` 로 다시 내보내고 YoloDotNet 이 `end2end False` 인 `[1,300,6]` 를 어떻게 읽는지, NPU 짝(`NonMaxSuppression` 이 NPU 에서 어디로 가는지)도 봐야 한다 — 모두 확인 안 했다
- **640×384 는 정확도가 아니라 속도다** — 16:9 소스에서 `session.Run` −31%. 지금 앱은 속도에 쫓기지 않아서(파일 재생 처리 95~99 FPS · HUD 박스 지연 약 9 ms) YOLO26s 로는 이득이 작다(프레임당 약 2 ms).
  **YOLO26m 으로 갈 때 쓸모가 크다** — YOLO26m 640×384 는 9.14 ms 로 RF-DETR-S FP16(9.1 ms + 전처리 0.29 ms)과 같은 속도이고, 16:9 이미지에서 AP 도 같다(0.694 vs 0.695). 지금 YOLO 경로(YoloDotNet · `YoloLetterbox` · NPU 짝)를 그대로 쓴다.
  앱 쪽 일: 소스 비율에 따라 세션 둘(640×640 · 640×384)을 고르기, NPU 짝도 둘. `YoloLetterbox` 는 모델 입력의 가로·세로를 따로 읽는다 — YoloDotNet 이 직사각형 입력을 그대로 받는지는 확인 안 했다
- 3단계(RF-DETR-S 를 '모델 종류'로)는 사용자 결정 대기 그대로다(→ 11-06 사용자가 YOLO26m 640×384 를 골랐다 — 맨 끝 '3단계'). 다만 앱의 소스(16:9 CCTV·화면)만 보면 선택지가 가까워졌다: **RF-DETR-S FP16**(IoU 0.5 재현율 · 사람·두 바퀴 우위, 새 추론 경로, NPU 없음) vs **YOLO26m 640×384**(같은 AP · 작은 물체·차량 우위, 지금 경로)

```powershell
# 내보내기 — NMS 판(AP 용 0.001 · 앱용 0.25)과 640×384 (저장소 루트에서, Windows CPU)
uv run --no-project --python 3.12 --with "ultralytics==8.4.142" --with onnx --with onnxslim --with onnxruntime experiments/RfDetrBench/step2/export_trained.py yolo "\\wsl.localhost\Ubuntu\home\chisu\rfdetr_work\runs\yolo\y26s\weights\best.pt" y26s-nms --nms 0.001
uv run --no-project --python 3.12 --with "ultralytics==8.4.142" --with onnx --with onnxslim --with onnxruntime experiments/RfDetrBench/step2/export_trained.py yolo "\\wsl.localhost\Ubuntu\home\chisu\rfdetr_work\runs\yolo\y26s\weights\best.pt" y26s-384 --imgsz 384 640
# (y26s-nms25 = --nms 0.25 · y26s-384-nms · y26m 도 같게)
# 시험 분할 — 앱 경로 그대로. --subset wide 면 16:9 만 (모델을 다시 돌리지 않고 --det-json <이름>:<out>\det_<이름>.json 으로 다시 채점해도 된다)
uv run --no-project --python 3.12 --with onnxruntime-directml --with pycocotools --with opencv-python --with numpy experiments/RfDetrBench/step2/eval_test.py `
    --data '\\wsl.localhost\Ubuntu\home\chisu\rfdetr_work\data\visible' --out experiments\RfDetrBench\out\eval_yolo --ep dml --yolo-resize linear --yolo-no-upscale `
    --model 'yolo:y26s:experiments\RfDetrBench\out\step2_models\y26s.onnx' --model 'yolo:y26s-nms:experiments\RfDetrBench\out\step2_models\y26s-nms.onnx'
# 실제 영상 (DirectML — y26s · y26s-nms25 · y26s-384 를 쓴다)
uv run --no-project --python 3.12 --with onnxruntime-directml --with opencv-python --with numpy experiments/RfDetrBench/step2/yolo_video_check.py experiments\RfDetrBench\out\step2_models
# 속도·노드 배치 (앱 런타임)
dotnet run --project experiments\RfDetrBench -c Release -- yolo experiments\RfDetrBench\out\step2_models\y26s.onnx experiments\RfDetrBench\out\step2_models\y26s-nms25.onnx experiments\RfDetrBench\out\step2_models\y26s-384.onnx
# val 을 single-label NMS 로 (WSL, CPU — crosscheck_native.py 머리말)
#   ... crosscheck_native.py yolo runs/yolo/y26s/weights/best.pt y26s_square_single --square --single-label
```

## 3단계 — YOLO26m 640×384 를 앱에 (2026-11-06)

'YOLO 남은 여지' 끝의 두 후보(RF-DETR-S FP16 · YOLO26m 640×384) 중 사용자가 **YOLO26m 640×384** 를 골랐다 — 지금 YOLO 경로(YoloDotNet · `YoloLetterbox` · NPU 짝)를 그대로 쓴다.

**한 일**
- 루트 `best.onnx` ← `out/step2_models/y26m-384.onnx`(2단계 YOLO26m 의 `best.pt` 를 `--imgsz 384 640` 로 내보낸 end-to-end — Ultralytics 8.4.142 `nms=False`, 81.7 MB, MD5 `94A26C12…`,
  메타데이터 `imgsz [384, 640]` · `end2end True` · 날짜 2026-11-05. 7클래스 이름·순서는 배포 모델과 같다)
- NPU 짝 `best.npu.onnx` 다시 만들기(`tools\Make-NpuModel.ps1` — 보정 test1~5 100장을 640×384 로, 43초, 21.4 MB — `experiments/NpuBench` README 결과 4)
- 앱: SAHI 타일 = 모델 입력 크기(`RunSahi` — 예전엔 640 정사각형 고정이라 640×384 모델에 넣으면 0.6배로 줄어들었다), 창 제목에 입력 크기(`DescribeModel` — `YOLO26m 640×384 (2026-11-05 학습)`).
  레터박스(`YoloLetterbox`)·NPU 보정(`tools/npu_quantize.py`)은 이미 모델 입력의 가로·세로를 따로 읽었다. `TrackerCheck` 의 SAHI 사본 둘과 `NpuBench`(640 상수였다)도 모델 입력 크기를 읽게 했다
- **소스 비율로 세션 둘(640×640 · 640×384)을 고르는 일은 하지 않았다**('YOLO 남은 여지' 판단에 적은 앱 쪽 일). 앱의 소스(16:9 CCTV 영상 · HUD 2560×1440)는 640×384 가 640×640 과 같은 배율이고,
  SAHI 는 타일을 모델 크기로 바꾸면 배율 1 이다. 세션을 둘 두면 모델 파일·NPU 짝이 둘(YOLO26m 이면 모델만 160 MB 남짓)이 되고, 사용자가 `best.onnx` 를 바꿔 쓸 때 두 판이 어긋날 위험도 생긴다.
  대가: 16:9 가 아닌 소스는 640×640 판보다 작게 들어간다 — test5(1280×854)는 0.45배(640×640 이면 0.5배), 4:3 웹캠 640×480 은 0.8배(1배)

**확인 — YoloDotNet 이 직사각형 입력을 그대로 받는다**: 앱과 같은 길(`TrackerCheck testsplit` — YoloDotNet + `YoloLetterbox` + DirectML)로 시험 분할 973장을 검출해 이 평가기로 채점했다(`--app-json`).
정밀도·재현율은 앱 임계 0.3 · IoU 0.5 에서 앱의 중복 박스 필터를 건 뒤다.

| 모델 (앱 경로) | 전체 973장 AP (APs · AR100) | 정밀도 / 재현율 | 16:9 356장 AP (APs) | 정밀도 / 재현율 |
|---|---|---|---|---|
| 배포 YOLO26s 640×640 (09-17 — 11-05 에 낸 검출) | 0.6658 (0.388 · 0.747) | 0.924 / 0.813 | 0.6306 (0.502) | 0.902 / 0.738 |
| YOLO26m 640×640 | 0.7024 (0.395 · 0.778) | 0.927 / 0.871 | 0.6892 (0.511) | 0.929 / 0.874 |
| **YOLO26m 640×384 (새 루트 모델)** | **0.6942** (0.340 · 0.772) | 0.918 / 0.870 | **0.6865** (0.498) | 0.926 / 0.868 |

- 두 YOLO26m 판이 파이썬 평가기(실수 좌표 — 'YOLO 남은 여지' 표의 0.708 · 0.700, 16:9 0.696 · 0.694)보다 **똑같이** 0.006~0.007 낮다 — YoloDotNet 이 박스를 정수로 내리는 몫이다
  (배포 YOLO26s 도 실수 좌표 0.6725 → 앱 0.6658 — '앱의 쌍선형 축소'). 짝지은 박스(임계 0.3, 같은 클래스 IoU 0.5 이상 — 6,275 · 6,386쌍)의 변별 **부호 있는** 평균 차(앱 − 파이썬)도
  두 판 모두 ±0.33 px 안이라(16:9 에서 640×640 은 아래 변 +0.18 · 640×384 는 +0.33, 나머지 ±0.07) 직사각형 입력을 되돌리며 치우치는 것은 없다
- 16:9 에서 640×384 는 640×640 과 같다(−0.003). 배포 YOLO26s 보다 16:9 AP **+0.056**, 같은 임계값에서 재현율 0.738 → 0.868(정밀도 0.902 → 0.926)
- 16:9 클래스별 AP (640×384 · 배포 YOLO26s): car 0.795 · 0.716 · truck 0.776 · 0.725 · bus 0.904 · 0.882 · motorcycle 0.609 · 0.559 · scooter 0.620 · 0.561 · person 0.624 · 0.483 · bicycle 0.477 · 0.490 — bicycle 만 조금 낮다
- 16:9 가 아닌 이미지(617장)에선 640×384 가 640×640 보다 작은 물체에서 잃는다(전체 APs 0.395 → 0.340) — 위 '대가'
- 같은 입력을 두 번 넣으면 비트 단위로 같다(앞 25장 × 두 모델)
- 주의: 배포 YOLO26s 는 이 백업과 조금 다른 판의 데이터로 학습됐을 수 있다(위 '데이터셋 백업') — 이 비교에서 배포 모델이 불리하거나 유리할 수 있다

**속도**
- `session.Run`(NpuBench, test1~5 100장): GPU 중앙 8.82 ms(YOLO26s 640×640 6.2 ms) · NPU 20.37 ms(15.1 ms)
- 앱의 파일 재생 처리 FPS(`tests/ui/Measure-Fps.ps1`, test1.mp4 720p, Release 단일 파일, 5회씩 번갈아 두 번): 배포본(YOLO26s) **92.5 · 90.5** → 새 모델 **77.5 · 78.0** — 프레임당 약 2 ms 더 든다(30·60fps 영상엔 여유가 있다)

```powershell
# 루트 모델 바꾸기 — y26m-384.onnx 는 위 'YOLO 남은 여지'의 내보내기 명령(y26m · --imgsz 384 640)으로 만든 것
Copy-Item <out\step2_models\y26m-384.onnx> best.onnx
pwsh tools\Make-NpuModel.ps1
# 앱 경로 검출 (GPU, 약 3분) → 채점 (16:9 만이면 --subset wide)
experiments\TrackerCheck\bin\Release\net10.0-windows10.0.19041.0\TrackerCheck.exe testsplit '\wsl.localhost\Ubuntu\home\chisu\rfdetr_work\data\visible' `
    experiments\TrackerCheck\out\testsplit_y26m 'y26m=<y26m.onnx>' 'y26m384=<y26m-384.onnx>'
uv run --no-project --python 3.12 --with onnxruntime --with pycocotools --with opencv-python --with numpy experiments/RfDetrBench/step2/eval_test.py `
    --data '\wsl.localhost\Ubuntu\home\chisu\rfdetr_work\data\visible' --out experiments\RfDetrBench\out\eval_app_y26m `
    --app-json 'app_y26m:experiments\TrackerCheck\out\testsplit_y26m\app_y26m_prelb.json' --app-json 'app_y26m384:experiments\TrackerCheck\out\testsplit_y26m\app_y26m384_prelb.json'
```

## 8클래스 YOLO26m 으로 (2026-11-08 — 사용자 결정)

라벨링 도구(`E:\local_auto_labeling`)가 11-08 데이터(8클래스 — 09-20 pickup 추가 · 11-02 van 을 car 로 합침, 승인 5,203장 · train 3,725 / val 729 / test 749)로
2단계 YOLO26m 과 같은 설정(640 · batch 10 · 100 epoch · optimizer auto · cos_lr · close_mosaic 10 · seed 0)으로 학습한 운영 가중치 `models/best.pt`
(= `best_y26m_8cls.pt`, 42.0 MB, best = epoch 84 · val mAP50-95 0.6934)를 사용자가 앱 모델로 골랐다.

**한 일**
- 내보내기: 위 '3단계'와 같은 명령(`export_trained.py yolo <best.pt> y26m8-384 --imgsz 384 640` — Ultralytics 8.4.142 `nms=False`, Windows CPU 31초) → `out/step2_models/y26m8-384.onnx`
  81.7 MB, MD5 `AB56CED7…`, 출력 `[1,300,6]` · 입력 `[1,3,384,640]` · opset 18 · 노드 418개(7클래스 판과 같다), 메타데이터 `imgsz [384, 640]` · `end2end True` · 날짜 2026-11-08 ·
  `names` 8개 — 앞 7개는 7클래스 판과 같은 번호이고 pickup 이 7번(라벨링 도구는 클래스를 뒤에만 붙인다)
- 루트 `best.onnx` ← 그 파일, NPU 짝 다시 만들기(`tools\Make-NpuModel.ps1` — 보정 test1~5 100장, 29초, 21.4 MB, MD5 `546567CB…`)
- 앱: pickup 을 관심 클래스(`TargetClasses`)·차량 묶음(`DuplicateBoxes` — 중복 박스·추적기·유고 규칙이 쓴다)·범례 순서(truck 바로 아래)·박스 색(`Box.pickup` #F09CF0)에 더했다.
  TrackerCheck 사본 둘과 이 폴더 `eval_test.py` 의 차량 묶음, UI 테스트의 차량 라벨 패턴도 같이. `yolo_video_check.py` 의 `NAMES`(7클래스 데이터 순서)는 그대로 — 7클래스 모델들(y26s 등)용이다
- 같은 날 사용자 승인으로 배포 폴더(`publish`)도 이 모델·짝으로 바꾸고 exe 를 다시 배포했다(`79a041a`) — 7클래스 판(`94A26C12…`)과 짝은 `best.yolo26m-1105.onnx` · `.npu.onnx` 로 보관

**정확도** — 다시 재지 않았다. 라벨링 도구 쪽 test 749장 비교(`E:\local_auto_labeling\docs\experiments.md` '8클래스 첫 학습' — Ultralytics val 기본값이라
앱 경로(end-to-end)보다 0.007~0.010 높게 나온다. 이 README 의 시험 분할 숫자와는 데이터도 달라 견주지 말 것):

| test (장 / 박스) | 평균 | 7클래스 YOLO26m (지금까지 앱) | **8클래스 YOLO26m** |
|---|---|---|---|
| 전체 (749 / 4,192) | pickup 뺀 7 | 0.6957 | **0.7398** |
| | pickup 과 상관없는 5 | 0.6958 | **0.7084** |
| CCTV (485 / 3,072) | pickup 뺀 7 | 0.6986 | **0.7367** |
| | pickup 과 상관없는 5 | 0.6942 | **0.6985** |

- 큰 차이는 truck(0.563 → 0.804)과 pickup(0 → 0.606)이다 — 7클래스 모델은 pickup 을 truck·car 로 잡아 truck 정밀도가 0.53~0.57 이었다(8클래스 0.83).
  pickup 과 상관없는 클래스는 CCTV 에서 클래스마다 오르내린다(bicycle −0.005 · motorcycle −0.008 · person −0.001 · bus +0.016 · scooter +0.020)
- 앱 경로로 다시 재려면 11-08 데이터셋을 먼저 백업해야 한다(라벨링 도구의 'WSL 학습'이 `~/datasets/visible` 을 덮어쓴다 — 위 '데이터셋'). 7클래스 모델과 견줄 땐 pickup 을 빼거나 truck·car 로 합쳐야 한다
