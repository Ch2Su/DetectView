"""RF-DETR(COCO 사전학습) Nano·Small·Medium 을 ONNX 로 내보내고, C# 벤치(Program.cs)가 맞춰 볼 기준값을 만든다.

실행 (저장소 루트에서. 처음엔 PyTorch(CPU)·rfdetr 를 uv 캐시에, 사전학습 가중치를 %USERPROFILE%\\.roboflow\\models 에 받는다 —
가중치는 학습 체크포인트 그대로라 크기당 350~390 MB, 셋이 1.1 GB):
    uv run --no-project --python 3.12 --with "rfdetr[onnx]==1.11.2" --with opencv-python ^
        experiments/RfDetrBench/export_rfdetr.py --dataset <데이터셋 폴더>

--dataset 은 YOLO 형식 데이터셋(images/test 가 있는 폴더)이다. 2026-11-04 엔 라벨링 도구의 'WSL 학습' 버튼이
덮어쓸 수 있는 ~/datasets/visible 대신 그 백업(\\\\wsl.localhost\\Ubuntu\\home\\chisu\\datasets_backup\\visible_2026-11-04)을 줬다.
주지 않으면 시험 영상 프레임만 쓴다.

만드는 것 (experiments/RfDetrBench/out/ — gitignore):
    models/rfdetr-<size>.onnx     입력 input [1,3,R,R] · 출력 dets [1,Q,4](정규화 cxcywh) · labels [1,Q,C](로짓)
    samples/*.png                 기준 이미지 — 데이터셋 시험 분할 몇 장 + 시험 영상 프레임. 무손실 PNG 로 바꿔 두어
                                  JPEG 디코더 차이 없이 C# 과 같은 화소를 읽는다
    ref/<size>/<이름>.in.f32      공식 전처리 텐서 (float32 NCHW, 헤더 없음)
    ref/<size>/<이름>.dets.f32    ONNX Runtime(CPU) 출력 (float32, 헤더 없음 — 모양은 manifest)
    ref/<size>/<이름>.labels.f32
    ref/manifest.json             모델별 해상도·출력 모양·연산 개수, 이미지 목록, PyTorch predict() 검출(임계 0.3), 클래스 이름,
                                  OpenCV 리사이즈가 공식 전처리와 얼마나 같은지
"""
from __future__ import annotations

import argparse
import inspect
import json
import shutil
import sys
import time
from collections import Counter
from pathlib import Path

import cv2
import numpy as np
import onnx
import onnxruntime as ort
import torch
import torchvision.transforms.functional as TF
from PIL import Image

HERE = Path(__file__).resolve().parent
OUT = HERE / "out"
SIZES = ["nano", "small", "medium"]   # Apache 2.0 인 것 중 앱에 넣을 만한 크기 (Large 704·XL·2XL 은 뺐다 — XL·2XL 은 PML 1.0)
THRESHOLD = 0.3                        # 앱의 ConfidenceThreshold
MEAN = [0.485, 0.456, 0.406]
STD = [0.229, 0.224, 0.225]
# (영상, 초) — test4 3초: 차로에 멈춘 덤프트럭, 26초: 신호대기 행렬(멀리 있는 작은 차 수십 대), test5 10초: 갓길 화물차
VIDEO_FRAMES = [("test4", 3.0), ("test4", 26.0), ("test5", 10.0), ("test1", 5.0), ("test2", 5.0), ("test3", 5.0)]
DATASET_SAMPLES = 12


def find_video_dir() -> Path | None:
    """시험 영상(publish/test1.mp4)이 있는 가장 가까운 상위 폴더 — worktree 안에서 돌려도 본 checkout 의 publish 를 찾는다."""
    for d in [HERE, *HERE.parents]:
        if (d / "publish" / "test1.mp4").exists():
            return d / "publish"
    return None


def collect_samples(dataset: Path | None) -> list[dict]:
    samples_dir = OUT / "samples"
    shutil.rmtree(samples_dir, ignore_errors=True)
    samples_dir.mkdir(parents=True)
    samples = []

    video_dir = find_video_dir()
    if video_dir is None:
        print("  시험 영상(publish/test1.mp4)을 못 찾음 — 영상 프레임 없이 진행")
    else:
        for video, sec in VIDEO_FRAMES:
            path = video_dir / f"{video}.mp4"
            if not path.exists():
                continue
            cap = cv2.VideoCapture(str(path))
            cap.set(cv2.CAP_PROP_POS_MSEC, sec * 1000)
            ok, frame = cap.read()
            cap.release()
            if not ok:
                print(f"  {video} {sec}초 프레임을 못 읽음 — 건너뜀")
                continue
            name = f"vid_{video}_{int(sec):02d}s"
            cv2.imwrite(str(samples_dir / f"{name}.png"), frame)
            samples.append({"name": name, "source": f"{path.name} @ {sec}s"})

    if dataset is not None:
        test_dir = dataset / "images" / "test"
        files = sorted(p for p in test_dir.iterdir() if p.suffix.lower() in (".jpg", ".jpeg", ".png"))
        step = max(1, len(files) // DATASET_SAMPLES)
        for i, p in enumerate(files[::step][:DATASET_SAMPLES]):
            name = f"ds_{i:02d}"
            with Image.open(p) as im:   # 백업은 읽기만 한다
                im.convert("RGB").save(samples_dir / f"{name}.png")
            samples.append({"name": name, "source": f"{dataset.name}/images/test/{p.name}"})

    for s in samples:
        with Image.open(samples_dir / f"{s['name']}.png") as im:
            s["width"], s["height"] = im.size
    print(f"  기준 이미지 {len(samples)}장 → {samples_dir}")
    return samples


def preprocess(pil_img: Image.Image, res: int) -> np.ndarray:
    """공식 ONNX 전처리 (rfdetr.export._runtime.preprocess.preprocess_to_nchw 와 같게):
    to_tensor(0~1) → 쌍선형 리사이즈(반 화소 중심, antialias=False) → ImageNet 정규화. 공식 함수가 있으면 그것을 쓴다."""
    try:
        from rfdetr.export._runtime.preprocess import preprocess_to_nchw
        return preprocess_to_nchw(pil_img, res, res, 3)
    except ImportError:
        t = TF.to_tensor(pil_img.convert("RGB"))
        t = TF.resize(t, [res, res], antialias=False)
        t = TF.normalize(t, MEAN, STD)
        return t.unsqueeze(0).numpy().astype(np.float32)


def resize_check(pil_img: Image.Image, res: int) -> dict:
    """C# 은 OpenCV 로 리사이즈한다 — 공식(torchvision) 결과와 정규화 전 0~1 값에서 얼마나 다른지 잰다."""
    t = TF.resize(TF.to_tensor(pil_img.convert("RGB")), [res, res], antialias=False).numpy()   # CHW
    rgb = np.asarray(pil_img.convert("RGB"))
    f32 = cv2.resize(rgb.astype(np.float32) / 255.0, (res, res), interpolation=cv2.INTER_LINEAR).transpose(2, 0, 1)
    u8 = cv2.resize(rgb, (res, res), interpolation=cv2.INTER_LINEAR).astype(np.float32).transpose(2, 0, 1) / 255.0
    return {"float_linear_max_abs": float(np.abs(f32 - t).max()), "uint8_linear_max_abs": float(np.abs(u8 - t).max()),
            "uint8_linear_mean_abs": float(np.abs(u8 - t).mean())}


def export_model(model, size: str) -> Path:
    tmp = OUT / f"export_{size}"   # export() 는 폴더만 받는다 — 거기 만든 파일을 models/ 로 옮기고 폴더는 지운다
    shutil.rmtree(tmp, ignore_errors=True)
    tmp.mkdir(parents=True)
    print(f"  export{inspect.signature(model.export)}")
    t0 = time.time()
    model.export(output_dir=str(tmp), verbose=False)   # verbose 면 그래프 전체(수 MB)를 찍는다
    found = sorted(p for p in tmp.rglob("*.onnx") if "backbone" not in p.name)
    if not found:
        raise RuntimeError(f"{tmp} 에 ONNX 가 없다: {list(tmp.rglob('*'))}")
    dst = OUT / "models" / f"rfdetr-{size}.onnx"
    dst.parent.mkdir(parents=True, exist_ok=True)
    shutil.move(str(found[0]), dst)
    shutil.rmtree(tmp, ignore_errors=True)
    print(f"  내보냄 {dst.name} ({dst.stat().st_size / 1e6:.1f} MB, {time.time() - t0:.0f}초, 후보 {[p.name for p in found]})")
    return dst


def coco_names(model) -> dict[int, str]:
    try:
        from rfdetr.util.coco_classes import COCO_CLASSES
        return {int(k): v for k, v in COCO_CLASSES.items()}
    except ImportError:
        names = getattr(model, "class_names", None)
        if isinstance(names, dict):
            return {int(k): v for k, v in names.items()}
        return {i: n for i, n in enumerate(names or [])}


def decode_official(dets: np.ndarray, labels: np.ndarray, size: tuple[int, int]) -> list[dict]:
    """공식 디코더(rfdetr.export._runtime.decode.decode_detections). COCO 사전학습 가중치는 배경 칸이 없는 희소 COCO 번호라 None"""
    from rfdetr.export._runtime.decode import decode_detections
    d = decode_detections(dets, labels, size, threshold=THRESHOLD, num_select=None, background_class_id=None)
    return [{"xyxy": [round(float(v), 3) for v in b], "class_id": int(c), "score": round(float(s), 5)}
            for b, c, s in zip(d.xyxy, d.class_id, d.confidence)]


def iou(a, b) -> float:
    ix = max(0.0, min(a[2], b[2]) - max(a[0], b[0]))
    iy = max(0.0, min(a[3], b[3]) - max(a[1], b[1]))
    inter = ix * iy
    union = (a[2] - a[0]) * (a[3] - a[1]) + (b[2] - b[0]) * (b[3] - b[1]) - inter
    return inter / union if union > 0 else 0.0


def match(ref: list[dict], test: list[dict]) -> dict:
    """같은 클래스·IoU 0.9 이상을 짝짓는다 (신뢰도 높은 것부터 탐욕적으로)"""
    used, matched, max_score_diff = set(), 0, 0.0
    for r in sorted(ref, key=lambda d: -d["score"]):
        best, best_iou = None, 0.9
        for j, t in enumerate(test):
            if j in used or t["class_id"] != r["class_id"]:
                continue
            v = iou(r["xyxy"], t["xyxy"])
            if v >= best_iou:
                best, best_iou = j, v
        if best is not None:
            used.add(best)
            matched += 1
            max_score_diff = max(max_score_diff, abs(r["score"] - test[best]["score"]))
    return {"ref": len(ref), "test": len(test), "matched": matched, "max_score_diff": round(max_score_diff, 5)}


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--dataset", type=Path, help="YOLO 형식 데이터셋 폴더 (images/test 를 읽기만 한다)")
    ap.add_argument("--sizes", nargs="+", default=SIZES, choices=SIZES)
    args = ap.parse_args()

    from importlib.metadata import version as pkg_version   # rfdetr 엔 __version__ 이 없다

    from rfdetr import RFDETRMedium, RFDETRNano, RFDETRSmall
    classes = {"nano": RFDETRNano, "small": RFDETRSmall, "medium": RFDETRMedium}
    rfdetr_version = pkg_version("rfdetr")
    print(f"rfdetr {rfdetr_version} · torch {torch.__version__} · onnx {onnx.__version__} · "
          f"onnxruntime {ort.__version__} · OpenCV {cv2.__version__} · Python {sys.version.split()[0]}")
    torch.manual_seed(0)

    OUT.mkdir(parents=True, exist_ok=True)
    samples = collect_samples(args.dataset)
    manifest = {"rfdetr": rfdetr_version, "torch": torch.__version__, "threshold": THRESHOLD,
                "samples": samples, "models": {}}

    for size in args.sizes:
        print(f"\n[{size}]")
        model = classes[size]()
        path = export_model(model, size)

        g = onnx.load(str(path))
        ops = Counter(n.op_type for n in g.graph.node)
        opset = next((o.version for o in g.opset_import if o.domain in ("", "ai.onnx")), None)
        sess = ort.InferenceSession(str(path), providers=["CPUExecutionProvider"])
        inp = sess.get_inputs()[0]
        res = int(inp.shape[2])
        outs = {o.name: o.shape for o in sess.get_outputs()}
        print(f"  opset {opset} · 입력 {inp.name} {inp.shape} · 출력 {outs}")
        print(f"  연산 {sum(ops.values())}개 — GridSample {ops.get('GridSample', 0)}, TopK {ops.get('TopK', 0)}, "
              f"자주 나오는 것 {ops.most_common(8)}")

        names = coco_names(model)
        entry = {"file": f"models/{path.name}", "resolution": res, "input": inp.name, "outputs": outs, "opset": opset,
                 "ops": dict(ops.most_common()), "class_names": {str(k): v for k, v in sorted(names.items())},
                 "images": {}}
        ref_dir = OUT / "ref" / size
        shutil.rmtree(ref_dir, ignore_errors=True)
        ref_dir.mkdir(parents=True)

        agree = Counter()
        for i, s in enumerate(samples):
            with Image.open(OUT / "samples" / f"{s['name']}.png") as im:
                img = im.convert("RGB")
            x = preprocess(img, res)
            x.astype(np.float32).tofile(ref_dir / f"{s['name']}.in.f32")
            o = dict(zip([o.name for o in sess.get_outputs()], sess.run(None, {inp.name: x})))
            o["dets"].astype(np.float32).tofile(ref_dir / f"{s['name']}.dets.f32")
            o["labels"].astype(np.float32).tofile(ref_dir / f"{s['name']}.labels.f32")

            onnx_dets = decode_official(o["dets"][0], o["labels"][0], img.size)
            pt = model.predict(img, threshold=THRESHOLD)
            torch_dets = [{"xyxy": [round(float(v), 3) for v in b], "class_id": int(c), "score": round(float(sc), 5)}
                          for b, c, sc in zip(pt.xyxy, pt.class_id, pt.confidence)]
            m = match(torch_dets, onnx_dets)
            agree.update({k: v for k, v in m.items() if k != "max_score_diff"})
            agree["max_score_diff_x1e5"] = max(agree["max_score_diff_x1e5"], int(m["max_score_diff"] * 1e5))
            entry["images"][s["name"]] = {"torch": torch_dets, "onnx_cpu": onnx_dets}
            if i == 0:
                entry["resize_check"] = resize_check(img, res)
                print(f"  OpenCV 리사이즈 vs 공식(0~1 값): {entry['resize_check']}")
        print(f"  PyTorch predict() vs ONNX(CPU) 검출 — 기준 {agree['ref']} · ONNX {agree['test']} · 짝 {agree['matched']} · "
              f"신뢰도 차 최대 {agree['max_score_diff_x1e5'] / 1e5:.5f}")
        entry["torch_vs_onnx_cpu"] = dict(agree)
        manifest["models"][size] = entry
        del sess, model

    (OUT / "ref" / "manifest.json").write_text(json.dumps(manifest, ensure_ascii=False, indent=1), encoding="utf-8")
    print(f"\n기준값 → {OUT / 'ref' / 'manifest.json'}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
