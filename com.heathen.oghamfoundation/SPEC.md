# Ogham Storyteller — Implementation Spec

**Package:** `com.heathen.oghamfoundation` (+ optional `com.heathen.oghamtoolkit`)  
**Last updated:** 2026-05-17

---

## Architecture Overview

Two-package split:
- **Foundation** — runtime types, processor core, ScriptableObject authoring assets, node graph editor
- **Toolkit** — importers (Twee), play-test window, any MonoBehaviour/component wrappers

The editor lives entirely inside Foundation's `Editor/` folder.

### Editor layout

```
OghamGraphEditorWindow   (EditorWindow)
  ├── OghamTreePanel     (UIElements TwoPaneSplitView — left, 220 px)
  │     per-asset rows with ColorField swatch + foldout entry list
  └── IMGUIContainer     (right pane — canvas)
        OghamCanvas      (pure IMGUI + Handles, no MonoBehaviour)
```

Coordinate system: `Handles.BeginGUI()` inside `IMGUIContainer` uses the same container-local space as IMGUI (0,0 = container top-left). **No offset is needed or applied.**

### Data files

| File | Purpose |
|---|---|
| `OghamData` (`.asset`) | Authoring asset. Contains `List<DialogueEntry>`. |
| `OghamData.graph` (`.graph.asset`) | Editor-only layout: node positions, colors, collapse state. `OghamGraphMetadata`. |
| `OghamCompiledData` (`.asset`) | Runtime-ready merged asset. Built by `OghamBuildProcessor` on build or manually via Compile button. |

---

## Serialization — Root Cause & Strategy

### Problem

`GameplayTag` is declared `public readonly struct` with `[SerializeField] private readonly ulong _id`. Unity cannot write to `readonly` fields during deserialization, so any `GameplayTag` value stored directly in a serializable class silently loses its value on every domain reload / asset save.

`GameplayTagCondition` and `GameplayTagOperation` live in the immutable git package `com.heathen.gameplaytags` and **cannot be modified**.

```csharp
// In package — cannot edit:
public class GameplayTagCondition { public GameplayTag Tag; ... }
public class GameplayTagOperation { public GameplayTag Tag; ... public List<GameplayTagCondition> Conditions; }
```

### Fix applied (DialogueEntry, DialogueOption)

Replace direct `GameplayTag` fields with private `[SerializeField] string` backing fields and read-only computed properties:

```csharp
[Serializable]
public class DialogueEntry
{
    [SerializeField] private string _tagPath = "";
    public string TagPath { get => _tagPath; set => _tagPath = value ?? ""; }
    public GameplayTag Tag => string.IsNullOrEmpty(_tagPath) ? default : GameplayTag.FromName(_tagPath);
    // ...
}

[Serializable]
public class DialogueOption
{
    [SerializeField] private string _tagPath         = "";
    [SerializeField] private string _targetEntryPath = "";
    public string TagPath         { get => _tagPath;         set => _tagPath         = value ?? ""; }
    public string TargetEntryPath { get => _targetEntryPath; set => _targetEntryPath = value ?? ""; }
    public GameplayTag Tag         => string.IsNullOrEmpty(_tagPath)         ? default : GameplayTag.FromName(_tagPath);
    public GameplayTag TargetEntry => string.IsNullOrEmpty(_targetEntryPath) ? default : GameplayTag.FromName(_targetEntryPath);
    // ...
}
```

### Fix NOT YET applied (Conditions and Operations)

`DialogueEntry.EntryOperations` is `List<GameplayTagOperation>` and `DialogueOption.Conditions`/`Operations` are `List<GameplayTagCondition>`/`List<GameplayTagOperation>`. These use the package types directly, so their `Tag` fields **will not serialize**.

**Required fix:** Define Ogham-owned wrapper types in `OghamTypes.cs`:

```csharp
[Serializable]
public class OghamCondition
{
    [SerializeField] private string _tagPath = "";
    public string TagPath { get => _tagPath; set => _tagPath = value ?? ""; }
    public GameplayTagComparisonOp Comparison  = GameplayTagComparisonOp.Exists;
    public ulong                   CompareValue = 1;
    public bool                    ExactMatch   = true;
    public GameplayTagLogicOp      LogicOp      = GameplayTagLogicOp.And;

    public GameplayTagCondition ToCondition() => new GameplayTagCondition
        { Tag = GameplayTag.FromName(_tagPath), Comparison = Comparison,
          CompareValue = CompareValue, ExactMatch = ExactMatch, LogicOp = LogicOp };
}

[Serializable]
public class OghamOperation
{
    [SerializeField] private string _tagPath = "";
    public string TagPath { get => _tagPath; set => _tagPath = value ?? ""; }
    public GameplayTagArithmetic   Arithmetic = GameplayTagArithmetic.Set;
    public ulong                   Value      = 1;
    public List<OghamCondition>    Conditions = new();

    public bool Apply(GameplayTagCollection collection)
    {
        var conds = Conditions.ConvertAll(c => c.ToCondition());
        if (!GameplayTagCondition.EvaluateAll(conds, collection)) return false;
        collection.Apply(GameplayTag.FromName(_tagPath), Arithmetic, Value);
        return true;
    }
}
```

Then update `DialogueEntry` and `DialogueOption`:
- `EntryOperations` → `List<OghamOperation>`
- `Options[i].Conditions` → `List<OghamCondition>`
- `Options[i].Operations` → `List<OghamOperation>`

All call sites that currently pass these lists to `GameplayTagCondition.EvaluateAll` / `op.Apply` need updating to use the wrapper's `.Apply()` or a `.ToCondition()` adapter.

---

## Graph Metadata (`OghamGraphMetadata`)

Stored in the companion `.graph.asset` file. All keys are **string tag names**, not ulongs.

```csharp
public class OghamNodeMeta
{
    public string  TagName;          // primary key
    public Rect    Position;
    public string  LabelText;
    public Color   LabelColor;
    public bool    IsCollapsed;
    public bool    OpsExpanded;
    public bool    FieldsExpanded;
    public bool    ChoicesExpanded;
    public List<string>         TabFlagOptions; // option tag paths shown as tab flags
    public List<OghamAliasMeta> AliasPins;
}
```

---

## Node Rename — Cascade Requirements

When `OpenRenameDialog` commits a rename from `oldPath` to `newPath`:

### 1. Update incoming edges (nodes pointing at us)
Any option in the graph whose `TargetEntryPath == oldPath` must be updated to `newPath`.

```csharp
// For each asset in _assets, for each entry, for each option:
if (opt.TargetEntryPath == oldPath)
    opt.TargetEntryPath = newPath;
```

### 2. Cascade option tag paths (child prefix rename)
Options whose `TagPath` starts with `oldPath + "."` are child tags of this node. Rename the prefix:

```csharp
// e.g. "Dialogue.Start.Option1" → "Dialogue.Entry.Option1"
if (opt.TagPath.StartsWith(oldPath + "."))
    opt.TagPath = newPath + opt.TagPath.Substring(oldPath.Length);
```

Also apply to `OghamNodeMeta.TabFlagOptions` strings in the metadata.

### 3. Mark all affected assets dirty
All `OghamData` assets that contained changed options must be `EditorUtility.SetDirty`-marked.

**Currently:** `OpenRenameDialog` in `OghamCanvas.cs` only updates the renamed node itself — it does not walk other assets for edges or child option paths.

---

## Canvas Visual Conventions

### Button styles (transparent, colored text)
- Add (`+`): green text `(0.35, 0.90, 0.35)`, no background
- Remove (`×`): red text `(0.90, 0.35, 0.35)`, no background

### Layout constants
| Name | Value | Notes |
|---|---|---|
| `NodeW` | 260 | Node body width in canvas units |
| `PinColW` | 20 | Width reserved for output-pin column |
| `indent` | varies | Expansion panel header indent |
| content row indent | `indent + 8 * zoom` | Extra step in from header |

### Add/Remove button alignment
All three sections (On Enter, Keys, Options) use `PinColW * _zoom` right margin for the `+` button, ensuring a uniform right edge.

### Coordinate transforms
- `ToScreen(canvasPos)` → container-local pixel position (used for IMGUI rects and Handles)
- `ToCanvas(screenPos)` → canvas-space position (used for hit-testing)
- `GUIUtility.GUIToScreenPoint(containerLocal)` → OS screen coords (used only for popup window placement)

### Bezier connections
```csharp
Vector3 s   = ToScreen(OutputPinPos(edge.Source, optIdx));
Vector3 t   = ToScreen(InputPinPos(edge.Target));
Vector3 tan = new Vector3(Mathf.Max(60f, Mathf.Abs(t.x - s.x) * 0.5f) * _zoom, 0f, 0f);
Handles.DrawBezier(s, t, s + tan, t - tan, color, null, 2f * _zoom);
```
`Vector3` used explicitly throughout Handles calls to avoid `Vector2 + Vector3` ambiguity (Unity defines implicit conversions in both directions).

---

## Popup Windows

All four edit popups follow the same pattern: `ShowPopup()`, frameless, anchored near the invoking control, commit on Enter or focus-loss, cancel on Escape. No title bar, no OK/Cancel buttons.

| Class | Invoked from | Anchor |
|---|---|---|
| `OghamOptionEditWindow` | canvas option row edit button | row screen pos |
| `OghamOperationEditWindow` | canvas operation row edit button | row screen pos |
| `OghamKeyEditWindow` | canvas key row edit button | row screen pos |
| `OghamRenameWindow` | node header double-click / context menu | node top-left screen pos |

`OghamRenameWindow.Open(string current, Action<string> onCommit, Vector2 anchor)` — the `anchor` is computed as `GUIUtility.GUIToScreenPoint(ToScreen(node.Rect.position))` for the context-menu path and `GUIUtility.GUIToScreenPoint(mp)` for the connection-drag drop path.

---

## Per-Asset Node Color

`OghamCanvas` exposes `GetAssetColor(OghamData)` / `SetAssetColor(OghamData, Color)`. Colors are stored in `OghamGraphMetadata` keyed by asset instance ID. `OghamTreePanel` binds to these via `ColorGetter` / `ColorSetter` delegates. The header of every node drawn from a given asset uses that asset's color.

---

## Outstanding Work Items

### BUG — Conditions and Operations do not serialize their Tag (HIGH PRIORITY)

`GameplayTagCondition.Tag` and `GameplayTagOperation.Tag` use the `readonly struct` `GameplayTag` which Unity cannot deserialize. Affects:
- `DialogueEntry.EntryOperations` — every operation's target tag is lost on reload
- `DialogueOption.Conditions` — every condition's tag is lost
- `DialogueOption.Operations` — every operation's tag is lost
- `GameplayTagOperation.Conditions` (nested) — same

**Fix:** Introduce `OghamCondition` and `OghamOperation` wrapper types (see Serialization section above). Replace all list field types and update all call sites in `OghamProcessorCore`, `OghamCanvas`, `OghamOperationEditWindow`, and `OghamBuildProcessor`.

---

### BUG — Node rename does not cascade to referencing options or child option paths

`OpenRenameDialog` in `OghamCanvas.cs` currently only renames the node itself. It must also:
1. Walk all options across all registered assets and update `TargetEntryPath` where it equals the old tag path (incoming edge fix)
2. Walk all options and rename any `TagPath` that starts with `oldPath + "."` (child option cascade)
3. Update `OghamNodeMeta.TabFlagOptions` strings for the same prefix pattern
4. Call `EditorUtility.SetDirty` on every affected asset

---

### FEATURE — Rubber-band multi-select (Step 6)

Drag on empty canvas space draws a selection rectangle. All nodes whose `Rect` intersects the rubber-band rect become selected. Selected nodes move together. Selection cleared on click-away.

Storage: `_selectedNodes: HashSet<CanvasNode>` in `OghamCanvas`. Draw with `EditorGUI.DrawRect` at low alpha during drag.

---

### FEATURE — Redirect waypoints on bezier wires (Step 7)

Double-click a wire to insert a waypoint at that position. Waypoints are stored in `OghamGraphMetadata` keyed by the option's tag path. The bezier is split into segments through each waypoint. Waypoints can be dragged. Right-click a waypoint to remove it.

Storage in `OghamNodeMeta` (or a separate per-option meta structure — TBD):
```csharp
public class OghamWireWaypoint { public string OptionTagPath; public Vector2 Position; }
```

---

### FEATURE — Tab-flag rendering mode per option (Step 8)

An option can be toggled between bezier-wire display and tab-flag display. In tab-flag mode the bezier is hidden; instead a small colored rectangular flag tab is drawn at the output pin. The flag label shows the option's tag short name. Toggle stored in `OghamNodeMeta.TabFlagOptions` (list of option tag paths currently in flag mode).

Right-click a wire or its source pin to toggle "Show as Tab Flag" on that option.

---

### CLEANUP — `OghamGraphEditorWindow` no longer calls `SetHandlesOffset`

`SetHandlesOffset` and `_handlesOffset` were fully removed from `OghamCanvas`. Verify `OghamGraphEditorWindow.DrawCanvas()` does not call anything that no longer exists (it should not — the removal was tracked in the last session).

---

## File Map

| File | Layer | Status |
|---|---|---|
| `Runtime/OghamTypes.cs` | Runtime | **Needs OghamCondition/OghamOperation wrappers** |
| `Runtime/OghamData.cs` | Runtime | OK — child index derived from connections |
| `Runtime/OghamCompiledData.cs` | Runtime | OK |
| `Runtime/OghamProcessorCore.cs` | Runtime | **Needs update when OghamOperation wrappers added** |
| `Runtime/OghamProcessor.cs` | Runtime | OK |
| `Runtime/OghamBlob.cs` | Runtime (ECS) | OK — ParentTagId removed |
| `Runtime/OghamDataBaker.cs` | Runtime (ECS) | OK — ParentTagId removed |
| `Editor/OghamGraphEditorWindow.cs` | Editor | OK |
| `Editor/OghamCanvas.cs` | Editor | **Needs rename-cascade fix** |
| `Editor/OghamTreePanel.cs` | Editor | OK — ColorField, ColorGetter/Setter |
| `Editor/OghamGraphMetadata.cs` | Editor | OK — string keys throughout |
| `Editor/OghamRenameWindow.cs` | Editor | OK — ShowPopup, anchor param |
| `Editor/OghamOptionEditWindow.cs` | Editor | OK — uses TagPath/TargetEntryPath |
| `Editor/OghamOperationEditWindow.cs` | Editor | **Needs update when OghamOperation wrappers added** |
| `Editor/OghamTagHelper.cs` | Editor | OK |
| `../com.heathen.oghamtoolkit/Editor/OghamTweeImportWindow.cs` | Toolkit Editor | OK — 3 CS0200 errors fixed |
