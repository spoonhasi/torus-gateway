# TORUS Gateway

## 프로젝트 개요
TORUS 플랫폼의 REST API 게이트웨이. TORUS(산업용 CNC 장비 관리 플랫폼)에 연결하여 외부 클라이언트가 HTTP로 장비 데이터를 조회/수정/파일 전송할 수 있게 해주는 WPF 데스크탑 애플리케이션.

## 기술 스택
- **프레임워크**: .NET 8.0 (Windows), WPF + ASP.NET Core 내장 웹서버
- **언어**: C#
- **빌드**: Visual Studio 2022, MSBuild
- **타겟 플랫폼**: x86 (32비트 필수 - TORUS 네이티브 DLL 의존)
- **외부 DLL**: `TorusApi/` 폴더의 네이티브 DLL들 (IntelligentApiCS.dll, ItemCS.dll 등)

## 프로젝트 구조
```
TorusGateway.sln
TorusGateway/
  App.xaml(.cs)              # WPF 앱 엔트리 (단일 인스턴스 Mutex, 수동 MainWindow 생성)
  MainWindow.xaml(.cs)       # 메인 UI - Torus 연결, 웹서버 시작/종료, 재시작
  AssemblyInfo.cs
  TorusGateway.csproj
  Torus/
    Torus.cs                 # TORUS API 래퍼 (싱글턴, getData/updateData/파일 전송)
    MachineListControl.xaml(.cs)  # MachineList.xml 편집 UI 컨트롤
    MachineListWindow.xaml(.cs)   # MachineList 편집 모달 창
  WebServer/
    WebServer.cs             # ASP.NET Core 호스트 (Startup, CORS 설정)
    MachineController.cs     # GET/PUT /machine/{path} - 장비 데이터 조회/수정
    FileController.cs        # GET/PUT/DELETE /file/machine/ncpath - NC 파일 전송/삭제
TorusApi/                    # TORUS 네이티브 DLL들 (빌드시 출력 디렉토리로 복사됨)
TorusGateway.info            # 앱 메타데이터 (이름, 버전, GUID)
```

## 빌드
```bash
dotnet build TorusGateway.sln -c Release
# 또는 Visual Studio에서 x86 / Release로 빌드
```
- 반드시 **x86**으로 빌드해야 함 (csproj에 `<PlatformTarget>x86</PlatformTarget>` 설정됨)
- PreBuild: `TorusApi/*.dll` → 출력 디렉토리 복사
- PostBuild: `TorusGateway.info` → 출력 디렉토리 복사

## 핵심 동작 흐름
1. **Torus Connect** 버튼 → `Torus.Initialize()` + `Torus.Connect()` (네이티브 API 초기화)
   - 연결 실패 시 버튼이 "Reconnect"로 변경 → 클릭 시 앱 자동 재시작(`--auto-connect`)하여 재연결
   - TORUS 네이티브 API는 Uninitialize가 없으므로 재연결 시 프로세스 재시작 필수
2. **WebServer Open** 버튼 → ASP.NET Core 웹서버 시작 (기본 `0.0.0.0:5001`)
3. 외부 클라이언트가 REST API로 장비 데이터 접근:
   - `GET /machine/{path}?params` → `Torus.GetData(address, filter)`
   - `PUT /machine/{path}?params` + JSON body → `Torus.UpdateData(address, filter, value)`
   - `GET /file/machine/ncpath?machine=N&ncpath=...` → NC 파일 다운로드
   - `PUT /file/machine/ncpath?machine=N&ncpath=...` + multipart file → NC 파일 업로드
   - `DELETE /file/machine/ncpath?machine=N&ncpath=...` → NC 파일 삭제

## API 주소 체계
- TORUS 내부 주소: `data://machine/{경로}?{필터}` (예: `data://machine/channel/axis/machinePosition?machine=1&channel=1&axis=1`)
- REST URL이 TORUS 내부 주소로 1:1 매핑됨

## 단일 인스턴스
- Named Mutex(`TorusGateway_SingleInstance`)로 중복 실행 방지
- 중복 실행 시 기존 프로세스의 `MainWindowHandle`로 트레이/작업표시줄 상태를 판별하여 안내 메시지 표시
- `--auto-connect` 인자로 재시작 시에는 Mutex 체크를 건너뜀

## MachineList.xml 관리
- TORUS Settings 버튼으로 MachineList.xml 편집 모달 창 열기
- NCmachine 속성: activate, name, id, vendorCode, address, port, exDllPath, connectCode, ncVersionCode, toolSystem, username, password
- 지원 벤더: Fanuc, Siemens, CSCAM, Mitsubishi, Kcnc, Mazak, Heidenhain
- 설정 파일 경로는 `config.ini`에 저장 (Win32 INI API 사용)

## 코딩 컨벤션
- 한국어 주석 사용
- 네임스페이스: `TorusGateway`, `TorusGateway.Torus`, `TorusGateway.WebServer`
- TORUS 앱 상수: `Constants` 클래스 (MainWindow.xaml.cs)
- `Torus` 클래스는 싱글턴 패턴 (Initialize → Instance)
- WPF 이벤트 핸들러에서 sender 비교로 버튼 분기
- `ExitApplication()`에서 `Environment.Exit(0)`으로 프로세스 확실한 종료 (네이티브 DLL 리소스 OS 회수)
- `RestartApplication()`으로 설정 저장 → 웹서버 종료 → 새 프로세스 시작 → 현재 프로세스 종료
