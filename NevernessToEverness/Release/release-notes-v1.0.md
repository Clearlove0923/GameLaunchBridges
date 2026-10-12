## NevernessToEvernessLaunchBridge v1.0

首个《异环》启动桥接器正式版本，使用 C++20 和原生 Win32 API 开发。发布附件为 Windows x64 静态运行库单文件程序，无需安装 .NET、Python 或额外 VC++ Runtime。

## 主要功能

1. 以 `/launcher /directly` 隐藏启动官方入口，等待本次启动器完成资源检查后再自动触发“开始游戏”，避开 `/autoplay` 导致全部角色语音缺失的问题。
2. 抑制官方启动器主窗口显示，并在游戏退出后请求本次创建的启动器关闭；官方程序只缩到托盘时可按配置超时清理。
3. 按进程名和完整路径跟踪游戏进程，提供退出防抖、有限路径发现和可选 INI 兼容配置。
4. 支持 Steam `%command%` 尾随命令隔离，兼容 SteamCN-GameLauncher 启动形式。
5. 使用单一日志文件记录正常及错误流程，自动保留最近 7 天记录。

## 使用说明

1. 下载 `NevernessToEvernessLaunchBridge_v1.0.exe`，重命名为 `NevernessToEvernessLaunchBridge.exe` 后放入《异环》官方安装根目录。
2. 直接双击运行；程序会请求管理员权限。
3. Steam 启动项可设置为 `"<异环安装根目录>\NevernessToEvernessLaunchBridge.exe" %command%`，桥接器自己的可选参数需放在 `%command%` 前。

## 验证

1. Windows x64 Release 构建、静态运行库单文件、多尺寸图标、标准与精简路径发现、排除目录、INI 覆盖、日志位置、七天保留和原生错误堆栈测试通过。
2. Steam 尾随命令隔离、显式分隔符和未知桥接参数拒绝测试通过。
3. 官方资源检查完成后触发启动、隐藏启动器及游戏进程检测已完成实机验证；Steam 国服尾随参数修复尚未再次完成 Steam 客户端实机复测。

SHA-256：`08445C2E588A7F0039FAD7053D9C0B00E27946A97A58BAA1A5B9631FA66E9C27`
