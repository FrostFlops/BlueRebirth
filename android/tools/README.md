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

### 配置库不再打进 APK

客户端 / 服务端 App / 热更资源包三件套必定同装，而**资源包里本就含 `config/`（72 个
`config_*.db`）**。因此服务端 App 启动时直接读资源包里的 `config/`（`EmbeddedServer.
ResolveConfigDirAsync`），APK 里那份 ~105MB 副本已删掉。

- 目录解析优先级：`<bundle>/config` → 同机游戏目录 `files/bundles/config`（Android 11+ 通常
  读不到，best-effort）→ 历史解压目录 → APK 内置 asset（仅自包含构建会有）。
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
| `verify-merged.py` | 用 1.4.140 的 assetmap 全量校验 `merged/` 的 md5 与大小。 |
| `build-install-kit.py` | 打包 / 校验 / 解包「安装套件」`.brk`（客户端 APK + 热更资源包合成单文件）。 |

## 安装套件（.brk）

把「客户端 APK + 热更资源包」合成**一个文件**，用户在 App 内用系统文件选择器选它，
App 自动解包资源并调 PackageInstaller 装客户端 —— 免 PC、免 adb。

```bash
# 打包（版本号自动从 assetmap 识别）
python android/tools/build-install-kit.py \
  --bundle android/_work2/merged \
  --client-apk android/finel_clients/BlueRebirth-client-1.4.140-planL.apk \
  --out android/finel_clients/BlueRebirth-kit-1.4.140.brk

# 校验（重算每个文件的 md5）
python android/tools/build-install-kit.py --verify-only <kit.brk>

# 解包（排障 / 端到端自检）
python android/tools/build-install-kit.py --extract-to <kit.brk> <输出目录>
```

格式 `BRKIT001`：`magic(8B) + headerLen(u64 LE) + header JSON + payload`。
payload 里 `bundle` 条目是「文件表」（`u8 kind / u16 pathLen / u32 dataLen / path / 16B md5 / data`，
`kind` 0=文件 1=结束 2=空目录），`apk` 条目就是 APK 原始字节。

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

安装套件时，`bundle/config/*.db` 会被**额外导入** App 自己的存储目录：

```
/sdcard/Android/data/com.blueoath.server/files/config/
```

服务端解析配置表的优先级变为：**该目录** → `<bundle>/config` → 同机游戏目录 →
历史解压目录 → APK 内置 asset。这样以后服务端升级换用别的配置表时，
不再受「资源包那份 config 当前状态」影响（资源包里仍保留 config，
因为客户端热更可能会来下载它）。导入是幂等的（同尺寸文件跳过）。

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

### 反馈入口

App 底部的「反馈」按钮用 `Intent.ActionView` 打开 <https://pd.qq.com/s/8gwks8zdo>
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
4. **设备全局代理**：BlueStacks 等模拟器若设了 `global http_proxy`，libcurl 连 127.0.0.1
   也会走代理 → 服务端收不到任何请求。测前执行：
   `adb shell settings put global http_proxy :0`
5. **端口占用**：测前停掉占用 19090/19191/19192/19193 的 adb reverse 保活脚本
   （否则 App 报 `Address already in use`）。
6. **大批量安装/推送**：`adb install` 对 1GB+ APK 会卡死，用
   `adb push` + `adb shell pm install`；`/sdcard/Android/data/...` 下 push 建不了嵌套目录，
   需先用 shell `mkdir -p`（`push-bundle.sh` 已处理）。
