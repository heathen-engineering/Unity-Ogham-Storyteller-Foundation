using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Heathen.GameplayTags;
using Heathen.Lexicon;
using Object = UnityEngine.Object;

namespace Heathen.Ogham
{
    /// <summary>
    /// Async delegate for loading assets from an arbitrary source path. Assign to
    /// <see cref="OghamStoryBuilder.AssetLoader"/> before calling <see cref="OghamStoryBuilder.BuildAsync(bool)"/>
    /// when the manifest contains asset entries. The delegate may use Addressables, Resources, asset bundles,
    /// or any other loading system; Foundation has no hard dependency on any of them.
    /// </summary>
    /// <param name="source">The source path or key passed from the <see cref="OghamAssetManifest"/>.</param>
    /// <param name="assetType">The expected <see cref="Type"/> of the asset.</param>
    /// <returns>A task that resolves to the loaded <see cref="Object"/>, or <c>null</c> on failure.</returns>
    public delegate Task<Object> OghamAssetLoader(string source, Type assetType);

    /// <summary>
    /// Creates and registers <see cref="OghamStory"/> instances either from a data-driven
    /// <see cref="OghamStoryManifest"/> or via a fluent code-first API.
    /// Use the static <see cref="Build(OghamStoryManifest, bool)"/> / <see cref="BuildAsync(OghamStoryManifest, bool)"/>
    /// overloads for the manifest path, or instantiate and chain methods for the fluent path.
    /// </summary>
    public class OghamStoryBuilder
    {
        /// <summary>
        /// Assign before calling <see cref="BuildAsync(bool)"/> or <see cref="BuildAsync(OghamStoryManifest, bool)"/>
        /// when the manifest contains asset entries. The delegate loads assets from an external system and injects
        /// them into <see cref="Heathen.Lexicon.LexiconRegistry"/>.
        /// </summary>
        public static OghamAssetLoader AssetLoader;

        private readonly string _storyTagPath;
        private readonly List<OghamLocaleManifest>    _locales = new();
        private readonly List<OghamAssetManifest>     _assets  = new();
        private readonly List<OghamEntryManifest>     _entries = new();

        /// <summary>
        /// Initialises a new builder for a story identified by <paramref name="storyTagPath"/>.
        /// </summary>
        /// <param name="storyTagPath">The dot-path GameplayTag that uniquely identifies the story.</param>
        public OghamStoryBuilder(string storyTagPath)
        {
            _storyTagPath = storyTagPath ?? string.Empty;
        }

        /// <summary>
        /// Adds an inline localisation entry that is injected into <see cref="Heathen.Lexicon.LexiconRegistry"/>
        /// before the story is built.
        /// </summary>
        /// <param name="culture">BCP 47 culture code, for example "en" or "fr". Null or empty uses the active culture.</param>
        /// <param name="key">The Lexicon dot-path key.</param>
        /// <param name="value">The localised string value.</param>
        /// <returns>This builder, for method chaining.</returns>
        public OghamStoryBuilder AddLocale(string culture, string key, string value)
        {
            _locales.Add(new OghamLocaleManifest { Culture = culture ?? string.Empty, Key = key, Value = value });
            return this;
        }

        /// <summary>
        /// Registers an asset source to be loaded asynchronously via <see cref="AssetLoader"/> and injected
        /// into <see cref="Heathen.Lexicon.LexiconRegistry"/> under <paramref name="lexiconKey"/>.
        /// </summary>
        /// <param name="lexiconKey">The Lexicon dot-path key under which the loaded asset is stored.</param>
        /// <param name="source">The source path passed to <see cref="AssetLoader"/>. Defaults to <paramref name="lexiconKey"/> when null or empty.</param>
        /// <param name="culture">BCP 47 culture code. Null or empty uses the active culture.</param>
        /// <returns>This builder, for method chaining.</returns>
        public OghamStoryBuilder AddAsset(string lexiconKey, string source = null, string culture = null)
        {
            _assets.Add(new OghamAssetManifest
            {
                LexiconKey = lexiconKey,
                Source     = source  ?? string.Empty,
                Culture    = culture ?? string.Empty,
            });
            return this;
        }

        /// <summary>
        /// Adds a dialogue entry to the story and returns a sub-builder for configuring its content, operations,
        /// and options. Call <see cref="OghamEntryBuilder.Done"/> to return to this builder.
        /// </summary>
        /// <param name="tagPath">The dot-path GameplayTag that identifies the new entry.</param>
        /// <returns>An <see cref="OghamEntryBuilder"/> for configuring the entry.</returns>
        public OghamEntryBuilder AddEntry(string tagPath)
        {
            var manifest = new OghamEntryManifest { TagPath = tagPath };
            _entries.Add(manifest);
            return new OghamEntryBuilder(this, manifest);
        }

        /// <summary>
        /// Synchronously builds a definition and opens a play session in the main world. Asset entries are not loaded.
        /// </summary>
        /// <param name="setAsMain">When <c>true</c>, sets the new session as the main story in <see cref="Storyteller"/>.</param>
        /// <returns>The opened <see cref="OghamSession"/>.</returns>
        public OghamSession Build(bool setAsMain = false)
        {
            var m = ToManifest();
            ApplyTags(m);
            ApplyLocalisations(m);
            return Storyteller.OpenSession(BuildDefinitionCore(m), setAsMain);
        }

        /// <summary>
        /// Asynchronously builds a definition and opens a play session in the main world, loading any registered
        /// asset entries via <see cref="AssetLoader"/> before creation.
        /// </summary>
        /// <param name="setAsMain">When <c>true</c>, sets the new session as the main story in <see cref="Storyteller"/>.</param>
        /// <returns>A task that resolves to the opened <see cref="OghamSession"/>.</returns>
        public async Task<OghamSession> BuildAsync(bool setAsMain = false)
        {
            var m = ToManifest();
            ApplyTags(m);
            ApplyLocalisations(m);
            await LoadAssetsAsync(m);
            return Storyteller.OpenSession(BuildDefinitionCore(m), setAsMain);
        }

        private OghamStoryManifest ToManifest() => new OghamStoryManifest
        {
            StoryTagPath  = _storyTagPath,
            Localisations = new List<OghamLocaleManifest>(_locales),
            Assets        = new List<OghamAssetManifest>(_assets),
            Entries       = new List<OghamEntryManifest>(_entries),
        };

        /// <summary>
        /// Builds a fresh <see cref="OghamStory"/> definition from the given manifest, registering its tags and
        /// inline localisations. The definition carries no session state — open a session to play it. This is the
        /// entry point the <see cref="OghamStoryCatalog"/> caches. Returns <c>null</c> when <paramref name="manifest"/> is <c>null</c>.
        /// </summary>
        /// <param name="manifest">The manifest describing the story definition to build.</param>
        /// <returns>The built definition, or <c>null</c>.</returns>
        public static OghamStory BuildDefinition(OghamStoryManifest manifest)
        {
            if (manifest == null) return null;
            ApplyTags(manifest);
            ApplyLocalisations(manifest);
            return BuildDefinitionCore(manifest);
        }

        /// <summary>
        /// Builds a definition from the given manifest and opens a play session in the main world.
        /// Asset entries are not loaded. Returns <c>null</c> when <paramref name="manifest"/> is <c>null</c>.
        /// </summary>
        /// <param name="manifest">The manifest describing the story to create.</param>
        /// <param name="setAsMain">When <c>true</c>, sets the session as the main story in <see cref="Storyteller"/>.</param>
        /// <returns>The opened <see cref="OghamSession"/>, or <c>null</c>.</returns>
        public static OghamSession Build(OghamStoryManifest manifest, bool setAsMain = false)
        {
            var def = BuildDefinition(manifest);
            return def == null ? null : Storyteller.OpenSession(def, setAsMain);
        }

        /// <summary>
        /// Asynchronously builds a definition from the given manifest and opens a play session in the main world,
        /// loading asset entries via <see cref="AssetLoader"/>. Returns <c>null</c> when <paramref name="manifest"/> is <c>null</c>.
        /// </summary>
        /// <param name="manifest">The manifest describing the story to create.</param>
        /// <param name="setAsMain">When <c>true</c>, sets the session as the main story in <see cref="Storyteller"/>.</param>
        /// <returns>A task that resolves to the opened <see cref="OghamSession"/>, or <c>null</c>.</returns>
        public static async Task<OghamSession> BuildAsync(OghamStoryManifest manifest, bool setAsMain = false)
        {
            if (manifest == null) return null;
            ApplyTags(manifest);
            ApplyLocalisations(manifest);
            await LoadAssetsAsync(manifest);
            return Storyteller.OpenSession(BuildDefinitionCore(manifest), setAsMain);
        }

        // ── Internals ─────────────────────────────────────────────────────────

        private static void ApplyTags(OghamStoryManifest m)
        {
            if (!string.IsNullOrWhiteSpace(m.StoryTagPath))
                GameplayTagRegistry.Register(m.StoryTagPath);

            foreach (var em in m.Entries)
            {
                if (!string.IsNullOrWhiteSpace(em.TagPath))
                    GameplayTagRegistry.Register(em.TagPath);

                if (em.EntryOperations != null)
                    foreach (var op in em.EntryOperations)
                    {
                        if (!string.IsNullOrWhiteSpace(op.TagPath))
                            GameplayTagRegistry.Register(op.TagPath);
                        if (!string.IsNullOrWhiteSpace(op.ValueTag))
                            GameplayTagRegistry.Register(op.ValueTag);
                    }

                foreach (var opt in em.Options)
                {
                    if (!string.IsNullOrWhiteSpace(opt.TagPath))
                        GameplayTagRegistry.Register(opt.TagPath);
                    if (!string.IsNullOrWhiteSpace(opt.TargetEntryPath))
                        GameplayTagRegistry.Register(opt.TargetEntryPath);

                    if (opt.Conditions != null)
                        foreach (var cond in opt.Conditions)
                        {
                            if (!string.IsNullOrWhiteSpace(cond.TagPath))
                                GameplayTagRegistry.Register(cond.TagPath);
                            if (!string.IsNullOrWhiteSpace(cond.CompareTagPath))
                                GameplayTagRegistry.Register(cond.CompareTagPath);
                        }

                    if (opt.Operations != null)
                        foreach (var op in opt.Operations)
                        {
                            if (!string.IsNullOrWhiteSpace(op.TagPath))
                                GameplayTagRegistry.Register(op.TagPath);
                            if (!string.IsNullOrWhiteSpace(op.ValueTag))
                                GameplayTagRegistry.Register(op.ValueTag);
                            if (op.Conditions != null)
                                foreach (var cond in op.Conditions)
                                {
                                    if (!string.IsNullOrWhiteSpace(cond.TagPath))
                                        GameplayTagRegistry.Register(cond.TagPath);
                                    if (!string.IsNullOrWhiteSpace(cond.CompareTagPath))
                                        GameplayTagRegistry.Register(cond.CompareTagPath);
                                }
                        }
                }
            }
        }

        private static void ApplyLocalisations(OghamStoryManifest m)
        {
            foreach (var loc in m.Localisations)
            {
                if (string.IsNullOrWhiteSpace(loc.Key)) continue;
                var culture = string.IsNullOrWhiteSpace(loc.Culture) ? null : loc.Culture;
                LexiconRegistry.SetString(loc.Key, loc.Value, culture);
            }
        }

        private static async Task LoadAssetsAsync(OghamStoryManifest m)
        {
            if (AssetLoader == null || m.Assets == null || m.Assets.Count == 0) return;
            foreach (var am in m.Assets)
            {
                if (string.IsNullOrWhiteSpace(am.LexiconKey)) continue;
                var source  = string.IsNullOrWhiteSpace(am.Source) ? am.LexiconKey : am.Source;
                var culture = string.IsNullOrWhiteSpace(am.Culture) ? null : am.Culture;
                var asset   = await AssetLoader(source, typeof(Object));
                if (asset != null)
                    LexiconRegistry.SetAsset(am.LexiconKey, asset, culture);
            }
        }

        // Builds the definition graph from an already tag/locale-applied manifest. No session/storyteller coupling.
        private static OghamStory BuildDefinitionCore(OghamStoryManifest m)
        {
            var story   = new OghamStory(GameplayTag.FromName(m.StoryTagPath));

            var entries = new List<DialogueEntry>(m.Entries.Count);
            foreach (var em in m.Entries)
                entries.Add(BuildEntry(em));

            story.RegisterEntries(entries);
            // Assets are streamed per node by OghamAssetStreamer (windowed + evicted), not bulk-loaded here:
            // a 100-1000 node story with images/audio/VFX/prefabs per node must not load everything at once.
            return story;
        }

        private static DialogueEntry BuildEntry(OghamEntryManifest em)
        {
            var entry = new DialogueEntry { TagPath = em.TagPath };
            if (Enum.TryParse<OghamNodeMode>(em.Mode, true, out var nodeMode))
                entry.Mode = nodeMode;

            foreach (var cm in em.ContentKeys)
                entry.ContentKeys.Add(BuildContentKey(cm));
            foreach (var om in em.EntryOperations)
                entry.EntryOperations.Add(BuildOperation(om));
            foreach (var optm in em.Options)
                entry.Options.Add(BuildOption(optm));

            return entry;
        }

        private static OghamContentKey BuildContentKey(OghamContentManifest cm)
        {
            Enum.TryParse<OghamContentType>(cm.Type, true, out var type);
            Enum.TryParse<LexiconLocMode>(cm.Mode,   true, out var mode);
            return new OghamContentKey
            {
                Type       = type,
                Mode       = mode,
                KeyOrValue = cm.KeyOrValue,
                AssetGuid  = cm.AssetGuid ?? string.Empty,
                AssetName  = cm.AssetName ?? string.Empty,
            };
        }

        /// <summary>
        /// Converts an <see cref="OghamOperationManifest"/> into a runtime <see cref="GameplayTagOperation"/>,
        /// resolving enum names and registering the tag. Used internally and by importers.
        /// </summary>
        /// <param name="om">The manifest to convert.</param>
        /// <returns>A fully configured <see cref="GameplayTagOperation"/>.</returns>
        internal static GameplayTagOperation BuildOperation(OghamOperationManifest om)
        {
            Enum.TryParse<GameplayTagArithmetic>(om.Arithmetic, true, out var arith);
            // Accept O3DE short forms for cross-engine portability: Sub→Subtract, Mul→Multiply, Div→Divide.
            if (arith == GameplayTagArithmetic.Set)
                arith = om.Arithmetic switch
                {
                    "Sub" => GameplayTagArithmetic.Subtract,
                    "Mul" => GameplayTagArithmetic.Multiply,
                    "Div" => GameplayTagArithmetic.Divide,
                    _     => arith,
                };

            var op = new GameplayTagOperation
            {
                Tag        = GameplayTag.FromName(om.TagPath),
                Arithmetic = arith,
                Value      = om.Value,
            };

            // Tag-valued operand takes precedence; otherwise honour the declared value type.
            if (!string.IsNullOrWhiteSpace(om.ValueTag))
            {
                op.ValueTag  = GameplayTag.FromName(om.ValueTag);
                op.ValueType = GameplayTagValueType.Tag;
            }
            else if (Enum.TryParse<GameplayTagValueType>(om.ValueType, true, out var vt))
            {
                op.ValueType = vt;
            }

            if (om.Conditions != null)
                foreach (var cm in om.Conditions)
                    op.Conditions.Add(BuildCondition(cm));
            return op;
        }

        /// <summary>
        /// Converts an <see cref="OghamConditionManifest"/> into a runtime <see cref="GameplayTagCondition"/>,
        /// resolving enum names and the optional compare-tag path. Used internally and by importers.
        /// </summary>
        /// <param name="cm">The manifest to convert.</param>
        /// <returns>A fully configured <see cref="GameplayTagCondition"/>.</returns>
        internal static GameplayTagCondition BuildCondition(OghamConditionManifest cm)
        {
            Enum.TryParse<GameplayTagComparisonOp>(cm.Comparison, true, out var comp);
            Enum.TryParse<GameplayTagLogicOp>(cm.LogicOp,         true, out var logic);
            var cond = new GameplayTagCondition
            {
                Tag          = GameplayTag.FromName(cm.TagPath),
                Comparison   = comp,
                CompareValue = cm.CompareValue,
                ExactMatch   = cm.ExactMatch,
                LogicOp      = logic,
            };
            if (!string.IsNullOrWhiteSpace(cm.CompareTagPath))
            {
                // A compare-tag operand implies a Tag-typed right-hand side (mirrors the condition editor).
                cond.CompareTag       = GameplayTag.FromName(cm.CompareTagPath);
                cond.CompareValueType = GameplayTagValueType.Tag;
            }
            else if (Enum.TryParse<GameplayTagValueType>(cm.CompareValueType, true, out var cvt))
            {
                cond.CompareValueType = cvt;
            }
            return cond;
        }

        private static DialogueOption BuildOption(OghamOptionManifest om)
        {
            Enum.TryParse<LexiconLocMode>(om.TextMode, true, out var mode);
            var opt = new DialogueOption
            {
                TagPath         = om.TagPath,
                TargetEntryPath = om.TargetEntryPath,
                TextKey         = new LexiconText { Mode = mode, KeyOrValue = om.TextKey },
            };
            if (om.Conditions != null)
                foreach (var cm in om.Conditions)
                    opt.Conditions.Add(BuildCondition(cm));
            if (om.Operations != null)
                foreach (var opm in om.Operations)
                    opt.Operations.Add(BuildOperation(opm));
            return opt;
        }
    }

    /// <summary>
    /// Fluent sub-builder for configuring a single dialogue entry within an <see cref="OghamStoryBuilder"/>.
    /// Call <see cref="Done"/> to return to the parent story builder when the entry is complete.
    /// </summary>
    public class OghamEntryBuilder
    {
        private readonly OghamStoryBuilder  _parent;
        private readonly OghamEntryManifest _manifest;

        internal OghamEntryBuilder(OghamStoryBuilder parent, OghamEntryManifest manifest)
        {
            _parent   = parent;
            _manifest = manifest;
        }

        /// <summary>
        /// Adds a Text content key to this entry.
        /// </summary>
        /// <param name="keyOrValue">The literal display string or Lexicon key, depending on <paramref name="localised"/>.</param>
        /// <param name="localised">When <c>true</c>, <paramref name="keyOrValue"/> is treated as a Lexicon key.</param>
        /// <returns>This entry builder, for method chaining.</returns>
        public OghamEntryBuilder AddText(string keyOrValue, bool localised = false)
        {
            _manifest.ContentKeys.Add(new OghamContentManifest
            {
                Type       = "Text",
                Mode       = localised ? "Localised" : "Literal",
                KeyOrValue = keyOrValue,
            });
            return this;
        }

        /// <summary>Adds a localised Image content key to this entry using the given Lexicon key.</summary>
        /// <param name="lexiconKey">The Lexicon key used to resolve the image asset.</param>
        /// <returns>This entry builder, for method chaining.</returns>
        public OghamEntryBuilder AddImage(string lexiconKey)
        {
            _manifest.ContentKeys.Add(new OghamContentManifest { Type = "Image", Mode = "Localised", KeyOrValue = lexiconKey });
            return this;
        }

        /// <summary>Adds a localised Audio content key to this entry using the given Lexicon key.</summary>
        /// <param name="lexiconKey">The Lexicon key used to resolve the audio clip asset.</param>
        /// <returns>This entry builder, for method chaining.</returns>
        public OghamEntryBuilder AddAudio(string lexiconKey)
        {
            _manifest.ContentKeys.Add(new OghamContentManifest { Type = "Audio", Mode = "Localised", KeyOrValue = lexiconKey });
            return this;
        }

        /// <summary>Adds a narrative-state operation that executes when this entry is entered.</summary>
        /// <param name="tagPath">The dot-path tag whose state value is modified.</param>
        /// <param name="arithmetic">The arithmetic operation name (e.g. "Set", "Add"). Defaults to "Set".</param>
        /// <param name="value">The operand value. Defaults to 1.</param>
        /// <returns>This entry builder, for method chaining.</returns>
        public OghamEntryBuilder AddOperation(string tagPath, string arithmetic = "Set", ulong value = 1)
        {
            _manifest.EntryOperations.Add(new OghamOperationManifest
            {
                TagPath    = tagPath,
                Arithmetic = arithmetic,
                Value      = value,
            });
            return this;
        }

        /// <summary>
        /// Adds a player option to this entry and returns a sub-builder for configuring conditions and operations.
        /// Call <see cref="OghamOptionBuilder.Done"/> to return to this entry builder.
        /// </summary>
        /// <param name="tagPath">The dot-path tag that identifies this option.</param>
        /// <param name="targetEntryPath">The dot-path tag of the entry to navigate to. Empty means close the conversation.</param>
        /// <param name="text">The display text or Lexicon key for this option, depending on <paramref name="localised"/>.</param>
        /// <param name="localised">When <c>true</c>, <paramref name="text"/> is treated as a Lexicon key.</param>
        /// <returns>An <see cref="OghamOptionBuilder"/> for configuring the option.</returns>
        public OghamOptionBuilder AddOption(string tagPath, string targetEntryPath, string text, bool localised = false)
        {
            var opt = new OghamOptionManifest
            {
                TagPath         = tagPath,
                TargetEntryPath = targetEntryPath ?? string.Empty,
                TextMode        = localised ? "Localised" : "Literal",
                TextKey         = text ?? string.Empty,
            };
            _manifest.Options.Add(opt);
            return new OghamOptionBuilder(this, opt);
        }

        /// <summary>Returns the parent <see cref="OghamStoryBuilder"/> to continue building the story.</summary>
        /// <returns>The parent story builder.</returns>
        public OghamStoryBuilder Done() => _parent;
    }

    /// <summary>
    /// Fluent sub-builder for configuring a single dialogue option within an <see cref="OghamEntryBuilder"/>.
    /// Call <see cref="Done"/> to return to the parent entry builder when the option is complete.
    /// </summary>
    public class OghamOptionBuilder
    {
        private readonly OghamEntryBuilder   _parent;
        private readonly OghamOptionManifest _manifest;

        internal OghamOptionBuilder(OghamEntryBuilder parent, OghamOptionManifest manifest)
        {
            _parent   = parent;
            _manifest = manifest;
        }

        /// <summary>Adds a condition that must be satisfied for this option to be available.</summary>
        /// <param name="tagPath">The dot-path tag whose state value is tested.</param>
        /// <param name="comparison">The comparison operator name (e.g. "Exists", "Equal"). Defaults to "Exists".</param>
        /// <param name="compareValue">The right-hand side value for the comparison. Defaults to 1.</param>
        /// <param name="exactMatch">When <c>true</c>, only an exact tag match is considered. Defaults to <c>true</c>.</param>
        /// <param name="logicOp">The logic operator joining this condition to the preceding one. Defaults to "And".</param>
        /// <param name="compareTagPath">Optional dot-path tag whose state value is used as the right-hand side instead of <paramref name="compareValue"/>.</param>
        /// <returns>This option builder, for method chaining.</returns>
        public OghamOptionBuilder WithCondition(string tagPath,
                                                string comparison    = "Exists",
                                                ulong  compareValue  = 1,
                                                bool   exactMatch    = true,
                                                string logicOp       = "And",
                                                string compareTagPath = "")
        {
            _manifest.Conditions.Add(new OghamConditionManifest
            {
                TagPath         = tagPath,
                Comparison      = comparison,
                CompareValue    = compareValue,
                ExactMatch      = exactMatch,
                LogicOp         = logicOp,
                CompareTagPath  = compareTagPath,
            });
            return this;
        }

        /// <summary>Adds a narrative-state operation that executes when this option is chosen.</summary>
        /// <param name="tagPath">The dot-path tag whose state value is modified.</param>
        /// <param name="arithmetic">The arithmetic operation name. Defaults to "Set".</param>
        /// <param name="value">The operand value. Defaults to 1.</param>
        /// <returns>This option builder, for method chaining.</returns>
        public OghamOptionBuilder WithOperation(string tagPath, string arithmetic = "Set", ulong value = 1)
        {
            _manifest.Operations.Add(new OghamOperationManifest
            {
                TagPath    = tagPath,
                Arithmetic = arithmetic,
                Value      = value,
            });
            return this;
        }

        /// <summary>
        /// Starts a sub-builder for an operation that carries its own conditions.
        /// Call <see cref="OghamOperationBuilder.Done"/> to return to this option builder.
        /// </summary>
        /// <param name="tagPath">The dot-path tag whose state value is modified.</param>
        /// <param name="arithmetic">The arithmetic operation name. Defaults to "Set".</param>
        /// <param name="value">The operand value. Defaults to 1.</param>
        /// <returns>An <see cref="OghamOperationBuilder"/> for configuring the operation and its conditions.</returns>
        public OghamOperationBuilder BeginOperation(string tagPath, string arithmetic = "Set", ulong value = 1)
        {
            var manifest = new OghamOperationManifest
            {
                TagPath    = tagPath,
                Arithmetic = arithmetic,
                Value      = value,
            };
            _manifest.Operations.Add(manifest);
            return new OghamOperationBuilder(this, manifest);
        }

        /// <summary>Returns the parent <see cref="OghamEntryBuilder"/> to continue building the entry.</summary>
        /// <returns>The parent entry builder.</returns>
        public OghamEntryBuilder Done() => _parent;
    }

    /// <summary>
    /// Fluent sub-builder for configuring a conditioned operation within an <see cref="OghamOptionBuilder"/>.
    /// Call <see cref="Done"/> to return to the parent option builder when the operation is complete.
    /// </summary>
    public class OghamOperationBuilder
    {
        private readonly OghamOptionBuilder    _parent;
        private readonly OghamOperationManifest _manifest;

        internal OghamOperationBuilder(OghamOptionBuilder parent, OghamOperationManifest manifest)
        {
            _parent   = parent;
            _manifest = manifest;
        }

        /// <summary>Adds a condition that gates this operation.</summary>
        /// <param name="tagPath">The dot-path tag whose state value is tested.</param>
        /// <param name="comparison">The comparison operator name. Defaults to "Exists".</param>
        /// <param name="compareValue">The right-hand side value. Defaults to 1.</param>
        /// <param name="exactMatch">When <c>true</c>, requires an exact tag match. Defaults to <c>true</c>.</param>
        /// <param name="logicOp">Logic operator joining this condition to the preceding one. Defaults to "And".</param>
        /// <param name="compareTagPath">Optional dot-path tag used as the right-hand side instead of <paramref name="compareValue"/>.</param>
        /// <returns>This operation builder, for method chaining.</returns>
        public OghamOperationBuilder WithCondition(string tagPath,
                                                   string comparison    = "Exists",
                                                   ulong  compareValue  = 1,
                                                   bool   exactMatch    = true,
                                                   string logicOp       = "And",
                                                   string compareTagPath = "")
        {
            _manifest.Conditions.Add(new OghamConditionManifest
            {
                TagPath         = tagPath,
                Comparison      = comparison,
                CompareValue    = compareValue,
                ExactMatch      = exactMatch,
                LogicOp         = logicOp,
                CompareTagPath  = compareTagPath,
            });
            return this;
        }

        /// <summary>Returns the parent <see cref="OghamOptionBuilder"/> to continue building the option.</summary>
        /// <returns>The parent option builder.</returns>
        public OghamOptionBuilder Done() => _parent;
    }
}
