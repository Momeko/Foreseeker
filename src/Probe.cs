using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace DSH.Foreseeker
{
    /// <summary>
    /// Read-only probe for the "skill calculation RNG budget" question:
    ///   - when does SkillCalculation precompute fire (turn start vs skill selection)?
    ///   - how many SKILL_CALCULATION stream values does each (skill, target) cost?
    /// Emits "P ..." lines through RandomTrace.Sink. Never mutates game state.
    /// </summary>
    internal static class Probe
    {
        /// <summary>Stamped into the log at startup so the loaded build can be identified.</summary>
        internal const string BuildTag = "solver-p2.3";

        private static readonly List<MethodInfo> Hooked = new List<MethodInfo>();

        private static FieldInfo _inputPerformer;
        private static FieldInfo _inputSkill;
        private static FieldInfo _inputTarget;

        private static long _addSeq;
        private static long _addSkillCalc;
        private static long _selSeq;
        private static long _selSkillCalc;

        /// <summary>RNG state captured immediately before the actor's turn-start precompute.</summary>
        internal static Newtonsoft.Json.Linq.JToken PrecomputeRngSnapshot;
        internal static bool PrecomputeCaptured;
        internal static long PrecomputeCapturedAt;

        internal static void Emit(string line)
        {
            Action<string> sink = RandomTrace.Sink;
            if (sink != null)
            {
                sink("P " + line);
            }
        }

        internal static void Install(HarmonyLib.Harmony harmony)
        {
            ResolveInputFields();

            Hook(harmony, typeof(Assets.Code.Combat.Events.EventBattleStateChanged), "Trigger", "OnStateChanged", null);
            Hook(harmony, typeof(Assets.Code.Combat.Events.EventBattleStartRound), "Trigger", "OnStartRound", null);
            Hook(harmony, typeof(Assets.Code.Combat.Events.EventBattleIncrementTurn), "Trigger", "OnIncrementTurn", null);
            Hook(harmony, typeof(Assets.Code.Actor.Events.EventSkillSelectionChanged), "Trigger", "OnSelection", "OnSelectionPre");
            Hook(harmony, typeof(Assets.Code.Skill.Events.EventSkillFinalizeResults), "Trigger", "OnFinalize", null);
            Hook(harmony, typeof(Assets.Code.Skill.SkillCalculation), "HandleEventActorControllerCalculatedValidSkills", "OnPrecompute", null);
            Hook(harmony, typeof(Assets.Code.Actor.ActorController.ActorControllerBase), "CalculateValidSkillTargetEntries", null, "OnValidSkillsPre");

            MethodInfo addPotential = FindAddPotentialResult();
            if (addPotential != null)
            {
                Patch(harmony, addPotential, "OnAddPotentialPre", "OnAddPotentialPost");
            }
            else
            {
                Emit("WARN AddPotentialResult(Input, IReadOnlyList<uint>) not found");
            }

            Emit("PROBE installed hooks=" + Hooked.Count + " build=" + BuildTag);
        }

        private static void ResolveInputFields()
        {
            try
            {
                Type inputType = typeof(Assets.Code.Skill.SkillCalculation).GetNestedType(
                    "Input", BindingFlags.Public | BindingFlags.NonPublic);
                if (inputType == null)
                {
                    foreach (Type nested in typeof(Assets.Code.Skill.SkillCalculation).GetNestedTypes(
                                 BindingFlags.Public | BindingFlags.NonPublic))
                    {
                        if (nested.Name == "Input")
                        {
                            inputType = nested;
                            break;
                        }
                    }
                }

                if (inputType == null)
                {
                    Emit("WARN SkillCalculation.Input type not found");
                    return;
                }

                _inputPerformer = inputType.GetField("m_PerformerGuid");
                _inputSkill = inputType.GetField("m_SelectedSkillId");
                _inputTarget = inputType.GetField("m_SelectedTargetActorGuid");
            }
            catch (Exception ex)
            {
                Emit("WARN ResolveInputFields: " + ex.Message);
            }
        }

        private static MethodInfo FindAddPotentialResult()
        {
            foreach (MethodInfo method in typeof(Assets.Code.Skill.SkillCalculation).GetMethods(
                         BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
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

        private static void Hook(Harmony harmony, Type type, string methodName, string postfixName, string prefixName)
        {
            MethodInfo target;
            try
            {
                target = AccessTools.Method(type, methodName);
            }
            catch (Exception ex)
            {
                Emit("WARN resolve " + type.Name + "." + methodName + ": " + ex.Message);
                return;
            }

            if (target == null)
            {
                Emit("WARN missing " + type.Name + "." + methodName);
                return;
            }

            Patch(harmony, target, prefixName, postfixName);
        }

        private static void Patch(Harmony harmony, MethodInfo target, string prefixName, string postfixName)
        {
            try
            {
                HarmonyMethod prefix = prefixName == null
                    ? null
                    : new HarmonyMethod(typeof(Probe).GetMethod(prefixName, BindingFlags.Static | BindingFlags.NonPublic));
                HarmonyMethod postfix = postfixName == null
                    ? null
                    : new HarmonyMethod(typeof(Probe).GetMethod(postfixName, BindingFlags.Static | BindingFlags.NonPublic));

                harmony.Patch(target, prefix, postfix);
                Hooked.Add(target);
                Emit("HOOK " + target.DeclaringType.Name + "." + target.Name + "/" + target.GetParameters().Length);
            }
            catch (Exception ex)
            {
                Emit("WARN patch failed " + target.Name + ": " + ex.Message);
            }
        }

        // ---------------- handlers ----------------

        private static void OnStateChanged(object[] __args)
        {
            ForeseekerHost.Ensure();

            string to = Str(__args, 1);
            Emit("STATE " + Str(__args, 0) + "->" + to
                 + " actor=" + Str(__args, 2) + " team=" + Str(__args, 3)
                 + " turn=" + Str(__args, 4) + " seq=" + RandomTrace.TotalSeq);

            if (string.Equals(to, "in_turn_select", StringComparison.OrdinalIgnoreCase))
            {
                Emit("AUTOREQ seq=" + RandomTrace.TotalSeq);
                SolverProbe.RequestAuto();
            }
        }

        private static void OnStartRound(object[] __args)
        {
            Emit("ROUND n=" + Str(__args, 0) + " seq=" + RandomTrace.TotalSeq);
        }

        private static void OnIncrementTurn(object[] __args)
        {
            Emit("TURN cur=" + Str(__args, 0) + " total=" + Str(__args, 1) + " seq=" + RandomTrace.TotalSeq);
        }

        private static void OnSelectionPre()
        {
            _selSeq = RandomTrace.TotalSeq;
            _selSkillCalc = RandomTrace.SkillCalcDraws;
        }

        private static void OnSelection(object[] __args)
        {
            Emit("SELECT " + Join(__args)
                 + " draws=" + (RandomTrace.TotalSeq - _selSeq)
                 + " skdraws=" + (RandomTrace.SkillCalcDraws - _selSkillCalc)
                 + " seq=" + RandomTrace.TotalSeq);
        }

        private static void OnFinalize(object[] __args)
        {
            object result = __args.Length > 0 ? __args[0] : null;
            Emit("FINALIZE skill=" + Field(result, "m_SkillId")
                 + " performer=" + Field(result, "m_PerformerActorGuid")
                 + " team=" + Str(__args, 1) + " seq=" + RandomTrace.TotalSeq);
        }

        private static void OnPrecompute(object[] __args)
        {
            object evt = __args.Length > 0 ? __args[0] : null;
            Emit("PRECOMPUTE performer=" + Field(evt, "m_PerformerActorGuid")
                 + " user=" + Field(evt, "m_IsUserInput")
                 + " seq=" + RandomTrace.TotalSeq);
        }

        /// <summary>
        /// Prefix on CalculateValidSkillTargetEntries(skills, triggerEvent): when triggerEvent is true
        /// the event handler will run the whole precompute synchronously, so this is the exact
        /// moment before that turn's skill-calculation draws.
        /// </summary>
        private static void OnValidSkillsPre(object[] __args)
        {
            try
            {
                if (__args == null || __args.Length < 2)
                {
                    return;
                }

                if (!(__args[1] is bool) || !(bool)__args[1])
                {
                    return;
                }

                PrecomputeRngSnapshot = Assets.Code.Math.RandomContainer.SaveToJson();
                PrecomputeCaptured = true;
                PrecomputeCapturedAt = RandomTrace.TotalSeq;
                Emit("SNAPRNG seq=" + PrecomputeCapturedAt);
            }
            catch (Exception ex)
            {
                Emit("WARN snapshot failed: " + ex.Message);
            }
        }

        private static void OnAddPotentialPre()
        {
            _addSeq = RandomTrace.TotalSeq;
            _addSkillCalc = RandomTrace.SkillCalcDraws;
        }

        private static void OnAddPotentialPost(object[] __args)
        {
            long cost = RandomTrace.TotalSeq - _addSeq;
            long skillCost = RandomTrace.SkillCalcDraws - _addSkillCalc;
            object input = __args.Length > 0 ? __args[0] : null;

            int targets = 0;
            if (__args.Length > 1 && __args[1] is IReadOnlyList<uint> list)
            {
                targets = list.Count;
            }

            Emit("ADDPOT skill=" + FieldOf(input, _inputSkill)
                 + " target=" + FieldOf(input, _inputTarget)
                 + " performer=" + FieldOf(input, _inputPerformer)
                 + " targets=" + targets
                 + " skcost=" + skillCost
                 + " allcost=" + cost
                 + " seq=" + RandomTrace.TotalSeq);
        }

        // ---------------- helpers ----------------

        private static string Str(object[] args, int index)
        {
            if (args == null || index < 0 || index >= args.Length || args[index] == null)
            {
                return "-";
            }

            return args[index].ToString();
        }

        private static string Join(object[] args)
        {
            if (args == null || args.Length == 0)
            {
                return "-";
            }

            string text = "";
            for (int i = 0; i < args.Length; i++)
            {
                if (i > 0)
                {
                    text += " ";
                }

                text += "a" + i + "=" + (args[i] == null ? "-" : args[i].ToString());
            }

            return text;
        }

        private static string Field(object instance, string name)
        {
            if (instance == null)
            {
                return "-";
            }

            try
            {
                FieldInfo field = instance.GetType().GetField(name,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                object value = field == null ? null : field.GetValue(instance);
                return value == null ? "-" : value.ToString();
            }
            catch
            {
                return "?";
            }
        }

        private static string FieldOf(object instance, FieldInfo field)
        {
            if (instance == null || field == null)
            {
                return "-";
            }

            try
            {
                object value = field.GetValue(instance);
                return value == null ? "-" : value.ToString();
            }
            catch
            {
                return "?";
            }
        }
    }
}
