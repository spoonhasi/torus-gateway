# TORUS Gateway

## 프로젝트 개요
TORUS(통합 CNC 인터페이스 플랫폼)를 HTTP REST API로 노출하는 WPF 데스크탑 게이트웨이.
논문 "The Development of REST API Gateway Application Using Unified CNC Interface Platform"
(IJPEM-ST, https://doi.org/10.57062/ijpem-st.2024.00171)에서 소개한 프로그램의 구현이다.

**유지보수 종료.** v2.0.0은 논문 설계(그림 4·5·6)에 맞춰 정리한 최종 버전이다.
v1.2.3~v1.7.1에 있던 과도기 기능(커스텀 엔드포인트, 응답 필터링, body 정규화, NC 탐색기, 문서 창,
콘솔 임베딩)은 v2.0.0에서 제거했다. 마지막 운영 버전은 태그 `v1.7.1`.

## 기술 스택
- .NET 8.0 (Windows), WPF + ASP.NET Core 내장 웹서버, C#
- **x86 필수** (TORUS 네이티브 DLL이 32비트)
- `TorusApi/`: TORUS SDK v2.3.3의 DLL 중 실제 의존하는 14개만 보관
  (IntelligentApiCS, ItemCS는 관리 어셈블리 참조, 나머지는 그 의존 네이티브 DLL)
- 테스트: xUnit (`TorusGateway.Tests/`)

## 프로젝트 구조
```
TorusGateway.sln
TorusGateway/
  App.xaml(.cs)              # 단일 인스턴스 Mutex, 수동 MainWindow 생성, 종료 시 임시 디렉토리 정리
  MainWindow.xaml(.cs)       # Torus 연결, 웹서버 open/close, Test Request, IP 허용목록(런타임 갱신), 요청 로그, 트레이/종료
  TestRequestWindow.xaml(.cs)# HTTP 테스터 (GET/PUT/DELETE, JSON body, 모달)
  ConfigManager.cs           # config.ini (Win32 INI) 읽기/쓰기 헬퍼 (static)
  Torus/
    Torus.cs                 # TORUS API 싱글턴 래퍼. _apiLock으로 모든 네이티브 호출 직렬화
                             # GetData, UpdateData, UploadFile, DownloadFile, DeleteFile, GetMachinesInfo
    MachineListControl.xaml(.cs)  # MachineList.xml 편집 UserControl (재사용 가능)
    MachineListWindow.xaml(.cs)   # 위 컨트롤을 감싼 모달 창
  WebServer/
    WebServer.cs             # ASP.NET Core 호스트 + 미들웨어(IpAllowlistMiddleware, RequestLoggingMiddleware, IpAllowlistOptions)
    MachineController.cs     # GET/PUT /machine/{*segments}, GET /machineList, GET /, GET /health
                             # BuildAddressAndFilter(internal static): 경로→주소, 쿼리→필터, 구분자 주입 거부
    FileController.cs        # GET/PUT/DELETE /file/machine/ncpath + 임시 디렉토리 관리
    TorusStatusCodes.cs      # TORUS NC_RET 값 → 설명 (파일 전송 실패 응답에 사용)
    FileLogger.cs            # 일자별 파일 로그(static). 4xx/5xx만, 총 사이즈 캡
TorusGateway.Tests/
  BuildAddressAndFilterTests.cs
  RequestValidationTests.cs  # ValidateUpdateBody, FileController.FileNameOf
  TorusStatusCodesTests.cs
  IpAllowlistOptionsTests.cs
  ConfigManagerTests.cs
TorusApi/                    # TORUS SDK v2.3.3 DLL 14개 (빌드 시 출력 디렉토리로 복사)
docs/                        # 논문 그림 PNG (원본 TIFF는 .gitignore)
TorusGateway.info            # 앱 메타데이터 (이름, 버전, GUID)
```

## 빌드 / 테스트
```bash
dotnet build TorusGateway.sln -c Release
dotnet test TorusGateway.Tests/TorusGateway.Tests.csproj
```
- PreBuild: `TorusApi/*.dll` → 출력 디렉토리 복사 (`$(SolutionDir)` 폴백으로 솔루션 외부 빌드도 동작)
- PostBuild: `TorusGateway.info` → 출력 디렉토리 복사
- 앱이 실행 중이면 출력 디렉토리 DLL이 잠겨 PreBuild 복사가 실패한다. 실행 중 컴파일만 확인하려면 `-o <임시 경로>`로 빌드.
- 테스트 프로젝트는 `InternalsVisibleTo`로 internal 헬퍼에 접근

## API (논문 그림 4·5·6과 동일)
- `GET /machine/{*segments}?{query}` → `Api.getData("data://machine/{segments}", query)`. TORUS Item JSON을 **가공 없이** 반환.
- `PUT /machine/{*segments}?{query}` + body → `Api.updateData`. body는 TORUS Item JSON 그대로(`{"value":[12.34]}`), 응답 `{"status":N}`.
  **TORUS로 넘기기 전에 `ValidateUpdateBody`로 검사** — 루트 객체, 소문자 `value`, 비어 있지 않은 배열이 아니면 400.
  TORUS v2.3.3은 `{"value":12.34}`(배열 아님)를 받으면 mgrCommunication이 libSharedMap.dll 접근 위반으로 크래시해
  모든 설비 통신이 끊긴다(2026-10-22 시뮬레이터에서 재현). v1.2.3의 "자동 배열 변환"은 이 때문에 필요했던 것.
  원소 타입은 모델마다 달라 제한하지 않는다. 다른 잘못된 Item이 크래시를 일으키는지는 미확인.
- 쿼리 키·값은 가공 없이 `key=value`를 `&`로 잇는다(대소문자 정규화 없음). 디코딩된 경로 세그먼트에 `?`/`&`/`=`,
  쿼리 키·값에 `&`/`=`가 있으면 400 (필터 주입 방지).
- `GET|PUT|DELETE /file/machine/ncpath?machine=N&ncpath=<CNC 경로>` → DownloadFile / UploadFile(multipart `file`) / CNCFileDelete.
  쿼리 키는 정확히 `machine`, `ncpath`. `..` 거부, `/`·`\`로 끝나는(파일 이름 없는) ncpath는 GET/PUT에서 400.
  업로드 100MB(RequestSizeLimit/RequestFormLimits).
  업로드 임시 파일은 ncpath의 파일 이름으로 저장. TORUS 실패 시 502 `{"status":<코드>,"error":<설명>}`.
- `GET /machineList` → `Api.getMachinesInfo` Item 그대로. `GET /` → `{"status":"ok"}`, `GET /health` → `{"status":0,"value":"ok"}`.
- 앱 자체 오류: `{"status":-1,"error":"<message>"}`, HTTP 400/404/500.

## 동작 흐름
1. **Torus Connect** → `Torus.Connect()` (`Api.Initialize`). 실패 시 "Reconnect" → 앱 재시작(`--auto-connect`).
   TORUS API에 Uninitialize가 없어 재연결은 프로세스 재시작으로만 가능.
2. **WebServer Open** → ASP.NET Core 시작 (기본 `0.0.0.0:5001`)
3. 요청 → 미들웨어(로그, IP 허용목록) → 컨트롤러 → `Torus` 싱글턴 → 네이티브 API

## 단일 인스턴스
- Named Mutex(`TorusGateway_SingleInstance`)로 중복 실행 방지. `--auto-connect` 재시작 시에는 건너뜀.

## 모달 창 가드
- `MainWindow._modalDepth`로 모달(`MachineListWindow`/`TestRequestWindow`) 열림을 추적.
  열려 있는 동안 종료/트레이 이동/최소화를 차단 (작업표시줄 입력 대비).

## Test Request 창
- Base URL은 메인 창 Host/Port (`0.0.0.0`/`*`/빈 값은 `127.0.0.1`).
- GET/PUT/DELETE. PUT만 body 활성, 비어 있으면 `{"value": []}` 템플릿. 10초 타임아웃. 파일 업로드(multipart)는 지원하지 않음.

## MachineList.xml 관리
- TORUS Settings 버튼 → `MachineListWindow` 모달. 경로가 실제 파일이면 자동 Read.
- NCmachine 속성: activate, name, id, vendorCode, address, port, exDllPath, connectCode, ncVersionCode, toolSystem, username, password
- vendorCode: FANUC, SIEMENS, CSCAM, MITSUBISHI, KCNC, MAZAK, HEIDENHAIN (대문자)
- Run/Exit 버튼은 설정한 bat를 외부 프로세스로 실행만 한다.

## 동시성
- `Torus._apiLock`으로 모든 네이티브 호출 직렬화. 한 호출이 막히면 후속 호출 모두 대기(타임아웃 없음).

## 임시 디렉토리
- `FileController.TempDirDownload`/`TempDirUpload`, 실행 파일 디렉토리(BaseDirectory) 기준.
- 요청마다 `yyyyMMddHHmmssfff_<GUID8>` 하위 디렉토리. 다운로드는 응답 완료(`OnCompleted`) 후, 그 외는 `finally`에서 정리.
- 크래시 잔여물은 시작 시 `FileController.CleanupOrphanedTempDirs()`, 종료 시 `App.OnExit`가 정리. 실패는 `FileLogger.WriteWarn`.

## API 요청 로그 (UI)
- `RequestLoggingMiddleware`가 요청마다 `RequestLogInfo`를 이벤트로 발행 → `MainWindow`가 200ms 타이머로 반영. favicon 제외.
- **GET 정기 / GET 간헐 / PUT·DELETE** 세 패널(GridSplitter). 패널당 1000줄 캡, 맨 아래일 때만 자동 스크롤.
- GET 주기성 집계(`ProcessGet`): 키=`IP + 경로+쿼리`. 도착 간격 EMA의 변동계수가 작고(≤`RegularMaxCv`) 충분히 반복(≥`RegularMinCount`),
  주기가 빠르면(≤`RegularMaxPeriodMs`) 정기 표로 승격. `/health`는 1회만 와도 정기.
- 유휴 제거: 마지막 요청 후 `max(60s, 5×주기)`. 키 상한 500(LRU).

## 파일 로그
- `<exe 디렉토리>/logs/yyyy-MM-dd.log`, 4xx → WARN, 5xx → ERROR.
- `config.ini` `[Logging] MaxTotalMB=50` (0이면 비활성). 초과 시 오래된 파일부터 삭제(최신 1개 보존).

## IP 허용목록 (런타임 갱신)
- `IpAllowlistOptions`는 immutable 상태를 volatile 참조로 보유, `Update(csv)`로 원자적 교체.
- UI 변경 즉시 `WebServer.UpdateAllowlist(csv)` + `config.ini` 저장. loopback(`::1`)은 `127.0.0.1` 규칙으로 정규화.
- CORS 미설정(브라우저 cross-origin 차단으로 CSRF 방어).

## 코딩 컨벤션
- 한국어 주석, 사용자 표시 문자열은 영문
- 네임스페이스: `TorusGateway`, `TorusGateway.Torus`, `TorusGateway.WebServer`
- TORUS 앱 상수: `Constants` 클래스 (MainWindow.xaml.cs)
- WPF 이벤트 핸들러는 sender 비교로 버튼 분기
- `ExitApplication()`은 `Environment.Exit(0)`으로 종료 (네이티브 DLL 리소스 OS 회수)
- pure 헬퍼는 `internal static`으로 두고 `TorusGateway.Tests`에서 검증
