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

        internal static bool Visible = true;

        private static float _nextRefresh;
        private static string _header = "solver: idle";
        private static readonly List<Row> Rows = new List<Row>();
        private static readonly HashSet<uint> _doomed = new HashSet<uint>();

        private static object _healthMaxStat;
        private static object _healthDamageStat;
        private static object _dealtStat;
        private static object _receivedStat;
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

                    Rows.Add(new Row
                    {
                        Label = SkillName(skillId) + " → 敌方全体"
                                + (totalDamage > 0f ? "   伤害合计" + totalDamage.ToString("F0") : string.Empty)
                                + EffectsText(firstResult),
                        Score = merged,
                        Tint = targetRows[0].Tint,
                        SkillId = skillId,
                        TargetGuid = 0,
                        Damage = 0f,
                        ActorResult = null
                    });
                }
                else
                {
                    Rows.AddRange(targetRows);
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
                        score += HpLossCost(victim, avoided);
                        avoidedText = "  少挨" + avoided.ToString("F0");
                    }
                }
            }

            int effects;
            score += EffectValue(performer, actorResult, out effects);

            string label = SkillName(skillId) + " → " + ActorName(target)
                           + (damage > 0f ? "   伤害" + damage.ToString("F0") : string.Empty)
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
            return 5f * (damage / max) * HpRiskFactor(actor);
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
                            incoming = Mitigate(victim, incoming);
                            float maxHp = Mathf.Max(1f, Stat(victim, _healthMaxStat, "m_Hp"));
                            _lookaheadPenalties[row.SkillId + "@" + row.TargetGuid] = HpLossCost(victim, incoming);
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