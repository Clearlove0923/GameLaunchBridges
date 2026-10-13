# Game Launch Bridges

面向 Windows 游戏的独立启动与进程生命周期桥接器集合。
用于绕过游戏厂商不能直接启动游戏本体的限制，从而达到通过执行一个exe即可启动的目的

## 目录

| 游戏 | 目录 | 状态 |
|---|---|---|
| 燕云十六声（Where Winds Meet） | [`WhereWindsMeet/`](WhereWindsMeet/) | EXE 可用性：v1.3 已发布（[GitHub](https://github.com/Clearlove0923/GameLaunchBridges/releases/tag/WhereWindsMeet-v1.3) / [CNB](https://cnb.cool/SteamCN-GameLauncher/GameLaunchBridges/-/releases/tag/WhereWindsMeet-v1.3)），提供 Windows x64 原生 C++ 单文件程序，无需 .NET 或额外 VC++ Runtime，支持有限深度递归发现、JSON 兼容配置和 Steam `%command%` 尾随命令隔离；自动回归已通过，v1.3 真实登录、UAC、反作弊和 Steam 生命周期待复验。已发现 Bug：暂无已确认 Bug。 |
| 无限暖暖（Infinity Nikki） | [`InfinityNikki/`](InfinityNikki/) | EXE 可用性：v1.1 已发布（[GitHub](https://github.com/Clearlove0923/GameLaunchBridges/releases/tag/InfinityNikki-v1.1) / [CNB](https://cnb.cool/SteamCN-GameLauncher/GameLaunchBridges/-/releases/tag/InfinityNikki-v1.1)），支持默认、DX11、DX12 三种图形接口模式；官方 `xstarter.exe -skiplauncher` 启动链与游戏进程检测已验证。已发现 Bug：暂无已确认 Bug。 |
| 卡拉彼丘（Calabiyau） | [`Calabiyau/`](Calabiyau/) | EXE 可用性：v1.1 已发布（[GitHub](https://github.com/Clearlove0923/GameLaunchBridges/releases/tag/Calabiyau-v1.1) / [CNB](https://cnb.cool/SteamCN-GameLauncher/GameLaunchBridges/-/releases/tag/Calabiyau-v1.1)）；标准版与腾讯版均已实机验证，官方入口可自动隐藏到托盘并在后台初始化 SDK，随后无需点击即可直接进入游戏；反作弊、游戏进程跟踪、后台入口清理和 3 秒退出防抖均正常。已发现 Bug：首次使用前必须通过官方启动器成功进入游戏一次以生成登录缓存和启动记录。 |
| 异环（Neverness to Everness） | [`NevernessToEverness/`](NevernessToEverness/) | EXE 可用性：v1.0 已发布（[GitHub](https://github.com/Clearlove0923/GameLaunchBridges/releases/tag/NevernessToEverness-v1.0) / [CNB](https://cnb.cool/SteamCN-GameLauncher/GameLaunchBridges/-/releases/tag/NevernessToEverness-v1.0)），提供 Windows x64 原生 C++ 单文件程序，无需 .NET、Python 或额外 VC++ Runtime；支持官方资源检查完成后隐藏触发“开始游戏”、Steam `%command%` 尾随命令隔离和游戏退出后的启动器清理。已发现 Bug：官方启动器直接使用 `/autoplay` 会导致全部角色语音缺失，桥接器已避开该启动方式。 |

## 特别说明
本仓库中的桥接器不是游戏官方组件，不修改或绕过游戏文件、登录系统或反作弊机制。游戏更新可能改变启动链，使用前请先通过官方启动器完成更新并确认游戏可正常运行。

## 开发文档

- [燕云十六声桥接 EXE 实现原理](docs/WhereWindsMeetLaunchBridge.md)
- [无限暖暖桥接 EXE 实现原理](docs/InfinityNikkiLaunchBridge.md)
- [卡拉彼丘桥接 EXE 实现原理](docs/CalabiyauLaunchBridge.md)
- [异环桥接 EXE 实现原理](docs/NevernessToEvernessLaunchBridge.md)
