using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace TorusGateway.WebServer
{
    [ApiController]
    [Route("[controller]/machine/ncpath")]
    public class FileController : ControllerBase
    {
        internal const string TempDirDownload = "ncTempFileForDownloading";
        internal const string TempDirUpload = "ncTempFileForUploading";

        private readonly ILogger<FileController> _logger;

        public FileController(ILogger<FileController> logger)
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

        //ncpath 쿼리 경로의 파일 다운로드를 위한 Get입니다. 일단 TORUS를 통해 NC로부터 게이트웨이가 구동되는 PC에 파일을 다운로드한 후에 요청한 클라이언트에 해당 파일을 제공합니다.
        [HttpGet]
        public ActionResult<string> GetTorusDownloadFile()
        {
            string? tempDirectory = null;
            bool streamingOwnsCleanup = false;
            try
            {
                var (machineID, ncPath) = ExtractMachineAndPath();

                // Path Traversal 방지
                if (ncPath.Contains(".."))
                    return ErrorResponse(400, "Invalid NC path");

                // 입력값 검증
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

                // 다운로드 결과 확인
                using var downloadDoc = JsonDocument.Parse(downloadFileResult);
                int downloadStatus = downloadDoc.RootElement.GetProperty("status").GetInt32();
                if (downloadStatus != 0)
                    return ErrorResponse(502, "Failed to download file from NC machine");

                string fileName = ncPath.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries).Last();
                string filePath = absolutePath + "/" + fileName;

                if (!System.IO.File.Exists(filePath))
                {
                    return ErrorResponse(404, "File not found");
                }

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

        //ncpath 쿼리 경로로의 파일 업로드를 위한 PUT입니다. 일단 게이트웨이가 구동되는 PC에 파일 저장하고 해당 파일을 TORUS를 통해 NC로 전송합니다.
        [HttpPut]
        public async Task<ActionResult<string>> PutTorusUploadFile(IFormFile file)
        {
            string? tempDirectory = null;
            try
            {
                var (machineID, ncPath) = ExtractMachineAndPath();

                // Path Traversal 방지
                if (ncPath.Contains(".."))
                    return ErrorResponse(400, "Invalid NC path");

                // 입력값 검증
                if (machineID < 1)
                    return ErrorResponse(400, "Invalid 'machine' parameter");
                if (string.IsNullOrWhiteSpace(ncPath))
                    return ErrorResponse(400, "'ncpath' parameter is empty");

                if (file == null || file.Length == 0)
                {
                    return ErrorResponse(400, "No file provided or file is empty");
                }

                // 파일 크기 제한 (100MB)
                const long maxFileSize = 100 * 1024 * 1024;
                if (file.Length > maxFileSize)
                    return ErrorResponse(400, $"File size exceeds limit (max: {maxFileSize / 1024 / 1024}MB)");

                string safeFileName = Path.GetFileName(file.FileName);
                // 타임스탬프 + 짧은 GUID로 동시 요청 시 디렉토리 충돌 방지.
                string uniqueName = DateTime.Now.ToString("yyyyMMddHHmmssfff")
                                  + "_" + Guid.NewGuid().ToString("N").Substring(0, 8);
                string filePath = TempDirUpload + "/" + uniqueName + "/" + safeFileName;
                try
                {
                    string? directory = Path.GetDirectoryName(filePath);
                    if (directory != null && !Directory.Exists(directory))
                    {
                        Directory.CreateDirectory(directory);
                    }
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

                // 업로드 결과 확인
                using var uploadDoc = JsonDocument.Parse(uploadFileResult);
                int uploadStatus = uploadDoc.RootElement.GetProperty("status").GetInt32();
                if (uploadStatus != 0)
                    return ErrorResponse(502, "Failed to upload file to NC machine");

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

        //ncpath 쿼리 경로의 파일을 삭제합니다.
        [HttpDelete]
        public ActionResult<string> DeleteTorusUploadFile()
        {
            try
            {
                var (machineID, ncPath) = ExtractMachineAndPath();

                // Path Traversal 방지
                if (ncPath.Contains(".."))
                    return ErrorResponse(400, "Invalid NC path");

                // 입력값 검증
                if (machineID < 1)
                    return ErrorResponse(400, "Invalid 'machine' parameter");
                if (string.IsNullOrWhiteSpace(ncPath))
                    return ErrorResponse(400, "'ncpath' parameter is empty");

                string deleteFileResult = Torus.Torus.Instance.DeleteFile(ncPath, machineID);

                // 삭제 결과 확인
                using var deleteDoc = JsonDocument.Parse(deleteFileResult);
                int deleteStatus = deleteDoc.RootElement.GetProperty("status").GetInt32();
                if (deleteStatus != 0)
                    return ErrorResponse(502, "Failed to delete file from NC machine");

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
    }
}
