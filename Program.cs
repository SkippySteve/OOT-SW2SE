using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

[assembly: AssemblyTitle("OOT SW2SE")]
[assembly: AssemblyDescription("Offline save editor for the Nintendo Switch 2 remake")]
[assembly: AssemblyProduct("OOT SW2SE")]
[assembly: AssemblyVersion("2.0.0.0")]
[assembly: AssemblyFileVersion("2.0.0.0")]

internal static class Program
{
    internal static readonly string AppFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OcarinaOfTimeSaveEditor");

    [STAThread]
    private static void Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        AppDomain.CurrentDomain.AssemblyResolve += ResolveAssembly;
        // After an update, the new version waits until the old one has exited
        // and released the browser profile, then removes the old exe.
        int previous;
        if (args.Length == 2 && args[0] == "--after" && int.TryParse(args[1], out previous))
        {
            try { Process.GetProcessById(previous).WaitForExit(15000); }
            catch (ArgumentException) { }
        }
        Updater.RemoveReplaced(Application.ExecutablePath);
        try
        {
            LoadBrowserLoader();
            RunEditor();
        }
        catch (Exception error)
        {
            MessageBox.Show(error.GetBaseException().Message, "OOT SW2SE",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RunEditor()
    {
        Application.Run(new EditorWindow());
    }

    private static Assembly ResolveAssembly(object sender, ResolveEventArgs args)
    {
        string name = new AssemblyName(args.Name).Name;
        if (name != "Microsoft.Web.WebView2.Core" && name != "Microsoft.Web.WebView2.WinForms")
            return null;
        return Assembly.Load(ReadResource(name + ".gz"));
    }

    internal static byte[] ReadResource(string name)
    {
        using (Stream raw = typeof(Program).Assembly.GetManifestResourceStream(name))
        using (var gzip = new GZipStream(raw, CompressionMode.Decompress))
        using (var buffer = new MemoryStream())
        {
            gzip.CopyTo(buffer);
            return buffer.ToArray();
        }
    }

    private static void LoadBrowserLoader()
    {
        string folder = Path.Combine(AppFolder, "WebView2Loader-1.0.4314.35");
        string file = Path.Combine(folder, "WebView2Loader.dll");
        Directory.CreateDirectory(folder);
        if (!File.Exists(file))
        {
            string temp = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                File.WriteAllBytes(temp, ReadResource("WebView2Loader.gz"));
                try { File.Move(temp, file); }
                catch (IOException) { if (!File.Exists(file)) throw; }
            }
            finally
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
        }
        if (LoadLibraryEx(file, IntPtr.Zero, 0x1100) == IntPtr.Zero)
            throw new InvalidOperationException("The Microsoft WebView2 loader could not start.");
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryEx(string fileName, IntPtr fileHandle, uint flags);
}

internal sealed class EditorWindow : Form
{
    private const string PageUri = "https://editor.oot.local/index.html";
    private readonly Updater updater = new Updater(
        typeof(Program).Assembly.GetName().Version.ToString(3), Updater.LoadKey(), Updater.Download);
    private const int MaxBackupBytes = 8 * 1024 * 1024;
    private const int MaxFileBytes = 2 * 1024 * 1024;
    private readonly WebView2 browser = new WebView2();
    private readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 };
    private bool dialogOpen;
    private bool workspaceLoaded;
    private string lastFolder;

    internal EditorWindow()
    {
        Text = "OOT SW2SE";
        StartPosition = FormStartPosition.Manual;
        ApplyWorkspaceLayout(false, Screen.FromPoint(Cursor.Position).WorkingArea);
        BackColor = Color.FromArgb(16, 27, 24);
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        browser.Dock = DockStyle.Fill;
        browser.DefaultBackgroundColor = BackColor;
        Controls.Add(browser);
        Shown += (sender, args) => ApplyWorkspaceLayout(false, Screen.FromControl(this).WorkingArea);
        Shown += InitializeBrowser;
    }

    private void ApplyWorkspaceLayout(bool loaded, Rectangle area)
    {
        WindowState = FormWindowState.Normal;
        FormBorderStyle = loaded ? FormBorderStyle.Sizable : FormBorderStyle.FixedSingle;
        MaximizeBox = loaded;
        MinimumSize = Size.Empty;
        double scale = IsHandleCreated ? Math.Max(96, GetDpiForWindow(Handle)) / 96.0 : 1.0;
        Size logicalSize = loaded ? new Size(1100, 730) : new Size(570, 300);
        Size requested = SizeFromClientSize(new Size((int)Math.Ceiling(logicalSize.Width * scale),
            (int)Math.Ceiling(logicalSize.Height * scale)));
        int width = Math.Min(requested.Width, area.Width);
        int height = Math.Min(requested.Height, area.Height);
        Bounds = new Rectangle(area.Left + (area.Width - width) / 2,
            area.Top + (area.Height - height) / 2, width, height);
        if (loaded) MinimumSize = new Size(Math.Min(740, width), Math.Min(500, height));
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int enabled = 1;
        if (DwmSetWindowAttribute(Handle, 20, ref enabled, sizeof(int)) != 0)
            DwmSetWindowAttribute(Handle, 19, ref enabled, sizeof(int));
    }

    private async void InitializeBrowser(object sender, EventArgs e)
    {
        try
        {
            string version = typeof(Program).Assembly.GetName().Version.ToString(3);
            string html = System.Text.Encoding.UTF8.GetString(Program.ReadResource("editor.html.gz"));
            byte[] page = System.Text.Encoding.UTF8.GetBytes(html.Replace("{{EDITOR_VERSION}}", version));
            var environment = await CoreWebView2Environment.CreateAsync(
                null, Path.Combine(Program.AppFolder, "BrowserProfile"));
            if (IsDisposed) return;
            await browser.EnsureCoreWebView2Async(environment);
            if (IsDisposed) return;
            var core = browser.CoreWebView2;
            // Tells the page what this host offers beyond the three requests.
            await core.AddScriptToExecuteOnDocumentCreatedAsync("window.chrome.webview.hostFeatures = " +
                json.Serialize(updater.Enabled ? (object)new { updates = new { auto = Environment.GetEnvironmentVariable(
                    "OOT_SW2SE_NO_UPDATE_CHECK") != "1", releases = Updater.Releases } } : new { }) + ";");
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.NavigationStarting += (s, args) =>
            {
                if (args.Uri != PageUri && !args.Uri.StartsWith(PageUri + "#", StringComparison.Ordinal))
                    args.Cancel = true;
            };
            core.NewWindowRequested += (s, args) => args.Handled = true;
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += (s, args) =>
            {
                args.Response = args.Request.Uri == PageUri
                    ? environment.CreateWebResourceResponse(new MemoryStream(page, false), 200, "OK",
                        "Content-Type: text/html; charset=utf-8\r\nCache-Control: no-store")
                    : environment.CreateWebResourceResponse(null, 404, "Not Found", "");
            };
            core.WebMessageReceived += HandleMessage;
            core.Navigate(PageUri);
        }
        catch (WebView2RuntimeNotFoundException)
        {
            MessageBox.Show(this, "The Microsoft Edge WebView2 Runtime is required to run this app.",
                Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
        }
        catch (Exception error)
        {
            if (IsDisposed) return;
            MessageBox.Show(this, "The editor window could not start.\n\n" +
                error.GetBaseException().Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
        }
    }

    private async void HandleMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (e.Source != PageUri || IsDisposed) return;
        Request request;
        try { request = json.Deserialize<Request>(e.WebMessageAsJson); }
        catch (ArgumentException) { return; }
        catch (InvalidOperationException) { return; }
        if (request == null) return;
        if (request.action == "workspace")
        {
            if (workspaceLoaded != request.loaded)
            {
                workspaceLoaded = request.loaded;
                ApplyWorkspaceLayout(workspaceLoaded, Screen.FromControl(this).WorkingArea);
            }
            return;
        }
        if (request.id < 1) return;
        if (request.action == "openUrl")
        {
            try { Reply(request.id, Updater.OpenGuide(request.url), null); }
            catch (Exception error) { Reply(request.id, null, error.GetBaseException().Message); }
            return;
        }
        if (dialogOpen)
        {
            Reply(request.id, null, "A file dialog is already open.");
            return;
        }

        dialogOpen = true;
        try
        {
            object result;
            switch (request.action)
            {
                case "openBackup":
                    string folder = FolderPicker.Select(Handle, lastFolder);
                    if (folder != null) lastFolder = folder;
                    result = folder == null ? null : await Task.Run(() => ReadBackup(folder));
                    break;
                case "savePatch":
                    result = await SavePatch(request);
                    break;
                case "saveFolder":
                    result = await SaveFolder(request);
                    break;
                case "checkUpdate":
                    if (!updater.Enabled) throw new InvalidOperationException("Updates are off for this copy of the app.");
                    result = await updater.Check();
                    break;
                case "installUpdate":
                    if (!updater.Enabled) throw new InvalidOperationException("Updates are off for this copy of the app.");
                    await updater.Install(Application.ExecutablePath);
                    Process.Start(new ProcessStartInfo(Application.ExecutablePath, "--after " + Process.GetCurrentProcess().Id) { UseShellExecute = false });
                    BeginInvoke(new Action(Close));
                    result = true;
                    break;
                default:
                    throw new InvalidOperationException("Unknown editor action.");
            }
            Reply(request.id, result, null);
        }
        catch (Exception error)
        {
            Reply(request.id, null, error.GetBaseException().Message);
        }
        finally { dialogOpen = false; }
    }

    private void Reply(int id, object result, string error)
    {
        if (IsDisposed) return;
        browser.CoreWebView2.PostWebMessageAsJson(json.Serialize(new { id, result, error }));
    }

    private static object ReadBackup(string folder)
    {
        var files = new List<object>();
        int bytes = 0;
        string prefix = "Backup/files/";
        if (HasSavePair(folder))
        {
            string name = new DirectoryInfo(folder).Name;
            if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(new[] { '/', '\\', ':' }) >= 0)
                name = "Save";
            ReadPair(files, folder, name + "/", ref bytes);
            string localOption = Path.Combine(folder, "option.sav");
            if (File.Exists(localOption)) ReadFile(files, localOption, name + "/option.sav", ref bytes);
            string localAccount = Path.Combine(folder, "account.sav");
            if (File.Exists(localAccount)) ReadFile(files, localAccount, name + "/account.sav", ref bytes);
            return files;
        }

        // A PC port's game or data folder: open the save folder inside it.
        string profile = PortSaves.ProfileFolder(folder);
        if (profile != null) folder = profile;

        string nested = Path.Combine(folder, "files");
        string parent = Directory.Exists(nested) ? nested : folder;
        for (int i = 0; i < 6; i++)
        {
            string slot = "slot_" + i.ToString("00");
            string slotFolder = Path.Combine(parent, slot);
            if (HasSavePair(slotFolder)) ReadPair(files, slotFolder, prefix + slot + "/", ref bytes);
        }
        if (files.Count == 0) throw new IOException("No folder containing progress.sav and caption.sav was found.");
        string option = Path.Combine(parent, "option.sav");
        string account = Path.Combine(parent, "account.sav");
        string manifest = Path.Combine(folder, "manifest.json");
        if (File.Exists(option)) ReadFile(files, option, prefix + "option.sav", ref bytes);
        if (File.Exists(account)) ReadFile(files, account, prefix + "account.sav", ref bytes);
        if (File.Exists(manifest)) ReadFile(files, manifest, "Backup/manifest.json", ref bytes);
        return files;
    }

    private static bool HasSavePair(string directory)
    {
        return File.Exists(Path.Combine(directory, "progress.sav")) &&
            File.Exists(Path.Combine(directory, "caption.sav"));
    }

    private static void ReadPair(List<object> files, string folder, string prefix, ref int bytes)
    {
        foreach (string name in new[] { "progress.sav", "caption.sav" })
            ReadFile(files, Path.Combine(folder, name), prefix + name, ref bytes);
    }

    private static void ReadFile(List<object> files, string file, string relativePath, ref int bytes)
    {
        using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            long size = stream.Length;
            if (size > MaxFileBytes || size > MaxBackupBytes - bytes)
                throw new IOException("The backup files exceed the expected size.");
            var contents = new byte[(int)size];
            int offset = 0;
            while (offset < contents.Length)
            {
                int read = stream.Read(contents, offset, contents.Length - offset);
                if (read == 0) throw new EndOfStreamException("A backup file was truncated while reading it.");
                offset += read;
            }
            bytes += contents.Length;
            files.Add(new { relativePath, contents = Convert.ToBase64String(contents) });
        }
    }

    private async Task<object> SavePatch(Request request)
    {
        if (string.IsNullOrEmpty(request.contents) || request.contents.Length > MaxBackupBytes * 2)
            throw new IOException("The patch ZIP is empty or too large.");
        byte[] bytes = Convert.FromBase64String(request.contents);
        if (bytes.Length < 22 || bytes.Length > MaxBackupBytes ||
            bytes[0] != 0x50 || bytes[1] != 0x4B || bytes[2] != 3 || bytes[3] != 4)
            throw new IOException("The editor did not create a valid patch ZIP.");
        using (var dialog = new SaveFileDialog())
        {
            dialog.Title = "Save patch";
            dialog.Filter = "ZIP archive (*.zip)|*.zip";
            dialog.DefaultExt = "zip";
            dialog.AddExtension = true;
            dialog.OverwritePrompt = true;
            dialog.FileName = Path.GetFileName(request.name ?? "Ocarina-of-Time-Patch.zip");
            if (dialog.ShowDialog(this) != DialogResult.OK) return null;
            string path = dialog.FileName;
            await Task.Run(() => WritePatch(path, bytes));
            return Path.GetFileName(path);
        }
    }

    private static void WritePatch(string path, byte[] bytes)
    {
        string temp = Path.Combine(Path.GetDirectoryName(path), Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                stream.Write(bytes, 0, bytes.Length);
            if (File.Exists(path)) File.Replace(temp, path, null);
            else File.Move(temp, path);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    private async Task<object> SaveFolder(Request request)
    {
        var files = PatchFolderWriter.Decode(request.files, request.name);
        string folder = FolderPicker.Select(Handle, lastFolder, "Select where to create the patch folder");
        if (folder == null) return null;
        return await Task.Run(() => PatchFolderWriter.Write(folder, files, request.name));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    public sealed class Request
    {
        public int id { get; set; }
        public string action { get; set; }
        public string name { get; set; }
        public string contents { get; set; }
        public PatchFolderWriter.Entry[] files { get; set; }
        public bool loaded { get; set; }
        public string url { get; set; }
    }
}

// PC ports of the game keep the same save files as the Switch, in one folder per
// profile: Longshot in %APPDATA%\Longshot\save\<user id> (Linux:
// ~/.local/share/longshot/save/<user id>), Project ReLink in
// <game folder>\user\save\<user id> (Linux: ~/.local/share/relink/save/<user id>).
// When the picked folder holds no save itself, the profile folder is looked for
// in it, in save and in user\save. Same rules as the macOS and Linux hosts.
internal static class PortSaves
{
    // The one profile both ports open (their nn_applet user id).
    internal const string GameUser = "5a3c9e1f0b7d24680000000000000001";

    // The profile folder to open for `folder`, or null when `folder` holds a save
    // itself or no profile folder is found.
    internal static string ProfileFolder(string folder)
    {
        if (HoldsSlots(folder) || HoldsSlots(Path.Combine(folder, "files"))) return null;
        foreach (string saves in new[] { folder, Path.Combine(folder, "save"), Path.Combine(folder, "user", "save") })
        {
            if (!Directory.Exists(saves)) continue;
            var profiles = new List<string>();
            foreach (string profile in Directory.GetDirectories(saves))
                if (HoldsSlots(profile)) profiles.Add(profile);
            if (profiles.Count == 0) continue;
            if (profiles.Count == 1) return profiles[0];
            foreach (string profile in profiles)
                if (string.Equals(Path.GetFileName(profile), GameUser, StringComparison.OrdinalIgnoreCase)) return profile;
            var names = profiles.ConvertAll(p => Path.GetFileName(p));
            names.Sort(StringComparer.Ordinal);
            throw new IOException("This folder holds the saves of several profiles. Open one of them: " +
                string.Join(", ", names) + ".");
        }
        return null;
    }

    private static bool HoldsSlots(string folder)
    {
        for (int i = 0; i < 6; i++)
        {
            string slot = Path.Combine(folder, "slot_" + i.ToString("00"));
            if (File.Exists(Path.Combine(slot, "progress.sav")) && File.Exists(Path.Combine(slot, "caption.sav")))
                return true;
        }
        return false;
    }
}

internal static class PatchFolderWriter
{
    public sealed class Entry
    {
        public string relativePath { get; set; }
        public string contents { get; set; }
    }

    internal static string Timestamp(string name)
    {
        var match = System.Text.RegularExpressions.Regex.Match(name ?? "",
            @"\AOcarina-of-Time-(?:Save|slot_0[0-5])-Patch-([0-9]{8}_[0-9]{6})_[0-9a-fA-F]{8}(?:-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}\z");
        if (!match.Success) throw new IOException("Invalid patch folder name.");
        return match.Groups[1].Value;
    }

    internal static Dictionary<string, byte[]> Decode(Entry[] entries, string name)
    {
        string timestamp = Timestamp(name);
        if (entries == null || entries.Length < 2 || entries.Length > 4)
            throw new IOException("Invalid patch file list.");
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        int total = 0;
        foreach (var entry in entries)
        {
            if (entry == null || entry.relativePath == null || !System.Text.RegularExpressions.Regex.IsMatch(
                entry.relativePath, @"\A(?:manifest\.json|" + timestamp + @"/(?:progress\.sav|caption\.sav|option\.sav|slot_0[0-5]/(?:progress|caption)\.sav))\z") ||
                files.ContainsKey(entry.relativePath) || string.IsNullOrEmpty(entry.contents) || entry.contents.Length > 4 * 1024 * 1024)
                throw new IOException("Invalid patch file.");
            byte[] bytes = Convert.FromBase64String(entry.contents);
            total += bytes.Length;
            if (bytes.Length > 2 * 1024 * 1024 || total > 8 * 1024 * 1024)
                throw new IOException("The patch files are too large.");
            files.Add(entry.relativePath, bytes);
        }
        return files;
    }

    internal static string Write(string parent, Dictionary<string, byte[]> files, string name)
    {
        Timestamp(name);
        string token = Guid.NewGuid().ToString("N");
        string staging = Path.Combine(parent, ".oot-patch-" + token);
        string destination = Path.Combine(parent, name);
        Directory.CreateDirectory(staging);
        try
        {
            foreach (var file in files)
            {
                string path = Path.Combine(staging, file.Key.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    stream.Write(file.Value, 0, file.Value.Length);
            }
            Directory.Move(staging, destination);
            return destination;
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
        }
    }
}

internal static class FolderPicker
{
    internal static string Select(IntPtr owner, string initial, string title = "Select a backup or save folder")
    {
        var dialog = (IFileDialog)new FileOpenDialog();
        try
        {
            uint options;
            dialog.GetOptions(out options);
            dialog.SetOptions(options | 0x20 | 0x40 | 0x800);
            dialog.SetTitle(title);
            dialog.SetOkButtonLabel("Select Folder");
            if (Directory.Exists(initial))
            {
                IShellItem item;
                var id = typeof(IShellItem).GUID;
                if (SHCreateItemFromParsingName(initial, IntPtr.Zero, ref id, out item) == 0)
                {
                    try { dialog.SetFolder(item); }
                    finally { Marshal.ReleaseComObject(item); }
                }
            }
            int hr = dialog.Show(owner);
            if (hr == unchecked((int)0x800704C7)) return null;
            Marshal.ThrowExceptionForHR(hr);
            IShellItem selected;
            dialog.GetResult(out selected);
            try
            {
                IntPtr path;
                selected.GetDisplayName(0x80058000, out path);
                try { return Marshal.PtrToStringUni(path); }
                finally { Marshal.FreeCoTaskMem(path); }
            }
            finally { Marshal.ReleaseComObject(selected); }
        }
        finally { Marshal.ReleaseComObject(dialog); }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr context, ref Guid id, out IShellItem item);

    [ComImport, Guid("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7")]
    private class FileOpenDialog { }

    [ComImport, Guid("42f85136-db7e-439c-85f1-e4075d135fc8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileDialog
    {
        [PreserveSig] int Show(IntPtr owner);
        void SetFileTypes(uint count, IntPtr types);
        void SetFileTypeIndex(uint index);
        void GetFileTypeIndex(out uint index);
        void Advise(IntPtr events, out uint cookie);
        void Unadvise(uint cookie);
        void SetOptions(uint options);
        void GetOptions(out uint options);
        void SetDefaultFolder(IShellItem folder);
        void SetFolder(IShellItem folder);
        void GetFolder(out IShellItem folder);
        void GetCurrentSelection(out IShellItem item);
        void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetFileName(out IntPtr name);
        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
        void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string text);
        void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
        void GetResult(out IShellItem item);
    }

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(IntPtr bindContext, ref Guid handler, ref Guid id, out IntPtr result);
        void GetParent(out IShellItem parent);
        void GetDisplayName(uint format, out IntPtr name);
    }
}

// Signed updates (update/README.md). Each release carries update.json, listing
// the files of every platform with their SHA-256, and update.json.sig: an ECDSA
// P-256 signature over it (SHA-256, raw r||s, base64) made in GitHub Actions
// with a key only the repository holds. The public key is built into the app
// (update/public-key.pem); without it, updates are off. Same scheme as the
// macOS and Linux hosts.
internal sealed class Updater
{
    internal const string Repo = "SkippySteve/OOT-SW2SE";
    internal const string Releases = "https://github.com/" + Repo + "/releases";
    internal const int MaxManifestBytes = 64 * 1024;
    internal const int MaxUpdateBytes = 256 * 1024 * 1024;
    private const string ExeName = "OOT SW2SE.exe";
    private static readonly string[] GuidePrefixes = { "https://www.ign.com/wikis/ocarina-of-time-remake/", Releases + "/" };

    private readonly string version;
    private readonly byte[] publicKey;
    private readonly Func<string, int, Task<byte[]>> download;
    private Dictionary<string, object> manifest;

    internal Updater(string version, byte[] publicKey, Func<string, int, Task<byte[]>> download)
    {
        this.version = version;
        this.publicKey = publicKey;
        this.download = download;
    }

    internal bool Enabled { get { return publicKey != null; } }

    internal static byte[] LoadKey()
    {
        try { return ParsePublicKey(System.Text.Encoding.ASCII.GetString(Program.ReadResource("update-key.pem.gz"))); }
        catch (Exception) { return null; }
    }

    // The X and Y of a P-256 public key in PEM (SubjectPublicKeyInfo) form.
    internal static byte[] ParsePublicKey(string pem)
    {
        const string begin = "-----BEGIN PUBLIC KEY-----", end = "-----END PUBLIC KEY-----";
        int start = pem.IndexOf(begin, StringComparison.Ordinal), stop = pem.IndexOf(end, StringComparison.Ordinal);
        if (start < 0 || stop < start) throw new FormatException("Not a public key.");
        byte[] der = Convert.FromBase64String(pem.Substring(start + begin.Length, stop - start - begin.Length));
        byte[] prefix = { 0x30, 0x59, 0x30, 0x13, 0x06, 0x07, 0x2A, 0x86, 0x48, 0xCE, 0x3D, 0x02, 0x01, 0x06, 0x08, 0x2A,
            0x86, 0x48, 0xCE, 0x3D, 0x03, 0x01, 0x07, 0x03, 0x42, 0x00, 0x04 };
        if (der.Length != prefix.Length + 64) throw new FormatException("Not a P-256 public key.");
        for (int i = 0; i < prefix.Length; i++)
            if (der[i] != prefix[i]) throw new FormatException("Not a P-256 public key.");
        var key = new byte[64];
        Array.Copy(der, prefix.Length, key, 0, 64);
        return key;
    }

    internal static bool Verify(byte[] data, byte[] signature, byte[] key)
    {
        if (signature.Length != 64 || key.Length != 64) return false;
        var x = new byte[32];
        var y = new byte[32];
        Array.Copy(key, 0, x, 0, 32);
        Array.Copy(key, 32, y, 0, 32);
        var parameters = new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = new ECPoint { X = x, Y = y } };
        using (ECDsa ecdsa = ECDsa.Create(parameters))
            return ecdsa.VerifyData(data, signature, HashAlgorithmName.SHA256);
    }

    internal static int[] ParseVersion(object text)
    {
        var value = text as string;
        var match = value == null ? null : System.Text.RegularExpressions.Regex.Match(value, @"\Av?([0-9]{1,4})\.([0-9]{1,4})\.([0-9]{1,6})\z");
        if (match == null || !match.Success) return null;
        return new[] { int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value), int.Parse(match.Groups[3].Value) };
    }

    internal static int CompareVersions(int[] a, int[] b)
    {
        for (int i = 0; i < 3; i++)
            if (a[i] != b[i]) return a[i].CompareTo(b[i]);
        return 0;
    }

    internal static async Task<byte[]> Download(string url, int limit)
    {
        if (!url.StartsWith("https://", StringComparison.Ordinal)) throw new IOException("Updates are only downloaded over HTTPS.");
        ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        using (var client = new WebClient())
        {
            client.Headers[HttpRequestHeader.UserAgent] = "OOT-SW2SE";
            byte[] data = await client.DownloadDataTaskAsync(url);
            if (data.Length > limit) throw new IOException("The update is larger than expected.");
            return data;
        }
    }

    internal async Task<object> Check()
    {
        byte[] data = await download(Releases + "/latest/download/update.json", MaxManifestBytes);
        byte[] signatureText = await download(Releases + "/latest/download/update.json.sig", 1024);
        byte[] signature;
        try { signature = Convert.FromBase64String(System.Text.Encoding.ASCII.GetString(signatureText).Trim()); }
        catch (FormatException) { throw new IOException("The update signature is malformed."); }
        if (!Verify(data, signature, publicKey)) throw new IOException("The update's signature is not valid. Nothing was installed.");
        Dictionary<string, object> parsed;
        try { parsed = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(System.Text.Encoding.UTF8.GetString(data)); }
        catch (ArgumentException) { throw new IOException("The update manifest is malformed."); }
        int[] latest = parsed == null || !parsed.ContainsKey("version") ? null : ParseVersion(parsed["version"]);
        int[] current = ParseVersion(version);
        object format;
        if (latest == null || current == null || !parsed.TryGetValue("format", out format) || !(format is int) || (int)format != 1)
            throw new IOException("The update manifest is malformed.");
        manifest = parsed;
        return new Dictionary<string, object> {
            { "current", version }, { "latest", parsed["version"] },
            { "available", CompareVersions(latest, current) > 0 && Asset() != null },
            { "notes", Releases + "/tag/v" + parsed["version"] } };
    }

    private Dictionary<string, object> Asset()
    {
        object assets, asset;
        if (manifest == null || !manifest.TryGetValue("assets", out assets)) return null;
        var all = assets as Dictionary<string, object>;
        return all != null && all.TryGetValue("windows", out asset) ? asset as Dictionary<string, object> : null;
    }

    // Downloads and checks the new version, then puts its exe in place of `exe`.
    // The running exe is renamed to "<exe>.old" (Windows allows renaming a
    // running program) and deleted at the next start.
    internal async Task Install(string exe)
    {
        if (manifest == null) await Check();
        if (CompareVersions(ParseVersion(manifest["version"]), ParseVersion(version)) <= 0)
            throw new IOException("This is already the newest version.");
        var asset = Asset();
        object name = null, digest = null, size = null;
        if (asset == null || !asset.TryGetValue("name", out name) || !asset.TryGetValue("sha256", out digest) ||
            !asset.TryGetValue("size", out size) || !(name is string) || !(digest is string) || !(size is int) ||
            !System.Text.RegularExpressions.Regex.IsMatch((string)name, @"\A[A-Za-z0-9][A-Za-z0-9._-]{0,127}\z") ||
            !System.Text.RegularExpressions.Regex.IsMatch((string)digest, @"\A[0-9a-f]{64}\z") || (int)size <= 0 || (int)size > MaxUpdateBytes)
            throw new IOException("The update manifest is malformed.");
        byte[] data = await download(Releases + "/download/v" + manifest["version"] + "/" + name, (int)size);
        string actual;
        using (var sha = SHA256.Create()) actual = BitConverter.ToString(sha.ComputeHash(data)).Replace("-", "").ToLowerInvariant();
        if (data.Length != (int)size || actual != (string)digest)
            throw new IOException("The downloaded update does not match its checksum. Nothing was installed.");
        string folder = Path.GetDirectoryName(Path.GetFullPath(exe));
        string staged = Path.Combine(folder, "." + Guid.NewGuid().ToString("N") + ".update");
        try
        {
            using (var archive = new ZipArchive(new MemoryStream(data, false), ZipArchiveMode.Read))
            {
                ZipArchiveEntry entry = null;
                foreach (ZipArchiveEntry candidate in archive.Entries)
                    if (string.Equals(Path.GetFileName(candidate.FullName), ExeName, StringComparison.OrdinalIgnoreCase)) entry = candidate;
                if (entry == null || entry.Length <= 0 || entry.Length > MaxUpdateBytes)
                    throw new IOException("The update does not contain " + ExeName + ".");
                using (Stream input = entry.Open())
                using (var output = new FileStream(staged, FileMode.CreateNew, FileAccess.Write))
                    input.CopyTo(output);
            }
            Swap(exe, staged);
        }
        catch (UnauthorizedAccessException)
        {
            throw new IOException(folder + " can't be written. Download the new version from " + Releases + ".");
        }
        finally
        {
            if (File.Exists(staged)) File.Delete(staged);
        }
    }

    internal static void Swap(string exe, string replacement)
    {
        string old = exe + ".old";
        if (File.Exists(old)) File.Delete(old);
        File.Move(exe, old);
        try { File.Move(replacement, exe); }
        catch
        {
            File.Move(old, exe);
            throw;
        }
    }

    internal static void RemoveReplaced(string exe)
    {
        try { if (File.Exists(exe + ".old")) File.Delete(exe + ".old"); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    // Guides open in the default browser, but only IGN's wiki for this game and
    // this app's release notes.
    internal static bool OpenGuide(string url)
    {
        bool allowed = url != null && url.Length <= 500 && url.IndexOfAny(new[] { '"', '<', '>', '\\', ' ', '\t', '\r', '\n' }) < 0;
        bool known = false;
        foreach (string prefix in GuidePrefixes)
            if (allowed && url.StartsWith(prefix, StringComparison.Ordinal)) known = true;
        if (!known) throw new InvalidOperationException("Only IGN's Ocarina of Time Remake guides can be opened.");
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        return true;
    }
}
