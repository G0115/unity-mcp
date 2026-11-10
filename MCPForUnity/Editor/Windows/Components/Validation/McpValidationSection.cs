using System;
using MCPForUnity.Editor.Constants;
using UnityEditor;
using UnityEngine;

namespace MCPForUnity.Editor.Windows.Components.Validation
{
    /// <summary>
    /// Controller for the Script Validation section.
    /// Handles script validation level settings.
    /// </summary>
    public class McpValidationSection
    {
        // UI state (IMGUI backing fields)
        private string validationDescriptionText = string.Empty;

        // Data
        private ValidationLevel currentValidationLevel = ValidationLevel.Standard;

        // Validation levels
        public enum ValidationLevel
        {
            Basic,
            Standard,
            Comprehensive,
            Strict
        }

        public McpValidationSection()
        {
            InitializeUI();
        }

        private void InitializeUI()
        {
            int savedLevel = EditorPrefs.GetInt(EditorPrefKeys.ValidationLevel, 1);
            currentValidationLevel = (ValidationLevel)Mathf.Clamp(savedLevel, 0, 3);
            UpdateValidationDescription();
        }

        /// <summary>
        /// Draws the Script Validation section (mirrors McpValidationSection.uxml layout).
        /// </summary>
        public void Draw()
        {
            GUILayout.Label("Script Validation", EditorStyles.boldLabel);

            GUILayout.Label("Validation Level:", EditorStyles.miniLabel);

            ValidationLevel newLevel = (ValidationLevel)EditorGUILayout.EnumPopup(currentValidationLevel);
            if (newLevel != currentValidationLevel)
            {
                currentValidationLevel = newLevel;
                EditorPrefs.SetInt(EditorPrefKeys.ValidationLevel, (int)currentValidationLevel);
                UpdateValidationDescription();
            }

            GUILayout.Label(validationDescriptionText, EditorStyles.wordWrappedMiniLabel);
        }

        private void UpdateValidationDescription()
        {
            switch (currentValidationLevel)
            {
                case ValidationLevel.Basic:
                    validationDescriptionText = "Basic: Validates syntax only. Fast compilation checks.";
                    break;
                case ValidationLevel.Standard:
                    validationDescriptionText = "Standard (Recommended): Checks syntax + common errors. Balanced speed and coverage.";
                    break;
                case ValidationLevel.Comprehensive:
                    validationDescriptionText = "Comprehensive: Detailed validation including code quality. Slower but thorough.";
                    break;
                case ValidationLevel.Strict:
                    validationDescriptionText = "Strict: Maximum validation + warnings as errors. Slowest but catches all issues.";
                    break;
                default:
                    validationDescriptionText = "Unknown validation level";
                    break;
            }
        }
    }
}
