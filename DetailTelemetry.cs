using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;

namespace Traymetry
{
    internal sealed class ProcessDetail
    {
        internal int ProcessId;
        internal string Name;
        internal double Value;
        internal double Secondary;

        internal static string NameOf(int id)
        {
            try
            {
                using (Process process = Process.GetProcessById(id))
                    return process.ProcessName;
            }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
            catch (Win32Exception) { }
            return "PID " + id.ToString(CultureInfo.InvariantCulture);
        }
    }

    internal sealed class DetailSample
    {
        internal ProcessDetail[] Rows = new ProcessDetail[0];
        internal string StatusKey;
        internal bool ConnectionCounts;
        internal double? CpuTotalPercent;
        internal double? GpuAdapterBytes;
        internal int GpuOtherProcessCount;
        internal double GpuOtherProcessBytes;
    }

    internal sealed class CpuDetailSampler
    {
        private struct Reading
        {
            internal long Started;
            internal long Time;
        }

        private Dictionary<int, Reading> _previous = new Dictionary<int, Reading>();
        private long _previousTimestamp;
        private long _previousWallTicks;
        private long _previousSystemTicks;
        private long _previousIdleTicks;
        // GetSystemTimes only sees the calling thread's processor group.
        private readonly bool _singleProcessorGroup = GetActiveProcessorGroupCount() == 1;
        private readonly uint _processorCount = GetActiveProcessorCount(0xFFFF);

        internal DetailSample Read()
        {
            long now = Stopwatch.GetTimestamp();
            long wallTicks = DateTime.UtcNow.Ticks;
            double elapsedTicks = (now - _previousTimestamp) *
                ((double)TimeSpan.TicksPerSecond / Stopwatch.Frequency);
            double capacity = elapsedTicks * (_processorCount > 0 ? (double)_processorCount : Environment.ProcessorCount);
            double? totalPercent = null;
            long idle, kernel, user;
            if (_singleProcessorGroup && GetSystemTimes(out idle, out kernel, out user))
            {
                long systemTicks = kernel + user; // Windows includes idle time in kernel time.
                long elapsedSystem = systemTicks - _previousSystemTicks;
                long elapsedIdle = idle - _previousIdleTicks;
                if (_previousSystemTicks > 0 && elapsedSystem > 0 && elapsedIdle >= 0 &&
                    elapsedIdle <= elapsedSystem)
                {
                    capacity = elapsedSystem;
                    totalPercent = (elapsedSystem - elapsedIdle) * 100.0 / elapsedSystem;
                }
                _previousSystemTicks = systemTicks;
                _previousIdleTicks = idle;
            }
            else
            {
                _previousSystemTicks = 0;
                _previousIdleTicks = 0;
            }
            Dictionary<int, Reading> current = new Dictionary<int, Reading>();
            List<ProcessDetail> active = new List<ProcessDetail>();
            foreach (Process process in Process.GetProcesses())
            {
                using (process)
                {
                    try
                    {
                        int id = process.Id;
                        if (id <= 0)
                            continue;
                        Reading sample = new Reading
                        {
                            Started = process.StartTime.ToUniversalTime().Ticks,
                            Time = process.TotalProcessorTime.Ticks
                        };
                        current[id] = sample;
                        Reading prior;
                        long delta = 0;
                        if (_previous.TryGetValue(id, out prior) && sample.Started == prior.Started)
                            delta = sample.Time - prior.Time;
                        else if (_previousWallTicks > 0 && sample.Started >= _previousWallTicks)
                            delta = sample.Time;
                        if (capacity > 0 && delta > 0)
                        {
                            active.Add(new ProcessDetail
                            {
                                Name = process.ProcessName,
                                Value = Math.Min(100, delta / capacity * 100)
                            });
                        }
                    }
                    catch (Win32Exception) { }
                    catch (InvalidOperationException) { }
                }
            }
            bool hasBaseline = _previousTimestamp != 0;
            _previous = current;
            _previousTimestamp = now;
            _previousWallTicks = wallTicks;
            return new DetailSample
            {
                Rows = active.OrderByDescending(row => row.Value).ThenBy(row => row.Name).Take(5).ToArray(),
                CpuTotalPercent = totalPercent,
                StatusKey = !hasBaseline ? "state.collecting" :
                    current.Count == 0 ? "detail.unavailable" : "detail.quiet"
            };
        }

        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
        [DllImport("kernel32.dll")]
        private static extern ushort GetActiveProcessorGroupCount();
        [DllImport("kernel32.dll")]
        private static extern uint GetActiveProcessorCount(ushort group);
    }

    internal sealed class GpuDetailSampler : IDisposable
    {
        private static readonly Regex Instance = new Regex(
            @"^(?:pid_(\d+)_)?luid_0x([0-9a-f]+)_0x([0-9a-f]+)_phys_\d+(?:#\d+)?$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private const uint MoreData = 0x800007D2;
        private IntPtr _query;
        private IntPtr _counter;
        private IntPtr _adapterCounter;

        internal GpuDetailSampler()
        {
            if (PdhOpenQuery(null, IntPtr.Zero, out _query) == 0)
            {
                PdhAddEnglishCounter(_query, @"\GPU Process Memory(*)\Dedicated Usage",
                    IntPtr.Zero, out _counter);
                PdhAddEnglishCounter(_query, @"\GPU Adapter Memory(*)\Dedicated Usage",
                    IntPtr.Zero, out _adapterCounter);
            }
        }

        internal DetailSample Read(string gpuName)
        {
            long luid;
            if (_counter == IntPtr.Zero || !TryFindAdapter(gpuName, out luid) ||
                PdhCollectQueryData(_query) != 0)
                return new DetailSample { StatusKey = "detail.unavailable" };

            Dictionary<int, double> usage = new Dictionary<int, double>();
            if (!ReadDedicatedUsage(_counter, luid, delegate(int pid, long value)
            {
                if (pid <= 0 || value <= 0)
                    return;
                double amount;
                usage.TryGetValue(pid, out amount);
                usage[pid] = amount + value;
            }))
                return new DetailSample { StatusKey = "detail.unavailable" };

            double? adapterBytes = null;
            if (_adapterCounter != IntPtr.Zero)
            {
                bool success = ReadDedicatedUsage(_adapterCounter, luid, delegate(int pid, long value)
                {
                    if (pid == 0)
                        adapterBytes = adapterBytes.GetValueOrDefault() + value;
                });
                if (!success)
                    adapterBytes = null;
            }

            // Shared window allocations can inflate DWM's process counter.
            // Keep it out of both the visible rows and their subtotal.
            foreach (Process compositor in Process.GetProcessesByName("dwm"))
                using (compositor)
                    usage.Remove(compositor.Id);

            ProcessDetail[] rows = usage.OrderByDescending(row => row.Value).ThenBy(row => row.Key)
                .Take(5).Select(row => new ProcessDetail { Name = ProcessDetail.NameOf(row.Key), Value = row.Value })
                .ToArray();
            return new DetailSample
            {
                Rows = rows,
                GpuAdapterBytes = adapterBytes,
                GpuOtherProcessCount = usage.Count - rows.Length,
                GpuOtherProcessBytes = usage.Values.Sum() - rows.Sum(row => row.Value),
                StatusKey = "detail.gpu.empty"
            };
        }

        private static bool ReadDedicatedUsage(IntPtr counter, long luid, Action<int, long> collect)
        {
            uint bytes = 0, count = 0;
            uint status = PdhGetRawCounterArray(counter, ref bytes, ref count, IntPtr.Zero);
            if (status != MoreData || bytes < 48 || bytes > 32 * 1024 * 1024)
                return false;

            IntPtr buffer = Marshal.AllocHGlobal((int)bytes);
            uint allocated = bytes;
            try
            {
                if (PdhGetRawCounterArray(counter, ref bytes, ref count, buffer) != 0 ||
                    count > allocated / 48)
                    return false;
                for (int index = 0; index < count; index++)
                {
                    IntPtr item = IntPtr.Add(buffer, index * 48);
                    int counterStatus = Marshal.ReadInt32(item, 8);
                    long value = Marshal.ReadInt64(item, 24);
                    if ((counterStatus != 0 && counterStatus != 1) || value < 0)
                        continue;
                    Match match = Instance.Match(Marshal.PtrToStringUni(Marshal.ReadIntPtr(item)) ?? "");
                    uint high, low;
                    int pid = 0;
                    if (!match.Success ||
                        (match.Groups[1].Success && !Int32.TryParse(match.Groups[1].Value, out pid)) ||
                        !UInt32.TryParse(match.Groups[2].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out high) ||
                        !UInt32.TryParse(match.Groups[3].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out low) ||
                        (((long)high << 32) | low) != luid)
                        continue;
                    collect(pid, value);
                }
                return true;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }

        private static bool TryFindAdapter(string name, out long luid)
        {
            luid = 0;
            if (String.IsNullOrWhiteSpace(name))
                return false;
            Guid iid = new Guid("7b7166ec-21c7-44ae-b21a-c9ae321ae369");
            IntPtr factory;
            if (CreateDXGIFactory(ref iid, out factory) < 0)
                return false;
            int matches = 0;
            try
            {
                EnumAdapters enumerate = (EnumAdapters)Marshal.GetDelegateForFunctionPointer(
                    ComMethod(factory, 7), typeof(EnumAdapters));
                for (uint index = 0; index < 32; index++)
                {
                    IntPtr adapter;
                    if (enumerate(factory, index, out adapter) < 0)
                        break;
                    try
                    {
                        GetDescription describe = (GetDescription)Marshal.GetDelegateForFunctionPointer(
                            ComMethod(adapter, 8), typeof(GetDescription));
                        AdapterDescription desc;
                        if (describe(adapter, out desc) >= 0 &&
                            String.Equals(name.Trim(), desc.Name.Trim(), StringComparison.OrdinalIgnoreCase))
                        {
                            matches++;
                            luid = desc.Luid;
                        }
                    }
                    finally { Marshal.Release(adapter); }
                }
            }
            finally { Marshal.Release(factory); }
            return matches == 1;
        }

        private static IntPtr ComMethod(IntPtr instance, int slot)
        {
            return Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * IntPtr.Size);
        }

        public void Dispose()
        {
            if (_query != IntPtr.Zero)
                PdhCloseQuery(_query);
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct AdapterDescription
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            internal string Name;
            internal uint Vendor, Device, Subsystem, Revision;
            internal UIntPtr VideoMemory, SystemMemory, SharedMemory;
            internal long Luid;
        }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int EnumAdapters(IntPtr factory, uint index, out IntPtr adapter);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int GetDescription(IntPtr adapter, out AdapterDescription description);
        [DllImport("dxgi.dll")]
        private static extern int CreateDXGIFactory(ref Guid iid, out IntPtr factory);
        [DllImport("pdh.dll", EntryPoint = "PdhOpenQueryW", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern uint PdhOpenQuery(string source, IntPtr userData, out IntPtr query);
        [DllImport("pdh.dll", EntryPoint = "PdhAddEnglishCounterW", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern uint PdhAddEnglishCounter(IntPtr query, string path, IntPtr userData, out IntPtr counter);
        [DllImport("pdh.dll")]
        private static extern uint PdhCollectQueryData(IntPtr query);
        [DllImport("pdh.dll", EntryPoint = "PdhGetRawCounterArrayW", ExactSpelling = true)]
        private static extern uint PdhGetRawCounterArray(IntPtr counter, ref uint bytes, ref uint count, IntPtr buffer);
        [DllImport("pdh.dll")]
        private static extern uint PdhCloseQuery(IntPtr query);
    }

    internal sealed class NetworkDetailSampler : IDisposable
    {
        private static readonly Guid TcpIp = new Guid("9a280ac0-c8e0-11d1-84e2-00c04fb998a2");
        private static readonly Guid UdpIp = new Guid("bf3a50c5-a9c9-4988-a005-2df0b7c80f80");
        private const string TraceName = "Traymetry.SensorHost.Network";
        private static readonly Guid TraceId = new Guid("6543e021-ef9f-49b8-b27e-6964a6a445a2");
        private const uint TraceNotFound = 4201;
        private volatile bool _disposing;
        private readonly object _gate = new object();
        private readonly EventCallback _callback;
        private Dictionary<int, Bytes> _traffic = new Dictionary<int, Bytes>();
        private ulong _session;
        private ulong _reader = UInt64.MaxValue;
        private Thread _consumer;
        private volatile string _error;
        private long _lastSample;

        private struct Bytes
        {
            internal long Received, Sent;
        }

        internal NetworkDetailSampler()
        {
            _callback = CountPacket;
            ulong session;
            uint status = StartSession(out session);
            if (status == 183) // ERROR_ALREADY_EXISTS
            {
                // Only reclaim our fixed session and GUID after an unclean host exit.
                IntPtr previous = AllocateProperties();
                try
                {
                    if (ControlTrace(0, TraceName, previous, 0) == 0 &&
                        (Guid)Marshal.PtrToStructure(IntPtr.Add(previous, 24), typeof(Guid)) == TraceId &&
                        StopSession(0) == 0)
                        status = StartSession(out session);
                }
                finally { Marshal.FreeHGlobal(previous); }
            }
            if (status != 0)
            {
                _error = status == 5 || status == 1314 ? "detail.network.denied" : "detail.unavailable";
                return;
            }
            _session = session;

            IntPtr name = Marshal.StringToHGlobalUni(TraceName);
            try
            {
                TraceLogfile logfile = new TraceLogfile();
                logfile.LoggerName = name;
                logfile.Mode = 0x10000100; // EVENT_RECORD | REAL_TIME
                logfile.Callback = Marshal.GetFunctionPointerForDelegate(_callback);
                _reader = OpenTrace(ref logfile);
            }
            finally { Marshal.FreeHGlobal(name); }
            if (_reader == UInt64.MaxValue)
            {
                _error = "detail.unavailable";
                Dispose();
                return;
            }

            _lastSample = Stopwatch.GetTimestamp();
            ulong reader = _reader;
            _consumer = new Thread(delegate()
            {
                ProcessTrace(new[] { reader }, 1, IntPtr.Zero, IntPtr.Zero);
                if (!_disposing)
                    _error = "detail.unavailable";
                GC.KeepAlive(_callback);
            });
            _consumer.IsBackground = true;
            _consumer.Start();
        }

        internal DetailSample ReadRates()
        {
            if (_error != null)
                return new DetailSample { StatusKey = _error };
            Dictionary<int, Bytes> interval;
            long now;
            lock (_gate)
            {
                interval = _traffic;
                _traffic = new Dictionary<int, Bytes>();
                now = Stopwatch.GetTimestamp();
            }
            double seconds = Math.Max(0.01, (now - _lastSample) / (double)Stopwatch.Frequency);
            _lastSample = now;
            return new DetailSample
            {
                Rows = interval.OrderByDescending(row => row.Value.Received + row.Value.Sent)
                    .ThenBy(row => row.Key).Select(row => new ProcessDetail
                    {
                        ProcessId = row.Key,
                        Value = row.Value.Received / seconds / 1024,
                        Secondary = row.Value.Sent / seconds / 1024
                    }).ToArray(),
                StatusKey = "detail.quiet"
            };
        }

        // Standard accounts can still see the owners of established TCP
        // connections when the system logger is unavailable.
        internal static DetailSample ReadConnectionCounts()
        {
            Dictionary<int, int> counts = new Dictionary<int, int>();
            bool ipv4 = CountConnections(2, 24, 0, 20, counts);
            bool ipv6 = CountConnections(23, 56, 48, 52, counts);
            if (!ipv4 && !ipv6)
                return new DetailSample { StatusKey = "detail.unavailable" };
            return new DetailSample
            {
                ConnectionCounts = true,
                Rows = counts.OrderByDescending(row => row.Value).ThenBy(row => row.Key)
                    .Take(5).Select(row => new ProcessDetail
                    {
                        Name = ProcessDetail.NameOf(row.Key),
                        Value = row.Value
                    }).ToArray(),
                StatusKey = counts.Count > 0 ? "detail.network.connections" : "detail.network.empty"
            };
        }

        private static bool CountConnections(int addressFamily, int rowSize, int stateOffset,
            int pidOffset, Dictionary<int, int> counts)
        {
            int size = 0;
            const int ownerPidConnections = 4;
            const uint insufficientBuffer = 122;
            if (GetExtendedTcpTable(IntPtr.Zero, ref size, false, addressFamily, ownerPidConnections, 0) !=
                insufficientBuffer)
                return false;
            for (int attempt = 0; attempt < 3 && size >= 4 && size <= 32 * 1024 * 1024; attempt++)
            {
                int capacity = size;
                IntPtr table = Marshal.AllocHGlobal(capacity);
                try
                {
                    uint status = GetExtendedTcpTable(table, ref size, false, addressFamily, ownerPidConnections, 0);
                    if (status == insufficientBuffer)
                        continue;
                    if (status != 0)
                        return false;
                    int count = Marshal.ReadInt32(table);
                    if (count < 0 || count > (capacity - 4) / rowSize)
                        return false;
                    for (int index = 0; index < count; index++)
                    {
                        int offset = 4 + index * rowSize;
                        if (Marshal.ReadInt32(table, offset + stateOffset) != 5) // MIB_TCP_STATE_ESTAB
                            continue;
                        int pid = Marshal.ReadInt32(table, offset + pidOffset);
                        if (pid <= 0)
                            continue;
                        int current;
                        counts.TryGetValue(pid, out current);
                        counts[pid] = current + 1;
                    }
                    return true;
                }
                finally { Marshal.FreeHGlobal(table); }
            }
            return false;
        }

        private void CountPacket(ref EventRecord record)
        {
            if (record.Provider != TcpIp && record.Provider != UdpIp ||
                record.PayloadLength < 8 || record.Payload == IntPtr.Zero)
                return;
            bool receive = record.Opcode == 11 || record.Opcode == 27;
            if (!receive && record.Opcode != 10 && record.Opcode != 26)
                return;
            int pid = Marshal.ReadInt32(record.Payload);
            uint bytes = unchecked((uint)Marshal.ReadInt32(record.Payload, 4));
            if (pid <= 0 || bytes == 0)
                return;
            lock (_gate)
            {
                Bytes total;
                if (!_traffic.TryGetValue(pid, out total) && _traffic.Count >= 4096)
                    return;
                if (receive)
                    total.Received += bytes;
                else
                    total.Sent += bytes;
                _traffic[pid] = total;
            }
        }

        public void Dispose()
        {
            _disposing = true;
            if (_session != 0)
            {
                uint status = StopSession(_session);
                if (status != 0 && status != TraceNotFound)
                    SensorServiceTrace.WritePipe(new Win32Exception((int)status, "Stop Traymetry network trace"));
                _session = 0;
            }
            if (_reader != UInt64.MaxValue)
            {
                CloseTrace(_reader);
                _reader = UInt64.MaxValue;
            }
            if (_consumer != null && !_consumer.Join(1500))
                SensorServiceTrace.WritePipe(new TimeoutException("Closing the Traymetry network trace reader."));
        }

        private static uint StartSession(out ulong session)
        {
            IntPtr properties = AllocateProperties();
            try { return StartTrace(out session, TraceName, properties); }
            finally { Marshal.FreeHGlobal(properties); }
        }

        private static uint StopSession(ulong session)
        {
            IntPtr properties = AllocateProperties();
            try { return ControlTrace(session, TraceName, properties, 1); }
            finally { Marshal.FreeHGlobal(properties); }
        }

        // The native layouts are from the Windows SDK x64 headers.
        private static IntPtr AllocateProperties()
        {
            const int propertiesSize = 120;
            int size = propertiesSize + 1024;
            IntPtr buffer = Marshal.AllocHGlobal(size);
            Marshal.Copy(new byte[size], 0, buffer, size);
            Marshal.WriteInt32(buffer, 0, size);          // Wnode.BufferSize
            Marshal.Copy(TraceId.ToByteArray(), 0, IntPtr.Add(buffer, 24), 16); // Wnode.Guid
            Marshal.WriteInt32(buffer, 40, 1);            // Wnode.ClientContext (QPC)
            Marshal.WriteInt32(buffer, 44, 0x00020000);   // WNODE_FLAG_TRACED_GUID
            Marshal.WriteInt32(buffer, 48, 64);           // BufferSize in KiB
            Marshal.WriteInt32(buffer, 64, 0x02000100);   // SYSTEM_LOGGER_MODE | REAL_TIME
            Marshal.WriteInt32(buffer, 68, 1);            // FlushTimer
            Marshal.WriteInt32(buffer, 72, 0x00010000);   // EVENT_TRACE_FLAG_NETWORK_TCPIP
            Marshal.WriteInt32(buffer, 116, propertiesSize);
            return buffer;
        }

        [StructLayout(LayoutKind.Explicit, Size = 448)]
        private struct TraceLogfile
        {
            [FieldOffset(8)] internal IntPtr LoggerName;
            [FieldOffset(28)] internal int Mode;
            [FieldOffset(424)] internal IntPtr Callback;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct EventRecord
        {
            [FieldOffset(24)] internal Guid Provider;
            [FieldOffset(45)] internal byte Opcode;
            [FieldOffset(86)] internal ushort PayloadLength;
            [FieldOffset(96)] internal IntPtr Payload;
        }

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate void EventCallback(ref EventRecord record);
        [DllImport("advapi32.dll", EntryPoint = "StartTraceW", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern uint StartTrace(out ulong handle, string name, IntPtr properties);
        [DllImport("advapi32.dll", EntryPoint = "ControlTraceW", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern uint ControlTrace(ulong handle, string name, IntPtr properties, uint code);
        [DllImport("advapi32.dll", EntryPoint = "OpenTraceW", ExactSpelling = true)]
        private static extern ulong OpenTrace(ref TraceLogfile logfile);
        [DllImport("advapi32.dll")]
        private static extern uint ProcessTrace(ulong[] handles, uint count, IntPtr start, IntPtr end);
        [DllImport("advapi32.dll")]
        private static extern uint CloseTrace(ulong handle);
        [DllImport("iphlpapi.dll")]
        private static extern uint GetExtendedTcpTable(IntPtr table, ref int size,
            [MarshalAs(UnmanagedType.Bool)] bool order, int family, int tableClass, int reserved);
    }
}
