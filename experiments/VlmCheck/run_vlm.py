"""로컬 VLM(Ollama)으로 유고 후보를 확인한다 — 모델 설정 × 장면 × 입력 방식(A·B·C).

입력: make_cases.py 가 만든 out/cases/<id>/ 의 그림
  A = 화면 전체(후보 빨간 박스) · B = A + 후보 주변 확대 · C = B + 2초 전·뒤 화면
출력: out/results/<설정>.jsonl (한 줄에 한 번 호출 — 응답 원문·파싱한 JSON·시간), out/results/<설정>.meta.json (모델 올리는 시간·VRAM)
이미 성공한 (장면, 입력) 은 건너뛴다(다시 돌리면 이어서).

Ollama 서버가 떠 있으면 그것을 쓰고, 없으면 OLLAMA_MODELS=E:/Ollama/models 로 `ollama serve` 를 띄웠다가 끝에 끈다.
설정마다 끝나면 그 모델을 메모리에서 내린다(keep_alive 0).

    uv run --no-project --python 3.12 experiments/VlmCheck/run_vlm.py [설정 이름...]
"""
import argparse
import base64
import json
import os
import subprocess
import sys
import time
import urllib.error
import urllib.request
from pathlib import Path

HERE = Path(__file__).resolve().parent
API = "http://127.0.0.1:11434"
OLLAMA = Path(os.environ.get("LOCALAPPDATA", "")) / "Programs/Ollama/ollama.exe"

CONFIGS = [  # 빠른 것부터 — 생각 모드는 가장 느려 맨 끝
    {"name": "qwen3.5-2b", "model": "qwen3.5:2b", "think": False},
    {"name": "qwen3.5-4b", "model": "qwen3.5:4b", "think": False},
    {"name": "gemma3-4b", "model": "gemma3:4b", "think": None},
    {"name": "qwen3-vl-8b", "model": "qwen3-vl:8b-instruct", "think": None},
    # 생각 모드는 한 번에 1~2분, 생각만 1만 6천 자 넘게 써서 4096 토큰 안에 답을 못 냈다(11-07 시험) — B 만, 한도 8192
    {"name": "qwen3.5-4b-think", "model": "qwen3.5:4b", "think": True, "variants": ["B"], "num_predict": 8192},
]
PROBES = [  # 1차 결과 뒤 확인(11-07): Qwen 이 박스 안 차를 '없음'이라 했다 — 물체 종류를 맨 먼저 고르게 한 JSON 순서 탓인지,
    # 화면 전체를 같이 준 탓인지. 설명을 맨 앞에 쓰게(desc_first) 하고, 키운 그림만 주는 D 를 더한다. --probe 로 돈다
    {"name": "qwen3.5-4b-descfirst", "model": "qwen3.5:4b", "think": False, "desc_first": True, "variants": ["B", "D"]},
    {"name": "gemma3-4b-descfirst", "model": "gemma3:4b", "think": None, "desc_first": True, "variants": ["B", "D"]},
]
ROUND2 = [  # 2차(11-07): 확인에서 가장 나았던 '설명 먼저 + 키운 그림만(D)' 을 네 모델 모두에, 장면 30개. --round2 로 돈다
    {"name": "r2-qwen3.5-2b", "model": "qwen3.5:2b", "think": False, "desc_first": True, "variants": ["D"]},
    {"name": "r2-qwen3.5-4b", "model": "qwen3.5:4b", "think": False, "desc_first": True, "variants": ["D"]},
    # 검출기 라벨을 믿는 문제(T2 — 'bus' 라벨이면 버스라고 함): 라벨·신뢰도를 알려 주지 않으면 어떤가
    {"name": "r2-qwen3.5-4b-nolabel", "model": "qwen3.5:4b", "think": False, "desc_first": True, "variants": ["D"], "no_label": True},
    {"name": "r2-gemma3-4b", "model": "gemma3:4b", "think": None, "desc_first": True, "variants": ["D"]},
    {"name": "r2-qwen3-vl-8b", "model": "qwen3-vl:8b-instruct", "think": None, "desc_first": True, "variants": ["D"]},
]
ROUND3 = [  # 3차(11-07): 그림으로는 멈춤을 못 가른다(2차) — 멈춤·차로는 추적기 사실로 글에 주고 '왜 멈췄나'만 묻는다. --round3
    # 장면은 cases.json 의 round3 중 멈춘 지 3초 이상인 것. D3 = 키운 그림(박스의 3배), D2 = 좁게(2배 — 옆의 사고에 덜 끌리는지)
    {"name": "r3-qwen3.5-4b", "model": "qwen3.5:4b", "think": False, "r3": True, "variants": ["D3", "D2"]},
    {"name": "r3-qwen3.5-2b", "model": "qwen3.5:2b", "think": False, "r3": True, "variants": ["D3"]},
    {"name": "r3-gemma3-4b", "model": "gemma3:4b", "think": None, "r3": True, "variants": ["D3"]},
    {"name": "r3-qwen3-vl-8b", "model": "qwen3-vl:8b-instruct", "think": None, "r3": True, "variants": ["D3"]},
]
ROUND4 = [  # 4차(11-07): 판정을 묻지 않고 관찰만 묻는다 — 판정은 decide() 규칙이(돌리기 전에 정함). 장면은 3차와 같은 20개. --round4
    # 3차에서 VLM 이 질문의 예시 문구를 근거로 베꼈다 — 예시 문구·검출 라벨·추적기 사실을 모두 뺐다
    {"name": "r4-qwen3.5-4b", "model": "qwen3.5:4b", "think": False, "r4": True, "variants": ["D2", "D3"]},
    {"name": "r4-qwen3.5-2b", "model": "qwen3.5:2b", "think": False, "r4": True, "variants": ["D2"]},
    {"name": "r4-gemma3-4b", "model": "gemma3:4b", "think": None, "r4": True, "variants": ["D2"]},
    {"name": "r4-qwen3-vl-8b", "model": "qwen3-vl:8b-instruct", "think": None, "r4": True, "variants": ["D2"]},
]
OPTIONS = {"temperature": 0, "seed": 7, "num_ctx": 8192}   # 올릴 때도 같은 값 — 다르면 첫 호출에서 모델을 다시 올린다
VARIANTS = {"A": ["full"], "B": ["full", "crop"], "C": ["full", "crop", "prev", "next"], "D": ["crop"], "D3": ["crop"], "D2": ["crop2"]}

ENUMS = {
    "object_type": ["car", "truck", "bus", "motorcycle", "bicycle", "person", "emergency_vehicle", "tow_truck", "work_vehicle", "none", "unknown"],
    "object_state": ["stopped", "moving", "unknown"],
    "location": ["travel_lane", "shoulder", "median_side", "intersection", "sidewalk_or_plaza", "parking", "unknown"],
    "people_on_road": ["none", "few", "many", "unknown"],
    "emergency_vehicles": ["yes", "no", "unknown"],
    "cones_or_signs": ["yes", "no", "unknown"],
    "damage_visible": ["yes", "no", "unknown"],
    "smoke_or_fire": ["yes", "no", "unknown"],
    "congestion": ["none", "light", "heavy", "unknown"],
    "verdict": ["incident", "normal", "detection_error", "cannot_judge"],
}
def make_schema(desc_first=False):
    """Ollama 는 JSON 을 properties 순서대로 쓰게 한다 — desc_first 면 설명을 먼저 쓰고 나서 고른다."""
    props = {k: {"type": "string", "enum": v} for k, v in ENUMS.items()}
    desc = {"description_ko": {"type": "string"}}
    props = {**desc, **props} if desc_first else {**props, **desc}
    return {"type": "object", "properties": props, "required": list(props)}

SYSTEM = """너는 도로 CCTV 관제 보조다. 유고(돌발상황) 감지 규칙이 고른 '후보'를 확인한다.
첫 그림은 CCTV 화면 전체이고 빨간 박스가 후보다. 그림이 더 있으면 다음은 후보 주변을 키운 것, 그다음은 2초 전과 2초 뒤 화면이다(같은 자리에 빨간 박스).
보이는 것만 답하고, 확실하지 않은 항목은 unknown 을 고른다. 화면에 겹친 글자(지명·안내 문구)는 카메라가 덧씌운 것이니 판단 근거로 쓰지 않는다.
항목 뜻:
- object_type: 빨간 박스 안 물체. emergency_vehicle = 소방차·구급차·경찰차, tow_truck = 견인차, work_vehicle = 도로 작업·안전 차량, none = 차량·사람이 없음
- object_state: 박스 안 물체가 멈춰 있는지(그림이 한 장이면 대개 unknown)
- location: 박스 안 물체가 있는 곳
- people_on_road: 차로·갓길 위에 차 밖으로 나온 사람(few = 1~3명, many = 4명 이상)
- emergency_vehicles · cones_or_signs · damage_visible · smoke_or_fire · congestion: 화면 전체에 대해
verdict:
- incident: 박스 안 물체가 사고·고장·사고 처리 현장의 일부다(멈춘 사고·고장 차량, 그 현장에 출동한 소방·구급·경찰·견인·작업 차량, 차로 위 사람)
- normal: 박스 안 물체가 정상 상황이다(달리는 중, 신호 대기, 정상 주차, 평범한 정체, 보행)
- detection_error: 박스 안에 차량·사람이 없다(나무·건물·표지판·글자·그림자·빈 도로)
- cannot_judge: 화면이 검거나 심하게 흔들리거나 흐려 판단할 수 없다
description_ko 는 박스 안 물체와 주변 상황을 한국어 한두 문장으로 쓴다."""
# 확인(PROBES)용: 그림 구성이 입력 방식마다 달라(D 는 키운 그림 한 장) 첫 문단만 일반화했다 — 나머지는 SYSTEM 과 같다
SYSTEM_PROBE = SYSTEM.replace(
    "첫 그림은 CCTV 화면 전체이고 빨간 박스가 후보다. 그림이 더 있으면 다음은 후보 주변을 키운 것, 그다음은 2초 전과 2초 뒤 화면이다(같은 자리에 빨간 박스).",
    "그림은 사용자 글에 적힌 순서다(CCTV 화면 전체·후보 주변을 키운 그림·2초 전과 뒤 화면 중 일부). 모든 그림에서 빨간 박스가 후보다.")
assert SYSTEM_PROBE != SYSTEM

IMAGE_TEXT = {"full": "CCTV 화면 전체(빨간 박스 = 후보)", "crop": "후보 주변을 키운 그림", "crop2": "후보 주변을 키운 그림",
              "prev": "2초 전 화면", "next": "2초 뒤 화면"}

# ── 3차: 추적기 사실을 주고 '왜 멈췄나'만 묻는다. 판정 이름도 바꿨다(2차에서 detection_error 를 '아무 일 없음' 뜻으로 썼다)
ENUMS3 = {
    "object_type": ENUMS["object_type"],
    "emergency_vehicles": ["yes", "no", "unknown"],
    "people_on_road": ["none", "few", "many", "unknown"],
    "damage_visible": ["yes", "no", "unknown"],
    "queue_or_signal": ["yes", "no", "unknown"],
    "parking_area": ["yes", "no", "unknown"],
    "verdict": ["accident_or_breakdown", "normal_stop", "not_vehicle", "unusable_image"],
}


def make_schema3():
    props = {"description_ko": {"type": "string"}, "cause_evidence": {"type": "string"},
             **{k: {"type": "string", "enum": v} for k, v in ENUMS3.items()}}
    return {"type": "object", "properties": props, "required": list(props)}


SYSTEM3 = """너는 도로 CCTV 관제 보조다. 유고 감지 규칙이 '차로 구역 안에서 몇 초째 멈춰 있는 차량'을 후보로 골랐다.
멈춤과 위치는 추적기가 이미 확인했으니 다시 따지지 않는다. 네 일은 그림에서 '왜 멈췄는지'를 찾는 것이다.
그림은 후보 주변을 키운 것이고 빨간 박스가 후보다. 보이는 것만 쓰고, 확실하지 않으면 unknown 을 고른다. 화면에 겹친 글자(지명·안내 문구)는 근거로 쓰지 않는다.
verdict:
- accident_or_breakdown: 박스 안 차량이 사고·고장으로 멈췄거나 그 현장 처리의 일부다(손상·비상등·차 밖의 사람·삼각대, 현장에 출동한 소방·구급·경찰·견인·작업 차량)
- normal_stop: 정상적인 정차다(신호 대기 줄, 차들이 줄지어 선 정체, 주차장·길가 주차, 정류장·요금소)
- not_vehicle: 박스 안에 차량이 없다(나무·건물·표지판·글자·그림자·빈 도로)
- unusable_image: 화면이 검거나 심하게 흔들리거나 흐려 볼 수 없다
cause_evidence 에는 판정 근거가 된 '보이는 단서'를 짧게 쓴다(예: '소방차와 구조대원', '앞뒤로 줄지어 선 차들과 정지선').
description_ko 는 박스 안 차량과 주변을 한국어 한두 문장으로 쓴다."""

# ── 4차: 관찰만 묻는다(예·아니오·모름). 판정은 decide() 가 단서로 — 규칙은 돌리기 전에 정했다
YN = ["yes", "no", "unknown"]
ENUMS4 = {
    "image_usable": YN, "vehicle_in_box": YN, "vehicle_type": ENUMS["object_type"],
    "emergency_vehicle_near": YN, "flashing_lights": YN, "people_on_road": YN, "visible_damage": YN,
    "door_or_trunk_open": YN, "cones_or_triangle": YN,
    "vehicles_stopped_ahead": YN, "signal_or_stop_line": YN, "parking_area": YN,
}


def make_schema4():
    props = {"description_ko": {"type": "string"}, **{k: {"type": "string", "enum": v} for k, v in ENUMS4.items()}}
    return {"type": "object", "properties": props, "required": list(props)}


SYSTEM4 = """너는 도로 CCTV 화면을 보고 사실만 적는 관찰자다. 그림은 CCTV 화면의 일부를 키운 것이고 빨간 박스가 확인할 대상이다.
판단하거나 추측하지 말고 그림에 보이는 것만 답한다. 보이지 않거나 확실하지 않으면 unknown 을 고른다. 화면에 겹친 글자는 무시한다.
항목:
- image_usable: 그림이 대상을 알아볼 만큼 선명한가(검은 화면·심한 흔들림·심한 흐림이면 no)
- vehicle_in_box: 빨간 박스 안에 차량이 있는가
- vehicle_type: 박스 안 차량의 종류(차량이 없으면 none)
- emergency_vehicle_near: 박스 안이나 그 주변에 소방차·구급차·경찰차·견인차가 보이는가
- flashing_lights: 경광등이나 비상등을 켠 차량이 보이는가
- people_on_road: 차로 위(보도·횡단보도는 빼고)에 차 밖으로 나와 서 있거나 걷는 사람이 보이는가
- visible_damage: 박스 안 차량에 찌그러지거나 부서진 곳이 보이거나, 도로에 파편이 보이는가
- door_or_trunk_open: 박스 안 차량의 문·트렁크·보닛이 열려 있는가
- cones_or_triangle: 라바콘이나 삼각대가 도로에 놓여 있는가
- vehicles_stopped_ahead: 박스 안 차량 바로 앞(같은 차로)에 다른 차량이 바짝 붙어 줄지어 있는가
- signal_or_stop_line: 신호등·정지선·횡단보도가 박스 안 차량 가까이 보이는가
- parking_area: 박스 안 차량이 주차장이나 주차 칸 안에 있는가
description_ko 는 보이는 것을 한국어 한두 문장으로 쓴다."""

INCIDENT_CUES = ("emergency_vehicle_near", "flashing_lights", "people_on_road", "visible_damage", "door_or_trunk_open")
NORMAL_CUES = ("vehicles_stopped_ahead", "signal_or_stop_line", "parking_area")


def decide(p):
    """4차 판정 규칙(돌리기 전에 정함): 화면 불량 → 차량 아님 → 사고 단서가 하나라도 → 정상 맥락 → 아무 단서도 없으면
    규칙(차로에 멈춘 차)의 후보를 그대로 둔다(사고·고장). 라바콘·삼각대는 표시용 — 주차장 옆 라바콘 같은 정상 장면이 있어 판정엔 안 쓴다"""
    if p["image_usable"] == "no":
        return "unusable_image"
    if p["vehicle_in_box"] == "no":
        return "not_vehicle"
    if any(p[k] == "yes" for k in INCIDENT_CUES):
        return "accident_or_breakdown"
    if any(p[k] == "yes" for k in NORMAL_CUES):
        return "normal_stop"
    return "accident_or_breakdown"


def call(path, body=None, timeout=900):
    req = urllib.request.Request(API + path, data=None if body is None else json.dumps(body).encode(),
                                 headers={"Content-Type": "application/json"}, method="GET" if body is None else "POST")
    with urllib.request.urlopen(req, timeout=timeout) as r:
        return json.loads(r.read().decode("utf-8"))


def server_up():
    try:
        call("/api/version", timeout=3)
        return True
    except Exception:
        return False


def gpu_mem_mib():
    try:
        out = subprocess.run(["nvidia-smi", "--query-gpu=memory.used", "--format=csv,noheader,nounits"], capture_output=True, text=True)
        return int(out.stdout.strip().splitlines()[0])
    except Exception:
        return None


def check(parsed, enums=ENUMS, texts=("description_ko",)):
    bad = [k for k, v in enums.items() if parsed.get(k) not in v]
    bad += [k for k in texts if not isinstance(parsed.get(k), str)]
    return bad


def run_config(cfg, cases, out_dir, round3=()):
    res_path = out_dir / f"{cfg['name']}.jsonl"
    done = set()
    if res_path.exists():
        for line in res_path.read_text(encoding="utf-8").splitlines():
            r = json.loads(line)
            if r.get("error") is None:
                done.add((r["case"], r["variant"]))
    # 모델 올리기 — 걸린 시간과 VRAM 을 남긴다
    before = gpu_mem_mib()
    t0 = time.time()
    warm = call("/api/generate", {"model": cfg["model"], "prompt": "", "keep_alive": "20m", "options": OPTIONS})
    meta = {"config": cfg, "load_s": round(time.time() - t0, 2), "load_duration_s": round(warm.get("load_duration", 0) / 1e9, 2),
            "gpu_mib_before": before, "gpu_mib_after_load": gpu_mem_mib()}
    ps = call("/api/ps").get("models", [])
    for m in ps:
        if m.get("name", "").startswith(cfg["model"].split(":")[0]):
            meta.update(size_gib=round(m["size"] / 2**30, 2), size_vram_gib=round(m.get("size_vram", 0) / 2**30, 2))
    print(f"== {cfg['name']} 올림 {meta['load_s']}초 · {meta.get('size_vram_gib')}/{meta.get('size_gib')} GiB 가 GPU", flush=True)

    with res_path.open("a", encoding="utf-8") as f:
        for c in cases:
            d = HERE / "out" / "cases" / c["id"]
            info = json.loads((d / "case.json").read_text(encoding="utf-8"))
            if (cfg.get("r3") or cfg.get("r4")) and (c["id"] not in round3 or info.get("stopped_s", 0) < 3):
                continue   # 3·4차는 규칙이 고를 '멈춘 차'만
            for v, imgs in VARIANTS.items():
                if (c["id"], v) in done or v not in cfg.get("variants", ["A", "B", "C"]):
                    continue
                if cfg.get("r4"):
                    ask = "보이는 것만 JSON 으로 답하라."
                    system, schema, enums, texts = SYSTEM4, make_schema4(), ENUMS4, ("description_ko",)
                elif cfg.get("r3"):
                    ask = (f"추적기: 박스 안 차량은 {info['stopped_s']:.0f}초째 거의 움직이지 않았고 차로 구역 안에 있다(멈춤·위치는 확인됨). "
                           f"검출기 라벨 '{info['label']}'(신뢰도 {info['score']:.2f}). 왜 멈췄는지 JSON 으로 답하라.")
                    system, schema, enums, texts = SYSTEM3, make_schema3(), ENUMS3, ("description_ko", "cause_evidence")
                else:
                    ask = ("빨간 박스 안 물체를 확인해 JSON 으로 답하라." if cfg.get("no_label") else
                           f"검출기는 빨간 박스 안 물체를 '{info['label']}'(으)로 봤다(신뢰도 {info['score']:.2f}). 이 후보를 확인해 JSON 으로 답하라.")
                    system = SYSTEM_PROBE if cfg.get("desc_first") else SYSTEM
                    schema, enums, texts = make_schema(cfg.get("desc_first", False)), ENUMS, ("description_ko",)
                lines = [f"그림 {len(imgs)}장: " + " / ".join(f"{i + 1}) {IMAGE_TEXT[k]}" for i, k in enumerate(imgs)), ask]
                body = {"model": cfg["model"], "stream": False, "keep_alive": "20m", "format": schema,
                        "messages": [{"role": "system", "content": system},
                                     {"role": "user", "content": "\n".join(lines),
                                      "images": [base64.b64encode((d / f"{k}.jpg").read_bytes()).decode() for k in imgs]}],
                        "options": {**OPTIONS, "num_predict": cfg.get("num_predict", 768)}}
                if cfg["think"] is not None:
                    body["think"] = cfg["think"]
                rec = {"config": cfg["name"], "case": c["id"], "variant": v, "error": None}
                t0 = time.time()
                try:
                    r = call("/api/chat", body)
                    rec.update(wall_s=round(time.time() - t0, 2), done_reason=r.get("done_reason"),
                               prompt_tokens=r.get("prompt_eval_count"), out_tokens=r.get("eval_count"),
                               total_s=round(r.get("total_duration", 0) / 1e9, 2), load_s=round(r.get("load_duration", 0) / 1e9, 2),
                               content=r["message"].get("content", ""), thinking_chars=len(r["message"].get("thinking") or ""))
                    try:
                        parsed = json.loads(rec["content"])
                        rec.update(parsed=parsed, invalid=check(parsed, enums, texts))
                        if cfg.get("r4") and not rec["invalid"]:
                            parsed["verdict"] = decide(parsed)   # 모델 답이 아니라 규칙 — 채점은 3차 정답으로
                    except json.JSONDecodeError:
                        rec.update(parsed=None, invalid=["json"])
                except (urllib.error.URLError, TimeoutError, OSError) as e:
                    rec.update(wall_s=round(time.time() - t0, 2), error=str(e))
                f.write(json.dumps(rec, ensure_ascii=False) + "\n")
                f.flush()
                p = rec.get("parsed") or {}
                print(f"{cfg['name']} {c['id']} {v}: {p.get('verdict', '-'):14} {p.get('object_type', '-'):18} "
                      f"{rec['wall_s']:6.1f}초 출력 {rec.get('out_tokens')}토큰{' 생각 ' + str(rec.get('thinking_chars')) + '자' if cfg['think'] else ''}"
                      f"{' 오류 ' + rec['error'] if rec['error'] else ''}{' 형식 어긋남 ' + str(rec.get('invalid')) if rec.get('invalid') else ''}", flush=True)
    (out_dir / f"{cfg['name']}.meta.json").write_text(json.dumps(meta, ensure_ascii=False, indent=1), encoding="utf-8")
    return meta


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("configs", nargs="*", help="설정 이름(없으면 모두)")
    ap.add_argument("--cases", help="장면 id 를 쉼표로(없으면 모두) — 시험 삼아 몇 개만")
    ap.add_argument("--results", default="results", help="out/ 아래 결과 폴더 이름")
    ap.add_argument("--probe", action="store_true", help="1차 결과 뒤 확인(PROBES)을 돈다")
    ap.add_argument("--round2", action="store_true", help="2차(ROUND2)를 돈다")
    ap.add_argument("--round3", action="store_true", help="3차(ROUND3)를 돈다")
    ap.add_argument("--round4", action="store_true", help="4차(ROUND4)를 돈다")
    a = ap.parse_args()
    spec = json.loads((HERE / "cases.json").read_text(encoding="utf-8"))
    cases, round3 = spec["cases"], spec.get("round3", {})
    if a.cases:
        cases = [c for c in cases if c["id"] in a.cases.split(",")]
    pool = ROUND4 if a.round4 else ROUND3 if a.round3 else ROUND2 if a.round2 else PROBES if a.probe else CONFIGS
    configs = [c for c in pool if not a.configs or c["name"] in a.configs]
    out_dir = HERE / "out" / a.results
    out_dir.mkdir(parents=True, exist_ok=True)
    started = None
    if not server_up():
        env = dict(os.environ, OLLAMA_MODELS="E:/Ollama/models")
        started = subprocess.Popen([str(OLLAMA), "serve"], env=env, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        for _ in range(30):
            if server_up():
                break
            time.sleep(1)
        print(f"Ollama 서버를 띄움(PID {started.pid})", flush=True)
    try:
        prev_model = None
        for cfg in configs:
            if prev_model and prev_model != cfg["model"]:
                call("/api/generate", {"model": prev_model, "keep_alive": 0})   # 앞 모델을 내린다
            run_config(cfg, cases, out_dir, round3)
            prev_model = cfg["model"]
        if prev_model:
            call("/api/generate", {"model": prev_model, "keep_alive": 0})
    finally:
        if started:
            started.terminate()


if __name__ == "__main__":
    sys.exit(main())
