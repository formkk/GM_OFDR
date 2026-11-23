using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using GmixLx.Core;
using GmixLx.Vendor;

namespace GmixLx.Tests
{
    /// <summary>
    /// C4 单元测试：YinXun_2 SDK 适配器。
    /// 不加载真实 DLL：ABI 用 Marshal 尺寸/偏移校验，会话与就绪帧语义用假 SDK 覆盖。
    /// </summary>
    internal static class YinXun2ClientTests
    {
        static void Check(ref int failures, string name, bool ok, string detail = null)
        {
            if (ok) return;
            failures++;
            Console.WriteLine("FAIL " + name + (string.IsNullOrEmpty(detail) ? string.Empty : " :: " + detail));
        }

        public static void Run(ref int failures)
        {
            RunAbi(ref failures);
            RunErrorMapping(ref failures);
            RunSdkDirectory(ref failures);
            RunClientValidation(ref failures);
            RunReadyFrames(ref failures);
            Console.WriteLine("PASS YinXun_2 SDK 适配器（ABI/错误映射/就绪帧）");
        }

        // ---------- ABI ----------

        static void RunAbi(ref int failures)
        {
            Check(ref failures, "YinXun_2 常量：点数 160000",
                YinXun2ProtocolConstants.PointCount == 160000);
            Check(ref failures, "YinXun_2 常量：点距与厂商一致",
                YinXun2ProtocolConstants.PitchM == 0.0004258363425773126);
            Check(ref failures, "YinXun_2 常量：轴末端 68.133389 m",
                Math.Abs(YinXun2ProtocolConstants.MaxDistanceM - 68.133389) < 1e-5,
                YinXun2ProtocolConstants.MaxDistanceM.ToString("F9", CultureInfo.InvariantCulture));

            Check(ref failures, "YinXun_2 ABI：OcsResult 56 字节",
                Marshal.SizeOf<YinXun2OcsResult>() == 56,
                Marshal.SizeOf<YinXun2OcsResult>().ToString(CultureInfo.InvariantCulture));
            Check(ref failures, "YinXun_2 ABI：OcsPeakResult 48 字节",
                Marshal.SizeOf<YinXun2OcsPeakResult>() == 48,
                Marshal.SizeOf<YinXun2OcsPeakResult>().ToString(CultureInfo.InvariantCulture));
            Check(ref failures, "YinXun_2 ABI：OcsResultV4 88 字节",
                Marshal.SizeOf<YinXun2OcsResultV4>() == 88,
                Marshal.SizeOf<YinXun2OcsResultV4>().ToString(CultureInfo.InvariantCulture));

            Check(ref failures, "YinXun_2 ABI：V4.Peaks 偏移 64",
                Marshal.OffsetOf<YinXun2OcsResultV4>("Peaks").ToInt64() == 64,
                Marshal.OffsetOf<YinXun2OcsResultV4>("Peaks").ToString());
            Check(ref failures, "YinXun_2 ABI：V4.PeakCount 偏移 56",
                Marshal.OffsetOf<YinXun2OcsResultV4>("PeakCount").ToInt64() == 56);
            Check(ref failures, "YinXun_2 ABI：V4 平均计数字段连续",
                Marshal.OffsetOf<YinXun2OcsResultV4>("SpectrumAverageCount").ToInt64() == 72
                && Marshal.OffsetOf<YinXun2OcsResultV4>("SpectrumAverageReady").ToInt64() == 76
                && Marshal.OffsetOf<YinXun2OcsResultV4>("RoiIlStatus").ToInt64() == 80
                && Marshal.OffsetOf<YinXun2OcsResultV4>("RoiRlStatus").ToInt64() == 84);
        }

        // ---------- 错误映射 ----------

        static void RunErrorMapping(ref int failures)
        {
            Check(ref failures, "YinXun_2 错误：0 → None",
                YinXun2Exception.Classify(0) == ErrorClass.None);
            Check(ref failures, "YinXun_2 错误：−1 → Configuration",
                YinXun2Exception.Classify(-1) == ErrorClass.Configuration);
            Check(ref failures, "YinXun_2 错误：−2 → Measurement",
                YinXun2Exception.Classify(-2) == ErrorClass.Measurement);
            Check(ref failures, "YinXun_2 错误：−3 → Device",
                YinXun2Exception.Classify(-3) == ErrorClass.Device);
            Check(ref failures, "YinXun_2 错误：未知 rc 按设备错误",
                YinXun2Exception.Classify(-99) == ErrorClass.Device);

            bool threwOnOk = false;
            try { YinXun2Exception.ThrowIfFailed(0, "op", "x"); }
            catch (YinXun2Exception) { threwOnOk = true; }
            Check(ref failures, "YinXun_2 错误：rc=0 不抛", !threwOnOk);

            YinXun2Exception captured = null;
            try { YinXun2Exception.ThrowIfFailed(-2, "ocs_acquire_measurement_v4", "光谱采集超时"); }
            catch (YinXun2Exception ex) { captured = ex; }
            Check(ref failures, "YinXun_2 错误：抛出并带分类",
                captured != null && captured.ErrorClass == ErrorClass.Measurement);
            Check(ref failures, "YinXun_2 错误：消息含操作名与厂商文本",
                captured != null
                && captured.Message.Contains("ocs_acquire_measurement_v4")
                && captured.Message.Contains("光谱采集超时"));
        }

        // ---------- SDK 目录 ----------

        static void RunSdkDirectory(ref int failures)
        {
            Check(ref failures, "YinXun_2 SDK：空目录拒绝", Throws<YinXun2Exception>(
                delegate { YinXun2NativeApi.ResolveBinDirectory("  "); }));
            Check(ref failures, "YinXun_2 SDK：目录不存在拒绝", Throws<YinXun2Exception>(
                delegate { YinXun2NativeApi.ResolveBinDirectory(@"C:\__yx2_missing_sdk__"); }));

            YinXun2Exception captured = null;
            try { YinXun2NativeApi.ResolveBinDirectory(@"C:\__yx2_missing_sdk__"); }
            catch (YinXun2Exception ex) { captured = ex; }
            Check(ref failures, "YinXun_2 SDK：缺失按配置错误上报",
                captured != null && captured.ErrorClass == ErrorClass.Configuration);
            Check(ref failures, "YinXun_2 SDK：错误消息带期望文件名",
                captured != null && captured.Message.Contains(YinXun2ProtocolConstants.DllFileName));

            Check(ref failures, "YinXun_2 SDK：未封装 DLL 时 Open 立即失败", Throws<YinXun2Exception>(
                delegate { new YinXun2NativeSdk().Open(@"C:\__yx2_missing_sdk__", 1, 10); }));
        }

        // ---------- 客户端参数校验 ----------

        static void RunClientValidation(ref int failures)
        {
            var fake = new FakeYinXun2Sdk();
            var client = new YinXun2Client(fake);

            Check(ref failures, "YinXun_2 客户端：未打开时 Acquire 拒绝", Throws<YinXun2Exception>(
                delegate { client.Acquire(); }));
            Check(ref failures, "YinXun_2 客户端：空 SDK 目录拒绝", Throws<ArgumentException>(
                delegate { client.Open(string.Empty); }));
            Check(ref failures, "YinXun_2 客户端：m=0 拒绝", Throws<ArgumentOutOfRangeException>(
                delegate { client.Open(@"C:\sdk", 0, 10); }));
            Check(ref failures, "YinXun_2 客户端：n=0 拒绝", Throws<ArgumentOutOfRangeException>(
                delegate { client.Open(@"C:\sdk", 1, 0); }));
            Check(ref failures, "YinXun_2 客户端：非法参数不会调用 SDK",
                fake.OpenCalls == 0 && fake.AcquireCalls == 0);

            client.Open(@"C:\sdk");
            Check(ref failures, "YinXun_2 客户端：默认 m=1 / n=4",
                fake.LastM == YinXun2ProtocolConstants.DefaultSpatialAverageM
                && fake.LastN == YinXun2ProtocolConstants.DefaultTemporalAverageN
                && fake.LastN == 4);
            Check(ref failures, "YinXun_2 客户端：打开状态可查", client.IsOpen);

            Check(ref failures, "YinXun_2 ROI：起点为负拒绝", Throws<ArgumentOutOfRangeException>(
                delegate { client.SetAnalysisRoi(-1.0, 5.0); }));
            Check(ref failures, "YinXun_2 ROI：start ≥ end 拒绝", Throws<ArgumentException>(
                delegate { client.SetAnalysisRoi(5.0, 5.0); }));
            Check(ref failures, "YinXun_2 ROI：NaN 拒绝", Throws<ArgumentException>(
                delegate { client.SetAnalysisRoi(double.NaN, 5.0); }));
            Check(ref failures, "YinXun_2 ROI：超出轴末端拒绝", Throws<ArgumentOutOfRangeException>(
                delegate { client.SetAnalysisRoi(1.0, YinXun2ProtocolConstants.MaxDistanceM + 1.0); }));
            Check(ref failures, "YinXun_2 ROI：非法值未下发", fake.RoiCalls == 0);

            client.SetAnalysisRoi(3.5, 12.0);
            Check(ref failures, "YinXun_2 ROI：合法值透传",
                fake.RoiCalls == 1 && fake.LastRoiStart == 3.5 && fake.LastRoiEnd == 12.0);

            // 厂商异常原样上抛，不被客户端吞掉
            var failing = new FakeYinXun2Sdk
            {
                AcquireError = new YinXun2Exception(ErrorClass.Device, "设备断连")
            };
            var failingClient = new YinXun2Client(failing);
            failingClient.Open(@"C:\sdk");
            YinXun2Exception captured = null;
            try { failingClient.Acquire(); }
            catch (YinXun2Exception ex) { captured = ex; }
            Check(ref failures, "YinXun_2 客户端：SDK 异常原样上抛",
                captured != null && captured.ErrorClass == ErrorClass.Device);

            client.Dispose();
            Check(ref failures, "YinXun_2 客户端：释放后关闭并置为非打开",
                !client.IsOpen && fake.CloseCalls == 1 && fake.DisposeCalls == 1);
        }

        // ---------- 就绪帧 ----------

        static void RunReadyFrames(ref int failures)
        {
            var fake = new FakeYinXun2Sdk();
            var client = new YinXun2Client(fake);
            client.Open(@"C:\sdk", 1, 10);

            fake.Frames.Enqueue(NotReady(1));
            fake.Frames.Enqueue(NotReady(2));
            fake.Frames.Enqueue(Ready(3));

            YinXun2Measurement measurement = client.AcquireReady(2000);
            Check(ref failures, "YinXun_2 就绪帧：跳过中间帧",
                measurement.AverageReady && measurement.AverageCount == 3 && fake.AcquireCalls == 3);

            fake.Frames.Clear();
            fake.Fallback = NotReady(4);
            Check(ref failures, "YinXun_2 就绪帧：超时按测量错误", Throws<YinXun2Exception>(
                delegate { client.AcquireReady(60); }));

            fake.Fallback = null;
            Check(ref failures, "YinXun_2 就绪帧：默认帧可用",
                client.AcquireReady(1000).AverageReady);
        }

        static YinXun2Measurement Ready(uint count)
        {
            return new YinXun2Measurement
            {
                DistanceM = new[] { 0.0, 1.0 },
                LevelsDb = new[] { -100.0, -40.0 },
                AverageCount = count,
                AverageReady = true
            };
        }

        static YinXun2Measurement NotReady(uint count)
        {
            return new YinXun2Measurement { AverageCount = count, AverageReady = false };
        }

        static bool Throws<T>(Action action) where T : Exception
        {
            try
            {
                action();
                return false;
            }
            catch (T)
            {
                return true;
            }
        }
    }
}
