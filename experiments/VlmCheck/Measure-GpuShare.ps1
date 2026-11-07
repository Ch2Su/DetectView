# VLM 이 앱과 GPU 를 나눠 쓸 때 — 배포본 재생 처리 FPS 를 기준선 → VLM(GPU) 부하 → VLM(CPU) 부하 → 기준선 순으로 잰다 (2026-11-07, README 결과 6)
#
#   pwsh experiments\VlmCheck\Measure-GpuShare.ps1 [-ExeDir publish] [-Video publish\test1.mp4] [-Runs 3] [-Seconds 15]
#
# 필요: Ollama 가 떠 있고 qwen3.5:4b 를 받아 둠 · uv(파이썬 3.12) · out\cases\P7\crop.jpg(make_cases.py) ·
#       배포본과 테스트 영상(worktree 에선 publish 연결 — CLAUDE.md '빌드 · 배포'). 상대 경로는 저장소 루트 기준.
# 앱 창이 실행마다 떴다 닫힌다(tests\ui\Measure-Fps.ps1 — 3단계 × Runs 번 + 기준선 둘). 전원을 연결해 둔다.
# VLM 은 쉬지 않고 부른다 — 실제 앱(유고 후보 하나에 한 번)보다 훨씬 나쁜 조건이다.
param(
    [string]$ExeDir = 'publish',
    [string]$Video = 'publish\test1.mp4',
    [int]$Runs = 3,
    [int]$Seconds = 15
)
$ErrorActionPreference = 'Continue'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
function Abs([string]$p) { if ([IO.Path]::IsPathRooted($p)) { $p } else { Join-Path $repo $p } }
$fpsScript = Join-Path $repo 'tests\ui\Measure-Fps.ps1'
$fpsArgs = @('-ExeDir', (Abs $ExeDir), '-Video', (Abs $Video), '-Runs', $Runs, '-Seconds', $Seconds)
$work = Join-Path ([IO.Path]::GetTempPath()) "vlm_gpu_share_$PID"
New-Item $work -ItemType Directory -Force | Out-Null

# 함수 이름을 'Measure' 로 두면 PowerShell 기본 별칭 measure(Measure-Object)가 먼저 잡혀 아무것도 재지 않고 지나간다 (11-07 한 번 그랬다)
function Run-Fps([string]$tag) {
    "==== $tag ($(Get-Date -Format HH:mm:ss))"
    & pwsh -NoProfile -File $fpsScript @fpsArgs 2>&1 | Where-Object { $_ -match 'FPS' }
}
function Gpu-Memory { try { (nvidia-smi --query-gpu=memory.used --format=csv,noheader) -join ' / ' } catch { '?' } }

$version = try { (Invoke-RestMethod http://127.0.0.1:11434/api/version -TimeoutSec 3).version } catch { $null }
if (-not $version) { 'Ollama 가 떠 있지 않다 — 먼저 띄운다'; exit 1 }
"Ollama $version · GPU 메모리 $(Gpu-Memory)"
Run-Fps '기준선 1'
foreach ($mode in 'gpu', 'cpu') {
    $stop = Join-Path $work "stop_$mode"
    $log = Join-Path $work "load_$mode.txt"
    $p = Start-Process uv -ArgumentList @('run', '--no-project', '--python', '3.12', 'python', (Join-Path $PSScriptRoot 'gpu_load.py'), $mode, $stop, $log) -PassThru -WindowStyle Hidden
    Start-Sleep -Seconds 20   # 모델을 올리고 부하가 자리 잡게
    "  ($mode 부하 중 GPU 메모리 $(Gpu-Memory))"
    Run-Fps "VLM $mode 부하 중"
    New-Item $stop -ItemType File -Force | Out-Null
    $p.WaitForExit(300000) | Out-Null
    if (Test-Path $log) { Get-Content $log -Encoding utf8 | Select-Object -Last 1 }
}
Run-Fps '기준선 2'
"VLM 호출 기록: $work"
