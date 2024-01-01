using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using PKTracker.App.Infrastructure;

namespace PKTracker.App.Hud;

/// <summary>
/// Окно-носитель для значка выбора раскладки над столом. Такое же layered-окно без рамки,
/// как панели игроков: иначе оно отбирало бы фокус у стола и мешало кликать по кнопкам рума (§5.2).
/// </summary>
public sealed class BadgeWindow : Window
{
    public IntPtr Handle { get; private set; }
    public int LastX { get; set; } = int.MinValue;
    public int LastY { get; set; } = int.MinValue;
    public ProfileBadge Badge { get; }

    public BadgeWindow(ProfileBadge badge)
    {
        Badge = badge;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        Focusable = false;
        Content = badge;
        SourceInitialized += (_, _) =>
        {
            Handle = new WindowInteropHelper(this).Handle;
            Win32.MakeOverlay(Handle);
        };
    }

    public (int W, int H) PixelSize(uint dpi) =>
        ((int)Math.Ceiling(ActualWidth * dpi / 96.0), (int)Math.Ceiling(ActualHeight * dpi / 96.0));
}
