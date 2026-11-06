// OcrEngine.cs - 调 Python rapidocr 跑 V6 模型
// 编译: csc /nologo /target:exe /platform:x64 /optimize+ /r:System.dll /out:OcrEngine.exe OcrEngine.cs
using System;
using System.Diagnostics;
using System.IO;
using System.Text;

class OcrEngine
{
    static int Main(string[] args)
    {
        string dir = "";
        string detName = "", recName = "", keysName = "", imgPath = "";
        for (int i = 0; i < args.Length - 1; i++)
        {
            switch (args[i])
            {
                case "-d": dir = args[++i]; break;
                case "-1": detName = args[++i]; break;
                case "-3": recName = args[++i]; break;
                case "-4": keysName = args[++i]; break;
                case "-i": imgPath = args[++i]; break;
            }
        }
        if (string.IsNullOrEmpty(imgPath) || !File.Exists(imgPath))
        {
            Console.Error.WriteLine("missing image: " + imgPath);
            return 1;
        }
        string detPath = Path.Combine(dir, detName);
        string recPath = Path.Combine(dir, recName);
        string keysPath = Path.Combine(dir, keysName);

        string scriptPath = Path.Combine(Path.GetTempPath(), "xu_ocr_run.py");
        string script =
            "# -*- coding: utf-8 -*-\n" +
            "import sys\n" +
            "sys.stdout.reconfigure(encoding='utf-8')\n" +
            "from rapidocr import RapidOCR\n" +
            "engine = RapidOCR(params={\n" +
            "    'Det.model_path': r'" + detPath.Replace("\\", "\\\\") + "',\n" +
            "    'Rec.model_path': r'" + recPath.Replace("\\", "\\\\") + "',\n" +
            "    'Rec.rec_keys_path': r'" + keysPath.Replace("\\", "\\\\") + "',\n" +
            "    'Global.use_cls': False,\n" +
            "})\n" +
            "result = engine(r'" + imgPath.Replace("\\", "\\\\") + "')\n" +
            "if result.txts:\n" +
            "    for t in result.txts:\n" +
            "        print(t)\n";
        File.WriteAllText(scriptPath, script, Encoding.UTF8);

        var psi = new ProcessStartInfo();
        psi.FileName = "python";
        psi.Arguments = "-X utf8 \"" + scriptPath + "\"";
        psi.UseShellExecute = false;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.CreateNoWindow = true;
        psi.StandardOutputEncoding = Encoding.UTF8;
        psi.StandardErrorEncoding = Encoding.UTF8;
        var p = Process.Start(psi);
        string stdout = p.StandardOutput.ReadToEnd();
        string stderr = p.StandardError.ReadToEnd();
        if (!p.WaitForExit(60000))
        {
            try { p.Kill(); } catch {}
            Console.Error.WriteLine("timeout");
            return 2;
        }
        Console.Write(stdout);
        if (p.ExitCode != 0) Console.Error.Write(stderr);
        return p.ExitCode;
    }
}
