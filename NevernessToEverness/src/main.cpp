#include <windows.h>
#include <shellapi.h>
#include <tlhelp32.h>
#include <oleacc.h>
#include <dbghelp.h>

#include <algorithm>
#include <atomic>
#include <chrono>
#include <cmath>
#include <filesystem>
#include <fstream>
#include <iomanip>
#include <mutex>
#include <optional>
#include <queue>
#include <sstream>
#include <stdexcept>
#include <string>
#include <thread>
#include <unordered_set>
#include <vector>

namespace fs = std::filesystem;
using namespace std::chrono_literals;

namespace {
constexpr wchar_t ProductName[] = L"NevernessToEvernessLaunchBridge";
constexpr wchar_t Version[] = L"1.1.0";

std::wstring Trim(std::wstring value) {
    const auto first = value.find_first_not_of(L" \t\r\n");
    if (first == std::wstring::npos) return {};
    const auto last = value.find_last_not_of(L" \t\r\n");
    return value.substr(first, last - first + 1);
}

std::string Utf8(const std::wstring& value) {
    if (value.empty()) return {};
    const int size = WideCharToMultiByte(CP_UTF8, 0, value.data(), static_cast<int>(value.size()), nullptr, 0, nullptr, nullptr);
    if (size <= 0) throw std::runtime_error("WideCharToMultiByte failed");
    std::string result(static_cast<size_t>(size), '\0');
    WideCharToMultiByte(CP_UTF8, 0, value.data(), static_cast<int>(value.size()), result.data(), size, nullptr, nullptr);
    return result;
}

std::wstring Widen(const std::string& value) {
    if (value.empty()) return {};
    const int size = MultiByteToWideChar(CP_UTF8, 0, value.data(), static_cast<int>(value.size()), nullptr, 0);
    std::wstring result(static_cast<size_t>(size), L'\0');
    MultiByteToWideChar(CP_UTF8, 0, value.data(), static_cast<int>(value.size()), result.data(), size);
    return result;
}

std::wstring Win32Message(DWORD code = GetLastError()) {
    wchar_t* buffer = nullptr;
    const DWORD length = FormatMessageW(FORMAT_MESSAGE_ALLOCATE_BUFFER | FORMAT_MESSAGE_FROM_SYSTEM | FORMAT_MESSAGE_IGNORE_INSERTS,
        nullptr, code, 0, reinterpret_cast<wchar_t*>(&buffer), 0, nullptr);
    std::wstring message = length && buffer ? std::wstring(buffer, length) : L"Win32 error " + std::to_wstring(code);
    if (buffer) LocalFree(buffer);
    return Trim(message);
}

fs::path ExecutablePath() {
    std::wstring buffer(32768, L'\0');
    const DWORD length = GetModuleFileNameW(nullptr, buffer.data(), static_cast<DWORD>(buffer.size()));
    if (!length || length >= buffer.size()) throw std::runtime_error("GetModuleFileNameW failed");
    buffer.resize(length);
    return fs::path(buffer);
}

std::wstring Timestamp() {
    SYSTEMTIME time{};
    GetLocalTime(&time);
    wchar_t buffer[64]{};
    swprintf_s(buffer, L"%04u-%02u-%02uT%02u:%02u:%02u.%03u", time.wYear, time.wMonth, time.wDay,
        time.wHour, time.wMinute, time.wSecond, time.wMilliseconds);
    TIME_ZONE_INFORMATION zone{};
    const DWORD kind = GetTimeZoneInformation(&zone);
    LONG bias = zone.Bias + (kind == TIME_ZONE_ID_DAYLIGHT ? zone.DaylightBias : kind == TIME_ZONE_ID_STANDARD ? zone.StandardBias : 0);
    const wchar_t sign = bias <= 0 ? L'+' : L'-';
    bias = std::abs(bias);
    wchar_t suffix[16]{};
    swprintf_s(suffix, L"%c%02ld:%02ld", sign, bias / 60, bias % 60);
    return std::wstring(buffer) + suffix;
}

std::wstring CutoffDate() {
    FILETIME now{}, cutoff{}, local{};
    GetSystemTimeAsFileTime(&now);
    ULARGE_INTEGER value{now.dwLowDateTime, now.dwHighDateTime};
    value.QuadPart -= 7ULL * 24 * 60 * 60 * 10'000'000;
    cutoff.dwLowDateTime = value.LowPart;
    cutoff.dwHighDateTime = value.HighPart;
    FileTimeToLocalFileTime(&cutoff, &local);
    SYSTEMTIME time{};
    FileTimeToSystemTime(&local, &time);
    wchar_t result[16]{};
    swprintf_s(result, L"%04u-%02u-%02u", time.wYear, time.wMonth, time.wDay);
    return result;
}

std::wstring StackTrace() {
    static std::once_flag initialized;
    std::call_once(initialized, [] { SymInitialize(GetCurrentProcess(), nullptr, TRUE); });
    void* frames[48]{};
    const USHORT count = CaptureStackBackTrace(0, 48, frames, nullptr);
    std::wostringstream output;
    std::vector<unsigned char> storage(sizeof(SYMBOL_INFOW) + MAX_SYM_NAME * sizeof(wchar_t));
    auto* symbol = reinterpret_cast<SYMBOL_INFOW*>(storage.data());
    symbol->SizeOfStruct = sizeof(SYMBOL_INFOW);
    symbol->MaxNameLen = MAX_SYM_NAME;
    for (USHORT i = 0; i < count; ++i) {
        DWORD64 displacement = 0;
        const auto address = reinterpret_cast<DWORD64>(frames[i]);
        if (SymFromAddrW(GetCurrentProcess(), address, &displacement, symbol))
            output << L"\n  at " << symbol->Name << L"+0x" << std::hex << displacement;
        else output << L"\n  at 0x" << std::hex << address;
    }
    return output.str();
}

class BridgeError final : public std::runtime_error {
public:
    BridgeError(int code, const std::string& message) : std::runtime_error(message), code_(code) {}
    int code() const noexcept { return code_; }
private:
    int code_;
};

class Logger {
public:
    Logger() {
        path_ = ExecutablePath().parent_path() / L"NevernessToEvernessLaunchBridge-log" / L"NevernessToEvernessLaunchBridge.log";
        std::error_code error;
        fs::create_directories(path_.parent_path(), error);
        if (error) throw std::runtime_error("Unable to create log directory: " + error.message());
        const auto guid = GetTickCount64() ^ (static_cast<unsigned long long>(GetCurrentProcessId()) << 32);
        std::wostringstream id;
        id << std::hex << std::setw(12) << std::setfill(L'0') << guid;
        run_ = id.str();
        TrimOldEntries();
    }

    const fs::path& path() const { return path_; }
    void Info(const std::wstring& event, const std::wstring& message) { Write(L"INFO", event, message); }
    void Debug(const std::wstring& event, const std::wstring& message) { Write(L"DEBUG", event, message); }
    void Error(const std::wstring& event, const std::wstring& message) { Write(L"ERROR", event, message); }

private:
    fs::path path_;
    std::wstring run_;
    std::mutex mutex_;
    std::chrono::steady_clock::time_point lastTrim_{};

    void Write(const std::wstring& level, const std::wstring& event, std::wstring message) {
        std::scoped_lock lock(mutex_);
        if (std::chrono::steady_clock::now() - lastTrim_ > 1h) TrimOldEntries();
        std::replace(message.begin(), message.end(), L'\r', L' ');
        std::replace(message.begin(), message.end(), L'\n', L' ');
        std::ofstream stream(path_, std::ios::binary | std::ios::app);
        if (!stream) throw std::runtime_error("Unable to open log file");
        const std::wstring line = L"[" + Timestamp() + L"] [run=" + run_ + L" pid=" + std::to_wstring(GetCurrentProcessId()) +
            L"] [" + level + L"] [" + event + L"] " + message + L"\r\n";
        stream << Utf8(line);
        if (!stream) throw std::runtime_error("Unable to write log file");
    }

    void TrimOldEntries() {
        lastTrim_ = std::chrono::steady_clock::now();
        if (!fs::exists(path_)) return;
        std::ifstream input(path_, std::ios::binary);
        if (!input) return;
        const std::wstring cutoff = CutoffDate();
        std::string line;
        std::vector<std::string> kept;
        while (std::getline(input, line)) {
            const std::wstring wide = Widen(line);
            if (wide.size() < 12 || wide[0] != L'[' || wide.substr(1, 10) >= cutoff) kept.push_back(line);
        }
        input.close();
        std::ofstream output(path_, std::ios::binary | std::ios::trunc);
        for (const auto& item : kept) output << item << "\n";
    }
};

std::vector<std::wstring> Split(const std::wstring& value, wchar_t separator) {
    std::vector<std::wstring> result;
    std::wstringstream stream(value);
    std::wstring item;
    while (std::getline(stream, item, separator)) if (!(item = Trim(item)).empty()) result.push_back(item);
    return result;
}

std::wstring IniString(const fs::path& path, const wchar_t* section, const wchar_t* key, const wchar_t* fallback) {
    std::wstring buffer(32768, L'\0');
    const DWORD count = GetPrivateProfileStringW(section, key, fallback, buffer.data(), static_cast<DWORD>(buffer.size()), path.c_str());
    buffer.resize(count);
    return buffer;
}

struct Configuration {
    fs::path source;
    std::wstring installRoot;
    std::wstring launcherFile = L"NTEGame.exe";
    std::wstring gameFile = L"HTGame.exe";
    std::vector<std::wstring> launcherCandidates{L"NTELauncher/NTEGame.exe"};
    std::vector<std::wstring> gameCandidates{L"Client/WindowsNoEditor/HT/Binaries/Win64/HTGame.exe"};
    std::wstring directArguments = L"/launcher /directly";
    std::wstring launcherLog = L"NTELauncher/UserData/Log/NTEGame.log";
    std::vector<std::wstring> readyMarkers{L"_setLocalResChecked] 0->1", L"onGameElementUpdateFinished] all ready"};
    std::vector<std::wstring> excluded{L"Backup", L"Backups", L"Download", L"Downloads", L"Temp", L"Tmp", L"Cache", L"Caches", L"Patch", L"BinPatch", L"$RECYCLE.BIN"};
    std::vector<std::wstring> buttonNames{L"开始游戏", L"开始"};
    std::wstring triggerMode = L"accessibilityThenClick";
    int searchDepth = 12;
    int readyTimeout = 120;
    int gameTimeout = 120;
    int exitDebounce = 10;
    int pollMs = 250;
    int triggerDelayMs = 500;
    double clickX = 0.88;
    double clickY = 0.90;
    bool startHidden = true;
    bool suppressWindow = true;
    bool closeAfterGame = true;
    bool forceTerminateAfterTimeout = true;
    int gracefulCloseSeconds = 3;

    static Configuration Load(const std::optional<fs::path>& explicitPath) {
        Configuration value;
        const fs::path defaultPath = ExecutablePath().parent_path() / L"NevernessToEvernessLaunchBridge.ini";
        value.source = explicitPath.value_or(defaultPath);
        if (!fs::exists(value.source)) {
            if (explicitPath) throw BridgeError(3, "Configuration file does not exist");
            value.source.clear();
            return value;
        }
        value.installRoot = IniString(value.source, L"Paths", L"InstallRoot", L"");
        value.launcherFile = IniString(value.source, L"Paths", L"LauncherFileName", value.launcherFile.c_str());
        value.gameFile = IniString(value.source, L"Paths", L"GameFileName", value.gameFile.c_str());
        value.launcherCandidates = Split(IniString(value.source, L"Paths", L"LauncherCandidates", L"NTELauncher/NTEGame.exe"), L';');
        value.gameCandidates = Split(IniString(value.source, L"Paths", L"GameCandidates", L"Client/WindowsNoEditor/HT/Binaries/Win64/HTGame.exe"), L';');
        value.launcherLog = IniString(value.source, L"Paths", L"LauncherLog", value.launcherLog.c_str());
        value.excluded = Split(IniString(value.source, L"Discovery", L"ExcludedDirectories", L"Backup;Backups;Download;Downloads;Temp;Tmp;Cache;Caches;Patch;BinPatch;$RECYCLE.BIN"), L';');
        value.searchDepth = GetPrivateProfileIntW(L"Discovery", L"SearchDepth", value.searchDepth, value.source.c_str());
        value.directArguments = IniString(value.source, L"Launch", L"DirectArguments", value.directArguments.c_str());
        value.readyMarkers = Split(IniString(value.source, L"Launch", L"ReadyMarkers", L"_setLocalResChecked] 0->1;onGameElementUpdateFinished] all ready"), L';');
        value.triggerMode = IniString(value.source, L"Trigger", L"Mode", value.triggerMode.c_str());
        value.buttonNames = Split(IniString(value.source, L"Trigger", L"StartButtonNames", L"开始游戏;开始"), L';');
        value.clickX = std::stod(IniString(value.source, L"Trigger", L"FallbackClickXRatio", L"0.88"));
        value.clickY = std::stod(IniString(value.source, L"Trigger", L"FallbackClickYRatio", L"0.90"));
        value.startHidden = GetPrivateProfileIntW(L"LauncherWindow", L"StartHidden", 1, value.source.c_str()) != 0;
        value.suppressWindow = GetPrivateProfileIntW(L"LauncherWindow", L"SuppressWindow", 1, value.source.c_str()) != 0;
        value.closeAfterGame = GetPrivateProfileIntW(L"LauncherWindow", L"CloseAfterGame", 1, value.source.c_str()) != 0;
        value.forceTerminateAfterTimeout = GetPrivateProfileIntW(L"LauncherWindow", L"ForceTerminateAfterTimeout", 1, value.source.c_str()) != 0;
        value.gracefulCloseSeconds = GetPrivateProfileIntW(L"LauncherWindow", L"GracefulCloseSeconds", value.gracefulCloseSeconds, value.source.c_str());
        value.readyTimeout = GetPrivateProfileIntW(L"Timing", L"ReadyTimeoutSeconds", value.readyTimeout, value.source.c_str());
        value.gameTimeout = GetPrivateProfileIntW(L"Timing", L"GameStartupTimeoutSeconds", value.gameTimeout, value.source.c_str());
        value.exitDebounce = GetPrivateProfileIntW(L"Timing", L"ExitDebounceSeconds", value.exitDebounce, value.source.c_str());
        value.pollMs = GetPrivateProfileIntW(L"Timing", L"PollMilliseconds", value.pollMs, value.source.c_str());
        value.triggerDelayMs = GetPrivateProfileIntW(L"Timing", L"TriggerDelayMilliseconds", value.triggerDelayMs, value.source.c_str());
        if (value.searchDepth < 1 || value.searchDepth > 20 || value.readyTimeout < 10 || value.readyTimeout > 600 ||
            value.gameTimeout < 10 || value.gameTimeout > 600 || value.exitDebounce < 1 || value.exitDebounce > 60 ||
            value.pollMs < 100 || value.pollMs > 2000 || value.gracefulCloseSeconds < 1 || value.gracefulCloseSeconds > 30 ||
            value.clickX <= 0 || value.clickX >= 1 || value.clickY <= 0 || value.clickY >= 1)
            throw BridgeError(3, "Configuration values are outside allowed ranges");
        return value;
    }
};

struct Options {
    std::optional<fs::path> root;
    std::optional<fs::path> config;
    bool validate = false;
    bool noDialog = false;
    bool steamCommandTailIgnored = false;
    std::vector<std::wstring> ignoredArguments;
};

bool SensitiveArgumentName(std::wstring name) {
    std::transform(name.begin(), name.end(), name.begin(), ::towlower);
    static const std::vector<std::wstring> markers{
        L"token", L"cookie", L"password", L"passwd", L"secret", L"authorization",
        L"auth", L"session", L"ticket", L"api-key", L"apikey", L"credential"
    };
    return std::any_of(markers.begin(), markers.end(), [&](const std::wstring& marker) { return name.find(marker) != std::wstring::npos; });
}

std::wstring SanitizeIgnoredArgument(std::wstring argument, bool forceRedact = false) {
    if (argument.empty()) return L"<empty>";
    if (forceRedact) return L"<redacted>";
    const size_t separator = argument.find_first_of(L"=:");
    if (separator != std::wstring::npos) {
        const std::wstring name = argument.substr(0, separator);
        if (SensitiveArgumentName(name)) return name + argument.substr(separator, 1) + L"<redacted>";
    }
    std::wstring lower = argument;
    std::transform(lower.begin(), lower.end(), lower.begin(), ::towlower);
    static const std::vector<std::wstring> assignments{
        L"token=", L"cookie=", L"password=", L"passwd=", L"secret=", L"authorization=",
        L"auth=", L"session=", L"ticket=", L"api-key=", L"apikey=", L"credential="
    };
    if (std::any_of(assignments.begin(), assignments.end(), [&](const std::wstring& marker) { return lower.find(marker) != std::wstring::npos; }))
        return L"<redacted-argument>";
    std::replace(argument.begin(), argument.end(), L'\r', L' ');
    std::replace(argument.begin(), argument.end(), L'\n', L' ');
    if (argument.size() > 512) argument = argument.substr(0, 512) + L"...";
    return argument;
}

bool SensitiveSeparateValueOption(const std::wstring& argument) {
    return !argument.empty() && argument[0] == L'-' && argument.find_first_of(L"=:") == std::wstring::npos && SensitiveArgumentName(argument);
}

void RecordIgnoredTail(Options& options, wchar_t** argv, int first, int count) {
    bool redactNext = false;
    for (int i = first; i < count; ++i) {
        const std::wstring argument = argv[i];
        options.ignoredArguments.push_back(SanitizeIgnoredArgument(argument, redactNext));
        if (redactNext) redactNext = false;
        else redactNext = SensitiveSeparateValueOption(argument);
    }
}

std::wstring FormatIgnoredArguments(const Options& options) {
    std::wostringstream output;
    for (size_t i = 0; i < options.ignoredArguments.size(); ++i) {
        if (i) output << L" | ";
        output << options.ignoredArguments[i];
    }
    return output.str();
}

Options ParseOptions() {
    int count = 0;
    wchar_t** argv = CommandLineToArgvW(GetCommandLineW(), &count);
    if (!argv) throw BridgeError(3, "CommandLineToArgvW failed");
    Options result;
    try {
        for (int i = 1; i < count; ++i) {
            const std::wstring argument = argv[i];
            if (argument == L"--") {
                result.steamCommandTailIgnored = i + 1 < count;
                RecordIgnoredTail(result, argv, i + 1, count);
                break;
            }
            if (argument == L"--install-root") {
                if (i + 1 >= count) throw BridgeError(3, "Missing value for --install-root");
                result.root = fs::path(argv[++i]);
            }
            else if (argument == L"--config") {
                if (i + 1 >= count) throw BridgeError(3, "Missing value for --config");
                result.config = fs::path(argv[++i]);
            }
            else if (argument == L"--validate") result.validate = true;
            else if (argument == L"--no-dialog") result.noDialog = true;
            else if (argument.rfind(L"--", 0) == 0) {
                result.ignoredArguments.push_back(SanitizeIgnoredArgument(argument));
                if (SensitiveSeparateValueOption(argument) && i + 1 < count)
                    result.ignoredArguments.push_back(SanitizeIgnoredArgument(argv[++i], true));
            }
            else {
                // Steam expands %command% after bridge-owned options. The first
                // positional token is the original Steam command; ignore its
                // entire tail so it cannot affect bridge discovery or launching.
                result.steamCommandTailIgnored = true;
                RecordIgnoredTail(result, argv, i, count);
                break;
            }
        }
    } catch (...) { LocalFree(argv); throw; }
    LocalFree(argv);
    return result;
}

fs::path SafePath(const fs::path& root, const std::wstring& relative) {
    const fs::path candidate = fs::weakly_canonical(root / fs::path(relative));
    const fs::path rel = candidate.lexically_relative(root);
    if (rel.empty() || *rel.begin() == L"..") throw BridgeError(3, "Configured path leaves the installation root");
    return candidate;
}

bool NonemptyFile(const fs::path& path) {
    std::error_code error;
    return fs::is_regular_file(path, error) && fs::file_size(path, error) > 0 && !error;
}

std::optional<fs::path> FindPreferredExecutable(const fs::path& root, const std::vector<std::wstring>& preferred) {
    for (const auto& relative : preferred) {
        const fs::path candidate = SafePath(root, relative);
        if (NonemptyFile(candidate)) return fs::weakly_canonical(candidate);
    }
    return std::nullopt;
}

fs::path FindExecutable(const fs::path& root, const std::vector<std::wstring>& preferred, const std::wstring& fileName, const Configuration& config) {
    for (const auto& relative : preferred) {
        const fs::path candidate = SafePath(root, relative);
        if (NonemptyFile(candidate)) return candidate;
    }
    std::unordered_set<std::wstring> excluded;
    for (auto item : config.excluded) { std::transform(item.begin(), item.end(), item.begin(), ::towlower); excluded.insert(item); }
    std::queue<std::pair<fs::path, int>> queue;
    queue.emplace(root, 0);
    while (!queue.empty()) {
        auto [directory, depth] = queue.front(); queue.pop();
        std::error_code error;
        for (const auto& entry : fs::directory_iterator(directory, fs::directory_options::skip_permission_denied, error)) {
            if (error) break;
            if (entry.is_regular_file(error) && _wcsicmp(entry.path().filename().c_str(), fileName.c_str()) == 0 && NonemptyFile(entry.path()))
                return fs::weakly_canonical(entry.path());
            if (depth >= config.searchDepth || !entry.is_directory(error) || entry.is_symlink(error)) continue;
            std::wstring name = entry.path().filename().wstring();
            std::transform(name.begin(), name.end(), name.begin(), ::towlower);
            if (!excluded.contains(name)) queue.emplace(entry.path(), depth + 1);
        }
    }
    throw BridgeError(3, "Required executable was not found in the bounded installation scope");
}

struct Installation { fs::path root, launcher, game, launcherLog; };

Installation ResolveInstallation(const Options& options, const Configuration& config) {
    auto resolve = [&](const fs::path& root) -> Installation {
        const fs::path normalized = fs::weakly_canonical(root);
        if (!fs::is_directory(normalized)) throw BridgeError(3, "Installation root does not exist");
        return {normalized, FindExecutable(normalized, config.launcherCandidates, config.launcherFile, config),
            FindExecutable(normalized, config.gameCandidates, config.gameFile, config), SafePath(normalized, config.launcherLog)};
    };
    if (options.root) return resolve(*options.root);
    if (!config.installRoot.empty()) {
        fs::path configured(config.installRoot);
        if (configured.is_relative()) configured = (config.source.empty() ? ExecutablePath().parent_path() : config.source.parent_path()) / configured;
        return resolve(configured);
    }

    // A bridge copied anywhere below the installation root can locate the root
    // cheaply by matching configured relative candidates while walking upward.
    // This does not recursively scan broad parent directories or the whole drive.
    fs::path candidate = ExecutablePath().parent_path();
    while (true) {
        const fs::path normalized = fs::weakly_canonical(candidate);
        const auto launcher = FindPreferredExecutable(normalized, config.launcherCandidates);
        const auto game = FindPreferredExecutable(normalized, config.gameCandidates);
        if (launcher && game)
            return {normalized, *launcher, *game, SafePath(normalized, config.launcherLog)};
        const fs::path parent = normalized.parent_path();
        if (parent.empty() || parent == normalized) break;
        candidate = parent;
    }

    candidate = ExecutablePath().parent_path();
    for (int i = 0; i < 5; ++i) {
        try { return resolve(candidate); } catch (const BridgeError&) {}
        const fs::path parent = candidate.parent_path();
        if (parent.empty() || parent == candidate) break;
        candidate = parent;
    }
    throw BridgeError(3, "Unable to discover the NTE installation; use --install-root");
}

std::optional<fs::path> ProcessPath(DWORD pid) {
    HANDLE process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pid);
    if (!process) return std::nullopt;
    std::wstring buffer(32768, L'\0');
    DWORD size = static_cast<DWORD>(buffer.size());
    const BOOL ok = QueryFullProcessImageNameW(process, 0, buffer.data(), &size);
    CloseHandle(process);
    if (!ok) return std::nullopt;
    buffer.resize(size);
    return fs::path(buffer);
}

std::vector<DWORD> MatchingProcesses(const std::wstring& name, const fs::path& expected) {
    std::vector<DWORD> result;
    HANDLE snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
    if (snapshot == INVALID_HANDLE_VALUE) return result;
    PROCESSENTRY32W entry{sizeof(entry)};
    if (Process32FirstW(snapshot, &entry)) do {
        if (_wcsicmp(entry.szExeFile, name.c_str()) != 0) continue;
        const auto path = ProcessPath(entry.th32ProcessID);
        if (path && _wcsicmp(fs::weakly_canonical(*path).c_str(), fs::weakly_canonical(expected).c_str()) == 0) result.push_back(entry.th32ProcessID);
    } while (Process32NextW(snapshot, &entry));
    CloseHandle(snapshot);
    return result;
}

std::wstring QuoteArgument(const std::wstring& value) {
    if (value.find_first_of(L" \t\"") == std::wstring::npos) return value;
    std::wstring result = L"\"";
    size_t slashes = 0;
    for (const wchar_t ch : value) {
        if (ch == L'\\') { ++slashes; continue; }
        if (ch == L'\"') { result.append(slashes * 2 + 1, L'\\'); result += ch; slashes = 0; continue; }
        result.append(slashes, L'\\'); slashes = 0; result += ch;
    }
    result.append(slashes * 2, L'\\');
    return result + L"\"";
}

struct ProcessHandle {
    HANDLE process = nullptr;
    DWORD pid = 0;
    ProcessHandle() = default;
    ProcessHandle(HANDLE handle, DWORD id) : process(handle), pid(id) {}
    ProcessHandle(const ProcessHandle&) = delete;
    ProcessHandle& operator=(const ProcessHandle&) = delete;
    ProcessHandle(ProcessHandle&& other) noexcept : process(other.process), pid(other.pid) { other.process = nullptr; }
    ProcessHandle& operator=(ProcessHandle&& other) noexcept {
        if (this != &other) {
            if (process) CloseHandle(process);
            process = other.process;
            pid = other.pid;
            other.process = nullptr;
        }
        return *this;
    }
    ~ProcessHandle() { if (process) CloseHandle(process); }
};

ProcessHandle StartProcess(const fs::path& executable, const std::wstring& arguments, const fs::path& workingDirectory, bool startHidden) {
    std::wstring command = QuoteArgument(executable.wstring()) + (arguments.empty() ? L"" : L" " + arguments);
    STARTUPINFOW startup{sizeof(startup)};
    if (startHidden) {
        startup.dwFlags |= STARTF_USESHOWWINDOW;
        startup.wShowWindow = SW_HIDE;
    }
    PROCESS_INFORMATION info{};
    if (!CreateProcessW(executable.c_str(), command.data(), nullptr, nullptr, FALSE, 0, nullptr, workingDirectory.c_str(), &startup, &info))
        throw BridgeError(7, "CreateProcessW failed: " + Utf8(Win32Message()));
    CloseHandle(info.hThread);
    return ProcessHandle(info.hProcess, info.dwProcessId);
}

std::string ReadSharedFile(const fs::path& path) {
    HANDLE file = CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE) return {};
    LARGE_INTEGER size{};
    if (!GetFileSizeEx(file, &size) || size.QuadPart <= 0 || size.QuadPart > 16 * 1024 * 1024) { CloseHandle(file); return {}; }
    std::string data(static_cast<size_t>(size.QuadPart), '\0');
    DWORD read = 0;
    const BOOL ok = ReadFile(file, data.data(), static_cast<DWORD>(data.size()), &read, nullptr);
    CloseHandle(file);
    if (!ok) return {};
    data.resize(read);
    return data;
}

bool CurrentSessionReady(const fs::path& logPath, DWORD expectedPid, const Configuration& config, std::wstring& state) {
    const std::string content = ReadSharedFile(logPath);
    if (content.empty()) { state = L"log_missing_or_busy"; return false; }
    const size_t args = content.rfind("ARGS :");
    if (args == std::string::npos) { state = L"session_not_found"; return false; }
    const size_t headerStart = content.rfind("Log Init", args);
    if (headerStart == std::string::npos) { state = L"header_not_found"; return false; }
    const std::string header = content.substr(headerStart, args - headerStart + 128);
    const std::string pid1 = "pid=" + std::to_string(expectedPid);
    const std::string pid2 = "Process ID: " + std::to_string(expectedPid);
    if (header.find(pid1) == std::string::npos && header.find(pid2) == std::string::npos) { state = L"foreign_session"; return false; }
    if (header.find("/directly") == std::string::npos) { state = L"unexpected_arguments"; return false; }
    const std::string session = content.substr(args);
    for (const auto& marker : config.readyMarkers)
        if (session.find(Utf8(marker)) == std::string::npos) { state = L"marker_pending:" + marker; return false; }
    state = L"ready";
    return true;
}

struct WindowInfo { HWND handle = nullptr; DWORD pid = 0; std::wstring className; int width = 0; int height = 0; };

BOOL CALLBACK CollectWindows(HWND window, LPARAM parameter) {
    auto* context = reinterpret_cast<std::pair<const std::unordered_set<DWORD>*, std::vector<WindowInfo>*>*>(parameter);
    DWORD pid = 0; GetWindowThreadProcessId(window, &pid);
    if (!context->first->contains(pid)) return TRUE;
    RECT rect{}; if (!GetClientRect(window, &rect)) return TRUE;
    int width = rect.right - rect.left, height = rect.bottom - rect.top;
    if ((width < 300 || height < 200) && GetWindowRect(window, &rect)) {
        width = rect.right - rect.left;
        height = rect.bottom - rect.top;
    }
    if (width < 300 || height < 200) return TRUE;
    wchar_t className[256]{}; GetClassNameW(window, className, 256);
    if (wcsstr(className, L"QWindowOwnDC") == nullptr) return TRUE;
    context->second->push_back({window, pid, className, width, height});
    return TRUE;
}

WindowInfo WaitForWindow(const std::unordered_set<DWORD>& pids, int pollMs) {
    const auto deadline = std::chrono::steady_clock::now() + 30s;
    while (std::chrono::steady_clock::now() < deadline) {
        std::vector<WindowInfo> windows;
        std::pair context{&pids, &windows};
        EnumWindows(CollectWindows, reinterpret_cast<LPARAM>(&context));
        if (!windows.empty()) return *std::max_element(windows.begin(), windows.end(), [](const auto& a, const auto& b) { return a.width * a.height < b.width * b.height; });
        Sleep(pollMs);
    }
    throw BridgeError(8, "Unable to find the official launcher window");
}

// Outlives the suppressor, so recovery cannot race with the hiding thread.
class LauncherFailureRecovery {
public:
    LauncherFailureRecovery(DWORD pid, Logger& log) : pid_(pid), log_(log), exceptions_(std::uncaught_exceptions()) {}
    ~LauncherFailureRecovery() noexcept {
        if (std::uncaught_exceptions() <= exceptions_) return;
        try {
            std::unordered_set<DWORD> pids{pid_};
            std::vector<WindowInfo> windows;
            std::pair context{&pids, &windows};
            EnumWindows(CollectWindows, reinterpret_cast<LPARAM>(&context));
            for (const auto& window : windows) {
                ShowWindowAsync(window.handle, SW_RESTORE);
                ShowWindowAsync(window.handle, SW_SHOW);
            }
            log_.Info(L"launcher.failure_recovery", L"pid=" + std::to_wstring(pid_) +
                L" restoreRequested=" + std::to_wstring(windows.size()) + L" suppressionStopped=true; launcher retained for manual update/retry");
        } catch (...) {} // Preserve the original failure even if logging fails.
    }
private:
    DWORD pid_;
    Logger& log_;
    int exceptions_;
};

class WindowSuppressor {
public:
    WindowSuppressor(HWND window, bool enabled) : window_(window), enabled_(enabled) {
        if (!enabled_) return;
        ShowWindowAsync(window_, SW_HIDE);
        thread_ = std::jthread([this](std::stop_token stop) {
            while (!stop.stop_requested() && IsWindow(window_)) {
                if (IsWindowVisible(window_)) ShowWindowAsync(window_, SW_HIDE);
                std::this_thread::sleep_for(20ms);
            }
        });
    }
    ~WindowSuppressor() { Stop(); }
    void Stop() {
        if (thread_.joinable()) { thread_.request_stop(); thread_.join(); }
    }
private:
    HWND window_ = nullptr;
    bool enabled_ = false;
    std::jthread thread_;
};

void CloseStartedLauncher(const Configuration& config, const WindowInfo& window, ProcessHandle& launcher, Logger& log) {
    if (!config.closeAfterGame || !launcher.process) return;
    log.Info(L"launcher.close_requested", L"pid=" + std::to_wstring(launcher.pid) + L" gracefulTimeoutSeconds=" + std::to_wstring(config.gracefulCloseSeconds));
    PostMessageW(window.handle, WM_CLOSE, 0, 0);
    const DWORD result = WaitForSingleObject(launcher.process, static_cast<DWORD>(config.gracefulCloseSeconds * 1000));
    if (result == WAIT_OBJECT_0) {
        DWORD exitCode = 0;
        GetExitCodeProcess(launcher.process, &exitCode);
        log.Info(L"launcher.closed", L"mode=graceful pid=" + std::to_wstring(launcher.pid) + L" exitCode=" + std::to_wstring(exitCode));
        return;
    }
    if (!config.forceTerminateAfterTimeout) {
        log.Info(L"launcher.close_timeout", L"official launcher remained in tray; force termination disabled");
        return;
    }
    if (!TerminateProcess(launcher.process, 0)) throw BridgeError(20, "Unable to terminate the bridge-started launcher: " + Utf8(Win32Message()));
    WaitForSingleObject(launcher.process, 5000);
    log.Info(L"launcher.closed", L"mode=forced pid=" + std::to_wstring(launcher.pid));
}

bool NameMatches(IAccessible* accessible, VARIANT child, const std::vector<std::wstring>& names, std::wstring& matched) {
    BSTR text = nullptr;
    if (FAILED(accessible->get_accName(child, &text)) || !text) return false;
    const std::wstring actual(text, SysStringLen(text));
    SysFreeString(text);
    for (const auto& expected : names) if (_wcsicmp(actual.c_str(), expected.c_str()) == 0 || actual.find(expected) != std::wstring::npos) {
        matched = actual;
        return true;
    }
    return false;
}

bool InvokeAccessibleRecursive(IAccessible* accessible, const std::vector<std::wstring>& names, std::wstring& matched, int depth, int& visited) {
    if (!accessible || depth > 12 || visited++ > 2048) return false;
    VARIANT self{}; self.vt = VT_I4; self.lVal = CHILDID_SELF;
    if (NameMatches(accessible, self, names, matched) && SUCCEEDED(accessible->accDoDefaultAction(self))) return true;
    long count = 0;
    if (FAILED(accessible->get_accChildCount(&count)) || count <= 0) return false;
    std::vector<VARIANT> children(static_cast<size_t>(count));
    long obtained = 0;
    if (FAILED(AccessibleChildren(accessible, 0, count, children.data(), &obtained))) return false;
    bool found = false;
    for (long i = 0; i < obtained && !found; ++i) {
        VARIANT& child = children[static_cast<size_t>(i)];
        if (child.vt == VT_I4) found = NameMatches(accessible, child, names, matched) && SUCCEEDED(accessible->accDoDefaultAction(child));
        else if (child.vt == VT_DISPATCH && child.pdispVal) {
            IAccessible* nested = nullptr;
            if (SUCCEEDED(child.pdispVal->QueryInterface(IID_IAccessible, reinterpret_cast<void**>(&nested)))) {
                found = InvokeAccessibleRecursive(nested, names, matched, depth + 1, visited);
                nested->Release();
            }
        }
        VariantClear(&child);
    }
    return found;
}

bool InvokeAccessibleButton(HWND window, const std::vector<std::wstring>& names, std::wstring& matched, int& visited) {
    IAccessible* root = nullptr;
    if (FAILED(AccessibleObjectFromWindow(window, static_cast<DWORD>(OBJID_CLIENT), IID_IAccessible, reinterpret_cast<void**>(&root))) || !root) return false;
    const bool result = InvokeAccessibleRecursive(root, names, matched, 0, visited);
    root->Release();
    return result;
}

void ClickRelative(const WindowInfo& window, double xRatio, double yRatio) {
    const int x = std::clamp(static_cast<int>(std::lround(window.width * xRatio)), 0, window.width - 1);
    const int y = std::clamp(static_cast<int>(std::lround(window.height * yRatio)), 0, window.height - 1);
    const LPARAM point = MAKELPARAM(x, y);
    if (!PostMessageW(window.handle, WM_LBUTTONDOWN, MK_LBUTTON, point) || !PostMessageW(window.handle, WM_LBUTTONUP, 0, point))
        throw BridgeError(8, "Unable to send mouse messages to the launcher window");
}

std::vector<DWORD> WaitForGame(const Installation& installation, const std::unordered_set<DWORD>& baseline, int timeoutSeconds, int pollMs) {
    const auto deadline = std::chrono::steady_clock::now() + std::chrono::seconds(timeoutSeconds);
    while (std::chrono::steady_clock::now() < deadline) {
        auto ids = MatchingProcesses(L"HTGame.exe", installation.game);
        std::erase_if(ids, [&](DWORD id) { return baseline.contains(id); });
        if (!ids.empty()) return ids;
        Sleep(pollMs);
    }
    return {};
}

class InstanceLock {
public:
    explicit InstanceLock(const fs::path& root, Logger* log = nullptr) {
        std::hash<std::wstring> hash;
        const std::wstring name = L"Local\\NevernessToEvernessLaunchBridge-" + std::to_wstring(hash(fs::weakly_canonical(root).wstring()));
        HANDLE gate = CreateMutexW(nullptr, FALSE, (name + L"-restart").c_str());
        if (!gate) throw BridgeError(4, "Unable to restart the previous bridge instance");
        const DWORD gateWait = WaitForSingleObject(gate, 10000);
        if (gateWait != WAIT_OBJECT_0 && gateWait != WAIT_ABANDONED) {
            CloseHandle(gate);
            throw BridgeError(4, "Unable to restart the previous bridge instance");
        }
        try {
            if (log) RestartOthers(root, *log);
            mutex_ = CreateMutexW(nullptr, TRUE, name.c_str());
            acquired_ = mutex_ && GetLastError() != ERROR_ALREADY_EXISTS;
            if (mutex_ && !acquired_) {
                const DWORD result = WaitForSingleObject(mutex_, 5000);
                acquired_ = result == WAIT_OBJECT_0 || result == WAIT_ABANDONED;
            }
        } catch (...) { ReleaseMutex(gate); CloseHandle(gate); throw; }
        ReleaseMutex(gate);
        CloseHandle(gate);
    }
    ~InstanceLock() { if (mutex_) { if (acquired_) ReleaseMutex(mutex_); CloseHandle(mutex_); } }
    bool acquired() const { return acquired_; }
    static bool IsBridge(const fs::path& path) {
        DWORD ignored = 0;
        const DWORD size = GetFileVersionInfoSizeW(path.c_str(), &ignored);
        if (!size) return false;
        std::vector<BYTE> data(size);
        if (!GetFileVersionInfoW(path.c_str(), 0, size, data.data())) return false;
        wchar_t* product = nullptr; UINT length = 0;
        return VerQueryValueW(data.data(), L"\\StringFileInfo\\080404B0\\ProductName", reinterpret_cast<void**>(&product), &length)
            && length && wcscmp(product, ProductName) == 0;
    }
    static void RestartOthers(const fs::path& root, Logger& log) {
        HANDLE snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snapshot == INVALID_HANDLE_VALUE) throw BridgeError(4, "Unable to restart the previous bridge instance");
        std::vector<DWORD> candidates;
        PROCESSENTRY32W entry{sizeof(entry)};
        if (Process32FirstW(snapshot, &entry)) do {
            if (entry.th32ProcessID != GetCurrentProcessId()) candidates.push_back(entry.th32ProcessID);
        } while (Process32NextW(snapshot, &entry));
        CloseHandle(snapshot);
        for (DWORD pid : candidates) {
            ProcessHandle process(OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_TERMINATE | SYNCHRONIZE, FALSE, pid), pid);
            if (!process.process) continue;
            wchar_t buffer[32768]{}; DWORD length = 32768;
            if (!QueryFullProcessImageNameW(process.process, 0, buffer, &length)) continue;
            const auto path = fs::weakly_canonical(fs::path(buffer));
            const auto relative = path.lexically_relative(fs::weakly_canonical(root));
            const bool inside = !relative.empty() && *relative.begin() != L"..";
            if (!inside || !IsBridge(path)) continue;
            log.Info(L"bridge.restart_previous", L"pid=" + std::to_wstring(pid) + L" path=" + path.wstring());
            if (!TerminateProcess(process.process, 0) || WaitForSingleObject(process.process, 5000) != WAIT_OBJECT_0)
                throw BridgeError(4, "Unable to restart the previous bridge instance");
        }
    }
private: HANDLE mutex_ = nullptr; bool acquired_ = false;
};

int Run(const Options& options, const Configuration& config, const Installation& installation, Logger& log) {
    const auto bridgeStarted = std::chrono::steady_clock::now();
    InstanceLock lock(installation.root, options.validate ? nullptr : &log);
    if (!lock.acquired()) throw BridgeError(4, "Another bridge instance is already running for this installation");
    log.Info(L"bridge.start", L"version=" + std::wstring(Version) + L" root=" + installation.root.wstring() + L" launcher=" + installation.launcher.wstring() + L" game=" + installation.game.wstring() + (options.validate ? L" validate=true" : L" validate=false") + (options.steamCommandTailIgnored ? L" steamCommandTailIgnored=true" : L" steamCommandTailIgnored=false"));
    if (options.validate) { log.Info(L"bridge.validate_success", L"paths, configuration, logging and native prerequisites validated"); return 0; }
    if (!MatchingProcesses(L"HTGame.exe", installation.game).empty()) {
        log.Info(L"game.reattached", L"bridge restarted; tracking existing game path=" + installation.game.wstring());
        std::optional<std::chrono::steady_clock::time_point> absent;
        while (true) {
            if (!MatchingProcesses(L"HTGame.exe", installation.game).empty()) absent.reset();
            else if (!absent) absent = std::chrono::steady_clock::now();
            if (absent && std::chrono::steady_clock::now() - *absent >= std::chrono::seconds(config.exitDebounce)) break;
            Sleep(config.pollMs);
        }
        log.Info(L"bridge.complete", L"reattached game exited; exitCode=unavailable elapsedSeconds=" +
            std::to_wstring(std::chrono::duration_cast<std::chrono::seconds>(std::chrono::steady_clock::now() - bridgeStarted).count()));
        return 0;
    }

    auto launcherIds = MatchingProcesses(L"NTEGame.exe", installation.launcher);
    ProcessHandle started;
    DWORD expectedPid = 0;
    if (launcherIds.empty()) {
        started = StartProcess(installation.launcher, config.directArguments, installation.root, config.startHidden);
        expectedPid = started.pid;
        launcherIds.push_back(started.pid);
        log.Info(L"launcher.started", L"pid=" + std::to_wstring(started.pid) + L" arguments=" + config.directArguments + L" workingDirectory=" + installation.root.wstring());
    } else {
        expectedPid = launcherIds.front();
        log.Info(L"launcher.attached", L"pid=" + std::to_wstring(expectedPid));
    }

    LauncherFailureRecovery recovery(expectedPid, log);
    std::unordered_set<DWORD> launcherSet{expectedPid};
    const WindowInfo window = WaitForWindow(launcherSet, config.pollMs);
    WindowSuppressor suppressor(window.handle, config.suppressWindow);
    log.Info(L"launcher.window", L"pid=" + std::to_wstring(window.pid) + L" class=" + window.className + L" client=" + std::to_wstring(window.width) + L"x" + std::to_wstring(window.height) +
        L" hidden=" + (config.suppressWindow ? L"true" : L"false"));

    const auto readyDeadline = std::chrono::steady_clock::now() + std::chrono::seconds(config.readyTimeout);
    auto nextProgress = std::chrono::steady_clock::time_point{};
    while (std::chrono::steady_clock::now() < readyDeadline) {
        std::wstring state;
        if (CurrentSessionReady(installation.launcherLog, expectedPid, config, state)) { log.Info(L"resource.ready", L"pid=" + std::to_wstring(expectedPid) + L" log=" + installation.launcherLog.wstring()); break; }
        if (MatchingProcesses(L"NTEGame.exe", installation.launcher).empty()) throw BridgeError(7, "The official launcher exited while waiting for resource checks");
        if (std::chrono::steady_clock::now() >= nextProgress) { log.Debug(L"resource.waiting", L"pid=" + std::to_wstring(expectedPid) + L" state=" + state); nextProgress = std::chrono::steady_clock::now() + 10s; }
        Sleep(config.pollMs);
    }
    std::wstring finalState;
    if (!CurrentSessionReady(installation.launcherLog, expectedPid, config, finalState)) throw BridgeError(8, "Timed out waiting for the official resource check");
    Sleep(config.triggerDelayMs);

    const auto baselineVector = MatchingProcesses(L"HTGame.exe", installation.game);
    const std::unordered_set<DWORD> baseline(baselineVector.begin(), baselineVector.end());
    std::vector<DWORD> games;
    const bool accessibilityAllowed = _wcsicmp(config.triggerMode.c_str(), L"clickOnly") != 0;
    const bool clickAllowed = _wcsicmp(config.triggerMode.c_str(), L"accessibilityOnly") != 0;
    if (accessibilityAllowed) {
        std::wstring matched; int visited = 0;
        if (InvokeAccessibleButton(window.handle, config.buttonNames, matched, visited)) {
            log.Info(L"launcher.triggered", L"mode=MSAA button=" + matched + L" visited=" + std::to_wstring(visited));
            games = WaitForGame(installation, baseline, std::min(15, config.gameTimeout), config.pollMs);
        } else log.Debug(L"launcher.accessibility_unavailable", L"visited=" + std::to_wstring(visited));
    }
    if (games.empty() && clickAllowed) {
        ClickRelative(window, config.clickX, config.clickY);
        log.Info(L"launcher.triggered", L"mode=relative_click x=" + std::to_wstring(config.clickX) + L" y=" + std::to_wstring(config.clickY));
        games = WaitForGame(installation, baseline, std::max(10, config.gameTimeout - 15), config.pollMs);
    }
    if (games.empty()) throw BridgeError(8, "The game process did not appear after triggering the official launcher");
    log.Info(L"game.detected", L"pid=" + std::to_wstring(games.front()) + L" path=" + installation.game.wstring());
    HANDLE gameHandle = OpenProcess(SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION, FALSE, games.front());

    std::optional<std::chrono::steady_clock::time_point> absentSince;
    auto nextHeartbeat = std::chrono::steady_clock::time_point{};
    while (true) {
        auto current = MatchingProcesses(L"HTGame.exe", installation.game);
        std::erase_if(current, [&](DWORD id) { return baseline.contains(id); });
        if (!current.empty()) absentSince.reset(); else if (!absentSince) absentSince = std::chrono::steady_clock::now();
        if (absentSince && std::chrono::steady_clock::now() - *absentSince >= std::chrono::seconds(config.exitDebounce)) break;
        if (std::chrono::steady_clock::now() >= nextHeartbeat) { log.Debug(L"game.heartbeat", current.empty() ? L"game temporarily absent" : L"pid=" + std::to_wstring(current.front())); nextHeartbeat = std::chrono::steady_clock::now() + 60s; }
        Sleep(config.pollMs);
    }
    DWORD gameExitCode = STILL_ACTIVE;
    if (gameHandle) { GetExitCodeProcess(gameHandle, &gameExitCode); CloseHandle(gameHandle); }
    CloseStartedLauncher(config, window, started, log);
    suppressor.Stop();
    const auto elapsed = std::chrono::duration_cast<std::chrono::seconds>(std::chrono::steady_clock::now() - bridgeStarted).count();
    log.Info(L"bridge.complete", L"game exited; gameExitCode=" + (gameExitCode == STILL_ACTIVE ? L"unavailable" : std::to_wstring(gameExitCode)) +
        L" elapsedSeconds=" + std::to_wstring(elapsed) + L" debounceSeconds=" + std::to_wstring(config.exitDebounce));
    return 0;
}
std::wstring ChineseErrorHint(const std::string& message) {
    if (message.find("Unable to restart the previous bridge instance") != std::string::npos)
        return L"无法退出旧桥接器，请在任务管理器中结束旧桥接器后重试，并检查权限是否一致。";
    if (message.find("Timed out waiting for the official resource check") != std::string::npos)
        return L"等待官方资源检查超时，请检查是否已将游戏更新至最新版本。在官方启动器中完成更新后重试；若窗口未显示，可从系统托盘打开或退出后重开。";
    if (message.find("Another bridge instance") != std::string::npos)
        return L"此游戏已有桥接器正在运行，请等待启动完成或退出已有桥接器后重试，避免重复双击。";
    if (message.find("already running") != std::string::npos)
        return L"游戏已经运行，请切换到游戏窗口；重新启动前请先正常退出游戏。";
    if (message.find("resource checks") != std::string::npos)
        return L"等待资源检查时官方启动器已退出，请手动打开启动器检查更新及错误提示后重试。";
    if (message.find("launcher window") != std::string::npos || message.find("mouse messages") != std::string::npos)
        return L"无法找到或操作官方启动器窗口，请退出已有启动器后重试，并检查更新或弹窗。";
    if (message.find("game process did not appear") != std::string::npos)
        return L"已尝试开始游戏，但未检测到游戏进程，请在官方启动器中检查更新、登录或错误提示后重试。";
    if (message.find("log directory") != std::string::npos || message.find("log file") != std::string::npos)
        return L"日志写入失败，具体原因见上方英文信息。请检查所在目录的写入权限、磁盘空间及安全软件拦截。";
    if (message.find("Configuration") != std::string::npos || message.find("Configured path") != std::string::npos || message.find("Missing value") != std::string::npos)
        return L"配置或启动参数无效，请检查 INI 文件、参数值和路径范围。";
    if (message.find("installation") != std::string::npos || message.find("executable was not found") != std::string::npos)
        return L"未找到有效游戏安装，请将桥接器放在安装目录内，或用 --install-root 指定目录，并检查游戏文件完整性。";
    if (message.find("CreateProcessW") != std::string::npos)
        return L"无法启动官方启动器，请根据上方系统错误检查文件权限、安全软件拦截及游戏文件完整性。";
    if (message.find("terminate") != std::string::npos)
        return L"无法关闭本次启动的官方启动器，请从系统托盘正常退出后重试。";
    return L"桥接器运行失败，请根据上方英文信息和日志排查，可先手动打开官方启动器检查更新和错误提示。";
}
} // namespace

int WINAPI wWinMain(HINSTANCE, HINSTANCE, PWSTR, int) {
    std::optional<Logger> logger;
    Options options;
    try {
        options = ParseOptions();
        logger.emplace();
        if (!options.ignoredArguments.empty())
            logger->Info(L"arguments.ignored", L"count=" + std::to_wstring(options.ignoredArguments.size()) + L" values=" + FormatIgnoredArguments(options));
        const Configuration config = Configuration::Load(options.config);
        const Installation installation = ResolveInstallation(options, config);
        return Run(options, config, installation, *logger);
    } catch (const BridgeError& error) {
        const std::wstring summary = L"NTE bridge failed: " + Widen(error.what());
        try { if (logger) logger->Error(L"bridge.failure", summary + StackTrace()); } catch (...) {}
        const std::wstring dialog = summary + L"\n\n" + ChineseErrorHint(Utf8(summary)) + (logger ? L"\n\nLog: " + logger->path().wstring() : L"");
        if (!options.noDialog) MessageBoxW(nullptr, dialog.c_str(), ProductName, MB_OK | MB_ICONERROR);
        return error.code();
    } catch (const std::exception& error) {
        const std::wstring summary = L"NTE bridge failed: " + Widen(error.what());
        try { if (logger) logger->Error(L"bridge.failure", summary + StackTrace()); } catch (...) {}
        const std::wstring dialog = summary + L"\n\n" + ChineseErrorHint(Utf8(summary)) + (logger ? L"\n\nLog: " + logger->path().wstring() : L"");
        if (!options.noDialog) MessageBoxW(nullptr, dialog.c_str(), ProductName, MB_OK | MB_ICONERROR);
        return 20;
    } catch (...) {
        const std::wstring summary = L"NTE bridge failed: unknown native exception";
        try { if (logger) logger->Error(L"bridge.failure", summary + StackTrace()); } catch (...) {}
        const std::wstring dialog = summary + L"\n\n" + ChineseErrorHint(Utf8(summary)) + (logger ? L"\n\nLog: " + logger->path().wstring() : L"");
        if (!options.noDialog) MessageBoxW(nullptr, dialog.c_str(), ProductName, MB_OK | MB_ICONERROR);
        return 20;
    }
}
