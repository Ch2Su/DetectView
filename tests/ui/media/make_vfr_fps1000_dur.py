# tests/ui/media/vfr_fps1000_dur.webm 을 만든 스크립트 (Test-VfrPlayback 두 번째 파일, 이슈 #7 재현 표본).
# 테스트는 이 파일의 프레임 시각에 기대므로(9,015·9,490·13,024·13,521·16,009 ms 등) 다시 만들 땐 판을 고정한 이 명령으로:
#   uv run --no-project --python 3.12 --with av==19.0.2 --with numpy==2.5.4 --with opencv-python-headless==5.0.0.93 python -I tests/ui/media/make_vfr_fps1000_dur.py <out.webm>
# (numpy 의 난수 분포는 판마다 바뀔 수 있다. opencv 는 글자 그리기에만 써서 시각에는 영향이 없다.
#  2026-10-31 만든 표본과 같은 판으로 다시 만들면 Matroska 의 무작위 UID 16바이트만 다르다)
# 끝에 테스트가 쓰는 목표 시각마다 그 시각 이상인 첫 프레임을 찍는다 — 판을 바꿨다면 테스트의 허용 범위와 맞는지 볼 것
#
# - VP8 640x360, 타임베이스 1 ms(Matroska 기본), 프레임 시각은 실제 화면 녹화처럼 누적 지터(간격 정규분포 33.3±9 ms)라
#   FFmpeg 가 표준 프레임레이트를 못 맞춰 r_frame_rate = 1/타임베이스 = 1000 이 된다(OpenCV FPS 는 r_frame_rate 를 먼저 쓴다)
# - 구간: 0~6 초 움직임(~30fps) · 6~12 초 정지 화면(커서 깜빡임만, 0.5초마다 1장) · 12~24 초 움직임(~30fps)
#   — 화면 녹화의 '변화 없는 동안 프레임이 드문' 모습. 평균 fps 가 구간마다 달라 프레임 번호로 시크하면 시각이 어긋난다
# - 키프레임 60장마다(움직임 구간 약 2초), 미리보기(alt-ref)·지연 없음(lag 0) — 실시간 녹화기처럼
# - PyAV(FFmpeg) 의 Matroska 먹서는 스트림 평균 fps 로 TrackEntry DefaultDuration 을 쓰는데, 그게 있으면 읽을 때
#   r_frame_rate 가 그 값(여기선 30)이 되어 버린다 → 같은 길이의 Void(0xEC)로 덮고 Tracks 의 CRC-32 를 다시 계산한다
#   (tests/ui/media/vfr60_nodur.webm 이 Segment Info Duration 을 Void 로 덮은 것과 같은 방식)
import sys
import zlib
from fractions import Fraction

import av
import cv2
import numpy as np

out_path = sys.argv[1]
W, H = 640, 360
FONT = cv2.FONT_HERSHEY_SIMPLEX


def timeline():
    rng = np.random.default_rng(20261031)
    ts = []

    def active(start, end):
        t = float(start)
        while t < end:
            ts.append(int(round(t)))
            t += float(np.clip(rng.normal(33.3, 9.0), 16.0, 60.0))

    def static(start, end):
        t = float(start)
        while t < end:
            ts.append(int(round(t)))
            t += float(rng.normal(500.0, 15.0))

    active(0, 6000)
    static(6000, 12000)
    active(12000, 24000)
    out = []
    for t in ts:   # 반올림으로 같은 ms 가 나오면 1 ms 뒤로
        if out and t <= out[-1]:
            t = out[-1] + 1
        out.append(t)
    return out


def render(i, tms):
    static = 6000 <= tms < 12000
    img = np.full((H, W, 3), (48, 40, 36), np.uint8)                    # 바탕화면
    cv2.rectangle(img, (40, 28), (600, 332), (72, 66, 62), -1)          # 창
    cv2.rectangle(img, (40, 28), (600, 58), (150, 96, 32), -1)          # 제목 표시줄
    cv2.putText(img, "VFR  fps=1000 (time base)  with duration", (52, 50), FONT, 0.55, (255, 255, 255), 1, cv2.LINE_AA)
    cv2.putText(img, f"{tms / 1000:6.3f} s", (70, 165), FONT, 2.2, (240, 240, 240), 3, cv2.LINE_AA)
    cv2.putText(img, f"frame {i}", (74, 215), FONT, 0.9, (210, 210, 210), 2, cv2.LINE_AA)
    cv2.putText(img, "static (cursor blink)" if static else "moving", (74, 252), FONT, 0.7, (180, 200, 220), 1, cv2.LINE_AA)
    # 진행 막대 — 눈으로 위치를 볼 때
    cv2.rectangle(img, (60, 290), (580, 306), (110, 110, 110), 1)
    cv2.rectangle(img, (61, 291), (61 + int(518 * min(tms, 24000) / 24000), 305), (80, 180, 240), -1)
    if static:
        if i % 2 == 0:
            cv2.rectangle(img, (420, 120), (432, 172), (240, 240, 240), -1)   # 깜빡이는 커서
    else:
        x = 60 + int((tms / 4) % 500)
        cv2.circle(img, (x + 10, 268), 10, (60, 200, 255), -1)            # 움직이는 공
    return img


def ebml_id(b, p):
    first = b[p]
    n = 1
    mask = 0x80
    while not (first & mask):
        mask >>= 1
        n += 1
    return int.from_bytes(b[p:p + n], "big"), n


def ebml_size(b, p):
    first = b[p]
    n = 1
    mask = 0x80
    while not (first & mask):
        mask >>= 1
        n += 1
    v = first & (mask - 1)
    for k in range(1, n):
        v = (v << 8) | b[p + k]
    return v, n


def children(b, start, end):
    p = start
    while p < end:
        eid, ni = ebml_id(b, p)
        size, ns = ebml_size(b, p + ni)
        data = p + ni + ns
        yield eid, p, data, data + size
        p = data + size


def void_default_duration(path):
    b = bytearray(open(path, "rb").read())
    patched = 0
    for eid, _, d0, d1 in children(b, 0, len(b)):
        if eid != 0x18538067:   # Segment
            continue
        for sid, _, s0, s1 in children(b, d0, d1):
            if sid != 0x1654AE6B:   # Tracks
                continue
            for tid, _, t0, t1 in children(b, s0, s1):
                if tid != 0xAE:   # TrackEntry
                    continue
                for cid, c_start, _, c_end in children(b, t0, t1):
                    if cid != 0x23E383:   # DefaultDuration
                        continue
                    total = c_end - c_start
                    assert 2 <= total <= 128, total
                    b[c_start:c_end] = bytes([0xEC, 0x80 | (total - 2)]) + bytes(total - 2)   # Void, 같은 길이
                    patched += 1
            # Tracks 첫 자식이 CRC-32(0xBF)면 나머지 내용으로 다시 계산 (Matroska: zlib 과 같은 CRC-32, 리틀 엔디언)
            first = next(children(b, s0, s1))
            if first[0] == 0xBF:
                crc = zlib.crc32(bytes(b[first[3]:s1])) & 0xFFFFFFFF
                b[first[2]:first[3]] = crc.to_bytes(4, "little")
                print("Tracks CRC-32 다시 계산")
    open(path, "wb").write(bytes(b))
    return patched


def main():
    ts = timeline()
    out = av.open(out_path, "w", format="webm")
    st = out.add_stream("libvpx", rate=30)
    st.width, st.height = W, H
    st.pix_fmt = "yuv420p"
    cc = st.codec_context
    cc.time_base = Fraction(1, 1000)
    cc.gop_size = 60
    cc.bit_rate = 300_000
    cc.options = {"deadline": "good", "cpu-used": "4", "auto-alt-ref": "0", "lag-in-frames": "0", "kf-max-dist": "60"}
    for i, t in enumerate(ts):
        frame = av.VideoFrame.from_ndarray(render(i, t), format="bgr24")
        frame.pts = t
        frame.time_base = Fraction(1, 1000)
        for pkt in st.encode(frame):
            out.mux(pkt)
    for pkt in st.encode():
        out.mux(pkt)
    out.close()
    if "--no-patch" in sys.argv:
        print(f"frames={len(ts)} (DefaultDuration 그대로)")
        return
    n = void_default_duration(out_path)
    print(f"frames={len(ts)} first={ts[0]} last={ts[-1]} DefaultDuration voided={n}")
    # Test-VfrPlayback 의 시크 목표(9 s·12.5 s·13 s·13.5 s·16 s) — grab 경로는 목표 이상인 첫 프레임에 선다
    for target in (9000, 12500, 13000, 13500, 16000):
        print(f"  목표 {target} ms → 첫 프레임 {next(t for t in ts if t >= target - 1)} ms")


main()
