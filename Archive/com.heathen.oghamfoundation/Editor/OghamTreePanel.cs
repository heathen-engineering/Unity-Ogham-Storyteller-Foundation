using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;
using Heathen.GameplayTags;
using Heathen.Ogham;

namespace Heathen.Ogham.Editor
{
    /// <summary>
    /// Left-side file and entry hierarchy panel for the Ogham graph editor. Displays a scrollable list of loaded
    /// <see cref="OghamData"/> assets and their dialogue entries with expand/collapse, visibility, and colour controls.
    /// </summary>
    public class OghamTreePanel : VisualElement
    {
        /// <summary>Raised when the user clicks a dialogue entry row. Provides the owning asset and the selected entry.</summary>
        public event System.Action<OghamData, DialogueEntry> OnEntrySelected;
        /// <summary>Raised when the user clicks an asset row header.</summary>
        public event System.Action<OghamData>                OnAssetSelected;
        /// <summary>Raised when the user closes an asset from the panel.</summary>
        public event System.Action<OghamData>                OnAssetClosed;
        /// <summary>Raised when the user toggles the eye button for an asset. The boolean parameter is <c>true</c> when the asset is now hidden.</summary>
        public event System.Action<OghamData, bool>          OnAssetVisibilityChanged;

        /// <summary>Optional delegate that resolves the display name for a dialogue entry. Falls back to the tag path when <c>null</c>.</summary>
        public System.Func<DialogueEntry, string> NameResolver { get; set; }

        /// <summary>Optional delegate for reading the per-asset node header colour to display in the colour swatch.</summary>
        public System.Func<OghamData, Color>   ColorGetter { get; set; }
        /// <summary>Optional delegate for writing the per-asset node header colour when the user changes the swatch.</summary>
        public System.Action<OghamData, Color> ColorSetter { get; set; }

        /// <summary>
        /// Optional delegate that resolves the AssetDatabase path for a synthetic (<c>.ogham</c>-backed) asset
        /// so that clicking the asset label pings the source file rather than the synthetic object.
        /// </summary>
        public System.Func<OghamData, string> PathResolver { get; set; }

        /// <summary>Optional delegate that returns <c>true</c> when the given asset is the current active target for new nodes.</summary>
        public System.Func<OghamData, bool> IsActiveAsset { get; set; }

        /// <summary>The set of assets whose nodes are currently hidden in the canvas view.</summary>
        public readonly HashSet<OghamData> HiddenAssets = new();

        private readonly ScrollView                  _scroll;
        private readonly List<OghamData>             _assets   = new();
        private readonly Dictionary<OghamData, bool> _expanded = new();
        private string                               _searchQuery = "";

        /// <summary>
        /// Initialises the tree panel, building its header, search field, and scrollable asset list.
        /// </summary>
        public OghamTreePanel()
        {
            style.width            = 220f;
            style.minWidth         = 120f;
            style.borderRightWidth = 1f;
            style.borderRightColor = new Color(0.15f, 0.15f, 0.15f);

            var header = new Label("Dialogue Files")
            {
                style = {
                    unityFontStyleAndWeight = FontStyle.Bold,
                    paddingLeft             = 6f,
                    paddingTop              = 6f,
                    paddingBottom           = 4f,
                }
            };
            Add(header);

            var searchRow = new VisualElement
            {
                style = {
                    flexDirection  = FlexDirection.Row,
                    alignItems     = Align.Center,
                    paddingLeft    = 4f,
                    paddingRight   = 4f,
                    paddingBottom  = 4f,
                }
            };
            searchRow.Add(new Label("Filter:") { style = { marginRight = 4f, fontSize = 10f } });
            var searchField = new TextField { style = { flexGrow = 1f } };
            searchField.RegisterValueChangedCallback(evt =>
            {
                _searchQuery = evt.newValue ?? "";
                Rebuild();
            });
            searchRow.Add(searchField);
            Add(searchRow);

            _scroll = new ScrollView(ScrollViewMode.Vertical) { style = { flexGrow = 1f } };
            Add(_scroll);
        }

        /// <summary>Replaces the current asset list with the given collection and rebuilds the panel.</summary>
        /// <param name="assets">The assets to display in the panel.</param>
        public void LoadAssets(IEnumerable<OghamData> assets)
        {
            _assets.Clear();
            _assets.AddRange(assets);
            Rebuild();
        }

        /// <summary>Adds an asset to the panel and rebuilds the list. Duplicate additions are silently ignored.</summary>
        /// <param name="asset">The asset to add.</param>
        public void AddAsset(OghamData asset)
        {
            if (!_assets.Contains(asset))
            {
                _assets.Add(asset);
                Rebuild();
            }
        }

        /// <summary>Removes an asset from the panel and rebuilds the list. Has no effect when the asset is not in the panel.</summary>
        /// <param name="asset">The asset to remove.</param>
        public void RemoveAsset(OghamData asset)
        {
            if (_assets.Remove(asset))
                Rebuild();
        }

        /// <summary>
        /// Clears and rebuilds all asset and entry rows, applying the current search filter and expand/collapse state.
        /// </summary>
        public void Rebuild()
        {
            _scroll.Clear();
            string q         = _searchQuery.Trim();
            bool   searching = !string.IsNullOrEmpty(q);

            foreach (var asset in _assets)
            {
                if (asset == null) continue;
                _expanded.TryAdd(asset, true);

                _scroll.Add(MakeAssetRow(asset));

                bool entryVisible = (!HiddenAssets.Contains(asset)) && (_expanded[asset] || searching);
                if (!entryVisible) continue;

                foreach (var entry in asset.Entries)
                {
                    if (searching && ResolveName(entry).IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0)
                        continue;
                    _scroll.Add(MakeEntryRow(asset, entry));
                }
            }
        }

        private string ResolveName(DialogueEntry entry)
        {
            if (NameResolver != null) return NameResolver(entry);
            if (!string.IsNullOrEmpty(entry.TagPath)) return entry.TagPath;
            if (entry.Tag.IsValid)
                return GameplayTagRegistry.GetName(entry.Tag.Id) ?? entry.Tag.Id.ToString("X16");
            return "(no tag)";
        }

        private VisualElement MakeAssetRow(OghamData asset)
        {
            bool isActive = IsActiveAsset?.Invoke(asset) == true;
            var row = new VisualElement
            {
                style = {
                    flexDirection   = FlexDirection.Row,
                    alignItems      = Align.Center,
                    paddingLeft     = 4f,
                    paddingTop      = 3f,
                    paddingBottom   = 3f,
                    backgroundColor = isActive
                        ? new Color(0.18f, 0.32f, 0.18f)
                        : new Color(0.22f, 0.22f, 0.22f),
                }
            };

            var toggle = new Button(() => { _expanded[asset] = !_expanded[asset]; Rebuild(); })
            {
                text  = _expanded[asset] ? "▾" : "▸",
                style = { width = 18f, marginRight = 4f },
            };
            row.Add(toggle);

            // Color swatch — shows node header color; clicking opens color picker.
            if (ColorGetter != null)
            {
                var colorField = new ColorField { value = ColorGetter(asset) };
                colorField.style.width       = 50f;
                colorField.style.height      = 16f;
                colorField.style.marginRight = 4f;
                colorField.RegisterValueChangedCallback(evt =>
                    ColorSetter?.Invoke(asset, evt.newValue));
                row.Add(colorField);
            }

            // Label: clicking pings the file in the Project window.
            var label = new Label(asset.Name) { style = { flexGrow = 1f } };
            label.RegisterCallback<ClickEvent>(_ =>
            {
                string path = PathResolver?.Invoke(asset);
                if (!string.IsNullOrEmpty(path))
                {
                    var fileObj = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path);
                    if (fileObj != null) EditorGUIUtility.PingObject(fileObj);
                }
            });
            row.Add(label);

            // Eye toggle — show/hide this asset's nodes in the canvas.
            bool isHidden = HiddenAssets.Contains(asset);
            var eyeBtn = new Button(() =>
            {
                bool nowHidden = !HiddenAssets.Contains(asset);
                if (nowHidden) HiddenAssets.Add(asset); else HiddenAssets.Remove(asset);
                OnAssetVisibilityChanged?.Invoke(asset, nowHidden);
                Rebuild();
            })
            {
                text  = isHidden ? "○" : "◉",
                style = { width = 18f },
            };
            if (isHidden) eyeBtn.style.opacity = 0.45f;
            row.Add(eyeBtn);

            return row;
        }

        private VisualElement MakeEntryRow(OghamData asset, DialogueEntry entry)
        {
            string name = ResolveName(entry);

            var row = new Label($"   {name}")
            {
                style = { paddingTop = 2f, paddingBottom = 2f, paddingLeft = 14f }
            };
            row.AddToClassList("ogham-tree-entry");
            row.RegisterCallback<ClickEvent>(_ => OnEntrySelected?.Invoke(asset, entry));
            row.RegisterCallback<MouseEnterEvent>(_ => row.style.backgroundColor = new Color(0.28f, 0.28f, 0.28f));
            row.RegisterCallback<MouseLeaveEvent>(_ => row.style.backgroundColor = Color.clear);
            return row;
        }
    }
}
