using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace Traymetry
{
    internal static class Program
    {
        private static Mutex _singleInstance;

        [STAThread]
        private static void Main(string[] args)
        {
            StartupTrace.Enable(args.Any(delegate(string argument)
            {
                return String.Equals(argument, "--diagnostic-startup", StringComparison.OrdinalIgnoreCase);
            }));
            StartupTrace.Write("main-enter");
            EmbeddedDependencies.Register();
            StartupTrace.Write("dependencies-registered");

            int updateExitCode;
            if (UpdateInstaller.TryHandleCommandLine(args, out updateExitCode))
            {
                Environment.ExitCode = updateExitCode;
                return;
            }

            if (args.Length > 0 && String.Equals(args[0], "--sensor-service", StringComparison.OrdinalIgnoreCase))
            {
                TraymetrySensorService.RunService();
                return;
            }
            if (args.Length > 0 && MachineBootstrap.IsSetupArgument(args[0]))
            {
                Environment.ExitCode = MachineBootstrap.RunElevatedSetup();
                return;
            }
            if (args.Length > 0 && MachineBootstrap.IsUninstallArgument(args[0]))
            {
                Environment.ExitCode = MachineBootstrap.RunElevatedUninstall();
                return;
            }

            bool created;
            _singleInstance = new Mutex(true, @"Local\Traymetry", out created);
            StartupTrace.Write("mutex-created=" + created);
            if (!created)
            {
                MessageBox.Show(
                    "Traymetry уже запущен. Закройте работающий экземпляр через меню «Выход», затем запустите приложение снова.",
                    "Traymetry",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            NativeUi.SetProcessDPIAware();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            StartupTrace.Write("ui-initialized");
            if (UpdateInstaller.ShouldShowUpdateFailure(args))
            {
                MessageBox.Show(
                    "Обновление не удалось применить. Запущена прежняя версия Traymetry; " +
                    "подробности записаны в журнал обновления.",
                    "Traymetry — обновление",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
            if (UpdateInstaller.ShouldRefreshSensorService(args))
                MachineBootstrap.RequestRepair();
            else
                MachineBootstrap.EnsureReady();
            StartupTrace.Write("machine-bootstrap-finished");
            MonitorForm form = new MonitorForm();
            StartupTrace.Write("form-constructed");
            Application.Run(form);
            StartupTrace.Write("message-loop-finished");
        }
    }

    internal static class StartupTrace
    {
        private static bool _enabled;
        internal static string FilePath
        {
            get { return Path.Combine(Path.GetTempPath(), "Traymetry-startup.log"); }
        }

        internal static void Enable(bool enabled)
        {
            _enabled = enabled;
            if (!enabled)
                return;
            try { File.WriteAllText(FilePath, String.Empty); }
            catch { }
        }

        internal static void Write(string message)
        {
            if (!_enabled)
                return;
            try
            {
                File.AppendAllText(FilePath,
                    DateTime.Now.ToString("HH:mm:ss.fff") + " " + message + Environment.NewLine);
            }
            catch { }
        }
    }

    public sealed class SensorSnapshot
    {
        public string CpuName { get; set; }
        public double Temperature { get; set; }
        public double Usage { get; set; }
        public double ClockMhz { get; set; }
        public double PowerWatts { get; set; }
        public string GpuName { get; set; }
        public double GpuTemperature { get; set; }
        public double GpuUsage { get; set; }
        public double GpuClockMhz { get; set; }
        public double GpuPowerWatts { get; set; }
        public double GpuMemoryUsedGb { get; set; }
        public double GpuMemoryTotalGb { get; set; }
        public double NetworkDownloadKbps { get; set; }
        public double NetworkUploadKbps { get; set; }
        public double MemoryUsedGb { get; set; }
        public double MemoryTotalGb { get; set; }
        public double MemoryClockMhz { get; set; }
        public double StorageUsedGb { get; set; }
        public double StorageTotalGb { get; set; }
        public string[] StorageDriveNames { get; set; }
        public double[] StorageDriveUsedGb { get; set; }
        public double[] StorageDriveTotalGb { get; set; }
        public string[] FanNames { get; set; }
        public double[] FanRpm { get; set; }
        public double[] FanControlPercent { get; set; }
        public int FrameTelemetryState { get; set; }
        public int[] FrameProcessIds { get; set; }
        public string[] FrameProcessNames { get; set; }
        public int[] FrameStatuses { get; set; }
        public double[] FrameDisplayedFps { get; set; }
        public double[] FramePresentedFps { get; set; }
        public double[] FrameApplicationFps { get; set; }
        public double[] FrameTimeMs { get; set; }
        public double[] FrameOnePercentLowFps { get; set; }
    }

    internal static class SystemTelemetry
    {
        private static readonly object Sync = new object();
        private static DateTime _lastRefresh = DateTime.MinValue;
        private static double _memoryUsedGb;
        private static double _memoryTotalGb;
        private static double _memoryClockMhz;
        private static double _storageUsedGb;
        private static double _storageTotalGb;
        private static string[] _storageDriveNames = new string[0];
        private static double[] _storageDriveUsedGb = new double[0];
        private static double[] _storageDriveTotalGb = new double[0];

        public static void Fill(SensorSnapshot snapshot)
        {
            lock (Sync)
            {
                if ((DateTime.UtcNow - _lastRefresh).TotalSeconds >= 5)
                    Refresh();
                snapshot.MemoryUsedGb = _memoryUsedGb;
                snapshot.MemoryTotalGb = _memoryTotalGb;
                snapshot.MemoryClockMhz = _memoryClockMhz;
                snapshot.StorageUsedGb = _storageUsedGb;
                snapshot.StorageTotalGb = _storageTotalGb;
                snapshot.StorageDriveNames = _storageDriveNames;
                snapshot.StorageDriveUsedGb = _storageDriveUsedGb;
                snapshot.StorageDriveTotalGb = _storageDriveTotalGb;
            }
        }

        private static void Refresh()
        {
            const double bytesPerGb = 1024.0 * 1024.0 * 1024.0;
            MemoryStatus status = new MemoryStatus();
            status.Length = (uint)Marshal.SizeOf(typeof(MemoryStatus));
            if (GlobalMemoryStatusEx(ref status))
            {
                _memoryTotalGb = status.TotalPhysical / bytesPerGb;
                _memoryUsedGb = (status.TotalPhysical - status.AvailablePhysical) / bytesPerGb;
            }
            if (_memoryClockMhz <= 0)
                _memoryClockMhz = ReadMemoryClockFromSmbios();

            long total = 0;
            long free = 0;
            List<string> driveNames = new List<string>();
            List<double> driveUsed = new List<double>();
            List<double> driveTotals = new List<double>();
            try
            {
                foreach (DriveInfo drive in DriveInfo.GetDrives().OrderBy(delegate(DriveInfo item) { return item.Name; }))
                {
                    if (drive.DriveType != DriveType.Fixed || !drive.IsReady)
                        continue;
                    total += drive.TotalSize;
                    free += drive.AvailableFreeSpace;
                    driveNames.Add(drive.Name.TrimEnd('\\'));
                    driveTotals.Add(drive.TotalSize / bytesPerGb);
                    driveUsed.Add((drive.TotalSize - drive.AvailableFreeSpace) / bytesPerGb);
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }

            _storageTotalGb = total / bytesPerGb;
            _storageUsedGb = (total - free) / bytesPerGb;
            _storageDriveNames = driveNames.ToArray();
            _storageDriveUsedGb = driveUsed.ToArray();
            _storageDriveTotalGb = driveTotals.ToArray();
            _lastRefresh = DateTime.UtcNow;
        }

        private static double ReadMemoryClockFromSmbios()
        {
            const uint rawSmbiosProvider = 0x52534D42;
            try
            {
                uint size = NativeUi.GetSystemFirmwareTable(rawSmbiosProvider, 0, null, 0);
                if (size < 12 || size > 16 * 1024 * 1024)
                    return 0;
                byte[] data = new byte[size];
                uint received = NativeUi.GetSystemFirmwareTable(rawSmbiosProvider, 0, data, size);
                if (received < 12)
                    return 0;

                int declaredLength = BitConverter.ToInt32(data, 4);
                int end = Math.Min((int)received, Math.Min(data.Length, 8 + Math.Max(0, declaredLength)));
                int offset = 8;
                double fastest = 0;
                while (offset + 4 <= end)
                {
                    byte type = data[offset];
                    int structureLength = data[offset + 1];
                    if (structureLength < 4 || offset + structureLength > end)
                        break;
                    if (type == 17)
                    {
                        uint configured = structureLength >= 0x22
                            ? (uint)BitConverter.ToUInt16(data, offset + 0x20)
                            : 0U;
                        uint advertised = structureLength >= 0x17
                            ? (uint)BitConverter.ToUInt16(data, offset + 0x15)
                            : 0U;
                        if (configured == 0xFFFF && structureLength >= 0x5C)
                            configured = BitConverter.ToUInt32(data, offset + 0x58);
                        if (advertised == 0xFFFF && structureLength >= 0x58)
                            advertised = BitConverter.ToUInt32(data, offset + 0x54);
                        uint speed = configured > 0 && configured != 0xFFFF ? configured : advertised;
                        if (speed > fastest && speed < 100000)
                            fastest = speed;
                    }
                    if (type == 127)
                        break;

                    int next = offset + structureLength;
                    while (next + 1 < end && (data[next] != 0 || data[next + 1] != 0))
                        next++;
                    next += 2;
                    if (next <= offset)
                        break;
                    offset = next;
                }
                return fastest;
            }
            catch
            {
                return 0;
            }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct MemoryStatus
        {
            public uint Length;
            public uint MemoryLoad;
            public ulong TotalPhysical;
            public ulong AvailablePhysical;
            public ulong TotalPageFile;
            public ulong AvailablePageFile;
            public ulong TotalVirtual;
            public ulong AvailableVirtual;
            public ulong AvailableExtendedVirtual;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
    }

    internal static class NativeUi
    {
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetProcessDPIAware();

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        internal static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter,
            int x, int y, int width, int height, uint flags);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern uint GetSystemFirmwareTable(uint providerSignature, uint tableId,
            [Out] byte[] buffer, uint bufferSize);

        [DllImport("gdi32.dll")]
        internal static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int ellipseWidth, int ellipseHeight);

        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DeleteObject(IntPtr obj);
    }
}
