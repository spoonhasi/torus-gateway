# TORUS Gateway — Custom Endpoints

이 문서는 TORUS Gateway가 자체 처리하는 **커스텀 엔드포인트**를 설명합니다.
TORUS의 기본 pass-through 엔드포인트(`GET/POST /machine/{path}`)는 TORUS 매뉴얼을 참고하세요.

This document describes the **custom endpoints** handled by TORUS Gateway itself.
For the default pass-through endpoints (`GET/POST /machine/{path}`), refer to the TORUS manual.

## 공통 응답 형식 / Common response format

모든 JSON 응답은 최소한 `status` 필드를 포함합니다.
Every JSON response contains at least a `status` field.

- 성공 / Success: `status: 0`
- 앱 자체 오류(파라미터 검증 실패 등) / App-level error (e.g., parameter validation): `{ "status": -1, "error": "<message>" }`, HTTP 400/404/500
- TORUS 호출 실패 / TORUS call failure: `{ "status": <TORUS code>, "error": "<mapped description>" }`, HTTP 502

## 공통 규칙 / Common rules

쿼리 파라미터 **키는 대소문자를 구분하지 않습니다** (예: `machine`, `Machine`, `MACHINE` 모두 동일). 값은 그대로 유지됩니다.
Query parameter **keys are case-insensitive** (e.g. `machine`, `Machine`, `MACHINE` are equivalent). Values are preserved as-is.

---

## `GET /`

단순 alive 체크.
Simple alive check.

**Response:** `{ "status": "ok" }`

---

## `GET /health`

헬스 체크. TORUS 연결 상태와 무관하게 항상 동일한 정적 응답을 반환합니다.
Health check. Always returns the same static response regardless of TORUS connection state.

**Response:** `{ "status": 0, "value": "ok" }`

---

## `GET /docs/help`

이 문서를 그대로 반환합니다 (`text/markdown; charset=utf-8`).
Returns this document as-is (`text/markdown; charset=utf-8`).

---

## `GET /machineList`

현재 TORUS에 연결된 머신 목록 반환. `Api.getMachinesInfo` 래핑.
Returns the list of machines currently connected to TORUS. Wraps `Api.getMachinesInfo`.

**Query parameters:** 없음 / none

**Response example:**
```json
{
  "status": 0,
  "value": [
    {
      "name": "메인제어기",
      "id": 100,
      "vendor": "Kcnc",
      "connectCode": "Default",
      "ip_address": "192.168.86.76",
      "toolSystem": 1
    }
  ]
}
```

**Notes:**
- TORUS 원본의 `vendorCode` 키는 `vendor`로 변환되어 반환됩니다.
  The original `vendorCode` key from TORUS is renamed to `vendor`.
- 단일 머신 연결 시에도 `value`는 배열로 유지됩니다.
  `value` is always an array, even when only one machine is connected.

---

## `GET /machine/channel/programName`

지정한 channel에서 현재 실행 중인 파일의 이름 조회 (경로 제외).
Reads the name (without path) of the file currently running on the specified channel.

내부적으로 `data://machine/channel/currentProgram/currentFile/programName`을 사용합니다.
Internally uses `data://machine/channel/currentProgram/currentFile/programName`.

`mainProgramName`은 메인 프로그램(mainFile), 이 엔드포인트는 현재 실행 파일(currentFile)을 반환합니다.
`mainProgramName` returns the main program (mainFile); this endpoint returns the currently running file (currentFile).

**Query parameters:**
- `machine` (int, required): 머신 ID / machine ID
- `channel` (int, required): 채널 번호 / channel number

**Response example:**
```json
{ "status": 0, "value": "O0002" }
```

경로까지 포함된 값이 필요하면 `GET /machine/channel/programPath`를 사용하세요.
If you need the value including the path, use `GET /machine/channel/programPath`.

---

## `GET /machine/channel/mainProgramName`

지정한 channel의 현재 메인 프로그램 이름 조회 (경로 제외).
Reads the current main program name (without path) for the specified channel.

내부적으로 `data://machine/channel/currentProgram/mainFile/programName`을 사용합니다.
Internally uses `data://machine/channel/currentProgram/mainFile/programName`.

**Query parameters:**
- `machine` (int, required): 머신 ID / machine ID
- `channel` (int, required): 채널 번호 / channel number

**Response example:**
```json
{ "status": 0, "value": "O0001" }
```

경로까지 포함된 값이 필요하면 `GET /machine/channel/mainProgramPath`를 사용하세요.
If you need the value including the path, use `GET /machine/channel/mainProgramPath`.

---

## `GET /machine/channel/programPath`

지정한 channel에서 현재 실행 중인 파일의 경로 조회.
Reads the path of the file currently running on the specified channel.

내부적으로 `data://machine/channel/currentProgram/currentFile/programNameWithPath`를 사용합니다.
Internally uses `data://machine/channel/currentProgram/currentFile/programNameWithPath`.

`mainProgramPath`는 메인 프로그램(mainFile), 이 엔드포인트는 현재 실행 파일(currentFile)을 반환합니다.
`mainProgramPath` returns the main program (mainFile); this endpoint returns the currently running file (currentFile).

**Query parameters:**
- `machine` (int, required): 머신 ID / machine ID
- `channel` (int, required): 채널 번호 / channel number

**Response example:**
```json
{ "status": 0, "value": "//NC/MPF.DIR/O0002" }
```

---

## `GET /machine/channel/mainProgramPath`

지정한 channel의 현재 메인 프로그램 경로 조회.
Reads the current main program path for the specified channel.

내부적으로 `data://machine/channel/currentProgram/mainFile/programNameWithPath`를 사용합니다.
Internally uses `data://machine/channel/currentProgram/mainFile/programNameWithPath`.

**Query parameters:**
- `machine` (int, required): 머신 ID / machine ID
- `channel` (int, required): 채널 번호 / channel number

**Response example:**
```json
{ "status": 0, "value": "//NC/MDI/2000" }
```

---

## `POST /machine/channel/mainProgramPath`

지정한 channel에 메인 프로그램을 설정·실행. `Api.CNCFileExecute` 래핑.
Sets / executes the main program on the specified channel. Wraps `Api.CNCFileExecute`.

**Query parameters:**
- `machine` (int, required)
- `channel` (int, required)

**Body:**
```json
{ "value": "program/O0001.nc" }
```
배열 형식 `{ "value": ["program/O0001.nc"] }`도 허용됩니다.
The array form `{ "value": ["program/O0001.nc"] }` is also accepted.

**Response:** `{ "status": 0 }`

---

## `GET /machine/channel/variableNumber/variableValue`

지정한 channel의 사용자 변수(user variable) 값 조회.
Reads the value of a user variable on the specified channel.

내부적으로 `data://machine/channel/variable/userVariable`를 사용하며, 쿼리의 `variableNumber` 키를 TORUS 필터 키 `variable`로 변환해 전달합니다.
Internally uses `data://machine/channel/variable/userVariable`; the `variableNumber` query key is translated to the TORUS filter key `variable`.

**Query parameters:**
- `machine` (int, required): 머신 ID / machine ID
- `channel` (int, required): 채널 번호 / channel number
- `variableNumber` (int, required): 변수 번호 / variable number

**Response example:**
```json
{ "status": 0, "value": 123.456 }
```

---

## `GET /machine/ncMemoryRootPath`

지정한 머신의 NC 메모리 루트 경로 조회.
Reads the NC memory root path of the specified machine.

내부적으로 `data://machine/ncMemory/rootPath`를 사용합니다.
Internally uses `data://machine/ncMemory/rootPath`.

**Query parameters:**
- `machine` (int, required): 머신 ID / machine ID

경로 끝의 `/`는 제거되어 반환됩니다 (예: `//NC/MPF.DIR/` → `//NC/MPF.DIR`).
Any trailing `/` is stripped from the returned path (e.g. `//NC/MPF.DIR/` → `//NC/MPF.DIR`).

**Response example:**
```json
{ "status": 0, "value": "//NC/MPF.DIR" }
```

---

## `GET /machine/ncMemoryPath/directory`

지정한 NC 메모리 경로 하위의 파일/폴더 목록 조회. `Api.getFileListEx` 래핑.
Lists files and folders under the given NC memory path. Wraps `Api.getFileListEx`.

정렬은 폴더 우선 → 이름 오름차순입니다. 루트 목록을 보려면 먼저 `GET /machine/ncMemoryRootPath`로 루트 경로를 얻으세요.
Sorted folders-first, then by name ascending. To list the root, first obtain the root path via `GET /machine/ncMemoryRootPath`.

**Query parameters:**
- `machine` (int, required): 머신 ID / machine ID
- `ncMemoryPath` (string, required): 조회할 디렉토리 경로 / directory path to list. 예 / e.g.: `program`, `//NC/MPF.DIR`

**Response fields (per item) / 항목별 필드:**
- `name` (string): 파일/폴더 이름 (끝의 `/`는 제거됨) / file or folder name (trailing `/` stripped)
- `sizeBytes` (int): 바이트 단위 크기 / size in bytes
- `modifiedAt` (string): 수정 시각 (TORUS 원본 형식) / last modified time (TORUS raw format)
- `isDir` (bool): 폴더 여부 / whether the entry is a directory

**Response example:**
```json
{
  "status": 0,
  "value": [
    { "name": "subDir", "sizeBytes": 0, "modifiedAt": "2026-05-30 09:12:00", "isDir": true },
    { "name": "O0001.nc", "sizeBytes": 1234, "modifiedAt": "2026-05-29 14:23:00", "isDir": false }
  ]
}
```

---

## `POST /machine/ncMemoryPath/directory`

지정한 경로에 새 폴더를 생성. `Api.CreateCNCFolder` 래핑.
Creates a new folder at the given path. Wraps `Api.CreateCNCFolder`.

`ncMemoryPath`의 마지막 세그먼트가 만들 폴더 이름이고, 나머지가 부모 경로입니다.
The last segment of `ncMemoryPath` is the new folder name; the rest is the parent path.
예 / e.g.: `program/newDir` → 부모 `program`에 `newDir` 생성 / creates `newDir` under `program`.

**Query parameters:**
- `machine` (int, required): 머신 ID / machine ID
- `ncMemoryPath` (string, required): 부모 경로 + 새 폴더 이름 / parent path + new folder name

**Body:**
```json
{ "value": [] }
```
body는 형식(JSON)만 확인하며 내용은 사용하지 않습니다.
The body is only validated as JSON; its content is not used.

**Response:** `{ "status": 0 }`

---

## `GET /machine/ncMemoryPath/content`

NC 메모리상의 텍스트 파일 내용을 JSON `value`로 반환.
Returns the content of a text file in NC memory as the JSON `value`.

**Query parameters:**
- `machine` (int, required)
- `ncMemoryPath` (string, required): NC 파일 경로 / NC file path. 예 / e.g.: `program/O0001.nc`, `//NC/MDI/2000`

**Response example:**
```json
{ "status": 0, "value": "O0001\nG0 X0 Y0\nM30\n" }
```

---

## `POST /machine/ncMemoryPath/content`

JSON `value`의 텍스트를 NC 파일로 업로드.
Uploads the text in JSON `value` as an NC file.

**Query parameters:**
- `machine` (int, required)
- `ncMemoryPath` (string, required)

**Body:**
```json
{ "value": "O0001\nG0 X0 Y0\nM30\n" }
```
배열 형식도 허용. 이중 이스케이프된 `\n`/`\r`/`\t`는 실제 제어 문자로 자동 변환됩니다.
The array form is also accepted. Double-escaped `\n`/`\r`/`\t` are automatically converted to real control characters.

**Response:** `{ "status": 0 }`

---

## `POST /machine/ncMemoryPath/exist`

`{"value": false}`를 보내면 해당 NC 파일을 삭제합니다. `true`는 지원하지 않습니다.
Sending `{"value": false}` deletes the target NC file. `true` is not supported.

**Query parameters:**
- `machine` (int, required)
- `ncMemoryPath` (string, required)

**Body:**
```json
{ "value": false }
```

**Response:** `{ "status": 0 }`

---

## `POST /machine/ncMemoryPath/name`

지정한 NC 파일/폴더의 이름을 변경. `Api.CNCFileRename` 래핑.
Renames the target NC file or folder. Wraps `Api.CNCFileRename`.

`ncMemoryPath`는 대상의 **전체 경로**, body의 `value`는 **새 이름**(경로가 아님)입니다.
`ncMemoryPath` is the **full path** of the target; the body `value` is the **new name** (not a path).

**Query parameters:**
- `machine` (int, required): 머신 ID / machine ID
- `ncMemoryPath` (string, required): 대상의 전체 경로 / full path of the target. 예 / e.g.: `program/O0001.nc`

**Body:**
```json
{ "value": "O0002.nc" }
```
배열 형식 `{ "value": ["O0002.nc"] }`도 허용됩니다. `value`에는 `/`, `\`, `..`를 쓸 수 없습니다(단일 이름만 허용).
The array form `{ "value": ["O0002.nc"] }` is also accepted. `value` may not contain `/`, `\`, or `..` (a single name only).

**Response:** `{ "status": 0 }`
