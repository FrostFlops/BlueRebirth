# android/tools —— 安卓端复原工具链

本目录是「安卓端无需 PC、独立运行」这条最终方案的可复现工具。
相关背景见 `.workbuddy/memory/MEMORY.md` 与 `docs/ANDROID_RESTORATION_PLAN.zh-CN.md`。

## 方案概述

```
┌─────────────────────┐        loopback         ┌──────────────────────────┐
│  BlueRebirthApp      │  127.0.0.1:19090 (TLS)  │  客户端本体（planL APK）  │
│  （独立启动器 App）    │ ◄─────────────────────► │  com.zephyrus.clsy.gp     │
│  com.blueoath.server │  127.0.0.1:19191 登录    │                          │
│                      │  127.0.0.1:19193 明文下载 │                          │
└─────────────────────┘                          └──────────────────────────┘
```

- **服务端**跑在独立 App 内（进程内 TLS，无需 PC、无需 adb reverse）。
- 客户端本体（planL）与热更资源包已备份在 `android/finel_clients/`（不入 git）。

### 配置库：安装套件时导入「全量 497 个」

服务端真正需要的是**客户端 APK 内置的那套完整配置库**：
`assets/assetpack/config/*.db`（**497 个**，约 105MB）。
热更资源包里的 `config/` 只有 **72 个**（热更覆盖用的增量），只导入它会导致服务端缺表。

因此 `build-install-kit.py` 会把这 497 个配置表**单独作为一条 `kind=config` 条目**打进套件
（App 不需要去解析 1.1GB 的 APK —— 而且 SAF 的流不可定位，读不了 zip 中央目录），
安装时解到 App 自己的配置目录，最后再用资源包里的 72 个**覆盖**（与客户端运行时的有效配置一致）。

- 落地目录：`/sdcard/Android/data/com.blueoath.server/files/config`（服务端优先读这里）。
- 覆盖判定用 **md5**，不是只看大小 —— `config_language.db` 就踩过「同尺寸不同内容」的坑。
- 不想带配置表时可以 `--config-from none`（那就只剩资源包里的 72 个）。

### 客户端资源铺设（套件安装的一部分）

客户端判断「自己的包内容版本」用的是**它自己那份 assetmap**：

- 游戏目录 `files/bundles/` 为空 → 回落 APK 内置 assetmap → 标题页显示 **1.4.90**，
  与服务端宣告的 1.4.140 不一致 → 走 `PATCH_TO_LATEST` 去热更（要真下载 3.5GB）；
- 铺上 1.4.140 资源 → 客户端直接认这个版本，弹「0.00MB 需要下载」→ 点确认即可进游戏。

所以套件安装完，App 会把资源**镜像**到游戏客户端自己的资源目录：

```
/sdcard/Android/data/com.zephyrus.clsy.gp/files/bundles/
```

- 优先硬链接（省一份 3.5GB），模拟存储不支持时自动退回复制；已存在且大小一致的文件跳过。
- ★ 该目录属于**另一个 App**，API 28 上必须持有 `READ/WRITE_EXTERNAL_STORAGE`
  （App 会主动申请；也可 `adb shell pm grant com.blueoath.server android.permission.WRITE_EXTERNAL_STORAGE`）。
  API 30+ 系统已不再真正授予 → 铺设失败**只告警**，客户端仍可从 19193 端口热更。
- 失败/跳过不会阻断套件安装，日志里会明确写出原因。

### 资源不齐时的门控

App 启动时/回到前台时会体检：**资源包（assetmap）** + **配置库（config_*.db）**。
不齐时：

- `EmbeddedServer.EnsureStarted` 直接拒绝启动（Application / 前台服务都不会自动拉起服务端）；
- 「启动服务」「停止服务」「打开游戏」三个按钮全部禁用，红色提示写出缺什么。

### 配置库不再打进 APK

客户端 / 服务端 App / 热更资源包三件套必定同装，而**资源包里本就含 `config/`（72 个
`config_*.db`）**。因此服务端 App 启动时直接读资源包里的 `config/`（`EmbeddedServer.
ResolveConfigDirAsync`），APK 里那份 ~105MB 副本已删掉。

- 目录解析优先级：`<导入的全量配置>` → `<bundle>/config` → 同机游戏目录
  `files/bundles/config` → 历史解压目录 → APK 内置 asset（仅自包含构建会有）。
- 需要「自包含 APK」时，取消 `src/BlueOath.Android/BlueOath.Android.csproj` 里被注释掉的
  `AndroidAsset` ItemGroup 即可。
- ⚠ **不要对该 App 执行 `adb shell pm clear`**：它会连外部目录一起清空，`bundle/` 就没了。
  要重置 App 请只 `am force-stop` + 重新启动。

### 端口

| 端口 | 用途 |
| --- | --- |
| 19090 | **TLS**（自签）· SDK 引导（getstate / getPlData / getversion / index.php） |
| 19191 | 游戏登录（引导响应下发给客户端） |
| 19192 | 通告给客户端的 KCP 端口（未真正实现 UDP 端） |
| 19193 | **明文 HTTP** · 热更下载专用（客户端下载器是 libcurl+OpenSSL，不接受自签 TLS） |

## 脚本

| 脚本 | 说明 |
| --- | --- |
| `build-server-app.sh` | 构建 BlueRebirthApp。路径含非 ASCII 时自动建 ASCII 联接；强制 clean build（含引用工程）。 |
| `push-bundle.sh` | 把热更资源包推到设备（默认 → App 的 `bundle/`；也可指定游戏目录）。 |
| `build-merged.py` | 生成热更资源包 `merged/`（`new_bundles` 为主 + `apk_bundles`/`overlay` 补缺）。 |
| `build-compressed-blobs.py` | 生成 `merged/compressed/`（config 的 gzip 变体，客户端热更会来下载）。 |
| `verify-merged.py` | 用 1.4.140 的 assetmap 全量校验 `merged/` 的 md5 与大小。 |
| `build-install-kit.py` | 打包 / 校验 / 解包「安装套件」`.brk`（客户端 APK + 热更资源包 + 全量配置表合成单文件）。 |

## 安装套件（.brk）

把「客户端 APK + 热更资源包 + 全量配置表」合成**一个文件**，用户在 App 内用系统文件选择器选它，
App 自动解包资源、导入配置表、把资源铺进游戏目录，并调 PackageInstaller 装客户端 —— 免 PC、免 adb。

```bash
# 打包（版本号自动从 assetmap 识别；配置表默认从客户端 APK 抽取 497 个）
python android/tools/build-install-kit.py \
  --bundle android/_work2/merged \
  --client-apk android/finel_clients/BlueRebirth-client-1.4.140-planL.apk \
  --out android/finel_clients/BlueRebirth-kit-1.4.140.brk

# 校验（重算每个文件的 md5）
python android/tools/build-install-kit.py --verify-only <kit.brk>

# 解包（排障 / 端到端自检）
python android/tools/build-install-kit.py --extract-to <kit.brk> <输出目录>
```

常用开关：`--config-from apk|目录|none`（默认 `apk` = 从客户端 APK 抽 497 个配置表；
`none` = 不带配置表，只靠资源包里的 72 个）。

格式 `BRKIT002`：`magic(8B) + headerLen(u64 LE) + header JSON + payload`。
payload 里 `bundle` / `config` 条目是「文件表」（`u8 kind / u16 pathLen / u32 dataLen / path / 16B md5 / data`，
`kind` 0=文件 1=结束 2=空目录），`apk` 条目就是 APK 原始字节。
条目顺序：`config` → `bundle` → `apk`（App 侧处理与顺序无关，配置表的「热更覆盖」最后统一做）。
App 同时兼容 v1（只有 bundle+apk）的老套件。

**为什么不用 ZIP**：套件 >4GB 需要 Zip64，且 ZIP 要从**尾部**读中央目录，
而 SAF 给的 `content://` 流不保证可定位。自定义容器只做顺序读，实现最简单也最稳。

### 设备端安装

- **图形化**：App 内点「安装套件…」→ 系统文件选择器选 `.brk`
  （`Intent.ActionOpenDocument`，不需要存储权限）。
- **adb（免点选择器）**：先把套件推到 App 自己的外部目录，再用 `kitPath` 参数拉起：

```bash
adb push BlueRebirth-kit-1.4.140.brk \
  /sdcard/Android/data/com.blueoath.server/files/kit.brk
adb shell am start -n com.blueoath.server/crc649bef45f3691b5cb4.MainActivity \
  --es kitPath /sdcard/Android/data/com.blueoath.server/files/kit.brk
```

前置条件：系统里给 BlueRebirthApp 打开「**安装未知应用**」开关（App 会自己引导跳转），
且剩余空间 ≥ 套件大小 + 解出大小。安卓不允许静默安装 APK，最后那一次系统确认点是省不掉的。

> **API 28~30 的坑（已处理，勿删）**：安装会话不会自己弹确认框。
> 必须用 **可变广播**（`PendingIntent.GetBroadcast` + `Flags.Mutable`）接收会话状态，
> 收到 `PendingInstallStatus.PendingUserAction` 后**自己**启动结果里附带的
> `Intent.EXTRA_INTENT`，系统确认界面才会出现。相关代码在
> `KitInstallResultReceiver.cs` / `KitInstaller.InstallApkAsync`。
> 用 `PendingIntent.GetActivity` 或 `Flags.Immutable` 会让安装「无声无息地什么都不发生」。

### 实测数据（BlueStacks / Android 9）

| 阶段 | 结果 |
| --- | --- |
| 套件 | 4.48 GB（6738 文件 / 681 目录），push 后设备侧 md5 与本地一致 |
| 解包 | 6738 文件 / 681 目录，3533 MB，md5 不符 0（约 39 s） |
| 装客户端 | 1050 MB，PackageInstaller 会话 → 系统确认 → `INSTALL_SUCCEEDED`（约 15 s） |
| 版本对齐 | 自动写入 1.4.140，服务端重启后 `config dir` 指向套件解出的 `bundle/config` |
| 总计 | 约 **54 秒**（4.5 GB）；重复安装走「跳过写入」，幂等 |

### 配置表导入（App 自己的存储）

安装套件时，配置表会被导入 App 自己的存储目录：

```
/sdcard/Android/data/com.blueoath.server/files/config/
```

- 先解出套件 `config` 条目里的**全量 497 个**（若套件带了），再用资源包里的 72 个**覆盖**；
- 服务端解析配置表的优先级：**该目录** → `<bundle>/config` → 同机游戏目录 →
  历史解压目录 → APK 内置 asset。这样以后服务端升级换用别的配置表时，
  不再受「资源包那份 config 当前状态」影响。
- 导入/覆盖判定用 **md5**（先比大小再算 md5），不是只看尺寸 —— 资源包与整体里的
  `config_language.db` 就踩过「同尺寸不同内容」的坑。

### 版本号与自动更新

- **单一版本源 = `src/BlueOath.Launcher.Wpf/version.txt`**（PC 启动器沿用同一份）。
  安卓端在 csproj 里于**求值期**读该文件，生成
  `versionName = 1.2.1`、`versionCode = major*10000+minor*100+patch`。
- 打 `v*` tag 时，`pc-launcher-build.yml` 与 `android-launcher-build.yml` 会把
  PC 包与 `BlueRebirthApp-v<version>.apk` 挂到**同一个 Release**上。
- App 内「检查更新」直接读 GitHub Release：
  `GET https://api.github.com/repos/LunarConcerto/BlueRebirth/releases/latest`
  → 取 `tag_name` 与本机 versionName 比大小 → 在 assets 里找
  `BlueRebirthApp*.apk` → 下载到缓存目录 → 复用套件那套 PackageInstaller 安装。
- 测试钩子：`--es updateApi <url>` 可临时指定 Release API 地址。

### 热更下载：两套 URL，别漏了 `compressed/`

客户端热更时按 assetmap 的条目决定从哪条 URL 取文件：

| URL | 何时用 | 服务器该给什么 |
| --- | --- | --- |
| `/windows_android/<rel>_<crc32>` | assetmap 第 4 字段 = 0 的条目 | 原文件 |
| `/windows_android/compressed/<rel>_<MD5大写>` | assetmap 第 4 字段 > 0 的条目 | **gzip 压缩块**（客户端自己解压后按 MD5 校验） |

> **★ 踩过的坑（2026-10-11）**：包内没有 `compressed/` 那一份时，客户端会对每个文件重试
> 10 次后**整轮热更失败** —— 进度停在 76%、弹「更新需要下载 750.31MB」→ 点确认后报
> 「ダウンロードに失敗しました」，日志里 `NetworkFailTimes` 一路涨、服务端全是
> `compressed/config/*.db_<MD5> -> 404`。本项目走这条 URL 的只有 `config/*.db`
> （497 个里包里有的那 72 个，压缩后共 8.7MB）；`wwise/*` 的第 4 字段是 0，走普通 URL。

生成（已并入打包流程，见下）：

```bash
python android/tools/build-compressed-blobs.py             # 生成到 <merged>/compressed/
python android/tools/build-compressed-blobs.py --verify-only # 校验大小与 assetmap 一致
```

压缩参数必须与 CDN 一致：**gzip / level 6 / mtime=0**（否则压缩后大小与 assetmap 声明不符）。
**注意**：只有权限受限、客户端自己下载时才走这条路；正常「套件把资源铺进游戏目录」时
客户端根本不会来下载，所以这个坑只在没存储权限的设备上暴露。

`BundleHttpServer` 另有一层**现场 gzip 兜底**：包内缺 `compressed/` 时用原文件临时压一份返回
（字节数可能与 CDN 略有出入，但远好过 404），日志会打
`compressed 块缺失，已现场 gzip：…`。所以即便不重新打包资源包，只更新 APK 也能救回来。

### 界面结构（面向外行，文字最少）

主界面只保留三样东西，其余全部收进独立页面：

```
┌ 防倒卖横幅（一行小字）
├ 状态卡（唯一需要读的内容，整块着色）
│    · 能玩   → 绿卡「可以开始游戏了」
│    · 阻塞   → 红卡「还不能开始游戏」+ 点哪里
│    · 启动中 → 黄卡「正在启动…」
├ [ 开始游戏 ]   ← 主按钮（资源不齐时置灰）
├ [ 安装资源包 ] ← 主按钮
└ [停止][日志][检查更新][反馈]  ← 小按钮一行
```

- **`开始游戏` 一个按钮包办**：服务没起就先起（等就绪，最长 25s），起来后直接拉起游戏；
  使用者不需要理解「服务端 / 端口 / 资源包」。
- **不显示**端口号、路径、版本号、配置表数量、日志 —— 这些都在日志页。
  新版启动器只在真检测到新版本时，才额外浮出一条「有新版本 x.y.z 可用 + 立即更新」。
- **阻塞重点标注**：`EmbeddedServer.InspectResources` 不通过时，状态卡变红并直接写
  「点下面『安装资源包』」，`开始游戏` 按钮同时置灰（半透明）。
- **日志页 `LogActivity`**：主界面点「日志」跳进去；顶部是「诊断信息」（版本 / 资源 /
  配置表 / 客户端版本 / 关键路径），下面才是实时日志，并提供「复制日志」一键复制
  （诊断信息 + 日志，反馈时直接粘贴）。

### 反馈入口

主界面小按钮行的「反馈」用 `Intent.ActionView` 打开 <https://pd.qq.com/s/8gwks8zdo>
（腾讯频道「苍蓝誓约复原」），由系统交给浏览器 / 频道 App 处理。
若没有任何 App 能处理 http(s)（理论上极少），兜底把链接复制到剪贴板并 Toast 提示。
改地址只需动 `MainActivity.FeedbackUrl` 常量。

## 常见坑（务必先读）

1. **构建**：必须 clean build（增量重建 → 安装运行抛 `UnsatisfiedLinkError ... n_onCreate`）；
   含中文的工程路径会让 aapt2 报 APT2265（脚本用 ASCII 联接规避）。
2. **ABI**：不要锁 `armeabi-v7a`（x86 模拟器走 ARM 转译层会崩 .NET 运行时）；保持默认
   arm64-v8a + x86_64。也别设 `AndroidEnableAssemblyCompression=false`（会缺
   `compressed_assembly_count` 符号）。
3. **版本号**：服务端 `EmbeddedServer.ClientVersion` 必须等于**设备上资源 assetmap 的版本**。
   放入 1.4.140 资源包 → `1.4.140`；清了 sdcard 资源（回落 APK 内置）→ `1.4.90`。
   不一致会永远停在热更页。
   ★ 客户端那份 assetmap 在**游戏自己的**目录 `files/bundles/assetmap`（不是 App 的），
   套件安装会负责铺过去；只有 App 那份资源、游戏目录是空的话，客户端仍然显示 1.4.90。
4. **存储权限**：要把资源铺进游戏目录必须 `READ/WRITE_EXTERNAL_STORAGE`（API 28）。
   自动化测试可 `adb shell pm grant com.blueoath.server android.permission.WRITE_EXTERNAL_STORAGE`。
5. **资源不齐不会自动启动服务**：`EnsureStarted` 会先体检（assetmap + config_*.db），
   不齐就只在界面上报「资源不齐」。所以「装完套件但日志里没有服务启动」先看界面那行红字。
6. **设备全局代理**：BlueStacks 等模拟器若设了 `global http_proxy`，libcurl 连 127.0.0.1
   也会走代理 → 服务端收不到任何请求。测前执行：
   `adb shell settings put global http_proxy :0`
7. **端口占用**：测前停掉占用 19090/19191/19192/19193 的 adb reverse 保活脚本
   （否则 App 报 `Address already in use`）。
6. **大批量安装/推送**：`adb install` 对 1GB+ APK 会卡死，用
   `adb push` + `adb shell pm install`；`/sdcard/Android/data/...` 下 push 建不了嵌套目录，
   需先用 shell `mkdir -p`（`push-bundle.sh` 已处理）。
