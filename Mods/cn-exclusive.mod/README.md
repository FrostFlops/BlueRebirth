# cn-exclusive.mod：国服独有内容

把几项国服（1.5.120）独有、而日服 1.4.0 客户端里其实已有资源的内容重新打开。所有图片、模型、剧情文本和语音都来自你自己的日服客户端，Mod 只带少量由脚本生成的配置数据（ID、标题和国服文本），运行时不需要国服客户端。

做法的说明见 `docs/MOD_DEVELOPMENT.zh-CN.md` 第 12 章。

## 恢复的内容

| 开关 | 内容 | 说明 |
|---|---|---|
| `fubuki_spring_fashion` | 吹雪春节时装「爆竹天女」 | 日服配置把它隐藏了（`config_fashion` 2013014 的 `is_appear = 0`），模型、语音、立绘都在。服务端本来就发放全部时装，打开后即可在换装页穿上 |
| `oakland_spring_touch` | 奥克兰春节时装的触摸互动 | 恢复国服的 `click_fs02` 触摸动作和语音，动作、语音与字幕日服都有 |
| `spring_festival_story` | 春节风波（2020 年国服春节活动剧情） | 7 个节点，其中 50306 是战斗。在图鉴「回忆」中新增章节 12010，节点名称和说明改回国服原文。剧情正文日服配置里本来就是中文 |
| `character_stories` | 10 艘舰娘的角色剧情 | 飞龙、苍龙、阿蒂格利埃尔、旧金山、海伦娜、克利夫兰、伏尔塔、阿拉斯加、川内、吹雪，约 90 篇，在图鉴「回忆」里查看。剧情正文日服配置里已有 |
| `cn_voice_ninghai`、`cn_voice_pinghai` | 宁海、平海中文语音 | 沿用日服模型，语音换成客户端自带的中文语音库，同时换上国服字幕和誓约文本，语音时长（动作长度、剧情自动翻页）也改按中文录音 |
| `spring_furniture` | 春节家具套装（国服套装 5） | 吉祥结、爆竹堆、鞭炮串、福字、彩灯、屏风、摇钱树（左、右）、灯笼共 9 件，出现在背包「装飾」的家具套装里，开启后主页白天、清晨、夜晚都会摆出来。模型日服都有（日服配置的 36–44 号物件），名称和说明是国服原文。需要支持指挥室装饰的服务端（见下方使用要求），由服务端直接全部持有 |

## 不包含的内容

- 只在国服客户端里的美术资源：头像框、表情、杂志第 6 期等。春节家具的图标和套装封面也只在国服客户端里，改用日服已有的图（见已知限制）。
- 春节家具的获取途径（国服的春节家具活动）：日服客户端和服务端都没有这个活动。
- 国服版本的 CG 动画。
- 国服宁海、平海的 3D 模型。
- 摆件（`config_interaction_figurte`）：服务端没有对应系统，加了也看不到。
- 国服独有的活动系统：签到、里程碑、盲盒商店、红包等，需要国服的 Lua、界面和服务端协议。

## 使用要求

- 与基线一致的 JP 1.4.0 客户端，以及带 Lua Mod Loader 的 Payload。
- 春节风波需要支持 `memory-chapters.json` 的服务端（与本 Mod 同一版本的本地服）。服务端没有登记这一章时，客户端不会显示它，其他功能不受影响。
- 服务端从 `<客户端目录的父目录>\Mods` 读取 `memory-chapters.json`：发布包里就是包根的 `Mods`；源码布局（客户端在 `<仓库>\blueoath\blueoath`）下是 `<仓库>\blueoath\Mods`，没有脚本会创建它，需要先建目录联接 `mklink /J "<仓库>\blueoath\Mods" "<仓库>\Mods"`（或复制一份）。服务端标准错误输出（源码脚本为 `runtime\debug\<时间戳>\server.stderr.log`）出现 `memory-chapter mod cn-exclusive.mod: loaded 1 of 1 memory chapters` 即已登记；出现 `memory-chapter mods: no Mods directory at ...` 说明它找的目录不存在。
- 春节家具套装需要支持指挥室装饰的服务端：服务端从同一个 Mods 目录读取本 Mod 的 `decorations.json`，并且只在 `Mods/bootstrap.lua` 的 `entries` 列出本 Mod 时才收。服务端标准错误输出出现 `decor mod cn-exclusive.mod: loaded 9 of 9 decorations and 1 of 1 sets` 即已登记。服务端不支持装饰时套装不会出现，其他功能不受影响。
- 运行时不需要国服客户端，也不需要额外下载。

## 开关

- 单项开关：`main.lua` 顶部的 `SETTINGS` 表，每项默认开启，改成 `false` 后重启游戏生效。项名见上表，以文件中的实际内容为准。
- 中文语音的附属项在 `SETTINGS.voice` 里，对宁海、平海两项都生效：
  - `captions`：字幕换成国服文本。
  - `wedding_text`：誓约剧情换成国服文本（与字幕一样，只在该舰娘的中文语音确实生效时）。
  - `durations`：语音时长按中文录音（同上）。游戏按语音时长结束战斗台词、待机、入手等动作，并决定剧情自动播放何时翻页；不改时沿用日语录音的时长，中文台词较长时动作会在它说完之前结束，剧情也会提前翻页。
  - `dorm`：宿舍语音（共享库 `CV_role_sd_bank`）。
  - `letter`：情书语音（共享库 `CV_role_gaobai_bank`）。
  - `probe`：挂载语音包后做一次自检，结果写进日志。
- `spring_furniture` 关掉后，服务端仍会按 `decorations.json` 下发这些家具（它读不到这里的开关）。客户端会从推送里去掉它们，套装从背包消失；正在使用这个套装时主页按未选处理。重新打开后恢复原样。
- 整个 Mod：把 `mod.json` 的 `enabled` 改成 `false`，或在启动器的 Mod 页关闭。客户端（`main.lua` 自己读取）和服务端都会跳过它。
- 卸载：从 `Mods/bootstrap.lua` 的 `entries` 删除 `cn-exclusive.mod/main.lua`，再删除本目录。

## 确认是否生效

在 `BlueOath.Payload.log` 里搜 `[cn-exclusive]`（日志位置见 `docs/MOD_DEVELOPMENT.zh-CN.md` 第 13 章）。每项功能都有一行汇总，例如：

```text
fubuki_spring_fashion: config_fashion 1/1 patched
spring_festival_story: memory chapter 12010 listed (server progress 7/7)
cn_voice_ninghai: Chinese voice served from the package: c_cl_ninghai_jp_bank (38 events), ...
captions: 76/76 C# caption rows carry the Chinese text (...)
durations: 79/79 voice events last as long as the Chinese recordings (...)
spring_furniture: config_interaction_item_bag 9/9 added, config_interaction_item_bag_group 1/1 added
```

含 `skip`、`NOT active`、`hidden` 或 `failed` 的行会写明原因。配置相关的行在对应的表第一次被读取时出现（有的要打开相应页面才会出现，例如誓约文本；家具套装的行最晚在打开背包「装飾」页时出现），回忆章节的行在打开图鉴「回忆」时出现，语音、字幕和语音时长的行在进入登录界面时出现。

## 重新生成数据

`data.lua`（客户端）和 `memory-chapters.json`、`decorations.json`（服务端）由脚本从国服 1.5.120 与日服 1.4.0 的配置生成，不要手改：

```powershell
python tools/export-cn-exclusive.py
```

脚本只用 Python 标准库，输出是确定的，参数和默认路径见脚本开头的说明。只有维护者更新数据时才需要国服客户端。

## 已知限制

- 中文显示：日服字体缺少不少简体字，缺的字由系统字体补上，字形和大小会有些不一致。
- 宁海、平海的口型和表情动画来自日服客户端，是按日语台词做的，与中文语音对不上（本 Mod 不带美术资源，国服客户端里按中文语音做的表情动画不在日服客户端里）。语音时长已按中文录音，动作在中文台词结束时收尾：中文比日语短的台词，口型在语音结束时随动作一起收掉；中文更长的台词，口型先停下，语音仍会说完。
- 语音时长取自客户端自己配置里中文事件的时长（`share/setting` 的 SoundBanksInfoProfile，与字幕一样改 C# 端的缓存）。平海的待机语音（wait）记的是国服客户端那一版录音的长度（24 秒，含结尾的静音），日服客户端里的这一版更短，所以待机动作会在语音结束后多停留约 9 秒，与国服客户端一样。
- 中文语音库在首次进入登录界面时由客户端自带的音频库生成到本目录的 `cache/`（约 12 MB）。源音频库大小变化、`data.lua` 更新或开关改动后会自动重建；删除 `cache/` 也没有问题，下次启动会重新生成。`cache/` 不入库，也不会被打进发布包。
- 某艘舰娘的中文语音没有生效时（源音频库缺失、读不出或没通过检查，或者语音包没能写入、挂载或通过自检），她的语音整体保持日语，宿舍和情书语音也不换，字幕、誓约文本和语音时长同样保持日语；日志里有对应的 `skip bank` 或 `NOT active` 行。
- 字幕替换依赖游戏 C# 端的字幕缓存，属于尽力而为，个别场景可能仍显示日文字幕。只有中文语音确实生效的舰娘才换字幕。
- 春节家具：国服的家具图标（`*_124`）和套装封面 `uipic_ui_lunarnewyear_cover` 日服客户端里没有。图标改用这件家具的场景图（`picture`），套装封面用灯笼的场景图（184×104，在封面框里会被拉宽一些）。
- 不支持安卓客户端。
