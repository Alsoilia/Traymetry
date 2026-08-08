using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace Traymetry
{
    internal enum FrameTelemetryStatus
    {
        WaitingForTarget,
        WaitingForFrames,
        Collecting,
        Ready,
        Stale
    }

    internal sealed class FrameTelemetrySnapshot
    {
        internal FrameTelemetryStatus Status { get; set; }
        internal int ProcessId { get; set; }
        internal string ProcessName { get; set; }
        internal ulong SwapChainAddress { get; set; }
        internal double DisplayedFps { get; set; }
        internal double PresentedFps { get; set; }
        internal double ApplicationFps { get; set; }
        internal double FrameTimeMs { get; set; }
        internal double FrameTimeP95Ms { get; set; }
        internal double OnePercentLowFps { get; set; }
        internal double DroppedPercent { get; set; }
        internal int SampleCount { get; set; }
        internal DateTime LastSampleUtc { get; set; }

        internal bool HasFps
        {
            get
            {
                return DisplayedFps > 0 || PresentedFps > 0 || ApplicationFps > 0;
            }
        }
    }

    /// <summary>
    /// A replaceable boundary between Traymetry and a frame telemetry engine.
    /// Implementations consume already-produced data and never elevate or launch
    /// a third-party process themselves.
    /// </summary>
    internal interface IFrameTelemetryProvider : IDisposable
    {
        bool TryConsumeLine(string line, DateTime receivedUtc);
        FrameTelemetrySnapshot GetSnapshot(int selectedProcessId, DateTime utcNow);
        FrameTelemetrySnapshot[] GetSnapshots(DateTime utcNow, int maximumCount);
        void Reset();
    }

    internal sealed class PresentMonStdoutTelemetryAdapter : IFrameTelemetryProvider
    {
        private const int MaximumTrackedProcesses = 32;
        private const int MaximumSamplesPerProcess = 4096;
        private static readonly TimeSpan ProcessRetention = TimeSpan.FromSeconds(75);
        private static readonly TimeSpan ReadySampleAge = TimeSpan.FromMilliseconds(2500);
        private static readonly TimeSpan InstantWindow = TimeSpan.FromMilliseconds(1750);
        private static readonly TimeSpan StatisticsWindow = TimeSpan.FromSeconds(30);

        private readonly object _sync = new object();
        private readonly PresentMonCsvFrameParser _parser = new PresentMonCsvFrameParser();
        private readonly Dictionary<int, ProcessFrameWindow> _processes =
            new Dictionary<int, ProcessFrameWindow>();
        private bool _disposed;

        public bool TryConsumeLine(string line, DateTime receivedUtc)
        {
            lock (_sync)
            {
                ThrowIfDisposed();
                PresentMonFrameSample sample;
                if (!_parser.TryParseLine(line, receivedUtc, out sample))
                    return false;

                RemoveExpiredProcesses(receivedUtc);
                ProcessFrameWindow window;
                if (!_processes.TryGetValue(sample.ProcessId, out window))
                {
                    if (_processes.Count >= MaximumTrackedProcesses)
                        RemoveOldestProcess();
                    window = new ProcessFrameWindow(sample.ProcessId);
                    _processes.Add(sample.ProcessId, window);
                }
                window.Add(sample, MaximumSamplesPerProcess, ProcessRetention);
                return true;
            }
        }

        public FrameTelemetrySnapshot GetSnapshot(int selectedProcessId, DateTime utcNow)
        {
            lock (_sync)
            {
                ThrowIfDisposed();
                RemoveExpiredProcesses(utcNow);

                if (selectedProcessId <= 0)
                {
                    return new FrameTelemetrySnapshot
                    {
                        Status = FrameTelemetryStatus.WaitingForTarget,
                        ProcessId = selectedProcessId,
                        ProcessName = String.Empty
                    };
                }

                ProcessFrameWindow process;
                if (!_processes.TryGetValue(selectedProcessId, out process))
                {
                    return new FrameTelemetrySnapshot
                    {
                        Status = FrameTelemetryStatus.WaitingForFrames,
                        ProcessId = selectedProcessId,
                        ProcessName = String.Empty
                    };
                }

                return BuildSnapshot(process, utcNow);
            }
        }

        public FrameTelemetrySnapshot[] GetSnapshots(DateTime utcNow, int maximumCount)
        {
            lock (_sync)
            {
                ThrowIfDisposed();
                RemoveExpiredProcesses(utcNow);
                int boundedCount = Math.Max(0, Math.Min(MaximumTrackedProcesses, maximumCount));
                if (boundedCount == 0)
                    return new FrameTelemetrySnapshot[0];

                return _processes.Values
                    .Select(delegate(ProcessFrameWindow process) { return BuildSnapshot(process, utcNow); })
                    .OrderByDescending(delegate(FrameTelemetrySnapshot snapshot)
                    {
                        return snapshot.Status == FrameTelemetryStatus.Ready ? 2 :
                            snapshot.Status == FrameTelemetryStatus.Collecting ? 1 : 0;
                    })
                    .ThenByDescending(delegate(FrameTelemetrySnapshot snapshot) { return snapshot.LastSampleUtc; })
                    .ThenByDescending(delegate(FrameTelemetrySnapshot snapshot) { return snapshot.SampleCount; })
                    .ThenBy(delegate(FrameTelemetrySnapshot snapshot) { return snapshot.ProcessId; })
                    .Take(boundedCount)
                    .ToArray();
            }
        }

        public void Reset()
        {
            lock (_sync)
            {
                if (_disposed)
                    return;
                _parser.Reset();
                _processes.Clear();
            }
        }

        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed)
                    return;
                _disposed = true;
                _parser.Reset();
                _processes.Clear();
            }
        }

        private FrameTelemetrySnapshot BuildSnapshot(ProcessFrameWindow process, DateTime utcNow)
        {
            PresentMonFrameSample[] all = process.Samples
                .Where(delegate(PresentMonFrameSample item)
                {
                    return utcNow - item.ReceivedUtc <= StatisticsWindow;
                })
                .ToArray();

            ulong selectedSwapChain = SelectSwapChain(all, utcNow);
            PresentMonFrameSample[] selected = all
                .Where(delegate(PresentMonFrameSample item)
                {
                    return item.SwapChainAddress == selectedSwapChain;
                })
                .ToArray();
            PresentMonFrameSample[] instant = selected
                .Where(delegate(PresentMonFrameSample item)
                {
                    return utcNow - item.ReceivedUtc <= InstantWindow;
                })
                .ToArray();

            FrameTelemetrySnapshot snapshot = new FrameTelemetrySnapshot
            {
                ProcessId = process.ProcessId,
                ProcessName = process.ProcessName,
                SwapChainAddress = selectedSwapChain,
                LastSampleUtc = process.LastReceivedUtc,
                SampleCount = selected.Length
            };

            if (utcNow - process.LastReceivedUtc > ReadySampleAge)
            {
                snapshot.Status = FrameTelemetryStatus.Stale;
                return snapshot;
            }

            snapshot.DisplayedFps = ResolveFps(instant,
                delegate(PresentMonFrameSample item) { return item.DisplayedFps; },
                delegate(PresentMonFrameSample item) { return item.DisplayIntervalMs; }, true);
            snapshot.PresentedFps = ResolveFps(instant,
                delegate(PresentMonFrameSample item) { return item.PresentedFps; },
                delegate(PresentMonFrameSample item) { return item.PresentIntervalMs; }, false);
            snapshot.ApplicationFps = ResolveFps(instant,
                delegate(PresentMonFrameSample item) { return item.ApplicationFps; },
                delegate(PresentMonFrameSample item) { return item.ApplicationIntervalMs; }, false);

            double[] instantFrameTimes = SelectFrameTimes(instant);
            snapshot.FrameTimeMs = Average(instantFrameTimes);

            double[] statisticsFrameTimes = SelectFrameTimes(selected);
            snapshot.FrameTimeP95Ms = Percentile(statisticsFrameTimes, 0.95);
            snapshot.OnePercentLowFps = OnePercentLow(statisticsFrameTimes);
            snapshot.DroppedPercent = selected.Length == 0
                ? 0
                : 100.0 * selected.Count(delegate(PresentMonFrameSample item) { return item.Dropped; }) /
                    selected.Length;

            snapshot.Status = snapshot.HasFps || snapshot.FrameTimeMs > 0
                ? FrameTelemetryStatus.Ready
                : FrameTelemetryStatus.Collecting;
            return snapshot;
        }

        private static ulong SelectSwapChain(PresentMonFrameSample[] samples, DateTime utcNow)
        {
            if (samples.Length == 0)
                return 0;

            return samples
                .GroupBy(delegate(PresentMonFrameSample item) { return item.SwapChainAddress; })
                .Select(delegate(IGrouping<ulong, PresentMonFrameSample> group)
                {
                    return new
                    {
                        Address = group.Key,
                        RecentCount = group.Count(delegate(PresentMonFrameSample item)
                        {
                            return utcNow - item.ReceivedUtc <= InstantWindow;
                        }),
                        LastSeen = group.Max(delegate(PresentMonFrameSample item) { return item.ReceivedUtc; })
                    };
                })
                .OrderByDescending(item => item.RecentCount)
                .ThenByDescending(item => item.LastSeen)
                .First().Address;
        }

        private static double ResolveFps(PresentMonFrameSample[] samples,
            Func<PresentMonFrameSample, double> directSelector,
            Func<PresentMonFrameSample, double> intervalSelector,
            bool excludeDropped)
        {
            double[] direct = samples
                .Where(delegate(PresentMonFrameSample item) { return !excludeDropped || !item.Dropped; })
                .Select(directSelector)
                .Where(IsPositiveFinite)
                .ToArray();
            if (direct.Length > 0)
                return Average(direct);

            double[] intervals = samples
                .Where(delegate(PresentMonFrameSample item) { return !excludeDropped || !item.Dropped; })
                .Select(intervalSelector)
                .Where(IsPositiveFinite)
                .ToArray();
            double average = Average(intervals);
            return average > 0 ? 1000.0 / average : 0;
        }

        private static double[] SelectFrameTimes(IEnumerable<PresentMonFrameSample> samples)
        {
            PresentMonFrameSample[] materialized = samples.ToArray();
            double[] displayed = materialized
                .Where(delegate(PresentMonFrameSample item) { return !item.Dropped; })
                .Select(delegate(PresentMonFrameSample item) { return item.DisplayIntervalMs; })
                .Where(IsPositiveFinite)
                .ToArray();
            double[] presented = materialized
                .Select(delegate(PresentMonFrameSample item) { return item.PresentIntervalMs; })
                .Where(IsPositiveFinite)
                .ToArray();

            // Display intervals are the preferred user-visible frame times, but
            // some APIs provide them only sporadically. Do not let one isolated
            // display sample replace a complete presented-frame history.
            int minimumDisplayCoverage = Math.Max(2, presented.Length / 2);
            if (displayed.Length >= minimumDisplayCoverage || presented.Length == 0)
                return displayed;
            return presented;
        }

        private static double OnePercentLow(double[] frameTimes)
        {
            if (frameTimes == null || frameTimes.Length < 100)
                return 0;
            double[] slowestFirst = frameTimes.OrderByDescending(value => value).ToArray();
            int count = Math.Max(1, (int)Math.Ceiling(slowestFirst.Length * 0.01));
            double averageSlowFrameTime = slowestFirst.Take(count).Average();
            return averageSlowFrameTime > 0 ? 1000.0 / averageSlowFrameTime : 0;
        }

        private static double Percentile(double[] values, double percentile)
        {
            if (values == null || values.Length < 2)
                return 0;
            double[] sorted = values.OrderBy(value => value).ToArray();
            double position = (sorted.Length - 1) * percentile;
            int lower = (int)Math.Floor(position);
            int upper = (int)Math.Ceiling(position);
            if (lower == upper)
                return sorted[lower];
            double weight = position - lower;
            return sorted[lower] + (sorted[upper] - sorted[lower]) * weight;
        }

        private static double Average(IEnumerable<double> values)
        {
            double total = 0;
            int count = 0;
            foreach (double value in values)
            {
                if (!IsPositiveFinite(value))
                    continue;
                total += value;
                count++;
            }
            return count == 0 ? 0 : total / count;
        }

        private static bool IsPositiveFinite(double value)
        {
            return value > 0 && !Double.IsNaN(value) && !Double.IsInfinity(value);
        }

        private void RemoveExpiredProcesses(DateTime utcNow)
        {
            int[] expired = _processes
                .Where(delegate(KeyValuePair<int, ProcessFrameWindow> item)
                {
                    return utcNow - item.Value.LastReceivedUtc > ProcessRetention;
                })
                .Select(item => item.Key)
                .ToArray();
            foreach (int processId in expired)
                _processes.Remove(processId);
        }

        private void RemoveOldestProcess()
        {
            if (_processes.Count == 0)
                return;
            int oldest = _processes
                .OrderBy(item => item.Value.LastReceivedUtc)
                .First().Key;
            _processes.Remove(oldest);
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(typeof(PresentMonStdoutTelemetryAdapter).Name);
        }

        private sealed class ProcessFrameWindow
        {
            internal readonly int ProcessId;
            internal readonly List<PresentMonFrameSample> Samples = new List<PresentMonFrameSample>();
            internal string ProcessName = String.Empty;
            internal DateTime LastReceivedUtc = DateTime.MinValue;

            internal ProcessFrameWindow(int processId)
            {
                ProcessId = processId;
            }

            internal void Add(PresentMonFrameSample sample, int maximumSamples, TimeSpan retention)
            {
                if (!String.IsNullOrWhiteSpace(sample.ProcessName))
                    ProcessName = sample.ProcessName;
                LastReceivedUtc = sample.ReceivedUtc;
                Samples.Add(sample);

                DateTime oldestAllowed = sample.ReceivedUtc - retention;
                int removeCount = 0;
                while (removeCount < Samples.Count &&
                    (Samples[removeCount].ReceivedUtc < oldestAllowed ||
                     Samples.Count - removeCount > maximumSamples))
                {
                    removeCount++;
                }
                if (removeCount > 0)
                    Samples.RemoveRange(0, removeCount);
            }
        }
    }

    internal static class PresentMonDependency
    {
        internal const string Version = "2.5.1";
        internal const string FileName = "PresentMon-2.5.1-x64.exe";
        internal const string ResourceName = "Traymetry.Embedded.PresentMon-2.5.1-x64.exe";
        internal const long ExpectedSize = 956768;
        internal const string ExpectedSha256 =
            "9BEC3083069F58F911E6A512F4806DB51A27BD096103087BC1D05EF54C80A191";

        internal static string InstallDirectory
        {
            get
            {
                return Path.Combine(SensorServiceInstaller.HostDirectory,
                    "Dependencies", "PresentMon", Version);
            }
        }

        internal static string InstalledPath
        {
            get { return Path.Combine(InstallDirectory, FileName); }
        }

        internal static string InstallOrVerify()
        {
            EnsureProtectedDirectories();
            if (IsVerifiedFile(InstalledPath))
            {
                SensorServiceInstaller.HardenExecutableFile(InstalledPath);
                return InstalledPath;
            }

            string temporaryPath = Path.Combine(InstallDirectory,
                "." + FileName + "." + Guid.NewGuid().ToString("N") + ".tmp");
            string backupPath = Path.Combine(InstallDirectory,
                "." + FileName + "." + Guid.NewGuid().ToString("N") + ".bak");
            try
            {
                ExtractEmbeddedPayload(temporaryPath);
                SensorServiceInstaller.HardenExecutableFile(temporaryPath);

                if (File.Exists(InstalledPath))
                {
                    if ((File.GetAttributes(InstalledPath) & FileAttributes.ReparsePoint) != 0)
                        throw new InvalidDataException("The installed PresentMon path is a reparse point.");
                    File.Replace(temporaryPath, InstalledPath, backupPath, true);
                    TryDeleteFile(backupPath);
                }
                else
                {
                    File.Move(temporaryPath, InstalledPath);
                }

                SensorServiceInstaller.HardenExecutableFile(InstalledPath);
                if (!IsVerifiedFile(InstalledPath))
                    throw new InvalidDataException("The installed PresentMon payload failed verification.");
                return InstalledPath;
            }
            finally
            {
                TryDeleteFile(temporaryPath);
                TryDeleteFile(backupPath);
            }
        }

        internal static bool IsVerifiedFile(string path)
        {
            try
            {
                if (String.IsNullOrWhiteSpace(path) || !File.Exists(path))
                    return false;
                FileInfo file = new FileInfo(path);
                if ((file.Attributes & FileAttributes.ReparsePoint) != 0 || file.Length != ExpectedSize)
                    return false;
                return FixedTimeEquals(ComputeSha256(path), ExpectedSha256);
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
            catch (System.Security.SecurityException) { return false; }
        }

        private static void EnsureProtectedDirectories()
        {
            string dependencies = Path.Combine(SensorServiceInstaller.HostDirectory, "Dependencies");
            string presentMon = Path.Combine(dependencies, "PresentMon");
            SensorServiceInstaller.EnsureSecureDirectory(SensorServiceInstaller.HostDirectory, true);
            SensorServiceInstaller.EnsureSecureDirectory(dependencies, true);
            SensorServiceInstaller.EnsureSecureDirectory(presentMon, true);
            SensorServiceInstaller.EnsureSecureDirectory(InstallDirectory, true);
        }

        private static void ExtractEmbeddedPayload(string destination)
        {
            Assembly assembly = Assembly.GetExecutingAssembly();
            using (Stream input = assembly.GetManifestResourceStream(ResourceName))
            {
                if (input == null)
                    throw new InvalidOperationException("Embedded PresentMon payload was not found.");
                using (FileStream output = new FileStream(destination, FileMode.CreateNew,
                    FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.WriteThrough))
                using (SHA256 sha = SHA256.Create())
                {
                    byte[] buffer = new byte[64 * 1024];
                    long total = 0;
                    int count;
                    while ((count = input.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        total += count;
                        if (total > ExpectedSize)
                            throw new InvalidDataException("Embedded PresentMon payload is larger than expected.");
                        output.Write(buffer, 0, count);
                        sha.TransformBlock(buffer, 0, count, null, 0);
                    }
                    sha.TransformFinalBlock(new byte[0], 0, 0);
                    output.Flush(true);
                    string hash = BitConverter.ToString(sha.Hash).Replace("-", String.Empty);
                    if (total != ExpectedSize || !FixedTimeEquals(hash, ExpectedSha256))
                        throw new InvalidDataException("Embedded PresentMon payload failed verification.");
                }
            }
        }

        private static string ComputeSha256(string path)
        {
            using (SHA256 sha = SHA256.Create())
            using (FileStream input = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.Read, 64 * 1024, FileOptions.SequentialScan))
            {
                return BitConverter.ToString(sha.ComputeHash(input)).Replace("-", String.Empty);
            }
        }

        private static bool FixedTimeEquals(string left, string right)
        {
            if (left == null || right == null || left.Length != right.Length)
                return false;
            int difference = 0;
            for (int index = 0; index < left.Length; index++)
                difference |= Char.ToUpperInvariant(left[index]) ^ Char.ToUpperInvariant(right[index]);
            return difference == 0;
        }

        private static void TryDeleteFile(string path)
        {
            try
            {
                if (!String.IsNullOrEmpty(path) && File.Exists(path))
                    File.Delete(path);
            }
            catch { }
        }
    }

    internal enum FrameTelemetryRunnerState
    {
        Idle,
        Starting,
        Running,
        Backoff,
        Faulted,
        Stopping
    }

    internal interface IFrameTelemetryRunner : IDisposable
    {
        FrameTelemetryRunnerState State { get; }
        string LastError { get; }
        void SetDemand(bool requested, DateTime utcNow);
        FrameTelemetrySnapshot[] GetSnapshots(DateTime utcNow, int maximumCount);
    }

    internal sealed class PresentMonStdoutRunner : IFrameTelemetryRunner
    {
        private const int MaximumOutputLineLength = 64 * 1024;
        private static readonly TimeSpan DemandIdleTimeout = TimeSpan.FromSeconds(8);
        private static readonly TimeSpan StableRunTime = TimeSpan.FromSeconds(15);
        private static readonly int[] RestartBackoffSeconds = { 1, 2, 5, 15, 30 };

        private readonly object _demandLock = new object();
        private readonly object _errorLock = new object();
        private readonly PresentMonStdoutTelemetryAdapter _adapter =
            new PresentMonStdoutTelemetryAdapter();
        private readonly ManualResetEvent _stopping = new ManualResetEvent(false);
        private readonly AutoResetEvent _demandChanged = new AutoResetEvent(false);
        private readonly Thread _supervisor;
        private DateTime _lastDemandUtc = DateTime.MinValue;
        private string _lastError = String.Empty;
        private int _state = (int)FrameTelemetryRunnerState.Idle;
        private bool _disposed;

        internal PresentMonStdoutRunner()
        {
            _supervisor = new Thread(SupervisorLoop)
            {
                IsBackground = true,
                Name = "Traymetry frame telemetry supervisor"
            };
            _supervisor.Start();
        }

        public FrameTelemetryRunnerState State
        {
            get { return (FrameTelemetryRunnerState)Thread.VolatileRead(ref _state); }
        }

        public string LastError
        {
            get
            {
                lock (_errorLock)
                    return _lastError;
            }
        }

        public void SetDemand(bool requested, DateTime utcNow)
        {
            if (!requested || _disposed)
                return;
            if (utcNow.Kind != DateTimeKind.Utc)
                utcNow = utcNow.ToUniversalTime();
            lock (_demandLock)
                _lastDemandUtc = utcNow;
            _demandChanged.Set();
        }

        public FrameTelemetrySnapshot[] GetSnapshots(DateTime utcNow, int maximumCount)
        {
            if (_disposed)
                return new FrameTelemetrySnapshot[0];
            return _adapter.GetSnapshots(utcNow, maximumCount);
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            SetState(FrameTelemetryRunnerState.Stopping);
            _stopping.Set();
            _demandChanged.Set();
            if (_supervisor.IsAlive)
                _supervisor.Join(5000);
            _adapter.Dispose();
            _demandChanged.Dispose();
            _stopping.Dispose();
        }

        private void SupervisorLoop()
        {
            Process process = null;
            KillOnCloseJob job = null;
            Thread outputThread = null;
            Thread errorThread = null;
            int failureCount = 0;
            DateTime nextStartUtc = DateTime.MinValue;
            DateTime processStartedUtc = DateTime.MinValue;

            try
            {
                while (!_stopping.WaitOne(0))
                {
                    DateTime now = DateTime.UtcNow;
                    if (!HasDemand(now))
                    {
                        StopProcess(ref process, ref job, ref outputThread, ref errorThread);
                        _adapter.Reset();
                        failureCount = 0;
                        nextStartUtc = DateTime.MinValue;
                        SetState(FrameTelemetryRunnerState.Idle);
                        WaitForSignal(250);
                        continue;
                    }

                    if (process == null)
                    {
                        if (now < nextStartUtc)
                        {
                            SetState(FrameTelemetryRunnerState.Backoff);
                            WaitForSignal(Math.Min(250,
                                Math.Max(1, (int)(nextStartUtc - now).TotalMilliseconds)));
                            continue;
                        }

                        try
                        {
                            StartProcess(out process, out job, out outputThread, out errorThread);
                            processStartedUtc = DateTime.UtcNow;
                            SetState(FrameTelemetryRunnerState.Running);
                            ClearLastError();
                        }
                        catch (Exception error)
                        {
                            StopProcess(ref process, ref job, ref outputThread, ref errorThread);
                            RecordError(error);
                            failureCount++;
                            nextStartUtc = DateTime.UtcNow.AddSeconds(
                                RestartBackoffSeconds[Math.Min(failureCount - 1,
                                    RestartBackoffSeconds.Length - 1)]);
                            SetState(FrameTelemetryRunnerState.Backoff);
                        }
                        continue;
                    }

                    bool exited;
                    try { exited = process.HasExited; }
                    catch (InvalidOperationException) { exited = true; }
                    if (exited)
                    {
                        int exitCode = 0;
                        try { exitCode = process.ExitCode; }
                        catch { }
                        RecordError("PresentMon exited unexpectedly with code " +
                            exitCode.ToString(CultureInfo.InvariantCulture) + ".");
                        bool wasStable = DateTime.UtcNow - processStartedUtc >= StableRunTime;
                        StopProcess(ref process, ref job, ref outputThread, ref errorThread);
                        failureCount = wasStable ? 1 : failureCount + 1;
                        nextStartUtc = DateTime.UtcNow.AddSeconds(
                            RestartBackoffSeconds[Math.Min(failureCount - 1,
                                RestartBackoffSeconds.Length - 1)]);
                        SetState(FrameTelemetryRunnerState.Backoff);
                        continue;
                    }

                    if (DateTime.UtcNow - processStartedUtc >= StableRunTime)
                        failureCount = 0;
                    WaitForSignal(200);
                }
            }
            catch (Exception error)
            {
                RecordError(error);
                SetState(FrameTelemetryRunnerState.Faulted);
            }
            finally
            {
                StopProcess(ref process, ref job, ref outputThread, ref errorThread);
                if (State != FrameTelemetryRunnerState.Faulted)
                    SetState(FrameTelemetryRunnerState.Idle);
            }
        }

        private void StartProcess(out Process process, out KillOnCloseJob job,
            out Thread outputThread, out Thread errorThread)
        {
            process = null;
            job = null;
            outputThread = null;
            errorThread = null;
            SetState(FrameTelemetryRunnerState.Starting);
            string executable = PresentMonDependency.InstallOrVerify();
            if (!PresentMonDependency.IsVerifiedFile(executable))
                throw new InvalidDataException("PresentMon executable verification failed.");

            _adapter.Reset();
            ProcessStartInfo start = new ProcessStartInfo
            {
                FileName = executable,
                Arguments = "--output_stdout --no_console_stats --v1_metrics --qpc_time " +
                    "--no_track_input --no_track_gpu --session_name Traymetry.FrameTelemetry.v1 " +
                    "--stop_existing_session",
                WorkingDirectory = PresentMonDependency.InstallDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = false
            };

            Process started = new Process { StartInfo = start, EnableRaisingEvents = false };
            try
            {
                if (!started.Start())
                    throw new InvalidOperationException("PresentMon did not start.");
                KillOnCloseJob startedJob = KillOnCloseJob.CreateAndAssign(started);

                Thread stdout = new Thread(delegate() { ReadOutput(started.StandardOutput); })
                {
                    IsBackground = true,
                    Name = "Traymetry PresentMon stdout"
                };
                Thread stderr = new Thread(delegate() { ReadErrors(started.StandardError); })
                {
                    IsBackground = true,
                    Name = "Traymetry PresentMon stderr"
                };
                stdout.Start();
                stderr.Start();

                process = started;
                job = startedJob;
                outputThread = stdout;
                errorThread = stderr;
            }
            catch
            {
                try
                {
                    if (!started.HasExited)
                        started.Kill();
                }
                catch { }
                started.Dispose();
                throw;
            }
        }

        private void ReadOutput(TextReader reader)
        {
            try
            {
                while (!_stopping.WaitOne(0))
                {
                    bool overlong;
                    string line = ReadBoundedLine(reader, MaximumOutputLineLength, out overlong);
                    if (line == null)
                        return;
                    if (!overlong)
                        _adapter.TryConsumeLine(line, DateTime.UtcNow);
                }
            }
            catch (IOException error) { RecordError(error); }
            catch (ObjectDisposedException) { }
        }

        private void ReadErrors(TextReader reader)
        {
            try
            {
                while (!_stopping.WaitOne(0))
                {
                    bool overlong;
                    string line = ReadBoundedLine(reader, 4096, out overlong);
                    if (line == null)
                        return;
                    if (!overlong && !String.IsNullOrWhiteSpace(line))
                        RecordError(line);
                }
            }
            catch (IOException error) { RecordError(error); }
            catch (ObjectDisposedException) { }
        }

        private static string ReadBoundedLine(TextReader reader, int maximumLength,
            out bool overlong)
        {
            overlong = false;
            StringBuilder result = new StringBuilder(Math.Min(maximumLength, 4096));
            bool sawInput = false;
            while (true)
            {
                int value = reader.Read();
                if (value < 0)
                    return sawInput ? result.ToString() : null;
                sawInput = true;
                char item = (char)value;
                if (item == '\n')
                    return result.ToString();
                if (item == '\r')
                    continue;
                if (result.Length < maximumLength)
                    result.Append(item);
                else
                    overlong = true;
            }
        }

        private static void StopProcess(ref Process process, ref KillOnCloseJob job,
            ref Thread outputThread, ref Thread errorThread)
        {
            if (job != null)
            {
                job.Dispose();
                job = null;
            }
            if (process != null)
            {
                try
                {
                    if (!process.HasExited)
                        process.Kill();
                }
                catch { }
                try { process.WaitForExit(2000); }
                catch { }
            }
            if (outputThread != null && outputThread.IsAlive)
                outputThread.Join(1000);
            if (errorThread != null && errorThread.IsAlive)
                errorThread.Join(1000);
            if (process != null)
                process.Dispose();
            process = null;
            outputThread = null;
            errorThread = null;
        }

        private bool HasDemand(DateTime utcNow)
        {
            lock (_demandLock)
                return _lastDemandUtc != DateTime.MinValue &&
                    utcNow - _lastDemandUtc <= DemandIdleTimeout;
        }

        private void WaitForSignal(int milliseconds)
        {
            if (_stopping.WaitOne(0))
                return;
            _demandChanged.WaitOne(Math.Max(1, milliseconds));
        }

        private void SetState(FrameTelemetryRunnerState state)
        {
            Interlocked.Exchange(ref _state, (int)state);
        }

        private void ClearLastError()
        {
            lock (_errorLock)
                _lastError = String.Empty;
        }

        private void RecordError(Exception error)
        {
            RecordError(error == null ? "Unknown frame telemetry error." : error.Message);
        }

        private void RecordError(string message)
        {
            if (String.IsNullOrWhiteSpace(message))
                return;
            if (message.Length > 4096)
                message = message.Substring(0, 4096);
            lock (_errorLock)
                _lastError = message;
        }

        private sealed class KillOnCloseJob : IDisposable
        {
            private const int JobObjectExtendedLimitInformationClass = 9;
            private const uint JobObjectLimitKillOnJobClose = 0x00002000;
            private IntPtr _handle;

            private KillOnCloseJob(IntPtr handle)
            {
                _handle = handle;
            }

            internal static KillOnCloseJob CreateAndAssign(Process process)
            {
                IntPtr handle = CreateJobObject(IntPtr.Zero, null);
                if (handle == IntPtr.Zero)
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                KillOnCloseJob job = new KillOnCloseJob(handle);
                try
                {
                    JobObjectExtendedLimitInformation information =
                        new JobObjectExtendedLimitInformation();
                    information.BasicLimitInformation.LimitFlags = JobObjectLimitKillOnJobClose;
                    int size = Marshal.SizeOf(typeof(JobObjectExtendedLimitInformation));
                    IntPtr buffer = Marshal.AllocHGlobal(size);
                    try
                    {
                        Marshal.StructureToPtr(information, buffer, false);
                        if (!SetInformationJobObject(handle, JobObjectExtendedLimitInformationClass,
                            buffer, (uint)size))
                        {
                            throw new Win32Exception(Marshal.GetLastWin32Error());
                        }
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(buffer);
                    }
                    if (!AssignProcessToJobObject(handle, process.Handle))
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                    return job;
                }
                catch
                {
                    job.Dispose();
                    throw;
                }
            }

            public void Dispose()
            {
                IntPtr handle = Interlocked.Exchange(ref _handle, IntPtr.Zero);
                if (handle != IntPtr.Zero)
                    CloseHandle(handle);
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct IoCounters
            {
                internal ulong ReadOperationCount;
                internal ulong WriteOperationCount;
                internal ulong OtherOperationCount;
                internal ulong ReadTransferCount;
                internal ulong WriteTransferCount;
                internal ulong OtherTransferCount;
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct JobObjectBasicLimitInformation
            {
                internal long PerProcessUserTimeLimit;
                internal long PerJobUserTimeLimit;
                internal uint LimitFlags;
                internal UIntPtr MinimumWorkingSetSize;
                internal UIntPtr MaximumWorkingSetSize;
                internal uint ActiveProcessLimit;
                internal UIntPtr Affinity;
                internal uint PriorityClass;
                internal uint SchedulingClass;
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct JobObjectExtendedLimitInformation
            {
                internal JobObjectBasicLimitInformation BasicLimitInformation;
                internal IoCounters IoInfo;
                internal UIntPtr ProcessMemoryLimit;
                internal UIntPtr JobMemoryLimit;
                internal UIntPtr PeakProcessMemoryUsed;
                internal UIntPtr PeakJobMemoryUsed;
            }

            [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            private static extern IntPtr CreateJobObject(IntPtr jobAttributes, string name);

            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            private static extern bool SetInformationJobObject(IntPtr job, int informationClass,
                IntPtr information, uint informationLength);

            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

            [DllImport("kernel32.dll")]
            [return: MarshalAs(UnmanagedType.Bool)]
            private static extern bool CloseHandle(IntPtr handle);
        }
    }

    internal sealed class PresentMonCsvFrameParser
    {
        private const int MaximumLineLength = 64 * 1024;
        private const int MaximumColumnCount = 256;
        private const int MaximumFieldLength = 4096;
        private const double MaximumFps = 100000;
        private const double MaximumFrameIntervalMs = 10 * 60 * 1000;

        private Dictionary<string, int> _columns;

        internal void Reset()
        {
            _columns = null;
        }

        internal bool TryParseLine(string line, DateTime receivedUtc, out PresentMonFrameSample sample)
        {
            sample = null;
            string[] fields;
            if (!TrySplitCsv(line, out fields))
                return false;

            if (LooksLikeHeader(fields))
            {
                BuildHeader(fields);
                return false;
            }
            if (_columns == null)
                return false;

            int processId;
            if (!TryReadInt32(fields, out processId, "PROCESSID", "PID") || processId <= 0)
                return false;

            PresentMonFrameSample parsed = new PresentMonFrameSample
            {
                ProcessId = processId,
                ProcessName = SanitizeProcessName(ReadString(fields, "APPLICATION", "PROCESSNAME")),
                ReceivedUtc = NormalizeUtc(receivedUtc)
            };

            ulong swapChain;
            if (TryReadUInt64(fields, out swapChain, "SWAPCHAINADDRESS", "SWAPCHAIN"))
                parsed.SwapChainAddress = swapChain;

            bool dropped;
            if (TryReadBoolean(fields, out dropped, "DROPPED"))
                parsed.Dropped = dropped;

            parsed.DisplayedFps = ReadBoundedDouble(fields, 0, MaximumFps,
                "DISPLAYEDFPS", "DISPLAYFPS", "FPSDISPLAY");
            parsed.PresentedFps = ReadBoundedDouble(fields, 0, MaximumFps,
                "PRESENTEDFPS", "FPSPRESENTS", "FPSPRESENTED", "FPS");
            parsed.ApplicationFps = ReadBoundedDouble(fields, 0, MaximumFps,
                "APPLICATIONFPS", "APPFPS", "FPSAPP");

            parsed.PresentIntervalMs = ReadBoundedDouble(fields, 0, MaximumFrameIntervalMs,
                "MSBETWEENPRESENTS", "PRESENTEDFRAMETIME", "FRAMETIME", "FRAMETIMEMS");
            parsed.DisplayIntervalMs = ReadBoundedDouble(fields, 0, MaximumFrameIntervalMs,
                "MSBETWEENDISPLAYCHANGE", "DISPLAYEDFRAMETIME", "DISPLAYEDTIME");
            parsed.ApplicationIntervalMs = ReadBoundedDouble(fields, 0, MaximumFrameIntervalMs,
                "MSBETWEENAPPSTART", "APPLICATIONFRAMETIME");

            double sourceTime;
            if (TryReadDouble(fields, out sourceTime, "TIMEINSECONDS", "CPUSTARTTIME"))
                parsed.SourceTimeSeconds = IsWithin(sourceTime, 0, 1e12) ? sourceTime : 0;
            else if (TryReadDouble(fields, out sourceTime, "CPUSTARTQPCTIME"))
                parsed.SourceTimeSeconds = IsWithin(sourceTime, 0, 1e15) ? sourceTime / 1000.0 : 0;

            if (parsed.DisplayedFps <= 0 && parsed.PresentedFps <= 0 &&
                parsed.ApplicationFps <= 0 && parsed.PresentIntervalMs <= 0 &&
                parsed.DisplayIntervalMs <= 0 && parsed.ApplicationIntervalMs <= 0)
            {
                // A structurally valid row is retained while the source warms up.
                // This lets callers distinguish "no process" from "no metric yet".
            }

            sample = parsed;
            return true;
        }

        private bool LooksLikeHeader(string[] fields)
        {
            bool processId = false;
            bool application = false;
            foreach (string field in fields)
            {
                string name = NormalizeColumnName(field);
                processId |= name == "PROCESSID" || name == "PID";
                application |= name == "APPLICATION" || name == "PROCESSNAME";
            }
            return processId && application;
        }

        private void BuildHeader(string[] fields)
        {
            Dictionary<string, int> columns = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int index = 0; index < fields.Length; index++)
            {
                string name = NormalizeColumnName(fields[index]);
                if (name.Length > 0 && !columns.ContainsKey(name))
                    columns.Add(name, index);
            }
            _columns = columns;
        }

        private string ReadString(string[] fields, params string[] aliases)
        {
            int index;
            if (!TryGetColumnIndex(out index, aliases) || index >= fields.Length)
                return String.Empty;
            string value = fields[index].Trim();
            return IsUnavailable(value) ? String.Empty : value;
        }

        private double ReadBoundedDouble(string[] fields, double minimum, double maximum,
            params string[] aliases)
        {
            double value;
            return TryReadDouble(fields, out value, aliases) && IsWithin(value, minimum, maximum)
                ? value
                : 0;
        }

        private bool TryReadDouble(string[] fields, out double value, params string[] aliases)
        {
            value = 0;
            int index;
            if (!TryGetColumnIndex(out index, aliases) || index >= fields.Length)
                return false;
            string text = fields[index].Trim();
            if (IsUnavailable(text) || !Double.TryParse(text, NumberStyles.Float,
                CultureInfo.InvariantCulture, out value))
                return false;
            return !Double.IsNaN(value) && !Double.IsInfinity(value);
        }

        private bool TryReadInt32(string[] fields, out int value, params string[] aliases)
        {
            value = 0;
            int index;
            return TryGetColumnIndex(out index, aliases) && index < fields.Length &&
                Int32.TryParse(fields[index].Trim(), NumberStyles.None,
                    CultureInfo.InvariantCulture, out value);
        }

        private bool TryReadUInt64(string[] fields, out ulong value, params string[] aliases)
        {
            value = 0;
            int index;
            if (!TryGetColumnIndex(out index, aliases) || index >= fields.Length)
                return false;
            string text = fields[index].Trim();
            if (IsUnavailable(text))
                return false;
            if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                return UInt64.TryParse(text.Substring(2), NumberStyles.AllowHexSpecifier,
                    CultureInfo.InvariantCulture, out value);
            if (text.Any(delegate(char item)
                { return (item >= 'A' && item <= 'F') || (item >= 'a' && item <= 'f'); }))
            {
                return UInt64.TryParse(text, NumberStyles.AllowHexSpecifier,
                    CultureInfo.InvariantCulture, out value);
            }
            return UInt64.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
        }

        private bool TryReadBoolean(string[] fields, out bool value, params string[] aliases)
        {
            value = false;
            int index;
            if (!TryGetColumnIndex(out index, aliases) || index >= fields.Length)
                return false;
            string text = fields[index].Trim();
            if (text == "1" || text.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                text.Equals("yes", StringComparison.OrdinalIgnoreCase))
            {
                value = true;
                return true;
            }
            if (text == "0" || text.Equals("false", StringComparison.OrdinalIgnoreCase) ||
                text.Equals("no", StringComparison.OrdinalIgnoreCase))
                return true;
            return false;
        }

        private bool TryGetColumnIndex(out int index, params string[] aliases)
        {
            index = -1;
            if (_columns == null)
                return false;
            foreach (string alias in aliases)
            {
                if (_columns.TryGetValue(alias, out index))
                    return true;
            }
            return false;
        }

        private static bool TrySplitCsv(string line, out string[] fields)
        {
            fields = null;
            if (String.IsNullOrEmpty(line) || line.Length > MaximumLineLength || line.IndexOf('\0') >= 0)
                return false;

            List<string> result = new List<string>();
            StringBuilder field = new StringBuilder();
            bool quoted = false;
            bool quoteClosed = false;

            for (int index = 0; index < line.Length; index++)
            {
                char current = line[index];
                if (quoted)
                {
                    if (current == '"')
                    {
                        if (index + 1 < line.Length && line[index + 1] == '"')
                        {
                            field.Append('"');
                            index++;
                        }
                        else
                        {
                            quoted = false;
                            quoteClosed = true;
                        }
                    }
                    else
                    {
                        field.Append(current);
                    }
                }
                else if (current == ',' && !quoted)
                {
                    if (!AddField(result, field))
                        return false;
                    quoteClosed = false;
                }
                else if (current == '"')
                {
                    if (field.Length != 0 || quoteClosed)
                        return false;
                    quoted = true;
                }
                else
                {
                    if (quoteClosed && !Char.IsWhiteSpace(current))
                        return false;
                    if (!quoteClosed)
                        field.Append(current);
                }

                if (field.Length > MaximumFieldLength)
                    return false;
            }

            if (quoted || !AddField(result, field))
                return false;
            fields = result.ToArray();
            return true;
        }

        private static bool AddField(List<string> fields, StringBuilder value)
        {
            if (fields.Count >= MaximumColumnCount || value.Length > MaximumFieldLength)
                return false;
            fields.Add(value.ToString());
            value.Clear();
            return true;
        }

        private static string NormalizeColumnName(string value)
        {
            if (String.IsNullOrEmpty(value))
                return String.Empty;
            StringBuilder normalized = new StringBuilder(value.Length);
            foreach (char item in value.Trim().TrimStart('\uFEFF'))
            {
                if (Char.IsLetterOrDigit(item))
                    normalized.Append(Char.ToUpperInvariant(item));
            }
            return normalized.ToString();
        }

        private static string SanitizeProcessName(string value)
        {
            if (String.IsNullOrWhiteSpace(value))
                return String.Empty;
            int separator = Math.Max(value.LastIndexOf('\\'), value.LastIndexOf('/'));
            if (separator >= 0 && separator + 1 < value.Length)
                value = value.Substring(separator + 1);
            StringBuilder sanitized = new StringBuilder(Math.Min(value.Length, 260));
            foreach (char item in value)
            {
                if (!Char.IsControl(item))
                    sanitized.Append(item);
                if (sanitized.Length == 260)
                    break;
            }
            return sanitized.ToString().Trim();
        }

        private static bool IsUnavailable(string value)
        {
            if (String.IsNullOrWhiteSpace(value))
                return true;
            return value.Equals("NA", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("N/A", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("NULL", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("NAN", StringComparison.OrdinalIgnoreCase) ||
                value == "-";
        }

        private static bool IsWithin(double value, double minimum, double maximum)
        {
            return !Double.IsNaN(value) && !Double.IsInfinity(value) &&
                value >= minimum && value <= maximum;
        }

        private static DateTime NormalizeUtc(DateTime value)
        {
            if (value.Kind == DateTimeKind.Utc)
                return value;
            if (value.Kind == DateTimeKind.Local)
                return value.ToUniversalTime();
            return DateTime.SpecifyKind(value, DateTimeKind.Utc);
        }
    }

    internal sealed class PresentMonFrameSample
    {
        internal int ProcessId;
        internal string ProcessName = String.Empty;
        internal ulong SwapChainAddress;
        internal DateTime ReceivedUtc;
        internal double SourceTimeSeconds;
        internal double DisplayedFps;
        internal double PresentedFps;
        internal double ApplicationFps;
        internal double PresentIntervalMs;
        internal double DisplayIntervalMs;
        internal double ApplicationIntervalMs;
        internal bool Dropped;
    }

    /// <summary>
    /// Pure, process-free regression checks. A future command-line test hook can
    /// call Run without changing the parser or granting additional privileges.
    /// </summary>
    internal static class FrameTelemetrySelfTest
    {
        internal static string Run()
        {
            TestReorderedColumnsAndIntervals();
            TestDirectFpsAndUnavailableValues();
            TestOnePercentLowAndProcessIsolation();
            TestInputLimits();
            return "Frame telemetry self-test passed.";
        }

        private static void TestReorderedColumnsAndIntervals()
        {
            DateTime start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            using (PresentMonStdoutTelemetryAdapter adapter = new PresentMonStdoutTelemetryAdapter())
            {
                adapter.TryConsumeLine(
                    "MsBetweenDisplayChange,ProcessID,Application,Dropped,SwapChainAddress,MsBetweenPresents,MsBetweenAppStart",
                    start);
                for (int index = 0; index < 180; index++)
                {
                    DateTime received = start.AddMilliseconds(index * 5);
                    Assert(adapter.TryConsumeLine(
                        "16.6667,4242,game.exe,0,0xABC,16.6667,16.6667", received),
                        "A valid reordered PresentMon row was rejected.");
                }

                FrameTelemetrySnapshot snapshot = adapter.GetSnapshot(4242, start.AddMilliseconds(900));
                Assert(snapshot.Status == FrameTelemetryStatus.Ready, "FPS snapshot did not become ready.");
                Assert(snapshot.ProcessName == "game.exe", "Process name was parsed incorrectly.");
                Assert(snapshot.SwapChainAddress == 0xABC, "Swap chain was parsed incorrectly.");
                AssertNear(snapshot.DisplayedFps, 60, 0.05, "Displayed FPS");
                AssertNear(snapshot.PresentedFps, 60, 0.05, "Presented FPS");
                AssertNear(snapshot.ApplicationFps, 60, 0.05, "Application FPS");
                AssertNear(snapshot.FrameTimeMs, 16.6667, 0.001, "Frame time");
            }
        }

        private static void TestDirectFpsAndUnavailableValues()
        {
            DateTime now = new DateTime(2026, 1, 1, 0, 1, 0, DateTimeKind.Utc);
            using (PresentMonStdoutTelemetryAdapter adapter = new PresentMonStdoutTelemetryAdapter())
            {
                adapter.TryConsumeLine(
                    "Application,ApplicationFPS,FrameTime,ProcessID,DisplayedFPS,PresentedFPS,SwapChainAddress",
                    now);
                Assert(adapter.TryConsumeLine(
                    "\"C:\\Games\\A, Game.exe\",72,13.8,91,144,72,1234", now),
                    "Quoted process row was rejected.");
                Assert(adapter.TryConsumeLine(
                    "Other.exe,NA,N/A,92,NA,-,0", now),
                    "A structurally valid NA row was rejected.");

                FrameTelemetrySnapshot snapshot = adapter.GetSnapshot(91, now.AddMilliseconds(10));
                Assert(snapshot.ProcessName == "A, Game.exe", "Quoted process name was not sanitized.");
                AssertNear(snapshot.DisplayedFps, 144, 0.001, "Direct displayed FPS");
                AssertNear(snapshot.PresentedFps, 72, 0.001, "Direct presented FPS");
                AssertNear(snapshot.ApplicationFps, 72, 0.001, "Direct application FPS");

                FrameTelemetrySnapshot unavailable = adapter.GetSnapshot(92, now.AddMilliseconds(10));
                Assert(unavailable.Status == FrameTelemetryStatus.Collecting,
                    "NA metrics should remain in the collecting state.");
            }
        }

        private static void TestOnePercentLowAndProcessIsolation()
        {
            DateTime start = new DateTime(2026, 1, 1, 0, 2, 0, DateTimeKind.Utc);
            using (PresentMonStdoutTelemetryAdapter adapter = new PresentMonStdoutTelemetryAdapter())
            {
                adapter.TryConsumeLine("Application,ProcessID,SwapChainAddress,MsBetweenPresents", start);
                for (int index = 0; index < 100; index++)
                {
                    double frameTime = index == 99 ? 50 : 16.6667;
                    adapter.TryConsumeLine(String.Format(CultureInfo.InvariantCulture,
                        "slow.exe,501,1,{0}", frameTime), start.AddMilliseconds(index * 5));
                }
                adapter.TryConsumeLine("fast.exe,502,1,5", start.AddMilliseconds(500));

                FrameTelemetrySnapshot slow = adapter.GetSnapshot(501, start.AddMilliseconds(510));
                FrameTelemetrySnapshot fast = adapter.GetSnapshot(502, start.AddMilliseconds(510));
                AssertNear(slow.OnePercentLowFps, 20, 0.01, "1% low FPS");
                Assert(slow.ProcessId == 501 && fast.ProcessId == 502,
                    "Samples from different processes were mixed.");
                Assert(fast.PresentedFps > slow.PresentedFps,
                    "Selected PID did not isolate the faster process.");
            }
        }

        private static void TestInputLimits()
        {
            DateTime now = new DateTime(2026, 1, 1, 0, 3, 0, DateTimeKind.Utc);
            using (PresentMonStdoutTelemetryAdapter adapter = new PresentMonStdoutTelemetryAdapter())
            {
                Assert(!adapter.TryConsumeLine(new string('x', 64 * 1024 + 1), now),
                    "An oversized line was accepted.");
                Assert(!adapter.TryConsumeLine("Application,ProcessID,MsBetweenPresents", now),
                    "A header must not be reported as a frame.");
                Assert(!adapter.TryConsumeLine("\"unterminated,1,16", now),
                    "Malformed CSV quoting was accepted.");
                Assert(!adapter.TryConsumeLine("bad.exe,-1,16", now),
                    "An invalid process ID was accepted.");
            }
        }

        private static void AssertNear(double actual, double expected, double tolerance, string name)
        {
            if (Math.Abs(actual - expected) > tolerance)
            {
                throw new InvalidOperationException(String.Format(CultureInfo.InvariantCulture,
                    "{0} expected {1}, actual {2}.", name, expected, actual));
            }
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition)
                throw new InvalidOperationException(message);
        }
    }
}
