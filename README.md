# Game Launch Bridges

面向 Windows 游戏的独立启动与进程生命周期桥接器集合。
用于绕过游戏厂商不能直接启动游戏本体的限制，从而达到通过执行一个exe即可启动的目的

## 目录

| 游戏 | 目录 | 状态 |
|---|---|---|
| 燕云十六声（Where Winds Meet） | [`WhereWindsMeet/`](WhereWindsMeet/) | EXE 可用性：v1.4 已发布（[GitHub](https://github.com/Clearlove0923/GameLaunchBridges/releases/tag/WhereWindsMeet-v1.4) / [CNB](https://cnb.cool/SteamCN-GameLauncher/GameLaunchBridges/-/releases/tag/WhereWindsMeet-v1.4)）|
| 无限暖暖（Infinity Nikki） | [`InfinityNikki/`](InfinityNikki/) | EXE 可用性：v1.1 已发布（[GitHub](https://github.com/Clearlove0923/GameLaunchBridges/releases/tag/InfinityNikki-v1.1) / [CNB](https://cnb.cool/SteamCN-GameLauncher/GameLaunchBridges/-/releases/tag/InfinityNikki-v1.1)），支持默认、DX11、DX12 三种图形接口模式；官方 `xstarter.exe -skiplauncher`  |
| 卡拉彼丘（Calabiyau） | [`Calabiyau/`](Calabiyau/) | EXE 可用性：v1.3 已发布（[GitHub](https://github.com/Clearlove0923/GameLaunchBridges/releases/tag/Calabiyau-v1.3) / [CNB](https://cnb.cool/SteamCN-GameLauncher/GameLaunchBridges/-/releases/tag/Calabiyau-v1.3)）；支持放在游戏安装目录内任意层级，并会忽略和记录未知参数 |
| 异环（Neverness to Everness） | [`NevernessToEverness/`](NevernessToEverness/) | EXE 可用性：v1.0 已发布（[GitHub](https://github.com/Clearlove0923/GameLaunchBridges/releases/tag/NevernessToEverness-v1.0) / [CNB](https://cnb.cool/SteamCN-GameLauncher/GameLaunchBridges/-/releases/tag/NevernessToEverness-v1.0)）|
| 明日方舟：终末地（Arknights Endfield） | [`ArknightsEndfield/`](ArknightsEndfield/) | EXE 可用性：v1.0 已发布（[GitHub](https://github.com/Clearlove0923/GameLaunchBridges/releases/tag/ArknightsEndfield-v1.0) / [CNB](https://cnb.cool/SteamCN-GameLauncher/GameLaunchBridges/-/releases/tag/ArknightsEndfield-v1.0)），C++20 原生 Windows x64 单文件，无需 .NET 或额外 VC++ 运行库 |

## 特别说明
本仓库中的桥接器不是游戏官方组件，不修改或绕过游戏文件、登录系统或反作弊机制。游戏更新可能改变启动链，使用前请先通过官方启动器完成更新并确认游戏可正常运行。

## 开发文档

- [游戏桥接器开发 Skill：直启验证、Capture 与官方启动链分析](docs/skills/game-launch-bridge/SKILL.md)
- [燕云十六声桥接 EXE 实现原理](docs/WhereWindsMeetLaunchBridge.md)
- [无限暖暖桥接 EXE 实现原理](docs/InfinityNikkiLaunchBridge.md)
- [卡拉彼丘桥接 EXE 实现原理](docs/CalabiyauLaunchBridge.md)
- [异环桥接 EXE 实现原理](docs/NevernessToEvernessLaunchBridge.md)
- [终末地桥接 EXE 实现原理](docs/ArknightsEndfieldLaunchBridge.md)
