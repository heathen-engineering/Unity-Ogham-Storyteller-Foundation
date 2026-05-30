using System;
using UnityEditor;
using UnityEngine;

namespace Heathen.Ogham.Editor
{
    // Popup for renaming a tag path.
    // Buttons: [Create] when the tag is new, [Okay] when it already exists, plus [Cancel].
    public class OghamRenameWindow : EditorWindow
    {
        private string         _tagPath;
        private Action<string> _onCommit;
        private bool           _focusSet;
        private bool           _closing;

        private const float W = 320f;
        private const float H = 74f;  // space + text field + space + button row + padding

        public static void Open(string current, Action<string> onCommit, Vector2 anchor)
        {
            var w = CreateInstance<OghamRenameWindow>();
            w.titleContent = new GUIContent("Rename Tag");
            w._tagPath     = current;
            w._onCommit    = onCommit;
            w._focusSet    = false;
            w._closing     = false;
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

            GUI.SetNextControlName("tagField");
            _tagPath = EditorGUILayout.TextField(_tagPath);

            if (!_focusSet)
            {
                GUI.FocusControl("tagField");
                _focusSet = true;
            }

            var ev = Event.current;
            if (ev.type == EventType.KeyDown)
            {
                if (ev.keyCode == KeyCode.Return || ev.keyCode == KeyCode.KeypadEnter)
                    { Commit(); ev.Use(); }
                else if (ev.keyCode == KeyCode.Escape)
                    { Cancel(); ev.Use(); }
            }

            EditorGUILayout.Space(4f);

            string trimmed   = _tagPath?.Trim() ?? "";
            bool   tagExists = !string.IsNullOrEmpty(trimmed) && OghamTagHelper.IsValidTagPath(trimmed);
            string saveLabel = tagExists ? "Okay" : "Create";

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button(saveLabel)) Commit();
                if (GUILayout.Button("Cancel"))  Cancel();
            }
        }

        private void OnLostFocus()
        {
            if (!_closing) Commit();
        }

        private void Commit()
        {
            if (_closing) return;
            _closing = true;
            _onCommit?.Invoke(_tagPath);
            Close();
        }

        private void Cancel()
        {
            _closing = true;
            Close();
        }
    }
}
