using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Gmix.Commit;
using Gmix.Data;

namespace Gmix.App.Orchestration
{
    public sealed class ScanRequest
    {
        public int Repeat = 1;
        public double StartM = 0.05;
        public double EndM = 20.0;
        public string RlMode = "peak_only";
        public string CalibrationPath;
        public bool Synthetic;
    }

    public sealed class AcquisitionOutcome
    {
        public string GroupId;
        public bool Success;
        public bool Cancelled;
        public bool TimedOut;
        public int ExitCode;
        public string StagingDir;
        public string LastScanDir;
        public int ReadyCount;
        public string Message;
    }

    public sealed class CommitOutcome
    {
        public int ExitCode;
        public string GroupDir;
        public string Status;
        public string Message;

        public bool Success { get { return ExitCode == 0; } }
    }

    /// <summary>
    /// 编排（SDD §3）：启动 Gmix.Acquirer 子进程写 staging → 轮询 `.ready` → 调用提交流水线。
    /// UI 线程不参与任何 IO 与厂商调用。
    /// </summary>
    public sealed class ScanRunner
    {
        const int ReadyPollMs = 500;
        const int PerScanIdleTimeoutMs = 30000;
        const int StopGraceMs = 5000;

        readonly AppEnvironment _env;

        public event Action<string> Log;

        public ScanRunner(AppEnvironment env)
        {
            _env = env;
        }

        public Task<AcquisitionOutcome> AcquireAsync(ScanRequest request, CancellationToken token)
        {
            return Task.Run(() => Acquire(request, token));
        }

        AcquisitionOutcome Acquire(ScanRequest request, CancellationToken token)
        {
            var outcome = new AcquisitionOutcome();
            outcome.GroupId = GroupIds.Next(_env.DataRoot);
            outcome.StagingDir = Path.Combine(_env.DataRoot, GroupStore.StagingFolderName, outcome.GroupId);

            if (string.IsNullOrEmpty(_env.AcquirerExe) || !File.Exists(_env.AcquirerExe))
            {
                outcome.Message = "找不到 Gmix.Acquirer.exe，无法采集";
                return outcome;
            }

            var args = new List<string>();
            args.Add("--groupId " + outcome.GroupId);
            args.Add("--repeat " + request.Repeat.ToString(CultureInfo.InvariantCulture));
            args.Add("--root \"" + _env.DataRoot + "\"");
            if (!string.IsNullOrEmpty(_env.RuntimeDir)) args.Add("--runtime \"" + _env.RuntimeDir + "\"");
            args.Add("--start " + request.StartM.ToString(CultureInfo.InvariantCulture));
            args.Add("--end " + request.EndM.ToString(CultureInfo.InvariantCulture));
            args.Add("--rl-mode " + request.RlMode);
            if (!string.IsNullOrEmpty(request.CalibrationPath))
                args.Add("--calibration \"" + request.CalibrationPath + "\"");
            if (request.Synthetic) args.Add("--synthetic");

            var psi = new ProcessStartInfo(_env.AcquirerExe, string.Join(" ", args.ToArray()));
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.StandardOutputEncoding = Encoding.UTF8;
            psi.StandardErrorEncoding = Encoding.UTF8;
            psi.WorkingDirectory = Path.GetDirectoryName(_env.AcquirerExe);

            Emit("开始采集 " + outcome.GroupId + "（N=" + request.Repeat + "）");

            using (var process = new Process())
            {
                process.StartInfo = psi;
                process.OutputDataReceived += delegate (object s, DataReceivedEventArgs e) { if (e.Data != null) Emit(e.Data); };
                process.ErrorDataReceived += delegate (object s, DataReceivedEventArgs e) { if (e.Data != null) Emit("错误: " + e.Data); };
                try
                {
                    process.Start();
                }
                catch (Exception ex)
                {
                    outcome.Message = "启动采集进程失败：" + ex.Message;
                    return outcome;
                }
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                var clock = Stopwatch.StartNew();
                int lastReady = -1;
                long lastChangeMs = 0;

                while (!process.HasExited)
                {
                    if (token.IsCancellationRequested)
                    {
                        outcome.Cancelled = true;
                        break;
                    }
                    Sleep(ReadyPollMs);
                    int ready = CountReady(outcome.StagingDir);
                    if (ready != lastReady)
                    {
                        lastReady = ready;
                        lastChangeMs = clock.ElapsedMilliseconds;
                        if (ready > 0) Emit("已就绪 " + ready + "/" + request.Repeat);
                    }
                    if (clock.ElapsedMilliseconds - lastChangeMs > PerScanIdleTimeoutMs)
                    {
                        outcome.TimedOut = true;
                        break;
                    }
                }

                if (outcome.Cancelled || outcome.TimedOut)
                {
                    StopProcess(process);
                }
                else
                {
                    process.WaitForExit();
                }

                outcome.ExitCode = process.HasExited ? process.ExitCode : -1;
                outcome.ReadyCount = CountReady(outcome.StagingDir);
                outcome.LastScanDir = LastReadyScanDir(outcome.StagingDir);
            }

            if (outcome.Cancelled)
            {
                outcome.Message = "已停止，残留 " + outcome.ReadyCount + " 次扫描在 staging";
                Emit(outcome.Message);
                return outcome;
            }
            if (outcome.TimedOut)
            {
                outcome.Message = "采集超时（" + PerScanIdleTimeoutMs / 1000 + " s 无新扫描），已终止进程";
                Emit(outcome.Message);
                return outcome;
            }
            if (outcome.ExitCode != 0)
            {
                outcome.Message = "采集进程退出码 " + outcome.ExitCode;
                Emit(outcome.Message);
                return outcome;
            }

            outcome.Success = outcome.ReadyCount > 0;
            if (!outcome.Success) outcome.Message = "采集完成但没有可用扫描";
            return outcome;
        }

        public Task<CommitOutcome> CommitAsync(string groupId, string calibrationPath)
        {
            return Task.Run(() => Commit(groupId, calibrationPath));
        }

        CommitOutcome Commit(string groupId, string calibrationPath)
        {
            var outcome = new CommitOutcome();
            Emit("提交组 " + groupId);
            int code = GroupCommitPipeline.Run(_env.DataRoot, groupId, _env.RuntimeDir, calibrationPath,
                Emit, delegate (string message) { Emit("错误: " + message); });
            outcome.ExitCode = code;
            outcome.GroupDir = Path.Combine(_env.DataRoot, groupId);

            string groupJson = Path.Combine(outcome.GroupDir, "group.json");
            if (File.Exists(groupJson))
            {
                GroupDocument doc = JsonStore.Read<GroupDocument>(groupJson);
                outcome.Status = doc.status;
                outcome.Message = "组 " + groupId + " 状态 " + doc.status + "（" + doc.validCount + "/" + doc.averageCount + "）";
            }
            else
            {
                string failed = Path.Combine(_env.DataRoot, GroupStore.StagingFolderName, groupId + GroupStore.FailedSuffix);
                if (Directory.Exists(failed)) outcome.GroupDir = failed;
                outcome.Status = "failed";
                outcome.Message = "组 " + groupId + " 提交失败（退出码 " + code + "）";
            }
            Emit(outcome.Message);
            return outcome;
        }

        static int CountReady(string stagingDir)
        {
            if (!Directory.Exists(stagingDir)) return 0;
            int count = 0;
            foreach (string dir in Directory.GetDirectories(stagingDir))
            {
                if (File.Exists(Path.Combine(dir, GroupStore.ReadyFileName))) count++;
            }
            return count;
        }

        static string LastReadyScanDir(string stagingDir)
        {
            if (!Directory.Exists(stagingDir)) return null;
            string last = null;
            foreach (string dir in Directory.GetDirectories(stagingDir))
            {
                if (File.Exists(Path.Combine(dir, GroupStore.ReadyFileName))) last = dir;
            }
            return last;
        }

        static void StopProcess(Process process)
        {
            try
            {
                if (!process.HasExited) process.WaitForExit(StopGraceMs);
            }
            catch (Exception)
            {
                // 进程已退出或句柄失效，下面统一走强杀判断
            }
            try
            {
                if (!process.HasExited) process.Kill(true);
            }
            catch (Exception)
            {
                // 强杀失败不改变编排结论（staging 由下次启动清理）
            }
        }

        static void Sleep(int milliseconds)
        {
            try
            {
                Thread.Sleep(milliseconds);
            }
            catch (ThreadInterruptedException)
            {
                // 线程中断即视为退出轮询，由调用方检查取消标志
            }
        }

        void Emit(string message)
        {
            Action<string> handler = Log;
            if (handler != null) handler(message);
        }
    }
}
