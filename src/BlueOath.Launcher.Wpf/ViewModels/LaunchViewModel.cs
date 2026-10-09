using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Threading.Tasks;
using System.Windows.Input;
using BlueOath.Launcher.Wpf.Models;
using BlueOath.Launcher.Wpf.Services;

namespace BlueOath.Launcher.Wpf.ViewModels;

public class LaunchViewModel : ViewModelBase, INavigationAware
{
    private readonly ProcessManager _processManager;
    private readonly MainViewModel _mainViewModel;
    private readonly SettingsService _settingsService;
    private readonly AccountService _accountService;

    private List<Announcement> _announcements = new();
    private Announcement? _selectedAnnouncement;
    private bool _isLaunching;
    private string _statusText = "就绪";
    private string _cheatSummary = "";
    private LaunchConfig _config = new();

    public List<Announcement> Announcements
    {
        get => _announcements;
        set => SetProperty(ref _announcements, value);
    }

    public Announcement? SelectedAnnouncement
    {
        get => _selectedAnnouncement;
        set => SetProperty(ref _selectedAnnouncement, value);
    }

    public bool IsLaunching
    {
        get => _isLaunching;
        set => SetProperty(ref _isLaunching, value);
    }

    public string StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }

    /// <summary>启动页提示：当前设置里开启了哪些作弊与原规则选项；全关时为空串。</summary>
    public string CheatSummary
    {
        get => _cheatSummary;
        private set
        {
            if (SetProperty(ref _cheatSummary, value))
                OnPropertyChanged(nameof(HasCheats));
        }
    }

    public bool HasCheats => _cheatSummary.Length > 0;

    public LaunchConfig Config
    {
        get => _config;
        set => SetProperty(ref _config, value);
    }

    public int ServerPort
    {
        get => _config.ServerPort;
        set { _config.ServerPort = value; OnPropertyChanged(); }
    }

    public ObservableCollection<AccountProfile> Accounts => _accountService.Accounts;

    public AccountProfile ActiveAccount
    {
        get => _accountService.ActiveAccount;
        set
        {
            if (value is null || ReferenceEquals(value, _accountService.ActiveAccount)) return;
            try
            {
                _accountService.Select(value);
                OnPropertyChanged();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "账号切换失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                OnPropertyChanged();
            }
        }
    }

    public ICommand LaunchCommand { get; }
    public ICommand DebugLaunchCommand { get; }
    public ICommand ManageAccountsCommand { get; }
    public ICommand OpenSaveEditorCommand { get; }

    public string Version => VersionInfo.Version;

    /// <summary>在浏览器打开服务端 GM 控制台的存档编辑页（服务器需已由「启动游戏」启动）。</summary>
    private void OpenSaveEditor()
    {
        string url = $"http://localhost:{_settingsService.Load().GmPort}/save";
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"无法打开 {url}：{ex.Message}", "存档编辑", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    public LaunchViewModel(ProcessManager processManager, MainViewModel mainViewModel,
        SettingsService settingsService, AccountService accountService)
    {
        _processManager = processManager;
        _mainViewModel = mainViewModel;
        _settingsService = settingsService;
        _accountService = accountService;

        LaunchCommand = new RelayCommand(async () => await Launch(true));
        DebugLaunchCommand = new RelayCommand(async () => await Launch(false));
        ManageAccountsCommand = new RelayCommand(() => _mainViewModel.NavigateTo(2));
        OpenSaveEditorCommand = new RelayCommand(OpenSaveEditor);

        _accountService.ActiveAccountChanged += (_, _) => OnPropertyChanged(nameof(ActiveAccount));

        _processManager.StageChanged += (s, stage) =>
        {
            StatusText = stage switch
            {
                ProcessStage.Idle => "就绪",
                ProcessStage.CleaningUp => "正在清理...",
                ProcessStage.GeneratingTls => "正在生成 TLS 证书...",
                ProcessStage.StartingServer => "正在启动服务器...",
                ProcessStage.StartingProxy => "正在启动代理...",
                ProcessStage.InjectingGame => "正在注入游戏...",
                ProcessStage.Running => "运行中",
                ProcessStage.Stopping => "正在停止...",
                ProcessStage.Failed => "失败",
                _ => stage.ToString()
            };
            IsLaunching = _processManager.IsRunning;
        };
    }

    public void OnNavigatedTo()
    {
        // 从设置页回来时刷新作弊提示（设置页勾选即落盘）。
        SyncCheatsFromSettings();
    }

    /// <summary>
    /// 作弊开关以设置文件为准：设置页勾选即保存，这里每次重新读取，
    /// 不依赖各页面共享的 SettingsConfig 实例（「恢复默认」与选择客户端都会替换它）。
    /// </summary>
    private void SyncCheatsFromSettings()
    {
        _config.ApplyCheats(_settingsService.Load());
        CheatSummary = _config.SummaryText();
    }

    public void LoadAnnouncements(List<Announcement> announcements)
    {
        Announcements = announcements;
        if (announcements.Count > 0)
            SelectedAnnouncement = announcements[0];
    }

    private bool TryResolveGameClientPath()
    {
        var settings = _settingsService.Load();
        var clientDir = _processManager.ResolvePath(settings.GameClientPath);
        string exe = settings.Region == "cn" ? "clsy.exe" : "blueoath.exe";
        string exePath = Path.Combine(clientDir, exe);
        if (File.Exists(exePath)) return true;

        var result = MessageBox.Show("游戏客户端路径未设置或无效，是否现在选择？", "路径缺失",
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes) return false;

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择游戏客户端 (blueoath.exe 或 clsy.exe)",
            Filter = "游戏客户端|blueoath.exe;clsy.exe",
            CheckFileExists = true
        };
        if (dialog.ShowDialog() != true) return false;

        var selectedExe = dialog.FileName;
        var selectedDir = Path.GetDirectoryName(selectedExe) ?? "";
        settings.GameClientPath = _processManager.MakeRelativePath(selectedDir);
        if (Path.GetFileName(selectedExe).StartsWith("clsy", StringComparison.OrdinalIgnoreCase))
            settings.Region = "cn";
        _settingsService.Save(settings);
        _processManager.UpdateSettings(settings);
        return true;
    }

    private async Task Launch(bool startServer)
    {
        if (IsLaunching) return;

        if (!TryResolveGameClientPath()) return;

        // 启动时重新读取作弊开关：LaunchConfig 只在「保存设置」时重建，重启启动器后不会自动带上设置里的值。
        SyncCheatsFromSettings();

        _config.ProfileId = ActiveAccount.Id;
        _config.ProfileName = ActiveAccount.Name;
        var validationError = _processManager.ValidatePaths(_config, startServer);
        if (validationError is not null)
        {
            MessageBox.Show(validationError, "路径验证失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        IsLaunching = true;
        _mainViewModel.NavigateTo(1);

        await _processManager.LaunchAsync(_config, startServer);

        if (_processManager.Stage == ProcessStage.Failed)
        {
            var error = _processManager.LastError;
            if (!string.IsNullOrEmpty(error))
            {
                MessageBox.Show($"启动失败: {error}", "启动失败", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        IsLaunching = false;
    }
}
