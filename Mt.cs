using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace QRSnap
{
    // ---------- 语言检测（纯本地，按文字系统判，拉丁字母默认英语） ----------
    public static class LangDetect
    {
        public static string Detect(string text)
        {
            if (string.IsNullOrEmpty(text)) return "en";
            int han = 0, kana = 0, hangul = 0, cyr = 0, latin = 0, other = 0;
            foreach (char c in text)
            {
                if (c < 128) { if (char.IsLetter(c)) latin++; continue; }
                if (c >= 0x4E00 && c <= 0x9FFF) han++;
                else if (c >= 0x3040 && c <= 0x30FF) kana++;
                else if (c >= 0xAC00 && c <= 0xD7AF) hangul++;
                else if (c >= 0x0400 && c <= 0x04FF) cyr++;
                else if (char.IsLetter(c)) other++;
            }
            if (kana > 0 && (kana + han) > 0 && kana * 4 >= han) return "ja";
            // 判「这段已经是中文」要严格：以前只要出现一个汉字就判中文，
            // 英文页面里混进一个汉字（或 OCR 把一个汉字认进来）就整段不再翻译。
            // 现在要求汉字占（汉字+拉丁字母）的 75% 以上、且至少 4 个汉字。
            int cjkLatin = han + latin;
            if (han >= 4 && cjkLatin > 0 && han * 4 >= cjkLatin * 3) return "zh";
            if (hangul > 0) return "ko";
            if (cyr > 0 && cyr > latin) return "ru";
            if (latin > 0) return "en";
            if (other > 0) return "en";
            return "en";
        }
    }

    // ---------- SentencePiece(Unigram) 分词，直接读 tokenizer.json 里的 vocab ----------
    internal sealed class UnigramTok
    {
        private readonly List<string> _pieces = new List<string>();
        private readonly List<float> _scores = new List<float>();
        private readonly Dictionary<string, int> _ids = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _byFirst = new Dictionary<string, string>(StringComparer.Ordinal);
        private float _unkScore = -20f;
        private int _maxLen = 1;
        private readonly List<List<int>> _bucket = new List<List<int>>();
        private readonly Dictionary<char, int> _bucketOf = new Dictionary<char, int>();

        public int UnkId { get; private set; }
        public int EosId { get; private set; }
        public int PadId { get; private set; }

        public int Count { get { return _pieces.Count; } }

        public UnigramTok(string tokenizerJsonPath)
        {
            string text = File.ReadAllText(tokenizerJsonPath, Encoding.UTF8);
            ParseVocab(text);
            for (int i = 0; i < _pieces.Count; i++)
            {
                if (!_ids.ContainsKey(_pieces[i])) _ids[_pieces[i]] = i;
                if (_pieces[i] == "<unk>") UnkId = i;
                if (_pieces[i] == "</s>") EosId = i;
                if (_pieces[i] == "<pad>") PadId = i;
            }
            // 首字符分桶，避免每次全表比较
            for (int i = 0; i < _pieces.Count; i++)
            {
                string p = _pieces[i];
                if (p.Length == 0) continue;
                char c = p[0];
                int bi;
                if (!_bucketOf.TryGetValue(c, out bi)) { bi = _bucket.Count; _bucketOf[c] = bi; _bucket.Add(new List<int>()); }
                _bucket[bi].Add(i);
                if (p.Length > _maxLen) _maxLen = p.Length;
            }
            if (UnkId >= 0 && UnkId < _scores.Count) _unkScore = _scores[UnkId] - 10f;
        }

        private void ParseVocab(string json)
        {
            int at = json.IndexOf("\"vocab\"", StringComparison.Ordinal);
            if (at < 0) throw new InvalidDataException("tokenizer.json 里没找到 vocab");
            int i = at + 7;
            int n = json.Length;
            i = SkipWs(json, i);
            if (i >= n || json[i] != ':') throw new InvalidDataException("vocab 字段格式异常");
            i = SkipWs(json, i + 1);
            if (i >= n || json[i] != '[') throw new InvalidDataException("vocab 不是数组");
            i++;
            while (i < n)
            {
                i = SkipWs(json, i);
                if (i >= n) break;
                if (json[i] == ']') break;
                if (json[i] != '[') { i++; continue; }
                i++;
                i = SkipWs(json, i);
                if (i >= n || json[i] != '"') break;
                int p = i + 1;
                StringBuilder sb = new StringBuilder(16);
                while (p < n)
                {
                    char ch = json[p];
                    if (ch == '\\')
                    {
                        if (p + 1 >= n) break;
                        char e = json[p + 1];
                        if (e == 'u')
                        {
                            int code = 0;
                            for (int k = 0; k < 4 && p + 2 + k < n; k++)
                            {
                                char h = json[p + 2 + k];
                                int v = h >= '0' && h <= '9' ? h - '0' : (h >= 'a' && h <= 'f' ? h - 'a' + 10 : (h >= 'A' && h <= 'F' ? h - 'A' + 10 : 0));
                                code = code * 16 + v;
                            }
                            sb.Append((char)code);
                            p += 6;
                            continue;
                        }
                        if (e == 'n') { sb.Append('\n'); p += 2; continue; }
                        if (e == 'r') { sb.Append('\r'); p += 2; continue; }
                        if (e == 't') { sb.Append('\t'); p += 2; continue; }
                        sb.Append(e);
                        p += 2;
                        continue;
                    }
                    if (ch == '"') { p++; break; }
                    sb.Append(ch);
                    p++;
                }
                i = SkipWs(json, p);
                if (i < n && json[i] == ',') i = SkipWs(json, i + 1);
                int start = i;
                while (i < n && json[i] != ']' && json[i] != ',' && json[i] != '\n' && json[i] != ' ') i++;
                float score = 0f;
                if (i > start)
                    float.TryParse(json.Substring(start, i - start), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out score);
                _pieces.Add(sb.ToString());
                _scores.Add(score);
                i = SkipWs(json, i);
                if (i < n && json[i] == ',') i++;
                i = SkipWs(json, i);
                if (i < n && json[i] == ']') i++;
            }
        }

        private static int SkipWs(string s, int i)
        {
            while (i < s.Length)
            {
                char c = s[i];
                if (c == ' ' || c == '\t' || c == '\r' || c == '\n') i++;
                else break;
            }
            return i;
        }

        // 空格换成 ▁，并在开头补一个 ▁（等价 Metaspace + add_prefix_space）
        public static string Prepare(string text)
        {
            StringBuilder sb = new StringBuilder(text.Length + 8);
            sb.Append('▁');
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == ' ' || c == '\t' || c == '\r' || c == '\n') sb.Append('▁');
                else sb.Append(c);
            }
            return sb.ToString();
        }

        public int[] Encode(string text, int maxLen)
        {
            string s = Prepare(text);
            int n = s.Length;
            if (n > maxLen) { s = s.Substring(0, maxLen); n = maxLen; }
            float[] best = new float[n + 1];
            int[] from = new int[n + 1];
            int[] pick = new int[n + 1];
            for (int i = 1; i <= n; i++) { best[i] = float.NegativeInfinity; from[i] = i - 1; pick[i] = -1; }
            best[0] = 0f;
            for (int i = 0; i < n; i++)
            {
                if (best[i] == float.NegativeInfinity) continue;
                int bi;
                if (!_bucketOf.TryGetValue(s[i], out bi)) { bi = -1; }
                if (bi >= 0)
                {
                    List<int> list = _bucket[bi];
                    for (int k = 0; k < list.Count; k++)
                    {
                        int id = list[k];
                        string p = _pieces[id];
                        int len = p.Length;
                        if (len == 0 || i + len > n) continue;
                        if (string.CompareOrdinal(s, i, p, 0, len) != 0) continue;
                        float sc = best[i] + _scores[id];
                        if (sc > best[i + len]) { best[i + len] = sc; from[i + len] = i; pick[i + len] = id; }
                    }
                }
                // 未登录字：按单字符吃掉
                {
                    float sc = best[i] + _unkScore;
                    if (sc > best[i + 1]) { best[i + 1] = sc; from[i + 1] = i; pick[i + 1] = -1; }
                }
            }
            List<int> ids = new List<int>();
            int pos = n;
            while (pos > 0)
            {
                int p = from[pos];
                int id = pick[pos];
                if (id >= 0) ids.Add(id);
                else
                {
                    string ch = s.Substring(p, pos - p);
                    int v;
                    if (_ids.TryGetValue(ch, out v)) ids.Add(v);
                    else ids.Add(UnkId);
                }
                pos = p;
            }
            ids.Reverse();
            return ids.ToArray();
        }

        public string DecodeId(int id)
        {
            if (id < 0 || id >= _pieces.Count) return "";
            return _pieces[id];
        }

        public string DecodeAll(IEnumerable<int> ids)
        {
            StringBuilder sb = new StringBuilder();
            foreach (int id in ids)
            {
                if (id == EosId || id == PadId) continue;
                string p = DecodeId(id);
                if (p.Length == 0) continue;
                if (p == "<unk>") { sb.Append('□'); continue; }
                if (p.Length > 0 && p[0] == '<' && p[p.Length - 1] == '>') continue;
                sb.Append(p);
            }
            return sb.ToString().Replace('▁', ' ').Trim();
        }
    }

    // ---------- 一个 Opus-MT 模型（encoder + merged decoder，ONNX Runtime） ----------
    public sealed class MtModel : IDisposable
    {
        public const string EncFile = "encoder_model_quantized.onnx";
        public const string DecFile = "decoder_model_merged_quantized.onnx";
        public const string PlainFile = "decoder_model_quantized.onnx";
        public const string TokFile = "tokenizer.json";
        public const string CfgFile = "config.json";

        private InferenceSession _enc;        private InferenceSession _dec;
        private InferenceSession _decPlain;
        private UnigramTok _tok;
        private int _layers = 6;
        private int _heads = 8;
        private int _headDim = 64;
        private int _startId = 65000;
        private int _eosId = 0;
        private int _vocab = 65001;
        private string _label;

        public string Label { get { return _label; } }
        public int VocabCount { get { return _vocab; } }

        public static bool FolderReady(string dir)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return false;
            // merged 版解码器只在旧路径里用过，现在解码走 plain 版；有就带着，没有也能跑
            return File.Exists(Path.Combine(dir, EncFile))
                && File.Exists(Path.Combine(dir, PlainFile))
                && File.Exists(Path.Combine(dir, TokFile));
        }

        public MtModel(string dir, string label)
        {
            _label = label;
            if (!FolderReady(dir)) throw new FileNotFoundException("缺少翻译模型文件", dir);
            _tok = new UnigramTok(Path.Combine(dir, TokFile));

            SessionOptions so = new SessionOptions();
            try { so.IntraOpNumThreads = Math.Max(1, Environment.ProcessorCount / 2); } catch { }
            try { so.InterOpNumThreads = 1; } catch { }
            so.EnableCpuMemArena = true;

            _enc = new InferenceSession(Path.Combine(dir, EncFile), so);
            string merged = Path.Combine(dir, DecFile);
            if (File.Exists(merged)) _dec = new InferenceSession(merged, so);
            string plain = Path.Combine(dir, PlainFile);
            if (File.Exists(plain)) _decPlain = new InferenceSession(plain, so);

            string cfg = Path.Combine(dir, CfgFile);
            if (File.Exists(cfg)) ReadConfig(cfg);
            _startId = _tok.PadId >= 0 ? _tok.PadId : 65000;
            _eosId = _tok.EosId;
            _vocab = _tok.Count;
        }

        private void ReadConfig(string path)
        {
            string s = File.ReadAllText(path, Encoding.UTF8);
            _layers = IntOf(s, "\"decoder_layers\":", 6);
            _heads = IntOf(s, "\"decoder_attention_heads\":", 8);
            int d = IntOf(s, "\"d_model\":", 512);
            _headDim = d > 0 && _heads > 0 ? d / _heads : 64;
            _startId = IntOf(s, "\"decoder_start_token_id\":", 65000);
            _eosId = IntOf(s, "\"eos_token_id\":", 0);
            _vocab = IntOf(s, "\"decoder_vocab_size\":", IntOf(s, "\"vocab_size\":", 65001));
        }

        private static int IntOf(string s, string key, int fallback)
        {
            int at = s.IndexOf(key, StringComparison.Ordinal);
            if (at < 0) return fallback;
            int i = at + key.Length;
            while (i < s.Length && (s[i] == ' ' || s[i] == '\t')) i++;
            int start = i;
            while (i < s.Length && ((s[i] >= '0' && s[i] <= '9') || s[i] == '-')) i++;
            int v;
            if (int.TryParse(s.Substring(start, i - start), out v)) return v;
            return fallback;
        }

        public void Dispose()
        {
            try { if (_enc != null) _enc.Dispose(); } catch { }
            try { if (_dec != null) _dec.Dispose(); } catch { }
            try { if (_decPlain != null) _decPlain.Dispose(); } catch { }
            _enc = null; _dec = null; _decPlain = null;
        }

        // 调试用：把源句按当前分词器切一遍，检查是不是切错了
        public string TokenDebug(string text)
        {
            int[] ids = _tok.Encode(text, 500);
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < ids.Length; i++)
            {
                if (i > 0) sb.Append('|');
                sb.Append(_tok.DecodeId(ids[i])).Append('#').Append(ids[i]);
            }
            return sb.ToString();
        }

        // 贪心解码：够快，屏幕短句质量够用
        public string Translate(string text, int maxTokens)
        {
            if (string.IsNullOrEmpty(text)) return "";
            int[] srcIds = _tok.Encode(text, 500);
            if (srcIds.Length == 0) return "";
            int[] inIds = new int[srcIds.Length + 1];
            for (int i = 0; i < srcIds.Length; i++) inIds[i] = srcIds[i];
            inIds[srcIds.Length] = _eosId;

            long[] ids = new long[inIds.Length];
            long[] mask = new long[inIds.Length];
            for (int i = 0; i < inIds.Length; i++) { ids[i] = inIds[i]; mask[i] = 1; }

            Tensor<long> tIds = new DenseTensor<long>(ids, new int[] { 1, ids.Length });
            Tensor<long> tMask = new DenseTensor<long>(mask, new int[] { 1, mask.Length });

            float[] encOut;
            int seqLen;
            using (var run = _enc.Run(new[] {
                NamedOnnxValue.CreateFromTensor("input_ids", tIds),
                NamedOnnxValue.CreateFromTensor("attention_mask", tMask) }))
            {
                var v = run[0];
                var t = v.AsTensor<float>();
                int[] dims = t.Dimensions.ToArray();
                seqLen = dims[1];
                int hidden = dims[2];
                encOut = new float[seqLen * hidden];
                int p = 0;
                for (int a = 0; a < 1; a++)
                    for (int b = 0; b < seqLen; b++)
                        for (int c = 0; c < hidden; c++)
                            encOut[p++] = t[a, b, c];
            }

            Tensor<float> encT = new DenseTensor<float>(encOut, new int[] { 1, seqLen, 512 });

            List<int> outIds = new List<int>();

            // 第一步必须走“不带 cache”的解码器：merged 图在 zero-length KV 上会崩
            // （onnxruntime 报 encoder_attn Reshape 的 0 维越界），所以先用无 past 版算首词和 KV
            IDisposableReadOnlyCollection<DisposableNamedOnnxValue> res = null;
            Dictionary<string, float[]> past;
            int next;
            try
            {
                res = RunPlain(_decPlain != null ? _decPlain : _dec, encT, tMask, _startId);
                next = ArgMaxLast(res);
                past = GrabPast(res);
            }
            finally
            {
                if (res != null) res.Dispose();
            }

            if (next != _eosId && next != _startId)
            {
                outIds.Add(next);
                for (int step = 1; step < maxTokens; step++)
                {
                    res = RunMerged(encT, tMask, next, past);
                    try
                    {
                        int nt = ArgMaxLast(res);
                        past = GrabPast(res);
                        if (nt == _eosId || nt == _startId) break;
                        outIds.Add(nt);
                        next = nt;
                    }
                    finally
                    {
                        res.Dispose();
                    }
                }
            }
            return _tok.DecodeAll(outIds);
        }

        // 不借助 KV cache 的解码：每一步把整段前缀重算一遍（merged 图在部分 ORT 版本上首步会崩，这条是兜底路径）
        public string TranslateNoCache(string text, int maxTokens)
        {
            if (string.IsNullOrEmpty(text)) return "";
            int[] srcIds = _tok.Encode(text, 500);
            if (srcIds.Length == 0) return "";

            int[] inIds = new int[srcIds.Length + 1];
            for (int i = 0; i < srcIds.Length; i++) inIds[i] = srcIds[i];
            inIds[srcIds.Length] = _eosId;

            long[] ids = new long[inIds.Length];
            long[] mask = new long[inIds.Length];
            for (int i = 0; i < inIds.Length; i++) { ids[i] = inIds[i]; mask[i] = 1; }

            Tensor<long> tIds = new DenseTensor<long>(ids, new int[] { 1, ids.Length });
            Tensor<long> tMask = new DenseTensor<long>(mask, new int[] { 1, mask.Length });

            float[] encOut;
            int seqLen;
            using (var run = _enc.Run(new[] {
                NamedOnnxValue.CreateFromTensor("input_ids", tIds),
                NamedOnnxValue.CreateFromTensor("attention_mask", tMask) }))
            {
                var t = run[0].AsTensor<float>();
                int[] dims = t.Dimensions.ToArray();
                seqLen = dims[1];
                int hidden = dims[2];
                encOut = new float[seqLen * hidden];
                int p = 0;
                for (int b = 0; b < seqLen; b++)
                    for (int c = 0; c < hidden; c++)
                        encOut[p++] = t[0, b, c];
            }
            Tensor<float> encT = new DenseTensor<float>(encOut, new int[] { 1, seqLen, 512 });

            InferenceSession dec = _decPlain != null ? _decPlain : _dec;
            List<int> outIds = new List<int>();
            List<long> cur = new List<long>();
            cur.Add(_startId);
            Dictionary<int, int> used = new Dictionary<int, int>();
            int lastId = -1;
            string lastPiece = null;
            for (int step = 0; step < maxTokens; step++)
            {
                long[] arr = new long[cur.Count];
                for (int i = 0; i < cur.Count; i++) arr[i] = cur[i];
                Tensor<long> one = new DenseTensor<long>(arr, new int[] { 1, arr.Length });
                using (var run = dec.Run(new[] {
                    NamedOnnxValue.CreateFromTensor("input_ids", one),
                    NamedOnnxValue.CreateFromTensor("encoder_hidden_states", encT),
                    NamedOnnxValue.CreateFromTensor("encoder_attention_mask", tMask) }))
                {
                    int best = ArgMaxAt(run, arr.Length - 1, used);
                    if (best == _eosId || best == _startId) break;
                    string piece = _tok.DecodeId(best);
                    if (lastId == best) break;   // 同一个 token 连着蹦就收手
                    if (piece != null && piece.Length > 0 && piece == lastPiece) break;  // 换个 token 但字面一样，也是复读
                    lastPiece = piece;
                    lastId = best;
                    outIds.Add(best);
                    cur.Add(best);
                    used[best] = used.ContainsKey(best) ? used[best] + 1 : 1;
                }
            }
            return _tok.DecodeAll(outIds);
        }

        private int ArgMaxAt(IDisposableReadOnlyCollection<DisposableNamedOnnxValue> res, int pos, Dictionary<int, int> used)
        {
            foreach (var v in res)
            {
                Tensor<float> t = v.AsTensor<float>();
                int[] dims = t.Dimensions.ToArray();
                if (dims.Length != 3) continue;
                int vocab = dims[2];
                int best = 0;
                float bv = float.NegativeInfinity;
                for (int i = 0; i < vocab; i++)
                {
                    float f = t[0, pos, i];
                    if (used != null && used.ContainsKey(i))
                    {
                        // 复读的词轻微降权即可；压太狠会把正常词顶到奇怪的同义项上
                        if (f < 0f) f *= 1.1f;
                        else f /= 1.1f;
                    }
                    if (f > bv) { bv = f; best = i; }
                }
                return best;
            }
            return _eosId;
        }

        private IDisposableReadOnlyCollection<DisposableNamedOnnxValue> RunPlain(InferenceSession s, Tensor<float> encT, Tensor<long> mask, int startId)
        {
            Tensor<long> one = new DenseTensor<long>(new long[] { startId }, new int[] { 1, 1 });
            return s.Run(new[] {
                NamedOnnxValue.CreateFromTensor("input_ids", one),
                NamedOnnxValue.CreateFromTensor("encoder_hidden_states", encT),
                NamedOnnxValue.CreateFromTensor("encoder_attention_mask", mask) });
        }

        private IDisposableReadOnlyCollection<DisposableNamedOnnxValue> RunMerged(Tensor<float> encT, Tensor<long> mask, int token, Dictionary<string, float[]> past)
        {
            Tensor<long> one = new DenseTensor<long>(new long[] { token }, new int[] { 1, 1 });
            List<NamedOnnxValue> feed = new List<NamedOnnxValue>();
            feed.Add(NamedOnnxValue.CreateFromTensor("input_ids", one));
            feed.Add(NamedOnnxValue.CreateFromTensor("encoder_hidden_states", encT));
            feed.Add(NamedOnnxValue.CreateFromTensor("encoder_attention_mask", mask));
            feed.Add(NamedOnnxValue.CreateFromTensor("use_cache_branch", new DenseTensor<bool>(new bool[] { true }, new int[] { 1 })));
            for (int l = 0; l < _layers; l++)
            {
                AddPast(feed, past, "present." + l + ".decoder.key", "past_key_values." + l + ".decoder.key");
                AddPast(feed, past, "present." + l + ".decoder.value", "past_key_values." + l + ".decoder.value");
                AddPast(feed, past, "present." + l + ".encoder.key", "past_key_values." + l + ".encoder.key");
                AddPast(feed, past, "present." + l + ".encoder.value", "past_key_values." + l + ".encoder.value");
            }
            return _dec.Run(feed);
        }

        private void AddPast(List<NamedOnnxValue> feed, Dictionary<string, float[]> past, string key, string inputName)
        {
            float[] buf = past[key];
            int[] shape = new int[] { 1, _heads, buf.Length / (_heads * _headDim), _headDim };
            feed.Add(NamedOnnxValue.CreateFromTensor(inputName, new DenseTensor<float>(buf, shape)));
        }

        private int ArgMaxLast(IDisposableReadOnlyCollection<DisposableNamedOnnxValue> res)
        {
            foreach (var v in res)
            {
                int[] dims = v.AsTensor<float>().Dimensions.ToArray();
                if (dims.Length != 3) continue;
                Tensor<float> t = v.AsTensor<float>();
                int vocab = dims[2];
                if (vocab < 2) continue;
                int best = 0;
                float bv = float.NegativeInfinity;
                for (int i = 0; i < vocab; i++)
                {
                    float f = t[0, 0, i];
                    if (f > bv) { bv = f; best = i; }
                }
                return best;
            }
            return _eosId;
        }

        private Dictionary<string, float[]> GrabPast(IDisposableReadOnlyCollection<DisposableNamedOnnxValue> res)
        {
            Dictionary<string, float[]> map = new Dictionary<string, float[]>(StringComparer.Ordinal);
            foreach (var v in res)
            {
                if (!v.Name.StartsWith("present.", StringComparison.Ordinal)) continue;
                Tensor<float> t = v.AsTensor<float>();
                int[] dims = t.Dimensions.ToArray();
                float[] buf = new float[dims[0] * dims[1] * dims[2] * dims[3]];
                int p = 0;
                for (int a = 0; a < dims[0]; a++)
                    for (int b = 0; b < dims[1]; b++)
                        for (int c = 0; c < dims[2]; c++)
                            for (int d = 0; d < dims[3]; d++)
                                buf[p++] = t[a, b, c, d];
                map[v.Name] = buf;
            }
            return map;
        }
    }

    // ---------- 语言对路由 ----------
    // 一个“翻译模型”= MT Model 下的一个目录，目录里是一对语言的权重。
    // 目录名按 en-zh 这样写，或者目录里放一个 model.json {"src":"en","tgt":"zh"}。
    public sealed class MtModelInfo
    {
        public string Name;     // 目录名 / GGUF 文件名
        public string Src;
        public string Tgt;
        public string Dir;
        public bool Ready;
        public string Gguf;     // 非空表示 GGUF 单文件模型（一个模型覆盖多语种）
        public string Label;    // 界面显示名
        public bool Multi;      // 是否支持多方向
        public string Pair { get { return Multi ? "多语种" : (Src + "-" + Tgt); } }
    }

    public sealed class MtRouter : IDisposable
    {
        private readonly string _root;
        private readonly Dictionary<string, MtModel> _cache = new Dictionary<string, MtModel>(StringComparer.Ordinal);
        private readonly object _lock = new object();

        public MtRouter(string root) { _root = root; }

        public string Root { get { return _root; } }

        public string DirOf(string src, string tgt) { return Path.Combine(_root, src + "-" + tgt); }

        // 从目录名认语言对：en-zh / zh-en / ja-zh…
        private static bool TryParsePair(string name, out string src, out string tgt)
        {
            src = tgt = null;
            int i = name.LastIndexOf('-');
            if (i <= 0 || i >= name.Length - 1) return false;
            string a = name.Substring(0, i).Trim().ToLowerInvariant();
            string b = name.Substring(i + 1).Trim().ToLowerInvariant();
            if (!IsLangCode(a) || !IsLangCode(b)) return false;
            src = a; tgt = b;
            return true;
        }

        private static bool IsLangCode(string s)
        {
            if (s.Length < 2 || s.Length > 3) return false;
            for (int i = 0; i < s.Length; i++) if (s[i] < 'a' || s[i] > 'z') return false;
            return true;
        }

        // model.json 里读 src / tgt（只认这两个字段，够用）
        private static string ReadSidecar(string dir, string key)
        {
            try
            {
                string f = Path.Combine(dir, "model.json");
                if (!File.Exists(f)) return "";
                string s = File.ReadAllText(f);
                int k = s.IndexOf("\"" + key + "\"", StringComparison.OrdinalIgnoreCase);
                if (k < 0) return "";
                int c = s.IndexOf(':', k);
                if (c < 0) return "";
                int q1 = s.IndexOf('"', c + 1);
                if (q1 < 0) return "";
                int q2 = s.IndexOf('"', q1 + 1);
                if (q2 < 0) return "";
                return s.Substring(q1 + 1, q2 - q1 - 1).Trim().ToLowerInvariant();
            }
            catch (Exception) { return ""; }
        }

        // 扫描目录下所有能认出语言对的模型（含没装全的，方便界面置灰显示）
        public List<MtModelInfo> Models()
        {
            List<MtModelInfo> list = new List<MtModelInfo>();
            if (!Directory.Exists(_root)) return list;

            // GGUF 单文件模型：一个文件就能翻译多个语种方向（Hy-MT 这类），排在前面
            string[] ggufs = null;
            try { ggufs = Directory.GetFiles(_root, "*.gguf", SearchOption.AllDirectories); }
            catch (Exception) { ggufs = null; }
            if (ggufs != null)
            {
                string rootFull = Path.GetFullPath(_root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                for (int i = 0; i < ggufs.Length; i++)
                {
                    string f = ggufs[i];
                    string rel = Path.GetDirectoryName(Path.GetFullPath(f));
                    if (rel == null) continue;
                    rel = rel.Substring(Math.Min(rel.Length, rootFull.Length)).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    // 最多下钻两层，免得把乱七八糟的目录扫进来
                    if (rel.Length > 0 && (rel.Split(Path.DirectorySeparatorChar).Length > 2)) continue;
                    long size = 0;
                    try { size = new FileInfo(f).Length; } catch (Exception) { }
                    if (size < 100L * 1024L * 1024L) continue;   // 小于 100MB 的不像翻译模型
                    MtModelInfo m = new MtModelInfo();
                    m.Name = Path.GetFileNameWithoutExtension(f);
                    m.Label = m.Name;
                    m.Src = "any";
                    m.Tgt = "zh";
                    m.Dir = Path.GetDirectoryName(f);
                    m.Gguf = f;
                    m.Multi = true;
                    m.Ready = LlamaMt.RuntimeReady;
                    list.Add(m);
                }
            }

            string[] dirs;
            try { dirs = Directory.GetDirectories(_root); }
            catch (Exception) { return list; }
            for (int i = 0; i < dirs.Length; i++)
            {
                string d = dirs[i];
                string name = Path.GetFileName(d);
                string src, tgt;
                if (!TryParsePair(name, out src, out tgt))
                {
                    src = ReadSidecar(d, "src");
                    tgt = ReadSidecar(d, "tgt");
                    if (src == null || tgt == null || !IsLangCode(src) || !IsLangCode(tgt)) continue;
                }
                MtModelInfo m = new MtModelInfo();
                m.Name = name;
                m.Src = src;
                m.Tgt = tgt;
                m.Dir = d;
                m.Ready = MtModel.FolderReady(d);
                list.Add(m);
            }
            list.Sort(delegate(MtModelInfo a, MtModelInfo b) { return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase); });
            return list;
        }

        public bool PairReady(string src, string tgt)
        {
            foreach (MtModelInfo m in Models())
                if (m.Ready && m.Src == src && m.Tgt == tgt) return true;
            return false;
        }

        public static string NameOf(string code)
        {
            if (code == "zh") return "中文";
            if (code == "en") return "英语";
            if (code == "ja") return "日语";
            if (code == "ko") return "韩语";
            if (code == "ru") return "俄语";
            if (code == "fr") return "法语";
            if (code == "de") return "德语";
            return code;
        }

        public string[] PlanFor(string src, string tgt) { return PlanFor(src, tgt, null); }

        // src→tgt 没有直连模型时用英语中转；preferred 是用户选中的模型目录名
        public string[] PlanFor(string src, string tgt, string preferred)
        {
            if (src == tgt) return new string[0];
            List<MtModelInfo> all = Models();
            // 多语种 GGUF 模型：一个模型就能覆盖所有方向，优先用它
            MtModelInfo pick = null;
            if (preferred != null && preferred.Length > 0)
                foreach (MtModelInfo m in all)
                    if (m.Multi && m.Ready && string.Equals(m.Name, preferred, StringComparison.OrdinalIgnoreCase)) { pick = m; break; }
            if (pick == null)
                foreach (MtModelInfo m in all)
                    if (m.Multi && m.Ready) { pick = m; break; }
            if (pick != null) return new string[] { "gguf", pick.Gguf };
            if (preferred != null && preferred.Length > 0)
            {
                foreach (MtModelInfo m in all)
                    if (m.Ready && m.Src == src && m.Tgt == tgt && string.Equals(m.Name, preferred, StringComparison.OrdinalIgnoreCase))
                        return new string[] { src + "-" + tgt, m.Name };
            }
            if (PairReady(src, tgt)) return new string[] { src + "-" + tgt };
            if (src != "en" && tgt != "en" && PairReady(src, "en") && PairReady("en", tgt))
                return new string[] { src + "-en", "en-" + tgt };
            return new string[0];
        }

        public List<string> AvailablePairs()
        {
            List<string> list = new List<string>();
            foreach (MtModelInfo m in Models())
            {
                if (!m.Ready) continue;
                if (m.Multi) { if (!list.Contains("多语种")) list.Add("多语种"); continue; }
                if (!list.Contains(m.Pair)) list.Add(m.Pair);
            }
            return list;
        }

        // 找到某对语言该用哪个目录：优先用户选中的模型，否则第一个装好的
        private string DirForPair(string pair, string preferred)
        {
            List<MtModelInfo> all = Models();
            if (preferred != null && preferred.Length > 0)
            {
                foreach (MtModelInfo m in all)
                    if (m.Ready && string.Equals(m.Name, preferred, StringComparison.OrdinalIgnoreCase) && m.Pair == pair) return m.Dir;
            }
            foreach (MtModelInfo m in all)
                if (m.Ready && m.Pair == pair) return m.Dir;
            return null;
        }

        private MtModel Get(string pair, string preferred)
        {
            string dir = DirForPair(pair, preferred);
            if (dir == null) dir = Path.Combine(_root, pair);
            lock (_lock)
            {
                MtModel m;
                if (_cache.TryGetValue(dir, out m)) return m;
                m = new MtModel(dir, pair);
                _cache[dir] = m;
                return m;
            }
        }

        public string Translate(string text, string src, string tgt, out string route)
        {
            return Translate(text, src, tgt, null, out route);
        }

        public string Translate(string text, string src, string tgt, string preferred, out string route)
        {
            route = "";
            if (string.IsNullOrEmpty(text)) return "";
            if (src == tgt) return text;
            string[] plan = PlanFor(src, tgt, preferred);
            if (plan.Length == 0) { route = "缺模型"; return ""; }
            if (plan[0] == "gguf")
            {
                route = "gguf · " + Path.GetFileNameWithoutExtension(plan[1]);
                return LlamaMt.Translate(plan[1], text, tgt);
            }
            string cur = text;
            for (int i = 0; i < plan.Length; i += 2)
            {
                string pair = plan[i];
                string model = i + 1 < plan.Length ? plan[i + 1] : null;
                MtModel m = Get(pair, model);
                int maxTokens = Math.Min(160, Math.Max(16, (int)(cur.Length * 1.8f)));
                cur = m.TranslateNoCache(cur, maxTokens);
                route += (route.Length == 0 ? "" : " → ") + pair;
            }
            return cur;
        }

        public void Dispose()
        {
            lock (_lock)
            {
                foreach (var kv in _cache) { try { kv.Value.Dispose(); } catch { } }
                _cache.Clear();
            }
        }
    }
}
