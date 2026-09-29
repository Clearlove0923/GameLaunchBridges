# Game Launch Bridges

面向 Windows 游戏的独立启动与进程生命周期桥接器集合。
用于绕过游戏厂商不能直接启动游戏本体的限制，从而达到通过执行一个exe即可启动的目的

## 目录

| 游戏 | 目录 | 状态 |
|---|---|---|
| 燕云十六声（Where Winds Meet） | [`WhereWindsMeet/`](WhereWindsMeet/) | EXE 可用性：[`v1.0` 已发布](https://github.com/Clearlove0923/GameLaunchBridges/releases/tag/v1.0)，标准版路径已验证，极速版路径测试通过但真实启动待验证。已发现 Bug：暂无已确认 Bug。 |
| 无限暖暖（Infinity Nikki） | [`InfinityNikki/`](InfinityNikki/) | EXE 可用性：[`v1.0` 已发布](https://github.com/Clearlove0923/GameLaunchBridges/releases/tag/InfinityNikki-v1.0)，官方 `xstarter.exe -skiplauncher` 启动链与游戏进程检测已验证。已发现 Bug：暂无已确认 Bug。 |
| 异环（Neverness to Everness） | — | EXE 可用性：暂无。已发现 Bug：不适用。 |

## 特别说明
本仓库中的桥接器不是游戏官方组件，不修改或绕过游戏文件、登录系统或反作弊机制。游戏更新可能改变启动链，使用前请先通过官方启动器完成更新并确认游戏可正常运行。
