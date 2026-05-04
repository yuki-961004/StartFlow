using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Management.Deployment;
using Windows.Storage.Streams;
using StartFlow.Models;

namespace StartFlow.ViewModels;

public class AppItemViewModel : INotifyPropertyChanged
{
    public AppItem Item { get; }
    private ScheduleRule _rule;
    public ObservableCollection<string> AvailableTiers { get; }
    public string IconGlyph { get; private set; }
    public ImageSource? AppIcon { get; private set; }
    
    public Visibility FallbackVisibility => 
        AppIcon == null ? Visibility.Visible : Visibility.Collapsed;
        
    public Visibility RealIconVisibility => 
        AppIcon != null ? Visibility.Visible : Visibility.Collapsed;
        
    // UWP 图标因为自带巨大透明边距，我们在 UI 层将其进一步放大来抵消边距
    public double IconScale => Item.Source == StartupSource.UwpApp ? 1.8 : 1.0;

    private bool _isVisible = true;
    public bool IsVisible
    {
        get => _isVisible;
        set
        {
            if (_isVisible != value)
            {
                _isVisible = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ItemVisibility));
            }
        }
    }

    public Visibility ItemVisibility => _isVisible ? Visibility.Visible : Visibility.Collapsed;

    public AppItemViewModel(
        AppItem item, 
        ScheduleRule rule, 
        ObservableCollection<string> availableTiers)
    {
        Item = item;
        _rule = rule;
        AvailableTiers = availableTiers;
        IconGlyph = "\uE718"; // 统一的默认 App 占位图标
        _ = LoadRealIconAsync(); // 触发异步加载真实图标
    }

    public ScheduleRule Rule => _rule;

    // 下拉框索引直接对应优先级数字 (0=Disabled, 1=T1...)
    public int PriorityIndex
    {
        get => _rule.PriorityLevel;
        set
        {
            if (_rule.PriorityLevel != value)
            {
                _rule = _rule with { PriorityLevel = value };
                OnPropertyChanged();
            }
        }
    }

    // 双向绑定：静默启动开关
    public bool IsSilent
    {
        get => _rule.IsSilent;
        set
        {
            if (_rule.IsSilent != value)
            {
                _rule = _rule with { IsSilent = value };
                OnPropertyChanged();
            }
        }
    }

    // 双向绑定：启动前强制延迟的秒数（使用字符串以适配 UI TextBox 输入）
    public string DelaySecondsStr
    {
        get => _rule.DelaySeconds.ToString();
        set
        {
            if (int.TryParse(value, out int delay) && _rule.DelaySeconds != delay)
            {
                _rule = _rule with { DelaySeconds = delay };
                OnPropertyChanged();
            }
        }
    }

    // 核心黑科技：提取真实 EXE / LNK 文件的内置图标
    private async Task LoadRealIconAsync()
    {
        if (Item.Source == StartupSource.RegistryGhostItem) return;

        try
        {
            // 针对 UWP / 微软商店应用，调用 WinRT API 穿透沙盒提取真实高清图标
            if (Item.Source == StartupSource.UwpApp)
            {
                string aumid = Item.FilePath;
                int bangIndex = aumid.IndexOf('!');
                if (bangIndex > 0)
                {
                    string familyName = aumid.Substring(0, bangIndex);
                    var pkgManager = new PackageManager();
                    var package = pkgManager
                        .FindPackagesForUser(string.Empty, familyName)
                        .FirstOrDefault();

                    if (package != null)
                    {
                        var entries = await package.GetAppListEntriesAsync();
                        var entry = entries.FirstOrDefault();
                        if (entry != null)
                        {
                            // 请求 256x256 的高清尺寸，系统会自动下发最清晰的资产
                            var streamRef = entry.DisplayInfo.GetLogo(
                                new Windows.Foundation.Size(256, 256));
                            using var stream = await streamRef.OpenReadAsync();
                            
                            var bitmapImage = new BitmapImage();
                            await bitmapImage.SetSourceAsync(stream);
                            AppIcon = bitmapImage;
                            OnPropertyChanged(nameof(AppIcon));
                            OnPropertyChanged(nameof(FallbackVisibility));
                            OnPropertyChanged(nameof(RealIconVisibility));
                        }
                    }
                }
                return; // 提取完毕，跳过下方的 Win32 提取逻辑
            }

            if (!File.Exists(Item.FilePath)) return;

            byte[]? imageBytes = null;
            await Task.Run(() =>
            {
                // 回滚：使用 .NET 原生、极度稳定的 Icon 提取器
                using var icon = Icon.ExtractAssociatedIcon(Item.FilePath);
                if (icon == null) return;
                using var bmp = icon.ToBitmap();
                using var ms = new MemoryStream();
                bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                imageBytes = ms.ToArray();
            });

            if (imageBytes != null && imageBytes.Length > 0)
            {
                var ras = new InMemoryRandomAccessStream();
                using var dw = new DataWriter(ras.GetOutputStreamAt(0));
                dw.WriteBytes(imageBytes);
                await dw.StoreAsync();
                
                var bitmapImage = new BitmapImage();
                await bitmapImage.SetSourceAsync(ras);
                AppIcon = bitmapImage;
                OnPropertyChanged(nameof(AppIcon));
                OnPropertyChanged(nameof(FallbackVisibility));
                OnPropertyChanged(nameof(RealIconVisibility));
            }
        }
        catch { }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}