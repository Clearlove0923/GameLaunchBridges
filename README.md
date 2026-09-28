# Game Launch Bridges

面向 Windows 游戏的独立启动与进程生命周期桥接器集合。每款游戏使用一个顶层目录管理自己的源码、构建脚本和说明，避免将不同游戏的路径、参数和进程规则混在一起。

## 目录

| 游戏 | 目录 | 状态 |
|---|---|---|
| 燕云十六声（Where Winds Meet） | [`WhereWindsMeet/`](WhereWindsMeet/) | EXE 可用性：[`v1.0` 已发布](https://github.com/Clearlove0923/GameLaunchBridges/releases/tag/v1.0)，标准版路径已验证，极速版路径测试通过但真实启动待验证。已发现 Bug：暂无已确认 Bug。 |
| 无限暖暖（Infinity Nikki） | — | EXE 可用性：暂无。已发现 Bug：不适用。 |
| 异环（Neverness to Everness） | — | EXE 可用性：暂无。已发现 Bug：不适用。 |

## 仓库约定

- 每款游戏必须放入独立的游戏名称目录。
- 每个发布 EXE 统一使用“英文游戏名 + `LaunchBridge`”命名，例如 `WhereWindsMeetLaunchBridge.exe`、`InfinityNikkiLaunchBridge.exe`。
- 游戏路径、启动参数、工作目录、进程名称和退出条件必须在对应游戏目录内维护。
- 游戏图标可以随对应桥接器源码提交；不得提交游戏程序、令牌、Cookie、账号数据或运行日志。
- 发布前必须分别验证构建成功和真实游戏生命周期；仅通过静态检查不能证明游戏能够启动。
- 新增游戏时应在对应目录记录已验证的版本、启动链、日志位置和已知限制。

本仓库中的桥接器不是游戏官方组件，不修改或绕过游戏文件、登录系统或反作弊机制。游戏更新可能改变启动链，使用前请先通过官方启动器完成更新并确认游戏可正常运行。
