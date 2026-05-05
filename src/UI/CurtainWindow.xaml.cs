using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.Graphics.Canvas.Effects;

namespace StartFlow.UI;

public sealed partial class CurtainWindow : Window
{
    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
    private static extern IntPtr GetWindowLong32(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
    [DllImport("user32.dll", EntryPoint = "SetWindowLong")]
    private static extern IntPtr SetWindowLong32(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
    [DllImport("user32.dll")]
    private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

    private static IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex) => IntPtr.Size == 8 ? GetWindowLongPtr64(hWnd, nIndex) : GetWindowLong32(hWnd, nIndex);
    private static IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong) => IntPtr.Size == 8 ? SetWindowLongPtr64(hWnd, nIndex, dwNewLong) : SetWindowLong32(hWnd, nIndex, dwNewLong);

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_LAYERED = 0x00080000;
    private const uint LWA_ALPHA = 0x00000002;
    private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;

    private readonly bool _enableAnimation;
    private readonly DateTime _showTime; // 记录幕布实例化的时间

    public CurtainWindow(string imagePath = "", double blurOpacity = 1.0, bool showSpinner = true, string customText = "Wait for it...", bool enableAnimation = true)
    {
        this.InitializeComponent();
        _enableAnimation = enableAnimation;
        
        // 将窗口设为无边框全屏模式
        this.ExtendsContentIntoTitleBar = true;
        this.AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);

        // 调用底层的 Win32 API 强制设为 TopMost 置顶，防止被启动中的其他软件抢占焦点
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE);
        
        _showTime = DateTime.UtcNow;

        LoadingText.Text = string.IsNullOrWhiteSpace(customText) ? "Preparing Desktop Environment..." : customText;

        if (showSpinner) 
        { 
            Spinner.IsActive = true; 
            Spinner.Visibility = Visibility.Visible; 
        }
        else 
        { 
            Spinner.IsActive = false; 
            Spinner.Visibility = Visibility.Collapsed; 
        }

        if (!string.IsNullOrEmpty(imagePath) && System.IO.File.Exists(imagePath))
        {
            try { BackgroundImage.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(imagePath)); } catch { }
        }

        // 设置 Rectangle 的整体透明度，通过叠加混合模拟从 0% (完全原图) 到 100% (标准锁屏模糊) 的跨度
        BlurOverlay.Opacity = blurOpacity;

        // 清除原本会变暗的假模糊背景
        BlurOverlay.Fill = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);

        // 引入真正的 Composition API 与 Win2D 进行硬件级高斯模糊
        var compositor = ElementCompositionPreview.GetElementVisual(BlurOverlay).Compositor;
        var backdropBrush = compositor.CreateBackdropBrush();

        var blurEffect = new GaussianBlurEffect
        {
            Name = "Blur",
            BlurAmount = 80.0f, // 大幅提升模糊强度，对标 Windows 锁屏界面的深度模糊
            BorderMode = EffectBorderMode.Hard,
            Source = new CompositionEffectSourceParameter("backdrop")
        };

        var tintEffect = new ColorSourceEffect
        {
            Name = "Tint",
            // 叠加一层微弱的纯黑遮罩 (Alpha = 45, 约 18% 不透明度)
            // 完美抵消纯高斯模糊造成的亮化/发白效果，使文字更加清晰
            Color = Windows.UI.Color.FromArgb(45, 0, 0, 0)
        };

        var compositeEffect = new CompositeEffect
        {
            Mode = Microsoft.Graphics.Canvas.CanvasComposite.SourceOver,
            Sources = { blurEffect, tintEffect }
        };

        var effectFactory = compositor.CreateEffectFactory(compositeEffect);
        var effectBrush = effectFactory.CreateBrush();
        effectBrush.SetSourceParameter("backdrop", backdropBrush);

        var spriteVisual = compositor.CreateSpriteVisual();
        spriteVisual.Brush = effectBrush;

        // 使用表达式动画将模糊遮罩的大小与 BlurOverlay 矩形实时绑定
        var expression = compositor.CreateExpressionAnimation("visual.Size");
        expression.SetReferenceParameter("visual", ElementCompositionPreview.GetElementVisual(BlurOverlay));
        spriteVisual.StartAnimation("Size", expression);

        // 将真正的硬件高斯模糊 Visual 挂载到 BlurOverlay 内部
        ElementCompositionPreview.SetElementChildVisual(BlurOverlay, spriteVisual);
    }

    // 核心特效：水滴坠落与系统级淡出联动动画
    public async Task DismissAsync()
    {
        // 核心体验优化：如果幕布前的程序启动极快（或者用户在锁屏停留了一会儿）
        // 强制让幕布至少保持展示 1.8 秒。这样转圈动画能有个短暂的播放过程，
        // 避免幕布一拉起就闪退或动画错乱，保证水滴退场动画的从容与优雅。
        var elapsed = DateTime.UtcNow - _showTime;
        var minDisplayTime = TimeSpan.FromSeconds(1.8);
        if (elapsed < minDisplayTime)
        {
            await Task.Delay(minDisplayTime - elapsed);
        }

        if (_enableAnimation)
        {
            var storyboard = new Storyboard();

            // 1. 水滴坠落：文字和转圈迅速缩小并消失（0.3秒）
            var contentFade = new DoubleAnimation { To = 0, Duration = TimeSpan.FromSeconds(0.3) };
            Storyboard.SetTarget(contentFade, ContentPanel);
            Storyboard.SetTargetProperty(contentFade, "Opacity");

            var contentScaleX = new DoubleAnimation { To = 0.5, Duration = TimeSpan.FromSeconds(0.3), EasingFunction = new BackEase { EasingMode = EasingMode.EaseIn } };
            Storyboard.SetTarget(contentScaleX, ContentScale);
            Storyboard.SetTargetProperty(contentScaleX, "ScaleX");
            
            var contentScaleY = new DoubleAnimation { To = 0.5, Duration = TimeSpan.FromSeconds(0.3), EasingFunction = new BackEase { EasingMode = EasingMode.EaseIn } };
            Storyboard.SetTarget(contentScaleY, ContentScale);
            Storyboard.SetTargetProperty(contentScaleY, "ScaleY");

            // 2. 涟漪爆开：波纹扩散距离略微收敛，透明度下降更快，避免像雷达扫描
            var rippleScaleX = new DoubleAnimation { From = 0, To = 35, Duration = TimeSpan.FromSeconds(0.8), EasingFunction = new CircleEase { EasingMode = EasingMode.EaseOut } };
            Storyboard.SetTarget(rippleScaleX, RippleScale);
            Storyboard.SetTargetProperty(rippleScaleX, "ScaleX");

            var rippleScaleY = new DoubleAnimation { From = 0, To = 35, Duration = TimeSpan.FromSeconds(0.8), EasingFunction = new CircleEase { EasingMode = EasingMode.EaseOut } };
            Storyboard.SetTarget(rippleScaleY, RippleScale);
            Storyboard.SetTargetProperty(rippleScaleY, "ScaleY");

            // 因为起始折射光已经非常微弱，直接从透明度 1 开始线性降低到 0 即可
            var rippleFade = new DoubleAnimation { From = 1.0, To = 0, Duration = TimeSpan.FromSeconds(0.7) };
            Storyboard.SetTarget(rippleFade, Ripple);
            Storyboard.SetTargetProperty(rippleFade, "Opacity");

            // 第二层跟随波纹
            var ripple2ScaleX = new DoubleAnimation { From = 0, To = 25, Duration = TimeSpan.FromSeconds(0.9), EasingFunction = new CircleEase { EasingMode = EasingMode.EaseOut } };
            Storyboard.SetTarget(ripple2ScaleX, RippleScale2);
            Storyboard.SetTargetProperty(ripple2ScaleX, "ScaleX");

            var ripple2ScaleY = new DoubleAnimation { From = 0, To = 25, Duration = TimeSpan.FromSeconds(0.9), EasingFunction = new CircleEase { EasingMode = EasingMode.EaseOut } };
            Storyboard.SetTarget(ripple2ScaleY, RippleScale2);
            Storyboard.SetTargetProperty(ripple2ScaleY, "ScaleY");

            var ripple2Fade = new DoubleAnimation { From = 1.0, To = 0, Duration = TimeSpan.FromSeconds(0.8) };
            Storyboard.SetTarget(ripple2Fade, Ripple2);
            Storyboard.SetTargetProperty(ripple2Fade, "Opacity");

            // 3. 幕布扭曲：动用 ElasticEase (物理弹簧缓动)，增加放大系数，模拟水面因表面张力产生“果冻般的抖动扭曲”
            var rootScaleX = new DoubleAnimation { To = 1.15, Duration = TimeSpan.FromSeconds(1.2), EasingFunction = new ElasticEase { EasingMode = EasingMode.EaseOut, Oscillations = 2, Springiness = 4 } };
            Storyboard.SetTarget(rootScaleX, RootScale);
            Storyboard.SetTargetProperty(rootScaleX, "ScaleX");

            var rootScaleY = new DoubleAnimation { To = 1.15, Duration = TimeSpan.FromSeconds(1.2), EasingFunction = new ElasticEase { EasingMode = EasingMode.EaseOut, Oscillations = 2, Springiness = 4 } };
            Storyboard.SetTarget(rootScaleY, RootScale);
            Storyboard.SetTargetProperty(rootScaleY, "ScaleY");

            // [核心修复]：伴随涟漪爆开，毛玻璃遮罩层平滑消散，完美洗刷掉模糊并露出底层明亮的清晰图片（解决到底层透明度转换时突然变亮的割裂感）
            var blurFadeOut = new DoubleAnimation { To = 0, Duration = TimeSpan.FromSeconds(0.6) };
            Storyboard.SetTarget(blurFadeOut, BlurOverlay);
            Storyboard.SetTargetProperty(blurFadeOut, "Opacity");

            // 错开时间：涟漪在水滴落入水面 (0.3s) 时爆开
            rippleScaleX.BeginTime = TimeSpan.FromSeconds(0.3);
            rippleScaleY.BeginTime = TimeSpan.FromSeconds(0.3);
            rippleFade.BeginTime = TimeSpan.FromSeconds(0.3);
            
            // 稍微延迟0.08秒爆开第二圈
            ripple2ScaleX.BeginTime = TimeSpan.FromSeconds(0.38); 
            ripple2ScaleY.BeginTime = TimeSpan.FromSeconds(0.38);
            ripple2Fade.BeginTime = TimeSpan.FromSeconds(0.38);

            blurFadeOut.BeginTime = TimeSpan.FromSeconds(0.3);

            rootScaleX.BeginTime = TimeSpan.FromSeconds(0.3);
            rootScaleY.BeginTime = TimeSpan.FromSeconds(0.3);

            storyboard.Children.Add(contentFade);
            storyboard.Children.Add(contentScaleX);
            storyboard.Children.Add(contentScaleY);
            storyboard.Children.Add(rippleScaleX);
            storyboard.Children.Add(rippleScaleY);
            storyboard.Children.Add(rippleFade);
            
            storyboard.Children.Add(ripple2ScaleX);
            storyboard.Children.Add(ripple2ScaleY);
            storyboard.Children.Add(ripple2Fade);

            storyboard.Children.Add(rootScaleX);
            storyboard.Children.Add(rootScaleY);

            storyboard.Begin();

            // 稍微延长等待，让剧烈的扭曲充分展示后再执行系统级淡出露出桌面 (400毫秒)
            await Task.Delay(400);
        }

        // 4. 彻底揭开：动用 Win32 底层接口让当前黑色/毛玻璃全屏窗口在 600ms 内平滑淡化透明，露出 Windows 桌面！
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        IntPtr initialStyle = GetWindowLongPtr(hwnd, GWL_EXSTYLE);
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, (IntPtr)((long)initialStyle | WS_EX_LAYERED));

        for (int i = 30; i >= 0; i--)
        {
            byte alpha = (byte)(255 * i / 30);
            SetLayeredWindowAttributes(hwnd, 0, alpha, LWA_ALPHA);
            await Task.Delay(20); // 30次 * 20ms = 600ms
        }

        // [核心修复]：在 WinUI 3 框架中，如果代码中调用了 Close() 且这是当前进程唯一一个窗口，
        // 整个后台进程就会被立刻强制杀死。这就导致了 Orchestrator 会直接中断，后面的 T4、T5 永远不会启动。
        // 改用 AppWindow.Hide() 可以完美隐藏幕布，同时保持管家进程存活，继续执行后续的调度引擎。
        this.AppWindow.Hide();
    }
}