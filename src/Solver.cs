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
            public string Text;
            public float Score;
            public Color Tint;
        }

        internal static void Refresh()
        {
            if (Time.realtimeSinceStartup < _nextRefresh)
            {
                return;
            }

            _nextRefresh = Time.realtimeSinceStartup + 0.5f;

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

            string name = (GetMember(actor, "m_ActorName") as string) ?? ("#" + actorGuid);
            _header = "solver: " + name + "  round=" + ToInt(GetMember(battle, "CurrentRound"))
                      + " turn=" + ToInt(GetMember(battle, "CurrentTurn"))
                      + "  options=" + plans;
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
                        avoidedText = " -in" + avoided.ToString("F0");
                    }
                }
            }

            int effects;
            score += EffectValue(performer, actorResult, out effects);

            string targetName = (GetMember(target, "m_ActorName") as string) ?? ("#" + targetGuid);
            string label = ShortSkill(skillId) + " → " + targetName
                           + "  " + damage.ToString("F0")
                           + (crit ? "!" : string.Empty)
                           + (hit ? string.Empty : " miss")
                           + (effects > 0 ? "  fx" + effects : string.Empty)
                           + avoidedText
                           + "  [" + score.ToString("F1") + "]";

            Color tint = targetIsAlly ? (after <= 0f ? new Color(1f, 0.4f, 0.4f) : new Color(0.8f, 0.9f, 1f))
                                      : new Color(1f, 0.95f, 0.8f);

            return new Row { Text = label, Score = score, Tint = tint };
        }

        /// <summary>
        /// Folds the effects this result would apply into the score:
        /// tokens are valued by their own positive/negative tag, DOTs are always bad for the
        /// holder, heals are good for the holder — then flipped for the opposing team.
        /// </summary>
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

        internal static void Draw()
        {
            if (!Visible)
            {
                return;
            }

            int count = Mathf.Min(Rows.Count, 10);
            var rect = new Rect(Screen.width - 470f, 80f, 460f, 46f + count * 20f);
            GUI.Box(rect, string.Empty);

            GUI.Label(new Rect(rect.x + 8f, rect.y + 4f, rect.width - 16f, 20f), _header);

            for (int i = 0; i < count; i++)
            {
                Row row = Rows[i];
                Color old = GUI.color;
                GUI.color = row.Tint;
                GUI.Label(new Rect(rect.x + 8f, rect.y + 26f + i * 20f, rect.width - 16f, 20f), row.Text);
                GUI.color = old;
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
