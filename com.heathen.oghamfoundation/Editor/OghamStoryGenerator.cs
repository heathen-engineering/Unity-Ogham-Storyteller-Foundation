using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using UnityEditor;
using UnityEngine;
using Heathen.Editor;            // ISettingsGenerator, GeneratorOutput
using Heathen.GameplayTags;       // CompiledTagEntry, GameplayTagRegistry
using Heathen.GameplayTags.Editor; // GameplayTagsCompiler, GameplayTagsCodeGenerator.SanitizeMember
using Heathen.Lexicon.Editor;     // LexiconAddressables (build-time addressable marking)

namespace Heathen.Ogham.Editor
{
    /// <summary>
    /// Bakes each <c>.ogham</c> JSON source into a <c>.g.cs</c> so the runtime needs no file read: a baked
    /// tag <c>RegisterTags()</c> run at load (mirrors <see cref="GameplayTagsCodeGenerator"/>) plus the
    /// <see cref="OghamStoryManifest"/> emitted as object-init code, built via
    /// <see cref="OghamStoryBuilder.Build(OghamStoryManifest, bool)"/>. Plugs into the Game Framework
    /// generator pipeline (<see cref="ISettingsGenerator"/>): the shared build hook guards staleness and the
    /// shared menu (plus the Ogham menu item here) drives generation.
    /// </summary>
    public sealed class OghamStoryGenerator : ISettingsGenerator
    {
        public const string GeneratedNamespace = "Heathen.Ogham.Generated";
        public const string GeneratedFolder    = "Generated";
        private const string HashMarker = "// ogham-hash:0x";

        // ── ISettingsGenerator ──────────────────────────────────────────────────

        public string Name => "Ogham Stories";
        public GeneratorOutput Output => GeneratorOutput.SourceCode;

        public bool IsStale()
        {
            foreach (var path in Sources())
                if (IsStale(path)) return true;
            return false;
        }

        public void Generate()
        {
            foreach (var path in Sources())
                Generate(path);
            LexiconAddressables.Save();
        }

        [MenuItem("Tools/Heathen/Ogham/Generate Story Code")]
        private static void GenerateAllMenu()
        {
            int n = 0, total = 0;
            foreach (var path in Sources()) { total++; if (Generate(path)) n++; }
            LexiconAddressables.Save();
            AssetDatabase.Refresh();
            Debug.Log($"[Ogham] Generated story code for {n} of {total} .ogham file(s).");
        }

        // ── Per-file generation ─────────────────────────────────────────────────

        private static IEnumerable<string> Sources()
        {
            string root = Application.dataPath;
            foreach (var full in Directory.GetFiles(root, "*.ogham", SearchOption.AllDirectories))
            {
                // Skip Unity hidden folders (e.g. Samples~, anything ending in '~'): they are never imported
                // or compiled, so their generated code can never exist and would always read as "stale".
                var norm = full.Replace('\\', '/');
                if (norm.Contains("~/")) continue;
                yield return "Assets" + full.Substring(root.Length).Replace('\\', '/');
            }
        }

        /// <summary>Generate the <c>.g.cs</c> for one <c>.ogham</c>. Returns false when the source can't be read/parsed.</summary>
        public static bool Generate(string oghamAssetPath)
        {
            if (!TryLoad(oghamAssetPath, out var doc)) return false;

            string className = ClassNameFor(oghamAssetPath, doc);
            string source    = GenerateSource(className, doc);

            string outPath = GeneratedPathFor(oghamAssetPath);
            Directory.CreateDirectory(Path.GetDirectoryName(outPath));
            File.WriteAllText(outPath, source);

            // Mark every referenced asset addressable (address = GUID) so baked GUID-keyed content ships and
            // resolves at runtime. Lexicon owns asset delivery, so the marking lives there. Save() is deferred
            // to the caller so a multi-file run persists the Addressables settings once.
            MarkAddressables(doc);
            return true;
        }

        // Gives every non-text literal asset content key's GUID an addressable entry. The single-file menu/toolbar
        // path is also covered because this runs inside Generate(path); orchestrators call Save() afterwards.
        private static void MarkAddressables(OghamJsonDocument doc)
        {
            foreach (var entry in doc.ToManifest().Entries)
                foreach (var key in entry.ContentKeys)
                    if (!string.IsNullOrEmpty(key.AssetGuid))
                        LexiconAddressables.EnsureAddressable(key.AssetGuid);
        }

        // The generated class is named after the story's TAG (its identity), so two stories sharing a tag
        // collide at compile time (a real authoring error). Falls back to the file name when untagged.
        private static string ClassNameFor(string oghamAssetPath, OghamJsonDocument doc)
        {
            string tag   = doc.StoryTag;
            string basis = string.IsNullOrWhiteSpace(tag)
                ? Path.GetFileNameWithoutExtension(oghamAssetPath)
                : tag.Replace('.', '_'); // dot-path → valid identifier (Story.MainQuest → Story_MainQuest)
            return GameplayTagsCodeGenerator.SanitizeMember(basis);
        }

        /// <summary>
        /// The absolute path of the <c>.g.cs</c> a given <c>.ogham</c> generates — named after the story's tag
        /// (its identity), in a <c>Generated</c> folder beside the source. Falls back to the file name when the
        /// source is untagged or unreadable.
        /// </summary>
        public static string GeneratedPathFor(string oghamAssetPath)
        {
            string full = Path.GetFullPath(oghamAssetPath);
            string name = TryLoad(oghamAssetPath, out var doc)
                ? ClassNameFor(oghamAssetPath, doc)
                : GameplayTagsCodeGenerator.SanitizeMember(Path.GetFileNameWithoutExtension(oghamAssetPath));
            string dir = Path.Combine(Path.GetDirectoryName(full) ?? ".", GeneratedFolder);
            return Path.Combine(dir, name + ".g.cs");
        }

        /// <summary>True when a <c>.ogham</c> has no generated file or one whose content hash is behind the source.</summary>
        public static bool IsStale(string oghamAssetPath)
        {
            if (!TryLoad(oghamAssetPath, out var doc)) return false;

            ulong want = ContentHash(doc);
            string outPath = GeneratedPathFor(oghamAssetPath);
            if (!File.Exists(outPath)) return true;

            foreach (var line in File.ReadLines(outPath))
            {
                int idx = line.IndexOf(HashMarker, StringComparison.Ordinal);
                if (idx < 0) continue;
                // Take just the hex token; the marker line has a trailing "  // staleness marker …" comment
                // that must not be fed to the parser (otherwise it always fails → always "stale").
                string hex = line.Substring(idx + HashMarker.Length).Trim().Split(new[] { ' ', '\t' }, 2)[0];
                return !(ulong.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong have) && have == want);
            }
            return true; // no marker → regenerate
        }

        private static bool TryLoad(string oghamAssetPath, out OghamJsonDocument doc)
        {
            doc = null;
            try { doc = OghamJsonDocument.Parse(File.ReadAllText(Path.GetFullPath(oghamAssetPath))); return true; }
            catch { return false; }
        }

        // Hash the runtime-relevant data only (the manifest), so editor-layout edits in the _editor block
        // don't churn the baked code.
        private static ulong ContentHash(OghamJsonDocument doc) =>
            GameplayTagRegistry.Hash(JsonConvert.SerializeObject(doc.ToManifest()));

        // ── Emit ────────────────────────────────────────────────────────────────

        /// <summary>Pure code emitter (no I/O): the baked tag registration + story manifest for one document.</summary>
        public static string GenerateSource(string className, OghamJsonDocument doc)
        {
            var manifest = doc.ToManifest();

            var tagPaths = new List<string>(doc.GetAllTagPaths());
            tagPaths.Sort(StringComparer.Ordinal);
            var entries = GameplayTagsCompiler.BuildEntries(tagPaths.ToArray());

            var sb = new StringBuilder();
            sb.AppendLine("// <auto-generated>");
            sb.AppendLine("//   Generated from a .ogham source by OghamStoryGenerator. DO NOT EDIT.");
            sb.AppendLine("//   Edit the .ogham file and re-run Tools ▸ Heathen ▸ Ogham ▸ Generate Story Code.");
            sb.AppendLine("// </auto-generated>");
            sb.AppendLine($"{HashMarker}{ContentHash(doc):X16}  // staleness marker — do not edit");
            sb.AppendLine("using System.Collections.Generic;");
            sb.AppendLine("using Heathen.GameplayTags;");
            sb.AppendLine("using Heathen.Ogham;");
            sb.AppendLine("using UnityEngine;");
            sb.AppendLine();
            sb.AppendLine($"namespace {GeneratedNamespace}");
            sb.AppendLine("{");
            sb.AppendLine($"    public static class {className}");
            sb.AppendLine("    {");

            // ── Baked tags: registered at load, no .gptags, no runtime file read. ──
            sb.AppendLine("        static readonly CompiledTagEntry[] _bakedTags =");
            sb.AppendLine("        {");
            foreach (var e in entries)
                sb.AppendLine($"            new CompiledTagEntry {{ Id = 0x{e.Id:X16}UL, ParentId = 0x{e.ParentId:X16}UL, Name = {Q(e.Name)} }},");
            sb.AppendLine("        };");
            sb.AppendLine();
            sb.AppendLine("        /// <summary>Registers this story's baked tags and manifest at load (tag-addressed; no SO, no file read).</summary>");
            sb.AppendLine("        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]");
            sb.AppendLine("        public static void Register()");
            sb.AppendLine("        {");
            sb.AppendLine("            GameplayTagRegistry.RegisterBaked(_bakedTags);");
            sb.AppendLine("            OghamStoryCatalog.Register(Manifest);");
            sb.AppendLine("        }");
            sb.AppendLine();

            // ── Baked story manifest: built at runtime via the no-SO builder. ──
            sb.AppendLine("        /// <summary>The baked story manifest (no ScriptableObject, no runtime file read).</summary>");
            sb.AppendLine("        public static OghamStoryManifest Manifest =>");
            EmitManifest(sb, manifest, "            ");
            sb.AppendLine(";");
            sb.AppendLine();
            sb.AppendLine("        /// <summary>Opens a play session for this story from the baked manifest.</summary>");
            sb.AppendLine("        public static OghamSession Build(bool setAsMain = false) => OghamStoryBuilder.Build(Manifest, setAsMain);");

            sb.AppendLine("    }");
            sb.AppendLine("}");
            return sb.ToString();
        }

        private static void EmitManifest(StringBuilder sb, OghamStoryManifest m, string ind)
        {
            sb.AppendLine($"{ind}new OghamStoryManifest");
            sb.AppendLine($"{ind}{{");
            sb.AppendLine($"{ind}    StoryTagPath = {Q(m.StoryTagPath)},");
            EmitList(sb, ind, "Localisations", "OghamLocaleManifest", m.Localisations,
                (s, l) => s.AppendLine($"{ind}        new OghamLocaleManifest {{ Culture = {Q(l.Culture)}, Key = {Q(l.Key)}, Value = {Q(l.Value)} }},"));
            EmitList(sb, ind, "Assets", "OghamAssetManifest", m.Assets,
                (s, a) => s.AppendLine($"{ind}        new OghamAssetManifest {{ LexiconKey = {Q(a.LexiconKey)}, Source = {Q(a.Source)}, Culture = {Q(a.Culture)} }},"));
            if (m.Entries.Count > 0)
            {
                sb.AppendLine($"{ind}    Entries = new List<OghamEntryManifest>");
                sb.AppendLine($"{ind}    {{");
                foreach (var e in m.Entries) EmitEntry(sb, e, ind + "        ");
                sb.AppendLine($"{ind}    }},");
            }
            sb.Append($"{ind}}}"); // caller appends ';'
        }

        private static void EmitEntry(StringBuilder sb, OghamEntryManifest e, string ind)
        {
            sb.AppendLine($"{ind}new OghamEntryManifest");
            sb.AppendLine($"{ind}{{");
            sb.AppendLine($"{ind}    TagPath = {Q(e.TagPath)},");
            // Emit the node mode only when it is not the default ("Content"), so Fork nodes route silently at
            // runtime instead of presenting as content nodes.
            if (!string.IsNullOrEmpty(e.Mode) && e.Mode != "Content")
                sb.AppendLine($"{ind}    Mode = {Q(e.Mode)},");
            EmitList(sb, ind, "ContentKeys", "OghamContentManifest", e.ContentKeys,
                (s, c) =>
                {
                    var fields = $"Type = {Q(c.Type)}, Mode = {Q(c.Mode)}, KeyOrValue = {Q(c.KeyOrValue)}";
                    if (!string.IsNullOrEmpty(c.AssetGuid)) fields += $", AssetGuid = {Q(c.AssetGuid)}";
                    if (!string.IsNullOrEmpty(c.AssetName)) fields += $", AssetName = {Q(c.AssetName)}";
                    s.AppendLine($"{ind}        new OghamContentManifest {{ {fields} }},");
                });
            if (e.EntryOperations.Count > 0)
            {
                sb.AppendLine($"{ind}    EntryOperations = new List<OghamOperationManifest>");
                sb.AppendLine($"{ind}    {{");
                foreach (var op in e.EntryOperations) EmitOperation(sb, op, ind + "        ");
                sb.AppendLine($"{ind}    }},");
            }
            if (e.Options.Count > 0)
            {
                sb.AppendLine($"{ind}    Options = new List<OghamOptionManifest>");
                sb.AppendLine($"{ind}    {{");
                foreach (var o in e.Options) EmitOption(sb, o, ind + "        ");
                sb.AppendLine($"{ind}    }},");
            }
            sb.AppendLine($"{ind}}},");
        }

        private static void EmitOption(StringBuilder sb, OghamOptionManifest o, string ind)
        {
            sb.AppendLine($"{ind}new OghamOptionManifest");
            sb.AppendLine($"{ind}{{");
            sb.AppendLine($"{ind}    TagPath = {Q(o.TagPath)}, TargetEntryPath = {Q(o.TargetEntryPath)}, TextMode = {Q(o.TextMode)}, TextKey = {Q(o.TextKey)},");
            if (o.Conditions.Count > 0)
            {
                sb.AppendLine($"{ind}    Conditions = new List<OghamConditionManifest>");
                sb.AppendLine($"{ind}    {{");
                foreach (var c in o.Conditions) EmitCondition(sb, c, ind + "        ");
                sb.AppendLine($"{ind}    }},");
            }
            if (o.Operations.Count > 0)
            {
                sb.AppendLine($"{ind}    Operations = new List<OghamOperationManifest>");
                sb.AppendLine($"{ind}    {{");
                foreach (var op in o.Operations) EmitOperation(sb, op, ind + "        ");
                sb.AppendLine($"{ind}    }},");
            }
            sb.AppendLine($"{ind}}},");
        }

        private static void EmitOperation(StringBuilder sb, OghamOperationManifest op, string ind)
        {
            string head = $"TagPath = {Q(op.TagPath)}, Arithmetic = {Q(op.Arithmetic)}, Value = {op.Value}UL, ValueTag = {Q(op.ValueTag)}, ValueType = {Q(op.ValueType)}";
            if (op.Conditions.Count == 0)
            {
                sb.AppendLine($"{ind}new OghamOperationManifest {{ {head} }},");
                return;
            }
            sb.AppendLine($"{ind}new OghamOperationManifest");
            sb.AppendLine($"{ind}{{");
            sb.AppendLine($"{ind}    {head},");
            sb.AppendLine($"{ind}    Conditions = new List<OghamConditionManifest>");
            sb.AppendLine($"{ind}    {{");
            foreach (var c in op.Conditions) EmitCondition(sb, c, ind + "        ");
            sb.AppendLine($"{ind}    }},");
            sb.AppendLine($"{ind}}},");
        }

        private static void EmitCondition(StringBuilder sb, OghamConditionManifest c, string ind) =>
            sb.AppendLine($"{ind}new OghamConditionManifest {{ TagPath = {Q(c.TagPath)}, Comparison = {Q(c.Comparison)}, " +
                          $"CompareValue = {c.CompareValue}UL, CompareTagPath = {Q(c.CompareTagPath)}, " +
                          $"CompareValueType = {Q(c.CompareValueType)}, " +
                          $"ExactMatch = {(c.ExactMatch ? "true" : "false")}, LogicOp = {Q(c.LogicOp)} }},");

        private static void EmitList<T>(StringBuilder sb, string ind, string field, string itemType,
                                        List<T> items, Action<StringBuilder, T> emitItem)
        {
            if (items == null || items.Count == 0) return;
            sb.AppendLine($"{ind}    {field} = new List<{itemType}>");
            sb.AppendLine($"{ind}    {{");
            foreach (var item in items) emitItem(sb, item);
            sb.AppendLine($"{ind}    }},");
        }

        /// <summary>C# string literal: escapes quotes, backslashes and control chars. Null → <c>null</c>.</summary>
        private static string Q(string s)
        {
            if (s == null) return "null";
            var sb = new StringBuilder(s.Length + 2);
            sb.Append('"');
            foreach (char c in s)
                switch (c)
                {
                    case '"':  sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n");  break;
                    case '\r': sb.Append("\\r");  break;
                    case '\t': sb.Append("\\t");  break;
                    default:   sb.Append(c);      break;
                }
            sb.Append('"');
            return sb.ToString();
        }
    }
}
