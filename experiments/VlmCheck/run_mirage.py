"""Mirage(Cactus Compute 의 22 MB 비전 모델)로 4차 장면을 본다 — run_vlm.py 의 MIRAGE_POOL 두 설정을 한 번에.

51.3M 매개변수 · 2비트(mirage.cact 21.6 MB) · Apache-2.0. CPU 로 돈다(파이썬 패키지 cactus-compute 의 엔진 libcactus.dll 4.1.0).
그림마다 두 번 부른다:
  r4-mirage-form      extract(그림, 항목): 항목마다 고를 답의 확률('-' = 답하지 않음 → unknown). 항목 설명(description)은 엔진이
                      쓰지 않아서(11-09 확인) 4차 항목 이름과 고를 답이 곧 질문이다. 판정도 하나 더 묻는다(why_stopped — 3차 후보 이름)
  r4-mirage-describe  describe(그림): 장면·물체(이름·상자 0~1000·점수·속성)·관계·추론 글. 돌리기 전에 정한 낱말 표(CUE_WORDS)로
                      4차 항목을 채운다 — 표를 정할 때 본 장면은 P1·N1·S1 셋(엔진 출력 꼴과 nan 을 보려고)
두 설정 모두 판정은 4차와 같은 규칙(run_vlm.decide)이다. 결과는 run_vlm.py 와 같은 꼴(out/<결과>/<설정>.jsonl · .meta.json)이라
score.py·report.py 가 그대로 읽는다. 이미 성공한 (장면, 입력) 은 건너뛴다(다시 돌리면 이어서).

준비 (사용자가 한 번 — 외부 코드라 자동 모드가 설치를 막았다. 가상 환경·엔진·가중치 모두 E:\\Cactus):
    uv venv E:\\Cactus\\.venv --python 3.12
    uv pip install --python E:\\Cactus\\.venv\\Scripts\\python.exe cactus-compute pillow
    $env:XDG_CACHE_HOME='E:\\Cactus\\cache'; $env:CACTUS_TELEMETRY='0'; E:\\Cactus\\.venv\\Scripts\\cactus.exe fetch; E:\\Cactus\\.venv\\Scripts\\cactus.exe download mirage
    (download 은 지금 폴더에도 mirage.cact 를 내려놓는다 — 저장소 밖에서 돌린다. 이 스크립트는 캐시의 것을 읽는다)

실행 (저장소 루트에서 — 텔레메트리·내려받기를 끈다. -I 라 출력 글자는 -X utf8 로):
    $env:XDG_CACHE_HOME='E:\\Cactus\\cache'; $env:CACTUS_TELEMETRY='0'; $env:DO_NOT_TRACK='1'; $env:HF_HUB_OFFLINE='1'
    E:\\Cactus\\.venv\\Scripts\\python.exe -I -X utf8 experiments/VlmCheck/run_mirage.py --results round4_quant
    E:\\Cactus\\.venv\\Scripts\\python.exe -I -X utf8 experiments/VlmCheck/run_mirage.py --probe-nl   # 사후 확인 — 항목 이름을 영어 문장으로, 화면에만
"""
import argparse
import hashlib
import json
import os
import platform
import re
import sys
import time
from importlib import metadata
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))   # -I 로 돌려도 run_vlm·report 를 읽게 (스크립트 폴더는 저장소 안 — 내려받은 폴더가 아니다)
import run_vlm  # noqa: E402

# 엔진 버그(4.1.0): 점수가 nan 이면 JSON 에 -nan(ind) 를 그대로 써서 json.loads 가 깨진다(S1 — 글자 물체 하나).
# 원문 버퍼에서 nan·inf 값만 null 로 바꿔 읽고 바꾼 개수를 기록에 남긴다. 문자열을 먼저 통째로 잡아 건너뛴다 —
# 읽은 글자·추론 글 안의 ', nan,' 같은 조각은 값이 아니다
NAN = re.compile(r'"(?:[^"\\]|\\.)*"|(?<=[:\[,])\s*-?(?:nan|inf)(?:\([a-z]+\))?(?=\s*[,\]}])', re.I)


def fix_nan(raw):
    """(nan·inf 값을 null 로 바꾼 글, 바꾼 개수)"""
    n = 0

    def rep(m):
        nonlocal n
        if m.group(0).startswith('"'):
            return m.group(0)
        n += 1
        return "null"
    return NAN.sub(rep, raw), n



FORM = {k: {"enum": v} for k, v in run_vlm.MIRAGE_FORM.items()}


def W(words):
    return re.compile(rf"\b(?:{words})\b", re.I)


# describe → 4차 항목 낱말 표 (돌리기 전에 정함 — 11-09). 물체·구역의 이름과 속성 값, 장면 값, 사건 글만 본다.
# 11-10 코드 검토 뒤 고친 것(README 결과 12 의 숫자는 고치기 전 코드로 낸 것): 밑줄 이름 띄어 읽기 · 박스 가운데를 크롭 셈으로 ·
# 문·트렁크 열림은 차량·문 물체에서만. 낱말 자체는 그대로다
# 추론 글(reasoning)은 '화살표 표지는 차·보행자를 안내한다' 같은 일반 지식이 섞여 보지 않는다(설명 칸에만 보인다).
# 차로 위인지·박스 안 차인지는 describe 로 가를 수 없다 — 사람은 어디 있든 센다
CUE_WORDS = {
    "vehicle": W("car|cars|truck|trucks|bus|buses|van|vans|minivan|suv|pickup|motorcycle|motorcycles|motorbike|scooter|moped|"
                 "bicycle|bicycles|bike|vehicle|vehicles|taxi|ambulance|lorry|trailer|jeep|sedan|tanker|tractor|excavator|bulldozer|forklift"),
    "emergency": W("fire truck|fire engine|ambulance|police car|police vehicle|police van|patrol car|tow truck|wrecker|emergency vehicle"),
    "person": W("person|persons|people|man|men|woman|women|pedestrian|pedestrians|worker|workers|firefighter|firefighters|"
                "officer|officers|child|children|boy|girl|crowd|driver"),
    "lights": W("siren|sirens|beacon|beacons|flashing|emergency lights?|hazard lights?|warning lights?|light ?bar"),
    "damage": W("damage|damaged|dent|dented|wreck|wrecked|wreckage|crash|crashed|debris|broken|smashed|shattered|overturned|collision|accident"),
    "open": W("open|opened|ajar"),
    "cone": W("cone|cones|pylon|pylons|triangle|warning triangle"),
    "queue": W("traffic jam|jam|queue|queued|congestion|congested|heavy traffic|line of (?:cars|vehicles)|row of (?:cars|vehicles)"),
    "ahead": W("behind|in front of|in_front_of|ahead of|following"),
    "signal": W("traffic lights?|traffic signals?|signal lights?|stoplight|stop light|stop line|stop sign|crosswalk|zebra crossing|pedestrian crossing"),
    "parking": W("parking|car park"),
    "unusable": W("black screen|blank|blurry|blurred|out of focus|too dark|no image"),
}
OPENABLE = W("door|doors|trunk|hood|bonnet|tailgate|hatch")
VEHICLE_TYPE = [  # 박스 가운데 차량의 이름 → 4차 차종 (위에서부터 처음 맞는 것, 나머지 car·suv·sedan·taxi·van·jeep·vehicle 은 승용차)
    (W("tow truck|wrecker"), "tow_truck"),
    (CUE_WORDS["emergency"], "emergency_vehicle"),
    (W("excavator|bulldozer|forklift|tractor"), "work_vehicle"),
    (W("bus|buses"), "bus"),
    (W("truck|trucks|lorry|tanker|trailer|pickup"), "truck"),
    (W("motorcycle|motorcycles|motorbike|scooter|moped"), "motorcycle"),
    (W("bicycle|bicycles|bike"), "bicycle"),
]


# 사후 확인(11-09 — 결과를 본 뒤 만든 질문): form 항목 이름을 영어 문장으로 바꾸면 답이 그림을 따라 바뀌는가.
# 기록은 남기지 않고 화면에만 찍는다(--probe-nl, 3배 크롭만)
FORM_NL = {
    "image_usable": "is the image clear enough to see the object in the red box",
    "vehicle_in_box": "is there a vehicle inside the red box",
    "vehicle_type": "what type of vehicle is inside the red box",
    "emergency_vehicle_near": "is there a fire truck, ambulance, police car or tow truck in the image",
    "flashing_lights": "are there flashing emergency lights or hazard lights",
    "people_on_road": "are there people standing or walking on the road",
    "visible_damage": "is there visible damage or debris",
    "door_or_trunk_open": "is a door, trunk or hood of the vehicle in the red box open",
    "cones_or_triangle": "are there traffic cones or a warning triangle on the road",
    "vehicles_stopped_ahead": "are other vehicles queued right in front of the vehicle in the red box",
    "signal_or_stop_line": "is there a traffic light, stop line or crosswalk near the vehicle in the red box",
    "parking_area": "is the vehicle in the red box in a parking lot or parking space",
    "why_stopped": "why is the vehicle in the red box stopped",
}


def call(m, image, **opts):
    """describe / extract 한 번 — (결과, nan 을 바꾼 개수)"""
    try:
        return m.describe(image, **opts), 0
    except json.JSONDecodeError:
        fixed, n = fix_nan(m._buffer.value.decode("utf-8"))
        return json.loads(fixed), n


def strip_masks(d):
    """기록용 — 물체·구역의 윤곽 점(mask)은 크기만 크고 쓰지 않는다"""
    return {k: [{kk: vv for kk, vv in x.items() if kk != "mask"} if isinstance(x, dict) else x for x in v] if isinstance(v, list) else v
            for k, v in d.items()}


def from_form(res):
    """form 답 → 4차 답 꼴(parsed)·확률(probs: 예/아니오는 P(예), 고르기는 후보별 — run_decide.py 와 같은 꼴)·답 분포 전부"""
    f = res.get("form") or {}
    parsed, probs, full = {"description_ko": ""}, {}, {}
    for k in run_vlm.MIRAGE_FORM:
        a = f.get(k) or {}
        key = "direct_verdict" if k == "why_stopped" else k
        dist = {c: round(float(x), 4) for c, x in (a.get("probabilities") or {}).items() if isinstance(x, (int, float))}
        full[key] = dist
        v = a.get("value")
        parsed[key] = v if isinstance(v, str) else "unknown"   # 답하지 않음('-')이 가장 크면 value 가 null
        probs[key] = dist.get("yes", 0.0) if run_vlm.ENUMS4.get(k) is run_vlm.YN else dist
    return parsed, probs, full


def vehicle_type(name):
    return next((t for pat, t in VEHICLE_TYPE if pat.search(name)), "car")


def hits(pattern, strings):
    return sorted({m.group(0).lower() for s in strings for m in pattern.finditer(s)})


def name(x):
    """물체·구역 이름 — 밑줄로 이은 이름(fire_truck)도 낱말 표에 걸리게 띄어 쓴다. 이름이 없거나 null 이면 빈 글"""
    return str(x.get("name") or "").replace("_", " ")


def box_center(info, variant):
    """크롭 안에서 빨간 박스 가운데(0~1000) — make_cases.crop_around 와 같은 셈. 크롭은 프레임 밖으로 나가는 쪽만 잘려서
    가장자리 장면에서는 박스가 그림 가운데(500, 500)에 있지 않다"""
    factor, min_side = (2.0, 160) if variant == "D2" else (3.0, 256)   # crop2.jpg / crop.jpg
    (w, h), b = info["frame"], info["box"]
    cx, cy = (b[0] + b[2]) / 2, (b[1] + b[3]) / 2
    side = max(min_side, factor * max(b[2] - b[0], b[3] - b[1]))
    x1, y1 = int(max(0, cx - side / 2)), int(max(0, cy - side / 2))
    x2, y2 = int(min(w, cx + side / 2)), int(min(h, cy + side / 2))
    return 1000 * (cx - x1) / (x2 - x1), 1000 * (cy - y1) / (y2 - y1)


def cues_from_describe(d, center=(500, 500)):
    """describe 기록 → 4차 답(parsed)과 항목마다 무엇이 걸렸는지(evidence). 점수가 nan(null)인 물체는 뺀다.
    center 는 그림 안 빨간 박스의 가운데(box_center)"""
    objs = [o for o in d.get("objects") or [] if isinstance(o, dict) and isinstance(o.get("score"), (int, float))]
    regs = [r for r in d.get("regions") or [] if isinstance(r, dict)]
    names = [name(x) for x in objs + regs]
    attrs = [str(v) for x in objs + regs for v in (x.get("description") or {}).values()]
    scene = [str(v) for v in (d.get("scene") or {}).values()]
    events = [e if isinstance(e, str) else json.dumps(e, ensure_ascii=False) for e in d.get("events") or []]
    veh = [o for o in objs if CUE_WORDS["vehicle"].search(name(o))]
    veh_ids = {o.get("id") for o in veh}
    name_of = {o.get("id"): name(o) for o in objs}
    # 문·트렁크 열림은 차량과 문·트렁크·보닛 물체에서만 본다 — 도로 구역의 'open road' 같은 값이 사고 단서가 되지 않게
    openable = [o for o in objs if o in veh or OPENABLE.search(name(o))]
    ev = {
        "emergency_vehicle_near": hits(CUE_WORDS["emergency"], names),
        "flashing_lights": hits(CUE_WORDS["lights"], names + attrs),
        "people_on_road": hits(CUE_WORDS["person"], names),
        "visible_damage": hits(CUE_WORDS["damage"], names + attrs + events),
        "door_or_trunk_open": hits(CUE_WORDS["open"], [name(o) for o in openable]
                                   + [str(v) for o in openable for v in (o.get("description") or {}).values()]),
        "cones_or_triangle": hits(CUE_WORDS["cone"], names),
        # 앞에 선 차: 차량끼리의 앞뒤 관계(behind·in front of …) 또는 장면·구역·사건 글의 정체·줄
        "vehicles_stopped_ahead": [f'{name_of[r["subject"]]} {r["predicate"]} {name_of[r["object"]]}' for r in d.get("relations") or []
                                   if isinstance(r, dict) and r.get("subject") in veh_ids and r.get("object") in veh_ids
                                   and CUE_WORDS["ahead"].search(str(r.get("predicate", "")))]
                                  + hits(CUE_WORDS["queue"], names + scene + events),
        "signal_or_stop_line": hits(CUE_WORDS["signal"], names + scene),
        "parking_area": hits(CUE_WORDS["parking"], names + scene),
    }
    p = {k: "yes" if v else "no" for k, v in ev.items()}
    ev["image_usable"] = hits(CUE_WORDS["unusable"], scene + attrs + events)
    p["image_usable"] = "no" if ev["image_usable"] else "yes"
    # 박스 안 차량: 그림 가운데(빨간 박스의 가운데 — 크롭은 박스를 가운데 두고 자른다)를 덮는 차량 중 점수가 가장 높은 것
    # (상자 좌표가 nan(null)이거나 없는 물체는 여기서만 뺀다 — 이름 단서에는 그대로 센다)
    cx, cy = center
    boxed = [o for o in veh if isinstance(o.get("bbox"), list) and len(o["bbox"]) == 4
             and all(isinstance(v, (int, float)) for v in o["bbox"])]
    over = [o for o in boxed if o["bbox"][0] <= cx <= o["bbox"][2] and o["bbox"][1] <= cy <= o["bbox"][3]]
    top = max(over, key=lambda o: o["score"], default=None)
    p["vehicle_in_box"] = "yes" if top else "no"
    p["vehicle_type"] = vehicle_type(name(top)) if top else "none"
    ev["vehicle_in_box"] = [f'{name(top)} {top["score"]:.2f} {top["bbox"]}'] if top else []
    return p, ev


def summary(d):
    """설명 칸에 보일 영어 요약 — 장면 · 물체(이름별 개수·가장 높은 점수) · 읽은 글자 · 추론 글. Mirage 는 한국어 설명을 못 쓴다"""
    scene = ", ".join(f"{k}={v}" for k, v in (d.get("scene") or {}).items())
    counts = {}
    for o in d.get("objects") or []:
        s = o.get("score")
        n, best = counts.get(o.get("name", "?"), (0, 0.0))
        counts[o.get("name", "?")] = (n + 1, max(best, s if isinstance(s, (int, float)) else 0.0))
    objs = " · ".join(f"{k}{'×' + str(n) if n > 1 else ''} {s:.2f}" for k, (n, s) in sorted(counts.items(), key=lambda kv: -kv[1][1]))
    text = " / ".join(t.get("string", "") for t in d.get("text") or [] if isinstance(t, dict) and t.get("string"))
    reason = re.sub(r"^lang: \w+ \| ", "", d.get("reasoning") or "")
    return " | ".join([f"scene: {scene}", f"objects: {objs or '-'}"] + ([f"text: {text}"] if text else []) + [f"reasoning: {reason}"])[:800]


def probe_nl(m, scenes):
    """--probe-nl: 영어 문장 항목으로 3배 크롭을 한 번씩 — 장면마다 답(답하지 않음 '-')과 항목별 답 분포"""
    from collections import Counter
    form = {FORM_NL[k]: {"enum": v} for k, v in run_vlm.MIRAGE_FORM.items()}
    counts = {k: Counter() for k in run_vlm.MIRAGE_FORM}
    for cid, d, gt in scenes:
        res, _ = call(m, str(d / "crop.jpg"), form=form)
        ans = {k: (res.get("form", {}).get(FORM_NL[k]) or {}).get("value") or "-" for k in run_vlm.MIRAGE_FORM}
        for k, v in ans.items():
            counts[k][v] += 1
        print(f"{cid:4} 정답 {'/'.join(gt)} | " + " · ".join(f"{run_vlm.FIELD_KO.get(k, k)} {v}" for k, v in ans.items()), flush=True)
    for k, cnt in counts.items():
        print(f"  {k:24} {dict(cnt.most_common())}", flush=True)


def loaded_module(name):
    """이 프로세스에 실제로 올라온 DLL 경로(엔진 판 확인용 — 캐시 폴더 이름에 판이 들어 있다)"""
    import ctypes
    from ctypes import wintypes
    k = ctypes.WinDLL("kernel32", use_last_error=True)
    k.GetModuleHandleW.argtypes, k.GetModuleHandleW.restype = [wintypes.LPCWSTR], wintypes.HMODULE
    k.GetModuleFileNameW.argtypes = [wintypes.HMODULE, wintypes.LPWSTR, wintypes.DWORD]
    h = k.GetModuleHandleW(name)
    if not h:
        return None
    buf = ctypes.create_unicode_buffer(1024)
    k.GetModuleFileNameW(h, buf, 1024)
    return buf.value


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--cases", help="장면 id 를 쉼표로(없으면 4차 장면 모두)")
    ap.add_argument("--results", default="round4_quant", help="out/ 아래 결과 폴더 이름")
    ap.add_argument("--probe-nl", action="store_true", help="사후 확인 — 항목 이름을 영어 문장으로(FORM_NL), 화면에만 찍는다")
    a = ap.parse_args()
    sys.stdout.reconfigure(errors="replace")   # cp949 콘솔에서 '—' 같은 글자로 멈추지 않게
    cfgs = {c["mode"]: c for c in run_vlm.MIRAGE_POOL}
    spec = json.loads((HERE / "cases.json").read_text(encoding="utf-8"))
    cases, round3 = spec["cases"], spec.get("round3", {})
    if a.cases:
        cases = [c for c in cases if c["id"] in a.cases.split(",")]
    out_dir = HERE / "out" / a.results
    out_dir.mkdir(parents=True, exist_ok=True)
    paths = {mode: out_dir / f"{c['name']}.jsonl" for mode, c in cfgs.items()}
    done = {mode: run_vlm.done_pairs(path) for mode, path in paths.items()}

    from PIL import Image
    from cactus.agent.mirage import Mirage
    t0 = time.time()
    m = Mirage()
    w = Path(m.weights)
    meta = {"api": "mirage", "load_s": round(time.time() - t0, 2), "package": metadata.version("cactus-compute"),
            "engine": loaded_module("libcactus.dll"), "weights": str(w), "weights_mb": round(w.stat().st_size / 1e6, 1),
            "weights_md5": hashlib.md5(w.read_bytes()).hexdigest(), "cpu": platform.processor(), "cpu_count": os.cpu_count(),
            "telemetry_off": os.environ.get("CACTUS_TELEMETRY") == "0" and os.environ.get("DO_NOT_TRACK") == "1",
            "offline": os.environ.get("HF_HUB_OFFLINE") == "1"}
    # 데우기 — 첫 호출만 느릴 수 있다. 회색 그림으로 describe·form 한 번씩(장면 시간에 섞이지 않게)
    gray = Image.new("RGB", (512, 512), (128, 128, 128))
    t1 = time.time()
    call(m, gray)
    call(m, gray, form=FORM)
    meta["warmup_s"] = round(time.time() - t1, 2)
    print(f"== Mirage 올림 {meta['load_s']}초 · 가중치 {meta['weights_mb']} MB ({meta['weights_md5'][:8]}) · 데우기 {meta['warmup_s']}초 · "
          f"엔진 {meta['engine']}", flush=True)
    if a.probe_nl:
        scenes = []
        for c in cases:
            d = HERE / "out" / "cases" / c["id"]
            if run_vlm.stopped_scene(c["id"], json.loads((d / "case.json").read_text(encoding="utf-8")), round3):
                scenes.append((c["id"], d, round3[c["id"]]))
        probe_nl(m, scenes)
        return

    # 앞 실행의 메타 — 이번에 새로 부른 그림이 없는 설정은 그대로 둔다(다시 돌려 보고서만 만들 때 올린 시간·결정성 기록이
    # 이번 값으로 덮이지 않게)
    meta_paths = {mode: out_dir / f"{cfg['name']}.meta.json" for mode, cfg in cfgs.items()}
    prev = {mode: json.loads(p.read_text(encoding="utf-8")) for mode, p in meta_paths.items() if p.exists()}

    def write_meta(modes):
        for mode in modes:
            keep = {"deterministic": prev[mode]["deterministic"]} if "deterministic" in prev.get(mode, {}) else {}
            meta_paths[mode].write_text(json.dumps({"config": cfgs[mode], **keep, **meta}, ensure_ascii=False, indent=1), encoding="utf-8")
    write_meta([mode for mode in cfgs if mode not in prev])   # 도중에 멈춰도 보고서가 메타를 읽게 먼저 한 번

    first = {}   # 설정마다 이번에 처음 부른 그림 — 끝에 한 번 더 불러 결과가 같은지(결정적인지) 본다
    files = {mode: path.open("a", encoding="utf-8") for mode, path in paths.items()}
    try:
        for c in cases:
            d = HERE / "out" / "cases" / c["id"]
            info = json.loads((d / "case.json").read_text(encoding="utf-8"))
            if not run_vlm.stopped_scene(c["id"], info, round3):
                continue   # 4차와 같은 장면 — 규칙이 고를 '멈춘 차'만 (run_vlm.run_config 와 같은 조건)
            for v in run_vlm.VARIANTS:
                for mode, cfg in cfgs.items():
                    if v not in cfg["variants"] or (c["id"], v) in done[mode]:
                        continue
                    q = run_vlm.question(cfg, info, v)
                    img = str(d / f"{q['images'][0]}.jpg")
                    opts = {"form": FORM} if mode == "form" else {}
                    rec = {"config": cfg["name"], "case": c["id"], "variant": v, "error": None}
                    t2, c2 = time.time(), time.process_time()
                    try:
                        res, fixed = call(m, img, **opts)
                        wall, cpu = round(time.time() - t2, 3), round(time.process_time() - c2, 3)
                        content = json.dumps(strip_masks(res), ensure_ascii=False)
                        if mode == "form":
                            parsed, probs, full = from_form(res)
                            extra = {"probs": probs, "form_probs": full}
                        else:
                            parsed, ev = cues_from_describe(res, box_center(info, v))
                            parsed["description_ko"] = summary(res)
                            extra = {"evidence": ev}
                        rec.update(wall_s=wall, cpu_s=cpu, done_reason=mode, total_s=wall, load_s=0, thinking_chars=0, nan_fixed=fixed,
                                   content=content, parsed=parsed, **extra, invalid=run_vlm.check(parsed, run_vlm.ENUMS4, q["texts"]))
                        if not rec["invalid"]:
                            parsed["verdict"] = run_vlm.decide(parsed)   # 4차와 같은 규칙 — 채점은 3차 정답으로
                        first.setdefault(mode, (img, opts, content))
                    except Exception as e:   # 한 장면이 실패해도 나머지는 돈다 — 사유를 남긴다
                        rec.update(wall_s=round(time.time() - t2, 3), error=f"{type(e).__name__}: {e}"[:400])
                    files[mode].write(json.dumps(rec, ensure_ascii=False) + "\n")
                    files[mode].flush()
                    p = rec.get("parsed") or {}
                    print(f"{cfg['name']:19} {c['id']:4} {v}: {p.get('verdict', '-'):22} {rec['wall_s']:5.2f}초"
                          + (f" CPU {rec['cpu_s']:.2f}초" if rec.get("cpu_s") is not None else "")
                          + (f" · 판정 질문 {p.get('direct_verdict')}" if mode == "form" and p else "")
                          + (f" · nan {rec['nan_fixed']}개 바꿈" if rec.get("nan_fixed") else "")
                          + (f" 오류 {rec['error']}" if rec["error"] else ""), flush=True)
                    if p:
                        print(f"    {run_vlm.cue_text(p)} · 박스 안 {p['vehicle_in_box']} · 차종 {p['vehicle_type']} · 선명 {p['image_usable']}",
                              flush=True)
    finally:
        for f in files.values():
            f.close()
    same = {}
    for mode, (img, opts, content) in first.items():
        try:
            res, _ = call(m, img, **opts)
            same[mode] = json.dumps(strip_masks(res), ensure_ascii=False) == content
        except Exception as e:   # 확인이 실패해도 메타·보고서는 남긴다 — 사유를 값 자리에
            same[mode] = f"{type(e).__name__}: {e}"[:200]
    if same:
        meta["deterministic"] = same
        print(f"같은 그림을 다시 부른 결과가 같은가: {same}", flush=True)
    write_meta(first)
    try:   # 장면별 보고서 — 실패해도 결과(jsonl)는 이미 남았다
        import report
        print(f"보고서: {report.build(out_dir)}", flush=True)
    except Exception as e:
        print(f"보고서를 못 만듦: {e!r} (report.py 로 다시)", flush=True)


if __name__ == "__main__":
    main()
