# WO-85: «دايماً ديسك», the Windows program v0 (SENSITIVE: runs on the shop's cashier PC)

Owner: Ali, 19 Nov 2026. New repo `dayman-desk` (Ali creates it empty on GitHub). C# .NET 8, one self-contained
exe, no paid tools, no code signing yet (the installer shows SmartScreen once). Server API: WO-84.

## Goal
Installed in 15 minutes on a Windows cashier PC, it sits between the POS and the receipt printer, never delays a
print, reads each receipt's invoice number and amount, prints a QR for it, and reports its health to us.

## How it intercepts (port level, so every POS works the same)
The installer records the receipt printer's current port (USB001, IP, COM), adds a hidden raw queue on that real
port, and points the POS's printer at a local TCP port (127.0.0.1:9100) served by the program. The POS and its
driver are untouched; the program receives the printer's own bytes and forwards them at once.
**Never block printing:** forward first, analyse after; a watchdog restarts the program; «إيقاف دايماً ديسك» and
the uninstaller restore the original port in one click; if the program is down, the installer's fallback task
restores the port automatically within 60 seconds.

## Scope
1. Installer + tray app + Windows service; Arabic UI; pairing with the WO-84 code; choose the receipt printer.
2. **Detect the print kind** on the first receipts: ESC/POS text, ESC/POS raster image, or unknown. Show it.
3. **Text path (full):** decode (CP864/CP1256/UTF-8 Arabic, Latin), find the invoice number and total by labels
   learned from the shop's first receipts plus defaults («رقم الفاتورة», «فاتورة», «Invoice», «الإجمالي»,
   «المجموع», «Total», «Net»); the owner confirms the two fields once on the first receipt.
4. **QR:** text receipts get the QR appended before the cut; image/unknown receipts get a small separate QR slip
   right after. Text under it: «امسح لتحصل على نقاطك».
5. **Image path (basic):** detected and reported; no QR until AI reading exists (later order).
6. **Offline:** a local queue; receipts sent when back online; the QR is printed only after the server answered
   (or, offline, skipped and reported).
7. **Feedback to us (WO-84 report):** heartbeat every 5 min; errors, unknown formats, parse failures, slow
   forwards (> 300 ms) as reports; a raw sample only with the owner's switch on. Never a phone number or name.
8. A test harness that replays captured print files (folder `samples/`) through the parser; Ali adds the real
   files from جمرة and الطلة later.

## Out of scope
AI reading of image receipts, POS APIs, auto-update (manual reinstall in v0), code signing, kitchen printing.
