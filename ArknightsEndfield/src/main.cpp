#include <windows.h>
#include <shellapi.h>
#include <tlhelp32.h>
#include <shlobj.h>
#include <regex>
#include <map>
#include <memory>
#include <functional>
#include <typeinfo>
#include <cerrno>
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
constexpr wchar_t ProductName[] = L"ArknightsEndfieldLaunchBridge";
constexpr wchar_t Version[] = L"1.0.0";

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

ULONGLONG NowTicks() {FILETIME t{};GetSystemTimeAsFileTime(&t);ULARGE_INTEGER n{t.dwLowDateTime,t.dwHighDateTime};return n.QuadPart;}
std::optional<ULONGLONG> IsoTicks(const std::wstring& line) {
    unsigned y=0,m=0,d=0,h=0,n=0,sec=0,ms=0,zh=0,zm=0; wchar_t sign=0;
    if(swscanf_s(line.c_str(),L"[%u-%u-%uT%u:%u:%u.%u%c%u:%u]",&y,&m,&d,&h,&n,&sec,&ms,&sign,1,&zh,&zm)!=10)return {};
    if((sign!=L'+'&&sign!=L'-')||zh>23||zm>59||y>9999||m>12||d>31||h>23||n>59||sec>59||ms>999)return {};
    SYSTEMTIME st{};st.wYear=WORD(y);st.wMonth=WORD(m);st.wDay=WORD(d);st.wHour=WORD(h);st.wMinute=WORD(n);st.wSecond=WORD(sec);st.wMilliseconds=WORD(ms);
    FILETIME ft{};if(!SystemTimeToFileTime(&st,&ft))return {};
    ULARGE_INTEGER ticks{ft.dwLowDateTime,ft.dwHighDateTime};
    const auto offset=(zh*60ULL+zm)*60*10'000'000;
    return sign==L'+'?ticks.QuadPart-offset:ticks.QuadPart+offset;
}

std::wstring StackTrace() {
    static std::mutex symbolsMutex;
    std::scoped_lock symbolsLock(symbolsMutex);
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
    const std::wstring stack=StackTrace();
private:
    int code_;
};

class LogFileLock {
    HANDLE handle_=nullptr;
public:
    explicit LogFileLock(const fs::path& path) {
        const auto key=L"Local\\ArknightsEndfieldLog-"+std::to_wstring(std::hash<std::wstring>{}(path.wstring()));
        handle_=CreateMutexW(nullptr,FALSE,key.c_str());
        if(!handle_)throw std::runtime_error("Unable to create log mutex: "+Utf8(Win32Message()));
        const auto wait=WaitForSingleObject(handle_,10000);
        if(wait!=WAIT_OBJECT_0&&wait!=WAIT_ABANDONED){CloseHandle(handle_);handle_=nullptr;throw std::runtime_error("Log mutex wait failed");}
    }
    ~LogFileLock(){if(handle_){ReleaseMutex(handle_);CloseHandle(handle_);}}
};
class Logger {
public:
    Logger() {
        path_ = ExecutablePath().parent_path() / L"ArknightsEndfieldLaunchBridge-log" / L"ArknightsEndfieldLaunchBridge.log";
        std::error_code error;
        fs::create_directories(path_.parent_path(), error);
        if (error) throw std::runtime_error("Unable to create log directory: " + error.message());
        const auto guid = GetTickCount64() ^ (static_cast<unsigned long long>(GetCurrentProcessId()) << 32);
        std::wostringstream id;
        id << std::hex << std::setw(12) << std::setfill(L'0') << guid;
        run_ = id.str();
        LogFileLock fileLock(path_);TrimOldEntries();
    }

    const fs::path& path() const { return path_; }
    void Maintain(){std::scoped_lock lock(mutex_);if(std::chrono::steady_clock::now()-lastTrim_>1h){LogFileLock fileLock(path_);TrimOldEntries();}}
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
        LogFileLock fileLock(path_);
        if (std::chrono::steady_clock::now() - lastTrim_ > 1h) TrimOldEntries();
        std::replace(message.begin(), message.end(), L'\r', L' ');
        std::replace(message.begin(), message.end(), L'\n', L' ');
        std::ofstream stream(path_, std::ios::binary | std::ios::app);
        if (!stream) throw std::runtime_error("Unable to open log file: errno="+std::to_string(errno));
        const std::wstring line = L"[" + Timestamp() + L"] [run=" + run_ + L" pid=" + std::to_wstring(GetCurrentProcessId()) +
            L"] [" + level + L"] [" + event + L"] " + message + L"\r\n";
        stream << Utf8(line); stream.flush();
        if (!stream) throw std::runtime_error("Unable to write log file: errno="+std::to_string(errno));
    }

    void TrimOldEntries() {
        lastTrim_ = std::chrono::steady_clock::now();
        if (!fs::exists(path_)) return;
        std::ifstream input(path_, std::ios::binary);
        if (!input) throw std::runtime_error("Unable to read log for retention: errno="+std::to_string(errno));
        const auto cutoff = NowTicks()-7ULL*24*60*60*10'000'000;
        bool keep=true;
        std::string line;
        std::vector<std::string> kept;
        while (std::getline(input, line)) {
            const std::wstring wide = Widen(line);
            if(const auto stamp=IsoTicks(wide)) keep=*stamp>=cutoff;
            if(keep)kept.push_back(line);
        }
        if(input.bad())throw std::runtime_error("Unable to read complete log for retention");
        input.close();
        std::ofstream output(path_, std::ios::binary | std::ios::trunc);
        if(!output)throw std::runtime_error("Unable to rewrite log for retention: errno="+std::to_string(errno));
        for (const auto& item : kept) output << item << "\n";
        output.flush();if(!output)throw std::runtime_error("Unable to flush log retention: errno="+std::to_string(errno));
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
    std::wstring gameFile=L"Endfield.exe", launcherFile=L"Launcher.exe", runtimeFile=L"Games.exe";
    std::wstring launcherProduct=L"鹰角", gameCode=L"endfield", uiVersion=L"1.6.";
    std::wstring arguments=L"--game=endfield --reason=4";
    std::vector<std::wstring> gameCandidates{L"Arknights Endfield/Endfield.exe",L"Endfield.exe"};
    std::vector<std::wstring> excluded{L"Backup",L"Backups",L"Downloads",L"Temp",L"Cache",L"$RECYCLE.BIN",L".git",L"tests",L"tools",L"src",L"Windows",L"node_modules"};
    int depth=8, readyTimeout=300, gameTimeout=60, debounce=10, closeTimeout=5, pollMs=250;
    double x=.835, y=.866;
    bool hidden=true, close=true, forceClose=true;
    static Configuration Load(const std::optional<fs::path>& path) {
        Configuration c;
        c.source=path.value_or(ExecutablePath().parent_path()/L"ArknightsEndfieldLaunchBridge.ini");
        if (!fs::exists(c.source)) { if(path) throw BridgeError(3,"Configuration file not found"); c.source.clear(); return c; }
        c.gameFile=IniString(c.source,L"Paths",L"GameFileName",c.gameFile.c_str());
        c.launcherFile=IniString(c.source,L"Paths",L"LauncherFileName",c.launcherFile.c_str());
        c.runtimeFile=IniString(c.source,L"Paths",L"RuntimeFileName",c.runtimeFile.c_str());
        c.launcherProduct=IniString(c.source,L"Paths",L"LauncherProductContains",c.launcherProduct.c_str());
        c.gameCandidates=Split(IniString(c.source,L"Discovery",L"GameCandidates",L"Arknights Endfield/Endfield.exe;Endfield.exe"),L';');
        c.excluded=Split(IniString(c.source,L"Discovery",L"ExcludedDirectories",L"Backup;Backups;Downloads;Temp;Cache;$RECYCLE.BIN;.git;tests;tools;src;Windows;node_modules"),L';');
        c.depth=GetPrivateProfileIntW(L"Discovery",L"SearchDepth",8,c.source.c_str());
        c.arguments=IniString(c.source,L"Launch",L"LauncherArguments",c.arguments.c_str());
        c.gameCode=IniString(c.source,L"Launch",L"GameCode",c.gameCode.c_str());
        c.uiVersion=IniString(c.source,L"Trigger",L"UiVersionPrefix",c.uiVersion.c_str());
        c.x=std::stod(IniString(c.source,L"Trigger",L"ClickXRatio",L"0.835"));
        c.y=std::stod(IniString(c.source,L"Trigger",L"ClickYRatio",L"0.866"));
        c.hidden=GetPrivateProfileIntW(L"LauncherWindow",L"SuppressWindow",1,c.source.c_str())!=0;
        c.close=GetPrivateProfileIntW(L"LauncherWindow",L"CloseAfterGame",1,c.source.c_str())!=0;
        c.forceClose=GetPrivateProfileIntW(L"LauncherWindow",L"ForceAfterTimeout",1,c.source.c_str())!=0;
        c.closeTimeout=GetPrivateProfileIntW(L"Timing",L"CloseTimeoutSeconds",5,c.source.c_str());
        c.readyTimeout=GetPrivateProfileIntW(L"Timing",L"ReadyTimeoutSeconds",300,c.source.c_str());
        c.gameTimeout=GetPrivateProfileIntW(L"Timing",L"GameTimeoutSeconds",60,c.source.c_str());
        c.debounce=GetPrivateProfileIntW(L"Timing",L"ExitDebounceSeconds",10,c.source.c_str());
        c.pollMs=GetPrivateProfileIntW(L"Timing",L"PollMilliseconds",250,c.source.c_str());
        if(c.depth<1||c.depth>12||c.readyTimeout<10||c.readyTimeout>600||c.gameTimeout<1||c.gameTimeout>600||c.debounce<1||c.debounce>300||c.closeTimeout<1||c.closeTimeout>30||c.pollMs<100||c.pollMs>2000||!std::isfinite(c.x)||!std::isfinite(c.y)||c.x<=0||c.x>=1||c.y<=0||c.y>=1)
            throw BridgeError(3,"Configuration values are outside allowed ranges");
        for(const auto& file:{c.gameFile,c.launcherFile,c.runtimeFile})
            if(file.empty()||fs::path(file).filename()!=file||fs::path(file).extension()!=L".exe") throw BridgeError(3,"Executable names must be plain .exe file names");
        if(c.uiVersion.empty()||c.gameCode.empty()||c.launcherProduct.empty()) throw BridgeError(3,"Required launcher configuration is empty");
        return c;
    }
};
struct Options { std::optional<fs::path> root,config; bool validate=false,noDialog=false; size_t ignored=0; };
std::wstring Lower(std::wstring s) { std::transform(s.begin(),s.end(),s.begin(),::towlower); return s; }
Options ParseArguments(const std::vector<std::wstring>& args) {
    Options o;
    for(size_t i=0;i<args.size();++i) {
        const auto& a=args[i]; const auto lower=Lower(a);
        if(a==L"--"||a==L"%command%"||fs::path(a).extension()==L".exe"||lower.rfind(L"steam://",0)==0||lower==L"-applaunch") {o.ignored+=args.size()-i;break;}
        auto next=[&]() {if(++i>=args.size()) throw BridgeError(3,"Missing bridge option value"); return args[i];};
        if(a==L"--install-root") o.root=fs::absolute(next());
        else if(a==L"--config") o.config=fs::absolute(next());
        else if(a==L"--validate") o.validate=true;
        else if(a==L"--no-dialog") o.noDialog=true;
        else ++o.ignored; // Never log unknown values or forward them.
    }
    return o;
}
Options ParseOptions() {
    int n=0; wchar_t** argv=CommandLineToArgvW(GetCommandLineW(),&n);
    if(!argv) throw BridgeError(3,"CommandLineToArgvW failed");
    std::vector<std::wstring> args;
    for(int i=1;i<n;++i) args.emplace_back(argv[i]); LocalFree(argv);
    return ParseArguments(args);
}
bool Within(const fs::path& path,const fs::path& root) {
    const auto p=Lower(fs::weakly_canonical(path).wstring()),r=Lower(fs::weakly_canonical(root).wstring());
    return p==r||(p.size()>r.size()&&p.compare(0,r.size(),r)==0&&(p[r.size()]==L'\\'||p[r.size()]==L'/'));
}
bool NonemptyFile(const fs::path& p) {std::error_code e;return fs::is_regular_file(p,e)&&fs::file_size(p,e)>0&&!e;}
std::vector<fs::path> FindFiles(const fs::path& root,const std::wstring& name,const Configuration& c,int maxDepth=-1) {
    std::queue<std::pair<fs::path,int>> q; q.emplace(root,0);
    std::unordered_set<std::wstring> excluded;
    for(const auto& s:c.excluded) excluded.insert(Lower(s));
    std::vector<fs::path> found; size_t visited=0;
    while(!q.empty()) {
        auto [dir,depth]=q.front();q.pop();
        if(++visited>10000) throw BridgeError(3,"Search exceeds 10000 directories; narrow --install-root");
        std::error_code e;
        for(const auto& f:fs::directory_iterator(dir,fs::directory_options::skip_permission_denied,e)) {
            if(e) break;
            const DWORD attrs=GetFileAttributesW(f.path().c_str());
            if(attrs==INVALID_FILE_ATTRIBUTES||(attrs&FILE_ATTRIBUTE_REPARSE_POINT)) continue;
            if(f.is_regular_file(e)&&_wcsicmp(f.path().filename().c_str(),name.c_str())==0&&NonemptyFile(f.path())) found.push_back(fs::weakly_canonical(f.path()));
            if(depth<(maxDepth<0?c.depth:maxDepth)&&f.is_directory(e)&&!excluded.contains(Lower(f.path().filename().wstring()))) q.emplace(f.path(),depth+1);
        }
    }
    return found;
}
std::wstring FileProperty(const fs::path& path,const wchar_t* property) {
    DWORD ignored=0,size=GetFileVersionInfoSizeW(path.c_str(),&ignored); if(!size)return {};
    std::vector<BYTE> bytes(size); if(!GetFileVersionInfoW(path.c_str(),0,size,bytes.data()))return {};
    struct Translation{WORD language,codepage;}; Translation* t=nullptr;UINT len=0;
    if(!VerQueryValueW(bytes.data(),L"\\VarFileInfo\\Translation",reinterpret_cast<void**>(&t),&len)||len<sizeof(Translation))return {};
    wchar_t query[128]{};swprintf_s(query,L"\\StringFileInfo\\%04x%04x\\%s",t[0].language,t[0].codepage,property);
    wchar_t* value=nullptr; if(!VerQueryValueW(bytes.data(),query,reinterpret_cast<void**>(&value),&len)||!value)return {};
    return value;
}
fs::path PickGame(const fs::path& root,const Configuration& c,Logger* log=nullptr) {
    const auto found=FindFiles(root,c.gameFile,c);
    if(log)for(const auto& p:found)log->Info(L"discovery.candidate",p.wstring()+L" fileVersion="+FileProperty(p,L"FileVersion"));
    for(const auto& preferred:c.gameCandidates) {
        if(fs::path(preferred).is_absolute())throw BridgeError(3,"GameCandidates must be relative paths");
        const auto p=fs::weakly_canonical(root/preferred);
        if(!Within(p,root))throw BridgeError(3,"Candidate escapes root");
        for(const auto& f:found)if(_wcsicmp(f.c_str(),p.c_str())==0)return f;
    }
    if(found.empty())throw BridgeError(3,"Game executable not found");
    if(found.size()>1)throw BridgeError(3,"Ambiguous game candidates; narrow root or configure GameCandidates");
    return found[0];
}
fs::path FindLauncher(const fs::path& game,const Configuration& c) {
    auto root=game.parent_path();
    for(int level=0;level<3&&root!=root.root_path();++level,root=root.parent_path()) {
        auto files=FindFiles(root,c.launcherFile,c,std::min(c.depth,4));
        std::erase_if(files,[&](const fs::path& p){const auto product=FileProperty(p,L"ProductName");return product.find(c.launcherProduct)==std::wstring::npos;});
        // Compare path strings rather than iterators from separate temporary path objects.
        auto depth=[&](const fs::path& p){const auto r=p.lexically_relative(root);return std::distance(r.begin(),r.end());};
        std::sort(files.begin(),files.end(),[&](const auto& a,const auto& b){return depth(a)<depth(b);});
        if(files.empty())continue;
        if(files.size()>1&&depth(files[0])==depth(files[1]))throw BridgeError(3,"Ambiguous official launchers");
        return files[0];
    }
    throw BridgeError(3,"Official launcher not found; direct game launch is disabled");
}
std::vector<fs::path> RegistryRoots() {
    std::vector<fs::path> roots;
    for(HKEY hive:{HKEY_CURRENT_USER,HKEY_LOCAL_MACHINE})for(const wchar_t* path:{L"Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall",L"Software\\WOW6432Node\\Microsoft\\Windows\\CurrentVersion\\Uninstall"}) {
        HKEY parent=nullptr;if(RegOpenKeyExW(hive,path,0,KEY_READ,&parent)!=ERROR_SUCCESS)continue;
        for(DWORD index=0;;++index) {
            wchar_t name[512];DWORD length=512;
            if(RegEnumKeyExW(parent,index,name,&length,nullptr,nullptr,nullptr,nullptr)!=ERROR_SUCCESS)break;
            HKEY child=nullptr;if(RegOpenKeyExW(parent,name,0,KEY_READ,&child)!=ERROR_SUCCESS)continue;
            auto value=[&](const wchar_t* field){wchar_t buffer[32768]{};DWORD bytes=sizeof(buffer),type=0;if(RegQueryValueExW(child,field,nullptr,&type,reinterpret_cast<BYTE*>(buffer),&bytes)!=ERROR_SUCCESS||(type!=REG_SZ&&type!=REG_EXPAND_SZ))return std::wstring();return std::wstring(buffer);};
            const auto display=value(L"DisplayName"),location=value(L"InstallLocation");
            if((display.find(L"鹰角")!=std::wstring::npos||Lower(display).find(L"hypergryph")!=std::wstring::npos)&&!location.empty()) {
                const auto root=fs::path(location).parent_path();if(root!=root.root_path()&&fs::is_directory(root))roots.push_back(root);
            }
            RegCloseKey(child);
        }
        RegCloseKey(parent);
    }
    return roots;
}
struct Installation {fs::path root,game,launcher;};
Installation ResolveInstallation(const Options& o,const Configuration& c,Logger* log=nullptr) {
    auto resolve=[&](fs::path root) {
        root=fs::weakly_canonical(root);
        if(!fs::is_directory(root)||root==root.root_path())throw BridgeError(3,"Invalid installation root (drive roots are forbidden)");
        const auto game=PickGame(root,c,log);return Installation{root,game,FindLauncher(game,c)};
    };
    if(o.root)return resolve(*o.root);
    auto root=ExecutablePath().parent_path();
    for(int i=0;i<4&&root!=root.root_path();++i,root=root.parent_path()) {
        auto scan=c;scan.depth=std::min(c.depth,i==0?c.depth:3);
        if(!FindFiles(root,c.gameFile,scan).empty())return resolve(root);
    }
    for(const auto& p:RegistryRoots()) {try{return resolve(p);}catch(const BridgeError&){}}
    throw BridgeError(3,"Installation not found; copy the bridge into the installation tree or use --install-root");
}
std::optional<fs::path> ProcessPath(DWORD pid) {
    HANDLE h=OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION,FALSE,pid);if(!h)return {};
    std::wstring text(32768,L'\0');DWORD n=static_cast<DWORD>(text.size());
    const BOOL ok=QueryFullProcessImageNameW(h,0,text.data(),&n);CloseHandle(h);
    if(!ok)return {};text.resize(n);return fs::path(text);
}
std::map<DWORD,DWORD> ProcessParents() {
    std::map<DWORD,DWORD> result;HANDLE h=CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS,0);
    if(h==INVALID_HANDLE_VALUE)throw BridgeError(7,"Unable to snapshot process identities");
    PROCESSENTRY32W p{sizeof(p)};
    if(Process32FirstW(h,&p))do{result[p.th32ProcessID]=p.th32ParentProcessID;}while(Process32NextW(h,&p));CloseHandle(h);return result;
}
std::vector<DWORD> MatchingProcesses(const fs::path& game) {
    std::vector<DWORD> result;HANDLE h=CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS,0);
    if(h==INVALID_HANDLE_VALUE)throw BridgeError(7,"Unable to snapshot game processes");
    PROCESSENTRY32W p{sizeof(p)};
    if(Process32FirstW(h,&p))do{
        if(_wcsicmp(p.szExeFile,game.filename().c_str())!=0)continue;
        auto path=ProcessPath(p.th32ProcessID);if(path&&_wcsicmp(fs::weakly_canonical(*path).c_str(),fs::weakly_canonical(game).c_str())==0)result.push_back(p.th32ProcessID);
    }while(Process32NextW(h,&p));CloseHandle(h);return result;
}
struct ProcessHandle {
    HANDLE handle=nullptr;DWORD pid=0;
    ProcessHandle()=default;ProcessHandle(HANDLE h,DWORD p):handle(h),pid(p){}
    ProcessHandle(const ProcessHandle&)=delete;ProcessHandle& operator=(const ProcessHandle&)=delete;
    ProcessHandle(ProcessHandle&& p) noexcept:handle(p.handle),pid(p.pid){p.handle=nullptr;}
    ProcessHandle& operator=(ProcessHandle&& p) noexcept{if(this!=&p){if(handle)CloseHandle(handle);handle=p.handle;pid=p.pid;p.handle=nullptr;}return *this;}
    ~ProcessHandle(){if(handle)CloseHandle(handle);}
    bool Alive()const{return handle&&WaitForSingleObject(handle,0)==WAIT_TIMEOUT;}
    std::wstring ExitCode()const{DWORD code=0;return handle&&GetExitCodeProcess(handle,&code)&&code!=STILL_ACTIVE?std::to_wstring(code):L"unavailable";}
};
std::wstring QuoteArgument(const std::wstring& v) {
    if(!v.empty()&&v.find_first_of(L" \t\"")==std::wstring::npos)return v;
    std::wstring out=L"\"";size_t slashes=0;
    for(wchar_t ch:v){if(ch==L'\\'){++slashes;continue;}if(ch==L'"')out.append(slashes*2+1,L'\\');else out.append(slashes,L'\\');slashes=0;out+=ch;}
    out.append(slashes*2,L'\\');return out+L"\"";
}
ProcessHandle StartProcess(const fs::path& exe,const std::wstring& args,const fs::path& cwd,bool hidden) {
    auto command=QuoteArgument(exe.wstring())+(args.empty()?L"":L" "+args);
    STARTUPINFOW si{sizeof(si)};if(hidden){si.dwFlags=STARTF_USESHOWWINDOW;si.wShowWindow=SW_HIDE;}
    PROCESS_INFORMATION pi{};
    if(!CreateProcessW(exe.c_str(),command.data(),nullptr,nullptr,FALSE,0,nullptr,cwd.c_str(),&si,&pi))throw BridgeError(7,"CreateProcessW: "+Utf8(Win32Message()));
    CloseHandle(pi.hThread);return {pi.hProcess,pi.dwProcessId};
}
std::string ReadSharedFile(const fs::path& path,ULONGLONG maxBytes=64*1024*1024) {
    HANDLE h=CreateFileW(path.c_str(),GENERIC_READ,FILE_SHARE_READ|FILE_SHARE_WRITE|FILE_SHARE_DELETE,nullptr,OPEN_EXISTING,0,nullptr);
    if(h==INVALID_HANDLE_VALUE)return {};
    LARGE_INTEGER size{};if(!GetFileSizeEx(h,&size)||size.QuadPart<=0||static_cast<ULONGLONG>(size.QuadPart)>maxBytes){CloseHandle(h);return {};}
    std::string text(static_cast<size_t>(size.QuadPart),'\0');DWORD n=0;BOOL ok=ReadFile(h,text.data(),static_cast<DWORD>(text.size()),&n,nullptr);CloseHandle(h);if(!ok)return {};text.resize(n);return text;
}
fs::path LocalLow() {
    PWSTR text=nullptr;if(FAILED(SHGetKnownFolderPath(FOLDERID_LocalAppDataLow,0,nullptr,&text)))throw BridgeError(3,"LocalLow folder lookup failed");
    fs::path result(text);CoTaskMemFree(text);return result;
}
std::string ParseState(const std::string& text,DWORD thread,const std::wstring& gameCode,bool requireWeb=true) {
    std::istringstream input(text);std::string line,uuid,state="session_missing";
    bool connected=false,loaded=false;
    const auto tag="[thread "+std::to_string(thread)+"]";
    const auto header="csc PrintConfig item:strProjectName: "+Utf8(gameCode)+",";
    const std::regex id(R"(strGameUUID:\s*([^,\s]+))"), status(R"(strState:([a-z_]+))");
    while(std::getline(input,line)) {
        if(line.find(tag)==std::string::npos)continue;
        if(line.find(header)!=std::string::npos) {
            std::smatch m;uuid=std::regex_search(line,m,id)?m[1].str():"";state="state_pending";connected=loaded=false;
        }
        if(uuid.empty())continue;
        if(line.find("cwdmb OnJsBridgeConnected")!=std::string::npos)connected=true;
        if(line.find("cwdmb OnRecvWebPageLoaded")!=std::string::npos)loaded=true;
        if(line.find("cwdmb OnMainBtnStateInfoChanged info:strUUID:"+uuid+",")==std::string::npos)continue;
        std::smatch m;state=std::regex_search(line,m,status)?m[1].str():"state_pending";
        if(state=="start_game") {
            for(const auto* marker:{"bQueryLatestGameInfoResult:1","bGameServiceStopped:0","bNeedLogin:0","bNeedPurchase:0","bPreparingStartGame:0"})
                if(line.find(marker)==std::string::npos)state="not_ready";
        }
    }
    return state=="start_game"&&requireWeb&&(!connected||!loaded)?"web_ui_pending":state;
}
std::pair<std::string,fs::path> ReadOfficialState(DWORD thread,const Configuration& c) {
    const auto base=LocalLow()/L"Hypergryph";if(!fs::is_directory(base))return {"log_missing",{}};
    size_t visited=0;std::error_code e;
    for(const auto& dir:fs::directory_iterator(base,fs::directory_options::skip_permission_denied,e)) {
        if(++visited>64)break;
        if(!dir.is_directory(e)||(GetFileAttributesW(dir.path().c_str())&FILE_ATTRIBUTE_REPARSE_POINT))continue;
        const auto path=dir.path()/L"logs"/L"games.log";
        const auto state=ParseState(ReadSharedFile(path),thread,c.gameCode,c.hidden);
        if(state!="session_missing")return {state,path};
    }
    return {"session_missing",{}};
}
bool AuthFailure(const std::string& text) {
    std::istringstream input(text);std::string line;
    while(std::getline(input,line))if(line.find("PC launch auth failed")!=std::string::npos&&line.find("errorCode: 10100")!=std::string::npos)return true;
    return false;
}
bool CurrentAuthFailure(ULONGLONG since) {
    const auto path=LocalLow()/L"Hypergryph"/L"Endfield"/L"Player.log";
    WIN32_FILE_ATTRIBUTE_DATA data{};if(!GetFileAttributesExW(path.c_str(),GetFileExInfoStandard,&data))return false;
    ULARGE_INTEGER modified{data.ftLastWriteTime.dwLowDateTime,data.ftLastWriteTime.dwHighDateTime};
    return modified.QuadPart>=since&&AuthFailure(ReadSharedFile(path,16*1024*1024));
}
struct WindowInfo {HWND handle=nullptr;DWORD pid=0,thread=0;int width=0,height=0;fs::path path;};
std::optional<WindowInfo> FindWindow(const fs::path& root,const Configuration& c) {
    struct Context {const fs::path& root;const Configuration& c;std::optional<WindowInfo> result;bool multiple=false;std::exception_ptr error;};
    Context context{root,c};
    EnumWindows([](HWND window,LPARAM arg)->BOOL{
        auto& ctx=*reinterpret_cast<Context*>(arg);try {RECT r{};if(!GetClientRect(window,&r)||r.right<800||r.bottom<400)return TRUE;
        DWORD pid=0;const DWORD tid=GetWindowThreadProcessId(window,&pid);
        auto path=ProcessPath(pid);if(!path||!Within(*path,ctx.root)||_wcsicmp(path->filename().c_str(),ctx.c.runtimeFile.c_str())!=0)return TRUE;
        wchar_t cls[256]{};GetClassNameW(window,cls,256);if(!wcsstr(cls,L"QWindow"))return TRUE;
        if(ctx.result){ctx.multiple=true;return FALSE;}
        ctx.result=WindowInfo{window,pid,tid,r.right,r.bottom,*path};return TRUE;
        }catch(...){ctx.error=std::current_exception();return FALSE;}
    },reinterpret_cast<LPARAM>(&context));
    if(context.error)std::rethrow_exception(context.error);
    if(context.multiple)throw BridgeError(8,"Multiple official launcher windows; refusing input");
    return context.result;
}
bool HasForeignChild(DWORD pid,const fs::path& root) {
    for(const auto& [child,parent]:ProcessParents())if(parent==pid) {const auto path=ProcessPath(child);if(!path||!Within(*path,root))return true;}
    return false;
}
bool MayForceClose(bool identity,bool game,bool foreign,bool enabled){return identity&&!game&&!foreign&&enabled;}
bool UiVersionMatches(const std::wstring& actual,const std::wstring& prefix){return !prefix.empty()&&actual.rfind(prefix,0)==0;}
bool Yellow(COLORREF c){return c!=CLR_INVALID&&GetRValue(c)>=200&&GetGValue(c)>=180&&GetBValue(c)<=90;}
bool YellowButton(HWND window,int x,int y) {
    HDC dc=GetDC(window);if(!dc)return false;int yes=0;for(int dx:{-35,-25,-15,15,25,35})if(Yellow(GetPixel(dc,x+dx,y-12)))++yes;ReleaseDC(window,dc);return yes>=4;
}
class WindowSession {
    struct Runtime {ProcessHandle process;WindowInfo window;};
    fs::path root_,game_;const Configuration& c_;Logger& log_;
    std::mutex mutex_;std::map<DWORD,Runtime> runtimes_;
    std::atomic<bool> interaction_{false};std::exception_ptr error_;std::jthread thread_;
public:
    WindowSession(const Installation& install,const Configuration& c,Logger& log):root_(install.launcher.parent_path()),game_(install.game),c_(c),log_(log) {
        thread_=std::jthread([this](std::stop_token stop) {
            try {
                while(!stop.stop_requested()) {
                    const auto window=FindWindow(root_,c_);
                    if(window) {
                        std::scoped_lock lock(mutex_);
                        if(!runtimes_.contains(window->pid)) {
                            HANDLE h=OpenProcess(SYNCHRONIZE|PROCESS_QUERY_LIMITED_INFORMATION|PROCESS_TERMINATE,FALSE,window->pid);
                            if(!h)throw BridgeError(7,"Unable to retain official launcher: "+Utf8(Win32Message()));
                            runtimes_.emplace(window->pid,Runtime{ProcessHandle(h,window->pid),*window});
                            log_.Info(L"launcher.window_bound",L"pid="+std::to_wstring(window->pid)+L" path="+window->path.wstring());
                        }
                        runtimes_.at(window->pid).window=*window;
                        if(c_.hidden&&!interaction_&&IsWindowVisible(window->handle)) {
                            ShowWindow(window->handle,SW_HIDE);
                            log_.Info(L"launcher.window_hidden",L"pid="+std::to_wstring(window->pid)+L" visible="+(IsWindowVisible(window->handle)?L"true":L"false")+L" officialTray=retained");
                        }
                    }
                    std::this_thread::sleep_for(20ms);
                }
            }catch(...) {std::scoped_lock lock(mutex_);error_=std::current_exception();interaction_=true;for(auto& [pid,r]:runtimes_)if(r.process.Alive())ShowWindow(r.window.handle,SW_RESTORE);}
        });
    }
    ~WindowSession(){thread_.request_stop();if(thread_.joinable())thread_.join();}
    void Check(){std::scoped_lock lock(mutex_);if(error_)std::rethrow_exception(error_);}
    void Restore(){interaction_=true;std::scoped_lock lock(mutex_);for(auto& [pid,r]:runtimes_)if(r.process.Alive())ShowWindow(r.window.handle,SW_RESTORE);}
    void CloseAfterGame() {
        Check();if(!c_.close)return;
        std::scoped_lock lock(mutex_);
        for(auto& [pid,r]:runtimes_) {
            auto same=[&]{const auto p=ProcessPath(pid);return r.process.Alive()&&p&&_wcsicmp(p->c_str(),r.window.path.c_str())==0;};
            if(!r.process.Alive())continue;
            if(!same()||!MatchingProcesses(game_).empty()||HasForeignChild(pid,root_)){log_.Info(L"launcher.close_skipped",L"pid="+std::to_wstring(pid)+L" reason=identity_or_other_game");continue;}
            log_.Info(L"launcher.close_requested",L"pid="+std::to_wstring(pid)+L" timeoutSeconds="+std::to_wstring(c_.closeTimeout));
            DWORD owner=0;GetWindowThreadProcessId(r.window.handle,&owner);
            if(owner==pid&&IsWindow(r.window.handle))PostMessageW(r.window.handle,WM_CLOSE,0,0);
            if(WaitForSingleObject(r.process.handle,c_.closeTimeout*1000)==WAIT_OBJECT_0){log_.Info(L"launcher.closed",L"mode=graceful pid="+std::to_wstring(pid)+L" exitCode="+r.process.ExitCode());continue;}
            if(!MayForceClose(same(),!MatchingProcesses(game_).empty(),HasForeignChild(pid,root_),c_.forceClose)){log_.Info(L"launcher.close_skipped",L"reason=force_disabled_or_other_game");continue;}
            if(!TerminateProcess(r.process.handle,0))throw BridgeError(20,"Unable to end official launcher: "+Utf8(Win32Message()));
            if(WaitForSingleObject(r.process.handle,5000)!=WAIT_OBJECT_0)throw BridgeError(20,"Official launcher close timed out");
            log_.Info(L"launcher.closed",L"mode=forced_after_tray_close pid="+std::to_wstring(pid)+L" exitCode="+r.process.ExitCode());
        }
    }
};
void TriggerOfficial(const Installation& install,const Configuration& c,WindowSession& session,Logger& log) {
    log.Info(L"launcher.start",L"path="+install.launcher.wstring()+L" cwd="+install.launcher.parent_path().wstring()+L" arguments="+(c.arguments==L"--game=endfield --reason=4"?c.arguments:L"<custom-arguments-redacted>")+L" graphics=official_launcher");
    auto entry=StartProcess(install.launcher,c.arguments,install.launcher.parent_path(),c.hidden);
    const auto deadline=std::chrono::steady_clock::now()+std::chrono::seconds(c.readyTimeout);std::string last;
    try {
        while(std::chrono::steady_clock::now()<deadline) {
            session.Check();
            if(!MatchingProcesses(install.game).empty()){log.Info(L"launcher.game_already_appeared",L"skip button");return;}
            auto window=FindWindow(install.launcher.parent_path(),c);
            if(window) {
                auto [state,path]=ReadOfficialState(window->thread,c);
                if(state!=last){last=state;log.Info(L"launcher.waiting",L"pid="+std::to_wstring(window->pid)+L" uiThread="+std::to_wstring(window->thread)+L" state="+Widen(state));}
                if(state=="need_login"||state=="need_purchase"||state=="update_game"||state=="download_game"||state=="download_except"||state=="game_service_stopped")throw BridgeError(8,"Official launcher needs user action: "+state);
                if(state=="start_game") {
                    const auto current=ProcessPath(window->pid);
                    if(!current||_wcsicmp(current->c_str(),window->path.c_str())!=0)throw BridgeError(8,"Official launcher identity changed");
                    if(!UiVersionMatches(FileProperty(window->path,L"FileVersion"),c.uiVersion))throw BridgeError(8,"Unverified launcher UI version; update UiVersionPrefix and button ratios only after checking UI");
                    if(!c.hidden){ShowWindow(window->handle,SW_RESTORE);SetForegroundWindow(window->handle);Sleep(300);}
                    const auto popup=GetWindow(window->handle,GW_ENABLEDPOPUP);
                    if((popup&&popup!=window->handle)||(!c.hidden&&GetForegroundWindow()!=window->handle)){Sleep(500);continue;}
                    if(ReadOfficialState(window->thread,c).first!="start_game"){Sleep(500);continue;}
                    RECT r{};if(!GetClientRect(window->handle,&r)||r.right<800||r.bottom<400||double(r.right)/r.bottom<1.5||double(r.right)/r.bottom>2.2)throw BridgeError(8,"Unexpected launcher client dimensions");
                    const int x=static_cast<int>(std::lround(r.right*c.x)),y=static_cast<int>(std::lround(r.bottom*c.y));
                    if(!c.hidden&&!YellowButton(window->handle,x,y)){Sleep(500);continue;}
                    log.Info(L"resource.ready",L"pid="+std::to_wstring(window->pid)+L" uiThread="+std::to_wstring(window->thread)+L" log="+path.wstring());
                    const LPARAM point=MAKELPARAM(x,y);
                    if(!PostMessageW(window->handle,WM_LBUTTONDOWN,MK_LBUTTON,point)||!PostMessageW(window->handle,WM_LBUTTONUP,0,point))throw BridgeError(8,"Official button message failed: "+Utf8(Win32Message()));
                    log.Info(L"launcher.triggered",L"mode="+std::wstring(c.hidden?L"hidden_relative_click":L"relative_click")+L" pid="+std::to_wstring(window->pid)+L" client="+std::to_wstring(r.right)+L"x"+std::to_wstring(r.bottom)+L" x="+std::to_wstring(x)+L" y="+std::to_wstring(y));return;
                }
            }
            Sleep(500);
        }
        throw BridgeError(8,"Timed out waiting for official resources / web UI: "+last);
    }catch(...){session.Restore();throw;}
}
void WatchGame(const fs::path& game,const Configuration& c,Logger& log,const std::function<void()>& pulse={},bool checkAuth=false) {
    std::map<DWORD,ProcessHandle> watched;bool seen=false;std::optional<std::chrono::steady_clock::time_point> gone;
    const auto start=std::chrono::steady_clock::now();const auto ticks=NowTicks();
    while(true) {
        log.Maintain();if(pulse)pulse();
        const auto active=MatchingProcesses(game);
        for(DWORD pid:active)if(!watched.contains(pid)) {
            HANDLE h=OpenProcess(SYNCHRONIZE|PROCESS_QUERY_LIMITED_INFORMATION,FALSE,pid);
            if(!h)throw BridgeError(7,"Unable to retain game process: "+Utf8(Win32Message()));
            watched.emplace(pid,ProcessHandle(h,pid));log.Info(L"game.appeared",L"pid="+std::to_wstring(pid)+L" path="+game.wstring());seen=true;
        }
        for(auto it=watched.begin();it!=watched.end();) {
            if(!it->second.Alive()){log.Info(L"game.disappeared",L"pid="+std::to_wstring(it->first)+L" exitCode="+it->second.ExitCode());it=watched.erase(it);}else ++it;
        }
        if(checkAuth&&!watched.empty()&&CurrentAuthFailure(ticks))throw BridgeError(8,"Official SDK launch authentication failed (10100); process existence is not gameplay success");
        if(!watched.empty())gone.reset();
        else if(seen) {
            if(!gone)gone=std::chrono::steady_clock::now();
            const auto ms=std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::steady_clock::now()-*gone).count();
            if(ms>=c.debounce*1000){log.Info(L"game.finished",L"exitDelayMs="+std::to_wstring(ms));return;}
        } else if(std::chrono::steady_clock::now()-start>std::chrono::seconds(c.gameTimeout))throw BridgeError(8,"Game did not appear after official trigger");
        Sleep(c.pollMs);
    }
}
std::optional<int> Elevate(const Options& o,const Installation& install,Logger& log) {
    if(IsUserAnAdmin())return {};
    std::wstring args=L"--install-root "+QuoteArgument(install.root.wstring());
    if(o.config)args+=L" --config "+QuoteArgument(o.config->wstring());
    if(o.noDialog)args+=L" --no-dialog";
    const auto exe=ExecutablePath();SHELLEXECUTEINFOW info{sizeof(info)};
    info.fMask=SEE_MASK_NOCLOSEPROCESS;info.lpVerb=L"runas";info.lpFile=exe.c_str();info.lpParameters=args.c_str();info.nShow=SW_HIDE;
    log.Info(L"bridge.elevation_requested",L"native parent waits for elevated bridge");
    if(!ShellExecuteExW(&info))throw BridgeError(7,"UAC request failed or cancelled: "+Utf8(Win32Message()));
    ProcessHandle child(info.hProcess,GetProcessId(info.hProcess));
    if(WaitForSingleObject(child.handle,INFINITE)!=WAIT_OBJECT_0)throw BridgeError(7,"Unable to wait for elevated bridge");
    DWORD code=20;GetExitCodeProcess(child.handle,&code);return static_cast<int>(code);
}
int Run(const Options& o,const Configuration& c,const Installation& install,Logger& log) {
    log.Info(L"effective",L"root="+install.root.wstring()+L" game="+install.game.wstring()+L" launcher="+install.launcher.wstring()+L" graphics=official_launcher ignoredArgumentCount="+std::to_wstring(o.ignored));
    log.Info(L"configuration",L"source="+c.source.wstring()+L" searchDepth="+std::to_wstring(c.depth)+L" readyTimeoutSeconds="+std::to_wstring(c.readyTimeout)+L" gameTimeoutSeconds="+std::to_wstring(c.gameTimeout)+L" exitDebounceSeconds="+std::to_wstring(c.debounce)+L" closeTimeoutSeconds="+std::to_wstring(c.closeTimeout)+L" pollMilliseconds="+std::to_wstring(c.pollMs)+L" suppressWindow="+std::to_wstring(c.hidden)+L" closeAfterGame="+std::to_wstring(c.close)+L" forceAfterTimeout="+std::to_wstring(c.forceClose)+L" uiVersionPrefix="+c.uiVersion+L" clickRatio="+std::to_wstring(c.x)+L","+std::to_wstring(c.y));
    if(o.validate){log.Info(L"validation.success",L"paths and native resources validated; no UI action performed");return 0;}
    if(const auto elevated=Elevate(o,install,log))return *elevated;
    // A path hash selects a single native bridge per installation.
    const std::wstring key=L"Local\\ArknightsEndfieldLaunchBridge-"+std::to_wstring(std::hash<std::wstring>{}(Lower(install.game.wstring())));
    HANDLE mutex=CreateMutexW(nullptr,TRUE,key.c_str());if(!mutex)throw BridgeError(7,"Unable to create bridge mutex");
    if(GetLastError()==ERROR_ALREADY_EXISTS){CloseHandle(mutex);log.Info(L"instance.already_running",L"skip duplicate");return 0;}
    try {
        WindowSession session(install,c,log);
        if(MatchingProcesses(install.game).empty())TriggerOfficial(install,c,session,log);
        else log.Info(L"attach.existing",L"full path matched; official graphics unchanged");
        try{WatchGame(install.game,c,log,[&]{session.Check();},true);session.CloseAfterGame();}
        catch(...){session.Restore();throw;}
    }catch(...){ReleaseMutex(mutex);CloseHandle(mutex);throw;}
    ReleaseMutex(mutex);CloseHandle(mutex);return 0;
}
std::wstring SystemDescription() {
    using RtlVersion=LONG(WINAPI*)(OSVERSIONINFOW*);
    OSVERSIONINFOW v{sizeof(v)};const auto fn=reinterpret_cast<RtlVersion>(GetProcAddress(GetModuleHandleW(L"ntdll.dll"),"RtlGetVersion"));if(fn)fn(&v);
    return L"Windows "+std::to_wstring(v.dwMajorVersion)+L"."+std::to_wstring(v.dwMinorVersion)+L"."+std::to_wstring(v.dwBuildNumber)+L" processArchitecture=x64";
}
int BridgeMain() {
    Options o;std::optional<Logger> log;const auto started=std::chrono::steady_clock::now();int result=20;
    try {
        o=ParseOptions();log.emplace();
        log->Info(L"start",L"version="+std::wstring(Version)+L" implementation=C++20 MSVC-static "+SystemDescription());
        const auto c=Configuration::Load(o.config);const auto install=ResolveInstallation(o,c,&*log);result=Run(o,c,install,*log);
    }catch(const std::exception& ex) {
        std::wstring message=L"终末地桥接器失败："+Widen(ex.what());
        try{if(!log)throw std::runtime_error("Logger initialization failed");log->Error(L"bridge.failure",L"type="+Widen(typeid(ex).name())+L" message="+Widen(ex.what())+(dynamic_cast<const BridgeError*>(&ex)?dynamic_cast<const BridgeError*>(&ex)->stack:StackTrace()));message+=L"\n日志："+log->path().wstring();}
        catch(const std::exception& write){message+=L"\n日志写入失败："+Widen(write.what());}
        if(!o.noDialog)MessageBoxW(nullptr,message.c_str(),ProductName,MB_OK|MB_ICONERROR);
        if(const auto* known=dynamic_cast<const BridgeError*>(&ex))result=known->code();else result=20;
    }
    try{if(log)log->Info(L"end",L"exitCode="+std::to_wstring(result)+L" elapsedMs="+std::to_wstring(std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::steady_clock::now()-started).count()));}
    catch(const std::exception& ex){if(!o.noDialog)MessageBoxW(nullptr,(L"日志写入失败："+Widen(ex.what())).c_str(),ProductName,MB_OK|MB_ICONERROR);result=20;}
    return result;
}
} // namespace
#ifndef ENDFIELD_UNIT_TEST
int WINAPI wWinMain(HINSTANCE,HINSTANCE,PWSTR,int){return BridgeMain();}
#endif
