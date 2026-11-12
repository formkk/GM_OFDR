using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Detection.Core
{
    /// <summary>
    /// 检测侧配置快照（SDD §8.1）。不可变语义：运行中锁定，变更需在批次前完成并重算 `configHash`。
    /// 纳入哈希的字段集合见 SRS §6.3。
    /// </summary>
    public sealed class DetectionConfig
    {
        public OfdrScanConfig ofdr { get; set; }

        public OcsPortConfig ocs1x8 { get; set; }

        public OcsPortConfig ocs8x8 { get; set; }

        public PathPlan plan { get; set; }

        /// <summary>光路稳定时间；TBD-O03 未实测，必须由现场填写，禁止给默认值。</summary>
        public int settleMs { get; set; }

        public string dataRoot { get; set; }

        public string baselineId { get; set; }

        public int? baselineVersion { get; set; }

        public RetentionConfig retention { get; set; }

        /// <summary>
        /// 计算 `configHash`（SRS §6.3）。字段顺序固定、数值用不变文化格式化，
        /// 保证同一配置在不同机器上得到同一哈希。
        /// </summary>
        public string ComputeConfigHash()
        {
            var sb = new StringBuilder();
            sb.Append("ofdr.endpoint=").Append(ofdr == null ? "" : ofdr.endpoint).Append(';');
            sb.Append("ofdr.range=").Append(ofdr == null ? "" : ofdr.range).Append(';');
            sb.Append("ofdr.resolution=").Append(ofdr == null ? "" : ofdr.resolution).Append(';');
            sb.Append("ofdr.average=").Append(Format(ofdr == null ? null : ofdr.average)).Append(';');
            sb.Append("ofdr.timeoutMs=").Append(Format(ofdr == null ? null : ofdr.timeoutMs)).Append(';');
            sb.Append("ocs1x8=").Append(DescribePort(ocs1x8)).Append(';');
            sb.Append("ocs8x8=").Append(DescribePort(ocs8x8)).Append(';');
            sb.Append("plan.version=").Append(plan == null ? "" : plan.planVersion).Append(';');
            AppendPlan(sb, plan);
            sb.Append("settleMs=").Append(Format(settleMs)).Append(';');
            sb.Append("dataRoot=").Append(dataRoot).Append(';');
            sb.Append("baseline=").Append(baselineId).Append('@').Append(Format(baselineVersion)).Append(';');
            sb.Append("retention=").Append(retention == null ? "" : retention.Describe()).Append(';');
            return "sha256:" + Sha256Hex(sb.ToString());
        }

        static void AppendPlan(StringBuilder sb, PathPlan plan)
        {
            sb.Append("plan.entries=");
            if (plan == null || plan.entries == null)
            {
                sb.Append(';');
                return;
            }

            foreach (PathPlanEntry entry in plan.entries)
            {
                if (entry == null) continue;
                sb.Append(entry.pathId).Append(':')
                  .Append(entry.inChannel).Append(':')
                  .Append(entry.outChannel).Append(':')
                  .Append(entry.route1x8).Append(':')
                  .Append(entry.averageCount).Append(',');
            }
            sb.Append(';');
        }

        static string DescribePort(OcsPortConfig port)
        {
            if (port == null) return "";
            var sb = new StringBuilder();
            sb.Append(port.port).Append('/')
              .Append(Format(port.baud)).Append('/')
              .Append(port.handshake).Append('/')
              .Append(Format(port.timeoutMs)).Append('/')
              .Append(Format(port.matrixTimeoutMs)).Append('/')
              .Append(Format(port.retry)).Append('/');
            if (port.backoffMs != null)
                foreach (int ms in port.backoffMs) sb.Append(Format(ms)).Append(',');
            return sb.ToString();
        }

        static string Format(int? value)
        {
            return value.HasValue ? value.Value.ToString(CultureInfo.InvariantCulture) : "";
        }

        static string Sha256Hex(string text)
        {
            using (var sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(text ?? string.Empty));
                var sb = new StringBuilder(hash.Length * 2);
                foreach (byte b in hash) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                return sb.ToString();
            }
        }
    }

    /// <summary>OFDR 扫描参数（量程与分辨率的口径待 TBD-D10，按字符串透传，不做单位换算）。</summary>
    public sealed class OfdrScanConfig
    {
        public string endpoint { get; set; }
        public string range { get; set; }
        public string resolution { get; set; }
        public int? average { get; set; }
        public int timeoutMs { get; set; }
    }

    /// <summary>OCS 串口参数与重试（默认值来自实测报告 §4，可按现场调整）。</summary>
    public sealed class OcsPortConfig
    {
        public string port { get; set; }
        public int baud { get; set; }
        public string handshake { get; set; }
        public int timeoutMs { get; set; }
        public int matrixTimeoutMs { get; set; }
        public int retry { get; set; }
        public int[] backoffMs { get; set; }
    }

    /// <summary>数据保留策略；默认值待 TBD-D12。</summary>
    public sealed class RetentionConfig
    {
        public string mode { get; set; }
        public int? days { get; set; }
        public int? batches { get; set; }
        public long? diskWarnBytes { get; set; }
        public long? diskHardBytes { get; set; }
        public long? logMaxBytes { get; set; }

        public string Describe()
        {
            return string.Format(CultureInfo.InvariantCulture, "{0}/{1}/{2}/{3}/{4}/{5}",
                mode, days, batches, diskWarnBytes, diskHardBytes, logMaxBytes);
        }
    }
}
