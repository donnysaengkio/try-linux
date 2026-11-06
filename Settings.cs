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

// A settings section's heading that opens and closes it. Click it, or press Space or Enter.
class SectionHeader : Control
{
    public string Title = "";
    public Func<string> Summary;
    public bool Open;
    public int Style;   // 0: heading, chevron and divider; 1 and 2: a bar
    public event EventHandler Toggled;
    bool hover;
    static readonly Font Glyph = new Font("Segoe MDL2 Assets", 9f);

    public SectionHeader()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        BackColor = Theme.Bg;
        Cursor = Cursors.Hand;
        TabStop = true;
    }

    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnClick(EventArgs e) { Focus(); if (Toggled != null) Toggled(this, e); base.OnClick(e); }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if ((e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter) && Toggled != null) { Toggled(this, e); e.Handled = true; }
        base.OnKeyDown(e);
    }
    protected override bool IsInputKey(Keys k) { return k == Keys.Enter || k == Keys.Space || base.IsInputKey(k); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

    // The title as shown: every word starts with a capital letter ("Camera And USB").
    string Shown { get { return string.Join(" ", Title.Split(' ').Select(t => t.Length > 0 ? char.ToUpper(t[0]) + t.Substring(1) : t)); } }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        int w = Width, h = Height, pad = Style == 0 ? 0 : Theme.D(14);
        string chevron = Open ? "" : "";   // down when open, right when closed
        string summary = !Open && Summary != null ? Summary() : "";
        var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
        if (Style == 0)
        {
            g.Clear(Theme.Bg);
            var title = new Rectangle(Theme.D(22), 0, w, h - Theme.D(4));
            TextRenderer.DrawText(g, chevron, Glyph, new Rectangle(0, 0, Theme.D(18), h - Theme.D(4)), hover ? Theme.Text : Theme.Highlight, flags | TextFormatFlags.HorizontalCenter);
            TextRenderer.DrawText(g, Shown, Theme.Heading, title, hover ? Theme.Text : Theme.Highlight, flags);
            TextRenderer.DrawText(g, summary, Theme.Small, new Rectangle(0, 0, w, h - Theme.D(4)), Theme.Muted, flags | TextFormatFlags.Right | TextFormatFlags.EndEllipsis);
            using (var p = new Pen(Focused ? Theme.Highlight : Theme.Border)) g.DrawLine(p, 0, h - 1, w, h - 1);
            return;
        }
        Theme.Surface(g, ClientRectangle, Theme.Bg,
            Open ? Theme.SelectedTop : hover ? Theme.HoverTop : Theme.CardTop,
            Open ? Theme.SelectedBottom : hover ? Theme.HoverBottom : Theme.CardBottom,
            Open ? Theme.Accent : Focused ? Theme.Highlight : Theme.Border, Open);
        TextRenderer.DrawText(g, Shown, Theme.Heading, new Rectangle(pad, 0, w, h), Open ? Theme.Highlight : Theme.Text, flags);
        int chevW = Theme.D(20);
        TextRenderer.DrawText(g, chevron, Glyph, new Rectangle(w - pad - chevW, 0, chevW, h), Theme.Muted, flags | TextFormatFlags.HorizontalCenter);
        // The summary sits between the title and the chevron, shortened with "..." if it doesn't fit.
        int left = pad + TextRenderer.MeasureText(g, Shown, Theme.Heading, Size.Empty, TextFormatFlags.NoPadding).Width + Theme.D(24);
        int right = w - pad - chevW - Theme.D(8);
        if (right > left)
            TextRenderer.DrawText(g, summary, Theme.Small, new Rectangle(left, 0, right - left, h), Theme.Muted, flags | TextFormatFlags.Right | TextFormatFlags.EndEllipsis);
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
        if (Collapsible && current != null)
        {
            // Inside a section: placed in the section's own panel, so closing it hides them all at once
            // without touching rows that show or hide themselves.
            c.SetBounds(Theme.D(x), Theme.D(top - current.ContentTop), Theme.D(w), Theme.D(h));
            current.Body.Controls.Add(c);
            current.Bottom = Math.Max(current.Bottom, top + h - current.ContentTop);
            return c;
        }
        c.SetBounds(Theme.D(x), Theme.D(top), Theme.D(w), Theme.D(h));
        Panel.Controls.Add(c);
        return c;
    }

    static Label Lbl(string text, Font font, Color color)
    {
        return new Label { Text = text, Font = font, ForeColor = color, BackColor = Theme.Bg, UseMnemonic = false };
    }

    // ---- collapsible sections
    // With Collapsible on, each section's heading opens and closes it. Style 0 is a heading with a
    // chevron and a divider; 1 is a card-like bar; 2 is the bar as an accordion (one open at a time).
    public bool Collapsible;
    public int Style;
    public static int HardwareStyle = 1;   // the Hardware tab uses bars
    public static readonly HashSet<string> Opened = new HashSet<string>();   // remembered while Try Linux runs

    class Group
    {
        public SectionHeader Header;
        public readonly Panel Body = new Panel { BackColor = Theme.Bg };
        public int ContentTop, Bottom;   // in sheet units: where its rows start, and how far down they reach
    }
    readonly List<Group> groups = new List<Group>();
    Group current;
    bool fitting;

    int HeaderH { get { return Style == 0 ? 30 : 42; } }
    int SectionGap { get { return Style == 0 ? 16 : 10; } }

    public void Section(string title)
    {
        if (!Collapsible)
        {
            if (y > 10) y += 36;   // twice the space between sections
            Place(Lbl(title, Theme.Heading, Theme.Highlight), 0, y, Width, 24);
            y += 32;
            return;
        }
        if (current != null) y = current.ContentTop + current.Bottom + SectionGap + 12;
        var g = new Group { Header = new SectionHeader { Title = title, Style = Style, Open = Opened.Contains(title) } };
        current = null;   // the header itself isn't part of the section's contents
        Place(g.Header, 0, y, Width, HeaderH);
        y += HeaderH + 12;
        g.ContentTop = y;
        Panel.Controls.Add(g.Body);
        g.Header.Toggled += delegate { Toggle(g); };
        groups.Add(g);
        current = g;
    }

    // A smaller heading inside a section, to group its rows.
    public void SubHeading(string title)
    {
        y += 14;
        Place(Lbl(title, Theme.Bold, Theme.Text), 0, y, Width, 22);
        y += 28;
    }

    // A short line shown on the right of a closed section's heading (read when it's drawn).
    public void Summary(string section, Func<string> text)
    {
        foreach (var g in groups) if (g.Header.Title == section) g.Header.Summary = text;
    }

    void Toggle(Group g)
    {
        bool open = !g.Header.Open;
        if (open && Style == 2) foreach (var o in groups) if (o != g) { o.Header.Open = false; Opened.Remove(o.Header.Title); }
        g.Header.Open = open;
        if (open) Opened.Add(g.Header.Title); else Opened.Remove(g.Header.Title);
        Layout();
    }

    // Bars span the tab area, the same width as the tabs above (less the scrollbar when there is one).
    void FitHeaders() { foreach (var g in groups) g.Header.Width = g.Body.Width = Panel.ClientSize.Width; }

    // Stacks the headings, showing each open section's contents under its heading.
    public void Layout()
    {
        if (!Collapsible) return;
        if (!fitting) { fitting = true; Panel.ClientSizeChanged += delegate { FitHeaders(); }; }
        FitHeaders();
        Panel.SuspendLayout();
        int scroll = Panel.AutoScrollPosition.Y, yy = 10;
        foreach (var g in groups)
        {
            g.Header.Top = Theme.D(yy) + scroll;
            g.Header.Invalidate();
            yy += HeaderH;
            if (g.Header.Open)
            {
                yy += 12;
                g.Body.SetBounds(0, Theme.D(yy) + scroll, Panel.ClientSize.Width, Theme.D(g.Bottom));
                g.Body.Visible = true;
                yy += g.Bottom + 12;
            }
            else g.Body.Visible = false;
            yy += SectionGap;
        }
        Panel.ResumeLayout();
    }

    public CheckBox Check(string text, bool on, int x = 0)
    {
        // In the left column the box starts where labels' and notes' text does (they draw a few pixels in).
        int inset = x == 0 ? TextInset : 0;
        var c = Place(new CheckBox { Text = text, Checked = on, Font = Theme.Body, ForeColor = Theme.Text, BackColor = Theme.Bg, FlatStyle = FlatStyle.Flat, UseMnemonic = false }, x + inset, y, Width - x - inset, 26);
        c.FlatAppearance.BorderColor = Theme.Border;
        c.FlatAppearance.CheckedBackColor = Theme.Highlight;
        y += 30;
        return c;
    }

    // How far in a Label draws its text, in design units.
    public const int TextInset = 3;

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

    public enum Align { Left, Center, Right }

    // A row of buttons against the right edge, like the buttons of Action rows.
    public List<Button> Buttons(params string[] names) { return Buttons(Align.Right, names); }

    // Left and Right: each button fits its text. Center (across the section's width): the buttons share
    // one width, the widest text's. Gaps are even, and a hidden button leaves none.
    public List<Button> Buttons(Align align, params string[] names)
    {
        var list = new List<Button>();
        int same = Math.Min(names.Max(n => Math.Max(130, SnugWidth(n) + 12)),(Width - 10 * (names.Length - 1)) / names.Length);
        foreach (var n in names)
            list.Add(Place(new FlatBtn(n), 0, y, align == Align.Center ? same : SnugWidth(n), 30));
        Action arrange = () =>
        {
            var shown = list.Where(Shown).ToList();
            int gap = Theme.D(10), total = shown.Sum(b => b.Width) + gap * Math.Max(0, shown.Count - 1);
            int edge = list[0].Parent != null ? list[0].Parent.ClientSize.Width : Theme.D(Width);
            int x = align == Align.Left ? 0 : align == Align.Right ? edge - Theme.D(EdgePad) - total : edge / 2 - total / 2;
            foreach (var b in shown) { b.Left = x; x += b.Width + gap; }
        };
        foreach (var b in list) b.VisibleChanged += delegate { arrange(); };
        // Snug buttons keep fitting when their text changes (Add / Update desktop shortcut, Checking...).
        if (align != Align.Center)
            foreach (var b in list) { var btn = b; btn.TextChanged += delegate { btn.Width = Theme.D(SnugWidth(btn.Text)); arrange(); }; }
        if (align != Align.Left && list[0].Parent != null) list[0].Parent.Resize += delegate { arrange(); };
        arrange();
        y += 40;
        return list;
    }

    // Whether a control is set to show, even while its tab or section is hidden (Visible reads false then).
    static readonly System.Reflection.MethodInfo GetState = typeof(Control).GetMethod("GetState",
        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance, null, new[] { typeof(int) }, null);
    static bool Shown(Control c) { return GetState != null ? (bool)GetState.Invoke(c, new object[] { 2 }) : c.Visible; }

    // A line of text with its button against the bar's right edge.
    public Button Action(string text, string button)
    {
        int bw = SnugWidth(button), right = Field + 410;
        Place(Lbl(text, Theme.Body, Theme.Text), 0, y + 4, right - bw - 10, 22);
        var b = Place(new FlatBtn(button), right - bw, y, bw, 30);
        // Every Action button on a tab shares the widest one's width, so they look equal.
        actions.Add(b);
        int w = actions.Max(a => a.Width);
        // In front of their text, whose label was laid out for a narrower button.
        foreach (var a in actions) { a.Width = w; if (a.Parent != null) a.Left = a.Parent.ClientSize.Width - Theme.D(EdgePad) - w; a.BringToFront(); }
        PinRight(b, 0);
        y += 40;
        return b;
    }
    readonly List<Button> actions = new List<Button>();

    // A right-aligned button's width: its text with a little room each side, in design units.
    public static int SnugWidth(string text)
    {
        return Math.Max(80, (int)Math.Ceiling(TextRenderer.MeasureText(Theme.TitleCase(text), Theme.Bold).Width / Theme.Scale) + 32);
    }

    // Room between right-aligned buttons and the bar's right edge, in design units.
    public const int EdgePad = 16;

    // Keeps a control this far (in design units, plus EdgePad) from its section's right edge, which follows the bar's
    // width; with stretch, the control before it grows or shrinks to meet it.
    public static void PinRight(Control c, int gap, Control stretch = null)
    {
        if (c.Parent == null) return;
        EventHandler fit = delegate
        {
            c.Left = c.Parent.ClientSize.Width - Theme.D(EdgePad + gap) - c.Width;
            if (stretch != null) stretch.Width = Math.Max(Theme.D(60), c.Left - Theme.D(10) - stretch.Left);
        };
        c.Parent.Resize += fit;
        fit(null, EventArgs.Empty);
    }

    public void Gap(int h) { y += h; }

    public static ComboBox Combo(params string[] items)
    {
        var c = new DarkList();
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
            Theme.Surface(g, new Rectangle(r.X, r.Y, r.Width + 1, r.Height + 1), Color.Empty,
                sel ? Theme.SelectedTop : Theme.CardTop, sel ? Theme.SelectedBottom : Theme.CardBottom, sel ? Theme.Accent : Theme.Border, false);
            TextRenderer.DrawText(g, Tabs[i], Theme.Bold, r, sel ? Theme.Highlight : Theme.Text,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }
    }
}

partial class MainForm
{
    public const string Version = "1.1.8";
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    void ShowSettings(VmConfig cfg)
    {
        string who = cfg.DisplayName;
        bool isRunning = running.ContainsKey(cfg.Name);
        var root = new Page(0, false);

        // Header: icon, title, subtitle.
        var icon = root.Add(new PictureBox { Image = Icons.Draw(Theme.D(44)), SizeMode = PictureBoxSizeMode.CenterImage, BackColor = Theme.Bg }, 0, 14, 48, 48);
        // 3 units left of the line below it: the serif title font draws its first letter further in.
        var titleLbl = root.Caption(who, 59, 12, 581, 28, Theme.Title);
        root.Caption(OsLabel(cfg).ToUpperInvariant() + "  ·  TRY LINUX v" + Version + (isRunning ? "  ·  RUNNING" : ""),
            62, 40, 578, 22, Theme.Small, Theme.Muted);
        // Renaming: a pencil after the name edits it in place; Enter (or clicking away) saves, Esc cancels.
        var pencil = root.Add(new Label { Text = "", Font = new Font("Segoe MDL2 Assets", 11f), ForeColor = Theme.Muted, BackColor = Theme.Bg,
                                          TextAlign = ContentAlignment.MiddleCenter, Cursor = Cursors.Hand }, 0, 12, 28, 28);
        Action placePencil = () =>
        {
            int w = TextRenderer.MeasureText(titleLbl.Text, Theme.Title).Width;
            pencil.Left = titleLbl.Left + Math.Min(w, titleLbl.Width - Theme.D(30)) + Theme.D(4);
        };
        placePencil();
        pencil.BringToFront();   // the title label spans the row
        pencil.MouseEnter += delegate { pencil.ForeColor = Theme.Highlight; };
        pencil.MouseLeave += delegate { pencil.ForeColor = Theme.Muted; };
        pencil.Click += delegate
        {
            var edit = new TextBox { Text = titleLbl.Text, Font = Theme.Title, BackColor = Theme.Card, ForeColor = Theme.Text, BorderStyle = BorderStyle.FixedSingle };
            edit.SetBounds(titleLbl.Left, titleLbl.Top, titleLbl.Width, titleLbl.Height + Theme.D(2));
            root.Col.Controls.Add(edit);
            edit.BringToFront();
            titleLbl.Visible = pencil.Visible = false;
            bool done = false;
            Action<bool> finish = keep =>
            {
                if (done) return;
                done = true;
                if (keep && edit.Text.Trim().Length > 0)
                {
                    Rename(cfg, edit.Text);
                    titleLbl.Text = cfg.DisplayName;
                }
                root.Col.Controls.Remove(edit);
                edit.Dispose();
                titleLbl.Visible = pencil.Visible = true;
                placePencil();
                pencil.BringToFront();
            };
            edit.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; finish(true); }
                else if (e.KeyCode == Keys.Escape) { e.SuppressKeyPress = true; finish(false); }
            };
            edit.LostFocus += delegate { finish(true); };
            edit.Focus();
            edit.SelectAll();
        };

        // Header, tabs and the tab's contents are spaced twice as far apart as before.
        var tabs = root.Add(new TabStrip { Tabs = new[] { "General", "Hardware", "Devices", "Recovery" } }, 0, 90, 640, 34);
        var body = root.Add(new Panel { BackColor = Theme.Bg }, 0, 140, 640, 100);
        var footer = new Panel { BackColor = Theme.Bg };
        root.Col.Controls.Add(footer);
        root.Fill = () =>
        {
            int h = root.ClientSize.Height;
            root.Col.Height = h;
            footer.SetBounds(0, h - Theme.D(60), root.Col.Width, Theme.D(60));
            body.Height = Math.Max(Theme.D(100), footer.Top - body.Top - Theme.D(8));
        };

        var general = new Sheet { Collapsible = true, Style = Sheet.HardwareStyle };
        var hardware = new Sheet { Collapsible = true, Style = Sheet.HardwareStyle };
        var devices = new Sheet { Collapsible = true, Style = Sheet.HardwareStyle };
        var recovery = new Sheet { Collapsible = true, Style = Sheet.HardwareStyle };
        var sheets = new[] { general, hardware, devices, recovery };
        foreach (var s in sheets) body.Controls.Add(s.Panel);
        tabs.Changed += delegate { for (int i = 0; i < sheets.Length; i++) sheets[i].Panel.Visible = i == tabs.SelectedIndex; };

        // ---- General
        general.Section("Display and startup");
        var full = general.Check("Open fullscreen", cfg.Fullscreen);
        var screens = System.Windows.Forms.Screen.AllScreens;
        var display = general.Row("Fullscreen display",
            Sheet.Combo(new[] { "Primary display" }.Concat(screens.Select(s =>
                s.DeviceName + " (" + s.Bounds.Width + " x " + s.Bounds.Height + ")" + (s.Primary ? ", primary" : ""))).ToArray()), 280);
        for (int i = 0; i < screens.Length; i++) if (screens[i].DeviceName == cfg.FullscreenDisplay) display.SelectedIndex = i + 1;
        // It belongs to Open fullscreen, so its label lines up with that checkbox's text.
        var displayLabel = display.Parent.Controls.OfType<Label>().Last(l => l.Text == "Fullscreen display");
        displayLabel.Left = full.Left + Theme.D(14);
        displayLabel.Width = display.Left - displayLabel.Left - Theme.D(4);
        var direct = general.Check("Desktop shortcut starts " + who + " directly", cfg.ShortcutDirect);
        var login = general.Check("Start " + who + " when I sign in to Windows", RunAtLogin(cfg));
        var shortcutBtn = general.Buttons(Sheet.Align.Left, ShortcutPath(cfg) != null && File.Exists(ShortcutPath(cfg)) ? "Update desktop shortcut" : "Add desktop shortcut")[0];

        Func<bool> store = null;   // set below; buttons above it call it
        // ---- Hardware
        hardware.Section("Graphics");
        var displays = new SizeBox(1, 4, cfg.Displays, new[] { 1, 2, 3, 4 }, 1);
        hardware.RowOf("Guest displays", Tuple.Create((Control)displays, 100),
            Tuple.Create((Control)new Label { Text = "1 to 4; each opens its own window", Font = Theme.Small, ForeColor = Theme.Muted, BackColor = Theme.Bg, TextAlign = ContentAlignment.MiddleLeft }, 300));
        var rAuto = Sheet.Radio("Automatic", cfg.Render == 0);
        var rGpu = Sheet.Radio("GPU", cfg.Render == 1);
        var rCpu = Sheet.Radio("CPU", cfg.Render == 2);
        hardware.RowOf("Rendering", Tuple.Create((Control)rAuto, 120), Tuple.Create((Control)rGpu, 80), Tuple.Create((Control)rCpu, 80));
        hardware.Note("Automatic tries the GPU and switches to CPU rendering if the GPU path fails\nto start. GPU retries every launch.", 2);
        var venus = hardware.Check("Vulkan acceleration (Venus)", cfg.Venus);
        var gpuMem = Sys.RamBox(Sys.MaxGpuMemGB, cfg.GpuMemGB);
        hardware.RowOf("GPU memory (GiB)", Tuple.Create((Control)gpuMem, 100),
            Tuple.Create((Control)new Label { Text = "up to " + Sys.MaxGpuMemGB + " GiB" + (Sys.GpuDedicated ? " (" + Sys.GpuName + ")" : ", shared with Windows"),
                Font = Theme.Small, ForeColor = Theme.Muted, BackColor = Theme.Bg, UseMnemonic = false, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft }, 300));
        hardware.Note("GPU shares Windows graphics through OpenGL / Vulkan. Full GPU passthrough\nand NVIDIA CUDA / OptiX are not available in this runtime.\nLast successful boot: " +
            (cfg.LastRender.Length > 0 ? cfg.LastRender : "not started yet") + ".", 3);

        hardware.Section("Resources");
        var profile = hardware.Row("Resource profile", new Segmented { Options = VmConfig.ProfileNames }, 410, 30);   // buttons, as on Set up
        // What's left to assign: Windows keeps its share, and other running systems keep theirs.
        int otherCores = 0, otherRam = 0;
        foreach (var r in running.Values)
        {
            if (r.Held || r.Cfg == null || r.Cfg.Name == cfg.Name) continue;
            int rc, rg; r.Cfg.Resources(out rc, out rg);
            otherCores += rc; otherRam += rg;
        }
        int maxCores = Math.Max(1, Sys.MaxCores - otherCores), maxRam = Math.Max(1, Sys.MaxRamGB - otherRam);
        Func<string, Label> limit = text => new Label { Text = text, Font = Theme.Small, ForeColor = Theme.Muted, BackColor = Theme.Bg, UseMnemonic = false, TextAlign = ContentAlignment.MiddleLeft };
        var ram = Sys.RamBox(maxRam, cfg.RamGB);
        hardware.RowOf("Memory (GiB)", Tuple.Create((Control)ram, 100), Tuple.Create((Control)limit("up to " + maxRam + " of " + Sys.TotalRamGB + " GiB"), 300));
        var cores = Sys.CoreBox(maxCores, cfg.Cores);
        maxCores = cores.Maximum;
        hardware.RowOf("CPU cores", Tuple.Create((Control)cores, 100), Tuple.Create((Control)limit("up to " + maxCores + " of " + Environment.ProcessorCount), 300));
        bool showing = false;
        Action refreshEstimate = () =>
        {
            if (showing) return;
            bool manual = profile.SelectedIndex == 2;
            ram.Locked = cores.Locked = !manual;
            var probe = new VmConfig { Profile = profile.SelectedIndex, RamGB = (int)ram.Value, Cores = (int)cores.Value };
            int n, gb;
            probe.Resources(out n, out gb);
            // Balanced and Maximum show what they'll give in the greyed-out fields; Manual starts from there.
            if (!manual) { showing = true; ram.Value = gb; cores.Value = n; showing = false; }
        };
        profile.Changed += delegate { refreshEstimate(); };
        ram.ValueChanged += delegate { refreshEstimate(); };
        cores.ValueChanged += delegate { refreshEstimate(); };
        profile.SelectedIndex = Math.Max(0, Math.Min(2, cfg.Profile));
        refreshEstimate();

        hardware.Section("Disks");
        var diskPick = hardware.Row("Disk", Sheet.Combo(), 100);   // as wide as the Resources dropdowns
        hardware.Note("Linux sees each disk as its own drive. " + who + " starts from Disk 1.", 1);

        hardware.SubHeading("Capacity");
        Func<string, Label> valueRow = name => hardware.Row(name, new Label { Text = "Checking...", Font = Theme.Body, ForeColor = Theme.Text, BackColor = Theme.Bg, UseMnemonic = false, TextAlign = ContentAlignment.MiddleLeft }, 410, 22);
        var curSize = valueRow("Current size");
        var freeRow = valueRow("System free");
        var maxSize = valueRow("Maximum size");

        hardware.SubHeading("Disk information");
        hardware.Note("Disk space is not preallocated: the file grows as Linux uses it, up to the maximum size.", 1);
        var stored = hardware.Note("", 1);
        var location = hardware.Row("Location", Sheet.Input(""), 410);
        location.ReadOnly = true;
        // The path box starts just after its label and ends where the buttons below it do.
        location.Left = TextRenderer.MeasureText("Location", Theme.Body).Width + Theme.D(16);
        location.BringToFront();   // over the label's empty right part
        EventHandler fitLocation = delegate { location.Width = location.Parent.ClientSize.Width - Theme.D(Sheet.EdgePad) - location.Left; };
        location.Parent.Resize += fitLocation;
        fitLocation(null, EventArgs.Empty);
        var resizeBtn = hardware.Action("Shrink or expand this disk's maximum size.", "Resize");

        hardware.SubHeading("Free space");
        var reclaim = hardware.Check("Reclaim free space when Linux deletes files", cfg.Reclaim);
        hardware.Note("Linux tells the disk which space it no longer uses (TRIM), so new files reuse it\ninstead of growing the file. Applies to every disk, from the next start.", 2);
        var compactBtn = hardware.Action("Give unused space in this disk's file back to Windows.", "Compact");

        hardware.Section("Disk utilities");
        var addBtn = hardware.Action("Add another disk to " + who + ".", "Add disk");
        var removeBtn = hardware.Action("Take this disk out of " + who + ".", "Remove");
        DiskInfo disk = null;
        Func<int, string> diskName = i => "Disk " + (i + 1);
        Action readDisk = () =>
        {
            int index = Math.Max(0, diskPick.SelectedIndex);
            string path = cfg.AllDisks()[index];
            disk = null;
            curSize.Text = "Checking...";
            freeRow.Text = maxSize.Text = "";
            location.Text = path;
            stored.Text = "Stored in " + Path.GetFileName(path) + (index == 0 ? ", the disk " + who + " starts from." : ".");
            removeBtn.Enabled = index > 0;
            resizeBtn.Enabled = compactBtn.Enabled = false;
            Task.Run(() =>
            {
                DiskInfo info = null; string err = null; long free = -1; string drive = "";
                try
                {
                    if (!File.Exists(path)) throw new FileNotFoundException("the file is missing. It may have been moved or deleted.");
                    info = DiskInfo.Read(path);
                    var d = new DriveInfo(Path.GetPathRoot(path));
                    free = d.AvailableFreeSpace; drive = d.Name.TrimEnd('\\');
                }
                catch (Exception ex) { err = "Couldn't read the disk: " + ex.Message; }
                Ui(() =>
                {
                    if (curSize.IsDisposed || diskPick.SelectedIndex != index) return;
                    disk = info;
                    resizeBtn.Enabled = compactBtn.Enabled = info != null;
                    curSize.Text = err ?? DiskSize(info.Actual);
                    freeRow.Text = err != null ? "" : DiskSize(free) + " on drive " + drive;
                    maxSize.Text = err != null ? "" : DiskSize(info.Virtual);
                    if (info != null && info.Snapshots.Count > 0)
                        stored.Text = stored.Text.TrimEnd('.') + ", with " + info.Snapshots.Count + (info.Snapshots.Count == 1 ? " snapshot." : " snapshots.");
                });
            });
        };
        Action<int> fillDisks = select =>
        {
            diskPick.Items.Clear();
            var all = cfg.AllDisks();
            for (int i = 0; i < all.Count; i++)
                diskPick.Items.Add(diskName(i));   // its file and whether it's missing show below
            diskPick.SelectedIndex = Math.Max(0, Math.Min(select, all.Count - 1));
        };
        diskPick.SelectedIndexChanged += delegate { readDisk(); };
        fillDisks(0);
        // Disk changes take effect at once, so they're saved straight away instead of with Save.
        Action saveDisks = () => { var saved = VmConfig.Load(cfg.Dir); saved.ExtraDisks = cfg.ExtraDisks.ToList(); saved.Save(); };

        addBtn.Click += delegate
        {
            var pick = AskNewDisk();
            if (pick == null) return;
            try
            {
                Cursor = Cursors.WaitCursor;
                if (pick.Item2 != null)
                {
                    string chosenPath = Path.GetFullPath(pick.Item2);
                    if (cfg.AllDisks().Any(d => string.Equals(Path.GetFullPath(d), chosenPath, StringComparison.OrdinalIgnoreCase)))
                    { Error("That disk is already part of " + who + "."); return; }
                    if (VmConfig.All().Any(v => v.Name != cfg.Name && v.AllDisks().Any(d => string.Equals(Path.GetFullPath(d), chosenPath, StringComparison.OrdinalIgnoreCase))))
                    { Error("That disk belongs to another system. Two systems can't share a disk."); return; }
                    DiskInfo.Read(chosenPath);   // fails if it isn't a disk image QEMU can open
                    cfg.ExtraDisks.Add(chosenPath);
                }
                else
                {
                    string name = cfg.NewDiskName();
                    Winq.Run(Winq.Img, "create -f qcow2 " + Q(Path.Combine(cfg.Dir, name)) + " " + pick.Item1 + "G");
                    cfg.ExtraDisks.Add(name);
                }
                saveDisks();
                fillDisks(cfg.ExtraDisks.Count);
                Info(diskName(cfg.ExtraDisks.Count) + " was added to " + who + "." +
                    (running.ContainsKey(cfg.Name) ? " Linux sees it the next time " + who + " starts." : "") +
                    (pick.Item2 == null ? " Partition and format it in Linux (with GNOME Disks, for example) before using it." : ""));
            }
            catch (Exception ex) { Error("Couldn't add the disk.\n\n" + ex.Message); }
            finally { Cursor = Cursors.Default; }
        };

        resizeBtn.Click += delegate
        {
            if (disk == null) return;
            int index = diskPick.SelectedIndex;
            string path = cfg.AllDisks()[index];
            if (running.ContainsKey(cfg.Name)) { Error("Shut down " + who + " before resizing its disks."); return; }
            if (disk.Snapshots.Count > 0) { Error("A disk with snapshots can't be resized. Delete its snapshots first (Settings > Recovery)."); return; }
            int gb = AskDiskSize(diskName(index), disk);
            if (gb <= 0) return;
            bool shrink = (long)gb << 30 < disk.Virtual;
            try
            {
                Cursor = Cursors.WaitCursor;
                Winq.Run(Winq.Img, "resize " + (shrink ? "--shrink " : "") + Q(path) + " " + gb + "G");
                if (index == 0)
                {
                    // The new size is the disk's own now; nothing is left to grow at the next start.
                    cfg.DiskGB = 0;
                    var saved = VmConfig.Load(cfg.Dir); saved.DiskGB = 0; saved.Save();
                }
                Info(diskName(index) + " is now " + gb + " GB." + (shrink ? "" : " Linux may need to expand its partition to use the new space."));
            }
            catch (Exception ex) { Error("Couldn't resize the disk.\n\n" + ex.Message); }
            finally { Cursor = Cursors.Default; }
            readDisk();
        };

        compactBtn.Click += delegate
        {
            if (disk == null) return;
            int index = diskPick.SelectedIndex;
            string path = cfg.AllDisks()[index];
            if (running.ContainsKey(cfg.Name)) { Error("Shut down " + who + " before compacting its disks."); return; }
            if (disk.Snapshots.Count > 0) { Error("A disk with snapshots can't be compacted, because compacting would remove them. Delete its snapshots first (Settings > Recovery)."); return; }
            long free = -1;
            try { free = new DriveInfo(Path.GetPathRoot(path)).AvailableFreeSpace; } catch { }
            if (free >= 0 && free < disk.Actual) { Error("Compacting needs room for a second copy while it works: " + DiskSize(disk.Actual) + ", but drive " + Path.GetPathRoot(path).TrimEnd('\\') + " has " + DiskSize(free) + " free."); return; }
            if (!ThemedDialog.Show(this, "Compact " + diskName(index) + "?",
                    "Try Linux rewrites the disk file without its unused space. Linux and your files are unchanged.", null,
                    "It needs " + DiskSize(disk.Actual) + " free while it works" + (reclaim.Checked ? "." : ". Turn on Reclaim free space and start Linux once first to free the most."),
                    "Compact", "Cancel")) return;
            if (!store()) return;
            RunTool(cfg, "Compacting " + diskName(index) + " of " + who, () => CompactDisk(path, diskName(index)));
        };

        removeBtn.Click += delegate
        {
            int index = diskPick.SelectedIndex;
            if (index <= 0) return;
            if (running.ContainsKey(cfg.Name)) { Error("Shut down " + who + " before removing a disk."); return; }
            string entry = cfg.ExtraDisks[index - 1], path = cfg.DiskPath(entry);
            bool ours = !Path.IsPathRooted(entry);
            if (!ThemedDialog.Show(this, "Remove " + diskName(index) + " from " + who + "?",
                    ours ? "Its file goes to the Recycle Bin, with everything Linux stored on it." : "The disk file is kept where it is:",
                    ours ? null : path,
                    ours ? "To get it back, restore the file from the Recycle Bin and add it with Add disk." : "You can add it again with Add disk.",
                    "Remove", "Cancel", ours)) return;
            try
            {
                if (ours && File.Exists(path))
                    Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                        Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                cfg.ExtraDisks.RemoveAt(index - 1);
                saveDisks();
                fillDisks(index - 1);
                Info(diskName(index) + " was removed from " + who + ".");
            }
            catch (Exception ex) { Error("Couldn't remove the disk.\n\n" + ex.Message); }
        };

        // What each closed Hardware section holds, on the right of its heading.
        hardware.Summary("Graphics", () => displays.Value + (displays.Value == 1 ? " display" : " displays") + "  ·  " +
            (rGpu.Checked ? "GPU" : rCpu.Checked ? "CPU" : "Automatic") + " rendering" + (venus.Checked ? "  ·  Vulkan, " + gpuMem.Value + " GiB" : ""));
        hardware.Summary("Resources", () => VmConfig.ProfileNames[Math.Max(0, Math.Min(2, profile.SelectedIndex))] + "  ·  " + cores.Value + " cores  ·  " + ram.Value + " GiB");
        hardware.Summary("Disks", () =>
        {
            int n = cfg.AllDisks().Count;
            return n + (n == 1 ? " disk" : " disks") + "  ·  Disk " + (Math.Max(0, diskPick.SelectedIndex) + 1) + ": " + curSize.Text + (maxSize.Text.Length > 0 ? " of " + maxSize.Text : "");
        });
        hardware.Summary("Disk utilities", () => "Add or remove disks");
        hardware.Layout();

        // ---- Devices
        devices.Section("Microphone and sound");
        var sound = devices.Check("Enable sound", cfg.Sound);
        var mic = devices.Check("Allow microphone access (next start)", cfg.Mic);
        devices.Note("Turning microphone access off keeps sound playback enabled.");
        var outDev = devices.Row("Sound output", Sheet.Combo("Windows default"), 220);
        var inDev = devices.Row("Microphone", Sheet.Combo("Windows default"), 220);
        outDev.Enabled = inDev.Enabled = false;
        devices.Note("This graphics engine doesn't support separate audio choices.\nWindows defaults are used.", 2);
        var soundBtns = devices.Buttons(Sheet.Align.Left, "Windows sound devices", "Microphone privacy");
        devices.Note("Choose Windows playback and recording defaults before launching.\nIf a choice isn't picked up, restart " + who + ".", 2);
        sound.CheckedChanged += delegate { mic.Enabled = sound.Checked; };
        mic.Enabled = sound.Checked;
        soundBtns[0].Click += delegate { OpenUri("ms-settings:sound"); };
        soundBtns[1].Click += delegate { OpenUri("ms-settings:privacy-microphone"); };

        devices.Section("CD/DVD drive");
        var cd = Sheet.Input(cfg.Cd.Length > 0 ? cfg.Cd : "Empty");
        cd.ReadOnly = true;
        var insertBtn = new FlatBtn("Insert");
        var ejectBtn = new FlatBtn("Eject");
        devices.RowOf("Disc", Tuple.Create((Control)cd, 130), Tuple.Create((Control)insertBtn, 130), Tuple.Create((Control)ejectBtn, 130));
        insertBtn.Width = Theme.D(Sheet.SnugWidth("Insert"));
        ejectBtn.Width = Theme.D(Sheet.SnugWidth("Eject"));
        Sheet.PinRight(ejectBtn, 0);
        Sheet.PinRight(insertBtn, Sheet.SnugWidth("Eject") + 10, cd);
        devices.Note("Put an ISO in the drive, such as drivers or a rescue disk. The disc changes right\naway while " + who + " runs. It still starts from Disk 1; to start from a disc, use Recovery.", 2);
        ejectBtn.Enabled = cfg.Cd.Length > 0;
        // Like a real drive, a disc goes in or out at once, without Save.
        Action<string> setDisc = iso =>
        {
            RunningVm vm;
            string failed = null;
            if (running.TryGetValue(cfg.Name, out vm) && !vm.Held)
                try
                {
                    if (iso.Length > 0)
                        Net.Qmp(vm.QmpPort, "blockdev-change-medium", "{\"id\":\"cd\",\"filename\":\"" + iso.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\",\"format\":\"raw\",\"read-only-mode\":\"read-only\"}");
                    else Net.Qmp(vm.QmpPort, "eject", "{\"id\":\"cd\",\"force\":true}");
                }
                catch (Exception ex) { failed = ex.Message; }
            cfg.Cd = iso;
            var saved = VmConfig.Load(cfg.Dir); saved.Cd = iso; saved.Save();
            cd.Text = iso.Length > 0 ? iso : "Empty";
            ejectBtn.Enabled = iso.Length > 0;
            if (failed != null) Info("The drive changes the next time " + who + " starts. (" + failed + ")");
        };
        insertBtn.Click += delegate
        {
            using (var dlg = new OpenFileDialog { Filter = "Disc images (*.iso)|*.iso|All files|*.*", Title = "Insert a disc into " + who })
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    if (dlg.FileName.Contains(",")) { Error("QEMU can't open a disc whose path contains a comma."); return; }
                    setDisc(dlg.FileName);
                }
        };
        ejectBtn.Click += delegate { setDisc(""); };

        devices.Section("Camera and USB");
        devices.Note("WINQ-EMU doesn't include camera or USB passthrough, so Windows cameras\nand USB devices can't be attached. Use the shared folder to move files.", 2);

        devices.Section("Network");
        Action<bool> forwardsEnabled = on => { };
        var netType = devices.Row("Network type", Sheet.Combo(VmConfig.NetTypeNames), 220);
        netType.SelectedIndex = Math.Max(0, Math.Min(2, cfg.NetType));
        var netNote = devices.Note("", 2);
        Action showNet = () =>
        {
            netNote.Text = netType.SelectedIndex == 0 ? "Linux reaches the internet through this PC's connection.\nChanges apply the next time it starts."
                : netType.SelectedIndex == 1 ? "Linux can't reach the internet, your network or this PC, but the port forwards\nbelow still reach it. Changes apply the next time it starts."
                : "No network card: Linux is fully offline and port forwards are off.\nChanges apply the next time it starts.";
            forwardsEnabled(netType.SelectedIndex != 2);
        };
        var forwards = devices.Row("Port forwards", Sheet.Input(string.Join(Environment.NewLine, cfg.ForwardLines()), true), 410, 90);
        devices.Note("One per line. tcp:HOST:GUEST is reachable from this PC only;\nlan tcp:HOST:GUEST also accepts other devices on your network\n(Windows Firewall asks the first time). SSH uses the rule for guest port 22.", 3);
        forwardsEnabled = on => forwards.Enabled = on;
        netType.SelectedIndexChanged += delegate { showNet(); };
        showNet();

        general.Section("Shared folder");
        var shared = Sheet.Input(cfg.SharedFolder);
        var browse = new FlatBtn("Browse");
        general.RowOf("Shared folder", Tuple.Create((Control)shared, 270), Tuple.Create((Control)browse, 130));
        browse.Width = Theme.D(Sheet.SnugWidth("Browse"));
        // Browse sits against the bar's right edge and the folder box takes the room up to it.
        Sheet.PinRight(browse, 0, shared);
        var write = general.Check("Allow Linux to read and change this folder", cfg.SharedWrite, 190);
        general.Note("Off shares it read-only. In Linux: sudo mount -t 9p -o trans=virtio host /mnt/host", 1, 190);
        browse.Click += delegate
        {
            using (var dlg = new FolderBrowserDialog { Description = "Choose a Windows folder to share with " + who })
                if (dlg.ShowDialog(this) == DialogResult.OK) shared.Text = dlg.SelectedPath;
        };

        // ---- About (on General)
        general.Section("About");
        general.Note("Try Linux " + Version + "\nWINQ-EMU: " + (Winq.Ready ? (Winq.Version != null ? Winq.Version + ", " : "") + Winq.Bin : "not installed") +
            (Updater.WinqAvailable != null ? "  ·  " + Updater.WinqAvailable + " available" : "") + "\nStorage: " + AppSettings.Storage, 3);
        var aboutBtns = SelfInstall.IsInstalledCopy
            ? general.Buttons(Sheet.Align.Left, "WINQ-EMU releases", "Open storage folder")
            : general.Buttons(Sheet.Align.Left, "WINQ-EMU releases", "Open storage folder", "Install");
        aboutBtns[0].Click += delegate { OpenUri("https://github.com/cmspam/winq-emu/releases"); };
        aboutBtns[1].Click += delegate { Directory.CreateDirectory(AppSettings.Storage); Process.Start("explorer.exe", Q(AppSettings.Storage)); };
        if (aboutBtns.Count > 2) aboutBtns[2].Click += delegate
        {
            if (!ThemedDialog.Show(this, "Install Try Linux?",
                    "Try Linux copies itself to this folder and adds itself to the Start menu, so you can delete the downloaded file:", SelfInstall.Dir,
                    "Uninstall it any time from Windows Settings > Apps > Installed apps.", "Install", "Cancel")) return;
            try { SelfInstall.Install(); }
            catch (Exception ex) { ThemedDialog.Show(this, "Couldn't install Try Linux", ex.Message, null, null, "OK"); return; }
            // Open switches to the installed copy; closing the window keeps using this one for now.
            if (!ThemedDialog.Show(this, "Try Linux is installed",
                    "Try Linux is now available in the Windows Start menu.", null, null, "Open")) return;
            if (running.Count > 0 && !ThemedDialog.Show(this, "Open the installed copy?",
                    "Running systems keep running, but this copy's tray menu closes, so shut them down from inside Linux.",
                    null, null, "Open", "Back")) return;
            Process.Start(SelfInstall.Exe);
            quitting = true;
            Close();
        };

        var autoUpdate = general.Check("Check for updates automatically (once a day)", AppSettings.CheckUpdates);
        var updateRow = general.Buttons(Sheet.Align.Left, Updater.Available != null ? "Update to " + Updater.Available : "Check for updates", "Update WINQ-EMU");
        var updateBtn = updateRow[0];
        var winqBtn = updateRow[1];
        winqBtn.Visible = Updater.WinqAvailable != null;
        winqBtn.Click += delegate { StartWinqUpdate(); };
        updateBtn.Click += async (s, e) =>
        {
            if (Updater.Available != null) { StartUpdate(); return; }
            updateBtn.Enabled = false;
            updateBtn.Text = "Checking...";
            string found = null, failed = null;
            try { found = await Task.Run(() => Updater.Check(true)); }
            catch (Exception ex) { failed = ex.Message; }
            if (updateBtn.IsDisposed) return;
            updateBtn.Enabled = true;
            updateBtn.Text = "Check for updates";
            if (failed != null) { ThemedDialog.Show(this, "Couldn't check for updates", failed, null, null, "OK"); return; }
            winqBtn.Visible = Updater.WinqAvailable != null;
            if (found == null && Updater.WinqAvailable == null)
            {
                ThemedDialog.Show(this, "Try Linux is up to date", "You have the latest Try Linux (" + Version + ")" +
                    (Winq.Version != null ? " and WINQ-EMU (" + Winq.Version + ")." : "."), null, null, "OK");
                return;
            }
            Updater.Available = found;
            if (found != null) updateBtn.Text = "Update to " + found;
            UpdateClicked();
        };

        // ---- Recovery
        // What each closed General and Devices section holds.
        general.Summary("Display and startup", () => (full.Checked ? "Fullscreen" : "In a window") + (login.Checked ? "  ·  Starts when you sign in" : ""));
        general.Summary("Shared folder", () => shared.Text.Trim().Length == 0 ? "Not shared"
            : Path.GetFileName(shared.Text.Trim().TrimEnd('\\')) + (write.Checked ? "  ·  Read and write" : "  ·  Read-only"));
        general.Summary("About", () => "Try Linux " + Version + (Updater.Available != null ? "  ·  " + Updater.Available + " available" : ""));
        devices.Summary("Microphone and sound", () => !sound.Checked ? "Sound off" : mic.Checked ? "Sound and microphone on" : "Sound on, microphone off");
        devices.Summary("Camera and USB", () => "Not available in WINQ-EMU");
        devices.Summary("CD/DVD drive", () => cfg.Cd.Length == 0 ? "Empty" : cfg.Cd.Substring(cfg.Cd.LastIndexOfAny(new[] { '\\', '/' }) + 1));
        devices.Summary("Network", () =>
        {
            if (netType.SelectedIndex == 2) return "No network";
            int n = forwards.Text.Split('\n').Count(l => l.Trim().Length > 0);
            return (netType.SelectedIndex == 1 ? "Port forwards only  ·  " : "Shared (NAT)  ·  ") + (n == 0 ? "No port forwards" : n + (n == 1 ? " port forward" : " port forwards"));
        });        general.Layout();
        devices.Layout();

        recovery.Section("Backup and restore");
        var backupBtn = recovery.Action("Save a copy of " + who + ", its disks and its settings.", "Back up");
        var restoreBtn = recovery.Action("Restore a backup as a separate copy; the original stays.", "Restore");
        recovery.Note("Shut " + who + " down before backing it up.", 1);

        recovery.Section("Snapshots");
        var snapBtn = recovery.Action("Save Disk 1 as it is now, or go back to a saved point.", "Snapshots");

        recovery.Section("Storage");
        var moveBtn = recovery.Action("Move all systems and downloads to another folder.", "Move storage");
        var cleanBtn = recovery.Action("Delete installers that no system still needs.", "Clean up");

        recovery.Section("Repair");
        var resetBtn = recovery.Action("Erase " + who + " and run its installer again.", "Reset system");
        var bootBtn = recovery.Action("Start " + who + " from an ISO, such as a rescue disk.", "Boot an ISO");

        recovery.Section("Delete");
        var deleteBtn = recovery.Action("Remove " + who + " and everything installed in it.", "Delete");
        deleteBtn.ForeColor = Theme.Danger;
        deleteBtn.FlatAppearance.BorderColor = Theme.Danger;
        var rec1 = new List<Button> { backupBtn, restoreBtn, snapBtn, resetBtn };
        var rec2 = new List<Button> { bootBtn, moveBtn, cleanBtn, deleteBtn };

        recovery.Summary("Backup and restore", () => "Back up, or restore a copy");
        recovery.Summary("Snapshots", () => "Points in time for Disk 1");
        recovery.Summary("Repair",() => "Reset, or boot an ISO");
        recovery.Summary("Storage", () => "Move storage, or clean up downloads");
        recovery.Summary("Delete", () => "Remove " + who);
        recovery.Layout();

        // ---- Footer
        var back = new FlatBtn("Back");
        back.SetBounds(0, Theme.D(8), Theme.D(120), Theme.D(42));
        // Save is the page's main action; start a system from Your systems.
        var save = new FlatBtn("Save", true);
        save.SetBounds(Theme.D(520), Theme.D(8), Theme.D(120), Theme.D(42));   // the same size as Back
        footer.Controls.AddRange(new Control[] { back, save });

        // Copies the form into cfg; false (after telling the user) when something is invalid.
        store = () =>
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
            cfg.SharedFolder = sf;
            cfg.SharedWrite = write.Checked;
            cfg.Sound = sound.Checked;
            cfg.Mic = mic.Checked;
            cfg.Displays = (int)displays.Value;
            cfg.Render = rGpu.Checked ? 1 : rCpu.Checked ? 2 : 0;
            cfg.Venus = venus.Checked;
            cfg.GpuMemGB = (int)gpuMem.Value;
            cfg.Forwards = string.Join(";", fwd);
            cfg.Reclaim = reclaim.Checked;
            cfg.NetType = netType.SelectedIndex;
            cfg.Save();
            if (AppSettings.CheckUpdates != autoUpdate.Checked)
            {
                AppSettings.CheckUpdates = autoUpdate.Checked;
                try { AppSettings.Save(); } catch { }
            }
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

    // Asks for another disk: (size in GB, null) for a new empty disk, (0, path) to attach a file; null means cancelled.
    Tuple<int, string> AskNewDisk()
    {
        Func<int, int> D = Theme.D;
        int w = D(560), pad = D(24), textX = D(80), textW = w - textX - pad;
        string file = null;
        using (var f = new DialogForm { Text = "Try Linux", StartPosition = FormStartPosition.CenterParent,
            BackColor = Theme.Bg, ForeColor = Theme.Text, Font = Theme.Body, ShowInTaskbar = false })
        {
            var icon = new PictureBox { Image = Icons.Draw(D(40)), SizeMode = PictureBoxSizeMode.AutoSize, BackColor = Theme.Bg };
            icon.Location = new Point(pad, pad);
            int y = pad;
            Func<string, Font, Color, int, Label> label = (text, font, color, h) =>
            {
                var l = new Label { Text = text, Font = font, ForeColor = color, BackColor = Theme.Bg, UseMnemonic = false, AutoEllipsis = true };
                l.SetBounds(textX, y, textW, h);
                f.Controls.Add(l);
                y += h;
                return l;
            };
            label("Add a disk", Theme.Title, Theme.Text, D(30));
            y += D(14);

            label("A new empty disk", Theme.Heading, Theme.Text, D(22));
            y += D(6);
            var size = Sys.DiskBox(1, 4096, 64);
            size.SetBounds(textX, y, D(110), D(28));
            f.Controls.Add(size);
            var unit = new Label { Text = "GB maximum size, takes no space until Linux uses it", Font = Theme.Small, ForeColor = Theme.Muted, BackColor = Theme.Bg, UseMnemonic = false, TextAlign = ContentAlignment.MiddleLeft };
            unit.SetBounds(textX + D(120), y, textW - D(120), D(28));
            f.Controls.Add(unit);
            y += D(28) + D(16);

            var or = new Panel { BackColor = Theme.Bg };
            or.SetBounds(textX, y, textW, D(20));
            or.Paint += (s, e) =>
            {
                var g = e.Graphics;
                var sz = TextRenderer.MeasureText(g, "OR", Theme.Small, System.Drawing.Size.Empty, TextFormatFlags.NoPadding);
                int mid = or.Height / 2, gap = D(10), left = (or.Width - sz.Width) / 2;
                using (var p = new Pen(Theme.Border))
                {
                    g.DrawLine(p, 0, mid, left - gap, mid);
                    g.DrawLine(p, left + sz.Width + gap, mid, or.Width, mid);
                }
                TextRenderer.DrawText(g, "OR", Theme.Small, new Rectangle(0, 0, or.Width, or.Height), Theme.Muted,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            };
            f.Controls.Add(or);
            y += D(20) + D(10);

            label("An existing disk file", Theme.Heading, Theme.Text, D(22));
            y += D(6);
            var pick = new FlatBtn("Choose a disk file...");
            pick.SetBounds(textX, y, D(200), D(34));
            f.Controls.Add(pick);
            y += D(34) + D(6);
            var chosen = label("No file chosen. Try Linux uses .qcow2 disks; the file stays where it is.", Theme.Small, Theme.Muted, D(20));
            y += D(20);

            int bw = D(150), bh = D(38);
            var ok = new FlatBtn("Add", true);
            ok.SetBounds(w - pad - bw, y, bw, bh);
            var cancel = new FlatBtn("Cancel") { DialogResult = DialogResult.Cancel };
            cancel.SetBounds(w - pad - bw * 2 - D(10), y, bw, bh);
            f.Controls.Add(ok);
            f.Controls.Add(cancel);
            f.Controls.Add(icon);
            f.ClientSize = new System.Drawing.Size(w, y + bh + pad);
            foreach (Control c in f.Controls) if (!(c is Button) && !(c is ComboBox)) f.DragBy(c);

            Action refresh = () =>
            {
                chosen.Text = file != null ? Path.GetFileName(file) : "No file chosen. Try Linux uses .qcow2 disks; the file stays where it is.";
                chosen.ForeColor = file != null ? Theme.Highlight : Theme.Muted;
                unit.ForeColor = file != null ? Theme.Border : Theme.Muted;
                size.ForeColor = file != null ? Theme.Border : Theme.Text;
                ok.Text = file != null ? "Add this disk" : "Add new disk";
            };
            // Picking a file chooses it; touching the size goes back to a new disk.
            size.ValueChanged += delegate { file = null; refresh(); };
            size.Enter += delegate { if (file != null) { file = null; refresh(); } };
            pick.Click += delegate
            {
                using (var dlg = new OpenFileDialog { Filter = "QEMU disks (*.qcow2)|*.qcow2", Title = "Choose a disk to add" })
                {
                    if (dlg.ShowDialog(f) != DialogResult.OK) return;
                    file = dlg.FileName;
                    refresh();
                }
            };
            ok.Click += delegate { f.DialogResult = DialogResult.OK; };
            f.CancelButton = cancel;
            f.AcceptButton = ok;
            refresh();
            if (f.ShowDialog(this) != DialogResult.OK) return null;
            return file != null ? Tuple.Create(0, file) : Tuple.Create((int)size.Value, (string)null);
        }
    }

    // Asks for a disk's new maximum size in GB; 0 means cancelled.
    int AskDiskSize(string name, DiskInfo info)
    {
        Func<int, int> D = Theme.D;
        int w = D(560), pad = D(24), textX = D(80), textW = w - textX - pad;
        int current = (int)Math.Ceiling(info.Virtual / 1073741824.0);
        // Never below what Linux has already written, plus a little room.
        int min = Math.Max(1, (int)Math.Ceiling(info.Actual / 1073741824.0) + 1);
        using (var f = new DialogForm { Text = "Try Linux", StartPosition = FormStartPosition.CenterParent,
            BackColor = Theme.Bg, ForeColor = Theme.Text, Font = Theme.Body, ShowInTaskbar = false })
        {
            var icon = new PictureBox { Image = Icons.Draw(D(40)), SizeMode = PictureBoxSizeMode.AutoSize, BackColor = Theme.Bg };
            icon.Location = new Point(pad, pad);
            int y = pad;
            Func<string, Font, Color, int, Label> label = (text, font, color, h) =>
            {
                var l = new Label { Text = text, Font = font, ForeColor = color, BackColor = Theme.Bg, UseMnemonic = false };
                l.SetBounds(textX, y, textW, h);
                f.Controls.Add(l);
                y += h;
                return l;
            };
            label("Resize " + name, Theme.Title, Theme.Text, D(30));
            y += D(6);
            label("Current size " + DiskSize(info.Actual) + ", maximum size " + DiskSize(info.Virtual) + ".", Theme.Body, Theme.Muted, D(24));
            y += D(14);
            label("New maximum size (GB)", Theme.Heading, Theme.Text, D(22));
            y += D(6);
            var size = Sys.DiskBox(min, 4096, current);
            size.SetBounds(textX, y, D(110), D(28));
            f.Controls.Add(size);
            y += D(28) + D(10);
            var note = label("", Theme.Small, Theme.Muted, D(58));
            y += D(14);

            int bw = D(150), bh = D(38);
            var ok = new FlatBtn("Resize", true) { Enabled = false };
            ok.SetBounds(w - pad - bw, y, bw, bh);
            var cancel = new FlatBtn("Cancel") { DialogResult = DialogResult.Cancel };
            cancel.SetBounds(w - pad - bw * 2 - D(10), y, bw, bh);
            f.Controls.Add(ok);
            f.Controls.Add(cancel);
            f.Controls.Add(icon);
            f.ClientSize = new Size(w, y + bh + pad);
            foreach (Control c in f.Controls) if (!(c is Button) && !(c is ComboBox)) f.DragBy(c);

            Action refresh = () =>
            {
                int gb = (int)size.Value;
                ok.Enabled = gb != current;
                if (gb > current)
                {
                    note.Text = "Expands the disk by " + (gb - current) + " GB. It takes no space until Linux uses it. " +
                        "Linux may need to expand its partition to use the new space (with GNOME Disks or GParted).";
                    note.ForeColor = Theme.Muted;
                }
                else if (gb < current)
                {
                    note.Text = "Shrinking cuts " + (current - gb) + " GB off the end of the disk. Shrink Linux's partition to fit first " +
                        "(with GParted from a live ISO, for example), or Linux may not start. Back it up in Settings > Recovery.";
                    note.ForeColor = Theme.Warning;
                }
                else { note.Text = "Choose a larger size to expand the disk, or a smaller one to shrink it."; note.ForeColor = Theme.Muted; }
            };
            size.ValueChanged += delegate { refresh(); };
            size.KeyUp += delegate { refresh(); };
            refresh();
            ok.Click += delegate { f.DialogResult = DialogResult.OK; };
            f.CancelButton = cancel;
            f.AcceptButton = ok;
            if (f.ShowDialog(this) != DialogResult.OK) return 0;
            int result = (int)size.Value;
            if (result < current && !ThemedDialog.Show(this, "Shrink " + name + " to " + result + " GB?",
                "Anything Linux stored past " + result + " GB is lost. Only continue if you've shrunk its partition to fit.",
                null, null, "Shrink", "Cancel", true)) return 0;
            return result;
        }
    }

    // Sizes as Windows shows them: MB under a gigabyte, GB above (binary units).
    static string DiskSize(long bytes)
    {
        if (bytes < 0) return "?";
        if (bytes < 1L << 30) return (bytes / 1048576.0).ToString(bytes < 10L << 20 ? "0.0" : "0") + " MB";
        return (bytes / 1073741824.0).ToString("0.0") + " GB";
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
            RunTool(cfg, "Backing up " + cfg.DisplayName, () => BackupTo(cfg, dest));
        }
    }

    static string BackupTo(VmConfig cfg, string dest)
    {
        // Disk 1 goes to the chosen file; each other disk goes next to it as <name>-disk2.qcow2 and so on.
        var all = cfg.AllDisks();
        long total = 0;
        for (int i = 0; i < all.Count; i++)
        {
            if (i > 0 && !File.Exists(all[i])) continue;
            string to = BackupDiskPath(dest, i);
            if (File.Exists(to)) File.Delete(to);
            Winq.Run(Winq.Img, "convert -c -O qcow2 " + Q(all[i]) + " " + Q(to));
            total += new FileInfo(to).Length;
        }
        File.Copy(Path.Combine(cfg.Dir, "vm.ini"), dest + ".ini", true);
        return "Backed up " + cfg.DisplayName + (all.Count > 1 ? " and its " + all.Count + " disks" : "") + " to\n" + dest + "\n(" + DiskSize(total) + ", compressed).";
    }

    void Restore(VmConfig cfg)
    {
        using (var dlg = new OpenFileDialog { Filter = "Try Linux backup (*.qcow2)|*.qcow2", Title = "Restore a backup as a new system" })
        {
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            string src = dlg.FileName;
            var copy = RestoreTarget(cfg, src);
            RunTool(cfg, "Restoring a copy of " + cfg.DisplayName, () => RestoreFrom(copy, src));
        }
    }

    // The new system a backup is restored as, from the settings saved with it.
    VmConfig RestoreTarget(VmConfig cfg, string src)
    {
        string id = UniqueName(cfg.Name + "-restored");
        var copy = File.Exists(src + ".ini") ? VmConfig.LoadIni(src + ".ini") : VmConfig.Load(cfg.Dir);
        copy.Dir = Path.Combine(AppSettings.VmsDir, id);
        copy.Name = id;
        copy.DisplayName = copy.DisplayName + " (restored " + File.GetLastWriteTime(src).ToString("yyyy-MM-dd") + ")";
        copy.PendingInstall = false;
        return copy;
    }

    static string RestoreFrom(VmConfig copy, string src)
    {
        Directory.CreateDirectory(copy.Dir);
        Winq.Run(Winq.Img, "convert -O qcow2 " + Q(src) + " " + Q(copy.Disk));
        // The other disks were backed up next to it; the copy gets its own files for them.
        int count = copy.ExtraDisks.Count;
        copy.ExtraDisks.Clear();
        for (int i = 1; i <= count; i++)
        {
            string from = BackupDiskPath(src, i);
            if (!File.Exists(from)) continue;
            string name = copy.NewDiskName();
            Winq.Run(Winq.Img, "convert -O qcow2 " + Q(from) + " " + Q(Path.Combine(copy.Dir, name)));
            copy.ExtraDisks.Add(name);
        }
        copy.Save();
        return "Restored as \"" + copy.DisplayName + "\". It uses the same port forwards as the original, so run one at a time or change them in Settings.";
    }

    // Rewrites a disk without its unused clusters; the original stays until the copy is checked.
    static string CompactDisk(string path, string name)
    {
        string temp = path + ".compacting";
        long before = new FileInfo(path).Length;
        if (File.Exists(temp)) File.Delete(temp);
        try
        {
            Winq.Run(Winq.Img, "convert -O qcow2 " + Q(path) + " " + Q(temp));
            Winq.Run(Winq.Img, "compare -q " + Q(path) + " " + Q(temp));   // same contents, or stop here
        }
        catch { try { File.Delete(temp); } catch { } throw; }
        File.Delete(path);
        File.Move(temp, path);
        long after = new FileInfo(path).Length;
        return name + " was compacted from " + DiskSize(before) + " to " + DiskSize(after) +
            (before - after > (1L << 20) ? ", giving " + DiskSize(before - after) + " back to Windows." : ". There was little unused space to give back.");
    }

    // Where a backup keeps disk number i (0 is Disk 1, the chosen file itself).
    static string BackupDiskPath(string backup, int i)
    {
        if (i == 0) return backup;
        return Path.Combine(Path.GetDirectoryName(backup), Path.GetFileNameWithoutExtension(backup) + "-disk" + (i + 1) + ".qcow2");
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
        if (!ThemedDialog.Show(this, "Reset " + cfg.DisplayName + "?",
                "This erases everything installed in it, including its snapshots, and starts over with the installer.", null,
                "Back it up first if you might need anything.", "Reset", "Cancel", true)) return;
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
            return cfg.DisplayName + " was reset. Click Install on Virtual Machines to run the installer.";
        });
    }

    void DeleteSystem(VmConfig cfg)
    {
        bool attached = cfg.ExtraDisks.Any(Path.IsPathRooted);
        if (!ThemedDialog.Show(this, "Delete " + cfg.DisplayName + "?",
                "This permanently removes its virtual " + (cfg.ExtraDisks.Count > 0 ? "disks" : "disk") + " and everything installed in it.", null,
                attached ? "Disks added from other folders and downloaded ISOs are kept." : "Downloaded ISOs are kept.", "Delete", "Cancel", true)) return;
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
        if (files.Count == 0)
        {
            ThemedDialog.Show(this, "Nothing to clean up", "Installers that a system still needs are kept.", null, null, "OK");
            return;
        }
        long total = files.Sum(f => f.Length);
        if (!ThemedDialog.Show(this, "Delete " + files.Count + " downloaded " + (files.Count == 1 ? "file" : "files") + "?",
                Gib(total) + " in all:",
                string.Join("\n", files.Take(8).Select(f => f.Name)) + (files.Count > 8 ? "\n... and " + (files.Count - 8) + " more" : ""),
                "You can download them again later.", "Delete", "Cancel", true)) return;
        int failed = 0;
        foreach (var f in files) try { f.Delete(); } catch { failed++; }
        ThemedDialog.Show(this, "Freed " + Gib(total),
            failed > 0 ? failed + " file(s) were in use and kept." : null, null, null, "OK");
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
            if (s != null && Ask("Revert to this snapshot?", s, "Changes made to " + cfg.DisplayName + " since then are lost.", "Revert"))
                Act(() => Winq.Run(Winq.Img, "snapshot -a " + MainForm.Q(s) + " " + MainForm.Q(cfg.Disk)));
        };
        delete.Click += delegate
        {
            string s = Selected();
            if (s != null && Ask("Delete this snapshot?", s, "It can't be restored afterwards.", "Delete"))
                Act(() => Winq.Run(Winq.Img, "snapshot -d " + MainForm.Q(s) + " " + MainForm.Q(cfg.Disk)));
        };
        Load += delegate { Reload(); };
    }

    string Selected()
    {
        return list.SelectedIndex >= 0 && list.SelectedIndex < snaps.Count ? snaps[list.SelectedIndex].Key : null;
    }

    bool Ask(string title, string snapshot, string text, string action)
    {
        return ThemedDialog.Show(this, title, null, snapshot, text, action, "Cancel", true);
    }

    void Act(Action a)
    {
        Cursor = Cursors.WaitCursor;
        try { a(); }
        catch (Exception ex) { ThemedDialog.Show(this, "Snapshots", ex.Message, null, null, "OK"); }
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
