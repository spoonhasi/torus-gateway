using System.Security.Cryptography;
using System.Text;

namespace TorusGateway.OpcUa
{
    // OPC UA 서버 설정. config.ini의 [OpcUa] 섹션에 저장한다.
    internal sealed class OpcUaSettings
    {
        private const string Section = "OpcUa";

        // UI에 노출하는 보안 정책. 폐기된 Basic128Rsa15/Basic256은 제외.
        internal static readonly string[] SecurityPolicyNames = { "None", "Basic256Sha256", "Aes128_Sha256_RsaOaep", "Aes256_Sha256_RsaPss" };
        internal static readonly string[] SecurityModeNames = { "None", "Sign", "SignAndEncrypt" };

        public string EndpointUrl { get; set; } = "opc.tcp://0.0.0.0:4840";
        public string SecurityPolicy { get; set; } = "None";
        public string SecurityMode { get; set; } = "None";
        public bool UseUsername { get; set; }
        public string Username { get; set; } = "";
        public string Password { get; set; } = "";
        // 신뢰 목록에 없는 클라이언트 인증서를 자동 수락할지. 기본은 거부(pki/rejected에 쌓이고, trusted로 옮기면 허용).
        public bool AutoAcceptUntrustedCertificates { get; set; }
        // 구독 중인 노드를 TORUS에서 다시 읽는 주기(ms). config.ini에서만 조정.
        public int PollIntervalMs { get; set; } = 100;

        // 설정 검증. 문제가 없으면 null, 있으면 사용자에게 보여줄 메시지.
        internal static string? Validate(OpcUaSettings s)
        {
            if (!Uri.TryCreate(s.EndpointUrl, UriKind.Absolute, out var uri) || uri.Scheme != "opc.tcp"
                || string.IsNullOrEmpty(uri.Host) || uri.Port < 1 || uri.Port > 65535)
                return "Endpoint must look like opc.tcp://<host>:<port>";
            if (!SecurityPolicyNames.Contains(s.SecurityPolicy)) return "Unknown security policy";
            if (!SecurityModeNames.Contains(s.SecurityMode)) return "Unknown security mode";
            if ((s.SecurityPolicy == "None") != (s.SecurityMode == "None"))
                return "Security policy None must be used with mode None (and vice versa)";
            if (s.UseUsername && (string.IsNullOrWhiteSpace(s.Username) || string.IsNullOrEmpty(s.Password)))
                return "Username and password are required for username authentication";
            if (s.PollIntervalMs < 10) return "PollIntervalMs must be at least 10";
            return null;
        }

        internal static OpcUaSettings Load(string configFile)
        {
            var s = new OpcUaSettings
            {
                EndpointUrl = ConfigManager.Read(configFile, Section, "EndpointUrl", "opc.tcp://0.0.0.0:4840"),
                SecurityPolicy = ConfigManager.Read(configFile, Section, "SecurityPolicy", "None"),
                SecurityMode = ConfigManager.Read(configFile, Section, "SecurityMode", "None"),
                UseUsername = ConfigManager.Read(configFile, Section, "Authentication", "Anonymous") == "Username",
                Username = ConfigManager.Read(configFile, Section, "Username", ""),
                Password = Unprotect(ConfigManager.Read(configFile, Section, "PasswordProtected", "")),
                AutoAcceptUntrustedCertificates = ConfigManager.Read(configFile, Section, "AutoAcceptUntrustedCertificates", "false")
                    .Equals("true", StringComparison.OrdinalIgnoreCase),
            };
            if (int.TryParse(ConfigManager.Read(configFile, Section, "PollIntervalMs", "100"), out int poll)) s.PollIntervalMs = poll;
            return s;
        }

        internal void Save(string configFile)
        {
            ConfigManager.Write(configFile, Section, "EndpointUrl", EndpointUrl);
            ConfigManager.Write(configFile, Section, "SecurityPolicy", SecurityPolicy);
            ConfigManager.Write(configFile, Section, "SecurityMode", SecurityMode);
            ConfigManager.Write(configFile, Section, "Authentication", UseUsername ? "Username" : "Anonymous");
            ConfigManager.Write(configFile, Section, "Username", Username);
            ConfigManager.Write(configFile, Section, "PasswordProtected", Protect(Password));
            ConfigManager.Write(configFile, Section, "AutoAcceptUntrustedCertificates", AutoAcceptUntrustedCertificates ? "true" : "false");
        }

        // 비밀번호는 평문 대신 현재 Windows 사용자 계정으로 암호화(DPAPI)해 저장한다.
        // 다른 계정이나 PC에서는 복호화되지 않으므로 그 경우 빈 값으로 읽힌다.
        private static string Protect(string plain) =>
            plain.Length == 0 ? "" : Convert.ToBase64String(
                ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), null, DataProtectionScope.CurrentUser));

        private static string Unprotect(string stored)
        {
            if (stored.Length == 0) return "";
            try
            {
                return Encoding.UTF8.GetString(
                    ProtectedData.Unprotect(Convert.FromBase64String(stored), null, DataProtectionScope.CurrentUser));
            }
            catch (Exception) { return ""; }
        }
    }
}
