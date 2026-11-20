# WO-84: «دايماً ديسك», the server side (SENSITIVE: new award path, new tables, device auth)

Owner: Ali, 19 Nov 2026 (approved: a Windows desk program on the cashier PC, C#/.NET, separate repo dayman-desk).
Read CLAUDE.md (points invariant, WO-61 delivery codes, WO-62 invoice numbers, admin rules) and lib/stamp-codes.ts.

## Goal
A desk program on the shop's cashier PC registers each printed receipt; the receipt carries a single-use QR; the
customer scans it and gets the points of that purchase. The cashier does nothing. We see every device's health.

## Scope
1. **Devices:** in «الإعدادات» the owner adds «جهاز ديسك» → a 6-character pairing code (10 min). The program trades it
   for a device token (random 32 bytes, only its hash stored) in `desk_devices` (tenant_id, name, token_hash,
   created_at, last_seen_at, app_version, print_kind 'text'|'image'|'unknown', revoked_at), RLS. Owner can revoke.
2. **API** `/api/desk/*` (Bearer device token, Zod, rate-limited in Postgres, never a merchant session):
   `pair`, `receipt` (invoice_no, amount, printed_at, kind) → returns the claim URL, `heartbeat`, `report`.
3. **Receipt → code:** `receipt` creates a stamp code exactly like `createDeliveryCode` (amount, single use) with
   `source 'desk'` and the invoice number; validity set by the owner (default 2 days, 1 to 14). Same invoice twice
   → the same code. The claim URL is the existing sticker route `/c/<shopToken>/s/<CODE>` (WO-42).
4. **Redeem:** unchanged `redeemCode` path (WO-61 rules: spend shop = purchase with that amount, one award row,
   method 'code'; visit shop = one stamp; daily cap and cooldown). The purchase carries the invoice_no, so the
   WO-62 unique index makes a cashier entry and a desk claim of the same invoice exclusive: first wins.
5. **Reports (feedback to us):** `desk_reports` (tenant_id, device_id, level info|warn|error, kind, message ≤ 2000,
   app_version, created_at), no customer data ever. A receipt sample (raw bytes, ≤ 64 KB) is stored only when the
   owner switched on «ساعد في تحسين دايماً ديسك», deleted after 30 days.
   Admin panel page «دايماً ديسك»: devices per shop (last seen, version, print kind, error count 7 days) and the
   latest reports. This needs a NEW wala_admin SELECT grant on desk_devices and desk_reports (not customer tables):
   state it in CLAUDE.md as a third sanctioned admin read.
6. Log: desk claims show in «سجل الحركات» as method «ديسك» with the invoice; «فاتورة غير مُستلمة» filter
   (registered receipts never claimed).
7. CLAUDE.md: add desk receipt codes to the points invariant (a fourth exception, approved by Ali 19 Nov 2026).
8. Tests `tests/wo84.test.mts`: pairing, revoked token refused, another shop's device NOT_FOUND (RLS), same invoice
   returns the same code, claim gives the right points, cashier entry of the same invoice blocks the claim (and the
   reverse), report without sample when the switch is off, admin reads devices but no customer table.

## Out of scope
The Windows program (WO-85), reading image receipts with AI (later), online payments, any POS API.
