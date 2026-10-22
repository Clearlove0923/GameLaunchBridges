# Game Launch Bridges
- 面向 Windows 游戏的独立启动与进程生命周期桥接器集合。
- 用于绕过游戏厂商不能直接启动游戏本体的限制，从而达到通过执行一个exe即可启动的目的

## 目录

| 游戏 | 目录 | 状态 |
|---|---|---|
| 燕云十六声（Where Winds Meet） | [`WhereWindsMeet/`](WhereWindsMeet/) | EXE 可用性：v1.4 已发布（[GitHub](https://github.com/Clearlove0923/GameLaunchBridges/releases/tag/WhereWindsMeet-v1.4) / [CNB](https://cnb.cool/SteamCN-GameLauncher/GameLaunchBridges/-/releases/tag/WhereWindsMeet-v1.4)）|
| 无限暖暖（Infinity Nikki） | [`InfinityNikki/`](InfinityNikki/) | EXE 可用性：v1.1 已发布（[GitHub](https://github.com/Clearlove0923/GameLaunchBridges/releases/tag/InfinityNikki-v1.1) / [CNB](https://cnb.cool/SteamCN-GameLauncher/GameLaunchBridges/-/releases/tag/InfinityNikki-v1.1)），支持默认、DX11、DX12 三种图形接口模式；官方 `xstarter.exe -skiplauncher`  |
| 卡拉彼丘（Calabiyau） | [`Calabiyau/`](Calabiyau/) | EXE 可用性：v1.3 已发布（[GitHub](https://github.com/Clearlove0923/GameLaunchBridges/releases/tag/Calabiyau-v1.3) / [CNB](https://cnb.cool/SteamCN-GameLauncher/GameLaunchBridges/-/releases/tag/Calabiyau-v1.3)）；支持放在游戏安装目录内任意层级，并会忽略和记录未知参数 |
| 异环（Neverness to Everness） | [`NevernessToEverness/`](NevernessToEverness/) | EXE 可用性：v1.1（[GitHub](https://github.com/Clearlove0923/GameLaunchBridges/releases/tag/NevernessToEverness-v1.1) / [CNB](https://cnb.cool/SteamCN-GameLauncher/GameLaunchBridges/-/releases/tag/NevernessToEverness-v1.1)），Windows x64 C++ 单文件；资源检查失败恢复窗口，英文报错追加中文提示，重复双击可重新接管安装范围内的旧桥接器。验证边界：已通过自动回归，用户反馈超时窗口恢复符合预期；游戏运行中重新接管及 Steam 连续状态尚未实机验证。已知限制：待更新资源仍须在官方启动器手动更新；接管已运行游戏后不强制清理旧启动器。 |
| 明日方舟：终末地（Arknights Endfield） | [`ArknightsEndfield/`](ArknightsEndfield/) | EXE 可用性：v1.0 已发布（[GitHub](https://github.com/Clearlove0923/GameLaunchBridges/releases/tag/ArknightsEndfield-v1.0) / [CNB](https://cnb.cool/SteamCN-GameLauncher/GameLaunchBridges/-/releases/tag/ArknightsEndfield-v1.0)），C++20 原生 Windows x64 单文件，无需 .NET 或额外 VC++ 运行库 |

## 特别说明
- 本仓库中的桥接器不是游戏官方组件，不修改或绕过游戏文件、登录系统或反作弊机制。
- 桥接器只负责完整走过游戏启动流程，不会修改游戏文件，更不会修改内存数据
- 游戏更新可能改变启动链，如果官方有改动需要重新适配，使用前请先通过官方启动器完成更新并确认游戏可正常运行。
- 桥接器不会全盘扫描用户文件，只会在放置位置进行有限查找发现，建议将游戏相关文件都放在一个文件夹

