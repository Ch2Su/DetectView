# Ultralytics YOLO ONNX(FP32)를 AMD NPU(Windows ML · VitisAI EP)용 QDQ 모델로 양자화한다 (가중치·활성값 대칭, 실수 스케일).
# 보통은 tools/Make-NpuModel.ps1 로 돌린다 (python 이 PATH 에 없어서 uv 가 격리 환경을 만든다). 직접 돌리려면:
#   uv run --no-project --python 3.12 --with onnxruntime==1.23.2 --with onnx --with numpy --with opencv-python-headless \
#       tools/npu_quantize.py --model best.onnx [--out best.npu.onnx] [--calib 영상·이미지·폴더 ...] [--mode a16w8-pc]
#
# FP32 모델을 NPU 에 그대로 넣는 BF16 자동 변환은 이 PC 의 VitisAI EP(1.8.75)에서 NPU 커널 빌드가 실패한다
# (1.5시간 뒤 프로세스 종료) → 양자화 모델만 NPU 에 올린다. 방식별 실측은 experiments/NpuBench/README.md:
#   a16w8-pc   : 활성값 INT16 + 가중치 채널별 스케일 (기본값). NPU 에서 GPU 검출 대비 재현율 96.0%·정밀도 99.1%, 16.3 ms
#   a16w8      : 활성값 INT16, 가중치 텐서별. 같은 속도에 92.0%·96.5% — 채널별이 확실히 낫다 (2026-10-24, 루트 YOLO26s)
#                (16비트 Q/DQ 는 opset < 21 이라 com.microsoft 도메인으로 나가는데 VitisAI 가 받는다)
#   a8w8       : 활성값 INT8, MinMax 보정. 이 모델은 신뢰도가 평균 0.4 떨어져 GPU 검출의 14% 만 남았다 (쓰지 말 것)
#   a8w8-pc    : + 가중치 채널별 스케일 (머리의 depthwise Conv 는 채널마다 범위가 크게 다르다)
#   a8w8-pct   : + Percentile(99.999%) 보정 (SiLU 출력의 드문 큰 값 때문에 스케일이 거칠어지는 것을 막는다)
#   a8w8-pc-pct: 둘 다 (INT8 중엔 가장 낫다)
#
# 결과 모델 메타데이터에 짝 확인용 값을 넣는다 — 앱은 best.onnx 를 바꿨는데 옛 NPU 모델이 남은 경우를 이걸로 알아챈다:
#   npu_source_md5 = 원본 모델 파일의 MD5 · npu_quant = 방식 · npu_calib = 보정 데이터 요약
import argparse
import hashlib
import pathlib
import re
import sys
import tempfile

import cv2
import numpy as np
import onnx
import onnxruntime
from onnxruntime.quantization import (CalibrationDataReader, CalibrationMethod, QuantFormat, QuantType,
                                      quantize_static)
from onnxruntime.quantization.shape_inference import quant_pre_process

MODES = {
    # 이름: (활성값 형식, 가중치 채널별, 보정 방법)
    "a16w8-pc": (QuantType.QInt16, True, CalibrationMethod.MinMax),
    "a16w8": (QuantType.QInt16, False, CalibrationMethod.MinMax),
    "a8w8": (QuantType.QInt8, False, CalibrationMethod.MinMax),
    "a8w8-pc": (QuantType.QInt8, True, CalibrationMethod.MinMax),
    "a8w8-pct": (QuantType.QInt8, False, CalibrationMethod.Percentile),
    "a8w8-pc-pct": (QuantType.QInt8, True, CalibrationMethod.Percentile),
}
VIDEO_EXT = {".mp4", ".mkv", ".webm", ".avi", ".mov"}
IMAGE_EXT = {".jpg", ".jpeg", ".png", ".bmp"}
VIDEOS = ["test1.mp4", "test2.mp4", "test3.mp4", "test4.mp4", "test5.mp4"]   # 보정 기본값 (publish 안)

repo = pathlib.Path(__file__).resolve().parents[1]
# OpenCV 의 INTER_NEAREST 는 화소 중심을 맞추지 않는다. Skia(YoloDotNet)의 Nearest 와 같은 쪽은 NEAREST_EXACT
NEAREST = getattr(cv2, "INTER_NEAREST_EXACT", cv2.INTER_NEAREST)


def letterbox(img, width, height):
    """BGR 이미지 → 모델 입력 [1,3,H,W] float32(RGB, 0~1).
    앱이 실제로 넣는 입력과 같게 YoloDotNet 의 ResizeImageProportional 을 따른다: 비율 유지·가운데·Nearest·검은 여백
    (캔버스를 SKColors.Empty 로 지운다), 크기는 (int)(w·s + 0.5), 모델보다 작은 이미지는 줄이지 않고 가운데에 둔다."""
    ih, iw = img.shape[:2]
    if iw < width and ih < height:
        nw, nh = iw, ih
    else:
        s = min(width / iw, height / ih)
        nw, nh = int(iw * s + 0.5), int(ih * s + 0.5)
    canvas = np.zeros((height, width, 3), np.uint8)
    x, y = (width - nw) // 2, (height - nh) // 2
    canvas[y:y + nh, x:x + nw] = img if (nw, nh) == (iw, ih) else cv2.resize(img, (nw, nh), interpolation=NEAREST)
    return canvas[:, :, ::-1].transpose(2, 0, 1)[None].astype(np.float32) / 255.0


def video_frames(path, per_video):
    """영상에서 고르게 per_video 장. 평가 쪽(NpuBench)은 (i+0.5)/n 위치를 쓰므로 여기서는 i/n 위치 — 서로 겹치지 않는다"""
    cap = cv2.VideoCapture(str(path))
    count = int(cap.get(cv2.CAP_PROP_FRAME_COUNT))
    frames = []
    if count > 0:
        for i in range(per_video):
            cap.set(cv2.CAP_PROP_POS_FRAMES, i * count // per_video)
            ok, img = cap.read()
            if ok:
                frames.append(img)
    else:
        # 길이 정보가 없는 영상(브라우저 녹화 webm 등)은 프레임 시크가 스트림을 망가뜨린다(CLAUDE.md) → 앞에서부터 30장마다
        i = 0
        while len(frames) < per_video:
            ok, img = cap.read()
            if not ok:
                break
            if i % 30 == 0:
                frames.append(img)
            i += 1
    cap.release()
    return frames


def calibration_frames(sources, per_video, width, height):
    """영상·이미지·폴더(안의 이미지) 목록 → (입력 텐서 목록, 요약 문자열)"""
    tensors, summary = [], []
    for src in sources:
        p = pathlib.Path(src)
        if p.is_dir():
            images = sorted(f for f in p.iterdir() if f.suffix.lower() in IMAGE_EXT)
            imgs = [cv2.imread(str(f)) for f in images]
            imgs = [im for im in imgs if im is not None]
            label = f"{p.name}/ 이미지"
        elif p.suffix.lower() in VIDEO_EXT:
            imgs = video_frames(p, per_video)
            label = p.name
        elif p.suffix.lower() in IMAGE_EXT:
            im = cv2.imread(str(p))
            imgs = [] if im is None else [im]
            label = p.name
        else:
            print(f"  건너뜀 (영상·이미지가 아님): {p}")
            continue
        tensors += [letterbox(im, width, height) for im in imgs]
        summary.append(f"{label}×{len(imgs)}")
    return tensors, ", ".join(summary)


class Reader(CalibrationDataReader):
    def __init__(self, input_name, frames):
        self._name = input_name
        self._it = iter(frames)

    def get_next(self):
        x = next(self._it, None)
        return None if x is None else {self._name: x}


def input_size(model):
    """모델 입력 [1,3,H,W] 의 (W, H). 고정 크기가 아니면 Ultralytics 메타데이터 imgsz, 그것도 없으면 640"""
    dims = model.graph.input[0].type.tensor_type.shape.dim
    if len(dims) == 4 and dims[2].dim_value > 0 and dims[3].dim_value > 0:
        return dims[3].dim_value, dims[2].dim_value
    meta = {p.key: p.value for p in model.metadata_props}
    m = re.findall(r"\d+", meta.get("imgsz", ""))
    return (int(m[1]), int(m[0])) if len(m) >= 2 else (640, 640)


def decode_tail(model):
    """양자화하지 않고 실수로 둘 노드 = 검출 머리의 마지막 Conv 뒤 디코딩(앵커 더하기·stride 곱·Concat·Sigmoid·TopK 등).
    박스 좌표(0~640)와 클래스 확률(0~1)이 한 텐서에 섞이는 지점이라 INT8 스케일 하나로는 확률이 뭉개진다.
    머리 안의 Sigmoid·Mul 대부분은 Conv 뒤 SiLU 라서 같이 빼면 NPU 구간이 잘게 끊긴다 → Conv 출력을 받는
    Conv·Sigmoid·Mul 이외의 노드(= 마지막 Conv 의 Reshape/Concat 등)에서 시작해 그 아래를 전부 뺀다.
    머리는 Ultralytics 노드 이름 /model.N/ 중 N 이 가장 큰 모듈 (YOLO26·YOLO11 은 23, YOLOv8 은 22)"""
    nodes = list(model.graph.node)
    indices = [int(m.group(1)) for n in nodes if (m := re.match(r"/model\.(\d+)/", n.name))]
    if not indices:
        sys.exit("Ultralytics 로 내보낸 모델이 아닙니다 (노드 이름에 /model.N/ 이 없음)")
    head = f"/model.{max(indices)}/"

    consumers = {}
    for n in nodes:
        for t in n.input:
            consumers.setdefault(t, []).append(n)
    conv_out = {t for n in nodes if n.op_type == "Conv" for t in n.output}
    roots = [n for n in nodes if n.name.startswith(head) and n.op_type not in ("Conv", "Sigmoid", "Mul")
             and any(t in conv_out for t in n.input)]
    excluded, stack = {}, list(roots)
    while stack:
        n = stack.pop()
        if n.name in excluded:
            continue
        excluded[n.name] = n.op_type
        for t in n.output:
            stack.extend(consumers.get(t, []))
    if not excluded:
        sys.exit(f"검출 머리({head})에서 디코딩 시작점을 찾지 못했습니다 — 지원하지 않는 구조")
    types = {}
    for t in excluded.values():
        types[t] = types.get(t, 0) + 1
    print(f"노드 {len(nodes)}개, 머리 {head} — 실수로 둘 디코딩 {len(excluded)}개 (시작점 {len(roots)}개)")
    print("  " + ", ".join(f"{k}×{v}" for k, v in sorted(types.items(), key=lambda kv: -kv[1])))
    return list(excluded)


def main():
    ap = argparse.ArgumentParser(description="YOLO ONNX → AMD NPU 용 QDQ 모델")
    ap.add_argument("--model", required=True, help="원본 FP32 ONNX (Ultralytics 내보내기)")
    ap.add_argument("--out", help="결과 경로 (기본: 같은 폴더의 <이름>.npu.onnx)")
    ap.add_argument("--calib", nargs="*", help="보정용 영상·이미지·이미지 폴더 (기본: publish/test1~5.mp4)")
    ap.add_argument("--per-video", type=int, default=20, help="영상마다 뽑을 장 수 (기본 20)")
    ap.add_argument("--mode", choices=MODES, default="a16w8-pc")
    a = ap.parse_args()

    src = pathlib.Path(a.model).resolve()
    dst = pathlib.Path(a.out).resolve() if a.out else src.with_name(src.stem + ".npu.onnx")
    if src == dst:
        sys.exit("결과 경로가 원본과 같습니다")
    calib = a.calib or [str(repo / "publish" / v) for v in VIDEOS if (repo / "publish" / v).exists()]
    if not calib:
        sys.exit("보정 데이터가 없습니다 — --calib 로 영상이나 이미지 폴더를 주세요 (publish/test1~5.mp4 도 없음)")
    act, per_channel, method = MODES[a.mode]

    source_md5 = hashlib.md5(src.read_bytes()).hexdigest().upper()
    print(f"원본: {src} (MD5 {source_md5})")
    with tempfile.TemporaryDirectory(prefix="npu_quant_") as tmp:
        pre = pathlib.Path(tmp) / "pre.onnx"
        # 기호 형상 추론은 end-to-end 꼬리의 TopK 에서 죽는다 (입력 형상을 못 구함). 입력이 고정이라 ONNX 기본 추론으로 충분하다
        quant_pre_process(str(src), str(pre), skip_symbolic_shape=True)
        model = onnx.load(str(pre))
        width, height = input_size(model)
        exclude = decode_tail(model)

        frames, calib_summary = calibration_frames(calib, a.per_video, width, height)
        if not frames:
            sys.exit("보정 프레임을 하나도 못 읽었습니다")
        print(f"보정: {len(frames)}장 ({calib_summary}) · 입력 {width}×{height} · 방식 {a.mode}")

        quantize_static(
            str(pre), str(dst), Reader(model.graph.input[0].name, frames),
            quant_format=QuantFormat.QDQ,
            activation_type=act,
            weight_type=QuantType.QInt8,
            per_channel=per_channel,
            calibrate_method=method,
            nodes_to_exclude=exclude,
            extra_options={"ActivationSymmetric": True, "WeightSymmetric": True, "CalibPercentile": 99.999,
                           "UseQDQContribOps": act == QuantType.QInt16},
        )

    # 짝 확인용 메타데이터 (원본의 Ultralytics 메타데이터 description·names·task 등은 양자화기가 그대로 옮긴다)
    out = onnx.load(str(dst))
    tags = {"npu_source_md5": source_md5, "npu_quant": a.mode,
            "npu_calib": f"{len(frames)}장: {calib_summary}", "npu_quantizer": f"onnxruntime {onnxruntime.__version__}"}
    keep = [p for p in out.metadata_props if p.key not in tags]
    del out.metadata_props[:]
    out.metadata_props.extend(keep)
    for k, v in tags.items():
        out.metadata_props.add(key=k, value=v)
    onnx.save(out, str(dst))
    print(f"저장: {dst} ({dst.stat().st_size / 1e6:.1f} MB)")


if __name__ == "__main__":
    main()
