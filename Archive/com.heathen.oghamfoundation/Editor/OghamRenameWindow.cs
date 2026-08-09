using System;
using UnityEditor;
using UnityEngine;

namespace Heathen.Ogham.Editor
{
    /// <summary>
    /// Popup dialog for renaming a dialogue entry's tag path in the graph editor. Shows a "Create" button
    /// when the entered tag does not yet exist in the registry, or "Okay" when it already exists.
    /// </summary>
    public class OghamRenameWindow : EditorWindow
    {
        private string         _tagPath;
        private Action<string> _onCommit;
        private bool           _focusSet;
        private bool           _closing;

        private const float W = 320f;
        private const float H = 74f;  // space + text field + space + button row + padding

        /// <summary>
        /// Opens the rename popup anchored near the given screen position, pre-filled with <paramref name="current"/>.
        /// Calls <paramref name="onCommit"/> with the new tag path when the user confirms.
        /// </summary>
        /// <param name="current">The existing tag path to pre-fill in the text field.</param>
        /// <param name="onCommit">Callback invoked with the entered tag path when the user confirms.</param>
        /// <param name="anchor">The screen-space position near which the popup is anchored.</param>
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
