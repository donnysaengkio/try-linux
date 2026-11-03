// Try Linux: installs and runs Linux distributions in a VM on Windows, using
// WINQ-EMU (QEMU on the Windows Hypervisor Platform with Venus/VirGL graphics).
// Build with build.cmd (uses the C# compiler that ships with Windows).
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--write-icon") { Icons.WriteIco(args[1]); return; }
        try { Native.SetProcessDPIAware(); } catch { }
        ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        if (args.Length == 2 && args[0] == "--resolve-test") { Resolver.Test(args[1]); return; }
        if (args.Length == 2 && args[0] == "--virt-test") { Virt.Test(args[1]); return; }

        using (var g = Graphics.FromHwnd(IntPtr.Zero)) Theme.Scale = g.DpiX / 96f;
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.ThreadException += (s, e) =>
            MessageBox.Show(e.Exception.Message, "Try Linux", MessageBoxButtons.OK, MessageBoxIcon.Error);

        string start = null;
        for (int i = 0; i < args.Length - 1; i++) if (args[i] == "--start") start = args[i + 1];
        Application.Run(new MainForm(start));
    }
}

// ---------------------------------------------------------------- catalog

class Distro
{
    public string Id, Name, Blurb, Base, Checksum;
    // Each step is appended to Base. A step starting with ~ is a regex matched
    // against the links in the current directory listing; the newest match wins.
    public string[] Steps;
    public int DiskGB = 64;

    public static readonly Distro[] Catalog = {
        new Distro {
            Id = "fedora", Name = "Fedora Workstation",
            Blurb = "A community distribution sponsored by Red Hat that ships new GNOME, kernel and Mesa releases soon after they come out",
            Base = "https://mirrors.kernel.org/fedora/releases/",
            Steps = new[] { @"~^\d+/$", "Workstation/x86_64/iso/", @"~^Fedora-Workstation-Live-.*\.iso$" },
            Checksum = "~CHECKSUM$" },
        new Distro {
            Id = "ubuntu", Name = "Ubuntu Desktop",
            Blurb = "The most widely used desktop Linux, made by Canonical, with long-term support releases and a large software catalog",
            Base = "https://releases.ubuntu.com/",
            Steps = new[] { @"~^\d\d\.\d\d(\.\d+)?/$", @"~^ubuntu-.*-desktop-amd64\.iso$" },
            Checksum = "SHA256SUMS" },
        new Distro {
            Id = "debian", Name = "Debian (GNOME)",
            Blurb = "One of the oldest community distributions, known for stability and the base of Ubuntu, Mint and many others",
            Base = "https://cdimage.debian.org/debian-cd/current-live/amd64/iso-hybrid/",
            Steps = new[] { @"~^debian-live-.*-amd64-gnome\.iso$" },
            Checksum = "SHA256SUMS" },
        new Distro {
            Id = "mint", Name = "Linux Mint (Cinnamon)",
            Blurb = "A beginner-friendly distribution based on Ubuntu, with the Windows-like Cinnamon desktop and media codecs available at install",
            Base = "https://mirrors.edge.kernel.org/linuxmint/stable/",
            Steps = new[] { @"~^\d+(\.\d+)?/$", @"~^linuxmint-.*-cinnamon-64bit\.iso$" },
            Checksum = "sha256sum.txt" },
        new Distro {
            Id = "zorin", Name = "Zorin OS Core",
            Blurb = "A polished distribution based on Ubuntu, designed to feel familiar to people moving from Windows",
            Base = "https://mirrors.edge.kernel.org/zorinos-isos/",
            Steps = new[] { @"~^\d+/$", @"~^Zorin-OS-(?<v>\d+(\.\d+)*)-Core-64-bit(-r\d+)?\.iso$" },
            Checksum = "SHA256SUMS.txt" },
    };

    public static Distro Find(string id)
    {
        return Catalog.FirstOrDefault(d => d.Id == id);
    }
}

// ---------------------------------------------------------------- storage

static class AppSettings
{
    static readonly string Ini = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TryLinux", "settings.ini");
    public static string Storage = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TryLinux");
    public static string VmsDir { get { return Path.Combine(Storage, "vms"); } }
    public static string IsoDir { get { return Path.Combine(Storage, "isos"); } }
    public static string DownloadsDir { get { return Path.Combine(Storage, "downloads"); } }

    public static void Load()
    {
        if (!File.Exists(Ini)) return;
        foreach (var line in File.ReadAllLines(Ini))
            if (line.StartsWith("storage=") && line.Length > 8) Storage = line.Substring(8).Trim();
    }

    public static void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Ini));
        File.WriteAllLines(Ini, new[] { "storage=" + Storage });
    }
}

class VmConfig
{
    public string Dir, Name, DisplayName = "", DistroId = "", Iso = "", SharedFolder = "";
    public int Profile;                 // 0 balanced, 1 maximum, 2 manual
    public int RamGB = 8, Cores = 4, GpuMemGB = 4, SshPort = 2223;
    public bool Venus = true, Fullscreen, PendingInstall;
    public int DiskGB;                  // requested capacity; 0 keeps the current size
    public int Displays = 1;
    public int Render;                  // 0 automatic, 1 GPU, 2 CPU
    public bool SharedWrite, Sound = true, Mic = true, ShortcutDirect = true;
    public string Forwards = "", FullscreenDisplay = "", LastRender = "";

    public string Disk { get { return Path.Combine(Dir, Name + ".qcow2"); } }
    string IniPath { get { return Path.Combine(Dir, "vm.ini"); } }

    public static readonly string[] ProfileNames = { "Balanced", "Maximum", "Manual" };

    // Port forward rules; systems set up before forwards existed get their SSH port.
    public string[] ForwardLines()
    {
        return Forwards.Length > 0 ? Forwards.Split(';') : new[] { "tcp:" + SshPort + ":22" };
    }

    public List<Forward> ForwardRules()
    {
        var list = new List<Forward>();
        foreach (var line in ForwardLines()) { Forward f; if (Forward.TryParse(line, out f)) list.Add(f); }
        return list;
    }

    public int SshHostPort
    {
        get { var f = ForwardRules().FirstOrDefault(r => r.Guest == 22 && r.Proto == "tcp"); return f != null ? f.Host : 0; }
    }

    public static VmConfig Load(string dir)
    {
        var c = LoadIni(Path.Combine(dir, "vm.ini"));
        c.Dir = dir;
        c.Name = Path.GetFileName(dir);
        if (c.DisplayName.Length == 0) c.DisplayName = c.Name;
        return c;
    }

    public static VmConfig LoadIni(string path)
    {
        var c = new VmConfig();
        if (!File.Exists(path)) return c;
        foreach (var line in File.ReadAllLines(path))
        {
            int i = line.IndexOf('=');
            if (i < 0) continue;
            string k = line.Substring(0, i).Trim(), v = line.Substring(i + 1).Trim();
            int n;
            bool isInt = int.TryParse(v, out n);
            switch (k)
            {
                case "name": c.DisplayName = v; break;
                case "distro": c.DistroId = v; break;
                case "iso": c.Iso = v; break;
                case "shared": c.SharedFolder = v; break;
                case "profile": if (isInt) c.Profile = n; break;
                case "ram": if (isInt) c.RamGB = n; break;
                case "cores": if (isInt) c.Cores = n; break;
                case "gpumem": if (isInt) c.GpuMemGB = n; break;
                case "sshport": if (isInt) c.SshPort = n; break;
                case "venus": c.Venus = v != "0"; break;
                case "fullscreen": c.Fullscreen = v == "1"; break;
                case "pending": c.PendingInstall = v == "1"; break;
                case "diskgb": if (isInt) c.DiskGB = n; break;
                case "displays": if (isInt) c.Displays = Math.Max(1, Math.Min(4, n)); break;
                case "render": if (isInt) c.Render = n; break;
                case "sharedwrite": c.SharedWrite = v == "1"; break;
                case "sound": c.Sound = v != "0"; break;
                case "mic": c.Mic = v != "0"; break;
                case "shortcutdirect": c.ShortcutDirect = v != "0"; break;
                case "forwards": c.Forwards = v; break;
                case "fsdisplay": c.FullscreenDisplay = v; break;
                case "lastrender": c.LastRender = v; break;
            }
        }
        return c;
    }

    public void Save()
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllLines(IniPath, new[] {
            "name=" + DisplayName, "distro=" + DistroId, "iso=" + Iso, "shared=" + SharedFolder,
            "profile=" + Profile, "ram=" + RamGB, "cores=" + Cores, "gpumem=" + GpuMemGB,
            "sshport=" + SshPort, "venus=" + (Venus ? "1" : "0"),
            "fullscreen=" + (Fullscreen ? "1" : "0"), "pending=" + (PendingInstall ? "1" : "0"),
            "diskgb=" + DiskGB, "displays=" + Displays, "render=" + Render,
            "sharedwrite=" + (SharedWrite ? "1" : "0"), "sound=" + (Sound ? "1" : "0"), "mic=" + (Mic ? "1" : "0"),
            "shortcutdirect=" + (ShortcutDirect ? "1" : "0"), "forwards=" + Forwards,
            "fsdisplay=" + FullscreenDisplay, "lastrender=" + LastRender });
    }

    public void Resources(out int cores, out int ramGB)
    {
        int n = Environment.ProcessorCount, total = Sys.TotalRamGB;
        if (Profile == 1) { cores = Math.Max(2, n - 2); ramGB = Math.Max(2, total * 3 / 4); }
        else if (Profile == 2) { cores = Math.Max(1, Math.Min(n, Cores)); ramGB = Math.Max(1, Math.Min(total, RamGB)); }
        else { cores = Math.Max(2, n / 2); ramGB = Math.Max(2, Math.Min(16, total / 2)); }
    }

    public static List<VmConfig> All()
    {
        var list = new List<VmConfig>();
        if (!Directory.Exists(AppSettings.VmsDir)) return list;
        foreach (var dir in Directory.GetDirectories(AppSettings.VmsDir))
        {
            var c = Load(dir);
            if (File.Exists(c.Disk)) list.Add(c);
        }
        list.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.CurrentCultureIgnoreCase));
        return list;
    }
}

static class Sys
{
    static int totalRam;
    public static int TotalRamGB
    {
        get
        {
            if (totalRam == 0)
                totalRam = Math.Max(2, (int)Math.Round(new Microsoft.VisualBasic.Devices.ComputerInfo().TotalPhysicalMemory / 1073741824.0));
            return totalRam;
        }
    }
}

// ---------------------------------------------------------------- WINQ-EMU

static class Winq
{
    public static string Bin;
    public static bool Ready { get { return Bin != null; } }
    public static string Qemu { get { return Path.Combine(Bin, "qemu-system-x86_64w.exe"); } }
    public static string Img { get { return Path.Combine(Bin, "qemu-img.exe"); } }

    public static void Detect()
    {
        Bin = null;
        foreach (var root in new[] { @"C:\WINQ-EMU",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WINQ-EMU") })
            foreach (var dir in new[] { Path.Combine(root, "bin"), root })
                if (Bin == null && File.Exists(Path.Combine(dir, "qemu-system-x86_64w.exe"))) Bin = dir;
    }

    public static string Run(string exe, string args)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        using (var p = Process.Start(psi))
        {
            var err = p.StandardError.ReadToEndAsync();
            string output = p.StandardOutput.ReadToEnd() + err.Result;
            p.WaitForExit();
            if (p.ExitCode != 0) throw new Exception(output.Trim());
            return output;
        }
    }
}

class RunningVm
{
    public VmConfig Cfg;
    public Process P;
    public int QmpPort;
    public bool Installer, Cpu;
    public DateTime Started = DateTime.Now;
    public readonly StringBuilder Err = new StringBuilder();
}

// ---------------------------------------------------------------- network

class ResolvedIso
{
    public string Url, FileName, Sha256;
    public long Size = -1;
}

static class Net
{
    public const string UserAgent = "TryLinux/1.0";

    public static WebClient Client()
    {
        var wc = new WebClient();
        wc.Headers[HttpRequestHeader.UserAgent] = UserAgent;
        return wc;
    }

    public static string GetString(string url)
    {
        using (var wc = Client()) return wc.DownloadString(url);
    }

    public static long HeadSize(string url)
    {
        try
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = "HEAD";
            req.UserAgent = UserAgent;
            req.Timeout = 30000;
            using (var resp = (HttpWebResponse)req.GetResponse()) return resp.ContentLength;
        }
        catch (WebException) { return -1; }
    }

    // Downloads to `part`, resuming a previous partial download when the server allows it.
    public static void Download(string url, string part, long expected, Func<bool> cancelled, Action<long, long, double> report)
    {
        long have = File.Exists(part) ? new FileInfo(part).Length : 0;
        if (expected > 0 && have == expected) return;
        if (expected > 0 && have > expected) { File.Delete(part); have = 0; }

        var req = (HttpWebRequest)WebRequest.Create(url);
        req.UserAgent = UserAgent;
        req.Timeout = 30000;
        req.ReadWriteTimeout = 60000;
        if (have > 0) req.AddRange(have);
        HttpWebResponse resp;
        try { resp = (HttpWebResponse)req.GetResponse(); }
        catch (WebException ex)
        {
            var r = ex.Response as HttpWebResponse;
            if (r != null && (int)r.StatusCode == 416 && have > 0) return;   // already complete
            throw;
        }
        using (resp)
        {
            bool append = have > 0 && resp.StatusCode == HttpStatusCode.PartialContent;
            if (!append) have = 0;
            long total = resp.ContentLength >= 0 ? resp.ContentLength + have : expected;
            using (var src = resp.GetResponseStream())
            using (var dst = new FileStream(part, append ? FileMode.Append : FileMode.Create, FileAccess.Write))
            {
                var buf = new byte[1 << 20];
                var clock = Stopwatch.StartNew();
                long startBytes = have, lastReport = -1000;
                int n;
                while ((n = src.Read(buf, 0, buf.Length)) > 0)
                {
                    if (cancelled()) throw new OperationCanceledException();
                    dst.Write(buf, 0, n);
                    have += n;
                    if (clock.ElapsedMilliseconds - lastReport >= 200)
                    {
                        lastReport = clock.ElapsedMilliseconds;
                        report(have, total, (have - startBytes) / Math.Max(0.001, clock.Elapsed.TotalSeconds));
                    }
                }
            }
            if (total > 0 && have < total) throw new Exception("The download was interrupted. Try again to resume it.");
        }
    }

    public static string Sha256(string file, Func<bool> cancelled, Action<double> report)
    {
        using (var sha = SHA256.Create())
        using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20))
        {
            var buf = new byte[1 << 20];
            long done = 0, len = Math.Max(1, fs.Length);
            int n, tick = 0;
            while ((n = fs.Read(buf, 0, buf.Length)) > 0)
            {
                if (cancelled()) throw new OperationCanceledException();
                sha.TransformBlock(buf, 0, n, null, 0);
                done += n;
                if (++tick % 32 == 0) report((double)done / len);
            }
            sha.TransformFinalBlock(buf, 0, 0);
            return BitConverter.ToString(sha.Hash).Replace("-", "").ToLowerInvariant();
        }
    }

    public static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    // Sends one QMP command (e.g. system_powerdown) to a running QEMU.
    public static void Qmp(int port, string command)
    {
        using (var c = new TcpClient("127.0.0.1", port))
        using (var s = c.GetStream())
        {
            s.ReadTimeout = 5000;
            var r = new StreamReader(s);
            var w = new StreamWriter(s) { AutoFlush = true, NewLine = "\n" };
            r.ReadLine();
            w.WriteLine("{\"execute\":\"qmp_capabilities\"}");
            r.ReadLine();
            w.WriteLine("{\"execute\":\"" + command + "\"}");
            r.ReadLine();
        }
    }
}

static class Resolver
{
    public static ResolvedIso Resolve(Distro d)
    {
        string url = Walk(d.Base, d.Steps, 0);
        if (url == null)
            throw new Exception("Couldn't find the current " + d.Name + " download. The mirror may be updating; try again later.");
        var r = new ResolvedIso { Url = url, FileName = LastSegment(url) };
        r.Size = Net.HeadSize(url);
        if (d.Checksum != null)
        {
            try { r.Sha256 = FindHash(url, d.Checksum, r.FileName); }
            catch (WebException) { }
        }
        return r;
    }

    public static ResolvedIso FromUrl(string url)
    {
        string name = LastSegment(url);
        if (!name.EndsWith(".iso", StringComparison.OrdinalIgnoreCase)) name += ".iso";
        return new ResolvedIso { Url = url, FileName = SafeFileName(name), Size = Net.HeadSize(url) };
    }

    static string Walk(string url, string[] steps, int i)
    {
        if (i == steps.Length) return url;
        string step = steps[i];
        if (!step.StartsWith("~")) return Walk(url + step, steps, i + 1);
        int tries = 0;
        // Newest first; fall back to an older entry when the newest isn't fully published yet.
        foreach (var m in Matches(url, step.Substring(1)))
        {
            if (tries++ == 3) break;
            try
            {
                string found = Walk(url + m, steps, i + 1);
                if (found != null) return found;
            }
            catch (WebException) { }
        }
        return null;
    }

    static List<string> Matches(string url, string pattern)
    {
        string html = Net.GetString(url);
        var found = new HashSet<string>();
        foreach (Match m in Regex.Matches(html, "href=\"([^\"?#]+)\"", RegexOptions.IgnoreCase))
        {
            string h = m.Groups[1].Value;
            bool dir = h.EndsWith("/");
            string seg = h.TrimEnd('/');
            seg = seg.Substring(seg.LastIndexOf('/') + 1);
            if (seg.Length == 0 || seg == "..") continue;
            if (dir) seg += "/";
            if (Regex.IsMatch(Uri.UnescapeDataString(seg), pattern)) found.Add(seg);
        }
        // A (?<v>...) group in the pattern marks the version; it outranks
        // the rest of the name (so Zorin 18.1 beats 18 re-spin r3).
        var re = new Regex(pattern);
        Func<string, string> version = s =>
        {
            var g = re.Match(Uri.UnescapeDataString(s)).Groups["v"];
            return g.Success ? g.Value : s;
        };
        var list = found.ToList();
        list.Sort((a, b) =>
        {
            int c = CompareVersions(version(a), version(b));
            return c != 0 ? c : CompareVersions(a, b);
        });
        list.Reverse();
        return list;
    }

    static int CompareVersions(string a, string b)
    {
        // Architecture tags are not version numbers.
        a = Regex.Replace(a, @"(x86_|amd)64|64-?bit", "", RegexOptions.IgnoreCase);
        b = Regex.Replace(b, @"(x86_|amd)64|64-?bit", "", RegexOptions.IgnoreCase);
        var na = Regex.Matches(a, @"\d+");
        var nb = Regex.Matches(b, @"\d+");
        for (int i = 0; i < Math.Min(na.Count, nb.Count); i++)
        {
            int c = decimal.Parse(na[i].Value).CompareTo(decimal.Parse(nb[i].Value));
            if (c != 0) return c;
        }
        if (na.Count != nb.Count) return na.Count.CompareTo(nb.Count);
        return string.CompareOrdinal(a, b);
    }

    static string FindHash(string isoUrl, string spec, string fileName)
    {
        string dir = isoUrl.Substring(0, isoUrl.LastIndexOf('/') + 1);
        string name;
        if (spec.StartsWith("~"))
        {
            name = Matches(dir, spec.Substring(1)).FirstOrDefault();
            if (name == null) return null;
        }
        else name = spec.Replace("{iso}", isoUrl.Substring(dir.Length));
        string text = Net.GetString(dir + name);
        var hex = new Regex(@"\b[0-9a-fA-F]{64}\b");
        foreach (var line in text.Split('\n'))
            if (line.Contains(fileName))
            {
                var m = hex.Match(line);
                if (m.Success) return m.Value.ToLowerInvariant();
            }
        // A checksum file for a single image (e.g. openSUSE's *-Current.iso.sha256).
        var all = hex.Matches(text).Cast<Match>().Select(m => m.Value.ToLowerInvariant()).Distinct().ToList();
        return all.Count == 1 ? all[0] : null;
    }

    static string LastSegment(string url)
    {
        string path = new Uri(url).AbsolutePath;
        return Uri.UnescapeDataString(path.Substring(path.LastIndexOf('/') + 1));
    }

    static string SafeFileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name;
    }

    // Diagnostic: --resolve-test <file> writes what each catalog entry resolves to.
    public static void Test(string outFile)
    {
        var sb = new StringBuilder();
        foreach (var d in Distro.Catalog)
        {
            try
            {
                var r = Resolve(d);
                sb.AppendLine(d.Id + "\t" + r.Url + "\t" + r.Size + "\t" + (r.Sha256 ?? "no-checksum"));
            }
            catch (Exception ex) { sb.AppendLine(d.Id + "\tERROR\t" + ex.Message); }
        }
        File.WriteAllText(outFile, sb.ToString());
    }
}

// ---------------------------------------------------------------- native

static class Native
{
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int value, int size);
    [DllImport("WinHvPlatform.dll")] static extern int WHvGetCapability(int code, out int buffer, uint size, out uint written);

    public static void BringToFront(IntPtr h)
    {
        if (h == IntPtr.Zero) return;
        if (IsIconic(h)) ShowWindow(h, 9);
        SetForegroundWindow(h);
    }

    public static void DarkTitleBar(IntPtr h)
    {
        int on = 1;
        try { DwmSetWindowAttribute(h, 20, ref on, 4); } catch { }
    }

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] static extern int SetWindowTheme(IntPtr h, string app, string idList);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);

    // Windows 10/11 dark scrollbars for a scrollable control.
    public static void DarkScrollbars(Control c)
    {
        EventHandler apply = delegate { try { SetWindowTheme(c.Handle, "DarkMode_Explorer", null); } catch { } };
        if (c.IsHandleCreated) apply(c, EventArgs.Empty); else c.HandleCreated += apply;
    }

    public static void MoveTo(IntPtr h, int x, int y)
    {
        SetWindowPos(h, IntPtr.Zero, x, y, 0, 0, 0x0001 | 0x0004);   // SWP_NOSIZE | SWP_NOZORDER
    }

    // QEMU's SDL window toggles fullscreen on Ctrl+Alt+F.
    public static void SendCtrlAltF(IntPtr h)
    {
        SetForegroundWindow(h);
        byte ctrl = 0x11, alt = 0x12, f = 0x46;
        keybd_event(ctrl, 0, 0, UIntPtr.Zero);
        keybd_event(alt, 0, 0, UIntPtr.Zero);
        keybd_event(f, 0, 0, UIntPtr.Zero);
        keybd_event(f, 0, 2, UIntPtr.Zero);
        keybd_event(alt, 0, 2, UIntPtr.Zero);
        keybd_event(ctrl, 0, 2, UIntPtr.Zero);
    }

    [DllImport("WinHvPlatform.dll")] static extern int WHvCreatePartition(out IntPtr partition);
    [DllImport("WinHvPlatform.dll")] static extern int WHvDeletePartition(IntPtr partition);

    // True when a hypervisor is running at all (Hyper-V counts, even without the WHPX API).
    public static bool HypervisorPresent()
    {
        try
        {
            int present; uint written;
            return WHvGetCapability(0, out present, 4, out written) >= 0 && present != 0;
        }
        catch (DllNotFoundException) { return false; }
        catch (EntryPointNotFoundException) { return false; }
    }

    // True when QEMU can use the Windows Hypervisor Platform: an empty VM partition can be created.
    public static bool HypervisorReady()
    {
        if (!HypervisorPresent()) return false;
        try
        {
            IntPtr part;
            if (WHvCreatePartition(out part) < 0) return false;
            WHvDeletePartition(part);
            return true;
        }
        catch (DllNotFoundException) { return false; }
        catch (EntryPointNotFoundException) { return false; }
    }
}

enum VirtState { Ready, NoCpuSupport, FirmwareOff, FeatureOff, NotRunning }

// Works out why the hypervisor isn't running, without needing admin rights.
static class Virt
{
    public static VirtState Check()
    {
        if (Native.HypervisorReady()) return VirtState.Ready;
        bool? feature = FeatureEnabled();
        // A hypervisor (e.g. Hyper-V) is running, so the CPU and BIOS are fine; only the API is missing.
        if (Native.HypervisorPresent()) return feature == true ? VirtState.NotRunning : VirtState.FeatureOff;
        // With no hypervisor running, Win32_Processor reports the CPU's own virtualization state.
        bool? cpu = ProcessorFlag("VMMonitorModeExtensions"), firmware = ProcessorFlag("VirtualizationFirmwareEnabled");
        if (cpu == false) return VirtState.NoCpuSupport;
        if (firmware == false) return VirtState.FirmwareOff;
        return feature == true ? VirtState.NotRunning : VirtState.FeatureOff;
    }

    static bool? ProcessorFlag(string name)
    {
        try
        {
            using (var s = new System.Management.ManagementObjectSearcher("SELECT " + name + " FROM Win32_Processor"))
                foreach (System.Management.ManagementObject o in s.Get())
                {
                    var v = o[name];
                    if (v is bool) return (bool)v;
                }
        }
        catch (Exception) { }
        return null;
    }

    // Windows Hypervisor Platform: true when on (or on after a pending restart), false when off.
    static bool? FeatureEnabled()
    {
        try
        {
            using (var s = new System.Management.ManagementObjectSearcher("SELECT InstallState FROM Win32_OptionalFeature WHERE Name='HypervisorPlatform'"))
                foreach (System.Management.ManagementObject o in s.Get())
                    return Convert.ToInt32(o["InstallState"]) == 1;
        }
        catch (Exception) { }
        return null;
    }

    public const string BiosSteps =
        "To turn it on:\n" +
        "1. Open Settings > System > Recovery, and next to Advanced startup click Restart now.\n" +
        "2. Choose Troubleshoot > Advanced options > UEFI Firmware Settings > Restart.\n" +
        "   (Or press Del, F2, F10 or Esc while the PC starts.)\n" +
        "3. Turn on Intel Virtualization Technology (VT-x) or AMD SVM Mode. It's often under Advanced, CPU Configuration or Security.\n" +
        "4. Save, exit, and open Try Linux again.";

    public static string Message(VirtState s)
    {
        switch (s)
        {
            case VirtState.NoCpuSupport:
                return "This PC's processor doesn't support hardware virtualization, which Try Linux needs to run Linux.";
            case VirtState.FirmwareOff:
                return "Hardware virtualization is turned off in your PC's BIOS/UEFI settings, so Linux can't run yet.\n\n" + BiosSteps;
            case VirtState.FeatureOff:
                return "Windows Hypervisor Platform is turned off. Turn it on in 'Turn Windows features on or off', restart, then try again.";
            case VirtState.NotRunning:
                return "Windows Hypervisor Platform is turned on but isn't running yet. Restart Windows, then try again.\n\n" +
                    "If this message comes back after restarting, hardware virtualization is probably turned off in your PC's BIOS/UEFI settings.\n\n" + BiosSteps;
        }
        return "";
    }

    // Diagnostic: --virt-test <file> writes what Try Linux detects on this PC.
    public static void Test(string outFile)
    {
        File.WriteAllText(outFile, "present=" + Native.HypervisorPresent() + "\nwhpx=" + Native.HypervisorReady() +
            "\ncpu=" + ProcessorFlag("VMMonitorModeExtensions") + "\nfirmware=" + ProcessorFlag("VirtualizationFirmwareEnabled") +
            "\nfeature=" + FeatureEnabled() + "\nstate=" + Check() + "\n");
    }
}

// ---------------------------------------------------------------- look

static class Theme
{
    public static float Scale = 1;
    public static readonly Color Bg = Color.FromArgb(20, 22, 28);
    public static readonly Color Card = Color.FromArgb(30, 33, 42);
    public static readonly Color CardHover = Color.FromArgb(38, 42, 54);
    public static readonly Color CardSelected = Color.FromArgb(40, 52, 62);
    public static readonly Color Border = Color.FromArgb(54, 60, 76);
    public static readonly Color Text = Color.FromArgb(216, 222, 233);
    public static readonly Color Muted = Color.FromArgb(132, 142, 162);
    public static readonly Color Accent = Color.FromArgb(136, 192, 208);
    public static readonly Color Accent2 = Color.FromArgb(129, 161, 193);
    public static readonly Color Danger = Color.FromArgb(220, 120, 128);

    static string family;
    static string Family
    {
        get
        {
            if (family == null)
            {
                using (var fonts = new InstalledFontCollection())
                    family = fonts.Families.Any(f => f.Name == "Cascadia Mono") ? "Cascadia Mono" : "Consolas";
            }
            return family;
        }
    }

    public static Font Mono(float size, FontStyle style = FontStyle.Regular) { return new Font(Family, size, style); }
    public static readonly Font Title = Mono(14f, FontStyle.Bold);
    public static readonly Font Body = Mono(10f);
    public static readonly Font Bold = Mono(10f, FontStyle.Bold);
    public static readonly Font Small = Mono(9f);

    public static int D(int v) { return (int)Math.Round(v * Scale); }
}

static class Icons
{
    static readonly string[] Glyph = {
        "........",
        "##......",
        ".##.....",
        "..##....",
        ".##.....",
        "##......",
        "....####",
        "........" };

    public static Bitmap Draw(int size)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float r = size * 0.22f;
            using (var path = new GraphicsPath())
            {
                path.AddArc(0, 0, r * 2, r * 2, 180, 90);
                path.AddArc(size - r * 2 - 1, 0, r * 2, r * 2, 270, 90);
                path.AddArc(size - r * 2 - 1, size - r * 2 - 1, r * 2, r * 2, 0, 90);
                path.AddArc(0, size - r * 2 - 1, r * 2, r * 2, 90, 90);
                path.CloseFigure();
                using (var b = new LinearGradientBrush(new Rectangle(0, 0, size, size), Theme.Accent, Theme.Accent2, 90f))
                    g.FillPath(b, path);
            }
            g.SmoothingMode = SmoothingMode.None;
            float cell = size / 10f, off = size * 0.1f;
            using (var b = new SolidBrush(Theme.Bg))
                for (int y = 0; y < 8; y++)
                    for (int x = 0; x < 8; x++)
                        if (Glyph[y][x] == '#') g.FillRectangle(b, off + x * cell, off + y * cell, cell + 0.5f, cell + 0.5f);
        }
        return bmp;
    }

    public static Icon App()
    {
        try
        {
            var icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            if (icon != null) return icon;
        }
        catch { }
        return Icon.FromHandle(Draw(32).GetHicon());
    }

    // Writes a multi-size .ico (PNG entries) for embedding into the exe at build time.
    public static void WriteIco(string path)
    {
        int[] sizes = { 16, 24, 32, 48, 256 };
        var pngs = sizes.Select(s => { using (var b = Draw(s)) using (var ms = new MemoryStream()) { b.Save(ms, ImageFormat.Png); return ms.ToArray(); } }).ToList();
        using (var w = new BinaryWriter(File.Create(path)))
        {
            w.Write((short)0); w.Write((short)1); w.Write((short)sizes.Length);
            int offset = 6 + 16 * sizes.Length;
            for (int i = 0; i < sizes.Length; i++)
            {
                w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                w.Write((byte)0); w.Write((byte)0);
                w.Write((short)1); w.Write((short)32);
                w.Write(pngs[i].Length); w.Write(offset);
                offset += pngs[i].Length;
            }
            foreach (var p in pngs) w.Write(p);
        }
    }
}

// ---------------------------------------------------------------- controls

class PixelLogo : Control
{
    static readonly Dictionary<char, string[]> Glyphs = new Dictionary<char, string[]> {
        { 'T', new[] { "#####", "..#..", "..#..", "..#..", "..#..", "..#..", "..#.." } },
        { 'R', new[] { "####.", "#...#", "#...#", "####.", "#.#..", "#..#.", "#...#" } },
        { 'Y', new[] { "#...#", "#...#", ".#.#.", "..#..", "..#..", "..#..", "..#.." } },
        { 'L', new[] { "#....", "#....", "#....", "#....", "#....", "#....", "#####" } },
        { 'I', new[] { "#####", "..#..", "..#..", "..#..", "..#..", "..#..", "#####" } },
        { 'N', new[] { "#...#", "##..#", "##..#", "#.#.#", "#..##", "#..##", "#...#" } },
        { 'U', new[] { "#...#", "#...#", "#...#", "#...#", "#...#", "#...#", ".###." } },
        { 'X', new[] { "#...#", "#...#", ".#.#.", "..#..", ".#.#.", "#...#", "#...#" } },
    };
    public string Word = "TRY LINUX";
    public int Px = 10;

    public PixelLogo()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Bg;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(BackColor);
        int px = Theme.D(Px);
        int cols = Word.Length * 6 - 1;
        int x0 = (Width - cols * px) / 2, y0 = (Height - 7 * px) / 2;
        int shadow = Math.Max(1, px / 3);
        using (var sb = new SolidBrush(Color.FromArgb(34, 52, 62)))
            DrawWord(e.Graphics, x0 + shadow, y0 + shadow, px, r => sb);
        var rows = Enumerable.Range(0, 7).Select(r => new SolidBrush(Lerp(Theme.Accent, Theme.Accent2, r / 6f))).ToArray();
        DrawWord(e.Graphics, x0, y0, px, r => rows[r]);
        foreach (var b in rows) b.Dispose();
    }

    void DrawWord(Graphics g, int x0, int y0, int px, Func<int, Brush> brush)
    {
        for (int i = 0; i < Word.Length; i++)
        {
            string[] glyph;
            if (!Glyphs.TryGetValue(Word[i], out glyph)) continue;
            for (int r = 0; r < 7; r++)
                for (int c = 0; c < 5; c++)
                    if (glyph[r][c] == '#') g.FillRectangle(brush(r), x0 + (i * 6 + c) * px, y0 + r * px, px, px);
        }
    }

    static Color Lerp(Color a, Color b, float t)
    {
        return Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
    }
}

class Bar : Control
{
    double value = -1;
    float phase;
    readonly Timer anim = new Timer { Interval = 30 };

    public Bar()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        anim.Tick += delegate { phase = (phase + 0.012f) % 1.3f; Invalidate(); };
        anim.Start();
    }

    // 0..1, or negative for an indeterminate (moving) bar.
    public double Value
    {
        get { return value; }
        set { this.value = value; anim.Enabled = value < 0; Invalidate(); }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.Card);
        using (var b = new SolidBrush(Theme.Accent))
        {
            if (value >= 0) g.FillRectangle(b, 0, 0, (float)(Width * Math.Min(1, value)), Height);
            else g.FillRectangle(b, (phase - 0.3f) * Width, 0, Width * 0.3f, Height);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) anim.Dispose();
        base.Dispose(disposing);
    }
}

class FlatBtn : Button
{
    public FlatBtn(string text, bool primary = false, bool danger = false)
    {
        Text = text;
        FlatStyle = FlatStyle.Flat;
        UseVisualStyleBackColor = false;
        Font = Theme.Bold;
        Cursor = Cursors.Hand;
        FlatAppearance.BorderSize = 1;
        if (primary)
        {
            BackColor = Theme.Accent;
            ForeColor = Theme.Bg;
            FlatAppearance.BorderColor = Theme.Accent;
            FlatAppearance.MouseOverBackColor = Color.FromArgb(160, 210, 224);
        }
        else
        {
            BackColor = Theme.Card;
            ForeColor = danger ? Theme.Danger : Theme.Text;
            FlatAppearance.BorderColor = danger ? Theme.Danger : Theme.Border;
            FlatAppearance.MouseOverBackColor = Theme.CardHover;
        }
    }
}

class OptionRow : Control
{
    public string Title = "", Sub = "";
    bool selected, hover;

    public OptionRow()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Cursor = Cursors.Hand;
    }

    public bool Selected { get { return selected; } set { selected = value; Invalidate(); } }
    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(selected ? Theme.CardSelected : hover ? Theme.CardHover : Theme.Card);
        using (var p = new Pen(selected ? Theme.Accent : Theme.Border)) g.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
        if (selected) using (var b = new SolidBrush(Theme.Accent)) g.FillRectangle(b, 0, 0, Theme.D(4), Height);
        int pad = Theme.D(18), half = Height / 2;
        var flags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
        if (Height < Theme.D(64))
        {
            TextRenderer.DrawText(g, Title, Theme.Bold, new Rectangle(pad, Theme.D(4), Width - pad * 2, half - Theme.D(2)), Theme.Text, flags);
            TextRenderer.DrawText(g, Sub, Theme.Small, new Rectangle(pad, half, Width - pad * 2, half - Theme.D(6)), Theme.Muted, flags);
            return;
        }
        // Tall rows: title on top, description wrapped onto the lines below.
        TextRenderer.DrawText(g, Title, Theme.Bold, new Rectangle(pad, Theme.D(8), Width - pad * 2, Theme.D(20)), Theme.Text, flags);
        TextRenderer.DrawText(g, Sub, Theme.Small, new Rectangle(pad, Theme.D(31), Width - pad * 2, Height - Theme.D(37)), Theme.Muted,
            TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }
}

class OptionList : Panel
{
    readonly List<OptionRow> rows = new List<OptionRow>();
    int selectedIndex = -1;
    public event EventHandler SelectionChanged, Activated;
    public int RowHeight = 54;          // design units; 72 fits a two-line description

    public OptionList()
    {
        AutoScroll = true;
        BackColor = Theme.Bg;
    }

    public void AddItem(string title, string sub, object tag)
    {
        var row = new OptionRow { Title = title, Sub = sub, Tag = tag };
        int index = rows.Count;
        row.MouseDown += delegate { SelectedIndex = index; };
        row.DoubleClick += delegate { if (Activated != null) Activated(this, EventArgs.Empty); };
        rows.Add(row);
        Controls.Add(row);
        LayoutRows();
    }

    protected override void OnResize(EventArgs e) { base.OnResize(e); LayoutRows(); }

    void LayoutRows()
    {
        int h = Theme.D(RowHeight), gap = Theme.D(6);
        bool scroll = rows.Count * (h + gap) > ClientSize.Height;
        int w = ClientSize.Width - (scroll ? SystemInformation.VerticalScrollBarWidth + Theme.D(4) : 0);
        for (int i = 0; i < rows.Count; i++) rows[i].SetBounds(0, AutoScrollPosition.Y + i * (h + gap), w, h);
    }

    public int SelectedIndex
    {
        get { return selectedIndex; }
        set
        {
            selectedIndex = value;
            for (int i = 0; i < rows.Count; i++) rows[i].Selected = i == value;
            if (value >= 0 && value < rows.Count) ScrollControlIntoView(rows[value]);
            if (SelectionChanged != null) SelectionChanged(this, EventArgs.Empty);
        }
    }

    public object SelectedTag { get { return selectedIndex >= 0 ? rows[selectedIndex].Tag : null; } }
    public int Count { get { return rows.Count; } }
}

class Segmented : Control
{
    public string[] Options = new string[0];
    int selectedIndex;
    public event EventHandler Changed;

    public Segmented()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Cursor = Cursors.Hand;
    }

    public int SelectedIndex
    {
        get { return selectedIndex; }
        set { selectedIndex = value; Invalidate(); if (Changed != null) Changed(this, EventArgs.Empty); }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (Options.Length > 0) SelectedIndex = Math.Min(Options.Length - 1, e.X * Options.Length / Math.Max(1, Width));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.Card);
        int n = Options.Length;
        for (int i = 0; i < n; i++)
        {
            var r = new Rectangle(i * Width / n, 0, (i + 1) * Width / n - i * Width / n, Height);
            if (i == selectedIndex) using (var b = new SolidBrush(Theme.Accent)) g.FillRectangle(b, r);
            TextRenderer.DrawText(g, Options[i], Theme.Bold, r, i == selectedIndex ? Theme.Bg : Theme.Text,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }
        using (var p = new Pen(Theme.Border)) g.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
    }
}

// A full-window page with a centered fixed-width column, like a setup screen.
class Page : Panel
{
    public readonly Panel Col = new Panel();
    // Set for pages whose column fills the window height (it lays out the column's contents).
    public Action Fill;

    public Page(int colHeight, bool logo = true)
    {
        Dock = DockStyle.Fill;
        BackColor = Theme.Bg;
        AutoScroll = logo;
        Col.BackColor = Theme.Bg;
        Col.Size = new Size(Theme.D(640), Theme.D(colHeight));
        Controls.Add(Col);
        if (logo) Add(new PixelLogo(), 0, 0, 640, 84);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        int x = Math.Max(0, (ClientSize.Width - Col.Width) / 2);
        if (Fill != null) { Col.Location = new Point(x, 0); Fill(); }
        else Col.Location = new Point(x, Math.Max(0, (ClientSize.Height - Col.Height) / 2));
    }

    public T Add<T>(T c, int x, int y, int w, int h) where T : Control
    {
        c.SetBounds(Theme.D(x), Theme.D(y), Theme.D(w), Theme.D(h));
        Col.Controls.Add(c);
        return c;
    }

    public Label Caption(string text, int x, int y, int w, int h, Font font = null, Color? color = null,
        ContentAlignment align = ContentAlignment.MiddleLeft)
    {
        return Add(new Label {
            Text = text, Font = font ?? Theme.Body, ForeColor = color ?? Theme.Text,
            BackColor = Theme.Bg, TextAlign = align, AutoEllipsis = true, UseMnemonic = false }, x, y, w, h);
    }

    public TextBox Input(string text, int x, int y, int w)
    {
        return Add(new TextBox { Text = text, Font = Theme.Body, BackColor = Theme.Card, ForeColor = Theme.Text, BorderStyle = BorderStyle.FixedSingle }, x, y, w, 26);
    }

    public NumericUpDown Number(int min, int max, int value, int x, int y, int w = 90)
    {
        var n = Add(new NumericUpDown { Minimum = min, Maximum = max, Font = Theme.Body, BackColor = Theme.Card, ForeColor = Theme.Text, BorderStyle = BorderStyle.FixedSingle }, x, y, w, 26);
        n.Value = Math.Max(min, Math.Min(max, value));
        return n;
    }

    public CheckBox Check(string text, bool on, int x, int y, int w)
    {
        var c = Add(new CheckBox { Text = text, Checked = on, Font = Theme.Body, ForeColor = Theme.Text, BackColor = Theme.Bg, FlatStyle = FlatStyle.Flat, UseMnemonic = false }, x, y, w, 26);
        c.FlatAppearance.BorderColor = Theme.Border;
        c.FlatAppearance.CheckedBackColor = Theme.Accent;
        return c;
    }
}

// Resource profile chooser shared by the setup and settings pages.
class ResourcePicker
{
    Segmented profile;
    NumericUpDown ram, cores;
    Label summary;

    public void Attach(Page p, int y, VmConfig c)
    {
        p.Caption("Resources", 0, y, 180, 32);
        profile = p.Add(new Segmented { Options = VmConfig.ProfileNames }, 190, y, 450, 32);
        p.Caption("Memory GB", 190, y + 44, 100, 26, Theme.Small, Theme.Muted);
        ram = p.Number(1, Sys.TotalRamGB, c.RamGB, 290, y + 44);
        p.Caption("Cores", 410, y + 44, 70, 26, Theme.Small, Theme.Muted);
        cores = p.Number(1, Environment.ProcessorCount, c.Cores, 480, y + 44);
        summary = p.Caption("", 190, y + 76, 450, 24, Theme.Small, Theme.Muted);
        profile.Changed += delegate { Refresh(); };
        ram.ValueChanged += delegate { Refresh(); };
        cores.ValueChanged += delegate { Refresh(); };
        profile.SelectedIndex = Math.Max(0, Math.Min(2, c.Profile));
    }

    void Refresh()
    {
        bool manual = profile.SelectedIndex == 2;
        ram.Enabled = cores.Enabled = manual;
        var probe = new VmConfig();
        Store(probe);
        int n, gb;
        probe.Resources(out n, out gb);
        summary.Text = "Uses " + n + " of " + Environment.ProcessorCount + " cores and " + gb + " of " + Sys.TotalRamGB + " GB memory";
    }

    public void Store(VmConfig c)
    {
        c.Profile = profile.SelectedIndex;
        c.RamGB = (int)ram.Value;
        c.Cores = (int)cores.Value;
    }
}

// ---------------------------------------------------------------- main window

class InstallPlan
{
    public Distro Distro;
    public string LocalIso, Url, Label;
}

partial class MainForm : Form
{
    static readonly string[] Tips = {
        "Ctrl+Alt+G releases the mouse and keyboard back to Windows.",
        "Ctrl+Alt+F switches the VM window in and out of fullscreen.",
        "The installer only sees the virtual disk, a file on your PC. Your Windows drive is never touched.",
        "Right-click the Try Linux tray icon to shut a system down cleanly.",
        "Chromium-based browsers can show black video in the VM. Firefox works best.",
        "Settings can share a Windows folder with Linux and add a desktop shortcut.",
    };

    readonly Dictionary<string, RunningVm> running = new Dictionary<string, RunningVm>();
    readonly NotifyIcon tray;
    readonly string startArg;
    bool busy, cancelRequested, quitting, trayHintShown, restartPending;
    Page page;
    Label pStatus, pDetail, pTip;
    Bar pBar;
    Timer tipTimer;

    static int D(int v) { return Theme.D(v); }

    public MainForm(string startVm)
    {
        startArg = startVm;
        Text = "Try Linux";
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        Font = Theme.Body;
        Icon = Icons.App();
        StartPosition = FormStartPosition.CenterScreen;
        var area = System.Windows.Forms.Screen.PrimaryScreen.WorkingArea;
        ClientSize = new Size(Math.Min(D(980), area.Width - 40), Math.Min(D(640), area.Height - 60));
        MinimumSize = new Size(D(700), D(520));

        tray = new NotifyIcon { Icon = Icon, Text = "Try Linux", Visible = true, ContextMenuStrip = new ContextMenuStrip() };
        tray.ContextMenuStrip.Opening += delegate { BuildTrayMenu(); };
        tray.DoubleClick += delegate { ShowLauncher(); };

        AppSettings.Load();
        Winq.Detect();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.DarkTitleBar(Handle);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (startArg != null)
        {
            var cfg = VmConfig.All().FirstOrDefault(c => c.Name.Equals(startArg, StringComparison.OrdinalIgnoreCase));
            if (cfg != null) { Boot(cfg); return; }
        }
        ShowHome();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!quitting && e.CloseReason == CloseReason.UserClosing)
        {
            if (running.Count > 0)
            {
                e.Cancel = true;
                HideToTray("Try Linux is still running", "Your system keeps running. Right-click this icon to shut it down.");
                return;
            }
            if (busy && !ConfirmCancel()) { e.Cancel = true; return; }
        }
        cancelRequested = true;
        tray.Visible = false;
        tray.Dispose();
        base.OnFormClosing(e);
    }

    void SetPage(Page p)
    {
        if (tipTimer != null) { tipTimer.Dispose(); tipTimer = null; }
        SuspendLayout();
        var old = page;
        page = p;
        Controls.Add(p);
        if (old != null) { Controls.Remove(old); old.Dispose(); }
        ResumeLayout();
    }

    // ------------------------------------------------------------ pages

    void ShowHome()
    {
        var vms = VmConfig.All();
        if (vms.Count == 0) { ShowChoose(); return; }

        var p = new Page(520);
        p.Caption("Your systems", 0, 100, 640, 30, Theme.Title);
        var list = p.Add(new OptionList(), 0, 140, 640, 270);
        foreach (var vm in vms) list.AddItem(vm.DisplayName, Describe(vm), vm);

        var add = p.Add(new FlatBtn("Add a system"), 0, 428, 170, 42);
        var settings = p.Add(new FlatBtn("Settings"), 182, 428, 140, 42);
        var start = p.Add(new FlatBtn("Start", true), 470, 428, 170, 42);
        p.Caption("Double-click a system to start it.", 0, 482, 640, 24, Theme.Small, Theme.Muted);

        list.SelectionChanged += delegate
        {
            var vm = (VmConfig)list.SelectedTag;
            if (vm == null) return;
            start.Text = running.ContainsKey(vm.Name) ? "Show" : vm.PendingInstall ? "Install" : "Start";
        };
        list.SelectedIndex = 0;
        EventHandler go = delegate { var vm = (VmConfig)list.SelectedTag; if (vm != null) Boot(vm); };
        list.Activated += go;
        start.Click += go;
        add.Click += delegate { ShowChoose(); };
        settings.Click += delegate { var vm = (VmConfig)list.SelectedTag; if (vm != null) ShowSettings(vm); };
        AcceptButton = start;
        SetPage(p);
    }

    string Describe(VmConfig vm)
    {
        int cores, ram;
        vm.Resources(out cores, out ram);
        var d = Distro.Find(vm.DistroId);
        string what = d != null ? d.Name : Path.GetFileName(vm.Iso);
        string state = running.ContainsKey(vm.Name) ? "running" : vm.PendingInstall ? "ready to install" : "";
        return string.Join("  ·  ", new[] { what, ram + " GB, " + cores + " cores", state }.Where(s => !string.IsNullOrEmpty(s)));
    }

    void ShowChoose()
    {
        bool hasVms = VmConfig.All().Count > 0;
        var p = new Page(604);
        p.Caption("Choose a Linux to try", 0, 98, 640, 30, Theme.Title);
        p.Caption("Try Linux downloads the official installer and checks it before use.", 0, 128, 640, 22, Theme.Small, Theme.Muted);
        int listH = Distro.Catalog.Length * 78 - 6;
        var list = p.Add(new OptionList { RowHeight = 72 }, 0, 158, 640, listH);
        foreach (var d in Distro.Catalog) list.AddItem(d.Name, d.Blurb, d);
        list.SelectedIndex = 0;

        // Bottom row: Back, the two other install sources, Next.
        int y = 158 + listH + 14;
        if (hasVms)
        {
            var back = p.Add(new FlatBtn("Back"), 0, y, 110, 42);
            back.Click += delegate { ShowHome(); };
        }
        var fromFile = p.Add(new FlatBtn("From an ISO file..."), 122, y, 190, 42);
        var fromUrl = p.Add(new FlatBtn("From a link..."), 322, y, 150, 42);
        var next = p.Add(new FlatBtn("Next", true), 500, y, 140, 42);
        Action<object> choose = tag =>
        {
            var plan = new InstallPlan();
            if (tag is Distro) { plan.Distro = (Distro)tag; plan.Label = plan.Distro.Name; }
            else if ((string)tag == "file")
            {
                using (var dlg = new OpenFileDialog { Filter = "Disk images (*.iso)|*.iso|All files|*.*", Title = "Choose a Linux installer" })
                {
                    if (dlg.ShowDialog(this) != DialogResult.OK) return;
                    plan.LocalIso = dlg.FileName;
                    plan.Label = Path.GetFileNameWithoutExtension(dlg.FileName);
                }
            }
            else
            {
                string url = Prompt("Download from a link", "Address of the .iso file (https://...)");
                if (url == null) return;
                Uri u;
                if (!Uri.TryCreate(url, UriKind.Absolute, out u) || (u.Scheme != "https" && u.Scheme != "http"))
                {
                    Error("That isn't a web address. It should start with https://");
                    return;
                }
                plan.Url = url;
                plan.Label = Path.GetFileNameWithoutExtension(u.AbsolutePath);
            }
            ShowOptions(plan);
        };
        EventHandler go = delegate { choose(list.SelectedTag); };
        next.Click += go;
        list.Activated += go;
        fromFile.Click += delegate { choose("file"); };
        fromUrl.Click += delegate { choose("url"); };
        AcceptButton = next;
        SetPage(p);
    }

    void ShowOptions(InstallPlan plan)
    {
        var existing = VmConfig.All();
        var cfg = new VmConfig { Profile = 0, RamGB = 8, Cores = Math.Max(2, Environment.ProcessorCount / 2) };
        cfg.SshPort = existing.Count == 0 ? 2223 : Math.Max(2222, existing.Max(v => v.SshHostPort)) + 1;

        var p = new Page(560);
        p.Caption("Set up " + plan.Label, 0, 100, 640, 30, Theme.Title);
        p.Caption("Name", 0, 146, 180, 26);
        var name = p.Input(UniqueName(plan.Distro != null ? plan.Distro.Id : plan.Label), 190, 146, 250);

        var res = new ResourcePicker();
        res.Attach(p, 188, cfg);

        p.Caption("Disk size", 0, 300, 180, 26);
        var disk = p.Number(16, 2048, plan.Distro != null ? plan.Distro.DiskGB : 64, 190, 300);
        p.Caption("GB, grows only as you use it", 290, 300, 350, 26, Theme.Small, Theme.Muted);

        p.Caption("Storage", 0, 342, 180, 26);
        TextBox storage = null;
        if (existing.Count == 0)
        {
            storage = p.Input(AppSettings.Storage, 190, 342, 330);
            var change = p.Add(new FlatBtn("Change"), 530, 340, 110, 30);
            change.Click += delegate
            {
                using (var dlg = new FolderBrowserDialog { Description = "Where should Try Linux keep its systems and downloads?" })
                    if (dlg.ShowDialog(this) == DialogResult.OK) storage.Text = Path.Combine(dlg.SelectedPath, "TryLinux");
            };
        }
        else p.Caption(AppSettings.Storage, 190, 342, 450, 26, Theme.Small, Theme.Muted);

        p.Caption("The installer only sees a virtual disk. Your Windows drive is never touched.", 0, 392, 640, 40, Theme.Small, Theme.Muted);

        var back = p.Add(new FlatBtn("Back"), 0, 470, 120, 42);
        back.Click += delegate { ShowChoose(); };
        var install = p.Add(new FlatBtn("Install", true), 500, 470, 140, 42);
        install.Click += delegate
        {
            string id = name.Text.Trim();
            if (!Regex.IsMatch(id, "^[A-Za-z0-9_-]{1,40}$")) { Error("Use 1-40 letters, numbers, - or _ for the name."); return; }
            if (storage != null)
            {
                string s = storage.Text.Trim();
                if (s.Length < 3 || !Path.IsPathRooted(s)) { Error("Choose a full folder path for storage."); return; }
                AppSettings.Storage = s;
                AppSettings.Save();
            }
            cfg.Name = id;
            cfg.Dir = Path.Combine(AppSettings.VmsDir, id);
            if (File.Exists(cfg.Disk)) { Error("A system named '" + id + "' already exists."); return; }
            cfg.DisplayName = plan.Distro != null ? plan.Distro.Name : plan.Label;
            if (existing.Any(v => v.DisplayName == cfg.DisplayName)) cfg.DisplayName += " (" + id + ")";
            cfg.DistroId = plan.Distro != null ? plan.Distro.Id : "";
            res.Store(cfg);
            Install(plan, cfg, (int)disk.Value);
        };
        AcceptButton = install;
        SetPage(p);
    }

    string UniqueName(string seed)
    {
        string baseName = Regex.Replace(seed.ToLowerInvariant(), "[^a-z0-9_-]+", "-").Trim('-');
        if (baseName.Length == 0) baseName = "linux";
        if (baseName.Length > 32) baseName = baseName.Substring(0, 32);
        string n = baseName;
        for (int i = 2; File.Exists(Path.Combine(AppSettings.VmsDir, n, n + ".qcow2")); i++) n = baseName + "-" + i;
        return n;
    }

    void ShowProgress(string status, bool cancellable)
    {
        var p = new Page(380);
        pStatus = p.Caption(status, 0, 124, 640, 28, Theme.Body, Theme.Text, ContentAlignment.MiddleCenter);
        pBar = p.Add(new Bar(), 120, 164, 400, 10);
        pDetail = p.Caption("", 0, 184, 640, 22, Theme.Small, Theme.Muted, ContentAlignment.MiddleCenter);
        pTip = p.Caption("", 40, 228, 560, 44, Theme.Small, Theme.Accent, ContentAlignment.TopCenter);
        if (cancellable)
        {
            var cancel = p.Add(new FlatBtn("Cancel"), 260, 300, 120, 38);
            cancel.Click += delegate { if (ConfirmCancel()) { cancelRequested = true; SetProgress("Stopping...", -1, ""); } };
        }
        AcceptButton = null;
        SetPage(p);
        int tip = new Random().Next(Tips.Length);
        pTip.Text = "Tip: " + Tips[tip];
        tipTimer = new Timer { Interval = 7000 };
        tipTimer.Tick += delegate { tip = (tip + 1) % Tips.Length; pTip.Text = "Tip: " + Tips[tip]; };
        tipTimer.Start();
    }

    void SetProgress(string status, double value, string detail)
    {
        if (pStatus == null || pStatus.IsDisposed) return;
        pStatus.Text = status;
        pBar.Value = value;
        pDetail.Text = detail;
    }

    bool ConfirmCancel()
    {
        return MessageBox.Show(this, "Stop setting up?\n\nAnything already downloaded is kept, so next time it picks up where it left off.",
            "Try Linux", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
    }

    // ------------------------------------------------------------ install

    async void Install(InstallPlan plan, VmConfig cfg, int diskGB)
    {
        var virt = Virt.Check();
        if (virt == VirtState.NoCpuSupport) { Error(Virt.Message(virt)); return; }
        if (virt == VirtState.FirmwareOff)
        {
            // Setup can still download now, so it's ready once the BIOS setting is changed.
            if (MessageBox.Show(this, Virt.Message(virt) + "\n\nDownload and prepare " + cfg.DisplayName + " now anyway, so it's ready after you change the setting?",
                    "Try Linux", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        }
        else if (virt == VirtState.FeatureOff && !restartPending && !await EnableHypervisor()) return;
        else if (virt == VirtState.NotRunning) restartPending = true;

        busy = true;
        cancelRequested = false;
        ShowProgress("Getting ready", true);
        Func<bool> cancelled = () => cancelRequested;
        try
        {
            if (!Winq.Ready) await InstallWinq(cancelled);

            string iso = plan.LocalIso;
            if (iso == null)
            {
                SetProgress("Finding the latest " + plan.Label, -1, plan.Distro != null ? new Uri(plan.Distro.Base).Host : new Uri(plan.Url).Host);
                var r = await Task.Run(() => plan.Distro != null ? Resolver.Resolve(plan.Distro) : Resolver.FromUrl(plan.Url));
                if (cancelRequested) throw new OperationCanceledException();
                iso = Path.Combine(AppSettings.IsoDir, r.FileName);
                if (!File.Exists(iso))
                {
                    Directory.CreateDirectory(AppSettings.IsoDir);
                    string part = iso + ".part";
                    string what = "Downloading " + r.FileName;
                    SetProgress(what, r.Size > 0 ? 0 : -1, "Connecting...");
                    await Task.Run(() => Net.Download(r.Url, part, r.Size, cancelled,
                        (got, total, rate) => Ui(() => SetProgress(what, total > 0 ? (double)got / total : -1, Transfer(got, total, rate)))));
                    if (r.Sha256 != null)
                    {
                        SetProgress("Verifying the download", 0, "Comparing with the official SHA-256 checksum");
                        string hash = await Task.Run(() => Net.Sha256(part, cancelled,
                            f => Ui(() => SetProgress("Verifying the download", f, "Comparing with the official SHA-256 checksum"))));
                        if (hash != r.Sha256)
                        {
                            File.Delete(part);
                            throw new Exception("The download didn't match its official checksum, so it was deleted. Try again.");
                        }
                    }
                    if (File.Exists(iso)) File.Delete(iso);
                    File.Move(part, iso);
                }
            }

            SetProgress("Creating the virtual disk", -1, diskGB + " GB, grows only as you use it");
            Directory.CreateDirectory(cfg.Dir);
            await Task.Run(() => Winq.Run(Winq.Img, "create -f qcow2 " + Q(cfg.Disk) + " " + diskGB + "G"));
            cfg.Iso = iso;
            cfg.PendingInstall = true;
            cfg.Save();
            busy = false;

            var state = Virt.Check();
            if (state != VirtState.Ready)
            {
                ShowHome();
                Info(cfg.DisplayName + " is ready to install. When Linux can run, open Try Linux and click Install.\n\n" +
                    (state == VirtState.FeatureOff || state == VirtState.NotRunning
                        ? "Restart Windows to finish turning on Windows Hypervisor Platform."
                          + (state == VirtState.NotRunning ? "\n\nIf Try Linux still can't start it after restarting, hardware virtualization is probably turned off in your PC's BIOS/UEFI settings.\n\n" + Virt.BiosSteps : "")
                        : Virt.Message(state)));
                return;
            }
            Boot(cfg);
        }
        catch (OperationCanceledException) { busy = false; ShowHome(); }
        catch (Exception ex) { busy = false; ShowHome(); Error(ex.Message); }
    }

    async Task<bool> EnableHypervisor()
    {
        if (MessageBox.Show(this, "Try Linux needs Windows Hypervisor Platform, which is turned off.\n\nTurn it on now? Windows asks for permission, then needs one restart. Setup can keep downloading in the meantime.",
                "Try Linux", MessageBoxButtons.YesNo, MessageBoxIcon.Information) != DialogResult.Yes) return false;
        int code;
        try
        {
            var p = Process.Start(new ProcessStartInfo("dism.exe", "/online /enable-feature /featurename:HypervisorPlatform /all /norestart /quiet")
            {
                UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden
            });
            code = await Task.Run(() => { p.WaitForExit(); return p.ExitCode; });
        }
        catch (Win32Exception) { return false; }   // the permission prompt was declined
        if (code == 0 || code == 3010) { restartPending = true; return true; }
        Error("Windows couldn't turn on Windows Hypervisor Platform (code " + code + ").\n\nIf hardware virtualization is turned off in your PC's BIOS/UEFI settings, turn it on there first.\n\n" + Virt.BiosSteps);
        return false;
    }

    async Task InstallWinq(Func<bool> cancelled)
    {
        if (MessageBox.Show(this, "Try Linux runs systems with WINQ-EMU, a free QEMU build with GPU acceleration for Windows. It isn't installed yet.\n\nDownload and install it now (about 55 MB)?",
                "Try Linux", MessageBoxButtons.YesNo, MessageBoxIcon.Information) != DialogResult.Yes)
            throw new OperationCanceledException();
        SetProgress("Downloading WINQ-EMU", -1, "github.com/cmspam/winq-emu");
        string url = await Task.Run(() =>
        {
            string json = Net.GetString("https://api.github.com/repos/cmspam/winq-emu/releases/latest");
            var m = Regex.Match(json, "\"browser_download_url\"\\s*:\\s*\"([^\"]+-Setup\\.exe)\"", RegexOptions.IgnoreCase);
            if (!m.Success) throw new Exception("Couldn't find the WINQ-EMU installer on GitHub. Install it from github.com/cmspam/winq-emu/releases, then try again.");
            return m.Groups[1].Value;
        });
        Directory.CreateDirectory(AppSettings.DownloadsDir);
        string file = Path.Combine(AppSettings.DownloadsDir, Path.GetFileName(new Uri(url).AbsolutePath));
        if (!File.Exists(file))
        {
            await Task.Run(() => Net.Download(url, file + ".part", -1, cancelled,
                (got, total, rate) => Ui(() => SetProgress("Downloading WINQ-EMU", total > 0 ? (double)got / total : -1, Transfer(got, total, rate)))));
            File.Move(file + ".part", file);
        }
        SetProgress("Installing WINQ-EMU", -1, "Approve the Windows prompt, then finish the installer");
        try
        {
            var p = Process.Start(new ProcessStartInfo(file) { UseShellExecute = true, Verb = "runas" });
            await Task.Run(() => p.WaitForExit());
        }
        catch (Win32Exception) { throw new Exception("WINQ-EMU wasn't installed, so setup stopped."); }
        Winq.Detect();
        if (!Winq.Ready) throw new Exception("WINQ-EMU wasn't found in C:\\WINQ-EMU after installing. Install it there, then try again.");
    }

    static string Transfer(long got, long total, double rate)
    {
        string s = FormatBytes(got) + (total > 0 ? " of " + FormatBytes(total) : "") + "  ·  " + FormatBytes((long)rate) + "/s";
        if (total > 0 && rate > 1)
        {
            double secs = (total - got) / rate;
            s += "  ·  " + (secs < 60 ? "under a minute" : Math.Ceiling(secs / 60) + " min") + " left";
        }
        return s;
    }

    static string FormatBytes(long bytes)
    {
        if (bytes >= 1L << 30) return (bytes / 1073741824.0).ToString("0.00") + " GB";
        if (bytes >= 1L << 20) return (bytes / 1048576.0).ToString("0.0") + " MB";
        return (bytes / 1024) + " KB";
    }

    // ------------------------------------------------------------ running

    void Boot(VmConfig cfg) { Boot(cfg, cfg.Render == 2); }

    void Boot(VmConfig cfg, bool cpu)
    {
        RunningVm existing;
        if (running.TryGetValue(cfg.Name, out existing)) { ShowVm(existing); return; }
        if (!Winq.Ready)
        {
            Error("WINQ-EMU isn't installed. Add a system to install it, or get it from github.com/cmspam/winq-emu/releases.");
            return;
        }
        var virt = Virt.Check();
        if (virt != VirtState.Ready) { Error(cfg.DisplayName + " can't start yet.\n\n" + Virt.Message(virt)); return; }
        bool installer = cfg.PendingInstall && File.Exists(cfg.Iso);
        ShowProgress("Starting " + cfg.DisplayName, false);
        SetProgress("Starting " + cfg.DisplayName, -1,
            string.Join("  ·  ", new[] { installer ? "Booting the installer" : "", cpu ? "CPU rendering" : "" }.Where(s => s.Length > 0)));
        GrowDisk(cfg);
        var vm = Launch(cfg, installer, cpu);
        if (vm == null) { ShowHome(); return; }

        var poll = new Timer { Interval = 250 };
        poll.Tick += delegate
        {
            vm.P.Refresh();
            if (vm.P.HasExited) { poll.Dispose(); return; }
            if (vm.P.MainWindowHandle != IntPtr.Zero || (DateTime.Now - vm.Started).TotalSeconds > 60)
            {
                poll.Dispose();
                IntPtr h = vm.P.MainWindowHandle;
                Native.BringToFront(h);
                RecordRender(vm);
                var target = FullscreenScreen(cfg);
                if (cfg.Fullscreen && target != null && h != IntPtr.Zero)
                {
                    // -full-screen always uses the primary display, so move the window first.
                    Native.MoveTo(h, target.Bounds.X + 40, target.Bounds.Y + 40);
                    var later = new Timer { Interval = 1200 };
                    later.Tick += delegate
                    {
                        later.Dispose();
                        vm.P.Refresh();
                        if (!vm.P.HasExited) Native.SendCtrlAltF(vm.P.MainWindowHandle);
                    };
                    later.Start();
                }
                HideToTray(cfg.DisplayName + " is running", "Right-click the Try Linux icon here to shut it down or open settings.");
                ShowHome();
            }
        };
        poll.Start();
    }

    // The non-primary display chosen for fullscreen, or null for the primary one.
    static System.Windows.Forms.Screen FullscreenScreen(VmConfig cfg)
    {
        if (cfg.FullscreenDisplay.Length == 0) return null;
        var s = System.Windows.Forms.Screen.AllScreens.FirstOrDefault(x => x.DeviceName == cfg.FullscreenDisplay);
        return s == null || s.Primary ? null : s;
    }

    void GrowDisk(VmConfig cfg)
    {
        if (cfg.DiskGB <= 0) return;
        try
        {
            if ((long)cfg.DiskGB << 30 > DiskInfo.Read(cfg.Disk).Virtual)
                Winq.Run(Winq.Img, "resize " + Q(cfg.Disk) + " " + cfg.DiskGB + "G");
        }
        catch (Exception ex)
        {
            Error("Couldn't grow the disk to " + cfg.DiskGB + " GiB, so it keeps its current size.\n\n" + ex.Message +
                (ex.Message.IndexOf("snapshot", StringComparison.OrdinalIgnoreCase) >= 0 ? "\n\nDelete its snapshots first (Settings > Recovery)." : ""));
        }
    }

    static void RecordRender(RunningVm vm)
    {
        try
        {
            var fresh = VmConfig.Load(vm.Cfg.Dir);
            fresh.LastRender = (vm.Cpu ? "CPU" : "GPU") + " rendering (" + DateTime.Now.ToString("yyyy-MM-dd HH:mm") + ")";
            if (File.Exists(fresh.Disk)) fresh.Save();
        }
        catch (IOException) { }
    }

    RunningVm Launch(VmConfig cfg, bool installer, bool cpu)
    {
        int cores, ram;
        cfg.Resources(out cores, out ram);
        var vm = new RunningVm { Cfg = cfg, Installer = installer, Cpu = cpu, QmpPort = Net.FreePort() };
        string outputs = cfg.Displays > 1 ? ",max_outputs=" + cfg.Displays : "";
        string gpu = cpu ? "virtio-vga" + outputs
            : (cfg.Venus ? "virtio-vga-gl,blob=on,hostmem=" + cfg.GpuMemGB + "G,venus=on" : "virtio-vga-gl") + outputs;
        var a = new List<string> {
            "-machine q35,accel=whpx", "-cpu host",
            "-smp " + cores, "-m " + ram + "G",
            "-display " + (cpu ? "sdl" : "sdl,gl=on"),
            "-device " + gpu,
            "-drive " + Q("file=" + cfg.Disk + ",format=qcow2,if=virtio"),
            "-device virtio-net-pci,netdev=net0",
            "-netdev user,id=net0" + string.Concat(cfg.ForwardRules().Select(f => "," + f.ToHostFwd())),
            "-usb", "-device usb-tablet",
            "-name " + Q(cfg.DisplayName),
            "-qmp tcp:127.0.0.1:" + vm.QmpPort + ",server=on,wait=off"
        };
        if (cfg.Sound) { a.Add("-device intel-hda"); a.Add("-device " + (cfg.Mic ? "hda-duplex" : "hda-output")); }
        if (cfg.Fullscreen && FullscreenScreen(cfg) == null) a.Add("-full-screen");
        // BIOS boot (QEMU's default SeaBIOS): WINQ-EMU notes Vulkan is far slower under EFI.
        // once=d boots the installer this time only, so the post-install reboot goes to disk.
        if (installer) { a.Add("-cdrom " + Q(cfg.Iso)); a.Add("-boot once=d"); }
        if (cfg.SharedFolder.Length > 0 && Directory.Exists(cfg.SharedFolder))
        {
            a.Add("-fsdev " + Q("local,id=fs0,path=" + cfg.SharedFolder + ",security_model=mapped-xattr" + (cfg.SharedWrite ? "" : ",readonly=on")));
            a.Add("-device virtio-9p-pci,fsdev=fs0,mount_tag=host");
        }

        vm.P = new Process
        {
            StartInfo = new ProcessStartInfo(Winq.Qemu, string.Join(" ", a))
            {
                UseShellExecute = false, RedirectStandardError = true, CreateNoWindow = true, WorkingDirectory = cfg.Dir
            },
            EnableRaisingEvents = true
        };
        vm.P.ErrorDataReceived += (s, e) => { if (e.Data != null) lock (vm.Err) vm.Err.AppendLine(e.Data); };
        vm.P.Exited += delegate { Ui(() => OnVmExited(vm)); };
        try
        {
            vm.P.Start();
            vm.P.BeginErrorReadLine();
        }
        catch (Exception ex) { Error("Couldn't start QEMU: " + ex.Message); return null; }
        running[cfg.Name] = vm;
        return vm;
    }

    void OnVmExited(RunningVm vm)
    {
        running.Remove(vm.Cfg.Name);
        double secs = (DateTime.Now - vm.Started).TotalSeconds;
        // An installer session that ran a while has most likely installed the system.
        if (vm.Installer && secs > 60)
        {
            var fresh = VmConfig.Load(vm.Cfg.Dir);
            fresh.PendingInstall = false;
            if (File.Exists(fresh.Disk)) fresh.Save();
        }
        if (vm.P.ExitCode != 0 && secs < 20)
        {
            string msg;
            lock (vm.Err) msg = vm.Err.ToString().Trim();
            if (msg.Length == 0) msg = "QEMU exited with code " + vm.P.ExitCode + ".";
            bool portClash = msg.IndexOf("hostfwd", StringComparison.OrdinalIgnoreCase) >= 0 || msg.IndexOf("host forwarding", StringComparison.OrdinalIgnoreCase) >= 0;
            if (!vm.Cpu && vm.Cfg.Render == 0 && !portClash)
            {
                // Automatic rendering: this PC couldn't use the GPU path, so retry on the CPU.
                ShowLauncher();
                Boot(VmConfig.Load(vm.Cfg.Dir), true);
                return;
            }
            if (portClash) msg += "\n\nA forwarded port is already in use. Change the port forwards in Settings > Advanced.";
            ShowLauncher();
            Error(vm.Cfg.DisplayName + " couldn't start.\n\n" + msg);
            return;
        }
        if (running.Count == 0 && !Visible) ShowLauncher();
        else if (!busy && Visible) ShowHome();
    }

    void ShowVm(RunningVm vm)
    {
        vm.P.Refresh();
        Native.BringToFront(vm.P.MainWindowHandle);
    }

    void Shutdown(RunningVm vm)
    {
        Task.Run(() =>
        {
            try { Net.Qmp(vm.QmpPort, "system_powerdown"); }
            catch (Exception ex) { Ui(() => Error("Couldn't ask " + vm.Cfg.DisplayName + " to shut down: " + ex.Message)); }
        });
        tray.ShowBalloonTip(3000, "Shutting down", vm.Cfg.DisplayName + " is shutting down.", ToolTipIcon.Info);
    }

    void BuildTrayMenu()
    {
        var m = tray.ContextMenuStrip;
        m.Items.Clear();
        foreach (var vm in running.Values.ToList())
        {
            var v = vm;
            var item = new ToolStripMenuItem(v.Cfg.DisplayName);
            item.DropDownItems.Add("Show window", null, delegate { ShowVm(v); });
            if (v.Cfg.SharedFolder.Length > 0)
                item.DropDownItems.Add("Open shared folder", null, delegate { Process.Start("explorer.exe", Q(v.Cfg.SharedFolder)); });
            item.DropDownItems.Add("Shut down", null, delegate { Shutdown(v); });
            item.DropDownItems.Add("Force stop", null, delegate
            {
                if (MessageBox.Show("Force " + v.Cfg.DisplayName + " to stop? Unsaved work in it is lost.", "Try Linux",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
                    try { v.P.Kill(); } catch { }
            });
            m.Items.Add(item);
        }
        if (running.Count == 0) m.Items.Add(new ToolStripMenuItem("No systems running") { Enabled = false });
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add("Open Try Linux", null, delegate { ShowLauncher(); });
        m.Items.Add("Quit Try Linux", null, delegate
        {
            if (running.Count > 0 && MessageBox.Show("Quit Try Linux? Running systems keep running, but without the tray menu.",
                    "Try Linux", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            quitting = true;
            Close();
        });
    }

    void HideToTray(string title, string text)
    {
        Hide();
        if (!trayHintShown)
        {
            trayHintShown = true;
            tray.ShowBalloonTip(5000, title, text, ToolTipIcon.Info);
        }
    }

    void ShowLauncher()
    {
        Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
        if (!busy) ShowHome();
    }

    // ------------------------------------------------------------ helpers

    void Ui(Action a)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(a); } catch (InvalidOperationException) { }
    }

    void Error(string msg) { MessageBox.Show(this, msg, "Try Linux", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    void Info(string msg) { MessageBox.Show(this, msg, "Try Linux", MessageBoxButtons.OK, MessageBoxIcon.Information); }

    // Quote a single command-line argument for the Windows argument parser.
    public static string Q(string s) { return "\"" + s.Replace("\"", "\\\"") + "\""; }

    string Prompt(string title, string label)
    {
        using (var f = new Form { Text = title, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false,
            StartPosition = FormStartPosition.CenterParent, BackColor = Theme.Bg, ForeColor = Theme.Text, Font = Theme.Body,
            ClientSize = new Size(D(520), D(140)), ShowInTaskbar = false })
        {
            f.HandleCreated += delegate { Native.DarkTitleBar(f.Handle); };
            var l = new Label { Text = label, BackColor = Theme.Bg, ForeColor = Theme.Muted, Font = Theme.Small };
            l.SetBounds(D(16), D(14), D(488), D(22));
            var box = new TextBox { Font = Theme.Body, BackColor = Theme.Card, ForeColor = Theme.Text, BorderStyle = BorderStyle.FixedSingle };
            box.SetBounds(D(16), D(42), D(488), D(26));
            var ok = new FlatBtn("OK", true) { DialogResult = DialogResult.OK };
            ok.SetBounds(D(384), D(88), D(120), D(36));
            var cancel = new FlatBtn("Cancel") { DialogResult = DialogResult.Cancel };
            cancel.SetBounds(D(254), D(88), D(120), D(36));
            f.Controls.AddRange(new Control[] { l, box, ok, cancel });
            f.AcceptButton = ok;
            f.CancelButton = cancel;
            if (f.ShowDialog(this) != DialogResult.OK) return null;
            string v = box.Text.Trim();
            return v.Length == 0 ? null : v;
        }
    }
}
