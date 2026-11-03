# VFR 파일 재생 테스트 — 화면녹화 webm 처럼 메타데이터를 믿을 수 없는 파일의 경로
#   - 재생부터 시작하고 길이는 백그라운드 분석(ScanFrames)으로 확정되는지
#   - 원본 속도로 재생되는지 (예전엔 임시값 30fps 로 고정돼 60fps 파일이 0.41배속이었다)
#   - 앞·뒤 시크가 목표 시각에 서는지 — 길이 정보가 없는 파일이라 프레임 시크 대신 grab·다시 열기로 간다
#     (예전엔 Set(PosFrames) 가 스트림을 망가뜨려 시크하면 곧바로 EOF 였다)
#   - 재생 중 뒤로 시크(다시 열어 처음부터 따라감) 직후 SAHI → 그 위치의 프레임을 분석하는지
#   - EOF 뒤 재생 → 처음부터 (EOF 상태의 캡처는 더 못 읽으므로 다시 열어야 한다)
#   - 길이는 있고 fps 가 타임베이스 값(1000)으로 읽히는 VFR 녹화도 같은 grab 경로로 목표 시각에 서는지 (이슈 #7, 두 번째 파일)
#
# media\vfr60_nodur.webm: OpenCV 로 만든 60fps VP8 webm(20초, 1200프레임)의 Segment Info Duration(0x4489)
#   요소를 같은 길이의 Void(0xEC)로 덮은 것. FRAME_COUNT 가 쓰레기값(-5.5e17)이 되어 metaReliable=false 로 간다.
#   원래의 실제 화면녹화 테스트 파일(recording.webm)은 없어져서 대신 쓴다
# media\vfr_fps1000_dur.webm (2026-10-31, 0.86 MB): 길이는 있는 옛 화면 녹화 꼴 — OpenCV 가 FPS=1000·FRAME_COUNT=24007 로 읽는다.
#   PyAV 19.0.2(FFmpeg 8) 로 VP8 640x360 을 타임베이스 1 ms 에 누적 지터 시각(간격 정규분포 33.3±9 ms, 16~60 ms 로 자름, 난수 씨앗 20261031)으로
#   인코딩했다: 0~6초 움직임(~30fps) · 6~12초 정지 화면(커서 깜빡임만, 0.5초마다 1장 — 6,000·6,507·…·9,015·9,490·…·11,989) ·
#   12~24초 움직임, 모두 550장(마지막 23,974 ms). 키프레임 60장마다, alt-ref·lag 없음. 지터 때문에 FFmpeg 가 표준 프레임레이트를
#   못 맞춰 r_frame_rate = 1000/1 이 된다. Matroska 먹서가 쓴 TrackEntry DefaultDuration(0x23E383, 30fps)은 그대로 두면 OpenCV 가
#   FPS=30·FRAME_COUNT=720 으로 읽어 CFR 경로로 가므로, 같은 길이의 Void(0xEC)로 덮었다. 화면엔 시각·프레임 번호·진행 막대가 찍혀 있다
#   (같은 방법으로 다시 만들면 Matroska 의 무작위 UID 16바이트만 다르다). 만든 스크립트와 판을 고정한 명령: media\make_vfr_fps1000_dur.py
#   — 아래 검사는 이 파일의 프레임 시각(9,015·9,490·13,024·13,521·16,009 ms)에 기대므로 다시 만들면 그 스크립트가 찍는 시각과 맞춰 볼 것
#
#   pwsh tests\ui\Test-VfrPlayback.ps1
#   pwsh tests\ui\Test-VfrPlayback.ps1 -ExeDir publish
param([string]$ExeDir)
. (Join-Path $PSScriptRoot 'Common.ps1')

$ExeDir = Resolve-ExeDir $ExeDir
$video = Join-Path $PSScriptRoot 'media\vfr60_nodur.webm'
$name = Split-Path $video -Leaf

$proc = $null
try {
    $proc = Start-DetectView $ExeDir
    $root = $AE::FromHandle($proc.MainWindowHandle)
    $status = ById $root 'Txt_Status'
    $range = (ById $root 'VideoSlider').GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern)

    Open-VideoFile $root $video
    # 분석이 끝나면 "분석 중(재생 가능): …" 이 "파일: …" 로 바뀐다
    $ok = WaitFor { (NameOf $status) -eq "파일: $name" } 20000
    Check 'VFR 분석 완료' $ok "상태='$(NameOf $status)'"

    $max = $range.Current.Maximum
    Check '길이 실측' ([Math]::Abs($max - 20000) -lt 300) ('{0:N0} ms (실제 20,000)' -f $max)

    $rate = Measure-PlaybackRate $range 2000
    Check '재생 속도' ($rate -gt 0.9 -and $rate -lt 1.1) ('{0:N2} 배속' -f $rate)

    # 시크: 이 파일은 길이 정보가 없어 프레임 시크 대신 grab 으로 따라간다 (예전엔 시크하면 스트림이 망가져 곧바로 EOF).
    # 슬라이더는 시크가 적용된 뒤에야 재생 루프가 다시 움직이므로, 목표를 지나 움직이기 시작할 때까지 기다린 뒤 본다
    $status0 = { NameOf $status }
    foreach ($t in @(@{ Target = 12000; Label = '앞으로' }, @{ Target = 3000; Label = '뒤로(다시 열기)' })) {
        $range.SetValue($t.Target)
        $moved = WaitFor { $range.Current.Value -gt $t.Target + 100 } 5000
        $v = $range.Current.Value
        # 시크 직후엔 재생 시계를 새로 맞추는 첫 몇 프레임이 몰려 나와 배속이 들쭉날쭉하다 — 가라앉은 뒤 잰다
        Start-Sleep -Milliseconds 400
        Check "VFR 시크 $($t.Label)" ($moved -and $v -lt $t.Target + 1500 -and (& $status0) -ne '재생 완료') `
            ('{0:N0} ms (목표 {1:N0}), 상태={2}' -f $v, $t.Target, (& $status0))
        $rate = Measure-PlaybackRate $range 2000
        Check "시크($($t.Label)) 후 재생 속도" ($rate -gt 0.9 -and $rate -lt 1.1) ('{0:N2} 배속' -f $rate)
    }

    # 재생 중 뒤로 시크 직후 SAHI → 시크한 위치를 분석한다. 이 파일은 뒤로 가면 다시 열어 처음부터 따라가서 시크 처리가 길다 —
    # 예전엔 그 사이 누르면 시크 전 프레임을 분석했다. 분석한 위치는 상태 문구 끝 (mm:ss).
    # 뒤쪽(14초 넘어)에서 12.5초로 되감아 처음부터 따라갈 거리(750프레임)를 늘리고, 루프가 시크를 꺼낸 뒤에 누르도록 35ms 기다린다.
    # 다만 이 합성 파일은 다시 열기 + 750장 grab 이 수십 ms 로 짧아서, 대기 논리만 옛것으로 되돌린 빌드로 재 보니(10-31, 5번씩)
    # 35ms 에서 1번, 70·120ms 에선 0번만 옛 버그를 잡았다 — 회귀 판별은 Test-FilePlayback 2-2(35ms 에서 5번 모두)가 맡고,
    # 여기선 되감은 위치를 분석하는지를 본다
    $play = ById $root 'Btn_PlayPause'
    $sahi = ById $root 'Btn_Sahi'
    $count = ById $root 'Txt_DetectionCount'
    $range.SetValue(14000)
    $past = WaitFor { $range.Current.Value -gt 14100 } 5000
    $range.SetValue(12500)
    Start-Sleep -Milliseconds 35
    # Test-FilePlayback 2-2 와 같다 — 되감기가 끝나 재생이 이어졌으면 12,500 을 넘어 있다(시크한 프레임 자체가 12,500 이라 그 한 장만 보인 때는 못 가린다)
    $pre = $range.Current.Value
    Click $sahi
    $ok = WaitFor { (NameOf $count) -like '*(SAHI)*' -and (& $status0) -like '정밀 분석 완료*' } 30000
    $st = & $status0
    $sec = if ($st -match '\((\d+):(\d\d)\)$') { [int]$Matches[1] * 60 + [int]$Matches[2] } else { -1 }
    # 시크 전 프레임(14초 넘음)을 분석했으면 (00:14) 라 걸린다
    Check '뒤로 시크 직후 SAHI → 그 위치 분석' ($ok -and $sec -ge 12 -and $sec -le 13) "상태='$st' (시크 12,500 ms)"
    if (-not $past) { Note '뒤로 시크 직후 SAHI 판별력' '14초를 못 넘긴 채 되감음 — 시크 전 프레임도 12~13초일 수 있어 옛 버그를 가리지 못함' }
    elseif ([Math]::Abs($pre - 12500) -gt 0.5) { Note '뒤로 시크 직후 SAHI 판별력' "누르기 전에 되감기가 끝남(슬라이더 $([int]$pre) ms) — 이번 실행은 옛 버그를 가리지 못함" }
    $null = WaitFor { $play.Current.IsEnabled } 30000
    Click $play   # SAHI 가 멈춘 재생을 다시 — 아래 EOF 단계는 재생 중이어야 한다

    # EOF 뒤 재생 → 처음부터: 이 파일은 다시 열어서 돌아가야 한다 (EOF 상태의 캡처는 더 읽을 수 없음)
    $range.SetValue(19000)
    $ok = WaitFor { (& $status0) -eq '재생 완료' } 10000
    Check 'EOF 도달' $ok "상태='$(& $status0)'"
    Click $play
    $ok = WaitFor { $range.Current.Value -gt 300 -and $range.Current.Value -lt 5000 } 5000
    Start-Sleep -Milliseconds 1000
    Check 'EOF 후 재생 → 처음부터 계속 재생' ($ok -and (NameOf $play) -eq '일시정지' -and $range.Current.Value -lt 5000) `
        ('위치={0:N0} ms 버튼={1} 상태={2}' -f $range.Current.Value, (NameOf $play), (& $status0))

    # ---- 길이는 있고 fps 가 타임베이스 값(1000)으로 읽히는 VFR 녹화 (이슈 #7) ----
    # 메타데이터를 못 믿어 분석으로 가고, 시크 탐침이 어긋나(OpenCV 가 파일 끝까지 따라감) 길이는 grab 스캔으로 잰다.
    # 예전엔 분석 뒤 실측 평균 fps(22.9)로 Set(PosFrames) 해 OpenCV 가 '처음부터 N번째 프레임'에 섰다 — 6~12초 정지 구간 뒤의 목표는
    # 2~3.5초 지나쳤고 정밀화는 앞으로만 가서 되돌아오지 못했다 (콘솔로 같은 흐름을 재 보니(10-31) 16초 → 18.06, 13초 → 15.76,
    # 9초 → 12.51, 멈춘 채 13.5초 → 16.23). 지금은 길이 없는 파일처럼 grab·다시 열기로 간다 (16.009 · 13.024 · 9.015 · 13.521)
    $video2 = Join-Path $PSScriptRoot 'media\vfr_fps1000_dur.webm'
    $name2 = Split-Path $video2 -Leaf
    Open-VideoFile $root $video2
    $ok = WaitFor { (NameOf $status) -eq "파일: $name2" } 20000
    Check 'fps1000 분석 완료' $ok "상태='$(NameOf $status)'"
    $max = $range.Current.Maximum
    Check 'fps1000 길이 실측' ([Math]::Abs($max - 24000) -lt 300) ('{0:N0} ms (마지막 프레임 23,974 · 컨테이너 24,007)' -f $max)
    # 슬라이더는 분석이 끝난 뒤의 프레임부터 움직인다 — 막 바뀐 문구를 보고 곧바로 재면 시작값이 0 에 머문 채라 배속이 부푼다
    Start-Sleep -Milliseconds 300
    $rate = Measure-PlaybackRate $range 2000   # 0~6초는 움직임 구간(~30fps) — 정지 구간에선 슬라이더가 0.5초씩 건너뛰어 배속을 못 잰다
    Check 'fps1000 재생 속도' ($rate -gt 0.9 -and $rate -lt 1.1) ('{0:N2} 배속' -f $rate)

    # 앞·뒤 시크 — 목표를 정지 구간 뒤 움직임 구간에 둔다. 옛 방식은 2초 넘게 지나쳐 '목표 + 1.5초' 를 넘는다(위치로 가린다 —
    # 이 360p 합성 파일은 grab 이 장당 0.1 ms 안팎이라 옛 방식도 시크가 50 ms 안에 끝나 걸린 시간으로는 못 가린다)
    foreach ($t in @(@{ Target = 16000; Label = '앞으로' }, @{ Target = 13000; Label = '뒤로(다시 열기)' })) {
        $range.SetValue($t.Target)
        $moved = WaitFor { $range.Current.Value -gt $t.Target + 100 } 5000
        $v = $range.Current.Value
        Start-Sleep -Milliseconds 400
        Check "fps1000 시크 $($t.Label)" ($moved -and $v -lt $t.Target + 1500 -and (& $status0) -ne '재생 완료') `
            ('{0:N0} ms (목표 {1:N0}), 상태={2}' -f $v, $t.Target, (& $status0))
        $rate = Measure-PlaybackRate $range 2000
        Check "fps1000 시크($($t.Label)) 후 재생 속도" ($rate -gt 0.9 -and $rate -lt 1.1) ('{0:N2} 배속' -f $rate)
    }

    # 정지 구간으로 되감기: 9,015 ms 프레임에 서고 다음 프레임 9,490 이 0.5초 뒤에 나오므로, 슬라이더가 목표를 처음 넘는 값은 9.4~10.5초다.
    # 9.4초 아래면 위치를 PosMsec 가 아니라 프레임 수로 세는 경로(메타데이터를 믿는 CFR 경로)로 열린 것 — 이 표본이 이슈 #7 경로를 타지 않는다는 뜻.
    # 옛 방식은 12.5초에 섰다
    $range.SetValue(9000)
    $moved = WaitFor { $range.Current.Value -gt 9100 } 5000
    $v = $range.Current.Value
    Check 'fps1000 정지 구간으로 시크' ($moved -and $v -ge 9400 -and $v -lt 10500) ('{0:N0} ms (목표 9,000 → 9,015 프레임, 다음 프레임 9,490)' -f $v)

    # 멈춘 채 시크 → 한 장 보기 → SAHI 가 그 위치를 분석 (grab 경로에서 한 장 보기와 SAHI 의 기다림이 맞물리는지). 옛 방식은 16초대 프레임이었다
    Click $play   # 일시정지
    $null = WaitFor { (NameOf $play) -eq '재생' } 2000
    $range.SetValue(13500)
    Start-Sleep -Milliseconds 300
    Click $sahi
    $ok = WaitFor { (NameOf $count) -like '*(SAHI)*' -and (& $status0) -like '정밀 분석 완료*' } 30000
    $st = & $status0
    $sec = if ($st -match '\((\d+):(\d\d)\)$') { [int]$Matches[1] * 60 + [int]$Matches[2] } else { -1 }
    Check 'fps1000 멈춘 채 시크 → SAHI 가 그 위치 분석' ($ok -and $sec -eq 13) "상태='$st' (시크 13,500 ms → 13,521 프레임)"
    $null = WaitFor { $play.Current.IsEnabled } 30000
    Click $play   # SAHI 가 멈춘 재생을 다시 — 아래 EOF 단계는 재생 중이어야 한다

    # EOF 뒤 재생 → 처음부터: 이 파일도 이제 grab 경로라 다시 열어서 돌아간다 (예전엔 Set(PosFrames, 0) 이었다)
    $range.SetValue(23000)
    $ok = WaitFor { (& $status0) -eq '재생 완료' } 10000
    Check 'fps1000 EOF 도달' $ok "상태='$(& $status0)'"
    Click $play
    $ok = WaitFor { $range.Current.Value -gt 300 -and $range.Current.Value -lt 5000 } 5000
    Start-Sleep -Milliseconds 1000
    Check 'fps1000 EOF 후 재생 → 처음부터 계속 재생' ($ok -and (NameOf $play) -eq '일시정지' -and $range.Current.Value -lt 5000) `
        ('위치={0:N0} ms 버튼={1} 상태={2}' -f $range.Current.Value, (NameOf $play), (& $status0))
}
catch { Check '예외' $false $_.Exception.Message }
finally { Stop-DetectView $proc }

Complete-Test
