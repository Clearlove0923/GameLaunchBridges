# NevernessToEvernessLaunchBridge 实现原理

本文说明 `NevernessToEvernessLaunchBridge.exe` 当前如何等待《异环》官方资源检查、触发官方“开始游戏”动作并管理启动器与游戏生命周期。

## 不能直接使用 `/autoplay` 的原因

2026-10-12 的实机对照采集确认，官方入口以 `/launcher /directly` 启动 `NTEGame.exe` 后，会先完成本地资源检查并将 `localResChecked` 置为完成状态；用户再点击“开始游戏”，启动器从 `Client` 工作目录启动：

```text
HTGame.exe /Game/LoginAndCreate/Map/Updater/Updater_P -SAVEWINPOS=1
```

该流程下角色语音正常。`/launcher /autoplay` 最终使用相同的游戏本体、参数和工作目录，却会在资源状态机完成前启动游戏，表现为音乐和音效正常、所有角色语音缺失。先运行 `/directly`、再启动第二个 `/autoplay` 进程也不会向已有启动器实例传递开始动作。因此桥接器不能只拼接静态参数，必须等待资源状态就绪后操作官方启动器。

## 执行链

```text
解析桥接参数并隔离 Steam 原命令尾部
→ 确定安装根目录并加载可选 INI
→ 有限递归发现 NTEGame.exe 与 HTGame.exe
→ 隐藏启动 NTEGame.exe /launcher /directly
→ 定位本次启动器的 Qt 主窗口并持续抑制显示
→ 从本次 PID 对应的官方日志会话确认资源检查完成
→ 通过 MSAA 或窗口相对坐标触发“开始游戏”
→ 跟踪完整路径匹配的 HTGame.exe
→ 游戏连续消失达到防抖时间后关闭本次创建的启动器
```

## 路径发现与配置

默认安装根目录为桥接 EXE 所在目录，也可用 `--install-root` 指定。程序优先检查 INI 中配置的候选相对路径，再在安装根目录的有限深度内按文件名查找；递归过程跳过重解析点以及备份、补丁、下载、缓存和临时目录，不扫描整个磁盘。

默认入口为 `NTEGame.exe`，默认游戏本体为 `HTGame.exe`。同级或 `--config` 指定的 `NevernessToEvernessLaunchBridge.ini` 可覆盖文件名、候选路径、排除目录、搜索深度、官方启动参数、日志相对路径、资源完成标记、按钮名称、回退坐标、窗口策略和各项超时。配置路径必须保持在识别到的安装根目录内。

## 资源状态与窗口触发

桥接器以隐藏窗口方式创建官方入口，并只接受进程完整路径与发现结果一致的 `NTEGame.exe`。Qt 主窗口按本次启动器 PID、顶层窗口尺寸和 `QWindowOwnDC` 类名识别；窗口抑制线程在运行期间持续隐藏它，避免启动和游戏退出时界面弹出。

资源就绪判断读取官方 `NTEGame.log`，从最后一个 `Log Init` 会话向后核对启动参数和 `Process ID`，确保完成标记属于本次启动器而不是旧日志。默认完成标记为 `_setLocalResChecked] 0->1` 或 `onGameElementUpdateFinished] all ready`。

确认就绪后，程序先递归检查 MSAA 无障碍树并调用名称为“开始游戏”或“开始”的控件；若官方 Qt 界面没有暴露可调用控件，则向主窗口客户区的配置比例位置发送点击消息。只有在点击后发现新的、完整路径匹配的 `HTGame.exe`，启动才算成功。

## 进程与生命周期

游戏进程先按 `HTGame.exe` 名称筛选，再用 `QueryFullProcessImageNameW` 核对完整路径，避免附着到其他安装中的同名进程。桥接器保存首次发现进程的句柄以读取退出码，并持续观察路径匹配的进程集合；短暂消失不会立即结束，只有连续达到配置的退出防抖时间才进入清理。

清理阶段只处理本次桥接器创建的官方启动器：先向已识别主窗口发送正常关闭请求，等待配置的宽限时间；若官方程序只缩到托盘且配置允许，再终止该启动器进程。启动桥接器之前已经存在的官方入口不会被强制结束。

## Steam 参数与权限

Steam 启动项可使用：

```text
"<异环安装根目录>\NevernessToEvernessLaunchBridge.exe" %command%
```

桥接器先解析自己的 `--install-root`、`--config`、`--validate` 和 `--no-dialog`；遇到首个普通位置参数后，将其及后续内容整体视为 Steam `%command%` 展开的原始命令并隔离。未知的 `--xxx` 长选项仍会报错，桥接器自身参数必须放在 `%command%` 前面。

正式 EXE 包含管理员权限清单，以便在相同完整性级别控制官方启动器窗口。产物为 Windows x64 原生 C++ 单文件，并静态链接 MSVC 运行库，不要求用户安装 .NET、Python 或额外 VC++ Runtime。

## 日志与采集工具

日志固定写入 EXE 同级的 `NevernessToEvernessLaunchBridge-log/NevernessToEvernessLaunchBridge.log`，始终使用一个主要日志文件并按条目保留最近 7 天。日志记录运行标识、版本、系统、有效参数、发现路径、官方启动命令、资源状态、窗口触发方式、游戏进程变化、退出码、运行时长和完整原生异常堆栈，不复制官方日志原文或账号数据。

`NevernessToEvernessLaunchCapture.exe` 是只读采集工具，用于游戏更新后记录官方入口、子进程、命令行、工作目录和生命周期，以重新核对真实启动链；它不属于正式桥接附件。

## 更新失效边界

普通版本目录变化和有限层级调整通常可由路径发现或 INI 覆盖适配。如果官方更改资源日志格式、完成标记、Qt 窗口结构、开始按钮位置、入口或本体名称、游戏参数、工作目录、进程接力方式、登录上下文或反作弊要求，则必须重新运行采集工具并完成实机验证，不能只依赖自动测试。

更新后应依次验证官方手动启动与角色语音、桥接器隐藏触发、UAC、登录和反作弊、Steam `%command%`、游戏退出防抖、托盘启动器清理以及日志中的完整生命周期。

主要实现：[`NevernessToEverness/src/main.cpp`](../NevernessToEverness/src/main.cpp)；示例配置：[`NevernessToEverness/examples/NevernessToEvernessLaunchBridge.ini`](../NevernessToEverness/examples/NevernessToEvernessLaunchBridge.ini)；固定测试：[`NevernessToEverness/tests/`](../NevernessToEverness/tests/)。
