using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;

namespace Heathen.Ogham.Editor
{
    /// <summary>
    /// Recompiles every <see cref="OghamCompiledData"/> asset in the project before each player build,
    /// ensuring the runtime asset reflects the latest source <see cref="OghamData"/> files.
    /// </summary>
    internal class OghamBuildProcessor : IPreprocessBuildWithReport
    {
        /// <summary>The callback order for this pre-process build step. Runs first (order 0).</summary>
        public int callbackOrder => 0;

        /// <summary>
        /// Locates all <see cref="OghamCompiledData"/> assets in the project, recompiles each one,
        /// and saves any that were dirtied during compilation.
        /// </summary>
        /// <param name="report">The Unity build report for the current build.</param>
        public void OnPreprocessBuild(BuildReport report)
        {
            var guids = AssetDatabase.FindAssets("t:OghamCompiledData");
            foreach (var guid in guids)
            {
                var path     = AssetDatabase.GUIDToAssetPath(guid);
                var compiled = AssetDatabase.LoadAssetAtPath<OghamCompiledData>(path);
                if (compiled == null) continue;

                compiled.Compile();
                AssetDatabase.SaveAssetIfDirty(compiled);
            }
        }
    }
}
