#include <windows.h>
#include <filesystem>
#include <fstream>
int WINAPI wWinMain(HINSTANCE,HINSTANCE,PWSTR,int) {
    wchar_t path[32768]{}; GetModuleFileNameW(nullptr,path,32768);
    std::wofstream out(std::filesystem::path(path).parent_path()/L"fixture-observed.txt");
    out << std::filesystem::current_path().wstring() << L"\n" << GetCommandLineW(); out.close();
    Sleep(1500); return 7;
}
