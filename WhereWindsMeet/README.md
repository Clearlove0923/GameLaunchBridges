# 燕云十六声直启桥接器

`WhereWindsMeetLaunchBridge.exe` 用于启动或附着到当前安装目录中的《燕云十六声》游戏本体，并在游戏退出后延迟结束自身。它适合由 Steam 等外部程序启动并跟踪桥接器的生命周期。仓库后续桥接程序统一采用“英文游戏名 + `LaunchBridge`”的文件名。

当前正式版本：`v1.1`。

## 功能

- 默认把桥接器所在目录作为安装范围，也可用 `--install-root` 显式指定；不依赖 `launcher.exe`、版本目录名或固定层级。
- 在安装范围内进行有限深度递归发现，默认寻找 `yysls.exe`，并排除补丁缓存、备份、下载和临时目录，避免误启动更新残留副本。
- 候选路径通过可配置的软优先级排序，不要求必须存在 `yysls_medium`、`yysls_fast`、`Engine\Binaries`、`Win64r` 或 `Win64rh`。
- 游戏已经运行时附着到同一安装目录的进程，不重复启动。
- 游戏未运行时使用本体目录作为工作目录，以 `--launch-type=launcher` 尝试启动。
- 游戏退出后默认延迟 10 秒退出。
- 在 EXE 同级创建 `WhereWindsMeetLaunchBridge-log` 文件夹，并使用其中唯一的 `where-winds-meet-launch-bridge.log`，记录每次运行的启动参数、路径、进程变化、退出结果和异常堆栈。
- 启动时及长时间运行期间每小时清理日志中超过 7 天的记录，保留同一文件内最近 7 天内容。

## 构建

需要 Windows x64 和 .NET 8 SDK。在本目录执行：

```powershell
.\build.ps1 -GameExe "D:\Games\yysls\yysls_medium\Engine\Binaries\Win64r\yysls.exe"
```

仓库已包含当前从游戏本体提取的多尺寸 `yysls.ico`，直接运行构建脚本即可生成带图标的 EXE。`-GameExe` 可选；提供后，脚本会先从指定的本机游戏 EXE 重新提取图标，适合游戏图标更新后的维护。

输出文件位于 `Release\WhereWindsMeetLaunchBridge.exe`。通常只需把该 EXE 放到包含游戏文件的安装根目录；目录结构更新后无需移动到某个固定子目录。

构建后可运行 `.\tests\Test-PathDiscovery.ps1`，验证标准版、极速版、任意新目录层级、补丁副本排除、改名配置和日志文件夹生成。

## 游戏更新兼容配置

普通资源或本体更新只要本体仍名为 `yysls.exe`、启动参数仍有效，桥接器通常无需更新。目录层级、版本目录名和 `Win64r` / `Win64rh` 变化不会影响递归发现。

如果大版本更新修改了本体文件名或启动参数，把 [`examples/WhereWindsMeetLaunchBridge.json`](examples/WhereWindsMeetLaunchBridge.json) 复制到桥接 EXE 同目录，再修改：

- `executableNames`：更新后的本体 EXE 文件名，可配置多个候选，不填写绝对路径；
- `launchArguments`：官方启动器当前实际使用的参数；
- `excludedDirectoryNames`：递归发现时跳过的补丁、缓存或备份目录；
- `preferredPathKeywords`：存在多个有效本体时的软排序关键字，不是必须路径；
- `maxSearchDepth`：最大搜索深度，范围 1–32，默认 12。

程序会在日志中记录配置、全部候选、本体大小与修改时间、最终选择和实际启动命令。若更新后启动失败，应先通过官方启动器确认游戏正常，再结合日志重新捕获启动参数；不得通过猜测固定新路径进行适配。

## 参数

| 参数 | 说明 |
|---|---|
| `--attach-only` | 不主动启动本体，只等待或附着到已启动游戏。 |
| `--startup-timeout-seconds N` | 等待游戏出现的最长时间，范围 1–1800 秒，默认 180 秒。 |
| `--exit-delay-seconds N` | 游戏消失后的退出延时，范围 0–300 秒，默认 10 秒。 |
| `--install-root "路径"` | 显式指定游戏安装根目录。 |
| `--variant Win64r` | 指定 `Win64r` 本体。 |
| `--variant Win64rh` | 指定 `Win64rh` 本体。 |
| `--dry-run` | 输出识别结果和启动命令，不启动游戏。 |
| `--no-dialog` | 出错时不显示对话框，仍写日志。 |

## 捕获官方启动参数

需要核对 DX11、DX12 或游戏更新后的官方启动链时，运行 `build-capture.ps1` 生成 `Capture\WhereWindsMeetLaunchCapture.exe`。捕获器只读取进程和 DX 相关配置，不修改官方文件。

1. 把捕获 EXE 单独复制到游戏安装根目录。
2. 确保游戏和官方启动器已经退出，再双击捕获 EXE，并允许管理员权限。
3. 在官方启动器中选择需要核对的模式并启动游戏。
4. 检测到游戏后，捕获器继续记录 45 秒并自动退出。
5. 返回同级 `WhereWindsMeetLaunchCapture-log\where-winds-meet-launch-capture.log`。

日志包含启动器和游戏本体的完整路径、脱敏后的命令行、父进程、D3D11/D3D12 模块，以及 `setting.ini` 中 DX 相关值的启动前后变化。捕获器会把命令行中的令牌、Cookie、密码、会话和密钥参数值替换为 `<redacted>`。

## 维护边界

当前启动参数来自 2026-09-26 捕获的一次官方启动器启动命令，不是官方承诺长期兼容的公开接口。以下变化可能需要更新源码：

- `yysls.exe` 改名时更新同级 JSON 的 `executableNames`；仅安装目录结构变化通常无需维护；
- 官方启动器改用新的参数、工作目录、环境变量、登录上下文或反作弊初始化流程；
- 游戏改为多个进程接力，或退出期间出现新的临时进程；
- 游戏图标更新，需要重新提取后构建。

桥接器不会伪造 Steam 状态，也不绕过登录或反作弊。自包含构建、路径识别、日志保留和模拟进程生命周期可以自动验证，但真实游戏启动、UAC、反作弊和 Steam 跟踪需要在目标电脑手动验证。
