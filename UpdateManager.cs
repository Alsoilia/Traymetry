using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace Traymetry
{
    internal static class UpdateManager
    {
        private const string RegistryPath = @"Software\Traymetry";
        private const string LastCheckValue = "LastUpdateCheckUtc";
        private const long MaximumUpdateBytes = 64L * 1024L * 1024L;
        private const int MaximumManifestBytes = 32 * 1024;
        private static int _checking;

        internal static void CheckForUpdatesAsync(Form owner, bool manual)
        {
            if (owner == null || owner.IsDisposed)
                return;
            if (Interlocked.Exchange(ref _checking, 1) != 0)
            {
                if (manual)
                    Show(owner, Loc.T("update.inProgress"), MessageBoxIcon.Information);
                return;
            }

            ThreadPool.QueueUserWorkItem(delegate
            {
                bool handedToUi = false;
                try
                {
                    UpdateRelease release = FindUpdate();
                    WriteLastCheckTime();
                    if (release == null)
                    {
                        WriteLog("Update check: no newer release was offered.");
                        if (manual)
                            Show(owner, Loc.T("update.upToDate"), MessageBoxIcon.Information);
                        return;
                    }

                    WriteLog("Update check: offering " + release.VersionText + ".");

                    handedToUi = BeginOnUi(owner, delegate
                    {
                        try
                        {
                            DialogResult answer = MessageBox.Show(owner,
                                Loc.T("update.available", release.VersionText),
                                Loc.T("update.title"),
                                MessageBoxButtons.YesNo,
                                MessageBoxIcon.Information,
                                MessageBoxDefaultButton.Button1);
                            if (answer == DialogResult.Yes)
                                DownloadAndInstallAsync(owner, release);
                            else
                                Interlocked.Exchange(ref _checking, 0);
                        }
                        catch
                        {
                            Interlocked.Exchange(ref _checking, 0);
                            throw;
                        }
                    });
                }
                catch (Exception error)
                {
                    // A failed check used to leave no trace at all.  The
                    // automatic one says nothing on purpose - nobody wants a
                    // box on every start because the network was down - and
                    // the manual one says it in a message box that is gone by
                    // the time anyone thinks to ask.  "It never offers me
                    // anything" was therefore a report that could not be
                    // answered: a repository that does not exist, a name typed
                    // wrong and a machine with no network all look identical
                    // from the outside, and all three are one line here.
                    WriteLog("Update check failed: " + error.Message);
                    if (manual)
                        Show(owner, Loc.T("update.checkFailed") + error.Message,
                            MessageBoxIcon.Warning);
                    else
                        WriteLastCheckTime();
                }
                finally
                {
                    if (!handedToUi)
                        Interlocked.Exchange(ref _checking, 0);
                }
            });
        }

        /// <summary>
        /// One update log for the whole of updating, kept by the installer
        /// because that is the part which outlives the executable it is
        /// replacing.  Checking writes to the same file: a report that says
        /// "nothing was ever offered" and a report that says "the download was
        /// refused" are two ends of one story and belong on one timeline.
        /// </summary>
        private static void WriteLog(string text)
        {
            UpdateInstaller.WriteLog(text);
        }

        internal static void CheckAutomaticallyIfDue(Form owner)
        {
            if (!IsAutomaticCheckDue())
                return;
            CheckForUpdatesAsync(owner, false);
        }

        private static void DownloadAndInstallAsync(Form owner, UpdateRelease release)
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    string package = DownloadUpdate(release);
                    UpdateInstaller.Launch(package, release.Sha256,
                        Process.GetCurrentProcess().MainModule.FileName);
                    BeginOnUi(owner, delegate { owner.Close(); });
                }
                catch (Exception error)
                {
                    Show(owner, Loc.T("update.installFailed") +
                        error.Message, MessageBoxIcon.Warning);
                }
                finally
                {
                    Interlocked.Exchange(ref _checking, 0);
                }
            });
        }

        private static UpdateRelease FindUpdate()
        {
            GitHubRelease[] releases;
            HttpWebRequest request = CreateRequest(ReleaseConfiguration.ReleasesApiUrl);
            using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
            using (Stream stream = response.GetResponseStream())
            {
                DataContractJsonSerializer serializer =
                    new DataContractJsonSerializer(typeof(GitHubRelease[]));
                releases = (GitHubRelease[])serializer.ReadObject(stream);
            }

            SemanticVersion current = SemanticVersion.Parse(GetCurrentVersion());
            bool previewChannel = current.IsPrerelease;
            UpdateRelease best = null;
            if (releases == null)
                return null;

            foreach (GitHubRelease release in releases)
            {
                if (release == null || release.Draft || String.IsNullOrWhiteSpace(release.TagName))
                    continue;
                if (release.Prerelease && !previewChannel)
                    continue;

                SemanticVersion candidate;
                if (!SemanticVersion.TryParse(release.TagName, out candidate) || candidate.CompareTo(current) <= 0)
                    continue;

                GitHubAsset asset = FindAsset(release, ReleaseConfiguration.UpdateAssetName);
                GitHubAsset manifestAsset = FindAsset(release, ReleaseConfiguration.UpdateManifestName);
                GitHubAsset signatureAsset = FindAsset(release, ReleaseConfiguration.UpdateSignatureName);
                if (asset == null || manifestAsset == null || signatureAsset == null ||
                    asset.Size <= 0 || asset.Size > MaximumUpdateBytes ||
                    !IsSafeAssetUrl(asset.DownloadUrl) ||
                    !IsSafeAssetUrl(manifestAsset.DownloadUrl) ||
                    !IsSafeAssetUrl(signatureAsset.DownloadUrl))
                    continue;

                byte[] manifestBytes = DownloadBytes(manifestAsset.DownloadUrl,
                    MaximumManifestBytes);
                byte[] signatureBytes = DownloadBytes(signatureAsset.DownloadUrl,
                    MaximumManifestBytes);
                SignedUpdateManifest manifest = SignedUpdateManifest.VerifyAndParse(
                    manifestBytes, signatureBytes,
                    ReleaseConfiguration.UpdateSigningPublicKeyXml);
                if (!String.Equals(manifest.Version, release.TagName,
                        StringComparison.OrdinalIgnoreCase) ||
                    !String.Equals(manifest.AssetName, ReleaseConfiguration.UpdateAssetName,
                        StringComparison.Ordinal) ||
                    manifest.Size != asset.Size || manifest.Size > MaximumUpdateBytes)
                    continue;

                string githubDigest = ParseDigest(asset.Digest);
                if (githubDigest != null && !String.Equals(githubDigest, manifest.Sha256,
                    StringComparison.OrdinalIgnoreCase))
                    continue;

                if (best == null || candidate.CompareTo(best.Version) > 0)
                {
                    best = new UpdateRelease
                    {
                        Version = candidate,
                        VersionText = release.TagName,
                        DownloadUrl = asset.DownloadUrl,
                        Sha256 = manifest.Sha256,
                        Size = manifest.Size
                    };
                }
            }
            return best;
        }

        private static string DownloadUpdate(UpdateRelease release)
        {
            string directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Traymetry", "Updates", SanitizePathPart(release.VersionText));
            Directory.CreateDirectory(directory);
            string destination = Path.Combine(directory, "Traymetry.exe.download");

            HttpWebRequest request = CreateRequest(release.DownloadUrl);
            using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
            using (Stream input = response.GetResponseStream())
            using (FileStream output = new FileStream(destination, FileMode.Create,
                FileAccess.Write, FileShare.None))
                CopyWithLimit(input, output, release.Size);

            if (new FileInfo(destination).Length != release.Size)
            {
                File.Delete(destination);
                throw new InvalidDataException(Loc.T("update.sizeMismatch"));
            }

            string actual = UpdateInstaller.ComputeSha256(destination);
            if (!String.Equals(actual, release.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(destination);
                throw new InvalidDataException(Loc.T("update.hashMismatch"));
            }
            return destination;
        }

        private static GitHubAsset FindAsset(GitHubRelease release, string name)
        {
            if (release == null || release.Assets == null)
                return null;
            foreach (GitHubAsset asset in release.Assets)
                if (asset != null && String.Equals(asset.Name, name,
                    StringComparison.OrdinalIgnoreCase))
                    return asset;
            return null;
        }

        private static bool IsSafeAssetUrl(string url)
        {
            Uri uri;
            return Uri.TryCreate(url, UriKind.Absolute, out uri) &&
                String.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
                String.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase);
        }

        private static byte[] DownloadBytes(string url, int maximumBytes)
        {
            HttpWebRequest request = CreateRequest(url);
            using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
            using (Stream input = response.GetResponseStream())
            using (MemoryStream output = new MemoryStream())
            {
                CopyWithLimit(input, output, maximumBytes);
                return output.ToArray();
            }
        }

        private static void CopyWithLimit(Stream input, Stream output, long maximumBytes)
        {
            byte[] buffer = new byte[64 * 1024];
            long total = 0;
            while (true)
            {
                int read = input.Read(buffer, 0, buffer.Length);
                if (read <= 0)
                    break;
                total += read;
                if (total > maximumBytes)
                    throw new InvalidDataException(Loc.T("update.tooLarge"));
                output.Write(buffer, 0, read);
            }
        }

        private static HttpWebRequest CreateRequest(string url)
        {
            // Added rather than assigned: assigning turns off everything newer
            // than TLS 1.2, and this setting is process-wide.
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
            request.UserAgent = "Traymetry/" + GetCurrentVersion();
            request.Accept = "application/vnd.github+json";
            request.Headers["X-GitHub-Api-Version"] = "2022-11-28";
            request.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            request.Timeout = 15000;
            request.ReadWriteTimeout = 30000;
            return request;
        }

        private static string ParseDigest(string digest)
        {
            if (String.IsNullOrWhiteSpace(digest) ||
                !digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                return null;
            string value = digest.Substring(7).Trim();
            if (value.Length != 64)
                return null;
            foreach (char character in value)
                if (!Uri.IsHexDigit(character))
                    return null;
            return value.ToUpperInvariant();
        }

        private static bool IsAutomaticCheckDue()
        {
            try
            {
                using (Microsoft.Win32.RegistryKey key =
                    Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RegistryPath))
                {
                    string stored = key == null ? null : Convert.ToString(
                        key.GetValue(LastCheckValue, String.Empty), CultureInfo.InvariantCulture);
                    DateTime last;
                    if (DateTime.TryParse(stored, CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out last))
                        return DateTime.UtcNow - last.ToUniversalTime() >= TimeSpan.FromHours(24);
                }
            }
            catch { }
            return true;
        }

        private static void WriteLastCheckTime()
        {
            try
            {
                using (Microsoft.Win32.RegistryKey key =
                    Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RegistryPath))
                    key.SetValue(LastCheckValue, DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                        Microsoft.Win32.RegistryValueKind.String);
            }
            catch { }
        }

        private static string GetCurrentVersion()
        {
            object[] attributes = Assembly.GetExecutingAssembly().GetCustomAttributes(
                typeof(AssemblyInformationalVersionAttribute), false);
            if (attributes.Length > 0)
                return ((AssemblyInformationalVersionAttribute)attributes[0]).InformationalVersion;
            return Assembly.GetExecutingAssembly().GetName().Version.ToString();
        }

        private static string SanitizePathPart(string value)
        {
            StringBuilder result = new StringBuilder();
            foreach (char character in value ?? String.Empty)
                if (Char.IsLetterOrDigit(character) || character == '.' || character == '-' || character == '_')
                    result.Append(character);
            return result.Length == 0 ? "update" : result.ToString();
        }

        private static void Show(Form owner, string text, MessageBoxIcon icon)
        {
            BeginOnUi(owner, delegate
            {
                MessageBox.Show(owner, text, "Traymetry", MessageBoxButtons.OK, icon);
            });
        }

        private static bool BeginOnUi(Form owner, Action action)
        {
            if (owner == null || owner.IsDisposed || !owner.IsHandleCreated)
                return false;
            try
            {
                owner.BeginInvoke(action);
                return true;
            }
            catch (InvalidOperationException) { return false; }
        }

        [DataContract]
        private sealed class GitHubRelease
        {
            [DataMember(Name = "tag_name")]
            public string TagName { get; set; }

            [DataMember(Name = "draft")]
            public bool Draft { get; set; }

            [DataMember(Name = "prerelease")]
            public bool Prerelease { get; set; }

            [DataMember(Name = "assets")]
            public GitHubAsset[] Assets { get; set; }
        }

        [DataContract]
        private sealed class GitHubAsset
        {
            [DataMember(Name = "name")]
            public string Name { get; set; }

            [DataMember(Name = "browser_download_url")]
            public string DownloadUrl { get; set; }

            [DataMember(Name = "digest")]
            public string Digest { get; set; }

            [DataMember(Name = "size")]
            public long Size { get; set; }
        }

        private sealed class UpdateRelease
        {
            public SemanticVersion Version { get; set; }
            public string VersionText { get; set; }
            public string DownloadUrl { get; set; }
            public string Sha256 { get; set; }
            public long Size { get; set; }
        }
    }

    internal sealed class SignedUpdateManifest
    {
        private const string Header = "TRAYMETRY-UPDATE-MANIFEST-V1";

        internal string Version { get; private set; }
        internal string AssetName { get; private set; }
        internal string Sha256 { get; private set; }
        internal long Size { get; private set; }

        internal static byte[] CreateBytes(string version, string assetName,
            string sha256, long size)
        {
            if (String.IsNullOrWhiteSpace(version) ||
                !String.Equals(assetName, ReleaseConfiguration.UpdateAssetName,
                    StringComparison.Ordinal) || !IsSha256(sha256) || size <= 0)
                throw new ArgumentException(Loc.T("update.manifestInvalid"));
            string text = Header + "\n" +
                "version=" + version.Trim() + "\n" +
                "asset=" + assetName + "\n" +
                "sha256=" + sha256.ToUpperInvariant() + "\n" +
                "size=" + size.ToString(CultureInfo.InvariantCulture) + "\n";
            return new UTF8Encoding(false, true).GetBytes(text);
        }

        internal static SignedUpdateManifest VerifyAndParse(byte[] manifestBytes,
            byte[] signatureFileBytes, string publicKeyXml)
        {
            if (manifestBytes == null || manifestBytes.Length == 0 ||
                manifestBytes.Length > 32 * 1024 || signatureFileBytes == null ||
                signatureFileBytes.Length == 0 || signatureFileBytes.Length > 32 * 1024)
                throw new InvalidDataException(Loc.T("update.manifestSize"));

            byte[] signature;
            try
            {
                signature = Convert.FromBase64String(
                    Encoding.ASCII.GetString(signatureFileBytes).Trim());
            }
            catch (FormatException)
            {
                throw new InvalidDataException(Loc.T("update.signatureFormat"));
            }

            using (RSACryptoServiceProvider rsa = new RSACryptoServiceProvider())
            {
                rsa.PersistKeyInCsp = false;
                rsa.FromXmlString(publicKeyXml);
                if (!rsa.VerifyData(manifestBytes, CryptoConfig.MapNameToOID("SHA256"), signature))
                    throw new CryptographicException(Loc.T("update.signatureInvalid"));
            }

            string text;
            try { text = new UTF8Encoding(false, true).GetString(manifestBytes); }
            catch (DecoderFallbackException)
            {
                throw new InvalidDataException(Loc.T("update.manifestUtf8"));
            }
            string[] lines = text.Replace("\r\n", "\n").Split('\n');
            if (lines.Length != 6 || !String.Equals(lines[0], Header, StringComparison.Ordinal) ||
                lines[5].Length != 0)
                throw new InvalidDataException(Loc.T("update.manifestShape"));

            Dictionary<string, string> values = new Dictionary<string, string>(
                StringComparer.Ordinal);
            for (int index = 1; index <= 4; index++)
            {
                int separator = lines[index].IndexOf('=');
                if (separator <= 0 || separator == lines[index].Length - 1)
                    throw new InvalidDataException(Loc.T("update.manifestCorrupt"));
                string key = lines[index].Substring(0, separator);
                string value = lines[index].Substring(separator + 1);
                if (values.ContainsKey(key))
                    throw new InvalidDataException(Loc.T("update.manifestDuplicate"));
                values.Add(key, value);
            }

            string version;
            string asset;
            string sha256;
            string sizeText;
            long size;
            if (values.Count != 4 || !values.TryGetValue("version", out version) ||
                !values.TryGetValue("asset", out asset) ||
                !values.TryGetValue("sha256", out sha256) ||
                !values.TryGetValue("size", out sizeText) || !IsSha256(sha256) ||
                !Int64.TryParse(sizeText, NumberStyles.None, CultureInfo.InvariantCulture, out size) ||
                size <= 0)
                throw new InvalidDataException(Loc.T("update.manifestValues"));

            return new SignedUpdateManifest
            {
                Version = version,
                AssetName = asset,
                Sha256 = sha256.ToUpperInvariant(),
                Size = size
            };
        }

        private static bool IsSha256(string value)
        {
            if (String.IsNullOrWhiteSpace(value) || value.Length != 64)
                return false;
            foreach (char character in value)
                if (!Uri.IsHexDigit(character))
                    return false;
            return true;
        }
    }

    internal static class UpdateInstaller
    {
        private const string ApplyArgument = "--apply-update";
        private const string TestArgument = "--test-updater";
        private const string CleanupArgument = "--cleanup-update";
        private const string VerifyManifestArgument = "--verify-update-manifest";
        private const string FrameTelemetryTestArgument = "--test-frame-telemetry";
        private const string RefreshSensorServiceArgument = "--refresh-sensor-service";
        private const string UpdateFailedArgument = "--update-failed";

        internal static bool ShouldRefreshSensorService(string[] args)
        {
            return args != null && Array.Exists(args, delegate(string argument)
            {
                return String.Equals(argument, RefreshSensorServiceArgument,
                    StringComparison.OrdinalIgnoreCase);
            });
        }

        internal static bool ShouldShowUpdateFailure(string[] args)
        {
            return args != null && Array.Exists(args, delegate(string argument)
            {
                return String.Equals(argument, UpdateFailedArgument,
                    StringComparison.OrdinalIgnoreCase);
            });
        }

        internal static bool TryHandleCommandLine(string[] args, out int exitCode)
        {
            exitCode = 0;
            if (args == null || args.Length == 0)
                return false;
            if (String.Equals(args[0], ApplyArgument, StringComparison.OrdinalIgnoreCase))
            {
                exitCode = args.Length >= 2 ? Apply(args[1]) : 2;
                return true;
            }
            if (String.Equals(args[0], TestArgument, StringComparison.OrdinalIgnoreCase))
            {
                exitCode = RunSelfTest() ? 0 : 1;
                return true;
            }
            if (String.Equals(args[0], VerifyManifestArgument, StringComparison.OrdinalIgnoreCase))
            {
                exitCode = VerifyManifestFiles(args);
                return true;
            }
            if (String.Equals(args[0], FrameTelemetryTestArgument, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    FrameTelemetrySelfTest.Run();
                    exitCode = 0;
                }
                catch (Exception error)
                {
                    WriteLog("Frame telemetry self-test failed: " + error);
                    exitCode = 1;
                }
                return true;
            }
            if (String.Equals(args[0], CleanupArgument, StringComparison.OrdinalIgnoreCase))
            {
                if (args.Length >= 3)
                {
                    int helperProcessId;
                    if (Int32.TryParse(args[2], NumberStyles.None,
                        CultureInfo.InvariantCulture, out helperProcessId))
                        CleanupInBackground(args[1], helperProcessId);
                }
                return false;
            }
            return false;
        }

        private static int VerifyManifestFiles(string[] args)
        {
            if (args.Length < 3 || !File.Exists(args[1]) || !File.Exists(args[2]))
                return 2;
            try
            {
                SignedUpdateManifest.VerifyAndParse(
                    File.ReadAllBytes(args[1]),
                    File.ReadAllBytes(args[2]),
                    ReleaseConfiguration.UpdateSigningPublicKeyXml);
                return 0;
            }
            catch (Exception error)
            {
                WriteLog("Release manifest verification failed: " + error.Message);
                return 1;
            }
        }

        internal static void Launch(string downloadedPath, string sha256, string targetPath)
        {
            if (!File.Exists(downloadedPath) || !File.Exists(targetPath))
                throw new FileNotFoundException(Loc.T("update.fileMissing"));
            if (IsRunningElevated())
                throw new InvalidOperationException(
                    Loc.T("update.elevated"));
            EnsureTargetDirectoryWritable(targetPath);
            if (!String.Equals(ComputeSha256(downloadedPath), sha256,
                StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(Loc.T("update.hashCheckFailed"));

            string workDirectory = Path.Combine(Path.GetTempPath(),
                "Traymetry.Update." + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(workDirectory);
            string helperPath = Path.Combine(workDirectory, "Traymetry.Update.exe");
            string jobPath = Path.Combine(workDirectory, "update.job");
            File.Copy(targetPath, helperPath, true);
            bool updateSensorService = File.Exists(SensorServiceInstaller.HostPath);
            File.WriteAllLines(jobPath, new[]
            {
                Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture),
                Encode(targetPath),
                Encode(downloadedPath),
                sha256,
                updateSensorService ? "1" : "0"
            }, Encoding.UTF8);

            ProcessStartInfo start = new ProcessStartInfo
            {
                FileName = helperPath,
                Arguments = ApplyArgument + " " + Quote(jobPath),
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = workDirectory
            };
            if (Process.Start(start) == null)
                throw new InvalidOperationException(Loc.T("update.helperFailed"));
        }

        private static bool IsRunningElevated()
        {
            using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
                return new WindowsPrincipal(identity).IsInRole(
                    WindowsBuiltInRole.Administrator);
        }

        private static void EnsureTargetDirectoryWritable(string targetPath)
        {
            string directory = Path.GetDirectoryName(Path.GetFullPath(targetPath));
            if (String.IsNullOrWhiteSpace(directory))
                throw new InvalidOperationException(Loc.T("update.folderUnknown"));
            string probe = Path.Combine(directory,
                ".Traymetry.write-test." + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (FileStream stream = new FileStream(probe, FileMode.CreateNew,
                    FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose))
                    stream.WriteByte(0);
            }
            catch (UnauthorizedAccessException)
            {
                throw new InvalidOperationException(
                    Loc.T("update.protectedFolder"));
            }
        }

        private static int Apply(string jobPath)
        {
            string targetPath = null;
            try
            {
                string[] lines = File.ReadAllLines(jobPath, Encoding.UTF8);
                if (lines.Length < 4)
                    return 3;
                int processId = Int32.Parse(lines[0], CultureInfo.InvariantCulture);
                targetPath = Decode(lines[1]);
                string downloadedPath = Decode(lines[2]);
                string expectedSha256 = lines[3].Trim();
                bool updateSensorService = lines.Length >= 5 &&
                    String.Equals(lines[4].Trim(), "1", StringComparison.Ordinal);

                WaitForProcess(processId);
                if (!String.Equals(ComputeSha256(downloadedPath), expectedSha256,
                    StringComparison.OrdinalIgnoreCase))
                    return 4;

                string backupPath = targetPath + ".previous";
                ReplaceFile(downloadedPath, targetPath, backupPath);

                try { File.Delete(downloadedPath); }
                catch { }
                string launchArguments = CleanupArgument + " " +
                    Quote(Path.GetDirectoryName(jobPath)) + " " +
                    Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture);
                if (updateSensorService)
                    launchArguments += " " + RefreshSensorServiceArgument;
                Process.Start(new ProcessStartInfo
                {
                    FileName = targetPath,
                    Arguments = launchArguments,
                    UseShellExecute = true,
                    WorkingDirectory = Path.GetDirectoryName(targetPath)
                });
                return 0;
            }
            catch (Exception error)
            {
                WriteLog(error.ToString());
                TryRelaunchAfterFailure(targetPath, jobPath);
                return 1;
            }
        }

        private static void TryRelaunchAfterFailure(string targetPath, string jobPath)
        {
            try
            {
                if (String.IsNullOrWhiteSpace(targetPath) || !File.Exists(targetPath))
                    return;
                string arguments = UpdateFailedArgument;
                string directory = Path.GetDirectoryName(jobPath);
                if (!String.IsNullOrWhiteSpace(directory))
                {
                    arguments = CleanupArgument + " " + Quote(directory) + " " +
                        Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture) +
                        " " + UpdateFailedArgument;
                }
                Process.Start(new ProcessStartInfo
                {
                    FileName = targetPath,
                    Arguments = arguments,
                    UseShellExecute = true,
                    WorkingDirectory = Path.GetDirectoryName(targetPath)
                });
            }
            catch (Exception relaunchError)
            {
                WriteLog("Relaunch after update failure failed: " + relaunchError.Message);
            }
        }

        private static void ReplaceFile(string sourcePath, string targetPath, string backupPath)
        {
            string targetDirectory = Path.GetDirectoryName(targetPath);
            if (String.IsNullOrWhiteSpace(targetDirectory))
                throw new InvalidOperationException(Loc.T("update.installedFolderUnknown"));
            string stagedPath = Path.Combine(targetDirectory,
                ".Traymetry.update." + Guid.NewGuid().ToString("N") + ".tmp");
            string sourceHash = ComputeSha256(sourcePath);
            if (File.Exists(backupPath))
                File.Delete(backupPath);
            try
            {
                // File.Replace is atomic on the supported Windows/NTFS target
                // and creates a rollback copy in the same operation.
                File.Copy(sourcePath, stagedPath, true);
                if (!String.Equals(ComputeSha256(stagedPath), sourceHash,
                    StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException(Loc.T("update.stagedCheckFailed"));
                File.Replace(stagedPath, targetPath, backupPath, true);
                if (!String.Equals(ComputeSha256(targetPath), sourceHash,
                    StringComparison.OrdinalIgnoreCase))
                {
                    if (File.Exists(backupPath))
                        File.Replace(backupPath, targetPath, null, true);
                    throw new IOException(Loc.T("update.installedCheckFailed"));
                }
            }
            finally
            {
                try
                {
                    if (File.Exists(stagedPath))
                        File.Delete(stagedPath);
                }
                catch { }
            }
        }

        private static void WaitForProcess(int processId)
        {
            try
            {
                using (Process process = Process.GetProcessById(processId))
                    if (!process.WaitForExit(30000))
                        throw new TimeoutException(Loc.T("update.exitTimeout"));
            }
            catch (ArgumentException) { }
        }

        private static void CleanupInBackground(string directory, int helperProcessId)
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    string fullDirectory = Path.GetFullPath(directory);
                    string tempRoot = Path.GetFullPath(Path.GetTempPath()).
                        TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                    string leaf = Path.GetFileName(fullDirectory.TrimEnd(
                        Path.DirectorySeparatorChar));
                    if (!fullDirectory.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase) ||
                        !leaf.StartsWith("Traymetry.Update.", StringComparison.Ordinal))
                        return;
                    WaitForProcess(helperProcessId);
                    if (Directory.Exists(fullDirectory))
                        Directory.Delete(fullDirectory, true);
                }
                catch (Exception error)
                {
                    WriteLog("Update cleanup failed: " + error.Message);
                }
            });
        }

        internal static string ComputeSha256(string path)
        {
            using (SHA256 sha = SHA256.Create())
            using (FileStream stream = File.OpenRead(path))
                return ToHex(sha.ComputeHash(stream));
        }

        private static string ToHex(byte[] hash)
        {
            StringBuilder result = new StringBuilder(hash.Length * 2);
            foreach (byte item in hash)
                result.Append(item.ToString("X2", CultureInfo.InvariantCulture));
            return result.ToString();
        }

        /// <summary>
        /// Checks the key this build will judge every update by.  The key is a
        /// string in the source, and a string in the source is a thing that can
        /// be replaced in a pull request without anyone reading its digits; the
        /// fingerprint beside it is the second place that would have to be
        /// changed to match, and this is what makes the two agree.  The self-test
        /// runs in the build, so a swapped key is a build that does not finish.
        ///
        /// Public-only is checked as well.  Signing is done on the maintainer's
        /// machine and the private key never belongs here: pasting it in by
        /// accident would ship the ability to sign updates to everyone who
        /// downloads one.
        /// </summary>
        private static bool ShippedSigningKeyIsSound()
        {
            string xml = ReleaseConfiguration.UpdateSigningPublicKeyXml;
            using (SHA256 sha = SHA256.Create())
            {
                string fingerprint = ToHex(sha.ComputeHash(Encoding.UTF8.GetBytes(xml)));
                if (!String.Equals(fingerprint,
                        ReleaseConfiguration.UpdateSigningKeyFingerprint,
                        StringComparison.OrdinalIgnoreCase))
                {
                    WriteLog("Self-test: update signing key fingerprint is " +
                        fingerprint + ", expected " +
                        ReleaseConfiguration.UpdateSigningKeyFingerprint);
                    return false;
                }
            }
            using (RSACryptoServiceProvider rsa = new RSACryptoServiceProvider())
            {
                rsa.PersistKeyInCsp = false;
                rsa.FromXmlString(xml);
                if (!rsa.PublicOnly)
                {
                    WriteLog("Self-test: the update signing key in this build is not public-only.");
                    return false;
                }
                if (rsa.KeySize < 2048)
                {
                    WriteLog("Self-test: update signing key is " + rsa.KeySize +
                        " bits, which is below the 2048 this program will accept.");
                    return false;
                }
            }
            return true;
        }

        private static bool RunSelfTest()
        {
            string directory = Path.Combine(Path.GetTempPath(),
                "Traymetry.Update.Test." + Guid.NewGuid().ToString("N"));
            try
            {
                if (!ShippedSigningKeyIsSound())
                    return false;
                Directory.CreateDirectory(directory);
                string target = Path.Combine(directory, "Traymetry.exe");
                string source = Path.Combine(directory, "Traymetry.new.exe");
                string backup = target + ".previous";
                File.WriteAllText(target, "old", Encoding.UTF8);
                File.WriteAllText(source, "new", Encoding.UTF8);
                string hash = ComputeSha256(source);
                ReplaceFile(source, target, backup);
                if (!String.Equals(hash, ComputeSha256(target), StringComparison.Ordinal))
                    return false;

                byte[] manifest = SignedUpdateManifest.CreateBytes(
                    "v0.9.0-preview.37", ReleaseConfiguration.UpdateAssetName,
                    hash, new FileInfo(target).Length);
                byte[] signature;
                string publicKey;
                using (RSACryptoServiceProvider rsa = new RSACryptoServiceProvider(2048))
                {
                    rsa.PersistKeyInCsp = false;
                    publicKey = rsa.ToXmlString(false);
                    signature = rsa.SignData(manifest,
                        CryptoConfig.MapNameToOID("SHA256"));
                }
                SignedUpdateManifest verified = SignedUpdateManifest.VerifyAndParse(
                    manifest,
                    Encoding.ASCII.GetBytes(Convert.ToBase64String(signature)),
                    publicKey);
                byte[] corruptedManifest = (byte[])manifest.Clone();
                corruptedManifest[corruptedManifest.Length - 2] ^= 1;
                bool rejectedCorruption = false;
                try
                {
                    SignedUpdateManifest.VerifyAndParse(corruptedManifest,
                        Encoding.ASCII.GetBytes(Convert.ToBase64String(signature)), publicKey);
                }
                catch (CryptographicException) { rejectedCorruption = true; }

                return rejectedCorruption && verified.Size == new FileInfo(target).Length &&
                    String.Equals(verified.Sha256, hash, StringComparison.Ordinal) &&
                    File.ReadAllText(target, Encoding.UTF8) == "new" &&
                    File.ReadAllText(backup, Encoding.UTF8) == "old" &&
                    SemanticVersion.Parse("0.9.0-preview.37").CompareTo(
                        SemanticVersion.Parse("0.9.0-preview.36")) > 0 &&
                    SemanticVersion.Parse("0.9.0").CompareTo(
                        SemanticVersion.Parse("0.9.0-preview.99")) > 0;
            }
            catch (Exception error)
            {
                WriteLog("Self-test: " + error);
                return false;
            }
            finally
            {
                try { Directory.Delete(directory, true); }
                catch { }
            }
        }

        private static string Encode(string value)
        {
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(value ?? String.Empty));
        }

        private static string Decode(string value)
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(value));
        }

        private static string Quote(string value)
        {
            return "\"" + (value ?? String.Empty).Replace("\"", "\\\"") + "\"";
        }

        /// <summary>
        /// Updating keeps its own log rather than sharing the widget's: it runs
        /// in processes that have no widget - the installer relaunch, the
        /// self-tests, the manifest verifier - and it has to survive the moment
        /// the executable underneath it is replaced.  The problem report picks
        /// it up from here, so a report answers "why does it never update" as
        /// well as it answers everything else.
        /// </summary>
        internal static string LogPath
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Traymetry", "update.log");
            }
        }

        internal static void WriteLog(string text)
        {
            try
            {
                string path = LogPath;
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.AppendAllText(path,
                    DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) + " " + text +
                    Environment.NewLine, Encoding.UTF8);
            }
            catch { }
        }
    }

    internal sealed class SemanticVersion : IComparable<SemanticVersion>
    {
        private readonly int[] _numbers;
        private readonly string[] _prerelease;

        private SemanticVersion(int[] numbers, string[] prerelease)
        {
            _numbers = numbers;
            _prerelease = prerelease;
        }

        internal bool IsPrerelease
        {
            get { return _prerelease.Length > 0; }
        }

        internal static SemanticVersion Parse(string value)
        {
            SemanticVersion result;
            if (!TryParse(value, out result))
                throw new FormatException(Loc.T("update.badVersion", value));
            return result;
        }

        internal static bool TryParse(string value, out SemanticVersion result)
        {
            result = null;
            if (String.IsNullOrWhiteSpace(value))
                return false;
            value = value.Trim();
            if (value.StartsWith("v", StringComparison.OrdinalIgnoreCase))
                value = value.Substring(1);
            int metadata = value.IndexOf('+');
            if (metadata >= 0)
                value = value.Substring(0, metadata);
            string[] halves = value.Split(new[] { '-' }, 2);
            string[] core = halves[0].Split('.');
            if (core.Length < 2 || core.Length > 4)
                return false;
            int[] numbers = new int[Math.Max(3, core.Length)];
            for (int index = 0; index < core.Length; index++)
                if (!Int32.TryParse(core[index], NumberStyles.None,
                    CultureInfo.InvariantCulture, out numbers[index]) || numbers[index] < 0)
                    return false;
            string[] prerelease = halves.Length == 2 && halves[1].Length > 0
                ? halves[1].Split('.')
                : new string[0];
            result = new SemanticVersion(numbers, prerelease);
            return true;
        }

        public int CompareTo(SemanticVersion other)
        {
            if (other == null)
                return 1;
            int length = Math.Max(_numbers.Length, other._numbers.Length);
            for (int index = 0; index < length; index++)
            {
                int left = index < _numbers.Length ? _numbers[index] : 0;
                int right = index < other._numbers.Length ? other._numbers[index] : 0;
                if (left != right)
                    return left.CompareTo(right);
            }
            if (_prerelease.Length == 0 || other._prerelease.Length == 0)
                return _prerelease.Length == other._prerelease.Length ? 0 :
                    (_prerelease.Length == 0 ? 1 : -1);
            length = Math.Max(_prerelease.Length, other._prerelease.Length);
            for (int index = 0; index < length; index++)
            {
                if (index >= _prerelease.Length)
                    return -1;
                if (index >= other._prerelease.Length)
                    return 1;
                int leftNumber;
                int rightNumber;
                bool leftNumeric = Int32.TryParse(_prerelease[index], NumberStyles.None,
                    CultureInfo.InvariantCulture, out leftNumber);
                bool rightNumeric = Int32.TryParse(other._prerelease[index], NumberStyles.None,
                    CultureInfo.InvariantCulture, out rightNumber);
                int comparison;
                if (leftNumeric && rightNumeric)
                    comparison = leftNumber.CompareTo(rightNumber);
                else if (leftNumeric != rightNumeric)
                    comparison = leftNumeric ? -1 : 1;
                else
                    comparison = StringComparer.OrdinalIgnoreCase.Compare(
                        _prerelease[index], other._prerelease[index]);
                if (comparison != 0)
                    return comparison;
            }
            return 0;
        }
    }
}
