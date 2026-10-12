# 异环启动桥接器

`NevernessToEvernessLaunchBridge.exe` 是使用 C++20 和原生 Win32 API 开发的 Windows x64 桥接器，当前版本 `v1.0`。正式产物静态链接 MSVC 运行库，不依赖 .NET、Python 或额外 VC++ Runtime。

桥接器不会直接启动 `HTGame.exe`，也不会使用会导致角色语音缺失的首进程 `/autoplay`。它先以 `/launcher /directly` 隐藏启动官方 `NTEGame.exe`，找到 Qt 主窗口后持续抑制显示，从本次启动器日志确认资源检查完成，再通过 MSAA 调用原生“开始游戏”控件；无障碍控件不可用时，可使用 INI 中的窗口相对坐标回退。游戏启动后，桥接器按进程名和完整路径跟踪 `HTGame.exe`。游戏消失达到退出防抖时间后，桥接器先请求本次创建的启动器正常关闭；若官方程序仅缩到托盘，可在配置的等待时间后终止该启动器进程，避免游戏退出时启动器界面再次弹出。

## 构建与测试

需要 Visual Studio 2022 Build Tools，包含“使用 C++ 的桌面开发”、Windows SDK 和 CMake：

```powershell
.\build.ps1 -GameExe "<官方安装根目录>\NTELauncher.exe"
.\tests\Test-Bridge.ps1
```

首次构建可通过 `-GameExe` 从官方入口提取多尺寸图标；仓库保存 ICO 和可重复执行的提取脚本，后续可以直接运行 `build.ps1`。正式单文件产物为 `Release\NevernessToEvernessLaunchBridge.exe`。测试覆盖标准/精简路径、备份目录排除、INI 覆盖、日志位置、七天保留、原生错误堆栈、单文件和多尺寸图标。

## 使用与配置

桥接器包含管理员权限清单，启动时会出现一次 UAC：

```powershell
.\Release\NevernessToEvernessLaunchBridge.exe --install-root "<官方安装根目录>"
```

把 EXE 放到官方安装根目录时可以直接双击。`--validate` 仅验证路径与配置，`--no-dialog` 禁止错误窗口，`--config <路径>` 加载可选 INI。示例配置位于 `examples/NevernessToEvernessLaunchBridge.ini`，可覆盖入口和本体文件名、候选相对路径、搜索深度、排除目录、启动参数、资源完成标记、按钮名称、回退坐标、隐藏策略、游戏退出后的启动器关闭策略和各项超时。桥接器只会强制结束自己创建的启动器，不会强制结束启动桥接器前已经存在的官方启动器。

Steam 启动项可写为（桥接器自己的可选参数必须放在 `%command%` 前面）：

```text
"<异环安装根目录>\NevernessToEvernessLaunchBridge.exe" %command%
```

Steam 展开的原始命令及其尾随参数只用于维持 Steam 的启动链上下文，桥接器会从首个普通位置参数开始隔离整段尾部，不会把它误当成桥接器配置。未知的桥接器长选项（`--xxx`）仍会报错，以避免拼写错误被静默忽略。

日志固定写入 EXE 同级的 `NevernessToEvernessLaunchBridge-log/NevernessToEvernessLaunchBridge.log`，始终只有一个主要日志文件并保留最近 7 天。日志记录生效配置、发现路径、启动器资源状态、触发方式、游戏进程变化、运行时长和原生异常堆栈，不复制官方日志原文或账号数据。

`NevernessToEvernessLaunchCapture.exe` 仍保留在 `tools/LaunchCapture` 对应构建流程中，用于游戏更新后重新采集官方启动链，不属于正式桥接产物。

## 已确认的官方启动链

2026-10-12 的实机采集确认：官方入口以 `/launcher /directly` 启动 `NTEGame.exe`，先完成本地游戏资源检查并将 `localResChecked` 置为完成状态；用户点击“开始游戏”后，启动器从 `Client` 工作目录启动 `HTGame.exe`，游戏参数为 `/Game/LoginAndCreate/Map/Updater/Updater_P -SAVEWINPOS=1`。该流程下角色语音正常。

对照采集显示，`/launcher /autoplay` 虽然最终使用相同的游戏本体、参数和工作目录，却会在资源状态机完成前直接启动游戏，缺少本地资源检查完成状态；该流程下全部角色语音缺失而音乐与音效正常。再次实测还确认：先启动 `/directly`、再启动第二个 `/autoplay` 进程不会向已有实例传递“开始游戏”动作。因此纯参数桥接不可行，必须在资源检查完成后操作官方启动器控件。

当前 C++ 版本已经通过自动测试和真实安装路径验证；MSAA/相对坐标触发及角色语音仍需完成实机验证，不能以自动测试结果代替。
