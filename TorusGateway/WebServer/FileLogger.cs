using System;
using System.IO;
using System.Linq;

namespace TorusGateway.WebServer
{
    // 일자별 로그 파일 + 총 사이즈 캡. ERROR/WARN만 기록되는 가정.
    // 파일은 항상 열고 닫는 방식(File.AppendAllText)으로 외부 도구가 동시에 읽어도 안전.
    public static class FileLogger
    {
        private static readonly object _lock = new();
        private static string _logDir = "";
        private static long _maxTotalBytes;
        private static bool _enabled;
        private static DateTime _lastWriteDate = DateTime.MinValue;

        // maxTotalMB가 0 이하면 파일 로그 비활성화.
        public static void Init(int maxTotalMB)
        {
            lock (_lock)
            {
                _logDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
                _maxTotalBytes = (long)maxTotalMB * 1024 * 1024;
                _enabled = maxTotalMB > 0;
                if (!_enabled) return;

                try
                {
                    Directory.CreateDirectory(_logDir);
                    EnforceSizeCap();
                }
                catch
                {
                    _enabled = false;
                }
            }
        }

        public static void WriteWarn(string message) => Write("WARN ", message);
        public static void WriteError(string message) => Write("ERROR", message);

        private static void Write(string level, string message)
        {
            if (!_enabled) return;
            var now = DateTime.Now;
            var line = $"{now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}";

            lock (_lock)
            {
                try
                {
                    // 일자 전환 시 캡 적용 (하루에 한 번 정도면 충분)
                    if (now.Date != _lastWriteDate)
                    {
                        _lastWriteDate = now.Date;
                        EnforceSizeCap();
                    }
                    var path = Path.Combine(_logDir, $"{now:yyyy-MM-dd}.log");
                    File.AppendAllText(path, line);
                }
                catch
                {
                    // 파일 쓰기 실패는 무시 — UI 동작에 영향 주지 않음
                }
            }
        }

        // 총 사이즈가 캡을 넘으면 오래된 파일부터 삭제. 가장 최근 파일은 항상 보존.
        private static void EnforceSizeCap()
        {
            try
            {
                var dir = new DirectoryInfo(_logDir);
                if (!dir.Exists) return;

                var files = dir.GetFiles("*.log")
                    .OrderBy(f => f.LastWriteTimeUtc)
                    .ToList();

                long total = files.Sum(f => f.Length);
                int i = 0;
                while (total > _maxTotalBytes && i < files.Count - 1)
                {
                    total -= files[i].Length;
                    try { files[i].Delete(); } catch { }
                    i++;
                }
            }
            catch { }
        }
    }
}
