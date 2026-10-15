using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using BlueOath.Launcher.Wpf.Models;

namespace BlueOath.Launcher.Wpf.ViewModels;

public class MainViewModel : ViewModelBase
{
    public const string FeedbackUrl = "https://pd.qq.com/s/8gwks8zdo";

    private object _currentPage = null!;
    private int _selectedPageIndex = -1;
    private LaunchViewModel? _launchViewModel;

    public object CurrentPage
    {
        get => _currentPage;
        set => SetProperty(ref _currentPage, value);
    }

    public int SelectedPageIndex
    {
        get => _selectedPageIndex;
        set
        {
            if (SetProperty(ref _selectedPageIndex, value) && value < Pages.Count)
            {
                CurrentPage = Pages[value];
                (CurrentPage as INavigationAware)?.OnNavigatedTo();
            }
        }
    }

    public ObservableCollection<object> Pages { get; } = new();

    public ICommand NavigateToPageCommand { get; }

    public ICommand OpenFeedbackCommand { get; }

    public ICommand HideAntiResaleBannerCommand { get; }

    private bool _isAntiResaleBannerVisible = true;

    /// <summary>防倒卖提示横幅是否可见。仅内存状态，重启后恢复显示。</summary>
    public bool IsAntiResaleBannerVisible
    {
        get => _isAntiResaleBannerVisible;
        set => SetProperty(ref _isAntiResaleBannerVisible, value);
    }

    public MainViewModel()
    {
        NavigateToPageCommand = new RelayCommand<object>(param =>
        {
            int index = Convert.ToInt32(param);
            if (index >= 0 && index < Pages.Count)
            {
                SelectedPageIndex = index;
            }
        });

        OpenFeedbackCommand = new RelayCommand(OpenFeedback);
        HideAntiResaleBannerCommand = new RelayCommand(() => IsAntiResaleBannerVisible = false);
    }

    private static void OpenFeedback()
    {
        try
        {
            Process.Start(new ProcessStartInfo(FeedbackUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"无法打开反馈链接：{ex.Message}", "反馈", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    public void AddPage(object pageViewModel)
    {
        Pages.Add(pageViewModel);
    }

    public void NavigateTo(int index)
    {
        if (index >= 0 && index < Pages.Count)
        {
            SelectedPageIndex = index;
        }
    }

    public void RegisterLaunchViewModel(LaunchViewModel launchViewModel)
    {
        _launchViewModel = launchViewModel;
    }

    public void UpdateLaunchConfig(SettingsConfig settings)
    {
        if (_launchViewModel is null) return;
        var config = new LaunchConfig
        {
            Region = settings.Region,
            ServerPort = settings.ServerPort,
            GameLoginPort = settings.GameLoginPort,
            GmPort = settings.GmPort,
            SkipBuild = settings.SkipBuild,
            KeepLog = settings.KeepLog,
        };
        config.ApplyCheats(settings);
        _launchViewModel.Config = config;
    }
}

public class RelayCommand<T> : ICommand
{
    private readonly Action<T> _execute;
    private readonly Func<T, bool>? _canExecute;

    public RelayCommand(Action<T> execute, Func<T, bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter)
    {
        if (parameter is T t) return _canExecute?.Invoke(t) ?? true;
        return _canExecute?.Invoke(default!) ?? true;
    }

    public void Execute(object? parameter)
    {
        if (parameter is T t) _execute(t);
    }
}

public class RelayCommand : ICommand
{
    private readonly Action _execute;
    private readonly Func<bool>? _canExecute;

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => _canExecute?.Invoke() ?? true;
    public void Execute(object? parameter) => _execute();
}