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
                VerifyHash(installerPath);
                using (FileStream installerLock = new FileStream(installerPath, FileMode.Open,
                    FileAccess.Read, FileShare.Read))
                {
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
                            throw new InvalidOperationException("Не удалось запустить установщик PawnIO.");
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

        private static void DownloadInstaller(string destination)
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(InstallerUrl);
            request.UserAgent = "Traymetry PawnIO bootstrap/1.0";
            request.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            request.Timeout = 30000;
            request.ReadWriteTimeout = 60000;
            using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
            using (Stream input = response.GetResponseStream())
            using (FileStream output = new FileStream(destination, FileMode.CreateNew,
                FileAccess.Write, FileShare.None))
                input.CopyTo(output);
        }

        private static void VerifyHash(string installerPath)
        {
            string actual;
            using (SHA256 sha = SHA256.Create())
            using (FileStream stream = File.OpenRead(installerPath))
                actual = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", String.Empty);
            if (!String.Equals(actual, InstallerSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Контрольная сумма официального установщика PawnIO не совпадает.");
        }

        private static void VerifySigner(string path)
        {
            X509Certificate certificate = X509Certificate.CreateFromSignedFile(path);
            using (X509Certificate2 signer = new X509Certificate2(certificate))
            {
                string thumbprint = (signer.Thumbprint ?? String.Empty).Replace(" ", String.Empty);
                if (!String.Equals(thumbprint, SignerThumbprint, StringComparison.OrdinalIgnoreCase) ||
                    signer.Subject.IndexOf("namazso", StringComparison.OrdinalIgnoreCase) < 0)
                    throw new InvalidDataException("Цифровая подпись установщика PawnIO не соответствует ожидаемому издателю.");
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
