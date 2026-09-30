#include <Windows.h>
#include <iostream>
#include <string>
#include <filesystem>
#include <fstream>
#include "auth.h"

namespace fs = std::filesystem;

static void SetColor(WORD c) { SetConsoleTextAttribute(GetStdHandle(STD_OUTPUT_HANDLE), c); }

static void PrintBanner() {
    SetColor(0x0B);
    std::cout << R"(
   ___  ___  ___  ___  _  __
  / __\/ _ \/ _ \/ _ \| |/ /
 / /  | | | | |_) |  __/|   <
/ /___| |_| |  _ <| |___| . \
\____/ \___/|_| \_\\____|_|\_\
)" << std::endl;
    SetColor(0x07);
    std::cout << "  CORE X  v2.1" << std::endl;
    std::cout << "  ================================" << std::endl << std::endl;
}

static std::string LoadConfig(const std::string& path) {
    std::ifstream f(path);
    if (!f.is_open()) return "";
    std::string line;
    std::getline(f, line);
    return line;
}

static fs::path FindLoader(const fs::path& exeDir) {
    // Check multiple locations for CoreX.Loader.exe
    std::vector<fs::path> candidates = {
        exeDir / "CoreX.Loader" / "bin" / "Debug" / "net8.0-windows" / "CoreX.Loader.exe",
        exeDir / "CoreX.Loader" / "bin" / "Release" / "net8.0-windows" / "CoreX.Loader.exe",
        exeDir / "CoreX.Loader.exe",
        exeDir / "Loader" / "CoreX.Loader.exe",
    };
    for (auto& p : candidates)
        if (fs::exists(p)) return p;
    return {};
}

static int RunClean(const fs::path& exeDir) {
    PrintBanner();
    std::string cfgPath = (exeDir / "gamedir.cfg").string();
    std::string gameDir = LoadConfig(cfgPath);

    if (gameDir.empty() || !fs::exists(gameDir)) {
        SetColor(0x0C);
        std::cout << "  [ERROR] No game directory configured." << std::endl;
        SetColor(0x07);
        return 1;
    }

    std::vector<std::string> files = { "cxpayload.dll", "t10 workspace.dll", "dummy.dll" };
    int removed = 0;
    for (auto& f : files) {
        fs::path p = fs::path(gameDir) / f;
        try { if (fs::exists(p)) { fs::remove(p); removed++; SetColor(0x0A); std::cout << "  [+] Deleted " << f << std::endl; } }
        catch (...) { SetColor(0x0C); std::cout << "  [!] Could not delete " << f << std::endl; }
    }

    // Restore original version.dll from backup
    fs::path proxy  = fs::path(gameDir) / "version.dll";
    fs::path backup = fs::path(gameDir) / "version.dll.orig";
    try {
        if (fs::exists(proxy)) fs::remove(proxy);
        if (fs::exists(backup)) {
            fs::rename(backup, proxy);
            SetColor(0x0A);
            std::cout << "  [+] Original version.dll restored." << std::endl;
        }
    }
    catch (...) {}

    SetColor(0x0A);
    std::cout << "  [+] Game directory cleaned." << std::endl;
    SetColor(0x07);
    return 0;
}

int main(int argc, char* argv[]) {
    SetConsoleTitleA("CORE X");

    fs::path exeDir = fs::path(argv[0]).parent_path();
    if (exeDir.empty()) exeDir = ".";
    std::string dbPath = (exeDir / "keys.dat").string();

    // --admin: key management mode (kept for backwards compat)
    if (argc > 1 && std::string(argv[1]) == "--admin") {
        PrintBanner();
        auth::RunAdminMode(dbPath);
        return 0;
    }

    // --clean: remove DLLs from game dir
    if (argc > 1 && std::string(argv[1]) == "--clean") {
        return RunClean(exeDir);
    }

    PrintBanner();

    // --- Find and launch the WPF loader ---
    SetColor(0x0E);
    std::cout << "  [*] Locating CORE X Loader..." << std::endl;
    SetColor(0x07);

    fs::path loaderPath = FindLoader(exeDir);

    if (loaderPath.empty()) {
        SetColor(0x0C);
        std::cout << "  [ERROR] CoreX.Loader.exe not found." << std::endl;
        std::cout << std::endl;
        std::cout << "  Expected locations:" << std::endl;
        std::cout << "    " << (exeDir / "CoreX.Loader" / "bin" / "Debug" / "net8.0-windows" / "CoreX.Loader.exe").string() << std::endl;
        std::cout << "    " << (exeDir / "CoreX.Loader.exe").string() << std::endl;
        std::cout << std::endl;
        std::cout << "  Build it first:" << std::endl;
        std::cout << "    cd CoreX.Loader && dotnet build" << std::endl;
        SetColor(0x07);
        std::cout << std::endl << "  Press any key to exit...";
        std::cin.get();
        return 1;
    }

    SetColor(0x0A);
    std::cout << "  [+] Found: " << loaderPath.filename().string() << std::endl;
    std::cout << "  [*] Launching CORE X Loader..." << std::endl;
    SetColor(0x07);

    STARTUPINFOW si{};
    si.cb = sizeof(si);
    PROCESS_INFORMATION pi{};

    std::wstring cmdLine = L"\"" + loaderPath.wstring() + L"\"";

    BOOL ok = CreateProcessW(
        loaderPath.wstring().c_str(),
        cmdLine.data(),
        nullptr, nullptr, FALSE,
        0, nullptr,
        loaderPath.parent_path().wstring().c_str(),
        &si, &pi
    );

    if (!ok) {
        DWORD err = GetLastError();
        SetColor(0x0C);
        std::cout << "  [ERROR] Failed to launch loader (error " << err << ")" << std::endl;

        if (err == 0x000003D || err == 0x00000002) {
            std::cout << std::endl;
            std::cout << "  .NET 8 Desktop Runtime may not be installed." << std::endl;
            std::cout << "  Download: https://dotnet.microsoft.com/download/dotnet/8.0" << std::endl;
        }

        SetColor(0x07);
        std::cout << std::endl << "  Press any key to exit...";
        std::cin.get();
        return 1;
    }

    SetColor(0x0A);
    std::cout << "  [+] CORE X Loader started (PID " << pi.dwProcessId << ")" << std::endl;
    SetColor(0x07);
    std::cout << std::endl << "  Closing bootstrapper..." << std::endl;

    CloseHandle(pi.hProcess);
    CloseHandle(pi.hThread);

    Sleep(1500);
    return 0;
}
