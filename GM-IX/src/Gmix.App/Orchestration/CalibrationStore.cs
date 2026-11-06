using System;
using System.IO;

using Gmix.Data;

namespace Gmix.App.Orchestration
{
    /// <summary>校准版本化存储：`_calibration/CAL-YYYYMMDD-NNN/calibration.json`（SRS FR-CAL-02）。</summary>
    public static class CalibrationStore
    {
        public static string Save(string dataRoot, CalibrationModel model, string configHash)
        {
            if (model == null) throw new ArgumentNullException(nameof(model));
            string root = Path.Combine(dataRoot, "_calibration");
            Directory.CreateDirectory(root);

            string prefix = "CAL-" + DateTime.Now.ToString("yyyyMMdd") + "-";
            int max = 0;
            foreach (string dir in Directory.GetDirectories(root))
            {
                string name = Path.GetFileName(dir);
                if (!name.StartsWith(prefix, StringComparison.Ordinal)) continue;
                int seq;
                if (int.TryParse(name.Substring(prefix.Length), out seq) && seq > max) max = seq;
            }

            string calibrationId = prefix + (max + 1).ToString("D3");
            var doc = new CalibrationDocument
            {
                schemaVersion = "1.0",
                calibrationId = calibrationId,
                createdAtUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                configHash = configHash,
                method = "manual",
                distScale = model.distScale,
                psdRef = model.psdRef,
                rlOffsetK = model.rlOffsetK,
                ilSlope = model.ilSlope,
                ilIntercept = model.ilIntercept,
                triggerThresholdDb = model.triggerThresholdDb
            };

            string path = Path.Combine(root, calibrationId, "calibration.json");
            JsonStore.Write(path, doc);
            model.calibrationId = calibrationId;
            model.configHash = configHash;
            return path;
        }

        public static CalibrationModel LoadLatest(string dataRoot)
        {
            string root = Path.Combine(dataRoot, "_calibration");
            if (!Directory.Exists(root)) return null;
            string[] dirs = Directory.GetDirectories(root);
            Array.Sort(dirs, StringComparer.Ordinal);
            for (int i = dirs.Length - 1; i >= 0; i--)
            {
                string path = Path.Combine(dirs[i], "calibration.json");
                if (File.Exists(path))
                    return CalibrationModel.FromDocument(JsonStore.Read<CalibrationDocument>(path));
            }
            return null;
        }
    }
}
