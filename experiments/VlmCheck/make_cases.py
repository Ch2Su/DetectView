"""로컬 VLM 유고 후보 확인 실험 — 장면마다 입력 그림을 만든다 (README '만드는 법').

cases.json 의 장면마다 그 시각 프레임을 앱 모델(저장소 루트 best.onnx — YOLO26m 640x384 end-to-end)로 검출해
'hint' 점을 담은(없으면 가장 가까운) 박스를 후보로 고른다. 'box' 를 준 장면(가짜 검출·흐린 화면)은 그대로 쓰고,
'box_from' 은 다른 장면에서 고른 박스를 빌린다(검은 화면).

out/cases/<id>/ 에:
  full.jpg   화면 전체 + 후보 빨간 박스              (입력 A)
  crop.jpg   후보 주변(박스의 3배, 한 변 최소 256px)을 잘라 긴 변 512px 로 + 박스   (B 에 더함)
  prev.jpg · next.jpg   2초 전·뒤 화면 + 같은 자리 박스   (C 에 더함)
  case.json  고른 박스·검출 라벨·신뢰도·프레임 크기
그리고 out/review.jpg — 모든 장면의 full.jpg 를 모아 박스가 맞게 골렸는지 눈으로 본다(다른 검출은 얇은 초록 박스).

    uv run --no-project --python 3.12 --with opencv-python-headless --with numpy --with onnxruntime experiments/VlmCheck/make_cases.py
"""
import argparse
import ast
import json
from pathlib import Path

import cv2
import numpy as np
import onnxruntime as ort

HERE = Path(__file__).resolve().parent
RED, GREEN = (0, 0, 255), (0, 200, 0)


def letterbox(img, mw, mh):
    """앱(YoloLetterbox)처럼 쌍선형으로 줄여 검은 여백 가운데에 둔다."""
    h, w = img.shape[:2]
    s = min(mw / w, mh / h)
    nw, nh = int(round(w * s)), int(round(h * s))
    canvas = np.zeros((mh, mw, 3), np.uint8)
    x0, y0 = (mw - nw) // 2, (mh - nh) // 2
    canvas[y0:y0 + nh, x0:x0 + nw] = cv2.resize(img, (nw, nh), interpolation=cv2.INTER_LINEAR)
    return canvas, s, x0, y0


class Detector:
    def __init__(self, model):
        self.sess = ort.InferenceSession(str(model), providers=["CPUExecutionProvider"])
        self.inp = self.sess.get_inputs()[0]
        self.mh, self.mw = self.inp.shape[2], self.inp.shape[3]
        self.names = ast.literal_eval(self.sess.get_modelmeta().custom_metadata_map["names"])

    def __call__(self, img, conf=0.25):
        lb, s, x0, y0 = letterbox(img, self.mw, self.mh)
        x = cv2.cvtColor(lb, cv2.COLOR_BGR2RGB).transpose(2, 0, 1)[None].astype(np.float32) / 255
        out = self.sess.run(None, {self.inp.name: x})[0][0]          # [300, 6] = x1,y1,x2,y2,점수,클래스 (입력 좌표)
        dets = []
        for x1, y1, x2, y2, sc, c in out:
            if sc >= conf:
                dets.append({"box": [float((x1 - x0) / s), float((y1 - y0) / s), float((x2 - x0) / s), float((y2 - y0) / s)],
                             "score": float(sc), "label": self.names[int(c)]})
        return dets


def read_frame(path, t):
    cap = cv2.VideoCapture(str(path), cv2.CAP_FFMPEG)
    fps = cap.get(cv2.CAP_PROP_FPS)
    n = int(cap.get(cv2.CAP_PROP_FRAME_COUNT))
    k = min(max(0, int(round(t * fps))), n - 1)
    cap.set(cv2.CAP_PROP_POS_FRAMES, k)
    ok, f = cap.read()
    cap.release()
    if not ok:
        raise RuntimeError(f"{path} {t}s 를 못 읽음")
    return f, k / fps, n / fps


def pick(dets, hint):
    hx, hy = hint
    inside = [d for d in dets if d["box"][0] <= hx <= d["box"][2] and d["box"][1] <= hy <= d["box"][3]]
    pool = inside or dets
    if not pool:
        return None
    dist = lambda d: np.hypot((d["box"][0] + d["box"][2]) / 2 - hx, (d["box"][1] + d["box"][3]) / 2 - hy)
    best = min(pool, key=dist)
    return best if inside or dist(best) < 150 else None


def draw(img, box, color=RED, thick=None):
    out = img.copy()
    t = thick or max(2, img.shape[1] // 400)
    x1, y1, x2, y2 = (int(round(v)) for v in box)
    cv2.rectangle(out, (x1, y1), (x2, y2), color, t)
    return out


def crop_around(img, box, factor=3.0, min_side=256, long_side=512):
    h, w = img.shape[:2]
    cx, cy = (box[0] + box[2]) / 2, (box[1] + box[3]) / 2
    side = max(min_side, factor * max(box[2] - box[0], box[3] - box[1]))
    x1, y1 = int(max(0, cx - side / 2)), int(max(0, cy - side / 2))
    x2, y2 = int(min(w, cx + side / 2)), int(min(h, cy + side / 2))
    c = draw(img, box, thick=2)[y1:y2, x1:x2]
    s = long_side / max(c.shape[:2])
    return cv2.resize(c, (int(round(c.shape[1] * s)), int(round(c.shape[0] * s))),
                      interpolation=cv2.INTER_CUBIC if s > 1 else cv2.INTER_AREA)


def iou(a, b):
    ix = max(0.0, min(a[2], b[2]) - max(a[0], b[0]))
    iy = max(0.0, min(a[3], b[3]) - max(a[1], b[1]))
    inter = ix * iy
    union = (a[2] - a[0]) * (a[3] - a[1]) + (b[2] - b[0]) * (b[3] - b[1]) - inter
    return inter / union if union > 0 else 0.0


def stopped_seconds(det, path, t, box, step=0.5, horizon=10.0):
    """3차: 후보가 그 자리에 몇 초째 있었나 — 0.5초씩 거슬러 올라가며 같은 자리 검출(IoU ≥ 0.5, 또는 중심이 박스 크기의
    35% 안)이 이어지는 동안. 앱 추적기(정지 판정은 두 변의 움직임)를 흉내 낸 대략값이다"""
    side = max(box[2] - box[0], box[3] - box[1])
    cx, cy = (box[0] + box[2]) / 2, (box[1] + box[3]) / 2
    s = 0.0
    while s + step <= min(horizon, t):
        f, _, _ = read_frame(path, t - s - step)
        ok = False
        for d in det(f):
            b = d["box"]
            near = np.hypot((b[0] + b[2]) / 2 - cx, (b[1] + b[3]) / 2 - cy) < 0.35 * side
            if iou(b, box) >= 0.5 or near:
                ok = True
                break
        if not ok:
            break
        s += step
    return s


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--media", default="E:/DetectView/publish")
    ap.add_argument("--model", default=str(HERE.parents[1] / "best.onnx"))
    ap.add_argument("--out", default=str(HERE / "out"))
    a = ap.parse_args()
    media, out = Path(a.media), Path(a.out)
    det = Detector(a.model)
    spec = json.loads((HERE / "cases.json").read_text(encoding="utf-8"))
    cases, round3, given_stop = spec["cases"], spec.get("round3", {}), spec.get("round3_stopped_s", {})
    chosen, review = {}, []
    for c in cases:
        path = media / c["video"]
        frame, t, dur = read_frame(path, c["t"])
        dets = det(frame)
        if "box" in c:
            cand = {"box": c["box"], "label": c["label"], "score": c["score"], "manual": True}
        elif "box_from" in c:
            cand = dict(chosen[c["box_from"]], manual=True)
        else:
            cand = pick(dets, c["hint"])
            if cand is None:
                raise SystemExit(f"{c['id']}: hint {c['hint']} 근처에 검출이 없음 — 검출 {len(dets)}개")
            cand = dict(cand, manual=False)
            if "label" in c:   # 검출기의 틀린 라벨을 흉내(박스는 검출 그대로)
                cand.update(label=c["label"], score=c.get("score", cand["score"]))
        chosen[c["id"]] = cand
        d = out / "cases" / c["id"]
        d.mkdir(parents=True, exist_ok=True)
        cv2.imwrite(str(d / "full.jpg"), draw(frame, cand["box"]), [cv2.IMWRITE_JPEG_QUALITY, 90])
        cv2.imwrite(str(d / "crop.jpg"), crop_around(frame, cand["box"]), [cv2.IMWRITE_JPEG_QUALITY, 90])
        # 3차: 조금 좁게(박스의 2배, 한 변 최소 160px) — 옆의 사고 현장에 덜 끌리는지
        cv2.imwrite(str(d / "crop2.jpg"), crop_around(frame, cand["box"], factor=2.0, min_side=160), [cv2.IMWRITE_JPEG_QUALITY, 90])
        for tag, dt in (("prev", -2.0), ("next", 2.0)):
            f2, _, _ = read_frame(path, min(max(0.0, c["t"] + dt), dur - 0.1))
            cv2.imwrite(str(d / f"{tag}.jpg"), draw(f2, cand["box"]), [cv2.IMWRITE_JPEG_QUALITY, 90])
        info = {"id": c["id"], "video": c["video"], "t": round(t, 3), "frame": [frame.shape[1], frame.shape[0]],
                "box": [round(v, 1) for v in cand["box"]], "label": cand["label"], "score": round(cand["score"], 3), "manual": cand["manual"]}
        if c["id"] in round3:
            info["stopped_s"] = given_stop[c["id"]] if c["id"] in given_stop else stopped_seconds(det, path, t, cand["box"])
        (d / "case.json").write_text(json.dumps(info, ensure_ascii=False), encoding="utf-8")
        print(f"{c['id']}: {c['video']} {t:.2f}s 후보 {cand['label']} {cand['score']:.2f} {info['box']}{' (직접 준 박스)' if cand['manual'] else ''} · 검출 {len(dets)}개"
              f"{' · 3차 멈춤 ' + str(info['stopped_s']) + '초' if 'stopped_s' in info else ''}")
        # 확인용: 다른 검출은 얇은 초록, 후보는 빨강, 장면 번호
        r = frame.copy()
        for o in dets:
            r = draw(r, o["box"], GREEN, 1)
        r = cv2.resize(draw(r, cand["box"], RED, 3), (640, int(640 * frame.shape[0] / frame.shape[1])))
        r = cv2.copyMakeBorder(r, 0, 360 - r.shape[0], 0, 0, cv2.BORDER_CONSTANT) if r.shape[0] < 360 else r[:360]
        cv2.putText(r, f"{c['id']} {cand['label']} {cand['score']:.2f}", (8, 28), cv2.FONT_HERSHEY_SIMPLEX, 0.8, (0, 255, 255), 2)
        review.append(r)
    while len(review) % 4:
        review.append(np.zeros_like(review[0]))
    cv2.imwrite(str(out / "review.jpg"), np.vstack([np.hstack(review[i:i + 4]) for i in range(0, len(review), 4)]),
                [cv2.IMWRITE_JPEG_QUALITY, 85])


if __name__ == "__main__":
    main()
