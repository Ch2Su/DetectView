"""시간순 그림(결과 15) — 장면마다 같은 자리를 3초 전 · 1.5초 전 · 지금에 키운 그림과, 그 사이 후보 차량이 움직였는지 잰 값.

make_cases.py 가 만든 out/cases/<id>/case.json 의 박스·시각을 그대로 쓴다(그 그림들은 다시 만들지 않는다 — 4차 결과와 같은 박스여야 견줄 수 있다).
차량 후보만 한다: 사람 후보(P10·P12·N5)와 멈춘 시간을 직접 준 장면(cases.json 의 round3_stopped_s — 흐림·검은 화면·가짜 검출,
카메라가 도는 중인 T2)은 뺀다 — 움직임의 정답을 정할 수 없다.

out/cases/<id>/ 에:
  seq1.jpg · seq2.jpg · seq3.jpg   3초 전 · 1.5초 전 · 지금 — crop.jpg 와 같은 사각형(박스의 3배)·같은 자리의 빨간 박스. seq3 은 crop.jpg 와 같은 그림
  seq.jpg     셋을 왼쪽부터 이어 붙인 한 장(사이에 흰 띠)
  seq.json    잰 값 — 앞 두 시각에 박스와 가장 겹치는 차량 검출의 IoU, 0.5초씩 거슬러 잰 멈춘 시간, 움직임 정답(motion)
그리고 out/seq_review.jpg — 모든 장면의 seq.jpg 에 정답을 적어 모은 것(눈으로 확인).

움직임 정답(모델을 돌리기 전에 정한 기준 — 검출기로 잰다):
  stationary  3초 전·1.5초 전 모두 같은 자리에 차량 검출이 있고(IoU ≥ 0.7) 멈춘 시간이 3초 이상
  moved       멈춘 시간이 1.5초 미만이고, 앞 두 시각 중 한 번은 그 자리에 차량 검출이 없으며(IoU < 0.3) 같은 자리 검출은 한 번도 없다(IoU < 0.5)
  ambiguous   그 밖 — 검출이 흔들렸거나(작은 박스·경광등) 다른 차가 같은 자리를 지나간 장면. cases.json 의 seq_by_eye 에
              Claude 가 이어 붙인 그림을 보고 적은 답이 있으면 그것을 정답으로 쓴다(seq.json 의 by_eye — 채점표는 잰 것만 · 모두 둘 다 낸다)
처음엔 moved 를 '3초 전 IoU < 0.3' 으로만 정했는데 다른 차가 3초 전에 그 자리를 지나간 장면(C5·C6·N2 — 1.5초 전엔 비어 있다)이 빠져
위처럼 고쳤다 — 모델을 돌리기 전이다.

    uv run --no-project --python 3.12 --with opencv-python-headless --with numpy --with onnxruntime experiments/VlmCheck/make_seq.py
"""
import argparse
import json
from pathlib import Path

import cv2
import numpy as np

from make_cases import MEDIA, REPO, Detector, crop_around, iou, pick, read_frame, stopped_seconds
from run_vlm import SEQ_OFFSETS

HERE = Path(__file__).resolve().parent
GAP = 8                       # 이어 붙일 때 그림 사이 흰 띠(px)
SAME, GONE = 0.7, 0.3         # 같은 자리 · 그 자리에 없음으로 볼 IoU
STOPPED, MOVING = 3.0, 1.5    # 멈춘 시간(초) — 이상이면 멈춤 · 미만이면 움직임
JPEG = [cv2.IMWRITE_JPEG_QUALITY, 90]


def classify(ious, stopped_s):
    if min(ious) >= SAME and stopped_s >= STOPPED:
        return "stationary"
    if stopped_s < MOVING and min(ious) < GONE and max(ious) < 0.5:
        return "moved"
    return "ambiguous"


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--media", default=str(MEDIA))
    old = REPO / "publish" / "best.yolo26m-1105.onnx"   # 장면 박스를 만든 11-07 의 7클래스 모델(README 결과 13) — 없으면 루트 모델
    ap.add_argument("--model", default=str(old if old.exists() else REPO / "best.onnx"))
    ap.add_argument("--out", default=str(HERE / "out"))
    a = ap.parse_args()
    media, out = Path(a.media), Path(a.out)
    det = Detector(a.model)
    print(f"검출 모델 {Path(a.model).name} · 시각 {SEQ_OFFSETS}")
    spec = json.loads((HERE / "cases.json").read_text(encoding="utf-8"))
    skip, by_eye = set(spec.get("round3_stopped_s", {})), spec.get("seq_by_eye", {})
    review = []
    for c in spec["cases"]:
        d = out / "cases" / c["id"]
        info = json.loads((d / "case.json").read_text(encoding="utf-8"))
        if info["label"] == "person" or c["id"] in skip:
            continue
        path = media / c["video"]
        # 박스는 make_cases 처럼 그 프레임에서 다시 고른다 — case.json 의 박스는 0.1px 로 반올림돼 자른 그림이 1px 어긋날 수 있다
        cand = pick(det(read_frame(path, c["t"])[0]), c["hint"])
        box = cand["box"] if cand and [round(v, 1) for v in cand["box"]] == info["box"] else info["box"]
        if box is info["box"]:
            print(f"{c['id']}: 다시 고른 박스가 case.json 과 다름 — case.json 의 박스를 쓴다")
        panels, ious, times = [], [], []
        for off in SEQ_OFFSETS:
            frame, t, _ = read_frame(path, c["t"] + off)
            times.append(round(t, 3))
            panels.append(crop_around(frame, box))
            if off:   # 그 시각에 같은 자리에 차량이 있었나 — 사람 검출은 빼고 가장 겹치는 것
                ious.append(round(max((iou(x["box"], box) for x in det(frame) if x["label"] != "person"), default=0.0), 3))
        for i, p in enumerate(panels):
            cv2.imwrite(str(d / f"seq{i + 1}.jpg"), p, JPEG)
        same_as_crop = (d / "seq3.jpg").read_bytes() == (d / "crop.jpg").read_bytes()
        bar = np.full((panels[0].shape[0], GAP, 3), 255, np.uint8)
        strip = np.hstack([x for p in panels for x in (p, bar)][:-1])
        cv2.imwrite(str(d / "seq.jpg"), strip, JPEG)
        stopped = info["stopped_s"] if "stopped_s" in info else stopped_seconds(det, path, info["t"], box)
        measured = classify(ious, stopped)
        motion = by_eye.get(c["id"], measured) if measured == "ambiguous" else measured   # 잰 것이 먼저 — 눈으로 본 답은 잰 것이 모호할 때만
        seq = {"id": c["id"], "offsets": list(SEQ_OFFSETS), "t": times, "iou": ious, "stopped_s": stopped, "measured": measured, "motion": motion,
               "by_eye": measured == "ambiguous" and motion != "ambiguous", "strip": [strip.shape[1], strip.shape[0]], "same_as_crop": same_as_crop}
        (d / "seq.json").write_text(json.dumps(seq, ensure_ascii=False), encoding="utf-8")
        print(f"{c['id']:4} {motion:10}{' (눈)' if seq['by_eye'] else '     '} IoU {ious[0]:.2f} · {ious[1]:.2f} · 멈춘 지 {stopped:g}초 · {info['label']} · 그림 {strip.shape[1]}x{strip.shape[0]}"
              f"{'' if same_as_crop else ' · seq3 이 crop.jpg 와 다름!'} · {c['name']}")
        r = cv2.resize(strip, (780, int(round(780 * strip.shape[0] / strip.shape[1]))))
        r = cv2.copyMakeBorder(r, 26, max(0, 264 - r.shape[0]), 0, 0, cv2.BORDER_CONSTANT)[:290]
        cv2.putText(r, f"{c['id']} {motion} iou {ious[0]:.2f}/{ious[1]:.2f} stop {stopped:g}s", (6, 19), cv2.FONT_HERSHEY_SIMPLEX, 0.6, (0, 255, 255), 2)
        review.append(r)
    while len(review) % 2:
        review.append(np.zeros_like(review[0]))
    cv2.imwrite(str(out / "seq_review.jpg"), np.vstack([np.hstack(review[i:i + 2]) for i in range(0, len(review), 2)]), [cv2.IMWRITE_JPEG_QUALITY, 85])
    print(f"확인용 {out / 'seq_review.jpg'}")


if __name__ == "__main__":
    main()
