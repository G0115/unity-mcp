using System;
using System.Threading.Tasks;
using MCPForUnity.Editor.Clients;
using MCPForUnity.Editor.Constants;
using MCPForUnity.Editor.Helpers;
using MCPForUnity.Editor.Models;
using MCPForUnity.Editor.Services;
using MCPForUnity.Editor.Services.Transport;
using UnityEditor;
using UnityEngine;

namespace MCPForUnity.Editor.Windows.Components.Connection
{
    /// <summary>
    /// Controller for the Connection section of the MCP for Unity editor window.
    /// Handles transport protocol, HTTP/stdio configuration, connection status, and health checks.
    /// IMGUI implementation: the main window calls <see cref="Draw"/> from OnGUI each frame.
    /// </summary>
    public class McpConnectionSection
    {
        // Transport protocol enum
        private enum TransportProtocol
        {
            HTTPLocal,
            HTTPRemote,
            Stdio
        }

        // Approximation of the UXML status-dot classes (connected/disconnected/neutral)
        private enum StatusDotState
        {
            Neutral,
            Connected,
            Disconnected
        }

        private static readonly string[] TransportOptions = { "HTTP Local", "HTTP Remote", "Stdio" };

        // Control names used to persist text fields on focus-out / Enter (IMGUI equivalent
        // of the FocusOutEvent/KeyDownEvent callbacks used by the previous UITK implementation).
        private const string HttpUrlControlName = "McpConnection_HttpUrl";
        private const string UnityPortControlName = "McpConnection_UnityPort";
        private const string ApiKeyControlName = "McpConnection_ApiKey";

        // Field-backed UI state (replaces the cached UI Toolkit element tree)
        private TransportProtocol transportSelection = TransportProtocol.HTTPLocal;
        private bool transportMismatchVisible;
        private string transportMismatchText = string.Empty;
        private bool versionMismatchVisible;
        private string versionMismatchText = string.Empty;
        private string cachedLoginUrl;
        private string httpUrlField = string.Empty;
        private string apiKeyField = string.Empty;
        private string startHttpServerButtonText = "Start Server";
        private bool startHttpServerButtonEnabled;
        private string startHttpServerButtonTooltip = string.Empty;
        private string unityPortField = string.Empty;
        private bool unityPortEnabled = true;
        private string connectionStatusLabelText = "Disconnected";
        private string connectionToggleButtonText = "Start Session";
        private bool connectionToggleButtonEnabled;
        private string connectionToggleButtonTooltip = string.Empty;
        private bool showConnectionToggle = true;
        private StatusDotState statusDotState = StatusDotState.Disconnected;
        private bool showHttpUrlRow;
        private bool showApiKeyRow;
        private bool showHttpServerControlRow;
        private bool showUnitySocketPortRow;
        private bool showManualCommandFoldout;
        private bool manualCommandFoldoutOpen;
        private bool showHttpServerCommandSection;
        private string httpServerCommandField = string.Empty;
        private bool copyHttpServerCommandEnabled;
        private string httpServerCommandHint = string.Empty;
        private bool getApiKeyButtonEnabled = true;
        private bool httpUrlWasFocused;
        private bool unityPortWasFocused;
        private bool apiKeyWasFocused;

        private bool connectionToggleInProgress;
        private bool httpServerToggleInProgress;
        private Task verificationTask;
        private string lastHealthStatus;
        private int consecutiveVerifyFailures;
        private double lastLocalServerRunningPollTime;
        private bool lastLocalServerRunning;
        private int consecutiveServerDownPolls;
        private bool sessionWasRunning;

        // Reference to Advanced section for health status updates
        private Action<bool, string> onHealthStatusUpdate;

        // Events
        public event Action OnManualConfigUpdateRequested;
        public event Action OnTransportChanged;

        public void SetHealthStatusUpdateCallback(Action<bool, string> callback)
        {
            onHealthStatusUpdate = callback;
        }

        public McpConnectionSection()
        {
            InitializeState();
        }

        private void InitializeState()
        {
            // Ensure manual command foldout starts collapsed
            manualCommandFoldoutOpen = false;

            bool useHttpTransport = EditorConfigurationCache.Instance.UseHttpTransport;
            if (!useHttpTransport)
            {
                transportSelection = TransportProtocol.Stdio;
            }
            else
            {
                // Back-compat: if scope pref isn't set yet, infer from current URL.
                string scope = EditorPrefs.GetString(EditorPrefKeys.HttpTransportScope, string.Empty);
                if (string.IsNullOrEmpty(scope))
                {
                    scope = MCPServiceLocator.Server.IsLocalUrl() ? "local" : "remote";
                    try
                    {
                        EditorPrefs.SetString(EditorPrefKeys.HttpTransportScope, scope);
                    }
                    catch
                    {
                        McpLog.Debug("Failed to set HttpTransportScope pref.");
                    }
                }

                transportSelection = scope == "remote" ? TransportProtocol.HTTPRemote : TransportProtocol.HTTPLocal;
            }

            httpUrlField = HttpEndpointUtility.GetBaseUrl();

            // Initialize API key field
            apiKeyField = EditorPrefs.GetString(EditorPrefKeys.ApiKey, string.Empty);

            int unityPort = EditorPrefs.GetInt(EditorPrefKeys.UnitySocketPort, 0);
            if (unityPort == 0)
            {
                unityPort = MCPServiceLocator.Bridge.CurrentPort;
            }
            unityPortField = unityPort.ToString();

            UpdateHttpFieldVisibility();
            RefreshHttpUi();
            UpdateConnectionStatus();
        }

        /// <summary>
        /// Draws the whole Connection section. Called by the main window from OnGUI every frame.
        /// Layout mirrors McpConnectionSection.uxml top-to-bottom.
        /// </summary>
        public void Draw()
        {
            EditorGUILayout.LabelField("Server", EditorStyles.boldLabel);

            DrawTransportRow();
            DrawWarningBanners();
            DrawHttpUrlRow();
            DrawApiKeyRow();
            DrawHttpServerControlRow();
            DrawUnityPortRow();
            DrawStatusRow();
            DrawManualCommandFoldout();
        }

        private void DrawTransportRow()
        {
            int selectedIndex = (int)transportSelection;
            int newSelectedIndex = EditorGUILayout.Popup("Transport:", selectedIndex, TransportOptions);
            if (newSelectedIndex != selectedIndex)
            {
                TransportProtocol previous = transportSelection;
                transportSelection = (TransportProtocol)newSelectedIndex;
                OnTransportSelectionChanged(previous, transportSelection);
            }
        }

        private void DrawWarningBanners()
        {
            if (transportMismatchVisible && !string.IsNullOrEmpty(transportMismatchText))
            {
                EditorGUILayout.HelpBox(transportMismatchText, MessageType.Warning);
            }

            if (versionMismatchVisible && !string.IsNullOrEmpty(versionMismatchText))
            {
                EditorGUILayout.HelpBox(versionMismatchText, MessageType.Warning);
            }
        }

        private void DrawHttpUrlRow()
        {
            if (!showHttpUrlRow)
            {
                return;
            }

            GUI.SetNextControlName(HttpUrlControlName);
            string newUrl = EditorGUILayout.TextField(
                new GUIContent("HTTP URL:", "HTTP endpoint URL for the MCP server. Use localhost for local servers."),
                httpUrlField);
            if (newUrl != httpUrlField)
            {
                httpUrlField = newUrl;
            }

            bool focused = GUI.GetNameOfFocusedControl() == HttpUrlControlName;
            bool enterPressed = focused
                && Event.current.type == EventType.KeyDown
                && (Event.current.keyCode == KeyCode.Return || Event.current.keyCode == KeyCode.KeypadEnter);
            if ((httpUrlWasFocused && !focused) || enterPressed)
            {
                PersistHttpUrlFromField();
                if (enterPressed)
                {
                    Event.current.Use();
                }
            }
            httpUrlWasFocused = focused;
        }

        private void DrawApiKeyRow()
        {
            if (!showApiKeyRow)
            {
                return;
            }

            GUI.SetNextControlName(ApiKeyControlName);
            string newKey = EditorGUILayout.PasswordField(
                new GUIContent("API Key:", "API key for remote-hosted MCP server authentication"),
                apiKeyField);
            if (newKey != apiKeyField)
            {
                apiKeyField = newKey;
            }

            bool focused = GUI.GetNameOfFocusedControl() == ApiKeyControlName;
            bool enterPressed = focused
                && Event.current.type == EventType.KeyDown
                && (Event.current.keyCode == KeyCode.Return || Event.current.keyCode == KeyCode.KeypadEnter);
            if ((apiKeyWasFocused && !focused) || enterPressed)
            {
                PersistApiKeyFromField();
                if (enterPressed)
                {
                    Event.current.Use();
                }
            }
            apiKeyWasFocused = focused;

            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            using (new EditorGUI.DisabledScope(!getApiKeyButtonEnabled))
            {
                if (GUILayout.Button("Get API Key"))
                {
                    OnGetApiKeyClicked();
                }
            }
            if (GUILayout.Button("Clear"))
            {
                OnClearApiKeyClicked();
            }
            EditorGUILayout.EndHorizontal();
        }

        private void DrawHttpServerControlRow()
        {
            if (!showHttpServerControlRow)
            {
                return;
            }

            EditorGUILayout.BeginHorizontal();
            GUILayout.Label("Local Server:", GUILayout.Width(EditorGUIUtility.labelWidth));
            using (new EditorGUI.DisabledScope(!startHttpServerButtonEnabled))
            {
                if (GUILayout.Button(new GUIContent(startHttpServerButtonText, startHttpServerButtonTooltip)))
                {
                    OnHttpServerToggleClicked();
                }
            }
            EditorGUILayout.EndHorizontal();
        }

        private void DrawUnityPortRow()
        {
            if (!showUnitySocketPortRow)
            {
                return;
            }

            GUI.SetNextControlName(UnityPortControlName);
            using (new EditorGUI.DisabledScope(!unityPortEnabled))
            {
                string newPort = EditorGUILayout.TextField(
                    new GUIContent("Unity Socket Port:", "Port for Unity's internal MCP bridge socket. Used for stdio transport."),
                    unityPortField);
                if (newPort != unityPortField)
                {
                    unityPortField = newPort;
                }
            }

            bool focused = GUI.GetNameOfFocusedControl() == UnityPortControlName;
            bool enterPressed = focused
                && Event.current.type == EventType.KeyDown
                && (Event.current.keyCode == KeyCode.Return || Event.current.keyCode == KeyCode.KeypadEnter);
            if ((unityPortWasFocused && !focused) || enterPressed)
            {
                PersistUnityPortFromField();
                if (enterPressed)
                {
                    Event.current.Use();
                }
            }
            unityPortWasFocused = focused;
        }

        private void DrawStatusRow()
        {
            EditorGUILayout.BeginHorizontal();

            // Status indicator dot (approximation of the "status-dot" element's color classes)
            Rect rowRect = GUILayoutUtility.GetRect(12f, EditorGUIUtility.singleLineHeight, GUILayout.Width(12f));
            float dotSize = 10f;
            Rect dotRect = new Rect(rowRect.x + 1f, rowRect.y + ((rowRect.height - dotSize) / 2f), dotSize, dotSize);
            EditorGUI.DrawRect(dotRect, GetStatusDotColor());

            GUILayout.Label(connectionStatusLabelText, GUILayout.MinWidth(60f));

            if (showConnectionToggle)
            {
                using (new EditorGUI.DisabledScope(!connectionToggleButtonEnabled || connectionToggleInProgress))
                {
                    if (GUILayout.Button(
                        new GUIContent(connectionToggleButtonText, connectionToggleButtonTooltip),
                        GUILayout.Width(110f)))
                    {
                        OnConnectionToggleClicked();
                    }
                }
            }

            EditorGUILayout.EndHorizontal();
        }

        private Color GetStatusDotColor()
        {
            switch (statusDotState)
            {
                case StatusDotState.Connected:
                    return new Color(0.36f, 0.78f, 0.35f);
                case StatusDotState.Disconnected:
                    return new Color(0.86f, 0.35f, 0.28f);
                default:
                    return new Color(0.62f, 0.62f, 0.62f);
            }
        }

        private void DrawManualCommandFoldout()
        {
            if (!showManualCommandFoldout)
            {
                return;
            }

            manualCommandFoldoutOpen = EditorGUILayout.Foldout(manualCommandFoldoutOpen, "Manual Server Launch", true);
            if (!manualCommandFoldoutOpen || !showHttpServerCommandSection)
            {
                return;
            }

            EditorGUI.indentLevel++;

            EditorGUILayout.LabelField("Use this command to launch the server manually:", EditorStyles.miniLabel);

            EditorGUILayout.BeginHorizontal();
            using (new EditorGUI.DisabledScope(true))
            {
                httpServerCommandField = EditorGUILayout.TextArea(
                    httpServerCommandField,
                    GUILayout.MinHeight(EditorGUIUtility.singleLineHeight * 2f));
            }
            using (new EditorGUI.DisabledScope(!copyHttpServerCommandEnabled))
            {
                if (GUILayout.Button(
                    "Copy",
                    GUILayout.Width(50f),
                    GUILayout.Height(EditorGUIUtility.singleLineHeight * 2f)))
                {
                    if (!string.IsNullOrEmpty(httpServerCommandField) && copyHttpServerCommandEnabled)
                    {
                        EditorGUIUtility.systemCopyBuffer = httpServerCommandField;
                        McpLog.Info("HTTP server command copied to clipboard.");
                    }
                }
            }
            EditorGUILayout.EndHorizontal();

            if (!string.IsNullOrEmpty(httpServerCommandHint))
            {
                EditorGUILayout.LabelField(httpServerCommandHint, EditorStyles.wordWrappedMiniLabel);
            }

            EditorGUI.indentLevel--;
        }

        private void OnTransportSelectionChanged(TransportProtocol previous, TransportProtocol selected)
        {
            bool useHttp = selected != TransportProtocol.Stdio;
            EditorConfigurationCache.Instance.SetUseHttpTransport(useHttp);

            // Clear any stale resume flags when user manually changes transport
            try { EditorPrefs.DeleteKey(EditorPrefKeys.ResumeStdioAfterReload); } catch { }
            HttpBridgeReloadHandler.CancelPendingResume();
            HttpAutoStartHandler.CancelPendingReconnect();

            if (useHttp)
            {
                string scope = selected == TransportProtocol.HTTPRemote ? "remote" : "local";
                EditorConfigurationCache.Instance.SetHttpTransportScope(scope);
            }

            // Swap the displayed URL to match the newly selected scope
            SyncUrlFieldToScope();
            UpdateHttpFieldVisibility();
            RefreshHttpUi();
            UpdateConnectionStatus();
            OnManualConfigUpdateRequested?.Invoke();
            OnTransportChanged?.Invoke();
            McpLog.Info($"Transport changed to: {selected}");

            // Best-effort: stop the deselected transport to avoid leaving duplicated sessions running.
            // (Switching between HttpLocal/HttpRemote does not require stopping.)
            bool prevWasHttp = previous != TransportProtocol.Stdio;
            bool nextIsHttp = selected != TransportProtocol.Stdio;
            if (prevWasHttp != nextIsHttp)
            {
                var stopMode = nextIsHttp ? TransportMode.Stdio : TransportMode.Http;
                try
                {
                    var stopTask = MCPServiceLocator.TransportManager.StopAsync(stopMode);
                    stopTask.ContinueWith(t =>
                    {
                        try
                        {
                            if (t.IsFaulted)
                            {
                                var msg = t.Exception?.GetBaseException()?.Message ?? "Unknown error";
                                McpLog.Warn($"Async stop of {stopMode} transport failed: {msg}");
                            }
                        }
                        catch { }
                    }, TaskScheduler.Default);
                }
                catch (Exception ex)
                {
                    McpLog.Warn($"Failed to stop previous transport ({stopMode}) after selection change: {ex.Message}");
                }
            }
        }

        private void PersistHttpUrlFromField()
        {
            HttpEndpointUtility.SaveBaseUrl(httpUrlField);
            // Update displayed value to normalized form.
            httpUrlField = HttpEndpointUtility.GetBaseUrl();
            // Invalidate cached login URL so it is re-fetched for the new base URL.
            cachedLoginUrl = null;
            OnManualConfigUpdateRequested?.Invoke();
            RefreshHttpUi();
        }

        // Consecutive failed reachability polls (0.75s cadence) required before an active
        // session is declared orphaned. A single stale reading used to be enough, and one
        // missed probe on a machine busy with test runs or reloads tore down healthy
        // sessions into reconnect churn (#1207).
        internal const int OrphanedSessionDownPollThreshold = 3;

        internal static bool ShouldEndOrphanedSession(
            bool httpLocalSelected,
            bool sessionRunning,
            bool toggleInProgress,
            bool editorBusy,
            int consecutiveDownPolls)
        {
            return httpLocalSelected
                && sessionRunning
                && !toggleInProgress
                && !editorBusy
                && consecutiveDownPolls >= OrphanedSessionDownPollThreshold;
        }

        // Consecutive failed bridge verifications required before the health indicator is
        // shown as Unhealthy ("broken"). A stdio domain reload briefly rebinds the listener
        // — the port can even hop (e.g. 6402 -> 6403) — during which a single VerifyAsync()
        // ping transiently fails with "Bridge not running" even though the bridge recovers on
        // its own. Flashing broken on that lone miss is misleading, so debounce: require
        // repeated failures before surfacing it.
        internal const int UnhealthyVerificationThreshold = 2;

        internal static bool ShouldReportUnhealthy(int consecutiveVerifyFailures)
            => consecutiveVerifyFailures >= UnhealthyVerificationThreshold;

        public void UpdateConnectionStatus()
        {
            var bridgeService = MCPServiceLocator.Bridge;
            bool isRunning = bridgeService.IsRunning;
            bool showLocalServerControls = IsHttpLocalSelected();
            bool debugMode = EditorPrefs.GetBool(EditorPrefKeys.DebugLogs, false);
            // EditorConfigurationCache is the source of truth for transport selection after domain reload
            // (EditorPrefs is still used for debugMode and other UI-only state)
            bool stdioSelected = !EditorConfigurationCache.Instance.UseHttpTransport;

            // Keep the Start/Stop Server button label in sync even when the session is not running
            // (e.g., orphaned server after a domain reload).
            // NOTE: This also updates lastLocalServerRunning which is used below for session toggle visibility.
            UpdateStartHttpButtonState();

            // A down-poll streak accumulated before/while no session was running must not
            // carry into a freshly started session — it would satisfy orphan detection
            // before the first post-start probe refreshes (the poll cadence is 0.75s).
            if (isRunning && !sessionWasRunning)
            {
                consecutiveServerDownPolls = 0;
            }
            sessionWasRunning = isRunning;

            // Detect orphaned session: if HTTP Local session thinks it's running but the server is gone,
            // automatically end the session to keep UI in sync with reality.
            bool editorBusy = EditorApplication.isCompiling || EditorApplication.isUpdating;
            if (ShouldEndOrphanedSession(showLocalServerControls, isRunning, connectionToggleInProgress,
                    editorBusy, consecutiveServerDownPolls))
            {
                McpLog.Info("Server no longer running; ending orphaned session.");
                _ = EndOrphanedSessionAsync();
                isRunning = false; // Update local state for the rest of this method
            }

            // For HTTP Local: show session toggle button only when server is running (so user can manually start/end session).
            // For Stdio/HTTP Remote: always show the session toggle button.
            // This separates server lifecycle from session lifecycle for multi-instance scenarios.
            // We use lastLocalServerRunning which was just refreshed by UpdateStartHttpButtonState() above.
            showConnectionToggle = !showLocalServerControls || lastLocalServerRunning;

            if (isRunning)
            {
                // Show instance name (project folder name) for better identification in multi-instance scenarios.
                // Defensive: handle edge cases where path parsing might return null/empty.
                string projectDir = System.IO.Path.GetDirectoryName(Application.dataPath);
                string instanceName = !string.IsNullOrEmpty(projectDir)
                    ? System.IO.Path.GetFileName(projectDir)
                    : "Unity";
                if (string.IsNullOrEmpty(instanceName)) instanceName = "Unity";
                connectionStatusLabelText = $"Session Active ({instanceName})";
                statusDotState = StatusDotState.Connected;
                connectionToggleButtonText = stdioSelected ? "End Session" : "Disconnect";
                connectionToggleButtonEnabled = true; // Re-enable in case it was disabled during resumption

                // Force the UI to reflect the actual port being used
                unityPortField = bridgeService.CurrentPort.ToString();
                unityPortEnabled = false;
            }
            else
            {
                // Check if we're resuming the stdio bridge after a domain reload.
                // During this brief window, show "Resuming..." instead of "No Session" to avoid UI flicker.
                bool isStdioResuming = stdioSelected
                    && EditorPrefs.GetBool(EditorPrefKeys.ResumeStdioAfterReload, false);

                if (isStdioResuming)
                {
                    connectionStatusLabelText = "Resuming...";
                    // Keep the indicator in a neutral/transitional state
                    statusDotState = StatusDotState.Neutral;
                    connectionToggleButtonText = stdioSelected ? "Start Session" : "Connect";
                    connectionToggleButtonEnabled = false;
                }
                else
                {
                    connectionStatusLabelText = "No Session";
                    statusDotState = StatusDotState.Disconnected;
                    connectionToggleButtonText = stdioSelected ? "Start Session" : "Connect";

                    bool httpRemoteSelected = transportSelection == TransportProtocol.HTTPRemote;
                    bool httpRemoteNeedsKey = httpRemoteSelected
                        && string.IsNullOrEmpty(EditorPrefs.GetString(EditorPrefKeys.ApiKey, string.Empty));
                    string remoteUrlError = null;
                    bool remoteUrlAllowed = !httpRemoteSelected
                        || HttpEndpointUtility.IsCurrentRemoteUrlAllowed(out remoteUrlError);

                    bool httpLocalSelected = IsHttpLocalSelected();
                    string localUrlError = null;
                    bool localUrlAllowed = !httpLocalSelected
                        || TryGetLocalHttpLaunchPolicy(out _, out localUrlError);

                    bool blockedByRemoteUrlPolicy = httpRemoteSelected && !remoteUrlAllowed;
                    bool blockedByLocalUrlPolicy = httpLocalSelected && !localUrlAllowed;
                    bool canStartSession = !httpRemoteNeedsKey && !blockedByRemoteUrlPolicy && !blockedByLocalUrlPolicy;
                    connectionToggleButtonEnabled = canStartSession;

                    if (httpRemoteNeedsKey)
                    {
                        connectionToggleButtonTooltip = "An API key is required for HTTP Remote. Enter one above.";
                    }
                    else if (blockedByRemoteUrlPolicy)
                    {
                        connectionToggleButtonTooltip = remoteUrlError ?? "HTTP Remote URL is blocked by current security settings.";
                    }
                    else if (blockedByLocalUrlPolicy)
                    {
                        connectionToggleButtonTooltip = localUrlError ?? "HTTP Local URL is blocked by current security settings.";
                    }
                    else
                    {
                        connectionToggleButtonTooltip = string.Empty;
                    }
                }

                unityPortEnabled = !isStdioResuming;

                int savedPort = EditorPrefs.GetInt(EditorPrefKeys.UnitySocketPort, 0);
                unityPortField = (savedPort == 0
                    ? bridgeService.CurrentPort
                    : savedPort).ToString();
            }
        }

        public void UpdateHttpServerCommandDisplay()
        {
            bool useHttp = transportSelection != TransportProtocol.Stdio;
            bool httpLocalSelected = IsHttpLocalSelected();
            bool isLocalHttpUrlAllowed = TryGetLocalHttpLaunchPolicy(out _, out string localUrlError);

            // Only show the local-server helper UI when HTTP Local is selected.
            if (!useHttp || !httpLocalSelected)
            {
                showHttpServerCommandSection = false;
                httpServerCommandField = string.Empty;
                httpServerCommandHint = string.Empty;
                copyHttpServerCommandEnabled = false;
                return;
            }

            showHttpServerCommandSection = true;

            if (!isLocalHttpUrlAllowed)
            {
                httpServerCommandField = string.Empty;
                string requirements = HttpEndpointUtility.GetHttpLocalHostRequirementText();
                httpServerCommandHint = $"⚠ {localUrlError ?? $"HTTP Local requires a loopback URL ({requirements})."}";
                copyHttpServerCommandEnabled = false;
                return;
            }

            if (MCPServiceLocator.Server.TryGetLocalHttpServerCommand(out var command, out var error))
            {
                httpServerCommandField = command;
                httpServerCommandHint = "Run this command in your shell if you prefer to start the server manually.";
                copyHttpServerCommandEnabled = true;
            }
            else
            {
                httpServerCommandField = string.Empty;
                httpServerCommandHint = error ?? "The command is not available with the current configuration.";
                copyHttpServerCommandEnabled = false;
            }
        }

        private void UpdateHttpFieldVisibility()
        {
            bool useHttp = transportSelection != TransportProtocol.Stdio;
            bool httpLocalSelected = IsHttpLocalSelected();
            bool httpRemoteSelected = transportSelection == TransportProtocol.HTTPRemote;

            showHttpUrlRow = useHttp;
            showHttpServerControlRow = useHttp && httpLocalSelected;
            showUnitySocketPortRow = !useHttp;

            // Manual Server Launch foldout only relevant for HTTP Local
            showManualCommandFoldout = httpLocalSelected;

            // API key fields only visible in HTTP Remote mode
            showApiKeyRow = httpRemoteSelected;
        }

        private bool IsHttpLocalSelected()
        {
            return transportSelection == TransportProtocol.HTTPLocal;
        }

        private bool TryGetLocalHttpLaunchPolicy(out string localBaseUrl, out string localUrlError)
        {
            localBaseUrl = HttpEndpointUtility.GetLocalBaseUrl();
            return HttpEndpointUtility.IsHttpLocalUrlAllowedForLaunch(localBaseUrl, out localUrlError);
        }

        private void SyncUrlFieldToScope()
        {
            httpUrlField = HttpEndpointUtility.GetBaseUrl();
            cachedLoginUrl = null;
        }

        private void UpdateStartHttpButtonState()
        {
            bool useHttp = transportSelection != TransportProtocol.Stdio;
            if (!useHttp)
            {
                startHttpServerButtonEnabled = false;
                startHttpServerButtonTooltip = string.Empty;
                return;
            }

            bool httpLocalSelected = IsHttpLocalSelected();
            bool localUrlAllowedForLaunch = TryGetLocalHttpLaunchPolicy(out _, out string localUrlError);
            bool canStartLocalServer = httpLocalSelected && localUrlAllowedForLaunch;
            bool localServerRunning = false;

            // Avoid running expensive port/PID checks every UI tick; use a fast socket probe for UI state.
            if (httpLocalSelected)
            {
                double now = EditorApplication.timeSinceStartup;
                if ((now - lastLocalServerRunningPollTime) > 0.75f || httpServerToggleInProgress)
                {
                    lastLocalServerRunningPollTime = now;
                    lastLocalServerRunning = MCPServiceLocator.Server.IsLocalHttpServerReachable();

                    // Probe results taken while the editor is busy are unreliable (main-thread
                    // stalls starve the connect wait), so they don't count toward teardown.
                    if (EditorApplication.isCompiling || EditorApplication.isUpdating)
                    {
                        consecutiveServerDownPolls = 0;
                    }
                    else
                    {
                        consecutiveServerDownPolls = lastLocalServerRunning ? 0 : consecutiveServerDownPolls + 1;
                    }
                }
                localServerRunning = lastLocalServerRunning;
            }

            // Server button only controls server lifecycle (Start/Stop Server).
            // Session lifecycle is handled by the separate session toggle button.
            bool shouldShowStop = localServerRunning;
            // While we are launching+connecting and the server isn't up yet, show a transient "Starting…" label.
            if (httpServerToggleInProgress && !localServerRunning)
            {
                startHttpServerButtonText = "Starting…";
            }
            else
            {
                startHttpServerButtonText = shouldShowStop ? "Stop Server" : "Start Server";
            }
            // Note: Server logs may contain transient HTTP 400s on /mcp during startup probing and
            // CancelledError stack traces on shutdown when streaming requests are cancelled; this is expected.
            startHttpServerButtonEnabled = !httpServerToggleInProgress && (shouldShowStop || canStartLocalServer);
            startHttpServerButtonTooltip = httpLocalSelected
                ? (canStartLocalServer
                    ? string.Empty
                    : localUrlError ?? $"HTTP Local requires a loopback URL ({HttpEndpointUtility.GetHttpLocalHostRequirementText()}).")
                : string.Empty;
        }

        private void RefreshHttpUi()
        {
            UpdateStartHttpButtonState();
            UpdateHttpServerCommandDisplay();
        }

        private async void OnHttpServerToggleClicked()
        {
            if (httpServerToggleInProgress)
            {
                return;
            }

            var bridgeService = MCPServiceLocator.Bridge;
            httpServerToggleInProgress = true;

            try
            {
                // Check if a local server is running.
                bool serverRunning = IsHttpLocalSelected() && MCPServiceLocator.Server.IsLocalHttpServerReachable();

                if (serverRunning)
                {
                    // Stop Server: end session first (if active), then stop the server.
                    if (bridgeService.IsRunning)
                    {
                        await bridgeService.StopAsync();
                    }
                    bool stopped = MCPServiceLocator.Server.StopLocalHttpServer();
                    if (stopped)
                    {
                        McpLog.Info("Server stopped");
                    }
                    else
                    {
                        McpLog.Warn("Failed to stop HTTP server or no server was running");
                    }
                }
                else
                {
                    // Start Server: launch the local HTTP server.
                    // When WE start the server, auto-start our session (we clearly want to use it).
                    // This differs from detecting an already-running server, where we require manual session start.
                    if (!TryGetLocalHttpLaunchPolicy(out _, out string localPolicyError))
                    {
                        string errorMsg = localPolicyError ?? "HTTP Local URL is blocked by current security settings.";
                        EditorUtility.DisplayDialog("Cannot Start HTTP Server", errorMsg, "OK");
                        McpLog.Warn($"Start server blocked by local URL security policy: {errorMsg}");
                        return;
                    }

                    // Reflect the transient "Starting…" state on the button before the (possibly long) launch.
                    UpdateStartHttpButtonState();

                    bool serverStarted = MCPServiceLocator.Server.StartLocalHttpServer();
                    if (serverStarted)
                    {
                        await TryAutoStartSessionAsync();
                    }
                    else
                    {
                        McpLog.Warn("Failed to start local HTTP server");
                    }
                }
            }
            catch (Exception ex)
            {
                McpLog.Error($"HTTP server toggle failed: {ex.Message}");
                EditorUtility.DisplayDialog("Error", $"Failed to toggle local HTTP server:\n\n{ex.Message}", "OK");
            }
            finally
            {
                httpServerToggleInProgress = false;
                RefreshHttpUi();
                UpdateConnectionStatus();
            }
        }

        private async Task TryAutoStartSessionAsync()
        {
            // Wait for the HTTP server to become ready, then start the session.
            // This is called when THIS instance starts the server (not when detecting an external server).
            // Patience: keep polling reachability while the launched process is alive; declare failure only
            // when the process exits without the port coming up, or a generous hard cap is reached.
            var server = MCPServiceLocator.Server;
            var bridgeService = MCPServiceLocator.Bridge;
            string url = HttpEndpointUtility.GetLocalBaseUrl();

            var pollDelay = TimeSpan.FromMilliseconds(500);
            var hardCap = TimeSpan.FromMinutes(5);
            double startTime = EditorApplication.timeSinceStartup;

            while (true)
            {
                if (server.IsLocalHttpServerReachable())
                {
                    McpLog.Info($"Server ready on {url}");
                    bool started = await bridgeService.StartAsync();
                    if (started)
                    {
                        McpLog.Info("Session connected");
                        await VerifyBridgeConnectionAsync();
                        UpdateConnectionStatus();
                        return;
                    }
                }

                bool processAlive = server.IsManagedServerLaunchProcessAlive();
                double elapsed = EditorApplication.timeSinceStartup - startTime;

                if ((!processAlive && elapsed > 1.0) || elapsed > hardCap.TotalSeconds)
                {
                    // Last-resort connect attempt in case reachability detection missed a live server.
                    if (await bridgeService.StartAsync())
                    {
                        McpLog.Info("Session connected");
                        await VerifyBridgeConnectionAsync();
                        UpdateConnectionStatus();
                        return;
                    }

                    server.LogLocalHttpServerLaunchFailure();
                    return;
                }

                await Task.Delay(pollDelay);
            }
        }

        private void PersistUnityPortFromField()
        {
            string input = unityPortField?.Trim();
            if (!int.TryParse(input, out int requestedPort) || requestedPort <= 0)
            {
                unityPortField = MCPServiceLocator.Bridge.CurrentPort.ToString();
                return;
            }

            try
            {
                int storedPort = PortManager.SetPreferredPort(requestedPort);
                EditorPrefs.SetInt(EditorPrefKeys.UnitySocketPort, storedPort);
                unityPortField = storedPort.ToString();
            }
            catch (Exception ex)
            {
                McpLog.Warn($"Failed to persist Unity socket port: {ex.Message}");
                EditorUtility.DisplayDialog(
                    "Port Unavailable",
                    $"The requested port could not be used:\n\n{ex.Message}\n\nReverting to the active Unity port.",
                    "OK");
                unityPortField = MCPServiceLocator.Bridge.CurrentPort.ToString();
            }
        }

        private async void OnConnectionToggleClicked()
        {
            if (connectionToggleInProgress)
            {
                return;
            }

            var bridgeService = MCPServiceLocator.Bridge;
            connectionToggleInProgress = true;

            try
            {
                bool httpRemoteForLog = transportSelection == TransportProtocol.HTTPRemote;

                if (bridgeService.IsRunning)
                {
                    // Clear any resume flags when user manually ends the session to prevent
                    // getting stuck in "Resuming..." state (the flag may have been set by a
                    // domain reload that started just before the user clicked End Session)
                    try { EditorPrefs.DeleteKey(EditorPrefKeys.ResumeStdioAfterReload); } catch { }
                    HttpBridgeReloadHandler.CancelPendingResume();
                    HttpAutoStartHandler.CancelPendingReconnect();

                    await bridgeService.StopAsync();
                    if (httpRemoteForLog)
                    {
                        McpLog.Info("Disconnected");
                    }
                }
                else
                {
                    bool httpRemoteSelected = httpRemoteForLog;
                    if (httpRemoteSelected
                        && !HttpEndpointUtility.IsCurrentRemoteUrlAllowed(out string remotePolicyError))
                    {
                        string errorMsg = remotePolicyError ?? "HTTP Remote URL is blocked by current security settings.";
                        EditorUtility.DisplayDialog("Connection Blocked", errorMsg, "OK");
                        McpLog.Warn($"Connection blocked by remote URL security policy: {errorMsg}");
                        return;
                    }

                    bool httpLocalSelected = IsHttpLocalSelected();
                    if (httpLocalSelected
                        && !TryGetLocalHttpLaunchPolicy(out _, out string localPolicyError))
                    {
                        string errorMsg = localPolicyError ?? "HTTP Local URL is blocked by current security settings.";
                        EditorUtility.DisplayDialog("Connection Blocked", errorMsg, "OK");
                        McpLog.Warn($"Connection blocked by local URL security policy: {errorMsg}");
                        return;
                    }

                    if (httpRemoteSelected)
                    {
                        McpLog.Info($"Connecting to {HttpEndpointUtility.GetRemoteBaseUrl()}…");
                    }

                    // The user is taking over bridge lifecycle: drop any pending reload-resume
                    // or interrupted auto-start reconnect so neither can bounce the session
                    // we are about to establish.
                    HttpBridgeReloadHandler.CancelPendingResume();
                    HttpAutoStartHandler.CancelPendingReconnect();

                    bool started = await bridgeService.StartAsync();
                    if (started)
                    {
                        if (httpRemoteSelected)
                        {
                            McpLog.Info("Connected");
                        }
                        await VerifyBridgeConnectionAsync();
                    }
                    else
                    {
                        var mode = EditorConfigurationCache.Instance.UseHttpTransport
                            ? TransportMode.Http : TransportMode.Stdio;
                        var state = MCPServiceLocator.TransportManager.GetState(mode);
                        string errorMsg = state?.Error
                            ?? "Failed to start the MCP session. Check the server URL and that the server is running.";
                        EditorUtility.DisplayDialog("Connection Failed", errorMsg, "OK");
                        if (httpRemoteSelected)
                        {
                            McpLog.Error($"Connection failed: {errorMsg}");
                        }
                        else
                        {
                            McpLog.Warn($"Failed to start MCP bridge: {errorMsg}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                McpLog.Error($"Connection toggle failed: {ex.Message}");
                EditorUtility.DisplayDialog("Connection Error",
                    $"Failed to toggle the MCP connection:\n\n{ex.Message}",
                    "OK");
            }
            finally
            {
                connectionToggleInProgress = false;
                UpdateConnectionStatus();
            }
        }

        private async Task EndOrphanedSessionAsync()
        {
            // Fire-and-forget cleanup of orphaned session when server is no longer running.
            // This prevents the UI from showing "Session Active" when the underlying server is gone.
            try
            {
                connectionToggleInProgress = true;

                // Clear resume flags to prevent getting stuck in "Resuming..." state
                try { EditorPrefs.DeleteKey(EditorPrefKeys.ResumeStdioAfterReload); } catch { }
                HttpBridgeReloadHandler.CancelPendingResume();
                HttpAutoStartHandler.CancelPendingReconnect();

                await MCPServiceLocator.Bridge.StopAsync();
            }
            catch (Exception ex)
            {
                McpLog.Warn($"Failed to end orphaned session: {ex.Message}");
            }
            finally
            {
                connectionToggleInProgress = false;
                UpdateConnectionStatus();
            }
        }

        private void PersistApiKeyFromField()
        {
            string apiKey = apiKeyField?.Trim() ?? string.Empty;
            string existingKey = EditorPrefs.GetString(EditorPrefKeys.ApiKey, string.Empty);

            if (apiKey != existingKey)
            {
                EditorPrefs.SetString(EditorPrefKeys.ApiKey, apiKey);
                OnManualConfigUpdateRequested?.Invoke();
                UpdateConnectionStatus();
                McpLog.Info(string.IsNullOrEmpty(apiKey) ? "API key cleared" : "API key updated");
            }
        }

        private async void OnGetApiKeyClicked()
        {
            getApiKeyButtonEnabled = false;

            try
            {
                string loginUrl = await GetLoginUrlAsync();
                if (string.IsNullOrEmpty(loginUrl))
                {
                    EditorUtility.DisplayDialog("API Key",
                        "API key management is not available for this server. Contact your server administrator.",
                        "OK");
                    return;
                }
                Application.OpenURL(loginUrl);
            }
            catch (Exception ex)
            {
                McpLog.Error($"Failed to get login URL: {ex.Message}");
                EditorUtility.DisplayDialog("Error",
                    $"Failed to get API key login URL:\n\n{ex.Message}",
                    "OK");
            }
            finally
            {
                getApiKeyButtonEnabled = true;
            }
        }

        private async Task<string> GetLoginUrlAsync()
        {
            if (!string.IsNullOrEmpty(cachedLoginUrl))
            {
                return cachedLoginUrl;
            }

            string baseUrl = HttpEndpointUtility.GetBaseUrl();
            string loginUrlEndpoint = $"{baseUrl.TrimEnd('/')}/api/auth/login-url";

            try
            {
                using (var client = new System.Net.Http.HttpClient())
                {
                    client.Timeout = TimeSpan.FromSeconds(10);
                    var response = await client.GetAsync(loginUrlEndpoint);

                    if (response.IsSuccessStatusCode)
                    {
                        string json = await response.Content.ReadAsStringAsync();
                        var result = Newtonsoft.Json.Linq.JObject.Parse(json);

                        if (result.Value<bool>("success"))
                        {
                            cachedLoginUrl = result.Value<string>("login_url");
                            return cachedLoginUrl;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                McpLog.Debug($"Failed to fetch login URL from {loginUrlEndpoint}: {ex.Message}");
            }

            return null;
        }

        private void OnClearApiKeyClicked()
        {
            EditorPrefs.SetString(EditorPrefKeys.ApiKey, string.Empty);
            apiKeyField = string.Empty;
            OnManualConfigUpdateRequested?.Invoke();
            UpdateConnectionStatus();
            McpLog.Info("API key cleared");
        }

        public async Task VerifyBridgeConnectionAsync()
        {
            // Prevent concurrent verification calls
            if (verificationTask != null && !verificationTask.IsCompleted)
            {
                return;
            }

            verificationTask = VerifyBridgeConnectionInternalAsync();
            await verificationTask;
        }

        private async Task VerifyBridgeConnectionInternalAsync()
        {
            var bridgeService = MCPServiceLocator.Bridge;
            if (!bridgeService.IsRunning)
            {
                onHealthStatusUpdate?.Invoke(false, HealthStatus.Unknown);

                // Only log if state changed
                if (lastHealthStatus != HealthStatus.Unknown)
                {
                    McpLog.Warn("Cannot verify connection: Bridge is not running");
                    lastHealthStatus = HealthStatus.Unknown;
                }
                return;
            }

            var result = await bridgeService.VerifyAsync();

            string newStatus;
            bool isHealthy;
            if (result.Success && result.PingSucceeded)
            {
                newStatus = HealthStatus.Healthy;
                isHealthy = true;
                consecutiveVerifyFailures = 0;

                // Only log if state changed
                if (lastHealthStatus != newStatus)
                {
                    McpLog.Debug($"Connection verification successful: {result.Message}");
                    lastHealthStatus = newStatus;
                }
            }
            else if (result.HandshakeValid)
            {
                // Handshake succeeded, so the socket is reachable — not the transient
                // rebind case. Ping failing is a genuine warning; reset the miss counter.
                newStatus = HealthStatus.PingFailed;
                isHealthy = false;
                consecutiveVerifyFailures = 0;

                // Log once per distinct warning state
                if (lastHealthStatus != newStatus)
                {
                    McpLog.Warn($"Connection verification warning: {result.Message}");
                    lastHealthStatus = newStatus;
                }
            }
            else
            {
                // Could not reach the bridge at all. A stdio reload rebinds the listener
                // (the port can hop), so one miss does not mean it is down. Debounce: only
                // surface "broken" after repeated failures (mirrors #1207). Until then leave
                // the indicator in its current state; the next verification resolves it.
                consecutiveVerifyFailures++;
                if (!ShouldReportUnhealthy(consecutiveVerifyFailures))
                {
                    McpLog.Debug(
                        $"Connection verification miss {consecutiveVerifyFailures}/{UnhealthyVerificationThreshold} " +
                        $"(transient, not surfacing): {result.Message}");
                    return;
                }

                newStatus = HealthStatus.Unhealthy;
                isHealthy = false;

                // Log once per distinct error state
                if (lastHealthStatus != newStatus)
                {
                    McpLog.Error($"Connection verification failed: {result.Message}");
                    lastHealthStatus = newStatus;
                }
            }

            onHealthStatusUpdate?.Invoke(isHealthy, newStatus);
        }

        /// <summary>
        /// Updates the transport mismatch warning banner based on the client's configured transport.
        /// Shows a warning if the client's transport doesn't match the server's current transport setting.
        /// </summary>
        /// <param name="clientName">The display name of the client being checked.</param>
        /// <param name="clientTransport">The transport the client is configured to use.</param>
        public void UpdateTransportMismatchWarning(string clientName, ConfiguredTransport clientTransport)
        {
            // If client transport is unknown, hide the warning (we can't determine mismatch)
            if (clientTransport == ConfiguredTransport.Unknown)
            {
                transportMismatchVisible = false;
                return;
            }

            // When a project dir override is active the registered transport belongs
            // to a different project and may legitimately differ from the local server
            // transport, so skip the mismatch check.
            if (ClaudeCliMcpConfigurator.HasClientProjectDirOverride)
            {
                transportMismatchVisible = false;
                return;
            }

            // Determine the server's current transport setting (3-way: Stdio, Http, HttpRemote)
            ConfiguredTransport serverTransport = HttpEndpointUtility.GetCurrentServerTransport();

            // Check for mismatch
            bool hasMismatch = clientTransport != serverTransport;

            if (hasMismatch)
            {
                string clientTransportName = TransportDisplayName(clientTransport);
                string serverTransportName = TransportDisplayName(serverTransport);

                transportMismatchText = $"⚠ {clientName} is configured for \"{clientTransportName}\" but server is set to \"{serverTransportName}\". " +
                    "Click \"Configure\" in Client Configuration to update.";
                transportMismatchVisible = true;
            }
            else
            {
                transportMismatchVisible = false;
            }
        }

        /// <summary>
        /// Clears the transport mismatch warning banner.
        /// </summary>
        public void ClearTransportMismatchWarning()
        {
            transportMismatchVisible = false;
        }

        /// <summary>
        /// Updates the version mismatch warning banner based on the client's configuration status.
        /// Shows a warning if the client is registered with a different package version than expected.
        /// </summary>
        /// <param name="clientName">The display name of the client being checked.</param>
        /// <param name="mismatchMessage">The mismatch message, or null if no mismatch.</param>
        public void UpdateVersionMismatchWarning(string clientName, string mismatchMessage)
        {
            if (string.IsNullOrEmpty(mismatchMessage))
            {
                versionMismatchVisible = false;
                return;
            }

            versionMismatchText = $"⚠ {clientName}: {mismatchMessage}";
            versionMismatchVisible = true;
        }

        /// <summary>
        /// Clears the version mismatch warning banner.
        /// </summary>
        public void ClearVersionMismatchWarning()
        {
            versionMismatchVisible = false;
        }

        private static string TransportDisplayName(ConfiguredTransport transport)
        {
            switch (transport)
            {
                case ConfiguredTransport.Stdio: return "stdio";
                case ConfiguredTransport.Http: return "HTTP Local";
                case ConfiguredTransport.HttpRemote: return "HTTP Remote";
                default: return "unknown";
            }
        }
    }
}
