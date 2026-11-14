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

    // .NET 的 HttpWebRequest 默认每个 host 只给 2 条并发连接；不放开的话
    // 4 路并发会被悄悄降成 2 并发 + 2 排队，看起来改了却没效果。
    static LlamaMt()
    {
        try { ServicePointManager.DefaultConnectionLimit = 16; } catch (Exception) { }
    }

    // 译文缓存：网页导航栏、按钮文案、滚动一屏后的旧行大量重复。
    // 命中就不必起服务、不必发请求，是最省的一层。
    private static readonly Dictionary<string, string> CacheMap = new Dictionary<string, string>(StringComparer.Ordinal);
    private static readonly List<string> CacheOrder = new List<string>();
    private static readonly object CacheGate = new object();
    private const int CacheMax = 600;

    public static int CacheCount { get { lock (CacheGate) { return CacheMap.Count; } } }

    private static string CacheKey(string model, string text, string tgt)
    {
        return Path.GetFileName(model == null ? "" : model) + "|" + (tgt == null ? "" : tgt) + "|" + text;
    }

    public static bool CacheTry(string model, string text, string tgt, out string tr)
    {
        tr = null;
        if (string.IsNullOrEmpty(text)) return false;
        string k = CacheKey(model, text, tgt);
        lock (CacheGate)
        {
            if (!CacheMap.TryGetValue(k, out tr)) { tr = null; return false; }
            return true;
        }
    }

    private static void CachePut(string model, string text, string tgt, string tr)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(tr)) return;
        string k = CacheKey(model, text, tgt);
        lock (CacheGate)
        {
            if (CacheMap.ContainsKey(k)) return;
            CacheMap[k] = tr;
            CacheOrder.Add(k);
            while (CacheOrder.Count > CacheMax)
            {
                string old = CacheOrder[0];
                CacheOrder.RemoveAt(0);
                CacheMap.Remove(old);
            }
        }
    }
    private static Process _proc;
    private static string _model;      // 当前服务加载的模型文件
    private static int _port;
    private static string _exe;
    private static DateTime _lastUse = DateTime.MinValue;
    private static Timer _idle;
    private static bool _exitHook;

    // ===== 子进程托管 =====
    // 只靠 ProcessExit 钩子不够：任务管理器/强杀（TerminateProcess）不会触发它，
    // llama-server 会一直挂着，越积越多，每个 -ngl 99 还各占一份显存。
    // 所以把子进程塞进一个「句柄关闭就全杀」的 Job 对象：本进程无论怎么死，内核都会带走它。
    private static IntPtr _job;

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct JobBasicLimit
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct JobIoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct JobExtendedLimit
    {
        public JobBasicLimit BasicLimitInformation;
        public JobIoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern IntPtr CreateJobObject(IntPtr attrs, string name);
    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref JobExtendedLimit info, uint len);
    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;

    private static void EnsureJob()
    {
        if (_job != IntPtr.Zero) return;
        try
        {
            IntPtr job = CreateJobObject(IntPtr.Zero, null);
            if (job == IntPtr.Zero) return;
            JobExtendedLimit info = new JobExtendedLimit();
            info.BasicLimitInformation.LimitFlags = JobObjectLimitKillOnJobClose;
            SetInformationJobObject(job, JobObjectExtendedLimitInformation, ref info,
                (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(JobExtendedLimit)));
            _job = job;
        }
        catch (Exception) { }
    }

    private static void AttachToJob(Process p)
    {
        try
        {
            EnsureJob();
            if (_job == IntPtr.Zero || p == null) return;
            AssignProcessToJobObject(_job, p.Handle);
        }
        catch (Exception) { }
    }

    // 兜底：起新服务前，先把「父进程已经不在了」的 llama-server 收掉。
    // 老版本强杀留下的孤儿就是靠这一步不再堆起来；父进程还活着的一律不动（多开时不误杀）。
    private static void ReapOrphans(string dir)
    {
        try
        {
            Dictionary<int, int> pm = ParentMap();
            string self = Path.Combine(dir, "llama-server.exe");
            foreach (Process sp in Process.GetProcessesByName("llama-server"))
            {
                try
                {
                    int pid = sp.Id;
                    if (_proc != null && pid == _proc.Id) continue;
                    // 只收本工程目录下的，别人的 llama-server 不碰
                    string path = null;
                    try { path = sp.MainModule.FileName; } catch (Exception) { }
                    if (path == null || string.Compare(path, self, StringComparison.OrdinalIgnoreCase) != 0) continue;
                    // 父进程还活着就跳过
                    int ppid = pm.ContainsKey(pid) ? pm[pid] : 0;
                    if (ppid > 0 && pm.ContainsKey(ppid)) continue;
                    sp.Kill();
                }
                catch (Exception) { }
                finally { try { sp.Dispose(); } catch (Exception) { } }
            }
        }
        catch (Exception) { }
    }

    // 进程表快照：pid -> ppid，只取一次，避免逐个查 WMI
    private static Dictionary<int, int> ParentMap()
    {
        Dictionary<int, int> m = new Dictionary<int, int>();
        IntPtr snap = CreateToolhelp32Snapshot(0x00000002, 0);
        if (snap == IntPtr.Zero || snap == (IntPtr)(-1)) return m;
        try
        {
            ProcEntry pe = new ProcEntry();
            pe.Size = (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(ProcEntry));
            if (Process32FirstW(snap, ref pe))
            {
                do { m[(int)pe.Pid] = (int)pe.PParentPid; } while (Process32NextW(snap, ref pe));
            }
        }
        catch (Exception) { }
        finally { CloseHandle(snap); }
        return m;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private struct ProcEntry
    {
        public uint Size;
        public uint CntUsage;
        public uint Pid;
        public IntPtr DefaultHeapId;
        public uint ModuleId;
        public uint CntThreads;
        public uint PParentPid;
        public int PriClassBase;
        public uint Flags;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 260)]
        public string ExeFile;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern bool Process32FirstW(IntPtr snap, ref ProcEntry pe);
    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern bool Process32NextW(IntPtr snap, ref ProcEntry pe);
    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr h);



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
                    AttachToJob(p);
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

    // 当前实际加载的档位（standard / efficiency / turbo），空 = 没有模型驻留
    public static string LoadedProfile = string.Empty;
    // 当前服务实际加载的模型文件路径（未运行或已释放时为 null）
    public static string LoadedModelPath
    {
        get { lock (Gate) { return ServerRunning ? _model : null; } }
    }
    // 正在后台加载的档位，空 = 空闲
    public static string LoadingProfile = string.Empty;
    // 上一次加载失败的原因
    public static string LastLoadError = string.Empty;

    // 显式加载：同一时间只保留一个模型，StartServer 内部会先 Kill 掉旧的
    public static void Preload(string modelPath, string profile)
    {
        lock (Gate)
        {
            LastLoadError = string.Empty;
            LoadingProfile = profile;
            try
            {
                if (!RuntimeReady) throw new Exception("缺少 llama.cpp 运行时（llama-server.exe），翻译用不了");
                if (string.IsNullOrEmpty(modelPath) || !File.Exists(modelPath))
                    throw new Exception("模型文件不存在");
                StartServer(modelPath);
                _lastUse = DateTime.UtcNow;
                LoadedProfile = profile;
            }
            catch (Exception ex)
            {
                LoadedProfile = string.Empty;
                LastLoadError = ex.Message;
                throw;
            }
            finally
            {
                LoadingProfile = string.Empty;
            }
        }
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
        // 并发槽：GPU 上开 4 槽，能把"等解码"的空隙填满（整屏几十行收益明显）；
        // CPU 上并发只会把线程挤爆，保持单槽。
        int slots = gpu ? 4 : 1;
        // -c 是所有槽共享的总上下文，每槽分到 -c/slots；8192/4=2048，
        // 和原来单槽一致，长行也不会被截。
        int ctx = gpu ? 8192 : 2048;
        string parArg = slots > 1 ? " --parallel " + slots : "";
        ProcessStartInfo si = new ProcessStartInfo();
        si.FileName = Path.Combine(dir, "llama-server.exe");
        si.Arguments = "--model \"" + model + "\" --host 127.0.0.1 --port " + port
            + " --jinja -c " + ctx + " -t " + threads + (gpu ? " -ngl 99" : " -ngl 0") + parArg + perfArgs + plamoArg + " --no-warmup";
        si.WorkingDirectory = dir;
        si.UseShellExecute = false;
        si.CreateNoWindow = true;
        si.RedirectStandardOutput = true;
        si.RedirectStandardError = true;
        // 起新服务前先收掉老的孤儿（历史上被强杀留下的）
        ReapOrphans(dir);
        Process p = new Process();
        p.StartInfo = si;
        p.OutputDataReceived += delegate(object s, DataReceivedEventArgs e) { };
        p.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e) { };
        p.Start();
        AttachToJob(p);
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
                try { JadeHost.Log("[mt] idle kill idle=" + idle + " dt=" + ((int)(DateTime.UtcNow - _lastUse).TotalSeconds)); } catch (Exception) { }
                Kill();
            }
            // 锁外面推状态：空闲自动释放后，档位后面的「释放」得变回「加载」
            try { JadeHost.NotifyState(); } catch (Exception) { }
        }
        catch (Exception) { }
    }

    public static void Kill()
    {
        try
        {
            System.Diagnostics.StackTrace st = new System.Diagnostics.StackTrace();
            string who = st.FrameCount > 1 ? st.GetFrame(1).GetMethod().DeclaringType.Name + "." + st.GetFrame(1).GetMethod().Name : "?";
            JadeHost.Log("[mt] kill <- " + who);
        }
        catch (Exception) { }
        Process p = _proc;
        _proc = null;
        _model = null;
        _port = 0;
        LoadedProfile = string.Empty;
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
        string hit;
        if (CacheTry(model, text, tgt, out hit)) return hit;
        if (!RuntimeReady) throw new Exception("缺少 llama.cpp 运行时（llama-server.exe），翻译用不了");
        lock (Gate)
        {
            if (_proc == null || !string.Equals(_model, model, StringComparison.OrdinalIgnoreCase) || (_proc != null && _proc.HasExited))
                StartServer(model);
            _lastUse = DateTime.UtcNow;

            string tr = TranslateOnce(model, text, tgt, "http://127.0.0.1:" + _port + "/v1/chat/completions");
            _lastUse = DateTime.UtcNow;
            return tr == null ? "" : tr;
        }
    }

    // 单次请求：调用方保证服务已在跑、url/port 已取好。这里不加锁，供并发调用。
    private static string TranslateOnce(string model, string text, string tgt, string url)
    {
        if (string.IsNullOrEmpty(text)) return null;
        string payload = IsPlamo(model) ? BuildPlamoRequest(text, 192) : BuildRequest(Prompt(text, tgt), 192);
        string resp = HttpPost(url, payload, 120);
        string content = Clean(JsonField(resp, "content"));
        if (string.IsNullOrEmpty(content)) return null;
        CachePut(model, text, tgt, content);
        return content;
    }

    // 多行翻译：先查缓存，剩下的按 maxParallel 路并发发请求。
    // 串行版每行要付一次模板+前缀的固定开销（实测约 40ms），整屏上百行就是 4 秒；
    // 4 路并发能把总时间压到六成左右。结果按原索引回填，单行失败留 null。
    public static List<string> TranslateMany(string model, List<string> lines, string tgt, int maxParallel)
    {
        int n = lines == null ? 0 : lines.Count;
        string[] arr = new string[n];
        if (n == 0) return new List<string>();
        if (!RuntimeReady) throw new Exception("缺少 llama.cpp 运行时（llama-server.exe），翻译用不了");

        List<int> todo = new List<int>();
        for (int i = 0; i < n; i++)
        {
            string hit;
            if (!string.IsNullOrEmpty(lines[i]) && CacheTry(model, lines[i], tgt, out hit)) arr[i] = hit;
            else if (!string.IsNullOrEmpty(lines[i])) todo.Add(i);
        }
        if (todo.Count == 0) return new List<string>(arr);

        // 拉起服务这一步必须串行，之后才放并发
        string url;
        lock (Gate)
        {
            if (_proc == null || !string.Equals(_model, model, StringComparison.OrdinalIgnoreCase) || _proc.HasExited)
                StartServer(model);
            _lastUse = DateTime.UtcNow;
            url = "http://127.0.0.1:" + _port + "/v1/chat/completions";
        }

        int workers = Math.Max(1, Math.Min(maxParallel, todo.Count));
        if (workers == 1)
        {
            for (int i = 0; i < todo.Count; i++)
            {
                string tr = null;
                try { tr = TranslateOnce(model, lines[todo[i]], tgt, url); } catch (Exception) { tr = null; }
                if (tr != null) arr[todo[i]] = tr;
            }
        }
        else
        {
            Exception firstErr = null;
            object errLock = new object();
            Thread[] th = new Thread[workers];
            for (int w = 0; w < workers; w++)
            {
                int step = workers;
                int begin = w;
                Thread t = new Thread(delegate()
                {
                    try
                    {
                        for (int i = begin; i < todo.Count; i += step)
                        {
                            string tr = null;
                            try { tr = TranslateOnce(model, lines[todo[i]], tgt, url); }
                            catch (Exception) { tr = null; }
                            if (tr != null) arr[todo[i]] = tr;
                        }
                    }
                    catch (Exception ex)
                    {
                        lock (errLock) { if (firstErr == null) firstErr = ex; }
                    }
                });
                t.IsBackground = true;
                t.Start();
                th[w] = t;
            }
            for (int w = 0; w < th.Length; w++) { try { th[w].Join(); } catch (Exception) { } }
            lock (Gate) { _lastUse = DateTime.UtcNow; }
            if (firstErr != null && firstErr is Exception)
            {
                // 全部失败才往上抛，让调用方自己决定回退
                bool any = false;
                for (int i = 0; i < n; i++) if (arr[i] != null) { any = true; break; }
                if (!any) throw firstErr;
            }
        }
        return new List<string>(arr);
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

        // PLaMo 官方要求一条原文一个请求。多行 prompt 实测更慢（2748ms vs 368ms）
        // 还会漏行、把提示词一起翻出来，所以这里直接走逐行，但可以并发。
        if (IsPlamo(model))
            return TranslateMany(model, lines, tgt, lines.Count >= 8 ? 4 : 2);

        lock (Gate)
        {
            if (_proc == null || !string.Equals(_model, model, StringComparison.OrdinalIgnoreCase) || (_proc != null && _proc.HasExited))
                StartServer(model);
            _lastUse = DateTime.UtcNow;

            // 先吃掉缓存里已有的行（滚动一屏后大部分能命中），剩下的才发给模型
            string[] done = new string[lines.Count];
            List<int> todo = new List<int>();
            for (int i = 0; i < lines.Count; i++)
            {
                string hit;
                if (!string.IsNullOrEmpty(lines[i]) && CacheTry(model, lines[i], tgt, out hit)) done[i] = hit;
                else if (!string.IsNullOrEmpty(lines[i])) todo.Add(i);
            }
            if (todo.Count == 0) return new List<string>(done);

            StringBuilder prompt = new StringBuilder();
            int chars = 0;
            if (tgt == "zh")
                prompt.Append("只输出译文（简体中文），每行对应一行：\n");
            else
                prompt.Append("Only output translations (").Append(langEn(tgt)).Append("), one per line:\n");
            for (int i = 0; i < todo.Count; i++)
            {
                prompt.Append(lines[todo[i]]).Append('\n');
                chars += lines[todo[i]].Length;
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
            if (got.Count != todo.Count) return null;
            for (int i = 0; i < todo.Count; i++)
            {
                done[todo[i]] = got[i];
                CachePut(model, lines[todo[i]], tgt, got[i]);
            }
            return new List<string>(done);
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
