using System;
using System.IO;
using System.Runtime.InteropServices;

namespace GmixLx.Vendor
{
    // ---- 原生结构体：必须与 ocs_measurement.h 逐字段一致（Windows x64，默认对齐，禁止 Pack=1）----

    [StructLayout(LayoutKind.Sequential)]
    internal struct YinXun2OcsResult
    {
        public UIntPtr Count;
        public IntPtr DistanceM;
        public IntPtr ReflectionDb;
        public double PeakDistanceM;
        public double InsertionLossDb;
        public double ReturnLossDb;
        public int LossStatus;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct YinXun2OcsPeakResult
    {
        public double DistanceM;
        public double PeakLevelDb;
        public double InsertionLossDb;
        public double ReturnLossDb;
        public int LossStatus;
        public int IlStatus;
        public int RlStatus;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct YinXun2OcsResultV4
    {
        public YinXun2OcsResult Summary;
        public UIntPtr PeakCount;
        public IntPtr Peaks;
        public uint SpectrumAverageCount;
        public uint SpectrumAverageReady;
        public int RoiIlStatus;
        public int RoiRlStatus;
    }

    // ---- 导出函数签名（C ABI：__cdecl）----

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int YinXun2OpenDeviceFn(IntPtr sdkBinDirectoryUtf8,
        uint spatialAverageM, uint temporalAverageN, out IntPtr device, byte[] error, UIntPtr errorCapacity);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int YinXun2SetRoiFn(IntPtr device,
        double startM, double endM, byte[] error, UIntPtr errorCapacity);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int YinXun2AcquireV4Fn(IntPtr device,
        out YinXun2OcsResultV4 result, byte[] error, UIntPtr errorCapacity);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void YinXun2ReleaseV4Fn(ref YinXun2OcsResultV4 result);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void YinXun2CloseDeviceFn(ref IntPtr device);

    /// <summary>
    /// ocs_measurement.dll 的加载与导出绑定。全产品只有这里认识原生入口名与库文件布局（规则 30）。
    /// 主 DLL 在 customer/ 根，依赖运行库在 acquisition/bin；加载期间把后者临时加入 DLL 搜索路径。
    /// </summary>
    internal static class YinXun2NativeApi
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool SetDllDirectory(string pathName);

        public static string ResolveBinDirectory(string sdkDirectory)
        {
            if (string.IsNullOrWhiteSpace(sdkDirectory))
                throw new YinXun2Exception(GmixLx.Core.ErrorClass.Configuration,
                    "YinXun_2 SDK 目录不能为空");

            string root = Path.GetFullPath(sdkDirectory);
            string dllPath = Path.Combine(root, YinXun2ProtocolConstants.DllFileName);
            if (!File.Exists(dllPath))
            {
                throw new YinXun2Exception(GmixLx.Core.ErrorClass.Configuration,
                    "未找到 YinXun_2 SDK：" + dllPath + "（应指向含 ocs_measurement.dll 的 customer 目录）");
            }

            string binDirectory = Path.Combine(root,
                YinXun2ProtocolConstants.SdkBinRelativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!Directory.Exists(binDirectory))
            {
                throw new YinXun2Exception(GmixLx.Core.ErrorClass.Configuration,
                    "未找到 YinXun_2 运行库目录：" + binDirectory + "（acquisition/bin 必须完整）");
            }

            return binDirectory;
        }

        /// <summary>按厂商目录布局加载主 DLL；失败按配置错误上报，不静默降级。</summary>
        public static IntPtr Load(string sdkDirectory)
        {
            string binDirectory = ResolveBinDirectory(sdkDirectory);
            string dllPath = Path.Combine(Path.GetFullPath(sdkDirectory), YinXun2ProtocolConstants.DllFileName);

            SetDllDirectory(binDirectory);
            try
            {
                return NativeLibrary.Load(dllPath);
            }
            catch (Exception ex)
            {
                throw new YinXun2Exception(GmixLx.Core.ErrorClass.Configuration,
                    "加载 YinXun_2 SDK 失败：" + dllPath + "（依赖目录 " + binDirectory + "）", ex);
            }
            finally
            {
                SetDllDirectory(null);
            }
        }

        public static T Bind<T>(IntPtr library, string exportName) where T : Delegate
        {
            IntPtr address;
            if (!NativeLibrary.TryGetExport(library, exportName, out address))
            {
                throw new YinXun2Exception(GmixLx.Core.ErrorClass.Configuration,
                    "YinXun_2 SDK 缺少导出函数：" + exportName + "（SDK 版本不匹配）");
            }
            return Marshal.GetDelegateForFunctionPointer<T>(address);
        }
    }
}
