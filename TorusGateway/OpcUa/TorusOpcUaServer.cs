using System.Security.Cryptography;
using System.Text;
using Opc.Ua;
using Opc.Ua.Server;

namespace TorusGateway.OpcUa
{
    // TORUS OPC UA 서버. 노드는 TorusNodeManager가 관리하고, 여기서는 서버 정보와 사용자 인증을 담당한다.
    internal sealed class TorusOpcUaServer : StandardServer
    {
        private readonly OpcUaSettings _settings;
        private readonly Action<string>? _log;

        internal TorusNodeManager? NodeManager { get; private set; }

        public TorusOpcUaServer(OpcUaSettings settings, Action<string>? log)
        {
            _settings = settings;
            _log = log;
        }

        protected override ServerProperties LoadServerProperties() => new()
        {
            ManufacturerName = "TORUS Gateway",
            ProductName = "TORUS Gateway OPC UA Server",
            ProductUri = "urn:TorusGateway",
            SoftwareVersion = Utils.GetAssemblySoftwareVersion(),
            BuildNumber = Utils.GetAssemblyBuildNumber(),
            BuildDate = Utils.GetAssemblyTimestamp(),
        };

        protected override MasterNodeManager CreateMasterNodeManager(IServerInternal server, ApplicationConfiguration configuration)
        {
            NodeManager = new TorusNodeManager(server, configuration, _settings.PollIntervalMs, _log);
            return new MasterNodeManager(server, configuration, null, new INodeManager[] { NodeManager });
        }

        protected override void OnServerStarted(IServerInternal server)
        {
            base.OnServerStarted(server);
            server.SessionManager.ImpersonateUser += (session, args) => Impersonate(args);
        }

        // 설정한 인증 방식만 허용한다: 익명 모드면 익명만, 사용자 이름 모드면 일치하는 사용자 이름/비밀번호만.
        private void Impersonate(ImpersonateEventArgs args)
        {
            if (args.NewIdentity is UserNameIdentityToken token)
            {
                if (!_settings.UseUsername || !Matches(token))
                {
                    _log?.Invoke($"AUTH    rejected user '{token.UserName}'");
                    throw ServiceResultException.Create(StatusCodes.BadUserAccessDenied, "Invalid username or password.");
                }
                args.Identity = new UserIdentity(token);
                args.Identity.GrantedRoleIds.Add(ObjectIds.WellKnownRole_AuthenticatedUser);
                return;
            }

            if (args.NewIdentity == null || args.NewIdentity is AnonymousIdentityToken)
            {
                if (_settings.UseUsername)
                {
                    _log?.Invoke("AUTH    rejected anonymous");
                    throw ServiceResultException.Create(StatusCodes.BadIdentityTokenRejected, "Anonymous access is disabled.");
                }
                args.Identity = new UserIdentity();
                args.Identity.GrantedRoleIds.Add(ObjectIds.WellKnownRole_Anonymous);
                return;
            }

            _log?.Invoke($"AUTH    rejected {args.NewIdentity.GetType().Name}");
            throw ServiceResultException.Create(StatusCodes.BadIdentityTokenRejected, "Unsupported user identity token.");
        }

        // 길이 정보 외에는 시간 차이가 나지 않도록 고정 시간 비교.
        private bool Matches(UserNameIdentityToken token)
        {
            byte[] password = token.DecryptedPassword ?? Array.Empty<byte>();
            bool userOk = CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(token.UserName ?? ""), Encoding.UTF8.GetBytes(_settings.Username));
            bool passwordOk = CryptographicOperations.FixedTimeEquals(password, Encoding.UTF8.GetBytes(_settings.Password));
            return userOk && passwordOk;
        }
    }
}
