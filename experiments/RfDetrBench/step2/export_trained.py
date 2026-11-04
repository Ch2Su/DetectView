"""2단계: WSL 에서 학습한 모델을 앱이 쓸 ONNX 로 내보낸다 (Windows 에서 uv 로, CPU — README '2단계').

    # RF-DETR (rfdetr 1.11.2) — FP32 와 FP16 둘 다
    uv run --no-project --python 3.12 --with "rfdetr[onnx]==1.11.2" --with onnxruntime `
        experiments/RfDetrBench/step2/export_trained.py rfdetr <checkpoint_best_total.pth> rfdetr_s
    # YOLO (학습한 Ultralytics 8.4.142 — 그 판은 nms=False 를 줘야 end-to-end [1,300,6])
    uv run --no-project --python 3.12 --with "ultralytics==8.4.142" --with onnx --with onnxslim --with onnxruntime `
        experiments/RfDetrBench/step2/export_trained.py yolo <best.pt> y26s

체크포인트는 out/step2_models/src 로 복사해서 쓴다(WSL 경로를 직접 열지 않게, Ultralytics 는 .pt 옆에 ONNX 를 쓴다).
산출물: out/step2_models/<이름>.onnx (RF-DETR 는 <이름>-fp16.onnx 도)
"""
from __future__ import annotations

import shutil
import sys
import time
from pathlib import Path

import onnx

HERE = Path(__file__).resolve().parent
OUT = HERE.parent / "out" / "step2_models"


def shape(path: Path) -> list[list[int]]:
    return [[d.dim_value for d in o.type.tensor_type.shape.dim] for o in onnx.load(str(path)).graph.output]


def to_fp16(src: Path, dst: Path) -> None:
    # variants.py 의 to_fp16 과 같다 — onnxconverter-common 은 RF-DETR 에서 ORT 가 못 여는 그래프를 냈다
    from onnxruntime.transformers.onnx_model import OnnxModel
    om = OnnxModel(onnx.load(str(src)))
    om.convert_float_to_float16(keep_io_types=True, use_symbolic_shape_infer=True)
    om.save_model_to_file(str(dst))


def export_rfdetr(src: Path, name: str) -> None:
    from rfdetr.detr import RFDETR
    model = RFDETR.from_checkpoint(str(src), trust_checkpoint=True)   # 우리가 학습한 체크포인트 — 클래스·크기를 거기서 읽는다
    tmp = OUT / f"export_{name}"
    shutil.rmtree(tmp, ignore_errors=True)
    model.export(output_dir=str(tmp), verbose=False)
    found = sorted(p for p in tmp.rglob("*.onnx") if "backbone" not in p.name)
    dst = OUT / f"{name}.onnx"
    shutil.move(str(found[0]), dst)
    shutil.rmtree(tmp, ignore_errors=True)
    print(f"  {dst.name} ({dst.stat().st_size / 1e6:.1f} MB) 출력 {shape(dst)} · 클래스 {getattr(model, 'class_names', '?')}")
    fp16 = OUT / f"{name}-fp16.onnx"
    to_fp16(dst, fp16)
    print(f"  {fp16.name} ({fp16.stat().st_size / 1e6:.1f} MB)")


def export_yolo(src: Path, name: str) -> None:
    from ultralytics import YOLO
    path = Path(YOLO(str(src)).export(format="onnx", imgsz=640, nms=False))
    dst = OUT / f"{name}.onnx"
    shutil.move(str(path), dst)
    s = shape(dst)
    print(f"  {dst.name} ({dst.stat().st_size / 1e6:.1f} MB) 출력 {s}")
    if s[0] != [1, 300, 6]:
        raise SystemExit(f"{dst.name} 이 end-to-end([1,300,6])가 아니다 — 앱과 다른 꼴")


def main() -> int:
    if len(sys.argv) != 4 or sys.argv[1] not in ("rfdetr", "yolo"):
        print(__doc__)
        return 2
    kind, src, name = sys.argv[1], Path(sys.argv[2]), sys.argv[3]
    # 파일 이름은 그대로 둔다 — rfdetr 는 체크포인트에 model_name 이 없으면(배포용 시작 가중치) 파일 이름(rf-detr-small.pth)으로 크기를 안다
    (OUT / "src" / name).mkdir(parents=True, exist_ok=True)
    local = OUT / "src" / name / src.name
    shutil.copy2(src, local)
    t0 = time.time()
    (export_rfdetr if kind == "rfdetr" else export_yolo)(local, name)
    print(f"  {time.time() - t0:.0f}초")
    return 0


if __name__ == "__main__":
    sys.exit(main())
