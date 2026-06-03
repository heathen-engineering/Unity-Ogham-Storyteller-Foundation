using System;
using UnityEditor;
using UnityEngine;

namespace Heathen.Ogham.Editor
{
    // Small floating popup for editing per-node Director Notes.
    internal class OghamNotesWindow : EditorWindow
    {
        private string         _notes  = "";
        private Action<string> _onCommit;
        private Vector2        _scroll;
        private bool           _closing;
        private GUIStyle       _textAreaStyle;

        internal static void Open(string current, Action<string> onCommit, Vector2 screenAnchor)
        {
            var w = CreateInstance<OghamNotesWindow>();
            w.titleContent = new GUIContent("Director Notes");
            w._notes       = current ?? "";
            w._onCommit    = onCommit;
            w.minSize      = new Vector2(320f, 160f);
            w.maxSize      = new Vector2(480f, 320f);
            w.position     = new Rect(screenAnchor.x, screenAnchor.y, 360f, 200f);
            w.ShowUtility();
            w.Focus();
        }

        private void OnGUI()
        {
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Director notes for this node (VO export only):",
                EditorStyles.miniLabel);
            EditorGUILayout.Space(2);

            if (_textAreaStyle == null)
                _textAreaStyle = new GUIStyle(EditorStyles.textArea) { wordWrap = true };

            _scroll = EditorGUILayout.BeginScrollView(_scroll, GUILayout.ExpandHeight(true));
            _notes  = EditorGUILayout.TextArea(_notes, _textAreaStyle, GUILayout.ExpandHeight(true));
            EditorGUILayout.EndScrollView();

            EditorGUILayout.Space(4);
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Cancel", GUILayout.Width(70)))
                {
                    _closing = true;
                    Close();
                }
                if (GUILayout.Button("Save", GUILayout.Width(70)))
                    Commit();
            }
            EditorGUILayout.Space(4);

            if (Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Escape)
            {
                _closing = true;
                Close();
                Event.current.Use();
            }
        }

        private void OnLostFocus() { if (!_closing) Commit(); }

        private void Commit()
        {
            if (_closing) return;
            _closing = true;
            _onCommit?.Invoke(_notes);
            Close();
        }
    }
}
