using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using UnityEditor;
using UnityEngine;
using Heathen.GameplayTags;
using Heathen.Lexicon;

namespace Heathen.Ogham.Editor
{
    // Bidirectional adapter between .ogham JSON source files and Unity runtime objects.
    //
    // Reading paths:
    //   ToOghamData()     — authoring format (Markdown inline links preserved, for graph editor)
    //   ToMetadata()      — OghamGraphMetadata from _editor block
    //   ToCompiledData()  — runtime format (inline links → TMPro, pure links dropped, for OghamImporter)
    //
    // Writing path:
    //   SyncFrom(data, meta) — update entries[] and _editor from in-memory objects
    //   ToJson()             — serialize back to .ogham JSON
    //
    // .ogham format (superset of O3DE's .ogmcon):
    // {
    //   "storyTag": "Stories.MainQuest",
    //   "entries": [
    //     {
    //       "tag": "Stories.MainQuest.Intro",
    //       "parentTag": "Stories.MainQuest",          // optional
    //       "contentKeys": [                            // OR legacy "dataKeys"/"textKeys"
    //         {"type": "Text", "mode": "Localised", "key": "Dialogue.Intro"},
    //         {"type": "Text", "mode": "Literal",   "key": "Hello [world](Ogham://Option.Tag)!"}
    //       ],
    //       "entryOperations": [...],
    //       "options": [
    //         {
    //           "tag":        "Stories.MainQuest.Intro.Accept",
    //           "targetTag":  "Stories.MainQuest.Accept",  // empty = close conversation
    //           "textKey":    "Accept",                     // string = Literal; obj = {mode, key}
    //           "textMode":   "Literal",                   // optional with string textKey
    //           "conditions": [...],
    //           "operations": [...]
    //         }
    //       ]
    //     }
    //   ],
    //   "localisations": [{"culture":"en","key":"Dialogue.Intro","value":"Hello!"}],
    //   "assets": [{"lexiconKey":"Art.Portrait","source":"Assets/Sprites/NPC.png","culture":""}],
    //   "_editor": {
    //     "viewTransform": [0,0,1],
    //     "labels": [{"id":1,"name":"Important","color":"#FF0000"}],
    //     "nodes": [{"tag":"...","position":[x,y,w,h],"label":"","labelColor":"#FFFFFF",...}]
    //   }
    // }
    internal class OghamJsonDocument
    {
        private JsonObject _root;

        private OghamJsonDocument(JsonObject root) => _root = root;

        // ── Factory ───────────────────────────────────────────────────────────

        public static OghamJsonDocument Parse(string json)
        {
            try
            {
                var root = JsonNode.Parse(json) as JsonObject ?? new JsonObject();
                return new OghamJsonDocument(root);
            }
            catch
            {
                return new OghamJsonDocument(new JsonObject());
            }
        }

        public static OghamJsonDocument CreateNew(string storyTag = "")
        {
            var root = new JsonObject
            {
                ["storyTag"] = storyTag,
                ["entries"]  = new JsonArray(),
            };
            return new OghamJsonDocument(root);
        }

        // ── Properties ────────────────────────────────────────────────────────

        public string StoryTag => _root["storyTag"]?.GetValue<string>() ?? string.Empty;

        // ── Read → OghamData (authoring format) ───────────────────────────────

        public OghamData ToOghamData()
        {
            var data = ScriptableObject.CreateInstance<OghamData>();
            if (_root["entries"] is not JsonArray entries) return data;

            foreach (var entryNode in entries)
            {
                if (entryNode is not JsonObject eo) continue;
                var entry = new DialogueEntry
                {
                    TagPath = eo["tag"]?.GetValue<string>() ?? string.Empty,
                };
                ParseContentKeys(eo, entry.ContentKeys);
                ParseOperations(eo["entryOperations"] as JsonArray, entry.EntryOperations);
                ParseOptions(eo["options"] as JsonArray, entry.Options);
                data.Entries.Add(entry);
            }
            return data;
        }

        // ── Read → OghamGraphMetadata ─────────────────────────────────────────

        public OghamGraphMetadata ToMetadata()
        {
            var meta = ScriptableObject.CreateInstance<OghamGraphMetadata>();
            if (_root["_editor"] is not JsonObject editor) return meta;

            if (editor["viewTransform"] is JsonArray vt && vt.Count == 3)
                meta.ViewTransform = new Vector3(
                    vt[0]?.GetValue<float>() ?? 0f,
                    vt[1]?.GetValue<float>() ?? 0f,
                    vt[2]?.GetValue<float>() ?? 1f);

            if (editor["labels"] is JsonArray labels)
                foreach (var lbl in labels)
                    if (lbl is JsonObject lo)
                        meta.Labels.Add(new OghamLabelDef
                        {
                            Id    = lo["id"]?.GetValue<int>()    ?? 0,
                            Name  = lo["name"]?.GetValue<string>() ?? string.Empty,
                            Color = ParseColor(lo["color"]?.GetValue<string>(), Color.white),
                        });

            if (editor["nodes"] is JsonArray nodes)
                foreach (var n in nodes)
                    if (n is JsonObject no)
                        meta.Nodes.Add(ParseNodeMeta(no));

            return meta;
        }

        // ── Read → OghamCompiledData (runtime format, TMPro markup) ───────────

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

        // ── Write from in-memory state ────────────────────────────────────────

        // Syncs entries[] and _editor from live objects. Preserves storyTag/localisations/assets.
        public void SyncFrom(OghamData data, OghamGraphMetadata meta)
        {
            if (data != null) _root["entries"] = BuildEntriesArray(data.Entries);
            if (meta != null) _root["_editor"] = BuildEditorBlock(meta);
        }

        public void SetStoryTag(string storyTag) => _root["storyTag"] = storyTag;

        // ── Serialize ─────────────────────────────────────────────────────────

        public string ToJson()
        {
            var opts = new JsonSerializerOptions { WriteIndented = true };
            return _root.ToJsonString(opts);
        }

        // ── Parse helpers ─────────────────────────────────────────────────────

        private static void ParseContentKeys(JsonObject eo, List<OghamContentKey> keys)
        {
            // New format: contentKeys[]
            if (eo["contentKeys"] is JsonArray ck)
            {
                foreach (var k in ck)
                {
                    if (k is not JsonObject ko) continue;
                    Enum.TryParse<OghamContentType>(ko["type"]?.GetValue<string>() ?? "Text", true, out var type);
                    Enum.TryParse<LexiconLocMode>(  ko["mode"]?.GetValue<string>() ?? "Literal", true, out var mode);
                    keys.Add(new OghamContentKey
                    {
                        Type       = type,
                        Mode       = mode,
                        KeyOrValue = ko["key"]?.GetValue<string>() ?? string.Empty,
                    });
                }
                return;
            }

            // Legacy O3DE format: dataKeys[] or textKeys[] → Text + Localised
            var legacyArr = eo["dataKeys"] as JsonArray ?? eo["textKeys"] as JsonArray;
            if (legacyArr != null)
                foreach (var k in legacyArr)
                    if (k?.GetValue<string>() is { } s)
                        keys.Add(new OghamContentKey { Type = OghamContentType.Text, Mode = LexiconLocMode.Localised, KeyOrValue = s });
        }

        private static void ParseOperations(JsonArray arr, List<GameplayTagOperation> ops)
        {
            if (arr == null) return;
            foreach (var o in arr)
            {
                if (o is not JsonObject oo) continue;
                Enum.TryParse<GameplayTagArithmetic>(oo["arithmetic"]?.GetValue<string>() ?? "Set", true, out var arith);
                // Accept O3DE short forms: Sub→Subtract, Mul→Multiply, Div→Divide
                if (arith == GameplayTagArithmetic.Set && oo["arithmetic"]?.GetValue<string>() is { } raw)
                    arith = raw switch { "Sub" => GameplayTagArithmetic.Subtract, "Mul" => GameplayTagArithmetic.Multiply, "Div" => GameplayTagArithmetic.Divide, _ => arith };

                var op = new GameplayTagOperation
                {
                    Tag        = HashTag(oo["tag"]?.GetValue<string>()),
                    Arithmetic = arith,
                    Value      = oo["value"]?.GetValue<ulong>() ?? 0UL,
                };
                ParseConditions(oo["conditions"] as JsonArray, op.Conditions);
                ops.Add(op);
            }
        }

        private static void ParseConditions(JsonArray arr, List<GameplayTagCondition> conds)
        {
            if (arr == null) return;
            foreach (var c in arr)
            {
                if (c is not JsonObject co) continue;
                Enum.TryParse<GameplayTagComparisonOp>(co["comparison"]?.GetValue<string>() ?? "Exists", true, out var comp);
                Enum.TryParse<GameplayTagLogicOp>(     co["logicOp"]?.GetValue<string>()    ?? "And",    true, out var logic);
                var cond = new GameplayTagCondition
                {
                    Tag          = HashTag(co["tag"]?.GetValue<string>()),
                    Comparison   = comp,
                    CompareValue = co["compareValue"]?.GetValue<ulong>() ?? 0UL,
                    ExactMatch   = co["exactMatch"]?.GetValue<bool>() ?? true,
                    LogicOp      = logic,
                };
                var compareTagStr = co["compareTag"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(compareTagStr))
                    cond.CompareTag = HashTag(compareTagStr);
                conds.Add(cond);
            }
        }

        private static void ParseOptions(JsonArray arr, List<DialogueOption> opts)
        {
            if (arr == null) return;
            foreach (var o in arr)
            {
                if (o is not JsonObject oo) continue;
                var opt = new DialogueOption
                {
                    TagPath         = oo["tag"]?.GetValue<string>()        ?? string.Empty,
                    TargetEntryPath = (oo["targetTag"] ?? oo["targetEntry"])?.GetValue<string>() ?? string.Empty,
                };

                // textKey: string or {"mode":..., "key":...}
                if (oo["textKey"] is JsonValue tv && tv.TryGetValue<string>(out var tvStr))
                {
                    Enum.TryParse<LexiconLocMode>(oo["textMode"]?.GetValue<string>() ?? "Literal", true, out var tMode);
                    opt.TextKey = new LexiconText { Mode = tMode, KeyOrValue = tvStr };
                }
                else if (oo["textKey"] is JsonObject tko)
                {
                    Enum.TryParse<LexiconLocMode>(tko["mode"]?.GetValue<string>() ?? "Literal", true, out var tMode);
                    opt.TextKey = new LexiconText { Mode = tMode, KeyOrValue = tko["key"]?.GetValue<string>() ?? string.Empty };
                }

                ParseConditions(oo["conditions"] as JsonArray, opt.Conditions);
                ParseOperations(oo["operations"] as JsonArray, opt.Operations);
                opts.Add(opt);
            }
        }

        private OghamCompiledLocale[] ParseLocalisations()
        {
            if (_root["localisations"] is not JsonArray arr) return Array.Empty<OghamCompiledLocale>();
            var result = new List<OghamCompiledLocale>(arr.Count);
            foreach (var l in arr)
            {
                if (l is not JsonObject lo) continue;
                result.Add(new OghamCompiledLocale
                {
                    Culture = lo["culture"]?.GetValue<string>() ?? string.Empty,
                    Key     = lo["key"]?.GetValue<string>()     ?? string.Empty,
                    Value   = lo["value"]?.GetValue<string>()   ?? string.Empty,
                });
            }
            return result.ToArray();
        }

        // ── Tag and localisation accessors (for OghamImporter / InitializeOnLoad) ─

        // All dot-path tag strings referenced anywhere in this document.
        public IEnumerable<string> GetAllTagPaths()
        {
            var paths = new HashSet<string>(StringComparer.Ordinal);
            var st = StoryTag;
            if (!string.IsNullOrWhiteSpace(st)) paths.Add(st.Trim());
            if (_root["entries"] is JsonArray entries)
                foreach (var e in entries)
                    if (e is JsonObject eo) CollectEntryTagPaths(eo, paths);
            return paths;
        }

        public OghamCompiledLocale[] GetLocalisations() => ParseLocalisations();

        private static void CollectEntryTagPaths(JsonObject eo, HashSet<string> paths)
        {
            AddTagPath(paths, eo["tag"]?.GetValue<string>());
            CollectOperationTagPaths(eo["entryOperations"] as JsonArray, paths);
            if (eo["options"] is JsonArray opts)
                foreach (var o in opts)
                    if (o is JsonObject oo)
                    {
                        AddTagPath(paths, oo["tag"]?.GetValue<string>());
                        AddTagPath(paths, (oo["targetTag"] ?? oo["targetEntry"])?.GetValue<string>());
                        CollectOperationTagPaths(oo["operations"] as JsonArray, paths);
                        CollectConditionTagPaths(oo["conditions"] as JsonArray, paths);
                    }
        }

        private static void CollectOperationTagPaths(JsonArray arr, HashSet<string> paths)
        {
            if (arr == null) return;
            foreach (var o in arr)
                if (o is JsonObject oo)
                {
                    AddTagPath(paths, oo["tag"]?.GetValue<string>());
                    CollectConditionTagPaths(oo["conditions"] as JsonArray, paths);
                }
        }

        private static void CollectConditionTagPaths(JsonArray arr, HashSet<string> paths)
        {
            if (arr == null) return;
            foreach (var c in arr)
                if (c is JsonObject co)
                {
                    AddTagPath(paths, co["tag"]?.GetValue<string>());
                    AddTagPath(paths, co["compareTag"]?.GetValue<string>());
                }
        }

        private static void AddTagPath(HashSet<string> paths, string p)
        {
            if (!string.IsNullOrWhiteSpace(p)) paths.Add(p.Trim());
        }

        private static OghamNodeMeta ParseNodeMeta(JsonObject no)
        {
            var nm = new OghamNodeMeta
            {
                TagName        = no["tag"]?.GetValue<string>()          ?? string.Empty,
                LabelText      = no["label"]?.GetValue<string>()        ?? string.Empty,
                LabelColor     = ParseColor(no["labelColor"]?.GetValue<string>(), Color.white),
                IsCollapsed    = no["collapsed"]?.GetValue<bool>()      ?? false,
                OpsExpanded    = no["opsExpanded"]?.GetValue<bool>()    ?? false,
                FieldsExpanded = no["fieldsExpanded"]?.GetValue<bool>() ?? true,
                ChoicesExpanded= no["choicesExpanded"]?.GetValue<bool>()  ?? true,
                HighlightColor = ParseColor(no["highlightColor"]?.GetValue<string>(), Color.clear),
            };

            if (no["position"] is JsonArray pos && pos.Count == 4)
                nm.Position = new Rect(pos[0]?.GetValue<float>() ?? 0f, pos[1]?.GetValue<float>() ?? 0f,
                                       pos[2]?.GetValue<float>() ?? 300f, pos[3]?.GetValue<float>() ?? 200f);

            if (no["tabFlagOptions"] is JsonArray tfo)
                foreach (var t in tfo)
                    if (t?.GetValue<string>() is { } s) nm.TabFlagOptions.Add(s);

            if (no["assignedLabels"] is JsonArray al)
                foreach (var id in al)
                    nm.AssignedLabelIds.Add(id?.GetValue<int>() ?? 0);

            if (no["aliasPins"] is JsonArray ap)
                foreach (var a in ap)
                    if (a is JsonObject ao)
                    {
                        var pin = new OghamAliasMeta
                        {
                            Name            = ao["name"]?.GetValue<string>()   ?? string.Empty,
                            TargetEntryTagName = ao["target"]?.GetValue<string>() ?? string.Empty,
                        };
                        if (ao["position"] is JsonArray pp && pp.Count == 2)
                            pin.Position = new Vector2(pp[0]?.GetValue<float>() ?? 0f, pp[1]?.GetValue<float>() ?? 0f);
                        nm.AliasPins.Add(pin);
                    }

            if (no["edgeWaypoints"] is JsonArray ew)
                foreach (var e in ew)
                    if (e is JsonObject ewo)
                    {
                        var wp = new OghamEdgeWaypoints
                        {
                            OptionTagPath = ewo["option"]?.GetValue<string>() ?? string.Empty,
                        };
                        if (ewo["points"] is JsonArray pts)
                            foreach (var pt in pts)
                                if (pt is JsonArray p2 && p2.Count == 2)
                                    wp.Points.Add(new Vector2(p2[0]?.GetValue<float>() ?? 0f, p2[1]?.GetValue<float>() ?? 0f));
                        nm.EdgeWaypoints.Add(wp);
                    }

            return nm;
        }

        // ── Build helpers (for SyncFrom) ──────────────────────────────────────

        private static JsonArray BuildEntriesArray(List<DialogueEntry> entries)
        {
            var arr = new JsonArray();
            if (entries == null) return arr;
            foreach (var e in entries)
            {
                var eo = new JsonObject { ["tag"] = e.TagPath };

                if (e.ContentKeys.Count > 0)
                {
                    var ck = new JsonArray();
                    foreach (var k in e.ContentKeys)
                    {
                        var ko = new JsonObject
                        {
                            ["type"] = k.Type.ToString(),
                            ["mode"] = k.Mode.ToString(),
                            ["key"]  = k.KeyOrValue,
                        };
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

        private static JsonArray BuildOperationsArray(List<GameplayTagOperation> ops)
        {
            var arr = new JsonArray();
            foreach (var op in ops)
            {
                var oo = new JsonObject
                {
                    ["tag"]        = op.Tag.Id != 0 ? GameplayTagRegistry.GetName(op.Tag.Id) : string.Empty,
                    ["arithmetic"] = op.Arithmetic.ToString(),
                    ["value"]      = op.Value,
                };
                if (op.Conditions.Count > 0)
                    oo["conditions"] = BuildConditionsArray(op.Conditions);
                arr.Add(oo);
            }
            return arr;
        }

        private static JsonArray BuildConditionsArray(List<GameplayTagCondition> conds)
        {
            var arr = new JsonArray();
            foreach (var c in conds)
            {
                var co = new JsonObject
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

        private static JsonArray BuildOptionsArray(List<DialogueOption> opts)
        {
            var arr = new JsonArray();
            foreach (var opt in opts)
            {
                var oo = new JsonObject
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
                    oo["textKey"] = new JsonObject
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

        private static JsonObject BuildEditorBlock(OghamGraphMetadata meta)
        {
            var editor = new JsonObject
            {
                ["viewTransform"] = new JsonArray
                {
                    meta.ViewTransform.x, meta.ViewTransform.y, meta.ViewTransform.z
                },
            };

            if (meta.Labels.Count > 0)
            {
                var labels = new JsonArray();
                foreach (var l in meta.Labels)
                    labels.Add(new JsonObject
                    {
                        ["id"]    = l.Id,
                        ["name"]  = l.Name,
                        ["color"] = ToHex(l.Color),
                    });
                editor["labels"] = labels;
            }

            var nodes = new JsonArray();
            foreach (var n in meta.Nodes)
            {
                var no = new JsonObject
                {
                    ["tag"]            = n.TagName,
                    ["position"]       = new JsonArray { n.Position.x, n.Position.y, n.Position.width, n.Position.height },
                    ["label"]          = n.LabelText,
                    ["labelColor"]     = ToHex(n.LabelColor),
                    ["collapsed"]      = n.IsCollapsed,
                    ["opsExpanded"]    = n.OpsExpanded,
                    ["fieldsExpanded"] = n.FieldsExpanded,
                    ["choicesExpanded"]= n.ChoicesExpanded,
                    ["highlightColor"] = ToHex(n.HighlightColor),
                };

                if (n.TabFlagOptions.Count > 0)
                {
                    var tfo = new JsonArray();
                    foreach (var t in n.TabFlagOptions) tfo.Add(t);
                    no["tabFlagOptions"] = tfo;
                }

                if (n.AssignedLabelIds.Count > 0)
                {
                    var al = new JsonArray();
                    foreach (var id in n.AssignedLabelIds) al.Add(id);
                    no["assignedLabels"] = al;
                }

                if (n.AliasPins.Count > 0)
                {
                    var ap = new JsonArray();
                    foreach (var pin in n.AliasPins)
                        ap.Add(new JsonObject
                        {
                            ["name"]     = pin.Name,
                            ["target"]   = pin.TargetEntryTagName,
                            ["position"] = new JsonArray { pin.Position.x, pin.Position.y },
                        });
                    no["aliasPins"] = ap;
                }

                if (n.EdgeWaypoints.Count > 0)
                {
                    var ew = new JsonArray();
                    foreach (var wp in n.EdgeWaypoints)
                    {
                        var pts = new JsonArray();
                        foreach (var pt in wp.Points) pts.Add(new JsonArray { pt.x, pt.y });
                        ew.Add(new JsonObject { ["option"] = wp.OptionTagPath, ["points"] = pts });
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
