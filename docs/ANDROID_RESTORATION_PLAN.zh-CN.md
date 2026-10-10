# 手机端（Android）复原方案

> 编制日期：2026-10-05（v5 — **热更 bundle 下载链路已打通，客户端可真实从本地服务器下载 3.0 GB 热更包**）
> 适用客户端：JP Android `苍蓝誓约日服.apk`（versionName **1.4.90**，versionCode 407，包名 `com.zephyrus.clsy.gp`）
> 关联文档：[项目概述](project-overview.md) · [Roadmap](ROADMAP.zh-CN.md) · [复盘总结](retrospective.md) · [JP 传输观察](research/transport.md)

---

## 零、本方案的确立前提（用户已确认）

| # | 决策 |
| --- | --- |
| 1 | **模拟器与真机二者兼容**；前期以**模拟器**为调试环境 |
| 2 | **只做日服**，不做国服 |
| 3 | **接受重打包 APK** 作为交付形态 |
| 4 | 调试期**直接用已连接的模拟器**（`emulator-5554`） |
| 5 | `Android_苍蓝誓约_这颗芯有点凉.zip` **就是当年完整的运行时热更缓存** |

---

## 一、结论先行：核心难点已攻克

**手机端复原的「最不确定环节」（网络重定向 + 热更下载）已经在模拟器上端到端跑通，且完全不需要 root、不需要 Frida、不需要 native hook。**

具体已打通到：客户端真实点击「ダウンロード」后，逐包向本机服务器请求
`/windows_android/<rel>_<crc>`，服务器剥离 crc 后缀后回原始 bundle 字节并落盘。

**当前唯一实质障碍**是资源可得性：assetmap 声明的 5766 个 bundle 中有 **196 个**
在 APK 与热更缓存中**均不存在**，且 PC 端因 Unity 版本不同（2018.2.14f1 vs 2018.2.4f1）
无法替代 —— 详见 **A2.4**。

### 1.1 根本原因：域名在明文 JSON 里，不在 so 里

静态扫描发现，**游戏域名在整个 APK 的任何 `.so` 中都不存在**（`libil2cpp.so` 内含 0 处 `blueoath`），
而是放在 `assets/` 下的**明文 JSON**：

| 文件 | 内容 | 状态 |
| --- | --- | --- |
| **`assets/platform/game_config.json`** | **`host` / `track_host` / `crashHost`** | ✅ 明文 |
| **`assets/urls.json`** | SDK 登录/网页 URL（`ONLINE_LOGIN_URL` 等） | ✅ 明文 |
| `assets/config.json` | 屏幕方向 | ✅ 明文 |
| `assets/channel_sdkconfig.json` | Google 内购 SKU | ✅ 明文 |
| `assets/supplierconfig.json` | 渠道配置 | ✅ 明文 |
| `assets/platform/config.json` | — | 🔒 加密（`F0F0F0F0` 头），暂未解 |

`game_config.json` 原文：

```json
{
    "host":"https://mapijpshipgirl.blueoath.com",
    "host_debug":"https://mapijpshipgirl.staging.blueoath.com",
    "track_host":"https://haina.blueoath.com",
    "track_key":"ee178d2c8a3f38c9df75",
    "gn":"jpshipgirl",
    "breakpad":"on",
    "crashHost":"http://debug.zuiyouxi.com/",
    "scanMessage":{ "2":"…「蒼藍の誓い-ブルーオース」Windows版が作成したQRコード…" },
    "localized":{ "1":"网络不可用" }
}
```

> 💡 `scanMessage` 里「扫描 **Windows 版**生成的二维码」正解释了 PC 侧 `TrySetSimulationMode`
> 把 `pl` 伪装成 `google_windows` 的由来 —— **两端本就是配对设计**。

### 1.2 实测验证结果（决定性证据）

只做了三件事：**改一个 JSON → 重打包签名 → 建 adb reverse**。结果：

**客户端 logcat（证明它接受了新 host）**：
```
BTLOG [Platform.cpp:950] host:http://127.0.0.1:19090/phone/getPlData/getPlData/
```

**本地探针服务端（证明请求真的到了本机）**：
```
CONNECT from ('127.0.0.1', 6697)
  RECV 299B: b'POST /phone/getPlData/getPlData/ HTTP/1.1
              Host: 127.0.0.1:19090
              Content-Type: application/x-www-form-urlencoded'
  RECV 1159B: b'POST /c.gif? HTTP/1.1'      ← haina 埋点也一并走本地
```

**→ A2「网络重定向」目标达成。之前方案里最担心的 HTTPDNS / TLS / root 三座大山，全部绕过了。**

**现已把探针换成真实 `BlueOath.Server`，完成端到端验证**（详见 §6 A2.1 / A2.2）：

- 服务器侧捕获到完整的 8 条引导请求序列，**零新增路由**（仅补了 `supernotice` / `bugly/status` 两条无害兜底）;
- 修掉一个真实阻塞缺陷：`getversion` 的 `static_url` 不能为空，否则客户端抛
  `依赖工具包错误: serverPath为空, 加载不了资源!`；
- 修复后客户端**已进入正常热更界面**（`HotPatchPage` / `PatchGroup:ShowWebView()`），
  不再是错误弹窗。当前推进到 SDK 登录 WebView（`ActionType=29`）。

### 1.3 意外之喜：服务器无需新增任何接口

`BlueOath.Server/Protocols/BootstrapHttpResponder.cs` **已经实现了 Android 客户端需要的全部 `/phone/*` 接口**：

```
✅ /phone/switch/getstate    ✅ /sdk/gettime           ✅ /phone/applereview
✅ /phone/getPlData/getPlData  ← Android 首个请求     ✅ /phone/getversion/
✅ /login?   ✅ /gethash      ✅ /phone/serverlist/    ✅ /phone/loginrole/
✅ /phone/platform/getPlatformUserInfo   ✅ /phone/getuserextra/   ✅ /c.gif
➕ /phone/supernotice（本次补，空 HTML）  ➕ /bugly/status（本次补，{}）
```

且 `getPlData` 的响应体里**已硬编码 `"pl":"google_windows","os":"android"`** —— 正是 Android 客户端期望的配对。
**PC 为 Windows 客户端写的 HTTP 引导层，与 Android 需要的一模一样（同一套 SDK）。**

### 1.4 各层复用程度

| 层 | 复用程度 |
| --- | --- |
| **服务端** | ✅ **完全复用**，零改动（loopback + adb reverse 回连源 = 127.0.0.1，接口已覆盖） |
| **协议层** | ✅ 完全复用（11 字节头 + protobuf + KCP，平台无关） |
| **配置库** | ✅ 高度复用（497 个 db 中 460 个与 PC 字节完全相同） |
| **资源层** | ✅ 复用甚至更简单（`bundles/windows/` 与 PC 共用，bundle 为明文 `UnityFS`） |
| **客户端接入** | ✅ **已跑通**（改明文 JSON + 重打包 + adb reverse，无 hook、无 root） |
| **TLS** | 🔄 优先走「明文 HTTP 降级」，大概率无需定位证书校验点 |
| **SDK 登录** | 🔄 仍需验证；但类名与 PC 同名，语义可照搬 |

---

## 二、现状盘点：`android/` 两份产物

### 2.1 `苍蓝誓约日服.apk`（1.10 GB / 6502 条目）

| 维度 | 观测值 | 对复原的含义 |
| --- | --- | --- |
| 版本 | `versionName=1.4.90`，`versionCode=407`，`minSdk=16`，`targetSdk=30` | 与日服 PC 1.4.0 同代 |
| 包名 / 入口 | `com.zephyrus.clsy.gp` / `com.Babel.GD.MainActivity` | 标准 Unity 导出壳 |
| **ABI** | **仅 `lib/armeabi-v7a`（32 位 ARM）**，无 arm64-v8a | 需 32 位 ARM 环境（模拟器靠转译，见 3.2） |
| 引擎核心 | `libil2cpp.so` 54.7 MB、`libunity.so` 18.9 MB、`libmain.so` | 与 PC `GameAssembly.dll`/`UnityPlayer.dll` 同源 |
| IL2CPP 元数据 | magic `0xFAB11BAF`，**version 24** | 与 PC 同代；APK 自带 `SymbolMap-ARMv7` / `-ARM64` |
| Lua | `libxlua.so`，Lua 5.3.5 | **Mod（xLua loader）在 Android 同样成立** |
| SDK | `libnew_sdk.so`(C++) + `classes*.dex`(Java) | PC 的 `new_sdk.dll` hook 不可直接复用 |
| **配置入口** | **`assets/platform/game_config.json` + `assets/urls.json`（明文）** | ⭐ **重定向的关键抓手** |
| 资源容器 | `assets/assetpack/{bundles,config,movie}` | Unity Addressables AssetPack |

**资源结构**：

```
assets/assetpack/
├── bundles/            1339 个 bundle（魔数 UnityFS，明文，未加密）
│   ├── windows/  517   ← 平台名直接叫 windows，与 PC 共用
│   ├── share/    372 · character_q/ 195 · atlases/ 141
│   ├── effects/ 61 · modelsprite/ 23 · font/ 14 · shader/ 2 ...
│   └── assetmap        ← 本地 catalog，不含任何 http(s) URL
├── config/        497 个 config_*.db（标准 SQLite format 3）
└── movie/         40 个 .mp4
```

### 2.2 `Android_苍蓝誓约_这台芯有点凉.zip`（2.64 GB）— 已确认为完整热更缓存

设备端数据目录快照（`/sdcard/Android/data/com.zephyrus.clsy.gp/`，2022-01-23）：

```
files/bundles/
├── characters/ 4641  ← 角色资源（APK 内没有，靠运行时下载）
├── wwise/      845   ← 音频
├── config/ 132 · scenes/ 124 · uitextures/ 108
├── windows/ 37 · atlases/ 32 · share/ 21 ...
└── assetmap
```

> ✅ 用户已确认这是**完整的**热更缓存 → 可直接作为 A5 资产本地化的完整数据源，**不必再依赖联网下载**。

### 2.3 APK 配置库 vs PC 配置库

| 指标 | 结果 |
| --- | --- |
| APK / PC `config_*.db` 数量 | **497 / 497**，一一对应，无一方独有 |
| 字节完全一致 | **460 / 497** |
| 有差异 | 37（如 `config_shop_goods.db` 1,945,600 vs 2,088,960） |

**结论**：APK 是同一款 JP 客户端略早的构建。现有 `export-config.bat` / `import-config.bat` / `generate-config-cs.bat` **可直接吃 APK 内的 db**。

---

## 三、实测环境（本机现成，无需新建）

### 3.1 设备与工具链

| 项 | 值 |
| --- | --- |
| 调试设备 | `emulator-5554`（**已连接且游戏已实测运行**）；另有 `emulator-5562` offline |
| 机型伪装 | 三星 SM-G998B，**Android 9 / API 28**，`ro.hardware=exynos2100` |
| 内核 | 4.19.195 x86_64（宿主 x86_64） |
| root 状态 | `ro.secure=0`、**SELinux `Disabled`**、`adb root` 可执行（su 在 `/sbin/su`）；**当前方案不依赖 root** |
| adb | `E:/Develops/AndroidTools/SDK/platform-tools/adb` |
| build-tools | `E:/Develops/AndroidTools/SDK/build-tools/37.0.0`（`apksigner` / `zipalign`） |
| JDK | `C:/Program Files/Java/jdk-17`（`keytool` / `jarsigner`） |
| apktool / jadx | `E:/Develops/AndroidTools/apktool.jar`(2.10.0) · `jadx-1.5.0` |

### 3.2 ARM 转译机制（「模拟器兼容」的可行性依据）

```
ro.product.cpu.abilist      = x86_64,x86,arm64-v8a,armeabi-v7a,armeabi
ro.dalvik.vm.isa.arm        = x86
ro.dalvik.vm.native.bridge  = libnb.so        ← Native Bridge（Houdini 类）ARM 转译
游戏 primaryCpuAbi          = armeabi-v7a
```

**x86_64 宿主通过 Native Bridge 转译执行 32 位 ARM 库；已实测游戏能完整运行到登录界面。**

### 3.3 运行时实测日志（SDK 带源码行号，属调试构建）

```
SDK.BabelTimeSDKManager:OnCallBackImpl / TickLoop / SetReview / SetConfigResult
    → 与 PC 完全同名的 IL2CPP 类（PC 侧 hook 的正是 BabelTimeSDKManager.Login）

BTLOG [Platform.cpp:950]  host:https://mapijpshipgirl.blueoath.com/phone/getPlData/getPlData/
BTLOG [Platform.cpp:951]  param:uuid=…&pl=google&os=android&gn=jpshipgirl&other_pl=zyxphone&time=…&sign=…
BTLOG [BTHttpClient.cpp:199] response Buffer:Couldn't resolve host 'mapijpshipgirl.blueoath.com'
BTLOG [Platform.cpp:913]  request failed! …, response code:-1
BTLOG [Platform.cpp:919]  tag = 1007 request failed = {"errornu":"-1","errordesc":"网络不可用2-901"}
```

- 首次失败点是 **libcurl DNS 解析**（`Couldn't resolve host`）→ **请求根本没走到 HTTPDNS**。
- `BTSDK_JAVA: DBCipherManager` + `https://haina.blueoath.com/c.gif?` = haina 埋点（本地 sqlcipher）。
- `NativeHelper: Installing ARM Unity App libs` = 转译器安装 ARM 库的证据。

**域名集合**：`mapijpshipgirl.blueoath.com`、`haina.blueoath.com`、`jpmsdk.blueoath.com`、`haina.zuiyouxi.com`、`haina.one-piece.cc`、`static1.zuiyouxi.com`

---

## 四、与 PC 方案的差异对照

| 能力 | PC 现有实现 | Android 实测结论 | 复用性 |
| --- | --- | --- | --- |
| 本地服务端 | `BlueOath.Server`（loopback） | **adb reverse 回连源=127.0.0.1 → 零改动可用，接口已全覆盖** | ✅ **完全复用** |
| 域名重定向 | Winsock IAT hook `getaddrinfo` | **改 `game_config.json` 明文 → 已实测跑通** | ✅ **已解决** |
| 协议 / 配置知识库 | `BlueOath.Tools`、`docs/*-catalog` | 配置格式一致；IL2CPP 需对 APK 重跑 dump | ✅ 大半复用 |
| TLS 证书信任 | patch `UnityPlayer.dll!0x8E1573` | **优先「明文 HTTP 降级」绕开**；必要时再定位 `libunity.so` | 🔄 降级方案优先 |
| SDK 登录绕过 | `new_sdk.dll` hook + `BabelTimeSDKManager.Login` | 类名与 PC **同名**，语义可照搬；`libnew_sdk.so` + dex | 🔄 语义复用 |
| 注入方式 | x86 DLL + xinput 劫持 | **重打包 APK（首选）**；Frida 仅用于取证 | 🔄 换成重打包 |
| Mod（xLua） | Payload 钩 `lua_pcallk` | `libxlua.so` 存在，Lua 5.3.5 相同 | 🔄 思路复用 |
| 资源 | 本地 StreamingAssets | Addressables AssetPack（`windows` 平台、`UnityFS` 明文） | ✅ 更简单 |

### 仍需留意的 Android 特性

**(1) HTTPDNS 存在但未触发。** `libnew_sdk.so` 导出 `bt::HttpDNS::getIp/setIpsToLocal`、`bt::DNSAli::fetchIp`、`bt::DNSTencent::fetchIp`。
当前请求死在 libcurl DNS；但因我们**直接指向 `127.0.0.1`（不做解析）**，HTTPDNS 已被绕开。若后续出现域名解析行为，`setIpsToLocal(host,ip)` 是现成的二道保险。

**(2) 两条并行的网络路径。** Android 侧 SDK 走的是 **HTTP `/phone/*` 接口**（非 UnityTLS protobuf）。
好消息：`BootstrapHttpResponder` 已覆盖；坏消息：**游戏正文可能仍走 UnityTLS + KCP**，需在 A3 验证。

---

## 五、推荐路线（已据实测收敛）

| 路线 | 说明 | 定位 |
| --- | --- | --- |
| **A. 重打包 APK（明文 JSON 改 host）** | 改 `game_config.json` → 重签 → `adb reverse` | ✅ **主路线（已跑通）** |
| **B. Frida 动态注入** | 仅在需要挂钩 TLS/登录时使用 | 按需取证 |
| **C. 改 hosts / hook getaddrinfo** | — | ❌ **已弃用**（system 只读，且被 HTTPDNS 威胁；已被 A 全面取代） |

---

## 六、分阶段实施计划

### A1 取证与基线
- [ ] Il2CppDumper 对 `libil2cpp.so`（armeabi-v7a）+ `global-metadata.dat`（version 24）出 dump，
      与 `il2cppdump/dump.cs` **逐类型 diff**，确认 1.4.90 相对 1.4.0 的增量。
- [ ] jadx 反编译 `classes*.dex`，定位 `com.platform.main.sdk.*`（登录）、`HainaReport`（埋点）。
- [ ] 解 `assets/platform/config.json` 的加密（`F0F0F0F0` 头）。
- [ ] 建立 Android 基线哈希（`libil2cpp.so`/`libunity.so`/`libnew_sdk.so`/`libxlua.so`）写入 `baseline.json`。

### A2 网络重定向 —— ✅ **已完成（实测跑通）**
- [x] 定位域名配置入口 = `assets/platform/game_config.json`（明文）
- [x] 改 `host` / `track_host` / `crashHost` → `http://127.0.0.1:19090`，`breakpad=off`
- [x] 外科手术式重打包 + `apksigner` 自签 + `adb install` + `adb reverse`
- [x] **验证：客户端请求成功到达本机**（logcat + 探针双向印证）
- [x] 把探针替换为**真实 `BlueOath.Server`** —— ✅ HTTP 引导全链路应答
- [x] 修复 `getversion` 的 `serverPath为空` 崩溃（`static_url` + `tar_version` + `path` 三项）—— ✅
- [x] **新增 `/windows_android/<rel>` 热更下载路由，客户端可真实下载 bundle** —— ✅
- [x] 确认资源可得性：assetmap 5766 项中 APK∪缓存覆盖 5570，**真缺 196** —— ✅ 已量化
- [ ] 剩余：196 个缺包的处理（见 A2.4）/ `urls.json` 的 SDK 登录 URL（`jpmsdk.*`）本地化

#### A2.1 与真实服务器端到端验证（✅ 2026-10-05 实测通过）

启动真实服务器（stdout 首行输出 `{"ready":true,...}`）：

```bash
"C:/Program Files/dotnet/dotnet.exe" run --project src/BlueOath.Server/BlueOath.Server.csproj \
    --no-build -- --port=19090 --region=jp --data=../../runtime/jp \
    --game-login-port=19191 --kcp-game-login-port=19192
adb -s emulator-5554 reverse tcp:19090 tcp:19090
adb -s emulator-5554 reverse tcp:19191 tcp:19191
adb -s emulator-5554 reverse tcp:19192 tcp:19192
```

服务器 `BootstrapHttpSession` 捕获到的完整引导序列（**无需新增任何路由**，原有实现已全覆盖）：

| # | 请求 | 应答 |
| --- | --- | --- |
| 1 | `POST /phone/switch/getstate` | `{"errornu":"0","DNS_sw":{"state":1}}` |
| 2 | `POST /phone/applereview/` | `{"errornu":"0","applereview":1}` |
| 3 | `POST /phone/getPlData/getPlData/` | `data{networkCheck,uuid,pid,serverId:"jp",...}` |
| 4 | `POST /phone/getversion/` | `script[{...,"tar_version":"1.4.0"}],static_url` |
| 5 | `POST /index.php?` | `ok` |
| 6 | `POST /c.gif?` | `ok` |
| 7 | `GET /phone/supernotice/?...` | 空 HTML（本次补路由，原先 501） |
| 8 | `POST /bugly/status` | `{}`（本次补路由，原先 501） |

#### A2.2 `serverPath` 不能为空（★ 关键修复，真实成因有三项）

原始实现把 `static_url` / `spare_static_url` 都填空串、且 `script[0].path` 也为空，
客户端随即崩到：

```
热更逻辑错误:依赖工具包错误: serverPath为空, 加载不了资源! SDK整体报文:{...}
```

**逆向定位**（PC `GameAssembly.dll`，`tools/disasm.py` + capstone，C# 源码同源故逻辑等价）：

| 方法 | RVA | 说明 |
| --- | --- | --- |
| `StateChecker.OnNeedHotPatchGetServerPathBack` | `0x3DF460` | 热更路径回调，常规/异常分叉 |
| `StateChecker.OnCallBack` | `0x3DF020` | 常规路进热更 UI；`0x103DF1F1` 处 `call 0x11633df0` 抛异常 |
| `SDKVersionGetter.Request` | `0x2E1700` | 尾调用真实实现 |
| `SDKVersionGetter.Request` 实体 | `0x2CF560` | 塞 5 个回调参数后 `call 0x102CFB80` 比对版本/装配路径 |
| `BundleDownloadInfo.GetPostFix` | `0x207C60` | 下载 URL 后缀 = `"_" + crc`（见 A2.3） |
| `BundleDownloadInfo.IsDownloadCorrect` | `0x207C80` | 仅校验路径后缀，不校验内容 |

`StateChecker` 字段布局（`dump.cs:519211`，TypeDefIndex 10907）：
`serverVersion(0x10)` `errType(0x14)` `versionGetter(0x18)` `successCallBack(0x1C)`
`msgCallBack(0x20)` `state(0x24)` `isLastUpdateFinished(0x28)` `infoGetter(0x2C)`
`downloadState(0x3C)` `packageUrl(0x40)` `packageMd5(0x44)` `missingState(0x48)`。

**修复**（`src/BlueOath.Server/Protocols/BootstrapHttpResponder.cs`）——

判定 `serverPath` 是否为空**同时取决于三个字段**，缺一不可：

```csharp
var staticUrl  = $"http://127.0.0.1:{_endpoints.Port}/";  // ① CDN 根，空串即失败
var tarVersion = options.Profile.ClientVersion;           // ② 必须 == APK 内 assetmap 版本
var scriptPath = "windows_android/";                      // ③ script[].path，空串同样判空
```

| # | 字段 | 修复前 | 修复后 | 说明 |
| --- | --- | --- | --- | --- |
| ① | `static_url` / `spare_static_url` | `""` | `http://127.0.0.1:19090/` | 空串直接失败 |
| ② | `script[0].tar_version` | `"1.4.0"`（PC Profile） | `"1.4.90"`（`--client-version=` 覆盖） | 不等则走 `PATCH_TO_LATEST` 而非 `NO_NEED_DOWNLOAD` |
| ③ | `script[0].path` | `""` | `"windows_android/"` | **决定性**：`serverPath = static_url + path`，空串照样判空 |

> ★ 纠错记录：先前一度认为仅补 `static_url` 即可，但实测错误依旧；补上 `path` 之后
> `serverPath为空` 出现次数才真正归零。**三项必须同时满足。**

**验证结果**：重启客户端后 `serverPath为空` 出现次数 = **0**，日志推进到
`HotPatchPage:SwitchToState(GroupInterface)` / `PatchGroup:ShowWebView()` /
`HotPatchManager:OnPrepareComplete()` —— 已进入正常热更界面（不再是错误弹窗），
且界面显示 `バージョン内容: 1.4.90` 与绿色「ダウンロード」按钮。

#### A2.3 热更 bundle 下载路由（✅ 2026-10-05 实测跑通）

`tar_version` 对齐后客户端不再走 `NO_NEED_DOWNLOAD`，而是**真的开始下载 3.0 GB 热更包**。
下载请求直接打到引导端口，形如：

```
GET /windows_android/characters/c_cl_ninghai_jp/animssplits/packages_0_2423299669 HTTP/1.1
```

**`_<crc>` 后缀是客户端拼的，不是磁盘文件名的一部分。**

- `BundleDownloadInfo.GetPostFix()`（`0x207C60`）实现即 `"_" + crc.ToString()`：
  `DownloadInfo` 布局 `name(0x8)/needDownloadSize(0x10)/isCompressed(0x18)/state(0x1C)`，
  `BundleDownloadInfo.crc` 位于 `0x20`；反汇编为 `add [ebp+8],0x20` 后 tail-call `uint→string`。
- 因此服务端必须**先剥离末段 `_<纯数字>` 再查表**，真实文件名 = assetmap 的 key。

**assetmap 权威格式**（APK 内 `assets/assetpack/bundles/assetmap`，UnityFS/2018.2.4f1，
`flags 0x43`；blocksInfo 在 header(49) 之后，数据块自偏移 214 起共 20 block）解压后为文本表：

```
<bundle 相对路径>,<crc>,<md5>,<size>
characters/c_cl_ninghai_jp/animssplits/packages_0,2423299669,b5a9cfc27f7056cb1cb8fe9f4de29349,142713
```

> ⚠️ 表中的 `md5` **不是文件原始 MD5**（实测该文件真实 md5 = `bc7d30726a...`），
> **只有 `size` 与 `crc` 可信**。用 md5 做校验会全部落空。

**服务端实现**（`BootstrapHttpResponder.TryResolveBundleFile`）：

1. 从请求行取 `/windows_android/<rel>`，URL 解码、去查询串；
2. 末段 `_<纯数字>` 剥离（`suffix.All(char.IsAsciiDigit)`）；
3. 与 `--bundle-root` 拼接后 `Path.GetFullPath` 归一化，并校验仍在根目录内（防目录穿越）；
4. 命中 → `200 application/octet-stream` 回原始字节；缺包 → `404`（区分路由未命中的 `501`）。

> 配套：`BootstrapHttpResponse.Body` 类型由 `string` 改为 `byte[]`（新增 `Text(...)`
> 工厂做 UTF-8 编码），否则 3 GB 级二进制无法安全往返；`BootstrapHttpSession` 直接写 `byte[]`。

**实测结果**（模拟器 `emulator-5554`，APK 包名 `com.zephyrus.clsy.gp`）：

- 服务端捕获到连续数十条 `/windows_android/...` 请求（`packages_0..11`、`meshs_mats`、
  `models_facial_turndata`、`texturessplits/*`），客户端侧真实落盘；
- 本地 `curl` 校验：带 crc 后缀的请求返回 `200 / 142713 bytes`，与源文件 `cmp` **完全一致**；
  缺包返回 `404`；目录穿越被拒；
- 单测 `HotPatchBundleDownloadTest` 通过，全量 **37 PASS / 0 FAIL**。

#### A2.4 资源覆盖缺口（★ 当前阻塞点）

`assetmap` 声明 **5766** 个 bundle，实际可得：

| 来源 | 数量 | 说明 |
| --- | --- | --- |
| APK 内 `assets/assetpack/bundles/` | 1339 | 随包基线 |
| 热更缓存 zip `files/bundles/` | 5310 | 运营方留的热更集 |
| **两者并集** | **5570** | |
| **真缺** | **196** | 34 个角色受影响 |

- 缓存中有 980 个 assetmap 未声明的文件（`config/*.db` + `assetmap` 本身），
  属 md5 映射的另一套机制，**不是 bundle**。
- 真缺 196 项：185 个 `characters/*`、8 个 `blade_01_0X`、2 个 `uitextures`、1 个 `scenes`。
  其中 **10 个角色整套缺失**（19/19 分片），**7 个角色仅缺 1 个分片**。
- ★ **PC 端 bundle 不能直接补齐**：Android APK 是 Unity **2018.2.4f1**，PC 是
  **2018.2.14f1**，同名 bundle 字节数不同（PC 普遍大 5 B ~ 7 KB），序列化格式不同，
  且客户端按 assetmap 的 `crc` 校验 → 无法替代。

**当前失败点**：客户端启动即预载标题/登录页的两个默认角色
（宁海 `c_cl_ninghai_jp`、平海 `c_cl_pinghai_jp`），其中
`c_cl_ninghai_jp/texturessplits/packages_0`（crc=877721053, size=947953）三处皆无，
重试约 10 次后弹「ダウンロードに失敗しました」并退出：

```
熱更逻辑错误:ダウンロードcharacters/c_cl_ninghai_jp/texturessplits/packages_0_877721053時に
異常を発見しました 最終的にダウンロード成功の是非False
CDNUrl:http://127.0.0.1:19090/windows_android/ NetworkFailTimes:...
```

**候选对策**（按推荐度）：

1. **补齐 196 个缺包**：从其他存档/官网残留 CDN 镜像收集（需联网，当前已停服）。
2. **本地占位 + 放行**：让服务端对缺包返回最小合法 UnityFS 空 bundle，并同步改写
   APK 内 assetmap 去掉这些条目 → 相关角色会缺贴图/动作，但**可进游戏**。
   （需评估客户端对 crc 不匹配的容忍度；`IsDownloadCorrect` 只查后缀，实际校验点在
   Unity `AssetBundle.LoadFromFile` 的 crc 参数，可置 0 关闭。）
3. **补齐 config/*.db 后再评估**：先确认这 196 项是否仅影响少数角色的皮肤/特效。

### A3 TLS 处理
- [ ] **首选：明文 HTTP 降级**（已把 host 改成 `http://`；只需确认 SDK 接受非 TLS）
- [ ] `assets/urls.json` 的 `jpmsdk.blueoath.com` 本地化 —— **当前实测阻塞点**：
      Java 侧 `newsdk.base.BaseSdk.httpGet` 抛 `UnknownHostException`，
      该域名不经 `game_config.json`，硬编码在 `urls.json`
- [ ] 若游戏正文仍强行走 TLS：Frida 定位 `libunity.so`/`libil2cpp.so` 证书校验点，
     参照 PC `0x8E1573` 语义（mask `NOT_TRUSTED 0x08`），固化 RVA + 字节签名 + SHA 门控
- [ ] 备用：`SSL_CTX_set_verify` / `X509_verify_cert` 调用点

### A4 SDK 登录绕过
- [ ] 验证客户端能否走通 SDK 登录（依赖 A2/A3 完成）
- [ ] **当前实测进度**：客户端已推进到 `ActionType=29` → `BaseSdk.openCustomWebView`
     （`BaseSdk.java:1467`）→ `InnerBrowser.openCustomWebView`（`InnerBrowser.java:112`），
     即 SDK 在弹登录 WebView。需在此处旁路
- [ ] 若卡住：定位 Java `com.platform.main.sdk.*` 与 native `libnew_sdk.so` 登录函数
- [ ] 照搬 PC 语义（hook `new_sdk.login` → 派发登录结果事件）；**实测已确认同名类存在**


### A5 资源与配置本地化 + 一键启动
- [ ] `assetpack/config/*.db` 用现有 `export-config.bat` 抽样跑通（预期一致）
- [ ] 对 **37 个差异表**逐一 diff，决定以哪版为准（建议 APK 1.4.90 为准）
- [ ] Addressables 本地化：`--bundle-root` 指向 `runtime/android/bundles`（**已可用**）
- [ ] **处理 196 个缺包**（A2.4）：补齐 / 占位放行 / 改写 assetmap
- [ ] 整合为「重打包 APK + 本地服务端 + adb reverse」一键启动（对照 PC `run-game.bat`）
- [ ] 固化重打包流水线（1.1 GB 重打包耗时需评估；`zipalign` + 签名策略）

---

## 七、模拟器与真机兼容设计

| 关注点 | 模拟器 | 真机 | 统一方案 |
| --- | --- | --- | --- |
| 网络回连 | `adb reverse`（已实测） | `adb reverse`（USB，同样适用） | **统一 adb reverse** |
| ABI | x86_64 + Native Bridge 转译 armeabi-v7a | 需支持 32 位 ARM | 只编 armeabi-v7a |
| 域名指向 | 改 `game_config.json` 明文 | 同左 | 统一改配置 |
| root | 具备但**不需要** | 通常无 | 交付形态**不依赖 root** |
| 签名 | 自签调试密钥 | 同左 | 统一重签流水线 |

> 设计原则：**调试期可用 root/Frida，但交付 APK 不得依赖 root** —— 与 PC「无需外部环境、一键启动」目标一致。

---

## 八、风险与对策

| 风险 | 级别 | 对策 |
| --- | --- | --- |
| 游戏正文可能仍走 UnityTLS（非 HTTP） | 中 | A3 先试明文降级；不行再定位证书校验点 |
| HTTPDNS 在后续阶段接管 | 低 | 已直接指 `127.0.0.1` 绕开；`setIpsToLocal` 兜底 |
| SDK 登录流程仍需打通 | 中 | 类名与 PC 同名，语义照搬；优先复用 PC 实现 |
| 仅 armeabi-v7a，真机兼容性 | 中 | 目标真机需支持 32 位 ARM |
| 1.1 GB 重打包耗时长 | 低 | 外科手术式替换（非全解包）已验证可行；可缓存基础包 |
| 转译层导致 hook 不稳定 | 低 | 主方案已不依赖 hook |
| 热更资产版本与 APK 不一致 | 低 | zip 已确认为完整缓存，以 `assetmap` 做一致性校验 |

---

## 九、复用清单

- ✅ **`BlueOath.Server`** —— **零改动**（loopback 可达 + `/phone/*` 接口已全覆盖，均经实测确认）
- ✅ **`BlueOath.Protocol`** —— 11 字节头 + protobuf + KCP，平台无关
- ✅ **`BlueOath.Tools`** —— 配置分析 / 目录生成；补一次 APK IL2CPP dump
- ✅ **配置工具链** —— `export-config.bat` / `import-config.bat` / `generate-config-cs.bat`
- ✅ **`docs/*-catalog`** —— 协议 / 配置 / Lua 三套目录继续有效
- ✅ **Lua 反编译链路** —— `extract-normalize.py` + `decompile-all.py`（Android 同为 Lua 5.3.5）
- ✅ **`Android_..._这台芯有点凉.zip`** —— 完整热更缓存，A5 直接可用
- 🔄 **`native/Payload` 设计思路** —— 若需异常兜底，重写为 Android `.so`，保留「SHA 门控 + 窄 patch」原则
- 🔄 **Mod（xLua）机制** —— 思路移植到 `libxlua.so`

---

## 附录 A：A2 完整复现步骤（已实测）

```bash
# ---- 0. 环境 ----
export PATH="/e/Develops/AndroidTools/SDK/platform-tools:/e/Develops/AndroidTools/SDK/build-tools/37.0.0:$PATH"
BT="/c/Program Files/Java/jdk-17/bin"
cd "E:/逆向工程/苍蓝誓约项目/android"

# ---- 1. 改明文配置：host -> 127.0.0.1:19090 ----
python -c "
import zipfile,json
z=zipfile.ZipFile('苍蓝誓约日服.apk')
gc=json.loads(z.read('assets/platform/game_config.json').decode('utf-8'))
gc['host']='http://127.0.0.1:19090'
gc['track_host']='http://127.0.0.1:19090'
gc['crashHost']='http://127.0.0.1:19090/'
gc['breakpad']='off'
open('_work/patch/game_config.json','w',encoding='utf-8').write(json.dumps(gc,ensure_ascii=False,indent=4))
"

# ---- 2. 外科手术式重打包（只替换该条目）----
python _work/repack.py

# ---- 3. 自签 ----
"$BT/keytool" -genkeypair -keystore _work/debug.keystore -storepass android -keypass android \
    -alias androiddebugkey -keyalg RSA -keysize 2048 -validity 10000 \
    -dname "CN=Android Debug,O=Android,C=US"
apksigner.bat sign --ks _work/debug.keystore --ks-pass pass:android --key-pass pass:android \
    --ks-key-alias androiddebugkey \
    --out _work/BlueOathJP-patched.apk _work/BlueOathJP-patched-unsigned.apk

# ---- 4. 安装 + 端口映射 ----
adb -s emulator-5554 uninstall com.zephyrus.clsy.gp
adb -s emulator-5554 install -r -d _work/BlueOathJP-patched.apk
adb -s emulator-5554 reverse tcp:19090 tcp:19090

# ---- 5. 启动服务端（★ 必须用绝对路径 + run_in_background；nohup & 会被回收）----
./src/BlueOath.Server/bin/Release/net8.0/BlueOath.Server.exe \
    --port=19090 --region=jp --client-version=1.4.90 \
    --data="E:/逆向工程/苍蓝誓约项目/runtime/jp" \
    --game-login-port=19191 --kcp-game-login-port=19192 \
    --bundle-root="E:/逆向工程/苍蓝誓约项目/runtime/android/bundles"

# ---- 6. 启动客户端并观察 ----
adb -s emulator-5554 logcat -c
adb -s emulator-5554 shell "am force-stop com.zephyrus.clsy.gp"
adb -s emulator-5554 shell monkey -p com.zephyrus.clsy.gp -c android.intent.category.LAUNCHER 1
adb -s emulator-5554 logcat -d | grep BTLOG       # 应看到 host:http://127.0.0.1:19090/...

# ---- 7. 本机自测（★ 必须 --noproxy，环境有 http_proxy=127.0.0.1:8758）----
curl -s --noproxy '*' "http://127.0.0.1:19090/phone/getversion/?os=android"
curl -s --noproxy '*' -o /tmp/b.bin -w "%{http_code} %{size_download}\n" \
  "http://127.0.0.1:19090/windows_android/characters/c_cl_ninghai_jp/animssplits/packages_0_2423299669"
```

> 备注：`--bundle-root` 必须指向已抽好的 `runtime/android/bundles`（zip 内 `files/bundles/` 的对应物）。

**结果**：客户端请求成功到达本机（见 §1.2 双向证据）。

> 备注：`adb reverse` 的回连源 IP 实测为 **`127.0.0.1`**，因此现有 loopback-only 服务器**无需改动**。

## 附录 B：本次侦察产物（`android/_work/` 与 `android/_probe/`）

| 路径 | 说明 |
| --- | --- |
| `_work/patch/game_config.json` | 改好 host 的配置样本 |
| `_work/repack.py` | 外科手术式重打包脚本（可复现） |
| `_work/debug.keystore` | 调试签名密钥（口令 `android`） |
| `_work/BlueOathJP-patched.apk` | 已签名可安装的修补版（1.10 GB） |
| `_work/probe_server.py` / `probe_hits.log` | 探针服务与命中记录（A2 证据） |
| `_probe/emu-game-01.png` | 原版游戏登录画面截图 |
| `_probe/emu-patched-01.png` | 修补版运行截图 |
| `_work/assetmap.bin` | 从 APK 抽出的 assetmap（UnityFS） |
| `_work/assetmap_table.tsv` | **解出的 5766 条权威 bundle 表**（path/crc/md5/size） |
| `_work/missing_bundles.txt` | 相对热更缓存的缺失清单 |
| `_work/overlay_missing.json` | **三处皆无的 196 项**（含 size） |
| `_work/bundle_coverage.json` | 覆盖统计（供脚本读取） |
| `runtime/android/bundles/` | 已抽取的 5310 个 bundle（2.94 GB，`--bundle-root` 指向此） |

> 以上除 `runtime/android/bundles/` 外均为**侦察临时产物**，非最终交付物，可随时清理或纳入 `.gitignore`。

## 附录 D：assetmap 解包与 bundle 覆盖检查脚本要点

```python
# 1) 解 assetmap（UnityFS / 2018.2.4f1 / flags 0x43）
#    blocksInfo 在 header(49) 之后；cib=165 解出 281B，数据块自 offset 214 起共 20 block
bi = lz4.block.decompress(data[49:49+165], uncompressed_size=281)
#    blockCount=20，每块 (uSize,cSize,flags) = u32,u32,u16
# 2) 逐块 lz4.block.decompress 拼接 → 文本表
# 3) 正则抽表：^\s*(path),(crc),(md5),(size)$
pat = re.compile(rb'([A-Za-z0-9_\-/\.]+),(\d{1,12}),([0-9a-f]{32}),(\d{1,15})')
# 4) 覆盖检查：assetmap - APK(assets/assetpack/bundles/) - cache(files/bundles/)
```

> ⚠️ 两个坑：① blocksInfo **不在文件末尾**（尽管 flag 含 `0x40`）；
> ② 表中的 `md5` **不是文件 MD5**，做校验会全部落空，只能用 `size` / `crc`。

## 附录 C：核心数字备忘

| 项 | 值 |
| --- | --- |
| APK versionName / versionCode | 1.4.90 / 407 |
| 包名 / 入口 | `com.zephyrus.clsy.gp` / `com.Babel.GD.MainActivity` |
| ABI | 仅 armeabi-v7a（模拟器靠 Native Bridge 转译） |
| IL2CPP metadata version | 24 |
| APK config db 数 / 与 PC 一致 | 497 / **460 完全一致** |
| APK bundle 数（含 `windows/`） | 1339 |
| bundle 加密 | 无（`UnityFS` 明文） |
| **域名配置位置** | **`assets/platform/game_config.json`（明文）** |
| **adb reverse 回连源 IP** | **`127.0.0.1`** |
| **客户端 Unity 版本** | **2018.2.4f1** |
| **PC 端 Unity 版本** | **2018.2.14f1**（→ bundle 不通用） |
| **assetmap 声明 bundle / APK / 缓存 / 并集** | **5766 / 1339 / 5310 / 5570** |
| **资源真缺口** | **196（34 个角色）** |
| 热更总下载量（客户端显示） | 3020.78 MB |
| PC 侧 JP 版本 / CN 版本 | 1.4.0 / 1.5.20（CN 不在本期范围） |
