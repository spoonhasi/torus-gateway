using System.Collections.Generic;

namespace TorusGateway.WebServer
{
    // TORUS 네이티브 API의 NC_RET enum 값을 사람이 읽을 수 있는 설명으로 매핑.
    // 새 코드가 추가되면 여기에 보강하면 된다.
    internal static class TorusStatusCodes
    {
        private static readonly Dictionary<int, string> Descriptions = new()
        {
            { 0x0,        "Success" },
            { 0x21B00000, "Internal exception" },
            { 0x21B10000, "Initialization failed" },
            { 0x21B20000, "No initial value" },
            { 0x21B30000, "define_number is not defined in the code" },
            { 0x21B40000, "No function matching define_number" },
            { 0x21B50000, "Duplicate writer on the same item" },
            { 0x21B60000, "Connection failed" },
            { 0x21B61000, "No machine object" },
            { 0x21B62000, "Connection not attempted" },
            { 0x21B70000, "Function not available on this thread (reconnect mismatch)" },
            { 0x21B80000, "No option" },
            { 0x21B81000, "DLL file not found" },
            { 0x21B82000, "No handle" },
            { 0x21B83000, "No active tool or tool group" },
            { 0x21B84000, "Invalid tool system" },
            { 0x21B85000, "No tool group" },
            { 0x21B86000, "No selected file" },
            { 0x21B87000, "No tool in tool manager" },
            { 0x21B90000, "Invalid write value" },
            { 0x21B91000, "Write list count mismatch" },
            { 0x21B92000, "Operation not allowed in current state" },
            { 0x21BA0000, "Invalid filter (I/J/K/L) value" },
            { 0x21BB0000, "Unknown error" },
            { 0x21BC1000, "Object (file, etc.) not found on NC" },
            { 0x21BC1100, "Object not found on local" },
            { 0x21BC1200, "Invalid NC path" },
            { 0x21BC1300, "Invalid local path" },
            { 0x21BC2000, "Object already exists on NC" },
            { 0x21BC2100, "Object already exists on local" },
            { 0x21BC3000, "Failed to create NC object" },
            { 0x21BC3100, "Failed to create local object" },
            { 0x21BC4000, "Object in use on NC" },
            { 0x21BC4100, "Object in use on local" },
            { 0x21BC5000, "Insufficient storage on NC" },
            { 0x21BC6000, "NC file error" },
            { 0x21BC6100, "Invalid NC file name" },
            { 0x21BC7000, "Failed to open NC object" },
            { 0x21BC7100, "Failed to open local object" },
            { 0x21BD0000, "Return data type mismatch" },
            { 0x21BE0000, "Null value" },
            { 0x21BE1000, "No value" },
            { 0x21BE2000, "Timeseries already being collected" },
            { 0x21BE3000, "No active stream (or not continuous)" },
            { 0x21BE4000, "Reconnect attempted from a thread different from the one that created the connection" },
            { 0x21BE5000, "Not a timeseries instance" },
            { 0x21BF0000, "No symbol data" },
        };

        public static string GetDescription(int statusCode)
        {
            return Descriptions.TryGetValue(statusCode, out var desc)
                ? desc
                : $"Unknown status code: 0x{statusCode:X8}";
        }
    }
}
