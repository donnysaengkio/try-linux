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

        if (args.Length == 1 && args[0] == "--uninstall") { SelfInstall.Uninstall(); return; }
        string start = null;
        for (int i = 0; i < args.Length - 1; i++) if (args[i] == "--start") start = args[i + 1];
        if (start == null)
        {
            AppSettings.Load();
            Theme.Apply(AppSettings.EffectiveTheme);
            if (SelfInstall.OfferAtStartup()) return;
        }
        Application.Run(new MainForm(start));
    }
}

// ---------------------------------------------------------------- catalog

class Distro
{
    public string Id, Name, Blurb, Base, Checksum;
    public string Home;   // the distribution's home page (as DistroWatch lists it), https only; null if unknown
    // No download Try Linux can follow (no Base): the ISO has to be downloaded from the home page by hand.
    public bool WebsiteOnly { get { return Base == null; } }
    // Each step is appended to Base. A step starting with ~ is a regex matched
    // against the links in the current directory listing; the newest match wins.
    public string[] Steps;
    public int DiskGB = 64;

    public static readonly Distro[] Catalog = {
        new Distro {
            Id = "fedora", Name = "Fedora Workstation", Home = "https://fedoraproject.org/",
            Blurb = "A community distribution sponsored by Red Hat that ships new GNOME, kernel and Mesa releases soon after they come out",
            Base = "https://mirrors.kernel.org/fedora/releases/",
            Steps = new[] { @"~^\d+/$", "Workstation/x86_64/iso/", @"~^Fedora-Workstation-Live-.*\.iso$" },
            Checksum = "~CHECKSUM$" },
        new Distro {
            Id = "ubuntu", Name = "Ubuntu Desktop", Home = "https://www.ubuntu.com/",
            Blurb = "The most widely used desktop Linux, made by Canonical, with long-term support releases and a large software catalog",
            Base = "https://releases.ubuntu.com/",
            Steps = new[] { @"~^\d\d\.\d\d(\.\d+)?/$", @"~^ubuntu-.*-desktop-amd64\.iso$" },
            Checksum = "SHA256SUMS" },
        new Distro {
            Id = "debian", Name = "Debian (GNOME)", Home = "https://www.debian.org/",
            Blurb = "One of the oldest community distributions, known for stability and the base of Ubuntu, Mint and many others",
            Base = "https://cdimage.debian.org/debian-cd/current-live/amd64/iso-hybrid/",
            Steps = new[] { @"~^debian-live-.*-amd64-gnome\.iso$" },
            Checksum = "SHA256SUMS" },
        new Distro {
            Id = "mint", Name = "Linux Mint (Cinnamon)", Home = "https://linuxmint.com/",
            Blurb = "A beginner-friendly distribution based on Ubuntu, with the Windows-like Cinnamon desktop and media codecs available at install",
            Base = "https://mirrors.edge.kernel.org/linuxmint/stable/",
            Steps = new[] { @"~^\d+(\.\d+)?/$", @"~^linuxmint-.*-cinnamon-64bit\.iso$" },
            Checksum = "sha256sum.txt" },
        new Distro {
            Id = "zorin", Name = "Zorin OS Core", Home = "https://zorin.com/os/",
            Blurb = "A polished distribution based on Ubuntu, designed to feel familiar to people moving from Windows",
            Base = "https://mirrors.edge.kernel.org/zorinos-isos/",
            Steps = new[] { @"~^\d+/$", @"~^Zorin-OS-(?<v>\d+(\.\d+)*)-Core-64-bit(-r\d+)?\.iso$" },
            Checksum = "SHA256SUMS.txt" },
    };

    public static Distro Find(string id)
    {
        return Catalog.FirstOrDefault(d => d.Id == id) ?? MoreDistros.Cached().FirstOrDefault(d => d.Id == id);
    }
}

// More distributions, from a list kept in the GitHub repo (catalog/distros.json), so it can grow without
// a new release of Try Linux. Each entry is found and checked against its official checksum the same way
// as the built-in ones. The list is fetched at most once a day and cached; offline, the cached copy is used.
static class MoreDistros
{
    public static string Source = "https://raw.githubusercontent.com/donnysaengkio/try-linux/main/catalog/distros.json";
    static List<Distro> loaded;

    static string CacheFile { get { return Path.Combine(AppSettings.Storage, "catalog", "distros.json"); } }

    // When the cached list was last fetched, or null if there isn't one.
    public static DateTime? Updated { get { return File.Exists(CacheFile) ? File.GetLastWriteTime(CacheFile) : (DateTime?)null; } }

    public static List<Distro> Cached()
    {
        if (loaded == null && File.Exists(CacheFile))
            try { loaded = Parse(File.ReadAllText(CacheFile)); }
            catch (Exception) { }
        return loaded ?? new List<Distro>();
    }

    // The ETag GitHub gave the cached copy, kept beside it.
    static string EtagFile { get { return Path.Combine(AppSettings.Storage, "catalog", "distros.etag"); } }

    // The list, checked against GitHub each time Choose a Linux OS opens: a conditional request (If-None-Match,
    // with the cached copy's ETag) gets a short "not modified" while the list is the same, and the new list as
    // soon as it changes. `force` downloads it whatever. The cached copy is used if the check fails.
    public static List<Distro> Load(bool force)
    {
        try
        {
            string etag = !force && Cached().Count > 0 ? SavedEtag() : null;
            string newEtag;
            string text = Fetch(etag, out newEtag);
            if (text == null) return Cached();   // not modified
            var list = Parse(text);
            Directory.CreateDirectory(Path.GetDirectoryName(CacheFile));
            File.WriteAllText(CacheFile, text);
            // The ETag with a fingerprint of the list it came with, so it's only ever sent for that list.
            if (newEtag != null) File.WriteAllText(EtagFile, newEtag + "\n" + Fingerprint(text)); else if (File.Exists(EtagFile)) File.Delete(EtagFile);
            loaded = list;
            return list;
        }
        catch (Exception)
        {
            if (Cached().Count > 0) return Cached();
            throw;
        }
    }

    // The saved ETag, if it belongs to the cached list. One saved without its list (as 1.1.13 and earlier could
    // leave it: a newer ETag beside an older list) would get "not modified" for a list this PC never had, so
    // it's dropped and the whole list is downloaded again.
    static string SavedEtag()
    {
        try
        {
            if (!File.Exists(EtagFile) || !File.Exists(CacheFile)) return null;
            var lines = File.ReadAllText(EtagFile).Split('\n');
            if (lines.Length < 2 || lines[1].Trim() != Fingerprint(File.ReadAllText(CacheFile))) return null;
            return lines[0].Trim();
        }
        catch { return null; }
    }

    static string Fingerprint(string text)
    {
        using (var sha = SHA256.Create())
            return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-", "").ToLowerInvariant();
    }

    // The list's text, or null when the server says it hasn't changed since `etag`.
    static string Fetch(string etag, out string newEtag)
    {
        newEtag = null;
        if (!Source.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return Net.GetString(Source);   // a local file, for testing
        var req = (HttpWebRequest)WebRequest.Create(Source);
        req.UserAgent = Net.UserAgent;
        req.Timeout = 30000;
        req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
        if (etag != null) req.Headers[HttpRequestHeader.IfNoneMatch] = etag;
        try
        {
            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var r = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
            {
                newEtag = resp.Headers[HttpResponseHeader.ETag];
                return r.ReadToEnd();
            }
        }
        catch (WebException ex)
        {
            var resp = ex.Response as HttpWebResponse;
            if (resp != null && resp.StatusCode == HttpStatusCode.NotModified) { resp.Close(); return null; }
            throw;
        }
    }

    // Entries that aren't on an https mirror, or that would replace a built-in one, are left out.
    static List<Distro> Parse(string json)
    {
        var root = (Dictionary<string, object>)new System.Web.Script.Serialization.JavaScriptSerializer().DeserializeObject(json);
        var list = new List<Distro>();
        object items;
        if (!root.TryGetValue("distros", out items) || !(items is object[])) throw new Exception("The list of distributions is empty or damaged.");
        Func<Dictionary<string, object>, string, string> str = (d, k) => { object v; return d.TryGetValue(k, out v) && v is string ? (string)v : null; };
        foreach (var o in (object[])items)
        {
            var d = o as Dictionary<string, object>;
            if (d == null) continue;
            string id = str(d, "id"), name = str(d, "name"), baseUrl = str(d, "base");
            string home = (str(d, "home") ?? "").StartsWith("https://", StringComparison.Ordinal) ? str(d, "home") : null;
            object steps; d.TryGetValue("steps", out steps);
            if (id == null || name == null || !Regex.IsMatch(id, "^[a-z0-9-]{1,40}$")) continue;
            if (Distro.Catalog.Any(c => c.Id == id) || list.Any(c => c.Id == id)) continue;
            // Website only: no download Try Linux can follow, just the home page to get the ISO from by hand
            // (searchable; Next offers to open the page). Versions before 1.1.11 leave these out, as they need base.
            if (baseUrl == null && steps == null)
            {
                if (home != null) list.Add(new Distro { Id = id, Name = name, Blurb = str(d, "about") ?? "", Home = home, Steps = new string[0] });
                continue;
            }
            if (baseUrl == null || !(steps is object[]) || !baseUrl.StartsWith("https://", StringComparison.Ordinal)) continue;
            object disk; d.TryGetValue("diskGB", out disk);
            list.Add(new Distro {
                Id = id, Name = name, Blurb = str(d, "about") ?? "", Base = baseUrl,
                Steps = ((object[])steps).OfType<string>().ToArray(), Checksum = str(d, "checksum"), Home = home,
                DiskGB = disk is int ? Math.Max(16, Math.Min(2048, (int)disk)) : 64 });
        }
        return list;
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

    public static string DeclinedInstall = "";   // the version whose install offer was turned down
    public static bool CheckUpdates = true;       // look for a newer release once a day
    public static string LastUpdateCheck = "", LatestVersion = "", LatestWinq = "";
    // Folders of systems created outside VmsDir (Create > Location > Choose a different location).
    public static List<string> VmDirs = new List<string>();
    // Settings > General > Appearance: the theme (dark or light), or Windows' own light or dark mode.
    public static string ThemeName = "dark";
    public static bool FollowSystem;

    // The theme to use now: with Follow system, Light while Windows shows apps light and Dark otherwise.
    public static string EffectiveTheme { get { return FollowSystem ? (WindowsUsesLight() ? "light" : "dark") : ThemeName; } }

    public static bool WindowsUsesLight()
    {
        try
        {
            using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                return k != null && Convert.ToInt32(k.GetValue("AppsUseLightTheme", 0)) == 1;
        }
        catch { return false; }
    }

    public static void Load()
    {
        if (!File.Exists(Ini)) return;
        foreach (var line in File.ReadAllLines(Ini))
        {
            if (line.StartsWith("storage=") && line.Length > 8) Storage = line.Substring(8).Trim();
            if (line.StartsWith("declinedinstall=")) DeclinedInstall = line.Substring(16).Trim();
            if (line.StartsWith("checkupdates=")) CheckUpdates = line.Substring(13).Trim() != "0";
            if (line.StartsWith("lastupdatecheck=")) LastUpdateCheck = line.Substring(16).Trim();
            if (line.StartsWith("latestversion=")) LatestVersion = line.Substring(14).Trim();
            if (line.StartsWith("latestwinq=")) LatestWinq = line.Substring(11).Trim();
            if (line.StartsWith("theme=")) ThemeName = line.Substring(6).Trim().ToLowerInvariant();
            if (line.StartsWith("followsystem=")) FollowSystem = line.Substring(13).Trim() == "1";
            if (line.StartsWith("vmdirs=")) VmDirs = line.Substring(7).Split('|').Select(d => d.Trim()).Where(d => d.Length > 0).ToList();
        }
    }

    public static void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Ini));
        File.WriteAllLines(Ini, new[] { "storage=" + Storage, "declinedinstall=" + DeclinedInstall,
            "checkupdates=" + (CheckUpdates ? "1" : "0"), "lastupdatecheck=" + LastUpdateCheck, "latestversion=" + LatestVersion,
            "latestwinq=" + LatestWinq, "vmdirs=" + string.Join("|", VmDirs),
            "theme=" + ThemeName, "followsystem=" + (FollowSystem ? "1" : "0") });
    }
}

// ---------------------------------------------------------------- updates

class UpdateInfo
{
    public string Version, Page, ExeUrl, SumsUrl;
}

// Looks for a newer release on GitHub (at most once a day) and swaps the exe for it.
// A release carries TryLinux.exe and SHA256SUMS.txt as separate files, so the download can be checked.
static class Updater
{
    // The repository holds every edition; Windows releases are the ones tagged v1.2.3 (the Mac's are macos-v...,
    // Linux's linux-v...). Tests point this at a local server.
    public static string Api = "https://api.github.com/repos/donnysaengkio/try-linux/releases?per_page=30";
    public const string ReleasesPage = "https://github.com/donnysaengkio/try-linux/releases";

    // WINQ-EMU, which runs the systems, is checked at the same time. Its releases have no checksum file;
    // its installer comes over HTTPS from the WINQ-EMU GitHub page, as on first install.
    public static string WinqApi = "https://api.github.com/repos/cmspam/winq-emu/releases/latest";
    public const string WinqReleases = "https://github.com/cmspam/winq-emu/releases";

    // The newer versions found by the last check, or null.
    public static string Available, WinqAvailable;

    // Returns the newer version, or null. `force` ignores the setting and the once-a-day limit,
    // and only then are errors thrown; otherwise a failed check is skipped quietly.
    public static string Check(bool force)
    {
        if (!force && !AppSettings.CheckUpdates) return null;
        DateTime last;
        bool recent = DateTime.TryParse(AppSettings.LastUpdateCheck, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind, out last) && (DateTime.UtcNow - last).TotalHours < 24;
        if (force || !recent)
        {
            Exception failed = null;
            try { AppSettings.LatestVersion = Fetch().Version; }
            catch (Exception ex) { failed = ex; }
            try { AppSettings.LatestWinq = FetchWinq().Version; } catch { }
            if (failed == null) AppSettings.LastUpdateCheck = DateTime.UtcNow.ToString("o");
            try { AppSettings.Save(); } catch { }
            if (failed != null && force) throw failed;
        }
        string winq = Winq.Version;
        WinqAvailable = winq != null && IsNewerWinq(AppSettings.LatestWinq, winq) ? AppSettings.LatestWinq : null;
        return IsNewer(AppSettings.LatestVersion, MainForm.Version) ? AppSettings.LatestVersion : null;
    }

    public static UpdateInfo FetchWinq()
    {
        var json = (Dictionary<string, object>)new System.Web.Script.Serialization.JavaScriptSerializer()
            .DeserializeObject(Net.GetString(WinqApi));
        string name = json.ContainsKey("name") ? json["name"] as string : null;
        var info = new UpdateInfo
        {
            Version = string.IsNullOrEmpty(name) ? (string)json["tag_name"] : name,
            Page = json.ContainsKey("html_url") ? (string)json["html_url"] : WinqReleases
        };
        foreach (Dictionary<string, object> a in (json.ContainsKey("assets") ? json["assets"] as object[] : null) ?? new object[0])
            if (((string)a["name"]).EndsWith("-Setup.exe", StringComparison.OrdinalIgnoreCase)) info.ExeUrl = (string)a["browser_download_url"];
        return info;
    }

    // WINQ-EMU versions read like "Alpha 10": compare the stage (alpha < beta < rc < release), then the numbers.
    public static bool IsNewerWinq(string candidate, string installed)
    {
        if (string.IsNullOrEmpty(candidate)) return false;
        Func<string, int> stage = v =>
        {
            v = v.ToLowerInvariant();
            return v.Contains("alpha") ? 0 : v.Contains("beta") ? 1 : Regex.IsMatch(v, @"\brc") ? 2 : 3;
        };
        int a = stage(candidate), b = stage(installed);
        if (a != b) return a > b;
        return IsNewer(candidate, installed);
    }

    public static UpdateInfo Fetch()
    {
        var parsed = new System.Web.Script.Serialization.JavaScriptSerializer().DeserializeObject(Net.GetString(Api));
        // A list of releases (newest first): the first Windows one. A single release is taken as it is.
        var json = parsed as Dictionary<string, object>;
        if (json == null)
            json = ((object[])parsed).Cast<Dictionary<string, object>>().FirstOrDefault(r =>
                !(r.ContainsKey("draft") && r["draft"] is bool && (bool)r["draft"]) &&
                Regex.IsMatch((string)r["tag_name"], @"^[vV]\d"));
        if (json == null) throw new Exception("GitHub didn't list any Try Linux for Windows releases.");
        var info = new UpdateInfo
        {
            Version = ((string)json["tag_name"]).TrimStart('v', 'V'),
            Page = json.ContainsKey("html_url") ? (string)json["html_url"] : ReleasesPage
        };
        var assets = json.ContainsKey("assets") ? json["assets"] as object[] : null;
        foreach (Dictionary<string, object> a in assets ?? new object[0])
        {
            string name = (string)a["name"], url = (string)a["browser_download_url"];
            if (name.Equals("TryLinux.exe", StringComparison.OrdinalIgnoreCase)) info.ExeUrl = url;
            if (name.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase)) info.SumsUrl = url;
        }
        return info;
    }

    public static bool IsNewer(string candidate, string current)
    {
        if (string.IsNullOrEmpty(candidate)) return false;
        var a = Regex.Matches(candidate, @"\d+").Cast<Match>().Select(m => int.Parse(m.Value)).ToList();
        var b = Regex.Matches(current, @"\d+").Cast<Match>().Select(m => int.Parse(m.Value)).ToList();
        for (int i = 0; i < Math.Max(a.Count, b.Count); i++)
        {
            int x = i < a.Count ? a[i] : 0, y = i < b.Count ? b[i] : 0;
            if (x != y) return x > y;
        }
        return false;
    }

    // Downloads the new exe and checks it against the release's SHA256SUMS.txt. Returns its path.
    public static string Download(UpdateInfo info, Action<long, long, double> report)
    {
        if (info.ExeUrl == null || info.SumsUrl == null)
            throw new Exception("This release can't be installed automatically. Download it from the release page instead.");
        string dir = Path.Combine(Path.GetTempPath(), "TryLinux-update");
        Directory.CreateDirectory(dir);
        string exe = Path.Combine(dir, "TryLinux-" + info.Version + ".exe");
        if (File.Exists(exe)) File.Delete(exe);
        Net.Download(info.ExeUrl, exe, -1, () => false, report);

        string expected = null;
        foreach (var line in Net.GetString(info.SumsUrl).Split('\n'))
        {
            var m = Regex.Match(line, @"^\s*([0-9a-fA-F]{64})\s+\*?TryLinux\.exe\s*$");
            if (m.Success) expected = m.Groups[1].Value.ToLowerInvariant();
        }
        if (expected == null) throw new Exception("The release's SHA256SUMS.txt has no checksum for TryLinux.exe.");
        if (Net.Sha256(exe, () => false, x => { }) != expected)
        {
            File.Delete(exe);
            throw new Exception("The downloaded update doesn't match its published checksum, so it wasn't used. Try again later.");
        }
        return exe;
    }

    // Replaces the running exe with the verified download.
    public static void Apply(string downloaded, string version)
    {
        SelfInstall.Replace(Path.GetFullPath(Application.ExecutablePath), downloaded);
        if (SelfInstall.IsInstalledCopy) try { SelfInstall.Register(version); } catch { }
        try { File.Delete(downloaded); } catch { }
    }
}

// ---------------------------------------------------------------- install

// Try Linux is a single exe. Run from anywhere else, it offers to copy itself to
// %LOCALAPPDATA%\Programs\TryLinux with a Start menu entry, so the download can be deleted.
static class SelfInstall
{
    public static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "TryLinux");
    public static string Exe { get { return Path.Combine(Dir, "TryLinux.exe"); } }
    static string StartMenuLink
    {
        get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Try Linux.lnk"); }
    }
    const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\TryLinux";
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    static string Current { get { return Path.GetFullPath(Application.ExecutablePath); } }
    static bool SamePath(string a, string b) { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
    static string Q(string s) { return "\"" + s + "\""; }
    public static bool IsInstalledCopy { get { return SamePath(Current, Exe); } }

    static bool SameFile(string a, string b)
    {
        try
        {
            var fa = new FileInfo(a);
            var fb = new FileInfo(b);
            if (!fa.Exists || !fb.Exists || fa.Length != fb.Length) return false;
            using (var sha = SHA256.Create())
            {
                byte[] ha, hb;
                using (var s = File.OpenRead(a)) ha = sha.ComputeHash(s);
                using (var s = File.OpenRead(b)) hb = sha.ComputeHash(s);
                return ha.SequenceEqual(hb);
            }
        }
        catch { return false; }
    }

    // Returns true when this process should exit: the installed copy was started, or the user quit.
    public static bool OfferAtStartup()
    {
        RemoveOldCopies();
        if (IsInstalledCopy) return false;
        bool installed = File.Exists(Exe);
        if (installed && SameFile(Current, Exe)) return false;
        if (AppSettings.DeclinedInstall == MainForm.Version) return false;

        DialogResult choice;
        while (true)
        {
            choice = installed
                ? ThemedDialog.Choose(null, "Update Try Linux?",
                    "A different version of Try Linux is installed in this folder:", Dir,
                    "Update replaces it with version " + MainForm.Version + " and opens it. Your Linux systems aren't affected.\nRun opens this copy without changing the installed one.",
                    "Update", "Run")
                : ThemedDialog.Choose(null, "Install Try Linux?",
                    "Try Linux copies itself to this folder and adds itself to the Start menu, so you can delete the downloaded file:", Dir,
                    "Run opens it from here without installing; you can install it later from Settings > General > About.\n\nUninstall it any time from Windows Settings > Apps > Installed apps.",
                    "Install", "Run");
            if (choice != DialogResult.Cancel) break;
            // Closing the window quits without remembering a choice, so the offer comes back next time.
            if (ThemedDialog.Show(null, "Quit Try Linux?", "Try Linux will close without " + (installed ? "updating" : "installing") +
                    ". You'll be asked again the next time you open it.", null, null, "Quit", "Back"))
                return true;
        }
        if (choice == DialogResult.No)
        {
            AppSettings.DeclinedInstall = MainForm.Version;
            try { AppSettings.Save(); } catch { }
            return false;
        }
        try { Install(); }
        catch (Exception ex)
        {
            ThemedDialog.Show(null, "Couldn't install Try Linux", ex.Message, null, "It will run from here instead.", "OK");
            return false;
        }
        // Open starts the installed copy; closing the window quits, and Try Linux is in the Start menu.
        bool open = installed
            ? ThemedDialog.Show(null, "Try Linux is updated", "You now have version " + MainForm.Version + " in the Windows Start menu.", null, null, "Open")
            : ThemedDialog.Show(null, "Try Linux is installed", "Try Linux is now available in the Windows Start menu.", null, null, "Open");
        if (open) Process.Start(Exe);
        return true;
    }

    // Updating renames the running exe to TryLinux.exe.<ticks>.old; the next start deletes it.
    public static void RemoveOldCopies()
    {
        try
        {
            foreach (var f in Directory.GetFiles(Path.GetDirectoryName(Current), Path.GetFileName(Current) + ".*.old"))
                try { File.Delete(f); } catch { }
        }
        catch { }
    }

    // Puts `source` in place of `target`, even while `target` is running.
    public static void Replace(string target, string source)
    {
        string old = target + "." + DateTime.Now.Ticks + ".old";
        if (File.Exists(target)) File.Move(target, old);
        try { File.Copy(source, target); }
        catch { if (File.Exists(old) && !File.Exists(target)) File.Move(old, target); throw; }
    }

    public static void Install()
    {
        Directory.CreateDirectory(Dir);
        // A running copy can't be overwritten, but it can be renamed; the new copy deletes it on its next start.
        Replace(Exe, Current);

        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
        dynamic sc = shell.CreateShortcut(StartMenuLink);
        sc.TargetPath = Exe;
        sc.WorkingDirectory = Dir;
        sc.IconLocation = Exe + ",0";
        sc.Description = "Run Linux distributions in a virtual machine";
        sc.Save();

        // Desktop shortcuts and Start with Windows entries made by another copy now point here.
        foreach (var lnk in Directory.GetFiles(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "*.lnk"))
        {
            try
            {
                dynamic d = shell.CreateShortcut(lnk);
                string target = d.TargetPath;
                if (string.Equals(Path.GetFileName(target), "TryLinux.exe", StringComparison.OrdinalIgnoreCase) && !SamePath(target, Exe))
                {
                    d.TargetPath = Exe;
                    d.WorkingDirectory = Dir;
                    d.IconLocation = Exe + ",0";
                    d.Save();
                }
            }
            catch { }
        }
        using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey, true))
            if (k != null)
                foreach (var n in k.GetValueNames().Where(n => n.StartsWith("TryLinux-")))
                {
                    var m = Regex.Match(k.GetValue(n) as string ?? "", "^\"([^\"]*)\"(.*)$");
                    if (m.Success && !SamePath(m.Groups[1].Value, Exe)) k.SetValue(n, Q(Exe) + m.Groups[2].Value);
                }

        Register();
    }

    // Lists Try Linux in Windows Settings > Apps, which runs "TryLinux.exe --uninstall".
    public static void Register(string version = null)
    {
        using (var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(UninstallKey))
        {
            k.SetValue("DisplayName", "Try Linux");
            k.SetValue("DisplayVersion", version ?? MainForm.Version);
            k.SetValue("DisplayIcon", Exe + ",0");
            k.SetValue("InstallLocation", Dir);
            k.SetValue("UninstallString", Q(Exe) + " --uninstall");
            k.SetValue("URLInfoAbout", "https://github.com/donnysaengkio/try-linux");
            k.SetValue("EstimatedSize", (int)(new FileInfo(Exe).Length / 1024), Microsoft.Win32.RegistryValueKind.DWord);
            k.SetValue("NoModify", 1, Microsoft.Win32.RegistryValueKind.DWord);
            k.SetValue("NoRepair", 1, Microsoft.Win32.RegistryValueKind.DWord);
        }
    }

    public static void Uninstall()
    {
        AppSettings.Load();
        int me = Process.GetCurrentProcess().Id;
        if (Process.GetProcessesByName("TryLinux").Any(p => p.Id != me))
        {
            ThemedDialog.Show(null, "Try Linux is still open", "Quit it from its tray icon first, then uninstall again.", null, null, "OK");
            return;
        }
        if (!ThemedDialog.Show(null, "Uninstall Try Linux?",
                "This removes the app, its Start menu entry, desktop shortcuts and Start with Windows entries. Your Linux systems and downloads are kept in this folder:",
                AppSettings.Storage, "Delete that folder afterwards to remove them too.", "Uninstall", "Cancel", true)) return;

        try { File.Delete(StartMenuLink); } catch { }
        try
        {
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
            foreach (var lnk in Directory.GetFiles(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "*.lnk"))
            {
                try
                {
                    string target = shell.CreateShortcut(lnk).TargetPath;
                    if (SamePath(target, Exe)) File.Delete(lnk);
                }
                catch { }
            }
        }
        catch { }
        using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey, true))
            if (k != null)
                foreach (var n in k.GetValueNames().Where(n => n.StartsWith("TryLinux-"))) k.DeleteValue(n);
        try { Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(UninstallKey); } catch { }

        // This exe can't delete itself while it runs, so a hidden cmd removes the folder once it exits.
        if (IsInstalledCopy)
            Process.Start(new ProcessStartInfo("cmd.exe", "/c ping 127.0.0.1 -n 3 > nul & rmdir /s /q " + Q(Dir))
                { CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden });
        ThemedDialog.Show(null, "Try Linux is uninstalled", null, null, null, "OK");
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
    public bool Reclaim = true;         // pass Linux's TRIM through, so deleted files free space in the disk file
    public string Cd = "";              // ISO in the CD/DVD drive; empty when the drive is empty
    public int NetType;                 // 0 shared (NAT), 1 port forwards only, 2 no network
    public string Description = "";     // the user's notes about this system (may be several lines)
    public bool DiskFull;               // the boot disk was made with its full size allocated (not sparse)
    public static readonly string[] NetTypeNames = { "Shared with this PC (NAT)", "Port forwards only", "No network" };

    public string Disk { get { return Path.Combine(Dir, Name + ".qcow2"); } }
    // More disks after the boot disk: a file name inside Dir (made by Try Linux) or the full path of an attached file.
    public List<string> ExtraDisks = new List<string>();

    public string DiskPath(string entry) { return Path.IsPathRooted(entry) ? entry : Path.Combine(Dir, entry); }

    // The boot disk first, then the others in the order they were added.
    public List<string> AllDisks()
    {
        var list = new List<string> { Disk };
        foreach (var d in ExtraDisks) list.Add(DiskPath(d));
        return list;
    }

    public string NewDiskName()
    {
        for (int n = 2; ; n++)
        {
            string name = Name + "-disk" + n + ".qcow2";
            if (!ExtraDisks.Contains(name, StringComparer.OrdinalIgnoreCase) && !File.Exists(Path.Combine(Dir, name))) return name;
        }
    }
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
                case "reclaim": c.Reclaim = v != "0"; break;
                case "cd": c.Cd = v; break;
                case "nettype": if (isInt) c.NetType = Math.Max(0, Math.Min(2, n)); break;
                case "disks": c.ExtraDisks = v.Split('|').Where(d => d.Length > 0).ToList(); break;
                case "diskfull": c.DiskFull = v == "1"; break;
                case "description": c.Description = Regex.Replace(v, @"\\(.)", m => m.Groups[1].Value == "n" ? "\n" : m.Groups[1].Value); break;
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
            "fsdisplay=" + FullscreenDisplay, "lastrender=" + LastRender, "disks=" + string.Join("|", ExtraDisks),
            "reclaim=" + (Reclaim ? "1" : "0"), "cd=" + Cd, "nettype=" + NetType,
            "diskfull=" + (DiskFull ? "1" : "0"),
            // One line: a backslash and a line break are written as \\ and \n.
            "description=" + (Description ?? "").Replace("\r\n", "\n").Replace("\\", "\\\\").Replace("\n", "\\n") });
    }

    public void Resources(out int cores, out int ramGB)
    {
        int n = Environment.ProcessorCount, total = Sys.TotalRamGB;
        if (Profile == 1) { cores = Sys.MaxCores; ramGB = Sys.MaxRamGB; }
        else if (Profile == 2) { cores = Math.Max(1, Math.Min(Sys.MaxCores, Cores)); ramGB = Math.Max(1, Math.Min(Sys.MaxRamGB, RamGB)); }
        else { cores = Math.Max(2, n / 2); ramGB = Math.Max(2, Math.Min(16, total / 2)); }
    }

    public static List<VmConfig> All()
    {
        var list = new List<VmConfig>();
        // Systems in the storage folder, then any created in a location of their own (a drive that's
        // disconnected just leaves them out until it's back).
        var dirs = Directory.Exists(AppSettings.VmsDir) ? Directory.GetDirectories(AppSettings.VmsDir).ToList() : new List<string>();
        foreach (var d in AppSettings.VmDirs)
            if (!dirs.Any(x => string.Equals(Path.GetFullPath(x), Path.GetFullPath(d), StringComparison.OrdinalIgnoreCase))) dirs.Add(d);
        foreach (var dir in dirs)
        {
            try
            {
                if (!Directory.Exists(dir)) continue;
                var c = Load(dir);
                if (File.Exists(c.Disk)) list.Add(c);
            }
            catch { }
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

    // The most one system may be given: Windows keeps 2 cores and a quarter of the memory.
    public static int MaxCores { get { int n = Environment.ProcessorCount; return Math.Max(Math.Min(2, n), n - 2); } }
    public static int MaxRamGB { get { return Math.Max(1, TotalRamGB * 3 / 4); } }

    // The graphics card with the most memory of its own, as Windows records it (the WMI AdapterRAM
    // figure stops at 4 GB). Graphics built into the processor have little or none of their own and
    // use system memory instead, up to half of it, so they're given that.
    static string gpuName;
    static int gpuMem = -1;
    public static int MaxGpuMemGB { get { ReadGpu(); return gpuMem; } }
    public static string GpuName { get { ReadGpu(); return gpuName; } }
    public static bool GpuDedicated { get { ReadGpu(); return gpuDedicated; } }
    static bool gpuDedicated;

    static void ReadGpu()
    {
        if (gpuMem >= 0) return;
        long best = 0;
        try
        {
            using (var cls = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}"))
                if (cls != null)
                    foreach (var sub in cls.GetSubKeyNames())
                    {
                        long size = 0; string name = null;
                        try
                        {
                            using (var k = cls.OpenSubKey(sub))
                            {
                                if (k == null) continue;
                                name = k.GetValue("DriverDesc") as string;
                                object q = k.GetValue("HardwareInformation.qwMemorySize"), m = k.GetValue("HardwareInformation.MemorySize");
                                if (q is long) size = (long)q;
                                else if (q is byte[] && ((byte[])q).Length >= 8) size = BitConverter.ToInt64((byte[])q, 0);
                                else if (m is int) size = (uint)(int)m;
                                else if (m is byte[] && ((byte[])m).Length >= 4) size = BitConverter.ToUInt32((byte[])m, 0);
                            }
                        }
                        catch { continue; }
                        if (name != null && size > best) { best = size; gpuName = name; }
                    }
        }
        catch { }
        int dedicated = (int)((best + (1L << 29)) >> 30);   // to the nearest GiB: a "16 GB" card reports a little under 16
        gpuDedicated = dedicated >= 2;
        gpuMem = gpuDedicated ? dedicated : Math.Max(1, TotalRamGB / 2);
    }

    // Common sizes offered in the memory and disk lists; any size in between can be typed.
    public static readonly int[] RamSizes = { 1, 2, 4, 6, 8, 12, 16, 20, 24, 32, 40, 48, 64, 96, 128, 192, 256, 384, 512 };
    public static readonly int[] DiskSizes = { 16, 32, 64, 128, 256, 512, 1024, 2048, 4096 };

    public static SizeBox RamBox(int max, int value) { return new SizeBox(1, max, value, RamSizes, 1); }
    public static SizeBox DiskBox(int min, int max, int value) { return new SizeBox(min, max, value, DiskSizes, 1); }

    // Cores in twos (2, 4, 6...), up to `max` rounded down to an even number. A single-core PC can only give one.
    public static SizeBox CoreBox(int max, int value)
    {
        int top = max >= 2 ? max / 2 * 2 : 1;
        var steps = new List<int>();
        for (int n = top >= 2 ? 2 : 1; n <= top; n += 2) steps.Add(n);
        return new SizeBox(Math.Min(2, top), top, value, steps, top >= 2 ? 2 : 1);
    }
}

// A number picked from a list of common sizes, or typed in. Typed values are kept within
// Minimum..Maximum and, with Step 2, rounded up to an even number when the field is left.
class SizeBox : ComboBox
{
    int min, max, step, value;
    readonly int[] presets;
    bool setting;
    public event EventHandler ValueChanged;

    public SizeBox(int min, int max, int value, IEnumerable<int> presets, int step)
    {
        this.min = min; this.max = Math.Max(min, max); this.step = step; this.presets = presets.ToArray();
        DropDownStyle = ComboBoxStyle.DropDown;
        FlatStyle = FlatStyle.Flat;
        BackColor = Theme.Card; ForeColor = Theme.Text; Font = Theme.Body;
        MaxDropDownItems = 12;   // (the open list's dark scrollbar: DarkCombo.Unthemed)
        Fill();
        this.value = Fit(value);
        Text = this.value.ToString();
    }

    public int Minimum { get { return min; } set { min = value; max = Math.Max(min, max); Fill(); Value = this.value; } }
    public int Maximum { get { return max; } set { max = Math.Max(min, value); Fill(); Value = this.value; } }

    public int Value
    {
        get { return value; }
        set
        {
            int v = Fit(value);
            bool changed = v != this.value;
            this.value = v;
            setting = true;
            Text = v.ToString();
            SelectionStart = Text.Length;
            setting = false;
            if (changed && ValueChanged != null) ValueChanged(this, EventArgs.Empty);
        }
    }

    int Fit(int v)
    {
        if (step == 2 && max >= 2) v = (v + 1) / 2 * 2;
        return Math.Max(min, Math.Min(max, v));
    }

    // The list: the preset sizes that fit, plus the top size so the largest choice is always there.
    void Fill()
    {
        BeginUpdate();
        Items.Clear();
        foreach (int p in presets) if (p >= min && p <= max) Items.Add(p.ToString());
        if (!presets.Contains(max)) Items.Add(max.ToString());
        EndUpdate();
    }

    protected override void OnSelectedIndexChanged(EventArgs e)
    {
        base.OnSelectedIndexChanged(e);
        int v;
        if (!setting && SelectedIndex >= 0 && int.TryParse((string)Items[SelectedIndex], out v)) Value = v;
    }

    // While typing, a valid number takes effect at once; anything else waits until the field is left.
    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        if (setting) return;
        int v;
        if (int.TryParse(Text.Trim(), out v) && v >= min && v <= max && Fit(v) == v && v != value)
        {
            value = v;
            if (ValueChanged != null) ValueChanged(this, EventArgs.Empty);
        }
    }

    protected override void OnLeave(EventArgs e) { Commit(); base.OnLeave(e); Unselect(); }

    // Windows selects an editable dropdown's text whenever it's sized or shown, which looks like
    // a highlight on every field; only a field being typed in should show a selection.
    protected override void OnResize(EventArgs e) { base.OnResize(e); Unselect(); }
    protected override void OnDropDownClosed(EventArgs e) { base.OnDropDownClosed(e); if (!Focused) Unselect(); }

    void Unselect()
    {
        if (!IsHandleCreated || Focused) return;
        BeginInvoke((Action)(() => { if (!IsDisposed && !Focused) SelectionLength = 0; }));
    }

    // Locked: shown dimmed and can't be changed (Balanced and Maximum fill in their own values).
    // Disabling it instead would make Windows draw it light grey.
    bool locked;
    public bool Locked
    {
        get { return locked; }
        set
        {
            locked = value; ForeColor = value ? Theme.Muted : Theme.Text; TabStop = !value;
            if (value) { if (DroppedDown) DroppedDown = false; if (Focused && Parent != null) Parent.SelectNextControl(this, true, true, true, true); SelectionLength = 0; }
            Invalidate();
        }
    }

    protected override void OnDropDown(EventArgs e)
    {
        base.OnDropDown(e);
        if (locked) BeginInvoke((Action)(() => DroppedDown = false));
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (locked) { e.Handled = e.SuppressKeyPress = true; return; }
        if (e.KeyCode == Keys.Enter) Commit();
        base.OnKeyDown(e);
    }

    protected override void OnKeyPress(KeyPressEventArgs e)
    {
        if (locked || (!char.IsDigit(e.KeyChar) && !char.IsControl(e.KeyChar))) e.Handled = true;   // numbers only
        base.OnKeyPress(e);
    }

    // While locked, clicks do nothing at all: no list, no caret, an arrow pointer (on the button part here,
    // on the text part through the edit control inside the box).
    static bool Blocked(int msg) { return msg == 0x201 || msg == 0x202 || msg == 0x203 || msg == 0x204 || msg == 0x206 || msg == 0x207 || msg == 0x209 || msg == 0x20A; }
    const int WM_SETCURSOR = 0x20;

    protected override void WndProc(ref Message m)
    {
        if (locked && Blocked(m.Msg)) return;
        if (locked && m.Msg == WM_SETCURSOR) { Cursor.Current = Cursors.Default; m.Result = (IntPtr)1; return; }
        base.WndProc(ref m);
        if (m.Msg == DarkCombo.WM_PAINT) DarkCombo.Paint(this, locked || !Enabled);
    }

    class EditPart : NativeWindow
    {
        readonly SizeBox box;
        public EditPart(SizeBox box, IntPtr edit) { this.box = box; AssignHandle(edit); }
        protected override void WndProc(ref Message m)
        {
            if (box.locked && Blocked(m.Msg)) return;
            if (box.locked && m.Msg == WM_SETCURSOR) { Cursor.Current = Cursors.Default; m.Result = (IntPtr)1; return; }
            base.WndProc(ref m);
        }
    }
    EditPart editPart;
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string title);

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        DarkCombo.Unthemed(this);
        Unselect();
        var edit = FindWindowEx(Handle, IntPtr.Zero, "Edit", null);
        if (edit != IntPtr.Zero) editPart = new EditPart(this, edit);
    }

    // Snaps whatever was typed to a valid value (or back to the last one if it isn't a number).
    public void Commit()
    {
        int v;
        Value = int.TryParse(Text.Trim(), out v) ? v : value;
    }
}

// Dropdowns drawn like the Mac version's: a dark field with a thin border, and the arrow in its own
// dark button behind a divider line. Windows draws the field first; this paints over its frame and button.
static class DarkCombo
{
    public const int WM_PAINT = 0x000F;
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential)] struct COMBOBOXINFO { public int cbSize; public RECT rcItem, rcButton; public int stateButton; public IntPtr hwndCombo, hwndItem, hwndList; }
    [DllImport("user32.dll")] static extern bool GetComboBoxInfo(IntPtr h, ref COMBOBOXINFO info);
    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] static extern int SetWindowTheme(IntPtr h, string app, string idList);

    // The themed Windows combo box animates a highlighted border when the pointer is over it, which
    // showed for a moment before Paint drew the dark frame over it. Unthemed, it doesn't (Paint draws
    // everything but the text anyway).
    // The open list keeps the dark theme, for its scrollbar.
    public static void Unthemed(ComboBox c)
    {
        try
        {
            SetWindowTheme(c.Handle, "", "");
            var info = new COMBOBOXINFO { cbSize = Marshal.SizeOf(typeof(COMBOBOXINFO)) };
            if (GetComboBoxInfo(c.Handle, ref info) && info.hwndList != IntPtr.Zero)
            {
                SetWindowTheme(info.hwndList, Native.ScrollbarTheme, null);
                Native.RoundCorners(info.hwndList);
            }
        }
        catch { }
    }

    public static void Paint(ComboBox c, bool dim)
    {
        if (!c.IsHandleCreated || c.Width < 10) return;
        var info = new COMBOBOXINFO { cbSize = Marshal.SizeOf(typeof(COMBOBOXINFO)) };
        if (!GetComboBoxInfo(c.Handle, ref info)) return;
        int bw = Math.Max(Theme.D(22), info.rcButton.R - info.rcButton.L + 2);
        var item = Rectangle.FromLTRB(info.rcItem.L, info.rcItem.T, Math.Min(info.rcItem.R, c.Width - bw - 1), info.rcItem.B);
        var button = new Rectangle(c.Width - bw, 0, bw, c.Height);
        using (var g = Graphics.FromHwnd(c.Handle))
        {
            // Everything but the text: a rounded box like GTK's dropdown button, the arrow inside it.
            g.ExcludeClip(item);
            using (var b = new SolidBrush(c.Parent != null ? c.Parent.BackColor : Theme.Bg)) g.FillRectangle(b, 0, 0, c.Width, c.Height);
            var mode0 = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var box = new Rectangle(0, 0, c.Width - 1, c.Height - 1);
            using (var path = Theme.Shape(box, Theme.D(6), true, true))
            {
                using (var b = new SolidBrush(Theme.Card)) g.FillPath(b, path);
                g.ResetClip();
                using (var p = new Pen(Theme.Border)) g.DrawPath(p, path);
            }
            g.SmoothingMode = mode0;
            // A chevron in the middle of the button.
            int cx = button.Left + button.Width / 2, cy = c.Height / 2, s = Math.Max(3, Theme.D(4));
            var mode = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            if (Lucide.Has("chevron-down"))
            {
                int ic = Theme.D(16);
                Lucide.Draw(g, "chevron-down", new Rectangle(cx - ic / 2, cy - ic / 2, ic, ic), dim ? Theme.Muted : Theme.Text);
            }
            else using (var p = new Pen(dim ? Theme.Muted : Theme.Text, Math.Max(1.5f, Theme.Scale * 1.5f)))
                g.DrawLines(p, new[] { new Point(cx - s, cy - s / 2), new Point(cx, cy + s / 2), new Point(cx + s, cy - s / 2) });
            g.SmoothingMode = mode;
        }
    }
}

// A dropdown list (no typing), drawn like the others: the chosen item on the dark field, muted when disabled.
class DarkList : ComboBox
{
    public DarkList()
    {
        DropDownStyle = ComboBoxStyle.DropDownList;
        DrawMode = DrawMode.OwnerDrawFixed;
        FlatStyle = FlatStyle.Flat;
        BackColor = Theme.Card; ForeColor = Theme.Text; Font = Theme.Body;
        ItemHeight = Theme.D(20);
    }

    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        bool field = (e.State & DrawItemState.ComboBoxEdit) != 0;
        bool chosen = !field && (e.State & DrawItemState.Selected) != 0;
        using (var b = new SolidBrush(chosen ? Theme.CardSelected : Theme.Card)) e.Graphics.FillRectangle(b, e.Bounds);
        if (e.Index >= 0)
            TextRenderer.DrawText(e.Graphics, GetItemText(Items[e.Index]), Font, Rectangle.Inflate(e.Bounds, -Theme.D(3), 0),
                Enabled ? (chosen ? Theme.Highlight : Theme.Text) : Theme.Muted,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
    }

    protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }
    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); DarkCombo.Unthemed(this); }

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        if (m.Msg == DarkCombo.WM_PAINT) DarkCombo.Paint(this, !Enabled);
    }
}

// ---------------------------------------------------------------- WINQ-EMU

static class Winq
{
    public static string Bin;
    public static bool Ready { get { return Bin != null; } }
    public static string Qemu { get { return Path.Combine(Bin, "qemu-system-x86_64w.exe"); } }
    public static string Img { get { return Path.Combine(Bin, "qemu-img.exe"); } }

    // The installed release, e.g. "Alpha 10", from the first line of WINQ-EMU's README.txt.
    public static string Version
    {
        get
        {
            if (Bin == null) return null;
            foreach (var dir in new[] { Bin, Path.GetDirectoryName(Bin) })
            {
                try
                {
                    string f = Path.Combine(dir, "README.txt");
                    if (!File.Exists(f)) continue;
                    var m = Regex.Match(File.ReadLines(f).FirstOrDefault() ?? "", @"^\s*WINQ-EMU\s+(.+?)\s*$", RegexOptions.IgnoreCase);
                    if (m.Success) return m.Groups[1].Value;
                }
                catch { }
            }
            return null;
        }
    }

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

// The QEMU guest agent protocol: one JSON command per line on the virtio-serial channel
// "org.qemu.guest_agent.0", which QEMU passes to a local TCP socket.
static class GuestAgent
{
    // The OS's name from /etc/os-release (e.g. "Fedora Linux 45 (Workstation Edition)"), or null when
    // no agent answers.
    public static string OsName(int port)
    {
        try
        {
            using (var c = new TcpClient("127.0.0.1", port))
            using (var s = c.GetStream())
            {
                s.ReadTimeout = 3000;
                var r = new StreamReader(s);
                var w = new StreamWriter(s) { AutoFlush = true, NewLine = "\n" };
                // guest-sync with a fresh id, so a reply left over from an earlier client is skipped.
                int id = new Random().Next(1, int.MaxValue);
                w.WriteLine("{\"execute\":\"guest-sync\",\"arguments\":{\"id\":" + id + "}}");
                string line;
                while ((line = r.ReadLine()) != null && !line.Contains(id.ToString())) { }
                if (line == null) return null;
                w.WriteLine("{\"execute\":\"guest-get-osinfo\"}");
                line = r.ReadLine();
                if (line == null) return null;
                var reply = new System.Web.Script.Serialization.JavaScriptSerializer().DeserializeObject(line) as Dictionary<string, object>;
                object ret;
                if (reply == null || !reply.TryGetValue("return", out ret)) return null;
                var info = ret as Dictionary<string, object>;
                if (info == null) return null;
                Func<string, string> get = k => { object v; return info.TryGetValue(k, out v) && v != null ? v.ToString().Trim() : ""; };
                string name = get("pretty-name");
                if (name.Length == 0) name = (get("name") + " " + get("version")).Trim();
                if (name.Length == 0 || name.Length > 100) return null;
                return Regex.Replace(name, @"[\r\n]+", " ");
            }
        }
        catch { return null; }
    }
}

class RunningVm
{
    public VmConfig Cfg;
    public Process P;
    public int QmpPort, EventPort, AgentPort;
    public bool Installer, Cpu;
    // QEMU's window, kept because Process.MainWindowHandle is zero while it's hidden.
    public IntPtr Window;
    public bool Hidden, Paused;
    // Opened from Console while off (QEMU -S): the window is open but the machine hasn't been powered on.
    public bool Held;
    // Power menu: Restart asks Linux to shut down, then starts it again (if it shuts down within a few
    // minutes, so a cancelled prompt doesn't turn a later shutdown into a restart); Reset and Force stop end QEMU.
    public DateTime RestartAsked = DateTime.MinValue;
    public bool ResetAsked, ForceStopped;
    public DateTime Started = DateTime.Now;
    public readonly StringBuilder Err = new StringBuilder();

    // Live activity, shown on its row on Virtual Machines: CPU as a share of the cores it was given, the memory
    // QEMU holds on this PC, and how fast its disks are read and written (QEMU's query-blockstats). -1: not known yet.
    public double CpuPct = -1, DiskRate = -1;
    public long MemBytes = -1;
    // The memory Linux itself is using (total less available), from the balloon's statistics; -1 until Linux's
    // driver has reported, or for a system started without the balloon.
    public long GuestUsed = -1;
    bool balloonAsked;
    TimeSpan lastCpu;
    long lastDisk = -1;
    DateTime lastAt = DateTime.MinValue;

    // Takes a reading; rates come from the change since the last one. Slow (it asks QEMU), so not on the UI thread.
    public void Sample()
    {
        var now = DateTime.UtcNow;
        double wall = lastAt == DateTime.MinValue ? 0 : (now - lastAt).TotalSeconds;
        int cores, ram;
        Cfg.Resources(out cores, out ram);
        try
        {
            P.Refresh();
            var cpu = P.TotalProcessorTime;
            MemBytes = P.WorkingSet64;
            if (wall > 0) CpuPct = Math.Max(0, Math.Min(100, (cpu - lastCpu).TotalSeconds / wall / Math.Max(1, cores) * 100));
            lastCpu = cpu;
        }
        catch { }
        try
        {
            long total = DiskBytes(Net.QmpQuery(QmpPort, "query-blockstats"));
            if (wall > 0 && lastDisk >= 0) DiskRate = Math.Max(0, (total - lastDisk) / wall);
            lastDisk = total;
        }
        catch { }
        try
        {
            // Linux's driver reports only once asked to, every couple of seconds.
            if (!balloonAsked)
            {
                Net.QmpQuery(QmpPort, "qom-set", "{\"path\":\"/machine/peripheral/balloon0\",\"property\":\"guest-stats-polling-interval\",\"value\":2}");
                balloonAsked = true;
            }
            GuestUsed = BalloonUsed(Net.QmpQuery(QmpPort, "qom-get", "{\"path\":\"/machine/peripheral/balloon0\",\"property\":\"guest-stats\"}"));
        }
        catch { }
        lastAt = now;
    }

    // Total less available memory, as Linux's "free" counts it as used; -1 if Linux hasn't reported yet.
    static long BalloonUsed(string reply)
    {
        var doc = new System.Web.Script.Serialization.JavaScriptSerializer().DeserializeObject(reply) as Dictionary<string, object>;
        var ret = doc != null && doc.ContainsKey("return") ? doc["return"] as Dictionary<string, object> : null;
        if (ret == null || Stat(ret, "last-update") <= 0) return -1;
        var stats = (Dictionary<string, object>)ret["stats"];
        long total = Stat(stats, "stat-total-memory"), avail = Stat(stats, "stat-available-memory");
        if (avail < 0) avail = Stat(stats, "stat-free-memory");
        return total > 0 && avail >= 0 ? Math.Max(0, total - avail) : -1;
    }

    // A balloon statistic; -1 when missing or unknown (QEMU reports those as 2^64 - 1).
    static long Stat(Dictionary<string, object> d, string key)
    {
        object v;
        if (!d.TryGetValue(key, out v)) return -1;
        decimal n = Convert.ToDecimal(v);
        return n < 0 || n > long.MaxValue ? -1 : (long)n;
    }

    // Bytes read and written so far across the system's drives (each drive's own counts, not its layers').
    static long DiskBytes(string reply)
    {
        var doc = new System.Web.Script.Serialization.JavaScriptSerializer().DeserializeObject(reply) as Dictionary<string, object>;
        long total = 0;
        foreach (var o in (object[])doc["return"])
        {
            var stats = ((Dictionary<string, object>)o)["stats"] as Dictionary<string, object>;
            if (stats == null) continue;
            total += Convert.ToInt64(stats["rd_bytes"]) + Convert.ToInt64(stats["wr_bytes"]);
        }
        return total;
    }

    // Why QEMU stopped, from its SHUTDOWN event: "guest-shutdown" for a power-off, "guest-reset" for a restart.
    public volatile string ShutdownReason;
    public System.Threading.Thread Watcher;

    // Asks the QEMU guest agent inside Linux (qemu-guest-agent, if installed) which OS is running, and
    // saves its name to guest-os.txt in the system's folder. Tries every 15 seconds for 15 minutes, as
    // the agent only answers once Linux has booted; without an agent nothing changes.
    public void WatchAgent(Action found)
    {
        var vm = this;
        new System.Threading.Thread(() =>
        {
            for (int i = 0; i < 60; i++)
            {
                System.Threading.Thread.Sleep(15000);
                try { if (vm.P.HasExited) return; } catch { return; }
                string name = GuestAgent.OsName(vm.AgentPort);
                if (name == null) continue;
                try
                {
                    string file = Path.Combine(vm.Cfg.Dir, "guest-os.txt");
                    if (!File.Exists(file) || File.ReadAllText(file).Trim() != name)
                    {
                        File.WriteAllText(file, name + "\r\n");
                        found();
                    }
                }
                catch { }
                return;
            }
        }) { IsBackground = true, Name = "Guest agent" }.Start();
    }

    // Listens on a second QMP monitor (the first stays free for commands like system_powerdown).
    public void WatchEvents()
    {
        var vm = this;
        Watcher = new System.Threading.Thread(() =>
        {
            for (int i = 0; i < 240; i++)
            {
                try { if (vm.P.HasExited) return; } catch { return; }
                try
                {
                    using (var c = new TcpClient("127.0.0.1", vm.EventPort))
                    using (var s = c.GetStream())
                    {
                        var r = new StreamReader(s);
                        var w = new StreamWriter(s) { AutoFlush = true, NewLine = "\n" };
                        r.ReadLine();
                        w.WriteLine("{\"execute\":\"qmp_capabilities\"}");
                        string line;
                        while ((line = r.ReadLine()) != null)
                        {
                            var m = Regex.Match(line, "\"event\"\\s*:\\s*\"SHUTDOWN\".*?\"reason\"\\s*:\\s*\"([^\"]+)\"");
                            if (m.Success) vm.ShutdownReason = m.Groups[1].Value;
                        }
                        return;
                    }
                }
                catch (SocketException) { System.Threading.Thread.Sleep(250); }
                catch (IOException) { return; }
            }
        }) { IsBackground = true, Name = "QMP events" };
        Watcher.Start();
    }
}

// ---------------------------------------------------------------- network

class ResolvedIso
{
    public string Url, FileName, Sha256;   // Sha256: the official SHA-256, or SHA-512 when that's all a project publishes
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

    public static string Sha256(string file, Func<bool> cancelled, Action<double> report) { return Hash(file, false, cancelled, report); }

    // SHA-256, or SHA-512 for projects that only publish that (Mageia, EndeavourOS, openSUSE Leap 16).
    public static string Hash(string file, bool sha512, Func<bool> cancelled, Action<double> report)
    {
        using (HashAlgorithm sha = sha512 ? (HashAlgorithm)SHA512.Create() : SHA256.Create())
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
    public static void Qmp(int port, string command) { Qmp(port, command, null); }

    // A query's reply (its JSON line), for reading what QEMU reports, such as query-blockstats.
    public static string QmpQuery(int port, string command, string args = null)
    {
        using (var c = new TcpClient())
        {
            if (!c.ConnectAsync("127.0.0.1", port).Wait(1000)) throw new TimeoutException("QMP");
            using (var s = c.GetStream())
            {
                s.ReadTimeout = 2000;
                var r = new StreamReader(s);
                var w = new StreamWriter(s) { AutoFlush = true, NewLine = "\n" };
                r.ReadLine();
                w.WriteLine("{\"execute\":\"qmp_capabilities\"}");
                r.ReadLine();
                w.WriteLine("{\"execute\":\"" + command + "\"" + (args != null ? ",\"arguments\":" + args : "") + "}");
                string reply = r.ReadLine();
                while (reply != null && reply.Contains("\"event\"")) reply = r.ReadLine();
                return reply;
            }
        }
    }

    // With arguments (a JSON object); throws when QEMU answers with an error.
    public static void Qmp(int port, string command, string args)
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
            w.WriteLine("{\"execute\":\"" + command + "\"" + (args != null ? ",\"arguments\":" + args : "") + "}");
            string reply = r.ReadLine();
            // Events can arrive before the reply.
            while (reply != null && reply.Contains("\"event\"")) reply = r.ReadLine();
            if (args != null && reply != null && reply.Contains("\"error\""))
            {
                var m = System.Text.RegularExpressions.Regex.Match(reply, "\"desc\":\\s*\"([^\"]*)\"");
                throw new Exception(m.Success ? m.Groups[1].Value : reply);
            }
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
        // A SHA-256 (64 hex digits) or SHA-512 (128) checksum; the download is checked with whichever it is.
        var hex = new Regex(@"\b([0-9a-fA-F]{128}|[0-9a-fA-F]{64})\b");
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
    [DllImport("gdi32.dll")] public static extern IntPtr AddFontMemResourceEx(IntPtr font, uint size, IntPtr reserved, ref uint fonts);
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(Microsoft.Win32.SafeHandles.SafeFileHandle h, uint code, ref byte input, int inputSize,
        IntPtr output, int outputSize, out int returned, IntPtr overlapped);

    // Turns a file's NTFS sparse flag off, so Windows allocates its whole length on the drive now
    // (FSCTL_SET_SPARSE with SetSparse = false). Used for a disk created with its full size allocated.
    public static void ClearSparse(string file)
    {
        using (var fs = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            byte setSparse = 0;
            int returned;
            if (!DeviceIoControl(fs.SafeFileHandle, 0x000900C4, ref setSparse, 1, IntPtr.Zero, 0, out returned, IntPtr.Zero))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Couldn't allocate the disk's full size");
        }
    }

    // Space between a text box's border and its text, on both sides (EM_SETMARGINS; set again
    // once the box has a window, as Windows only keeps it from then on).
    public static void TextMargins(TextBox t, int designPx = 6)
    {
        int px = Theme.D(designPx);
        // EC_LEFTMARGIN | EC_RIGHTMARGIN; a NoteBox keeps its own right margin, which leaves room for its scroll bar.
        int which = t is NoteBox ? 1 : 3;
        Action set = () => SendMessage(t.Handle, 0xD3, (IntPtr)which, (IntPtr)(px | (px << 16)));
        if (t.IsHandleCreated) set();
        t.HandleCreated += delegate { set(); };
        t.FontChanged += delegate { if (t.IsHandleCreated) set(); };
    }
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

    // Makes Windows draw the title bar again (after DarkTitleBar changes it on a window that's showing).
    public static void RedrawFrame(IntPtr h)
    {
        try { SetWindowPos(h, IntPtr.Zero, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0004 | 0x0010 | 0x0020); } catch { }   // NOSIZE NOMOVE NOZORDER NOACTIVATE FRAMECHANGED
    }

    // Windows 11's rounded corners (and the shadow that goes with them) on a pop-up: a menu or a dropdown's list.
    // Earlier Windows ignores it and keeps them square.
    public static void RoundCorners(IntPtr h)
    {
        int round = 2;   // DWMWCP_ROUND
        try { DwmSetWindowAttribute(h, 33, ref round, 4); } catch { }   // DWMWA_WINDOW_CORNER_PREFERENCE
    }

    // A dark title bar for the dark themes, the usual light one for Light.
    public static void DarkTitleBar(IntPtr h)
    {
        int on = Theme.Light ? 0 : 1;
        try { DwmSetWindowAttribute(h, 20, ref on, 4); } catch { }
    }

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] static extern int SetWindowTheme(IntPtr h, string app, string idList);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);

    // Windows 10/11 dark scrollbars for a scrollable control.
    // Scrollbars drawn to match: Windows' dark ones for the dark themes, its usual ones for Light.
    public static string ScrollbarTheme { get { return Theme.Light ? "Explorer" : "DarkMode_Explorer"; } }

    public static void DarkScrollbars(Control c)
    {
        EventHandler apply = delegate { try { SetWindowTheme(c.Handle, ScrollbarTheme, null); } catch { } };
        if (c.IsHandleCreated) apply(c, EventArgs.Empty); else c.HandleCreated += apply;
    }

    // ---- closing a VM window hides it instead (QEMU runs with window-close=off)

    public delegate IntPtr HookProc(int code, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr module, uint thread);
    [DllImport("user32.dll")] public static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] public static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr w, IntPtr l);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string name);
    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr h, uint flags);
    [DllImport("user32.dll")] static extern bool PhysicalToLogicalPointForPerMonitorDPI(IntPtr h, ref POINT p);
    [DllImport("user32.dll")] static extern IntPtr SendMessageTimeout(IntPtr h, uint msg, IntPtr w, IntPtr l, uint flags, uint ms, out IntPtr result);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    public struct POINT { public int X, Y; }

    // A low-level mouse hook (WH_MOUSE_LL); `proc` must stay referenced while it's installed.
    public static IntPtr HookMouse(HookProc proc) { return SetWindowsHookEx(14, proc, GetModuleHandle(null), 0); }

    // True when (x, y), in physical screen pixels, is on the close button of top-level window `h`.
    public static bool OnCloseButton(IntPtr h, int x, int y)
    {
        var p = new POINT { X = x, Y = y };
        if (GetAncestor(WindowFromPoint(p), 2) != h) return false;   // GA_ROOT
        try { PhysicalToLogicalPointForPerMonitorDPI(h, ref p); } catch (EntryPointNotFoundException) { }
        IntPtr hit;
        if (SendMessageTimeout(h, 0x0084, IntPtr.Zero, (IntPtr)((p.Y << 16) | (p.X & 0xFFFF)), 0x0002, 500, out hit) == IntPtr.Zero)   // WM_NCHITTEST, SMTO_ABORTIFHUNG
            return false;
        return (int)hit == 20;   // HTCLOSE
    }

    public static void HideWindow(IntPtr h) { ShowWindow(h, 0); }   // SW_HIDE

    public static void ShowAndFocus(IntPtr h)
    {
        if (h == IntPtr.Zero) return;
        if (!IsWindowVisible(h)) ShowWindow(h, 5);   // SW_SHOW
        BringToFront(h);
    }

    public static void MoveTo(IntPtr h, int x, int y)
    {
        SetWindowPos(h, IntPtr.Zero, x, y, 0, 0, 0x0001 | 0x0004);   // SWP_NOSIZE | SWP_NOZORDER
    }

    // QEMU's SDL window toggles fullscreen on Ctrl+Alt+F.
    public static void SendCtrlAltF(IntPtr h)
    {
        SetForegroundWindow(h);
        // SDL reads the hardware scan code and QEMU checks for the left Ctrl and Alt keys, so send
        // those exactly: VK_LCONTROL, VK_LMENU and F with their scan codes.
        byte[,] keys = { { 0xA2, 0x1D }, { 0xA4, 0x38 }, { 0x46, 0x21 } };
        for (int i = 0; i < 3; i++) keybd_event(keys[i, 0], keys[i, 1], 0, UIntPtr.Zero);
        for (int i = 2; i >= 0; i--) keybd_event(keys[i, 0], keys[i, 1], 2, UIntPtr.Zero);   // KEYEVENTF_KEYUP
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
    // "Depth": a deep background with surfaces shaded from top to bottom, and a lavender outline on
    // what's selected or open.
    // Two themes, chosen in Settings > General > Appearance (settings.ini: theme=dark or light;
    // TRYLINUX_THEME overrides it, for previews):
    //   Dark: Firefox's dark Settings page. Neutral grays (page #111113, cards #252429, panel #1C1B22,
    //         borders #39383D) with lavender #B89CFE for what's selected and the main buttons.
    //   Light: Proton Pass's light settings. A lavender-white page (#FCF9FE), white cards, soft lavender-grey
    //         lines (#DEDBE8) and Proton's purple #6D4AFF for what's selected and the main buttons.
    public static string Name { get; private set; }
    public static bool Light { get { return Name == "light"; } }
    public static readonly string[] Names = { "dark", "light" };
    public static readonly string[] Titles = { "Dark", "Light" };
    static Theme() { Apply("dark"); }
    static Color C(string dark, string light) { return ColorTranslator.FromHtml(Name == "light" ? light : dark); }

    // Sets every color for a theme (dark or light; TRYLINUX_THEME overrides it, for previews).
    // Controls take their colors when they're made, so the window redraws its page afterwards.
    public static void Apply(string name)
    {
        string forced = Environment.GetEnvironmentVariable("TRYLINUX_THEME");
        name = ((string.IsNullOrEmpty(forced) ? name : forced) ?? "").Trim().ToLowerInvariant();
        Name = Names.Contains(name) ? name : "dark";
        Bg = C("#111113", "#FCF9FE");
        Card = C("#252429", "#FFFFFF");
        CardHover = C("#2E2D33", "#F6F2FC");
        CardSelected = C("#352E48", "#EEE8FF");
        CardTop = C("#28272C", "#FFFFFF");
        CardBottom = C("#232227", "#FBF9FE");
        HoverTop = C("#302F35", "#F8F5FD");
        HoverBottom = C("#2A292F", "#F2EEFA");
        SelectedTop = C("#372F4D", "#F3EEFF");
        SelectedBottom = C("#2C263D", "#E9E1FF");
        Border = C("#39383D", "#DEDBE8");
        Text = C("#FBFBFE", "#0C0C14");
        Muted = C("#BFBFC9", "#6B6880");
        Accent = C("#B89CFE", "#6D4AFF");
        AccentHover = C("#C7B1FF", "#8466FF");
        Accent2 = C("#5E4898", "#3A1F99");
        Highlight = C("#C7B1FF", "#5B2BD1");
        Highlight2 = C("#B89CFE", "#3A1F99");
        AccentLight = C("#D6C7FF", "#9A82FF");
        AccentMid = C("#C2A9FF", "#7A5CFF");
        AccentDark = C("#A487F2", "#5A37E6");
        OnAccent = C("#15141A", "#FFFFFF");
        Success = C("#7ED68A", "#1C8A4E");
        Danger = C("#F26D6D", "#D6332E");
        Warning = C("#F9D949", "#B38600");
        BoxFill = C("#1C1B22", "#F4F1F9");
    }
    public static Color Bg;
    public static Color Card;                  // fields and flat fills
    public static Color CardHover;
    public static Color CardSelected;
    public static Color CardTop, CardBottom;
    public static Color HoverTop, HoverBottom;
    public static Color SelectedTop, SelectedBottom;
    public static Color Border;
    public static Color Text;
    public static Color Muted;
    // Accent is the main call to action only (Start, Next, Install). Highlight is the logo and
    // active form elements: selection, tabs, checkboxes, progress, section titles.
    public static Color Accent;
    public static Color AccentHover;
    public static Color Accent2;
    public static Color Highlight;
    public static Color Highlight2;
    // The primary button: its hover and pressed shades, and its text (dark on the lavender, white on the purple).
    public static Color AccentLight, AccentMid, AccentDark;
    public static Color OnAccent;

    // Corner rounding for cards, bars, tabs and buttons, in design units (like the Linux edition's GTK look).
    public static int Radius = 8;

    // Rounded on the top corners, the bottom ones, or both (rows joined into one card round only its ends).
    public static GraphicsPath Shape(Rectangle r, int radius, bool top, bool bottom)
    {
        var p = new GraphicsPath();
        int d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d <= 1 || (!top && !bottom)) { p.AddRectangle(r); return p; }
        if (top) { p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90); }
        else { p.AddLine(r.X, r.Y, r.Right, r.Y); }
        if (bottom) { p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90); }
        else { p.AddLine(r.Right, r.Bottom, r.X, r.Bottom); }
        p.CloseFigure();
        return p;
    }

    public static Color Mix(Color a, Color b, double t)
    {
        Func<int, int, int> m = (x, y) => (int)Math.Round(x + (y - x) * t);
        return Color.FromArgb(m(a.R, b.R), m(a.G, b.G), m(a.B, b.B));
    }

    public static GraphicsPath Shape(Rectangle r, int radius)
    {
        var p = new GraphicsPath();
        int d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d <= 1) { p.AddRectangle(r); return p; }
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    // A surface: shaded top to bottom, with a border, drawn a little heavier when `glow` (selected or
    // open) is set. There's no glow inside the edge any more, only the outline.
    public static void Surface(Graphics g, Rectangle bounds, Color behind, Color top, Color bottom, Color border, bool glow)
    {
        if (!behind.IsEmpty) g.Clear(behind);   // Color.Empty: draw over what's there (one of several on a control)
        var mode = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new Rectangle(bounds.X, bounds.Y, bounds.Width - 1, bounds.Height - 1);
        int rad = D(Radius);
        using (var path = Shape(r, rad))
        {
            using (var b = new LinearGradientBrush(new Rectangle(r.X, r.Y - 1, r.Width + 1, r.Height + 2), top, bottom, LinearGradientMode.Vertical))
                g.FillPath(b, path);
            using (var p = new Pen(border, glow ? 1.5f : 1f)) g.DrawPath(p, path);
        }
        g.SmoothingMode = mode;
    }
    public static Color Success;          // a running system
    public static Color Danger;           // red, apart from the accent
    public static Color Warning;
    public static Color BoxFill;          // containers that group rows

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

    // Page and dialog titles are Prompt, section bars and headings Work Sans, everything else (descriptions,
    // labels, buttons, tabs, system names) Noto Sans. The three are embedded in the exe (Open Font License,
    // see fonts\); if they can't be loaded the app falls back to Segoe UI. The logo stays pixel art.
    public static Font Mono(float size, FontStyle style = FontStyle.Regular) { return new Font(Family, size, style); }
    static readonly PrivateFontCollection bundled = LoadBundled();
    static PrivateFontCollection LoadBundled()
    {
        var pfc = new PrivateFontCollection();
        try
        {
            var asm = typeof(Theme).Assembly;
            foreach (var name in new[] { "Prompt-SemiBold.ttf", "WorkSans-SemiBold.ttf", "NotoSans-Regular.ttf", "NotoSans-SemiBold.ttf" })
                using (var s = asm.GetManifestResourceStream(name))
                {
                    if (s == null) continue;
                    var data = new byte[s.Length];
                    s.Read(data, 0, data.Length);
                    var mem = Marshal.AllocCoTaskMem(data.Length);   // kept for the life of the process, as both APIs need
                    Marshal.Copy(data, 0, mem, data.Length);
                    pfc.AddMemoryFont(mem, data.Length);
                    uint count = 0;
                    Native.AddFontMemResourceEx(mem, (uint)data.Length, IntPtr.Zero, ref count);   // so GDI text (TextRenderer, native controls) finds it too
                }
        }
        catch { }
        return pfc;
    }
    static Font Pick(string family, float size, string fallback, float fallbackSize)
    {
        try
        {
            var f = bundled.Families.FirstOrDefault(x => x.Name == family);
            if (f != null && f.IsStyleAvailable(FontStyle.Regular)) return new Font(f, size);
        }
        catch { }
        return new Font(fallback, fallbackSize);
    }
    public static readonly Font Title = Pick("Prompt SemiBold", 15f, "Segoe UI Semibold", 16f);
    public static readonly Font Heading = Pick("Work Sans SemiBold", 11f, "Segoe UI Semibold", 11.5f);
    public static readonly Font Body = Pick("Noto Sans", 9.5f, "Segoe UI", 10f);
    public static readonly Font Bold = Pick("Noto Sans SemiBold", 9.5f, "Segoe UI Semibold", 10f);
    public static readonly Font Small = Pick("Noto Sans", 8.5f, "Segoe UI", 9f);
    public static readonly Font RowTitle = Pick("Noto Sans SemiBold", 10f, "Segoe UI Semibold", 10.5f);   // a system or distribution name in a list

    public static int D(int v) { return (int)Math.Round(v * Scale); }

    // Every word starting with a capital letter, as buttons and section titles show their text
    // ("Check for updates" is drawn "Check For Updates"); the rest of each word is left as it is.
    public static string TitleCase(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        var words = s.Split(' ');
        for (int i = 0; i < words.Length; i++)
            if (words[i].Length > 0 && char.IsLower(words[i][0])) words[i] = char.ToUpper(words[i][0]) + words[i].Substring(1);
        return string.Join(" ", words);
    }
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
        int shadow = Math.Max(1, px / 3);
        // Center the letters plus their drop shadow, so the logo looks centered, not just measures it.
        int x0 = (Width - cols * px - shadow) / 2, y0 = (Height - 7 * px - shadow) / 2;
        using (var sb = new SolidBrush(Theme.Accent2))
            DrawWord(e.Graphics, x0 + shadow, y0 + shadow, px, r => sb);
        var rows = Enumerable.Range(0, 7).Select(r => new SolidBrush(Lerp(Theme.Highlight, Theme.Highlight2, r / 6f))).ToArray();
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
        using (var b = new SolidBrush(Theme.Highlight))
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

// A message box in the app's own style: icon, title, text, an optional highlighted path, and named buttons.
static class ThemedDialog
{
    // Returns true when the primary button is chosen. With no secondary button it is a notice.
    public static bool Show(IWin32Window owner, string title, string before, string path, string after,
        string primary, string secondary = null, bool danger = false)
    {
        return Choose(owner, title, before, path, after, primary, secondary, danger) == DialogResult.OK;
    }

    // OK for the primary button, No for the secondary one, Cancel when the window is closed (X or Esc).
    public static DialogResult Choose(IWin32Window owner, string title, string before, string path, string after,
        string primary, string secondary = null, bool danger = false)
    {
        Func<int, int> D = Theme.D;
        int w = D(560), pad = D(24), textX = D(80), textW = w - textX - pad;
        using (var f = new DialogForm { Text = "Try Linux",
            StartPosition = owner == null ? FormStartPosition.CenterScreen : FormStartPosition.CenterParent,
            BackColor = Theme.Bg, ForeColor = Theme.Text, Font = Theme.Body, ShowInTaskbar = owner == null, Icon = Icons.App() })
        {
            var icon = new PictureBox { Image = Icons.Draw(D(40)), SizeMode = PictureBoxSizeMode.AutoSize, BackColor = Theme.Bg };
            icon.Location = new Point(pad, pad);
            f.Controls.Add(icon);

            int y = pad;
            Func<string, Font, Color, Color, int, Control> add = (text, font, fore, back, inset) =>
            {
                var size = TextRenderer.MeasureText(text, font, new Size(textW - inset * 2, 0), TextFormatFlags.WordBreak);
                var l = new Label { Text = text, Font = font, ForeColor = fore, BackColor = back, Padding = new Padding(inset) };
                l.SetBounds(textX, y, textW, size.Height + inset * 2);
                f.Controls.Add(l);
                y = l.Bottom;
                return l;
            };
            add(title, Theme.Title, Theme.Text, Theme.Bg, 0);
            y += D(10);
            if (!string.IsNullOrEmpty(before)) { add(before, Theme.Body, Theme.Muted, Theme.Bg, 0); y += D(10); }
            if (!string.IsNullOrEmpty(path)) { add(path, Theme.Small, Theme.Highlight, Theme.Card, D(8)); y += D(10); }
            if (!string.IsNullOrEmpty(after)) { add(after, Theme.Body, Theme.Muted, Theme.Bg, 0); y += D(10); }
            y = Math.Max(y, icon.Bottom) + D(14);

            int bw = D(150), bh = D(38), x = w - pad - bw;
            var ok = new FlatBtn(primary, !danger, danger) { DialogResult = DialogResult.OK };
            ok.SetBounds(x, y, bw, bh);
            f.Controls.Add(ok);
            FlatBtn cancel = null;
            if (secondary != null)
            {
                cancel = new FlatBtn(secondary) { DialogResult = DialogResult.No };
                cancel.SetBounds(x - bw - D(10), y, bw, bh);
                f.Controls.Add(cancel);
            }
            f.ClientSize = new Size(w, y + bh + pad);
            // No title bar: drag the dialog by any part that isn't a button.
            foreach (Control c in f.Controls) if (!(c is Button)) f.DragBy(c);
            // Enter picks the primary button, except for destructive actions.
            f.AcceptButton = danger && cancel != null ? cancel : ok;
            // Esc closes the dialog (it has no X button).
            f.KeyPreview = true;
            f.KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) { f.DialogResult = DialogResult.Cancel; f.Close(); } };
            f.Shown += delegate { f.Activate(); ((Control)f.AcceptButton).Focus(); };
            var result = owner == null ? f.ShowDialog() : f.ShowDialog(owner);
            return result == DialogResult.OK || result == DialogResult.No ? result : DialogResult.Cancel;
        }
    }
}

enum NoticeKind { Info, Success, Warning, Error }

// The window's notification area, above every page: updates, results and problems that don't need an
// answer. Each notification has a colored edge and icon by kind, an optional action button and a dismiss X.
class NoticeBar : Panel
{
    class Item { public string Id, Text, ActionText; public NoticeKind Kind; public Action OnAction; }
    readonly List<Item> items = new List<Item>();
    readonly HashSet<string> dismissed = new HashSet<string>();
    const int ColW = 640;
    static readonly string IconFont = "Segoe MDL2 Assets";

    public NoticeBar()
    {
        Dock = DockStyle.Top;
        BackColor = Theme.Bg;
        Visible = false;
    }

    // Redraws the notifications in the current theme's colors.
    public void Restyle() { BackColor = Theme.Bg; Rebuild(); }

    public static Color KindColor(NoticeKind k)
    {
        switch (k)
        {
            case NoticeKind.Success: return Theme.Success;
            case NoticeKind.Warning: return Theme.Warning;
            case NoticeKind.Error: return Theme.Danger;
            default: return Theme.Highlight;
        }
    }

    static string Glyph(NoticeKind k)
    {
        switch (k)
        {
            case NoticeKind.Success: return "";   // Completed
            case NoticeKind.Warning: return "";   // Warning
            case NoticeKind.Error: return "";     // ErrorBadge
            default: return "";                    // Info
        }
    }

    // Shows a notification, replacing one with the same id. Ones the user dismissed stay hidden this session.
    public void Post(string id, NoticeKind kind, string text, string actionText = null, Action onAction = null)
    {
        if (dismissed.Contains(id)) return;
        items.RemoveAll(i => i.Id == id);
        items.Add(new Item { Id = id, Kind = kind, Text = text, ActionText = actionText, OnAction = onAction });
        Rebuild();
    }

    public void Remove(string id) { if (items.RemoveAll(i => i.Id == id) > 0) Rebuild(); }

    void Rebuild()
    {
        SuspendLayout();
        foreach (Control c in Controls.Cast<Control>().ToList()) { Controls.Remove(c); c.Dispose(); }
        Func<int, int> D = Theme.D;
        int y = D(12), w = D(ColW);
        foreach (var it in items)
        {
            var item = it;
            int actionW = item.ActionText != null ? D(110) : 0;
            int textW = w - D(16 + 30 + 12 + 40) - (actionW > 0 ? actionW + D(10) : 0);
            int textH = TextRenderer.MeasureText(item.Text, Theme.Body, new Size(textW, 0), TextFormatFlags.WordBreak).Height;
            int h = Math.Max(D(44), textH + D(20));
            var row = new Panel { BackColor = Theme.Card, Tag = "row" };
            row.SetBounds(0, y, w, h);
            Color kc = KindColor(item.Kind);
            row.Paint += (s, e) =>
            {
                using (var p = new Pen(Theme.Border)) e.Graphics.DrawRectangle(p, 0, 0, row.Width - 1, row.Height - 1);
                using (var b = new SolidBrush(kc)) e.Graphics.FillRectangle(b, 0, 0, D(4), row.Height);
            };
            var icon = new Label { Text = Glyph(item.Kind), Font = new Font(IconFont, 12f), ForeColor = kc, BackColor = Theme.Card,
                                   TextAlign = ContentAlignment.MiddleCenter };
            icon.SetBounds(D(16), 1, D(30), h - 2);
            var text = new Label { Text = item.Text, Font = Theme.Body, ForeColor = Theme.Text, BackColor = Theme.Card,
                                   TextAlign = ContentAlignment.MiddleLeft, UseMnemonic = false };
            text.SetBounds(D(16 + 30 + 6), 1, textW, h - 2);
            var close = new Label { Text = "", Font = new Font(IconFont, 9f), ForeColor = Theme.Muted, BackColor = Theme.Card,
                                    TextAlign = ContentAlignment.MiddleCenter, Cursor = Cursors.Hand };
            close.SetBounds(w - D(40), 1, D(36), h - 2);
            close.MouseEnter += delegate { close.ForeColor = Theme.Text; };
            close.MouseLeave += delegate { close.ForeColor = Theme.Muted; };
            close.Click += delegate { dismissed.Add(item.Id); Remove(item.Id); };
            row.Controls.Add(icon);
            row.Controls.Add(text);
            row.Controls.Add(close);
            if (item.ActionText != null)
            {
                var act = new FlatBtn(item.ActionText);
                act.ForeColor = kc;
                act.FlatAppearance.BorderColor = kc;
                act.SetBounds(w - D(40) - actionW - D(4), (h - D(30)) / 2, actionW, D(30));
                act.Click += delegate { if (item.OnAction != null) item.OnAction(); };
                row.Controls.Add(act);
            }
            Controls.Add(row);
            y += h + D(8);
        }
        Height = items.Count > 0 ? y + D(4) : 0;
        Visible = items.Count > 0;
        PlaceRows();
        ResumeLayout();
    }

    void PlaceRows()
    {
        int x = Math.Max(0, (ClientSize.Width - Theme.D(ColW)) / 2);
        foreach (Control c in Controls) c.Left = x;
    }

    protected override void OnResize(EventArgs e) { base.OnResize(e); PlaceRows(); }
}

// A dialog window with no title bar: a thin border, rounded corners and a shadow, dragged by its body.
class DialogForm : Form
{
    [DllImport("user32.dll")] static extern bool ReleaseCapture();
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int value, int size);

    public DialogForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        MaximizeBox = MinimizeBox = false;
        DragBy(this);
    }

    protected override CreateParams CreateParams
    {
        get { var cp = base.CreateParams; cp.ClassStyle |= 0x20000; return cp; }   // CS_DROPSHADOW
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int round = 2;   // DWMWCP_ROUND (Windows 11; ignored elsewhere)
        try { DwmSetWindowAttribute(Handle, 33, ref round, 4); } catch { }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using (var p = new Pen(Theme.Border)) e.Graphics.DrawRectangle(p, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
    }

    // Pressing on `c` and moving drags the window, as a title bar would.
    public void DragBy(Control c)
    {
        c.MouseDown += (s, e) =>
        {
            if (e.Button != MouseButtons.Left) return;
            ReleaseCapture();
            SendMessage(Handle, 0x00A1, (IntPtr)2, IntPtr.Zero);   // WM_NCLBUTTONDOWN, HTCAPTION
        };
    }
}

// Menus in the app's dark colours.
class DarkMenu : ToolStripProfessionalRenderer
{
    public DarkMenu() : base(new Colors()) { RoundedEdges = false; }

    // Menus get rounded corners, like the rest of the app.
    protected override void Initialize(ToolStrip toolStrip)
    {
        base.Initialize(toolStrip);
        if (!(toolStrip is ToolStripDropDown)) return;
        if (toolStrip.IsHandleCreated) Native.RoundCorners(toolStrip.Handle);
        toolStrip.HandleCreated += delegate { Native.RoundCorners(toolStrip.Handle); };
    }

    protected override void InitializeItem(ToolStripItem item)
    {
        base.InitializeItem(item);
        var sub = item as ToolStripDropDownItem;   // a submenu
        if (sub != null) sub.DropDown.HandleCreated += delegate { Native.RoundCorners(sub.DropDown.Handle); };
    }

    // Windows draws the rounded edge itself; a square line of our own would show at the corners.
    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        if (e.ToolStrip is ToolStripDropDown && Environment.OSVersion.Version.Build >= 22000) return;
        base.OnRenderToolStripBorder(e);
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = !e.Item.Enabled ? Theme.Muted : e.Item.ForeColor == Theme.Danger ? Theme.Danger : Theme.Text;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e) { e.ArrowColor = Theme.Text; base.OnRenderArrow(e); }

    class Colors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground { get { return Theme.Card; } }
        public override Color ImageMarginGradientBegin { get { return Theme.Card; } }
        public override Color ImageMarginGradientMiddle { get { return Theme.Card; } }
        public override Color ImageMarginGradientEnd { get { return Theme.Card; } }
        public override Color MenuBorder { get { return Theme.Border; } }
        public override Color MenuItemBorder { get { return Theme.Highlight; } }
        public override Color MenuItemSelected { get { return Theme.CardSelected; } }
        public override Color MenuItemSelectedGradientBegin { get { return Theme.CardSelected; } }
        public override Color MenuItemSelectedGradientEnd { get { return Theme.CardSelected; } }
        public override Color SeparatorDark { get { return Theme.Border; } }
        public override Color SeparatorLight { get { return Theme.Card; } }
    }
}

// A checkbox drawn as a switch on the right of its row, with its text on the left (like GTK's switch rows).
class Toggle : CheckBox
{
    bool hover;

    public Toggle()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Cursor = Cursors.Hand;
    }

    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnCheckedChanged(EventArgs e) { Invalidate(); base.OnCheckedChanged(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent != null ? Parent.BackColor : Theme.Bg);
        int sw = Theme.D(40), sh = Theme.D(22);
        var track = new Rectangle(Width - sw - 2, (Height - sh) / 2, sw, sh);
        TextRenderer.DrawText(g, Text, Font, new Rectangle(0, 0, track.X - Theme.D(12), Height), Enabled ? ForeColor : Theme.Muted,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Color fill = Checked ? (hover ? Theme.AccentHover : Theme.Accent) : Theme.Mix(Theme.Card, Theme.Text, hover ? 0.24 : 0.18);
        if (!Enabled) fill = Theme.Mix(fill, Theme.Bg, 0.5);
        using (var path = Theme.Shape(track, sh / 2)) using (var b = new SolidBrush(fill)) g.FillPath(b, path);
        int d = sh - Theme.D(6);
        int kx = Checked ? track.Right - d - Theme.D(3) : track.X + Theme.D(3);
        using (var b = new SolidBrush(Checked ? Theme.OnAccent : Color.White)) g.FillEllipse(b, kx, track.Y + Theme.D(3), d, d);
        if (Focused && ShowFocusCues)
            using (var path = Theme.Shape(Rectangle.Inflate(track, 2, 2), sh / 2 + 2)) using (var pen = new Pen(Theme.Highlight)) g.DrawPath(pen, path);
    }
}

// The bar at the top of Virtual Machines and Settings, like GTK's header bar: a control on the left and one on
// the right, and a title (or the tabs) in the middle.
class HeaderBar : Panel
{
    Control left, middle, right;
    FlatBtn close;

    public HeaderBar()
    {
        Dock = DockStyle.Top;
        Height = Theme.D(54);
        BackColor = Theme.Bg;
        Clear();
    }

    public void Set(Control l, Control m, Control r)
    {
        Clear();
        left = l; middle = m; right = r;
        foreach (var c in new[] { l, m, r }) if (c != null) Controls.Add(c);
        Place();
    }

    // Leaves just the close button (the window has no title bar of its own; this bar is where it's moved from).
    public void Clear()
    {
        foreach (Control c in Controls.Cast<Control>().ToList()) { Controls.Remove(c); c.Dispose(); }
        left = middle = right = null;
        BackColor = Theme.Bg;
        close = new FlatBtn("\uE8BB") { Font = new Font("Segoe MDL2 Assets", 8f), Size = new Size(Theme.D(36), Theme.D(36)), TabStop = false };
        close.Click += delegate { var f = FindForm(); if (f != null) f.Close(); };
        new ToolTip().SetToolTip(close, "Close");
        Controls.Add(close);
        Place();
    }

    public static Label Title(string text)
    {
        return new PassLabel { Text = text, Font = Theme.Bold, ForeColor = Theme.Text, BackColor = Theme.Bg, AutoSize = true, UseMnemonic = false };
    }

    // The bar's empty space and its title belong to the window behind them, which treats them as its title bar:
    // drag to move, double-click to maximize or restore.
    const int WM_NCHITTEST = 0x84, HTTRANSPARENT = -1;
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_NCHITTEST && !DesignMode) { m.Result = (IntPtr)HTTRANSPARENT; return; }
        base.WndProc(ref m);
    }

    class PassLabel : Label
    {
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_NCHITTEST) { m.Result = (IntPtr)HTTRANSPARENT; return; }
            base.WndProc(ref m);
        }
    }

    protected override void OnResize(EventArgs e) { base.OnResize(e); Place(); }

    void Place()
    {
        int pad = Theme.D(12);
        int end = Width - pad;
        if (close != null) { close.Location = new Point(end - close.Width, (Height - close.Height) / 2); end = close.Left - Theme.D(8); }
        if (left != null) left.Location = new Point(pad, (Height - left.Height) / 2);
        if (right != null) right.Location = new Point(end - right.Width, (Height - right.Height) / 2);
        if (middle != null) middle.Location = new Point((Width - middle.Width) / 2, (Height - middle.Height) / 2);
    }
}

class FlatBtn : Button
{
    bool hover, down;

    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e)
    {
        hover = down = false; Invalidate(); base.OnMouseLeave(e);
        var row = Parent as OptionRow;
        if (row != null && row.Join != 0) row.RefreshHover();
    }
    protected override void OnMouseDown(MouseEventArgs e) { down = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { down = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

    // Drawn by hand so it can be shaded and rounded like the cards: the main action is a purple
    // gradient, the others a shaded surface. A button whose BackColor is set to Accent counts as main.
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Color behind = Parent != null ? Parent.BackColor : Theme.Bg;
        var onRow = Parent as OptionRow;
        if (onRow != null && onRow.Join != 0) behind = onRow.JoinFill;
        bool primary = BackColor.ToArgb() == Theme.Accent.ToArgb();
        Color top, bottom, border, fore = ForeColor;
        if (!Enabled) { top = bottom = Theme.Card; border = Theme.Border; fore = Theme.Muted; }
        else if (primary)
        {
            top = down ? Theme.Accent : hover ? Theme.AccentLight : Theme.AccentHover;
            bottom = down ? Theme.AccentDark : hover ? Theme.AccentMid : Theme.Accent;
            border = bottom;
        }
        else
        {
            top = hover || down ? Theme.HoverTop : Theme.CardTop;
            bottom = hover || down ? Theme.HoverBottom : Theme.CardBottom;
            border = Focused && ShowFocusCues ? Theme.Highlight : FlatAppearance.BorderColor;
        }
        Theme.Surface(g, ClientRectangle, behind, top, bottom, border, false);
        int icon = Theme.D(18);
        string name = Lucide.ForGlyph(Text);
        if (name != null)   // an icon button: the Lucide icon instead of the glyph
        {
            Lucide.Draw(g, name, new Rectangle((Width - icon) / 2, (Height - icon) / 2, icon, icon), fore);
            return;
        }
        if (Text.EndsWith("▾") && Lucide.Has("chevron-down"))   // a menu button: its text, then a chevron
        {
            string label = Theme.TitleCase(Text.TrimEnd('▾', ' '));
            int small = Theme.D(14), gap = Theme.D(6);
            int tw = TextRenderer.MeasureText(g, label, Font, Size.Empty, TextFormatFlags.NoPadding).Width;
            int x = (Width - tw - gap - small) / 2;
            TextRenderer.DrawText(g, label, Font, new Rectangle(x, 0, tw + 2, Height), fore, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
            Lucide.Draw(g, "chevron-down", new Rectangle(x + tw + gap, (Height - small) / 2, small, small), fore, 2.5f);
            return;
        }
        TextRenderer.DrawText(g, Theme.TitleCase(Text), Font, ClientRectangle, fore,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
    }

    public FlatBtn(string text, bool primary = false, bool danger = false)
    {
        // ResizeRedraw: a button that's widened after it first draws repaints all of itself.
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Text = text;
        FlatStyle = FlatStyle.Flat;
        UseVisualStyleBackColor = false;
        Font = Theme.Bold;
        Cursor = Cursors.Hand;
        FlatAppearance.BorderSize = 1;
        if (primary)
        {
            BackColor = Theme.Accent;
            ForeColor = Theme.OnAccent;
            FlatAppearance.BorderColor = Theme.Accent;
            FlatAppearance.MouseOverBackColor = Theme.AccentHover;
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
    public string Title = "", Sub = "", Status = "";
    public int DesignHeight;   // 0: the list's RowHeight
    public Color StatusColor = Theme.Muted;
    // A system's stats (label, value), e.g. ("CPU", "16 cores"), shown as columns on the right. The labels are
    // column headers above the list (Virtual Machines), so each row shows just the values, with the status in
    // its own column before them.
    public Tuple<string, string>[] Stats;
    // A smaller line under each stat (a running system's live value goes above what it was given: "of 16 cores").
    public string[] StatSubs;
    public const int StatColW = 86, StatusColW = 76, Pad = 18;
    bool selected, hover;

    public OptionRow()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.Selectable, false);   // clicking a row leaves the focus where it was (e.g. in search)
        Cursor = Cursors.Hand;
    }

    public bool Selected { get { return selected; } set { selected = value; Invalidate(); } }
    // Drawn without a box (just the list's background) unless pointed at or selected, like a GTK list.
    public bool Flat;
    // Pixels kept clear on the right for the row's own buttons (Virtual Machines' Start and Settings).
    public int Reserve;
    // Part of one card with the rows around it (like GTK's boxed list): 1 the first row (its top corners are
    // round), 2 the last (bottom corners, no line under it), 3 the only one; 0 a row of its own.
    // Joined rows show no selection.
    public int Join;
    // A joined row's colour, which the buttons on it paint their corners with (so none show the page behind).
    public Color JoinFill { get { return hover ? Theme.Mix(Theme.Card, Theme.Text, 0.04) : Theme.Card; } }
    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(Join != 0); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { linkHover = false; RefreshHover(); base.OnMouseLeave(e); }
    // Pointing at one of the row's own buttons still counts as pointing at the row.
    public void RefreshHover()
    {
        hover = Join != 0 && ClientRectangle.Contains(PointToClient(Cursor.Position));
        Invalidate(Join != 0);
    }

    // A web page for the row (a distribution's home page), shown as "Website ↗" on two-line rows; clicking it
    // opens the page in the browser, and the rest of the row still selects it.
    public string Link;
    public string LinkText = "Website ↗";
    static Font linkHoverFont;
    static Font LinkHoverFont { get { return linkHoverFont ?? (linkHoverFont = new Font(Theme.Small, FontStyle.Underline)); } }
    Rectangle linkRect;
    bool linkHover;

    protected override void OnMouseMove(MouseEventArgs e)
    {
        bool over = !linkRect.IsEmpty && linkRect.Contains(e.Location);
        if (over != linkHover) { linkHover = over; Invalidate(); }
        base.OnMouseMove(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Left && !linkRect.IsEmpty && linkRect.Contains(e.Location) && Link.StartsWith("https://", StringComparison.Ordinal))
            try { Process.Start(new ProcessStartInfo(Link) { UseShellExecute = true }); } catch { }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var behind = Parent != null ? Parent.BackColor : Theme.Bg;
        if (Join != 0)
        {
            g.Clear(behind);
            var mode = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            bool first = (Join & 1) != 0, last = (Join & 2) != 0;
            var r = new Rectangle(0, 0, Width - 1, Height - (last ? 1 : 0));
            using (var path = Theme.Shape(r, Theme.D(Theme.Radius + 4), first, last))
            {
                using (var b = new SolidBrush(JoinFill)) g.FillPath(b, path);
                using (var pen = new Pen(Theme.Mix(Theme.Border, Theme.Card, 0.3))) g.DrawPath(pen, path);
            }
            g.SmoothingMode = mode;
            if (!last) using (var pen = new Pen(Theme.Mix(Theme.Border, Theme.Card, 0.3))) g.DrawLine(pen, 0, Height - 1, Width, Height - 1);
        }
        else if (Flat && !selected && !hover) g.Clear(behind);   // a list row: no box until it's pointed at or chosen
        else
            Theme.Surface(g, ClientRectangle, behind,
                selected ? Theme.SelectedTop : hover ? Theme.HoverTop : Theme.CardTop,
                selected ? Theme.SelectedBottom : hover ? Theme.HoverBottom : Theme.CardBottom,
                selected ? Theme.Accent : Flat ? Theme.HoverBottom : Theme.Border, selected);
        int pad = Theme.D(18), half = Height / 2;
        var flags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
        int textW = Width - pad * 2;
        int right = Width - pad - Reserve;   // the status and any stat columns are laid out leftwards from here
        bool statusDone = false;
        if (Stats != null && Height < Theme.D(64))
        {
            int colW = Theme.D(StatColW);
            for (int i = Stats.Length - 1; i >= 0; i--)
            {
                string sub = StatSubs != null && i < StatSubs.Length ? StatSubs[i] : null;
                var cellFlags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis;
                if (string.IsNullOrEmpty(sub))
                    TextRenderer.DrawText(g, Stats[i].Item2, Theme.Bold, new Rectangle(right - colW, 0, colW, Height), Theme.Text, cellFlags);
                else
                {
                    // Lined up with the name and the line under it.
                    TextRenderer.DrawText(g, Stats[i].Item2, Theme.Bold, new Rectangle(right - colW, Theme.D(3), colW, half - Theme.D(3)), Theme.Text, cellFlags);
                    TextRenderer.DrawText(g, sub, Theme.Small, new Rectangle(right - colW, half, colW, half - Theme.D(4)), Theme.Muted, cellFlags);
                }
                right -= colW;
            }
            using (var p = new Pen(Theme.Border)) g.DrawLine(p, right - Theme.D(10), Theme.D(12), right - Theme.D(10), Height - Theme.D(12));
            right -= Theme.D(10);   // the status starts one column-width (86) before CPU, like the stats
            // The status column (empty for a system that's off). A longer status ("Running, window closed")
            // widens it to the left rather than being cut off.
            int sw = Math.Max(Theme.D(StatusColW), TextRenderer.MeasureText(g, Status, Theme.Small, Size.Empty, TextFormatFlags.NoPrefix).Width + Theme.D(4));
            if (Status.Length > 0)
                TextRenderer.DrawText(g, Status, Theme.Small, new Rectangle(right - sw, 0, sw, Height), StatusColor,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            right -= sw + Theme.D(10);
            textW = right - pad;
            statusDone = true;
        }
        if (Status.Length > 0 && !statusDone)
        {
            // The status sits on the right, centred on the row; the text to its left is shortened to fit.
            int statusW = TextRenderer.MeasureText(g, Status, Theme.Small, Size.Empty, TextFormatFlags.NoPrefix).Width;
            TextRenderer.DrawText(g, Status, Theme.Small, new Rectangle(right - statusW, 0, statusW, Height), StatusColor,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            textW -= statusW + Theme.D(16);
        }
        if (Height < Theme.D(64))
        {
            // A little more space between the title and the line under it.
            TextRenderer.DrawText(g, Title, Theme.RowTitle, new Rectangle(pad, Theme.D(3), textW, half - Theme.D(3)), Theme.Text, flags);
            TextRenderer.DrawText(g, Sub, Theme.Small, new Rectangle(pad, half + Theme.D(3), textW, half - Theme.D(7)), Theme.Muted, flags);
            return;
        }
        // Tall rows: the title with the description wrapped below it, centred in the row together, so a
        // one-line description has the same space above and below as a two-line one.
        var wrap = TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
        int w = Width - pad * 2, titleH = Theme.D(20), gap = Theme.D(6);
        // The distribution's home page in its own column on the right, centred on the row, past a divider
        // line like the one before the stats on Virtual Machines; the title and description stop short of it.
        linkRect = Rectangle.Empty;
        if (!string.IsNullOrEmpty(Link))
        {
            var size = TextRenderer.MeasureText(g, LinkText, Theme.Small, Size.Empty, TextFormatFlags.NoPrefix);   // the same width hovered
            int linkX = Width - pad - size.Width, divider = linkX - Theme.D(18);
            linkRect = new Rectangle(linkX, (Height - size.Height) / 2, size.Width, size.Height);
            using (var p = new Pen(Theme.Border)) g.DrawLine(p, divider, Theme.D(12), divider, Height - Theme.D(12));
            TextRenderer.DrawText(g, LinkText, linkHover ? LinkHoverFont : Theme.Small, linkRect, linkHover ? Theme.Text : Theme.Highlight, flags);
            w = divider - Theme.D(18) - pad;
        }
        int subH = Math.Min(Height - titleH - gap - Theme.D(8), TextRenderer.MeasureText(g, Sub, Theme.Small, new Size(w, int.MaxValue), wrap).Height);
        int top = Math.Max(Theme.D(4), (Height - titleH - gap - subH) / 2);
        TextRenderer.DrawText(g, Title, Theme.RowTitle, new Rectangle(pad, top, w, titleH), Theme.Text, flags);
        TextRenderer.DrawText(g, Sub, Theme.Small, new Rectangle(pad, top + titleH + gap, w, subH), Theme.Muted, wrap);
    }
}

// The scroll bar drawn over a scrolling area's contents, like Windows 11's and Firefox's overlay scroll bars:
// a thin rounded thumb at the right edge that leaves the contents their full width. With the pointer over
// that edge it widens into a bar with a track and arrows, and narrows again a moment after the pointer leaves.
static class OverlayBar
{
    public static int Thin { get { return Theme.D(6); } }        // the thumb, resting
    public static int Wide { get { return Theme.D(14); } }       // the bar under the pointer
    public static int WideThumb { get { return Theme.D(8); } }
    public static int Margin { get { return Theme.D(3); } }
    public static int Arrow { get { return Theme.D(14); } }

    // A shade between the background and the text colour (0 is the background), so it suits every theme.
    public static Color Mix(double t)
    {
        Func<int, int, int> m = (a, b) => (int)Math.Round(a + (b - a) * t);
        return Color.FromArgb(m(Theme.Bg.R, Theme.Text.R), m(Theme.Bg.G, Theme.Text.G), m(Theme.Bg.B, Theme.Text.B));
    }
    public static Color Track { get { return Mix(0.09); } }
    public static Color ThumbColor(bool wide, bool hot) { return Mix(hot ? 0.62 : wide ? 0.45 : 0.3); }
    public static Color ArrowColor(bool hot) { return Mix(hot ? 0.9 : 0.55); }

    public static GraphicsPath Rounded(Rectangle r)
    {
        var p = new GraphicsPath();
        int d = Math.Max(2, Math.Min(r.Width, r.Height));
        if (r.Height <= d) { p.AddEllipse(r); return p; }
        p.AddArc(r.X, r.Y, d, d, 180, 180);
        p.AddArc(r.X, r.Bottom - d, d, d, 0, 180);
        p.CloseFigure();
        return p;
    }
}

// The overlay scroll bar's shape, hover and mouse handling, shared by ScrollPanel and NoteBox. Points are in
// the host's client coordinates; the host says how much there is, how much shows and where it's scrolled to
// (pixels for a panel, lines for a text box), and scrolls when told to.
class OverlayScroll : IDisposable
{
    public Func<Size> Client;
    public Func<int> Content, View, Pos;
    public Action<int> Set;
    public Func<Point, Point> ToClient;   // from screen coordinates
    public Action Changed;                // the bar's look or place changed
    public int Right;                     // the gap between the bar and the host's right edge
    public int Line = 1;                  // how far an arrow click scrolls
    public bool Wide { get; private set; }
    public bool Dragging { get; private set; }
    // The part under the pointer, and the part being pressed: 1 up arrow, 2 down arrow, 3 track above the
    // thumb, 4 track below it, 5 the thumb; 0 none.
    int over, pressed;
    int startY, startPos;
    Point pointer;   // where the pointer is while a button is held on the bar
    readonly Timer collapse = new Timer { Interval = 900 }, repeat = new Timer();

    public OverlayScroll()
    {
        collapse.Tick += delegate
        {
            if (Dragging || pressed != 0 || Strip.Contains(ToClient(Cursor.Position))) return;
            collapse.Stop();
            Wide = false; over = 0;
            Changed();
        };
        repeat.Tick += delegate { repeat.Interval = 50; Step(); };
    }

    public void Dispose() { collapse.Dispose(); repeat.Dispose(); }

    public bool Scrollable { get { return View() > 0 && Content() > View(); } }

    // The strip along the right edge that the bar widens into.
    public Rectangle Strip
    {
        get { var c = Client(); return new Rectangle(c.Width - OverlayBar.Wide - Right, 0, OverlayBar.Wide, c.Height); }
    }

    Rectangle Track
    {
        get
        {
            var c = Client();
            if (Wide) return new Rectangle(Strip.X, OverlayBar.Arrow, OverlayBar.Wide, Math.Max(0, c.Height - OverlayBar.Arrow * 2));
            return new Rectangle(c.Width - OverlayBar.Thin - Right, OverlayBar.Margin, OverlayBar.Thin, Math.Max(0, c.Height - OverlayBar.Margin * 2));
        }
    }

    public Rectangle Thumb
    {
        get
        {
            int content = Content(), view = Math.Max(0, View());
            var t = Track;
            if (view <= 0 || content <= view || t.Height <= 0) return Rectangle.Empty;
            int h = Math.Min(t.Height, Math.Max(Theme.D(28), (int)((long)t.Height * view / content)));
            int y = t.Y + (int)((long)(t.Height - h) * Math.Max(0, Math.Min(Pos(), content - view)) / (content - view));
            int w = Wide ? OverlayBar.WideThumb : OverlayBar.Thin;
            return new Rectangle(t.X + (t.Width - w) / 2, y, w, h);
        }
    }

    // What the bar covers now: just the thumb, or the whole strip while it's wide.
    public Rectangle Bounds { get { return !Scrollable ? Rectangle.Empty : Wide ? Strip : Thumb; } }

    Rectangle UpArrow { get { var s = Strip; return new Rectangle(s.X, 0, s.Width, OverlayBar.Arrow); } }
    Rectangle DownArrow { get { var s = Strip; return new Rectangle(s.X, s.Bottom - OverlayBar.Arrow, s.Width, OverlayBar.Arrow); } }

    int PartAt(Point p)
    {
        if (!Scrollable) return 0;
        var th = Thumb;
        if (Wide ? new Rectangle(Strip.X, th.Y, Strip.Width, th.Height).Contains(p) : th.Contains(p)) return 5;
        if (!Wide || !Strip.Contains(p)) return 0;
        if (UpArrow.Contains(p)) return 1;
        if (DownArrow.Contains(p)) return 2;
        return p.Y < th.Y ? 3 : 4;
    }

    // Whether a press at `p` would be taken by the bar (for the pointer's shape).
    public bool Covers(Point p) { return Dragging || (Scrollable && (Wide ? Strip : Thumb).Contains(p)); }

    // The pointer moved to `p`: widens the bar when it's over the right edge, and tracks which part it's over.
    public void Hover(Point p)
    {
        if (Dragging || pressed != 0) return;
        bool changed = false;
        if (Scrollable && Strip.Contains(p))
        {
            if (!Wide) { Wide = true; changed = true; }
            collapse.Stop(); collapse.Start();
        }
        int at = Wide ? PartAt(p) : 0;
        if (at != over) { over = at; changed = true; }
        if (changed) Changed();
    }

    public void Leave()
    {
        if (Dragging || pressed != 0 || over == 0) return;
        over = 0;
        Changed();
    }

    // A press at `p`: true when it's on the bar (and so not for the contents under it).
    public bool Down(Point p)
    {
        int at = PartAt(p);
        if (at == 0) return false;
        pressed = at;
        pointer = p;
        if (at == 5) { Dragging = true; startY = p.Y; startPos = Pos(); Changed(); return true; }
        Step();
        repeat.Interval = 400;   // held down: repeats after a moment, like Windows' scroll bars
        repeat.Start();
        return true;
    }

    void Step()
    {
        int page = Math.Max(Line, View() - Line);
        if (pressed == 1) Set(Pos() - Line);
        else if (pressed == 2) Set(Pos() + Line);
        else if (pressed == 3 || pressed == 4)
        {
            // Pages toward the pointer, stopping once the thumb reaches it.
            var p = pointer;
            var th = Thumb;
            if (pressed == 3 && p.Y < th.Top) Set(Pos() - page);
            else if (pressed == 4 && p.Y >= th.Bottom) Set(Pos() + page);
        }
        Changed();
    }

    // The pointer moved to `p` while a button is held on the bar: true when it is (the move is the bar's).
    public bool Drag(Point p)
    {
        if (pressed == 0) return false;
        pointer = p;
        if (!Dragging) return true;   // held on an arrow or the track: repeats from where the pointer is
        int travel = Track.Height - Thumb.Height;
        if (travel > 0) Set(startPos + (int)((long)(p.Y - startY) * (Content() - View()) / travel));
        Changed();
        return true;
    }

    // The button came up: true when the bar had the press.
    public bool Up()
    {
        if (pressed == 0) return false;
        pressed = 0; Dragging = false;
        repeat.Stop();
        collapse.Stop(); collapse.Start();   // narrows once the pointer has left
        Changed();
        return true;
    }

    public void Paint(Graphics g)
    {
        if (!Scrollable) return;
        var mode = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        if (Wide)
        {
            using (var b = new SolidBrush(OverlayBar.Track)) g.FillRectangle(b, Strip);
            ArrowAt(g, UpArrow, true, over == 1 || pressed == 1);
            ArrowAt(g, DownArrow, false, over == 2 || pressed == 2);
        }
        using (var path = OverlayBar.Rounded(Thumb))
        using (var b = new SolidBrush(OverlayBar.ThumbColor(Wide, over == 5 || Dragging))) g.FillPath(b, path);
        g.SmoothingMode = mode;
    }

    static void ArrowAt(Graphics g, Rectangle r, bool up, bool hot)
    {
        float s = Theme.D(4), cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f;
        var pts = up
            ? new[] { new PointF(cx - s, cy + s / 2), new PointF(cx + s, cy + s / 2), new PointF(cx, cy - s / 2) }
            : new[] { new PointF(cx - s, cy - s / 2), new PointF(cx + s, cy - s / 2), new PointF(cx, cy + s / 2) };
        using (var b = new SolidBrush(OverlayBar.ArrowColor(hot))) g.FillPolygon(b, pts);
    }
}

// The bar of a ScrollPanel: a small window of its own over the contents (the thumb, or the whole strip while
// it's wide), so it gets the mouse before the contents under it.
class OverlayStrip : Control
{
    readonly OverlayScroll bar;

    public OverlayStrip(OverlayScroll bar)
    {
        this.bar = bar;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.Selectable, false);
        Visible = false;
    }

    Point InHost(Point p) { return new Point(p.X + Left, p.Y + Top); }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(Parent != null ? Parent.BackColor : Theme.Bg);
        e.Graphics.TranslateTransform(-Left, -Top);
        bar.Paint(e.Graphics);
    }

    protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); if (e.Button == MouseButtons.Left) bar.Down(InHost(e.Location)); }
    protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); if (!bar.Drag(InHost(e.Location))) bar.Hover(InHost(e.Location)); }
    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); bar.Up(); }
    protected override void OnMouseCaptureChanged(EventArgs e) { base.OnMouseCaptureChanged(e); if (!Capture) bar.Up(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); bar.Leave(); }
}

// Tells each ScrollPanel where the pointer is, wherever it is over the window (over a row, a label...), so the
// bar widens when the pointer reaches the panel's right edge.
class OverlayHover : IMessageFilter
{
    static readonly List<WeakReference> panels = new List<WeakReference>();
    static bool watching;

    public static void Watch(ScrollPanel p)
    {
        if (!watching) { watching = true; Application.AddMessageFilter(new OverlayHover()); }
        panels.Add(new WeakReference(p));
    }

    public bool PreFilterMessage(ref Message m)
    {
        if (m.Msg != 0x200) return false;   // WM_MOUSEMOVE
        var under = Control.FromChildHandle(m.HWnd);
        var form = under != null ? under.FindForm() : null;
        var at = Cursor.Position;
        for (int i = panels.Count - 1; i >= 0; i--)
        {
            var p = panels[i].Target as ScrollPanel;
            if (p == null || p.IsDisposed) { panels.RemoveAt(i); continue; }
            if (form != null && p.IsHandleCreated && p.Visible && p.FindForm() == form) p.Pointer(at);
        }
        return false;
    }
}

// A panel that scrolls its contents itself (mouse wheel, or the overlay bar) instead of with Windows' scroll
// bar, which takes its width from the contents. Child controls are placed as if unscrolled less ScrollY, the
// way AutoScrollPosition worked.
class ScrollPanel : Panel
{
    readonly OverlayScroll bar;
    readonly OverlayStrip strip;
    int scrollY;
    bool shifting, updating;
    Size regionFor = Size.Empty;
    public bool Scrolls = true;
    public int ScrollY { get { return scrollY; } }

    public ScrollPanel()
    {
        bar = new OverlayScroll
        {
            Client = () => ClientSize,
            Content = () => Scrolls ? ContentHeight : 0,
            View = () => ClientSize.Height,
            Pos = () => scrollY,
            Set = ScrollTo,
            ToClient = PointToClient,
            Changed = UpdateBar,
            Line = Theme.D(40),
        };
        strip = new OverlayStrip(bar);
        Controls.Add(strip);
        OverlayHover.Watch(this);
    }

    protected override void Dispose(bool disposing) { if (disposing) bar.Dispose(); base.Dispose(disposing); }

    // The pointer is at `screen`, somewhere over this panel's window.
    public void Pointer(Point screen)
    {
        var p = PointToClient(screen);
        if (ClientRectangle.Contains(p) || bar.Wide) bar.Hover(p);
    }

    // The contents' full height: down to the lowest control showing, as if not scrolled.
    public virtual int ContentHeight
    {
        get
        {
            int bottom = 0;
            foreach (Control c in Controls) if (c != strip && c.Visible) bottom = Math.Max(bottom, c.Bottom);
            return bottom + scrollY;
        }
    }

    public int MaxScroll { get { return Scrolls ? Math.Max(0, ContentHeight - ClientSize.Height) : 0; } }

    public void ScrollTo(int y)
    {
        y = Math.Max(0, Math.Min(y, MaxScroll));
        if (y != scrollY)
        {
            int d = y - scrollY;
            scrollY = y;
            shifting = true;
            try { Shift(d); } finally { shifting = false; }
        }
        UpdateBar();
    }

    // Back to the top without moving anything (the contents are about to be replaced).
    protected void ResetScroll() { scrollY = 0; }

    [DllImport("user32.dll")] static extern IntPtr BeginDeferWindowPos(int n);
    [DllImport("user32.dll")] static extern IntPtr DeferWindowPos(IntPtr info, IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern bool EndDeferWindowPos(IntPtr info);

    // Moves several child controls at once, in one step (so they never show half moved).
    protected static void MoveAll(List<KeyValuePair<Control, Rectangle>> moves)
    {
        var windows = new List<KeyValuePair<Control, Rectangle>>();
        foreach (var m in moves)
            if (m.Key.IsHandleCreated) { if (m.Key.Bounds != m.Value) windows.Add(m); }
            else m.Key.Bounds = m.Value;
        if (windows.Count == 0) return;
        IntPtr info = BeginDeferWindowPos(windows.Count);
        foreach (var m in windows)
        {
            if (info == IntPtr.Zero) break;
            info = DeferWindowPos(info, m.Key.Handle, IntPtr.Zero, m.Value.X, m.Value.Y, m.Value.Width, m.Value.Height, 0x0004 | 0x0010 | 0x0200);   // SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER
        }
        if (info != IntPtr.Zero) EndDeferWindowPos(info);
        else foreach (var m in windows) m.Key.Bounds = m.Value;
    }

    // Moves the contents up by `d` pixels (down when negative).
    protected virtual void Shift(int d)
    {
        var moves = new List<KeyValuePair<Control, Rectangle>>();
        foreach (Control c in Controls)
            if (c != strip) moves.Add(new KeyValuePair<Control, Rectangle>(c, new Rectangle(c.Left, c.Top - d, c.Width, c.Height)));
        MoveAll(moves);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        int lines = SystemInformation.MouseWheelScrollLines;
        int step = (lines > 0 ? lines : 3) * Theme.D(20);
        // Only taken when this panel can move that way; otherwise the wheel goes on to the panel around it.
        if (MaxScroll > 0 && (e.Delta < 0 ? scrollY < MaxScroll : scrollY > 0))
        {
            ScrollTo(scrollY - e.Delta * step / 120);
            var h = e as HandledMouseEventArgs;
            if (h != null) h.Handled = true;
        }
        base.OnMouseWheel(e);
    }

    protected override void OnLayout(LayoutEventArgs e) { base.OnLayout(e); Clamp(); }
    protected override void OnResize(EventArgs e) { base.OnResize(e); Clamp(); }
    protected override void OnVisibleChanged(EventArgs e) { base.OnVisibleChanged(e); Clamp(); }

    // After the contents or the size change: keeps the scroll position within the contents and the bar in step.
    void Clamp()
    {
        if (shifting || updating || !Visible) return;
        if (scrollY > MaxScroll) ScrollTo(MaxScroll); else UpdateBar();
    }

    public void UpdateBar()
    {
        if (updating) return;
        updating = true;
        try
        {
            var r = bar.Bounds;
            if (r.IsEmpty) { strip.Visible = false; return; }
            // Resting, the window is the thumb's rounded shape; wide, it's the whole strip.
            var shape = bar.Wide ? Size.Empty : r.Size;
            if (shape != regionFor)
            {
                regionFor = shape;
                if (bar.Wide) strip.Region = null;
                else using (var p = OverlayBar.Rounded(new Rectangle(Point.Empty, r.Size))) strip.Region = new Region(p);
            }
            if (strip.Bounds != r) strip.Bounds = r;
            if (!strip.Visible) strip.Visible = true;
            strip.BringToFront();
            strip.Invalidate();
        }
        finally { updating = false; }
    }

    // Scrolls just enough to show the part from `top` to `bottom` (content coordinates, as if unscrolled).
    public void ShowRange(int top, int bottom)
    {
        if (top < scrollY) ScrollTo(top);
        else if (bottom > scrollY + ClientSize.Height) ScrollTo(bottom - ClientSize.Height);
    }
}

// A list of rows. Only the rows in view are windows (the rest are just data), so a long list, such as
// every match while typing in search, fills and scrolls quickly.
class OptionList : ScrollPanel
{
    readonly List<OptionRow> rows = new List<OptionRow>();
    int selectedIndex = -1, updates;
    public event EventHandler SelectionChanged, Activated;
    public int RowHeight = 54;          // design units; 72 fits a two-line description
    // Pixels kept clear right of the rows: a list that reaches into its box's padding, so the scroll bar sits
    // at the box's edge rather than between the rows and the box's border.
    public int RightInset;
    // Rows without a box of their own (see OptionRow.Flat), for a long list such as search results.
    public bool FlatRows;
    // Design units between rows; 0 for rows joined into one card (see OptionRow.Join).
    public int Gap = 6;

    public OptionList()
    {
        BackColor = Theme.Bg;
    }

    // `height` (design units) overrides RowHeight for this row, e.g. a one-line entry in a list of two-line ones.
    public OptionRow AddItem(string title, string sub, object tag, string status = null, Color? statusColor = null, int height = 0)
    {
        var row = new OptionRow { Title = title, Sub = sub, Tag = tag, Status = status ?? "", StatusColor = statusColor ?? Theme.Muted, DesignHeight = height, Flat = FlatRows };
        int index = rows.Count;
        row.MouseDown += delegate { SelectedIndex = index; };
        row.DoubleClick += delegate { if (Activated != null) Activated(this, EventArgs.Empty); };
        rows.Add(row);
        if (updates == 0) LayoutRows();
        return row;
    }

    // Around adding many rows: they're laid out once at the end.
    public void BeginUpdate() { updates++; }
    public void EndUpdate() { if (--updates == 0) LayoutRows(); }

    public void Clear()
    {
        foreach (var r in rows) { if (r.Parent == this) Controls.Remove(r); r.Dispose(); }
        rows.Clear();
        selectedIndex = -1;
        ResetScroll();
        if (updates == 0) LayoutRows();
    }

    int H(OptionRow r) { return Theme.D(r.DesignHeight > 0 ? r.DesignHeight : RowHeight); }

    public override int ContentHeight
    {
        get
        {
            int gap = Theme.D(Gap), total = 0;
            foreach (var r in rows) total += H(r) + gap;
            return rows.Count > 0 ? total - gap : 0;
        }
    }

    protected override void OnResize(EventArgs e) { LayoutRows(); base.OnResize(e); }
    protected override void Shift(int d) { LayoutRows(); }

    void LayoutRows()
    {
        if (updates > 0) return;
        int gap = Theme.D(Gap), w = ClientSize.Width - RightInset, view = ClientSize.Height, y = -ScrollY;
        var moves = new List<KeyValuePair<Control, Rectangle>>();
        SuspendLayout();
        foreach (var r in rows)
        {
            int h = H(r);
            var place = new Rectangle(0, y, w, h);
            if (y + h > 0 && y < view)
            {
                if (r.Parent == this) moves.Add(new KeyValuePair<Control, Rectangle>(r, place));
                else { r.Bounds = place; Controls.Add(r); }
            }
            else if (r.Parent == this) Controls.Remove(r);
            y += h + gap;
        }
        MoveAll(moves);   // the rows already showing move together
        ResumeLayout(false);
        UpdateBar();
    }

    public int SelectedIndex
    {
        get { return selectedIndex; }
        set
        {
            selectedIndex = value;
            for (int i = 0; i < rows.Count; i++) rows[i].Selected = i == value;
            if (value >= 0 && value < rows.Count)
            {
                int top = 0, gap = Theme.D(Gap);
                for (int i = 0; i < value; i++) top += H(rows[i]) + gap;
                ShowRange(top, top + H(rows[value]));
            }
            if (SelectionChanged != null) SelectionChanged(this, EventArgs.Empty);
        }
    }

    public object SelectedTag { get { return selectedIndex >= 0 ? rows[selectedIndex].Tag : null; } }
    public int Count { get { return rows.Count; } }
}

// A text box of several lines with the overlay scroll bar instead of Windows' one. The edit control still
// scrolls itself (keys, typing); the wheel and the bar, painted in its right margin, scroll it from here.
// The app's icons: Lucide (lucide.dev, ISC license), the same SVG files the Linux and Mac editions use, embedded
// in the exe as icon-<name>.svg and drawn here as lines in the text colour. Only what Lucide's outline icons use is
// read: path, line, polyline, polygon, rect, circle and ellipse, all stroked 2 units wide on a 24-unit grid.
static class Lucide
{
    static readonly Dictionary<string, GraphicsPath> cache = new Dictionary<string, GraphicsPath>();
    // Dots: Lucide writes them as lines a hundredth of a unit long ("M12 8h.01"), which GDI+ doesn't draw.
    static readonly Dictionary<string, List<PointF>> dots = new Dictionary<string, List<PointF>>();

    // The Segoe MDL2 glyphs (and text icons) buttons were made with, and the Lucide icon each now shows.
    static readonly Dictionary<string, string> forGlyph = new Dictionary<string, string> {
        { "\uE713", "settings" }, { "\uE700", "menu" }, { "\uE72B", "arrow-left" }, { "\uE8BB", "x" },
        { "\uE70F", "pencil" }, { ">_", "terminal" },
    };

    public static string ForGlyph(string text) { string n; return text != null && forGlyph.TryGetValue(text, out n) && Has(n) ? n : null; }

    public static bool Has(string name) { return Load(name) != null; }

    public static void Draw(Graphics g, string name, Rectangle box, Color color, float strokeUnits = 2f)
    {
        var shape = Load(name);
        if (shape == null) return;
        int size = Math.Min(box.Width, box.Height);
        float s = size / 24f;
        using (var p = (GraphicsPath)shape.Clone())
        using (var m = new Matrix())
        {
            m.Translate(box.X + (box.Width - size) / 2f, box.Y + (box.Height - size) / 2f);
            m.Scale(s, s);
            p.Transform(m);
            var mode = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float width = Math.Max(1f, strokeUnits * s);
            using (var pen = new Pen(color, width) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                g.DrawPath(pen, p);
            List<PointF> spots;
            if (dots.TryGetValue(name, out spots) && spots.Count > 0)
            {
                var pts = spots.ToArray();
                m.TransformPoints(pts);
                using (var b = new SolidBrush(color))
                    foreach (var pt in pts) g.FillEllipse(b, pt.X - width / 2, pt.Y - width / 2, width, width);
            }
            g.SmoothingMode = mode;
        }
    }

    static GraphicsPath Load(string name)
    {
        GraphicsPath p;
        if (cache.TryGetValue(name, out p)) return p;
        try
        {
            using (var st = typeof(Lucide).Assembly.GetManifestResourceStream("icon-" + name + ".svg"))
                if (st != null) using (var r = new StreamReader(st)) { var found = new List<PointF>(); p = Parse(r.ReadToEnd(), found); dots[name] = found; }
        }
        catch { p = null; }
        cache[name] = p;
        return p;
    }

    static float A(string tag, string attr, float otherwise = 0)
    {
        var m = Regex.Match(tag, "\\s" + attr + "=\"([^\"]*)\"");
        float v;
        return m.Success && float.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v) ? v : otherwise;
    }

    static GraphicsPath Parse(string svg, List<PointF> spots)
    {
        var p = new GraphicsPath();
        foreach (Match el in Regex.Matches(svg, "<(path|line|polyline|polygon|rect|circle|ellipse)\\b([^>]*)>"))
        {
            string kind = el.Groups[1].Value, tag = " " + el.Groups[2].Value;
            p.StartFigure();
            switch (kind)
            {
                case "path": PathData(p, Regex.Match(tag, "\\sd=\"([^\"]*)\"").Groups[1].Value, spots); break;
                case "line": p.AddLine(A(tag, "x1"), A(tag, "y1"), A(tag, "x2"), A(tag, "y2")); break;
                case "circle": { float r = A(tag, "r"); p.AddEllipse(A(tag, "cx") - r, A(tag, "cy") - r, 2 * r, 2 * r); break; }
                case "ellipse": { float rx = A(tag, "rx"), ry = A(tag, "ry"); p.AddEllipse(A(tag, "cx") - rx, A(tag, "cy") - ry, 2 * rx, 2 * ry); break; }
                case "rect":
                {
                    float x = A(tag, "x"), y = A(tag, "y"), w = A(tag, "width"), h = A(tag, "height");
                    float rx = A(tag, "rx", A(tag, "ry")), ry = A(tag, "ry", rx);
                    rx = Math.Min(rx, w / 2); ry = Math.Min(ry, h / 2);
                    if (rx <= 0 || ry <= 0) { p.AddRectangle(new RectangleF(x, y, w, h)); break; }
                    p.AddArc(x, y, 2 * rx, 2 * ry, 180, 90); p.AddArc(x + w - 2 * rx, y, 2 * rx, 2 * ry, 270, 90);
                    p.AddArc(x + w - 2 * rx, y + h - 2 * ry, 2 * rx, 2 * ry, 0, 90); p.AddArc(x, y + h - 2 * ry, 2 * rx, 2 * ry, 90, 90);
                    p.CloseFigure();
                    break;
                }
                default:   // polyline, polygon
                {
                    var n = Numbers(Regex.Match(tag, "\\spoints=\"([^\"]*)\"").Groups[1].Value);
                    var pts = new List<PointF>();
                    for (int i = 0; i + 1 < n.Count; i += 2) pts.Add(new PointF(n[i], n[i + 1]));
                    if (pts.Count > 1) p.AddLines(pts.ToArray());
                    if (kind == "polygon") p.CloseFigure();
                    break;
                }
            }
        }
        return p;
    }

    static List<float> Numbers(string s)
    {
        return Regex.Matches(s, "[-+]?(?:\\d+\\.?\\d*|\\.\\d+)(?:[eE][-+]?\\d+)?").Cast<Match>()
            .Select(m => float.Parse(m.Value, System.Globalization.CultureInfo.InvariantCulture)).ToList();
    }

    // SVG path data: M L H V C S Q T A Z, absolute and relative.
    static void PathData(GraphicsPath p, string d, List<PointF> spots)
    {
        int i = 0;
        char cmd = ' ';
        PointF cur = PointF.Empty, start = PointF.Empty, ctrl = PointF.Empty;
        char prev = ' ';
        Action skip = () => { while (i < d.Length && (char.IsWhiteSpace(d[i]) || d[i] == ',')) i++; };
        Func<float> num = () =>
        {
            skip();
            var m = Regex.Match(d.Substring(i), "^[-+]?(?:\\d+\\.?\\d*|\\.\\d+)(?:[eE][-+]?\\d+)?");
            if (!m.Success) throw new FormatException("path data");
            i += m.Length;
            return float.Parse(m.Value, System.Globalization.CultureInfo.InvariantCulture);
        };
        Func<bool> flag = () => { skip(); return d[i++] == '1'; };   // arc flags can run together ("01")
        while (true)
        {
            skip();
            if (i >= d.Length) break;
            if (char.IsLetter(d[i])) cmd = d[i++];
            else if (cmd == 'M') cmd = 'L'; else if (cmd == 'm') cmd = 'l';   // more pairs after a move are lines
            bool rel = char.IsLower(cmd);
            float ox = rel ? cur.X : 0, oy = rel ? cur.Y : 0;
            switch (char.ToUpper(cmd))
            {
                case 'M': cur = start = new PointF(ox + num(), oy + num()); p.StartFigure(); break;
                case 'L': { var to = new PointF(ox + num(), oy + num()); Line(p, cur, to, spots); cur = to; break; }
                case 'H': { var to = new PointF(ox + num(), cur.Y); Line(p, cur, to, spots); cur = to; break; }
                case 'V': { var to = new PointF(cur.X, oy + num()); Line(p, cur, to, spots); cur = to; break; }
                case 'C':
                {
                    var c1 = new PointF(ox + num(), oy + num()); var c2 = new PointF(ox + num(), oy + num()); var to = new PointF(ox + num(), oy + num());
                    p.AddBezier(cur, c1, c2, to); ctrl = c2; cur = to; break;
                }
                case 'S':
                {
                    var c1 = "CcSs".IndexOf(prev) >= 0 ? new PointF(2 * cur.X - ctrl.X, 2 * cur.Y - ctrl.Y) : cur;
                    var c2 = new PointF(ox + num(), oy + num()); var to = new PointF(ox + num(), oy + num());
                    p.AddBezier(cur, c1, c2, to); ctrl = c2; cur = to; break;
                }
                case 'Q':
                case 'T':
                {
                    PointF q = char.ToUpper(cmd) == 'Q' ? new PointF(ox + num(), oy + num())
                        : "QqTt".IndexOf(prev) >= 0 ? new PointF(2 * cur.X - ctrl.X, 2 * cur.Y - ctrl.Y) : cur;
                    var to = new PointF(ox + num(), oy + num());
                    p.AddBezier(cur, new PointF(cur.X + 2f / 3 * (q.X - cur.X), cur.Y + 2f / 3 * (q.Y - cur.Y)),
                        new PointF(to.X + 2f / 3 * (q.X - to.X), to.Y + 2f / 3 * (q.Y - to.Y)), to);
                    ctrl = q; cur = to; break;
                }
                case 'A':
                {
                    float rx = num(), ry = num(), rot = num(); bool large = flag(), sweep = flag();
                    var to = new PointF(ox + num(), oy + num());
                    Arc(p, cur, rx, ry, rot, large, sweep, to); cur = to; break;
                }
                case 'Z': p.CloseFigure(); cur = start; break;
                default: return;
            }
            prev = cmd;
        }
    }

    static void Line(GraphicsPath p, PointF a, PointF b, List<PointF> spots)
    {
        if (Math.Abs(a.X - b.X) + Math.Abs(a.Y - b.Y) < 0.1f) spots.Add(a);   // a dot
        else p.AddLine(a, b);
    }

    // An SVG arc as Bézier curves (the endpoint form converted to the centre form, then quarter turns at most).
    static void Arc(GraphicsPath p, PointF a, double rx, double ry, double rotation, bool large, bool sweep, PointF b)
    {
        if (rx == 0 || ry == 0 || (a.X == b.X && a.Y == b.Y)) { p.AddLine(a, b); return; }
        double phi = rotation * Math.PI / 180, cos = Math.Cos(phi), sin = Math.Sin(phi);
        double dx = (a.X - b.X) / 2.0, dy = (a.Y - b.Y) / 2.0;
        double x1 = cos * dx + sin * dy, y1 = -sin * dx + cos * dy;
        rx = Math.Abs(rx); ry = Math.Abs(ry);
        double lam = x1 * x1 / (rx * rx) + y1 * y1 / (ry * ry);
        if (lam > 1) { rx *= Math.Sqrt(lam); ry *= Math.Sqrt(lam); }
        double num = rx * rx * ry * ry - rx * rx * y1 * y1 - ry * ry * x1 * x1, den = rx * rx * y1 * y1 + ry * ry * x1 * x1;
        double co = Math.Sqrt(Math.Max(0, num / den)) * (large == sweep ? -1 : 1);
        double cxp = co * rx * y1 / ry, cyp = -co * ry * x1 / rx;
        double cx = cos * cxp - sin * cyp + (a.X + b.X) / 2.0, cy = sin * cxp + cos * cyp + (a.Y + b.Y) / 2.0;
        Func<double, double, double, double, double> ang = (ux, uy, vx, vy) => Math.Atan2(ux * vy - uy * vx, ux * vx + uy * vy);
        double t1 = ang(1, 0, (x1 - cxp) / rx, (y1 - cyp) / ry);
        double dt = ang((x1 - cxp) / rx, (y1 - cyp) / ry, (-x1 - cxp) / rx, (-y1 - cyp) / ry);
        if (!sweep && dt > 0) dt -= 2 * Math.PI; else if (sweep && dt < 0) dt += 2 * Math.PI;
        int n = Math.Max(1, (int)Math.Ceiling(Math.Abs(dt) / (Math.PI / 2) - 1e-9));
        double step = dt / n, k = 4.0 / 3 * Math.Tan(step / 4);
        Func<double, PointF> at = t => new PointF((float)(cx + rx * Math.Cos(t) * cos - ry * Math.Sin(t) * sin), (float)(cy + rx * Math.Cos(t) * sin + ry * Math.Sin(t) * cos));
        Func<double, PointF> tangent = t => new PointF((float)(-rx * Math.Sin(t) * cos - ry * Math.Cos(t) * sin), (float)(-rx * Math.Sin(t) * sin + ry * Math.Cos(t) * cos));
        for (int s = 0; s < n; s++)
        {
            double ta = t1 + s * step, tb = ta + step;
            PointF pa = at(ta), pb = at(tb), da = tangent(ta), db = tangent(tb);
            p.AddBezier(pa, new PointF((float)(pa.X + k * da.X), (float)(pa.Y + k * da.Y)), new PointF((float)(pb.X - k * db.X), (float)(pb.Y - k * db.Y)), pb);
        }
    }
}

// Rounds a text box like the other fields (and the Linux edition's entries): Windows' square border is replaced by
// a frame drawn here, a few pixels wide, with rounded corners, so the text sits inside it. The accent colour marks
// the box with the keyboard focus.
class RoundBox : NativeWindow
{
    [DllImport("user32.dll")] static extern IntPtr GetWindowDC(IntPtr h);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    const int WM_NCCALCSIZE = 0x83, WM_NCHITTEST = 0x84, WM_NCPAINT = 0x85, HTNOWHERE = 0, HTCLIENT = 1, HTBORDER = 18;
    readonly TextBox box;

    public static void Attach(TextBox t)
    {
        int h = t.Height;
        t.AutoSize = false;
        t.BorderStyle = BorderStyle.None;
        t.Height = h;
        new RoundBox(t);
    }

    RoundBox(TextBox t)
    {
        box = t;
        // These handlers also keep this object alive as long as the box.
        t.HandleCreated += delegate { Hook(); };
        t.HandleDestroyed += delegate { ReleaseHandle(); };
        t.Resize += delegate { Frame(); };
        t.GotFocus += delegate { Frame(); };
        t.LostFocus += delegate { Frame(); };
        t.EnabledChanged += delegate { Frame(); };
        t.BackColorChanged += delegate { Frame(); };
        if (t.IsHandleCreated) Hook();
    }

    void Hook() { if (Handle != box.Handle) { if (Handle != IntPtr.Zero) ReleaseHandle(); AssignHandle(box.Handle); } Frame(); }

    // Has Windows work out the frame again (WM_NCCALCSIZE) and draw it (WM_NCPAINT).
    void Frame() { if (Handle != IntPtr.Zero) SetWindowPos(Handle, IntPtr.Zero, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0004 | 0x0010 | 0x0020); }

    int InsetX { get { return Theme.D(4); } }
    int InsetY
    {
        get
        {
            if (box.Multiline) return Theme.D(4);
            return Math.Max(Theme.D(2), (box.Height - box.Font.Height) / 2);   // one line: centred
        }
    }

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case WM_NCCALCSIZE:
            {
                base.WndProc(ref m);
                var r = (RECT)Marshal.PtrToStructure(m.LParam, typeof(RECT));
                r.Left += InsetX; r.Right -= InsetX; r.Top += InsetY; r.Bottom -= InsetY;
                Marshal.StructureToPtr(r, m.LParam, false);
                m.Result = IntPtr.Zero;
                return;
            }
            case WM_NCHITTEST:
                base.WndProc(ref m);
                // The frame counts as the box: clicking it puts the caret in, as on its text.
                if ((int)m.Result == HTNOWHERE || (int)m.Result == HTBORDER) m.Result = (IntPtr)HTCLIENT;
                return;
            case WM_NCPAINT:
                base.WndProc(ref m);
                Paint();
                m.Result = IntPtr.Zero;
                return;
        }
        base.WndProc(ref m);
    }

    void Paint()
    {
        IntPtr dc = GetWindowDC(Handle);
        if (dc == IntPtr.Zero) return;
        try
        {
            using (var g = Graphics.FromHdc(dc))
            {
                int w = box.Width, h = box.Height;
                g.ExcludeClip(new Rectangle(InsetX, InsetY, w - InsetX * 2, h - InsetY * 2));
                using (var b = new SolidBrush(box.Parent != null ? box.Parent.BackColor : Theme.Bg)) g.FillRectangle(b, 0, 0, w, h);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var path = Theme.Shape(new Rectangle(0, 0, w - 1, h - 1), Theme.D(6), true, true))
                {
                    using (var b = new SolidBrush(box.BackColor)) g.FillPath(b, path);
                    using (var p = new Pen(box.Focused && !box.ReadOnly ? Theme.Accent : Theme.Border)) g.DrawPath(p, path);
                }
            }
        }
        finally { ReleaseDC(Handle, dc); }
    }
}

class NoteBox : TextBox
{
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
    const int WM_PAINT = 0x0F, WM_SETCURSOR = 0x20, WM_MOUSEMOVE = 0x200, WM_LBUTTONDOWN = 0x201, WM_LBUTTONUP = 0x202,
        WM_LBUTTONDBLCLK = 0x203, WM_MOUSEWHEEL = 0x20A, WM_CAPTURECHANGED = 0x215, WM_MOUSELEAVE = 0x2A3,
        EM_GETFIRSTVISIBLELINE = 0xCE, EM_GETLINECOUNT = 0xBA, EM_LINESCROLL = 0xB6, EM_SETMARGINS = 0xD3;
    readonly OverlayScroll bar;
    Rectangle shown = Rectangle.Empty;
    bool shownWide;
    int wheel;

    public NoteBox()
    {
        Multiline = true; AcceptsReturn = true; WordWrap = true;
        ScrollBars = ScrollBars.None;
        bar = new OverlayScroll
        {
            Client = () => ClientSize,
            Content = () => LineCount,
            View = () => View,
            Pos = () => First,
            Set = ScrollToLine,
            ToClient = PointToClient,
            Changed = () => { if (IsHandleCreated) Invalidate(Rectangle.Inflate(bar.Strip, 1, 1)); },
            Right = OverlayBar.Margin,   // inside the box's border
            Line = 1,
        };
    }

    protected override void Dispose(bool disposing) { if (disposing) bar.Dispose(); base.Dispose(disposing); }

    // Room on the right for the bar (even wide), so text never runs under it.
    void RightMargin()
    {
        if (IsHandleCreated) SendMessage(Handle, EM_SETMARGINS, (IntPtr)2, (IntPtr)((OverlayBar.Wide + OverlayBar.Margin + Theme.D(4)) << 16));
    }
    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); RightMargin(); }
    protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); RightMargin(); }

    int First { get { return IsHandleCreated ? (int)SendMessage(Handle, EM_GETFIRSTVISIBLELINE, IntPtr.Zero, IntPtr.Zero) : 0; } }
    int LineCount { get { return IsHandleCreated ? (int)SendMessage(Handle, EM_GETLINECOUNT, IntPtr.Zero, IntPtr.Zero) : 0; } }
    int View { get { return Math.Max(1, ClientSize.Height / Math.Max(1, TextRenderer.MeasureText("A", Font, Size.Empty, TextFormatFlags.NoPadding).Height)); } }

    void ScrollToLine(int line)
    {
        line = Math.Max(0, Math.Min(line, LineCount - View));
        int d = line - First;
        if (d != 0) SendMessage(Handle, EM_LINESCROLL, IntPtr.Zero, (IntPtr)d);
        Check();
    }

    protected override void WndProc(ref Message m)
    {
        // The edit control's own messages (EM_*), including the ones asked here, go straight through.
        if (!IsHandleCreated || (m.Msg >= 0xB0 && m.Msg <= 0xDA)) { base.WndProc(ref m); return; }
        var pt = new Point((short)((long)m.LParam & 0xFFFF), (short)(((long)m.LParam >> 16) & 0xFFFF));
        switch (m.Msg)
        {
            case WM_LBUTTONDOWN:
            case WM_LBUTTONDBLCLK:
                if (bar.Down(pt)) { Capture = true; return; }
                break;
            case WM_MOUSEMOVE:
                if (bar.Drag(pt)) return;
                bar.Hover(pt);
                break;
            case WM_LBUTTONUP:
                if (bar.Up()) { Capture = false; return; }
                break;
            case WM_CAPTURECHANGED:
                bar.Up();
                break;
            case WM_MOUSELEAVE:
                bar.Leave();
                break;
            case WM_MOUSEWHEEL:
                // Without Windows' scroll bar the edit control ignores the wheel, so it's scrolled here, by
                // the usual number of lines a notch (touchpads send parts of a notch, which add up).
                int lines = SystemInformation.MouseWheelScrollLines;
                wheel += (short)(((long)m.WParam >> 16) & 0xFFFF) * (lines > 0 ? lines : 3);
                int by = wheel / 120, first = First, to = Math.Max(0, Math.Min(LineCount - View, first - by));
                wheel -= by * 120;
                if (to != first) { ScrollToLine(to); m.Result = IntPtr.Zero; return; }
                wheel = 0;
                break;   // already at the end: the panel around it gets the wheel
            case WM_SETCURSOR:
                if (bar.Covers(PointToClient(Cursor.Position))) { Cursor.Current = Cursors.Default; m.Result = (IntPtr)1; return; }
                break;
        }
        base.WndProc(ref m);
        if (m.Msg == WM_PAINT) { PaintBar(); return; }
        Check();
        // Typing and editing redraw lines straight away (no WM_PAINT), which can paint over the bar.
        if ((m.Msg >= 0x100 && m.Msg <= 0x109) || (m.Msg >= 0x300 && m.Msg <= 0x304) || m.Msg == 0x0C || m.Msg == 0x0B
            || (m.Msg == WM_MOUSEMOVE && MouseButtons == MouseButtons.Left)) PaintBar();
    }

    // After anything that may have scrolled or changed the text: repaints the bar where it moved to.
    void Check()
    {
        if (bar.Thumb != shown || bar.Wide != shownWide) Invalidate(Rectangle.Inflate(bar.Strip, 1, 1));
    }

    void PaintBar()
    {
        shown = bar.Thumb; shownWide = bar.Wide;
        if (!bar.Scrollable) return;
        using (var g = CreateGraphics()) bar.Paint(g);
    }
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
        Theme.Surface(g, ClientRectangle, Parent != null ? Parent.BackColor : Theme.Bg, Theme.CardTop, Theme.CardBottom, Theme.Border, false);
        int n = Options.Length;
        for (int i = 0; i < n; i++)
        {
            var r = new Rectangle(i * Width / n, 0, (i + 1) * Width / n - i * Width / n, Height);
            if (i == selectedIndex)
            {
                var mode = g.SmoothingMode;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                // Flush with the edge, so it lines up with the fields below it (as on the Mac).
                using (var path = Theme.Shape(r, Theme.D(Theme.Radius)))
                using (var b = new LinearGradientBrush(r, Theme.Highlight, Theme.AccentHover, LinearGradientMode.Vertical)) g.FillPath(b, path);
                g.SmoothingMode = mode;
            }
            TextRenderer.DrawText(g, Options[i], Theme.Bold, r, i == selectedIndex ? Theme.Bg : Theme.Text,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }
    }
}

// A bordered container that groups related rows, a shade darker than the rows inside it.
class BoxPanel : Panel
{
    public static Color Fill { get { return Theme.BoxFill; } }

    public BoxPanel()
    {
        BackColor = Fill;
        SetStyle(ControlStyles.ResizeRedraw, true);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Theme.Surface(e.Graphics, ClientRectangle, Parent != null ? Parent.BackColor : Theme.Bg, Fill, Fill, Theme.Border, false);
    }
}

// A full-window page with a centered fixed-width column, like a setup screen.
class Page : ScrollPanel
{
    public readonly Panel Col = new Panel();
    // Set for pages whose column fills the window height (it lays out the column's contents).
    public Action Fill;
    // Pages are laid out for an 84-tall logo; the logo is now 56 tall, so everything below it moves up.
    const int LogoH = 56, LayoutLogoH = 84;
    readonly int shift;
    // A column that widens with the window, up to this many design units (0: a fixed width). Controls anchored
    // Left and Right grow with it; ones anchored Right move with its right edge.
    public int MaxWidth;
    // The room kept either side of a widening column, as at the default window size.
    const int SideRoom = 110;

    // `width`: the column's width in design units (Virtual Machines is wider, for each row's buttons).
    public Page(int colHeight, bool logo = true, int width = 640)
    {
        Dock = DockStyle.Fill;
        BackColor = Theme.Bg;
        Scrolls = logo;   // with the overlay scroll bar when the window is shorter than the page
        Col.BackColor = Theme.Bg;
        shift = logo ? LayoutLogoH - LogoH : 0;
        Col.Size = new Size(Theme.D(width), Theme.D(Math.Max(0, colHeight - shift)));
        Controls.Add(Col);
        minWidth = Col.Width;
        if (logo)
        {
            var l = new PixelLogo { Px = 7, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
            l.SetBounds(0, 0, Theme.D(width), Theme.D(LogoH));
            Col.Controls.Add(l);
        }
    }

    readonly int minWidth;

    protected override void OnResize(EventArgs e)
    {
        if (MaxWidth > 0)
        {
            int w = Math.Max(minWidth, Math.Min(Theme.D(MaxWidth), ClientSize.Width - 2 * Theme.D(SideRoom)));
            if (w != Col.Width) Col.Width = w;
        }
        int x = Math.Max(0, (ClientSize.Width - Col.Width) / 2);
        if (Fill != null) { Col.Location = new Point(x, -ScrollY); Fill(); }
        else Col.Location = new Point(x, Math.Max(0, (ClientSize.Height - Col.Height) / 2) - ScrollY);
        base.OnResize(e);   // keeps the scroll position within the column's new place (e.g. after notifications shrink the page)
    }

    // A page's Back and Next (or Install) buttons: the same width, fitted to the longer text, 36 tall like the
    // header's buttons, at the column's left and right edges. Either can be null (the first page has no Back).
    public void Nav(Button back, Button next)
    {
        int w = Math.Max(back != null ? Sheet.SnugWidth(back.Text) : 0, next != null ? Sheet.SnugWidth(next.Text) : 0) + 8;
        foreach (var b in new[] { back, next })
        {
            if (b == null) continue;
            b.Top += (b.Height - Theme.D(36)) / 2;
            b.Size = new Size(Theme.D(w), Theme.D(36));
        }
        if (back != null) back.Left = 0;
        if (next != null) { next.Left = Col.Width - next.Width; next.Anchor = AnchorStyles.Top | AnchorStyles.Right; }
    }

    public T Add<T>(T c, int x, int y, int w, int h) where T : Control
    {
        c.SetBounds(Theme.D(x), Theme.D(y - shift), Theme.D(w), Theme.D(h));
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
        var t = Add(new TextBox { Text = text, Font = Theme.Body, BackColor = Theme.Card, ForeColor = Theme.Text, BorderStyle = BorderStyle.FixedSingle }, x, y, w, 26);
        RoundBox.Attach(t);
        t.Height = Theme.D(30);
        Native.TextMargins(t);
        return t;
    }

    // A text box of several lines (e.g. a description), with the overlay scroll bar when the text is longer.
    public TextBox Notes(string text, int x, int y, int w, int h)
    {
        var t = Add(new NoteBox { Text = text, Font = Theme.Body, BackColor = Theme.Card, ForeColor = Theme.Text, BorderStyle = BorderStyle.FixedSingle }, x, y, w, h);
        RoundBox.Attach(t);
        Native.TextMargins(t);
        return t;
    }

    public CheckBox Check(string text, bool on, int x, int y, int w)
    {
        var c = Add(new CheckBox { Text = text, Checked = on, Font = Theme.Body, ForeColor = Theme.Text, BackColor = Theme.Bg, FlatStyle = FlatStyle.Flat, UseMnemonic = false }, x, y, w, 26);
        c.FlatAppearance.BorderColor = Theme.Border;
        c.FlatAppearance.CheckedBackColor = Theme.Highlight;
        return c;
    }
}

// Resource profile chooser shared by the setup and settings pages.
class ResourcePicker
{
    Segmented profile;
    SizeBox ram, cores;
    public void Attach(Page p, int y, VmConfig c)
    {
        // Laid out like Settings > Hardware > Resources: a row each, with its limit beside it.
        p.Caption("Resource profile", 0, y, 180, 30);
        profile = p.Add(new Segmented { Options = VmConfig.ProfileNames }, 190, y, 410, 30);
        p.Caption("Memory (GiB)", 0, y + 40, 180, 26);
        ram = p.Add(Sys.RamBox(Sys.MaxRamGB, c.RamGB), 190, y + 40, 100, 26);
        p.Caption("up to " + Sys.MaxRamGB + " of " + Sys.TotalRamGB + " GiB", 300, y + 40, 300, 26, Theme.Small, Theme.Muted);
        p.Caption("CPU cores", 0, y + 74, 180, 26);
        cores = p.Add(Sys.CoreBox(Sys.MaxCores, c.Cores), 190, y + 74, 100, 26);
        p.Caption("up to " + cores.Maximum + " of " + Environment.ProcessorCount, 300, y + 74, 300, 26, Theme.Small, Theme.Muted);
        profile.Changed += delegate { Refresh(); };
        ram.ValueChanged += delegate { Refresh(); };
        cores.ValueChanged += delegate { Refresh(); };
        profile.SelectedIndex = Math.Max(0, Math.Min(2, c.Profile));
    }

    bool showing;

    void Refresh()
    {
        if (showing) return;
        bool manual = profile.SelectedIndex == 2;
        ram.Locked = cores.Locked = !manual;
        var probe = new VmConfig();
        Store(probe);
        int n, gb;
        probe.Resources(out n, out gb);
        // Balanced and Maximum show what they'll give in the greyed-out fields; Manual starts from there.
        if (!manual) { showing = true; ram.Value = gb; cores.Value = n; showing = false; }
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
    public string Disk;   // an existing virtual disk to import (copied into a new qcow2), instead of an installer
    public DiskSource Source;   // what was read from that disk and the settings beside it
    public bool Own;      // chosen on Use Your Own Installer (a step before General)
}

// What an existing virtual disk tells about itself: its format and size (qemu-img), and the settings saved
// beside it by Try Linux (vm.ini, or a backup's .ini), VMware (.vmx) or VirtualBox (.vbox), when there are any.
class DiskSource
{
    public string Format = "", Name = "", Description = "", From = "";
    public long Virtual = -1;
    public int RamGB, Cores;

    public static DiskSource Read(string disk)
    {
        var s = new DiskSource();
        try
        {
            if (Winq.Ready)
            {
                var o = (Dictionary<string, object>)new System.Web.Script.Serialization.JavaScriptSerializer()
                    .DeserializeObject(Winq.Run(Winq.Img, "info --force-share --output=json " + MainForm.Q(disk)));
                if (o.ContainsKey("format")) s.Format = o["format"].ToString();
                if (o.ContainsKey("virtual-size")) s.Virtual = Convert.ToInt64(o["virtual-size"]);
            }
        }
        catch { }
        try { s.ReadSettings(disk); } catch { }
        return s;
    }

    void ReadSettings(string disk)
    {
        string dir = Path.GetDirectoryName(disk), file = Path.GetFileName(disk);
        // Try Linux: a backup's settings, or the vm.ini of the system the disk belongs to.
        string ini = File.Exists(disk + ".ini") ? disk + ".ini" : Path.Combine(dir, "vm.ini");
        if (File.Exists(ini))
        {
            var c = VmConfig.LoadIni(ini);
            c.Dir = dir;
            if (File.Exists(disk + ".ini") || string.Equals(Path.GetFileNameWithoutExtension(file), Path.GetFileName(dir), StringComparison.OrdinalIgnoreCase))
            {
                Name = c.DisplayName; Description = c.Description ?? "";
                int n, gb; c.Resources(out n, out gb);
                RamGB = gb; Cores = n;
                From = "Try Linux";
                return;
            }
        }
        // VMware: displayName, memsize (MB) and numvcpus in a .vmx that names this disk.
        foreach (var vmx in Directory.GetFiles(dir, "*.vmx"))
        {
            string text = File.ReadAllText(vmx);
            if (text.IndexOf(file, StringComparison.OrdinalIgnoreCase) < 0) continue;
            Func<string, string> get = key =>
            {
                var m = Regex.Match(text, "^\\s*" + Regex.Escape(key) + "\\s*=\\s*\"([^\"]*)\"", RegexOptions.Multiline | RegexOptions.IgnoreCase);
                return m.Success ? m.Groups[1].Value : "";
            };
            Name = get("displayName"); Description = get("annotation").Replace("|0A", "\n").Replace("|0D", "");
            int mb, cpus;
            if (int.TryParse(get("memsize"), out mb)) RamGB = Math.Max(1, (mb + 1023) / 1024);
            if (int.TryParse(get("numvcpus"), out cpus)) Cores = cpus;
            From = "VMware (" + Path.GetFileName(vmx) + ")";
            return;
        }
        // VirtualBox: the machine's name, description, RAMSize (MB) and CPU count, in a .vbox beside it or one folder up.
        foreach (var folder in new[] { dir, Path.GetDirectoryName(dir) })
        {
            if (folder == null) continue;
            foreach (var vbox in Directory.GetFiles(folder, "*.vbox"))
            {
                var x = System.Xml.Linq.XDocument.Load(vbox);
                if (!x.Descendants().Any(e => e.Name.LocalName == "HardDisk" && ((string)e.Attribute("location") ?? "").EndsWith(file, StringComparison.OrdinalIgnoreCase))) continue;
                var machine = x.Descendants().FirstOrDefault(e => e.Name.LocalName == "Machine");
                if (machine == null) continue;
                Name = (string)machine.Attribute("name") ?? "";
                var desc = machine.Elements().FirstOrDefault(e => e.Name.LocalName == "Description");
                if (desc != null) Description = desc.Value.Trim();
                var memory = x.Descendants().FirstOrDefault(e => e.Name.LocalName == "Memory");
                int mb, cpus;
                if (memory != null && int.TryParse((string)memory.Attribute("RAMSize"), out mb)) RamGB = Math.Max(1, (mb + 1023) / 1024);
                var cpu = x.Descendants().FirstOrDefault(e => e.Name.LocalName == "CPU");
                Cores = cpu != null && int.TryParse((string)cpu.Attribute("count"), out cpus) ? cpus : 1;
                From = "VirtualBox (" + Path.GetFileName(vbox) + ")";
                return;
            }
        }
    }

    // One line for Use Your Own Installer, e.g. "vmdk · 64 GiB · settings from VMware (Fedora.vmx): 8 GiB, 4 cores".
    public string Summary()
    {
        var parts = new List<string>();
        if (Format.Length > 0) parts.Add(Format);
        if (Virtual > 0) parts.Add((Virtual / 1073741824.0).ToString("0.#") + " GiB");
        if (From.Length > 0) parts.Add("settings from " + From + (RamGB > 0 ? ": " + RamGB + " GiB, " + Cores + (Cores == 1 ? " core" : " cores") : ""));
        else parts.Add("no saved settings found beside it");
        return string.Join("  ·  ", parts);
    }
}

// What the Create steps (General, Resources, Summary) have collected so far, kept across Back and Next.
class CreateDraft
{
    public InstallPlan Plan;
    public VmConfig Cfg;
    public string Name = "", Description = "", Location = "";
    public bool OwnLocation;
    public int DiskGB = 64;
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
    // Started from a shortcut or at sign-in (--start): go to the tray once it's up. Otherwise Try Linux stays open.
    bool trayAfterStart;
    bool busy, cancelRequested, quitting, trayHintShown, restartPending;
    readonly NoticeBar notices = new NoticeBar();   // the notification area above every page
    readonly HeaderBar header = new HeaderBar();    // Create / Back, the title or tabs, and ☰ / Save, above it
    TextBox searchBox;                              // Choose a Linux OS's search (Ctrl+F)
    string stagedUpdate;    // a verified update waiting to be applied when Try Linux quits
    Page page;
    Label pStatus, pDetail, pTip;
    Bar pBar;
    Timer tipTimer;

    static int D(int v) { return Theme.D(v); }

    public MainForm(string startVm)
    {
        startArg = startVm;
        AppSettings.Load();
        Theme.Apply(AppSettings.EffectiveTheme);
        Text = "Try Linux";
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        Font = Theme.Body;
        Icon = Icons.App();
        StartPosition = FormStartPosition.CenterScreen;
        var area = System.Windows.Forms.Screen.PrimaryScreen.WorkingArea;
        ClientSize = new Size(Math.Min(D(980), area.Width - 40), Math.Min(D(760), area.Height - 60));
        MinimumSize = new Size(D(700), D(520));

        tray = new NotifyIcon { Icon = Icon, Text = "Try Linux", Visible = true, ContextMenuStrip = new ContextMenuStrip { Renderer = new DarkMenu(), ShowItemToolTips = true } };
        tray.ContextMenuStrip.Opening += delegate { BuildTrayMenu(); };
        tray.DoubleClick += delegate { ShowLauncher(); };
        Controls.Add(notices);
        Controls.Add(header);   // docked above the notifications

        AppSettings.Load();
        Winq.Detect();
        // Follow system: Windows switching between light and dark switches Try Linux too.
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += (s, e) =>
        {
            if (AppSettings.FollowSystem && IsHandleCreated) BeginInvoke((Action)RefreshTheme);
        };
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.DarkTitleBar(Handle);
        Native.RedrawFrame(Handle);   // drops the title bar (see WndProc)
        // Tall enough for the tallest page (Choose a Linux OS) under the header bar, so its Back and Next show
        // without scrolling. Set after the title bar is gone, from the frame Windows now actually draws.
        var area = System.Windows.Forms.Screen.FromHandle(Handle).WorkingArea;
        int frameW = Width - ClientSize.Width, frameH = Height - ClientSize.Height;
        Size = new Size(Math.Min(D(980), area.Width - 40) + frameW, Math.Min(D(830), area.Height - 40) + frameH);
    }

    // No Windows title bar, like the Linux edition: the header bar takes its place. The window keeps its resizing
    // edges, snapping and shadow; the header bar's empty space moves it, and double-clicking it maximizes.
    [StructLayout(LayoutKind.Sequential)] struct WinRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] static extern bool IsZoomed(IntPtr h);
    const int WM_NCCALCSIZE = 0x83, WM_NCHITTEST = 0x84, HTCLIENT = 1, HTCAPTION = 2, HTTOP = 12, HTTOPLEFT = 13, HTTOPRIGHT = 14;

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_NCCALCSIZE && m.WParam != IntPtr.Zero)
        {
            // Windows works out the side and bottom edges as usual; the top stays where the window starts.
            var before = (WinRect)Marshal.PtrToStructure(m.LParam, typeof(WinRect));
            base.WndProc(ref m);
            var after = (WinRect)Marshal.PtrToStructure(m.LParam, typeof(WinRect));
            after.Top = before.Top;
            // Maximized, the window reaches past the screen by its frame on every side, so the client is inset by it.
            if (IsZoomed(Handle))   // WindowState can still say Normal while Windows is maximizing it
            {
                int frame = GetSystemMetrics(33) + GetSystemMetrics(92);   // SM_CYSIZEFRAME + SM_CXPADDEDBORDER
                after = new WinRect { Left = before.Left + frame, Top = before.Top + frame, Right = before.Right - frame, Bottom = before.Bottom - frame };
            }
            Marshal.StructureToPtr(after, m.LParam, false);
            m.Result = IntPtr.Zero;
            return;
        }
        base.WndProc(ref m);
        if (m.Msg == WM_NCHITTEST && (int)m.Result == HTCLIENT)
        {
            var pt = PointToClient(new Point(unchecked((short)(long)m.LParam), unchecked((short)((long)m.LParam >> 16))));
            int edge = GetSystemMetrics(33) + GetSystemMetrics(92);
            if (!IsZoomed(Handle) && pt.Y < edge)
                m.Result = (IntPtr)(pt.X < edge * 2 ? HTTOPLEFT : pt.X >= ClientSize.Width - edge * 2 ? HTTOPRIGHT : HTTOP);
            else if (header.Visible && pt.Y < header.Bottom)
                m.Result = (IntPtr)HTCAPTION;
        }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        SelfInstall.RemoveOldCopies();
        Task.Run(() => Updater.Check(false)).ContinueWith(t =>
        {
            if (t.IsFaulted || IsDisposed || (t.Result == null && Updater.WinqAvailable == null)) return;
            try { BeginInvoke((Action)(() => { Updater.Available = t.Result; ShowUpdateButton(); })); } catch { }
        });
        if (startArg != null)
        {
            var cfg = VmConfig.All().FirstOrDefault(c => c.Name.Equals(startArg, StringComparison.OrdinalIgnoreCase));
            if (cfg != null) { trayAfterStart = true; Boot(cfg); return; }
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
        // Without Try Linux nothing could show a hidden system again, so show them all.
        StopWatchingCloseButtons();
        // Paused ones are resumed for the same reason.
        foreach (var vm in running.Values)
        {
            if (vm.Hidden) Native.ShowAndFocus(vm.Window);
            if (vm.Paused) try { Net.Qmp(vm.QmpPort, "cont"); } catch { }
        }
        tray.Visible = false;
        tray.Dispose();
        if (stagedUpdate != null) try { Updater.Apply(stagedUpdate, Updater.Available); } catch { }
        base.OnFormClosing(e);
    }

    void SetPage(Page p)
    {
        if (tipTimer != null) { tipTimer.Dispose(); tipTimer = null; }
        if (liveTimer != null) { liveTimer.Dispose(); liveTimer = null; }
        homeRows = new List<OptionRow>();
        SuspendLayout();
        header.Clear();   // the page sets its own after this
        searchBox = null;
        var old = page;
        page = p;
        Controls.Add(p);
        p.BringToFront();   // docked last, so it fills the space under the notification area
        if (old != null) { Controls.Remove(old); old.Dispose(); }
        ResumeLayout();
    }

    // ------------------------------------------------------------ pages

    // Virtual Machines: like the Linux edition, the header bar has Create and the ☰ menu, and each system's
    // row has its own Console, Start (or Power) and Settings buttons. Double-click a row to start
    // it.
    void ShowHome()
    {
        var vms = VmConfig.All();
        if (vms.Count == 0) { ShowChoose(); return; }

        const int width = 760, reserve = 196;   // the column, and the room on each row's right for its buttons
        // The column widens with the window (names get the extra room); at the default size it's 760.
        var p = new Page(470, true, width) { MaxWidth = 1200 };
        p.Caption("Virtual Machines", 0, 116, 300, 30, Theme.Title);
        // Column headers on the title's line (3 higher than its box: their letters sit on its baseline). They're
        // laid out from the right, like the rows' stats, so they move with the column's right edge.
        int x = width - OptionRow.Pad - reserve;
        var right = AnchorStyles.Top | AnchorStyles.Right;
        foreach (var label in new[] { "DISK", "MEM", "CPU" })
        {
            x -= OptionRow.StatColW;
            var c = p.Caption(label, x, 113, OptionRow.StatColW, 30, Theme.Small, Theme.Muted, ContentAlignment.BottomLeft);
            c.Anchor = right; c.BringToFront();
        }
        var st = p.Caption("STATUS", x - 10 - OptionRow.StatusColW, 113, OptionRow.StatusColW, 30, Theme.Small, Theme.Muted, ContentAlignment.BottomLeft);
        st.Anchor = right; st.BringToFront();
        var list = p.Add(new OptionList { Gap = 0, RowHeight = 62, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right }, 0, 160, width, 300);
        p.Tag = "home";
        var rows = new List<OptionRow>();
        foreach (var vm in vms)
        {
            var v = vm;
            var row = list.AddItem(vm.DisplayName, OsLabel(vm), vm, Status(vm), On(vm) ? Theme.Success : Theme.Muted);
            row.Reserve = D(reserve);
            bool on = On(vm);
            var act = new FlatBtn(on ? "Power  \u25be" : vm.PendingInstall ? "Install" : "Start", !on);
            var console = new FlatBtn(">_") { Font = new Font("Consolas", 10f, FontStyle.Bold) };   // a terminal prompt
            var gear = new FlatBtn("\uE713") { Font = new Font("Segoe MDL2 Assets", 10f) };
            var tip = new ToolTip();
            tip.SetToolTip(console, on ? "Show console" : "Open console");
            tip.SetToolTip(gear, "Settings");
            row.Controls.Add(console);
            row.Controls.Add(act);
            row.Controls.Add(gear);
            EventHandler place = delegate
            {
                int h = D(34), y = (row.Height - h) / 2;
                gear.SetBounds(row.Width - D(14) - D(38), y, D(38), h);
                act.SetBounds(gear.Left - D(8) - D(88), y, D(88), h);
                console.SetBounds(act.Left - D(8) - D(38), y, D(38), h);
            };
            console.Click += delegate { OpenConsole(v); };
            row.Resize += place;
            place(row, EventArgs.Empty);
            act.Click += delegate
            {
                RunningVm r;
                if (running.TryGetValue(v.Name, out r) && !r.Held)
                {
                    // A running system: the Power menu, with its console first.
                    var menu = new ContextMenuStrip { Renderer = new DarkMenu(), Font = Theme.Body, ShowItemToolTips = true, ShowImageMargin = false,
                                                      MinimumSize = new Size(act.Width, 0) };
                    menu.Items.Add(new ToolStripMenuItem("Show console", null, delegate { ShowVm(r); }) { ToolTipText = "Brings its window to the front" });
                    menu.Items.Add(new ToolStripSeparator());
                    menu.Items.AddRange(PowerItems(r));
                    foreach (ToolStripItem i in menu.Items) i.Padding = new Padding(D(6), D(3), 0, D(3));
                    menu.Closed += delegate { BeginInvoke((Action)menu.Dispose); };
                    menu.Show(act, new Point(0, act.Height));
                    return;
                }
                if (r != null && r.Held) PowerOn(r); else Boot(v);
            };
            gear.Click += delegate { Sheet.ResetSections(); ShowSettings(v); };   // every section starts closed
            rows.Add(row);
        }
        for (int i = 0; i < rows.Count; i++) rows[i].Join = (i == 0 ? 1 : 0) | (i == rows.Count - 1 ? 2 : 0);
        homeRows = rows;   // again after SetPage, which clears it for the page it replaces
        ShowLiveStats();
        list.Activated += delegate { var vm = (VmConfig)list.SelectedTag; if (vm != null && !On(vm)) Boot(vm); };
        ShowUpdateButton();
        SetPage(p);
        var create = new FlatBtn("Create", true) { Size = new Size(D(88), D(36)) };
        create.Click += delegate { ShowChoose(); };
        var more = new FlatBtn("") { Font = new Font("Segoe MDL2 Assets", 10f), Size = new Size(D(40), D(36)) };
        more.Click += delegate { MainMenu().Show(more, new Point(more.Width, more.Height), ToolStripDropDownDirection.BelowLeft); };
        header.Set(create, null, more);   // no title: the logo below already says Try Linux
        // Running systems' activity, read every two seconds while this page is open.
        homeRows = rows;
        liveTimer = new Timer { Interval = 2000 };
        liveTimer.Tick += delegate { SampleLive(); };
        liveTimer.Start();
        SampleLive();
    }

    List<OptionRow> homeRows = new List<OptionRow>();
    Timer liveTimer;
    bool sampling;

    void SampleLive()
    {
        var vms = running.Values.Where(r => !r.Held && r.P != null).ToList();
        if (sampling || vms.Count == 0) return;
        sampling = true;
        Task.Run(() => { foreach (var r in vms) r.Sample(); }).ContinueWith(t =>
        {
            try { BeginInvoke((Action)(() => { sampling = false; ShowLiveStats(); })); } catch { sampling = false; }
        });
    }

    // A running system's row shows what it's doing now, above what it was given; one that's off, what it was given.
    void ShowLiveStats()
    {
        foreach (var row in homeRows)
        {
            var vm = row.Tag as VmConfig;
            RunningVm r;
            if (vm == null || row.IsDisposed) continue;
            int cores, ram;
            vm.Resources(out cores, out ram);
            string coresText = cores + (cores == 1 ? " core" : " cores"), ramText = ram + " GB", disk = DiskUsed(vm);
            if (running.TryGetValue(vm.Name, out r) && !r.Held && r.CpuPct >= 0)
            {
                row.Stats = new[] {
                    Tuple.Create("CPU", Math.Round(r.CpuPct) + "%"),
                    // What Linux is using; without the balloon's report, what QEMU holds on this PC (all of it, with WHPX).
                    Tuple.Create("MEM", r.GuestUsed >= 0 ? DiskSize(r.GuestUsed) : r.MemBytes >= 0 ? DiskSize(r.MemBytes) : ramText),
                    Tuple.Create("DISK", r.DiskRate >= 0 ? Rate(r.DiskRate) : disk),
                };
                row.StatSubs = new[] { "of " + coresText, "of " + ramText, disk };
            }
            else
            {
                row.Stats = new[] { Tuple.Create("CPU", coresText), Tuple.Create("MEM", ramText), Tuple.Create("DISK", disk) };
                row.StatSubs = null;
            }
            row.Invalidate();
        }
    }

    static string Rate(double bytesPerSecond)
    {
        if (bytesPerSecond < 1024) return "0 KB/s";
        if (bytesPerSecond < 1024 * 1024) return Math.Round(bytesPerSecond / 1024) + " KB/s";
        return (bytesPerSecond / 1024 / 1024).ToString(bytesPerSecond < 10 * 1024 * 1024 ? "0.0" : "0") + " MB/s";
    }

    // ---- the main menu (☰), its windows and keyboard shortcuts

    ContextMenuStrip MainMenu()
    {
        var m = new ContextMenuStrip { Renderer = new DarkMenu(), Font = Theme.Body, ShowImageMargin = false };
        Func<string, string, Action, ToolStripMenuItem> item = (text, keys, act) =>
            new ToolStripMenuItem(text, null, delegate { act(); }) { ShortcutKeyDisplayString = keys, ShowShortcutKeys = keys.Length > 0 };
        m.Items.Add(item("Check for Updates", "Ctrl+U", CheckForUpdatesNow));
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add(item("Preferences", "Ctrl+,", ShowPreferences));
        m.Items.Add(item("Keyboard Shortcuts", "Ctrl+?", ShowShortcuts));
        m.Items.Add(item("About Try Linux", "", ShowAbout));
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add(item("Quit Try Linux", "Ctrl+Q", QuitApp));
        foreach (ToolStripItem i in m.Items) i.Padding = new Padding(D(6), D(4), D(6), D(4));
        m.Closed += delegate { BeginInvoke((Action)m.Dispose); };
        return m;
    }

    // Every keyboard shortcut, as (keys, what it does); Keyboard Shortcuts lists them.
    static readonly string[][] Shortcuts = {
        new[] { "General", "" },
        new[] { "Ctrl+N", "Create a virtual machine" },
        new[] { "Ctrl+F", "Search distributions (on Choose a Linux OS)" },
        new[] { "Ctrl+U", "Check for updates" },
        new[] { "Ctrl+,", "Preferences" },
        new[] { "Ctrl+?", "Keyboard shortcuts" },
        new[] { "Ctrl+Q", "Quit Try Linux" },
        new[] { "Navigation", "" },
        new[] { "Alt+←", "Back" },
        new[] { "Esc", "Close a dialog" },
    };

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        switch (keyData)
        {
            case Keys.Control | Keys.N: if (!busy) ShowChoose(); return true;
            case Keys.Control | Keys.F:
                if (searchBox != null && !searchBox.IsDisposed) { searchBox.Focus(); searchBox.SelectAll(); }
                return true;
            case Keys.Control | Keys.U: CheckForUpdatesNow(); return true;
            case Keys.Control | Keys.Oemcomma: ShowPreferences(); return true;
            case Keys.Control | Keys.OemQuestion:
            case Keys.Control | Keys.Shift | Keys.OemQuestion: ShowShortcuts(); return true;
            case Keys.Control | Keys.Q: QuitApp(); return true;
            case Keys.Alt | Keys.Left:
                // The page's Back button (in the header bar, or at the bottom of the page).
                var back = header.Controls.OfType<FlatBtn>().Concat(page != null ? AllControls(page).OfType<FlatBtn>() : Enumerable.Empty<FlatBtn>())
                    .FirstOrDefault(b => b.Visible && b.Enabled && ((b.Tag as string) == "back" || b.Text == "Back"));
                if (back != null) { back.PerformClick(); return true; }
                break;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    static IEnumerable<Control> AllControls(Control c)
    {
        foreach (Control child in c.Controls)
        {
            yield return child;
            foreach (var d in AllControls(child)) yield return d;
        }
    }

    void QuitApp()
    {
        if (running.Count > 0 && !Ask("Quit Try Linux?", "Running systems keep running, but without the tray menu.", "Quit")) return;
        quitting = true;
        Close();
    }

    // Check for Updates (Ctrl+U): an available update is offered straight away; otherwise GitHub is asked now.
    async void CheckForUpdatesNow()
    {
        if (busy) return;
        if (Updater.Available != null || Updater.WinqAvailable != null) { UpdateClicked(); return; }
        string found = null, failed = null;
        Cursor = Cursors.WaitCursor;
        try { found = await Task.Run(() => Updater.Check(true)); }
        catch (Exception ex) { failed = ex.Message; }
        Cursor = Cursors.Default;
        if (IsDisposed) return;
        if (failed != null) { ThemedDialog.Show(this, "Couldn't check for updates", failed, null, null, "OK"); return; }
        if (found == null && Updater.WinqAvailable == null)
        {
            ThemedDialog.Show(this, "Try Linux is up to date", "You have the latest Try Linux (" + Version + ")" +
                (Winq.Version != null ? " and WINQ-EMU (" + Winq.Version + ")." : "."), null, null, "OK");
            return;
        }
        Updater.Available = found;
        ShowUpdateButton();
        UpdateClicked();
    }

    Form DialogForm(string title, int w, int h)
    {
        var f = new Form { Text = title, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false, ShowInTaskbar = false,
                           StartPosition = FormStartPosition.CenterParent, BackColor = Theme.Bg, ForeColor = Theme.Text, Font = Theme.Body,
                           ClientSize = new Size(D(w), D(h)), KeyPreview = true };
        f.HandleCreated += delegate { Native.DarkTitleBar(f.Handle); };
        f.KeyDown += (s2, e2) => { if (e2.KeyCode == Keys.Escape) f.Close(); };
        return f;
    }

    // Preferences (Ctrl+,): Try Linux as a whole. Changes apply straight away.
    void ShowPreferences()
    {
        bool reopen = false;
        using (var f = DialogForm("Preferences", 640, 380))
        {
            var sheet = new Sheet();
            sheet.Panel.Visible = true;
            sheet.Section("Appearance");
            var themePick = sheet.Row("Theme", Sheet.Combo(Theme.Titles), 160);
            themePick.SelectedIndex = Math.Max(0, Array.IndexOf(Theme.Names, AppSettings.ThemeName));
            var follow = sheet.Check("Follow system", AppSettings.FollowSystem);
            sheet.Note("Light while Windows shows apps in light mode, and Dark otherwise.", 1);
            themePick.Enabled = !follow.Checked;
            sheet.Section("Updates");
            var auto = sheet.Check("Check for updates automatically (once a day)", AppSettings.CheckUpdates);
            sheet.Note("You have Try Linux " + Version + (Winq.Version != null ? " and WINQ-EMU " + Winq.Version : "") + ".", 1);
            var check = sheet.Buttons(Sheet.Align.Left, "Check For Updates")[0];
            EventHandler look = delegate
            {
                AppSettings.ThemeName = Theme.Names[Math.Max(0, themePick.SelectedIndex)];
                AppSettings.FollowSystem = follow.Checked;
                try { AppSettings.Save(); } catch { }
                if (AppSettings.EffectiveTheme != Theme.Name) { reopen = true; f.Close(); }   // drawn again in the new theme
                themePick.Enabled = !follow.Checked;
            };
            themePick.SelectedIndexChanged += look;
            follow.CheckedChanged += look;
            auto.CheckedChanged += delegate { AppSettings.CheckUpdates = auto.Checked; try { AppSettings.Save(); } catch { } };
            check.Click += delegate { f.Close(); BeginInvoke((Action)CheckForUpdatesNow); };
            var holder = new Panel { Dock = DockStyle.Fill, Padding = new Padding(D(20), D(10), D(20), D(10)), BackColor = Theme.Bg };
            holder.Controls.Add(sheet.Panel);
            f.Controls.Add(holder);
            f.ShowDialog(this);
        }
        if (reopen) { RefreshTheme(); ShowPreferences(); }
    }

    // About Try Linux: versions, where things are kept, and links.
    void ShowAbout()
    {
        using (var f = DialogForm("About Try Linux", 520, 430))
        {
            Action<Control, int, int, int, int> add = (c, x, y, w, h) => { c.SetBounds(D(x), D(y), D(w), D(h)); f.Controls.Add(c); };
            add(new PictureBox { Image = Icons.Draw(D(72)), SizeMode = PictureBoxSizeMode.CenterImage, BackColor = Theme.Bg }, 0, 22, 520, 76);
            add(new Label { Text = "Try Linux", Font = Theme.Title, ForeColor = Theme.Text, BackColor = Theme.Bg, TextAlign = ContentAlignment.MiddleCenter }, 0, 104, 520, 34);
            add(new Label { Text = "Version " + Version, Font = Theme.Bold, ForeColor = Theme.Highlight, BackColor = Theme.Bg, TextAlign = ContentAlignment.MiddleCenter }, 0, 138, 520, 24);
            add(new Label { Text = "Try a Linux desktop on Windows in a virtual machine, from the official installer, checked against its published checksum.",
                            Font = Theme.Body, ForeColor = Theme.Muted, BackColor = Theme.Bg, TextAlign = ContentAlignment.MiddleCenter }, 40, 168, 440, 44);
            string winq = Winq.Ready ? "WINQ-EMU " + (Winq.Version ?? "") + (Updater.WinqAvailable != null ? "  (" + Updater.WinqAvailable + " available)" : "") : "WINQ-EMU isn't installed";
            add(new Label { Text = winq + "\nStorage: " + AppSettings.Storage, Font = Theme.Small, ForeColor = Theme.Muted, BackColor = Theme.Bg,
                            TextAlign = ContentAlignment.MiddleCenter, AutoEllipsis = true }, 20, 216, 480, 40);
            var names = new List<string> { "Website", "Report an Issue", "WINQ-EMU Releases", "Open Storage Folder" };
            if (!SelfInstall.IsInstalledCopy) names.Add("Install");
            if (Updater.WinqAvailable != null) names.Add("Update WINQ-EMU");
            int bx = 40, by = 270;
            foreach (var n in names)
            {
                var b = new FlatBtn(n);
                add(b, bx, by, 214, 36);
                string name = n;
                b.Click += delegate
                {
                    if (name == "Website") OpenUri("https://github.com/donnysaengkio/try-linux/tree/main/windows");
                    else if (name == "Report an Issue") OpenUri("https://github.com/donnysaengkio/try-linux/issues");
                    else if (name == "WINQ-EMU Releases") OpenUri("https://github.com/cmspam/winq-emu/releases");
                    else if (name == "Open Storage Folder") { Directory.CreateDirectory(AppSettings.Storage); Process.Start("explorer.exe", Q(AppSettings.Storage)); }
                    else if (name == "Install") { f.Close(); BeginInvoke((Action)InstallSelf); }
                    else if (name == "Update WINQ-EMU") { f.Close(); BeginInvoke((Action)StartWinqUpdate); }
                };
                if (bx == 40) bx = 266; else { bx = 40; by += 44; }
            }
            f.ClientSize = new Size(D(520), D(by + (bx == 266 ? 44 : 0) + 20));
            f.ShowDialog(this);
        }
    }

    void InstallSelf()
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
    }

    // Keyboard Shortcuts (Ctrl+?): each shortcut's keys as key caps, and what it does.
    void ShowShortcuts()
    {
        using (var f = DialogForm("Keyboard Shortcuts", 520, 30 + Shortcuts.Length * 38))
        {
            f.Paint += (s2, e2) =>
            {
                var g = e2.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                int y = D(16);
                var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix;
                foreach (var row in Shortcuts)
                {
                    if (row[1].Length == 0)
                    {
                        TextRenderer.DrawText(g, row[0], Theme.Heading, new Rectangle(D(24), y, D(400), D(30)), Theme.Text, flags);
                        y += D(38);
                        continue;
                    }
                    int x = D(24);
                    var keys = row[0].Split('+');
                    for (int i = 0; i < keys.Length; i++)
                    {
                        if (i > 0) { TextRenderer.DrawText(g, "+", Theme.Small, new Rectangle(x, y, D(16), D(30)), Theme.Muted, flags | TextFormatFlags.HorizontalCenter); x += D(18); }
                        int kw = Math.Max(D(32), TextRenderer.MeasureText(keys[i], Theme.Small).Width + D(12));
                        var r = new Rectangle(x, y, kw, D(30));
                        using (var path = Theme.Shape(r, D(6))) using (var b = new SolidBrush(Theme.Card)) g.FillPath(b, path);
                        using (var path = Theme.Shape(r, D(6))) using (var pen = new Pen(Theme.Border)) g.DrawPath(pen, path);
                        TextRenderer.DrawText(g, keys[i], Theme.Small, r, Theme.Text, flags | TextFormatFlags.HorizontalCenter);
                        x += kw + D(2);
                    }
                    TextRenderer.DrawText(g, row[1], Theme.Body, new Rectangle(D(140), y, D(370), D(30)), Theme.Text, flags | TextFormatFlags.EndEllipsis);
                    y += D(38);
                }
            };
            f.ShowDialog(this);
        }
    }

    // Updates are notifications: one for Try Linux and one for WINQ-EMU, each with an Update button.
    void ShowUpdateButton()
    {
        if (stagedUpdate != null)
            notices.Post("update", NoticeKind.Info, "Try Linux " + Updater.Available + " is downloaded and installs when you quit Try Linux.");
        else if (Updater.Available != null)
            notices.Post("update", NoticeKind.Info, "Try Linux " + Updater.Available + " is available. You have " + Version + ".", "Update", StartUpdate);
        else notices.Remove("update");
        if (Updater.WinqAvailable != null)
            notices.Post("winq", NoticeKind.Info, "WINQ-EMU " + Updater.WinqAvailable + " is available. You have " + (Winq.Version ?? "an older version") + ".",
                "Update", StartWinqUpdate);
        else notices.Remove("winq");
    }

    // One button covers both updates; with both available, it asks which one.
    void UpdateClicked()
    {
        bool app = Updater.Available != null && stagedUpdate == null, winq = Updater.WinqAvailable != null;
        if (app && winq)
        {
            var c = ThemedDialog.Choose(this, "Updates available",
                "Try Linux " + Updater.Available + " and WINQ-EMU " + Updater.WinqAvailable + " are available. Which would you like to update first?",
                null, null, "Try Linux", "WINQ-EMU");
            if (c == DialogResult.OK) StartUpdate();
            else if (c == DialogResult.No) StartWinqUpdate();
        }
        else if (app) StartUpdate();
        else if (winq) StartWinqUpdate();
    }

    // Downloads the newer WINQ-EMU installer and runs it. Systems must be shut down: their files are in use.
    async void StartWinqUpdate()
    {
        if (busy) return;
        string version = Updater.WinqAvailable, had = Winq.Version;
        var choice = ThemedDialog.Choose(this, "Update WINQ-EMU to " + version + "?",
            "You have WINQ-EMU " + had + ". Try Linux downloads the new installer from the WINQ-EMU GitHub page and runs it; Windows asks for permission.",
            null, "WINQ-EMU is an alpha, so check what's new first. Your Linux systems aren't affected.", "Update", "What's new");
        if (choice == DialogResult.No) { OpenUri(Updater.WinqReleases); return; }
        if (choice != DialogResult.OK) return;
        if (running.Count > 0)
        {
            ThemedDialog.Show(this, "Shut down your systems first",
                "WINQ-EMU's files are in use while a system runs. Shut down running systems from inside Linux, then update again.", null, null, "OK");
            return;
        }
        busy = true;
        cancelRequested = false;
        ShowProgress("Downloading WINQ-EMU " + version, false);
        string failed = null;
        try { await InstallWinq(() => cancelRequested, true); }
        catch (Exception ex) { failed = ex.Message; }
        busy = false;
        string now = Winq.Version;
        if (now != null && !Updater.IsNewerWinq(AppSettings.LatestWinq, now)) Updater.WinqAvailable = null;
        ShowHome();
        if (failed != null)
        {
            if (ThemedDialog.Show(this, "Couldn't update WINQ-EMU", failed, null, null, "Release page", "Close")) OpenUri(Updater.WinqReleases);
        }
        else if (Updater.WinqAvailable == null)
            ThemedDialog.Show(this, "WINQ-EMU is updated", "You now have WINQ-EMU " + now + ".", null, null, "OK");
        else
            ThemedDialog.Show(this, "WINQ-EMU wasn't updated", "It's still " + (now ?? had) + ". If the installer was closed early, try again.", null, null, "OK");
    }

    // Asks, downloads and verifies the update, then restarts into it (or waits until quit).
    async void StartUpdate()
    {
        if (busy || stagedUpdate != null) return;
        string version = Updater.Available;
        var choice = ThemedDialog.Choose(this, "Update to Try Linux " + version + "?",
            "You have version " + Version + ". Try Linux downloads the update from GitHub, checks it against its published checksum and restarts.",
            null, "Your Linux systems aren't affected.", "Update", "What's new");
        if (choice == DialogResult.No) { OpenUri(Updater.ReleasesPage + "/tag/v" + version); return; }
        if (choice != DialogResult.OK) return;

        var back = page;
        busy = true;
        ShowProgress("Downloading Try Linux " + version, false);
        SetProgress("Downloading Try Linux " + version, -1, "");
        string file = null, failed = null;
        UpdateInfo info = null;
        try
        {
            file = await Task.Run(() =>
            {
                info = Updater.Fetch();
                return Updater.Download(info, (have, total, rate) => BeginInvoke((Action)(() =>
                    SetProgress("Downloading Try Linux " + version, total > 0 ? (double)have / total : -1, FormatBytes(have)))));
            });
        }
        catch (Exception ex) { failed = ex.Message; }
        busy = false;
        if (failed != null)
        {
            ShowHome();
            if (ThemedDialog.Show(this, "Couldn't update Try Linux", failed, null, null, "Release page", "Close"))
                OpenUri(info != null ? info.Page : Updater.ReleasesPage);
            return;
        }

        if (running.Count > 0 && !ThemedDialog.Show(this, "Restart Try Linux now?",
                "The update is ready. Running systems keep running, but this copy's tray menu closes, so shut them down from inside Linux.",
                null, "Update when I quit installs it the next time Try Linux closes.", "Restart now", "Update when I quit"))
        {
            stagedUpdate = file;
            ShowHome();
            return;
        }
        try { Updater.Apply(file, version); }
        catch (Exception ex)
        {
            ShowHome();
            if (ThemedDialog.Show(this, "Couldn't update Try Linux",
                    "Try Linux can't replace itself in this folder (" + ex.Message + "). Download the new version from the release page instead.",
                    null, null, "Release page", "Close"))
                OpenUri(info.Page);
            return;
        }
        Process.Start(Application.ExecutablePath);
        quitting = true;
        Close();
    }

    string Describe(VmConfig vm)
    {
        int cores, ram;
        vm.Resources(out cores, out ram);
        var d = Distro.Find(vm.DistroId);
        string what = d != null ? d.Name : Path.GetFileName(vm.Iso);
        return string.Join("  ·  ", new[] { what, ram + " GB, " + cores + " cores" }.Where(s => !string.IsNullOrEmpty(s)));
    }

    // What's installed: the distribution and the version in its installer's file name, e.g.
    // "Fedora-Workstation-Live-45-1.4.x86_64.iso" -> "Fedora Workstation 45". A custom ISO shows its file name.
    static string OsLabel(VmConfig vm)
    {
        // What the guest agent reported the last time the system ran, when it has one.
        try
        {
            string reported = Path.Combine(vm.Dir, "guest-os.txt");
            if (File.Exists(reported))
            {
                string name = File.ReadAllText(reported).Trim();
                if (name.Length > 0) return name;
            }
        }
        catch { }
        string file = Path.GetFileNameWithoutExtension(vm.Iso ?? "");
        var d = Distro.Find(vm.DistroId);
        if (d == null) return file.Length > 0 ? file : vm.Iso == "" && !vm.PendingInstall ? "Imported disk" : "Custom ISO";
        var m = Regex.Match(Regex.Replace(file, @"x86_64|amd64|aarch64|arm64|64-?bit", ""), @"(?<![A-Za-z\d])(\d+(?:\.\d+)?)");
        return m.Success ? d.Name + " " + m.Groups[1].Value : d.Name;
    }

    // Renames a system (its display name; the folder and internal name stay). A desktop shortcut named
    // after it moves to the new name.
    void Rename(VmConfig cfg, string name)
    {
        name = (name ?? "").Trim();
        if (name.Length == 0 || name == cfg.DisplayName) return;
        string oldShortcut = ShortcutPath(cfg);
        bool hadShortcut = File.Exists(oldShortcut);
        cfg.DisplayName = name;
        cfg.Save();
        if (hadShortcut)
            try { File.Delete(oldShortcut); WriteShortcut(cfg); }
            catch (Exception ex) { Error("Renamed, but the desktop shortcut couldn't be renamed: " + ex.Message); }
    }

    // How much space the system's virtual disk file takes on this PC now (it grows as Linux uses space).
    static string DiskUsed(VmConfig vm)
    {
        try
        {
            long b = vm.AllDisks().Where(File.Exists).Sum(d => new FileInfo(d).Length);
            return b >= 1L << 30 ? (b / 1073741824.0).ToString("0.0") + " GB" : Math.Max(0, b >> 20) + " MB";
        }
        catch (IOException) { return "?"; }
    }

    // Powered on: running or paused, not a console opened while off.
    bool On(VmConfig vm) { RunningVm r; return running.TryGetValue(vm.Name, out r) && !r.Held; }

    // Shown on the right of the system's row on Your systems.
    string Status(VmConfig vm)
    {
        RunningVm r;
        if (running.TryGetValue(vm.Name, out r))
            return r.Held ? "Off, console open" : (r.Paused ? "Paused" : "Running") + (r.Hidden ? ", window closed" : "");
        return vm.PendingInstall ? "Ready to install" : "Powered off";
    }

    void SetCue(TextBox box, string text)
    {
        if (box.IsHandleCreated) SendMessage(box.Handle, 0x1501, (IntPtr)1, text);   // EM_SETCUEBANNER
        else box.HandleCreated += delegate { SendMessage(box.Handle, 0x1501, (IntPtr)1, text); };
    }

    void ShowChoose()
    {
        bool hasVms = VmConfig.All().Count > 0;
        // A search field, then two containers: the distributions ("Recommended", or the search results),
        // then your own installer (an ISO file on this PC or a URL link).
        // One selection across both: choosing in one clears the other.
        const int padIn = 6, rowH = 70;   // the same row height in both boxes; fits a two-line description
        int distroH = Distro.Catalog.Length * (rowH + 6) - 6, ownH = rowH;
        int box1 = distroH + padIn * 2, box2 = ownH + padIn * 2;
        int top = 232;
        var p = new Page(top + box1 + 12 + box2 + 20 + 42);
        p.Caption("Choose a Linux OS", 0, 104, 640, 30, Theme.Title, null, ContentAlignment.MiddleCenter);
        p.Caption("Try Linux downloads the official installer and checks it before use.", 0, 134, 640, 22, Theme.Small, Theme.Muted, ContentAlignment.MiddleCenter);

        var search = p.Input("", 0, 170, 640);
        searchBox = search;
        search.Height = Theme.D(28);
        SetCue(search, "Search distributions, e.g. KDE, Xfce or Ubuntu");
        var heading = p.Caption("Recommended", 0, 206, 640, 22, Theme.Heading, Theme.Text);

        var distroBox = p.Add(new BoxPanel(), 0, top, 640, box1);
        var list = new OptionList { RowHeight = rowH, BackColor = BoxPanel.Fill, FlatRows = true };
        // The list reaches across the box's right padding (to just inside its border) for the scroll bar.
        int listW = Theme.D(640) - Theme.D(padIn) - 1;
        list.SetBounds(Theme.D(padIn), Theme.D(padIn), listW, Theme.D(distroH));
        list.RightInset = listW - Theme.D(640 - padIn * 2);
        distroBox.Controls.Add(list);
        var hint = new Label { BackColor = BoxPanel.Fill, ForeColor = Theme.Muted, Font = Theme.Body, TextAlign = ContentAlignment.MiddleCenter, UseMnemonic = false };
        hint.SetBounds(Theme.D(padIn), Theme.D(padIn), Theme.D(640 - padIn * 2), Theme.D(distroH));
        distroBox.Controls.Add(hint);
        hint.BringToFront();

        // Recommended: the built-in distributions. A search covers those and the list from the repo
        // (MoreDistros), by name, description or desktop, names that start with the text first.
        Func<List<Distro>> all = () => Distro.Catalog.Concat(MoreDistros.Cached()).ToList();
        Action refresh = () =>
        {
            string q = search.Text.Trim();
            var words = q.ToLowerInvariant().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var shown = words.Length == 0 ? Distro.Catalog.ToList()
                : all().Where(d => words.All(w => (d.Name + " " + d.Blurb + " " + d.Id).ToLowerInvariant().Contains(w)))
                    .OrderByDescending(d => d.Name.StartsWith(q, StringComparison.OrdinalIgnoreCase)).ToList();
            list.BeginUpdate();
            list.Clear();
            foreach (var d in shown)
                list.AddItem(d.Name, d.Blurb, d).Link = d.Home;
            list.EndUpdate();
            if (shown.Count > 0) list.SelectedIndex = 0;
            heading.Text = words.Length == 0 ? "Recommended" : shown.Count + (shown.Count == 1 ? " result" : " results");
            hint.Visible = shown.Count == 0;
            hint.Text = "No distribution matches \"" + q + "\".\nTry another name, or use your own installer below.";
        };
        search.TextChanged += delegate { refresh(); };
        refresh();
        // The list is checked against GitHub in the background each time this page opens; results update when a new one arrives.
        Task.Run(() => MoreDistros.Load(false)).ContinueWith(t => Ui(() => { if (page == p && !t.IsFaulted && !search.IsDisposed && search.Text.Trim().Length > 0) refresh(); }));

        var ownBox = p.Add(new BoxPanel(), 0, top + box1 + 12, 640, box2);
        // One option; Next asks whether the installer is an ISO file on this PC or a URL link.
        var own = new OptionList { RowHeight = rowH, BackColor = BoxPanel.Fill, FlatRows = true };
        own.SetBounds(Theme.D(padIn), Theme.D(padIn), Theme.D(640 - padIn * 2), Theme.D(ownH));
        ownBox.Controls.Add(own);
        own.AddItem("Use Your Own Installer", "An existing virtual disk to import, an .iso file on this PC, or a URL link to one", "own");

        bool syncing = false;
        list.SelectionChanged += delegate { if (!syncing && list.SelectedIndex >= 0) { syncing = true; own.SelectedIndex = -1; syncing = false; } };
        own.SelectionChanged += delegate { if (!syncing && own.SelectedIndex >= 0) { syncing = true; list.SelectedIndex = -1; syncing = false; } };
        list.SelectedIndex = 0;

        int y = top + box1 + 12 + box2 + 20;
        FlatBtn back = null;
        if (hasVms)
        {
            back = p.Add(new FlatBtn("Back"), 0, y, 120, 42);
            back.Click += delegate { ShowHome(); };
        }
        var next = p.Add(new FlatBtn("Next", true), 500, y, 140, 42);
        p.Nav(back, next);
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
        EventHandler go = delegate
        {
            var tag = list.SelectedTag ?? own.SelectedTag;
            if ((tag as string) == "own") { ShowOwnInstaller(); return; }
            // A distribution Try Linux can't download itself: offer its website, then Use Your Own Installer
            // for the ISO once it's downloaded.
            var web = tag as Distro;
            if (web != null && web.WebsiteOnly)
            {
                if (Ask("Download " + web.Name + " from its website?",
                        "Try Linux can't fetch the " + web.Name + " ISO file itself. Open its website to download the ISO, then choose it in Use Your Own Installer (An ISO File On This PC) to install it.",
                        "Open Website"))
                {
                    try { Process.Start(new ProcessStartInfo(web.Home) { UseShellExecute = true }); } catch { }
                    ShowOwnInstaller();
                }
                return;
            }
            if (tag != null) choose(tag);
        };
        next.Click += go;
        list.Activated += go;
        own.Activated += go;
        AcceptButton = next;
        SetPage(p);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, string l);

    // ------------------------------------------------------------ create: (Use Your Own Installer,) General, Resources, Summary

    const string DiskFilter = "Virtual disks (*.qcow2;*.vmdk;*.vhd;*.vhdx;*.vdi;*.img;*.raw)|*.qcow2;*.vmdk;*.vhd;*.vhdx;*.vdi;*.img;*.raw|All files|*.*";
    const int CreateH = 600, CreateButtonsY = 500;

    // A Create page with its title (Use Your Own Installer, General, Resources or Summary).
    Page CreatePage(string title)
    {
        var p = new Page(CreateH);
        p.Caption(title, 0, 104, 640, 30, Theme.Title, null, ContentAlignment.MiddleCenter);
        return p;
    }

    // "OR", centred on a divider line across the column.
    Panel OrDivider(Page p, int y)
    {
        var or = p.Add(new Panel { BackColor = Theme.Bg }, 0, y, 640, 20);
        or.Paint += (s, e) =>
        {
            var g = e.Graphics;
            var size = TextRenderer.MeasureText(g, "OR", Theme.Small, Size.Empty, TextFormatFlags.NoPadding);
            int mid = or.Height / 2, gap = Theme.D(10), left = (or.Width - size.Width) / 2;
            using (var pen = new Pen(Theme.Border))
            {
                g.DrawLine(pen, 0, mid, left - gap, mid);
                g.DrawLine(pen, left + size.Width + gap, mid, or.Width, mid);
            }
            TextRenderer.DrawText(g, "OR", Theme.Small, new Rectangle(0, 0, or.Width, or.Height), Theme.Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        };
        return or;
    }

    // Use Your Own Installer: an existing virtual disk to import, an ISO file on this PC, or a URL link.
    // Choosing one clears the others. `last` is what was chosen before, when coming Back from General.
    void ShowOwnInstaller(InstallPlan last = null)
    {
        string iso = last != null ? last.LocalIso : null, disk = last != null ? last.Disk : null;
        DiskSource source = last != null ? last.Source : null;
        var p = CreatePage("Use Your Own Installer");
        // Both buttons as wide as the longer label needs, so they line up without much space around the text.
        int bw = Math.Max(Sheet.SnugWidth("Choose An ISO File"), Sheet.SnugWidth("Choose A Disk File"));

        // A disk that already has Linux installed (from Try Linux, QEMU, VirtualBox, VMware or Hyper-V).
        p.Caption("An Existing Virtual Disk", 0, 172, 640, 22, Theme.Heading, Theme.Text);
        var pickDisk = p.Add(new FlatBtn("Choose A Disk File"), 0, 198, bw, 32);
        var chosenDisk = p.Caption("", bw + 14, 198, 626 - bw, 32);
        var diskNote = p.Caption("", 0, 234, 640, 22, Theme.Small, Theme.Muted);
        OrDivider(p, 264);

        p.Caption("An ISO File On This PC", 0, 292, 640, 22, Theme.Heading, Theme.Text);
        var pick = p.Add(new FlatBtn("Choose An ISO File"), 0, 318, bw, 32);
        var chosen = p.Caption("", bw + 14, 318, 626 - bw, 32);
        OrDivider(p, 362);

        p.Caption("URL Link", 0, 390, 640, 22, Theme.Heading, Theme.Text);
        var url = p.Input(last != null && last.Url != null ? last.Url : "", 0, 416, 640);
        url.Height = Theme.D(28);
        SetCue(url, "https://... (address of an .iso file)");
        var urlNote = p.Caption("Paste the address of an .iso file.", 0, 446, 640, 20, Theme.Small, Theme.Muted);

        var back = p.Add(new FlatBtn("Back"), 0, CreateButtonsY, 120, 42);
        back.Click += delegate { ShowChoose(); };
        var next = p.Add(new FlatBtn("Next", true), 500, CreateButtonsY, 140, 42);
        p.Nav(back, next);

        Action refresh = () =>
        {
            next.Enabled = iso != null || disk != null || url.Text.Trim().Length > 0;
            chosen.Text = iso != null ? Path.GetFileName(iso) : "No file chosen";
            chosen.ForeColor = iso != null ? Theme.Highlight : Theme.Muted;
            chosenDisk.Text = disk != null ? Path.GetFileName(disk) : "No disk chosen";
            chosenDisk.ForeColor = disk != null ? Theme.Highlight : Theme.Muted;
            diskNote.Text = disk != null && source != null ? source.Summary()
                : "qcow2, vmdk, vhd, vhdx, vdi or raw. It's copied; the original isn't changed.";
        };
        pick.Click += delegate
        {
            using (var dlg = new OpenFileDialog { Filter = "Disk images (*.iso)|*.iso|All files|*.*", Title = "Choose a Linux installer" })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                iso = dlg.FileName;
                disk = null; source = null;
                url.Text = "";
                refresh();
            }
        };
        pickDisk.Click += delegate
        {
            using (var dlg = new OpenFileDialog { Filter = DiskFilter, Title = "Choose a virtual disk to import" })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                disk = dlg.FileName;
                iso = null;
                url.Text = "";
                // Its format and size, and any settings saved beside it, fill in the next steps.
                Cursor = Cursors.WaitCursor;
                try { source = DiskSource.Read(disk); } finally { Cursor = Cursors.Default; }
                refresh();
            }
        };
        url.TextChanged += delegate
        {
            if (url.Text.Length > 0) { iso = null; disk = null; source = null; }
            urlNote.Text = "Paste the address of an .iso file.";
            urlNote.ForeColor = Theme.Muted;
            refresh();
        };
        next.Click += delegate
        {
            var plan = new InstallPlan { Own = true };
            if (iso != null) { plan.LocalIso = iso; plan.Label = Path.GetFileNameWithoutExtension(iso); }
            else if (disk != null) { plan.Disk = disk; plan.Source = source; plan.Label = Path.GetFileNameWithoutExtension(disk); }
            else
            {
                Uri u;
                if (!Uri.TryCreate(url.Text.Trim(), UriKind.Absolute, out u) || (u.Scheme != "https" && u.Scheme != "http"))
                {
                    urlNote.Text = "That isn't a web address. It should start with https://";
                    urlNote.ForeColor = Theme.Danger;
                    return;
                }
                plan.Url = u.ToString();
                plan.Label = Path.GetFileNameWithoutExtension(u.AbsolutePath);
            }
            // Coming back to the same choice keeps what was entered on the next steps.
            if (lastDraft != null && SamePlan(lastDraft.Plan, plan)) { lastDraft.Plan = plan; ShowCreateGeneral(lastDraft); }
            else ShowOptions(plan);
        };
        refresh();
        AcceptButton = next;
        SetPage(p);
    }

    CreateDraft lastDraft;   // the Create steps' answers, kept when going Back to Use Your Own Installer
    static bool SamePlan(InstallPlan a, InstallPlan b)
    {
        return a != null && b != null && a.LocalIso == b.LocalIso && a.Url == b.Url && a.Disk == b.Disk && a.Distro == b.Distro;
    }

    void ShowOptions(InstallPlan plan)
    {
        var existing = VmConfig.All();
        var d = new CreateDraft { Plan = plan, Location = AppSettings.VmsDir };
        d.Cfg = new VmConfig { Profile = 0, RamGB = 8, Cores = Math.Max(2, Environment.ProcessorCount / 2) };
        d.Cfg.SshPort = existing.Count == 0 ? 2223 : Math.Max(2222, existing.Max(v => v.SshHostPort)) + 1;
        // A disk's own settings (beside it, from Try Linux, VMware or VirtualBox) fill in what they can.
        var src = plan.Source;
        string seed = src != null && src.Name.Length > 0 ? src.Name : plan.Distro != null ? plan.Distro.Name : plan.Label;
        if (src != null)
        {
            d.Description = src.Description;
            if (src.RamGB > 0 || src.Cores > 0)
            {
                d.Cfg.Profile = 2;   // Manual, starting from the disk's own memory and cores
                if (src.RamGB > 0) d.Cfg.RamGB = Math.Max(1, Math.Min(Sys.MaxRamGB, src.RamGB));
                if (src.Cores > 0) d.Cfg.Cores = Math.Max(1, Math.Min(Sys.MaxCores, src.Cores));
            }
        }
        // The name shown on Virtual Machines; the folder name is made from it.
        d.Name = seed;
        for (int i = 2; existing.Any(v => string.Equals(v.DisplayName, d.Name, StringComparison.CurrentCultureIgnoreCase)); i++) d.Name = seed + " " + i;
        d.DiskGB = plan.Distro != null ? plan.Distro.DiskGB : 64;
        lastDraft = d;
        ShowCreateGeneral(d);
    }

    void ShowCreateGeneral(CreateDraft d)
    {
        var p = CreatePage("General");
        p.Caption("Name", 0, 180, 180, 26);
        var name = p.Input(d.Name, 190, 180, 450);

        p.Caption("Description", 0, 218, 180, 26);
        var notes = p.Notes(d.Description.Replace("\r\n", "\n").Replace("\n", "\r\n"), 190, 218, 450, 76);
        // Lined up with the text inside the box above it.
        p.Caption("(Optional): Shown in Settings > General > Info.", 193, 298, 447, 22, Theme.Small, Theme.Muted);

        // The default is the storage folder; a different one is used only while the box is ticked.
        p.Caption("Location", 0, 336, 180, 26);
        var location = p.Input(d.OwnLocation ? d.Location : AppSettings.VmsDir, 190, 336, 330);
        var browse = p.Add(new FlatBtn("Browse"), 530, 334, 110, 30);
        var own = p.Check("Choose a different location", d.OwnLocation, 190, 370, 450);
        var where = p.Caption("", 190, 400, 450, 22, Theme.Small, Theme.Muted);
        Action lockLocation = () =>
        {
            bool on = own.Checked;
            location.ReadOnly = !on;
            location.TabStop = on;
            location.BackColor = on ? Theme.Card : Theme.Bg;
            location.ForeColor = on ? Theme.Text : Theme.Muted;
            browse.Enabled = on;
            if (!on) location.Text = AppSettings.VmsDir;
            where.Text = on ? "Try Linux makes a folder for this system inside it." : "The default: where Try Linux keeps its systems.";
        };
        own.CheckedChanged += delegate { lockLocation(); if (own.Checked) location.Focus(); };
        browse.Click += delegate
        {
            using (var dlg = new FolderBrowserDialog { Description = "Where should Try Linux keep this system?", ShowNewFolderButton = true })
            {
                if (Directory.Exists(location.Text.Trim())) dlg.SelectedPath = location.Text.Trim();
                if (dlg.ShowDialog(this) == DialogResult.OK) location.Text = dlg.SelectedPath;
            }
        };
        lockLocation();

        Action keep = () =>
        {
            d.Name = name.Text.Trim();
            d.Description = notes.Text.Trim();
            d.OwnLocation = own.Checked;
            d.Location = own.Checked ? location.Text.Trim() : AppSettings.VmsDir;
        };
        var back = p.Add(new FlatBtn("Back"), 0, CreateButtonsY, 120, 42);
        back.Click += delegate { keep(); if (d.Plan.Own) ShowOwnInstaller(d.Plan); else ShowChoose(); };
        var next = p.Add(new FlatBtn("Next", true), 500, CreateButtonsY, 140, 42);
        p.Nav(back, next);
        next.Click += delegate
        {
            keep();
            if (d.Name.Length == 0 || d.Name.Length > 60) { Error("Give the system a name of up to 60 characters."); return; }
            if (VmConfig.All().Any(v => string.Equals(v.DisplayName, d.Name, StringComparison.CurrentCultureIgnoreCase)))
            { Error("You already have a system named \"" + d.Name + "\". Choose another name."); return; }
            if (d.OwnLocation)
            {
                if (d.Location.Length < 3 || !Path.IsPathRooted(d.Location)) { Error("Choose a full folder path for the location, or untick \"Choose a different location\"."); return; }
                if (d.Location.Contains(",")) { Error("QEMU can't use a folder whose path contains a comma. Choose another location."); return; }
            }
            ShowCreateResources(d);
        };
        AcceptButton = next;
        SetPage(p);
        name.Focus();
        name.SelectionStart = name.Text.Length;   // the cursor at the end; a selection would show in Windows' blue
    }

    void ShowCreateResources(CreateDraft d)
    {
        var p = CreatePage("Resources");
        var res = new ResourcePicker();
        res.Attach(p, 180, d.Cfg);
        SizeBox disk = null;
        CheckBox full = null;
        if (d.Plan.Disk == null)
        {
            p.Caption("Disk size (GiB)", 0, 300, 180, 26);
            disk = p.Add(Sys.DiskBox(16, 2048, d.DiskGB), 190, 300, 100, 26);
            // Like Memory and CPU cores: how much the location's drive has free for the disk to grow into.
            p.Caption(FreeOn(d.Location), 300, 300, 340, 26, Theme.Small, Theme.Muted);
            full = p.Check("Allocate the full size now", d.Cfg.DiskFull, 190, 334, 240);
            // The size is fixed while it's to be allocated in full; untick to change it.
            full.CheckedChanged += delegate { disk.Commit(); disk.Locked = full.Checked; };
            disk.Locked = full.Checked;
        }
        else
        {
            var src = d.Plan.Source;
            p.Caption("Disk", 0, 300, 180, 26);
            p.Caption(Path.GetFileName(d.Plan.Disk) + (src != null && src.Virtual > 0 ? "  ·  " + GibText(src.Virtual) + " GiB" : "  ·  " + FileSize(d.Plan.Disk)), 190, 300, 450, 26, Theme.Body, Theme.Text);
            p.Caption("Keeps its own size  ·  " + FreeOn(d.Location), 190, 326, 450, 22, Theme.Small, Theme.Muted);
        }

        Action keep = () =>
        {
            res.Store(d.Cfg);
            if (disk != null) { d.DiskGB = (int)disk.Value; d.Cfg.DiskFull = full.Checked; }
        };
        var back = p.Add(new FlatBtn("Back"), 0, CreateButtonsY, 120, 42);
        back.Click += delegate { keep(); ShowCreateGeneral(d); };
        var next = p.Add(new FlatBtn("Next", true), 500, CreateButtonsY, 140, 42);
        p.Nav(back, next);
        next.Click += delegate
        {
            keep();
            if (d.Cfg.DiskFull && d.Plan.Disk == null)
            {
                long free = FreeBytes(d.Location);
                if (free >= 0 && free < ((long)d.DiskGB << 30) + (1L << 30))
                { Error("The drive doesn't have " + d.DiskGB + " GiB free to allocate now. Choose a smaller size, or untick \"Allocate the full size now\"."); return; }
            }
            ShowCreateSummary(d);
        };
        AcceptButton = next;
        SetPage(p);
    }

    void ShowCreateSummary(CreateDraft d)
    {
        var p = CreatePage("Summary");
        var plan = d.Plan;
        int cores, ram;
        d.Cfg.Resources(out cores, out ram);
        string id = UniqueName(d.Name, d.Location);
        string source = plan.Disk != null ? "Import " + plan.Disk
            : plan.LocalIso != null ? plan.LocalIso
            : plan.Url != null ? "Download " + plan.Url
            : "Download from " + new Uri(plan.Distro.Base).Host + ", checksum verified";
        string firstLine = d.Description.Split('\n')[0].Trim();
        Func<bool, string> how = on => on ? " (Preallocated)" : " (Dynamic)";
        string diskSize = plan.Disk == null ? d.DiskGB + " GiB" + how(d.Cfg.DiskFull)
            : (plan.Source != null && plan.Source.Virtual > 0 ? GibText(plan.Source.Virtual) + " GiB" : "As imported") + how(false);
        var rows = new List<Tuple<string, string>> {
            Tuple.Create("Name", d.Name),
            Tuple.Create("Description", d.Description.Length == 0 ? "None" : firstLine + (d.Description.Contains("\n") ? " ..." : "")),
            Tuple.Create("Operating system", plan.Distro != null ? plan.Distro.Name : plan.Disk != null ? "From the imported disk" : plan.Label),
            Tuple.Create(plan.Disk != null ? "Disk" : "Installer", source),
            Tuple.Create("Location", Path.Combine(d.Location, id)),
            Tuple.Create("Resource profile", VmConfig.ProfileNames[Math.Max(0, Math.Min(2, d.Cfg.Profile))]),
            Tuple.Create("Memory", ram + " GiB"),
            Tuple.Create("CPU cores", cores.ToString()),
            Tuple.Create("Disk size", diskSize),
        };
        // The details in a bordered box, like the lists on Choose a Linux OS.
        const int rowH = 28, pad = 10;
        var details = p.Add(new BoxPanel(), 0, 162, 640, rows.Count * rowH + pad * 2);
        int y = pad;
        foreach (var r in rows)
        {
            foreach (var cell in new[] {
                new Label { Text = r.Item1, ForeColor = Theme.Muted },
                new Label { Text = r.Item2, ForeColor = Theme.Text } })
            {
                cell.Font = Theme.Body; cell.BackColor = BoxPanel.Fill; cell.TextAlign = ContentAlignment.MiddleLeft;
                cell.AutoEllipsis = true; cell.UseMnemonic = false;
                bool label = cell.ForeColor == Theme.Muted;
                cell.SetBounds(Theme.D(label ? 16 : 196), Theme.D(y), Theme.D(label ? 170 : 428), Theme.D(rowH - 2));
                details.Controls.Add(cell);
            }
            y += rowH;
        }

        var back = p.Add(new FlatBtn("Back"), 0, CreateButtonsY, 120, 42);
        back.Click += delegate { ShowCreateResources(d); };
        var go = p.Add(new FlatBtn(plan.Disk != null ? "Import" : "Install", true), 500, CreateButtonsY, 140, 42);
        p.Nav(back, go);
        go.Click += delegate
        {
            var cfg = d.Cfg;
            // The folder is made from the name now, so it's unique where it's created.
            cfg.Name = UniqueName(d.Name, d.Location);
            cfg.Dir = Path.Combine(d.Location, cfg.Name);
            cfg.DisplayName = d.Name;
            cfg.Description = d.Description;
            cfg.DistroId = plan.Distro != null ? plan.Distro.Id : "";
            if (plan.Disk != null) { cfg.DiskFull = false; Import(plan, cfg); } else Install(plan, cfg, d.DiskGB);
        };
        AcceptButton = go;
        SetPage(p);
    }

    static string GibText(long bytes) { double g = bytes / 1073741824.0; return g == Math.Floor(g) ? g.ToString("0") : g.ToString("0.0"); }

    static long FreeBytes(string folder)
    {
        try { return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(folder))).AvailableFreeSpace; } catch { return -1; }
    }

    static string FreeOn(string folder)
    {
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(folder)));
            return "up to " + drive.AvailableFreeSpace / 1073741824 + " GiB free on drive " + drive.Name.TrimEnd('\\');
        }
        catch { return ""; }
    }

    static string FileSize(string file)
    {
        try { return FormatBytes(new FileInfo(file).Length); } catch { return "?"; }
    }

    // A system made outside the storage folder is remembered, so Virtual Machines finds it.
    static void Remember(VmConfig cfg)
    {
        if (string.Equals(Path.GetFullPath(Path.GetDirectoryName(cfg.Dir)), Path.GetFullPath(AppSettings.VmsDir), StringComparison.OrdinalIgnoreCase)) return;
        if (AppSettings.VmDirs.Any(x => string.Equals(x, cfg.Dir, StringComparison.OrdinalIgnoreCase))) return;
        AppSettings.VmDirs.Add(cfg.Dir);
        AppSettings.Save();
    }

    // The folder and internal name for a new system: made from its name, unique among your systems and
    // in the folder it goes in (`parent`, the storage folder unless the system has a location of its own).
    string UniqueName(string seed, string parent = null)
    {
        parent = parent ?? AppSettings.VmsDir;
        string baseName = Regex.Replace(seed.ToLowerInvariant(), "[^a-z0-9_-]+", "-").Trim('-');
        if (baseName.Length == 0) baseName = "linux";
        if (baseName.Length > 32) baseName = baseName.Substring(0, 32);
        var taken = new HashSet<string>(VmConfig.All().Select(v => v.Name), StringComparer.OrdinalIgnoreCase);
        string n = baseName;
        for (int i = 2; taken.Contains(n) || File.Exists(Path.Combine(parent, n, n + ".qcow2")); i++) n = baseName + "-" + i;
        return n;
    }

    void ShowProgress(string status, bool cancellable)
    {
        var p = new Page(380);
        pStatus = p.Caption(status, 0, 124, 640, 28, Theme.Body, Theme.Text, ContentAlignment.MiddleCenter);
        pBar = p.Add(new Bar(), 120, 164, 400, 10);
        pDetail = p.Caption("", 0, 184, 640, 22, Theme.Small, Theme.Muted, ContentAlignment.MiddleCenter);
        pTip = p.Caption("", 40, 228, 560, 44, Theme.Small, Theme.Highlight, ContentAlignment.TopCenter);
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
        return Ask("Stop setting up?", "Anything already downloaded is kept, so next time it picks up where it left off.", "Stop", "Keep going");
    }

    // ------------------------------------------------------------ install

    async void Install(InstallPlan plan, VmConfig cfg, int diskGB)
    {
        var virt = Virt.Check();
        if (virt == VirtState.NoCpuSupport) { Error(Virt.Message(virt)); return; }
        if (virt == VirtState.FirmwareOff)
        {
            // Setup can still download now, so it's ready once the BIOS setting is changed.
            if (!Ask("Download " + cfg.DisplayName + " anyway?", Virt.Message(virt) +
                    "\n\nTry Linux can download and prepare it now, so it's ready after you change the setting.", "Download", "Cancel")) return;
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
                        bool sha512 = r.Sha256.Length == 128;
                        string against = "Comparing with the official " + (sha512 ? "SHA-512" : "SHA-256") + " checksum";
                        SetProgress("Verifying the download", 0, against);
                        string hash = await Task.Run(() => Net.Hash(part, sha512, cancelled,
                            f => Ui(() => SetProgress("Verifying the download", f, against))));
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

            SetProgress("Creating the virtual disk", -1, diskGB + " GiB" + (cfg.DiskFull ? ", allocating its full size" : ""));
            Directory.CreateDirectory(cfg.Dir);
            // Allocate the full size now: qcow2 metadata for every cluster, then the file stops being sparse,
            // so the whole size is taken on the drive (WINQ-EMU's qemu-img can't do full preallocation on Windows).
            await Task.Run(() =>
            {
                Winq.Run(Winq.Img, "create -f qcow2 " + (cfg.DiskFull ? "-o preallocation=metadata " : "") + Q(cfg.Disk) + " " + diskGB + "G");
                if (cfg.DiskFull) Native.ClearSparse(cfg.Disk);
            });
            cfg.Iso = iso;
            cfg.PendingInstall = true;
            cfg.Save();
            Remember(cfg);
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

    // Use Your Own Installer > An Existing Virtual Disk: copies the disk into a new system's qcow2 (any
    // format qemu-img reads), then starts it. Nothing is installed; the original file isn't changed.
    async void Import(InstallPlan plan, VmConfig cfg)
    {
        var virt = Virt.Check();
        if (virt == VirtState.NoCpuSupport) { Error(Virt.Message(virt)); return; }
        else if (virt == VirtState.FeatureOff && !restartPending && !await EnableHypervisor()) return;
        else if (virt == VirtState.NotRunning) restartPending = true;

        busy = true;
        cancelRequested = false;
        ShowProgress("Getting ready", true);
        Func<bool> cancelled = () => cancelRequested;
        try
        {
            if (!Winq.Ready) await InstallWinq(cancelled);
            if (cancelRequested) throw new OperationCanceledException();
            SetProgress("Importing " + Path.GetFileName(plan.Disk), -1, FileSize(plan.Disk) + ", copied into a new disk for " + cfg.DisplayName);
            Directory.CreateDirectory(cfg.Dir);
            try { await Task.Run(() => Winq.Run(Winq.Img, "convert -O qcow2 " + Q(plan.Disk) + " " + Q(cfg.Disk))); }
            catch (Exception ex)
            {
                try { if (File.Exists(cfg.Disk)) File.Delete(cfg.Disk); Directory.Delete(cfg.Dir); } catch { }
                throw new Exception("Couldn't import " + Path.GetFileName(plan.Disk) + ". qemu-img said:\n\n" + ex.Message);
            }
            cfg.Iso = "";
            cfg.PendingInstall = false;
            cfg.Save();
            Remember(cfg);
            busy = false;
            if (Virt.Check() != VirtState.Ready)
            {
                ShowHome();
                Info(cfg.DisplayName + " is imported. Start it once Linux can run on this PC.\n\n" + Virt.Message(Virt.Check()));
                return;
            }
            Boot(cfg);
        }
        catch (OperationCanceledException) { busy = false; ShowHome(); }
        catch (Exception ex) { busy = false; ShowHome(); Error(ex.Message); }
    }

    async Task<bool> EnableHypervisor()
    {
        if (!Ask("Turn on Windows Hypervisor Platform?", "Try Linux needs it, and it's turned off. Windows asks for permission, then needs one restart. Setup can keep downloading in the meantime.",
                "Turn on", "Not now")) return false;
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

    async Task InstallWinq(Func<bool> cancelled, bool update = false)
    {
        if (!update && !Ask("Install WINQ-EMU?", "Try Linux runs systems with WINQ-EMU, a free QEMU build with GPU acceleration for Windows. It isn't installed yet. Download and install it now (about 55 MB)?",
                "Install", "Cancel"))
            throw new OperationCanceledException();
        SetProgress("Downloading WINQ-EMU", -1, "github.com/cmspam/winq-emu");
        string url = await Task.Run(() =>
        {
            string json = Net.GetString(Updater.WinqApi);
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

    // `restart` starts a system again after Linux restarted it: no progress page or tray notice.
    // `held` opens its window with the machine still powered off (QEMU -S); Start powers it on.
    void Boot(VmConfig cfg, bool cpu, bool restart = false, bool held = false)
    {
        RunningVm existing;
        if (running.TryGetValue(cfg.Name, out existing)) { ShowVm(existing); return; }
        if (!Winq.Ready)
        {
            Error("WINQ-EMU isn't installed. Click Create to install it, or get it from github.com/cmspam/winq-emu/releases.");
            return;
        }
        var virt = Virt.Check();
        if (virt != VirtState.Ready) { Error(cfg.DisplayName + " can't start yet.\n\n" + Virt.Message(virt)); return; }
        bool installer = cfg.PendingInstall && File.Exists(cfg.Iso);
        if (!restart && !held)
        {
            ShowProgress("Starting " + cfg.DisplayName, false);
            SetProgress("Starting " + cfg.DisplayName, -1,
                string.Join("  ·  ", new[] { installer ? "Booting the installer" : "", cpu ? "CPU rendering" : "" }.Where(s => s.Length > 0)));
        }
        GrowDisk(cfg);
        var vm = Launch(cfg, installer, cpu, held);
        if (vm == null) { ShowHome(); return; }
        if (held) ShowHome();

        var poll = new Timer { Interval = 250 };
        poll.Tick += delegate
        {
            vm.P.Refresh();
            if (vm.P.HasExited) { poll.Dispose(); return; }
            if (vm.P.MainWindowHandle != IntPtr.Zero || (DateTime.Now - vm.Started).TotalSeconds > 60)
            {
                poll.Dispose();
                IntPtr h = vm.P.MainWindowHandle;
                vm.Window = h;
                Native.BringToFront(h);
                if (held) return;   // still off: Try Linux stays open with Start
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
                if (restart) return;
                if (trayAfterStart)
                {
                    trayAfterStart = false;
                    HideToTray(cfg.DisplayName + " is running", "Right-click the Try Linux icon here to shut it down or open settings.");
                }
                ShowHome();
                Native.BringToFront(h);   // the system's window stays in front of Try Linux
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

    RunningVm Launch(VmConfig cfg, bool installer, bool cpu, bool held = false)
    {
        int cores, ram;
        cfg.Resources(out cores, out ram);
        var vm = new RunningVm { Cfg = cfg, Installer = installer, Cpu = cpu, Held = held, QmpPort = Net.FreePort() };
        do vm.EventPort = Net.FreePort(); while (vm.EventPort == vm.QmpPort);
        do vm.AgentPort = Net.FreePort(); while (vm.AgentPort == vm.QmpPort || vm.AgentPort == vm.EventPort);
        string outputs = cfg.Displays > 1 ? ",max_outputs=" + cfg.Displays : "";
        string gpu = cpu ? "virtio-vga" + outputs
            : (cfg.Venus ? "virtio-vga-gl,blob=on,hostmem=" + Math.Max(1, Math.Min(Sys.MaxGpuMemGB, cfg.GpuMemGB)) + "G,venus=on" : "virtio-vga-gl") + outputs;
        var a = new List<string> {
            "-machine q35,accel=whpx", "-cpu host",
            "-smp " + cores, "-m " + ram + "G",
            // window-close=off: closing the window doesn't quit QEMU; Try Linux hides it instead.
            "-display " + (cpu ? "sdl" : "sdl,gl=on") + ",window-close=off",
            "-device " + gpu,
            cfg.NetType == 2 ? "-nic none" : "-device virtio-net-pci,netdev=net0",
            "-usb", "-device usb-tablet",
            "-name " + Q(cfg.DisplayName),
            "-qmp tcp:127.0.0.1:" + vm.QmpPort + ",server=on,wait=off",
            "-qmp tcp:127.0.0.1:" + vm.EventPort + ",server=on,wait=off",
            // A channel for the QEMU guest agent, if Linux has one, which tells Try Linux the installed OS.
            "-device virtio-serial",
            "-chardev socket,id=qga0,host=127.0.0.1,port=" + vm.AgentPort + ",server=on,wait=off",
            "-device virtserialport,chardev=qga0,name=org.qemu.guest_agent.0",
            // A memory balloon, only for its statistics: Linux's own driver (virtio_balloon, in every kernel)
            // reports how much memory Linux is using, shown on Virtual Machines. Nothing is taken back.
            "-device virtio-balloon-pci,id=balloon0",
            // Restarting Linux ends QEMU (with a "guest-reset" SHUTDOWN event) and Try Linux starts it
            // again. Resetting in place could leave QEMU's GPU state behind and stop it from coming back.
            "-action reboot=shutdown"
        };
        // The boot disk comes first, so it's the one the BIOS starts from; a missing extra disk is left out.
        int at = 6;   // after the graphics device, where the boot disk used to be listed
        foreach (var d in cfg.AllDisks())
            if (d == cfg.Disk || File.Exists(d))
                a.Insert(at++, "-drive " + Q("file=" + d + ",format=qcow2,if=virtio" + (cfg.Reclaim ? ",discard=unmap,detect-zeroes=unmap" : "")));
        // restrict=on keeps the system off this PC and the internet, but port forwards still reach it.
        if (cfg.NetType != 2)
            a.Add("-netdev user,id=net0" + (cfg.NetType == 1 ? ",restrict=on" : "") + string.Concat(cfg.ForwardRules().Select(f => "," + f.ToHostFwd())));
        // A CD/DVD drive whose disc can be changed while the system runs (QMP id "cd"). The hard disk still boots first.
        if (!installer)
        {
            a.Add("-drive " + Q("if=none,id=cd0,media=cdrom,readonly=on" + (cfg.Cd.Length > 0 && File.Exists(cfg.Cd) ? ",format=raw,file=" + cfg.Cd : "")));
            a.Add("-device ide-cd,drive=cd0,id=cd");
        }
        if (cfg.Sound) { a.Add("-device intel-hda"); a.Add("-device " + (cfg.Mic ? "hda-duplex" : "hda-output")); }
        if (cfg.Fullscreen && FullscreenScreen(cfg) == null && !held) a.Add("-full-screen");
        if (held) a.Add("-S");   // window open, machine powered off until "cont"
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
            vm.WatchEvents();
            vm.WatchAgent(() => Ui(RefreshHome));
        }
        catch (Exception ex) { Error("Couldn't start QEMU: " + ex.Message); return null; }
        running[cfg.Name] = vm;
        WatchCloseButtons();
        return vm;
    }

    void OnVmExited(RunningVm vm)
    {
        running.Remove(vm.Cfg.Name);
        if (running.Count == 0) StopWatchingCloseButtons();
        double secs = (DateTime.Now - vm.Started).TotalSeconds;
        // An installer session that ran a while has most likely installed the system.
        if (vm.Installer && secs > 60 && !vm.Held)
        {
            var fresh = VmConfig.Load(vm.Cfg.Dir);
            fresh.PendingInstall = false;
            if (File.Exists(fresh.Disk)) fresh.Save();
        }
        // Linux restarted, or Restart / Reset from the power menu: start it again, with any changed
        // settings. (The SHUTDOWN event can arrive just after the exit.)
        if (vm.Watcher != null) vm.Watcher.Join(1500);
        bool restartAsked = vm.ShutdownReason == "guest-shutdown" && (DateTime.Now - vm.RestartAsked).TotalMinutes < 5;
        if ((vm.ShutdownReason == "guest-reset" || restartAsked || vm.ResetAsked) && !quitting)
        {
            var again = VmConfig.Load(vm.Cfg.Dir);
            if (File.Exists(again.Disk)) { Boot(again, vm.Cpu, true); return; }
        }
        if (vm.ForceStopped) { if (!busy && Visible) ShowHome(); return; }
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
                Boot(VmConfig.Load(vm.Cfg.Dir), true, false, vm.Held);
                return;
            }
            if (portClash) msg += "\n\nA forwarded port is already in use. Change the port forwards in Settings > Devices.";
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
        if (vm.Window == IntPtr.Zero) vm.Window = vm.P.MainWindowHandle;
        Native.ShowAndFocus(vm.Window);
        if (vm.Hidden) { vm.Hidden = false; if (Visible && !busy) ShowHome(); }
    }

    // QEMU ignores its window's close button (window-close=off), so watch for clicks on it and hide
    // the window instead: the system keeps running and Your systems shows it.
    IntPtr mouseHook;
    Native.HookProc mouseProc;

    void WatchCloseButtons()
    {
        if (mouseHook != IntPtr.Zero) return;
        mouseProc = (code, w, l) =>
        {
            if (code >= 0 && (int)w == 0x0202)   // WM_LBUTTONUP
            {
                int x = Marshal.ReadInt32(l, 0), y = Marshal.ReadInt32(l, 4);
                Ui(() => CloseClicked(x, y));   // after the hook returns: hooks must be quick
            }
            return Native.CallNextHookEx(mouseHook, code, w, l);
        };
        mouseHook = Native.HookMouse(mouseProc);
    }

    void StopWatchingCloseButtons()
    {
        if (mouseHook == IntPtr.Zero) return;
        Native.UnhookWindowsHookEx(mouseHook);
        mouseHook = IntPtr.Zero;
    }

    void CloseClicked(int x, int y)
    {
        var vm = running.Values.FirstOrDefault(v => v.Window != IntPtr.Zero && !v.Hidden && Native.OnCloseButton(v.Window, x, y));
        if (vm == null) return;
        if (vm.Held) { CloseConsole(vm); return; }   // nothing is running, so just close it
        Native.HideWindow(vm.Window);
        vm.Hidden = true;
        ShowLauncher();
    }

    // Presses the VM's power button. Linux shuts down cleanly; GNOME asks first and powers off after 60 seconds.
    void Shutdown(RunningVm vm, bool restart = false)
    {
        // Some desktops ask before powering off, so the window must be visible to answer.
        if (vm.Hidden) ShowVm(vm);
        if (restart) vm.RestartAsked = DateTime.Now;
        bool paused = vm.Paused;
        Qmp(vm, paused ? new[] { "cont", "system_powerdown" } : new[] { "system_powerdown" }, restart ? "restart" : "shut down");
        vm.Paused = false;
        tray.ShowBalloonTip(3000, restart ? "Restarting" : "Shutting down",
            vm.Cfg.DisplayName + (restart ? " shuts down, then starts again." : " is shutting down."), ToolTipIcon.Info);
        if (paused) RefreshHome();
    }

    // Console: a running system's window, or for one that's off, its window with the machine still off.
    // In fullscreen that would be a blank screen, so it offers to power the system on instead.
    void OpenConsole(VmConfig cfg)
    {
        RunningVm r;
        if (running.TryGetValue(cfg.Name, out r)) { ShowVm(r); return; }
        if (cfg.Fullscreen)
        {
            if (Confirm(cfg.DisplayName + " isn't running",
                    "Would you like to power it on?", "Power on"))
                Boot(cfg);
            return;
        }
        Boot(cfg, cfg.Render == 2, false, true);
    }

    // Powers on a system whose console was opened while it was off.
    void PowerOn(RunningVm vm)
    {
        Qmp(vm, new[] { "cont" }, "power on");
        vm.Held = false;
        vm.Started = DateTime.Now;   // the installer check counts from here
        RecordRender(vm);
        RefreshHome();
        ShowVm(vm);
    }

    void CloseConsole(RunningVm vm)
    {
        vm.ForceStopped = true;   // ends quietly: no error, restart or CPU-rendering retry
        Qmp(vm, new[] { "quit" }, "close the console of");
    }

    bool Confirm(string title, string text, string action, bool danger = false)
    {
        return ThemedDialog.Show(Visible ? this : null, title, text, null, null, action, "Cancel", danger);
    }

    void SetPaused(RunningVm vm, bool pause)
    {
        Qmp(vm, new[] { pause ? "stop" : "cont" }, pause ? "pause" : "resume");
        vm.Paused = pause;
        RefreshHome();
    }

    // Reset and Force stop lose unsaved work, so they ask first.
    void Reset(RunningVm vm)
    {
        if (!Confirm("Reset " + vm.Cfg.DisplayName + "?",
                "This restarts it immediately, like a PC's reset button. Unsaved work in Linux is lost.", "Reset", true)) return;
        // Ending QEMU and starting it again is a cleaner reset than resetting in place (see -action reboot=shutdown).
        vm.ResetAsked = true;
        Qmp(vm, new[] { "quit" }, "reset");
    }

    void ForceStop(RunningVm vm)
    {
        if (!Confirm("Force " + vm.Cfg.DisplayName + " to stop?",
                "This turns it off immediately, like unplugging a PC. Unsaved work in Linux is lost.", "Force stop", true)) return;
        vm.ForceStopped = true;
        try { vm.P.Kill(); } catch { }
    }

    void Qmp(RunningVm vm, string[] commands, string what)
    {
        Task.Run(() =>
        {
            try { foreach (var c in commands) Net.Qmp(vm.QmpPort, c); }
            catch (Exception ex) { Ui(() => Error("Couldn't " + what + " " + vm.Cfg.DisplayName + ": " + ex.Message)); }
        });
    }

    void RefreshHome() { if (Visible && !busy && page != null && page.Tag as string == "home") ShowHome(); }

    // Applies the theme chosen in Settings > General > Appearance (or Windows' light or dark mode, with
    // Follow system) and redraws the window in it. Pages take their colors when they're made, so the
    // page on screen is made again; any other page picks the theme up the next time it opens.
    void RefreshTheme()
    {
        string want = AppSettings.EffectiveTheme;
        if (want == Theme.Name) return;
        Theme.Apply(want);
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        if (IsHandleCreated) { Native.DarkTitleBar(Handle); Native.RedrawFrame(Handle); }
        notices.Restyle();
        RefreshHome();
    }

    // The power commands for a running system, for the home page's Power menu and the tray menu.
    ToolStripItem[] PowerItems(RunningVm v)
    {
        Func<string, string, Action, ToolStripMenuItem> item = (text, tip, act) =>
            new ToolStripMenuItem(text, null, delegate { act(); }) { ToolTipText = tip };
        Func<ToolStripMenuItem, ToolStripMenuItem> danger = i => { i.ForeColor = Theme.Danger; return i; };
        if (v.Held) return new ToolStripItem[] {
            item("Power on", "Starts the system in its open window", () => PowerOn(v)),
            item("Close console", "Closes the window; the system stays off", () => CloseConsole(v)),
        };
        return new ToolStripItem[] {
            item("Shut down", "Presses the power button so Linux shuts down cleanly", () =>
            {
                if (Confirm("Shut down " + v.Cfg.DisplayName + "?",
                        "Linux shuts down cleanly. Its desktop may ask first; GNOME goes ahead on its own after 60 seconds.", "Shut down"))
                    Shutdown(v);
            }),
            item("Restart", "Shuts Linux down cleanly, then starts it again with any changed settings", () =>
            {
                if (Confirm("Restart " + v.Cfg.DisplayName + "?",
                        "Linux shuts down cleanly, then starts again with any settings you changed. Its desktop may ask first; GNOME goes ahead on its own after 60 seconds.", "Restart"))
                    Shutdown(v, true);
            }),
            v.Paused ? item("Resume", "Continues where it was paused", () => SetPaused(v, false))
                     : item("Pause", "Freezes the system where it is, without shutting down", () => SetPaused(v, true)),
            new ToolStripSeparator(),
            danger(item("Force stop", "Turns it off immediately; unsaved work is lost", () => ForceStop(v))),
            danger(item("Reset", "Restarts it immediately; unsaved work is lost", () => Reset(v))),
        };
    }

    void BuildTrayMenu()
    {
        var m = tray.ContextMenuStrip;
        m.Items.Clear();
        foreach (var vm in running.Values.ToList())
        {
            var v = vm;
            var item = new ToolStripMenuItem(v.Cfg.DisplayName);
            item.DropDownItems.Add("Open window", null, delegate { ShowVm(v); });
            if (v.Cfg.SharedFolder.Length > 0)
                item.DropDownItems.Add("Open shared folder", null, delegate { Process.Start("explorer.exe", Q(v.Cfg.SharedFolder)); });
            item.DropDownItems.Add(new ToolStripSeparator());
            item.DropDownItems.AddRange(PowerItems(v));
            m.Items.Add(item);
        }
        if (running.Count == 0) m.Items.Add(new ToolStripMenuItem("No systems running") { Enabled = false });
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add("Open Try Linux", null, delegate { ShowLauncher(); });
        m.Items.Add("Quit Try Linux", null, delegate
        {
            if (running.Count > 0 && !Ask("Quit Try Linux?", "Running systems keep running, but without the tray menu.", "Quit")) return;
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

    void Error(string msg) { Notice(msg, "Something went wrong", NoticeKind.Error); }
    void Info(string msg) { Notice(msg, "Try Linux", NoticeKind.Info); }
    int noticeCount;

    // A themed notice. Its title is the message's first part: up to a blank line, a colon or the
    // first sentence, when that's short; otherwise `fallback`, with the whole message below.
    // While the window is open, a message goes to the notification area; info clears itself after 10 seconds.
    // With Try Linux only in the tray, it's a dialog, since nothing would show the notification.
    void Notice(string msg, string fallback, NoticeKind kind)
    {
        if (Visible && WindowState != FormWindowState.Minimized)
        {
            string id = "msg" + (++noticeCount);
            notices.Post(id, kind, msg.Trim().Replace("\n\n", "\n"));
            if (kind == NoticeKind.Info || kind == NoticeKind.Success)
            {
                var t = new Timer { Interval = 10000 };
                t.Tick += delegate { t.Dispose(); notices.Remove(id); };
                t.Start();
            }
            return;
        }
        msg = msg.Trim();
        string title = null, body = null;
        int cut;
        if ((cut = msg.IndexOf("\n\n")) > 0) { title = msg.Substring(0, cut); body = msg.Substring(cut + 2); }
        else if ((cut = msg.IndexOf(": ")) > 0) { title = msg.Substring(0, cut); body = msg.Substring(cut + 2); }
        else if ((cut = msg.IndexOf(". ")) > 0) { title = msg.Substring(0, cut + 1); body = msg.Substring(cut + 2); }
        else title = msg;
        if (title.Length > 70) { title = fallback; body = msg; }
        if (body != null && body.Length > 0) body = char.ToUpper(body[0]) + body.Substring(1);
        ThemedDialog.Show(Visible ? this : null, title.TrimEnd('.', ':'), body, null, null, "OK");
    }

    bool Ask(string title, string text, string yes, string no = "Cancel", bool danger = false)
    {
        return ThemedDialog.Show(Visible ? this : null, title, text, null, null, yes, no, danger);
    }

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
            RoundBox.Attach(box);
            box.SetBounds(D(16), D(40), D(488), D(30));
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
