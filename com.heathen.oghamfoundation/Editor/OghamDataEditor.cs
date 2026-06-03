using UnityEditor;
using UnityEngine;
using Heathen.Ogham;

namespace Heathen.Ogham.Editor
{
    /// <summary>
    /// Custom Inspector for <see cref="OghamData"/> assets. Replaces the default property drawer with a single
    /// button that opens the asset in the Ogham graph editor window.
    /// </summary>
    [CustomEditor(typeof(OghamData))]
    public class OghamDataEditor : UnityEditor.Editor
    {
        /// <summary>Draws the Inspector GUI, showing an "Open in Graph Editor" button.</summary>
        public override void OnInspectorGUI()
        {
            EditorGUILayout.Space(4);
            if (GUILayout.Button("Open in Graph Editor"))
                OghamGraphEditorWindow.OpenAsset((OghamData)target);
        }
    }
}
