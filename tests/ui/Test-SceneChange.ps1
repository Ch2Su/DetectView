# 화면 전환 감지 UI 테스트 — 영상 위 배지와 추적 번호 (2026-11-03, SceneChange.cs · MainWindow.Scene.cs)
#   재생하면서 0.03초 간격으로 배지(Txt_SceneBadge — 숨겨져 있으면 UIA 에서 사라진다)·위치·번호(검출 수 칸의 UIA ItemType)를 기록해 그 열을 본다.
#   두 값을 읽는 사이 프레임이 넘어갈 수 있어 배지를 번호 앞뒤로 읽어 같은 때만 센다.
#
#   test4 (10.5~11.4초에 카메라가 돌아간다, 0~10.5초엔 차로에 멈춘 덤프트럭): 7초로 시크해 재생
#     1) 돌기 전(10.4초 전): 배지 없음, 멈춘 차에 '정지' 번호가 붙어 있다
#     2) 도는 동안: '화면 이동 중' 배지, 박스는 보이지만 번호는 없다 (가짜 트랙을 만들지 않으려고 추적기에 안 넣는다)
#     3) 안정된 뒤: '화면 전환 · 번호 새로 매김' 배지, 번호가 모두 돌기 전에 본 번호보다 크다 (추적기를 비웠다)
#     4) 몇 초 뒤(13.9초~): 배지가 사라진다 (13.43초에 한 프레임 크게 튀는 건 전환이 아니다)
#   test5 (29.63~29.98초에 스트림이 검게 끊겼다 같은 화면으로 돌아온다): 27초로 시크해 재생
#     5) 끊김 동안 '화면 끊김' 배지 (이동 중도 전환도 아님)
#     6) 끊김 전에 '정지' 로 나온 흰색 화물차의 번호가 끊김 뒤에도 그대로다 — 같은 화면이라 추적기를 비우지 않는다
#     7) '화면 전환' 배지가 한 번도 안 뜬다
#   8) 정지하면 배지가 사라진다
#
#   pwsh tests\ui\Test-SceneChange.ps1                              # bin\Debug 빌드
#   pwsh tests\ui\Test-SceneChange.ps1 -ExeDir publish_gpu_ver      # 배포본
param(
    [string]$ExeDir,
    [string]$Video4,   # 생략 시 publish_gpu_ver\test4.mp4
    [string]$Video5    # 생략 시 publish_gpu_ver\test5.mp4
)
. (Join-Path $PSScriptRoot 'Common.ps1')

$ExeDir = Resolve-ExeDir $ExeDir
if (-not $Video4) { $Video4 = Join-Path $RepoRoot 'publish_gpu_ver\test4.mp4' }
if (-not $Video5) { $Video5 = Join-Path $RepoRoot 'publish_gpu_ver\test5.mp4' }
foreach ($v in $Video4, $Video5) { if (-not (Test-Path $v)) { throw "테스트 영상이 없습니다: $v" } }

$MOVING = '● 화면 이동 중'; $BLANK = '● 화면 끊김'; $CHANGED = '● 화면 전환 · 번호 새로 매김'

# 위치 $FromMs 로 시크한 뒤 $ToMs 까지 재생하며 표본을 모은다. 표본: Pos(ms) · Badge('' 이면 없음) · Ids · Stationary · N(검출 수)
function Sample-Playback($Root, $Range, $Count, [double]$FromMs, [double]$ToMs, [int]$TimeoutMs) {
    $Range.SetValue($FromMs)
    $samples = [System.Collections.Generic.List[object]]::new()
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $TimeoutMs) {
        $b1 = ById $Root 'Txt_SceneBadge'
        $badge1 = if ($b1) { try { $b1.Current.Name } catch { $null } } else { '' }
        $pos = $Range.Current.Value
        $marks = Get-TrackMarks $Count
        $n = Get-DetectionCount $Count
        $b2 = ById $Root 'Txt_SceneBadge'
        $badge2 = if ($b2) { try { $b2.Current.Name } catch { $null } } else { '' }
        if ($null -ne $marks -and $null -ne $badge1 -and $badge1 -eq $badge2) {
            $samples.Add([pscustomobject]@{ Pos = $pos; Badge = $badge1; Ids = @($marks.Ids); Stationary = @($marks.Stationary); N = $n })
        }
        if ($pos -ge $ToMs) { break }
        Start-Sleep -Milliseconds 30
    }
    , $samples
}
function Fmt($Samples) { ($Samples | ForEach-Object { '{0:N1}s[{1}]' -f ($_.Pos / 1000), $_.Badge } | Select-Object -First 6) -join ' ' }

$proc = $null
try {
    $proc = Start-DetectView $ExeDir
    $root = $AE::FromHandle($proc.MainWindowHandle)
    $play   = ById $root 'Btn_PlayPause'
    $stop   = ById $root 'Btn_Stop'
    $status = ById $root 'Txt_Status'
    $count  = ById $root 'Txt_DetectionCount'
    $range  = (ById $root 'VideoSlider').GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern)

    # ===== test4: 카메라가 돌아간다 =====
    Open-VideoFile $root $Video4
    $ok = WaitFor { (NameOf $status) -like '파일:*' -and (NameOf $play) -eq '일시정지' } 10000
    Check 'test4 재생 시작' $ok "상태='$(NameOf $status)'"
    Check '평소엔 배지가 없음' ($null -eq (ById $root 'Txt_SceneBadge')) ''

    $s = Sample-Playback $root $range $count 7000 15200 25000
    Check '표본을 충분히 모음' ($s.Count -ge 40 -and $s[-1].Pos -ge 15000) "표본 $($s.Count)개 · 마지막 위치 $([int]$s[-1].Pos) ms"

    $before = @($s | Where-Object { $_.Pos -ge 7300 -and $_.Pos -lt 10400 })
    $badBefore = @($before | Where-Object { $_.Badge -ne '' })
    $stillBefore = @($before | Where-Object { $_.Stationary.Count -gt 0 })
    Check '돌기 전(10.4초 전): 배지 없음' ($before.Count -ge 10 -and $badBefore.Count -eq 0) "표본 $($before.Count)개 · 배지 있던 표본 $($badBefore.Count)개 $(Fmt $badBefore)"
    Check '돌기 전: 멈춘 덤프트럭에 정지 번호가 붙음' ($stillBefore.Count -gt 0) "정지 표본 $($stillBefore.Count)/$($before.Count)"
    $idsBefore = @($before | ForEach-Object { $_.Ids } | Where-Object { $_ })
    $maxBefore = if ($idsBefore.Count) { ($idsBefore | Measure-Object -Maximum).Maximum } else { 0 }

    $moving = @($s | Where-Object { $_.Badge -eq $MOVING })
    $movingNumbered = @($moving | Where-Object { $_.Ids.Count -gt 0 })
    $movingBoxes = @($moving | Where-Object { $_.N -gt 0 })
    Check '도는 동안: 화면 이동 중 배지' ($moving.Count -ge 5 -and $moving[0].Pos -ge 10300 -and $moving[-1].Pos -le 12000) "표본 $($moving.Count)개 · $(if ($moving.Count) { "$([int]$moving[0].Pos)~$([int]$moving[-1].Pos) ms" }) (카메라는 10.5~11.4초에 돈다)"
    Check '도는 동안: 박스는 보이되 번호는 없음' ($movingBoxes.Count -gt 0 -and $movingNumbered.Count -eq 0) "박스 있는 표본 $($movingBoxes.Count)개 · 번호 있던 표본 $($movingNumbered.Count)개"

    $changed = @($s | Where-Object { $_.Badge -eq $CHANGED })
    $changedNumbered = @($changed | Where-Object { $_.Ids.Count -gt 0 })
    $oldIds = @($changedNumbered | ForEach-Object { $_.Ids } | Where-Object { $_ -le $maxBefore })
    Check '안정된 뒤: 화면 전환 · 번호 새로 매김 배지' ($changed.Count -ge 5 -and $changed[0].Pos -ge 11000 -and $changed[-1].Pos -le 14200) "표본 $($changed.Count)개 · $(if ($changed.Count) { "$([int]$changed[0].Pos)~$([int]$changed[-1].Pos) ms" })"
    Check '안정된 뒤: 번호가 돌기 전에 본 번호보다 모두 큼 (추적기를 비웠다)' ($changedNumbered.Count -gt 0 -and $oldIds.Count -eq 0) "돌기 전 최대 #$maxBefore · 번호 있던 표본 $($changedNumbered.Count)개 · 옛 번호 $($oldIds.Count)개"

    $late = @($s | Where-Object { $_.Pos -ge 13900 })
    $lateBadge = @($late | Where-Object { $_.Badge -ne '' })
    Check '몇 초 뒤(13.9초~): 배지가 사라지고 번호는 붙음 (13.43초의 한 프레임 튐은 전환이 아님)' ($late.Count -ge 10 -and $lateBadge.Count -eq 0 -and @($late | Where-Object { $_.Ids.Count -gt 0 }).Count -gt 0) "표본 $($late.Count)개 · 배지 있던 표본 $($lateBadge.Count)개 $(Fmt $lateBadge)"

    # ===== test5: 스트림이 검게 끊겼다 돌아온다 =====
    Click $stop
    $null = WaitFor { (NameOf $status) -eq '정지됨' } 5000
    Check '정지: 배지가 사라짐' ($null -eq (ById $root 'Txt_SceneBadge')) ''

    Open-VideoFile $root $Video5
    $ok = WaitFor { (NameOf $status) -like '파일:*' -and (NameOf $play) -eq '일시정지' } 10000
    Check 'test5 재생 시작' $ok "상태='$(NameOf $status)'"
    $range = (ById $root 'VideoSlider').GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern)
    $s5 = Sample-Playback $root $range $count 27000 32600 25000
    Check '표본을 충분히 모음 (test5)' ($s5.Count -ge 40 -and $s5[-1].Pos -ge 32500) "표본 $($s5.Count)개 · 마지막 위치 $([int]$s5[-1].Pos) ms"

    $blank = @($s5 | Where-Object { $_.Badge -eq $BLANK })
    $other = @($s5 | Where-Object { $_.Badge -eq $MOVING -or $_.Badge -eq $CHANGED })
    Check '검은 끊김 동안: 화면 끊김 배지 (이동 중·전환 아님)' ($blank.Count -ge 3 -and $blank[0].Pos -ge 29400 -and $blank[-1].Pos -le 30800 -and $other.Count -eq 0) "끊김 표본 $($blank.Count)개 · $(if ($blank.Count) { "$([int]$blank[0].Pos)~$([int]$blank[-1].Pos) ms" }) · 다른 배지 $($other.Count)개 $(Fmt $other)"

    $stillIds = @($s5 | Where-Object { $_.Pos -ge 28300 -and $_.Pos -lt 29500 } | ForEach-Object { $_.Stationary } | Where-Object { $_ } | Select-Object -Unique)
    $afterStill = @($s5 | Where-Object { $_.Pos -ge 30800 } | ForEach-Object { $_.Stationary } | Where-Object { $_ } | Select-Object -Unique)
    $kept = @($afterStill | Where-Object { $stillIds -contains $_ })
    Check '끊김 앞뒤로 멈춘 화물차의 번호가 그대로 (같은 화면이라 추적기를 안 비움)' ($stillIds.Count -gt 0 -and $kept.Count -gt 0) "끊김 전 정지 번호=[$($stillIds -join ' ')] → 끊김 뒤 정지 번호=[$($afterStill -join ' ')]"
    Check '끊김 뒤 번호가 다시 붙고 배지가 사라짐' (@($s5 | Where-Object { $_.Pos -ge 31000 -and $_.Ids.Count -gt 0 -and $_.Badge -eq '' }).Count -gt 0) ''
}
catch { Check '예외' $false $_.Exception.Message }
finally { Stop-DetectView $proc }

Complete-Test
