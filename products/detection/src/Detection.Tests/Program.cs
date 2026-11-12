using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Detection.Core;
using Detection.Vendor;

namespace Detection.Tests
{
    /// <summary>暴露 Exchange 的测试客户端，用于逐条回放夹具中的原始命令体。</summary>
    internal sealed class RawClient : OcsClientBase
    {
        public RawClient(IOcsTransport transport, int timeoutMs) : base(transport, timeoutMs) { }

        public string SendBody(string body)
        {
            return Exchange(body, DefaultTimeoutMs);
        }
    }

    internal static class Program
    {
        const int Timeout = 300;

        static int Main(string[] args)
        {
            int failures = 0;
            try
            {
                RunConstants(ref failures);
                RunStateModel(ref failures);
                RunParser(ref failures);
                RunFixtureReplay(ref failures);
                RunClients(ref failures);
                RunErrorMapping(ref failures);
                RunLocalRejection(ref failures);
                RunCompleteFrame(ref failures);
                RunSerialization(ref failures);
                RunFrameLog(ref failures);
                OrchestrationTests.Run(ref failures);
            }
            catch (Exception ex)
            {
                Console.WriteLine("FATAL: " + ex);
                return 2;
            }

            Console.WriteLine(failures == 0 ? "PASS detection vendor+orchestration" : "FAIL detection: " + failures);
            return failures == 0 ? 0 : 1;
        }

        // ---------- 常量 ----------

        static void RunConstants(ref int failures)
        {
            Check(ref failures, "常量：1×8 波特率 9600", OcsProtocolConstants.BaudRate1x8 == 9600);
            Check(ref failures, "常量：8×8 波特率 115200（厂商文档 9600 与实物不符）",
                OcsProtocolConstants.BaudRate8x8 == 115200);
            Check(ref failures, "常量：帧定界符 < >",
                OcsProtocolConstants.FrameStart == '<' && OcsProtocolConstants.FrameEnd == '>');
            Check(ref failures, "常量：错误响应字面量",
                OcsProtocolConstants.ResponseError == "<ERROR>"
                && OcsProtocolConstants.ResponseErrorSetValue == "<ER_SetValue>");
            Check(ref failures, "常量：通道取值域 0..8",
                OcsProtocolConstants.MinChannel == 0 && OcsProtocolConstants.MaxChannel == 8);
        }

        // ---------- 状态模型 ----------

        static void RunStateModel(ref int failures)
        {
            Check(ref failures, "Ocs1x8State 越界拒绝",
                Throws<ArgumentOutOfRangeException>(delegate { new Ocs1x8State(9); }));

            Check(ref failures, "Ocs8x8State 长度必须为 8",
                Throws<ArgumentException>(delegate { new Ocs8x8State(new int[7]); }));

            Ocs8x8State identity = Ocs8x8State.Identity();
            Check(ref failures, "Ocs8x8State 单位矩阵 IN3→OUT3", identity.OutputOf(3) == 3);
            Check(ref failures, "Ocs8x8State 占用检测 IN2 占用 OUT2", identity.FindOccupant(2, 1) == 2);
            Check(ref failures, "Ocs8x8State 断开后不占用", identity.With(2, 0).FindOccupant(2, 1) == 0);
            Check(ref failures, "Ocs8x8State With 不修改原状态", identity.OutputOf(2) == 2);
            Check(ref failures, "Ocs1x8State 断开语义", new Ocs1x8State(0).IsDisconnected);
        }

        // ---------- 解析器 ----------

        static void RunParser(ref int failures)
        {
            Check(ref failures, "Payload 去定界符", OcsResponseParser.Payload("<ABC>") == "ABC");
            Check(ref failures, "Payload 缺定界符返回 null", OcsResponseParser.Payload("ABC") == null);

            int input;
            int output;
            Check(ref failures, "解析设置回包 <1-03>",
                OcsResponseParser.TryParseSetAck("<1-03>", out input, out output) && input == 1 && output == 3);
            Check(ref failures, "解析单路状态 <SW4-02>",
                OcsResponseParser.TryParseSwitchState("<SW4-02>", out input, out output) && input == 4 && output == 2);

            int[] matrix;
            Check(ref failures, "解析全量矩阵 <SW01_..._08>",
                OcsResponseParser.TryParseMatrix("<SW01_02_03_04_05_06_07_08>", out matrix)
                && matrix.Length == 8 && matrix[0] == 1 && matrix[7] == 8);
            Check(ref failures, "解析全量矩阵含断开 <SW00_01_...>",
                OcsResponseParser.TryParseMatrix("<SW00_01_03_04_05_06_07_08>", out matrix)
                && matrix[0] == 0 && matrix[1] == 1);
            Check(ref failures, "解析全量矩阵分段数不足时失败",
                !OcsResponseParser.TryParseMatrix("<SW01_02>", out matrix));
            Check(ref failures, "解析全量矩阵含越界值时失败",
                !OcsResponseParser.TryParseMatrix("<SW01_02_03_04_05_06_07_09>", out matrix));

            Check(ref failures, "构造单路命令 SETSW1-03",
                OcsResponseParser.BuildRouteCommand(1, 3) == "SETSW1-03");
            Check(ref failures, "构造断开命令 SETSW8-00",
                OcsResponseParser.BuildRouteCommand(8, 0) == "SETSW8-00");
            Check(ref failures, "构造整矩阵命令 SETSW12345678",
                OcsResponseParser.BuildMatrixCommand(new[] { 1, 2, 3, 4, 5, 6, 7, 8 }) == "SETSW12345678");
        }

        // ---------- 夹具回放（实机抓包） ----------

        static void RunFixtureReplay(ref int failures)
        {
            string path = FrameFixture.ResolvePath();
            if (path == null)
            {
                Check(ref failures, "夹具 tests/fixtures/ocs/protocol_frames.txt 存在", false, "未找到夹具");
                return;
            }

            List<FrameSample> samples = FrameFixture.Load(path);
            Check(ref failures, "夹具至少包含 20 条记录", samples.Count >= 20, "实际 " + samples.Count);

            int replayed = 0;
            foreach (FrameSample sample in samples)
            {
                // 半帧挂起属真实串口缓冲行为，Mock 无法模拟；由 RunCompleteFrame 从发送侧保证不发半帧。
                if (sample.Label.StartsWith("missing_close", StringComparison.Ordinal)) continue;

                string body = FrameFixture.ToCommandBody(sample.Request);
                var mock = new MemoryOcsTransport();
                mock.Open();
                var client = new RawClient(mock, Timeout);

                if (string.IsNullOrEmpty(body))
                {
                    bool rejected = Throws<ArgumentException>(delegate { client.SendBody(body ?? string.Empty); });
                    Check(ref failures, "夹具 " + sample.Label + "：空命令本地拒绝", rejected);
                    Check(ref failures, "夹具 " + sample.Label + "：空命令未发送", mock.Written.Count == 0);
                    replayed++;
                    continue;
                }

                if (!sample.ExpectTimeout) mock.Map(sample.Request, sample.Response);
                else mock.SimulateReadTimeout = true;

                bool deviceError = IsDeviceError(sample.Response);
                try
                {
                    string actual = client.SendBody(body);
                    bool ok = !sample.ExpectTimeout && !deviceError && actual == sample.Response;
                    Check(ref failures, "夹具 " + sample.Label + "：" + body, ok,
                        ok ? null : "实际回包 " + actual);
                }
                catch (OcsTransportException)
                {
                    Check(ref failures, "夹具 " + sample.Label + "：预期超时", sample.ExpectTimeout);
                }
                catch (OcsDeviceException ex)
                {
                    Check(ref failures, "夹具 " + sample.Label + "：设备错误映射",
                        deviceError && ex.Class == ErrorClass.Device, ex.Message);
                }
                catch (OcsProtocolException ex)
                {
                    Check(ref failures, "夹具 " + sample.Label + "：不应判为协议错误", false, ex.Message);
                }
                replayed++;
            }

            Check(ref failures, "夹具回放覆盖条数", replayed >= 20, "回放 " + replayed);
        }

        // ---------- 客户端命令 ----------

        static void RunClients(ref int failures)
        {
            // 1×8
            var m1 = new MemoryOcsTransport();
            m1.Open();
            m1.Map("<IDN?>", "<GK,GK-OSW-1X8-SM-1315-FU-R-01,2026-08-16,VERSION1.0>")
              .Map("<SN?>", "<26081501>")
              .Map("<QURSW?>", "<SW1-03>")
              .Map("<SETSW1-03>", "<1-03>");

            var c1 = new Ocs1x8Client(m1, Timeout);
            OcsDeviceInfo info1 = c1.Identify();
            Check(ref failures, "1×8 型号识别", info1.ModelMatches(OcsProtocolConstants.ModelToken1x8), info1.Model);
            Check(ref failures, "1×8 不误认 8×8", !info1.ModelMatches(OcsProtocolConstants.ModelToken8x8));
            Check(ref failures, "1×8 SN 查询", c1.ReadSerialNumber() == "26081501");
            Check(ref failures, "1×8 状态查询", c1.QueryState().Output == 3);
            Check(ref failures, "1×8 设置通道", c1.SetOutput(3).Output == 3);

            // 1×8 返回非预期输入通道
            var m1bad = new MemoryOcsTransport();
            m1bad.Open();
            m1bad.Map("<QURSW?>", "<SW2-03>");
            var c1bad = new Ocs1x8Client(m1bad, Timeout);
            Check(ref failures, "1×8 拒绝非预期输入通道",
                Throws<OcsProtocolException>(delegate { c1bad.QueryState(); }));

            // 8×8
            var m8 = new MemoryOcsTransport();
            m8.Open();
            m8.Map("<IDN?>", "<GK,GK-OSW-8x8-SM-1315-FU-R-01,2026-08-15,VERSION1.0>")
              .Map("<SN?>", "<26081502>")
              .Map("<QURSW?>", "<SW01_02_03_04_05_06_07_08>")
              .Map("<QURSW4?>", "<SW4-02>")
              .Map("<SETSW8-00>", "<8-00>");

            var c8 = new Ocs8x8Client(m8, Timeout);
            OcsDeviceInfo info8 = c8.Identify();
            Check(ref failures, "8×8 型号识别", info8.ModelMatches(OcsProtocolConstants.ModelToken8x8), info8.Model);

            Ocs8x8State all = c8.QueryAll();
            Check(ref failures, "8×8 全量查询", all.OutputOf(1) == 1 && all.OutputOf(8) == 8);
            Check(ref failures, "8×8 单路查询", c8.QueryOutput(4) == 2);
            Check(ref failures, "8×8 单路断开", c8.SetRoute(8, 0).OutputOf(8) == 0);

            // 整矩阵设置
            var m8m = new MemoryOcsTransport();
            m8m.Open();
            m8m.Map("<SETSW87654321>", "<OK>");
            var c8m = new Ocs8x8Client(m8m, Timeout);
            Ocs8x8State reversed = c8m.SetMatrix(new[] { 8, 7, 6, 5, 4, 3, 2, 1 });
            Check(ref failures, "8×8 整矩阵设置", reversed.OutputOf(1) == 8 && reversed.OutputOf(8) == 1);

            // 复位后状态失效
            var m8r = new MemoryOcsTransport();
            m8r.Open();
            m8r.Map("<QURSW?>", "<SW01_02_03_04_05_06_07_08>")
               .Map("<RST>", "<OK>");
            var c8r = new Ocs8x8Client(m8r, Timeout);
            c8r.QueryAll();
            c8r.Reset(Timeout);
            Check(ref failures, "8×8 复位后矩阵缓存失效", c8r.KnownMatrix == null);
        }

        // ---------- 错误映射 ----------

        static void RunErrorMapping(ref int failures)
        {
            CheckMapsTo(ref failures, "<ERROR>", "设备错误 <ERROR>", ErrorClass.Device, true);
            CheckMapsTo(ref failures, "<ER_SetValue>", "设备错误 <ER_SetValue>", ErrorClass.Device, true);
            CheckMapsTo(ref failures, "ABC", "协议错误：不以 < 起始", ErrorClass.Protocol, false);
            CheckMapsTo(ref failures, "<ABC", "协议错误：不以 > 结束", ErrorClass.Protocol, false);
            CheckMapsTo(ref failures, "<AB?C>", "协议错误：回包含问号", ErrorClass.Protocol, false);
            CheckMapsTo(ref failures, "<" + new string('A', 300) + ">", "协议错误：回包超长", ErrorClass.Protocol, false);
        }

        static void CheckMapsTo(ref int failures, string response, string label, ErrorClass expected, bool device)
        {
            var mock = new MemoryOcsTransport();
            mock.Open();
            mock.Map("<ABC>", response);
            var client = new RawClient(mock, Timeout);
            try
            {
                client.SendBody("ABC");
                Check(ref failures, label, false, "未抛出异常");
            }
            catch (OcsException ex)
            {
                Check(ref failures, label, ex.Class == expected, "实际类别 " + ex.Class);
                if (device)
                    Check(ref failures, label + "：携带原始回包", ex.RawResponse == response);
            }
        }

        // ---------- 本地拒绝（发送前） ----------

        static void RunLocalRejection(ref int failures)
        {
            var mock = new MemoryOcsTransport();
            mock.Open();
            mock.Map("<QURSW?>", "<SW01_02_03_04_05_06_07_08>");
            var c8 = new Ocs8x8Client(mock, Timeout);

            bool conflict = Throws<OcsRouteConflictException>(delegate { c8.SetRoute(1, 2); });
            Check(ref failures, "8×8 单路唯一性本地拒绝", conflict);
            Check(ref failures, "冲突时仅发送了查询帧", mock.Written.Count == 1, "实际 " + mock.Written.Count);

            OcsRouteConflictException captured = null;
            try { c8.SetRoute(1, 2); }
            catch (OcsRouteConflictException ex) { captured = ex; }
            Check(ref failures, "冲突归类为 Configuration",
                captured != null && captured.Class == ErrorClass.Configuration);

            bool matrixConflict = Throws<OcsRouteConflictException>(
                delegate { c8.SetMatrix(new[] { 1, 1, 3, 4, 5, 6, 7, 8 }, Timeout); });
            Check(ref failures, "8×8 整矩阵重复输出本地拒绝", matrixConflict);
            Check(ref failures, "整矩阵冲突未发送", mock.Written.Count == 1, "实际 " + mock.Written.Count);

            Check(ref failures, "8×8 输入越界拒绝",
                Throws<ArgumentOutOfRangeException>(delegate { c8.SetRoute(0, 1); }));
            Check(ref failures, "8×8 输出越界拒绝",
                Throws<ArgumentOutOfRangeException>(delegate { c8.SetRoute(1, 9); }));
            Check(ref failures, "8×8 整矩阵长度错误拒绝",
                Throws<ArgumentException>(delegate { c8.SetMatrix(new[] { 1, 2, 3 }, Timeout); }));

            var c1 = new Ocs1x8Client(new MemoryOcsTransport(), Timeout);
            Check(ref failures, "1×8 输出越界拒绝",
                Throws<ArgumentOutOfRangeException>(delegate { c1.SetOutput(9); }));
        }

        // ---------- 完整帧 ----------

        static void RunCompleteFrame(ref int failures)
        {
            var mock = new MemoryOcsTransport();
            mock.Open();
            mock.Map("<IDN?>", "<GK,GK-OSW-1X8-SM-1315-FU-R-01,2026-08-16,VERSION1.0>");
            var c1 = new Ocs1x8Client(mock, Timeout);
            c1.Identify();

            bool ok = mock.Written.Count == 1;
            string detail = null;
            foreach (string frame in mock.Written)
            {
                if (frame.Length < 2 || frame[0] != '<' || frame[frame.Length - 1] != '>')
                {
                    ok = false;
                    detail = "未完整定界：" + frame;
                    break;
                }
                if (frame.IndexOf('\r') >= 0 || frame.IndexOf('\n') >= 0)
                {
                    ok = false;
                    detail = "含结束符：" + OcsException.ToHexDigest(frame);
                    break;
                }
            }
            Check(ref failures, "发送帧总是完整 <...> 且不带结束符", ok, detail);

            var c = new RawClient(mock, Timeout);
            Check(ref failures, "命令体含定界符时本地拒绝",
                Throws<ArgumentException>(delegate { c.SendBody("<IDN?>"); }));
        }

        // ---------- 串行化 ----------

        static void RunSerialization(ref int failures)
        {
            var mock = new MemoryOcsTransport();
            mock.Open();
            mock.Map("<SN?>", "<26081501>");
            var client = new RawClient(mock, Timeout);

            const int workers = 8;
            const int perWorker = 10;
            var tasks = new Task[workers];
            for (int i = 0; i < workers; i++)
            {
                tasks[i] = Task.Run(delegate
                {
                    for (int k = 0; k < perWorker; k++) client.SendBody("SN?");
                });
            }
            Task.WaitAll(tasks);

            Check(ref failures, "并发调用全部完成",
                mock.Written.Count == workers * perWorker, "实际 " + mock.Written.Count);

            bool allComplete = true;
            foreach (string frame in mock.Written)
            {
                if (frame != "<SN?>") { allComplete = false; break; }
            }
            Check(ref failures, "并发下每条帧均完整且唯一对应一次调用", allComplete);
        }

        // ---------- 帧日志 ----------

        static void RunFrameLog(ref int failures)
        {
            var mock = new MemoryOcsTransport();
            mock.Open();
            mock.Map("<SN?>", "<26081501>");
            var client = new Ocs1x8Client(mock, 500);

            OcsFrameLog captured = null;
            client.FrameExchanged += delegate(OcsFrameLog log) { captured = log; };
            client.ReadSerialNumber();

            Check(ref failures, "FrameExchanged 触发", captured != null);
            if (captured == null) return;

            Check(ref failures, "日志含请求原文", captured.Request == "<SN?>", captured.Request);
            Check(ref failures, "日志含回包原文", captured.Response == "<26081501>", captured.Response);
            Check(ref failures, "日志标记成功", captured.Succeeded);
            Check(ref failures, "日志含耗时", captured.ElapsedMs >= 0);

            var mock2 = new MemoryOcsTransport();
            mock2.Open();
            mock2.Map("<SN?>", "<ERROR>");
            var client2 = new Ocs1x8Client(mock2, 500);
            OcsFrameLog failureLog = null;
            client2.FrameExchanged += delegate(OcsFrameLog log) { failureLog = log; };
            try { client2.ReadSerialNumber(); }
            catch (OcsDeviceException) { }

            Check(ref failures, "失败也写入帧日志", failureLog != null && !failureLog.Succeeded);
            Check(ref failures, "失败日志带错误分类",
                failureLog != null && failureLog.ErrorClass == ErrorClass.Device);
        }

        // ---------- 辅助 ----------

        static bool IsDeviceError(string response)
        {
            return string.Equals(response, OcsProtocolConstants.ResponseError, StringComparison.Ordinal)
                || string.Equals(response, OcsProtocolConstants.ResponseErrorSetValue, StringComparison.Ordinal);
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

        static void Check(ref int failures, string name, bool ok, string detail = null)
        {
            if (ok)
            {
                Console.WriteLine("PASS " + name);
                return;
            }

            failures++;
            Console.WriteLine("FAIL " + name + (string.IsNullOrEmpty(detail) ? string.Empty : " :: " + detail));
        }
    }
}
