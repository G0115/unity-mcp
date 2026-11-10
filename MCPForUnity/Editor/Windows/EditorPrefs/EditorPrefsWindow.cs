using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using MCPForUnity.Editor.Constants;
using UnityEditor;
using UnityEngine;

namespace MCPForUnity.Editor.Windows
{
    /// <summary>
    /// Editor window for managing Unity EditorPrefs, specifically for MCP for Unity development
    /// </summary>
    public class EditorPrefsWindow : EditorWindow
    {
        private static readonly string[] TypeChoices = { "String", "Int", "Float", "Bool" };

        private string searchFilter = "";

        // Add-new-row state (shown on demand, replaces the "add-new-row display:none" UXML block)
        private bool showAddNewRow;
        private string newKeyValue = "";
        private string newValueValue = "";
        private int newTypeIndex;

        // Data
        private List<EditorPrefItem> currentPrefs = new List<EditorPrefItem>();
        private HashSet<string> knownMcpKeys = new HashSet<string>();

        // Per-row edit state for the IMGUI prefs list (replaces the EditorPrefItem.uxml template)
        private readonly List<PrefRow> rows = new List<PrefRow>();

        // Type mapping for known EditorPrefs
        private readonly Dictionary<string, EditorPrefType> knownPrefTypes = new Dictionary<string, EditorPrefType>
        {
            // Boolean prefs
            { EditorPrefKeys.DebugLogs, EditorPrefType.Bool },
            { EditorPrefKeys.UseHttpTransport, EditorPrefType.Bool },
            { EditorPrefKeys.ResumeStdioAfterReload, EditorPrefType.Bool },
            { EditorPrefKeys.UseEmbeddedServer, EditorPrefType.Bool },
            { EditorPrefKeys.LockCursorConfig, EditorPrefType.Bool },
            { EditorPrefKeys.AutoRegisterEnabled, EditorPrefType.Bool },
            { EditorPrefKeys.SetupCompleted, EditorPrefType.Bool },
            { EditorPrefKeys.SetupDismissed, EditorPrefType.Bool },
            { EditorPrefKeys.CustomToolRegistrationEnabled, EditorPrefType.Bool },
            { EditorPrefKeys.TelemetryDisabled, EditorPrefType.Bool },
            { EditorPrefKeys.DevModeForceServerRefresh, EditorPrefType.Bool },
            { EditorPrefKeys.ProjectScopedToolsLocalHttp, EditorPrefType.Bool },
            { EditorPrefKeys.AllowLanHttpBind, EditorPrefType.Bool },
            { EditorPrefKeys.AllowInsecureRemoteHttp, EditorPrefType.Bool },

            // Integer prefs
            { EditorPrefKeys.UnitySocketPort, EditorPrefType.Int },
            { EditorPrefKeys.ValidationLevel, EditorPrefType.Int },
            { EditorPrefKeys.LastUpdateCheck, EditorPrefType.String },
            { EditorPrefKeys.LastStdIoUpgradeVersion, EditorPrefType.Int },
            { EditorPrefKeys.LastLocalHttpServerPid, EditorPrefType.Int },
            { EditorPrefKeys.LastLocalHttpServerPort, EditorPrefType.Int },

            // String prefs
            { EditorPrefKeys.EditorWindowActivePanel, EditorPrefType.String },
            { EditorPrefKeys.ClaudeCliPathOverride, EditorPrefType.String },
            { EditorPrefKeys.UvxPathOverride, EditorPrefType.String },
            { EditorPrefKeys.HttpBaseUrl, EditorPrefType.String },
            { EditorPrefKeys.HttpRemoteBaseUrl, EditorPrefType.String },
            { EditorPrefKeys.HttpTransportScope, EditorPrefType.String },
            { EditorPrefKeys.SessionId, EditorPrefType.String },
            { EditorPrefKeys.WebSocketUrlOverride, EditorPrefType.String },
            { EditorPrefKeys.GitUrlOverride, EditorPrefType.String },
            { EditorPrefKeys.PackageDeploySourcePath, EditorPrefType.String },
            { EditorPrefKeys.PackageDeployLastBackupPath, EditorPrefType.String },
            { EditorPrefKeys.PackageDeployLastTargetPath, EditorPrefType.String },
            { EditorPrefKeys.PackageDeployLastSourcePath, EditorPrefType.String },
            { EditorPrefKeys.ServerSrc, EditorPrefType.String },
            { EditorPrefKeys.LastSelectedClientId, EditorPrefType.String },
            { EditorPrefKeys.LatestKnownVersion, EditorPrefType.String },
            { EditorPrefKeys.LastAssetStoreUpdateCheck, EditorPrefType.String },
            { EditorPrefKeys.LatestKnownAssetStoreVersion, EditorPrefType.String },
            { EditorPrefKeys.LastLocalHttpServerStartedUtc, EditorPrefType.String },
            { EditorPrefKeys.LastLocalHttpServerPidArgsHash, EditorPrefType.String },
            { EditorPrefKeys.LastLocalHttpServerPidFilePath, EditorPrefType.String },
            { EditorPrefKeys.LastLocalHttpServerInstanceToken, EditorPrefType.String },
        };

        private Vector2 scrollPosition;

        /// <summary>
        /// Editable state for one prefs row in the IMGUI list.
        /// </summary>
        private class PrefRow
        {
            public EditorPrefItem Item;
            public string TextValue;
            public bool BoolValue;
            public int TypeIndex;
        }

        /// <summary>
        /// Show the EditorPrefs window
        /// </summary>
        public static void ShowWindow()
        {
            var window = GetWindow<EditorPrefsWindow>("EditorPrefs");
            window.minSize = new Vector2(600, 400);
            window.Show();
        }

        private void OnEnable()
        {
            // Reset transient UI state to avoid stale filtered results
            searchFilter = "";
            showAddNewRow = false;

            // Load known MCP keys
            LoadKnownMcpKeys();

            // Load initial data
            RefreshPrefs();
        }

        private void OnGUI()
        {
            EditorGUILayout.LabelField("EditorPrefs Manager", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(
                "Manage MCP for Unity EditorPrefs. Useful for development and testing.",
                EditorStyles.wordWrappedLabel);

            EditorGUILayout.Space();

            // Search bar
            EditorGUILayout.BeginHorizontal();
            string newFilter = EditorGUILayout.TextField("Search", searchFilter);
            if (newFilter != searchFilter)
            {
                searchFilter = newFilter ?? "";
                RefreshPrefs();
            }
            if (GUILayout.Button(new GUIContent("↻", "Refresh prefs"), GUILayout.Width(30f), GUILayout.Height(18f)))
            {
                RefreshPrefs();
            }
            EditorGUILayout.EndHorizontal();

            if (GUILayout.Button("Add Pref", GUILayout.Width(80f)))
            {
                showAddNewRow = true;
            }

            // Add-new-row (hidden until "Add Pref" is clicked)
            if (showAddNewRow)
            {
                DrawAddNewRow();
            }

            EditorGUILayout.Space();

            // Prefs list
            scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition);
            foreach (var row in rows)
            {
                DrawPrefRow(row);
            }
            EditorGUILayout.EndScrollView();
        }

        private void DrawAddNewRow()
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("Add New Pref", EditorStyles.boldLabel);

            newKeyValue = EditorGUILayout.TextField("Key", newKeyValue);
            newValueValue = EditorGUILayout.TextField("Value", newValueValue);
            newTypeIndex = EditorGUILayout.Popup("Type", newTypeIndex, TypeChoices);

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Create"))
            {
                OnCreateNewPref();
            }
            if (GUILayout.Button("Cancel"))
            {
                showAddNewRow = false;
                newKeyValue = "";
                newValueValue = "";
                newTypeIndex = 0;
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.EndVertical();
        }

        private void OnCreateNewPref()
        {
            if (string.IsNullOrEmpty(newKeyValue))
            {
                EditorUtility.DisplayDialog("Error", "Key cannot be empty.", "OK");
                return;
            }

            SaveValue(newKeyValue, newValueValue ?? "", (EditorPrefType)newTypeIndex);

            showAddNewRow = false;
            newKeyValue = "";
            newValueValue = "";
            newTypeIndex = 0;

            RefreshPrefs();
        }

        private void LoadKnownMcpKeys()
        {
            knownMcpKeys.Clear();
            var fields = typeof(EditorPrefKeys).GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);

            foreach (var field in fields)
            {
                if (field.IsLiteral && !field.IsInitOnly)
                {
                    knownMcpKeys.Add(field.GetValue(null).ToString());
                }
            }
        }

        private void RefreshPrefs()
        {
            currentPrefs.Clear();
            rows.Clear();

            // Get all EditorPrefs keys
            var allKeys = new List<string>();

            // Always show all MCP keys
            allKeys.AddRange(knownMcpKeys);

            // Try to find additional MCP keys
            var mcpKeys = GetAllMcpKeys();
            foreach (var key in mcpKeys)
            {
                if (!allKeys.Contains(key))
                {
                    allKeys.Add(key);
                }
            }

            // Sort keys
            allKeys.Sort();

            // Pre-trim filter once outside the loop
            var filter = searchFilter != null ? searchFilter.Trim() : null;

            // Create items for existing prefs
            foreach (var key in allKeys)
            {
                // Skip Customer UUID but show everything else that's defined
                if (key != EditorPrefKeys.CustomerUuid)
                {
                    // Apply search filter using OrdinalIgnoreCase for fewer allocations
                    if (!string.IsNullOrEmpty(filter) &&
                        key.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        continue;
                    }

                    var item = CreateEditorPrefItem(key);
                    if (item != null)
                    {
                        currentPrefs.Add(item);
                        rows.Add(CreateRowState(item));
                    }
                }
            }
        }

        private static PrefRow CreateRowState(EditorPrefItem item)
        {
            bool boolValue;
            bool.TryParse(item.Value, out boolValue);

            return new PrefRow
            {
                Item = item,
                TextValue = item.Value,
                BoolValue = boolValue,
                TypeIndex = (int)item.Type
            };
        }

        private List<string> GetAllMcpKeys()
        {
            // This is a simplified approach - in reality, getting all EditorPrefs is platform-specific
            // For now, we'll return known MCP keys that might exist
            var keys = new List<string>();

            // Add some common MCP keys that might not be in EditorPrefKeys
            keys.Add("MCPForUnity.TestKey");

            // Filter to only those that actually exist
            return keys.Where(EditorPrefs.HasKey).ToList();
        }

        private EditorPrefItem CreateEditorPrefItem(string key)
        {
            var item = new EditorPrefItem { Key = key, IsKnown = knownMcpKeys.Contains(key) };

            // Check if we know the type of this pref
            if (knownPrefTypes.TryGetValue(key, out var knownType))
            {
                // Check if the key actually exists
                item.IsUnset = !EditorPrefs.HasKey(key);

                // Use the known type
                switch (knownType)
                {
                    case EditorPrefType.Bool:
                        item.Type = EditorPrefType.Bool;
                        item.Value = item.IsUnset ? "Unset. Default: False" : EditorPrefs.GetBool(key, false).ToString();
                        break;
                    case EditorPrefType.Int:
                        item.Type = EditorPrefType.Int;
                        item.Value = item.IsUnset ? "Unset. Default: 0" : EditorPrefs.GetInt(key, 0).ToString();
                        break;
                    case EditorPrefType.Float:
                        item.Type = EditorPrefType.Float;
                        item.Value = item.IsUnset ? "Unset. Default: 0" : EditorPrefs.GetFloat(key, 0f).ToString();
                        break;
                    case EditorPrefType.String:
                        item.Type = EditorPrefType.String;
                        item.Value = item.IsUnset ? "Unset. Default: (empty)" : EditorPrefs.GetString(key, "");
                        break;
                }
            }
            else
            {
                // Only try to detect type for unknown keys that actually exist
                if (!EditorPrefs.HasKey(key))
                {
                    // Key doesn't exist and we don't know its type, skip it
                    return null;
                }

                // Unknown pref - try to detect type
                var stringValue = EditorPrefs.GetString(key, "");

                if (int.TryParse(stringValue, out var intValue))
                {
                    item.Type = EditorPrefType.Int;
                    item.Value = intValue.ToString();
                }
                else if (float.TryParse(stringValue, out var floatValue))
                {
                    item.Type = EditorPrefType.Float;
                    item.Value = floatValue.ToString();
                }
                else if (bool.TryParse(stringValue, out var boolValue))
                {
                    item.Type = EditorPrefType.Bool;
                    item.Value = boolValue.ToString();
                }
                else
                {
                    item.Type = EditorPrefType.String;
                    item.Value = stringValue;
                }
            }

            return item;
        }

        private void DrawPrefRow(PrefRow row)
        {
            EditorPrefItem item = row.Item;

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.BeginHorizontal();

            EditorGUILayout.LabelField(
                new GUIContent(item.Key, item.IsKnown ? "Known MCP pref" : null),
                EditorStyles.boldLabel,
                GUILayout.MinWidth(160f));

            // Type dropdown
            EditorGUI.BeginChangeCheck();
            int selectedType = EditorGUILayout.Popup(row.TypeIndex, TypeChoices, GUILayout.Width(70f));
            if (EditorGUI.EndChangeCheck() && selectedType != row.TypeIndex)
            {
                bool wasBool = row.TypeIndex == (int)EditorPrefType.Bool;
                row.TypeIndex = selectedType;
                bool isBool = selectedType == (int)EditorPrefType.Bool;
                if (isBool && !wasBool)
                {
                    bool parsed;
                    bool.TryParse(row.TextValue, out parsed);
                    row.BoolValue = parsed;
                }
                else if (!isBool && wasBool)
                {
                    row.TextValue = row.BoolValue ? "True" : "False";
                }
            }

            // Value control (by type; unset prefs show their default hint instead)
            if (item.IsUnset)
            {
                EditorGUILayout.LabelField(row.TextValue, EditorStyles.miniLabel);
            }
            else
            {
                switch ((EditorPrefType)row.TypeIndex)
                {
                    case EditorPrefType.Bool:
                        row.BoolValue = EditorGUILayout.Toggle(row.BoolValue, GUILayout.Width(60f));
                        break;
                    case EditorPrefType.Int:
                        int intValue;
                        int.TryParse(row.TextValue, out intValue);
                        row.TextValue = EditorGUILayout.IntField(intValue).ToString();
                        break;
                    case EditorPrefType.Float:
                        float floatValue;
                        float.TryParse(row.TextValue, out floatValue);
                        row.TextValue = EditorGUILayout.FloatField(floatValue).ToString();
                        break;
                    default:
                        row.TextValue = EditorGUILayout.TextField(row.TextValue ?? "");
                        break;
                }
            }

            // Action buttons
            using (new EditorGUI.DisabledScope(item.IsUnset))
            {
                if (GUILayout.Button(new GUIContent("✓", "Save changes"), GUILayout.Width(24f)))
                {
                    SavePref(item, GetRowValueText(row), (EditorPrefType)row.TypeIndex);
                }
            }
            if (GUILayout.Button(new GUIContent("✕", "Delete pref"), GUILayout.Width(24f)))
            {
                if (EditorUtility.DisplayDialog(
                        "Delete Pref",
                        $"Delete EditorPref '{item.Key}'?",
                        "Delete",
                        "Cancel"))
                {
                    EditorPrefs.DeleteKey(item.Key);
                    RefreshPrefs();
                }
            }

            EditorGUILayout.EndHorizontal();
            EditorGUILayout.EndVertical();
        }

        private static string GetRowValueText(PrefRow row)
        {
            if ((EditorPrefType)row.TypeIndex == EditorPrefType.Bool)
            {
                return row.BoolValue ? "True" : "False";
            }
            return row.TextValue ?? "";
        }

        private void SavePref(EditorPrefItem item, string newValue, EditorPrefType newType)
        {
            SaveValue(item.Key, newValue, newType);
            RefreshPrefs();
        }

        private void SaveValue(string key, string value, EditorPrefType type)
        {
            switch (type)
            {
                case EditorPrefType.String:
                    EditorPrefs.SetString(key, value);
                    break;
                case EditorPrefType.Int:
                    if (int.TryParse(value, out var intValue))
                    {
                        EditorPrefs.SetInt(key, intValue);
                    }
                    else
                    {
                        EditorUtility.DisplayDialog("Error", $"Cannot convert '{value}' to int", "OK");
                        return;
                    }
                    break;
                case EditorPrefType.Float:
                    if (float.TryParse(value, out var floatValue))
                    {
                        EditorPrefs.SetFloat(key, floatValue);
                    }
                    else
                    {
                        EditorUtility.DisplayDialog("Error", $"Cannot convert '{value}' to float", "OK");
                        return;
                    }
                    break;
                case EditorPrefType.Bool:
                    if (bool.TryParse(value, out var boolValue))
                    {
                        EditorPrefs.SetBool(key, boolValue);
                    }
                    else
                    {
                        EditorUtility.DisplayDialog("Error", $"Cannot convert '{value}' to bool (use 'True' or 'False')", "OK");
                        return;
                    }
                    break;
            }
        }
    }

    /// <summary>
    /// Represents an EditorPrefs item
    /// </summary>
    public class EditorPrefItem
    {
        public string Key { get; set; }
        public string Value { get; set; }
        public EditorPrefType Type { get; set; }
        public bool IsKnown { get; set; }
        public bool IsUnset { get; set; }
    }

    /// <summary>
    /// EditorPrefs value types
    /// </summary>
    public enum EditorPrefType
    {
        String,
        Int,
        Float,
        Bool
    }
}
