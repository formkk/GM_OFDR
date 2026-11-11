using System;
using System.IO;

namespace Gmix.App.Orchestration
{
    /// <summary>组 ID 生成：GR-YYYYMMDD-HHMM-NNN，同分钟内序号递增（ICD §2.1）。</summary>
    public static class GroupIds
    {
        public static string Next(string dataRoot)
        {
            string prefix = "GR-" + DateTime.Now.ToString("yyyyMMdd-HHmm") + "-";
            int max = 0;
            max = MaxSequence(dataRoot, prefix, max);
            max = MaxSequence(Path.Combine(dataRoot, "_staging"), prefix, max);
            return prefix + (max + 1).ToString("D3");
        }

        static int MaxSequence(string root, string prefix, int max)
        {
            if (!Directory.Exists(root)) return max;
            foreach (string dir in Directory.GetDirectories(root))
            {
                string name = Path.GetFileName(dir);
                if (name.EndsWith(".failed", StringComparison.OrdinalIgnoreCase))
                    name = name.Substring(0, name.Length - ".failed".Length);
                if (!name.StartsWith(prefix, StringComparison.Ordinal)) continue;
                int seq;
                if (int.TryParse(name.Substring(prefix.Length), out seq) && seq > max) max = seq;
            }
            return max;
        }
    }
}
