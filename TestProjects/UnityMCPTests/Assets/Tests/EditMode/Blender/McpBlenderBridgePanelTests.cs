using System;
using System.Collections;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using MCPForUnity.Editor.Constants;
using MCPForUnity.Editor.Windows.Components.AssetGen;
using NUnit.Framework;
using UnityEditor;
using UnityEngine.TestTools;

namespace MCPForUnityTests.Editor.Blender
{
    /// <summary>
    /// Drives the IMGUI Blender Bridge panel controller: the addon status fields, the connection
    /// status (text + state) and the automatic socket probe against a fake addon. State is asserted
    /// through the panel's private fields via reflection. Every Blender EditorPref is saved and
    /// restored, and the socket defaults to a closed local port so no test ever reaches a real
    /// Blender.
    /// </summary>
    public class McpBlenderBridgePanelTests
    {
        private string savedHost, savedFork, savedAddons;
        private int? savedPort;
        private string root;

        [SetUp]
        public void SetUp()
        {
            savedHost = EditorPrefs.HasKey(EditorPrefKeys.BlenderHost) ? EditorPrefs.GetString(EditorPrefKeys.BlenderHost) : null;
            savedPort = EditorPrefs.HasKey(EditorPrefKeys.BlenderPort) ? EditorPrefs.GetInt(EditorPrefKeys.BlenderPort) : (int?)null;
            savedFork = EditorPrefs.HasKey(EditorPrefKeys.BlenderForkPath) ? EditorPrefs.GetString(EditorPrefKeys.BlenderForkPath) : null;
            savedAddons = EditorPrefs.HasKey(EditorPrefKeys.BlenderAddonsDir) ? EditorPrefs.GetString(EditorPrefKeys.BlenderAddonsDir) : null;

            root = Path.Combine(Path.GetTempPath(), "blender-panel-test-" + Guid.NewGuid().ToString("N")).Replace('\\', '/');
            BlenderBridgePrefs.Host = "127.0.0.1";
            BlenderBridgePrefs.Port = ClosedPort();
            BlenderBridgePrefs.ForkPath = string.Empty;
            BlenderBridgePrefs.AddonsDirOverride = root + "/missing/scripts/addons";
        }

        [TearDown]
        public void TearDown()
        {
            Restore(EditorPrefKeys.BlenderHost, savedHost);
            Restore(EditorPrefKeys.BlenderForkPath, savedFork);
            Restore(EditorPrefKeys.BlenderAddonsDir, savedAddons);
            if (savedPort.HasValue) EditorPrefs.SetInt(EditorPrefKeys.BlenderPort, savedPort.Value);
            else EditorPrefs.DeleteKey(EditorPrefKeys.BlenderPort);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }

        private static void Restore(string key, string value)
        {
            if (value == null) EditorPrefs.DeleteKey(key);
            else EditorPrefs.SetString(key, value);
        }

        /// <summary>A local port nothing listens on, so a probe fails fast instead of finding a real Blender.</summary>
        private static int ClosedPort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        /// <summary>Builds the panel controller; the constructor syncs prefs and probes the socket.</summary>
        private static McpBlenderBridgePanel BuildPanel() => new McpBlenderBridgePanel();

        private static object Field(object panel, string name) =>
            typeof(McpBlenderBridgePanel).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(panel);

        /// <summary>The header status line next to the status dot.</summary>
        private static string StatusText(object panel) => (string)Field(panel, "statusText");

        /// <summary>Status dot colour as an int: 1 = green (reachable), -1 = red (failed), 0 = amber (unknown).</summary>
        private static int StatusState(object panel) => (int)Field(panel, "statusState");

        /// <summary>The resolved addons-dir / addon-installation line.</summary>
        private static string AddonsStatus(object panel) => (string)Field(panel, "addonsResolvedText");

        /// <summary>True when the addons line is green, dim (grey) otherwise.</summary>
        private static bool AddonsStatusOk(object panel) => (bool)Field(panel, "addonsResolvedOk");

        /// <summary>True while a bridge action is in flight (action buttons disabled).</summary>
        private static bool Busy(object panel) => (bool)Field(panel, "busy");

        [Test]
        public void AddonStatus_MissingAddonsFolder_IsGrayAndNotCreatedYet()
        {
            var panel = BuildPanel();
            StringAssert.Contains("(not created yet)", AddonsStatus(panel));
            StringAssert.Contains("BlenderMCP addon: not installed", AddonsStatus(panel));
            Assert.IsFalse(AddonsStatusOk(panel), "A missing addons folder must keep the dim (grey) status.");
        }

        [Test]
        public void AddonStatus_FolderWithoutAddon_ReportsNotInstalled()
        {
            Directory.CreateDirectory(root + "/addons");
            BlenderBridgePrefs.AddonsDirOverride = root + "/addons";
            var panel = BuildPanel();
            StringAssert.DoesNotContain("not created yet", AddonsStatus(panel));
            StringAssert.Contains("BlenderMCP addon: not installed", AddonsStatus(panel));
            Assert.IsFalse(AddonsStatusOk(panel), "A folder without the addon must keep the dim (grey) status.");
        }

        [Test]
        public void AddonStatus_FolderWithAddon_IsGreen()
        {
            Directory.CreateDirectory(root + "/addons");
            File.WriteAllText(root + "/addons/" + BlenderBridgePrefs.AddonFileName, "# addon\n");
            BlenderBridgePrefs.AddonsDirOverride = root + "/addons";
            var panel = BuildPanel();
            StringAssert.Contains("BlenderMCP addon: installed", AddonsStatus(panel));
            Assert.IsTrue(AddonsStatusOk(panel), "An installed addon must turn the status green.");
        }

        [Test]
        public void AddonStatus_WithCheckout_ComparesInstalledAddon()
        {
            Directory.CreateDirectory(root + "/addons");
            Directory.CreateDirectory(root + "/checkout");
            File.WriteAllText(root + "/addons/" + BlenderBridgePrefs.AddonFileName, "# addon v1\n");
            File.WriteAllText(root + "/checkout/" + BlenderBridgePrefs.AddonFileName, "# addon v1\n");
            BlenderBridgePrefs.AddonsDirOverride = root + "/addons";
            BlenderBridgePrefs.ForkPath = root + "/checkout";

            var panel = BuildPanel();
            StringAssert.Contains("in sync with checkout", AddonsStatus(panel));
            Assert.IsTrue(AddonsStatusOk(panel));

            File.WriteAllText(root + "/checkout/" + BlenderBridgePrefs.AddonFileName, "# addon v2\n");
            panel = BuildPanel();
            StringAssert.Contains("differs from checkout (Sync Addon)", AddonsStatus(panel));
            Assert.IsFalse(AddonsStatusOk(panel));
        }

        [Test]
        public void CheckoutRow_IsOptional_AndSyncAddonNeedsACheckout()
        {
            var panel = BuildPanel();
            Assert.IsFalse(BlenderBridgePrefs.IsForkConfigured, "The checkout is optional; without one Sync Addon is gated off.");

            Directory.CreateDirectory(root + "/checkout");
            File.WriteAllText(root + "/checkout/" + BlenderBridgePrefs.AddonFileName, "# addon\n");
            BlenderBridgePrefs.ForkPath = root + "/checkout";
            Assert.IsTrue(BlenderBridgePrefs.IsForkConfigured, "A checkout containing addon.py enables Sync Addon and Check Updates.");
        }

        /// <summary>Answers every connection like the BlenderMCP addon answering get_scene_info.</summary>
        private static async Task ServeFakeAddon(TcpListener listener)
        {
            byte[] reply = Encoding.UTF8.GetBytes("{\"status\": \"success\", \"result\": {\"name\": \"Scene\", \"object_count\": 0}}");
            while (true)
            {
                TcpClient client;
                try { client = await listener.AcceptTcpClientAsync(); }
                catch (Exception) { return; } // listener stopped
                using (client)
                {
                    try
                    {
                        NetworkStream stream = client.GetStream();
                        await stream.ReadAsync(new byte[4096], 0, 4096);
                        await stream.WriteAsync(reply, 0, reply.Length);
                    }
                    catch (Exception) { /* client went away; keep serving */ }
                }
            }
        }

        [UnityTest]
        public IEnumerator AutomaticProbe_ReachesAFakeAddon_WithoutDisablingActions()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                _ = Task.Run(() => ServeFakeAddon(listener));
                BlenderBridgePrefs.Port = port;

                var panel = BuildPanel();
                StringAssert.StartsWith($"Checking Blender at 127.0.0.1:{port}", StatusText(panel));
                Assert.IsFalse(Busy(panel), "An automatic probe must not disable the action buttons.");

                DateTime deadline = DateTime.UtcNow.AddSeconds(15);
                while (!StatusText(panel).StartsWith("Blender reachable") && DateTime.UtcNow < deadline) yield return null;
                Assert.AreEqual($"Blender reachable at 127.0.0.1:{port}", StatusText(panel), "The panel never reported the fake addon as reachable.");
                Assert.AreEqual(1, StatusState(panel), "The status dot must be green (reachable) for the fake addon.");
            }
            finally
            {
                listener.Stop();
            }
        }

        [UnityTest]
        public IEnumerator AutomaticProbe_ClosedPort_ReportsUnreachable()
        {
            var panel = BuildPanel();

            DateTime deadline = DateTime.UtcNow.AddSeconds(15);
            while (StatusState(panel) != -1 && DateTime.UtcNow < deadline) yield return null;
            Assert.AreEqual(-1, StatusState(panel), $"The probe never failed; status: {StatusText(panel)}");
            StringAssert.Contains("not reachable", StatusText(panel));
        }
    }
}
