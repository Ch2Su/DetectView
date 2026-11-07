"""앱의 VLM 설명이 실험과 같은 그림·요청을 만드는지 견준다 (2026-11-07 — docs/vlm-annotation.md '크롭').

실험 결과(4차 — 단서가 쓸 만하다)는 make_cases.crop_around 의 그림과 run_vlm 4차(D3)의 질문에 대한 것이다. 앱(VlmCrop.cs · VlmAnnotator.cs)이
글자·화소까지 같은지 장면마다 본다:
  1) make_cases.read_frame 으로 원본 프레임을 읽어 PNG 로 둔다 (out/parity/<id>.png)
  2) TrackerCheck vlm 이 앱 코드로 VLM 에 보낼 그림(JPEG 전)과 /api/chat 요청 본문을 쓴다
  3) 그림은 crop_around 와 화소로, 본문은 run_vlm 4차 D3 의 본문과 JSON 으로 견준다 (그림 자리만 빼고 — keep_alive 도 실험 값으로 쓰게 했다)
장면은 cases.json 중 out/cases/<id>/case.json 이 있는 것(make_cases.py 를 먼저 돌린다). TrackerCheck 를 먼저 빌드한다:

    dotnet build experiments/TrackerCheck -c Release
    uv run --no-project --python 3.12 --with opencv-python-headless --with numpy --with onnxruntime experiments/VlmCheck/check_app_parity.py [장면 ...]

종료 코드 = 다른 장면 수.
"""
import json
import subprocess
import sys
from pathlib import Path

import cv2
import numpy as np

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
from make_cases import crop_around, read_frame  # noqa: E402
from run_vlm import IMAGE_TEXT, OPTIONS, SYSTEM4, VARIANTS, make_schema4  # noqa: E402

REPO = HERE.parents[1]
VIDEOS = REPO / "publish"
OUT = HERE / "out" / "parity"


def tracker_check():
    exes = sorted((REPO / "experiments" / "TrackerCheck" / "bin").glob("*/*/TrackerCheck.exe"), key=lambda p: p.stat().st_mtime)
    if not exes:
        sys.exit("TrackerCheck.exe 가 없다 — 먼저 dotnet build experiments/TrackerCheck -c Release")
    return exes[-1]


def expected_body():
    """run_vlm 4차(qwen3.5-4b, D3)가 보낸 본문 — 그림 자리만 '<그림>'."""
    imgs = VARIANTS["D3"]
    lines = [f"그림 {len(imgs)}장: " + " / ".join(f"{i + 1}) {IMAGE_TEXT[k]}" for i, k in enumerate(imgs)), "보이는 것만 JSON 으로 답하라."]
    return {"model": "qwen3.5:4b", "stream": False, "keep_alive": "20m", "format": make_schema4(),
            "messages": [{"role": "system", "content": SYSTEM4},
                         {"role": "user", "content": "\n".join(lines), "images": ["<그림>"]}],
            "options": {**OPTIONS, "num_predict": 768}, "think": False}


def main():
    exe = tracker_check()
    OUT.mkdir(parents=True, exist_ok=True)
    cases = json.loads((HERE / "cases.json").read_text(encoding="utf-8"))["cases"]
    want = set(sys.argv[1:])
    exp = expected_body()
    bad = 0
    body_checked = False
    for c in cases:
        info_path = HERE / "out" / "cases" / c["id"] / "case.json"
        if (want and c["id"] not in want) or not info_path.exists():
            continue
        info = json.loads(info_path.read_text(encoding="utf-8"))
        frame, _, _ = read_frame(VIDEOS / c["video"], c["t"])
        png = OUT / f"{c['id']}.png"
        cv2.imwrite(str(png), frame)
        prefix = OUT / c["id"]
        x1, y1, x2, y2 = info["box"]
        r = subprocess.run([str(exe), "vlm", str(png), *(repr(float(v)) for v in (x1, y1, x2, y2)), str(prefix)],
                           capture_output=True, text=True, encoding="utf-8")
        if r.returncode != 0:
            print(f"{c['id']}: TrackerCheck 실패 — {r.stdout.strip()} {r.stderr.strip()}")
            bad += 1
            continue
        app = cv2.imread(str(prefix) + ".render.png")
        py = crop_around(frame, info["box"])
        if app is None or app.shape != py.shape:
            print(f"{c['id']}: 그림 크기가 다르다 — 앱 {None if app is None else app.shape} · 실험 {py.shape}")
            bad += 1
            continue
        d = np.abs(app.astype(np.int16) - py.astype(np.int16))
        same_pixels = int((d.max(axis=2) == 0).sum())
        total = d.shape[0] * d.shape[1]
        body = json.loads(Path(str(prefix) + ".request.json").read_text(encoding="utf-8"))
        n_img = len(body["messages"][1].get("images", []))
        body["messages"][1]["images"] = ["<그림>"] * n_img
        body_ok = body == exp and list(body["format"]["properties"]) == list(exp["format"]["properties"]) and list(body) == list(exp)
        ok = d.max() == 0 and body_ok
        bad += 0 if ok else 1
        print(f"{c['id']}: 그림 {app.shape[1]}×{app.shape[0]} {'같음' if d.max() == 0 else f'다름 — 최대 차 {d.max()} · 같은 화소 {same_pixels}/{total}'}"
              f" · 요청 본문 {'같음' if body_ok else '다름'}")
        if not body_ok and not body_checked:
            for k in exp:
                if body.get(k) != exp[k]:
                    print(f"   다른 항목: {k}\n     앱  : {json.dumps(body.get(k), ensure_ascii=False)[:400]}\n     실험: {json.dumps(exp[k], ensure_ascii=False)[:400]}")
            body_checked = True
    print(f"다른 장면 {bad}개")
    return bad


if __name__ == "__main__":
    sys.exit(main())
