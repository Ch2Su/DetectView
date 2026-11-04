"""1단계 추가 비교용 모델을 만든다 — export_rfdetr.py 뒤에 돌린다 (README '추가 비교').

  - RF-DETR FP16: export_rfdetr.py 가 만든 FP32 를 FP16 으로 바꾼다 (입출력은 FP32 그대로라 C# 쪽이 같다 — to_fp16)
  - YOLO26 COCO 사전학습 n·s·m (FP32): Ultralytics 로 내보낸다 — COCO 정확도가 비슷한 RF-DETR 와 같은 GPU 에서 맞대려고
    (Roboflow 표: RF-DETR-N 48.4 · RF-DETR-S 53.0 ≈ YOLO26m 53.1)
  - 앱의 YOLO26s(루트 best.onnx) FP16: FP16 이 YOLO 에도 같은 만큼 듣는지

실행 (저장소 루트에서):
    uv run --no-project --python 3.12 --with "ultralytics==8.4.31" --with onnx --with onnxslim --with onnxruntime ^
        experiments/RfDetrBench/variants.py --yolo-weights <yolo26n.pt·yolo26s.pt·yolo26m.pt 가 있는 폴더>

Ultralytics 는 앱의 best.onnx 를 내보낸 8.4.31 로 고정한다 — 그 판은 기본값으로 end-to-end([1,300,6], 메타데이터 end2end True)를 내보낸다.
8.4.142 는 nms 기본값이 None 으로 바뀌어 같은 호출이 NMS 전 원출력([1,84,8400], end2end False)을 냈다 — 그 판에선 nms=False(또는 end2end=True)를
주면 [1,300,6] 이 나온다 (2026-11-04 실측).
앱 YOLO26s 의 FP16 은 만들어지지만 DirectML 이 세션을 못 만든다(그래프 융합에서 E_INVALIDARG) — 벤치에서 '실패'로 찍힌다 (README).
onnxruntime 은 FP16 변환기(onnxruntime.transformers)로 쓰고, 없으면 Ultralytics 가 내보내는 도중 pip 로 깔려고 한다(AutoUpdate).

--yolo-weights 의 .pt 는 out/ 으로 복사해서 내보낸다 (Ultralytics 는 .pt 옆에 ONNX 를 쓴다 — 원본 폴더를 건드리지 않게).
만드는 것: out/models/*.onnx, out/ref/variants.json (C# 벤치가 읽어 비교 대상에 더한다)
"""
from __future__ import annotations

import argparse
import json
import shutil
import sys
import time
from pathlib import Path

import onnx

HERE = Path(__file__).resolve().parent
OUT = HERE / "out"
REPO = HERE.parent.parent


def to_fp16(src: Path, dst: Path) -> None:
    """FP16 변환 — onnxruntime.transformers 의 변환기를 기호 모양 추론과 함께 쓴다.
    onnxconverter-common 의 convert_float_to_float16(keep_io_types=True)는 텐서 형식 정보(value_info)가 빈 곳에 Cast 를 잘못 넣어
    RF-DETR(패치 임베딩 Conv 에 float·float16 이 섞임)·YOLO26(Resize 출력 Cast) 모두 ORT 가 열지 못하는 그래프를 냈다 (2026-11-04)"""
    from onnxruntime.transformers.onnx_model import OnnxModel
    t0 = time.time()
    om = OnnxModel(onnx.load(str(src)))
    om.convert_float_to_float16(keep_io_types=True, use_symbolic_shape_infer=True)
    om.save_model_to_file(str(dst))
    print(f"  FP16 {dst.name} ({dst.stat().st_size / 1e6:.1f} MB, {time.time() - t0:.0f}초)")


def export_yolo(pt: Path, name: str) -> Path:
    from ultralytics import YOLO
    tmp = OUT / f"export_{name}"
    shutil.rmtree(tmp, ignore_errors=True)
    tmp.mkdir(parents=True)
    shutil.copy2(pt, tmp / pt.name)   # 원본 .pt 는 읽기만 한다
    t0 = time.time()
    path = Path(YOLO(str(tmp / pt.name)).export(format="onnx", imgsz=640))
    dst = OUT / "models" / f"{name}.onnx"
    shutil.move(str(path), dst)
    shutil.rmtree(tmp, ignore_errors=True)
    out_shape = [d.dim_value for d in onnx.load(str(dst)).graph.output[0].type.tensor_type.shape.dim]
    print(f"  내보냄 {dst.name} ({dst.stat().st_size / 1e6:.1f} MB, 출력 {out_shape}, {time.time() - t0:.0f}초)")
    if out_shape != [1, 300, 6]:
        # 앱 모델과 다른 꼴이면 시간 비교가 어긋난다 (머리 부분의 TopK 가 없거나 출력이 크다)
        raise RuntimeError(f"{dst.name} 이 end-to-end([1,300,6])가 아니다: {out_shape} — Ultralytics 판을 확인할 것")
    return dst


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--yolo-weights", type=Path, help="yolo26n.pt·yolo26s.pt·yolo26m.pt 가 있는 폴더 (읽기만 한다)")
    args = ap.parse_args()

    manifest = json.loads((OUT / "ref" / "manifest.json").read_text(encoding="utf-8"))
    models_dir = OUT / "models"
    variants = []

    print("[RF-DETR FP16]")
    for size, entry in manifest["models"].items():
        src = OUT / entry["file"]
        dst = models_dir / f"rfdetr-{size}-fp16.onnx"
        to_fp16(src, dst)
        variants.append({"name": f"RF-DETR {size} FP16", "file": f"models/{dst.name}", "kind": "rfdetr",
                         "resolution": entry["resolution"], "ref": size})

    print("[YOLO26s 앱 모델 FP16]")
    app = REPO / "best.onnx"
    dst = models_dir / "yolo26s-app-fp16.onnx"
    to_fp16(app, dst)
    variants.append({"name": "YOLO26s 앱 FP16", "file": f"models/{dst.name}", "kind": "yolo", "resolution": 640})

    if args.yolo_weights:
        print("[YOLO26 COCO 사전학습]")
        for name in ["yolo26n", "yolo26s", "yolo26m"]:
            pt = args.yolo_weights / f"{name}.pt"
            if not pt.exists():
                print(f"  {pt} 없음 — 건너뜀")
                continue
            path = export_yolo(pt, f"{name}-coco")
            variants.append({"name": f"YOLO26{name[-1]} COCO", "file": f"models/{path.name}", "kind": "yolo", "resolution": 640})

    (OUT / "ref" / "variants.json").write_text(json.dumps({"models": variants}, ensure_ascii=False, indent=1), encoding="utf-8")
    print(f"\n추가 비교 {len(variants)}개 → {OUT / 'ref' / 'variants.json'}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
