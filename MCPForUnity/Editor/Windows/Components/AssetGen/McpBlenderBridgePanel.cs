using System;
using System.IO;
using System.Threading.Tasks;
using MCPForUnity.Editor.Helpers;
using MCPForUnity.Editor.Services.Blender;
using MCPForUnity.Editor.Tools.Blender;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace MCPForUnity.Editor.Windows.Components.AssetGen
{
    /// <summary>
    /// Controller for the "Blender Bridge" block of the Asset Gen tab: addon socket host/port,
    /// the user's blender-mcp checkout, Blender's addons folder, plus Test Connection / Sync Addon /
    /// Check Updates / Import Selection buttons that call <see cref="BlenderBridgeTool"/>.
    /// Unlike the provider rows, nothing here is a paid API call — it is local file and socket I/O,
    /// awaited off the editor thread so the window never freezes while Blender works.
    ///
    /// IMGUI implementation: the owning section calls <see cref="Draw"/> from its OnGUI. All state
    /// lives in plain fields; text edits commit when their control loses keyboard focus, matching
    /// the original FocusOutEvent callbacks.
    /// </summary>
    public class McpBlenderBridgePanel
    {
        private const string HostControl = "blender-host";
        private const string PortControl = "blender-port";
        private const string ForkControl = "blender-fork";
        private const string AddonsControl = "blender-addons";

        private static readonly GUIStyle BoldLabelStyle = new GUIStyle(EditorStyles.label) { fontStyle = FontStyle.Bold };
        private static readonly GUIStyle OkWrapStyle = MakeWrapStyle(new Color(0.4f, 0.8f, 0.4f));
        private static readonly GUIStyle DimWrapStyle = MakeWrapStyle(new Color(0.7f, 0.7f, 0.7f));
        private static readonly GUIStyle ErrorWrapStyle = MakeWrapStyle(new Color(0.85f, 0.2f, 0.2f));
        private static GUIStyle placeholderStyle;

        // Edit buffers for the text fields; committed to prefs on focus loss.
        private string hostText = string.Empty;
        private string portText = string.Empty;
        private string forkText = string.Empty;
        private string addonsText = string.Empty;

        private string addonsResolvedText = string.Empty;
        private bool addonsResolvedOk;

        private string statusText = string.Empty;
        private string statusTooltip;
        // 1 = reachable, -1 = failed, 0 = unknown; colours the status dot.
        private int statusState;

        private string actionStatusText = string.Empty;
        private bool actionStatusIsError;

        private string lastFocusedControl = string.Empty;

        private bool busy;
        private bool probing;
        private bool probeQueued;

        public McpBlenderBridgePanel()
        {
            SyncFromPrefs();
            ProbeConnection();
        }

        /// <summary>Draws the Blender Bridge block in McpAssetGenSection.uxml order.</summary>
        public void Draw()
        {
            CommitFocusLostEdits();

            EditorGUILayout.BeginVertical(GUI.skin.box);

            // Header: title, connection status dot, status label.
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Blender Bridge", BoldLabelStyle, GUILayout.ExpandWidth(false));
            DrawStatusDot();
            EditorGUILayout.LabelField(new GUIContent(statusText, statusTooltip), EditorStyles.wordWrappedMiniLabel, GUILayout.MinWidth(0));
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.LabelField(
                "Talks directly to the BlenderMCP addon socket in a running Blender (N panel → Connect to MCP server). " +
                "Powers the blender_bridge tool and Window → MCP for Unity → Blender Bridge. No API keys involved.",
                EditorStyles.wordWrappedMiniLabel);

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.PrefixLabel("Addon Socket:");
            GUI.SetNextControlName(HostControl);
            hostText = EditorGUILayout.TextField(
                new GUIContent(hostText, "Host the BlenderMCP addon socket listens on (Blender's N panel > BlenderMCP)."),
                GUILayout.Width(140f));
            GUI.SetNextControlName(PortControl);
            portText = EditorGUILayout.TextField(
                new GUIContent(portText, $"Addon socket port. Default {BlenderBridgePrefs.DefaultPort}."),
                GUILayout.Width(70f));
            using (new EditorGUI.DisabledScope(busy))
            {
                if (GUILayout.Button(new GUIContent("Test Connection",
                    "Check the addon socket again now (the panel also checks it when it opens). " +
                    "Blender must be running with the addon connected."), EditorStyles.miniButton))
                {
                    ProbeConnection();
                }
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.LabelField("blender-mcp Checkout (optional):", BoldLabelStyle);
            EditorGUILayout.LabelField(
                "Only for blender-mcp addon developers: a local checkout enables Sync Addon and Check Updates. " +
                "Import and screenshot work without it.",
                EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.BeginHorizontal();
            forkText = DrawTextFieldWithPlaceholder(forkText, "/path/to/blender-mcp (contains addon.py)", ForkControl,
                "Folder of your blender-mcp checkout (the one containing addon.py). Enables Sync Addon and Check Updates.",
                GUILayout.ExpandWidth(true));
            using (new EditorGUI.DisabledScope(busy))
            {
                if (GUILayout.Button("Select", EditorStyles.miniButton))
                {
                    OnSelectFork();
                }
                if (GUILayout.Button("Clear", EditorStyles.miniButton))
                {
                    SetForkPath(string.Empty);
                }
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.LabelField("Blender Addons Dir:", BoldLabelStyle);
            EditorGUILayout.BeginHorizontal();
            addonsText = DrawTextFieldWithPlaceholder(addonsText, "auto-detect newest Blender user addons folder", AddonsControl,
                "Blender's user addons folder. Leave empty to auto-detect the newest Blender version's scripts/addons.",
                GUILayout.ExpandWidth(true));
            using (new EditorGUI.DisabledScope(busy))
            {
                if (GUILayout.Button("Select", EditorStyles.miniButton))
                {
                    OnSelectAddonsDir();
                }
                if (GUILayout.Button("Clear", EditorStyles.miniButton))
                {
                    SetAddonsDir(string.Empty);
                }
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.LabelField(new GUIContent(addonsResolvedText),
                addonsResolvedOk ? OkWrapStyle : DimWrapStyle);

            EditorGUILayout.BeginHorizontal();
            bool configured = BlenderBridgePrefs.IsForkConfigured;
            using (new EditorGUI.DisabledScope(busy || !configured))
            {
                if (GUILayout.Button(new GUIContent("Sync Addon",
                    "Copy the checkout's addon.py into Blender's addons folder (backs up the old file)."), EditorStyles.miniButton))
                {
                    SyncAddon();
                }
                if (GUILayout.Button(new GUIContent("Check Updates",
                    "git fetch the checkout and report how far behind its remotes it is."), EditorStyles.miniButton))
                {
                    CheckUpdates();
                }
                if (GUILayout.Button(new GUIContent("Import Blender Selection (GLB)",
                    "Export the objects selected in Blender as GLB, import them under Assets/ and place them in the open scene. " +
                    "Needs Blender running with the BlenderMCP addon connected; with nothing selected in Blender it reports an error."),
                    EditorStyles.miniButton))
                {
                    ImportSelection();
                }
            }
            EditorGUILayout.EndHorizontal();

            if (!string.IsNullOrEmpty(actionStatusText))
            {
                EditorGUILayout.LabelField(actionStatusText,
                    actionStatusIsError ? ErrorWrapStyle : EditorStyles.wordWrappedMiniLabel);
            }

            EditorGUILayout.EndVertical();
        }

        /// <summary>Re-reads prefs and the on-disk addon state, then re-checks the socket in the background.</summary>
        public void Refresh()
        {
            SyncFromPrefs();
            ProbeConnection();
        }

        /// <summary>Reflects prefs into the edit buffers and recomputes the addon status line.</summary>
        private void SyncFromPrefs()
        {
            hostText = BlenderBridgePrefs.Host;
            portText = BlenderBridgePrefs.Port.ToString();
            forkText = BlenderBridgePrefs.ForkPath;
            addonsText = BlenderBridgePrefs.AddonsDirOverride;

            UpdateAddonStatus();
        }

        /// <summary>
        /// Commits a text edit the moment its control loses keyboard focus — the IMGUI equivalent
        /// of the original FocusOutEvent callbacks.
        /// </summary>
        private void CommitFocusLostEdits()
        {
            string focused = GUI.GetNameOfFocusedControl();
            if (focused == lastFocusedControl) return;

            if (lastFocusedControl == HostControl)
            {
                CommitHost();
            }
            else if (lastFocusedControl == PortControl)
            {
                CommitPort();
            }
            else if (lastFocusedControl == ForkControl)
            {
                SetForkPath(forkText);
            }
            else if (lastFocusedControl == AddonsControl)
            {
                SetAddonsDir(addonsText);
            }
            lastFocusedControl = focused;
        }

        private void CommitHost()
        {
            string before = BlenderBridgePrefs.Host;
            BlenderBridgePrefs.Host = hostText;
            hostText = BlenderBridgePrefs.Host;
            if (BlenderBridgePrefs.Host != before) ProbeConnection();
        }

        // A text field rather than an int field: the addon reads it the same way and this keeps a
        // single code path, as the original UXML TextField did.
        private void CommitPort()
        {
            int before = BlenderBridgePrefs.Port;
            if (int.TryParse((portText ?? string.Empty).Trim(), out int port)) BlenderBridgePrefs.Port = port;
            portText = BlenderBridgePrefs.Port.ToString();
            if (BlenderBridgePrefs.Port != before) ProbeConnection();
        }

        /// <summary>Validates and persists the checkout folder; rejects folders without addon.py.</summary>
        private void SetForkPath(string path)
        {
            string normalized = BlenderBridgePrefs.NormalizePath(path);
            if (!string.IsNullOrEmpty(normalized) && !BlenderBridgePrefs.IsValidForkPath(normalized))
            {
                EditorUtility.DisplayDialog("Invalid blender-mcp checkout",
                    $"No {BlenderBridgePrefs.AddonFileName} found in:\n{normalized}\n\nPick the folder that contains addon.py.", "OK");
                forkText = BlenderBridgePrefs.ForkPath;
                return;
            }
            BlenderBridgePrefs.ForkPath = normalized;
            SyncFromPrefs();
        }

        /// <summary>Opens a folder picker for the checkout.</summary>
        private void OnSelectFork()
        {
            string picked = EditorUtility.OpenFolderPanel("Select blender-mcp checkout (folder containing addon.py)",
                BlenderBridgePrefs.ForkPath, "");
            if (!string.IsNullOrEmpty(picked)) SetForkPath(picked);
        }

        /// <summary>Validates and persists the addons folder override.</summary>
        private void SetAddonsDir(string path)
        {
            string normalized = BlenderBridgePrefs.NormalizePath(path);
            if (!string.IsNullOrEmpty(normalized) && !Directory.Exists(normalized))
            {
                EditorUtility.DisplayDialog("Folder not found", $"Blender addons folder does not exist:\n{normalized}", "OK");
                addonsText = BlenderBridgePrefs.AddonsDirOverride;
                return;
            }
            BlenderBridgePrefs.AddonsDirOverride = normalized;
            SyncFromPrefs();
        }

        /// <summary>Opens a folder picker for the addons folder.</summary>
        private void OnSelectAddonsDir()
        {
            string picked = EditorUtility.OpenFolderPanel("Select Blender user addons folder (…/scripts/addons)",
                BlenderBridgePrefs.ResolveAddonsDir() ?? "", "");
            if (!string.IsNullOrEmpty(picked)) SetAddonsDir(picked);
        }

        /// <summary>
        /// Shows the resolved addons folder, whether it exists, whether the BlenderMCP addon is installed
        /// there and, with a checkout set, whether it matches the checkout. Green only when all hold.
        /// </summary>
        private void UpdateAddonStatus()
        {
            string dir = BlenderBridgePrefs.ResolveAddonsDir();
            bool configured = BlenderBridgePrefs.IsForkConfigured;
            string notInstalled = configured ? "BlenderMCP addon: not installed (Sync Addon)" : "BlenderMCP addon: not installed";
            string text;
            bool ok = false;

            if (string.IsNullOrEmpty(dir)) text = $"Addons folder: not found · {notInstalled}";
            else if (!Directory.Exists(dir)) text = $"Addons folder: {dir} (not created yet) · {notInstalled}";
            else if (!File.Exists(BlenderBridgePrefs.InstalledAddonPath)) text = $"Addons folder: {dir} · {notInstalled}";
            else
            {
                text = $"Addons folder: {dir} · BlenderMCP addon: installed";
                ok = true;
                if (configured)
                {
                    try
                    {
                        if (BlenderBridgeTool.FileMd5(BlenderBridgePrefs.ForkAddonPath) == BlenderBridgeTool.FileMd5(BlenderBridgePrefs.InstalledAddonPath))
                            text += " · in sync with checkout ✓";
                        else { text += " · differs from checkout (Sync Addon)"; ok = false; }
                    }
                    catch (Exception e)
                    {
                        text += $" · could not compare addon files: {e.Message}";
                        ok = false;
                    }
                }
            }

            addonsResolvedText = text;
            addonsResolvedOk = ok;
        }

        private async void SyncAddon()
        {
            await RunActionAsync(new JObject { ["action"] = "sync_addon" });
            UpdateAddonStatus();
        }

        private async void CheckUpdates()
        {
            await RunActionAsync(new JObject { ["action"] = "check_updates" });
        }

        private async void ImportSelection()
        {
            await RunActionAsync(new JObject { ["action"] = "import_model", ["selection_only"] = true, ["format"] = "glb" });
        }

        /// <summary>
        /// Probes the addon socket off the editor thread and shows the result on the status dot. Runs when
        /// the panel opens, on Refresh, after a host/port change and from Test Connection. Only one probe
        /// runs at a time; a request during a probe re-runs it afterwards, so the shown result always
        /// belongs to the current endpoint. It never disables the action buttons.
        /// </summary>
        private async void ProbeConnection()
        {
            if (probing)
            {
                probeQueued = true;
                return;
            }

            probing = true;
            try
            {
                do
                {
                    probeQueued = false;
                    BlenderEndpoint endpoint = BlenderBridgePrefs.Endpoint;
                    SetConnectionStatus(null, $"Checking Blender at {endpoint}…");
                    var (ok, error) = await BlenderSocketClient.ProbeAsync(endpoint);
                    if (probeQueued) continue;
                    if (ok) SetConnectionStatus(true, $"Blender reachable at {endpoint}");
                    // The actionable socket error leads; detection can miss portable installs, so it is only a hint.
                    else SetConnectionStatus(false, BlenderDetection.IsInstalled() ? error : error + " (Blender app not detected on this machine.)");
                } while (probeQueued);
            }
            finally
            {
                probing = false;
            }
        }

        /// <summary>Colours the status dot: green for reachable, red for failed, amber for unknown.</summary>
        private void SetConnectionStatus(bool? ok, string text)
        {
            statusText = Truncate(text, 240);
            statusTooltip = text;
            statusState = ok == true ? 1 : ok == false ? -1 : 0;
        }

        private void DrawStatusDot()
        {
            Rect rect = GUILayoutUtility.GetRect(12f, 16f, GUILayout.Width(12f));
            Color color = statusState == 1 ? new Color(0.4f, 0.8f, 0.4f)
                : statusState == -1 ? new Color(0.85f, 0.2f, 0.2f)
                : new Color(0.9f, 0.7f, 0.2f);
            EditorGUI.DrawRect(new Rect(rect.x + 1f, rect.y + (rect.height - 8f) / 2f, 8f, 8f), color);
        }

        /// <summary>Runs one bridge action without blocking the editor and reports its message.</summary>
        private async Task<JObject> RunActionAsync(JObject parameters)
        {
            if (busy) return null;
            SetBusy(true);
            SetActionStatus($"Running {parameters["action"]}…", false);
            JObject json;
            try
            {
                json = JObject.FromObject(await BlenderBridgeTool.HandleCommand(parameters));
            }
            catch (Exception e)
            {
                json = new JObject { ["success"] = false, ["error"] = e.Message };
            }
            finally
            {
                SetBusy(false);
            }

            bool ok = json.Value<bool?>("success") ?? false;
            string message = ok ? json["message"]?.ToString() : json["error"]?.ToString();
            SetActionStatus(message ?? (ok ? "done" : "failed"), !ok);

            string details = json.ToString(Formatting.Indented);
            if (ok) McpLog.Info($"[Blender Bridge] {parameters["action"]}: {message}\n{details}");
            else McpLog.Warn($"[Blender Bridge] {parameters["action"]} failed: {message}\n{details}");
            return json;
        }

        /// <summary>Marks a bridge call as in flight; the action buttons disable while busy.</summary>
        private void SetBusy(bool value)
        {
            busy = value;
        }

        /// <summary>Writes the last action's outcome under the buttons, red on error.</summary>
        private void SetActionStatus(string text, bool isError)
        {
            actionStatusText = Truncate(text, 240);
            actionStatusIsError = isError;
        }

        /// <summary>Caps a message for the status labels.</summary>
        private static string Truncate(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            return s.Length <= max ? s : s.Substring(0, max) + "…";
        }

        private string DrawTextFieldWithPlaceholder(string value, string placeholder, string controlName, string tooltip,
            params GUILayoutOption[] options)
        {
            GUI.SetNextControlName(controlName);
            string result = EditorGUILayout.TextField(new GUIContent(value, tooltip), options);
            DrawPlaceholder(placeholder, controlName, value);
            return result;
        }

        private static void DrawPlaceholder(string placeholder, string controlName, string value)
        {
            if (!string.IsNullOrEmpty(value) || GUI.GetNameOfFocusedControl() == controlName) return;
            if (placeholderStyle == null)
            {
                placeholderStyle = new GUIStyle(EditorStyles.miniLabel)
                {
                    fontStyle = FontStyle.Italic,
                    normal = { textColor = new Color(0.5f, 0.5f, 0.5f) }
                };
            }
            Rect rect = GUILayoutUtility.GetLastRect();
            GUI.Label(new Rect(rect.x + 3f, rect.y + 2f, rect.width - 6f, rect.height - 4f), placeholder, placeholderStyle);
        }

        private static GUIStyle MakeWrapStyle(Color color)
        {
            return new GUIStyle(EditorStyles.wordWrappedMiniLabel)
            {
                normal = { textColor = color }
            };
        }
    }
}
