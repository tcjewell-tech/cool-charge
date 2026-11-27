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
        // No UI: the installer runs this before removing the app, so a cap left by a killed instance is undone.
        if (args.Contains("--restore"))
        {
            bool ok = Throttle.Restore();
            TrayApp.Log($"--restore -> {(ok ? "ok" : "FAILED: " + Throttle.LastError)}");
            return ok ? 0 : 1;
        }

        double? simulate = null;
        int i = Array.IndexOf(args, "--simulate");
        if (i >= 0)
        {
            if (i + 1 >= args.Length ||
                !double.TryParse(args[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out double t) || !double.IsFinite(t))
            {
                MessageBox.Show("Usage: CoolCharge.exe [--selftest] [--restore] [--simulate <tempC>]", "CoolCharge");
                return 2;
            }
            simulate = t;
        }

        Mutex mutex;
        bool first;
        // An elevated instance owns the mutex with an admin-only ACL, so a non-elevated second start can't even open it.
        try { mutex = new Mutex(true, "CoolCharge.SingleInstance", out first); }
        catch (UnauthorizedAccessException) { TrayApp.Log("exit: already running elevated (single-instance mutex access denied)"); return 0; }
        using var _ = mutex;
        if (!first) { TrayApp.Log("exit: already running (single-instance mutex is owned)"); return 0; }

        // The runtime reads profiler variables before Main, so nothing here can undo them; the scheduled task sets
        // DOTNET_EnableDiagnostics=0 at launch. Started elevated any other way, at least leave a trace.
        if (Environment.IsPrivilegedProcess && Environment.GetEnvironmentVariable("DOTNET_EnableDiagnostics") != "0")
        {
            var set = Environment.GetEnvironmentVariables().Keys.Cast<string>().Where(k =>
                k.Equals("CORECLR_ENABLE_PROFILING", StringComparison.OrdinalIgnoreCase) ||
                k.StartsWith("CORECLR_PROFILER", StringComparison.OrdinalIgnoreCase) ||
                (k.StartsWith("DOTNET_", StringComparison.OrdinalIgnoreCase) && k.Contains("PROFILER", StringComparison.OrdinalIgnoreCase)) ||
                k.Equals("DOTNET_STARTUP_HOOKS", StringComparison.OrdinalIgnoreCase)).ToList();
            TrayApp.Log("WARNING started elevated outside the scheduled task (DOTNET_EnableDiagnostics is not 0), so .NET " +
                        "diagnostics/profiler variables were not locked out" + (set.Count > 0 ? "; set: " + string.Join(", ", set) : ""));
        }

        // Without this an exception in a timer tick shows the stock crash dialog, and "Quit" there would leave a cap on.
        Application.ThreadException += (_, e) => TrayApp.Log("ERROR " + e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => TrayApp.Log("FATAL " + e.ExceptionObject);
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
    // Never ended from here even if they run in the user's session: ending one can crash or log off the machine.
    static readonly string[] CriticalNames = { "csrss", "dwm", "winlogon", "lsass", "services", "smss", "wininit", "fontdrvhost" };

    readonly NotifyIcon _tray = new() { Visible = true };
    readonly System.Windows.Forms.Timer _timer = new() { Interval = PollMs };
    readonly Sensors _sensors = new();
    readonly ThermalMonitor _monitor = new();
    readonly PromptPolicy _policy = new();
    readonly Microsoft.Win32.SessionEndingEventHandler _sessionEnding;
    readonly Stopwatch _sinceTick = Stopwatch.StartNew();
    // Invisible always-on-top owner so prompts appear above full-screen video etc.
    readonly Form _owner = new() { TopMost = true, ShowInTaskbar = false, FormBorderStyle = FormBorderStyle.None,
                                   Opacity = 0, Size = new Size(1, 1), StartPosition = FormStartPosition.CenterScreen };
    readonly ToolStripMenuItem _status = new("Starting…") { Enabled = false };
    readonly ToolStripMenuItem _restoreItem = new("Restore full speed");

    double? _simulate;
    static readonly bool Admin = IsAdmin();
    bool _prompting, _exiting;
    bool? _pluggedIn; // last seen, to log changes
    volatile bool _capped; // cached so the 5 s tick doesn't touch the disk; SessionEnding writes it (usually re-entrantly on the UI thread, inside a dialog's loop)
    IntPtr _hIcon;
    string _iconKey = "";

    public TrayApp(double? simulate)
    {
        _simulate = simulate;

        var menu = new ContextMenuStrip();
        menu.Items.Add(_status);
        menu.Items.Add(new ToolStripSeparator());
        var capItem = menu.Items.Add(CapLabel, null, (_, _) => ApplyCap());
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
        // Only the action that can do something is clickable: off can't be turned off twice.
        menu.Opening += (_, _) => capItem.Enabled = !(_restoreItem.Enabled = Throttle.IsCapped);
        _tray.ContextMenuStrip = menu;

        Log($"start (admin={IsAdmin()}, simulate={simulate?.ToString(CultureInfo.InvariantCulture) ?? "off"}, capped={Throttle.IsCapped}, " +
            $"AC boost mode={Throttle.GetBoostMode()?.ToString() ?? "unreadable"})");
        if (simulate is null && !Admin)
            _tray.ShowBalloonTip(10000, Title, "Not running as administrator, so the CPU temperature can't be read.", ToolTipIcon.Warning);

        // A cap left by a crash, kill or shutdown must be visible at once, even if the sensor never reads.
        _capped = Throttle.IsCapped;
        if (_capped)
            _tray.ShowBalloonTip(15000, Title, "Turbo Boost is still off from an earlier run. Right-click this icon and choose \"Restore full speed\" to undo it.", ToolTipIcon.Warning);
        // Logoff and shutdown skip ExitThreadCore, so restore here too.
        _sessionEnding = (_, _) =>
        {
            bool ok = Throttle.Restore();
            Log($"session ending: restore -> {(ok ? "ok" : "FAILED: " + Throttle.LastError)}");
            if (ok) _capped = false;
        };
        Microsoft.Win32.SystemEvents.SessionEnding += _sessionEnding;

        _timer.Tick += (_, _) => Tick();
        _timer.Start();
        Tick();
    }

    void Tick()
    {
        double elapsed = _sinceTick.Elapsed.TotalSeconds;
        _sinceTick.Restart();
        // A long gap means sleep/resume: old samples say nothing about now, and must not satisfy the cool-down hold.
        if (elapsed > 30) { Log($"sleep/resume: {elapsed:0} s since the last reading, samples dropped"); StopJudging(); }

        double? temp = _simulate ?? _sensors.CpuTempC();
        bool pluggedIn = SystemInformation.PowerStatus.PowerLineStatus == PowerLineStatus.Online;
        if (pluggedIn != _pluggedIn) { Log(pluggedIn ? "plugged in" : "on battery"); _pluggedIn = pluggedIn; }
        string cap = !_capped ? "" : pluggedIn ? " · Turbo Boost off" : " · Turbo Boost off when plugged in";

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
            _policy.StateChanged(prev, state, Environment.TickCount64, _capped);
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
            switch (_policy.Next(_monitor.State, _monitor.Average, Environment.TickCount64, _capped))
            {
                case PromptPolicy.Prompt.Heat: PromptHeat(_monitor.State == ThermalState.Hot); break;
                case PromptPolicy.Prompt.Restore: PromptRestore(); break;
            }
        }
        finally { _prompting = false; }
    }

    /// Readings stopped (no sensor, on battery, sleep): drop the samples and any pending heat prompt.
    void StopJudging()
    {
        _monitor.Reset();
        _policy.ReadingsStopped(_capped);
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

        int episode = _policy.HeatShown();
        var result = ShowDialog(page);
        Log($"{(hot ? "hot" : "warm")} prompt -> {result.Text ?? "closed"}");

        if (result == cap && _policy.IsStale(episode))
        {
            // Ticks run while the prompt is open; the answer was to a CPU that is no longer warm or no longer read.
            Log("turbo off ignored: cooled down or readings stopped while the prompt was open");
            _tray.ShowBalloonTip(5000, Title, "Nothing changed: the CPU cooled down or readings stopped while the prompt was open.", ToolTipIcon.Info);
        }
        else if (result == cap) ApplyCap();
        if (result == top) ShowTopProcesses();
        _policy.HeatClosed(episode, hot, _monitor.State, Environment.TickCount64, _capped);
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
        Log($"turbo off -> {(ok ? "ok" : "FAILED: " + Throttle.LastError)}");
        _capped = Throttle.IsCapped; // a failed cap whose rollback failed too keeps the marker, so restore is still offered
        if (ok)
        {
            _policy.TurboOff(Environment.TickCount64); // from the prompt or the tray menu: no more Warm reminders
            // Only the plugged-in setting is changed, so on battery the message must not claim anything happened yet.
            bool onBattery = SystemInformation.PowerStatus.PowerLineStatus != PowerLineStatus.Online;
            _tray.ShowBalloonTip(5000, Title, onBattery
                ? "Turbo Boost will be off whenever the laptop is plugged in. Nothing changes on battery."
                : "Turbo Boost is now off while the laptop is plugged in.", ToolTipIcon.Info);
        }
        else MessageBox.Show("Could not change the power setting. The reason is in log.txt next to the program.", Title, MessageBoxButtons.OK, MessageBoxIcon.Error);
        return ok;
    }

    void RestoreCap()
    {
        bool ok = Throttle.Restore();
        Log($"restore -> {(ok ? "ok" : "FAILED: " + Throttle.LastError)}");
        if (ok) { _capped = false; _tray.ShowBalloonTip(5000, Title, "Turbo Boost is back on (full speed).", ToolTipIcon.Info); }
        else MessageBox.Show("Could not restore the power setting. The reason is in log.txt next to the program.", Title, MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    async void ShowTopProcesses()
    {
        var list = new ListView { View = View.Details, FullRowSelect = true, Dock = DockStyle.Fill, MultiSelect = false };
        // Pixel sizes are for 100 % scaling; the font scales by itself, these don't.
        list.Columns.Add("Process", list.LogicalToDeviceUnits(200));
        list.Columns.Add("PID", list.LogicalToDeviceUnits(70));
        list.Columns.Add("CPU %", list.LogicalToDeviceUnits(70));
        var refresh = new Button { Text = "Refresh", AutoSize = true };
        var end = new Button { Text = "End process…", AutoSize = true };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft };
        buttons.Controls.Add(end);
        buttons.Controls.Add(refresh);
        var form = new Form { Text = "Top CPU processes (2-second sample)", TopMost = true, Size = list.LogicalToDeviceUnits(new Size(400, 260)),
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
                // This app runs elevated, so refuse Windows' own processes (session 0, the critical ones in this
                // session, and any Windows marks critical; if that can't be read, refuse too): ending one can crash the machine.
                if (p.SessionId != me.SessionId || CriticalNames.Contains(p.ProcessName, StringComparer.OrdinalIgnoreCase) ||
                    !IsProcessCritical(p.SafeHandle, out bool critical) || critical)
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
            Log($"exit: restore -> {(ok ? "ok" : "FAILED: " + Throttle.LastError)}");
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
            Microsoft.Win32.SystemEvents.SessionEnding -= _sessionEnding; // static event: it would keep this alive
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
            lock (LogGate) // SessionEnding and UnhandledException log from other threads
                File.AppendAllText(Path.Combine(Throttle.StateDir, "log.txt"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {line}{Environment.NewLine}");
        }
        catch { }
    }
    static readonly object LogGate = new();

    [DllImport("user32.dll")]
    static extern bool DestroyIcon(IntPtr handle);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool IsProcessCritical(Microsoft.Win32.SafeHandles.SafeProcessHandle process, [MarshalAs(UnmanagedType.Bool)] out bool critical);
}
