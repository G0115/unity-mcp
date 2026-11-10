using System;
using System.Diagnostics;

namespace MCPForUnity.Editor.Security
{
    /// <summary>
    /// Linux libsecret-backed key store via the `secret-tool` CLI. The secret value is
    /// passed on stdin (never as a process argument). Falls back to
    /// <see cref="EncryptedFileKeyStore"/> when secret-tool is not installed.
    /// </summary>
    internal sealed class LinuxSecretToolKeyStore : ISecureKeyStore
    {
        private const string Service = SecureKeyStoreConstants.ServiceName;

        // netstandard2.0 has no ProcessStartInfo.ArgumentList; emulate with quoted args
        private static void AppendArg(System.Diagnostics.ProcessStartInfo psi, string arg)
        {
            bool needsQuote = arg.Length == 0 || arg.IndexOfAny(new[] { ' ', '\t', '"', '\'' }) >= 0;
            string quoted = needsQuote ? "\"" + arg.Replace("\"", "\\\"") + "\"" : arg;
            psi.Arguments += (psi.Arguments.Length > 0 ? " " : "") + quoted;
        }

        internal static bool IsAvailable()
        {
            try
            {
                var psi = NewPsi();
                AppendArg(psi, "--version");
                using (var p = Process.Start(psi))
                {
                    p.WaitForExit(2000);
                    return p.ExitCode == 0;
                }
            }
            catch { return false; }
        }

        public bool Has(string providerId) => TryGet(providerId, out _);

        public bool TryGet(string providerId, out string apiKey)
        {
            apiKey = null;
            if (string.IsNullOrEmpty(providerId)) return false;
            try
            {
                var psi = NewPsi();
                AppendArg(psi, "lookup");
                AppendArg(psi, "service"); AppendArg(psi, Service);
                AppendArg(psi, "account"); AppendArg(psi, providerId);
                using (var p = Process.Start(psi))
                {
                    string outp = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(5000);
                    if (p.ExitCode != 0) return false;
                    apiKey = (outp ?? string.Empty).TrimEnd('\n', '\r');
                    return !string.IsNullOrEmpty(apiKey);
                }
            }
            catch { return false; }
        }

        public void Set(string providerId, string apiKey)
        {
            if (string.IsNullOrEmpty(providerId)) return;
            if (string.IsNullOrEmpty(apiKey)) { Delete(providerId); return; }
            try
            {
                var psi = NewPsi(redirectIn: true);
                AppendArg(psi, "store");
                AppendArg(psi, "--label=MCPForUnity AssetGen");
                AppendArg(psi, "service"); AppendArg(psi, Service);
                AppendArg(psi, "account"); AppendArg(psi, providerId);
                using (var p = Process.Start(psi))
                {
                    p.StandardInput.Write(apiKey);
                    p.StandardInput.Close();
                    p.WaitForExit(5000);
                }
            }
            catch { /* best effort */ }
        }

        public void Delete(string providerId)
        {
            if (string.IsNullOrEmpty(providerId)) return;
            try
            {
                var psi = NewPsi();
                AppendArg(psi, "clear");
                AppendArg(psi, "service"); AppendArg(psi, Service);
                AppendArg(psi, "account"); AppendArg(psi, providerId);
                using (var p = Process.Start(psi)) p.WaitForExit(5000);
            }
            catch { /* best effort */ }
        }

        private static ProcessStartInfo NewPsi(bool redirectIn = false)
        {
            var psi = new ProcessStartInfo("/usr/bin/env")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = redirectIn,
            };
            AppendArg(psi, "secret-tool");
            return psi;
        }
    }
}
