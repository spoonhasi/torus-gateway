using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Linq;
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
                        try { Directory.Delete(sub, true); } catch { /* 사용 중이면 다음 시작 때 다시 시도 */ }
                    }
                }
                catch { /* root 자체 접근 실패 무시 */ }
            }
        }

        private ContentResult ErrorResponse(int statusCode, string errorMessage)
        {
            var body = JsonSerializer.Serialize(new { status = -1, error = errorMessage });
            return new ContentResult { Content = body, ContentType = "application/json", StatusCode = statusCode };
        }

        // TORUS 함수 호출 결과의 status가 0이 아닌 경우. 실제 status 코드와 매핑된 설명을 반환한다.
        private ContentResult TorusErrorResponse(int torusStatus)
        {
            var body = JsonSerializer.Serialize(new { status = torusStatus, error = TorusStatusCodes.GetDescription(torusStatus) });
            return new ContentResult { Content = body, ContentType = "application/json", StatusCode = 502 };
        }

        // ── /file/machine/ncpath : 바이너리 NC 파일 업로드/다운로드/삭제 ──

        private (int machineID, string ncPath) ExtractMachineAndPath()
        {
            int machineID = -1;
            string ncPath = "";
            foreach (var param in HttpContext.Request.Query)
            {
                if (param.Key == "machine") _ = int.TryParse(param.Value, out machineID);
                else if (param.Key == "ncpath") ncPath = param.Value.ToString() ?? "";
            }
            return (machineID, ncPath);
        }

        // GET /file/machine/ncpath?machine=N&ncpath=...
        // TORUS → 게이트웨이 PC → 클라이언트로 NC 파일 전달 (스트리밍).
        [HttpGet("/file/machine/ncpath")]
        public ActionResult<string> GetTorusDownloadFile()
        {
            string? tempDirectory = null;
            bool streamingOwnsCleanup = false;
            try
            {
                var (machineID, ncPath) = ExtractMachineAndPath();

                if (ncPath.Contains(".."))
                    return ErrorResponse(400, "Invalid NC path");
                if (machineID < 1)
                    return ErrorResponse(400, "Invalid 'machine' parameter");
                if (string.IsNullOrWhiteSpace(ncPath))
                    return ErrorResponse(400, "'ncpath' parameter is empty");

                // 타임스탬프(가독성) + 짧은 GUID(유일성). 동시 요청이 같은 ms에 들어와도 디렉토리 충돌 없음.
                string uniqueName = DateTime.Now.ToString("yyyyMMddHHmmssfff")
                                  + "_" + Guid.NewGuid().ToString("N").Substring(0, 8);
                string absolutePath = Path.GetFullPath(TempDirDownload + "/" + uniqueName, Directory.GetCurrentDirectory());
                tempDirectory = absolutePath;
                Directory.CreateDirectory(absolutePath);
                string downloadFileResult = Torus.Torus.Instance.DownloadFile(ncPath, absolutePath + "/", machineID);

                using var downloadDoc = JsonDocument.Parse(downloadFileResult);
                int downloadStatus = downloadDoc.RootElement.GetProperty("status").GetInt32();
                if (downloadStatus != 0)
                    return TorusErrorResponse(downloadStatus);

                string fileName = ncPath.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries).Last();
                string filePath = absolutePath + "/" + fileName;

                if (!System.IO.File.Exists(filePath))
                    return ErrorResponse(404, "File not found");

                // 스트리밍 응답: 32비트 프로세스에서 큰 NC 파일 전체를 메모리에 적재하지 않도록 FileStream 사용.
                // ASP.NET이 응답 완료 시 스트림을 자동으로 Dispose함.
                var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                string capturedDir = tempDirectory;
                HttpContext.Response.OnCompleted(() =>
                {
                    try { Directory.Delete(capturedDir, true); } catch { }
                    return Task.CompletedTask;
                });
                streamingOwnsCleanup = true;
                return File(fileStream, "application/octet-stream", fileName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "NC file download failed");
                return ErrorResponse(500, "Error occurred during NC file download");
            }
            finally
            {
                // 스트리밍 성공 경로는 OnCompleted가 정리. 그 외(에러/조기 return) 경로만 finally에서 정리.
                if (!streamingOwnsCleanup && tempDirectory != null)
                    try { Directory.Delete(tempDirectory, true); } catch { }
            }
        }

        // PUT /file/machine/ncpath?machine=N&ncpath=...  (multipart/form-data)
        // 게이트웨이 PC에 임시 저장 후 TORUS를 통해 NC로 업로드.
        [HttpPut("/file/machine/ncpath")]
        public async Task<ActionResult<string>> PutTorusUploadFile(IFormFile file)
        {
            string? tempDirectory = null;
            try
            {
                var (machineID, ncPath) = ExtractMachineAndPath();

                if (ncPath.Contains(".."))
                    return ErrorResponse(400, "Invalid NC path");
                if (machineID < 1)
                    return ErrorResponse(400, "Invalid 'machine' parameter");
                if (string.IsNullOrWhiteSpace(ncPath))
                    return ErrorResponse(400, "'ncpath' parameter is empty");

                if (file == null || file.Length == 0)
                    return ErrorResponse(400, "No file provided or file is empty");

                const long maxFileSize = 100 * 1024 * 1024;
                if (file.Length > maxFileSize)
                    return ErrorResponse(400, $"File size exceeds limit (max: {maxFileSize / 1024 / 1024}MB)");

                string safeFileName = Path.GetFileName(file.FileName);
                string uniqueName = DateTime.Now.ToString("yyyyMMddHHmmssfff")
                                  + "_" + Guid.NewGuid().ToString("N").Substring(0, 8);
                string filePath = TempDirUpload + "/" + uniqueName + "/" + safeFileName;
                try
                {
                    string? directory = Path.GetDirectoryName(filePath);
                    if (directory != null && !Directory.Exists(directory))
                        Directory.CreateDirectory(directory);
                    using var stream = new FileStream(filePath, FileMode.Create);
                    await file.CopyToAsync(stream);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Temporary file save failed");
                    return ErrorResponse(500, "File upload failed");
                }

                string absolutePath = Path.GetFullPath(filePath, Directory.GetCurrentDirectory());
                tempDirectory = Path.GetDirectoryName(absolutePath);

                string uploadFileResult = Torus.Torus.Instance.UploadFile(absolutePath, ncPath, machineID);

                using var uploadDoc = JsonDocument.Parse(uploadFileResult);
                int uploadStatus = uploadDoc.RootElement.GetProperty("status").GetInt32();
                if (uploadStatus != 0)
                    return TorusErrorResponse(uploadStatus);

                return new ContentResult
                {
                    Content = uploadFileResult,
                    ContentType = "application/json",
                    StatusCode = 200
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "NC file upload failed");
                return ErrorResponse(500, "Error occurred during NC file upload");
            }
            finally
            {
                if (tempDirectory != null) try { Directory.Delete(tempDirectory, true); } catch { }
            }
        }

        // DELETE /file/machine/ncpath?machine=N&ncpath=...
        [HttpDelete("/file/machine/ncpath")]
        public ActionResult<string> DeleteTorusUploadFile()
        {
            try
            {
                var (machineID, ncPath) = ExtractMachineAndPath();

                if (ncPath.Contains(".."))
                    return ErrorResponse(400, "Invalid NC path");
                if (machineID < 1)
                    return ErrorResponse(400, "Invalid 'machine' parameter");
                if (string.IsNullOrWhiteSpace(ncPath))
                    return ErrorResponse(400, "'ncpath' parameter is empty");

                string deleteFileResult = Torus.Torus.Instance.DeleteFile(ncPath, machineID);

                using var deleteDoc = JsonDocument.Parse(deleteFileResult);
                int deleteStatus = deleteDoc.RootElement.GetProperty("status").GetInt32();
                if (deleteStatus != 0)
                    return TorusErrorResponse(deleteStatus);

                return new ContentResult
                {
                    Content = deleteFileResult,
                    ContentType = "application/json",
                    StatusCode = 200
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "NC file delete failed");
                return ErrorResponse(500, "Error occurred during NC file deletion");
            }
        }

        // ── /machine/ncMemoryPath : NC 파일 내용을 JSON value로 주고받는 엔드포인트 ──
        // 위 /file/machine/ncpath는 바이너리 파일 전송용이고,
        // 이쪽은 텍스트 NC 프로그램을 JSON body로 편하게 읽고/쓰는 용도.

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

        // PUT|POST /machine/ncMemoryPath/content?machine=1&ncMemoryPath=program/O0001.nc
        // body의 "value"를 NC 파일 내용으로 작성하여 업로드
        [HttpPut("/machine/ncMemoryPath/content")]
        [HttpPost("/machine/ncMemoryPath/content")]
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

                using var doc = JsonDocument.Parse(body);
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
            catch (Exception ex)
            {
                _logger.LogError(ex, "Main program path set/execute failed");
                return ErrorResponse(500, "Error occurred while setting main program path");
            }
        }

        // PUT|POST /machine/ncMemoryPath/exist?machine=1&ncMemoryPath=program/O0001.nc
        // {"value": false} → NC 파일 삭제
        [HttpPut("/machine/ncMemoryPath/exist")]
        [HttpPost("/machine/ncMemoryPath/exist")]
        public async Task<ActionResult<string>> PutNcFileExist()
        {
            try
            {
                var (machineID, ncPath) = ExtractNcMemoryPathParams();
                var validationError = ValidateNcMemoryPathParams(machineID, ncPath);
                if (validationError != null) return validationError;

                using var reader = new StreamReader(Request.Body);
                string body = await reader.ReadToEndAsync();

                using var doc = JsonDocument.Parse(body);
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
            catch (Exception ex)
            {
                _logger.LogError(ex, "NC file delete failed");
                return ErrorResponse(500, "Error occurred while deleting NC file");
            }
        }
    }
}
