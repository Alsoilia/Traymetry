using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Win32;

namespace Traymetry
{
    internal static class EmbeddedDependencies
    {
        private const string ResourcePrefix = "Traymetry.Embedded.";
        private static bool _registered;

        public static void Register()
        {
            if (_registered)
                return;
            _registered = true;
            AppDomain.CurrentDomain.AssemblyResolve += ResolveAssembly;
        }

        public static byte[] ReadResource(string resourceName)
        {
            Assembly assembly = Assembly.GetExecutingAssembly();
            using (Stream input = assembly.GetManifestResourceStream(resourceName))
            {
                if (input == null)
                    throw new InvalidOperationException("Embedded resource was not found: " + resourceName);
                using (MemoryStream output = new MemoryStream())
                {
                    input.CopyTo(output);
                    return output.ToArray();
                }
            }
        }

        private static Assembly ResolveAssembly(object sender, ResolveEventArgs args)
        {
            string simpleName;
            try { simpleName = new AssemblyName(args.Name).Name; }
            catch { return null; }

            string resourceName = ResourcePrefix + simpleName + ".dll";
            Assembly current = Assembly.GetExecutingAssembly();
            using (Stream input = current.GetManifestResourceStream(resourceName))
            {
                if (input == null)
                    return null;
                using (MemoryStream output = new MemoryStream())
                {
                    input.CopyTo(output);
                    return Assembly.Load(output.ToArray());
                }
            }
        }
    }

    internal static class PawnIoBootstrap
    {
        private const string ProductVersion = "2.2.0.0";
        private const string InstallerUrl =
            "https://github.com/namazso/PawnIO.Setup/releases/download/2.2.0/PawnIO_setup.exe";
        private const string InstallerFileName = "PawnIO_setup_2.2.0.exe";
        private const string InstallerSha256 = "1F519A22E47187F70A1379A48CA604981C4FCF694F4E65B734AAA74A9FBA3032";
        private const string SignerThumbprint = "F380DCC9F706E2756A5047B832FFE719E1BC35F5";
        private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO";

        public static bool IsInstalled
        {
            get { return ReadInstalledVersion().CompareTo(new Version(ProductVersion)) >= 0; }
        }

        internal static int InstallAsAdministrator()
        {
            if (IsInstalled)
                return 0;

            string temporaryDirectory = null;
            try
            {
                temporaryDirectory = SensorServiceInstaller.CreateSecureTemporaryDirectory();
                string installerPath = Path.Combine(temporaryDirectory, InstallerFileName);
                DownloadInstaller(installerPath);
                // Everything is checked with the file held open and no writer
                // allowed, so what is hashed, what is checked for a signature
                // and what is run are the same bytes.  Checked before the lock,
                // each answer is only about the file as it was at the time of
                // asking, and the file that ends up being run is whatever is
                // there by then.
                using (FileStream installerLock = new FileStream(installerPath, FileMode.Open,
                    FileAccess.Read, FileShare.Read))
                {
                    VerifyHash(installerPath);
                    VerifySigner(installerPath);
                    ProcessStartInfo start = new ProcessStartInfo
                    {
                        FileName = installerPath,
                        Arguments = "-install -silent",
                        WorkingDirectory = temporaryDirectory,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        WindowStyle = ProcessWindowStyle.Hidden
                    };
                    using (Process process = Process.Start(start))
                    {
                        if (process == null)
                            throw new InvalidOperationException(Loc.T("pawnio.launchFailed"));
                        process.WaitForExit();
                        return process.ExitCode;
                    }
                }
            }
            finally
            {
                TryDeleteDirectory(temporaryDirectory);
            }
        }

        private static Version ReadInstalledVersion()
        {
            foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using (RegistryKey root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
                    using (RegistryKey key = root.OpenSubKey(UninstallKey))
                    {
                        if (key == null)
                            continue;
                        string text = Convert.ToString(key.GetValue("DisplayVersion", String.Empty));
                        Version version;
                        if (Version.TryParse(text, out version))
                            return version;
                    }
                }
                catch (UnauthorizedAccessException) { }
                catch (System.Security.SecurityException) { }
            }
            return new Version(0, 0, 0, 0);
        }

        /// <summary>
        /// The installer is a couple of megabytes; anything approaching this
        /// ceiling is not it.  The hash is only checked once the file is on
        /// disk, so without a limit a wrong answer at the other end - a captive
        /// portal, a hijacked mirror, a server that simply never stops talking -
        /// gets to fill the disk before anyone asks what it sent.
        /// </summary>
        private const long MaximumInstallerBytes = 32L * 1024L * 1024L;

        private static void DownloadInstaller(string destination)
        {
            // Added rather than assigned: assigning turns off everything newer
            // than TLS 1.2, and this setting is process-wide.
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(InstallerUrl);
            request.UserAgent = "Traymetry PawnIO bootstrap/1.0";
            request.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            request.Timeout = 30000;
            request.ReadWriteTimeout = 60000;
            using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
            using (Stream input = response.GetResponseStream())
            using (FileStream output = new FileStream(destination, FileMode.CreateNew,
                FileAccess.Write, FileShare.None))
            {
                byte[] buffer = new byte[64 * 1024];
                long total = 0;
                while (true)
                {
                    int read = input.Read(buffer, 0, buffer.Length);
                    if (read <= 0)
                        break;
                    total += read;
                    if (total > MaximumInstallerBytes)
                        throw new InvalidDataException(Loc.T("pawnio.tooLarge"));
                    output.Write(buffer, 0, read);
                }
            }
        }

        private static void VerifyHash(string installerPath)
        {
            string actual;
            using (SHA256 sha = SHA256.Create())
            using (FileStream stream = File.OpenRead(installerPath))
                actual = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", String.Empty);
            if (!String.Equals(actual, InstallerSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(Loc.T("pawnio.hashMismatch"));
        }

        private static void VerifySigner(string path)
        {
            X509Certificate certificate = X509Certificate.CreateFromSignedFile(path);
            using (X509Certificate2 signer = new X509Certificate2(certificate))
            {
                string thumbprint = (signer.Thumbprint ?? String.Empty).Replace(" ", String.Empty);
                if (!String.Equals(thumbprint, SignerThumbprint, StringComparison.OrdinalIgnoreCase) ||
                    signer.Subject.IndexOf("namazso", StringComparison.OrdinalIgnoreCase) < 0)
                    throw new InvalidDataException(Loc.T("pawnio.signerMismatch"));
            }
        }

        private static void TryDeleteDirectory(string path)
        {
            if (String.IsNullOrEmpty(path))
                return;
            try
            {
                if (Directory.Exists(path))
                    Directory.Delete(path, true);
            }
            catch { }
        }
    }
}
