r"""2단계 평가: 학습한 모델들을 데이터셋 시험 분할(학습·best 고르기에 안 쓴 몫)에서 같은 평가기로 견준다 (README '2단계').

모든 모델을 **ONNX + 앱과 같은 전처리**로 돌린다 — 앱이 실제로 돌릴 것을 재려고:
  - YOLO (end-to-end [1,300,6]): YoloDotNet 처럼 비율 유지 · 가운데 · 검은 여백, RGB 0~1 (NpuBench·RfDetrBench 의 Letterbox 와 같음).
    축소는 --yolo-resize: linear = 지금 앱(2026-11-05 부터 — 앱도 YoloLetterbox.cs 에서 OpenCV INTER_LINEAR 로 같은 레터박스를 만든다.
    다만 앱 박스는 레터박스 좌표의 정수 + 0.5 라 이 평가기의 실수 좌표와 조금 다르다), nearest = 그 전의 앱(YoloDotNet 기본값 — Skia 로 직접 줄임).
    다만 OpenCV NEAREST_EXACT 는 Skia 의 Nearest 와 화소를 똑같이 고르지 않는다
    — 시험 분할 973장 중 540장이 달랐다(정확히 2배 축소에서 Skia 는 2×2 묶음의 왼쪽 위, NEAREST_EXACT 는 오른쪽 아래 화소를 고른다 —
    TrackerCheck inputbench). 그래서 nearest 값은 그때의 앱과 조금 다르다.
    YoloDotNet 은 모델보다 작은 이미지(가로·세로 모두 640 미만 — 시험 분할 111장)를 키우지 않고 가운데에 그대로 둔다 — 똑같이 하려면 --yolo-no-upscale
  - RF-DETR ([1,Q,4] + [1,Q,C]): 정사각형으로 그냥 늘이기(float 쌍선형, antialias 없음) + ImageNet 정규화, 시그모이드 → 쿼리×클래스 상위 Q
앱 경로 그대로의 검출(YoloDotNet + DirectML — TrackerCheck testsplit 이 쓴 JSON)은 --app-json 으로 같은 평가기에 넣고,
--compare 로 두 검출 묶음을 앱 임계값(0.3)에서 박스 단위로 견준다.
점수는 pycocotools(COCO mAP50-95 · mAP50 · 작은/중간/큰 물체 AP · 클래스별 AP)와, 앱의 신뢰도 임계 0.3 에서의 정밀도·재현율(IoU 0.5) —
그 정밀도·재현율은 앱의 중복 박스 필터(Detection.cs 의 DuplicateBoxes — app_dedup)를 걸기 전·건 뒤 둘 다 낸다(뒤의 것이 앱 화면·추적기가 받는 박스).
추론은 ONNX Runtime CPU(실행 공급자 차이는 RfDetrBench ③ 에서 검출 단위로 같음을 봤다).

    # 저장소 루트에서 (Windows — 데이터는 WSL 작업 사본을 UNC 경로로 읽는다). 모델을 나눠 돌려도 out/eval/results.json 에 쌓인다
    uv run --no-project --python 3.12 --with onnxruntime --with pycocotools --with opencv-python --with numpy `
        experiments/RfDetrBench/step2/eval_test.py --data "\\wsl.localhost\Ubuntu\home\chisu\rfdetr_work\data\visible" `
        --out experiments\RfDetrBench\out\eval --threads 4 `
        --model "yolo:y26s:experiments\RfDetrBench\out\step2_models\y26s.onnx" --model "rfdetr:rfdetr_s:experiments\RfDetrBench\out\step2_models\rfdetr_s.onnx"
    # FP16 은 앱처럼 DirectML 로: onnxruntime 대신 --with onnxruntime-directml, 그리고 --ep dml (GPU 를 학습이 쓰는 동안엔 돌리지 않는다)
    # 앱 경로 검출과 맞대기 (2026-11-05 — README '앱의 쌍선형 축소'): 먼저 TrackerCheck testsplit 으로 JSON 을 만들고
    #   ... --ep dml --yolo-resize linear --yolo-no-upscale --model "yolo:deployed_cv2_linear:publish\best.onnx" `
    #       --app-json "deployed_app_linear:experiments\TrackerCheck\out\testsplit\app_deployed_linear.json" --compare deployed_cv2_linear:deployed_app_linear

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


def letterbox(bgr: np.ndarray, side: int, interp: int = cv2.INTER_NEAREST_EXACT, pad: int = 0,
              no_upscale: bool = False, side_h: int | None = None) -> tuple[np.ndarray, float, int, int]:
    """YoloDotNet ResizeImageProportional 과 같게 — 크기 (int)(w·s + 0.5), 가운데, 검은 여백.
    interp 는 INTER_LINEAR(지금 앱 · Ultralytics 와 같은 쌍선형) 또는 INTER_NEAREST_EXACT(2026-11-05 전의 앱 — 화소 중심을 맞추는 Nearest),
    pad 114 면 Ultralytics 의 여백 색(회색)이 된다 — 앱의 전처리가 정확도에서 잃는 몫을 나눠 재려고.
    no_upscale 이면 YoloDotNet 처럼 모델보다 작은 이미지(가로·세로 모두 입력 미만)는 키우지 않고 가운데에 그대로 둔다.
    side_h 를 주면 입력이 side(가로) × side_h(세로) 직사각형이다 — 16:9 영상용 640×384 모델"""
    h, w = bgr.shape[:2]
    sh = side_h or side
    if no_upscale and w < side and h < sh:
        s, nw, nh = 1.0, w, h
    else:
        s = min(side / w, sh / h)
        nw, nh = int(w * s + 0.5), int(h * s + 0.5)
    canvas = np.full((sh, side, 3), pad, np.uint8)
    px, py = (side - nw) // 2, (sh - nh) // 2
    canvas[py:py + nh, px:px + nw] = bgr if (nw, nh) == (w, h) else cv2.resize(bgr, (nw, nh), interpolation=interp)
    x = canvas[:, :, ::-1].transpose(2, 0, 1).astype(np.float32)[None] / 255.0
    return np.ascontiguousarray(x), s, px, py


def run_yolo(sess, bgr: np.ndarray, interp: int = cv2.INTER_NEAREST_EXACT, pad: int = 0, no_upscale: bool = False) -> list[tuple]:
    h_in, w_in = sess.get_inputs()[0].shape[2:4]   # 정사각형(640×640)이 아닐 수 있다 — 16:9 영상용 640×384
    x, s, px, py = letterbox(bgr, w_in, interp, pad, no_upscale, side_h=h_in)
    # [300, 6] = x1 y1 x2 y2 점수 클래스 (레터박스 좌표). end-to-end 출력이든 NMS 를 넣어 내보낸 출력(nms=True — 남는 줄은 0)이든 같은 꼴
    out = sess.run(None, {sess.get_inputs()[0].name: x})[0][0]
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


VEHICLE_LABELS = {"car", "truck", "bus", "motorcycle", "scooter", "bicycle", "pickup"}   # Detection.cs DuplicateBoxes 의 묶음 — 차량 7종은 한 묶음 (pickup 은 8클래스 모델부터, 7클래스 데이터엔 없다)


def app_dedup(dets: list, names: list[str]) -> list:
    """앱의 DuplicateBoxes(루트 Detection.cs — CLAUDE.md '절대 건드리면 안 되는 것들' 3번)를 그대로: 같은 묶음 · 작은 박스의 80% 이상이 큰 박스 안 ·
    넓이비 0.5 이상이면 신뢰도 높은 쪽만 남긴다. 신뢰도 순 대표와 직접 겹치는 것만 흡수하고 흡수된 박스는 다른 박스를 흡수하지 않는다(비이행)"""
    group = ["vehicle" if names[d[4]] in VEHICLE_LABELS else names[d[4]] for d in dets]
    area = [max(0.0, d[2] - d[0]) * max(0.0, d[3] - d[1]) for d in dets]
    order = sorted(range(len(dets)), key=lambda i: -dets[i][5])
    gone = [False] * len(dets)
    for a, i in enumerate(order):
        if gone[i]:
            continue
        for j in order[a + 1:]:
            if gone[j] or group[i] != group[j]:
                continue
            small, large = min(area[i], area[j]), max(area[i], area[j])
            ix = max(0.0, min(dets[i][2], dets[j][2]) - max(dets[i][0], dets[j][0]))
            iy = max(0.0, min(dets[i][3], dets[j][3]) - max(dets[i][1], dets[j][1]))
            if small > 0 and small >= 0.5 * large and ix * iy >= 0.8 * small:
                gone[j] = True
    return [d for k, d in enumerate(dets) if not gone[k]]


def app_point(gt: dict, preds: dict[int, list], names: list[str], dedup: bool = False) -> dict:
    """앱의 임계값(0.3)에서 정밀도·재현율 — 같은 클래스·IoU 0.5 이상을 신뢰도 높은 예측부터 탐욕적으로 짝짓는다.
    dedup: 임계값을 넘은 검출에 앱의 중복 박스 필터(app_dedup)를 먼저 건다 — 앱 화면·추적기가 받는 박스"""
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
        shown = [d for d in preds.get(img["id"], []) if d[5] > THRESHOLD_APP]
        if dedup:
            shown = app_dedup(shown, names)
        for d in sorted(shown, key=lambda d: -d[5]):
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


def compare_preds(gt: dict, pa: dict[int, list], pb: dict[int, list], thr: float = THRESHOLD_APP) -> dict:
    """두 검출 묶음(a 기준)을 앱 임계값(신뢰도 ≥ 0.3 — YoloDotNet 은 0.3 미만만 버린다)에서 견준다.
    이미지마다 같은 클래스끼리 IoU 큰 순으로 1:1 짝짓는다(IoU 0.5 이상 — NpuBench 의 일치도와 같은 방식).
    좌표 차는 짝지은 박스의 네 변 중 가장 큰 화소 차"""
    n_a = n_b = matched = same_images = 0
    ious, dconf, dcoord = [], [], []
    for img in gt["images"]:
        a = [d for d in pa.get(img["id"], []) if d[5] >= thr]
        b = [d for d in pb.get(img["id"], []) if d[5] >= thr]
        n_a, n_b = n_a + len(a), n_b + len(b)
        pairs = sorted(((iou(x, y), i, j) for i, x in enumerate(a) for j, y in enumerate(b) if x[4] == y[4]), reverse=True)
        used_a, used_b, m = set(), set(), 0
        for v, i, j in pairs:
            if v < 0.5:
                break
            if i in used_a or j in used_b:
                continue
            used_a.add(i)
            used_b.add(j)
            m += 1
            ious.append(v)
            dconf.append(abs(a[i][5] - b[j][5]))
            dcoord.append(max(abs(a[i][k] - b[j][k]) for k in range(4)))
        matched += m
        same_images += m == len(a) == len(b)
    q = lambda xs, p: float(np.percentile(xs, p)) if xs else float("nan")   # noqa: E731
    return {"a": n_a, "b": n_b, "matched": matched, "a_matched": matched / max(1, n_a), "b_matched": matched / max(1, n_b),
            "images_all_matched": same_images, "images": len(gt["images"]),
            "iou_median": q(ious, 50), "iou_p1": q(ious, 1), "dconf_median": q(dconf, 50), "dconf_p99": q(dconf, 99),
            "dconf_max": max(dconf, default=float("nan")), "dcoord_median": q(dcoord, 50), "dcoord_p99": q(dcoord, 99),
            "dcoord_max": max(dcoord, default=float("nan"))}


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
    ap.add_argument("--app-json", action="append", default=[],
                    help="이름:경로 — 앱 경로 그대로(YoloDotNet + DirectML)의 검출. TrackerCheck testsplit 이 쓴 app_<모델>_<방식>.json "
                         "(file_name · category_id = 클래스 + 1 · bbox · score)")
    ap.add_argument("--det-json", action="append", default=[],
                    help="이름:경로 — 이 스크립트가 예전에 쓴 det_<이름>.json 을 다시 채점한다 (같은 gt_test.json 의 이미지 번호여야 한다 — 같은 --out 이나 그 복사본). "
                         "--subset 으로 나눠 보거나 --compare 하려고")
    ap.add_argument("--compare", action="append", default=[],
                    help="이름A:이름B — 이 실행에서 채점한 두 검출 묶음을 앱 임계값(0.3)에서 박스 단위로 견준다 (A 기준)")
    ap.add_argument("--threads", type=int, default=0, help="ONNX Runtime 스레드 수 (0 = 기본)")
    # 기본값 nearest 는 README 2단계 표를 다시 낼 수 있게 그대로 둔다 (그 표의 'Nearest (앱 그대로)' 행)
    ap.add_argument("--yolo-resize", choices=["nearest", "linear"], default="nearest",
                    help="YOLO 레터박스 축소: linear = 지금 앱(2026-11-05 부터 OpenCV 쌍선형 — YoloLetterbox) = Ultralytics, nearest = 그 전의 앱(YoloDotNet 기본값 — 화소는 조금 다르다, 머리말)")
    ap.add_argument("--yolo-no-upscale", action="store_true",
                    help="모델보다 작은 이미지를 키우지 않는다 — YoloDotNet(앱)과 같게. 안 주면 예전처럼 모든 이미지를 640 에 맞춘다")
    ap.add_argument("--yolo-pad", type=int, default=0, help="YOLO 레터박스 여백 값: 0 = 앱(검정), 114 = Ultralytics 의 회색")
    ap.add_argument("--subset", choices=["all", "resized", "small", "wide"], default="all",
                    help="채점할 이미지: resized = 가로나 세로가 640 이상(앱이 줄이는 이미지), small = 둘 다 640 미만(앱은 그대로 둔다), "
                         "wide = 가로/세로 1.65 이상(16:9 등 — 640×384 입력이 640×640 과 같은 배율로 담는다). 결과 이름에 @<subset> 이 붙는다")
    ap.add_argument("--ep", choices=["cpu", "dml"], default="cpu",
                    help="dml = DirectML (onnxruntime-directml 패키지 필요). FP16 모델은 앱처럼 DirectML 로 잰다")
    args = ap.parse_args()
    if not (args.model or args.ultra_json or args.app_json or args.det_json):
        ap.error("--model · --ultra-json · --app-json · --det-json 중 하나 이상 준다")
    yolo_interp = cv2.INTER_LINEAR if args.yolo_resize == "linear" else cv2.INTER_NEAREST_EXACT
    args.out.mkdir(parents=True, exist_ok=True)

    names = read_names(args.data)
    gt_path = args.out / "gt_test.json"
    gt = gt_all = build_gt(args.data, names, gt_path)
    if args.subset != "all":
        # 640 = 앱 YOLO 입력. 이미지 번호는 전체 분할 그대로 둔다(검출 JSON 의 image_id 가 같은 번호를 가리키게)
        def pick(im: dict) -> bool:
            small = im["width"] < 640 and im["height"] < 640
            if args.subset == "wide":   # 640/384 = 1.67 — 이보다 조금 좁은 16:10(1.6)은 세로에 맞춰 더 줄어서 뺀다
                return im["width"] / im["height"] >= 1.65
            return small == (args.subset == "small")
        keep = {im["id"] for im in gt["images"] if pick(im)}
        gt = {"images": [im for im in gt["images"] if im["id"] in keep],
              "annotations": [a for a in gt["annotations"] if a["image_id"] in keep], "categories": gt["categories"]}
        gt_path = args.out / f"gt_test_{args.subset}.json"
        gt_path.write_text(json.dumps(gt))
    keep_ids = {im["id"] for im in gt["images"]}
    suffix = "" if args.subset == "all" else f"@{args.subset}"
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
    scored: dict[str, dict] = {}   # 이 실행에서 채점한 검출 (--compare 용)

    def record(name: str, preds: dict, dets_json: list, extra: dict) -> None:
        # --subset 이면 그 이미지의 검출만 (pycocotools 는 정답에 없는 이미지의 검출을 받지 않는다)
        preds = {k: v for k, v in preds.items() if k in keep_ids}
        dets_json = [d for d in dets_json if d["image_id"] in keep_ids]
        name += suffix
        scored[name] = preds
        det_path = args.out / f"det_{name}.json"
        det_path.write_text(json.dumps(dets_json))
        r = coco_scores(gt_path, det_path, names)
        r["app_point"] = app_point(gt, preds, names)
        r["app_point_dedup"] = app_point(gt, preds, names, dedup=True)
        r.update(extra)
        r["images"] = len(gt["images"])
        results[name] = r
        a, b = r["app_point"], r["app_point_dedup"]
        tail = f" | {r['ep']} {r['ms_per_image']:.0f} ms/장" if "ms_per_image" in r else ""
        print(f"[{name}] AP {r['AP']:.4f} · AP50 {r['AP50']:.4f} · APs {r['APs']:.4f} · APm {r['APm']:.4f} · APl {r['APl']:.4f} · "
              f"AR100 {r['AR100']:.4f} | 임계 0.3: 정밀도 {a['precision']:.3f} 재현율 {a['recall']:.3f} "
              f"(앱 중복 필터 뒤 {b['precision']:.3f} · {b['recall']:.3f}, 검출 {a['tp'] + a['fp']} → {b['tp'] + b['fp']}){tail}", flush=True)
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
            d = (run_yolo(sess, bgr, yolo_interp, args.yolo_pad, args.yolo_no_upscale) if kind == "yolo"
                 else run_rfdetr(sess, bgr, background_last))
            preds[img["id"]] = d
            dets_json += [{"image_id": img["id"], "category_id": c + 1, "bbox": [x1, y1, x2 - x1, y2 - y1], "score": sc}
                          for x1, y1, x2, y2, c, sc in d]
        dt = time.time() - t0
        record(name, preds, dets_json, {
            "ep": args.ep,
            "preprocess": (f"letterbox-{args.yolo_resize}" + (f"-pad{args.yolo_pad}" if args.yolo_pad else "")
                           + ("-noupscale" if args.yolo_no_upscale else "")) if kind == "yolo"
                          else "stretch-linear-float",
            "ms_per_image": dt / len(gt["images"]) * 1000,   # 디코딩·전처리·후처리 포함 — 참고용
        })

    # 바깥에서 낸 검출을 같은 평가기로 — Ultralytics val(그쪽 전처리 — rect 배치 · 회색 114 여백 · 쌍선형)과 앱 경로(TrackerCheck testsplit).
    # 이미지는 파일 이름(확장자 뺌)으로 맞춘다. 둘 다 클래스 번호 + 1 을 category_id 로 쓴다
    by_stem = {Path(img["file_name"]).stem: img["id"] for img in gt_all["images"]}   # --subset 밖 이미지의 검출은 record 가 뺀다

    def load_external(name: str, path: str) -> tuple[dict, list]:
        preds, dets_json = {}, []
        for d in json.loads(Path(path).read_text(encoding="utf-8")):
            if "file_name" not in d and isinstance(d["image_id"], int):
                iid = d["image_id"]   # 이 스크립트가 쓴 det_<이름>.json — gt_test.json 의 이미지 번호
            else:
                stem = Path(d["file_name"]).stem if "file_name" in d else str(d["image_id"])
                if stem not in by_stem:
                    raise SystemExit(f"{name}: {stem} 이 시험 분할에 없다 — 다른 분할의 검출?")
                iid = by_stem[stem]
            x, y, w, h = map(float, d["bbox"])
            c, sc = int(d["category_id"]) - 1, float(d["score"])
            preds.setdefault(iid, []).append((x, y, x + w, y + h, c, sc))
            dets_json.append({"image_id": iid, "category_id": c + 1, "bbox": [x, y, w, h], "score": sc})
        return preds, dets_json

    for spec in args.ultra_json:
        name, path = spec.split(":", 1)
        record(name, *load_external(name, path), {"ep": "ultralytics-val", "preprocess": "ultralytics-val"})
    for spec in args.app_json:
        name, path = spec.split(":", 1)
        record(name, *load_external(name, path), {"ep": "app-yolodotnet-dml", "preprocess": "app (YoloDotNet ResizeImageProportional)"})
    for spec in args.det_json:
        name, path = spec.split(":", 1)
        record(name, *load_external(name, path), {"ep": "rescored", "preprocess": f"rescored {Path(path).name}"})

    compare_path = args.out / "compare.json"
    compares = json.loads(compare_path.read_text()) if compare_path.exists() else {}
    for spec in args.compare:
        a_name, b_name = (n + suffix for n in spec.split(":", 1))
        spec += suffix
        missing = [n for n in (a_name, b_name) if n not in scored]
        if missing:
            raise SystemExit(f"--compare {spec}: 이 실행에서 채점하지 않은 이름 {missing}")
        c = compare_preds(gt, scored[a_name], scored[b_name])
        compares[spec] = c
        print(f"[비교 {a_name} → {b_name}] 임계 0.3 검출 {c['a']} · {c['b']}, 짝 {c['matched']} ({c['a_matched']:.4f} · {c['b_matched']:.4f}) · "
              f"모두 짝지은 이미지 {c['images_all_matched']}/{c['images']} · IoU 중앙 {c['iou_median']:.4f} 1% {c['iou_p1']:.4f} · "
              f"신뢰도 차 중앙 {c['dconf_median']:.4f} 99% {c['dconf_p99']:.4f} 최대 {c['dconf_max']:.4f} · "
              f"좌표 차 중앙 {c['dcoord_median']:.2f} 99% {c['dcoord_p99']:.2f} 최대 {c['dcoord_max']:.2f} px", flush=True)
        compare_path.write_text(json.dumps(compares, ensure_ascii=False, indent=1))

    print(f"\n결과 → {results_path}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
