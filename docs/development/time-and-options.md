# 时间结算、原规则与作弊选项：交接说明

本文说明时间结算、原规则与作弊选项这一组改动对服务端的结构性影响，供维护者审阅和后续开发参考。面向玩家的变化见 `CHANGELOG.md`「未发布」一节。

## 1. 时间结算（`Protocols/Services/TimeSettlement.cs`）

原先各协议各自计算经过时间或完全不算，心情、产出、体力等都不随时间变化。现在所有按时间推进的数据统一由纯函数 `TimeSettlement.Settle(account, now, rules)` 结算：

- 心情（自然、宿舍、浴场回复与基建工作消耗）、秘书舰好感、浴券到期。
- 资源楼与道具工厂产出、工人体力回复（公式在 `BuildingProduction.cs`，与客户端 `GetCurStrengthReal` 等同口径）。
- 祈愿墙每日重置、「商店真实库存」的定时补货与免费刷新回复（`ShopStock.cs`）。
- 前哨按时间产出（`OutpostService` / 见第 5 节）。

约定（违反会让客户端与服务端数据分叉）：

1. 每个协议在账号锁（`GameServices.LockAccountAsync`）内先 `SettleLockedAsync` 落盘，再执行业务。
2. 写回的时间锚点不得早于 `PlayerAccount.LastSettleTime`；业务里的「现在」取 `max(now, LastSettleTime)`。
3. 落盘的结算变化必须随同一应答推送：建筑与舰娘按 [建筑, 舰娘, 建筑] 的顺序（`BuildMoodSyncPushes`），浴场、祈愿墙、商店等用 `BuildSettlementPostPushes`；业务失败也要推结算结果。
4. 新增按时间推进的功能并入 `Settle`，不要在单个协议里自己算经过时间。`Settle` 不读时钟、不做 IO，规则由 `SettlementRules` 注入（测试可替换配置）。
5. 首次加载旧档会一次性迁移（心情万分制 `MoodVersion`、基建 `ProductionVersion` 等），迁移不可回退。

## 2. 网络帧（`BlueOath.Protocol/NetSocketCodec.cs`）

客户端收发不对称：发送时长度字段只算正文，接收时把长度当作含 5 字节帧头的整帧长度、只读 `L − 5` 字节正文。原先服务端按正文长度写，每条消息末尾少读 5 字节，`TResponse.Time` / `IsResponse` 被截断，客户端从未与服务端对时（夏令时下快一小时，加速与领取在临近完成时被拒）。服务端→客户端统一用 `WriteToClientAsync`（长度 = 正文 + 5），测试里的模拟客户端用 `ReadFromServerAsync`。

## 3. 作弊与原规则选项（`CheatOptions.cs`）

服务端开关：`--cheat-x` 开启、`--cheat-x=on|off` 指定、`--no-cheats` 关闭全部作弊，后出现的为准。作弊默认全部开启（`CheatOptions.Default`，离线版原来的规则），两项 `--real-*` 默认关闭。启动器逐项显式传 `=on/off`；`run-game.bat` 用 `-NoCheats`、`-NoCheatXxx`、`-CheatXxx`。ready JSON 的 `cheats` 对象回显每一项，启动器与脚本据此核对。`CheatOptions` 记录参数的默认值是 false（`CheatOptions.None` 即全部按原规则），测试用 `ServerOptions.Parse` 时加 `--no-cheats`。

| 开关 | 关闭时 | 打开后（默认） | 实现位置 |
| --- | --- | --- | --- |
| `--cheat-production` | 按时间生产 | 道具下单即完成，资源楼满仓 | `SettlementRules.OmitProductionTime` |
| `--cheat-strength` | 工人体力按时间回复、被消耗 | 不消耗并保持上限 | `SettlementRules.OmitWorkerStrength` |
| `--cheat-vow` | 祈愿冷却 | 无冷却 | `SettlementRules.OmitVowCooldown`、`VowRules.OmitCooldown` |
| `--cheat-mood` | 基建工作、体力加速与前哨驻守扣心情 | 不扣（自然、宿舍、浴场回复照常） | `SettlementRules.OmitMoodCost` |
| `--cheat-medals` | 探索按 `config_ship_main.extract_reward` 发勲章（SSR 25、SR 5） | 每抽固定 100 个 | `BuildShipService.ExtractBonus` |
| `--cheat-drops` | 掉落池按 `config_drop_item` 数量 | 可堆叠资源/道具每项 +600～2000 | `DropPoolResolver.Resolve(bulkBonus)` |
| `--cheat-sweep` | 扫荡作战按真实时间，前哨按时间产出、每天 2 次加速 | 扫荡开始即完成；前哨不按时间产出、不扣驻守心情，加速不限次数、立即产出 | `SweepLogic.StartTime`、`SettlementRules.OutpostInstant` |
| `--cheat-battle` | `copy.StartBase` 按详情页公式编码属性与装备，带当前耐久 | 离线版原来的属性：`ShipMainLoader.Leveled` 每级约放大 100 倍、PlaneNum 100、满耐久 | `BattleStats.Compute`、`ProtocolEncoder.EncodeStartBaseRet(realStats)` |
| `--real-resource-cost`（默认关） | 离线版免费：探索、商店、出击不扣资源 | 按原规则扣（`CostLogic` / `ShopPricing` / `SortieCost`） | 见下 |
| `--real-shop-stock`（默认关） | 列出全部商品、不限量 | 随机陈列、库存、定时与手动刷新（`ShopStock.cs`） | `PlayerAccount.Shop` |

`--real-*` 两项方向与作弊相反：默认保持离线版原来的免费规则，打开后按原游戏规则。

- 真实消耗资源：探索按 `config_extract_ship.expend`（十连有 `new_ten_expend` 券时用券）；商店按 `GetPriceByNum` 分档价格（`cur_relation == 2` 只扣所选的一种货币）；普通出击按 `config_copy_display` 扣燃料（只数主舰队，追击用追击关卡的表，新海域用 `after_clear_supple_num`，余额不足扣到 0、不拒绝出击），共闘单人扣 RP；扫荡预扣燃料并退还未完成的轮数。
- 商店真实库存：日历按 UTC；定时刷新补满整个商店（`manual_refresh_stock` 只决定手动刷新时哪些商品补货）；手动刷新顺序为免费次数 → 入荷指令 10303 → 钻石分档，每日上限 `max_count`；道具与钻石只在同时打开真实消耗资源时扣。

已删除的行为：服务端原来在每次加载档案时把 17553 与基地建材补满。17553（黒猫のカップケーキ）是 2022 年 4 月活动的兑换道具，只在活动期间开放的交换商店（1071/1072）使用，与戒指无关（戒指的常规来源是ダイヤ特集与定期補給品2）；基地建材可在道具工厂用工人体力合成。现在建造/升级按 `config_buildinglevelup.rawmaterial*` 扣建材，加载档案不再补任何道具，需要时用「体力」作弊或存档编辑。

## 4. 新功能模块

- **扫荡作战**（`Modules/MopUpModule.cs`、`Services/SweepLogic.cs`，状态 `PlayerAccount.Sweep`）：`CheckSweep` 回可用舰队数；`StartSweep` 1–10 轮、每轮 `autobattle_time` 秒；`StopSweep` 按完成轮数发放与普通通关相同的奖励（`BattleService.GrantSweepRun`，追击掉落放在 `ExtraReward`）；登录同步推送 `mopUp.GetMopUpData`。结算在领取时按 `CompletedRuns` 计算，不进 `Settle`。
- **GM 存档编辑**（`Services/SaveEditor.cs`、`ItemCatalogLoader.cs`、`Listeners/GmWebListener.cs` 的 `/save` 与 `/api/save*`、`save-editor.html`）：设置货币与背包道具数量，「全部道具」页按 `config_table_index` 合并所有背包表的名称供搜索添加。改档在账号锁内进行；在线时经 `Sessions/SessionPushHub.cs` 给会话补发玩家信息与背包，会话正在处理请求时推迟到该请求写完，并在写出时按最新存档生成。存档在 `accounts` 表（旧 `profiles` 表不再写入）。
- **前哨**：见第 5 节。
- **建材**：基地建造/升级按 `config_buildinglevelup.rawmaterial*` 扣建材（`BuildingService.TryChargeMaterials`），不足时拒绝且不改档。
- **养成消耗**（`Services/UpgradeCosts.cs`，总是扣，与 `--real-resource-cost` 无关）：技能升级扣 `config_pskill_dict_group.upgrade_materials[min(等级, 行数)]` 与同下标的 `upgrade_materials_mub`（满级或没有材料时拒绝；存档里缺的技能按 1 级算，升到 2 级）；经验道具先从背包扣再加经验；实验室天赋扣所请求天赋的 `config_talent.levelup`；修理按客户端 `CalculateNeedAllGold`（誓约舰按 `config_affection_favor.affection_cost` 9 折）并检查余额；前哨升级扣当前等级那一行的 `config_outpost_level.item_cost`。不足时都返回错误、不改档，并在应答前重推玩家信息与背包。

## 5. 前哨（`Services/OutpostService.cs`、`Modules/OutpostModule.cs`）

原先前哨没有按时间的产出：`outpost.SpeedUpProduction` 每次立即给一轮 `config_outpost_level.reward`，不限次数。客户端自己不计算产出，只每 60 秒请求 `GetOutPostInfo`，所以规则全在服务端。

默认（`--cheat-sweep` 关）由 `TimeSettlement.Settle` 的前哨步骤（`OutpostProduction`）推进：

- 清理不合法的驻守舰娘：入浴、驻守基地建筑、已退役、重复或超过 `ship_num` 的会被移出（`SetHero` 也会把舰娘从其它前哨和基地建筑撤下，拒绝入浴中的舰娘）。
- 驻守舰娘按 `config_outpost_info.mood_cost` 每 600 秒扣心情；开启「自动入浴」（`UseCoin`）时心情降到 0 用 50 浴币回复 60 心情（`config_parameter[406]`）。
- 只在有驻守舰娘心情大于 0 的时段产出，按 `config_outpost_level.reward` 累进 `ItemInfo`，不足一个的余数存在 `Progress`。
- 状态：0 稼働停止、1 稼働中、2 未使用（无驻守）。
- 加速：每天所有前哨共 `config_parameter[407]` = 2 次（UTC+8 零点重置），只有工作中的前哨可以加速，立即获得 `speedup_reward`（150% × 24 小时）的产出并直接进背包；开启「真实消耗资源」时扣 `speedup_cost`（燃料 1500）。

`--cheat-sweep` 打开时不按时间产出、不扣驻守心情，加速不限次数、不消耗，把一份 `reward` 原值加进 `ItemInfo`（离线版原来的规则）。

推断（客户端不计算产出，无法从字节码证实）：`reward` 是每秒产量 × 1e8（约每小时 1250 资源）；`speedup_reward` 的第二项是小时数；产出固定 1.0 倍（服务端算不出客户端的战力加成）；不限制 `box_limit`；加速产出直接进背包。

## 6. 存档新增字段

全部是记录末尾的可选参数，旧档读入为默认值：

- `PlayerAccount`：`LastSettleTime`、`MoodVersion`、`Vow`、`Shop`、`Sweep`。
- `Hero`：`Mood`（万分制）、`AffectionSettledAt`。
- `PlayerBuildingEntry`：`LastBuildUpdateTime`、`MoodSpeed`、`ProductCount`、`Progress`、`RecipeId`、`ItemCount`、`HeroWindows`、`Productivity`。
- `PlayerBuilding`：`ElectricMax`、`WorkerUpdateTime`、`ProductionVersion`、`WorkerStrengthCarry`。
- `PlayerOutpostBuilding`：`UpdateTime`（产出与心情的结算锚点，0 = 不在生产）、`Progress`；`PlayerOutpost`：`SpeedUpDay`。

## 7. 客户端 Mod

- `Mods/production-speedup-fix.mod`：日服加速页在加速量超过剩余时间时，「确认」只弹 `UILayer.MAIN` 层的浪费确认框而从不发请求。
- `Mods/wish-cooldown-tip-fix.mod`：「许愿墙」作弊的界面配套。日服祈愿结果页按客户端公式现算「下次可祈愿时间」，不读服务端冷却；作弊开启（服务端冷却为 0）时不再弹出这段并不存在的冷却。

新增 Mod 要同时加进 `Mods/bootstrap.lua` 的 entries 列表（它不读 `mod.json` 的 enabled）。

## 8. 测试

`src/BlueOath.Tests` 是自定义宿主（`dotnet test` 不执行用例）：

```powershell
dotnet build .\BlueOath.Local.sln
dotnet run --project .\src\BlueOath.Tests\BlueOath.Tests.csproj --no-build
dotnet run --project .\src\BlueOath.Tests\BlueOath.Tests.csproj --no-build -- --integration
```

主题参数：`--time-settlement`、`--vow`、`--production`、`--cheats`、`--bath-gift`、`--netsocket`、`--production-speedup-mod`、`--real-cost`、`--shop-stock`、`--sweep`、`--save-editor`、`--outpost`、`--resource-cost`、`--battle-stats`。集成测试启动与测试项目同一构建配置（Debug / Release）的服务端程序集。

基线：默认用例的 `hero advance preserves neighbors…`，以及 `--integration` 的 fashion synchronization、traditional construction、building construction and hero assignment persist、hero remould、hero gift legacy affection，在 `master` 上同样失败（依赖日服 1.4.0 配置中不存在的数据或既有问题），评估改动时对照这些基线。

## 9. 推断与未实现

以下按配置或界面文字推断，客户端字节码无法直接证实：

- 商店定时刷新补满整个商店；免费刷新次数按日重置。
- 每日副本扫荡与手动通关一样不限当日次数。
- 突破 MAX 的 SSR 再次抽到时的追加勲章（`extract_get_exceed_reward`）规则不明，未实现。
- 扫荡与战斗都不发经验、不扣心情与耐久（与原有的 `copy.PassBase` 一致）。
- 服务端不校验 `unlock_before_shop_id`（客户端校验）；演习点数、建造 `costmoney` 不扣（日服配置为 0）。
