using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MCPForUnity.Editor.Constants;
using MCPForUnity.Editor.Helpers;
using MCPForUnity.Editor.Services;
using MCPForUnity.Editor.Windows.Components.Advanced;
using MCPForUnity.Editor.Windows.Components.AssetGen;
using MCPForUnity.Editor.Windows.Components.ClientConfig;
using MCPForUnity.Editor.Windows.Components.Connection;
using MCPForUnity.Editor.Windows.Components.Resources;
using MCPForUnity.Editor.Windows.Components.Tools;
using MCPForUnity.Editor.Setup;
using MCPForUnity.Editor.Windows.Components.Validation;
using UnityEditor;
using UnityEngine;

namespace MCPForUnity.Editor.Windows
{
    /// <summary>
    /// Main editor window. Drawn entirely with IMGUI (EditorGUILayout/GUILayout) so a single
    /// code path covers every supported Unity version (2019.4 LTS and later).
    /// </summary>
    public class MCPForUnityEditorWindow : EditorWindow
    {
        // Section controllers
        private McpConnectionSection connectionSection;
        private McpClientConfigSection clientConfigSection;
        private McpAdvancedSection advancedSection;
        private McpToolsSection toolsSection;
        private McpResourcesSection resourcesSection;
        private McpAssetGenSection assetGenSection;
        private McpValidationSection validationSection;

        // Header state
        private string versionLabelText = "";
        private string versionLabelTooltip = "";
        private string updateNotificationText = "";
        private bool updateNotificationVisible = false;

        // Tabs
        private static readonly string[] TabNames = { "Connect", "Tools", "Resources", "Generative", "Deps", "Advanced" };
        private static readonly ActivePanel[] TabPanels =
        {
            ActivePanel.Clients,
            ActivePanel.Tools,
            ActivePanel.Resources,
            ActivePanel.AssetGen,
            ActivePanel.Deps,
            ActivePanel.Advanced
        };

        private int activeTabIndex = 0;
        private Vector2 panelScroll;

        private static readonly HashSet<MCPForUnityEditorWindow> OpenWindows = new HashSet<MCPForUnityEditorWindow>();
        private bool toolsLoaded = false;
        private bool resourcesLoaded = false;
        private double lastRefreshTime = 0;
        private const double RefreshDebounceSeconds = 0.5;
        private bool updateCheckQueued = false;
        private bool updateCheckInFlight = false;

        private enum ActivePanel
        {
            Clients,
            Deps,
            Advanced,
            Tools,
            Resources,
            AssetGen
        }

        internal static void CloseAllWindows()
        {
            var windows = OpenWindows.Where(window => window != null).ToArray();
            foreach (var window in windows)
            {
                window.Close();
            }
        }

        public static void ShowWindow()
        {
            var existingWindows = UnityEngine.Resources.FindObjectsOfTypeAll<MCPForUnityEditorWindow>();
            MCPForUnityEditorWindow window = null;

            if (existingWindows.Length > 0)
            {
                window = existingWindows[0];

                // If multiple instances exist, keep one and close the extras to avoid stale hidden tabs.
                for (int i = 1; i < existingWindows.Length; i++)
                {
                    try
                    {
                        existingWindows[i].Close();
                    }
                    catch (Exception ex)
                    {
                        McpLog.Warn($"Error closing duplicate MCP window: {ex.Message}");
                    }
                }
            }
            else
            {
                window = GetWindow<MCPForUnityEditorWindow>(ProductInfo.ProductName);
            }

            window.titleContent = new GUIContent(ProductInfo.ProductName);
            window.minSize = new Vector2(500, 340);

            if (window.position.width < 100 || window.position.height < 100)
            {
                window.position = new Rect(120, 120, 900, 700);
            }

            window.Show();
            window.ShowTab();
            window.Focus();
        }

        // Helper to check and manage open windows from other classes
        public static bool HasAnyOpenWindow()
        {
            return OpenWindows.Count > 0;
        }

        public static void CloseAllOpenWindows()
        {
            if (OpenWindows.Count == 0)
                return;

            // Copy to array to avoid modifying the collection while iterating
            var arr = new MCPForUnityEditorWindow[OpenWindows.Count];
            OpenWindows.CopyTo(arr);
            foreach (var window in arr)
            {
                try
                {
                    window?.Close();
                }
                catch (Exception ex)
                {
                    McpLog.Warn($"Error closing MCP window: {ex.Message}");
                }
            }
        }

        private void OnEnable()
        {
            EditorApplication.update += OnEditorUpdate;
            OpenWindows.Add(this);

            // Section controllers are pure IMGUI state holders — no tree to build.
            connectionSection = new McpConnectionSection();
            clientConfigSection = new McpClientConfigSection();
            advancedSection = new McpAdvancedSection();
            toolsSection = new McpToolsSection();
            resourcesSection = new McpResourcesSection();
            assetGenSection = new McpAssetGenSection();
            validationSection = new McpValidationSection();

            // Wire up events between sections (same wiring as the previous UI Toolkit build)
            connectionSection.OnManualConfigUpdateRequested += () =>
                clientConfigSection?.UpdateManualConfiguration();
            connectionSection.OnTransportChanged += () =>
                clientConfigSection?.RefreshSelectedClient(forceImmediate: true);

            clientConfigSection.OnClientTransportDetected += (clientName, transport) =>
                connectionSection?.UpdateTransportMismatchWarning(clientName, transport);
            clientConfigSection.OnClientConfigMismatch += (clientName, mismatchMessage) =>
                connectionSection?.UpdateVersionMismatchWarning(clientName, mismatchMessage);

            advancedSection.OnGitUrlChanged += () =>
                clientConfigSection?.UpdateManualConfiguration();
            advancedSection.OnHttpServerCommandUpdateRequested += () =>
            {
                connectionSection?.UpdateHttpServerCommandDisplay();
                connectionSection?.UpdateConnectionStatus();
            };
            advancedSection.OnTestConnectionRequested += async () =>
            {
                if (connectionSection != null)
                    await connectionSection.VerifyBridgeConnectionAsync();
            };
            advancedSection.OnPackageDeployed += () =>
            {
                UpdateVersionLabel();
                QueueUpdateCheck();
            };
            // Wire up health status updates from Connection to Advanced
            connectionSection.SetHealthStatusUpdateCallback((isHealthy, statusText) =>
                advancedSection?.UpdateHealthStatus(isHealthy, statusText));

            UpdateVersionLabel();

            // Restore the saved tab (migrate old "Validation" value to "Deps")
            string savedPanel = EditorPrefs.GetString(EditorPrefKeys.EditorWindowActivePanel, ActivePanel.Clients.ToString());
            if (savedPanel == "Validation") savedPanel = "Deps";
            ActivePanel initialPanel;
            if (!Enum.TryParse(savedPanel, out initialPanel))
            {
                initialPanel = ActivePanel.Clients;
            }
            SwitchPanel(initialPanel);

            RefreshAllData();
            QueueUpdateCheck();
        }

        private void OnDisable()
        {
            EditorApplication.update -= OnEditorUpdate;
            OpenWindows.Remove(this);
            toolsLoaded = false;
            resourcesLoaded = false;
        }

        private void UpdateVersionLabel()
        {
            string version = AssetPathUtility.GetPackageVersion();
            versionLabelText = $"v{version}";
            versionLabelTooltip = AssetPathUtility.IsPreReleaseVersion()
                ? $"{ProductInfo.ProductName} v{version} (pre-release package, using prerelease server channel)"
                : $"{ProductInfo.ProductName} v{version}";
        }

        private void QueueUpdateCheck()
        {
            if (updateCheckQueued || updateCheckInFlight)
            {
                return;
            }

            updateCheckQueued = true;
            EditorApplication.delayCall += CheckForPackageUpdates;
        }

        private void CheckForPackageUpdates()
        {
            updateCheckQueued = false;

            string currentVersion = AssetPathUtility.GetPackageVersion();
            if (string.IsNullOrEmpty(currentVersion) || currentVersion == "unknown")
            {
                updateNotificationVisible = false;
                return;
            }

            // Main thread: resolve service + read EditorPrefs cache (both require main thread)
            var updateService = MCPServiceLocator.Updates;
            var cachedResult = updateService.TryGetCachedResult(currentVersion);
            if (cachedResult != null)
            {
                ApplyUpdateCheckResult(cachedResult, currentVersion);
                return;
            }

            // Main thread: pre-compute installation info (uses main-thread-only Unity APIs)
            bool isGitInstallation = updateService.IsGitInstallation();
            string gitBranch = isGitInstallation ? updateService.GetGitUpdateBranch(currentVersion) : "main";

            // Background thread: network I/O only (no EditorPrefs or Unity API access)
            updateCheckInFlight = true;
            Task.Run(() =>
            {
                try
                {
                    return updateService.FetchAndCompare(currentVersion, isGitInstallation, gitBranch);
                }
                catch (Exception ex)
                {
                    McpLog.Info($"Package update check skipped: {ex.Message}");
                    return null;
                }
            }).ContinueWith(t =>
            {
                EditorApplication.delayCall += () =>
                {
                    updateCheckInFlight = false;

                    // Main thread: cache the result in EditorPrefs
                    var result = t.Status == TaskStatus.RanToCompletion ? t.Result : null;
                    if (result != null && result.CheckSucceeded && !string.IsNullOrEmpty(result.LatestVersion))
                    {
                        updateService.CacheFetchResult(currentVersion, result.LatestVersion);
                    }

                    if (this == null)
                    {
                        return;
                    }

                    ApplyUpdateCheckResult(result, currentVersion);
                    Repaint();
                };
            }, TaskScheduler.Default);
        }

        private void ApplyUpdateCheckResult(UpdateCheckResult result, string currentVersion)
        {
            if (result != null && result.CheckSucceeded && result.UpdateAvailable && !string.IsNullOrEmpty(result.LatestVersion))
            {
                updateNotificationText = $"Update available: v{result.LatestVersion}  (current: v{currentVersion})";
                updateNotificationVisible = true;
            }
            else
            {
                updateNotificationVisible = false;
            }
        }

        private void EnsureToolsLoaded()
        {
            if (toolsLoaded)
            {
                return;
            }

            if (toolsSection == null)
            {
                return;
            }

            toolsLoaded = true;
            toolsSection.Refresh();
        }

        private void EnsureResourcesLoaded()
        {
            if (resourcesLoaded)
            {
                return;
            }

            if (resourcesSection == null)
            {
                return;
            }

            resourcesLoaded = true;
            resourcesSection.Refresh();
        }

        // Throttle OnEditorUpdate to avoid per-frame overhead (GitHub issue #577).
        // Connection status polling every frame caused expensive network checks 60+ times/sec.
        private double _lastEditorUpdateTime;
        private const double EditorUpdateIntervalSeconds = 2.0;

        private void OnEditorUpdate()
        {
            // Throttle to 2-second intervals instead of every frame.
            // This prevents the expensive IsLocalHttpServerReachable() socket checks from running
            // 60+ times per second, which caused main thread blocking and GC pressure.
            double now = EditorApplication.timeSinceStartup;
            if (now - _lastEditorUpdateTime < EditorUpdateIntervalSeconds)
            {
                return;
            }
            _lastEditorUpdateTime = now;

            if (connectionSection == null)
                return;

            connectionSection.UpdateConnectionStatus();
            Repaint();
        }

        private void OnGUI()
        {
            DrawHeader();
            DrawTabs();

            panelScroll = EditorGUILayout.BeginScrollView(panelScroll, GUILayout.ExpandHeight(true));
            switch (TabPanels[activeTabIndex])
            {
                case ActivePanel.Clients:
                    connectionSection?.Draw();
                    EditorGUILayout.Space(6);
                    clientConfigSection?.Draw();
                    break;
                case ActivePanel.Deps:
                    DrawDependenciesSection();
                    break;
                case ActivePanel.Advanced:
                    advancedSection?.Draw();
                    EditorGUILayout.Space(6);
                    validationSection?.Draw();
                    break;
                case ActivePanel.Tools:
                    EnsureToolsLoaded();
                    toolsSection?.Draw();
                    break;
                case ActivePanel.Resources:
                    EnsureResourcesLoaded();
                    resourcesSection?.Draw();
                    break;
                case ActivePanel.AssetGen:
                    assetGenSection?.Draw();
                    break;
            }
            EditorGUILayout.EndScrollView();
        }

        private void DrawHeader()
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("MCP for Unity", EditorStyles.boldLabel, GUILayout.ExpandWidth(false));
            GUILayout.FlexibleSpace();
            var versionContent = new GUIContent(versionLabelText, versionLabelTooltip);
            EditorGUILayout.LabelField(versionContent, EditorStyles.miniLabel, GUILayout.ExpandWidth(false));
            EditorGUILayout.EndHorizontal();

            if (updateNotificationVisible && !string.IsNullOrEmpty(updateNotificationText))
            {
                EditorGUILayout.HelpBox(updateNotificationText, MessageType.Info);
            }
            EditorGUILayout.Space(4);
        }

        private void DrawTabs()
        {
            int selected = GUILayout.Toolbar(activeTabIndex, TabNames, GUILayout.ExpandWidth(true));
            if (selected != activeTabIndex)
            {
                SwitchPanel(TabPanels[selected]);
            }
        }

        private void SwitchPanel(ActivePanel panel)
        {
            activeTabIndex = Array.IndexOf(TabPanels, panel);
            if (activeTabIndex < 0)
            {
                activeTabIndex = 0;
                panel = TabPanels[0];
            }

            // Per-tab refresh side effects (same semantics as the previous SwitchPanel)
            switch (panel)
            {
                case ActivePanel.Clients:
                    // Refresh client status when switching to Connect tab (e.g., after package/version changes).
                    clientConfigSection?.RefreshSelectedClient(forceImmediate: true);
                    break;
                case ActivePanel.Tools:
                    EnsureToolsLoaded();
                    break;
                case ActivePanel.Resources:
                    EnsureResourcesLoaded();
                    break;
                case ActivePanel.AssetGen:
                    assetGenSection?.Refresh();
                    break;
            }

            EditorPrefs.SetString(EditorPrefKeys.EditorWindowActivePanel, panel.ToString());
            Repaint();
        }

        private void OnFocus()
        {
            RefreshAllData();
        }

        internal static void RequestHealthVerification()
        {
            foreach (var window in OpenWindows)
            {
                window?.ScheduleHealthCheck();
            }
        }

        private void ScheduleHealthCheck()
        {
            EditorApplication.delayCall += async () =>
            {
                // Ensure window and components are still valid before execution
                if (this == null || connectionSection == null)
                {
                    return;
                }

                try
                {
                    await connectionSection.VerifyBridgeConnectionAsync();
                }
                catch (Exception ex)
                {
                    // Log but don't crash if verification fails during cleanup
                    McpLog.Warn($"Health check verification failed: {ex.Message}");
                }
            };
        }

        private void RefreshAllData()
        {
            // Debounce rapid successive calls (e.g., from OnFocus being called multiple times)
            double currentTime = EditorApplication.timeSinceStartup;
            if (currentTime - lastRefreshTime < RefreshDebounceSeconds)
            {
                return;
            }
            lastRefreshTime = currentTime;

            connectionSection?.UpdateConnectionStatus();

            if (MCPServiceLocator.Bridge.IsRunning)
            {
                _ = connectionSection?.VerifyBridgeConnectionAsync();
            }

            advancedSection?.UpdatePathOverrides();
            clientConfigSection?.RefreshSelectedClient();
        }

        // ---- Dependencies tab (IMGUI conversion of the previous VisualElement-based section) ----

        /// <summary>Mutable per-row button state for a dependency row.</summary>
        private class DependencyRow
        {
            internal string Name;
            internal string Description;
            internal bool IsInstalled;
            internal string InstalledText;
            internal string MissingText;
            internal Action<Action> InstallAction;   // done callback restores button state
            internal Action<Action> UninstallAction;
            internal bool InstallBusy;
            internal bool UninstallBusy;
        }

        private DependencyRow[] dependencyRows;
        private bool installAllBusy;
        private bool uninstallAllBusy;

        private static readonly string[] UpmPackages =
        {
            "com.unity.probuilder", "com.unity.cinemachine", "com.unity.visualeffectgraph", "com.unity.cloud.gltfast"
        };

        private void EnsureDependencyRows()
        {
            if (dependencyRows != null)
            {
                return;
            }

            // Roslyn — for execute_code modern C# support
            // Check if Roslyn types are actually loaded (covers NuGet, Plugins folder, etc.)
            bool roslynLoaded = Type.GetType("Microsoft.CodeAnalysis.CSharp.CSharpCompilation, Microsoft.CodeAnalysis.CSharp") != null;
            bool roslynInstalledLocally = RoslynInstaller.IsInstalled();
            dependencyRows = new[]
            {
                new DependencyRow
                {
                    Name = "Roslyn (C# 12+ Compiler)",
                    Description = "Enables modern C# syntax in execute_code tool (scripting_ext group).",
                    IsInstalled = roslynLoaded,
                    InstalledText = roslynInstalledLocally
                        ? "Installed via Plugins/Roslyn \u2014 execute_code uses Roslyn"
                        : "Available (loaded from NuGet/external) \u2014 execute_code uses Roslyn",
                    MissingText = "Not installed \u2014 execute_code falls back to C# 6 (CodeDom)",
                    InstallAction = done => { RoslynInstaller.Install(interactive: true); done?.Invoke(); },
                    UninstallAction = roslynInstalledLocally
                        ? (Action<Action>)(done => { UninstallRoslyn(); done?.Invoke(); })
                        : null
                },
                new DependencyRow
                {
                    Name = "ProBuilder",
                    Description = "Required for the manage_probuilder tool (probuilder group).",
                    IsInstalled = Type.GetType("UnityEngine.ProBuilder.ProBuilderMesh, Unity.ProBuilder") != null,
                    InstalledText = "Installed",
                    MissingText = "Not installed",
                    InstallAction = done => InstallUpmPackage("com.unity.probuilder", done),
                    UninstallAction = done => RemoveUpmPackage("com.unity.probuilder", done)
                },
                new DependencyRow
                {
                    Name = "Cinemachine",
                    Description = "Enhances manage_camera with virtual camera support (core group).",
                    IsInstalled = Type.GetType("Unity.Cinemachine.CinemachineCamera, Unity.Cinemachine") != null
                        || Type.GetType("Cinemachine.CinemachineVirtualCamera, Cinemachine") != null,
                    InstalledText = "Installed",
                    MissingText = "Not installed \u2014 camera tool works without it",
                    InstallAction = done => InstallUpmPackage("com.unity.cinemachine", done),
                    UninstallAction = done => RemoveUpmPackage("com.unity.cinemachine", done)
                },
                new DependencyRow
                {
                    Name = "VFX Graph",
                    Description = "Enables VisualEffect support in manage_vfx tool (vfx group).",
                    IsInstalled = IsUpmPackageInstalled("com.unity.visualeffectgraph"),
                    InstalledText = "Installed",
                    MissingText = "Not installed \u2014 VFX tool falls back to ParticleSystem/LineRenderer",
                    InstallAction = done => InstallUpmPackage("com.unity.visualeffectgraph", done),
                    UninstallAction = done => RemoveUpmPackage("com.unity.visualeffectgraph", done)
                },
                new DependencyRow
                {
                    Name = "glTFast (glTF/GLB import)",
                    Description = "Enables .glb/.gltf model import for the AI Asset Generation tools (asset_gen group).",
                    IsInstalled = IsUpmPackageInstalled("com.unity.cloud.gltfast") || Type.GetType("GLTFast.GltfImport, glTFast") != null,
                    InstalledText = "Installed — GLB generation/import works",
                    MissingText = "Not installed — GLB import is unavailable; FBX still works, or install to enable GLB",
                    InstallAction = done => InstallUpmPackage("com.unity.cloud.gltfast", done),
                    UninstallAction = done => RemoveUpmPackage("com.unity.cloud.gltfast", done)
                }
            };
        }

        private void DrawDependenciesSection()
        {
            EnsureDependencyRows();

            EditorGUILayout.LabelField("Optional Dependencies", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(
                "Some tool groups require optional packages. Install them to unlock additional capabilities.",
                EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.Space(4);

            // Install All / Uninstall All buttons
            EditorGUILayout.BeginHorizontal();
            using (new EditorGUI.DisabledScope(installAllBusy))
            {
                if (GUILayout.Button(installAllBusy ? "Installing..." : "Install All", GUILayout.Width(110)))
                {
                    if (EditorUtility.DisplayDialog("Install All Dependencies",
                        "This will install Roslyn DLLs, ProBuilder, Cinemachine, VFX Graph, and glTFast. Continue?",
                        "Install All", "Cancel"))
                    {
                        installAllBusy = true;
                        if (!RoslynInstaller.IsInstalled()) RoslynInstaller.Install(interactive: false);
                        BatchUpmAdd(UpmPackages, () =>
                        {
                            installAllBusy = false;
                            Repaint();
                        });
                    }
                }
            }
            using (new EditorGUI.DisabledScope(uninstallAllBusy))
            {
                if (GUILayout.Button(uninstallAllBusy ? "Removing..." : "Uninstall All", GUILayout.Width(110)))
                {
                    if (EditorUtility.DisplayDialog("Uninstall All Dependencies",
                        "This will remove Roslyn DLLs, ProBuilder, Cinemachine, VFX Graph, and glTFast. Continue?",
                        "Uninstall All", "Cancel"))
                    {
                        uninstallAllBusy = true;
                        UninstallRoslyn();
                        BatchUpmRemove(UpmPackages, () =>
                        {
                            uninstallAllBusy = false;
                            Repaint();
                        });
                    }
                }
            }
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.Space(6);

            foreach (var row in dependencyRows)
            {
                DrawDependencyRow(row);
                EditorGUILayout.Space(4);
            }
        }

        private void DrawDependencyRow(DependencyRow row)
        {
            Rect rowRect = EditorGUILayout.BeginVertical(GUI.skin.box);

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(row.Name, EditorStyles.boldLabel, GUILayout.ExpandWidth(false));
            GUILayout.FlexibleSpace();
            var statusContent = new GUIContent(row.IsInstalled ? "\u2713" : "\u2717");
            Color statusColor = row.IsInstalled ? new Color(0.4f, 0.8f, 0.4f) : new Color(0.8f, 0.4f, 0.4f);
            var oldColor = GUI.color;
            GUI.color = statusColor;
            EditorGUILayout.LabelField(statusContent, GUILayout.Width(16));
            GUI.color = oldColor;
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.LabelField(row.Description, EditorStyles.wordWrappedMiniLabel);

            var statusText = row.IsInstalled ? row.InstalledText : row.MissingText;
            var detailStyle = new GUIStyle(EditorStyles.miniLabel);
            detailStyle.normal.textColor = row.IsInstalled
                ? new Color(0.6f, 0.8f, 0.6f)
                : new Color(0.8f, 0.7f, 0.5f);
            EditorGUILayout.LabelField(statusText, detailStyle);

            bool installAvailable = !row.IsInstalled && row.InstallAction != null;
            bool uninstallAvailable = row.IsInstalled && row.UninstallAction != null;
            if (installAvailable || uninstallAvailable)
            {
                EditorGUILayout.BeginHorizontal();
                if (installAvailable)
                {
                    using (new EditorGUI.DisabledScope(row.InstallBusy))
                    {
                        if (GUILayout.Button(row.InstallBusy ? "Installing..." : "Install", GUILayout.Width(90)))
                        {
                            row.InstallBusy = true;
                            try
                            {
                                row.InstallAction(() =>
                                {
                                    row.InstallBusy = false;
                                    Repaint();
                                });
                            }
                            catch (Exception e)
                            {
                                Debug.LogError($"[MCP] Install failed: {e.Message}");
                                row.InstallBusy = false;
                            }
                        }
                    }
                }

                if (uninstallAvailable)
                {
                    using (new EditorGUI.DisabledScope(row.UninstallBusy))
                    {
                        if (GUILayout.Button(row.UninstallBusy ? "Removing..." : "Uninstall", GUILayout.Width(90)))
                        {
                            if (EditorUtility.DisplayDialog("Remove " + row.Name,
                                $"Are you sure you want to remove {row.Name}?", "Remove", "Cancel"))
                            {
                                row.UninstallBusy = true;
                                try
                                {
                                    row.UninstallAction(() =>
                                    {
                                        row.UninstallBusy = false;
                                        Repaint();
                                    });
                                }
                                catch (Exception e)
                                {
                                    Debug.LogError($"[MCP] Uninstall failed: {e.Message}");
                                    row.UninstallBusy = false;
                                }
                            }
                        }
                    }
                }
                EditorGUILayout.EndHorizontal();
            }

            EditorGUILayout.EndVertical();
            EditorGUI.DrawRect(new Rect(rowRect.x, rowRect.yMax - 1, rowRect.width, 1),
                new Color(0.3f, 0.3f, 0.3f, 0.3f));
        }

        private static void InstallUpmPackage(string packageId, Action onComplete = null)
        {
            BatchUpmAdd(new[] { packageId }, onComplete);
        }

        private static void RemoveUpmPackage(string packageId, Action onComplete = null)
        {
            BatchUpmRemove(new[] { packageId }, onComplete);
        }

        private static void BatchUpmAdd(string[] packageIds, Action onComplete = null)
        {
#if UNITY_2020_1_OR_NEWER
            var request = UnityEditor.PackageManager.Client.AddAndRemove(packageIds, null);
            EditorUtility.DisplayProgressBar("Installing Packages", $"Installing {packageIds.Length} package(s)...", 0.5f);
            PollUpmRequest(request, "install", onComplete);
#else
            BatchUpmAddLegacy(packageIds, 0, onComplete);
#endif
        }

        private static void BatchUpmRemove(string[] packageIds, Action onComplete = null)
        {
#if UNITY_2020_1_OR_NEWER
            var request = UnityEditor.PackageManager.Client.AddAndRemove(null, packageIds);
            EditorUtility.DisplayProgressBar("Removing Packages", $"Removing {packageIds.Length} package(s)...", 0.5f);
            PollUpmRequest(request, "remove", onComplete);
#else
            BatchUpmRemoveLegacy(packageIds, 0, onComplete);
#endif
        }

#if UNITY_2020_1_OR_NEWER
        private static void PollUpmRequest(UnityEditor.PackageManager.Requests.AddAndRemoveRequest request, string verb, Action onComplete)
        {
            EditorApplication.CallbackFunction pollCallback = null;
            pollCallback = () =>
            {
                if (!request.IsCompleted) return;
                EditorApplication.update -= pollCallback;
                EditorUtility.ClearProgressBar();
                if (request.Status == UnityEditor.PackageManager.StatusCode.Success)
                    Debug.Log($"[MCP] Package {verb} succeeded.");
                else
                    Debug.LogError($"[MCP] Package {verb} failed: {request.Error?.message}");
                onComplete?.Invoke();
            };
            EditorApplication.update += pollCallback;
        }

#endif

#if !UNITY_2020_1_OR_NEWER
        private static void BatchUpmAddLegacy(string[] packageIds, int index, Action onComplete)
        {
            if (index >= packageIds.Length)
            {
                onComplete?.Invoke();
                return;
            }
            EditorUtility.DisplayProgressBar("Installing Packages",
                $"Installing {packageIds[index]} ({index + 1}/{packageIds.Length})...", (float)index / packageIds.Length);
            var request = UnityEditor.PackageManager.Client.Add(packageIds[index]);
            EditorApplication.CallbackFunction poll = null;
            poll = () =>
            {
                if (!request.IsCompleted) return;
                EditorApplication.update -= poll;
                EditorUtility.ClearProgressBar();
                if (request.Status == UnityEditor.PackageManager.StatusCode.Success)
                    Debug.Log($"[MCP] Package {packageIds[index]} installed.");
                else
                    Debug.LogError($"[MCP] Package {packageIds[index]} failed: {request.Error?.message}");
                BatchUpmAddLegacy(packageIds, index + 1, onComplete);
            };
            EditorApplication.update += poll;
        }

        private static void BatchUpmRemoveLegacy(string[] packageIds, int index, Action onComplete)
        {
            if (index >= packageIds.Length)
            {
                onComplete?.Invoke();
                return;
            }
            EditorUtility.DisplayProgressBar("Removing Packages",
                $"Removing {packageIds[index]} ({index + 1}/{packageIds.Length})...", (float)index / packageIds.Length);
            var request = UnityEditor.PackageManager.Client.Remove(packageIds[index]);
            EditorApplication.CallbackFunction poll = null;
            poll = () =>
            {
                if (!request.IsCompleted) return;
                EditorApplication.update -= poll;
                EditorUtility.ClearProgressBar();
                if (request.Status == UnityEditor.PackageManager.StatusCode.Success)
                    Debug.Log($"[MCP] Package {packageIds[index]} removed.");
                else
                    Debug.LogError($"[MCP] Package {packageIds[index]} failed: {request.Error?.message}");
                BatchUpmRemoveLegacy(packageIds, index + 1, onComplete);
            };
            EditorApplication.update += poll;
        }
#endif

        private static void UninstallRoslyn()
        {
            string folder = System.IO.Path.Combine(Application.dataPath, "Plugins/Roslyn");
            if (System.IO.Directory.Exists(folder))
            {
                System.IO.Directory.Delete(folder, true);
                string metaPath = folder + ".meta";
                if (System.IO.File.Exists(metaPath))
                    System.IO.File.Delete(metaPath);
                AssetDatabase.Refresh();
                Debug.Log("[MCP] Roslyn DLLs removed from Assets/Plugins/Roslyn/");
            }
        }

        private static bool IsUpmPackageInstalled(string packageId)
        {
            // Check manifest.json directly — faster than async UPM API
            string manifestPath = System.IO.Path.Combine(Application.dataPath, "../Packages/manifest.json");
            if (!System.IO.File.Exists(manifestPath)) return false;
            string manifest = System.IO.File.ReadAllText(manifestPath);
            return manifest.Contains($"\"{packageId}\"");
        }
    }
}
