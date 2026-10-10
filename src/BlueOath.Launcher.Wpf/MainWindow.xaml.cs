using System;
using System.IO;
using System.Windows;
using BlueOath.Launcher.Wpf.Services;
using BlueOath.Launcher.Wpf.ViewModels;

namespace BlueOath.Launcher.Wpf;

public partial class MainWindow : Window
{
    private readonly MainViewModel _mainViewModel = new();

    public MainWindow()
    {
        InitializeComponent();
        Title = $"BlueOath Rebirth 启动器 v{VersionInfo.Version}";

        var rootDir = FindRoot();
        var settingsService = new SettingsService();
        var settings = settingsService.Load();
        InitializeViews(rootDir, settingsService, settings);
    }

    private void InitializeViews(string rootDir, SettingsService settingsService, BlueOath.Launcher.Wpf.Models.SettingsConfig settings)
    {
        var processManager = new ProcessManager(rootDir, settings);
        var accountService = new AccountService();

        var launchViewModel = new LaunchViewModel(processManager, _mainViewModel, settingsService, accountService);
        _mainViewModel.RegisterLaunchViewModel(launchViewModel);
        var announcementService = new AnnouncementService();
        launchViewModel.LoadAnnouncements(announcementService.LoadAnnouncements());

        var guardianViewModel = new GuardianViewModel(processManager, _mainViewModel);
        var accountsViewModel = new AccountsViewModel(accountService);
        var modViewModel = new ModViewModel(new ModService(rootDir), _mainViewModel);
        var settingsViewModel = new SettingsViewModel(settingsService, _mainViewModel, settings);

        _mainViewModel.AddPage(launchViewModel);
        _mainViewModel.AddPage(guardianViewModel);
        _mainViewModel.AddPage(accountsViewModel);
        _mainViewModel.AddPage(modViewModel);
        _mainViewModel.AddPage(settingsViewModel);
        _mainViewModel.SelectedPageIndex = 0;

        DataContext = _mainViewModel;
    }

    private static string FindRoot() => Services.AppPaths.ResolveRoot();
}
