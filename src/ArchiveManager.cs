using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace CodexHistoryArchive
{
    internal static class Json
    {
        internal static JavaScriptSerializer Serializer() { return new JavaScriptSerializer { MaxJsonLength = 64 * 1024 * 1024, RecursionLimit = 200 }; }
        internal static Dictionary<string, object> Obj(object value) { return value as Dictionary<string, object> ?? new Dictionary<string, object>(); }
        internal static string Text(Dictionary<string, object> value, string key) { object found; return value.TryGetValue(key, out found) && found != null ? Convert.ToString(found) : ""; }
        internal static object Get(Dictionary<string, object> value, string key) { object found; return value.TryGetValue(key, out found) ? found : null; }
        internal static IEnumerable<object> Items(object value) { return (value as IEnumerable<object>) ?? new object[0]; }
    }

    internal sealed class NativeError : Exception
    {
        internal readonly bool Unknown;
        internal readonly bool ActiveWriter;
        internal readonly bool NotFound;
        internal NativeError(string message, bool unknown, bool activeWriter, bool notFound = false) : base(message) { Unknown = unknown; ActiveWriter = activeWriter; NotFound = notFound; }
    }

    internal sealed class NativeClient : IDisposable
    {
        private readonly Process child;
        private readonly StreamWriter input;
        private readonly object gate = new object();
        private readonly Dictionary<int, TaskCompletionSource<Dictionary<string, object>>> pending = new Dictionary<int, TaskCompletionSource<Dictionary<string, object>>>();
        private int serial;
        private bool closing;
        internal readonly string Home;
        internal readonly string Executable;
        internal string Version { get; private set; }
        internal static readonly string[] Sources = { "cli", "vscode", "exec", "appServer", "unknown" };

        internal static List<string> FindExecutables()
        {
            string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenAI", "Codex", "bin");
            var candidates = new List<string>();
            try
            {
                if (Directory.Exists(root))
                    candidates.AddRange(Directory.GetDirectories(root).Select(p => Path.Combine(p, "codex.exe")).Concat(new[] { Path.Combine(root, "codex.exe") }).Where(File.Exists).OrderByDescending(File.GetLastWriteTimeUtc));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            {
                if (String.IsNullOrWhiteSpace(directory)) continue;
                try
                {
                    string candidate = Path.GetFullPath(Path.Combine(Environment.ExpandEnvironmentVariables(directory.Trim().Trim('"')), "codex.exe"));
                    if (File.Exists(candidate)) candidates.Add(candidate);
                }
                catch (ArgumentException) { }
                catch (NotSupportedException) { }
                catch (IOException) { }
                catch (System.Security.SecurityException) { }
            }
            return candidates.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        internal static string FindExecutable()
        {
            string candidate = FindExecutables().FirstOrDefault();
            if (candidate != null) return candidate;
            throw new NativeError("没有找到 codex.exe。请在“设置”中选择已安装的 Codex 程序；工具不会下载或安装 Codex。", false, false);
        }

        internal NativeClient(string home, string executable = null)
        {
            Home = Path.GetFullPath(home);
            if (!Directory.Exists(Home)) throw new NativeError("找不到本地会话目录：" + Home, false, false);
            Executable = String.IsNullOrWhiteSpace(executable) ? FindExecutable() : Path.GetFullPath(executable);
            if (!File.Exists(Executable) || !String.Equals(Path.GetExtension(Executable), ".exe", StringComparison.OrdinalIgnoreCase))
                throw new NativeError("找不到所选的 Codex .exe 程序，请在“设置”中重新选择。", false, false);
            Version = "版本未读取";
            var info = new ProcessStartInfo(Executable, "app-server --stdio") {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = new UTF8Encoding(false), StandardErrorEncoding = new UTF8Encoding(false),
                WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory
            };
            info.EnvironmentVariables["CODEX_HOME"] = Home;
            child = new Process { StartInfo = info, EnableRaisingEvents = true };
            child.OutputDataReceived += OnLine;
            child.ErrorDataReceived += delegate { /* Never persist stderr: it may contain environment details. */ };
            child.Exited += delegate { FailAll(new NativeError("本地会话服务已退出。请刷新列表；刚才未确认的操作不要立即重复。", true, false)); };
            if (!child.Start()) throw new NativeError("无法启动本地会话服务。", false, false);
            input = new StreamWriter(child.StandardInput.BaseStream, new UTF8Encoding(false));
            input.AutoFlush = true;
            child.BeginOutputReadLine();
            child.BeginErrorReadLine();
        }

        private void OnLine(object sender, DataReceivedEventArgs args)
        {
            if (String.IsNullOrWhiteSpace(args.Data)) return;
            try
            {
                var message = Json.Obj(Json.Serializer().DeserializeObject(args.Data));
                object rawId;
                if (!message.TryGetValue("id", out rawId) || rawId == null) return;
                int id;
                if (!Int32.TryParse(Convert.ToString(rawId), out id)) return;
                TaskCompletionSource<Dictionary<string, object>> completion;
                lock (gate) { if (!pending.TryGetValue(id, out completion)) return; pending.Remove(id); }
                completion.TrySetResult(message);
            }
            catch { /* Ignore notifications and malformed non-response lines; requests time out safely. */ }
        }

        private void FailAll(Exception error)
        {
            lock (gate) { foreach (var completion in pending.Values) completion.TrySetException(error); pending.Clear(); }
        }

        internal async Task Initialize()
        {
            await Request("initialize", new { clientInfo = new { name = "codex_history_archiver", title = "Codex History Archiver (unofficial)", version = Program.Version }, capabilities = new { experimentalApi = true } });
            lock (gate) input.WriteLine(Json.Serializer().Serialize(new { method = "initialized" }));
            Version = await ReadVersion();
        }

        private async Task<string> ReadVersion()
        {
            return await Task.Run(async delegate {
                try
                {
                    using (var process = new Process { StartInfo = new ProcessStartInfo(Executable, "--version") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = new UTF8Encoding(false) } })
                    {
                        if (!process.Start()) return "版本未知";
                        Task<string> output = process.StandardOutput.ReadToEndAsync();
                        Task<string> errors = process.StandardError.ReadToEndAsync();
                        if (!process.WaitForExit(5000)) { try { process.Kill(); } catch { } return "版本读取超时"; }
                        string version = (await output).Replace('\r', ' ').Replace('\n', ' ').Trim();
                        await errors; // Drain, but never display or persist stderr.
                        return process.ExitCode == 0 && version.Length > 0 ? version.Substring(0, Math.Min(160, version.Length)) : "版本未知";
                    }
                }
                catch { return "版本未知"; }
            });
        }

        internal async Task<Dictionary<string, object>> Request(string method, object parameters)
        {
            if (method != "initialize" && method != "thread/list" && method != "thread/read" && method != "thread/archive" && method != "thread/unarchive")
                throw new InvalidOperationException("Unsupported operation.");
            int id = Interlocked.Increment(ref serial);
            var completion = new TaskCompletionSource<Dictionary<string, object>>();
            lock (gate)
            {
                if (closing || child.HasExited) throw new NativeError("本地会话服务不可用。请关闭工具后重新打开。", false, false);
                pending.Add(id, completion);
                try { input.WriteLine(Json.Serializer().Serialize(new { id = id, method = method, @params = parameters })); }
                catch { pending.Remove(id); throw new NativeError("写入本地服务失败；本次操作结果尚未确认，请先刷新。", true, false); }
            }
            Task finished = await Task.WhenAny(completion.Task, Task.Delay(30000));
            if (finished != completion.Task)
            {
                lock (gate) pending.Remove(id);
                throw new NativeError("本地服务响应超时，操作结果尚未确认。请刷新并检查已归档列表后再决定，不会自动重试。", true, false);
            }
            var reply = await completion.Task;
            if (reply.ContainsKey("error"))
            {
                var error = Json.Obj(reply["error"]);
                string message = Json.Text(error, "message");
                bool writer = message.IndexOf("active writer", StringComparison.OrdinalIgnoreCase) >= 0;
                if (writer) throw new NativeError("此会话仍被其他 Codex / CLI 进程占用。请等任务完成并正常退出占用程序后再处理。", false, true);
                if (message.IndexOf("not found", StringComparison.OrdinalIgnoreCase) >= 0) throw new NativeError("会话记录或历史文件未找到。请刷新列表核对；工具没有删除任何会话。", false, false, true);
                bool mutation = method == "thread/archive" || method == "thread/unarchive";
                throw new NativeError("本地会话服务返回错误（代码 " + Json.Text(error, "code") + "）。" + (mutation ? "结果尚未确认，已停止批量操作。请刷新并检查两种列表后再决定，不会自动重试。" : "请刷新列表核对。"), mutation, false);
            }
            var result = Json.Get(reply, "result") as Dictionary<string, object>;
            if (result == null) throw new NativeError("Codex 接口返回了不支持的数据格式。请检查版本兼容性；刚才的操作结果需要重新核对。", true, false);
            return result;
        }

        internal async Task<List<HistoryRow>> List(bool archived, Func<bool> cancellationRequested = null)
        {
            var result = new List<HistoryRow>();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var cursors = new HashSet<string>(StringComparer.Ordinal);
            string cursor = null;
            do
            {
                CheckCancellation(cancellationRequested);
                var response = await Request("thread/list", new { archived = archived, cursor = cursor, limit = 100, sourceKinds = Sources, modelProviders = new string[0], sortKey = "updated_at", sortDirection = "desc", useStateDbOnly = true });
                var data = Json.Get(response, "data") as object[];
                if (data == null) throw new NativeError("Codex 未返回有效的会话列表，请检查版本兼容性。", false, false);
                foreach (object item in data)
                {
                    CheckCancellation(cancellationRequested);
                    var metadata = item as Dictionary<string, object>;
                    if (metadata == null || String.IsNullOrWhiteSpace(Json.Text(metadata, "id"))) throw new NativeError("Codex 返回的会话元数据不完整，请检查版本兼容性。", false, false);
                    if (String.IsNullOrEmpty(Json.Text(metadata, "threadSource")))
                    {
                        string itemId = Json.Text(metadata, "id");
                        try
                        {
                            var read = await Request("thread/read", new { threadId = itemId, includeTurns = false });
                            var detail = Json.Obj(Json.Get(read, "thread"));
                            if (Json.Text(detail, "id") != itemId) throw new NativeError("会话元数据校验失败，请刷新列表。", false, false);
                            metadata["threadSource"] = Json.Get(detail, "threadSource");
                            metadata["parentThreadId"] = Json.Get(detail, "parentThreadId");
                            metadata["source"] = Json.Get(detail, "source");
                            if (String.IsNullOrEmpty(Json.Text(metadata, "threadSource"))) throw new NativeError("此 Codex 版本未提供会话来源，无法安全排除后台子任务。请使用兼容版本。", false, false);
                        }
                        catch (NativeError error) { if (error.NotFound) continue; throw; }
                    }
                    var row = HistoryRow.From(metadata);
                    if (row != null && ids.Add(row.Id)) result.Add(row);
                }
                object next = Json.Get(response, "nextCursor");
                if (next != null && !(next is string)) throw new NativeError("Codex 返回的分页信息格式不受支持，请检查版本兼容性。", false, false);
                cursor = Json.Text(response, "nextCursor");
                if (!String.IsNullOrEmpty(cursor) && !cursors.Add(cursor)) throw new NativeError("会话列表分页异常，请关闭工具后重新打开。", false, false);
            } while (!String.IsNullOrEmpty(cursor));
            CheckCancellation(cancellationRequested);
            return result;
        }

        private static void CheckCancellation(Func<bool> requested)
        {
            if (requested != null && requested()) throw new OperationCanceledException();
        }

        internal async Task Verify(string id, bool archived)
        {
            try
            {
                var response = await Request("thread/read", new { threadId = id, includeTurns = false });
                var thread = Json.Obj(Json.Get(response, "thread"));
                if (Json.Text(thread, "id") != id) throw new NativeError("会话元数据校验失败。", true, false);
                string location = Json.Text(thread, "path");
                string expected = Path.GetFullPath(Path.Combine(Home, archived ? "archived_sessions" : "sessions")) + Path.DirectorySeparatorChar;
                bool matches = !String.IsNullOrEmpty(location) && Path.GetFullPath(location).StartsWith(expected, StringComparison.OrdinalIgnoreCase) && File.Exists(location);
                if (!matches)
                {
                    // Native list is the fallback when a later Codex version changes storage layout.
                    var all = await List(archived);
                    if (!all.Any(row => row.Id == id)) throw new NativeError("无法确认归档状态。", true, false);
                }
            }
            catch { throw new NativeError("操作已返回，但未能确认最终状态。已停止批量操作，请刷新并检查两种列表；不要立即重复操作。", true, false); }
        }

        internal async Task CloseAsync()
        {
            lock (gate)
            {
                if (closing) return;
                closing = true;
                try { input.Close(); } catch { }
            }
            await Task.Run(delegate {
                try { if (!child.WaitForExit(6000)) { child.Kill(); child.WaitForExit(3000); } } catch { }
            });
            child.Dispose();
        }

        public void Dispose() { CloseAsync().GetAwaiter().GetResult(); }
    }

    internal sealed class HistoryRow
    {
        internal string Id, Title, Cwd, ProjectId, Provider, Source, Outcome = "";
        internal DateTime Updated;
        internal bool Checked;
        internal static HistoryRow From(Dictionary<string, object> value)
        {
            string id = Json.Text(value, "id"), source = Json.Text(value, "threadSource");
            if (String.IsNullOrEmpty(id) || !String.IsNullOrEmpty(Json.Text(value, "parentThreadId"))) return null;
            if (!String.Equals(source, "user", StringComparison.OrdinalIgnoreCase)) return null;
            string origin = Json.Text(value, "source");
            var originObject = Json.Get(value, "source") as Dictionary<string, object>;
            if (origin.IndexOf("subAgent", StringComparison.OrdinalIgnoreCase) >= 0 || origin.IndexOf("sub_agent", StringComparison.OrdinalIgnoreCase) >= 0 || (originObject != null && originObject.Keys.Any(key => key.IndexOf("subAgent", StringComparison.OrdinalIgnoreCase) >= 0 || key.IndexOf("sub_agent", StringComparison.OrdinalIgnoreCase) >= 0))) return null;
            string title = Json.Text(value, "name");
            if (String.IsNullOrWhiteSpace(title)) title = Json.Text(value, "title");
            if (String.IsNullOrWhiteSpace(title)) title = Json.Text(value, "preview");
            title = title.Replace('\r', ' ').Replace('\n', ' ').Trim();
            if (String.IsNullOrEmpty(title)) title = "（无标题会话）";
            long updated;
            Int64.TryParse(Json.Text(value, "updatedAt"), out updated);
            DateTime time = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            try { time = time.AddSeconds(updated).ToLocalTime(); } catch { }
            return new HistoryRow { Id = id, Title = title, Cwd = Json.Text(value, "cwd"), ProjectId = Json.Text(value, "projectId"), Provider = Json.Text(value, "modelProvider"), Source = source, Updated = time };
        }
    }

    internal static class DesktopGuard
    {
        internal static bool IsDesktopRunning(string knownCliPath = null)
        {
            foreach (string name in new[] { "ChatGPT", "Codex", "OpenAI Codex" })
            {
                foreach (Process process in Process.GetProcessesByName(name))
                {
                    using (process)
                    {
                        try
                        {
                            string path = process.MainModule.FileName;
                            if (String.Equals(path, knownCliPath, StringComparison.OrdinalIgnoreCase)) continue;
                            if (path.IndexOf("OpenAI\\Codex\\bin\\", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                            if (path.IndexOf("OpenAI.Codex", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                            var version = FileVersionInfo.GetVersionInfo(path);
                            string product = (version.ProductName ?? "") + " " + (version.FileDescription ?? "");
                            bool electron = (version.OriginalFilename ?? "").IndexOf("electron", StringComparison.OrdinalIgnoreCase) >= 0;
                            bool codexProduct = product.IndexOf("Codex", StringComparison.OrdinalIgnoreCase) >= 0 && product.IndexOf("CLI", StringComparison.OrdinalIgnoreCase) < 0;
                            if (codexProduct || (electron && name != "ChatGPT") || (name == "ChatGPT" && path.IndexOf("OpenAI\\Codex", StringComparison.OrdinalIgnoreCase) >= 0)) return true;
                        }
                        catch (InvalidOperationException) { /* Process exited between enumeration and inspection. */ }
                        catch { return true; } // An uninspectable candidate must block writes.
                    }
                }
            }
            return false;
        }
    }

    internal sealed class LocalSettings
    {
        internal string Home, Executable;
        internal static string DataDirectory { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexHistoryArchiver"); } }
        internal static string DefaultHome()
        {
            string value = Environment.GetEnvironmentVariable("CODEX_HOME");
            return String.IsNullOrWhiteSpace(value) ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex") : value;
        }
        internal static LocalSettings Load()
        {
            var settings = new LocalSettings { Home = DefaultHome() };
            try
            {
                string path = Path.Combine(DataDirectory, "settings.json");
                if (File.Exists(path))
                {
                    var data = Json.Obj(Json.Serializer().DeserializeObject(File.ReadAllText(path, Encoding.UTF8)));
                    string home = Json.Text(data, "home"), executable = Json.Text(data, "executable");
                    if (!String.IsNullOrWhiteSpace(home)) settings.Home = home;
                    if (!String.IsNullOrWhiteSpace(executable)) settings.Executable = executable;
                }
            }
            catch { /* A damaged settings file falls back to defaults; history is never changed. */ }
            return settings;
        }
        internal void Save()
        {
            Directory.CreateDirectory(DataDirectory);
            string path = Path.Combine(DataDirectory, "settings.json"), temporary = Path.Combine(DataDirectory, "settings-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                File.WriteAllText(temporary, Json.Serializer().Serialize(new { home = Home, executable = Executable }), new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }

    internal sealed class SettingsDialog : Form
    {
        private readonly TextBox home = new TextBox { Dock = DockStyle.Fill };
        private readonly TextBox executable = new TextBox { Dock = DockStyle.Fill };
        internal string HomePath { get; private set; }
        internal string ExecutablePath { get; private set; }

        internal SettingsDialog(string selectedHome, string selectedExecutable)
        {
            Text = "本地路径设置"; StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false;
            Font = new Font("Microsoft YaHei UI", 10F); ClientSize = new Size(760, 285);
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18), ColumnCount = 2, RowCount = 6 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            Controls.Add(layout);
            var homeLabel = new Label { Text = "会话目录（CODEX_HOME）", AutoSize = true }; layout.Controls.Add(homeLabel, 0, 0); layout.SetColumnSpan(homeLabel, 2);
            home.Text = selectedHome; layout.Controls.Add(home, 0, 1);
            var browseHome = new Button { Text = "选择目录", AutoSize = true }; layout.Controls.Add(browseHome, 1, 1);
            var exeLabel = new Label { Text = "Codex 程序（留空时自动查找已安装的 codex.exe）", AutoSize = true }; layout.Controls.Add(exeLabel, 0, 2); layout.SetColumnSpan(exeLabel, 2);
            executable.Text = selectedExecutable ?? ""; layout.Controls.Add(executable, 0, 3);
            var browseExe = new Button { Text = "选择程序", AutoSize = true }; layout.Controls.Add(browseExe, 1, 3);
            var note = new Label { Text = "仅选择你已安装并信任的 Codex 程序。工具会启动该程序的本地会话服务。\r\n设置保存在此 Windows 用户的本地应用数据目录，不会上传。", Dock = DockStyle.Fill, ForeColor = Color.DimGray };
            layout.Controls.Add(note, 0, 4); layout.SetColumnSpan(note, 2);
            var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill };
            var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, AutoSize = true };
            var save = new Button { Text = "应用", AutoSize = true };
            var defaults = new Button { Text = "恢复默认路径", AutoSize = true };
            buttons.Controls.Add(cancel); buttons.Controls.Add(save); buttons.Controls.Add(defaults); layout.Controls.Add(buttons, 0, 5); layout.SetColumnSpan(buttons, 2);
            AcceptButton = save; CancelButton = cancel;
            browseHome.Click += delegate { using (var picker = new FolderBrowserDialog { Description = "选择已有的 CODEX_HOME 目录", SelectedPath = home.Text, ShowNewFolderButton = false }) if (picker.ShowDialog(this) == DialogResult.OK) home.Text = picker.SelectedPath; };
            browseExe.Click += delegate { using (var picker = new OpenFileDialog { Title = "选择已安装的 codex.exe", Filter = "Codex 程序 (codex.exe)|codex.exe|可执行程序 (*.exe)|*.exe", CheckFileExists = true }) if (picker.ShowDialog(this) == DialogResult.OK) executable.Text = picker.FileName; };
            defaults.Click += delegate { home.Text = LocalSettings.DefaultHome(); executable.Text = ""; };
            save.Click += delegate {
                try
                {
                    string selected = Path.GetFullPath(home.Text.Trim());
                    string cli = String.IsNullOrWhiteSpace(executable.Text) ? null : Path.GetFullPath(executable.Text.Trim());
                    if (!Directory.Exists(selected)) throw new ArgumentException();
                    if (cli != null && (!File.Exists(cli) || !String.Equals(Path.GetExtension(cli), ".exe", StringComparison.OrdinalIgnoreCase))) throw new ArgumentException();
                    HomePath = selected; ExecutablePath = cli; DialogResult = DialogResult.OK; Close();
                }
                catch { MessageBox.Show(this, "请选择已存在的会话目录和有效的 .exe 程序；程序路径也可以留空以自动查找。", "路径无效", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            };
        }
    }

    internal sealed class ManagerForm : Form
    {
        private readonly TextBox search = new TextBox();
        private readonly CheckBox archived = new CheckBox { Text = "显示已归档（可恢复）", AutoSize = true };
        private readonly Button refresh = new Button { Text = "刷新列表", AutoSize = true };
        private readonly Button selectAll = new Button { Text = "全选当前结果", AutoSize = true };
        private readonly Button selectHighlighted = new Button { Text = "勾选高亮行", AutoSize = true };
        private readonly Button clear = new Button { Text = "清空勾选", AutoSize = true };
        private readonly Button execute = new Button { Text = "归档勾选会话", AutoSize = true };
        private readonly Button settings = new Button { Text = "设置", AutoSize = true };
        private readonly Button stop = new Button { Text = "停止", AutoSize = true, Enabled = false };
        private readonly Label paths = new Label { Dock = DockStyle.Fill, AutoEllipsis = true, ForeColor = Color.DimGray };
        private readonly ToolTip pathTooltip = new ToolTip();
        private readonly Label counts = new Label { AutoSize = true, Padding = new Padding(10, 7, 0, 0) };
        private readonly Label status = new Label { Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
        private readonly DataGridView grid = new DataGridView();
        private List<HistoryRow> all = new List<HistoryRow>();
        private NativeClient client;
        private bool busy, closeReady, closing, stopRequested;
        private string home, executable;
        private readonly string logDirectory;

        internal ManagerForm(string chosenHome, string chosenExecutable = null)
        {
            home = chosenHome;
            executable = chosenExecutable;
            logDirectory = Path.Combine(LocalSettings.DataDirectory, "Logs");
            Text = "Codex History Archiver · 非官方预览版 " + Program.Version;
            Font = new Font("Microsoft YaHei UI", 10F);
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(1250, 810);
            MinimumSize = new Size(1100, 650);
            AutoScaleMode = AutoScaleMode.Dpi;
            BackColor = Color.FromArgb(246, 248, 251);
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 7, Padding = new Padding(20) };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 70));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 75));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            Controls.Add(layout);
            layout.Controls.Add(new Label { Text = "批量整理本地历史会话", Dock = DockStyle.Fill, Font = new Font(Font.FontFamily, 17F, FontStyle.Bold) }, 0, 0);
            layout.Controls.Add(new Label { Text = "先等 Codex 中的任务完成，再通过应用菜单完全退出 Codex / ChatGPT Work，然后在此勾选归档。\r\n非官方工具，使用 Codex 本地接口；支持本地会话，不修复客户端原有的归档按钮。归档后可在此恢复。", Dock = DockStyle.Fill, ForeColor = Color.FromArgb(70, 81, 99) }, 0, 1);
            layout.Controls.Add(paths, 0, 2); UpdatePaths();
            var filter = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
            filter.Controls.Add(new Label { Text = "搜索标题 / ID：", AutoSize = true, Padding = new Padding(0, 6, 0, 0) });
            search.Width = 370;
            filter.Controls.Add(search);
            archived.Margin = new Padding(16, 6, 8, 0);
            filter.Controls.Add(archived); filter.Controls.Add(refresh); filter.Controls.Add(settings);
            layout.Controls.Add(filter, 0, 3);
            var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
            foreach (var button in new[] { selectAll, selectHighlighted, clear, execute, stop }) { button.Padding = new Padding(10, 4, 10, 4); actions.Controls.Add(button); }
            execute.BackColor = Color.FromArgb(32, 90, 174); execute.ForeColor = Color.White; execute.FlatStyle = FlatStyle.Flat;
            actions.Controls.Add(counts); layout.Controls.Add(actions, 0, 4);
            grid.Dock = DockStyle.Fill; grid.BackgroundColor = Color.White; grid.BorderStyle = BorderStyle.FixedSingle;
            grid.AutoGenerateColumns = false; grid.AllowUserToAddRows = false; grid.AllowUserToDeleteRows = false;
            grid.AllowUserToResizeRows = false; grid.RowHeadersVisible = false; grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            grid.MultiSelect = true; grid.RowTemplate.Height = 33; grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None;
            grid.ColumnHeadersHeight = 38; grid.EnableHeadersVisualStyles = false; grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(231, 236, 244);
            grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(218, 231, 249); grid.DefaultCellStyle.SelectionForeColor = Color.Black;
            grid.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(248, 250, 253);
            grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "checked", HeaderText = "选", Width = 42 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "title", HeaderText = "会话标题", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, MinimumWidth = 300, ReadOnly = true });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "updated", HeaderText = "最后更新", Width = 152, ReadOnly = true });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "project", HeaderText = "工作目录", Width = 170, ReadOnly = true });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "provider", HeaderText = "模型服务", Width = 100, ReadOnly = true });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "outcome", HeaderText = "本次结果", Width = 145, ReadOnly = true });
            layout.Controls.Add(grid, 0, 5); layout.Controls.Add(status, 0, 6);
            search.TextChanged += delegate { Filter(true); };
            archived.CheckedChanged += async delegate { execute.Text = archived.Checked ? "恢复勾选会话" : "归档勾选会话"; await RefreshData(); };
            refresh.Click += async delegate { await RefreshData(); };
            selectAll.Click += delegate { SetChecks(true, false); };
            selectHighlighted.Click += delegate { SetChecks(true, true); };
            clear.Click += delegate { SetChecks(false, false); };
            execute.Click += async delegate { await ExecuteSelected(); };
            settings.Click += async delegate { await ChangeSettings(); };
            stop.Click += delegate { stopRequested = true; stop.Enabled = false; status.Text = "已请求停止。当前请求及状态核对完成后停止，剩余会话不会继续处理。"; };
            grid.CurrentCellDirtyStateChanged += delegate { if (grid.IsCurrentCellDirty) grid.CommitEdit(DataGridViewDataErrorContexts.Commit); };
            grid.CellValueChanged += delegate(object sender, DataGridViewCellEventArgs e) { if (e.RowIndex >= 0 && e.ColumnIndex == 0 && grid.Rows[e.RowIndex].Tag is HistoryRow) { ((HistoryRow)grid.Rows[e.RowIndex].Tag).Checked = Convert.ToBoolean(grid.Rows[e.RowIndex].Cells[0].Value); UpdateCounts(); } };
            Shown += async delegate { await RefreshData(); };
            FormClosing += OnClosing;
        }

        private void SetBusy(bool value, string message, bool canStop = true)
        {
            busy = value;
            if (value) stopRequested = false;
            foreach (Control control in new Control[] { search, archived, refresh, selectAll, selectHighlighted, clear, execute, settings, grid }) control.Enabled = !value;
            stop.Enabled = value && canStop;
            status.Text = message;
            UseWaitCursor = value;
        }

        private void UpdatePaths()
        {
            string cli = client != null ? client.Executable : (String.IsNullOrWhiteSpace(executable) ? "自动查找（已安装的 Codex / PATH）" : executable);
            paths.Text = "会话目录：" + home + "\r\nCodex 程序：" + cli + "\r\n版本：" + (client != null ? client.Version : "连接后显示") + " · 工具 " + Program.Version;
            pathTooltip.SetToolTip(paths, paths.Text);
        }

        private async Task ChangeSettings()
        {
            if (busy) return;
            using (var dialog = new SettingsDialog(home, executable))
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                SetBusy(true, "正在切换本地路径……", false);
                if (client != null) { await client.CloseAsync(); client = null; }
                home = dialog.HomePath; executable = dialog.ExecutablePath;
                bool saved = true;
                try { new LocalSettings { Home = home, Executable = executable }.Save(); } catch { saved = false; }
                UpdatePaths(); SetBusy(false, "路径已切换。");
                await RefreshData();
                if (!saved) MessageBox.Show(this, "新路径已用于本次运行，但本地设置保存失败。下次启动时请重新选择。", "设置未保存", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void UpdateCounts() { counts.Text = String.Format("显示 {0} / {1} 条 · 已勾选 {2} 条", grid.Rows.Count, all.Count, all.Count(r => r.Checked)); }

        private void Filter(bool clearChecks)
        {
            if (busy) return;
            if (clearChecks) foreach (var item in all) item.Checked = false;
            grid.Rows.Clear();
            string term = search.Text.Trim();
            foreach (var item in all)
            {
                if (term.Length > 0 && item.Title.IndexOf(term, StringComparison.OrdinalIgnoreCase) < 0 && item.Id.IndexOf(term, StringComparison.OrdinalIgnoreCase) < 0) continue;
                string directory = item.Cwd.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string label = String.IsNullOrEmpty(directory) ? "（未记录）" : Path.GetFileName(directory);
                int index = grid.Rows.Add(item.Checked, item.Title, item.Updated.ToString("yyyy-MM-dd HH:mm"), label, item.Provider, item.Outcome);
                grid.Rows[index].Tag = item;
                grid.Rows[index].Cells[1].ToolTipText = item.Title + "\r\nID：" + item.Id;
                grid.Rows[index].Cells[3].ToolTipText = item.Cwd + (String.IsNullOrEmpty(item.ProjectId) ? "" : "\r\n项目 ID：" + item.ProjectId);
                grid.Rows[index].Cells[5].ToolTipText = item.Outcome;
            }
            grid.ClearSelection();
            UpdateCounts();
        }

        private void SetChecks(bool value, bool highlightedOnly)
        {
            foreach (DataGridViewRow row in grid.Rows)
                if (!highlightedOnly || row.Selected) { ((HistoryRow)row.Tag).Checked = value; row.Cells[0].Value = value; }
            UpdateCounts();
        }

        private async Task EnsureClient()
        {
            if (client == null)
            {
                var created = new NativeClient(home, executable);
                Exception failure = null;
                try { await created.Initialize(); client = created; UpdatePaths(); }
                catch (Exception error) { failure = error; }
                if (failure != null) { await created.CloseAsync(); throw failure; }
            }
        }

        private async Task RefreshData()
        {
            if (busy) return;
            SetBusy(true, "正在读取本地会话列表（不读取对话正文）……");
            all = new List<HistoryRow>(); grid.Rows.Clear(); UpdateCounts();
            Exception failure = null;
            string message;
            try
            {
                await EnsureClient();
                all = await client.List(archived.Checked, delegate { return stopRequested; });
                message = "列表已刷新。改变筛选条件会清空勾选；按住 Ctrl / Shift 可多选高亮行。" + (DesktopGuard.IsDesktopRunning(client.Executable) ? "归档前请完全退出 Codex。" : "");
            }
            catch (OperationCanceledException) { message = "读取已停止。点击“刷新列表”重新读取完整列表。"; }
            catch (Exception error)
            {
                message = "读取失败；请在“设置”中检查 Codex 程序和会话目录。";
                failure = error;
            }
            if (failure != null && client != null) { await client.CloseAsync(); client = null; UpdatePaths(); }
            SetBusy(false, message); Filter(false);
            if (failure != null) MessageBox.Show(this, Friendly(failure), "无法加载", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        private static string Friendly(Exception error)
        {
            var known = error as NativeError;
            return known != null ? known.Message : "操作遇到本地错误。请关闭工具后重试；若刚执行过归档，请先刷新并检查已归档列表。";
        }

        private void Log(HistoryRow row, string action, string outcome)
        {
            Directory.CreateDirectory(logDirectory);
            string line = Json.Serializer().Serialize(new { time = DateTimeOffset.Now.ToString("o"), id = row.Id, action = action, result = outcome });
            File.AppendAllText(Path.Combine(logDirectory, DateTime.Today.ToString("yyyy-MM-dd") + ".jsonl"), line + Environment.NewLine, new UTF8Encoding(false));
        }

        private async Task ExecuteSelected()
        {
            if (busy) return;
            grid.EndEdit();
            var chosen = all.Where(item => item.Checked).ToList();
            if (chosen.Count == 0) { MessageBox.Show(this, "请先勾选要处理的会话。", "尚未选择", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            if (DesktopGuard.IsDesktopRunning(client != null ? client.Executable : executable))
            {
                MessageBox.Show(this, "检测到 Codex / ChatGPT Work 桌面程序仍在运行。\r\n\r\n请先等其他任务完成，再从应用菜单选择退出，保持本工具打开，然后重新点击此按钮。仅关闭当前聊天或最小化窗口不算退出。\r\n\r\n工具不会结束任何其他程序，也尚未归档这些会话。", "请先完全退出桌面应用", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            bool restore = archived.Checked;
            string verb = restore ? "恢复" : "归档";
            string preview = String.Join("\r\n", chosen.Take(12).Select(item => "• " + (item.Title.Length > 75 ? item.Title.Substring(0, 75) + "…" : item.Title)));
            if (chosen.Count > 12) preview += "\r\n……另有 " + (chosen.Count - 12) + " 条。";
            string confirm = String.Format("确定{0}已勾选的 {1} 条会话吗？\r\n\r\n{2}\r\n\r\n{3}", verb, chosen.Count, preview, restore ? "恢复后会回到未归档历史列表。" : "列表也包含项目中的会话，请核对标题和工作目录。归档会将整条会话移到已归档列表，之后可恢复。不会删除聊天正文或项目文件。" );
            if (MessageBox.Show(this, confirm, verb + "确认", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            // Recheck after confirmation because the user may have reopened the desktop app.
            if (DesktopGuard.IsDesktopRunning(client != null ? client.Executable : executable)) { MessageBox.Show(this, "桌面应用已重新运行，操作已取消。请退出后再执行。", "未开始", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            SetBusy(true, "准备" + verb + "……");
            int succeeded = 0, failed = 0, uncertain = 0;
            string lastFailure = "";
            try
            {
                Directory.CreateDirectory(logDirectory);
                string probe = Path.Combine(logDirectory, ".write-check-" + Guid.NewGuid().ToString("N"));
                File.WriteAllText(probe, ""); File.Delete(probe);
                await EnsureClient();
                for (int i = 0; i < chosen.Count; i++)
                {
                    if (stopRequested) break;
                    var item = chosen[i];
                    if (DesktopGuard.IsDesktopRunning(client.Executable)) { lastFailure = "桌面应用已重新运行，剩余会话未处理。"; break; }
                    status.Text = String.Format("正在{0} {1} / {2}：{3}", verb, i + 1, chosen.Count, item.Title);
                    try
                    {
                        await client.Request(restore ? "thread/unarchive" : "thread/archive", new { threadId = item.Id });
                        await client.Verify(item.Id, !restore);
                        item.Outcome = restore ? "已恢复并确认" : "已归档并确认";
                        item.Checked = false;
                        succeeded++;
                        try { Log(item, restore ? "restore" : "archive", "confirmed"); }
                        catch { lastFailure = "归档状态已确认，但运行记录写入失败。剩余会话未处理。"; break; }
                    }
                    catch (Exception error)
                    {
                        var native = error as NativeError;
                        bool unknown = native == null || native.Unknown;
                        item.Outcome = unknown ? "结果待确认（停止）" : (native.ActiveWriter ? "被其他进程占用" : "未成功");
                        if (unknown) uncertain++; else failed++;
                        lastFailure = Friendly(error);
                        try { Log(item, restore ? "restore" : "archive", unknown ? "unconfirmed" : (native.ActiveWriter ? "active_writer" : "failed")); } catch { lastFailure += "\r\n运行记录写入失败，剩余会话未处理。"; break; }
                        if (unknown) break;
                    }
                }
            }
            catch (Exception error) { lastFailure = Friendly(error); }
            finally
            {
                int remaining = chosen.Count - succeeded - failed - uncertain;
                string summary = String.Format("{0}{1}：已确认 {2} 条，失败 {3} 条，待确认 {4} 条，未处理 {5} 条。", verb, stopRequested ? "已停止" : "处理结束", succeeded, failed, uncertain, remaining);
                SetBusy(false, summary);
                Filter(false);
                MessageBox.Show(this, summary + (String.IsNullOrEmpty(lastFailure) ? "" : "\r\n\r\n" + lastFailure) + "\r\n\r\n点击“刷新列表”查看当前状态；已确认成功的会话会在刷新后从当前列表移走。", "操作结果", MessageBoxButtons.OK, uncertain + failed > 0 || !String.IsNullOrEmpty(lastFailure) ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
            }
        }

        private async void OnClosing(object sender, FormClosingEventArgs e)
        {
            if (closeReady) return;
            e.Cancel = true;
            if (closing) return;
            if (busy) { MessageBox.Show(this, "请先点击“停止”，等当前请求及状态核对完成后再关闭。", "正在处理", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            closing = true; SetBusy(true, "正在关闭本工具启动的本地服务……", false);
            if (client != null) await client.CloseAsync();
            pathTooltip.Dispose();
            closeReady = true; Close();
        }
    }

    internal static class Program
    {
        internal const string Version = "0.1.0-preview";

        [STAThread]
        private static int Main(string[] args)
        {
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            try
            {
                var settings = LocalSettings.Load();
                for (int i = 0; i < args.Length; i++)
                {
                    if ((args[i] != "--home" && args[i] != "--codex-exe") || i + 1 >= args.Length)
                    {
                        MessageBox.Show("用法：CodexHistoryArchiver.exe [--home <已有会话目录>] [--codex-exe <已有程序.exe>]\r\n\r\n也可以直接打开工具，然后在“设置”中选择路径。命令行不提供归档或删除操作。", "启动参数", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        return 2;
                    }
                    string option = args[i++], value = Path.GetFullPath(args[i]);
                    if (option == "--home")
                    {
                        if (!Directory.Exists(value)) throw new ArgumentException();
                        settings.Home = value;
                    }
                    else
                    {
                        if (!File.Exists(value) || !String.Equals(Path.GetExtension(value), ".exe", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException();
                        settings.Executable = value;
                    }
                }
                Application.Run(new ManagerForm(settings.Home, settings.Executable)); return 0;
            }
            catch (Exception)
            {
                MessageBox.Show("工具未能启动。请检查启动参数是否为已存在的会话目录和 .exe 程序，并将工具完整解压到本地目录。", "启动失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
        }
    }
}
