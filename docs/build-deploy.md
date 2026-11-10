# 빌드 · 배포 — 이력과 실측

> CLAUDE.md 에서 옮긴 글이다(2026-11-10 — 세션마다 읽히지 않게 내용은 글자 그대로 옮기고, CLAUDE.md 에는 규칙·금지 사항과 가리키는 줄만 남겼다).
> 본문의 '위'·'아래'·'○번'·'○○ 절'이 가리키는 곳은 이 문서나 CLAUDE.md, 또는 다른 `docs/` 문서다 — CLAUDE.md 의 '자세한 문서' 표에서 찾는다(예: '아래 3번' = CLAUDE.md '절대 건드리면 안 되는 것들' 3번). 새로 알게 된 실측·경과는 여기에 더한다.

## 배포 이력

- 결과: `DetectView.exe` 126.4 MB (2026-10-24, Windows ML 런타임. 이전 DirectML 1.23 은 119.4 MB. 2026-10-30·10-31·11-01 다시 배포도 같은 크기 — 11-01 은 세 번: 이슈 수정판 `53db7ed`, 화면 개편판 `c274575`, 클래스별 검출 수 범례판 `fb06cf9` · 11-02 추적기 연결·라벨/꺾쇠·상태바판 `3939f40` · 11-03 관심 구역·화면 전환 감지·구역 이벤트 로그·유고 규칙·화면 식별판 `10f7b31` · 같은 날 폴더 이전·이름 정리판 — 위 '이름' · 11-05 YOLO 쌍선형 레터박스·NPU 짝 재보정판 `f863585` · 11-06 YOLO26m 640×384 판 `a378a58` — 이때는 사용자 승인으로 배포 폴더 모델도 바꿨다, 아래 · 11-07 VLM 설명판 `4c05f5f` 132,590,495 바이트 — exe 만 옮김 · 11-08 8클래스 모델판 `79a041a` 132,590,524 바이트 — 이때도 사용자 승인으로 배포 폴더 모델을 바꿨다, 아래 · 같은 날 TensorRT 판 `b8d4ee0` 132,596,048 바이트 — 배포 폴더에 FP16 짝 `best.fp16.onnx` 를 더함). 테스트 영상(`test1~4.mp4`)은 같은 폴더에 있어도 덮어쓰지 않는다

## 배포 폴더의 모델

- **`publish\best.onnx` 는 루트의 것과 다를 수 있고, 사용자가 수시로 바꾼다** (YOLO26s ↔ YOLO26x(COCO 80클래스),
  데이터셋이 다른 7클래스 모델 등 — 2026-09-23 한 세션 안에서도 바뀌었다). 건드리지 말 것.
  2026-11-06 엔 사용자 승인으로 루트와 같은 YOLO26m 640×384(`94A26C12…`)로 바꿨다 — 그 전 YOLO26s(09-17, `87D0E1CB…`)와 그 짝은
  `publish\best.yolo26s-0917.onnx` · `best.yolo26s-0917.npu.onnx` 로 이름을 바꿔 옆에 두었다(앱은 `best.onnx`·`best.npu.onnx` 만 읽는다. `Test-NpuSwitch` 의 '다른 모델의 짝'이 이것을 쓴다).
  2026-11-08 루트를 8클래스 YOLO26m(`AB56CED7…`)으로 바꾸고 같은 날 사용자 승인("배포하기")으로 배포 폴더도 같은 모델·짝(`546567CB…` — 루트 짝과 바이트까지 같다)으로 바꿨다.
  그 전 7클래스 YOLO26m(11-05, `94A26C12…`)과 짝(`BD18D32C…`)은 `publish\best.yolo26m-1105.onnx` · `best.yolo26m-1105.npu.onnx` 로 이름을 바꿔 보관했다
  **성능을 재거나 비교하기 전엔 그 순간 어떤 모델인지 확인한다** — 앱이 떠 있으면 창 제목에 보이고(아래),
  아니면 파일 끝의 Ultralytics 메타데이터를 읽는다:
  `uv run --no-project --python 3.12 python -c "import re,sys;f=open(sys.argv[1],'rb');f.seek(-65536,2);t=f.read().decode('utf-8','ignore');[print(k,'=',v.strip()[:80]) for k,v in re.findall(r'(description|names|date|imgsz)\x12.?([^\x00-\x08\x0e-\x1f]+)',t)]" publish/best.onnx`
  (`imgsz` 가 `[384, 640]` 이면 16:9 영상용 640×384 판 — 같은 가중치의 640×640 판과 이름·날짜가 같을 수 있다)

## 앱용 ONNX 내보내기

- **앱용 ONNX 를 새로 내보낼 땐 end-to-end(`[1,300,6]`)인지 본다** — Ultralytics 8.4.142(라벨링 도구가 쓰는 판)의 `export(format="onnx")` 기본값은 NMS 전 원출력 `[1,84,8400]`(메타데이터 `end2end False`)이다.
  `nms=False`(또는 `end2end=True`)를 줄 것. 지금 루트 `best.onnx`(8클래스 YOLO26m 640×384, 11-08 — 그 전 7클래스 판도)는 8.4.142 에서 `nms=False` 로 내보낸 end-to-end 다
  (`experiments/RfDetrBench/step2/export_trained.py yolo <best.pt> <이름> --imgsz 384 640` — 그 README 끝 '8클래스'). 그 전 YOLO26s 는 8.4.31 기본값으로 end-to-end 였다 (2026-11-04 실측 — `experiments/RfDetrBench` README. 원출력 꼴을 앱이 어떻게 처리하는지는 확인 안 함 — 아래 3번의 전제가 깨진다).
  같은 판의 **val 도 `nms` 를 안 주면 one-to-many 출력 + NMS 로 잰다**(학습 중 검증·`best.pt` 고르기도) — 학습 로그의 mAP 는 앱이 쓰는 end-to-end 출력보다 0.007~0.010 높게 나온다(2026-11-05 실측). 앱과 같은 걸 재려면 val 에 `nms=False`.
  그 몫은 val 의 NMS 가 **multi-label**(한 박스가 여러 클래스로 남음)이라서다 — `nms=True` 로 내보낸 ONNX(박스마다 최고 클래스 하나)는 앱 경로에서 end-to-end 보다 AP 가 0.003~0.005 **낮다**(같은 날 — 아래 3번·"RF-DETR 검토")

## 창 제목 만드는 법

실행 시 창 제목 `DetectView — <모델> <입력 가로×세로> (<학습일> 학습) — <GPU 이름> (DirectML)` 의 각 부분:

  모델 이름은 ONNX 메타데이터 `description`("Ultralytics YOLO26m model …")에서, 입력 크기는 ONNX 입력 꼴에서, 학습일은 `date` 에서 뽑는다(`DescribeModel`).
  입력 크기는 2026-11-06 더했다 — 같은 가중치의 640×640 판과 640×384 판은 이름·학습일이 같은데 속도가 31% 다르다.
  `date` 는 Ultralytics 가 **내보낸 때**를 쓴다(지금 모델은 11-08 학습을 마치고 같은 날 내보냄 · 그 전 7클래스 판은 11-05 — 배포 YOLO26s 도 학습한 날 내보냈다)
  YoloDotNet 의 `ModelInfo` 에는 s/x 크기가 없어서 쓰지 않는다. 메타데이터가 없는 모델이면 `ModelInfo` 로 대신한다
