# 양자화 변형을 NPU 에 올리기 전에 CPU 에서 빠르게 걸러 본다: 기준 FP32 모델(CPU) 대비 검출 일치도.
# 기준이 GPU 가 아니라 CPU FP32 라는 것 말고는 Program.cs 의 Stats 와 같은 규칙이다
# (신뢰도 ≥ 0.3, 같은 클래스·IoU ≥ 0.5 를 IoU 큰 순으로 1:1). 최종 숫자는 NpuBench(GPU 기준)로 잰다.
#
# 실행: uv run --no-project --python 3.12 --with onnxruntime==1.23.2 --with onnx --with numpy --with opencv-python-headless screen.py [--ref 기준.onnx] out/best.a8w8.onnx ...
#   --ref 기본값은 저장소 루트 best.onnx. 변형마다 원본 MD5(메타데이터 npu_source_md5 — tools/npu_quantize.py 가 넣는다)를 기준 파일 MD5 와
#   맞대어 보고 다르면 건너뛴다(앱의 CheckNpuModel 과 같은 규칙, 대소문자 무시). 예전엔 기준이 루트 best.onnx 로 고정이라 배포 폴더 모델로 만든
#   변형을 넣어도 경고 없이 다른 원본과 비교한 숫자가 나왔다 — 클래스 구성이 다르면(COCO 80클래스 등) 같은 번호가 다른 물체를 가리키고,
#   같은 7클래스라도 양자화 영향과 모델 차이가 섞인다 (이슈 #6)
# 전처리·보정 영상 목록은 tools/npu_quantize.py 의 것을 그대로 쓴다 (앱 YoloDotNet 과 같은 전처리)
import argparse
import hashlib
import pathlib
import sys

import cv2
import numpy as np
import onnxruntime as ort

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[2] / "tools"))
from npu_quantize import VIDEOS, letterbox, repo  # noqa: E402

CONF, IOU = 0.3, 0.5
PER_VIDEO = 20


def eval_frames():
    # Program.cs 와 같은 (i+0.5)/n 위치 (보정 프레임 i/n 과 겹치지 않음)
    frames = []
    for name in VIDEOS:
        path = repo / "publish" / name
        if not path.exists():
            continue
        cap = cv2.VideoCapture(str(path))
        count = int(cap.get(cv2.CAP_PROP_FRAME_COUNT))
        for i in range(PER_VIDEO):
            cap.set(cv2.CAP_PROP_POS_FRAMES, int((i + 0.5) * count / PER_VIDEO))
            ok, img = cap.read()
            if ok:
                frames.append(letterbox(img, 640, 640))
        cap.release()
    return frames


def run(model, frames):
    s = ort.InferenceSession(str(model), providers=["CPUExecutionProvider"])
    name = s.get_inputs()[0].name
    return [s.run(None, {name: x})[0][0] for x in frames]


def iou(a, b):
    w = min(a[2], b[2]) - max(a[0], b[0])
    h = min(a[3], b[3]) - max(a[1], b[1])
    if w <= 0 or h <= 0:
        return 0.0
    inter = w * h
    return inter / ((a[2] - a[0]) * (a[3] - a[1]) + (b[2] - b[0]) * (b[3] - b[1]) - inter)


def compare(ref, test):
    nr = nt = nm = 0
    dscore = []
    for r, t in zip(ref, test):
        r, t = r[r[:, 4] >= CONF], t[t[:, 4] >= CONF]
        pairs = sorted(((iou(a, b), i, j) for i, a in enumerate(r) for j, b in enumerate(t)
                        if a[5] == b[5]), reverse=True)
        ur, ut = set(), set()
        for v, i, j in pairs:
            if v < IOU:
                break
            if i in ur or j in ut:
                continue
            ur.add(i)
            ut.add(j)
            dscore.append(abs(r[i, 4] - t[j, 4]))
        nr += len(r)
        nt += len(t)
        nm += len(ur)
    return nr, nt, nm, float(np.mean(dscore)) if dscore else 0.0


def md5(path):
    """tools/npu_quantize.py 의 npu_source_md5 와 같은 형식 (대문자 16진수)"""
    return hashlib.md5(pathlib.Path(path).read_bytes()).hexdigest().upper()


def metadata(path):
    """모델 메타데이터. 메타데이터만 읽으므로 그래프 최적화는 끈다 (앱의 CheckNpuModel 과 같게)"""
    so = ort.SessionOptions()
    so.graph_optimization_level = ort.GraphOptimizationLevel.ORT_DISABLE_ALL
    return ort.InferenceSession(str(path), so, providers=["CPUExecutionProvider"]).get_modelmeta().custom_metadata_map


def find_source(variant, source_md5):
    """원본 MD5 가 source_md5 인 FP32 모델 — 같은 폴더의 짝 이름 원본(<이름>.npu.onnx → <이름>.onnx)·best.onnx,
    저장소 루트·배포 폴더의 best.onnx 가운데. 건너뛴 변형을 어느 기준으로 돌려야 하는지 알려 주려고"""
    candidates = []
    if variant.name.lower().endswith(".npu.onnx"):
        candidates.append(variant.with_name(variant.name[:-len(".npu.onnx")] + ".onnx"))
    candidates += [variant.with_name("best.onnx"), repo / "best.onnx", repo / "publish" / "best.onnx"]
    seen = set()
    for c in candidates:
        key = str(c.resolve()).lower()
        if key in seen or not c.exists():
            continue
        seen.add(key)
        if md5(c) == source_md5.upper():
            return c.resolve()
    return None


def main():
    # 파이프로 받으면 stdout 이 블록 버퍼라 sys.exit 의 stderr 문구가 앞 줄들보다 먼저 보였다 → 줄마다 내보낸다
    sys.stdout.reconfigure(line_buffering=True)
    ap = argparse.ArgumentParser(description="양자화 변형을 CPU 에서 기준 FP32 모델과 비교한다 (NPU 에 올리기 전에 거르기)")
    ap.add_argument("variants", nargs="+", type=pathlib.Path, help="양자화 변형 ONNX (tools/npu_quantize.py 로 만든 것)")
    ap.add_argument("--ref", type=pathlib.Path, default=repo / "best.onnx", help="기준 FP32 모델 (기본: 저장소 루트 best.onnx)")
    a = ap.parse_args()

    ref_path = a.ref.resolve()
    if not ref_path.exists():
        sys.exit(f"기준 모델이 없습니다: {ref_path}")
    ref_md5 = md5(ref_path)
    print(f"기준 FP32(CPU): {ref_path} (MD5 {ref_md5})")

    # 원본이 기준과 같은 변형만 비교한다 — 기준 모델을 돌리기(100장, 수십 초) 전에 거른다.
    # 이름은 준 경로 그대로 보인다 (루트·배포 폴더의 짝이 둘 다 best.npu.onnx 라 이름만으론 못 가린다)
    variants, skipped = [], 0
    for v in a.variants:
        if not v.exists():
            print(f"  건너뜀 {v}: 파일이 없습니다")
            skipped += 1
            continue
        meta = metadata(v)
        src = meta.get("npu_source_md5")
        if src is not None and src.upper() == ref_md5:
            print(f"  비교함 {v}: 원본 MD5 가 기준과 같다 (npu_quant {meta.get('npu_quant', '없음')})")
            variants.append(v)
            continue
        skipped += 1
        if src is None:
            print(f"  건너뜀 {v}: 원본 MD5(npu_source_md5)가 없어 기준 모델에서 만든 것인지 알 수 없습니다 "
                  "(tools/npu_quantize.py 로 만든 모델에만 들어 있다)")
            continue
        print(f"  건너뜀 {v}: 다른 원본에서 만든 변형입니다 (npu_source_md5 {src[:8]}… ≠ 기준 {ref_md5[:8]}…)")
        found = find_source(v.resolve(), src)
        print(f"      이 변형의 원본: {found} — 그 모델을 --ref 로 주고 다시 돌리세요" if found else
              "      원본을 찾지 못했습니다 (같은 폴더·저장소 루트·배포 폴더의 best.onnx 와 모두 다름)")
    if not variants:
        sys.exit("비교할 변형이 없습니다")

    frames = eval_frames()
    if not frames:
        sys.exit(f"평가 프레임이 없습니다 — {repo / 'publish'} 의 {', '.join(VIDEOS)} 가 필요합니다")
    ref = run(ref_path, frames)
    print(f"평가 프레임 {len(frames)}장, 기준 FP32(CPU)")
    for path in variants:
        nr, nt, nm, ds = compare(ref, run(path, frames))
        print(f"  {path.name:<26} 기준 {nr} · 이쪽 {nt} · 일치 {nm} "
              f"(재현율 {100 * nm / max(nr, 1):.1f}%, 정밀도 {100 * nm / max(nt, 1):.1f}%) · 신뢰도 차 평균 {ds:.3f}")
    if skipped:
        sys.exit(f"{skipped}개는 비교하지 않고 건너뛰었습니다 (위 '건너뜀')")


if __name__ == "__main__":
    main()
