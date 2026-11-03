using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace DSH.Foresee
{
    /// <summary>
    /// Reads the newest Darkest Dungeon II save (run → stage → combat) and renders it as
    /// text / JSON. Pure System.IO + Newtonsoft.Json, no Unity dependency, so it can be
    /// exercised by an offline harness.
    ///
    /// Save layout (LocalLow\RedHook\Darkest Dungeon II\SaveFiles):
    ///   &lt;steamid&gt;\profiles\&lt;profile&gt;_runs\&lt;yyMMdd_HHmmss&gt;\Save_000N_&lt;Stage&gt;\Save_000M\
    ///       run.json, map.json, actors.json, campaign.json
    ///       combat\combat.json, combat\combat_scenario_data.json
    /// </summary>
    internal static class SaveReader
    {
        internal sealed class ActorRow
        {
            public uint Guid;
            public string Name;
            public string ClassId;
            public double Hp;
            public double Stress;
            public int Position;
            public List<string> Tokens = new List<string>();
            public int Buffs;
            public List<DotEntry> Dots = new List<DotEntry>();
            public bool InBattle;
            public string TurnType;
        }

        /// <summary>One DOT instance: damage per tick, remaining turns and flavour (bleed/blight/burn...).</summary>
        internal sealed class DotEntry
        {
            public string Id;
            public string Type;
            public double Damage;
            public int Duration;
        }

        internal sealed class TeamRow
        {
            public int TeamIndex;
            public int TeamSize;
            public int Summons;
            public string Controller;
            public List<ActorRow> Actors = new List<ActorRow>();
        }

        internal sealed class Snapshot
        {
            public string DataDir;
            public string StageName;
            public string RunName;
            public DateTime Written;

            public string RunStart;
            public int NodeCount;
            public string Biome;
            public string LastNode;
            public int CombatCount;
            public string CombatConfig;
            public int RoundCount;
            public long MapSeed;

            public bool InCombat;
            public int Round;
            public int Turn;
            public int TotalTurns;
            public readonly List<string> Order = new List<string>();
            public string LoadedFrom;
            public string SceneName;
            public bool IsExpeditionBoss;

            public readonly List<TeamRow> Teams = new List<TeamRow>();

            // Live data pulled from the running game (QueryTurnOrder), better than the save
            // snapshot: the game only writes the combat save when it enters combat.
            public bool HasLiveCombat;
            public int LiveRound = -1;
            public int LiveTurn = -1;
            public int LiveTotalTurns = -1;
            public readonly List<string> LiveOrder = new List<string>();
            public readonly List<uint> LiveOrderGuids = new List<uint>();

            /// <summary>Cheap identity of this snapshot for change detection.</summary>
            public string Stamp()
            {
                return Written.Ticks.ToString(CultureInfo.InvariantCulture) + "|" + DataDir;
            }
        }

        // ---------------------------------------------------------------- entry

        public static string SaveRootFromPersistentData(string persistentDataPath)
        {
            return Path.Combine(persistentDataPath, "SaveFiles");
        }

        // ------------------------------------------------------------- catalogs

        // DOT metadata comes from the game's CSV data (id -> type / per-tick damage).
        private static readonly Dictionary<string, string> DotTypeById = new Dictionary<string, string>(StringComparer.Ordinal);
        private static readonly Dictionary<string, int> DotDurationById = new Dictionary<string, int>(StringComparer.Ordinal);
        private static readonly Dictionary<string, List<string>> DotEffectsById = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        private static readonly Dictionary<string, double> EffectDamageById = new Dictionary<string, double>(StringComparer.Ordinal);
        private static readonly Dictionary<string, double> EffectHealById = new Dictionary<string, double>(StringComparer.Ordinal);

        // Token id -> localized display name (zh_CN.po / tw_CN.po, else the English source).
        private static readonly Dictionary<string, string> TokenNameById = new Dictionary<string, string>(StringComparer.Ordinal);
        private static readonly Dictionary<string, string> SkillNameById = new Dictionary<string, string>(StringComparer.Ordinal);
        private static readonly Dictionary<string, bool> VisibleBuffById = new Dictionary<string, bool>(StringComparer.Ordinal);

        private static string _catalogExcelDir;
        private static bool _catalogLoaded;

        /// <summary>Points the DOT/token catalogs at "&lt;game&gt;_Data\StreamingAssets\Excel".</summary>
        public static void ConfigureCatalogs(string excelDir)
        {
            if (!string.Equals(excelDir, _catalogExcelDir, StringComparison.OrdinalIgnoreCase))
            {
                _catalogExcelDir = excelDir;
                _catalogLoaded = false;
            }
        }

        private static void EnsureCatalogs()
        {
            if (_catalogLoaded)
            {
                return;
            }

            _catalogLoaded = true;

            if (string.IsNullOrEmpty(_catalogExcelDir) || !Directory.Exists(_catalogExcelDir))
            {
                return;
            }

            try
            {
                LoadTokenNames(Path.GetDirectoryName(_catalogExcelDir));
            }
            catch
            {
                // localization is optional; the reader falls back to raw ids
            }

            try
            {
                foreach (CsvElement element in ReadCsvElements(Path.Combine(_catalogExcelDir, "buff_data_export.Group.csv")))
                {
                    if (!string.Equals(element.Type, "Buff", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    bool visible = false;

                    foreach (string[] row in element.Rows)
                    {
                        if (row.Length >= 2 && row[0] == "m_Tags")
                        {
                            for (int i = 1; i < row.Length; i++)
                            {
                                string tag = row[i].Trim();
                                if (tag == "buff" || tag == "debuff")
                                {
                                    visible = true;
                                    break;
                                }
                            }
                        }
                    }

                    VisibleBuffById[element.Id] = visible;
                }
            }
            catch
            {
                // buff tags are optional; unknown buffs stay visible
            }

            try
            {
                foreach (CsvElement element in ReadCsvElements(Path.Combine(_catalogExcelDir, "dots_data_export.Group.csv")))
                {
                    if (!string.Equals(element.Type, "Dot", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var effects = new List<string>();
                    foreach (string[] row in element.Rows)
                    {
                        if (row.Length >= 2 && row[0] == "m_Type")
                        {
                            DotTypeById[element.Id] = row[1];
                        }
                        else if (row.Length >= 2 && row[0] == "m_DurationAmount")
                        {
                            int duration;
                            if (int.TryParse(row[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out duration))
                            {
                                DotDurationById[element.Id] = duration;
                            }
                        }
                        else if (row.Length >= 2 && row[0] == "effects")
                        {
                            for (int i = 1; i < row.Length; i++)
                            {
                                if (row[i].Length > 0)
                                {
                                    effects.Add(row[i]);
                                }
                            }
                        }
                    }

                    DotEffectsById[element.Id] = effects;
                }

                foreach (CsvElement element in ReadCsvElements(Path.Combine(_catalogExcelDir, "effect_data_export.Group.csv")))
                {
                    if (!string.Equals(element.Type, "Effect", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    foreach (string[] row in element.Rows)
                    {
                        if (row.Length < 2)
                        {
                            continue;
                        }

                        double value;
                        if (row[0] == "m_HealthDamageAmount" && double.TryParse(row[1], NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                        {
                            EffectDamageById[element.Id] = value;
                        }
                        else if (row[0] == "m_HealthHealAmount" && double.TryParse(row[1], NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                        {
                            EffectHealById[element.Id] = value;
                        }
                    }
                }
            }
            catch
            {
                // keep whatever was parsed; the reader must never break on data files
            }
        }

        private sealed class CsvElement
        {
            public string Id;
            public string Type;
            public readonly List<string[]> Rows = new List<string[]>();
        }

        private static List<CsvElement> ReadCsvElements(string path)
        {
            var result = new List<CsvElement>();

            if (!File.Exists(path))
            {
                return result;
            }

            CsvElement current = null;

            foreach (string rawLine in File.ReadAllLines(path, System.Text.Encoding.UTF8))
            {
                string line = rawLine.Trim();
                if (line.Length == 0)
                {
                    continue;
                }

                string[] cells = line.Split(',');

                if (cells[0] == "element_start")
                {
                    if (current != null)
                    {
                        result.Add(current);
                    }

                    current = new CsvElement
                    {
                        Id = cells.Length > 1 ? cells[1] : string.Empty,
                        Type = cells.Length > 2 ? cells[2] : string.Empty
                    };
                    continue;
                }

                if (cells[0] == "element_end")
                {
                    if (current != null)
                    {
                        result.Add(current);
                        current = null;
                    }
                    continue;
                }

                if (current != null)
                {
                    current.Rows.Add(cells);
                }
            }

            if (current != null)
            {
                result.Add(current);
            }

            return result;
        }

        // ------------------------------------------------------------ token names

        private static void LoadTokenNames(string streamingAssets)
        {
            if (string.IsNullOrEmpty(streamingAssets) || !Directory.Exists(streamingAssets))
            {
                return;
            }

            string poDir = Path.Combine(streamingAssets, "Localization", "Poedit");
            foreach (string language in new[] { "zh_CN.po", "tw_CN.po" })
            {
                string poPath = Path.Combine(poDir, language);
                if (File.Exists(poPath) && ParsePoTokenNames(poPath))
                {
                    return;
                }
            }

            string sourcePath = Path.Combine(streamingAssets, "Localization", "Sources", "tokens.txt");
            if (File.Exists(sourcePath))
            {
                ParseTokenSourceNames(sourcePath);
            }
        }

        /// <summary>Reads msgctxt "token_name_x" / msgstr pairs out of a .po file.</summary>
        private static bool ParsePoTokenNames(string path)
        {
            try
            {
                string pendingKey = null;

                foreach (string raw in File.ReadLines(path, System.Text.Encoding.UTF8))
                {
                    string line = raw.Trim();

                    if (line.Length == 0)
                    {
                        pendingKey = null;
                        continue;
                    }

                    if (line[0] == '#')
                    {
                        continue; // translator comments / previous context
                    }

                    if (line.StartsWith("msgctxt \"", StringComparison.Ordinal))
                    {
                        pendingKey = Unquote(line, 9);
                        continue;
                    }

                    if (line.StartsWith("msgstr \"", StringComparison.Ordinal) && pendingKey != null)
                    {
                        if (pendingKey.StartsWith("token_name_", StringComparison.Ordinal))
                        {
                            string id = pendingKey.Substring("token_name_".Length);
                            string name = CleanLocalized(Unquote(line, 8));
                            if (id.Length > 0 && name.Length > 0)
                            {
                                TokenNameById[id] = name;
                            }
                        }
                        else if (pendingKey.StartsWith("skill_name_", StringComparison.Ordinal))
                        {
                            string id = pendingKey.Substring("skill_name_".Length);
                            string name = CleanLocalized(Unquote(line, 8));
                            if (id.Length > 0 && name.Length > 0)
                            {
                                SkillNameById[id] = name;
                            }
                        }

                        pendingKey = null;
                    }
                }

                return TokenNameById.Count > 0;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>English fallback: "token_name_block=&lt;color=...&gt;Block&lt;/color&gt;".</summary>
        private static void ParseTokenSourceNames(string path)
        {
            try
            {
                foreach (string raw in File.ReadLines(path, System.Text.Encoding.UTF8))
                {
                    string line = raw.Trim();
                    if (!line.StartsWith("token_name_", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    int eq = line.IndexOf('=');
                    if (eq <= 0)
                    {
                        continue;
                    }

                    string id = line.Substring("token_name_".Length, eq - "token_name_".Length);
                    string name = CleanLocalized(line.Substring(eq + 1));
                    if (id.Length > 0 && name.Length > 0)
                    {
                        TokenNameById[id] = name;
                    }
                }
            }
            catch
            {
                // ignore
            }
        }

        private static string Unquote(string line, int prefixLength)
        {
            string value = line.Substring(prefixLength);

            if (value.EndsWith("\"", StringComparison.Ordinal))
            {
                value = value.Substring(0, value.Length - 1);
            }

            return value.Replace("\\\"", "\"").Replace("\\n", " ");
        }

        /// <summary>Strips Unity rich-text tags: "&lt;color=#{buff}&gt;格挡&lt;/color&gt;" -> "格挡".</summary>
        private static string CleanLocalized(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            var sb = new System.Text.StringBuilder(value.Length);
            bool insideTag = false;

            foreach (char c in value)
            {
                if (c == '<')
                {
                    insideTag = true;
                }
                else if (c == '>')
                {
                    insideTag = false;
                }
                else if (!insideTag)
                {
                    sb.Append(c);
                }
            }

            return sb.ToString().Trim();
        }

        /// <summary>"毒(2*3)" / "毒(2*3)×2" for an effect that applies a DOT.</summary>
        public static string DescribeDotApplication(string dotId, int amount)
        {
            if (string.IsNullOrEmpty(dotId))
            {
                return null;
            }

            string type;
            if (!DotTypeById.TryGetValue(dotId, out type))
            {
                type = string.Empty;
            }

            double damage = 0d;
            List<string> effects;
            if (DotEffectsById.TryGetValue(dotId, out effects))
            {
                foreach (string effectId in effects)
                {
                    double value;
                    if (EffectDamageById.TryGetValue(effectId, out value))
                    {
                        damage += value;
                    }
                }
            }

            int duration;
            if (!DotDurationById.TryGetValue(dotId, out duration))
            {
                duration = 0;
            }

            string text = DotTypeLabel(type) + "(" + FormatNumber(damage) + "*" + duration + ")";
            return amount > 1 ? text + "×" + amount : text;
        }

        public static string DescribeTokenApplication(string tokenId)
        {
            return string.IsNullOrEmpty(tokenId) ? null : TokenDisplayName(tokenId);
        }

        /// <summary>Localized display name for a token id (Chinese when zh_CN.po is present).</summary>
        public static string SkillName(string skillId)
        {
            if (string.IsNullOrEmpty(skillId))
            {
                return null;
            }

            string name;
            return SkillNameById.TryGetValue(skillId, out name) && !string.IsNullOrEmpty(name) ? name : null;
        }

        private static readonly Dictionary<string, string> ClassNameById = new Dictionary<string, string>(StringComparer.Ordinal);
        private static readonly HashSet<string> ClassNameMisses = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>Class ids that had no .po entry (useful for diagnostics / modded actors).</summary>
        public static readonly HashSet<string> UnknownClassIds = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>Localized actor CLASS name from the .po ("man_at_arms" -> "老兵"); falls back to the id.</summary>
        public static string ClassName(string classId)
        {
            if (string.IsNullOrEmpty(classId))
            {
                return classId;
            }

            string first = classId;
            int slash = first.IndexOf('/');
            if (slash > 0)
            {
                first = first.Substring(0, slash);
            }

            // resolve lazily so callers that pass an id we have not seen yet still get a name
            var probe = new List<string> { first };
            if (first.EndsWith("_corpse", StringComparison.OrdinalIgnoreCase))
            {
                probe.Add(first.Substring(0, first.Length - "_corpse".Length));
            }

            try
            {
                EnsureClassNames(probe);
            }
            catch
            {
                // naming is optional
            }

            string name;

            // "<base>_corpse" -> "<base name>的尸体" (e.g. shared_lost_soul_chirurgeon_corpse -> 外科医生的尸体)
            if (first.EndsWith("_corpse", StringComparison.OrdinalIgnoreCase))
            {
                string baseId = first.Substring(0, first.Length - "_corpse".Length);
                string baseName;

                if (ClassNameById.TryGetValue(baseId, out baseName) && !string.IsNullOrEmpty(baseName))
                {
                    return baseName + "的尸体";
                }

                if (ClassNameById.TryGetValue(first, out name) && !string.IsNullOrEmpty(name))
                {
                    return name;
                }

                return "尸体";
            }

            if (ClassNameById.TryGetValue(first, out name) && !string.IsNullOrEmpty(name))
            {
                return name;
            }

            return first;
        }

        /// <summary>Resolves class ids on demand (one pass over the po for the ids we actually use).</summary>
        public static void EnsureClassNames(IEnumerable<string> classIds)
        {
            var wanted = new HashSet<string>(StringComparer.Ordinal);
            foreach (string raw in classIds)
            {
                if (string.IsNullOrEmpty(raw))
                {
                    continue;
                }

                string id = raw;
                int slash = id.IndexOf('/');
                if (slash > 0)
                {
                    id = id.Substring(0, slash);
                }

                if (id.Length > 0 && !ClassNameById.ContainsKey(id) && !ClassNameMisses.Contains(id))
                {
                    wanted.Add(id);
                }

                if (id.EndsWith("_corpse", StringComparison.OrdinalIgnoreCase))
                {
                    string baseId = id.Substring(0, id.Length - "_corpse".Length);
                    if (baseId.Length > 0 && !ClassNameById.ContainsKey(baseId) && !ClassNameMisses.Contains(baseId))
                    {
                        wanted.Add(baseId);
                    }
                }
            }

            if (wanted.Count == 0 || _catalogLoaded == false)
            {
                return;
            }

            string streamingAssets = Path.GetDirectoryName(_catalogExcelDir);
            if (string.IsNullOrEmpty(streamingAssets))
            {
                return;
            }

            string poDir = Path.Combine(streamingAssets, "Localization", "Poedit");
            foreach (string language in new[] { "zh_CN.po", "tw_CN.po" })
            {
                string poPath = Path.Combine(poDir, language);
                if (!File.Exists(poPath))
                {
                    continue;
                }

                try
                {
                    string pendingKey = null;

                    foreach (string raw in File.ReadLines(poPath, System.Text.Encoding.UTF8))
                    {
                        string line = raw.Trim();

                        if (line.Length == 0)
                        {
                            pendingKey = null;
                            continue;
                        }

                        if (line[0] == '#')
                        {
                            continue;
                        }

                        if (line.StartsWith("msgctxt \"", StringComparison.Ordinal))
                        {
                            pendingKey = Unquote(line, 9);
                            continue;
                        }

                        if (line.StartsWith("msgstr \"", StringComparison.Ordinal) && pendingKey != null)
                        {
                            if (wanted.Contains(pendingKey))
                            {
                                string name = CleanLocalized(Unquote(line, 8));
                                if (name.Length > 0)
                                {
                                    ClassNameById[pendingKey] = name;
                                    wanted.Remove(pendingKey);
                                }
                            }

                            pendingKey = null;
                        }
                    }
                }
                catch
                {
                    // ignore
                }

                if (wanted.Count == 0)
                {
                    return;
                }
            }

            foreach (string id in wanted)
            {
                ClassNameMisses.Add(id);
                UnknownClassIds.Add(id);
            }
        }

        public static string TokenName(string tokenId)
        {
            return string.IsNullOrEmpty(tokenId) ? null : TokenDisplayName(tokenId);
        }

        private static string TokenDisplayName(string id)
        {
            string name;
            return TokenNameById.TryGetValue(id, out name) && !string.IsNullOrEmpty(name) ? name : id;
        }

        private static double DotDamage(string dotId, JToken instance)
        {
            double baseDamage = 0d;

            List<string> effects;
            if (DotEffectsById.TryGetValue(dotId, out effects))
            {
                foreach (string effectId in effects)
                {
                    double value;
                    if (EffectDamageById.TryGetValue(effectId, out value))
                    {
                        baseDamage += value;
                    }
                }
            }

            double change = instance == null ? 0d : (instance.Value<double?>("m_EffectValueChange") ?? 0d);
            double multiplier = instance == null ? 1d : (instance.Value<double?>("m_EffectValueMultiplier") ?? 1d);

            return (baseDamage + change) * multiplier;
        }

        /// <summary>Returns the newest readable snapshot, or null.</summary>
        public static Snapshot TryRead(string saveRoot)
        {
            if (string.IsNullOrEmpty(saveRoot) || !Directory.Exists(saveRoot))
            {
                return null;
            }

            EnsureCatalogs();

            DirectoryInfo run = FindNewestRun(saveRoot);
            if (run == null)
            {
                return null;
            }

            string dataDir;
            string stageName;
            if (!FindNewestStage(run.FullName, out dataDir, out stageName))
            {
                return null;
            }

            return Build(saveRoot, run, dataDir, stageName);
        }

        /// <summary>
        /// Snapshot of the CURRENT battle's first save (lowest Save_000N inside the newest
        /// Save_*_Combat stage) — i.e. the state the battle started from.
        /// </summary>
        public static Snapshot TryReadBattleStart(string saveRoot)
        {
            if (string.IsNullOrEmpty(saveRoot) || !Directory.Exists(saveRoot))
            {
                return null;
            }

            EnsureCatalogs();

            DirectoryInfo run = FindNewestRun(saveRoot);
            if (run == null)
            {
                return null;
            }

            DirectoryInfo stage = null;
            foreach (string candidate in SafeDirs(run.FullName, "Save_*"))
            {
                if (!candidate.EndsWith("Combat", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var info = new DirectoryInfo(candidate);
                if (stage == null || info.LastWriteTimeUtc > stage.LastWriteTimeUtc)
                {
                    stage = info;
                }
            }

            if (stage == null)
            {
                return null;
            }

            // Lowest Save_000N = the state the battle was created in (before round 1 effects).
            DirectoryInfo chosen = null;
            foreach (string sub in SafeDirs(stage.FullName, "Save_*"))
            {
                var info = new DirectoryInfo(sub);
                if (chosen == null || string.CompareOrdinal(info.Name, chosen.Name) < 0)
                {
                    chosen = info;
                }
            }

            if (chosen == null)
            {
                return null;
            }

            return Build(saveRoot, run, chosen.FullName, stage.Name);
        }

        /// <summary>Round stored in &lt;save&gt;\combat\combat.json, or -1 when unreadable.</summary>
        /// <summary>First save of the current battle whose Round >= 1 (start-of-round effects applied).</summary>
        public static Snapshot TryReadBattleRound1(string saveRoot)
        {
            if (string.IsNullOrEmpty(saveRoot) || !Directory.Exists(saveRoot))
            {
                return null;
            }

            EnsureCatalogs();

            DirectoryInfo run = FindNewestRun(saveRoot);
            if (run == null)
            {
                return null;
            }

            DirectoryInfo stage = null;
            foreach (string candidate in SafeDirs(run.FullName, "Save_*"))
            {
                if (!candidate.EndsWith("Combat", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var info = new DirectoryInfo(candidate);
                if (stage == null || info.LastWriteTimeUtc > stage.LastWriteTimeUtc)
                {
                    stage = info;
                }
            }

            if (stage == null)
            {
                return null;
            }

            var candidates = new List<DirectoryInfo>();
            foreach (string sub in SafeDirs(stage.FullName, "Save_*"))
            {
                candidates.Add(new DirectoryInfo(sub));
            }

            candidates.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));

            foreach (DirectoryInfo candidate in candidates)
            {
                if (ReadRound(candidate.FullName) >= 1)
                {
                    return Build(saveRoot, run, candidate.FullName, stage.Name);
                }
            }

            return null;
        }
        private static int ReadRound(string dataDir)
        {
            try
            {
                JObject combat = LoadJson(Path.Combine(dataDir, "combat", "combat.json"));
                if (combat == null)
                {
                    return -1;
                }

                JToken order = combat.SelectToken("battle.turn_order");
                return order == null ? -1 : (int)order.Value<long>("m_Round");
            }
            catch
            {
                return -1;
            }
        }
        // ------------------------------------------------------------ discovery

        private static DirectoryInfo FindNewestRun(string saveRoot)
        {
            DirectoryInfo newest = null;

            foreach (string steamDir in SafeDirs(saveRoot, "*"))
            {
                string profilesDir = Path.Combine(steamDir, "profiles");
                if (!Directory.Exists(profilesDir))
                {
                    continue;
                }

                foreach (string runsRoot in SafeDirs(profilesDir, "*_runs"))
                {
                    foreach (string runDir in SafeDirs(runsRoot, "*"))
                    {
                        string name = Path.GetFileName(runDir);
                        if (!LooksLikeRunName(name))
                        {
                            continue;
                        }

                        DateTime written;
                        try
                        {
                            written = Directory.GetLastWriteTimeUtc(runDir);
                        }
                        catch
                        {
                            continue;
                        }

                        if (newest == null || written > newest.LastWriteTimeUtc)
                        {
                            newest = new DirectoryInfo(runDir);
                        }
                    }
                }
            }

            return newest;
        }

        private static bool LooksLikeRunName(string name)
        {
            if (name == null || name.Length != 13 || name[6] != '_')
            {
                return false;
            }

            for (int i = 0; i < name.Length; i++)
            {
                if (i == 6)
                {
                    continue;
                }

                if (!char.IsDigit(name[i]))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool FindNewestStage(string runDir, out string dataDir, out string stageName)
        {
            dataDir = null;
            stageName = null;

            DirectoryInfo best = null;
            foreach (string stage in SafeDirs(runDir, "Save_*"))
            {
                var di = new DirectoryInfo(stage);
                if (best == null || di.LastWriteTimeUtc > best.LastWriteTimeUtc)
                {
                    best = di;
                }
            }

            if (best == null)
            {
                return false;
            }

            stageName = best.Name;

            DirectoryInfo inner = null;
            foreach (string sub in SafeDirs(best.FullName, "Save_*"))
            {
                var di = new DirectoryInfo(sub);
                if (inner == null || string.CompareOrdinal(di.Name, inner.Name) > 0)
                {
                    inner = di;
                }
            }

            dataDir = inner != null ? inner.FullName : best.FullName;
            return true;
        }

        private static IEnumerable<string> SafeDirs(string dir, string pattern)
        {
            try
            {
                return Directory.GetDirectories(dir, pattern);
            }
            catch
            {
                return new string[0];
            }
        }

        // ------------------------------------------------------------- parsing

        private static Snapshot Build(string saveRoot, DirectoryInfo run, string dataDir, string stageName)
        {
            var s = new Snapshot
            {
                DataDir = dataDir,
                StageName = stageName,
                RunName = run.Name,
                Written = Directory.GetLastWriteTime(dataDir)
            };

            JObject runJson = LoadJson(Path.Combine(dataDir, "run.json"));
            JObject mapJson = LoadJson(Path.Combine(dataDir, "map.json"));
            JObject actorsJson = LoadJson(Path.Combine(dataDir, "actors.json"));
            JObject combatJson = LoadJson(Path.Combine(dataDir, "combat", "combat.json"));
            JObject scenarioJson = LoadJson(Path.Combine(dataDir, "combat", "combat_scenario_data.json"));

            if (runJson != null)
            {
                s.RunStart = Str(runJson, "run_manager", "m_runStartTimestamp");
                s.NodeCount = Int(runJson, "run_manager", "m_NodeCount");

                s.Biome = JoinStrings(runJson.SelectTokens("biome_manager.m_VisitedBiomeInstances[*].m_BiomeType"));
                s.LastNode = Str(runJson, "biome_manager", "m_LastReachedNodeType");

                s.CombatCount = Int(runJson, "combat_manager", "m_CombatCount");
                s.CombatConfig = JoinStrings(runJson.SelectTokens("combat_manager.m_BattleConfigurationHistory[*]"));
                s.RoundCount = Int(runJson, "combat_manager", "m_RoundCount");
            }

            if (mapJson != null)
            {
                s.MapSeed = Long(mapJson, "seed");
            }

            if (scenarioJson != null)
            {
                s.LoadedFrom = Str(scenarioJson, "m_LoadedFrom");
                s.SceneName = Str(scenarioJson, "m_BackgroundSceneName");
                s.IsExpeditionBoss = Bool(scenarioJson, "m_IsExpeditionBoss");
            }

            if (combatJson != null)
            {
                s.InCombat = true;

                JToken order = combatJson.SelectToken("battle.turn_order");
                if (order != null)
                {
                    s.Round = (int)order.Value<long>("m_Round");
                    s.Turn = (int)order.Value<long>("m_CurrentTurn");
                    s.TotalTurns = (int)order.Value<long>("m_TotalTurns");

                    JToken list = order["m_TurnOrder"];
                    if (list is JArray arr)
                    {
                        foreach (JToken e in arr)
                        {
                            if (e is JValue)
                            {
                                s.Order.Add(e.ToString());
                            }
                            else
                            {
                                string guid = e.Value<string>("m_ActorGuid") ?? "?";
                                string type = e.Value<string>("m_TurnType") ?? "?";
                                s.Order.Add(guid + ":" + type);
                            }
                        }
                    }
                }

                JToken teams = combatJson.SelectToken("battle.teams.teams");
                if (teams is JArray teamArray)
                {
                    foreach (JToken t in teamArray)
                    {
                        var row = new TeamRow
                        {
                            TeamIndex = (int)(t.Value<long?>("m_TeamIndex") ?? 0),
                            TeamSize = (int)(t.Value<long?>("m_TeamSize") ?? 0),
                            Controller = t.Value<string>("m_ActorControllerType") ?? "?",
                        };

                        JToken queue = t.SelectToken("summon_controller.m_Queue");
                        row.Summons = queue is JArray q ? q.Count : 0;

                        if (t["actors"] is JArray actorIds)
                        {
                            foreach (JToken id in actorIds)
                            {
                                uint guid = id.Value<uint>();
                                if (guid == 0)
                                {
                                    continue;
                                }

                                ActorRow a = ReadActor(actorsJson, guid);
                                if (a != null)
                                {
                                    row.Actors.Add(a);
                                }
                            }
                        }

                        s.Teams.Add(row);
                    }
                }
            }

            s.Teams.Sort((x, y) => x.TeamIndex.CompareTo(y.TeamIndex));
            return s;
        }

        private static ActorRow ReadActor(JObject actorsJson, uint guid)
        {
            if (actorsJson == null)
            {
                return null;
            }

            JToken e = actorsJson["ActorGUID" + guid.ToString(CultureInfo.InvariantCulture)];
            if (e == null)
            {
                return null;
            }

            var a = new ActorRow
            {
                Guid = guid,
                Name = e.Value<string>("m_ActorName") ?? string.Empty,
                ClassId = JoinStrings(e.SelectTokens("m_ActorDataIds[*]")),
                Hp = e.Value<double?>("m_Hp") ?? 0d,
                Stress = e.Value<double?>("m_Stress") ?? 0d,
                Position = (int)(e.Value<long?>("m_TeamPosition") ?? 0),
                InBattle = e.Value<bool?>("m_IsInBattle") ?? false,
                TurnType = e.Value<string>("m_CurrentBattleTurnType") ?? string.Empty
            };

            // TokenContainer / BuffContainer / DotContainer are JSON *arrays* of instances
            // (each instance: m_Definition = id string, m_DurationAmountRemaining = turns left).
            a.Tokens.AddRange(DescribeTokens(e["TokenContainer"]));

            int visibleBuffs = 0;
            foreach (JToken buff in AsArray(e["BuffContainer"]))
            {
                string buffId = InstanceId(buff);
                if (buffId == null || IsVisibleBuff(buffId))
                {
                    visibleBuffs++;
                }
            }

            a.Buffs = visibleBuffs;

            foreach (JToken t in AsArray(e["DotContainer"]))
            {
                DotEntry dot = DescribeDot(t);
                if (dot != null)
                {
                    a.Dots.Add(dot);
                }
            }

            return a;
        }

        /// <summary>
        /// Tokens are shown as "id" or "id×count" only — the game UI does not show their
        /// remaining duration, so neither do we. Hidden tokens (m_IsHidden) are skipped.
        /// </summary>
        private static List<string> DescribeTokens(JToken container)
        {
            var result = new List<string>();

            var order = new List<string>();
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);

            foreach (JToken t in AsArray(container))
            {
                if (t == null || t.Type == JTokenType.Null)
                {
                    continue;
                }

                if (t.Value<bool?>("m_IsHidden") ?? false)
                {
                    continue;
                }

                string id = InstanceId(t);
                if (string.IsNullOrEmpty(id))
                {
                    continue;
                }

                if (!counts.ContainsKey(id))
                {
                    counts[id] = 0;
                    order.Add(id);
                }

                counts[id] = counts[id] + 1;
            }

            foreach (string id in order)
            {
                int count = counts[id];
                string name = TokenDisplayName(id);
                result.Add(count > 1 ? name + "×" + count : name);
            }

            return result;
        }

        private static DotEntry DescribeDot(JToken instance)
        {
            if (instance == null || instance.Type == JTokenType.Null)
            {
                return null;
            }

            string id = InstanceId(instance);
            if (string.IsNullOrEmpty(id))
            {
                return null;
            }

            string type;
            if (!DotTypeById.TryGetValue(id, out type))
            {
                type = string.Empty;
            }

            return new DotEntry
            {
                Id = id,
                Type = type,
                Damage = DotDamage(id, instance),
                Duration = instance.Value<int?>("m_DurationAmountRemaining") ?? 0
            };
        }

        private static string InstanceId(JToken instance)
        {
            if (instance == null || instance.Type == JTokenType.Null)
            {
                return null;
            }

            if (instance is JValue value)
            {
                return value.ToString();
            }

            JToken def = instance["m_Definition"];
            if (def is JValue defValue)
            {
                return defValue.ToString();
            }

            if (def != null)
            {
                string viaDef = def.Value<string>("m_Id") ?? def.Value<string>("Id") ?? def.Value<string>("m_Name");
                if (!string.IsNullOrEmpty(viaDef))
                {
                    return viaDef;
                }
            }

            return instance.Value<string>("m_Id") ?? instance.Value<string>("m_SourceId");
        }

        private static List<JToken> AsArray(JToken token)
        {
            return token is JArray array ? array.ToList() : new List<JToken>();
        }

        // ------------------------------------------------------------ rendering

        public static string BuildText(Snapshot s)
        {
            return BuildTextInternal(s, false);
        }

        /// <summary>Same report but with localized CLASS names and no personal names (state window).</summary>
        private static string BuildTextInternal(Snapshot s, bool useClassNames)
        {
            if (s == null)
            {
                return "没有找到存档。";
            }
            if (useClassNames)
            {
                var ids = new List<string>();
                foreach (TeamRow team in s.Teams)
                {
                    foreach (ActorRow actor in team.Actors)
                    {
                        ids.Add(actor.ClassId);
                    }
                }

                EnsureClassNames(ids);
            }


            var sb = new System.Text.StringBuilder();
            sb.AppendLine("================ DD2 存档读数器 ================");
            sb.AppendLine("存档目录 : " + s.DataDir);
            sb.AppendLine("档位     : " + s.StageName + "    写入 " + s.Written.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            sb.AppendLine("旅程     : run " + s.RunName + "   开始 " + (s.RunStart ?? "-"));
            sb.AppendLine("区域/节点: " + (s.Biome ?? "-") + "   最近节点 " + (s.LastNode ?? "-") + "   地图种子 " + s.MapSeed.ToString(CultureInfo.InvariantCulture));

            if (!string.IsNullOrEmpty(s.LoadedFrom) || !string.IsNullOrEmpty(s.SceneName))
            {
                sb.AppendLine("战斗来源 : " + (s.LoadedFrom ?? "-") + "   配置 " + (s.CombatConfig ?? "-") + "   场景 " + (s.SceneName ?? "-"));
            }

            sb.AppendLine("本局战斗 : 第 " + s.CombatCount + " 场   战斗配置历史 [" + (s.CombatConfig ?? "-") + "]   m_RoundCount=" + s.RoundCount);

            if (!s.InCombat)
            {
                sb.AppendLine();
                sb.AppendLine("当前不在战斗中。");
                return sb.ToString();
            }

            sb.AppendLine();

            sb.AppendLine("==== DD2 状态读取 现在=====");

            if (s.HasLiveCombat)
            {
                string roundText = s.LiveRound >= 0 ? ("Round " + s.LiveRound) : "Round ?";
                string turnText = s.LiveTurn >= 0 ? ("Turn " + s.LiveTurn) : "Turn ?";
                string totalText = s.LiveTotalTurns >= 0 ? ("（累计行动 " + s.LiveTotalTurns + "）") : "（累计行动 ?）";

                sb.AppendLine("回合     : " + roundText + " / " + turnText + totalText + "  [实时]");
                sb.AppendLine("行动队列 : " + FormatQueue(s, useClassNames, true) + "  [实时]");
            }
            else
            {
                sb.AppendLine("回合     : Round " + s.Round + " / Turn " + s.Turn + "（累计行动 " + s.TotalTurns + "）  [存档快照]");
                sb.AppendLine("行动队列 : " + FormatQueue(s, useClassNames, false) + "  [存档快照]");
            }

            foreach (TeamRow t in s.Teams)
            {
                string label = t.TeamIndex == 0 ? "我方" : "敌方";
                sb.AppendLine();
                sb.AppendLine("--- " + label + " team " + t.TeamIndex + " (" + t.Controller + ")  上限 " + t.TeamSize + "  召唤队列 " + t.Summons + " ---");

                if (t.Actors.Count == 0)
                {
                    sb.AppendLine("  (无单位)");
                    continue;
                }

                foreach (ActorRow a in t.Actors)
                {
                    sb.AppendLine(useClassNames ? FormatActorClass(a) : FormatActor(a));
                }
            }

            return sb.ToString();
        }

        // ------------------------------------------------ state window helpers (column layout)

        public static string StateTitle()
        {
            return "==== DD2 状态读取 现在=====";
        }

        public static string StateRoundLine(Snapshot s)
        {
            if (s.HasLiveCombat)
            {
                string roundText = s.LiveRound >= 0 ? ("Round " + s.LiveRound) : "Round ?";
                string turnText = s.LiveTurn >= 0 ? ("Turn " + s.LiveTurn) : "Turn ?";
                string totalText = s.LiveTotalTurns >= 0 ? ("（累计行动 " + s.LiveTotalTurns + "）") : "（累计行动 ?）";
                return "回合     : " + roundText + " / " + turnText + totalText + "  [实时]";
            }

            return "回合     : Round " + s.Round + " / Turn " + s.Turn + "（累计行动 " + s.TotalTurns + "）  [存档快照]";
        }

        public static string StateQueueLine(Snapshot s)
        {
            return "行动队列 : " + FormatQueue(s, true, s.HasLiveCombat) + (s.HasLiveCombat ? "  [实时]" : "  [存档快照]");
        }

        public static string TeamHeaderLine(TeamRow t)
        {
            string label = t.TeamIndex == 0 ? "我方" : "敌方";
            return "--- " + label + " team " + t.TeamIndex + " (" + t.Controller + ")  上限 " + t.TeamSize + "  召唤队列 " + t.Summons + " ---";
        }

        /// <summary>Resolves class names used by the state window (one po pass on first use).</summary>
        public static void EnsureSnapshotClassNames(Snapshot s)
        {
            if (s == null)
            {
                return;
            }

            var ids = new List<string>();
            foreach (TeamRow team in s.Teams)
            {
                foreach (ActorRow actor in team.Actors)
                {
                    ids.Add(actor.ClassId);
                }
            }

            EnsureClassNames(ids);
        }

        /// <summary>One actor row split into fixed columns for the state window.</summary>
        public static string[] ActorCells(ActorRow a)
        {
            string name = ClassName(a.ClassId);
            if (string.IsNullOrEmpty(name))
            {
                name = string.IsNullOrEmpty(a.Name) ? "(无名)" : a.Name;
            }
            else if (name == a.ClassId && name.Length > 14)
            {
                // unresolved raw id: shorten so the fixed-width column does not cut it mid-glyph
                name = name.Substring(0, 13) + "…";
            }

            string tokens = a.Tokens.Count == 0 ? "-" : string.Join(",", a.Tokens);

            var extra = new List<string>();
            if (a.Buffs > 0) { extra.Add("buff=" + a.Buffs); }
            if (!string.IsNullOrEmpty(a.TurnType)) { extra.Add("turn=" + a.TurnType); }

            return new[]
            {
                "站位 " + PositionLabel(a.Position),
                name,
                "HP " + a.Hp.ToString("0.#", CultureInfo.InvariantCulture),
                "压力 " + a.Stress.ToString("0.#", CultureInfo.InvariantCulture),
                "标记[" + tokens + "]",
                "Dot[" + FormatDots(a.Dots) + "]",
                string.Join(" ", extra)
            };
        }
        /// <summary>Turn queue line. In state mode uses class names and appends (position) for enemies.</summary>
        private static string FormatQueue(Snapshot s, bool useClassNames, bool live)
        {
            List<string> names = live ? s.LiveOrder : s.Order;
            List<uint> guids = s.LiveOrderGuids;

            if (names == null || names.Count == 0)
            {
                return live ? "(空)" : "(空 · 存档写于战斗开始时，之后不再更新)";
            }

            if (!useClassNames)
            {
                if (live)
                {
                    return string.Join(" → ", names);
                }

                var reportParts = new List<string>();
                foreach (string raw in names)
                {
                    uint reportGuid = ParseQueueGuid(raw);
                    reportParts.Add(reportGuid != 0 ? ActorNameFor(s, reportGuid) : raw);
                }

                return string.Join(" → ", reportParts);
            }

            var parts = new List<string>();

            for (int i = 0; i < names.Count; i++)
            {
                uint guid = 0;

                if (live)
                {
                    if (i < guids.Count)
                    {
                        guid = guids[i];
                    }
                }
                else
                {
                    guid = ParseQueueGuid(names[i]);
                }

                parts.Add(DescribeQueueActor(s, guid));
            }

            return string.Join(" → ", parts);
        }

        private static uint ParseQueueGuid(string raw)
        {
            if (string.IsNullOrEmpty(raw))
            {
                return 0;
            }

            int colon = raw.IndexOf(':');
            string text = colon > 0 ? raw.Substring(0, colon) : raw;

            uint guid;
            return uint.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out guid) ? guid : 0;
        }

        private static string ActorNameFor(Snapshot s, uint guid)
        {
            foreach (TeamRow team in s.Teams)
            {
                foreach (ActorRow actor in team.Actors)
                {
                    if (actor.Guid == guid)
                    {
                        return string.IsNullOrEmpty(actor.Name) ? actor.ClassId : actor.Name;
                    }
                }
            }

            return guid.ToString(CultureInfo.InvariantCulture);
        }
        private static string DescribeQueueActor(Snapshot s, uint guid)
        {
            foreach (TeamRow team in s.Teams)
            {
                foreach (ActorRow actor in team.Actors)
                {
                    if (actor.Guid != guid)
                    {
                        continue;
                    }

                    string name = ClassName(actor.ClassId);
                    if (team.TeamIndex != 0)
                    {
                        name += "（" + (actor.Position + 1) + "）";
                    }

                    return name;
                }
            }

            return guid == 0 ? "?" : guid.ToString(CultureInfo.InvariantCulture);
        }
        /// <summary>Compact line for the state window: localized class name, no personal name.</summary>
        private static string FormatActorClass(ActorRow a)
        {
            string name = ClassName(a.ClassId);
            if (string.IsNullOrEmpty(name))
            {
                name = string.IsNullOrEmpty(a.Name) ? "(无名)" : a.Name;
            }

            string tokens = a.Tokens.Count == 0 ? "-" : string.Join(",", a.Tokens);
            string dots = FormatDots(a.Dots);

            var extra = new List<string>();
            if (a.Buffs > 0) { extra.Add("buff=" + a.Buffs); }
            if (!string.IsNullOrEmpty(a.TurnType)) { extra.Add("turn=" + a.TurnType); }
            string tail = extra.Count > 0 ? "  " + string.Join(" ", extra) : string.Empty;

            return "  站位 " + PositionLabel(a.Position)
                 + "  " + PadDisplay(name, 12)
                 + " HP " + PadDisplay(a.Hp.ToString("0.#", CultureInfo.InvariantCulture), 4, left: true)
                 + "  压力 " + PadDisplay(a.Stress.ToString("0.#", CultureInfo.InvariantCulture), 3, left: true)
                 + "  标记[" + PadDisplay(tokens, 12) + "]"
                 + " Dot[" + dots + "]"
                 + tail;
        }

        /// <summary>Display position: internal m_TeamPosition is 0-based, players see 1..4.</summary>
        private static string PositionLabel(int position)
        {
            return position >= 0 ? (position + 1).ToString(CultureInfo.InvariantCulture) : "-";
        }
        private static int DisplayWidth(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return 0;
            }

            int width = 0;
            foreach (char c in text)
            {
                width += IsWideChar(c) ? 2 : 1;
            }

            return width;
        }

        /// <summary>CJK / fullwidth glyphs occupy two terminal columns.</summary>
        private static bool IsWideChar(char c)
        {
            return (c >= 0x1100 && c <= 0x115F)
                   || (c >= 0x2E80 && c <= 0xA4CF)
                   || (c >= 0xAC00 && c <= 0xD7A3)
                   || (c >= 0xF900 && c <= 0xFAFF)
                   || (c >= 0xFE30 && c <= 0xFE4F)
                   || (c >= 0xFF00 && c <= 0xFF60)
                   || (c >= 0xFFE0 && c <= 0xFFE6);
        }

        private static string PadDisplay(string text, int width, bool left = false)
        {
            text = text ?? string.Empty;

            int pad = width - DisplayWidth(text);
            if (pad <= 0)
            {
                return text;
            }

            string spaces = new string(' ', pad);
            return left ? spaces + text : text + spaces;
        }

        private static bool IsVisibleBuff(string buffId)
        {
            if (VisibleBuffById.Count == 0)
            {
                return true;
            }

            bool visible;
            return VisibleBuffById.TryGetValue(buffId, out visible) ? visible : true;
        }

        private static string FormatActor(ActorRow a)
        {
            string name = string.IsNullOrEmpty(a.Name) ? "(无名)" : a.Name;
            string tokens = a.Tokens.Count == 0 ? "-" : string.Join(",", a.Tokens);

            var extra = new List<string>();
            if (a.Buffs > 0) { extra.Add("buff=" + a.Buffs); }
            if (!string.IsNullOrEmpty(a.TurnType)) { extra.Add("turn=" + a.TurnType); }

            string tail = extra.Count > 0 ? "  " + string.Join(" ", extra) : string.Empty;
            string dots = FormatDots(a.Dots);

            return "  站位 " + PositionLabel(a.Position)
                 + "  " + PadDisplay(name, 14)
                 + " " + PadDisplay(a.ClassId, 34)
                 + " HP " + PadDisplay(a.Hp.ToString("0.#", CultureInfo.InvariantCulture), 5, left: true)
                 + " 压力 " + PadDisplay(a.Stress.ToString("0.#", CultureInfo.InvariantCulture), 4, left: true)
                 + " 标记[" + tokens + "] Dot[" + dots + "]"
                 + tail;
        }

        private static readonly string[] DotTypeOrder = { "bleed", "blight", "burn", "hot", "horror" };

        /// <summary>
        /// "血(2*3 1*2)毒(3*6)火(4*3) 总计(10)" — per flavour, "damage*turns" per instance,
        /// and the damage the target takes at the NEXT turn start (sum of one tick per instance).
        /// </summary>
        private static string FormatDots(List<DotEntry> dots)
        {
            if (dots == null || dots.Count == 0)
            {
                return "-";
            }

            var groups = new List<KeyValuePair<string, List<DotEntry>>>();

            foreach (string type in DotTypeOrder)
            {
                var entries = dots.FindAll(d => string.Equals(d.Type, type, StringComparison.OrdinalIgnoreCase));
                if (entries.Count > 0)
                {
                    groups.Add(new KeyValuePair<string, List<DotEntry>>(type, entries));
                }
            }

            // Any flavour not in the fixed order goes last.
            var others = dots.FindAll(d => Array.IndexOf(DotTypeOrder, (d.Type ?? string.Empty).ToLowerInvariant()) < 0);
            if (others.Count > 0)
            {
                groups.Add(new KeyValuePair<string, List<DotEntry>>(others[0].Type ?? "other", others));
            }

            var sb = new System.Text.StringBuilder();
            double nextTickDamage = 0d;

            foreach (KeyValuePair<string, List<DotEntry>> group in groups)
            {
                sb.Append(DotTypeLabel(group.Key));
                sb.Append('(');

                for (int i = 0; i < group.Value.Count; i++)
                {
                    if (i > 0)
                    {
                        sb.Append(' ');
                    }

                    DotEntry d = group.Value[i];
                    sb.Append(FormatNumber(d.Damage));
                    sb.Append('*');
                    sb.Append(d.Duration.ToString(CultureInfo.InvariantCulture));

                    if (!string.Equals(group.Key, "hot", StringComparison.OrdinalIgnoreCase))
                    {
                        nextTickDamage += d.Damage;
                    }
                }

                sb.Append(')');
            }

            sb.Append(" 总计(");
            sb.Append(FormatNumber(nextTickDamage));
            sb.Append(')');

            return sb.ToString();
        }

        private static string DotTypeLabel(string type)
        {
            switch ((type ?? string.Empty).ToLowerInvariant())
            {
                case "bleed": return "血";
                case "blight": return "毒";
                case "burn": return "火";
                case "hot": return "治疗";
                case "horror": return "恐惧";
                default: return string.IsNullOrEmpty(type) ? "其他" : type;
            }
        }

        private static string FormatNumber(double value)
        {
            return value.ToString("0.#", CultureInfo.InvariantCulture);
        }

        /// <summary>Report body from the "DD2 状态读取" divider down (turn/queue/teams/units).</summary>
        public static string BuildStateText(Snapshot s)
        {
            if (s == null)
            {
                return "(无存档)";
            }

            string full = BuildTextInternal(s, true);
            const string marker = "==== DD2 状态读取 现在=====";
            int index = full.IndexOf(marker, StringComparison.Ordinal);
            return index >= 0 ? full.Substring(index) : full;
        }
        public static string BuildJson(Snapshot s)
        {
            var root = new JObject();
            if (s == null)
            {
                root["found"] = false;
                return root.ToString();
            }

            root["found"] = true;
            root["dataDir"] = s.DataDir;
            root["stage"] = s.StageName;
            root["run"] = s.RunName;
            root["written"] = s.Written.ToString("o", CultureInfo.InvariantCulture);
            root["runStart"] = s.RunStart;
            root["biome"] = s.Biome;
            root["lastNode"] = s.LastNode;
            root["mapSeed"] = s.MapSeed;
            root["combatCount"] = s.CombatCount;
            root["combatConfig"] = s.CombatConfig;
            root["roundCount"] = s.RoundCount;
            root["inCombat"] = s.InCombat;

            if (s.InCombat)
            {
                root["round"] = s.Round;
                root["turn"] = s.Turn;
                root["totalTurns"] = s.TotalTurns;
                root["order"] = new JArray(s.Order.Cast<object>().ToArray());
                root["orderSource"] = "save";
                root["loadedFrom"] = s.LoadedFrom;
                root["scene"] = s.SceneName;
                root["isExpeditionBoss"] = s.IsExpeditionBoss;
            }

            if (s.HasLiveCombat)
            {
                root["live"] = new JObject
                {
                    ["round"] = s.LiveRound,
                    ["turn"] = s.LiveTurn,
                    ["totalTurns"] = s.LiveTotalTurns,
                    ["order"] = new JArray(s.LiveOrder.Cast<object>().ToArray())
                };
                root["orderSource"] = "live";
            }

            var teams = new JArray();
            foreach (TeamRow t in s.Teams)
            {
                var actors = new JArray();
                foreach (ActorRow a in t.Actors)
                {
                    actors.Add(new JObject
                    {
                        ["guid"] = a.Guid,
                        ["name"] = a.Name,
                        ["class"] = a.ClassId,
                        ["hp"] = a.Hp,
                        ["stress"] = a.Stress,
                        ["position"] = a.Position,
                        ["tokens"] = new JArray(a.Tokens.Cast<object>().ToArray()),
                        ["buffs"] = a.Buffs,
                        ["dots"] = new JArray(a.Dots.Select(d => (object)new JObject
                        {
                            ["id"] = d.Id,
                            ["type"] = d.Type,
                            ["damage"] = d.Damage,
                            ["duration"] = d.Duration
                        }).ToArray()),
                        ["inBattle"] = a.InBattle,
                        ["turnType"] = a.TurnType
                    });
                }

                teams.Add(new JObject
                {
                    ["teamIndex"] = t.TeamIndex,
                    ["controller"] = t.Controller,
                    ["teamSize"] = t.TeamSize,
                    ["summons"] = t.Summons,
                    ["actors"] = actors
                });
            }

            root["teams"] = teams;
            return root.ToString();
        }

        /// <summary>One-line summary for the rolling log.</summary>
        public static string BuildLogLine(Snapshot s)
        {
            if (s == null)
            {
                return "no-save";
            }

            string party = string.Join(", ", s.Teams
                .Where(t => t.TeamIndex == 0)
                .SelectMany(t => t.Actors)
                .Select(a => (string.IsNullOrEmpty(a.Name) ? a.ClassId : a.Name) + ":" + a.Hp.ToString("0.#", CultureInfo.InvariantCulture)));

            return string.Format(
                CultureInfo.InvariantCulture,
                "{0:yyyy-MM-dd HH:mm:ss}  combat#{1}  round={2} turn={3}  order={4}  party=[{5}]",
                DateTime.Now, s.CombatCount, s.Round, s.Turn, s.Order.Count, party);
        }

        /// <summary>
        /// Writes save_latest.txt / save_latest.json / save_log.txt into <paramref name="outputDir"/>.
        /// Returns the number of bytes written for the text report.
        /// </summary>
        public static int Write(string outputDir, Snapshot s)
        {
            if (string.IsNullOrEmpty(outputDir) || s == null)
            {
                return 0;
            }

            Directory.CreateDirectory(outputDir);

            string text = BuildText(s);
            File.WriteAllText(Path.Combine(outputDir, "save_latest.txt"), text, Utf8);
            File.WriteAllText(Path.Combine(outputDir, "save_latest.json"), BuildJson(s), Utf8);
            File.AppendAllText(Path.Combine(outputDir, "save_log.txt"), BuildLogLine(s) + Environment.NewLine, Utf8);

            return text.Length;
        }

        /// <summary>Writes battle_round1.txt / battle_round1.json (first save with Round >= 1).</summary>
        public static int WriteBattleRound1(string outputDir, Snapshot s)
        {
            if (string.IsNullOrEmpty(outputDir) || s == null)
            {
                return 0;
            }

            Directory.CreateDirectory(outputDir);

            string text = BuildText(s);
            File.WriteAllText(Path.Combine(outputDir, "battle_round1.txt"), text, Utf8);
            File.WriteAllText(Path.Combine(outputDir, "battle_round1.json"), BuildJson(s), Utf8);
            return text.Length;
        }
        /// <summary>Writes battle_start.txt / battle_start.json (the state the current battle began in).</summary>
        public static int WriteBattleStart(string outputDir, Snapshot s)
        {
            if (string.IsNullOrEmpty(outputDir) || s == null)
            {
                return 0;
            }

            Directory.CreateDirectory(outputDir);

            string text = BuildText(s);
            File.WriteAllText(Path.Combine(outputDir, "battle_start.txt"), text, Utf8);
            File.WriteAllText(Path.Combine(outputDir, "battle_start.json"), BuildJson(s), Utf8);
            return text.Length;
        }
        private static readonly System.Text.UTF8Encoding Utf8 = new System.Text.UTF8Encoding(false);


        // --------------------------------------------------------------- helpers

        private static JObject LoadJson(string path)
        {
            try
            {
                if (!File.Exists(path))
                {
                    return null;
                }

                return JObject.Parse(File.ReadAllText(path, System.Text.Encoding.UTF8));
            }
            catch
            {
                return null;
            }
        }

        private static string Str(JObject o, params string[] path)
        {
            JToken t = o;
            foreach (string p in path)
            {
                if (t == null)
                {
                    return null;
                }

                t = t[p];
            }

            return t != null && t.Type != JTokenType.Null ? t.ToString() : null;
        }

        private static int Int(JObject o, params string[] path)
        {
            JToken t = o;
            foreach (string p in path)
            {
                if (t == null)
                {
                    return 0;
                }

                t = t[p];
            }

            return t == null || t.Type == JTokenType.Null ? 0 : t.Value<int>();
        }

        private static long Long(JObject o, params string[] path)
        {
            JToken t = o;
            foreach (string p in path)
            {
                if (t == null)
                {
                    return 0;
                }

                t = t[p];
            }

            return t == null || t.Type == JTokenType.Null ? 0 : t.Value<long>();
        }

        private static bool Bool(JObject o, params string[] path)
        {
            JToken t = o;
            foreach (string p in path)
            {
                if (t == null)
                {
                    return false;
                }

                t = t[p];
            }

            return t != null && t.Type != JTokenType.Null && t.Value<bool>();
        }

        private static string JoinStrings(IEnumerable<JToken> tokens)
        {
            if (tokens == null)
            {
                return null;
            }

            var parts = tokens.Where(t => t != null && t.Type != JTokenType.Null).Select(t => t.ToString()).ToList();
            return parts.Count == 0 ? null : string.Join(",", parts);
        }
    }
}
