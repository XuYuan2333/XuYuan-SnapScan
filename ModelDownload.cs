using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;

namespace QRSnap
{
    // 设置里「下载」按钮：从 GitHub Releases 拉模型包，下完直接复用「导入模型包」那套逻辑，
    // 导入完把下载下来的 zip 和解压残留一起删掉，不在磁盘上留第二份。
    internal static class ModelDownload
    {
        private const string RepoBase = "https://github.com/XuYuan2333/XuYuan-SnapScan/releases/download/";

        private class Item
        {
            public string Key;
            public string Tag;
            public string Asset;
            public string Label;
            public Item(string k, string t, string a, string l) { Key = k; Tag = t; Asset = a; Label = l; }
        }

        private static readonly Item[] Items = new Item[]
        {
            new Item(AppConfig.EngineWeChat, "v1.1.8", "SnapScan-Model-WeChatOCR-v1.1.8.zip", "微信 OCR"),
            new Item(AppConfig.EnginePpTiny,  "v1.1.8", "SnapScan-Model-PP-OCR-Tiny-v1.1.8.zip", "PP-OCR 极速"),
            new Item(AppConfig.EnginePpMedium,"v1.1.8", "SnapScan-Model-PP-OCR-Medium-v1.1.8.zip", "PP-OCR 高精"),
            new Item("mt",                    "v1.1.8", "SnapScan-Model-HyMT1.5-v1.1.9.zip", "混元翻译模型"),
        };

        private static readonly object Gate = new object();
        private static string _key = string.Empty;
        private static string _status = string.Empty;
        private static int _percent;
        private static bool _running;
        private static long _total;
        private static long _got;

        public static bool Running { get { lock (Gate) return _running; } }
        public static string CurrentKey { get { lock (Gate) return _key; } }
        public static string Status { get { lock (Gate) return _status; } }
        public static int Percent { get { lock (Gate) return _percent; } }

        public static string StateJson()
        {
            lock (Gate)
            {
                return "{\"key\":" + J.S(_key) + ",\"status\":" + J.S(_status) + ",\"percent\":" + _percent
                    + ",\"running\":" + (_running ? "true" : "false") + "}";
            }
        }

        private static Item Find(string key)
        {
            for (int i = 0; i < Items.Length; i++)
                if (string.Equals(Items[i].Key, key, StringComparison.OrdinalIgnoreCase)) return Items[i];
            return null;
        }

        private static void Report(string status, int percent, string key, bool running)
        {
            lock (Gate)
            {
                _status = status;
                _percent = percent;
                _key = key;
                _running = running;
            }
            try
            {
                NativeShell.Log("dl " + key + " " + percent + "% " + status);
            }
            catch (Exception) { }
            try
            {
                JadeHost.Send("dl-progress", "{\"key\":" + QRSnap.J.S(key) + ",\"status\":" + QRSnap.J.S(status)
                    + ",\"percent\":" + percent + ",\"running\":" + (running ? "true" : "false")
                    + ",\"total\":" + _total + ",\"got\":" + _got + "}");
            }
            catch (Exception) { }
        }

        // 返回给前端的即时回执（真正干活在线程里）
        public static string Start(string key)
        {
            Item it = Find(key);
            if (it == null) return "{\"ok\":false,\"msg\":" + J.S("未知的模型：" + key) + "}";
            lock (Gate)
            {
                if (_running) return "{\"ok\":false,\"msg\":" + J.S("已经在下载「" + _status + "」，请稍等") + "}";
                _running = true;
                _key = key;
            }
            Thread t = new Thread(delegate() { Run(it); });
            t.IsBackground = true;
            t.Start();
            return "{\"ok\":true,\"msg\":" + J.S("开始下载 " + it.Label) + "}";
        }

        // 优先走系统自带 curl.exe：TLS 和代理都比老 .NET 的 HttpWebRequest 靠谱
        private static void CurlFetch(string curl, string url, string zip, Item it)
        {
            string proxy = SystemProxy();
            string args = "-L --fail --retry 2 --connect-timeout 20 -o \"" + zip + "\" \"" + url + "\"";
            if (proxy != null) args = "-x \"" + proxy + "\" " + args;

            ProcessStartInfo psi = new ProcessStartInfo(curl, args);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            long total = HeadSize(curl, url, proxy);
            Process p = Process.Start(psi);
            DateTime t0 = DateTime.Now;
            while (!p.HasExited)
            {
                Thread.Sleep(250);
                long got = 0;
                try { if (File.Exists(zip)) got = new FileInfo(zip).Length; } catch (Exception) { }
                _got = got;
                int pct = total > 0 ? (int)(got * 100 / total) : 0;
                string txt = total > 0
                    ? ("下载 " + it.Label + " " + pct + "%（" + MB(got) + " / " + MB(total) + "）")
                    : ("下载 " + it.Label + " " + MB(got) + "（已下了 " + (int)(DateTime.Now - t0).TotalSeconds + " s）");
                Report(txt, pct, it.Key, true);
            }
            int code = p.ExitCode;
            if (code != 0) throw new Exception("curl 退出码 " + code + (proxy == null ? "，没走代理" : "，代理 " + proxy));
            long size = 0;
            try { size = new FileInfo(zip).Length; } catch (Exception) { }
            if (size < 1024) throw new Exception("下载文件为空");
            Report("下载完成 " + MB(size) + "，正在导入 …", 100, it.Key, true);
        }

        private static void NetFetch(string url, string zip, Item it)
        {
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
            req.UserAgent = "XuYuan-SnapScan";
            req.Timeout = 20000;
            req.ReadWriteTimeout = 30000;
            req.AllowAutoRedirect = true;
            using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
            {
                if (resp.StatusCode != HttpStatusCode.OK)
                    throw new Exception("HTTP " + (int)resp.StatusCode);
                long total = resp.ContentLength;
                _total = total;
                _got = 0;
                using (Stream s = resp.GetResponseStream())
                using (FileStream fs = new FileStream(zip, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    byte[] buf = new byte[131072];
                    DateTime lastPush = DateTime.UtcNow;
                    int n;
                    while ((n = s.Read(buf, 0, buf.Length)) > 0)
                    {
                        fs.Write(buf, 0, n);
                        _got += n;
                        int pct = total > 0 ? (int)(_got * 100 / total) : -1;
                        if ((DateTime.UtcNow - lastPush).TotalSeconds >= 0.25)
                        {
                            lastPush = DateTime.UtcNow;
                            string txt = total > 0
                                ? ("下载 " + it.Label + " " + pct + "%（" + MB(_got) + " / " + MB(total) + "）")
                                : ("下载 " + it.Label + " " + MB(_got));
                            Report(txt, pct < 0 ? 0 : pct, it.Key, true);
                        }
                    }
                }
            }
        }

        // HEAD 拿总大小（curl -I 跟随重定向），拿不到就返回 0，只显示已下多少
        private static long HeadSize(string curl, string url, string proxy)
        {
            try
            {
                string args = "-sI -L --connect-timeout 10 \"" + url + "\"";
                if (proxy != null) args = "-x \"" + proxy + "\" " + args;
                ProcessStartInfo psi = new ProcessStartInfo(curl, args);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                Process p = Process.Start(psi);
                string outp = p.StandardOutput.ReadToEnd();
                p.WaitForExit(15000);
                long last = 0;
                foreach (string line in outp.Split('\n'))
                {
                    string s = line.Trim();
                    if (s.StartsWith("content-length:", StringComparison.OrdinalIgnoreCase))
                    {
                        try { last = long.Parse(s.Substring(15).Trim()); } catch (Exception) { }
                    }
                }
                return last;
            }
            catch (Exception) { }
            return 0;
        }

        // 系统代理（IE/WinINET 设置），curl 默认不读它，得自己带过去
        private static string SystemProxy()
        {
            try
            {
                using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    "Software\\Microsoft\\Windows\\CurrentVersion\\Internet Settings"))
                {
                    if (k == null) return null;
                    object en = k.GetValue("ProxyEnable");
                    if (en == null || Convert.ToInt32(en) == 0) return null;
                    object srv = k.GetValue("ProxyServer");
                    if (srv == null) return null;
                    string s = Convert.ToString(srv);
                    if (s == null || s.Trim().Length == 0) return null;
                    return s.Trim();
                }
            }
            catch (Exception) { }
            return null;
        }

        private static void Run(Item it)
        {
            string zip = null;
            try
            {
                string baseUrl = RepoBase;
                string env = Environment.GetEnvironmentVariable("QRSNAP_DL_BASE");
                if (env != null && env.Trim().Length > 0) baseUrl = env.Trim();
                string url = baseUrl + it.Tag + "/" + Uri.EscapeDataString(it.Asset);

                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XuYuan-SnapScan", "download");
                Directory.CreateDirectory(dir);
                zip = Path.Combine(dir, it.Asset);

                Report("连接 GitHub …", 0, it.Key, true);
                string curl = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "curl.exe");
                if (File.Exists(curl)) CurlFetch(curl, url, zip, it);
                else NetFetch(url, zip, it);

                Report("下载完成，正在导入 …", 100, it.Key, true);
                string result = JadeHost.ImportForTest(zip);
                Report("已装好：" + it.Label, 100, it.Key, false);
                try { JadeHost.Send("state-refresh", JadeHost.BuildState()); } catch (Exception) { }
                Log("model download ok " + it.Key + " -> " + result);
            }
            catch (Exception ex)
            {
                Report("下载失败：" + Short(ex.Message) + "（需要能访问 GitHub）", 0, it.Key, false);
                Log("model download fail " + it.Key + " : " + ex.Message);
            }
            finally
            {
                // 导入后 zip 已没用了：删掉，别在本地留第二份大文件
                try { if (zip != null && File.Exists(zip)) File.Delete(zip); } catch (Exception) { }
                try
                {
                    string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XuYuan-SnapScan", "download");
                    if (Directory.Exists(dir) && Directory.GetFiles(dir).Length == 0 && Directory.GetDirectories(dir).Length == 0)
                        Directory.Delete(dir);
                }
                catch (Exception) { }
            }
        }

        private static string MB(long b) { return (b / 1048576.0).ToString("0.0") + " MB"; }

        private static string Short(string s)
        {
            if (s == null) return "";
            s = s.Replace("\r", " ").Replace("\n", " ");
            return s.Length > 90 ? s.Substring(0, 90) : s;
        }

        private static void Log(string msg)
        {
            try { NativeShell.Log(msg); } catch (Exception) { }
        }
    }
}
