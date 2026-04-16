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

        // ── ncMemoryPath/content: NC 파일 내용을 JSON value로 주고받는 엔드포인트 ──
        // 기존 FileController(/file/machine/ncpath)는 바이너리 파일 전송용이고,
        // 이 엔드포인트는 텍스트 NC 프로그램을 JSON body로 편하게 읽고/쓰는 용도.

        private (int machineID, string ncPath) ExtractNcMemoryPathParams()
        {
            int machineID = -1;
            string ncPath = "";
            foreach (var param in HttpContext.Request.Query)
            {
                if (param.Key == "machine") _ = int.TryParse(param.Value, out machineID);
                else if (param.Key == "ncMemoryPath") ncPath = param.Value.ToString() ?? "";
            }
            return (machineID, ncPath);
        }

        private ContentResult? ValidateNcMemoryPathParams(int machineID, string ncPath)
        {
            if (machineID < 1) return ErrorResponse(400, "Invalid 'machine' parameter");
            if (string.IsNullOrWhiteSpace(ncPath)) return ErrorResponse(400, "'ncMemoryPath' parameter is empty");
            if (ncPath.Contains("..")) return ErrorResponse(400, "Invalid NC path");
            return null;
        }

        // GET /machine/ncMemoryPath/content?machine=1&ncMemoryPath=program/O0001.nc
        // NC 파일 내용을 다운로드하여 JSON value로 반환
        [HttpGet("ncMemoryPath/content")]
        public ActionResult<string> GetNcFileContent()
        {
            string? tempDirectory = null;
            try
            {
                var (machineID, ncPath) = ExtractNcMemoryPathParams();
                var validationError = ValidateNcMemoryPathParams(machineID, ncPath);
                if (validationError != null) return validationError;

                string uniqueName = DateTime.Now.ToString("yyyyMMddHHmmssfff")
                                  + "_" + Guid.NewGuid().ToString("N").Substring(0, 8);
                string absolutePath = Path.GetFullPath(FileController.TempDirDownload + "/" + uniqueName, Directory.GetCurrentDirectory());
                tempDirectory = absolutePath;
                Directory.CreateDirectory(absolutePath);

                string downloadResult = Torus.Torus.Instance.DownloadFile(ncPath, absolutePath + "/", machineID);
                using var downloadDoc = JsonDocument.Parse(downloadResult);
                if (downloadDoc.RootElement.GetProperty("status").GetInt32() != 0)
                    return ErrorResponse(502, "Failed to download file from NC machine");

                string fileName = Path.GetFileName(ncPath);
                string filePath = absolutePath + "/" + fileName;
                if (!System.IO.File.Exists(filePath))
                    return ErrorResponse(404, "File not found");

                string content = System.IO.File.ReadAllText(filePath);
                var jsonOptions = new JsonSerializerOptions
                {
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                };
                var response = JsonSerializer.Serialize(new { status = 0, value = content }, jsonOptions);
                return new ContentResult { Content = response, ContentType = "application/json", StatusCode = 200 };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "NC file content read failed");
                return ErrorResponse(500, "Error occurred during NC file content read");
            }
            finally
            {
                if (tempDirectory != null) try { Directory.Delete(tempDirectory, true); } catch { }
            }
        }

        // PUT /machine/ncMemoryPath/content?machine=1&ncMemoryPath=program/O0001.nc
        // body의 "value"를 NC 파일 내용으로 작성하여 업로드
        [HttpPut("ncMemoryPath/content")]
        public async Task<ActionResult<string>> PutNcFileContent()
        {
            string? tempDirectory = null;
            try
            {
                var (machineID, ncPath) = ExtractNcMemoryPathParams();
                var validationError = ValidateNcMemoryPathParams(machineID, ncPath);
                if (validationError != null) return validationError;

                using var reader = new StreamReader(Request.Body);
                string body = await reader.ReadToEndAsync();

                // body에서 "value" 추출. 배열이면 첫 번째 요소, 문자열이면 그대로.
                using var doc = JsonDocument.Parse(body);
                if (!doc.RootElement.TryGetProperty("value", out var valueProp))
                    return ErrorResponse(400, "'value' field is required");

                string fileContent;
                if (valueProp.ValueKind == JsonValueKind.Array)
                    fileContent = valueProp[0].GetString() ?? "";
                else
                    fileContent = valueProp.GetString() ?? "";

                // 임시 파일에 저장 후 TORUS로 업로드
                string uniqueName = DateTime.Now.ToString("yyyyMMddHHmmssfff")
                                  + "_" + Guid.NewGuid().ToString("N").Substring(0, 8);
                string tempDir = Path.GetFullPath(FileController.TempDirUpload + "/" + uniqueName, Directory.GetCurrentDirectory());
                tempDirectory = tempDir;
                Directory.CreateDirectory(tempDir);

                string fileName = Path.GetFileName(ncPath);
                string filePath = tempDir + "/" + fileName;
                await System.IO.File.WriteAllTextAsync(filePath, fileContent);

                string uploadResult = Torus.Torus.Instance.UploadFile(filePath, ncPath, machineID);
                using var uploadDoc = JsonDocument.Parse(uploadResult);
                if (uploadDoc.RootElement.GetProperty("status").GetInt32() != 0)
                    return ErrorResponse(502, "Failed to upload file to NC machine");

                return new ContentResult { Content = uploadResult, ContentType = "application/json", StatusCode = 200 };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "NC file content write failed");
                return ErrorResponse(500, "Error occurred during NC file content write");
            }
            finally
            {
                if (tempDirectory != null) try { Directory.Delete(tempDirectory, true); } catch { }
            }
        }

        // DELETE /machine/ncMemoryPath/content?machine=1&ncMemoryPath=program/O0001.nc
        // NC 파일 삭제
        [HttpDelete("ncMemoryPath/content")]
        public ActionResult<string> DeleteNcFile()
        {
            try
            {
                var (machineID, ncPath) = ExtractNcMemoryPathParams();
                var validationError = ValidateNcMemoryPathParams(machineID, ncPath);
                if (validationError != null) return validationError;

                string deleteResult = Torus.Torus.Instance.DeleteFile(ncPath, machineID);
                using var doc = JsonDocument.Parse(deleteResult);
                if (doc.RootElement.GetProperty("status").GetInt32() != 0)
                    return ErrorResponse(502, "Failed to delete file from NC machine");

                return new ContentResult { Content = deleteResult, ContentType = "application/json", StatusCode = 200 };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "NC file delete failed");
                return ErrorResponse(500, "Error occurred while deleting NC file");
            }
        }
    }
}
