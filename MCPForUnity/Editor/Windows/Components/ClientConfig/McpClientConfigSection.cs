using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using MCPForUnity.Editor.Clients;
using MCPForUnity.Editor.Constants;
using MCPForUnity.Editor.Helpers;
using MCPForUnity.Editor.Models;
using MCPForUnity.Editor.Services;
using MCPForUnity.Editor.Setup;
using UnityEditor;
using UnityEngine;

namespace MCPForUnity.Editor.Windows.Components.ClientConfig
{
    /// <summary>
    /// Controller for the Client Configuration section of the MCP for Unity editor window.
    /// Handles client selection, configuration, status display, and manual configuration details.
    /// </summary>
    public class McpClientConfigSection
    {
        /// <summary>
        /// Approximates the USS-driven status dot colors from the UI Toolkit layout.
        /// </summary>
        private enum StatusIndicatorKind
        {
            NotConfigured = 0,
            Configured = 1,
            Warning = 2,
            Error = 3
        }

        // Backing state for the IMGUI draw path (replaces the cached editor toolkit widgets)
        private readonly string[] clientNames;
        private StatusIndicatorKind statusIndicatorKind = StatusIndicatorKind.NotConfigured;
        private string clientStatusText = "Not Configured";
        private string configureButtonText = "Configure";
        private bool installSkillsVisible;
        private bool installSkillsEnabled = true;
        private string installSkillsText = "Install Skills";
        private bool claudeCliPathVisible;
        private string claudeCliPathText = string.Empty;
        private bool clientProjectDirVisible;
        private string clientProjectDirText = string.Empty;
        private bool clientDetailsOpen;
        private bool manualConfigOpen;
        private string configPathText = string.Empty;
        private string configJsonText = string.Empty;
        private string installationStepsText = string.Empty;
        private static GUIStyle wrappedLabelStyle;

        // Data
        private readonly List<IMcpClientConfigurator> configurators;
        private readonly Dictionary<IMcpClientConfigurator, DateTime> lastStatusChecks = new Dictionary<IMcpClientConfigurator, DateTime>();
        private readonly HashSet<IMcpClientConfigurator> statusRefreshInFlight = new HashSet<IMcpClientConfigurator>();
        private static readonly TimeSpan StatusRefreshInterval = TimeSpan.FromSeconds(45);
        private int selectedClientIndex = 0;
        private bool isSkillSyncInProgress;

        // Events
        /// <summary>
        /// Fired when the selected client's configured transport is detected/updated.
        /// The parameter contains the client name and its configured transport.
        /// </summary>
        public event Action<string, ConfiguredTransport> OnClientTransportDetected;

        /// <summary>
        /// Fired when a config mismatch is detected (e.g., version mismatch).
        /// The parameter contains the client name and the mismatch message (null if no mismatch).
        /// </summary>
        public event Action<string, string> OnClientConfigMismatch;

        public McpClientConfigSection()
        {
            configurators = MCPServiceLocator.Client.GetAllClients().ToList();
            clientNames = configurators.Select(c => c.DisplayName).ToArray();

            // Ensure manual config foldout starts collapsed
            manualConfigOpen = false;

            // Default the per-client setup foldout to expanded. The Configure / Unregister
            // button for the currently-selected client lives inside it, and the 9.7.0
            // default-collapsed behavior made users believe the Unregister action had been
            // removed (community report: "the Unregister button was removed in 9.7.0,
            // something is stuck on stdio and I can't switch to local"). The state still
            // persists per-user, so anyone who explicitly collapses it keeps that preference.
            clientDetailsOpen = EditorPrefs.GetBool(EditorPrefKeys.ClientDetailsFoldoutOpen, true);

            if (configurators.Count > 0)
            {
                // Restore last selected client from EditorPrefs
                string lastClientId = EditorPrefs.GetString(EditorPrefKeys.LastSelectedClientId, string.Empty);
                int restoredIndex = FindConfiguratorIndex(lastClientId);
                if (restoredIndex < 0)
                    restoredIndex = 0;

                selectedClientIndex = restoredIndex;
            }

            // Initialize the configuration display for the first selected client
            UpdateClientStatus();
            UpdateManualConfiguration();
            UpdateClaudeCliPathVisibility();
            UpdateClientProjectDirVisibility();
            UpdateInstallSkillsVisibility();
        }

        /// <summary>
        /// Draws the Client Configuration section using IMGUI.
        /// Mirrors the layout order of McpClientConfigSection.uxml.
        /// </summary>
        public void Draw()
        {
            EditorGUILayout.LabelField("Client Configuration", EditorStyles.boldLabel);

            if (GUILayout.Button("Configure All Detected Clients"))
            {
                OnConfigureAllClientsClicked();
            }

            bool detailsOpen = EditorGUILayout.Foldout(clientDetailsOpen, "Per-client setup", true);
            if (detailsOpen != clientDetailsOpen)
            {
                clientDetailsOpen = detailsOpen;
                EditorPrefs.SetBool(EditorPrefKeys.ClientDetailsFoldoutOpen, detailsOpen);
            }

            if (clientDetailsOpen)
            {
                EditorGUI.indentLevel++;

                // Client selection row
                if (clientNames != null && clientNames.Length > 0)
                {
                    int clampedIndex = Mathf.Clamp(selectedClientIndex, 0, clientNames.Length - 1);
                    int newIndex = EditorGUILayout.Popup("Client:", clampedIndex, clientNames);
                    if (newIndex != selectedClientIndex)
                    {
                        OnClientSelected(newIndex);
                    }
                }

                // Status row: indicator dot + status label + action buttons
                EditorGUILayout.BeginHorizontal();
                DrawStatusIndicator();
                EditorGUILayout.LabelField(clientStatusText, GUILayout.MinWidth(80f));
                if (GUILayout.Button(configureButtonText, GUILayout.Width(110f)))
                {
                    OnConfigureClicked();
                }
                if (installSkillsVisible)
                {
                    using (new EditorGUI.DisabledScope(!installSkillsEnabled || isSkillSyncInProgress))
                    {
                        if (GUILayout.Button(installSkillsText, GUILayout.Width(110f)))
                        {
                            OnInstallSkillsClicked();
                        }
                    }
                }
                EditorGUILayout.EndHorizontal();

                // Claude CLI path row
                if (claudeCliPathVisible)
                {
                    EditorGUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField("Claude CLI Path:", GUILayout.Width(120f));
                    EditorGUILayout.SelectableLabel(claudeCliPathText, GUILayout.MinWidth(60f));
                    if (GUILayout.Button("Browse", GUILayout.Width(60f)))
                    {
                        OnBrowseClaudeClicked();
                    }
                    EditorGUILayout.EndHorizontal();
                }

                // Client project dir row
                if (clientProjectDirVisible)
                {
                    EditorGUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField("Client Project Dir:", GUILayout.Width(120f));
                    EditorGUILayout.SelectableLabel(clientProjectDirText, GUILayout.MinWidth(40f));
                    if (GUILayout.Button("Browse", GUILayout.Width(60f)))
                    {
                        OnBrowseProjectDirClicked();
                    }
                    if (GUILayout.Button("Clear", GUILayout.Width(50f)))
                    {
                        OnClearProjectDirClicked();
                    }
                    EditorGUILayout.EndHorizontal();
                }

                // Manual configuration foldout
                manualConfigOpen = EditorGUILayout.Foldout(manualConfigOpen, "Manual Configuration", true);
                if (manualConfigOpen)
                {
                    EditorGUILayout.LabelField("Config Path:");
                    EditorGUILayout.BeginHorizontal();
                    EditorGUILayout.SelectableLabel(configPathText, GUILayout.MinWidth(60f));
                    if (GUILayout.Button("Copy", GUILayout.Width(50f)))
                    {
                        OnCopyPathClicked();
                    }
                    if (GUILayout.Button("Open", GUILayout.Width(50f)))
                    {
                        OnOpenFileClicked();
                    }
                    EditorGUILayout.EndHorizontal();

                    EditorGUILayout.LabelField("Configuration:");
                    EditorGUILayout.SelectableLabel(configJsonText, GetWrappedLabelStyle(), GUILayout.MinHeight(96f));

                    EditorGUILayout.LabelField("Installation Steps:");
                    EditorGUILayout.SelectableLabel(installationStepsText, GetWrappedLabelStyle());
                }

                EditorGUI.indentLevel--;
            }
        }

        private void DrawStatusIndicator()
        {
            Rect dotRect = GUILayoutUtility.GetRect(10f, 16f, GUILayout.Width(10f));
            Color previousColor = GUI.color;
            GUI.color = GetStatusColor(statusIndicatorKind);
            GUI.DrawTexture(dotRect, Texture2D.whiteTexture, ScaleMode.ScaleToFit);
            GUI.color = previousColor;
        }

        private static Color GetStatusColor(StatusIndicatorKind kind)
        {
            switch (kind)
            {
                case StatusIndicatorKind.Configured: return new Color(0.35f, 0.85f, 0.35f);
                case StatusIndicatorKind.Warning: return new Color(1.00f, 0.72f, 0.15f);
                case StatusIndicatorKind.Error: return Color.red;
                default: return new Color(0.55f, 0.55f, 0.55f);
            }
        }

        private static GUIStyle GetWrappedLabelStyle()
        {
            if (wrappedLabelStyle == null)
            {
                wrappedLabelStyle = new GUIStyle(EditorStyles.label)
                {
                    wordWrap = true
                };
            }

            return wrappedLabelStyle;
        }

        private void OnClientSelected(int selectedIndex)
        {
            if (selectedIndex < 0 || selectedIndex >= configurators.Count)
            {
                return;
            }

            selectedClientIndex = selectedIndex;
            // Persist the selected client so it's restored on next window open
            if (selectedClientIndex >= 0 && selectedClientIndex < configurators.Count)
            {
                EditorPrefs.SetString(EditorPrefKeys.LastSelectedClientId, configurators[selectedClientIndex].Id);
            }
            UpdateClientStatus();
            UpdateManualConfiguration();
            UpdateClaudeCliPathVisibility();
            UpdateClientProjectDirVisibility();
            UpdateInstallSkillsVisibility();
        }

        public void UpdateClientStatus()
        {
            if (selectedClientIndex < 0 || selectedClientIndex >= configurators.Count)
                return;

            var client = configurators[selectedClientIndex];
            RefreshClientStatus(client);
        }

        private string GetStatusDisplayString(McpStatus status)
        {
            switch (status)
            {
                case McpStatus.NotConfigured: return "Not Configured";
                case McpStatus.Configured: return "Configured";
                case McpStatus.Running: return "Running";
                case McpStatus.Connected: return "Connected";
                case McpStatus.IncorrectPath: return "Incorrect Path";
                case McpStatus.CommunicationError: return "Communication Error";
                case McpStatus.NoResponse: return "No Response";
                case McpStatus.UnsupportedOS: return "Unsupported OS";
                case McpStatus.MissingConfig: return "Missing MCPForUnity Config";
                case McpStatus.Error: return "Error";
                case McpStatus.VersionMismatch: return "Version Mismatch";
                default: return "Unknown";
            }
        }

        public void UpdateManualConfiguration()
        {
            if (selectedClientIndex < 0 || selectedClientIndex >= configurators.Count)
                return;

            var client = configurators[selectedClientIndex];

            string configPath = client.GetConfigPath();
            configPathText = configPath;

            string configJson = client.GetManualSnippet();
            configJsonText = configJson;

            var steps = client.GetInstallationSteps();
            if (steps != null && steps.Count > 0)
            {
                var numbered = steps.Select((s, i) => $"{i + 1}. {s}");
                installationStepsText = string.Join("\n", numbered);
            }
            else
            {
                installationStepsText = "Configuration steps not available for this client.";
            }
        }

        private void UpdateClaudeCliPathVisibility()
        {
            if (selectedClientIndex < 0 || selectedClientIndex >= configurators.Count)
                return;

            var client = configurators[selectedClientIndex];

            if (client is ClaudeCliMcpConfigurator)
            {
                claudeCliPathVisible = true;
                string claudePath = MCPServiceLocator.Paths.GetClaudeCliPath();
                if (string.IsNullOrEmpty(claudePath))
                {
                    claudeCliPathText = "Not found - click Browse to select";
                }
                else
                {
                    claudeCliPathText = claudePath;
                }
            }
            else
            {
                claudeCliPathVisible = false;
            }
        }

        private void UpdateClientProjectDirVisibility()
        {
            if (selectedClientIndex < 0 || selectedClientIndex >= configurators.Count)
                return;

            var client = configurators[selectedClientIndex];

            if (client is ClaudeCliMcpConfigurator)
            {
                clientProjectDirVisible = true;
                string projectDir = ClaudeCliMcpConfigurator.GetClientProjectDir();
                if (ClaudeCliMcpConfigurator.HasClientProjectDirOverride)
                {
                    clientProjectDirText = projectDir + "  (override)";
                }
                else
                {
                    clientProjectDirText = projectDir;
                }
            }
            else
            {
                clientProjectDirVisible = false;
            }
        }

        private void OnConfigureAllClientsClicked()
        {
            try
            {
                var summary = MCPServiceLocator.Client.ConfigureAllDetectedClients();

                string headline = summary.SkippedCount > 0
                    ? $"{summary.SuccessCount + summary.FailureCount} detected client(s) processed. ({summary.SkippedCount} not installed, skipped.)"
                    : summary.GetSummaryMessage();
                string message = headline + "\n\n";
                foreach (var msg in summary.Messages)
                {
                    message += msg + "\n";
                }

                EditorUtility.DisplayDialog("Configure Detected Clients", message, "OK");

                // The bulk path mutated state for every detected client. Clear the per-client
                // status cache so any subsequent dropdown switch (or the currently-selected
                // client refresh below) reads fresh status from disk instead of the pre-bulk
                // value — otherwise every other client in the dropdown looks like it still
                // has Missing/IncorrectPath status until 45 s elapses on the throttle.
                lastStatusChecks.Clear();

                if (selectedClientIndex >= 0 && selectedClientIndex < configurators.Count)
                {
                    UpdateClientStatus();
                    UpdateManualConfiguration();
                }
            }
            catch (Exception ex)
            {
                EditorUtility.DisplayDialog("Configuration Failed", ex.Message, "OK");
            }
        }

        private void OnConfigureClicked()
        {
            if (selectedClientIndex < 0 || selectedClientIndex >= configurators.Count)
                return;

            var client = configurators[selectedClientIndex];

            // Handle CLI configurators asynchronously
            if (client is ClaudeCliMcpConfigurator)
            {
                ConfigureClaudeCliAsync(client);
                return;
            }

            try
            {
                // Per-client toggle: Configure→register, Unregister→remove. The Configure path
                // on JsonFile / Codex configurators is always idempotent-write so the bulk
                // "Configure All" can call it unconditionally; the toggle lives here in the UI
                // handler so the manual button still does what its label says.
                if (client.Status == McpStatus.Configured)
                {
                    client.Unregister();
                }
                else
                {
                    MCPServiceLocator.Client.ConfigureClient(client);
                }
                lastStatusChecks.Remove(client);
                RefreshClientStatus(client, forceImmediate: true);
                UpdateManualConfiguration();
            }
            catch (Exception ex)
            {
                clientStatusText = "Error";
                statusIndicatorKind = StatusIndicatorKind.Error;
                McpLog.Error($"Configuration failed: {ex.Message}");
                EditorUtility.DisplayDialog("Configuration Failed", ex.Message, "OK");
            }
        }

        private void ConfigureClaudeCliAsync(IMcpClientConfigurator client)
        {
            if (statusRefreshInFlight.Contains(client))
                return;

            statusRefreshInFlight.Add(client);
            bool isCurrentlyConfigured = client.Status == McpStatus.Configured;
            ApplyStatusToUi(client, showChecking: true, customMessage: isCurrentlyConfigured ? "Unregistering..." : "Configuring...");

            // Capture ALL main-thread-only values before async task
            string projectDir = ClaudeCliMcpConfigurator.GetClientProjectDir();
            bool useHttpTransport = EditorConfigurationCache.Instance.UseHttpTransport;
            string claudePath = MCPServiceLocator.Paths.GetClaudeCliPath();
            string httpUrl = HttpEndpointUtility.GetMcpRpcUrl();
            var (uvxPath, _, packageName) = AssetPathUtility.GetUvxCommandParts();
            string fromArgs = AssetPathUtility.GetBetaServerFromArgs(quoteFromPath: true);
            string uvxDevFlags = AssetPathUtility.GetUvxDevFlags();
            string apiKey = EditorPrefs.GetString(EditorPrefKeys.ApiKey, string.Empty);

            // Compute pathPrepend on main thread
            string pathPrepend = null;
            if (Application.platform == RuntimePlatform.OSXEditor)
                pathPrepend = "/opt/homebrew/bin:/usr/local/bin:/usr/bin:/bin";
            else if (Application.platform == RuntimePlatform.LinuxEditor)
                pathPrepend = "/usr/local/bin:/usr/bin:/bin";
            try
            {
                string claudeDir = Path.GetDirectoryName(claudePath);
                if (!string.IsNullOrEmpty(claudeDir))
                    pathPrepend = string.IsNullOrEmpty(pathPrepend) ? claudeDir : $"{claudeDir}:{pathPrepend}";
            }
            catch { }

            Task.Run(() =>
            {
                try
                {
                    if (client is ClaudeCliMcpConfigurator cliConfigurator)
                    {
                        var serverTransport = HttpEndpointUtility.GetCurrentServerTransport();
                        cliConfigurator.ConfigureWithCapturedValues(
                            projectDir, claudePath, pathPrepend,
                            useHttpTransport, httpUrl,
                            uvxPath, fromArgs, packageName, uvxDevFlags,
                            apiKey, serverTransport);
                    }
                    return (success: true, error: (string)null);
                }
                catch (Exception ex)
                {
                    return (success: false, error: ex.Message);
                }
            }).ContinueWith(t =>
            {
                string errorMessage = null;
                if (t.IsFaulted && t.Exception != null)
                {
                    errorMessage = t.Exception.GetBaseException()?.Message ?? "Configuration failed";
                }
                else if (!t.Result.success)
                {
                    errorMessage = t.Result.error;
                }

                EditorApplication.delayCall += () =>
                {
                    statusRefreshInFlight.Remove(client);
                    lastStatusChecks.Remove(client);

                    if (errorMessage != null)
                    {
                        if (client is McpClientConfiguratorBase baseConfigurator)
                        {
                            baseConfigurator.Client.SetStatus(McpStatus.Error, errorMessage);
                        }
                        McpLog.Error($"Configuration failed: {errorMessage}");
                        RefreshClientStatus(client, forceImmediate: true);
                    }
                    else
                    {
                        // Registration succeeded - trust the status set by RegisterWithCapturedValues
                        // and update UI without re-verifying (which could fail due to CLI timing/scope issues)
                        lastStatusChecks[client] = DateTime.UtcNow;
                        ApplyStatusToUi(client);
                    }
                    UpdateManualConfiguration();
                };
            });
        }

        private void UpdateInstallSkillsVisibility()
        {
            installSkillsVisible = selectedClientIndex >= 0
                                   && selectedClientIndex < configurators.Count
                                   && configurators[selectedClientIndex].SupportsSkills;
        }

        private void OnInstallSkillsClicked()
        {
            if (isSkillSyncInProgress)
                return;

            if (selectedClientIndex < 0 || selectedClientIndex >= configurators.Count)
                return;

            var client = configurators[selectedClientIndex];
            if (!client.SupportsSkills)
                return;

            string installPath = client.GetSkillInstallPath();
            if (string.IsNullOrEmpty(installPath))
                return;

            string branch = AssetPathUtility.IsPreReleaseVersion() ? "beta" : "main";

            isSkillSyncInProgress = true;
            installSkillsEnabled = false;
            installSkillsText = "Syncing...";

            SkillSyncService.SyncAsync(installPath, branch, null, result =>
                {
                    isSkillSyncInProgress = false;
                    installSkillsEnabled = true;
                    installSkillsText = "Install Skills";

                    if (result.Success)
                    {
                        bool noChanges = result.Added == 0 && result.Updated == 0 && result.Deleted == 0;
                        string summary = noChanges
                            ? "Skills are already up to date."
                            : $"Added: {result.Added}, Updated: {result.Updated}, Deleted: {result.Deleted}";
                        McpLog.Info($"SkillSync complete: {summary} ({installPath})");
                        EditorUtility.DisplayDialog("Install Skills",
                            $"{summary}\n\nInstalled at: {installPath}", "OK");
                    }
                    else
                    {
                        McpLog.Error($"SkillSync failed: {result.Error}");
                        EditorUtility.DisplayDialog("Install Skills Failed", result.Error, "OK");
                    }
                });
        }

        private void OnBrowseClaudeClicked()
        {
            string suggested = RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
                ? "/opt/homebrew/bin"
                : Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string picked = EditorUtility.OpenFilePanel("Select Claude CLI", suggested, "");
            if (!string.IsNullOrEmpty(picked))
            {
                try
                {
                    MCPServiceLocator.Paths.SetClaudeCliPathOverride(picked);
                    UpdateClaudeCliPathVisibility();
                    UpdateClientStatus();
                    McpLog.Info($"Claude CLI path override set to: {picked}");
                }
                catch (Exception ex)
                {
                    EditorUtility.DisplayDialog("Invalid Path", ex.Message, "OK");
                }
            }
        }

        private void OnBrowseProjectDirClicked()
        {
            string currentDir = ClaudeCliMcpConfigurator.GetClientProjectDir();
            string picked = EditorUtility.OpenFolderPanel("Select Client Project Directory", currentDir, "");
            if (!string.IsNullOrEmpty(picked))
            {
                if (!Directory.Exists(picked))
                {
                    EditorUtility.DisplayDialog("Invalid Path", "The selected directory does not exist.", "OK");
                    return;
                }
                EditorPrefs.SetString(EditorPrefKeys.ClientProjectDirOverride, picked);
                UpdateClientProjectDirVisibility();
                UpdateClientStatus();
                McpLog.Info($"Client project directory override set to: {picked}");
            }
        }

        private void OnClearProjectDirClicked()
        {
            EditorPrefs.DeleteKey(EditorPrefKeys.ClientProjectDirOverride);
            UpdateClientProjectDirVisibility();
            UpdateClientStatus();
            McpLog.Info("Client project directory override cleared, using Unity project directory.");
        }

        private void OnCopyPathClicked()
        {
            EditorGUIUtility.systemCopyBuffer = configPathText;
            McpLog.Info("Config path copied to clipboard");
        }

        private void OnOpenFileClicked()
        {
            string path = configPathText;
            try
            {
                if (!File.Exists(path))
                {
                    EditorUtility.DisplayDialog("Open File", "The configuration file path does not exist.", "OK");
                    return;
                }

                Process.Start(new ProcessStartInfo
                {
                    FileName = path,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                McpLog.Error($"Failed to open file: {ex.Message}");
            }
        }

        private void OnCopyJsonClicked()
        {
            EditorGUIUtility.systemCopyBuffer = configJsonText;
            McpLog.Info("Configuration copied to clipboard");
        }

        public void RefreshSelectedClient(bool forceImmediate = false)
        {
            if (selectedClientIndex >= 0 && selectedClientIndex < configurators.Count)
            {
                var client = configurators[selectedClientIndex];
                // Force immediate for non-Claude CLI, or when explicitly requested
                bool shouldForceImmediate = forceImmediate || !(client is ClaudeCliMcpConfigurator);
                RefreshClientStatus(client, shouldForceImmediate);
                UpdateManualConfiguration();
                UpdateClaudeCliPathVisibility();
            }
        }

        private void RefreshClientStatus(IMcpClientConfigurator client, bool forceImmediate = false)
        {
            if (client is ClaudeCliMcpConfigurator)
            {
                RefreshClaudeCliStatus(client, forceImmediate);
                return;
            }

            if (forceImmediate || ShouldRefreshClient(client))
            {
                MCPServiceLocator.Client.CheckClientStatus(client);
                lastStatusChecks[client] = DateTime.UtcNow;
            }

            ApplyStatusToUi(client);
        }

        private void RefreshClaudeCliStatus(IMcpClientConfigurator client, bool forceImmediate)
        {
            bool hasStatus = lastStatusChecks.ContainsKey(client);
            bool needsRefresh = !hasStatus || ShouldRefreshClient(client);

            if (!hasStatus)
            {
                ApplyStatusToUi(client, showChecking: true);
            }
            else
            {
                ApplyStatusToUi(client);
            }

            if ((forceImmediate || needsRefresh) && !statusRefreshInFlight.Contains(client))
            {
                statusRefreshInFlight.Add(client);
                ApplyStatusToUi(client, showChecking: true);

                // Capture main-thread-only values before async task
                string projectDir = ClaudeCliMcpConfigurator.GetClientProjectDir();
                bool useHttpTransport = EditorConfigurationCache.Instance.UseHttpTransport;
                string claudePath = MCPServiceLocator.Paths.GetClaudeCliPath();
                RuntimePlatform platform = Application.platform;
                bool isRemoteScope = HttpEndpointUtility.IsRemoteScope();
                // Get expected package source based on installed package version and overrides.
                string expectedPackageSource = GetExpectedPackageSourceForCurrentPackage();
                bool hasProjectDirOverride = ClaudeCliMcpConfigurator.HasClientProjectDirOverride;

                Task.Run(() =>
                {
                    // Defensive: RefreshClientStatus routes Claude CLI clients here, but avoid hard-cast
                    // so accidental future call sites can't crash the UI.
                    if (client is ClaudeCliMcpConfigurator claudeConfigurator)
                    {
                        // Use thread-safe version with captured main-thread values
                        claudeConfigurator.CheckStatusWithProjectDir(projectDir, useHttpTransport, claudePath, platform, isRemoteScope, expectedPackageSource, attemptAutoRewrite: false, hasProjectDirOverride: hasProjectDirOverride);
                    }
                }).ContinueWith(t =>
                {
                    bool faulted = false;
                    string errorMessage = null;
                    if (t.IsFaulted && t.Exception != null)
                    {
                        var baseException = t.Exception.GetBaseException();
                        errorMessage = baseException?.Message ?? "Status check failed";
                        McpLog.Error($"Failed to refresh Claude CLI status: {errorMessage}");
                        faulted = true;
                    }

                    EditorApplication.delayCall += () =>
                    {
                        statusRefreshInFlight.Remove(client);
                        lastStatusChecks[client] = DateTime.UtcNow;
                        if (faulted)
                        {
                            if (client is McpClientConfiguratorBase baseConfigurator)
                            {
                                baseConfigurator.Client.SetStatus(McpStatus.Error, errorMessage ?? "Status check failed");
                            }
                        }
                        ApplyStatusToUi(client);
                    };
                });
            }
        }

        private bool ShouldRefreshClient(IMcpClientConfigurator client)
        {
            if (!lastStatusChecks.TryGetValue(client, out var last))
            {
                return true;
            }

            return (DateTime.UtcNow - last) > StatusRefreshInterval;
        }

        private void ApplyStatusToUi(IMcpClientConfigurator client, bool showChecking = false, string customMessage = null)
        {
            if (selectedClientIndex < 0 || selectedClientIndex >= configurators.Count)
                return;

            if (!ReferenceEquals(configurators[selectedClientIndex], client))
                return;

            if (showChecking)
            {
                clientStatusText = customMessage ?? "Checking...";
                statusIndicatorKind = StatusIndicatorKind.Warning;
                configureButtonText = client.GetConfigureActionLabel();
                return;
            }

            // Check for transport mismatch (3-way: Stdio, Http, HttpRemote).
            // Skip when a project dir override is active — the registered transport
            // in the overridden project may legitimately differ from the local server.
            bool hasTransportMismatch = false;
            if (client.ConfiguredTransport != ConfiguredTransport.Unknown
                && !ClaudeCliMcpConfigurator.HasClientProjectDirOverride)
            {
                ConfiguredTransport serverTransport = HttpEndpointUtility.GetCurrentServerTransport();
                hasTransportMismatch = client.ConfiguredTransport != serverTransport;
            }

            // If configured but with transport mismatch, show warning state
            if (hasTransportMismatch && (client.Status == McpStatus.Configured || client.Status == McpStatus.Running || client.Status == McpStatus.Connected))
            {
                clientStatusText = "Transport Mismatch";
                statusIndicatorKind = StatusIndicatorKind.Warning;
            }
            else
            {
                clientStatusText = GetStatusDisplayString(client.Status);

                switch (client.Status)
                {
                    case McpStatus.Configured:
                    case McpStatus.Running:
                    case McpStatus.Connected:
                        statusIndicatorKind = StatusIndicatorKind.Configured;
                        break;
                    case McpStatus.IncorrectPath:
                    case McpStatus.CommunicationError:
                    case McpStatus.NoResponse:
                    case McpStatus.Error:
                    case McpStatus.VersionMismatch:
                        statusIndicatorKind = StatusIndicatorKind.Warning;
                        break;
                    default:
                        statusIndicatorKind = StatusIndicatorKind.NotConfigured;
                        break;
                }
            }

            configureButtonText = client.GetConfigureActionLabel();

            // Notify listeners about the client's configured transport
            OnClientTransportDetected?.Invoke(client.DisplayName, client.ConfiguredTransport);

            // Notify listeners about version mismatch if applicable
            if (client.Status == McpStatus.VersionMismatch && client is McpClientConfiguratorBase baseConfigurator)
            {
                // Get the mismatch reason from the configStatus field
                string mismatchReason = baseConfigurator.Client.configStatus;
                OnClientConfigMismatch?.Invoke(client.DisplayName, mismatchReason);
            }
            else
            {
                // Clear any previous mismatch warning
                OnClientConfigMismatch?.Invoke(client.DisplayName, null);
            }
        }

        /// <summary>
        /// Gets the expected package source for validation based on installed package version.
        /// Uses the same logic as registration to ensure validation matches what was registered.
        /// MUST be called from the main thread due to EditorPrefs access.
        /// </summary>
        private static string GetExpectedPackageSourceForCurrentPackage()
        {
            return AssetPathUtility.GetMcpServerPackageSource();
        }

        private int FindConfiguratorIndex(string persistedClientValue)
        {
            if (string.IsNullOrWhiteSpace(persistedClientValue))
                return -1;

            // Primary match: stored stable ID.
            for (int i = 0; i < configurators.Count; i++)
            {
                if (string.Equals(configurators[i].Id, persistedClientValue, StringComparison.OrdinalIgnoreCase))
                    return i;
            }

            // Compatibility match for older persisted values (e.g., display names with spaces).
            string normalized = NormalizeClientToken(persistedClientValue);
            if (string.IsNullOrEmpty(normalized))
                return -1;

            for (int i = 0; i < configurators.Count; i++)
            {
                if (NormalizeClientToken(configurators[i].Id) == normalized ||
                    NormalizeClientToken(configurators[i].DisplayName) == normalized)
                {
                    return i;
                }
            }

            return -1;
        }

        private static string NormalizeClientToken(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            return new string(value.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        }
    }
}
