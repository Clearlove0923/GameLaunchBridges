#include <windows.h>
#include <shellapi.h>
#include <tlhelp32.h>
#include <dbghelp.h>

#include <algorithm>
#include <chrono>
#include <filesystem>
#include <fstream>
#include <iomanip>
#include <mutex>
#include <optional>
#include <set>
#include <sstream>
#include <stdexcept>
#include <string>
#include <thread>
#include <unordered_set>
#include <vector>

namespace fs = std::filesystem;
using namespace std::chrono_literals;

namespace {
constexpr wchar_t ProductName[] = L"WhereWindsMeetLaunchBridge";
constexpr wchar_t DialogTitle[] = L"燕云十六声直启实验";
constexpr wchar_t Version[] = L"1.4.0";
constexpr auto PollInterval = 500ms;

std::wstring Trim(std::wstring value) {
    const auto first = value.find_first_not_of(L" \t\r\n");
    if (first == std::wstring::npos) return {};
    return value.substr(first, value.find_last_not_of(L" \t\r\n") - first + 1);
}

std::string Utf8(const std::wstring& value) {
    if (value.empty()) return {};
    const int size = WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, value.data(), static_cast<int>(value.size()), nullptr, 0, nullptr, nullptr);
    if (size <= 0) throw std::runtime_error("WideCharToMultiByte failed");
    std::string result(static_cast<size_t>(size), '\0');
    if (!WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, value.data(), static_cast<int>(value.size()), result.data(), size, nullptr, nullptr))
        throw std::runtime_error("WideCharToMultiByte failed");
    return result;
}

std::wstring Widen(const std::string& value) {
    if (value.empty()) return {};
    const int size = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, value.data(), static_cast<int>(value.size()), nullptr, 0);
    if (size <= 0) throw std::runtime_error("Invalid UTF-8 text");
    std::wstring result(static_cast<size_t>(size), L'\0');
    if (!MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, value.data(), static_cast<int>(value.size()), result.data(), size))
        throw std::runtime_error("Invalid UTF-8 text");
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
    TIME_ZONE_INFORMATION zone{};
    const DWORD kind = GetTimeZoneInformation(&zone);
    LONG bias = zone.Bias + (kind == TIME_ZONE_ID_DAYLIGHT ? zone.DaylightBias : kind == TIME_ZONE_ID_STANDARD ? zone.StandardBias : 0);
    const wchar_t sign = bias <= 0 ? L'+' : L'-';
    bias = std::abs(bias);
    wchar_t buffer[80]{};
    swprintf_s(buffer, L"%04u-%02u-%02uT%02u:%02u:%02u.%03u%c%02ld:%02ld", time.wYear, time.wMonth, time.wDay,
        time.wHour, time.wMinute, time.wSecond, time.wMilliseconds, sign, bias / 60, bias % 60);
    return buffer;
}

std::wstring OsDescription() {
    using RtlGetVersionType = LONG(WINAPI*)(PRTL_OSVERSIONINFOW);
    const HMODULE module = GetModuleHandleW(L"ntdll.dll");
    const auto rtlGetVersion = module ? reinterpret_cast<RtlGetVersionType>(GetProcAddress(module, "RtlGetVersion")) : nullptr;
    RTL_OSVERSIONINFOW version{sizeof(version)};
    if (!rtlGetVersion || rtlGetVersion(&version) != 0) return L"Windows (version unavailable)";
    return L"Windows " + std::to_wstring(version.dwMajorVersion) + L"." + std::to_wstring(version.dwMinorVersion) +
        L" build " + std::to_wstring(version.dwBuildNumber);
}

std::wstring StackTrace() {
    static std::once_flag initialized;
    std::call_once(initialized, [] { SymInitialize(GetCurrentProcess(), nullptr, TRUE); });
    void* frames[48]{};
    const USHORT count = CaptureStackBackTrace(1, 48, frames, nullptr);
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

class BridgeError : public std::runtime_error {
public:
    BridgeError(std::string type, std::string message, int code = 1)
        : std::runtime_error(std::move(message)), type_(std::move(type)), code_(code), stack_(StackTrace()) {}
    const std::string& type() const noexcept { return type_; }
    int code() const noexcept { return code_; }
    const std::wstring& stack() const noexcept { return stack_; }
private:
    std::string type_;
    int code_;
    std::wstring stack_;
};

class JsonParseError final : public BridgeError {
public: explicit JsonParseError(const std::string& message) : BridgeError("JsonParseError", message) {}
};

struct Configuration {
    std::vector<std::wstring> executableNames{L"yysls.exe"};
    std::wstring launchArguments = L"--launch-type=launcher";
    std::vector<std::wstring> excluded{L"Patch", L"BinPatch", L"Backup", L"Backups", L"Download", L"Downloads", L"Temp", L"Tmp", L"Cache", L"Caches"};
    std::vector<std::wstring> preferred{L"Win64r", L"Win64rh"};
    int maxSearchDepth = 12;
};

class JsonReader {
public:
    explicit JsonReader(std::wstring text) : text_(std::move(text)) {
        if (!text_.empty() && text_[0] == 0xfeff) position_ = 1;
    }
    Configuration ReadConfiguration() {
        Configuration config;
        bool executableNamesSet = false, excludedSet = false, preferredSet = false;
        Space(); Expect(L'{'); Space();
        if (Take(L'}')) { Finish(); return config; }
        while (true) {
            const std::wstring key = String();
            Space(); Expect(L':'); Space();
            if (Equal(key, L"executableNames")) {
                if (Null()) { /* default */ } else { config.executableNames = StringArray(); executableNamesSet = true; }
            } else if (Equal(key, L"launchArguments")) {
                if (!Null()) config.launchArguments = String();
            } else if (Equal(key, L"excludedDirectoryNames")) {
                if (!Null()) { config.excluded = StringArray(); excludedSet = true; }
            } else if (Equal(key, L"preferredPathKeywords")) {
                if (!Null()) { config.preferred = StringArray(); preferredSet = true; }
            } else if (Equal(key, L"maxSearchDepth")) {
                if (!Null()) config.maxSearchDepth = Integer();
            } else SkipValue();
            Space();
            if (Take(L'}')) break;
            Expect(L','); Space();
        }
        Finish();
        Normalize(config.executableNames);
        Normalize(config.excluded);
        Normalize(config.preferred);
        if (executableNamesSet && config.executableNames.empty()) config.executableNames = {L"yysls.exe"};
        if (!excludedSet) { /* defaults retained */ }
        if (!preferredSet) { /* defaults retained */ }
        if (config.maxSearchDepth < 1 || config.maxSearchDepth > 32)
            throw BridgeError("InvalidDataError", "maxSearchDepth must be between 1 and 32");
        for (const auto& name : config.executableNames) {
            if (name.size() < 4 || _wcsicmp(name.c_str() + name.size() - 4, L".exe") != 0 ||
                name.find_first_of(L"/\\") != std::wstring::npos)
                throw BridgeError("InvalidDataError", "executableNames may contain only EXE file names without paths");
        }
        return config;
    }
private:
    std::wstring text_;
    size_t position_ = 0;
    static bool Equal(const std::wstring& left, const wchar_t* right) { return _wcsicmp(left.c_str(), right) == 0; }
    [[noreturn]] void Fail(const char* message) const {
        throw JsonParseError(std::string(message) + " at character " + std::to_string(position_ + 1));
    }
    void Space() { while (position_ < text_.size() && iswspace(text_[position_])) ++position_; }
    bool Take(wchar_t expected) {
        if (position_ < text_.size() && text_[position_] == expected) { ++position_; return true; }
        return false;
    }
    void Expect(wchar_t expected) { if (!Take(expected)) Fail("Unexpected JSON token"); }
    bool Null() {
        if (text_.compare(position_, 4, L"null") == 0) { position_ += 4; return true; }
        return false;
    }
    std::wstring String() {
        Expect(L'\"');
        std::wstring result;
        while (position_ < text_.size()) {
            wchar_t ch = text_[position_++];
            if (ch == L'\"') return result;
            if (ch < 0x20) Fail("Control character in JSON string");
            if (ch != L'\\') { result.push_back(ch); continue; }
            if (position_ >= text_.size()) Fail("Incomplete JSON escape");
            ch = text_[position_++];
            switch (ch) {
                case L'\"': case L'\\': case L'/': result.push_back(ch); break;
                case L'b': result.push_back(L'\b'); break; case L'f': result.push_back(L'\f'); break;
                case L'n': result.push_back(L'\n'); break; case L'r': result.push_back(L'\r'); break; case L't': result.push_back(L'\t'); break;
                case L'u': {
                    if (position_ + 4 > text_.size()) Fail("Incomplete Unicode escape");
                    unsigned value = 0;
                    for (int i = 0; i < 4; ++i) {
                        const wchar_t hex = text_[position_++];
                        value <<= 4;
                        if (hex >= L'0' && hex <= L'9') value += hex - L'0';
                        else if (hex >= L'a' && hex <= L'f') value += hex - L'a' + 10;
                        else if (hex >= L'A' && hex <= L'F') value += hex - L'A' + 10;
                        else Fail("Invalid Unicode escape");
                    }
                    result.push_back(static_cast<wchar_t>(value));
                    break;
                }
                default: Fail("Invalid JSON escape");
            }
        }
        Fail("Unterminated JSON string");
    }
    int Integer() {
        const size_t start = position_;
        if (Take(L'-')) {}
        if (position_ >= text_.size() || !iswdigit(text_[position_])) Fail("Expected JSON integer");
        while (position_ < text_.size() && iswdigit(text_[position_])) ++position_;
        if (position_ < text_.size() && (text_[position_] == L'.' || text_[position_] == L'e' || text_[position_] == L'E'))
            Fail("Expected JSON integer");
        try { return std::stoi(text_.substr(start, position_ - start)); }
        catch (...) { Fail("JSON integer is outside the supported range"); }
    }
    std::vector<std::wstring> StringArray() {
        std::vector<std::wstring> result;
        Expect(L'['); Space();
        if (Take(L']')) return result;
        while (true) {
            result.push_back(String()); Space();
            if (Take(L']')) return result;
            Expect(L','); Space();
        }
    }
    void SkipValue() {
        Space();
        if (position_ >= text_.size()) Fail("Missing JSON value");
        if (text_[position_] == L'\"') { String(); return; }
        if (text_[position_] == L'{') {
            ++position_; Space(); if (Take(L'}')) return;
            while (true) { String(); Space(); Expect(L':'); SkipValue(); Space(); if (Take(L'}')) return; Expect(L','); Space(); }
        }
        if (text_[position_] == L'[') {
            ++position_; Space(); if (Take(L']')) return;
            while (true) { SkipValue(); Space(); if (Take(L']')) return; Expect(L','); Space(); }
        }
        const size_t start = position_;
        while (position_ < text_.size() && !iswspace(text_[position_]) && text_[position_] != L',' && text_[position_] != L'}' && text_[position_] != L']') ++position_;
        if (position_ == start) Fail("Invalid JSON value");
    }
    void Finish() { Space(); if (position_ != text_.size()) Fail("Trailing JSON content"); }
    static void Normalize(std::vector<std::wstring>& values) {
        std::vector<std::wstring> result;
        for (auto value : values) {
            value = Trim(std::move(value)); if (value.empty()) continue;
            if (std::none_of(result.begin(), result.end(), [&](const auto& old) { return _wcsicmp(old.c_str(), value.c_str()) == 0; }))
                result.push_back(std::move(value));
        }
        values = std::move(result);
    }
};

Configuration LoadConfiguration(const fs::path& path) {
    if (!fs::exists(path)) return {};
    std::ifstream input(path, std::ios::binary);
    if (!input) throw BridgeError("IOException", "Unable to read configuration file");
    std::string bytes((std::istreambuf_iterator<char>(input)), std::istreambuf_iterator<char>());
    try { return JsonReader(Widen(bytes)).ReadConfiguration(); }
    catch (const JsonParseError&) { throw; }
    catch (const BridgeError&) { throw; }
    catch (const std::exception& error) { throw JsonParseError(error.what()); }
}

class Logger {
public:
    Logger() {
        path_ = ExecutablePath().parent_path() / L"WhereWindsMeetLaunchBridge-log" / L"where-winds-meet-launch-bridge.log";
        std::error_code error;
        fs::create_directories(path_.parent_path(), error);
        if (error) throw BridgeError("IOException", "Unable to create log directory: " + error.message());
        std::wostringstream id;
        id << std::hex << std::setw(8) << std::setfill(L'0') << (GetTickCount64() ^ GetCurrentProcessId());
        session_ = id.str();
        Prune();
    }
    const fs::path& path() const { return path_; }
    void Info(const std::wstring& message) { Write(message); }
    void Error(const std::wstring& message) { Write(L"ERROR: " + message); }
    void Maintain() { if (std::chrono::steady_clock::now() - lastPrune_ >= 1h) Prune(); }
private:
    fs::path path_;
    std::wstring session_;
    std::mutex mutex_;
    std::chrono::steady_clock::time_point lastPrune_{};
    void Write(std::wstring message) {
        std::scoped_lock lock(mutex_);
        std::replace(message.begin(), message.end(), L'\r', L' ');
        std::replace(message.begin(), message.end(), L'\n', L' ');
        std::ofstream output(path_, std::ios::binary | std::ios::app);
        if (!output) throw BridgeError("IOException", "Unable to open log file");
        output << Utf8(L"[" + Timestamp() + L"] [session=" + session_ + L" pid=" + std::to_wstring(GetCurrentProcessId()) + L"] " + message + L"\r\n");
        if (!output) throw BridgeError("IOException", "Unable to write log file");
    }
    static std::optional<ULONGLONG> ParseTimestamp(const std::string& line) {
        if (line.size() < 30 || line[0] != '[') return std::nullopt;
        SYSTEMTIME time{}; int offsetHour = 0, offsetMinute = 0; char sign = '+';
        if (sscanf_s(line.c_str(), "[%hu-%hu-%huT%hu:%hu:%hu", &time.wYear, &time.wMonth, &time.wDay,
            &time.wHour, &time.wMinute, &time.wSecond) != 6) return std::nullopt;
        const size_t dot = line.find('.', 20);
        if (dot == std::string::npos) return std::nullopt;
        size_t zone = dot + 1;
        while (zone < line.size() && isdigit(static_cast<unsigned char>(line[zone]))) ++zone;
        if (zone == dot + 1 || zone + 5 >= line.size() || (line[zone] != '+' && line[zone] != '-')) return std::nullopt;
        const std::string fraction = line.substr(dot + 1, std::min<size_t>(3, zone - dot - 1));
        time.wMilliseconds = static_cast<WORD>(std::stoi(fraction) * (fraction.size() == 1 ? 100 : fraction.size() == 2 ? 10 : 1));
        if (sscanf_s(line.c_str() + zone, "%c%d:%d", &sign, 1, &offsetHour, &offsetMinute) != 3) return std::nullopt;
        FILETIME file{}; if (!SystemTimeToFileTime(&time, &file)) return std::nullopt;
        ULARGE_INTEGER value{file.dwLowDateTime, file.dwHighDateTime};
        const LONGLONG offset = static_cast<LONGLONG>(offsetHour * 60 + offsetMinute) * 60 * 10'000'000;
        value.QuadPart = sign == '+' ? value.QuadPart - offset : value.QuadPart + offset;
        return value.QuadPart;
    }
    void Prune() {
        lastPrune_ = std::chrono::steady_clock::now();
        if (!fs::exists(path_)) return;
        FILETIME now{}; GetSystemTimeAsFileTime(&now); ULARGE_INTEGER current{now.dwLowDateTime, now.dwHighDateTime};
        const ULONGLONG cutoff = current.QuadPart - 7ULL * 24 * 60 * 60 * 10'000'000;
        std::ifstream input(path_, std::ios::binary); if (!input) return;
        std::vector<std::string> kept; std::string line; size_t removed = 0;
        while (std::getline(input, line)) {
            const auto timestamp = ParseTimestamp(line);
            if (!timestamp || *timestamp >= cutoff) kept.push_back(line); else ++removed;
        }
        input.close(); if (!removed) return;
        const fs::path temporary = path_.wstring() + L".prune.tmp";
        std::ofstream output(temporary, std::ios::binary | std::ios::trunc);
        for (const auto& item : kept) output << item << "\n";
        output.close();
        std::error_code error; fs::rename(temporary, path_, error);
        if (error) { fs::remove(path_, error); error.clear(); fs::rename(temporary, path_, error); }
    }
};

struct Options {
    std::optional<fs::path> installRoot;
    std::optional<std::wstring> variant;
    std::vector<std::wstring> ignoredArguments;
    bool dryRun = false, noDialog = false, attachOnly = false, steamCommandTailIgnored = false;
    int exitDelaySeconds = 10, startupTimeoutSeconds = 180;
};

int ParseInteger(const wchar_t* value, int minimum, int maximum, const char* name) {
    wchar_t* end = nullptr; errno = 0; const long parsed = wcstol(value, &end, 10);
    if (errno || !end || *end || parsed < minimum || parsed > maximum) throw BridgeError("ArgumentError", std::string(name) + " is outside the allowed range");
    return static_cast<int>(parsed);
}

Options ParseOptions() {
    int count = 0; wchar_t** argv = CommandLineToArgvW(GetCommandLineW(), &count);
    if (!argv) throw BridgeError("Win32Error", "CommandLineToArgvW failed");
    Options result;
    try {
        for (int i = 1; i < count; ++i) {
            const std::wstring argument = argv[i];
            if (argument == L"--install-root") {
                if (i + 1 < count && std::wstring(argv[i + 1]).rfind(L"--", 0) != 0) result.installRoot = fs::path(argv[++i]);
                else result.ignoredArguments.push_back(argument);
            } else if (argument == L"--variant") {
                if (i + 1 >= count || std::wstring(argv[i + 1]).rfind(L"--", 0) == 0) result.ignoredArguments.push_back(argument);
                else {
                    const std::wstring value = argv[++i];
                    if (value.empty() || value.size() > 64 || value.find_first_of(L"/\\") != std::wstring::npos) {
                        result.ignoredArguments.push_back(argument);
                        result.ignoredArguments.push_back(value);
                    } else result.variant = value;
                }
            } else if (argument == L"--dry-run") result.dryRun = true;
            else if (argument == L"--no-dialog") result.noDialog = true;
            else if (argument == L"--attach-only") result.attachOnly = true;
            else if (argument == L"--exit-delay-seconds" || argument == L"--startup-timeout-seconds") {
                if (i + 1 >= count || std::wstring(argv[i + 1]).rfind(L"--", 0) == 0) result.ignoredArguments.push_back(argument);
                else {
                    const std::wstring value = argv[++i];
                    try {
                        if (argument == L"--exit-delay-seconds") result.exitDelaySeconds = ParseInteger(value.c_str(), 0, 300, "--exit-delay-seconds");
                        else result.startupTimeoutSeconds = ParseInteger(value.c_str(), 1, 1800, "--startup-timeout-seconds");
                    } catch (const BridgeError&) {
                        result.ignoredArguments.push_back(argument);
                        result.ignoredArguments.push_back(value);
                    }
                }
            } else if (argument.rfind(L"--", 0) == 0) result.ignoredArguments.push_back(argument);
            else {
                result.steamCommandTailIgnored = true;
                for (; i < count; ++i) result.ignoredArguments.emplace_back(argv[i]);
                break;
            }
        }
    } catch (...) { LocalFree(argv); throw; }
    LocalFree(argv); return result;
}

bool EqualPath(const fs::path& left, const fs::path& right) { return _wcsicmp(left.lexically_normal().c_str(), right.lexically_normal().c_str()) == 0; }
bool EqualText(const std::wstring& left, const std::wstring& right) { return _wcsicmp(left.c_str(), right.c_str()) == 0; }
bool HasSegment(const fs::path& path, const std::wstring& segment) {
    for (const auto& part : path) if (EqualText(part.wstring(), segment)) return true;
    return false;
}

std::vector<fs::path> FindExecutables(const fs::path& root, const Configuration& config, const std::optional<std::wstring>& variant, int maximumDepth = -1) {
    std::vector<fs::path> found;
    const int searchDepth = maximumDepth < 0 ? config.maxSearchDepth : maximumDepth;
    std::vector<std::pair<fs::path, int>> pending{{root, 0}};
    while (!pending.empty()) {
        auto [directory, depth] = pending.back(); pending.pop_back();
        for (const auto& name : config.executableNames) {
            const fs::path candidate = directory / name; std::error_code error;
            if (fs::is_regular_file(candidate, error) && (!variant || HasSegment(candidate, *variant))) found.push_back(fs::absolute(candidate).lexically_normal());
        }
        if (depth >= searchDepth) continue;
        std::error_code error;
        for (const auto& entry : fs::directory_iterator(directory, fs::directory_options::skip_permission_denied, error)) {
            if (error) break;
            if (!entry.is_directory(error) || entry.is_symlink(error)) continue;
            const auto name = entry.path().filename().wstring();
            if (EqualText(name, L"WhereWindsMeetLaunchBridge-log") ||
                std::any_of(config.excluded.begin(), config.excluded.end(), [&](const auto& item) { return EqualText(item, name); })) continue;
            pending.emplace_back(entry.path(), depth + 1);
        }
    }
    auto score = [&](const fs::path& path) {
        const fs::path relative = path.lexically_relative(root); int depth = 0; for ([[maybe_unused]] const auto& part : relative) ++depth;
        int value = depth * 10;
        for (size_t i = 0; i < config.executableNames.size(); ++i) if (EqualText(path.filename().wstring(), config.executableNames[i])) { value += static_cast<int>(i) * 100; break; }
        for (size_t i = 0; i < config.preferred.size(); ++i) if (HasSegment(path, config.preferred[i])) { value -= 1000 - static_cast<int>(i) * 10; break; }
        return value;
    };
    std::sort(found.begin(), found.end(), [&](const auto& a, const auto& b) { const int sa = score(a), sb = score(b); return sa != sb ? sa < sb : _wcsicmp(a.c_str(), b.c_str()) < 0; });
    found.erase(std::unique(found.begin(), found.end(), EqualPath), found.end());
    return found;
}

struct InstallationDiscovery {
    fs::path root;
    std::vector<fs::path> executables;
    std::vector<fs::path> searchedScopes;
};

InstallationDiscovery DiscoverInstallation(const Options& options, const Configuration& config) {
    if (options.installRoot) {
        const fs::path root = fs::absolute(*options.installRoot).lexically_normal();
        if (!fs::is_directory(root)) throw BridgeError("DirectoryNotFoundError", "Game installation directory does not exist: " + Utf8(root.wstring()));
        return {root, FindExecutables(root, config, std::nullopt), {root}};
    }

    fs::path scope = fs::absolute(ExecutablePath().parent_path()).lexically_normal();
    std::vector<fs::path> searched;
    constexpr int MaximumAncestorScopes = 32;
    for (int ascent = 0; ascent < MaximumAncestorScopes; ++ascent) {
        searched.push_back(scope);
        auto found = FindExecutables(scope, config, std::nullopt, config.maxSearchDepth);
        if (!found.empty()) return {scope, std::move(found), std::move(searched)};
        const fs::path parent = scope.parent_path();
        if (parent.empty() || parent == scope || parent == parent.root_path()) break;
        scope = parent;
    }
    return {ExecutablePath().parent_path(), {}, std::move(searched)};
}

std::optional<fs::path> ProcessPath(DWORD pid) {
    HANDLE process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pid); if (!process) return std::nullopt;
    std::wstring buffer(32768, L'\0'); DWORD size = static_cast<DWORD>(buffer.size());
    const BOOL ok = QueryFullProcessImageNameW(process, 0, buffer.data(), &size); CloseHandle(process);
    if (!ok) return std::nullopt; buffer.resize(size); return fs::path(buffer).lexically_normal();
}

struct ProcessScan { std::vector<DWORD> ids; int unreadable = 0; };
ProcessScan FindProcesses(const std::vector<fs::path>& expected) {
    ProcessScan result; std::set<std::wstring> names;
    for (const auto& path : expected) { auto name = path.stem().wstring(); std::transform(name.begin(), name.end(), name.begin(), towlower); names.insert(name); }
    HANDLE snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0); if (snapshot == INVALID_HANDLE_VALUE) return result;
    PROCESSENTRY32W entry{sizeof(entry)};
    if (Process32FirstW(snapshot, &entry)) do {
        std::wstring name = fs::path(entry.szExeFile).stem().wstring(); std::transform(name.begin(), name.end(), name.begin(), towlower);
        if (!names.contains(name)) continue;
        const auto path = ProcessPath(entry.th32ProcessID);
        if (!path) { ++result.unreadable; continue; }
        if (std::any_of(expected.begin(), expected.end(), [&](const auto& item) { return EqualPath(item, *path); })) result.ids.push_back(entry.th32ProcessID);
    } while (Process32NextW(snapshot, &entry));
    CloseHandle(snapshot); std::sort(result.ids.begin(), result.ids.end()); return result;
}

class ProcessHandle {
public:
    ProcessHandle() = default; explicit ProcessHandle(HANDLE handle) : handle_(handle) {}
    ~ProcessHandle() { if (handle_) CloseHandle(handle_); }
    ProcessHandle(const ProcessHandle&) = delete; ProcessHandle& operator=(const ProcessHandle&) = delete;
    ProcessHandle(ProcessHandle&& other) noexcept : handle_(other.handle_) { other.handle_ = nullptr; }
    bool Alive() const { return handle_ && WaitForSingleObject(handle_, 0) == WAIT_TIMEOUT; }
    DWORD ExitCode() const { DWORD value = 0; return handle_ && GetExitCodeProcess(handle_, &value) ? value : 0; }
private: HANDLE handle_ = nullptr;
};

ProcessHandle StartGame(const fs::path& executable, const std::wstring& arguments) {
    SHELLEXECUTEINFOW info{sizeof(info)};
    info.fMask = SEE_MASK_NOCLOSEPROCESS | SEE_MASK_FLAG_NO_UI;
    info.lpVerb = L"runas"; info.lpFile = executable.c_str(); info.lpParameters = arguments.c_str();
    const std::wstring directory = executable.parent_path().wstring(); info.lpDirectory = directory.c_str(); info.nShow = SW_SHOWNORMAL;
    if (!ShellExecuteExW(&info)) throw BridgeError("Win32Error", "Unable to start game: " + Utf8(Win32Message()), GetLastError() == ERROR_CANCELLED ? 2 : 1);
    if (!info.hProcess) throw BridgeError("Win32Error", "Windows did not return a game process handle");
    return ProcessHandle(info.hProcess);
}

std::wstring Join(const std::vector<fs::path>& paths) { std::wstring result; for (const auto& path : paths) { if (!result.empty()) result += L"; "; result += path.wstring(); } return result; }
std::wstring JoinIds(const std::vector<DWORD>& ids) { std::wstring result; for (DWORD id : ids) { if (!result.empty()) result += L","; result += std::to_wstring(id); } return result; }
std::wstring JoinStrings(const std::vector<std::wstring>& values) { std::wstring result; for (const auto& value : values) { if (!result.empty()) result += L","; result += value; } return result; }
std::wstring Bool(bool value) { return value ? L"True" : L"False"; }

bool IsSensitiveArgumentName(const std::wstring& argument) {
    if (argument.empty() || (argument[0] != L'-' && argument[0] != L'/')) return false;
    std::wstring lower = argument; std::transform(lower.begin(), lower.end(), lower.begin(), towlower);
    for (const wchar_t* sensitive : {L"token", L"cookie", L"password", L"passwd", L"secret", L"session", L"key"}) {
        if (lower.find(sensitive) != std::wstring::npos) return true;
    }
    return false;
}

std::wstring JoinArgumentsForLog(const std::vector<std::wstring>& values) {
    std::wstring result;
    bool redactNext = false;
    for (const auto& value : values) {
        if (!result.empty()) result += L" | ";
        if (redactNext) { result += L"<redacted>"; redactNext = false; continue; }
        if (IsSensitiveArgumentName(value)) {
            const size_t equals = value.find(L'=');
            result += equals == std::wstring::npos ? value : value.substr(0, equals + 1) + L"<redacted>";
            redactNext = equals == std::wstring::npos;
        } else result += value;
    }
    return result.empty() ? L"<none>" : result;
}

void WriteConsoleUtf8(DWORD stream, const std::wstring& text) {
    const std::string bytes = Utf8(text); HANDLE handle = GetStdHandle(stream); if (!handle || handle == INVALID_HANDLE_VALUE) return;
    DWORD written = 0; WriteFile(handle, bytes.data(), static_cast<DWORD>(bytes.size()), &written, nullptr);
}

int Run(const Options& options, Logger& log) {
    const auto started = std::chrono::steady_clock::now();
    log.Info(L"Bridge started: version=" + std::wstring(Version) + L", OS=" + OsDescription() + L", architecture=x64, executable=" + ExecutablePath().wstring());
    log.Info(L"Options: installRoot=" + (options.installRoot ? options.installRoot->wstring() : L"<exe directory>") + L", dryRun=" + Bool(options.dryRun) +
        L", attachOnly=" + Bool(options.attachOnly) + L", exitDelay=" + std::to_wstring(options.exitDelaySeconds) + L"s, startupTimeout=" +
        std::to_wstring(options.startupTimeoutSeconds) + L"s, variant=" + (options.variant ? *options.variant : L"auto") +
        L", steamCommandTailIgnored=" + Bool(options.steamCommandTailIgnored) + L", ignoredArguments=" + JoinArgumentsForLog(options.ignoredArguments));
    const fs::path configPath = ExecutablePath().parent_path() / L"WhereWindsMeetLaunchBridge.json";
    const Configuration config = LoadConfiguration(configPath);
    log.Info(L"Configuration: path=" + configPath.wstring() + L", exists=" + Bool(fs::exists(configPath)) + L", executableNames=" + JoinStrings(config.executableNames) +
        L", launchArguments=" + config.launchArguments + L", maxSearchDepth=" + std::to_wstring(config.maxSearchDepth));
    const InstallationDiscovery discovery = DiscoverInstallation(options, config);
    const fs::path root = discovery.root;
    log.Info(L"Discovery scopes: " + Join(discovery.searchedScopes));
    log.Info(L"Install root: " + root.wstring());
    const auto& allPaths = discovery.executables;
    log.Info(L"Tracked game paths: " + Join(allPaths));
    for (const auto& path : allPaths) {
        std::error_code error; const auto size = fs::file_size(path, error);
        log.Info(L"Candidate executable: path=" + path.wstring() + (error ? L"" : L", size=" + std::to_wstring(size)));
    }
    if (allPaths.empty()) throw BridgeError("FileNotFoundError", "Game executable was not found in the bounded installation scope");
    std::vector<fs::path> selectedPaths = allPaths;
    if (options.variant) std::erase_if(selectedPaths, [&](const auto& path) { return !HasSegment(path, *options.variant); });
    if (selectedPaths.empty()) throw BridgeError("FileNotFoundError", "No game executable matched --variant");
    const fs::path selected = selectedPaths.front();
    if (options.dryRun) {
        log.Info(L"Dry run command: \"" + selected.wstring() + L"\" " + config.launchArguments + L"; workingDirectory=" + selected.parent_path().wstring());
        WriteConsoleUtf8(STD_OUTPUT_HANDLE, L"安装目录: " + root.wstring() + L"\r\n退出延时: " + std::to_wstring(options.exitDelaySeconds) +
            L" 秒\r\n仅跟踪已启动游戏: " + Bool(options.attachOnly) + L"\r\n" + (options.attachOnly ? L"" : L"工作目录: " + selected.parent_path().wstring() +
            L"\r\n启动命令: \"" + selected.wstring() + L"\" " + config.launchArguments + L"\r\n"));
        log.Info(L"Dry run completed without starting the game."); return 0;
    }
    ProcessScan scan = FindProcesses(allPaths);
    log.Info(L"Initial game scan: matchingPids=" + JoinIds(scan.ids) + L", unreadableProcesses=" + std::to_wstring(scan.unreadable));
    if (scan.ids.empty() && scan.unreadable > 0) throw BridgeError("AccessDeniedError", "Detected yysls.exe but could not verify its full path; run the bridge as administrator");
    std::optional<ProcessHandle> launched;
    if (scan.ids.empty() && !options.attachOnly) {
        log.Info(L"Starting game: \"" + selected.wstring() + L"\" " + config.launchArguments + L"; workingDirectory=" + selected.parent_path().wstring());
        launched.emplace(StartGame(selected, config.launchArguments));
    } else if (!scan.ids.empty()) log.Info(L"Attached to running game: pids=" + JoinIds(scan.ids));
    else log.Info(L"Waiting for official game start: timeout=" + std::to_wstring(options.startupTimeoutSeconds) + L"s");
    bool gameSeen = !scan.ids.empty() || launched.has_value(); std::wstring previous = JoinIds(scan.ids);
    std::optional<std::chrono::steady_clock::time_point> absentSince;
    while (true) {
        scan = FindProcesses(allPaths); const std::wstring current = JoinIds(scan.ids);
        if (current != previous) { log.Info(L"Game process change: previousPids=" + previous + L", currentPids=" + current + L", unreadableProcesses=" + std::to_wstring(scan.unreadable)); previous = current; }
        const bool running = !scan.ids.empty() || (launched && launched->Alive());
        if (running) { if (!gameSeen) log.Info(L"Game appeared: pids=" + current); gameSeen = true; absentSince.reset(); }
        else if (!gameSeen) {
            if (std::chrono::steady_clock::now() - started >= std::chrono::seconds(options.startupTimeoutSeconds)) throw BridgeError("TimeoutError", "Timed out waiting for the official game process");
        } else {
            if (!absentSince) { absentSince = std::chrono::steady_clock::now(); log.Info(L"Game process no longer detected; waiting for exit delay."); }
            if (std::chrono::steady_clock::now() - *absentSince >= std::chrono::seconds(options.exitDelaySeconds)) break;
        }
        log.Maintain(); std::this_thread::sleep_for(PollInterval);
    }
    const DWORD exitCode = launched ? launched->ExitCode() : 0;
    const auto elapsed = std::chrono::duration_cast<std::chrono::seconds>(std::chrono::steady_clock::now() - started).count();
    log.Info(L"Bridge completed: gameExitCode=" + std::to_wstring(exitCode) + L", bridgeElapsed=" + std::to_wstring(elapsed) + L"s, delay=" + std::to_wstring(options.exitDelaySeconds) + L"s");
    return static_cast<int>(exitCode);
}
} // namespace

int WINAPI wWinMain(HINSTANCE, HINSTANCE, PWSTR, int) {
    Options options; std::optional<Logger> logger;
    try {
        options = ParseOptions(); logger.emplace(); return Run(options, *logger);
    } catch (const BridgeError& error) {
        const std::wstring summary = Widen(error.type()) + L": " + Widen(error.what());
        try { if (logger) logger->Error(L"exception=" + summary + L" stack:" + error.stack()); } catch (...) {}
        std::wstring message = L"无法启动游戏：" + Widen(error.what());
        if (logger) message += L"\n\n日志：" + logger->path().wstring(); else message += L"\n\n日志写入失败：无法创建或写入日志目录。";
        if (!options.noDialog) MessageBoxW(nullptr, message.c_str(), DialogTitle, MB_OK | MB_ICONERROR);
        else WriteConsoleUtf8(STD_ERROR_HANDLE, message + L"\r\n");
        return error.code();
    } catch (const std::exception& error) {
        const std::wstring stack = StackTrace();
        try { if (logger) logger->Error(L"exception=std::exception message=" + Widen(error.what()) + L" stack:" + stack); } catch (...) {}
        const std::wstring message = L"无法启动游戏：" + Widen(error.what()) + (logger ? L"\n\n日志：" + logger->path().wstring() : L"\n\n日志写入失败：无法创建或写入日志目录。");
        if (!options.noDialog) MessageBoxW(nullptr, message.c_str(), DialogTitle, MB_OK | MB_ICONERROR); else WriteConsoleUtf8(STD_ERROR_HANDLE, message + L"\r\n");
        return 1;
    }
}
