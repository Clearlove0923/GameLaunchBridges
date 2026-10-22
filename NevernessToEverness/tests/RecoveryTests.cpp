#include "../src/main.cpp"

int wmain(int argc, wchar_t** argv) {
    if (argc == 3) {
        InstanceLock lock(ExecutablePath().parent_path());
        HANDLE ready = OpenEventW(EVENT_MODIFY_STATE, FALSE, argv[2]);
        if (!ready || !lock.acquired()) return 10;
        SetEvent(ready);
        CloseHandle(ready);
        Sleep(INFINITE);
        return 0;
    }
    Logger log;
    const auto root = ExecutablePath().parent_path();
    const std::wstring eventName = L"Local\\NTE-Restart-Test-" + std::to_wstring(GetCurrentProcessId());
    HANDLE ready = CreateEventW(nullptr, TRUE, FALSE, eventName.c_str());
    if (!ready) return 11;
    auto child = StartProcess(ExecutablePath(), L"--hold " + eventName, root, true);
    if (WaitForSingleObject(ready, 5000) != WAIT_OBJECT_0) {
        TerminateProcess(child.process, 1); CloseHandle(ready); return 12;
    }
    {
        InstanceLock takeover(root, &log);
        if (!takeover.acquired() || WaitForSingleObject(child.process, 0) != WAIT_OBJECT_0) return 13;
    }
    CloseHandle(ready);
    WNDCLASSW wc{};
    wc.lpfnWndProc = DefWindowProcW;
    wc.hInstance = GetModuleHandleW(nullptr);
    wc.lpszClassName = L"RecoveryTestQWindowOwnDC";
    if (!RegisterClassW(&wc)) return 1;
    HWND window = CreateWindowW(wc.lpszClassName, L"Launcher recovery fixture", WS_OVERLAPPEDWINDOW,
        0, 0, 640, 480, nullptr, nullptr, wc.hInstance, nullptr);
    if (!window) return 2;
    // Both newly created and attached launchers use the same PID-bound recovery.
    for (int run = 0; run < 2; ++run) {
        ShowWindow(window, SW_HIDE);
        try {
            LauncherFailureRecovery recovery(GetCurrentProcessId(), log);
            WindowSuppressor suppressor(window, true);
            throw BridgeError(8, "Timed out waiting for the official resource check");
        } catch (const BridgeError&) {}
        MSG message{};
        while (PeekMessageW(&message, nullptr, 0, 0, PM_REMOVE)) {
            TranslateMessage(&message);
            DispatchMessageW(&message);
        }
        if (!IsWindowVisible(window)) return 3;
        Sleep(80);
        if (!IsWindowVisible(window)) return 4;
    }
    ShowWindow(window, SW_HIDE);
    { LauncherFailureRecovery recovery(GetCurrentProcessId(), log); }
    if (IsWindowVisible(window)) return 5; // Successful lifecycle must not restore.
    DestroyWindow(window);
    if (ChineseErrorHint("Timed out waiting for the official resource check").find(L"更新") == std::wstring::npos) return 6;
    if (ChineseErrorHint("Another bridge instance is already running for this installation").find(L"重复双击") == std::wstring::npos) return 7;
    if (ChineseErrorHint("Unable to write log file").find(L"日志写入失败") == std::wstring::npos) return 8;
    if (ChineseErrorHint("The game process did not appear after triggering the official launcher").find(L"登录") == std::wstring::npos) return 9;
    return 0;
}
