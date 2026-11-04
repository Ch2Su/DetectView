"""2단계: RF-DETR-S 를 데이터셋(백업의 작업 사본)으로 파인튜닝한다 — WSL 의 ~/rfdetr_work/.venv 에서 돈다 (README '2단계').

    ~/rfdetr_work/.venv/bin/python train_rfdetr.py --data ~/rfdetr_work/data/visible --out ~/rfdetr_work/runs/rfdetr_s

조리법은 rfdetr 기본값 그대로다(100 epoch · lr 1e-4 · EMA · 배치마다 다른 크기 352~672 · bf16). 배치만 실제 4 × 그래디언트 누적 4
= 실효 16 (rfdetr 의 'auto' 가 맞추는 목표 실효 배치와 같다 — 문서의 작은 GPU 조합)이고 시드 0, 데이터 로더 8개.
YOLO 쪽(run_step2.sh)도 각자의 기본 조리법을 같은 100 epoch 로 돌린다.

'auto' 를 안 쓰는 이유 (2026-11-04): 'auto' 는 배치를 2, 4, 8… 로 키우며 GPU 메모리가 넘칠 때까지 시험하는데, 이 WSL 의 CUDA 는
한계에서 메모리 부족 대신 'device not ready' 를 내고 컨텍스트가 망가져(뒤이은 empty_cache 도 out of memory) 학습 전에 죽었다.
"""
from __future__ import annotations

import argparse
import inspect
import sys
import time
from pathlib import Path


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--data", type=Path, required=True, help="YOLO 형식 데이터셋 폴더 (data.yaml 의 path 가 이 폴더여야 한다)")
    ap.add_argument("--out", type=Path, required=True)
    ap.add_argument("--epochs", type=int, default=100)
    ap.add_argument("--workers", type=int, default=8)
    ap.add_argument("--batch", default="4", help="실제 배치 수, 또는 'auto'(이 WSL 에선 시험 중에 죽는다 — 머리말)")
    ap.add_argument("--accum", type=int, default=4, help="그래디언트 누적 단계 — 배치 × 누적 = 실효 배치 ('auto' 면 무시)")
    ap.add_argument("--resume", type=Path, help="끊긴 학습을 이을 checkpoint.pth")
    args = ap.parse_args()

    import os
    import shutil
    import torch
    from importlib.metadata import version
    from rfdetr import RFDETRSmall

    # rfdetr 1.11.2 의 학습 매처(헝가리안)는 torch_linear_assignment 의 Triton 커널을 쓰는데, Triton 이 그 자리에서 C 모듈을
    # 컴파일하려고 C 컴파일러를 찾는다 — 이 WSL 엔 gcc 가 없어(sudo 없이 못 깖) 첫 배치에서 멈췄다 (2026-11-04).
    # 그럴 땐 그 라이브러리가 Triton 을 못 쓸 때 가는 SciPy(CPU) 경로로 보낸다 — DETR·LW-DETR 의 원래 방식이고 결과는 같은 최적 짝이다
    if not any(shutil.which(c) for c in (os.environ.get("CC", ""), "cc", "gcc", "clang") if c):
        import torch_linear_assignment.assignment as tla
        tla._cuda_uses_triton = lambda cost: False
        print("C 컴파일러가 없어 헝가리안 매칭을 SciPy(CPU)로 푼다", flush=True)

    print(f"rfdetr {version('rfdetr')} · torch {torch.__version__} · {torch.cuda.get_device_name(0)}", flush=True)
    # 데이터셋 경로가 백업이나 원본(~/datasets/visible)이 아니라 작업 사본인지 — 원본은 라벨링 도구가 덮어쓰고, 백업은 고치지 않는다
    yaml_path = next((p for p in (args.data / "data.yaml", args.data / "data.yml") if p.exists()), None)
    if yaml_path is None:
        raise SystemExit(f"{args.data} 에 data.yaml 이 없다")
    path_line = next((l for l in yaml_path.read_text().splitlines() if l.startswith("path:")), "")
    if path_line and Path(path_line.split(":", 1)[1].strip()).resolve() != args.data.resolve():
        raise SystemExit(f"{yaml_path} 의 {path_line!r} 가 이 폴더가 아니다 — 다른 데이터셋을 읽게 된다")

    args.out.mkdir(parents=True, exist_ok=True)
    model = RFDETRSmall()   # COCO 사전학습 가중치에서 시작
    batch = args.batch if args.batch == "auto" else int(args.batch)
    kw = dict(dataset_dir=str(args.data), dataset_file="yolo", epochs=args.epochs, batch_size=batch,
              num_workers=args.workers, seed=0, output_dir=str(args.out), tensorboard=False)
    if batch != "auto":
        kw["grad_accum_steps"] = args.accum
    if args.resume:
        kw["resume"] = str(args.resume)
    print(f"train({kw})", flush=True)
    t0 = time.time()
    model.train(**kw)
    print(f"학습 끝 — {(time.time() - t0) / 3600:.2f}시간", flush=True)
    return 0


if __name__ == "__main__":
    sys.exit(main())
