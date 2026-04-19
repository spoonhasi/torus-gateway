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
  MainWindow.xaml(.cs)       # 메인 UI - Torus 연결, 웹서버 open/close, Docs, IP 허용목록, 트레이/종료
  DocsWindow.xaml(.cs)       # 커스텀 엔드포인트 문서 뷰어 (EmbeddedResource CustomEndpoints.md 표시)
  TorusGateway.csproj        # <InternalsVisibleTo Include="TorusGateway.Tests">, SolutionDir 폴백 포함
  Torus/
    Torus.cs                 # TORUS API 싱글턴 래퍼. _apiLock으로 모든 네이티브 호출 직렬화
                             # 메서드: GetData, UpdateData, UploadFile, DownloadFile, DeleteFile,
                             #         ExecuteCNCFile, GetMachinesInfo
    MachineListControl.xaml(.cs)  # MachineList.xml 편집 UserControl (재사용 가능, 외부 의존 없음)
    MachineListWindow.xaml(.cs)   # 위 컨트롤을 감싼 모달 창 (재사용 래퍼)
  WebServer/
    WebServer.cs             # ASP.NET Core 호스트 (Startup/Run/Stop). CORS 미설정(CSRF 방어)
    TorusController.cs       # 단순 pass-through: GET/POST /machine/{*segments}
                             # 응답 필터 헬퍼(static): FilterStatusAndValue, FilterStatusOnly, WrapValueToArray
                             # 공용 JSON 옵션: JsonSerOptions, JsonWriterOpts (UnsafeRelaxedJsonEscaping)
    CustomController.cs      # 합성 엔드포인트 모음 + TempDirDownload/Upload 상수 + CleanupOrphanedTempDirs
                             # /machineList (GET), /docs/help (GET),
                             # /machine/channel/mainProgramPath (GET/POST),
                             # /machine/ncMemoryPath/content (GET/POST), /machine/ncMemoryPath/exist (POST)
                             # 헬퍼: RenameValueArrayKey, ErrorResponse, TorusErrorResponse
    CustomEndpoints.md       # 커스텀 엔드포인트 안내 문서 (EmbeddedResource, 한/영 병행)
    TorusStatusCodes.cs      # TORUS NC_RET enum 값 → 설명 문자열 매핑
    RequestLoggingMiddleware.cs  # 요청 로깅 (UI ListBox에만 표시 - 파일 로그 없음)
    IpAllowlistMiddleware.cs     # 클라이언트 IP 허용목록 검사
TorusGateway.Tests/          # xUnit 테스트 프로젝트 (net8.0-windows, x86)
  TorusGateway.Tests.csproj
  FilterStatusAndValueTests.cs   # 10건
  WrapValueToArrayTests.cs       # 6건
  FilterStatusOnlyTests.cs       # 3건
  RenameValueArrayKeyTests.cs    # 7건
  TorusStatusCodesTests.cs       # 4건
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
  - body `"value"` 키가 단일 값이면 자동 배열 래핑 (`WrapValueToArray`)
  - 응답: status만

### Custom (CustomController) — CustomEndpoints.md 참조
- `GET /docs/help` - 임베디드 CustomEndpoints.md 원문 반환 (text/markdown)
- `GET /machineList` - 연결된 머신 목록 (`Api.getMachinesInfo` 래핑, vendorCode→vendor, 단일 머신도 배열 유지)
- `GET /machine/channel/mainProgramPath?machine&channel` - 내부적으로 `data://machine/channel/currentProgram/mainFile/programNameWithPath`로 리라이트
- `POST /machine/channel/mainProgramPath?machine&channel` + `{"value":"path"}` → `Api.CNCFileExecute`
- `GET /machine/ncMemoryPath/content?machine&ncMemoryPath` - NC 텍스트 파일 내용을 JSON value로 반환
- `POST /machine/ncMemoryPath/content?machine&ncMemoryPath` + `{"value":"text"}` - 업로드
- `POST /machine/ncMemoryPath/exist?machine&ncMemoryPath` + `{"value":false}` - NC 파일 삭제

## 응답 공통 형식
- 성공: `{"status":0, "value": ...}` 또는 `{"status":0}`
- 앱 자체 오류: `{"status":-1, "error":"<message>"}`, HTTP 400/404/500
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

## MachineList.xml 관리
- TORUS Settings 버튼으로 `MachineListWindow` 모달 열기. 경로가 실제 파일이면 자동 Read.
- NCmachine 속성: activate, name, id, vendorCode, address, port, exDllPath, connectCode, ncVersionCode, toolSystem, username, password
- 지원 vendorCode: FANUC, SIEMENS, CSCAM, MITSUBISHI, KCNC, MAZAK, HEIDENHAIN (대문자)
- Sort by ID / Move Up / Move Down 버튼으로 목록 순서 조작
- 설정 파일 경로는 `config.ini`에 저장 (Win32 INI API 사용)

## 동시성 / 스레드
- `Torus._apiLock`으로 모든 네이티브 API 호출 직렬화 (DLL 스레드 안전성 불명확)
- 한 호출이 블로킹되면 모든 후속 호출 대기 (타임아웃 메커니즘 없음 - 잠재적 개선 포인트)

## 임시 디렉토리
- `TempDirDownload` (ncTempFileForDownloading), `TempDirUpload` (ncTempFileForUploading)
- 요청마다 `yyyyMMddHHmmssfff_<GUID8>` 하위 디렉토리 생성
- 정상 완료 시 `OnCompleted` 콜백 또는 `finally`에서 정리
- 크래시 후 남은 잔여물은 앱 시작 시 `CustomController.CleanupOrphanedTempDirs()`가 처리

## 코딩 컨벤션
- 한국어 주석 사용, 사용자 표시 문자열은 영문
- 네임스페이스: `TorusGateway`, `TorusGateway.Torus`, `TorusGateway.WebServer`
- TORUS 앱 상수: `Constants` 클래스 (MainWindow.xaml.cs)
- `Torus` 클래스는 싱글턴 패턴 (Initialize → Instance)
- WPF 이벤트 핸들러에서 sender 비교로 버튼 분기
- `ExitApplication()`에서 `Environment.Exit(0)`으로 프로세스 확실한 종료 (네이티브 DLL 리소스 OS 회수)
- `RestartApplication()`으로 설정 저장 → 웹서버 종료 → 새 프로세스 시작 → 현재 프로세스 종료
- pure 헬퍼 함수는 `internal static`으로 선언하여 `TorusGateway.Tests`에서 검증
