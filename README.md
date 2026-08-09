> [!IMPORTANT]
> ## 🔀 This repo has moved to Codeberg
>
> **The active copy now lives at [codeberg.org/Heathen-Engineering/Unity-Ogham-Storyteller-Foundation](https://codeberg.org/Heathen-Engineering/Unity-Ogham-Storyteller-Foundation)** — please point your git remote, UPM manifest, or Gem reference there going forward. That's where new commits, releases, and issues actually happen now.
>
> **This GitHub copy is preserved as-is and still works** for anyone already pointing at it, but it isn't receiving new updates. It will be archived (read-only) once every downstream package that depends on it has finished migrating too, not immediately.
>
> Questions? [Discord](https://discord.gg/6X3xrRc).

# Ogham Storyteller Foundation

![License](https://img.shields.io/badge/License-Apache_2.0-blue?style=flat-square)
[![Maintained](https://img.shields.io/badge/Maintained-On%20Codeberg-brightgreen?style=flat-square)](https://codeberg.org/Heathen-Engineering/Unity-Ogham-Storyteller-Foundation)
![Unity](https://img.shields.io/badge/Unity-6%20%2B-%23313131?style=flat-square&logo=unity&logoColor=white)
[![Dependency](https://img.shields.io/badge/Dependency-GameplayTags_Foundation-lightgrey?style=flat-square)](https://github.com/heathen-engineering/Unity-GameplayTags-Foundation)
[![Dependency](https://img.shields.io/badge/Dependency-Lexicon_Foundation-lightgrey?style=flat-square)](https://github.com/heathen-engineering/Unity-Lexicon-Localisation-Foundation)

A tag-driven narrative graph system for Unity. Dialogue nodes are identified by `GameplayTag` dot-paths and connect through conditional player choices. A runtime processor walks the graph, maintains a persistent narrative `GameplayTagCollection` state, and fires events as the conversation progresses. All displayed content references Lexicon keys so the entire story is localisation-aware by default.

-----

## 🛠 Also Available For
[![O3DE](https://img.shields.io/badge/O3DE-25.10%20%2B-%2300AEEF?style=for-the-badge&logo=data:image/svg+xml;base64,PHN2ZyB4bWxucz0iaHR0cDovL3d3dy53My5vcmcvMjAwMC9zdmciIHZpZXdCb3g9IjAgMCAyNCAyNCI+PHBhdGggZmlsbD0id2hpdGUiIGQ9Ik0xMiAxTDEgNy40djkuMkwxMiAyM2wxMS02LjRWNy40TDEyIDF6bTkuMSAxNC45TDExLjUgMjEuM2wtOC42LTYuNFY4LjFsOC42LTYuNCA5LjEgNi40djYuOHpNMTEuNSA0LjZMMi45IDkuNnY0LjhsOC42IDUuMSA4LjYtNS4xVjkuNmwtOC42LTUuMHoiLz48L3N2Zz4=)](https://github.com/heathen-engineering/O3DE-Ogham-Storyteller-Foundation)

-----

## Become a GitHub Sponsor

**Prefer a one-time purchase?** Heathen's own storefront is live, direct source access at [heathen.group/pricing](https://heathen.group/pricing/), no sponsorship required.

[![Discord](https://img.shields.io/badge/Discord--1877F2?style=social&logo=discord)](https://discord.gg/6X3xrRc)
[![GitHub followers](https://img.shields.io/github/followers/heathen-engineering?style=social)](https://github.com/heathen-engineering?tab=followers)  
Support Heathen by becoming a [GitHub Sponsor](https://github.com/sponsors/heathen-engineering). Sponsorship directly funds the development and maintenance of free tools like this, as well as our game development [Knowledge Base](https://heathen.group/) and community on [Discord](https://discord.gg/6X3xrRc).

Sponsors also get access to our private SourceRepo, which includes developer tools for O3DE, Unreal, Unity, and Godot.  
Learn more or explore other ways to support @ [heathen.group/kb](https://heathen.group/pricing/)

-----

## What it does

Ogham Storyteller Foundation gives you a data-driven dialogue and narrative graph built on three runtime layers:

| Layer | Purpose |
|-------|---------|
| **Data** | `OghamData` ScriptableObjects authored in the graph editor; compiled to `OghamCompiledData` for shipping |
| **Processor** | `OghamProcessorCore` (pure C#) walks the graph, evaluates conditions, applies operations, and fires events |
| **Component** | `OghamProcessor` MonoBehaviour wraps the core for standard Unity scenes |

Every node in the graph is a `DialogueEntry` identified by a `GameplayTag`. Every branch is a `DialogueOption` that conditionally points to another entry. The narrative state is a live `GameplayTagCollection` that conditions and operations read and write as the player moves through the story.

The following features are included:

- **Node graph editor** — `Window > Heathen > Ogham` opens a zoomable, pannable IMGUI canvas. Nodes are colour-coded per data asset, bezier wires connect options to target entries, and each node expands to show content keys, on-enter operations, and player options.
- **Multi-file authoring** — Multiple `OghamData` assets can be registered into the same processor. The graph editor displays all assets simultaneously, enabling team-split authoring across many files.
- **Content keys** — Each entry carries a list of `OghamContentKey` items typed as `Text`, `Image`, `Audio`, or `Prefab`. All text keys support Lexicon's `Localised` / `Literal` / `Invariant` modes so story content is automatically culture-aware.
- **Conditional options** — `DialogueOption` items carry a `GameplayTagCondition` list. Options whose conditions fail are filtered out by `GetAvailableOptions()` before being presented to the player.
- **On-enter operations** — `DialogueEntry.EntryOperations` runs a `GameplayTagOperation` list the moment the player enters a node, letting you modify narrative state (reputation, flags, counters) as part of the story flow.
- **Inline links** — `[display text](TargetEntry.TagPath)` syntax inside a text content key is parsed automatically to synthesize `DialogueOption` entries, enabling hyperlink-style navigation in prose.
- **Build pipeline** — `OghamBuildProcessor` merges all registered `OghamData` assets into a single `OghamCompiledData` asset at build time (or via the Compile button) for efficient runtime loading.
- **ECS support** — `OghamBlob` / `OghamDataBaker` bake `OghamCompiledData` into BlobAsset form for use in Entities worlds.
- **Save / load** — `OghamSaveState` captures the current entry id, full narrative `GameplayTagCollection`, and conversation history. Serialize it however you like and pass it back to `LoadSaveState`.
- **Tree panel** — The left pane of the graph editor shows all open `OghamData` assets as a collapsible tree with per-asset colour swatches for easy visual separation.

---

## Requirements

- Unity **6000.0** or compatible
- [`com.heathen.gameplaytagsfoundation`](https://github.com/heathen-engineering/Unity-GameplayTags-Foundation) **1.0.0**
- [`com.heathen.lexiconfoundation`](https://github.com/heathen-engineering/Unity-Lexicon-Localisation-Foundation) **1.0.0**

---

## Installation

### Via Unity Package Manager (UPM)

1. In Unity, go to `Window > Package Manager`.
2. Click **+** > **Add package from git URL**.
3. Enter:
   ```
   https://github.com/heathen-engineering/Unity-Ogham-Storyteller-Foundation.git?path=/com.heathen.oghamfoundation
   ```

`com.heathen.gameplaytagsfoundation` and `com.heathen.lexiconfoundation` must be installed first (or listed as git dependencies in your manifest). UPM does not resolve cross-GitHub git dependencies automatically.

-----

## Setup & Workflow

### 1. Create a Dialogue Data asset

Right-click in the Project window and choose **Create > Heathen > Ogham > Dialogue Data**. This creates an `OghamData` asset.

### 2. Author the graph

Open **Window > Heathen > Ogham**. Drag your `OghamData` asset into the left tree panel. The canvas shows any existing nodes. Right-click on empty canvas space to add a new `DialogueEntry`. Double-click a node header to rename it (sets its `GameplayTag` path). Drag from an output pin to another node's input pin to connect a `DialogueOption`.

### 3. Add content to a node

Expand a node's **Keys** section and click **+** to add a content key. Choose a type (`Text`, `Image`, `Audio`, `Prefab`) and a mode (`Localised` to point at a Lexicon key, `Literal` for a raw value).

```
Dialogue.Start     → Text, Localised, key "Story.Act1.Opening"
Dialogue.Start     → Audio, Localised, key "VO.Act1.Opening"
```

### 4. Set up the processor

Add an `OghamProcessor` component to a GameObject in your scene. In the Inspector, assign your `OghamCompiledData` asset (built after authoring), or assign `OghamData` assets directly to the **Auto Register** list for editor iteration.

### 5. Drive the conversation via Storyteller (recommended)

`Storyteller` is the primary developer entry point — a static facade over the active `OghamProcessor`. No references or singletons to manage; just call `Storyteller.*` from anywhere.

```csharp
using Heathen.Ogham;

// Subscribe to events
Storyteller.OnEntered += HandleNodeEntered;
Storyteller.OnChoice  += opt => Debug.Log($"Player chose: {opt.GetText()}");
Storyteller.OnClosed  += () => Debug.Log("Conversation ended");

// Start a conversation — string is hashed automatically
Storyteller.Enter("NPC.Blacksmith.Greeting");

void HandleNodeEntered(StoryNode node)
{
    // Typed content accessors by index (order matches the editor)
    SpeakerName.text  = node.GetText(0);
    MessageBody.text  = node.GetText(1);
    Background.sprite = node.GetSprite(2);

    // Spawn buttons for each available option
    foreach (var option in node.Options)
    {
        var btn    = Instantiate(buttonPrefab, buttonContainer);
        btn.label  = option.GetText();
        btn.onClick.AddListener(option.Choose); // no Storyteller reference needed in the button
    }
}
```

### 6. Narrative state and external systems

```csharp
// Read a subtree (e.g., the player's inventory as tracked by story operations)
GameplayTagCollection inventory = Storyteller.ReadState("Inventory.Items");

// Inject state from an external system (e.g., a shop transaction)
Storyteller.ClearState("Inventory.Items");
Storyteller.Execute(
    new GameplayTagOperation { Tag = "Inventory.Items.Potion",      Arithmetic = GameplayTagArithmetic.Set, Value = 3 },
    new GameplayTagOperation { Tag = "Inventory.Items.BronzeSword", Arithmetic = GameplayTagArithmetic.Set, Value = 1 }
);

// Clear history without touching state, or vice versa
Storyteller.ClearHistory(5);  // remove last 5 entries
Storyteller.ClearState();     // reset all story flags
```

### 7. Save and load

```csharp
// Capture — returns an OghamSaveState with current state + history
OghamSaveState save = Storyteller.Snapshot("slot1");
string json = JsonUtility.ToJson(save);  // or any other serialiser

// Restore
Storyteller.Restore(JsonUtility.FromJson<OghamSaveState>(json));
```

### 8. Low-level access (advanced)

If you need direct access to the underlying processor (ECS, multi-processor setups), `OghamProcessor.Current` is still available:

```csharp
OghamProcessor.Current.OnDialogueEntered += (entry, options) => { ... };
OghamProcessor.Current.StartConversation(GameplayTag.FromName("Dialogue.Start"));
```

-----

## Core Types

### `Storyteller`

| Member | Description |
|--------|-------------|
| `Enter(tag)` | Begin a conversation at the named node; accepts string, ulong, or GameplayTag |
| `Choose(tag)` | Select an option; navigates to its target or closes if no target |
| `Close()` | Force-close the conversation |
| `OnEntered` | `event Action<StoryNode>` — fired when a node is entered |
| `OnChoice` | `event Action<StoryOption>` — fired when `Choose()` is called, before navigation |
| `OnClosed` | `event Action` — fired when the conversation ends |
| `IsActive` | `true` while a conversation is in progress |
| `Data` | Current `StoryNode`; `null` when not active |
| `Options` | Pre-filtered `IReadOnlyList<StoryOption>` for the current node |
| `History` | `IReadOnlyList<HistoryEntry>` — ordered visit log |
| `ReadState(tag)` | Returns a `GameplayTagCollection` subset of state at/below `tag` |
| `Execute(ops)` | Apply one or more `GameplayTagOperation` items to narrative state |
| `ClearState()` | Clear all narrative state (does not clear history) |
| `ClearState(tag)` | Clear all state tags at/below `tag` |
| `ClearHistory()` | Clear all history (does not clear state) |
| `ClearHistory(steps)` | Remove the last N history entries |
| `Snapshot(name)` | Capture state + history into an `OghamSaveState` for serialisation |
| `Restore(state)` | Restore from a previously captured snapshot |

### `StoryNode`

| Member | Description |
|--------|-------------|
| `Tag` | `GameplayTag` identity of this node (ulong at runtime) |
| `ContentCount` | Number of content keys on this node |
| `Options` | Pre-filtered `IReadOnlyList<StoryOption>` |
| `GetText(index)` | Resolved string for the content key at `index` |
| `GetSprite(index)` | Resolved `Sprite` for the content key at `index` |
| `GetAudio(index)` | Resolved `AudioClip` for the content key at `index` |
| `GetPrefab(index)` | Resolved `GameObject` for the content key at `index` |

Content key index is **absolute** — matches the order the author placed keys in the editor. Type mismatches return `null` / `string.Empty` silently.

### `StoryOption`

| Member | Description |
|--------|-------------|
| `Tag` | `GameplayTag` identity (usable for tracking without string resolution) |
| `GetText()` | Resolved display text (Lexicon-aware) |
| `Choose()` | Advance the conversation by selecting this option |

### `DialogueEntry`

| Member | Description |
|--------|-------------|
| `TagPath` | Dot-path string backing the `GameplayTag` identity of this node |
| `Tag` | Computed `GameplayTag` from `TagPath` |
| `ContentKeys` | `List<OghamContentKey>` — ordered content items (text, image, audio, prefab) |
| `EntryOperations` | `List<GameplayTagOperation>` — run on entry to modify narrative state |
| `Options` | `List<DialogueOption>` — player choices leading out of this node |

### `DialogueOption`

| Member | Description |
|--------|-------------|
| `TagPath` | Dot-path identity of this option |
| `TargetEntryPath` | Dot-path of the entry to move to; empty = close conversation |
| `TextKey` | `LexiconText` for the option label (localisation-aware) |
| `Conditions` | `List<GameplayTagCondition>` — all must pass for the option to be visible |
| `Operations` | `List<GameplayTagOperation>` — run when this option is selected |

### `OghamContentKey`

| Member | Description |
|--------|-------------|
| `Type` | `OghamContentType`: `Text`, `Image`, `Audio`, `Prefab` |
| `Mode` | `LexiconLocMode`: `Localised`, `Literal`, `Invariant` |
| `KeyOrValue` | Lexicon dot-path key (Localised) or raw string/identifier (Literal/Invariant) |
| `AssetRef` | Direct `UnityEngine.Object` reference for non-text Literal content |
| `ResolveText()` | Returns active-culture string for Text-type keys |
| `ResolveAsset()` | Returns active-culture asset for non-text keys |

### `OghamProcessorCore`

| Member | Description |
|--------|-------------|
| `RegisterData(OghamData)` | Add an authoring asset to the processor |
| `RegisterData(OghamCompiledData)` | Add a compiled (build-time) asset |
| `StartConversation(tag)` | Begin at the named entry; returns `false` if not found |
| `SelectOption(tag)` | Select an option by its `GameplayTag`; navigates to its target |
| `CloseConversation(interrupted)` | End the current conversation |
| `ReturnTo(tag)` | Jump back to a previously visited entry |
| `GetAvailableOptions()` | Condition-filtered options for the current entry |
| `IsConversationActive` | Whether a conversation is in progress |
| `CurrentEntry` | The active `DialogueEntry`, or `null` |
| `NarrativeState` | The live `GameplayTagCollection` tracking story state |
| `History` | `IReadOnlyList<HistoryEntry>` — ordered visit log |
| `CreateSaveState(name)` | Snapshot current entry + state + history |
| `LoadSaveState(state)` | Restore from a snapshot |
| `OnDialogueEntered` | `event Action<DialogueEntry, List<DialogueOption>>` |
| `OnDialogueClosed` | `event Action<bool>` — `true` if interrupted |

-----

## Namespaces

| Namespace | Contents |
|-----------|----------|
| `Heathen.Ogham` | All runtime types: `Storyteller`, `StoryNode`, `StoryOption`, `OghamData`, `OghamCompiledData`, `OghamProcessorCore`, `OghamProcessor`, `DialogueEntry`, `DialogueOption`, `OghamContentKey`, `OghamSaveState`, ECS bakers |
| `Heathen.Ogham.Editor` | Editor-only: `OghamGraphEditorWindow`, `OghamCanvas`, `OghamTreePanel`, `OghamGraphMetadata`, all popup windows and custom drawers |
