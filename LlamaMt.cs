using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

// GGUF 翻译模型后端：用 llama.cpp 的 llama-server.exe 起一个本地 HTTP 服务，
// 把 OCR 出来的每行文本发给 /v1/chat/completions 拿译文。
// 选 GGUF 而不是 ONNX 的原因：一个模型文件就能覆盖多语种方向，且小模型在 CPU 上够快。
namespace QRSnap
{
internal static class LlamaMt
{
    private static readonly object Gate = new object();
    private static Process _proc;
    private static string _model;      // 当前服务加载的模型文件
    private static int _port;
    private static string _exe;
    private static DateTime _lastUse = DateTime.MinValue;
    private static Timer _idle;
    private static bool _exitHook;

    private static int IdleSeconds { get { return AppConfig.MtIdle; } }   // 设置里可改，0 = 不自动卸载

    // 运行时定位：随包分发的 llama-server.exe 可能放在这几个位置
    public static string ExeDir()
    {
        string base_ = AppDomain.CurrentDomain.BaseDirectory;
        // 优先 Vulkan 运行时：一套 DLL 同时支持 AMD / NVIDIA / Intel
        if (AppConfig.NormalizeMtGpu(AppConfig.MtGpu) != "off")
        {
            string vk = Path.Combine(base_, "llama-vulkan");
            if (File.Exists(Path.Combine(vk, "llama-server.exe")) && File.Exists(Path.Combine(vk, "ggml-vulkan.dll")))
                return vk;
        }
        return CpuDir(base_);
    }

    private static string CpuDir()
    {
        return CpuDir(AppDomain.CurrentDomain.BaseDirectory);
    }

    private static string CpuDir(string base_)
    {
        string[] cands = new string[]
        {
            Path.Combine(base_, "llama"),
            Path.Combine(base_, "Runtime", "llama"),
            Path.Combine(base_, "runtime", "llama"),
            Path.Combine(base_, "llama.cpp"),
            base_
        };
        for (int i = 0; i < cands.Length; i++)
        {
            string f = Path.Combine(cands[i], "llama-server.exe");
            if (File.Exists(f)) return cands[i];
        }
        return null;
    }

    private static bool IsVulkanDir(string dir)
    {
        return dir != null && dir.IndexOf("llama-vulkan", StringComparison.OrdinalIgnoreCase) >= 0
            && File.Exists(Path.Combine(dir, "ggml-vulkan.dll"));
    }

    public static bool GpuActive;

    private static int _gpuProbe = -1;
    // 探测 Vulkan 设备：有 ggml-vulkan.dll 且 llama-server --list-devices 能枚举到 Vulkan0
    public static bool GpuUsable
    {
        get
        {
            if (_gpuProbe >= 0) return _gpuProbe == 1;
            _gpuProbe = 0;
            try
            {
                if (AppConfig.NormalizeMtGpu(AppConfig.MtGpu) == "off") return false;
                string dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "llama-vulkan");
                if (!File.Exists(Path.Combine(dir, "llama-server.exe")) || !File.Exists(Path.Combine(dir, "ggml-vulkan.dll")))
                    return false;
                ProcessStartInfo si = new ProcessStartInfo();
                si.FileName = Path.Combine(dir, "llama-server.exe");
                si.Arguments = "--list-devices";
                si.WorkingDirectory = dir;
                si.UseShellExecute = false;
                si.CreateNoWindow = true;
                si.RedirectStandardOutput = true;
                si.RedirectStandardError = true;
                using (Process p = Process.Start(si))
                {
                    string outp = p.StandardOutput.ReadToEnd();
                    try { p.StandardError.ReadToEnd(); } catch (Exception) { }
                    if (!p.WaitForExit(4000)) { try { p.Kill(); } catch (Exception) { } return false; }
                    if (outp != null && outp.IndexOf("Vulkan", StringComparison.OrdinalIgnoreCase) >= 0)
                        _gpuProbe = 1;
                }
            }
            catch (Exception) { _gpuProbe = 0; }
            return _gpuProbe == 1;
        }
    }

    public static bool RuntimeReady
    {
        get
        {
            if (_exe != null && File.Exists(Path.Combine(_exe, "llama-server.exe"))) return true;
            _exe = ExeDir();
            return _exe != null;
        }
    }

    public static string RuntimeHint
    {
        get { return RuntimeReady ? "" : "缺少 llama.cpp 运行时（llama-server.exe），翻译用不了"; }
    }

    // 引擎进程是否已经驻留：空闲释放后是 false，第一次翻译要等它重新加载
    public static bool ServerRunning
    {
        get { try { return _proc != null && !_proc.HasExited; } catch (Exception) { return false; } }
    }

    private static int FreePort()
    {
        TcpListener l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    // 线程档位：后台无感最省（1/8），均衡 1/3，性能 1/2，全力以赴吃满
    private static int ThreadsForMode()
    {
        int cores = Environment.ProcessorCount;
        if (cores < 1) cores = 1;
        string m = AppConfig.NormalizeMtPerf(AppConfig.MtPerf);
        if (m == AppConfig.MtPerfFull) return cores;
        if (m == AppConfig.MtPerfPerformance) return Math.Max(2, cores / 2);
        if (m == AppConfig.MtPerfSilent) return Math.Max(1, cores / 8);
        return Math.Max(2, cores / 3);
    }

    // 后台无感：线程优先级降到 low，并且关掉 spin 轮询，尽量不跟前台抢 CPU
    private static string PerfArgs()
    {
        string m = AppConfig.NormalizeMtPerf(AppConfig.MtPerf);
        if (m == AppConfig.MtPerfSilent) return " --prio -1 --poll 0";
        return string.Empty;
    }

    private static void StartServer(string model)
    {
        string dir = ExeDir();
        if (dir == null) throw new Exception("缺少 llama.cpp 运行时（llama-server.exe）");
        bool gpu = IsVulkanDir(dir);
        try
        {
            StartServerCore(model, dir, gpu);
            GpuActive = gpu;
        }
        catch (Exception)
        {
            // GPU 起不来（驱动/显存/后端不兼容）就回 CPU，不让翻译整体失败
            if (!gpu) throw;
            Kill();
            string cpu = CpuDir();
            if (cpu == null) throw new Exception("GPU 初始化失败，且找不到 CPU 版 llama.cpp");
            StartServerCore(model, cpu, false);
            GpuActive = false;
        }
    }

    private static void StartServerCore(string model, string dir, bool gpu)
    {
        Kill();

        int port = FreePort();
        int threads = ThreadsForMode();
        string perfArgs = PerfArgs();
        string plamoArg = IsPlamo(model) ? " --no-prefill-assistant" : "";
        ProcessStartInfo si = new ProcessStartInfo();
        si.FileName = Path.Combine(dir, "llama-server.exe");
        si.Arguments = "--model \"" + model + "\" --host 127.0.0.1 --port " + port
            + " --jinja -c 2048 -t " + threads + (gpu ? " -ngl 99" : " -ngl 0") + perfArgs + plamoArg + " --no-warmup";
        si.WorkingDirectory = dir;
        si.UseShellExecute = false;
        si.CreateNoWindow = true;
        si.RedirectStandardOutput = true;
        si.RedirectStandardError = true;
        Process p = new Process();
        p.StartInfo = si;
        p.OutputDataReceived += delegate(object s, DataReceivedEventArgs e) { };
        p.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e) { };
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();

        // 等 /health 变绿（模型是 mmap 加载，通常一两秒就好）
        DateTime t0 = DateTime.UtcNow;
        while (true)
        {
            if (p.HasExited) throw new Exception("翻译模型加载失败（" + (gpu ? "GPU" : "CPU") + " 运行时，模型文件与 llama.cpp 版本可能不匹配）");
            try
            {
                string body = HttpGet("http://127.0.0.1:" + port + "/health", 2);
                if (body != null && body.IndexOf("ok", StringComparison.OrdinalIgnoreCase) >= 0) break;
            }
            catch (Exception) { }
            if ((DateTime.UtcNow - t0).TotalSeconds > 60) { try { p.Kill(); } catch (Exception) { } throw new Exception("翻译模型加载超时"); }
            Thread.Sleep(300);
        }

        _proc = p;
        _model = model;
        _port = port;
        _lastUse = DateTime.UtcNow;
        if (!_exitHook)
        {
            _exitHook = true;
            AppDomain.CurrentDomain.ProcessExit += delegate(object s, EventArgs e) { try { Kill(); } catch (Exception) { } };
        }
        if (_idle == null) _idle = new Timer(IdleTick, null, 5000, 5000);
    }

    private static void IdleTick(object state)
    {
        try
        {
            lock (Gate)
            {
                if (_proc == null) return;
                // 游戏模式内常驻；关闭游戏模式或切换模型由 GameRuntime/设置统一释放
                if (AppConfig.GameOn) return;
                int idle = IdleSeconds;
                if (idle <= 0) return;                                   // 0 = 常驻，不自动释放
                if ((DateTime.UtcNow - _lastUse).TotalSeconds < idle) return;
                Kill();
            }
        }
        catch (Exception) { }
    }

    public static void Kill()
    {
        Process p = _proc;
        _proc = null;
        _model = null;
        _port = 0;
        if (p == null) return;
        try { if (!p.HasExited) { p.Kill(); p.WaitForExit(3000); } } catch (Exception) { }
        try { p.Dispose(); } catch (Exception) { }
    }

    public static void Shutdown() { lock (Gate) { Kill(); } }

    // 目标语言写进提示词：中文用官方中文模板（收紧版），其它方向用英文模板
    // 原版模板会让模型自行补词（“Press any key to continue” 被译成「按任意键继续游戏」），
    // 这里显式要求「忠实原文、不增不减」，实测能压住这类幻觉。
    private static string Prompt(string text, string tgt)
    {
        // 只保留一句提示，避免小模型把规则/术语说明当成正文吐出来
        string lang = tgt == "zh" ? "简体中文" : langEn(tgt);
        return "只输出译文（" + lang + "）：\n\n" + text;
    }

    private static string langEn(string tgt)
    {
        if (tgt == "en") return "English";
        if (tgt == "ja") return "Japanese";
        if (tgt == "ko") return "Korean";
        if (tgt == "ru") return "Russian";
        if (tgt == "fr") return "French";
        if (tgt == "de") return "German";
        if (tgt == "es") return "Spanish";
        return tgt;
    }

    public static string Translate(string model, string text, string tgt)
    {
        if (string.IsNullOrEmpty(text)) return "";
        if (!RuntimeReady) throw new Exception("缺少 llama.cpp 运行时（llama-server.exe），翻译用不了");
        lock (Gate)
        {
            if (_proc == null || !string.Equals(_model, model, StringComparison.OrdinalIgnoreCase) || (_proc != null && _proc.HasExited))
                StartServer(model);
            _lastUse = DateTime.UtcNow;

            string payload = IsPlamo(model) ? BuildPlamoRequest(text, 192) : BuildRequest(Prompt(text, tgt), 192);
            string resp = HttpPost("http://127.0.0.1:" + _port + "/v1/chat/completions", payload, 120);
            string content = JsonField(resp, "content");
            _lastUse = DateTime.UtcNow;
            return Clean(content);
        }
    }

    private static bool IsPlamo(string model)
    {
        return !string.IsNullOrEmpty(model) && Path.GetFileName(model).IndexOf("plamo", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static string BuildPlamoRequest(string text, int maxTokens)
    {
        StringBuilder sb = new StringBuilder();
        sb.Append("{\"messages\":[{\"role\":\"user\",\"content\":").Append(J(text)).Append("}]");
        sb.Append(",\"chat_template_kwargs\":{\"target_lang\":\"zh\"}");
        sb.Append(",\"temperature\":0,\"max_tokens\":").Append(maxTokens);
        sb.Append(",\"stop\":[\"<|plamo:tag|>\",\"<|plamo:bos|>\"],\"stream\":false}");
        return sb.ToString();
    }

    private static string BuildRequest(string prompt)
    {
        return BuildRequest(prompt, 192);
    }

    private static string BuildRequest(string prompt, int maxTokens, string system = null)
    {
        StringBuilder sb = new StringBuilder();
        sb.Append("{\"messages\":[");
        if (!string.IsNullOrEmpty(system))
        {
            sb.Append("{\"role\":\"system\",\"content\":").Append(J(system)).Append("},");
        }
        sb.Append("{\"role\":\"user\",\"content\":");
        sb.Append(J(prompt));
        sb.Append("}],\"max_tokens\":").Append(maxTokens).Append(",\"temperature\":0.2,\"stream\":false}");
        return sb.ToString();
    }

    // 纯离线批量翻译：一次请求翻多行，喂给同一个 llama-server，省掉每行一次 HTTP + 前缀计算。
    // 行数对不上（模型合并/拆行）就返回 null，调用方回退逐行，绝不把错位的译文贴回去。
    public static List<string> TranslateBatch(string model, List<string> lines, string tgt)
    {
        if (lines == null || lines.Count == 0) return new List<string>();
        if (!RuntimeReady) throw new Exception("缺少 llama.cpp 运行时（llama-server.exe），翻译用不了");
        lock (Gate)
        {
            if (_proc == null || !string.Equals(_model, model, StringComparison.OrdinalIgnoreCase) || (_proc != null && _proc.HasExited))
                StartServer(model);
            _lastUse = DateTime.UtcNow;

            // PLaMo 官方要求一条原文一个请求；不要用多行 prompt 批翻
            if (IsPlamo(model))
            {
                List<string> one = new List<string>();
                for (int i = 0; i < lines.Count; i++) one.Add(Translate(model, lines[i], tgt));
                return one;
            }

            StringBuilder prompt = new StringBuilder();
            int chars = 0;
            if (tgt == "zh")
                prompt.Append("只输出译文（简体中文），每行对应一行：\n");
            else
                prompt.Append("Only output translations (").Append(langEn(tgt)).Append("), one per line:\n");
            for (int i = 0; i < lines.Count; i++)
            {
                prompt.Append(lines[i]).Append('\n');
                chars += lines[i].Length;
            }
            int maxTokens = Math.Min(1536, Math.Max(96, chars * 2));
            string payload = BuildRequest(prompt.ToString(), maxTokens);
            string resp = HttpPost("http://127.0.0.1:" + _port + "/v1/chat/completions", payload, 180);
            string content = Clean(JsonField(resp, "content"));
            _lastUse = DateTime.UtcNow;
            if (string.IsNullOrEmpty(content)) return null;

            string[] parts = content.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            List<string> got = new List<string>();
            for (int i = 0; i < parts.Length; i++)
            {
                string s = parts[i].Trim();
                if (s.Length == 0) continue;
                // 去掉模型自作主张加的 “1. ” / “1、” 前缀
                int k = 0;
                while (k < s.Length && char.IsDigit(s[k])) k++;
                if (k > 0 && k < s.Length && (s[k] == '.' || s[k] == '、' || s[k] == ')' || s[k] == '．'))
                    s = s.Substring(k + 1).Trim();
                if (s.Length > 0) got.Add(s);
            }
            return got.Count == lines.Count ? got : null;
        }
    }

    // 模型偶尔会带出多余说明或引号，收一下尾
    private static string Clean(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        s = s.Trim().Trim('\uFEFF').Trim();
        s = s.Trim('"').Trim();
        s = StripPromptEcho(s);
        if (s.StartsWith("翻译：", StringComparison.Ordinal)) s = s.Substring(3).Trim();
        if (s.StartsWith("译文：", StringComparison.Ordinal)) s = s.Substring(3).Trim();
        return s.Trim();
    }

    // 0.5B 偶尔把 system/user 提示词一起吐出来；这里把整行提示和前缀切掉，只留译文
    private static string StripPromptEcho(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        string[] lines = s.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        StringBuilder sb = new StringBuilder();
        for (int i = 0; i < lines.Length; i++)
        {
            string t = lines[i].Trim();
            if (t.Length == 0) continue;
            if (IsPromptEcho(t))
            {
                // 提示词和译文同一行时，取“只输出译文：/翻译成简体中文：”后面的部分
                int cut = t.LastIndexOf("只输出译文", StringComparison.Ordinal);
                if (cut < 0) cut = t.LastIndexOf("翻译成简体中文：", StringComparison.Ordinal);
                if (cut < 0) cut = t.LastIndexOf("翻译成中文：", StringComparison.Ordinal);
                if (cut >= 0)
                {
                    int p = t.IndexOf('：', cut);
                    if (p >= 0 && p + 1 < t.Length) t = t.Substring(p + 1).Trim();
                    else continue;
                }
                else continue;
            }
            t = TrimMetaPrefix(t);
            if (t.Length == 0) continue;
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(t);
        }
        return sb.ToString();
    }

    private static string TrimMetaPrefix(string t)
    {
        string low = t.ToLowerInvariant();
        string[] marks = new string[] { "in simple chinese:", "in chinese:", "translation:", "译文：", "翻译：" };
        for (int i = 0; i < marks.Length; i++)
        {
            int at = low.LastIndexOf(marks[i], StringComparison.Ordinal);
            if (at < 0) continue;
            int p = t.IndexOf(':', at);
            if (p < 0) p = t.IndexOf('：', at);
            if (p >= 0 && p + 1 < t.Length) return t.Substring(p + 1).Trim();
        }
        return t;
    }

    private static bool IsPromptEcho(string t)
    {
        if (t.IndexOf("若有多重意思", StringComparison.Ordinal) >= 0) return true;
        if (t.IndexOf("只输出译文", StringComparison.Ordinal) >= 0) return true;
        if (t.IndexOf("不要解释", StringComparison.Ordinal) >= 0) return true;
        if (t.IndexOf("游戏行业术语", StringComparison.Ordinal) >= 0) return true;
        if (t.IndexOf("游戏短句翻译引擎", StringComparison.Ordinal) >= 0) return true;
        if (t.StartsWith("针对短句翻译", StringComparison.Ordinal)) return true;
        if (t.StartsWith("把下面这段", StringComparison.Ordinal)) return true;
        if (t.StartsWith("翻译成简体中文：", StringComparison.Ordinal)) return true;
        if (t.StartsWith("翻译成中文：", StringComparison.Ordinal)) return true;
        if (t.StartsWith("你是", StringComparison.Ordinal) && t.IndexOf("翻译", StringComparison.Ordinal) >= 0) return true;
        return false;
    }

    private static string J(string s)
    {
        StringBuilder sb = new StringBuilder("\"");
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '"') sb.Append("\\\"");
            else if (c == '\\') sb.Append("\\\\");
            else if (c == '\n') sb.Append("\\n");
            else if (c == '\r') sb.Append("\\r");
            else if (c == '\t') sb.Append("\\t");
            else if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4"));
            else sb.Append(c);
        }
        return sb.Append('"').ToString();
    }

    private static string HttpGet(string url, int timeoutSec)
    {
        HttpWebRequest r = (HttpWebRequest)WebRequest.Create(url);
        r.Method = "GET";
        r.Timeout = timeoutSec * 1000;
        r.Proxy = null;
        r.KeepAlive = false;
        using (WebResponse w = r.GetResponse())
        using (StreamReader sr = new StreamReader(w.GetResponseStream(), Encoding.UTF8))
            return sr.ReadToEnd();
    }

    private static string HttpPost(string url, string json, int timeoutSec)
    {
        byte[] data = Encoding.UTF8.GetBytes(json);
        HttpWebRequest r = (HttpWebRequest)WebRequest.Create(url);
        r.Method = "POST";
        r.ContentType = "application/json; charset=utf-8";
        r.Timeout = timeoutSec * 1000;
        r.ReadWriteTimeout = timeoutSec * 1000;
        r.Proxy = null;
        r.KeepAlive = false;
        r.ContentLength = data.Length;
        using (Stream s = r.GetRequestStream()) s.Write(data, 0, data.Length);
        using (WebResponse w = r.GetResponse())
        using (StreamReader sr = new StreamReader(w.GetResponseStream(), Encoding.UTF8))
            return sr.ReadToEnd();
    }

    // 从 JSON 里取一个字符串字段（只要第一个匹配，够用且不必引入 JSON 库）
    private static string JsonField(string json, string key)
    {
        if (string.IsNullOrEmpty(json)) return "";
        int k = json.IndexOf("\"" + key + "\"", StringComparison.Ordinal);
        if (k < 0) return "";
        int c = json.IndexOf(':', k + key.Length + 2);
        if (c < 0) return "";
        int i = c + 1;
        while (i < json.Length && char.IsWhiteSpace(json[i])) i++;
        if (i >= json.Length || json[i] != '"') return "";
        i++;
        StringBuilder sb = new StringBuilder();
        while (i < json.Length)
        {
            char ch = json[i];
            if (ch == '\\' && i + 1 < json.Length)
            {
                char n = json[i + 1];
                if (n == 'n') sb.Append('\n');
                else if (n == 'r') sb.Append('\r');
                else if (n == 't') sb.Append('\t');
                else if (n == 'b') sb.Append('\b');
                else if (n == 'f') sb.Append('\f');
                else if (n == 'u' && i + 5 < json.Length)
                {
                    try { sb.Append((char)Convert.ToInt32(json.Substring(i + 2, 4), 16)); } catch (Exception) { }
                    i += 6;
                    continue;
                }
                else sb.Append(n);
                i += 2;
                continue;
            }
            if (ch == '"') break;
            sb.Append(ch);
            i++;
        }
        return sb.ToString();
    }
}
}
