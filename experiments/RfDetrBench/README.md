# RfDetrBench — RF-DETR 를 앱의 런타임(DirectML)에서 돌려 보기

RF-DETR(Roboflow, Nano·Small·Medium·Large 는 Apache 2.0)로 YOLO26s 를 바꾸거나 모델 종류로 더할 만한지 보는 실험의 **1단계**다 (2026-11-04).
학습 없이 COCO 사전학습 가중치로, 앱과 같은 런타임 — Windows ML 의 ONNX Runtime 1.27 + DirectML(NVIDIA) — 에서

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

**조건**: 노트북이 **배터리**(균형 조정 전원 구성, 잔량 67 → 59%)였다. 10-24 NpuBench 에선 YOLO26s 가 6.1~6.5 ms 였는데 이날은 8.2~8.9 ms 라, 절대값은 30% 안팎 느리게 나왔을 것이다
(전원을 연결하고 다시 재지는 않았다). 같은 조건에서 모델끼리 견준 비율은 그대로 본다. 같은 날 세 번 돌려 회차마다 5% 안에서 같았다(덮개가 닫혀 대기 모드에 들어간 한 회차는 뺐다).

### ② 추론 시간 — `session.Run` 중앙값 (입력 복사·출력 읽기 포함, 전처리·후처리 제외. 마지막 실행의 1·2회차)

| 모델 | 입력 | COCO mAP (발표) | 중앙 ms | 앱 YOLO26s 대비 |
|---|---|---|---|---|
| YOLO26n COCO | 640 레터박스 | 40.1 | 5.26 · 5.13 | 0.6배 |
| **YOLO26s 앱** (7클래스, 루트 `best.onnx`) | 640 레터박스 | — | **8.20 · 8.29** | 1 |
| YOLO26s COCO | 640 레터박스 | 47.8 | 8.82 · 8.86 | 1.1배 |
| YOLO26m COCO | 640 레터박스 | 52.5 | 18.04 · 18.34 | 2.2배 |
| RF-DETR Nano | 384 정사각형 | 48.4 | 11.86 · 12.02 | 1.4배 |
| RF-DETR Small | 512 정사각형 | 53.0 | 24.37¹ · 20.84 | 2.5배 |
| RF-DETR Medium | 576 정사각형 | 54.7 | 27.15 · 27.10 | 3.3배 |
| RF-DETR Nano FP16 | 384 | | 6.33 · 6.24 | 0.8배 |
| RF-DETR Small FP16 | 512 | | 10.97 · 10.77 | 1.3배 |
| RF-DETR Medium FP16 | 576 | | 15.84 · 16.13 | 1.9배 |

¹ 그 회차만 세션 생성 3.5초·p95 42.7 ms 로 다른 부하가 끼었다(앞선 두 실행은 21.2~21.4). COCO mAP 는 RF-DETR README 와 Ultralytics YOLO26 문서의 end-to-end 열(앱이 end-to-end 출력을 쓴다).

- **같은 COCO 정확도끼리 보면 RF-DETR 가 15~35% 느리다**: RF-DETR-N(48.4) 11.9 ms vs YOLO26s(47.8) 8.8 ms = 1.35배, RF-DETR-S(53.0) 20.8 ms vs YOLO26m(52.5) 18.0~18.3 ms = 1.15배.
  T4·TensorRT 발표값(N 2.3 vs YOLO26s 2.5 · S 3.5 vs YOLO26m 4.7 ms)과 순서가 뒤집힌다 — DirectML FP32 에선 트랜스포머 쪽이 덜 빠르다
- **FP16 이면 RF-DETR 가 1.7~1.9배 빨라진다**(N 6.2 · S 10.8 · M 15.8 ms). YOLO 의 FP16 은 못 재서(위) FP16 끼리의 비교는 없다
- 1단계 기준(12 ms 안 통과 · 25 ms 초과 접기): FP32 는 N 경계(11.9~12.0) · S 애매(20.8) · M 접기(27.1), **FP16 은 N·S 통과**(6.2 · 10.8) · M 애매(15.8)

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
- 같은 COCO 정확도의 YOLO26 보다 FP32 로 15~35% 느리다 → 바꿀 이유는 **우리 데이터로 파인튜닝했을 때의 정확도**에서 나와야 한다 (2단계)
- 2단계 후보는 **RF-DETR-S**(FP16 10.8 ms). 비교 상대는 앱의 YOLO26s 와 같은 정확도 급인 **YOLO26m** — 셋 다 같은 데이터셋 백업으로 학습해야 공정하다(배포 YOLO26s 는 09-17 에 조금 다른 판으로 학습)
- 확인 안 한 것: 전원 연결 상태의 절대값 · NPU(VitisAI — FP32 는 이 PC 에서 BF16 변환이 실패하는 경로(NpuBench README 함정 1)라 시도하지 않았고, 양자화한 트랜스포머도 안 해 봤다) ·
  같은 입력에서 DirectML 출력이 매번 같은지(HUD 의 '새 화면 고리'가 기대는 성질 — CLAUDE.md '화면 HUD') · YOLO FP16 · Large(704)
