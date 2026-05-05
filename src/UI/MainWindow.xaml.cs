using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.IO;
using System.Text.Json;
using System.Linq;
using System.Threading.Tasks;
using Windows.Graphics;
using StartFlow.ViewModels;

namespace StartFlow.UI;

public sealed partial class MainWindow : Window
{
    public class CurtainDragItem : System.ComponentModel.INotifyPropertyChanged
    {
        private string _name = string.Empty;
        public string Name { get => _name; set { _name = value; PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Name))); } }
        public bool IsCurtain { get; set; }
        
        public Windows.UI.Text.FontWeight FontWeight => IsCurtain ? Microsoft.UI.Text.FontWeights.Bold : Microsoft.UI.Text.FontWeights.Normal;
        
        // 使用纯白色突出幕布条
        public Microsoft.UI.Xaml.Media.Brush TextColor => IsCurtain 
            ? new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White) 
            : new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Gray);

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    }

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
    private System.Collections.ObjectModel.ObservableCollection<EditableTier>? _tempTiers;

    public MainWindow()
    {
        this.InitializeComponent();
        ViewModel = new MainViewModel();

        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        _uiSettingsPath = Path.Combine(appData, "StartFlow", "ui_settings.json");

        // 订阅窗体激活事件，在显示时自动加载数据
        this.Activated += MainWindow_Activated;
        this.Closed += MainWindow_Closed;
        
        // 设置运行时窗口和任务栏图标
        try
        {
            string iconPath = Path.Combine(AppContext.BaseDirectory, "StartFlow.ico");
            if (File.Exists(iconPath))
            {
                this.AppWindow.SetIcon(iconPath);
            }
        }
        catch { }

        LoadUISettings();
    }

    // UI 辅助方法：禁用组 (Disabled) 不需要一键静默按钮
    public static Visibility GetSilentButtonVisibility(string tierName)
    {
        return string.Equals(tierName, "Disabled", StringComparison.OrdinalIgnoreCase) 
            ? Visibility.Collapsed : Visibility.Visible;
    }

        // UI 辅助方法：根据静默状态返回对应的图标
        public static string GetSilentIcon(bool isSilent)
        {
            return isSilent ? "\uED1A" : "\uE890"; // \uED1A(带斜线的闭眼), \uE890(正常睁眼)
        }

    public static string GetShortTierName(string fullName)
    {
        if (string.IsNullOrWhiteSpace(fullName)) return string.Empty;
        if (fullName.StartsWith("T", StringComparison.OrdinalIgnoreCase) && fullName.Length > 1 && char.IsDigit(fullName[1]))
        {
            int spaceIndex = fullName.IndexOf(' ');
            if (spaceIndex > 0) return fullName.Substring(0, spaceIndex);
        }
        return fullName;
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

    private async void ManageTiers_Click(object sender, RoutedEventArgs e)
    {
        _tempTiers = new System.Collections.ObjectModel.ObservableCollection<EditableTier>();
        for (int i = 1; i < ViewModel.AvailableTiers.Count; i++)
        {
            _tempTiers.Add(new EditableTier { Name = ViewModel.AvailableTiers[i], OriginalIndex = i });
        }
        
        // 订阅集合变更事件，实现在拖放或增删时自动重排行号
        _tempTiers.CollectionChanged += (s, e) => UpdateTierNames();

        var listView = new ListView
        {
            ItemsSource = _tempTiers,
            SelectionMode = ListViewSelectionMode.None,
            CanReorderItems = true,
            AllowDrop = true,
            ItemTemplate = (DataTemplate)((FrameworkElement)this.Content).Resources["ManageTierItemTemplate"],
            Footer = new Button 
            { 
                Content = new FontIcon { Glyph = "\uE710", FontSize = 14 }, 
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent),
                BorderThickness = new Thickness(0),
                Height = 40
            }
        };
        
        ((Button)listView.Footer).Click += (s, args) =>
        {
            _tempTiers.Add(new EditableTier { Name = "New Tier", OriginalIndex = -1 });
        };

        var dialog = new ContentDialog
        {
            Title = "Manage Tiers",
            Content = new ScrollViewer { Content = listView, MaxHeight = 400 },
            PrimaryButtonText = "Apply",
            CloseButtonText = "Cancel",
            XamlRoot = this.Content.XamlRoot,
            RequestedTheme = _currentTheme
        };

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            UpdateTierNames(); // 确保最终保存前全部名称都经过规范化洗牌
            await ViewModel.ApplyTiersManagementAsync(_tempTiers);
        }
        _tempTiers = null;
    }

    private void ManageTierItem_DoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement element && element.DataContext is EditableTier tier)
        {
            tier.IsEditing = true;
            if (element is Grid grid)
            {
                var textBox = grid.Children.OfType<TextBox>().FirstOrDefault();
                if (textBox != null)
                {
                    textBox.Focus(FocusState.Programmatic);
                    textBox.SelectAll();
                }
            }
        }
    }

    // 自动重排并规范化列表名称的方法
    private void UpdateTierNames()
    {
        if (_tempTiers == null) return;
        for (int i = 0; i < _tempTiers.Count; i++)
        {
            var tier = _tempTiers[i];
            var customName = ExtractCustomName(tier.Name);
            var expectedName = $"T{i + 1} ({customName})";
            
            if (tier.Name != expectedName)
            {
                tier.Name = expectedName;
            }
        }
    }

    // 解析方法：智能从原字符串中剥离出“自定义的真实名字”
    public static string ExtractCustomName(string fullName)
    {
        if (string.IsNullOrWhiteSpace(fullName)) return "";
        var match1 = System.Text.RegularExpressions.Regex.Match(fullName, @"^T\d+\s*\((.*)\)$");
        if (match1.Success) return match1.Groups[1].Value;
        var match2 = System.Text.RegularExpressions.Regex.Match(fullName, @"^T\d+\s+(.*)$");
        if (match2.Success) return match2.Groups[1].Value;
        if (fullName.StartsWith("(") && fullName.EndsWith(")")) return fullName.Substring(1, fullName.Length - 2);
        return fullName;
    }

    private void ManageTierItem_TextBoxLostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element && element.DataContext is EditableTier tier)
        {
            tier.IsEditing = false;
            UpdateTierNames(); // 失去焦点保存时重组 T{n} 前缀
        }
    }

    private void ManageTierItem_TextBoxKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            if (sender is FrameworkElement element && element.DataContext is EditableTier tier)
            {
                tier.IsEditing = false;
                UpdateTierNames(); // 回车保存时重组 T{n} 前缀
            }
        }
    }

    private void RemoveManageTier_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element && element.DataContext is EditableTier tier && _tempTiers != null)
        {
            _tempTiers.Remove(tier);
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
            
            if (theme == ElementTheme.Dark)
            {
                var darkBg = Windows.UI.Color.FromArgb(255, 32, 32, 32);
                titleBar.BackgroundColor = darkBg;
                titleBar.ButtonBackgroundColor = darkBg;
                titleBar.InactiveBackgroundColor = darkBg;
                titleBar.ButtonInactiveBackgroundColor = darkBg;
                
                titleBar.ForegroundColor = Microsoft.UI.Colors.White;
                titleBar.ButtonForegroundColor = Microsoft.UI.Colors.White;
                titleBar.ButtonHoverBackgroundColor = Windows.UI.Color.FromArgb(255, 50, 50, 50);
                titleBar.ButtonHoverForegroundColor = Microsoft.UI.Colors.White;
                titleBar.ButtonPressedBackgroundColor = Windows.UI.Color.FromArgb(255, 70, 70, 70);
                titleBar.ButtonPressedForegroundColor = Microsoft.UI.Colors.White;
                titleBar.InactiveForegroundColor = Microsoft.UI.Colors.Gray;
            }
            else
            {
                var lightBg = Windows.UI.Color.FromArgb(255, 243, 243, 243);
                titleBar.BackgroundColor = lightBg;
                titleBar.ButtonBackgroundColor = lightBg;
                titleBar.InactiveBackgroundColor = lightBg;
                titleBar.ButtonInactiveBackgroundColor = lightBg;
                
                titleBar.ForegroundColor = Microsoft.UI.Colors.Black;
                titleBar.ButtonForegroundColor = Microsoft.UI.Colors.Black;
                titleBar.ButtonHoverBackgroundColor = Windows.UI.Color.FromArgb(255, 220, 220, 220);
                titleBar.ButtonHoverForegroundColor = Microsoft.UI.Colors.Black;
                titleBar.ButtonPressedBackgroundColor = Windows.UI.Color.FromArgb(255, 200, 200, 200);
                titleBar.ButtonPressedForegroundColor = Microsoft.UI.Colors.Black;
                titleBar.InactiveForegroundColor = Microsoft.UI.Colors.Gray;
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
        var settings = ViewModel.GlobalSettings;
        
        var toggleEnable = new ToggleSwitch { Header = "Enable Transition Curtain (Wait For It)", IsOn = settings.EnableCurtain };
        
        var btnPickImage = new Button { Content = "Select Background Image" };
        var txtImagePath = new TextBlock { Text = string.IsNullOrEmpty(settings.CurtainImagePath) ? "No image selected (Black background)" : settings.CurtainImagePath, TextWrapping = TextWrapping.Wrap, MaxWidth = 300, VerticalAlignment = VerticalAlignment.Center };
        
        var sliderBlur = new Slider { Header = "Blur Intensity", Minimum = 0, Maximum = 100, Value = settings.CurtainBlurOpacity * 100 };
        var toggleSpinner = new ToggleSwitch { Header = "Show Loading Spinner", IsOn = settings.CurtainShowSpinner };
        var toggleAnimation = new ToggleSwitch { Header = "Enable Water Ripple Animation", IsOn = settings.CurtainEnableAnimation };
        var txtCurtainText = new TextBox { Header = "Loading Text", Text = settings.CurtainText, PlaceholderText = "e.g. Wait for it..." };
        var btnPreview = new Button { Content = "Preview Curtain" };

        // 构建可供拖拽排序的 T 级列表和特殊幕布长条
        var dragList = new System.Collections.ObjectModel.ObservableCollection<CurtainDragItem>();
        bool curtainAdded = false;
        string initialMarker = $"--- {txtCurtainText.Text} ---";
        
        if (settings.TargetCurtainTier == 0) { dragList.Add(new CurtainDragItem { Name = initialMarker, IsCurtain = true }); curtainAdded = true; }
        for (int i = 1; i < ViewModel.AvailableTiers.Count; i++)
        {
            dragList.Add(new CurtainDragItem { Name = ViewModel.AvailableTiers[i], IsCurtain = false });
            if (!curtainAdded && settings.TargetCurtainTier == i)
            {
                dragList.Add(new CurtainDragItem { Name = initialMarker, IsCurtain = true });
                curtainAdded = true;
            }
        }
        if (!curtainAdded) dragList.Add(new CurtainDragItem { Name = initialMarker, IsCurtain = true });

        // 实现实时联动：修改文本框时，拖拽列表中的长条名字也跟着变
        txtCurtainText.TextChanged += (s, args) =>
        {
            foreach (var item in dragList)
            {
                if (item.IsCurtain)
                {
                    item.Name = $"--- {txtCurtainText.Text} ---";
                    break;
                }
            }
        };

        var listView = new ListView { ItemsSource = dragList, CanReorderItems = true, AllowDrop = true, SelectionMode = ListViewSelectionMode.None, MaxHeight = 250, Margin = new Thickness(0, 10, 0, 0), ItemTemplate = (DataTemplate)((FrameworkElement)this.Content).Resources["CurtainDragItemTemplate"] };
        
        var containerStyle = new Style(typeof(ListViewItem));
        containerStyle.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        containerStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0)));
        containerStyle.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, 0.0));
        listView.ItemContainerStyle = containerStyle;

        var settingsPanel = new StackPanel { Spacing = 12, Margin = new Thickness(0, 20, 0, 0), Visibility = settings.EnableCurtain ? Visibility.Visible : Visibility.Collapsed };
        settingsPanel.Children.Add(new TextBlock { Text = "Appearance Settings", FontWeight = Microsoft.UI.Text.FontWeights.Bold });
        
        var imagePanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        imagePanel.Children.Add(btnPickImage);
        imagePanel.Children.Add(txtImagePath);
        settingsPanel.Children.Add(imagePanel);
        
        settingsPanel.Children.Add(sliderBlur);
        settingsPanel.Children.Add(toggleSpinner);
        settingsPanel.Children.Add(toggleAnimation);
        settingsPanel.Children.Add(txtCurtainText);
        settingsPanel.Children.Add(btnPreview);
        
        settingsPanel.Children.Add(new TextBlock { Text = "Drag the Curtain below to set when it dismisses:", FontWeight = Microsoft.UI.Text.FontWeights.Bold, Margin = new Thickness(0, 10, 0, 0) });
        settingsPanel.Children.Add(listView);

        toggleEnable.Toggled += (s, args) => { settingsPanel.Visibility = toggleEnable.IsOn ? Visibility.Visible : Visibility.Collapsed; };
        
        btnPickImage.Click += async (s, args) => {
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            picker.ViewMode = Windows.Storage.Pickers.PickerViewMode.Thumbnail;
            picker.FileTypeFilter.Add(".jpg"); picker.FileTypeFilter.Add(".jpeg"); picker.FileTypeFilter.Add(".png"); picker.FileTypeFilter.Add(".bmp");
            var file = await picker.PickSingleFileAsync();
            if (file != null) { txtImagePath.Text = file.Path; }
        };

        btnPreview.Click += async (s, args) => {
            var previewWindow = new CurtainWindow(txtImagePath.Text == "No image selected (Black background)" ? "" : txtImagePath.Text, sliderBlur.Value / 100.0, toggleSpinner.IsOn, txtCurtainText.Text, toggleAnimation.IsOn);
            previewWindow.Activate();
            await Task.Delay(3000); // 预览 3 秒后自动关闭
            await previewWindow.DismissAsync();
        };

        var rootPanel = new StackPanel { Spacing = 10 };
        rootPanel.Children.Add(toggleEnable);
        rootPanel.Children.Add(settingsPanel);

        var dialog = new ContentDialog
        {
            Title = "Global Orchestrator Settings",
            Content = new ScrollViewer { Content = rootPanel },
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            XamlRoot = this.Content.XamlRoot,
            RequestedTheme = _currentTheme
        };
        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            settings.EnableCurtain = toggleEnable.IsOn;
            settings.CurtainImagePath = txtImagePath.Text == "No image selected (Black background)" ? "" : txtImagePath.Text;
            settings.CurtainBlurOpacity = sliderBlur.Value / 100.0;
            settings.CurtainShowSpinner = toggleSpinner.IsOn;
            settings.CurtainText = txtCurtainText.Text;
            settings.CurtainEnableAnimation = toggleAnimation.IsOn;
            
            int tierCount = 0;
            foreach (var item in dragList)
            {
                if (item.IsCurtain) break; // 探测幕布长条被拖到了哪个 T 级的后面
                tierCount++;
            }
            settings.TargetCurtainTier = tierCount;
            ViewModel.SaveGlobalSettings();
        }
    }

    private void ToggleGroup_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element && 
            element.DataContext is TierGroup group)
        {
            group.ToggleExpand();
        }
    }

        private void ToggleAppSilent_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement element && 
                element.DataContext is AppItemViewModel item)
            {
                item.IsSilent = !item.IsSilent;
            }
        }

    private void ToggleTierActions_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element && element.DataContext is TierGroup group)
        {
            group.ToggleActions();
        }
    }

    private void SetTierSilent_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element && 
            element.DataContext is TierGroup group)
        {
            // 检查是否该组内的所有项目都已经设为静默
            bool allSilent = true;
            foreach (var item in group)
            {
                if (!item.IsSilent)
                {
                    allSilent = false;
                    break;
                }
            }

            // 如果全都静默了，就全部取消静默；否则全部设为静默
            foreach (var item in group)
            {
                item.IsSilent = !allSilent;
            }
        }
    }

    private async void TierSettings_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement element && 
                element.DataContext is TierGroup group)
            {
                var stackPanel = new StackPanel { Spacing = 15 };

                var delayInput = new TextBox
                {
                    Header = "Tier Startup Delay (seconds)",
                    PlaceholderText = "e.g. 5",
                    Text = group.DelaySeconds.ToString() 
                };

                var modeLabel = new TextBlock { Text = "Execution Mode", Margin = new Thickness(0, 10, 0, 0) };
                
                var radioParallel = new RadioButton { Content = "Parallel Startup (All at once)", IsChecked = !group.IsSequential };
                var radioSequential = new RadioButton { Content = "Sequential Startup (One by one based on order)", IsChecked = group.IsSequential };
                
                var radioStack = new StackPanel { Spacing = 8 };
                radioStack.Children.Add(radioParallel);
                radioStack.Children.Add(radioSequential);
                
                stackPanel.Children.Add(delayInput);
                stackPanel.Children.Add(modeLabel);
                stackPanel.Children.Add(radioStack);

                var dialog = new ContentDialog
                {
                    Title = $"{group.TierName} Settings",
                    Content = stackPanel,
                    PrimaryButtonText = "Save",
                    CloseButtonText = "Cancel",
                    XamlRoot = this.Content.XamlRoot,
                    RequestedTheme = _currentTheme
                };

                var result = await dialog.ShowAsync();
                if (result == ContentDialogResult.Primary)
                {
                    if (int.TryParse(delayInput.Text, out int delay))
                    {
                        group.DelaySeconds = delay;
                    }
                    group.IsSequential = radioSequential.IsChecked == true;

                    // 自动保存配置，防止后台 Orchestrator 读取到未保存的 0 秒旧配置
                    ViewModel.SaveConfiguration();
                }
            }
        }
    }

public class ShortTierNameConverter : Microsoft.UI.Xaml.Data.IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is string fullName)
        {
            return MainWindow.GetShortTierName(fullName);
        }
        return value;
    }
    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotImplementedException();
}

public class TierTemplateSelector : Microsoft.UI.Xaml.Controls.DataTemplateSelector
{
    public DataTemplate? SelectedTemplate { get; set; }
    public DataTemplate? DropdownTemplate { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container)
    {
        // ComboBoxItem 意味着当前控件正处于下拉列表的内部渲染流程
        if (container is Microsoft.UI.Xaml.Controls.ComboBoxItem) return DropdownTemplate;
        return SelectedTemplate;
    }
}

// 用于将 `T1 (Late Start)` 和 `Late Start` 进行转换，以便用户编辑时只会看到干净的内容
public class TierCustomNameConverter : Microsoft.UI.Xaml.Data.IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) => MainWindow.ExtractCustomName(value as string ?? "");
    public object ConvertBack(object value, Type targetType, object parameter, string language) => value as string ?? "";
}