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
| `build-server-app.sh` | 构建 BlueRebirthApp。路径含非 ASCII 时自动建 ASCII 联接；强制 clean build。 |
| `push-bundle.sh` | 把热更资源包推到设备（默认 → App 的 `bundle/`；也可指定游戏目录）。 |
| `build-merged.py` | 生成热更资源包 `merged/`（`new_bundles` 为主 + `apk_bundles`/`overlay` 补缺）。 |
| `verify-merged.py` | 用 1.4.140 的 assetmap 全量校验 `merged/` 的 md5 与大小。 |

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
