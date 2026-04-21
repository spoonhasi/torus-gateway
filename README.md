# TORUS Gateway
---
TORUS의 REST API 게이트웨이를 제공하는 데스크톱 소프트웨어입니다. 32비트로만 동작하므로 반드시 x86으로 빌드하십시오.

## 빌드 / Build
---
```bash
dotnet build TorusGateway.sln -c Release
```
- `<PlatformTarget>x86</PlatformTarget>` 필수 (TORUS 네이티브 DLL이 32비트).
- PreBuild: `TorusApi/*.dll`을 출력 디렉토리로 복사.
- PostBuild: `TorusGateway.info`를 출력 디렉토리로 복사.
- `$(SolutionDir)`가 없는 외부 빌드(예: `dotnet test`)를 대비해 csproj에 폴백 설정 있음.

## 테스트 / Tests
---
```bash
dotnet test TorusGateway.Tests/TorusGateway.Tests.csproj
```
- xUnit 기반. 순수 헬퍼 함수(`FilterStatusAndValue`, `WrapValueToArray`, `FilterStatusOnly`, `RenameValueArrayKey`, `NormalizeValueKey`, `TorusStatusCodes.GetDescription`)를 커버.
- 테스트 접근을 위해 `TorusGateway.csproj`에 `<InternalsVisibleTo Include="TorusGateway.Tests" />` 설정.

## Sourcecode
---
- TORUS에 접속할 때는 TORUS 라이브러리(`TorusApi/*.dll`)를 사용합니다. 래퍼 클래스는 `Torus/Torus.cs`의 싱글턴.
- MachineList.xml 편집 UI는 `Torus/MachineListControl.xaml`. `MachineListWindow`는 이를 감싼 윈도우(다른 프로젝트 재사용 목적).
- 웹 프레임워크의 기반은 ASP.NET Core (`WebServer/WebServer.cs`).
- `WebServer/TorusController.cs`는 TORUS의 `api.getData` / `api.updateData`를 그대로 노출하는 **pass-through** 컨트롤러 (`GET/POST /machine/{*}`).
  - GET 응답은 `status + value`만 남기고, 단일 원소 배열은 해제 (단, `datatype`에 `"list"` 포함 시 유지).
  - POST 응답은 `status`만 남김.
  - 두 경우 모두 `status != 0`이면 `error` 키에 매핑된 설명 자동 삽입.
  - 공용 JSON 옵션(`JsonSerOptions`, `JsonWriterOpts`)은 `UnsafeRelaxedJsonEscaping`을 적용해 한글/특수문자 이스케이프 방지.
  - POST body는 `NormalizeValueKey`로 `Value`/`VALUE` 등 대소문자 변형을 소문자 `value`로 정규화(TORUS는 소문자만 인식).
- `WebServer/CustomController.cs`는 이 앱이 중간에 가공하는 **합성 엔드포인트** 모음.
  - `/machineList` (GET): `Api.getMachinesInfo` 래핑 + `vendorCode→vendor` 리네임.
  - `/machine/channel/mainProgramPath` (GET/POST): GET은 `currentProgram/mainFile/programNameWithPath`로 리라이트, POST는 `Api.CNCFileExecute` 래핑.
  - `/machine/ncMemoryPath/content` (GET/POST): NC 텍스트 파일을 JSON `value`로 주고받음.
  - `/machine/ncMemoryPath/exist` (POST): `{"value":false}`로 NC 파일 삭제.
  - `/docs/help` (GET): `WebServer/CustomEndpoints.md` 임베디드 리소스 원문 반환 (`text/markdown; charset=utf-8`).
  - 임시 디렉토리 상수(`TempDirDownload`/`TempDirUpload`) 및 `CleanupOrphanedTempDirs()` 제공.
- `WebServer/TorusStatusCodes.cs`는 TORUS의 `NC_RET` 에러 코드를 영문(일부 한글 원문) 설명으로 매핑.
- `WebServer/CustomEndpoints.md`는 커스텀 엔드포인트 안내 문서(한/영 병행). 임베디드 리소스로 빌드 시 DLL에 포함되어 `/docs/help` 및 `DocsWindow` 양쪽에서 동일 소스 사용.
- `DocsWindow.xaml(.cs)`는 메인 창의 `Docs` 버튼으로 열리며 위 마크다운 원문을 읽기 전용 TextBox로 표시.
- `TestRequestWindow.xaml(.cs)`는 메인 창의 `Test Request` 버튼으로 열리는 초기 셋팅 확인용 HTTP 테스터(GET/POST, JSON body, 응답 표시). Base URL은 현재 Host/Port로 자동 채움(`0.0.0.0`은 `127.0.0.1`로 대체).
- `MainWindow`는 모달 창(`MachineListWindow`/`DocsWindow`/`TestRequestWindow`)이 열려 있는 동안 `_modalDepth` 카운터로 트레이 이동/최소화/종료를 차단.

## 프로젝트 구조
---
```
TorusGateway.sln
TorusGateway/
  App.xaml(.cs)              # WPF 앱 엔트리 (단일 인스턴스 Mutex, 수동 MainWindow 생성, 종료 시 임시 디렉토리 정리)
  MainWindow.xaml(.cs)       # 메인 UI: Torus 연결, 웹서버 시작/종료, Docs, Test Request, IP 허용목록, 트레이/종료. 모달 중 트레이/최소화 차단
  DocsWindow.xaml(.cs)       # 커스텀 엔드포인트 문서 뷰어 (임베디드 CustomEndpoints.md 표시, 모달)
  TestRequestWindow.xaml(.cs)# 초기 셋팅용 HTTP 테스터 (GET/POST, JSON body, 모달)
  TorusGateway.csproj
  Torus/
    Torus.cs                 # TORUS API 래퍼 (싱글턴). GetData/UpdateData/UploadFile/DownloadFile/DeleteFile/ExecuteCNCFile/GetMachinesInfo. _apiLock으로 호출 직렬화
    MachineListControl.xaml(.cs)  # MachineList.xml 편집 UserControl (재사용 가능 - 외부 의존 없음)
    MachineListWindow.xaml(.cs)   # 위 컨트롤을 감싼 모달 창
  WebServer/
    WebServer.cs             # ASP.NET Core 호스트, 시작/종료
    TorusController.cs       # pass-through: GET/POST /machine/{*}
    CustomController.cs      # 합성 엔드포인트 + 임시 디렉토리 관리
    CustomEndpoints.md       # 커스텀 엔드포인트 문서 (EmbeddedResource, 한/영 병행)
    TorusStatusCodes.cs      # NC_RET enum → 설명 매핑
    RequestLoggingMiddleware.cs
    IpAllowlistMiddleware.cs
TorusGateway.Tests/
  TorusGateway.Tests.csproj  # xUnit 테스트 프로젝트 (net8.0-windows, x86)
  FilterStatusAndValueTests.cs
  WrapValueToArrayTests.cs
  FilterStatusOnlyTests.cs
  RenameValueArrayKeyTests.cs
  NormalizeValueKeyTests.cs
  TorusStatusCodesTests.cs
TorusApi/                    # TORUS 네이티브 DLL (빌드 시 출력 디렉토리로 복사)
TorusGateway.info            # 앱 메타데이터 (이름, 버전, GUID)
```

## Updates
---
v1.3.2
- POST body 대소문자 정규화: `Value`/`VALUE` 등 루트 키 변형을 소문자 `value`로 자동 변환(TORUS는 소문자만 인식). 모든 POST 엔드포인트에 적용.
- 신규 UI: `Test Request` 버튼 → `TestRequestWindow`로 GET/POST 간이 테스터 제공. Base URL/Path/JSON body/응답 표시. POST 선택 시 `{"value": }` 템플릿 자동 삽입 + 힌트.
- UX: `DocsWindow`도 `TORUS Settings`와 동일하게 모달. 모달이 열려 있는 동안 메인 창의 트레이 이동/최소화/종료를 차단(`_modalDepth` 카운터).
- 테스트: `NormalizeValueKeyTests` 8건 추가 (총 38건).

v1.3.1
- 신규 엔드포인트: `GET /machineList` — `Api.getMachinesInfo` 래핑. `vendorCode→vendor` 키 리네임, 단일 머신 연결 시에도 `value` 배열 유지. 원본에 `status`가 없으므로 `status:0` 주입.
- 신규 엔드포인트: `GET /docs/help` — 임베디드 마크다운(`CustomEndpoints.md`) 원문 반환(`text/markdown; charset=utf-8`).
- 신규 UI: `Docs` 버튼 → `DocsWindow`로 같은 문서 뷰어 제공. 마크다운 소스는 엔드포인트와 공유.
- 응답 필터: `FilterStatusAndValue`에 `unwrapSingletonArray` 옵션 매개변수 추가 (`/machineList`는 항상 배열 유지).
- 엔드포인트 정리: `GET/PUT/DELETE /file/machine/ncpath` 제거(같은 기능을 `/machine/ncMemoryPath/*`가 대체). 모든 PUT 제거, POST만 사용.
- UI 개선: TORUS Settings 창 열 때 MachineList 경로가 실제 파일이면 자동 Read. Sort by ID 버튼을 Remove Selected 오른쪽 열로 이동, Move Up / Move Down 버튼 추가. 창 높이 +50px (600→650).
- UI: VendorCode 콤보박스 항목 전부 대문자(FANUC, SIEMENS, CSCAM, MITSUBISHI, KCNC, MAZAK, HEIDENHAIN).
- 에러 코드 매핑 확장: `TorusStatusCodes`에 공구/파일/타임시리즈 관련 40+개 항목 추가 (일부는 한글 원문 유지).
- 테스트: xUnit 프로젝트 `TorusGateway.Tests` 신설. 30건 커버.

v1.3.0
- 구조: MachineController → TorusController 리네임, FileController는 CustomController로 통합 (URL은 그대로)
- 응답 필터: GET `/machine/{*}`는 status + value만 남기고, 단일 원소 배열은 해제 (datatype에 "list" 포함 시 예외)
- 응답 필터: PUT/POST `/machine/{*}`는 status만 남김
- 에러 매핑: TORUS status != 0이면 `error` 키에 매핑된 설명 자동 삽입 (WebServer/TorusStatusCodes.cs)
- 커스텀 엔드포인트 실패 응답: 실제 TORUS status 코드와 설명을 포함한 502 반환
- 신규: `GET/POST /machine/channel/mainProgramPath` (CNCFileExecute 기반)
- JSON 직렬화에 UnsafeRelaxedJsonEscaping 적용하여 한글/기호 이스케이프 제거

v1.2.5
- NC 파일 내용을 JSON value로 읽기/쓰기하는 엔드포인트 (`GET/PUT/POST /machine/ncMemoryPath/content`)
- NC 파일 삭제를 `{"value": false}`로 수행 (`PUT/POST /machine/ncMemoryPath/exist`)
- 모든 PUT 엔드포인트에 POST도 동일 지원 (클라이언트 편의)
- NC 경로의 `Path.IsPathRooted` 체크 제거 (`//NC/2000` 같은 절대 NC 경로 허용)
- NC 경로에서 파일명 추출 시 `/`, `\` 모두 지원
- 업로드 시 리터럴 `\n` → 실제 줄바꿈 자동 변환
- JSON 직렬화 시 `+` 등의 불필요한 유니코드 이스케이프 제거 (`\u002B` → `+`)

v1.2.3
- PUT 요청 body의 "value" 키가 단일 값이면 자동으로 배열로 감싸서 TORUS에 전달 (`{"value": 42}` → `{"value": [42]}`)

v1.2.2
- 보안 강화: 클라이언트 IP 허용목록 (Allow All / Local Only / Specify IPs)
- 보안 강화: CORS AllowAnyOrigin 제거 (브라우저 cross-origin 호출 차단으로 CSRF 방어)
- 안정성: TORUS 네이티브 API 호출 직렬화 (스레드 안전성 보장)
- 안정성: 임시 디렉토리 이름에 GUID 부여 (동시 요청 충돌 방지)
- 안정성: NC 파일 다운로드 스트리밍 응답 (32비트 OOM 위험 제거)
- 안정성: 앱 시작 시 이전 세션의 잔여 임시 디렉토리 자동 정리
- UI: WebServer Open/Close 버튼 한 줄 정렬, TORUS Settings 버튼 폭 통일
- UI: TORUS Settings 창 레이아웃 정리 (경로/편집 영역 Border 분리, 파일 액션 툴바 분리)
- UI: 종료 확인 모달 추가
- 전체 사용자 표시 문자열 영문화 (메뉴/메시지/오류 응답)

v1.2.1
- 서버 alive 체크 엔드포인트 추가 (`GET /` → `{"status":"ok"}`)
- 앱 아이콘 적용 (작업표시줄/창 제목표시줄/시스템 트레이)

v1.2
- TORUS 연결 실패 시 Reconnect 버튼으로 앱 자동 재시작하여 재연결
- 앱 종료 시 Environment.Exit(0)으로 네이티브 DLL 리소스 확실한 해제
- 단일 인스턴스 실행 제한 (중복 실행 시 트레이/작업표시줄 상태 안내)
- TORUS 설정 창 ComboBox/TextBox 레이아웃 통일

v1.1
- MachineList 작성 기능의 대폭 개선

v1.0
- 테스트 완료
