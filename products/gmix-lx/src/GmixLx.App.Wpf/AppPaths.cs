using System;
using System.IO;

namespace GmixLx.App.Wpf
{
    /// <summary>
    /// 仓库内相对路径解析：从 exe 目录逐级向上找，命中第一个存在项；找不到返回 null，
    /// 由调用方按 WinForms 版文案报错，不做静默兜底。
    /// </summary>
    internal static class AppPaths
    {
        /// <summary>厂商 SDK 目录（相对仓库根）。</summary>
        public const string SdkRelativePath = "references/YinXun_2/customer";

        /// <summary>参考标定文件（相对仓库根）。</summary>
        public const string CalibrationRelativePath = "products/gmix-lx/config/yinxun2_reference_calibration.json";

        public static string SdkDirectory
        {
            get { return Resolve(SdkRelativePath); }
        }

        public static string CalibrationPath
        {
            get { return Resolve(CalibrationRelativePath); }
        }

        public static string Resolve(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            path = path.Trim();

            if (Path.IsPathRooted(path))
            {
                return File.Exists(path) || Directory.Exists(path) ? path : null;
            }

            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory != null)
            {
                string candidate = Path.Combine(directory.FullName, path);
                if (File.Exists(candidate) || Directory.Exists(candidate)) return candidate;
                directory = directory.Parent;
            }
            return null;
        }
    }
}
