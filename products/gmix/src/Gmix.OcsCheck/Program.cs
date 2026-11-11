using System;

namespace Gmix.OcsCheck
{
    /// <summary>
    /// OCS 检测系统编排进程（骨架占位，业务逻辑未实现）。
    /// 计划职责：经 Gmix.Vendor/GkOcs 驱动控制 1×8 与 8×8 光开关（双串口）；
    /// 装调 / 自检 / 核验三模式编排；逐配置调用 Gmix.Acquirer.exe 采图（文件边界 _staging + .ready）。
    /// </summary>
    static class Program
    {
        const string AppVersion = "0.0.1";

        static int Main(string[] args)
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            Console.WriteLine("Gmix.OcsCheck " + AppVersion + "（骨架占位，业务逻辑未实现）");
            return 0;
        }
    }
}
