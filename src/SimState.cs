using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using Assets.Code.Combat;
using Assets.Code.Math;
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
    }
}
