"""run_vlm.py 결과를 cases.json 의 정답(gt)과 견준다 → out/summary.md 와 화면 출력.

채점 항목(gt 가 null 이면 그 장면은 빼고 센다):
- verdict: 장면·입력 방식마다 맞다고 본 답 목록에 들면 맞음 (주 지표)
- object_type: 박스 안 물체 종류가 목록에 들면 맞음
- people: 차로 위 사람이 있는지(few·many = 있음, none = 없음, unknown = 틀림으로 셈)
- emergency: 출동·작업 차량이 있는지(yes/no, unknown = 틀림)
- 연기·불: 어느 장면에도 없다 — yes 라고 하면 '지어냄'으로 센다
JSON 이 형식대로 오지 않은 호출은 모든 항목에서 틀림으로 센다(형식 어긋남 칸에 따로).

    uv run --no-project --python 3.12 experiments/VlmCheck/score.py [결과 폴더 이름]
"""
import json
import statistics
import sys
from collections import defaultdict
from pathlib import Path

HERE = Path(__file__).resolve().parent
GROUP = {"P": "유고", "N": "정상", "R": "정상", "C": "사고 화면 속 정상", "T": "함정", "S": "가짜 검출"}
SHORT = {"incident": "유고", "normal": "정상", "detection_error": "오검출", "cannot_judge": "판단불가", None: "×",
         # 3차 판정
         "accident_or_breakdown": "사고·고장", "normal_stop": "정상 정차", "not_vehicle": "차량 아님", "unusable_image": "화면 불량"}


def gt_verdicts(gt, variant):
    v = gt["verdict"]
    return v.get(variant, v["B"]) if isinstance(v, dict) else v   # D(키운 그림만)는 B 와 같은 답을 기대


def score(rec, gt, confirm="incident"):
    p = rec.get("parsed") if not rec.get("invalid") else None
    out = {"valid": p is not None}
    out["verdict"] = p is not None and p["verdict"] in gt_verdicts(gt, rec["variant"])
    # 4차는 항목 이름·값이 조금 다르다(vehicle_type, people_on_road yes/no, emergency_vehicle_near)
    if gt.get("object_type") is not None:
        out["object"] = p is not None and p.get("object_type", p.get("vehicle_type")) in gt["object_type"]
    if gt.get("people") is not None:
        out["people"] = p is not None and p["people_on_road"] in (("few", "many", "yes") if gt["people"] else ("none", "no"))
    if gt.get("emergency") is not None:
        out["emergency"] = p is not None and p.get("emergency_vehicles", p.get("emergency_vehicle_near")) == ("yes" if gt["emergency"] else "no")
    out["smoke"] = p is not None and p.get("smoke_or_fire") == "yes"     # 3차 스키마엔 이 항목이 없다
    # 앱에서 쓰는 꼴: VLM 이 유고를 '확인'(1·2차 incident, 3차 accident_or_breakdown)했나 안 했나. 정답 목록이 그것뿐이면
    # 확인해야 맞고, 없으면 확인하지 않아야 맞다. 섞여 있으면(T2·흐림·검은 화면의 C) 빼고 센다
    want = gt_verdicts(gt, rec["variant"])
    if all(v == confirm for v in want) or confirm not in want:
        out["confirm"] = (p is not None and p["verdict"] == confirm) == (confirm in want)
    return out


def pct(xs):
    return f"{100 * sum(xs) / len(xs):.0f}% ({sum(xs)}/{len(xs)})" if xs else "-"


def main():
    res_dir = HERE / "out" / (sys.argv[1] if len(sys.argv) > 1 else "results")
    spec = json.loads((HERE / "cases.json").read_text(encoding="utf-8"))
    cases = {c["id"]: c for c in spec["cases"]}
    # 3차 결과 폴더(round3*)는 판정 이름이 다르다 — cases.json 의 round3 정답으로 바꿔 채점
    confirm = "incident"
    if res_dir.name.startswith(("round3", "round4")):   # 4차는 관찰을 규칙(decide)으로 바꾼 판정이 같은 이름으로 들어 있다
        confirm = "accident_or_breakdown"
        cases = {cid: dict(c, gt=dict(c["gt"], verdict=spec["round3"][cid])) for cid, c in cases.items() if cid in spec["round3"]}
    recs = []
    for f in sorted(res_dir.glob("*.jsonl")):
        last = {}
        for line in f.read_text(encoding="utf-8").splitlines():
            r = json.loads(line)
            last[(r["case"], r["variant"])] = r          # 같은 호출을 다시 돌렸으면 마지막 것
        recs += [r for r in last.values() if r.get("error") is None]
    metas = {json.loads(m.read_text(encoding="utf-8"))["config"]["name"]: json.loads(m.read_text(encoding="utf-8"))
             for m in res_dir.glob("*.meta.json")}

    agg = defaultdict(lambda: defaultdict(list))
    matrix = defaultdict(dict)
    recs = [r for r in recs if r["case"] in cases]
    for r in recs:
        s = score(r, cases[r["case"]]["gt"], confirm)
        key = (r["config"], r["variant"])
        for k, v in s.items():
            agg[key][k].append(v)
        agg[key]["v_" + GROUP[r["case"][0]]].append(s["verdict"])
        agg[key]["wall"].append(r["wall_s"])
        verdict = (r.get("parsed") or {}).get("verdict") if s["valid"] else None
        matrix[r["case"]][key] = f"{SHORT.get(verdict, verdict)}{'' if s['verdict'] else '✗'}"

    keys = sorted(agg, key=lambda k: (list(metas).index(k[0]) if k[0] in metas else 99, k[1]))
    lines = ["| 설정 | 입력 | 판정 맞음 | 유고 확인 맞음(앱 기준) | 유고 | 정상 | 사고 화면 속 정상 | 함정 | 가짜 검출 | 물체 종류 | 사람 있음 | 출동 차량 | 연기·불 지어냄 | 형식 어긋남 | 시간 중앙값 |",
             "|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|"]
    for k in keys:
        a = agg[k]
        lines.append(f"| {k[0]} | {k[1]} | **{pct(a['verdict'])}** | {pct(a['confirm'])} | {pct(a['v_유고'])} | {pct(a['v_정상'])} | {pct(a['v_사고 화면 속 정상'])} | "
                     f"{pct(a['v_함정'])} | {pct(a['v_가짜 검출'])} | "
                     f"{pct(a['object'])} | {pct(a['people'])} | {pct(a['emergency'])} | {sum(a['smoke'])} | {a['valid'].count(False)} | "
                     f"{statistics.median(a['wall']):.1f}초 |")
    lines += ["", "모델 올리기(설정마다 처음 한 번):", ""]
    for name, m in metas.items():
        lines.append(f"- {name}: {m['load_s']}초, {m.get('size_vram_gib')}/{m.get('size_gib')} GiB 가 GPU(나머지는 CPU)")
    lines += ["", "장면별 판정(✗ = 틀림, × = 형식 어긋남):", "",
              "| 장면 | 정답 | " + " | ".join(f"{c} {v}" for c, v in keys) + " |", "|---|---|" + "---|" * len(keys)]
    for cid, c in cases.items():
        if cid not in matrix:
            continue   # 이 결과 폴더에서 돌리지 않은 장면(3차의 움직이는 후보 등)
        gv = c["gt"]["verdict"]
        gv = "·".join(SHORT[x] for x in (gv["A"] if isinstance(gv, dict) else gv))
        lines.append(f"| {cid} {c['name']} | {gv} | " + " | ".join(matrix[cid].get(k, "") for k in keys) + " |")
    text = "\n".join(lines)
    (res_dir.parent / "summary.md").write_text(text + "\n", encoding="utf-8")
    print(text)


if __name__ == "__main__":
    main()
