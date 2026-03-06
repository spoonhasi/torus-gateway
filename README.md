# TORUS Gateway
---
TORUS에 REST API 인터페이스를 부여하는 게이트웨이 소프트웨어입니다. 32비트만을 지원하므로 반드시 x86으로 빌드하십시오.

## Sourcecode
---
- TORUS에 연결할때는 TORUS 라이브러리를 사용합니다.
- TORUS를 사용할수있는 래퍼클래스(Torus.cs)를 만들어서 사용합니다.
- TORUS의 연결 설비 설정을 손쉽게 할수있도록 machineList.xml 편집 기능이 있습니다. (Torus/MachineListControl.xaml)
- TORUS의 연결 설비 설정 기능은 TorusTester를 참고하여 개발되었습니다.
- 본 게이트웨이의 서버는 ASP.net입니다. WebServer/WebServer.cs에 관련 소스가 있습니다.
- WebServer/MachineController.cs의 GetTorusGetData 함수는 Get을 TORUS의 api.getData 함수로 변환하여 획득한 결과를 그대로 반환합니다.
- WebServer/MachineController.cs의 PutTorusUpdateData 함수는 Put을 TORUS의 api.updateData 함수로 변환하여 획득한 결과를 그대로 반환합니다.
- WebServer/FileController.cs의 함수들은 파일 업로드/다운로드/삭제 함수입니다. TORUS에서 해당 기능은 MachineDataModel이 구현되어 있지 않으므로 별도의 address와 filter를 사용하였습니다.

## Updates
---
v1.1
- MachineList 작성 기능의 버그 수정

v1.0
- 테스트 완료