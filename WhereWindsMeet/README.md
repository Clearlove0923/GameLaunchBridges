# 燕云十六声直启桥接器

`WhereWindsMeetLaunchBridge.exe` 用于启动或附着到当前安装目录中的《燕云十六声》游戏本体，并在游戏退出后延迟结束自身。它适合由 Steam 等外部程序启动并跟踪桥接器的生命周期。仓库后续桥接程序统一采用“英文游戏名 + `LaunchBridge`”的文件名。

## 功能

- 从桥接器所在位置向上识别与 `launcher.exe` 或 `launcher.exe.lnk` 同级的安装根目录。
- 在安装根目录的下一级 `yysls_*` 目录中查找 `Win64r` 和 `Win64rh` 的 `yysls.exe`，兼容标准版 `yysls_medium`、极速版 `yysls_fast` 以及相同结构的后续版本。
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

输出文件位于 `Release\WhereWindsMeetLaunchBridge.exe`。只需把该 EXE 放到游戏安装根目录，即与 `launcher.exe`（或资源管理器中显示为 `launcher.exe` 的快捷方式）同级的位置。

构建后可运行 `.\tests\Test-PathDiscovery.ps1`，验证标准版、极速版和后续 `yysls_*` 目录的定位，以及日志文件夹生成。

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

## 维护边界

当前启动参数来自 2026-09-26 捕获的一次官方启动器启动命令，不是官方承诺长期兼容的公开接口。以下变化可能需要更新源码：

- `yysls.exe` 改名或安装目录结构变化；
- 官方启动器改用新的参数、工作目录、环境变量、登录上下文或反作弊初始化流程；
- 游戏改为多个进程接力，或退出期间出现新的临时进程；
- 游戏图标更新，需要重新提取后构建。

桥接器不会伪造 Steam 状态，也不绕过登录或反作弊。自包含构建、路径识别、日志保留和模拟进程生命周期可以自动验证，但真实游戏启动、UAC、反作弊和 Steam 跟踪需要在目标电脑手动验证。
