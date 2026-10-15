# 明日方舟：终末地 LaunchBridge

当前版本 **v1.0（[GitHub](https://github.com/Clearlove0923/GameLaunchBridges/releases/tag/ArknightsEndfield-v1.0) / [CNB](https://cnb.cool/SteamCN-GameLauncher/GameLaunchBridges/-/releases/tag/ArknightsEndfield-v1.0)），C++20 原生 Windows x64**。发布程序为单文件 `Release/ArknightsEndfieldLaunchBridge.exe`，静态链接 MSVC 运行库，仅依赖 Windows 系统 DLL，无需安装 .NET 或 VC++ 运行库。采用官方游戏多尺寸图标。

## 使用

1. 先用官方鹰角启动器完成安装、更新和登录。
2. 在官方启动器设置是否勾选 DX11，桥接器沿用此设置。不勾选时使用官方默认模式，现有证据不足以将默认模式写成已确认 DX12。
3. 将 EXE 放在终末地安装范围内，或通过 `--install-root "安装目录"` 指定范围。运行后接受 UAC；原始实例等待提权实例退出。
4. 桥接器调用官方入口，后台抑制主窗口、保留官方托盘。等待终末地当前日志会话资源检查与网页接口就绪，然后向官方窗口发送一次开始按钮消息。需要登录、更新等人工操作时恢复官方界面并报错。
5. 本体消失后默认等待 10 秒，再向启动器发送关闭请求。5 秒后仍留在托盘时，重新核对保留句柄、完整路径及其他游戏子进程，再结束对应官方 Games 进程；不结束游戏、反作弊或整个进程树。

2026-10-15 用户已实机确认 C++ 版本启动正常。历史原型已确认进入游戏、仅保留托盘和退出清理；C++ 版本的托盘与退出清理没有单独追加完整复测记录，Steam 生命周期、语音及其他渠道仍未验证。窗口首次创建时仍存在短暂闪现的可能。

## 配置和日志

普通使用不需要配置。[示例 INI](examples/ArknightsEndfieldLaunchBridge.ini) 可复制为 EXE 同级 `ArknightsEndfieldLaunchBridge.ini`，或以 `--config "文件路径"` 指定。旧 C# 原型的 JSON 不再使用。INI 支持文件名、官方参数、目录优先级/排除项/搜索深度、UI 版本前缀/相对坐标、窗口抑制/退出策略和各项超时。含中文的 INI 请保存为 UTF-16 LE BOM；英文示例可直接使用。

`--validate --no-dialog --install-root "安装目录"` 仅验证路径与官方入口文件属性，不启动游戏、不提权、不点击界面。Steam 尾随 `%command%`、EXE、`steam://`、`-applaunch` 和 `--` 后内容被隔离，不传给官方入口。桥接器不提供 DX11/DX12 开关，也不改写官方配置。

唯一主要日志为 EXE 同级 `ArknightsEndfieldLaunchBridge-log/ArknightsEndfieldLaunchBridge.log`。启动时及运行期间每小时清除超过 7 天的记录，保留同一文件中的近期内容。记录 ISO 8601 时间、运行标识/PID、版本/系统/架构、路径、官方入口命令及工作目录、状态转换、游戏生命周期/退出码、关闭结果、防抖、总耗时和原生异常堆栈。自定义参数值与未知尾随内容不写入日志，日志写入失败会在错误弹窗说明原因。

## 构建与检查

安装 Visual Studio 2022 C++ x64 Build Tools、Windows SDK 与 CMake 后，在本目录运行：

```powershell
.\build.ps1
.\tests\Test-Bridge.ps1
```

CMake 使用 C++20、Release x64、静态 MSVC 运行库。通过 `build.ps1 -GameExe "官方 Endfield.exe"` 可重新提取图标。[图标提取脚本](tools/ExtractGameIcon.ps1) 与多尺寸 ICO 一并保留。

2026-10-15 本地确认：真实安装目录只读路径验证通过（未启动游戏）；47 项原生回归通过；单文件、仅系统 DLL、版本、官方图标一致、INI 配置、路径发现、Steam 尾随隔离、日志位置、精确七天保留和错误堆栈检查通过。模拟进程不调用真实游戏，自动测试不能证明登录、反作弊、声音或 Steam 状态。

[实现原理与实机证据](../docs/ArknightsEndfieldLaunchBridge.md) · [C++ 源码](src/main.cpp) · [CMake](CMakeLists.txt) · [固定测试](tests/Test-Bridge.ps1)

终末地旧 C# 桥接器、Capture 与测试源码已移除，后续仅维护 C++ 实现。历史 Capture 的跨盘采集范围问题不属于当前发布 EXE。

正式版本从 v1.0 独立计数，标签 `ArknightsEndfield-v1.0`，附件 `ArknightsEndfieldLaunchBridge_v1.0.exe`。此前 C# 实验编号不作为正式发布版本。
