using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.NetworkInformation;
using Microsoft.Win32;
using LibreHardwareMonitor.Hardware;

namespace Traymetry
{
    /// <summary>
    /// Vendor-independent telemetry backed by LibreHardwareMonitor and Windows APIs.
    /// The regular UI reads privileged CPU values from Traymetry's read-only local sensor service.
    /// </summary>
    internal sealed class HardwareTelemetrySession : IDisposable
    {
        private Computer _computer;
        private readonly bool _allowSensorService;
        private SensorSnapshot _initialServiceSnapshot;
        private Dictionary<string, long> _lastBytesReceived = new Dictionary<string, long>(StringComparer.Ordinal);
        private Dictionary<string, long> _lastBytesSent = new Dictionary<string, long>(StringComparer.Ordinal);
        private readonly HashSet<string> _observedFanKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private DateTime _lastNetworkSampleAt = DateTime.MinValue;

        public HardwareTelemetrySession()
            : this(true)
        {
        }

        internal HardwareTelemetrySession(bool allowSensorService)
        {
            _allowSensorService = allowSensorService;
            if (_allowSensorService && SensorServiceClient.TryReadSnapshot(out _initialServiceSnapshot))
                return;
            OpenLocalComputer();
        }

        private void OpenLocalComputer()
        {
            if (_computer != null)
                return;
            Computer computer = new Computer
            {
                IsCpuEnabled = true,
                IsGpuEnabled = true,
                IsMotherboardEnabled = true,
                IsControllerEnabled = true,
                // Traymetry reads these values through lightweight Windows APIs below.
                // Asking LibreHardwareMonitor to enumerate the same device classes is
                // redundant and can stall a Session 0 sensor service on some systems.
                IsMemoryEnabled = false,
                IsStorageEnabled = false,
                IsNetworkEnabled = false
            };
            try
            {
                computer.Open();
                _computer = computer;
            }
            catch
            {
                try { computer.Close(); }
                catch { }
                throw;
            }
        }

        public SensorSnapshot ReadSnapshot()
        {
            if (_initialServiceSnapshot != null)
            {
                SensorSnapshot initial = _initialServiceSnapshot;
                _initialServiceSnapshot = null;
                return initial;
            }

            SensorSnapshot serviceSnapshot;
            if (_allowSensorService && SensorServiceClient.TryReadSnapshot(out serviceSnapshot))
                return serviceSnapshot;

            OpenLocalComputer();
            UpdateHardwareTree();

            SensorSnapshot snapshot = new SensorSnapshot();
            IHardware cpu = FindHardware(HardwareType.Cpu);
            IHardware gpu = FindPreferredGpu();

            FillCpu(snapshot, cpu);
            FillGpu(snapshot, gpu);
            FillFans(snapshot);
            FillNetwork(snapshot);
            SystemTelemetry.Fill(snapshot);
            if (String.IsNullOrWhiteSpace(snapshot.CpuName))
                snapshot.CpuName = ReadProcessorName();
            if (snapshot.ClockMhz <= 0)
                snapshot.ClockMhz = ReadProcessorClock();
            return snapshot;
        }

        private void UpdateHardwareTree()
        {
            if (_computer == null)
                return;
            foreach (IHardware hardware in EnumerateHardware(_computer.Hardware))
            {
                try { hardware.Update(); }
                catch { }
            }
        }

        private IHardware FindHardware(HardwareType type)
        {
            return EnumerateHardware(_computer.Hardware)
                .FirstOrDefault(delegate(IHardware hardware) { return hardware.HardwareType == type; });
        }

        private IHardware FindPreferredGpu()
        {
            IHardware[] hardware = EnumerateHardware(_computer.Hardware).ToArray();
            foreach (HardwareType type in new[] { HardwareType.GpuNvidia, HardwareType.GpuAmd, HardwareType.GpuIntel })
            {
                IHardware gpu = hardware.FirstOrDefault(delegate(IHardware item) { return item.HardwareType == type; });
                if (gpu != null)
                    return gpu;
            }
            return null;
        }

        private static IEnumerable<IHardware> EnumerateHardware(IEnumerable<IHardware> roots)
        {
            foreach (IHardware hardware in roots)
            {
                yield return hardware;
                foreach (IHardware child in EnumerateHardware(hardware.SubHardware))
                    yield return child;
            }
        }

        private static IEnumerable<ISensor> EnumerateSensors(IHardware hardware)
        {
            if (hardware == null)
                yield break;
            foreach (IHardware item in EnumerateHardware(new[] { hardware }))
            {
                foreach (ISensor sensor in item.Sensors)
                    yield return sensor;
            }
        }

        private static void FillCpu(SensorSnapshot snapshot, IHardware cpu)
        {
            snapshot.CpuName = cpu != null ? cpu.Name : ReadProcessorName();
            ISensor[] sensors = EnumerateSensors(cpu).ToArray();
            snapshot.Temperature = PreferredValue(sensors, SensorType.Temperature,
                new[] { "CPU Package", "CPU Average", "Core Average" }, true, 0, 150);
            snapshot.Usage = PreferredValue(sensors, SensorType.Load,
                new[] { "CPU Total", "CPU Core Max" }, false, 0, 100);
            if (snapshot.Usage <= 0)
                snapshot.Usage = AverageValue(sensors, SensorType.Load, "CPU Core", 0, 100);

            snapshot.ClockMhz = AverageValue(sensors, SensorType.Clock, "CPU Core", 0, 100000);
            if (snapshot.ClockMhz <= 0)
                snapshot.ClockMhz = PreferredValue(sensors, SensorType.Clock,
                    new[] { "CPU Core #1", "CPU Core" }, false, 0, 100000);
            snapshot.PowerWatts = PreferredValue(sensors, SensorType.Power,
                new[] { "CPU Package", "CPU Cores" }, true, 0, 5000);
        }

        private static void FillGpu(SensorSnapshot snapshot, IHardware gpu)
        {
            if (gpu == null)
                return;
            snapshot.GpuName = gpu.Name;
            ISensor[] sensors = EnumerateSensors(gpu).ToArray();
            snapshot.GpuTemperature = PreferredValue(sensors, SensorType.Temperature,
                new[] { "GPU Core", "GPU Hot Spot", "GPU Temperature" }, true, 0, 150);
            snapshot.GpuUsage = PreferredValue(sensors, SensorType.Load,
                new[] { "GPU Core", "D3D 3D" }, false, 0, 100);
            snapshot.GpuClockMhz = PreferredValue(sensors, SensorType.Clock,
                new[] { "GPU Core" }, false, 0, 100000);
            snapshot.GpuPowerWatts = PreferredValue(sensors, SensorType.Power,
                new[] { "GPU Package", "GPU Power" }, true, 0, 5000);

            double memoryUsedMb = PreferredValue(sensors, SensorType.SmallData,
                new[] { "GPU Memory Used", "GPU Memory Dedicated" }, false, 0, Double.MaxValue);
            double memoryTotalMb = PreferredValue(sensors, SensorType.SmallData,
                new[] { "GPU Memory Total" }, false, 0, Double.MaxValue);
            snapshot.GpuMemoryUsedGb = memoryUsedMb > 0 ? memoryUsedMb / 1024.0 : 0;
            snapshot.GpuMemoryTotalGb = memoryTotalMb > 0 ? memoryTotalMb / 1024.0 : 0;
        }

        private void FillFans(SensorSnapshot snapshot)
        {
            List<string> names = new List<string>();
            List<double> speeds = new List<double>();
            List<double> controls = new List<double>();
            if (_computer == null)
            {
                snapshot.FanNames = names.ToArray();
                snapshot.FanRpm = speeds.ToArray();
                snapshot.FanControlPercent = controls.ToArray();
                return;
            }

            foreach (IHardware hardware in EnumerateHardware(_computer.Hardware))
            {
                ISensor[] hardwareSensors = hardware.Sensors.ToArray();
                ISensor[] fanSensors = hardwareSensors
                    .Where(delegate(ISensor sensor) { return sensor.SensorType == SensorType.Fan; })
                    .ToArray();
                ISensor[] controlSensors = hardwareSensors
                    .Where(delegate(ISensor sensor) { return sensor.SensorType == SensorType.Control; })
                    .ToArray();

                foreach (ISensor fan in fanSensors)
                {
                    ISensor control = FindFanControl(fan, controlSensors);
                    double rpm = NonNegativeValue(fan.Value, 100000);
                    string fanKey = (hardware.Identifier != null ? hardware.Identifier.ToString() : hardware.Name) +
                        "|" + (fan.Identifier != null ? fan.Identifier.ToString() : fan.Name);
                    bool gpuFan = hardware.HardwareType == HardwareType.GpuNvidia ||
                        hardware.HardwareType == HardwareType.GpuAmd ||
                        hardware.HardwareType == HardwareType.GpuIntel;
                    if (rpm > 0 || gpuFan)
                        _observedFanKeys.Add(fanKey);
                    // Super-I/O chips expose every physical header even when it
                    // is empty. Keep a motherboard fan after it has produced a
                    // real tachometer reading, while GPU zero-RPM sensors remain
                    // visible because stopping at idle is expected behaviour.
                    if (!_observedFanKeys.Contains(fanKey))
                        continue;
                    names.Add(BuildFanName(hardware, fan.Name));
                    speeds.Add(rpm);
                    controls.Add(control != null ? NonNegativeValue(control.Value, 100) : -1);
                }
            }

            snapshot.FanNames = names.ToArray();
            snapshot.FanRpm = speeds.ToArray();
            snapshot.FanControlPercent = controls.ToArray();
        }

        private static ISensor FindFanControl(ISensor fan, IEnumerable<ISensor> controls)
        {
            ISensor exact = controls.FirstOrDefault(delegate(ISensor control)
            {
                return String.Equals(control.Name, fan.Name, StringComparison.OrdinalIgnoreCase);
            });
            if (exact != null)
                return exact;

            string fanKey = SensorIdentifierLeaf(fan);
            return controls.FirstOrDefault(delegate(ISensor control)
            {
                return String.Equals(SensorIdentifierLeaf(control), fanKey,
                    StringComparison.OrdinalIgnoreCase);
            });
        }

        private static string SensorIdentifierLeaf(ISensor sensor)
        {
            string identifier = sensor != null && sensor.Identifier != null
                ? sensor.Identifier.ToString()
                : String.Empty;
            int slash = identifier.LastIndexOf('/');
            return slash >= 0 ? identifier.Substring(slash + 1) : identifier;
        }

        private static string BuildFanName(IHardware hardware, string sensorName)
        {
            string name = String.IsNullOrWhiteSpace(sensorName) ? "Вентилятор" : sensorName.Trim();
            if (hardware != null &&
                (hardware.HardwareType == HardwareType.GpuNvidia ||
                 hardware.HardwareType == HardwareType.GpuAmd ||
                 hardware.HardwareType == HardwareType.GpuIntel) &&
                name.IndexOf("GPU", StringComparison.OrdinalIgnoreCase) < 0)
                return "GPU " + name;
            return name;
        }

        private static double NonNegativeValue(float? value, double maximumInclusive)
        {
            if (!value.HasValue || Single.IsNaN(value.Value) || Single.IsInfinity(value.Value) ||
                value.Value < 0 || value.Value > maximumInclusive)
                return -1;
            return value.Value;
        }

        private static double PreferredValue(ISensor[] sensors, SensorType type, string[] preferredNames,
            bool useMaximumFallback, double minimumExclusive, double maximumInclusive)
        {
            foreach (string name in preferredNames)
            {
                ISensor sensor = sensors.FirstOrDefault(delegate(ISensor candidate)
                {
                    return candidate.SensorType == type &&
                        String.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase) &&
                        Valid(candidate.Value, minimumExclusive, maximumInclusive);
                });
                if (sensor != null)
                    return sensor.Value.Value;
            }

            double[] values = sensors
                .Where(delegate(ISensor sensor) { return sensor.SensorType == type && Valid(sensor.Value, minimumExclusive, maximumInclusive); })
                .Select(delegate(ISensor sensor) { return (double)sensor.Value.Value; })
                .ToArray();
            if (values.Length == 0)
                return 0;
            return useMaximumFallback ? values.Max() : values.Average();
        }

        private static double AverageValue(ISensor[] sensors, SensorType type, string nameFragment,
            double minimumExclusive, double maximumInclusive)
        {
            double[] values = sensors
                .Where(delegate(ISensor sensor)
                {
                    return sensor.SensorType == type && sensor.Name.IndexOf(nameFragment, StringComparison.OrdinalIgnoreCase) >= 0 &&
                        sensor.Name.IndexOf("Bus", StringComparison.OrdinalIgnoreCase) < 0 &&
                        Valid(sensor.Value, minimumExclusive, maximumInclusive);
                })
                .Select(delegate(ISensor sensor) { return (double)sensor.Value.Value; })
                .ToArray();
            return values.Length > 0 ? values.Average() : 0;
        }

        private static bool Valid(float? value, double minimumExclusive, double maximumInclusive)
        {
            return value.HasValue && !Single.IsNaN(value.Value) && !Single.IsInfinity(value.Value) &&
                value.Value > minimumExclusive && value.Value <= maximumInclusive;
        }

        private void FillNetwork(SensorSnapshot snapshot)
        {
            Dictionary<string, long> received = new Dictionary<string, long>(StringComparer.Ordinal);
            Dictionary<string, long> sent = new Dictionary<string, long>(StringComparer.Ordinal);
            try
            {
                foreach (NetworkInterface adapter in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (adapter.OperationalStatus != OperationalStatus.Up ||
                        adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                        adapter.NetworkInterfaceType == NetworkInterfaceType.Tunnel)
                        continue;
                    try
                    {
                        IPv4InterfaceStatistics statistics = adapter.GetIPv4Statistics();
                        received[adapter.Id] = Math.Max(0, statistics.BytesReceived);
                        sent[adapter.Id] = Math.Max(0, statistics.BytesSent);
                    }
                    catch (NetworkInformationException) { }
                }
            }
            catch (NetworkInformationException) { }

            DateTime now = DateTime.UtcNow;
            if (received.Count == 0)
            {
                _lastBytesReceived.Clear();
                _lastBytesSent.Clear();
                _lastNetworkSampleAt = DateTime.MinValue;
                return;
            }

            if (_lastNetworkSampleAt != DateTime.MinValue)
            {
                double seconds = (now - _lastNetworkSampleAt).TotalSeconds;
                if (seconds > 0.05)
                {
                    long receivedDelta = 0;
                    long sentDelta = 0;
                    foreach (KeyValuePair<string, long> counter in received)
                    {
                        long previous;
                        if (_lastBytesReceived.TryGetValue(counter.Key, out previous))
                            receivedDelta += Math.Max(0, counter.Value - previous);
                    }
                    foreach (KeyValuePair<string, long> counter in sent)
                    {
                        long previous;
                        if (_lastBytesSent.TryGetValue(counter.Key, out previous))
                            sentDelta += Math.Max(0, counter.Value - previous);
                    }
                    snapshot.NetworkDownloadKbps = receivedDelta / seconds / 1024.0;
                    snapshot.NetworkUploadKbps = sentDelta / seconds / 1024.0;
                }
            }
            _lastBytesReceived = received;
            _lastBytesSent = sent;
            _lastNetworkSampleAt = now;
        }

        private static string ReadProcessorName()
        {
            object value = Registry.GetValue(
                @"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0",
                "ProcessorNameString", String.Empty);
            return Convert.ToString(value) ?? String.Empty;
        }

        private static double ReadProcessorClock()
        {
            object value = Registry.GetValue(
                @"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0",
                "~MHz", 0);
            try { return Convert.ToDouble(value); }
            catch { return 0; }
        }

        public void Dispose()
        {
            if (_computer != null)
                _computer.Close();
        }
    }
}
