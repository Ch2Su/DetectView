# YOLO 모델(FP32 ONNX)로 NVIDIA TensorRT for RTX 용 FP16 짝 모델(<이름>.fp16.onnx — 입출력은 FP32 그대로)을 만든다.
#   pwsh tools\Make-Fp16Model.ps1                                   # 루트 best.onnx → best.fp16.onnx (git 추적 대상)
#   pwsh tools\Make-Fp16Model.ps1 -Model publish\best.onnx          # 배포 폴더 모델을 바꿨을 때 — 짝도 다시 만들어야 한다
#   pwsh tools\Make-Fp16Model.ps1 -Model ... -Bench                 # 만든 뒤 NpuBench 로 DirectML·TensorRT 속도·검출 일치 확인 (1~2분)
#
# 앱의 '추론 장치'에서 TensorRT 를 고르면 이 짝으로 돈다(MainWindow.Trt.cs). 짝 모델에는 원본 MD5(fp16_source_md5)가 들어간다 —
# 원본을 바꾸면 짝이 어긋났다는 걸 그걸로 안다. 바꾸는 방법·판은 tools\fp16_convert.py 머리말.
# 필요: uv (python 은 PATH 에 없다 — uv 가 격리 환경을 만들고 처음 한 번 패키지를 내려받는다).
param(
    [string]$Model = 'best.onnx',
    [string]$Out,                       # 기본: 같은 폴더의 <이름>.fp16.onnx
    [switch]$Bench
)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$env:PYTHONIOENCODING = 'utf-8'
$env:PYTHONDONTWRITEBYTECODE = '1'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path

# 상대 경로는 저장소 루트 기준 (테스트 스크립트와 같게)
function Resolve-RepoPath([string]$Path) {
    if ([IO.Path]::IsPathRooted($Path)) { return $Path }
    return (Join-Path $repo $Path)
}

$modelPath = Resolve-RepoPath $Model
if (-not (Test-Path $modelPath)) { throw "모델이 없습니다: $modelPath" }
$outPath = if ($Out) { Resolve-RepoPath $Out } else { [IO.Path]::ChangeExtension($modelPath, '.fp16.onnx') }
if (-not (Get-Command uv -ErrorAction SilentlyContinue)) {
    throw 'uv 가 필요합니다 (winget install astral-sh.uv). python 은 따로 설치하지 않아도 된다'
}

# 바꾸는 도구의 판을 고정한다 — 이 판으로 만든 모델을 TensorRT for RTX 에서 재 봤다 (experiments\NpuBench\README.md 결과 6)
$uvArgs = @('run', '--no-project', '--python', '3.12',
            '--with', 'onnxruntime==1.31.1', '--with', 'onnx', '--with', 'sympy', '--with', 'numpy',
            (Join-Path $PSScriptRoot 'fp16_convert.py'), '--model', $modelPath, '--out', $outPath)

$sw = [Diagnostics.Stopwatch]::StartNew()
& uv @uvArgs
if ($LASTEXITCODE -ne 0) { throw "FP16 변환 실패 (종료 코드 $LASTEXITCODE)" }
"변환 {0:F0}초" -f $sw.Elapsed.TotalSeconds

if ($Bench) {
    # DirectML(원본 FP32) 기준으로 TensorRT(원본 · FP16 짝)의 검출 일치도와 시간을 잰다. 테스트 영상은 <저장소>\publish\test1~5.mp4
    $proj = Join-Path $repo 'experiments\NpuBench\NpuBench.csproj'
    dotnet build $proj -c Release --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw 'NpuBench 빌드 실패' }
    $exe = Get-ChildItem (Join-Path $repo 'experiments\NpuBench\bin\Release') -Recurse -Filter NpuBench.exe | Select-Object -First 1
    $noise = 'VerifyEachNodeIsAssignedToAnEp|ng with verbose|e\.g\. ORT explicitly|^\s*$|^[IWEF]20\d\d|ep_event_profiling|EndProfiling|to add to OrtProfiling|^WARNING:'
    & $exe.FullName $modelPath --trt-fp16 $outPath --gpu-only 2>&1 | ForEach-Object { "$_" } | Where-Object { $_ -notmatch $noise }
    if ($LASTEXITCODE -ne 0) { throw "NpuBench 실패 (종료 코드 $LASTEXITCODE)" }
}
