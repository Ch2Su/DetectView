# 앱(DetectView)이 GPU 메모리를 얼마나 쓰는가 — 앱 없음 → 띄움 → 재생 중(GPU) → [-Trt] 재생 중(TensorRT) → 닫은 뒤 (2026-11-26, README 결과 14 ①)
#
#   pwsh experiments/VlmCheck/Measure-AppGpuMemory.ps1 [-ExeDir publish] [-Video publish/test1.mp4] [-Trt]
#
# VLM 에 줄 수 있는 GPU 메모리를 따지려고 쟀다(조언의 '객체 탐지 3.1 GB'는 VLM 의 크기였다 — 앱은 재생 중 0.4~0.5 GiB).
# 전체 사용량은 nvidia-smi, 앱 프로세스 몫은 성능 카운터 'GPU Process Memory … Dedicated Usage'(WDDM 에선 nvidia-smi 가 프로세스별 값을 안 준다).
# 앱 창이 한 번 떴다 닫힌다. 상대 경로는 저장소 루트 기준. 다른 GPU 작업(Ollama 모델 등)이 올라가 있으면 '앱 없음' 값에 섞인다 — 늘어난 양으로 읽는다.
param([string]$ExeDir = 'publish', [string]$Video = 'publish/test1.mp4', [switch]$Trt)
[Console]::OutputEncoding = [Text.Encoding]::UTF8
. (Join-Path $PSScriptRoot '../../tests/ui/Common.ps1')
function Gpu { [int]((nvidia-smi --query-gpu=memory.used --format=csv,noheader,nounits) | Select-Object -First 1) }
function Sample([int]$Sec) {
    $v = [System.Collections.Generic.List[int]]::new()
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($sw.Elapsed.TotalSeconds -lt $Sec) { $v.Add((Gpu)); Start-Sleep -Milliseconds 400 }
    $s = @($v | Sort-Object)
    [pscustomobject]@{ Min = $s[0]; Max = $s[-1]; Med = $s[[math]::Floor($s.Count / 2)] }
}
function Proc-Mem($ProcId) {
    $bs = [char]92
    try {
        $c = Get-Counter ("{0}GPU Process Memory(pid_{1}_*){0}Dedicated Usage" -f $bs, $ProcId) -ErrorAction Stop
        [math]::Round((($c.CounterSamples | Measure-Object CookedValue -Sum).Sum) / 1MB)
    } catch { -1 }
}
# 추론 장치 콤보에서 항목을 고른다 (tests/ui/Test-NpuSwitch.ps1 의 Select-Device 와 같은 것)
function Select-Device($Root, [string]$Name) {
    $combo = Need $Root 'InferenceSelector'
    $ec = $combo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
    $ec.Expand(); Start-Sleep -Milliseconds 300
    $item = $combo.FindFirst($Tree::Descendants, (New-Object $PC ($AE::NameProperty, $Name)))
    $enabled = $null -ne $item -and $item.Current.IsEnabled
    if ($enabled) { $item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select() }
    $ec.Collapse(); Start-Sleep -Milliseconds 200
    return $enabled
}
$exe = Resolve-ExeDir $ExeDir
$video = if ([IO.Path]::IsPathRooted($Video)) { $Video } else { Join-Path $RepoRoot $Video }
$idle = Sample 4
"앱 없음: {0} MiB ({1}~{2})" -f $idle.Med, $idle.Min, $idle.Max
$proc = $null
try {
    $proc = Start-DetectView $exe
    $root = $proc.Root
    Start-Sleep -Seconds 4
    $a = Sample 3
    $proc.Refresh()
    "앱 뜬 뒤(재생 전): {0} MiB (+{1}) · 프로세스 전용 {2} MiB · 제목 '{3}'" -f $a.Med, ($a.Med - $idle.Med), (Proc-Mem $proc.Id), $proc.MainWindowTitle
    $status = ById $root 'Txt_Status'
    Open-VideoFile $root $video
    if (-not (WaitFor { (NameOf $status) -like '파일:*' } 10000)) { throw '재생이 시작되지 않음' }
    Start-Sleep -Seconds 3
    $b = Sample 10
    "재생 중(DirectML): {0} MiB ({1}~{2}, +{3}) · 프로세스 전용 {4} MiB · {5}" -f $b.Med, $b.Min, $b.Max, ($b.Med - $idle.Med), (Proc-Mem $proc.Id), (NameOf (ById $root 'Txt_InferenceFps'))
    if ($Trt) {
        if (Select-Device $root 'TensorRT') {
            $ok = WaitFor { $proc.Refresh(); $proc.MainWindowTitle -like '*(TensorRT*' } 120000 250
            Start-Sleep -Seconds 4
            $c = Sample 10
            $proc.Refresh()
            "재생 중(TensorRT, 전환 {0}): {1} MiB ({2}~{3}, +{4}) · 프로세스 전용 {5} MiB · {6} · 제목 '{7}'" -f $ok, $c.Med, $c.Min, $c.Max, ($c.Med - $idle.Med), (Proc-Mem $proc.Id), (NameOf (ById $root 'Txt_InferenceFps')), $proc.MainWindowTitle
        } else { 'TensorRT 항목이 막혀 있음' }
    }
}
finally { if ($proc) { Stop-DetectView $proc } }
Start-Sleep -Seconds 3
$z = Sample 3
"닫은 뒤: {0} MiB" -f $z.Med
