# 런타임 · 패키지 — 실측과 경과

> CLAUDE.md 에서 옮긴 글이다(2026-11-10 — 세션마다 읽히지 않게 내용은 글자 그대로 옮기고, CLAUDE.md 에는 규칙·금지 사항과 가리키는 줄만 남겼다).
> 본문의 '위'·'아래'·'○번'·'○○ 절'이 가리키는 곳은 이 문서나 CLAUDE.md, 또는 다른 `docs/` 문서다 — CLAUDE.md 의 '자세한 문서' 표에서 찾는다(예: '아래 3번' = CLAUDE.md '절대 건드리면 안 되는 것들' 3번). 새로 알게 된 실측·경과는 여기에 더한다.

## Windows ML 런타임 교체 전후 비교

- 교체 전후 비교(2026-10-24, 단일 파일 배포본, test1.mp4 재생 처리 FPS 5회 중앙): 기존 DirectML 1.23 **98.5 / 98.6** vs Windows ML **99.1** — 차이 없음. exe 119.4 → 126.4 MB. UI 테스트 3종 통과
- **검출 결과는 비트 단위로 같다**: test1~5 각 40프레임(200장)·검출 2538개가 좌표·신뢰도까지 완전히 일치(결과 CSV 해시 동일). 같은 런타임 두 번 실행도 완전히 같다(DirectML 결정적).
  `experiments/RuntimeParity/`(같은 소스를 `Old/`·`WinML/` 로 두 번 빌드) — 런타임을 다시 바꿀 때 같은 방법으로 확인한다.
  참고로 옛 조합은 `DirectML.dll` 을 배포하지 않고 **OS 의 `System32\DirectML.dll`(1.15.6)** 을 썼다. Windows ML 은 자기 것(1.15.4)을 exe 에 넣는다

## OpenCvSharp 판

지금 판은 4.13.0.20260302 짝이다(판은 `Directory.Build.props` 한 곳 — CLAUDE.md '절대 건드리면 안 되는 것들' 1번).

  초기 커밋부터 관리 20260526 + 네이티브 20260302 로 어긋나 있었다 — 관리 DLL 의 P/Invoke 진입점 2879개 중 117개(`aruco_*`·`saliency_*`·`shape_*`·`ximgproc_*` 등)가 네이티브에 없었고
  시그니처가 바뀐 것도 10개(LineSegmentDetector·MergeDebevec·MergeMertens) 있었다. 빌드는 되고 부르는 순간 터진다.
  runtime.win 에 20260526 판이 없어 관리 쪽을 내렸다(2026-10-31, 이슈 #9): 네이티브 DLL(`OpenCvSharpExtern.dll`·`opencv_videoio_ffmpeg4130_64.dll`)은 바이트 단위로 그대로,
  앱 컴파일 결과(IL·MemberRef 750개)도 같고, test1~5·`vfr60_nodur.webm` 7,696 프레임의 디코딩·시크·CvtColor·그리기와 RuntimeParity 검출 CSV(2538개)도 옛 짝과 완전히 같았다.
  참고(10-31 평가만, 채택 안 함): 20260627 짝은 네이티브를 다시 빌드해 contrib 13개가 빠졌고(bioinspired·ccalib·dpm·stereo·videostab 등) 관리 쪽 AssemblyVersion 이 4.0.0.0 이다.

## 중복 박스 필터

규칙(`DuplicateBoxes`)은 CLAUDE.md '절대 건드리면 안 되는 것들' 3번.

- 실측(2026-10-25, `experiments/TrackerCheck` 의 `dedup`): 프레임 검출 test4 18795 → **17892개(−4.8%)** (같은 라벨 IoU>0.7 553 · 같은 라벨 범위만 다름 294 ·
  차량 라벨 다름 56), test5 5451 → **5289개(−3.0%)**. 목표 정지 차량(test4 덤프트럭·test5 갓길 화물차) 자리에 박스가 둘 이상인 프레임 1·2 → 0.
  SAHI(15프레임에 1장) 병합 결과 test4 2054 → 2028, test5 468 → 427 — 빠진 것은 모두 남은 박스의 중복이고 새로 생긴 박스 없음.
  앱으로 test4 를 1.1·4.9·23.9초에서 멈춰(SAHI 포함) 눈으로 봤을 때 겹친 라벨이 사라지고 빠진 차 없음
- **NMS 를 넣어 내보낸 모델(Ultralytics `nms=True`)로 바꿔도 이 필터는 그대로 필요하다** (2026-11-05, 2단계 학습 YOLO26s 로 test1~5 모든 프레임 — `experiments/RfDetrBench` README 'YOLO 남은 여지'):
  같은 클래스 IoU>0.7 쌍은 180~553 → 0~6 으로 거의 없어지지만, 클래스별 NMS 가 못 지우는 같은 자리 car+truck·범위만 다른 중복은 영상 5개 중 4개에서 늘고(test4 1,027 → 1,577쌍) 박스도 5~10% 많다.
  그 모델 자체도 권하지 않았다 — 시험 분할 AP 가 오히려 0.003~0.005 낮고(작은 물체·AR100 손해) `NonMaxSuppression` 이 DirectML 에 없어 CPU 로 가 `session.Run` 이 1 ms 느리다
