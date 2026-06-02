using System;
using System.Collections.Generic;
using UnityEngine;
using Heathen.Ogham;

namespace Heathen.Ogham.Editor
{
    // Editor-only positioning and annotation data for one OghamData asset.
    // Stored as a companion ScriptableObject next to the OghamData asset so
    // graph layout is not serialised into the runtime data.

    [Serializable]
    public class OghamNodeMeta
    {
        // Primary key — the dotted tag-path string (e.g. "Quest.Start").
        public string  TagName      = string.Empty;
        public Rect    Position;
        public string  LabelText    = string.Empty;
        public Color   LabelColor   = Color.white;
        public bool    IsCollapsed;
        // Section expand/collapse state
        public bool    OpsExpanded     = false;
        public bool    FieldsExpanded  = true;
        public bool    ChoicesExpanded = true;
        // Option tag paths that should render as tab-flags instead of bezier wires.
        public List<string>             TabFlagOptions = new();
        // Alias pin definitions anchored to this node.
        public List<OghamAliasMeta>     AliasPins      = new();
        // Waypoints for bezier edges originating from this node's options.
        public List<OghamEdgeWaypoints> EdgeWaypoints  = new();
        // Highlight and label annotations.
        public Color     HighlightColor   = Color.clear;
        public List<int> AssignedLabelIds = new();
        // VO export metadata — editor only, not compiled into runtime assets.
        public string    DirectorNotes    = string.Empty;
    }

    [Serializable]
    public class OghamAliasMeta
    {
        public string  Name;
        // Entry tag path that this alias pin visually represents as a jump target.
        public string  TargetEntryTagName = string.Empty;
        public Vector2 Position;
    }

    [Serializable]
    public class OghamEdgeWaypoints
    {
        public string        OptionTagPath;
        public List<Vector2> Points = new();
    }

    [Serializable]
    public class OghamLabelDef
    {
        public int    Id;
        public Color  Color;
        public string Name;
    }

    [UnityEngine.CreateAssetMenu(menuName = "Ogham/Graph Metadata", fileName = "OghamGraphMetadata")]
    public class OghamGraphMetadata : UnityEngine.ScriptableObject
    {
        public OghamData SourceData;
        public List<OghamNodeMeta> Nodes = new();
        // Canvas view state: x/y = scroll offset, z = zoom scale.
        public Vector3             ViewTransform = new Vector3(0f, 0f, 1f);
        // Per-asset node header colour (Color.clear = use default rotation).
        public Color               HeaderColor   = Color.clear;
        // Global label definitions for this asset's graph.
        public List<OghamLabelDef> Labels        = new();

        public OghamNodeMeta GetOrCreateNode(string tagName)
        {
            foreach (var n in Nodes)
                if (n.TagName == tagName) return n;

            var meta = new OghamNodeMeta { TagName = tagName };
            Nodes.Add(meta);
            return meta;
        }

        public void RemoveNode(string tagName)
        {
            Nodes.RemoveAll(n => n.TagName == tagName);
        }

        public void PruneOrphans()
        {
            // Synthetic assets (.ogham workflow) never set SourceData — skip rather than
            // clearing everything; orphaned empty-tag nodes from aborted creations are harmless.
            if (SourceData == null) return;
            var validNames = new HashSet<string>();
            foreach (var e in SourceData.Entries)
                if (!string.IsNullOrEmpty(e.TagPath)) validNames.Add(e.TagPath);
            Nodes.RemoveAll(n => !validNames.Contains(n.TagName));
        }
    }
}
