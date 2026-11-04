# 2단계 학습(run_step2.sh)을 WSL 에서 돌리는 동안 Windows 가 유휴 절전에 들어가지 않게 붙잡는다 (README '2단계').
# 몇 시간 걸리므로 따로 떠 있는 숨은 창으로 띄우는 것을 전제로 한다 — 띄운 쪽(터미널·Claude 앱)이 닫혀도 학습이 이어지게:
#   Start-Process pwsh -WindowStyle Hidden -ArgumentList '-NoProfile','-File','<이 파일>','rfdetr_s','y26s','y26m'
# 막는 것은 유휴 절전뿐이다 — 직접 절전을 고르거나 덮개 닫힘 동작이 '절전'이면 잠든다(그동안 학습이 멈춘다. 이 PC 는 덮개 닫힘 동작을 꺼 두었다).
# 배터리일 땐 20분 무입력이면 유휴 절전이 걸리는데(11-04 벤치가 그렇게 멈췄다), 이 스크립트가 도는 동안은 그것도 막힌다.
# 이름을 여러 개 받는다 — -File 로 넘긴 인자는 [string[]] 에 첫 것만 묶이므로 나머지를 모두 모은다
param([Parameter(ValueFromRemainingArguments = $true)][string[]]$Names = @())

$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$log = Join-Path $here '..\out\step2_driver.log'
New-Item -ItemType Directory -Force (Split-Path $log) | Out-Null

Add-Type -Namespace Native -Name Power -MemberDefinition @'
[DllImport("kernel32.dll")] public static extern uint SetThreadExecutionState(uint esFlags);
'@
# ES_CONTINUOUS | ES_SYSTEM_REQUIRED(0x80000001) — 이 스레드가 살아 있는 동안 유휴 절전을 막는다 (화면은 꺼져도 된다). 설정은 바꾸지 않는다.
# 16진 리터럴 0x80000001 은 PowerShell 에서 음수 Int32 라 UInt32 로 못 넘긴다 — 10진수로
$ES_CONTINUOUS = [uint32]2147483648
[void][Native.Power]::SetThreadExecutionState($ES_CONTINUOUS -bor [uint32]1)

# WSL 프로그램의 출력은 UTF-8 이다 — 콘솔 코드 페이지(949)로 읽으면 한국어가 깨진다
[Console]::OutputEncoding = [Text.Encoding]::UTF8
# 스크립트를 WSL 쪽에 복사해 거기서 돌린다 — 도는 중에 저장소의 파일을 고쳐도 bash 가 꼬이지 않게.
# CRLF 로 체크아웃돼 있어도 bash 가 돌게 줄 끝의 CR 을 지운다
$wslHere = (& wsl.exe -e wslpath -a ($here -replace '\\', '/')).Trim()
"[$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')] 시작 — $($Names -join ' ') (Windows PID $PID)" | Add-Content -Encoding utf8 $log
$ErrorActionPreference = 'Continue'   # 학습 로그가 stderr 로 나와도(2>&1) 멈추지 않게
& wsl.exe -e bash -c "mkdir -p ~/rfdetr_work/scripts && cp '$wslHere'/train_rfdetr.py '$wslHere'/run_step2.sh ~/rfdetr_work/scripts/ && sed -i 's/\r$//' ~/rfdetr_work/scripts/run_step2.sh && bash ~/rfdetr_work/scripts/run_step2.sh $($Names -join ' ')" 2>&1 |
    ForEach-Object { "$_" } | Add-Content -Encoding utf8 $log
"[$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')] 끝 (wsl 종료 코드 $LASTEXITCODE)" | Add-Content -Encoding utf8 $log
[void][Native.Power]::SetThreadExecutionState($ES_CONTINUOUS)
