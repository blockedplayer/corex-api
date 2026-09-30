#include <Windows.h>

static HMODULE g_real = nullptr;

static void InitReal() {
    if (g_real) return;
    char path[MAX_PATH];
    GetSystemDirectoryA(path, MAX_PATH);
    strcat_s(path, "\\version.dll");
    g_real = LoadLibraryA(path);
}

static FARPROC Orig(const char* name) {
    InitReal();
    return g_real ? GetProcAddress(g_real, name) : nullptr;
}

static DWORD WINAPI LoadPayloadThread(LPVOID) {
    Sleep(10000);

    char dir[MAX_PATH];
    GetModuleFileNameA(nullptr, dir, MAX_PATH);
    char* p = strrchr(dir, '\\');
    if (p) p[1] = '\0';

    char path[MAX_PATH];

    strcpy_s(path, dir);
    strcat_s(path, "cxpayload.dll");
    if (GetFileAttributesA(path) != INVALID_FILE_ATTRIBUTES) {
        LoadLibraryA(path);

        HANDLE hLog = CreateFileA("C:\\Users\\Germani Rosario\\Desktop\\cxlog2.txt",
            GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (hLog != INVALID_HANDLE_VALUE) {
            const char* msg = "PAYLOAD: cxpayload.dll loaded\r\n";
            DWORD w; WriteFile(hLog, msg, (DWORD)strlen(msg), &w, nullptr);
            CloseHandle(hLog);
        }

        return 0;
    }

    strcpy_s(path, dir);
    strcat_s(path, "t10 workspace.dll");
    if (GetFileAttributesA(path) != INVALID_FILE_ATTRIBUTES) {
        LoadLibraryA(path);

        HANDLE hLog = CreateFileA("C:\\Users\\Germani Rosario\\Desktop\\cxlog2.txt",
            GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (hLog != INVALID_HANDLE_VALUE) {
            const char* msg = "PAYLOAD: t10 workspace.dll loaded\r\n";
            DWORD w; WriteFile(hLog, msg, (DWORD)strlen(msg), &w, nullptr);
            CloseHandle(hLog);
        }

        return 0;
    }

    strcpy_s(path, dir);
    strcat_s(path, "dummy.dll");
    if (GetFileAttributesA(path) != INVALID_FILE_ATTRIBUTES) {
        LoadLibraryA(path);

        HANDLE hLog = CreateFileA("C:\\Users\\Germani Rosario\\Desktop\\cxlog2.txt",
            GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (hLog != INVALID_HANDLE_VALUE) {
            const char* msg = "PAYLOAD: dummy.dll loaded\r\n";
            DWORD w; WriteFile(hLog, msg, (DWORD)strlen(msg), &w, nullptr);
            CloseHandle(hLog);
        }
    }

    return 0;
}

BOOL APIENTRY DllMain(HMODULE hModule, DWORD reason, LPVOID) {
    if (reason == DLL_PROCESS_ATTACH) {
        DisableThreadLibraryCalls(hModule);
        InitReal();

        HANDLE hLog = CreateFileA("C:\\Users\\Germani Rosario\\Desktop\\cxlog.txt",
            GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (hLog != INVALID_HANDLE_VALUE) {
            const char* msg = "PROXY: version.dll loaded by game\r\n";
            DWORD w; WriteFile(hLog, msg, (DWORD)strlen(msg), &w, nullptr);
            CloseHandle(hLog);
        }

        CreateThread(nullptr, 0, LoadPayloadThread, nullptr, 0, nullptr);
    }
    return TRUE;
}

extern "C" {

    BOOL WINAPI cx_GetFileVersionInfoA(LPCSTR a, DWORD b, DWORD c, LPVOID d) {
        auto f = (BOOL(WINAPI*)(LPCSTR, DWORD, DWORD, LPVOID))Orig("GetFileVersionInfoA");
        return f ? f(a, b, c, d) : FALSE;
    }

    BOOL WINAPI cx_GetFileVersionInfoW(LPCWSTR a, DWORD b, DWORD c, LPVOID d) {
        auto f = (BOOL(WINAPI*)(LPCWSTR, DWORD, DWORD, LPVOID))Orig("GetFileVersionInfoW");
        return f ? f(a, b, c, d) : FALSE;
    }

    BOOL WINAPI cx_GetFileVersionInfoExA(DWORD fl, LPCSTR a, DWORD b, DWORD c, LPVOID d) {
        auto f = (BOOL(WINAPI*)(DWORD, LPCSTR, DWORD, DWORD, LPVOID))Orig("GetFileVersionInfoExA");
        return f ? f(fl, a, b, c, d) : FALSE;
    }

    BOOL WINAPI cx_GetFileVersionInfoExW(DWORD fl, LPCWSTR a, DWORD b, DWORD c, LPVOID d) {
        auto f = (BOOL(WINAPI*)(DWORD, LPCWSTR, DWORD, DWORD, LPVOID))Orig("GetFileVersionInfoExW");
        return f ? f(fl, a, b, c, d) : FALSE;
    }

    DWORD WINAPI cx_GetFileVersionInfoSizeA(LPCSTR a, LPDWORD b) {
        auto f = (DWORD(WINAPI*)(LPCSTR, LPDWORD))Orig("GetFileVersionInfoSizeA");
        return f ? f(a, b) : 0;
    }

    DWORD WINAPI cx_GetFileVersionInfoSizeW(LPCWSTR a, LPDWORD b) {
        auto f = (DWORD(WINAPI*)(LPCWSTR, LPDWORD))Orig("GetFileVersionInfoSizeW");
        return f ? f(a, b) : 0;
    }

    DWORD WINAPI cx_GetFileVersionInfoSizeExA(DWORD fl, LPCSTR a, LPDWORD b) {
        auto f = (DWORD(WINAPI*)(DWORD, LPCSTR, LPDWORD))Orig("GetFileVersionInfoSizeExA");
        return f ? f(fl, a, b) : 0;
    }

    DWORD WINAPI cx_GetFileVersionInfoSizeExW(DWORD fl, LPCWSTR a, LPDWORD b) {
        auto f = (DWORD(WINAPI*)(DWORD, LPCWSTR, LPDWORD))Orig("GetFileVersionInfoSizeExW");
        return f ? f(fl, a, b) : 0;
    }

    int WINAPI cx_GetFileVersionInfoByHandle(int a, LPCWSTR b, DWORD c, LPVOID d) {
        auto f = (int(WINAPI*)(int, LPCWSTR, DWORD, LPVOID))Orig("GetFileVersionInfoByHandle");
        return f ? f(a, b, c, d) : 0;
    }

    DWORD WINAPI cx_VerFindFileA(DWORD a, LPCSTR b, LPCSTR c, LPCSTR d, LPSTR e, PUINT g, LPSTR h, PUINT j) {
        auto f = (DWORD(WINAPI*)(DWORD, LPCSTR, LPCSTR, LPCSTR, LPSTR, PUINT, LPSTR, PUINT))Orig("VerFindFileA");
        return f ? f(a, b, c, d, e, g, h, j) : 0;
    }

    DWORD WINAPI cx_VerFindFileW(DWORD a, LPCWSTR b, LPCWSTR c, LPCWSTR d, LPWSTR e, PUINT g, LPWSTR h, PUINT j) {
        auto f = (DWORD(WINAPI*)(DWORD, LPCWSTR, LPCWSTR, LPCWSTR, LPWSTR, PUINT, LPWSTR, PUINT))Orig("VerFindFileW");
        return f ? f(a, b, c, d, e, g, h, j) : 0;
    }

    DWORD WINAPI cx_VerInstallFileA(DWORD a, LPCSTR b, LPCSTR c, LPCSTR d, LPCSTR e, LPCSTR g, LPSTR h, PUINT j) {
        auto f = (DWORD(WINAPI*)(DWORD, LPCSTR, LPCSTR, LPCSTR, LPCSTR, LPCSTR, LPSTR, PUINT))Orig("VerInstallFileA");
        return f ? f(a, b, c, d, e, g, h, j) : 0;
    }

    DWORD WINAPI cx_VerInstallFileW(DWORD a, LPCWSTR b, LPCWSTR c, LPCWSTR d, LPCWSTR e, LPCWSTR g, LPWSTR h, PUINT j) {
        auto f = (DWORD(WINAPI*)(DWORD, LPCWSTR, LPCWSTR, LPCWSTR, LPCWSTR, LPCWSTR, LPWSTR, PUINT))Orig("VerInstallFileW");
        return f ? f(a, b, c, d, e, g, h, j) : 0;
    }

    DWORD WINAPI cx_VerLanguageNameA(DWORD a, LPSTR b, DWORD c) {
        auto f = (DWORD(WINAPI*)(DWORD, LPSTR, DWORD))Orig("VerLanguageNameA");
        return f ? f(a, b, c) : 0;
    }

    DWORD WINAPI cx_VerLanguageNameW(DWORD a, LPWSTR b, DWORD c) {
        auto f = (DWORD(WINAPI*)(DWORD, LPWSTR, DWORD))Orig("VerLanguageNameW");
        return f ? f(a, b, c) : 0;
    }

    BOOL WINAPI cx_VerQueryValueA(LPCVOID a, LPCSTR b, LPVOID* c, PUINT d) {
        auto f = (BOOL(WINAPI*)(LPCVOID, LPCSTR, LPVOID*, PUINT))Orig("VerQueryValueA");
        return f ? f(a, b, c, d) : FALSE;
    }

    BOOL WINAPI cx_VerQueryValueW(LPCVOID a, LPCWSTR b, LPVOID* c, PUINT d) {
        auto f = (BOOL(WINAPI*)(LPCVOID, LPCWSTR, LPVOID*, PUINT))Orig("VerQueryValueW");
        return f ? f(a, b, c, d) : FALSE;
    }

} // extern "C"