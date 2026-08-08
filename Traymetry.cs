using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
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

            // Per-pixel transparency rather than a colour key, and now the
            // default: a colour key has no partial coverage, so every antialiased
            // edge in the widget is thrown away and the digits come out ragged
            // on anything but a dark desktop.  --layered, from when this was
            // opt-in, now asks for what it already gets and is ignored; the
            // switch that remains is --classic, for a display driver that turns
            // out to dislike a layered window.
            foreach (string argument in args)
            {
                if (String.Equals(argument, "--classic", StringComparison.OrdinalIgnoreCase))
                    MonitorForm.LayeredMode = false;
            }

            // Support path: a widget that will not start cannot collect its own
            // report from its own menu, and this asks nothing of the machine
            // beyond reading the log and the settings.
            if (args.Any(delegate(string argument)
            {
                return String.Equals(argument, "--report", StringComparison.OrdinalIgnoreCase);
            }))
            {
                string reportPath = DiagnosticReport.Write(DiagnosticReport.ReadSettings(),
                    "(collected without the widget running)");
                try { System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + reportPath + "\""); }
                catch { }
                return;
            }

            bool created;
            _singleInstance = new Mutex(true, @"Local\Traymetry", out created);
            StartupTrace.Write("mutex-created=" + created);
            if (!created)
            {
                MessageBox.Show(
                    Loc.T("app.alreadyRunning"),
                    "Traymetry",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            NativeUi.SetProcessDPIAware();
            NativeUi.KeepFullSpeed();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            StartupTrace.Write("ui-initialized");
            // First line of every run, so the log reads as sessions rather than
            // as a heap of events with no idea which build produced them.
            DiagLog.Write("start version=" + DiagnosticReport.Version +
                " layered=" + (MonitorForm.LayeredMode ? "1" : "0") +
                " os=" + Environment.OSVersion.Version +
                " clr=" + Environment.Version);
            if (UpdateInstaller.ShouldShowUpdateFailure(args))
            {
                MessageBox.Show(
                    Loc.T("app.updateRolledBack"),
                    Loc.T("app.updateTitle"),
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
            if (UpdateInstaller.ShouldRefreshSensorService(args))
                MachineBootstrap.RequestRepair();
            else
                MachineBootstrap.EnsureReady();
            StartupTrace.Write("machine-bootstrap-finished");
            Application.ThreadException += CrashLog.OnThreadException;
            AppDomain.CurrentDomain.UnhandledException += CrashLog.OnDomainException;
            MonitorForm form = new MonitorForm();
            StartupTrace.Write("form-constructed");
            Application.Run(form);
            StartupTrace.Write("message-loop-finished");
        }
    }

    /// <summary>
    /// Writes down what actually went wrong.  The framework's own dialog gives
    /// the user a translated one-line summary and nothing anybody can act on -
    /// "Недостаточно памяти" is what GDI+ says for a dozen unrelated failures -
    /// so the type, the message and the stack go to a file first.
    /// </summary>
    internal static class CrashLog
    {
        internal static string FilePath
        {
            get { return Path.Combine(Path.GetTempPath(), "Traymetry-crash.log"); }
        }

        internal static void OnThreadException(object sender, ThreadExceptionEventArgs e)
        {
            Write("ui-thread", e.Exception);
            MessageBox.Show(
                e.Exception.GetType().Name + ": " + e.Exception.Message +
                Environment.NewLine + Environment.NewLine + FilePath,
                "Traymetry", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        internal static void OnDomainException(object sender, UnhandledExceptionEventArgs e)
        {
            Write("domain", e.ExceptionObject as Exception);
        }

        private static void Write(string origin, Exception error)
        {
            // Also in the running log, where it lands next to whatever the
            // widget was doing in the minutes before it fell over.
            DiagLog.Write("crash [" + origin + "] " +
                (error == null ? "unknown" : error.GetType().Name + ": " + error.Message));
            try
            {
                File.AppendAllText(FilePath,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " [" + origin + "] " +
                    (error == null ? "unknown" : error.ToString()) +
                    Environment.NewLine + Environment.NewLine);
            }
            catch { }
        }
    }

    /// <summary>
    /// The running log, always on.  A widget that stutters or loses a sensor
    /// does it on somebody else's machine, hours after it was started, and a
    /// bug report saying "it lagged" is not something anybody can act on: what
    /// can be acted on is a line saying the interface thread was away for two
    /// seconds with this much memory paged out at that moment.
    ///
    /// Capped and rotated once, so a machine that runs the widget for a month
    /// spends a megabyte on it and never more.
    /// </summary>
    internal static class DiagLog
    {
        private const long MaxBytes = 512 * 1024;
        private static readonly object Sync = new object();

        internal static string FolderPath
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Traymetry");
            }
        }

        internal static string FilePath
        {
            get { return Path.Combine(FolderPath, "traymetry.log"); }
        }

        internal static string PreviousFilePath
        {
            get { return FilePath + ".1"; }
        }

        internal static void Write(string message)
        {
            lock (Sync)
            {
                try
                {
                    if (!Directory.Exists(FolderPath))
                        Directory.CreateDirectory(FolderPath);
                    Rotate();
                    File.AppendAllText(FilePath,
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) +
                        " " + message + Environment.NewLine,
                        Encoding.UTF8);
                }
                catch
                {
                    // A log that throws is worse than no log.
                }
            }
        }

        /// <summary>
        /// Machine state in one line: the working set, how many pages the
        /// process has had to fetch since it started, and what the collector has
        /// been doing.  All three are what a stall after a long idle period
        /// would leave a mark in.
        /// </summary>
        internal static string DescribeProcess()
        {
            try
            {
                ProcessMemoryCounters counters = new ProcessMemoryCounters();
                counters.Size = (uint)Marshal.SizeOf(typeof(ProcessMemoryCounters));
                string memory = "ws=? pf=?";
                if (GetProcessMemoryInfo(GetCurrentProcess(), out counters, counters.Size))
                    memory = "ws=" + (counters.WorkingSetSize.ToInt64() / (1024 * 1024)) + "MB" +
                        " pf=" + counters.PageFaultCount;
                return memory +
                    " gc=" + GC.CollectionCount(0) + "/" + GC.CollectionCount(1) + "/" +
                    GC.CollectionCount(2) +
                    " heap=" + (GC.GetTotalMemory(false) / (1024 * 1024)) + "MB";
            }
            catch (Exception error)
            {
                return "state-unavailable " + error.GetType().Name;
            }
        }

        private static void Rotate()
        {
            FileInfo file = new FileInfo(FilePath);
            if (!file.Exists || file.Length < MaxBytes)
                return;
            try
            {
                if (File.Exists(PreviousFilePath))
                    File.Delete(PreviousFilePath);
                File.Move(FilePath, PreviousFilePath);
            }
            catch
            {
                try { File.WriteAllText(FilePath, String.Empty); }
                catch { }
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessMemoryCounters
        {
            public uint Size;
            public uint PageFaultCount;
            public IntPtr PeakWorkingSetSize;
            public IntPtr WorkingSetSize;
            public IntPtr QuotaPeakPagedPoolUsage;
            public IntPtr QuotaPagedPoolUsage;
            public IntPtr QuotaPeakNonPagedPoolUsage;
            public IntPtr QuotaNonPagedPoolUsage;
            public IntPtr PagefileUsage;
            public IntPtr PeakPagefileUsage;
        }

        /// <summary>
        /// Processor time this process has been given, in milliseconds, kernel
        /// and user together.  Read across a stall it settles the only question
        /// a stall really asks: whether the widget was busy or was not being
        /// run.  A second-long gap that cost twenty milliseconds of processor
        /// time was spent waiting for the machine, not working.
        ///
        /// Read through the API rather than through a Process object, because
        /// this is sampled on every tick and that object is not free.
        /// </summary>
        internal static long ProcessorMilliseconds()
        {
            try
            {
                long creation, exit, kernel, user;
                if (!GetProcessTimes(GetCurrentProcess(), out creation, out exit,
                        out kernel, out user))
                    return -1;
                // Both are hundreds of nanoseconds.
                return (kernel + user) / 10000;
            }
            catch (EntryPointNotFoundException)
            {
                return -1;
            }
            catch (DllNotFoundException)
            {
                return -1;
            }
        }

        /// <summary>
        /// How hard the machine as a whole is pressed for memory.  A widget
        /// whose pages were taken away has nothing in its own numbers to show
        /// for it - the working set simply stays the size it was - so the
        /// reason has to be read from outside the process.
        /// </summary>
        internal static string DescribeMachine()
        {
            try
            {
                MemoryStatus status = new MemoryStatus();
                status.Size = (uint)Marshal.SizeOf(typeof(MemoryStatus));
                if (!GlobalMemoryStatusEx(ref status))
                    return "mem=?";
                return "mem.load=" + status.MemoryLoad + "%" +
                    " mem.free=" + (status.AvailablePhysical / (1024UL * 1024UL)) + "MB";
            }
            catch (Exception error)
            {
                return "mem-unavailable " + error.GetType().Name;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MemoryStatus
        {
            public uint Size;
            public uint MemoryLoad;
            public ulong TotalPhysical;
            public ulong AvailablePhysical;
            public ulong TotalPageFile;
            public ulong AvailablePageFile;
            public ulong TotalVirtual;
            public ulong AvailableVirtual;
            public ulong AvailableExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);

        [DllImport("psapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetProcessMemoryInfo(IntPtr process,
            out ProcessMemoryCounters counters, uint size);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetProcessTimes(IntPtr process, out long creation,
            out long exit, out long kernel, out long user);
    }

    /// <summary>
    /// One file a user can attach to a bug report without being asked three
    /// rounds of questions first: which build, which machine, what the sensor
    /// service is doing, what the widget is set to, and what the log has to say
    /// about the last few hours.
    ///
    /// Everything in it is about this program and this machine's hardware.  No
    /// file names, no window contents, nothing typed by the user.
    /// </summary>
    internal static class DiagnosticReport
    {
        private const int LogTailLines = 400;

        internal static string Write(string settingsDump, string snapshotDump)
        {
            string path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                "Traymetry-report-" +
                DateTime.Now.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture) + ".txt");
            StringBuilder report = new StringBuilder();
            AppendHeader(report);
            AppendSection(report, "settings", settingsDump);
            AppendSection(report, "sensors", snapshotDump);
            AppendSection(report, "service", DescribeService());
            AppendSection(report, "crash log", ReadTail(CrashLog.FilePath, 120));
            AppendSection(report, "update log", ReadTail(UpdateInstaller.LogPath, 60));
            AppendSection(report, "log (previous file)",
                ReadTail(DiagLog.PreviousFilePath, 80));
            AppendSection(report, "log", ReadTail(DiagLog.FilePath, LogTailLines));
            File.WriteAllText(path, Redact(report.ToString()), Encoding.UTF8);
            return path;
        }

        /// <summary>
        /// The report is written so it can be handed to someone else, and a
        /// stack trace or a log line carries the profile path with the account
        /// name in it.  Nothing here needs the real name to be readable, so it
        /// goes out as the placeholder Windows itself uses.
        /// </summary>
        private static string Redact(string report)
        {
            string profile = Environment.GetFolderPath(
                Environment.SpecialFolder.UserProfile);
            if (!String.IsNullOrEmpty(profile))
                report = report.Replace(profile, @"%USERPROFILE%");
            string user = Environment.UserName;
            // A one or two letter account name would turn ordinary words into
            // placeholders, which costs more readability than it buys privacy.
            if (!String.IsNullOrEmpty(user) && user.Length >= 3)
                report = Replace(report, user, "%USERNAME%");
            return report;
        }

        private static string Replace(string text, string search, string replacement)
        {
            StringBuilder result = new StringBuilder(text.Length);
            int position = 0;
            while (true)
            {
                int found = text.IndexOf(search, position,
                    StringComparison.OrdinalIgnoreCase);
                if (found < 0)
                    break;
                result.Append(text, position, found - position).Append(replacement);
                position = found + search.Length;
            }
            return result.Append(text, position, text.Length - position).ToString();
        }

        private static void AppendHeader(StringBuilder report)
        {
            report.AppendLine("Traymetry problem report");
            report.AppendLine("collected: " +
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            report.AppendLine("version: " + Version);
            report.AppendLine("os: " + Environment.OSVersion.VersionString +
                (Environment.Is64BitOperatingSystem ? " x64" : " x86"));
            report.AppendLine("clr: " + Environment.Version);
            report.AppendLine("elevated: " + (IsElevated() ? "yes" : "no"));
            report.AppendLine("layered: " + (MonitorForm.LayeredMode ? "yes" : "no"));
            report.AppendLine("language: " + Loc.Code);
            report.AppendLine("displays: " + DescribeDisplays());
            try
            {
                using (System.Diagnostics.Process process =
                    System.Diagnostics.Process.GetCurrentProcess())
                    report.AppendLine("uptime: " +
                        Math.Round((DateTime.Now - process.StartTime).TotalMinutes) + " min");
            }
            catch { }
            report.AppendLine("process: " + DiagLog.DescribeProcess());
        }

        internal static string Version
        {
            get { return DescribeVersion(); }
        }

        /// <summary>
        /// Everything the widget stores about itself, straight out of the
        /// registry key it owns: sizes, colours, slots, keys.  Nothing here came
        /// from anywhere but this program's own menus.
        /// </summary>
        internal static string ReadSettings()
        {
            StringBuilder text = new StringBuilder();
            try
            {
                using (Microsoft.Win32.RegistryKey key =
                    Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Traymetry"))
                {
                    if (key == null)
                        return "(no stored settings)";
                    string[] names = key.GetValueNames();
                    Array.Sort(names, StringComparer.OrdinalIgnoreCase);
                    foreach (string name in names)
                        text.AppendLine(name + " = " +
                            Convert.ToString(key.GetValue(name), CultureInfo.InvariantCulture));
                }
            }
            catch (Exception error)
            {
                text.AppendLine("(unreadable: " + error.GetType().Name + ": " + error.Message + ")");
            }
            return text.ToString();
        }

        private static string DescribeVersion()
        {
            try
            {
                object[] attributes = typeof(DiagnosticReport).Assembly.GetCustomAttributes(
                    typeof(System.Reflection.AssemblyInformationalVersionAttribute), false);
                if (attributes.Length > 0)
                    return ((System.Reflection.AssemblyInformationalVersionAttribute)
                        attributes[0]).InformationalVersion;
            }
            catch { }
            return "unknown";
        }

        private static void AppendSection(StringBuilder report, string title, string body)
        {
            report.AppendLine();
            report.AppendLine("== " + title + " ==");
            report.AppendLine(String.IsNullOrEmpty(body) ? "(empty)" : body.TrimEnd());
        }

        private static string DescribeDisplays()
        {
            string[] descriptions = Screen.AllScreens.Select(delegate(Screen screen)
            {
                return screen.Bounds.Width + "x" + screen.Bounds.Height +
                    (screen.Primary ? " primary" : String.Empty);
            }).ToArray();
            return String.Join(", ", descriptions);
        }

        private static string DescribeService()
        {
            StringBuilder text = new StringBuilder();
            try
            {
                text.AppendLine("pawnio installed: " +
                    (PawnIoBootstrap.IsInstalled ? "yes" : "no"));
            }
            catch (Exception error)
            {
                text.AppendLine("pawnio: " + error.GetType().Name + ": " + error.Message);
            }
            try
            {
                text.AppendLine("sensor service current and running: " +
                    (SensorServiceInstaller.IsCurrentAndRunning() ? "yes" : "no"));
            }
            catch (Exception error)
            {
                text.AppendLine("sensor service: " + error.GetType().Name + ": " + error.Message);
            }
            return text.ToString();
        }

        private static bool IsElevated()
        {
            try
            {
                using (System.Security.Principal.WindowsIdentity identity =
                    System.Security.Principal.WindowsIdentity.GetCurrent())
                {
                    return new System.Security.Principal.WindowsPrincipal(identity).IsInRole(
                        System.Security.Principal.WindowsBuiltInRole.Administrator);
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// The end of a file, opened the way a reader opens it - shared, so a
        /// log the widget is still writing to can be read without stopping it.
        /// </summary>
        private static string ReadTail(string path, int lines)
        {
            try
            {
                if (!File.Exists(path))
                    return "(no file)";
                List<string> tail = new List<string>();
                using (FileStream stream = new FileStream(path, FileMode.Open,
                    FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        tail.Add(line);
                        if (tail.Count > lines)
                            tail.RemoveAt(0);
                    }
                }
                return String.Join(Environment.NewLine, tail.ToArray());
            }
            catch (Exception error)
            {
                return "(unreadable: " + error.GetType().Name + ": " + error.Message + ")";
            }
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
        internal static extern short GetAsyncKeyState(int key);

        /// <summary>
        /// True while any mouse button is held, wherever the pointer is and
        /// whoever owns the window under it.  A press in another program never
        /// reaches this one's message queue, so it cannot be read from there.
        /// </summary>
        internal static bool AnyMouseButtonDown()
        {
            const int LeftButton = 0x01;
            const int RightButton = 0x02;
            const int MiddleButton = 0x04;
            return (GetAsyncKeyState(LeftButton) & 0x8000) != 0 ||
                (GetAsyncKeyState(RightButton) & 0x8000) != 0 ||
                (GetAsyncKeyState(MiddleButton) & 0x8000) != 0;
        }

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        /// <summary>
        /// Which thread is running this.  A low-level mouse hook is only ever
        /// called on the thread that installed it, and only while that thread is
        /// pumping messages, so "which thread" is the first question to ask of a
        /// hook that was installed and is never heard from again.
        /// </summary>
        internal static uint CurrentThreadId()
        {
            return GetCurrentThreadId();
        }

        private const int ExStyleIndex = -20;
        private const int ExStyleTopMost = 0x00000008;
        private const int ExStyleTransparent = 0x00000020;
        private const int ExStyleToolWindow = 0x00000080;
        private const int ExStyleAppWindow = 0x00040000;

        /// <summary>
        /// Keeps a window out of the taskbar and out of Alt+Tab.  A menu here is
        /// a popup owned by a hidden window, which the shell is entitled to
        /// treat as a window of its own: opening the context menu made a
        /// Traymetry button appear in the taskbar for as long as the menu stood.
        /// A tool window never gets one.
        /// </summary>
        internal static void SetToolWindow(IntPtr window)
        {
            if (window == IntPtr.Zero)
                return;
            int style = GetWindowLong(window, ExStyleIndex);
            int wanted = (style | ExStyleToolWindow) & ~ExStyleAppWindow;
            if (wanted != style)
                SetWindowLong(window, ExStyleIndex, wanted);
        }

        /// <summary>
        /// Takes a window and everything inside it out of the hit test, or puts
        /// it back.  Answering the hit test from the window's own procedure only
        /// speaks for the window itself: a control is a window too, it is asked
        /// about its own pixels, and the readings of this widget are controls -
        /// so a click on the numbers never reached the form that was trying to
        /// wave it through.  The extended style is read by the system before any
        /// of that and covers the whole tree at once.
        ///
        /// Returns whether the bit had to move.  Writing the extended style of a
        /// layered window empties it until a whole frame is handed over, so the
        /// caller has to know, and this is not something to do per click.
        /// </summary>
        internal static bool SetClickThrough(IntPtr window, bool through)
        {
            if (window == IntPtr.Zero)
                return false;
            int style = GetWindowLong(window, ExStyleIndex);
            if (((style & ExStyleTransparent) != 0) == through)
                return false;
            SetWindowLong(window, ExStyleIndex, through
                ? style | ExStyleTransparent
                : style & ~ExStyleTransparent);
            return true;
        }

        /// <summary>
        /// The whole extended style word, for the log.  Which bits are set says
        /// which of several look-alike faults is happening; the summary flags on
        /// their own have twice sent this after the wrong one.
        /// </summary>
        internal static string DescribeExStyle(IntPtr window)
        {
            if (window == IntPtr.Zero)
                return "0x00000000";
            return "0x" + GetWindowLong(window, ExStyleIndex).ToString("X8",
                CultureInfo.InvariantCulture);
        }


        /// <summary>
        /// Whether <paramref name="above"/> sits in front of
        /// <paramref name="below"/> on screen.  Walked from the lower window
        /// towards the front, so the answer is the one the hit test would give.
        /// </summary>
        internal static bool IsInFrontOf(IntPtr above, IntPtr below)
        {
            const uint PreviousWindow = 3; // GW_HWNDPREV
            if (above == IntPtr.Zero || below == IntPtr.Zero)
                return false;
            IntPtr candidate = below;
            for (int guard = 0; guard < 256; guard++)
            {
                candidate = GetWindow(candidate, PreviousWindow);
                if (candidate == IntPtr.Zero)
                    return false;
                if (candidate == above)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Whether a visible window that is not in the topmost band stands in
        /// front of this one, which is the only thing "not on top any more" can
        /// mean on screen.
        ///
        /// The extended style word cannot answer this by itself.  It is written
        /// whole by anything that sets a style, so its topmost bit drifts out of
        /// step with the band the system actually keeps the window in, and
        /// asking for the band again is a no-op that leaves the stale bit
        /// exactly where it was.  Acting on the bit alone means dropping the
        /// window out of the band and back to correct a fault that was not
        /// there - once per menu, which is a blink the user can see.
        ///
        /// The z-order does not drift.  Everything in front of a window that is
        /// really topmost is topmost too, so one normal window ahead of it
        /// settles the question.
        /// </summary>
        internal static bool IsBuriedUnderNormalWindow(IntPtr window)
        {
            const uint PreviousWindow = 3; // GW_HWNDPREV
            if (window == IntPtr.Zero)
                return false;
            IntPtr candidate = window;
            for (int guard = 0; guard < 512; guard++)
            {
                candidate = GetWindow(candidate, PreviousWindow);
                if (candidate == IntPtr.Zero)
                    return false;
                if (!IsWindowVisible(candidate))
                    continue;
                if ((GetWindowLong(candidate, ExStyleIndex) & ExStyleTopMost) == 0)
                    return true;
            }
            return false;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetWindow(IntPtr window, uint command);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindowVisible(IntPtr window);

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr window, int index);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr window, int index, int style);

        [StructLayout(LayoutKind.Sequential)]
        private struct PowerThrottlingState
        {
            internal uint Version;
            internal uint ControlMask;
            internal uint StateMask;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetProcessInformation(IntPtr process, int informationClass,
            ref PowerThrottlingState information, int size);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        /// <summary>
        /// Asks Windows to keep the widget off the efficiency track.  A window
        /// that is on screen the whole time but almost never in the foreground
        /// is exactly what Windows 11 throttles, and the first second after the
        /// pointer arrives is then spent climbing back out of it - which is not
        /// felt as a power saving, it is felt as a widget that stutters until
        /// it warms up.  Costs a little battery; the alternative costs the
        /// smoothness that is most of the point of the thing.
        /// </summary>
        internal static void KeepFullSpeed()
        {
            const int ProcessPowerThrottling = 4;
            const uint CurrentVersion = 1;
            const uint ExecutionSpeed = 0x1;
            try
            {
                PowerThrottlingState state = new PowerThrottlingState();
                state.Version = CurrentVersion;
                // Take control of the execution-speed knob, and leave it off.
                state.ControlMask = ExecutionSpeed;
                state.StateMask = 0;
                SetProcessInformation(GetCurrentProcess(), ProcessPowerThrottling,
                    ref state, Marshal.SizeOf(typeof(PowerThrottlingState)));
            }
            catch (EntryPointNotFoundException)
            {
                // Older than Windows 10 1709: nothing throttles it there either.
            }
            catch (DllNotFoundException)
            {
            }
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        internal static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool UnregisterHotKey(IntPtr window, int id);

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
