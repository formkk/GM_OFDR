using System;

namespace GmixLx.Core
{
    /// <summary>
    /// OCS 设备标识，来自 <c>&lt;IDN?&gt;</c> 与 <c>&lt;SN?&gt;</c> 实测回包。
    /// 例：<c>&lt;GK,GK-OSW-8x8-SM-1315-FU-R-01,2026-08-15,VERSION1.0&gt;</c>、<c>&lt;26081502&gt;</c>。
    /// </summary>
    public sealed class OcsDeviceInfo
    {
        public string Manufacturer { get; private set; }
        public string Model { get; private set; }
        public string ManufactureDate { get; private set; }
        public string FirmwareVersion { get; private set; }
        public string SerialNumber { get; set; }
        public string RawIdentity { get; private set; }

        private OcsDeviceInfo() { }

        /// <summary>
        /// 解析 <c>&lt;IDN?&gt;</c> 回包内容（已去除定界符）。字段不足时保留原文并返回 null 之外的实例。
        /// </summary>
        public static OcsDeviceInfo ParseIdentity(string identityPayload)
        {
            if (identityPayload == null) throw new ArgumentNullException(nameof(identityPayload));

            var info = new OcsDeviceInfo { RawIdentity = identityPayload };
            var parts = identityPayload.Split(',');
            if (parts.Length >= 1) info.Manufacturer = parts[0].Trim();
            if (parts.Length >= 2) info.Model = parts[1].Trim();
            if (parts.Length >= 3) info.ManufactureDate = parts[2].Trim();
            if (parts.Length >= 4) info.FirmwareVersion = parts[3].Trim();
            return info;
        }

        /// <summary>型号是否属于指定族（1×8 / 8×8），用于自检防错接。</summary>
        public bool ModelMatches(string expectedModelToken)
        {
            if (string.IsNullOrEmpty(Model) || string.IsNullOrEmpty(expectedModelToken)) return false;
            return Model.IndexOf(expectedModelToken, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public override string ToString()
        {
            return Model + " / SN " + (SerialNumber ?? "?") + " / FW " + (FirmwareVersion ?? "?");
        }
    }
}
