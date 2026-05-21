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
    // Left-side file/entry hierarchy panel for the Ogham graph editor.
    public class OghamTreePanel : VisualElement
    {
        public event System.Action<OghamData, DialogueEntry> OnEntrySelected;
        public event System.Action<OghamData>                OnAssetSelected;
        public event System.Action<OghamData>                OnAssetClosed;

        // Optional delegate for resolving the display name of an entry.
        public System.Func<DialogueEntry, string> NameResolver { get; set; }

        // Optional delegates for reading and writing per-asset node header colors.
        public System.Func<OghamData, Color>   ColorGetter { get; set; }
        public System.Action<OghamData, Color> ColorSetter { get; set; }

        private readonly ScrollView                  _scroll;
        private readonly List<OghamData>             _assets   = new();
        private readonly Dictionary<OghamData, bool> _expanded = new();
        private string                               _searchQuery = "";

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

        public void LoadAssets(IEnumerable<OghamData> assets)
        {
            _assets.Clear();
            _assets.AddRange(assets);
            Rebuild();
        }

        public void AddAsset(OghamData asset)
        {
            if (!_assets.Contains(asset))
            {
                _assets.Add(asset);
                Rebuild();
            }
        }

        public void RemoveAsset(OghamData asset)
        {
            if (_assets.Remove(asset))
                Rebuild();
        }

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

                if (!_expanded[asset] && !searching) continue;

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
            var row = new VisualElement
            {
                style = {
                    flexDirection   = FlexDirection.Row,
                    alignItems      = Align.Center,
                    paddingLeft     = 4f,
                    paddingTop      = 3f,
                    paddingBottom   = 3f,
                    backgroundColor = new Color(0.22f, 0.22f, 0.22f),
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

            var label = new Label(asset.name) { style = { flexGrow = 1f } };
            label.RegisterCallback<ClickEvent>(_ => OnAssetSelected?.Invoke(asset));
            row.Add(label);

            var ping = new Button(() => EditorGUIUtility.PingObject(asset))
            {
                text  = "•",
                style = { width = 18f },
            };
            row.Add(ping);

            var close = new Button(() => OnAssetClosed?.Invoke(asset))
            {
                text  = "×",
                style = { width = 18f, color = new Color(0.8f, 0.4f, 0.4f) },
            };
            row.Add(close);

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
