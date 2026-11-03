// Try Linux settings: a tabbed page (General, Devices, Advanced, Recovery)
// for one system, plus the recovery tools behind it.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

class Forward
{
    public bool Lan;
    public string Proto = "tcp";
    public int Host, Guest;

    // "tcp:2223:22" forwards a port on this PC only; "lan tcp:8080:80" also accepts other devices.
    public static bool TryParse(string line, out Forward f)
    {
        f = null;
        var m = Regex.Match(line.Trim(), @"^(lan\s+)?(tcp|udp):(\d{1,5}):(\d{1,5})$", RegexOptions.IgnoreCase);
        if (!m.Success) return false;
        int h = int.Parse(m.Groups[3].Value), g = int.Parse(m.Groups[4].Value);
        if (h < 1 || h > 65535 || g < 1 || g > 65535) return false;
        f = new Forward { Lan = m.Groups[1].Success, Proto = m.Groups[2].Value.ToLowerInvariant(), Host = h, Guest = g };
        return true;
    }

    public string ToHostFwd()
    {
        return "hostfwd=" + Proto + ":" + (Lan ? "0.0.0.0" : "127.0.0.1") + ":" + Host + "-:" + Guest;
    }
}

class DiskInfo
{
    public long Virtual = -1, Actual = -1;
    public List<KeyValuePair<string, string>> Snapshots = new List<KeyValuePair<string, string>>();

    public static DiskInfo Read(string disk)
    {
        var d = new DiskInfo();
        string json = Winq.Run(Winq.Img, "info --force-share --output=json " + MainForm.Q(disk));
        var o = (Dictionary<string, object>)new JavaScriptSerializer().DeserializeObject(json);
        if (o.ContainsKey("virtual-size")) d.Virtual = Convert.ToInt64(o["virtual-size"]);
        if (o.ContainsKey("actual-size")) d.Actual = Convert.ToInt64(o["actual-size"]);
        object snaps;
        if (o.TryGetValue("snapshots", out snaps) && snaps is object[])
            foreach (Dictionary<string, object> s in (object[])snaps)
            {
                var when = DateTimeOffset.FromUnixTimeSeconds(Convert.ToInt64(s["date-sec"])).LocalDateTime;
                d.Snapshots.Add(new KeyValuePair<string, string>((string)s["name"], when.ToString("yyyy-MM-dd HH:mm")));
            }
        return d;
    }
}

// Vertical form builder for one settings tab.
class Sheet
{
    public readonly Panel Panel = new Panel { AutoScroll = true, BackColor = Theme.Bg, Dock = DockStyle.Fill, Visible = false };
    int y = 10;
    const int LabelW = 180, Field = 190, Width = 600;

    public Sheet() { Native.DarkScrollbars(Panel); }

    T Place<T>(T c, int x, int top, int w, int h) where T : Control
    {
        c.SetBounds(Theme.D(x), Theme.D(top), Theme.D(w), Theme.D(h));
        Panel.Controls.Add(c);
        return c;
    }

    static Label Lbl(string text, Font font, Color color)
    {
        return new Label { Text = text, Font = font, ForeColor = color, BackColor = Theme.Bg, UseMnemonic = false };
    }

    public void Section(string title)
    {
        if (y > 10) y += 18;
        Place(Lbl(title, Theme.Bold, Theme.Accent), 0, y, Width, 24);
        y += 32;
    }

    public CheckBox Check(string text, bool on, int x = 0)
    {
        var c = Place(new CheckBox { Text = text, Checked = on, Font = Theme.Body, ForeColor = Theme.Text, BackColor = Theme.Bg, FlatStyle = FlatStyle.Flat, UseMnemonic = false }, x, y, Width - x, 26);
        c.FlatAppearance.BorderColor = Theme.Border;
        c.FlatAppearance.CheckedBackColor = Theme.Accent;
        y += 30;
        return c;
    }

    public T Row<T>(string label, T c, int w, int h = 26) where T : Control
    {
        Place(Lbl(label, Theme.Body, Theme.Text), 0, y + 3, LabelW, Math.Max(22, h - 4));
        Place(c, Field, y, w, h);
        y += h + 8;
        return c;
    }

    // A label column with several controls laid out to the right of it.
    public void RowOf(string label, params Tuple<Control, int>[] items)
    {
        Place(Lbl(label, Theme.Body, Theme.Text), 0, y + 3, LabelW, 22);
        int x = Field;
        foreach (var it in items) { Place(it.Item1, x, y, it.Item2, it.Item1 is Button ? 28 : 26); x += it.Item2 + 10; }
        y += 34;
    }

    public Label Note(string text, int lines = 1, int x = 0)
    {
        var l = Place(Lbl(text, Theme.Small, Theme.Muted), x, y, Width - x, 18 * lines + 2);
        y += 18 * lines + 8;
        return l;
    }

    public List<Button> Buttons(params string[] names)
    {
        var list = new List<Button>();
        int x = 0;
        // Several buttons share the row evenly; a lone button sizes to its text.
        int even = (Width - 20 - 10 * (names.Length - 1)) / names.Length;
        foreach (var n in names)
        {
            int w = names.Length > 1 ? Math.Min(even, Math.Max(130, n.Length * 9 + 30)) : Math.Max(130, n.Length * 9 + 30);
            list.Add(Place(new FlatBtn(n), x, y, w, 30));
            x += w + 10;
        }
        y += 40;
        return list;
    }

    public void Gap(int h) { y += h; }

    public static ComboBox Combo(params string[] items)
    {
        var c = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat, BackColor = Theme.Card, ForeColor = Theme.Text, Font = Theme.Body };
        c.Items.AddRange(items);
        if (items.Length > 0) c.SelectedIndex = 0;
        Native.DarkScrollbars(c);
        return c;
    }

    public static TextBox Input(string text, bool multiline = false)
    {
        var t = new TextBox { Text = text, Font = Theme.Body, BackColor = Theme.Card, ForeColor = Theme.Text, BorderStyle = BorderStyle.FixedSingle, Multiline = multiline };
        if (multiline) { t.ScrollBars = ScrollBars.Vertical; t.AcceptsReturn = true; Native.DarkScrollbars(t); }
        return t;
    }

    public static NumericUpDown Number(int min, int max, int value)
    {
        var n = new NumericUpDown { Minimum = min, Maximum = max, Font = Theme.Body, BackColor = Theme.Card, ForeColor = Theme.Text, BorderStyle = BorderStyle.FixedSingle };
        n.Value = Math.Max(min, Math.Min(max, value));
        return n;
    }

    public static RadioButton Radio(string text, bool on)
    {
        return new RadioButton { Text = text, Checked = on, Font = Theme.Body, ForeColor = Theme.Text, BackColor = Theme.Bg, FlatStyle = FlatStyle.Flat, UseMnemonic = false };
    }
}

class TabStrip : Control
{
    public string[] Tabs = new string[0];
    int selectedIndex;
    public event EventHandler Changed;

    public TabStrip()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Cursor = Cursors.Hand;
        BackColor = Theme.Bg;
    }

    public int SelectedIndex
    {
        get { return selectedIndex; }
        set { selectedIndex = value; Invalidate(); if (Changed != null) Changed(this, EventArgs.Empty); }
    }

    Rectangle TabRect(int i)
    {
        int gap = Theme.D(6), w = (Width - gap * (Tabs.Length - 1)) / Tabs.Length;
        return new Rectangle(i * (w + gap), 0, w, Height - 1);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        for (int i = 0; i < Tabs.Length; i++) if (TabRect(i).Contains(e.Location)) SelectedIndex = i;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.Bg);
        for (int i = 0; i < Tabs.Length; i++)
        {
            var r = TabRect(i);
            bool sel = i == selectedIndex;
            using (var b = new SolidBrush(sel ? Theme.CardSelected : Theme.Card)) g.FillRectangle(b, r);
            using (var p = new Pen(sel ? Theme.Accent : Theme.Border)) g.DrawRectangle(p, r);
            TextRenderer.DrawText(g, Tabs[i], Theme.Bold, r, sel ? Theme.Accent : Theme.Text,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }
    }
}

partial class MainForm
{
    public const string Version = "1.1.2";
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    void ShowSettings(VmConfig cfg)
    {
        string who = cfg.DisplayName;
        bool isRunning = running.ContainsKey(cfg.Name);
        var root = new Page(0, false);

        // Header: icon, title, subtitle.
        var icon = root.Add(new PictureBox { Image = Icons.Draw(Theme.D(44)), SizeMode = PictureBoxSizeMode.CenterImage, BackColor = Theme.Bg }, 0, 14, 48, 48);
        root.Caption(who, 62, 12, 578, 28, Theme.Title);
        var d = Distro.Find(cfg.DistroId);
        root.Caption((d != null ? d.Name.ToUpperInvariant() : "CUSTOM ISO") + "  ·  TRY LINUX v" + Version + (isRunning ? "  ·  RUNNING" : ""),
            62, 40, 578, 22, Theme.Small, Theme.Muted);

        var tabs = root.Add(new TabStrip { Tabs = new[] { "General", "Devices", "Advanced", "Recovery" } }, 0, 76, 640, 34);
        var body = root.Add(new Panel { BackColor = Theme.Bg }, 0, 118, 640, 100);
        var footer = new Panel { BackColor = Theme.Bg };
        root.Col.Controls.Add(footer);
        root.Fill = () =>
        {
            int h = root.ClientSize.Height;
            root.Col.Height = h;
            footer.SetBounds(0, h - Theme.D(92), root.Col.Width, Theme.D(92));
            body.Height = Math.Max(Theme.D(100), footer.Top - body.Top - Theme.D(8));
        };

        var general = new Sheet();
        var devices = new Sheet();
        var advanced = new Sheet();
        var recovery = new Sheet();
        var sheets = new[] { general, devices, advanced, recovery };
        foreach (var s in sheets) body.Controls.Add(s.Panel);
        tabs.Changed += delegate { for (int i = 0; i < sheets.Length; i++) sheets[i].Panel.Visible = i == tabs.SelectedIndex; };

        // ---- General
        general.Section("Display and startup");
        var full = general.Check("Open fullscreen", cfg.Fullscreen);
        var screens = System.Windows.Forms.Screen.AllScreens;
        var display = general.Row("Fullscreen display",
            Sheet.Combo(new[] { "Primary display" }.Concat(screens.Select(s =>
                s.DeviceName + " (" + s.Bounds.Width + " x " + s.Bounds.Height + ")" + (s.Primary ? ", primary" : ""))).ToArray()), 340);
        for (int i = 0; i < screens.Length; i++) if (screens[i].DeviceName == cfg.FullscreenDisplay) display.SelectedIndex = i + 1;
        var direct = general.Check("Desktop shortcut starts " + who + " directly", cfg.ShortcutDirect);
        var login = general.Check("Start " + who + " when I sign in to Windows", RunAtLogin(cfg));
        var shortcutBtn = general.Buttons(ShortcutPath(cfg) != null && File.Exists(ShortcutPath(cfg)) ? "Update desktop shortcut" : "Add desktop shortcut")[0];

        general.Section("Resources");
        var profile = general.Row("Resource profile", Sheet.Combo(VmConfig.ProfileNames), 340);
        var ram = general.Row("Memory (GiB)", Sheet.Number(1, Sys.TotalRamGB, cfg.RamGB), 100);
        var cores = general.Row("CPU cores", Sheet.Number(1, Environment.ProcessorCount, cfg.Cores), 100);
        var estimate = general.Note("", 3);
        double availGB = new Microsoft.VisualBasic.Devices.ComputerInfo().AvailablePhysicalMemory / 1073741824.0;
        Action refreshEstimate = () =>
        {
            ram.Enabled = cores.Enabled = profile.SelectedIndex == 2;
            var probe = new VmConfig { Profile = profile.SelectedIndex, RamGB = (int)ram.Value, Cores = (int)cores.Value };
            int n, gb;
            probe.Resources(out n, out gb);
            estimate.Text = "Estimated next boot: " + n + " vCPUs, " + gb + " GiB RAM.\n" +
                "PC: " + Environment.ProcessorCount + " logical CPUs, " + Sys.TotalRamGB + " GiB RAM; " + availGB.ToString("0.0") + " GiB available when Settings opened.\n" +
                "Applies next boot; no live resizing.";
        };
        profile.SelectedIndexChanged += delegate { refreshEstimate(); };
        ram.ValueChanged += delegate { refreshEstimate(); };
        cores.ValueChanged += delegate { refreshEstimate(); };
        profile.SelectedIndex = Math.Max(0, Math.Min(2, cfg.Profile));
        refreshEstimate();

        general.Section("Storage and sharing");
        var disk = general.Row("Disk capacity (GiB)", Sheet.Number(0, 4096, cfg.DiskGB), 100);
        general.Note("0 keeps the current size. Increasing grows the disk next launch; lowering never shrinks it.\nLinux may need to expand its partition to use the new space.", 2);
        var capacity = general.Note("Current capacity: checking...", 1);
        var location = general.Row("Location", Sheet.Input(cfg.Dir), 410);
        location.ReadOnly = true;
        var shared = Sheet.Input(cfg.SharedFolder);
        var browse = new FlatBtn("Browse...");
        general.RowOf("Shared folder", Tuple.Create((Control)shared, 300), Tuple.Create((Control)browse, 100));
        var write = general.Check("Allow Linux to read and change this folder", cfg.SharedWrite, 190);
        general.Note("Off shares it read-only. In Linux: sudo mount -t 9p -o trans=virtio host /mnt/host", 1, 190);
        browse.Click += delegate
        {
            using (var dlg = new FolderBrowserDialog { Description = "Choose a Windows folder to share with " + who })
                if (dlg.ShowDialog(this) == DialogResult.OK) shared.Text = dlg.SelectedPath;
        };
        Task.Run(() =>
        {
            string text;
            try
            {
                var info = DiskInfo.Read(cfg.Disk);
                var drive = new DriveInfo(Path.GetPathRoot(cfg.Dir));
                text = "Current capacity: " + Gib(info.Virtual) + " (" + Gib(info.Actual) + " used). Free on drive " +
                    drive.Name.TrimEnd('\\') + " " + Gib(drive.AvailableFreeSpace) + ".";
            }
            catch (Exception ex) { text = "Couldn't read the disk: " + ex.Message; }
            Ui(() => { if (!capacity.IsDisposed) capacity.Text = text; });
        });

        // ---- Devices
        devices.Section("Microphone and sound");
        var sound = devices.Check("Enable sound", cfg.Sound);
        var mic = devices.Check("Allow microphone access (next start)", cfg.Mic);
        devices.Note("Turning microphone access off keeps sound playback enabled.");
        var outDev = devices.Row("Sound output", Sheet.Combo("Windows default"), 340);
        var inDev = devices.Row("Microphone", Sheet.Combo("Windows default"), 340);
        outDev.Enabled = inDev.Enabled = false;
        devices.Note("This graphics engine doesn't support separate audio choices.\nWindows defaults are used.", 2);
        var soundBtns = devices.Buttons("Windows sound devices...", "Microphone privacy...");
        devices.Note("Choose Windows playback and recording defaults before launching.\nIf a choice isn't picked up, restart " + who + ".", 2);
        sound.CheckedChanged += delegate { mic.Enabled = sound.Checked; };
        mic.Enabled = sound.Checked;
        soundBtns[0].Click += delegate { OpenUri("ms-settings:sound"); };
        soundBtns[1].Click += delegate { OpenUri("ms-settings:privacy-microphone"); };

        devices.Section("Camera and USB");
        devices.Note("WINQ-EMU doesn't include camera or USB passthrough, so Windows cameras\nand USB devices can't be attached. Use the shared folder to move files.", 2);

        // ---- Advanced
        advanced.Section("Graphics");
        var displays = Sheet.Number(1, 4, cfg.Displays);
        advanced.RowOf("Guest displays", Tuple.Create((Control)displays, 100),
            Tuple.Create((Control)new Label { Text = "1 to 4; each opens its own window", Font = Theme.Small, ForeColor = Theme.Muted, BackColor = Theme.Bg }, 300));
        var rAuto = Sheet.Radio("Automatic", cfg.Render == 0);
        var rGpu = Sheet.Radio("GPU", cfg.Render == 1);
        var rCpu = Sheet.Radio("CPU", cfg.Render == 2);
        advanced.RowOf("Rendering", Tuple.Create((Control)rAuto, 120), Tuple.Create((Control)rGpu, 80), Tuple.Create((Control)rCpu, 80));
        advanced.Note("Automatic tries the GPU and switches to CPU rendering if the GPU path fails\nto start. GPU retries every launch.", 2);
        var venus = advanced.Check("Vulkan acceleration (Venus)", cfg.Venus);
        var gpuMem = advanced.Row("GPU memory (GiB)", Sheet.Number(1, 64, cfg.GpuMemGB), 100);
        advanced.Note("GPU shares Windows graphics through OpenGL / Vulkan. Full GPU passthrough\nand NVIDIA CUDA / OptiX are not available in this runtime.\nLast successful boot: " +
            (cfg.LastRender.Length > 0 ? cfg.LastRender : "not started yet") + ".", 3);

        advanced.Section("Network");
        var forwards = advanced.Row("Port forwards", Sheet.Input(string.Join(Environment.NewLine, cfg.ForwardLines()), true), 410, 90);
        advanced.Note("One per line. tcp:HOST:GUEST is reachable from this PC only;\nlan tcp:HOST:GUEST also accepts other devices on your network\n(Windows Firewall asks the first time). SSH uses the rule for guest port 22.", 3);

        advanced.Section("About");
        advanced.Note("Try Linux " + Version + "\nWINQ-EMU: " + (Winq.Ready ? Winq.Bin : "not installed") + "\nStorage: " + AppSettings.Storage, 3);
        var aboutBtns = advanced.Buttons("WINQ-EMU releases...", "Open storage folder");
        aboutBtns[0].Click += delegate { OpenUri("https://github.com/cmspam/winq-emu/releases"); };
        aboutBtns[1].Click += delegate { Directory.CreateDirectory(AppSettings.Storage); Process.Start("explorer.exe", Q(AppSettings.Storage)); };

        // ---- Recovery
        recovery.Section("Backup and recovery");
        var rec1 = recovery.Buttons("Back up...", "Restore...", "Snapshots...", "Reset system...");
        recovery.Note("Shut " + who + " down first. Backups include its settings.\nRestore creates a separate copy, so the original stays as it is.", 2);
        var rec2 = recovery.Buttons("Boot an ISO...", "Move storage...", "Clean up...", "Delete...");
        var cleanupNote = recovery.Note("Clean up removes downloaded installers that no system still needs.", 1);
        rec2[3].ForeColor = Theme.Danger;
        rec2[3].FlatAppearance.BorderColor = Theme.Danger;

        // ---- Footer
        var hint = new Label { Text = isRunning ? who + " is running. Changes apply the next time it starts." : "Your files persist between sessions. Choose your settings, then launch.",
            Font = Theme.Small, ForeColor = Theme.Muted, BackColor = Theme.Bg, UseMnemonic = false };
        hint.SetBounds(0, Theme.D(6), root.Col.Width, Theme.D(22));
        var back = new FlatBtn("Back");
        back.SetBounds(0, Theme.D(40), Theme.D(120), Theme.D(42));
        var save = new FlatBtn("Save");
        save.SetBounds(Theme.D(330), Theme.D(40), Theme.D(120), Theme.D(42));
        var launch = new FlatBtn(isRunning ? "Show " + Short(who) : "Launch " + Short(who), true);
        launch.SetBounds(Theme.D(460), Theme.D(40), Theme.D(180), Theme.D(42));
        footer.Controls.AddRange(new Control[] { hint, back, save, launch });

        // Copies the form into cfg; false (after telling the user) when something is invalid.
        Func<bool> store = () =>
        {
            var fwd = new List<string>();
            foreach (var line in forwards.Lines.Select(l => l.Trim()).Where(l => l.Length > 0))
            {
                Forward f;
                if (!Forward.TryParse(line, out f)) { tabs.SelectedIndex = 2; Error("\"" + line + "\" isn't a port forward. Use tcp:HOST:GUEST, e.g. tcp:2223:22."); return false; }
                fwd.Add(line);
            }
            string sf = shared.Text.Trim();
            if (sf.Length > 0 && !Directory.Exists(sf)) { tabs.SelectedIndex = 0; Error("The shared folder doesn't exist."); return false; }
            if (sf.Contains(",")) { tabs.SelectedIndex = 0; Error("QEMU can't share a folder whose path contains a comma."); return false; }

            cfg.Fullscreen = full.Checked;
            cfg.FullscreenDisplay = display.SelectedIndex > 0 ? screens[display.SelectedIndex - 1].DeviceName : "";
            cfg.ShortcutDirect = direct.Checked;
            cfg.Profile = profile.SelectedIndex;
            cfg.RamGB = (int)ram.Value;
            cfg.Cores = (int)cores.Value;
            cfg.DiskGB = (int)disk.Value;
            cfg.SharedFolder = sf;
            cfg.SharedWrite = write.Checked;
            cfg.Sound = sound.Checked;
            cfg.Mic = mic.Checked;
            cfg.Displays = (int)displays.Value;
            cfg.Render = rGpu.Checked ? 1 : rCpu.Checked ? 2 : 0;
            cfg.Venus = venus.Checked;
            cfg.GpuMemGB = (int)gpuMem.Value;
            cfg.Forwards = string.Join(";", fwd);
            cfg.Save();
            try
            {
                SetRunAtLogin(cfg, login.Checked);
                if (File.Exists(ShortcutPath(cfg))) WriteShortcut(cfg);
            }
            catch (Exception ex) { Error("Settings were saved, but Windows startup or the shortcut couldn't be updated: " + ex.Message); }
            return true;
        };

        back.Click += delegate { ShowHome(); };
        save.Click += delegate { if (store()) ShowHome(); };
        launch.Click += delegate { if (store()) Boot(cfg); };
        shortcutBtn.Click += delegate
        {
            if (!store()) return;
            try { WriteShortcut(cfg); shortcutBtn.Text = "Update desktop shortcut"; Info("\"" + cfg.DisplayName + "\" is on your desktop."); }
            catch (Exception ex) { Error("Couldn't create the shortcut: " + ex.Message); }
        };

        rec1[0].Click += delegate { if (Stopped(cfg)) Backup(cfg); };
        rec1[1].Click += delegate { Restore(cfg); };
        rec1[2].Click += delegate { if (Stopped(cfg)) using (var dlg = new SnapshotDialog(cfg)) dlg.ShowDialog(this); };
        rec1[3].Click += delegate { if (Stopped(cfg)) ResetSystem(cfg); };
        rec2[0].Click += delegate
        {
            if (!Stopped(cfg) || !store()) return;
            using (var dlg = new OpenFileDialog { Filter = "Disk images (*.iso)|*.iso|All files|*.*", Title = "Boot " + who + " from an ISO" })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                cfg.Iso = dlg.FileName;
                cfg.PendingInstall = true;
                cfg.Save();
                Boot(cfg);
            }
        };
        rec2[1].Click += delegate { MoveStorage(cfg); };
        rec2[2].Click += delegate { CleanUp(cfg); };
        rec2[3].Click += delegate { if (Stopped(cfg)) DeleteSystem(cfg); };

        AcceptButton = null;
        SetPage(root);
        tabs.SelectedIndex = 0;
    }

    static string Short(string name)
    {
        int i = name.IndexOf(' ');
        return name.Length <= 14 ? name : i > 0 && i <= 14 ? name.Substring(0, i) : name.Substring(0, 14);
    }

    static string Gib(long bytes) { return bytes < 0 ? "?" : (bytes / 1073741824.0).ToString("0.0") + " GiB"; }

    void OpenUri(string uri)
    {
        try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); }
        catch (Exception ex) { Error("Couldn't open " + uri + ": " + ex.Message); }
    }

    bool Stopped(VmConfig cfg)
    {
        if (!running.ContainsKey(cfg.Name)) return true;
        Error("Shut " + cfg.DisplayName + " down first. Use the Try Linux tray icon, or shut down from inside Linux.");
        return false;
    }

    // ------------------------------------------------------------ startup and shortcuts

    static string RunValue(VmConfig cfg) { return "TryLinux-" + cfg.Name; }

    static bool RunAtLogin(VmConfig cfg)
    {
        using (var k = Registry.CurrentUser.OpenSubKey(RunKey)) return k != null && k.GetValue(RunValue(cfg)) != null;
    }

    static void SetRunAtLogin(VmConfig cfg, bool on)
    {
        using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
        {
            if (on) k.SetValue(RunValue(cfg), Q(Application.ExecutablePath) + " --start " + cfg.Name);
            else if (k.GetValue(RunValue(cfg)) != null) k.DeleteValue(RunValue(cfg));
        }
    }

    static string ShortcutPath(VmConfig cfg)
    {
        string name = string.Concat(cfg.DisplayName.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), name + ".lnk");
    }

    static void WriteShortcut(VmConfig cfg)
    {
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
        dynamic sc = shell.CreateShortcut(ShortcutPath(cfg));
        sc.TargetPath = Application.ExecutablePath;
        sc.Arguments = cfg.ShortcutDirect ? "--start " + cfg.Name : "";
        sc.WorkingDirectory = Path.GetDirectoryName(Application.ExecutablePath);
        sc.IconLocation = Application.ExecutablePath + ",0";
        sc.Description = (cfg.ShortcutDirect ? "Start " : "Open Try Linux for ") + cfg.DisplayName;
        sc.Save();
    }

    // ------------------------------------------------------------ recovery

    // Runs slow work behind the progress screen, then returns to this system's settings.
    async void RunTool(VmConfig cfg, string status, Func<string> work)
    {
        busy = true;
        ShowProgress(status, false);
        SetProgress(status, -1, "This can take a few minutes for large disks");
        string done = null, failed = null;
        try { done = await Task.Run(work); }
        catch (Exception ex) { failed = ex.Message; }
        busy = false;
        var fresh = Directory.Exists(cfg.Dir) ? VmConfig.Load(cfg.Dir) : null;
        if (fresh != null && File.Exists(fresh.Disk)) ShowSettings(fresh); else ShowHome();
        if (failed != null) Error(failed);
        else if (done != null) Info(done);
    }

    void Backup(VmConfig cfg)
    {
        using (var dlg = new SaveFileDialog { Filter = "Try Linux backup (*.qcow2)|*.qcow2", Title = "Back up " + cfg.DisplayName,
            FileName = cfg.Name + "-" + DateTime.Now.ToString("yyyyMMdd") + ".qcow2" })
        {
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            string dest = dlg.FileName;
            RunTool(cfg, "Backing up " + cfg.DisplayName, () =>
            {
                if (File.Exists(dest)) File.Delete(dest);
                Winq.Run(Winq.Img, "convert -c -O qcow2 " + Q(cfg.Disk) + " " + Q(dest));
                File.Copy(Path.Combine(cfg.Dir, "vm.ini"), dest + ".ini", true);
                return "Backed up " + cfg.DisplayName + " to\n" + dest + "\n(" + Gib(new FileInfo(dest).Length) + ", compressed).";
            });
        }
    }

    void Restore(VmConfig cfg)
    {
        using (var dlg = new OpenFileDialog { Filter = "Try Linux backup (*.qcow2)|*.qcow2", Title = "Restore a backup as a new system" })
        {
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            string src = dlg.FileName;
            string id = UniqueName(cfg.Name + "-restored");
            var copy = File.Exists(src + ".ini") ? VmConfig.LoadIni(src + ".ini") : VmConfig.Load(cfg.Dir);
            copy.Dir = Path.Combine(AppSettings.VmsDir, id);
            copy.Name = id;
            copy.DisplayName = copy.DisplayName + " (restored " + File.GetLastWriteTime(src).ToString("yyyy-MM-dd") + ")";
            copy.PendingInstall = false;
            RunTool(cfg, "Restoring a copy of " + cfg.DisplayName, () =>
            {
                Directory.CreateDirectory(copy.Dir);
                Winq.Run(Winq.Img, "convert -O qcow2 " + Q(src) + " " + Q(copy.Disk));
                copy.Save();
                return "Restored as \"" + copy.DisplayName + "\". It uses the same port forwards as the original, so run one at a time or change them in Settings.";
            });
        }
    }

    void ResetSystem(VmConfig cfg)
    {
        string iso = cfg.Iso;
        if (!File.Exists(iso))
            using (var dlg = new OpenFileDialog { Filter = "Disk images (*.iso)|*.iso|All files|*.*", Title = "Choose the installer to start over with" })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                iso = dlg.FileName;
            }
        if (MessageBox.Show(this, "Reset " + cfg.DisplayName + "?\n\nThis erases everything installed in it, including its snapshots, and starts over with the installer. Back it up first if you might need anything.",
                "Reset system", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        RunTool(cfg, "Resetting " + cfg.DisplayName, () =>
        {
            long size = DiskInfo.Read(cfg.Disk).Virtual;
            string gb = Math.Max(16, Math.Max(cfg.DiskGB, (int)(size / 1073741824))) + "G";
            File.Delete(cfg.Disk);
            Winq.Run(Winq.Img, "create -f qcow2 " + Q(cfg.Disk) + " " + gb);
            var fresh = VmConfig.Load(cfg.Dir);
            fresh.Iso = iso;
            fresh.PendingInstall = true;
            fresh.Save();
            return cfg.DisplayName + " was reset. Click Launch to run the installer.";
        });
    }

    void DeleteSystem(VmConfig cfg)
    {
        if (MessageBox.Show(this, "Delete " + cfg.DisplayName + "?\n\nThis permanently removes its virtual disk and everything installed in it. Downloaded ISOs are kept.",
                "Delete system", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        try
        {
            SetRunAtLogin(cfg, false);
            if (cfg.ShortcutDirect && File.Exists(ShortcutPath(cfg))) File.Delete(ShortcutPath(cfg));
            Directory.Delete(cfg.Dir, true);
        }
        catch (Exception ex) { Error("Couldn't delete it completely: " + ex.Message); }
        ShowHome();
    }

    void MoveStorage(VmConfig cfg)
    {
        if (running.Count > 0) { Error("Shut every system down before moving storage."); return; }
        using (var dlg = new FolderBrowserDialog { Description = "Move all Try Linux systems and downloads to:" })
        {
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            string from = AppSettings.Storage, to = Path.Combine(dlg.SelectedPath, "TryLinux");
            if (to.StartsWith(from.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase) || to.Equals(from, StringComparison.OrdinalIgnoreCase))
            { Error("Choose a folder outside the current storage location."); return; }
            if (Directory.Exists(to) && Directory.EnumerateFileSystemEntries(to).Any()) { Error(to + " already exists and isn't empty."); return; }
            var drive = new DriveInfo(Path.GetPathRoot(to));
            long need = Directory.Exists(from) ? new DirectoryInfo(from).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length) : 0;
            if (drive.AvailableFreeSpace < need + (1L << 30)) { Error("Not enough space on " + drive.Name + ": " + Gib(need) + " needed."); return; }
            RunTool(cfg, "Moving storage to " + to, () =>
            {
                CopyTree(from, to);
                AppSettings.Storage = to;
                AppSettings.Save();
                foreach (var vm in VmConfig.All())
                    if (vm.Iso.StartsWith(from, StringComparison.OrdinalIgnoreCase)) { vm.Iso = to + vm.Iso.Substring(from.Length); vm.Save(); }
                Directory.Delete(from, true);
                cfg.Dir = Path.Combine(AppSettings.VmsDir, cfg.Name);
                return "Moved " + Gib(need) + " to " + to + ".";
            });
        }
    }

    static void CopyTree(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)));
        foreach (var d in Directory.GetDirectories(from)) CopyTree(d, Path.Combine(to, Path.GetFileName(d)));
    }

    void CleanUp(VmConfig cfg)
    {
        var needed = new HashSet<string>(VmConfig.All().Where(v => v.PendingInstall).Select(v => v.Iso), StringComparer.OrdinalIgnoreCase);
        var files = new List<FileInfo>();
        foreach (var dir in new[] { AppSettings.IsoDir, AppSettings.DownloadsDir })
            if (Directory.Exists(dir))
                files.AddRange(new DirectoryInfo(dir).GetFiles().Where(f => !needed.Contains(f.FullName)));
        if (files.Count == 0) { Info("Nothing to clean up. Installers that a system still needs are kept."); return; }
        long total = files.Sum(f => f.Length);
        if (MessageBox.Show(this, "Delete " + files.Count + " downloaded file(s), " + Gib(total) + "?\n\n" +
                string.Join("\n", files.Take(8).Select(f => f.Name)) + (files.Count > 8 ? "\n..." : "") +
                "\n\nYou can download them again later.", "Clean up", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        int failed = 0;
        foreach (var f in files) try { f.Delete(); } catch { failed++; }
        Info("Freed " + Gib(total) + "." + (failed > 0 ? " " + failed + " file(s) were in use and kept." : ""));
    }
}

class SnapshotDialog : Form
{
    readonly VmConfig cfg;
    readonly ListBox list = new ListBox();
    List<KeyValuePair<string, string>> snaps = new List<KeyValuePair<string, string>>();

    public SnapshotDialog(VmConfig cfg)
    {
        this.cfg = cfg;
        Text = "Snapshots  ·  " + cfg.DisplayName;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        Font = Theme.Body;
        ClientSize = new Size(Theme.D(520), Theme.D(380));
        HandleCreated += delegate { Native.DarkTitleBar(Handle); };

        var note = new Label { Text = "A snapshot saves the whole disk as it is now, inside the disk file. Reverting throws away changes made since.",
            ForeColor = Theme.Muted, BackColor = Theme.Bg, Font = Theme.Small };
        note.SetBounds(Theme.D(16), Theme.D(12), Theme.D(488), Theme.D(40));
        list.SetBounds(Theme.D(16), Theme.D(56), Theme.D(488), Theme.D(250));
        list.BackColor = Theme.Card;
        list.ForeColor = Theme.Text;
        list.BorderStyle = BorderStyle.FixedSingle;
        list.Font = Theme.Body;
        Native.DarkScrollbars(list);
        var take = new FlatBtn("Take snapshot", true);
        take.SetBounds(Theme.D(16), Theme.D(324), Theme.D(160), Theme.D(38));
        var revert = new FlatBtn("Revert");
        revert.SetBounds(Theme.D(186), Theme.D(324), Theme.D(100), Theme.D(38));
        var delete = new FlatBtn("Delete", false, true);
        delete.SetBounds(Theme.D(296), Theme.D(324), Theme.D(100), Theme.D(38));
        var close = new FlatBtn("Close") { DialogResult = DialogResult.Cancel };
        close.SetBounds(Theme.D(404), Theme.D(324), Theme.D(100), Theme.D(38));
        Controls.AddRange(new Control[] { note, list, take, revert, delete, close });
        CancelButton = close;

        take.Click += delegate
        {
            string name = "snapshot-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
            Act(() => Winq.Run(Winq.Img, "snapshot -c " + MainForm.Q(name) + " " + MainForm.Q(cfg.Disk)));
        };
        revert.Click += delegate
        {
            string s = Selected();
            if (s != null && Ask("Revert " + cfg.DisplayName + " to \"" + s + "\"? Changes made since then are lost."))
                Act(() => Winq.Run(Winq.Img, "snapshot -a " + MainForm.Q(s) + " " + MainForm.Q(cfg.Disk)));
        };
        delete.Click += delegate
        {
            string s = Selected();
            if (s != null && Ask("Delete the snapshot \"" + s + "\"?"))
                Act(() => Winq.Run(Winq.Img, "snapshot -d " + MainForm.Q(s) + " " + MainForm.Q(cfg.Disk)));
        };
        Load += delegate { Reload(); };
    }

    string Selected()
    {
        return list.SelectedIndex >= 0 && list.SelectedIndex < snaps.Count ? snaps[list.SelectedIndex].Key : null;
    }

    bool Ask(string q)
    {
        return MessageBox.Show(this, q, "Snapshots", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
    }

    void Act(Action a)
    {
        Cursor = Cursors.WaitCursor;
        try { a(); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Snapshots", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        Cursor = Cursors.Default;
        Reload();
    }

    void Reload()
    {
        list.Items.Clear();
        try { snaps = DiskInfo.Read(cfg.Disk).Snapshots; }
        catch (Exception ex) { snaps.Clear(); list.Items.Add("Couldn't read snapshots: " + ex.Message); return; }
        foreach (var s in snaps) list.Items.Add(s.Value + "   " + s.Key);
        if (snaps.Count == 0) list.Items.Add("No snapshots yet.");
    }
}
