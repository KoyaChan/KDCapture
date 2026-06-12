using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;

namespace KDCapture
{
    internal static class Program
    {
        private static readonly object LogLock = new object();
        private static readonly object KdNoiseLock = new object();
        private static StreamWriter LogWriter;
        private static int ValidateSequenceSuppressedCount;
        private static DateTime ValidateSequenceLastSummaryUtc = DateTime.MinValue;

        private static int Main(string[] args)
        {
            try
            {
                var options = ParseArgs(args);

                if (options.ShowHelp)
                {
                    PrintUsage();
                    return 0;
                }

                if (string.IsNullOrWhiteSpace(options.Key) || options.Port <= 0 || string.IsNullOrWhiteSpace(options.OutputPath))
                {
                    Error("Missing required arguments.");
                    PrintUsage();
                    return 2;
                }

                if (options.CaptureTimeoutSec <= 0)
                {
                    Error("capture timeout must be greater than 0.");
                    return 2;
                }

                if (options.NoDebuggeeTimeoutSec <= 0)
                {
                    Error("no_debuggee timeout must be greater than 0.");
                    return 2;
                }

                if (options.DelayBeforeDumpSec < 0)
                {
                    Error("delay-before-dump-sec must be 0 or greater.");
                    return 2;
                }

                if (options.LoopMode && options.IntervalSec <= 0)
                {
                    Error("interval must be greater than 0 in loop mode.");
                    return 2;
                }

                var outputFullPath = Path.GetFullPath(options.OutputPath);
                var outputDir = Path.GetDirectoryName(outputFullPath);
                if (!string.IsNullOrEmpty(outputDir))
                {
                    Directory.CreateDirectory(outputDir);
                }

                InitializeLogger(options, outputFullPath);

                if (string.IsNullOrWhiteSpace(options.TargetIp))
                {
                    Info("Target IP is not specified. If kd stays in [no_debuggee], specify --targetip <WinPE_IP>.");
                }

                var tempRoot = Path.Combine(Path.GetTempPath(), "KDCapture", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tempRoot);

                try
                {
                    ExtractRequiredBinaries(tempRoot);

                    var kdPath = Path.Combine(tempRoot, "kd.exe");
                    if (!File.Exists(kdPath))
                    {
                        throw new FileNotFoundException("kd.exe was not found in embedded resources.");
                    }

                    if (options.LoopMode || options.DelayBeforeDumpSec > 0)
                    {
                        // When delay is specified, use loop mode.
                        // kd.exe connects immediately, then delays BEFORE sending dump command.
                        var delayedOptions = options;
                        if (options.DelayBeforeDumpSec > 0 && !options.LoopMode)
                        {
                            Info("Delayed capture requested. Switching to loop mode.");
                            Info("KDCapture will wait first, then start kd.exe and capture a dump.");
                            Info("Press Ctrl+C to stop once dump is captured, or wait for auto-success exit.");
                            delayedOptions = new Options
                            {
                                Key = options.Key,
                                Port = options.Port,
                                TargetIp = options.TargetIp,
                                OutputPath = options.OutputPath,
                                CaptureTimeoutSec = options.CaptureTimeoutSec,
                                NoDebuggeeTimeoutSec = options.NoDebuggeeTimeoutSec,
                                DelayBeforeDumpSec = options.DelayBeforeDumpSec,
                                LoopMode = true,
                                IntervalSec = 5,  // Retry every 5 seconds if first attempt fails
                                LogPath = options.LogPath,
                                ShowHelp = false
                            };
                        }
                        return RunLoopMode(delayedOptions, kdPath, outputFullPath);
                    }

                    var singleResult = ExecuteSingleCapture(
                        kdPath,
                        BuildKdTarget(options),
                        outputFullPath,
                        options.CaptureTimeoutSec,
                        options.NoDebuggeeTimeoutSec,
                        0);

                    if (singleResult != 0)
                    {
                        return singleResult;
                    }

                    Info("Dump created successfully: " + outputFullPath);
                    return 0;
                }
                finally
                {
                    TryDeleteDirectory(tempRoot);
                }
            }
            catch (Exception ex)
            {
                Error("KDCapture failed: " + ex.Message);
                return 1;
            }
            finally
            {
                CloseLogger();
            }
        }

        private static int RunLoopMode(Options options, string kdPath, string finalOutputPath)
        {
            var kdTarget = BuildKdTarget(options);
            var stableDumpPath = finalOutputPath;
            var gen1DumpPath = finalOutputPath + ".1";
            var gen2DumpPath = finalOutputPath + ".2";
            var workDumpPath = finalOutputPath + ".work";
            var successCount = 0;
            var autoExitOnFirstSuccess = options.DelayBeforeDumpSec > 0;

            Info("Loop mode enabled.");
            if (autoExitOnFirstSuccess)
            {
                Info("Delayed capture: will auto-exit on first successful dump.");
            }
            Info("Target           : " + kdTarget);
            Info("Final dump path  : " + stableDumpPath);
            Info("Retention         : 3 generations (.dmp, .dmp.1, .dmp.2)");
            Info("Interval(sec)    : " + options.IntervalSec);
            Info("Timeout(sec)     : " + options.CaptureTimeoutSec);
            Info("NoDebuggeeTimeout(sec): " + options.NoDebuggeeTimeoutSec);
            Info("Press Ctrl+C to stop loop manually.");

            while (true)
            {
                if (File.Exists(workDumpPath))
                {
                    File.Delete(workDumpPath);
                }

                Info("Starting periodic capture attempt...");

                // Pass delay to ExecuteSingleCapture - it will apply it before starting kd
                var result = ExecuteSingleCapture(
                    kdPath,
                    kdTarget,
                    workDumpPath,
                    options.CaptureTimeoutSec,
                    options.NoDebuggeeTimeoutSec,
                    options.DelayBeforeDumpSec,
                    quietSuccessMessage: true);

                if (result != 0)
                {
                    Error("Capture attempt failed. Assuming target is no longer debuggable over KDNET.");
                    if (successCount > 0 && File.Exists(stableDumpPath))
                    {
                        Info("Keeping last successful dump: " + stableDumpPath);
                        if (File.Exists(gen1DumpPath))
                        {
                            Info("Previous generation #1     : " + gen1DumpPath);
                        }

                        if (File.Exists(gen2DumpPath))
                        {
                            Info("Previous generation #2     : " + gen2DumpPath);
                        }

                        return 0;
                    }

                    Error("No successful dump has been captured yet.");
                    return result;
                }

                RotateRetention(stableDumpPath, gen1DumpPath, gen2DumpPath);
                ReplaceFile(workDumpPath, stableDumpPath);
                successCount++;
                Info("Periodic dump updated: " + stableDumpPath + " (count=" + successCount + ")");

                if (autoExitOnFirstSuccess)
                {
                    Info("Auto-exiting on first successful capture.");
                    return 0;
                }

                System.Threading.Thread.Sleep(TimeSpan.FromSeconds(options.IntervalSec));
            }
        }

        private static int ExecuteSingleCapture(
            string kdPath,
            string kdTarget,
            string dumpPath,
            int captureTimeoutSec,
            int noDebuggeeTimeoutSec,
            int delayBeforeDumpSec,
            bool quietSuccessMessage = false)
        {
            ResetKdNoiseSummaryState();
            
            // Capture dump and then quit+detach gracefully so next KDCapture run can reconnect cleanly.
            var kdCommand = ".dump /k \"" + dumpPath + "\";qd";
            var kdArgs = "-b -k " + kdTarget + " -c \"" + kdCommand + "\"";
            long noDebuggeeSinceTicksUtc = 0;
            bool connectionEstablished = false;

            if (delayBeforeDumpSec > 0)
            {
                Info("Delayed dump mode: waiting " + delayBeforeDumpSec + " sec before starting kd.exe...");
                var waitDeadline = DateTime.UtcNow.AddSeconds(delayBeforeDumpSec);
                while (DateTime.UtcNow < waitDeadline)
                {
                    var remaining = (int)(waitDeadline - DateTime.UtcNow).TotalSeconds;
                    if (remaining % 60 == 0 && remaining > 0)
                    {
                        Info("Waiting before dump start... " + remaining + " sec remaining.");
                    }
                    Thread.Sleep(1000);
                }
                Info("Delay elapsed. Starting kd.exe now.");
            }

            Info("Starting kd.exe...");
            Info("Target: " + kdTarget);
            Info("Dump  : " + dumpPath);

            var startInfo = new ProcessStartInfo
            {
                FileName = kdPath,
                Arguments = kdArgs,
                WorkingDirectory = Path.GetDirectoryName(kdPath) ?? Environment.CurrentDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using (var process = new Process { StartInfo = startInfo })
            {
                process.OutputDataReceived += (_, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                    {
                        if (TrySummarizeKdNoiseLine(e.Data))
                        {
                            return;
                        }

                        Info(e.Data);

                        if (e.Data.IndexOf("Kernel Debugger connection established", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            connectionEstablished = true;
                            Interlocked.Exchange(ref noDebuggeeSinceTicksUtc, 0);
                        }

                        if (connectionEstablished && e.Data.IndexOf("Kernel Debug Target Status: [no_debuggee]", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            Interlocked.CompareExchange(ref noDebuggeeSinceTicksUtc, DateTime.UtcNow.Ticks, 0);
                        }
                    }
                };

                process.ErrorDataReceived += (_, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                    {
                        Error(e.Data);
                    }
                };

                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                var captureDeadlineUtc = DateTime.UtcNow.AddSeconds(captureTimeoutSec);
                long lastDumpBytes = -1;

                while (true)
                {
                    if (process.WaitForExit(500))
                    {
                        break;
                    }

                    if (File.Exists(dumpPath))
                    {
                        long currentDumpBytes;
                        try
                        {
                            currentDumpBytes = new FileInfo(dumpPath).Length;
                        }
                        catch
                        {
                            currentDumpBytes = lastDumpBytes;
                        }

                        if (currentDumpBytes > lastDumpBytes)
                        {
                            // Keep waiting while dump file is actively growing.
                            lastDumpBytes = currentDumpBytes;
                            captureDeadlineUtc = DateTime.UtcNow.AddSeconds(captureTimeoutSec);
                        }
                    }

                    var noDebuggeeTicks = Interlocked.Read(ref noDebuggeeSinceTicksUtc);
                    if (noDebuggeeTicks != 0)
                    {
                        var elapsedNoDebuggeeSec = (DateTime.UtcNow - new DateTime(noDebuggeeTicks, DateTimeKind.Utc)).TotalSeconds;
                        if (elapsedNoDebuggeeSec >= noDebuggeeTimeoutSec)
                        {
                            Error("kd.exe stayed in [no_debuggee] for " + noDebuggeeTimeoutSec + " seconds. Aborting early.");
                            Error("Tip: try adding --targetip <WinPE_IP> so kd uses net:...target=<WinPE_IP>.");
                            FlushKdNoiseSummary();
                            TryKillProcess(process);
                            return 5;
                        }
                    }

                    if (DateTime.UtcNow >= captureDeadlineUtc)
                    {
                        Error("kd.exe timed out waiting for connection or dump completion.");
                        if (lastDumpBytes >= 0)
                        {
                            Error("Last observed dump size(bytes): " + lastDumpBytes + ". Timeout means dump growth stalled.");
                        }
                        FlushKdNoiseSummary();
                        TryKillProcess(process);
                        return 4;
                    }
                }

                if (process.ExitCode != 0)
                {
                    FlushKdNoiseSummary();
                    Error("kd.exe exited with code: " + process.ExitCode);
                    return process.ExitCode;
                }
            }

            if (!File.Exists(dumpPath))
            {
                FlushKdNoiseSummary();
                Error("kd.exe finished, but dump file was not created.");
                return 3;
            }

            FlushKdNoiseSummary();
            if (!quietSuccessMessage)
            {
                Info("Dump created successfully: " + dumpPath);
            }

            return 0;
        }

        private static bool TrySummarizeKdNoiseLine(string line)
        {
            if (line.IndexOf("ValidateSequenceNumber:", StringComparison.OrdinalIgnoreCase) < 0)
            {
                return false;
            }

            string summaryToWrite = null;
            lock (KdNoiseLock)
            {
                ValidateSequenceSuppressedCount++;
                var nowUtc = DateTime.UtcNow;

                if (ValidateSequenceLastSummaryUtc == DateTime.MinValue)
                {
                    ValidateSequenceLastSummaryUtc = nowUtc;
                    summaryToWrite = "KDNET sequence warnings detected. Repeated ValidateSequenceNumber lines are being summarized.";
                }
                else if ((nowUtc - ValidateSequenceLastSummaryUtc).TotalSeconds >= 15)
                {
                    summaryToWrite = "ValidateSequenceNumber repeated " + ValidateSequenceSuppressedCount + " times in the last interval (suppressed).";
                    ValidateSequenceSuppressedCount = 0;
                    ValidateSequenceLastSummaryUtc = nowUtc;
                }
            }

            if (!string.IsNullOrEmpty(summaryToWrite))
            {
                Info(summaryToWrite);
            }

            return true;
        }

        private static void FlushKdNoiseSummary()
        {
            string summaryToWrite = null;
            lock (KdNoiseLock)
            {
                if (ValidateSequenceSuppressedCount > 0)
                {
                    summaryToWrite = "ValidateSequenceNumber total suppressed lines in this attempt: " + ValidateSequenceSuppressedCount + ".";
                }

                ValidateSequenceSuppressedCount = 0;
                ValidateSequenceLastSummaryUtc = DateTime.MinValue;
            }

            if (!string.IsNullOrEmpty(summaryToWrite))
            {
                Info(summaryToWrite);
            }
        }

        private static void ResetKdNoiseSummaryState()
        {
            lock (KdNoiseLock)
            {
                ValidateSequenceSuppressedCount = 0;
                ValidateSequenceLastSummaryUtc = DateTime.MinValue;
            }
        }

        private static void TryKillProcess(Process process)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill();
                    process.WaitForExit(3000);
                }
            }
            catch
            {
                // Ignore cleanup failures.
            }
        }

        private static string BuildKdTarget(Options options)
        {
            var kdTarget = "net:port=" + options.Port + ",key=" + options.Key;
            if (!string.IsNullOrWhiteSpace(options.TargetIp))
            {
                kdTarget += ",target=" + options.TargetIp;
            }

            return kdTarget;
        }

        private static void ReplaceFile(string sourcePath, string destinationPath)
        {
            if (File.Exists(destinationPath))
            {
                File.Delete(destinationPath);
            }

            File.Move(sourcePath, destinationPath);
        }

        private static void RotateRetention(string latestPath, string gen1Path, string gen2Path)
        {
            if (File.Exists(gen2Path))
            {
                File.Delete(gen2Path);
            }

            if (File.Exists(gen1Path))
            {
                File.Move(gen1Path, gen2Path);
            }

            if (File.Exists(latestPath))
            {
                File.Move(latestPath, gen1Path);
            }
        }

        private static void InitializeLogger(Options options, string outputFullPath)
        {
            try
            {
                var logPath = string.IsNullOrWhiteSpace(options.LogPath)
                    ? outputFullPath + ".log"
                    : Path.GetFullPath(options.LogPath);

                var logDir = Path.GetDirectoryName(logPath);
                if (!string.IsNullOrEmpty(logDir))
                {
                    Directory.CreateDirectory(logDir);
                }

                LogWriter = new StreamWriter(new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                {
                    AutoFlush = true
                };

                Info("Log file: " + logPath);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Failed to initialize log file. Continuing without file logging: " + ex.Message);
                LogWriter = null;
            }
        }

        private static void CloseLogger()
        {
            lock (LogLock)
            {
                if (LogWriter == null)
                {
                    return;
                }

                try
                {
                    LogWriter.Dispose();
                }
                catch
                {
                    // Ignore close failures.
                }
                finally
                {
                    LogWriter = null;
                }
            }
        }

        private static void Info(string message)
        {
            WriteLog("INFO", message, false);
        }

        private static void Error(string message)
        {
            WriteLog("ERROR", message, true);
        }

        private static void WriteLog(string level, string message, bool isError)
        {
            var line = "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "] [" + level + "] " + message;

            if (isError)
            {
                Console.Error.WriteLine(line);
            }
            else
            {
                Console.WriteLine(line);
            }

            lock (LogLock)
            {
                if (LogWriter == null)
                {
                    return;
                }

                try
                {
                    LogWriter.WriteLine(line);
                }
                catch
                {
                    // Keep runtime behavior even when file logging fails.
                }
            }
        }

        private static void ExtractRequiredBinaries(string targetDir)
        {
            var required = new[]
            {
                "kd.exe",
                "dbgeng.dll",
                "dbghelp.dll",
                "dbgcore.dll",
                "EngHost.exe"
            };

            var asm = Assembly.GetExecutingAssembly();
            var resourceNames = asm.GetManifestResourceNames();

            foreach (var fileName in required)
            {
                var resourceName = resourceNames.FirstOrDefault(r =>
                    r.Equals(fileName, StringComparison.OrdinalIgnoreCase) ||
                    r.EndsWith("." + fileName, StringComparison.OrdinalIgnoreCase));

                if (string.IsNullOrEmpty(resourceName))
                {
                    continue;
                }

                var destinationPath = Path.Combine(targetDir, fileName);
                using (var src = asm.GetManifestResourceStream(resourceName))
                {
                    if (src == null)
                    {
                        continue;
                    }

                    using (var dst = File.Create(destinationPath))
                    {
                        src.CopyTo(dst);
                    }
                }
            }
        }

        private static void TryDeleteDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, true);
                }
            }
            catch
            {
                // Keep silent for cleanup failures.
            }
        }

        private static Options ParseArgs(string[] args)
        {
            var options = new Options();
            var map = new Dictionary<string, Action<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["--key"] = v => options.Key = v,
                ["--port"] = v => options.Port = int.TryParse(v, out var p) ? p : 0,
                ["--output"] = v => options.OutputPath = v,
                ["--hostip"] = v => options.TargetIp = v,
                ["--target"] = v => options.TargetIp = v,
                ["--targetip"] = v => options.TargetIp = v,
                ["--loop"] = _ => options.LoopMode = true,
                ["--interval-sec"] = v => options.IntervalSec = int.TryParse(v, out var s) ? s : 0,
                ["--capture-timeout-sec"] = v => options.CaptureTimeoutSec = int.TryParse(v, out var t) ? t : 0,
                ["--no-debuggee-timeout-sec"] = v => options.NoDebuggeeTimeoutSec = int.TryParse(v, out var t) ? t : 0,
                ["--delay-before-dump-sec"] = v => options.DelayBeforeDumpSec = int.TryParse(v, out var t) ? t : -1,
                ["--log"] = v => options.LogPath = v,
                ["-k"] = v => options.Key = v,
                ["-p"] = v => options.Port = int.TryParse(v, out var p) ? p : 0,
                ["-o"] = v => options.OutputPath = v,
                ["-t"] = v => options.TargetIp = v,
                ["-g"] = v => options.LogPath = v,
                ["-l"] = _ => options.LoopMode = true,
                ["-h"] = _ => options.ShowHelp = true,
                ["--help"] = _ => options.ShowHelp = true,
                ["/?"] = _ => options.ShowHelp = true
            };

            for (var i = 0; i < args.Length; i++)
            {
                var arg = args[i];
                if (!map.TryGetValue(arg, out var setter))
                {
                    continue;
                }

                if (arg.Equals("-h", StringComparison.OrdinalIgnoreCase) ||
                    arg.Equals("--help", StringComparison.OrdinalIgnoreCase) ||
                    arg.Equals("/?", StringComparison.OrdinalIgnoreCase))
                {
                    setter(string.Empty);
                    continue;
                }

                if (arg.Equals("--loop", StringComparison.OrdinalIgnoreCase) ||
                    arg.Equals("-l", StringComparison.OrdinalIgnoreCase))
                {
                    setter(string.Empty);
                    continue;
                }

                if (i + 1 >= args.Length)
                {
                    throw new ArgumentException("Missing value for argument: " + arg);
                }

                setter(args[++i]);
            }

            return options;
        }

        private static void PrintUsage()
        {
            Console.WriteLine("KDCapture usage:");
            Console.WriteLine("  single: KDCapture.exe --key <KDNET_KEY> --port <PORT> --output <DUMP_PATH> [--targetip <TARGET_IP>] [--delay-before-dump-sec 480] [--log <LOG_PATH>]");
            Console.WriteLine("  loop  : KDCapture.exe --key <KDNET_KEY> --port <PORT> --output <DUMP_PATH> --loop [--interval-sec 30] [--capture-timeout-sec 20] [--no-debuggee-timeout-sec 30] [--delay-before-dump-sec 480] [--targetip <TARGET_IP>] [--log <LOG_PATH>]");
            Console.WriteLine();
            Console.WriteLine("Notes:");
            Console.WriteLine("  - If --log is omitted, default log path is <output>.log");
            Console.WriteLine("  - --targetip is strongly recommended for KDNET stability (maps to net:...target=<TARGET_IP>)");
            Console.WriteLine("  - delay-before-dump-sec controls when KDCapture sends .breakin and starts the dump (0 = dump immediately after attach)");
            Console.WriteLine();
            Console.WriteLine("Example:");
            Console.WriteLine("  KDCapture.exe --key 1.2.3.4 --port 50000 --delay-before-dump-sec 480 --output C:\\dumps\\winpe_target.dmp");
            Console.WriteLine("  KDCapture.exe --key 1.2.3.4 --port 50000 --targetip 10.0.0.10 --output C:\\dumps\\target_latest.dmp --loop --interval-sec 30 --capture-timeout-sec 300 --no-debuggee-timeout-sec 30 --delay-before-dump-sec 480 --log C:\\dumps\\kdcapture.log");
        }

        private sealed class Options
        {
            public string Key { get; set; }
            public int Port { get; set; }
            public string OutputPath { get; set; }
            public string TargetIp { get; set; }
            public bool LoopMode { get; set; }
            public int IntervalSec { get; set; } = 30;
            public int CaptureTimeoutSec { get; set; } = 20;
            public int NoDebuggeeTimeoutSec { get; set; } = 30;
            public int DelayBeforeDumpSec { get; set; } = 480;
            public string LogPath { get; set; }
            public bool ShowHelp { get; set; }
        }
    }
}
