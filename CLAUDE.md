# BlueOath Rebirth — Claude 工作说明

某已关服 Unity IL2CPP + xLua 手游的本地离线复原工程。.NET 8 本地服务端 + x86 原生注入 Payload + Python TLS 回环代理 + WPF 启动器 + xLua Mod。
上游仓库 `LunarConcerto/BlueRebirth`，默认分支是 `master`（没有 `main`）。机器专属的环境状态与本地约定写在 `CLAUDE.local.md`（不进版本控制）。

## 1. 项目如何运行

四个部件：

- **服务端** `src/BlueOath.Server`：Generic Host 控制台程序，只监听 127.0.0.1。三个监听器：主端口（SDK 引导 HTTP，`--port=0` 随机）、游戏登录 TCP（`--game-login-port`，脚本与启动器固定 7201，NetSocket 帧 + protobuf TMessage）、GM 控制台 HTTP（`--gm-port`，固定 9780）。stdout **只允许**第一行 ready JSON，启动器/脚本/测试都靠它取真实端口。配置从 `--client-path` 下 `blueoath_Data/StreamingAssets/config/config_*.db` 读取（SQLite，jsonbytes 逐字节 XOR 0x55），存档写 `--data` 目录的 `profiles.db`。协议按方法名前缀路由到 `Protocols/` 下的 IGameModule，未命中回落 OfflineStubModule。
- **原生组件** `native/`：Injector 用 CreateProcess 挂起 + CreateRemoteThread(LoadLibraryW) 注入 Payload；Payload 每 500ms 对进程内模块做 IAT 钩子：游戏域名 getaddrinfo → 127.0.0.1、curl https 降级 http、UnityTLS 信任补丁、伪造 SDK 登录回调、跳过热更、伤害公式补丁、钩 lua_pcallk 执行 `Mods/bootstrap.lua`。配置来自同目录 `bootstrap.ini`（只读 `[redirect]` `[trust]` `[mods]` 三节，`[sdk]` `[debug]` 是死配置）。所有补丁按 JP 1.4.0 的文件 SHA-256 门控，哈希在 `baseline.json` 与源码常量里。
- **TLS 代理** `tools/tls-loopback-proxy.py`：仅标准库 asyncio + ssl，TLS 1.2 终止后转发到服务端主端口。当前产品钩子里 getaddrinfo 一律指向服务端 HTTP 端口，代理端口只是回退项，实际几乎无流量。
- **启动器** `src/BlueOath.Launcher.Wpf`（自包含单文件发布）与 `src/BlueOath.Publisher`（生成发布整合包）。

一键流程（`run-game.bat` 与启动器同构）：清理残留进程 → 服务端 `--tls-material-only` 生成自签证书 → 启动服务端读 ready JSON → 启动 Python 代理读 ready JSON → 写 `bootstrap.ini` 并用 Injector 校验 GameAssembly.dll 哈希后注入 → 客户端经引导 HTTP 拿服务器列表 → TCP 7201 完成 `player.Login` → Payload 强制切到主界面。

## 2. 启动路径与日志位置

| 路径 | 入口 | 依赖 | 日志 |
| --- | --- | --- | --- |
| 发布整合包 | `blueoath\启动游戏.bat` | 包内自带 server/、native/、tools/python；只需 PATH 上有 dotnet 8 运行时 | `blueoath\native\BlueOath.Payload.log`、`blueoath\server\game-login.log`、`blueoath\runtime\debug\<时间戳>\traffic\`；界面「服务器/代理」Tab 不落盘 |
| 源码脚本 | `run-game.bat` → `tools/debug-game.ps1 -SkipBuild` | 必须先 `dotnet build`；需要 `native\bin-x86\{BlueOath.Injector.exe,BlueOath.Payload.dll}`；PATH 上 python ≥ 3.10 | `native\bin-x86\BlueOath.Payload.log`、`runtime\debug\<时间戳>\{server.stdout.log,server.stderr.log,proxy.stderr.log,traffic\}`、`src\BlueOath.Server\bin\Debug\net8.0\game-login.log` |
| 源码启动器 | `dotnet run --project src\BlueOath.Launcher.Wpf` | 同上；首次需在设置页「恢复默认设置」→「保存」（bin 里的 settings 模板路径为空） | 同发布包，但根目录换成仓库根 |
| IDE 调试 | `start-client.bat`（服务端另起，`--port=7080`） | 同源码脚本 | 同源码脚本 |

客户端侧：`blueoath\blueoath\debug.log` / `console.log` 是 CEF 日志，通常只有 GPU 噪音。GM 控制台 `http://localhost:9780` 可看实时日志并执行 add_currency / add_ship / add_item 等命令。

**目录布局（硬性约定）**：源码脚本（`tools/debug-game.ps1`、`inject-game.ps1`、`baseline.ps1`）、`baseline.json`、单元测试与 `BlueOath.Tools` 都硬编码：日服客户端在 `<repo>\blueoath\blueoath\blueoath.exe`，国服客户端在 `<repo>\苍蓝誓约\clsy\clsy.exe`（两个目录都被 .gitignore 忽略）。只有 WPF 启动器同时兼容 `<root>\blueoath\blueoath.exe`（整合包布局）。客户端放错一层时，单元测试会因加载不到 `config_*.db` 大面积失败（12 项），`run-game.bat` 会在注入前报客户端缺失。服务端的装备 Mod 根目录是 `--client-path` 的父目录下的 `Mods\`，即 `<repo>\blueoath\Mods`，本机用目录联接指向 `<repo>\Mods`。两条启动路径共用 7201/9780 并互相杀进程，不能同时运行。

## 3. 文档与代码不一致处（以代码为准）

- README 的 KCP over UDP 端点已删除；`--kcp-game-login-port` 只解析不生效，真实客户端走 TCP 7201。
- README 的「xinput 劫持」不存在，注入是远程线程 LoadLibrary。
- README 说 `runtime/jp/gm-goods.json` 等可数据驱动；实际 gm-goods/gm-mails/build-pools/announcements 都是程序集内嵌资源，改动必须落在 `src/BlueOath.Server/*.json` 并重新构建。
- 发布文档说启动器框架依赖；实际启动器自包含，框架依赖的是包内 `server/`。
- `tools/build-native.ps1` 注释说 VS2022 会在 MSVCP140 崩溃；这是静态 CRT 之前的历史结论，现在 CMake 强制 `/MT` 且 CI 用 windows-2022 发布，VS2022 可用。该脚本还会把 cmake 配置+构建跑两遍，属已知冗余。
- `region=cn` 全线不可用：无国服客户端、ConfigDbLoader 硬编码 `blueoath_Data`、Payload 哈希门控只认 JP、启动器硬编码 `--region=jp`。

## 4. 修复问题的流程约定

**反馈问题时需要**：启动路径、复现步骤、预期、截图、版本（源码给 `git rev-parse HEAD`，发布包给启动器版本号），以及上表对应的日志。最省事：打包最新 `runtime\debug\<时间戳>` 目录 + Payload.log + game-login.log。数据类问题附 `profiles.db` 与账号名。

**定位思路**：从 game-login.log 找协议名 → `Protocols/MessageRouter.cs` 前缀 → 对应 Module/Service；对照 `docs/lua-catalog` 里的客户端 Lua 确认客户端期望。诊断用工作流：服务端逻辑 / 配置数据 / 客户端 Lua 三个角度独立调查，再裁决根因。

**修改约定**（详见 `docs/development/code-conventions.md`）：用 ILogger 禁止 Console；stdout 只允许 ready JSON；帧诊断走 `GameLoginFileLoggerProvider.Category`；`TreatWarningsAsErrors` 已开，构建必须 0 警告 0 错误。每个修复补一条 `src/BlueOath.Tests/Program.cs` 里的单元测试，协议级改动补集成测试。

**验证命令**（`run-game.bat` 固定 `-SkipBuild`，不会替你编译）：

```powershell
dotnet build .\BlueOath.Local.sln
dotnet run --project .\src\BlueOath.Tests\BlueOath.Tests.csproj --no-build
dotnet run --project .\src\BlueOath.Tests\BlueOath.Tests.csproj --no-build -- --integration
.\run-game.bat
```

注意 `dotnet test` 对本仓库不执行任何用例（Tests 项目是自定义宿主，没有 Microsoft.NET.Test.Sdk）。按主题单跑：`-- --time-settlement`、`-- --vow`、`-- --production`、`-- --cheats`、`-- --real-cost`、`-- --shop-stock`、`-- --sweep`、`-- --save-editor`、`-- --outpost`、`-- --resource-cost`、`-- --battle-stats`、`-- --bath-gift`、`-- --production-speedup-mod`、`-- --building-integration` 等（见 Program.cs 顶部的参数分支）。

**测试基线**：默认用例的 `hero advance preserves neighbors and unbinds consumed equipment`，以及 `--integration` 的 fashion synchronization、traditional construction、building construction and hero assignment persist、hero remould、hero gift legacy affection 在 `master` 上同样失败（依赖日服 1.4.0 配置中不存在的数据或既有问题），评估改动时对照这些基线。集成测试启动与测试项目同一构建配置（Debug / Release）的服务端；游戏正在运行（Debug 产物被占用）时可以用 `-c Release` 构建和测试。结构性改动的交接说明见 `docs/development/time-and-options.md`。

**按时间流逝的功能**（心情、宿舍/浴场回复、秘书舰好感、浴券、资源楼与道具工厂产出、工人体力回复、祈愿墙每日重置）统一在 `Protocols/Services/TimeSettlement.cs` 的纯函数 `Settle` 里结算，产出与体力公式在 `BuildingProduction.cs`。约定：每个协议在账号锁内先 `SettleLockedAsync` 落盘再执行业务；任何写回的锚点不得早于 `LastSettleTime`；落盘的变化必须在同一应答里推送（建筑与舰娘按 [建筑, 舰娘, 建筑] 顺序，业务失败也要推结算结果）。新增按时间推进的功能要并入 `Settle`，不要在单个协议里自己算经过时间。

**工人体力**（货币 21，日服メカニカルメダル / 国服工匠体力，存 `PlayerBuilding.WorkerStrength` 万分制 + `WorkerUpdateTime` 锚点 + `WorkerStrengthCarry`）按客户端 GetCurStrengthReal 回复，被体力加速、配方 [5,21,n]、建造/升级 costwork 扣减；扣之前先 `BuildingProduction.AdvanceWorker`。体力加速还按实际加速秒数扣本楼驻守舰娘心情。「奥斯能量」图标那个是电力（货币 19），不随时间变化。

**作弊选项**（服务端、启动器与脚本的默认值是 `CheatOptions.Default`：除 `--cheat-battle` 外的作弊开启，即离线版原来的规则；`--real-resource-cost` 开启，`--real-shop-stock` 与实验功能关闭。记录参数的默认值是 false，`CheatOptions.None` 供测试显式构造）：服务端开关 `--cheat-x`、`--cheat-x=on|off`、`--no-cheats`（后出现的为准）→ `CheatOptions` → `SettlementRules.Omit*` / `VowRules.OmitCooldown` 或由服务直接读 `GameServices.Cheats`，ready JSON 回显 `cheats`；启动器设置页「作弊选项」逐项显式传 `=on/off`，`run-game.bat` 用 `-NoCheats` / `-NoCheatXxx` / `-CheatXxx`，启动器的开关表在 `LaunchConfig.Options`。测试经 `ServerOptions.Parse` 构造服务时按原规则加 `--no-cheats`，按免费规则再加 `--real-resource-cost=off`（`--no-cheats` 不动按原规则的选项与实验功能）。`--cheat-production / --cheat-strength / --cheat-vow / --cheat-mood` 跳过时间；`--cheat-medals` 探索每抽固定 100 个精鋭戦姫勲章（默认按 `config_ship_main.extract_reward`：SSR 25、SR 5）；`--cheat-drops` 掉落池可堆叠物品每项 +600～2000（默认按 `config_drop_item`，见 `DropPoolResolver.Resolve(bulkBonus)`）；`--cheat-sweep` 扫荡作战开始即完成、前哨加速不限次数立即产出（关闭时按真实时间）；`--cheat-battle` 出击沿用 `ShipMainLoader.Leveled`（base + levelup ×(等级−1)，约放大百倍）的旧属性，关闭时 `copy.StartBase` 按 `Services/BattleStats.cs`（客户端 HeroBasicAttr 公式 + 强化、改造、装备、好感）编码并带当前耐久，测试 `-- --battle-stats`。Payload 的 damageFac 补丁不是作弊（离线时 `actSkillInfo.damageFac` 未初始化，去掉会全部 MISS）。加载档案不再补满 17553 与基地建材，建造/升级按 `config_buildinglevelup.rawmaterial*` 扣建材（`BuildingService.TryChargeMaterials`）。

**养成消耗**：装备强化/改修、突破、改造、共鸣、誓约戒指、送礼、建材、浴币、祈愿石，以及实验室天赋、修理金币、前哨升级 item_cost 总是扣；技能升级教材与经验道具只在 `--real-resource-cost` 开启时扣（关闭时免费且不消耗，经验道具仍要求背包里有）。后五项按日服客户端的预检算价（`Services/UpgradeCosts.cs`），都走 `CostLogic.TryPay` 全部够才扣；不足、满级或没有材料时回 Err 并在应答前重推玩家信息与背包（客户端不自己改背包数，成功时的背包推送即可纠正）。测试 `-- --resource-cost`。

**原规则选项**（方向与作弊相反，关闭即离线版的免费规则；`--real-resource-cost` 默认开，`--real-shop-stock` 默认关）：`--real-resource-cost`（`CostLogic.TryPay` 全部够才扣；技能升级与经验道具见上；探索按 config_extract_ship 的 expend/new_ten_expend、商店按 `ShopPricing` 分档价格、出击按 `SortieCost`（config_copy_display，只在 BattleMode 1 扣、余额不足扣到 0 不拒绝））与 `--real-shop-stock`（`ShopStock.cs`：随机商店 1/5 按权重抽陈列、购买扣库存、UTC 定时补货、手动刷新免费→券 10303→钻石；状态存 `PlayerAccount.Shop`，在 `Settle` 第 4 步推进）。两者都在 ready JSON 的 `cheats` 里回显（`realResourceCost` / `realShopStock`），启动器在「资源与商店」下，脚本 `-NoRealResourceCost`、`-RealShopStock`。**实验功能** `--exp-full-battle-stats`（`CheatOptions.FullBattleStats`，回显 `fullBattleStats`，启动器「实验功能」，脚本 `-FullBattleStats`）只在 `--cheat-battle` 关闭时生效：`BattleStats.ComputeFull` 按 `config_prop.enum_name` 与 `NpcAssistFleetManager:CreateNpcShip4Battle` 额外下发 21/24/25/47/210/3101 与突破增量（81/82/84/88–90，此时 `PlaneNum` 只含 `plane_number`）及 method 3 突破效果，临时舰用 `config_assist_ship_info` 全部属性与技能；玩家舰 27 航速、39、62/63、213 单位未定不发。测试 `-- --real-cost`、`-- --shop-stock`。

**扫荡作战**：`MopUpModule` + `SweepLogic`，状态存 `PlayerAccount.Sweep`；不进 `Settle`（领取时按 `CompletedRuns` 结算）；登录同步推送含 `mopUp.GetMopUpData`。测试 `-- --sweep`。

**GM 存档编辑**：`/save` 页（内嵌资源 `save-editor.html`）+ `/api/save*`，逻辑在 `SaveEditor`，道具名称来自 `ItemCatalogLoader`（按 `config_table_index` 合并所有背包表），括号里的简体中文名来自内嵌资源 `item-names-zh.json`（`ZhNameCatalog`，由 `tools/export-zh-names.py` 从国服配置与 `tools/zh-names-extra.json` 生成），经 `GmCommandHandler.EditSaveAsync` 加锁改档，在线时用 `SessionPushHub` 给会话补发玩家信息与背包（处理请求期间推迟到请求写完）。存档在 `accounts` 表，`profiles` 表是旧格式（一直为空）。测试 `-- --save-editor`。

**客户端 Mod**：`Mods/production-speedup-fix.mod` 修日服加速页「确认」在加速量超过剩余时间时无反应（原版只弹 UILayer.MAIN 层的 3200004 浪费确认框，从不发请求）。`Mods/wish-cooldown-tip-fix.mod` 是「许愿墙」作弊的界面配套（服务端冷却为 0 时不再弹出客户端自己算的冷却）。新增 Mod 要同时加进 `Mods/bootstrap.lua` 的 entries 列表（它不读 mod.json 的 enabled）。

**分支、提交与 PR**：

- 分支名 `fix/<issue号>-<简述>` 或 `feat/<issue号>-<简述>`，从 `master` 切出。提交信息带 issue 号（没有 issue 时写清问题现象），末尾带当前模型的 `Co-Authored-By: Claude <模型名> <noreply@anthropic.com>` 行。
- 推送到 fork：`git push -u origin <分支>`；PR 的 base 是上游 `master`，建 PR 地址：`https://github.com/LunarConcerto/BlueRebirth/compare/master...<fork 用户名>:<仓库名>:<分支>?expand=1`。本机没装 gh CLI，装了（`winget install GitHub.cli` + `gh auth login`）才能用 `gh pr create`。
- PR 描述按 issue 模板结构：问题、复现、根因、修法、验证方式；末尾带 `🤖 Generated with [Claude Code](https://claude.com/claude-code)`。
- **CI 不会自动跑普通修复 PR**：`.github/workflows/ci.yml` 的 push 与 pull_request 都带 paths 过滤，只有 `src/BlueOath.Launcher.Wpf/version.txt` 或 `VersionInfo.g.cs` 变更才触发。本地验证命令就是门禁。
- **修复 PR 不要动版本号**：`version.txt` 是唯一版本源，只由维护者发版时用 Publisher `--increment-version` 递增。`VersionInfo.g.cs` 是生成文件不要手改。
- 合并后同步 fork：`git remote add upstream https://github.com/LunarConcerto/BlueRebirth.git`（一次性），然后 `git checkout master && git pull upstream master && git push origin master`。

## 5. 国服（cn）支持现状与待办（2026-10-08 代码审计，7 个代理交叉核验）

**结论**：仓库从未真正跑通过国服。协议层对国服不陌生（TMessage/NetSocket 帧、TArgLogin 新增字段 6-9 都能兼容，`TestServer\raw-protobuf\net_type.proto` 与 `GameLoginProtocol.cs` 逐字段相同），但从脚本到 Payload 全是日服写死。手上的 1.5.120 客户端与基线 1.5.20 哈希不同，不过**即使换回 1.5.20 下面大部分改动也逃不掉**。不要在未完成第 5 项前向国服客户端注入：Payload 有几处无哈希门控的裸 RVA 调用，国服进程大概率崩溃或静默写坏内存。

按执行顺序：

1. **干净客户端**：社区包的 `lua\` 把发包全禁了（`lua\service\baseservice.lua`、`lua\socket_net.lua` 伪造连接成功），`version.dll` 自带 getaddrinfo/证书钩子，永远不会向 7201 发 player.Login。需要去掉这些文件的干净副本；首次起游戏需看日志确认 Lua 回退到 `clsy_Data\StreamingAssets\bundles\share\lua`（CustomLoader 优先级未实证）。
2. **基线**：`baseline.json` 的 cn 行要**替换**成 1.5.120 的四个哈希，不要追加第二条 cn 行（`tools/inject-game.ps1:45-47` 遇到多条会得到空哈希，Injector 对空哈希直接跳过校验；启动器只取第一条）。不要跑 `tools/baseline.ps1`，它整文件覆盖且把 cn 键写成绝对路径。1.5.120 哈希：clsy.exe `F3475B64…7327`、GameAssembly.dll `12AF03D6…6868`、global-metadata.dat `FC5EB4DA…DA93`、xlua.dll `A0CE0C94…59BB`（完整值用 Get-FileHash 重算）。
3. **服务端 [code-change]**：`Protocols/ConfigDbLoader.cs:24-28` 硬编码 `blueoath_Data`，国服是 `clsy_Data`，现在 `--region=cn` 会带着 70 张空表照常打印 ready JSON（`LoadRows` 对缺文件静默 return）。改为探测 `*_Data` 或按 Region 选名，并把「配置目录不存在」改成报错；同步 `Tests/Program.cs` 里的调用点。国服 config_*.db 仍是 DBObject + XOR 0x55，解码不用动。
4. **脚本 [code-change]**：`tools/debug-game.ps1` 加 `-Region`、`-GameLoginPort`、`-GmPort`、`-DataRoot`、`-NativeDir`（现在客户端路径、`--region=jp`、7201/9780、`-Region jp` 全写死；清理步骤会杀掉所有 blueoath/clsy 进程与所有服务端）；`run-game.bat`/`start-client.bat` 透传 `%*`；`inject-game.ps1` 加 `-NativeDir`。建议端口：jp 7201/9780，cn 7202/9781，IDE 调试 HTTP jp 7080 / cn 7081；数据目录 `runtime\jp` / `runtime\cn`。
5. **Payload 安全门控 [code-change，注入国服前必做]**：`hooks_product.cpp` 的 `TrySetSimulationMode`（:922-934，裸调 new_sdk+0x3CE20，国服该处是指令中段）、`CloseSdkWebView`（:818-827）、`TryApplyAttachedFleetsFix`（:1220-1243，仅凭两字节特征就写代码）、`TrySetReview`（:1186-1187 无保护写）都没有哈希门控。建议在主循环入口按 GameAssembly 哈希选整套补丁表，未知客户端只保留 IAT 重定向 + UnityTLS 补丁。国服 new_sdk 的 login/setSimulationInfo/closeCustomWebView/initSDK/getServerList 都是具名导出，改成 GetProcAddress 可免去 new_sdk 逆向。另：`TryApplySdkTlsPatches`（:936-945）按日服 sdk_ui/new_sdk 哈希门控，国服两库哈希不同，curl https→http 降级钩子根本装不上，只改 :208-210 的域名白名单无效，需把期望哈希扩成「日服或国服」集合；`lua_mod_loader.cpp:36-37` 的 xlua 白名单加国服哈希即可（lua_pcallk 两版 RVA 与前 14 字节相同）。
6. **逆向 [reverse-engineering]**：国服 GameAssembly 侧仍需定位 BabelTimeSDKManager.Login、StageMgr.Goto、NetLogic/SDKManager TypeInfo 槽位（`IsGameNetworkConnected` :748-760 与 `TrySetReview` :1172-1192 读的静态域在国服是垃圾）；伤害/主炮/attachedFleets 补丁国服可先不做。`BlueOath.Tools --analyze-il2cpp` 已指向 `苍蓝誓约\clsy`，标签 "cn-1.5.20" 改 1.5.120 即可重跑。ROADMAP 里的国服 RVA 是 1.5.20 的，作废。
7. **引导 HTTP 响应 [code-change]**：`BootstrapHttpResponder.cs:56-58/74/84-85` 的 gn=jpshipgirl、tar_version=1.4.0、serverId:jp 无 cn 分支；国服 SDK 主机是 `mapishipgirl.zuiyouxi.com`（getaddrinfo 白名单 `.zuiyouxi.com` 已覆盖），gn/主机常量藏在 `clsy_Data\StreamingAssets\android\platform\config.json`（F0F0F0F0 头加密，仓库无解密工具）。国服 sdk_ui 还会请求 `/phone/helper/getagreeversion`、`/sdk/switch/getSwitch`、`zyxtysdk.zuiyouxi.com/user/*`，现在全落 501；可先用 `--capture` 抓一轮再补。tar_version 要与 `bundles\assetmap` 里的版本串一致。
8. **版本字面量**：`src/BlueOath.Protocol/Protocol.cs:13` 写死 ClientVersion "1.5.20" → clientId "cn-1.5.20" → Mod targetClients 过滤；改成读 Version.txt 或加 `--client-version=`。
9. **配置差异**：服务端引用的 70 张表里国服缺 5 张（config_affection_item、config_equip_enhance_level_ur、config_talent、config_talentmain、config_task_treaty），21 张字段集不同；`GameServices.cs:21` 的 OathShopCurrencyId=17553 在国服 config_item_info 中不存在；内嵌 gm-goods/gm-mails/build-pools 的 ID 未按国服核对。`docs/config-catalog` 的差异表是按 1.5.20 做的，用于规划前需重跑 `--analyze-config`。
10. **并行调试**：服务端本身支持多实例（端口、`--data` 均可独立）。阻碍全在脚本/启动器：杀全部进程、写死端口与 `runtime\jp`、`bootstrap.ini` 与 Payload 日志落在 Payload 所在目录（两套需各自的 `native\bin-x86-cn`，注入器与 Payload 必须同目录）、`game-login.log` 落在服务端 dll 旁会交织、`start-client.ps1` 的 `runtime\tls` 共享。做完第 4 项后可顺序跑；真正同时在线要再做这些隔离。启动器一个目录只能有一份 settings，国服调试近期只走脚本；需要并行时复制一份启动器目录（如 `launcher-cn\`）配自己的 settings。
