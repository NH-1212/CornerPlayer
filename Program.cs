// CornerPlayer: 画面隅に表示する表示専用メディアプレイヤー（マウス入力を一切受け付けない）
// ホットキー: Ctrl+Alt+P 再生/一時停止, Ctrl+Alt+←/→ 前/次, Ctrl+Alt+H 表示切替
// 制限: 排他的フルスクリーンのゲームには表示不可（ボーダーレス/ウィンドウモードで使用）
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.Media.Control;
using Windows.Storage.Streams;
using WF = System.Windows.Forms;
using Drawing = System.Drawing;

sealed class Config
{
    public string Corner { get; set; } = "BottomRight"; // TopLeft/TopRight/BottomLeft/BottomRight/Custom
    public double X { get; set; }
    public double Y { get; set; }
    public double Margin { get; set; } = 12;
    public double Opacity { get; set; } = 0.9;

    static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CornerPlayer", "config.json");

    public static Config Load()
    {
        try { return JsonSerializer.Deserialize<Config>(File.ReadAllText(FilePath)) ?? new Config(); }
        catch { return new Config(); }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}

// 幅に収まらない場合のみ横スクロールするテキスト
sealed class MarqueeText : Border
{
    readonly TextBlock tb;
    readonly TranslateTransform tt = new TranslateTransform();
    string text = "";

    public MarqueeText(double fontSize, Brush fg, FontWeight weight)
    {
        ClipToBounds = true;
        tb = new TextBlock
        {
            FontSize = fontSize,
            Foreground = fg,
            FontWeight = weight,
            TextWrapping = TextWrapping.NoWrap,
            RenderTransform = tt
        };
        var cv = new Canvas { ClipToBounds = true };
        cv.Children.Add(tb);
        Child = cv;

        tb.Text = "Ag";
        tb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Height = tb.DesiredSize.Height;
        tb.Text = "";

        SizeChanged += (_, e) => { if (e.WidthChanged) Restart(); };
    }

    public string Text
    {
        get { return text; }
        set
        {
            value = value ?? "";
            if (value == text) return;
            text = value;
            tb.Text = value;
            Restart();
        }
    }

    void Restart()
    {
        tt.BeginAnimation(TranslateTransform.XProperty, null);
        tt.X = 0;
        tb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double cw = ActualWidth;
        double tw = tb.DesiredSize.Width;
        if (cw <= 0 || tw <= cw) return;

        double over = tw - cw + 8;
        double move = over / 40.0; // 40px/秒
        var a = new DoubleAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever };
        a.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        a.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(1.5))));
        a.KeyFrames.Add(new LinearDoubleKeyFrame(-over, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(1.5 + move))));
        a.KeyFrames.Add(new LinearDoubleKeyFrame(-over, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(3.0 + move))));
        Timeline.SetDesiredFrameRate(a, 30);
        tt.BeginAnimation(TranslateTransform.XProperty, a);
    }
}

sealed class Overlay : Window
{
    const int GWL_EXSTYLE = -20;
    const int WS_EX_TRANSPARENT = 0x00000020;
    const int WS_EX_TOOLWINDOW = 0x00000080;
    const int WS_EX_LAYERED = 0x00080000;
    const int WS_EX_NOACTIVATE = 0x08000000;
    const int WM_MOUSEACTIVATE = 0x0021;
    const int WM_NCHITTEST = 0x0084;
    const int WM_HOTKEY = 0x0312;
    const int MA_NOACTIVATE = 3;
    const int HTTRANSPARENT = -1;
    const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOACTIVATE = 0x0010;
    const uint GW_HWNDPREV = 3;
    const int SW_HIDE = 0, SW_SHOWNOACTIVATE = 4;
    const uint MOD_ALT = 0x0001, MOD_CONTROL = 0x0002, MOD_NOREPEAT = 0x4000;
    static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);

    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int i);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h, int i, int v);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr h, int id, uint mod, uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr h, int id);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h, uint cmd);

    const double ArtSize = 44;

    readonly Config cfg;
    IntPtr hwnd;
    bool shown = true;
    bool busy;
    int tick;
    volatile bool dirty = true;
    string thumbKey;
    bool hasThumb;

    GlobalSystemMediaTransportControlsSessionManager mgr;
    GlobalSystemMediaTransportControlsSession session;
    DispatcherTimer timer, topTimer;

    MarqueeText titleM, artistM;
    TextBlock timeTb;
    ProgressBar bar;
    Image art;

    public Overlay(Config c)
    {
        cfg = c;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Focusable = false;
        IsHitTestVisible = false;
        IsTabStop = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.Height;
        Width = 240;
        Opacity = cfg.Opacity;
        Content = BuildUi();

        SourceInitialized += OnSourceInit;
        SizeChanged += (_, _) => ApplyPosition();
        Loaded += async (_, _) => { try { await InitMedia(); } catch { } };
        Closed += OnClosedHandler;
    }

    UIElement BuildUi()
    {
        var gray = new SolidColorBrush(Color.FromRgb(0xB0, 0xB0, 0xB0));

        // ジャケット
        var artGrid = new Grid
        {
            Width = ArtSize,
            Height = ArtSize,
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        artGrid.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(5),
            Background = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A))
        });
        artGrid.Children.Add(new TextBlock
        {
            Text = "\uE8D6",
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 18,
            Foreground = new SolidColorBrush(Color.FromRgb(0x70, 0x70, 0x70)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        });
        art = new Image
        {
            Stretch = Stretch.UniformToFill,
            Clip = new RectangleGeometry(new Rect(0, 0, ArtSize, ArtSize), 5, 5)
        };
        RenderOptions.SetBitmapScalingMode(art, BitmapScalingMode.HighQuality);
        artGrid.Children.Add(art);

        titleM = new MarqueeText(12, Brushes.White, FontWeights.SemiBold) { Text = "再生なし" };
        artistM = new MarqueeText(10, gray, FontWeights.Normal);
        bar = new ProgressBar
        {
            Height = 3,
            Minimum = 0,
            Maximum = 1,
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44)),
            Foreground = Brushes.White,
            Margin = new Thickness(0, 5, 0, 3),
            Focusable = false
        };
        timeTb = new TextBlock { Text = "--:-- / --:--", Foreground = gray, FontSize = 10 };

        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(titleM);
        texts.Children.Add(artistM);
        texts.Children.Add(bar);
        texts.Children.Add(timeTb);

        var main = new Grid();
        main.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(artGrid, 0);
        Grid.SetColumn(texts, 1);
        main.Children.Add(artGrid);
        main.Children.Add(texts);

        return new Border
        {
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Color.FromArgb(0xD0, 0x18, 0x18, 0x18)),
            Padding = new Thickness(8, 6, 8, 6),
            IsHitTestVisible = false,
            Child = main
        };
    }

    async Task Act(Func<GlobalSystemMediaTransportControlsSession, Task> f)
    {
        var s = session;
        if (s == null) return;
        try { await f(s); } catch { }
    }

    void OnSourceInit(object sender, EventArgs e)
    {
        hwnd = new WindowInteropHelper(this).Handle;

        // 非アクティブ + 常時クリック透過 + タスクバー/Alt+Tab非表示
        int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, ex | WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
        HwndSource.FromHwnd(hwnd).AddHook(WndProc);

        uint mod = MOD_CONTROL | MOD_ALT | MOD_NOREPEAT;
        RegisterHotKey(hwnd, 1, mod, 0x50); // P
        RegisterHotKey(hwnd, 2, mod, 0x25); // Left
        RegisterHotKey(hwnd, 3, mod, 0x27); // Right
        RegisterHotKey(hwnd, 4, mod, 0x48); // H

        topTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        topTimer.Tick += (_, _) =>
        {
            // 自分より前面にウィンドウがある場合のみ最前面を再設定（不要なZオーダー変更を避ける）
            if (shown && GetWindow(hwnd, GW_HWNDPREV) != IntPtr.Zero)
                SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        };
        topTimer.Start();
    }

    IntPtr WndProc(IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled)
    {
        switch (msg)
        {
            case WM_MOUSEACTIVATE:
                handled = true;
                return new IntPtr(MA_NOACTIVATE);
            case WM_NCHITTEST:
                handled = true;
                return new IntPtr(HTTRANSPARENT);
            case WM_HOTKEY:
                switch (w.ToInt32())
                {
                    case 1: _ = Act(s => s.TryTogglePlayPauseAsync().AsTask()); break;
                    case 2: _ = Act(s => s.TrySkipPreviousAsync().AsTask()); break;
                    case 3: _ = Act(s => s.TrySkipNextAsync().AsTask()); break;
                    case 4: ToggleVisible(); break;
                }
                handled = true;
                return IntPtr.Zero;
        }
        return IntPtr.Zero;
    }

    void ApplyPosition()
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        double sw = SystemParameters.PrimaryScreenWidth, sh = SystemParameters.PrimaryScreenHeight;
        double m = cfg.Margin;
        switch (cfg.Corner)
        {
            case "TopLeft": Left = m; Top = m; break;
            case "TopRight": Left = sw - ActualWidth - m; Top = m; break;
            case "BottomLeft": Left = m; Top = sh - ActualHeight - m; break;
            case "BottomRight": Left = sw - ActualWidth - m; Top = sh - ActualHeight - m; break;
            default:
                double vl = SystemParameters.VirtualScreenLeft, vt = SystemParameters.VirtualScreenTop;
                double vw = SystemParameters.VirtualScreenWidth, vh = SystemParameters.VirtualScreenHeight;
                Left = Math.Min(Math.Max(cfg.X, vl), vl + vw - ActualWidth);
                Top = Math.Min(Math.Max(cfg.Y, vt), vt + vh - ActualHeight);
                break;
        }
    }

    public void SetCorner(string key)
    {
        cfg.Corner = key;
        cfg.Save();
        ApplyPosition();
    }

    public void SetOpacity(double v)
    {
        cfg.Opacity = v;
        Opacity = v;
        cfg.Save();
    }

    public void ToggleVisible()
    {
        shown = !shown;
        ShowWindow(hwnd, shown ? SW_SHOWNOACTIVATE : SW_HIDE);
    }

    async Task InitMedia()
    {
        mgr = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        mgr.CurrentSessionChanged += (_, _) => dirty = true;
        timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        timer.Tick += async (_, _) => await Refresh();
        timer.Start();
        await Refresh();
    }

    void OnPropsChanged(GlobalSystemMediaTransportControlsSession s, MediaPropertiesChangedEventArgs a)
    {
        dirty = true;
    }

    static async Task<BitmapImage> LoadThumb(IRandomAccessStreamReference r)
    {
        if (r == null) return null;
        try
        {
            using (var ras = await r.OpenReadAsync())
            using (var s = ras.AsStreamForRead())
            {
                var ms = new MemoryStream();
                await s.CopyToAsync(ms);
                if (ms.Length == 0) return null;
                ms.Position = 0;
                var bi = new BitmapImage();
                bi.BeginInit();
                bi.CacheOption = BitmapCacheOption.OnLoad;
                bi.DecodePixelWidth = 128;
                bi.StreamSource = ms;
                bi.EndInit();
                bi.Freeze();
                return bi;
            }
        }
        catch { return null; }
    }

    async Task Refresh()
    {
        if (busy || mgr == null) return;
        busy = true;
        try
        {
            var cur = mgr.GetCurrentSession();
            if (!string.Equals(cur?.SourceAppUserModelId, session?.SourceAppUserModelId))
            {
                if (session != null) session.MediaPropertiesChanged -= OnPropsChanged;
                session = cur;
                if (session != null) session.MediaPropertiesChanged += OnPropsChanged;
                thumbKey = null;
                hasThumb = false;
                dirty = true;
            }

            if (session == null)
            {
                SetIdle();
                return;
            }

            if (++tick % 10 == 0) dirty = true;
            if (dirty)
            {
                dirty = false;
                var p = await session.TryGetMediaPropertiesAsync();
                titleM.Text = string.IsNullOrWhiteSpace(p.Title) ? "(タイトル不明)" : p.Title;
                artistM.Text = p.Artist ?? "";

                string key = p.Title + "|" + p.Artist + "|" + p.AlbumTitle;
                if (key != thumbKey || (!hasThumb && p.Thumbnail != null))
                {
                    thumbKey = key;
                    var bi = await LoadThumb(p.Thumbnail);
                    art.Source = bi;
                    hasThumb = bi != null;
                }
            }

            var info = session.GetPlaybackInfo();
            var tl = session.GetTimelineProperties();
            bool playing = info.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

            var pos = tl.Position;
            if (playing)
            {
                var el = DateTimeOffset.Now - tl.LastUpdatedTime;
                if (el > TimeSpan.Zero)
                    pos += TimeSpan.FromSeconds(el.TotalSeconds * (info.PlaybackRate ?? 1.0));
            }

            var dur = tl.EndTime - tl.StartTime;
            var rel = pos - tl.StartTime;
            if (rel < TimeSpan.Zero) rel = TimeSpan.Zero;

            if (dur > TimeSpan.Zero)
            {
                if (rel > dur) rel = dur;
                timeTb.Text = Fmt(rel) + " / " + Fmt(dur);
                bar.Value = rel.TotalSeconds / dur.TotalSeconds;
            }
            else
            {
                timeTb.Text = Fmt(rel) + " / --:--";
                bar.Value = 0;
            }
        }
        catch { }
        finally { busy = false; }
    }

    void SetIdle()
    {
        titleM.Text = "再生なし";
        artistM.Text = "";
        timeTb.Text = "--:-- / --:--";
        bar.Value = 0;
        art.Source = null;
        thumbKey = null;
        hasThumb = false;
    }

    static string Fmt(TimeSpan t)
    {
        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
            : $"{t.Minutes:00}:{t.Seconds:00}";
    }

    void OnClosedHandler(object sender, EventArgs e)
    {
        timer?.Stop();
        topTimer?.Stop();
        for (int i = 1; i <= 4; i++) UnregisterHotKey(hwnd, i);
    }
}

static class Program
{
    static Drawing.Icon LoadIcon()
    {
        try
        {
            using (var st = typeof(Program).Assembly.GetManifestResourceStream("icon.ico"))
            {
                if (st != null) return new Drawing.Icon(st, WF.SystemInformation.SmallIconSize);
            }
        }
        catch { }
        return Drawing.SystemIcons.Application;
    }

    [STAThread]
    static void Main()
    {
        using var mutex = new Mutex(true, "CornerPlayer_SingleInstance", out bool created);
        if (!created) return;

        var cfg = Config.Load();
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var win = new Overlay(cfg);

        var tray = new WF.NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "CornerPlayer",
            Visible = true
        };
        var menu = new WF.ContextMenuStrip();

        menu.Items.Add("左上", null, (_, _) => win.SetCorner("TopLeft"));
        menu.Items.Add("右上", null, (_, _) => win.SetCorner("TopRight"));
        menu.Items.Add("左下", null, (_, _) => win.SetCorner("BottomLeft"));
        menu.Items.Add("右下", null, (_, _) => win.SetCorner("BottomRight"));
        menu.Items.Add(new WF.ToolStripSeparator());

        var op = new WF.ToolStripMenuItem("不透明度");
        foreach (var v in new[] { 100, 80, 60, 40 })
            op.DropDownItems.Add(v + "%", null, (_, _) => win.SetOpacity(v / 100.0));
        menu.Items.Add(op);

        menu.Items.Add("表示/非表示 (Ctrl+Alt+H)", null, (_, _) => win.ToggleVisible());
        menu.Items.Add(new WF.ToolStripSeparator());
        menu.Items.Add("終了", null, (_, _) =>
        {
            tray.Visible = false;
            app.Shutdown();
        });

        tray.ContextMenuStrip = menu;
        tray.MouseClick += (_, e) =>
        {
            if (e.Button == WF.MouseButtons.Left) win.ToggleVisible();
        };

        win.Show();
        app.Run();

        tray.Visible = false;
        tray.Dispose();
    }
}
