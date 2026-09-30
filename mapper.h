#pragma once
#include <Windows.h>
#include <string>

namespace mapper {
    bool ManualMap(DWORD pid, const std::string& dllPath);
}