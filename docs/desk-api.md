# Dayman Desk API contract (v1.1, Ali 20 Nov 2026; v1 19 Nov 2026)

Both the server (WO-84, WO-86 items 8-12) and the Windows program (WO-85, WO-86 items 1-7) implement exactly this.
This file is the single source; v1.1 changes are marked **v1.1** and are backward compatible (a v1 program ignores
them, a v1.1 program works with a v1 server). Base URL: `https://daymanjo.com/api/desk`
(configurable in the program). JSON, UTF-8. Every error body: `{ "error": "<CODE>", "message": "<Arabic text>" }`.
Every call except `pair` sends `Authorization: Bearer <deviceToken>`; a missing, wrong or revoked token → 401
`UNAUTHORIZED` / `REVOKED`. Rate limits (Postgres counters) → 429 `RATE_LIMITED` with `retryAfterSeconds`.

## POST /pair
Request `{ "code": "AB12CD", "deviceName": "كاشير 1", "appVersion": "0.1.0" }`
200 `{ "token": "<64 hex>", "shopName": "جمرة", "stampMode": "spend"|"visit", "codeValidityDays": 2, "qrEnabled": true }`
404 `CODE_NOT_FOUND`, 410 `CODE_EXPIRED` (pairing codes live 10 minutes, single use).

## POST /receipt
Request `{ "clientId": "<uuid v4>", "invoiceNo": "10432", "amount": "12.500", "printedAt": "2026-11-19T18:02:11+03:00", "kind": "text"|"image"|"unknown" }`
- `invoiceNo`: 1-40 safe characters (the WO-62 rule). `amount`: JOD as a string, up to 3 decimals, > 0, within
  the WO-61 delivery-code amount bounds. `clientId` makes the call idempotent.
200 `{ "code": "K7Q2M9XP", "claimUrl": "https://daymanjo.com/c/<shopToken>/s/K7Q2M9XP", "expiresAt": "<ISO>", "already": false }`
- The same invoice again → the same code with `"already": true`.
- **WO-88** `printedAt` more than 5 minutes ahead of the server's clock or more than 8 days behind it (past the
  7-day offline queue) is replaced by the server's time (a wrong PC clock); the invoice's day is then today in Amman.
  A queued receipt within the window keeps its own print day.
409 `INVOICE_USED` (a purchase not undone already carries this invoice number: the cashier entered it).
409 `INVOICE_VOIDED` (**WO-88**: the owner voided this invoice's code with «إلغاء الكود»; the same invoice on the same
Amman day never gets a code again). No QR, not queued, no report: the receipt is done.
422 `OUT_OF_RANGE` (amount), 400 `BAD_INPUT`.

## POST /heartbeat (every 5 minutes)
Request `{ "appVersion": "1.0.0", "printKind": "text"|"image"|"unknown", "queueLength": 0, "lastForwardMs": 42 }`
200 `{ "serverTime": "<ISO>", "qrEnabled": true, "codeValidityDays": 2, "latestVersion": "1.0.0", "downloadUrl": "https://..." }`
- **v1.1** `latestVersion` (optional, env `DESK_LATEST_VERSION`): the newest released program version, semantic
  `MAJOR.MINOR.PATCH`. `downloadUrl` (optional, env `DESK_DOWNLOAD_URL`): an absolute https link to that version's
  `DaymanDesk.exe`. Each is omitted when its env is empty; the program shows an update only when both are present. The link must open without a login on a shop PC (the
  GitHub repository is private, so its release links do not qualify).
- **v1.1** program: if `latestVersion` is newer than its own version and `downloadUrl` is https, the window and the
  tray show «يوجد تحديث» with the link. No silent self-update in 1.0. A missing, older, equal or malformed value, or
  a non-https link, shows nothing.

## POST /report
Request `{ "level": "info"|"warn"|"error", "kind": "parse_failed"|"unknown_format"|"slow_forward"|"crash"|"sample"|"other", "message": "≤2000 chars, never a phone or name", "appVersion": "0.1.0", "sample": "<base64, ≤64 KB, optional>" }`
204. The server keeps `sample` only when the shop's «ساعد في تحسين دايماً ديسك» switch is on (default ON during
the pilot), and deletes samples after 30 days.

## Not in v1.1
`POST /receipt-image` (AI reading of image receipts) was drafted for WO-86 item 3 and postponed by Ali (20 Nov
2026). Since WO-89 (program 1.1, no contract change) the program reads image receipts itself with the OCR built
into Windows and registers them through the unchanged `/receipt` with `kind: "image"`, under the same rules as text
receipts (confirmation, both fields ≥ 0.75); an image it cannot read gets no `/receipt` call, no QR, and a `report`.

## Program behaviour bound to this contract
- The QR (claimUrl) is printed only after `/receipt` answered 200. Offline or any error → the receipt prints
  without a QR, the receipt is queued and sent later (no QR then), and a `report` is sent.
- `qrEnabled: false` (owner switched it off) → no QR, receipts still sent.
- **v1.1** `/receipt` is called only after the owner confirmed the two fields on the first text receipt after pairing
  («تأكيد الحقول»), and only when both the invoice number and the total reach confidence ≥ 0.75. The confirmation
  receipt itself is not registered.
- **v1.1** the device token is stored sealed with Windows DPAPI (LocalMachine scope), never in plain text.
