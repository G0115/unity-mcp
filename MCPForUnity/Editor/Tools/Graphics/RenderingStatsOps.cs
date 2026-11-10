using System;
using System.Collections.Generic;
using System.Linq;
using MCPForUnity.Editor.Helpers;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Profiling;
using UProfiler = UnityEngine.Profiling.Profiler;

namespace MCPForUnity.Editor.Tools.Graphics
{
    internal static class RenderingStatsOps
    {
        private static readonly (string counterName, string jsonKey)[] COUNTER_MAP = new[]
        {
            ("Draw Calls Count", "draw_calls"),
            ("Batches Count", "batches"),
            ("SetPass Calls Count", "set_pass_calls"),
            ("Triangles Count", "triangles"),
            ("Vertices Count", "vertices"),
            ("Dynamic Batches Count", "dynamic_batches"),
            ("Dynamic Batched Draw Calls Count", "dynamic_batched_draw_calls"),
            ("Static Batches Count", "static_batches"),
            ("Static Batched Draw Calls Count", "static_batched_draw_calls"),
            ("Instanced Batches Count", "instanced_batches"),
            ("Instanced Batched Draw Calls Count", "instanced_batched_draw_calls"),
            ("Shadow Casters Count", "shadow_casters"),
            ("Render Textures Count", "render_textures"),
            ("Render Textures Bytes", "render_textures_bytes"),
            ("Used Textures Count", "used_textures"),
            ("Used Textures Bytes", "used_textures_bytes"),
            ("Render Textures Changes Count", "render_target_changes"),
            ("Visible Skinned Meshes Count", "visible_skinned_meshes"),
        };

        // === stats_get ===
        internal static object GetStats(JObject @params)
        {
            var stats = new Dictionary<string, object>();

#if UNITY_2020_2_OR_NEWER
            foreach (var (counterName, jsonKey) in COUNTER_MAP)
            {
                using (var recorder = Unity.Profiling.ProfilerRecorder.StartNew(Unity.Profiling.ProfilerCategory.Render, counterName))
                {
                    stats[jsonKey] = recorder.Valid ? recorder.CurrentValue : 0;
                }
            }
#else
            // Legacy fallback: UnityEditor.UnityStats exposes the same render counters
            // before the ProfilerRecorder API existed (pre-2020.2).
            stats["draw_calls"] = (long)UnityStats.drawCalls;
            stats["batches"] = (long)UnityStats.batches;
            stats["set_pass_calls"] = (long)UnityStats.setPassCalls;
            stats["triangles"] = (long)UnityStats.triangles;
            stats["vertices"] = (long)UnityStats.vertices;
            stats["dynamic_batches"] = (long)UnityStats.dynamicBatches;
            stats["static_batches"] = (long)UnityStats.staticBatches;
            stats["static_batched_draw_calls"] = (long)UnityStats.staticBatchedDrawCalls;
            stats["instanced_batches"] = (long)UnityStats.instancedBatches;
            stats["shadow_casters"] = (long)UnityStats.shadowCasters;
            stats["render_textures"] = (long)UnityStats.renderTextureCount;
            stats["render_textures_bytes"] = (long)UnityStats.renderTextureBytes;
            stats["used_textures"] = (long)UnityStats.usedTextureCount;
            stats["used_textures_bytes"] = (long)UnityStats.usedTextureMemorySize;
            stats["render_target_changes"] = (long)UnityStats.renderTextureChanges;
            stats["visible_skinned_meshes"] = (long)UnityStats.visibleSkinnedMeshes;
#endif

            return new
            {
                success = true,
                message = "Rendering stats captured.",
                data = stats
            };
        }

        // === stats_list_counters ===
        internal static object ListCounters(JObject @params)
        {
#if UNITY_2020_2_OR_NEWER
            var p = new ToolParams(@params);
            string categoryName = p.Get("category");

            // Default to "Render" category to avoid massive payloads (all categories = 300K+ chars)
            Unity.Profiling.ProfilerCategory category = Unity.Profiling.ProfilerCategory.Render;
            if (!string.IsNullOrEmpty(categoryName))
            {
                category = TryResolveCategory(categoryName);
            }

            var allHandles = new List<Unity.Profiling.LowLevel.Unsafe.ProfilerRecorderHandle>();
            Unity.Profiling.LowLevel.Unsafe.ProfilerRecorderHandle.GetAvailable(allHandles);
            var counters = allHandles
                .Select(h => Unity.Profiling.LowLevel.Unsafe.ProfilerRecorderHandle.GetDescription(h))
                .Where(d => string.Equals(d.Category.Name, category.Name, StringComparison.OrdinalIgnoreCase))
                .Select(d => new
                {
                    name = d.Name,
                    category = d.Category.Name,
                    unit = d.UnitType.ToString()
                })
                .OrderBy(c => c.name).ToList();

            return new
            {
                success = true,
                message = $"Found {counters.Count} counters in category '{category.Name}'.",
                data = new { counters }
            };
#else
            // No counters discovery API pre-2020.2 — expose the UnityStats keys instead.
            var counters = new[] { "draw_calls", "batches", "set_pass_calls", "triangles", "vertices",
                "dynamic_batches", "static_batches", "static_batched_draw_calls", "instanced_batches",
                "shadow_casters", "render_textures", "render_textures_bytes", "used_textures",
                "used_textures_bytes", "render_target_changes", "visible_skinned_meshes" }
                .Select(n => new { name = n, category = "Render", unit = "Unknown" })
                .OrderBy(c => c.name).ToList();

            return new
            {
                success = true,
                message = "Found " + counters.Count() + " legacy stats (UnityStats; ProfilerRecorder counters need Unity 2020.2+).",
                data = new { counters }
            };
#endif
        }

        // === stats_set_scene_debug_mode ===
        internal static object SetSceneDebugMode(JObject @params)
        {
            var p = new ToolParams(@params);
            string modeName = p.Get("mode");
            if (string.IsNullOrEmpty(modeName))
            {
                var validModes = string.Join(", ", Enum.GetNames(typeof(DrawCameraMode)).Take(20));
                return new ErrorResponse(
                    $"'mode' parameter required. Options: {validModes}");
            }

            if (!Enum.TryParse<DrawCameraMode>(modeName, true, out var drawMode))
            {
                var validModes = string.Join(", ", Enum.GetNames(typeof(DrawCameraMode)).Take(20));
                return new ErrorResponse($"Unknown mode '{modeName}'. Valid: {validModes}");
            }

            var sceneView = SceneView.lastActiveSceneView;
            if (sceneView == null)
                return new ErrorResponse("No active Scene View found.");

            sceneView.cameraMode = SceneView.GetBuiltinCameraMode(drawMode);

            sceneView.Repaint();

            return new
            {
                success = true,
                message = $"Scene debug mode set to '{drawMode}'."
            };
        }

        // === stats_get_memory ===
        internal static object GetMemory(JObject @params)
        {
            var data = new Dictionary<string, object>
            {
                ["totalAllocatedMB"] = Math.Round(UProfiler.GetTotalAllocatedMemoryLong() / (1024.0 * 1024.0), 2),
                ["totalReservedMB"] = Math.Round(UProfiler.GetTotalReservedMemoryLong() / (1024.0 * 1024.0), 2),
                ["totalUnusedReservedMB"] = Math.Round(UProfiler.GetTotalUnusedReservedMemoryLong() / (1024.0 * 1024.0), 2),
                ["monoUsedMB"] = Math.Round(UProfiler.GetMonoUsedSizeLong() / (1024.0 * 1024.0), 2),
                ["monoHeapMB"] = Math.Round(UProfiler.GetMonoHeapSizeLong() / (1024.0 * 1024.0), 2),
                ["graphicsDriverMB"] = Math.Round(UProfiler.GetAllocatedMemoryForGraphicsDriver() / (1024.0 * 1024.0), 2),
            };

            return new
            {
                success = true,
                message = "Memory stats captured.",
                data
            };
        }

#if UNITY_2020_2_OR_NEWER
        // --- Helper: Try to resolve a ProfilerCategory by name ---
        private static Unity.Profiling.ProfilerCategory TryResolveCategory(string name)
        {
            // ProfilerCategory has static properties for well-known categories
            switch (name.ToLowerInvariant())
            {
                case "render": return Unity.Profiling.ProfilerCategory.Render;
                case "scripts": return Unity.Profiling.ProfilerCategory.Scripts;
                case "memory": return Unity.Profiling.ProfilerCategory.Memory;
                case "physics": return Unity.Profiling.ProfilerCategory.Physics;
                case "animation": return Unity.Profiling.ProfilerCategory.Animation;
                case "audio": return Unity.Profiling.ProfilerCategory.Audio;
                case "lighting": return Unity.Profiling.ProfilerCategory.Lighting;
                case "network": return Unity.Profiling.ProfilerCategory.Network;
                case "gui": return Unity.Profiling.ProfilerCategory.Gui;
                case "ai": return Unity.Profiling.ProfilerCategory.Ai;
                case "video": return Unity.Profiling.ProfilerCategory.Video;
                case "loading": return Unity.Profiling.ProfilerCategory.Loading;
                case "input": return Unity.Profiling.ProfilerCategory.Input;
                case "vr": return Unity.Profiling.ProfilerCategory.Vr;
                case "internal": return Unity.Profiling.ProfilerCategory.Internal;
                default: return Unity.Profiling.ProfilerCategory.Render;
            }
        }
#endif
    }
}
