using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using Assets.Code.Combat;
using Assets.Code.Math;
using Assets.Code.Skill;
using Assets.Code.Utils;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace DSH.Foreseeker
{
    /// <summary>
    /// Semantic snapshot of the live battle: everything an applied skill result can change.
    /// Used to try a candidate action on the real objects and roll it back.
    /// </summary>
    internal static class SimState
    {
        private const BindingFlags Any =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private sealed class FieldSnap
        {
            public object Target;
            public FieldInfo Field;
            public object Value;
            public Array ArrayValue;
        }

        private sealed class ListSnap
        {
            public object Target;
            public FieldInfo Field;
            public ArrayList Items;
        }

        private static readonly List<FieldSnap> Fields = new List<FieldSnap>();
        private static readonly List<ListSnap> Lists = new List<ListSnap>();
        private static JToken _rng;
        private static object _potentialDictionary;
        private static object _skillCalculation;

        internal static int CapturedCount
        {
            get { return Fields.Count; }
        }

        private static bool _autoRequested;
        private static float _nextAuto;

        /// <summary>Queued by the state-change hook: run the apply/rollback probe at turn start.</summary>
        internal static void RequestAuto()
        {
            _autoRequested = true;
        }

        internal static void TickAuto()
        {
            if (!_autoRequested)
            {
                return;
            }

            if (Time.realtimeSinceStartup < _nextAuto)
            {
                return;
            }

            _autoRequested = false;
            _nextAuto = Time.realtimeSinceStartup + 1.5f;
            ApplyTest();
        }

        internal static object Battle
        {
            get
            {
                try
                {
                    return GetMember(SingletonMonoBehaviour<CombatBhv>.Instance, "m_Battle");
                }
                catch
                {
                    return null;
                }
            }
        }

        // ---------------------------------------------------------------- capture / restore

        internal static bool Capture()
        {
            Clear();

            object battle = Battle;
            if (battle == null)
            {
                return false;
            }

            _rng = RandomContainer.SaveToJson();
            _skillCalculation = GetMember(battle, "m_BattleSkillCalculation");
            _potentialDictionary = GetMember(_skillCalculation, "m_PotentialResults");

            var actors = CollectActors(battle);
            for (int i = 0; i < actors.Count; i++)
            {
                CaptureObject(actors[i]);
                CaptureContainer(actors[i], "m_TokenContainer");
                CaptureContainer(actors[i], "m_DotContainer");
                CaptureContainer(actors[i], "m_BuffContainer");
                CaptureListField(actors[i], "m_SkillCooldowns");
            }

            return true;
        }

        internal static bool Restore()
        {
            try
            {
                for (int i = Fields.Count - 1; i >= 0; i--)
                {
                    FieldSnap snap = Fields[i];
                    try
                    {
                        if (snap.ArrayValue != null)
                        {
                            snap.Field.SetValue(snap.Target, snap.ArrayValue.Clone());
                        }
                        else
                        {
                            snap.Field.SetValue(snap.Target, snap.Value);
                        }
                    }
                    catch
                    {
                        // best effort; the hash comparison will report a mismatch
                    }
                }

                for (int i = Lists.Count - 1; i >= 0; i--)
                {
                    ListSnap snap = Lists[i];
                    var list = snap.Field.GetValue(snap.Target) as IList;
                    if (list == null)
                    {
                        continue;
                    }

                    list.Clear();
                    for (int j = 0; j < snap.Items.Count; j++)
                    {
                        list.Add(snap.Items[j]);
                    }
                }

                if (_potentialDictionary != null)
                {
                    try
                    {
                        var field = _skillCalculation.GetType().GetField("m_PotentialResults", Any);
                        if (field != null)
                        {
                            field.SetValue(_skillCalculation, _potentialDictionary);
                        }
                    }
                    catch
                    {
                    }
                }

                ResetTokenConsumes();
                RandomContainer.LoadFromJson((JObject)_rng);
                return true;
            }
            catch (Exception ex)
            {
                Log("restore failed: " + ex.Message);
                return false;
            }
        }

        internal static void Clear()
        {
            Fields.Clear();
            Lists.Clear();
            _rng = null;
            _potentialDictionary = null;
            _skillCalculation = null;
        }

        // ---------------------------------------------------------------- verification

        /// <summary>Reads the LIVE objects and reports fields/lists that differ from the snapshot.</summary>
        internal static List<string> Mismatches()
        {
            var problems = new List<string>();

            for (int i = 0; i < Fields.Count; i++)
            {
                FieldSnap snap = Fields[i];
                try
                {
                    object live = snap.Field.GetValue(snap.Target);
                    if (snap.ArrayValue != null)
                    {
                        var liveArray = live as Array;
                        var savedArray = snap.ArrayValue;
                        if (liveArray == null || liveArray.Length != savedArray.Length)
                        {
                            problems.Add(snap.Field.Name + ": array shape");
                            continue;
                        }

                        for (int j = 0; j < liveArray.Length; j++)
                        {
                            if (!Equals(liveArray.GetValue(j), savedArray.GetValue(j)))
                            {
                                problems.Add(snap.Field.Name + "[" + j + "]: "
                                             + savedArray.GetValue(j) + " -> " + liveArray.GetValue(j));
                                break;
                            }
                        }
                    }
                    else if (!Equals(live, snap.Value))
                    {
                        problems.Add(snap.Field.Name + ": " + snap.Value + " -> " + live);
                    }
                }
                catch (Exception ex)
                {
                    problems.Add(snap.Field.Name + ": " + ex.GetType().Name);
                }
            }

            for (int i = 0; i < Lists.Count; i++)
            {
                ListSnap snap = Lists[i];
                try
                {
                    var live = snap.Field.GetValue(snap.Target) as IList;
                    if (live == null || live.Count != snap.Items.Count)
                    {
                        problems.Add(snap.Field.Name + ": count "
                                     + (live == null ? -1 : live.Count) + " != " + snap.Items.Count);
                        continue;
                    }

                    for (int j = 0; j < live.Count; j++)
                    {
                        if (!ReferenceEquals(live[j], snap.Items[j]))
                        {
                            problems.Add(snap.Field.Name + "[" + j + "]: item changed");
                            break;
                        }
                    }
                }
                catch (Exception ex)
                {
                    problems.Add(snap.Field.Name + ": " + ex.GetType().Name);
                }
            }

            return problems;
        }

        // ---------------------------------------------------------------- test

        /// <summary>Snapshot -> mutate -> restore -> compare. Logs SIMTEST.</summary>
        internal static void SelfTest()
        {
            object battle = Battle;
            if (battle == null)
            {
                Log("SIMTEST skip: no battle");
                return;
            }

            if (!Capture())
            {
                Log("SIMTEST skip: capture failed");
                return;
            }

            int before = Mismatches().Count;

            // Synthetic mutation through the same channel an apply would use.
            object victim = FirstActor(battle);
            FieldInfo hpField = FindField(victim, "m_Hp");
            if (victim != null && hpField != null)
            {
                hpField.SetValue(victim, Convert.ToSingle(hpField.GetValue(victim)) - 3f);
            }

            int mutated = Mismatches().Count;

            bool restored = Restore();
            List<string> after = Mismatches();

            Log("SIMTEST fields=" + Fields.Count + " lists=" + Lists.Count
                + " cleanBefore=" + (before == 0)
                + " detected=" + (mutated > 0)
                + " restored=" + (restored && after.Count == 0)
                + " remaining=" + after.Count);

            for (int i = 0; i < after.Count && i < 8; i++)
            {
                Log("SIMDIFF " + after[i]);
            }

            Clear();
        }

        // ---------------------------------------------------------------- apply

        /// <summary>
        /// Takes a real pre-rolled result for the current actor, applies its damage to the live
        /// target, checks the change is visible, then rolls the whole battle back.
        /// Logs APPTEST.
        /// </summary>
        internal static void ApplyTest()
        {
            object battle = Battle;
            if (battle == null)
            {
                Log("APPTEST skip: no battle");
                return;
            }

            string skillId;
            uint targetGuid;
            float damage;
            object target;
            object actorResult;
            if (!TryFirstCandidate(battle, out skillId, out targetGuid, out damage, out target, out actorResult))
            {
                Log("APPTEST skip: no candidate result");
                return;
            }

            if (!Capture())
            {
                Log("APPTEST skip: capture failed");
                return;
            }

            int cleanBefore = Mismatches().Count;
            FieldInfo hpField = FindField(target, "m_Hp");
            float hpBefore = hpField == null ? float.NaN : Convert.ToSingle(hpField.GetValue(target));

            ApplyDamage(target, damage);

            int tokens;
            int dots;
            float heal;
            float stress;
            int move;
            ApplyActorResult(actorResult, out tokens, out dots, out heal, out stress, out move);

            uint currentGuid = ToUInt(GetMember(battle, "CurrentActorGuid"));
            string peek = PeekNextEnemy(battle, currentGuid);

            float hpAfter = hpField == null ? float.NaN : Convert.ToSingle(hpField.GetValue(target));
            int afterApply = Mismatches().Count;

            Restore();
            int afterRestore = Mismatches().Count;

            Log("APPTEST skill=" + skillId
                + " target=" + ActorLabel(target)
                + " dmg=" + damage.ToString("F1")
                + " hp=" + hpBefore.ToString("F1") + "->" + hpAfter.ToString("F1")
                + " heal=" + heal.ToString("F1")
                + " stress=" + stress.ToString("F1")
                + " move=" + move
                + " tok=" + tokens
                + " dot=" + dots
                + " " + peek
                + " cleanBefore=" + (cleanBefore == 0)
                + " applied=" + (afterApply > 0)
                + " restored=" + (afterRestore == 0)
                + " remaining=" + afterRestore);

            Clear();
        }

        internal static void ApplyDamage(object actor, float damage)
        {
            if (actor == null || damage <= 0f)
            {
                return;
            }

            FieldInfo hpField = FindField(actor, "m_Hp");
            if (hpField == null)
            {
                return;
            }

            float current = Convert.ToSingle(hpField.GetValue(actor));
            hpField.SetValue(actor, Math.Max(0f, current - damage));
        }

        /// <summary>
        /// Applies the non-damage part of a pre-rolled result: heals, stress, movement.
        /// Tokens and DOTs are only counted for now (they need container APIs).
        /// </summary>
        internal static void ApplyActorResult(object actorResult, out int tokens, out int dots,
                                              out float heal, out float stress, out int move)
        {
            tokens = 0;
            dots = 0;
            heal = 0f;
            stress = 0f;
            move = 0;

            if (actorResult == null)
            {
                return;
            }

            object container = GetMember(actorResult, "m_AppliedEffectsOutputContainer");
            var outputs = GetMember(container, "Outputs") as IEnumerable;
            if (outputs == null)
            {
                return;
            }

            foreach (object output in outputs)
            {
                object holder = GetMember(output, "m_TargetActor");
                if (holder == null)
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
                    object definition = GetMember(instance, "EffectDefinition");
                    if (definition == null)
                    {
                        continue;
                    }

                    float healAmount = ToFloat(GetMember(instance, "HealthHealAmount"));
                    if (healAmount > 0f)
                    {
                        heal += healAmount;
                        ApplyHealthDelta(holder, healAmount);
                    }

                    float stressDamage = ToFloat(GetMember(instance, "StressDamage"));
                    if (stressDamage > 0f)
                    {
                        stress += stressDamage;
                        ApplyStressDelta(holder, stressDamage);
                    }

                    float stressHeal = ToFloat(GetMember(instance, "StressHeal"));
                    if (stressHeal > 0f)
                    {
                        stress -= stressHeal;
                        ApplyStressDelta(holder, -stressHeal);
                    }

                    int moveAmount = ToInt(GetMember(instance, "Move"));
                    if (moveAmount != 0)
                    {
                        move += moveAmount;
                        ApplyMoveDelta(holder, moveAmount);
                    }

                    if (!string.IsNullOrEmpty(GetMember(definition, "m_TokenAddId") as string))
                    {
                        tokens += Math.Max(1, ToInt(GetMember(instance, "TokenAddAmount")));
                    }

                    if (!string.IsNullOrEmpty(GetMember(definition, "m_DotAddId") as string))
                    {
                        dots += Math.Max(1, ToInt(GetMember(instance, "DotAddAmount")));
                    }
                }
            }
        }

        internal static void ApplyHealthDelta(object actor, float delta)
        {
            FieldInfo hpField = FindField(actor, "m_Hp");
            if (hpField == null)
            {
                return;
            }

            float current = Convert.ToSingle(hpField.GetValue(actor));
            hpField.SetValue(actor, Math.Max(0f, current + delta));
        }

        internal static void ApplyStressDelta(object actor, float delta)
        {
            FieldInfo stressField = FindField(actor, "m_Stress");
            if (stressField == null)
            {
                return;
            }

            float current = Convert.ToSingle(stressField.GetValue(actor));
            stressField.SetValue(actor, Math.Max(0f, current + delta));
        }

        internal static void ApplyMoveDelta(object actor, int delta)
        {
            FieldInfo offsetField = FindField(actor, "m_TargetTeamPositionOffset");
            if (offsetField == null)
            {
                return;
            }

            int current = Convert.ToInt32(offsetField.GetValue(actor));
            offsetField.SetValue(actor, current + delta);
        }

        private static bool TryFirstCandidate(object battle, out string skillId, out uint targetGuid,
                                              out float damage, out object target, out object actorResult)
        {
            skillId = null;
            targetGuid = 0;
            damage = 0f;
            target = null;
            actorResult = null;

            object teams = GetMember(battle, "m_BattleTeams");
            uint actorGuid = ToUInt(GetMember(battle, "CurrentActorGuid"));
            object actor = Invoke(teams, "GetActorFromActorGuid", actorGuid);
            if (actor == null)
            {
                return false;
            }

            object controller = GetMember(actor, "Controller") ?? GetMember(actor, "m_ActorController");
            var entries = Invoke(controller, "GetValidSkillTargetEntries", null) as IEnumerable;
            if (entries == null)
            {
                return false;
            }

            IDictionary results = GetMember(GetMember(battle, "m_BattleSkillCalculation"), "m_PotentialResults") as IDictionary;
            if (results == null)
            {
                return false;
            }

            bool hasFallback = false;
            string fallbackSkill = null;
            uint fallbackTarget = 0;
            float fallbackDamage = 0f;
            object fallbackActor = null;
            object fallbackResult = null;

            foreach (object entry in entries)
            {
                string id = GetMember(entry, "m_SkillId") as string;
                var guids = GetMember(entry, "m_ValidTargetActorGuids") as IReadOnlyList<uint>;
                if (string.IsNullOrEmpty(id) || guids == null || guids.Count == 0)
                {
                    continue;
                }

                object candidate = Invoke(teams, "GetActorFromActorGuid", guids[0]);
                if (candidate == null)
                {
                    continue;
                }

                object input = new SkillCalculation.Input(actorGuid, id, guids[0]);
                if (!results.Contains(input))
                {
                    continue;
                }

                var actorResults = GetMember(results[input], "ActorResults") as IEnumerable;
                if (actorResults == null)
                {
                    continue;
                }

                foreach (object mapped in actorResults)
                {
                    if (ToUInt(GetMember(mapped, "m_TargetActorGuid")) != guids[0])
                    {
                        continue;
                    }

                    float value = ToFloat(GetMember(mapped, "HealthDamage"));
                    if (float.IsNaN(value))
                    {
                        value = 0f;
                    }

                    if (value > 0f)
                    {
                        skillId = id;
                        targetGuid = guids[0];
                        damage = value;
                        target = candidate;
                        actorResult = mapped;
                        return true;
                    }

                    if (!hasFallback)
                    {
                        hasFallback = true;
                        fallbackSkill = id;
                        fallbackTarget = guids[0];
                        fallbackDamage = value;
                        fallbackActor = candidate;
                        fallbackResult = mapped;
                    }
                }
            }

            if (hasFallback)
            {
                skillId = fallbackSkill;
                targetGuid = fallbackTarget;
                damage = fallbackDamage;
                target = fallbackActor;
                actorResult = fallbackResult;
                return true;
            }

            return false;
        }

        private static string ActorLabel(object actor)
        {
            if (actor == null)
            {
                return "?";
            }

            string name = GetMember(actor, "m_ActorName") as string;
            return string.IsNullOrEmpty(name) ? ("#" + ToUInt(GetMember(actor, "m_ActorGuid"))) : name;
        }

        // ---------------------------------------------------------------- enemy peek

        /// <summary>
        /// Runs the game's own precompute for one living enemy on the CURRENT (already applied)
        /// state, then reports what that enemy could do. The caller restores everything after.
        /// </summary>
        private static string PeekNextEnemy(object battle, uint currentGuid)
        {
            try
            {
                object enemy = FindLivingEnemy(battle, currentGuid);
                if (enemy == null)
                {
                    return "enemy=none";
                }

                uint enemyGuid = ToUInt(GetMember(enemy, "m_ActorGuid"));
                object controller = GetMember(enemy, "Controller") ?? GetMember(enemy, "m_ActorController");
                if (controller == null)
                {
                    return "enemy=" + enemyGuid + " ctrl=none";
                }

                List<string> skillIds = EquippedSkillIds(enemy);
                if (skillIds.Count == 0)
                {
                    return "enemy=" + enemyGuid + " skills=0";
                }

                // true keeps the entry list (false clears it), but the event it raises makes the
                // UI drop the current actor's skill bar, so suppress it for this call only.
                Probe.SuppressValidSkillsEvent = true;
                try
                {
                    Invoke(controller, "CalculateValidSkillTargetEntries", skillIds, true);
                }
                finally
                {
                    Probe.SuppressValidSkillsEvent = false;
                }

                var entries = GetMember(controller, "m_SkillTargetEntries") as IEnumerable;
                if (entries == null)
                {
                    return "enemy=" + enemyGuid + " entries=none";
                }

                object teams = GetMember(battle, "m_BattleTeams");
                object turnOrder = GetMember(battle, "m_BattleTurnOrder");
                object tracking = GetMember(battle, "m_BattleTracking");
                object temp = Activator.CreateInstance(typeof(SkillCalculation), teams, turnOrder, tracking);
                MethodInfo addPotential = FindAddPotential();
                if (addPotential == null)
                {
                    return "enemy=" + enemyGuid + " addPotential=none";
                }

                int pairs = 0;
                foreach (object entry in entries)
                {
                    string skill = GetMember(entry, "m_SkillId") as string;
                    var guids = GetMember(entry, "m_ValidTargetActorGuids") as IReadOnlyList<uint>;
                    if (string.IsNullOrEmpty(skill) || guids == null)
                    {
                        continue;
                    }

                    for (int i = 0; i < guids.Count; i++)
                    {
                        object input = new SkillCalculation.Input(enemyGuid, skill, guids[i]);
                        addPotential.Invoke(temp, new object[] { input, new List<uint> { guids[i] } });
                        pairs++;
                    }
                }

                IDictionary results = GetMember(temp, "m_PotentialResults") as IDictionary;
                if (results == null)
                {
                    return "enemy=" + enemyGuid + " results=none";
                }

                string best = "none";
                float bestDamage = -1f;
                int count = 0;

                foreach (DictionaryEntry pair in results)
                {
                    string skill = GetMember(pair.Key, "m_SelectedSkillId") as string;
                    var actorResults = GetMember(pair.Value, "ActorResults") as IEnumerable;
                    if (actorResults == null)
                    {
                        continue;
                    }

                    foreach (object mapped in actorResults)
                    {
                        count++;
                        float value = ToFloat(GetMember(mapped, "HealthDamage"));
                        if (float.IsNaN(value) || value <= 0f)
                        {
                            continue;
                        }

                        if (value > bestDamage)
                        {
                            bestDamage = value;
                            best = skill + "@" + ToUInt(GetMember(mapped, "m_TargetActorGuid"))
                                   + "=" + value.ToString("F1");
                        }
                    }
                }

                string name = GetMember(enemy, "m_ActorName") as string;
                return "enemy=" + (string.IsNullOrEmpty(name) ? "#" + enemyGuid : name)
                       + " pairs=" + pairs + " results=" + count + " best=" + best;
            }
            catch (Exception ex)
            {
                return "enemy=error:" + ex.GetType().Name + ":" + ex.Message;
            }
        }

        private static MethodInfo FindAddPotential()
        {
            foreach (MethodInfo method in typeof(SkillCalculation).GetMethods(Any))
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

        private static object FindLivingEnemy(object battle, uint currentGuid)
        {
            object teams = GetMember(battle, "m_BattleTeams");
            int currentTeam = CurrentTeamIndex(teams, currentGuid);

            var teamList = GetMember(teams, "m_Teams") as IEnumerable;
            if (teamList == null)
            {
                return null;
            }

            foreach (object team in teamList)
            {
                int teamIndex = ToInt(GetMember(team, "m_TeamIndex"));
                if (teamIndex == currentTeam)
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
                    if (actor != null && ToInt(GetMember(actor, "m_IsLiving")) != 0)
                    {
                        return actor;
                    }
                }
            }

            return null;
        }

        private static int CurrentTeamIndex(object teams, uint guid)
        {
            object actor = Invoke(teams, "GetActorFromActorGuid", guid);
            object team = GetMember(actor, "m_Team");
            return ToInt(GetMember(team, "m_TeamIndex"));
        }

        private static List<string> EquippedSkillIds(object actor)
        {
            var ids = new List<string>();
            var skills = GetMember(actor, "m_CombatSkills") as IEnumerable;
            if (skills == null)
            {
                return ids;
            }

            foreach (object skill in skills)
            {
                if (skill == null)
                {
                    continue;
                }

                object equipped = GetMember(skill, "m_IsEquipped");
                if (equipped != null && ToInt(equipped) == 0)
                {
                    continue;
                }

                string id = GetMember(skill, "m_SkillId") as string;
                if (!string.IsNullOrEmpty(id))
                {
                    ids.Add(id);
                }
            }

            return ids;
        }

        // ---------------------------------------------------------------- internals

        private static void CaptureObject(object target)
        {
            if (target == null)
            {
                return;
            }

            for (Type type = target.GetType(); type != null && type != typeof(object); type = type.BaseType)
            {
                foreach (FieldInfo field in type.GetFields(Any | BindingFlags.DeclaredOnly))
                {
                    if (field.IsStatic || field.IsLiteral)
                    {
                        continue;
                    }

                    Type fieldType = field.FieldType;
                    bool simple = fieldType.IsPrimitive || fieldType.IsEnum || fieldType == typeof(string);
                    bool array = fieldType.IsArray && fieldType.GetElementType() == typeof(bool);

                    if (!simple && !array)
                    {
                        continue;
                    }

                    try
                    {
                        object value = field.GetValue(target);
                        var snap = new FieldSnap { Target = target, Field = field, Value = value };
                        if (array)
                        {
                            snap.ArrayValue = (Array)value;
                            snap.Value = null;
                        }

                        Fields.Add(snap);
                    }
                    catch
                    {
                    }
                }
            }
        }

        private static void CaptureContainer(object actor, string fieldName)
        {
            object container = GetMember(actor, fieldName);
            if (container == null)
            {
                return;
            }

            CaptureObject(container);
            CaptureListField(container, FindListField(container));
            CaptureInstances(container);
        }

        private static void CaptureInstances(object container)
        {
            FieldInfo listField = FindListField(container);
            if (listField == null)
            {
                return;
            }

            var list = listField.GetValue(container) as IList;
            if (list == null)
            {
                return;
            }

            for (int i = 0; i < list.Count; i++)
            {
                CaptureObject(list[i]);
            }
        }

        private static void CaptureListField(object target, string fieldName)
        {
            if (target == null || string.IsNullOrEmpty(fieldName))
            {
                return;
            }

            FieldInfo field = FindField(target, fieldName);
            CaptureListField(target, field);
        }

        private static void CaptureListField(object target, FieldInfo field)
        {
            if (field == null)
            {
                return;
            }

            var list = field.GetValue(target) as IList;
            if (list == null)
            {
                return;
            }

            var copy = new ArrayList();
            for (int i = 0; i < list.Count; i++)
            {
                copy.Add(list[i]);
            }

            Lists.Add(new ListSnap { Target = target, Field = field, Items = copy });
        }

        private static FieldInfo FindListField(object target)
        {
            if (target == null)
            {
                return null;
            }

            for (Type type = target.GetType(); type != null && type != typeof(object); type = type.BaseType)
            {
                foreach (FieldInfo field in type.GetFields(Any | BindingFlags.DeclaredOnly))
                {
                    if (field.IsStatic || !field.FieldType.IsGenericType)
                    {
                        continue;
                    }

                    Type definition = field.FieldType.GetGenericTypeDefinition();
                    if (definition != typeof(List<>))
                    {
                        continue;
                    }

                    Type element = field.FieldType.GetGenericArguments()[0];
                    if (element == typeof(string))
                    {
                        continue;
                    }

                    return field;
                }
            }

            return null;
        }

        private static FieldInfo FindField(object target, string name)
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
                    return field;
                }
            }

            return null;
        }

        private static List<object> CollectActors(object battle)
        {
            var actors = new List<object>();
            object teams = GetMember(battle, "m_BattleTeams");
            var teamList = GetMember(teams, "m_Teams") as IEnumerable;
            if (teamList == null)
            {
                return actors;
            }

            foreach (object team in teamList)
            {
                var list = GetMember(team, "m_Actors") as IEnumerable;
                if (list == null)
                {
                    continue;
                }

                foreach (object actor in list)
                {
                    actors.Add(actor);
                }
            }

            return actors;
        }

        private static object FirstActor(object battle)
        {
            List<object> actors = CollectActors(battle);
            return actors.Count > 0 ? actors[0] : null;
        }

        private static void ResetTokenConsumes()
        {
            object battle = Battle;
            if (battle == null)
            {
                return;
            }

            List<object> actors = CollectActors(battle);
            for (int i = 0; i < actors.Count; i++)
            {
                object container = GetMember(actors[i], "m_TokenContainer");
                if (container == null)
                {
                    continue;
                }

                try
                {
                    MethodInfo reset = container.GetType().GetMethod("ResetConsumedTokens", Any);
                    if (reset != null)
                    {
                        reset.Invoke(container, null);
                    }
                }
                catch
                {
                }
            }
        }

        private static string RuntimeHelpersId(object value)
        {
            if (value == null)
            {
                return "null";
            }

            object id = GetMember(value, "m_ActorGuid");
            if (id != null)
            {
                return "a" + id;
            }

            return value.GetType().Name;
        }

        private static string StableHash(string text)
        {
            unchecked
            {
                uint hash = 2166136261;
                foreach (char c in text)
                {
                    hash ^= c;
                    hash *= 16777619;
                }

                return hash.ToString("X8");
            }
        }

        private static void Log(string message)
        {
            Action<string> sink = RandomTrace.Sink;
            if (sink != null)
            {
                sink("S " + message);
            }
        }

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
            return value == null ? 0 : Convert.ToInt32(value);
        }

        private static float ToFloat(object value)
        {
            return value == null ? float.NaN : Convert.ToSingle(value);
        }
    }
}