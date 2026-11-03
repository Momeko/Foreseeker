using System;
using UnityEngine;

namespace DSH.Foresee
{
    /// <summary>
    /// Self-owned driver object. BepInEx hosts plugin components on "BepInEx_Manager", which
    /// this game destroys at boot (OnEnable -> OnDisable -> OnDestroy, no Update ever again).
    /// The plugin therefore keeps its state in the managed object and lets this host, created
    /// by us and marked DontDestroyOnLoad, deliver Update/OnGUI.
    /// </summary>
    internal static class ForeseeHost
    {
        internal static ForeseePlugin Plugin;

        internal static bool Alive
        {
            get { return !ReferenceEquals(Runner.Instance, null); }
        }

        internal static void Attach(ForeseePlugin plugin)
        {
            if (!ReferenceEquals(plugin, null))
            {
                Plugin = plugin;
            }

            Ensure();
        }

        /// <summary>Re-creates the driver if the game destroyed it (it wipes hosted objects at boot).</summary>
        internal static void Ensure()
        {
            if (Alive || ReferenceEquals(Plugin, null))
            {
                return;
            }

            try
            {
                var host = new GameObject("ForeseeHost");
                UnityEngine.Object.DontDestroyOnLoad(host);
                host.AddComponent<Runner>();
                Log("LIFECYCLE host created alive=" + Alive);
            }
            catch (Exception ex)
            {
                Log("LIFECYCLE host failed: " + ex.Message);
            }
        }

        private static void Log(string message)
        {
            try
            {
                if (!ReferenceEquals(Plugin, null))
                {
                    Plugin.HostLog(message);
                }
            }
            catch
            {
                // never let diagnostics break the host
            }
        }

        internal sealed class Runner : MonoBehaviour
        {
            internal static Runner Instance;

            private int _frames;

            private void Awake()
            {
                Instance = this;
            }

            private void OnDestroy()
            {
                if (ReferenceEquals(Instance, this))
                {
                    Instance = null;
                    Log("LIFECYCLE host destroyed");
                }
            }

            private void Update()
            {
                if (ReferenceEquals(Plugin, null))
                {
                    return;
                }

                if (_frames < 3)
                {
                    _frames++;
                    Log("LIFECYCLE host Update frame=" + _frames
                        + " t=" + Time.realtimeSinceStartup.ToString("F1"));
                }

                Plugin.TickPlugin();
            }

            private void OnGUI()
            {
                if (!ReferenceEquals(Plugin, null))
                {
                    Plugin.DrawPluginGui();
                }
            }
        }
    }
}
