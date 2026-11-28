# tests/ui/media/vfr_cfrmeta.mp4 을 만든 스크립트 (Test-VfrPlayback 세 번째 파일 — 2026-11-28 ChatGPT 리뷰 5번 재현 표본).
# 평균 fps·프레임 수는 정상으로 보이지만 프레임 간격이 일정하지 않은 MP4(VFR): OpenCV 가 FPS=23·FRAME_COUNT=552 로 읽어 앱이 CFR 로 판정한다(metaReliable).
# 프레임 번호로 센 시각(번호/23)과 실제 표시 시각(PTS)이 12초 지점에서 3.65초 어긋난다 — 예전 앱은 번호로 센 시각을 위치·이벤트 시각으로 썼다.
# 다시 만들 땐 판을 고정한 이 명령으로 (테스트는 아래 시각표와 막대 부호에 기댄다):
#   uv run --no-project --python 3.12 --with av==19.0.2 --with numpy==2.5.4 --with opencv-python-headless==5.0.0.93 python -I tests/ui/media/make_vfr_cfrmeta.py <out.mp4>
#
# - H.264(libx264, B 프레임 없음, 키프레임 60장마다, crf 23) 640x360, MP4 타임베이스 1/15360
# - 시각표: 0~6초 30fps(180장, k·1000/30 ms 반올림) · 6~12초 0.5초마다 1장(12장 — 6,000·6,500·…·11,500) · 12~24초 30fps(360장, 12,000 + k·1000/30 ms) — 모두 552장, 마지막 23,967 ms
#   — 화면 녹화의 '변화 없는 동안 프레임이 드문' 모습. MP4 먹서가 평균 프레임레이트(23)를 적어 OpenCV 가 그 값을 FPS 로 읽는다
# - 맨 위 띠(높이 48)에 16칸(칸마다 폭 40) 막대 부호: 0번 칸은 늘 흰색(표지), 1~15번 칸은 그 프레임의 실제 시각(ms)의 15비트(큰 자리부터, 흰색 = 1).
#   테스트가 화면에 보인 프레임을 앱과 무관하게 읽는 길이다(앱의 시각 표시와 견준다). 가운데엔 시각·프레임 번호 글자
import sys
from fractions import Fraction

import av
import cv2
import numpy as np

sys.stdout.reconfigure(encoding="utf-8")
out_path = sys.argv[1]
W, H, TB = 640, 360, 15360
FONT = cv2.FONT_HERSHEY_SIMPLEX


def timeline():
    a = [round(k * 1000 / 30) for k in range(180)]
    b = [6000 + 500 * k for k in range(12)]
    c = [12000 + round(k * 1000 / 30) for k in range(360)]
    return a + b + c


def render(i, tms):
    sparse = 6000 <= tms < 12000
    img = np.full((H, W, 3), (40, 36, 32), np.uint8)
    for cell in range(16):   # 막대 부호 — 0번 칸은 표지(흰색), 1~15번은 tms 의 비트(큰 자리부터)
        bit = 1 if cell == 0 else (tms >> (15 - cell)) & 1
        color = (240, 240, 240) if bit else (16, 16, 16)
        cv2.rectangle(img, (cell * 40, 0), (cell * 40 + 39, 47), color, -1)
    cv2.putText(img, f"{tms / 1000:6.3f} s", (70, 175), FONT, 2.2, (240, 240, 240), 3, cv2.LINE_AA)
    cv2.putText(img, f"frame {i}  ({'0.5 s apart' if sparse else '30 fps'})", (74, 225), FONT, 0.9, (210, 210, 210), 2, cv2.LINE_AA)
    cv2.rectangle(img, (60, 300), (580, 316), (110, 110, 110), 1)
    cv2.rectangle(img, (61, 301), (61 + int(518 * min(tms, 24000) / 24000), 315), (80, 180, 240), -1)
    return img


def main():
    ts = timeline()
    cont = av.open(out_path, "w", format="mp4")
    st = cont.add_stream("libx264", rate=30)
    st.width, st.height, st.pix_fmt = W, H, "yuv420p"
    st.time_base = Fraction(1, TB)
    st.codec_context.time_base = Fraction(1, TB)
    st.options = {"bf": "0", "g": "60", "preset": "medium", "crf": "23"}
    for i, t in enumerate(ts):
        f = av.VideoFrame.from_ndarray(render(i, t), format="bgr24").reformat(format="yuv420p")
        f.pts = round(t * TB / 1000)
        f.time_base = Fraction(1, TB)
        for p in st.encode(f):
            cont.mux(p)
    for p in st.encode():
        cont.mux(p)
    cont.close()

    # 확인: OpenCV 가 읽는 FPS·프레임 수, 프레임마다 PTS 와 막대 부호가 같은지, 테스트가 쓰는 시크 목표마다 그 이상인 첫 프레임
    cap = cv2.VideoCapture(out_path, cv2.CAP_FFMPEG)
    fps, n = cap.get(cv2.CAP_PROP_FPS), cap.get(cv2.CAP_PROP_FRAME_COUNT)
    pts, bad = [], 0
    while True:
        ok, img = cap.read()
        if not ok:
            break
        p = cap.get(cv2.CAP_PROP_POS_MSEC)
        pts.append(p)
        cells = [img[18:30, c * 40 + 14:c * 40 + 26].mean() > 128 for c in range(16)]
        code = sum(1 << (15 - c) for c in range(1, 16) if cells[c])
        if not cells[0] or abs(code - p) > 1:
            bad += 1
    print(f"저장 {out_path} · OpenCV FPS {fps:.3f} · FRAME_COUNT {n:.0f} · 읽은 프레임 {len(pts)} · 마지막 PTS {pts[-1]:.0f} ms · 막대 부호가 PTS 와 다른 프레임 {bad}")
    print(f"번호/fps 와 PTS 차 최대 {max(abs(q - i / fps * 1000) for i, q in enumerate(pts)):.0f} ms")
    for target in (3000, 9200, 15000):
        print(f"  {target} ms 이상인 첫 프레임: {min(q for q in pts if q >= target - 1):.0f} ms")


if __name__ == "__main__":
    main()
