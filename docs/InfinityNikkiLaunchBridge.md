# InfinityNikkiLaunchBridge 实现原理

本文说明 `InfinityNikkiLaunchBridge.exe` 当前的启动器发现、游戏进程识别和生命周期管理方式。

## 执行链

```text
解析命令行或显式 JSON
→ 发现最高版本目录中的 xstarter.exe
→ 发现 X6Game\Binaries\Win64\*-Win64-Shipping.exe
→ 记录启动前目标进程 PID
→ 执行 xstarter.exe -skiplauncher
→ 等待新的完整路径匹配进程
→ 游戏连续消失 8 秒后退出
```

## 启动器发现

零参数模式从桥接 EXE 所在目录开始，有限检查上级目录、名称包含 `Nikki`/`暖暖` 的直接子目录、注册表卸载项和少量常见安装位置，不扫描整个磁盘。

程序只把点分数字目录识别为版本目录，例如 `1.3.1` 或 `1.10.0.12345`，并逐段按整数比较，所以 `1.10.0` 正确高于 `1.9.0`。最终选择最高版本目录内的 `xstarter.exe`。显式 JSON 中的启动器路径必须是不能逃离 `launcherRoot` 的相对路径。

## 游戏本体发现

程序检查安装根目录、其 `InfinityNikki` 子目录，以及名称包含 `Nikki`、`暖暖` 或含 `X6Game` 的直接子目录，然后在其中寻找：

```text
X6Game\Binaries\Win64\*-Win64-Shipping.exe
```

Shipping 文件名前缀可以变化，但 `X6Game\Binaries\Win64` 目前仍是必要结构。

## 启动原理

自动模式以安装根目录为工作目录，通过 `runas` 执行最高版本的 `xstarter.exe -skiplauncher`。`--dx11` 或 `--dx12` 会先移除已有的同类图形参数，再追加唯一的 `-dx11` 或 `-dx12`；不指定时由游戏自行选择。

Steam `%command%` 展开的原始命令被整体忽略，不能覆盖桥接器的发现或启动配置。

## 进程与生命周期

协调器在启动前记录所有路径匹配的目标 PID，默认拒绝在游戏已经运行时再次启动。启动后只接受进程名正确、完整路径属于已发现 Shipping EXE，且 PID 不在启动前基线中的进程。

`xstarter.exe` 提前退出不代表游戏结束。桥接器等待真正的 Shipping 进程出现，持续记录本次会话中的目标 PID；所有目标进程连续消失 8 秒后读取可用退出码并结束。

日志固定写入 EXE 同级的 `InfinityNikkiLaunchBridge-log/InfinityNikkiLaunchBridge.log`，始终使用一个主要日志文件并按条目保留最近 7 天。

## 更新失效边界

常规点分数字版本升级可自动适配。若 `xstarter.exe` 被移除、改名或离开数字版本目录，`-skiplauncher` 失效，官方增加新的登录或反作弊上下文，或重构 `X6Game\Binaries\Win64`，则需要更新实现。

更新后应先通过官方启动器完成更新，再运行 `--validate` 检查路径和参数，最后实机验证 UAC、登录、反作弊和 Steam 生命周期。

主要源码：[`Program.cs`](../InfinityNikki/src/Program.cs)、[`InfinityNikkiAutoDiscovery.cs`](../InfinityNikki/src/InfinityNikkiAutoDiscovery.cs)、[`GameExecutableLocator.cs`](../InfinityNikki/src/GameExecutableLocator.cs)、[`BridgeCoordinator.cs`](../InfinityNikki/src/BridgeCoordinator.cs)。
