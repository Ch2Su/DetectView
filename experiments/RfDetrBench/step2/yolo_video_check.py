r"""YOLO 변형을 실제 CCTV 영상(publish/test1~5.mp4)에서 견준다 — 정답이 없어 정확도가 아니라 '무엇이 달라지나'를 본다 (README 'YOLO 남은 여지').

    uv run --no-project --python 3.12 --with onnxruntime-directml --with opencv-python --with numpy `
        experiments/RfDetrBench/step2/yolo_video_check.py experiments\RfDetrBench\out\step2_models

  ① 같은 자리 중복 박스 — end-to-end(앱이 쓰는 꼴) vs NMS 를 넣어 내보낸 판. 앱은 end-to-end 출력의 중복을 Detection.cs 의 DuplicateBoxes 로
     따로 뺀다(CLAUDE.md '절대 건드리면 안 되는 것들' 3번 — 같은 묶음 · 작은 쪽의 80% 이상이 큰 쪽 안 · 넓이비 0.5 이상). NMS 판에서 그게 얼마나 남는지
  ② 640×384 입력 vs 640×640 — 16:9 영상(test1~4, 1280×720)은 두 입력에 같은 배율(0.5)로 들어가 여백만 다르다. 검출이 얼마나 같은지

전처리는 지금 앱과 같다(OpenCV 쌍선형 레터박스, 검은 여백 — eval_test.py 의 letterbox), 신뢰도 임계 0.3(앱), DirectML.
"""
from __future__ import annotations

import sys
from pathlib import Path

import cv2
import numpy as np
import onnxruntime as ort

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
from eval_test import letterbox  # noqa: E402 — 평가와 같은 레터박스

THRESHOLD = 0.3
NAMES = ["bicycle", "bus", "car", "motorcycle", "person", "scooter", "truck"]   # 7클래스 데이터셋 순서 (data.yaml)
VEHICLE = {0, 1, 2, 3, 5, 6}   # Detection.cs 의 묶음 — 차량 6종은 한 묶음, person 은 따로


def find_publish() -> Path:
    for d in [HERE, *HERE.parents]:
        if (d / "publish" / "test4.mp4").exists():
            return d / "publish"
    raise SystemExit("publish/test4.mp4 를 찾지 못했다 (위로 올라가며 찾는다)")


def session(path: Path) -> ort.InferenceSession:
    so = ort.SessionOptions()
    so.enable_mem_pattern = False
    so.execution_mode = ort.ExecutionMode.ORT_SEQUENTIAL
    so.log_severity_level = 3
    return ort.InferenceSession(str(path), so, providers=[("DmlExecutionProvider", {"performance_preference": "high_performance", "device_filter": "gpu"})])


def detect(sess: ort.InferenceSession, bgr: np.ndarray) -> np.ndarray:
    h_in, w_in = sess.get_inputs()[0].shape[2:4]
    x, s, px, py = letterbox(bgr, w_in, cv2.INTER_LINEAR, 0, True, side_h=h_in)
    out = sess.run(None, {sess.get_inputs()[0].name: x})[0][0]
    out = out[out[:, 4] > THRESHOLD]
    h, w = bgr.shape[:2]
    out[:, [0, 2]] = np.clip((out[:, [0, 2]] - px) / s, 0, w)
    out[:, [1, 3]] = np.clip((out[:, [1, 3]] - py) / s, 0, h)
    return out   # [n, 6] x1 y1 x2 y2 점수 클래스 (원본 좌표)


def area(b) -> float:
    return max(0.0, b[2] - b[0]) * max(0.0, b[3] - b[1])


def inter(a, b) -> float:
    return max(0.0, min(a[2], b[2]) - max(a[0], b[0])) * max(0.0, min(a[3], b[3]) - max(a[1], b[1]))


def dup_pairs(d: np.ndarray) -> tuple[int, int]:
    """(DuplicateBoxes 규칙에 걸리는 쌍, 같은 클래스 IoU > 0.7 쌍)"""
    n_rule = n_iou = 0
    for i in range(len(d)):
        for j in range(i + 1, len(d)):
            a, b = d[i], d[j]
            ca, cb = int(a[5]), int(b[5])
            same_group = (ca in VEHICLE and cb in VEHICLE) or ca == cb
            if not same_group:
                continue
            ia, ib, it = area(a), area(b), inter(a, b)
            small, big = min(ia, ib), max(ia, ib)
            if small > 0 and it / small >= 0.8 and small / big >= 0.5:
                n_rule += 1
            if ca == cb and it / (ia + ib - it + 1e-9) > 0.7:
                n_iou += 1
    return n_rule, n_iou


def agree(ref: np.ndarray, test: np.ndarray) -> int:
    """같은 클래스 · IoU 0.5 이상을 신뢰도 높은 기준부터 짝짓는다"""
    used, hit = [False] * len(test), 0
    for r in ref[np.argsort(-ref[:, 4])] if len(ref) else ref:
        best, bi = 0.5, -1
        for j, t in enumerate(test):
            if used[j] or int(t[5]) != int(r[5]):
                continue
            v = inter(r, t) / (area(r) + area(t) - inter(r, t) + 1e-9)
            if v >= best:
                best, bi = v, j
        if bi >= 0:
            used[bi], hit = True, hit + 1
    return hit


def main() -> int:
    models = Path(sys.argv[1]) if len(sys.argv) > 1 else HERE.parent / "out" / "step2_models"
    pub = find_publish()
    e2e, nms, r384 = session(models / "y26s.onnx"), session(models / "y26s-nms25.onnx"), session(models / "y26s-384.onnx")
    print(f"영상: {pub} · 모델 y26s(end-to-end 640) · y26s-nms25(NMS, conf 0.25 — 앱에 넣을 판) · y26s-384(end-to-end 640×384) · 임계 {THRESHOLD}")
    print(f"{'영상':10s} {'프레임':>6s} | {'박스 e2e':>8s} {'NMS':>7s} | {'중복 규칙 쌍 e2e':>14s} {'NMS':>5s} | {'중복 있는 프레임 e2e':>16s} {'NMS':>5s} | "
          f"{'같은 클래스 IoU>0.7 e2e':>20s} {'NMS':>5s} | 640×384 박스 · 640 과 짝")
    for v in ["test1.mp4", "test2.mp4", "test3.mp4", "test4.mp4", "test5.mp4"]:
        cap = cv2.VideoCapture(str(pub / v))
        n = box_e = box_n = rule_e = rule_n = fr_e = fr_n = iou_e = iou_n = box_r = hit_r = 0
        wide = None
        while True:
            ok, f = cap.read()
            if not ok:
                break
            if wide is None:
                wide = f.shape[1] / f.shape[0] >= 1.65
            n += 1
            de, dn = detect(e2e, f), detect(nms, f)
            re_, ie = dup_pairs(de)
            rn, inn = dup_pairs(dn)
            box_e, box_n, rule_e, rule_n, iou_e, iou_n = box_e + len(de), box_n + len(dn), rule_e + re_, rule_n + rn, iou_e + ie, iou_n + inn
            fr_e, fr_n = fr_e + (re_ > 0), fr_n + (rn > 0)
            if wide:
                dr = detect(r384, f)
                box_r, hit_r = box_r + len(dr), hit_r + agree(de, dr)
        cap.release()
        r384txt = f"{box_r} · {100 * hit_r / max(1, box_e):.1f}% (640 의 {box_e} 중 {hit_r})" if wide else "— (16:9 아님)"
        print(f"{v:10s} {n:6d} | {box_e:8d} {box_n:7d} | {rule_e:14d} {rule_n:5d} | {fr_e:16d} {fr_n:5d} | {iou_e:20d} {iou_n:5d} | {r384txt}", flush=True)
    return 0


if __name__ == "__main__":
    sys.exit(main())
