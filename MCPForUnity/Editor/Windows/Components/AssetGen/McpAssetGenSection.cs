using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using MCPForUnity.Editor.Helpers;
using MCPForUnity.Editor.Security;
using MCPForUnity.Editor.Services.AssetGen;
using MCPForUnity.Editor.Services.AssetGen.Import;
using UnityEditor;
using UnityEngine;

namespace MCPForUnity.Editor.Windows.Components.AssetGen
{
    /// <summary>
    /// Controller for the AI Asset Generation settings tab. This tab is CONFIG ONLY:
    /// it lets users enter/clear per-provider API keys, toggle providers on/off,
    /// presence-check a key, and set non-secret generation preferences.
    /// Generation itself is never triggered here — only via MCP tools / CLI. The one exception is
    /// the Blender Bridge block (<see cref="McpBlenderBridgePanel"/>): its buttons run local socket
    /// and file operations against a Blender on this machine, never a paid provider call.
    ///
    /// Keys are written to the OS secure store (<see cref="SecureKeyStore"/>), never to
    /// EditorPrefs or the project. The stored key is never read back into the field; only
    /// its presence is surfaced through the status label.
    ///
    /// IMGUI implementation: the window calls <see cref="Draw"/> each OnGUI while this tab is
    /// active. All state lives in plain fields (never in controls), so catalog refreshes can
    /// never replace or clear an in-progress API-key input.
    /// </summary>
    public class McpAssetGenSection
    {
        // Fixed provider lists. Each Id is both the SecureKeyStore key and the
        // AssetGenPrefs enable-flag id. All model/marketplace providers below emit GLB.
        // fal's key and enable toggle live on its 2D Images row; the 3D row only picks a model.
        private static readonly (string Id, string Label)[] ModelProviders =
        {
            ("tripo", "Tripo"),
            ("meshy", "Meshy"),
            ("fal", "fal (3D)"),
            ("sketchfab", "Sketchfab"),
        };

        // Editor dropdowns open a native menu with no search, so long catalogs are capped and
        // the per-provider search field reaches the remaining models.
        internal const int MenuLimit = 25;

        private static readonly (string Id, string Label)[] ImageProviders =
        {
            ("fal", "fal"),
            ("openrouter", "OpenRouter"),
        };

        private const string KeyControlPrefix = "assetgen-key-";
        private const string OutputRootControl = "assetgen-output-root";
        private const double AutoRefreshIntervalSeconds = 60.0;

        private static readonly GUIStyle BoldLabelStyle = new GUIStyle(EditorStyles.label) { fontStyle = FontStyle.Bold };
        private static readonly GUIStyle OkHelpStyle = MakeHelpStyle(new Color(0.4f, 0.8f, 0.4f));
        private static readonly GUIStyle DimHelpStyle = MakeHelpStyle(new Color(0.7f, 0.7f, 0.7f));
        private static readonly GUIStyle WarnHelpStyle = MakeHelpStyle(new Color(0.85f, 0.45f, 0.1f));
        private static GUIStyle placeholderStyle;

        private McpBlenderBridgePanel blenderPanel;

        private readonly Dictionary<string, string> searches = new Dictionary<string, string>();
        // Last failed compatibility check per kind/provider, kept so a catalog rebuild cannot hide it.
        private readonly Dictionary<string, (string Id, string Error)> verifyErrors = new Dictionary<string, (string Id, string Error)>();
        // Verified ModelEntry per kind/provider from the latest successful compatibility check.
        private readonly Dictionary<string, ModelEntry> verifiedResults = new Dictionary<string, ModelEntry>();
        // kind/provider keys with a compatibility check in flight (meta shows "Checking compatibility…").
        private readonly HashSet<string> pendingVerify = new HashSet<string>();
        // Typed-but-unsaved API keys per provider id; cleared after a successful save.
        private readonly Dictionary<string, string> keyEdits = new Dictionary<string, string>();
        // Transient per-provider status lines set by Save/Clear/Test; missing entries fall back to
        // secure-store presence, exactly like the freshly built rows of the Toolkit original.
        private readonly Dictionary<string, (string Text, bool Ok)> statusOverrides = new Dictionary<string, (string Text, bool Ok)>();

        private string outputRootEdit = string.Empty;
        private bool showGltfastNotice;
        private string refreshStatusText = string.Empty;
        private bool refreshStatusOk = true;
        private bool catalogChecking;
        private string lastFocusedControl = string.Empty;

        // 60-second automatic catalog refresh, polled from EditorApplication.update; the hook
        // unhooks itself once the host window is gone, so nothing is left registered permanently.
        private bool autoRefreshHooked;
        private double nextAutoRefreshAt;

        // Deferred SyncFromPrefs: a one-shot temporary EditorApplication.update callback.
        private bool syncQueued;
        private EditorApplication.CallbackFunction syncHandler;

        public McpAssetGenSection()
        {
            blenderPanel = new McpBlenderBridgePanel();
            SyncFromPrefs();
            EnsureAutoRefreshHooked();
            _ = RefreshCatalog(false);
        }

        /// <summary>
        /// Draws the whole Asset Generation tab with IMGUI, in McpAssetGenSection.uxml order.
        /// Called by the host window while this tab is the active one.
        /// </summary>
        public void Draw()
        {
            CommitFocusLostEdits();

            EditorGUILayout.LabelField("AI Asset Generation", BoldLabelStyle);
            EditorGUILayout.LabelField(
                "Enter your own provider API keys. Generation is triggered via MCP tools / CLI, not here. " +
                "Keys are stored in your OS secure store (Keychain / Credential Manager / libsecret), never in the project.",
                EditorStyles.wordWrappedMiniLabel);

            if (showGltfastNotice)
            {
                EditorGUILayout.HelpBox("GLB import needs the glTFast package — install it from the Dependencies tab.", MessageType.Warning);
            }

            EditorGUILayout.BeginHorizontal();
            using (new EditorGUI.DisabledScope(catalogChecking))
            {
                if (GUILayout.Button(new GUIContent("Refresh", "Refresh fal image, sound and 3D models, and OpenRouter images."),
                    EditorStyles.miniButton, GUILayout.ExpandWidth(false)))
                {
                    OnRefreshClicked();
                }
            }
            EditorGUILayout.LabelField(new GUIContent(refreshStatusText), refreshStatusOk ? OkHelpStyle : DimHelpStyle, GUILayout.MinWidth(0));
            EditorGUILayout.EndHorizontal();

            GUILayout.Space(4);

            EditorGUILayout.BeginVertical(GUI.skin.box);
            EditorGUILayout.LabelField("3D Models", BoldLabelStyle);
            foreach (var provider in ModelProviders)
            {
                if (provider.Id == "fal")
                {
                    // No toggle of its own: the glTFast notice reads the 2D fal row's enable pref.
                    DrawSharedFalRow("model", provider.Label);
                }
                else
                {
                    DrawProviderRow(provider.Id, provider.Label, "model");
                }
            }
            EditorGUILayout.EndVertical();

            EditorGUILayout.BeginVertical(GUI.skin.box);
            EditorGUILayout.LabelField("2D Images", BoldLabelStyle);
            foreach (var provider in ImageProviders)
            {
                DrawProviderRow(provider.Id, provider.Label, "image");
            }
            EditorGUILayout.EndVertical();

            EditorGUILayout.BeginVertical(GUI.skin.box);
            EditorGUILayout.LabelField("Sound (fal.ai)", BoldLabelStyle);
            DrawSharedFalRow("audio", "fal (audio)");
            EditorGUILayout.EndVertical();

            if (blenderPanel != null)
            {
                blenderPanel.Draw();
            }

            GUILayout.Space(4);
            EditorGUILayout.LabelField("Preferences", BoldLabelStyle);

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Default 3D Format:", GUILayout.ExpandWidth(false));
            string[] formatChoices = { "glb", "fbx", "obj" };
            int formatIndex = Array.IndexOf(formatChoices, NormalizeFormat(AssetGenPrefs.DefaultFormat));
            if (formatIndex < 0) formatIndex = 0;
            int newFormatIndex = EditorGUILayout.Popup(formatIndex, formatChoices);
            if (newFormatIndex != formatIndex)
            {
                AssetGenPrefs.DefaultFormat = formatChoices[newFormatIndex];
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.LabelField("Output Root:", GUILayout.ExpandWidth(false));
            outputRootEdit = DrawTextFieldWithPlaceholder(outputRootEdit, "Assets/Generated", OutputRootControl,
                $"Project-relative folder where generated assets are written. Empty = {AssetGenPrefs.DefaultOutputRoot}.",
                GUILayout.ExpandWidth(true));

            EditorGUILayout.BeginHorizontal();
            bool autoNormalize = AssetGenPrefs.AutoNormalize;
            bool newAutoNormalize = EditorGUILayout.Toggle(
                new GUIContent("Auto-Normalize Imported Models:", "Uniformly scale imported models to the target size on import."),
                autoNormalize);
            if (newAutoNormalize != autoNormalize)
            {
                AssetGenPrefs.AutoNormalize = newAutoNormalize;
            }
            EditorGUILayout.EndHorizontal();
        }

        /// <summary>
        /// Re-reads key presence and forces a nonblocking fal catalog refresh.
        /// </summary>
        private void OnRefreshClicked()
        {
            SyncFromPrefs();
            _ = RefreshCatalog(true);
        }

        /// <summary>
        /// Re-reads secure-store presence and prefs and redraws. Called when the
        /// tab becomes visible so keys set elsewhere (e.g. via CLI) are reflected.
        /// </summary>
        public void Refresh()
        {
            SyncFromPrefs();
            if (blenderPanel != null) blenderPanel.Refresh();
            _ = RefreshCatalog(false);
        }

        private async Task RefreshCatalog(bool force)
        {
            catalogChecking = true;
            refreshStatusText = "Checking model catalogs…";
            refreshStatusOk = true;
            try
            {
                // Unchanged catalogs leave any unsaved API-key input alone: nothing here touches
                // the key edit buffers.
                await Task.WhenAll(FalModelCatalog.RefreshAsync("image", force), FalModelCatalog.RefreshAsync("audio", force),
                    FalModelCatalog.RefreshAsync("model", force), OpenRouterModelCatalog.RefreshAsync(force));
                string error = FalModelCatalog.LastError("image") ?? FalModelCatalog.LastError("audio") ?? FalModelCatalog.LastError("model") ?? OpenRouterModelCatalog.LastError;
                string verified = FalModelCatalog.VerifiedAt("audio") ?? FalModelCatalog.VerifiedAt("image");
                string when = DateTime.TryParse(verified, out var time) ? time.ToLocalTime().ToString("g") : "unknown";
                refreshStatusText = error != null ? error : "Catalogs checked " + when + " · refreshed automatically every 24 hours";
                refreshStatusOk = error == null;
            }
            finally
            {
                catalogChecking = false;
            }
        }

        private void EnsureAutoRefreshHooked()
        {
            if (autoRefreshHooked) return;
            autoRefreshHooked = true;
            nextAutoRefreshAt = EditorApplication.timeSinceStartup + AutoRefreshIntervalSeconds;
            EditorApplication.update += AutoRefreshTick;
        }

        /// <summary>
        /// EditorApplication.update poll for the 60-second automatic catalog refresh.
        /// Same guard as the original: never fires while a catalog refresh is already running.
        /// Once the host window is closed, the hook removes itself so nothing stays registered.
        /// </summary>
        private void AutoRefreshTick()
        {
            if (EditorApplication.timeSinceStartup < nextAutoRefreshAt) return;
            nextAutoRefreshAt = EditorApplication.timeSinceStartup + AutoRefreshIntervalSeconds;

            if (Resources.FindObjectsOfTypeAll<MCPForUnityEditorWindow>().Length == 0)
            {
                EditorApplication.update -= AutoRefreshTick;
                autoRefreshHooked = false;
                return;
            }

            if (FalModelCatalog.IsRefreshing("image") || FalModelCatalog.IsRefreshing("audio")
                || FalModelCatalog.IsRefreshing("model") || OpenRouterModelCatalog.IsRefreshing)
            {
                return;
            }
            _ = RefreshCatalog(false);
        }

        /// <summary>
        /// Commits an output-root or API-key edit the moment its control loses keyboard focus —
        /// the IMGUI equivalent of the original FocusOutEvent callbacks.
        /// </summary>
        private void CommitFocusLostEdits()
        {
            string focused = GUI.GetNameOfFocusedControl();
            if (focused == lastFocusedControl) return;

            if (lastFocusedControl == OutputRootControl)
            {
                CommitOutputRoot();
            }
            else if (lastFocusedControl != null && lastFocusedControl.StartsWith(KeyControlPrefix, StringComparison.Ordinal))
            {
                SaveKeyFromField(lastFocusedControl.Substring(KeyControlPrefix.Length));
            }
            lastFocusedControl = focused;
        }

        /// <summary>Re-reads prefs into the transient fields; rows are drawn from live state.</summary>
        private void SyncFromPrefs()
        {
            outputRootEdit = AssetGenPrefs.OutputRoot;
            statusOverrides.Clear();
            UpdateGltfastNotice();
        }

        private void CommitOutputRoot()
        {
            AssetGenPrefs.OutputRoot = outputRootEdit != null ? outputRootEdit.Trim() : null;
            // Reflect the normalized/default value (empty -> default) without re-triggering.
            outputRootEdit = AssetGenPrefs.OutputRoot;
        }

        private void DrawProviderRow(string id, string displayName, string kind)
        {
            EditorGUILayout.BeginVertical();

            // Header: bold provider name, key status inline to its right, enable toggle far right.
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(displayName, BoldLabelStyle, GUILayout.ExpandWidth(false));
            DrawKeyStatus(id);
            bool enabled = AssetGenPrefs.IsProviderEnabled(id);
            bool newEnabled = EditorGUILayout.Toggle(
                new GUIContent("Enabled", $"Enable the {displayName} provider for asset generation."), enabled);
            if (newEnabled != enabled)
            {
                AssetGenPrefs.SetProviderEnabled(id, newEnabled);
                UpdateGltfastNotice();
            }
            EditorGUILayout.EndHorizontal();

            // Masked key field + Save / Clear / Test buttons.
            EditorGUILayout.BeginHorizontal();
            string edit = keyEdits.TryGetValue(id, out string buffered) ? buffered : string.Empty;
            GUI.SetNextControlName(KeyControlPrefix + id);
            edit = EditorGUILayout.PasswordField(new GUIContent(edit,
                $"Paste your {displayName} API key, then press Save (or click away). " +
                "The key is stored in your OS secure store and is never read back into this field."),
                GUILayout.ExpandWidth(true));
            keyEdits[id] = edit;
            if (GUILayout.Button("Save", EditorStyles.miniButton))
            {
                SaveKeyFromField(id);
            }
            if (GUILayout.Button("Clear", EditorStyles.miniButton))
            {
                ClearKey(id);
            }
            if (GUILayout.Button("Test", EditorStyles.miniButton))
            {
                TestKey(id);
            }
            EditorGUILayout.EndHorizontal();

            // "Which model" selector for this provider (skipped for providers with no catalog
            // models, e.g. the Sketchfab marketplace).
            DrawModelDropdown(kind, id);

            GUILayout.Space(6);
            EditorGUILayout.EndVertical();
        }

        private void DrawKeyStatus(string id)
        {
            string text;
            bool ok;
            if (statusOverrides.TryGetValue(id, out (string Text, bool Ok) status))
            {
                text = status.Text;
                ok = status.Ok;
            }
            else
            {
                // Initial status reflects secure-store presence (existence only; never the value).
                bool has = HasKey(id);
                text = has ? "saved ✓" : "not set";
                ok = has;
            }
            EditorGUILayout.LabelField(text, ok ? OkHelpStyle : DimHelpStyle, GUILayout.MinWidth(0));
        }

        /// <summary>Persists the typed key, then clears the field so the secret is never displayed.</summary>
        private void SaveKeyFromField(string id)
        {
            string buffered;
            if (!keyEdits.TryGetValue(id, out buffered)) return;
            string text = buffered != null ? buffered.Trim() : null;
            if (string.IsNullOrEmpty(text)) return;

            try
            {
                SecureKeyStore.Current.Set(id, text);
                keyEdits[id] = string.Empty;
                statusOverrides[id] = ("saved ✓", true);
                RebuildIfSharedKey(id);
            }
            catch (Exception ex)
            {
                McpLog.Warn($"Failed to store {id} key: {ex.Message}");
                statusOverrides[id] = ("save failed", false);
            }
        }

        private void ClearKey(string id)
        {
            try
            {
                SecureKeyStore.Current.Delete(id);
            }
            catch (Exception ex)
            {
                McpLog.Warn($"Failed to delete {id} key: {ex.Message}");
            }

            keyEdits[id] = string.Empty;
            statusOverrides[id] = ("not set", false);
            RebuildIfSharedKey(id);
        }

        // v1 surfaces presence only. Live endpoint validation (an actual auth ping to the
        // provider) is a future enhancement and intentionally not performed here.
        private void TestKey(string id)
        {
            bool present = HasKey(id);
            statusOverrides[id] = (present ? "key present ✓" : "no key set", present);
        }

        /// <summary>
        /// Draws a "Model" dropdown + metadata lines for a (kind, provider) pair, if the catalog has
        /// any models for it. The dropdown shows friendly labels; the pref stores the model id.
        /// Selecting a model becomes the default that generate_* uses when no explicit model is passed.
        /// </summary>
        private void DrawModelDropdown(string kind, string providerId)
        {
            bool live = providerId == "fal" || providerId == "openrouter";
            // Bundled-only providers with no models (the Sketchfab marketplace) get no selector at all.
            if (!live && AssetGenModelCatalog.ForProvider(providerId, kind).Count == 0) return;

            string key = kind + "/" + providerId;
            string term = searches.TryGetValue(key, out string stored) ? stored : "";

            if (live)
            {
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField("Search", GUILayout.ExpandWidth(false));
                string newTerm = EditorGUILayout.TextField(term);
                if (newTerm != term)
                {
                    searches[key] = newTerm ?? "";
                }
                EditorGUILayout.EndHorizontal();
                term = newTerm ?? "";
            }

            var all = AssetGenModelCatalog.ForProvider(providerId, kind);
            string selectedId = AssetGenPrefs.GetSelectedModel(kind, providerId);
            if (string.IsNullOrEmpty(selectedId)) selectedId = AssetGenModelCatalog.DefaultModelId(providerId, kind);
            var matches = all.Where(m => (m.Id + " " + m.Label + " " + m.UseCase).IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            // Catalog order puts bundled and eagerly verified models first, so the cap keeps those.
            var models = matches.Take(MenuLimit).ToList();
            ModelEntry selected = all.FirstOrDefault(model => model.Id == selectedId);
            if (selected != null && !models.Contains(selected)) models.Insert(0, selected);
            if (models.Count == 0)
            {
                EditorGUILayout.LabelField("No models found. Clear the search or refresh the catalog.", EditorStyles.wordWrappedMiniLabel);
                return;
            }

            var choices = new List<string>();
            foreach (ModelEntry m in models) choices.Add(m.Label + " (" + m.Id + ")");
            int selectedIndex = selected == null ? choices.Count : models.IndexOf(selected);
            if (selected == null)
                choices.Add(string.IsNullOrEmpty(selectedId) ? "Choose a model" : "Saved model unavailable — choose another (" + selectedId + ")");

            var displayed = new string[choices.Count];
            for (int i = 0; i < choices.Count; i++) displayed[i] = MenuItemText(choices[i]);

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Model", GUILayout.ExpandWidth(false));
            int newIndex = EditorGUILayout.Popup(selectedIndex, displayed);
            EditorGUILayout.EndHorizontal();

            if (newIndex != selectedIndex && newIndex >= 0 && newIndex < models.Count)
            {
                ModelEntry picked = models[newIndex];
                AssetGenPrefs.SetSelectedModel(kind, providerId, picked.Id);
                verifyErrors.Remove(key);
                verifiedResults.Remove(key);
                if (providerId == "fal" || providerId == "openrouter") _ = VerifySelection(picked);
            }

            if (matches.Count > MenuLimit)
            {
                EditorGUILayout.LabelField($"The menu shows {MenuLimit} of {matches.Count} models. Use Search to find the others.",
                    EditorStyles.wordWrappedMiniLabel);
            }

            DrawModelMeta(key, selected, selectedId);
        }

        /// <summary>Draws the metadata line under a model dropdown plus the commercial-use caveat.</summary>
        private void DrawModelMeta(string key, ModelEntry selected, string selectedId)
        {
            string metaText;
            string tooltip = null;
            bool isError = false;

            if (selected != null)
            {
                if (pendingVerify.Contains(key))
                {
                    metaText = "Checking compatibility…";
                }
                else if (verifyErrors.TryGetValue(key, out (string Id, string Error) failure) && failure.Id == selected.Id)
                {
                    metaText = failure.Error;
                    isError = true;
                }
                else
                {
                    ModelEntry entry = verifiedResults.TryGetValue(key, out ModelEntry verified) && verified != null && verified.Id == selected.Id
                        ? verified
                        : selected;
                    metaText = BuildModelMetaText(entry, out tooltip);
                }
                if (tooltip == null) tooltip = selected.ModelUrl ?? selected.Id;
            }
            else if (string.IsNullOrEmpty(selectedId))
            {
                metaText = "Choose a model before generating.";
            }
            else
            {
                metaText = "Your saved selection is preserved. Choose an available model before generating.";
            }

            if (!string.IsNullOrEmpty(metaText))
            {
                EditorGUILayout.LabelField(new GUIContent(metaText, tooltip), isError ? WarnHelpStyle : EditorStyles.wordWrappedMiniLabel);
            }

            // A null selection hides the caveat line instead of leaving it empty.
            if (selected != null && !string.IsNullOrEmpty(selected.CommercialNote))
            {
                EditorGUILayout.LabelField(selected.CommercialNote, EditorStyles.wordWrappedMiniLabel);
            }
        }

        private async Task VerifySelection(ModelEntry picked)
        {
            string key = picked.Kind + "/" + picked.Provider;
            pendingVerify.Add(key);
            try
            {
                string apiKey = null;
                if (picked.Provider == "fal") try { SecureKeyStore.Current.TryGet("fal", out apiKey); } catch { }
                string mode = picked.Modes?.FirstOrDefault() ?? "text";
                var verified = picked.Provider == "fal"
                    ? await FalModelCatalog.VerifyForGeneration(picked.Id, picked.Kind, mode, CancellationToken.None, apiKey)
                    : await OpenRouterModelCatalog.VerifyForGeneration(picked.Id, mode, CancellationToken.None);
                if (AssetGenPrefs.GetSelectedModel(picked.Kind, picked.Provider) == picked.Id) verifiedResults[key] = verified;
            }
            catch (Exception error)
            {
                if (AssetGenPrefs.GetSelectedModel(picked.Kind, picked.Provider) != picked.Id) return;
                string message = "Compatibility check failed: " + SecretRedactor.Scrub(error.Message);
                verifyErrors[key] = (picked.Id, message);
            }
            finally
            {
                pendingVerify.Remove(key);
            }
        }

        /// <summary>
        /// Editor dropdowns open a native GenericMenu, which reads every '/' in a model id as a
        /// submenu separator. The menu shows a look-alike slash; the field keeps the real text.
        /// </summary>
        internal static string MenuItemText(string choice) => choice.Replace('/', '\u2215');

        private static string BuildModelMetaText(ModelEntry m, out string tooltip)
        {
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(m.UseCase)) parts.Add(m.UseCase);
            if (!string.IsNullOrEmpty(m.PriceLabel)) parts.Add(m.PriceLabel);
            // Only surface the "≤Ns" hint for models with an actual duration control (DurationField);
            // Lyria advertises a max but takes no duration input, so showing a hint would mislead.
            if (m.MaxDurationSeconds > 0f && !string.IsNullOrEmpty(m.DurationField)) parts.Add($"≤{m.MaxDurationSeconds:0}s");
            if (m.Loopable) parts.Add("loopable");
            // Only live-catalog providers have a verification state; Tripo/Meshy are bundled by design.
            if (m.Provider == "fal" || m.Provider == "openrouter")
                parts.Add(m.VerifiedAt != null ? "compatibility verified" : m.FromRefresh ? "discovered · checked before generation" : "bundled · checked before generation");
            if (!string.IsNullOrEmpty(m.LicenseType)) parts.Add("license: " + m.LicenseType);
            tooltip = m.ModelUrl ?? m.Id;
            return string.Join(" · ", parts);
        }

        /// <summary>
        /// fal audio / 3D row: no enable toggle and no key field — these kinds reuse the single fal
        /// key owned by the Image "fal" row. Surfaces that key's presence and a model dropdown.
        /// </summary>
        private void DrawSharedFalRow(string kind, string displayName)
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(displayName, BoldLabelStyle, GUILayout.ExpandWidth(false));
            bool hasFal = HasKey("fal");
            EditorGUILayout.LabelField(hasFal ? "key present ✓ (shared with 2D fal)" : "no fal key — set it in 2D Images",
                hasFal ? OkHelpStyle : DimHelpStyle, GUILayout.MinWidth(0));
            EditorGUILayout.EndHorizontal();

            DrawModelDropdown(kind, "fal");

            GUILayout.Space(6);
        }

        /// <summary>
        /// The fal key is shared with the audio and 3D rows. When the 2D fal key is saved/cleared,
        /// queue a deferred SyncFromPrefs so shared state reflects it without a manual Refresh —
        /// the Toolkit original deferred this through the element scheduler.
        /// </summary>
        private void RebuildIfSharedKey(string id)
        {
            if (!string.Equals(id, "fal", StringComparison.OrdinalIgnoreCase)) return;
            QueueSyncFromPrefs();
        }

        /// <summary>Runs SyncFromPrefs once on the next editor update, then unregisters.</summary>
        private void QueueSyncFromPrefs()
        {
            if (syncQueued) return;
            syncQueued = true;
            syncHandler = () =>
            {
                EditorApplication.update -= syncHandler;
                syncHandler = null;
                syncQueued = false;
                SyncFromPrefs();
            };
            EditorApplication.update += syncHandler;
        }

        private void UpdateGltfastNotice()
        {
            bool anyGlbProviderEnabled = false;
            foreach (var provider in ModelProviders)
            {
                if (AssetGenPrefs.IsProviderEnabled(provider.Id))
                {
                    anyGlbProviderEnabled = true;
                    break;
                }
            }

            showGltfastNotice = anyGlbProviderEnabled && !ModelImportPipeline.IsGltfastAvailable();
        }

        private static bool HasKey(string id)
        {
            try { return SecureKeyStore.Current.Has(id); }
            catch { return false; }
        }

        private static string NormalizeFormat(string format)
        {
            switch ((format ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "glb":
                case "fbx":
                case "obj":
                    return format.Trim().ToLowerInvariant();
                default:
                    return AssetGenPrefs.DefaultFormatValue;
            }
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

        private static GUIStyle MakeHelpStyle(Color color)
        {
            return new GUIStyle(EditorStyles.wordWrappedMiniLabel)
            {
                normal = { textColor = color }
            };
        }
    }
}
