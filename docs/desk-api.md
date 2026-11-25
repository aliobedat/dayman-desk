# Dayman Desk API contract (v1.4 WO-97, Ali 26 Nov 2026; v1.3 WO-94, Ali 25 Nov 2026; v1.2 WO-90 and v1.1, Ali 20 Nov 2026; v1 19 Nov 2026)

Both the server (WO-84, WO-86 items 8-12) and the Windows program (WO-85, WO-86 items 1-7) implement exactly this.
This file is the single source; v1.1 changes are marked **v1.1**, v1.2 changes **v1.2**, v1.3 changes **v1.3**, v1.4 changes **v1.4**. All are backward compatible
(an older program ignores them and keeps working unchanged; a newer program works with an older server). Base URL: `https://daymanjo.com/api/desk`
(configurable in the program). JSON, UTF-8. Every error body: `{ "error": "<CODE>", "message": "<Arabic text>" }`.
Every call except `pair` sends `Authorization: Bearer <deviceToken>`; a missing, wrong or revoked token → 401
`UNAUTHORIZED` / `REVOKED`. Rate limits (Postgres counters) → 429 `RATE_LIMITED` with `retryAfterSeconds`.

## POST /pair
Request `{ "code": "AB12CD", "deviceName": "كاشير 1", "appVersion": "0.1.0", "publicKey": "<base64 SPKI>" }`
200 `{ "token": "<64 hex>", "shopName": "جمرة", "stampMode": "spend"|"visit", "codeValidityDays": 2, "qrEnabled": true, "deviceNumber": 1 }`
404 `CODE_NOT_FOUND`, 410 `CODE_EXPIRED` (pairing codes live 10 minutes, single use).
- **v1.3** `publicKey` (optional): the device's signing key, as in `/key`; a malformed one → 400 `BAD_INPUT`.
  `deviceNumber`: this device's number in the shop (1-32767, never reused), carried by its signed QR.

## POST /key (**v1.3**, WO-94)
Request `{ "publicKey": "<base64 of the DER SubjectPublicKeyInfo of an ECDSA P-256 key>" }`
200 `{ "deviceNumber": 1 }`
- The device generates its key pair once per pairing and keeps the private key sealed (DPAPI, like the token).
  The key is set once: the same key again → 200 (a lost answer); another key → 409 `KEY_SET`. A new key needs a new
  pairing (the owner adds the device again). Any other key type or curve → 400 `BAD_INPUT`. 20 calls per device per hour.

## Signed QR (**v1.3**, WO-94)
A device with a key may print, instead of a pool or `/receipt` code, a QR it signs itself, so the customer's scan awards
at once even while the PC is offline. QR content: `claimBase` with `/s/` replaced by `/r/`, plus the token:
`https://daymanjo.com/c/<shopToken>/r/<token>`, token = base64url(payload) + `.` + base64url(signature), no padding.
- payload (big-endian, 13 bytes + invoice): version `1` (1 byte), deviceNumber (2), seq (4: the device's own counter,
  unique per device, never reused), amount in fils (4: 1000 to 10,000,000), print day (2: days since 1970-01-01 of the
  print date in Asia/Amman), then the invoice number as UTF-8 to the end (1-40 characters of the `/receipt` rule, already
  normalised: Latin digits, single spaces, trimmed).
- signature: ECDSA P-256 with SHA-256 over the payload bytes, IEEE P1363 (r‖s, 64 bytes). With an invoice of up to 8
  characters the URL stays under 180 characters.
- The server: the device by its number in the shop of the link, its key, the signature; a stopped device's QR reads «هذا
  الرمز لا يعمل»; a print day after today (Amman) is refused; the QR works through the print day plus `codeValidityDays`. The claim then follows every rule
  of a `/receipt` code (one award, the cashier's entry of the invoice first wins, the daily cap). A second scan of the
  same QR (device + seq) gives nothing more.
- Send `/receipt` for it as usual with `"seq": <its seq>` and no `code`: the answer names the receipt's one code
  (`already: true` when the customer scanned first); no second code is ever made. `seq` with `code` → 400 `BAD_INPUT`.
  A `/receipt` without `seq` for the same invoice and print day also finds it.

## POST /codes (**v1.2**, WO-90)
Request `{ "count": 50 }` (`count` optional, 1-100, default 50).
200 `{ "codes": ["K7Q2M9XP2T", ...], "claimBase": "https://daymanjo.com/c/<shopToken>/s/" }`
- Fresh random codes reserved for this device (the pool). A pool code has no invoice and no amount and is not
  claimable: a customer who scans one before it is bound gets nothing yet and nothing is consumed (**v1.3**: the code
  is reserved for his card and redeemed at its binding, see `/receipt`; v1.2 read «فاتورتك قيد التسجيل، جرّب بعد دقيقة.»). Pool codes count in no cap and no statistic (not «فاتورة غير مُستلمة») and are voided when the owner stops
  the device. One never bound within 30 days of its refill expires (a claim reads the usual expired message, `/receipt`
  answers `CODE_REFUSED`). A device holds at most 300 live unbound codes: past that `codes` is shorter or `[]`.
- `claimBase` + code is the code's claim URL (the same as `/receipt`'s `claimUrl`), so the program prints offline.
- Rate limit: 60 calls per device per hour. 400 `BAD_INPUT`.

## POST /receipt
Request `{ "clientId": "<uuid v4>", "invoiceNo": "10432", "amount": "12.500", "printedAt": "2026-11-19T18:02:11+03:00", "kind": "text"|"image"|"unknown", "code": "K7Q2M9XP2T" }`
- `invoiceNo`: 1-40 safe characters (the WO-62 rule). `amount`: JOD as a string, up to 3 decimals, > 0, within
  the WO-61 delivery-code amount bounds. `clientId` makes the call idempotent.
200 `{ "code": "K7Q2M9XP", "claimUrl": "https://daymanjo.com/c/<shopToken>/s/K7Q2M9XP", "expiresAt": "<ISO>", "already": false }`
- The same invoice again → the same code with `"already": true`.
- **WO-88** `printedAt` more than 5 minutes ahead of the server's clock or more than 8 days behind it (past the
  7-day offline queue) is replaced by the server's time (a wrong PC clock); the invoice's day is then today in Amman.
  A queued receipt within the window keeps its own print day.
- **v1.2** `code` (optional): a pool code of this device the program already printed. The server binds it to this
  receipt under every rule of this section (it becomes the receipt's code; `expiresAt` counts from the binding) and
  answers it with `already: false`. The same receipt again answers it with `already: true`. If the invoice (number and
  print day) already has another code, the answer is that code with `already: true` and the offered code is voided
  (accepted edge case: two tills printed one invoice; the slip of the second is dead). A refusal below (`BAD_INPUT`,
  `OUT_OF_RANGE`, `INVOICE_USED`, `INVOICE_VOIDED`) voids the offered code too, so its scan reads «هذا الرمز لا يعمل».
  Without `code` (a v1.1 program) nothing changes.
- **v1.3** a pool code a customer scanned before its binding is reserved for his card («تم حفظ نقاط فاتورتك. رح تنضاف
  لبطاقتك تلقائياً.»); the binding redeems it for him at once (another card's scan is refused). Accepted risk: a
  reservation is lost when the binding is refused (as above), the invoice already has another code, or the device is revoked. `seq` (optional, 0 to 4294967295): see «Signed QR».
409 `INVOICE_USED` (a purchase not undone already carries this invoice number: the cashier entered it).
409 `INVOICE_VOIDED` (**WO-88**: the owner voided this invoice's code with «إلغاء الكود»; the same invoice on the same
Amman day never gets a code again). No QR, not queued, no report: the receipt is done.
409 `CODE_REFUSED` (**v1.2**: `code` is not an unbound, unvoided, unexpired pool code of this device; nothing is registered).
422 `OUT_OF_RANGE` (amount), 400 `BAD_INPUT`.

## POST /fields (**v1.4**, WO-97)
Request `{ "sampleId": "<uuid>", "lines": ["...", ...], "invoiceLine": 3, "totalLine": 9, "problem": "<Arabic, optional>" }`
204. The first receipt after pairing (or after «إعادة تأكيد الحقول»), for the owner to confirm on his dashboard
(settings → دايماً ديسك, «أول فاتورة بانتظار تأكيدك»). `lines`: the decoded lines (text or OCR), the first 60, each
≤ 80 characters, Latin digits, Jordanian mobile numbers (07[789] / 7[789] / (+|00)?9627[789] + 7 digits, spaces or dashes
allowed, never inside a longer digit run) replaced by «[رقم مخفي]» on the device (long invoice numbers stay visible);
the server scrubs again and stores them on the device row (never readable by the admin panel), cleared at confirmation
and after 7 days. `invoiceLine` / `totalLine`: the detected lines, -1 when none. A new sample replaces the old one.
`problem`: why the owner's last choice was refused (`ConfirmFields`' Arabic reason), shown to him above the lines.
20 calls per device per hour. 400 `BAD_INPUT` (an index outside `lines`).

## POST /heartbeat (every 5 minutes)
Request `{ "appVersion": "1.0.0", "printKind": "text"|"image"|"unknown", "queueLength": 0, "lastForwardMs": 42, "lastProblem": "offline", "lastProblemText": "<Arabic>", "fieldsApplied": "<uuid>" }`
200 `{ "serverTime": "<ISO>", "qrEnabled": true, "codeValidityDays": 2, "latestVersion": "1.0.0", "downloadUrl": "https://...", "fieldsConfirmed": { "sampleId": "<uuid>", "invoiceLine": 3, "totalLine": 9 } }`
- **v1.4** `lastProblem` (code, `[a-z_]{1,40}`) and `lastProblemText` (its Arabic text, ≤ 400, scrubbed like `/fields`):
  the program's current problem, null when none, omitted = unchanged. Shown on the owner's device card and in /admin.
- **v1.4** `fieldsConfirmed`: the owner confirmed the sample `sampleId` on his dashboard. It is answered on every
  heartbeat until one carries `fieldsApplied` = that id (the program sends the id it last handled, applied or refused;
  a lost answer never drops the choice). The program applies a given id once, and only to the receipt it sent with that id, through the same `ConfirmFields` as the local window, and reports it
  (`report`, kind `other`). A refused choice re-sends the sample under a fresh `sampleId` with `problem`.
  An installer's confirmation in the program's own window sends that sample's id as `fieldsApplied` too, which also
  clears the pending sample on the dashboard.
- **v1.1** `latestVersion` (optional, env `DESK_LATEST_VERSION`): the newest released program version, semantic
  `MAJOR.MINOR.PATCH`. `downloadUrl` (optional, env `DESK_DOWNLOAD_URL`): an absolute https link to that version's
  `DaymanDesk.exe`. Each is omitted when its env is empty; the program shows an update only when both are present. The link must open without a login on a shop PC (the
  GitHub repository is private, so its release links do not qualify).
- **v1.1** program: if `latestVersion` is newer than its own version and `downloadUrl` is https, the window and the
  tray show «يوجد تحديث» with the link. No silent self-update in 1.0. A missing, older, equal or malformed value, or
  a non-https link, shows nothing.
- **WO-96** (program 2.1, no contract change): the program updates itself silently. `DESK_DOWNLOAD_URL` must point to
  the exe itself (e.g. `https://github.com/aliobedat/dayman-desk/releases/download/desk-latest/DaymanDesk.exe`, not a page);
  its signature sits at the same URL + `.sig` (base64 ECDSA P-256 / SHA-256, IEEE P1363, made by CI with
  `DESK_UPDATE_KEY`). Every hop must be https on github.com, objects.githubusercontent.com,
  release-assets.githubusercontent.com or daymanjo.com. An exe whose signature or file version does not match is
  never run; the download link stays the manual fallback.

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
- **v1.2** pool: the program keeps a local pool from `/codes`, topped up to 50 when fewer than 20 are left (after a
  heartbeat and after a registered receipt; a server without `/codes` leaves it empty), and stops printing a pool code
  25 days after receiving it. On a confidently read receipt whose total is within the amount bounds (1 to 10,000 JOD;
  outside them it waits for `/receipt` as in 1.1, which answers 422 and prints no QR) it takes the next pool code, prints the QR slip at once (`claimBase` + code), records invoice + local day → code
  (a reprint of the same invoice the same day prints the SAME code), then sends `/receipt` with `code`; offline or on
  a transient error the call is queued with its code and binds when the server answers. `already: true` with a
  different code is reported (`report`, kind `other`) and later reprints print that code. A refusal records the
  invoice as refused for the day: a reprint prints no QR. A new pairing or a revoked token empties the pool.
- With an empty pool (or a 1.1 program) the QR (claimUrl) is printed only after `/receipt` answered 200. Offline or
  any error → the receipt prints without a QR, the receipt is queued and sent later (no QR then), and a `report` is sent.
- `qrEnabled: false` (owner switched it off) → no QR, receipts still sent.
- **v1.1** `/receipt` is called only after the owner confirmed the two fields on the first text receipt after pairing
  («تأكيد الحقول»; **v1.4** on his dashboard through `/fields`, the window no longer opens by itself on the cashier's
  PC and stays on a button for an installer; status «بانتظار تأكيد صاحب المحل من لوحة التحكم»), and only when both the invoice number and the total reach confidence ≥ 0.75. The confirmation
  receipt itself is not registered.
- **v1.1** the device token is stored sealed with Windows DPAPI (LocalMachine scope), never in plain text.
