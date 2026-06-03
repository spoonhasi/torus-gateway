using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
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

        // 임시 디렉토리는 항상 실행 파일 디렉토리(BaseDirectory) 기준으로 해석한다.
        // CWD(GetCurrentDirectory)는 파일 열기 다이얼로그 등으로 바뀔 수 있어,
        // 같은 BaseDirectory를 쓰는 App.OnExit 정리/FileLogger와 경로가 어긋나는 것을 방지.
        private static string ResolveTempPath(string relative) =>
            Path.GetFullPath(relative, AppDomain.CurrentDomain.BaseDirectory);

        // 앱 시작 시 호출. 비정상 종료 등으로 남은 이전 세션의 임시 디렉토리를 정리한다.
        // OnCompleted 콜백이 실행되지 못한 경우(프로세스 강제 종료, 크래시)에 대비.
        public static void CleanupOrphanedTempDirs()
        {
            foreach (var root in new[] { TempDirDownload, TempDirUpload })
            {
                try
                {
                    string fullRoot = ResolveTempPath(root);
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
                // 쿼리 키는 대소문자 무관하게 인식한다.
                if (string.Equals(param.Key, "machine", StringComparison.OrdinalIgnoreCase))
                    _ = int.TryParse(param.Value, out machineID);
                // 경로 구분자는 '/'로 통일 (클라이언트가 '\'로 보내도 허용).
                else if (string.Equals(param.Key, "ncMemoryPath", StringComparison.OrdinalIgnoreCase))
                    ncPath = (param.Value.ToString() ?? "").Replace('\\', '/');
            }
            return (machineID, ncPath);
        }

        // 쿼리 파라미터를 TORUS filter 문자열로 변환한다.
        // TORUS 필터 키는 모두 소문자이므로 클라이언트가 대소문자를 섞어 보내도 키를 소문자로 정규화한다.
        // (값은 변경하지 않는다 — 경로 등 대소문자 의미 보존)
        // aliasFrom(소문자 기준)이 주어지면 해당 키를 aliasTo로 치환한다. (예: variablenumber → variable)
        private string BuildFilter(string? aliasFrom = null, string? aliasTo = null)
        {
            return string.Join("&", HttpContext.Request.Query.Select(p =>
            {
                string key = p.Key.ToLowerInvariant();
                if (aliasFrom != null && key == aliasFrom) key = aliasTo!;
                return $"{key}={p.Value}";
            }));
        }

        private ContentResult? ValidateNcMemoryPathParams(int machineID, string ncPath)
        {
            if (machineID < 1) return ErrorResponse(400, "Invalid 'machine' parameter");
            if (string.IsNullOrWhiteSpace(ncPath)) return ErrorResponse(400, "'ncMemoryPath' parameter is empty");
            if (ncPath.Contains("..")) return ErrorResponse(400, "Invalid NC path");
            return null;
        }

        // NC 파일을 임시 디렉토리로 내려받아 텍스트 내용을 읽어 반환한다.
        // GET /content 와 NC 탐색기(다운로드)가 공유하는 핵심 로직. (HTTP/JSON 래핑은 호출부 담당)
        // 반환: Status==0 && !NotFound 이면 Content 유효. Status!=0이면 TORUS 다운로드 실패.
        // JsonException은 호출부에서 처리하도록 전파한다.
        internal static NcTextDownloadResult DownloadNcFileText(int machineID, string ncPath)
        {
            string? tempDirectory = null;
            try
            {
                string uniqueName = DateTime.Now.ToString("yyyyMMddHHmmssfff")
                                  + "_" + Guid.NewGuid().ToString("N").Substring(0, 8);
                string absolutePath = ResolveTempPath(TempDirDownload + "/" + uniqueName);
                tempDirectory = absolutePath;
                Directory.CreateDirectory(absolutePath);

                string downloadResult = Torus.Torus.Instance.DownloadFile(ncPath, absolutePath + "/", machineID);
                using var downloadDoc = JsonDocument.Parse(downloadResult);
                int downloadStatus = downloadDoc.RootElement.GetProperty("status").GetInt32();
                if (downloadStatus != 0)
                    return new NcTextDownloadResult { Status = downloadStatus };

                string fileName = ncPath.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries).Last();
                string filePath = absolutePath + "/" + fileName;
                if (!System.IO.File.Exists(filePath))
                    return new NcTextDownloadResult { Status = 0, NotFound = true };

                return new NcTextDownloadResult { Status = 0, Content = System.IO.File.ReadAllText(filePath) };
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

        // GET /machine/ncMemoryPath/content?machine=1&ncMemoryPath=program/O0001.nc
        // NC 파일 내용을 다운로드하여 JSON value로 반환
        [HttpGet("/machine/ncMemoryPath/content")]
        public ActionResult<string> GetNcFileContent()
        {
            try
            {
                var (machineID, ncPath) = ExtractNcMemoryPathParams();
                var validationError = ValidateNcMemoryPathParams(machineID, ncPath);
                if (validationError != null) return validationError;

                var result = DownloadNcFileText(machineID, ncPath);
                if (result.Status != 0) return TorusErrorResponse(result.Status);
                if (result.NotFound) return ErrorResponse(404, "File not found");

                var jsonOptions = new JsonSerializerOptions
                {
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                };
                var response = JsonSerializer.Serialize(new { status = 0, value = result.Content }, jsonOptions);
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
        }

        // 텍스트 내용을 임시 파일로 저장한 뒤 TORUS로 업로드한다.
        // POST /content 와 NC 탐색기(업로드)가 공유하는 핵심 로직. content는 이미 실제 텍스트여야 한다.
        // 반환: TORUS 업로드 status (0이면 성공). JsonException은 호출부에서 처리하도록 전파한다.
        internal static int UploadNcFileText(int machineID, string ncPath, string content)
        {
            string? tempDirectory = null;
            try
            {
                string uniqueName = DateTime.Now.ToString("yyyyMMddHHmmssfff")
                                  + "_" + Guid.NewGuid().ToString("N").Substring(0, 8);
                string tempDir = ResolveTempPath(TempDirUpload + "/" + uniqueName);
                tempDirectory = tempDir;
                Directory.CreateDirectory(tempDir);

                string fileName = ncPath.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries).Last();
                string filePath = tempDir + "/" + fileName;
                System.IO.File.WriteAllText(filePath, content);

                string uploadResult = Torus.Torus.Instance.UploadFile(filePath, ncPath, machineID);
                using var uploadDoc = JsonDocument.Parse(uploadResult);
                return uploadDoc.RootElement.GetProperty("status").GetInt32();
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

                int uploadStatus = UploadNcFileText(machineID, ncPath, fileContent);
                if (uploadStatus != 0)
                    return TorusErrorResponse(uploadStatus);

                var response = JsonSerializer.Serialize(new { status = 0 }, TorusController.JsonSerOptions);
                return new ContentResult { Content = response, ContentType = "application/json", StatusCode = 200 };
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
        }

        // GET /health - 헬스 체크 (TORUS 연결과 무관한 정적 응답)
        [HttpGet("/health")]
        public ContentResult GetHealth()
        {
            return new ContentResult
            {
                Content = "{\"status\":0,\"value\":\"ok\"}",
                ContentType = "application/json",
                StatusCode = 200
            };
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

        // JSON 루트 객체의 문자열 "value" 끝에 붙은 '/'를 제거한다.
        // value가 문자열이 아니거나 변경점이 없으면 원본 그대로 반환. (선행 '/'는 보존)
        internal static string TrimTrailingSlashInValue(string json)
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return json;
            if (!doc.RootElement.TryGetProperty("value", out var valueProp)
                || valueProp.ValueKind != JsonValueKind.String)
                return json;

            string? original = valueProp.GetString();
            if (original == null) return json;
            string trimmed = original.TrimEnd('/');
            if (trimmed == original) return json;

            using var ms = new MemoryStream();
            using var writer = new Utf8JsonWriter(ms, TorusController.JsonWriterOpts);
            writer.WriteStartObject();
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Name == "value")
                    writer.WriteString("value", trimmed);
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

        // GET /machine/ncMemoryPath/directory?machine=1&ncMemoryPath=program
        // 지정한 NC 메모리 경로 하위의 파일/폴더 목록 반환. Api.getFileListEx 래핑.
        // 정렬: 폴더 우선 → 이름 오름차순. 각 항목은 name/sizeBytes/modifiedAt/isDir.
        // name 끝의 '/'(폴더 표기)는 제거한다.
        [HttpGet("/machine/ncMemoryPath/directory")]
        public ActionResult<string> GetNcMemoryPathDirectory()
        {
            try
            {
                var (machineID, ncPath) = ExtractNcMemoryPathParams();
                var validationError = ValidateNcMemoryPathParams(machineID, ncPath);
                if (validationError != null) return validationError;

                string listResult = Torus.Torus.Instance.GetFileList(ncPath, machineID);

                // TORUS 호출 status 확인. 0이 아니면 매핑된 오류로 반환.
                using (var statusDoc = JsonDocument.Parse(listResult))
                {
                    int status = statusDoc.RootElement.TryGetProperty("status", out var st)
                                 && st.ValueKind == JsonValueKind.Number && st.TryGetInt32(out int code)
                                 ? code : -1;
                    if (status != 0)
                        return TorusErrorResponse(status);
                }

                var files = JsonSerializer.Deserialize<FilesObject>(listResult);
                // 폴더 우선 → 이름 오름차순. 출력 키는 sizeBytes/modifiedAt으로 리네임하고 path는 노출하지 않음.
                var sorted = (files?.Value ?? new List<FileObject>())
                    .Where(f => f is not null)
                    .OrderByDescending(f => f.IsDir)
                    .ThenBy(f => f.Name)
                    .Select(f => new
                    {
                        name = f.Name.TrimEnd('/'),
                        sizeBytes = f.Size,
                        modifiedAt = f.Datetime,
                        isDir = f.IsDir
                    })
                    .ToList();

                string body = JsonSerializer.Serialize(new { status = 0, value = sorted }, TorusController.JsonSerOptions);
                return new ContentResult { Content = body, ContentType = "application/json", StatusCode = 200 };
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, "Invalid JSON in TORUS response");
                return ErrorResponse(502, "Invalid JSON response from TORUS");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "NC directory list failed");
                return ErrorResponse(500, "Error occurred while listing NC directory");
            }
        }

        // POST /machine/ncMemoryPath/directory?machine=1&ncMemoryPath=program/subDir
        // body: {"value": []}  (입력만 받음 — 내용 무시)
        // ncMemoryPath의 마지막 세그먼트를 폴더 이름으로, 나머지를 부모 경로로 분리해 폴더 생성.
        // Api.CreateCNCFolder(parentPath, name, ...) 래핑.
        [HttpPost("/machine/ncMemoryPath/directory")]
        public async Task<ActionResult<string>> PostNcMemoryPathDirectory()
        {
            bool bodyParseDone = false;
            try
            {
                var (machineID, ncPath) = ExtractNcMemoryPathParams();
                var validationError = ValidateNcMemoryPathParams(machineID, ncPath);
                if (validationError != null) return validationError;

                // body는 형식만 확인(빈 배열/객체 등 무엇이든 허용). JSON이 아니면 400.
                using var reader = new StreamReader(Request.Body);
                string rawBody = await reader.ReadToEndAsync();
                if (!string.IsNullOrWhiteSpace(rawBody))
                {
                    using var _ = JsonDocument.Parse(rawBody);
                }
                bodyParseDone = true;

                // ncMemoryPath를 부모 경로 + 폴더 이름으로 분리. 끝의 '/'는 무시.
                string trimmed = ncPath.TrimEnd('/');
                int lastSep = trimmed.LastIndexOfAny(new[] { '/', '\\' });
                if (lastSep < 0)
                    return ErrorResponse(400, "'ncMemoryPath' must include a parent path and a folder name");
                string parentPath = trimmed.Substring(0, lastSep);
                string folderName = trimmed.Substring(lastSep + 1);
                if (string.IsNullOrWhiteSpace(parentPath) || string.IsNullOrWhiteSpace(folderName))
                    return ErrorResponse(400, "'ncMemoryPath' must include a parent path and a folder name");

                string createResult = Torus.Torus.Instance.CreateFolder(parentPath, folderName, machineID);
                using var resultDoc = JsonDocument.Parse(createResult);
                int createStatus = resultDoc.RootElement.GetProperty("status").GetInt32();
                if (createStatus != 0)
                    return TorusErrorResponse(createStatus);

                var response = JsonSerializer.Serialize(new { status = 0 }, TorusController.JsonSerOptions);
                return new ContentResult { Content = response, ContentType = "application/json", StatusCode = 200 };
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
                _logger.LogError(ex, "NC folder creation failed");
                return ErrorResponse(500, "Error occurred while creating NC folder");
            }
        }

        // GET /machine/channel/variableNumber/variableValue?machine=N&channel=M&variableNumber=N
        // 사용자 변수 값 조회. TORUS는 필터 키로 'variable'을 기대하므로,
        // 클라이언트가 보낸 'variableNumber' 키를 'variable'로 변환해 전달한다.
        [HttpGet("/machine/channel/variableNumber/variableValue")]
        public ActionResult<string> GetVariableValue()
        {
            try
            {
                const string address = "data://machine/channel/variable/userVariable";
                // TORUS는 필터 키로 'variable'을 기대하므로 'variableNumber'(대소문자 무관)를 'variable'로 치환.
                string filter = BuildFilter(aliasFrom: "variablenumber", aliasTo: "variable");

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
                _logger.LogError(ex, "Variable value read failed");
                return ErrorResponse(500, "Error occurred while reading variable value");
            }
        }

        // POST /machine/channel/variableNumber/variableValue?machine=N&channel=M&variableNumber=N
        // body: {"value": <값>}  → 사용자 변수 값 쓰기.
        // GET과 동일하게 주소를 data://machine/channel/variable/userVariable로 리라이트하고
        // 'variableNumber' 키를 'variable'로 치환한다. body는 pass-through POST와 동일하게 정규화한다
        // (Value/VALUE → value, 단일 값 → 배열 래핑).
        [HttpPost("/machine/channel/variableNumber/variableValue")]
        public async Task<ActionResult<string>> PostVariableValue()
        {
            bool bodyParseDone = false;
            try
            {
                const string address = "data://machine/channel/variable/userVariable";
                string filter = BuildFilter(aliasFrom: "variablenumber", aliasTo: "variable");

                using var reader = new StreamReader(Request.Body);
                string body = await reader.ReadToEndAsync();
                body = TorusController.NormalizeValueKey(body);
                body = TorusController.WrapValueToArray(body);
                bodyParseDone = true;

                string updateResult = Torus.Torus.Instance.UpdateData(address, filter, body);
                using var resultDoc = JsonDocument.Parse(updateResult);
                int status = resultDoc.RootElement.GetProperty("status").GetInt32();
                if (status != 0)
                    return TorusErrorResponse(status);

                var response = JsonSerializer.Serialize(new { status = 0 }, TorusController.JsonSerOptions);
                return new ContentResult { Content = response, ContentType = "application/json", StatusCode = 200 };
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
                _logger.LogError(ex, "Variable value write failed");
                return ErrorResponse(500, "Error occurred while writing variable value");
            }
        }

        // GET /machine/ncMemoryRootPath?machine=N
        [HttpGet("/machine/ncMemoryRootPath")]
        public ActionResult<string> GetNcMemoryRootPath()
        {
            try
            {
                const string address = "data://machine/ncMemory/rootPath";
                string filter = BuildFilter();

                string getDataResult = Torus.Torus.Instance.GetData(address, filter);
                // 루트 경로 끝의 '/' 제거 (예: "//NC/MPF.DIR/" → "//NC/MPF.DIR")
                return new ContentResult
                {
                    Content = TrimTrailingSlashInValue(TorusController.FilterStatusAndValue(getDataResult)),
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
                _logger.LogError(ex, "NC root path read failed");
                return ErrorResponse(500, "Error occurred while reading NC root path");
            }
        }

        // GET /machine/channel/programName?machine=N&channel=M
        [HttpGet("/machine/channel/programName")]
        public ActionResult<string> GetCurrentProgramName()
        {
            try
            {
                const string address = "data://machine/channel/currentProgram/currentFile/programName";
                string filter = BuildFilter();

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
                _logger.LogError(ex, "Current program name read failed");
                return ErrorResponse(500, "Error occurred while reading current program name");
            }
        }

        // GET /machine/channel/mainProgramName?machine=N&channel=M
        [HttpGet("/machine/channel/mainProgramName")]
        public ActionResult<string> GetMainProgramName()
        {
            try
            {
                const string address = "data://machine/channel/currentProgram/mainFile/programName";
                string filter = BuildFilter();

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
                _logger.LogError(ex, "Main program name read failed");
                return ErrorResponse(500, "Error occurred while reading main program name");
            }
        }

        // GET /machine/channel/programPath?machine=N&channel=M
        [HttpGet("/machine/channel/programPath")]
        public ActionResult<string> GetCurrentProgramPath()
        {
            try
            {
                const string address = "data://machine/channel/currentProgram/currentFile/programNameWithPath";
                string filter = BuildFilter();

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
                _logger.LogError(ex, "Current program path read failed");
                return ErrorResponse(500, "Error occurred while reading current program path");
            }
        }

        // GET /machine/channel/mainProgramPath?machine=N&channel=M
        [HttpGet("/machine/channel/mainProgramPath")]
        public ActionResult<string> GetMainProgramPath()
        {
            try
            {
                const string address = "data://machine/channel/currentProgram/mainFile/programNameWithPath";
                string filter = BuildFilter();

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
                    // 쿼리 키는 대소문자 무관하게 인식한다.
                    if (string.Equals(param.Key, "machine", StringComparison.OrdinalIgnoreCase))
                        _ = int.TryParse(param.Value, out machineID);
                    else if (string.Equals(param.Key, "channel", StringComparison.OrdinalIgnoreCase))
                        _ = int.TryParse(param.Value, out channel);
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

                // 경로 구분자는 '/'로 통일 (클라이언트가 '\'로 보내도 허용).
                ncFilePath = ncFilePath.Replace('\\', '/');

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

        // POST /machine/ncMemoryPath/name?machine=1&ncMemoryPath=program/O0001.nc
        // body: {"value": "O0002.nc"}  → 지정한 NC 파일/폴더의 이름 변경
        // ncMemoryPath는 대상의 전체 경로, value는 새 이름(경로 아님). Api.CNCFileRename 래핑.
        [HttpPost("/machine/ncMemoryPath/name")]
        public async Task<ActionResult<string>> PostNcFileName()
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

                // value가 배열이면 첫 번째 요소, 아니면 직접 사용
                string newName;
                if (valueProp.ValueKind == JsonValueKind.Array)
                    newName = valueProp[0].GetString() ?? "";
                else
                    newName = valueProp.GetString() ?? "";

                if (string.IsNullOrWhiteSpace(newName))
                    return ErrorResponse(400, "'value' must be a non-empty new name");
                // 새 이름은 경로가 아닌 단일 이름이어야 한다(디렉토리 이동/탈출 방지).
                if (newName.Contains('/') || newName.Contains('\\') || newName.Contains(".."))
                    return ErrorResponse(400, "'value' must be a name, not a path");

                string renameResult = Torus.Torus.Instance.RenameFile(ncPath, newName, machineID);
                using var resultDoc = JsonDocument.Parse(renameResult);
                int renameStatus = resultDoc.RootElement.GetProperty("status").GetInt32();
                if (renameStatus != 0)
                    return TorusErrorResponse(renameStatus);

                var response = JsonSerializer.Serialize(new { status = 0 }, TorusController.JsonSerOptions);
                return new ContentResult { Content = response, ContentType = "application/json", StatusCode = 200 };
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
                _logger.LogError(ex, "NC file rename failed");
                return ErrorResponse(500, "Error occurred while renaming NC file");
            }
        }

    }

    // getFileListEx 응답 역직렬화용 모델. (TORUS 입력 키 기준 — path는 응답/탐색에 미사용)
    internal class FileObject
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("size")]
        public long Size { get; set; }

        [JsonPropertyName("datetime")]
        public string Datetime { get; set; } = "";

        [JsonPropertyName("isDir")]
        public bool IsDir { get; set; }
    }

    internal class FilesObject
    {
        [JsonPropertyName("value")]
        public List<FileObject> Value { get; set; } = new();
    }

    // DownloadNcFileText 결과. Status==0 이고 NotFound==false 이면 Content 유효.
    internal sealed class NcTextDownloadResult
    {
        public int Status { get; init; }
        public bool NotFound { get; init; }
        public string Content { get; init; } = "";
    }
}
