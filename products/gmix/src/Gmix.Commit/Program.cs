using System;
using System.Text;

namespace Gmix.Commit
{
    /// <summary>
    /// 提交进程入口（ICD Part B §7 的唯一提交者）：
    /// 校验 staging → 生成 AVG/ → 写 group.json → 原子提交。
    /// 用法：Gmix.Commit --root &lt;数据根&gt; --groupId GR-... --runtime &lt;SDK 目录&gt; [--calibration file]
    /// </summary>
    static class Program
    {
        static int Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            string root = null, groupId = null, runtime = null, calibrationPath = null;
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--root": root = args[++i]; break;
                    case "--groupId": groupId = args[++i]; break;
                    case "--runtime": runtime = args[++i]; break;
                    case "--calibration": calibrationPath = args[++i]; break;
                    default: Console.Error.WriteLine("未知参数: " + args[i]); return 64;
                }
            }
            if (root == null || groupId == null)
            {
                Console.Error.WriteLine("用法: Gmix.Commit --root <数据根> --groupId GR-... --runtime <SDK 目录> [--calibration file]");
                return 64;
            }

            return GroupCommitPipeline.Run(root, groupId, runtime, calibrationPath,
                Console.WriteLine, Console.Error.WriteLine);
        }
    }
}
