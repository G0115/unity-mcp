using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MCPForUnity.Editor.Clients;
using MCPForUnity.Editor.Constants;
using MCPForUnity.Editor.Helpers;
using MCPForUnity.Editor.Models;
using MCPForUnity.Editor.Services;
using MCPForUnity.Editor.Services.Transport;
using MCPForUnity.Editor.Tools;
using UnityEditor;
using UnityEngine;

namespace MCPForUnity.Editor.Windows.Components.Tools
{
    /// <summary>
    /// Controller for the Tools section inside the MCP for Unity editor window.
    /// Provides discovery, filtering, and per-tool enablement toggles.
    /// Tools are grouped by their Group property (core first, then alphabetical).
    /// IMGUI implementation drawn via <see cref="Draw"/> in the main window's OnGUI.
    /// </summary>
    public class McpToolsSection
    {
        /// <summary>A tool group rendered as one foldout in Draw().</summary>
        private class CategoryEntry
        {
            public string Title;
            public string PrefsSuffix;
            public bool IsExperimental;
            public List<ToolMetadata> Tools;
            public bool FoldoutOpen;
        }

        private List<ToolMetadata> allTools = new List<ToolMetadata>();
        private readonly List<CategoryEntry> categories = new List<CategoryEntry>();
        private readonly List<string> infoMessages = new List<string>();
        private string summaryText = "Discovering tools...";
        private bool showNote;
        private string noteText = "Changes apply after reconnecting or re-registering tools.";
        private bool hasTools;
        private bool projectScopedTools;

        /// <summary>Human-friendly names for tool groups shown in the UI.</summary>
        private static readonly Dictionary<string, string> GroupDisplayNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "core", "Core Tools" },
            { "vfx", "VFX & Shaders" },
            { "animation", "Animation" },
            { "ui", "UI Toolkit" },
            { "scripting_ext", "Scripting Extensions" },
            { "testing", "Testing" },
            { "probuilder", "ProBuilder — Experimental" },
            { "profiling", "Profiling & Frame Debugger" },
            { "asset_gen", "Asset Gen" },
        };

        public McpToolsSection()
        {
            projectScopedTools = EditorPrefs.GetBool(
                EditorPrefKeys.ProjectScopedToolsLocalHttp,
                false
            );
        }

        /// <summary>
        /// Draws the Tools section. Call from the main window's OnGUI.
        /// Layout order follows McpToolsSection.uxml.
        /// </summary>
        public void Draw()
        {
            EditorGUILayout.LabelField("Tools", EditorStyles.boldLabel);

            // Project-scoped tools row
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Project-Scoped Tools:", GUILayout.Width(EditorGUIUtility.labelWidth));
            EditorGUI.BeginChangeCheck();
            projectScopedTools = EditorGUILayout.Toggle(
                new GUIContent(string.Empty, "When enabled, register project-scoped tools with HTTP Local and stdio transports. Allows per-project tool customization."),
                projectScopedTools
            );
            if (EditorGUI.EndChangeCheck())
            {
                EditorPrefs.SetBool(EditorPrefKeys.ProjectScopedToolsLocalHttp, projectScopedTools);
            }
            EditorGUILayout.EndHorizontal();

            // Summary
            EditorGUILayout.HelpBox(summaryText, MessageType.None);

            // Action buttons (row 1: Enable All / Disable All)
            EditorGUILayout.BeginHorizontal();
            using (new EditorGUI.DisabledScope(!hasTools))
            {
                if (GUILayout.Button("Enable All", GUILayout.Width(100)))
                {
                    SetAllToolsState(true);
                }
                if (GUILayout.Button("Disable All", GUILayout.Width(100)))
                {
                    SetAllToolsState(false);
                }
            }
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();

            // Action buttons (row 2: Rescan / Reconfigure Clients)
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Rescan", GUILayout.Width(100)))
            {
                McpLog.Info("Rescanning MCP tools from the editor window.");
                MCPServiceLocator.ToolDiscovery.InvalidateCache();
                Refresh();
            }
            if (GUILayout.Button("Reconfigure Clients", GUILayout.Width(160)))
            {
                OnReconfigureClientsClicked();
            }
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();

            // Note shown only when tools exist
            if (showNote)
            {
                EditorGUILayout.HelpBox(noteText, MessageType.Info);
            }

            // Info messages (e.g. "No MCP tools found.")
            foreach (string message in infoMessages)
            {
                EditorGUILayout.HelpBox(message, MessageType.None);
            }

            // Tool categories
            foreach (CategoryEntry category in categories)
            {
                DrawCategory(category);
            }
        }

        /// <summary>
        /// Rebuilds the tool list and synchronises toggle states.
        /// Tools are displayed in group-based foldouts: core first, then other
        /// groups alphabetically. Custom (non-built-in) tools appear in a
        /// separate "Custom Tools" foldout at the bottom.
        /// </summary>
        public void Refresh()
        {
            categories.Clear();
            infoMessages.Clear();

            var service = MCPServiceLocator.ToolDiscovery;
            allTools = service.DiscoverAllTools()
                .OrderBy(tool => tool.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            hasTools = allTools.Count > 0;
            showNote = hasTools;

            if (hasTools)
            {
                bool isHttp = EditorConfigurationCache.Instance.UseHttpTransport;
                noteText = isHttp
                    ? "Changes apply after reconnecting or re-registering tools."
                    : "Stdio mode: toggles sync at startup. After changing toggles, ask the AI to run manage_tools with action 'sync' to refresh.";
            }

            if (!hasTools)
            {
                infoMessages.Add("No MCP tools found. Add classes decorated with [McpForUnityTool] to expose tools.");
                UpdateSummary();
                return;
            }

            // Partition into built-in and custom
            var builtInTools = allTools.Where(IsBuiltIn).ToList();
            var customTools = allTools.Where(tool => !IsBuiltIn(tool)).ToList();

            // Group built-in tools by their Group property
            var grouped = builtInTools
                .GroupBy(t => t.Group ?? "core")
                .ToDictionary(g => g.Key, g => g.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList());

            // Render "core" first, then remaining groups alphabetically
            if (grouped.TryGetValue("core", out var coreTools))
            {
                BuildCategory(GetGroupDisplayName("core"), "group-core", coreTools);
                grouped.Remove("core");
            }

            foreach (var kvp in grouped.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            {
                BuildCategory(GetGroupDisplayName(kvp.Key), $"group-{kvp.Key}", kvp.Value);
            }

            // Custom tools at the bottom
            if (customTools.Count > 0)
            {
                BuildCategory("Custom Tools", "custom", customTools);
            }

            UpdateSummary();
        }

        private static string GetGroupDisplayName(string group)
        {
            if (GroupDisplayNames.TryGetValue(group, out var displayName))
                return displayName;
            // Fallback: capitalize first letter
            return string.IsNullOrEmpty(group)
                ? "Other"
                : char.ToUpper(group[0]) + group.Substring(1);
        }

        private void BuildCategory(string title, string prefsSuffix, IEnumerable<ToolMetadata> tools)
        {
            var toolList = tools.ToList();
            if (toolList.Count == 0)
            {
                return;
            }

            bool isExperimental = string.Equals(prefsSuffix, "group-probuilder", StringComparison.OrdinalIgnoreCase);

            // Default foldout state: core is open, others collapsed
            bool defaultOpen = prefsSuffix == "group-core";
            categories.Add(new CategoryEntry
            {
                Title = title,
                PrefsSuffix = prefsSuffix,
                IsExperimental = isExperimental,
                Tools = toolList,
                FoldoutOpen = EditorPrefs.GetBool(EditorPrefKeys.ToolFoldoutStatePrefix + prefsSuffix, defaultOpen)
            });
        }

        private void DrawCategory(CategoryEntry category)
        {
            // Header count is computed live so it stays accurate after toggles.
            int enabledCount = category.Tools.Count(t => MCPServiceLocator.ToolDiscovery.IsToolEnabled(t.Name));
            string header = $"{category.Title} ({enabledCount}/{category.Tools.Count})";

            EditorGUILayout.BeginHorizontal();
            EditorGUI.BeginChangeCheck();
            category.FoldoutOpen = EditorGUILayout.Foldout(category.FoldoutOpen, header, true);
            if (EditorGUI.EndChangeCheck())
            {
                EditorPrefs.SetBool(EditorPrefKeys.ToolFoldoutStatePrefix + category.PrefsSuffix, category.FoldoutOpen);
            }

            // Checkbox in the foldout header to toggle all tools in this group
            bool allEnabled = enabledCount == category.Tools.Count;
            EditorGUI.BeginChangeCheck();
            bool toggleAll = GUILayout.Toggle(
                allEnabled,
                new GUIContent(string.Empty, $"Toggle all tools in \"{category.Title}\" on or off."),
                GUILayout.Width(14)
            );
            if (EditorGUI.EndChangeCheck() && toggleAll != allEnabled)
            {
                SetGroupToolsState(category.Tools, toggleAll);
            }
            EditorGUILayout.EndHorizontal();

            if (!category.FoldoutOpen)
            {
                return;
            }

            EditorGUI.indentLevel++;
            if (category.IsExperimental)
            {
                EditorGUILayout.HelpBox(
                    "ProBuilder support is experimental. Mesh editing operations may produce " +
                    "unexpected results on complex topologies. Always save your scene before " +
                    "performing destructive operations.",
                    MessageType.Warning
                );
            }

            foreach (ToolMetadata tool in category.Tools)
            {
                DrawToolRow(tool);
            }
            EditorGUI.indentLevel--;
        }

        private void DrawToolRow(ToolMetadata tool)
        {
            bool isEnabled = MCPServiceLocator.ToolDiscovery.IsToolEnabled(tool.Name);

            string tooltip = string.IsNullOrWhiteSpace(tool.Description) ? tool.Name : tool.Description;

            EditorGUILayout.BeginHorizontal();
            EditorGUI.BeginChangeCheck();
            bool newEnabled = GUILayout.Toggle(isEnabled, new GUIContent(tool.Name, tooltip));

            if (EditorGUI.EndChangeCheck() && newEnabled != isEnabled)
            {
                HandleToggleChange(tool, newEnabled);
            }

            bool defaultEnabled = tool.AutoRegister || tool.IsBuiltIn;
            string tags = defaultEnabled ? "On by default" : "Off by default";
            tags += " | " + (tool.StructuredOutput ? "Structured output" : "Free-form");
            if (tool.RequiresPolling)
            {
                tags += $" | Polling: {tool.PollAction}";
            }

            GUILayout.FlexibleSpace();
            GUILayout.Label(tags, EditorStyles.miniLabel, GUILayout.ExpandWidth(false));
            EditorGUILayout.EndHorizontal();

            // Skip auto-generated placeholder descriptions like "Tool: find_gameobjects"
            if (!string.IsNullOrWhiteSpace(tool.Description)
                && !tool.Description.StartsWith("Tool: ", StringComparison.OrdinalIgnoreCase))
            {
                EditorGUILayout.LabelField(tool.Description, EditorStyles.miniLabel);
            }

            if (tool.Parameters != null && tool.Parameters.Count > 0)
            {
                string paramSummary = string.Join(", ", tool.Parameters.Select(p =>
                    $"{p.Name}{(p.Required ? string.Empty : " (optional)")}: {p.Type}"));
                EditorGUILayout.LabelField(paramSummary, EditorStyles.miniLabel);
            }

            if (IsManageCameraTool(tool))
            {
                DrawManageSceneActions();
            }

            if (IsBatchExecuteTool(tool))
            {
                DrawBatchExecuteSettings();
            }
        }

        // Math.Clamp is netstandard2.1; emulate for 2019.4
        private static int ClampInt(int v, int min, int max)
        {
            return v < min ? min : (v > max ? max : v);
        }

        private void HandleToggleChange(
            ToolMetadata tool,
            bool enabled,
            bool updateSummary = true,
            bool reregisterTools = true)
        {
            MCPServiceLocator.ToolDiscovery.SetToolEnabled(tool.Name, enabled);

            if (updateSummary)
            {
                UpdateSummary();
            }

            if (reregisterTools)
            {
                // Trigger tool reregistration with connected MCP server
                ReregisterToolsAsync();
            }
        }

        private void ReregisterToolsAsync()
        {
            // Fire and forget - don't block UI thread
            var transportManager = MCPServiceLocator.TransportManager;
            var client = transportManager.GetClient(TransportMode.Http);
            if (client == null || !client.IsConnected)
            {
                return;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    await client.ReregisterToolsAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    McpLog.Warn($"Failed to reregister tools: {ex}");
                }
            });
        }

        private void SetAllToolsState(bool enabled)
        {
            bool hasChanges = false;

            foreach (var tool in allTools)
            {
                if (MCPServiceLocator.ToolDiscovery.IsToolEnabled(tool.Name) != enabled)
                {
                    MCPServiceLocator.ToolDiscovery.SetToolEnabled(tool.Name, enabled);
                    hasChanges = true;
                }
            }

            UpdateSummary();

            if (hasChanges)
            {
                // Trigger a single reregistration after bulk change
                ReregisterToolsAsync();
            }
        }

        private void SetGroupToolsState(List<ToolMetadata> groupTools, bool enabled)
        {
            bool hasChanges = false;

            foreach (var tool in groupTools)
            {
                if (MCPServiceLocator.ToolDiscovery.IsToolEnabled(tool.Name) != enabled)
                {
                    MCPServiceLocator.ToolDiscovery.SetToolEnabled(tool.Name, enabled);
                    hasChanges = true;
                }
            }

            UpdateSummary();

            if (hasChanges)
            {
                ReregisterToolsAsync();
            }
        }

        private void OnReconfigureClientsClicked()
        {
            try
            {
                // Re-register tools with the server (HTTP mode)
                ReregisterToolsAsync();

                // Reconfigure all already-configured clients.
                // For CLI-based clients Configure() is a toggle (unregister if
                // configured, register if not), so we call it twice: first to
                // unregister, then to re-register with the updated tool set.
                var clients = MCPServiceLocator.Client.GetAllClients();
                int success = 0;
                int skipped = 0;
                var messages = new List<string>();

                foreach (var client in clients)
                {
                    try
                    {
                        client.CheckStatus(attemptAutoRewrite: false);

                        if (client.Status != McpStatus.Configured)
                        {
                            skipped++;
                            continue;
                        }

                        if (client is ClaudeCliMcpConfigurator)
                        {
                            // Toggle off (unregister), then toggle on (register)
                            MCPServiceLocator.Client.ConfigureClient(client);
                            MCPServiceLocator.Client.ConfigureClient(client);
                        }
                        else
                        {
                            // JSON-file clients: rewrite is idempotent
                            MCPServiceLocator.Client.ConfigureClient(client);
                        }

                        success++;
                        messages.Add($"✓ {client.DisplayName}: Reconfigured");
                    }
                    catch (Exception ex)
                    {
                        messages.Add($"⚠ {client.DisplayName}: {ex.Message}");
                    }
                }

                string header = $"Reconfigured {success} client(s), skipped {skipped}.";
                string body = messages.Count > 0
                    ? header + "\n\n" + string.Join("\n", messages)
                    : header;

                EditorUtility.DisplayDialog("Reconfigure Clients", body, "OK");
            }
            catch (Exception ex)
            {
                EditorUtility.DisplayDialog("Reconfigure Failed", ex.Message, "OK");
                McpLog.Error($"Reconfigure failed: {ex.Message}");
            }
        }

        private void UpdateSummary()
        {
            if (allTools.Count == 0)
            {
                summaryText = "No MCP tools discovered.";
                return;
            }

            int enabledCount = allTools.Count(tool => MCPServiceLocator.ToolDiscovery.IsToolEnabled(tool.Name));
            summaryText = $"{enabledCount} of {allTools.Count} tools will register with connected clients.";
        }

        private void DrawManageSceneActions()
        {
            EditorGUILayout.LabelField("Capture:", EditorStyles.miniLabel);
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button(
                new GUIContent("Game View", "Capture a game camera screenshot. Default: Assets/Screenshots (configurable in Advanced)."),
                GUILayout.Width(90)))
            {
                OnManageSceneScreenshotClicked();
            }
            if (GUILayout.Button(
                new GUIContent("Scene View", "Capture the active Scene View viewport. Default: Assets/Screenshots (configurable in Advanced)."),
                GUILayout.Width(90)))
            {
                OnSceneViewScreenshotClicked();
            }
            if (GUILayout.Button(
                new GUIContent("Multiview", "Capture a 6-angle contact sheet around the scene centre. Default: Assets/Screenshots (configurable in Advanced)."),
                GUILayout.Width(90)))
            {
                OnManageSceneMultiviewClicked();
            }
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
        }

        private void DrawBatchExecuteSettings()
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(
                new GUIContent("Max commands per batch:", $"Number of commands allowed per batch_execute call (1–{BatchExecute.AbsoluteMaxCommandsPerBatch}). Default: {BatchExecute.DefaultMaxCommandsPerBatch}."),
                GUILayout.Width(EditorGUIUtility.labelWidth)
            );

            int currentValue = EditorPrefs.GetInt(
                EditorPrefKeys.BatchExecuteMaxCommands,
                BatchExecute.DefaultMaxCommandsPerBatch
            );

            int clamped = ClampInt(currentValue, 1, BatchExecute.AbsoluteMaxCommandsPerBatch);
            EditorGUI.BeginChangeCheck();
            int newValue = EditorGUILayout.IntField(clamped, GUILayout.Width(60));
            if (EditorGUI.EndChangeCheck())
            {
                clamped = ClampInt(newValue, 1, BatchExecute.AbsoluteMaxCommandsPerBatch);
                EditorPrefs.SetInt(EditorPrefKeys.BatchExecuteMaxCommands, clamped);
            }

            GUILayout.Label($"(max {BatchExecute.AbsoluteMaxCommandsPerBatch})", EditorStyles.miniLabel, GUILayout.ExpandWidth(false));
            EditorGUILayout.EndHorizontal();
        }

        private void OnManageSceneScreenshotClicked()
        {
            try
            {
                var response = ManageScene.ExecuteScreenshot();
                if (response is SuccessResponse success && !string.IsNullOrWhiteSpace(success.Message))
                {
                    McpLog.Info(success.Message);
                }
                else if (response is ErrorResponse error && !string.IsNullOrWhiteSpace(error.Error))
                {
                    McpLog.Error(error.Error);
                }
                else
                {
                    McpLog.Info("Screenshot capture requested.");
                }
            }
            catch (Exception ex)
            {
                McpLog.Error($"Failed to capture screenshot: {ex.Message}");
            }
        }

        private void OnSceneViewScreenshotClicked()
        {
            try
            {
                var response = ManageScene.ExecuteSceneViewScreenshot();
                if (response is SuccessResponse success && !string.IsNullOrWhiteSpace(success.Message))
                {
                    McpLog.Info(success.Message);
                }
                else if (response is ErrorResponse error && !string.IsNullOrWhiteSpace(error.Error))
                {
                    McpLog.Error(error.Error);
                }
                else
                {
                    McpLog.Info("Scene View screenshot capture requested.");
                }
            }
            catch (Exception ex)
            {
                McpLog.Error($"Failed to capture Scene View screenshot: {ex.Message}");
            }
        }

        private void OnManageSceneMultiviewClicked()
        {
            try
            {
                var response = ManageScene.ExecuteMultiviewScreenshot();
                if (response is SuccessResponse success)
                {
                    // The data object is an anonymous type with imageBase64 — serialize to extract it
                    var json = Newtonsoft.Json.Linq.JObject.FromObject(success.Data);
                    string base64 = json["imageBase64"]?.ToString();
                    if (!string.IsNullOrEmpty(base64))
                    {
                        string folderSpec = MCPForUnity.Editor.Helpers.ScreenshotPreferences.Resolve(null);
                        string folder;
                        try
                        {
                            folder = MCPForUnity.Runtime.Helpers.ScreenshotUtility.ResolveFolderAbsolute(folderSpec);
                        }
                        catch (System.InvalidOperationException ex)
                        {
                            McpLog.Error(ex.Message);
                            return;
                        }
                        if (!System.IO.Directory.Exists(folder))
                            System.IO.Directory.CreateDirectory(folder);

                        string fileName = $"Multiview_{System.DateTime.Now:yyyyMMdd_HHmmss}.png";
                        string filePath = System.IO.Path.Combine(folder, fileName);
                        System.IO.File.WriteAllBytes(filePath, Convert.FromBase64String(base64));

                        // Import only when the file landed under Assets/.
                        string projectRoot = System.IO.Path.GetFullPath(System.IO.Path.Combine(UnityEngine.Application.dataPath, "..")).Replace('\\', '/');
                        string normalizedFile = filePath.Replace('\\', '/');
                        string normalizedRoot = projectRoot.EndsWith("/") ? projectRoot : projectRoot + "/";
                        string projectRelative = normalizedFile.StartsWith(normalizedRoot, System.StringComparison.OrdinalIgnoreCase)
                            ? normalizedFile.Substring(normalizedRoot.Length)
                            : normalizedFile;
                        if (MCPForUnity.Runtime.Helpers.ScreenshotUtility.IsUnderAssets(projectRelative))
                            AssetDatabase.ImportAsset(projectRelative, ImportAssetOptions.ForceSynchronousImport);

                        McpLog.Info($"Multiview contact sheet saved to {projectRelative}");
                    }
                    else
                    {
                        McpLog.Info(success.Message ?? "Multiview capture completed.");
                    }
                }
                else if (response is ErrorResponse error && !string.IsNullOrWhiteSpace(error.Error))
                {
                    McpLog.Error(error.Error);
                }
            }
            catch (Exception ex)
            {
                McpLog.Error($"Failed to capture multiview: {ex.Message}");
            }
        }

        private static bool IsManageSceneTool(ToolMetadata tool) => string.Equals(tool?.Name, "manage_scene", StringComparison.OrdinalIgnoreCase);

        private static bool IsManageCameraTool(ToolMetadata tool) => string.Equals(tool?.Name, "manage_camera", StringComparison.OrdinalIgnoreCase);

        private static bool IsBatchExecuteTool(ToolMetadata tool) => string.Equals(tool?.Name, "batch_execute", StringComparison.OrdinalIgnoreCase);

        private static bool IsBuiltIn(ToolMetadata tool) => tool?.IsBuiltIn ?? false;
    }
}
