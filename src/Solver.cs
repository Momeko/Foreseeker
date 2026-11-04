using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Assets.Code.Combat;
using Assets.Code.Game;
using Assets.Code.Library;
using Assets.Code.Math;
using Assets.Code.Skill;
using Assets.Code.Token;
using Assets.Code.Utils;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace DSH.Foreseeker
{
    /// <summary>
    /// A-stage helper: ranks the current actor's legal (skill, target) choices by a resource
    /// score, using the exact damage/crit the game already pre-rolled for this turn.
    /// Read-only: nothing is applied, nothing is mutated.
    /// </summary>
    internal static class Solver
    {
        private const BindingFlags Any =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        internal static bool Visible = false;

        private static float _nextRefresh;
        private static string _header = "solver: idle";
        private static readonly List<Row> Rows = new List<Row>();
        private static readonly HashSet<uint> _doomed = new HashSet<uint>();

        private static object _healthMaxStat;
        private static object _healthDamageStat;
        private static object _dealtStat;
        private static object _receivedStat;
        private static object _deathResistStat;
        private static float _avgEnemyMaxHp = 13f;
        private static object _stressMaxStat;
        private static bool _statsResolved;
        private static object _teams;

        private sealed class Row
        {
            public string Label;
            public float Score;
            public Color Tint;
            public string SkillId;
            public uint TargetGuid;
            public float Damage;
            public object ActorResult;
            public List<Row> Parts;
            public string TargetName;
        }

        internal static void Refresh()
        {
            if (Time.realtimeSinceStartup < _nextRefresh)
            {
                return;
            }

            _nextRefresh = Time.realtimeSinceStartup + (Visible ? 0.7f : 1.5f);

            try
            {
                Build();
            }
            catch (Exception ex)
            {
                _header = "solver error: " + ex.GetType().Name;
            }
        }

        private static string _itemLine = string.Empty;
        private static uint _itemTargetGuid;
        private static object _itemResult;
        private static float _itemScore;
        private static string _itemAdviceKey;

        private static MethodInfo _addPotentialMethod;

        private static MethodInfo FindAddPotentialMethod()
        {
            foreach (MethodInfo method in typeof(SkillCalculation).GetMethods(
                         BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (method.Name != "AddPotentialResult")
                {
                    continue;
                }

                ParameterInfo[] parameters = method.GetParameters();
                if (parameters.Length == 2 && parameters[1].ParameterType == typeof(IReadOnlyList<uint>))
                {
                    return method;
                }
            }

            return null;
        }

        private static List<object> TeamActors(int teamIndex)
        {
            var list = new List<object>();
            var teams = GetMember(_teams, "m_Teams") as IEnumerable;
            if (teams == null)
            {
                return list;
            }

            foreach (object team in teams)
            {
                if (team == null || ToInt(GetMember(team, "m_TeamIndex")) != teamIndex)
                {
                    continue;
                }

                var actors = GetMember(team, "m_Actors") as IEnumerable;
                if (actors == null)
                {
                    continue;
                }

                foreach (object actor in actors)
                {
                    if (actor != null && ToBool(GetMember(actor, "m_IsLiving")))
                    {
                        list.Add(actor);
                    }
                }
            }

            return list;
        }

        /// <summary>
        /// Combat items are free actions (one per hero, one use), so they are advice on top of the
        /// ranked skills rather than a row competing with them.
        /// </summary>
        private static int _partyLoggedBattle;
        private static bool _bossBattle;

        /// <summary>Logs the party's classes and paths once per battle, so a run can be labelled.</summary>
        private static void LogParty(object battle)
        {
            try
            {
                int hash = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(battle);
                if (hash == _partyLoggedBattle)
                {
                    return;
                }

                _partyLoggedBattle = hash;

                Action<string> sink = RandomTrace.Sink;
                if (sink == null)
                {
                    return;
                }

                foreach (object hero in TeamActors(0))
                {
                    string classId = DataId(GetMember(hero, "m_ActorDataClass"));
                    string pathId = DataId(GetMember(hero, "m_ActorDataPath"));
                    int position = ToInt(GetMember(hero, "m_TeamPosition")) + 1;
                    float hp = ToFloat(GetMember(hero, "m_Hp"));
                    float max = Mathf.Max(1f, Stat(hero, _healthMaxStat, "m_Hp"));

                    sink("S HERO class=" + ActorName(hero)
                         + "(" + classId + ")"
                         + " path=" + pathId
                         + " pos=" + position
                         + " hp=" + hp.ToString("F0") + "/" + max.ToString("F0"));
                }

                var bosses = new List<string>();
                foreach (object enemy in TeamActors(1))
                {
                    if (IsBoss(enemy))
                    {
                        bosses.Add(ActorName(enemy) + "(" + DataId(GetMember(enemy, "m_ActorDataClass")) + ")");
                    }
                }

                _bossBattle = bosses.Count > 0;
                if (bosses.Count > 0)
                {
                    sink("S BATTLE boss=1 bosses=" + string.Join("|", bosses.ToArray()));
                }
                else
                {
                    sink("S BATTLE boss=0");
                }
            }
            catch
            {
            }
        }

        private static readonly HashSet<string> BossTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "boss", "end_boss", "biome_boss", "gang_boss", "cultist_boss", "kingdom_boss"
        };

        private static bool IsBoss(object actor)
        {
            object classData = GetMember(actor, "m_ActorDataClass");
            var tags = classData == null ? null : GetMember(classData, "m_Tags") as IEnumerable;
            if (tags == null)
            {
                return false;
            }

            foreach (object tag in tags)
            {
                string text = tag as string;
                if (!string.IsNullOrEmpty(text) && BossTags.Contains(text.Trim()))
                {
                    return true;
                }
            }

            return false;
        }
        private static string DataId(object container)
        {
            if (container == null)
            {
                return "?";
            }

            object id = GetMember(container, "Id");
            string text = id as string;
            return string.IsNullOrEmpty(text) ? "?" : text;
        }
        private static void BuildItemAdvice(object actor)
        {
            try
            {
                object battleState = SimState.Battle;
                if (battleState != null)
                {
                    // Item value only changes with the battle state: cache per decision.
                    var keyBuilder = new System.Text.StringBuilder();
                    keyBuilder.Append(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(battleState))
                              .Append(':')
                              .Append(ToInt(GetMember(battleState, "CurrentRound"))).Append(':')
                              .Append(ToInt(GetMember(battleState, "CurrentTurn"))).Append(':')
                              .Append(ToUInt(GetMember(actor, "m_ActorGuid")));

                    object teamsField = GetMember(battleState, "m_BattleTeams");
                    var teamList = GetMember(teamsField, "m_Teams") as IEnumerable;
                    if (teamList != null)
                    {
                        foreach (object team in teamList)
                        {
                            var members = GetMember(team, "m_Actors") as IEnumerable;
                            if (members == null)
                            {
                                continue;
                            }

                            foreach (object member in members)
                            {
                                if (member == null)
                                {
                                    continue;
                                }

                                keyBuilder.Append('|')
                                          .Append(ToFloat(GetMember(member, "m_Hp")).ToString("0.#"))
                                          .Append(',')
                                          .Append(ToInt(GetMember(member, "m_TeamPosition")));
                            }
                        }
                    }

                    string adviceKey = keyBuilder.ToString();
                    if (adviceKey == _itemAdviceKey)
                    {
                        return;
                    }

                    _itemAdviceKey = adviceKey;
                }

                _itemLine = string.Empty;
                _itemTargetGuid = 0;
                _itemResult = null;
                _itemScore = 0f;

                object inventory = GetMember(actor, "m_CombatInventory");
                if (inventory == null)
                {
                    return;
                }

                var items = Invoke(inventory, "GetValidItems", null) as IEnumerable;
                if (items == null)
                {
                    return;
                }

                object battle = SimState.Battle;
                if (battle == null)
                {
                    return;
                }

                object teams = GetMember(battle, "m_BattleTeams");
                object turnOrder = GetMember(battle, "m_BattleTurnOrder");
                object tracking = GetMember(battle, "m_BattleTracking");
                if (teams == null || turnOrder == null)
                {
                    return;
                }

                if (_addPotentialMethod == null)
                {
                    _addPotentialMethod = FindAddPotentialMethod();
                }

                if (_addPotentialMethod == null)
                {
                    return;
                }

                object library = SingletonMonoBehaviour<Library<string, ActorDataSkill>>.Instance;
                uint actorGuid = ToUInt(GetMember(actor, "m_ActorGuid"));

                string bestLine = null;
                float bestScore = float.NegativeInfinity;

                JToken rng = RandomContainer.SaveToJson();
                try
                {
                    foreach (object item in items)
                    {
                        object definition = Invoke(item, "GetItemDefinition", null);
                        string id = definition == null ? null : GetMember(definition, "Id") as string;
                        if (string.IsNullOrEmpty(id))
                        {
                            continue;
                        }

                        object skill = Invoke(library, "GetLibraryElement", id);
                        if (skill == null)
                        {
                            continue;
                        }

                        bool friendly = ToBool(GetMember(skill, "m_IsFriendly"));
                        List<object> targets = TeamActors(friendly ? 0 : 1);

                        foreach (object target in targets)
                        {
                            uint targetGuid = ToUInt(GetMember(target, "m_ActorGuid"));
                            object temp = Activator.CreateInstance(
                                typeof(SkillCalculation), teams, turnOrder, tracking);
                            object input = new SkillCalculation.Input(actorGuid, id, targetGuid);
                            _addPotentialMethod.Invoke(temp, new object[] { input, new List<uint> { targetGuid } });

                            IDictionary results = GetMember(temp, "m_PotentialResults") as IDictionary;
                            if (results == null)
                            {
                                continue;
                            }

                            float score = 0f;
                            object chosenResult = null;
                            foreach (DictionaryEntry pair in results)
                            {
                                var actorResults = GetMember(pair.Value, "ActorResults") as IEnumerable;
                                if (actorResults == null)
                                {
                                    continue;
                                }

                                foreach (object actorResult in actorResults)
                                {
                                    if (ToUInt(GetMember(actorResult, "m_TargetActorGuid")) != targetGuid)
                                    {
                                        continue;
                                    }

                                    chosenResult = actorResult;

                                    float damage = ToFloat(GetMember(actorResult, "HealthDamage"));
                                    if (float.IsNaN(damage))
                                    {
                                        damage = 0f;
                                    }

                                    score += friendly ? -HpLossCost(target, damage) : HpLossCost(target, damage);

                                    int count;
                                    score += EffectValue(actor, actorResult, out count);
                                }
                            }

                            if (score > bestScore)
                            {
                                bestScore = score;
                                bestLine = SkillName(id) + " → " + ActorName(target);
                                _itemTargetGuid = targetGuid;
                                _itemResult = chosenResult;
                                _itemScore = score;
                            }
                        }
                    }
                }
                finally
                {
                    RandomContainer.LoadFromJson((JObject)rng);
                }

                if (bestLine != null)
                {
                    _itemLine = "免费物品：" + bestLine + "  [" + bestScore.ToString("F1") + "]";
                }
            }
            catch (Exception ex)
            {
                _itemLine = "物品读取失败：" + ex.GetType().Name;
            }
        }

        private static void Build()
        {
            Rows.Clear();
            UtilityCache.Clear();
            CoolingCache.Clear();

            object battle = GetMember(SingletonMonoBehaviour<CombatBhv>.Instance, "m_Battle");
            if (battle == null)
            {
                _header = "solver: no battle";
                return;
            }

            object teams = GetMember(battle, "m_BattleTeams");
            _teams = teams;
            LogParty(battle);
            uint actorGuid = ToUInt(GetMember(battle, "CurrentActorGuid"));
            object actor = actorGuid == 0 ? null : Invoke(teams, "GetActorFromActorGuid", actorGuid);
            if (actor == null)
            {
                _header = "solver: no current actor";
                return;
            }

            // Only the player's team gets suggestions; the enemies are not ours to drive.
            if (TeamIndexOf(actor) != 0)
            {
                Rows.Clear();
                _header = "敌方行动中 · 等待我方回合";
                return;
            }

            MarkDoomedEnemies(teams);

            object controller = GetMember(actor, "Controller") ?? GetMember(actor, "m_ActorController");
            var entries = Invoke(controller, "GetValidSkillTargetEntries", null) as IEnumerable;
            if (entries == null)
            {
                _header = "solver: waiting for turn start";
                return;
            }

            var results = GetMember(GetMember(battle, "m_BattleSkillCalculation"), "m_PotentialResults") as IDictionary;
            if (results == null)
            {
                _header = "solver: no potential results";
                return;
            }

            ResolveStats();

            var byGuid = new Dictionary<uint, object>();
            int plans = 0;

            foreach (object entry in entries)
            {
                string skillId = GetMember(entry, "m_SkillId") as string;
                var guids = GetMember(entry, "m_ValidTargetActorGuids") as IReadOnlyList<uint>;
                if (string.IsNullOrEmpty(skillId) || guids == null)
                {
                    continue;
                }

                var targetRows = new List<Row>();
                float totalDamage = 0f;
                object firstResult = null;

                for (int i = 0; i < guids.Count; i++)
                {
                    uint targetGuid = guids[i];
                    object target = Invoke(teams, "GetActorFromActorGuid", targetGuid);
                    if (target == null)
                    {
                        continue;
                    }

                    byGuid[targetGuid] = target;
                    plans++;

                    object input = new SkillCalculation.Input(actorGuid, skillId, targetGuid);
                    object result = results.Contains(input) ? results[input] : null;
                    if (result == null)
                    {
                        continue;
                    }

                    float damage = 0f;
                    bool crit = false;
                    bool hit = true;
                    bool found = false;
                    object matchedResult = null;

                    var actorResults = GetMember(result, "ActorResults") as IEnumerable;
                    if (actorResults != null)
                    {
                        foreach (object mapped in actorResults)
                        {
                            if (ToUInt(GetMember(mapped, "m_TargetActorGuid")) != targetGuid)
                            {
                                continue;
                            }

                            found = true;
                            matchedResult = mapped;
                            damage = ToFloat(GetMember(mapped, "HealthDamage"));
                            crit = ToBool(GetMember(mapped, "IsCrit"));
                            object hitValue = GetMember(mapped, "IsHit");
                            hit = hitValue == null || ToBool(hitValue);
                            break;
                        }
                    }

                    if (!found || float.IsNaN(damage))
                    {
                        damage = 0f;
                    }

                    if (firstResult == null)
                    {
                        firstResult = matchedResult;
                    }

                    totalDamage += damage;
                    targetRows.Add(Score(actor, target, skillId, targetGuid, damage, crit, hit, matchedResult));
                }

                // An AOE that always hits the whole team is the same action whichever slot you
                // click, so collapse its per-target rows into one.
                if (targetRows.Count > 1 && IsAoeEntry(results, actorGuid, skillId, guids))
                {
                    float merged = 0f;
                    for (int r = 0; r < targetRows.Count; r++)
                    {
                        merged += targetRows[r].Score;
                    }

                    string bestTarget = string.Empty;
                    float bestScore = float.MinValue;
                    for (int t = 0; t < targetRows.Count; t++)
                    {
                        if (targetRows[t].Score > bestScore)
                        {
                            bestScore = targetRows[t].Score;
                            bestTarget = targetRows[t].TargetName;
                        }
                    }

                    Rows.Add(new Row
                    {
                        Label = SkillName(skillId) + " → 敌方全体（" + targetRows.Count + " 目标）"
                                + (totalDamage > 0f ? "   伤害合计" + totalDamage.ToString("F0") : string.Empty)
                                + (string.IsNullOrEmpty(bestTarget) ? string.Empty : "   最大收益：" + bestTarget)
                                + EffectsText(firstResult),
                        Score = merged,
                        Tint = targetRows[0].Tint,
                        SkillId = skillId,
                        TargetGuid = 0,
                        Damage = 0f,
                        ActorResult = null,
                        Parts = targetRows
                    });
                }
                else
                {
                    Rows.AddRange(targetRows);
                }
            }

            AddPassSkillRows(actor);

            Rows.Sort((a, b) => b.Score.CompareTo(a.Score));
            ApplyLookahead(actor, ToInt(GetMember(battle, "CurrentRound")),
                           ToInt(GetMember(battle, "CurrentTurn")), actorGuid);
            Rows.Sort((a, b) => b.Score.CompareTo(a.Score));

            string name = ActorName(actor);
            _header = "求解器 · " + name
                      + " · 第" + ToInt(GetMember(battle, "CurrentRound")) + "回合"
                      + " 第" + (ToInt(GetMember(battle, "CurrentTurn")) + 1) + "手"
                      + " · " + plans + " 个选择 · 评分越高越好 · 按 F5 关闭";

            BuildItemAdvice(actor);
            LogDecision(battle, actor, actorGuid, plans);
        }

        private static string _logKey;

        /// <summary>Logs the ranked choices once per decision point, for later comparison.</summary>
        private static readonly string[] PassSkillIds = { "pass_heal", "pass_stress" };

        /// <summary>
        /// The always-equipped pass skills are part of every hero's kit: "pass" (self, +2 stress)
        /// is always legal, and pass_heal unlocks below 25% HP (heal 20% + 2 stress, cooldown 3).
        /// The controller may hide them, so add the legal ones explicitly.
        /// </summary>
        private static void AddPassSkillRows(object actor)
        {
            try
            {
                object battle = SimState.Battle;
                if (battle == null)
                {
                    return;
                }

                object teams = GetMember(battle, "m_BattleTeams");
                object turnOrder = GetMember(battle, "m_BattleTurnOrder");
                object tracking = GetMember(battle, "m_BattleTracking");
                if (teams == null || turnOrder == null)
                {
                    return;
                }

                if (_addPotentialMethod == null)
                {
                    _addPotentialMethod = FindAddPotentialMethod();
                }

                if (_addPotentialMethod == null)
                {
                    return;
                }

                uint selfGuid = ToUInt(GetMember(actor, "m_ActorGuid"));
                float maxHp = Mathf.Max(1f, Stat(actor, _healthMaxStat, "m_Hp"));
                float hp = ToFloat(GetMember(actor, "m_Hp"));
                float ratio = float.IsNaN(hp) ? 1f : hp / maxHp;
                Dictionary<string, int> cooling = CoolingMap(actor);

                foreach (string id in PassSkillIds)
                {
                    bool already = false;
                    for (int i = 0; i < Rows.Count; i++)
                    {
                        if (Rows[i].SkillId == id)
                        {
                            already = true;
                            break;
                        }
                    }

                    if (already)
                    {
                        continue;
                    }

                    if (id == "pass_heal" && ratio >= 0.25f)
                    {
                        continue;
                    }

                    int remaining;
                    if (id == "pass_heal" && cooling.TryGetValue(id, out remaining) && remaining > 0)
                    {
                        continue;
                    }

                    object temp = Activator.CreateInstance(typeof(SkillCalculation), teams, turnOrder, tracking);
                    object input = new SkillCalculation.Input(selfGuid, id, selfGuid);
                    _addPotentialMethod.Invoke(temp, new object[] { input, new List<uint> { selfGuid } });

                    IDictionary results = GetMember(temp, "m_PotentialResults") as IDictionary;
                    if (results == null)
                    {
                        continue;
                    }

                    foreach (DictionaryEntry pair in results)
                    {
                        var actorResults = GetMember(pair.Value, "ActorResults") as IEnumerable;
                        if (actorResults == null)
                        {
                            continue;
                        }

                        foreach (object actorResult in actorResults)
                        {
                            if (ToUInt(GetMember(actorResult, "m_TargetActorGuid")) != selfGuid)
                            {
                                continue;
                            }

                            float damage = ToFloat(GetMember(actorResult, "HealthDamage"));
                            if (float.IsNaN(damage))
                            {
                                damage = 0f;
                            }

                            Rows.Add(Score(actor, actor, id, selfGuid, damage, false, true, actorResult));
                        }
                    }
                }
            }
            catch
            {
            }
        }
        private static void LogDecision(object battle, object actor, uint actorGuid, int plans)
        {
            try
            {
                string key = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(battle) + ":"
                             + ToInt(GetMember(battle, "CurrentRound")) + ":"
                             + ToInt(GetMember(battle, "CurrentTurn")) + ":" + actorGuid;
                if (key == _logKey)
                {
                    return;
                }

                _logKey = key;

                Action<string> sink = RandomTrace.Sink;
                if (sink == null)
                {
                    return;
                }

                sink("S DECIDE round=" + ToInt(GetMember(battle, "CurrentRound"))
                     + " turn=" + ToInt(GetMember(battle, "CurrentTurn"))
                     + " actor=" + ActorName(actor)
                     + " choices=" + plans
                     + (_bossBattle ? " boss=1" : string.Empty)
                     + (string.IsNullOrEmpty(_itemLine) ? string.Empty : " item=" + _itemLine));

                for (int i = 0; i < Rows.Count && i < 3; i++)
                {
                    Row row = Rows[i];
                    sink("S TOP" + (i + 1) + " " + row.Label + " [" + row.Score.ToString("F1") + "]");
                }
            }
            catch
            {
            }
        }

        /// <summary>Called when the player actually executes a skill: log it next to the ranking.</summary>
        internal static void LogActual(string skillId, uint performerGuid)
        {
            try
            {
                Action<string> sink = RandomTrace.Sink;
                if (sink == null || string.IsNullOrEmpty(skillId))
                {
                    return;
                }

                int rank = -1;
                float score = 0f;
                for (int i = 0; i < Rows.Count; i++)
                {
                    if (Rows[i].SkillId == skillId)
                    {
                        rank = i + 1;
                        score = Rows[i].Score;
                        break;
                    }
                }

                sink("S ACTUAL skill=" + skillId
                     + " name=" + SkillName(skillId)
                     + " performer=" + performerGuid
                     + (rank > 0 ? " solverRank=" + rank + " solverScore=" + score.ToString("F1") : " solverRank=none"));
            }
            catch
            {
            }
        }

        private static Row Score(object performer, object target, string skillId, uint targetGuid,
                                float damage, bool crit, bool hit, object actorResult)
        {
            bool targetIsAlly = IsAlly(performer, target);

            // Already dead on its feet: its DOT kills it before it acts, so hitting it is wasted.
            if (!targetIsAlly && _doomed.Contains(targetGuid))
            {
                return new Row
                {
                    Label = SkillName(skillId) + " → " + ActorName(target) + "   它会被 DOT 打死，浪费",
                    Score = -1f,
                    Tint = new Color(0.55f, 0.55f, 0.55f),
                    SkillId = skillId,
                    TargetGuid = targetGuid,
                    Damage = 0f,
                    ActorResult = null
                };
            }

            float maxHp = Mathf.Max(1f, Stat(target, _healthMaxStat, "m_Hp"));
            float hp = ToFloat(GetMember(target, "m_Hp"));
            float after = Mathf.Max(0f, hp - damage);

            // Corpses cannot act, so spending an action on one is the worst option available.
            if (!targetIsAlly && (hp <= 0f || !ToBool(GetMember(target, "m_IsLiving"))))
            {
                return new Row
                {
                    Label = SkillName(skillId) + " → " + ActorName(target) + "   尸体，不构成威胁",
                    Score = -5f,
                    Tint = new Color(0.50f, 0.50f, 0.50f),
                    SkillId = skillId,
                    TargetGuid = targetGuid,
                    Damage = 0f,
                    ActorResult = null
                };
            }

            // Ally damage is a cost, enemy damage is a gain; a kill is worth much more.
            float sign = targetIsAlly ? -1f : 1f;
            float score = sign * HpLossCost(target, damage);
            string avoidedText = string.Empty;

            // Death's door armor absorbs a deathblow: reaching 0 HP only opens the door, and an
            // armored actor survives the finishing hit by burning one stack.
            bool atDeathsDoor = IsAtDeathsDoor(target);
            bool lethalHit = after <= 0f && damage > 0f;
            int deathArmor = TokenStacks(target, "deaths_door_armor");
            bool trueKill = actorResult != null && ToBool(GetMember(actorResult, "IsKill"));

            if (!trueKill && lethalHit)
            {
                if (atDeathsDoor && deathArmor > 0)
                {
                    score += 3f; // armor broken, target still alive
                    avoidedText += "  破甲" + deathArmor;
                }
                else if (atDeathsDoor)
                {
                    // survived the deathblow check
                }
                else
                {
                    // Entering death's door from above 0 HP: progress, not a kill.
                    score += targetIsAlly ? 3f : 2f;
                }
            }

            if (trueKill && targetIsAlly)
            {
                // Losing a hero is never an option: prune this line entirely.
                return new Row
                {
                    Label = SkillName(skillId) + " → " + ActorName(target) + "   会死人，不可选",
                    Score = -100000f,
                    Tint = new Color(0.75f, 0.30f, 0.30f),
                    SkillId = skillId,
                    TargetGuid = targetGuid,
                    Damage = 0f,
                    ActorResult = null,
                    TargetName = ActorName(target)
                };
            }

            if (trueKill)
            {
                score += 30f;

                // Killing the enemy that was about to strike prevents its predicted damage.
                if (!targetIsAlly)
                {
                    float avoided;
                    uint victimGuid;
                    if (ForeseekerPlugin.PredictedDamage.TryGetValue(targetGuid, out avoided)
                        && ForeseekerPlugin.PredictedTarget.TryGetValue(targetGuid, out victimGuid)
                        && avoided > 0f)
                    {
                        object victim = Invoke(_teams, "GetActorFromActorGuid", victimGuid);
                        float victimMax = Mathf.Max(1f, Stat(victim, _healthMaxStat, "m_Hp"));
                        score += HpLossCost(victim, avoided);
                        avoidedText = "  少挨" + avoided.ToString("F0");
                    }
                }
            }

            int effects;
            score += EffectValue(performer, actorResult, out effects);

            // Position matters: a move skill walks to the ally it targets, and some skills carry
            // their own movement effect. Value the rank change by how many skills work there.
            float positionDelta = PositionDelta(performer, target, actorResult, targetGuid);
            score += positionDelta * 0.7f;
            string positionText = Mathf.Abs(positionDelta) > 0.01f
                ? "  位置" + (positionDelta > 0f ? "+" : string.Empty) + positionDelta.ToString("F1")
                : string.Empty;

            string label = SkillName(skillId) + " → " + ActorName(target)
                           + (damage > 0f ? "   伤害" + damage.ToString("F0") : string.Empty)
                           + (crit ? " 暴击" : string.Empty)
                           + (hit ? string.Empty : " 未命中")
                           + EffectsText(actorResult)
                           + positionText
                           + avoidedText;

            Color tint = targetIsAlly ? (after <= 0f ? new Color(1f, 0.4f, 0.4f) : new Color(0.8f, 0.9f, 1f))
                                      : new Color(1f, 0.95f, 0.8f);

            return new Row
            {
                Label = label,
                Score = score,
                Tint = tint,
                SkillId = skillId,
                TargetGuid = targetGuid,
                Damage = damage,
                ActorResult = actorResult,
                TargetName = ActorName(target)
            };
        }

        /// <summary>
        /// Folds the effects this result would apply into the score:
        /// tokens are valued by their own positive/negative tag, DOTs are always bad for the
        /// holder, heals are good for the holder — then flipped for the opposing team.
        /// </summary>
        /// <summary>True when the skill's pre-rolled result already covers every valid target.</summary>
        private static bool IsAoeEntry(IDictionary results, uint actorGuid, string skillId, IReadOnlyList<uint> guids)
        {
            if (guids == null || guids.Count < 2)
            {
                return false;
            }

            object input = new SkillCalculation.Input(actorGuid, skillId, guids[0]);
            if (!results.Contains(input))
            {
                return false;
            }

            var actorResults = GetMember(results[input], "ActorResults") as IEnumerable;
            if (actorResults == null)
            {
                return false;
            }

            var covered = new HashSet<uint>();
            foreach (object mapped in actorResults)
            {
                covered.Add(ToUInt(GetMember(mapped, "m_TargetActorGuid")));
            }

            for (int i = 0; i < guids.Count; i++)
            {
                if (!covered.Contains(guids[i]))
                {
                    return false;
                }
            }

            return true;
        }

        private static void MarkDoomedEnemies(object teams)
        {
            _doomed.Clear();

            var teamList = GetMember(teams, "m_Teams") as IEnumerable;
            if (teamList == null)
            {
                return;
            }

            float hpSum = 0f;
            int hpCount = 0;

            foreach (object team in teamList)
            {
                if (ToInt(GetMember(team, "m_TeamIndex")) == 0)
                {
                    continue;
                }

                var actors = GetMember(team, "m_Actors") as IEnumerable;
                if (actors == null)
                {
                    continue;
                }

                foreach (object actor in actors)
                {
                    if (actor == null || !ToBool(GetMember(actor, "m_IsLiving")))
                    {
                        continue;
                    }

                    float hp = ToFloat(GetMember(actor, "m_Hp"));
                    hpSum += Mathf.Max(1f, Stat(actor, _healthMaxStat, "m_Hp"));
                    hpCount++;

                    float dot = PendingDotDamage(actor);
                    if (hp > 0f && dot > 0f && hp <= dot)
                    {
                        _doomed.Add(ToUInt(GetMember(actor, "m_ActorGuid")));
                    }
                }
            }

            if (hpCount > 0)
            {
                _avgEnemyMaxHp = Mathf.Max(1f, hpSum / hpCount);
            }
        }

        /// <summary>Damage the actor takes from its own DOTs when its next turn starts.</summary>
        private static float PendingDotDamage(object actor)
        {
            object container = GetMember(actor, "m_DotContainer");
            if (container == null)
            {
                return 0f;
            }

            int count = ToInt(Invoke(container, "GetNumberOfInstances", null));
            float total = 0f;

            for (int i = 0; i < count; i++)
            {
                object instance = Invoke(container, "GetInstanceAtIndex", i);
                if (instance == null)
                {
                    continue;
                }

                float amount = ToFloat(GetMember(instance, "m_EffectValueChange"));
                if (float.IsNaN(amount) || amount <= 0f)
                {
                    amount = 1f;
                }

                total += amount;
            }

            return total;
        }

        /// <summary>
        /// Cheap mitigation model for the incoming hit: dodge negates it, block halves it,
        /// vulnerable amplifies it. This is what makes self-buff skills worth something.
        /// </summary>
        private static float Mitigate(object actor, float damage)
        {
            if (actor == null || damage <= 0f)
            {
                return damage;
            }

            float factor = DodgeFactor(actor) * BlockFactor(actor);
            if (HasToken(actor, "vulnerable"))
            {
                factor *= 1.5f;
            }

            return damage * factor;
        }

        /// <summary>Chance that an attack is avoided: dodge 50%, dodge+ 75%. Stacks do not compound.</summary>
        private static float DodgeFactor(object actor)
        {
            // The game uses the chance baked into the first EVADE-type token (TokenDefinition.m_Chance),
            // so dodge and dodge+ never stack: whichever token gets consumed first sets the odds.
            float chance = FirstTokenChance(actor, "EVADE");
            if (chance > 0f && chance <= 1f)
            {
                return 1f - chance;
            }

            if (HasToken(actor, "dodge_plus"))
            {
                return 0.25f;
            }

            return HasToken(actor, "dodge") ? 0.5f : 1f;
        }

        private static object _evadeTokenType;
        private static bool _evadeTypeResolved;

        private static object EvadeTokenType()
        {
            if (_evadeTypeResolved)
            {
                return _evadeTokenType;
            }

            _evadeTypeResolved = true;
            try
            {
                Type tokenType = typeof(TokenDefinition).Assembly.GetType("Assets.Code.Token.TokenType");
                FieldInfo field = tokenType == null
                    ? null
                    : tokenType.GetField("EVADE", BindingFlags.Public | BindingFlags.Static);
                _evadeTokenType = field == null ? null : field.GetValue(null);
            }
            catch
            {
                _evadeTokenType = null;
            }

            return _evadeTokenType;
        }

        private static float FirstTokenChance(object actor, string tokenTypeName)
        {
            object tokenType = EvadeTokenType();
            if (tokenType == null || actor == null)
            {
                return 0f;
            }

            object container = GetMember(actor, "m_TokenContainer");
            if (container == null)
            {
                return 0f;
            }

            int count = ToInt(Invoke(container, "GetNumberOfInstances", null));
            for (int i = 0; i < count; i++)
            {
                object instance = Invoke(container, "GetInstanceAtIndex", i);
                object definition = instance == null ? null : GetMember(instance, "Definition");
                if (definition == null)
                {
                    continue;
                }

                object hasType = Invoke(definition, "GetHasType", tokenType);
                if (hasType != null && ToBool(hasType))
                {
                    return ToFloat(GetMember(definition, "m_Chance"));
                }
            }

            return 0f;
        }

        /// <summary>Damage multiplier for block: 50% / 75% reduction.</summary>
        private static float BlockFactor(object actor)
        {
            if (HasToken(actor, "block_plus"))
            {
                return 0.25f;
            }

            return HasToken(actor, "block") ? 0.5f : 1f;
        }

        private static bool HasToken(object actor, string tokenId)
        {
            if (actor == null)
            {
                return false;
            }

            object container = GetMember(actor, "m_TokenContainer");
            if (container == null)
            {
                return false;
            }

            int count = ToInt(Invoke(container, "GetNumberOfInstances", null));
            for (int i = 0; i < count; i++)
            {
                object instance = Invoke(container, "GetInstanceAtIndex", i);
                object definition = instance == null ? null : GetMember(instance, "Definition");
                string id = definition == null ? null : GetMember(definition, "m_Id") as string;
                if (string.Equals(id, tokenId, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        private static string SkillName(string skillId)
        {
            string name = SaveReader.SkillName(skillId);
            return string.IsNullOrEmpty(name) ? ShortSkill(skillId) : name;
        }

        private static string ActorName(object actor)
        {
            if (actor == null)
            {
                return "?";
            }

            uint guid = ToUInt(GetMember(actor, "m_ActorGuid"));
            string custom = GetMember(actor, "m_ActorName") as string;

            object classData = GetMember(actor, "m_ActorDataClass");
            string classId = GetMember(classData, "Id") as string;
            if (string.IsNullOrEmpty(classId))
            {
                classId = GetMember(classData, "m_Id") as string;
            }

            string className = string.IsNullOrEmpty(classId) ? null : SaveReader.ClassName(classId);

            int teamIndex = TeamIndexOf(actor);
            int position = ToInt(GetMember(actor, "m_TeamPosition")) + 1;

            if (teamIndex != 0)
            {
                // Enemies share class names, so tag them with their 1-based rank.
                string enemyName = string.IsNullOrEmpty(className)
                    ? (string.IsNullOrEmpty(custom) ? "#" + guid : custom)
                    : className;
                return enemyName + "（" + position + "）";
            }

            // Our heroes are shown by class only; custom names would just add noise.
            if (!string.IsNullOrEmpty(className))
            {
                return className;
            }

            return string.IsNullOrEmpty(custom) ? "#" + guid : custom;
        }

        private static string EffectsText(object actorResult)
        {
            if (actorResult == null)
            {
                return string.Empty;
            }

            object container = GetMember(actorResult, "m_AppliedEffectsOutputContainer");
            var outputs = GetMember(container, "Outputs") as IEnumerable;
            if (outputs == null)
            {
                return string.Empty;
            }

            var parts = new List<string>();

            foreach (object output in outputs)
            {
                var instances = GetMember(output, "EffectInstancesToApply") as IEnumerable;
                if (instances == null)
                {
                    continue;
                }

                object holder = GetMember(output, "m_TargetActor");
                foreach (object instance in instances)
                {
                    object definition = GetMember(instance, "EffectDefinition");
                    if (definition == null)
                    {
                        continue;
                    }

                    string tokenId = GetMember(definition, "m_TokenAddId") as string;
                    if (!string.IsNullOrEmpty(tokenId))
                    {
                        int amount = Math.Max(1, ToInt(GetMember(instance, "TokenAddAmount")));
                        string tokenName = SaveReader.TokenName(tokenId);
                        parts.Add(amount > 1
                            ? (string.IsNullOrEmpty(tokenName) ? tokenId : tokenName) + "×" + amount
                            : (string.IsNullOrEmpty(tokenName) ? tokenId : tokenName));
                        continue;
                    }

                    string dotId = GetMember(definition, "m_DotAddId") as string;
                    if (!string.IsNullOrEmpty(dotId))
                    {
                        int amount = Math.Max(1, ToInt(GetMember(instance, "DotAddAmount")));
                        string dot = SaveReader.DescribeDotApplication(dotId, amount);
                        parts.Add(string.IsNullOrEmpty(dot) ? dotId : dot);
                        continue;
                    }

                    float selfDamage = ToFloat(GetMember(instance, "HealthDamageAmount"));
                    float selfPercent = ToFloat(GetMember(instance, "HealthDamagePercent"));
                    if (float.IsNaN(selfDamage))
                    {
                        selfDamage = 0f;
                    }

                    if (float.IsNaN(selfPercent))
                    {
                        selfPercent = 0f;
                    }

                    if (selfPercent > 0f)
                    {
                        selfDamage += selfPercent * Mathf.Max(1f, Stat(holder, _healthMaxStat, "m_Hp"));
                    }

                    if (selfDamage > 0f)
                    {
                        parts.Add("自伤" + selfDamage.ToString("0.#"));
                        continue;
                    }

                    float stressDamage = ToFloat(GetMember(instance, "StressDamage"));
                    float stressHeal = ToFloat(GetMember(instance, "StressHeal"));
                    if (stressDamage > 0f)
                    {
                        parts.Add("压力+" + stressDamage.ToString("0.#"));
                    }
                    else if (stressHeal > 0f)
                    {
                        parts.Add("减压" + stressHeal.ToString("0.#"));
                    }

                    float heal = ToFloat(GetMember(instance, "HealthHealAmount"));
                    if (heal > 0f)
                    {
                        parts.Add("治疗" + heal.ToString("0.#"));
                    }
                }
            }

            if (parts.Count == 0)
            {
                return string.Empty;
            }

            string text = "  ";
            int shown = Math.Min(parts.Count, 3);
            for (int i = 0; i < shown; i++)
            {
                if (i > 0)
                {
                    text += "、";
                }

                text += parts[i];
            }

            return parts.Count > shown ? text + "…" : text;
        }

        private static float EffectValue(object performer, object actorResult, out int count)
        {
            count = 0;
            if (actorResult == null)
            {
                return 0f;
            }

            float total = 0f;

            object container = GetMember(actorResult, "m_AppliedEffectsOutputContainer");
            var outputs = GetMember(container, "Outputs") as IEnumerable;
            if (outputs == null)
            {
                return 0f;
            }

            // Damage tokens multiply: 10 damage with strength + crit on a vulnerable target is
            // 10 * 1.5 * 1.5 * 1.5, not 10 * (1 + 0.5 + 0.5 + 0.5). Value them as one bundle.
            int grantedStrength = 0;
            int grantedCrit = 0;
            int grantedVulnerable = 0;
            object vulnerableHolder = null;

            foreach (object output in outputs)
            {
                object scanHolder = GetMember(output, "m_TargetActor");
                if (scanHolder == null)
                {
                    continue;
                }

                bool holderIsAlly = IsAlly(performer, scanHolder);
                var scanInstances = GetMember(output, "EffectInstancesToApply") as IEnumerable;
                if (scanInstances == null)
                {
                    continue;
                }

                foreach (object instance in scanInstances)
                {
                    object definition = GetMember(instance, "EffectDefinition");
                    string tokenId = definition == null ? null : GetMember(definition, "m_TokenAddId") as string;
                    if (string.IsNullOrEmpty(tokenId))
                    {
                        continue;
                    }

                    if (tokenId == "strength")
                    {
                        grantedStrength += holderIsAlly ? 1 : -1;
                    }
                    else if (tokenId == "crit")
                    {
                        grantedCrit += holderIsAlly ? 1 : -1;
                    }
                    else if (tokenId == "vulnerable")
                    {
                        if (holderIsAlly)
                        {
                            grantedVulnerable -= 1;
                        }
                        else
                        {
                            grantedVulnerable += 1;
                            vulnerableHolder = scanHolder;
                        }
                    }
                }
            }

            if (grantedStrength != 0 || grantedCrit != 0 || grantedVulnerable != 0)
            {
                float hit = ExpectedHit(performer);
                if (hit > 0f)
                {
                    // All "+X% damage" sources live in ONE additive bucket: skill bonuses,
                    // strength tokens and trinkets add up (6 * (1 + 1.0 + 0.5 + 0.5) = 18).
                    // Crit and "received damage" stay multiplicative.
                    float dealt = BucketOf(performer, _dealtStat);
                    float received = BucketOf(vulnerableHolder, _receivedStat);

                    bool hadCrit = HasToken(performer, "crit");
                    float critBefore = hadCrit ? 1.5f : 1f;
                    float critAfter = (hadCrit || grantedCrit > 0) ? 1.5f : 1f;

                    float before = hit * dealt * critBefore * received;
                    float after = hit
                                  * Mathf.Max(0f, dealt + 0.5f * grantedStrength)
                                  * critAfter
                                  * Mathf.Max(0f, received + 0.5f * grantedVulnerable);

                    float gain = after - before;
                    total += 5f * gain / Mathf.Max(1f, _avgEnemyMaxHp);
                }
            }

            foreach (object output in outputs)
            {
                object holder = GetMember(output, "m_TargetActor");
                if (holder == null)
                {
                    continue;
                }

                // +1 means "good for the holder is good for us" (ally); -1 for enemies.
                float holderSign = IsAlly(performer, holder) ? 1f : -1f;

                var instances = GetMember(output, "EffectInstancesToApply") as IEnumerable;
                if (instances == null)
                {
                    continue;
                }

                foreach (object instance in instances)
                {
                    object definition = GetMember(instance, "EffectDefinition");
                    if (definition == null)
                    {
                        continue;
                    }

                    count++;

                    string tokenId = GetMember(definition, "m_TokenAddId") as string;
                    if (!string.IsNullOrEmpty(tokenId))
                    {
                        int amount = Math.Max(1, ToInt(GetMember(instance, "TokenAddAmount")));

                        // Stack caps: a token beyond m_Limit is wasted, so only value the room left.
                        int limit = TokenLimit(tokenId);
                        if (limit > 0)
                        {
                            int existing = TokenStacks(holder, tokenId);
                            amount = Mathf.Min(amount, Mathf.Max(0, limit - existing));
                            if (amount <= 0)
                            {
                                continue;
                            }
                        }

                        int polarity = TokenPolarity(tokenId);
                        float weight = TokenWeight(tokenId);

                        // strength / crit / vulnerable are valued together in the multiplicative
                        // pass above; counting them again here would double-dip.
                        if (tokenId == "strength" || tokenId == "crit" || tokenId == "vulnerable")
                        {
                            continue;
                        }

                        // Conviction is a pure resource: its payoff shows up in the skill that
                        // consumes it (already reflected in that skill's pre-rolled damage), so the
                        // token itself is worth nothing here.
                        if (tokenId.StartsWith("conviction", StringComparison.Ordinal))
                        {
                            continue;
                        }

                        // Taunt / guard are threat transfers; riposte pays off when the holder is
                        // the one being attacked. Value them against the predicted incoming hit.
                        if (tokenId == "taunt" || tokenId == "guard" || tokenId == "riposte")
                        {
                            float threat = ThreatTokenValue(tokenId, performer, holder, instance);
                            total += tokenId == "riposte" ? threat * amount : threat;
                            continue;
                        }

                        if (tokenId == "weak")
                        {
                            float predicted = PredictedDamageOf(holder);
                            if (predicted > 0f)
                            {
                                float gain = 0.5f * predicted * amount;
                                total += 5f * gain / PredictedVictimMaxHp(holder) * DodgeFactor(holder);
                                continue;
                            }
                        }

                        if (polarity > 0)
                        {
                            // A defensive buff on a healthy hero is worth much less than one
                            // that actually patches a wound.
                            float scale = 1f;
                            if (IsAlly(performer, holder))
                            {
                                float holderMax = Mathf.Max(1f, Stat(holder, _healthMaxStat, "m_Hp"));
                                float holderHp = ToFloat(GetMember(holder, "m_Hp"));
                                scale = 0.5f + 0.5f * Mathf.Clamp01((holderMax - holderHp) / holderMax);
                            }

                            total += holderSign * weight * amount * scale;
                        }
                        else if (polarity < 0)
                        {
                            // A dodged attack never applies its debuff, so scale by the chance
                            // the effect actually lands.
                            float land = holderSign < 0f ? DodgeFactor(holder) : 1f;
                            total -= holderSign * weight * amount * land;
                        }
                        else if (holderSign < 0f)
                        {
                            // Untagged token on an enemy: most of these are debuffs (e.g. 破绽),
                            // so give it a small offensive value and note the id once.
                            total += 0.6f * amount;
                            LogUnknownToken(tokenId);
                        }

                        continue;
                    }

                    string dotId = GetMember(definition, "m_DotAddId") as string;
                    if (!string.IsNullOrEmpty(dotId))
                    {
                        int amount = Math.Max(1, ToInt(GetMember(instance, "DotAddAmount")));
                        float land = holderSign < 0f ? DodgeFactor(holder) : 1f;
                        total -= holderSign * 1.0f * amount * land;
                        continue;
                    }

                    // Some effects hurt the holder directly (e.g. the Flagellant's self-damage);
                    // that is a real cost even though the skill itself deals no damage.
                    float effectDamage = ToFloat(GetMember(instance, "HealthDamageAmount"));
                    float effectPercent = ToFloat(GetMember(instance, "HealthDamagePercent"));
                    if (float.IsNaN(effectDamage))
                    {
                        effectDamage = 0f;
                    }

                    if (float.IsNaN(effectPercent))
                    {
                        effectPercent = 0f;
                    }

                    if (effectPercent > 0f)
                    {
                        effectDamage += effectPercent * Mathf.Max(1f, Stat(holder, _healthMaxStat, "m_Hp"));
                    }

                    if (effectDamage > 0f)
                    {
                        float holderMaxHp = Mathf.Max(1f, Stat(holder, _healthMaxStat, "m_Hp"));
                        float land = holderSign < 0f ? DodgeFactor(holder) : 1f;
                        total -= holderSign * HpLossCost(holder, effectDamage) * land;
                        continue;
                    }

                    float stressDamage = ToFloat(GetMember(instance, "StressDamage"));
                    float stressHeal = ToFloat(GetMember(instance, "StressHeal"));
                    if (stressDamage > 0f)
                    {
                        total -= holderSign * StressCost(holder, stressDamage);
                        continue;
                    }

                    if (stressHeal > 0f)
                    {
                        total -= holderSign * StressCost(holder, -stressHeal);
                        continue;
                    }

                    float heal = ToFloat(GetMember(instance, "HealthHealAmount"));
                    if (heal > 0f)
                    {
                        float maxHp = Mathf.Max(1f, Stat(holder, _healthMaxStat, "m_Hp"));
                        float hp = ToFloat(GetMember(holder, "m_Hp"));
                        float effective = Mathf.Max(0f, Mathf.Min(heal, maxHp - hp));
                        total += holderSign * 5f * (effective / maxHp) * HpRiskFactor(holder);
                    }
                }
            }

            // Keep a stack of small buffs from out-scoring a real hit.
            if (total > 3f)
            {
                total = 3f + (total - 3f) * 0.3f;
            }
            else if (total < -3f)
            {
                total = -3f + (total + 3f) * 0.3f;
            }

            return total;
        }

        /// <summary>
        /// How expensive one point of damage is for this actor right now. Damage is flat while
        /// health pools differ, so the same hit is cheaper on a 50 HP hero than on a 28 HP one;
        /// but once the bar is nearly empty, absolute distance to death matters more.
        /// Piecewise: 100% -> x0.5, 60% -> x0.8, 30% -> x1.6, 10% -> x2.6.
        /// </summary>
        private static float HpRiskFactor(object actor)
        {
            if (actor == null)
            {
                return 1f;
            }

            float max = Mathf.Max(1f, Stat(actor, _healthMaxStat, "m_Hp"));
            float hp = ToFloat(GetMember(actor, "m_Hp"));
            if (float.IsNaN(hp))
            {
                hp = max;
            }

            float r = Mathf.Clamp01(hp / max);

            if (r >= 0.6f)
            {
                return Mathf.Lerp(0.8f, 0.5f, (r - 0.6f) / 0.4f);
            }

            if (r >= 0.3f)
            {
                return Mathf.Lerp(1.6f, 0.8f, (r - 0.3f) / 0.3f);
            }

            if (r >= 0.1f)
            {
                return Mathf.Lerp(2.6f, 1.6f, (r - 0.1f) / 0.2f);
            }

            return 2.6f + (0.1f - r) * 5f;
        }

        private static float HpLossCost(object actor, float damage)
        {
            if (actor == null || damage <= 0f)
            {
                return 0f;
            }

            float max = Mathf.Max(1f, Stat(actor, _healthMaxStat, "m_Hp"));
            float hp = ToFloat(GetMember(actor, "m_Hp"));
            if (float.IsNaN(hp))
            {
                hp = max;
            }

            float after = Mathf.Max(0f, hp - damage);

            // Health is integer and damage is flat, so "how many more hits can this take" is
            // absolute, not a percentage: 5 HP survives a 4 hit, 3 HP does not.
            float hitsLeft = after / damage;
            float bufferRisk = 1f + Mathf.Clamp01(1f - hitsLeft / 2f);

            float cost = 5f * (damage / max) * HpRiskFactor(actor) * bufferRisk;

            if (IsAtDeathsDoor(actor))
            {
                // A hit here rolls a deathblow check: weigh it by the chance the check fails.
                cost += 20f * DeathBlowChance(actor);
            }
            else if (hp - damage <= 0f)
            {
                cost += 3f; // entering death's door
            }

            return cost;
        }

        /// <summary>How many stacks of a token the actor currently holds.</summary>
        /// <summary>Stack cap from the token definition (m_Limit); 0 means unlimited.</summary>
        private static int TokenLimit(string tokenId)
        {
            if (string.IsNullOrEmpty(tokenId))
            {
                return 0;
            }

            try
            {
                object library = SingletonMonoBehaviour<Library<string, TokenDefinition>>.Instance;
                object definition = Invoke(library, "GetLibraryElement", tokenId);
                if (definition == null)
                {
                    return 0;
                }

                int limit = ToInt(GetMember(definition, "m_Limit"));
                return limit < 0 ? 0 : limit;
            }
            catch
            {
                return 0;
            }
        }
        private static int TokenStacks(object actor, string tokenId)
        {
            if (actor == null || string.IsNullOrEmpty(tokenId))
            {
                return 0;
            }

            try
            {
                object container = GetMember(actor, "m_TokenContainer");
                if (container == null)
                {
                    return 0;
                }

                int count = ToInt(Invoke(container, "GetNumberOfInstances", null));
                int total = 0;
                for (int i = 0; i < count; i++)
                {
                    object instance = Invoke(container, "GetInstanceAtIndex", i);
                    if (instance == null)
                    {
                        continue;
                    }

                    object definition = GetMember(instance, "TokenDefinition");
                    string id = definition == null ? null : GetMember(definition, "Id") as string;
                    if (id == tokenId)
                    {
                        total += Mathf.Max(1, ToInt(GetMember(instance, "m_Amount")));
                    }
                }

                return total;
            }
            catch
            {
                return 0;
            }
        }
        /// <summary>
        /// Chance that a hit on a deaths-door actor finishes it. Monsters carry no death
        /// resistance (0), heroes sit around 0.6 and paths/buffs can shift it.
        /// </summary>
        private static float DeathBlowChance(object actor)
        {
            float resist;
            try
            {
                object value = _deathResistStat == null
                    ? null
                    : Invoke(actor, "GetClampedStatValue", _deathResistStat);
                resist = value == null ? 0.6f : ToFloat(value);
                if (float.IsNaN(resist))
                {
                    resist = 0.6f;
                }
            }
            catch
            {
                resist = 0.6f;
            }

            return Mathf.Clamp01(1f - resist);
        }
        private static bool IsAtDeathsDoor(object actor)
        {
            if (actor == null)
            {
                return false;
            }

            var statuses = GetMember(actor, "m_Statuses") as Array;
            if (statuses != null && statuses.Length > 0 && ToBool(statuses.GetValue(0)))
            {
                return true;
            }

            float hp = ToFloat(GetMember(actor, "m_Hp"));
            return !float.IsNaN(hp) && hp <= 0f;
        }

        /// <summary>
        /// Stress is scored per point (heavier past 8). Crossing the maximum triggers the actor's
        /// own overstress roll — which uses the STRESS stream, so we replay that draw exactly.
        /// </summary>
        private static float StressCost(object actor, float delta)
        {
            if (actor == null || Mathf.Abs(delta) < 0.01f)
            {
                return 0f;
            }

            float max = Mathf.Max(1f, Stat(actor, _stressMaxStat, "m_Stress"));
            float current = ToFloat(GetMember(actor, "m_Stress"));
            if (float.IsNaN(current))
            {
                current = 0f;
            }

            current = Mathf.Clamp(current, 0f, max);

            float cost = delta * (current >= 8f ? 1.5f : 1f);

            if (delta > 0f && current + delta >= max - 0.001f)
            {
                object outcome = PredictOverstress(actor);
                cost += outcome == null ? 3f : OverstressCost(actor, outcome);
            }

            return cost;
        }

        /// <summary>Replays the WeightedBag draw on the STRESS stream (same walk as the game).</summary>
        private static object PredictOverstress(object actor)
        {
            try
            {
                object classData = GetMember(actor, "m_ActorDataClass");
                var list = GetMember(classData, "m_Overstresses") as IEnumerable;
                if (list == null)
                {
                    return null;
                }

                var definitions = new List<object>();
                float total = 0f;
                foreach (object definition in list)
                {
                    float chance = ToFloat(GetMember(definition, "m_Chance"));
                    if (float.IsNaN(chance) || chance <= 0f)
                    {
                        continue;
                    }

                    definitions.Add(definition);
                    total += chance;
                }

                if (definitions.Count == 0 || total <= 0f)
                {
                    return null;
                }

                float roll = ForeseekerPlugin.PeekStreamValue(RandomIdentifier.STRESS);
                if (float.IsNaN(roll) || roll < 0f || roll >= 1f)
                {
                    return null;
                }

                float remaining = roll * total;
                for (int i = 0; i < definitions.Count; i++)
                {
                    remaining -= ToFloat(GetMember(definitions[i], "m_Chance"));
                    if (remaining < 0f)
                    {
                        return definitions[i];
                    }
                }

                return definitions[definitions.Count - 1];
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Cost of one overstress outcome: HP forced down (meltdown/toxic) or healed (resolute).</summary>
        private static float OverstressCost(object actor, object outcome)
        {
            string id = GetMember(outcome, "m_Id") as string;
            float max = Mathf.Max(1f, Stat(actor, _healthMaxStat, "m_Hp"));
            float hp = ToFloat(GetMember(actor, "m_Hp"));
            if (float.IsNaN(hp))
            {
                hp = max;
            }

            // Relationships are a 0-20 resource; scale the change on the same 5-point bar as HP.
            float leaning = ToFloat(GetMember(outcome, "m_LeaningChange"));
            if (float.IsNaN(leaning))
            {
                leaning = 0f;
            }

            float affinity = -leaning * 5f / 20f;

            if (id == "meltdown")
            {
                return HpLossCost(actor, Mathf.Max(0f, hp - max * 0.1f)) + 2f + affinity;
            }

            if (id == "resolute")
            {
                float heal = Mathf.Max(0f, Mathf.Min(max - hp, max * 0.67f));
                return -5f * (heal / max) - 1f + affinity;
            }

            if (id == "toxic")
            {
                return HpLossCost(actor, Mathf.Max(0f, hp - max * 0.3f)) + 1.5f + affinity;
            }

            return 3f + affinity;
        }

        /// <summary>
        /// How much better or worse the mover's rank becomes: ally-targeted skills walk to that
        /// ally's rank, and some skills carry their own Move effect on the performer.
        /// </summary>
        private static float PositionDelta(object performer, object target, object actorResult, uint targetGuid)
        {
            try
            {
                uint performerGuid = ToUInt(GetMember(performer, "m_ActorGuid"));
                int current = ToInt(GetMember(performer, "m_TeamPosition"));
                if (current < 0)
                {
                    return 0f;
                }

                int destination = current;

                bool targetIsAlly = target != null && IsAlly(performer, target) && targetGuid != performerGuid;
                if (targetIsAlly)
                {
                    destination = ToInt(GetMember(target, "m_TeamPosition"));
                }
                else
                {
                    int move = MoveEffectsOn(actorResult, performerGuid);
                    if (move != 0)
                    {
                        destination = current + move;
                    }
                }

                destination = Mathf.Clamp(destination, 0, 3);
                if (destination == current)
                {
                    return 0f;
                }

                return UtilityAt(performer, destination) - UtilityAt(performer, current);
            }
            catch
            {
                return 0f;
            }
        }

        private static int MoveEffectsOn(object actorResult, uint actorGuid)
        {
            object container = GetMember(actorResult, "m_AppliedEffectsOutputContainer");
            var outputs = GetMember(container, "Outputs") as IEnumerable;
            if (outputs == null)
            {
                return 0;
            }

            int move = 0;
            foreach (object output in outputs)
            {
                object holder = GetMember(output, "m_TargetActor");
                if (holder == null || ToUInt(GetMember(holder, "m_ActorGuid")) != actorGuid)
                {
                    continue;
                }

                var instances = GetMember(output, "EffectInstancesToApply") as IEnumerable;
                if (instances == null)
                {
                    continue;
                }

                foreach (object instance in instances)
                {
                    move += ToInt(GetMember(instance, "Move"));
                }
            }

            return move;
        }

        private static readonly Dictionary<string, float> UtilityCache = new Dictionary<string, float>();
        private static readonly Dictionary<uint, Dictionary<string, int>> CoolingCache =
            new Dictionary<uint, Dictionary<string, int>>();

        /// <summary>
        /// Cooldown id -> remaining turns. A skill that comes back next turn still matters:
        /// moving to a rank this turn prepares it for the next one.
        /// </summary>
        private static Dictionary<string, int> CoolingMap(object actor)
        {
            uint guid = ToUInt(GetMember(actor, "m_ActorGuid"));
            Dictionary<string, int> map;
            if (CoolingCache.TryGetValue(guid, out map))
            {
                return map;
            }

            map = new Dictionary<string, int>();
            try
            {
                var list = GetMember(actor, "m_SkillCooldowns") as IEnumerable;
                if (list != null)
                {
                    foreach (object instance in list)
                    {
                        if (instance == null)
                        {
                            continue;
                        }

                        object remaining = Invoke(instance, "GetCooldownDurationAmountRemaining", null);
                        int turns = remaining == null ? 0 : ToInt(remaining);
                        if (turns <= 0)
                        {
                            continue;
                        }

                        string id = GetMember(instance, "m_SkillCooldownId") as string;
                        if (!string.IsNullOrEmpty(id))
                        {
                            map[id] = turns;
                        }
                    }
                }
            }
            catch
            {
            }

            CoolingCache[guid] = map;
            return map;
        }

        /// <summary>How many of the actor's equipped skills can be launched from this rank.</summary>
        private static float UtilityAt(object actor, int rank)
        {
            uint guid = ToUInt(GetMember(actor, "m_ActorGuid"));
            string key = guid + ":" + rank;
            float cached;
            if (UtilityCache.TryGetValue(key, out cached))
            {
                return cached;
            }

            float total = 0f;
            try
            {
                Dictionary<string, int> cooling = CoolingMap(actor);
                object library = SingletonMonoBehaviour<Library<string, ActorDataSkill>>.Instance;
                var skills = GetMember(actor, "m_CombatSkills") as IEnumerable;
                if (skills != null)
                {
                    foreach (object skill in skills)
                    {
                        string id = GetMember(skill, "m_SkillId") as string;
                        if (string.IsNullOrEmpty(id))
                        {
                            continue;
                        }

                        object definition = Invoke(library, "GetLibraryElement", id);
                        var ranks = GetMember(definition, "m_LaunchRanks") as IEnumerable;
                        if (ranks == null)
                        {
                            continue;
                        }

                        // Cooling skills still count if they come back next turn: moving now sets
                        // up the big skill for then. Further out than that, ignore them.
                        float weight = 1f;
                        string cooldownId = GetMember(definition, "SkillCooldownId") as string;
                        int remaining;
                        if (!string.IsNullOrEmpty(cooldownId) && cooling.TryGetValue(cooldownId, out remaining))
                        {
                            if (remaining > 1)
                            {
                                continue;
                            }

                            weight = 0.6f;
                        }

                        foreach (object value in ranks)
                        {
                            if (ToInt(value) == rank + 1)
                            {
                                total += SkillValue(definition) * weight;
                                break;
                            }
                        }
                    }
                }
            }
            catch
            {
                total = 0f;
            }

            UtilityCache[key] = total;
            return total;
        }

        /// <summary>Utility of one usable skill: a baseline for utility skills plus its damage.</summary>
        private static float SkillValue(object definition)
        {
            if (definition == null || _healthDamageStat == null)
            {
                return 1f;
            }

            try
            {
                object stats = GetMember(definition, "m_ActorDataStats");
                object container = stats == null
                    ? null
                    : (GetMember(stats, "StatContainer") ?? GetMember(stats, "m_StatContainer"));
                if (container == null)
                {
                    return 1f;
                }

                object value = Invoke(container, "GetStatTotal", _healthDamageStat);
                float damage = value == null ? 0f : ToFloat(value);
                if (float.IsNaN(damage) || damage < 0f)
                {
                    damage = 0f;
                }

                return 1f + damage / 10f;
            }
            catch
            {
                return 1f;
            }
        }

        private static float BucketOf(object actor, object statType)
        {
            if (actor == null || statType == null)
            {
                return 1f;
            }

            object value = Invoke(actor, "GetClampedStatValue", statType);
            if (value == null)
            {
                return 1f;
            }

            float result = ToFloat(value);
            return float.IsNaN(result) ? 1f : result;
        }

        private static bool TryPredictedAttack(out uint attackerGuid, out uint victimGuid, out float damage)
        {
            attackerGuid = 0;
            victimGuid = 0;
            damage = 0f;

            foreach (KeyValuePair<uint, float> pair in ForeseekerPlugin.PredictedDamage)
            {
                if (pair.Value <= damage)
                {
                    continue;
                }

                uint victim;
                if (!ForeseekerPlugin.PredictedTarget.TryGetValue(pair.Key, out victim))
                {
                    continue;
                }

                attackerGuid = pair.Key;
                victimGuid = victim;
                damage = pair.Value;
            }

            return damage > 0f;
        }

        /// <summary>
        /// Taunt/guard move the incoming hit from a squishier unit to a sturdier one: value is the
        /// damage avoided minus the damage taken by the new target. Riposte counters when hit.
        /// </summary>
        private static float ThreatTokenValue(string tokenId, object performer, object holder, object instance)
        {
            uint holderGuid = ToUInt(GetMember(holder, "m_ActorGuid"));

            uint attackerGuid;
            uint predictedVictim;
            float damage;
            bool hasPrediction = TryPredictedAttack(out attackerGuid, out predictedVictim, out damage);

            if (tokenId == "riposte")
            {
                float counter = ExpectedHit(holder);
                if (counter <= 0f)
                {
                    return 0f;
                }

                float chance = hasPrediction && predictedVictim == holderGuid ? 1f : 0.3f;
                return 5f * counter * chance / Mathf.Max(1f, _avgEnemyMaxHp);
            }

            if (!hasPrediction)
            {
                return 0.3f;
            }

            uint tankGuid = holderGuid;
            uint fromGuid = predictedVictim;

            if (tokenId == "guard")
            {
                tankGuid = ToUInt(GetMember(instance, "m_SourceActorGuid"));
                if (tankGuid == 0)
                {
                    tankGuid = holderGuid;
                }

                fromGuid = holderGuid;
            }

            if (fromGuid == tankGuid || fromGuid != predictedVictim)
            {
                return 0f;
            }

            object fromActor = Invoke(_teams, "GetActorFromActorGuid", fromGuid);
            object tankActor = tankGuid == holderGuid ? holder : Invoke(_teams, "GetActorFromActorGuid", tankGuid);
            if (fromActor == null || tankActor == null)
            {
                return 0f;
            }

            float fromMax = Mathf.Max(1f, Stat(fromActor, _healthMaxStat, "m_Hp"));
            float tankMax = Mathf.Max(1f, Stat(tankActor, _healthMaxStat, "m_Hp"));

            float avoided = 5f * (Mitigate(fromActor, damage) / fromMax) * HpRiskFactor(fromActor);
            float incurred = 5f * (Mitigate(tankActor, damage) / tankMax) * HpRiskFactor(tankActor);
            return avoided - incurred;
        }

        private static float ExpectedHit(object actor)
        {
            if (actor == null || _healthDamageStat == null)
            {
                return 0f;
            }

            object value = Invoke(actor, "GetClampedStatValue", _healthDamageStat);
            float damage = value == null ? 0f : ToFloat(value);
            return float.IsNaN(damage) || damage <= 0f ? 0f : damage;
        }

        private static float PredictedDamageOf(object actor)
        {
            uint guid = ToUInt(GetMember(actor, "m_ActorGuid"));
            float damage;
            return ForeseekerPlugin.PredictedDamage.TryGetValue(guid, out damage) ? damage : 0f;
        }

        private static float PredictedVictimMaxHp(object actor)
        {
            uint guid = ToUInt(GetMember(actor, "m_ActorGuid"));
            uint victimGuid;
            if (ForeseekerPlugin.PredictedTarget.TryGetValue(guid, out victimGuid))
            {
                object victim = Invoke(_teams, "GetActorFromActorGuid", victimGuid);
                return Mathf.Max(1f, Stat(victim, _healthMaxStat, "m_Hp"));
            }

            return 13f;
        }

        /// <summary>Relative worth of one stack of a token, in "hit points of a 13 HP enemy" units.</summary>
        private static float TokenWeight(string tokenId)
        {
            switch (tokenId)
            {
                case "dodge":
                    return 0.6f;
                case "dodge_plus":
                    return 1.0f;
                case "block":
                    return 0.7f;
                case "block_plus":
                    return 1.1f;
                case "strength":
                case "crit":
                    return 1.0f;
                case "speed":
                case "speed_plus":
                    return 0.3f;
                case "vulnerable":
                case "weak":
                case "dazed":
                case "stun":
                    return 1.0f;
                case "guard":
                    return 0.8f;
                case "riposte":
                    return 0.9f;
                default:
                    return 0.6f;
            }
        }

        private static readonly HashSet<string> UnknownTokens = new HashSet<string>();

        private static void LogUnknownToken(string tokenId)
        {
            if (!UnknownTokens.Add(tokenId))
            {
                return;
            }

            Action<string> sink = RandomTrace.Sink;
            if (sink != null)
            {
                sink("S TOKENUNKNOWN id=" + tokenId);
            }
        }

        private static readonly Dictionary<string, int> TokenPolarityCache = new Dictionary<string, int>();

        /// <summary>1 = beneficial token, -1 = harmful, 0 = unknown.</summary>
        private static int TokenPolarity(string tokenId)
        {
            int cached;
            if (TokenPolarityCache.TryGetValue(tokenId, out cached))
            {
                return cached;
            }

            int value = 0;
            try
            {
                object library = SingletonMonoBehaviour<Library<string, TokenDefinition>>.Instance;
                object definition = Invoke(library, "GetLibraryElement", tokenId);
                if (definition != null)
                {
                    if (ToBool(GetMember(definition, "IsPositive")))
                    {
                        value = 1;
                    }
                    else if (ToBool(GetMember(definition, "IsNegative")))
                    {
                        value = -1;
                    }
                }
            }
            catch
            {
                value = 0;
            }

            TokenPolarityCache[tokenId] = value;
            return value;
        }

        private static int TeamIndexOf(object actor)
        {
            object team = GetMember(actor, "m_Team");
            return ToInt(GetMember(team, "m_TeamIndex"));
        }

        private static bool IsAlly(object a, object b)
        {
            object teamA = GetMember(a, "m_Team");
            object teamB = GetMember(b, "m_Team");
            int indexA = ToInt(GetMember(teamA, "m_TeamIndex"));
            int indexB = ToInt(GetMember(teamB, "m_TeamIndex"));
            return indexA == indexB;
        }

        private static float Stat(object actor, object statType, string fallbackField)
        {
            if (statType != null)
            {
                object value = Invoke(actor, "GetClampedStatValue", statType);
                if (value != null)
                {
                    return ToFloat(value);
                }
            }

            return ToFloat(GetMember(actor, fallbackField));
        }

        private static void ResolveStats()
        {
            if (_statsResolved)
            {
                return;
            }

            _statsResolved = true;
            try
            {
                Type statType = typeof(SkillCalculation).Assembly.GetType("Assets.Code.Actor.ActorStatType");
                if (statType == null)
                {
                    return;
                }

                _healthMaxStat = statType.GetField("HEALTH_MAX", BindingFlags.Public | BindingFlags.Static).GetValue(null);
                _stressMaxStat = statType.GetField("STRESS_MAX", BindingFlags.Public | BindingFlags.Static).GetValue(null);
                _healthDamageStat = statType.GetField("HEALTH_DAMAGE", BindingFlags.Public | BindingFlags.Static).GetValue(null);
                _dealtStat = statType.GetField("HEALTH_DAMAGE_DEALT_PERCENT", BindingFlags.Public | BindingFlags.Static).GetValue(null);
                _receivedStat = statType.GetField("HEALTH_DAMAGE_RECEIVED_PERCENT", BindingFlags.Public | BindingFlags.Static).GetValue(null);
                _deathResistStat = statType.GetField("DEATHS_DOOR_CHANCE", BindingFlags.Public | BindingFlags.Static).GetValue(null);
            }
            catch
            {
                _healthMaxStat = null;
            }
        }

        private static string ShortSkill(string id)
        {
            if (string.IsNullOrEmpty(id))
            {
                return "?";
            }

            return id.Length > 18 ? id.Substring(0, 18) : id;
        }

        private static GUIStyle _style;

        internal static void Draw()
        {
            if (!Visible)
            {
                return;
            }

            if (_style == null)
            {
                _style = new GUIStyle(GUI.skin.label);
                _style.fontSize = 14;
                _style.richText = false;
                _style.wordWrap = false;
            }

            int count = Mathf.Min(Rows.Count, 10);
            bool hasItem = !string.IsNullOrEmpty(_itemLine);
            var rect = new Rect(Screen.width - 560f, 80f, 550f, 54f + (count + (hasItem ? 1 : 0)) * 22f);
            GUI.Box(rect, string.Empty);

            Color header = GUI.color;
            GUI.color = new Color(0.95f, 0.95f, 0.85f);
            GUI.Label(new Rect(rect.x + 10f, rect.y + 6f, rect.width - 20f, 22f), _header, _style);
            GUI.color = header;

            float rowTop = rect.y + 30f;
            if (hasItem)
            {
                Color itemColor = GUI.color;
                GUI.color = new Color(0.75f, 0.92f, 1f);
                GUI.Label(new Rect(rect.x + 10f, rowTop, rect.width - 20f, 22f), _itemLine, _style);
                GUI.color = itemColor;
                rowTop += 22f;
            }

            for (int i = 0; i < count; i++)
            {
                Row row = Rows[i];
                Color old = GUI.color;
                GUI.color = row.Tint;
                GUI.Label(new Rect(rect.x + 10f, rowTop + i * 22f, rect.width - 20f, 22f),
                          (i + 1) + ". " + row.Label + "  [" + row.Score.ToString("F1") + "]", _style);
                GUI.color = old;
            }
        }

        /// <summary>
        /// For the best few candidates, really apply the action, ask the game what the opponent
        /// would do, then roll the battle back. The predicted incoming damage is subtracted.
        /// </summary>
        private static string _lookaheadKey = string.Empty;
        private static float _lookaheadNext;
        private static readonly Dictionary<string, float> _lookaheadPenalties = new Dictionary<string, float>();

        /// <summary>
        /// For the best few candidates, apply the action, ask the game what the opponent would do,
        /// then roll back. The expensive part only runs when the turn or the selected skill changes;
        /// the cached penalties are re-applied to every refreshed row.
        /// </summary>
        private static void ApplyLookahead(object actor, int round, int turn, uint actorGuid)
        {
            object battle = SimState.Battle;
            object state = battle == null ? null : GetMember(battle, "CurrentBattleState");
            if (state == null || state.ToString() != "in_turn_select")
            {
                return;
            }

            string selected = (actor == null ? null : GetMember(actor, "m_SelectedSkillId") as string) ?? string.Empty;
            string key = round + ":" + turn + ":" + actorGuid + ":" + selected;

            if (key != _lookaheadKey
                && Time.realtimeSinceStartup >= _lookaheadNext
                && Rows.Count > 0)
            {
                _lookaheadNext = Time.realtimeSinceStartup + 0.75f;
                _lookaheadKey = key;
                _lookaheadPenalties.Clear();

                float start = Time.realtimeSinceStartup;
                int limit = Mathf.Min(1, Rows.Count);

                for (int i = 0; i < limit; i++)
                {
                    Row row = Rows[i];
                    if (row.ActorResult == null && row.Parts == null)
                    {
                        continue;
                    }

                    try
                    {
                        if (!SimState.Capture())
                        {
                            continue;
                        }

                        // An AOE applies to every one of its targets; everything else is one hit.
                        List<Row> parts = row.Parts ?? new List<Row> { row };
                        for (int p = 0; p < parts.Count; p++)
                        {
                            Row part = parts[p];
                            if (part.ActorResult == null || part.Damage <= 0f)
                            {
                                continue;
                            }

                            object partTarget = Invoke(_teams, "GetActorFromActorGuid", part.TargetGuid);
                            SimState.ApplyDamage(partTarget, part.Damage);

                            int tk;
                            int dt;
                            float hl;
                            float st;
                            int mv;
                            SimState.ApplyActorResult(part.ActorResult, out tk, out dt, out hl, out st, out mv);
                        }

                        // The free item is used on this turn too, so the enemy reacts to it.
                        if (_itemResult != null && _itemTargetGuid != 0)
                        {
                            object itemTarget = Invoke(_teams, "GetActorFromActorGuid", _itemTargetGuid);
                            if (itemTarget != null)
                            {
                                float itemDamage = ToFloat(GetMember(_itemResult, "HealthDamage"));
                                if (!float.IsNaN(itemDamage) && itemDamage > 0f)
                                {
                                    SimState.ApplyDamage(itemTarget, itemDamage);
                                }

                                int itk;
                                int idt;
                                float ihl;
                                float ist;
                                int imv;
                                SimState.ApplyActorResult(_itemResult, out itk, out idt, out ihl, out ist, out imv);
                            }
                        }

                        float incoming = SimState.PeekIncoming();
                        uint victimGuid = SimState.PeekIncomingVictim();
                        float penalty = 0f;
                        if (incoming > 0f && victimGuid != 0)
                        {
                            object victim = Invoke(_teams, "GetActorFromActorGuid", victimGuid);
                            incoming = Mitigate(victim, incoming);
                            penalty += HpLossCost(victim, incoming);
                        }

                        // A deaths-door hero who still carries DOT rolls a deathblow at turn start.
                        foreach (object ally in TeamActors(0))
                        {
                            if (!IsAtDeathsDoor(ally) || PendingDotDamage(ally) <= 0f)
                            {
                                continue;
                            }

                            penalty += 20f * DeathBlowChance(ally);
                        }

                        if (penalty > 0f)
                        {
                            _lookaheadPenalties[row.SkillId + "@" + row.TargetGuid] = penalty;
                        }
                    }
                    catch (Exception ex)
                    {
                        _header = "求解器前瞻出错：" + ex.GetType().Name;
                            ForetestLog.WriteDebug("LOOKAHEAD-ERR " + ex.GetType().Name + ": " + ex.Message);
                    }
                    finally
                    {
                        SimState.Restore();

                        // Anything still different after the rollback is a leak into the real battle.
                        string leak = SimState.LeakReport(4);
                        if (!string.IsNullOrEmpty(leak))
                        {
                            Action<string> leakSink = RandomTrace.Sink;
                            if (leakSink != null)
                            {
                                leakSink("S LEAK " + leak);
                            }
                        }

                        SimState.Clear();
                    }
                }

                float spent = (Time.realtimeSinceStartup - start) * 1000f;
                if (spent > 120f)
                {
                    ForetestLog.WriteDebug("LOOKAHEAD-SLOW " + spent.ToString("0") + "ms");
                    Action<string> sink = RandomTrace.Sink;
                    if (sink != null)
                    {
                        sink("S solver lookahead took " + spent.ToString("F0") + "ms");
                    }
                }
            }

            for (int i = 0; i < Rows.Count; i++)
            {
                Row row = Rows[i];
                float penalty;
                if (_lookaheadPenalties.TryGetValue(row.SkillId + "@" + row.TargetGuid, out penalty)
                    && penalty > 0.01f)
                {
                    row.Score -= penalty;
                    row.Label += "  还手" + penalty.ToString("F1");
                }
            }
        }

        // ------------------------------------------------------------------ reflection

        private static object GetMember(object target, string name)
        {
            if (target == null)
            {
                return null;
            }

            for (Type type = target.GetType(); type != null; type = type.BaseType)
            {
                FieldInfo field = type.GetField(name, Any);
                if (field != null)
                {
                    return field.GetValue(target);
                }

                PropertyInfo property = type.GetProperty(name, Any);
                if (property != null && property.CanRead && property.GetIndexParameters().Length == 0)
                {
                    return property.GetValue(target, null);
                }
            }

            return null;
        }

        private static object Invoke(object target, string method, params object[] args)
        {
            if (target == null)
            {
                return null;
            }

            int argCount = args == null ? 0 : args.Length;
            for (Type type = target.GetType(); type != null; type = type.BaseType)
            {
                foreach (MethodInfo info in type.GetMethods(Any))
                {
                    if (info.Name != method || info.GetParameters().Length != argCount)
                    {
                        continue;
                    }

                    return info.Invoke(target, args);
                }
            }

            return null;
        }

        private static uint ToUInt(object value)
        {
            return value == null ? 0u : Convert.ToUInt32(value);
        }

        private static int ToInt(object value)
        {
            return value == null ? -1 : Convert.ToInt32(value);
        }

        private static float ToFloat(object value)
        {
            return value == null ? float.NaN : Convert.ToSingle(value);
        }

        private static bool ToBool(object value)
        {
            return value != null && Convert.ToBoolean(value);
        }
    }
}