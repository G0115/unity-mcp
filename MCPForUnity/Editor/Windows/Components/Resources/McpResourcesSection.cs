using System;
using System.Collections.Generic;
using System.Linq;
using MCPForUnity.Editor.Constants;
using MCPForUnity.Editor.Helpers;
using MCPForUnity.Editor.Services;
using UnityEditor;
using UnityEngine;

namespace MCPForUnity.Editor.Windows.Components.Resources
{
    /// <summary>
    /// Controller for the Resources section inside the MCP for Unity editor window.
    /// Provides discovery, filtering, and per-resource enablement toggles.
    /// IMGUI implementation drawn via <see cref="Draw"/> in the main window's OnGUI.
    /// </summary>
    public class McpResourcesSection
    {
        /// <summary>A resource group rendered as one foldout in Draw().</summary>
        private class CategoryEntry
        {
            public string Title;
            public string PrefsSuffix;
            public List<ResourceMetadata> Resources;
            public bool FoldoutOpen;
        }

        private List<ResourceMetadata> allResources = new List<ResourceMetadata>();
        private readonly List<CategoryEntry> categories = new List<CategoryEntry>();
        private readonly List<string> infoMessages = new List<string>();
        private string summaryText = "Discovering resources...";
        private bool showNote;
        private bool hasResources;

        public McpResourcesSection()
        {
        }

        /// <summary>
        /// Draws the Resources section. Call from the main window's OnGUI.
        /// Layout order follows McpResourcesSection.uxml.
        /// </summary>
        public void Draw()
        {
            EditorGUILayout.LabelField("Resources", EditorStyles.boldLabel);

            // Summary
            EditorGUILayout.HelpBox(summaryText, MessageType.None);

            // Action buttons (Enable All / Disable All / Rescan)
            EditorGUILayout.BeginHorizontal();
            using (new EditorGUI.DisabledScope(!hasResources))
            {
                if (GUILayout.Button("Enable All", GUILayout.Width(100)))
                {
                    SetAllResourcesState(true);
                }
                if (GUILayout.Button("Disable All", GUILayout.Width(100)))
                {
                    SetAllResourcesState(false);
                }
            }
            if (GUILayout.Button("Rescan", GUILayout.Width(100)))
            {
                McpLog.Info("Rescanning MCP resources from the editor window.");
                MCPServiceLocator.ResourceDiscovery.InvalidateCache();
                Refresh();
            }
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();

            // Note shown only when resources exist
            if (showNote)
            {
                EditorGUILayout.HelpBox("Changes apply after reconnecting or re-registering resources.", MessageType.Info);
            }

            // Info messages (e.g. "No MCP resources found.")
            foreach (string message in infoMessages)
            {
                EditorGUILayout.HelpBox(message, MessageType.None);
            }

            // Resource categories
            foreach (CategoryEntry category in categories)
            {
                DrawCategory(category);
            }
        }

        /// <summary>
        /// Rebuilds the resource list and synchronises toggle states.
        /// </summary>
        public void Refresh()
        {
            categories.Clear();
            infoMessages.Clear();

            var service = MCPServiceLocator.ResourceDiscovery;
            allResources = service.DiscoverAllResources()
                .OrderBy(r => r.IsBuiltIn ? 0 : 1)
                .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            hasResources = allResources.Count > 0;
            showNote = hasResources;

            if (!hasResources)
            {
                infoMessages.Add("No MCP resources found. Add classes decorated with [McpForUnityResource] to expose resources.");
                UpdateSummary();
                return;
            }

            BuildCategory("Built-in Resources", "built-in", allResources.Where(r => r.IsBuiltIn));

            var customResources = allResources.Where(r => !r.IsBuiltIn).ToList();
            if (customResources.Count > 0)
            {
                BuildCategory("Custom Resources", "custom", customResources);
            }
            else
            {
                infoMessages.Add("No custom resources detected in loaded assemblies.");
            }

            UpdateSummary();
        }

        private void BuildCategory(string title, string prefsSuffix, IEnumerable<ResourceMetadata> resources)
        {
            var resourceList = resources.ToList();
            if (resourceList.Count == 0)
            {
                return;
            }

            categories.Add(new CategoryEntry
            {
                Title = title,
                PrefsSuffix = prefsSuffix,
                Resources = resourceList,
                FoldoutOpen = EditorPrefs.GetBool(EditorPrefKeys.ResourceFoldoutStatePrefix + prefsSuffix, true)
            });
        }

        private void DrawCategory(CategoryEntry category)
        {
            string header = $"{category.Title} ({category.Resources.Count})";

            category.FoldoutOpen = EditorGUILayout.Foldout(category.FoldoutOpen, header, true);

            if (!category.FoldoutOpen)
            {
                return;
            }

            EditorGUI.indentLevel++;
            foreach (ResourceMetadata resource in category.Resources)
            {
                DrawResourceRow(resource);
            }
            EditorGUI.indentLevel--;
        }

        private void DrawResourceRow(ResourceMetadata resource)
        {
            bool isEnabled = MCPServiceLocator.ResourceDiscovery.IsResourceEnabled(resource.Name);
            string tooltip = string.IsNullOrWhiteSpace(resource.Description) ? resource.Name : resource.Description;

            EditorGUILayout.BeginHorizontal();
            EditorGUI.BeginChangeCheck();
            bool newEnabled = GUILayout.Toggle(isEnabled, new GUIContent(resource.Name, tooltip));
            if (EditorGUI.EndChangeCheck() && newEnabled != isEnabled)
            {
                HandleToggleChange(resource, newEnabled);
            }

            GUILayout.FlexibleSpace();
            GUILayout.Label(resource.IsBuiltIn ? "Built-in" : "Custom", EditorStyles.miniLabel, GUILayout.ExpandWidth(false));
            EditorGUILayout.EndHorizontal();

            if (!string.IsNullOrWhiteSpace(resource.Description) && !resource.Description.StartsWith("Resource:", StringComparison.Ordinal))
            {
                EditorGUILayout.LabelField(resource.Description, EditorStyles.miniLabel);
            }
        }

        private void HandleToggleChange(ResourceMetadata resource, bool enabled, bool updateSummary = true)
        {
            MCPServiceLocator.ResourceDiscovery.SetResourceEnabled(resource.Name, enabled);

            if (updateSummary)
            {
                UpdateSummary();
            }
        }

        private void SetAllResourcesState(bool enabled)
        {
            foreach (var resource in allResources)
            {
                if (MCPServiceLocator.ResourceDiscovery.IsResourceEnabled(resource.Name) != enabled)
                {
                    MCPServiceLocator.ResourceDiscovery.SetResourceEnabled(resource.Name, enabled);
                }
            }

            UpdateSummary();
        }

        private void UpdateSummary()
        {
            if (allResources.Count == 0)
            {
                summaryText = "No MCP resources discovered.";
                return;
            }

            int enabledCount = allResources.Count(r => MCPServiceLocator.ResourceDiscovery.IsResourceEnabled(r.Name));
            summaryText = $"{enabledCount} of {allResources.Count} resources enabled.";
        }
    }
}
