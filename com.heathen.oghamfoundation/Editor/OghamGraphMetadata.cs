using System;
using System.Collections.Generic;
using UnityEngine;
using Heathen.Ogham;

namespace Heathen.Ogham.Editor
{
    /// <summary>
    /// Editor-only positioning and annotation data for a single node within an <see cref="OghamGraphMetadata"/> asset.
    /// Stored as part of the companion ScriptableObject so graph layout is not serialised into the runtime data.
    /// </summary>
    [Serializable]
    public class OghamNodeMeta
    {
        /// <summary>Primary key — the dot-path tag string identifying the node, e.g. "Quest.Start".</summary>
        public string  TagName      = string.Empty;
        /// <summary>The canvas-space position and size of this node.</summary>
        public Rect    Position;
        /// <summary>An optional short label displayed in the graph editor (not related to content keys).</summary>
        public string  LabelText    = string.Empty;
        /// <summary>The colour of the <see cref="LabelText"/> badge.</summary>
        public Color   LabelColor   = Color.white;
        /// <summary>When <c>true</c>, the node body is collapsed to show only the header.</summary>
        public bool    IsCollapsed;
        /// <summary>When <c>true</c>, the On Enter operations section is expanded in the canvas.</summary>
        public bool    OpsExpanded     = false;
        /// <summary>When <c>true</c>, the content keys (Fields) section is expanded in the canvas.</summary>
        public bool    FieldsExpanded  = true;
        /// <summary>When <c>true</c>, the options or routes section is expanded in the canvas.</summary>
        public bool    ChoicesExpanded = true;
        /// <summary>Option tag paths that should render as compact tab-flag labels instead of bezier wire connections.</summary>
        public List<string>             TabFlagOptions = new();
        /// <summary>Alias pin definitions anchored to this node for visual jump-target annotations.</summary>
        public List<OghamAliasMeta>     AliasPins      = new();
        /// <summary>Waypoint lists for bezier edges originating from this node's options.</summary>
        public List<OghamEdgeWaypoints> EdgeWaypoints  = new();
        /// <summary>Optional highlight colour for this node. <see cref="Color.clear"/> means no highlight.</summary>
        public Color     HighlightColor   = Color.clear;
        /// <summary>IDs of labels from the owning metadata's <see cref="OghamGraphMetadata.Labels"/> list assigned to this node.</summary>
        public List<int> AssignedLabelIds = new();
        /// <summary>Director notes for VO script export. Editor-only; not compiled into runtime assets.</summary>
        public string    DirectorNotes    = string.Empty;
    }

    /// <summary>
    /// Defines a named alias pin badge anchored to a node in the graph canvas, visually representing
    /// a jump to another entry without drawing a full bezier wire.
    /// </summary>
    [Serializable]
    public class OghamAliasMeta
    {
        /// <summary>The display name shown on the alias badge.</summary>
        public string  Name;
        /// <summary>The dot-path tag of the entry this alias pin visually represents as a jump target.</summary>
        public string  TargetEntryTagName = string.Empty;
        /// <summary>The canvas-space position of this alias pin.</summary>
        public Vector2 Position;
    }

    /// <summary>
    /// Stores the list of canvas-space waypoints for a single bezier edge, identified by the option's tag path.
    /// </summary>
    [Serializable]
    public class OghamEdgeWaypoints
    {
        /// <summary>The dot-path tag path of the option whose bezier edge these waypoints belong to.</summary>
        public string        OptionTagPath;
        /// <summary>Ordered list of canvas-space redirect points along the bezier edge.</summary>
        public List<Vector2> Points = new();
    }

    /// <summary>
    /// Defines a named colour label that can be assigned to nodes in the graph editor for visual organisation.
    /// </summary>
    [Serializable]
    public class OghamLabelDef
    {
        /// <summary>A unique integer identifier for this label within its owning graph metadata asset.</summary>
        public int    Id;
        /// <summary>The pill colour displayed in the node's label strip.</summary>
        public Color  Color;
        /// <summary>The human-readable name shown when the label strip is expanded.</summary>
        public string Name;
    }

    /// <summary>
    /// Editor-only graph layout, view state, and annotation data for a single <see cref="OghamData"/>. A plain
    /// class (no longer a ScriptableObject): it is built in-memory from the <c>.ogham</c> <c>_editor</c> block
    /// and written back via <see cref="OghamJsonDocument.SyncFrom"/>; it is never an asset and never in builds.
    /// </summary>
    public class OghamGraphMetadata
    {
        /// <summary>The <see cref="OghamData"/> this metadata accompanies.</summary>
        public OghamData SourceData;
        /// <summary>Display name; set by the editor on load.</summary>
        public string Name = string.Empty;
        /// <summary>Per-node layout and annotation data, keyed by tag path.</summary>
        public List<OghamNodeMeta> Nodes = new();
        /// <summary>Canvas view state: x/y = scroll offset, z = zoom scale.</summary>
        public Vector3             ViewTransform = new Vector3(0f, 0f, 1f);
        /// <summary>Per-asset node header colour. <see cref="Color.clear"/> means use the default rotation colour.</summary>
        public Color               HeaderColor   = Color.clear;
        /// <summary>Global label definitions for this asset's graph, shared across all nodes.</summary>
        public List<OghamLabelDef> Labels        = new();

        /// <summary>
        /// Returns the <see cref="OghamNodeMeta"/> for the given tag name, creating a new entry if none exists.
        /// </summary>
        /// <param name="tagName">The dot-path tag string that identifies the node.</param>
        /// <returns>The existing or newly created <see cref="OghamNodeMeta"/>.</returns>
        public OghamNodeMeta GetOrCreateNode(string tagName)
        {
            foreach (var n in Nodes)
                if (n.TagName == tagName) return n;

            var meta = new OghamNodeMeta { TagName = tagName };
            Nodes.Add(meta);
            return meta;
        }

        /// <summary>Removes the <see cref="OghamNodeMeta"/> entry for the given tag name, if present.</summary>
        /// <param name="tagName">The dot-path tag string of the node to remove.</param>
        public void RemoveNode(string tagName)
        {
            Nodes.RemoveAll(n => n.TagName == tagName);
        }

        /// <summary>
        /// Removes any <see cref="OghamNodeMeta"/> entries whose tag names do not match an entry in <see cref="SourceData"/>.
        /// Synthetic assets (those without a <see cref="SourceData"/> reference) are skipped to avoid clearing their metadata.
        /// </summary>
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
