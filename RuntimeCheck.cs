using System;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;
using Microsoft.Win32;

namespace QRSnap
{
    // 启动前检查系统运行环境：缺 WebView2 直接提示并退出，缺 VC++/.NET 给警告但尽量继续
    internal static class RuntimeCheck
    {
        public static bool Verify()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string sys = Environment.GetFolderPath(Environment.SpecialFolder.System);

            bool vc = HasVcRuntime(baseDir) || HasVcRuntime(sys);
            if (!vc)
            {
                MessageBox.Show(
                    "缺少 VC++ 2015–2022 运行库（vcruntime140.dll / msvcp140.dll）。\n\n" +
                    "请安装 Microsoft Visual C++ 2015–2022 x64 Redistributable，\n" +
                    "或把运行库 DLL 放回程序目录。\n\n" +
                    "缺少它时翻译/OCR 的 ONNX 推理可能无法启动。",
                    "XuYuan-SnapScan · 缺少运行库", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }

            bool net48 = NetRelease() >= 528040;
            if (!net48)
            {
                MessageBox.Show(
                    "建议安装 .NET Framework 4.8。\n\n" +
                    "当前 .NET Framework 版本偏低，界面或模型加载可能异常。\n" +
                    "Windows 10 21H2 / Windows 11 一般已自带 4.8。",
                    "XuYuan-SnapScan · 建议更新 .NET", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }

            if (!HasWebView2())
            {
                string setup = Path.Combine(baseDir, "MicrosoftEdgeWebview2Setup.exe");
                string hint = File.Exists(setup)
                    ? "请先运行程序目录里的 MicrosoftEdgeWebview2Setup.exe 安装 WebView2 Runtime。"
                    : "请安装 Microsoft Edge WebView2 Runtime（Evergreen），再启动本程序。";
                MessageBox.Show(
                    "缺少 WebView2 Runtime，界面无法启动。\n\n" + hint,
                    "XuYuan-SnapScan · 缺少 WebView2", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }

            return true;
        }

        private static bool HasVcRuntime(string dir)
        {
            if (string.IsNullOrEmpty(dir)) return false;
            return File.Exists(Path.Combine(dir, "vcruntime140.dll"))
                && File.Exists(Path.Combine(dir, "msvcp140.dll"));
        }

        private static int NetRelease()
        {
            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full"))
                {
                    if (k == null) return 0;
                    object v = k.GetValue("Release");
                    return v == null ? 0 : Convert.ToInt32(v);
                }
            }
            catch (Exception) { return 0; }
        }

        private static bool HasWebView2()
        {
            try
            {
                string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
                if (HasVersionDir(Path.Combine(pf86, "Microsoft", "EdgeWebView", "Application"))) return true;
                string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                if (HasVersionDir(Path.Combine(pf, "Microsoft", "EdgeWebView", "Application"))) return true;
                string local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Microsoft", "EdgeWebView", "Application");
                if (HasVersionDir(local)) return true;
            }
            catch (Exception) { }
            return false;
        }

        private static bool HasVersionDir(string dir)
        {
            try
            {
                if (!Directory.Exists(dir)) return false;
                foreach (string d in Directory.GetDirectories(dir))
                {
                    string exe = Path.Combine(d, "msedgewebview2.exe");
                    if (File.Exists(exe)) return true;
                }
            }
            catch (Exception) { }
            return false;
        }
    }
}
