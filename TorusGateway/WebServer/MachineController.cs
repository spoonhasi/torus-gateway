using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace TorusGateway.WebServer
{
    [ApiController]
    [Route("[controller]")]
    public class MachineController : ControllerBase
    {
        private readonly ILogger<MachineController> _logger;

        public MachineController(ILogger<MachineController> logger)
        {
            _logger = logger;
        }

        private ContentResult ErrorResponse(int statusCode, string errorMessage)
        {
            var body = JsonSerializer.Serialize(new { status = -1, error = errorMessage });
            return new ContentResult { Content = body, ContentType = "application/json", StatusCode = statusCode };
        }

        private (string address, string filter) BuildAddressAndFilter(string segments)
        {
            var segmentArray = segments.Split('/', StringSplitOptions.RemoveEmptyEntries);
            string address = "data://machine/" + string.Join("/", segmentArray);
            string filter = string.Join("&", HttpContext.Request.Query.Select(p => $"{p.Key}={p.Value}"));
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
                    Content = getDataResult,
                    ContentType = "application/json",
                    StatusCode = 200
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to query machine data: {Segments}", segments);
                return ErrorResponse(500, "Error occurred while fetching machine data");
            }
        }

        // TORUS Item의 "value" 키는 항상 배열이어야 한다. (예: {"value": [42]})
        // 클라이언트 편의를 위해 단일 값({"value": 42})이 오면 자동으로 배열로 감싼다.
        private static string WrapValueToArray(string json)
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return json;

            if (!doc.RootElement.TryGetProperty("value", out var valueProp))
                return json;

            if (valueProp.ValueKind == JsonValueKind.Array)
                return json;

            using var ms = new MemoryStream();
            using var writer = new Utf8JsonWriter(ms);
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

        // PUT machine/{*segments}
        [HttpPut("{*segments}")]
        public async Task<ActionResult<string>> PutTorusUpdateData(string segments)
        {
            try
            {
                var (address, filter) = BuildAddressAndFilter(segments);

                // 비동기적으로 원시 데이터를 읽어옴
                using var reader = new StreamReader(Request.Body);
                string body = await reader.ReadToEndAsync();
                body = WrapValueToArray(body);

                string updateDataResult = Torus.Torus.Instance.UpdateData(address, filter, body);
                return new ContentResult
                {
                    Content = updateDataResult,
                    ContentType = "application/json",
                    StatusCode = 200
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to update machine data: {Segments}", segments);
                return ErrorResponse(500, "Error occurred while updating machine data");
            }
        }
    }
}
