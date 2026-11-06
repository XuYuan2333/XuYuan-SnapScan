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
        Kill();

        int port = FreePort();
        int threads = ThreadsForMode();
        string perfArgs = PerfArgs();
        ProcessStartInfo si = new ProcessStartInfo();
        si.FileName = Path.Combine(dir, "llama-server.exe");
        si.Arguments = "--model \"" + model + "\" --host 127.0.0.1 --port " + port
            + " --jinja -c 1024 -t " + threads + perfArgs + " --no-warmup";
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
            if (p.HasExited) throw new Exception("翻译模型加载失败（模型文件与 llama.cpp 版本不匹配）");
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
                int idle = IdleSeconds;
                if (idle <= 0) return;                                   // 0 = 常驻，不释放
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
        if (tgt == "zh")
            return "把下面这段文字翻译成简体中文，忠实原文，不增不减，直接给出译文：\n\n" + text;
        return "Translate the following segment into " + langEn(tgt) + ", faithfully, without adding or omitting information, without explanation.\n\n" + text;
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

            string payload = BuildRequest(Prompt(text, tgt));
            string resp = HttpPost("http://127.0.0.1:" + _port + "/v1/chat/completions", payload, 120);
            string content = JsonField(resp, "content");
            _lastUse = DateTime.UtcNow;
            return Clean(content);
        }
    }

    private static string BuildRequest(string prompt)
    {
        StringBuilder sb = new StringBuilder();
        sb.Append("{\"messages\":[{\"role\":\"user\",\"content\":");
        sb.Append(J(prompt));
        sb.Append("}],\"max_tokens\":192,\"temperature\":0.2,\"stream\":false}");
        return sb.ToString();
    }

    // 模型偶尔会带出多余说明或引号，收一下尾
    private static string Clean(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        s = s.Trim().Trim('\uFEFF').Trim();
        s = s.Trim('"').Trim();
        if (s.StartsWith("翻译：", StringComparison.Ordinal)) s = s.Substring(3).Trim();
        if (s.StartsWith("译文：", StringComparison.Ordinal)) s = s.Substring(3).Trim();
        return s;
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
