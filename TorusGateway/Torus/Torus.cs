using System;
using System.Text.Json;
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
            try
            {
                Interlocked.CompareExchange(ref _connectStatus, Api.Initialize(AppGuid, AppName), -1);
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
    }
}
