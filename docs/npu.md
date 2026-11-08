# 추론 장치 — NPU · TensorRT

> 2026-11-08 TensorRT 를 더하며 제목을 바꿨다(그 전엔 'NPU'). TensorRT 는 맨 끝 절 — 장치 전환의 상태 기계는 둘이 같이 쓴다.

> CLAUDE.md 에서 옮긴 절이다(2026-11-05 — CLAUDE.md 가 매 세션 컨텍스트를 너무 많이 차지해서 내용은 글자 그대로 옮기고, CLAUDE.md 에는 같은 제목으로 요약·꼭 지킬 규칙만 남겼다).
> 본문의 '위'·'아래'·'○○ 절'이 가리키는 곳은 이 문서나 CLAUDE.md, 또는 다른 `docs/` 문서다 — CLAUDE.md 의 '자세한 문서' 표에서 찾는다. 새로 알게 된 실측·경과는 여기에 더한다.

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
     이 PC 의 키: `0B58A84E6F01450D32A72C1B887C4222_1.8.80.0_1.27.20260711.1.7b7c0e2`(65자 — 2026-11-05 짝을 쌍선형으로 다시 보정해 MD5 가 `46098AE1…` 에서 바뀌었다). 쓴 키는 NPU 툴팁 끝 `컴파일 캐시 키: …` 에 보인다(테스트가 읽는다)
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
- 정확도(기본 방식 A16W8·가중치 채널별): GPU 검출 대비 루트 모델 **96.0% / 99.1%**, 배포 모델(09-17 visible) **95.8% / 98.7%**(재현율 / 정밀도 — 10-24, Nearest 입력).
  어긋난 박스는 거의 전부 신뢰도 0.3 경계 근처. 가중치 텐서별이면 92.0% / 96.5%, A8W8 은 보정 방법에 따라 14~80%.
  쌍선형 입력(11-05, 루트 모델 · 짝을 쌍선형으로 다시 보정): **97.5% / 98.5%**, 작은 물체 97.3% — 옛 짝(Nearest 보정)을 같은 입력에 넣으면 97.1% / 98.5% · 96.8% (NpuBench README 결과 3)
- **YOLO26m 640×384 짝 (2026-11-06 — 루트 모델을 바꾸며, NpuBench README 결과 4)**: 같은 도구로 43초, 21.4 MB(보정 입력도 640×384 — 도구가 모델 입력 크기를 읽는다).
  `session.Run` GPU 8.8 ms vs NPU 20.4 ms(2.3배, 첫 컴파일 64초 — NpuBench), GPU 검출 대비 **96.1% / 97.8%**, 작은 물체 94.7%.
  CPU 로 돌린 같은 짝은 98.8% / 97.5% 라 어긋남의 대부분은 NPU 실행 몫이다. 어긋난 박스는 모두 신뢰도 0.3 경계 근처
- **8클래스 YOLO26m 640×384 짝 (2026-11-08 — 루트 모델을 바꾸며, NpuBench README 결과 5)**: 29초, 21.4 MB, MD5 `546567CB…`. `session.Run` GPU 8.9 ms vs NPU 20.4 ms(7클래스 판과 같다),
  GPU 검출 대비 **96.5% / 98.6%**, 작은 물체 95.8%. 새 짝이라 NPU 첫 전환은 다시 컴파일한다(약 1분 — 캐시 키가 NPU 모델 MD5 로 바뀐다)
- **짝 모델 `best.npu.onnx`**: `pwsh tools\Make-NpuModel.ps1 [-Model <경로>] [-Bench]` 로 원본 옆에 만든다(약 30~40초, uv 로 `tools/npu_quantize.py` 실행).
  메타데이터 `npu_source_md5`(원본 파일 MD5)·`npu_quant`·`npu_calib`·`npu_resize`(보정 입력의 축소 방식 — 2026-11-05 부터 `linear`, 없으면 Nearest 로 보정한 옛 짝)가 들어간다 — 앱은 앞의 둘로 원본과 짝인지 확인한다(`CheckNpuModel` — `npu_resize` 는 보지 않는다).
  **루트 `best.npu.onnx` 는 git 으로 추적한다**(루트 `best.onnx` 의 짝). `publish\best.onnx` 를 바꾸면 **그 짝도 다시 만든다**
  (`-Model publish\best.onnx`, gitignore 쪽이라 커밋 안 됨). 양자화는 결정적이라 같은 입력이면 바이트 단위로 같은 파일이 나온다
- **실험 도구도 짝을 확인한다** (이슈 #6, 2026-10-31): `NpuBench.exe [기준] [NPU 모델] [영상당 프레임 수] [--allow-fp32-npu] [--allow-mismatch]` 의 NPU 모델 기본값은
  기준 옆 `<이름>.npu.onnx`(없으면 NPU 단계를 건너뛰고 GPU·CPU 만). 벤치 전에(EP 준비보다도 먼저) 앱의 `CheckNpuModel` 과 같은 규칙으로 본다 —
  `npu_quant` 가 없으면(FP32) NPU 단계만 건너뛰고, `npu_source_md5` 가 기준 MD5 와 다르거나 없으면 아무것도 재지 않고 종료 코드 1.
  예전엔 NPU 모델 기본값이 기준 FP32 자체라 생략하면 FP32 가 NPU 로 가서 1.5시간 뒤 프로세스가 끝났다. `screen.py` 는 `--ref`(기본 루트 `best.onnx`)와 원본이 다른 변형을 건너뛴다.
  NpuBench 의 `cache_key` 는 `{NPU 모델 MD5}_{EP 패키지 버전}_{ORT 의미 버전}` — 앱과 달리 의미 버전이지만 캐시가 자기 `out/vaip_cache` 라 섞이지 않는다
- 보정 전처리는 앱 YoloDotNet 과 같게(비율 유지·가운데·쌍선형 `INTER_LINEAR`·검은 여백 — 2026-11-05 전엔 앱처럼 Nearest) — 앱이 NPU 모델에 넣을 입력이 그것이라서. 보정 기본값은 `publish\test1~5.mp4` 각 20장,
  다른 현장 영상을 쓸 모델이면 `-Calib` 로 그 영상·이미지 폴더를 준다
- **FP32 를 그대로 넣으면 안 된다** — EP 의 BF16 자동 변환이 NPU 커널 빌드에서 잘린 경로(`C:\PROGRA~1\WindowsApps\`)로 실패하고 1.5시간 뒤 프로세스를 끝낸다
- **VitisAI `cache_key` 는 모델 파일 해시로 직접 줄 것.** 기본 키는 가중치·스케일이 달라도 같아서 옛 컴파일 결과를 재사용한다(틀린 결과·크래시 실측). 앱·NpuBench 는 여기에 EP·ORT 버전도 붙인다(위)
- Windows ML 은 자체 `onnxruntime.dll`(1.27)을 가져와 옛 DirectML 1.23 조합과 한 프로세스에 못 섞는다 → 본 앱 런타임을 Windows ML 로 바꿨다("절대 건드리면 안 되는 것들" 1번).
  NPU 도 같은 `WindowsMlExecutionProvider` 에 VitisAI 장치를 넘겨 돌린다 (레터박스는 GPU 와 같은 `YoloLetterbox` — 2026-11-05, 정규화·`[1,300,6]` 해석은 YoloDotNet 그대로)

## TensorRT — NVIDIA TensorRT for RTX (2026-11-08 — 왼쪽 패널 "추론 장치"의 'TensorRT')

사용자 요청으로 실험(`experiments/NpuBench` README 결과 6) 뒤 같은 날 앱에 넣었다. **Windows ML EP 카탈로그에 NPU 의 VitisAI 처럼 들어오는**
`NvTensorRTRTXExecutionProvider`(이 PC `Microsoft.WinML.NVIDIA.TRT-RTX.EP.Framework.2` 2.30.51 — NVIDIA RTX 30 이후에서만 장치가 나온다)라 새 NuGet 이 없다.
같은 NVIDIA GPU 를 DirectML 대신 TensorRT 로 쓴다.

- **장치 전환은 NPU 와 같은 상태 기계**(`MainWindow.Npu.cs`): 장치가 셋이 되며 `_onNpu`·`_wantNpu`·`_npuBusy` 를 `_onDevice`(지금)·`_wantDevice`(고른 것)·`_prepDevice`(준비 중인 가속 장치)로 일반화했다.
  **준비는 한 번에 하나** — 같은 장치를 다시 고르면 진행 중인 것을 기다리고, 다른 가속 장치를 고르면 상태바에 `<장치> 준비 대기 — <앞 장치> 준비가 끝나면 시작` 을 띄웠다가
  앞 준비가 끝난 뒤(결과는 버린다) 그쪽을 시작한다(창이 닫혔으면 시작하지 않는다 — 끝없이 되풀이되지 않게). 실패하면 지금 장치에 머문다(`_wantDevice = _onDevice`).
  NPU 의 단계 문구·툴팁·사유 글자는 그대로 두었다(Test-NpuSwitch 가 기댄다). 콤보 툴팁은 `지금: …` 다음 `GPU: …` · `TensorRT: …` · `NPU: …` 순 — NPU 를 끝 줄에 둔다
- TensorRT 쪽 코드는 `MainWindow.Trt.cs`: ① **FP16 짝 확인**(`CheckTrtModel` — `best.fp16.onnx` 있음 · 메타데이터 `fp16_source_md5` 있음(없으면 'FP16 짝 모델이 아님') ·
  그 값 = 지금 `best.onnx` MD5. 메타데이터는 CPU·최적화 끔 세션으로만 읽는다 — FP16 그래프도 CPU 로 열린다)
  ② 카탈로그에서 그 EP 만 `EnsureReadyAsync` → `TryRegister` → GPU 장치(NPU 와 같은 `EnsureProviderReadyAsync` — `Task.Yield` 까지)
  ③ 세션 생성 = **엔진 만들기**: EP 옵션 `enable_cuda_graph=1`(EP 기본값 — 이 모델에선 켜는 편이 조금 빨랐다) · `nv_runtime_cache_path` = `%LOCALAPPDATA%\DetectView\trt_cache\<키>`
  (JIT 로 만든 커널 — 엔진마다 파일 하나, 6~7 MB). 키 = FP16 짝 MD5 `_` EP 패키지 버전 `_` ONNX Runtime 파일 버전(`CacheKeyFor` — NPU 컴파일 캐시와 같은 함수).
  EP 가 캐시를 엔진(그래프 해시)마다 나누긴 하지만 옛 결과를 다른 모델·판에 쓴 VitisAI 의 일이 있어 폴더로도 가른다. 쓴 키는 TensorRT 툴팁 끝 `엔진 캐시 키: …`
  ④ `SwapYolo`. 창 제목 `… — <GPU 이름> (TensorRT · FP16)` (GPU 이름은 EP 장치의 PCI ID 로 DXGI 어댑터에서 찾는다 — EP 장치 정보엔 제조사뿐)
- 단계 문구: `TensorRT 확인 중` → (내려받을 때 `TensorRT 실행 공급자 내려받는 중 N%`) → `TensorRT 엔진 만드는 중 (첫 실행 약 10초)` / 캐시가 있으면 `TensorRT 준비 중`.
  쓸 수 없으면 항목이 막히고 사유가 툴팁에: `best.fp16.onnx 없음` · `FP16 짝 모델이 아님` · `best.onnx 가 바뀌어 best.fp16.onnx 와 짝이 안 맞음` · `TensorRT 실행 공급자가 없음 (NVIDIA RTX 30 이후 GPU 에서만)`
- **왜 FP16 짝인가**: TensorRT for RTX 는 모델에 적힌 정밀도대로 돈다(FP32 를 알아서 낮추지 않는다). 같은 8클래스 YOLO26m 640×384 의 `session.Run`(NpuBench, 100장 × 네 번의 중앙):
  DirectML FP32 **10.0 ms** · TensorRT FP32 6.6 ms · **TensorRT FP16 2.8 ms**, FP16 의 검출은 DirectML 대비 재현율 99.6% · 정밀도 99.9%(평균 IoU 0.989), 같은 입력이면 출력이 비트까지 같다(세션 안).
  짝은 `pwsh tools\Make-Fp16Model.ps1 [-Model <경로>] [-Bench]`(uv 로 `tools/fp16_convert.py` — `onnxruntime.transformers` 의 `convert_float_to_float16(keep_io_types=True)`,
  onnxruntime 1.31.1 고정, 4초, 결정적 — 같은 원본이면 바이트까지 같은 파일). 메타데이터는 원본 것을 그대로 두고 `fp16_source_md5`·`fp16_converter` 를 더한다.
  **FP16 짝은 DirectML 로 못 연다**(그래프 융합 `80070057` — RfDetrBench README '1단계') — `best.onnx` 자리에 두면 앱이 뜨지 않는다(Test-NpuSwitch 의 '다른 모델' 후보에서도 뺀다).
  루트 짝은 git 으로 추적한다(40.9 MB, MD5 `D7D760DC…`). publish 에는 복사되지 않는다 — 배포 폴더의 짝은 `-Model publish\best.onnx` 로 만든다
- **대가**: `session.Run` 동안 그 스레드가 GPU 를 기다리며 돈다(1회당 CPU 시간 ≈ 추론 시간 — DirectML 은 1 ms 안팎. EP 옵션엔 기다리는 방식을 바꾸는 것이 없다) ·
  첫 엔진 만들기(앱에서 고른 뒤 전환까지 16.9초, 캐시 뒤 6.7초 — 대부분 ①② 확인 단계로 NPU 의 캐시 뒤 5~6초와 같다) · NVIDIA RTX 전용
- **확인 (2026-11-08, Debug, `Test-NpuSwitch` 2b)**: NPU 에서 곧바로 TensorRT 16.9초(엔진 처음), ①② 창 응답 WM_NULL 최대 3 ms · UIA 최대 40 ms. test1.mp4 재생 0.99배속,
  참고용 재생 처리 GPU 74.6 · **TensorRT 113.4** · NPU 36.9 FPS. 멈춘 프레임 두 곳(3.0·6.5초)의 검출이 GPU 와 클래스까지 같았다(11 · 13개 — 두 곳을 보는 건 엔진이 옛 입력을 다시 쓰는 잘못을 가리려고).
  SAHI(작업 스레드에서 같은 세션 — 19개)도 된다. 재생 중 다시 고르면 캐시로 6.7초(`TensorRT 확인 중 → TensorRT 준비 중`), 재생은 끊기지 않는다
- 넣지 않은 것: EP 문맥 모델(`_ctx.onnx` — 미리 컴파일해 엔진 만들기를 더 줄인다고 한다, 재 보지 않음), FP32 원본을 TensorRT 로 도는 길(짝이 없을 때 1.5배라도 — 짝 하나로 단순하게),
  시작부터 TensorRT 로 띄우는 명령줄(`--npu` 같은 것)
