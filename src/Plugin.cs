using System;
using System.Collections.Generic;
using System.IO;
using System.Collections;
using System.Reflection;
using System.Text;
using Assets.Code.Combat;
using Assets.Code.Combat.Events;
using Assets.Code.Combat.Queries;
using Assets.Code.Actor;
using Assets.Code.Actor.Queries;
using Assets.Code.Effect;
using Assets.Code.Game;
using Assets.Code.Math;
using Assets.Code.Resist;
using Assets.Code.Skill;
using Assets.Code.Skill.Queries;
using Assets.Code.Events;
using BepInEx;
using UnityEngine;

namespace DSH.Foreseeker
{
    /// <summary>
        /// BepInEx 5 plugin: an IMGUI window that predicts the current skill, plus a state window.
        /// Both windows are draggable and toggled with hotkeys from Foreseeker.ini.
    ///
    /// Layout is 1.5x the base size (window 480x225, fonts 21/20).
    /// Placement: LEFT EDGE, VERTICALLY CENTERED on first use.
    ///
    /// Hotkeys go through BepInEx.UnityInput (it auto-detects the new Input System).
    /// DD2 runs with "Input System package" active, where UnityEngine.Input throws
    /// InvalidOperationException, so UnityEngine.Input must NOT be used here.
    ///
    /// Settings live in "Foreseeker.ini" next to the plugin DLL (BepInEx\plugins\Foreseeker.ini):
    ///   ToggleKey        show/hide toggle key        (default F2)
    ///   ToggleKeyAlt     alternative toggle key      (default F7, empty = disabled)
    ///   ResetPositionKey move back to left-center    (default F8)
    /// The file is re-read automatically about one second after it changes.
    /// </summary>
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class ForeseekerPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.momeko.dd2.foreseeker";
        public const string PluginName = "Foreseeker";
        public const string PluginVersion = "0.1.0";

        private const int WindowId = 987321;
        private const int StateWindowId = 987322;
        private const int PredictWindowId = 987323;
        private const float StateWindowWidth = 880f;
        private const float StateWindowHeight = 470f;
        private const int StateFontSize = 16;

        // --- 1.5x layout -----------------------------------------------------
        private const float UiScale = 1.5f;
        private const float WindowWidth = 320f * UiScale;   // 480
        private const float WindowHeight = 150f * UiScale;  // 225
        private const float LeftMargin = 16f;
        private const float TitleBarHeight = 20f * UiScale; // 30 (draggable strip)
        private const float CloseButtonWidth = 80f * UiScale;  // 120
        private const float CloseButtonHeight = 24f * UiScale; // 36
        private const int TitleFontSize = 20;  // round(13 * 1.5)
        private const int LabelFontSize = 21;  // round(14 * 1.5)

        private const string IniFileName = "Foreseeker.ini";

        private const string DefaultIni =
            "; ============================================================\r\n" +
            "; Foreseeker (BepInEx plugin) - 配置文件\r\n" +
            "; 放在 BepInEx\\plugins\\ 下、与 Foreseeker.dll 同目录。\r\n" +
            "; 修改保存后约 1 秒自动生效（无需重启游戏）。\r\n" +
            ";\r\n" +
            "; 键名 = UnityEngine.KeyCode 的枚举名，例如：\r\n" +
            ";   F1 F2 ... F12, A-Z, 0-9, Space, Tab, Escape,\r\n" +
            ";   LeftShift, RightShift, LeftControl, RightControl, LeftAlt, RightAlt,\r\n" +
            ";   UpArrow, DownArrow, LeftArrow, RightArrow, Keypad0-9, Mouse0-2 ...\r\n" +
            "; 值留空 = 禁用该键。\r\n" +
            "; ============================================================\r\n" +
            "\r\n" +
            "[Foreseeker]\r\n" +
            "\r\n" +
            "; 显示 / 隐藏窗口的切换键（默认 F2）\r\n" +
            "ToggleKey = F2\r\n" +
            "; 备用切换键（默认 F7，留空 = 禁用）\r\n" +
            "ToggleKeyAlt = F7\r\n" +
            "\r\n" +
            "; 把窗口移回左侧中间的键（默认 F8）\r\n" +
            "ResetPositionKey = F8\r\n" +
            "\r\n" +
            "; 存档读数器：是否启用（默认 true）\r\n" +
            "; 输出到插件 DLL 同目录的子文件夹（BepInEx\\plugins\\Foreseeker\\）\r\n" +
            "SaveReaderEnabled = true\r\n" +
            "\r\n" +
            "; 存档读数器：检查间隔（秒，最小 1，默认 5）\r\n" +
            "SaveReaderInterval = 5\r\n" +
            "\r\n" +
            "; 状态窗口（右侧正中）的显示/隐藏键（默认 F9）\r\n" +
            "StateWindowKey = F9\r\n" +
            "\r\n" +
            "; 右侧「DD2 状态读取」窗口启动时是否显示（true=显示，false=默认关闭，F9 仍可临时开关）\r\n" +
            "StateWindowVisible = true\r\n" +
            "\r\n" +
            "; 敌方行动预判（实验性：用 ACTOR_CONTROLLER 随机流 + 游戏自身的合法技能/目标表预测）\r\n" +
            "EnemyIntentEnabled = true\r\n" +
            "\r\n" +
            "; 「DD2 状态读取 预测」窗口的显示/隐藏键（默认 F10）\r\n" +
            "PredictWindowKey = F10\r\n" +
            "\r\n" +
            "; Harmony 随机流追踪（记录每次 GetValue/Range/CheckValue 的流与调用点）\r\n" +
            "RandomTraceEnabled = true\r\n";

        private bool _visible = true;
        private Rect _windowRect = new Rect(0f, 0f, WindowWidth, WindowHeight);

        private GUIStyle _windowStyle;
        private GUIStyle _labelStyle;

        private string _iniPath;

        private KeyCode _toggleKey = KeyCode.F2;
        private KeyCode _toggleKeyAlt = KeyCode.F7;
        private KeyCode _resetPositionKey = KeyCode.F8;

        private DateTime _iniWriteTimeUtc;
        private float _nextIniCheck;

        private bool _inputErrorLogged;

        // --- save reader -----------------------------------------------------
        private bool _saveReaderEnabled = true;
        private float _saveReaderInterval = 5f;
        private float _nextSavePoll;
        private string _lastSaveStamp;
        private string _saveRoot;
        private string _saveOutputDir;
        private bool _saveReaderErrorLogged;
        private SaveReader.Snapshot _lastSnapshot;
        private string _battleStartStamp;
        private string _battleRound1Stamp;
        private readonly HashSet<string> _loggedUnknownClasses = new HashSet<string>(StringComparer.Ordinal);

        // --- live combat (queried from the running game; the save only holds the battle-start snapshot) ---
        private int _liveRound = -1;
        private int _liveTurn = -1;
        private int _liveTotalTurns = -1;
        private List<uint> _liveOrder = new List<uint>();
        private float _nextLivePoll;
        private float _nextListenerCheck;
        private bool _liveErrorLogged;
        private bool _listenersAdded;

        private int _drawTicks;
        private bool _firstDrawLogged;

        // --- state window (right side, vertically centered) -------------------
        private bool _stateVisible = true;
        private bool _stateVisibleDefault = true;
        private bool _enemyIntentEnabled = true;
        private bool _randomTraceEnabled = true;
        private string _enemyIntent = string.Empty;
        private string _playerStatus = string.Empty;
        private readonly List<string> _playerLines = new List<string>();
        private bool _predictVisible = true;
        private Rect _predictRect = new Rect(0f, 0f, 340f, 200f);
        private bool _predictPlaced;
        private int _predictPlacedW;
        private int _predictPlacedH;
        private readonly List<string> _predictLines = new List<string>();
        private int _acOffset;
        private string _selKey = string.Empty;
        private string _selTurnKey = string.Empty;
        private string _roundKey = string.Empty;
        private List<float> _roundPeek;

        private List<float> _selPeek;
        private List<float> _selCalc;
        private int _actionCounter;
        private string _learnKey = string.Empty;
        private float _nextLearnPoll;
        private KeyCode _predictKey = KeyCode.F10;
        private Rect _stateRect = new Rect(0f, 0f, StateWindowWidth, StateWindowHeight);
        private Vector2 _stateScroll;
        private GUIStyle _stateLabelStyle;
        private bool _statePlaced;
        private int _statePlacedWidth;
        private int _statePlacedHeight;
        private string _stateText;
        private string _stateTextStamp;
        private KeyCode _stateKey = KeyCode.F9;

        // Placement bookkeeping: re-center only while the player has not dragged the window.
        private bool _placed;
        private int _placedScreenWidth;
        private int _placedScreenHeight;
        private Vector2 _autoPosition;

        private void Awake()
        {
            // BepInEx can host the plugin component on a CHILD object. DontDestroyOnLoad only
            // works for root GameObjects ("only works for root GameObjects..." in Player.log),
            // so without this the game destroys us at the first scene load: OnEnable ->
            // OnDisable -> OnDestroy, no Update ever runs, and Harmony hooks keep working.
            try
            {
                Transform parent = transform.parent;
                Logger.LogWarning("LIFECYCLE Awake object=" + gameObject.name
                                  + " parent=" + (parent == null ? "<root>" : parent.name));
                if (parent != null)
                {
                    transform.SetParent(null, true);
                    Logger.LogWarning("LIFECYCLE promoted to root");
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning("LIFECYCLE promote failed: " + ex.Message);
            }

            UnityEngine.Object.DontDestroyOnLoad(gameObject);

            _iniPath = Path.Combine(Paths.PluginPath, IniFileName);

            // Save reader: output goes into a subfolder next to the plugin DLL.
            _saveRoot = SaveReader.SaveRootFromPersistentData(Application.persistentDataPath);
            _saveOutputDir = Path.Combine(Paths.PluginPath, PluginName.Replace(' ', '_'));

            // DOT type / per-tick damage come from the game's CSV data.
            SaveReader.ConfigureCatalogs(Path.Combine(Application.dataPath, "StreamingAssets", "Excel"));

            LoadIni(createIfMissing: true);

            try
            {
                HarmonyLib.Harmony harmony = new HarmonyLib.Harmony(PluginGuid);
                harmony.PatchAll();
                RandomTrace.Sink = message => Logger.LogInfo(message);
                RandomTrace.Enabled = _randomTraceEnabled;
                Probe.Install(harmony);
                Logger.LogInfo("RandomTrace hooks installed (enabled=" + _randomTraceEnabled + ")");
            }
            catch (Exception ex)
            {
                Logger.LogWarning("RandomTrace hooks failed: " + ex.Message);
            }

            // Initial visibility of the right-side state window (F9 still toggles it).
            _stateVisible = _stateVisibleDefault;

            EnsureLiveListeners();

            Logger.LogInfo(PluginName + " " + PluginVersion + " loaded (window " + WindowWidth + "x" + WindowHeight
                           + ", toggle=" + _toggleKey + ", reset=" + _resetPositionKey + ", ini=" + _iniPath + ").");
            Logger.LogInfo("save reader: enabled=" + _saveReaderEnabled + " interval=" + _saveReaderInterval + "s"
                           + " root=" + _saveRoot + " out=" + _saveOutputDir);

            ForeseekerHost.Attach(this);

            try
            {
                UnityEngine.SceneManagement.SceneManager.sceneLoaded += OnSceneLoadedForHost;
            }
            catch (Exception ex)
            {
                Logger.LogWarning("LIFECYCLE scene hook failed: " + ex.Message);
            }
        }

        private static void OnSceneLoadedForHost(UnityEngine.SceneManagement.Scene scene,
                                                 UnityEngine.SceneManagement.LoadSceneMode mode)
        {
            ForeseekerHost.Ensure();
        }

        private float _nextHeartbeat;
        private int _frames;

        internal void HostLog(string message)
        {
            try
            {
                Logger.LogWarning(message);
            }
            catch
            {
                // diagnostics must never break the driver
            }
        }

        private void OnEnable()
        {
            Logger.LogWarning("LIFECYCLE OnEnable active=" + gameObject.activeInHierarchy
                              + " scene=" + gameObject.scene.name);

            // If Awake ran while BepInEx's manager object was still inactive, the earlier
            // DontDestroyOnLoad was ignored and the first scene load destroys us. Re-apply it
            // now that the object is active.
            try
            {
                UnityEngine.Object.DontDestroyOnLoad(gameObject);
                Logger.LogWarning("LIFECYCLE ddol re-applied");
            }
            catch (Exception ex)
            {
                Logger.LogWarning("LIFECYCLE ddol re-apply failed: " + ex.Message);
            }
        }

        private void Start()
        {
            Logger.LogWarning("LIFECYCLE Start");
        }

        private void OnDisable()
        {
            Logger.LogWarning("LIFECYCLE OnDisable");
        }

        private void OnDestroy()
        {
            Logger.LogWarning("LIFECYCLE OnDestroy");
        }

        private void Update()
        {
            if (ForeseekerHost.Alive)
            {
                return;
            }

            TickPlugin();
        }

        internal void TickPlugin()
        {
            _frames++;

            if (_frames <= 3)
            {
                Logger.LogWarning("LIFECYCLE Update frame=" + _frames
                                  + " t=" + Time.realtimeSinceStartup.ToString("F2"));
            }

            if (Time.realtimeSinceStartup >= _nextHeartbeat)
            {
                _nextHeartbeat = Time.realtimeSinceStartup + 5f;
                Logger.LogInfo("UPD t=" + Time.realtimeSinceStartup.ToString("F1")
                               + " vis=" + _visible + " pred=" + _predictVisible
                               + " rt=" + _randomTraceEnabled);
            }

            CheckIniForChanges();
            HandleHotkeys();
            Solver.Refresh();
            UpdateLiveCombat();
            UpdateSkillPreview();
            UpdateSaveReader();

            try
            {
                SolverProbe.TickAuto();
            }
            catch (Exception ex)
            {
                Logger.LogWarning("solver tick failed: " + ex.Message);
            }
        }

        // ------------------------------------------------- skill preview

        /// <summary>Rows shown in the window: "投掷匕首 → 敌人: 伤害 4-7（暴击 6） 暴击35%".</summary>
        private sealed class EffectLine
        {
            public string Text;
            public float Resist;
            public bool? PredictedResisted;
            public float PredictedRoll;
        }

        private sealed class SkillPreviewRow
        {
            public string TargetName;
            public uint TargetGuid;
            public int Position;
            public bool FromTargetResult;
            public float DamageLow;
            public float DamageHigh;
            public float CritDamage;
            public float CritChance;
            public float ToHitChance;
            public bool IsDamageValid;
            public bool IsCritValid;
            public bool IsToHitValid;

            // Exact roll from SkillCalculation.m_PotentialResults (null when unavailable).
            public readonly List<EffectLine> Effects = new List<EffectLine>();
            public float? ExactDamage;
            public bool? ExactCrit;
            public bool? ExactHit;
        }

        private readonly List<SkillPreviewRow> _skillRows = new List<SkillPreviewRow>();
        private float _nextPreviewPoll;
        private bool _previewErrorLogged;
        private string _previewStatus = "无预览（等待她的回合）";

        /// <summary>Pulls the game's own per-target prediction for the Grave Robber's Thrown Dagger.</summary>
        private void UpdateSkillPreview()
        {
            if (Time.realtimeSinceStartup < _nextPreviewPoll)
            {
                return;
            }

            _nextPreviewPoll = Time.realtimeSinceStartup + 1f;

            SaveReader.Snapshot snapshot = _lastSnapshot;
            if (snapshot == null || !snapshot.InCombat)
            {
                _skillRows.Clear();
                _previewStatus = "（不在战斗中）";
                return;
            }

            // Whoever is currently choosing an action, and whatever skill they picked.
            uint performerGuid = 0;
            string skillId = null;

            try
            {
                object battle = GetMember(Assets.Code.Utils.SingletonMonoBehaviour<CombatBhv>.Instance, "m_Battle");
                if (battle != null)
                {
                    performerGuid = ToUInt(GetMember(battle, "CurrentActorGuid"));
                }

                ActorInstance actor = GetActorInstance(performerGuid);
                if (actor != null)
                {
                    skillId = actor.GetSelectedSkillId();
                }
            }
            catch (Exception ex)
            {
                if (!_previewErrorLogged)
                {
                    _previewErrorLogged = true;
                    Logger.LogWarning("read selected skill failed: " + ex.Message);
                }
            }

            if (performerGuid == 0)
            {
                _skillRows.Clear();
                _previewStatus = "（等待当前英雄行动）";
                return;
            }

            string performerName = LookupActorName(snapshot, performerGuid);

            if (string.IsNullOrEmpty(skillId))
            {
                _skillRows.Clear();
                _previewStatus = performerName + " / 未选择技能";
                return;
            }

            string skillName = SaveReader.SkillName(skillId) ?? skillId;
            bool performerIsEnemy = IsEnemy(snapshot, performerGuid);

            var rows = new List<SkillPreviewRow>();
            var rowByTarget = new Dictionary<uint, SkillPreviewRow>();

            foreach (SaveReader.TeamRow team in snapshot.Teams)
            {
                if (performerIsEnemy == (team.TeamIndex != 0))
                {
                    continue;
                }

                foreach (SaveReader.ActorRow actor in team.Actors)
                {
                    QuerySkillPreview query = QuerySkillPreview.Trigger(performerGuid, skillId, actor.Guid);
                    if (query == null || !query.IsValid)
                    {
                        continue;
                    }

                    foreach (SkillCalculation.ActorResult.SkillPreview preview in query.m_SkillPreviews)
                    {
                        // Only the TARGET result is the real hit; ADDITIONAL_EFFECTS /
                        // RIPOSTE / ACT_OUT results show up as 0-damage duplicates.
                        if (preview.m_ResultType != SkillCalculation.ResultType.TARGET)
                        {
                            continue;
                        }

                        uint previewTargetGuid = preview.m_TargetActorGuid;
                        SaveReader.ActorRow previewTarget = FindActor(snapshot, previewTargetGuid);
                        if (previewTarget == null)
                        {
                            continue;
                        }

                        SkillPreviewRow existing;
                        if (rowByTarget.TryGetValue(previewTargetGuid, out existing))
                        {
                            if (preview.m_DamageHigh > existing.DamageHigh)
                            {
                                existing.DamageLow = preview.m_DamageLow;
                                existing.DamageHigh = preview.m_DamageHigh;
                                existing.CritDamage = preview.m_CritDamage;
                                existing.CritChance = preview.m_CritChance;
                                existing.ToHitChance = preview.m_ToHitChance;
                                existing.IsDamageValid = preview.m_IsDamageValid;
                                existing.IsCritValid = preview.m_IsCritValid;
                                existing.IsToHitValid = preview.m_IsToHitValid;
                            }

                            continue;
                        }

                        var previewRow = new SkillPreviewRow
                        {
                            TargetName = FormatPosition(previewTarget),
                            TargetGuid = previewTargetGuid,
                            Position = previewTarget.Position,
                            FromTargetResult = true,
                            DamageLow = preview.m_DamageLow,
                            DamageHigh = preview.m_DamageHigh,
                            CritDamage = preview.m_CritDamage,
                            CritChance = preview.m_CritChance,
                            ToHitChance = preview.m_ToHitChance,
                            IsDamageValid = preview.m_IsDamageValid,
                            IsCritValid = preview.m_IsCritValid,
                            IsToHitValid = preview.m_IsToHitValid
                        };

                        rowByTarget[previewTargetGuid] = previewRow;
                        rows.Add(previewRow);
                    }
                }
            }

            _skillRows.Clear();
            rows.Sort((a, b) => a.Position.CompareTo(b.Position));

            _skillRows.AddRange(rows);

            ApplyExactRolls(performerGuid, skillId, _skillRows);
            PredictHovered();

            string statusName = performerIsEnemy ? ClassNameOf(performerGuid) + "（敌方）" : ClassNameOf(performerGuid);

            _previewStatus = rows.Count == 0
                ? statusName + " / " + skillName + "：无合法目标或尚未计算"
                : statusName + " / " + skillName;
        }

        private static bool IsEnemy(SaveReader.Snapshot snapshot, uint guid)
        {
            foreach (SaveReader.TeamRow team in snapshot.Teams)
            {
                foreach (SaveReader.ActorRow actor in team.Actors)
                {
                    if (actor.Guid == guid)
                    {
                        return team.TeamIndex != 0;
                    }
                }
            }

            return false;
        }
        /// <summary>Localized class name of an actor (falls back to the personal name).</summary>
        private string ClassNameOf(uint guid)
        {
            SaveReader.Snapshot s = _lastSnapshot;
            if (s != null)
            {
                foreach (SaveReader.TeamRow team in s.Teams)
                {
                    foreach (SaveReader.ActorRow actor in team.Actors)
                    {
                        if (actor.Guid == guid)
                        {
                            string name = SaveReader.ClassName(actor.ClassId);
                            return string.IsNullOrEmpty(name) ? actor.Name : name;
                        }
                    }
                }
            }

            return "#" + guid;
        }
        private static string PadLabel(string text, int width)
        {
            int used = 0;
            foreach (char c in text)
            {
                used += c > 0x2E80 ? 2 : 1;
            }

            return text + new string(' ', Mathf.Max(0, width - used));
        }
        private static int RankOf(SaveReader.Snapshot snapshot, uint guid)
        {
            SaveReader.ActorRow row = FindActorRow(snapshot, guid);
            return row == null ? 0 : row.Position + 1;
        }
        private static string LookupActorName(SaveReader.Snapshot snapshot, uint guid)
        {
            foreach (SaveReader.TeamRow team in snapshot.Teams)
            {
                foreach (SaveReader.ActorRow actor in team.Actors)
                {
                    if (actor.Guid == guid)
                    {
                        string className = SaveReader.ClassName(actor.ClassId);
                        return string.IsNullOrEmpty(className) ? (string.IsNullOrEmpty(actor.Name) ? actor.ClassId : actor.Name) : className;
                }
            }
            }

            return guid.ToString();
        }

        private static SaveReader.ActorRow FindActor(SaveReader.Snapshot snapshot, uint guid)
        {
            foreach (SaveReader.TeamRow team in snapshot.Teams)
            {
                foreach (SaveReader.ActorRow actor in team.Actors)
                {
                    if (actor.Guid == guid)
                    {
                        return actor;
                    }
                }
            }

            return null;
        }
        /// <summary>Enemies are shown left-to-right as 1号位..4号位 (save team position + 1).</summary>
        private static string FormatPosition(SaveReader.ActorRow actor)
        {
            if (actor.Position >= 0)
            {
                return (actor.Position + 1) + "号位";
            }

            return string.IsNullOrEmpty(actor.Name) ? actor.ClassId : actor.Name;
        }
        // ------------------------------------------------- exact rolls (reflection)

        private static readonly BindingFlags AnyInstance =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private static object GetMember(object target, string name)
        {
            if (target == null)
            {
                return null;
            }

            for (Type type = target.GetType(); type != null; type = type.BaseType)
            {
                FieldInfo field = type.GetField(name, AnyInstance);
                if (field != null)
                {
                    return field.GetValue(target);
                }

                PropertyInfo property = type.GetProperty(name, AnyInstance);
                if (property != null && property.CanRead && property.GetIndexParameters().Length == 0)
                {
                    return property.GetValue(target, null);
                }
            }

            return null;
        }

        /// <summary>CombatBhv.m_Battle → Battle.m_BattleStateMachine → m_BattleSkillCalculation.</summary>
        private object FindSkillCalculation()
        {
            try
            {
                object bhv = Assets.Code.Utils.SingletonMonoBehaviour<CombatBhv>.Instance;
                if (bhv == null)
                {
                    bhv = UnityEngine.Object.FindObjectOfType(typeof(CombatBhv));
                }

                object battle = GetMember(bhv, "m_Battle");
                object machine = GetMember(battle, "m_BattleStateMachine");
                return GetMember(machine, "m_BattleSkillCalculation");
            }
            catch (Exception ex)
            {
                if (!_previewErrorLogged)
                {
                    _previewErrorLogged = true;
                    Logger.LogWarning("find SkillCalculation failed: " + ex.Message);
                }

                return null;
            }
        }

        /// <summary>
        /// The game pre-rolls damage/crit for every (skill, target) at selection time and keeps
        /// them in SkillCalculation.m_PotentialResults; read those exact values per target.
        /// </summary>
        private void ApplyExactRolls(uint performerGuid, string skillId, List<SkillPreviewRow> rows)
        {
            if (rows.Count == 0)
            {
                return;
            }

            try
            {
                object calculation = FindSkillCalculation();
                var results = GetMember(calculation, "m_PotentialResults") as IDictionary;
                if (results == null)
                {
                    return;
                }

                foreach (DictionaryEntry entry in results)
                {
                    object input = entry.Key;
                    if (input == null)
                    {
                        continue;
                    }

                    uint performer = ToUInt(GetMember(input, "m_PerformerGuid"));
                    string selectedSkill = GetMember(input, "m_SelectedSkillId") as string;
                    if (performer != performerGuid || !string.Equals(selectedSkill, skillId, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var actorResults = GetMember(entry.Value, "ActorResults") as IEnumerable;
                    if (actorResults == null)
                    {
                        continue;
                    }

                    foreach (object actorResult in actorResults)
                    {
                        uint targetGuid = ToUInt(GetMember(actorResult, "m_TargetActorGuid"));

                        foreach (SkillPreviewRow row in rows)
                        {
                            if (!RowMatches(row, targetGuid))
                            {
                                continue;
                            }

                            object hitValue = GetMember(actorResult, "IsHit");

                            float exactDamage = ToFloat(GetMember(actorResult, "HealthDamage"));
                            if (!row.ExactDamage.HasValue || exactDamage > row.ExactDamage.Value)
                            {
                                row.ExactDamage = exactDamage;
                                row.ExactCrit = ToBool(GetMember(actorResult, "IsCrit"));
                                row.ExactHit = hitValue == null ? (bool?)null : ToBool(hitValue);
                            }

                            foreach (EffectLine line in CollectEffects(actorResult))
                            {
                                bool duplicate = false;
                                foreach (EffectLine known in row.Effects)
                                {
                                    if (known.Text == line.Text)
                                    {
                                        duplicate = true;
                                        break;
                                    }
                                }

                                if (!duplicate)
                                {
                                    row.Effects.Add(line);
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                if (!_previewErrorLogged)
                {
                    _previewErrorLogged = true;
                    Logger.LogWarning("read exact skill results failed: " + ex.Message);
                }
            }
        }

        // ------------------------------------------------- resist peek / prediction

        /// <summary>Reads the next RESIST values by cloning the stream state (the game's state is restored).</summary>
        /// <summary>Clone a stream, draw N values, restore the game state (works for any identifier).</summary>
        private static UnityEngine.Random.State _simSaved;
        private static bool _simValid;

        /// <summary>Loads a clone of the stream into UnityEngine.Random so draws mirror the game exactly.</summary>
        internal static string RoundStateJson;

        private static bool BeginSimulation(RandomIdentifier identifier)
        {
            try
            {
                if (!RandomContainer.GetHasSeed(identifier))
                {
                    return false;
                }

                string json = RandomContainer.GetStateString(identifier);
                UnityEngine.Random.State state;
                if (!TryParseRandomState(json, out state))
                {
                    return false;
                }

                _simSaved = UnityEngine.Random.state;
                _simValid = true;
                UnityEngine.Random.state = state;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void EndSimulation()
        {
            if (!_simValid)
            {
                return;
            }

            UnityEngine.Random.state = _simSaved;
            _simValid = false;
        }
        private static List<float> PeekValues(RandomIdentifier identifier, int count)
        {
            var values = new List<float>();
            if (count <= 0)
            {
                return values;
            }

            try
            {
                if (!RandomContainer.GetHasSeed(identifier))
                {
                    return values;
                }

                string json = RandomContainer.GetStateString(identifier);
                UnityEngine.Random.State state;
                if (!TryParseRandomState(json, out state))
                {
                    return values;
                }

                UnityEngine.Random.State saved = UnityEngine.Random.state;
                try
                {
                    UnityEngine.Random.state = state;
                    for (int i = 0; i < count; i++)
                    {
                        values.Add(UnityEngine.Random.value);
                    }
                }
                finally
                {
                    UnityEngine.Random.state = saved;
                }
            }
            catch
            {
                // prediction is optional
            }

            return values;
        }

        /// <summary>
        /// Predict the next enemy action.  The game's random controller does exactly:
        ///   1) WeightedBag(skill) .GetRandomValue(ACTOR_CONTROLLER)  -> skill
        ///   2) ListUtils.GetRandomValue(targets, ACTOR_CONTROLLER)  -> target
        /// so the next TWO values of the ACTOR_CONTROLLER stream decide it.
        /// </summary>
        private void UpdateEnemyIntent()
        {
            _enemyIntent = string.Empty;

            if (!_enemyIntentEnabled || _lastSnapshot == null || !_lastSnapshot.InCombat)
            {
                return;
            }

            try
            {
                if (_liveOrder == null || _liveOrder.Count == 0)
                {
                    return;
                }

                uint enemyGuid = 0;
                foreach (uint guid in _liveOrder)
                {
                    if (IsEnemy(_lastSnapshot, guid))
                    {
                        enemyGuid = guid;
                        break;
                    }
                }

                if (enemyGuid == 0)
                {
                    return;
                }

                ActorInstance enemy = GetActorInstance(enemyGuid);
                if (enemy == null)
                {
                    return;
                }

                object controller = GetMember(enemy, "m_ActorController");
                if (controller == null)
                {
                    return;
                }

                var skillIds = new List<string>();
                var skills = GetMember(enemy, "m_CombatSkills") as IEnumerable;
                if (skills != null)
                {
                    foreach (object skill in skills)
                    {
                        if (skill == null || !ToBool(GetMember(skill, "m_IsEquipped")))
                        {
                            continue;
                        }

                        string id = GetMember(skill, "m_SkillId") as string;
                        if (!string.IsNullOrEmpty(id))
                        {
                            skillIds.Add(id);
                        }
                    }
                }

                if (skillIds.Count == 0)
                {
                    return;
                }

                MethodInfo calculate = FindMethod(controller.GetType(), "CalculateValidSkillTargetEntries");
                if (calculate == null)
                {
                    return;
                }

                calculate.Invoke(controller, new object[] { skillIds, false });

                var entries = GetMember(controller, "m_SkillTargetEntries") as IEnumerable;
                if (entries == null)
                {
                    return;
                }

                var skills2 = new List<string>();
                var targetsPerEntry = new List<List<uint>>();
                foreach (object entry in entries)
                {
                    if (entry == null)
                    {
                        continue;
                    }

                    string skillId = GetMember(entry, "m_SkillId") as string;
                    var targets = GetMember(entry, "m_ValidTargetActorGuids") as IEnumerable;
                    var list = new List<uint>();
                    if (targets != null)
                    {
                        foreach (object target in targets)
                        {
                            list.Add(ToUInt(target));
                        }
                    }

                    if (!string.IsNullOrEmpty(skillId) && list.Count > 0)
                    {
                        skills2.Add(skillId);
                        targetsPerEntry.Add(list);
                    }
                }

                if (skills2.Count == 0)
                {
                    return;
                }

                List<float> rolls = PeekValues(RandomIdentifier.ACTOR_CONTROLLER, 2);
                if (rolls.Count < 2)
                {
                    return;
                }

                int entryIndex = Mathf.Clamp((int)(rolls[0] * skills2.Count), 0, skills2.Count - 1);
                List<uint> chosenTargets = targetsPerEntry[entryIndex];
                int targetIndex = Mathf.Clamp((int)(rolls[1] * chosenTargets.Count), 0, chosenTargets.Count - 1);
                uint targetGuid = chosenTargets[targetIndex];

                _enemyIntent = "敌方预判: " + LookupActorName(_lastSnapshot, enemyGuid)
                               + " → " + (SaveReader.SkillName(skills2[entryIndex]) ?? skills2[entryIndex])
                               + " → " + LookupActorName(_lastSnapshot, targetGuid);
            }
            catch
            {
                _enemyIntent = string.Empty;
            }
        }

        private static List<float> PeekResistValues(int count)
        {
            var values = new List<float>();
            if (count <= 0)
            {
                return values;
            }

            try
            {
                if (!RandomContainer.GetHasSeed(RandomIdentifier.RESIST))
                {
                    return values;
                }

                string json = RandomContainer.GetStateString(RandomIdentifier.RESIST);
                UnityEngine.Random.State state;
                if (!TryParseRandomState(json, out state))
                {
                    return values;
                }

                UnityEngine.Random.State saved = UnityEngine.Random.state;
                try
                {
                    UnityEngine.Random.state = state;
                    for (int i = 0; i < count; i++)
                    {
                        values.Add(UnityEngine.Random.value);
                    }
                }
                finally
                {
                    UnityEngine.Random.state = saved;
                }
            }
            catch
            {
                // prediction is optional
            }

            return values;
        }

        private static bool TryParseRandomState(string json, out UnityEngine.Random.State state)
        {
            state = default(UnityEngine.Random.State);

            if (string.IsNullOrEmpty(json))
            {
                return false;
            }

            try
            {
                Newtonsoft.Json.Linq.JObject obj = Newtonsoft.Json.Linq.JObject.Parse(json);
                FieldInfo[] fields = typeof(UnityEngine.Random.State).GetFields(AnyInstance);

                object boxed = state;
                foreach (FieldInfo field in fields)
                {
                    Newtonsoft.Json.Linq.JToken token = obj[field.Name];
                    if (token != null)
                    {
                        field.SetValue(boxed, token.ToObject<int>());
                    }
                }

                state = (UnityEngine.Random.State)boxed;
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>For the actor under the cursor, turn the resist percentages into a yes/no verdict.</summary>
        private void PredictHovered()
        {
            if (_skillRows.Count == 0)
            {
                return;
            }

            try
            {
                uint hovered = QueryHoveredActor.Trigger().m_actorGuid;
                if (hovered == 0)
                {
                    return;
                }

                SkillPreviewRow row = null;
                foreach (SkillPreviewRow candidate in _skillRows)
                {
                    if (candidate.TargetGuid == hovered)
                    {
                        row = candidate;
                        break;
                    }
                }

                if (row == null || row.Effects.Count == 0)
                {
                    return;
                }

                List<float> rolls = PeekResistValues(row.Effects.Count);

                for (int i = 0; i < row.Effects.Count; i++)
                {
                    EffectLine line = row.Effects[i];
                    if (line.Resist < 0f || i >= rolls.Count)
                    {
                        line.PredictedResisted = null;
                        continue;
                    }

                    line.PredictedRoll = rolls[i];
                    line.PredictedResisted = rolls[i] < line.Resist;
                }
            }
            catch (Exception ex)
            {
                if (!_previewErrorLogged)
                {
                    _previewErrorLogged = true;
                    Logger.LogWarning("resist prediction failed: " + ex.Message);
                }
            }
        }
        private ActorInstance GetActorInstance(uint guid)
        {
            if (guid == 0)
            {
                return null;
            }

            try
            {
                object bhv = Assets.Code.Utils.SingletonMonoBehaviour<CombatBhv>.Instance;
                object battle = GetMember(bhv, "m_Battle");
                object teams = GetMember(battle, "m_BattleTeams");
                if (teams == null)
                {
                    return null;
                }

                MethodInfo method = teams.GetType().GetMethod("GetActorFromActorGuid", new[] { typeof(uint) });
                return method == null ? null : method.Invoke(teams, new object[] { guid }) as ActorInstance;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// The game rolls one RESIST value per effect and resists when roll &lt; max(resist),
        /// so the land chance is 1 - max(resist). Mirrors ResistCalculation.Check/Clamp.
        /// </summary>
        private static float ComputeResist(object effectInstance, ActorInstance target, ActorInstance performer)
        {
            var instance = effectInstance as EffectInstance;
            if (instance == null || target == null || performer == null)
            {
                return -1f;
            }

            try
            {
                IReadOnlyList<ResistDefinition> resists =
                    ResistCalculation.GetResistsForEffectInstance(instance, target, instance.SourceType);

                if (resists == null || resists.Count == 0)
                {
                    return -1f;
                }

                bool isCrit = instance.IsCritForResistance || instance.IsCrit;
                float worst = 0f;

                foreach (ResistDefinition resistDefinition in resists)
                {
                    if (resistDefinition.m_ActorStatType == default(ActorStatType))
                    {
                        continue;
                    }

                    float resist = target.GetUnclampedStatValue(resistDefinition.m_ActorStatType, resistDefinition.m_ActorStatSubType);

                    if (isCrit)
                    {
                        resist += resistDefinition.m_CritMod;
                    }

                    if (performer.ActorGuid != target.ActorGuid
                        && resistDefinition.m_IgnoreActorStatType != default(ActorStatType))
                    {
                        resist -= performer.GetUnclampedStatValue(resistDefinition.m_IgnoreActorStatType, resistDefinition.m_ActorStatSubType);
                    }

                    resist = ResistCalculation.Clamp(resist, resistDefinition, target);
                    if (resist > worst)
                    {
                        worst = resist;
                    }
                }

                return Mathf.Clamp01(worst);
            }
            catch
            {
                return -1f;
            }
        }
        /// <summary>Effects the precomputed result will try to apply (DOTs / tokens / heals).</summary>
        private List<EffectLine> CollectEffects(object actorResult)
        {
            var items = new List<EffectLine>();

            try
            {
                ActorInstance performerActor = GetActorInstance(ToUInt(GetMember(actorResult, "m_PerformerActorGuid")));
                object container = GetMember(actorResult, "m_AppliedEffectsOutputContainer");
                var outputs = GetMember(container, "Outputs") as IEnumerable;
                if (outputs == null)
                {
                    return items;
                }

                foreach (object output in outputs)
                {
                    ActorInstance targetActor = GetMember(output, "m_TargetActor") as ActorInstance;
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

                        float resist = ComputeResist(instance, targetActor, performerActor);

                        string dotId = GetMember(definition, "m_DotAddId") as string;
                        if (!string.IsNullOrEmpty(dotId))
                        {
                            int dotAmount = ToInt(GetMember(instance, "DotAddAmount"));
                            string dot = SaveReader.DescribeDotApplication(dotId, dotAmount);
                            if (!string.IsNullOrEmpty(dot))
                            {
                                items.Add(new EffectLine { Text = dot, Resist = resist });
                            }
                            continue;
                        }

                        string tokenId = GetMember(definition, "m_TokenAddId") as string;
                        if (!string.IsNullOrEmpty(tokenId))
                        {
                            int tokenAmount = ToInt(GetMember(instance, "TokenAddAmount"));
                            string token = SaveReader.DescribeTokenApplication(tokenId);
                            if (!string.IsNullOrEmpty(token))
                            {
                                items.Add(new EffectLine { Text = tokenAmount > 1 ? token + "×" + tokenAmount : token, Resist = resist });
                            }
                            continue;
                        }

                        float heal = ToFloat(GetMember(instance, "HealthHealAmount"));
                        if (heal > 0f)
                        {
                            items.Add(new EffectLine { Text = "治疗" + heal.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture), Resist = resist });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                if (!_previewErrorLogged)
                {
                    _previewErrorLogged = true;
                    Logger.LogWarning("read effect instances failed: " + ex.Message);
                }
            }

            return items;
        }

        private static int ToInt(object value)
        {
            try
            {
                return value == null ? 0 : Convert.ToInt32(value);
            }
            catch
            {
                return 0;
            }
        }

        private static bool RowMatches(SkillPreviewRow row, uint targetGuid)
        {
            return row.TargetGuid == targetGuid;
        }

        private static uint ToUInt(object value)
        {
            try
            {
                return value == null ? 0u : Convert.ToUInt32(value);
            }
            catch
            {
                return 0u;
            }
        }

        private static float ToFloat(object value)
        {
            try
            {
                return value == null ? 0f : Convert.ToSingle(value);
            }
            catch
            {
                return 0f;
            }
        }

        private static bool ToBool(object value)
        {
            try
            {
                return value != null && Convert.ToBoolean(value);
            }
            catch
            {
                return false;
            }
        }

        // ------------------------------------------------- live combat state

        /// <summary>Keeps round/turn/queue updated. The combat save is only written when combat
        /// starts, so the live query is the only way to see the current turn order.</summary>
        private void UpdateLiveCombat()
        {
            EnsureLiveListeners();

            if (Time.realtimeSinceStartup < _nextLivePoll)
            {
                return;
            }

            _nextLivePoll = Time.realtimeSinceStartup + 1f;
            PollBattleState();
            PollTurnOrder();
            UpdateEnemyIntent();
            RandomTrace.Tick();
            UpdatePlayerPrediction();
            UpdatePredictions();
            LogActualAction();
            LearnOffset();
            LogDiagnostics();
            LogPredictSteps();
        }

        private void EnsureLiveListeners()
        {
            if (Time.realtimeSinceStartup < _nextListenerCheck)
            {
                return;
            }

            _nextListenerCheck = Time.realtimeSinceStartup + 10f;

            try
            {
                // The game may clear its event tables between battles, so re-add when empty.
                bool missing = !_listenersAdded
                               || EventManager.GetListenerNames<EventBattleStartRound>().Count == 0;

                if (missing)
                {
                    EventManager.AddListener<EventBattleStartRound>(OnBattleStartRound);
                    EventManager.AddListener<EventBattleIncrementTurn>(OnBattleIncrementTurn);
                    _listenersAdded = true;
                }
            }
            catch (Exception ex)
            {
                if (!_liveErrorLogged)
                {
                    _liveErrorLogged = true;
                    Logger.LogWarning("live combat listeners failed: " + ex.Message);
                }
            }
        }

        private void OnBattleStartRound(EventBattleStartRound evt)
        {
            _liveRound = evt.m_Round;
            _liveTurn = -1;
            _liveTotalTurns = -1;
            PollTurnOrder();
        }

        private void OnBattleIncrementTurn(EventBattleIncrementTurn evt)
        {
            _liveTurn = evt.CurrentTurn;
            _liveTotalTurns = evt.TotalTurns;
            PollTurnOrder();
        }

        /// <summary>Round/turn straight from Battle (works even when the round-start event was missed).</summary>
        /// <summary>Full turn order of the current round (m_BattleTurnOrder.m_TurnOrder).</summary>
        /// <summary>Full turn order of the current round; logs what it actually read.</summary>
        private List<uint> GetFullTurnOrder()
        {
            var result = new List<uint>();

            try
            {
                object battle = GetMember(Assets.Code.Utils.SingletonMonoBehaviour<CombatBhv>.Instance, "m_Battle");
                object order = battle == null ? null : GetMember(battle, "m_BattleTurnOrder");

                if (order != null)
                {
                    object raw = GetMember(order, "m_TurnOrder");
                    if (raw == null)
                    {
                        raw = GetMember(order, "TurnOrder");
                    }

                    var entries = raw as IEnumerable;
                    if (entries != null)
                    {
                        foreach (object entry in entries)
                        {
                            if (entry == null)
                            {
                                continue;
                            }

                            uint guid = 0;

                            if (entry is uint)
                            {
                                guid = (uint)entry;
                            }
                            else
                            {
                                guid = ToUInt(GetMember(entry, "m_ActorGuid"));
                                if (guid == 0)
                                {
                                    guid = ToUInt(GetMember(entry, "ActorGuid"));
                                }
                            }

                            if (guid != 0)
                            {
                                result.Add(guid);
                            }
                        }
                    }
                }
            }
            catch
            {
                // fall through
            }

            if (result.Count == 0 && _liveOrder != null)
            {
                result.AddRange(_liveOrder);
            }

            Logger.LogInfo("ORDER: [" + string.Join(",", result.ConvertAll(g => g.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray()) + "]");
            return result;
        }
        private void UpdatePredictions()
        {
            _predictLines.Clear();
            SaveReader.Snapshot s = _lastSnapshot;
            if (!_enemyIntentEnabled || s == null || !s.InCombat)
            {
                return;
            }

            try
            {
                if (_liveOrder == null || _liveOrder.Count == 0)
                {
                    return;
                }

                uint currentGuid = 0;
                object battle = GetMember(Assets.Code.Utils.SingletonMonoBehaviour<CombatBhv>.Instance, "m_Battle");
                if (battle != null)
                {
                    currentGuid = ToUInt(GetMember(battle, "CurrentActorGuid"));
                }

                List<uint> fullOrder = GetFullTurnOrder();
                int currentIndex = 0;
                for (int i = 0; i < fullOrder.Count; i++)
                {
                    if (fullOrder[i] == currentGuid)
                    {
                        currentIndex = i;
                        break;
                    }
                }
                if (IsEnemy(s, currentGuid))
                {
                    // during an enemy turn the player snapshot is stale: skip predicting
                    _predictLines.Add("（敌方回合：等待我方下一次选择）");
                    return;
                }

                // ACTOR_CONTROLLER is AI-only: every remaining enemy this round can be predicted
                var predicted = new List<uint>();
                for (int i = currentIndex + 1; i < fullOrder.Count; i++)
                {
                    if (IsEnemy(s, fullOrder[i]))
                    {
                        predicted.Add(fullOrder[i]);
                    }
                }

                ActorInstance currentActor = GetActorInstance(currentGuid);
                string currentSkill = currentActor == null ? null : currentActor.GetSelectedSkillId();
                string turnKey = s.LiveRound + "/" + s.LiveTurn;

                // reset when the turn changes; capture ONLY when a skill is actually selected
                if (turnKey != _selTurnKey)
                {
                    _selTurnKey = turnKey;
                    _selKey = string.Empty;
                    _selPeek = null;
                    _selCalc = null;
                }

                string selKey = turnKey + "/" + (currentSkill ?? "-");
                // only snapshot during OUR turn: an enemy's own selection already consumed its values
                bool currentIsPlayer = !IsEnemy(s, currentGuid);
                if (currentIsPlayer && !string.IsNullOrEmpty(currentSkill) && selKey != _selKey)
                {
                    _selKey = selKey;
                    _selPeek = PeekValues(RandomIdentifier.ACTOR_CONTROLLER, Mathf.Max(8, predicted.Count * 2));
                    _selCalc = PeekValues(RandomIdentifier.SKILL_CALCULATION, Mathf.Max(8, predicted.Count * 2));
                    string acList = _selPeek == null ? "-" : string.Join(",", _selPeek.GetRange(0, Mathf.Min(6, _selPeek.Count)).ConvertAll(v => v.ToString("F4", System.Globalization.CultureInfo.InvariantCulture)).ToArray());
                    Logger.LogInfo("SNAPSHOT: key=" + selKey
                                   + " ac=" + (_selPeek == null ? 0 : _selPeek.Count)
                                   + " calc=" + (_selCalc == null ? 0 : _selCalc.Count)
                                   + " vals=[" + acList + "]");
                }

                string roundKey = s.LiveRound.ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (roundKey != _roundKey)
                {
                    _roundKey = roundKey;
                    _roundPeek = PeekValues(RandomIdentifier.ACTOR_CONTROLLER, 16);
                    try { ForeseekerPlugin.RoundStateJson = RandomContainer.GetStateString(RandomIdentifier.ACTOR_CONTROLLER); } catch { ForeseekerPlugin.RoundStateJson = null; }
                    _selPeek = _roundPeek;
                    Logger.LogInfo("ROUNDPEEK: round=" + roundKey
                                   + " vals=[" + (_roundPeek == null ? "-" : string.Join(",", _roundPeek.ConvertAll(v => v.ToString("F4", System.Globalization.CultureInfo.InvariantCulture)).ToArray())) + "]");
                }

                List<float> rolls = _roundPeek ?? _selPeek ?? new List<float>();
                if (rolls.Count < 2)
                {
                    return;
                }

                var hp = new Dictionary<uint, double>();
                foreach (SaveReader.TeamRow team in s.Teams)
                {
                    foreach (SaveReader.ActorRow actor in team.Actors)
                    {
                        hp[actor.Guid] = actor.Hp;
                    }
                }

                _predictLines.Add(string.Empty);

                int enemyBefore = 0;
                for (int i = 0; i <= currentIndex && i < fullOrder.Count; i++)
                {
                    if (IsEnemy(s, fullOrder[i]))
                    {
                        enemyBefore++;
                    }
                }

                BeginSimulation(RandomIdentifier.ACTOR_CONTROLLER);
                try
                {
                    foreach (uint guid in predicted)
                    {
                        string line = PredictEnemyAction(s, guid, hp);
                        if (line != null && fullOrder.IndexOf(guid) > currentIndex)
                        {
                            _predictLines.Add(line);
                            Logger.LogInfo("PRED_ROW: idx=" + fullOrder.IndexOf(guid) + " cur=" + currentIndex + " " + line.Trim());
                        }
                    }
                }
                finally
                {
                    EndSimulation();
                }
            }
            catch
            {
                _predictLines.Clear();
            }
        }

        // enemy classes that actually set m_RandomSelectChance (all others use 1.0)
        private static readonly HashSet<string> BiasedEnemyClasses = new HashSet<string>(StringComparer.Ordinal)
        {
            "beastmen_tribecaller", "cave_swine_skulker", "courtier_esquire", "courtier_sycophant",
            "coven_custode", "coven_dedicant", "coven_hateful_virago", "shared_ghoul"
        };

        private static readonly Dictionary<string, float> SkillWeightById = new Dictionary<string, float>(StringComparer.Ordinal);
        private static readonly HashSet<string> WeightScannedClasses = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>Weight of a skill for the random controller. 1.0 unless the class data overrides it.</summary>
        private static float GetSkillWeight(string classId, string skillId)
        {
            if (string.IsNullOrEmpty(classId) || string.IsNullOrEmpty(skillId))
            {
                return 1f;
            }

            if (!BiasedEnemyClasses.Contains(classId))
            {
                return 1f;
            }

            if (!WeightScannedClasses.Contains(classId))
            {
                WeightScannedClasses.Add(classId);

                try
                {
                    string path = GetDataFilePath(classId);
                    if (File.Exists(path))
                    {
                        string currentSkill = null;

                        foreach (string raw in File.ReadLines(path, System.Text.Encoding.UTF8))
                        {
                            string line = raw.Trim();
                            if (line.Length == 0)
                            {
                                continue;
                            }

                            string[] cells = line.Split(',');

                            if (cells[0] == "element_start")
                            {
                                currentSkill = cells.Length > 2 && cells[2] == "ActorDataSkill" ? cells[1] : null;
                                continue;
                            }

                            if (cells[0] == "element_end")
                            {
                                currentSkill = null;
                                continue;
                            }

                            if (currentSkill != null && cells[0] == "m_RandomSelectChance" && cells.Length > 1)
                            {
                                float value;
                                if (float.TryParse(cells[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out value))
                                {
                                    SkillWeightById[currentSkill] = value;
                                }
                            }
                        }
                    }
                }
                catch
                {
                    // fall back to 1
                }
            }

            float cached;
            return SkillWeightById.TryGetValue(skillId, out cached) ? cached : 1f;
        }

        /// <summary>Class id of an actor row (used to pick the right enemy data file).</summary>
        /// <summary>GetMethod does not return a base class's private members, so walk the hierarchy.</summary>
        private static MethodInfo FindMethod(Type type, string name)
        {
            for (Type current = type; current != null; current = current.BaseType)
            {
                MethodInfo method = current.GetMethod(name, AnyInstance);
                if (method != null)
                {
                    return method;
                }
            }

            return null;
        }
        private static string ClassIdOf(SaveReader.Snapshot s, uint guid)
        {
            foreach (SaveReader.TeamRow team in s.Teams)
            {
                foreach (SaveReader.ActorRow actor in team.Actors)
                {
                    if (actor.Guid == guid)
                    {
                        return actor.ClassId;
                    }
                }
            }

            return null;
        }

        /// <summary>Target currently holds a block token (halves the next hit and is consumed).</summary>
        private static bool HasToken(SaveReader.Snapshot s, uint guid, string tokenId)
        {
            foreach (SaveReader.TeamRow team in s.Teams)
            {
                foreach (SaveReader.ActorRow actor in team.Actors)
                {
                    if (actor.Guid != guid)
                    {
                        continue;
                    }

                    foreach (string token in actor.Tokens)
                    {
                        if (token == tokenId)
                        {
                            return true;
                        }
                    }

                    return false;
                }
            }

            return false;
        }
        private static bool HasBlockToken(SaveReader.Snapshot s, uint guid)
        {
            foreach (SaveReader.TeamRow team in s.Teams)
            {
                foreach (SaveReader.ActorRow actor in team.Actors)
                {
                    if (actor.Guid != guid)
                    {
                        continue;
                    }

                    foreach (string token in actor.Tokens)
                    {
                        if (token == "block")
                        {
                            return true;
                        }
                    }

                    return false;
                }
            }

            return false;
        }
        // ---- candidate enumeration from the enemy's own data (no game-side API needed)

        private sealed class SkillRule
        {
            public string Launch;
            public string Targets;
            public bool Friendly;
            public float Weight = 1f;
            public float Damage;
            public float DamageRange;
            public float CritChance;
        }

        private static readonly Dictionary<string, Dictionary<string, SkillRule>> RulesByClass =
            new Dictionary<string, Dictionary<string, SkillRule>>(StringComparer.Ordinal);

        private static readonly Dictionary<string, string> DataFileByClass = new Dictionary<string, string>(StringComparer.Ordinal);
        private static bool _dataFileIndexBuilt;

        /// <summary>Maps actor class id (e.g. shared_lost_soul) to its data CSV (shared_gaunt_lost_soul_data_export...).</summary>
        private static string GetDataFilePath(string classId)
        {
            if (string.IsNullOrEmpty(classId))
            {
                return null;
            }

            if (!_dataFileIndexBuilt)
            {
                _dataFileIndexBuilt = true;

                try
                {
                    string dir = Path.Combine(Application.dataPath, "StreamingAssets", "Excel");
                    foreach (string file in Directory.GetFiles(dir, "*_data_export.Group.csv"))
                    {
                        try
                        {
                            foreach (string raw in File.ReadLines(file, System.Text.Encoding.UTF8))
                            {
                                string line = raw.Trim();
                                if (line.Length == 0)
                                {
                                    continue;
                                }

                                string[] cells = line.Split(',');
                                if (cells[0] == "element_start" && cells.Length > 2 && cells[2] == "ActorDataClass")
                                {
                                    if (!DataFileByClass.ContainsKey(cells[1]))
                                    {
                                        DataFileByClass[cells[1]] = file;
                                    }
                                }
                            }
                        }
                        catch
                        {
                            // ignore a bad file
                        }
                    }
                }
                catch
                {
                    // index is best-effort
                }

                // index built (silent)
            }

            string exact;
            if (DataFileByClass.TryGetValue(classId, out exact))
            {
                return exact;
            }

            return null;
        }
        private static Dictionary<string, SkillRule> GetRules(string classId)
        {
            Dictionary<string, SkillRule> rules;
            if (RulesByClass.TryGetValue(classId, out rules))
            {
                return rules;
            }

            rules = new Dictionary<string, SkillRule>(StringComparer.Ordinal);
            RulesByClass[classId] = rules;

            try
            {
                string path = GetDataFilePath(classId);
                if (!File.Exists(path))
                {
                    return rules;
                }

                SkillRule current = null;
                string currentId = null;
                string statsSkill = null;
                string[] keys = new string[0];
                var statsPending = new Dictionary<string, float[]>(StringComparer.Ordinal);

                foreach (string raw in File.ReadLines(path, System.Text.Encoding.UTF8))
                {
                    string line = raw.Trim();
                    if (line.Length == 0)
                    {
                        continue;
                    }

                    string[] cells = line.Split(',');

                    if (cells[0] == "element_start")
                    {
                        string elementId = cells.Length > 1 ? cells[1] : null;
                        string elementType = cells.Length > 2 ? cells[2] : null;

                        if (elementType == "ActorDataSkill")
                        {
                            currentId = elementId;
                            current = new SkillRule();
                        }
                        else
                        {
                            currentId = null;
                            current = null;
                        }

                        statsSkill = elementType == "ActorDataStats" ? elementId : null;
                        continue;
                    }

                    if (cells[0] == "element_end")
                    {
                        // ActorDataStats follows ActorDataSkill in the CSV, so merge here.
                        if (statsSkill != null)
                        {
                            float[] lateStats;
                            if (statsPending.TryGetValue(statsSkill, out lateStats))
                            {
                                SkillRule lateRule;
                                if (!rules.TryGetValue(statsSkill, out lateRule))
                                {
                                    lateRule = new SkillRule();
                                    rules[statsSkill] = lateRule;
                                }

                                lateRule.Damage = lateStats[0];
                                lateRule.DamageRange = lateStats[1];
                                lateRule.CritChance = lateStats[2];
                            }
                        }

                        if (currentId != null && current != null)
                        {
                            float[] stats;
                            if (statsPending.TryGetValue(currentId, out stats))
                            {
                                current.Damage = stats[0];
                                current.DamageRange = stats[1];
                                current.CritChance = stats[2];
                            }

                            rules[currentId] = current;
                        }

                        currentId = null;
                        current = null;
                        statsSkill = null;
                        continue;
                    }

                    if (current == null && statsSkill == null)
                    {
                        continue;
                    }

                    switch (cells[0])
                    {
                        case "launch_ranks":
                            if (current != null) { current.Launch = string.Join(",", cells, 1, cells.Length - 1); }
                            break;
                        case "target_ranks":
                            if (current != null) { current.Targets = string.Join(",", cells, 1, cells.Length - 1); }
                            break;
                        case "m_IsFriendly":
                            if (current != null) { current.Friendly = cells.Length > 1 && cells[1] == "True"; }
                            break;
                        case "key_map":
                            keys = string.Join(",", cells, 1, cells.Length - 1).Split(',');
                            break;
                        case "add_stats":
                            {
                                if (statsSkill == null)
                                {
                                    break;
                                }

                                string[] values = string.Join(",", cells, 1, cells.Length - 1).Split(',');
                                var stats = new float[3];

                                for (int k = 0; k < keys.Length && k < values.Length; k++)
                                {
                                    string key = keys[k].Trim();
                                    float number;
                                    if (!float.TryParse(values[k], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out number))
                                    {
                                        continue;
                                    }

                                    if (key == "health_damage") { stats[0] = number; }
                                    else if (key == "health_damage_range") { stats[1] = number; }
                                    else if (key == "crit_chance") { stats[2] = number; }
                                }

                                statsPending[statsSkill] = stats;
                                break;
                            }
                        case "m_RandomSelectChance":
                            if (current != null && cells.Length > 1)
                            {
                                float value;
                                current.Weight = float.TryParse(cells[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out value) ? value : 1f;
                            }

                            break;
                    }
                }

            }
            catch
            {
                // rules are optional
            }

            return rules;
        }

        private static bool RankListContains(string csv, int rank)
        {
            if (string.IsNullOrEmpty(csv))
            {
                return false;
            }

            foreach (string part in csv.Split(','))
            {
                int value;
                if (int.TryParse(part.Trim(), out value) && value == rank)
                {
                    return true;
                }
            }

            return false;
        }

        private string PredictEnemyAction(SaveReader.Snapshot s, uint enemyGuid, Dictionary<uint, double> hp)
        {
            SaveReader.ActorRow enemyRow = FindActorRow(s, enemyGuid);
            if (enemyRow == null)
            {
                return null;
            }

            string classId = FirstClassIdOf(enemyRow.ClassId);
            Dictionary<string, SkillRule> rules = GetRules(classId);

            ActorInstance enemy = GetActorInstance(enemyGuid);
            var skillIds = new List<string>();
            var skills = enemy == null ? null : GetMember(enemy, "m_CombatSkills") as IEnumerable;
            if (skills != null)
            {
                foreach (object skill in skills)
                {
                    if (skill == null || !ToBool(GetMember(skill, "m_IsEquipped")))
                    {
                        continue;
                    }

                    string id = GetMember(skill, "m_SkillId") as string;
                    if (!string.IsNullOrEmpty(id))
                    {
                        skillIds.Add(id);
                    }
                }
            }

            int enemyRank = enemyRow.Position + 1;
            int enemyTeam = TeamIndexOf(s, enemyGuid);

            var entrySkills = new List<string>();
            var entryTargets = new List<uint>();
            var entryWeights = new List<float>();

            foreach (string skillId in skillIds)
            {
                SkillRule rule;
                if (!rules.TryGetValue(skillId, out rule) || !RankListContains(rule.Launch, enemyRank))
                {
                    continue;
                }

                foreach (SaveReader.TeamRow team in s.Teams)
                {
                    bool wantsThisTeam = rule.Friendly ? team.TeamIndex == enemyTeam : team.TeamIndex != enemyTeam;
                    if (!wantsThisTeam)
                    {
                        continue;
                    }

                    var orderedActors = new List<SaveReader.ActorRow>(team.Actors);
                    orderedActors.Sort((a, b) => a.Position.CompareTo(b.Position));   // game enumerates targets from rank 1 outwards
                    foreach (SaveReader.ActorRow actor in orderedActors)
                    {
                        if (actor.Guid == enemyGuid || !RankListContains(rule.Targets, actor.Position + 1))
                        {
                            continue;
                        }

                        entrySkills.Add(skillId);
                        entryTargets.Add(actor.Guid);
                        entryWeights.Add(rule.Weight);
                    }
                }
            }

            Logger.LogInfo("PREDICT2: class=" + classId + " rank=" + enemyRank + " skills=" + skillIds.Count + " entries=" + entrySkills.Count);

            if (entrySkills.Count == 0)
            {
                return null;
            }

            float total = 0f;
            foreach (float w in entryWeights)
            {
                total += w;
            }

            // replicate the game's draws on a cloned stream: Random.value then Random.Range
            float pickRoll = UnityEngine.Random.value;
            int chosen = entrySkills.Count - 1;
            float acc = pickRoll * total;
            for (int i = 0; i < entrySkills.Count; i++)
            {
                acc -= entryWeights[i];
                if (acc < 0f)
                {
                    chosen = i;
                    break;
                }
            }

            string chosenSkill = entrySkills[chosen];

            var sameTargets = new List<uint>();
            for (int i = 0; i < entrySkills.Count; i++)
            {
                if (entrySkills[i] == chosenSkill)
                {
                    sameTargets.Add(entryTargets[i]);
                }
            }

            int targetIndex = sameTargets.Count <= 1 ? 0 : UnityEngine.Random.Range(0, sameTargets.Count);
            targetIndex = Mathf.Clamp(targetIndex, 0, sameTargets.Count - 1);
            uint targetGuid = sameTargets[targetIndex];

            SkillRule chosenRule;
            rules.TryGetValue(chosenSkill, out chosenRule);

            int actionIndex = _actionCounter;
            _actionCounter++;
            List<float> calcRolls = _selCalc ?? new List<float>();
            int baseIndex = actionIndex * 2;
            float critRoll = calcRolls.Count > baseIndex ? calcRolls[baseIndex] : -1f;
            float damageRoll = calcRolls.Count > baseIndex + 1 ? calcRolls[baseIndex + 1] : -1f;

            Logger.LogInfo("PREDICT3: " + chosenSkill
                           + " base=" + (chosenRule == null ? "null" : chosenRule.Damage.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture))
                           + " range=" + (chosenRule == null ? "null" : chosenRule.DamageRange.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture))
                           + " crit=" + (chosenRule == null ? "null" : chosenRule.CritChance.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture))
                           + " critRoll=" + critRoll.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)
                           + " dmgRoll=" + (damageRoll >= 0f ? damageRoll.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) : "none")
                           + " calcCount=" + calcRolls.Count
                           + " rules=" + rules.Count
                           + " rules=" + rules.Count
                           + " pick=" + pickRoll.ToString("F4", System.Globalization.CultureInfo.InvariantCulture)
                           + " list=" + EntryList(entrySkills, entryTargets)
                           + " entries=" + entrySkills.Count
                           + " tcount=" + sameTargets.Count
                           + " tidx=" + targetIndex
                           + " target=" + targetGuid);

            float damage = chosenRule == null ? 0f : chosenRule.Damage;
            if (chosenRule != null && damageRoll >= 0f)
            {
                damage += Mathf.Round(damageRoll * chosenRule.DamageRange);
            }

            // weak / strength / other damage modifiers are folded into these stats
            if (enemy != null)
            {
                damage *= enemy.GetClampedStatValue(ActorStatType.HEALTH_DAMAGE_DEALT_PERCENT);
                damage *= enemy.GetClampedStatValue(ActorStatType.HEALTH_DAMAGE_DEALT_MULT_PERCENT);
            }

            // weak / strength are consumed at cast time into damage buffs (x0.5 / x1.5)
            if (HasToken(s, enemyGuid, "strength"))
            {
                damage *= 1.5f;
            }
            else if (HasToken(s, enemyGuid, "weak"))
            {
                damage *= 0.5f;
            }

            ActorInstance targetActor = GetActorInstance(targetGuid);
            if (targetActor != null)
            {
                damage *= targetActor.GetClampedStatValue(ActorStatType.HEALTH_DAMAGE_RECEIVED_PERCENT);
            }


            bool crit = chosenRule != null && critRoll >= 0f && critRoll < chosenRule.CritChance;
            if (crit)
            {
                damage *= 1.5f;
            }

            bool blocked = HasBlockToken(s, targetGuid);
            float dealt = blocked ? damage * 0.5f : damage;
            bool wasWeak = HasToken(s, enemyGuid, "weak") && !HasToken(s, enemyGuid, "strength");
            if (hp.ContainsKey(targetGuid))
            {
                hp[targetGuid] = hp[targetGuid] - dealt;
            }

            Logger.LogInfo("PRED_ENEMY: guid=" + enemyGuid
                           + " pick=" + pickRoll.ToString("F4", System.Globalization.CultureInfo.InvariantCulture)
                           + " skill=" + chosenSkill
                           + " tcount=" + sameTargets.Count
                           + " tidx=" + targetIndex
                           + " target=" + targetGuid);
            string enemyLabel = LookupActorName(s, enemyGuid) + "（" + RankOf(s, enemyGuid) + "）";
            return PadLabel(enemyLabel, 16) + "攻击 " + RankOf(s, targetGuid) + "号位";

        }
        private void BuildEntriesFor(SaveReader.Snapshot s, uint enemyGuid, List<string> skills, List<uint> targets, List<float> weights)
        {
            SaveReader.ActorRow enemyRow = FindActorRow(s, enemyGuid);
            if (enemyRow == null)
            {
                return;
            }

            Dictionary<string, SkillRule> rules = GetRules(FirstClassIdOf(enemyRow.ClassId));
            ActorInstance enemy = GetActorInstance(enemyGuid);
            var skillIds = new List<string>();
            var list = enemy == null ? null : GetMember(enemy, "m_CombatSkills") as IEnumerable;
            if (list != null)
            {
                foreach (object skill in list)
                {
                    if (skill == null || !ToBool(GetMember(skill, "m_IsEquipped")))
                    {
                        continue;
                    }

                    string id = GetMember(skill, "m_SkillId") as string;
                    if (!string.IsNullOrEmpty(id))
                    {
                        skillIds.Add(id);
                    }
                }
            }

            int enemyRank = enemyRow.Position + 1;
            int enemyTeam = TeamIndexOf(s, enemyGuid);

            foreach (string skillId in skillIds)
            {
                SkillRule rule;
                if (!rules.TryGetValue(skillId, out rule) || !RankListContains(rule.Launch, enemyRank))
                {
                    continue;
                }

                foreach (SaveReader.TeamRow team in s.Teams)
                {
                    bool wants = rule.Friendly ? team.TeamIndex == enemyTeam : team.TeamIndex != enemyTeam;
                    if (!wants)
                    {
                        continue;
                    }

                    var orderedActors = new List<SaveReader.ActorRow>(team.Actors);
                    orderedActors.Sort((a, b) => a.Position.CompareTo(b.Position));   // game enumerates targets from rank 1 outwards
                    foreach (SaveReader.ActorRow actor in orderedActors)
                    {
                        if (actor.Guid == enemyGuid || !RankListContains(rule.Targets, actor.Position + 1))
                        {
                            continue;
                        }

                        skills.Add(skillId);
                        targets.Add(actor.Guid);
                        weights.Add(rule.Weight);
                    }
                }
            }
        }

        /// <summary>Reads the target the game actually stored for this enemy+skill in m_PotentialResults.</summary>
        private uint FindActualTarget(uint enemyGuid, string skillId)
        {
            try
            {
                object battle = GetMember(Assets.Code.Utils.SingletonMonoBehaviour<CombatBhv>.Instance, "m_Battle");
                object stateMachine = battle == null ? null : GetMember(battle, "m_BattleStateMachine");
                object calculation = stateMachine == null ? null : GetMember(stateMachine, "m_BattleSkillCalculation");
                var results = calculation == null ? null : GetMember(calculation, "m_PotentialResults") as IDictionary;
                if (results == null)
                {
                    return 0;
                }

                foreach (DictionaryEntry entry in results)
                {
                    object input = entry.Key;
                    if (input == null)
                    {
                        continue;
                    }

                    if (ToUInt(GetMember(input, "m_PerformerGuid")) != enemyGuid)
                    {
                        continue;
                    }

                    if (!string.Equals(GetMember(input, "m_SelectedSkillId") as string, skillId, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    uint target = ToUInt(GetMember(input, "m_SelectedTargetActorGuid"));
                    if (target != 0)
                    {
                        return target;
                    }
                }
            }
            catch
            {
                // ignore
            }

            return 0;
        }

        /// <summary>When an enemy commits its action, find the AC offset that would have predicted it.</summary>
        private void LearnOffset()
        {
            try
            {
                if (Time.realtimeSinceStartup < _nextLearnPoll)
                {
                    return;
                }

                _nextLearnPoll = Time.realtimeSinceStartup + 0.5f;

                SaveReader.Snapshot s = _lastSnapshot;
                if (s == null || !s.InCombat)
                {
                    return;
                }

                object battle = GetMember(Assets.Code.Utils.SingletonMonoBehaviour<CombatBhv>.Instance, "m_Battle");
                if (battle == null)
                {
                    return;
                }

                uint currentGuid = ToUInt(GetMember(battle, "CurrentActorGuid"));
                if (currentGuid == 0 || !IsEnemy(s, currentGuid))
                {
                    return;
                }

                ActorInstance actor = GetActorInstance(currentGuid);
                string skillId = actor == null ? null : actor.GetSelectedSkillId();
                if (string.IsNullOrEmpty(skillId))
                {
                    return;
                }

                string key = s.LiveRound + "/" + s.LiveTurn;
                if (key == _learnKey)
                {
                    return;
                }

                uint actualTarget = FindActualTarget(currentGuid, skillId);
                if (actualTarget == 0)
                {
                    return;
                }

                var skills = new List<string>();
                var targets = new List<uint>();
                var weights = new List<float>();
                BuildEntriesFor(s, currentGuid, skills, targets, weights);
                if (skills.Count == 0)
                {
                    return;
                }

                List<float> rolls = PeekValues(RandomIdentifier.ACTOR_CONTROLLER, 24);
                float total = 0f;
                foreach (float w in weights)
                {
                    total += w;
                }

                for (int k = 0; k + 1 < rolls.Count; k++)
                {
                    float acc = rolls[k] * total;
                    int pick = skills.Count - 1;
                    for (int i = 0; i < skills.Count; i++)
                    {
                        acc -= weights[i];
                        if (acc < 0f)
                        {
                            pick = i;
                            break;
                        }
                    }

                    string pickedSkill = skills[pick];
                    var sameTargets = new List<uint>();
                    for (int i = 0; i < skills.Count; i++)
                    {
                        if (skills[i] == pickedSkill)
                        {
                            sameTargets.Add(targets[i]);
                        }
                    }

                    if (pickedSkill == skillId)
                    {
                        int idx = Mathf.Clamp((int)(rolls[k + 1] * sameTargets.Count), 0, sameTargets.Count - 1);
                        if (sameTargets[idx] == actualTarget)
                        {
                            _acOffset = k;
                            _learnKey = key;
                            Logger.LogInfo("OFFSET: learned=" + k + " (skill=" + skillId + " target=" + actualTarget + ")");
                            return;
                        }
                    }
                }

                _learnKey = key;
                Logger.LogInfo("OFFSET: no match in 24 values (skill=" + skillId + " target=" + actualTarget + ")");
            }
            catch (Exception ex)
            {
                Logger.LogWarning("OFFSET learn failed: " + ex.Message);
            }
        }
        private static string EntryList(List<string> skills, List<uint> targets)
        {
            var parts = new List<string>();
            int count = Mathf.Min(skills.Count, 8);
            for (int i = 0; i < count; i++)
            {
                parts.Add(skills[i] + "@" + targets[i].ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            return string.Join(" | ", parts);
        }
        private static string FirstClassIdOf(string classId)
        {
            if (string.IsNullOrEmpty(classId))
            {
                return classId;
            }

            int slash = classId.IndexOf('/');
            return slash > 0 ? classId.Substring(0, slash) : classId;
        }

        private static int TeamIndexOf(SaveReader.Snapshot s, uint guid)
        {
            foreach (SaveReader.TeamRow team in s.Teams)
            {
                foreach (SaveReader.ActorRow actor in team.Actors)
                {
                    if (actor.Guid == guid)
                    {
                        return team.TeamIndex;
                    }
                }
            }

            return -1;
        }

        private static SaveReader.ActorRow FindActorRow(SaveReader.Snapshot s, uint guid)
        {
            foreach (SaveReader.TeamRow team in s.Teams)
            {
                foreach (SaveReader.ActorRow actor in team.Actors)
                {
                    if (actor.Guid == guid)
                    {
                        return actor;
                    }
                }
            }

            return null;
        }

        private float _nextDiagLog;
        private string _lastDiagKey;

        /// <summary>Diagnostic trace (every 5 s in combat): peeked streams + current HP + prediction.</summary>
        /// <summary>Step-by-step trace of the enemy-action predictor (called with the diagnostics).</summary>
        private float _nextActualLog;

        /// <summary>Logs what the ENEMY actually selected (AI choice) so predictions can be diffed.</summary>
        private void LogActualAction()
        {
            try
            {
                if (Time.realtimeSinceStartup < _nextActualLog)
                {
                    return;
                }

                _nextActualLog = Time.realtimeSinceStartup + 0.5f;

                SaveReader.Snapshot s = _lastSnapshot;
                if (s == null || !s.InCombat)
                {
                    return;
                }

                object battle = GetMember(Assets.Code.Utils.SingletonMonoBehaviour<CombatBhv>.Instance, "m_Battle");
                if (battle == null)
                {
                    return;
                }

                uint currentGuid = ToUInt(GetMember(battle, "CurrentActorGuid"));
                if (currentGuid == 0 || !IsEnemy(s, currentGuid))
                {
                    return;
                }

                ActorInstance actor = GetActorInstance(currentGuid);
                if (actor == null)
                {
                    return;
                }

                string skill = actor.GetSelectedSkillId();
                uint target = 0;

                try
                {
                    MethodInfo method = FindMethod(actor.GetType(), "GetSelectedTargetActorGuid");
                    if (method != null)
                    {
                        target = ToUInt(method.Invoke(actor, null));
                    }
                }
                catch
                {
                    // target getter is optional
                }

                if (string.IsNullOrEmpty(skill) && target == 0)
                {
                    return;
                }

                Logger.LogInfo("ACTUAL: actor=" + currentGuid
                               + " skill=" + skill
                               + " target=" + target);
            }
            catch
            {
                // diagnostics only
            }
        }
        private void LogPredictSteps()
        {
            try
            {
                SaveReader.Snapshot s = _lastSnapshot;
                if (s == null || !s.InCombat)
                {
                    return;
                }

                if (_liveOrder == null || _liveOrder.Count == 0)
                {
                    Logger.LogInfo("PREDICT: no live order");
                    return;
                }

                uint enemyGuid = 0;
                foreach (uint guid in _liveOrder)
                {
                    if (IsEnemy(s, guid))
                    {
                        enemyGuid = guid;
                        break;
                    }
                }

                Logger.LogInfo("PREDICT: enemyGuid=" + enemyGuid);
                ActorInstance enemy = GetActorInstance(enemyGuid);
                Logger.LogInfo("PREDICT: actor=" + (enemy == null ? "null" : "ok"));

                object controller = enemy == null ? null : GetMember(enemy, "m_ActorController");
                Logger.LogInfo("PREDICT: controller=" + (controller == null ? "null" : controller.GetType().Name));

                var ids = new List<string>();
                var skills = enemy == null ? null : GetMember(enemy, "m_CombatSkills") as IEnumerable;
                int rawSkills = 0;
                if (skills != null)
                {
                    foreach (object skill in skills)
                    {
                        rawSkills++;
                        if (skill == null || !ToBool(GetMember(skill, "m_IsEquipped")))
                        {
                            continue;
                        }

                        string id = GetMember(skill, "m_SkillId") as string;
                        if (!string.IsNullOrEmpty(id))
                        {
                            ids.Add(id);
                        }
                    }
                }

                Logger.LogInfo("PREDICT: skills raw=" + rawSkills + " equipped=" + ids.Count + " [" + string.Join(",", ids.ToArray()) + "]");

                MethodInfo calculate = controller == null ? null : FindMethod(controller.GetType(), "CalculateValidSkillTargetEntries");
                Logger.LogInfo("PREDICT: calc method=" + (calculate == null ? "null" : "ok"));

                if (calculate != null)
                {
                    try
                    {
                        object result = calculate.Invoke(controller, new object[] { ids, false });
                        Logger.LogInfo("PREDICT: calc returned " + (result == null ? "null" : result.ToString()));
                    }
                    catch (Exception ex)
                    {
                        Logger.LogWarning("PREDICT: calc threw " + ex.GetBaseException().Message);
                    }
                }

                var entries = controller == null ? null : GetMember(controller, "m_SkillTargetEntries") as IEnumerable;
                int entryCount = 0;
                if (entries != null)
                {
                    foreach (object unused in entries)
                    {
                        entryCount++;
                    }
                }

                Logger.LogInfo("PREDICT: entries=" + entryCount);
            }
            catch (Exception ex)
            {
                Logger.LogWarning("PREDICT steps failed: " + ex.Message);
            }
        }
        /// <summary>
        /// Player-side prediction, ported from __Foreseeker_RNG__: enumerate m_PotentialResults for the
        /// current actor + selected skill, print damage/crit and each effect with its resist roll.
        /// </summary>
        private void UpdatePlayerPrediction()
        {
            _playerLines.Clear();
            _playerStatus = string.Empty;

            try
            {
                object battle = GetMember(Assets.Code.Utils.SingletonMonoBehaviour<CombatBhv>.Instance, "m_Battle");
                if (battle == null)
                {
                    return;
                }

                uint performerGuid = ToUInt(GetMember(battle, "CurrentActorGuid"));
                if (performerGuid == 0)
                {
                    _playerStatus = "(等待当前行动者)";
                    return;
                }

                ActorInstance performer = GetActorInstance(performerGuid);
                string skillId = performer == null ? null : performer.GetSelectedSkillId();

                _playerStatus = ClassNameOf(performerGuid)
                                + (string.IsNullOrEmpty(skillId) ? " / 未选择技能" : " / " + (SaveReader.SkillName(skillId) ?? skillId));

                if (string.IsNullOrEmpty(skillId))
                {
                    return;
                }

                object stateMachine = GetMember(battle, "m_BattleStateMachine");
                object calculation = stateMachine == null ? null : GetMember(stateMachine, "m_BattleSkillCalculation");
                var results = calculation == null ? null : GetMember(calculation, "m_PotentialResults") as IDictionary;
                if (results == null)
                {
                    return;
                }

                var rolls = PeekResistValues(24);
                int rollIndex = 0;

                foreach (DictionaryEntry entry in results)
                {
                    object input = entry.Key;
                    if (input == null || ToUInt(GetMember(input, "m_PerformerGuid")) != performerGuid)
                    {
                        continue;
                    }

                    if (!string.Equals(GetMember(input, "m_SelectedSkillId") as string, skillId, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var actorResults = GetMember(entry.Value, "ActorResults") as IEnumerable;
                    if (actorResults == null)
                    {
                        continue;
                    }

                    foreach (object actorResult in actorResults)
                    {
                        if (actorResult == null)
                        {
                            continue;
                        }

                        uint targetGuid = ToUInt(GetMember(actorResult, "m_TargetActorGuid"));
                        ActorInstance target = GetActorInstance(targetGuid);
                        float damage = ToFloat(GetMember(actorResult, "HealthDamage"));
                        bool crit = ToBool(GetMember(actorResult, "IsCrit"));

                        string line = "  " + ShortActor(target, targetGuid) + ": "
                                      + damage.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)
                                      + (crit ? "（暴击）" : string.Empty);

                        string effects = DescribeResultEffects(actorResult, performer, target, rolls, ref rollIndex);
                        if (effects.Length > 0)
                        {
                            line += "  " + effects;
                        }

                        _playerLines.Add(line);
                    }
                }

                if (_playerLines.Count == 0)
                {
                    _playerLines.Add("  (该技能还没有预计算)");
                }
            }
            catch (Exception ex)
            {
                if (!_previewErrorLogged)
                {
                    _previewErrorLogged = true;
                    Logger.LogWarning("player prediction failed: " + ex.Message);
                }
            }
        }

        private static string ShortActor(ActorInstance actor, uint guid)
        {
            string name = actor == null ? null : SaveReader.ClassName(FirstClassOf(actor));
            if (string.IsNullOrEmpty(name))
            {
                return "#" + guid;
            }

            float hp = ToFloat(GetMember(actor, "Hp"));
            if (hp <= 0f)
            {
                hp = ToFloat(GetMember(actor, "m_Hp"));
            }

            return name + " (HP " + hp.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + ")";
        }

        private static string FirstClassOf(ActorInstance actor)
        {
            var ids = actor == null ? null : GetMember(actor, "m_ActorDataIds") as IList;
            return ids != null && ids.Count > 0 ? ids[0] as string : null;
        }

        private string DescribeResultEffects(object actorResult, ActorInstance performer, ActorInstance target, List<float> rolls, ref int rollIndex)
        {
            var parts = new List<string>();

            try
            {
                object container = GetMember(actorResult, "m_AppliedEffectsOutputContainer");
                var outputs = container == null ? null : GetMember(container, "Outputs") as IEnumerable;
                if (outputs == null)
                {
                    return string.Empty;
                }

                foreach (object output in outputs)
                {
                    var instances = GetMember(output, "EffectInstancesToApply") as IEnumerable;
                    if (instances == null)
                    {
                        continue;
                    }

                    foreach (object instance in instances)
                    {
                        if (instance == null)
                        {
                            continue;
                        }

                        object definition = GetMember(instance, "EffectDefinition");
                        if (definition == null)
                        {
                            continue;
                        }

                        string dotId = GetMember(definition, "m_DotAddId") as string;
                        string tokenId = GetMember(definition, "m_TokenAddId") as string;
                        string label = null;

                        if (!string.IsNullOrEmpty(dotId))
                        {
                            label = SaveReader.DescribeDotApplication(dotId, ToInt(GetMember(instance, "DotAddAmount")));
                        }
                        else if (!string.IsNullOrEmpty(tokenId))
                        {
                            int amount = ToInt(GetMember(instance, "TokenAddAmount"));
                            label = SaveReader.TokenName(tokenId) + (amount > 1 ? "×" + amount : string.Empty);
                        }

                        if (string.IsNullOrEmpty(label))
                        {
                            continue;
                        }

                        float resist = ComputeResist(instance, target, performer);
                        // the container only holds effects the game already resolved as applying
                        string text = label + " ✔挂上";
                        if (resist >= 0.001f)
                        {
                            text += " 抵抗" + (resist * 100f).ToString("0", System.Globalization.CultureInfo.InvariantCulture) + "%";
                        }
                        rollIndex++;
                        parts.Add(text);
                    }
                }
            }
            catch
            {
                // effects are optional
            }

            return string.Join(", ", parts);
        }
        private void LogDiagnostics()
        {
            try
            {
                if (Time.realtimeSinceStartup < _nextDiagLog)
                {
                    return;
                }

                _nextDiagLog = Time.realtimeSinceStartup + 5f;

                SaveReader.Snapshot s = _lastSnapshot;
                if (s == null || !s.InCombat)
                {
                    return;
                }

                string key = s.LiveRound + "/" + s.LiveTurn + "/" + (_liveOrder == null ? 0 : _liveOrder.Count) + "/" + s.HasLiveCombat;
                if (key == _lastDiagKey)
                {
                    return;
                }

                _lastDiagKey = key;

                List<float> ctrl = PeekValues(RandomIdentifier.ACTOR_CONTROLLER, 6);
                List<float> resist = PeekValues(RandomIdentifier.RESIST, 6);
                List<float> calc = PeekValues(RandomIdentifier.SKILL_CALCULATION, 6);

                string hps = string.Empty;
                foreach (SaveReader.TeamRow team in s.Teams)
                {
                    foreach (SaveReader.ActorRow actor in team.Actors)
                    {
                        hps += (string.IsNullOrEmpty(actor.Name) ? actor.ClassId : actor.Name) + "=" + actor.Hp.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " ";
                    }
                }

                Logger.LogInfo("DIAG stamp=" + key
                               + " | ACTOR_CONTROLLER=" + string.Join(",", ctrl.ConvertAll(v => v.ToString("F4", System.Globalization.CultureInfo.InvariantCulture)).ToArray())
                               + " | RESIST=" + string.Join(",", resist.ConvertAll(v => v.ToString("F4", System.Globalization.CultureInfo.InvariantCulture)).ToArray())
                               + " | SKILL_CALCULATION=" + string.Join(",", calc.ConvertAll(v => v.ToString("F4", System.Globalization.CultureInfo.InvariantCulture)).ToArray())
                               + " | HP " + hps
                               + " | intent=" + _enemyIntent);

                foreach (string line in _predictLines)
                {
                    Logger.LogInfo("DIAG  " + line);
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning("DIAG failed: " + ex.Message);
            }
        }
        private void PollBattleState()
        {
            try
            {
                object battle = GetMember(Assets.Code.Utils.SingletonMonoBehaviour<CombatBhv>.Instance, "m_Battle");
                if (battle == null)
                {
                    return;
                }

                object round = GetMember(battle, "CurrentRound");
                object turn = GetMember(battle, "CurrentTurn");

                if (round != null)
                {
                    _liveRound = Convert.ToInt32(round);
                }

                if (turn != null)
                {
                    _liveTurn = Convert.ToInt32(turn);
                }
            }
            catch
            {
                // ignore
            }
        }


        private void PollTurnOrder()
        {
            try
            {
                QueryTurnOrder query = QueryTurnOrder.Trigger(true);
                if (query != null && query.m_RemainingTurnOrder != null)
                {
                    _liveOrder = query.m_RemainingTurnOrder;
                }
            }
            catch (Exception ex)
            {
                if (!_liveErrorLogged)
                {
                    _liveErrorLogged = true;
                    Logger.LogWarning("QueryTurnOrder failed: " + ex.Message);
                }
            }
        }

        /// <summary>Copies the live round/turn/queue onto the snapshot (names resolved from the save).</summary>
        private void ApplyLiveCombat(SaveReader.Snapshot snapshot)
        {
            if (snapshot == null || !snapshot.InCombat)
            {
                return;
            }

            if (_liveRound < 0 && _liveTurn < 0 && (_liveOrder == null || _liveOrder.Count == 0))
            {
                return;
            }

            snapshot.HasLiveCombat = true;
            snapshot.LiveRound = _liveRound;
            snapshot.LiveTurn = _liveTurn;
            snapshot.LiveTotalTurns = _liveTotalTurns;

            snapshot.LiveOrder.Clear();
            snapshot.LiveOrderGuids.Clear();
            if (_liveOrder != null)
            {
                foreach (uint guid in _liveOrder)
                {
                    snapshot.LiveOrderGuids.Add(guid);
                    snapshot.LiveOrder.Add(NameOf(snapshot, guid));
                }
            }
        }

        private static string NameOf(SaveReader.Snapshot snapshot, uint guid)
        {
            foreach (SaveReader.TeamRow team in snapshot.Teams)
            {
                foreach (SaveReader.ActorRow actor in team.Actors)
                {
                    if (actor.Guid == guid)
                    {
                        return string.IsNullOrEmpty(actor.Name) ? actor.ClassId : actor.Name;
                    }
                }
            }

            return guid.ToString();
        }

        private string LiveStamp()
        {
            return (_liveRound + "|" + _liveTurn + "|" + _liveTotalTurns + "|"
                    + (_liveOrder == null ? 0 : _liveOrder.Count));
        }

        /// <summary>Polls the newest DD2 save and rewrites the report when it changed.</summary>
        private void UpdateSaveReader()
        {
            if (!_saveReaderEnabled)
            {
                return;
            }

            if (Time.realtimeSinceStartup < _nextSavePoll)
            {
                return;
            }

            _nextSavePoll = Time.realtimeSinceStartup + Mathf.Max(1f, _saveReaderInterval);

            try
            {
                SaveReader.Snapshot snapshot = SaveReader.TryRead(_saveRoot);
                if (snapshot == null)
                {
                    return;
                }

                _lastSnapshot = snapshot;
                ApplyLiveCombat(snapshot);

                // debug file output removed: the save is parsed in memory only
                _lastSaveStamp = snapshot.Stamp() + "#" + LiveStamp();
            }
            catch (Exception ex)
            {
                if (!_saveReaderErrorLogged)
                {
                    _saveReaderErrorLogged = true;
                    Logger.LogError("save reader failed: " + ex);
                }
            }
        }

        private void HandleHotkeys()
        {
            try
            {
                IInputSystem input = UnityInput.Current;
                if (input == null)
                {
                    return;
                }

                if (IsPressed(input, _toggleKey) || IsPressed(input, _toggleKeyAlt))
                {
                    _visible = !_visible;
                    Logger.LogInfo("toggle -> visible=" + _visible);
                }

                if (IsPressed(input, _resetPositionKey))
                {
                    PlaceAtLeftCenter();
                    _visible = true;
                }

                if (IsPressed(input, _predictKey))
                {
                    _predictVisible = !_predictVisible;
                    Logger.LogInfo("predict window -> visible=" + _predictVisible);
                }

                // P2 experiment: replay the current actor's precompute and diff it.
                if (IsPressed(input, KeyCode.F3))
                {
                    SolverProbe.OnHotkey();
                }

                // Solver window.
                if (IsPressed(input, KeyCode.F5))
                {
                    Solver.Visible = !Solver.Visible;
                    Logger.LogInfo("solver window -> visible=" + Solver.Visible);
                }
            }
            catch (Exception ex)
            {
                if (!_inputErrorLogged)
                {
                    _inputErrorLogged = true;
                    Logger.LogError("hotkey polling failed: " + ex);
                }
            }
        }


        private static bool IsPressed(IInputSystem input, KeyCode key)
        {
            return key != KeyCode.None && input.GetKeyDown(key);
        }

        // ------------------------------------------------------------------ ini

        private void CheckIniForChanges()
        {
            if (Time.realtimeSinceStartup < _nextIniCheck)
            {
                return;
            }

            _nextIniCheck = Time.realtimeSinceStartup + 1f;

            try
            {
                if (File.Exists(_iniPath) && File.GetLastWriteTimeUtc(_iniPath) != _iniWriteTimeUtc)
                {
                    LoadIni(createIfMissing: false);
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning("ini watch failed: " + ex.Message);
            }
        }

        private void LoadIni(bool createIfMissing)
        {
            try
            {
                if (!File.Exists(_iniPath))
                {
                    if (!createIfMissing)
                    {
                        return;
                    }

                    File.WriteAllText(_iniPath, DefaultIni, new UTF8Encoding(false));
                    Logger.LogInfo("ini created with defaults: " + _iniPath);
                }

                foreach (string rawLine in File.ReadAllLines(_iniPath))
                {
                    string line = rawLine.Trim();

                    if (line.Length == 0 || line[0] == ';' || line[0] == '#')
                    {
                        continue;
                    }

                    if (line[0] == '[' && line[line.Length - 1] == ']')
                    {
                        continue; // section header, single flat namespace
                    }

                    int separator = line.IndexOf('=');
                    if (separator <= 0)
                    {
                        continue;
                    }

                    string key = line.Substring(0, separator).Trim();
                    string value = line.Substring(separator + 1).Trim();

                    if (key.Equals("ToggleKey", StringComparison.OrdinalIgnoreCase))
                    {
                        _toggleKey = ParseKey(value, KeyCode.F2, allowNone: false);
                    }
                    else if (key.Equals("ToggleKeyAlt", StringComparison.OrdinalIgnoreCase))
                    {
                        _toggleKeyAlt = ParseKey(value, KeyCode.F7, allowNone: true);
                    }
                    else if (key.Equals("ResetPositionKey", StringComparison.OrdinalIgnoreCase))
                    {
                        _resetPositionKey = ParseKey(value, KeyCode.F8, allowNone: false);
                    }
                    else if (key.Equals("SaveReaderEnabled", StringComparison.OrdinalIgnoreCase))
                    {
                        _saveReaderEnabled = ParseBool(value, _saveReaderEnabled);
                    }
                    else if (key.Equals("SaveReaderInterval", StringComparison.OrdinalIgnoreCase))
                    {
                        _saveReaderInterval = ParseFloat(value, _saveReaderInterval, 1f);
                    }
                    else if (key.Equals("StateWindowKey", StringComparison.OrdinalIgnoreCase))
                    {
                        _stateKey = ParseKey(value, KeyCode.F9, allowNone: false);
                    }

                    else if (key.Equals("StateWindowVisible", StringComparison.OrdinalIgnoreCase))
                    {
                        _stateVisibleDefault = ParseBool(value, true);
                    }

                }

                _iniWriteTimeUtc = File.GetLastWriteTimeUtc(_iniPath);
                Logger.LogInfo("ini loaded: ToggleKey=" + _toggleKey + " ToggleKeyAlt=" + _toggleKeyAlt
                               + " ResetPositionKey=" + _resetPositionKey);
            }
            catch (Exception ex)
            {
                Logger.LogError("ini load failed: " + ex);
            }
        }

        private KeyCode ParseKey(string value, KeyCode fallback, bool allowNone)
        {
            if (allowNone && value.Length == 0)
            {
                return KeyCode.None;
            }

            KeyCode parsed;
            if (value.Length > 0 && Enum.TryParse(value, true, out parsed) && parsed != KeyCode.None)
            {
                return parsed;
            }

            Logger.LogWarning("ini: invalid key '" + value + "', using " + fallback);
            return fallback;
        }

        private bool ParseBool(string value, bool fallback)
        {
            bool parsed;
            if (bool.TryParse(value.Trim(), out parsed))
            {
                return parsed;
            }

            int number;
            if (int.TryParse(value.Trim(), out number))
            {
                return number != 0;
            }

            Logger.LogWarning("ini: invalid bool '" + value + "', using " + fallback);
            return fallback;
        }

        private float ParseFloat(string value, float fallback, float minimum)
        {
            float parsed;
            if (float.TryParse(value.Trim(), System.Globalization.NumberStyles.Float,
                               System.Globalization.CultureInfo.InvariantCulture, out parsed))
            {
                return parsed < minimum ? minimum : parsed;
            }

            Logger.LogWarning("ini: invalid number '" + value + "', using " + fallback);
            return fallback;
        }

        // ------------------------------------------------------------- placement

        /// <summary>Left edge, vertically centered. Uses GUI space (Screen.width/height).</summary>
        private void PlaceAtLeftCenter()
        {
            float x = LeftMargin;
            float y = Mathf.Max(0f, (Screen.height - WindowHeight) * 0.5f);

            _windowRect = new Rect(x, y, WindowWidth, WindowHeight);
            _autoPosition = new Vector2(x, y);

            _placed = true;
            _placedScreenWidth = Screen.width;
            _placedScreenHeight = Screen.height;

            Logger.LogInfo("placed at left-center: " + _windowRect
                           + " screen=" + Screen.width + "x" + Screen.height);
        }

        // ------------------------------------------------------------------ gui

        private void OnGUI()
        {
            if (ForeseekerHost.Alive)
            {
                return;
            }

            DrawPluginGui();
        }

        internal void DrawPluginGui()
        {
            Solver.Draw();

            bool screenChanged = !_placed
                                 || Screen.width != _placedScreenWidth
                                 || Screen.height != _placedScreenHeight;

            if (screenChanged)
            {
                bool playerMovedWindow = _placed
                                         && new Vector2(_windowRect.x, _windowRect.y) != _autoPosition;

                if (playerMovedWindow)
                {
                    // Respect the player's placement; just remember the new screen size.
                    _placedScreenWidth = Screen.width;
                    _placedScreenHeight = Screen.height;
                }
                else
                {
                    PlaceAtLeftCenter();
                }
            }

            // styles shared by both windows
            if (_windowStyle == null)
            {
                _windowStyle = (GUI.skin != null && GUI.skin.window != null)
                    ? new GUIStyle(GUI.skin.window)
                    : new GUIStyle();
                _windowStyle.fontSize = TitleFontSize;

                _labelStyle = (GUI.skin != null && GUI.skin.label != null)
                    ? new GUIStyle(GUI.skin.label)
                    : new GUIStyle();
                _labelStyle.fontSize = LabelFontSize;

                _stateLabelStyle = (GUI.skin != null && GUI.skin.label != null)
                    ? new GUIStyle(GUI.skin.label)
                    : new GUIStyle();
                _stateLabelStyle.fontSize = StateFontSize;
                _stateLabelStyle.wordWrap = false;
            }

            if (_predictVisible)
            {
                try
                {
                    if (!_predictPlaced || Screen.width != _predictPlacedW || Screen.height != _predictPlacedH)
                    {
                        _predictPlacedW = Screen.width;
                        _predictPlacedH = Screen.height;
                        _predictRect = new Rect(Screen.width - _predictRect.width - 16f,
                                                (Screen.height * 0.72f),
                                                _predictRect.width,
                                                _predictRect.height);
                        _predictPlaced = true;
                    }

                    float predictHeight = Mathf.Max(200f, 52f + (_predictLines.Count * 22f));
                    float predictY = Mathf.Min(_predictRect.y, Screen.height - predictHeight - 16f);
                    _predictRect = new Rect(_predictRect.x, predictY, _predictRect.width, predictHeight);

                    _predictRect = GUI.Window(PredictWindowId, _predictRect, DrawPredictWindow, "=== DD2 状态读取 预测===", _windowStyle);
                }
                catch (Exception ex)
                {
                    Logger.LogError("predict OnGUI failed: " + ex);
                    _predictVisible = false;
                }
            }

            if (_visible)
            {
                try
                {
                    int lineCount = _playerLines.Count;
                    foreach (SkillPreviewRow row in _skillRows)
                    {
                        lineCount += row.Effects.Count > 0 ? 2 : 1;
                    }

                    float neededHeight = WindowHeight + 34f + (lineCount * 26f);
                    _windowRect = new Rect(_windowRect.x, _windowRect.y, _windowRect.width,
                                           Mathf.Max(WindowHeight, neededHeight));

                    _windowRect = GUI.Window(WindowId, _windowRect, DrawWindow, WindowTitle(), _windowStyle);
                }
                catch (Exception ex)
                {
                    Logger.LogError("OnGUI failed: " + ex);
                    _visible = false;
                }
            }
        }

        /// <summary>Title shows the hide key(s) exactly as configured in Foreseeker.ini, e.g. "Foreseeker [F2/F7 隐藏]".</summary>
        private string WindowTitle()
        {
            var keys = new List<string>();

            if (_toggleKey != KeyCode.None)
            {
                keys.Add(_toggleKey.ToString());
            }

            if (_toggleKeyAlt != KeyCode.None && _toggleKeyAlt != _toggleKey)
            {
                keys.Add(_toggleKeyAlt.ToString());
            }

            return keys.Count == 0
                ? PluginName
                : PluginName + " [" + string.Join("/", keys) + " 隐藏]";
        }
        /// <summary>__Foreseeker_RNG__-style target prefix: "1号位 (HP 11)  标记[虚弱] Dot[腐蚀(2*3)]".</summary>
        private string TargetSummary(SkillPreviewRow row)
        {
            SaveReader.Snapshot s = _lastSnapshot;
            if (s != null)
            {
                foreach (SaveReader.TeamRow team in s.Teams)
                {
                    foreach (SaveReader.ActorRow actor in team.Actors)
                    {
                        if (actor.Guid != row.TargetGuid)
                        {
                            continue;
                        }

                        string[] cells = SaveReader.ActorCells(actor);
                        return row.TargetName + " (" + cells[2] + ")";
                    }
                }
            }

            return row.TargetName;
        }

        private void DrawWindow(int windowId)
        {
            _drawTicks++;

            // RNG-style player prediction replaces the older row rendering.
            if (_playerLines.Count > 0 && _skillRows.Count > 0)
            {
                _skillRows.Clear();
            }

            if (!_firstDrawLogged)
            {
                _firstDrawLogged = true;
                Logger.LogInfo("first DrawWindow at t=" + Time.realtimeSinceStartup.ToString("F1")
                               + " rect=" + _windowRect);
            }

            GUILayout.Label(_playerLines.Count > 0 ? _playerStatus : _previewStatus, _labelStyle);

            foreach (string playerLine in _playerLines)
            {
                GUILayout.Label(playerLine, _labelStyle);
            }

            if (!string.IsNullOrEmpty(_enemyIntent))
            {
                GUILayout.Label(_enemyIntent, _labelStyle);
            }

            foreach (SkillPreviewRow row in _skillRows)
            {
                string line;

                if (row.ExactDamage.HasValue)
                {
                    // Exact pre-rolled value for this (skill, target).
                    line = "  " + TargetSummary(row) + ": " + row.ExactDamage.Value.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);

                    if (row.ExactHit == false)
                    {
                        line += "（未命中）";
                    }
                    else if (row.ExactCrit == true)
                    {
                        line += "（暴击）";
                    }
                }
                else
                {
                    string damage = row.IsDamageValid
                        ? (Mathf.Approximately(row.DamageLow, row.DamageHigh)
                            ? row.DamageLow.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)
                            : row.DamageLow.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)
                              + "-" + row.DamageHigh.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture))
                        : "-";

                    string critDamage = row.IsDamageValid && row.CritDamage > 0f
                        ? "（暴击 " + row.CritDamage.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "）"
                        : string.Empty;

                    string critChance = row.IsCritValid
                        ? (row.CritChance * 100f).ToString("0", System.Globalization.CultureInfo.InvariantCulture) + "%"
                        : "-";

                    string toHit = row.IsToHitValid
                        ? " 攻击命中" + (row.ToHitChance * 100f).ToString("0", System.Globalization.CultureInfo.InvariantCulture) + "%"
                        : string.Empty;

                    line = "  " + TargetSummary(row) + ": " + damage + critDamage + " 暴击" + critChance + toHit;
                }

                string effectSuffix = string.Empty;

                if (row.Effects.Count > 0)
                {
                    var parts = new List<string>();
                    foreach (EffectLine effect in row.Effects)
                    {
                        string text = (row.ExactHit == false ? "✘未命中·" : string.Empty) + effect.Text;

                        if (effect.Resist >= 0.001f)
                        {
                            text += " 抵抗" + (effect.Resist * 100f).ToString("0", System.Globalization.CultureInfo.InvariantCulture) + "%";
                        }

                        if (effect.PredictedResisted.HasValue)
                        {
                            text += effect.PredictedResisted.Value ? " ✘被抵抗" : " ✔可挂上";
                        }

                        parts.Add(text);
                    }

                    effectSuffix = "  " + string.Join(", ", parts);
                }

                GUILayout.Label(line + effectSuffix, _labelStyle);
            }

            GUILayout.FlexibleSpace();

            // Bottom-left close button.
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Close", GUILayout.Width(CloseButtonWidth), GUILayout.Height(CloseButtonHeight)))
            {
                _visible = false;
                Logger.LogInfo("Close pressed -> window hidden (press " + _toggleKey + " to reopen).");
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            // Only the title bar strip is draggable so the button stays clickable.
            GUI.DragWindow(new Rect(0f, 0f, _windowRect.width, TitleBarHeight));
        }
        private string GetStateText()
        {
            SaveReader.Snapshot snapshot = _lastSnapshot;
            if (snapshot == null)
            {
                return "(无存档)";
            }

            string stamp = snapshot.Stamp() + "#" + LiveStamp() + "#" + snapshot.HasLiveCombat;
            if (stamp != _stateTextStamp)
            {
                _stateTextStamp = stamp;
                ApplyLiveCombat(snapshot);
                _stateText = SaveReader.BuildStateText(snapshot);
            }

            return _stateText;
        }

        /// <summary>State-window title shows the hide key from Foreseeker.ini, e.g. "DD2 状态读取 [F9 隐藏]".</summary>
        private static readonly float[] StateColumnWidths = { 80f, 130f, 78f, 78f, 168f };

        private void DrawPredictWindow(int windowId)
        {
            foreach (string line in _predictLines)
            {
                GUILayout.Label(line, _stateLabelStyle);
            }

            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Close", GUILayout.Width(CloseButtonWidth), GUILayout.Height(CloseButtonHeight)))
            {
                _predictVisible = false;
            }
            GUILayout.EndHorizontal();

            GUI.DragWindow(new Rect(0f, 0f, _predictRect.width, TitleBarHeight));
        }


    }
}
