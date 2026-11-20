# WO-86: Dayman Desk 1.0, production grade (SENSITIVE)

Owner: Ali, 20 Nov 2026: «ما بدي نسخة أولية، بدي نسخة احترافية متكاملة جاهزة للعمل والنشر وفيها جميع الميزات».
Builds on WO-84 (server) and WO-85 (program v0, reviewed). Contract: docs/desk-api.md, bumped to v1.1 here.

## Program (desk/)
1. **First-receipt confirmation:** after pairing, the first text receipt opens «تأكيد الحقول»: the receipt text with the
   detected invoice number and total highlighted; the owner taps the right line for each if wrong. The learned labels
   and line positions are stored per device and used first afterwards. Registration needs confidence ≥ 0.75 on both.
2. **Token at rest:** DPAPI (CryptProtectData/CryptUnprotectData via P/Invoke to crypt32.dll, LocalMachine scope), no
   NuGet. Migrate a plain token on first start.
3. **Image receipts:** ESC/POS raster (GS v 0 / ESC * / GS ( L) decoded to a 1-bit bitmap, encoded as PNG (in-box
   System.Drawing on Windows, or a tiny hand-written PNG encoder in Core so it is testable on Linux), sent to
   POST /receipt-image (below); the answer is the same as /receipt; the QR slip prints as for text receipts.
4. **Status window, professional Arabic RTL:** connection state, shop name, print kind, today's receipts (registered,
   claimed, failed), last error in plain Arabic with what to do, version. A «مساعدة» page with the 5 install steps
   and «إيقاف وإرجاع الطابعة».
5. **Versioning and update check:** semantic version in the exe; /heartbeat answers `latestVersion` and `downloadUrl`;
   the tray shows «يوجد تحديث» with the link (no silent self-update in 1.0).
6. **Release:** every green build on master updates the `desk-latest` prerelease (exe + SHA256 file + REPORT.md).
7. All reviewer findings of v0 fixed (already in progress), plus a second full review of the final code.

## Server (main app)
8. **POST /receipt-image** (contract v1.1): `{ clientId, png (base64, ≤ 512 KB), printedAt }` → reads the receipt with
   Claude Haiku (`claude-haiku-5-5`, structured JSON: invoiceNo, total, shopName, date, confidence). Enabled only
   when `ANTHROPIC_API_KEY` is set (env, never logged, never NEXT_PUBLIC); without it → 503 `AI_DISABLED` and a
   report. Low confidence or a shop-name mismatch → 422 `UNREADABLE`, no code. Same validation and uniqueness as
   /receipt afterwards. Per-device rate limit (60/hour) and a per-shop daily cap on AI calls (default 500).
   The image is not stored unless the sample switch is on (30 days).
9. **/heartbeat** adds `latestVersion`, `downloadUrl` (env DESK_DOWNLOAD_URL; empty → omitted).
10. **Owner pages:** «دايماً ديسك» in settings: devices (status, last seen, version, kind), pairing, revoke, QR
    validity, the sample switch, QR on/off; today's desk receipts with claimed / not claimed.
11. CLAUDE.md: the AI receipt reading as part of the desk award path; ANTHROPIC_API_KEY in the env list.
12. Tests: /receipt-image with a stubbed model client (no network in tests), AI disabled path, cap, uniqueness.

## Out of scope
Code signing (no budget yet), silent self-update, POS APIs, customer-side receipt photos.
