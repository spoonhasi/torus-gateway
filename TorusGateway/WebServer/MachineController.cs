using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace TorusGateway.WebServer
{
    // Machine Data Model 조회/수정 (논문 Fig. 4, 5).
    // URL 경로는 TORUS 주소(data://machine/...)로, 쿼리스트링은 필터로 그대로 매핑하고
    // TORUS 결과를 가공 없이 반환한다.
    //   GET /machine/{경로}?{필터} → Api.getData
    //   PUT /machine/{경로}?{필터} → Api.updateData (body: TORUS Item JSON, 예: {"value":[12.34]})
    [ApiController]
    [Route("[controller]")]
    public class MachineController : ControllerBase
    {
        // 경로 세그먼트와 쿼리 키·값에 들어가면 안 되는 구분자.
        private static readonly char[] PathDelimiters = { '?', '&', '=' };
        private static readonly char[] QueryDelimiters = { '&', '=' };

        // 오류 메시지의 ' & 등이 ' &으로 바뀌지 않도록 하는 직렬화 옵션 (FileController와 공용).
        internal static readonly JsonSerializerOptions ErrorJsonOptions = new()
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        private readonly ILogger<MachineController> _logger;

        public MachineController(ILogger<MachineController> logger)
        {
            _logger = logger;
        }

        private ContentResult ErrorResponse(int statusCode, string errorMessage)
        {
            var body = JsonSerializer.Serialize(new { status = -1, error = errorMessage }, ErrorJsonOptions);
            return new ContentResult { Content = body, ContentType = "application/json", StatusCode = statusCode };
        }

        private static ContentResult JsonContent(string json) =>
            new() { Content = json, ContentType = "application/json", StatusCode = 200 };

        private IEnumerable<KeyValuePair<string, string>> QueryPairs() =>
            HttpContext.Request.Query.Select(p => new KeyValuePair<string, string>(p.Key, p.Value.ToString()));

        // URL 경로와 쿼리로 TORUS 주소와 필터를 만든다. 쿼리는 키·값 모두 가공 없이 'key=value'를 '&'로 잇는다.
        // 디코딩된 경로나 키·값에 구분자가 들어 있으면 필터에 키를 끼워 넣어 대상 설비 등을 바꿀 수 있으므로
        // (예: machine=1%26machine%3D2) ArgumentException으로 거부한다. 호출부가 400으로 매핑.
        internal static (string address, string filter) BuildAddressAndFilter(
            string? segments, IEnumerable<KeyValuePair<string, string>> query)
        {
            var segmentArray = (segments ?? "").Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segmentArray.Any(s => s.IndexOfAny(PathDelimiters) >= 0))
                throw new ArgumentException("Path must not contain '?', '&' or '='");

            var parts = new List<string>();
            foreach (var (key, value) in query)
            {
                if (key.IndexOfAny(QueryDelimiters) >= 0 || value.IndexOfAny(QueryDelimiters) >= 0)
                    throw new ArgumentException("Query keys and values must not contain '&' or '='");
                parts.Add(key + "=" + value);
            }
            return ("data://machine/" + string.Join("/", segmentArray), string.Join("&", parts));
        }

        // PUT body가 TORUS Item 형식({"value":[...]})인지 검사한다. 문제가 없으면 null, 있으면 오류 메시지.
        // TORUS(v2.3.3)는 value가 배열이 아닌 Item을 받으면 통신 매니저(mgrCommunication)가 크래시해
        // 연결된 모든 설비가 멈춘다. 그래서 TORUS에 넘기기 전에 루트 객체, 소문자 "value" 키, 비어 있지 않은
        // 배열을 요구한다. 원소 타입은 모델마다 다르므로(숫자/문자열) 제한하지 않는다.
        internal static string? ValidateUpdateBody(string body)
        {
            const string expected = "Body must be a JSON object with a non-empty \"value\" array, e.g. {\"value\":[12.34]}";
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.ValueKind != JsonValueKind.Object) return expected;
                if (!doc.RootElement.TryGetProperty("value", out var value)) return expected;
                if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() == 0) return expected;
                return null;
            }
            catch (JsonException)
            {
                return "Invalid JSON in request body";
            }
        }

        // GET / - 단순 alive 체크
        [HttpGet("/")]
        public ContentResult GetAlive() => JsonContent("{\"status\":\"ok\"}");

        // GET /health - 헬스 체크 (TORUS 연결과 무관한 정적 응답)
        [HttpGet("/health")]
        public ContentResult GetHealth() => JsonContent("{\"status\":0,\"value\":\"ok\"}");

        // GET /machineList - TORUS에 등록된 머신 목록. Api.getMachinesInfo 결과를 가공 없이 반환.
        [HttpGet("/machineList")]
        public ActionResult<string> GetMachineList()
        {
            try
            {
                return JsonContent(Torus.Torus.Instance.GetMachinesInfo());
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to query machine list");
                return ErrorResponse(500, "Error occurred while fetching machine list");
            }
        }

        // GET machine/{*segments}
        [HttpGet("{*segments}")]
        public ActionResult<string> GetTorusGetData(string? segments)
        {
            string address, filter;
            try { (address, filter) = BuildAddressAndFilter(segments, QueryPairs()); }
            catch (ArgumentException ex) { return ErrorResponse(400, ex.Message); }

            try
            {
                return JsonContent(Torus.Torus.Instance.GetData(address, filter));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to query machine data: {Segments}", segments);
                return ErrorResponse(500, "Error occurred while fetching machine data");
            }
        }

        // PUT machine/{*segments}
        [HttpPut("{*segments}")]
        public async Task<ActionResult<string>> PutTorusUpdateData(string? segments)
        {
            string address, filter;
            try { (address, filter) = BuildAddressAndFilter(segments, QueryPairs()); }
            catch (ArgumentException ex) { return ErrorResponse(400, ex.Message); }

            try
            {
                // body는 TORUS Item JSON 그대로 전달한다(값은 배열: {"value":[12.34]}).
                using var reader = new StreamReader(Request.Body);
                string body = await reader.ReadToEndAsync();

                string? invalid = ValidateUpdateBody(body);
                if (invalid != null) return ErrorResponse(400, invalid);

                return JsonContent(Torus.Torus.Instance.UpdateData(address, filter, body));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to update machine data: {Segments}", segments);
                return ErrorResponse(500, "Error occurred while updating machine data");
            }
        }
    }
}
