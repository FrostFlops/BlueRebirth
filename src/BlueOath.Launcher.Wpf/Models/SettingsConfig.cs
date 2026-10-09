using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace BlueOath.Launcher.Wpf.Models;

public class SettingsConfig : INotifyPropertyChanged
{
    private string _gameClientPath = "";
    private string _serverDllPath = "";
    private string _pythonPath = "python";
    private string _injectorPath = "";
    private string _payloadPath = "";
    private string _proxyScriptPath = "";
    private string _dataRoot = "";
    private string _baselinePath = "";
    private string _region = "jp";
    private string _updateManifestUrl = "";
    private bool _autoUpdateEnabled = true;
    private int _serverPort = 0;
    private int _gameLoginPort = 7201;
    private int _gmPort = 9780;
    private bool _skipBuild = true;
    private bool _keepLog = false;
    private bool _cheatProduction = false;
    private bool _cheatStrength = false;
    private bool _cheatVow = false;
    private bool _cheatMood = false;
    private bool _cheatMedals = false;
    private bool _cheatDrops = false;
    private bool _cheatSweep = false;
    private bool _realResourceCost = false;
    private bool _realShopStock = false;

    [JsonPropertyName("gameClientPath")]
    public string GameClientPath
    {
        get => _gameClientPath;
        set { _gameClientPath = value; OnPropertyChanged(); }
    }

    [JsonPropertyName("serverDllPath")]
    public string ServerDllPath
    {
        get => _serverDllPath;
        set { _serverDllPath = value; OnPropertyChanged(); }
    }

    [JsonPropertyName("pythonPath")]
    public string PythonPath
    {
        get => _pythonPath;
        set { _pythonPath = value; OnPropertyChanged(); }
    }

    [JsonPropertyName("injectorPath")]
    public string InjectorPath
    {
        get => _injectorPath;
        set { _injectorPath = value; OnPropertyChanged(); }
    }

    [JsonPropertyName("payloadPath")]
    public string PayloadPath
    {
        get => _payloadPath;
        set { _payloadPath = value; OnPropertyChanged(); }
    }

    [JsonPropertyName("proxyScriptPath")]
    public string ProxyScriptPath
    {
        get => _proxyScriptPath;
        set { _proxyScriptPath = value; OnPropertyChanged(); }
    }

    [JsonPropertyName("dataRoot")]
    public string DataRoot
    {
        get => _dataRoot;
        set { _dataRoot = value; OnPropertyChanged(); }
    }

    [JsonPropertyName("baselinePath")]
    public string BaselinePath
    {
        get => _baselinePath;
        set { _baselinePath = value; OnPropertyChanged(); }
    }

    [JsonPropertyName("updateManifestUrl")]
    public string UpdateManifestUrl
    {
        get => _updateManifestUrl;
        set { _updateManifestUrl = value; OnPropertyChanged(); }
    }

    [JsonPropertyName("autoUpdateEnabled")]
    public bool AutoUpdateEnabled
    {
        get => _autoUpdateEnabled;
        set { _autoUpdateEnabled = value; OnPropertyChanged(); }
    }

    [JsonPropertyName("region")]
    public string Region
    {
        get => _region;
        set { _region = value; OnPropertyChanged(); }
    }

    [JsonPropertyName("serverPort")]
    public int ServerPort
    {
        get => _serverPort;
        set { _serverPort = value; OnPropertyChanged(); }
    }

    [JsonPropertyName("gameLoginPort")]
    public int GameLoginPort
    {
        get => _gameLoginPort;
        set { _gameLoginPort = value; OnPropertyChanged(); }
    }

    [JsonPropertyName("gmPort")]
    public int GmPort
    {
        get => _gmPort;
        set { _gmPort = value; OnPropertyChanged(); }
    }

    [JsonPropertyName("skipBuild")]
    public bool SkipBuild
    {
        get => _skipBuild;
        set { _skipBuild = value; OnPropertyChanged(); }
    }

    [JsonPropertyName("keepLog")]
    public bool KeepLog
    {
        get => _keepLog;
        set { _keepLog = value; OnPropertyChanged(); }
    }

    // 作弊选项：启动服务端时转成 --cheat-* 开关。设置页勾选即保存，不受「修改设置」锁定。
    // 旧版设置文件没有这些键时按 false 读入；旧版启动器会忽略这些键。

    /// <summary>生产：道具下单即完成，资源楼始终满仓（--cheat-production）。</summary>
    [JsonPropertyName("cheatProduction")]
    public bool CheatProduction
    {
        get => _cheatProduction;
        set { _cheatProduction = value; OnPropertyChanged(); }
    }

    /// <summary>体力（货币 21，日服メカニカルメダル / 国服工匠体力）：加速、合成、建造不消耗（--cheat-strength）。</summary>
    [JsonPropertyName("cheatStrength")]
    public bool CheatStrength
    {
        get => _cheatStrength;
        set { _cheatStrength = value; OnPropertyChanged(); }
    }

    /// <summary>许愿墙：祈愿后无冷却（--cheat-vow）。</summary>
    [JsonPropertyName("cheatVow")]
    public bool CheatVow
    {
        get => _cheatVow;
        set { _cheatVow = value; OnPropertyChanged(); }
    }

    /// <summary>心情：基建工作与加速不消耗心情，宿舍、浴场照常回复（--cheat-mood）。</summary>
    [JsonPropertyName("cheatMood")]
    public bool CheatMood
    {
        get => _cheatMood;
        set { _cheatMood = value; OnPropertyChanged(); }
    }

    /// <summary>探索勋章：探索每抽固定附赠 100 个精鋭戦姫勲章；关闭时按原规则 SSR 25、SR 5（--cheat-medals）。</summary>
    [JsonPropertyName("cheatMedals")]
    public bool CheatMedals
    {
        get => _cheatMedals;
        set { _cheatMedals = value; OnPropertyChanged(); }
    }

    /// <summary>掉落加成：掉落池里的资源与道具每项额外 +600～+2000（--cheat-drops）。</summary>
    [JsonPropertyName("cheatDrops")]
    public bool CheatDrops
    {
        get => _cheatDrops;
        set { _cheatDrops = value; OnPropertyChanged(); }
    }

    /// <summary>扫荡跳过时间：扫荡作战开始即完成；前哨不按时间产出，加速不限次数、立即产出；不勾选时按真实时间（--cheat-sweep）。</summary>
    [JsonPropertyName("cheatSweep")]
    public bool CheatSweep
    {
        get => _cheatSweep;
        set { _cheatSweep = value; OnPropertyChanged(); }
    }

    // 原规则选项（资源与商店）：离线版默认资源免费、商店不限量，勾选后服务端改按原游戏规则（--real-resource-cost / --real-shop-stock）。
    // 与作弊选项一样勾选即保存、不受锁定；旧版设置文件没有这些键时按 false（沿用免费规则）读入。

    /// <summary>真实消耗资源：探索扣推荐信、商店购买扣价格、出击扣燃料、共闘扣 RP（--real-resource-cost）。</summary>
    [JsonPropertyName("realResourceCost")]
    public bool RealResourceCost
    {
        get => _realResourceCost;
        set { _realResourceCost = value; OnPropertyChanged(); }
    }

    /// <summary>商店真实库存：随机陈列、购买扣库存、定时与手动刷新（--real-shop-stock）。</summary>
    [JsonPropertyName("realShopStock")]
    public bool RealShopStock
    {
        get => _realShopStock;
        set { _realShopStock = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public void CopyFrom(SettingsConfig other)
    {
        GameClientPath = other.GameClientPath;
        ServerDllPath = other.ServerDllPath;
        PythonPath = other.PythonPath;
        InjectorPath = other.InjectorPath;
        PayloadPath = other.PayloadPath;
        ProxyScriptPath = other.ProxyScriptPath;
        DataRoot = other.DataRoot;
        BaselinePath = other.BaselinePath;
        UpdateManifestUrl = other.UpdateManifestUrl;
        AutoUpdateEnabled = other.AutoUpdateEnabled;
        Region = other.Region;
        ServerPort = other.ServerPort;
        GameLoginPort = other.GameLoginPort;
        GmPort = other.GmPort;
        SkipBuild = other.SkipBuild;
        KeepLog = other.KeepLog;
        CopyCheatsFrom(other);
    }

    /// <summary>只复制作弊与原规则选项；「恢复默认」用它保留当前勾选。</summary>
    public void CopyCheatsFrom(SettingsConfig other)
    {
        CheatProduction = other.CheatProduction;
        CheatStrength = other.CheatStrength;
        CheatVow = other.CheatVow;
        CheatMood = other.CheatMood;
        CheatMedals = other.CheatMedals;
        CheatDrops = other.CheatDrops;
        CheatSweep = other.CheatSweep;
        RealResourceCost = other.RealResourceCost;
        RealShopStock = other.RealShopStock;
    }
}
