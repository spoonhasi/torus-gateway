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
    // NC 파일 전송 (논문 Fig. 4, 6, 10). 모두 같은 URI를 쓰고 메서드로 기능을 나눈다.
    //   GET    /file/machine/ncpath?machine=1&ncpath=//NC/Partprograms/TEST.MPF → Api.DownloadFile
    //   PUT    (같은 URI, multipart/form-data의 file 필드)                       → Api.UploadFile
    //   DELETE (같은 URI)                                                         → Api.CNCFileDelete
    // TORUS는 게이트웨이 PC의 로컬 경로로만 파일을 주고받으므로, 요청마다 임시 디렉토리를 거친다.
    [ApiController]
    [Route("[controller]/machine/ncpath")]
    public class FileController : ControllerBase
    {
        internal const string TempDirDownload = "ncTempFileForDownloading";
        internal const string TempDirUpload = "ncTempFileForUploading";
        private const long MaxUploadBytes = 100L * 1024 * 1024; // 100MB

        private readonly ILogger<FileController> _logger;

        public FileController(ILogger<FileController> logger)
        {
            _logger = logger;
        }

        // 임시 디렉토리는 항상 실행 파일 디렉토리(BaseDirectory) 기준으로 해석한다.
        // CWD는 파일 열기 다이얼로그 등으로 바뀔 수 있어, 같은 기준을 쓰는 App.OnExit 정리와 어긋나는 것을 방지.
        internal static string ResolveTempPath(string relative) =>
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

        private static void TryDeleteDirectory(string dir)
        {
            try { Directory.Delete(dir, true); }
            catch (Exception ex) { FileLogger.WriteWarn($"Temp dir cleanup failed: {dir} - {ex.Message}"); }
        }

        // 요청마다 고유한 임시 디렉토리. 타임스탬프(가독성) + 짧은 GUID(같은 ms 동시 요청 충돌 방지).
        private static string CreateRequestTempDir(string root)
        {
            string uniqueName = DateTime.Now.ToString("yyyyMMddHHmmssfff")
                              + "_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            string dir = ResolveTempPath(Path.Combine(root, uniqueName));
            Directory.CreateDirectory(dir);
            return dir;
        }

        private ContentResult ErrorResponse(int statusCode, string errorMessage)
        {
            var body = JsonSerializer.Serialize(new { status = -1, error = errorMessage }, MachineController.ErrorJsonOptions);
            return new ContentResult { Content = body, ContentType = "application/json", StatusCode = statusCode };
        }

        // TORUS 호출 결과의 status가 0이 아닌 경우. 실제 status 코드와 매핑된 설명을 반환한다.
        private ContentResult TorusErrorResponse(int torusStatus)
        {
            var body = JsonSerializer.Serialize(
                new { status = torusStatus, error = TorusStatusCodes.GetDescription(torusStatus) },
                MachineController.ErrorJsonOptions);
            return new ContentResult { Content = body, ContentType = "application/json", StatusCode = 502 };
        }

        private static int ReadStatus(string torusResultJson)
        {
            using var doc = JsonDocument.Parse(torusResultJson);
            return doc.RootElement.GetProperty("status").GetInt32();
        }

        // 쿼리의 machine/ncpath를 꺼내 검증한다. 오류가 있으면 응답을, 없으면 null을 돌려준다.
        private ContentResult? ExtractMachineAndPath(out int machineID, out string ncPath)
        {
            machineID = -1;
            ncPath = "";
            foreach (var param in HttpContext.Request.Query)
            {
                if (param.Key == "machine") _ = int.TryParse(param.Value, out machineID);
                else if (param.Key == "ncpath") ncPath = param.Value.ToString() ?? "";
            }

            if (ncPath.Contains("..")) return ErrorResponse(400, "Invalid NC path"); // 경로 조작 방지
            if (machineID < 1) return ErrorResponse(400, "Invalid 'machine' parameter");
            if (string.IsNullOrWhiteSpace(ncPath)) return ErrorResponse(400, "'ncpath' parameter is empty");
            return null;
        }

        // ncpath의 마지막 세그먼트(파일 이름). 구분자로 끝나면(폴더) 빈 문자열.
        internal static string FileNameOf(string ncPath)
        {
            if (ncPath.EndsWith('/') || ncPath.EndsWith('\\')) return "";
            return ncPath.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "";
        }

        // ncpath 파일 다운로드. TORUS로 NC에서 게이트웨이 PC로 내려받은 뒤 클라이언트에 그대로 전송한다.
        [HttpGet]
        public ActionResult GetTorusDownloadFile()
        {
            string? tempDirectory = null;
            bool streamingOwnsCleanup = false;
            try
            {
                var invalid = ExtractMachineAndPath(out int machineID, out string ncPath);
                if (invalid != null) return invalid;
                string fileName = FileNameOf(ncPath);
                if (fileName.Length == 0) return ErrorResponse(400, "'ncpath' must include a file name");

                tempDirectory = CreateRequestTempDir(TempDirDownload);
                int status = ReadStatus(Torus.Torus.Instance.DownloadFile(ncPath, tempDirectory + "/", machineID));
                if (status != 0) return TorusErrorResponse(status);

                string filePath = Path.Combine(tempDirectory, fileName);
                if (!System.IO.File.Exists(filePath)) return ErrorResponse(404, "File not found");

                // 스트리밍 응답: 32비트 프로세스에서 큰 파일 전체를 메모리에 올리지 않도록 FileStream 사용.
                // ASP.NET이 응답 완료 시 스트림을 Dispose하고, 그 뒤 OnCompleted에서 임시 디렉토리를 지운다.
                var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                string capturedDir = tempDirectory;
                HttpContext.Response.OnCompleted(() =>
                {
                    TryDeleteDirectory(capturedDir);
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
                // 스트리밍 성공 경로는 OnCompleted가 정리. 그 외(오류/조기 return) 경로만 여기서 정리.
                if (!streamingOwnsCleanup && tempDirectory != null) TryDeleteDirectory(tempDirectory);
            }
        }

        // ncpath로 파일 업로드. 게이트웨이 PC에 임시 저장한 뒤 TORUS로 NC에 전송한다.
        // 요청 크기 상한을 MaxUploadBytes로 맞춘다(기본값은 Kestrel 약 28MB라 그보다 큰 파일이 413으로 막힘).
        [HttpPut]
        [RequestSizeLimit(MaxUploadBytes)]
        [RequestFormLimits(MultipartBodyLengthLimit = MaxUploadBytes)]
        public async Task<ActionResult> PutTorusUploadFile(IFormFile? file)
        {
            string? tempDirectory = null;
            try
            {
                var invalid = ExtractMachineAndPath(out int machineID, out string ncPath);
                if (invalid != null) return invalid;
                string fileName = FileNameOf(ncPath);
                if (fileName.Length == 0) return ErrorResponse(400, "'ncpath' must include a file name");

                if (file == null || file.Length == 0)
                    return ErrorResponse(400, "No file provided or file is empty");
                if (file.Length > MaxUploadBytes)
                    return ErrorResponse(400, $"File size exceeds limit (max: {MaxUploadBytes / 1024 / 1024}MB)");

                // 로컬 임시 파일은 ncpath의 파일 이름으로 저장한다. 업로드된 파일 이름과 ncpath가 달라도
                // NC에는 ncpath가 가리키는 이름으로 들어가도록 하기 위함.
                tempDirectory = CreateRequestTempDir(TempDirUpload);
                string localPath = Path.Combine(tempDirectory, fileName);
                await using (var stream = new FileStream(localPath, FileMode.Create))
                {
                    await file.CopyToAsync(stream);
                }

                string uploadResult = Torus.Torus.Instance.UploadFile(localPath, ncPath, machineID);
                int status = ReadStatus(uploadResult);
                if (status != 0) return TorusErrorResponse(status);

                return new ContentResult { Content = uploadResult, ContentType = "application/json", StatusCode = 200 };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "NC file upload failed");
                return ErrorResponse(500, "Error occurred during NC file upload");
            }
            finally
            {
                if (tempDirectory != null) TryDeleteDirectory(tempDirectory);
            }
        }

        // ncpath 파일 삭제.
        [HttpDelete]
        public ActionResult DeleteTorusFile()
        {
            try
            {
                var invalid = ExtractMachineAndPath(out int machineID, out string ncPath);
                if (invalid != null) return invalid;

                string deleteResult = Torus.Torus.Instance.DeleteFile(ncPath, machineID);
                int status = ReadStatus(deleteResult);
                if (status != 0) return TorusErrorResponse(status);

                return new ContentResult { Content = deleteResult, ContentType = "application/json", StatusCode = 200 };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "NC file delete failed");
                return ErrorResponse(500, "Error occurred during NC file deletion");
            }
        }
    }
}
