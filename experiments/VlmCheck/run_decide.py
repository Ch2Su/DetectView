"""Kanana 판정 모델(kakaocorp/kanana-2-v-decide-3b-2611)로 4차 장면을 본다 — run_vlm.py 의 DECIDE_POOL.

글을 만들지 않는 '판정 모델'이다. 상태(글·그림)와 질문(예/아니오 · 고르기)을 주면 답 후보마다 확률을 낸다.
Ollama 로는 못 돌려서, 모델이 함께 주는 파이썬 코드(trust_remote_code — 2026-11-08 일곱 파일을 읽어 봄: 외부 실행·eval·pickle 없음,
네트워크는 그림을 http 주소로 줄 때만)를 transformers 로 직접 부른다. 질문은 run_vlm.decide_questions() — 4차 항목과 같은 글자다.

준비 (한 번 — 모델 7.33 GB · 가상 환경 · 내려받기 캐시를 모두 E:\\Kanana 에):
    $env:UV_CACHE_DIR = 'E:\\Kanana\\uv-cache'
    uv venv E:\\Kanana\\.venv --python 3.12
    uv pip install --python E:\\Kanana\\.venv\\Scripts\\python.exe torch==2.10.0 torchvision==0.25.0 --index-url https://download.pytorch.org/whl/cu128
    uv pip install --python E:\\Kanana\\.venv\\Scripts\\python.exe transformers==5.5.3 pillow==12.1.1 einops==0.8.2 safetensors==0.7.0 accelerate bitsandbytes
    # 모델 — 판(revision)을 박아 둔다(아래 REVISION)
    uv run --no-project --python 3.12 --with huggingface_hub python -c "from huggingface_hub import snapshot_download as s; s('kakaocorp/kanana-2-v-decide-3b-2611', revision='<REVISION>', local_dir=r'E:\\Kanana\\kanana-2-v-decide-3b-2611')"

실행 (저장소 루트에서 — -I 라 사용자 site-packages·PYTHON* 환경 변수를 안 본다. 그래서 출력 글자는 -X utf8 로):
    $env:HF_HUB_OFFLINE = '1'
    E:\\Kanana\\.venv\\Scripts\\python.exe -I -X utf8 experiments/VlmCheck/run_decide.py r4-kanana-decide-3b --results round4_quant
    # 8비트가 확률을 얼마나 바꾸는지 — 같은 장면을 BF16 그대로(GPU 가 비어 있으면 7.33 GB 가 들어간다. 앱·바탕화면이 GPU 를 쓰면 CPU 판으로)
    E:\\Kanana\\.venv\\Scripts\\python.exe -I -X utf8 experiments/VlmCheck/run_decide.py r4-kanana-decide-3b-bf16 --results round4_decide_ref
    E:\\Kanana\\.venv\\Scripts\\python.exe -I -X utf8 experiments/VlmCheck/run_decide.py r4-kanana-decide-3b-bf16cpu --results round4_decide_ref --cases P2,P5,P11,N4,S1

결과는 run_vlm.py 와 같은 꼴(out/<결과>/<설정>.jsonl · .meta.json)이라 score.py·report.py 가 그대로 읽는다.
parsed 는 4차 답과 같은 항목(yes/no — P(예) ≥ run_vlm.DECIDE_YES 면 yes)에 판정 질문 답(direct_verdict)을 더한 것이고,
probs 에 질문마다 확률(예/아니오는 P(예), 고르기는 후보별)을 남긴다. 설명(description_ko)은 없다 — 빈 글.
이미 성공한 (장면, 입력) 은 건너뛴다(다시 돌리면 이어서).
"""
import argparse
import json
import subprocess
import sys
import time
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))   # -I 로 돌려도 run_vlm·report 를 읽게 (스크립트 폴더는 저장소 안 — 내려받은 폴더가 아니다)
import run_vlm  # noqa: E402

MODEL_DIR = Path(r"E:\Kanana\kanana-2-v-decide-3b-2611")
REVISION = "1489062def4145ae3a0a9261fbac4f4d88a8c6d1"   # 2026-11-06 공개판 — 코드를 읽어 본 판


def load(cfg):
    import warnings
    import torch
    from transformers import AutoModel, BitsAndBytesConfig
    # LLM.int8 은 입력을 FP16 으로 바꿔 곱한다 — 그 안내가 층마다 찍혀 결과 줄을 덮는다(바뀌는 건 없다)
    warnings.filterwarnings("ignore", message="MatMul8bitLt: inputs will be cast")
    kw = dict(trust_remote_code=True, dtype=torch.bfloat16)
    if cfg["device"] == "cuda":
        kw["device_map"] = {"": 0}
        if cfg["quant"] == "int8":
            # 언어 부분만 8비트 — 그림 읽는 부분(visual, 0.49B)은 BF16 그대로(Ollama 양자화 비교와 같은 나눔)
            kw["quantization_config"] = BitsAndBytesConfig(load_in_8bit=True, llm_int8_skip_modules=["visual"])
    else:
        kw["device_map"] = {"": "cpu"}
    return AutoModel.from_pretrained(str(MODEL_DIR), **kw).eval()


def to_record(answers):
    """판정 모델 답 → 4차 답 꼴(parsed)과 확률(probs)"""
    parsed, probs = {"description_ko": ""}, {}
    for k, a in answers.items():
        if a["type"] == "noul":
            probs[k] = round(a["noul"], 4)
            parsed[k] = "yes" if a["noul"] >= run_vlm.DECIDE_YES else "no"
        else:
            probs[k] = {c: round(p, 4) for c, p in a["probabilities"].items()}
            parsed[k] = a["choice"]
    return parsed, probs


def gpu_used_mib():
    try:
        out = subprocess.run(["nvidia-smi", "--query-gpu=memory.used", "--format=csv,noheader,nounits"],
                             capture_output=True, text=True, timeout=10).stdout
        return int(out.split()[0])
    except (OSError, ValueError, IndexError, subprocess.SubprocessError):
        return None


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("config", help="run_vlm.DECIDE_POOL 의 설정 이름")
    ap.add_argument("--cases", help="장면 id 를 쉼표로(없으면 4차 장면 모두)")
    ap.add_argument("--results", default="round4_quant", help="out/ 아래 결과 폴더 이름")
    a = ap.parse_args()
    sys.stdout.reconfigure(errors="replace")   # cp949 콘솔에서 '—' 같은 글자로 멈추지 않게
    cfg = next(c for c in run_vlm.DECIDE_POOL if c["name"] == a.config)
    spec = json.loads((HERE / "cases.json").read_text(encoding="utf-8"))
    cases, round3 = spec["cases"], spec.get("round3", {})
    if a.cases:
        cases = [c for c in cases if c["id"] in a.cases.split(",")]
    out_dir = HERE / "out" / a.results
    out_dir.mkdir(parents=True, exist_ok=True)
    res_path = out_dir / f"{cfg['name']}.jsonl"
    done = run_vlm.done_pairs(res_path)

    import torch
    import transformers
    from PIL import Image
    questions = run_vlm.decide_questions()
    cuda = cfg["device"] == "cuda"
    before = gpu_used_mib()
    t0 = time.time()
    model = load(cfg)
    meta = {"config": cfg, "api": "decide", "load_s": round(time.time() - t0, 2), "quant": cfg["quant"], "device": cfg["device"],
            "revision": REVISION, "torch": torch.__version__, "transformers": transformers.__version__,
            "footprint_gib": round(model.get_memory_footprint() / 2**30, 2), "gpu_mib_before": before, "gpu_mib_after_load": gpu_used_mib()}
    if cfg["quant"] == "int8":
        import bitsandbytes
        meta["bitsandbytes"] = bitsandbytes.__version__
    else:
        meta["threads"] = torch.get_num_threads()
    # 데우기: 답 후보 글의 임베딩은 모델이 캐시한다(앱에 넣어도 첫 호출 뒤엔 그렇다) — 첫 장면 시간에 섞이지 않게 회색 그림으로 한 번
    t1 = time.time()
    model.system_one(state=run_vlm.DECIDE_STATE, images=[Image.new("RGB", (512, 512), (128, 128, 128))], questions=questions)
    meta["warmup_s"] = round(time.time() - t1, 2)
    print(f"== {cfg['name']} 올림 {meta['load_s']}초 · {cfg['quant']} {cfg['device']} · 모델 {meta['footprint_gib']} GiB · "
          f"GPU {before} → {meta['gpu_mib_after_load']} MiB · 데우기 {meta['warmup_s']}초", flush=True)
    if cuda:
        torch.cuda.reset_peak_memory_stats()

    with res_path.open("a", encoding="utf-8") as f:
        for c in cases:
            d = HERE / "out" / "cases" / c["id"]
            info = json.loads((d / "case.json").read_text(encoding="utf-8"))
            if not run_vlm.stopped_scene(c["id"], info, round3):
                continue   # 4차와 같은 장면 — 규칙이 고를 '멈춘 차'만 (run_vlm.run_config 와 같은 조건)
            for v in run_vlm.VARIANTS:
                if (c["id"], v) in done or v not in cfg["variants"]:
                    continue
                q = run_vlm.question(cfg, info, v)
                rec = {"config": cfg["name"], "case": c["id"], "variant": v, "error": None}
                t2 = time.time()
                try:
                    images = [Image.open(d / f"{k}.jpg").convert("RGB") for k in q["images"]]
                    r = model.system_one(state=run_vlm.DECIDE_STATE, images=images, questions=questions)
                    if cuda:
                        torch.cuda.synchronize()
                    wall = round(time.time() - t2, 2)
                    parsed, probs = to_record(r["answers"])
                    rec.update(wall_s=wall, done_reason="decide", prompt_tokens=r["usage"]["input_tokens"], out_tokens=0,
                               total_s=wall, load_s=0, thinking_chars=0, questions=len(questions),
                               content=json.dumps(r["answers"], ensure_ascii=False), parsed=parsed, probs=probs,
                               invalid=run_vlm.check(parsed, run_vlm.ENUMS4, q["texts"]))
                    if not rec["invalid"]:
                        parsed["verdict"] = run_vlm.decide(parsed)   # 4차와 같은 규칙 — 채점은 3차 정답으로
                except Exception as e:   # 한 장면이 실패해도 나머지는 돈다 — 사유를 남긴다
                    rec.update(wall_s=round(time.time() - t2, 2), error=f"{type(e).__name__}: {e}"[:400])
                f.write(json.dumps(rec, ensure_ascii=False) + "\n")
                f.flush()
                p = rec.get("parsed") or {}
                pr = rec.get("probs") or {}
                dv = pr.get("direct_verdict") or {}
                print(f"{cfg['name']} {c['id']} {v}: {p.get('verdict', '-'):22} 판정 질문 {p.get('direct_verdict', '-'):22} "
                      f"{rec['wall_s']:6.1f}초{' 오류 ' + rec['error'] if rec['error'] else ''}", flush=True)
                if p:
                    cues = " ".join(f"{run_vlm.FIELD_KO[k]} {pr[k]:.2f}" for k in (*run_vlm.INCIDENT_CUES, *run_vlm.NORMAL_CUES,
                                                                                  "cones_or_triangle", "vehicle_in_box", "image_usable"))
                    print(f"    {run_vlm.cue_text(p)} | {cues} | 판정 질문 " + " ".join(f"{k} {x:.2f}" for k, x in dv.items()), flush=True)
    if cuda:
        meta["peak_alloc_gib"] = round(torch.cuda.max_memory_allocated() / 2**30, 2)
        meta["gpu_mib_end"] = gpu_used_mib()
    (out_dir / f"{cfg['name']}.meta.json").write_text(json.dumps(meta, ensure_ascii=False, indent=1), encoding="utf-8")
    try:   # 장면별 보고서 — 실패해도 결과(jsonl)는 이미 남았다
        import report
        print(f"보고서: {report.build(out_dir)}", flush=True)
    except Exception as e:
        print(f"보고서를 못 만듦: {e!r} (report.py 로 다시)", flush=True)


if __name__ == "__main__":
    main()
