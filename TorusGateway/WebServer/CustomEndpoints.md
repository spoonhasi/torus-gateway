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

---

## `GET /`

단순 alive 체크.
Simple alive check.

**Response:** `{ "status": "ok" }`

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
