using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using GmixLx.Core;

namespace GmixLx.Orchestration
{
    /// <summary>
    /// `detection.json` 与 `index.csv` 的读写（SDD §5.3）。写入一律"临时文件 + 原子改名"，
    /// 保证异常退出时不会留下半写文件。
    /// </summary>
    public sealed class DetectionStore
    {
        static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            WriteIndented = true
        };

        public string DataRoot { get; }

        public DetectionStore(string dataRoot)
        {
            if (string.IsNullOrEmpty(dataRoot))
                throw new ArgumentException("dataRoot 不能为空", nameof(dataRoot));
            DataRoot = Path.GetFullPath(dataRoot);
        }

        public string BatchDirectory(string batchId)
        {
            return Path.Combine(DataRoot, batchId);
        }

        public string DetectionJsonPath(string batchId)
        {
            return Path.Combine(BatchDirectory(batchId), "detection.json");
        }

        /// <summary>原子写入整个批次文件（先在内存组装，再一次落盘）。</summary>
        public void Write(DetectionBatch batch)
        {
            if (batch == null) throw new ArgumentNullException(nameof(batch));
            if (string.IsNullOrEmpty(batch.batchId))
                throw new ArgumentException("batchId 为空", nameof(batch));

            string path = DetectionJsonPath(batch.batchId);
            WriteAtomic(path, JsonSerializer.Serialize(batch, Options));
        }

        public DetectionBatch Read(string batchId)
        {
            string path = DetectionJsonPath(batchId);
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<DetectionBatch>(File.ReadAllText(path, Encoding.UTF8), Options);
        }

        /// <summary>批次到达终态时生成一次；列序见 SRS §6.6。空值写空字符串。</summary>
        public void WriteIndexCsv(DetectionBatch batch)
        {
            if (batch == null) throw new ArgumentNullException(nameof(batch));
            var sb = new StringBuilder();
            sb.AppendLine("pathId,groupId,avgScanId,scanIds,state");

            if (batch.paths != null)
            {
                foreach (PathResult path in batch.paths)
                {
                    if (path == null) continue;
                    sb.Append(Csv(path.pathId)).Append(',')
                      .Append(Csv(path.groupId)).Append(',')
                      .Append(Csv(path.avgScanId)).Append(',')
                      .Append(Csv(Join(path.scanIds))).Append(',')
                      .Append(Csv(path.pathState)).AppendLine();
                }
            }

            WriteAtomic(Path.Combine(BatchDirectory(batch.batchId), "index.csv"), sb.ToString());
        }

        /// <summary>列出数据根下的批次目录名，按名称（含时间戳）升序。</summary>
        public List<string> ListBatchIds()
        {
            var ids = new List<string>();
            if (!Directory.Exists(DataRoot)) return ids;

            foreach (string dir in Directory.GetDirectories(DataRoot))
            {
                string name = Path.GetFileName(dir);
                if (DetectionNaming.BatchIdOfDirectory(name) == null) continue;
                if (!File.Exists(Path.Combine(dir, "detection.json"))) continue;
                ids.Add(name);
            }

            ids.Sort(StringComparer.Ordinal);
            return ids;
        }

        static string Join(string[] values)
        {
            if (values == null || values.Length == 0) return string.Empty;
            return string.Join(";", values);
        }

        static string Csv(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            if (value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0) return value;
            return '"' + value.Replace("\"", "\"\"") + '"';
        }

        static void WriteAtomic(string path, string content)
        {
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            string temp = path + ".tmp";
            File.WriteAllText(temp, content, new UTF8Encoding(false));

            if (File.Exists(path))
            {
                string backup = path + ".bak";
                if (File.Exists(backup)) File.Delete(backup);
                File.Replace(temp, path, backup, true);
                if (File.Exists(backup)) File.Delete(backup);
            }
            else
            {
                File.Move(temp, path);
            }
        }

        /// <summary>UTC ISO 8601（秒级），用于批次时间戳。</summary>
        public static string UtcNowIso()
        {
            return DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        }
    }
}
