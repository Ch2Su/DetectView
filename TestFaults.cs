using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OpenCvSharp;

namespace DetectView
{
    /// <summary>
    /// 시험용 결함 주입 (Debug 빌드 전용). UI 테스트(tests/ui/Test-ScreenHud.ps1 · Test-VlmAnnotation.ps1)가 실제로 일으키기 어려운 실패 —
    /// 캡처 생성 실패·복구 불가 복제 상실·검출 예외 — 를 일으켜 복구·표시 경로를 끝까지 돌려 보게 하고, 진짜 Ollama 없이 VLM 설명을 돌려 본다.
    /// 환경 변수 DETECTVIEW_TEST_FAULTS 에 이름을 쉼표로 나열한다 (앱을 띄울 때만 읽는다):
    ///   hud-create-fail   [화면 HUD] 의 첫 캡처 생성이 실패한다 (이슈 #2 — 소스 없음으로 되돌리고 오류 창)
    ///   hud-capture-lost  HUD 의 첫 DXGI 캡처가 첫 화면 1.5초 뒤(화면이 그대로면 3초 뒤) 한 번 <see cref="ScreenCaptureLostException"/> 을
    ///                     던진다 (이슈 #1 — 새로 만들어 이어 감)
    ///   hud-detect-fail   HUD 검출이 첫 화면부터 3초 동안 예외를 낸다 (이슈 #1 — FPS 칸에 사유, 풀리면 되돌아옴)
    ///   vlm-fake          VLM 설명이 진짜 Ollama 대신 0.6초 뒤 정해진 답을 낸다 — 단서 출동 차량·차로 위 사람·앞에 선 차, 설명에 받은 그림 크기
    ///   vlm-down          VLM 설명이 켜지기는 하지만 부를 때마다 0.1초 뒤 '연결 안 됨'으로 실패한다
    ///   vlm-absent        VLM 설명을 켤 때의 확인이 'Ollama 가 꺼져 있음'으로 실패한다
    /// HUD 훅은 모두 [Conditional("DEBUG")] 라 Release 에선 부르는 코드째 컴파일되지 않는다 (환경 변수도 읽지 않음). HUD 훅은 HUD 캡처 루프 스레드에서만
    /// 부른다 — 루프는 한 번에 하나라 락을 두지 않는다. 가짜 VLM(<see cref="VlmClient"/>)은 값을 돌려줘야 해서 #if DEBUG 로 감쌌다(부르는 쪽도 — MainWindow.Vlm.cs)
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
        /// <summary>[UI] 가짜 VLM — vlm-fake · vlm-down · vlm-absent 중 하나가 있으면 그것, 없으면 null(진짜 Ollama)</summary>
        public static IVlmClient? VlmClient() =>
            Has("vlm-fake") ? new FakeVlm(FakeVlmMode.Answer)
            : Has("vlm-down") ? new FakeVlm(FakeVlmMode.Down)
            : Has("vlm-absent") ? new FakeVlm(FakeVlmMode.Absent)
            : null;

        private enum FakeVlmMode { Answer, Down, Absent }

        // 정해진 답 — 받은 JPEG 를 풀어 그 크기를 설명에 넣는다: 테스트가 크롭(긴 변 512)과 그림이 실제로 갔는지까지 본다.
        // 0.6초 기다리는 것은 테스트가 '분석 중'을 볼 틈
        private sealed class FakeVlm(FakeVlmMode mode) : IVlmClient
        {
            public string Model => "가짜 VLM (시험용)";

            public Task<string?> CheckAsync(CancellationToken ct) =>
                Task.FromResult(mode == FakeVlmMode.Absent ? $"Ollama 가 꺼져 있음 (시험용 결함 {EnvVar}=vlm-absent)" : null);

            public Task LoadAsync(CancellationToken ct) => Task.CompletedTask;

            public Task UnloadAsync(CancellationToken ct) => Task.CompletedTask;

            public async Task<VlmObservation> ObserveAsync(byte[] jpeg, CancellationToken ct)
            {
                await Task.Delay(mode == FakeVlmMode.Down ? 100 : 600, ct);
                if (mode == FakeVlmMode.Down) throw new VlmException($"Ollama 연결 안 됨 (시험용 결함 {EnvVar}=vlm-down)");
                using var img = Cv2.ImDecode(jpeg, ImreadModes.Color);
                var answers = VlmPrompt.Fields.ToDictionary(f => f.Key, f => f.Key switch
                {
                    "image_usable" or "vehicle_in_box" or "emergency_vehicle_near" or "people_on_road" or "vehicles_stopped_ahead" => "yes",
                    "vehicle_type" => "truck",
                    _ => "no",
                });
                return new VlmObservation($"시험용 가짜 VLM 답 — 그림 {img.Width}×{img.Height}", answers);
            }
        }

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
