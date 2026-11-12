using System;
using System.Collections.Generic;
using System.IO;
using Detection.Core;
using Detection.Orchestration;
using Gmix.Data;

namespace Detection.Tests
{
    /// <summary>阶段 B 编排层测试：命名、计划校验、汇总、关注提示、状态机、提交与恢复。</summary>
    internal static class OrchestrationTests
    {
        public static void Run(ref int failures)
        {
            RunNaming(ref failures);
            RunPlanValidation(ref failures);
            RunSummaryAndFlags(ref failures);
            RunConfigHash(ref failures);
            RunFullBatch(ref failures);
            RunFailureInjection(ref failures);
            RunAbortAndResume(ref failures);
            RunAveragePath(ref failures);
        }

        // ---------- 命名 ----------

        static void RunNaming(ref int failures)
        {
            Check(ref failures, "命名 pathId", DetectionNaming.PathId(1, 1) == "IN01_OUT01",
                DetectionNaming.PathId(1, 1));
            Check(ref failures, "命名 pathId 8x8", DetectionNaming.PathId(8, 7) == "IN08_OUT07");

            int i;
            int j;
            bool ok = DetectionNaming.TryParsePathId("IN08_OUT07", out i, out j);
            Check(ref failures, "解析 pathId", ok && i == 8 && j == 7, i + "/" + j);
            Check(ref failures, "拒绝非法 pathId", !DetectionNaming.TryParsePathId("IN8_OUT7", out i, out j));

            string batchId = DetectionNaming.BatchId(new DateTime(2026, 11, 11, 15, 30, 0, DateTimeKind.Utc), 1);
            Check(ref failures, "命名 batchId", batchId == "BATCH-20261111-153000-001", batchId);

            string groupId = DetectionNaming.GroupId(new DateTime(2026, 11, 11, 15, 30, 0, DateTimeKind.Utc), 64);
            Check(ref failures, "命名 groupId", groupId == "GR-20261111-1530-064", groupId);
            Check(ref failures, "命名扫描目录", DetectionNaming.ScanFolder(7) == "R007");
        }

        // ---------- 计划校验 ----------

        static void RunPlanValidation(ref int failures)
        {
            PathPlan full = PathPlan.CreateFull("plan-v1", 1);
            Check(ref failures, "计划：64 条通过校验", full.Validate(true).Count == 0,
                string.Join(";", full.Validate(true).ToArray()));

            PathPlan missing = PathPlan.CreateFull("plan-v1", 1);
            var list = new List<PathPlanEntry>(missing.entries);
            list.RemoveAt(10);
            missing.entries = list.ToArray();
            Check(ref failures, "计划：缺路径被拒", missing.Validate(true).Count > 0);

            PathPlan duplicate = PathPlan.CreateFull("plan-v1", 1);
            duplicate.entries[1].pathId = duplicate.entries[0].pathId;
            duplicate.entries[1].inChannel = duplicate.entries[0].inChannel;
            duplicate.entries[1].outChannel = duplicate.entries[0].outChannel;
            Check(ref failures, "计划：重复 pathId 被拒", duplicate.Validate(true).Count > 0);

            PathPlan zeroN = PathPlan.CreateFull("plan-v1", 1);
            zeroN.entries[0].averageCount = 0;
            Check(ref failures, "计划：N=0 被拒", zeroN.Validate(true).Count > 0);

            PathPlan badChannel = PathPlan.CreateFull("plan-v1", 1);
            badChannel.entries[0].outChannel = 9;
            Check(ref failures, "计划：输出越界被拒", badChannel.Validate(true).Count > 0);

            PathPlan subset = PathPlan.CreateFull("plan-v1", 2);
            subset.entries = new[] { subset.entries[0] };
            Check(ref failures, "计划：子集可用于重测", subset.Validate(false).Count == 0);
        }

        // ---------- 汇总与关注提示 ----------

        static void RunSummaryAndFlags(ref int failures)
        {
            var paths = new[]
            {
                new PathResult { pathState = PathStateNames.Computed, averageCount = 1, validCount = 1 },
                new PathResult { pathState = PathStateNames.Computed, averageCount = 1, validCount = 1 },
                new PathResult { pathState = PathStateNames.Failed, averageCount = 1, attempt = 1 },
                new PathResult { pathState = PathStateNames.NotAttempted, averageCount = 1, attempt = 1 }
            };

            BatchSummaryInfo summary = BatchSummaryCalculator.Compute(paths);
            Check(ref failures, "汇总 total", summary.total == 4, summary.total.ToString());
            Check(ref failures, "汇总 completed", summary.completed == 2);
            Check(ref failures, "汇总 measurementFailed", summary.measurementFailed == 1);
            Check(ref failures, "汇总 notAttempted", summary.notAttempted == 1);
            Check(ref failures, "汇总 计数闭合",
                summary.completed + summary.measurementFailed + summary.notAttempted == summary.total);
            Check(ref failures, "终态：有失败即 CompletedWithFailures",
                BatchSummaryCalculator.ResolveTerminalState(summary) == BatchState.CompletedWithFailures);

            var failed = new PathResult
            {
                pathState = PathStateNames.Failed,
                connectivity = ConnectivityNames.InvalidFrame,
                averageCount = 1,
                validCount = 0
            };
            AttentionFlag[] flags = FlagEvaluator.Evaluate(failed, CalibrationStatus.Uncalibrated, false, null);
            Check(ref failures, "提示：measurement_failed", Contains(flags, AttentionFlag.MeasurementFailed));
            Check(ref failures, "提示：calibration_invalid", Contains(flags, AttentionFlag.CalibrationInvalid));
            Check(ref failures, "提示：baseline_missing", Contains(flags, AttentionFlag.BaselineMissing));

            var noPeak = new PathResult
            {
                pathState = PathStateNames.Computed,
                connectivity = ConnectivityNames.NoPeak,
                averageCount = 3,
                validCount = 2
            };
            AttentionFlag[] flags2 = FlagEvaluator.Evaluate(noPeak, CalibrationStatus.Calibrated, false, null);
            Check(ref failures, "提示：no_peak", Contains(flags2, AttentionFlag.NoPeak));
            Check(ref failures, "提示：avg_incomplete", Contains(flags2, AttentionFlag.AvgIncomplete));

            var outOfRange = new PathResult
            {
                pathState = PathStateNames.Computed,
                connectivity = ConnectivityNames.Ok,
                averageCount = 1,
                validCount = 1,
                ilDb = -5.0,
                rlDb = -60.0
            };
            var range = new ReferenceRange { ilMinDb = -2.0, ilMaxDb = -0.5 };
            AttentionFlag[] flags3 = FlagEvaluator.Evaluate(outOfRange, CalibrationStatus.Calibrated, true, range);
            Check(ref failures, "提示：out_of_reference", Contains(flags3, AttentionFlag.OutOfReference));
            Check(ref failures, "提示：有基线时不加 baseline_missing", !Contains(flags3, AttentionFlag.BaselineMissing));

            string[] names = FlagEvaluator.ToNames(flags3);
            Check(ref failures, "提示：持久化字面量", Array.IndexOf(names, "out_of_reference") >= 0,
                string.Join(",", names));
        }

        // ---------- 配置哈希 ----------

        static void RunConfigHash(ref int failures)
        {
            DetectionConfig a = BuildConfig(PathPlan.CreateFull("plan-v1", 1), 100);
            DetectionConfig b = BuildConfig(PathPlan.CreateFull("plan-v1", 1), 100);
            Check(ref failures, "configHash：同配置同哈希", a.ComputeConfigHash() == b.ComputeConfigHash());

            DetectionConfig c = BuildConfig(PathPlan.CreateFull("plan-v1", 2), 100);
            Check(ref failures, "configHash：N 变化后哈希不同", a.ComputeConfigHash() != c.ComputeConfigHash());

            DetectionConfig d = BuildConfig(PathPlan.CreateFull("plan-v1", 1), 120);
            Check(ref failures, "configHash：settleMs 变化后哈希不同", a.ComputeConfigHash() != d.ComputeConfigHash());
            Check(ref failures, "configHash：格式", a.ComputeConfigHash().StartsWith("sha256:"));
        }

        // ---------- 完整批次 ----------

        static void RunFullBatch(ref int failures)
        {
            string root = NewRoot();
            try
            {
                PathPlan plan = PathPlan.CreateFull("plan-v1", 1);
                DetectionConfig config = BuildConfig(plan, 1);
                DetectionStore store = new DetectionStore(root);
                string batchId = DetectionNaming.BatchId(DateTime.UtcNow, 1);
                GroupPipeline pipeline = new GroupPipeline(new GroupStore(Path.Combine(root, batchId)));
                var producer = new FakeGroupProducer();

                var runner = new BatchRunner(store, pipeline, producer, config, null, null);
                BatchRunResult result = runner.Run(MakeRequest(batchId, plan, null));

                Check(ref failures, "批次：完成态", result.State == BatchState.Completed, result.State.ToString());
                Check(ref failures, "批次：64 条全部 Computed", result.Batch.summary.completed == 64,
                    result.Batch.summary.completed.ToString());
                Check(ref failures, "批次：detection.json 存在",
                    File.Exists(Path.Combine(store.BatchDirectory(batchId), "detection.json")));
                Check(ref failures, "批次：index.csv 存在",
                    File.Exists(Path.Combine(store.BatchDirectory(batchId), "index.csv")));

                DetectionBatch reload = store.Read(batchId);
                Check(ref failures, "批次：回读路径数", reload.paths.Length == 64);
                Check(ref failures, "批次：回读状态", reload.batchState == BatchStateNames.Completed, reload.batchState);
                Check(ref failures, "批次：回读汇总", reload.summary.completed == 64);

                string[] groupDirs = Directory.GetDirectories(store.BatchDirectory(batchId), "GR-*");
                Check(ref failures, "批次：64 个组目录", groupDirs.Length == 64, groupDirs.Length.ToString());
                Check(ref failures, "批次：group.json 已写", File.Exists(Path.Combine(groupDirs[0], "group.json")));
                Check(ref failures, "批次：提交后无 .ready",
                    Directory.GetFiles(store.BatchDirectory(batchId), ".ready", SearchOption.AllDirectories).Length == 0);

                PathResult first = reload.paths[0];
                Check(ref failures, "路径：groupId 已填", !string.IsNullOrEmpty(first.groupId));
                Check(ref failures, "路径：connectivity ok", first.connectivity == ConnectivityNames.Ok, first.connectivity);
                Check(ref failures, "路径：IL 来自 summary", first.ilDb.HasValue && Math.Abs(first.ilDb.Value + 1.25) < 1e-9,
                    first.ilDb.HasValue ? first.ilDb.Value.ToString() : "null");
                Check(ref failures, "路径：峰证据", first.evidence != null && first.evidence.peakPresent == true);
                Check(ref failures, "路径：无基线时仅 baseline_missing",
                    first.flags.Length == 1 && first.flags[0] == "baseline_missing",
                    string.Join(",", first.flags));
            }
            finally
            {
                DeleteRoot(root);
            }
        }

        static void RunFailureInjection(ref int failures)
        {
            string root = NewRoot();
            try
            {
                PathPlan plan = PathPlan.CreateFull("plan-v1", 1);
                DetectionConfig config = BuildConfig(plan, 1);
                DetectionStore store = new DetectionStore(root);
                string batchId = DetectionNaming.BatchId(DateTime.UtcNow, 1);
                GroupPipeline pipeline = new GroupPipeline(new GroupStore(Path.Combine(root, batchId)));
                var producer = new FakeGroupProducer();
                producer.FailPaths.Add("IN02_OUT03");

                var runner = new BatchRunner(store, pipeline, producer, config, null, null);
                BatchRunResult result = runner.Run(MakeRequest(batchId, plan, null));

                Check(ref failures, "失败注入：批次态", result.State == BatchState.CompletedWithFailures,
                    result.State.ToString());
                Check(ref failures, "失败注入：1 条 Failed", result.Batch.summary.measurementFailed == 1);
                Check(ref failures, "失败注入：其余仍完成", result.Batch.summary.completed == 63);
            }
            finally
            {
                DeleteRoot(root);
            }
        }

        // ---------- 中止与恢复 ----------

        static void RunAbortAndResume(ref int failures)
        {
            string root = NewRoot();
            try
            {
                PathPlan plan = PathPlan.CreateFull("plan-v1", 1);
                DetectionConfig config = BuildConfig(plan, 1);
                DetectionStore store = new DetectionStore(root);
                string batchId = DetectionNaming.BatchId(DateTime.UtcNow, 1);
                GroupPipeline pipeline = new GroupPipeline(new GroupStore(Path.Combine(root, batchId)));
                var producer = new FakeGroupProducer();

                var runner = new BatchRunner(store, pipeline, producer, config, null, null);
                int seen = 0;
                runner.PathCompleted += delegate (PathResult path)
                {
                    seen++;
                    if (seen == 4) runner.Abort();
                };

                BatchRunResult aborted = runner.Run(MakeRequest(batchId, plan, null));

                Check(ref failures, "中止：批次态 Aborted", aborted.State == BatchState.Aborted, aborted.State.ToString());
                Check(ref failures, "中止：已完成 4 条", aborted.Batch.summary.completed == 4,
                    aborted.Batch.summary.completed.ToString());
                Check(ref failures, "中止：未执行计数闭合",
                    aborted.Batch.summary.completed + aborted.Batch.summary.notAttempted == 64);

                // 模拟异常退出：把中止现场改成"运行中 + 未执行"
                DetectionBatch onDisk = store.Read(batchId);
                onDisk.batchState = BatchStateNames.Running;
                for (int k = 0; k < onDisk.paths.Length; k++)
                {
                    if (onDisk.paths[k].pathState == PathStateNames.NotAttempted)
                        onDisk.paths[k].pathState = PathStateNames.Pending;
                }
                store.Write(onDisk);

                var recovery = new RecoveryService(store);
                RecoveryPlan planInspect = recovery.Inspect(batchId);
                Check(ref failures, "恢复：可续跑", planInspect.Resumable, planInspect.BatchState);
                Check(ref failures, "恢复：待执行 60 条", planInspect.PendingPathIds.Length == 60,
                    planInspect.PendingPathIds.Length.ToString());

                Check(ref failures, "恢复：无残留 staging", recovery.DiscardIncompleteStaging(batchId) == 0);

                var runner2 = new BatchRunner(store, pipeline, new FakeGroupProducer(), config, null, null);
                BatchRunResult resumed = runner2.Resume(batchId, MakeRequest(batchId, plan, null));

                Check(ref failures, "恢复：续跑完成", resumed.State == BatchState.Completed, resumed.State.ToString());
                Check(ref failures, "恢复：64 条完成", resumed.Batch.summary.completed == 64,
                    resumed.Batch.summary.completed.ToString());
                Check(ref failures, "恢复：attempt 递增", resumed.Batch.paths[10].attempt == 2,
                    resumed.Batch.paths[10].attempt.ToString());
                Check(ref failures, "恢复：已完成的 attempt 保持", resumed.Batch.paths[0].attempt == 1,
                    resumed.Batch.paths[0].attempt.ToString());
            }
            finally
            {
                DeleteRoot(root);
            }
        }

        // ---------- N>1 平均 ----------

        static void RunAveragePath(ref int failures)
        {
            string root = NewRoot();
            try
            {
                PathPlan full = PathPlan.CreateFull("plan-v1", 3);
                var subset = new PathPlan { planVersion = "plan-v1", entries = new[] { full.entries[0] } };
                DetectionConfig config = BuildConfig(subset, 1);
                DetectionStore store = new DetectionStore(root);
                string batchId = DetectionNaming.BatchId(DateTime.UtcNow, 1);
                GroupPipeline pipeline = new GroupPipeline(new GroupStore(Path.Combine(root, batchId)));
                var producer = new FakeGroupProducer();

                var runner = new BatchRunner(store, pipeline, producer, config, null, null);
                BatchRunResult result = runner.Run(MakeRequest(batchId, subset, "BATCH-20261111-000000-001"));

                PathResult path = result.Batch.paths[0];
                Check(ref failures, "平均：validCount=3", path.validCount == 3, path.validCount.ToString());
                Check(ref failures, "平均：avgScanId 已填", !string.IsNullOrEmpty(path.avgScanId), path.avgScanId);
                Check(ref failures, "平均：metricSource 指向 AVG", path.metricSource == "AVG/summary.json", path.metricSource);
                Check(ref failures, "平均：IL 取 AVG 汇总", path.ilDb.HasValue && Math.Abs(path.ilDb.Value + 1.20) < 1e-9,
                    path.ilDb.HasValue ? path.ilDb.Value.ToString() : "null");
                Check(ref failures, "平均：无 avg_incomplete",
                    !Array.Exists(path.flags, delegate (string f) { return f == "avg_incomplete"; }),
                    string.Join(",", path.flags));
            }
            finally
            {
                DeleteRoot(root);
            }
        }

        // ---------- 辅助 ----------

        static DetectionConfig BuildConfig(PathPlan plan, int settleMs)
        {
            return new DetectionConfig
            {
                plan = plan,
                settleMs = settleMs,
                dataRoot = "DETECTION_DATA",
                ofdr = new OfdrScanConfig { endpoint = "192.168.2.26:4300", range = "15.2m", resolution = "185um", average = 1, timeoutMs = 2000 },
                ocs1x8 = new OcsPortConfig { port = "COM4", baud = 9600, handshake = "none", timeoutMs = 300, retry = 2, backoffMs = new[] { 100, 300 } },
                ocs8x8 = new OcsPortConfig { port = "COM3", baud = 115200, handshake = "none", timeoutMs = 300, matrixTimeoutMs = 500, retry = 2, backoffMs = new[] { 100, 300 } },
                retention = new RetentionConfig { mode = "days", days = 30 }
            };
        }

        static BatchRunRequest MakeRequest(string batchId, PathPlan plan, string retryOf)
        {
            return new BatchRunRequest
            {
                batchId = batchId,
                operatorName = "tester",
                station = "ST-01",
                deviceInfo = new BatchDeviceInfo { ofdrSn = "LGA50-0001", ocs1x8Sn = "26081501", ocs8x8Sn = "26081502" },
                calibration = CalibrationStatus.Calibrated,
                hasBaseline = false,
                calibrationId = "CAL-TEST",
                ofdrConfigHash = "sha256:test",
                retryOf = retryOf,
                plan = plan
            };
        }

        static string NewRoot()
        {
            string root = Path.Combine(Path.GetTempPath(), "detection_test_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            return root;
        }

        static void DeleteRoot(string root)
        {
            try
            {
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
            catch (IOException)
            {
                // 测试收尾失败不影响结论
            }
        }

        static bool Contains(AttentionFlag[] flags, AttentionFlag flag)
        {
            return Array.IndexOf(flags, flag) >= 0;
        }

        static void Check(ref int failures, string name, bool ok, string detail = null)
        {
            if (ok)
            {
                Console.WriteLine("PASS " + name);
                return;
            }

            failures++;
            Console.WriteLine("FAIL " + name + (string.IsNullOrEmpty(detail) ? "" : " — " + detail));
        }
    }
}
