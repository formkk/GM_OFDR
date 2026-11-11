using System;
using System.IO;

namespace Gmix.Vendor
{
    /// <summary>
    /// 厂商运行时初始化：设置 DLL 搜索目录并切换工作目录（SDK 以相对路径读取 config/）。
    /// 任何要调用 ofdr/detect 的进程都必须先调用一次，即使不打开采集设备。
    /// </summary>
    public static class VendorRuntime
    {
        public static void Initialize(string runtimeDir)
        {
            if (string.IsNullOrEmpty(runtimeDir))
                throw new ArgumentException("runtimeDir 不能为空", nameof(runtimeDir));
            string dir = Path.GetFullPath(runtimeDir);
            if (!Directory.Exists(dir))
                throw new DirectoryNotFoundException("厂商目录不存在: " + dir);
            NativeApi.SetDllDirectory(dir);
            Environment.CurrentDirectory = dir;
        }
    }
}
