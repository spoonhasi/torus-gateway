# TORUS Gateway

## 프로젝트 개요
TORUS(통합 CNC 인터페이스 플랫폼)를 HTTP REST API 또는 OPC UA로 노출하는 WPF 데스크탑 게이트웨이.
두 논문에서 소개한 프로그램의 구현이다.
- HTTP: "The Development of REST API Gateway Application Using Unified CNC Interface Platform"
  (IJPEM-ST, https://doi.org/10.57062/ijpem-st.2024.00171)
- OPC UA: "OPC UA-Based Machining Cell Monitoring System for Multi-Vendors' Machine Tools and Industrial Robots"
  (IJPEM-ST 1(1) 63–69, 2023, https://doi.org/10.57062/ijpem-st.2022.0024). 원래 별도의 비공개 저장소
  `torus_opcua_server`(v1.0.1)였던 것을 v2.1.0에서 최신 공식 스택으로 옮겨 통합했다.

**유지보수 종료.** v2.0.0에서 HTTP를 논문 설계(그림 4·5·6)대로 정리했고, v2.1.0에서 OPC UA 서버를 통합했다.
v1.2.3~v1.7.1에 있던 과도기 기능(커스텀 엔드포인트, 응답 필터링, body 정규화, NC 탐색기, 문서 창,
콘솔 임베딩)은 v2.0.0에서 제거했다. 마지막 운영 버전은 태그 `v1.7.1`.

## 기술 스택
- .NET 8.0 (Windows), WPF + ASP.NET Core 내장 웹서버, C#
- **x86 필수** (TORUS 네이티브 DLL이 32비트)
- `TorusApi/`: TORUS SDK v2.3.3의 DLL 중 실제 의존하는 14개만 보관
  (IntelligentApiCS, ItemCS는 관리 어셈블리 참조, 나머지는 그 의존 네이티브 DLL)
- OPC UA: NuGet `OPCFoundation.NetStandard.Opc.Ua.Server` 1.5.378.x (MIT). Core가
  `Microsoft.Extensions.Logging` 10.x를 끌어오지만 ASP.NET Core 8 웹서버와 함께 동작함을 확인.
  1.5.378 API: `ApplicationInstance(config, ITelemetryContext)`, `CheckApplicationInstanceCertificatesAsync`,
  `StartAsync/StopAsync`, `OnMonitoredItemCreated(..., ISampledDataChangeMonitoredItem)`, `DecryptedPassword`는 byte[].
  WPF 프로젝트라 암시적 using에 `System.IO`가 없다(파일 상단에 명시).
- 테스트: xUnit (`TorusGateway.Tests/`)

## 프로젝트 구조
```
TorusGateway.sln
TorusGateway/
  App.xaml(.cs)              # 단일 인스턴스 Mutex, 수동 MainWindow 생성, 종료 시 임시 디렉토리 정리
  MainWindow.xaml(.cs)       # Torus 연결, 프로토콜 선택(HTTP/OPC UA), 서버 open/close, Test Request, IP 허용목록(런타임 갱신), 로그, 트레이/종료
  TestRequestWindow.xaml(.cs)# HTTP 테스터 (GET/PUT/DELETE, JSON body, 모달)
  ConfigManager.cs           # config.ini (Win32 INI) 읽기/쓰기 헬퍼 (static). 값 버퍼 32767자(예전 255자는 DPAPI 비밀번호·긴 IP 목록을 잘랐음)
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
  OpcUa/
    OpcUaHost.cs             # ApplicationConfiguration 생성, 인증서(pki/) 준비, 서버 시작/종료
    TorusOpcUaServer.cs      # StandardServer. 서버 정보, 사용자 인증(익명 또는 사용자 이름 중 하나만)
    TorusNodeManager.cs      # CustomNodeManager2. 동적 노드 생성, OnRead/OnWrite, 구독 노드 폴링 스레드
    OpcUaValues.cs           # 순수 변환(internal static): NodeId↔TORUS 주소, Item→값, 값→updateData body
    OpcUaSettings.cs         # 설정 검증(Validate), config.ini [OpcUa] 저장, 비밀번호 DPAPI 암호화
TorusGateway.Tests/
  BuildAddressAndFilterTests.cs
  RequestValidationTests.cs  # ValidateUpdateBody, FileController.FileNameOf
  OpcUaValuesTests.cs        # OPC UA 변환·설정 검증
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
- `config.ini`, `logs/`, `pki/`(OPC UA 인증서)는 실행 파일 폴더(`bin/...`)에 생긴다. **`bin`을 통째로 지우면 사용자 설정과 인증서가 사라진다.**
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

## OPC UA (논문 2 그림 2·3, 기존 torus_opcua_server 이식)
- NodeId 문자열 식별자 = HTTP 경로+쿼리와 같은 문자열: `ns=2;s=/machine/channel/axis/machinePosition?machine=1&channel=1&axis=1`.
  논문은 `data://`를 붙였지만 이후 뺐다(사용자 결정, 2026-10-22). `data://` 형식은 받지 않는다(같은 데이터에 노드가 둘 생기므로 별칭 없음).
  필터(`?` 뒤)는 필수 — 경로만 있는 식별자는 같은 경로의 폴더 NodeId와 겹친다.
- 네임스페이스 URI `urn:TorusGateway:TORUS`가 ns=2. 주소 공간은 `Objects/machine`(NodeId `/machine`)만 두고 시작,
  `GetManagerHandle`에서 TORUS getData가 성공하면 경로 폴더 + 필터 이름 변수를 만들고, 실패하면 null(BadNodeIdUnknown).
- 타입: string/long/double/boolean → String/Int64/Double/Boolean. list* 타입은 항상 배열, 그 외는 원소 2개 이상일 때만 배열.
- 쓰기: `TypeInfo.IsInstanceOfDataType`로 노드 타입과 같은 값만, `BuildUpdateBody`로 항상 `{"value":[...]}`(JSON 직렬화,
  문화권 무관) 생성, 빈 배열·NaN·다른 형은 TORUS로 보내지 않음(BadTypeMismatch). IndexRange 부분 쓰기 미지원.
  OnRead/OnWrite는 SDK가 노드 관리자 `Lock` 안에서 부른다 — 그래서 기존 서버의 쓰기 후 100ms 대기는 뺐다(모든 세션을 막았음).
  쓰기 성공 후에는 그 노드의 마지막 값을 지워 다음 폴링이 TORUS의 실제 값을 다시 알리게 한다.
- 구독: `OnMonitoredItemCreated/Deleted`로 Value 속성 MonitoredItem의 Id→노드를 추적(다른 속성 구독은 무시), 폴링 스레드가 `[OpcUa] PollIntervalMs`(기본 100)마다
  구독 노드만 TORUS에서 다시 읽어 값/상태가 바뀔 때만 `ClearChangeMasks`. 구독 중인 노드의 OnRead는 캐시 값 반환.
  폴링은 노드마다 try/catch(백그라운드 스레드 예외는 프로세스를 죽이므로).
- 잠금 순서: 노드 관리자 `Lock` 안에서 TORUS를 부를 수 있지만(GetManagerHandle, OnRead), 폴링 스레드는 TORUS 호출을 끝낸 뒤에만
  `Lock`을 잡는다 — `_apiLock`을 쥔 채 `Lock`을 기다리는 경로가 없어 교착 없음.
- 인증: 익명 모드면 익명만, 사용자 이름 모드면 일치하는 계정만(고정 시간 비교). **익명은 읽기 전용** —
  OnWrite에서 BadUserAccessDenied, `OnReadUserAccessLevel`로 UserAccessLevel=CurrentRead를 알림(기본 설정이 익명+None이라
  막지 않으면 네트워크의 누구나 CNC 값을 바꿀 수 있음, 2026-10-22 사용자 결정). 쓰기는 Username 모드에서만. 채널 보안 None일 때 UserName 토큰 정책에
  Basic256Sha256을 지정해 비밀번호를 암호화.
- 인증서: `pki/{own,trusted,issuer,rejected}` (실행 파일 폴더 기준). 자동 수락 기본 꺼짐. rejected/certs → trusted/certs로 옮기면
  재시작 없이 허용됨(확인). 기존 서버의 `e.Accept = true`(무조건 수락)와 상대 경로 저장소는 고쳤다.
  서버 인증서는 처음 시작한 엔드포인트 주소·PC 이름으로 만들어져, 이후 주소(0.0.0.0 → 특정 IP 등)나 PC 이름이 바뀌면
  `CheckApplicationInstanceCertificatesAsync`가 ServiceResultException을 던진다 → `pki/own`을 지우고 새로 만든 뒤 CERT 로그.
  같은 주소로 재시작하면 인증서는 유지된다(확인). 새로 만들면 서버 인증서를 신뢰해 둔 클라이언트는 다시 신뢰해야 한다.
- 보안 정책: None, Basic256Sha256, Aes128_Sha256_RsaOaep, Aes256_Sha256_RsaPss (Basic128Rsa15/Basic256 제외).

## 동작 흐름
1. **Torus Connect** → `Torus.Connect()` (`Api.Initialize`). 실패 시 "Reconnect" → 앱 재시작(`--auto-connect`).
   TORUS API에 Uninitialize가 없어 재연결은 프로세스 재시작으로만 가능.
2. **Protocol** 선택(HTTP / OPC UA, `config.ini [MainWindow] Protocol=HTTP|OPCUA`) → **Server Open**.
   한 번에 하나만 실행한다(두 서버가 TORUS 호출 잠금을 공유하고 OPC UA 구독이 TORUS를 계속 폴링하므로).
   HTTP: ASP.NET Core (기본 `0.0.0.0:5001`). OPC UA: `OpcUaHost` (기본 `opc.tcp://0.0.0.0:4840`).
3. HTTP 요청 → 미들웨어(로그, IP 허용목록) → 컨트롤러 → `Torus` 싱글턴 → 네이티브 API.
   OPC UA 요청 → `TorusNodeManager` → `Torus` 싱글턴. OPC UA 이벤트(NODE/UNKNOWN/WRITE/AUTH)는 메인 창 아래 로그 패널에 표시.

## 단일 인스턴스
- Named Mutex(`TorusGateway_SingleInstance`)로 중복 실행 방지. `--auto-connect` 재시작 시에는 건너뜀.

## 모달 창 가드
- `MainWindow._modalDepth`로 모달(`MachineListWindow`/`TestRequestWindow`) 열림을 추적.
  열려 있는 동안 종료/트레이 이동/최소화를 차단 (작업표시줄 입력 대비).

## Test Request 창
- HTTP 전용. OPC UA 프로토콜을 고르면 버튼이 비활성화된다.
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
