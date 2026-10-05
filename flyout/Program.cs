// Tray flyout for the Claude Code usage dashboard (Windows).
//
// Sits in the notification area; a left-click pops the dashboard up above the
// taskbar corner and it dismisses itself when it loses focus, like the built-in
// volume / network flyouts. The dashboard itself is the unmodified Python server
// (`cli.py dashboard --no-browser`), spawned as a hidden child process and
// rendered in a WebView2.

using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Microsoft.Win32;

namespace ClaudeUsageFlyout;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        using var mutex = new Mutex(true, "ClaudeUsageFlyout.SingleInstance", out bool first);
        if (!first) return;

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayContext());
    }
}

internal sealed class TrayContext : ApplicationContext
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "ClaudeUsageFlyout";

    private readonly NotifyIcon _tray;
    private readonly DashboardServer _server;
    private readonly FlyoutForm _flyout;

    public TrayContext()
    {
        _server = new DashboardServer();
        _flyout = new FlyoutForm(_server);

        var startup = new ToolStripMenuItem("Start with Windows") { Checked = IsStartupEnabled() };
        startup.Click += (_, _) =>
        {
            SetStartup(!startup.Checked);
            startup.Checked = IsStartupEnabled();
        };

        _flyout.AutoHide = Settings.AutoHide;
        var autoHide = new ToolStripMenuItem("Hide when clicking elsewhere") { Checked = _flyout.AutoHide };
        autoHide.Click += (_, _) =>
        {
            _flyout.AutoHide = autoHide.Checked = !autoHide.Checked;
            Settings.AutoHide = _flyout.AutoHide;
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add("Open flyout", null, (_, _) => _flyout.ShowFlyout());
        menu.Items.Add("Open in browser", null, (_, _) =>
            Process.Start(new ProcessStartInfo(_server.Url) { UseShellExecute = true }));
        menu.Items.Add("Reset position and size", null, (_, _) => _flyout.ResetPosition());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(autoHide);
        menu.Items.Add(startup);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitThread());

        _tray = new NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "Claude Code Usage",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _tray.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) _flyout.Toggle();
        };

        string? error = _server.Start();
        if (error != null)
            _tray.ShowBalloonTip(8000, "Claude Usage", error, ToolTipIcon.Error);

        if (Environment.GetCommandLineArgs().Contains("--show")) _flyout.ShowFlyout();
    }

    protected override void ExitThreadCore()
    {
        _tray.Visible = false;
        _tray.Dispose();
        _flyout.Dispose();
        _server.Dispose();
        base.ExitThreadCore();
    }

    private static Icon LoadIcon()
    {
        using var stream = typeof(Program).Assembly.GetManifestResourceStream("icon.png")!;
        using var source = new Bitmap(stream);
        using var scaled = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(scaled))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            // Round the square artwork's corners so it sits well next to other tray icons.
            using var path = new GraphicsPath();
            const int r = 7, d = r * 2, s = 32;
            path.AddArc(0, 0, d, d, 180, 90);
            path.AddArc(s - d, 0, d, d, 270, 90);
            path.AddArc(s - d, s - d, d, d, 0, 90);
            path.AddArc(0, s - d, d, d, 90, 90);
            path.CloseFigure();
            g.SetClip(path);
            g.DrawImage(source, 0, 0, s, s);
        }
        return Icon.FromHandle(scaled.GetHicon());
    }

    private static bool IsStartupEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(RunValue) != null;
    }

    private static void SetStartup(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(RunValue, $"\"{Environment.ProcessPath}\"");
        else key.DeleteValue(RunValue, false);
    }
}

/// <summary>Per-user preferences, kept in HKCU.</summary>
internal static class Settings
{
    private const string Key = @"Software\ClaudeUsageFlyout";

    // Off by default: the flyout stays up until the tray icon is clicked again
    // (or Esc), so it can be read alongside other windows.
    public static bool AutoHide
    {
        get => Read("AutoHide") is int v && v != 0;
        set => Write("AutoHide", value ? 1 : 0);
    }

    /// <summary>Where the user last dragged the flyout; null means the default corner.</summary>
    public static Point? Position
    {
        get => Read("X") is int x && Read("Y") is int y ? new Point(x, y) : null;
        set => WritePair("X", "Y", value?.X, value?.Y);
    }

    /// <summary>The size the user last resized the flyout to, in pixels; null means the default.</summary>
    public static Size? Size
    {
        get => Read("Width") is int w && Read("Height") is int h ? new Size(w, h) : null;
        set => WritePair("Width", "Height", value?.Width, value?.Height);
    }

    private static object? Read(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(Key);
        return key?.GetValue(name);
    }

    private static void Write(string name, int value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(Key);
        key.SetValue(name, value, RegistryValueKind.DWord);
    }

    private static void WritePair(string nameA, string nameB, int? a, int? b)
    {
        using var key = Registry.CurrentUser.CreateSubKey(Key);
        if (a is int x && b is int y)
        {
            key.SetValue(nameA, x, RegistryValueKind.DWord);
            key.SetValue(nameB, y, RegistryValueKind.DWord);
        }
        else
        {
            key.DeleteValue(nameA, false);
            key.DeleteValue(nameB, false);
        }
    }
}

/// <summary>Owns the hidden <c>python cli.py dashboard</c> child process.</summary>
internal sealed class DashboardServer : IDisposable
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(2) };

    private Process? _process;

    // Fixed port: the dashboard keeps its UI state (collapsed sections, selected
    // range) in localStorage, which is keyed by origin — a random port per launch
    // would silently reset it. Override with CLAUDE_USAGE_FLYOUT_PORT.
    public int Port { get; } =
        int.TryParse(Environment.GetEnvironmentVariable("CLAUDE_USAGE_FLYOUT_PORT"), out int p) ? p : 47821;

    public string Url => $"http://127.0.0.1:{Port}/";

    /// <summary>Spawns the server. Returns an error message, or null on success.</summary>
    public string? Start()
    {
        string? root = FindRepoRoot();
        if (root == null)
            return "cli.py not found. Set CLAUDE_USAGE_HOME to the claude-usage folder.";

        var psi = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("CLAUDE_USAGE_PYTHON") ?? "python",
            WorkingDirectory = root,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string arg in new[] { "cli.py", "dashboard", "--no-browser", "--host", "127.0.0.1", "--port", Port.ToString() })
            psi.ArgumentList.Add(arg);

        try
        {
            _process = Process.Start(psi);
            return null;
        }
        catch (Exception ex)
        {
            return $"Could not start Python: {ex.Message}";
        }
    }

    public async Task<bool> WaitUntilReadyAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var response = await Http.GetAsync(Url + "icon.svg");
                return true;
            }
            catch (Exception)
            {
                await Task.Delay(250);
            }
        }
        return false;
    }

    public void Dispose()
    {
        try
        {
            if (_process is { HasExited: false }) _process.Kill(entireProcessTree: true);
        }
        catch (Exception)
        {
            // Already gone.
        }
        _process?.Dispose();
    }

    private static string? FindRepoRoot()
    {
        string? fromEnv = Environment.GetEnvironmentVariable("CLAUDE_USAGE_HOME");
        if (!string.IsNullOrEmpty(fromEnv) && File.Exists(Path.Combine(fromEnv, "cli.py")))
            return fromEnv;

        // Walk up from the exe (flyout/bin/<config>/<tfm>/) to the checkout root.
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "cli.py")) &&
                File.Exists(Path.Combine(dir.FullName, "dashboard.py")))
                return dir.FullName;
        }
        return null;
    }
}

internal sealed class FlyoutForm : Form
{
    private const int FlyoutWidth = 520;
    private const int FlyoutHeight = 760;
    private const int Margin_ = 12;
    private const int GripHeight = 18;
    private const int ResizeBorder = 5;
    private const int ResizeCorner = 18;

    private static readonly Color Background = Color.FromArgb(0x16, 0x16, 0x17);
    private static readonly Color GripColor = Color.FromArgb(0x6F, 0x6F, 0x70);

    private readonly DashboardServer _server;
    private readonly WebView2 _web;
    private readonly Panel _grip;
    private bool _loaded;
    private DateTime _hiddenAt = DateTime.MinValue;

    /// <summary>Dismiss the flyout when it loses focus, like the system flyouts.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool AutoHide { get; set; }

    public FlyoutForm(DashboardServer server)
    {
        _server = server;

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Background;
        Text = "Claude Code Usage";
        MinimumSize = new Size(340, 300);
        // The children fill the window, so leave a thin rim of the form itself
        // exposed for WndProc to turn into resize edges.
        Padding = new Padding(ResizeBorder);

        _web = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = Background };
        // The WebView2 swallows all mouse input, so the window is moved by a
        // slim native strip above it.
        _grip = new Panel { Dock = DockStyle.Top, Height = GripHeight, BackColor = Background, Cursor = Cursors.SizeAll };
        _grip.Paint += (_, e) =>
        {
            float scale = DeviceDpi / 96f;
            float w = 36 * scale, h = 4 * scale;
            var pill = new RectangleF((_grip.Width - w) / 2, (_grip.Height - h) / 2, w, h);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var path = new GraphicsPath();
            path.AddArc(pill.Left, pill.Top, h, h, 90, 180);
            path.AddArc(pill.Right - h, pill.Top, h, h, 270, 180);
            path.CloseFigure();
            using var brush = new SolidBrush(GripColor);
            e.Graphics.FillPath(brush, path);
        };
        // A resize only repaints the newly exposed part; redraw it all so the
        // handle stays centred.
        _grip.Resize += (_, _) => _grip.Invalidate();
        _grip.MouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Left) return;
            // Hand the drag to the system as if the strip were a title bar.
            ReleaseCapture();
            SendMessage(Handle, 0xA1 /* WM_NCLBUTTONDOWN */, 2 /* HTCAPTION */, 0);
        };
        Controls.Add(_web);
        Controls.Add(_grip);
        _web.BringToFront();

        // Force handle creation so the WebView2 can initialise and load the
        // dashboard while the flyout is still hidden — the first click then
        // opens instantly instead of flashing an empty window.
        _ = Handle;
        _ = _web.Handle;
        _ = InitializeAsync();
    }

    // Keep the borderless window out of Alt+Tab.
    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x80; // WS_EX_TOOLWINDOW
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // Windows 11 rounded corners (DWMWA_WINDOW_CORNER_PREFERENCE = DWMWCP_ROUND);
        // harmlessly fails on Windows 10.
        int round = 2;
        DwmSetWindowAttribute(Handle, 33, ref round, sizeof(int));
    }

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        // Borderless windows have no sizing frame; report the exposed rim as one.
        // Only the rim reaches here — the child controls cover everything else.
        if (m.Msg != 0x84 /* WM_NCHITTEST */ || m.Result != 1 /* HTCLIENT */) return;

        long lParam = m.LParam.ToInt64();
        Point pt = PointToClient(new Point((short)(lParam & 0xFFFF), (short)((lParam >> 16) & 0xFFFF)));
        float scale = DeviceDpi / 96f;
        int border = (int)(ResizeBorder * scale), corner = (int)(ResizeCorner * scale);
        bool left = pt.X < corner, right = pt.X >= Width - corner;
        bool top = pt.Y < corner, bottom = pt.Y >= Height - corner;

        int hit =
            top && left ? 13 /* HTTOPLEFT */ :
            top && right ? 14 /* HTTOPRIGHT */ :
            bottom && left ? 16 /* HTBOTTOMLEFT */ :
            bottom && right ? 17 /* HTBOTTOMRIGHT */ :
            pt.X < border ? 10 /* HTLEFT */ :
            pt.X >= Width - border ? 11 /* HTRIGHT */ :
            pt.Y < border ? 12 /* HTTOP */ :
            15 /* HTBOTTOM */;
        m.Result = hit;
    }

    protected override void OnResizeEnd(EventArgs e)
    {
        // Fires at the end of a drag as well as a resize.
        base.OnResizeEnd(e);
        Settings.Position = Location;
        Settings.Size = Size;
    }

    public void ResetPosition()
    {
        Settings.Position = null;
        Settings.Size = null;
        if (Visible) ShowFlyout();
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        if (AutoHide) HideFlyout();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Alt+F4 just dismisses; the app exits from the tray menu.
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            HideFlyout();
        }
        base.OnFormClosing(e);
    }

    public void Toggle()
    {
        // Clicking the tray icon while the flyout is open deactivates (hides) it
        // before the click arrives; without this guard it would reopen at once.
        if (Visible || DateTime.UtcNow - _hiddenAt < TimeSpan.FromMilliseconds(300)) HideFlyout();
        else ShowFlyout();
    }

    public void ShowFlyout()
    {
        float scale = DeviceDpi / 96f;
        int margin = (int)(Margin_ * scale);
        _grip.Height = (int)(GripHeight * scale);
        Padding = new Padding((int)(ResizeBorder * scale));

        // Reopen where the user left it, pulled back on-screen if that monitor
        // has since gone away; otherwise sit above the taskbar corner.
        Point? saved = Settings.Position;
        var area = saved is Point s
            ? Screen.FromPoint(s).WorkingArea
            : Screen.FromPoint(Cursor.Position).WorkingArea;
        Size size = Settings.Size ?? new Size((int)(FlyoutWidth * scale), (int)(FlyoutHeight * scale));
        int width = Math.Min(size.Width, area.Width - 2 * margin);
        int height = Math.Min(size.Height, area.Height - 2 * margin);
        Point location = saved is Point p
            ? new Point(Math.Clamp(p.X, area.Left, area.Right - width), Math.Clamp(p.Y, area.Top, area.Bottom - height))
            : new Point(area.Right - width - margin, area.Bottom - height - margin);
        Bounds = new Rectangle(location, new Size(width, height));

        Show();
        Activate();
        _web.Focus();

        // The server only scans once at startup; pick up usage since then so the
        // flyout is current every time it opens.
        if (_loaded)
            _ = _web.ExecuteScriptAsync(
                "fetch('/api/rescan', { method: 'POST' }).then(() => loadData()).catch(() => {});");
    }

    private void HideFlyout()
    {
        if (!Visible) return;
        _hiddenAt = DateTime.UtcNow;
        Hide();
    }

    private async Task InitializeAsync()
    {
        try
        {
            string dataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ClaudeUsageFlyout", "WebView2");
            var env = await CoreWebView2Environment.CreateAsync(null, dataDir);
            await _web.EnsureCoreWebView2Async(env);

            var core = _web.CoreWebView2;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            // Esc dismisses the flyout. Key events go to the WebView2, not the form.
            await core.AddScriptToExecuteOnDocumentCreatedAsync(
                "window.addEventListener('keydown', e => { if (e.key === 'Escape') chrome.webview.postMessage('close'); });");
            core.WebMessageReceived += (_, e) =>
            {
                if (e.TryGetWebMessageAsString() == "close") HideFlyout();
            };
            // External links (GitHub, pricing page) open in the default browser.
            core.NewWindowRequested += (_, e) =>
            {
                e.Handled = true;
                Process.Start(new ProcessStartInfo(e.Uri) { UseShellExecute = true });
            };
            core.NavigationCompleted += (_, e) => _loaded = e.IsSuccess;

            if (await _server.WaitUntilReadyAsync(TimeSpan.FromSeconds(30)))
                core.Navigate(_server.Url);
            else
                core.NavigateToString(StatusPage(
                    "The dashboard server did not start. Check that Python 3.8+ is on your PATH."));
        }
        catch (Exception ex)
        {
            MessageBox.Show($"WebView2 failed to initialise:\n{ex.Message}", "Claude Usage",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static string StatusPage(string message) =>
        "<body style=\"font-family:'Segoe UI',sans-serif;color:#BFBFBF;background:#161617;padding:24px\">" +
        $"<h1 style=\"font-size:18px\">Claude Code Usage</h1><p style=\"font-size:13px\">{message}</p></body>";

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
