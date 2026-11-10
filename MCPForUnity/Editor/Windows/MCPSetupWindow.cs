using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using MCPForUnity.Editor.Clients;
using MCPForUnity.Editor.Dependencies;
using MCPForUnity.Editor.Dependencies.Models;
using MCPForUnity.Editor.Services;
using UnityEditor;
using UnityEngine;

namespace MCPForUnity.Editor.Windows
{
    /// <summary>
    /// Setup window for checking and guiding dependency installation
    /// </summary>
    public class MCPSetupWindow : EditorWindow
    {
        internal enum StatusKind
        {
            Valid,
            Invalid,
            Neutral
        }

        /// <summary>
        /// Rendered state for one dependency row (replaces the cached UI Toolkit labels).
        /// </summary>
        internal struct DependencyDisplay
        {
            public string Version;
            public string Details;
            public Color DetailsColor;
            public StatusKind Indicator;
        }

        // Dependency status state (drawn in OnGUI)
        private DependencyDisplay pythonDisplay;
        private DependencyDisplay uvDisplay;
        private DependencyDisplay gitDisplay;

        // Overall status message
        private string statusMessageText = "";
        private Color statusMessageColor = Color.gray;
        private GUIStyle statusMessageStyle;

        // Installation section visibility/content
        private bool showInstallationSection;
        private string installationInstructionsText = "";
        private bool showInstallUvButton;
        private bool installUvButtonEnabled = true;

        // Wizard step: dependency check vs. client configuration
        private bool clientsStepActive;
        private readonly List<(IMcpClientConfigurator client, bool selected)> clientToggles = new List<(IMcpClientConfigurator client, bool selected)>();
        private bool configureSelectedEnabled = true;

        private Vector2 scrollPosition;
        private Vector2 clientsScrollPosition;

        // Tracks an in-flight uv install so completion is handled on the main thread.
        private Task<UvInstaller.UvInstallResult> _uvInstallTask;

        private DependencyCheckResult _dependencyResult;

        public static void ShowWindow(DependencyCheckResult dependencyResult = null)
        {
            var window = GetWindow<MCPSetupWindow>("MCP Setup");
            window.minSize = new Vector2(480, 320);
            window._dependencyResult = dependencyResult ?? DependencyManager.CheckAllDependencies();
            window.Show();
        }

        private void OnEnable()
        {
            if (_dependencyResult == null)
            {
                _dependencyResult = DependencyManager.CheckAllDependencies();
            }
            UpdateUI();
            // Resume polling if a uv install was still in flight when the window was last disabled,
            // so its completion is still processed (button reset, dependencies re-checked).
            if (_uvInstallTask != null)
            {
                EditorApplication.update -= PollUvInstall;
                EditorApplication.update += PollUvInstall;
            }
        }

        private void OnDisable()
        {
            EditorApplication.update -= PollUvInstall;
        }

        private void OnGUI()
        {
            if (statusMessageStyle == null)
            {
                statusMessageStyle = new GUIStyle(EditorStyles.label)
                {
                    wordWrap = true
                };
            }

            scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition);

            EditorGUILayout.LabelField("MCP for Unity Setup", EditorStyles.boldLabel);

            if (!clientsStepActive)
            {
                DrawDependenciesStep();
            }
            else
            {
                DrawClientsStep();
            }

            EditorGUILayout.EndScrollView();
        }

        private void DrawDependenciesStep()
        {
            EditorGUILayout.LabelField("System Requirements", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(
                "MCP for Unity requires Python 3.10+ and UV package manager to function. Git is only needed to install or update the package from a Git URL.",
                EditorStyles.wordWrappedLabel);

            EditorGUILayout.Space();

            DrawDependencyRow("Python", pythonDisplay);
            DrawDependencyRow("UV Package Manager", uvDisplay);
            DrawDependencyRow("Git (optional)", gitDisplay);

            EditorGUILayout.Space();

            statusMessageStyle.normal.textColor = statusMessageColor;
            EditorGUILayout.LabelField(statusMessageText, statusMessageStyle);

            if (showInstallationSection)
            {
                EditorGUILayout.Space();
                EditorGUILayout.LabelField("Installation Instructions", EditorStyles.boldLabel);
                EditorGUILayout.LabelField(installationInstructionsText, EditorStyles.wordWrappedLabel);

                EditorGUILayout.BeginHorizontal();
                using (new EditorGUI.DisabledScope(!installUvButtonEnabled))
                {
                    string installUvLabel = installUvButtonEnabled ? "Install UV Automatically" : "Installing UV…";
                    if (GUILayout.Button(installUvLabel, GUILayout.ExpandWidth(false)))
                    {
                        OnInstallUvClicked();
                    }
                }
                if (GUILayout.Button("Open Python Install Page", GUILayout.ExpandWidth(false)))
                {
                    OnOpenPythonInstallClicked();
                }
                if (GUILayout.Button("Open UV Install Page", GUILayout.ExpandWidth(false)))
                {
                    OnOpenUvInstallClicked();
                }
                EditorGUILayout.EndHorizontal();
            }

            EditorGUILayout.Space();

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Refresh"))
            {
                OnRefreshClicked();
            }
            if (GUILayout.Button("Next"))
            {
                OnDoneClicked();
            }
            EditorGUILayout.EndHorizontal();
        }

        private static void DrawDependencyRow(string displayName, DependencyDisplay display)
        {
            Color indicatorColor;
            switch (display.Indicator)
            {
                case StatusKind.Valid:
                    indicatorColor = Color.green;
                    break;
                case StatusKind.Invalid:
                    indicatorColor = Color.red;
                    break;
                default:
                    indicatorColor = Color.gray;
                    break;
            }

            EditorGUILayout.BeginHorizontal();

            EditorGUILayout.LabelField(displayName, GUILayout.Width(150f));
            EditorGUILayout.LabelField(display.Version, GUILayout.Width(90f));

            // Small colored status dot (replaces the CSS-styled indicator element).
            Rect indicatorRect = GUILayoutUtility.GetRect(12f, 12f, GUILayout.Width(12f));
            EditorGUI.DrawRect(new Rect(indicatorRect.x + 2f, indicatorRect.y + 2f, 8f, 8f), indicatorColor);
            GUILayout.FlexibleSpace();

            EditorGUILayout.EndHorizontal();

            Color prevColor = GUI.color;
            GUI.color = display.DetailsColor;
            EditorGUILayout.LabelField(display.Details, EditorStyles.wordWrappedMiniLabel);
            GUI.color = prevColor;
        }

        private void DrawClientsStep()
        {
            EditorGUILayout.LabelField("Configure MCP Clients", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(
                "We found the following MCP clients on your machine. Select which to configure:",
                EditorStyles.wordWrappedLabel);

            EditorGUILayout.Space();

            clientsScrollPosition = EditorGUILayout.BeginScrollView(clientsScrollPosition, GUILayout.MaxHeight(240f));
            for (int i = 0; i < clientToggles.Count; i++)
            {
                var entry = clientToggles[i];
                bool selected = EditorGUILayout.Toggle(
                    new GUIContent(entry.client.DisplayName, entry.client.GetConfigPath()),
                    entry.selected);
                if (selected != entry.selected)
                {
                    clientToggles[i] = (entry.client, selected);
                }
            }
            EditorGUILayout.EndScrollView();

            if (clientToggles.Count == 0)
            {
                EditorGUILayout.LabelField(
                    "No supported MCP clients detected on this machine. You can configure clients later from Tools → MCP for Unity.",
                    EditorStyles.wordWrappedLabel);
            }

            EditorGUILayout.Space();

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Skip"))
            {
                OnSkipClientsClicked();
            }
            using (new EditorGUI.DisabledScope(!configureSelectedEnabled))
            {
                if (GUILayout.Button("Configure Selected"))
                {
                    OnConfigureSelectedClicked();
                }
            }
            EditorGUILayout.EndHorizontal();
        }

        private void OnRefreshClicked()
        {
            _dependencyResult = DependencyManager.CheckAllDependencies();
            UpdateUI();
        }

        private void OnDoneClicked()
        {
            if (_dependencyResult != null && _dependencyResult.IsSystemReady)
            {
                ShowClientsStep();
            }
            else
            {
                Setup.SetupWindowService.MarkSetupDismissed();
                Close();
            }
        }

        private void ShowClientsStep()
        {
            clientsStepActive = true;
            PopulateClientsList();
        }

        private void PopulateClientsList()
        {
            clientToggles.Clear();
            foreach (var c in McpClientRegistry.All)
            {
                if (!c.IsInstalled) continue;
                clientToggles.Add((c, true));
            }
            configureSelectedEnabled = clientToggles.Count > 0;
        }

        private void OnSkipClientsClicked()
        {
            Setup.SetupWindowService.MarkSetupCompleted();
            Close();
        }

        private void OnConfigureSelectedClicked()
        {
            int success = 0, failure = 0;
            var failures = new List<string>();
            foreach (var entry in clientToggles)
            {
                if (!entry.selected) continue;
                try
                {
                    MCPServiceLocator.Client.ConfigureClient(entry.client);
                    success++;
                }
                catch (System.Exception ex)
                {
                    failure++;
                    failures.Add($"⚠ {entry.client.DisplayName}: {ex.Message}");
                }
            }
            if (success == 0 && failure == 0)
            {
                EditorUtility.DisplayDialog(
                    "Client Configuration",
                    "No clients were selected. Tick at least one client to continue, or close the window to skip setup.",
                    "OK");
                return;
            }
            // Keep the summary short: a count, only the failures (if any), and the next step —
            // no need to enumerate every successfully-configured client.
            string failureList = failures.Count > 0 ? "\n\n" + string.Join("\n", failures) : "";
            string nextStep = (failure == 0 && success > 0)
                ? "\n\nYou're all set. Ask your AI assistant to create a GameObject in the open scene to confirm the connection."
                : "";
            EditorUtility.DisplayDialog(
                "Client Configuration",
                $"{success} configured, {failure} failed.{failureList}{nextStep}",
                "OK");
            Setup.SetupWindowService.MarkSetupCompleted();
            Close();
        }

        private void OnOpenPythonInstallClicked()
        {
            var (pythonUrl, _) = DependencyManager.GetInstallationUrls();
            Application.OpenURL(pythonUrl);
        }

        private void OnOpenUvInstallClicked()
        {
            var (_, uvUrl) = DependencyManager.GetInstallationUrls();
            Application.OpenURL(uvUrl);
        }

        private void OnInstallUvClicked()
        {
            if (_uvInstallTask != null) return; // already running

            bool proceed = EditorUtility.DisplayDialog(
                "Install UV",
                "This will download and run the official uv installer:\n\n" +
                UvInstaller.DescribeCommand() +
                "\n\nContinue?",
                "Install",
                "Cancel");
            if (!proceed) return;

            installUvButtonEnabled = false;
            statusMessageText = "Installing uv… this can take a moment.";
            statusMessageColor = new Color(1f, 0.6f, 0f);

            _uvInstallTask = Task.Run(() => UvInstaller.Run());
            EditorApplication.update += PollUvInstall;
        }

        private void PollUvInstall()
        {
            // The window may have been torn down while the task ran — stop polling and drop it
            // (guards against dereferencing window state after teardown).
            if (this == null)
            {
                EditorApplication.update -= PollUvInstall;
                return;
            }
            if (_uvInstallTask == null || !_uvInstallTask.IsCompleted) return;

            EditorApplication.update -= PollUvInstall;
            var task = _uvInstallTask;
            _uvInstallTask = null;

            installUvButtonEnabled = true;

            // UvInstaller.Run catches its own exceptions, so the task always completes with a result.
            UvInstaller.UvInstallResult result = task.Result;

            if (result.Success)
            {
                _dependencyResult = DependencyManager.CheckAllDependencies();
                UpdateUI();
                if (!_dependencyResult.IsSystemReady)
                {
                    EditorUtility.DisplayDialog(
                        "Install UV",
                        "uv installed, but it isn't visible on PATH yet. Restart Unity (or your terminal) so it picks up the new PATH, then click Refresh.\n\n" +
                        result.Output,
                        "OK");
                }
            }
            else
            {
                // Reset the status message off the "Installing…" state before reporting the failure.
                UpdateUI();
                EditorUtility.DisplayDialog(
                    "Install UV Failed",
                    "The installer did not complete successfully. You can install uv manually via \"Open UV Install Page\".\n\n" +
                    result.Output,
                    "OK");
            }

            Repaint();
        }

        private void UpdateUI()
        {
            if (_dependencyResult == null)
                return;

            // Update Python status
            var pythonDep = _dependencyResult.Dependencies.Find(d => d.Name == "Python");
            if (pythonDep != null)
            {
                pythonDisplay = BuildDependencyDisplay(pythonDep);
            }

            // Update uv status
            var uvDep = _dependencyResult.Dependencies.Find(d => d.Name == "uv Package Manager");
            if (uvDep != null)
            {
                uvDisplay = BuildDependencyDisplay(uvDep);
            }

            // Update git status (optional dependency: never blocks readiness)
            var gitDep = _dependencyResult.Dependencies.Find(d => d.Name == "Git");
            if (gitDep != null)
            {
                gitDisplay = BuildDependencyDisplay(gitDep);
            }

            // Offer the one-click uv installer only when uv is actually missing
            bool uvMissing = uvDep != null && !uvDep.IsAvailable;
            showInstallUvButton = uvMissing && UvInstaller.IsSupported;

            // Update overall status
            if (_dependencyResult.IsSystemReady)
            {
                statusMessageText = "✓ All requirements met! MCP for Unity is ready to use.";
                statusMessageColor = Color.green;
                showInstallationSection = false;
            }
            else
            {
                statusMessageText = "⚠ Missing dependencies. MCP for Unity requires all dependencies to function.";
                statusMessageColor = new Color(1f, 0.6f, 0f); // Orange
                showInstallationSection = true;
                installationInstructionsText = DependencyManager.GetInstallationRecommendations();
            }
        }

        /// <summary>
        /// Computes the rendered state for a dependency from its check result
        /// (replacement for the old class-list-based UpdateDependencyStatus).
        /// </summary>
        internal static DependencyDisplay BuildDependencyDisplay(DependencyStatus dep)
        {
            var display = new DependencyDisplay
            {
                Version = dep.Version,
                Details = dep.Details,
                DetailsColor = Color.gray,
                Indicator = StatusKind.Neutral
            };

            if (dep.IsAvailable)
            {
                display.Indicator = StatusKind.Valid;
                display.Version = $"v{dep.Version}";
                display.Details = dep.Details ?? "Available";
                display.DetailsColor = Color.gray;
            }
            else if (dep.IsRequired)
            {
                display.Indicator = StatusKind.Invalid;
                display.Version = "Not Found";
                display.Details = dep.ErrorMessage ?? "Not available";
                display.DetailsColor = Color.red;
            }
            else
            {
                // A missing optional dependency is information, not a blocker — keep the neutral
                // grey indicator and say what it is for.
                display.Indicator = StatusKind.Neutral;
                display.Version = "Not Found";
                display.Details = dep.Details ?? dep.ErrorMessage ?? "Not available";
                display.DetailsColor = Color.gray;
            }

            return display;
        }
    }
}
