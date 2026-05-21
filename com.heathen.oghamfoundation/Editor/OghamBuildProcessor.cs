using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;

namespace Heathen.Ogham.Editor
{
    // Recompiles every OghamCompiledData asset in the project before each player build,
    // ensuring the runtime asset reflects the latest source OghamData files.
    internal class OghamBuildProcessor : IPreprocessBuildWithReport
    {
        public int callbackOrder => 0;

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
