#include "auth.h"
#include <iostream>
#include <fstream>
#include <sstream>
#include <iomanip>
#include <random>
#include <chrono>
#include <algorithm>

static const uint8_t DB_XOR_KEY[] = {
    0x63, 0x6F, 0x72, 0x65, 0x58, 0x4C, 0x6F, 0x61,
    0x64, 0x65, 0x72, 0x4B, 0x65, 0x79, 0x44, 0x42
};
static const char DB_MAGIC[] = "CXK1";

static void XorBuffer(uint8_t* data, size_t len) {
    for (size_t i = 0; i < len; i++)
        data[i] ^= DB_XOR_KEY[i % sizeof(DB_XOR_KEY)];
}

static void SetColor(WORD c) {
    SetConsoleTextAttribute(GetStdHandle(STD_OUTPUT_HANDLE), c);
}

std::string auth::GetHWID() {
    std::stringstream ss;

    char compName[MAX_COMPUTERNAME_LENGTH + 1]{};
    DWORD compSize = sizeof(compName);
    GetComputerNameA(compName, &compSize);
    ss << compName << "|";

    DWORD volSerial = 0;
    GetVolumeInformationA("C:\\", nullptr, 0, &volSerial, nullptr, nullptr, nullptr, 0);
    ss << volSerial << "|";

    SYSTEM_INFO si{};
    GetNativeSystemInfo(&si);
    ss << si.dwProcessorType << "|" << si.dwNumberOfProcessors << "|" << si.wProcessorArchitecture;

    std::string raw = ss.str();

    uint32_t hash1 = 0x811c9dc5;
    uint32_t hash2 = 0x01000193;
    for (char c : raw) {
        hash1 ^= static_cast<uint8_t>(c);
        hash1 *= 0x01000193;
        hash2 ^= static_cast<uint8_t>(c);
        hash2 *= 0x811c9dc5;
    }

    char hwid[65]{};
    snprintf(hwid, sizeof(hwid), "%08X%08X%08X%08X",
        hash1, hash2, hash1 ^ hash2, hash1 + hash2);

    return std::string(hwid);
}

std::string auth::GenerateKeyString() {
    static const char chars[] = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    std::random_device rd;
    std::mt19937 gen(rd());
    std::uniform_int_distribution<> dist(0, sizeof(chars) - 2);

    std::string key = "CX-";
    for (int group = 0; group < 4; group++) {
        if (group > 0) key += '-';
        for (int i = 0; i < 5; i++)
            key += chars[dist(gen)];
    }
    return key;
}

const char* auth::DurationLabel(KeyDuration d) {
    switch (d) {
        case KeyDuration::OneDay:     return "1 Day";
        case KeyDuration::SevenDays:  return "7 Days";
        case KeyDuration::ThirtyDays: return "30 Days";
        case KeyDuration::Lifetime:   return "Lifetime";
        default:                      return "Unknown";
    }
}

KeyDuration auth::ParseDuration(const std::string& input) {
    if (input == "1" || input == "1d")   return KeyDuration::OneDay;
    if (input == "7" || input == "7d")   return KeyDuration::SevenDays;
    if (input == "30" || input == "30d") return KeyDuration::ThirtyDays;
    if (input == "0" || input == "lifetime" || input == "lt") return KeyDuration::Lifetime;
    return KeyDuration::SevenDays;
}

int auth::RemainingSeconds(const LicenseKey& lk) {
    if (lk.duration == KeyDuration::Lifetime) return -1;
    if (lk.activated_at == 0) return -1;

    auto now = std::chrono::system_clock::now();
    auto epoch = std::chrono::duration_cast<std::chrono::seconds>(now.time_since_epoch()).count();

    int64_t totalSec = static_cast<int>(lk.duration) * 86400LL;
    int64_t elapsed = epoch - lk.activated_at;
    int64_t remaining = totalSec - elapsed;

    return remaining > 0 ? static_cast<int>(remaining) : 0;
}

bool auth::IsExpired(const LicenseKey& lk) {
    if (lk.revoked) return true;
    if (lk.duration == KeyDuration::Lifetime) return false;
    if (lk.activated_at == 0) return false;
    return RemainingSeconds(lk) <= 0;
}

bool auth::LoadDatabase(const std::string& path, std::vector<LicenseKey>& keys) {
    std::ifstream file(path, std::ios::binary);
    if (!file.is_open()) return false;

    char magic[4]{};
    file.read(magic, 4);
    if (memcmp(magic, DB_MAGIC, 4) != 0) return false;

    uint32_t count = 0;
    file.read(reinterpret_cast<char*>(&count), 4);
    if (count > 100000) return false;

    for (uint32_t i = 0; i < count; i++) {
        LicenseKey lk{};
        file.read(reinterpret_cast<char*>(&lk), sizeof(LicenseKey));
        XorBuffer(reinterpret_cast<uint8_t*>(&lk), sizeof(LicenseKey));
        keys.push_back(lk);
    }

    return true;
}

bool auth::SaveDatabase(const std::string& path, const std::vector<LicenseKey>& keys) {
    std::ofstream file(path, std::ios::binary | std::ios::trunc);
    if (!file.is_open()) return false;

    file.write(DB_MAGIC, 4);

    uint32_t count = static_cast<uint32_t>(keys.size());
    file.write(reinterpret_cast<const char*>(&count), 4);

    for (const auto& key : keys) {
        LicenseKey copy = key;
        XorBuffer(reinterpret_cast<uint8_t*>(&copy), sizeof(LicenseKey));
        file.write(reinterpret_cast<const char*>(&copy), sizeof(LicenseKey));
    }

    return true;
}

LicenseKey* auth::FindKey(std::vector<LicenseKey>& keys, const std::string& keyStr) {
    for (auto& k : keys) {
        if (_stricmp(k.key, keyStr.c_str()) == 0)
            return &k;
    }
    return nullptr;
}

auth::ValidateResult auth::ValidateAndActivate(
    std::vector<LicenseKey>& keys, const std::string& keyStr, const std::string& hwid)
{
    LicenseKey* lk = FindKey(keys, keyStr);
    if (!lk) return ValidateResult::NotFound;
    if (lk->revoked) return ValidateResult::Revoked;

    if (lk->activated_at == 0) {
        strncpy_s(lk->hwid, hwid.c_str(), sizeof(lk->hwid) - 1);
        auto now = std::chrono::system_clock::now();
        lk->activated_at = std::chrono::duration_cast<std::chrono::seconds>(
            now.time_since_epoch()).count();
        return ValidateResult::OK;
    }

    if (_stricmp(lk->hwid, hwid.c_str()) != 0)
        return ValidateResult::AlreadyBoundOther;

    if (IsExpired(*lk))
        return ValidateResult::Expired;

    return ValidateResult::OK;
}

static std::string FormatTime(int64_t epoch) {
    if (epoch == 0) return "Not activated";
    time_t t = static_cast<time_t>(epoch);
    struct tm tm{};
    localtime_s(&tm, &t);
    char buf[64]{};
    strftime(buf, sizeof(buf), "%Y-%m-%d %H:%M:%S", &tm);
    return buf;
}

static std::string FormatRemaining(int seconds) {
    if (seconds < 0) return "Unlimited";
    if (seconds == 0) return "EXPIRED";
    int days = seconds / 86400;
    int hours = (seconds % 86400) / 3600;
    int mins = (seconds % 3600) / 60;
    std::stringstream ss;
    if (days > 0) ss << days << "d ";
    if (hours > 0) ss << hours << "h ";
    ss << mins << "m";
    return ss.str();
}

void auth::RunAdminMode(const std::string& dbPath) {
    SetColor(0x0B);
    std::cout << "\n  === coreX Key Manager ===" << std::endl;
    SetColor(0x07);
    std::cout << "  Database: " << dbPath << std::endl;
    std::cout << std::endl;
    std::cout << "  Commands:" << std::endl;
    std::cout << "    gen <duration>  - Generate key (1d, 7d, 30d, lifetime)" << std::endl;
    std::cout << "    gen <N> <dur>   - Generate N keys at once" << std::endl;
    std::cout << "    list            - List all keys" << std::endl;
    std::cout << "    status <key>    - Show key details" << std::endl;
    std::cout << "    revoke <key>    - Revoke a key" << std::endl;
    std::cout << "    reset <key>     - Unbind HWID and reset activation" << std::endl;
    std::cout << "    exit            - Exit admin mode" << std::endl;
    std::cout << std::endl;

    std::vector<LicenseKey> keys;
    auth::LoadDatabase(dbPath, keys);

    std::string line;
    while (true) {
        SetColor(0x0B);
        std::cout << "  admin> ";
        SetColor(0x07);

        if (!std::getline(std::cin, line)) break;
        if (line.empty()) continue;

        std::istringstream iss(line);
        std::string cmd;
        iss >> cmd;
        std::transform(cmd.begin(), cmd.end(), cmd.begin(), ::tolower);

        if (cmd == "exit" || cmd == "quit") {
            break;
        }
        else if (cmd == "gen" || cmd == "generate") {
            std::string arg1, arg2;
            iss >> arg1 >> arg2;

            int count = 1;
            std::string durStr = arg1;

            if (!arg2.empty()) {
                count = (std::max)(1, (std::min)(100, atoi(arg1.c_str())));
                durStr = arg2;
            }

            KeyDuration dur = auth::ParseDuration(durStr);

            std::cout << std::endl;
            for (int i = 0; i < count; i++) {
                LicenseKey lk{};
                std::string keyStr = auth::GenerateKeyString();
                strncpy_s(lk.key, keyStr.c_str(), sizeof(lk.key) - 1);
                memset(lk.hwid, 0, sizeof(lk.hwid));
                lk.duration = dur;
                lk.activated_at = 0;
                lk.revoked = false;
                keys.push_back(lk);

                SetColor(0x0A);
                std::cout << "    " << lk.key;
                SetColor(0x07);
                std::cout << "  [" << auth::DurationLabel(dur) << "]" << std::endl;
            }

            if (auth::SaveDatabase(dbPath, keys)) {
                SetColor(0x0A);
                std::cout << "\n  Saved. Total keys: " << keys.size() << std::endl;
            } else {
                SetColor(0x0C);
                std::cout << "\n  Failed to save database!" << std::endl;
            }
            SetColor(0x07);
            std::cout << std::endl;
        }
        else if (cmd == "list" || cmd == "ls") {
            std::cout << std::endl;
            if (keys.empty()) {
                std::cout << "  No keys in database." << std::endl;
            } else {
                SetColor(0x0E);
                std::cout << "  " << std::left
                          << std::setw(26) << "KEY"
                          << std::setw(12) << "DURATION"
                          << std::setw(12) << "STATUS"
                          << std::setw(14) << "REMAINING"
                          << "HWID" << std::endl;
                SetColor(0x08);
                std::cout << "  " << std::string(80, '-') << std::endl;
                SetColor(0x07);

                for (const auto& lk : keys) {
                    std::string status;
                    WORD statusColor;

                    if (lk.revoked) {
                        status = "REVOKED";
                        statusColor = 0x0C;
                    } else if (lk.activated_at == 0) {
                        status = "UNUSED";
                        statusColor = 0x0E;
                    } else if (auth::IsExpired(lk)) {
                        status = "EXPIRED";
                        statusColor = 0x0C;
                    } else {
                        status = "ACTIVE";
                        statusColor = 0x0A;
                    }

                    std::cout << "  " << std::left << std::setw(26) << lk.key
                              << std::setw(12) << auth::DurationLabel(lk.duration);
                    SetColor(statusColor);
                    std::cout << std::setw(12) << status;
                    SetColor(0x07);

                    std::string remaining = (lk.activated_at == 0) ? "—" :
                        FormatRemaining(auth::RemainingSeconds(lk));
                    std::cout << std::setw(14) << remaining;

                    if (lk.hwid[0])
                        std::cout << std::string(lk.hwid).substr(0, 16) << "...";
                    else
                        std::cout << "—";

                    std::cout << std::endl;
                }
            }
            std::cout << std::endl;
        }
        else if (cmd == "status") {
            std::string keyStr;
            iss >> keyStr;
            if (keyStr.empty()) {
                std::cout << "  Usage: status <key>" << std::endl;
                continue;
            }

            LicenseKey* lk = auth::FindKey(keys, keyStr);
            if (!lk) {
                SetColor(0x0C);
                std::cout << "  Key not found." << std::endl;
                SetColor(0x07);
                continue;
            }

            std::cout << std::endl;
            std::cout << "  Key:         " << lk->key << std::endl;
            std::cout << "  Duration:    " << auth::DurationLabel(lk->duration) << std::endl;
            std::cout << "  HWID:        " << (lk->hwid[0] ? lk->hwid : "Not bound") << std::endl;
            std::cout << "  Activated:   " << FormatTime(lk->activated_at) << std::endl;
            std::cout << "  Remaining:   " << FormatRemaining(auth::RemainingSeconds(*lk)) << std::endl;
            std::cout << "  Revoked:     " << (lk->revoked ? "YES" : "No") << std::endl;
            std::cout << std::endl;
        }
        else if (cmd == "revoke") {
            std::string keyStr;
            iss >> keyStr;
            if (keyStr.empty()) {
                std::cout << "  Usage: revoke <key>" << std::endl;
                continue;
            }

            LicenseKey* lk = auth::FindKey(keys, keyStr);
            if (!lk) {
                SetColor(0x0C);
                std::cout << "  Key not found." << std::endl;
                SetColor(0x07);
                continue;
            }

            lk->revoked = true;
            if (auth::SaveDatabase(dbPath, keys)) {
                SetColor(0x0A);
                std::cout << "  Key revoked." << std::endl;
            } else {
                SetColor(0x0C);
                std::cout << "  Failed to save!" << std::endl;
            }
            SetColor(0x07);
        }
        else if (cmd == "reset") {
            std::string keyStr;
            iss >> keyStr;
            if (keyStr.empty()) {
                std::cout << "  Usage: reset <key>" << std::endl;
                continue;
            }

            LicenseKey* lk = auth::FindKey(keys, keyStr);
            if (!lk) {
                SetColor(0x0C);
                std::cout << "  Key not found." << std::endl;
                SetColor(0x07);
                continue;
            }

            memset(lk->hwid, 0, sizeof(lk->hwid));
            lk->activated_at = 0;
            lk->revoked = false;

            if (auth::SaveDatabase(dbPath, keys)) {
                SetColor(0x0A);
                std::cout << "  Key reset — HWID unbound, timer cleared." << std::endl;
            } else {
                SetColor(0x0C);
                std::cout << "  Failed to save!" << std::endl;
            }
            SetColor(0x07);
        }
        else {
            SetColor(0x0C);
            std::cout << "  Unknown command: " << cmd << std::endl;
            SetColor(0x07);
        }
    }
}
