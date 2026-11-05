"""3단계 준비: RF-DETR ONNX 앞에 전처리를 붙여 GPU(DirectML)에서 돌게 한다 — 앱은 디코딩한 프레임을 그대로 넘기면 된다 (README '3단계 준비').

    uv run --no-project --python 3.12 --with onnx --with onnxruntime --with opencv-python --with numpy experiments/RfDetrBench/prep_graph.py

입력은 앱이 받는 프레임 그대로 — uint8 [1, H, W, C] BGR(C = 3) 또는 BGRA(C = 4, HUD 캡처). H·W·C 는 정하지 않는다(앱은 영상 크기를 알 때 세션을 만들며
ORT 의 free dimension override 로 고정한다 — DirectML 은 모양이 정해져야 그래프를 하나로 묶는다). 채널 2,1,0 을 고르므로 BGRA 의 A 는 버려진다.
노드: Cast(float) → Gather(채널 2,1,0 = RGB) → Transpose(NCHW) → Resize(linear · half_pixel · res×res) → Mul(1/(255·std)) → Add(−mean/std).
공식 전처리(0~1 float 로 바꾼 뒤 정사각형으로 쌍선형, antialias 없음 — README '전처리·후처리')와 수학적으로 같다.
Resize 는 선형이라 1/255 를 앞에 곱하든 뒤에 곱하든 같고 반올림만 다르다. 모델 opset 17 의 Resize 엔 antialias 가 없다.
산출물: 원본 옆 <이름>-gpuprep.onnx. FP32 모델은 파이썬 ONNX Runtime(CPU)으로 공식 전처리 + 원본 모델과 출력이 같은지 확인한다.
"""
from __future__ import annotations

import sys
from pathlib import Path

import numpy as np
import onnx
from onnx import TensorProto, helper, numpy_helper

HERE = Path(__file__).resolve().parent
OUT = HERE / "out"
MEAN = np.array([0.485, 0.456, 0.406], np.float32)
STD = np.array([0.229, 0.224, 0.225], np.float32)
# 2단계에서 학습한 S(FP32·FP16)와 1단계의 COCO N(FP32·FP16 — N 은 아직 학습하지 않았다)
MODELS = ["step2_models/rfdetr_s.onnx", "step2_models/rfdetr_s-fp16.onnx", "models/rfdetr-nano.onnx", "models/rfdetr-nano-fp16.onnx"]
INPUT = "image_bgr_u8"


def prep_nodes(res: int, out_name: str, p: str = "prep_") -> tuple[list, list]:
    nodes = [
        helper.make_node("Cast", [INPUT], [p + "f"], to=TensorProto.FLOAT, name=p + "cast"),
        helper.make_node("Gather", [p + "f", p + "rgb_idx"], [p + "rgb"], axis=3, name=p + "rgb"),
        helper.make_node("Transpose", [p + "rgb"], [p + "nchw"], perm=[0, 3, 1, 2], name=p + "nchw"),
        helper.make_node("Resize", [p + "nchw", "", "", p + "sizes"], [p + "resized"], mode="linear",
                         coordinate_transformation_mode="half_pixel", name=p + "resize"),
        helper.make_node("Mul", [p + "resized", p + "scale"], [p + "scaled"], name=p + "scale"),
        helper.make_node("Add", [p + "scaled", p + "bias"], [out_name], name=p + "bias"),
    ]
    inits = [
        numpy_helper.from_array(np.array([2, 1, 0], np.int64), p + "rgb_idx"),
        numpy_helper.from_array(np.array([1, 3, res, res], np.int64), p + "sizes"),
        numpy_helper.from_array((1.0 / (255.0 * STD)).reshape(1, 3, 1, 1).astype(np.float32), p + "scale"),
        numpy_helper.from_array((-MEAN / STD).reshape(1, 3, 1, 1).astype(np.float32), p + "bias"),
    ]
    return nodes, inits


def add_prep(src: Path, dst: Path) -> tuple[int, str]:
    m = onnx.load(str(src))
    g = m.graph
    opset = next(o.version for o in m.opset_import if o.domain in ("", "ai.onnx"))
    if opset < 13:
        raise SystemExit(f"{src.name}: opset {opset} — Resize 의 roi 를 비울 수 있는 13 이상을 가정했다")
    old = g.input[0]
    dims = old.type.tensor_type.shape.dim
    res = dims[2].dim_value
    if old.type.tensor_type.elem_type != TensorProto.FLOAT or dims[3].dim_value != res:
        raise SystemExit(f"{src.name}: 입력이 float [1,3,{res},{res}] 가 아니다")
    nodes, inits = prep_nodes(res, old.name)
    g.input.remove(old)
    g.input.insert(0, helper.make_tensor_value_info(INPUT, TensorProto.UINT8, [1, "H", "W", "C"]))
    for n in reversed(nodes):
        g.node.insert(0, n)
    g.initializer.extend(inits)
    onnx.checker.check_model(m)
    onnx.save(m, str(dst))
    return res, old.name


def check(src: Path, dst: Path, res: int, in_name: str) -> None:
    """FP32 만: ① 붙인 전처리만 떼어 공식 전처리와 값 비교 ② 붙인 모델 vs 공식 전처리 + 원본 모델 출력 비교 (ORT CPU)"""
    import cv2
    import onnxruntime as ort
    so = ort.SessionOptions()
    so.log_severity_level = 3
    nodes, inits = prep_nodes(res, "x")
    only = helper.make_model(helper.make_graph(nodes, "prep", [helper.make_tensor_value_info(INPUT, TensorProto.UINT8, [1, "H", "W", "C"])],
                                               [helper.make_tensor_value_info("x", TensorProto.FLOAT, [1, 3, res, res])], inits),
                             opset_imports=[helper.make_opsetid("", 17)], ir_version=8)   # onnx 1.23 기본(IR 14)은 ORT 가 못 읽는다
    s_prep = ort.InferenceSession(only.SerializeToString(), so, providers=["CPUExecutionProvider"])
    s_ref = ort.InferenceSession(str(src), so, providers=["CPUExecutionProvider"])
    s_new = ort.InferenceSession(str(dst), so, providers=["CPUExecutionProvider"])
    out_names = [o.name for o in s_ref.get_outputs()]
    worst_in, worst_score, n_ref, n_hit = 0.0, 0.0, 0, 0
    for name in ("vid_test4_03s", "vid_test4_26s", "vid_test5_10s", "ds_00", "ds_01"):
        img = cv2.imread(str(OUT / "samples" / f"{name}.png"), cv2.IMREAD_COLOR)
        rgb = img[:, :, ::-1].astype(np.float32) / 255.0
        x = ((cv2.resize(rgb, (res, res), interpolation=cv2.INTER_LINEAR) - MEAN) / STD).transpose(2, 0, 1)[None].astype(np.float32)
        u8 = np.ascontiguousarray(img[None])
        worst_in = max(worst_in, float(np.abs(s_prep.run(None, {INPUT: u8})[0] - x).max()))
        # 원출력은 쿼리 번호별로 견주지 않는다 — 입력이 조금만 달라도 인코더의 상위 300 제안 순서가 바뀐다 (README ③). 검출로 견준다
        ref = decode(dict(zip(out_names, s_ref.run(None, {in_name: x}))))
        new = decode(dict(zip(out_names, s_new.run(None, {INPUT: u8}))))
        hit, diff = match(ref, new)
        n_ref, n_hit, worst_score = n_ref + len(ref), n_hit + hit, max(worst_score, diff)
    print(f"    CPU 확인 (5장): 붙인 전처리 vs 공식 전처리 값 차 최대 {worst_in:.2e} · "
          f"검출(임계 {THRESHOLD}) 짝 {n_hit}/{n_ref} · 신뢰도 차 최대 {worst_score:.4f}")


THRESHOLD = 0.3


def decode(outs: dict) -> list:
    d = outs[next(k for k in outs if "dets" in k)][0]
    s = 1.0 / (1.0 + np.exp(-np.clip(outs[next(k for k in outs if "labels" in k)][0], -88, 88)))
    q, c = np.nonzero(s > THRESHOLD)
    return sorted(((int(ci), float(s[qi, ci]), d[qi]) for qi, ci in zip(q, c)), key=lambda t: -t[1])


def match(ref: list, new: list) -> tuple[int, float]:
    """같은 클래스·IoU 0.9 이상을 신뢰도 높은 기준부터 짝짓는다 (Program.cs 의 Agree 와 같게)"""
    def iou(a, b):
        ax1, ay1, ax2, ay2 = a[0] - a[2] / 2, a[1] - a[3] / 2, a[0] + a[2] / 2, a[1] + a[3] / 2
        bx1, by1, bx2, by2 = b[0] - b[2] / 2, b[1] - b[3] / 2, b[0] + b[2] / 2, b[1] + b[3] / 2
        inter = max(0.0, min(ax2, bx2) - max(ax1, bx1)) * max(0.0, min(ay2, by2) - max(ay1, by1))
        union = a[2] * a[3] + b[2] * b[3] - inter
        return inter / union if union > 0 else 0.0
    used, hit, worst = [False] * len(new), 0, 0.0
    for c, s, box in ref:
        best, bi = 0.9, -1
        for j, (c2, _, box2) in enumerate(new):
            if not used[j] and c2 == c and (v := iou(box, box2)) >= best:
                best, bi = v, j
        if bi >= 0:
            used[bi], hit, worst = True, hit + 1, max(worst, abs(s - new[bi][1]))
    return hit, worst


def main() -> int:
    for rel in MODELS:
        src = OUT / rel
        if not src.exists():
            print(f"  {rel} 없음 — 건너뜀")
            continue
        dst = src.with_name(src.stem + "-gpuprep.onnx")
        res, in_name = add_prep(src, dst)
        print(f"  {dst.relative_to(OUT)} ({dst.stat().st_size / 1e6:.1f} MB) — 입력 {INPUT} uint8 [1,H,W,C] → {res}×{res}")
        if "fp16" not in src.name:
            check(src, dst, res, in_name)
    return 0


if __name__ == "__main__":
    sys.exit(main())
