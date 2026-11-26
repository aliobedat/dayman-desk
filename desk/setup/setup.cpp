// DaymanDeskSetup.exe (WO-103): the one file a shop owner downloads and double-clicks.
// Checks Windows, installs .NET Framework 4.8 when missing (Microsoft's signed offline installer), adds the ISRG
// roots when missing, copies the embedded DaymanDesk.exe to Program Files\DaymanDesk and starts it. The program
// then installs its tasks and shortcut itself and shows the pairing screen.
// Plain Win32, no STL: built with MSVC, static CRT (/MT), as x86 so one file runs on 32- and 64-bit Windows.
// --check prints what it would do and changes nothing; --check-download also downloads and verifies the .NET installer.
#define WIN32_LEAN_AND_MEAN
#define _WIN32_WINNT 0x0601
#include <windows.h>
#include <commctrl.h>
#include <shellapi.h>
#include <winhttp.h>
#include <bcrypt.h>
#include <wincrypt.h>
#include <wintrust.h>
#include <softpub.h>
#include <versionhelpers.h>
#include <stdio.h>
#include <string.h>
#include <wchar.h>

#if !defined(DESK_V1) || !defined(DESK_V2) || !defined(DESK_V3) || !defined(DESK_V4)
#error "build with /DDESK_V1=.. /DDESK_V2=.. /DDESK_V3=.. /DDESK_V4=.. (the embedded DaymanDesk.exe file version)"
#endif
static const ULONGLONG kEmbedded = ((ULONGLONG)DESK_V1 << 48) | ((ULONGLONG)DESK_V2 << 32) | ((ULONGLONG)DESK_V3 << 16) | (ULONGLONG)DESK_V4;

enum { IDR_DESK = 101, IDR_X1 = 102, IDR_X2 = 103, IDR_MS2011 = 104 };

static const wchar_t kTitle[] = L"دايماً ديسك";
static const wchar_t kOldWindows[] = L"هذا الجهاز يحتاج تحديث ويندوز قبل التثبيت. استخدم شاشة الكاشير على الموبايل مؤقتاً.";
static const wchar_t kChecking[] = L"جاري التحقق من الجهاز…";
static const wchar_t kPreparing[] = L"جاري تجهيز الجهاز (مرة واحدة فقط)…";
static const wchar_t kInstalling[] = L"جاري تثبيت دايماً ديسك…";
static const wchar_t kDownloadFailed[] = L"تعذّر تنزيل ملفات تجهيز الجهاز. تأكد من اتصال الإنترنت، ثم شغّل ملف التثبيت مرة أخرى.";
static const wchar_t kNotTrusted[] = L"الملف الذي نزل من الإنترنت ليس من Microsoft، فلم يُشغَّل. شغّل ملف التثبيت مرة أخرى.";
static const wchar_t kNetFailed[] = L"تعذّر تجهيز الجهاز (رمز %lu). أعد تشغيل الجهاز، ثم شغّل ملف التثبيت مرة أخرى.";
static const wchar_t kRestart[] = L"لازم نعيد تشغيل الجهاز. اضغط موافق\n\nبعد التشغيل يكمل التثبيت وحده.";
static const wchar_t kCopyFailed[] = L"تعذّر نسخ البرنامج. إن كان دايماً ديسك يعمل فاختر «خروج (إرجاع الطابعة)» من الأيقونة بجانب الساعة، ثم شغّل ملف التثبيت مرة أخرى.";

// Microsoft's offline installer for .NET Framework 4.8 (Windows 7 SP1 to 11), as linked from dotnet.microsoft.com.
static const wchar_t kNetHost[] = L"download.visualstudio.microsoft.com";
static const wchar_t kNetPath[] = L"/download/pr/2d6bb6b2-226a-4baa-bdec-798822606ff1/8494001c276a4b96804cde7829c04d7f/ndp48-x86-x64-allos-enu.exe";
static const DWORD kNet48Release = 528040;
// SHA-256 of that exact file: anything else is refused, even when signed by Microsoft.
static const char kNetSha256[] = "68c9986a8dcc0214d909aa1f31bee9fb5461bb839edca996a75b08ddffc1483f";

struct Root { int res; const char* name; const char* sha256; };
static const Root kRoots[] = {
    { IDR_X1, "ISRG Root X1", "96bcec06264976f37460779acf28c5a7cfe8a3c0aae11a8ffcee05c0bddf08c6" },
    { IDR_X2, "ISRG Root X2", "69729b8e15a86efc177a57afb7171dfc64add28c2fca8cf1507e34453ccb1470" },
};
// The root of Microsoft's code-signing chain for the .NET installer; old Windows 7 stores lack it (WinVerifyTrust fails).
static const Root kMsRoot = { IDR_MS2011, "Microsoft Root Certificate Authority 2011", "847df6a78497943f27fc72eb93f9a637320a02b561d0a91b09e87a7807ed7c61" };

static bool g_check, g_checkDownload;
static HWND g_win, g_text;
static volatile LONG g_done;
static int g_exit;

// ---- output (--check) and UI ----

static void Out(const char* fmt, ...)
{
    if (!g_check) return;
    static HANDLE h;
    if (!h)
    {
        h = GetStdHandle(STD_OUTPUT_HANDLE);
        if (!h || h == INVALID_HANDLE_VALUE)
        {
            AttachConsole(ATTACH_PARENT_PROCESS);
            h = CreateFileW(L"CONOUT$", GENERIC_WRITE, FILE_SHARE_WRITE, nullptr, OPEN_EXISTING, 0, nullptr);
        }
    }
    char buf[1024];
    va_list a;
    va_start(a, fmt);
    int n = vsnprintf(buf, sizeof buf - 2, fmt, a);
    va_end(a);
    if (n < 0) return;
    if (n > (int)sizeof buf - 3) n = (int)sizeof buf - 3;
    buf[n++] = '\r'; buf[n++] = '\n';
    DWORD w;
    if (h && h != INVALID_HANDLE_VALUE) WriteFile(h, buf, (DWORD)n, &w, nullptr);
}

static void Status(const wchar_t* text)
{
    if (g_text) SetWindowTextW(g_text, text);
}

static void Msg(const wchar_t* text, UINT icon)
{
    if (g_check) { Out("would show a message (icon 0x%x)", icon); return; }
    MessageBoxW(g_win, text, kTitle, MB_OK | icon | MB_RTLREADING | MB_RIGHT | MB_SETFOREGROUND);
}

// ---- helpers ----

static const BYTE* Res(int id, DWORD* size)
{
    HRSRC r = FindResourceW(nullptr, MAKEINTRESOURCEW(id), MAKEINTRESOURCEW(10)); // RT_RCDATA
    HGLOBAL g = r ? LoadResource(nullptr, r) : nullptr;
    *size = r ? SizeofResource(nullptr, r) : 0;
    return g ? (const BYTE*)LockResource(g) : nullptr;
}

static bool Sha256Is(const BYTE* data, DWORD n, const char* hex)
{
    BYTE h[32]; DWORD cb = sizeof h;
    if (!CryptHashCertificate2(BCRYPT_SHA256_ALGORITHM, 0, nullptr, data, n, h, &cb) || cb != 32) return false;
    char s[65];
    for (int i = 0; i < 32; i++) sprintf_s(s + i * 2, 3, "%02x", h[i]);
    return strcmp(s, hex) == 0;
}

static DWORD NetRelease()
{
    DWORD v = 0, cb = sizeof v, type = 0;
    HKEY k;
    if (RegOpenKeyExW(HKEY_LOCAL_MACHINE, L"SOFTWARE\\Microsoft\\NET Framework Setup\\NDP\\v4\\Full", 0,
            KEY_QUERY_VALUE | KEY_WOW64_64KEY, &k) != ERROR_SUCCESS) return 0;
    if (RegQueryValueExW(k, L"Release", nullptr, &type, (BYTE*)&v, &cb) != ERROR_SUCCESS || type != REG_DWORD) v = 0;
    RegCloseKey(k);
    return v;
}

static ULONGLONG FileVersion(const wchar_t* path)
{
    DWORD dummy, n = GetFileVersionInfoSizeW(path, &dummy);
    if (!n) return 0;
    BYTE* buf = (BYTE*)HeapAlloc(GetProcessHeap(), 0, n);
    ULONGLONG v = 0;
    VS_FIXEDFILEINFO* fi; UINT len;
    if (buf && GetFileVersionInfoW(path, 0, n, buf) && VerQueryValueW(buf, L"\\", (void**)&fi, &len) && len >= sizeof *fi)
        v = ((ULONGLONG)fi->dwFileVersionMS << 32) | fi->dwFileVersionLS;
    if (buf) HeapFree(GetProcessHeap(), 0, buf);
    return v;
}

static void VersionText(ULONGLONG v, char* out, size_t n)
{
    if (!v) { strcpy_s(out, n, "none"); return; }
    sprintf_s(out, n, "%u.%u.%u.%u", (unsigned)(v >> 48), (unsigned)(v >> 32) & 0xffff, (unsigned)(v >> 16) & 0xffff, (unsigned)v & 0xffff);
}

static DWORD RunWait(const wchar_t* exe, const wchar_t* args)
{
    wchar_t cmd[MAX_PATH * 2];
    swprintf_s(cmd, L"\"%s\" %s", exe, args);
    STARTUPINFOW si = { sizeof si };
    PROCESS_INFORMATION pi;
    if (!CreateProcessW(exe, cmd, nullptr, nullptr, FALSE, 0, nullptr, nullptr, &si, &pi)) return GetLastError() | 0x80000000;
    WaitForSingleObject(pi.hProcess, INFINITE);
    DWORD code = 1;
    GetExitCodeProcess(pi.hProcess, &code);
    CloseHandle(pi.hThread);
    CloseHandle(pi.hProcess);
    return code;
}

// Program Files\DaymanDesk (the program's own folder, Installer.cs): only administrators may write there.
static void InstallDir(wchar_t* dir, size_t n)
{
    wchar_t pf[MAX_PATH] = L"";
    // A 32-bit process on 64-bit Windows: the real Program Files, as the AnyCPU program sees it.
    if (!GetEnvironmentVariableW(L"ProgramW6432", pf, MAX_PATH)) GetEnvironmentVariableW(L"ProgramFiles", pf, MAX_PATH);
    swprintf_s(dir, n, L"%s\\DaymanDesk", pf);
}

/// SHA-256 of an open file, read from its start, as lowercase hex.
static bool FileSha256(HANDLE f, char* hex)
{
    HCRYPTPROV prov; HCRYPTHASH hh;
    if (!CryptAcquireContextW(&prov, nullptr, nullptr, PROV_RSA_AES, CRYPT_VERIFYCONTEXT)) return false;
    bool ok = CryptCreateHash(prov, CALG_SHA_256, 0, 0, &hh) != FALSE;
    if (ok)
    {
        static BYTE buf[1 << 16];
        SetFilePointer(f, 0, nullptr, FILE_BEGIN);
        for (DWORD n; ok && ReadFile(f, buf, sizeof buf, &n, nullptr) && n; ) ok = CryptHashData(hh, buf, n, 0) != FALSE;
        BYTE h[32]; DWORD cb = sizeof h;
        ok = ok && CryptGetHashParam(hh, HP_HASHVAL, h, &cb, 0) && cb == 32;
        for (int i = 0; ok && i < 32; i++) sprintf_s(hex + i * 2, 3, "%02x", h[i]);
        CryptDestroyHash(hh);
        SetFilePointer(f, 0, nullptr, FILE_BEGIN);
    }
    CryptReleaseContext(prov, 0);
    return ok;
}

// ---- step 2: .NET Framework 4.8 ----

/// GET of the .NET installer, bytes written to `path`. HTTPS with TLS 1.2 switched on explicitly (Windows 7 does not
/// offer it by default); `secure` false is the plain-http retry for a Windows 7 without KB3140245 (no TLS 1.2 in
/// WinHTTP). Either way nothing runs unless WinVerifyTrust and the Microsoft signer check pass.
static bool Download(const wchar_t* path, bool secure, ULONGLONG* got)
{
    bool ok = false;
    *got = 0;
    HINTERNET s = WinHttpOpen(L"DaymanDeskSetup/1", WINHTTP_ACCESS_TYPE_DEFAULT_PROXY, WINHTTP_NO_PROXY_NAME, WINHTTP_NO_PROXY_BYPASS, 0);
    if (!s) return false;
    DWORD proto = WINHTTP_FLAG_SECURE_PROTOCOL_TLS1_2 | WINHTTP_FLAG_SECURE_PROTOCOL_TLS1_1;
    if (!WinHttpSetOption(s, WINHTTP_OPTION_SECURE_PROTOCOLS, &proto, sizeof proto) && g_check)
        Out("  WinHTTP refused the TLS 1.2 option (error %lu): Windows 7 needs KB3140245", GetLastError());
    WinHttpSetTimeouts(s, 30000, 30000, 30000, 120000);
    HINTERNET c = WinHttpConnect(s, kNetHost, secure ? INTERNET_DEFAULT_HTTPS_PORT : INTERNET_DEFAULT_HTTP_PORT, 0);
    HINTERNET r = c ? WinHttpOpenRequest(c, L"GET", kNetPath, nullptr, WINHTTP_NO_REFERER, WINHTTP_DEFAULT_ACCEPT_TYPES, secure ? WINHTTP_FLAG_SECURE : 0) : nullptr;
    // Plain http must not be sent on to https: that is the very TLS that failed.
    DWORD never = WINHTTP_OPTION_REDIRECT_POLICY_NEVER;
    if (r && !secure) WinHttpSetOption(r, WINHTTP_OPTION_REDIRECT_POLICY, &never, sizeof never);
    DWORD status = 0, cb = sizeof status;
    if (r && WinHttpSendRequest(r, WINHTTP_NO_ADDITIONAL_HEADERS, 0, WINHTTP_NO_REQUEST_DATA, 0, 0, 0) && WinHttpReceiveResponse(r, nullptr)
        && WinHttpQueryHeaders(r, WINHTTP_QUERY_STATUS_CODE | WINHTTP_QUERY_FLAG_NUMBER, WINHTTP_HEADER_NAME_BY_INDEX, &status, &cb, WINHTTP_NO_HEADER_INDEX)
        && status == 200)
    {
        wchar_t lenText[32] = L""; DWORD lb = sizeof lenText;
        ULONGLONG total = WinHttpQueryHeaders(r, WINHTTP_QUERY_CONTENT_LENGTH, WINHTTP_HEADER_NAME_BY_INDEX, lenText, &lb, WINHTTP_NO_HEADER_INDEX)
            ? _wcstoui64(lenText, nullptr, 10) : 0;
        HANDLE f = CreateFileW(path, GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (f != INVALID_HANDLE_VALUE)
        {
            static BYTE buf[1 << 16];
            int shown = -1;
            ok = true;
            for (;;)
            {
                DWORD n = 0, w = 0;
                if (!WinHttpReadData(r, buf, sizeof buf, &n)) { ok = false; break; }
                if (n == 0) break;
                if (!WriteFile(f, buf, n, &w, nullptr) || w != n) { ok = false; break; }
                *got += n;
                int pct = total ? (int)(*got * 100 / total) : -1;
                if (pct != shown && pct >= 0)
                {
                    shown = pct;
                    wchar_t t[128];
                    swprintf_s(t, L"%s %d%%", kPreparing, pct);
                    Status(t);
                }
            }
            CloseHandle(f);
            if (total && *got != total) ok = false;
        }
    }
    else if (g_check) Out("  request failed: HTTP %lu, error %lu", status, GetLastError());
    if (r) WinHttpCloseHandle(r);
    if (c) WinHttpCloseHandle(c);
    WinHttpCloseHandle(s);
    return ok && *got > 0;
}

/// A valid Authenticode signature that chains to a trusted root, whose signer is «Microsoft Corporation».
static bool SignedByMicrosoft(const wchar_t* path, HANDLE file)
{
    WINTRUST_FILE_INFO fi = { sizeof fi };
    fi.pcwszFilePath = path;
    fi.hFile = file; // the handle kept open until the installer has started: no swap after the check
    WINTRUST_DATA wd = { sizeof wd };
    wd.dwUIChoice = WTD_UI_NONE;
    wd.fdwRevocationChecks = WTD_REVOKE_NONE;
    wd.dwUnionChoice = WTD_CHOICE_FILE;
    wd.pFile = &fi;
    wd.dwStateAction = WTD_STATEACTION_VERIFY;
    GUID action = WINTRUST_ACTION_GENERIC_VERIFY_V2;
    LONG st = WinVerifyTrust((HWND)INVALID_HANDLE_VALUE, &action, &wd);
    wchar_t name[256] = L"";
    if (st == ERROR_SUCCESS)
    {
        CRYPT_PROVIDER_DATA* pd = WTHelperProvDataFromStateData(wd.hWVTStateData);
        CRYPT_PROVIDER_SGNR* sg = pd ? WTHelperGetProvSignerFromChain(pd, 0, FALSE, 0) : nullptr;
        if (sg && sg->csCertChain > 0 && sg->pasCertChain[0].pCert)
            CertGetNameStringW(sg->pasCertChain[0].pCert, CERT_NAME_SIMPLE_DISPLAY_TYPE, 0, nullptr, name, 256);
    }
    wd.dwStateAction = WTD_STATEACTION_CLOSE;
    WinVerifyTrust((HWND)INVALID_HANDLE_VALUE, &action, &wd);
    bool ok = st == ERROR_SUCCESS && wcscmp(name, L"Microsoft Corporation") == 0;
    if (g_check)
    {
        char n8[256];
        WideCharToMultiByte(CP_UTF8, 0, name, -1, n8, sizeof n8, nullptr, nullptr);
        Out("  WinVerifyTrust 0x%08lx, signer \"%s\" -> %s", (unsigned long)st, n8, ok ? "trusted" : "REFUSED");
    }
    return ok;
}

/// Continue this setup once Windows is back (HKLM RunOnce runs it at the next administrator logon).
/// The copy in Program Files\DaymanDesk, never the one in Downloads (a user could replace that file before the next
/// administrator logon runs it).
static void ContinueAfterReboot(const wchar_t* dir)
{
    wchar_t self[MAX_PATH], copy[MAX_PATH], cmd[MAX_PATH + 2];
    GetModuleFileNameW(nullptr, self, MAX_PATH);
    swprintf_s(copy, L"%s\\DaymanDeskSetup.exe", dir);
    if (!CopyFileW(self, copy, FALSE)) return;
    swprintf_s(cmd, L"\"%s\"", copy);
    HKEY k;
    if (RegCreateKeyExW(HKEY_LOCAL_MACHINE, L"SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\RunOnce", 0, nullptr, 0,
            KEY_SET_VALUE | KEY_WOW64_64KEY, nullptr, &k, nullptr) == ERROR_SUCCESS)
    {
        RegSetValueExW(k, L"DaymanDeskSetup", 0, REG_SZ, (const BYTE*)cmd, (DWORD)((wcslen(cmd) + 1) * sizeof(wchar_t)));
        RegCloseKey(k);
    }
}

static void Reboot()
{
    HANDLE t;
    if (OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, &t))
    {
        TOKEN_PRIVILEGES tp = { 1 };
        LookupPrivilegeValueW(nullptr, SE_SHUTDOWN_NAME, &tp.Privileges[0].Luid);
        tp.Privileges[0].Attributes = SE_PRIVILEGE_ENABLED;
        AdjustTokenPrivileges(t, FALSE, &tp, 0, nullptr, nullptr);
        CloseHandle(t);
    }
    ExitWindowsEx(EWX_REBOOT | EWX_FORCEIFHUNG, SHTDN_REASON_MAJOR_APPLICATION | SHTDN_REASON_MINOR_INSTALLATION | SHTDN_REASON_FLAG_PLANNED);
}

enum { kRebooting = -1 };

/// Checks the open file (pinned SHA-256, then Microsoft's signature) and starts it while that handle, shared for
/// reading only, is still open: nobody can change it in between. The installer's exit code, or 0x8000000x if refused.
static DWORD CheckAndRun(const wchar_t* path)
{
    HANDLE f = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (f == INVALID_HANDLE_VALUE) return 0x80000001;
    char hex[65] = "";
    bool pinned = FileSha256(f, hex) && strcmp(hex, kNetSha256) == 0;
    Out("  SHA-256 %s -> %s", hex, pinned ? "the pinned file" : "NOT the pinned file");
    DWORD code = 0x80000002;
    if (pinned && SignedByMicrosoft(path, f))
        code = g_check ? 0 : RunWait(path, L"/q /norestart");
    CloseHandle(f);
    return code;
}

static int InstallNet48()
{
    Status(kPreparing);
    // Downloaded into the administrators-only program folder, never %TEMP%.
    wchar_t dir[MAX_PATH], path[MAX_PATH];
    InstallDir(dir, MAX_PATH);
    bool made = CreateDirectoryW(dir, nullptr) != FALSE;
    swprintf_s(path, L"%s\\ndp48-x86-x64-allos-enu.exe", dir);
    ULONGLONG got;
    bool ok = Download(path, true, &got);
    Out("  https: %s, %llu bytes", ok ? "ok" : "FAILED", got);
    // --check-download tries plain http too, so CI shows the Windows 7 fallback still works.
    if (!ok || g_checkDownload)
    {
        if (g_checkDownload && ok && CheckAndRun(path) != 0) { DeleteFileW(path); return 4; }
        ok = Download(path, false, &got);
        Out("  http: %s, %llu bytes", ok ? "ok" : "FAILED", got);
    }
    if (!ok) { DeleteFileW(path); Msg(kDownloadFailed, MB_ICONWARNING); return 3; }
    Status(kPreparing);
    DWORD code = CheckAndRun(path);
    DeleteFileW(path);
    if (g_check && made) RemoveDirectoryW(dir);
    if (code & 0x80000000) { Msg(kNotTrusted, MB_ICONERROR); return 4; }
    if (g_check) { Out("  would run it with /q /norestart"); return 0; }
    if (code == 3010 || code == 1641)
    {
        ContinueAfterReboot(dir);
        Msg(kRestart, MB_ICONINFORMATION);
        Reboot();
        return kRebooting;
    }
    if (code != 0)
    {
        wchar_t t[256];
        swprintf_s(t, kNetFailed, code);
        Msg(t, MB_ICONWARNING);
        return 5;
    }
    return 0;
}

// ---- step 3: root certificates ----

static bool EnsureRoot(const Root& root)
{
    DWORD n;
    const BYTE* der = Res(root.res, &n);
    if (!der || !Sha256Is(der, n, root.sha256)) { Out("root %s: embedded file wrong SHA-256, not added", root.name); return false; }
    PCCERT_CONTEXT cert = CertCreateCertificateContext(X509_ASN_ENCODING, der, n);
    HCERTSTORE st = CertOpenStore(CERT_STORE_PROV_SYSTEM_W, 0, 0,
        CERT_SYSTEM_STORE_LOCAL_MACHINE | (g_check ? CERT_STORE_READONLY_FLAG : 0), L"ROOT");
    bool ok = false;
    if (cert && st)
    {
        PCCERT_CONTEXT found = CertFindCertificateInStore(st, X509_ASN_ENCODING, 0, CERT_FIND_EXISTING, cert, nullptr);
        if (found) { CertFreeCertificateContext(found); ok = true; Out("root %s: SHA-256 ok, in LocalMachine\\Root", root.name); }
        else if (g_check) { ok = true; Out("root %s: SHA-256 ok, missing from LocalMachine\\Root -> would add it", root.name); }
        else ok = CertAddCertificateContextToStore(st, cert, CERT_STORE_ADD_NEW, nullptr) || GetLastError() == CRYPT_E_EXISTS;
    }
    if (st) CertCloseStore(st, 0);
    if (cert) CertFreeCertificateContext(cert);
    return ok;
}

// ---- steps 4 and 5: DaymanDesk.exe ----

static bool Extract(const wchar_t* dir, const wchar_t* exe, const BYTE* data, DWORD n)
{
    CreateDirectoryW(dir, nullptr);
    wchar_t tmp[MAX_PATH];
    swprintf_s(tmp, L"%s.new", exe);
    HANDLE f = CreateFileW(tmp, GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (f == INVALID_HANDLE_VALUE) return false;
    DWORD w = 0;
    bool ok = WriteFile(f, data, n, &w, nullptr) && w == n;
    CloseHandle(f);
    // The watchdog runs the installed exe for under a second each minute: retry a locked file.
    for (int i = 0; ok && i < 3; i++)
    {
        if (MoveFileExW(tmp, exe, MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH)) return true;
        Sleep(3000);
    }
    DeleteFileW(tmp);
    return false;
}

static int Run()
{
    if (g_check) Out("DaymanDeskSetup --check: a dry run, nothing on this machine is changed");
    Status(kChecking);
    // 1. Windows 7 SP1 or newer.
    if (!IsWindows7SP1OrGreater()) { Out("windows: older than Windows 7 SP1 -> stop"); Msg(kOldWindows, MB_ICONWARNING); return 1; }
    Out("windows: Windows 7 SP1 or newer -> ok");

    // 2. .NET Framework 4.8 (exactly 4.8: 4.8.1 does not install on Windows 7), after the root of its signature.
    bool roots = EnsureRoot(kMsRoot);
    DWORD rel = NetRelease();
    if (rel >= kNet48Release && !g_checkDownload) Out("net48: Release %lu -> installed", rel);
    else
    {
        Out("net48: Release %lu -> %s https://%ls%ls, verify the Microsoft signature, run /q /norestart",
            rel, g_checkDownload ? "downloading" : "would download", kNetHost, kNetPath);
        if (!g_check || g_checkDownload)
        {
            int r = InstallNet48();
            if (r == kRebooting) return 0;
            if (r) return r;
        }
    }

    // 3. Root certificates for the server's HTTPS (old Windows 7 stores lack them).
    for (const Root& root : kRoots) roots &= EnsureRoot(root);

    // 4 and 5. The program, where it installs itself (Installer.cs: Program Files\DaymanDesk\DaymanDesk.exe).
    DWORD n;
    const BYTE* desk = Res(IDR_DESK, &n);
    if (!desk || n < 2 || desk[0] != 'M' || desk[1] != 'Z') { Out("embedded DaymanDesk.exe: missing"); return 7; }
    wchar_t dir[MAX_PATH], exe[MAX_PATH];
    InstallDir(dir, MAX_PATH);
    swprintf_s(exe, L"%s\\DaymanDesk.exe", dir);
    ULONGLONG installed = FileVersion(exe);
    char a[32], b[32];
    VersionText(installed, a, sizeof a);
    VersionText(kEmbedded, b, sizeof b);
    bool copy = installed < kEmbedded;
    Out("install: %ls, installed %s, embedded %s (%lu bytes) -> %s", exe, a, b, n,
        copy ? (g_check ? "would copy it there and start it" : "copy and start") : (g_check ? "would start the installed one" : "start"));
    if (g_check) return roots ? 0 : 8;

    if (copy)
    {
        Status(kInstalling);
        if (!Extract(dir, exe, desk, n)) { Msg(kCopyFailed, MB_ICONWARNING); return 6; }
    }
    SHELLEXECUTEINFOW se = { sizeof se };
    se.lpFile = exe;
    se.lpDirectory = dir;
    se.nShow = SW_SHOWNORMAL;
    return ShellExecuteExW(&se) ? 0 : 9;
}

// ---- window ----

static DWORD WINAPI Work(LPVOID)
{
    g_exit = Run();
    InterlockedExchange(&g_done, 1);
    PostMessageW(g_win, WM_CLOSE, 0, 0);
    return 0;
}

static LRESULT CALLBACK WndProc(HWND h, UINT m, WPARAM w, LPARAM l)
{
    switch (m)
    {
    case WM_CLOSE:
        if (g_done) DestroyWindow(h); // the owner cannot stop a half-done install
        return 0;
    case WM_DESTROY:
        PostQuitMessage(0);
        return 0;
    case WM_CTLCOLORSTATIC:
        SetBkMode((HDC)w, TRANSPARENT);
        return (LRESULT)GetSysColorBrush(COLOR_WINDOW);
    }
    return DefWindowProcW(h, m, w, l);
}

int WINAPI wWinMain(HINSTANCE inst, HINSTANCE, LPWSTR, int)
{
    // The delay-loaded DLLs (desk.yml) come from System32 only, never from the Downloads folder next to this file.
    // SetDefaultDllDirectories needs KB2533623 on Windows 7; without it at least the current directory is dropped.
    typedef BOOL(WINAPI * SetDirs)(DWORD);
    SetDirs setDirs = (SetDirs)GetProcAddress(GetModuleHandleW(L"kernel32.dll"), "SetDefaultDllDirectories");
    if (!setDirs || !setDirs(LOAD_LIBRARY_SEARCH_SYSTEM32)) SetDllDirectoryW(L"");
    int argc;
    wchar_t** argv = CommandLineToArgvW(GetCommandLineW(), &argc);
    for (int i = 1; argv && i < argc; i++)
    {
        if (!wcscmp(argv[i], L"--check")) g_check = true;
        if (!wcscmp(argv[i], L"--check-download")) g_check = g_checkDownload = true;
    }
    if (g_check) return Run();

    INITCOMMONCONTROLSEX icc = { sizeof icc, ICC_PROGRESS_CLASS };
    InitCommonControlsEx(&icc);
    WNDCLASSW wc = {};
    wc.lpfnWndProc = WndProc;
    wc.hInstance = inst;
    wc.hIcon = LoadIconW(inst, MAKEINTRESOURCEW(1));
    wc.hCursor = LoadCursorW(nullptr, IDC_ARROW);
    wc.hbrBackground = GetSysColorBrush(COLOR_WINDOW);
    wc.lpszClassName = L"DaymanDeskSetup";
    RegisterClassW(&wc);

    HDC dc = GetDC(nullptr);
    int dpi = GetDeviceCaps(dc, LOGPIXELSY);
    ReleaseDC(nullptr, dc);
#define S(x) MulDiv((x), dpi, 96)
    RECT rc = { 0, 0, S(440), S(120) };
    DWORD style = WS_OVERLAPPED | WS_CAPTION | WS_SYSMENU | WS_MINIMIZEBOX;
    AdjustWindowRectEx(&rc, style, FALSE, WS_EX_LAYOUTRTL);
    RECT wa;
    SystemParametersInfoW(SPI_GETWORKAREA, 0, &wa, 0);
    int ww = rc.right - rc.left, wh = rc.bottom - rc.top;
    g_win = CreateWindowExW(WS_EX_LAYOUTRTL, wc.lpszClassName, kTitle, style,
        wa.left + (wa.right - wa.left - ww) / 2, wa.top + (wa.bottom - wa.top - wh) / 2, ww, wh, nullptr, nullptr, inst, nullptr);

    NONCLIENTMETRICSW ncm = { sizeof ncm };
    SystemParametersInfoW(SPI_GETNONCLIENTMETRICS, sizeof ncm, &ncm, 0);
    ncm.lfMessageFont.lfHeight = ncm.lfMessageFont.lfHeight * 5 / 4;
    HFONT font = CreateFontIndirectW(&ncm.lfMessageFont);
    g_text = CreateWindowExW(WS_EX_RTLREADING, L"STATIC", kChecking, WS_CHILD | WS_VISIBLE | SS_LEFT,
        S(24), S(24), S(392), S(36), g_win, nullptr, inst, nullptr);
    SendMessageW(g_text, WM_SETFONT, (WPARAM)font, TRUE);
    HWND bar = CreateWindowExW(0, PROGRESS_CLASSW, nullptr, WS_CHILD | WS_VISIBLE | PBS_MARQUEE,
        S(24), S(72), S(392), S(18), g_win, nullptr, inst, nullptr);
    SendMessageW(bar, PBM_SETMARQUEE, TRUE, 30);
#undef S
    ShowWindow(g_win, SW_SHOWNORMAL);
    UpdateWindow(g_win);

    HANDLE t = CreateThread(nullptr, 0, Work, nullptr, 0, nullptr);
    if (!t) return 1;
    MSG msg;
    while (GetMessageW(&msg, nullptr, 0, 0) > 0)
    {
        TranslateMessage(&msg);
        DispatchMessageW(&msg);
    }
    WaitForSingleObject(t, INFINITE);
    CloseHandle(t);
    return g_exit;
}
