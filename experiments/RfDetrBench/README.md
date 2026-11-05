# RfDetrBench — RF-DETR 를 앱의 런타임(DirectML)에서 돌려 보기

RF-DETR(Roboflow, Nano·Small·Medium·Large 는 Apache 2.0)로 YOLO26s 를 바꾸거나 모델 종류로 더할 만한지 보는 실험이다. 2단계(같은 데이터로 학습해 시험 분할에서 비교, 2026-11-04~05)는 아래 '2단계' 절.
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
  (uint8 로 중간 크기까지 줄인 뒤 float, 16비트 리사이즈, 리사이즈를 모델 그래프에 넣어 GPU 에서 등) — 해 보지 않았다

### 판단

- 앱의 런타임에서 **돌아가고 결과가 맞다** — 막힌 것은 없다(전처리는 레터박스가 아니라 정사각형 늘이기라 앱의 YoloDotNet 경로와 따로 써야 한다)
- 같은 COCO 정확도의 YOLO26 보다 FP32 로 20~50% 느리다 → 바꿀 이유는 **우리 데이터로 파인튜닝했을 때의 정확도**에서 나와야 한다 (2단계)
- 2단계 후보는 **RF-DETR-S**(FP16 9.1 ms). 비교 상대는 앱의 YOLO26s 와 같은 정확도 급인 **YOLO26m** — 셋 다 같은 데이터셋 백업으로 학습해야 공정하다(배포 YOLO26s 는 09-17 에 조금 다른 판으로 학습)
- 확인 안 한 것: NPU(VitisAI — FP32 는 이 PC 에서 BF16 변환이 실패하는 경로(NpuBench README 함정 1)라 시도하지 않았고, 양자화한 트랜스포머도 안 해 봤다) ·
  같은 입력에서 DirectML 출력이 매번 같은지(HUD 의 '새 화면 고리'가 기대는 성질 — CLAUDE.md '화면 HUD') · YOLO FP16 · Large(704)

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
    `train_rfdetr.py` 가 컴파일러가 없으면 그 라이브러리의 SciPy(CPU) 경로로 보낸다(DETR 원래 방식, 같은 최적 짝)
  - **`batch_size='auto'` 가 WSL 의 CUDA 를 죽인다** — 배치를 2, 4, 8… 로 키우며 메모리가 넘칠 때까지 시험하는데, 한계에서 메모리 부족 대신 `CUDA error: device not ready` 가 나고
    컨텍스트가 망가져 뒤이은 `empty_cache` 도 out of memory 로 죽었다(첫 시도, 3분). rfdetr 의 'auto' 가 맞추는 실효 배치 16 을 고정 4 × 누적 4 로 준다
  - 런처가 띄운 `wsl.exe` 는 Windows 의 현재 폴더(저장소)에서 시작해서, Ultralytics 의 AMP 점검이 `yolo26n.pt` 를 저장소의 `weights/` 에 내려받았다 → 드라이버가 `~/rfdetr_work` 로 옮겨 간다
  - Windows 런처는 `wsl.exe` 출력을 드라이버가 끝날 때에야 받는다(`out/step2_driver.log`) — 도는 동안의 진행은 WSL 쪽 `driver.log` 를 본다
- 드라이버는 한 모델이 실패해도 다음 모델로 넘어간다(밤새 돌 때 하나 때문에 다 서지 않게)
- **RF-DETR-N**(`rfdetr_n` = `train_rfdetr.py --size n`, 2단계 뒤에 더함)은 같은 조리법(배치 4 × 누적 4)에서 epoch 당 약 2.8분이다.
  S 는 3.5분이었다. GPU 사용률 45%·메모리 3.4 GB 라 CPU 매칭이 병목이다. 100 epoch 이면 약 4.7시간.
  11-05 10:48 에 시작했다가 사용자 요청으로 첫 epoch(검증 mAP50-95 0.564) 뒤 멈췄다(Ctrl+C — Lightning 이 정리하고 끝냈다).
  그 결과는 `~/rfdetr_work/runs/rfdetr_n_stopped_1105`(로그 `logs/rfdetr_n_stopped_1105.log`)로 옮겨 두었다. 다시 할 땐 위 명령에 `rfdetr_n` 을 주고 처음부터 돌린다.
  이어서 하려면 `train_rfdetr.py --size n --resume <그 폴더>/last.ckpt` 를 직접 실행한다(rfdetr 1.11.2 가 Lightning `Trainer.fit(ckpt_path=...)` 로 넘기고 `metrics.csv` 기록도 이어 쓴다)

```powershell
# 내보내기 (Windows, CPU) — out/step2_models/<이름>.onnx, RF-DETR 는 <이름>-fp16.onnx 도
uv run --no-project --python 3.12 --with "rfdetr[onnx]==1.11.2" --with onnxruntime experiments/RfDetrBench/step2/export_trained.py rfdetr "\\wsl.localhost\Ubuntu\home\chisu\rfdetr_work\runs\rfdetr_s\checkpoint_best_total.pth" rfdetr_s
uv run --no-project --python 3.12 --with "ultralytics==8.4.142" --with onnx --with onnxslim --with onnxruntime experiments/RfDetrBench/step2/export_trained.py yolo "\\wsl.localhost\Ubuntu\home\chisu\rfdetr_work\runs\yolo\y26s\weights\best.pt" y26s
# (y26m 도 같게 — runs\yolo\y26m\weights\best.pt)
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

YOLO26s·RF-DETR-S 는 중간(49·33 epoch)에 검증 최고를 찍고 그 뒤로는 학습 손실만 내려갔다(YOLO26m 은 75 epoch). Ultralytics 8.4.142 의 `best.pt` 는 검증 mAP50-95 가 가장 높은 epoch 다
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

FP32 모델은 CPU 로 쟀다. DirectML(`--ep dml`, NVIDIA)로 다시 재도 RF-DETR-S · YOLO26s · YOLO26m(Nearest) 모두 **넷째 자리까지 같았다**.

클래스별 AP (YOLO 는 쌍선형):

| 클래스 (시험 정답 수) | YOLO26s | YOLO26m | RF-DETR-S |
|---|---|---|---|
| car (1,591) | 0.799 | **0.832** | 0.815 |
| truck (244) | 0.716 | 0.792 | **0.802** |
| bus (149) | 0.849 | 0.868 | **0.903** |
| motorcycle (205) | 0.532 | 0.610 | **0.614** |
| scooter (156) | 0.537 | 0.556 | **0.675** |
| bicycle (198) | 0.548 | 0.619 | **0.667** |
| person (3,928) | 0.643 | 0.679 | **0.715** |

- **RF-DETR-S 가 전체로는 가장 정확하다**: AP 0.742 — 쌍선형으로 잰 YOLO26m 보다 +0.034, YOLO26s 보다 +0.082(앱 그대로의 Nearest 와 견주면 +0.065 · +0.102).
  앱 임계 0.3 에서 재현율 0.920(YOLO26m 0.875 · YOLO26s 0.841), 정밀도는 0.886 으로 YOLO(0.88~0.90)와 비슷하다
- **앞서는 몫은 두 바퀴·사람·버스에서 나온다**(YOLO26m 대비 scooter +0.12 · bicycle +0.05 · person +0.04 · bus +0.035). **car 는 YOLO26m 이 낫고**(0.832 vs 0.815) truck·motorcycle 은 비슷하다.
  **작은 물체(APs)도 YOLO26m 쌍선형이 낫다**(0.430 vs 0.403) — 도로 CCTV 의 먼 차가 여기 든다
- **FP16 은 정확도를 잃지 않는다**: DirectML 에서 FP32 0.7417 → FP16 0.7416(재현율 0.920 → 0.919). 1단계의 FP16 속도(9.1 ms)를 그대로 쓸 수 있다
- **앱의 Nearest 축소가 YOLO 의 AP 를 0.02~0.03 깎는다**: YOLO26s 0.640 → 0.660 · 배포 0.653 → 0.672 · YOLO26m 0.677 → 0.708(작은 물체 AP +0.02~0.045). Ultralytics 8.4.142 는 학습·검증 모두 쌍선형(`INTER_LINEAR`)으로 줄이는데
  앱(YoloDotNet 기본값)은 Nearest 라 학습 때와 다른 입력을 넣는 셈이다. YoloDotNet 4.2.0 `YoloOptions.SamplingOptions` 로 바꿀 수 있어 보인다 — 앱엔 안 해 봤다(NPU 짝의 양자화 보정도 Nearest 기준이라 같이 다뤄야 한다)
- **교차 확인 — 각 라이브러리의 자체 평가기**(`crosscheck_native.py`, WSL CPU): Ultralytics val(test) YOLO26s **0.674** · YOLO26m **0.725**(mAP50 0.870 · 0.897), rfdetr `evaluate(split="test")` RF-DETR-S **0.751**(mAP50 0.925).
  RF-DETR-S 와의 차이는 0.077 · 0.026 — 우리 평가기(쌍선형)의 0.082 · 0.034 와 같은 쪽·비슷한 크기다. 우리 평가기는 자체 평가기보다 YOLO26s 0.014 · YOLO26m 0.017 · RF-DETR-S 0.009 낮게 잰다
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
| 〃 Nearest 축소 (앱 그대로) | 0.640 | 0.677 | 축소 −0.020 · −0.031 |

- **Ultralytics 8.4.142 의 val 은 `nms` 를 안 주면 YOLO26 의 one-to-many 출력에 NMS 를 걸어 잰다** — `engine/validator.py` 가 `end2end=self.args.nms is False` 이고 기본값이 None 이다.
  학습 중 검증과 `best.pt` 고르기도 그렇다. 그래서 **학습 로그의 mAP 는 앱이 쓰는 end-to-end 출력보다 0.007~0.010 높게 나온다**. 앱과 같은 것을 재려면 `nms=False`
- 앱의 YOLO 경로가 Ultralytics 기본 val 보다 잃는 몫은 YOLO26s 0.034 · YOLO26m 0.048 이고, 대부분 Nearest 축소다. 되찾을 여지는 셋이다.
  ① 쌍선형 축소(+0.02~0.03).
  ② NMS 를 넣어 내보내기(Ultralytics `nms=True`, +0.007~0.010). DirectML 에서 도는지, 3번 항목의 중복 박스가 어떻게 바뀌는지는 안 봤다.
  ③ 16:9 영상에 640×384 입력(+0.002~0.008, 계산량 40% 적음). 시험 분할은 비율이 섞여 있어 영상에선 따로 재야 한다

### 판단 (2단계)

- 3단계 기준(이길 때만 앱에)으로는 **RF-DETR-S 가 이긴다** — 같은 데이터·각자의 기본 조리법에서 전체 AP·재현율이 셋 중 가장 높고, FP16 이면 1단계 속도로 YOLO26m(FP32 14.0 ms — YOLO FP16 은 DirectML 에서 못 돌림)보다 빠르다(9.1 ms).
  지금 앱 경로(배포 YOLO26s · Nearest, AP 0.653)보다 AP +0.09
- 다만 이득이 고르지 않다: car·작은 물체는 YOLO26m(쌍선형)이 같거나 낫다 — 유고 규칙이 보는 차량만 보면 YOLO26m 과의 차이가 작다
- 앱에 넣으려면(1단계에서 정리): YoloDotNet 밖의 새 추론 경로(정사각형 늘이기 전처리 — float 경로가 CPU 5~7 ms 라 줄여야 한다 · 후처리), 모델 파일 57 MB(FP16), NPU 는 YOLO 만(짝 모델이 YOLO 전용),
  HUD 가 기대는 결정성(같은 입력에 같은 출력) 확인
- 더 싼 길도 있다: ① 앱의 YOLO 축소를 쌍선형으로(새 모델 없이 AP +0.02~0.03 — 위 표의 나머지 몫까지 되찾으면 YOLO26m 은 0.72 안팎, RF-DETR-S 와 0.02~0.03 차이)
  ② YOLO26m 으로 바꾸기(쌍선형이면 0.708, 추론은 2.2배) ③ **RF-DETR-N 을 같은 데이터로 학습**해 보기 — FP16 5.2 ms 로 지금 YOLO26s(6.4 ms)보다 빠른데 이번엔 재지 않았다(11-05 시작했다가 첫 epoch 뒤 멈춤 — 위 학습 절)
- 확인 안 한 것: 학습을 한 번씩만 했다(시드 차이 모름) · 학습률을 일찍 내리는 조리법(rfdetr `lr_drop`) · 시험 분할은 학습 데이터와 같은 출처라 **앱의 CCTV 영상(test1~5)에서의 차이** ·
  추적기·유고 규칙 위에서의 차이
