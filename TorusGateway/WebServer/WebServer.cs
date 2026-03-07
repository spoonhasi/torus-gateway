using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Threading.Tasks;

namespace TorusGateway.WebServer
{
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

            string timestamp = DateTime.Now.ToString("HH:mm:ss");
            string logEntry = $"[{timestamp}] {clientIp,-15} {method,-6} {path,-40} {statusCode}  {elapsedMs}ms";

            RequestLogged?.Invoke(logEntry);
        }
    }

    public class WebServer : IDisposable
    {
        //ASP.NET Core는 소스코드에서 아래 기준에 맞는 컨트롤러를 자동으로 찾습니다.
        //Class 이름이 Controller로 끝납니다. 예: 'HomeController', 'ValuesController'
        //[ApiController] 또는 [Controller] 속성을 사용합니다. [ApiController]는 Api용, [Controller]는 HTML용입니다. KOS의 웹서버는 항상 [ApiController]를 사용합니다.
        //Controller Class는 반드시 Public이어야 합니다.

        private readonly IHost _webHost;//웹서버의 호스팅 환경 관리 변수
        private bool _disposed;

        public WebServer(string host, int port)
        {
            var hostBuilder = Host.CreateDefaultBuilder();
            hostBuilder.ConfigureWebHostDefaults(webBuilder =>
            {
                webBuilder.UseStartup<Startup>();//Startup 클래스를 앱의 시작 클래스(Entry Point)로 지정
                webBuilder.UseUrls("http://" + host + ":" + port.ToString());//서버가 리스닝할 URL과 포트 설정(http://localhost:5000)
            });
            _webHost = hostBuilder.Build();
        }

        public void Start()
        {
            _ = _webHost.RunAsync().ContinueWith(t =>  // 웹 서버를 비동기적으로 실행합니다.
            {
                System.Diagnostics.Debug.WriteLine($"웹 서버 시작 실패: {t.Exception?.InnerException?.Message}");
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
            services.AddCors(options =>
            {
                options.AddPolicy("AllowAllOrigins", builder =>
                {
                    builder
                        .AllowAnyOrigin()    // 모든 출처 허용
                        .AllowAnyMethod()    // 모든 HTTP 메서드 허용
                        .AllowAnyHeader();   // 모든 헤더 허용
                });
            });

            services.AddControllers(); //여기에서 Controller를 찾습니다.
        }

        public static void Configure(IApplicationBuilder app, IWebHostEnvironment env)
        {
            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();  // 개발 중 에러 페이지를 활성화합니다.
            }

            app.UseMiddleware<RequestLoggingMiddleware>();
            app.UseRouting();

            // CORS 미들웨어 사용
            app.UseCors("AllowAllOrigins");

            app.UseEndpoints(endpoints =>
            {
                endpoints.MapControllers(); // MVC 라우팅
            });
        }
    }
}
