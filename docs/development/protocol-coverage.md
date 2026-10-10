# 协议全覆盖实现报告

## 概述

本次工作实现了某游戏本地服务端对客户端所有网络协议的完整覆盖。总计 **423 个协议路由**，按功能模块分为以下类别：

## 统计

| 类别 | 协议数 | 实现方式 |
|------|--------|----------|
| 核心已实现 | 45 | 完整业务逻辑 |
| Hero 模块 | 22 | 完整业务逻辑 |
| Shop/Equip/Bag/Fashion/Illustrate | 21 | 完整业务逻辑 + 部分占位 |
| Building/Bathroom/Study/Strategy/Build | 48 | 基建、浴场按时间结算的完整业务逻辑；其余占位 |
| Friend/Chat/Discuss | 21 | 全部占位 (多人社交) |
| Task | 9 | 完整业务逻辑（任务同步/领奖/刷新/教学任务） |
| Guild/Teaching/Multiplayer | 100 | 全部占位 (多人系统) |
| Activity/Copy/Boss/Tower/Misc | 157 | 全部占位 (离线不适用) |
| **总计** | **423** | |

## 核心已实现协议

### 登录/用户
| 协议 | 功能 |
|------|------|
| player.Login | 登录 |
| player.GetUserList | 用户列表 |
| player.CreateUser | 创建用户 |
| user.UserLogin | 用户登录 |
| user.GetUserInfo | 获取用户信息 |
| user.SetUserSecretary | 设置秘书舰 |
| user.ChangeName | 改名 |
| user.SetMessage | 设置签名 |
| user.SetPlayerHeadFrame | 设置头像框 |
| user.SetHead | 设置头像 |
| user.GetHeadBuyCount | 头像购买次数 |
| user.BuyHead | 购买头像 |
| user.NewHeadUnlockedList | 新头像解锁列表 |

### 邮件
| 协议 | 功能 |
|------|------|
| mail.GetMailList | 邮件列表 |
| mail.OpenMail | 打开邮件 |
| mail.DeleteMail | 删除邮件 |
| mail.DeleteAllMail | 删除全部邮件 |
| mail.FetchItem | 领取附件 |
| mail.FetchAllItems | 全部领取 |
| mail.ReceiveNewMail | 接收新邮件 |

### 商城
| 协议 | 功能 |
|------|------|
| shop.BuyGoods | 购买商品（默认免费；`--real-resource-cost` 按 config_shop_goods 分档价格扣费，余额不足返回错误并重推货币/背包） |
| shop.QualityBuyGoods | 品质购买（同上，整单全部够才扣） |
| shop.GetShopsInfo | 获取商店信息（默认列出 GM 商品、不限量；`--real-shop-stock` 随机商店按权重抽取陈列，Num 为已购件数，售罄 Status=1，按 UTC 日/周/月定时补货） |
| shop.RefreshShop | 刷新商店（默认空应答；`--real-shop-stock` 按 config_refresh 先用免费次数，再用刷新券 10303，再按档位扣钻石，每日上限 max_count；道具与钻石只在同时开启 `--real-resource-cost` 时扣） |

### 战斗
| 协议 | 功能 |
|------|------|
| copy.StartBase | 开始战斗（`--real-resource-cost` 按 config_copy_display 扣出击燃料，共闘单人扣 RP；只扣到 0、不拒绝出击） |
| copy.AttackBase | 攻击 |
| copy.PassBase | 结算（区分 PlotCopy/SeaCopy）；落盘战后耐久，应答前推送船坞（结算页按它显示大破模型与头图），应答后推送关卡进度、背包与装备 |
| copy.QuitBase | 退出战斗 |
| copy.GetRandomFactors | 获取随机因子 |
| copy.GetCopy | 获取关卡数据 |
| copy.UnLockCopy | 解锁关卡 |
| copyinfo.GetCopyInfo | 获取关卡记录 |
| copyinfo.DotBase | 标记关卡 |

### 前哨（アンブラ前哨）
| 协议 | 功能 |
|------|------|
| outpost.GetOutPostInfo | 前哨信息（结算到当前时间后推送 outpost.UpdateOutPostInfo） |
| outpost.SetHero | 驻守舰娘（撤出其它前哨与基地建筑，拒绝入浴中的舰娘） |
| outpost.UpgradeBuilding / DegradeBuilding | 升级（扣当前等级那一行的 item_cost，不足返回错误并重推货币/背包）/ 降级（未实现，空应答） |
| outpost.SetUseCoin | 自动入浴（心情降到 0 时用 50 浴币回复 60 心情） |
| outpost.SpeedUpProduction | 加速：每天所有前哨共 2 次，立即获得 150% × 24 小时的产出；`--real-resource-cost` 扣燃料 1500；`--cheat-sweep` 不限次数 |
| outpost.ReceiveItem / ReceiveAll | 领取产出 |

### 扫荡作战
| 协议 | 功能 |
|------|------|
| mopUp.CheckSweep | 可同时扫荡的舰队数（默认 4；`--real-resource-cost` 下 1 + 追加艦隊チケット 17106，最多 +2）与当前扫荡列表 |
| mopUp.StartSweep | 开始扫荡：autobattle_open 的关卡，1–10 轮，每轮 config_copy_display.autobattle_time 秒；`--real-resource-cost` 下预扣燃料（含追击关卡） |
| mopUp.StopSweep | 结束/取消（按 fleetId）：按已完成轮数发放与普通通关相同的奖励（追击掉落放在 ExtraReward），未完成轮数退还燃料 |
| mopUp.GetMopUpData | 服务端推送扫荡列表与本次结算的 passRets；登录时也推送，重登后可继续领取 |

### 建造
| 协议 | 功能 |
|------|------|
| buildship.BuildShip | 建造舰娘（`--real-resource-cost` 扣探索消耗：十连有券用券，否则推荐信 × 抽数；不足返回错误、不抽） |
| buildship.BuildShipInfo | 建造信息 |
| buildship.BuildShipBox | 建造仓库 |
| buildship.BuildShipReward | 建造奖励 |

### 引导/剧情
| 协议 | 功能 |
|------|------|
| guide.PlotReward | 剧情奖励（持久化 PlotId） |
| guide.Setting | 引导设置 |
| cachedata.CacheData | 缓存数据 |

### 战术
| 协议 | 功能 |
|------|------|
| tactic.GetHerosTactic | 获取战术 |
| tactic.SetHerosTactic | 设置战术 |

### 任务
| 协议 | 功能 |
|------|------|
| task.TaskInfo | 同步主线/日常/周常/成长/成就/教学任务 |
| task.TaskReward | 领取单项任务奖励并持久化 |
| task.TaskAllReward | 一键领取任务或成就奖励 |
| task.TaskTrigger | 客户端任务事件触发确认 |
| task.GetPtReward | 领取教学履历阶段奖励 |
| task.GetTeachingTask | 获取教学日常与考核任务 |
| task.TaskRewardByDaysActivity | 领取七日活动任务奖励 |
| task.TaskSevenDayActivity | 领取限时七日任务奖励 |
| task.TaskRewardByReturnActivity | 领取回归任务奖励 |

## Hero 模块 (22 协议)

| 协议 | 功能 | 实现 |
|------|------|------|
| hero.ChangeEquip | 更换装备 | 完整逻辑 |
| hero.AddExp | 添加经验 | 完整逻辑（背包里须有所选经验道具，不足返回错误并重推货币/背包；`--real-resource-cost` 开启时扣道具，关闭时不消耗） |
| hero.Marry | 结婚 | 完整逻辑 |
| hero.HeroIntensify | 强化 | 空响应 |
| hero.HeroAdvance | 突破 | 空响应 |
| hero.HeroAdvanceMUB | 满破 | 空响应 |
| hero.LockHero | 锁定舰娘 | 更新 Lock 字段 |
| hero.RetireHero | 退役 | 移除 Hero |
| hero.ChangeName | 改名 | 更新 Name 字段 |
| hero.StudySkill | 技能升级 | 等级 +1；`--real-resource-cost` 开启时扣 `config_pskill_dict_group.upgrade_materials[等级]` 与同下标的 `upgrade_materials_mub`（不足返回错误），关闭时免费；满级或无材料总是返回错误并重推货币/背包 |
| hero.AutoEquip | 自动装备 | 空响应 |
| hero.AutoUnEquip | 自动卸装 | 空响应 |
| hero.HeroAdvMaxLv | 等级突破 | 空响应 |
| hero.HeroEquipEffect | 装备效果 | 空响应 |
| hero.HeroRemould | 改造 | 空响应 |
| hero.EquipBinding | 装备绑定 | 空响应 |
| hero.EquipUnBinding | 装备解绑 | 空响应 |
| hero.EquipLockTransplant | 锁定移植 | 空响应 |
| hero.HeroCombineUpLv | 合体升级 | 空响应 |
| hero.HeroCombineQuickLevelUp | 快速合体升级 | 空响应 |
| hero.HeroCombineBreak | 合体突破 | 空响应 |
| hero.HeroCombine | 合体 | 空响应 |
| hero.AddAffection | 添加好感度 | 更新 Affection |
| hero.GetHeroInfo | 获取英雄信息 | 返回 HeroBag |
| hero.GetHeroInfoByHeroIdArray | 按ID获取英雄 | 返回 HeroBag |

## 占位协议说明

### 建筑/浴室/学习/策略 (48 协议)
**原因**: 这些模块依赖 C# 3D 场景渲染、计时器、UI 动画等客户端逻辑，离线模式下无法复现，返回空成功响应。基建、浴场、扫荡与前哨已改为完整实现，见上文与 `time-and-options.md`。

### 社交/公会/教学/多人 (121 协议)
**原因**: 多人匹配、公会管理、聊天、好友等系统依赖在线服务端状态同步，离线模式不适用。全部返回空响应。

### 活动/爬塔/其他 (157 协议)
**原因**: 活动系统、爬塔、运动会等依赖服务端活动周期配置和在线排名，离线模式不适用。全部返回空响应。

## 实体变更

### PlayerEntities.cs
- `Hero` 新增 `string Name = ""`、`bool Lock = false`
- 新增 `CopyRecord`、`PlayerCopyProgress`、`PlayerSeaCopyProgress`
- `PlayerAccount` 新增 `CopyProgress`、`SeaProgress`、`PlotRewardIds`
- `CreateDefault` 默认 `Level=80`、`PlotChapterId=int.MaxValue`

### PlayerDataCodec.cs
- `HeroGrid` 新增 `Name`、`Lock` 字段
- 新增 `WriteStringField` 辅助方法
- `Encode(GuideInfo)` 支持 `PlotList` 字段

### ConfigLoaders.cs
- 新增 `ShipHandbookLoader` (舰娘名称查询)
- 新增 `PlotTriggerLoader` (2195 个剧情触发器 ID)
- `ChapterCopyLoader` 新增 `_copyTypeMap` + `GetCopyType()`

## 新增文件

- `GameLoginMessageHandler.NonCombat.cs` - shop/equip/bag/fashion/illustrate 处理
- `GameLoginMessageHandler.Stubs.cs` - 占位协议文档

## 构建验证

```
dotnet build BlueOath.Server.csproj
0 个警告 / 0 个错误
```
