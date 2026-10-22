# 异环启动桥接器

`NevernessToEvernessLaunchBridge.exe` 是使用 C++20 和原生 Win32 API 开发的 Windows x64 桥接器，当前版本 `v1.1`，已发布至 [GitHub](https://github.com/Clearlove0923/GameLaunchBridges/releases/tag/NevernessToEverness-v1.1) 和 [CNB](https://cnb.cool/SteamCN-GameLauncher/GameLaunchBridges/-/releases/tag/NevernessToEverness-v1.1)。正式产物静态链接 MSVC 运行库，不依赖 .NET、Python 或额外 VC++ Runtime。详细设计见[实现原理文档](../docs/NevernessToEvernessLaunchBridge.md)。

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

把 EXE 放到官方安装根目录或其任意子目录时均可直接双击。桥接器会从自身目录逐级向上匹配入口与本体的候选相对路径，以识别同一安装根目录；该过程不会递归扫描宽泛的上级目录或整个磁盘。EXE 位于游戏安装范围外时可使用 `--install-root <路径>`。`--validate` 仅验证路径与配置，`--no-dialog` 禁止错误窗口，`--config <路径>` 加载可选 INI。示例配置位于 `examples/NevernessToEvernessLaunchBridge.ini`，可覆盖入口和本体文件名、候选相对路径、搜索深度、排除目录、启动参数、资源完成标记、按钮名称、回退坐标、隐藏策略、游戏退出后的启动器关闭策略和各项超时。桥接器只会强制结束自己创建的启动器，不会强制结束启动桥接器前已经存在的官方启动器。

Steam 启动项可写为（桥接器自己的可选参数必须放在 `%command%` 前面）：

```text
"<异环安装根目录>\NevernessToEvernessLaunchBridge.exe" %command%
```

Steam 展开的原始命令及其尾随参数只用于维持 Steam 的启动链上下文，桥接器会从首个普通位置参数开始隔离整段尾部，不会把它误当成桥接器配置。无法识别的长选项（`--xxx`）也会自动忽略；所有忽略项及普通非敏感值均按传入内容写入 `arguments.ignored` 日志事件，方便核对第三方启动器实际追加的参数。参数名包含 `token`、`cookie`、`password`、`secret`、`auth`、`session`、`ticket` 或 `credential` 等敏感标记时，其值会替换为 `<redacted>`。已知但缺少必要值的 `--install-root` 和 `--config` 仍会报错。

日志固定写入 EXE 同级的 `NevernessToEvernessLaunchBridge-log/NevernessToEvernessLaunchBridge.log`，始终只有一个主要日志文件并保留最近 7 天。日志记录生效配置、发现路径、启动器资源状态、触发方式、游戏进程变化、运行时长和原生异常堆栈，不复制官方日志原文或账号数据。

`NevernessToEvernessLaunchCapture.exe` 仍保留在 `tools/LaunchCapture` 对应构建流程中，用于游戏更新后重新采集官方启动链，不属于正式桥接产物。

## 已确认的官方启动链

2026-10-12 的实机采集确认：官方入口以 `/launcher /directly` 启动 `NTEGame.exe`，先完成本地游戏资源检查并将 `localResChecked` 置为完成状态；用户点击“开始游戏”后，启动器从 `Client` 工作目录启动 `HTGame.exe`，游戏参数为 `/Game/LoginAndCreate/Map/Updater/Updater_P -SAVEWINPOS=1`。该流程下角色语音正常。

对照采集显示，`/launcher /autoplay` 虽然最终使用相同的游戏本体、参数和工作目录，却会在资源状态机完成前直接启动游戏，缺少本地资源检查完成状态；该流程下全部角色语音缺失而音乐与音效正常。再次实测还确认：先启动 `/directly`、再启动第二个 `/autoplay` 进程不会向已有实例传递“开始游戏”动作。因此纯参数桥接不可行，必须在资源检查完成后操作官方启动器控件。

当前 C++ v1.1 已通过自动测试；官方资源检查完成、隐藏启动器、相对坐标触发和游戏进程出现已完成实机验证，直接双击桥接器可正常启动。Steam 国服尾随参数修复已通过固定回归测试，尚需再次完成 Steam 客户端实机复测。角色语音正常的结论来自官方 `/directly` 完成资源检查后手动点击的采集流程；游戏更新后仍须重新核对桥接流程，不能以自动测试代替实机语音验证。

## v1.1：错误提示与失败恢复（2026-10-22）

保留原有英文错误及日志路径，追加按错误原因区分的中文排查提示。资源检查超时提示检查游戏是否更新；重复实例、路径或配置错误、启动器退出、窗口控制失败、游戏未出现和日志写入失败分别提示对应处理方法。

修复失败后官方启动器仍被隐藏的问题：在异常退出、显示错误框之前，先停止窗口抑制线程，再按本次确认的启动器 PID 请求恢复 Qt 主窗口。此恢复适用于桥接器创建及附着的启动器，不强制结束已有启动器，也不自动下载更新。恢复请求数量记录在 `launcher.failure_recovery`；若窗口已经退出或被替换而无法找到，可从官方托盘入口打开或退出启动器后重开。正常游戏退出仍使用原有关闭策略。

实机日志确认：2026-10-17 国服启动器检测版本 1.4.10 → 1.4.15，需下载 2321 MB，仅完成本地资源检查而未出现 `all ready`，桥接器等待超时并在重试时附着同一启动器。代码确认旧版异常路径只停止隐藏线程、不恢复窗口；用户实机反馈确认旧版失败后无法重新打开启动器；2026-10-22 用户反馈修复版超时与窗口恢复现象符合预期，未单独提供完整采集。

自动测试确认：Release 构建、隐藏 Qt 测试窗口异常恢复（连续两次重试）、恢复后不再被隐藏、正常作用域不恢复窗口、中文提示分类，以及原有路径发现、单文件、多尺寸图标、日志位置、七天保留和错误堆栈检查通过。固定入口为 `tests/Test-Bridge.ps1`，窗口恢复测试实现为 `tests/RecoveryTests.cpp`。真实官方更新界面恢复、登录、角色语音、反作弊和 Steam 生命周期尚未完成本次实机复测。
### v1.1：重复双击重新接管（2026-10-22）

正常启动时，先用安装范围对应的重启互斥体串行化接管，再枚举进程：仅对完整路径位于已识别安装根目录内、且 EXE 版本资源 ProductName 确认为 NevernessToEvernessLaunchBridge 的其他进程执行结束并等待退出，包括旧版及版本后缀文件。随后接管原有安装互斥体，开始新一轮桥接；`--validate` 不会结束其他桥接器。旧进程终止会关闭其错误弹窗和隐藏线程，事件记录为 `bridge.restart_previous`。失败时追加中文提示退出旧进程并检查权限。

不结束游戏、官方启动器或更新任务。若官方启动器仍隐藏，新进程按完整路径附着；再次失败时请求恢复窗口。若游戏已经运行，新进程按完整路径跟踪至游戏消失并完成退出防抖，不重复启动；此接管分支不再拥有旧桥接器的启动器句柄，因此游戏退出后不强制清理该启动器，游戏退出码暂不可用。安装根以外的旧桥接器不自动结束；应将桥接器放在各自游戏安装范围内使用。重启桥接器会使 Steam 观察到旧桥接器退出，Steam 在线状态连续性尚未实机验证。

自动测试已覆盖真实子进程持有安装互斥体时结束旧进程、接管被遗弃的互斥体，并通过 Release 编译、单文件与图标、发现路径、日志保留、错误堆栈及失败窗口恢复回归。真实游戏已运行时的跟踪、旧版弹窗接管、更新中的官方启动器与 Steam 生命周期尚需实机复测。测试入口仍为 `tests/Test-Bridge.ps1`，新增进程接管样本位于 `tests/RecoveryTests.cpp`。