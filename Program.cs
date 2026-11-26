using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace CoolCharge;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        if (args.Contains("--selftest")) return SelfTest.Run();

        double? simulate = null;
        int i = Array.IndexOf(args, "--simulate");
        if (i >= 0)
        {
            if (i + 1 >= args.Length ||
                !double.TryParse(args[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out double t) || !double.IsFinite(t))
            {
                MessageBox.Show("Usage: CoolCharge.exe [--selftest] [--simulate <tempC>]", "CoolCharge");
                return 2;
            }
            simulate = t;
        }

        Mutex mutex;
        bool first;
        // An elevated instance owns the mutex with an admin-only ACL, so a non-elevated second start can't even open it.
        try { mutex = new Mutex(true, "CoolCharge.SingleInstance", out first); }
        catch (UnauthorizedAccessException) { return 0; }
        using var _ = mutex;
        if (!first) return 0;

        // Without this an exception in a timer tick shows the stock crash dialog, and "Quit" there would leave a cap on.
        Application.ThreadException += (_, e) => TrayApp.Log("ERROR " + e.Exception);
        ApplicationConfiguration.Initialize();
        using var app = new TrayApp(simulate);
        Application.Run(app);
        return 0;
    }
}

sealed class TrayApp : ApplicationContext
{
    const string Title = "CoolCharge";
    const string CapLabel = "Turn Turbo Boost off";
    const int PollMs = 5000;
    static readonly TimeSpan Snooze = TimeSpan.FromMinutes(15);

    readonly NotifyIcon _tray = new() { Visible = true };
    readonly System.Windows.Forms.Timer _timer = new() { Interval = PollMs };
    readonly Sensors _sensors = new();
    readonly ThermalMonitor _monitor = new();
    readonly Stopwatch _sinceTick = Stopwatch.StartNew();
    // Invisible always-on-top owner so prompts appear above full-screen video etc.
    readonly Form _owner = new() { TopMost = true, ShowInTaskbar = false, FormBorderStyle = FormBorderStyle.None,
                                   Opacity = 0, Size = new Size(1, 1), StartPosition = FormStartPosition.CenterScreen };
    readonly ToolStripMenuItem _status = new("Starting…") { Enabled = false };
    readonly ToolStripMenuItem _restoreItem = new("Restore full speed");

    double? _simulate;
    static readonly bool Admin = IsAdmin();
    DateTime _heatPromptDue = DateTime.MaxValue;
    bool _offerRestore, _prompting, _exiting;
    bool _capped; // cached so the 5 s tick doesn't touch the disk
    IntPtr _hIcon;
    string _iconKey = "";

    public TrayApp(double? simulate)
    {
        _simulate = simulate;

        var menu = new ContextMenuStrip();
        menu.Items.Add(_status);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(CapLabel, null, (_, _) => ApplyCap());
        _restoreItem.Click += (_, _) => RestoreCap();
        menu.Items.Add(_restoreItem);
        menu.Items.Add("Show top processes", null, (_, _) => ShowTopProcesses());
        if (simulate is not null)
        {
            menu.Items.Add(new ToolStripSeparator());
            foreach (double t in new[] { 70.0, 87.0, 94.0 })
                menu.Items.Add($"Simulate {t:0} °C", null, (_, _) => { _simulate = t; Log($"simulate {t:0}"); });
        }
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitThread());
        menu.Opening += (_, _) => _restoreItem.Enabled = Throttle.IsCapped;
        _tray.ContextMenuStrip = menu;

        Log($"start (admin={IsAdmin()}, simulate={simulate?.ToString(CultureInfo.InvariantCulture) ?? "off"}, capped={Throttle.IsCapped})");
        if (simulate is null && !Admin)
            _tray.ShowBalloonTip(10000, Title, "Not running as administrator, so the CPU temperature can't be read.", ToolTipIcon.Warning);

        // A cap left by a crash, kill or shutdown must be visible at once, even if the sensor never reads.
        _capped = Throttle.IsCapped;
        if (_capped)
            _tray.ShowBalloonTip(15000, Title, "Turbo Boost is still off from an earlier run. Right-click this icon and choose \"Restore full speed\" to undo it.", ToolTipIcon.Warning);
        // Logoff and shutdown skip ExitThreadCore, so restore here too.
        Microsoft.Win32.SystemEvents.SessionEnding += (_, _) => Throttle.Restore();

        _timer.Tick += (_, _) => Tick();
        _timer.Start();
        Tick();
    }

    void Tick()
    {
        double elapsed = _sinceTick.Elapsed.TotalSeconds;
        _sinceTick.Restart();
        // A long gap means sleep/resume: old samples say nothing about now, and must not satisfy the cool-down hold.
        if (elapsed > 30) StopJudging();

        double? temp = _simulate ?? _sensors.CpuTempC();
        bool pluggedIn = SystemInformation.PowerStatus.PowerLineStatus == PowerLineStatus.Online;
        string cap = _capped ? " · Turbo Boost off" : "";

        if (temp is not double t)
        {
            if (_iconKey != "–Gray") Log("no CPU reading");
            StopJudging();
            SetStatus((Admin ? "No CPU reading (is the PawnIO driver installed?)" : "No CPU reading (run as administrator)") + cap, "–", Color.Gray);
            return;
        }
        if (!pluggedIn)
        {
            // Only the plugged-in case loads the power bank, so nothing is judged or prompted on battery.
            StopJudging();
            SetStatus($"On battery · CPU {t:0} °C · not monitoring" + cap, $"{t:0}", Color.Gray);
            return;
        }

        var prev = _monitor.State;
        var state = _monitor.Update(t, elapsed);
        if (state != prev)
        {
            Log($"{prev} -> {state} (avg {_monitor.Average:0.0} °C)");
            // Entering Hot always asks, even if Warm was snoozed.
            if (state == ThermalState.Hot || prev == ThermalState.Normal) _heatPromptDue = DateTime.Now;
            if (state == ThermalState.Normal)
            {
                // Only a real cool-down (Warm held below 75 °C for 2 min) offers a restore; a Reset never gets here.
                _heatPromptDue = DateTime.MaxValue;
                _offerRestore = Throttle.IsCapped;
            }
        }

        double? watts = Sensors.ChargeWatts();
        string avg = _monitor.Average is double a ? $" (30 s avg {a:0})" : "";
        string charge = watts is double w ? $" · charging {w:0} W" : " · plugged in";
        SetStatus($"CPU {t:0} °C{avg}{charge}{cap}", $"{t:0}", state switch
        {
            ThermalState.Hot => Color.Firebrick,
            ThermalState.Warm => Color.DarkOrange,
            _ => Color.SeaGreen,
        });

        ShowDuePrompt();
    }

    /// At most one prompt at a time. A prompt runs its own message loop, so ticks keep arriving while it is open;
    /// they keep the state current and anything that became due is shown on the first tick after it closes.
    void ShowDuePrompt()
    {
        if (_prompting) return;
        _prompting = true;
        try
        {
            var state = _monitor.State;
            if (state != ThermalState.Normal && DateTime.Now >= _heatPromptDue) PromptHeat(state == ThermalState.Hot);
            else if (state == ThermalState.Normal && _offerRestore)
            {
                _offerRestore = false;
                if (Throttle.IsCapped) PromptRestore();
            }
        }
        finally { _prompting = false; }
    }

    /// Readings stopped (no sensor, on battery, sleep): drop the samples and any pending heat prompt.
    void StopJudging()
    {
        _monitor.Reset();
        _heatPromptDue = DateTime.MaxValue;
    }

    void PromptHeat(bool hot)
    {
        var cap = new TaskDialogButton(CapLabel);
        var top = new TaskDialogButton("Show top processes");
        var later = new TaskDialogButton("Snooze 15 min");

        var page = new TaskDialogPage
        {
            Caption = Title,
            Heading = $"CPU is {(hot ? "hot" : "running warm")}: {_monitor.Average:0} °C",
            Text = Throttle.IsCapped
                ? "30-second average while plugged in. Turbo Boost is already off; closing the busiest programs is what's left."
                : "30-second average while plugged in. Turning Turbo Boost off lowers heat and the power drawn from the charger or power bank.",
            Icon = hot ? TaskDialogIcon.Warning : TaskDialogIcon.Information,
            AllowCancel = true,
        };
        if (!Throttle.IsCapped) page.Buttons.Add(cap);
        page.Buttons.Add(top);
        page.Buttons.Add(later);
        // The prompt pops up over whatever is being typed; a stray Enter or Space must never change a setting.
        page.DefaultButton = later;

        var result = ShowDialog(page);
        Log($"{(hot ? "hot" : "warm")} prompt -> {result.Text ?? "closed"}");

        bool capped = result == cap && ApplyCap();
        if (result == top) ShowTopProcesses();
        // It may have gone from Warm to Hot while this prompt sat open; that Hot prompt is still owed.
        if (!hot && !capped && _monitor.State == ThermalState.Hot) return;
        // Ask again in 15 min while still warm or hot. After turning Turbo Boost off, only a new entry into Hot asks.
        _heatPromptDue = capped ? DateTime.MaxValue : DateTime.Now + Snooze;
    }

    void PromptRestore()
    {
        var restore = new TaskDialogButton("Restore full speed");
        var keep = new TaskDialogButton("Keep it off");
        var page = new TaskDialogPage
        {
            Caption = Title,
            Heading = $"Cooled down: {_monitor.Average:0} °C",
            Text = "Turbo Boost is still off.",
            Icon = TaskDialogIcon.Information,
            AllowCancel = true,
            Buttons = { restore, keep },
            DefaultButton = keep,
        };
        var result = ShowDialog(page);
        Log($"restore prompt -> {result.Text ?? "closed"}");
        if (result == restore) RestoreCap();
    }

    TaskDialogButton ShowDialog(TaskDialogPage page)
    {
        _owner.Show();
        try { return TaskDialog.ShowDialog(_owner, page, TaskDialogStartupLocation.CenterScreen); }
        finally { _owner.Hide(); }
    }

    bool ApplyCap()
    {
        if (_exiting) return false; // a prompt left open during Exit must not throttle again after the restore
        bool ok = Throttle.Cap();
        Log($"turbo off -> {(ok ? "ok" : "FAILED")}");
        if (ok)
        {
            _capped = true;
            _tray.ShowBalloonTip(5000, Title, "Turbo Boost is off while plugged in.", ToolTipIcon.Info);
        }
        else MessageBox.Show("Could not change the power setting (powercfg failed).", Title, MessageBoxButtons.OK, MessageBoxIcon.Error);
        return ok;
    }

    void RestoreCap()
    {
        bool ok = Throttle.Restore();
        Log($"restore -> {(ok ? "ok" : "FAILED")}");
        if (ok) { _capped = false; _tray.ShowBalloonTip(5000, Title, "Turbo Boost is back on (full speed).", ToolTipIcon.Info); }
        else MessageBox.Show("Could not restore the power setting (powercfg failed).", Title, MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    async void ShowTopProcesses()
    {
        var list = new ListView { View = View.Details, FullRowSelect = true, Dock = DockStyle.Fill, MultiSelect = false };
        list.Columns.Add("Process", 200);
        list.Columns.Add("PID", 70);
        list.Columns.Add("CPU %", 70);
        var refresh = new Button { Text = "Refresh", AutoSize = true };
        var end = new Button { Text = "End process…", AutoSize = true };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft };
        buttons.Controls.Add(end);
        buttons.Controls.Add(refresh);
        var form = new Form { Text = "Top CPU processes (2-second sample)", TopMost = true, Size = new Size(400, 260),
                              StartPosition = FormStartPosition.CenterScreen, MinimizeBox = false, MaximizeBox = false };
        form.Controls.Add(list);
        form.Controls.Add(buttons);

        async Task Load()
        {
            refresh.Enabled = end.Enabled = false;
            var top = await Task.Run(() => TopProcesses.Sample());
            if (form.IsDisposed) return;
            list.Items.Clear();
            foreach (var p in top)
                list.Items.Add(new ListViewItem(new[] { p.Name, p.Pid.ToString(), p.CpuPercent.ToString("0.0") }) { Tag = p.Pid });
            refresh.Enabled = end.Enabled = true;
        }

        refresh.Click += async (_, _) => await Load();
        end.Click += async (_, _) =>
        {
            if (list.SelectedItems.Count == 0) return;
            var item = list.SelectedItems[0];
            if (MessageBox.Show(form, $"End {item.Text} (PID {item.Tag})? Unsaved work in it will be lost.", Title,
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            try
            {
                using var p = Process.GetProcessById((int)item.Tag!);
                using var me = Process.GetCurrentProcess();
                // This app runs elevated, so refuse Windows' own processes (session 0): ending one can crash the machine.
                if (p.SessionId != me.SessionId)
                    MessageBox.Show(form, $"{item.Text} is a Windows system process and can't be ended from here.", Title);
                // The PID could have been reused since the sample; only kill if it is still the same program.
                else if (p.ProcessName == item.Text) { p.Kill(); Log($"ended {item.Text} ({item.Tag})"); }
            }
            catch (Exception ex) { MessageBox.Show(form, "Could not end it: " + ex.Message, Title); }
            await Load();
        };

        form.Show();
        await Load();
    }

    void SetStatus(string text, string iconText, Color color)
    {
        _status.Text = text;
        _tray.Text = text.Length > 127 ? text[..127] : text;

        string key = iconText + color.Name;
        if (key == _iconKey) return;
        _iconKey = key;
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        using (var brush = new SolidBrush(color))
        using (var font = new Font("Segoe UI", 17, FontStyle.Bold, GraphicsUnit.Pixel))
        using (var center = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
        {
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            g.FillRectangle(brush, 0, 0, 32, 32);
            g.DrawString(iconText, font, Brushes.White, new RectangleF(0, 0, 32, 32), center);
        }
        IntPtr h = bmp.GetHicon();
        _tray.Icon = Icon.FromHandle(h);
        if (_hIcon != IntPtr.Zero) DestroyIcon(_hIcon); // GetHicon handles are not freed by the GC
        _hIcon = h;
    }

    protected override void ExitThreadCore()
    {
        _timer.Stop();
        _exiting = true;
        if (Throttle.IsCapped)
        {
            bool ok = Throttle.Restore();
            Log($"exit: restore -> {(ok ? "ok" : "FAILED")}");
            if (!ok) MessageBox.Show("Could not restore the power setting; Turbo Boost is still off. Start the app again and choose \"Restore full speed\".", Title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        _tray.Visible = false;
        base.ExitThreadCore();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
            _tray.Dispose();
            _owner.Dispose();
            _sensors.Dispose();
            if (_hIcon != IntPtr.Zero) DestroyIcon(_hIcon);
        }
        base.Dispose(disposing);
    }

    static bool IsAdmin()
    {
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }

    // shortcut: the log only gets a line per state change or action and is never trimmed; add rotation if it ever grows.
    internal static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(Throttle.StateDir);
            string line = message.ReplaceLineEndings(" "); // process names and exception text must stay on one line
            File.AppendAllText(Path.Combine(Throttle.StateDir, "log.txt"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {line}{Environment.NewLine}");
        }
        catch { }
    }

    [DllImport("user32.dll")]
    static extern bool DestroyIcon(IntPtr handle);
}
