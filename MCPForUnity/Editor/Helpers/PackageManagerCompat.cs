using System;
using System.Reflection;
using UnityEditor;
using UnityEditor.PackageManager;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace MCPForUnity.Editor.Helpers
{
    /// <summary>
    /// Version-tolerant PackageManager helpers.
    /// - PackageInfo.GetAllRegisteredPackages(): public since 2021.2; 2019.4/2020.x only expose
    ///   PackageInfo.GetAll() (internal in 2019.4) — resolved via reflection.
    /// - Client.Resolve(): public since 2020.1; internal in 2019.4 — resolved via reflection.
    /// </summary>
    internal static class PackageManagerCompat
    {
        /// <summary>Returns all registered packages, or an empty array when the API cannot be resolved.</summary>
        internal static PackageInfo[] GetAllRegisteredPackages()
        {
#if UNITY_2021_2_OR_NEWER
            return PackageInfo.GetAllRegisteredPackages();
#else
            try
            {
                var mi = typeof(PackageInfo).GetMethod(
                    "GetAll",
                    BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public,
                    null,
                    Type.EmptyTypes,
                    null);
                if (mi?.Invoke(null, null) is PackageInfo[] result)
                    return result;
            }
            catch (Exception e)
            {
                McpLog.Warn($"PackageManagerCompat: PackageInfo.GetAll failed: {e.Message}");
            }
            return new PackageInfo[0];
#endif
        }

        /// <summary>
        /// Triggers package re-resolution of the manifest. No-op with a warning when the
        /// editor version does not expose a usable resolve API (Unity re-resolves on focus).
        /// </summary>
        internal static void Resolve()
        {
#if UNITY_2020_1_OR_NEWER
            Client.Resolve();
#else
            try
            {
                var mi = typeof(Client).GetMethod(
                    "Resolve",
                    BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public,
                    null,
                    Type.EmptyTypes,
                    null);
                if (mi != null)
                {
                    mi.Invoke(null, null);
                    return;
                }
            }
            catch (Exception e)
            {
                McpLog.Warn($"PackageManagerCompat: Client.Resolve failed: {e.Message}");
                return;
            }
            McpLog.Warn("PackageManagerCompat: Client.Resolve is not available; Unity will re-resolve packages on next editor focus.");
#endif
        }
    }
}
