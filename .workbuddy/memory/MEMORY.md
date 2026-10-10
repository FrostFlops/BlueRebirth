# 苍蓝誓约复原项目 · 长期项目笔记

## 目标
复原《苍蓝誓约》日服：PC 端已完成，安卓端（模拟器 + 真机）正在补齐。
只做日服，不做国服；接受重打包 APK。

## 当前推荐产物
- **安卓最终版（客户端本体）：`android/finel_clients/BlueRebirth-client-1.4.140-planL.apk`**
  = planK（NRE 修复 + sigaction 全中和 + SSL 绕过 + JSON 重定向）+ damageFac 伤害补丁。
  （原 `android/_work/BlueOathJP-planL.apk` 仍保留；其余 planB..K/planSelf 试错 APK 已清理。）
- **热更资源包：`android/finel_clients/hotupdate-bundle-1.4.140/`**（= `android/_work2/merged`，6738 文件/3.5GB）
- 服务端代码改动集中在 `src/BlueOath.Server/Protocols/BootstrapHttpResponder.cs`
- **工具链在 `android/tools/`（已入库）**：`build-server-app.sh` / `push-bundle.sh` /
  `build-merged.py` / `verify-merged.py`。用法见 `android/tools/README.md`。

## 独立服务端 App（2026-10 新方向，优先）
**放弃"自包含/合并进游戏 APK"，改独立 App 承载服务端 + 进程内 TLS。**
- 工程 `src/BlueOath.Android`，包名 `com.blueoath.server`，**桌面名 BlueRebirthApp**，
  带简易 UI（启动/停止/打开游戏/日志）。
- 游戏客户端(planL) **无需改动**，通过 loopback 连 `https://127.0.0.1:19090`。
- **构建用 `android/tools/build-server-app.sh`**（已内置下面三条铁律的规避）。
  原理：项目路径含中文会让 aapt2 读 `-A assets` 失败(APT2265) → 自动建 ASCII 联接
  `E:\boj → 项目` 再构建。
- **三条铁律**：① ABI 用默认 arm64-v8a+x86_64（锁 armeabi-v7a 在 x86 模拟器转译层会崩）；
  ② 别设 `AndroidEnableAssemblyCompression=false`（缺 `compressed_assembly_count` 符号）；
  ③ 改打包属性后必须真 clean（`mv obj` 挪走，`rm -rf` 会被安全守卫拦）。
- **测试前先杀 `keep_reverse.sh`**，否则它占用 19090/19191/19192/19193 → App 报 `Address already in use`。
- **端口**：19090 = TLS（SDK 引导）；19191 登录；19192 KCP；**19193 = 明文 HTTP（热更下载专用）**。
  热更下载走客户端原生 libcurl+OpenSSL（自带证书校验，不走 SSL 绕过），必须用明文。
- **★ 设备全局代理会毁掉一切**：BlueStacks 上 `global http_proxy` 若非空，
  libcurl 连 127.0.0.1 也会走代理 → 下载失败且服务端**看不到任何请求**。
  测试前 `adb shell settings put global http_proxy :0`。
- **热更 bundle**（`android/_work2/merged`，3.5GB）需拷到
  `/sdcard/Android/data/com.blueoath.server/files/bundle`（脚本 `android/tools/push-bundle.sh`）。
- **★ 配置库不再打进 APK**：客户端/服务端 App/热更包三件套同装，资源包里本就含
  `config/`（72 个 `config_*.db`），服务端启动时直接读 —— `EmbeddedServer.ResolveConfigDirAsync`
  → `<bundle>/config`，并通过 `ServerOptions.ConfigRoot`（`--config-dir=`）传给服务端。
  解析顺序：`<bundle>/config` → 同机游戏目录（scoped storage 通常读不到）→ 历史解压目录 →
  APK 内置 asset（仅自包含构建有）。APK 因此从 54.7MB 降到 45.9MB。
  ⚠ **别对该 App 用 `adb shell pm clear`**：它会清空外部目录，`bundle/` 会一起没。
  要重置就 `am force-stop` 后重启。
- 客户端请求名带后缀（如 `assetmap_1.4.140`），服务端需剥后缀找真实文件。
- **★★ 宣告版本号（EmbeddedServer.ClientVersion）必须 = 客户端当前 sdcard 上 assetmap 的版本**
  （客户端同时读 APK 内置 assetmap 与 sdcard 资源 assetmap，**以 sdcard 为准**）：
  · sdcard 放了 1.4.140 资源（含 `files/bundles/assetmap`=668147B）→ 服务端必须 **1.4.140**（推荐/历史可用态）；
  · 若 `pm clear` 清掉 sdcard 资源 → 客户端回落 APK 内置 1.4.90 → 此时才必须 1.4.90。
  写错就走 PATCH_TO_LATEST 去热更 → 永远停在热更/下载界面。
- 客户端资源目录：`/sdcard/Android/data/com.zephyrus.clsy.gp/files/bundles/`（结构同 `merged`，
  且**必须有 `bundles/assetmap` 本体**）。预置资源 + 上面版本对齐后，
  客户端会弹「ダウンロードには 0.00MB が必要」→ 点確認即可进游戏。
- **★ `merged` 里曾有一个错的 `config/config_language.db`**（同尺寸不同内容，旧 overlay 版）：
  md5 应为 `C1FB863A82E1A797FDFAD268A2B3ECB4`，正确文件在
  `android/_work2/new_bundles/config/config_language.db`。用
  `android/tools/verify-merged.py` 可全量校验 merged 与 assetmap（本次仅这 1 个不符）。

## 一键启动（PC 直连方案，旧）
```bash
# 1) 服务端（★ 必须带 --client-path，否则配置全空 → 界面混乱）
android/_work/run_server.sh
# 2) TLS 代理
tools/tls-loopback-proxy.py --port 19090 --backend-port 19193
# 3) adb reverse 四个端口（19090 19191 19192 19193）+ keep_reverse.sh 后台保活
```
adb 用 BlueStacks 自带：`C:/Program Files/BlueStacks_nxt_cn/HD-Adb.exe connect 127.0.0.1:5555`
（连接很不稳，**每条命令前都要重新 connect**；统一 `MSYS_NO_PATHCONV=1`）

## 关键约定 / 坑（反复踩过的）
- **服务端必须 `--client-path`**：ConfigDbLoader 从
  `<clientPath>/blueoath_Data/StreamingAssets/config` 读配置，漏了就 `loaded 0`。
- **IL2CPP dump 有两套，别混**：项目根 `il2cppdump/` 是 **PC x86** 的；
  安卓要用 `android/_work/il2cpp_android/out/`（方法同名但地址完全不同）。
- **安卓 libil2cpp.so：file offset == VA**，ARM 模式；函数地址从 `out/script.json` 取。
- **config 是否完整以 assetmap 登记数为准**：merged/设备 72 个是正确的（热更版），
  APK/PC 内置的 497 个是旧版本，不是"应有"集合。
- **判断伤害/功能是否正常，以用户实况为准**，别只信一次结算面板。
- BlueStacks adb 与 platform-tools adb 争抢 5037 → reverse 会被清空，需保活脚本。
- Bugly 崩溃现场免 root 可读：
  `/sdcard/Android/data/com.zephyrus.clsy.gp/cache/bugly/*.logcat.sended`

## UI 自动化坐标（1920×1080）
海域 tab=(546,945)、1A 关卡=(345,360)、面板出撃=(1710,990)、
確認=(1185,765/771)、SKIP=(1815,55)。复用脚本 `battle_test3.sh` / `battle_test4.sh`。
Read 工具对相同像素截图会去重，缩图存 JPG 可强制查看。

## 收尾（2026-10-10）
- 稳定产物已备份到 `android/finel_clients/`（客户端 APK + 热更包 + 截图，**不入 git**）。
- 冗余清理：`android/` 从 ~62GB 降到 ~21GB（删掉 planB..K/planSelf 试错 APK、overlay/合并方案
  中间物、试错日志截图、jadx/java 反编译、self_build 等）。保留 `il2cpp_android/`、`patch/`、
  分析脚本与 `new_bundles/`。
- `.gitignore` 已加 `/android/_work/`、`/android/_work2/`、`/android/finel_clients/`、`/android/*.apk`、`/android/*.zip`。
- CI：`.github/workflows/ci.yml` → `pc-launcher-build.yml`；新增 `android-launcher-build.yml`
  （构建 BlueRebirthApp，CI 无配置库故不计入内置配置）。
- App 名改为 **BlueRebirthApp**；UI 里「热更 bundle 目录：…」提示已删。
