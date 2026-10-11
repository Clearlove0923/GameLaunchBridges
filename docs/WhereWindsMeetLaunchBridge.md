# WhereWindsMeetLaunchBridge 实现原理

本文说明 `WhereWindsMeetLaunchBridge.exe` 当前的路径发现、启动和生命周期管理方式。

## 执行链

```text
解析命令行与同级 JSON
→ 确定安装根目录
→ 有限递归发现 yysls.exe
→ 对候选评分并选择本体
→ 附着现有进程或以管理员权限启动
→ 跟踪完整路径匹配的进程
→ 游戏连续消失 10 秒后退出
```

## 路径发现

默认安装根目录为桥接 EXE 所在目录，也可用 `--install-root` 指定。程序默认在 12 层以内递归搜索 `yysls.exe`，跳过重解析点和 `Patch`、`BinPatch`、`Backup`、`Download`、`Temp`、`Cache` 等目录，不进行全盘扫描。

候选使用软评分排序：JSON 中靠前的 `executableNames` 优先；包含 `preferredPathKeywords` 的路径优先，默认偏好 `Win64r` 和 `Win64rh`；层级较浅的路径略优先。版本目录名、`Engine\Binaries` 和 `Win64r`/`Win64rh` 都不是必须结构。

## 启动原理

若不存在完整路径匹配的已运行游戏，桥接器选择评分最高的候选，以本体目录作为工作目录，通过 ShellExecute 和 `runas` 执行：

```text
"<发现的 yysls.exe>" --launch-type=launcher
```

同级 `WhereWindsMeetLaunchBridge.json` 可覆盖本体文件名、启动参数、排除目录、路径关键字和搜索深度。`--variant` 只按目录片段筛选候选，不接受绝对路径。Steam `%command%` 展开的原始命令被作为尾随载荷整体忽略。

## 进程与生命周期

程序从候选路径提取进程名，再用 `QueryFullProcessImageNameW` 读取同名进程的完整路径。只有路径属于候选集合的进程才算游戏，避免误附着到其他安装中的同名进程。

主动启动返回的进程对象也参与存活判断，以覆盖启动初期路径暂时不可读的情况。游戏出现后，目标进程连续消失达到默认 10 秒防抖才结束；主动启动进程的退出码会作为桥接器退出码返回。

日志固定写入 EXE 同级的 `WhereWindsMeetLaunchBridge-log/where-winds-meet-launch-bridge.log`，始终使用一个主要日志文件并按条目保留最近 7 天。

## 更新失效边界

普通目录和版本号变化通常可自动适配。本体改名或参数变化可先用 JSON 覆盖；若官方增加新的环境变量、登录上下文、反作弊初始化，禁止直接启动本体，或改成新的多进程接力方式，则需要重新捕获启动链并修改实现。

更新后应先通过官方启动器成功进入游戏，再运行 `--dry-run` 检查路径、工作目录和参数，最后实机验证 UAC、反作弊和 Steam 生命周期。

源码：[`WhereWindsMeet/src/Program.cs`](../WhereWindsMeet/src/Program.cs)；回归测试：[`WhereWindsMeet/tests/Test-PathDiscovery.ps1`](../WhereWindsMeet/tests/Test-PathDiscovery.ps1)。
