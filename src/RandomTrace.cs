using System;
using System.Collections.Generic;
using System.Diagnostics;
using Assets.Code.Math;
using HarmonyLib;
using UnityEngine;

namespace DSH.Foresee
{
    /// <summary>
    /// Harmony hooks on RandomContainer: records every draw with its stream id, call site and value.
    /// Used to learn exactly how many values each AI decision consumes.
    /// </summary>
    internal static class RandomTrace
    {
        internal static bool Enabled;
        internal static Action<string> Sink;

        private static void Emit(string message)
        {
            Action<string> sink = Sink;
            if (sink != null)
            {
                sink(message);
            }
        }

        private static long _seq;

        private static long _skillCalc;

        internal static long TotalSeq
        {
            get { return _seq; }
        }

        internal static long SkillCalcDraws
        {
            get { return _skillCalc; }
        }

        private static readonly Dictionary<string, int> Counts = new Dictionary<string, int>(StringComparer.Ordinal);
        private static readonly Dictionary<string, int> CallerCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        private static int _total;
        private static float _nextFlush;
        private static bool _overflow;

        private static readonly HashSet<string> Watched = new HashSet<string>(StringComparer.Ordinal)
        {
            "ACTOR_CONTROLLER", "SKILL_CALCULATION", "RESIST", "EFFECT", "TOKEN",
            "ACT_OUT", "DEATHS_DOOR", "COMBAT", "ACTOR_CONTAINER", "BUFF", "DOT"
        };

        internal static void OnDraw(string id, string kind, float value)
        {
            if (!Enabled)
            {
                return;
            }

            _total++;
            _seq++;

            string stream = string.IsNullOrEmpty(id) ? "?" : id;
            bool watched = Watched.Contains(stream);

            int count;
            Counts.TryGetValue(stream, out count);
            Counts[stream] = count + 1;

            string caller = "-";
            if (watched)
            {
                caller = CallerName();
                string callerKey = stream + " <- " + caller;
                CallerCounts.TryGetValue(callerKey, out count);
                CallerCounts[callerKey] = count + 1;
            }

            // ACTOR_CONTROLLER is the stream we predict: always log it in order
            if (stream == "ACTOR_CONTROLLER")
            {
                Emit("ACDRAW " + kind + " v=" + value.ToString("F4", System.Globalization.CultureInfo.InvariantCulture) + " from " + caller);
            }

            // Probe stream: every skill-calculation draw in order, with its sequence number,
            // so the caller can measure the exact cost of one precompute / one action.
            if (stream == "SKILL_CALCULATION")
            {
                _skillCalc++;
                if (_seq <= 20000)
                {
                    Emit("SCDRAW seq=" + _seq + " " + kind + " v=" + value.ToString("F4", System.Globalization.CultureInfo.InvariantCulture) + " from " + caller);
                }
            }

            // Every draw, every stream: allows attributing "ambient" draws during UI interaction.
            if (_seq <= 50000)
            {
                Emit("DRAW " + stream + " " + kind + " v=" + value.ToString("F4", System.Globalization.CultureInfo.InvariantCulture) + " from " + caller);
            }
            else if (!_overflow)
            {
                _overflow = true;
                Emit("DRAW ... (per-draw logging capped at 50000 entries)");
            }
        }

        internal static void Tick()
        {
            if (!Enabled || Time.realtimeSinceStartup < _nextFlush)
            {
                return;
            }

            _nextFlush = Time.realtimeSinceStartup + 2f;

            if (_total == 0)
            {
                return;
            }

            string summary = "DRAWSUM total=" + _total;
            foreach (KeyValuePair<string, int> pair in Counts)
            {
                summary += " " + pair.Key + "=" + pair.Value;
            }

            Emit(summary + " | " + CallerSummary());

            Counts.Clear();
            CallerCounts.Clear();
            _total = 0;
            _overflow = false;
        }

        private static string CallerSummary()
        {
            string best = null;
            int bestCount = 0;

            foreach (KeyValuePair<string, int> pair in CallerCounts)
            {
                if (pair.Value > bestCount)
                {
                    bestCount = pair.Value;
                    best = pair.Key;
                }
            }

            return best == null ? "callers=-" : best + " x" + bestCount;
        }

        private static string CallerName()
        {
            try
            {
                StackTrace trace = new StackTrace();
                for (int i = 2; i < trace.FrameCount; i++)
                {
                    System.Reflection.MethodBase method = trace.GetFrame(i).GetMethod();
                    if (method == null)
                    {
                        continue;
                    }

                    Type type = method.DeclaringType;
                    string typeName = type == null ? "?" : type.Name;
                    if (typeName == "RandomTrace"
                        || typeName.Contains("Patch")
                        || typeName.Contains("RandomContainer")
                        || typeName.Contains("Harmony")
                        || method.Name.Contains("DMD<"))
                    {
                        continue;
                    }

                    return typeName + "." + method.Name;
                }
            }
            catch
            {
                // fall through
            }

            return "?";
        }
    }

    [HarmonyPatch(typeof(RandomContainer), "GetValue", new[] { typeof(IRandomIdentifierContainer) })]
    internal static class PatchGetValue
    {
        private static void Postfix(IRandomIdentifierContainer identifierContainer, float __result)
        {
            RandomTrace.OnDraw(identifierContainer == null ? null : identifierContainer.GetIdentifier(), "GetValue", __result);
        }
    }

    [HarmonyPatch(typeof(RandomContainer), "Range", new[] { typeof(IRandomIdentifierContainer), typeof(int), typeof(int) })]
    internal static class PatchRangeInt
    {
        private static void Postfix(IRandomIdentifierContainer identifierContainer, int min, int max, int __result)
        {
            RandomTrace.OnDraw(identifierContainer == null ? null : identifierContainer.GetIdentifier(),
                               "RangeInt[" + min + "," + max + ")", __result);
        }
    }

    [HarmonyPatch(typeof(RandomContainer), "Range", new[] { typeof(IRandomIdentifierContainer), typeof(float), typeof(float) })]
    internal static class PatchRangeFloat
    {
        private static void Postfix(IRandomIdentifierContainer identifierContainer, float __result)
        {
            RandomTrace.OnDraw(identifierContainer == null ? null : identifierContainer.GetIdentifier(), "RangeFloat", __result);
        }
    }

    [HarmonyPatch(typeof(RandomContainer), "CheckValue", new[] { typeof(IRandomIdentifierContainer), typeof(float) })]
    internal static class PatchCheckValue
    {
        private static void Postfix(IRandomIdentifierContainer identifierContainer, bool __result)
        {
            RandomTrace.OnDraw(identifierContainer == null ? null : identifierContainer.GetIdentifier(), __result ? "Check(true)" : "Check(false)", 0f);
        }
    }
}
