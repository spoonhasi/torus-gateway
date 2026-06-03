# TORUS Gateway

## 프로젝트 개요
TORUS 플랫폼의 REST API 게이트웨이. TORUS(산업용 CNC 장비 관리 플랫폼)에 연결하여 외부 클라이언트가 HTTP로 장비 데이터를 조회/수정/파일 전송할 수 있게 해주는 WPF 데스크탑 애플리케이션.

## 기술 스택
- **프레임워크**: .NET 8.0 (Windows), WPF + ASP.NET Core 내장 웹서버
- **언어**: C#
- **빌드**: Visual Studio 2022, MSBuild (커맨드라인 `dotnet build` 가능)
- **타겟 플랫폼**: x86 (32비트 필수 - TORUS 네이티브 DLL 의존)
- **외부 DLL**: `TorusApi/` 폴더의 네이티브 DLL들 (IntelligentApiCS.dll, ItemCS.dll 등)
- **테스트**: xUnit (`TorusGateway.Tests/`)

## 프로젝트 구조
```
TorusGateway.sln
TorusGateway/                # 메인 WPF + 내장 웹서버
  App.xaml(.cs)              # 단일 인스턴스 Mutex, 수동 MainWindow 생성, 종료 시 임시 디렉토리 정리
  MainWindow.xaml(.cs)       # 메인 UI - Torus 연결, 웹서버 open/close, Docs, IP 허용목록(런타임 갱신), 트레이/종료
  DocsWindow.xaml(.cs)       # 커스텀 엔드포인트 문서 뷰어 (EmbeddedResource CustomEndpoints.md 표시, 모달)
  TestRequestWindow.xaml(.cs)# 초기 셋팅용 HTTP 테스터 (GET/POST, JSON body, 모달)
  NcExplorerWindow.xaml(.cs) # NC 파일 탐색기 (모달). 전부 게이트웨이 자신의 HTTP 엔드포인트로 동작
                             # 설비 선택→루트/목록 조회, 폴더 진입("..."), 다운로드/업로드(텍스트, 바이너리 가드)/이름변경/삭제/새폴더
                             # 웹서버 가동 중일 때만 활성 (MainWindow에서 버튼 제어)
  ConfigManager.cs           # config.ini (Win32 INI) 읽기/쓰기 헬퍼 (static)
  TorusGateway.csproj        # <InternalsVisibleTo Include="TorusGateway.Tests">, SolutionDir 폴백 포함
  Torus/
    Torus.cs                 # TORUS API 싱글턴 래퍼. _apiLock으로 모든 네이티브 호출 직렬화
                             # 메서드: GetData, UpdateData, UploadFile, DownloadFile, DeleteFile,
                             #         ExecuteCNCFile, CreateFolder, RenameFile, GetFileList, GetMachinesInfo
    MachineListControl.xaml(.cs)  # MachineList.xml 편집 UserControl (재사용 가능, 외부 의존 없음)
    MachineListWindow.xaml(.cs)   # 위 컨트롤을 감싼 모달 창 (재사용 래퍼)
  WebServer/
    WebServer.cs             # ASP.NET Core 호스트 + 미들웨어 inline 정의(IpAllowlistMiddleware, RequestLoggingMiddleware,
                             # IpAllowlistOptions). UpdateAllowlist(csv)로 런타임 IP 목록 교체. CORS 미설정(CSRF 방어)
    TorusController.cs       # 단순 pass-through: GET/POST /machine/{*segments}
                             # 응답 필터 헬퍼(static): FilterStatusAndValue, FilterStatusOnly, WrapValueToArray
                             # body 정규화 헬퍼(static): NormalizeValueKey — Value/VALUE → value
                             # 공용 JSON 옵션: JsonSerOptions, JsonWriterOpts (UnsafeRelaxedJsonEscaping)
                             # JsonException 분리: body 파싱 실패 400, TORUS 응답 파싱 실패 502
    CustomController.cs      # 합성 엔드포인트 모음 + TempDirDownload/Upload 상수 + CleanupOrphanedTempDirs
                             # 엔드포인트 목록/사양은 CustomEndpoints.md 참조 (예: /machineList, /docs/help,
                             #   /machine/channel/program*·mainProgram*, /machine/channel/variableNumber/variableValue,
                             #   /machine/ncMemoryRootPath, /machine/ncMemoryPath/{directory,content,exist,name})
                             # 헬퍼: BuildFilter, RenameValueArrayKey, TrimTrailingSlashInValue, ErrorResponse, TorusErrorResponse
                             # JsonException 분리: body 파싱 실패 400, TORUS 응답 파싱 실패 502
    CustomEndpoints.md       # 커스텀 엔드포인트 안내 문서 (EmbeddedResource, 한/영 병행)
    TorusStatusCodes.cs      # TORUS NC_RET enum 값 → 설명 문자열 매핑
    FileLogger.cs            # 일자별 파일 로그(static). logs/yyyy-MM-dd.log, ERROR/WARN만, 총 사이즈 캡
TorusGateway.Tests/          # xUnit 테스트 프로젝트 (net8.0-windows, x86)
  TorusGateway.Tests.csproj
  FilterStatusAndValueTests.cs   # 10건
  WrapValueToArrayTests.cs       # 6건
  FilterStatusOnlyTests.cs       # 3건
  RenameValueArrayKeyTests.cs    # 7건
  NormalizeValueKeyTests.cs      # 8건
  TorusStatusCodesTests.cs       # 4건
  IpAllowlistOptionsTests.cs     # 12건 (Star/CIDR/IPv6 loopback 정규화/Update 시나리오)
  ConfigManagerTests.cs          # 5건 (Write→Read, 기본값 기록, 섹션 격리)
  TryDecodeNcTextTests.cs        # 8건 (업로드 바이너리 판정 — NULL바이트/UTF-8 유효성/BOM)
TorusApi/                    # TORUS 네이티브 DLL (빌드시 출력 디렉토리로 복사)
TorusGateway.info            # 앱 메타데이터 (이름, 버전, GUID)
```

## 빌드 / 테스트
```bash
# 빌드
dotnet build TorusGateway.sln -c Release

# 테스트
dotnet test TorusGateway.Tests/TorusGateway.Tests.csproj
```
- **반드시 x86**으로 빌드 (csproj `<PlatformTarget>x86</PlatformTarget>`)
- PreBuild: `TorusApi/*.dll` → 출력 디렉토리 복사 (`$(SolutionDir)` 폴백으로 솔루션 외부 빌드도 동작)
- PostBuild: `TorusGateway.info` → 출력 디렉토리 복사
- 테스트 프로젝트는 `InternalsVisibleTo`로 internal 헬퍼에 접근

## 엔드포인트 요약

### Pass-through (TorusController) — TORUS 매뉴얼 참조
- `GET /` → `{"status":"ok"}` (alive 체크)
- `GET /machine/{*segments}?{filter}` → `Torus.GetData(address, filter)` 결과에 필터 적용
  - 응답: status + value만, 단일 원소 배열 해제 (datatype에 "list" 포함 시 유지)
- `POST /machine/{*segments}?{filter}` + JSON body → `Torus.UpdateData(...)`
  - body 루트 키 대소문자 정규화 (`NormalizeValueKey`): `Value`/`VALUE` → `value` (TORUS는 소문자만 인식)
  - body `"value"` 키가 단일 값이면 자동 배열 래핑 (`WrapValueToArray`)
  - 응답: status만

### Custom (CustomController)
- 합성 엔드포인트(`/health`, `/docs/help`, `/machineList`, `/machine/channel/mainProgramName`,
  `/machine/channel/mainProgramPath`, `/machine/ncMemoryPath/*` 등)의 상세 사양은
  `WebServer/CustomEndpoints.md`를 참조 (임베디드 리소스, `/docs/help` 및 Docs 창에 노출)

## 응답 공통 형식
- 성공: `{"status":0, "value": ...}` 또는 `{"status":0}`
- 앱 자체 오류: `{"status":-1, "error":"<message>"}`, HTTP 400/404/500
- 요청 body JSON 파싱 실패: HTTP 400 (`"Invalid JSON in request body"`)
- TORUS 응답 JSON 파싱 실패: HTTP 502 (`"Invalid JSON response from TORUS"`)
- TORUS 호출 실패: `{"status":<TORUS 코드>, "error":"<매핑된 설명>"}`, HTTP 502
- 매핑은 `TorusStatusCodes.cs`에서 정의 (0x0, 0x21B*****, 0x20206028 등)

## API 주소 체계
- TORUS 내부 주소: `data://machine/{경로}?{필터}` (예: `data://machine/channel/axis/machinePosition?machine=1&channel=1&axis=1`)
- 기본 REST URL이 TORUS 내부 주소로 1:1 매핑됨 (pass-through)
- 커스텀 엔드포인트는 TorusController의 와일드카드(`{*segments}`)보다 우선 (ASP.NET 라우트 특정성)

## 동작 흐름
1. **Torus Connect** 버튼 → `Torus.Initialize()` + `Torus.Connect()` (네이티브 API 초기화)
   - 연결 실패 시 버튼이 "Reconnect"로 변경 → 클릭 시 앱 자동 재시작(`--auto-connect`)하여 재연결
   - TORUS 네이티브 API는 Uninitialize가 없으므로 재연결 시 프로세스 재시작 필수
2. **WebServer Open** 버튼 → ASP.NET Core 웹서버 시작 (기본 `0.0.0.0:5001`)
3. 외부 클라이언트가 REST API로 접근 → MiddleWare → Controller → `Torus` 싱글턴 → 네이티브 API

## 단일 인스턴스
- Named Mutex(`TorusGateway_SingleInstance`)로 중복 실행 방지
- 중복 실행 시 기존 프로세스의 `MainWindowHandle`로 트레이/작업표시줄 상태를 판별하여 안내 메시지 표시
- `--auto-connect` 인자로 재시작 시에는 Mutex 체크를 건너뜀

## 모달 창 가드
- `MainWindow._modalDepth` 카운터로 모달 창(`MachineListWindow`/`DocsWindow`/`TestRequestWindow`)의 열림 상태 추적
- 모달 열려있는 동안 `MainWindow_Closing`은 종료/트레이 이동을 차단, `MainWindow_StateChanged`는 최소화를 `Normal`로 복귀
- 작업표시줄 클릭 등으로 owner가 최소화·종료되는 것을 방지 (WPF의 ShowDialog가 owner를 disable하긴 하지만 작업표시줄 입력은 막지 못함)

## Test Request 창
- 초기 셋팅 확인용 모달 HTTP 테스터 (`TestRequestWindow`)
- Base URL은 메인 창의 Host/Port에서 채움 (`0.0.0.0`/`*`/빈 값은 `127.0.0.1`로 대체)
- Method 드롭다운 (GET/POST), Path, Body(POST만), 응답(상태코드 + 본문) 표시
- POST 선택 시 라벨에 `{"value": 25.5}` 형태 힌트, body가 비어있으면 템플릿 자동 삽입
- 10초 타임아웃, `Content-Type: application/json` 고정

## NC 파일 탐색기 창
- NC 파일 탐색기 모달 (`NcExplorerWindow`). **모든 기능이 게이트웨이 자신의 HTTP 엔드포인트(커스텀 엔드포인트)로 동작** — Torus 싱글턴 직접 호출 안 함. 따라서 웹서버 가동 중일 때만 열 수 있다 (MainWindow에서 버튼 제어).
- 접속 대상은 메인 창 Host/Port (와일드카드/빈 값은 `127.0.0.1`로 대체). loopback은 IP 허용목록과 무관하게 항상 허용되어 자기 요청이 차단되지 않음.
- 설비 콤보박스 선택 → `GET /machine/ncMemoryRootPath`로 루트 조회 → `GET /machine/ncMemoryPath/directory`로 목록. 폴더 더블클릭 진입, 첫 항목 `...`으로 상위 복귀(방문 스택 기반, 경로 포맷 비의존).
- 기능별 엔드포인트: Download `GET .../content`, Upload `POST .../content`, New Folder `POST .../directory`, Rename `POST .../name`, Delete `POST .../exist {"value":false}`. 작업 후 목록을 갱신하고 대상 항목을 선택/스크롤.
- 업로드는 텍스트(`{"value":"<내용>"}`) 기반이라 바이너리 파일을 막는다: 로컬 파일을 읽기 전 NULL 바이트/엄격 UTF-8 디코딩으로 판정(`TryDecodeNcText`, `internal static`로 테스트됨), 바이너리면 경고 후 취소.
- Rename: `InputBox`에 현재 이름을 기본값으로 채움. 새 이름은 단일 이름만 허용(`/`·`\` 거부), 파일/폴더 모두 대상.

## MachineList.xml 관리
- TORUS Settings 버튼으로 `MachineListWindow` 모달 열기. 경로가 실제 파일이면 자동 Read.
- NCmachine 속성: activate, name, id, vendorCode, address, port, exDllPath, connectCode, ncVersionCode, toolSystem, username, password
- 지원 vendorCode: FANUC, SIEMENS, CSCAM, MITSUBISHI, KCNC, MAZAK, HEIDENHAIN (대문자)
- Sort by ID / Move Up / Move Down 버튼으로 목록 순서 조작
- 설정 파일 경로는 `config.ini`에 저장. INI 읽기/쓰기는 `ConfigManager` static 클래스를 통해 일원화 (P/Invoke로 Win32 INI API 호출)

## 동시성 / 스레드
- `Torus._apiLock`으로 모든 네이티브 API 호출 직렬화 (DLL 스레드 안전성 불명확)
- 한 호출이 블로킹되면 모든 후속 호출 대기 (타임아웃 메커니즘 없음 - 잠재적 개선 포인트)

## 임시 디렉토리
- `TempDirDownload` (ncTempFileForDownloading), `TempDirUpload` (ncTempFileForUploading)
- 요청마다 `yyyyMMddHHmmssfff_<GUID8>` 하위 디렉토리 생성
- 정상 완료 시 `OnCompleted` 콜백 또는 `finally`에서 정리
- 크래시 후 남은 잔여물은 앱 시작 시 `CustomController.CleanupOrphanedTempDirs()`가 처리
- 정리 실패는 `FileLogger.WriteWarn`으로 기록 (이전엔 silent catch였음)

## API 요청 로그 (UI)
- `RequestLoggingMiddleware`가 요청마다 구조화 정보(`RequestLogInfo`: 메서드·IP·경로+쿼리·상태·소요·시각)를 `RequestLogged` 이벤트로 발행 → `MainWindow`가 200ms 타이머로 배치 반영. favicon 제외, 각 줄에 IP 표시.
- UI는 **GET 정기 / GET 간헐 / POST** 세 패널(동일 높이, 사이를 `GridSplitter`로 조절). 패널당 1000줄 캡, 맨 아래일 때만 자동 스크롤.
- GET 주기성 집계(`ProcessGet`, UI 스레드): 키=`IP + 경로+쿼리`(입력 그대로, 대소문자 구분). 도착 간격 EMA의 변동계수(편차/평균)가 작고(≤`RegularMaxCv`) 충분히 반복(≥`RegularMinCount`)되며 주기가 빠르면(≤`RegularMaxPeriodMs`) **정기**로 승격 → 표(IP·Request·Count·Period·Last·Status)를 in-place 갱신, 아니면 간헐 스크롤 목록.
- `/health`는 조회 데이터와 무관한 정적 응답이라 **1회만 와도 무조건 정기**로 표시(`IsHealthPath`, 대소문자 무관). 도착 간격이 아직 없으면 Period는 `-`.
- 유휴 제거: 마지막 요청 후 `max(60s, 5×주기)` 경과 시 집계·표행 삭제(현재 살아있는 폴러만 유지). 키 상한 500(LRU).
- POST 및 GET 외 메서드는 POST 패널의 스크롤 목록으로.

## 파일 로그
- `FileLogger` (static, `WebServer/FileLogger.cs`) — 4xx/5xx 응답만 파일에 기록 (UI 로그 패널엔 모두 표시 — 위 'API 요청 로그' 참고)
- 위치: `<exe 디렉토리>/logs/yyyy-MM-dd.log`, 포맷: `2026-05-22 14:23:45.123 [WARN ] 192.168.1.10 GET /... 404 12ms`
- 사이즈 캡: 시작 시 + 일자 전환 시 점검, 총 합이 캡 초과면 가장 오래된 파일부터 삭제(최신 1개는 항상 보존)
- 설정: `config.ini` `[Logging] MaxTotalMB=50` (기본 50MB, 0이면 비활성화)
- 분류: HTTP 응답 코드 4xx → WARN, 5xx → ERROR. 미들웨어와 임시 디렉토리 cleanup 실패 catch에서 호출

## IP 허용목록 (런타임 갱신)
- `IpAllowlistOptions`는 내부 상태(immutable record)를 volatile 참조로 보유 — `Update(csv)`로 원자적 교체
- UI에서 라디오 변경/IP 추가/제거 시 즉시 `WebServer.UpdateAllowlist(csv)` 호출 + `config.ini` 저장
- 서버 실행 중에도 IP 컨트롤은 활성 상태 유지 (Host/Port만 readonly)

## 코딩 컨벤션
- 한국어 주석 사용, 사용자 표시 문자열은 영문
- 네임스페이스: `TorusGateway`, `TorusGateway.Torus`, `TorusGateway.WebServer`
- TORUS 앱 상수: `Constants` 클래스 (MainWindow.xaml.cs)
- `Torus` 클래스는 싱글턴 패턴 (Initialize → Instance)
- WPF 이벤트 핸들러에서 sender 비교로 버튼 분기
- `ExitApplication()`에서 `Environment.Exit(0)`으로 프로세스 확실한 종료 (네이티브 DLL 리소스 OS 회수)
- `RestartApplication()`으로 설정 저장 → 웹서버 종료 → 새 프로세스 시작 → 현재 프로세스 종료
- pure 헬퍼 함수는 `internal static`으로 선언하여 `TorusGateway.Tests`에서 검증
