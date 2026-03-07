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
            try
            {
                var (machineID, ncPath) = ExtractMachineAndPath();

                // Path Traversal 방지
                if (ncPath.Contains("..") || Path.IsPathRooted(ncPath))
                    return ErrorResponse(400, "유효하지 않은 NC 경로입니다");

                // 입력값 검증
                if (machineID < 1)
                    return ErrorResponse(400, "machine 파라미터가 유효하지 않습니다");
                if (string.IsNullOrWhiteSpace(ncPath))
                    return ErrorResponse(400, "ncpath 파라미터가 비어있습니다");

                string currentDatetime = DateTime.Now.ToString("yyyyMMddHHmmssfff");
                string absolutePath = Path.GetFullPath(TempDirDownload + "/" + currentDatetime, Directory.GetCurrentDirectory());
                tempDirectory = absolutePath;
                Directory.CreateDirectory(absolutePath);
                string downloadFileResult = Torus.Torus.Instance.DownloadFile(ncPath, absolutePath + "/", machineID);

                // 다운로드 결과 확인
                using var downloadDoc = JsonDocument.Parse(downloadFileResult);
                int downloadStatus = downloadDoc.RootElement.GetProperty("status").GetInt32();
                if (downloadStatus != 0)
                    return ErrorResponse(502, "NC 장비에서 파일 다운로드에 실패했습니다");

                string fileName = Path.GetFileName(ncPath);
                string filePath = absolutePath + "/" + fileName;

                if (!System.IO.File.Exists(filePath))
                {
                    return ErrorResponse(404, "파일을 찾을 수 없습니다");
                }

                // 파일 읽기
                var fileBytes = System.IO.File.ReadAllBytes(filePath);

                // 파일을 다운로드로 전송
                return File(fileBytes, "application/octet-stream", fileName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "NC 파일 다운로드 실패");
                return ErrorResponse(500, "NC 파일 다운로드 중 오류가 발생했습니다");
            }
            finally
            {
                if (tempDirectory != null) try { Directory.Delete(tempDirectory, true); } catch { }
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
                if (ncPath.Contains("..") || Path.IsPathRooted(ncPath))
                    return ErrorResponse(400, "유효하지 않은 NC 경로입니다");

                // 입력값 검증
                if (machineID < 1)
                    return ErrorResponse(400, "machine 파라미터가 유효하지 않습니다");
                if (string.IsNullOrWhiteSpace(ncPath))
                    return ErrorResponse(400, "ncpath 파라미터가 비어있습니다");

                if (file == null || file.Length == 0)
                {
                    return ErrorResponse(400, "파일이 제공되지 않았거나 비어있습니다");
                }

                // 파일 크기 제한 (100MB)
                const long maxFileSize = 100 * 1024 * 1024;
                if (file.Length > maxFileSize)
                    return ErrorResponse(400, $"파일 크기가 제한을 초과합니다 (최대: {maxFileSize / 1024 / 1024}MB)");

                string safeFileName = Path.GetFileName(file.FileName);
                string currentDatetime = DateTime.Now.ToString("yyyyMMddHHmmssfff");
                string filePath = TempDirUpload + "/" + currentDatetime + "/" + safeFileName;
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
                    _logger.LogWarning(ex, "임시 파일 저장 실패");
                    return ErrorResponse(500, "파일 업로드 실패");
                }

                string absolutePath = Path.GetFullPath(filePath, Directory.GetCurrentDirectory());
                tempDirectory = Path.GetDirectoryName(absolutePath);

                string uploadFileResult = Torus.Torus.Instance.UploadFile(absolutePath, ncPath, machineID);

                // 업로드 결과 확인
                using var uploadDoc = JsonDocument.Parse(uploadFileResult);
                int uploadStatus = uploadDoc.RootElement.GetProperty("status").GetInt32();
                if (uploadStatus != 0)
                    return ErrorResponse(502, "NC 장비로 파일 업로드에 실패했습니다");

                return new ContentResult
                {
                    Content = uploadFileResult,
                    ContentType = "application/json",
                    StatusCode = 200
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "NC 파일 업로드 실패");
                return ErrorResponse(500, "NC 파일 업로드 중 오류가 발생했습니다");
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
                if (ncPath.Contains("..") || Path.IsPathRooted(ncPath))
                    return ErrorResponse(400, "유효하지 않은 NC 경로입니다");

                // 입력값 검증
                if (machineID < 1)
                    return ErrorResponse(400, "machine 파라미터가 유효하지 않습니다");
                if (string.IsNullOrWhiteSpace(ncPath))
                    return ErrorResponse(400, "ncpath 파라미터가 비어있습니다");

                string deleteFileResult = Torus.Torus.Instance.DeleteFile(ncPath, machineID);

                // 삭제 결과 확인
                using var deleteDoc = JsonDocument.Parse(deleteFileResult);
                int deleteStatus = deleteDoc.RootElement.GetProperty("status").GetInt32();
                if (deleteStatus != 0)
                    return ErrorResponse(502, "NC 장비에서 파일 삭제에 실패했습니다");

                return new ContentResult
                {
                    Content = deleteFileResult,
                    ContentType = "application/json",
                    StatusCode = 200
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "NC 파일 삭제 실패");
                return ErrorResponse(500, "NC 파일 삭제 중 오류가 발생했습니다");
            }
        }
    }
}
