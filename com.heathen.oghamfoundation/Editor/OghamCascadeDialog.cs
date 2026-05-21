using System;
using UnityEditor;
using UnityEngine;

namespace Heathen.Ogham.Editor
{
    // Integer-input popup for the Cascade / Sequence feature.
    // Enter the total number of sequence nodes to create; must be ≥ 2.
    public class OghamCascadeDialog : EditorWindow
    {
        private int    _count = 3;
        private Action<int> _onConfirm;
        private bool   _focusSet;
        private bool   _closing;

        private const float W = 220f;
        private const float H = 62f;

        public static void Open(string entryTag, Action<int> onConfirm, Vector2 anchor)
        {
            var w = CreateInstance<OghamCascadeDialog>();
            w.titleContent = new GUIContent("Cascade");
            w._onConfirm   = onConfirm;
            w._closing     = false;
            w._focusSet    = false;
            w.minSize = w.maxSize = new Vector2(W, H);
            w.position = PlaceAtAnchor(anchor, W, H);
            w.ShowPopup();
            w.Focus();
        }

        private static Rect PlaceAtAnchor(Vector2 anchor, float w, float h)
        {
            var r   = new Rect(anchor.x, anchor.y, w, h);
            var res = Screen.currentResolution;
            if (r.xMax > res.width)  r.x = res.width  - w - 4f;
            if (r.x    < 0f)         r.x = 0f;
            if (r.yMax > res.height) r.y = res.height - h - 4f;
            if (r.y    < 0f)         r.y = 0f;
            return r;
        }

        private void OnGUI()
        {
            EditorGUILayout.Space(4f);

            GUI.SetNextControlName("countField");
            _count = EditorGUILayout.IntField("Sequence count", _count);
            if (_count < 2) _count = 2;

            if (!_focusSet) { GUI.FocusControl("countField"); _focusSet = true; }

            EditorGUILayout.Space(2f);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("OK")) Commit();
                if (GUILayout.Button("Cancel")) Cancel();
            }

            var ev = Event.current;
            if (ev.type == EventType.KeyDown)
            {
                if (ev.keyCode == KeyCode.Return || ev.keyCode == KeyCode.KeypadEnter)
                    { Commit(); ev.Use(); }
                else if (ev.keyCode == KeyCode.Escape)
                    { Cancel(); ev.Use(); }
            }
        }

        private void OnLostFocus() { if (!_closing) Cancel(); }

        private void Commit()
        {
            if (_closing) return;
            _closing = true;
            if (_count >= 2) _onConfirm?.Invoke(_count);
            Close();
        }

        private void Cancel() { _closing = true; Close(); }
    }
}
