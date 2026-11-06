using System;
using System.IO;

namespace Gmix.App
{
    /// <summary>运行路径解析：数据根、SDK 运行时目录、采集进程位置（SDD §11）。</summary>
    public sealed class AppEnvironment
    {
        public string DataRoot { get; private set; }
        public string RuntimeDir { get; private set; }
        public string AcquirerExe { get; private set; }
        public string CalibrationRoot { get { return Path.Combine(DataRoot, "_calibration"); } }

        public bool DeviceReady
        {
            get
            {
                return !string.IsNullOrEmpty(RuntimeDir)
                    && File.Exists(Path.Combine(RuntimeDir, "qt_acq.dll"))
                    && File.Exists(Path.Combine(RuntimeDir, "ofdr.dll"))
                    && File.Exists(Path.Combine(RuntimeDir, "detect.dll"));
            }
        }

        public static AppEnvironment Detect()
        {
            string baseDir = AppContext.BaseDirectory;
            string dataRoot = Environment.GetEnvironmentVariable("GMIX_DATA_ROOT");
            if (string.IsNullOrEmpty(dataRoot))
                dataRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "GMIX_DATA");

            var env = new AppEnvironment();
            env.DataRoot = Path.GetFullPath(dataRoot);
            env.RuntimeDir = ResolveRuntime(baseDir);
            env.AcquirerExe = ResolveAcquirer(baseDir);
            return env;
        }

        static string ResolveRuntime(string baseDir)
        {
            string fromEnv = Environment.GetEnvironmentVariable("GMIX_RUNTIME");
            if (!string.IsNullOrEmpty(fromEnv)) return Path.GetFullPath(fromEnv);
            if (File.Exists(Path.Combine(baseDir, "qt_acq.dll"))) return Path.GetFullPath(baseDir);
            string sub = Path.Combine(baseDir, "runtime");
            if (File.Exists(Path.Combine(sub, "qt_acq.dll"))) return Path.GetFullPath(sub);
            return FindUpwards(baseDir, Path.Combine("GM-IX", "runtime"), "qt_acq.dll");
        }

        static string ResolveAcquirer(string baseDir)
        {
            string fromEnv = Environment.GetEnvironmentVariable("GMIX_ACQUIRER");
            if (!string.IsNullOrEmpty(fromEnv)) return Path.GetFullPath(fromEnv);
            string local = Path.Combine(baseDir, "Gmix.Acquirer.exe");
            if (File.Exists(local)) return local;
            string debug = FindUpwards(baseDir,
                Path.Combine("src", "Gmix.Acquirer", "bin", "Debug", "net8.0"), "Gmix.Acquirer.exe");
            if (debug != null) return Path.Combine(debug, "Gmix.Acquirer.exe");
            string release = FindUpwards(baseDir,
                Path.Combine("src", "Gmix.Acquirer", "bin", "Release", "net8.0"), "Gmix.Acquirer.exe");
            return release == null ? null : Path.Combine(release, "Gmix.Acquirer.exe");
        }

        /// <summary>从当前目录逐级向上查找相对路径下的文件（开发期定位 SDK/采集进程，发行包不依赖）。</summary>
        static string FindUpwards(string startDir, string relativeDir, string fileName)
        {
            var dir = new DirectoryInfo(startDir);
            for (int depth = 0; depth < 8 && dir != null; depth++)
            {
                string candidate = Path.Combine(dir.FullName, relativeDir);
                if (File.Exists(Path.Combine(candidate, fileName))) return Path.GetFullPath(candidate);
                dir = dir.Parent;
            }
            return null;
        }
    }
}
