using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using Heathen.GameplayTags;
using Heathen.Lexicon;

namespace Heathen.Ogham.Editor
{
    /// <summary>
    /// Bidirectional adapter between <c>.ogham</c> JSON source files and Unity runtime objects.
    /// Supports three reading paths: <see cref="ToOghamData"/> (authoring format, Markdown links preserved),
    /// <see cref="ToMetadata"/> (<see cref="OghamGraphMetadata"/> from the <c>_editor</c> block), and
    /// <see cref="ToCompiledData"/> (runtime format with inline links converted to TMPro markup).
    /// The writing path is <see cref="SyncFrom"/> followed by <see cref="ToJson"/>.
    /// </summary>
    public class OghamJsonDocument
    {
        private JObject _root;

        private OghamJsonDocument(JObject root) => _root = root;

        /// <summary>
        /// Parses a JSON string and returns an <see cref="OghamJsonDocument"/>. Returns a document with an empty
        /// root object when the JSON is null, empty, or malformed.
        /// </summary>
        /// <param name="json">The JSON string to parse.</param>
        /// <returns>A new <see cref="OghamJsonDocument"/> backed by the parsed root object.</returns>
        public static OghamJsonDocument Parse(string json)
        {
            try
            {
                var root = JToken.Parse(json) as JObject ?? new JObject();
                return new OghamJsonDocument(root);
            }
            catch
            {
                return new OghamJsonDocument(new JObject());
            }
        }

        /// <summary>
        /// Creates a new <see cref="OghamJsonDocument"/> with an empty entries array and an optional story tag.
        /// </summary>
        /// <param name="storyTag">The dot-path story tag to write into the document's <c>storyTag</c> field.</param>
        /// <returns>A new, empty <see cref="OghamJsonDocument"/>.</returns>
        public static OghamJsonDocument CreateNew(string storyTag = "")
        {
            var root = new JObject
            {
                ["storyTag"] = storyTag,
                ["entries"]  = new JArray(),
            };
            return new OghamJsonDocument(root);
        }

        /// <summary>The dot-path story tag read from the document's <c>storyTag</c> field.</summary>
        public string StoryTag => _root["storyTag"]?.Value<string>() ?? string.Empty;

        /// <summary>
        /// Converts the document to an authoring <see cref="OghamData"/> object with Markdown inline links preserved.
        /// Use this path in the graph editor. The returned instance is a synthetic ScriptableObject (not in AssetDatabase).
        /// </summary>
        /// <returns>A new <see cref="OghamData"/> populated from the document's entries.</returns>
        public OghamData ToOghamData()
        {
            var data = ScriptableObject.CreateInstance<OghamData>();
            if (_root["entries"] is not JArray entries) return data;

            foreach (var entryNode in entries)
            {
                if (entryNode is not JObject eo) continue;
                var entry = new DialogueEntry
                {
                    TagPath = eo["tag"]?.Value<string>() ?? string.Empty,
                };
                if (eo["nodeMode"] is JValue nodeModeTok &&
                    Enum.TryParse<OghamNodeMode>(nodeModeTok.Value<string>(), true, out var nm))
                    entry.Mode = nm;
                ParseContentKeys(eo, entry.ContentKeys);
                ParseOperations(eo["entryOperations"] as JArray, entry.EntryOperations);
                ParseOptions(eo["options"] as JArray, entry.Options);
                data.Entries.Add(entry);
            }
            return data;
        }

        /// <summary>
        /// Converts the document's <c>_editor</c> block to an <see cref="OghamGraphMetadata"/> object containing
        /// the graph editor view state, label definitions, and per-node layout data.
        /// </summary>
        /// <returns>A new <see cref="OghamGraphMetadata"/> populated from the <c>_editor</c> block.</returns>
        public OghamGraphMetadata ToMetadata()
        {
            var meta = ScriptableObject.CreateInstance<OghamGraphMetadata>();
            if (_root["_editor"] is not JObject editor) return meta;

            if (editor["viewTransform"] is JArray vt && vt.Count == 3)
                meta.ViewTransform = new Vector3(
                    vt[0]?.Value<float>() ?? 0f,
                    vt[1]?.Value<float>() ?? 0f,
                    vt[2]?.Value<float>() ?? 1f);

            if (editor["labels"] is JArray labels)
                foreach (var lbl in labels)
                    if (lbl is JObject lo)
                        meta.Labels.Add(new OghamLabelDef
                        {
                            Id    = lo["id"]?.Value<int>()    ?? 0,
                            Name  = lo["name"]?.Value<string>() ?? string.Empty,
                            Color = ParseColor(lo["color"]?.Value<string>(), Color.white),
                        });

            if (editor["headerColor"] is JToken hcToken)
                meta.HeaderColor = ParseColor(hcToken.Value<string>(), Color.clear);

            if (editor["nodes"] is JArray nodes)
                foreach (var n in nodes)
                    if (n is JObject no)
                        meta.Nodes.Add(ParseNodeMeta(no));

            return meta;
        }

        /// <summary>
        /// Converts the document to an <see cref="OghamCompiledData"/> runtime asset, converting inline links
        /// to TMPro markup and dropping pure-link content keys. Used by <see cref="OghamImporter"/>.
        /// </summary>
        /// <returns>A new <see cref="OghamCompiledData"/> ready for use at runtime.</returns>
        public OghamCompiledData ToCompiledData()
        {
            var compiled = ScriptableObject.CreateInstance<OghamCompiledData>();
            compiled.StoryTagPath = StoryTag;

            // Build authoring entries then compile them (inline links → TMPro).
            var authoring = ToOghamData();
            authoring.BuildIndex(); // ensures inline-link options are synthesised
            foreach (var entry in authoring.Entries)
                compiled.Entries.Add(OghamCompiledData.CompileEntry(entry));
            compiled.BuildIndex();

            // Inline localisations.
            compiled.Localisations = ParseLocalisations();

            ScriptableObject.DestroyImmediate(authoring);
            return compiled;
        }

        /// <summary>
        /// Updates the document's <c>entries</c> array and <c>_editor</c> block from the given live objects.
        /// Preserves the <c>storyTag</c>, <c>localisations</c>, and <c>assets</c> fields. Call <see cref="ToJson"/>
        /// afterwards to serialise the updated document to disk.
        /// </summary>
        /// <param name="data">The live authoring data to write. Ignored when <c>null</c>.</param>
        /// <param name="meta">The live graph metadata to write. Ignored when <c>null</c>.</param>
        public void SyncFrom(OghamData data, OghamGraphMetadata meta)
        {
            if (data != null) _root["entries"] = BuildEntriesArray(data.Entries);
            if (meta != null) _root["_editor"] = BuildEditorBlock(meta);
        }

        /// <summary>Sets the <c>storyTag</c> field in the document to the given dot-path string.</summary>
        /// <param name="storyTag">The new story tag dot-path value.</param>
        public void SetStoryTag(string storyTag) => _root["storyTag"] = storyTag;

        /// <summary>Serialises the document back to indented JSON, suitable for writing to a <c>.ogham</c> file.</summary>
        /// <returns>The formatted JSON string.</returns>
        public string ToJson() => _root.ToString(Formatting.Indented);

        // ── Parse helpers ─────────────────────────────────────────────────────

        private static void ParseContentKeys(JObject eo, List<OghamContentKey> keys)
        {
            // New format: contentKeys[]
            if (eo["contentKeys"] is JArray ck)
            {
                foreach (var k in ck)
                {
                    if (k is not JObject ko) continue;
                    Enum.TryParse<OghamContentType>(ko["type"]?.Value<string>() ?? "Text", true, out var type);
                    Enum.TryParse<LexiconLocMode>(  ko["mode"]?.Value<string>() ?? "Literal", true, out var mode);
                    var contentKey = new OghamContentKey
                    {
                        Type       = type,
                        Mode       = mode,
                        KeyOrValue = ko["key"]?.Value<string>() ?? string.Empty,
                    };
                    if (type != OghamContentType.Text && mode == LexiconLocMode.Literal)
                    {
                        var guidStr = ko["assetGuid"]?.Value<string>();
                        if (!string.IsNullOrEmpty(guidStr))
                        {
                            var assetPath = AssetDatabase.GUIDToAssetPath(guidStr);
                            if (!string.IsNullOrEmpty(assetPath))
                            {
                                if (type == OghamContentType.Sprite)
                                {
                                    // Sprites are sub-assets — search by name so sprite sheets work.
                                    var spriteName = ko["assetName"]?.Value<string>();
                                    var all = AssetDatabase.LoadAllAssetsAtPath(assetPath);
                                    foreach (var a in all)
                                    {
                                        if (a is Sprite s && (string.IsNullOrEmpty(spriteName) || s.name == spriteName))
                                        { contentKey.AssetRef = s; break; }
                                    }
                                }
                                else
                                {
                                    contentKey.AssetRef = type switch {
                                        OghamContentType.Audio  => AssetDatabase.LoadAssetAtPath<AudioClip>(assetPath),
                                        OghamContentType.Prefab => AssetDatabase.LoadAssetAtPath<GameObject>(assetPath),
                                        _                       => AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(assetPath),
                                    };
                                }
                                if (contentKey.AssetRef != null && string.IsNullOrEmpty(contentKey.KeyOrValue))
                                    contentKey.KeyOrValue = contentKey.AssetRef.name;
                            }
                        }
                    }
                    keys.Add(contentKey);
                }
                return;
            }

            // Legacy O3DE format: dataKeys[] or textKeys[] → Text + Localised
            var legacyArr = eo["dataKeys"] as JArray ?? eo["textKeys"] as JArray;
            if (legacyArr != null)
                foreach (var k in legacyArr)
                    if (k?.Value<string>() is { } s)
                        keys.Add(new OghamContentKey { Type = OghamContentType.Text, Mode = LexiconLocMode.Localised, KeyOrValue = s });
        }

        private static void ParseOperations(JArray arr, List<GameplayTagOperation> ops)
        {
            if (arr == null) return;
            foreach (var o in arr)
            {
                if (o is not JObject oo) continue;
                Enum.TryParse<GameplayTagArithmetic>(oo["arithmetic"]?.Value<string>() ?? "Set", true, out var arith);
                // Accept O3DE short forms: Sub→Subtract, Mul→Multiply, Div→Divide
                if (arith == GameplayTagArithmetic.Set && oo["arithmetic"]?.Value<string>() is { } raw)
                    arith = raw switch { "Sub" => GameplayTagArithmetic.Subtract, "Mul" => GameplayTagArithmetic.Multiply, "Div" => GameplayTagArithmetic.Divide, _ => arith };

                var op = new GameplayTagOperation
                {
                    Tag        = HashTag(oo["tag"]?.Value<string>()),
                    Arithmetic = arith,
                    Value      = oo["value"]?.Value<ulong>() ?? 0UL,
                };
                var valueTagStr = oo["valueTag"]?.Value<string>();
                if (!string.IsNullOrWhiteSpace(valueTagStr))
                {
                    op.ValueTag  = HashTag(valueTagStr);
                    op.ValueType = GameplayTagValueType.Tag;
                }
                else if (oo["valueType"] is JValue vtTok &&
                         Enum.TryParse<GameplayTagValueType>(vtTok.Value<string>(), true, out var vt))
                {
                    op.ValueType = vt;
                }
                ParseConditions(oo["conditions"] as JArray, op.Conditions);
                ops.Add(op);
            }
        }

        private static void ParseConditions(JArray arr, List<GameplayTagCondition> conds)
        {
            if (arr == null) return;
            foreach (var c in arr)
            {
                if (c is not JObject co) continue;
                Enum.TryParse<GameplayTagComparisonOp>(co["comparison"]?.Value<string>() ?? "Exists", true, out var comp);
                Enum.TryParse<GameplayTagLogicOp>(     co["logicOp"]?.Value<string>()    ?? "And",    true, out var logic);
                var cond = new GameplayTagCondition
                {
                    Tag          = HashTag(co["tag"]?.Value<string>()),
                    Comparison   = comp,
                    CompareValue = co["compareValue"]?.Value<ulong>() ?? 0UL,
                    ExactMatch   = co["exactMatch"]?.Value<bool>() ?? true,
                    LogicOp      = logic,
                };
                var compareTagStr = co["compareTag"]?.Value<string>();
                if (!string.IsNullOrWhiteSpace(compareTagStr))
                    cond.CompareTag = HashTag(compareTagStr);
                conds.Add(cond);
            }
        }

        private static void ParseOptions(JArray arr, List<DialogueOption> opts)
        {
            if (arr == null) return;
            foreach (var o in arr)
            {
                if (o is not JObject oo) continue;
                var opt = new DialogueOption
                {
                    TagPath         = oo["tag"]?.Value<string>()        ?? string.Empty,
                    TargetEntryPath = (oo["targetTag"] ?? oo["targetEntry"])?.Value<string>() ?? string.Empty,
                };

                // textKey: string or {"mode":..., "key":...}
                if (oo["textKey"] is JValue tv)
                {
                    var tvStr = tv.Value<string>() ?? string.Empty;
                    Enum.TryParse<LexiconLocMode>(oo["textMode"]?.Value<string>() ?? "Literal", true, out var tMode);
                    opt.TextKey = new LexiconText { Mode = tMode, KeyOrValue = tvStr };
                }
                else if (oo["textKey"] is JObject tko)
                {
                    Enum.TryParse<LexiconLocMode>(tko["mode"]?.Value<string>() ?? "Literal", true, out var tMode);
                    opt.TextKey = new LexiconText { Mode = tMode, KeyOrValue = tko["key"]?.Value<string>() ?? string.Empty };
                }

                ParseConditions(oo["conditions"] as JArray, opt.Conditions);
                ParseOperations(oo["operations"] as JArray, opt.Operations);
                opts.Add(opt);
            }
        }

        private OghamCompiledLocale[] ParseLocalisations()
        {
            if (_root["localisations"] is not JArray arr) return Array.Empty<OghamCompiledLocale>();
            var result = new List<OghamCompiledLocale>(arr.Count);
            foreach (var l in arr)
            {
                if (l is not JObject lo) continue;
                result.Add(new OghamCompiledLocale
                {
                    Culture = lo["culture"]?.Value<string>() ?? string.Empty,
                    Key     = lo["key"]?.Value<string>()     ?? string.Empty,
                    Value   = lo["value"]?.Value<string>()   ?? string.Empty,
                });
            }
            return result.ToArray();
        }

        /// <summary>
        /// Returns an enumerable of all dot-path tag strings referenced anywhere in this document, including
        /// the story tag, entry tags, option tags, target tags, and condition/operation tags.
        /// Used by <see cref="OghamImporter"/> and <see cref="OghamImporterRefresh"/> to pre-register tags.
        /// </summary>
        /// <returns>A de-duplicated set of tag path strings.</returns>
        public IEnumerable<string> GetAllTagPaths()
        {
            var paths = new HashSet<string>(StringComparer.Ordinal);
            var st = StoryTag;
            if (!string.IsNullOrWhiteSpace(st)) paths.Add(st.Trim());
            if (_root["entries"] is JArray entries)
                foreach (var e in entries)
                    if (e is JObject eo) CollectEntryTagPaths(eo, paths);
            return paths;
        }

        /// <summary>
        /// Returns all inline localisation entries from the document's <c>localisations</c> array.
        /// Used by <see cref="OghamImporterRefresh"/> to inject strings into <see cref="Heathen.Lexicon.LexiconRegistry"/> on editor load.
        /// </summary>
        /// <returns>An array of <see cref="OghamCompiledLocale"/> values, or an empty array when none are defined.</returns>
        public OghamCompiledLocale[] GetLocalisations() => ParseLocalisations();

        private static void CollectEntryTagPaths(JObject eo, HashSet<string> paths)
        {
            AddTagPath(paths, eo["tag"]?.Value<string>());
            CollectOperationTagPaths(eo["entryOperations"] as JArray, paths);
            if (eo["options"] is JArray opts)
                foreach (var o in opts)
                    if (o is JObject oo)
                    {
                        AddTagPath(paths, oo["tag"]?.Value<string>());
                        AddTagPath(paths, (oo["targetTag"] ?? oo["targetEntry"])?.Value<string>());
                        CollectOperationTagPaths(oo["operations"] as JArray, paths);
                        CollectConditionTagPaths(oo["conditions"] as JArray, paths);
                    }
        }

        private static void CollectOperationTagPaths(JArray arr, HashSet<string> paths)
        {
            if (arr == null) return;
            foreach (var o in arr)
                if (o is JObject oo)
                {
                    AddTagPath(paths, oo["tag"]?.Value<string>());
                    AddTagPath(paths, oo["valueTag"]?.Value<string>());
                    CollectConditionTagPaths(oo["conditions"] as JArray, paths);
                }
        }

        private static void CollectConditionTagPaths(JArray arr, HashSet<string> paths)
        {
            if (arr == null) return;
            foreach (var c in arr)
                if (c is JObject co)
                {
                    AddTagPath(paths, co["tag"]?.Value<string>());
                    AddTagPath(paths, co["compareTag"]?.Value<string>());
                }
        }

        private static void AddTagPath(HashSet<string> paths, string p)
        {
            if (!string.IsNullOrWhiteSpace(p)) paths.Add(p.Trim());
        }

        private static OghamNodeMeta ParseNodeMeta(JObject no)
        {
            var nm = new OghamNodeMeta
            {
                TagName        = no["tag"]?.Value<string>()          ?? string.Empty,
                LabelText      = no["label"]?.Value<string>()        ?? string.Empty,
                LabelColor     = ParseColor(no["labelColor"]?.Value<string>(), Color.white),
                IsCollapsed    = no["collapsed"]?.Value<bool>()      ?? false,
                OpsExpanded    = no["opsExpanded"]?.Value<bool>()    ?? false,
                FieldsExpanded = no["fieldsExpanded"]?.Value<bool>() ?? true,
                ChoicesExpanded= no["choicesExpanded"]?.Value<bool>()  ?? true,
                HighlightColor = ParseColor(no["highlightColor"]?.Value<string>(), Color.clear),
            };

            if (no["position"] is JArray pos && pos.Count == 4)
                nm.Position = new Rect(pos[0]?.Value<float>() ?? 0f, pos[1]?.Value<float>() ?? 0f,
                                       pos[2]?.Value<float>() ?? 300f, pos[3]?.Value<float>() ?? 200f);

            nm.DirectorNotes = no["directorNotes"]?.Value<string>() ?? string.Empty;

            if (no["tabFlagOptions"] is JArray tfo)
                foreach (var t in tfo)
                    if (t?.Value<string>() is { } s) nm.TabFlagOptions.Add(s);

            if (no["assignedLabels"] is JArray al)
                foreach (var id in al)
                    nm.AssignedLabelIds.Add(id?.Value<int>() ?? 0);

            if (no["aliasPins"] is JArray ap)
                foreach (var a in ap)
                    if (a is JObject ao)
                    {
                        var pin = new OghamAliasMeta
                        {
                            Name            = ao["name"]?.Value<string>()   ?? string.Empty,
                            TargetEntryTagName = ao["target"]?.Value<string>() ?? string.Empty,
                        };
                        if (ao["position"] is JArray pp && pp.Count == 2)
                            pin.Position = new Vector2(pp[0]?.Value<float>() ?? 0f, pp[1]?.Value<float>() ?? 0f);
                        nm.AliasPins.Add(pin);
                    }

            if (no["edgeWaypoints"] is JArray ew)
                foreach (var e in ew)
                    if (e is JObject ewo)
                    {
                        var wp = new OghamEdgeWaypoints
                        {
                            OptionTagPath = ewo["option"]?.Value<string>() ?? string.Empty,
                        };
                        if (ewo["points"] is JArray pts)
                            foreach (var pt in pts)
                                if (pt is JArray p2 && p2.Count == 2)
                                    wp.Points.Add(new Vector2(p2[0]?.Value<float>() ?? 0f, p2[1]?.Value<float>() ?? 0f));
                        nm.EdgeWaypoints.Add(wp);
                    }

            return nm;
        }

        // ── Build helpers (for SyncFrom) ──────────────────────────────────────

        private static JArray BuildEntriesArray(List<DialogueEntry> entries)
        {
            var arr = new JArray();
            if (entries == null) return arr;
            foreach (var e in entries)
            {
                var eo = new JObject { ["tag"] = e.TagPath };

                if (e.Mode != OghamNodeMode.Content)
                    eo["nodeMode"] = e.Mode.ToString();

                if (e.ContentKeys.Count > 0)
                {
                    var ck = new JArray();
                    foreach (var k in e.ContentKeys)
                    {
                        var ko = new JObject
                        {
                            ["type"] = k.Type.ToString(),
                            ["mode"] = k.Mode.ToString(),
                            ["key"]  = k.KeyOrValue,
                        };
                        if (k.Type != OghamContentType.Text && k.Mode == LexiconLocMode.Literal && k.AssetRef != null)
                        {
                            var guid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(k.AssetRef));
                            if (!string.IsNullOrEmpty(guid))
                            {
                                ko["assetGuid"] = guid;
                                // Sprites are sub-assets of a Texture2D file. Store the sub-asset name
                                // so round-trip loading can find the correct sprite (handles sprite sheets).
                                if (k.Type == OghamContentType.Sprite)
                                    ko["assetName"] = k.AssetRef.name;
                            }
                        }
                        ck.Add(ko);
                    }
                    eo["contentKeys"] = ck;
                }

                if (e.EntryOperations.Count > 0)
                    eo["entryOperations"] = BuildOperationsArray(e.EntryOperations);

                if (e.Options.Count > 0)
                    eo["options"] = BuildOptionsArray(e.Options);

                arr.Add(eo);
            }
            return arr;
        }

        private static JArray BuildOperationsArray(List<GameplayTagOperation> ops)
        {
            var arr = new JArray();
            foreach (var op in ops)
            {
                var oo = new JObject
                {
                    ["tag"]        = op.Tag.Id != 0 ? GameplayTagRegistry.GetName(op.Tag.Id) : string.Empty,
                    ["arithmetic"] = op.Arithmetic.ToString(),
                    ["value"]      = op.Value,
                };
                if (op.ValueTag.Id != 0)
                    oo["valueTag"] = GameplayTagRegistry.GetName(op.ValueTag.Id);
                if (op.ValueType != GameplayTagValueType.Unsigned)
                    oo["valueType"] = op.ValueType.ToString();
                if (op.Conditions.Count > 0)
                    oo["conditions"] = BuildConditionsArray(op.Conditions);
                arr.Add(oo);
            }
            return arr;
        }

        private static JArray BuildConditionsArray(List<GameplayTagCondition> conds)
        {
            var arr = new JArray();
            foreach (var c in conds)
            {
                var co = new JObject
                {
                    ["tag"]          = c.Tag.Id != 0 ? GameplayTagRegistry.GetName(c.Tag.Id) : string.Empty,
                    ["comparison"]   = c.Comparison.ToString(),
                    ["compareValue"] = c.CompareValue,
                    ["exactMatch"]   = c.ExactMatch,
                    ["logicOp"]      = c.LogicOp.ToString(),
                };
                if (c.CompareTag.Id != 0)
                    co["compareTag"] = GameplayTagRegistry.GetName(c.CompareTag.Id);
                arr.Add(co);
            }
            return arr;
        }

        private static JArray BuildOptionsArray(List<DialogueOption> opts)
        {
            var arr = new JArray();
            foreach (var opt in opts)
            {
                var oo = new JObject
                {
                    ["tag"]       = opt.TagPath,
                    ["targetTag"] = opt.TargetEntryPath,
                };

                if (opt.TextKey.Mode == LexiconLocMode.Literal)
                {
                    oo["textKey"] = opt.TextKey.KeyOrValue ?? string.Empty;
                }
                else
                {
                    oo["textKey"] = new JObject
                    {
                        ["mode"] = opt.TextKey.Mode.ToString(),
                        ["key"]  = opt.TextKey.KeyOrValue ?? string.Empty,
                    };
                }

                if (opt.Conditions.Count > 0) oo["conditions"] = BuildConditionsArray(opt.Conditions);
                if (opt.Operations.Count > 0) oo["operations"] = BuildOperationsArray(opt.Operations);
                arr.Add(oo);
            }
            return arr;
        }

        private static JObject BuildEditorBlock(OghamGraphMetadata meta)
        {
            var editor = new JObject
            {
                ["viewTransform"] = new JArray
                {
                    meta.ViewTransform.x, meta.ViewTransform.y, meta.ViewTransform.z
                },
            };

            if (meta.HeaderColor.a > 0f)
                editor["headerColor"] = ToHex(meta.HeaderColor);

            if (meta.Labels.Count > 0)
            {
                var labels = new JArray();
                foreach (var l in meta.Labels)
                    labels.Add(new JObject
                    {
                        ["id"]    = l.Id,
                        ["name"]  = l.Name,
                        ["color"] = ToHex(l.Color),
                    });
                editor["labels"] = labels;
            }

            var nodes = new JArray();
            foreach (var n in meta.Nodes)
            {
                var no = new JObject
                {
                    ["tag"]            = n.TagName,
                    ["position"]       = new JArray { n.Position.x, n.Position.y, n.Position.width, n.Position.height },
                    ["label"]          = n.LabelText,
                    ["labelColor"]     = ToHex(n.LabelColor),
                    ["collapsed"]      = n.IsCollapsed,
                    ["opsExpanded"]    = n.OpsExpanded,
                    ["fieldsExpanded"] = n.FieldsExpanded,
                    ["choicesExpanded"]= n.ChoicesExpanded,
                    ["highlightColor"] = ToHex(n.HighlightColor),
                };

                if (!string.IsNullOrEmpty(n.DirectorNotes))
                    no["directorNotes"] = n.DirectorNotes;

                if (n.TabFlagOptions.Count > 0)
                {
                    var tfo = new JArray();
                    foreach (var t in n.TabFlagOptions) tfo.Add(t);
                    no["tabFlagOptions"] = tfo;
                }

                if (n.AssignedLabelIds.Count > 0)
                {
                    var al = new JArray();
                    foreach (var id in n.AssignedLabelIds) al.Add(id);
                    no["assignedLabels"] = al;
                }

                if (n.AliasPins.Count > 0)
                {
                    var ap = new JArray();
                    foreach (var pin in n.AliasPins)
                        ap.Add(new JObject
                        {
                            ["name"]     = pin.Name,
                            ["target"]   = pin.TargetEntryTagName,
                            ["position"] = new JArray { pin.Position.x, pin.Position.y },
                        });
                    no["aliasPins"] = ap;
                }

                if (n.EdgeWaypoints.Count > 0)
                {
                    var ew = new JArray();
                    foreach (var wp in n.EdgeWaypoints)
                    {
                        var pts = new JArray();
                        foreach (var pt in wp.Points) pts.Add(new JArray { pt.x, pt.y });
                        ew.Add(new JObject { ["option"] = wp.OptionTagPath, ["points"] = pts });
                    }
                    no["edgeWaypoints"] = ew;
                }

                nodes.Add(no);
            }
            editor["nodes"] = nodes;
            return editor;
        }

        // ── Utility ───────────────────────────────────────────────────────────

        private static GameplayTag HashTag(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return default;
            return GameplayTag.FromName(path.Trim());
        }

        private static Color ParseColor(string hex, Color fallback)
        {
            if (string.IsNullOrWhiteSpace(hex)) return fallback;
            return ColorUtility.TryParseHtmlString(hex, out var c) ? c : fallback;
        }

        private static string ToHex(Color c) => $"#{ColorUtility.ToHtmlStringRGBA(c)}";
    }
}
