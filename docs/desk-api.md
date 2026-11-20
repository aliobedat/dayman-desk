# Dayman Desk API contract (v1, Ali 19 Nov 2026)

Both WO-84 (server) and WO-85 (Windows program) implement exactly this. Base URL: `https://daymanjo.com/api/desk`
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
409 `INVOICE_USED` (a purchase not undone already carries this invoice number: the cashier entered it).
422 `OUT_OF_RANGE` (amount), 400 `BAD_INPUT`.

## POST /heartbeat (every 5 minutes)
Request `{ "appVersion": "0.1.0", "printKind": "text"|"image"|"unknown", "queueLength": 0, "lastForwardMs": 42 }`
200 `{ "serverTime": "<ISO>", "qrEnabled": true, "codeValidityDays": 2 }`

## POST /report
Request `{ "level": "info"|"warn"|"error", "kind": "parse_failed"|"unknown_format"|"slow_forward"|"crash"|"sample"|"other", "message": "≤2000 chars, never a phone or name", "appVersion": "0.1.0", "sample": "<base64, ≤64 KB, optional>" }`
204. The server keeps `sample` only when the shop's «ساعد في تحسين دايماً ديسك» switch is on (default ON during
the pilot), and deletes samples after 30 days.

## Program behaviour bound to this contract
- The QR (claimUrl) is printed only after `/receipt` answered 200. Offline or any error → the receipt prints
  without a QR, the receipt is queued and sent later (no QR then), and a `report` is sent.
- `qrEnabled: false` (owner switched it off) → no QR, receipts still sent.
