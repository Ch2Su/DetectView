# YOLO 모델(FP32 ONNX)로 AMD NPU 용 짝 모델(<이름>.npu.onnx, 기본 A16W8·가중치 채널별 QDQ)을 만든다.
#   pwsh tools\Make-NpuModel.ps1                                    # 루트 best.onnx → best.npu.onnx (git 추적 대상)
#   pwsh tools\Make-NpuModel.ps1 -Model publish\best.onnx   # 배포 폴더 모델을 바꿨을 때 — 짝도 다시 만들어야 한다
#   pwsh tools\Make-NpuModel.ps1 -Model ... -Bench                  # 만든 뒤 NpuBench 로 NPU 정확도·속도 확인 (몇 분)
#
# 짝 모델에는 원본 MD5(npu_source_md5)가 들어간다. 원본을 바꾸면 짝이 어긋났다는 걸 그걸로 안다.
# 필요: uv (python 은 PATH 에 없다 — uv 가 격리 환경을 만들고 처음 한 번 패키지를 내려받는다).
# 보정 데이터 기본값은 publish\test1~5.mp4 각 20장. 다른 현장 영상이면 -Calib 로 그 영상·이미지 폴더를 주는 게 낫다.
param(
    [string]$Model = 'best.onnx',
    [string]$Out,                       # 기본: 같은 폴더의 <이름>.npu.onnx
    [string[]]$Calib,                   # 보정용 영상·이미지·이미지 폴더
    [ValidateSet('a16w8-pc', 'a16w8', 'a8w8', 'a8w8-pc', 'a8w8-pct', 'a8w8-pc-pct')]
    [string]$Mode = 'a16w8-pc',         # 방식별 실측은 tools\npu_quantize.py 머리말
    [int]$PerVideo = 20,
    [switch]$Bench
)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$env:PYTHONIOENCODING = 'utf-8'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path

# 상대 경로는 저장소 루트 기준 (테스트 스크립트와 같게)
function Resolve-RepoPath([string]$Path) {
    if ([IO.Path]::IsPathRooted($Path)) { return $Path }
    return (Join-Path $repo $Path)
}

$modelPath = Resolve-RepoPath $Model
if (-not (Test-Path $modelPath)) { throw "모델이 없습니다: $modelPath" }
$outPath = if ($Out) { Resolve-RepoPath $Out } else { [IO.Path]::ChangeExtension($modelPath, '.npu.onnx') }
if (-not (Get-Command uv -ErrorAction SilentlyContinue)) {
    throw 'uv 가 필요합니다 (winget install astral-sh.uv). python 은 따로 설치하지 않아도 된다'
}

# 양자화기 버전을 고정한다 — 이 조합으로 만든 모델이 VitisAI EP 에서 돈다는 걸 확인했다 (experiments\NpuBench\README.md)
$uvArgs = @('run', '--no-project', '--python', '3.12',
            '--with', 'onnxruntime==1.23.2', '--with', 'onnx', '--with', 'numpy', '--with', 'opencv-python-headless',
            (Join-Path $PSScriptRoot 'npu_quantize.py'),
            '--model', $modelPath, '--out', $outPath, '--mode', $Mode, '--per-video', $PerVideo)
if ($Calib) { $uvArgs += '--calib'; $uvArgs += @($Calib | ForEach-Object { Resolve-RepoPath $_ }) }

$sw = [Diagnostics.Stopwatch]::StartNew()
& uv @uvArgs
if ($LASTEXITCODE -ne 0) { throw "양자화 실패 (종료 코드 $LASTEXITCODE)" }
"양자화 {0:F0}초" -f $sw.Elapsed.TotalSeconds

if ($Bench) {
    # GPU(원본 FP32) 기준으로 NPU·CPU(양자화 모델)의 검출 일치도와 시간을 잰다. NPU 는 처음 한 번 1분쯤 컴파일한다
    $proj = Join-Path $repo 'experiments\NpuBench\NpuBench.csproj'
    dotnet build $proj -c Release --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw 'NpuBench 빌드 실패' }
    $exe = Get-ChildItem (Join-Path $repo 'experiments\NpuBench\bin\Release') -Recurse -Filter NpuBench.exe | Select-Object -First 1
    $env:XLNX_ONNX_EP_REPORT_FILE = 'vitisai_ep_report.json'   # NPU 노드 배치 보고서 (EP 가 시작할 때 읽는다)
    # EP·컴파일러의 진행 로그는 수천 줄이라 걸러서 보여 준다 (전체 로그가 필요하면 NpuBench.exe 를 직접 실행)
    $noise = 'Invalid NodeIndex|PartitionPass\.cpp|DeviceReassignmentPass|VerifyEachNodeIsAssignedToAnEp|xir::|^\s*$|^[IWEF]20\d\d|ep_event_profiling|EndProfiling|to add to OrtProfiling|aiecompiler|^WARNING:|^INFO:|^DEBUG:'
    & $exe.FullName $modelPath $outPath 20 2>&1 | ForEach-Object { "$_" } | Where-Object { $_ -notmatch $noise }
    if ($LASTEXITCODE -ne 0) { throw "NpuBench 실패 (종료 코드 $LASTEXITCODE)" }
}
