using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace TorusGateway.WebServer
{
    // 클라이언트 IP 허용 목록. "*"면 전체 허용, 그 외엔 단일 IP(192.168.1.10) 또는 CIDR(192.168.1.0/24) 지원.
    // 런타임 갱신 가능 — Update()로 CSV를 다시 적용하면 내부 상태가 원자적으로 교체된다.
    public class IpAllowlistOptions
    {
        // 내부 상태는 immutable record로 묶어 참조 교체만으로 원자적 갱신.
        private record State(bool AllowAll, List<IPNetwork> Networks);

        private volatile State _state;

        public IpAllowlistOptions(string csv) { _state = Parse(csv); }

        public bool AllowAll => _state.AllowAll;

        // UI에서 IP/모드가 바뀌면 즉시 호출해 새 상태로 교체.
        public void Update(string csv) { _state = Parse(csv); }

        private static State Parse(string csv)
        {
            var networks = new List<IPNetwork>();
            if (string.IsNullOrWhiteSpace(csv)) return new State(false, networks); // 비어있으면 전체 거부

            foreach (var raw in csv.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var entry = raw.Trim();
                if (entry == "*") return new State(true, networks);
                try
                {
                    if (entry.Contains('/'))
                    {
                        networks.Add(IPNetwork.Parse(entry));
                    }
                    else
                    {
                        var ip = IPAddress.Parse(entry);
                        int prefix = ip.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
                        networks.Add(new IPNetwork(ip, prefix));
                    }
                }
                catch { /* 잘못된 항목은 무시 */ }
            }
            return new State(false, networks);
        }

        public bool IsAllowed(IPAddress? ip)
        {
            var s = _state; // 원자적 스냅샷
            if (s.AllowAll) return true;
            if (ip == null) return false;
            if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
            // loopback 정규화: ::1도 127.0.0.1 규칙에 매칭되도록
            if (IPAddress.IsLoopback(ip)) ip = IPAddress.Loopback;
            foreach (var net in s.Networks)
                if (net.Contains(ip)) return true;
            return false;
        }
    }

    public class IpAllowlistMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly IpAllowlistOptions _options;

        public IpAllowlistMiddleware(RequestDelegate next, IpAllowlistOptions options)
        {
            _next = next;
            _options = options;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            if (!_options.IsAllowed(context.Connection.RemoteIpAddress))
            {
                context.Response.StatusCode = 403;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync("{\"status\":-1,\"error\":\"client IP not allowed\"}");
                return;
            }
            await _next(context);
        }
    }

    public class RequestLoggingMiddleware
    {
        private readonly RequestDelegate _next;

        // WPF UI에서 구독할 static 이벤트
        public static event Action<string>? RequestLogged;

        public RequestLoggingMiddleware(RequestDelegate next)
        {
            _next = next;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            await _next(context);
            stopwatch.Stop();

            string clientIp = context.Connection.RemoteIpAddress?.ToString() ?? "-";
            string method = context.Request.Method;
            string path = context.Request.Path + context.Request.QueryString;
            int statusCode = context.Response.StatusCode;
            long elapsedMs = stopwatch.ElapsedMilliseconds;

            // favicon 요청은 로그에서 제외
            if (context.Request.Path.StartsWithSegments("/favicon.ico"))
                return;

            string body = $"{clientIp,-15} {method,-6} {path,-40} {statusCode}  {elapsedMs}ms";
            string timestamp = DateTime.Now.ToString("HH:mm:ss");
            string uiEntry = $"[{timestamp}] {body}";

            RequestLogged?.Invoke(uiEntry);

            // 4xx/5xx만 파일 로그에 남김 (정상 응답은 UI에만 표시).
            if (statusCode >= 500)
                FileLogger.WriteError(body);
            else if (statusCode >= 400)
                FileLogger.WriteWarn(body);
        }
    }

    public class WebServer : IDisposable
    {
        //ASP.NET Core는 소스코드에서 아래 기준에 맞는 컨트롤러를 자동으로 찾습니다.
        //Class 이름이 Controller로 끝납니다. 예: 'HomeController', 'ValuesController'
        //[ApiController] 또는 [Controller] 속성을 사용합니다. [ApiController]는 Api용, [Controller]는 HTML용입니다. KOS의 웹서버는 항상 [ApiController]를 사용합니다.
        //Controller Class는 반드시 Public이어야 합니다.

        private readonly IHost _webHost;//웹서버의 호스팅 환경 관리 변수
        private readonly IpAllowlistOptions _allowlist;
        private bool _disposed;

        public WebServer(string host, int port, string allowedIpsCsv)
        {
            _allowlist = new IpAllowlistOptions(allowedIpsCsv);
            var hostBuilder = Host.CreateDefaultBuilder();
            hostBuilder.ConfigureWebHostDefaults(webBuilder =>
            {
                webBuilder.ConfigureServices(services =>
                {
                    services.AddSingleton(_allowlist); // IpAllowlistMiddleware가 DI로 주입받음
                });
                webBuilder.UseStartup<Startup>();//Startup 클래스를 앱의 시작 클래스(Entry Point)로 지정
                webBuilder.UseUrls("http://" + host + ":" + port.ToString());//서버가 리스닝할 URL과 포트 설정(http://localhost:5000)
            });
            _webHost = hostBuilder.Build();
        }

        // UI에서 IP/모드 변경 시 호출. 재시작 없이 즉시 반영된다.
        public void UpdateAllowlist(string allowedIpsCsv)
        {
            _allowlist.Update(allowedIpsCsv);
        }

        public void Start()
        {
            _ = _webHost.RunAsync().ContinueWith(t =>  // 웹 서버를 비동기적으로 실행합니다.
            {
                System.Diagnostics.Debug.WriteLine($"Web server start failed: {t.Exception?.InnerException?.Message}");
            }, TaskContinuationOptions.OnlyOnFaulted);
        }

        public async Task StopAsync()
        {
            await _webHost.StopAsync();  // 웹 서버를 비동기적으로 정지합니다.
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _webHost.Dispose();
                _disposed = true;
            }
        }
    }

    public class Startup
    {
        public static void ConfigureServices(IServiceCollection services)//앱에 필요한 서비스들을 등록
        {
            // CORS 미설정: Access-Control-Allow-Origin 헤더를 보내지 않음.
            // 브라우저가 cross-origin JS 호출을 자체 차단하므로 악성 웹페이지를 통한 CSRF를 방어함.
            // 서버사이드 호출(Next.js SSR, curl, HttpClient 등)은 CORS와 무관하므로 정상 동작.
            services.AddControllers(); //여기에서 Controller를 찾습니다.
        }

        public static void Configure(IApplicationBuilder app, IWebHostEnvironment env)
        {
            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();  // 개발 중 에러 페이지를 활성화합니다.
            }

            app.UseMiddleware<RequestLoggingMiddleware>();
            app.UseMiddleware<IpAllowlistMiddleware>(); // 로깅 뒤에 두어 차단된 요청도 403으로 로그에 남게 함
            app.UseRouting();

            app.UseEndpoints(endpoints =>
            {
                endpoints.MapControllers(); // MVC 라우팅
            });
        }
    }
}
