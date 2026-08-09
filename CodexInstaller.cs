// CodexInstaller — OpenAI Codex CLI 一键安装（国内网络优化版，纯 C#）
// 功能：
//   1. Node.js 检测 / npmmirror 镜像静默安装（如缺失），node 目录注入进程 PATH
//   2. npm 镜像（registry.npmmirror.com）+ 全局安装 @openai/codex（失败自动重试）
//   3. 聚合 API 配置：~/.codex/config.toml（model / openai_base_url / [windows] sandbox）+ setx OPENAI_API_KEY
//   4. 验证 codex 命令可用
// 编译：build-exe.bat（本机 .NET Framework csc，无第三方依赖）
using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace CodexInstaller
{
    internal static class Program
    {
        private static bool SkipApi, SkipNodeJs;
        private static string NodeVersion = "v24.19.0";
        private static string ApiBaseUrl = "";
        private static string ApiKey = "";
        private static string ApiModel = "";
        private const string NpmRegistryMirror = "https://registry.npmmirror.com";
        private const string NodeMirrorBase = "https://npmmirror.com/mirrors/node";
        private const string DefaultModel = "gpt-5.6-sol";   // 默认模型（Codex 专属）
        private const string DefaultApiBaseUrl = "https://n.tokeness.io/v1";   // 默认聚合端点（OpenAI 兼容）

        [STAThread]
        private static int Main(string[] args)
        {
            try { Console.Title = "Codex Installer"; } catch { }
            foreach (string a in args)
            {
                string arg = a; string val = "";
                int eq = a.IndexOf('=');
                if (eq > 0) { arg = a.Substring(0, eq); val = a.Substring(eq + 1); }
                if (arg == "-h" || arg == "-?" || arg == "--help") { PrintUsage(); Pause(); return 0; }
                if (arg == "-SkipApi") SkipApi = true;
                else if (arg == "-SkipNodeJs") SkipNodeJs = true;
                else if (arg == "-NodeVersion") NodeVersion = eq > 0 ? val.Trim() : NextArg(args, a).Trim();
                else if (arg == "-ApiBaseUrl") ApiBaseUrl = (eq > 0 ? val : NextArg(args, a)).Trim();
                else if (arg == "-ApiKey") ApiKey = (eq > 0 ? val : NextArg(args, a)).Trim();
                else if (arg == "-ApiModel") ApiModel = (eq > 0 ? val : NextArg(args, a)).Trim();
            }

            // 管理员（Node 安装 / setx 系统级需要；npm 全局为用户级）
            if (!IsAdministrator())
            {
                Console.WriteLine("请求管理员权限，请在 UAC 弹窗中点击“是”...");
                try
                {
                    ProcessStartInfo psi = new ProcessStartInfo();
                    psi.UseShellExecute = true;
                    psi.Verb = "runas";
                    psi.FileName = Process.GetCurrentProcess().MainModule.FileName;
                    StringBuilder sb = new StringBuilder();
                    foreach (string a in args) sb.Append(" \"").Append(a.Replace("\"", "\\\"")).Append("\"");
                    psi.Arguments = sb.ToString();
                    Process.Start(psi);
                    return 0;
                }
                catch (Exception ex) { Console.WriteLine("提权失败: " + ex.Message); Pause(); return 3; }
            }

            try { ServicePointManagerTls(); } catch { }

            try
            {
                Banner();
                EnsureNode();
                string codexCmd = InstallCodex();
                ConfigureApi();
                Verify(codexCmd);
                Summary();
            }
            catch (Exception ex)
            {
                Fail("执行失败: " + ex.Message);
                Console.WriteLine(ex.StackTrace);
                Pause();
                return 1;
            }
            Pause();
            return 0;
        }

        private static string NextArg(string[] args, string current)
        {
            for (int i = 0; i < args.Length; i++)
                if (args[i] == current && i + 1 < args.Length) return args[i + 1];
            return "";
        }

        // ================= 工具 =================
        private static void Step(string msg) { Console.WriteLine("\n==> " + msg); }
        private static void Ok(string msg) { Console.WriteLine("  [OK] " + msg); }
        private static void Warn(string msg) { Console.WriteLine("  [!!] " + msg); }
        private static void Fail(string msg) { Console.WriteLine("  [XX] " + msg); }
        private static void Banner()
        {
            Console.WriteLine("===============================================");
            Console.WriteLine(" OpenAI Codex CLI 一键安装程序（国内网络优化版）");
            Console.WriteLine("===============================================");
        }
        private static void Pause()
        {
            try
            {
                if (Console.IsInputRedirected) return;
                Console.WriteLine("\n按任意键退出...");
                Console.ReadKey(true);
            }
            catch { }
        }
        private static bool IsAdministrator()
        {
            try
            {
                using (System.Security.Principal.WindowsIdentity id = System.Security.Principal.WindowsIdentity.GetCurrent())
                    return new System.Security.Principal.WindowsPrincipal(id).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }
        private static void ServicePointManagerTls()
        {
            System.Net.ServicePointManager.SecurityProtocol |= System.Net.SecurityProtocolType.Tls12;
        }
        private static string SafeReadLine()
        {
            try { return Console.ReadLine() ?? ""; }
            catch { return ""; }
        }

        // ================= Node.js =================
        private static void EnsureNodeInPath(string nodeExe)
        {
            try
            {
                string nodeDir = Path.GetDirectoryName(nodeExe);
                if (string.IsNullOrEmpty(nodeDir)) return;
                string pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
                if (pathEnv.IndexOf(nodeDir, StringComparison.OrdinalIgnoreCase) < 0)
                    Environment.SetEnvironmentVariable("PATH", nodeDir + ";" + pathEnv);
            }
            catch { }
        }

        private static string FindNodeExe()
        {
            string programData = Environment.GetEnvironmentVariable("ProgramData");
            if (string.IsNullOrEmpty(programData)) programData = @"C:\ProgramData";
            string[] fixedPaths = {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs", "node.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "nodejs", "node.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "scoop", "shims", "node.exe"),
                Path.Combine(programData, "chocolatey", "bin", "node.exe")
            };
            foreach (string c in fixedPaths)
                if (!string.IsNullOrEmpty(c) && File.Exists(c)) return c;
            try
            {
                using (Microsoft.Win32.RegistryKey key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\node.exe"))
                {
                    if (key != null)
                    {
                        string v = key.GetValue("") as string;
                        if (!string.IsNullOrEmpty(v) && File.Exists(v)) return v;
                    }
                }
            }
            catch { }
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("where.exe", "node");
                psi.UseShellExecute = false; psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true; psi.RedirectStandardError = true;
                Process p = Process.Start(psi);
                string outStr = p.StandardOutput.ReadToEnd();
                p.WaitForExit();
                if (p.ExitCode == 0)
                    foreach (string line in outStr.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        string t = line.Trim();
                        if (File.Exists(t)) return t;
                    }
            }
            catch { }
            string path = Environment.GetEnvironmentVariable("PATH");
            if (path != null)
                foreach (string dir in path.Split(';'))
                {
                    string d = dir.Trim().Trim('"');
                    if (string.IsNullOrEmpty(d) || !Directory.Exists(d)) continue;
                    string cand = Path.Combine(d, "node.exe");
                    if (File.Exists(cand)) return cand;
                }
            return null;
        }

        private static void EnsureNode()
        {
            string nodeExe = FindNodeExe();
            if (nodeExe != null)
            {
                EnsureNodeInPath(nodeExe);
                Ok("Node.js 已安装: " + nodeExe);
                return;
            }
            if (SkipNodeJs) { Warn("系统缺少 Node.js 且已指定 -SkipNodeJs，无法安装 Codex，中止"); throw new Exception("缺少 Node.js"); }
            Step("安装 Node.js " + NodeVersion + "（npmmirror 镜像）");
            string url = NodeMirrorBase + "/" + NodeVersion + "/node-" + NodeVersion + "-x64.msi";
            string msi = Path.Combine(Path.GetTempPath(), "node-setup.msi");
            if (!DownloadFile(url, msi, "Node.js " + NodeVersion + " MSI")) throw new Exception("Node.js 下载失败");
            Console.WriteLine("  静默安装中（msiexec /qn）...");
            ProcessStartInfo psi = new ProcessStartInfo("msiexec.exe", "/i \"" + msi + "\" /qn /norestart");
            psi.UseShellExecute = false; psi.CreateNoWindow = true;
            Process p = Process.Start(psi);
            p.WaitForExit();
            try { File.Delete(msi); } catch { }
            if (p.ExitCode != 0 && p.ExitCode != 3010) throw new Exception("Node.js 安装失败 (exit=" + p.ExitCode + ")");
            Ok("Node.js 安装完成");
            nodeExe = FindNodeExe();
            if (nodeExe == null) throw new Exception("Node.js 安装后仍无法定位");
            EnsureNodeInPath(nodeExe);
        }

        // ================= 下载 =================
        private static bool DownloadFile(string url, string dest, string desc)
        {
            Console.WriteLine("  下载中: " + desc + " (" + url + ")");
            Stopwatch sw = Stopwatch.StartNew();
            bool ok = false;
            for (int attempt = 1; attempt <= 3 && !ok; attempt++)
            {
                System.Net.HttpWebRequest req = null; System.Net.HttpWebResponse resp = null; Stream stream = null; FileStream fs = null;
                if (File.Exists(dest)) File.Delete(dest);
                try
                {
                    req = (System.Net.HttpWebRequest)System.Net.WebRequest.Create(url);
                    req.Method = "GET";
                    req.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64)";
                    req.Timeout = 30000; req.ReadWriteTimeout = 60000; req.AllowAutoRedirect = true;
                    resp = (System.Net.HttpWebResponse)req.GetResponse();
                    long total = resp.ContentLength;
                    stream = resp.GetResponseStream();
                    fs = File.Create(dest);
                    byte[] buf = new byte[65536];
                    long done = 0, lastBytes = 0;
                    DateTime lastTick = DateTime.Now;
                    Stopwatch progSw = Stopwatch.StartNew();
                    int n;
                    while ((n = stream.Read(buf, 0, buf.Length)) > 0)
                    {
                        fs.Write(buf, 0, n);
                        done += n;
                        if (progSw.ElapsedMilliseconds >= 200)
                        {
                            double dt = (DateTime.Now - lastTick).TotalSeconds;
                            double speed = Math.Max(done - lastBytes, 0) / Math.Max(dt, 0.001) / 1048576.0;
                            lastBytes = done; lastTick = DateTime.Now;
                            Console.Write(string.Format("\r  进度 {0,3:N0}% | {1,7:N1} / {2,7:N1} MB | {3,5:N2} MB/s ", Math.Min(100, done * 100.0 / total), done / 1048576.0, total / 1048576.0, speed));
                            progSw.Restart();
                        }
                    }
                    Console.WriteLine();
                    fs.Close(); stream.Close(); resp.Close();
                    ok = true;
                }
                catch (Exception ex)
                {
                    Console.WriteLine();
                    Fail(string.Format("第 {0} 次下载失败: {1}", attempt, ex.Message));
                    try { if (fs != null) fs.Close(); } catch { }
                    try { if (stream != null) stream.Close(); } catch { }
                    try { if (resp != null) resp.Close(); } catch { }
                    if (attempt < 3) Thread.Sleep(2000);
                }
            }
            sw.Stop();
            if (!ok) { if (File.Exists(dest)) File.Delete(dest); return false; }
            long size = new FileInfo(dest).Length;
            if (size < 1048576) { Fail("下载异常: 文件过小 (" + size + " bytes)"); File.Delete(dest); return false; }
            double avg = (size / 1048576.0) / Math.Max(sw.Elapsed.TotalSeconds, 0.001);
            Ok(string.Format("已下载 {0} ({1:N1} MB, 平均 {2:N2} MB/s)", dest, size / 1048576.0, avg));
            return true;
        }

        // ================= npm / codex =================
        private static int RunCmd(string cmdExe, string args, out string stdout)
        {
            ProcessStartInfo psi = new ProcessStartInfo(cmdExe, args);
            psi.UseShellExecute = false; psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true; psi.RedirectStandardError = true;
            psi.StandardOutputEncoding = Encoding.Default;
            psi.StandardErrorEncoding = Encoding.Default;
            Process p = Process.Start(psi);
            stdout = p.StandardOutput.ReadToEndAsync().Result;
            string stderr = p.StandardError.ReadToEndAsync().Result;
            p.WaitForExit();
            if (!string.IsNullOrEmpty(stderr)) Console.WriteLine("    " + stderr.Trim());
            return p.ExitCode;
        }

        private static string RunNpm(string npmCmd, string args)
        {
            string stdout;
            int code = RunCmd("cmd.exe", "/c \"\"" + npmCmd + "\" " + args + "\"\"", out stdout);
            foreach (string line in stdout.Trim().Split('\n'))
                if (!string.IsNullOrWhiteSpace(line)) Console.WriteLine("    " + line.Trim());
            if (code != 0) throw new Exception("npm " + args + " 失败 (exit=" + code + ")");
            return stdout;
        }

        private static string InstallCodex()
        {
            string nodeExe = FindNodeExe();
            EnsureNodeInPath(nodeExe);
            string nodeDir = Path.GetDirectoryName(nodeExe);
            string npmCmd = Path.Combine(nodeDir, "npm.cmd");
            if (!File.Exists(npmCmd))
                npmCmd = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "npm.cmd");
            if (!File.Exists(npmCmd)) throw new Exception("未找到 npm.cmd: " + npmCmd);

            Step("配置 npm 镜像并安装 @openai/codex");
            try { RunNpm(npmCmd, "config set registry " + NpmRegistryMirror); }
            catch (Exception ex) { Warn("npm config 失败: " + ex.Message); }

            Console.WriteLine("  npm install -g @openai/codex ...");
            bool installed = false;
            for (int attempt = 1; attempt <= 3 && !installed; attempt++)
            {
                try { RunNpm(npmCmd, "install -g @openai/codex"); installed = true; }
                catch (Exception ex)
                {
                    if (attempt < 3) { Warn(string.Format("npm install 第 {0} 次失败（{1}），自动重试...", attempt, ex.Message)); Thread.Sleep(2000); }
                    else throw;
                }
            }
            Ok("codex 安装完成");

            // 定位 codex.cmd（npm 全局 bin）
            string prefix = "";
            try
            {
                string so;
                RunCmd("cmd.exe", "/c \"" + npmCmd + "\" prefix -g", out so);
                string[] lines = so.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                if (lines.Length > 0) prefix = lines[lines.Length - 1].Trim();
            }
            catch { }
            string codexCmd = Path.Combine(prefix, "codex.cmd");
            if (!File.Exists(codexCmd))
                codexCmd = Path.Combine(prefix, "codex.exe");
            if (!File.Exists(codexCmd)) throw new Exception("未找到 codex 命令（npm 全局目录: " + prefix + "）");
            Ok("codex: " + codexCmd);
            return codexCmd;
        }

        // ================= 聚合 API 配置 =================
        private static void ConfigureApi()
        {
            string baseUrl = ApiBaseUrl;
            if (string.IsNullOrEmpty(baseUrl)) baseUrl = DefaultApiBaseUrl;
            string apiKey = ApiKey;
            string model = ApiModel;
            if (string.IsNullOrEmpty(baseUrl) && !SkipApi)
            {
                Console.WriteLine();
                Console.Write("是否配置聚合 API（OpenAI 兼容端点）？[Y/n] ");
                string ans = SafeReadLine();
                bool yes = string.IsNullOrWhiteSpace(ans) ||
                           ans.Trim().ToLower() == "y" || ans.Trim().ToLower() == "yes";
                if (yes)
                {
                    Console.Write("  OpenAI 兼容 Base URL [默认 " + DefaultApiBaseUrl + "]: ");
                    string b = SafeReadLine().Trim();
                    if (!string.IsNullOrEmpty(b)) baseUrl = b;
                    Console.Write("  API Key（格式 sk-xxxxxx）: ");
                    apiKey = SafeReadLine().Trim();
                    Console.Write("  模型（回车使用默认 " + DefaultModel + "）: ");
                    string m = SafeReadLine().Trim();
                    model = string.IsNullOrEmpty(m) ? DefaultModel : m;
                }
            }
            if (string.IsNullOrEmpty(model)) model = DefaultModel;
            WriteConfigToml(baseUrl, apiKey, model);
            if (!string.IsNullOrEmpty(apiKey))
            {
                Step("设置 OPENAI_API_KEY 环境变量（用户级，setx）");
                string so;
                RunCmd("setx.exe", "OPENAI_API_KEY \"" + apiKey.Replace("\"", "\\\"") + "\"", out so);
                // 当前进程也设置，避免本会话找不到
                try { Environment.SetEnvironmentVariable("OPENAI_API_KEY", apiKey, EnvironmentVariableTarget.Process); } catch { }
                Ok("OPENAI_API_KEY 已设置（新开的终端生效）");
            }
            Ok("Codex 配置完成。");
        }

        // 写 ~/.codex/config.toml（保留备份，幂等覆盖本程序管理的键）
        private static void WriteConfigToml(string baseUrl, string apiKey, string model)
        {
            Step("写入 ~/.codex/config.toml");
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string dir = Path.Combine(home, ".codex");
            string path = Path.Combine(dir, "config.toml");
            Directory.CreateDirectory(dir);
            if (File.Exists(path))
            {
                try { File.Copy(path, path + ".bak", true); Ok("已备份原配置: config.toml.bak"); }
                catch { }
            }
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("# Generated by CodexInstaller");
            sb.AppendLine("model = \"" + EscapeToml(model) + "\"");
            sb.AppendLine("model_provider = \"openai\"");
            if (!string.IsNullOrEmpty(baseUrl))
                sb.AppendLine("openai_base_url = \"" + EscapeToml(baseUrl) + "\"");
            sb.AppendLine();
            sb.AppendLine("[windows]");
            sb.AppendLine("sandbox = \"elevated\"");
            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
            Ok("已写入: " + path);
        }

        private static string EscapeToml(string s)
        {
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        // ================= 验证 =================
        private static void Verify(string codexCmd)
        {
            Step("验证 Codex 安装");
            try
            {
                string so;
                RunCmd("cmd.exe", "/c \"" + codexCmd + "\" --version", out so);
                foreach (string line in so.Trim().Split('\n'))
                    if (!string.IsNullOrWhiteSpace(line)) Console.WriteLine("  " + line.Trim());
                Ok("codex 命令可用");
            }
            catch (Exception ex) { Warn("codex --version 执行失败: " + ex.Message); }
        }

        private static void Summary()
        {
            Console.WriteLine("\n===============================================");
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine(" 安装完成。");
            Console.ResetColor();
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine(" 用法: 新开终端运行 codex");
            Console.WriteLine(" 国内使用需将 openai_base_url 指向可访问的 OpenAI 兼容端点，API Key 存于 OPENAI_API_KEY。");
            Console.ResetColor();
            Console.WriteLine("===============================================");
        }

        private static void PrintUsage()
        {
            Console.WriteLine("OpenAI Codex CLI 一键安装程序（国内网络优化版，纯 .NET 实现）");
            Console.WriteLine();
            Console.WriteLine("用法: CodexInstaller.exe [参数...]");
            Console.WriteLine();
            Console.WriteLine("参数:");
            Console.WriteLine("  -SkipApi               跳过聚合 API 配置");
            Console.WriteLine("  -SkipNodeJs            使用系统已有 Node.js");
            Console.WriteLine("  -NodeVersion v24.19.0  指定 Node.js 版本（npmmirror）");
            Console.WriteLine("  -ApiBaseUrl <url>      OpenAI 兼容端点（默认 " + DefaultApiBaseUrl + "）");
            Console.WriteLine("  -ApiKey <key>          API Key（格式 sk-xxxxxx）");
            Console.WriteLine("  -ApiModel <model>      模型（默认 " + DefaultModel + "）");
        }
    }
}
