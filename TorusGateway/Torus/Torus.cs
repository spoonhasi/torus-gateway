using System;
using System.Text.Json;
using System.Text.Json.Nodes;
using IntelligentApiCS;

namespace TorusGateway.Torus
{
    public class Torus
    {
        private static readonly object _lockInit = new();
        // 네이티브 TORUS API의 스레드 안전성이 보장되지 않으므로 모든 호출을 직렬화한다.
        // 동시 요청 처리량은 떨어지지만 정확성을 우선함.
        private static readonly object _apiLock = new();
        private static Torus? _instance;

        // 한글 머신명 등이 \uXXXX로 이스케이프되지 않도록 하는 직렬화 옵션.
        private static readonly JsonSerializerOptions _relaxedJson = new()
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        public string AppName { get; private set; }
        public Guid AppGuid { get; private set; }
        private int _connectStatus = -1;
        public string LastErrorMessage { get; private set; } = "";
        public static void Initialize(string appName, string appGuid)
        {
            if (_instance == null)
            {
                lock (_lockInit)
                {
                    _instance ??= new Torus(appName, appGuid);
                }
            }
        }
        private Torus(string appName, string appGuid)
        {
            AppName = appName;
            AppGuid = new Guid(appGuid);
        }
        public static Torus Instance
        {
            get
            {
                if (_instance == null)
                {
                    throw new InvalidOperationException("Torus is not initialized. Call Initialize first.");
                }
                return _instance;
            }
        }
        public int Connect()
        {
            // 이미 한 번 시도했으면(_connectStatus != -1) 네이티브 재초기화를 하지 않고 캐시된 상태를 반환.
            // TORUS API는 Uninitialize가 없어 재연결은 프로세스 재시작으로만 가능하므로,
            // 같은 프로세스에서 Api.Initialize는 한 번만 호출되어야 한다.
            if (_connectStatus != -1)
                return _connectStatus;
            try
            {
                _connectStatus = Api.Initialize(AppGuid, AppName);
            }
            catch (System.BadImageFormatException)
            {
                LastErrorMessage = "Error: Incorrect Build Mode - Please ensure the application is built in the correct 32-bit or 64-bit mode.";
            }
            catch (DllNotFoundException)
            {
                LastErrorMessage = "Failed to connect to Torus: DLL not found";
            }
            catch (System.IO.FileNotFoundException)
            {
                LastErrorMessage = "Failed to connect to Torus: DLL not found";
            }
            return _connectStatus;
        }

        public string GetData(string address, string filter)
        {
            lock (_apiLock)
            {
                int result = Api.getData(address, filter, out Item item, true);
                if (item == null)
                {
                    return JsonSerializer.Serialize(new { status = result });
                }
                else
                {
                    return item.ToString();
                }
            }
        }

        public string UpdateData(string address, string filter, string value)
        {
            lock (_apiLock)
            {
                Item InItem = Item.Parse(value);
                int result = Api.updateData(address, filter, InItem, out Item _);
                return JsonSerializer.Serialize(new { status = result });
            }
        }

        public string UploadFile(string localPath, string ncPath, int machineID)
        {
            lock (_apiLock)
            {
                int result = Api.UploadFile(localPath, ncPath, machineID);
                return JsonSerializer.Serialize(new { status = result });
            }
        }

        public string DownloadFile(string ncPath, string localPath, int machineID)
        {
            lock (_apiLock)
            {
                int result = Api.DownloadFile(ncPath, localPath, machineID);
                return JsonSerializer.Serialize(new { status = result });
            }
        }

        public string DeleteFile(string ncPath, int machineID)
        {
            lock (_apiLock)
            {
                int result = Api.CNCFileDelete(ncPath, out Item _, machineID);
                return JsonSerializer.Serialize(new { status = result });
            }
        }

        public string ExecuteCNCFile(int channel, string ncPath, int machineID)
        {
            lock (_apiLock)
            {
                int result = Api.CNCFileExecute(channel, ncPath, out Item _, machineID);
                return JsonSerializer.Serialize(new { status = result });
            }
        }

        public string GetMachinesInfo()
        {
            lock (_apiLock)
            {
                int result = Api.getMachinesInfo(out Item item);
                if (item == null)
                    return JsonSerializer.Serialize(new { status = result });

                return InjectStatus(item.ToString(), result);
            }
        }

        // getMachinesInfo 응답에는 status 필드가 없으므로 JSON에 주입한다.
        // 과거엔 문자열에서 '{'를 직접 찾아 끼워넣어, 루트가 배열이면 첫 원소 안에
        // 잘못 주입되는 등 취약했다. 루트 종류를 판별해 안전하게 처리한다.
        //  - 객체: status 키 추가/덮어쓰기
        //  - 객체가 아님(배열/스칼라/null): value로 감싸 status와 함께 반환
        //  - 파싱 불가: {"status":<status>}만 반환
        internal static string InjectStatus(string itemJson, int status)
        {
            JsonNode? root;
            try
            {
                root = JsonNode.Parse(itemJson);
            }
            catch (JsonException)
            {
                return JsonSerializer.Serialize(new { status });
            }

            if (root is JsonObject obj)
            {
                obj["status"] = status; // 없으면 추가, 이미 있으면 덮어씀
                return obj.ToJsonString(_relaxedJson);
            }

            // 루트가 객체가 아니면(배열/스칼라/null) value로 감싸 status와 함께 반환.
            return new JsonObject { ["status"] = status, ["value"] = root }.ToJsonString(_relaxedJson);
        }
    }
}
