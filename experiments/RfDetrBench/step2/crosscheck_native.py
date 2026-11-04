"""2단계 교차 확인: 각 라이브러리의 **자체 평가기**로 같은 시험 분할을 잰다 (README '2단계').

eval_test.py 는 모든 모델을 앱과 같은 전처리 + pycocotools 로 견준다. 그 경로가 한쪽에 불리하지 않은지 보려고,
학습한 원본 가중치를 각자의 라이브러리가 평가하는 숫자(Ultralytics val · rfdetr evaluate)와 맞대 본다.
둘 다 WSL 에서 CPU 로 돈다 — GPU 는 학습이 쓰고 있을 수 있어서(메모리가 넘치면 WSL 의 CUDA 학습이 죽은 적이 있다).

    cd ~/rfdetr_work
    # YOLO — 라벨링 도구의 학습 환경(yolo_final)을 고치지 않고 실행만. 결과 폴더는 runs_wsl 이 아니라 여기
    OMP_NUM_THREADS=4 YOLO_AUTOINSTALL=false ~/miniconda3/envs/yolo_final/bin/python <이 파일> yolo runs/yolo/y26s/weights/best.pt y26s
    #   --square: rect 배치를 끄고 앱처럼 640×640 정사각형 입력으로 · --e2e: 앱처럼 end-to-end(one-to-one) 출력으로 (기본은 one-to-many + NMS)
    #   (이름은 다르게 — 예: y26s_square_e2e)
    # RF-DETR — 학습한 venv
    OMP_NUM_THREADS=4 ~/rfdetr_work/.venv/bin/python <이 파일> rfdetr runs/rfdetr_s/checkpoint_best_total.pth rfdetr_s
"""
from __future__ import annotations

import json
import sys
import time
from pathlib import Path

W = Path.home() / "rfdetr_work"
DATA = W / "data" / "visible"            # 데이터셋 백업의 작업 사본 (train_rfdetr.py 와 같은 곳)
OUT = W / "runs" / "crosscheck"


def check_yolo(weights: Path, name: str, rect: bool = True, e2e: bool = False) -> dict:
    from ultralytics import YOLO
    # save_json: 검출을 predictions.json 으로도 남긴다 — eval_test.py --ultra-json 이 같은 평가기(pycocotools)로 채점해서
    # 두 평가기의 차이가 점수 계산에서 오는지 Ultralytics 의 추론 경로에서 오는지 가른다.
    # rect: Ultralytics val 의 기본값(배치마다 비율에 맞춘 직사각형 입력 — 16:9 면 640×384). False 면 앱처럼 640×640 정사각형
    # e2e: 8.4.142 의 val 은 nms 를 안 주면(None) YOLO26 의 one-to-many 출력 + 일반 NMS 로 잰다(validator 의 end2end = nms is False).
    #      학습 중 검증·best.pt 고르기도 그렇다. 앱의 ONNX(end-to-end [1,300,6])는 one-to-one 출력이라, 같은 것을 재려면 nms=False
    extra = {"nms": False} if e2e else {}
    r = YOLO(str(weights)).val(data=str(DATA / "data.yaml"), split="test", imgsz=640, batch=16, device="cpu", workers=2,
                               project=str(OUT), name=name, exist_ok=True, plots=False, verbose=True, save_json=True, rect=rect,
                               **extra)
    return {"mAP50_95": float(r.box.map), "mAP50": float(r.box.map50), "P": float(r.box.mp), "R": float(r.box.mr),
            "AP_per_class": {r.names[int(c)]: float(v) for c, v in zip(r.box.ap_class_index, r.box.maps[r.box.ap_class_index])},
            "predictions": str(OUT / name / "predictions.json")}


def check_rfdetr(ckpt: Path, name: str) -> dict:
    import torch
    torch.set_num_threads(4)
    from rfdetr.detr import RFDETR
    model = RFDETR.from_checkpoint(str(ckpt), trust_checkpoint=True)
    res = model.evaluate(split="test", dataset_dir=str(DATA), dataset_file="yolo", device="cpu", batch_size=4,
                         num_workers=2, output_dir=str(OUT / name), tensorboard=False)
    return {k: float(v) for k, v in res.items()}


def main() -> int:
    flags = {a for a in sys.argv[1:] if a.startswith("--")}
    argv = [a for a in sys.argv[1:] if not a.startswith("--")]
    if len(argv) != 3 or argv[0] not in ("yolo", "rfdetr") or flags - {"--square", "--e2e"}:
        print(__doc__)
        return 2
    kind, src, name = argv[0], Path(argv[1]), argv[2]
    if not src.is_absolute():
        src = W / src
    OUT.mkdir(parents=True, exist_ok=True)
    t0 = time.time()
    res = (check_yolo(src, name, rect="--square" not in flags, e2e="--e2e" in flags) if kind == "yolo"
           else check_rfdetr(src, name))
    res["seconds"] = round(time.time() - t0)
    path = OUT / "results.json"
    allres = json.loads(path.read_text()) if path.exists() else {}
    allres[name] = res
    path.write_text(json.dumps(allres, ensure_ascii=False, indent=1))
    print(f"CROSSCHECK {name}: " + json.dumps(res, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    sys.exit(main())
