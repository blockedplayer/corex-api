#include "mapper.h"
#include <fstream>
#include <iostream>

static void SetColor(WORD c) {
    SetConsoleTextAttribute(GetStdHandle(STD_OUTPUT_HANDLE), c);
}

// ==================== Direct Syscall Infrastructure ====================

typedef LONG NTSTATUS;
#define NT_SUCCESS(s) ((NTSTATUS)(s) >= 0)

typedef NTSTATUS(NTAPI* tNtAllocateVirtualMemory)(HANDLE, PVOID*, ULONG_PTR, PSIZE_T, ULONG, ULONG);
typedef NTSTATUS(NTAPI* tNtWriteVirtualMemory)(HANDLE, PVOID, PVOID, SIZE_T, PSIZE_T);
typedef NTSTATUS(NTAPI* tNtCreateThreadEx)(PHANDLE, ACCESS_MASK, PVOID, HANDLE, PVOID, PVOID, ULONG, SIZE_T, SIZE_T, SIZE_T, PVOID);
typedef NTSTATUS(NTAPI* tNtFreeVirtualMemory)(HANDLE, PVOID*, PSIZE_T, ULONG);

struct SyscallTable {
    tNtAllocateVirtualMemory pNtAllocateVirtualMemory;
    tNtWriteVirtualMemory pNtWriteVirtualMemory;
    tNtCreateThreadEx pNtCreateThreadEx;
    tNtFreeVirtualMemory pNtFreeVirtualMemory;
    void* stubs[4];
    int count;
};

static DWORD RvaToFileOffset(IMAGE_NT_HEADERS* pNT, DWORD rva) {
    IMAGE_SECTION_HEADER* pSec = IMAGE_FIRST_SECTION(pNT);
    for (WORD i = 0; i < pNT->FileHeader.NumberOfSections; i++, pSec++) {
        if (rva >= pSec->VirtualAddress && rva < pSec->VirtualAddress + pSec->Misc.VirtualSize)
            return rva - pSec->VirtualAddress + pSec->PointerToRawData;
    }
    return rva;
}

static DWORD FindSyscallNumber(BYTE* pFile, IMAGE_NT_HEADERS* pNT, IMAGE_EXPORT_DIRECTORY* pExp, const char* funcName) {
    DWORD* names = (DWORD*)(pFile + RvaToFileOffset(pNT, pExp->AddressOfNames));
    WORD* ordinals = (WORD*)(pFile + RvaToFileOffset(pNT, pExp->AddressOfNameOrdinals));
    DWORD* functions = (DWORD*)(pFile + RvaToFileOffset(pNT, pExp->AddressOfFunctions));

    for (DWORD i = 0; i < pExp->NumberOfNames; i++) {
        char* name = (char*)(pFile + RvaToFileOffset(pNT, names[i]));
        if (strcmp(name, funcName) == 0) {
            BYTE* pFunc = pFile + RvaToFileOffset(pNT, functions[ordinals[i]]);
            for (int j = 0; j < 32; j++) {
                if (pFunc[j] == 0x4C && pFunc[j + 1] == 0x8B && pFunc[j + 2] == 0xD1 && pFunc[j + 3] == 0xB8)
                    return *(DWORD*)(pFunc + j + 4);
            }
            break;
        }
    }
    return 0xFFFFFFFF;
}

static void* MakeStub(DWORD num) {
    BYTE code[] = {
        0x4C, 0x8B, 0xD1,
        0xB8, 0x00, 0x00, 0x00, 0x00,
        0x0F, 0x05,
        0xC3
    };
    *(DWORD*)(code + 4) = num;
    void* p = VirtualAlloc(nullptr, sizeof(code), MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE);
    if (p) memcpy(p, code, sizeof(code));
    return p;
}

static bool InitSyscalls(SyscallTable& sc) {
    memset(&sc, 0, sizeof(sc));

    char path[MAX_PATH];
    GetSystemDirectoryA(path, MAX_PATH);
    strcat_s(path, "\\ntdll.dll");

    HANDLE hFile = CreateFileA(path, GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING, 0, nullptr);
    if (hFile == INVALID_HANDLE_VALUE) return false;

    DWORD fileSize = GetFileSize(hFile, nullptr);
    BYTE* pFile = new BYTE[fileSize];
    DWORD br;
    ReadFile(hFile, pFile, fileSize, &br, nullptr);
    CloseHandle(hFile);

    IMAGE_DOS_HEADER* pDOS = (IMAGE_DOS_HEADER*)pFile;
    IMAGE_NT_HEADERS* pNT = (IMAGE_NT_HEADERS*)(pFile + pDOS->e_lfanew);
    DWORD expRva = pNT->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_EXPORT].VirtualAddress;
    IMAGE_EXPORT_DIRECTORY* pExp = (IMAGE_EXPORT_DIRECTORY*)(pFile + RvaToFileOffset(pNT, expRva));

    const char* names[] = {
        "NtAllocateVirtualMemory",
        "NtWriteVirtualMemory",
        "NtCreateThreadEx",
        "NtFreeVirtualMemory"
    };
    void** targets[] = {
        (void**)&sc.pNtAllocateVirtualMemory,
        (void**)&sc.pNtWriteVirtualMemory,
        (void**)&sc.pNtCreateThreadEx,
        (void**)&sc.pNtFreeVirtualMemory
    };

    sc.count = 0;
    for (int i = 0; i < 4; i++) {
        DWORD num = FindSyscallNumber(pFile, pNT, pExp, names[i]);
        if (num == 0xFFFFFFFF) { delete[] pFile; return false; }
        void* stub = MakeStub(num);
        if (!stub) { delete[] pFile; return false; }
        *targets[i] = stub;
        sc.stubs[sc.count++] = stub;
    }

    delete[] pFile;
    return true;
}

static void FreeSyscalls(SyscallTable& sc) {
    for (int i = 0; i < sc.count; i++)
        if (sc.stubs[i]) VirtualFree(sc.stubs[i], 0, MEM_RELEASE);
}

// ==================== Shellcode (runs inside target) ====================

struct LoaderData {
    LPVOID imageBase;
    HMODULE(WINAPI* fnLoadLibraryA)(LPCSTR);
    FARPROC(WINAPI* fnGetProcAddress)(HMODULE, LPCSTR);
    void(WINAPI* fnRtlZeroMemory)(PVOID, SIZE_T);
};

#pragma runtime_checks( "", off )
#pragma optimize( "", off )

static DWORD WINAPI Shellcode(LoaderData* pData) {
    if (!pData)
        return 1;

    BYTE* pBase = (BYTE*)pData->imageBase;

    IMAGE_DOS_HEADER* pDOS = (IMAGE_DOS_HEADER*)pBase;
    IMAGE_NT_HEADERS* pNT = (IMAGE_NT_HEADERS*)(pBase + pDOS->e_lfanew);
    IMAGE_OPTIONAL_HEADER* pOpt = &pNT->OptionalHeader;

    auto _LoadLibraryA = pData->fnLoadLibraryA;
    auto _GetProcAddress = pData->fnGetProcAddress;
    auto _RtlZeroMemory = pData->fnRtlZeroMemory;

    DWORD_PTR delta = (DWORD_PTR)(pBase - pOpt->ImageBase);
    if (delta) {
        if (pOpt->DataDirectory[IMAGE_DIRECTORY_ENTRY_BASERELOC].Size) {
            IMAGE_BASE_RELOCATION* pReloc = (IMAGE_BASE_RELOCATION*)(pBase +
                pOpt->DataDirectory[IMAGE_DIRECTORY_ENTRY_BASERELOC].VirtualAddress);

            while (pReloc->VirtualAddress) {
                DWORD count = (pReloc->SizeOfBlock - sizeof(IMAGE_BASE_RELOCATION)) / sizeof(WORD);
                WORD* pList = (WORD*)(pReloc + 1);

                for (DWORD i = 0; i < count; i++) {
                    int type = pList[i] >> 12;
                    DWORD offset = pList[i] & 0xFFF;

                    if (type == IMAGE_REL_BASED_DIR64) {
                        DWORD_PTR* pPatch = (DWORD_PTR*)(pBase + pReloc->VirtualAddress + offset);
                        *pPatch += delta;
                    }
                    else if (type == IMAGE_REL_BASED_HIGHLOW) {
                        DWORD* pPatch = (DWORD*)(pBase + pReloc->VirtualAddress + offset);
                        *pPatch += (DWORD)delta;
                    }
                }

                pReloc = (IMAGE_BASE_RELOCATION*)((BYTE*)pReloc + pReloc->SizeOfBlock);
            }
        }
    }

    if (pOpt->DataDirectory[IMAGE_DIRECTORY_ENTRY_IMPORT].Size) {
        IMAGE_IMPORT_DESCRIPTOR* pImport = (IMAGE_IMPORT_DESCRIPTOR*)(pBase +
            pOpt->DataDirectory[IMAGE_DIRECTORY_ENTRY_IMPORT].VirtualAddress);

        while (pImport->Name) {
            char* szMod = (char*)(pBase + pImport->Name);
            HMODULE hDll = _LoadLibraryA(szMod);

            ULONG_PTR* pThunk = (ULONG_PTR*)(pBase + (pImport->OriginalFirstThunk
                ? pImport->OriginalFirstThunk : pImport->FirstThunk));
            ULONG_PTR* pFunc = (ULONG_PTR*)(pBase + pImport->FirstThunk);

            for (; *pThunk; pThunk++, pFunc++) {
                if (IMAGE_SNAP_BY_ORDINAL(*pThunk)) {
                    *pFunc = (ULONG_PTR)_GetProcAddress(hDll, (char*)IMAGE_ORDINAL(*pThunk));
                }
                else {
                    IMAGE_IMPORT_BY_NAME* pName = (IMAGE_IMPORT_BY_NAME*)(pBase + *pThunk);
                    *pFunc = (ULONG_PTR)_GetProcAddress(hDll, pName->Name);
                }
            }
            pImport++;
        }
    }

    if (pOpt->DataDirectory[IMAGE_DIRECTORY_ENTRY_TLS].Size) {
        IMAGE_TLS_DIRECTORY* pTLS = (IMAGE_TLS_DIRECTORY*)(pBase +
            pOpt->DataDirectory[IMAGE_DIRECTORY_ENTRY_TLS].VirtualAddress);
        PIMAGE_TLS_CALLBACK* pCallback = (PIMAGE_TLS_CALLBACK*)pTLS->AddressOfCallBacks;
        if (pCallback) {
            while (*pCallback) {
                (*pCallback)((PVOID)pBase, DLL_PROCESS_ATTACH, nullptr);
                pCallback++;
            }
        }
    }

    typedef BOOL(WINAPI* tDllMain)(HINSTANCE, DWORD, LPVOID);
    tDllMain pDllMain = (tDllMain)(pBase + pOpt->AddressOfEntryPoint);
    pDllMain((HINSTANCE)pBase, DLL_PROCESS_ATTACH, nullptr);

    _RtlZeroMemory(pBase, pOpt->SizeOfHeaders);

    return 0;
}

static DWORD WINAPI ShellcodeEnd() { return 0; }

#pragma runtime_checks( "", restore )
#pragma optimize( "", on )

// ==================== Manual Map ====================

bool mapper::ManualMap(DWORD pid, const std::string& dllPath) {
    SyscallTable sc;
    if (!InitSyscalls(sc)) {
        SetColor(0x0C);
        std::cout << "  [ERROR] Syscall init failed" << std::endl;
        SetColor(0x07);
        return false;
    }

    SetColor(0x0A);
    std::cout << "  [+] Direct syscalls ready" << std::endl;
    SetColor(0x07);

    std::ifstream file(dllPath, std::ios::binary | std::ios::ate);
    if (!file.is_open()) {
        SetColor(0x0C);
        std::cout << "  [ERROR] Cannot read DLL file" << std::endl;
        SetColor(0x07);
        FreeSyscalls(sc);
        return false;
    }

    auto fileSize = file.tellg();
    file.seekg(0, std::ios::beg);
    BYTE* pFileData = new BYTE[(size_t)fileSize];
    file.read((char*)pFileData, fileSize);
    file.close();

    IMAGE_DOS_HEADER* pDOS = (IMAGE_DOS_HEADER*)pFileData;
    if (pDOS->e_magic != IMAGE_DOS_SIGNATURE) {
        delete[] pFileData;
        FreeSyscalls(sc);
        SetColor(0x0C);
        std::cout << "  [ERROR] Invalid DLL (bad DOS header)" << std::endl;
        SetColor(0x07);
        return false;
    }

    IMAGE_NT_HEADERS* pNT = (IMAGE_NT_HEADERS*)(pFileData + pDOS->e_lfanew);
    if (pNT->Signature != IMAGE_NT_SIGNATURE) {
        delete[] pFileData;
        FreeSyscalls(sc);
        SetColor(0x0C);
        std::cout << "  [ERROR] Invalid DLL (bad NT header)" << std::endl;
        SetColor(0x07);
        return false;
    }

    IMAGE_OPTIONAL_HEADER* pOpt = &pNT->OptionalHeader;

    HANDLE hProcess = OpenProcess(PROCESS_ALL_ACCESS, FALSE, pid);
    if (!hProcess) {
        delete[] pFileData;
        FreeSyscalls(sc);
        SetColor(0x0C);
        std::cout << "  [ERROR] Cannot open process (" << GetLastError() << ")" << std::endl;
        SetColor(0x07);
        return false;
    }

    // Allocate via direct syscall
    PVOID pTargetBase = nullptr;
    SIZE_T regionSize = pOpt->SizeOfImage;
    NTSTATUS status = sc.pNtAllocateVirtualMemory(hProcess, &pTargetBase, 0, &regionSize,
        MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE);

    if (!NT_SUCCESS(status)) {
        delete[] pFileData;
        CloseHandle(hProcess);
        FreeSyscalls(sc);
        SetColor(0x0C);
        std::cout << "  [ERROR] Alloc failed (NTSTATUS: 0x" << std::hex << (DWORD)status << std::dec << ")" << std::endl;
        SetColor(0x07);
        return false;
    }

    SetColor(0x0E);
    std::cout << "  [*] Mapped at 0x" << std::hex << (uintptr_t)pTargetBase << std::dec << std::endl;
    SetColor(0x07);

    // Write headers via direct syscall
    sc.pNtWriteVirtualMemory(hProcess, pTargetBase, pFileData, pOpt->SizeOfHeaders, nullptr);

    // Write sections via direct syscall
    IMAGE_SECTION_HEADER* pSection = IMAGE_FIRST_SECTION(pNT);
    for (WORD i = 0; i < pNT->FileHeader.NumberOfSections; i++, pSection++) {
        if (pSection->SizeOfRawData) {
            sc.pNtWriteVirtualMemory(hProcess, (BYTE*)pTargetBase + pSection->VirtualAddress,
                pFileData + pSection->PointerToRawData, pSection->SizeOfRawData, nullptr);
        }
    }

    delete[] pFileData;

    // Prepare loader data
    LoaderData loaderData{};
    loaderData.imageBase = pTargetBase;
    loaderData.fnLoadLibraryA = (decltype(loaderData.fnLoadLibraryA))
        GetProcAddress(GetModuleHandleA("kernel32.dll"), "LoadLibraryA");
    loaderData.fnGetProcAddress = (decltype(loaderData.fnGetProcAddress))
        GetProcAddress(GetModuleHandleA("kernel32.dll"), "GetProcAddress");
    loaderData.fnRtlZeroMemory = (decltype(loaderData.fnRtlZeroMemory))
        GetProcAddress(GetModuleHandleA("ntdll.dll"), "RtlZeroMemory");

    // Allocate loader data via direct syscall
    PVOID pLoaderMem = nullptr;
    SIZE_T loaderSize = sizeof(LoaderData);
    status = sc.pNtAllocateVirtualMemory(hProcess, &pLoaderMem, 0, &loaderSize,
        MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
    if (!NT_SUCCESS(status)) {
        SIZE_T freeSize = 0;
        sc.pNtFreeVirtualMemory(hProcess, &pTargetBase, &freeSize, MEM_RELEASE);
        CloseHandle(hProcess);
        FreeSyscalls(sc);
        SetColor(0x0C);
        std::cout << "  [ERROR] Loader data alloc failed (0x" << std::hex << (DWORD)status << ")" << std::dec << std::endl;
        SetColor(0x07);
        return false;
    }

    sc.pNtWriteVirtualMemory(hProcess, pLoaderMem, &loaderData, sizeof(LoaderData), nullptr);

    // Allocate shellcode via direct syscall
    SIZE_T shellcodeSize = (SIZE_T)((BYTE*)ShellcodeEnd - (BYTE*)Shellcode);
    if (shellcodeSize == 0 || shellcodeSize > 0x10000)
        shellcodeSize = 0x1000;

    PVOID pShellcodeMem = nullptr;
    SIZE_T scSize = shellcodeSize;
    status = sc.pNtAllocateVirtualMemory(hProcess, &pShellcodeMem, 0, &scSize,
        MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE);
    if (!NT_SUCCESS(status)) {
        SIZE_T freeSize = 0;
        sc.pNtFreeVirtualMemory(hProcess, &pTargetBase, &freeSize, MEM_RELEASE);
        freeSize = 0;
        sc.pNtFreeVirtualMemory(hProcess, &pLoaderMem, &freeSize, MEM_RELEASE);
        CloseHandle(hProcess);
        FreeSyscalls(sc);
        SetColor(0x0C);
        std::cout << "  [ERROR] Shellcode alloc failed (0x" << std::hex << (DWORD)status << ")" << std::dec << std::endl;
        SetColor(0x07);
        return false;
    }

    sc.pNtWriteVirtualMemory(hProcess, pShellcodeMem, Shellcode, shellcodeSize, nullptr);

    // Create remote thread via direct syscall
    HANDLE hThread = nullptr;
    status = sc.pNtCreateThreadEx(&hThread, THREAD_ALL_ACCESS, nullptr, hProcess,
        pShellcodeMem, pLoaderMem, 0, 0, 0, 0, nullptr);

    if (!NT_SUCCESS(status) || !hThread) {
        SIZE_T freeSize = 0;
        sc.pNtFreeVirtualMemory(hProcess, &pTargetBase, &freeSize, MEM_RELEASE);
        freeSize = 0;
        sc.pNtFreeVirtualMemory(hProcess, &pLoaderMem, &freeSize, MEM_RELEASE);
        freeSize = 0;
        sc.pNtFreeVirtualMemory(hProcess, &pShellcodeMem, &freeSize, MEM_RELEASE);
        CloseHandle(hProcess);
        FreeSyscalls(sc);
        SetColor(0x0C);
        std::cout << "  [ERROR] Thread failed (0x" << std::hex << (DWORD)status << ")" << std::dec << std::endl;
        SetColor(0x07);
        return false;
    }

    WaitForSingleObject(hThread, 10000);

    CloseHandle(hThread);
    SIZE_T freeSize = 0;
    sc.pNtFreeVirtualMemory(hProcess, &pLoaderMem, &freeSize, MEM_RELEASE);
    freeSize = 0;
    sc.pNtFreeVirtualMemory(hProcess, &pShellcodeMem, &freeSize, MEM_RELEASE);
    CloseHandle(hProcess);
    FreeSyscalls(sc);

    return true;
}