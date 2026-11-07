"""VLM 부하 — Ollama qwen3.5:4b 에 4차 관찰 질문(키운 그림 한 장)을 쉬지 않고 보낸다. 정지 파일이 생기면 멈추고 시간을 요약한다.
앱(DetectView)과 GPU 를 나눠 쓸 때의 지연을 재는 Measure-GpuShare.ps1 이 부른다 (README 결과 6).

    uv run --no-project --python 3.12 python gpu_load.py <gpu|cpu> <정지 파일> <기록 파일> [그림]

cpu 는 num_gpu 0 — 모델을 GPU 에 올리지 않는다(앱의 YOLO 와 GPU 를 나눠 쓰지 않는 대안).
그림을 안 주면 out/cases/P7/crop.jpg(test4 덤프트럭 — make_cases.py 가 만든다)."""
import base64
import json
import statistics
import sys
import time
import urllib.request
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
from run_vlm import API, OPTIONS, SYSTEM4, make_schema4  # noqa: E402

MODEL = "qwen3.5:4b"


def post(path, body, timeout):
    req = urllib.request.Request(API + path, data=json.dumps(body).encode(), headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=timeout) as r:
        return json.loads(r.read())


def main():
    mode, stop, log = sys.argv[1], Path(sys.argv[2]), Path(sys.argv[3])
    image = Path(sys.argv[4]) if len(sys.argv) > 4 else HERE / "out" / "cases" / "P7" / "crop.jpg"
    img = base64.b64encode(image.read_bytes()).decode()
    opts = {**OPTIONS, "num_predict": 768, **({"num_gpu": 0} if mode == "cpu" else {})}
    body = {"model": MODEL, "stream": False, "think": False, "keep_alive": "10m", "format": make_schema4(), "options": opts,
            "messages": [{"role": "system", "content": SYSTEM4},
                         {"role": "user", "content": "그림 1장: 1) 후보 주변을 키운 그림\n보이는 것만 JSON 으로 답하라.", "images": [img]}]}
    times, t_end = [], time.time() + 600
    with log.open("w", encoding="utf-8") as f:
        while not stop.exists() and time.time() < t_end:
            t0 = time.time()
            res = post("/api/chat", body, 600)
            dt = time.time() - t0
            times.append(dt)
            f.write(f"{time.strftime('%H:%M:%S')} {dt:.2f}s load {res.get('load_duration', 0) / 1e9:.2f}s\n")
            f.flush()
        if times:
            warm = times[1:] or times   # 첫 호출은 모델을 올리는 시간이 섞인다
            f.write(f"요약 {mode}: {len(times)}번, 첫 호출 {times[0]:.1f}초, 그 뒤 중앙 {statistics.median(warm):.2f}초 · 최대 {max(warm):.2f}초\n")
    # 끝나면 모델을 내린다 — 다음 단계(cpu ↔ gpu)가 새로 올리게, 끝난 뒤 GPU 메모리를 돌려주게
    post("/api/generate", {"model": MODEL, "keep_alive": 0}, 60)


if __name__ == "__main__":
    main()
