// SPDX-License-Identifier: MIT
// Distribution shell only. Game engines and assets retain their own licenses.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

namespace SwarmDistribution
{
    public class Config { public int Schema = 1; public string Product = "SwarmAssaultII"; public string Repository; public string PublicKeyXml; public string DataRoot; }
    public class Active { public string Version; public string Engine; }
    public class Entry { public string Path; public long Length; public string Sha256; }
    public class Package { public int Schema; public string Product; public string Version; public List<Entry> Files; }
    public class Update { public int Schema; public string Product; public string Repository; public string Version; public string FileName; public long Length; public string Sha256; }
    public class Signed { public string Payload; public string Signature; }
    public static class Core
    {
        public const string Product = "SwarmAssaultII";
        public static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 };
        public static string DefaultRoot { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", Product); } }
        public static string Data(Config c) { return String.IsNullOrEmpty(c.DataRoot) ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Product, "Profile") : Path.GetFullPath(c.DataRoot); }
        public static T Read<T>(string path) { return Json.Deserialize<T>(File.ReadAllText(path, Encoding.UTF8)); }
        public static void Write(string path, object value) { Atomic(path, Encoding.UTF8.GetBytes(Json.Serialize(value))); }
        public static void Atomic(string path, byte[] bytes)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            string temp = path + ".new-" + Guid.NewGuid().ToString("N");
            try { File.WriteAllBytes(temp, bytes); if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path); }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        public static string Hash(Stream stream) { using (var sha = SHA256.Create()) return Hex(sha.ComputeHash(stream)); }
        public static string HashFile(string path) { using (var f = File.OpenRead(path)) return Hash(f); }
        public static string Hex(byte[] bytes) { return BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant(); }
        public static string Quote(string s) { return "\"" + Regex.Replace(s, @"(\\*)(""|$)", m => m.Groups[1].Value + m.Groups[1].Value + (m.Groups[2].Value == "\"" ? "\\\"" : "")) + "\""; }
        public static string Args(params string[] args) { return String.Join(" ", args.Select(Quote)); }
        public static Version VersionOf(string value)
        {
            if (value == null || !Regex.IsMatch(value, @"^\d{4}\.\d{1,2}\.\d{1,2}\.\d{1,6}$")) throw new InvalidDataException("올바르지 않은 버전입니다.");
            return new Version(value);
        }
        public static string Inside(string root, string relative)
        {
            if (String.IsNullOrWhiteSpace(relative) || relative.Contains("\\") || relative.Contains(":") || relative.StartsWith("/") || relative.Split('/').Any(x => x == ".." || x == "." || x.EndsWith(".") || x.EndsWith(" "))) throw new InvalidDataException("잘못된 파일 경로입니다.");
            string basePath = Path.GetFullPath(root).TrimEnd('\\') + "\\";
            string path = Path.GetFullPath(Path.Combine(basePath, relative.Replace('/', '\\')));
            if (!path.StartsWith(basePath, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("설치 경로를 벗어난 파일입니다.");
            NoLinks(path);
            return path;
        }
        public static void NoLinks(string path)
        {
            string p = Path.GetFullPath(path);
            while (!String.IsNullOrEmpty(p))
            {
                if ((File.Exists(p) || Directory.Exists(p)) && (File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0) throw new IOException("연결 폴더에는 설치할 수 없습니다.");
                p = Path.GetDirectoryName(p);
            }
        }
        public static string Root(string path, bool existing)
        {
            path = Path.GetFullPath(path).TrimEnd('\\');
            if (!String.Equals(Path.GetFileName(path), Product, StringComparison.OrdinalIgnoreCase)) throw new IOException("설치 폴더 이름은 SwarmAssaultII여야 합니다.");
            NoLinks(path);
            if (existing && (!File.Exists(Path.Combine(path, ".sii-install.json")) || Read<Config>(Path.Combine(path, ".sii-install.json")).Product != Product)) throw new IOException("설치된 게임을 찾을 수 없습니다.");
            if (!existing && Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any() && !File.Exists(Path.Combine(path, ".sii-install.json"))) throw new IOException("비어 있는 폴더를 선택해 주세요.");
            return path;
        }
        public static string MutexName(string root) { using (var s = new MemoryStream(Encoding.UTF8.GetBytes(Path.GetFullPath(root).ToUpperInvariant()))) return "Local\\SII-" + Hash(s).Substring(0, 24); }
        public static bool GameRunning(string root)
        {
            foreach (var p in Process.GetProcessesByName("OpenSA"))
                using (p) { try { if (p.MainModule.FileName.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) return true; } catch { return true; } }
            return false;
        }
        // The ZIP overlay has a fixed, bounded footer. Verify it before reading ZIP metadata.
        public static bool HasPayload(string exe)
        {
            using (var f = File.OpenRead(exe)) { if (f.Length < 56) return false; f.Position = f.Length - 56; byte[] b = new byte[8]; f.Read(b, 0, 8); return Encoding.ASCII.GetString(b) == "SIISET01"; }
        }
        public static string Unpack(string exe, string stage, Action<string, int> progress)
        {
            string zip = stage + ".zip";
            try
            {
                using (var f = File.OpenRead(exe))
                using (var br = new BinaryReader(f, Encoding.UTF8, true))
                {
                    if (f.Length < 56) throw new InvalidDataException("설치 파일이 잘렸습니다.");
                    f.Position = f.Length - 56;
                    if (Encoding.ASCII.GetString(br.ReadBytes(8)) != "SIISET01") throw new InvalidDataException("설치 데이터가 없습니다.");
                    long offset = br.ReadInt64(), length = br.ReadInt64(); byte[] expected = br.ReadBytes(32);
                    if (offset < 1024 || length < 1 || length > 2147483648L || offset > f.Length - 56 || length != f.Length - 56 - offset) throw new InvalidDataException("설치 파일 길이가 맞지 않습니다.");
                    f.Position = offset;
                    using (var output = File.Create(zip))
                    { byte[] buffer = new byte[1024 * 1024]; long left = length; while (left > 0) { int n = f.Read(buffer, 0, (int)Math.Min(left, buffer.Length)); if (n == 0) throw new EndOfStreamException(); output.Write(buffer, 0, n); left -= n; } }
                    if (HashFile(zip) != Hex(expected)) throw new InvalidDataException("설치 파일 검증에 실패했습니다. 다시 받아 주세요.");
                }
                Directory.CreateDirectory(stage);
                using (var archive = ZipFile.OpenRead(zip))
                {
                    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase); long total = 0; int count = 0;
                    foreach (var entry in archive.Entries)
                    {
                        if (entry.FullName.EndsWith("/")) throw new InvalidDataException("불필요한 디렉터리 항목입니다.");
                        string target = Inside(stage, entry.FullName);
                        if (!seen.Add(entry.FullName) || ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000) throw new InvalidDataException("중복 또는 연결 파일입니다.");
                        total = checked(total + entry.Length); if (total > 3221225472L || archive.Entries.Count > 20000) throw new InvalidDataException("설치 용량이 너무 큽니다.");
                        Directory.CreateDirectory(Path.GetDirectoryName(target));
                        using (var input = entry.Open()) using (var output = File.Create(target)) input.CopyTo(output);
                        progress("게임 파일 푸는 중", ++count * 60 / archive.Entries.Count);
                    }
                }
                Package package = Read<Package>(Path.Combine(stage, "package.json"));
                if (package.Schema != 1 || package.Product != Product || package.Files == null) throw new InvalidDataException("다른 제품의 설치 파일입니다.");
                VersionOf(package.Version);
                var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase); int verified = 0;
                foreach (Entry item in package.Files)
                {
                    string p = Inside(stage, item.Path);
                    if (!paths.Add(item.Path) || !File.Exists(p) || new FileInfo(p).Length != item.Length || HashFile(p) != item.Sha256) throw new InvalidDataException("손상된 게임 파일: " + item.Path);
                    bool runtime = item.Path.StartsWith("versions/" + package.Version + "/", StringComparison.Ordinal);
                    bool shared = item.Path == "SwarmAssaultII.exe" || item.Path == "SwarmAssaultII.ico" || item.Path == "distribution.json" || item.Path.StartsWith("licenses/", StringComparison.Ordinal) || item.Path == "플레이-안내.txt";
                    if (!runtime && !shared) throw new InvalidDataException("허용되지 않은 설치 파일입니다.");
                    progress("파일 무결성 확인 중", 60 + ++verified * 25 / package.Files.Count);
                }
                if (Directory.GetFiles(stage, "*", SearchOption.AllDirectories).Length != paths.Count + 1) throw new InvalidDataException("목록에 없는 파일이 있습니다.");
                if (!paths.Contains("SwarmAssaultII.exe") || !paths.Contains("distribution.json") || !paths.Contains("versions/" + package.Version + "/OpenSA.exe")) throw new InvalidDataException("필수 실행 파일이 없습니다.");
                Config config = Read<Config>(Path.Combine(stage, "distribution.json"));
                ValidateConfig(config);
                return package.Version;
            }
            finally { if (File.Exists(zip)) File.Delete(zip); }
        }
        public static void ValidateConfig(Config c)
        {
            if (c.Schema != 1 || c.Product != Product || c.Repository != "ADELIEBEAR/SwarmAssault2" || String.IsNullOrEmpty(c.PublicKeyXml)) throw new InvalidDataException("배포 설정이 올바르지 않습니다.");
            using (var rsa = new RSACryptoServiceProvider()) { rsa.PersistKeyInCsp = false; rsa.FromXmlString(c.PublicKeyXml); if (rsa.PublicOnly == false || rsa.KeySize < 2048) throw new InvalidDataException("공개 키가 올바르지 않습니다."); }
        }
        public static string Install(string exe, string root, string dataRoot, bool register, bool shortcuts, Action<string, int> progress)
        {
            root = Root(root, false);
            if (GameRunning(root)) throw new IOException("실행 중인 게임을 종료한 뒤 다시 설치해 주세요.");
            Directory.CreateDirectory(Path.GetDirectoryName(root));
            string stage = Path.Combine(Path.GetDirectoryName(root), ".sii-stage-" + Guid.NewGuid().ToString("N"));
            try
            {
                string version = Unpack(exe, stage, progress);
                string activePath = Path.Combine(root, "active-version.json");
                Active old = File.Exists(activePath) ? Read<Active>(activePath) : null;
                if (old != null && VersionOf(version) < VersionOf(old.Version)) throw new IOException("이전 버전으로 덮어쓸 수 없습니다.");
                Directory.CreateDirectory(root);
                Config config = Read<Config>(Path.Combine(stage, "distribution.json"));
                if (File.Exists(Path.Combine(root, ".sii-install.json")))
                {
                    Config saved = Read<Config>(Path.Combine(root, ".sii-install.json"));
                    // A downloaded update cannot silently rotate the trusted signing key.
                    if (saved.PublicKeyXml != config.PublicKeyXml) throw new InvalidDataException("업데이트 서명 키가 기존 설치와 다릅니다.");
                    config.DataRoot = saved.DataRoot;
                }
                else if (!String.IsNullOrEmpty(dataRoot)) config.DataRoot = Path.GetFullPath(dataRoot);
                string profilePath = Data(config);
                if (String.Equals(profilePath.TrimEnd('\\'), root, StringComparison.OrdinalIgnoreCase) || profilePath.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase)) throw new IOException("저장 데이터 폴더와 다른 위치에 게임을 설치해 주세요.");
                string dest = Inside(root, "versions/" + version);
                // Versions are immutable. A same-version repair uses a unique slot, never a live folder.
                if (Directory.Exists(dest)) dest = Inside(root, "versions/" + version + "-repair-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Path.GetDirectoryName(dest));
                Directory.Move(Path.Combine(stage, "versions", version), dest);
                foreach (string file in Directory.GetFiles(stage, "*", SearchOption.AllDirectories))
                {
                    string relative = file.Substring(stage.Length + 1).Replace('\\', '/');
                    if (relative == "package.json" || relative == "distribution.json") continue;
                    Atomic(Inside(root, relative), File.ReadAllBytes(file));
                }
                Write(Path.Combine(root, "distribution.json"), config);
                Write(Path.Combine(root, ".sii-install.json"), config);
                if (old != null) Write(Path.Combine(root, "previous-version.json"), old);
                // This is the commit point. Failed extraction/hash checks leave the old pointer untouched.
                Write(activePath, new Active { Version = version, Engine = dest.Substring(root.Length + 1).Replace('\\', '/') });
                progress("설치 마무리 중", 95);
                if (register) Register(root, version);
                if (shortcuts) Shortcuts(root);
                progress("설치 완료", 100);
                return version;
            }
            finally
            {
                // Only this method's generated staging directory is removed.
                if (Directory.Exists(stage)) { NoLinks(stage); Directory.Delete(stage, true); }
            }
        }
        public static Update VerifyUpdate(string text, Config c, string current)
        {
            ValidateConfig(c);
            if (text.Length > 65536) throw new InvalidDataException("업데이트 정보가 너무 큽니다.");
            Signed envelope = Json.Deserialize<Signed>(text); byte[] payload = Convert.FromBase64String(envelope.Payload);
            using (var rsa = new RSACryptoServiceProvider())
            { rsa.PersistKeyInCsp = false; rsa.FromXmlString(c.PublicKeyXml); if (!rsa.VerifyData(payload, CryptoConfig.MapNameToOID("SHA256"), Convert.FromBase64String(envelope.Signature))) throw new InvalidDataException("업데이트 서명이 맞지 않습니다."); }
            Update u = Json.Deserialize<Update>(Encoding.UTF8.GetString(payload));
            Version version = VersionOf(u.Version);
            if (u.Schema != 1 || u.Product != Product || u.Repository != c.Repository || u.FileName != "SwarmAssaultII-Setup-v" + u.Version + ".exe" || u.Length < 1024 || u.Length > 2147483648L || u.Sha256 == null || !Regex.IsMatch(u.Sha256, "^[0-9a-f]{64}$")) throw new InvalidDataException("업데이트 정보가 올바르지 않습니다.");
            return version > VersionOf(current) ? u : null;
        }
        public static HttpClient Client(HttpMessageHandler handler = null)
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            var c = handler == null ? new HttpClient() : new HttpClient(handler);
            c.Timeout = TimeSpan.FromSeconds(12); c.DefaultRequestHeaders.UserAgent.ParseAdd("SwarmAssaultII-Updater/1.0"); return c;
        }
        public static async Task<Update> Check(HttpClient client, Config config, string current, CancellationToken ct)
        {
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
            timeout.CancelAfter(TimeSpan.FromSeconds(12)); ct = timeout.Token;
            using (var response = await client.GetAsync("https://github.com/" + config.Repository + "/releases/latest/download/swarm-update.json", HttpCompletionOption.ResponseHeadersRead, ct))
            {
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength > 65536) throw new InvalidDataException("업데이트 정보가 너무 큽니다.");
                using (var input = await response.Content.ReadAsStreamAsync()) using (var output = new MemoryStream())
                { byte[] b = new byte[4096]; int n; while ((n = await input.ReadAsync(b, 0, b.Length, ct)) > 0) { if (output.Length + n > 65536) throw new InvalidDataException(); output.Write(b, 0, n); } return VerifyUpdate(Encoding.UTF8.GetString(output.ToArray()), config, current); }
            }
            }
        }
        public static async Task Download(HttpClient client, Config config, Update update, string destination, Action<int> progress, CancellationToken ct)
        {
            string partial = destination + ".partial-" + Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(Path.GetDirectoryName(destination));
            try
            {
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                timeout.CancelAfter(TimeSpan.FromMinutes(30)); ct = timeout.Token;
                using (var response = await client.GetAsync("https://github.com/" + config.Repository + "/releases/download/v" + update.Version + "/" + update.FileName, HttpCompletionOption.ResponseHeadersRead, ct))
                {
                    response.EnsureSuccessStatusCode();
                    if (response.Content.Headers.ContentLength.HasValue && response.Content.Headers.ContentLength != update.Length) throw new InvalidDataException("업데이트 파일 크기가 다릅니다.");
                    using (var input = await response.Content.ReadAsStreamAsync()) using (var output = File.Create(partial))
                    { byte[] b = new byte[131072]; int n; long size = 0; while ((n = await input.ReadAsync(b, 0, b.Length, ct)) > 0) { size += n; if (size > update.Length) throw new InvalidDataException(); await output.WriteAsync(b, 0, n, ct); progress((int)(size * 100 / update.Length)); } if (size != update.Length) throw new InvalidDataException("업데이트 다운로드가 중단되었습니다."); }
                }
                }
                if (HashFile(partial) != update.Sha256) throw new InvalidDataException("업데이트 파일이 손상되었습니다.");
                if (File.Exists(destination)) File.Delete(destination);
                File.Move(partial, destination);
            }
            finally { if (File.Exists(partial)) File.Delete(partial); }
        }
        public static void Content(string engine, string profile)
        {
            string catalog = Path.Combine(engine, "content-catalog.json");
            foreach (Entry e in Read<List<Entry>>(catalog))
            {
                string source = Inside(Path.Combine(engine, "Support", "Content", "sa"), e.Path);
                string dest = Inside(Path.Combine(profile, "Content", "sa"), e.Path);
                if (!File.Exists(dest) || new FileInfo(dest).Length != e.Length || HashFile(dest) != e.Sha256) Atomic(dest, File.ReadAllBytes(source));
            }
        }
        public static Process Launch(string root)
        {
            Config config = Read<Config>(Path.Combine(root, "distribution.json"));
            Active active = Read<Active>(Path.Combine(root, "active-version.json"));
            string engine = Inside(root, active.Engine), profile = Data(config);
            Directory.CreateDirectory(profile); Content(engine, profile);
            string settings = Path.Combine(profile, "settings.yaml");
            if (!File.Exists(settings))
            {
                int height = Screen.PrimaryScreen.Bounds.Height;
                string scale = height >= 2160 ? "1.5" : height >= 1440 ? "1.25" : "1";
                File.WriteAllText(settings, "Graphics:\n\tUIScale: " + scale + "\n", new UTF8Encoding(false));
            }
            string arguments = Args("Engine.SupportDir=" + profile, "Game.Mod=sa", "Player.Language=ko", "Game.UsePlayerStanceColors=False", "Graphics.Mode=PseudoFullscreen", "Graphics.FullscreenSize=0,0", "Graphics.ViewportDistance=Medium", "Graphics.MaxFramerate=240", "Graphics.CapFramerate=True", "Graphics.CapFramerateToGameFps=False", "Graphics.VSync=False", "Debug.EnableSimulationPerfLogging=False");
            return Process.Start(new ProcessStartInfo(Path.Combine(engine, "OpenSA.exe"), arguments) { WorkingDirectory = engine, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden });
        }
        public static void Register(string root, string version)
        {
            using (var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\SwarmAssaultII"))
            {
                key.SetValue("DisplayName", "스웜 어썰트 II"); key.SetValue("DisplayVersion", version); key.SetValue("Publisher", "SwarmAssault2 project");
                key.SetValue("InstallLocation", root); key.SetValue("DisplayIcon", Path.Combine(root, "SwarmAssaultII.exe"));
                key.SetValue("UninstallString", Quote(Path.Combine(root, "SwarmAssaultII.exe")) + " --uninstall"); key.SetValue("NoModify", 1); key.SetValue("NoRepair", 1);
            }
        }
        public static void Shortcut(string root, string path, string arguments)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            Type type = Type.GetTypeFromProgID("WScript.Shell"); object shell = Activator.CreateInstance(type);
            object link = type.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { path }); Type lt = link.GetType();
            lt.InvokeMember("TargetPath", BindingFlags.SetProperty, null, link, new object[] { Path.Combine(root, "SwarmAssaultII.exe") });
            lt.InvokeMember("Arguments", BindingFlags.SetProperty, null, link, new object[] { arguments });
            lt.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, link, new object[] { root });
            lt.InvokeMember("Save", BindingFlags.InvokeMethod, null, link, null);
            System.Runtime.InteropServices.Marshal.FinalReleaseComObject(link); System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
        }
        public static void Shortcuts(string root)
        {
            Shortcut(root, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "스웜 어썰트 II.lnk"), "");
            Shortcut(root, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "SwarmAssaultII", "스웜 어썰트 II.lnk"), "");
        }
        public static void Uninstall(string root, bool registered)
        {
            root = Root(root, true);
            if (GameRunning(root)) throw new IOException("게임을 종료한 뒤 삭제해 주세요.");
            // Reject reparse points anywhere before recursive deletion. Save data is external.
            foreach (string d in Directory.GetDirectories(root, "*", SearchOption.AllDirectories)) NoLinks(d);
            foreach (string f in Directory.GetFiles(root, "*", SearchOption.AllDirectories)) NoLinks(f);
            if (registered)
            {
                string reg = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\SwarmAssaultII";
                using (var key = Registry.CurrentUser.OpenSubKey(reg))
                { if (key != null && String.Equals(key.GetValue("InstallLocation") as string, root, StringComparison.OrdinalIgnoreCase)) { key.Close(); Registry.CurrentUser.DeleteSubKeyTree(reg, false); } }
                // Only remove shortcuts that point to this exact installation.
                foreach (string p in new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "스웜 어썰트 II.lnk"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "SwarmAssaultII", "스웜 어썰트 II.lnk") })
                {
                    if (!File.Exists(p)) continue;
                    Type type = Type.GetTypeFromProgID("WScript.Shell"); object shell = Activator.CreateInstance(type);
                    object link = type.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { p });
                    string target = link.GetType().InvokeMember("TargetPath", BindingFlags.GetProperty, null, link, null) as string;
                    System.Runtime.InteropServices.Marshal.FinalReleaseComObject(link); System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
                    if (String.Equals(target, Path.Combine(root, "SwarmAssaultII.exe"), StringComparison.OrdinalIgnoreCase)) File.Delete(p);
                }
            }
            Directory.Delete(root, true);
        }
        public static void Log(string message)
        {
            try { string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Product, "Logs"); Directory.CreateDirectory(folder); File.AppendAllText(Path.Combine(folder, "launcher.log"), DateTime.Now.ToString("s") + " " + message + Environment.NewLine); } catch { }
        }
    }

    public sealed class ShellForm : Form
    {
        readonly bool install; readonly string exe; readonly string root;
        readonly Label status = new Label(); readonly ProgressBar progress = new ProgressBar(); readonly Button action = new Button(); readonly TextBox path = new TextBox();
        readonly CancellationTokenSource cancel = new CancellationTokenSource(); bool busy; bool started;
        public ShellForm(bool isInstaller, string self, string installRoot)
        {
            install = isInstaller; exe = self; root = installRoot;
            Text = "스웜 어썰트 II"; ClientSize = new Size(610, 350); FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen; BackColor = Color.FromArgb(18, 25, 22); ForeColor = Color.FromArgb(233, 226, 199); Font = new Font("맑은 고딕", 10);
            var title = new Label { Text = "SWARM ASSAULT II", Font = new Font("Segoe UI", 25, FontStyle.Bold), AutoSize = true, Location = new Point(30, 28), ForeColor = Color.FromArgb(213, 180, 104) };
            Controls.Add(title);
            Controls.Add(new Label { Text = install ? "스웜 어썰트 II · 플레이어 설치" : "군체 전쟁을 준비합니다", AutoSize = true, Location = new Point(33, 84) });
            path.SetBounds(34, 134, 448, 30); path.Text = installRoot; path.ReadOnly = true; path.BackColor = Color.FromArgb(34, 43, 37); path.ForeColor = ForeColor; Controls.Add(path);
            var browse = new Button { Text = "위치 변경", Location = new Point(490, 133), Size = new Size(88, 30), Visible = install };
            browse.Click += delegate { using (var dialog = new FolderBrowserDialog { Description = "설치할 상위 폴더를 선택하세요" }) if (dialog.ShowDialog() == DialogResult.OK) path.Text = Path.Combine(dialog.SelectedPath, Core.Product); }; Controls.Add(browse);
            status.SetBounds(34, 186, 540, 44); status.Text = install ? "관리자 권한 없이 설치합니다. 저장·캠페인 진행은 별도로 보관됩니다." : "최신 버전 확인 중…"; Controls.Add(status);
            progress.SetBounds(34, 239, 544, 10); Controls.Add(progress);
            action.SetBounds(386, 278, 192, 42); action.Text = install ? "설치하고 시작" : "업데이트 건너뛰고 실행"; action.BackColor = Color.FromArgb(187, 153, 80); action.ForeColor = Color.Black; action.FlatStyle = FlatStyle.Flat; Controls.Add(action);
            action.Click += async delegate { if (install) await InstallNow(); else { cancel.Cancel(); } };
            Shown += async delegate { if (!install) await StartGame(); };
            FormClosing += delegate(object sender, FormClosingEventArgs e) { if (busy) { e.Cancel = true; status.Text = "파일 작업이 끝날 때까지 기다려 주세요."; } else cancel.Cancel(); };
        }
        void Report(string text, int percent) { if (InvokeRequired) BeginInvoke(new Action<string, int>(Report), text, percent); else { status.Text = text; progress.Value = Math.Max(0, Math.Min(100, percent)); } }
        async Task InstallNow()
        {
            action.Enabled = false; busy = true;
            try
            {
                string target = Core.Root(path.Text, false); bool owns;
                using (var mutex = new Mutex(true, Core.MutexName(target), out owns))
                { if (!owns) throw new IOException("게임 실행 또는 다른 설치가 진행 중입니다."); try { await Task.Run(() => Core.Install(exe, target, null, true, true, Report)); } finally { mutex.ReleaseMutex(); } }
                busy = false; Core.Launch(target); Close();
            }
            catch (Exception e) { busy = false; action.Enabled = true; status.Text = e.Message; Core.Log(e.ToString()); }
        }
        async Task StartGame()
        {
            if (started) return; started = true; bool owns;
            using (var mutex = new Mutex(true, Core.MutexName(root), out owns))
            {
                if (!owns) { status.Text = "실행 또는 설치가 이미 진행 중입니다."; action.Enabled = false; return; }
                try
                {
                    Config config = Core.Read<Config>(Path.Combine(root, "distribution.json")); Active active = Core.Read<Active>(Path.Combine(root, "active-version.json"));
                    if (Core.GameRunning(root)) throw new IOException("게임이 이미 실행 중입니다.");
                    try
                    {
                        using (var client = Core.Client())
                        {
                            Update update = await Core.Check(client, config, active.Version, cancel.Token);
                            if (update != null)
                            {
                                Report("새 버전 " + update.Version + " 다운로드 중…", 0);
                                string cached = Path.Combine(Path.GetDirectoryName(Core.Data(config)), "Updates", Guid.NewGuid().ToString("N"), update.FileName);
                                // A slow connection may take minutes; cancellation still works at every chunk.
                                using (var downloadClient = Core.Client()) { downloadClient.Timeout = TimeSpan.FromMinutes(30); await Core.Download(downloadClient, config, update, cached, p => Report("업데이트 다운로드 " + p + "%", p), cancel.Token); }
                                action.Enabled = false; busy = true;
                                Process.Start(new ProcessStartInfo(cached, Core.Args("--silent", "--install-root", root, "--wait-pid", Process.GetCurrentProcess().Id.ToString(), "--launch")) { UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden });
                                busy = false; Close(); return;
                            }
                        }
                    }
                    catch (Exception e) { Core.Log("Update unavailable; using installed version: " + e.Message); }
                    Report("게임 시작 중…", 100); action.Enabled = false; busy = true;
                    await Task.Run(() => Core.Launch(root)); busy = false; Close();
                }
                catch (Exception e) { busy = false; action.Enabled = false; status.Text = e.Message; Core.Log(e.ToString()); }
                finally { mutex.ReleaseMutex(); }
            }
        }
    }

    public static class Program
    {
        static string Option(string[] args, string key) { int i = Array.IndexOf(args, key); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
        [STAThread] public static int Main(string[] args)
        {
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            string self = Assembly.GetExecutingAssembly().Location; string root = Option(args, "--install-root") ?? Path.GetDirectoryName(self);
            try
            {
                string wait = Option(args, "--wait-pid");
                if (wait != null) { try { if (!Process.GetProcessById(Int32.Parse(wait)).WaitForExit(60000)) throw new IOException("이전 실행기가 종료되지 않았습니다."); } catch (ArgumentException) { } }
                if (args.Contains("--uninstall"))
                {
                    Core.Root(root, true);
                    if (MessageBox.Show("게임을 삭제할까요? 저장 데이터와 캠페인 진행은 보관됩니다.", "스웜 어썰트 II", MessageBoxButtons.YesNo) != DialogResult.Yes) return 0;
                    string copy = Path.Combine(Path.GetTempPath(), "SII-uninstall-" + Guid.NewGuid().ToString("N") + ".exe"); File.Copy(self, copy);
                    Process.Start(new ProcessStartInfo(copy, Core.Args("--uninstall-root", root, "--wait-pid", Process.GetCurrentProcess().Id.ToString())) { UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden }); return 0;
                }
                string uninstall = Option(args, "--uninstall-root");
                if (uninstall != null)
                {
                    uninstall = Core.Root(uninstall, true); bool owns;
                    using (var mutex = new Mutex(true, Core.MutexName(uninstall), out owns)) { if (!owns) throw new IOException("게임 또는 설치가 진행 중입니다."); try { Core.Uninstall(uninstall, !args.Contains("--no-register")); } finally { mutex.ReleaseMutex(); } }
                    return 0;
                }
                bool payload = Core.HasPayload(self);
                if (args.Contains("--silent"))
                {
                    if (!payload) throw new InvalidDataException("설치 데이터가 없습니다.");
                    root = Core.Root(Option(args, "--install-root") ?? Core.DefaultRoot, false); bool owns;
                    using (var mutex = new Mutex(true, Core.MutexName(root), out owns))
                    {
                        if (!owns) throw new IOException("게임 또는 설치가 진행 중입니다.");
                        try { Core.Install(self, root, Option(args, "--data-root"), !args.Contains("--no-register"), !args.Contains("--no-shortcuts"), (s, n) => { }); }
                        finally { mutex.ReleaseMutex(); }
                    }
                    if (args.Contains("--launch")) Process.Start(new ProcessStartInfo(Path.Combine(root, "SwarmAssaultII.exe")) { UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden });
                    return 0;
                }
                Application.Run(new ShellForm(payload, self, payload ? Core.DefaultRoot : Core.Root(root, true))); return 0;
            }
            catch (Exception e) { Core.Log(e.ToString()); if (!args.Contains("--silent")) MessageBox.Show(e.Message, "스웜 어썰트 II", MessageBoxButtons.OK, MessageBoxIcon.Error); return 1; }
        }
    }
}
