"""run_vlm.py 결과를 장면마다 자세히 보는 HTML 보고서 (2026-11-08).

점수표(score.py)만으로는 모델이 무엇을 보고 뭐라고 했는지 안 보인다(11-08 사용자). 장면마다:
- 모델이 본 그림(입력 방식마다)과 참고용 화면 전체, 장면 정보(영상·시각·후보 라벨·박스 크기·멈춘 시간)
- 정답(Claude 초안 — cases.json 의 gt_unsure 는 사용자 확인을 부탁한 장면으로 따로 표시)
- 설정 × 입력 방식마다 판정과 맞고 틀림, 설명 원문, 항목별 답(4차는 앱과 같은 이름의 단서 칩),
  4차 규칙이 그 판정을 낸 까닭과 사후 규칙 둘(README 결과 5), 시간·토큰, 질문(run_vlm.question 그대로)·응답 원문
맨 위엔 설정별 요약(채점은 score.py 그대로)과 장면 × 설정 판정표. 거르기(틀린 장면만·설정 하나만·그림 크게)는 자바스크립트 없이 CSS 로.

    uv run --no-project --python 3.12 --with pillow experiments/VlmCheck/report.py [결과 폴더 이름...]

→ out/<결과 폴더>/report.html. 폴더를 안 주면 out/ 아래 결과 폴더 모두와 목차 out/index.html. run_vlm.py 도 끝에 부른다.
그림을 파일 안에 넣는다(파일 하나로 열리게) — 실제 CCTV 그림이니 out/(gitignore) 밖이나 저장소에 두지 않는다.
pillow 가 있으면 화면 전체·2초 전·뒤 그림만 표시용으로 줄여 넣는다(없으면 원본 그대로 — 파일이 커질 뿐). 모델이 본 키운 그림은 늘 원본 그대로.
"""
import base64
import html
import io
import json
import re
import statistics
import sys
import time
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
import run_vlm  # noqa: E402  질문(question)·4차 단서 묶음·항목 이름을 그대로 쓴다
import score    # noqa: E402  채점은 score.py 와 같게

try:
    from PIL import Image
except ImportError:   # 없으면 그림을 원본 그대로 넣는다
    Image = None

E = html.escape
SHOW_W = 720   # 화면 전체·2초 전·뒤 그림을 표시용으로 줄일 너비
FIELD_KO = run_vlm.FIELD_KO
VERDICT_KO = score.SHORT
VALUE_KO = {
    "yes": "예", "no": "아니오", "unknown": "모름", "none": "없음",
    "car": "승용차", "truck": "트럭", "bus": "버스", "motorcycle": "오토바이", "bicycle": "자전거", "person": "사람",
    "emergency_vehicle": "출동 차량", "tow_truck": "견인차", "work_vehicle": "작업 차량",
    "stopped": "멈춤", "moving": "움직임", "travel_lane": "차로", "shoulder": "갓길", "median_side": "중앙분리대 쪽",
    "intersection": "교차로", "sidewalk_or_plaza": "보도·광장", "parking": "주차장", "few": "1~3명", "many": "4명 이상",
    "light": "약간", "heavy": "심함",
}
TITLES = {"results": "1차 — 모델 5가지 × 입력 A·B·C", "probe": "1차 뒤 확인 — 설명 먼저 + 키운 그림만",
          "round2": "2차 — 장면 30개, 설명 먼저 + 키운 그림만", "round3": "3차 — 추적기 사실을 주고 '왜 멈췄나'만",
          "round4": "4차 — 관찰만 묻고 판정은 코드 규칙",
          "round4_quant": "4차 장면 모델 비교 — qwen3.5 4B(4·8비트)·9B·qwen3.6 35B-A3B MoE·Claude Opus 5.5·Kanana 판정 모델·Mirage(22 MB)",
          "round4_decide_ref": "Kanana 판정 모델 8비트 확인 — 같은 장면을 CPU BF16 으로"}
VARIANT_KO = {"A": "화면 전체", "B": "화면 전체 + 키운 그림", "C": "B + 2초 전·뒤 화면", "D": "키운 그림만(박스의 3배)",
              "D3": "키운 그림만(박스의 3배)", "D2": "키운 그림만(박스의 2배)"}
IMAGE_KO = {"full": "화면 전체", "crop": "키운 그림 — 박스의 3배", "crop2": "키운 그림 — 박스의 2배",
            "prev": "2초 전 화면", "next": "2초 뒤 화면"}
IMAGE_ORDER = ["crop", "crop2", "full", "prev", "next"]
GROUPS = {1: ["유고", "정상", "사고 화면 속 정상", "함정", "가짜 검출"],
          3: ["출동 차량 보임", "혼자 멈춘 차", "정상 정차", "화면 불량", "차량 아님"]}
FIELDS = {1: ["object_type", "object_state", "location", "people_on_road", "emergency_vehicles", "cones_or_signs",
              "damage_visible", "smoke_or_fire", "congestion"],
          3: ["object_type", "emergency_vehicles", "people_on_road", "damage_visible", "queue_or_signal", "parking_area"],
          4: ["image_usable", "vehicle_in_box", "vehicle_type", *run_vlm.INCIDENT_CUES, *run_vlm.NORMAL_CUES, "cones_or_triangle"]}
INC = set(run_vlm.INCIDENT_CUES) | {"emergency_vehicles", "damage_visible", "smoke_or_fire"}
CTX = set(run_vlm.NORMAL_CUES) | {"queue_or_signal"}
CATEGORICAL = {"object_type", "vehicle_type", "object_state", "location", "congestion"}
SCORED = {"object_type": "object", "vehicle_type": "object", "people_on_road": "people",
          "emergency_vehicles": "emergency", "emergency_vehicle_near": "emergency"}
ABBR = {"emergency_vehicle_near": "출동", "flashing_lights": "경광", "people_on_road": "사람", "visible_damage": "손상",
        "door_or_trunk_open": "열림", "vehicles_stopped_ahead": "앞차", "signal_or_stop_line": "신호", "parking_area": "주차",
        "cones_or_triangle": "콘"}
RULES4 = [("미리 정한 규칙", ("usable", "vehicle", "incident", "normal")),        # = run_vlm.decide
          ("사후 1 · 단서 먼저", ("vehicle", "incident", "normal", "usable")),     # 결과를 보고 고른 규칙(README 결과 5)
          ("사후 2 · 선명도 안 봄", ("vehicle", "incident", "normal"))]
ALL_CONFIGS = {c["name"]: c for pool in (run_vlm.CONFIGS, run_vlm.PROBES, run_vlm.ROUND2, run_vlm.ROUND3, run_vlm.ROUND4,
                                          run_vlm.QUANT, run_vlm.STRATA_POOL, run_vlm.CLAUDE_POOL, run_vlm.DECIDE_POOL,
                                          run_vlm.MIRAGE_POOL) for c in pool}


def ko(v):
    return VALUE_KO.get(v, "—" if v is None else str(v))


def vko(v):
    return VERDICT_KO.get(v, "—" if v is None else str(v))


def kind_of(cfg):
    """질문 종류 — 1(1·2차: 판정을 묻는다) · 3(3차: 왜 멈췄나) · 4(4차: 관찰만, 판정은 규칙)"""
    return 4 if cfg.get("r4") else 3 if cfg.get("r3") else 1


def short_name(name):
    return re.sub(r"^r\d-", "", name)


def cfg_tags(cfg):
    tags = [t for k, t in (("think", "생각 모드"), ("desc_first", "설명 먼저"), ("no_label", "라벨 없이")) if cfg.get(k)]
    api = {"openai": "Strata 엔진", "anthropic": "Claude API", "decide": "판정 모델", "mirage": "Mirage · CPU"}.get(cfg.get("api"))
    return tags + ([api] if api else [])


def frac(xs):
    return f"{sum(xs)}/{len(xs)}" if xs else "–"


def rule4(p, steps):
    """4차 판정 규칙을 한 단계씩 따라가 (판정, 까닭)을 돌려준다"""
    for step in steps:
        if step == "usable" and p["image_usable"] == "no":
            return "unusable_image", "선명하지 않다고 답함"
        if step == "vehicle" and p["vehicle_in_box"] == "no":
            return "not_vehicle", "박스 안에 차량이 없다고 답함"
        if step == "incident" and (hit := [k for k in run_vlm.INCIDENT_CUES if p[k] == "yes"]):
            return "accident_or_breakdown", "사고 단서 " + "·".join(FIELD_KO[k] for k in hit)
        if step == "normal" and (hit := [k for k in run_vlm.NORMAL_CUES if p[k] == "yes"]):
            return "normal_stop", "정상 맥락 " + "·".join(FIELD_KO[k] for k in hit)
    return "accident_or_breakdown", "단서 없음 — 규칙 후보를 그대로 둠"


def jpeg_size(b):
    """JPEG 머리에서 (너비, 높이) — pillow 없이도 크기를 적으려고"""
    i = 2
    while i + 9 < len(b):
        if b[i] != 0xFF:
            i += 1
            continue
        m = b[i + 1]
        if m in (0xC0, 0xC1, 0xC2):
            return int.from_bytes(b[i + 7:i + 9], "big"), int.from_bytes(b[i + 5:i + 7], "big")
        if m == 0xFF:
            i += 1
        elif m in (0x01, 0xD8) or 0xD0 <= m <= 0xD7:
            i += 2
        else:
            i += 2 + int.from_bytes(b[i + 2:i + 4], "big")
    return None


def embed(path, shrink):
    """그림 → (data URI, 원본 (너비, 높이), 줄였나)"""
    b = path.read_bytes()
    size = jpeg_size(b)
    shrunk = False
    if shrink and Image is not None and size and size[0] > SHOW_W:
        im = Image.open(io.BytesIO(b))
        im.thumbnail((SHOW_W, SHOW_W * 4))
        buf = io.BytesIO()
        im.convert("RGB").save(buf, "JPEG", quality=82)
        b, shrunk = buf.getvalue(), True
    return "data:image/jpeg;base64," + base64.b64encode(b).decode("ascii"), size, shrunk


def load(res_dir):
    """결과 폴더 → (성공한 호출, 오류만 남은 호출, 메타). 같은 호출을 다시 돌렸으면 마지막 것(score.py 와 같다)"""
    recs, errs, metas = {}, {}, {}
    for f in sorted(res_dir.glob("*.jsonl")):
        for line in f.read_text(encoding="utf-8").splitlines():
            if line.strip():
                r = json.loads(line)
                (recs if r.get("error") is None else errs)[(r["config"], r["variant"], r["case"])] = r
    errs = {k: r for k, r in errs.items() if k not in recs}
    for m in res_dir.glob("*.meta.json"):
        d = json.loads(m.read_text(encoding="utf-8"))
        metas[d["config"]["name"]] = d
    return recs, errs, metas


def gt_text(verdict):
    """정답 판정 목록(1차 함정은 입력 방식마다 다르다) → 글"""
    if isinstance(verdict, dict):
        parts = {}
        for v, lst in verdict.items():
            parts.setdefault("·".join(vko(x) for x in lst), []).append(v)
        return " / ".join(f"{','.join(vs)}: {t}" for t, vs in parts.items())
    return "·".join(vko(x) for x in verdict)


def gt_item(gt, what):
    if what == "object":
        return "·".join(ko(x) for x in gt["object_type"]) if gt.get("object_type") else None
    v = gt.get(what)
    return None if v is None else ("있음" if v else "없음")


def chip(k, v, miss=None):
    """항목 답 하나 — 색은 단서 묶음(사고 단서 빨강 · 정상 맥락 파랑 · 그 밖 회색), 모양은 예(채움)·아니오(흐림)·모름(점선)"""
    grp = "inc" if k in INC else "ctx" if k in CTX else "info"
    if k in ("image_usable", "vehicle_in_box"):
        st = "flag" if v == "no" else "unk" if v == "unknown" else "plain"
    elif v == "unknown":
        st = "unk"
    elif k in CATEGORICAL:
        st = "cat"
    elif v in ("yes", "few", "many"):
        st = "pos"
    else:
        st = "neg"
    title = f"{k} = {v}" + (f" · 정답: {miss}" if miss else "")
    return (f'<span class="chip {grp} {st}{" miss" if miss else ""}" title="{E(title)}">{E(FIELD_KO.get(k, k))} '
            f'<b>{E(ko(v))}</b>{" ✗" if miss else ""}</span>')


def cue_abbr(p):
    """판정표 칸에 넣을 4차 단서 줄임말 — '흐림'·'차 없음'은 판정을 가르는 아니오"""
    flags = [t for k, t in (("image_usable", "흐림"), ("vehicle_in_box", "차 없음")) if p.get(k) == "no"]
    yes = [ABBR[k] for k in ABBR if p.get(k) == "yes"]
    return " · ".join(x for x in ("·".join(flags), "·".join(yes)) if x)


CSS = """
:root{--bg:#f5f6f8;--card:#fff;--ink:#1c2230;--sub:#5c6576;--line:#dde1e8;--soft:#eef1f5;
--ok:#1d7a4c;--ok-bg:#e2f3e9;--bad:#b42318;--bad-bg:#fde7e4;--na:#7d8696;--na-bg:#eceef2;
--inc:#b42318;--inc-bg:#fdeae7;--ctx:#1c5ea8;--ctx-bg:#e5eefa;--info:#4a5363;--info-bg:#eceef2;
--warn:#8a5800;--warn-bg:#fff2d1;--accent:#2b67ad}
@media (prefers-color-scheme:dark){:root{--bg:#111419;--card:#181c23;--ink:#e5e8ee;--sub:#a0a8b7;--line:#2b313b;--soft:#20262f;
--ok:#5cc48d;--ok-bg:#15332a;--bad:#ff8b7f;--bad-bg:#3b1d1b;--na:#8b94a4;--na-bg:#252a32;
--inc:#ff8b7f;--inc-bg:#3a1f1d;--ctx:#80b3ff;--ctx-bg:#1a2a40;--info:#b5bcc9;--info-bg:#252a32;
--warn:#f0c062;--warn-bg:#382d14;--accent:#80b3ff}}
*{box-sizing:border-box}
body{margin:0;background:var(--bg);color:var(--ink);font:14px/1.55 "Malgun Gothic","Apple SD Gothic Neo","Noto Sans KR","Segoe UI",system-ui,sans-serif}
main{max-width:1240px;margin:0 auto;padding:20px 16px 60px}
a{color:var(--accent);text-decoration:none}a:hover{text-decoration:underline}
h1{font-size:22px;margin:0 0 6px}h2{font-size:17px;margin:34px 0 10px}h3{font-size:15px;margin:0}
.sub{color:var(--sub)}.small{font-size:12.5px}
.note{background:var(--card);border:1px solid var(--line);border-radius:10px;padding:10px 14px;margin:12px 0}
.note p{margin:4px 0}
.bar{position:sticky;top:0;z-index:5;background:var(--bg);border-bottom:1px solid var(--line);padding:8px 0;margin:14px 0 0;
display:flex;flex-wrap:wrap;gap:6px 14px;align-items:center;font-size:13px}
.bar label{display:inline-flex;gap:5px;align-items:center;cursor:pointer;white-space:nowrap}
.bar .grp{display:inline-flex;flex-wrap:wrap;gap:4px 10px;align-items:center}
.scroll{overflow-x:auto;border:1px solid var(--line);border-radius:10px;background:var(--card)}
table{border-collapse:collapse;width:100%;font-size:13px}
th,td{padding:6px 8px;border-bottom:1px solid var(--line);text-align:left;vertical-align:top;white-space:nowrap}
th{background:var(--soft);font-weight:600;position:sticky;top:0}
td.num{text-align:right;font-variant-numeric:tabular-nums}
tr:last-child td{border-bottom:0}
td.best{font-weight:700;color:var(--ok)}
.mx td.c{font-size:12.5px;min-width:86px}
.mx td.c a{color:inherit;display:block}
.mx td.c small{display:block;color:var(--sub);font-size:11.5px;white-space:normal;max-width:130px}
.mx td.ok{background:var(--ok-bg);color:var(--ok)}.mx td.bad{background:var(--bad-bg);color:var(--bad)}.mx td.na{background:var(--na-bg);color:var(--na)}
.mx td.scene,.mx th:first-child{position:sticky;left:0;z-index:1;background:var(--card);white-space:normal;min-width:190px;max-width:230px}
.mx th:first-child{background:var(--soft)}
.mx td.gt{white-space:normal;min-width:70px;max-width:110px}
.mx tr.gh td{background:var(--soft);font-weight:600;color:var(--sub)}
.scene-card{background:var(--card);border:1px solid var(--line);border-radius:12px;margin:14px 0;padding:14px 16px;scroll-margin-top:52px}
.scene-card header{margin-bottom:10px}
.sid{display:inline-block;min-width:34px;font-weight:700;color:var(--accent)}
.tags{display:flex;flex-wrap:wrap;gap:6px;margin:6px 0}
.tag{display:inline-block;white-space:nowrap;font-size:12px;padding:1px 8px;border-radius:999px;background:var(--soft);border:1px solid var(--line)}
.tag.gt{background:var(--ok-bg);border-color:transparent;color:var(--ok);font-weight:600}
.tag.warn{background:var(--warn-bg);border-color:transparent;color:var(--warn);font-weight:600}
.tag.long{white-space:normal;border-radius:10px}
.meta{margin:2px 0;color:var(--sub);font-size:12.5px}
.unsure{margin:6px 0 0;padding:6px 10px;border-radius:8px;background:var(--warn-bg);color:var(--warn);font-size:13px}
.body{display:grid;grid-template-columns:minmax(240px,340px) 1fr;gap:16px;align-items:start}
@media (max-width:820px){.body{grid-template-columns:1fr}}
.pics{display:grid;gap:10px}
.pics figure{margin:0}
.pics img{display:block;width:100%;height:auto;border-radius:8px;border:1px solid var(--line);background:#000}
.pics figcaption{font-size:12px;color:var(--sub);margin-top:3px}
.pics figure.ctx img{opacity:.92}
.answers{display:grid;gap:10px;min-width:0}
.ans{border:1px solid var(--line);border-left:4px solid var(--na);border-radius:10px;padding:8px 12px}
.ans.ok{border-left-color:var(--ok)}.ans.bad{border-left-color:var(--bad)}
.ans-head{display:flex;flex-wrap:wrap;gap:4px 10px;align-items:baseline}
.ans-head .var{font-size:12px;padding:0 6px;border-radius:5px;background:var(--soft);border:1px solid var(--line)}
.ans-head .time{margin-left:auto;font-size:12px;color:var(--sub)}
.v{display:inline-block;padding:0 8px;border-radius:6px;font-weight:600;font-size:13px}
.v.ok{background:var(--ok-bg);color:var(--ok)}.v.bad{background:var(--bad-bg);color:var(--bad)}.v.na{background:var(--na-bg);color:var(--na)}
.why{margin:4px 0 0;font-size:12.5px;color:var(--sub)}
blockquote{margin:6px 0;padding:4px 10px;border-left:3px solid var(--line);font-size:13.5px}
.evidence{margin:4px 0;font-size:13px}
.chips{display:flex;flex-wrap:wrap;gap:4px;margin:6px 0 2px}
.chip{display:inline-flex;gap:4px;align-items:baseline;padding:1px 8px;border-radius:999px;font-size:12px;
border:1px solid var(--line);background:var(--card);color:var(--sub);white-space:nowrap}
.chip b{font-weight:600}
.chip.pos.inc{background:var(--inc-bg);border-color:var(--inc);color:var(--inc)}
.chip.pos.ctx{background:var(--ctx-bg);border-color:var(--ctx);color:var(--ctx)}
.chip.pos.info{background:var(--info-bg);border-color:var(--info);color:var(--ink)}
.chip.neg{opacity:.5}.chip.unk{border-style:dashed;opacity:.75}.chip.cat,.chip.plain{color:var(--ink)}
.chip.flag{background:var(--warn-bg);border-color:var(--warn);color:var(--warn)}
.chip.miss{outline:2px solid var(--bad);outline-offset:1px}
.rules{display:flex;flex-wrap:wrap;gap:4px 14px;margin:4px 0 0;font-size:12.5px;color:var(--sub)}
.rules b.ok{color:var(--ok)}.rules b.bad{color:var(--bad)}
details{margin-top:6px;font-size:12.5px}
summary{cursor:pointer;color:var(--sub)}
pre{white-space:pre-wrap;word-break:break-all;background:var(--soft);border-radius:8px;padding:8px 10px;margin:6px 0;
font:12px/1.5 Consolas,"D2Coding","Malgun Gothic",monospace}
.legend{display:flex;flex-wrap:wrap;gap:6px;align-items:center;margin:6px 0}
.err{color:var(--bad)}
body:has(#f-big:checked) .body{grid-template-columns:1fr}
body:has(#f-big:checked) .pics{grid-template-columns:repeat(auto-fill,minmax(300px,512px))}
"""


def build(res_dir):
    return _build(Path(res_dir))[0]


def _build(res_dir):
    spec = json.loads((HERE / "cases.json").read_text(encoding="utf-8"))
    cases = {c["id"]: c for c in spec["cases"]}
    round3, unsure = spec.get("round3", {}), spec.get("gt_unsure", {})
    recs, errs, metas = load(res_dir)
    order = list(ALL_CONFIGS)
    keys = sorted({k[:2] for k in [*recs, *errs]},
                  key=lambda k: (order.index(k[0]) if k[0] in order else 99, k[0], list(run_vlm.VARIANTS).index(k[1])))
    cfgs = {n: (metas.get(n) or {}).get("config") or ALL_CONFIGS.get(n) or {"name": n} for n, _ in keys}
    kinds = {kind_of(cfgs[n]) for n, _ in keys}
    if len(kinds) != 1:
        raise SystemExit(f"{res_dir.name}: 한 폴더에 질문 종류가 섞였다 {sorted(kinds)}")
    kind = kinds.pop()
    confirm = "accident_or_breakdown" if kind >= 3 else "incident"
    groups = GROUPS[3 if kind >= 3 else 1]

    # 장면마다 정답·묶음, (장면, 설정)마다 답·채점
    gts, group, infos = {}, {}, {}
    for cid in {k[2] for k in [*recs, *errs]}:
        if cid not in cases or (kind >= 3 and cid not in round3):
            continue
        c = cases[cid]
        gts[cid] = dict(c["gt"], verdict=round3[cid]) if kind >= 3 else c["gt"]
        if kind >= 3:
            v0 = round3[cid][0]
            group[cid] = ("출동 차량 보임" if c["gt"].get("emergency") else "혼자 멈춘 차") if v0 == confirm else vko(v0)
        else:
            group[cid] = score.GROUP[cid[0]]
        infos[cid] = json.loads((HERE / "out" / "cases" / cid / "case.json").read_text(encoding="utf-8"))
    scene_ids = sorted(gts, key=lambda c: (groups.index(group[c]) if group[c] in groups else 99, c[0], int(re.sub(r"\D", "", c) or 0)))

    ans = {}
    for cid in scene_ids:
        for key in keys:
            rec, err = recs.get((*key, cid)), errs.get((*key, cid))
            if rec is None and err is None:
                continue
            a = {"key": key, "rec": rec, "err": err, "q": run_vlm.question(cfgs[key[0]], infos[cid], key[1]),
                 "gtv": score.gt_verdicts(gts[cid], key[1]), "p": None}
            if rec is not None:
                a["s"] = s = score.score(rec, gts[cid], confirm)
                a["p"] = p = rec.get("parsed") if s["valid"] else None
                if kind == 4 and p:
                    a["rules"] = []
                    for title, steps in RULES4:
                        verdict, why = rule4(p, steps)
                        a["rules"].append({"title": title, "verdict": verdict, "why": why, "ok": verdict in a["gtv"],
                                           "conf": (verdict == confirm) == (confirm in a["gtv"])})
                    if a["rules"][0]["verdict"] != p.get("verdict"):   # 저장된 판정(run_vlm.decide)과 같아야 한다
                        raise SystemExit(f"{res_dir.name} {key} {cid}: 규칙 판정이 저장된 것과 다르다")
            ans[(cid, key)] = a

    def wrong(a):
        return a["rec"] is None or not a["s"]["verdict"]

    # 설정별 요약(score.py 와 같은 채점)
    stats = []
    for key in keys:
        rows = [(cid, ans[(cid, key)]) for cid in scene_ids if (cid, key) in ans]
        done = [(cid, a) for cid, a in rows if a["rec"] is not None]
        st = {"n": len(done), "err": len(rows) - len(done),
              "ok": [a["s"]["verdict"] for _, a in done],
              "conf": [a["s"]["confirm"] for _, a in done if "confirm" in a["s"]],
              "groups": {g: [a["s"]["verdict"] for cid, a in done if group[cid] == g] for g in groups},
              "items": {w: [a["s"][w] for _, a in done if w in a["s"]] for w in ("object", "people", "emergency")},
              "smoke": sum(a["s"]["smoke"] for _, a in done),
              "invalid": sum(not a["s"]["valid"] for _, a in done),
              "wall": [a["rec"]["wall_s"] for _, a in done],
              "out_tok": [a["rec"]["out_tokens"] for _, a in done if a["rec"].get("out_tokens") is not None],
              "think": [a["rec"].get("thinking_chars") or 0 for _, a in done],
              "cost": sum(a["rec"].get("cost_usd") or 0 for _, a in done)}   # Claude API 만 (달러)
        if kind == 4:
            st["rules"] = [([a["rules"][i]["ok"] if a.get("rules") else False for _, a in done],
                            [a["rules"][i]["conf"] if a.get("rules") else confirm not in a["gtv"] for _, a in done])
                           for i in range(len(RULES4))]
            st["usable_no"] = sum(a["p"]["image_usable"] == "no" for _, a in done if a["p"])
        stats.append(st)
    best = max((sum(st["ok"]) for st in stats), default=0)

    folder = res_dir.name
    base = re.sub(r"_smoke$", "", folder)
    title = TITLES.get(base, folder) + (" (시험 실행)" if folder != base and folder in ("round3_smoke", "round4_smoke") else "")
    if folder == "smoke":
        title = "1차 전 시험 실행"
    models = sorted({cfgs[n].get("model", "?") for n, _ in keys})
    out = [f'<!doctype html><html lang="ko"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">'
           f'<meta name="color-scheme" content="light dark"><title>VLM {E(title.split(" — ")[0])} 결과</title><style>{CSS}']
    # 설정 하나만 보기·틀린 장면만 — 자바스크립트 없이(:has)
    filt = ["body:has(#f-wrong:checked):has(#k-all:checked) .scene-card.allright,"
            "body:has(#f-wrong:checked):has(#k-all:checked) tr.srow.allright{display:none}"]
    for i in range(len(keys)):
        filt.append(f"body:has(#k-{i}:checked) .ans:not(.k{i}),body:has(#k-{i}:checked) .mx .kc:not(.k{i}){{display:none}}"
                    f"body:has(#k-{i}:checked) .sum tr.k{i} td{{background:var(--soft)}}"
                    f"body:has(#f-wrong:checked):has(#k-{i}:checked) .scene-card:not(.wrong-k{i}),"
                    f"body:has(#f-wrong:checked):has(#k-{i}:checked) tr.srow:not(.wrong-k{i}){{display:none}}")
    out.append("\n".join(filt) + "</style></head><body><main>")

    # 머리말
    opts = run_vlm.OPTIONS
    num_predict = sorted({cfgs[n].get("num_predict", 768) for n, _ in keys})
    out.append(f'<h1>VLM 실험 {E(title)}</h1>'
               f'<p class="sub">결과 폴더 <code>out/{E(folder)}</code> · 장면 {len(scene_ids)}개 · 설정 {len(keys)}가지(모델 {len(models)}: {E(", ".join(models))}) · '
               f'temperature {opts["temperature"]} · seed {opts["seed"]} · num_ctx {opts["num_ctx"]} · 출력 한도 {"/".join(map(str, num_predict))} 토큰 · '
               f'만든 때 {time.strftime("%Y-%m-%d %H:%M")}</p>')
    notes = ["정답은 Claude 가 화면을 보고 쓴 <b>초안</b>이다(사용자 확인 전). <span class=\"tag warn\">정답 확인 필요</span> 는 따로 여쭌 장면.",
             "채점은 <code>score.py</code> 와 같다. '앱 기준'은 VLM 이 유고를 확인했는지만 본 것(정답이 섞인 장면은 뺌)."]
    if kind == 4:
        notes.append("4차 판정은 모델이 아니라 <b>미리 정한 규칙</b>(<code>run_vlm.decide</code>)이 모델의 관찰 답으로 낸 것이다: "
                     "선명하지 않음 → 화면 불량 · 박스에 차 없음 → 차량 아님 · 사고 단서(출동 차량·경광등·차로 위 사람·손상·문·트렁크 열림) 하나라도 → 사고·고장 · "
                     "정상 맥락(앞에 선 차·신호·정지선·주차 구역) → 정상 정차 · 아무 단서도 없음 → 사고·고장(규칙 후보 그대로). "
                     "사후 1·2 는 결과를 보고 고른 규칙이라 과적합일 수 있다.")
    elif kind == 3:
        notes.append("3차는 추적기 사실(몇 초째 멈춤·차로 구역)을 글로 주고 '왜 멈췄나'만 물었다 — 판정은 모델 답.")
    else:
        notes.append("1·2차는 모델에게 판정까지 물었다 — 판정은 모델 답. 항목 칩의 빨간 테두리(✗)는 정답 항목과 다른 답.")
    out.append('<div class="note">' + "".join(f"<p>{n}</p>" for n in notes) + "</div>")
    out.append('<div class="legend small"><span class="sub">칩:</span>' + chip("emergency_vehicle_near", "yes") + chip("vehicles_stopped_ahead", "yes")
               + chip("cones_or_triangle", "yes") + chip("people_on_road", "no") + chip("visible_damage", "unknown") + chip("image_usable", "no")
               + '<span class="sub">— 빨강 사고 단서 · 파랑 정상 맥락 · 회색 그 밖 / 채움 예 · 흐림 아니오 · 점선 모름 · 노랑 판정을 가르는 아니오</span></div>')

    # 거르기 막대
    radios = ['<label><input type="radio" name="k" id="k-all" checked> 모두</label>'] + [
        f'<label><input type="radio" name="k" id="k-{i}"> {E(short_name(n))} {E(v)}</label>' for i, (n, v) in enumerate(keys)]
    out.append('<div class="bar"><a href="#sum">요약</a><a href="#mx">판정표</a><a href="#scenes">장면</a><a href="#ask">질문</a>'
               '<label><input type="checkbox" id="f-wrong"> 틀린 장면만</label><label><input type="checkbox" id="f-big"> 그림 크게</label>'
               f'<span class="grp"><span class="sub">설정:</span>{"".join(radios)}</span></div>')

    # 요약표
    gcols = [g for g in groups if any(st["groups"][g] for st in stats)]
    show_invalid, show_err = any(st["invalid"] for st in stats), any(st["err"] for st in stats)   # 모두 0 이면 칸을 뺀다(좁은 창)
    head = ["설정", "입력", "판정 맞음", "앱 기준", *gcols]
    if kind == 4:
        head += [t for t, _ in RULES4[1:]] + ["'선명하지 않음' 답"]
    else:
        head += ["물체 종류", "사람", "출동 차량"] + (["연기·불 지어냄"] if kind == 1 else [])
    head += ["형식 어긋남"] * show_invalid + ["오류"] * show_err + ["한 번 시간 중앙 · 최대", "출력 토큰 중앙", "모델 올리기"]
    body = []
    for i, ((n, v), st) in enumerate(zip(keys, stats)):
        cfg, meta = cfgs[n], metas.get(n) or {}
        tags = "".join(f' <span class="tag">{E(t)}</span>' for t in cfg_tags(cfg))
        ok = sum(st["ok"])
        cells = [f'<b>{E(short_name(n))}</b>{tags}<br><span class="sub small">{E(cfg.get("model", "?"))}</span>',
                 f'{E(v)}<br><span class="sub small">{E(VARIANT_KO.get(v, ""))}</span>',
                 f'<td class="num{" best" if ok == best else ""}">{ok}/{st["n"]}', f'<td class="num">{frac(st["conf"])}']
        cells += [f'<td class="num">{frac(st["groups"][g])}' for g in gcols]
        if kind == 4:
            cells += [f'<td class="num">{sum(o)}/{len(o)} <span class="sub small">(앱 {sum(c)})</span>' for o, c in st["rules"][1:]]
            cells.append(f'<td class="num">{st["usable_no"]}/{st["n"]}')
        else:
            cells += [f'<td class="num">{frac(st["items"][w])}' for w in ("object", "people", "emergency")]
            if kind == 1:
                cells.append(f'<td class="num">{st["smoke"]}')
        wall = st["wall"]
        cells += [f'<td class="num">{st["invalid"]}'] * show_invalid + [f'<td class="num">{st["err"]}'] * show_err
        cells += [f'<td class="num">{statistics.median(wall):.1f} · {max(wall):.1f}초' if wall else '<td class="num">–',
                  f'<td class="num">{statistics.median(st["out_tok"]):.0f}' if st["out_tok"] else '<td class="num">–',
                  f'<td class="num">클라우드 · 합계 ${st["cost"]:.2f}' if meta.get("api") == "anthropic"
                  # 판정 모델(run_decide.py): transformers 로 직접 올린다 — 모델 메모리(8비트면 언어 부분만)와 돌릴 때 최대 할당
                  else f'<td class="num">{meta["load_s"]}초 · {meta["quant"]} · 모델 {meta["footprint_gib"]} GiB'
                       + (f' · 최대 {meta["peak_alloc_gib"]} GiB GPU' if meta.get("peak_alloc_gib") else f' · CPU {meta.get("threads")}스레드')
                  if meta.get("api") == "decide"
                  # Mirage(run_mirage.py): 22 MB 가중치를 CPU 로 — GPU 를 쓰지 않는다
                  else f'<td class="num">{meta["load_s"]}초 · 가중치 {meta["weights_mb"]} MB · CPU' if meta.get("api") == "mirage"
                  else f'<td class="num">{meta["load_s"]}초 · {meta.get("size_vram_gib")}/{meta.get("size_gib")} GiB GPU' if "size_gib" in meta
                  # Strata 는 `strata launch` 가 미리 올린다 — 돌릴 때 GPU 전체 사용량(nvidia-smi)만 안다
                  else f'<td class="num">미리 올림 · GPU {meta["gpu_mib_after_load"] / 1024:.1f} GiB 사용' if meta.get("gpu_mib_after_load")
                  else "<td>–"]
        body.append(f'<tr class="k{i}">' + "".join(c if c.startswith("<td") else f"<td>{c}" for c in cells) + "</tr>")
    out.append(f'<h2 id="sum">설정별 요약</h2><div class="scroll sum"><table><tr>{"".join(f"<th>{E(h)}</th>" for h in head)}</tr>{"".join(body)}</table></div>')
    if any(st["think"] and max(st["think"]) for st in stats):
        out.append('<p class="sub small">생각 모드는 답 전에 생각을 길게 쓴다 — 장면별 칸에 생각 글자 수가 있다.</p>')

    # 장면 × 설정 판정표
    rows, last_g = [], None
    for cid in scene_ids:
        if group[cid] != last_g:
            last_g = group[cid]
            rows.append(f'<tr class="gh"><td colspan="{len(keys) + 2}">{E(last_g)}</td></tr>')
        cls, cells = [], []
        for i, key in enumerate(keys):
            a = ans.get((cid, key))
            if a is None:
                cells.append(f'<td class="c kc k{i}"></td>')
                continue
            if wrong(a):
                cls.append(f"wrong-k{i}")
            if a["rec"] is None:
                cells.append(f'<td class="c kc k{i} na"><a href="#s-{cid}">오류</a></td>')
            elif a["p"] is None:
                cells.append(f'<td class="c kc k{i} na"><a href="#s-{cid}">형식 어긋남</a></td>')
            else:
                ok = a["s"]["verdict"]
                extra = f"<small>{E(cue_abbr(a['p']))}</small>" if kind == 4 and cue_abbr(a["p"]) else ""
                cells.append(f'<td class="c kc k{i} {"ok" if ok else "bad"}" title="{E(a["p"].get("description_ko", ""))}">'
                             f'<a href="#s-{cid}">{E(vko(a["p"]["verdict"]))} {"✓" if ok else "✗"}</a>{extra}</td>')
        if not cls:
            cls.append("allright")
        warn = ' <span class="tag warn">확인 필요</span>' if cid in unsure else ""
        rows.append(f'<tr class="srow {" ".join(cls)}"><td class="scene"><a href="#s-{cid}"><b>{cid}</b> {E(cases[cid]["name"])}</a>{warn}</td>'
                    f'<td class="gt">{E(gt_text(gts[cid]["verdict"]))}</td>{"".join(cells)}</tr>')
    hdr = "".join(f'<th class="kc k{i}">{E(short_name(n))}<br><span class="sub small">{E(v)}</span></th>' for i, (n, v) in enumerate(keys))
    out.append('<h2 id="mx">장면 × 설정 판정표</h2>'
               + ('<p class="sub small">칸 아래 작은 글자는 모델이 \'예\'라고 한 단서(출동·경광·사람·손상·열림 / 앞차·신호·주차 / 콘)와 판정을 가르는 \'아니오\'(흐림·차 없음).</p>' if kind == 4 else "")
               + f'<div class="scroll mx"><table><tr><th>장면</th><th>정답</th>{hdr}</tr>{"".join(rows)}</table></div>')

    # 장면 카드
    out.append(f'<h2 id="scenes">장면별 자세히 ({len(scene_ids)}개)</h2>')
    for cid in scene_ids:
        c, info, gt = cases[cid], infos[cid], gts[cid]
        mine = [(i, ans[(cid, key)]) for i, key in enumerate(keys) if (cid, key) in ans]
        cls = [f"wrong-k{i}" for i, a in mine if wrong(a)] or ["allright"]
        w, h = info["frame"]
        x1, y1, x2, y2 = info["box"]
        meta = [f'{info["video"]} · {info["t"]:.1f}초 · {w}×{h}',
                f'후보 {info["label"]} {info["score"]:.2f} ({"직접 준 박스" if info.get("manual") else "앱 모델 검출"}) · 박스 {x2 - x1:.0f}×{y2 - y1:.0f} px']
        if info.get("stopped_s") is not None:
            meta.append(f'멈춘 지 {info["stopped_s"]:g}초(앞 프레임과 박스를 맞춰 잼{" — 직접 준 값" if cid in spec.get("round3_stopped_s", {}) else ""})')
        items = [f"{t} {x}" for t, x in (("물체", gt_item(gt, "object")), ("차로 위 사람", gt_item(gt, "people")), ("출동 차량", gt_item(gt, "emergency"))) if x]
        tags = [f'<span class="tag">{E(group[cid])}</span>', f'<span class="tag gt">정답 {E(gt_text(gt["verdict"]))}</span>']
        if items:
            tags.append(f'<span class="tag long">정답 항목: {E(" · ".join(items))}</span>')
        if cid in unsure:
            tags.append('<span class="tag warn">정답 확인 필요</span>')
        card = [f'<section class="scene-card {" ".join(cls)}" id="s-{cid}"><header><h3><span class="sid">{cid}</span>{E(c["name"])}</h3>'
                f'<div class="tags">{"".join(tags)}</div>' + "".join(f'<p class="meta">{E(m)}</p>' for m in meta)
                + (f'<p class="unsure"><b>확인 부탁</b> — {E(unsure[cid])}</p>' if cid in unsure else "") + "</header>"]
        # 그림 — 모델이 본 것 먼저, 화면 전체는 보내지 않았어도 참고로
        used = {}
        for _, a in mine:
            for im in a["q"]["images"]:
                used.setdefault(im, set()).add(a["key"][1])
        figs = []
        for im in IMAGE_ORDER:
            path = HERE / "out" / "cases" / cid / f"{im}.jpg"
            if not path.exists() or (im not in used and im != "full"):
                continue
            uri, size, shrunk = embed(path, shrink=im in ("full", "prev", "next"))
            seen = f'모델에 보냄: {", ".join(sorted(used[im]))}' if im in used else "참고 — 모델에 보내지 않음"
            dims = f" · {size[0]}×{size[1]}" if size else ""
            figs.append(f'<figure class="{"" if im in used else "ctx"}"><img src="{uri}" alt="{cid} {E(IMAGE_KO[im])}" loading="lazy">'
                        f'<figcaption>{E(IMAGE_KO[im])}{dims} · {seen}{" · 표시용으로 줄임" if shrunk else ""}</figcaption></figure>')
        card.append(f'<div class="body"><div class="pics">{"".join(figs)}</div><div class="answers">')
        for i, a in mine:
            n, v = a["key"]
            rec, p = a["rec"], a["p"]
            tags = "".join(f' <span class="tag">{E(t)}</span>' for t in cfg_tags(cfgs[n]))
            if rec is None:
                state, verdict, timing = "bad", '<span class="v na">오류</span>', ""
            else:
                t = [f'{rec["wall_s"]:.1f}초']
                if rec.get("prompt_tokens") is not None:
                    t.append(f'입력 {rec["prompt_tokens"]:,} · 출력 {rec.get("out_tokens") or 0:,} 토큰')
                if rec.get("thinking_chars"):
                    t.append(f'생각 {rec["thinking_chars"]:,}자')
                if rec.get("done_reason") in ("length", "max_tokens"):
                    t.append("출력 한도에 걸림")
                if rec.get("done_reason") == "refusal":
                    t.append("거절됨")
                if rec.get("cost_usd") is not None:
                    t.append(f'${rec["cost_usd"]:.4f}')
                if rec.get("fallback"):   # 거절돼 다른 모델이 이어서 답했다 — 이 답은 그 모델의 것
                    t.append(f'{rec.get("served_by")} 이 대신 답함')
                timing = " · ".join(t)
                if p is None:
                    state, verdict = "bad", f'<span class="v na">형식 어긋남 ({E(", ".join(rec.get("invalid") or []))})</span>'
                else:
                    ok = a["s"]["verdict"]
                    state = "ok" if ok else "bad"
                    verdict = f'<span class="v {state}">{"규칙 판정" if kind == 4 else "판정"} {E(vko(p["verdict"]))} {"✓" if ok else "✗"}</span>'
                    if "confirm" in a["s"]:
                        verdict += f' <span class="sub small">앱 기준 {"✓" if a["s"]["confirm"] else "✗"}</span>'
            blk = [f'<article class="ans k{i} {state}"><div class="ans-head"><b>{E(short_name(n))}</b>{tags}<span class="var">{E(v)}</span>{verdict}'
                   f'<span class="time">{E(timing)}</span></div>']
            if rec is None:
                blk.append(f'<p class="err">오류: {E(str(a["err"].get("error")))}</p>')
            if p is not None:
                if kind == 4:
                    blk.append(f'<p class="why">까닭: {E(a["rules"][0]["why"])}</p>')
                if rec.get("probs"):   # 판정 모델 — 설명 글이 없다. 대신 질문마다 확률(예/아니오는 P(예), 판정 질문은 후보별)
                    pr = rec["probs"]
                    yes = " · ".join(f'{FIELD_KO.get(k, k)} {pr[k]:.2f}' for k in FIELDS[4] if isinstance(pr.get(k), float))
                    dv = " · ".join(f'{vko(k)} {x:.2f}' for k, x in sorted((pr.get("direct_verdict") or {}).items(), key=lambda kv: -kv[1]))
                    ok = p.get("direct_verdict") in a["gtv"]
                    blk.append(f'<p class="sub small">P(예): {E(yes)}</p>'
                               f'<p class="why">판정 질문(직접): <b class="{"ok" if ok else "bad"}">{E(vko(p.get("direct_verdict")))} {"✓" if ok else "✗"}</b>'
                               f' <span class="sub small">{E(dv)}</span></p>')
                else:
                    blk.append(f'<blockquote>{E(p.get("description_ko", ""))}</blockquote>')
                if rec.get("evidence"):   # Mirage describe — 미리 정한 낱말 표가 무엇을 보고 '예'로 했는지
                    found = " · ".join(f'{FIELD_KO.get(k, k)} ← {", ".join(v)}' for k, v in rec["evidence"].items() if v)
                    blk.append(f'<p class="sub small">낱말 표가 찾은 것: {E(found or "없음")}</p>')
                if kind == 3:
                    blk.append(f'<p class="evidence"><span class="sub">근거</span> {E(p.get("cause_evidence", ""))}</p>')
                chips = []
                for k in FIELDS[kind]:
                    if k not in p:
                        continue
                    what = SCORED.get(k)
                    miss = gt_item(gt, what) if what and a["s"].get(what) is False else None
                    if k == "smoke_or_fire" and a["s"].get("smoke"):
                        miss = "어느 장면에도 없다 — 지어냄"
                    chips.append(chip(k, p[k], miss))
                blk.append(f'<div class="chips">{"".join(chips)}</div>')
                if kind == 4:
                    blk.append('<div class="rules">' + "".join(
                        f'<span>{E(r["title"])}: <b class="{"ok" if r["ok"] else "bad"}">{E(vko(r["verdict"]))} {"✓" if r["ok"] else "✗"}</b>'
                        f'{"" if r["why"] == a["rules"][0]["why"] else " — " + E(r["why"])}</span>' for r in a["rules"][1:]) + "</div>")
            raw = [f'<pre>{E(a["q"]["text"])}</pre>', f'<p class="sub">보낸 그림: {E(", ".join(f"{im}.jpg" for im in a["q"]["images"]))}</p>']
            if rec is not None:
                raw.append(f'<pre>{E(rec.get("content") or "(빈 응답)")}</pre>')
            blk.append(f'<details><summary>질문·응답 원문</summary>{"".join(raw)}</details></article>')
            card.append("".join(blk))
        card.append("</div></div></section>")
        out.append("".join(card))

    # 질문 원문
    out.append('<h2 id="ask">질문 원문</h2><p class="sub small">장면마다 바뀌는 사용자 글은 각 답의 \'질문·응답 원문\'에 있다(run_vlm.question 그대로).</p>')
    systems = {}
    for n, _ in keys:
        q = run_vlm.question(cfgs[n], next(iter(infos.values())), _)
        systems.setdefault(q["system"], (q, []))[1].append(short_name(n))
    for sysmsg, (q, names) in systems.items():
        rows = "".join(f'<tr><td>{E(FIELD_KO.get(k, k))}</td><td><code>{E(k)}</code></td><td style="white-space:normal">{E(" · ".join(ko(x) for x in vals))}</td></tr>'
                       for k, vals in q["enums"].items())
        out.append(f'<div class="note"><p><b>system</b> — {E(", ".join(dict.fromkeys(names)))}</p><pre>{E(sysmsg)}</pre>'
                   f'<p class="sub">답 항목(JSON 스키마, 이 순서로 쓴다 — 설명 {"먼저" if next(iter(q["schema"]["properties"])) == "description_ko" else "마지막"})</p>'
                   f'<div class="scroll"><table><tr><th>항목</th><th>키</th><th>허용 값</th></tr>{rows}</table></div></div>')
    out.append("</main></body></html>")

    path = res_dir / "report.html"
    path.write_text("".join(out), encoding="utf-8")
    info = {"folder": folder, "title": title, "scenes": len(scene_ids), "keys": keys, "stats": stats, "kind": kind}
    print(f"{folder}: 장면 {len(scene_ids)} · 설정 {len(keys)} -> {path} ({path.stat().st_size / 2**20:.1f} MB)")
    for (n, v), st in zip(keys, stats):
        line = f"  {short_name(n)} {v}: 판정 {sum(st['ok'])}/{st['n']} · 앱 {frac(st['conf'])}"
        if kind == 4:
            line += "".join(f" · {t.split(' · ')[0]} {sum(o)}/{len(o)}(앱 {sum(c)})" for (t, _), (o, c) in zip(RULES4[1:], st["rules"][1:]))
        print(line)
    return path, info


def write_index(built):
    rows = []
    for path, info in built:
        best = max(((sum(st["ok"]), st["n"], k) for k, st in zip(info["keys"], info["stats"])), default=None)
        top = f'{short_name(best[2][0])} {best[2][1]} {best[0]}/{best[1]}' if best else "–"
        rows.append(f'<tr><td><a href="{E(info["folder"])}/report.html">{E(info["title"])}</a></td><td><code>{E(info["folder"])}</code></td>'
                    f'<td class="num">{info["scenes"]}</td><td class="num">{len(info["keys"])}</td><td>{E(top)}</td></tr>')
    page = (f'<!doctype html><html lang="ko"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">'
            f'<meta name="color-scheme" content="light dark"><title>VLM 실험 보고서</title><style>{CSS}</style></head><body><main>'
            f'<h1>VLM 실험 보고서</h1><p class="sub">experiments/VlmCheck — 결과 폴더마다 장면별 보고서(report.py, {time.strftime("%Y-%m-%d %H:%M")})</p>'
            f'<div class="scroll"><table><tr><th>차수</th><th>폴더</th><th>장면</th><th>설정</th><th>판정 가장 많이 맞힌 설정</th></tr>{"".join(rows)}</table></div>'
            f'</main></body></html>')
    path = HERE / "out" / "index.html"
    path.write_text(page, encoding="utf-8")
    print(f"목차 -> {path}")


def main():
    sys.stdout.reconfigure(errors="replace")   # 콘솔이 cp949 여도 멈추지 않게
    names = sys.argv[1:] or sorted(d.name for d in (HERE / "out").iterdir() if d.is_dir() and any(d.glob("*.jsonl")))
    built = [_build(HERE / "out" / n) for n in names]
    if not sys.argv[1:]:
        write_index(built)


if __name__ == "__main__":
    main()
