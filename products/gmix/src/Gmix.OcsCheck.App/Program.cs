using System;

namespace Gmix.OcsCheck.App
{
    /// <summary>
    /// OCS 检测系统上位机 UI（骨架占位，业务逻辑未实现）。
    /// 计划职责：装调 / 自检 / 核验三模式界面、基线管理、判定报告展示。
    /// 约束：不引用 Gmix.Vendor；与编排进程 Gmix.OcsCheck 通过文件边界交互（任务/状态文件）。
    /// </summary>
    static class Program
    {
        const string AppVersion = "0.0.1";

        [STAThread]
        static int Main(string[] args)
        {
            return 0;
        }
    }
}
