using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Gmix.Data
{
    /// <summary>
    /// 配置快照与 configHash（ICD Part A §6.1，冻结口径）：
    /// 参与域固定 6 段（interface / channelMap / acquisition 8 项 / afe / range / functional），
    /// 对象键按 Ordinal 升序、无空白、段间用 U+001E 连接、SHA256 + "sha256:" 小写 hex。
    /// 采集进程与编排进程必须共用本实现，禁止各写一份。
    /// </summary>
    public sealed class ConfigSnapshot
    {
        public const string AlgorithmVersion = "canonical-json/sha256/v1";

        public JsonNode Interface { get; private set; }
        public JsonNode Afe { get; private set; }
        public JsonNode Range { get; private set; }
        public JsonNode Functional { get; private set; }
        public string Hash { get; private set; }

        public static ConfigSnapshot Load(string configDir, uint cardIndex, AcquisitionInfo acquisition)
        {
            if (string.IsNullOrEmpty(configDir))
                throw new ArgumentException("configDir 不能为空", nameof(configDir));
            if (acquisition == null)
                throw new ArgumentNullException(nameof(acquisition));

            string cardDir = Path.Combine(configDir, cardIndex.ToString());
            string cardInterface = Path.Combine(cardDir, "Interface.json");
            string rootInterface = Path.Combine(configDir, "Interface.json");

            JsonNode iface = ReadNode(File.Exists(cardInterface) ? cardInterface : rootInterface);
            JsonNode afe = ReadNode(Path.Combine(cardDir, "AFE.json"));
            JsonNode range = ReadNode(Path.Combine(cardDir, "Range.json"));
            JsonNode functional = ReadNode(Path.Combine(cardDir, "FunctionalConfiguration.json"));

            var snapshot = new ConfigSnapshot
            {
                Interface = iface,
                Afe = afe,
                Range = range,
                Functional = functional
            };
            snapshot.Hash = ComputeHash(iface, BuildChannelMap(acquisition), BuildAcquisition(acquisition),
                afe, range, functional);
            return snapshot;
        }

        /// <summary>直接由六段计算哈希（供测试与一致性校验使用）。</summary>
        public static string ComputeHash(JsonNode iface, JsonNode channelMap, JsonNode acquisition,
            JsonNode afe, JsonNode range, JsonNode functional)
        {
            var sb = new StringBuilder();
            sb.Append("interface=").Append(Canon(iface)).Append('\u001E');
            sb.Append("channelMap=").Append(Canon(channelMap)).Append('\u001E');
            sb.Append("acquisition=").Append(Canon(acquisition)).Append('\u001E');
            sb.Append("afe=").Append(Canon(afe)).Append('\u001E');
            sb.Append("range=").Append(Canon(range)).Append('\u001E');
            sb.Append("functional=").Append(Canon(functional));
            return HashUtil.Sha256Text(sb.ToString());
        }

        public static JsonObject BuildChannelMap(AcquisitionInfo acquisition)
        {
            var map = new JsonObject();
            if (acquisition?.channelMap == null) return map;
            map["p"] = acquisition.channelMap.p;
            map["s"] = acquisition.channelMap.s;
            map["aux"] = acquisition.channelMap.aux;
            var ignored = new JsonArray();
            if (acquisition.channelMap.ignored != null)
            {
                foreach (int ch in acquisition.channelMap.ignored)
                    ignored.Add(JsonValue.Create(ch));
            }
            map["ignored"] = ignored;
            return map;
        }

        /// <summary>只取 8 项，排除 expectedFrames/bufferBytes/bytesPerFrame 等派生值（ICD §6.1 排除域）。</summary>
        public static JsonObject BuildAcquisition(AcquisitionInfo acquisition)
        {
            var node = new JsonObject();
            node["sampleRateMhz"] = acquisition.sampleRateMhz;
            node["acqTimeMs"] = acquisition.acqTimeMs;
            node["segCount"] = acquisition.segCount;
            node["triggerMode"] = acquisition.triggerMode;
            node["triggerDelayMs"] = acquisition.triggerDelayMs;
            node["sweepPeriodMs"] = acquisition.sweepPeriodMs.HasValue
                ? JsonValue.Create(acquisition.sweepPeriodMs.Value) : null;
            node["windowStartMs"] = acquisition.windowStartMs.HasValue
                ? JsonValue.Create(acquisition.windowStartMs.Value) : null;
            node["windowEndMs"] = acquisition.windowEndMs.HasValue
                ? JsonValue.Create(acquisition.windowEndMs.Value) : null;
            return node;
        }

        /// <summary>规范化：对象键 Ordinal 升序、数组保序、无空白；null 段输出 "null"。</summary>
        public static string Canon(JsonNode node)
        {
            if (node == null) return "null";
            return Sort(node).ToJsonString();
        }

        static JsonNode Sort(JsonNode node)
        {
            if (node is JsonObject obj)
            {
                var keys = new List<string>();
                foreach (var pair in obj) keys.Add(pair.Key);
                keys.Sort(StringComparer.Ordinal);

                var sorted = new JsonObject();
                foreach (string key in keys)
                {
                    JsonNode value = obj[key];
                    sorted[key] = value == null ? null : Sort(value);
                }
                return sorted;
            }
            if (node is JsonArray array)
            {
                var copy = new JsonArray();
                foreach (JsonNode item in array)
                    copy.Add(item == null ? null : Sort(item));
                return copy;
            }
            return node.DeepClone();
        }

        static JsonNode ReadNode(string path)
        {
            if (!File.Exists(path)) return null;
            string text = File.ReadAllText(path, Encoding.UTF8);
            if (text.Length == 0) return null;
            return JsonNode.Parse(text);
        }
    }
}
