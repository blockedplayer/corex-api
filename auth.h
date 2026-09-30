#pragma once
#include <Windows.h>
#include <string>
#include <vector>
#include <cstdint>

enum class KeyDuration : int {
    OneDay     = 1,
    SevenDays  = 7,
    ThirtyDays = 30,
    Lifetime   = 0
};

struct LicenseKey {
    char key[25];
    char hwid[65];
    KeyDuration duration;
    int64_t activated_at;
    bool revoked;
};

namespace auth {

std::string GetHWID();
std::string GenerateKeyString();
const char* DurationLabel(KeyDuration d);
KeyDuration ParseDuration(const std::string& input);
int RemainingSeconds(const LicenseKey& lk);
bool IsExpired(const LicenseKey& lk);

bool LoadDatabase(const std::string& path, std::vector<LicenseKey>& keys);
bool SaveDatabase(const std::string& path, const std::vector<LicenseKey>& keys);

// Returns nullptr if not found
LicenseKey* FindKey(std::vector<LicenseKey>& keys, const std::string& keyStr);

enum class ValidateResult {
    OK,
    NotFound,
    AlreadyBoundOther,
    Expired,
    Revoked
};

ValidateResult ValidateAndActivate(std::vector<LicenseKey>& keys, const std::string& keyStr, const std::string& hwid);

void RunAdminMode(const std::string& dbPath);

}
