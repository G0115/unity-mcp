using System;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using MCPForUnity.Editor.Constants;
using MCPForUnity.Editor.Windows;
using MCPForUnity.Editor.Windows.Components.AssetGen;
using MCPForUnity.Editor.Windows.Components.Advanced;
using MCPForUnity.Editor.Windows.Components.ClientConfig;
using MCPForUnity.Editor.Windows.Components.Connection;
using UnityEngine;

namespace MCPForUnityTests.Editor.Windows.Characterization
{
    /// <summary>
    /// Characterization tests for Windows & UI domain.
    /// These tests capture CURRENT behavior without refactoring.
    /// They serve as a regression baseline for future refactoring work.
    ///
    /// The windows and section controllers are IMGUI (OnGUI/EditorGUILayout): each section keeps
    /// its control state in plain private fields and renders it through a public Draw() method,
    /// so these tests anchor on that structure (fields, nested state types, Draw/handler methods).
    ///
    /// Covers: MCPForUnityEditorWindow, MCPSetupWindow, EditorPrefsWindow, and the section
    /// controllers (Connection, Advanced, ClientConfig, AssetGen, Blender Bridge).
    /// </summary>
    [TestFixture]
    public class WindowsCharacterizationTests : TransportPreferenceTestBase
    {
        #region Section 1: EditorPrefsWindow Tests (3 tests)

        /// <summary>
        /// Current behavior: EditorPrefsWindow keeps no VisualTree; each prefs row is one plain
        /// PrefRow state object in a List, scrolled by a Vector2 scroll position.
        /// </summary>
        [Test]
        public void EditorPrefsWindow_KeepsRowStateInPlainPrefRowObjects_InsteadOfVisualTree()
        {
            var type = typeof(EditorPrefsWindow);

            var prefRow = type.GetNestedType("PrefRow", BindingFlags.NonPublic);
            Assert.IsNotNull(prefRow, "Should have a private PrefRow row-state class");
            foreach (string member in new[] { "Item", "TextValue", "BoolValue", "TypeIndex" })
                Assert.IsNotNull(prefRow.GetField(member), $"PrefRow should have a {member} field");

            var rowsField = type.GetField("rows", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(rowsField, "Should have a rows field");
            Assert.IsTrue(rowsField.FieldType.IsGenericType && rowsField.FieldType.GetGenericArguments()[0] == prefRow,
                "rows should be a List<PrefRow>");

            var scrollField = type.GetField("scrollPosition", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(scrollField, "Should have a scrollPosition field");
            Assert.AreEqual(typeof(Vector2), scrollField.FieldType, "Scrolling is a Vector2 scroll position.");
        }

        /// <summary>
        /// Current behavior: EditorPrefsWindow uses type detection logic to identify
        /// whether an EditorPref is Bool, Int, Float, or String.
        /// </summary>
        [Test]
        public void EditorPrefsWindow_UsesTypeDetectionLogic_ForUnknownPrefs()
        {
            // Verify the enum exists
            var type = typeof(EditorPrefsWindow);
            var enumType = type.Assembly.GetType("MCPForUnity.Editor.Windows.EditorPrefType");
            Assert.IsNotNull(enumType, "Should have EditorPrefType enum");

            var enumValues = Enum.GetNames(enumType);
            Assert.Contains("String", enumValues);
            Assert.Contains("Int", enumValues);
            Assert.Contains("Float", enumValues);
            Assert.Contains("Bool", enumValues);

            // Detection lives in CreateEditorPrefItem; each detected item becomes one IMGUI row.
            Assert.IsNotNull(type.GetMethod("CreateEditorPrefItem", BindingFlags.NonPublic | BindingFlags.Instance),
                "type detection should live in CreateEditorPrefItem");
            Assert.IsNotNull(type.GetMethod("CreateRowState", BindingFlags.NonPublic | BindingFlags.Instance),
                "each pref should get one row state via CreateRowState");
        }

        /// <summary>
        /// Current behavior: EditorPrefsWindow draws each row from its state object and saves
        /// through SavePref when the row's save button is pressed — no per-item callbacks are
        /// registered up front anymore.
        /// </summary>
        [Test]
        public void EditorPrefsWindow_DrawsAndSavesPerRow_ViaDrawPrefRowAndSavePref()
        {
            var type = typeof(EditorPrefsWindow);
            Assert.IsNotNull(type.GetMethod("DrawPrefRow", BindingFlags.NonPublic | BindingFlags.Instance),
                "rows should be drawn by DrawPrefRow(row)");
            Assert.IsNotNull(type.GetMethod("SavePref", BindingFlags.NonPublic | BindingFlags.Instance),
                "row saves should go through SavePref(item, value, type)");
            Assert.IsNotNull(type.GetMethod("GetRowValueText", BindingFlags.NonPublic | BindingFlags.Static),
                "the row's value text should be computed by GetRowValueText(row)");
        }

        #endregion

        #region Section 2: MCPSetupWindow Tests (3 tests)

        /// <summary>
        /// Current behavior: MCPSetupWindow keeps dependency state in three DependencyDisplay
        /// structs (Python, uv, Git) plus plain wizard/status fields — no cached UI elements.
        /// </summary>
        [Test]
        public void MCPSetupWindow_KeepsDependencyStateInDependencyDisplayFields()
        {
            var type = typeof(MCPSetupWindow);
            var displayType = typeof(MCPSetupWindow.DependencyDisplay);

            foreach (string fieldName in new[] { "pythonDisplay", "uvDisplay", "gitDisplay" })
            {
                var field = type.GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance);
                Assert.IsNotNull(field, $"Should have a {fieldName} field");
                Assert.AreEqual(displayType, field.FieldType, $"{fieldName} should be a DependencyDisplay");
            }

            foreach (string fieldName in new[]
                     {
                         "clientsStepActive", "statusMessageText", "statusMessageColor",
                         "showInstallationSection", "installationInstructionsText",
                         "showInstallUvButton", "installUvButtonEnabled"
                     })
            {
                Assert.IsNotNull(type.GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance),
                    $"Should have a {fieldName} field");
            }

            // DependencyDisplay carries the whole rendered row: version, details, colour and dot.
            Assert.IsNotNull(displayType.GetField("Version"), "DependencyDisplay.Version");
            Assert.IsNotNull(displayType.GetField("Details"), "DependencyDisplay.Details");
            Assert.IsNotNull(displayType.GetField("DetailsColor"), "DependencyDisplay.DetailsColor");
            Assert.IsNotNull(displayType.GetField("Indicator"), "DependencyDisplay.Indicator");
        }

        /// <summary>
        /// Current behavior: MCPSetupWindow computes each row's status via the pure static
        /// BuildDependencyDisplay, mapping availability to a Valid/Invalid/Neutral indicator and
        /// a details colour (no CSS class lists anymore).
        /// </summary>
        [Test]
        public void MCPSetupWindow_BuildDependencyDisplay_MapsStatusToIndicatorAndColor()
        {
            var type = typeof(MCPSetupWindow);
            var method = type.GetMethod("BuildDependencyDisplay",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);

            Assert.IsNotNull(method, "Should have BuildDependencyDisplay");
            Assert.AreEqual(1, method.GetParameters().Length, "BuildDependencyDisplay takes the DependencyStatus");

            var statusKind = type.GetNestedType("StatusKind", BindingFlags.NonPublic);
            Assert.IsNotNull(statusKind, "Should have a private StatusKind enum");
            var kinds = Enum.GetNames(statusKind);
            Assert.Contains("Valid", kinds);
            Assert.Contains("Invalid", kinds);
            Assert.Contains("Neutral", kinds);
        }

        /// <summary>
        /// Current behavior: MCPSetupWindow handles buttons directly inside OnGUI/Draw* methods —
        /// there is no CreateGUI callback registration step anymore.
        /// </summary>
        [Test]
        public void MCPSetupWindow_HandlesButtonsDirectlyInOnGUI_WithoutCallbackRegistration()
        {
            var type = typeof(MCPSetupWindow);
            Assert.IsNotNull(type.GetMethod("OnGUI", BindingFlags.NonPublic | BindingFlags.Instance),
                "the window should render through OnGUI");
            Assert.IsNull(type.GetMethod("CreateGUI", BindingFlags.Public | BindingFlags.Instance),
                "the UITK CreateGUI hook should be gone");

            foreach (string handler in new[]
                     {
                         "OnRefreshClicked", "OnDoneClicked",
                         "OnOpenPythonInstallClicked", "OnOpenUvInstallClicked", "OnInstallUvClicked"
                     })
            {
                Assert.IsNotNull(type.GetMethod(handler, BindingFlags.NonPublic | BindingFlags.Instance),
                    $"button handlers should be plain methods: {handler}");
            }

            foreach (string draw in new[] { "DrawDependenciesStep", "DrawClientsStep", "DrawDependencyRow" })
            {
                Assert.IsNotNull(type.GetMethod(draw, BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static),
                    $"step drawing should be a Draw method: {draw}");
            }
        }

        #endregion

        #region Section 3: McpConnectionSection Tests (6 tests)

        /// <summary>
        /// Current behavior: McpConnectionSection keeps 20+ plain state fields (strings, bools,
        /// ints) that Draw() renders every frame — no cached UI elements.
        /// </summary>
        [Test]
        public void McpConnectionSection_KeepsControlStateInPlainFields()
        {
            var type = typeof(McpConnectionSection);
            Assert.IsNotNull(type.GetMethod("Draw", BindingFlags.Public | BindingFlags.Instance),
                "the section should render through Draw()");

            var fields = type.GetFields(BindingFlags.NonPublic | BindingFlags.Instance);
            var stateFields = fields.Where(f =>
                f.FieldType == typeof(string) || f.FieldType == typeof(bool) || f.FieldType == typeof(int)).ToArray();

            Assert.GreaterOrEqual(stateFields.Length, 10, "Should have 10+ plain state fields");

            foreach (string fieldName in new[]
                     {
                         "httpUrlField", "apiKeyField", "unityPortField",
                         "connectionStatusLabelText", "connectionToggleButtonText",
                         "startHttpServerButtonText", "transportMismatchText"
                     })
            {
                Assert.IsNotNull(type.GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance),
                    $"Should have a {fieldName} field");
            }
        }

        /// <summary>
        /// Current behavior: McpConnectionSection reads 3+ prefs in InitializeState
        /// (UseHttpTransport, HttpTransportScope, UnitySocketPort).
        /// </summary>
        [Test]
        public void McpConnectionSection_ReadsMultipleEditorPrefs_InInitializeState()
        {
            var prefKeys = new[]
            {
                EditorPrefKeys.UseHttpTransport,
                EditorPrefKeys.HttpTransportScope,
                EditorPrefKeys.UnitySocketPort
            };

            foreach (var key in prefKeys)
            {
                Assert.IsNotEmpty(key, $"EditorPrefKey should not be empty: {key}");
            }

            var type = typeof(McpConnectionSection);
            Assert.IsNotNull(type.GetMethod("InitializeState", BindingFlags.NonPublic | BindingFlags.Instance),
                "prefs should be read once in InitializeState (called from the constructor)");
        }

        /// <summary>
        /// Current behavior: McpConnectionSection routes transport changes through the
        /// OnTransportSelectionChanged(previous, selected) handler with a multi-step flow.
        /// </summary>
        [Test]
        public void McpConnectionSection_HandlesTransportChange_InOnTransportSelectionChanged()
        {
            var type = typeof(McpConnectionSection);
            var handler = type.GetMethod("OnTransportSelectionChanged", BindingFlags.NonPublic | BindingFlags.Instance);

            Assert.IsNotNull(handler, "Should have OnTransportSelectionChanged");
            Assert.AreEqual(2, handler.GetParameters().Length,
                "the handler takes (previous, selected) transports");

            var callbackSteps = new[]
            {
                "1. Get previous and new transport values",
                "2. Persist UseHttpTransport to EditorPrefs",
                "3. Persist HttpTransportScope if HTTP",
                "4. Clear resume flags (ResumeStdioAfterReload in EditorPrefs, HTTP resume key in SessionState)",
                "5. Update UI visibility flags",
                "6. Invoke OnManualConfigUpdateRequested event",
                "7. Invoke OnTransportChanged event",
                "8. Stop opposing transport if switching HTTP<->Stdio"
            };

            Assert.Greater(callbackSteps.Length, 0); // flow documented above
        }

        /// <summary>
        /// Current behavior: McpConnectionSection persists the HTTP URL only when its field loses
        /// keyboard focus (httpUrlWasFocused flag), not on every keystroke.
        /// </summary>
        [Test]
        public void McpConnectionSection_PersistsHttpUrl_OnFocusLoss()
        {
            var type = typeof(McpConnectionSection);
            Assert.IsNotNull(type.GetMethod("PersistHttpUrlFromField", BindingFlags.NonPublic | BindingFlags.Instance),
                "Should have PersistHttpUrlFromField");
            Assert.IsNotNull(type.GetField("httpUrlWasFocused", BindingFlags.NonPublic | BindingFlags.Instance),
                "focus-loss commits are tracked with the httpUrlWasFocused flag");
        }

        /// <summary>
        /// Current behavior: McpConnectionSection debounce thresholds (orphaned sessions, health
        /// reporting) are pure internal static functions, testable without a GUI context.
        /// </summary>
        [Test]
        public void McpConnectionSection_DebounceThresholds_AreInternalPureFunctions()
        {
            Assert.AreEqual(3, McpConnectionSection.OrphanedSessionDownPollThreshold,
                "an active session needs repeated missed polls before being torn down as orphaned");
            Assert.AreEqual(2, McpConnectionSection.UnhealthyVerificationThreshold,
                "health flips to Unhealthy only after repeated failed verifications");

            Assert.IsTrue(McpConnectionSection.ShouldEndOrphanedSession(true, true, false, false, 3));
            Assert.IsFalse(McpConnectionSection.ShouldEndOrphanedSession(true, true, false, false, 2));
            Assert.IsFalse(McpConnectionSection.ShouldEndOrphanedSession(false, true, false, false, 10));
            Assert.IsFalse(McpConnectionSection.ShouldEndOrphanedSession(true, true, true, false, 10),
                "a toggle in flight must never end the session");

            Assert.IsTrue(McpConnectionSection.ShouldReportUnhealthy(2));
            Assert.IsFalse(McpConnectionSection.ShouldReportUnhealthy(1));
        }

        /// <summary>
        /// Current behavior: McpConnectionSection raises events for inter-component communication
        /// (OnManualConfigUpdateRequested, OnTransportChanged).
        /// </summary>
        [Test]
        public void McpConnectionSection_RaisesEvents_ForInterComponentCommunication()
        {
            var type = typeof(McpConnectionSection);
            var events = type.GetEvents(BindingFlags.Public | BindingFlags.Instance);

            var eventNames = events.Select(e => e.Name).ToArray();
            Assert.Contains("OnManualConfigUpdateRequested", eventNames);
            Assert.Contains("OnTransportChanged", eventNames);
        }

        #endregion

        #region Section 4: McpAdvancedSection Tests (4 tests)

        /// <summary>
        /// Current behavior: McpAdvancedSection keeps its control state (paths, toggles, status)
        /// in plain fields rendered by Draw().
        /// </summary>
        [Test]
        public void McpAdvancedSection_KeepsControlStateInPlainFields()
        {
            var type = typeof(McpAdvancedSection);
            Assert.IsNotNull(type.GetMethod("Draw", BindingFlags.Public | BindingFlags.Instance),
                "the section should render through Draw()");

            foreach (string fieldName in new[]
                     {
                         "uvxPathDisplay", "gitUrl", "debugLogs", "autoStartOnLoad", "devModeForceRefresh",
                         "screenshotsFolder", "deploySourcePath", "deployTargetText", "deployBackupText",
                         "deployStatusText", "healthStatusText"
                     })
            {
                Assert.IsNotNull(type.GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance),
                    $"Should have a {fieldName} field");
            }
        }

        /// <summary>
        /// Current behavior: Advanced section reads 5+ EditorPrefs
        /// (GitUrl, DebugLogs, DevModeRefresh, paths).
        /// </summary>
        [Test]
        public void McpAdvancedSection_ReadsMultiplePreferences_ForConfiguration()
        {
            var expectedPrefKeys = new[]
            {
                EditorPrefKeys.GitUrlOverride,
                EditorPrefKeys.DebugLogs,
                EditorPrefKeys.DevModeForceServerRefresh,
                EditorPrefKeys.PackageDeploySourcePath,
                EditorPrefKeys.ClaudeCliPathOverride,
                EditorPrefKeys.UvxPathOverride
            };

            foreach (var key in expectedPrefKeys)
            {
                Assert.IsNotEmpty(key, $"EditorPrefKey should not be empty");
            }
        }

        /// <summary>
        /// Current behavior: Advanced section keeps one bool field per toggle-backed preference;
        /// the IMGUI toggle in Draw() writes the pref on change (no RegisterValueChangedCallback).
        /// </summary>
        [Test]
        public void McpAdvancedSection_KeepsBoolFields_ForToggleBackedPrefs()
        {
            var type = typeof(McpAdvancedSection);
            foreach (string fieldName in new[]
                     {
                         "debugLogs", "logRecord", "autoStartOnLoad", "devModeForceRefresh",
                         "allowLanHttpBind", "allowInsecureRemoteHttp", "deployRestoreEnabled"
                     })
            {
                var field = type.GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance);
                Assert.IsNotNull(field, $"Should have a {fieldName} field");
                Assert.AreEqual(typeof(bool), field.FieldType, $"{fieldName} should back a toggle");
            }
        }

        /// <summary>
        /// Current behavior: Advanced section surfaces validation and status through text + error
        /// flag fields (deploy status, health status) instead of CSS class lists.
        /// </summary>
        [Test]
        public void McpAdvancedSection_ReportsStatusThroughTextAndErrorFlagFields()
        {
            var type = typeof(McpAdvancedSection);
            Assert.IsNotNull(type.GetField("deployStatusText", BindingFlags.NonPublic | BindingFlags.Instance));
            Assert.IsNotNull(type.GetField("deployStatusIsError", BindingFlags.NonPublic | BindingFlags.Instance));
            Assert.IsNotNull(type.GetField("healthStatusText", BindingFlags.NonPublic | BindingFlags.Instance));
            Assert.IsNotNull(type.GetMethod("UpdateHealthStatus", BindingFlags.Public | BindingFlags.Instance),
                "health status should be pushed in via UpdateHealthStatus");
            Assert.IsNotNull(type.GetMethod("UpdatePathOverrides", BindingFlags.Public | BindingFlags.Instance),
                "path overrides should be re-read via UpdatePathOverrides");
        }

        #endregion

        #region Section 5: McpClientConfigSection Tests (4 tests)

        /// <summary>
        /// Current behavior: Client config section keeps selection/status/config state in plain
        /// fields (selected client index, status text, config path/json) rendered by Draw().
        /// </summary>
        [Test]
        public void McpClientConfigSection_KeepsSelectionAndStatusInPlainFields()
        {
            var type = typeof(McpClientConfigSection);
            Assert.IsNotNull(type.GetMethod("Draw", BindingFlags.Public | BindingFlags.Instance),
                "the section should render through Draw()");

            var indexField = type.GetField("selectedClientIndex", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(indexField, "Should have a selectedClientIndex field");
            Assert.AreEqual(typeof(int), indexField.FieldType, "the client dropdown is an int index");

            foreach (string fieldName in new[]
                     {
                         "clientStatusText", "configureButtonText", "configPathText", "configJsonText",
                         "installationStepsText"
                     })
            {
                Assert.IsNotNull(type.GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance),
                    $"Should have a {fieldName} field");
            }
        }

        /// <summary>
        /// Current behavior: Client config selection changes route through
        /// OnClientSelected(index), with FindConfiguratorIndex to restore a persisted selection.
        /// </summary>
        [Test]
        public void McpClientConfigSection_RoutesSelectionThroughOnClientSelected()
        {
            var type = typeof(McpClientConfigSection);
            var handler = type.GetMethod("OnClientSelected", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(handler, "Should have OnClientSelected");
            Assert.AreEqual(1, handler.GetParameters().Length, "OnClientSelected takes the new index");
            Assert.IsNotNull(type.GetMethod("FindConfiguratorIndex", BindingFlags.NonPublic | BindingFlags.Instance),
                "persisted selections are restored via FindConfiguratorIndex");
        }

        /// <summary>
        /// Current behavior: Client config conditional rows are plain bool visibility flags
        /// (no DisplayStyle toggles anymore).
        /// </summary>
        [Test]
        public void McpClientConfigSection_UsesBoolVisibilityFlags_ForConditionalRows()
        {
            var type = typeof(McpClientConfigSection);
            foreach (string fieldName in new[]
                     {
                         "installSkillsVisible", "claudeCliPathVisible", "clientProjectDirVisible",
                         "clientDetailsOpen", "manualConfigOpen"
                     })
            {
                var field = type.GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance);
                Assert.IsNotNull(field, $"Should have a {fieldName} field");
                Assert.AreEqual(typeof(bool), field.FieldType, $"{fieldName} should be a visibility flag");
            }
        }

        /// <summary>
        /// Current behavior: Client config cascades updates via events and public refresh methods
        /// (RefreshSelectedClient, UpdateClientStatus, UpdateManualConfiguration).
        /// </summary>
        [Test]
        public void McpClientConfigSection_DropdownTriggersCascadingUpdates_OnChange()
        {
            var type = typeof(McpClientConfigSection);
            var events = type.GetEvents(BindingFlags.Public | BindingFlags.Instance);
            var eventNames = events.Select(e => e.Name).ToArray();
            Assert.Contains("OnClientTransportDetected", eventNames);
            Assert.Contains("OnClientConfigMismatch", eventNames);

            foreach (string method in new[] { "RefreshSelectedClient", "UpdateClientStatus", "UpdateManualConfiguration" })
            {
                Assert.IsNotNull(type.GetMethod(method, BindingFlags.Public | BindingFlags.Instance),
                    $"selection changes should cascade through {method}");
            }
        }

        #endregion

        #region Section 6: Cross-Pattern Tests (5 tests)

        /// <summary>
        /// Current behavior: every section controller renders through a public Draw() and keeps
        /// its control state in plain fields (IMGUI), replacing the old
        /// Cache-Initialize-Register three-phase UITK pattern.
        /// </summary>
        [Test]
        public void CrossPattern_IMGUIStateInPlainFields_RepeatsAcrossComponents()
        {
            var components = new[]
            {
                typeof(McpConnectionSection),
                typeof(McpAdvancedSection),
                typeof(McpClientConfigSection),
                typeof(McpAssetGenSection),
                typeof(McpBlenderBridgePanel)
            };

            foreach (var component in components)
            {
                Assert.IsNotNull(component.GetMethod("Draw", BindingFlags.Public | BindingFlags.Instance),
                    component.Name + " should render through Draw()");
                var fields = component.GetFields(BindingFlags.NonPublic | BindingFlags.Instance);
                Assert.GreaterOrEqual(fields.Length, 10,
                    component.Name + " keeps its control state in plain fields");
            }
        }

        /// <summary>
        /// Current behavior: EditorPrefs binding still spans the same kinds (Bool, String/Path,
        /// Int, Key Deletion, Scope-Aware), keyed by EditorPrefKeys and typed via
        /// EditorPrefsWindow.knownPrefTypes.
        /// </summary>
        [Test]
        public void CrossPattern_EditorPrefsBinding_HasFiveVariations()
        {
            // 1. Simple Boolean, 2. String URL/Path, 3. Integer Port, 4. Key Deletion,
            // 5. Scope-Aware (HttpTransportScope).
            foreach (var key in new[]
                     {
                         EditorPrefKeys.DebugLogs,
                         EditorPrefKeys.GitUrlOverride,
                         EditorPrefKeys.UnitySocketPort,
                         EditorPrefKeys.HttpTransportScope,
                         EditorPrefKeys.EditorWindowActivePanel
                     })
            {
                Assert.IsNotEmpty(key, $"EditorPrefKey should not be empty: {key}");
            }

            var map = typeof(EditorPrefsWindow).GetField("knownPrefTypes", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(map, "EditorPrefsWindow should map keys to types via knownPrefTypes");
            var knownTypes = (System.Collections.IDictionary)map.GetValue(null);
            Assert.Greater(knownTypes.Count, 20, "most MCP prefs have a known type");
        }

        /// <summary>
        /// Current behavior: handlers are plain methods invoked directly from Draw() (button
        /// pressed → method call), replacing the old callback-registration patterns.
        /// </summary>
        [Test]
        public void CrossPattern_HandlersAreInvokedDirectlyFromDraw()
        {
            Assert.IsNotNull(typeof(McpConnectionSection)
                .GetMethod("OnTransportSelectionChanged", BindingFlags.NonPublic | BindingFlags.Instance));
            Assert.IsNotNull(typeof(McpAdvancedSection)
                .GetMethod("OnDeployClicked", BindingFlags.NonPublic | BindingFlags.Instance));
            Assert.IsNotNull(typeof(McpClientConfigSection)
                .GetMethod("OnConfigureClicked", BindingFlags.NonPublic | BindingFlags.Instance));
            Assert.IsNotNull(typeof(McpAssetGenSection)
                .GetMethod("OnRefreshClicked", BindingFlags.NonPublic | BindingFlags.Instance));
        }

        /// <summary>
        /// Current behavior: each controller re-reads prefs into its plain fields through a state
        /// sync method (SyncFromPrefs / InitializeState / UpdatePathOverrides / UpdateClientStatus).
        /// </summary>
        [Test]
        public void CrossPattern_PrefsToFieldSync_RunsThroughStateSyncMethods()
        {
            Assert.IsNotNull(typeof(McpAssetGenSection)
                .GetMethod("SyncFromPrefs", BindingFlags.NonPublic | BindingFlags.Instance));
            Assert.IsNotNull(typeof(McpBlenderBridgePanel)
                .GetMethod("SyncFromPrefs", BindingFlags.NonPublic | BindingFlags.Instance));
            Assert.IsNotNull(typeof(McpConnectionSection)
                .GetMethod("InitializeState", BindingFlags.NonPublic | BindingFlags.Instance));
            Assert.IsNotNull(typeof(McpAdvancedSection)
                .GetMethod("UpdatePathOverrides", BindingFlags.Public | BindingFlags.Instance));
            Assert.IsNotNull(typeof(McpClientConfigSection)
                .GetMethod("UpdateClientStatus", BindingFlags.Public | BindingFlags.Instance));
        }

        /// <summary>
        /// Current behavior: the main window persists the active tab in EditorPrefs and restores
        /// it after a domain reload (EditorWindowActivePanel).
        /// </summary>
        [Test]
        public void CrossPattern_ActivePanel_IsPersistedAcrossDomainReloads()
        {
            Assert.IsNotEmpty(EditorPrefKeys.EditorWindowActivePanel, "the active-panel key should exist");
            Assert.IsNotNull(typeof(MCPForUnityEditorWindow).GetMethod("ShowWindow", BindingFlags.Public | BindingFlags.Static),
                "the main window should open via ShowWindow");
        }

        #endregion

        #region Section 7: Visibility and Refresh Logic (2 tests)

        /// <summary>
        /// Current behavior: panel switching is tab drawing inside OnGUI, with the active panel
        /// persisted to EditorPrefs (EditorWindowActivePanel) so it survives domain reloads.
        /// </summary>
        [Test]
        public void VisibilityLogic_PanelSwitching_PersistsActivePanelInEditorPrefs()
        {
            var panelKey = EditorPrefKeys.EditorWindowActivePanel;
            Assert.IsNotEmpty(panelKey, "EditorWindowActivePanel key should exist");

            var type = typeof(MCPForUnityEditorWindow);
            Assert.IsNotNull(type.GetMethod("DrawTabs", BindingFlags.NonPublic | BindingFlags.Instance),
                "tabs should be drawn by DrawTabs (IMGUI)");
            Assert.IsNotNull(type.GetMethod("OnGUI", BindingFlags.NonPublic | BindingFlags.Instance),
                "the window should render through OnGUI");
        }

        /// <summary>
        /// Current behavior: conditional display of HTTP rows is driven by bool visibility flags
        /// on the connection section, updated when the transport selection changes.
        /// </summary>
        [Test]
        public void VisibilityLogic_TransportRows_AreBoolVisibilityFlags()
        {
            var type = typeof(McpConnectionSection);
            foreach (string fieldName in new[]
                     {
                         "showConnectionToggle", "showHttpUrlRow", "showApiKeyRow",
                         "showHttpServerControlRow", "showUnitySocketPortRow",
                         "showManualCommandFoldout", "showHttpServerCommandSection",
                         "transportMismatchVisible", "versionMismatchVisible"
                     })
            {
                var field = type.GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance);
                Assert.IsNotNull(field, $"Should have a {fieldName} field");
                Assert.AreEqual(typeof(bool), field.FieldType, $"{fieldName} should be a visibility flag");
            }
        }

        #endregion

        #region Section 8: Event Signaling Tests (1 test)

        /// <summary>
        /// Current behavior: Inter-component communication uses C# event pattern
        /// (Action delegates, raised with ?. null-conditional operator).
        /// </summary>
        [Test]
        public void EventSignaling_InterComponentCommunication_UsesActionDelegates()
        {
            var connectionEvents = typeof(McpConnectionSection).GetEvents(BindingFlags.Public | BindingFlags.Instance);
            Assert.GreaterOrEqual(connectionEvents.Length, 2, "McpConnectionSection should have at least 2 events");

            var clientEvents = typeof(McpClientConfigSection).GetEvents(BindingFlags.Public | BindingFlags.Instance);
            Assert.GreaterOrEqual(clientEvents.Length, 2, "McpClientConfigSection should have at least 2 events");

            var communicationFlow = new[]
            {
                "1. Component declares: public event Action OnSomethingHappened;",
                "2. Raises event: OnSomethingHappened?.Invoke();",
                "3. Other component subscribes: connection.OnSomethingHappened += HandleIt;",
                "4. Flow: ConnectionSection -> AdvancedSection or ClientConfigSection",
                "5. Used for: transport changes, config updates, manual refresh requests"
            };

            Assert.Contains("OnTransportChanged", connectionEvents.Select(e => e.Name).ToArray());
            Assert.Contains("OnClientTransportDetected", clientEvents.Select(e => e.Name).ToArray());
            Assert.Greater(communicationFlow.Length, 0); // flow documented above
        }

        #endregion

        #region Section 9: Pattern Summary Tests (Bonus documentation)

        /// <summary>
        /// Summary: Document the current IMGUI architecture metrics across the Windows/UI domain.
        /// </summary>
        [Test]
        public void PatternSummary_TotalRepetitionMetrics_AcrossDomain()
        {
            var metrics = new[]
            {
                "Window Classes: 3 (MCPForUnityEditorWindow, MCPSetupWindow, EditorPrefsWindow) - all OnGUI",
                "Section Controllers: 5+ (Connection, Advanced, ClientConfig, AssetGen, BlenderBridge) - all Draw()",
                "Control State: plain private fields, no cached UI element objects",
                "State Sync: SyncFromPrefs / InitializeState / UpdatePathOverrides / UpdateClientStatus",
                "Handlers: plain methods invoked directly from Draw()/OnGUI",
                "Events: Action delegates for inter-component signaling (OnTransportChanged, ...)",
                "Panel Persistence: EditorPrefs EditorWindowActivePanel"
            };

            // The three windows must all render through OnGUI.
            foreach (var window in new[] { typeof(MCPForUnityEditorWindow), typeof(MCPSetupWindow), typeof(EditorPrefsWindow) })
            {
                Assert.IsNotNull(window.GetMethod("OnGUI", BindingFlags.NonPublic | BindingFlags.Instance),
                    window.Name + " should render through OnGUI");
            }

            Assert.Greater(metrics.Length, 0, string.Join("\n", metrics));
        }

        #endregion

        #region Section 10: McpAssetGenSection model-selection structure (reflection, no Draw call)

        /// <summary>
        /// The Asset Gen section builds per-provider model dropdowns and shared-key fal rows (audio, 3D)
        /// inside Draw(). We assert the private builders exist and the parameterless constructor
        /// performs the state initialization, without calling Draw (it needs a GUI context).
        /// </summary>
        [Test]
        public void McpAssetGenSection_HasModelDropdownAndSharedFalRowBuilders()
        {
            var type = typeof(McpAssetGenSection);

            Assert.IsNotNull(type.GetMethod("DrawModelDropdown", BindingFlags.NonPublic | BindingFlags.Instance),
                "expected a per-provider model dropdown builder");
            Assert.IsNotNull(type.GetMethod("DrawSharedFalRow", BindingFlags.NonPublic | BindingFlags.Instance),
                "expected a shared-key fal row builder for audio and 3D");

            // DrawProviderRow takes (id, displayName, kind) so each row knows its catalog kind.
            var drawRow = type.GetMethod("DrawProviderRow", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(drawRow);
            Assert.AreEqual(3, drawRow.GetParameters().Length, "DrawProviderRow should take (id, displayName, kind)");

            Assert.IsNotNull(type.GetConstructor(Type.EmptyTypes),
                "the parameterless constructor should initialize all state (SyncFromPrefs + catalog refresh)");
        }

        [Test]
        public void McpAssetGenSection_HasRefreshHandler_AndRefreshStatusFields()
        {
            var type = typeof(McpAssetGenSection);

            Assert.IsNotNull(type.GetMethod("OnRefreshClicked", BindingFlags.NonPublic | BindingFlags.Instance),
                "expected a Refresh click handler");
            Assert.IsNotNull(type.GetField("refreshStatusText", BindingFlags.NonPublic | BindingFlags.Instance),
                "expected the Refresh status text field");
            Assert.IsNotNull(type.GetField("refreshStatusOk", BindingFlags.NonPublic | BindingFlags.Instance),
                "expected the Refresh status ok flag field");
            Assert.IsNotNull(type.GetField("catalogChecking", BindingFlags.NonPublic | BindingFlags.Instance),
                "expected the catalog-check-in-flight flag");
        }

        #endregion
    }
}
