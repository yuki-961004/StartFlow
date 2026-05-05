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
    
    // 生成无图标时的首字母文字头像 (例如 "Logitech" -> "L")
    public string FallbackText
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Item.Name)) return "?";
            var firstChar = Item.Name.Trim().FirstOrDefault(c => char.IsLetterOrDigit(c));
            return firstChar == '\0' ? "?" : firstChar.ToString().ToUpperInvariant();
        }
    }
    public ImageSource? AppIcon { get; private set; }
    
    public Visibility FallbackVisibility => 
        AppIcon == null ? Visibility.Visible : Visibility.Collapsed;
        
    public Visibility RealIconVisibility => 
        AppIcon != null ? Visibility.Visible : Visibility.Collapsed;
        
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
        _ = LoadRealIconAsync(); // 触发异步加载真实图标
    }

    public ScheduleRule Rule => _rule;

    // 允许更新内部 Rule 以保持内存和本地数据一致
    public void UpdateRule(ScheduleRule newRule)
    {
        _rule = newRule;
    }

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
        if (Item.Sources.Contains(StartupSource.RegistryGhostItem)) return;

        try
        {
            // 针对 UWP / 微软商店应用，调用 WinRT API 穿透沙盒提取真实高清图标
            if (Item.Sources.Contains(StartupSource.UwpApp))
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
                            // 请求 256 高清尺寸，防止裁切后变糊
                            var streamRef = entry.DisplayInfo.GetLogo(
                                new Windows.Foundation.Size(256, 256));
                            using var stream = await streamRef.OpenReadAsync();
                            using var memStream = new MemoryStream();
                            await stream.AsStreamForRead().CopyToAsync(memStream);
                            
                            // 通过后台线程智能裁切 UWP 图标的巨大透明废边
                            byte[]? finalBytes = await Task.Run(() => AutoCropIcon(memStream.ToArray()));
                            if (finalBytes != null)
                            {
                                await RenderIconFromBytesAsync(finalBytes);
                            }
                        }
                    }
                }
                return; // 提取完毕，跳过下方的 Win32 提取逻辑
            }

            if (!File.Exists(Item.FilePath)) return;

            byte[]? imageBytes = await Task.Run<byte[]?>(() =>
            {
                // 回滚：使用 .NET 原生、极度稳定的 Icon 提取器
                using var icon = Icon.ExtractAssociatedIcon(Item.FilePath);
                if (icon == null) return null;
                using var bmp = icon.ToBitmap();
                using var ms = new MemoryStream();
                bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                return AutoCropIcon(ms.ToArray()); // Win32 图标同样过一遍智能裁切
            });

            if (imageBytes != null && imageBytes.Length > 0)
            {
                await RenderIconFromBytesAsync(imageBytes);
            }
        }
        catch { }
    }

    private async Task RenderIconFromBytesAsync(byte[] imageBytes)
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

    // 世界级核心算法：基于 Alpha 通道智能裁切透明废边，使一切图标饱满统一
    private byte[]? AutoCropIcon(byte[] imageBytes)
    {
        try
        {
            using var ms = new MemoryStream(imageBytes);
            using var bmp = new System.Drawing.Bitmap(ms);

            var rect = new System.Drawing.Rectangle(0, 0, bmp.Width, bmp.Height);
            var bmpData = bmp.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);

            int bytesCount = Math.Abs(bmpData.Stride) * bmp.Height;
            byte[] rgbValues = new byte[bytesCount];
            System.Runtime.InteropServices.Marshal.Copy(bmpData.Scan0, rgbValues, 0, bytesCount);
            bmp.UnlockBits(bmpData);

            int top = bmp.Height, bottom = -1, left = bmp.Width, right = -1;
            int stride = bmpData.Stride;

            for (int y = 0; y < bmp.Height; y++)
            {
                int rowStart = y * stride;
                for (int x = 0; x < bmp.Width; x++)
                {
                    byte alpha = rgbValues[rowStart + x * 4 + 3];
                    if (alpha > 10) // 容忍微弱抗锯齿产生的低透明像素
                    {
                        if (x < left) left = x;
                        if (x > right) right = x;
                        if (y < top) top = y;
                        if (y > bottom) bottom = y;
                    }
                }
            }

            if (left > right || top > bottom) return imageBytes; // 全透明图片直接返回

            int width = right - left + 1;
            int height = bottom - top + 1;

            // 增加 5% 相对的舒适内边距，防贴边
            int margin = Math.Max(2, (int)(Math.Max(width, height) * 0.05));
            int maxDim = Math.Max(width, height) + margin * 2; 
            
            // 如果原图本身就已经很饱满了 (占 90% 以上)，直接放行以节约性能
            if (maxDim >= Math.Max(bmp.Width, bmp.Height) * 0.9) return imageBytes;

            int centerX = left + width / 2;
            int centerY = top + height / 2;

            int cropLeft = Math.Max(0, centerX - maxDim / 2);
            int cropTop = Math.Max(0, centerY - maxDim / 2);
            int cropRight = Math.Min(bmp.Width - 1, centerX + maxDim / 2);
            int cropBottom = Math.Min(bmp.Height - 1, centerY + maxDim / 2);

            int finalWidth = cropRight - cropLeft + 1;
            int finalHeight = cropBottom - cropTop + 1;

            using var croppedBmp = new System.Drawing.Bitmap(finalWidth, finalHeight);
            using var g = System.Drawing.Graphics.FromImage(croppedBmp);
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
            
            g.DrawImage(bmp, new System.Drawing.Rectangle(0, 0, finalWidth, finalHeight), 
                        new System.Drawing.Rectangle(cropLeft, cropTop, finalWidth, finalHeight), System.Drawing.GraphicsUnit.Pixel);

            using var outMs = new MemoryStream();
            croppedBmp.Save(outMs, System.Drawing.Imaging.ImageFormat.Png);
            return outMs.ToArray();
        }
        catch
        {
            return imageBytes; // 如果解码或运算出现任何意外，安全回退到原图
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}