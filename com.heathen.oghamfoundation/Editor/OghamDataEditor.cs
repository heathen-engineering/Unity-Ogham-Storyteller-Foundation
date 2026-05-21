using UnityEditor;
using UnityEngine;
using Heathen.Ogham;

namespace Heathen.Ogham.Editor
{
    [CustomEditor(typeof(OghamData))]
    public class OghamDataEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            EditorGUILayout.Space(4);
            if (GUILayout.Button("Open in Graph Editor"))
                OghamGraphEditorWindow.OpenAsset((OghamData)target);
        }
    }
}
