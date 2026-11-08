# Ultralytics YOLO ONNX(FP32)를 NVIDIA TensorRT for RTX 용 FP16 짝 모델(<이름>.fp16.onnx)로 바꾼다 — 입출력은 FP32 그대로.
# 보통은 tools/Make-Fp16Model.ps1 로 돌린다 (python 이 PATH 에 없어서 uv 가 격리 환경을 만든다). 직접 돌리려면:
#   uv run --no-project --python 3.12 --with onnxruntime==1.31.1 --with onnx --with sympy --with numpy \
#       tools/fp16_convert.py --model best.onnx [--out best.fp16.onnx]
#
# TensorRT for RTX 는 모델에 적힌 정밀도대로 돈다(FP32 모델을 알아서 FP16 으로 낮추지 않는다) → FP16 으로 바꾼 짝을 따로 둔다.
# 2026-11-08 실측(experiments/NpuBench README 결과 6): 같은 YOLO26m 640×384 가 DirectML FP32 10.0 ms · TensorRT FP32 6.6 ms · TensorRT FP16 2.8 ms,
# FP16 의 검출은 DirectML 대비 재현율 99.6% · 정밀도 99.9%(평균 IoU 0.989).
# 바꾸는 방법은 그때 잰 판과 같다 — onnxruntime.transformers 의 convert_float_to_float16(keep_io_types=True, use_symbolic_shape_infer=True).
# YOLO26 에선 기호 꼴 추론이 실패했다는 안내가 찍히지만 바꾸기는 그대로 끝난다(잰 판도 그랬다).
# onnxconverter-common 은 YOLO26 의 Resize 출력 Cast 에서 ORT 가 못 여는 그래프를 냈다(experiments/RfDetrBench README '1단계').
# 이 FP16 판은 DirectML 이 그래프 융합에서 못 연다 — TensorRT 전용이다.
#
# 메타데이터는 원본 것(이름·클래스·입력 크기·학습일 — 앱 제목과 YoloDotNet 이 읽는다)을 그대로 두고 짝 확인용을 더한다
# (앱은 best.onnx 를 바꿨는데 옛 FP16 짝이 남은 경우를 이걸로 알아챈다 — MainWindow.Trt.cs 의 CheckTrtModel):
#   fp16_source_md5 = 원본 모델 파일의 MD5(대문자 16진수) · fp16_converter = 바꾼 도구와 판
# 끝에 CPU 로 원본과 FP16 판을 같은 입력으로 한 번씩 돌려 꼴과 차이를 보인다 (돌아가는지만 — 검출 일치도는 NpuBench --trt-fp16)
import argparse
import hashlib
import pathlib
import sys

import numpy as np
import onnx
import onnxruntime
from onnxruntime.transformers.onnx_model import OnnxModel


def md5(path: pathlib.Path) -> str:
    h = hashlib.md5()
    with path.open("rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest().upper()   # 앱(Convert.ToHexString)·tools/npu_quantize.py 와 같은 형식


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--model", required=True, help="원본 FP32 YOLO ONNX (예: best.onnx)")
    ap.add_argument("--out", help="기본: 같은 폴더의 <이름>.fp16.onnx")
    a = ap.parse_args()
    sys.stdout.reconfigure(errors="replace")

    src = pathlib.Path(a.model).resolve()
    out = pathlib.Path(a.out).resolve() if a.out else src.with_suffix(".fp16.onnx")
    model = onnx.load(str(src))
    meta = {p.key: p.value for p in model.metadata_props}
    # 짝 모델을 다시 바꾸지 않게 — 원본은 FP32 여야 한다
    if "fp16_source_md5" in meta or "npu_quant" in meta:
        print(f"{src.name} 는 원본이 아니다(이미 FP16 짝이거나 NPU 짝) — 원본 FP32 모델을 주세요")
        return 2
    elem = lambda v: v.type.tensor_type.elem_type   # noqa: E731
    if any(elem(v) != onnx.TensorProto.FLOAT for v in [*model.graph.input, *model.graph.output]):
        print(f"{src.name} 의 입출력이 FP32 가 아니다 — 앱(YoloDotNet)이 FP32 입출력을 넣고 읽는다")
        return 2

    src_md5 = md5(src)
    om = OnnxModel(model)
    om.convert_float_to_float16(keep_io_types=True, use_symbolic_shape_infer=True)
    fp16 = om.model
    # 원본 메타데이터를 그대로 두고(바꾸기가 건드리지 않지만 확인 겸 다시 쓴다) 짝 확인용을 더한다
    del fp16.metadata_props[:]
    for k, v in {**meta, "fp16_source_md5": src_md5,
                 "fp16_converter": f"onnxruntime.transformers {onnxruntime.__version__} convert_float_to_float16(keep_io_types)"}.items():
        fp16.metadata_props.add(key=k, value=v)
    onnx.save(fp16, str(out))
    print(f"저장: {out} ({out.stat().st_size / 1e6:.1f} MB) · 원본 MD5 {src_md5} · 짝 MD5 {md5(out)}")

    # CPU 로 한 번씩 — 꼴이 같고 값이 FP16 반올림 정도로만 다른지
    so = onnxruntime.SessionOptions()
    so.log_severity_level = 3
    s32 = onnxruntime.InferenceSession(str(src), so, providers=["CPUExecutionProvider"])
    s16 = onnxruntime.InferenceSession(str(out), so, providers=["CPUExecutionProvider"])
    inp = s32.get_inputs()[0]
    x = np.random.default_rng(0).random([d if isinstance(d, int) else 1 for d in inp.shape], dtype=np.float32)
    y32, y16 = s32.run(None, {inp.name: x})[0], s16.run(None, {inp.name: x})[0]
    # end-to-end [1,300,6] 은 신뢰도 순으로 줄을 세워 값이 조금만 달라도 줄 순서가 바뀐다 — 줄끼리 빼지 않고 신뢰도(5번째 열)를 정렬해 견준다
    conf = lambda y: np.sort(y.reshape(-1, y.shape[-1])[:, 4])[::-1] if y.shape[-1] == 6 else y.ravel()   # noqa: E731
    print(f"CPU 확인: 입력 {list(x.shape)} · 출력 {list(y32.shape)} / {list(y16.shape)} · 정렬한 신뢰도 최대 차 {float(np.abs(conf(y32) - conf(y16)).max()):.4f}"
          f" (임의 입력 — 검출 일치도는 NpuBench --trt-fp16)")
    return 0 if y32.shape == y16.shape else 1


if __name__ == "__main__":
    sys.exit(main())
