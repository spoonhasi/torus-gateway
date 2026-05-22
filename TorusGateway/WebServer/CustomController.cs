using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace TorusGateway.WebServer
{
    // 이 앱이 중간에 처리하는(합성) 엔드포인트들을 모아둔 컨트롤러.
    // 단순 pass-through(GetData/UpdateData)는 TorusController에 둔다.
    [ApiController]
    public class CustomController : ControllerBase
    {
        internal const string TempDirDownload = "ncTempFileForDownloading";
        internal const string TempDirUpload = "ncTempFileForUploading";

        private readonly ILogger<CustomController> _logger;

        public CustomController(ILogger<CustomController> logger)
        {
            _logger = logger;
        }

        // 앱 시작 시 호출. 비정상 종료 등으로 남은 이전 세션의 임시 디렉토리를 정리한다.
        // OnCompleted 콜백이 실행되지 못한 경우(프로세스 강제 종료, 크래시)에 대비.
        public static void CleanupOrphanedTempDirs()
        {
            foreach (var root in new[] { TempDirDownload, TempDirUpload })
            {
                try
                {
                    string fullRoot = Path.GetFullPath(root, Directory.GetCurrentDirectory());
                    if (!Directory.Exists(fullRoot)) continue;
                    foreach (var sub in Directory.EnumerateDirectories(fullRoot))
                    {
                        try { Directory.Delete(sub, true); }
                        catch (Exception ex)
                        {
                            // 사용 중이면 다음 시작 때 다시 시도. 누적 추적용으로 로그만 남김.
                            FileLogger.WriteWarn($"Orphan temp dir cleanup failed: {sub} - {ex.Message}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    FileLogger.WriteWarn($"Orphan temp root access failed: {root} - {ex.Message}");
                }
            }
        }

        private ContentResult ErrorResponse(int statusCode, string errorMessage)
        {
            var body = JsonSerializer.Serialize(new { status = -1, error = errorMessage }, TorusController.JsonSerOptions);
            return new ContentResult { Content = body, ContentType = "application/json", StatusCode = statusCode };
        }

        // TORUS 함수 호출 결과의 status가 0이 아닌 경우. 실제 status 코드와 매핑된 설명을 반환한다.
        private ContentResult TorusErrorResponse(int torusStatus)
        {
            var body = JsonSerializer.Serialize(
                new { status = torusStatus, error = TorusStatusCodes.GetDescription(torusStatus) },
                TorusController.JsonSerOptions);
            return new ContentResult { Content = body, ContentType = "application/json", StatusCode = 502 };
        }

        // ── /machine/ncMemoryPath : NC 파일 내용을 JSON value로 주고받는 엔드포인트 ──
        // 텍스트 NC 프로그램을 JSON body로 편하게 읽고/쓰는 용도.

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
        [HttpGet("/machine/ncMemoryPath/content")]
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
                string absolutePath = Path.GetFullPath(TempDirDownload + "/" + uniqueName, Directory.GetCurrentDirectory());
                tempDirectory = absolutePath;
                Directory.CreateDirectory(absolutePath);

                string downloadResult = Torus.Torus.Instance.DownloadFile(ncPath, absolutePath + "/", machineID);
                using var downloadDoc = JsonDocument.Parse(downloadResult);
                int downloadStatus = downloadDoc.RootElement.GetProperty("status").GetInt32();
                if (downloadStatus != 0)
                    return TorusErrorResponse(downloadStatus);

                string fileName = ncPath.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries).Last();
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
            catch (JsonException ex)
            {
                _logger.LogError(ex, "Invalid JSON in TORUS response");
                return ErrorResponse(502, "Invalid JSON response from TORUS");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "NC file content read failed");
                return ErrorResponse(500, "Error occurred during NC file content read");
            }
            finally
            {
                if (tempDirectory != null)
                {
                    try { Directory.Delete(tempDirectory, true); }
                    catch (Exception ex)
                    {
                        FileLogger.WriteWarn($"Temp dir cleanup failed: {tempDirectory} - {ex.Message}");
                    }
                }
            }
        }

        // POST /machine/ncMemoryPath/content?machine=1&ncMemoryPath=program/O0001.nc
        // body의 "value"를 NC 파일 내용으로 작성하여 업로드
        [HttpPost("/machine/ncMemoryPath/content")]
        public async Task<ActionResult<string>> PostNcFileContent()
        {
            string? tempDirectory = null;
            bool bodyParseDone = false;
            try
            {
                var (machineID, ncPath) = ExtractNcMemoryPathParams();
                var validationError = ValidateNcMemoryPathParams(machineID, ncPath);
                if (validationError != null) return validationError;

                using var reader = new StreamReader(Request.Body);
                string body = await reader.ReadToEndAsync();
                body = TorusController.NormalizeValueKey(body);

                // body에서 "value" 추출. 배열이면 첫 번째 요소, 문자열이면 그대로.
                using var doc = JsonDocument.Parse(body);
                bodyParseDone = true;
                if (!doc.RootElement.TryGetProperty("value", out var valueProp))
                    return ErrorResponse(400, "'value' field is required");

                string fileContent;
                if (valueProp.ValueKind == JsonValueKind.Array)
                    fileContent = valueProp[0].GetString() ?? "";
                else
                    fileContent = valueProp.GetString() ?? "";

                // 리터럴 이스케이프 시퀀스(\n, \r, \t)를 실제 문자로 치환.
                // 클라이언트가 JSON 직렬화 시 이중 이스케이프(\\n → 리터럴 \n)할 수 있으므로 보정.
                fileContent = fileContent.Replace("\\r\\n", "\r\n")
                                         .Replace("\\n", "\n")
                                         .Replace("\\r", "\r")
                                         .Replace("\\t", "\t");

                // 임시 파일에 저장 후 TORUS로 업로드
                string uniqueName = DateTime.Now.ToString("yyyyMMddHHmmssfff")
                                  + "_" + Guid.NewGuid().ToString("N").Substring(0, 8);
                string tempDir = Path.GetFullPath(TempDirUpload + "/" + uniqueName, Directory.GetCurrentDirectory());
                tempDirectory = tempDir;
                Directory.CreateDirectory(tempDir);

                string fileName = ncPath.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries).Last();
                string filePath = tempDir + "/" + fileName;
                await System.IO.File.WriteAllTextAsync(filePath, fileContent);

                string uploadResult = Torus.Torus.Instance.UploadFile(filePath, ncPath, machineID);
                using var uploadDoc = JsonDocument.Parse(uploadResult);
                int uploadStatus = uploadDoc.RootElement.GetProperty("status").GetInt32();
                if (uploadStatus != 0)
                    return TorusErrorResponse(uploadStatus);

                return new ContentResult { Content = uploadResult, ContentType = "application/json", StatusCode = 200 };
            }
            catch (JsonException ex)
            {
                if (!bodyParseDone)
                {
                    _logger.LogWarning(ex, "Invalid JSON in request body");
                    return ErrorResponse(400, "Invalid JSON in request body");
                }
                _logger.LogError(ex, "Invalid JSON in TORUS response");
                return ErrorResponse(502, "Invalid JSON response from TORUS");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "NC file content write failed");
                return ErrorResponse(500, "Error occurred during NC file content write");
            }
            finally
            {
                if (tempDirectory != null)
                {
                    try { Directory.Delete(tempDirectory, true); }
                    catch (Exception ex)
                    {
                        FileLogger.WriteWarn($"Temp dir cleanup failed: {tempDirectory} - {ex.Message}");
                    }
                }
            }
        }

        // GET /docs/help - 커스텀 엔드포인트 안내 문서 (임베디드 리소스 CustomEndpoints.md)
        [HttpGet("/docs/help")]
        public ContentResult GetHelp()
        {
            string? text = LoadHelpText();
            if (text == null)
            {
                _logger.LogError("Embedded help resource not found");
                return new ContentResult
                {
                    Content = JsonSerializer.Serialize(new { status = -1, error = "Help document not found" }, TorusController.JsonSerOptions),
                    ContentType = "application/json",
                    StatusCode = 500
                };
            }
            return new ContentResult
            {
                Content = text,
                ContentType = "text/markdown; charset=utf-8",
                StatusCode = 200
            };
        }

        private static string? _helpText;
        // CustomEndpoints.md 내용을 반환. UI에서도 재사용하므로 static 공개.
        internal static string? LoadHelpText()
        {
            if (_helpText != null) return _helpText;
            var assembly = typeof(CustomController).Assembly;
            const string resourceName = "TorusGateway.WebServer.CustomEndpoints.md";
            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream == null) return null;
            using var reader = new StreamReader(stream);
            _helpText = reader.ReadToEnd();
            return _helpText;
        }

        // GET /machineList
        // 현재 TORUS에 연결된 머신 목록 반환. 필터 없음. Api.getMachinesInfo 래핑.
        [HttpGet("/machineList")]
        public ActionResult<string> GetMachineList()
        {
            try
            {
                string result = Torus.Torus.Instance.GetMachinesInfo();
                // 머신 목록은 단일 연결 시에도 배열로 유지한다.
                string filtered = TorusController.FilterStatusAndValue(result, unwrapSingletonArray: false);
                // value 배열의 각 원소에서 "vendorCode" 키를 "vendor"로 리네임.
                string renamed = RenameValueArrayKey(filtered, "vendorCode", "vendor");
                return new ContentResult
                {
                    Content = renamed,
                    ContentType = "application/json",
                    StatusCode = 200
                };
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, "Invalid JSON in TORUS response");
                return ErrorResponse(502, "Invalid JSON response from TORUS");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Machine list query failed");
                return ErrorResponse(500, "Error occurred while fetching machine list");
            }
        }

        // 루트 객체의 "value"가 배열일 때, 각 원소(객체)의 특정 키 이름을 변경한다.
        internal static string RenameValueArrayKey(string json, string oldKey, string newKey)
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return json;
            if (!doc.RootElement.TryGetProperty("value", out var valueProp)) return json;
            if (valueProp.ValueKind != JsonValueKind.Array) return json;

            using var ms = new MemoryStream();
            using var writer = new Utf8JsonWriter(ms, TorusController.JsonWriterOpts);
            writer.WriteStartObject();
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Name == "value")
                {
                    writer.WritePropertyName("value");
                    writer.WriteStartArray();
                    foreach (var elem in valueProp.EnumerateArray())
                    {
                        if (elem.ValueKind == JsonValueKind.Object)
                        {
                            writer.WriteStartObject();
                            foreach (var subProp in elem.EnumerateObject())
                            {
                                writer.WritePropertyName(subProp.Name == oldKey ? newKey : subProp.Name);
                                subProp.Value.WriteTo(writer);
                            }
                            writer.WriteEndObject();
                        }
                        else
                        {
                            elem.WriteTo(writer);
                        }
                    }
                    writer.WriteEndArray();
                }
                else
                {
                    writer.WritePropertyName(prop.Name);
                    prop.Value.WriteTo(writer);
                }
            }
            writer.WriteEndObject();
            writer.Flush();
            return Encoding.UTF8.GetString(ms.ToArray());
        }

        // ── /machine/channel/mainProgramPath : 메인 프로그램 경로 조회/설정 ──
        // GET은 실제로 data://machine/channel/currentProgram/mainFile/programNameWithPath로 리라이트.
        // POST는 updateData로 안 되고 Api.CNCFileExecute를 거쳐야 해서 별도 처리.

        // GET /machine/channel/mainProgramPath?machine=N&channel=M
        [HttpGet("/machine/channel/mainProgramPath")]
        public ActionResult<string> GetMainProgramPath()
        {
            try
            {
                const string address = "data://machine/channel/currentProgram/mainFile/programNameWithPath";
                string filter = string.Join("&", HttpContext.Request.Query.Select(p => $"{p.Key}={p.Value}"));

                string getDataResult = Torus.Torus.Instance.GetData(address, filter);
                return new ContentResult
                {
                    Content = TorusController.FilterStatusAndValue(getDataResult),
                    ContentType = "application/json",
                    StatusCode = 200
                };
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, "Invalid JSON in TORUS response");
                return ErrorResponse(502, "Invalid JSON response from TORUS");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Main program path read failed");
                return ErrorResponse(500, "Error occurred while reading main program path");
            }
        }

        // POST /machine/channel/mainProgramPath?machine=N&channel=M
        // body: {"value": "program/O0001.nc"}  또는  {"value": ["program/O0001.nc"]}
        [HttpPost("/machine/channel/mainProgramPath")]
        public async Task<ActionResult<string>> PostMainProgramPath()
        {
            bool bodyParseDone = false;
            try
            {
                int machineID = -1;
                int channel = -1;
                foreach (var param in HttpContext.Request.Query)
                {
                    if (param.Key == "machine") _ = int.TryParse(param.Value, out machineID);
                    else if (param.Key == "channel") _ = int.TryParse(param.Value, out channel);
                }
                if (machineID < 1) return ErrorResponse(400, "Invalid 'machine' parameter");
                if (channel < 1) return ErrorResponse(400, "Invalid 'channel' parameter");

                using var reader = new StreamReader(Request.Body);
                string body = await reader.ReadToEndAsync();
                body = TorusController.NormalizeValueKey(body);

                using var doc = JsonDocument.Parse(body);
                bodyParseDone = true;
                if (!doc.RootElement.TryGetProperty("value", out var valueProp))
                    return ErrorResponse(400, "'value' field is required");

                string ncFilePath;
                if (valueProp.ValueKind == JsonValueKind.Array)
                    ncFilePath = valueProp[0].GetString() ?? "";
                else
                    ncFilePath = valueProp.GetString() ?? "";

                if (string.IsNullOrWhiteSpace(ncFilePath))
                    return ErrorResponse(400, "'value' must be a non-empty NC file path");
                if (ncFilePath.Contains(".."))
                    return ErrorResponse(400, "Invalid NC path");

                string executeResult = Torus.Torus.Instance.ExecuteCNCFile(channel, ncFilePath, machineID);
                using var resultDoc = JsonDocument.Parse(executeResult);
                int executeStatus = resultDoc.RootElement.GetProperty("status").GetInt32();
                if (executeStatus != 0)
                    return TorusErrorResponse(executeStatus);

                return new ContentResult { Content = executeResult, ContentType = "application/json", StatusCode = 200 };
            }
            catch (JsonException ex)
            {
                if (!bodyParseDone)
                {
                    _logger.LogWarning(ex, "Invalid JSON in request body");
                    return ErrorResponse(400, "Invalid JSON in request body");
                }
                _logger.LogError(ex, "Invalid JSON in TORUS response");
                return ErrorResponse(502, "Invalid JSON response from TORUS");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Main program path set/execute failed");
                return ErrorResponse(500, "Error occurred while setting main program path");
            }
        }

        // POST /machine/ncMemoryPath/exist?machine=1&ncMemoryPath=program/O0001.nc
        // {"value": false} → NC 파일 삭제
        [HttpPost("/machine/ncMemoryPath/exist")]
        public async Task<ActionResult<string>> PostNcFileExist()
        {
            bool bodyParseDone = false;
            try
            {
                var (machineID, ncPath) = ExtractNcMemoryPathParams();
                var validationError = ValidateNcMemoryPathParams(machineID, ncPath);
                if (validationError != null) return validationError;

                using var reader = new StreamReader(Request.Body);
                string body = await reader.ReadToEndAsync();
                body = TorusController.NormalizeValueKey(body);

                using var doc = JsonDocument.Parse(body);
                bodyParseDone = true;
                if (!doc.RootElement.TryGetProperty("value", out var valueProp))
                    return ErrorResponse(400, "'value' field is required");

                // value가 배열이면 첫 번째 요소, 아니면 직접 판별
                bool value;
                if (valueProp.ValueKind == JsonValueKind.Array)
                    value = valueProp[0].GetBoolean();
                else
                    value = valueProp.GetBoolean();

                if (value)
                    return ErrorResponse(400, "Only {\"value\": false} is supported (file deletion)");

                string deleteResult = Torus.Torus.Instance.DeleteFile(ncPath, machineID);
                using var resultDoc = JsonDocument.Parse(deleteResult);
                int delStatus = resultDoc.RootElement.GetProperty("status").GetInt32();
                if (delStatus != 0)
                    return TorusErrorResponse(delStatus);

                return new ContentResult { Content = deleteResult, ContentType = "application/json", StatusCode = 200 };
            }
            catch (JsonException ex)
            {
                if (!bodyParseDone)
                {
                    _logger.LogWarning(ex, "Invalid JSON in request body");
                    return ErrorResponse(400, "Invalid JSON in request body");
                }
                _logger.LogError(ex, "Invalid JSON in TORUS response");
                return ErrorResponse(502, "Invalid JSON response from TORUS");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "NC file delete failed");
                return ErrorResponse(500, "Error occurred while deleting NC file");
            }
        }
    }
}
