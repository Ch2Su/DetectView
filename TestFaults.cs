using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace YOLO11_Test
{
    /// <summary>
    /// 시험용 결함 주입 (Debug 빌드 전용). UI 테스트(tests/ui/Test-ScreenHud.ps1)가 실제로 일으키기 어려운 실패 —
    /// 캡처 생성 실패·복구 불가 복제 상실·검출 예외 — 를 일으켜 복구·표시 경로를 끝까지 돌려 보게 한다.
    /// 환경 변수 DETECTVIEW_TEST_FAULTS 에 이름을 쉼표로 나열한다 (앱을 띄울 때만 읽는다):
    ///   hud-create-fail   [화면 HUD] 의 첫 캡처 생성이 실패한다 (이슈 #2 — 소스 없음으로 되돌리고 오류 창)
    ///   hud-capture-lost  HUD 의 첫 DXGI 캡처가 첫 화면 1.5초 뒤(화면이 그대로면 3초 뒤) 한 번 <see cref="ScreenCaptureLostException"/> 을
    ///                     던진다 (이슈 #1 — 새로 만들어 이어 감)
    ///   hud-detect-fail   HUD 검출이 첫 화면부터 3초 동안 예외를 낸다 (이슈 #1 — FPS 칸에 사유, 풀리면 되돌아옴)
    /// 훅은 모두 [Conditional("DEBUG")] 라 Release 에선 부르는 코드째 컴파일되지 않는다 (환경 변수도 읽지 않음).
    /// 훅은 HUD 캡처 루프 스레드에서만 부른다 — 루프는 한 번에 하나라 락을 두지 않는다
    /// </summary>
    internal static class TestFaults
    {
        public const string EnvVar = "DETECTVIEW_TEST_FAULTS";

        /// <summary>[루프] 첫 캡처를 만들기 직전 (MainWindow.ScreenLoop). 복구 중 다시 만들 때는 부르지 않는다</summary>
        [Conditional("DEBUG")]
        public static void HudCreate()
        {
#if DEBUG
            if (Has("hud-create-fail"))
                throw new InvalidOperationException($"시험용 결함: 캡처 생성 실패 ({EnvVar}=hud-create-fail)");
#endif
        }

        /// <summary>[루프] DXGI TryCapture 맨 앞 — 처음 불린 DXGI 캡처(HUD 의 첫 캡처)가 첫 화면 1.5초 뒤 한 번 상실을 던진다.
        /// 화면이 그대로면 첫 화면이 안 온다(갓 만든 복제는 화면이 바뀌어야 준다) → 그땐 처음 불린 지 3초 뒤에 던진다.
        /// TryCapture 는 새 화면이 없어도 0.1초마다 불리므로 시각으로 건다</summary>
        [Conditional("DEBUG")]
        public static void DxgiTryCapture(object capture)
        {
#if DEBUG
            if (s_lostDone || !Has("hud-capture-lost")) return;
            if (s_lostTarget == null) { s_lostTarget = capture; s_lostFirstCall = Stopwatch.GetTimestamp(); }
            if (!ReferenceEquals(capture, s_lostTarget)) return;   // 복구로 새로 만든 캡처는 대상이 아니다
            bool due = s_lostFirstFrame != 0
                ? Stopwatch.GetElapsedTime(s_lostFirstFrame).TotalMilliseconds >= 1500
                : Stopwatch.GetElapsedTime(s_lostFirstCall).TotalMilliseconds >= 3000;
            if (!due) return;
            s_lostDone = true;
            throw new ScreenCaptureLostException($"시험용 결함: 복구 불가 복제 상실 ({EnvVar}=hud-capture-lost)");
#endif
        }

        /// <summary>[루프] DXGI 캡처가 새 화면을 매핑했다 — 상실 대상의 첫 화면 시각을 남긴다</summary>
        [Conditional("DEBUG")]
        public static void DxgiFrame(object capture)
        {
#if DEBUG
            if (s_lostFirstFrame == 0 && ReferenceEquals(capture, s_lostTarget)) s_lostFirstFrame = Stopwatch.GetTimestamp();
#endif
        }

        /// <summary>[루프] HUD 검출 직전 — 처음 부른 때부터 3초 동안 예외</summary>
        [Conditional("DEBUG")]
        public static void HudDetect()
        {
#if DEBUG
            if (!Has("hud-detect-fail")) return;
            if (s_detectFirst == 0) s_detectFirst = Stopwatch.GetTimestamp();
            if (Stopwatch.GetElapsedTime(s_detectFirst).TotalMilliseconds < 3000)
                throw new InvalidOperationException($"시험용 결함: 검출 예외 ({EnvVar}=hud-detect-fail)");
#endif
        }

#if DEBUG
        private static readonly HashSet<string> s_faults = new(
            (Environment.GetEnvironmentVariable(EnvVar) ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries),
            StringComparer.OrdinalIgnoreCase);

        private static bool Has(string fault) => s_faults.Contains(fault);

        private static object? s_lostTarget;   // 상실을 일으킬 DXGI 캡처 (TryCapture 가 처음 불린 것)
        private static long s_lostFirstCall, s_lostFirstFrame;
        private static bool s_lostDone;        // 프로세스당 한 번
        private static long s_detectFirst;
#endif
    }
}
