using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace QRSnap
{
    // 阿里云机器翻译（通用版）在线接口：RPC 风格 + HMAC-SHA1 签名，整批走 HTTP，
    // 全程不加载本地翻译模型。返回数组与输入等长，失败返回 null 并把原因写进 LastError。
    public static class AliyunMt
    {
        private const string HostName = "mt.aliyuncs.com";
        private const string ApiVersion = "2018-10-12";
        private const string ApiRegion = "cn-hangzhou";

        public static string LastError = "";
        public static long LastMs;

        public static bool Configured
        {
            get { return !string.IsNullOrEmpty(AppConfig.AliAk) && !string.IsNullOrEmpty(AppConfig.AliSk); }
        }

        // 语言码：en / ja / zh 三家通用，中文统一用 zh
        private static string LangCode(string code, bool isTarget)
        {
            string s = (code ?? "").Trim().ToLowerInvariant();
            if (s.Length == 0) return isTarget ? "zh" : "en";
            if (s == "zh-cn" || s == "zh-hans" || s == "zh-tw" || s == "zh-hant" || s == "cmn") return "zh";
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
            if (!Configured) { LastError = "还没填阿里云 AccessKey ID / Secret"; return null; }

            long t0 = Environment.TickCount;
            string from = LangCode(src, false);
            string to = LangCode(tgt, true);
            string[] done = new string[texts.Length];

            int i = 0;
            while (i < texts.Length)
            {
                int n = Math.Min(50, texts.Length - i);   // 批量接口一次最多 50 条
                string[] one = new string[n];
                for (int k = 0; k < n; k++) one[k] = texts[i + k] ?? "";
                string[] got = TranslateChunk(one, from, to);
                if (got == null) return null;
                for (int k = 0; k < n; k++) done[i + k] = got[k];
                i += n;
            }
            LastMs = Environment.TickCount - t0;
            return done;
        }

        // 一批（最多 50 条）：优先批量接口，接口不认就退回逐条
        private static string[] TranslateChunk(string[] texts, string from, string to)
        {
            string[] batch = CallBatch(texts, from, to);
            if (batch != null) return batch;
            if (IsUnsupportedAction()) return CallOneByOne(texts, from, to);
            return null;
        }

        private static bool IsUnsupportedAction()
        {
            if (LastError == null) return false;
            // 批量接口不可用/参数不被接受时，退回逐条接口；鉴权、限流类错误不退回
            return LastError.IndexOf("InvalidApi", StringComparison.Ordinal) >= 0
                || LastError.IndexOf("UnsupportedOperation", StringComparison.Ordinal) >= 0
                || LastError.IndexOf("InvalidParameter", StringComparison.Ordinal) >= 0
                || LastError.IndexOf("UnsupportedLanguage", StringComparison.Ordinal) >= 0;
        }

        private static string[] CallBatch(string[] texts, string from, string to)
        {
            // SourceText 要 JSON 对象：key 是唯一标识，value 是待译文本
            StringBuilder arr = new StringBuilder("{");
            for (int i = 0; i < texts.Length; i++)
            {
                if (i > 0) arr.Append(',');
                arr.Append('"').Append(i + 1).Append("\":\"").Append(Esc(texts[i])).Append('"');
            }
            arr.Append('}');

            Dictionary<string, string> ps = BaseParams("GetBatchTranslate");
            ps["ApiType"] = "translate_standard";   // 通用版；专业版是 translate_ecommerce
            ps["SourceLanguage"] = from;
            ps["TargetLanguage"] = to;
            ps["Scene"] = "general";
            ps["FormatType"] = "text";
            ps["SourceText"] = arr.ToString();

            string json = PostSigned(ps);
            if (json == null) return null;

            int[] idx;
            string[] list = ParseTranslatedList(json, out idx);
            if (list == null) return null;

            string[] fixedList = new string[texts.Length];
            if (idx != null && idx.Length == list.Length)
            {
                for (int i = 0; i < list.Length; i++)
                {
                    int at = idx[i] - 1;   // Index 从 1 开始
                    if (at >= 0 && at < fixedList.Length) fixedList[at] = list[i];
                }
                return fixedList;
            }
            // 没有 Index 就按顺序放
            for (int i = 0; i < texts.Length; i++) fixedList[i] = i < list.Length ? list[i] : "";
            return fixedList;
        }

        private static string[] CallOneByOne(string[] texts, string from, string to)
        {
            string[] r = new string[texts.Length];
            for (int i = 0; i < texts.Length; i++)
            {
                Dictionary<string, string> ps = BaseParams("TranslateGeneral");
                ps["SourceLanguage"] = from;
                ps["TargetLanguage"] = to;
                ps["SourceText"] = texts[i] ?? "";
                ps["FormatType"] = "text";
                ps["Scene"] = "general";

                string json = PostSigned(ps);
                if (json == null) return null;
                string t = ParseString(json, "Translated");
                r[i] = t ?? "";
            }
            return r;
        }

        private static Dictionary<string, string> BaseParams(string action)
        {
            Dictionary<string, string> ps = new Dictionary<string, string>();
            ps["AccessKeyId"] = AppConfig.AliAk.Trim();
            ps["Action"] = action;
            ps["Format"] = "JSON";
            ps["RegionId"] = ApiRegion;
            ps["SignatureMethod"] = "HMAC-SHA1";
            ps["SignatureNonce"] = Guid.NewGuid().ToString("N");
            ps["SignatureVersion"] = "1.0";
            ps["Timestamp"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
            ps["Version"] = ApiVersion;
            return ps;
        }

        private static string PostSigned(Dictionary<string, string> ps)
        {
            try { ServicePointManager.SecurityProtocol = (SecurityProtocolType)(3072 | 768 | 192); }
            catch (Exception) { }

            // 1. 参数按编码后的名字典序排，拼规范查询串
            List<string> keys = new List<string>();
            foreach (string k in ps.Keys) keys.Add(k);
            keys.Sort(delegate(string a, string b) { return string.CompareOrdinal(Pct(a), Pct(b)); });

            StringBuilder cqs = new StringBuilder();
            for (int i = 0; i < keys.Count; i++)
            {
                if (i > 0) cqs.Append('&');
                cqs.Append(Pct(keys[i])).Append('=').Append(Pct(ps[keys[i]]));
            }

            // 2. 待签串
            string stringToSign = "POST" + "&" + Pct("/") + "&" + Pct(cqs.ToString());

            // 3. HMAC-SHA1，key 为 Secret + "&"
            string signature;
            using (HMACSHA1 h = new HMACSHA1(Encoding.UTF8.GetBytes(AppConfig.AliSk.Trim() + "&")))
                signature = Convert.ToBase64String(h.ComputeHash(Encoding.UTF8.GetBytes(stringToSign)));

            string body = cqs.ToString() + "&Signature=" + Pct(signature);
            byte[] data = Encoding.UTF8.GetBytes(body);

            HttpWebRequest req = null;
            try
            {
                req = (HttpWebRequest)WebRequest.Create("https://" + HostName + "/");
                req.Method = "POST";
                req.ContentType = "application/x-www-form-urlencoded";
                req.UserAgent = "SnapScan";
                req.Timeout = 15000;
                req.ReadWriteTimeout = 15000;
                req.ContentLength = data.Length;
                using (Stream s = req.GetRequestStream()) s.Write(data, 0, data.Length);
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                using (StreamReader sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                {
                    string json = sr.ReadToEnd();
                    string code = ParseString(json, "Code");
                    if (code != null && code.Length > 0 && code != "200")
                    {
                        LastError = code + " " + (ParseString(json, "Message") ?? "");
                        return null;
                    }
                    return json;
                }
            }
            catch (WebException wex)
            {
                try
                {
                    HttpWebResponse hr = wex.Response as HttpWebResponse;
                    if (hr != null)
                    {
                        string detail = "";
                        using (StreamReader sr = new StreamReader(hr.GetResponseStream(), Encoding.UTF8))
                            detail = sr.ReadToEnd();
                        string code = ParseString(detail, "Code");
                        string msg = ParseString(detail, "Message");
                        LastError = code != null ? (code + " " + (msg ?? "")) : ("HTTP " + (int)hr.StatusCode);
                        hr.Close();
                    }
                    else LastError = wex.Message;
                }
                catch (Exception) { LastError = wex.Message; }
                if (wex.Status == WebExceptionStatus.Timeout) LastError = "请求超时（15 秒）";
                return null;
            }
            catch (Exception ex) { LastError = ex.Message; return null; }
            finally { if (req != null) { try { req.Abort(); } catch (Exception) { } } }
        }

        // RFC3986 编码：只放过 A-Z a-z 0-9 - _ . ~，空格编码成 %20
        private static string Pct(string s)
        {
            if (s == null) s = "";
            StringBuilder sb = new StringBuilder(s.Length * 2);
            byte[] b = Encoding.UTF8.GetBytes(s);
            for (int i = 0; i < b.Length; i++)
            {
                byte c = b[i];
                if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')
                    || c == '-' || c == '_' || c == '.' || c == '~')
                    sb.Append((char)c);
                else
                    sb.Append('%').Append(c.ToString("X2"));
            }
            return sb.ToString();
        }

        // ---------------- 极简 JSON 取值（响应结构固定，够用） ----------------

        private static string[] ParseTranslatedList(string json, out int[] idx)
        {
            idx = null;
            if (json == null) return null;
            int a = json.IndexOf("\"TranslatedList\"", StringComparison.Ordinal);
            if (a < 0) return null;
            int c = a + 16;
            while (c < json.Length && IsSpace(json[c])) c++;
            if (c < json.Length && json[c] == ':') c++;
            while (c < json.Length && IsSpace(json[c])) c++;
            if (c >= json.Length || json[c] != '[') return null;

            List<string> list = new List<string>();
            List<int> ids = new List<int>();
            bool hasIdx = true;

            while (c < json.Length)
            {
                while (c < json.Length && json[c] != '{' && json[c] != ']') c++;
                if (c >= json.Length || json[c] == ']') break;

                // 取这个对象的 { ... }
                int start = c;
                int depth = 0;
                bool inStr = false;
                while (c < json.Length)
                {
                    char ch = json[c];
                    if (inStr)
                    {
                        if (ch == '\\') c++;
                        else if (ch == '"') inStr = false;
                    }
                    else
                    {
                        if (ch == '"') inStr = true;
                        else if (ch == '{') depth++;
                        else if (ch == '}')
                        {
                            depth--;
                            if (depth == 0) { c++; break; }
                        }
                    }
                    c++;
                }
                int end = c;
                if (end <= start) break;
                string obj = json.Substring(start, end - start);

                string t = ParseString(obj, "translated");
                if (t == null) t = ParseString(obj, "Translated");
                string idStr = ParseString(obj, "index");
                if (idStr == null) idStr = ParseString(obj, "Index");
                list.Add(t ?? "");
                int id = 0;
                if (idStr != null && int.TryParse(idStr, out id)) ids.Add(id);
                else hasIdx = false;
            }

            if (list.Count == 0) return null;
            if (hasIdx && ids.Count == list.Count) idx = ids.ToArray();
            return list.ToArray();
        }

        private static string ParseString(string json, string key)
        {
            if (json == null) return null;
            int a = json.IndexOf("\"" + key + "\"", StringComparison.Ordinal);
            if (a < 0) return null;
            int c = a + key.Length + 2;
            while (c < json.Length && IsSpace(json[c])) c++;
            if (c < json.Length && json[c] == ':') c++;
            while (c < json.Length && IsSpace(json[c])) c++;
            if (c >= json.Length || json[c] != '"') return null;
            c++;
            StringBuilder sb = new StringBuilder();
            while (c < json.Length)
            {
                char ch = json[c];
                if (ch == '\\') { c = Unescape(json, c, sb); continue; }
                if (ch == '"') break;
                sb.Append(ch);
                c++;
            }
            return sb.ToString();
        }

        private static bool IsSpace(char c)
        {
            return c == ' ' || c == '\t' || c == '\r' || c == '\n';
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

        private static string Esc(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            StringBuilder sb = new StringBuilder(s.Length + 8);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 32) sb.Append(' ');
                        else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }
    }
}
