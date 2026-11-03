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
    /// P2 experiment: replay one actor's whole precompute into a throw-away SkillCalculation
    /// and compare it against the numbers the game itself produced, while checking that the
    /// live battle state is unchanged afterwards. This is the gate for a sandbox-based solver.
    /// Read-only by intent: RNG and result tables are snapshotted and restored.
    /// </summary>
    internal static class SolverProbe
    {
        private const BindingFlags Any =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private const BindingFlags AnyStatic =
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        private static float _nextAllowed;
        private static bool _autoRequested;

        /// <summary>Queued by the state-change hook so the replay runs on the next Update tick.</summary>
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

            if (Time.realtimeSinceStartup < _nextAllowed)
            {
                return;
            }

            _autoRequested = false;
            OnHotkey();
        }

        internal static void OnHotkey()
        {
            if (Time.realtimeSinceStartup < _nextAllowed)
            {
                return;
            }

            _nextAllowed = Time.realtimeSinceStartup + 1.5f;

            try
            {
                ReplayCurrentActor();
            }
            catch (Exception ex)
            {
                Emit("ERROR " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void ReplayCurrentActor()
        {
            object battle = GetMember(SingletonMonoBehaviour<CombatBhv>.Instance, "m_Battle");
            if (battle == null)
            {
                Emit("skip: no battle");
                return;
            }

            object teams = GetMember(battle, "m_BattleTeams");
            object turnOrder = GetMember(battle, "m_BattleTurnOrder");
            object tracking = GetMember(battle, "m_BattleTracking");
            uint performerGuid = ToUInt(GetMember(battle, "CurrentActorGuid"));
            if (performerGuid == 0 || teams == null || turnOrder == null)
            {
                Emit("skip: battle not ready (actor=" + performerGuid + ")");
                return;
            }

            object performer = Invoke(teams, "GetActorFromActorGuid", performerGuid);
            object controller = GetMember(performer, "Controller") ?? GetMember(performer, "m_ActorController");
            if (controller == null)
            {
                Emit("skip: no controller");
                return;
            }

            var entries = Invoke(controller, "GetValidSkillTargetEntries", null) as IEnumerable;
            if (entries == null)
            {
                Emit("skip: no valid entries (state=" + GetMember(battle, "CurrentBattleState") + ")");
                return;
            }

            var plans = new List<KeyValuePair<string, List<uint>>>();
            foreach (object entry in entries)
            {
                string skillId = GetMember(entry, "m_SkillId") as string;
                var guids = GetMember(entry, "m_ValidTargetActorGuids") as IReadOnlyList<uint>;
                if (string.IsNullOrEmpty(skillId) || guids == null || guids.Count == 0)
                {
                    continue;
                }

                var targets = new List<uint>(guids.Count);
                for (int i = 0; i < guids.Count; i++)
                {
                    targets.Add(guids[i]);
                }

                plans.Add(new KeyValuePair<string, List<uint>>(skillId, targets));
            }

            if (plans.Count == 0)
            {
                Emit("skip: empty plan list");
                return;
            }

            // ---- snapshot ----
            if (!Probe.PrecomputeCaptured || Probe.PrecomputeRngSnapshot == null)
            {
                Emit("skip: no turn-start snapshot captured yet");
                return;
            }

            JToken liveRng = RandomContainer.SaveToJson();
            List<object> actors = CollectActors(teams);
            string hashBefore = HashTeams(teams);
            long seqBefore = RandomTrace.TotalSeq;
            long skillBefore = RandomTrace.SkillCalcDraws;

            // Rewind to the exact stream state the game used just before it precomputed this turn.
            RandomContainer.LoadFromJson((JObject)Probe.PrecomputeRngSnapshot.DeepClone());

            int replayed = 0;
            int matched = 0;
            int mismatched = 0;
            int multiHitSkills = 0;
            var mismatches = new List<string>();

            try
            {
                object temp = Activator.CreateInstance(
                    typeof(SkillCalculation), teams, turnOrder, tracking);
                MethodInfo addPotential = FindAddPotential(false);
                MethodInfo addPotentialShared = FindAddPotential(true);
                var liveResults = GetMember(GetMember(battle, "m_BattleSkillCalculation"), "m_PotentialResults") as IDictionary;

                if (addPotential == null)
                {
                    Emit("ERROR AddPotentialResult overload missing");
                    return;
                }

                foreach (KeyValuePair<string, List<uint>> plan in plans)
                {
                    // Multi-hit skills are pre-rolled once with the whole target list and then
                    // stored under every target; replicate that or the numbers drift by a point.
                    bool multiHit = DetectMultiHit(liveResults, performerGuid, plan);
                    if (multiHit)
                    {
                        multiHitSkills++;
                    }

                    object shared = null;
                    for (int i = 0; i < plan.Value.Count; i++)
                    {
                        uint targetGuid = plan.Value[i];
                        object input = new SkillCalculation.Input(performerGuid, plan.Key, targetGuid);

                        if (multiHit && shared != null && addPotentialShared != null)
                        {
                            addPotentialShared.Invoke(temp, new object[] { input, shared });
                        }
                        else if (multiHit)
                        {
                            shared = addPotential.Invoke(temp, new object[] { input, plan.Value });
                        }
                        else
                        {
                            var one = new List<uint> { targetGuid };
                            addPotential.Invoke(temp, new object[] { input, one });
                        }

                        replayed++;
                    }
                }

                var tempResults = GetMember(temp, "m_PotentialResults") as IDictionary;

                if (tempResults != null && liveResults != null)
                {
                    foreach (DictionaryEntry pair in tempResults)
                    {
                        object input = pair.Key;
                        string skillId = GetMember(input, "m_SelectedSkillId") as string;
                        uint targetGuid = ToUInt(GetMember(input, "m_SelectedTargetActorGuid"));

                        if (!liveResults.Contains(input))
                        {
                            mismatched++;
                            AddCapped(mismatches, "missing live: " + skillId + "@" + targetGuid);
                            continue;
                        }

                        object live = liveResults[input];
                        float replayDamage = FirstDamage(pair.Value);
                        float liveDamage = FirstDamage(live);
                        bool replayCrit = FirstCrit(pair.Value);
                        bool liveCrit = FirstCrit(live);

                        bool sameDamage = (float.IsNaN(replayDamage) && float.IsNaN(liveDamage))
                                          || Math.Abs(replayDamage - liveDamage) < 0.01f;

                        if (sameDamage && replayCrit == liveCrit)
                        {
                            matched++;
                        }
                        else
                        {
                            mismatched++;
                            AddCapped(mismatches, skillId + "@" + targetGuid
                                                + " replay=" + replayDamage.ToString("F1") + (replayCrit ? "!" : "")
                                                + " live=" + liveDamage.ToString("F1") + (liveCrit ? "!" : ""));
                        }
                    }
                }
                else
                {
                    Emit("ERROR results dictionary missing");
                }
            }
            finally
            {
                CleanupTokenState(actors);
                RandomContainer.LoadFromJson((JObject)liveRng);
            }

            string hashAfter = HashTeams(teams);
            long seqUsed = RandomTrace.TotalSeq - seqBefore;
            long skillUsed = RandomTrace.SkillCalcDraws - skillBefore;

            Emit("REPLAY actor=" + performerGuid
                 + " skills=" + plans.Count
                 + " replayed=" + replayed
                 + " multiHit=" + multiHitSkills
                 + " matched=" + matched
                 + " mismatched=" + mismatched
                 + " rng=" + seqUsed + " (skill=" + skillUsed + ")"
                 + " state=" + (hashBefore == hashAfter ? "UNCHANGED" : "CHANGED!!"));

            if (hashBefore != hashAfter)
            {
                Emit("STATE-BEFORE " + hashBefore);
                Emit("STATE-AFTER  " + hashAfter);
            }

            for (int i = 0; i < mismatches.Count; i++)
            {
                Emit("MISMATCH " + mismatches[i]);
            }
        }

        // ---------------------------------------------------------------- helpers

        private static void AddCapped(List<string> list, string text)
        {
            if (list.Count < 12)
            {
                list.Add(text);
            }
        }

        private static List<object> CollectActors(object teams)
        {
            var actors = new List<object>();
            var teamList = GetMember(teams, "m_Teams") as IEnumerable;
            if (teamList == null)
            {
                return actors;
            }

            foreach (object team in teamList)
            {
                var teamActors = GetMember(team, "m_Actors") as IEnumerable;
                if (teamActors == null)
                {
                    continue;
                }

                foreach (object actor in teamActors)
                {
                    actors.Add(actor);
                }
            }

            return actors;
        }

        /// <summary>
        /// Leaves every token container in the state the game expects. If the replay threw
        /// halfway, the calculation flag can still be set; clearing it here prevents a stuck turn.
        /// </summary>
        private static void CleanupTokenState(List<object> actors)
        {
            for (int i = 0; i < actors.Count; i++)
            {
                object container = GetMember(actors[i], "m_TokenContainer");
                if (container == null)
                {
                    continue;
                }

                if (ToBool(GetMember(container, "m_IsInSkillCalculation")))
                {
                    Invoke(container, "OnEndSkillCalculation", null);
                }

                Invoke(container, "ResetConsumedTokens", null);
            }
        }

        private static float FirstDamage(object skillResult)
        {
            object actorResult = FirstActorResult(skillResult);
            return actorResult == null ? float.NaN : ToFloat(GetMember(actorResult, "HealthDamage"));
        }

        private static bool FirstCrit(object skillResult)
        {
            object actorResult = FirstActorResult(skillResult);
            return actorResult != null && ToBool(GetMember(actorResult, "IsCrit"));
        }

        private static object FirstActorResult(object skillResult)
        {
            var results = GetMember(skillResult, "ActorResults") as IEnumerable;
            if (results == null)
            {
                return null;
            }

            foreach (object item in results)
            {
                return item;
            }

            return null;
        }

        private static MethodInfo FindAddPotential(bool shared)
        {
            Type second = shared ? typeof(SkillCalculation.SkillResult) : typeof(IReadOnlyList<uint>);
            foreach (MethodInfo method in typeof(SkillCalculation).GetMethods(Any))
            {
                if (method.Name != "AddPotentialResult")
                {
                    continue;
                }

                ParameterInfo[] parameters = method.GetParameters();
                if (parameters.Length == 2 && parameters[1].ParameterType == second)
                {
                    return method;
                }
            }

            return null;
        }

        /// <summary>
        /// A multi-hit skill is pre-rolled once with the whole target list and then registered
        /// under every target, which shows up as the very same SkillResult instance for each key.
        /// </summary>
        private static bool DetectMultiHit(IDictionary liveResults, uint performerGuid,
                                           KeyValuePair<string, List<uint>> plan)
        {
            if (liveResults == null || plan.Value.Count < 2)
            {
                return false;
            }

            object first = Lookup(liveResults, new SkillCalculation.Input(performerGuid, plan.Key, plan.Value[0]));
            if (first == null)
            {
                return false;
            }

            for (int i = 1; i < plan.Value.Count; i++)
            {
                object other = Lookup(liveResults, new SkillCalculation.Input(performerGuid, plan.Key, plan.Value[i]));
                if (!ReferenceEquals(first, other))
                {
                    return false;
                }
            }

            return true;
        }

        private static object Lookup(IDictionary dictionary, object key)
        {
            try
            {
                return dictionary.Contains(key) ? dictionary[key] : null;
            }
            catch
            {
                return null;
            }
        }

        private static string HashTeams(object teams)
        {
            var builder = new StringBuilder();
            var teamList = GetMember(teams, "m_Teams") as IEnumerable;
            if (teamList == null)
            {
                return "no-teams";
            }

            var actorValues = new List<string>();
            foreach (object team in teamList)
            {
                var actors = GetMember(team, "m_Actors") as IEnumerable;
                if (actors == null)
                {
                    continue;
                }

                foreach (object actor in actors)
                {
                    actorValues.Add(DescribeActor(actor));
                }
            }

            actorValues.Sort(StringComparer.Ordinal);
            foreach (string value in actorValues)
            {
                builder.Append(value).Append(';');
            }

            return StableHash(builder.ToString());
        }

        private static string DescribeActor(object actor)
        {
            if (actor == null)
            {
                return "null";
            }

            string tokens = CountContainer(GetMember(actor, "m_TokenContainer"));
            string dots = CountContainer(GetMember(actor, "m_DotContainer"));
            string buffs = CountContainer(GetMember(actor, "m_BuffContainer"));

            return ToUInt(GetMember(actor, "m_ActorGuid"))
                   + "|hp=" + ToFloat(GetMember(actor, "m_Hp")).ToString("F2")
                   + "|st=" + ToFloat(GetMember(actor, "m_Stress")).ToString("F2")
                   + "|pos=" + ToInt(GetMember(actor, "m_TeamPosition"))
                   + "|off=" + ToInt(GetMember(actor, "m_TargetTeamPositionOffset"))
                   + "|live=" + ToBool(GetMember(actor, "m_IsLiving"))
                   + "|db=" + ToInt(GetMember(actor, "m_CurrentBattleDeathblowCheckCount"))
                   + "|sel=" + (GetMember(actor, "m_SelectedSkillId") as string)
                   + "|tok=" + tokens + "|dot=" + dots + "|buf=" + buffs;
        }

        private static string CountContainer(object container)
        {
            if (container == null)
            {
                return "?";
            }

            object count = Invoke(container, "GetNumberOfInstances", null);
            return count == null ? "?" : count.ToString();
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

        private static void Emit(string message)
        {
            Action<string> sink = RandomTrace.Sink;
            if (sink != null)
            {
                sink("R " + message);
            }
        }

        // ---------------------------------------------------------------- reflection

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
