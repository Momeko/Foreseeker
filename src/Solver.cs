using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Assets.Code.Combat;
using Assets.Code.Library;
using Assets.Code.Skill;
using Assets.Code.Token;
using Assets.Code.Utils;
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

        internal static bool Visible;

        private static float _nextRefresh;
        private static string _header = "solver: idle";
        private static readonly List<Row> Rows = new List<Row>();

        private static object _healthMaxStat;
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

        private static void Build()
        {
            Rows.Clear();

            object battle = GetMember(SingletonMonoBehaviour<CombatBhv>.Instance, "m_Battle");
            if (battle == null)
            {
                _header = "solver: no battle";
                return;
            }

            object teams = GetMember(battle, "m_BattleTeams");
            _teams = teams;
            uint actorGuid = ToUInt(GetMember(battle, "CurrentActorGuid"));
            object actor = actorGuid == 0 ? null : Invoke(teams, "GetActorFromActorGuid", actorGuid);
            if (actor == null)
            {
                _header = "solver: no current actor";
                return;
            }

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

                    Rows.Add(Score(actor, target, skillId, targetGuid, damage, crit, hit, matchedResult));
                }
            }

            Rows.Sort((a, b) => b.Score.CompareTo(a.Score));
            ApplyLookahead(actor, ToInt(GetMember(battle, "CurrentRound")),
                           ToInt(GetMember(battle, "CurrentTurn")), actorGuid);
            Rows.Sort((a, b) => b.Score.CompareTo(a.Score));

            string name = ActorName(actor);
            _header = "求解器 · " + name
                      + " · 第" + ToInt(GetMember(battle, "CurrentRound")) + "回合"
                      + " 第" + (ToInt(GetMember(battle, "CurrentTurn")) + 1) + "手"
                      + " · " + plans + " 个选择 · 评分越高越好";
        }

        private static Row Score(object performer, object target, string skillId, uint targetGuid,
                                float damage, bool crit, bool hit, object actorResult)
        {
            bool targetIsAlly = IsAlly(performer, target);

            float maxHp = Mathf.Max(1f, Stat(target, _healthMaxStat, "m_Hp"));
            float hp = ToFloat(GetMember(target, "m_Hp"));
            float after = Mathf.Max(0f, hp - damage);

            // Ally damage is a cost, enemy damage is a gain; a kill is worth much more.
            float sign = targetIsAlly ? -1f : 1f;
            float score = sign * 5f * (damage / maxHp);
            string avoidedText = string.Empty;
            if (after <= 0f && damage > 0f)
            {
                score += targetIsAlly ? -1000f : 30f;

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
                        score += 5f * (avoided / victimMax);
                        avoidedText = "  少挨" + avoided.ToString("F0");
                    }
                }
            }

            int effects;
            score += EffectValue(performer, actorResult, out effects);

            string label = SkillName(skillId) + " → " + ActorName(target)
                           + "   伤害" + damage.ToString("F0")
                           + (crit ? " 暴击" : string.Empty)
                           + (hit ? string.Empty : " 未命中")
                           + EffectsText(actorResult)
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
                ActorResult = actorResult
            };
        }

        /// <summary>
        /// Folds the effects this result would apply into the score:
        /// tokens are valued by their own positive/negative tag, DOTs are always bad for the
        /// holder, heals are good for the holder — then flipped for the opposing team.
        /// </summary>
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

            if (!string.IsNullOrEmpty(className) && !string.IsNullOrEmpty(custom) && custom != className)
            {
                return custom + "（" + className + "）";
            }

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
                        int polarity = TokenPolarity(tokenId);
                        if (polarity > 0)
                        {
                            total += holderSign * 1.5f * amount;
                        }
                        else if (polarity < 0)
                        {
                            total -= holderSign * 1.5f * amount;
                        }

                        continue;
                    }

                    string dotId = GetMember(definition, "m_DotAddId") as string;
                    if (!string.IsNullOrEmpty(dotId))
                    {
                        int amount = Math.Max(1, ToInt(GetMember(instance, "DotAddAmount")));
                        total -= holderSign * 1.0f * amount;
                        continue;
                    }

                    float heal = ToFloat(GetMember(instance, "HealthHealAmount"));
                    if (heal > 0f)
                    {
                        float maxHp = Mathf.Max(1f, Stat(holder, _healthMaxStat, "m_Hp"));
                        total += holderSign * 5f * (heal / maxHp);
                    }
                }
            }

            return total;
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
            var rect = new Rect(Screen.width - 560f, 80f, 550f, 54f + count * 22f);
            GUI.Box(rect, string.Empty);

            Color header = GUI.color;
            GUI.color = new Color(0.95f, 0.95f, 0.85f);
            GUI.Label(new Rect(rect.x + 10f, rect.y + 6f, rect.width - 20f, 22f), _header, _style);
            GUI.color = header;

            for (int i = 0; i < count; i++)
            {
                Row row = Rows[i];
                Color old = GUI.color;
                GUI.color = row.Tint;
                GUI.Label(new Rect(rect.x + 10f, rect.y + 30f + i * 22f, rect.width - 20f, 22f),
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
                    if (row.ActorResult == null)
                    {
                        continue;
                    }

                    try
                    {
                        if (!SimState.Capture())
                        {
                            continue;
                        }

                        object target = Invoke(_teams, "GetActorFromActorGuid", row.TargetGuid);
                        SimState.ApplyDamage(target, row.Damage);

                        int tokens;
                        int dots;
                        float heal;
                        float stress;
                        int move;
                        SimState.ApplyActorResult(row.ActorResult, out tokens, out dots, out heal, out stress, out move);

                        float incoming = SimState.PeekIncoming();
                        uint victimGuid = SimState.PeekIncomingVictim();
                        if (incoming > 0f && victimGuid != 0)
                        {
                            object victim = Invoke(_teams, "GetActorFromActorGuid", victimGuid);
                            float maxHp = Mathf.Max(1f, Stat(victim, _healthMaxStat, "m_Hp"));
                            _lookaheadPenalties[row.SkillId + "@" + row.TargetGuid] = 5f * (incoming / maxHp);
                        }
                    }
                    catch (Exception ex)
                    {
                        _header = "求解器前瞻出错：" + ex.GetType().Name;
                    }
                    finally
                    {
                        SimState.Restore();
                        SimState.Clear();
                    }
                }

                float spent = (Time.realtimeSinceStartup - start) * 1000f;
                if (spent > 120f)
                {
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