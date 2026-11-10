# 공개 저장소 경과

2026-11-10 CLAUDE.md 에서 글자 그대로 옮겼다(세션마다 읽히지 않게). 규칙·금지 사항은 CLAUDE.md 에 남아 있다.

## 첫 커밋과 옛 기록

원격은 `origin` 하나 = 공개 저장소 GitHub `Ch2Su/DetectView` (2026-11-02 공개 — 사용자 결정: 옛 기록은 공개하지 않는다).

  옛 기록의 마지막 상태(`f8d66f4`)를 그대로 담은 기록 없는 첫 커밋(`6c43790`)부터 시작했다. 올리는 건 `main` 하나다.
  2026-11-06 GitHub 사용자 이름이 `chisu0208` → `Ch2Su` 로 바뀌어(계정 ID 42445310 같음) origin 주소를 옮겼다 — 옛 주소는 GitHub 가 새 주소로 넘겨 주지만, 옛 이름을 다른 사람이 가져가면 끊긴다.
  - **옛 개발 기록(2026-09-21~11-02, 커밋 75개)은 공개하지 않았다** — 이 PC 의 로컬 태그 `old-history`(`f8d66f4`)와, 사용자가 클라우드에 백업한 bundle `DetectView-history-2026-11-02.bundle`
    (44.9 MB — 2026-11-06 까지 `old/` 안에 있었다. `git clone <파일>` 로 되살아난다 — 복원 확인함)에만 있다. 그 기록을 담았던 비공개 저장소(`DetectView` → `DetectView-history` 로 이름 바꿈)는 같은 날 사용자가 지웠고 이슈 #1~#11 도 함께 없어졌다.
    **이 문서·메모의 커밋 해시(`6d1f61a` 등)와 이슈 번호 #1~#11 은 모두 그 옛 기록의 것**이다 — 해시는 `git show <해시>`·`git log old-history` 로 이 PC 에서 본다.
    이슈 본문은 남아 있지 않지만 내용은 이 문서의 해당 서술(번호 옆)에 있다. 이 PC 엔 이제 태그만 있다 — 태그를 지우면 이 PC 에선 옛 기록이 사라진다
    (클라우드의 bundle 로 되살린다. `git gc` 는 태그가 가리키는 커밋은 지우지 않는다).

## 공개 정리

  - 공개 정리 (2026-11-02 점검 — 비밀값은 옛 기록 전체에 없었다): 저장소 전체 GNU AGPL-3.0(`LICENSE`), 남의 저작물 고지 `THIRD-PARTY-NOTICES.md`(README 라이선스 절엔 NuGet 외부 구성요소 표).
    커밋 작성자 이메일은 이 저장소 설정(`.git/config` 의 `user.email` — worktree 도 같이 쓴다)만 GitHub noreply `42445310+chisu0208@users.noreply.github.com` 이다(계정 ID 로 잇는 주소라 11-06 사용자 이름이 바뀐 뒤에도 그대로 쓴다 · 전역 `~/.gitconfig` 도 같은 날 사용자 승인으로 같은 noreply 로 바꿨다 — 이 PC 의 다른 저장소 push 가 GH007 로 막히지 않게).
    README 스크린샷(test4 3초 장면, 실제 도로 CCTV 프레임 — 출처 기록 없음)은 영상 사용 권리를 확인하지 못해 뺐다(옛 기록에만 있다).
    GitHub 계정은 'Keep my email addresses private'·'Block command line pushes that expose my email' 을 켰다(11-02 사용자) — 실제 주소가 작성자인 커밋을 push 하면 GitHub 가 막는다(GH007).
    모델 메타데이터 `description` 에 학습 데이터 폴더 경로가 들어 있지만 그대로 뒀다 — 모델 파일을 바꾸면 MD5 가 바뀌어 NPU 짝·컴파일 캐시를 다시 만들어야 한다
