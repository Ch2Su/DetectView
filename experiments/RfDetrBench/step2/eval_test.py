r"""2단계 평가: 학습한 모델들을 데이터셋 시험 분할(학습·best 고르기에 안 쓴 몫)에서 같은 평가기로 견준다 (README '2단계').

모든 모델을 **ONNX + 앱과 같은 전처리**로 돌린다 — 앱이 실제로 돌릴 것을 재려고:
  - YOLO (end-to-end [1,300,6]): YoloDotNet 처럼 비율 유지 · 가운데 · Nearest · 검은 여백, RGB 0~1 (NpuBench·RfDetrBench 의 Letterbox 와 같음)
  - RF-DETR ([1,Q,4] + [1,Q,C]): 정사각형으로 그냥 늘이기(float 쌍선형, antialias 없음) + ImageNet 정규화, 시그모이드 → 쿼리×클래스 상위 Q
점수는 pycocotools(COCO mAP50-95 · mAP50 · 작은/중간/큰 물체 AP · 클래스별 AP)와, 앱의 신뢰도 임계 0.3 에서의 정밀도·재현율(IoU 0.5).
추론은 ONNX Runtime CPU(실행 공급자 차이는 RfDetrBench ③ 에서 검출 단위로 같음을 봤다).

    # 저장소 루트에서 (Windows — 데이터는 WSL 작업 사본을 UNC 경로로 읽는다). 모델을 나눠 돌려도 out/eval/results.json 에 쌓인다
    uv run --no-project --python 3.12 --with onnxruntime --with pycocotools --with opencv-python --with numpy `
        experiments/RfDetrBench/step2/eval_test.py --data "\\wsl.localhost\Ubuntu\home\chisu\rfdetr_work\data\visible" `
        --out experiments\RfDetrBench\out\eval --threads 4 `
        --model "yolo:y26s:experiments\RfDetrBench\out\step2_models\y26s.onnx" --model "rfdetr:rfdetr_s:experiments\RfDetrBench\out\step2_models\rfdetr_s.onnx"
    # FP16 은 앱처럼 DirectML 로: onnxruntime 대신 --with onnxruntime-directml, 그리고 --ep dml (GPU 를 학습이 쓰는 동안엔 돌리지 않는다)

--model 은 '종류:이름:경로' (종류 yolo | rfdetr). RF-DETR 는 마지막 로짓 칸이 배경이면(파인튜닝 모델 — 클래스 수 + 1) 그 칸을 뺀다
(rfdetr 의 YOLO 데이터 로더는 클래스 번호를 그대로 로짓 칸 번호로 쓰고, 남는 마지막 칸은 rfdetr 자체 ONNX 추론도 뺀다 — export/_onnx/inference.py 의 background_class_id=-1).
"""
from __future__ import annotations

import argparse
import json
import sys
import time
from pathlib import Path

import cv2
import numpy as np
import onnxruntime as ort

THRESHOLD_APP = 0.3        # 앱의 ConfidenceThreshold
# Ultralytics 가 읽는 형식과 같게 — 시험 분할에 webp 가 한 장 있다(빼면 972장이 된다)
IMG_EXTS = (".jpg", ".jpeg", ".png", ".bmp", ".webp", ".tif", ".tiff")
THRESHOLD_EVAL = 0.001     # mAP 는 PR 곡선 전체가 필요하다 (Ultralytics val 과 같은 값)
MEAN = np.array([0.485, 0.456, 0.406], np.float32)
STD = np.array([0.229, 0.224, 0.225], np.float32)


def read_names(data: Path) -> list[str]:
    names, inside = {}, False
    for line in (data / "data.yaml").read_text().splitlines():
        if line.startswith("names:"):
            inside = True
            continue
        if inside:
            if not line.startswith(" "):
                break
            k, v = line.strip().split(":", 1)
            names[int(k)] = v.strip().strip("'\"")
    return [names[i] for i in range(len(names))]


def build_gt(data: Path, names: list[str], cache: Path) -> dict:
    """YOLO 라벨(정규화 cx cy w h) → COCO 정답. 이미지 번호는 파일 이름 순서, 클래스 번호는 YOLO 번호 + 1"""
    if cache.exists():
        return json.loads(cache.read_text())
    images, anns = [], []
    files = sorted(p for p in (data / "images" / "test").iterdir() if p.suffix.lower() in IMG_EXTS)
    for i, p in enumerate(files, 1):
        h, w = cv2.imread(str(p), cv2.IMREAD_UNCHANGED).shape[:2]
        images.append({"id": i, "file_name": p.name, "width": w, "height": h})
        lab = data / "labels" / "test" / (p.stem + ".txt")
        for line in (lab.read_text().splitlines() if lab.exists() else []):
            parts = line.split()
            if len(parts) < 5:
                continue
            c, cx, cy, bw, bh = int(parts[0]), *map(float, parts[1:5])
            x, y, bw, bh = (cx - bw / 2) * w, (cy - bh / 2) * h, bw * w, bh * h
            anns.append({"id": len(anns) + 1, "image_id": i, "category_id": c + 1, "bbox": [x, y, bw, bh],
                         "area": bw * bh, "iscrowd": 0})
    gt = {"images": images, "annotations": anns,
          "categories": [{"id": i + 1, "name": n} for i, n in enumerate(names)]}
    cache.write_text(json.dumps(gt))
    return gt


def letterbox(bgr: np.ndarray, side: int, interp: int = cv2.INTER_NEAREST_EXACT, pad: int = 0) -> tuple[np.ndarray, float, int, int]:
    """YoloDotNet ResizeImageProportional 과 같게 — 크기 (int)(w·s + 0.5), 가운데, Nearest(화소 중심 맞춤), 검은 여백.
    interp 를 INTER_LINEAR 로 주면 Ultralytics LetterBox 의 축소 방식(쌍선형)이 되고, pad 114 면 그쪽 여백 색(회색)이 된다 —
    앱의 전처리가 정확도에서 잃는 몫을 나눠 재려고"""
    h, w = bgr.shape[:2]
    s = min(side / w, side / h)
    nw, nh = int(w * s + 0.5), int(h * s + 0.5)
    canvas = np.full((side, side, 3), pad, np.uint8)
    px, py = (side - nw) // 2, (side - nh) // 2
    canvas[py:py + nh, px:px + nw] = cv2.resize(bgr, (nw, nh), interpolation=interp)
    x = canvas[:, :, ::-1].transpose(2, 0, 1).astype(np.float32)[None] / 255.0
    return np.ascontiguousarray(x), s, px, py


def run_yolo(sess, bgr: np.ndarray, interp: int = cv2.INTER_NEAREST_EXACT, pad: int = 0) -> list[tuple]:
    side = sess.get_inputs()[0].shape[2]
    x, s, px, py = letterbox(bgr, side, interp, pad)
    out = sess.run(None, {sess.get_inputs()[0].name: x})[0][0]   # [300, 6] = x1 y1 x2 y2 점수 클래스 (레터박스 좌표)
    h, w = bgr.shape[:2]
    dets = []
    for x1, y1, x2, y2, sc, c in out:
        if sc <= THRESHOLD_EVAL:
            continue
        x1, x2 = np.clip([(x1 - px) / s, (x2 - px) / s], 0, w)
        y1, y2 = np.clip([(y1 - py) / s, (y2 - py) / s], 0, h)
        dets.append((float(x1), float(y1), float(x2), float(y2), int(c), float(sc)))
    return dets


def run_rfdetr(sess, bgr: np.ndarray, background_last: bool) -> list[tuple]:
    res = sess.get_inputs()[0].shape[2]
    rgb = bgr[:, :, ::-1].astype(np.float32) / 255.0
    x = (cv2.resize(rgb, (res, res), interpolation=cv2.INTER_LINEAR) - MEAN) / STD   # 공식 전처리와 0~1 값 차이 최대 8e-5 (RfDetrBench)
    x = np.ascontiguousarray(x.transpose(2, 0, 1)[None], dtype=np.float32)
    names = [o.name for o in sess.get_outputs()]
    o = dict(zip(names, sess.run(None, {sess.get_inputs()[0].name: x})))
    boxes = o[next(n for n in names if "dets" in n)][0]       # 이름으로 고른다 — 모양으로 고르면 클래스 3개일 때 뒤바뀐다
    logits = o[next(n for n in names if "labels" in n)][0]
    if background_last:
        logits = logits[:, :-1]
    q, c = logits.shape
    scores = 1.0 / (1.0 + np.exp(-np.clip(logits, -88, 88)))
    flat = scores.reshape(-1)
    top = np.argsort(-flat, kind="stable")[:q]                 # 쿼리×클래스 쌍 전체에서 상위 Q (rfdetr decode_detections 와 같음)
    h, w = bgr.shape[:2]
    dets = []
    for k in top:
        sc = float(flat[k])
        if sc <= THRESHOLD_EVAL:
            break
        qi, ci = divmod(int(k), c)
        cx, cy, bw, bh = boxes[qi]
        dets.append((float(np.clip((cx - bw / 2) * w, 0, w)), float(np.clip((cy - bh / 2) * h, 0, h)),
                     float(np.clip((cx + bw / 2) * w, 0, w)), float(np.clip((cy + bh / 2) * h, 0, h)), ci, sc))
    return dets


def iou(a, b) -> float:
    ix = max(0.0, min(a[2], b[2]) - max(a[0], b[0]))
    iy = max(0.0, min(a[3], b[3]) - max(a[1], b[1]))
    inter = ix * iy
    u = (a[2] - a[0]) * (a[3] - a[1]) + (b[2] - b[0]) * (b[3] - b[1]) - inter
    return inter / u if u > 0 else 0.0


def app_point(gt: dict, preds: dict[int, list], names: list[str]) -> dict:
    """앱의 임계값(0.3)에서 정밀도·재현율 — 같은 클래스·IoU 0.5 이상을 신뢰도 높은 예측부터 탐욕적으로 짝짓는다"""
    by_img: dict[int, list] = {}
    for a in gt["annotations"]:
        x, y, w, h = a["bbox"]
        by_img.setdefault(a["image_id"], []).append((x, y, x + w, y + h, a["category_id"] - 1))
    tp = fp = 0
    per = {i: [0, 0, 0] for i in range(len(names))}   # tp, fp, gt
    for img in gt["images"]:
        g = by_img.get(img["id"], [])
        for gg in g:
            per[gg[4]][2] += 1
        used = [False] * len(g)
        for d in sorted((d for d in preds.get(img["id"], []) if d[5] > THRESHOLD_APP), key=lambda d: -d[5]):
            best, bi = 0.5, -1
            for j, gg in enumerate(g):
                if not used[j] and gg[4] == d[4]:
                    v = iou(d, gg)
                    if v >= best:
                        best, bi = v, j
            if bi >= 0:
                used[bi] = True
                tp += 1
                per[d[4]][0] += 1
            else:
                fp += 1
                per[d[4]][1] += 1
    n_gt = sum(v[2] for v in per.values())
    return {"precision": tp / max(1, tp + fp), "recall": tp / max(1, n_gt), "tp": tp, "fp": fp, "gt": n_gt,
            "recall_per_class": {names[i]: v[0] / max(1, v[2]) for i, v in per.items()}}


def coco_scores(gt_path: Path, det_path: Path, names: list[str]) -> dict:
    from pycocotools.coco import COCO
    from pycocotools.cocoeval import COCOeval
    import contextlib
    import io
    with contextlib.redirect_stdout(io.StringIO()):
        coco_gt = COCO(str(gt_path))
        coco_dt = coco_gt.loadRes(str(det_path))
        ev = COCOeval(coco_gt, coco_dt, "bbox")
        ev.evaluate()
        ev.accumulate()
        ev.summarize()
    s = ev.stats   # AP, AP50, AP75, APs, APm, APl, AR1, AR10, AR100, ARs, ARm, ARl
    prec = ev.eval["precision"]   # [IoU 10, 재현율 101, 클래스, 넓이 4, maxDets 3]
    per_class = {}
    for k, cat in enumerate(ev.params.catIds):
        p = prec[:, :, k, 0, -1]
        per_class[names[cat - 1]] = float(np.mean(p[p > -1])) if (p > -1).any() else float("nan")
    return {"AP": s[0], "AP50": s[1], "AP75": s[2], "APs": s[3], "APm": s[4], "APl": s[5], "AR100": s[8],
            "AP_per_class": per_class}


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--data", type=Path, required=True)
    ap.add_argument("--out", type=Path, required=True)
    ap.add_argument("--model", action="append", default=[], help="종류:이름:경로 (종류 yolo | rfdetr)")
    ap.add_argument("--ultra-json", action="append", default=[],
                    help="이름:경로 — Ultralytics val(save_json=True, crosscheck_native.py)의 predictions.json 을 같은 평가기로 채점한다. "
                         "두 평가기의 차이가 점수 계산에서 오는지 Ultralytics 의 전처리(rect 배치 · 회색 여백)에서 오는지 가르려고")
    ap.add_argument("--threads", type=int, default=0, help="ONNX Runtime 스레드 수 (0 = 기본)")
    ap.add_argument("--yolo-resize", choices=["nearest", "linear"], default="nearest",
                    help="YOLO 레터박스 축소: nearest = 앱(YoloDotNet), linear = Ultralytics 와 같은 쌍선형")
    ap.add_argument("--yolo-pad", type=int, default=0, help="YOLO 레터박스 여백 값: 0 = 앱(검정), 114 = Ultralytics 의 회색")
    ap.add_argument("--ep", choices=["cpu", "dml"], default="cpu",
                    help="dml = DirectML (onnxruntime-directml 패키지 필요). FP16 모델은 앱처럼 DirectML 로 잰다")
    args = ap.parse_args()
    if not args.model and not args.ultra_json:
        ap.error("--model 이나 --ultra-json 을 하나 이상 준다")
    yolo_interp = cv2.INTER_LINEAR if args.yolo_resize == "linear" else cv2.INTER_NEAREST_EXACT
    args.out.mkdir(parents=True, exist_ok=True)

    names = read_names(args.data)
    gt_path = args.out / "gt_test.json"
    gt = build_gt(args.data, names, gt_path)
    n_ann = len(gt["annotations"])
    per_img = np.bincount([a["image_id"] for a in gt["annotations"]], minlength=len(gt["images"]) + 1)[1:]
    print(f"시험 분할 {len(gt['images'])}장 · 정답 {n_ann}개 · 한 장 최대 {per_img.max()}개 (100 넘는 장 {(per_img > 100).sum()}) · "
          f"작은 물체(32² 미만) {sum(a['area'] < 32 * 32 for a in gt['annotations'])}개", flush=True)

    so = ort.SessionOptions()
    if args.threads:
        so.intra_op_num_threads = args.threads
    if args.ep == "dml":
        # DirectML 은 메모리 패턴·병렬 실행을 지원하지 않는다 (앱의 WindowsMlExecutionProvider 와 같은 설정)
        so.enable_mem_pattern = False
        so.execution_mode = ort.ExecutionMode.ORT_SEQUENTIAL
    # 하이브리드 노트북이라 DirectML 장치가 둘이다(AMD 내장·NVIDIA) — 기본 device_id 0 은 어느 쪽일지 몰라 고성능 GPU 를 달라고 한다
    providers = ([("DmlExecutionProvider", {"performance_preference": "high_performance", "device_filter": "gpu"})]
                 if args.ep == "dml" else ["CPUExecutionProvider"])
    results_path = args.out / "results.json"
    results = json.loads(results_path.read_text()) if results_path.exists() else {}   # 모델을 나눠 돌려도 결과가 쌓이게

    def record(name: str, preds: dict, dets_json: list, extra: dict) -> None:
        det_path = args.out / f"det_{name}.json"
        det_path.write_text(json.dumps(dets_json))
        r = coco_scores(gt_path, det_path, names)
        r["app_point"] = app_point(gt, preds, names)
        r.update(extra)
        r["images"] = len(gt["images"])
        results[name] = r
        a = r["app_point"]
        tail = f" | {r['ep']} {r['ms_per_image']:.0f} ms/장" if "ms_per_image" in r else ""
        print(f"[{name}] AP {r['AP']:.4f} · AP50 {r['AP50']:.4f} · APs {r['APs']:.4f} · APm {r['APm']:.4f} · APl {r['APl']:.4f} · "
              f"AR100 {r['AR100']:.4f} | 임계 0.3: 정밀도 {a['precision']:.3f} 재현율 {a['recall']:.3f}{tail}", flush=True)
        print("    클래스별 AP: " + " · ".join(f"{k} {v:.3f}" for k, v in r["AP_per_class"].items()), flush=True)
        results_path.write_text(json.dumps(results, ensure_ascii=False, indent=1))

    for spec in args.model:
        kind, name, path = spec.split(":", 2)
        path = str(Path(path).expanduser())
        sess = ort.InferenceSession(path, so, providers=providers)
        print(f"[{name}] 실행 공급자 {sess.get_providers()}", flush=True)
        background_last = False
        if kind == "rfdetr":
            c = next(o for o in sess.get_outputs() if "labels" in o.name).shape[2]
            background_last = c == len(names) + 1   # 파인튜닝 모델 = 클래스 수 + 배경 칸
            if c not in (len(names), len(names) + 1):
                raise SystemExit(f"{name}: 로짓 폭 {c} 가 클래스 수 {len(names)} 와 맞지 않는다 (COCO 사전학습 모델은 평가하지 않는다)")
        t0 = time.time()
        preds, dets_json = {}, []
        for img in gt["images"]:
            bgr = cv2.imread(str(args.data / "images" / "test" / img["file_name"]), cv2.IMREAD_COLOR)
            d = run_yolo(sess, bgr, yolo_interp, args.yolo_pad) if kind == "yolo" else run_rfdetr(sess, bgr, background_last)
            preds[img["id"]] = d
            dets_json += [{"image_id": img["id"], "category_id": c + 1, "bbox": [x1, y1, x2 - x1, y2 - y1], "score": sc}
                          for x1, y1, x2, y2, c, sc in d]
        dt = time.time() - t0
        record(name, preds, dets_json, {
            "ep": args.ep,
            "preprocess": (f"letterbox-{args.yolo_resize}" + (f"-pad{args.yolo_pad}" if args.yolo_pad else "")) if kind == "yolo"
                          else "stretch-linear-float",
            "ms_per_image": dt / len(gt["images"]) * 1000,   # 디코딩·전처리·후처리 포함 — 참고용
        })

    # Ultralytics val 의 검출(그쪽 전처리 — rect 배치 · 회색 114 여백 · 쌍선형)을 같은 평가기로. 이미지는 파일 이름(확장자 뺌)으로 맞춘다
    by_stem = {Path(img["file_name"]).stem: img["id"] for img in gt["images"]}
    for spec in args.ultra_json:
        name, path = spec.split(":", 1)
        preds, dets_json = {}, []
        for d in json.loads(Path(path).read_text()):
            stem = Path(d["file_name"]).stem if "file_name" in d else str(d["image_id"])
            if stem not in by_stem:
                raise SystemExit(f"{name}: {stem} 이 시험 분할에 없다 — 다른 분할의 predictions.json?")
            iid = by_stem[stem]
            x, y, w, h = map(float, d["bbox"])
            c, sc = int(d["category_id"]) - 1, float(d["score"])   # Ultralytics 는 COCO 가 아닌 데이터셋에 클래스 번호 + 1 을 쓴다
            preds.setdefault(iid, []).append((x, y, x + w, y + h, c, sc))
            dets_json.append({"image_id": iid, "category_id": c + 1, "bbox": [x, y, w, h], "score": sc})
        record(name, preds, dets_json, {"ep": "ultralytics-val", "preprocess": "ultralytics-val"})

    print(f"\n결과 → {results_path}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
