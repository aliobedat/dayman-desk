# WO-89: Dayman Desk works with every kind of cashier (SENSITIVE: runs on the shop's cashier PC)

Owner: Ali, 20 Nov 2026: «افترض جميع الأجوبة وابنِ النظام يتعامل مع كل أنواع الكاشيرات». Builds on WO-86 (desk 1.0).
Hard rules unchanged: the cashier never touches the program; printing is never delayed or blocked (forward first,
analyse after); on any doubt no QR, never a wrong one; if the program dies the watchdog restores every original port.

1. Several receipt printers per PC: each gets its own loopback port (9100, 9101, ...), DAYMAN_<port> printer port and
   hidden raw queue on its real port; the QR slip goes to the printer the receipt came from. Watchdog/uninstall restore all.
2. Standard TCP/IP (RAW) printers: the forwarder sends straight to IP:port; unreachable → the job goes to the raw queue
   on the original TCP/IP port (the spooler queues and retries as the POS would), a report, no QR.
3. «وضع الشبكة» (off by default): a LAN listener (0.0.0.0, chosen port, firewall rule for the local subnet only) proxies
   tablets/Android POS to the real printer IP both ways; printer unreachable → the POS connection is refused as it
   would be without us. The status window shows this PC's IP.
4. Kitchen/order tickets (no confident total, or a kitchen marker): forwarded, no QR, no report, not a failure.
5. Raster receipts (GS v 0, ESC *, GS ( L, GS 8 L) are decoded to a bitmap in Core and read with Windows.Media.Ocr in App
   (ar-SA, else en-US; the installer adds the OCR language capabilities, best effort). OCR text goes through the same
   extractor (confirmation, learned labels, ≥ 0.75) with OCR rules: decimals required, ambiguous digit groups refused.
6. Unknown formats: forward, no QR, one report per format per day. 7. REPORT.md install steps. 8. Core tests.
Contract unchanged (docs/desk-api.md v1.1: image receipts register through /receipt with kind "image").
