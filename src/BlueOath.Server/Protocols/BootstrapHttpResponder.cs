using System.Text;
using System.Text.Json;
using BlueOath.Server.Hosting;

namespace BlueOath.Server.Protocols;

/// <summary>
/// 引导 HTTP 响应：状态码、原因短语、Content-Type 与响应体。
/// 响应体统一用 <see cref="byte"/> 承载 —— 除 JSON/HTML 文本外，热更 bundle 下载
/// 还要求按原始二进制回包（UnityFS 分片无法安全地以 UTF-8 字符串往返）。
/// 文本场景请用 <see cref="Text"/> 构造，它会按 UTF-8 编码。
/// </summary>
internal sealed record BootstrapHttpResponse(
    int StatusCode, string ReasonPhrase, string ContentType, byte[] Body)
{
    /// <summary>以 UTF-8 文本构造响应体（JSON / HTML / 纯文本等）。</summary>
    public static BootstrapHttpResponse Text(int statusCode, string reasonPhrase, string contentType, string body) =>
        new(statusCode, reasonPhrase, contentType, Encoding.UTF8.GetBytes(body));
}

/// <summary>
/// 应答真实客户端在登录过程中发出的 SDK 引导 HTTP 请求（公网 IP 探测、热更版本检查、
/// 服务器列表、登录角色等）。所有响应体都经过逆向确认，字段类型（字符串/数字）需与客户端
/// 解析方式精确匹配。
/// </summary>
internal sealed class BootstrapHttpResponder(ServerEndpoints endpoints, AnnouncementConfig announcementConfig,
    ServerOptions options)
{
    private readonly ServerEndpoints _endpoints = endpoints;
    private readonly string _profileIdJson = JsonSerializer.Serialize(options.ProfileId);

    /// <summary>
    /// 热更 bundle 的本地根目录（对应在线 CDN 的 <c>static_url + path</c> 基址）。
    /// 客户端点击「ダウンロード」后会以
    /// <c>GET /windows_android/&lt;bundle 相对路径&gt;_&lt;crc&gt;</c> 的形式逐个拉取，
    /// 这里把该前缀映射到本地文件系统。为空表示不提供下载（回到 501 行为）。
    /// </summary>
    private readonly string? _bundleRoot = string.IsNullOrWhiteSpace(options.BundleRoot)
        ? null : Path.GetFullPath(options.BundleRoot);

    public BootstrapHttpResponse BuildResponse(string requestLine, string? host = null)
    {
        // 公网 IP 探测：返回一个假 IP，让 SDK 认为网络可用。
        if (host is not null && (host.Contains("ifconfig.io", StringComparison.OrdinalIgnoreCase) ||
            host.Contains("ipify.org", StringComparison.OrdinalIgnoreCase) ||
            host.Contains("ipinfo.io", StringComparison.OrdinalIgnoreCase) ||
            host.Contains("3322.net", StringComparison.OrdinalIgnoreCase)))
            return BootstrapHttpResponse.Text(200, "OK", "text/plain; charset=utf-8", "203.0.113.1");

        if (requestLine.Contains("/phone/switch/getstate", StringComparison.OrdinalIgnoreCase))
            // switch（事件 27）在其 JSON 解析失败时会派发 errornu:"-1"。线上样本（catalog id=27）
            // 显示 errornu 是字符串，而 DNS_sw.state 被 asInt() 成数字。据此对齐：errornu 用字符串
            // "0"，state 用数字 1。
            return BootstrapHttpResponse.Text(200, "OK", "application/json; charset=utf-8",
                "{\"errornu\":\"0\",\"errordesc\":\"\",\"DNS_sw\":{\"state\":1}}");

        if (requestLine.Contains("/sdk/gettime", StringComparison.OrdinalIgnoreCase))
            return BootstrapHttpResponse.Text(200, "OK", "application/json; charset=utf-8",
                "{\"time\":" + DateTimeOffset.UtcNow.ToUnixTimeSeconds() + "}");

        // 用户协议版本（Java com.gamesdk.pages.ProtocolPage$3.onResponse）。
        // 逆向契约（classes2.dex）：
        //   json.getInt("errornu") == 0          ← 必须是**数字**
        //   json.getJSONObject("data") 下需要 status(int) / version / agreeUrl / privacyUrl / entranceUrl
        //   status == 0 → 客户端直接 forward(12) 跳过协议页；
        //   status != 0 且 (version != 本地已存 || onlyShow) → show() 并 loadUrl(entranceUrl)。
        // 原先没有该路由 → 501 空响应 → new JSONObject(null) →
        //   java.lang.NullPointerException @ org.json.JSONTokener.nextCleanInternal
        //   @ com.gamesdk.pages.ProtocolPage$3.onResponse（每轮登录必现）。
        if (requestLine.Contains("/sdk/help/getagreeversion", StringComparison.OrdinalIgnoreCase))
            return BootstrapHttpResponse.Text(200, "OK", "application/json; charset=utf-8",
                "{\"errornu\":0,\"errordesc\":\"\",\"data\":{\"status\":0,\"version\":\"1.4.90\"," +
                "\"agreeUrl\":\"http://127.0.0.1:19090/agree\"," +
                "\"privacyUrl\":\"http://127.0.0.1:19090/privacy\"," +
                "\"entranceUrl\":\"http://127.0.0.1:19090/agree\"}}");

        // /sdk/getuserextra 与 /phone/getuserextra/ **不同**：前者是 Java 侧 Google 订阅检查，
        // 后者是 C# 侧「用户附加功能状态」；响应结构分别见各自分支。

        if (requestLine.Contains("/sdk/sdklogin", StringComparison.OrdinalIgnoreCase))
            // 安卓端的登录**不走** native new_sdk 的 /login?，而是走 Java 层 com.gamesdk：
            //   com.gamesdk.lib.GamePlatform 把 Urls.BASE_LOGIN_URL 覆盖为
            //       urls.json 的 ONLINE_LOGIN_URL(或 DEBUG_LOGIN_URL) + "/sdk/"
            //   com.gamesdk.pages.LoginMainPage.guestLogin()
            //       httpGet(BASE_LOGIN_URL + "sdklogin", params{action=devicelogin, version=2})
            // 响应由 com.gamesdk.bean.Ret.getRet() 解析：
            //   errornu  = json.getInt("errornu")   ← **必须是数字**（与 /phone/* 的字符串 "0" 不同）
            //   cookie/token/username/applyDel = optString
            //   isBindSsos = optInt ; bindSsos = optJSONArray
            // errornu != 0 → onLoginFail()；登录未成功就不会进入登录页，
            // 也就永远不会触发 loginpage._SDKGetServerList（即 POST /phone/serverlist/），
            // 表现就是「选服列表为空」。
            return BootstrapHttpResponse.Text(200, "OK", "application/json; charset=utf-8",
                "{\"errornu\":0,\"errordesc\":\"\"," +
                "\"username\":\"BlueoathRebirth\",\"token\":\"local-token\",\"cookie\":\"local-cookie\"," +
                "\"isBindSsos\":0,\"bindSsos\":[],\"applyDel\":\"0\"}");

        if (requestLine.Contains("/phone/applereview", StringComparison.OrdinalIgnoreCase))
        {
            // 线上响应（catalog.json id=19）用字符串 "0" 作为 errornu、数字作为 applereview。
            //
            // ⚠️ 关键发现（2026-10-05）：applereview=1 虽然能跳过 getversion 热更下载，
            //    但它把 HotPatchFacade 路由到 OnlyInitHotPatchManager —— 该管理器**不会**执行
            //    HotPatchIniter.OnCalculating（即外部文件扫描），于是 ExternalInfoGetter.state
            //    永远停在 0（ONLY_INTERNAL）。后果：
            //      · BundleImplementer.GetPath() 对所有 bundle 返回 IsInPackage=true
            //        -> 全部走 APK 内路径 base.apk/assets/assetpack/bundles/...
            //      · uitextures/* 等仅存在于 sdcard 的 bundle 加载失败 -> 黑屏。
            //      · 客户端还会在 CheckExistExternalFileList 中因内容不符而删除我们的
            //        external_files_list.txt（写完又因异常未落盘），形成死循环。
            //
            //    因此必须使用 **正常热更流程**（applereview=0）：HotPatchManager -> StateChecker
            //    -> getversion -> OnCallBack -> EXTERNAL_PATCHING(state=2) -> 正确生成
            //    external_files_list.txt -> sdcard bundle 走外部路径。
            //    配合服务端 getversion 返回 tar_version == 本地 assetmap 版本(1.4.90)，即可
            //    得到 NO_NEED_DOWNLOAD，跳过实际下载。
            //
            //    如需回退到 applereview 直通模式，把下面的 0 改回 1 即可。
            var appleReview = options.Profile.AppleReviewBypass ? 1 : 0;
            return BootstrapHttpResponse.Text(200, "OK", "application/json; charset=utf-8",
                "{\"errornu\":\"0\",\"applereview\":" + appleReview + "}");
        }

        // /sdk/getversion/getversion —— Java `com.gamesdk.lib.VersionManger.updateVersion()`
        // 「SDK 自带资源更新检查」。逆向契约（classes2.dex VersionManger.java）：
        //   JSONObject data = jsonObject.getJSONObject("data");   ← 缺 data 会抛异常
        //   int status = data.optInt("status");
        //   status==0||2 → call(0)（成功，无提示）；1 → 下载；>2 → call(-1)。
        //   —— 原先无该路由 / 返回 phone 形状（无 data）→ 异常 → call(-1) → 客户端弹
        //      「アップデートが利用できます。アップデートを行ってください」。
        //   这里 status=0 直接放行。
        if (requestLine.Contains("/sdk/getversion", StringComparison.OrdinalIgnoreCase))
            return BootstrapHttpResponse.Text(200, "OK", "application/json; charset=utf-8",
                "{\"errornu\":0,\"errordesc\":\"\",\"data\":{\"status\":0,\"version\":\"1.4.90\"}}");

        if (requestLine.Contains("/phone/getversion/", StringComparison.OrdinalIgnoreCase))
        {
            // "script" 分支会反序列化成 SDK.ScriptInfo（而非 PackageUpdateInfo）：
            //   errornu、script=VersionInfo[]、static_url、spare_static_url。
            // VersionInfo：pl/os/groupbase/gn/path/src_version/tar_version/updateType/file/
            //   total_size/sizes/forceExit/forceUpdate。OnCallBack 用 tar_version 作为服务器版本；
            //   == 本地 assetmap 版本 "1.4.0" => NO_NEED_DOWNLOAD => FinishCheck。
            //
            // ★ static_url 不可为空，且 tar_version 必须等于客户端 assetmap 的版本。
            //   两点都必须满足，否则 StateChecker（IL2CPP 0x3DF460 的
            //   OnNeedHotPatchGetServerPathBack -> OnCallBack）会走到
            //   "依赖工具包错误: serverPath为空, 加载不了资源!" 分支并弹窗中止。
            //
            //   ① static_url：线上应答（CDN 根）与 script[].path 拼成 CDN 基址；空串即直接失败。
            //      离线场景不需要真实 CDN，给回环根路径即可（配合 adb reverse 由同一引导口响应）。
            //   ② tar_version：必须 == APK 内 `assets/assetpack/bundles/assetmap` 的版本。
            //      Android JP APK（1.4.90 / versionCode 407）的 assetmap 内嵌版本为 **1.4.90**
            //      （UnityFS 内偏移 1231 处为 "1.4.90" 字面量）；而 PC 侧 Profile 是 1.4.0。
            //      故这里不能硬编码 1.4.0，必须用 `options.Profile.ClientVersion` 才能与
            //      实际运行的客户端对齐 —— 版本不一致会走 PATCH_TO_LATEST 分支而非
            //      NO_NEED_DOWNLOAD，从而需要真正的下载 URL 而失败。
            //
            //   ★ 协议必须与「下载器」一致：热更下载走客户端原生 libcurl+OpenSSL
            //     （BTHttpClient.cpp，自带 CA 校验，不走客户端 SSL 绕过），
            //     所以主端口若是自签 TLS，下载会在握手阶段失败（服务端日志里
            //     **完全看不到** /windows_android/ 请求，客户端 NetworkFailTimes 递增）。
            //     因此优先使用显式配置的明文下载基址（--bundle-static-url），
            //     例如独立安卓服务端会另开一个明文端口专门做下载。
            string staticUrl;
            if (!string.IsNullOrWhiteSpace(options.BundleStaticBaseUrl))
            {
                staticUrl = options.BundleStaticBaseUrl;
                if (!staticUrl.EndsWith('/')) staticUrl += "/";
            }
            else
            {
                var scheme = options.EnableTls ? "https" : "http";
                staticUrl = $"{scheme}://127.0.0.1:{_endpoints.Port}/";
            }
            var tarVersion = options.Profile.ClientVersion;
            // path：线上是 CDN 子目录（客户端内同样存在 "windows_android/" 字面量，
            //   形如 static_url + path 拼出热更根）。空串会让 serverPath 判定为「为空」。
            var scriptPath = "windows_android/";
            return BootstrapHttpResponse.Text(200, "OK", "application/json; charset=utf-8",
                "{\"errornu\":\"0\",\"script\":[{\"pl\":\"google_windows\",\"os\":\"android\"," +
                "\"groupbase\":\"\",\"gn\":\"jpshipgirl\"," +
                "\"path\":" + JsonSerializer.Serialize(scriptPath) + "," +
                "\"src_version\":" + JsonSerializer.Serialize(tarVersion) +
                ",\"tar_version\":" + JsonSerializer.Serialize(tarVersion) + ",\"updateType\":\"0\"," +
                "\"file\":\"\",\"total_size\":0,\"sizes\":[],\"forceExit\":0,\"forceUpdate\":0}]," +
                "\"static_url\":" + JsonSerializer.Serialize(staticUrl) +
                ",\"spare_static_url\":" + JsonSerializer.Serialize(staticUrl) + "}");
        }

        if (requestLine.Contains("/phone/getPlData/getPlData", StringComparison.OrdinalIgnoreCase))
        {
            // noticeBoard 不能是 null：cjson.decode 会把 JSON null 解析成 cjson.null
            // （一个 userdata，truthy），platformManager.GetAnnounceState 里
            // self.noticeBoard.beforgame 会报 "attempt to index a userdata value"。
            // 用空对象 {} 让 self.noticeBoard 成为空 Lua 表，GetAnnounceState 返回 false。
            var noticeBoardJson = "{}";
            return BootstrapHttpResponse.Text(200, "OK", "application/json; charset=utf-8",
                // 线上事件 1007 的固定信封是 errornu/errordesc/data。抓包已确认 errornu
                // 为字符串；把平台字段直接摊在根对象会让 SDK 走 111111 未知错误分支。
                "{\"errornu\":\"0\",\"errordesc\":\"\",\"data\":{\"networkCheck\":\"1\"," +
                "\"uuid\":\"00000000-0000-4000-8000-000000000001\",\"pid\":" + _profileIdJson + "," +
                "\"serverId\":\"jp\",\"pl\":\"google_windows\",\"os\":\"android\",\"gn\":\"jpshipgirl\"," +
                "\"sensorInfo\":\"\",\"localInfo\":\"\",\"timeZoneId\":\"\"," +
                "\"screenWidth\":\"1920\",\"screenHeight\":\"1080\",\"dangerWidth\":\"0\",\"strDeviceInfo\":\"\"," +
                "\"noticeBoard\":" + noticeBoardJson + "}}");
        }

        if (requestLine.Contains("/login?", StringComparison.OrdinalIgnoreCase))
            return BootstrapHttpResponse.Text(200, "OK", "application/json; charset=utf-8",
                "{\"errornu\":0,\"errordesc\":\"\",\"Pid\":" + _profileIdJson + ",\"UID\":" + _profileIdJson + "," +
                "\"uid\":" + _profileIdJson + ",\"uuid\":\"00000000-0000-4000-8000-000000000001\"," +
                "\"token\":\"local-token\",\"openid\":" + _profileIdJson + ",\"ServerID\":\"jp\"," +
                "\"serverid\":\"jp\",\"newuser\":\"0\",\"qid\":\"1\",\"id\":\"1\"}");

        if (requestLine.Contains("/phone/login ", StringComparison.OrdinalIgnoreCase))
        {
            // ★ 安卓端 native SDK 在游客登录成功后请求 `POST /phone/login`（无 query，
            //   原先只判 Contains("/login?") 匹配不上 → 501 → 客户端弹「请求失败(2-501)」）。
            //
            // ★★ 2026-10-09 实测二分结论（每次重启游戏验证）：
            //   响应字段                     结果
            //   {"errornu":"0","errordesc":""}                     不崩，走到 /phone/serverlist/
            //   + "uid":"local-player"（**字符串**）               不崩，且走到 /phone/loginrole/
            //   + "newuser":"0"                                    崩溃（native SIGSEGV）
            //   + "uid":1（**数字**）                              崩溃
            //   PC 同款全字段（uid/pid/regPlosgn/token/protocolStatus/newuser）  崩溃
            // → 因此只回 **errornu + errordesc + uid(字符串)**，**不要 newuser**，uid 不能用数字。
            //   native 侧逻辑（libnew_sdk.so RVA 0x84328 起）：
            //     this+0x68(m_pid) = json["uid"]        ← 必需，后续 getLoginedServerInfo 用 pid= 参数
            //     json["newuser"] 非空 → 走 sb+0x78 分支 → 空指针崩溃
            //   errornu 必须是**字符串** "0"（native 用字符串比较）。
            // ⚠️ 匹配串带尾空格 "/phone/login "，避免吞掉 /phone/loginrole/。
            //
            // 【调试】若 exe 同目录存在 `phone_login.json`，则直接用它作为响应体，
            // 便于在不重新编译的情况下二分定位客户端崩溃所需的字段组合。
            var overrideFile = Path.Combine(AppContext.BaseDirectory, "phone_login.json");
            if (File.Exists(overrideFile))
                return BootstrapHttpResponse.Text(200, "OK", "application/json; charset=utf-8",
                    File.ReadAllText(overrideFile, Encoding.UTF8).Trim());
            return BootstrapHttpResponse.Text(200, "OK", "application/json; charset=utf-8",
                "{\"errornu\":\"0\",\"errordesc\":\"\",\"uid\":\"local-player\"}");
        }

        if (requestLine.Contains("/gethash", StringComparison.OrdinalIgnoreCase))
        {
            var gamePort = _endpoints.ResolvedGameLoginPort;
            return BootstrapHttpResponse.Text(200, "OK", "application/json; charset=utf-8",
                "{\"errornu\":\"0\",\"errordesc\":\"\",\"pid\":" + _profileIdJson + ",\"serverID\":\"game1\"," +
                "\"feignRoleId\":\"1\",\"qid\":\"1\",\"uuid\":\"00000000-0000-4000-8000-000000000001\"," +
                "\"offset\":\"0\",\"host\":\"127.0.0.1\",\"port\":" + gamePort + "}");
        }

        if (requestLine.Contains("/phone/serverlist/", StringComparison.OrdinalIgnoreCase))
        {
            var gamePort = _endpoints.ResolvedGameLoginPort;
            // SDK（new_sdk.dll 的 getServerList）不解析响应体，只把原始 JSON 存起来，
            // 由 Lua 侧（platformmanager.getServiceListAndAllServiceNotic）读取
            // result.root.notice + result.root.item[]。Lua 会把 result.errornu 与字符串 "0"
            // 比较，所以这里的 errornu 必须是带引号的 "0"（不同于 SDK 自己的 getPlData，
            // 后者是 asInt() 成数字）。
            //
            // 【调试】若 exe 同目录存在 `serverlist.json`，则直接用它作为响应体。
            var slOverride = Path.Combine(AppContext.BaseDirectory, "serverlist.json");
            if (File.Exists(slOverride))
                return BootstrapHttpResponse.Text(200, "OK", "application/json; charset=utf-8",
                    File.ReadAllText(slOverride, Encoding.UTF8).Trim());
            return BootstrapHttpResponse.Text(200, "OK", "application/json; charset=utf-8",
                "{\"errornu\":\"0\",\"errordesc\":\"\",\"root\":{\"notice\":{\"open\":0,\"desc\":\"\"},\"item\":[" +
                "{\"name\":\"BlueoathRebirth\",\"serverIndex\":1,\"new\":0,\"groupid\":\"1\",\"openDateTime\":\"20171109140000\"," +
                "\"status\":1,\"hot\":0,\"host\":\"127.0.0.1\",\"port\":" + gamePort + ",\"recommend_weight\":1}" +
                "]}}");
        }

        if (requestLine.Contains("/phone/loginrole/", StringComparison.OrdinalIgnoreCase))
        {
            var gamePort = _endpoints.ResolvedGameLoginPort;
            return BootstrapHttpResponse.Text(200, "OK", "application/json; charset=utf-8",
                "{\"errornu\":\"0\",\"errordesc\":\"\",\"root\":{\"role\":[" +
                "{\"name\":\"BlueoathRebirth\",\"serverIndex\":1,\"groupid\":\"1\",\"serverId\":\"1\"," +
                "\"host\":\"127.0.0.1\",\"port\":" + gamePort + ",\"status\":1,\"openDateTime\":\"20171109140000\"}" +
                "]}}");
        }

        if (requestLine.Contains("/phone/platform/getPlatformUserInfo", StringComparison.OrdinalIgnoreCase))
            // 实名/快速登录检查（事件 1002）。LoginPage._CheckRealName 把 isFastUser == 1 或
            // idcardStatus == 1 视为「无需实名门槛」，继续走 OnSDKEnterGame ->
            // LoginLogic.CheckUpdate -> getHash -> KCP 登录。
            return BootstrapHttpResponse.Text(200, "OK", "application/json; charset=utf-8",
                "{\"errornu\":\"0\",\"errordesc\":\"\",\"data\":{" +
                "\"isFastUser\":1,\"idcardStatus\":1,\"isAdult\":1,\"OnNoRealnameLogin\":0}}");

        if (requestLine.Contains("/phone/platform/getGameMaintainNotice", StringComparison.OrdinalIgnoreCase))
        {
            var dataJson = JsonSerializer.Serialize(announcementConfig.MaintainNotices);
            return BootstrapHttpResponse.Text(200, "OK", "application/json; charset=utf-8",
                "{\"errornu\":\"0\",\"errordesc\":\"\",\"data\":" + dataJson + "}");
        }

        if (requestLine.Contains("/phone/innerbrowse", StringComparison.OrdinalIgnoreCase))
        {
            var noticearJson = JsonSerializer.Serialize(announcementConfig.InnerBrowse);
            return BootstrapHttpResponse.Text(200, "OK", "application/json; charset=utf-8",
                "{\"errornu\":\"0\",\"errordesc\":\"\",\"noticear\":" + noticearJson + "}");
        }

        if (requestLine.Contains("/phone/supernotice", StringComparison.OrdinalIgnoreCase))
            // SDK 的内置 WebView（InnerBrowser）会以 GET 打开该「超级公告」页。
            // 客户端 Lua 侧并不解析它的返回值（全量 lua 中无 supernotice 字样），
            // 但 WebView 收到 501 会在 logcat 打出 onReceivedHttpError，且公告悬浮层
            // 会残留。这里返回一个最小的空 HTML 页，让 WebView 正常 onPageFinished 收尾。
            return BootstrapHttpResponse.Text(200, "OK", "text/html; charset=utf-8",
                "<!DOCTYPE html><html><head><meta charset=\"utf-8\">" +
                "<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">" +
                "<title></title></head><body></body></html>");

        if (requestLine.Contains("/bugly/status", StringComparison.OrdinalIgnoreCase))
            // Bugly 崩溃上报 SDK 的状态查询；应答体不参与游戏逻辑，给一个空 JSON 即可。
            return BootstrapHttpResponse.Text(200, "OK", "application/json; charset=utf-8", "{}");

        // /sdk/getuserextra —— Java `com.gamesdk.google.Subscription.get()`「Google 订阅续费提醒」。
        // 逆向契约（classes2.dex Subscription.java）：
        //   int errornu = jsonObject.getInt("errornu");            ← 必须是**数字**
        //   JSONObject data   = jsonObject.optJSONObject("data");
        //   JSONObject google = data.optJSONObject("google_sub");   ← data 为 null 会 NPE
        //   int notify = google.optInt("notify");                   ← google_sub 为 null 会 NPE
        //   （原先 501 空响应 → new JSONObject(null) 抛异常；返回普通 data 则
        //     `data.optJSONObject("google_sub")`=null → `google.optInt` NPE → FATAL →
        //     BaseSdk.restartApplication 重启进程）
        // notify != 1 即直接 return（不弹通知），故给 0。
        if (requestLine.Contains("/sdk/getuserextra", StringComparison.OrdinalIgnoreCase))
            return BootstrapHttpResponse.Text(200, "OK", "application/json; charset=utf-8",
                "{\"errornu\":0,\"errordesc\":\"\",\"data\":{\"google_sub\":{\"notify\":0," +
                "\"sub_product_id\":\"\",\"msg\":\"\",\"title\":\"\"}}}");

        if (requestLine.Contains("/phone/getuserextra/", StringComparison.OrdinalIgnoreCase))
            // 用户附加功能状态（事件 1008，LoginOk 之后）。PlatformManager.CheckUserExtraFunctionState
            // 读取 result.data.userInfo.readQuestion、result.data.payBack.returnGold/returnMonthCard
            // 和 result.data.oldUser.returnUserReceiveGift。
            return BootstrapHttpResponse.Text(200, "OK", "application/json; charset=utf-8",
                "{\"errornu\":\"0\",\"errordesc\":\"\",\"data\":{\"userInfo\":{\"readQuestion\":0}," +
                "\"payBack\":{\"returnGold\":0,\"returnMonthCard\":0}," +
                "\"oldUser\":{\"returnUserReceiveGift\":0}}}");

        if (requestLine.Contains("/c.gif", StringComparison.OrdinalIgnoreCase))
            return BootstrapHttpResponse.Text(200, "OK", "text/plain; charset=utf-8", "ok");

        // 热更 bundle 下载：客户端把 `static_url + script[].path` 作为 CDN 基址，再逐包拼
        // `<相对路径>_<crc>`（见 BundleDownloadInfo.GetPostFix，PC 0x207C60 —— 实现即
        // `"_" + crc`）。落到我们这里就是 `GET /windows_android/<rel>_<crc>`。
        // 真实文件名不含 crc 后缀（zip 内 files/bundles 即为准），所以先剥后缀再查表。
        if (_bundleRoot is not null)
        {
            var bundle = TryResolveBundleFile(requestLine);
            if (bundle is not null)
                return bundle;
        }

        // CDN 主机（static1/static3.zuiyouxi.com）在 SDK 初始化（事件 31）期间提供下载测速，
        // 之后也提供热更版本清单。凡是我们尚未明确理解的路径也统一回 200，让测速成功，
        // 上面的请求行 + 主机信息会被记入日志供进一步分析。
        if (host is not null && IsCdnHost(host))
            return BootstrapHttpResponse.Text(200, "OK", "text/plain; charset=utf-8", "ok");

        return BootstrapHttpResponse.Text(501, "Not Implemented", "text/plain; charset=utf-8", "");
    }

    /// <summary>
    /// 把热更下载请求行映射为本地 bundle 文件。返回 null 表示「不是 bundle 下载」
    /// （交给后续的 CDN/501 处理）。
    /// </summary>
    private BootstrapHttpResponse? TryResolveBundleFile(string requestLine)
    {
        // 请求行形如 `GET /windows_android/<rel> HTTP/1.1`。
        var space = requestLine.IndexOf(' ');
        if (space < 0) return null;
        var rest = requestLine[(space + 1)..];
        var end = rest.IndexOf(' ');
        var rawPath = end >= 0 ? rest[..end] : rest;
        // 去掉查询串并做 URL 解码（路径里可能出现 %20 等）。
        var query = rawPath.IndexOf('?');
        if (query >= 0) rawPath = rawPath[..query];
        var urlPath = Uri.UnescapeDataString(rawPath);

        const string prefix = "/windows_android/";
        if (!urlPath.StartsWith(prefix, StringComparison.Ordinal)) return null;

        var relative = urlPath[prefix.Length..];
        if (relative.Length == 0) return null;

        // 剥离 `_<crc>` 后缀：最后一个下划线之后的全部为纯数字时即视为 crc 后缀。
        var slash = relative.LastIndexOf('/');
        var lastSegment = slash >= 0 ? relative[(slash + 1)..] : relative;
        var lastUnderscore = lastSegment.LastIndexOf('_');
        if (lastUnderscore > 0)
        {
            var suffix = lastSegment[(lastUnderscore + 1)..];
            if (suffix.Length > 0 && suffix.All(char.IsAsciiDigit))
            {
                var strippedSegment = lastSegment[..lastUnderscore];
                relative = slash >= 0 ? relative[..(slash + 1)] + strippedSegment : strippedSegment;
            }
        }

        // 归一化并阻止目录穿越（`..` 逃逸出根目录）。
        var combined = Path.GetFullPath(Path.Combine(_bundleRoot!, relative.Replace('/', Path.DirectorySeparatorChar)));
        var rootWithSep = _bundleRoot!.EndsWith(Path.DirectorySeparatorChar)
            ? _bundleRoot : _bundleRoot + Path.DirectorySeparatorChar;
        if (!combined.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase)) return null;

        if (!File.Exists(combined))
            // 客户端对 404 会重试（NetworkFailTimes 递增）；给 404 便于在日志里区分
            // 「包缺失」与「路由没匹配上」。
            return BootstrapHttpResponse.Text(404, "Not Found", "text/plain; charset=utf-8",
                "bundle not found: " + relative);

        var bytes = File.ReadAllBytes(combined);
        return new BootstrapHttpResponse(200, "OK", "application/octet-stream", bytes);
    }

    private static bool IsCdnHost(string host) =>
        host.StartsWith("static", StringComparison.OrdinalIgnoreCase) &&
        host.EndsWith(".zuiyouxi.com", StringComparison.OrdinalIgnoreCase);
}
