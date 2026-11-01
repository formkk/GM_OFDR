using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace OFDR.Contracts;

/// <summary>
/// StandardScan 的序列化与校验和。schema 版本随扫描记录携带，便于日后演进与追溯。
/// 校验和只覆盖轴 + 数值，不覆盖时间戳等易变元数据。
/// </summary>
public static class StandardScanCodec
{
    public const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
    };

    public static string ComputeChecksum(StandardScan scan)
    {
        var sb = new StringBuilder(scan.Values.Length * 18);
        sb.Append(scan.Axis.Kind).Append('|')
          .Append(scan.Axis.Start.ToString("R", System.Globalization.CultureInfo.InvariantCulture)).Append('|')
          .Append(scan.Axis.Step.ToString("R", System.Globalization.CultureInfo.InvariantCulture)).Append('|')
          .Append(scan.Axis.Count).Append('|')
          .Append(scan.ValueUnit).Append('|');
        foreach (var v in scan.Values)
            sb.Append(v.ToString("R", System.Globalization.CultureInfo.InvariantCulture)).Append(',');
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes(sb.ToString()));
        return Convert.ToHexString(hash);
    }

    public static string ToJson(StandardScan scan, bool indented = false)
    {
        var options = indented ? new JsonSerializerOptions { WriteIndented = true } : Options;
        return JsonSerializer.Serialize(scan, options);
    }

    public static StandardScan FromJson(string json)
    {
        var scan = JsonSerializer.Deserialize<StandardScan>(json, Options)
                   ?? throw new InvalidOperationException("JSON 中不含 StandardScan");
        return scan;
    }
}
