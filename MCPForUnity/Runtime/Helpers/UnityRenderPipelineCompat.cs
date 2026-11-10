using UnityEngine.Rendering;

namespace MCPForUnity.Runtime.Helpers
{
    /// <summary>
    /// Version compat for reading the active ScriptableRenderPipeline asset.
    /// Routed through this shim so callers never hard-code the property name:
    ///   • 2021.2+  <see cref="GraphicsSettings.currentRenderPipeline"/> (quality-level aware)
    ///   • 2020.1+  <see cref="GraphicsSettings.defaultRenderPipeline"/>
    ///   • 2018.1+  <see cref="GraphicsSettings.renderPipelineAsset"/>
    /// </summary>
    public static class UnityRenderPipelineCompat
    {
        /// <summary>The active SRP asset, or null when the Built-in pipeline is active.</summary>
        public static RenderPipelineAsset GetCurrentRenderPipeline()
        {
#if UNITY_2021_2_OR_NEWER
            return GraphicsSettings.currentRenderPipeline;
#elif UNITY_2020_1_OR_NEWER
            return GraphicsSettings.defaultRenderPipeline;
#else
            return GraphicsSettings.renderPipelineAsset;
#endif
        }
    }
}
