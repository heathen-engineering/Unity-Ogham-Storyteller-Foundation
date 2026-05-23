using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Heathen.GameplayTags;
using Heathen.Lexicon;
using Object = UnityEngine.Object;

namespace Heathen.Ogham
{
    // Async delegate for loading assets by source path. Assign before calling BuildAsync with asset manifests.
    // Use Addressables, Resources, asset bundles, or any other system — Foundation has no dependency.
    public delegate Task<Object> OghamAssetLoader(string source, Type assetType);

    // Creates and registers OghamStory instances from code or from an OghamStoryManifest.
    //
    // Manifest path (data-driven, mod/UGC friendly):
    //   var story = await OghamStoryBuilder.BuildAsync(manifest, setAsMain: true);
    //
    // Fluent path (inline, code-first):
    //   var story = await new OghamStoryBuilder("Quests.TavernIntro")
    //       .AddLocale("en", "Quests.TavernIntro.Greeting", "Hello, traveler.")
    //       .AddEntry("Quests.TavernIntro.Node1")
    //           .AddText("Quests.TavernIntro.Greeting", localised: true)
    //           .AddOption("Quests.TavernIntro.Node1.Accept", "Quests.TavernIntro.Node2", "Accept")
    //               .Done()
    //           .Done()
    //       .BuildAsync(setAsMain: true);
    public class OghamStoryBuilder
    {
        // Assign before calling BuildAsync when the manifest contains Assets entries.
        public static OghamAssetLoader AssetLoader;

        private readonly string _storyTagPath;
        private readonly List<OghamLocaleManifest>    _locales = new();
        private readonly List<OghamAssetManifest>     _assets  = new();
        private readonly List<OghamEntryManifest>     _entries = new();

        public OghamStoryBuilder(string storyTagPath)
        {
            _storyTagPath = storyTagPath ?? string.Empty;
        }

        // ── Fluent API ────────────────────────────────────────────────────────

        public OghamStoryBuilder AddLocale(string culture, string key, string value)
        {
            _locales.Add(new OghamLocaleManifest { Culture = culture ?? string.Empty, Key = key, Value = value });
            return this;
        }

        // Register an asset source to be loaded async and injected into LexiconRegistry.
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

        public OghamEntryBuilder AddEntry(string tagPath)
        {
            var manifest = new OghamEntryManifest { TagPath = tagPath };
            _entries.Add(manifest);
            return new OghamEntryBuilder(this, manifest);
        }

        public OghamStory Build(bool setAsMain = false)
        {
            var m = ToManifest();
            ApplyTags(m);
            ApplyLocalisations(m);
            return CreateStory(m, setAsMain);
        }

        public async Task<OghamStory> BuildAsync(bool setAsMain = false)
        {
            var m = ToManifest();
            ApplyTags(m);
            ApplyLocalisations(m);
            await LoadAssetsAsync(m);
            return CreateStory(m, setAsMain);
        }

        private OghamStoryManifest ToManifest() => new OghamStoryManifest
        {
            StoryTagPath  = _storyTagPath,
            Localisations = new List<OghamLocaleManifest>(_locales),
            Assets        = new List<OghamAssetManifest>(_assets),
            Entries       = new List<OghamEntryManifest>(_entries),
        };

        // ── Static manifest entry points ──────────────────────────────────────

        public static OghamStory Build(OghamStoryManifest manifest, bool setAsMain = false)
        {
            if (manifest == null) return null;
            ApplyTags(manifest);
            ApplyLocalisations(manifest);
            return CreateStory(manifest, setAsMain);
        }

        public static async Task<OghamStory> BuildAsync(OghamStoryManifest manifest, bool setAsMain = false)
        {
            if (manifest == null) return null;
            ApplyTags(manifest);
            ApplyLocalisations(manifest);
            await LoadAssetsAsync(manifest);
            return CreateStory(manifest, setAsMain);
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
                        if (!string.IsNullOrWhiteSpace(op.TagPath))
                            GameplayTagRegistry.Register(op.TagPath);

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

        private static OghamStory CreateStory(OghamStoryManifest m, bool setAsMain)
        {
            var storyTag = GameplayTag.FromName(m.StoryTagPath);
            var story    = Storyteller.GetStory(storyTag) ?? new OghamStory(storyTag);

            var entries = new List<DialogueEntry>(m.Entries.Count);
            foreach (var em in m.Entries)
                entries.Add(BuildEntry(em));

            story.RegisterEntries(entries);
            Storyteller.RegisterStory(story, setAsMain);
            return story;
        }

        private static DialogueEntry BuildEntry(OghamEntryManifest em)
        {
            var entry = new DialogueEntry { TagPath = em.TagPath };

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
            return new OghamContentKey { Type = type, Mode = mode, KeyOrValue = cm.KeyOrValue };
        }

        internal static GameplayTagOperation BuildOperation(OghamOperationManifest om)
        {
            Enum.TryParse<GameplayTagArithmetic>(om.Arithmetic, true, out var arith);
            var op = new GameplayTagOperation
            {
                Tag        = GameplayTag.FromName(om.TagPath),
                Arithmetic = arith,
                Value      = om.Value,
            };
            if (om.Conditions != null)
                foreach (var cm in om.Conditions)
                    op.Conditions.Add(BuildCondition(cm));
            return op;
        }

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
                cond.CompareTag = GameplayTag.FromName(cm.CompareTagPath);
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

    // ── Fluent sub-builders ───────────────────────────────────────────────────

    public class OghamEntryBuilder
    {
        private readonly OghamStoryBuilder  _parent;
        private readonly OghamEntryManifest _manifest;

        internal OghamEntryBuilder(OghamStoryBuilder parent, OghamEntryManifest manifest)
        {
            _parent   = parent;
            _manifest = manifest;
        }

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

        public OghamEntryBuilder AddImage(string lexiconKey)
        {
            _manifest.ContentKeys.Add(new OghamContentManifest { Type = "Image", Mode = "Localised", KeyOrValue = lexiconKey });
            return this;
        }

        public OghamEntryBuilder AddAudio(string lexiconKey)
        {
            _manifest.ContentKeys.Add(new OghamContentManifest { Type = "Audio", Mode = "Localised", KeyOrValue = lexiconKey });
            return this;
        }

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

        public OghamStoryBuilder Done() => _parent;
    }

    public class OghamOptionBuilder
    {
        private readonly OghamEntryBuilder   _parent;
        private readonly OghamOptionManifest _manifest;

        internal OghamOptionBuilder(OghamEntryBuilder parent, OghamOptionManifest manifest)
        {
            _parent   = parent;
            _manifest = manifest;
        }

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

        // Returns a sub-builder for an operation that carries its own conditions.
        // Call .Done() to return to the option builder.
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

        public OghamEntryBuilder Done() => _parent;
    }

    public class OghamOperationBuilder
    {
        private readonly OghamOptionBuilder    _parent;
        private readonly OghamOperationManifest _manifest;

        internal OghamOperationBuilder(OghamOptionBuilder parent, OghamOperationManifest manifest)
        {
            _parent   = parent;
            _manifest = manifest;
        }

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

        public OghamOptionBuilder Done() => _parent;
    }
}
