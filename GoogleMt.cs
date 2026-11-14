using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;

namespace QRSnap
{
    // 谷歌翻译（translate_a/single 免密钥通道）：不用注册、不用密钥，
    // 只要能访问 translate.googleapis.com 就行；国内需自备网络通道，程序跟随系统代理。
    //
    // 调用方式：整屏的行用 \n 拼成一整段再请求（dj=1 结构化返回），
    // 谷歌会按 \n 守住行边界，返回的 sentences 里每行译文自带换行，按行切回去即可。
    // 这样 40 行只发 1~3 个请求（而不是 40 个），既快又不会被限流，句子上下文也更完整。
    public static class GoogleMt
    {
        private const string HostName = "translate.googleapis.com";
        private const int MaxWorkers = 3;        // 并发请求数（免密钥通道限流，开太高会 429）
        private const int ChunkChars = 1200;     // 单个请求最多拼多少字符（GET 的 URL 长度要留余量）
        private const int MaxChars = 1800;       // 单条兜底上限

        public static string LastError = "";
        public static long LastMs;

        public static bool Configured
        {
            get { return true; }   // 免密钥，永远算已配置
        }

        private static string LangCode(string code, bool isTarget)
        {
            string s = (code ?? "").Trim().ToLowerInvariant();
            if (s.Length == 0) return isTarget ? "zh-CN" : "en";
            if (s == "zh" || s == "zh-cn" || s == "zh-hans" || s == "cmn") return "zh-CN";
            if (s == "zh-tw" || s == "zh-hant") return "zh-TW";
            return s;
        }

        public static string SelfTest()
        {
            string[] got = TranslateBatch(new string[] { "Hello, this is a test." }, "en", "zh");
            if (got == null || got.Length == 0) return null;
            if (string.IsNullOrEmpty(got[0])) { LastError = "接口返回空译文"; return null; }
            return got[0];
        }

        public static string[] TranslateBatch(string[] texts, string src, string tgt)
        {
            LastError = "";
            LastMs = 0;
            if (texts == null || texts.Length == 0) return new string[0];

            long t0 = Environment.TickCount;
            string from = LangCode(src, false);
            string to = LangCode(tgt, true);
            string[] done = new string[texts.Length];

            List<int> idx = new List<int>();
            for (int i = 0; i < texts.Length; i++)
            {
                if (texts[i] != null && texts[i].Trim().Length > 0) idx.Add(i);
                else done[i] = "";
            }
            if (idx.Count == 0) return done;

            // 只按字符数切块，不按行数，块内行用换行拼起来
            List<List<int>> chunks = new List<List<int>>();
            List<int> cur = new List<int>();
            int curLen = 0;
            for (int n = 0; n < idx.Count; n++)
            {
                int i = idx[n];
                int len = texts[i].Length + 1;
                if (cur.Count > 0 && curLen + len > ChunkChars)
                {
                    chunks.Add(cur);
                    cur = new List<int>();
                    curLen = 0;
                }
                cur.Add(i);
                curLen += len;
            }
            if (cur.Count > 0) chunks.Add(cur);

            object gate = new object();
            string errMsg = null;
            int workers = Math.Min(MaxWorkers, chunks.Count);
            List<Thread> ths = new List<Thread>();
            for (int w = 0; w < workers; w++)
            {
                int start = w;
                Thread th = new Thread(delegate()
                {
                    for (int c = start; c < chunks.Count; c += workers)
                    {
                        lock (gate) { if (errMsg != null) return; }
                        string oneErr;
                        if (!TranslateChunk(chunks[c], texts, from, to, done, out oneErr))
                        {
                            lock (gate) { if (errMsg == null) errMsg = oneErr; }
                            return;
                        }
                    }
                });
                th.IsBackground = true;
                th.Start();
                ths.Add(th);
            }
            for (int i = 0; i < ths.Count; i++) ths[i].Join();

            LastMs = Environment.TickCount - t0;
            if (errMsg != null) { LastError = errMsg; return null; }
            return done;
        }

        // 一块内所有行拼成 "行1\n行2\n行3" 一次请求；返回段数和行数对不上就退回逐行
        private static bool TranslateChunk(List<int> ids, string[] texts, string from, string to, string[] done, out string err)
        {
            err = null;
            StringBuilder q = new StringBuilder();
            for (int n = 0; n < ids.Count; n++)
            {
                if (n > 0) q.Append('\n');
                q.Append(texts[ids[n]].Replace("\r", " ").Replace("\n", " "));
            }

            string body;
            if (Request(q.ToString(), from, to, out body, out err))
            {
                string[] parts = body.Split('\n');
                if (parts.Length == ids.Count)
                {
                    for (int n = 0; n < ids.Count; n++)
                    {
                        string p = parts[n].Trim();
                        if (p.Length > 0) { done[ids[n]] = p; continue; }
                        string e1;
                        string one = TranslateOne(texts[ids[n]], from, to, out e1);
                        if (one == null) { err = e1; return false; }
                        done[ids[n]] = one;
                    }
                    return true;
                }
            }
            else if (IsRateLimit(err))
            {
                return false;   // 限流就别再逐条硬打了
            }

            // 段数对不齐（谷歌合并了句子之类）：退回逐行，保证不错位
            for (int n = 0; n < ids.Count; n++)
            {
                string e2;
                string one = TranslateOne(texts[ids[n]], from, to, out e2);
                if (one == null) { err = e2; return false; }
                done[ids[n]] = one;
            }
            return true;
        }

        private static bool IsRateLimit(string err)
        {
            return err != null && (err.IndexOf("429") >= 0 || err.IndexOf("503") >= 0);
        }

        private static string TranslateOne(string text, string from, string to, out string err)
        {
            err = null;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                string oneErr;
                string t = TranslateOnce(text, from, to, out oneErr);
                if (t != null) return t;
                // 命中限流就退避一下再试，别直接判失败
                if (IsRateLimit(oneErr))
                {
                    Thread.Sleep(500 * (attempt + 1));
                    continue;
                }
                err = oneErr;
                return null;
            }
            err = "谷歌限流（HTTP 429），等一会儿再翻";
            return null;
        }

        private static string TranslateOnce(string text, string from, string to, out string err)
        {
            err = null;
            string q = text ?? "";
            if (q.Trim().Length == 0) return "";

            string body;
            if (!Request(q, from, to, out body, out err)) return null;
            return body.Trim();
        }

        // 发一次请求，返回按 sentences 拼好的整段译文（行间用 \n 分隔）
        private static bool Request(string q, string from, string to, out string body, out string err)
        {
            body = null;
            err = null;
            if (q.Length > MaxChars) q = q.Substring(0, MaxChars);

            try { ServicePointManager.SecurityProtocol = (SecurityProtocolType)(3072 | 768 | 192); }
            catch (Exception) { }

            string url = "https://" + HostName + "/translate_a/single?client=gtx&sl=" + from
                + "&tl=" + to + "&dt=t&dj=1&q=" + Uri.EscapeDataString(q);

            HttpWebRequest req = null;
            try
            {
                req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "GET";
                req.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Safari/537.36";
                req.Timeout = 15000;
                req.ReadWriteTimeout = 15000;
                req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
                // 跟随系统代理（本地代理软件开了系统代理就能走通）
                try
                {
                    IWebProxy sysProxy = WebRequest.DefaultWebProxy;
                    if (sysProxy != null) req.Proxy = sysProxy;
                }
                catch (Exception) { }

                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                using (StreamReader sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                {
                    string json = sr.ReadToEnd();
                    string t = ParseDj(json);
                    if (t == null) { err = "谷歌返回格式不认识"; return false; }
                    body = t;
                    return true;
                }
            }
            catch (WebException wex)
            {
                if (wex.Status == WebExceptionStatus.Timeout) err = "请求超时（15 秒）";
                else
                {
                    HttpWebResponse hr = wex.Response as HttpWebResponse;
                    if (hr != null) { err = "HTTP " + (int)hr.StatusCode; hr.Close(); }
                    else err = "连不上谷歌（需要能访问 Google 的网络）";
                }
                return false;
            }
            catch (Exception ex) { err = ex.Message; return false; }
            finally { if (req != null) { try { req.Abort(); } catch (Exception) { } } }
        }

        // dj=1 返回 {"sentences":[{"trans":"译文\n","orig":"..."},…],…}
        // sentences 里每段的 trans 自带行尾换行，全部拼起来就还原成多行译文
        private static string ParseDj(string json)
        {
            if (json == null) return null;
            int at = json.IndexOf("\"sentences\"", StringComparison.Ordinal);
            if (at < 0) return null;

            StringBuilder sb = new StringBuilder();
            int i = at;
            while (i < json.Length)
            {
                int k = json.IndexOf("\"trans\":\"", i, StringComparison.Ordinal);
                if (k < 0) break;
                i = k + 9;
                while (i < json.Length)
                {
                    char ch = json[i];
                    if (ch == '\\' && i + 1 < json.Length) { i = Unescape(json, i, sb); continue; }
                    if (ch == '"') { i++; break; }
                    sb.Append(ch);
                    i++;
                }
            }
            string s = sb.ToString();
            return s.Length > 0 ? s : null;
        }

        private static int Unescape(string s, int i, StringBuilder sb)
        {
            if (i + 1 >= s.Length) { sb.Append('\\'); return i + 1; }
            char e = s[i + 1];
            switch (e)
            {
                case '"': sb.Append('"'); return i + 2;
                case '\\': sb.Append('\\'); return i + 2;
                case '/': sb.Append('/'); return i + 2;
                case 'b': sb.Append('\b'); return i + 2;
                case 'f': sb.Append('\f'); return i + 2;
                case 'n': sb.Append('\n'); return i + 2;
                case 'r': sb.Append('\r'); return i + 2;
                case 't': sb.Append('\t'); return i + 2;
                case 'u':
                    {
                        if (i + 5 < s.Length)
                        {
                            int code;
                            if (int.TryParse(s.Substring(i + 2, 4), System.Globalization.NumberStyles.HexNumber, null, out code))
                            {
                                sb.Append((char)code);
                                return i + 6;
                            }
                        }
                        sb.Append('u');
                        return i + 2;
                    }
            }
            sb.Append(e);
            return i + 2;
        }
    }
}
