using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using MCPForUnity.Editor.Constants;
using MCPForUnity.Editor.Helpers;
using MCPForUnity.Editor.Services;
using UnityEditor;
using UnityEngine;

namespace MCPForUnity.Editor.Windows.Components.Advanced
{
    /// <summary>
    /// Controller for the Advanced Settings section.
    /// Handles path overrides, server source configuration, dev mode, and package deployment.
    /// </summary>
    public class McpAdvancedSection
    {
        private enum UvxStatusKind
        {
            Valid,
            Invalid,
            Warning
        }

        private enum HealthKind
        {
            Healthy,
            Disconnected,
            Unknown
        }

        // UI state (IMGUI backing fields)
        private string uvxPathDisplay = string.Empty;
        private UvxStatusKind uvxPathStatus = UvxStatusKind.Invalid;
        private string gitUrl = string.Empty;
        private bool debugLogs;
        private bool logRecord;
        private bool autoStartOnLoad;
        private bool devModeForceRefresh;
        private bool allowLanHttpBind;
        private bool allowInsecureRemoteHttp;
        private string screenshotsFolder = string.Empty;
        private string deploySourcePath = string.Empty;
        private string deployTargetText = string.Empty;
        private string deployBackupText = string.Empty;
        private bool deployRestoreEnabled;
        private string deployStatusText = string.Empty;
        private bool deployStatusIsError;
        private string healthStatusText = "Unknown";
        private HealthKind healthStatusKind = HealthKind.Unknown;

        // Tooltips (previously assigned to the UXML elements in InitializeUI)
        private const string UvxPathTooltip = "Override path to uvx executable. Leave empty for auto-detection.";
        private const string GitUrlTooltip = "Override server source for uvx --from. Leave empty to use default PyPI package. Example local dev: /path/to/unity-mcp/Server";
        private const string DebugLogsTooltip = "Enable verbose debug logging to the Unity Console.";
        private const string AutoStartTooltip = "Automatically start the local HTTP server and connect the MCP bridge when the Unity Editor opens. Only applies to HTTP transport (stdio always auto-starts).";
        private const string LogRecordTooltip = "Log every MCP tool execution (tool, action, status, duration) to Assets/UnityMCP/Log/mcp.log.";
        private const string DevModeForceRefreshTooltip = "When enabled, generated uvx commands add '--no-cache --refresh' before launching (slower startup, but avoids stale cached builds while iterating on the Server).";
        private const string AllowLanHttpBindTooltip = "Allow HTTP Local to bind on all interfaces (0.0.0.0 / ::). Disabled by default because devices on your LAN may reach MCP tools.";
        private const string AllowInsecureRemoteHttpTooltip = "Allow HTTP Remote over plaintext http/ws. Disabled by default to require HTTPS/WSS.";
        private const string TestConnectionTooltip = "Test the connection between Unity and the MCP server.";
        private const string ScreenshotsFolderTooltip = "Default folder for screenshots from manage_camera / manage_ui. " +
            "Project-relative (e.g. 'Assets/Screenshots' or 'Captures'). Empty = built-in default (Assets/Screenshots). " +
            "Per-call 'output_folder' parameters always override this.";
        private const string BrowseScreenshotsFolderTooltip = "Pick a folder inside the project; the path is stored project-relative.";
        private const string ClearScreenshotsFolderTooltip = "Clear override and use the built-in default (Assets/Screenshots).";
        private const string DeploySourcePathTooltip = "Copy a MCPForUnity folder into this project's package location.";
        private const string BrowseUvxTooltip = "Browse for uvx executable";
        private const string ClearUvxTooltip = "Clear override and use auto-detection";
        private const string BrowseGitUrlTooltip = "Select local server source folder";
        private const string ClearGitUrlTooltip = "Clear override and use default PyPI package";
        private const string BrowseDeploySourceTooltip = "Select MCPForUnity source folder";
        private const string ClearDeploySourceTooltip = "Clear deployment source path";
        private const string DeployButtonTooltip = "Copy MCPForUnity to this project's package location";
        private const string DeployRestoreButtonTooltip = "Restore the last backup before deployment";

        // Status indicator colors (approximates the USS classes valid/invalid/warning/healthy/disconnected/unknown)
        private static readonly Color ValidColor = new Color(0.35f, 0.8f, 0.35f);
        private static readonly Color InvalidColor = new Color(0.9f, 0.25f, 0.25f);
        private static readonly Color WarningColor = new Color(0.95f, 0.8f, 0.2f);
        private static readonly Color UnknownColor = new Color(0.6f, 0.6f, 0.6f);
        private static readonly Color DeployErrorColor = new Color(0.85f, 0.2f, 0.2f);

        private static readonly Dictionary<Color, Texture2D> DotTextureCache = new Dictionary<Color, Texture2D>();

        // Events
        public event Action OnGitUrlChanged;
        public event Action OnHttpServerCommandUpdateRequested;
        public event Action OnTestConnectionRequested;
        public event Action OnPackageDeployed;

        public McpAdvancedSection()
        {
            InitializeState();
        }

        private void InitializeState()
        {
            bool debugEnabled = EditorPrefs.GetBool(EditorPrefKeys.DebugLogs, false);
            debugLogs = debugEnabled;
            McpLog.SetDebugLoggingEnabled(debugEnabled);

            logRecord = McpLogRecord.IsEnabled;
            screenshotsFolder = ScreenshotPreferences.DefaultFolder;

            UpdatePathOverrides();
        }

        /// <summary>
        /// Draws the Advanced Settings section (mirrors McpAdvancedSection.uxml layout).
        /// </summary>
        public void Draw()
        {
            GUILayout.Label("Advanced Settings", EditorStyles.boldLabel);

            // UVX path override row
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label("UVX Path:", EditorStyles.miniLabel, GUILayout.Width(80f));
            DrawStatusDot(GetUvxStatusColor());
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.TextField(new GUIContent(uvxPathDisplay, UvxPathTooltip), GUILayout.MinWidth(120f));
            }
            if (GUILayout.Button(new GUIContent("Browse", BrowseUvxTooltip), EditorStyles.miniButton, GUILayout.Width(60f)))
            {
                OnBrowseUvxClicked();
            }
            if (GUILayout.Button(new GUIContent("Clear", ClearUvxTooltip), EditorStyles.miniButton, GUILayout.Width(60f)))
            {
                OnClearUvxClicked();
            }
            EditorGUILayout.EndHorizontal();

            // Server source override row
            GUILayout.Label("Server Source:", EditorStyles.miniLabel, GUILayout.Height(18f));
            EditorGUILayout.BeginHorizontal();
            string newGitUrl = EditorGUILayout.TextField(new GUIContent(gitUrl, GitUrlTooltip), GUILayout.MinWidth(120f));
            if (newGitUrl != gitUrl)
            {
                string url = newGitUrl != null ? newGitUrl.Trim() : string.Empty;
                if (string.IsNullOrEmpty(url))
                {
                    EditorPrefs.DeleteKey(EditorPrefKeys.GitUrlOverride);
                    gitUrl = url;
                }
                else
                {
                    url = ResolveServerPath(url);
                    gitUrl = url;
                    EditorPrefs.SetString(EditorPrefKeys.GitUrlOverride, url);
                }
                OnGitUrlChanged?.Invoke();
                OnHttpServerCommandUpdateRequested?.Invoke();
            }
            if (GUILayout.Button(new GUIContent("Select", BrowseGitUrlTooltip), EditorStyles.miniButton, GUILayout.Width(60f)))
            {
                OnBrowseGitUrlClicked();
            }
            if (GUILayout.Button(new GUIContent("Clear", ClearGitUrlTooltip), EditorStyles.miniButton, GUILayout.Width(60f)))
            {
                OnClearGitUrlClicked();
            }
            EditorGUILayout.EndHorizontal();

            // Toggle rows
            bool newDebugLogs = EditorGUILayout.Toggle(new GUIContent("Debug Logging:", DebugLogsTooltip), debugLogs);
            if (newDebugLogs != debugLogs)
            {
                debugLogs = newDebugLogs;
                McpLog.SetDebugLoggingEnabled(newDebugLogs);
            }

            bool newLogRecord = EditorGUILayout.Toggle(new GUIContent("Log Record (Assets/mcp.log):", LogRecordTooltip), logRecord);
            if (newLogRecord != logRecord)
            {
                logRecord = newLogRecord;
                McpLogRecord.IsEnabled = newLogRecord;
            }

            // Server health row
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label("Server Health:", EditorStyles.miniLabel, GUILayout.Width(120f));
            DrawStatusDot(GetHealthColor());
            GUILayout.Label(healthStatusText, EditorStyles.miniLabel);
            if (GUILayout.Button(new GUIContent("Test", TestConnectionTooltip), EditorStyles.miniButton, GUILayout.Width(50f)))
            {
                OnTestConnectionRequested?.Invoke();
            }
            EditorGUILayout.EndHorizontal();

            bool newAutoStart = EditorGUILayout.Toggle(new GUIContent("Auto-Start Server on Editor Load:", AutoStartTooltip), autoStartOnLoad);
            if (newAutoStart != autoStartOnLoad)
            {
                autoStartOnLoad = newAutoStart;
                EditorPrefs.SetBool(EditorPrefKeys.AutoStartOnLoad, newAutoStart);
            }

            bool newForceRefresh = EditorGUILayout.Toggle(new GUIContent("Force Fresh Install:", DevModeForceRefreshTooltip), devModeForceRefresh);
            if (newForceRefresh != devModeForceRefresh)
            {
                devModeForceRefresh = newForceRefresh;
                EditorPrefs.SetBool(EditorPrefKeys.DevModeForceServerRefresh, newForceRefresh);
                OnHttpServerCommandUpdateRequested?.Invoke();
            }

            bool newLanBind = EditorGUILayout.Toggle(new GUIContent("Allow LAN Bind (HTTP Local):", AllowLanHttpBindTooltip), allowLanHttpBind);
            if (newLanBind != allowLanHttpBind)
            {
                allowLanHttpBind = newLanBind;
                EditorPrefs.SetBool(EditorPrefKeys.AllowLanHttpBind, newLanBind);
                OnHttpServerCommandUpdateRequested?.Invoke();
            }

            bool newInsecureRemote = EditorGUILayout.Toggle(new GUIContent("Allow Insecure Remote HTTP:", AllowInsecureRemoteHttpTooltip), allowInsecureRemoteHttp);
            if (newInsecureRemote != allowInsecureRemoteHttp)
            {
                allowInsecureRemoteHttp = newInsecureRemote;
                EditorPrefs.SetBool(EditorPrefKeys.AllowInsecureRemoteHttp, newInsecureRemote);
                OnHttpServerCommandUpdateRequested?.Invoke();
            }

            // Screenshots folder override row
            GUILayout.Label("Screenshots Folder:", EditorStyles.miniLabel, GUILayout.Height(18f));
            EditorGUILayout.BeginHorizontal();
            string newScreenshotsFolder = EditorGUILayout.TextField(new GUIContent(screenshotsFolder, ScreenshotsFolderTooltip), GUILayout.MinWidth(120f));
            if (newScreenshotsFolder != screenshotsFolder)
            {
                screenshotsFolder = newScreenshotsFolder;
                ScreenshotPreferences.DefaultFolder = newScreenshotsFolder;
            }
            if (GUILayout.Button(new GUIContent("Select", BrowseScreenshotsFolderTooltip), EditorStyles.miniButton, GUILayout.Width(60f)))
            {
                OnBrowseScreenshotsFolderClicked();
            }
            if (GUILayout.Button(new GUIContent("Clear", ClearScreenshotsFolderTooltip), EditorStyles.miniButton, GUILayout.Width(60f)))
            {
                OnClearScreenshotsFolderClicked();
            }
            EditorGUILayout.EndHorizontal();

            // Deployment source row
            GUILayout.Label("Package Source:", EditorStyles.miniLabel, GUILayout.Height(18f));
            EditorGUILayout.BeginHorizontal();
            string newDeploySource = EditorGUILayout.TextField(new GUIContent(deploySourcePath, DeploySourcePathTooltip), GUILayout.MinWidth(120f));
            if (newDeploySource != deploySourcePath)
            {
                deploySourcePath = newDeploySource;
                string path = newDeploySource != null ? newDeploySource.Trim() : null;
                bool shouldApply = !string.IsNullOrEmpty(path) && path != "Not set";
                if (shouldApply)
                {
                    try
                    {
                        MCPServiceLocator.Deployment.SetStoredSourcePath(path);
                    }
                    catch (Exception ex)
                    {
                        EditorUtility.DisplayDialog("Invalid Source", ex.Message, "OK");
                        UpdateDeploymentSection();
                    }
                }
            }
            if (GUILayout.Button(new GUIContent("Select", BrowseDeploySourceTooltip), EditorStyles.miniButton, GUILayout.Width(60f)))
            {
                OnBrowseDeploySourceClicked();
            }
            if (GUILayout.Button(new GUIContent("Clear", ClearDeploySourceTooltip), EditorStyles.miniButton, GUILayout.Width(60f)))
            {
                OnClearDeploySourceClicked();
            }
            EditorGUILayout.EndHorizontal();

            GUILayout.Label(deployTargetText, EditorStyles.miniLabel);
            GUILayout.Label(deployBackupText, EditorStyles.miniLabel);

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button(new GUIContent("Deploy", DeployButtonTooltip), EditorStyles.miniButton, GUILayout.Width(80f)))
            {
                OnDeployClicked();
            }
            using (new EditorGUI.DisabledScope(!deployRestoreEnabled))
            {
                if (GUILayout.Button(new GUIContent("Restore", DeployRestoreButtonTooltip), EditorStyles.miniButton, GUILayout.Width(80f)))
                {
                    OnRestoreBackupClicked();
                }
            }
            EditorGUILayout.EndHorizontal();

            if (deployStatusIsError)
            {
                Color previousColor = GUI.color;
                GUI.color = DeployErrorColor;
                GUILayout.Label(deployStatusText, EditorStyles.miniLabel);
                GUI.color = previousColor;
            }
            else
            {
                GUILayout.Label(deployStatusText, EditorStyles.miniLabel);
            }
        }

        public void UpdatePathOverrides()
        {
            var pathService = MCPServiceLocator.Paths;

            bool hasOverride = pathService.HasUvxPathOverride;
            bool hasFallback = pathService.HasUvxPathFallback;
            string uvxPath = hasOverride ? pathService.GetUvxPath() : null;

            // Determine display text based on override and fallback status
            if (hasOverride)
            {
                if (hasFallback)
                {
                    // Override path invalid, using system fallback
                    string overridePath = EditorPrefs.GetString(EditorPrefKeys.UvxPathOverride, string.Empty);
                    uvxPathDisplay = $"Invalid override path: {overridePath} (fallback to uvx path) {uvxPath}";
                }
                else if (!string.IsNullOrEmpty(uvxPath))
                {
                    // Override path valid
                    uvxPathDisplay = uvxPath;
                }
                else
                {
                    // Override set but invalid, no fallback available
                    string overridePath = EditorPrefs.GetString(EditorPrefKeys.UvxPathOverride, string.Empty);
                    uvxPathDisplay = $"Invalid override path: {overridePath}, no uv found";
                }
            }
            else
            {
                uvxPathDisplay = "uvx (uses PATH)";
            }

            if (hasOverride)
            {
                if (hasFallback)
                {
                    // Using fallback - show as warning (yellow)
                    uvxPathStatus = UvxStatusKind.Warning;
                }
                else
                {
                    // Override mode: validate the override path
                    string overridePath = EditorPrefs.GetString(EditorPrefKeys.UvxPathOverride, string.Empty);
                    if (pathService.TryValidateUvxExecutable(overridePath, out _))
                    {
                        uvxPathStatus = UvxStatusKind.Valid;
                    }
                    else
                    {
                        uvxPathStatus = UvxStatusKind.Invalid;
                    }
                }
            }
            else
            {
                // PATH mode: validate system uvx
                string systemUvxPath = pathService.GetUvxPath();
                if (!string.IsNullOrEmpty(systemUvxPath) && pathService.TryValidateUvxExecutable(systemUvxPath, out _))
                {
                    uvxPathStatus = UvxStatusKind.Valid;
                }
                else
                {
                    uvxPathStatus = UvxStatusKind.Invalid;
                }
            }

            gitUrl = EditorPrefs.GetString(EditorPrefKeys.GitUrlOverride, "");
            autoStartOnLoad = EditorPrefs.GetBool(EditorPrefKeys.AutoStartOnLoad, false);
            debugLogs = EditorPrefs.GetBool(EditorPrefKeys.DebugLogs, false);
            logRecord = McpLogRecord.IsEnabled;
            devModeForceRefresh = EditorPrefs.GetBool(EditorPrefKeys.DevModeForceServerRefresh, false);
            allowLanHttpBind = EditorPrefs.GetBool(EditorPrefKeys.AllowLanHttpBind, false);
            allowInsecureRemoteHttp = EditorPrefs.GetBool(EditorPrefKeys.AllowInsecureRemoteHttp, false);
            UpdateDeploymentSection();
        }

        private void OnBrowseUvxClicked()
        {
            string suggested = RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
                ? "/opt/homebrew/bin"
                : Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string picked = EditorUtility.OpenFilePanel("Select uv Executable", suggested, "");
            if (!string.IsNullOrEmpty(picked))
            {
                try
                {
                    MCPServiceLocator.Paths.SetUvxPathOverride(picked);
                    UpdatePathOverrides();
                    McpLog.Info($"uv path override set to: {picked}");
                }
                catch (Exception ex)
                {
                    EditorUtility.DisplayDialog("Invalid Path", ex.Message, "OK");
                }
            }
        }

        private void OnClearUvxClicked()
        {
            MCPServiceLocator.Paths.ClearUvxPathOverride();
            UpdatePathOverrides();
            McpLog.Info("uv path override cleared");
        }

        private void OnBrowseGitUrlClicked()
        {
            string picked = EditorUtility.OpenFolderPanel("Select Server folder (containing pyproject.toml)", string.Empty, string.Empty);
            if (!string.IsNullOrEmpty(picked))
            {
                picked = ResolveServerPath(picked);
                gitUrl = picked;
                EditorPrefs.SetString(EditorPrefKeys.GitUrlOverride, picked);
                OnGitUrlChanged?.Invoke();
                OnHttpServerCommandUpdateRequested?.Invoke();
                McpLog.Info($"Server source override set to: {picked}");
            }
        }

        private void OnClearGitUrlClicked()
        {
            gitUrl = string.Empty;
            EditorPrefs.DeleteKey(EditorPrefKeys.GitUrlOverride);
            OnGitUrlChanged?.Invoke();
            OnHttpServerCommandUpdateRequested?.Invoke();
        }

        /// <summary>
        /// Validates and auto-corrects a local server path to ensure it points to the directory
        /// containing pyproject.toml (the Python package root). If the user selects a parent
        /// directory (e.g. the repo root), this checks for a "Server" subdirectory with
        /// pyproject.toml and returns that instead.
        /// </summary>
        private static string ResolveServerPath(string path)
        {
            if (string.IsNullOrEmpty(path))
                return path;

            // If path is not a local filesystem path, return as-is (git URLs, PyPI refs, etc.)
            if (path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("git+", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase))
            {
                return path;
            }

            // Strip file:// prefix for filesystem checks, but preserve it for the return value
            string checkPath = path;
            string prefix = string.Empty;
            if (checkPath.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            {
                prefix = "file://";
                checkPath = checkPath.Substring(7);
            }

            // Already points to a directory with pyproject.toml — correct path
            if (File.Exists(Path.Combine(checkPath, "pyproject.toml")))
            {
                return path;
            }

            // Check if "Server" subdirectory contains pyproject.toml (common repo structure)
            string serverSubDir = Path.Combine(checkPath, "Server");
            if (File.Exists(Path.Combine(serverSubDir, "pyproject.toml")))
            {
                string corrected = prefix + serverSubDir;
                McpLog.Info($"Auto-corrected server path to 'Server' subdirectory: {corrected}");
                return corrected;
            }

            // Return as-is; uvx will report the error if the path is invalid
            return path;
        }

        private void UpdateDeploymentSection()
        {
            var deployService = MCPServiceLocator.Deployment;

            string sourcePath = deployService.GetStoredSourcePath();
            deploySourcePath = sourcePath ?? string.Empty;

            deployTargetText = $"Target: {deployService.GetTargetDisplayPath()}";

            string backupPath = deployService.GetLastBackupPath();
            if (deployService.HasBackup())
            {
                // Use forward slashes to avoid backslash escape sequence issues in UI text
                deployBackupText = $"Last backup: {backupPath?.Replace('\\', '/')}";
            }
            else
            {
                deployBackupText = "Last backup: none";
            }

            deployRestoreEnabled = deployService.HasBackup();
        }

        private void OnBrowseScreenshotsFolderClicked()
        {
            // Start the picker at the project's Assets/ since that's the most common target.
            string startDir = UnityEngine.Application.dataPath;
            string picked = EditorUtility.OpenFolderPanel("Select Screenshots Folder (inside this project)", startDir, string.Empty);
            if (string.IsNullOrEmpty(picked))
            {
                return;
            }

            string projectRoot = Path.GetFullPath(Path.Combine(UnityEngine.Application.dataPath, "..")).Replace('\\', '/');
            string normalizedRoot = projectRoot.EndsWith("/") ? projectRoot : projectRoot + "/";
            string normalizedPicked = picked.Replace('\\', '/');

            if (normalizedPicked.Equals(projectRoot, StringComparison.OrdinalIgnoreCase))
            {
                // Storing "" would wipe the EditorPrefs key (= "unset"), so reject the project
                // root rather than silently revert the override the user just chose.
                EditorUtility.DisplayDialog(
                    "Pick a Subfolder",
                    "Please pick a subfolder of the project (for example 'Assets/Screenshots' or 'Captures'). " +
                    "Selecting the project root would mix screenshots in with your project files.",
                    "OK");
                return;
            }

            if (!normalizedPicked.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
            {
                EditorUtility.DisplayDialog(
                    "Folder Outside Project",
                    $"The selected folder is outside the Unity project root.\n\nPicked: {normalizedPicked}\nProject: {projectRoot}\n\nPlease pick a folder inside the project.",
                    "OK");
                return;
            }

            string projectRelative = normalizedPicked.Substring(normalizedRoot.Length);

            ScreenshotPreferences.DefaultFolder = projectRelative;
            screenshotsFolder = projectRelative;
            McpLog.Info($"Default screenshots folder set to '{projectRelative}'.");
        }

        private void OnClearScreenshotsFolderClicked()
        {
            ScreenshotPreferences.DefaultFolder = string.Empty;
            screenshotsFolder = string.Empty;
        }

        private void OnBrowseDeploySourceClicked()
        {
            string picked = EditorUtility.OpenFolderPanel("Select MCPForUnity folder", string.Empty, string.Empty);
            if (string.IsNullOrEmpty(picked))
            {
                return;
            }

            try
            {
                MCPServiceLocator.Deployment.SetStoredSourcePath(picked);
                SetDeployStatus($"Source set: {picked}");
            }
            catch (Exception ex)
            {
                EditorUtility.DisplayDialog("Invalid Source", ex.Message, "OK");
                SetDeployStatus("Source selection failed");
            }

            UpdateDeploymentSection();
        }

        private void OnClearDeploySourceClicked()
        {
            MCPServiceLocator.Deployment.ClearStoredSourcePath();
            UpdateDeploymentSection();
            SetDeployStatus("Source cleared");
        }

        private void OnDeployClicked()
        {
            var result = MCPServiceLocator.Deployment.DeployFromStoredSource();
            SetDeployStatus(result.Message, !result.Success);

            if (!result.Success)
            {
                EditorUtility.DisplayDialog("Deployment Failed", result.Message, "OK");
            }
            else
            {
                EditorUtility.DisplayDialog("Deployment Complete", result.Message + (string.IsNullOrEmpty(result.BackupPath) ? string.Empty : $"\nBackup: {result.BackupPath}"), "OK");
                OnPackageDeployed?.Invoke();
            }

            UpdateDeploymentSection();
        }

        private void OnRestoreBackupClicked()
        {
            var result = MCPServiceLocator.Deployment.RestoreLastBackup();
            SetDeployStatus(result.Message, !result.Success);

            if (!result.Success)
            {
                EditorUtility.DisplayDialog("Restore Failed", result.Message, "OK");
            }
            else
            {
                EditorUtility.DisplayDialog("Restore Complete", result.Message, "OK");
                OnPackageDeployed?.Invoke();
            }

            UpdateDeploymentSection();
        }

        private void SetDeployStatus(string message, bool isError = false)
        {
            deployStatusText = message;
            deployStatusIsError = isError;
        }

        public void UpdateHealthStatus(bool isHealthy, string statusText)
        {
            healthStatusText = statusText;

            if (isHealthy)
            {
                healthStatusKind = HealthKind.Healthy;
            }
            else if (statusText == HealthStatus.Unknown)
            {
                healthStatusKind = HealthKind.Unknown;
            }
            else
            {
                healthStatusKind = HealthKind.Disconnected;
            }
        }

        private Color GetUvxStatusColor()
        {
            switch (uvxPathStatus)
            {
                case UvxStatusKind.Valid:
                    return ValidColor;
                case UvxStatusKind.Warning:
                    return WarningColor;
                default:
                    return InvalidColor;
            }
        }

        private Color GetHealthColor()
        {
            switch (healthStatusKind)
            {
                case HealthKind.Healthy:
                    return ValidColor;
                case HealthKind.Unknown:
                    return UnknownColor;
                default:
                    return InvalidColor;
            }
        }

        private static void DrawStatusDot(Color color)
        {
            Rect rect = GUILayoutUtility.GetRect(10f, 10f, GUILayout.Width(10f), GUILayout.Height(10f));
            Color previousColor = GUI.color;
            GUI.color = color;
            GUI.DrawTexture(rect, GetDotTexture(Color.white));
            GUI.color = previousColor;
        }

        private static Texture2D GetDotTexture(Color color)
        {
            Texture2D texture;
            if (!DotTextureCache.TryGetValue(color, out texture) || texture == null)
            {
                texture = new Texture2D(8, 8, TextureFormat.RGBA32, false);
                texture.hideFlags = HideFlags.HideAndDontSave;
                texture.wrapMode = TextureWrapMode.Clamp;
                Color[] pixels = new Color[64];
                for (int i = 0; i < pixels.Length; i++)
                {
                    pixels[i] = color;
                }
                texture.SetPixels(pixels);
                texture.Apply();
                DotTextureCache[color] = texture;
            }
            return texture;
        }
    }
}
