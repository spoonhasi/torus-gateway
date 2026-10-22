using System.IO;
using System.Net;
using Opc.Ua;
using Opc.Ua.Configuration;

namespace TorusGateway.OpcUa
{
    // OPC UA 서버 수명 관리. 설정으로 ApplicationConfiguration을 만들고, 인증서를 준비한 뒤 서버를 시작/종료한다.
    // 인증서 저장소는 실행 파일 위치 기준 pki/ 아래에 둔다(own: 서버 인증서, trusted: 허용한 클라이언트,
    // issuer: 발급자, rejected: 거부된 클라이언트 — 여기서 trusted로 옮기면 그 클라이언트를 허용).
    internal sealed class OpcUaHost : IDisposable
    {
        private ApplicationInstance? _application;
        private TorusOpcUaServer? _server;

        internal static string PkiRoot => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "pki");

        internal int MonitoredNodeCount => _server?.NodeManager?.MonitoredNodeCount ?? 0;

        private static string PolicyUri(string name) => name switch
        {
            "Basic256Sha256" => SecurityPolicies.Basic256Sha256,
            "Aes128_Sha256_RsaOaep" => SecurityPolicies.Aes128_Sha256_RsaOaep,
            "Aes256_Sha256_RsaPss" => SecurityPolicies.Aes256_Sha256_RsaPss,
            _ => SecurityPolicies.None,
        };

        private static MessageSecurityMode ModeOf(string name) => name switch
        {
            "Sign" => MessageSecurityMode.Sign,
            "SignAndEncrypt" => MessageSecurityMode.SignAndEncrypt,
            _ => MessageSecurityMode.None,
        };

        private static CertificateTrustList Store(string name) =>
            new() { StoreType = CertificateStoreType.Directory, StorePath = Path.Combine(PkiRoot, name) };

        public async Task StartAsync(OpcUaSettings settings, Action<string>? log)
        {
            if (_server != null) return;
            string? invalid = OpcUaSettings.Validate(settings);
            if (invalid != null) throw new ArgumentException(invalid);

            string host = Dns.GetHostName();
            bool channelSecured = settings.SecurityPolicy != "None";

            var serverConfig = new ServerConfiguration
            {
                BaseAddresses = new StringCollection { settings.EndpointUrl },
                SecurityPolicies = new ServerSecurityPolicyCollection
                {
                    new ServerSecurityPolicy { SecurityPolicyUri = PolicyUri(settings.SecurityPolicy), SecurityMode = ModeOf(settings.SecurityMode) },
                },
                // 보안 채널이 없을 때(None) 사용자 이름 토큰은 자체적으로 암호화해 비밀번호가 평문으로 오가지 않게 한다.
                UserTokenPolicies = new UserTokenPolicyCollection
                {
                    settings.UseUsername
                        ? new UserTokenPolicy(UserTokenType.UserName) { SecurityPolicyUri = channelSecured ? null : SecurityPolicies.Basic256Sha256 }
                        : new UserTokenPolicy(UserTokenType.Anonymous),
                },
                MinRequestThreadCount = 5,
                MaxRequestThreadCount = 100,
                MaxQueuedRequestCount = 200,
            };

            var config = new ApplicationConfiguration
            {
                ApplicationName = "TorusGateway",
                ApplicationUri = $"urn:{host}:TorusGateway",
                ProductUri = "urn:TorusGateway",
                ApplicationType = ApplicationType.Server,
                ServerConfiguration = serverConfig,
                SecurityConfiguration = new SecurityConfiguration
                {
                    ApplicationCertificates = new CertificateIdentifierCollection
                    {
                        new CertificateIdentifier
                        {
                            StoreType = CertificateStoreType.Directory,
                            StorePath = Path.Combine(PkiRoot, "own"),
                            SubjectName = $"CN=TorusGateway, DC={host}",
                            CertificateType = ObjectTypeIds.RsaSha256ApplicationCertificateType,
                        },
                    },
                    TrustedPeerCertificates = Store("trusted"),
                    TrustedIssuerCertificates = Store("issuer"),
                    RejectedCertificateStore = Store("rejected"),
                    AutoAcceptUntrustedCertificates = settings.AutoAcceptUntrustedCertificates,
                    AddAppCertToTrustedStore = false,
                },
                TransportConfigurations = new TransportConfigurationCollection(),
                TransportQuotas = new TransportQuotas { OperationTimeout = 15000 },
            };
            await config.ValidateAsync(ApplicationType.Server, CancellationToken.None);

            var application = new ApplicationInstance(config, DefaultTelemetry.Create(_ => { }));
            // 서버 인증서가 없으면 자체 서명 인증서를 만든다(pki/own).
            bool certificateOk;
            try
            {
                certificateOk = await application.CheckApplicationInstanceCertificatesAsync(false, null, CancellationToken.None);
            }
            catch (ServiceResultException ex)
            {
                // 인증서는 처음 시작한 엔드포인트 주소와 PC 이름으로 만들어진다. 이후 주소(예: 0.0.0.0 → 특정 IP)나
                // PC 이름이 바뀌면 맞지 않아 거부되므로 지우고 새로 만든다. 서버 인증서를 신뢰해 둔 클라이언트는
                // 새 인증서를 다시 신뢰해야 한다.
                log?.Invoke($"CERT    existing server certificate rejected ({ex.Message.Split('\n')[0]}); creating a new one. " +
                            "Clients that trusted the old server certificate must trust the new one.");
                DeleteOwnCertificates();
                certificateOk = await application.CheckApplicationInstanceCertificatesAsync(false, null, CancellationToken.None);
            }
            if (!certificateOk) throw new InvalidOperationException("Failed to prepare the OPC UA application certificate.");

            var server = new TorusOpcUaServer(settings, log);
            try
            {
                await application.StartAsync(server);
            }
            catch
            {
                server.Dispose(); // 포트 사용 중 등으로 시작에 실패하면 노드 관리자(폴링 스레드)까지 정리
                throw;
            }
            _application = application;
            _server = server;
        }

        private static void DeleteOwnCertificates()
        {
            foreach (string dir in new[] { Path.Combine(PkiRoot, "own", "certs"), Path.Combine(PkiRoot, "own", "private") })
            {
                if (!Directory.Exists(dir)) continue;
                foreach (string file in Directory.GetFiles(dir)) File.Delete(file);
            }
        }

        public async Task StopAsync()
        {
            var application = _application;
            var server = _server;
            _application = null;
            _server = null;
            try
            {
                if (application != null) await application.StopAsync();
            }
            finally
            {
                server?.Dispose(); // 종료 중 예외가 나도 노드 관리자(폴링 스레드)는 정리
            }
        }

        public void Dispose()
        {
            try { StopAsync().GetAwaiter().GetResult(); } catch { }
        }
    }
}
