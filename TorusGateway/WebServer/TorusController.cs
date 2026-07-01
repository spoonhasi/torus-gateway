using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace TorusGateway.WebServer
{
    // TORUS의 GetData/UpdateData를 그대로 노출하는 pass-through 컨트롤러.
    // URL 세그먼트(/machine/...)는 TORUS 내부 주소 data://machine/...에 1:1 매핑됨.
    [ApiController]
    [Route("machine")]
    public class TorusController : ControllerBase
    {
        // 한글/특수문자가 \uXXXX로 이스케이프되지 않도록 공용 옵션.
        internal static readonly JsonSerializerOptions JsonSerOptions = new()
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };
        internal static readonly JsonWriterOptions JsonWriterOpts = new()
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        private readonly ILogger<TorusController> _logger;

        public TorusController(ILogger<TorusController> logger)
        {
            _logger = logger;
        }

        private ContentResult ErrorResponse(int statusCode, string errorMessage)
        {
            var body = JsonSerializer.Serialize(new { status = -1, error = errorMessage }, JsonSerOptions);
            return new ContentResult { Content = body, ContentType = "application/json", StatusCode = statusCode };
        }

        private (string address, string filter) BuildAddressAndFilter(string segments)
        {
            var segmentArray = segments.Split('/', StringSplitOptions.RemoveEmptyEntries);
            string address = "data://machine/" + string.Join("/", segmentArray);
            // TORUS 필터 키는 모두 소문자이므로, 클라이언트가 대소문자를 섞어 보내도 키를 소문자로 정규화한다.
            // (값은 변경하지 않는다 — 경로 등 대소문자 의미 보존)
            string filter = string.Join("&", HttpContext.Request.Query.Select(p => $"{p.Key.ToLowerInvariant()}={p.Value}"));
            return (address, filter);
        }

        // GET / - 단순 alive 체크
        [HttpGet("/")]
        public ContentResult GetAlive()
        {
            return new ContentResult
            {
                Content = "{\"status\":\"ok\"}",
                ContentType = "application/json",
                StatusCode = 200
            };
        }

        // GET machine/{*segments}
        [HttpGet("{*segments}")]
        public ActionResult<string> GetTorusGetData(string segments)
        {
            try
            {
                var (address, filter) = BuildAddressAndFilter(segments);

                string getDataResult = Torus.Torus.Instance.GetData(address, filter);
                return new ContentResult
                {
                    Content = FilterStatusAndValue(getDataResult),
                    ContentType = "application/json",
                    StatusCode = 200
                };
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, "Invalid JSON in TORUS response: {Segments}", segments);
                return ErrorResponse(502, "Invalid JSON response from TORUS");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to query machine data: {Segments}", segments);
                return ErrorResponse(500, "Error occurred while fetching machine data");
            }
        }

        // TORUS GetData 응답에서 status와 value만 남기고 나머지 키(type, timestamp 등)는 제거.
        // 추가로 value가 길이 1짜리 배열이면 배열을 해제해 단일 값으로 만든다.
        // 단, datatype 키 값에 "list"가 포함되어 있으면 배열을 유지한다 (빈 리스트/단일원소 리스트 구분용).
        // unwrapSingletonArray=false를 넘기면 datatype 여부와 무관하게 배열을 항상 유지한다.
        // ignoreListType=true를 넘기면 datatype이 list여도 단일 원소 배열을 해제한다 (예: PLC 신호 단건 조회).
        internal static string FilterStatusAndValue(string json, bool unwrapSingletonArray = true, bool ignoreListType = false)
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return json;

            bool isListType = false;
            if (doc.RootElement.TryGetProperty("datatype", out var datatypeProp)
                && datatypeProp.ValueKind == JsonValueKind.String)
            {
                string? dt = datatypeProp.GetString();
                if (dt != null && dt.Contains("list", StringComparison.OrdinalIgnoreCase))
                    isListType = true;
            }

            using var ms = new MemoryStream();
            using var writer = new Utf8JsonWriter(ms, JsonWriterOpts);
            writer.WriteStartObject();
            int? errorStatusCode = null;
            if (doc.RootElement.TryGetProperty("status", out var statusProp))
            {
                writer.WritePropertyName("status");
                statusProp.WriteTo(writer);
                if (statusProp.ValueKind == JsonValueKind.Number
                    && statusProp.TryGetInt32(out int code) && code != 0)
                {
                    errorStatusCode = code;
                }
            }
            if (doc.RootElement.TryGetProperty("value", out var valueProp))
            {
                writer.WritePropertyName("value");
                if (unwrapSingletonArray
                    && (!isListType || ignoreListType)
                    && valueProp.ValueKind == JsonValueKind.Array
                    && valueProp.GetArrayLength() == 1)
                {
                    valueProp[0].WriteTo(writer);
                }
                else
                {
                    valueProp.WriteTo(writer);
                }
            }
            if (errorStatusCode.HasValue)
                writer.WriteString("error", TorusStatusCodes.GetDescription(errorStatusCode.Value));
            writer.WriteEndObject();
            writer.Flush();
            return Encoding.UTF8.GetString(ms.ToArray());
        }

        // body 루트의 대소문자 변형("Value", "VALUE" 등) 키를 소문자 "value"로 정규화.
        // TORUS는 정확히 소문자 "value"만 인식하므로, 클라이언트 편의를 위해 게이트웨이에서 변환한다.
        // 이미 정확한 "value" 키가 있으면 우선하고 변형 키는 건드리지 않는다(충돌 시 정식 키 유지).
        internal static string NormalizeValueKey(string json)
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return json;

            bool hasExact = false;
            string? variantKey = null;
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Name == "value") { hasExact = true; }
                else if (variantKey == null
                         && string.Equals(prop.Name, "value", StringComparison.OrdinalIgnoreCase))
                {
                    variantKey = prop.Name;
                }
            }
            if (variantKey == null || hasExact) return json;

            using var ms = new MemoryStream();
            using var writer = new Utf8JsonWriter(ms, JsonWriterOpts);
            writer.WriteStartObject();
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                writer.WritePropertyName(prop.Name == variantKey ? "value" : prop.Name);
                prop.Value.WriteTo(writer);
            }
            writer.WriteEndObject();
            writer.Flush();
            return Encoding.UTF8.GetString(ms.ToArray());
        }

        // TORUS Item의 "value" 키는 항상 배열이어야 한다. (예: {"value": [42]})
        // 클라이언트 편의를 위해 단일 값({"value": 42})이 오면 자동으로 배열로 감싼다.
        internal static string WrapValueToArray(string json)
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return json;

            if (!doc.RootElement.TryGetProperty("value", out var valueProp))
                return json;

            if (valueProp.ValueKind == JsonValueKind.Array)
                return json;

            using var ms = new MemoryStream();
            using var writer = new Utf8JsonWriter(ms, JsonWriterOpts);
            writer.WriteStartObject();
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                writer.WritePropertyName(prop.Name);
                if (prop.Name == "value")
                {
                    writer.WriteStartArray();
                    prop.Value.WriteTo(writer);
                    writer.WriteEndArray();
                }
                else
                {
                    prop.Value.WriteTo(writer);
                }
            }
            writer.WriteEndObject();
            writer.Flush();
            return Encoding.UTF8.GetString(ms.ToArray());
        }

        // TORUS UpdateData 응답에서 status만 남긴다. status != 0이면 error 설명도 함께 반환.
        internal static string FilterStatusOnly(string json)
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return json;

            using var ms = new MemoryStream();
            using var writer = new Utf8JsonWriter(ms, JsonWriterOpts);
            writer.WriteStartObject();
            int? errorStatusCode = null;
            if (doc.RootElement.TryGetProperty("status", out var statusProp))
            {
                writer.WritePropertyName("status");
                statusProp.WriteTo(writer);
                if (statusProp.ValueKind == JsonValueKind.Number
                    && statusProp.TryGetInt32(out int code) && code != 0)
                {
                    errorStatusCode = code;
                }
            }
            if (errorStatusCode.HasValue)
                writer.WriteString("error", TorusStatusCodes.GetDescription(errorStatusCode.Value));
            writer.WriteEndObject();
            writer.Flush();
            return Encoding.UTF8.GetString(ms.ToArray());
        }

        // POST machine/{*segments}
        [HttpPost("{*segments}")]
        public async Task<ActionResult<string>> PostTorusUpdateData(string segments)
        {
            bool bodyParseDone = false;
            try
            {
                var (address, filter) = BuildAddressAndFilter(segments);

                // 비동기적으로 원시 데이터를 읽어옴
                using var reader = new StreamReader(Request.Body);
                string body = await reader.ReadToEndAsync();
                body = NormalizeValueKey(body);
                body = WrapValueToArray(body);
                bodyParseDone = true;

                string updateDataResult = Torus.Torus.Instance.UpdateData(address, filter, body);
                return new ContentResult
                {
                    Content = FilterStatusOnly(updateDataResult),
                    ContentType = "application/json",
                    StatusCode = 200
                };
            }
            catch (JsonException ex)
            {
                if (!bodyParseDone)
                {
                    _logger.LogWarning(ex, "Invalid JSON in request body: {Segments}", segments);
                    return ErrorResponse(400, "Invalid JSON in request body");
                }
                _logger.LogError(ex, "Invalid JSON in TORUS response: {Segments}", segments);
                return ErrorResponse(502, "Invalid JSON response from TORUS");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to update machine data: {Segments}", segments);
                return ErrorResponse(500, "Error occurred while updating machine data");
            }
        }
    }
}
