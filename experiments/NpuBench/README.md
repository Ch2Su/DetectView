# NpuBench — best.onnx 를 AMD NPU 로 돌려 본 실험 (2026-10-24)

본 앱(DetectView)과 따로 빌드하는 콘솔 프로젝트다. 만들 때(2026-10-24 오전)는 본 앱이 ONNX Runtime DirectML 1.23 이라
Windows ML(자체 `onnxruntime.dll` 1.27)과 한 프로세스에 섞을 수 없어서 따로 뒀다. 지금은 본 앱도 Windows ML 런타임이다
(CLAUDE.md "절대 건드리면 안 되는 것들" 1번). NPU 용 짝 모델은 `tools\Make-NpuModel.ps1` 로 만든다 (아래 "결과 2", "재현").

## 환경

| 항목 | 값 |
|---|---|
| CPU / NPU | AMD Ryzen AI 5 340, 내장 XDNA 2 NPU (`NPU Compute Accelerator Device`, 드라이버 32.0.20102.4480) |
| GPU (기준) | NVIDIA GeForce RTX 5060 Laptop — DirectML |
| OS | Windows 11 빌드 26300 |
| 런타임 | Windows ML `Microsoft.Windows.AI.MachineLearning` 2.4.89 (ONNX Runtime 1.27.1, self-contained) |
| NPU EP | VitisAI EP 1.8.75 (`Microsoft.WinML.AMD.NPU.EP.Framework.2`, EP 카탈로그가 내려받음). 2026-10-30 에 1.8.80 으로 바뀌었다 — 이 문서의 수치는 1.8.75 기준 |
| 모델 | 루트 `best.onnx` — YOLO26s 7클래스, 2026-09-11 학습, 입력 `[1,3,640,640]`, 출력 `[1,300,6]` |
| 프레임 | `publish/test1~5.mp4` 각 20장 = 100장 (영상 안 (i+0.5)/20 위치). 양자화 보정은 i/20 위치 100장 — 서로 겹치지 않음 |

모든 백엔드에 **같은 입력 텐서**를 넣고 `session.Run` 시간만 잰다. 전처리·후처리는 빼고 잰다.
일치도는 GPU 결과를 기준으로 한다: 신뢰도 ≥ 0.3(본 앱 `ConfidenceThreshold`), 같은 클래스, IoU ≥ 0.5 를 IoU 큰 순으로 1:1 로 짝짓는다.
전처리(평가 입력·양자화 보정 모두)는 오전엔 Ultralytics 식(선형 보간·회색 114 여백)이었고, 오후부터 **본 앱 YoloDotNet 과 같게**
(비율 유지·가운데·Nearest·검은 여백) 바꿨다 — 앱이 NPU 모델에 넣을 입력이 바로 그것이라서. 2026-11-05 본 앱이 쌍선형으로 바꿔 여기도 쌍선형이다(결과 3).

## 결과 1 — 방식 비교 (오전, Ultralytics 식 전처리)

| 백엔드 · 모델 | 추론 평균 / p95 | 첫 세션 (컴파일) | 재생성 (캐시) | GPU 대비 재현율 / 정밀도 | 신뢰도 차 평균 |
|---|---|---|---|---|---|
| **GPU** DirectML · FP32 | **6.0 / 6.7 ms** | 0.8 s | 0.7 s | (기준) | — |
| **NPU** · A16W8 | **15.4 / 15.8 ms** | 57 s | 0.8 s | **92.3% / 95.3%** | 0.062 |
| NPU · A8W8 채널별 + Percentile | 12.5 / 13.4 ms | 66 s | 0.6 s | 79.5% / 96.5% | 0.141 |
| NPU · A8W8 Percentile | 12.2 / 13.1 ms | 65 s | 0.6 s | 72.2% / 95.2% | 0.138 |
| NPU · A8W8 MinMax | 11.8 / 12.7 ms | 55 s | 0.6 s | 14.5% / 100% | 0.408 |
| NPU · FP32 (EP 가 BF16 으로 자동 변환) | — | **실패** (1.5시간 뒤 종료) | — | — | — |
| CPU · A16W8 (참고) | 128 ms | 0.2 s | 0.2 s | 93.5% / 94.3% | 0.053 |

- **NPU 에 올라간 비율**: 양자화 모델은 모두 노드 1460개 중 **NPU 1424개**다. CPU 29개는 end-to-end 꼬리(TopK·GatherElements·Tile 등, 1회 1.0~1.2 ms)이고, 경계 Q/DQ 7개도 CPU 에서 돈다
  (`XLNX_ONNX_EP_REPORT_FILE` 보고서의 `deviceStat` 와 ORT 프로파일이 같은 결과).
  FP32 분할 분석에서도 384개 연산 중 368개, 연산량으로는 100% 를 NPU 에 올리려 했다
- **A16W8 영상별** (GPU 대비 재현율 / 정밀도): test4 97.4/92.1 · test5 97.4/100 · test1 87.9/96.9 · test2 94.0/97.6 · test3 85.4/96.1.
  작은 물체(640 입력 기준 32×32 미만)는 93.0% 를 잡았다. **놓친 103개와 더 잡은 60개는 모두 반대쪽에서 신뢰도 0.05~0.3 이었다** (임계값 0.3 근처에서 오간 것뿐)
- **NPU 실행 자체의 오차**: 같은 A16W8 모델을 CPU 로 돌린 결과와 비교하면 재현율 96.7%, 정밀도 99.1%, 신뢰도 차 0.018 이다. 정확도 손실의 대부분은 양자화에서 나온다
- 1회당 CPU 시간: NPU 0.8~3.4 ms, GPU 0.6~0.9 ms (실행마다 흔들림이 크다), CPU 추론 약 510 ms (여러 코어 사용)
- 전력은 재지 않았다

### 해석
- 속도는 **GPU 가 약 2.5배 빠르다** (6.0 vs 15.4 ms). NPU 의 이점은 속도가 아니라 GPU 를 비워 두는 것과 배터리다.
  그래도 15.4 ms ≈ 65 FPS 라서 실시간 HUD 에는 충분하다
- 여기 GPU 6.0 ms 는 `session.Run` 만 잰 값이다. CLAUDE.md 의 "추론 ~11 ms"는 YoloDotNet(ORT 1.23) 경로 전체라 직접 비교할 수 없다
- 정확도는 A16W8 이 실용 수준이다. A8W8 은 이 모델에서 신뢰도가 체계적으로 떨어진다

## 결과 2 — 짝 모델 `best.npu.onnx` (오후, 앱과 같은 전처리)

`tools\Make-NpuModel.ps1` 로 만든 모델. 보정은 test1~5 각 20장(i/20 위치), 평가는 다른 100장((i+0.5)/20 위치).

| NPU 모델 | 추론 평균 / p95 | 첫 세션 (컴파일) | 재생성 | GPU 대비 재현율 / 정밀도 | 신뢰도 차 | 작은 물체 |
|---|---|---|---|---|---|---|
| 루트 `best.npu.onnx` · **A16W8 채널별 (기본)** | **16.3 / 16.9 ms** | 80 s | 1.2 s | **96.0% / 99.1%** | 0.018 | 95.8% |
| 루트 · A16W8 텐서별 | 16.1 / 16.7 ms | 69 s | 1.0 s | 92.0% / 96.5% | 0.054 | 92.4% |
| 배포 모델(09-17 visible)의 짝 · **A16W8 채널별** | **15.5 / 16.1 ms** | 51 s | 0.6 s | **95.8% / 98.7%** | 0.025 | 94.8% |
| 배포 모델의 짝 · A16W8 텐서별 | 16.0 / 16.8 ms | 54 s | 0.6 s | 89.8% / 95.9% | 0.066 | 88.3% |

- GPU(DirectML, FP32)는 같은 실행들에서 6.1~6.5 ms.
- **가중치를 채널별 스케일로 양자화하면 속도는 그대로이고 정확도만 오른다**. 놓친 박스가 절반 이하로 준다.
  CPU 로 돌린 같은 모델은 GPU 대비 98.9% / 99.0%(루트)였다 — 이제 남은 차이의 대부분은 양자화가 아니라 NPU 실행 쪽에서 나온다 (원인은 확인 안 함)
- 영상별 (채널별, 재현율 / 정밀도):
  - 루트: test4 96.0/99.3 · test5 100/100 · test1 92.0/99.2 · test2 97.5/98.1 · test3 95.5/99.7
  - 배포: test4 95.2/98.9 · test5 97.1/100 · test1 96.0/98.2 · test2 95.1/99.4 · test3 97.2/97.8
  - 배포 모델의 test5(갓길 정차 기준 영상)는 텐서별이면 82.9% / 87.9% 였다
- 어긋난 박스는 이번에도 거의 전부 반대쪽에서 신뢰도 0.05~0.3 이었다 (루트 52/52, 배포 53/53)
- 오전(Ultralytics 식 전처리) A16W8 텐서별 92.3% / 95.3% 와 오후 92.0% / 96.5% 가 비슷하다 — 전처리를 앱에 맞춘 것 자체로는 정확도가 크게 변하지 않았다

## 결과 3 — 앱이 쌍선형으로 줄인 뒤 (2026-11-05, 루트 모델 · VitisAI EP 1.8.80)

본 앱이 YOLO 입력을 YoloDotNet 기본값 Nearest 에서 쌍선형으로 바꿨다(CLAUDE.md '화면 HUD' 의 '입력 축소' — 지금은 앱도 `YoloLetterbox.cs` 에서 이 README 와 같은 OpenCV `INTER_LINEAR` 레터박스를 만든다). 평가 입력(`Letterbox`)과
양자화 보정(`tools/npu_quantize.py`)을 `INTER_LINEAR` 로 바꿔 짝을 다시 만들었다 — 루트 `best.npu.onnx` MD5 `46098AE1…` → `0B58A84E…`, 메타데이터에 `npu_resize = linear`.
옛 스크립트로 옛 짝을 다시 만들면 바이트 단위로 같아서(MD5 일치), 새 짝의 차이는 보정 입력의 축소 방식만의 것이다. 두 짝을 **같은 쌍선형 평가 입력**에서 견줬다 (GPU 기준 검출 1318개):

| NPU 모델 | 추론 평균 / p95 | 첫 세션 (컴파일) | GPU 대비 재현율 / 정밀도 | 신뢰도 차 | 작은 물체 | 놓친 / 더 잡은 |
|---|---|---|---|---|---|---|
| **새 짝 — 쌍선형 보정** (`0B58A84E…`) | 15.2 / 15.7 ms | 51 s | **97.5% / 98.5%** | 0.018 | 97.3% | 33 / 19 |
| 옛 짝 — Nearest 보정 (`46098AE1…`) | 15.2 / 15.7 ms | 51 s | 97.1% / 98.5% | 0.021 | 96.8% | 38 / 19 |
| (참고) CPU 로 돌린 새 짝 · 옛 짝 | 104 · 102 ms | — | 98.9% / 98.4% · 98.8% / 98.3% | 0.010 | 99.1% · 99.0% | |

처음 잰 두 실행(09:26~09:32)은 다른 세션의 GPU 평가와 겹쳐 옛 짝의 컴파일 75 s · CPU 145 ms · GPU p95 18 ms 로 나왔다 — 위 시간은 컴파일 캐시를 비우고 따로 다시 잰 값이다.
정확도 열은 두 번 모두 숫자까지 같았다(결정적).

- 보정을 쌍선형으로 바꾼 몫은 작지만 같은 쪽이다(재현율 +0.4%p, 작은 물체 +0.5%p) — 옛 짝을 쌍선형 앱에서 그대로 써도 크게 틀리진 않는다(배포 폴더의 짝이 그렇다 — 다음 배포 때 다시 만든다)
- 결과 2(96.0% / 99.1%)와 직접 견주지 말 것 — 평가 입력이 Nearest 에서 쌍선형으로 바뀌어 GPU 기준 검출부터 다르다(결과 2 의 GPU 기준과 같은 프레임이지만 다른 입력)
- 어긋난 박스는 이번에도 모두 반대쪽에서 신뢰도 0.3 바로 아래였다(놓친 33/33 · 더 잡은 19/19)
- GPU(DirectML, FP32) 중앙 6.2 ms · NPU 15.1 ms — 두 짝이 같다. 축소 방식과 상관없다(입력 텐서만 다르다)

## 알게 된 것 (함정)

1. **FP32 모델을 그대로 넣으면 실패한다.** EP 가 BF16 으로 바꿔(VAIML) 분할 분석까지는 하지만, NPU 커널 빌드(aiecompiler 의 Peano)가
   `-load-vfs C:\PROGRA~1\WindowsApps\` 처럼 **잘린 경로**로 DLL 을 찾다가 실패한다. 그 뒤 EP 가 프로세스를 끝낸다(종료 코드 255, 약 1.5시간 뒤).
   Windows ML 이 MSIX 로 설치한 EP 경로 문제로 보이며 코드로는 우회하지 못했다. 그래서 양자화(QDQ) 모델을 쓴다 — 이쪽은 다른 컴파일러(X2, `AMD_AIE2P_4x8`)를 쓰고 1분 안에 끝난다.
   NpuBench 는 NPU 모델 메타데이터에 `npu_quant` 가 없으면 NPU 단계를 건너뛴다 — 이 경로를 일부러 볼 때만 `--allow-fp32-npu` (이슈 #6, 아래 "재현")
2. **`cache_key` 를 꼭 모델 파일 해시로 줘야 한다.** 기본 키는 그래프 구조만 보는 듯하다. 그래서 가중치·스케일만 다른 양자화 변형끼리 같은 키가 나오고,
   **처음 컴파일한 모델을 그대로 재사용**한다. 실측: a8w8-pct 를 넣어도 a8w8 결과가 나왔고, a16w8 은 INT8 로 컴파일된 옛 모델에 16비트가 들어가
   `Not support data type. from: (0,16) to (0,8)` 로 종료됐다. 본 앱에 넣을 때도 모델을 바꾸면(CLAUDE.md: 사용자가 수시로 바꾼다) 같은 일이 생긴다.
   같은 이유로 버전도 키에 넣는다: NpuBench 의 키는 `{NPU 모델 MD5}_{VitisAI EP 패키지 버전}_{ONNX Runtime 버전}`(예: `46098AE1…_1.8.80.0_1.27.1`,
   실행 때 `[EP 장치]` 끝에 캐시 유무와 함께 찍힌다)이다 — EP 가 Windows Update 로 바뀌거나 런타임을 올리면 키가 바뀌어 다시 컴파일한다(이슈 #5).
   키가 MD5 뿐이던 때의 `out/vaip_cache/<MD5>` 폴더는 더 쓰이지 않으니 지워도 된다 (그래서 바꾼 뒤 첫 실행은 NPU 를 한 번 다시 컴파일한다)
3. **A8W8 은 MinMax 보정이면 망가진다** (신뢰도 −0.4). Percentile(99.999%) 보정이 가장 효과가 컸고(14% → 71%), 채널별 가중치가 조금 더 올렸다(→ 80%).
   YOLO 에는 AMD 도 A16W8 을 권한다. 16비트 Q/DQ 는 opset < 21 이라 `com.microsoft` 도메인으로 나가는데, VitisAI EP 가 그대로 받는다
4. 검출 머리의 **디코딩 꼬리 35개 노드는 실수로 둔다** (박스 좌표 0~640 과 확률 0~1 이 한 텐서에 섞임). 머리 안의 `Sigmoid`·`Mul` 대부분은 Conv 뒤 SiLU 라서 같이 빼면 NPU 구간이 끊긴다 (`tools/npu_quantize.py` `decode_tail`, 머리 모듈은 `/model.N/` 의 최대 N 으로 자동으로 찾는다)
5. ORT `quant_pre_process` 는 `skip_symbolic_shape=True` 가 필요하다. 기호 형상 추론이 TopK 에서 죽는다
6. EP 카탈로그 첫 `EnsureAndRegisterCertifiedAsync` 는 EP 를 내려받느라 **8분** 걸렸다. 그 뒤로는 5초다
7. ORT C# 프로파일링은 `ProfileOutputPathPrefix` 를 `EnableProfiling` **보다 먼저** 지정해야 한다. 반대로 하면 작업 디렉터리에 떨어진다
8. NPU 세션의 `EndProfiling` 은 VitisAI 쪽 이벤트가 없다는 오류 로그를 남기지만, 무해하다 (CPU·VitisAI 노드 배치는 정상 기록)
9. AMD Quark 0.13 의 ONNX 양자화기는 불러올 때 torch 를 요구해서 쓰지 않았다. ORT 양자화기로 충분했다
10. **A16W8 에도 가중치 채널별 스케일을 쓴다** — 텐서별보다 GPU 대비 재현율이 4~6%p 오르고 속도는 같다 (결과 2). VitisAI EP 가 INT16 활성값 + 채널별 INT8 가중치를 그대로 컴파일한다(노드 배치는 텐서별과 같음)
11. 보정 전처리는 앱(YoloDotNet `ResizeImageProportional`)과 같게 한다: 크기 `(int)(w·s + 0.5)`, 가운데, 쌍선형(`INTER_LINEAR` — 2026-11-05 부터, 결과 3), 검은 여백(`SKColors.Empty`).
    그 전엔 앱이 Nearest 라 `INTER_NEAREST_EXACT`(OpenCvSharp 에선 이름이 없어 `(InterpolationFlags)6`)로 맞췄다 — OpenCV 의 `INTER_NEAREST` 는 화소 중심을 맞추지 않아 Skia 와 한 칸씩 어긋난다.
    **다만 `NEAREST_EXACT` 도 Skia 의 Nearest 와 똑같지는 않았다**(데이터셋 시험 분할 973장 중 540장이 달랐다, `TrackerCheck testsplit` — 정확히 2배 축소면 Skia 는 2×2 묶음의 왼쪽 위,
    `NEAREST_EXACT` 는 오른쪽 아래 화소를 고른다, `TrackerCheck inputbench`. 테스트 영상 1280×720 이 바로 2배 축소라 10-24~11-05 의 Nearest 평가 입력은 앱과 한 화소씩 어긋나 있었다).
    쌍선형은 Skia 와 OpenCV 가 화소마다 많아야 1 다르다
12. 양자화는 결정적이다 — 같은 원본·보정 데이터·방식이면 결과 파일이 바이트 단위로 같았다(MD5 일치). 그래서 여기서 잰 숫자가 저장소의 `best.npu.onnx` 에 그대로 해당한다

## 재현

```powershell
# 1) 짝 모델 만들기 (python 은 PATH 에 없다 → uv 가 격리 환경을 만든다. 약 30~40초)
pwsh tools\Make-NpuModel.ps1                                    # 루트 best.onnx → best.npu.onnx
pwsh tools\Make-NpuModel.ps1 -Model publish\best.onnx   # 배포 폴더 모델 → 그 옆 best.npu.onnx
# 2) 측정까지 한 번에 (GPU → CPU → NPU 순. NPU 첫 컴파일 약 1분)
pwsh tools\Make-NpuModel.ps1 -Model publish\best.onnx -Bench
# (선택) 방식 변형을 CPU 에서 먼저 걸러 보기 — 기준 FP32(CPU) 대비. 기준은 --ref (기본: 루트 best.onnx)
uv run --no-project --python 3.12 --with onnxruntime==1.23.2 --with onnx --with numpy --with opencv-python-headless tools\npu_quantize.py --model best.onnx --out experiments\NpuBench\out\best.a8w8.onnx --mode a8w8
uv run --no-project --python 3.12 --with onnxruntime==1.23.2 --with onnx --with numpy --with opencv-python-headless experiments\NpuBench\screen.py best.npu.onnx experiments\NpuBench\out\best.a8w8.onnx
uv run --no-project --python 3.12 --with onnxruntime==1.23.2 --with onnx --with numpy --with opencv-python-headless experiments\NpuBench\screen.py --ref publish\best.onnx publish\best.npu.onnx
```

`screen.py` 는 변형마다 원본 MD5(`npu_source_md5`)를 `--ref` 파일의 MD5 와 맞대어 보고, 다르거나 없으면 그 변형을 **건너뛴다**(종료 코드 1).
같은 폴더·루트·배포 폴더의 `best.onnx` 가운데 원본이 있으면 `--ref` 로 줄 경로를 알려 준다. 예전엔 기준이 루트 고정이라 배포 폴더 모델로 만든
변형도 경고 없이 다른 원본과 비교한 숫자가 나왔다(클래스 구성이 다르면 같은 번호가 다른 물체 — 이슈 #6).

NpuBench 를 직접 돌릴 땐 `NpuBench.exe [기준 FP32 모델] [NPU 모델] [영상당 프레임 수] [--allow-fp32-npu] [--allow-mismatch]`
(`--help` 로 설명). 기준 모델의 기본값은 루트 `best.onnx`, NPU 모델의 기본값은 **기준 옆 `<이름>.npu.onnx`**(`Make-NpuModel.ps1` 이 만드는 짝)이고,
그게 없으면 NPU 는 건너뛰고 GPU·CPU(기준 FP32)만 잰다. 벤치 전에 — EP 준비(처음엔 수 분)보다도 먼저 — NPU 모델의 메타데이터를
앱의 `CheckNpuModel` 과 같은 규칙으로 본다 (이슈 #6. 예전엔 NPU 모델 기본값이 기준 자체라 생략하면 FP32 가 NPU 로 갔다):
- `npu_quant` 가 없으면(FP32) NPU 단계를 건너뛴다. GPU·CPU 는 그대로 잰다 (함정 1). 일부러 넣으려면 `--allow-fp32-npu`
- `npu_source_md5` 가 기준 모델 MD5 와 다르거나 없으면 아무것도 재지 않고 멈춘다(종료 코드 1). 원본으로 보이는 모델이 있으면 알려 준다.
  일부러 다른 모델과 비교하려면 `--allow-mismatch` (그 숫자엔 양자화 영향과 모델 차이가 섞인다)

NPU 노드 배치 보고서가 필요하면 그 전에 `$env:XLNX_ONNX_EP_REPORT_FILE = 'vitisai_ep_report.json'`.
산출물(실험 모델·NPU 컴파일 캐시·프로파일·로그)은 `out/`(gitignore)에 쌓인다. 실패한 BF16 캐시 196 MB 등 전체 400 MB 남짓이고, 지워도 된다.

## 본 앱에 넣기 (끝 — CLAUDE.md "NPU")

- ~~ONNX Runtime 통일~~ — 끝(2026-10-24). 본 앱 런타임을 Windows ML 로 바꾸고 YoloDotNet 공급자를 직접 뒀다(`WindowsMlExecutionProvider.cs`).
  전처리·`[1,300,6]` 해석·그리기는 YoloDotNet 이 그대로 하고, NPU 는 같은 공급자에 VitisAI 장치를 넘기면 된다
- ~~짝 모델 도구~~ — 끝(2026-10-24). `tools\Make-NpuModel.ps1` → `best.npu.onnx`(메타데이터 `npu_source_md5` 로 원본과 짝 확인). 루트 것은 git 으로 추적한다
- ~~본 앱 전환~~ — 끝(2026-10-24, NPU 3~5단계). 툴바 "추론"에서 고르면 짝 확인 → VitisAI 만 준비 → 백그라운드 컴파일(그동안 GPU) → 교체.
  앱도 `cache_key` 를 NPU 모델 MD5 와 버전으로 직접 준다 (`MainWindow.Npu.cs`. NpuBench 의 키는 위 함정 2)
- 모델을 바꿀 때마다 짝을 다시 만들어야 한다 (도구 30~40초 + NPU 첫 컴파일 1분)
- 정확도를 더 올릴 후보: Quark A16W8 + AdaRound, 보정 프레임 늘리기·다양화
