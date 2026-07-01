# TODO

## TORUS 매니저 콘솔 임베딩 (보류 — 다듬어야 함)

### 현재 상태
TORUS 매니저 콘솔을 메인 창 탭에 임베드하는 기능을 구현했으나(코드는 `ConsoleEmbedder.cs`),
캡처가 일부 매니저에서 안정적이지 않아 **휴면 상태로 두고 Run/Exit는 예전처럼 bat 외부 실행으로 되돌림**.

- `TorusGateway/Torus/ConsoleEmbedder.cs` — 임베딩 로직 전체 (보존됨)
- `TorusGateway/MainWindow.xaml(.cs)` — 탭 UI(`MainTabs`)/콘솔 호스트/이벤트 핸들러 (보존됨)
- `TorusGateway.Tests/ConsoleEmbedderParseTests.cs` — bat 파서 테스트 (유효)

### 현재 동작 (되돌린 상태)
- `MachineListControl` Run/Exit 버튼 → `RunProcess(...)`로 bat만 외부 실행 (임베드 안 함)
- `MainWindow_Loaded`의 자동 재부착은 early-`return`으로 비활성화
- 탭 바에는 "API Log" 탭만 표시됨

### 미해결 문제
1. **캡처 불안정** — 3개 매니저 중 `mgrCommand`가 종종 캡처 실패.
   - 원인 추정: 우리가 띄운 conhost(`host.Id`)가 콘솔 창을 직접 소유하지 않고 재호스팅되어
     소유 conhost PID가 달라짐. 이름(자식 프로세스) 기준 매칭(`FindExistingManagerConsoles(..., null)`)으로
     바꿔봤으나 실제 환경에서 여전히 실패 확인(2026-07-01). 더 파야 함.
   - 확인 방향: 실패 시 `mgrCommand`의 실제 프로세스 트리/콘솔 소유 conhost를 로깅해서
     자식 매칭 가정이 맞는지 검증. 필요하면 손자 프로세스까지 트리 탐색.
2. **리페인트** — conhost는 WM_PAINT 무효화엔 반응 안 하고 WM_SIZE에만 다시 그림.
   현재 `ForceRedraw`가 1px 리사이즈 넛지로 우회 중(동작 OK). 유지.

### 재활성화 시 할 일
- `MachineListControl.xaml.cs`: Run/Exit 버튼을 `TorusRunRequested`/`TorusExitRequested` 이벤트
  발행으로 되돌리기 (현재는 `RunProcess` 직접 호출).
- `MainWindow.xaml.cs`: `MainWindow_Loaded`의 early-`return`/`#pragma` 제거해 자동 재부착 복원.
- 위 캡처 불안정(1번) 근본 해결이 선행되어야 함.
- `CLAUDE.md`에 기능 문서화(현재 미반영).
