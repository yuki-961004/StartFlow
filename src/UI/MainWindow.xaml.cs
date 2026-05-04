using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.IO;
using System.Text.Json;
using Windows.Graphics;
using StartFlow.ViewModels;

namespace StartFlow.UI;

public sealed partial class MainWindow : Window
{
    // 轻量级 UI 记忆数据模型
    public class UISettings
    {
        public int WindowWidth { get; set; } = 1100;
        public int WindowHeight { get; set; } = 800;
        public string Theme { get; set; } = "Dark";
    }

    public MainViewModel ViewModel { get; }
    private ElementTheme _currentTheme = ElementTheme.Dark; // 默认深色主题
    private readonly string _uiSettingsPath;
    private UISettings _uiSettings = new();

    public MainWindow()
    {
        this.InitializeComponent();
        ViewModel = new MainViewModel();

        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        _uiSettingsPath = Path.Combine(appData, "StartFlow", "ui_settings.json");

        // 订阅窗体激活事件，在显示时自动加载数据
        this.Activated += MainWindow_Activated;
        this.Closed += MainWindow_Closed;
        
        LoadUISettings();
    }

    // 纯UI层副作用：恢复窗口尺寸与主题
    private void LoadUISettings()
    {
        try
        {
            if (File.Exists(_uiSettingsPath))
            {
                string json = File.ReadAllText(_uiSettingsPath);
                _uiSettings = JsonSerializer.Deserialize<UISettings>(json) ?? new UISettings();
            }
        }
        catch { }

        this.AppWindow.Resize(new SizeInt32(_uiSettings.WindowWidth, _uiSettings.WindowHeight));

        if (Enum.TryParse(_uiSettings.Theme, out ElementTheme theme))
        {
            ApplyTheme(theme);
        }
        else
        {
            ApplyTheme(ElementTheme.Dark); // 响应需求：默认启用暗色主题
        }
    }

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        _uiSettings.WindowWidth = this.AppWindow.Size.Width;
        _uiSettings.WindowHeight = this.AppWindow.Size.Height;
        _uiSettings.Theme = _currentTheme.ToString();

        try
        {
            File.WriteAllText(_uiSettingsPath, JsonSerializer.Serialize(_uiSettings));
        }
        catch { }
    }

    private async void MainWindow_Activated(
        object sender, WindowActivatedEventArgs args)
    {
        this.Activated -= MainWindow_Activated; // 确保只执行一次
        await ViewModel.LoadItemsAsync();
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.LoadItemsAsync();
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.SaveConfiguration();
        // 保存后重新加载以刷新界面的 Disabled 组归属
        _ = ViewModel.LoadItemsAsync();
    }

    private async void AddTier_Click(object sender, RoutedEventArgs e)
    {
        var inputTextBox = new TextBox 
        { 
            PlaceholderText = "e.g. Others (Optional)",
            Width = 300
        };
        
        var dialog = new ContentDialog
        {
            Title = "Add New Tier",
            Content = inputTextBox,
            PrimaryButtonText = "Add",
            CloseButtonText = "Cancel",
            XamlRoot = this.Content.XamlRoot,
            DefaultButton = ContentDialogButton.Primary,
            RequestedTheme = _currentTheme
        };

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            ViewModel.AddNewTier(inputTextBox.Text);
            _ = ViewModel.LoadItemsAsync();
        }
    }

    private async void RemoveTier_Click(object sender, RoutedEventArgs e)
    {
        // 获取除了 Disabled (索引0) 之外的所有 Tier 供用户选择
        var removableTiers = new System.Collections.Generic.List<string>();
        for (int i = 1; i < ViewModel.AvailableTiers.Count; i++)
        {
            removableTiers.Add(ViewModel.AvailableTiers[i]);
        }

        var listView = new ListView
        {
            ItemsSource = removableTiers,
            SelectionMode = ListViewSelectionMode.Single
        };

        var dialog = new ContentDialog
        {
            Title = "Select a Tier to Remove",
            Content = listView,
            PrimaryButtonText = "Remove",
            CloseButtonText = "Cancel",
            XamlRoot = this.Content.XamlRoot,
            RequestedTheme = _currentTheme
        };

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary && listView.SelectedIndex >= 0)
        {
            // ListView 的 SelectedIndex 0 对应 AvailableTiers 的 1
            int realIndex = listView.SelectedIndex + 1;
            await ViewModel.RemoveTierAsync(realIndex);
        }
    }

    private async void AddButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        picker.ViewMode = Windows.Storage.Pickers.PickerViewMode.List;
        picker.FileTypeFilter.Add(".lnk"); // 推荐用户添加快捷方式
        picker.FileTypeFilter.Add(".exe");

        var file = await picker.PickSingleFileAsync();
        if (file != null) await ViewModel.AddProgramToStartupAsync(file.Path);
    }

    private void OpenLocation_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element && 
            element.DataContext is AppItemViewModel vm)
        {
            ViewModel.OpenItemLocation(vm.Item);
        }
    }

    private void ApplyTheme(ElementTheme theme)
    {
        _currentTheme = theme;
        if (this.Content is FrameworkElement rootElement)
        {
            rootElement.RequestedTheme = theme;
        }

        // 同步修改 Windows 标题栏颜色
        if (Microsoft.UI.Windowing.AppWindowTitleBar.IsCustomizationSupported())
        {
            var titleBar = this.AppWindow.TitleBar;
            var transparent = Microsoft.UI.Colors.Transparent;
            titleBar.ButtonBackgroundColor = transparent;
            titleBar.ButtonInactiveBackgroundColor = transparent;
            
            if (theme == ElementTheme.Dark)
            {
                titleBar.ButtonForegroundColor = Microsoft.UI.Colors.White;
                titleBar.ButtonHoverForegroundColor = Microsoft.UI.Colors.White;
                titleBar.ButtonHoverBackgroundColor = 
                    Windows.UI.Color.FromArgb(25, 255, 255, 255);
            }
            else
            {
                titleBar.ButtonForegroundColor = Microsoft.UI.Colors.Black;
                titleBar.ButtonHoverForegroundColor = Microsoft.UI.Colors.Black;
                titleBar.ButtonHoverBackgroundColor = 
                    Windows.UI.Color.FromArgb(25, 0, 0, 0);
            }
        }

        // 图标切换：深色模式显示太阳，浅色模式显示月亮
        ThemeButton.Icon = new FontIcon 
            { Glyph = theme == ElementTheme.Dark ? "\uE706" : "\uE708" };
    }

    private void ThemeButton_Click(object sender, RoutedEventArgs e)
    {
        var newTheme = _currentTheme == ElementTheme.Dark 
            ? ElementTheme.Light : ElementTheme.Dark;
        ApplyTheme(newTheme);
    }

    private async void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            Title = "Settings",
            Content = "Global orchestrator settings are coming soon!",
            CloseButtonText = "OK",
            XamlRoot = this.Content.XamlRoot,
            RequestedTheme = _currentTheme
        };
        await dialog.ShowAsync();
    }

    private void ToggleGroup_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element && 
            element.DataContext is TierGroup group)
        {
            group.ToggleExpand();
        }
    }

    private void SetTierSilent_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element && 
            element.DataContext is TierGroup group)
        {
            foreach (var item in group)
            {
                item.IsSilent = true;
            }
        }
    }
}