#!/bin/bash
# 2단계 학습을 차례로 돌린다: RF-DETR-S → YOLO26s → YOLO26m (README '2단계'). WSL 에서 파일로 돈다.
# Start-Step2.ps1 이 이 폴더를 ~/rfdetr_work/scripts 로 복사해 거기서 부른다 — 도는 중에 저장소 쪽을 고쳐도 꼬이지 않게
# (bash 는 스크립트를 조금씩 읽는다).
#
#   bash ~/rfdetr_work/scripts/run_step2.sh [이름...]     # 이름: rfdetr_s y26s y26m — 없으면 전부
#   touch ~/rfdetr_work/runs/HALT                         # 지금 것이 끝나면 다음 것을 시작하지 않는다
#   touch ~/rfdetr_work/runs/yolo/STOP                    # 지금 도는 YOLO 학습을 정상 중단 (라벨링 도구 train_wsl.py 의 정지 파일)
set -u
W=/home/chisu/rfdetr_work
HERE=$(cd "$(dirname "$0")" && pwd)
DATA=$W/data/visible                       # 데이터셋 백업(~/datasets_backup/visible_2026-11-04)의 작업 사본 — data.yaml 의 path 도 이 폴더
LOGS=$W/runs/logs
mkdir -p "$LOGS"
# 작업 디렉터리를 옮긴다 — 런처가 띄운 wsl.exe 는 Windows 쪽 현재 폴더(저장소)에서 시작해서, Ultralytics 의 AMP 점검이
# yolo26n.pt 를 저장소의 weights/ 에 내려받았다 (2026-11-04)
cd "$W"
# 시작·끝 줄은 여기에도 바로 적는다 — Windows 런처는 wsl.exe 출력을 드라이버가 끝날 때에야 받는다(도는 동안 진행이 안 보였다)
say() { echo "$*" | tee -a "$LOGS/driver.log"; }

# YOLO 는 라벨링 도구의 학습 환경과 스크립트를 그대로 쓴다 — 배포 모델을 만든 것과 같은 Ultralytics(8.4.142)·torch.
# 환경은 고치지 않고 실행만 한다: YOLO_AUTOINSTALL=false 로 Ultralytics 가 빠진 패키지를 깔지 못하게 하고,
# 결과는 runs_wsl 이 아니라 여기 둔다(runs_wsl 에 있으면 웹 UI 'WSL 이어하기' 가 집어 가 models/best.pt 를 바꾼다)
YP=/home/chisu/miniconda3/envs/yolo_final/bin/python
YT=/mnt/e/local_auto_labeling/wsl/train_wsl.py
YW=/mnt/e/local_auto_labeling/weights
# 웹 UI 'WSL 학습' 버튼과 같은 인자(config.yaml 의 train.*·overrides, runs/compare_0918 의 HYP) 를 100 epoch 스케줄로.
# close_mosaic 은 300 epoch 의 30 처럼 끝 10% 로 맞춰 10
HYP="epochs=100 patience=120 seed=0 pretrained=true amp=true cos_lr=true box=7.5 cls=1.5 dfl=1.5 close_mosaic=10 hsv_h=0.015 hsv_s=0.7 hsv_v=0.4 fliplr=0.5 flipud=0.0 save_period=20"

run_rfdetr() {
  "$W/.venv/bin/python" "$HERE/train_rfdetr.py" --data "$DATA" --out "$W/runs/rfdetr_s" > "$LOGS/rfdetr_s.log" 2>&1
}
run_yolo() {   # 이름 가중치 배치 — 배치는 8GB 에서 compare_0918 과 같게(s 20 · m 10)
  local name=$1 weights=$2 batch=$3
  # shellcheck disable=SC2086  # HYP 는 KEY=VALUE 여러 개로 나뉘어야 한다
  YOLO_AUTOINSTALL=false "$YP" "$YT" --project "$W/runs/yolo" --name "$name" --data "$DATA/data.yaml" \
    --model "$YW/$weights" --workers 8 --cache ram --bare --hyp $HYP --imgsz 640 --batch "$batch" > "$LOGS/$name.log" 2>&1
}

ORDER=(rfdetr_s y26s y26m)
[ $# -gt 0 ] && ORDER=("$@")
for name in "${ORDER[@]}"; do
  if [ -f "$W/runs/HALT" ]; then
    rm -f "$W/runs/HALT"
    say "=== HALT — $name 부터 시작하지 않고 멈춤 $(date '+%F %T')"
    break
  fi
  say "=== $name 시작 $(date '+%F %T')"
  case $name in
    rfdetr_s) run_rfdetr ;;
    y26s) run_yolo y26s yolo26s.pt 20 ;;
    y26m) run_yolo y26m yolo26m.pt 10 ;;
    *) echo "모르는 이름: $name"; false ;;
  esac
  say "=== $name 끝 (종료 코드 $?) $(date '+%F %T') — 로그 $LOGS/$name.log"
done
