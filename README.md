# TORUS Gateway
---
TORUS의 REST API 게이트웨이를 제공하는 데스크톱 소프트웨어입니다. 32비트로만 동작하므로 반드시 x86으로 빌드하십시오.

## Sourcecode
---
- TORUS에 접속할때는 TORUS 라이브러리를 사용합니다.
- TORUS에 접속할수있는 래퍼클래스(Torus.cs)를 별도 사용합니다.
- TORUS의 장비 연결 설정을 손쉽게 할수있도록 machineList.xml 편집 기능이 있습니다. (Torus/MachineListControl.xaml)
- TORUS의 장비 연결 설정 기능은 TorusTester를 참고하여 구현되었습니다.
- 웹 프레임워크의 기반은 ASP.net입니다. WebServer/WebServer.cs에 관련 소스가 있습니다.
- WebServer/MachineController.cs의 GetTorusGetData 함수는 Get을 TORUS의 api.getData 함수로 변환하여 획득한 결과를 그대로 반환합니다.
- WebServer/MachineController.cs의 PutTorusUpdateData 함수는 Put을 TORUS의 api.updateData 함수로 변환하여 획득한 결과를 그대로 반환합니다.
- WebServer/FileController.cs의 함수들은 파일 업로드/다운로드/삭제 함수입니다. TORUS에서 해당 기능은 MachineDataModel에 정의되어 있지 않으므로 별도의 address와 filter를 사용하였습니다.

## Updates
---
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
