using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DetectView
{
    /// <summary>VLM 단서의 묶음 — 화면 색만 가른다(docs/vlm-annotation.md '단서 표시'). 판정에는 쓰지 않는다</summary>
    internal enum VlmCueGroup
    {
        /// <summary>사고·고장 쪽 — 출동 차량·경광등·차로 위 사람·손상·문 열림</summary>
        Incident,
        /// <summary>정상 맥락 — 앞에 선 차·신호·주차</summary>
        Context,
        /// <summary>표시만 — 라바콘·삼각대, 박스에 차 없음</summary>
        Info,
    }

    /// <summary>VLM 이 그렇다고 답한 관찰 하나. Key 는 CSV 에 쓰는 이름, Name 은 화면 글자</summary>
    internal readonly record struct VlmCue(string Key, string Name, VlmCueGroup Group);

    /// <summary>VLM 의 관찰 한 번 — 4차 실험 질문(예·아니오·모름 12개 + 한국어 설명)의 답</summary>
    internal sealed class VlmObservation
    {
        public VlmObservation(string description, IReadOnlyDictionary<string, string> answers)
        {
            Description = description;
            Answers = answers;
            Cues = VlmPrompt.CuesOf(answers);
        }

        /// <summary>한국어 설명 — 지어낸 내용이 섞일 수 있어 화면엔 '참고용'으로 낸다 (멈춘 덤프트럭을 "주행 중"이라 쓴 적이 있다)</summary>
        public string Description { get; }

        /// <summary>항목 → 답 (yes · no · unknown, 차종은 그 목록)</summary>
        public IReadOnlyDictionary<string, string> Answers { get; }

        /// <summary>보이는 단서 — 사고·고장 쪽, 정상 맥락, 표시만 순</summary>
        public IReadOnlyList<VlmCue> Cues { get; }
    }

    internal enum VlmOutcome { Done, Failed, Skipped, Cancelled }

    /// <summary>VLM 일 한 건의 끝 — 관찰(끝남) 또는 사유(실패·건너뜀). 취소는 행에 붙이지 않는다</summary>
    internal sealed record VlmResult(VlmOutcome Outcome, VlmObservation? Observation = null, double Seconds = 0, string Reason = "")
    {
        public static readonly VlmResult Cancelled = new(VlmOutcome.Cancelled);
        public static VlmResult Ok(VlmObservation observation, double seconds) => new(VlmOutcome.Done, observation, seconds);
        public static VlmResult Fail(string reason) => new(VlmOutcome.Failed, Reason: reason);
        public static VlmResult Skip(string reason) => new(VlmOutcome.Skipped, Reason: reason);
    }

    /// <summary>VLM 을 부르다 실패했다 — 메시지는 로그 행에 그대로 보인다</summary>
    internal sealed class VlmException(string message, Exception? inner = null) : Exception(message, inner);

    /// <summary>VLM 을 부르는 쪽 — 실제는 <see cref="OllamaVlmClient"/>, UI 테스트는 Debug 전용 가짜(TestFaults.cs)</summary>
    internal interface IVlmClient
    {
        /// <summary>모델 이름 (툴팁·상태바 글자)</summary>
        string Model { get; }

        /// <summary>쓸 수 있으면 null, 아니면 사유 ('Ollama 가 꺼져 있음' 등)</summary>
        Task<string?> CheckAsync(CancellationToken ct);

        /// <summary>모델을 미리 올려 둔다 — 첫 유고 후보도 3초 안팎이 되게</summary>
        Task LoadAsync(CancellationToken ct);

        /// <summary>그림 한 장(JPEG)을 보고 관찰을 받는다. 실패는 <see cref="VlmException"/>, 시간 제한·취소는 OperationCanceledException</summary>
        Task<VlmObservation> ObserveAsync(byte[] jpeg, CancellationToken ct);

        /// <summary>모델을 내린다 — GPU 메모리(약 4 GB)를 바로 돌려준다</summary>
        Task UnloadAsync(CancellationToken ct);
    }

    /// <summary>
    /// VLM 에 보내는 질문·스키마·옵션과 답 읽기 (WPF·OpenCV 없음 — 실험 도구가 소스로 링크한다).
    /// **실험(experiments/VlmCheck/run_vlm.py 의 4차 — SYSTEM4 · make_schema4 · OPTIONS, 3배 크롭 D3)과 글자 하나까지 같게 둔다.**
    /// 실험 결과(단서가 쓸 만하다 — 출동 차량·경광등·차로 위 사람 / 신호·앞 줄·주차)는 이 질문에 대한 것이다.
    /// 바꾸면 VlmCheck 로 다시 재고 `TrackerCheck vlm` + `check_app_request.py` 로 둘이 같은지 본다
    /// </summary>
    internal static class VlmPrompt
    {
        public const string Model = "qwen3.5:4b";

        // 원본 문자열(""")은 소스 파일의 줄바꿈(CRLF/LF)을 그대로 담아 실험(LF)과 달라질 수 있다 → 줄을 \n 으로 잇는다
        public static readonly string SystemPrompt = string.Join("\n",
            "너는 도로 CCTV 화면을 보고 사실만 적는 관찰자다. 그림은 CCTV 화면의 일부를 키운 것이고 빨간 박스가 확인할 대상이다.",
            "판단하거나 추측하지 말고 그림에 보이는 것만 답한다. 보이지 않거나 확실하지 않으면 unknown 을 고른다. 화면에 겹친 글자는 무시한다.",
            "항목:",
            "- image_usable: 그림이 대상을 알아볼 만큼 선명한가(검은 화면·심한 흔들림·심한 흐림이면 no)",
            "- vehicle_in_box: 빨간 박스 안에 차량이 있는가",
            "- vehicle_type: 박스 안 차량의 종류(차량이 없으면 none)",
            "- emergency_vehicle_near: 박스 안이나 그 주변에 소방차·구급차·경찰차·견인차가 보이는가",
            "- flashing_lights: 경광등이나 비상등을 켠 차량이 보이는가",
            "- people_on_road: 차로 위(보도·횡단보도는 빼고)에 차 밖으로 나와 서 있거나 걷는 사람이 보이는가",
            "- visible_damage: 박스 안 차량에 찌그러지거나 부서진 곳이 보이거나, 도로에 파편이 보이는가",
            "- door_or_trunk_open: 박스 안 차량의 문·트렁크·보닛이 열려 있는가",
            "- cones_or_triangle: 라바콘이나 삼각대가 도로에 놓여 있는가",
            "- vehicles_stopped_ahead: 박스 안 차량 바로 앞(같은 차로)에 다른 차량이 바짝 붙어 줄지어 있는가",
            "- signal_or_stop_line: 신호등·정지선·횡단보도가 박스 안 차량 가까이 보이는가",
            "- parking_area: 박스 안 차량이 주차장이나 주차 칸 안에 있는가",
            "description_ko 는 보이는 것을 한국어 한두 문장으로 쓴다.");

        /// <summary>사용자 글 — 실험이 그림 구성("그림 1장: 1) 후보 주변을 키운 그림")과 요청을 줄바꿈으로 이은 것</summary>
        public const string UserText = "그림 1장: 1) 후보 주변을 키운 그림\n보이는 것만 JSON 으로 답하라.";

        public const string DescriptionKey = "description_ko";

        private static readonly string[] YesNo = ["yes", "no", "unknown"];
        private static readonly string[] VehicleTypes =
            ["car", "truck", "bus", "motorcycle", "bicycle", "person", "emergency_vehicle", "tow_truck", "work_vehicle", "none", "unknown"];

        /// <summary>스키마 항목과 허용 값 — 설명 다음에 이 순서로 쓴다 (Ollama 는 properties 순서대로 답을 쓴다 — 설명을 먼저 쓰게 한 것이 1차의 교훈)</summary>
        public static readonly (string Key, string[] Values)[] Fields =
        [
            ("image_usable", YesNo), ("vehicle_in_box", YesNo), ("vehicle_type", VehicleTypes),
            ("emergency_vehicle_near", YesNo), ("flashing_lights", YesNo), ("people_on_road", YesNo), ("visible_damage", YesNo),
            ("door_or_trunk_open", YesNo), ("cones_or_triangle", YesNo),
            ("vehicles_stopped_ahead", YesNo), ("signal_or_stop_line", YesNo), ("parking_area", YesNo),
        ];

        // 단서 — (항목, 그 답일 때, 단서). 화면 순서도 이 순서. '모름'·'아니오'는 단서가 아니다(박스 안 차량만 '아니오'가 단서 — 가짜 검출일 수 있다).
        // image_usable(선명한가)은 보이지 않는다 — 4차에서 키운 그림에 '아니오'를 자주 잘못 답했다
        private static readonly (string Field, string When, VlmCue Cue)[] CueTable =
        [
            ("emergency_vehicle_near", "yes", new("emergency_vehicle_near", "출동 차량", VlmCueGroup.Incident)),
            ("flashing_lights", "yes", new("flashing_lights", "경광등", VlmCueGroup.Incident)),
            ("people_on_road", "yes", new("people_on_road", "차로 위 사람", VlmCueGroup.Incident)),
            ("visible_damage", "yes", new("visible_damage", "손상", VlmCueGroup.Incident)),
            ("door_or_trunk_open", "yes", new("door_or_trunk_open", "문·트렁크 열림", VlmCueGroup.Incident)),
            ("vehicles_stopped_ahead", "yes", new("vehicles_stopped_ahead", "앞에 선 차", VlmCueGroup.Context)),
            ("signal_or_stop_line", "yes", new("signal_or_stop_line", "신호·정지선", VlmCueGroup.Context)),
            ("parking_area", "yes", new("parking_area", "주차 구역", VlmCueGroup.Context)),
            ("cones_or_triangle", "yes", new("cones_or_triangle", "라바콘·삼각대", VlmCueGroup.Info)),
            ("vehicle_in_box", "no", new("no_vehicle_in_box", "박스에 차 없음", VlmCueGroup.Info)),
        ];

        public static IReadOnlyList<VlmCue> CuesOf(IReadOnlyDictionary<string, string> answers) =>
            CueTable.Where(c => answers.TryGetValue(c.Field, out var v) && v == c.When).Select(c => c.Cue).ToArray();

        // 올릴 때(/api/generate)도 같은 값이어야 한다 — num_ctx 가 다르면 첫 호출에서 모델을 다시 올린다 (실험의 OPTIONS)
        private static void WriteOptions(Utf8JsonWriter w, bool withPredict)
        {
            w.WriteStartObject("options");
            w.WriteNumber("temperature", 0);
            w.WriteNumber("seed", 7);
            w.WriteNumber("num_ctx", 8192);
            if (withPredict) w.WriteNumber("num_predict", 768);
            w.WriteEndObject();
        }

        private static void WriteSchema(Utf8JsonWriter w)
        {
            w.WriteStartObject("format");
            w.WriteString("type", "object");
            w.WriteStartObject("properties");
            w.WriteStartObject(DescriptionKey);
            w.WriteString("type", "string");
            w.WriteEndObject();
            foreach (var (key, values) in Fields)
            {
                w.WriteStartObject(key);
                w.WriteString("type", "string");
                w.WriteStartArray("enum");
                foreach (var v in values) w.WriteStringValue(v);
                w.WriteEndArray();
                w.WriteEndObject();
            }
            w.WriteEndObject();
            w.WriteStartArray("required");
            w.WriteStringValue(DescriptionKey);
            foreach (var (key, _) in Fields) w.WriteStringValue(key);
            w.WriteEndArray();
            w.WriteEndObject();
        }

        private static byte[] Json(Action<Utf8JsonWriter> write)
        {
            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms))
            {
                w.WriteStartObject();
                write(w);
                w.WriteEndObject();
            }
            return ms.ToArray();
        }

        /// <summary>/api/chat 요청 본문 — 실험 4차 D3 와 같다 (keep_alive 만 앱 사정: 켜 있는 동안 모델을 붙들어 두는 시간)</summary>
        public static byte[] ChatRequest(string model, byte[] jpeg, string keepAlive) => Json(w =>
        {
            w.WriteString("model", model);
            w.WriteBoolean("stream", false);
            w.WriteString("keep_alive", keepAlive);
            WriteSchema(w);
            w.WriteStartArray("messages");
            w.WriteStartObject();
            w.WriteString("role", "system");
            w.WriteString("content", SystemPrompt);
            w.WriteEndObject();
            w.WriteStartObject();
            w.WriteString("role", "user");
            w.WriteString("content", UserText);
            w.WriteStartArray("images");
            w.WriteBase64StringValue(jpeg);
            w.WriteEndArray();
            w.WriteEndObject();
            w.WriteEndArray();
            WriteOptions(w, withPredict: true);
            w.WriteBoolean("think", false);   // 생각 모드는 끈다 — 1차에서 11~100초에 답을 못 낸 적이 많았다
        });

        /// <summary>/api/generate — 빈 글로 모델만 올린다 (실험의 데우기와 같은 옵션)</summary>
        public static byte[] LoadRequest(string model, string keepAlive) => Json(w =>
        {
            w.WriteString("model", model);
            w.WriteString("prompt", "");
            w.WriteString("keep_alive", keepAlive);
            WriteOptions(w, withPredict: false);
        });

        /// <summary>/api/generate — keep_alive 0 이면 모델을 바로 내린다</summary>
        public static byte[] UnloadRequest(string model) => Json(w =>
        {
            w.WriteString("model", model);
            w.WriteNumber("keep_alive", 0);
        });

        /// <summary>/api/chat 응답(스트리밍 아님)에서 관찰을 읽는다 — 스키마 밖의 값·빠진 항목은 실패(<see cref="VlmException"/>)</summary>
        public static VlmObservation ParseChatResponse(string body)
        {
            string content;
            try
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                if (root.TryGetProperty("error", out var err)) throw new VlmException($"Ollama 오류 — {err}");
                if (root.TryGetProperty("done_reason", out var why) && why.ValueKind == JsonValueKind.String && why.GetString() == "length")
                    throw new VlmException("답이 잘림 (출력 한도)");
                content = root.GetProperty("message").GetProperty("content").GetString() ?? "";
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
            {
                throw new VlmException("답을 읽지 못함 (응답 형식)", ex);
            }

            try
            {
                using var doc = JsonDocument.Parse(content);
                var o = doc.RootElement;
                if (o.ValueKind != JsonValueKind.Object) throw new VlmException("답을 읽지 못함 (JSON 객체 아님)");
                if (!o.TryGetProperty(DescriptionKey, out var d) || d.ValueKind != JsonValueKind.String)
                    throw new VlmException($"답을 읽지 못함 ({DescriptionKey} 없음)");
                var answers = new Dictionary<string, string>();
                foreach (var (key, values) in Fields)
                {
                    string? v = o.TryGetProperty(key, out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
                    if (v == null || Array.IndexOf(values, v) < 0) throw new VlmException($"답을 읽지 못함 ({key})");
                    answers[key] = v;
                }
                return new VlmObservation((d.GetString() ?? "").Trim(), answers);
            }
            catch (JsonException ex)
            {
                throw new VlmException("답을 읽지 못함 (JSON 아님)", ex);
            }
        }
    }

    /// <summary>
    /// 이 PC 의 Ollama(HTTP)로 VLM 을 부른다 — 앱은 Ollama 를 띄우지 않는다(사용자가 띄운다). 새 NuGet 없이 HttpClient + System.Text.Json 이라
    /// ONNX Runtime 충돌(CLAUDE.md 1번)과 무관하다. 한 번에 하나만 부른다(MainWindow.Vlm.cs 의 작업이 하나)
    /// </summary>
    internal sealed class OllamaVlmClient : IVlmClient
    {
        public const string Address = "http://127.0.0.1:11434";

        // 켜 있는 동안 모델을 붙들어 두는 시간 (요청마다 다시 잰다). 끄거나 앱을 닫으면 바로 내린다
        private const string KeepAlive = "30m";

        // 이 PC 안의 Ollama 만 부른다 — 시스템 프록시를 거치지 않게 끈다. 서버가 꺼져 있으면 연결이 곧바로 거절된다
        private static readonly HttpClient s_http = new(new SocketsHttpHandler { UseProxy = false, ConnectTimeout = TimeSpan.FromSeconds(2) })
        {
            BaseAddress = new Uri(Address),
            Timeout = Timeout.InfiniteTimeSpan,   // 시간 제한은 부르는 쪽 토큰으로 (첫 호출은 모델 올리기까지 6~8초)
        };

        public string Model => VlmPrompt.Model;

        public async Task<string?> CheckAsync(CancellationToken ct)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(3));
            try
            {
                using var res = await s_http.GetAsync("/api/tags", cts.Token).ConfigureAwait(false);
                if (!res.IsSuccessStatusCode) return $"Ollama 가 응답하지 않음 ({(int)res.StatusCode})";
                using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false));
                bool has = doc.RootElement.TryGetProperty("models", out var models) && models.ValueKind == JsonValueKind.Array &&
                           models.EnumerateArray().Any(m => m.TryGetProperty("name", out var n) && n.GetString() == Model);
                return has ? null : $"모델 {Model} 이 없음 — 터미널에서 ollama pull {Model}";
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return "Ollama 가 응답하지 않음 (3초)"; }
            catch (HttpRequestException) { return $"Ollama 가 꺼져 있음 ({Address}) — Ollama 를 띄운 뒤 다시 켜세요"; }
            catch (JsonException) { return "Ollama 답을 읽지 못함 (/api/tags)"; }
        }

        public async Task LoadAsync(CancellationToken ct)
        {
            using var res = await PostAsync("/api/generate", VlmPrompt.LoadRequest(Model, KeepAlive), ct).ConfigureAwait(false);
        }

        public async Task<VlmObservation> ObserveAsync(byte[] jpeg, CancellationToken ct)
        {
            string body;
            try
            {
                using var res = await PostAsync("/api/chat", VlmPrompt.ChatRequest(Model, jpeg, KeepAlive), ct).ConfigureAwait(false);
                body = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                if (res.StatusCode == HttpStatusCode.NotFound) throw new VlmException($"모델 {Model} 이 없음");
                if (!res.IsSuccessStatusCode) throw new VlmException($"Ollama 오류 {(int)res.StatusCode}");
            }
            catch (HttpRequestException ex)
            {
                throw new VlmException("Ollama 연결 안 됨", ex);
            }
            return VlmPrompt.ParseChatResponse(body);
        }

        public async Task UnloadAsync(CancellationToken ct)
        {
            using var res = await PostAsync("/api/generate", VlmPrompt.UnloadRequest(Model), ct).ConfigureAwait(false);
        }

        private static async Task<HttpResponseMessage> PostAsync(string path, byte[] json, CancellationToken ct)
        {
            using var content = new ByteArrayContent(json);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            return await s_http.PostAsync(path, content, ct).ConfigureAwait(false);
        }
    }
}
