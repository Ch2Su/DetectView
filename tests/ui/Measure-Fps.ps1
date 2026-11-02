# 파일 재생 처리 FPS 측정 (테스트가 아니라 A/B 비교용 계측 — 실패 판정 없음)
#   상태바 Txt_InferenceFps 는 "프레임 읽기 + ProcessFrame" 시간의 역수를 지수 평활한 값이다 (재생 시계로 기다린 시간은 빠짐).
#   워밍업을 버리고 일정 간격으로 읽어 중앙값을 낸다. 실행마다 앱을 새로 띄운다.
#
#   pwsh tests\ui\Measure-Fps.ps1 -ExeDir <배포 폴더> [-Runs 3] [-Seconds 15]
# 주의: UI 테스트가 보고하는 HUD FPS 는 벤치마크로 쓰지 말 것 (CLAUDE.md). 이 스크립트는 파일 재생만 잰다
param(
    [string]$ExeDir,
    [string]$Video,
    [int]$Runs = 5,
    [int]$Seconds = 15,
    [int]$WarmupSeconds = 3
)
. (Join-Path $PSScriptRoot 'Common.ps1')

$ExeDir = Resolve-ExeDir $ExeDir
if (-not $Video) { $Video = Join-Path $RepoRoot 'publish_gpu_ver\test1.mp4' }
if (-not (Test-Path $Video)) { throw "테스트 영상이 없습니다: $Video" }

$medians = @()
for ($run = 1; $run -le $Runs; $run++) {
    $proc = $null
    try {
        $proc = Start-DetectView $ExeDir
        $root = $AE::FromHandle($proc.MainWindowHandle)
        $fps = ById $root 'Txt_InferenceFps'
        $status = ById $root 'Txt_Status'
        Open-VideoFile $root $Video
        if (-not (WaitFor { (NameOf $status) -like '파일:*' } 10000)) { throw '재생이 시작되지 않음' }

        Start-Sleep -Seconds $WarmupSeconds
        $samples = [System.Collections.Generic.List[double]]::new()
        $sw = [Diagnostics.Stopwatch]::StartNew()
        while ($sw.Elapsed.TotalSeconds -lt $Seconds) {
            if ((NameOf $fps) -match '([\d.]+)\s*FPS') { $samples.Add([double]$Matches[1]) }
            Start-Sleep -Milliseconds 250
        }
        $sorted = $samples | Sort-Object
        # [int]1.5 는 은행원 반올림으로 2 가 된다 → Floor
        $median = $sorted[[math]::Floor($sorted.Count / 2)]
        $medians += $median
        Note "실행 $run" ("중앙 {0:F1} FPS · 최소 {1:F1} · 최대 {2:F1} · 표본 {3}개" -f $median, $sorted[0], $sorted[-1], $sorted.Count)
    }
    finally { Stop-DetectView $proc }
    Start-Sleep -Seconds 2   # 앞 프로세스의 GPU 자원이 풀리게
}
$all = $medians | Sort-Object
Note '요약' ("실행별 중앙값 {0} → 중앙 {1:F1} FPS" -f (($medians | ForEach-Object { $_.ToString('F1') }) -join ' / '), $all[[math]::Floor($all.Count / 2)])
Complete-Test
