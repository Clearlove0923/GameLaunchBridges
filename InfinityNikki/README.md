# 无限暖暖启动桥接器

`InfinityNikkiLaunchBridge.exe` 通过官方 `xstarter.exe -skiplauncher` 启动《无限暖暖》，并在真正的游戏进程退出前保持自身运行，供 Steam 等外部程序正确跟踪游戏生命周期。

当前正式版本：`v1.0`。

## 功能

- 将 EXE 放到与官方 `launcher.exe` 和版本目录同级的安装根目录后即可零参数运行。
- 每次运行都会重新扫描，并自动选择最高的点分数字版本目录中的 `xstarter.exe`；支持常见三/四段版本及更长的构建号。
- 使用安装根目录作为工作目录，以管理员权限执行 `xstarter.exe -skiplauncher`。
- 在安装根目录的有限层级内动态发现 `X6Game\Binaries\Win64\*-Win64-Shipping.exe`，并只跟踪完整路径匹配的进程，不会附着到其他目录的同名进程。
- 启动器进程提前退出不会结束桥接器；游戏进程连续消失 8 秒后才结束。
- 在 EXE 同级创建 `InfinityNikkiLaunchBridge-log` 文件夹，始终只维护其中一个 `InfinityNikkiLaunchBridge.log`。
- 日志保留最近 7 天，记录会话 ID、版本、系统与架构、参数、候选路径、实际启动命令、进程变化、退出码、运行时长和完整异常。
- 内置从官方游戏程序提取的彩色多尺寸图标，自包含单文件无需另装 .NET。

## 使用

1. 下载 `InfinityNikkiLaunchBridge_v1.0.exe`。
2. 可将文件名保留或改成 `InfinityNikkiLaunchBridge.exe`。
3. 把它放到《无限暖暖》安装根目录，即与 `launcher.exe`、`1.3.1` 等版本目录同级的位置。
4. 在 Steam 中将该 EXE 添加为非 Steam 游戏并直接启动，不需要启动参数。

如果用它替换 Steam 中另一个快捷方式的启动命令，也可使用：

```text
"D:\Path\To\InfinityNikkiLaunchBridge.exe" --auto %command%
```

桥接器会忽略 `%command%` 展开的原始命令，只使用已验证的官方启动链。

## 参数

| 参数 | 说明 |
|---|---|
| `--auto` | 自动发现安装目录；无参数时默认启用。 |
| `--validate` | 只验证路径和配置，不启动游戏。 |
| `--config "配置.json"` | 使用显式 JSON 配置；示例位于 `examples/infinity-nikki.bridge.json`。 |

## 构建与测试

需要 Windows x64 和 .NET 8 SDK。在本目录执行：

```powershell
.\build.ps1
dotnet run --project .\tests\InfinityNikkiLaunchBridge.Tests.csproj --configuration Release
.\tests\Test-PublishedBridge.ps1
```

如需从本机官方游戏程序重新提取图标：

```powershell
.\build.ps1 -GameExe "D:\Games\InfinityNikki\InfinityNikki.exe"
```

输出文件位于 `Release\InfinityNikkiLaunchBridge.exe`。

## 验证边界

- 2026-09-13 已在国服安装结构中验证 `xstarter.exe -skiplauncher` 能启动游戏并检测到 `X6Game-Win64-Shipping.exe`。
- 2026-09-28 已验证自动发现、精确进程路径匹配、七天单文件日志保留、自包含单文件构建和内嵌多尺寸图标。
- 游戏更新可能改变版本目录、启动参数、登录上下文或反作弊初始化流程；使用前应先通过官方启动器完成更新。
- 常规版本号升级、版本目录增加、游戏顶层目录改名以及 Shipping 文件名前缀变化会在每次启动时自动重新识别；若官方移除 `xstarter.exe`、更改 `-skiplauncher` 或重构 `X6Game\Binaries\Win64` 层级，则仍需更新桥接器。
- 本工具不修改游戏、启动器、登录组件或反作弊文件，也不伪造 Steam 在线状态。
