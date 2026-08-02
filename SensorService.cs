using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.ServiceProcess;
using System.Threading;
using System.Windows.Forms;

namespace Traymetry
{
    internal static class SensorPipeProtocol
    {
        internal const string PipeName = "Traymetry.Sensor.v1";
        private const int Magic = 0x32524D54; // TMR2
        private const int MagicV2 = 0x33524D54; // TMR3
        internal const int MaximumFrameProcesses = 16;

        internal static void WriteSnapshot(BinaryWriter writer, SensorSnapshot snapshot)
        {
            writer.Write(Magic);
            WriteString(writer, snapshot.CpuName);
            writer.Write(snapshot.Temperature);
            writer.Write(snapshot.Usage);
            writer.Write(snapshot.ClockMhz);
            writer.Write(snapshot.PowerWatts);
            WriteString(writer, snapshot.GpuName);
            writer.Write(snapshot.GpuTemperature);
            writer.Write(snapshot.GpuUsage);
            writer.Write(snapshot.GpuClockMhz);
            writer.Write(snapshot.GpuPowerWatts);
            writer.Write(snapshot.GpuMemoryUsedGb);
            writer.Write(snapshot.GpuMemoryTotalGb);
            writer.Write(snapshot.NetworkDownloadKbps);
            writer.Write(snapshot.NetworkUploadKbps);
            writer.Write(snapshot.MemoryUsedGb);
            writer.Write(snapshot.MemoryTotalGb);
            writer.Write(snapshot.MemoryClockMhz);
            writer.Write(snapshot.StorageUsedGb);
            writer.Write(snapshot.StorageTotalGb);
            WriteStrings(writer, snapshot.StorageDriveNames);
            WriteDoubles(writer, snapshot.StorageDriveUsedGb);
            WriteDoubles(writer, snapshot.StorageDriveTotalGb);
            WriteStrings(writer, snapshot.FanNames);
            WriteDoubles(writer, snapshot.FanRpm);
            WriteDoubles(writer, snapshot.FanControlPercent);
        }

        internal static SensorSnapshot ReadSnapshot(BinaryReader reader)
        {
            if (reader.ReadInt32() != Magic)
                throw new InvalidDataException("Unsupported Traymetry sensor protocol.");

            SensorSnapshot snapshot = new SensorSnapshot();
            snapshot.CpuName = reader.ReadString();
            snapshot.Temperature = reader.ReadDouble();
            snapshot.Usage = reader.ReadDouble();
            snapshot.ClockMhz = reader.ReadDouble();
            snapshot.PowerWatts = reader.ReadDouble();
            snapshot.GpuName = reader.ReadString();
            snapshot.GpuTemperature = reader.ReadDouble();
            snapshot.GpuUsage = reader.ReadDouble();
            snapshot.GpuClockMhz = reader.ReadDouble();
            snapshot.GpuPowerWatts = reader.ReadDouble();
            snapshot.GpuMemoryUsedGb = reader.ReadDouble();
            snapshot.GpuMemoryTotalGb = reader.ReadDouble();
            snapshot.NetworkDownloadKbps = reader.ReadDouble();
            snapshot.NetworkUploadKbps = reader.ReadDouble();
            snapshot.MemoryUsedGb = reader.ReadDouble();
            snapshot.MemoryTotalGb = reader.ReadDouble();
            snapshot.MemoryClockMhz = reader.ReadDouble();
            snapshot.StorageUsedGb = reader.ReadDouble();
            snapshot.StorageTotalGb = reader.ReadDouble();
            snapshot.StorageDriveNames = ReadStrings(reader);
            snapshot.StorageDriveUsedGb = ReadDoubles(reader);
            snapshot.StorageDriveTotalGb = ReadDoubles(reader);
            snapshot.FanNames = ReadStrings(reader);
            snapshot.FanRpm = ReadDoubles(reader);
            snapshot.FanControlPercent = ReadDoubles(reader);
            return snapshot;
        }

        internal static void WriteSnapshotV2(BinaryWriter writer, SensorSnapshot snapshot)
        {
            writer.Write(MagicV2);
            byte[] basePayload;
            using (MemoryStream buffer = new MemoryStream())
            {
                using (BinaryWriter baseWriter = new BinaryWriter(buffer,
                    System.Text.Encoding.UTF8, true))
                {
                    WriteSnapshot(baseWriter, snapshot);
                    baseWriter.Flush();
                }
                basePayload = buffer.ToArray();
            }
            writer.Write(basePayload.Length);
            writer.Write(basePayload);
            writer.Write(snapshot.FrameTelemetryState);
            WriteInts(writer, snapshot.FrameProcessIds);
            WriteStrings(writer, snapshot.FrameProcessNames);
            WriteInts(writer, snapshot.FrameStatuses);
            WriteDoubles(writer, snapshot.FrameDisplayedFps);
            WriteDoubles(writer, snapshot.FramePresentedFps);
            WriteDoubles(writer, snapshot.FrameApplicationFps);
            WriteDoubles(writer, snapshot.FrameTimeMs);
            WriteDoubles(writer, snapshot.FrameOnePercentLowFps);
        }

        internal static SensorSnapshot ReadSnapshotV2(BinaryReader reader)
        {
            if (reader.ReadInt32() != MagicV2)
                throw new InvalidDataException("Unsupported Traymetry sensor protocol v2.");
            int baseLength = reader.ReadInt32();
            if (baseLength <= 0 || baseLength > 64 * 1024)
                throw new InvalidDataException("Invalid Traymetry base sensor payload length.");
            byte[] basePayload = reader.ReadBytes(baseLength);
            if (basePayload.Length != baseLength)
                throw new EndOfStreamException();

            SensorSnapshot snapshot;
            using (MemoryStream buffer = new MemoryStream(basePayload, false))
            using (BinaryReader baseReader = new BinaryReader(buffer,
                System.Text.Encoding.UTF8, true))
            {
                snapshot = ReadSnapshot(baseReader);
                if (buffer.Position != buffer.Length)
                    throw new InvalidDataException("Trailing data in Traymetry base sensor payload.");
            }

            snapshot.FrameTelemetryState = reader.ReadInt32();
            snapshot.FrameProcessIds = ReadInts(reader, MaximumFrameProcesses);
            snapshot.FrameProcessNames = ReadStrings(reader, MaximumFrameProcesses);
            snapshot.FrameStatuses = ReadInts(reader, MaximumFrameProcesses);
            snapshot.FrameDisplayedFps = ReadDoubles(reader, MaximumFrameProcesses);
            snapshot.FramePresentedFps = ReadDoubles(reader, MaximumFrameProcesses);
            snapshot.FrameApplicationFps = ReadDoubles(reader, MaximumFrameProcesses);
            snapshot.FrameTimeMs = ReadDoubles(reader, MaximumFrameProcesses);
            snapshot.FrameOnePercentLowFps = ReadDoubles(reader, MaximumFrameProcesses);
            ValidateFrameArrays(snapshot);
            return snapshot;
        }

        private static void WriteString(BinaryWriter writer, string value)
        {
            writer.Write(value ?? String.Empty);
        }

        private static void WriteStrings(BinaryWriter writer, string[] values)
        {
            values = values ?? new string[0];
            writer.Write(values.Length);
            foreach (string value in values)
                WriteString(writer, value);
        }

        private static void WriteDoubles(BinaryWriter writer, double[] values)
        {
            values = values ?? new double[0];
            writer.Write(values.Length);
            foreach (double value in values)
                writer.Write(value);
        }

        private static void WriteInts(BinaryWriter writer, int[] values)
        {
            values = values ?? new int[0];
            writer.Write(values.Length);
            foreach (int value in values)
                writer.Write(value);
        }

        private static string[] ReadStrings(BinaryReader reader)
        {
            return ReadStrings(reader, 128);
        }

        private static string[] ReadStrings(BinaryReader reader, int maximumCount)
        {
            int count = ReadArrayLength(reader, maximumCount);
            string[] values = new string[count];
            for (int index = 0; index < count; index++)
                values[index] = reader.ReadString();
            return values;
        }

        private static double[] ReadDoubles(BinaryReader reader)
        {
            return ReadDoubles(reader, 128);
        }

        private static double[] ReadDoubles(BinaryReader reader, int maximumCount)
        {
            int count = ReadArrayLength(reader, maximumCount);
            double[] values = new double[count];
            for (int index = 0; index < count; index++)
                values[index] = reader.ReadDouble();
            return values;
        }

        private static int[] ReadInts(BinaryReader reader, int maximumCount)
        {
            int count = ReadArrayLength(reader, maximumCount);
            int[] values = new int[count];
            for (int index = 0; index < count; index++)
                values[index] = reader.ReadInt32();
            return values;
        }

        private static int ReadArrayLength(BinaryReader reader)
        {
            return ReadArrayLength(reader, 128);
        }

        private static int ReadArrayLength(BinaryReader reader, int maximumCount)
        {
            int count = reader.ReadInt32();
            if (count < 0 || count > maximumCount)
                throw new InvalidDataException("Invalid Traymetry sensor array length.");
            return count;
        }

        private static void ValidateFrameArrays(SensorSnapshot snapshot)
        {
            int count = snapshot.FrameProcessIds.Length;
            if (snapshot.FrameProcessNames.Length != count ||
                snapshot.FrameStatuses.Length != count ||
                snapshot.FrameDisplayedFps.Length != count ||
                snapshot.FramePresentedFps.Length != count ||
                snapshot.FrameApplicationFps.Length != count ||
                snapshot.FrameTimeMs.Length != count ||
                snapshot.FrameOnePercentLowFps.Length != count)
            {
                throw new InvalidDataException("Mismatched Traymetry frame telemetry arrays.");
            }
        }
    }

    internal static class SensorServiceClient
    {
        private const uint ScManagerConnect = 0x0001;
        private const uint ServiceQueryStatus = 0x0004;

        internal static bool TryReadSnapshot(out SensorSnapshot snapshot)
        {
            return TryReadSnapshot(out snapshot, false);
        }

        internal static bool TryReadSnapshot(out SensorSnapshot snapshot,
            bool frameTelemetryDemand)
        {
            if (TryReadSnapshotVersion(2, frameTelemetryDemand, out snapshot))
                return true;
            return TryReadSnapshotVersion(1, false, out snapshot);
        }

        private static bool TryReadSnapshotVersion(byte version, bool frameTelemetryDemand,
            out SensorSnapshot snapshot)
        {
            snapshot = null;
            try
            {
                using (NamedPipeClientStream pipe = new NamedPipeClientStream(
                    ".", SensorPipeProtocol.PipeName, PipeDirection.InOut, PipeOptions.None,
                    TokenImpersonationLevel.Identification))
                {
                    pipe.Connect(500);
                    if (!IsExpectedServiceProcess(pipe))
                        return false;
                    try
                    {
                        pipe.ReadTimeout = 1500;
                        pipe.WriteTimeout = 1500;
                    }
                    catch (InvalidOperationException) { }
                    using (BinaryWriter writer = new BinaryWriter(pipe, System.Text.Encoding.UTF8, true))
                    {
                        writer.Write(version);
                        if (version == 2)
                            writer.Write(frameTelemetryDemand);
                        writer.Flush();
                        byte[] header = ReadExactly(pipe, 5, 1500);
                        if (header[0] != 1)
                            return false;
                        int payloadLength = BitConverter.ToInt32(header, 1);
                        if (payloadLength <= 0 || payloadLength > 64 * 1024)
                            return false;
                        byte[] payload = ReadExactly(pipe, payloadLength, 1500);
                        using (MemoryStream buffer = new MemoryStream(payload, false))
                        using (BinaryReader reader = new BinaryReader(buffer, System.Text.Encoding.UTF8, true))
                        {
                            snapshot = version == 2
                                ? SensorPipeProtocol.ReadSnapshotV2(reader)
                                : SensorPipeProtocol.ReadSnapshot(reader);
                            return buffer.Position == buffer.Length;
                        }
                    }
                }
            }
            catch (IOException) { return false; }
            catch (System.TimeoutException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
            catch (InvalidDataException) { return false; }
        }

        private static bool IsExpectedServiceProcess(NamedPipeClientStream pipe)
        {
            uint pipeServerProcessId;
            if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle.DangerousGetHandle(),
                out pipeServerProcessId) || pipeServerProcessId == 0)
                return false;

            IntPtr manager = OpenSCManager(null, null, ScManagerConnect);
            if (manager == IntPtr.Zero)
                return false;
            try
            {
                IntPtr service = OpenService(manager, TraymetrySensorService.ServiceNameValue,
                    ServiceQueryStatus);
                if (service == IntPtr.Zero)
                    return false;
                try
                {
                    ServiceStatusProcess status = new ServiceStatusProcess();
                    int bytesNeeded;
                    if (!QueryServiceStatusEx(service, 0, ref status,
                        Marshal.SizeOf(typeof(ServiceStatusProcess)), out bytesNeeded))
                        return false;
                    return status.ProcessId != 0 && status.ProcessId == pipeServerProcessId;
                }
                finally { CloseServiceHandle(service); }
            }
            finally { CloseServiceHandle(manager); }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ServiceStatusProcess
        {
            internal uint ServiceType;
            internal uint CurrentState;
            internal uint ControlsAccepted;
            internal uint Win32ExitCode;
            internal uint ServiceSpecificExitCode;
            internal uint CheckPoint;
            internal uint WaitHint;
            internal uint ProcessId;
            internal uint ServiceFlags;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetNamedPipeServerProcessId(IntPtr pipe,
            out uint serverProcessId);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr OpenSCManager(string machineName, string databaseName,
            uint desiredAccess);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr OpenService(IntPtr serviceManager, string serviceName,
            uint desiredAccess);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool QueryServiceStatusEx(IntPtr service, int infoLevel,
            ref ServiceStatusProcess status, int bufferSize, out int bytesNeeded);

        [DllImport("advapi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseServiceHandle(IntPtr handle);

        private static byte[] ReadExactly(Stream stream, int length, int timeoutMilliseconds)
        {
            byte[] result = new byte[length];
            int offset = 0;
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMilliseconds);
            while (offset < length)
            {
                IAsyncResult pending = stream.BeginRead(result, offset, length - offset, null, null);
                int remaining = Math.Max(1, (int)(deadline - DateTime.UtcNow).TotalMilliseconds);
                if (!WaitForCompletion(pending, remaining))
                    throw new System.TimeoutException("Traymetry sensor response timed out.");
                int count = stream.EndRead(pending);
                if (count <= 0)
                    throw new EndOfStreamException();
                offset += count;
            }
            return result;
        }

        private static bool WaitForCompletion(IAsyncResult pending, int timeoutMilliseconds)
        {
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMilliseconds);
            while (!pending.IsCompleted)
            {
                int remaining = (int)(deadline - DateTime.UtcNow).TotalMilliseconds;
                if (remaining <= 0)
                    return pending.IsCompleted;
                Thread.Sleep(Math.Min(10, remaining));
            }
            return true;
        }
    }

    internal sealed class TraymetrySensorService : ServiceBase
    {
        internal const string ServiceNameValue = "TraymetrySensorHost";
        private readonly object _snapshotLock = new object();
        private readonly ManualResetEvent _stopping = new ManualResetEvent(false);
        private Thread _sampleThread;
        private Thread _pipeThread;
        private SensorSnapshot _latestSnapshot;
        private DateTime _latestSnapshotAt = DateTime.MinValue;
        private IFrameTelemetryRunner _frameTelemetryRunner;

        internal TraymetrySensorService()
        {
            ServiceName = ServiceNameValue;
            CanStop = true;
            CanShutdown = true;
            AutoLog = false;
        }

        internal static void RunService()
        {
            ServiceBase.Run(new TraymetrySensorService());
        }

        protected override void OnStart(string[] args)
        {
            _frameTelemetryRunner = new PresentMonStdoutRunner();
            _sampleThread = new Thread(SampleLoop) { IsBackground = true, Name = "Traymetry sensor sampler" };
            _pipeThread = new Thread(PipeLoop) { IsBackground = true, Name = "Traymetry sensor pipe" };
            _sampleThread.Start();
            _pipeThread.Start();
        }

        protected override void OnStop()
        {
            StopThreads();
        }

        protected override void OnShutdown()
        {
            StopThreads();
            base.OnShutdown();
        }

        private void StopThreads()
        {
            _stopping.Set();
            PokePipe();
            if (_pipeThread != null && _pipeThread.IsAlive)
                _pipeThread.Join(3000);
            if (_sampleThread != null && _sampleThread.IsAlive)
                _sampleThread.Join(3000);
            IFrameTelemetryRunner runner = _frameTelemetryRunner;
            _frameTelemetryRunner = null;
            if (runner != null)
                runner.Dispose();
        }

        private void SampleLoop()
        {
            while (!_stopping.WaitOne(0))
            {
                try
                {
                    SensorServiceTrace.WriteStage("Opening CPU and GPU sensors.");
                    using (HardwareTelemetrySession session = new HardwareTelemetrySession(false))
                    {
                        bool firstSample = true;
                        while (!_stopping.WaitOne(0))
                        {
                            if (firstSample)
                                SensorServiceTrace.WriteStage("Reading the first sensor snapshot.");
                            SensorSnapshot snapshot = session.ReadSnapshot();
                            lock (_snapshotLock)
                            {
                                _latestSnapshot = snapshot;
                                _latestSnapshotAt = DateTime.UtcNow;
                            }
                            SensorServiceTrace.Clear();
                            firstSample = false;
                            if (_stopping.WaitOne(750))
                                return;
                        }
                    }
                }
                catch (Exception error)
                {
                    SensorServiceTrace.Write(error);
                    lock (_snapshotLock)
                    {
                        _latestSnapshot = null;
                        _latestSnapshotAt = DateTime.MinValue;
                    }
                    if (_stopping.WaitOne(1500))
                        return;
                }
            }
        }

        private void PipeLoop()
        {
            while (!_stopping.WaitOne(0))
            {
                try
                {
                    using (NamedPipeServerStream pipe = CreatePipe())
                    {
                        IAsyncResult pending = pipe.BeginWaitForConnection(null, null);
                        while (!pending.IsCompleted)
                        {
                            if (_stopping.WaitOne(100))
                                return;
                        }
                        if (_stopping.WaitOne(0))
                            return;
                        pipe.EndWaitForConnection(pending);
                        Serve(pipe);
                    }
                }
                catch (IOException error) { SensorServiceTrace.WritePipe(error); }
                catch (ObjectDisposedException error) { SensorServiceTrace.WritePipe(error); }
                catch (UnauthorizedAccessException error) { SensorServiceTrace.WritePipe(error); }
                catch (ArgumentException)
                {
                    if (_stopping.WaitOne(1000))
                        return;
                }
                catch (Exception error)
                {
                    SensorServiceTrace.WritePipe(error);
                    if (_stopping.WaitOne(1000))
                        return;
                }
            }
        }

        private NamedPipeServerStream CreatePipe()
        {
            PipeSecurity security = new PipeSecurity();
            security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new PipeAccessRule(
                new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                PipeAccessRights.FullControl, AccessControlType.Allow));
            security.AddAccessRule(new PipeAccessRule(
                new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                PipeAccessRights.FullControl, AccessControlType.Allow));
            security.AddAccessRule(new PipeAccessRule(
                new SecurityIdentifier(WellKnownSidType.NetworkSid, null),
                PipeAccessRights.FullControl, AccessControlType.Deny));
            security.AddAccessRule(new PipeAccessRule(
                new SecurityIdentifier(WellKnownSidType.WorldSid, null),
                PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize,
                AccessControlType.Allow));

            return new NamedPipeServerStream(
                SensorPipeProtocol.PipeName,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous,
                8192,
                8192,
                security);
        }

        private void Serve(Stream pipe)
        {
            using (BinaryWriter writer = new BinaryWriter(pipe, System.Text.Encoding.UTF8, true))
            {
                byte[] request = new byte[1];
                IAsyncResult pending = pipe.BeginRead(request, 0, 1, null, null);
                if (!WaitForRequest(pending, 1000))
                    return;
                int requestLength = pipe.EndRead(pending);
                if (requestLength != 1 || (request[0] != 1 && request[0] != 2))
                {
                    writer.Write((byte)0);
                    writer.Write(0);
                    writer.Flush();
                    return;
                }

                bool version2 = request[0] == 2;
                bool frameTelemetryDemand = false;
                if (version2)
                {
                    byte[] demand = new byte[1];
                    pending = pipe.BeginRead(demand, 0, 1, null, null);
                    if (!WaitForRequest(pending, 1000))
                        return;
                    if (pipe.EndRead(pending) != 1)
                        return;
                    frameTelemetryDemand = demand[0] != 0;
                }

                SensorSnapshot snapshot = null;
                lock (_snapshotLock)
                {
                    if (_latestSnapshot != null &&
                        (DateTime.UtcNow - _latestSnapshotAt).TotalSeconds <= 4)
                        snapshot = _latestSnapshot;
                }
                if (snapshot == null)
                {
                    writer.Write((byte)0);
                    writer.Write(0);
                    writer.Flush();
                    return;
                }

                if (version2)
                {
                    snapshot = CloneSnapshot(snapshot);
                    PopulateFrameTelemetry(snapshot, frameTelemetryDemand, DateTime.UtcNow);
                }

                byte[] payload;
                using (MemoryStream buffer = new MemoryStream())
                {
                    using (BinaryWriter payloadWriter = new BinaryWriter(buffer, System.Text.Encoding.UTF8, true))
                    {
                        if (version2)
                            SensorPipeProtocol.WriteSnapshotV2(payloadWriter, snapshot);
                        else
                            SensorPipeProtocol.WriteSnapshot(payloadWriter, snapshot);
                        payloadWriter.Flush();
                    }
                    payload = buffer.ToArray();
                }
                if (payload.Length > 64 * 1024)
                    throw new InvalidDataException("Traymetry sensor payload is too large.");
                writer.Write((byte)1);
                writer.Write(payload.Length);
                writer.Write(payload);
                writer.Flush();
            }
        }

        private void PopulateFrameTelemetry(SensorSnapshot snapshot, bool demand, DateTime utcNow)
        {
            IFrameTelemetryRunner runner = _frameTelemetryRunner;
            if (runner == null)
            {
                SetEmptyFrameTelemetry(snapshot, FrameTelemetryRunnerState.Faulted);
                return;
            }

            runner.SetDemand(demand, utcNow);
            FrameTelemetrySnapshot[] frames = runner.GetSnapshots(utcNow,
                SensorPipeProtocol.MaximumFrameProcesses);
            snapshot.FrameTelemetryState = (int)runner.State;
            snapshot.FrameProcessIds = frames.Select(item => item.ProcessId).ToArray();
            snapshot.FrameProcessNames = frames.Select(item => item.ProcessName ?? String.Empty).ToArray();
            snapshot.FrameStatuses = frames.Select(item => (int)item.Status).ToArray();
            snapshot.FrameDisplayedFps = frames.Select(item => item.DisplayedFps).ToArray();
            snapshot.FramePresentedFps = frames.Select(item => item.PresentedFps).ToArray();
            snapshot.FrameApplicationFps = frames.Select(item => item.ApplicationFps).ToArray();
            snapshot.FrameTimeMs = frames.Select(item => item.FrameTimeMs).ToArray();
            snapshot.FrameOnePercentLowFps = frames.Select(item => item.OnePercentLowFps).ToArray();
        }

        private static void SetEmptyFrameTelemetry(SensorSnapshot snapshot,
            FrameTelemetryRunnerState state)
        {
            snapshot.FrameTelemetryState = (int)state;
            snapshot.FrameProcessIds = new int[0];
            snapshot.FrameProcessNames = new string[0];
            snapshot.FrameStatuses = new int[0];
            snapshot.FrameDisplayedFps = new double[0];
            snapshot.FramePresentedFps = new double[0];
            snapshot.FrameApplicationFps = new double[0];
            snapshot.FrameTimeMs = new double[0];
            snapshot.FrameOnePercentLowFps = new double[0];
        }

        private static SensorSnapshot CloneSnapshot(SensorSnapshot source)
        {
            return new SensorSnapshot
            {
                CpuName = source.CpuName,
                Temperature = source.Temperature,
                Usage = source.Usage,
                ClockMhz = source.ClockMhz,
                PowerWatts = source.PowerWatts,
                GpuName = source.GpuName,
                GpuTemperature = source.GpuTemperature,
                GpuUsage = source.GpuUsage,
                GpuClockMhz = source.GpuClockMhz,
                GpuPowerWatts = source.GpuPowerWatts,
                GpuMemoryUsedGb = source.GpuMemoryUsedGb,
                GpuMemoryTotalGb = source.GpuMemoryTotalGb,
                NetworkDownloadKbps = source.NetworkDownloadKbps,
                NetworkUploadKbps = source.NetworkUploadKbps,
                MemoryUsedGb = source.MemoryUsedGb,
                MemoryTotalGb = source.MemoryTotalGb,
                MemoryClockMhz = source.MemoryClockMhz,
                StorageUsedGb = source.StorageUsedGb,
                StorageTotalGb = source.StorageTotalGb,
                StorageDriveNames = source.StorageDriveNames,
                StorageDriveUsedGb = source.StorageDriveUsedGb,
                StorageDriveTotalGb = source.StorageDriveTotalGb,
                FanNames = source.FanNames,
                FanRpm = source.FanRpm,
                FanControlPercent = source.FanControlPercent
            };
        }

        private bool WaitForRequest(IAsyncResult pending, int timeoutMilliseconds)
        {
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMilliseconds);
            while (!pending.IsCompleted)
            {
                int remaining = (int)(deadline - DateTime.UtcNow).TotalMilliseconds;
                if (remaining <= 0)
                    return pending.IsCompleted;
                if (_stopping.WaitOne(Math.Min(25, remaining)))
                    return false;
            }
            return true;
        }

        private static void PokePipe()
        {
            try
            {
                using (NamedPipeClientStream pipe = new NamedPipeClientStream(".", SensorPipeProtocol.PipeName, PipeDirection.Out))
                    pipe.Connect(100);
            }
            catch { }
        }
    }

    internal static class SensorServiceTrace
    {
        private const int MaximumMessageLength = 16 * 1024;

        private static string FilePath
        {
            get { return Path.Combine(SensorServiceInstaller.HostDirectory, "sensor-service.log"); }
        }

        private static string PipeFilePath
        {
            get { return Path.Combine(SensorServiceInstaller.HostDirectory, "sensor-pipe.log"); }
        }

        internal static void WriteStage(string stage)
        {
            WriteText(FilePath, DateTime.UtcNow.ToString("O") + Environment.NewLine +
                "Stage: " + (stage ?? "Unknown"));
        }

        internal static void Write(Exception error)
        {
            WriteText(FilePath, DateTime.UtcNow.ToString("O") + Environment.NewLine +
                (error == null ? "Unknown sensor service error." : error.ToString()));
        }

        internal static void WritePipe(Exception error)
        {
            WriteText(PipeFilePath, DateTime.UtcNow.ToString("O") + Environment.NewLine +
                (error == null ? "Unknown sensor pipe error." : error.ToString()));
        }

        internal static void Clear()
        {
            try
            {
                if (File.Exists(FilePath))
                    File.Delete(FilePath);
            }
            catch { }
        }

        private static void WriteText(string path, string message)
        {
            try
            {
                if (message.Length > MaximumMessageLength)
                    message = message.Substring(0, MaximumMessageLength);
                File.WriteAllText(path, message, System.Text.Encoding.UTF8);
            }
            catch { }
        }
    }

    internal static class MachineBootstrap
    {
        private const string SetupArgument = "--setup-machine";
        private const string UninstallArgument = "--uninstall-machine";

        internal static bool IsSetupArgument(string argument)
        {
            return String.Equals(argument, SetupArgument, StringComparison.OrdinalIgnoreCase);
        }

        internal static bool IsUninstallArgument(string argument)
        {
            return String.Equals(argument, UninstallArgument, StringComparison.OrdinalIgnoreCase);
        }

        internal static bool EnsureReady()
        {
            if (PawnIoBootstrap.IsInstalled && SensorServiceInstaller.IsCurrentAndRunning())
                return true;

            DialogResult answer = MessageBox.Show(
                "Для точных показателей CPU Traymetry один раз установит подписанный драйвер PawnIO и собственный локальный сервис датчиков.\r\n\r\n" +
                "Сервис отдаёт обычному окну только готовые значения температуры, частоты и мощности. Низкоуровневый доступ к оборудованию остаётся закрыт для обычных программ.\r\n\r\n" +
                "Windows сейчас покажет один запрос UAC. Продолжить?",
                "Traymetry — настройка датчиков",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Information,
                MessageBoxDefaultButton.Button1);
            if (answer != DialogResult.Yes)
                return false;

            return RequestElevatedSetup();
        }

        internal static bool RequestRepair()
        {
            return RequestElevatedSetup();
        }

        private static bool RequestElevatedSetup()
        {
            try
            {
                ProcessStartInfo start = new ProcessStartInfo
                {
                    FileName = Process.GetCurrentProcess().MainModule.FileName,
                    Arguments = SetupArgument,
                    UseShellExecute = true,
                    Verb = "runas",
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                using (Process process = Process.Start(start))
                {
                    if (process == null)
                        throw new InvalidOperationException("Не удалось запустить настройку датчиков.");
                    process.WaitForExit();
                    if (process.ExitCode != 0 && process.ExitCode != 3010)
                        throw new InvalidOperationException("Настройка датчиков завершилась с кодом " + process.ExitCode + ".");
                }

                for (int attempt = 0; attempt < 40; attempt++)
                {
                    if (PawnIoBootstrap.IsInstalled && SensorServiceInstaller.IsCurrentAndRunning())
                        return true;
                    Thread.Sleep(250);
                }
                throw new InvalidOperationException("Сервис датчиков не запустился вовремя.");
            }
            catch (Win32Exception error)
            {
                if (error.NativeErrorCode != 1223)
                    ShowError(error.Message);
                return false;
            }
            catch (Exception error)
            {
                ShowError(error.Message);
                return false;
            }
        }

        internal static int RunElevatedSetup()
        {
            try
            {
                if (!IsAdministrator())
                    return 5;
                int driverResult = PawnIoBootstrap.InstallAsAdministrator();
                if (driverResult != 0 && driverResult != 3010)
                    return driverResult;
                SensorServiceInstaller.InstallOrUpdate(Process.GetCurrentProcess().MainModule.FileName);
                return driverResult;
            }
            catch
            {
                return 1;
            }
        }

        internal static bool RequestUninstall()
        {
            try
            {
                ProcessStartInfo start = new ProcessStartInfo
                {
                    FileName = Process.GetCurrentProcess().MainModule.FileName,
                    Arguments = UninstallArgument,
                    UseShellExecute = true,
                    Verb = "runas",
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                using (Process process = Process.Start(start))
                {
                    if (process == null)
                        return false;
                    process.WaitForExit();
                    return process.ExitCode == 0;
                }
            }
            catch (Win32Exception error)
            {
                if (error.NativeErrorCode != 1223)
                    ShowError(error.Message);
                return false;
            }
        }

        internal static int RunElevatedUninstall()
        {
            try
            {
                if (!IsAdministrator())
                    return 5;
                SensorServiceInstaller.Uninstall();
                return 0;
            }
            catch
            {
                return 1;
            }
        }

        private static bool IsAdministrator()
        {
            using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }

        private static void ShowError(string details)
        {
            MessageBox.Show(
                "Не удалось настроить сервис датчиков. Traymetry продолжит работу, но часть показателей CPU может быть недоступна.\r\n\r\n" + details,
                "Traymetry",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    internal static class SensorServiceInstaller
    {
        private const uint ScManagerConnect = 0x0001;
        private const uint ScManagerCreateService = 0x0002;
        private const uint ServiceQueryStatus = 0x0004;
        private const uint ServiceStart = 0x0010;
        private const uint DeleteAccess = 0x00010000;
        private const uint ServiceChangeConfig = 0x0002;
        private const uint ServiceWin32OwnProcess = 0x00000010;
        private const uint ServiceAutoStart = 0x00000002;
        private const uint ServiceErrorNormal = 0x00000001;
        private const uint ServiceNoChange = 0xFFFFFFFF;
        private const int ErrorServiceDoesNotExist = 1060;
        private const int ErrorServiceAlreadyRunning = 1056;
        private const int ServiceConfigDescription = 1;
        private const string HostFileName = "Traymetry.SensorHost.exe";

        internal static string HostDirectory
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Traymetry"); }
        }

        internal static string HostPath
        {
            get { return Path.Combine(HostDirectory, HostFileName); }
        }

        internal static bool IsCurrentAndRunning()
        {
            try
            {
                string current = Process.GetCurrentProcess().MainModule.FileName;
                if (!File.Exists(HostPath) || !HashesMatch(current, HostPath))
                    return false;
                using (ServiceController controller = new ServiceController(TraymetrySensorService.ServiceNameValue))
                {
                    if (controller.Status != ServiceControllerStatus.Running)
                        return false;
                }
                SensorSnapshot snapshot;
                return SensorServiceClient.TryReadSnapshot(out snapshot) && snapshot != null;
            }
            catch { return false; }
        }

        internal static void InstallOrUpdate(string sourceExecutable)
        {
            StopIfInstalled();

            string directory = Path.GetDirectoryName(HostPath);
            EnsureSecureDirectory(directory, true);
            if (!String.Equals(Path.GetFullPath(sourceExecutable), Path.GetFullPath(HostPath), StringComparison.OrdinalIgnoreCase))
                File.Copy(sourceExecutable, HostPath, true);
            HardenExecutableFile(HostPath);
            PresentMonDependency.InstallOrVerify();

            IntPtr manager = OpenSCManager(null, null, ScManagerConnect | ScManagerCreateService);
            if (manager == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                string command = "\"" + HostPath + "\" --sensor-service";
                IntPtr service = OpenService(manager, TraymetrySensorService.ServiceNameValue,
                    ServiceChangeConfig | ServiceStart | ServiceQueryStatus);
                if (service == IntPtr.Zero)
                {
                    int error = Marshal.GetLastWin32Error();
                    if (error != ErrorServiceDoesNotExist)
                        throw new Win32Exception(error);
                    service = CreateService(
                        manager,
                        TraymetrySensorService.ServiceNameValue,
                        "Traymetry Sensor Host",
                        ServiceChangeConfig | ServiceStart | ServiceQueryStatus,
                        ServiceWin32OwnProcess,
                        ServiceAutoStart,
                        ServiceErrorNormal,
                        command,
                        null,
                        IntPtr.Zero,
                        null,
                        null,
                        null);
                    if (service == IntPtr.Zero)
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                }
                else if (!ChangeServiceConfig(
                    service,
                    ServiceNoChange,
                    ServiceAutoStart,
                    ServiceNoChange,
                    command,
                    null,
                    IntPtr.Zero,
                    null,
                    null,
                    null,
                    "Traymetry Sensor Host"))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }

                try
                {
                    ServiceDescription description = new ServiceDescription
                    {
                        Description = "Безопасно предоставляет приложению Traymetry готовые показания датчиков оборудования."
                    };
                    ChangeServiceConfig2(service, ServiceConfigDescription, ref description);

                    if (!StartService(service, 0, null))
                    {
                        int error = Marshal.GetLastWin32Error();
                        if (error != ErrorServiceAlreadyRunning)
                            throw new Win32Exception(error);
                    }
                }
                finally
                {
                    CloseServiceHandle(service);
                }
            }
            finally
            {
                CloseServiceHandle(manager);
            }

            using (ServiceController controller = new ServiceController(TraymetrySensorService.ServiceNameValue))
                controller.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(20));
        }

        internal static void Uninstall()
        {
            StopIfInstalled();
            IntPtr manager = OpenSCManager(null, null, ScManagerConnect);
            if (manager == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                IntPtr service = OpenService(manager, TraymetrySensorService.ServiceNameValue, DeleteAccess);
                if (service != IntPtr.Zero)
                {
                    try
                    {
                        if (!DeleteService(service))
                            throw new Win32Exception(Marshal.GetLastWin32Error());
                    }
                    finally
                    {
                        CloseServiceHandle(service);
                    }
                }
                else
                {
                    int error = Marshal.GetLastWin32Error();
                    if (error != ErrorServiceDoesNotExist)
                        throw new Win32Exception(error);
                }
            }
            finally
            {
                CloseServiceHandle(manager);
            }

            if (Directory.Exists(HostDirectory))
            {
                if ((File.GetAttributes(HostDirectory) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Системный каталог Traymetry является точкой повторной обработки.");
                Directory.Delete(HostDirectory, true);
            }
        }

        internal static string CreateSecureTemporaryDirectory()
        {
            EnsureSecureDirectory(HostDirectory, true);
            string setupRoot = Path.Combine(HostDirectory, "Setup");
            EnsureSecureDirectory(setupRoot, false);
            for (int attempt = 0; attempt < 10; attempt++)
            {
                string path = Path.Combine(setupRoot, Guid.NewGuid().ToString("N"));
                try
                {
                    new DirectoryInfo(path).Create(BuildDirectorySecurity(false));
                    return path;
                }
                catch (IOException)
                {
                    if (attempt == 9)
                        throw;
                }
            }
            throw new IOException("Не удалось создать защищённый временный каталог Traymetry.");
        }

        internal static void EnsureSecureDirectory(string directory, bool usersCanRead)
        {
            DirectorySecurity security = BuildDirectorySecurity(usersCanRead);
            if (Directory.Exists(directory))
            {
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Системный каталог Traymetry не может быть точкой повторной обработки.");
                Directory.SetAccessControl(directory, security);
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Системный каталог Traymetry был подменён во время настройки.");
                return;
            }

            new DirectoryInfo(directory).Create(security);
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Созданный системный каталог Traymetry оказался точкой повторной обработки.");
        }

        private static DirectorySecurity BuildDirectorySecurity(bool usersCanRead)
        {
            SecurityIdentifier system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
            SecurityIdentifier administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;

            DirectorySecurity security = new DirectorySecurity();
            security.SetAccessRuleProtection(true, false);
            security.SetOwner(administrators);
            security.AddAccessRule(new FileSystemAccessRule(system, FileSystemRights.FullControl,
                inherit, PropagationFlags.None, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(administrators, FileSystemRights.FullControl,
                inherit, PropagationFlags.None, AccessControlType.Allow));
            if (usersCanRead)
            {
                SecurityIdentifier users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
                security.AddAccessRule(new FileSystemAccessRule(users, FileSystemRights.ReadAndExecute,
                    inherit, PropagationFlags.None, AccessControlType.Allow));
            }
            return security;
        }

        internal static void HardenExecutableFile(string path)
        {
            SecurityIdentifier system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
            SecurityIdentifier administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            SecurityIdentifier users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);

            FileSecurity security = new FileSecurity();
            security.SetAccessRuleProtection(true, false);
            security.SetOwner(administrators);
            security.AddAccessRule(new FileSystemAccessRule(system, FileSystemRights.FullControl,
                AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(administrators, FileSystemRights.FullControl,
                AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(users, FileSystemRights.ReadAndExecute,
                AccessControlType.Allow));
            File.SetAccessControl(path, security);
        }

        private static void StopIfInstalled()
        {
            try
            {
                using (ServiceController controller = new ServiceController(TraymetrySensorService.ServiceNameValue))
                {
                    ServiceControllerStatus status = controller.Status;
                    if (status != ServiceControllerStatus.Stopped && status != ServiceControllerStatus.StopPending)
                        controller.Stop();
                    if (controller.Status != ServiceControllerStatus.Stopped)
                        controller.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(20));
                }
            }
            catch (InvalidOperationException error)
            {
                Win32Exception native = error.InnerException as Win32Exception;
                if (native == null || native.NativeErrorCode != ErrorServiceDoesNotExist)
                    throw;
            }
        }

        private static bool HashesMatch(string first, string second)
        {
            if (String.Equals(Path.GetFullPath(first), Path.GetFullPath(second), StringComparison.OrdinalIgnoreCase))
                return true;
            using (SHA256 sha = SHA256.Create())
            using (FileStream left = File.OpenRead(first))
            using (FileStream right = File.OpenRead(second))
            {
                byte[] leftHash = sha.ComputeHash(left);
                sha.Initialize();
                byte[] rightHash = sha.ComputeHash(right);
                if (leftHash.Length != rightHash.Length)
                    return false;
                for (int index = 0; index < leftHash.Length; index++)
                {
                    if (leftHash[index] != rightHash[index])
                        return false;
                }
                return true;
            }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct ServiceDescription
        {
            [MarshalAs(UnmanagedType.LPWStr)]
            public string Description;
        }

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr OpenSCManager(string machineName, string databaseName, uint desiredAccess);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr OpenService(IntPtr manager, string serviceName, uint desiredAccess);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateService(
            IntPtr manager,
            string serviceName,
            string displayName,
            uint desiredAccess,
            uint serviceType,
            uint startType,
            uint errorControl,
            string binaryPathName,
            string loadOrderGroup,
            IntPtr tagId,
            string dependencies,
            string serviceStartName,
            string password);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ChangeServiceConfig(
            IntPtr service,
            uint serviceType,
            uint startType,
            uint errorControl,
            string binaryPathName,
            string loadOrderGroup,
            IntPtr tagId,
            string dependencies,
            string serviceStartName,
            string password,
            string displayName);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ChangeServiceConfig2(IntPtr service, int infoLevel, ref ServiceDescription info);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool StartService(IntPtr service, int argumentCount, string[] arguments);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeleteService(IntPtr service);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseServiceHandle(IntPtr handle);
    }
}
