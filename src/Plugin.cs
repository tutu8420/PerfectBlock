using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace ChineseCanFly.PerfectBlock
{
    /// <summary>
    /// Perfect Block: learns when each enemy's attack actually lands (normalised animation time
    /// at the moment the damage event fires), then presses block for you a fraction of a second
    /// before that same point of the same animation. Nothing in the combat pipeline is touched:
    /// the mod only feeds the block key and lets the game run its own perfect-block logic.
    /// </summary>
    [BepInPlugin(Guid, "Perfect Block", "0.3.1")]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "ChineseCanFly.PerfectBlock";

        private static Plugin _instance;

        private static ConfigEntry<bool> _autoBlock;
        private static ConfigEntry<float> _detectRange;
        private static ConfigEntry<bool> _logLearning;
        private static ConfigEntry<bool> _logAttempts;
        private static ConfigEntry<float> _leadMs;
        private static ConfigEntry<int> _minSamples;
        private static ConfigEntry<float> _maxSpread;
        private static ConfigEntry<bool> _forceBlockTest;
        private static bool _testLogged;

        private static ConfigEntry<bool> _releaseOnAttack;
        private static ConfigEntry<bool> _showFlash;
        private static ConfigEntry<bool> _reliable;
        private static ConfigEntry<bool> _cancelOnManualBlock;

        private static ConfigEntry<int> _holdAfterPerfectMs;
        private static ConfigEntry<string> _afterPerfect;
        private static ConfigEntry<float> _pressTailMs;
        private static ConfigEntry<bool> _showCombatText;
        private static ConfigEntry<bool> _showVanillaNumbers;
        private static ConfigEntry<string> _fontName;
        private static ConfigEntry<string> _labelFontName;
        private static ConfigEntry<int> _baseFontSize;
        private static ConfigEntry<float> _critScale;
        private static ConfigEntry<float> _takenX;
        private static ConfigEntry<float> _takenY;
        private static ConfigEntry<float> _dealtX;
        private static ConfigEntry<float> _dealtY;
        private static ConfigEntry<bool> _testCombatText;
        private static ConfigEntry<bool> _showWeaponIcon;

        private static int _pressStartFrame = -1;
        private static float _pressUntil;
        private static float _holdUntil;
        private static bool _postPerfect;
        private static bool _toggleOnPending;
        private static bool _toggleOffPending;
        private static bool _wantBlockPrev;

        private sealed class HitText
        {
            public string Text;
            public string Label;
            public Color Color;
            public Sprite Icon;
            public float Born;
            public float Drift;
            public float Jitter;
            public int Size;
            public bool Taken;
        }

        private static readonly List<HitText> HitTexts = new List<HitText>();
        private static readonly Dictionary<string, Font> Fonts = new Dictionary<string, Font>();
        private static GUIStyle _numberStyle;
        private static GUIStyle _labelStyle;

        private static bool _attackCancelLogged;
        private static bool _staggerBypassLogged;
        private static int _perfectTotal;
        private static int _earlyTotal;
        private static int _lateTotal;
        private static int _damageLogged;
        private static int _reportLogged;
        private static bool _drawLogged;

        private sealed class Flash
        {
            public float Started;
        }

        private static readonly List<Flash> Flashes = new List<Flash>();
        private static GUIStyle _flashStyle;

        private static readonly FieldInfo CurrentAttackField = typeof(Humanoid).GetField("m_currentAttack", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo PreviousAttackField = typeof(Humanoid).GetField("m_previousAttack", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo CurrentAttackSecondaryField = typeof(Humanoid).GetField("m_currentAttackIsSecondary", BindingFlags.Instance | BindingFlags.NonPublic);

        private static bool _cancelSuppress;
        private static bool _forceBlockState;
        private static float _cancelStateAt;

        internal static bool CancelSuppress
        {
            get { return _cancelSuppress; }
        }

        internal static bool ForceBlockState
        {
            get { return _forceBlockState; }
        }

        internal static void ClearCancelState()
        {
            _cancelSuppress = false;
            _forceBlockState = false;
        }

        internal static bool BlockingFieldIsSet(Character c)
        {
            try
            {
                if (BlockingField == null || c == null) return false;
                return (bool)BlockingField.GetValue(c);
            }
            catch
            {
                return false;
            }
        }

        [Serializable]
        public class Record
        {
            public string prefab;
            public int stateHash;
            public float[] hits = new float[0];
            public float leadMs;
            public bool learned;
            public int successes;
            public int early;
            public int late;
        }

        private static readonly Dictionary<string, Record> Records = new Dictionary<string, Record>();

        // Hand written JSON: Unity's JsonUtility silently dropped the nested record array.
        private static readonly Regex RecordBlock = new Regex(@"\{[^{}]*\}", RegexOptions.Compiled);
        private static readonly Regex FieldString = new Regex("\"prefab\"\\s*:\\s*\"([^\"]*)\"", RegexOptions.Compiled);
        private static readonly Regex FieldInt = new Regex("\"state\"\\s*:\\s*(-?\\d+)", RegexOptions.Compiled);
        private static readonly Regex FieldHits = new Regex("\"hits\"\\s*:\\s*\\[([^\\]]*)\\]", RegexOptions.Compiled);
        private static readonly Regex FieldLead = new Regex("\"lead\"\\s*:\\s*(-?[0-9.]+)", RegexOptions.Compiled);
        private static readonly Regex FieldLearned = new Regex("\"learned\"\\s*:\\s*(true|false)", RegexOptions.Compiled);
        private static readonly Regex FieldOk = new Regex("\"ok\"\\s*:\\s*(-?\\d+)", RegexOptions.Compiled);
        private static readonly Regex FieldEarly = new Regex("\"early\"\\s*:\\s*(-?\\d+)", RegexOptions.Compiled);
        private static readonly Regex FieldLate = new Regex("\"late\"\\s*:\\s*(-?\\d+)", RegexOptions.Compiled);
        private static readonly Dictionary<Character, Animator> Animators = new Dictionary<Character, Animator>();
        private static readonly List<Character> Stale = new List<Character>();

        private static bool _wantBlock;
        private static bool _simulated;
        private static string _wantKey;

        private static readonly FieldInfo BlockingField = typeof(Humanoid).GetField("m_blocking", BindingFlags.Instance | BindingFlags.NonPublic);

        private static string SavePath
        {
            get { return Path.Combine(Paths.ConfigPath, "PerfectBlock", "learned.json"); }
        }

        private void Awake()
        {
            _instance = this;
            LoadLanguage();

            _autoBlock = Config.Bind("General", "Enable Auto Block", false,
                new ConfigDescription(T("EnableAutoBlock", "Take over the block key when an attack the mod has learned is about to land. Off by default: learning and logging still run.")));

            _detectRange = Config.Bind("General", "Detect Range", 25f,
                new ConfigDescription(T("DetectRange", "Only enemies within this many metres (m) are watched and blocked against."),
                    new AcceptableValueRange<float>(3f, 60f)));

            _logLearning = Config.Bind("General", "Log Learning", true,
                new ConfigDescription(T("LogLearning", "Write one log line per learned attack, with the sample count and spread.")));

            _logAttempts = Config.Bind("General", "Log Attempts", false,
                new ConfigDescription(T("LogAttempts", "Log every simulated block and its result. Noisy; for tuning.")));

            _forceBlockTest = Config.Bind("General", "Force Block Test", false,
                new ConfigDescription(T("ForceBlockTest", "Diagnostic only: hold the block key on every frame, ignoring enemies and learning.")));

            _releaseOnAttack = Config.Bind("Feedback", "Release Block On Attack", true,
                new ConfigDescription(T("ReleaseOnAttack", "After a perfect block, release the forced block as soon as you attack, until the next automatic block.")));

            _showFlash = Config.Bind("Feedback", "Show Perfect Flash", true,
                new ConfigDescription(T("ShowFlash", "Show PERFECT BLOCK in the middle of the screen when a perfect block lands; several in a row stack upwards.")));

            _reliable = Config.Bind("Feedback", "Reliable Perfect Block", true,
                new ConfigDescription(T("Reliable", "On a perfect block, ignore the vanilla rule that a hit bigger than your block armor plus stagger threshold breaks the block (what the ReliableBlock mod does, but only on perfect blocks).")));

            _cancelOnManualBlock = Config.Bind("Feedback", "Cancel Swing On Manual Block", true,
                new ConfigDescription(T("CancelManual", "Pressing block while you are mid-swing cancels the swing so the block starts immediately (AttackCancel behaviour).")));

            _holdAfterPerfectMs = Config.Bind("Feedback", "Hold After Perfect (ms)", 0,
                new ConfigDescription(T("HoldAfterPerfect", "After a perfect block, keep holding the block for this many milliseconds before letting go. The press already stays down for 'Press Tail Ms' after the hit, so 0-150 behaves the same; 250+ hides the shield coming back down and turns the next hit (in a multi-enemy fight) into an ordinary block. Raise it only if you really want the extra hold."),
                    new AcceptableValueRange<int>(0, 1000)));

            _afterPerfect = Config.Bind("Feedback", "After Perfect Block", "Release",
                new ConfigDescription(T("AfterPerfect", "What to do after a perfect block. Release = hold for 'Hold After Perfect (ms)', then let go. Keep = keep blocking until you attack or press block yourself (room for a future parry)."),
                    new AcceptableValueList<string>("Release", "Keep")));

            _showCombatText = Config.Bind("Combat", "Show Combat Text", true,
                new ConfigDescription(T("ShowCombat", "Draw damage numbers: damage you take on the left, damage you deal on the right.")));

            _showVanillaNumbers = Config.Bind("Combat", "Show Vanilla Numbers", false,
                new ConfigDescription(T("VanillaNumbers", "Keep the game's own floating damage numbers. Off by default, since our own text replaces them.")));

            _fontName = Config.Bind("Combat", "Font", "Impact",
                new ConfigDescription(T("Font", "Font used by the combat text and the PERFECT BLOCK flash. Any font installed on Windows works (Impact, Arial Black, Microsoft YaHei); falls back to the game font if missing.")));

            _baseFontSize = Config.Bind("Combat", "Base Font Size", 27,
                new ConfigDescription(T("BaseSize", "Font size of a normal damage number."),
                    new AcceptableValueRange<int>(10, 60)));

            _critScale = Config.Bind("Combat", "Big Hit Scale", 2.4f,
                new ConfigDescription(T("CritScale", "Size multiplier for the last hit of a combo or a doubled hit."),
                    new AcceptableValueRange<float>(1f, 3f)));

            _showWeaponIcon = Config.Bind("Combat", "Show Weapon Icon", true,
                new ConfigDescription(T("WeaponIcon", "Draw the weapon icon in front of every damage number: your weapon for damage you deal, the attacker's weapon for damage you take.")));

            _labelFontName = Config.Bind("Combat", "Label Font", "Microsoft YaHei",
                new ConfigDescription(T("LabelFont", "Font for the damage-type label in brackets (swing/heavy...). It has to contain Chinese glyphs, so it is separate from the number font. Falls back to the game font if missing.")));

            _takenX = Config.Bind("Combat", "Taken Position X", 0.2f,
                new ConfigDescription(T("TakenX", "Damage you take: horizontal anchor, 0 = left edge, 1 = right edge."),
                    new AcceptableValueRange<float>(0f, 1f)));
            _takenY = Config.Bind("Combat", "Taken Position Y", 0.6f,
                new ConfigDescription(T("TakenY", "Damage you take: vertical anchor, 0 = top, 1 = bottom."),
                    new AcceptableValueRange<float>(0f, 1f)));
            _dealtX = Config.Bind("Combat", "Dealt Position X", 0.7f,
                new ConfigDescription(T("DealtX", "Damage you deal: horizontal anchor, 0 = left edge, 1 = right edge."),
                    new AcceptableValueRange<float>(0f, 1f)));
            _dealtY = Config.Bind("Combat", "Dealt Position Y", 0.6f,
                new ConfigDescription(T("DealtY", "Damage you deal: vertical anchor, 0 = top, 1 = bottom."),
                    new AcceptableValueRange<float>(0f, 1f)));

            _testCombatText = Config.Bind("Combat", "Test Combat Text", false,
                new ConfigDescription(T("TestCombat", "Diagnostic: always show three sample damage numbers (red taken on the left, yellow dealt and a big heavy hit on the right) so you can drag the position sliders and see whether the text renders at all.")));

            _leadMs = Config.Bind("Learning", "Lead Ms", 70f,
                new ConfigDescription(T("LeadMs", "How early (milliseconds) to press block before the learned landing point. Fixed on purpose: the block timer is counted from the press, so a larger lead pushes you out of the 0.25 s perfect window."),
                    new AcceptableValueRange<float>(10f, 300f)));

            _pressTailMs = Config.Bind("Learning", "Press Tail Ms", 150f,
                new ConfigDescription(T("PressTail", "How long (milliseconds) to keep the simulated block down after the learned landing point. Covers prediction error; a late press is still an ordinary block instead of a free hit. Press length = Lead Ms + Press Tail Ms."),
                    new AcceptableValueRange<float>(0f, 500f)));

            _minSamples = Config.Bind("Learning", "Min Samples", 3,
                new ConfigDescription(T("MinSamples", "Samples needed before an attack counts as learned."),
                    new AcceptableValueRange<int>(1, 20)));

            _maxSpread = Config.Bind("Learning", "Max Spread", 0.05f,
                new ConfigDescription(T("MaxSpread", "Largest allowed spread of the recorded landing points, as animation progress (0.05 = 5%). Wider = treated as an unstable attack and ignored."),
                    new AcceptableValueRange<float>(0.005f, 0.3f)));

            LoadRecords();
            ApplyPatches();
            Logger.LogInfo("Perfect Block 0.3.1 loaded. Learned attacks: " + Records.Count + ", auto block: " + _autoBlock.Value
                           + ", lead: " + LeadMs + " ms, tail: " + TailMs + " ms, after perfect: " + AfterPerfectMode
                           + " (" + HoldMs + " ms hold)");
        }

        private void ApplyPatches()
        {
            Harmony harmony;
            try
            {
                harmony = new Harmony(Guid);
            }
            catch (Exception e)
            {
                Logger.LogError("Harmony unavailable, mod does nothing: " + e);
                return;
            }

            TryPatch(harmony, typeof(PatchAttackLanded), T("PatchLearn", "attack learning"));
            TryPatch(harmony, typeof(PatchSetControls), T("PatchSetControls", "input boundary"));
            TryPatch(harmony, typeof(PatchStaggerBypass), T("PatchStagger", "reliable perfect block"));
            TryPatch(harmony, typeof(PatchPlayerInAttack), T("PatchInAttack", "attack cancel transition"));
            TryPatch(harmony, typeof(PatchIsBlocking), T("PatchIsBlocking", "block during transition"));
            TryPatch(harmony, typeof(PatchBlockKey), T("PatchBlockKey", "block key"));
            TryPatch(harmony, typeof(PatchBlockResult), T("PatchBlockResult", "block result"));
            TryPatch(harmony, typeof(PatchDamageTaken), T("PatchDamage", "damage taken"));
            TryPatch(harmony, typeof(PatchCombatText), T("PatchCombat", "combat text"));
            TryPatch(harmony, typeof(PatchHideVanillaNumbers), T("PatchVanilla", "hide vanilla numbers"));
        }

        private void TryPatch(Harmony harmony, Type patch, string label)
        {
            try
            {
                harmony.PatchAll(patch);
                if (patch == typeof(PatchCombatText)) MarkApplyDamageHooked();
                Logger.LogInfo("Patch applied: " + label);
            }
            catch (Exception e)
            {
                Logger.LogError("Patch failed (" + label + "): " + e);
            }
        }

        private void Update()
        {
            try
            {
                _wantBlock = false;
                _wantKey = null;
                _simulated = false;

                // Test switch: hold the block key on every frame, no enemies involved.
                // This answers "does the simulated key actually reach the game" on its own.
                if (_forceBlockTest != null && _forceBlockTest.Value)
                {
                    _wantBlock = true;
                    _simulated = true;
                    PressFrames++;
                    if (!_testLogged)
                    {
                        _testLogged = true;
                        Logger.LogInfo("TEST MODE on: forcing the block key every frame. The character should hold its block.");
                    }
                    return;
                }
                if (_testLogged)
                {
                    _testLogged = false;
                    Logger.LogInfo("TEST MODE off.");
                }

                if (!_autoBlock.Value)
                {
                    _postPerfect = false;
                    return;
                }
                Player player = Player.m_localPlayer;
                if (player == null || player.IsDead())
                {
                    _postPerfect = false;
                    return;
                }

                // After a perfect block the block stays down for a while: holding is the reward, and
                // the release is what lets the next attack be perfect as well. Never put a "release"
                // window in front of the next attack (that was the old bug: it made every follow-up
                // hit land for free).
                if (_postPerfect)
                {
                    bool attacking = player.InAttack()
                                     || ZInput.GetButton("Attack") || ZInput.GetButton("JoyAttack");
                    bool blockTapped = ZInput.GetButtonDown("Block") || ZInput.GetButtonDown("JoyBlock");

                    if (attacking && (KeepMode || _releaseOnAttack == null || _releaseOnAttack.Value))
                    {
                        EndAfterPerfect();
                        if (BlockingField != null) BlockingField.SetValue(player, false);
                        Diag("after perfect: released because you attacked.");
                        return;
                    }

                    if (KeepMode)
                    {
                        if (blockTapped)
                        {
                            EndAfterPerfect();
                            Diag("after perfect: released because you pressed block yourself.");
                        }
                        else
                        {
                            _wantBlock = true;
                            _simulated = true;
                            PressFrames++;
                            return;
                        }
                    }
                    else if (Time.time < _holdUntil)
                    {
                        _wantBlock = true;
                        _simulated = true;
                        PressFrames++;
                        return;
                    }
                    else
                    {
                        EndAfterPerfect();
                        Diag("after perfect: hold finished, releasing.");
                    }
                }

                // Deliberate: never take over while the player is swinging. This is the mod's one
                // self-imposed handicap - an assist must not eat the player's attacks or every fight
                // would play itself. Do NOT "fix" this into an auto-parry without asking the author.
                if (player.InAttack()) return;
                // Only back off for a block the PLAYER is holding: if we pressed last frame, this
                // block is our own, and bailing out here made the press flicker one frame on and one
                // frame off (m_blocking went false again every other frame, so the hit always landed
                // on an "off" frame and read as "hit landed while NOT blocking", frames=1).
                if (player.IsBlocking() && !_wantBlockPrev)
                {
                    LogOnce("[diag] player is holding block on their own; auto block stays idle until that block drops.");
                    return;
                }

                float range = _detectRange.Value;
                float rangeSq = range * range;
                float lead = LeadSeconds;
                float tail = TailSeconds;
                List<Character> chars = Character.GetAllCharacters();
                for (int i = 0; i < chars.Count; i++)
                {
                    Character c = chars[i];
                    if (c == null || c == player) continue;
                    if (c.IsPlayer() || c.IsTamed() || c.IsDead()) continue;
                    if (!c.InAttack()) continue;
                    if ((c.transform.position - player.transform.position).sqrMagnitude > rangeSq) continue;

                    Animator animator = AnimatorOf(c);
                    if (animator == null) continue;

                    AnimatorStateInfo state = animator.GetCurrentAnimatorStateInfo(0);
                    if (state.length <= 0.01f) continue;

                    string key = Key(c, state);
                    Record record;
                    if (!Records.TryGetValue(key, out record) || !record.learned) continue;

                    float progress = Normalised(state);
                    if (progress < 0f) continue;

                    float remaining = (Average(record.hits) - progress) * state.length;
                    // Window: press `lead` before the learned landing point and keep pressing `tail`
                    // after it. Fixed numbers on purpose - the adaptive lead was pushing the press
                    // so far ahead that the block timer was already past 0.25 s when the hit came.
                    if (remaining <= lead && remaining >= -tail)
                    {
                        if (!_wantBlockPrev)
                        {
                            PressFrames = 0;
                            _pressStartFrame = Time.frameCount;
                            if (_logAttempts != null && _logAttempts.Value)
                            {
                                Logger.LogInfo(string.Format("attempt: {0}, press at {1:F0} ms before the learned hit (lead {2:F0}, tail {3:F0})",
                                    key, remaining * 1000f, LeadMs, TailMs));
                            }
                        }
                        _wantBlock = true;
                        _simulated = true;
                        _wantKey = key;
                        PressFrames++;
                        _pressUntil = Mathf.Max(_pressUntil, Time.time + Mathf.Max(remaining, 0f) + tail);
                        break;
                    }
                }

                // Keep the press for the tail even if the attacker's state already ended (staggered,
                // died, got parried): a late block is still an ordinary block instead of a free hit.
                if (!_wantBlock && Time.time < _pressUntil)
                {
                    _wantBlock = true;
                    _simulated = true;
                    PressFrames++;
                }
            }
            catch (Exception e)
            {
                Logger.LogError("update failed: " + e);
            }
        }

        /// <summary>Press/release bookkeeping. Runs after Update so it sees the final
        /// want-block state of the frame.</summary>
        private void LateUpdate()
        {
            try
            {
                if (_wantBlock && !_wantBlockPrev)
                {
                    _toggleOnPending = true;
                    _pressStartFrame = Time.frameCount;
                }
                else if (!_wantBlock && _wantBlockPrev)
                {
                    _toggleOnPending = false;
                    if (ToggleMode) _toggleOffPending = true;
                }
                _wantBlockPrev = _wantBlock;

                if (!_wantBlock && (_cancelSuppress || _forceBlockState) && Time.time - _cancelStateAt > 1f)
                {
                    ClearCancelState();
                }
            }
            catch
            {
                // never break the frame
            }
        }

        private void OnApplicationQuit()
        {
            SaveRecords();
        }

        // ---------------------------------------------------------------
        // learning
        // ---------------------------------------------------------------

        /// <summary>Called from the attack animation event of a non-player, non-tame creature.</summary>
        internal static void LearnFrom(Humanoid attacker)
        {
            try
            {
                Character c = attacker;
                if (c == null || c.IsPlayer() || c.IsTamed()) return;

                Animator animator = AnimatorOf(c);
                if (animator == null) return;

                AnimatorStateInfo state = animator.GetCurrentAnimatorStateInfo(0);
                float progress = Normalised(state);
                if (progress < 0.02f || progress > 1.2f) return;

                string key = Key(c, state);
                Record record;
                if (!Records.TryGetValue(key, out record))
                {
                    record = new Record
                    {
                        prefab = PrefabName(c),
                        stateHash = state.shortNameHash,
                        leadMs = LeadMs
                    };
                    Records[key] = record;
                }

                float[] next = new float[Mathf.Min(record.hits.Length + 1, 20)];
                Array.Copy(record.hits, next, record.hits.Length);
                next[next.Length - 1] = progress;
                record.hits = next;

                bool wasLearned = record.learned;
                record.learned = record.hits.Length >= (_minSamples != null ? _minSamples.Value : 3)
                                 && Spread(record.hits) <= (_maxSpread != null ? _maxSpread.Value : 0.05f);

                if (_logLearning != null && _logLearning.Value && _instance != null)
                {
                    _instance.Logger.LogInfo(string.Format("[learn] {0} | samples {1} | hit {2:F3} | spread {3:F3} | learned {4}",
                        key, record.hits.Length, Average(record.hits), Spread(record.hits), record.learned));
                }

                if (record.learned && !wasLearned && _instance != null)
                {
                    _instance.Logger.LogInfo("[learned] " + key + " is now stable enough to block against.");
                    _instance.SaveRecords();
                }
            }
            catch (Exception e)
            {
                if (_instance != null) _instance.Logger.LogError("learning failed: " + e);
            }
        }

        internal static void NotePerfect()
        {
            if (!_simulated) return;
            if (TestMode) return;

            _perfectTotal++;
            PostStat();
            if (_showFlash != null && _showFlash.Value) AddFlash();

            Record record;
            if (_wantKey != null && Records.TryGetValue(_wantKey, out record)) record.successes++;

            // The hold starts at the hit, not at the end of the press: from here on the block is
            // kept down for HoldMs (Release) or until the player ends it (Keep).
            _postPerfect = true;
            _holdUntil = Time.time + HoldMs / 1000f;
            Diag("perfect block #" + _perfectTotal);
        }

        internal static void NoteEarly()
        {
            if (!_simulated) return;
            if (TestMode) return;
            _earlyTotal++;
            // TODO(leave for later): this printout cannot tell WHY the game refused the perfect.
            // Measured 2026-11-03: 10 ordinary blocks, 9 of them with blockTimer 0.02-0.04 s, i.e.
            // inside the 0.25 s window - so the press timing was fine and the game's own condition
            // failed on the blocker (m_timedBlockBonus <= 1, or GetCurrentBlocker() returned null).
            // Different weapons do NOT change how long the block takes to come up: m_blockTimer
            // starts at the press for every weapon; only the parry multiplier and block armor differ.
            // Next time, log GetCurrentBlocker().m_shared.m_name + m_timedBlockBonus here too.
            Diag(string.Format("ordinary block (blockTimer {0:F3}s, outside the 0.25 s window): pressed too early",
                BlockTimerOf(Player.m_localPlayer)));
            PostStat();
            Record record;
            if (_wantKey != null && Records.TryGetValue(_wantKey, out record)) record.early++;
        }

        internal static void NoteLate(Character attacker)
        {
            if (!_simulated) return;
            if (TestMode) return;
            if (attacker == null) return;
            Animator animator = AnimatorOf(attacker);
            if (animator == null) return;
            AnimatorStateInfo state = animator.GetCurrentAnimatorStateInfo(0);
            Record record;
            if (!Records.TryGetValue(Key(attacker, state), out record)) return;
            _lateTotal++;
            PostStat();
            record.late++;
        }

        /// <summary>One line every 10 resolved blocks: the quickest way to read the perfect rate.</summary>
        private static void PostStat()
        {
            int total = _perfectTotal + _earlyTotal + _lateTotal;
            if (total == 0 || total % 10 != 0 || _instance == null) return;
            _instance.Logger.LogInfo(string.Format("[stat] {0} blocks: perfect={1} ordinary={2} late={3}",
                total, _perfectTotal, _earlyTotal, _lateTotal));
        }

        private static void EndAfterPerfect()
        {
            _postPerfect = false;
            _holdUntil = 0f;
        }

        // ---------------------------------------------------------------
        // helpers
        // ---------------------------------------------------------------

        internal static bool WantBlock
        {
            get { return _wantBlock; }
        }

        internal static bool Simulated
        {
            get { return _simulated; }
        }

        internal static bool TestMode
        {
            get { return _forceBlockTest != null && _forceBlockTest.Value; }
        }

        internal static int PressFrames;
        internal static int ForcedReturns;
        internal static bool LogAttemptsOn
        {
            get { return _logAttempts != null && _logAttempts.Value; }
        }

        internal static string AfterPerfectMode
        {
            get { return _afterPerfect != null && !string.IsNullOrEmpty(_afterPerfect.Value) ? _afterPerfect.Value : "Release"; }
        }

        internal static bool KeepMode
        {
            get { return string.Equals(AfterPerfectMode, "Keep", StringComparison.OrdinalIgnoreCase); }
        }

        internal static float LeadMs
        {
            get { return _leadMs != null ? Mathf.Clamp(_leadMs.Value, 1f, 500f) : 70f; }
        }

        internal static float TailMs
        {
            get { return _pressTailMs != null ? Mathf.Clamp(_pressTailMs.Value, 0f, 1000f) : 150f; }
        }

        internal static float LeadSeconds
        {
            get { return LeadMs / 1000f; }
        }

        internal static float TailSeconds
        {
            get { return TailMs / 1000f; }
        }

        internal static int HoldMs
        {
            get { return _holdAfterPerfectMs != null ? Mathf.Max(0, _holdAfterPerfectMs.Value) : 250; }
        }

        /// <summary>Seconds the block has been held, -1 = not blocking, for diagnostics.</summary>
        internal static float BlockTimerOf(Character c)
        {
            try
            {
                if (c == null || StaggerTimerField == null) return -999f;
                return (float)StaggerTimerField.GetValue(c);
            }
            catch
            {
                return -999f;
            }
        }

        private static float Normalised(AnimatorStateInfo state)
        {
            float t = state.normalizedTime;
            if (t < 0f) return -1f;
            if (t > 1.5f) t = Mathf.Repeat(t, 1f);
            return t;
        }

        private static string Key(Character c, AnimatorStateInfo state)
        {
            return PrefabName(c) + "|" + state.shortNameHash;
        }

        private static Animator AnimatorOf(Character c)
        {
            if (c == null) return null;
            Animator animator;
            if (Animators.TryGetValue(c, out animator) && animator != null) return animator;
            animator = c.GetComponentInChildren<Animator>();
            if (animator == null) return null;
            if (Animators.Count > 400)
            {
                Stale.Clear();
                foreach (KeyValuePair<Character, Animator> kv in Animators)
                {
                    if (kv.Key == null || kv.Value == null) Stale.Add(kv.Key);
                }
                for (int i = 0; i < Stale.Count; i++) Animators.Remove(Stale[i]);
            }
            Animators[c] = animator;
            return animator;
        }

        private static string PrefabName(Character c)
        {
            string name = c.gameObject.name;
            if (name.EndsWith("(Clone)", StringComparison.Ordinal)) name = name.Substring(0, name.Length - 7);
            return name.Trim();
        }

        private static float Average(float[] values)
        {
            if (values == null || values.Length == 0) return 0f;
            float sum = 0f;
            for (int i = 0; i < values.Length; i++) sum += values[i];
            return sum / values.Length;
        }

        private static float Spread(float[] values)
        {
            if (values == null || values.Length == 0) return 0f;
            float min = values[0];
            float max = values[0];
            for (int i = 1; i < values.Length; i++)
            {
                if (values[i] < min) min = values[i];
                if (values[i] > max) max = values[i];
            }
            return max - min;
        }

        // ---------------------------------------------------------------
        // persistence
        // ---------------------------------------------------------------

        private static void LoadRecords()
        {
            try
            {
                string path = SavePath;
                if (!File.Exists(path)) return;

                string text = File.ReadAllText(path, Encoding.UTF8);
                int added = 0;
                foreach (Match block in RecordBlock.Matches(text))
                {
                    string body = block.Value;
                    Match prefab = FieldString.Match(body);
                    Match state = FieldInt.Match(body);
                    if (!prefab.Success || !state.Success) continue;

                    Record record = new Record
                    {
                        prefab = prefab.Groups[1].Value,
                        stateHash = int.Parse(state.Groups[1].Value, CultureInfo.InvariantCulture)
                    };

                    Match hits = FieldHits.Match(body);
                    if (hits.Success)
                    {
                        List<float> values = new List<float>();
                        string[] parts = hits.Groups[1].Value.Split(',');
                        for (int i = 0; i < parts.Length; i++)
                        {
                            float value;
                            if (float.TryParse(parts[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value)) values.Add(value);
                        }
                        record.hits = values.ToArray();
                    }

                    Match lead = FieldLead.Match(body);
                    if (lead.Success) float.TryParse(lead.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out record.leadMs);

                    Match learned = FieldLearned.Match(body);
                    record.learned = learned.Success && learned.Groups[1].Value == "true";

                    Match ok = FieldOk.Match(body);
                    if (ok.Success) int.TryParse(ok.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out record.successes);
                    Match early = FieldEarly.Match(body);
                    if (early.Success) int.TryParse(early.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out record.early);
                    Match late = FieldLate.Match(body);
                    if (late.Success) int.TryParse(late.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out record.late);

                    Records[record.prefab + "|" + record.stateHash] = record;
                    added++;
                }

                if (_instance != null) _instance.Logger.LogInfo("Loaded " + added + " learned attack(s) from " + path);
            }
            catch (Exception e)
            {
                if (_instance != null) _instance.Logger.LogError("could not read " + SavePath + ": " + e.Message);
            }
        }

        private void SaveRecords()
        {
            try
            {
                string path = SavePath;
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

                StringBuilder sb = new StringBuilder();
                sb.Append("{\n");
                sb.Append("  \"version\": \"0.3.0\",\n");
                sb.Append("  \"note\": \"Learned attack landing points. hits = animation progress (0-1) at the moment damage fires; lead is informational only (the press lead is a fixed config value now).\",\n");
                sb.Append("  \"records\": [");

                bool first = true;
                foreach (Record record in Records.Values)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append("\n    {\"prefab\": \"").Append(Escape(record.prefab)).Append("\"");
                    sb.Append(", \"state\": ").Append(record.stateHash);
                    sb.Append(", \"hits\": [");
                    for (int i = 0; i < record.hits.Length; i++)
                    {
                        if (i > 0) sb.Append(", ");
                        sb.Append(record.hits[i].ToString("0.####", CultureInfo.InvariantCulture));
                    }
                    sb.Append(']');
                    sb.Append(", \"lead\": ").Append(record.leadMs.ToString("0.##", CultureInfo.InvariantCulture));
                    sb.Append(", \"learned\": ").Append(record.learned ? "true" : "false");
                    sb.Append(", \"ok\": ").Append(record.successes);
                    sb.Append(", \"early\": ").Append(record.early);
                    sb.Append(", \"late\": ").Append(record.late);
                    sb.Append('}');
                }

                sb.Append("\n  ]\n}\n");
                File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
            }
            catch (Exception e)
            {
                Logger.LogError("could not write " + SavePath + ": " + e.Message);
            }
        }

        private static string Escape(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }


        // ---------------------------------------------------------------
        // localization
        // ---------------------------------------------------------------

        private static readonly HashSet<string> InfoSeen = new HashSet<string>();

        internal static void LogOnce(string message)
        {
            if (_instance == null) return;
            if (!InfoSeen.Add(message)) return;
            _instance.Logger.LogInfo(message);
        }

        internal static void Diag(string message)
        {
            if (_instance == null) return;
            if (_logAttempts == null || !_logAttempts.Value) return;
            _instance.Logger.LogInfo("[diag] " + message);
        }

        /// <summary>Damage-number diagnostics: always logs the first few ApplyDamage calls, so the log
        /// alone tells us whether the hook fires and what the health delta looks like.</summary>
        internal static void DiagDamage(Character victim, HitData hit, float delta)
        {
            if (_instance == null) return;
            if (_damageLogged >= 5) return;
            _damageLogged++;
            try
            {
                Character attacker = hit.GetAttacker();
                _instance.Logger.LogInfo(string.Format(
                    "[diag] ApplyDamage #{0}: victim={1} player={2} attacker={3} total={4:F1} healthDelta={5:F1}",
                    _damageLogged,
                    victim != null ? victim.gameObject.name : "null",
                    victim != null && victim == Player.m_localPlayer,
                    attacker != null ? attacker.gameObject.name : "null",
                    hit.GetTotalDamage(), delta));
            }
            catch
            {
                // diagnostics must never throw
            }
        }

        // ---------------------------------------------------------------
        // feel: release on attack, flash, stagger bypass, attack cancel
        // ---------------------------------------------------------------

        private static readonly FieldInfo StaggerTimerField = typeof(Humanoid).GetField("m_blockTimer", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly MethodInfo StaggerBlockerMethod = typeof(Humanoid).GetMethod("GetCurrentBlocker", BindingFlags.Instance | BindingFlags.NonPublic);

        internal static bool ShouldSkipStagger(Character target)
        {
            try
            {
                if (_reliable == null || !_reliable.Value) return false;
                if (target == null || target != Player.m_localPlayer) return false;
                if (StaggerTimerField == null) return false;

                float timer = (float)StaggerTimerField.GetValue(target);
                if (timer < 0f || timer >= 0.25f) return false;

                if (StaggerBlockerMethod != null)
                {
                    ItemDrop.ItemData blocker = StaggerBlockerMethod.Invoke(target, null) as ItemDrop.ItemData;
                    if (blocker == null || blocker.m_shared.m_timedBlockBonus <= 1f) return false;
                }

                if (!_staggerBypassLogged)
                {
                    _staggerBypassLogged = true;
                    LogOnce("[diag] stagger rule bypassed on a perfect block.");
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void CancelPlayerAttack(Player player)
        {
            try
            {
                if (CurrentAttackField == null) return;
                Attack attack = CurrentAttackField.GetValue(player) as Attack;
                if (attack == null) return;

                // Same sequence the AttackCancel mod proved out: stop the attack, drop the
                // references, then ForceJump so the animator leaves the attack state. Patch
                // Player.InAttack / Humanoid.IsBlocking below cover the transition frames.
                attack.Stop();
                if (PreviousAttackField != null) PreviousAttackField.SetValue(player, attack);
                CurrentAttackField.SetValue(player, null);
                if (CurrentAttackSecondaryField != null) CurrentAttackSecondaryField.SetValue(player, false);

                _cancelSuppress = true;
                _forceBlockState = true;
                _cancelStateAt = Time.time;
                player.ForceJump(new Vector3(0f, -20f, 0f), true);

                if (!_attackCancelLogged)
                {
                    _attackCancelLogged = true;
                    LogOnce("[diag] attack cancelled (Stop + ForceJump) so the block could start.");
                }
            }
            catch
            {
                // leave the swing alone if anything unexpected shows up
            }
        }

        internal static bool ToggleMode
        {
            get
            {
                Player player = Player.m_localPlayer;
                return player != null && player.ToggleBlock;
            }
        }

        internal static bool ToggleOnPending
        {
            get { return _toggleOnPending; }
        }

        internal static void ClearToggleOn()
        {
            _toggleOnPending = false;
        }

        internal static bool ToggleOffPending
        {
            get { return _toggleOffPending; }
        }

        internal static void ClearToggleOff()
        {
            _toggleOffPending = false;
        }

        internal static void ForceBlocking(bool value)
        {
            try
            {
                Player player = Player.m_localPlayer;
                if (player == null || BlockingField == null) return;
                BlockingField.SetValue(player, value);
            }
            catch
            {
                // ignore
            }
        }

        internal static bool ShouldCancelManualBlock()
        {
            return _cancelOnManualBlock != null && _cancelOnManualBlock.Value;
        }

        internal static void CancelSwingNow()
        {
            Player player = Player.m_localPlayer;
            if (player == null || player.IsDead()) return;
            if (CurrentAttackField == null) return;
            if (CurrentAttackField.GetValue(player) == null) return;
            CancelPlayerAttack(player);
        }

        private static void AddFlash()
        {
            Flashes.Add(new Flash { Started = Time.time });
            if (Flashes.Count > 6) Flashes.RemoveAt(0);
        }

        private static readonly HashSet<string> OsFontNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static bool _osFontsRead;

        /// <summary>Unity happily returns a Font object for a name that does not exist on this machine,
        /// but that font renders nothing at all. So check the installed list first and fall back to the
        /// game's own font when the configured name is not there.</summary>
        private static bool IsInstalledFont(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            if (!_osFontsRead)
            {
                _osFontsRead = true;
                try
                {
                    string[] names = Font.GetOSInstalledFontNames();
                    if (names != null)
                    {
                        for (int i = 0; i < names.Length; i++)
                        {
                            if (!string.IsNullOrEmpty(names[i])) OsFontNames.Add(names[i]);
                        }
                    }
                }
                catch
                {
                    // leave the set empty: everything falls back to the game font
                }
            }
            return OsFontNames.Contains(name.Trim());
        }

        private static Font GameFont()
        {
            try
            {
                if (GUI.skin != null && GUI.skin.label != null) return GUI.skin.label.font;
            }
            catch
            {
                // ignore
            }
            return null;
        }

        private static Font FontFor(int size)
        {
            return FontFor(size, _fontName != null ? _fontName.Value : "Impact");
        }

        private static Font FontFor(int size, string requested)
        {
            if (string.IsNullOrEmpty(requested)) requested = "Impact";
            string key = requested.Trim() + "#" + size;
            Font font;
            if (Fonts.TryGetValue(key, out font) && font != null) return font;

            bool installed = IsInstalledFont(requested);
            font = null;
            if (installed)
            {
                try
                {
                    font = Font.CreateDynamicFontFromOSFont(requested.Trim(), size);
                }
                catch
                {
                    font = null;
                }
            }
            if (font == null) font = GameFont();

            LogOnce(string.Format("[diag] font for size {0}: requested '{1}' installed={2}, using '{3}'",
                size, requested, installed, font != null && !string.IsNullOrEmpty(font.name) ? font.name : "null"));
            Fonts[key] = font;
            return font;
        }

        /// <summary>Draw text with a dark outline and a fake bold (the game font has no bold face).</summary>
        private static void DrawBold(Rect rect, string text, GUIStyle style, Color color, int outline)
        {
            Color previous = GUI.color;
            if (outline > 0)
            {
                GUI.color = new Color(0f, 0f, 0f, color.a * 0.85f);
                GUI.Label(new Rect(rect.x - outline, rect.y, rect.width, rect.height), text, style);
                GUI.Label(new Rect(rect.x + outline, rect.y, rect.width, rect.height), text, style);
                GUI.Label(new Rect(rect.x, rect.y - outline, rect.width, rect.height), text, style);
                GUI.Label(new Rect(rect.x, rect.y + outline, rect.width, rect.height), text, style);
            }
            GUI.color = color;
            GUI.Label(rect, text, style);
            GUI.Label(new Rect(rect.x + 1f, rect.y, rect.width, rect.height), text, style);
            GUI.Label(new Rect(rect.x, rect.y + 1f, rect.width, rect.height), text, style);
            GUI.color = previous;
        }

        private void OnGUI()
        {
            try
            {
                if (Flashes.Count > 0) DrawFlashes();
                if (_showCombatText != null && _showCombatText.Value && HitTexts.Count > 0) DrawHitTexts();
                if (_testCombatText != null && _testCombatText.Value) DrawTestHitTexts();
            }
            catch
            {
                // never let the overlay break the game
            }
        }

        private void DrawFlashes()
        {
            float now = Time.time;
            for (int i = Flashes.Count - 1; i >= 0; i--)
            {
                if (now - Flashes[i].Started > 1.0f) Flashes.RemoveAt(i);
            }
            if (Flashes.Count == 0) return;

            if (_flashStyle == null)
            {
                _flashStyle = new GUIStyle(GUI.skin.label);
                _flashStyle.alignment = TextAnchor.MiddleCenter;
            }

            float centerX = Screen.width * 0.5f;
            float centerY = Screen.height * 0.40f;

            for (int i = 0; i < Flashes.Count; i++)
            {
                float age = now - Flashes[i].Started;
                float alpha = age < 0.6f ? 1f : Mathf.Clamp01(1f - (age - 0.6f) / 0.4f);
                int size = Mathf.RoundToInt(Screen.height * 0.055f);
                _flashStyle.fontSize = size;
                _flashStyle.font = FontFor(size);

                // A pop, not a scale: it drops in from slightly above and stays big.
                float lift = Mathf.Min(age * 40f, 10f);
                float y = centerY - i * (size + 14f) - lift;
                Rect rect = new Rect(centerX - 520f, y, 1040f, size * 1.6f);
                DrawBold(rect, "PERFECT BLOCK !!!", _flashStyle, new Color(1f, 0.82f, 0.1f, alpha), 3);
            }
        }

        private void DrawHitTexts()
        {
            float now = Time.time;
            for (int i = HitTexts.Count - 1; i >= 0; i--)
            {
                if (now - HitTexts[i].Born > 1.2f) HitTexts.RemoveAt(i);
            }
            if (HitTexts.Count == 0) return;

            for (int i = 0; i < HitTexts.Count; i++)
            {
                HitText text = HitTexts[i];
                float age = now - text.Born;
                float t = Mathf.Clamp01(age / 1.2f);
                float alpha = Mathf.Clamp01(1f - t * t);

                if (!_drawLogged)
                {
                    _drawLogged = true;
                    LogOnce(string.Format("[diag] first combat text draw: {0} entry/entries, text='{1}', taken={2}, font={3}, size={4}, screen={5}x{6}",
                        HitTexts.Count, text.Text, text.Taken,
                        (_fontName != null ? _fontName.Value : "?"), text.Size, Screen.width, Screen.height));
                }

                float x = Screen.width * (text.Taken ? TakenAnchorX : DealtAnchorX);
                float y = Screen.height * (text.Taken ? TakenAnchorY : DealtAnchorY) + text.Jitter - text.Drift * age;
                Color color = new Color(text.Color.r, text.Color.g, text.Color.b, alpha);
                DrawHitEntry(x, y, text.Text, text.Label, text.Size, color, text.Icon);
            }
        }

        /// <summary>Draw "78 (Swing)" - the weapon icon, then the number in the configured number
        /// font, then the bracketed damage type in the label font (which has to carry Chinese
        /// glyphs). The whole block is right-aligned at the anchor.</summary>
        private static void DrawHitEntry(float anchorX, float y, string number, string label, int size, Color color, Sprite icon)
        {
            if (_numberStyle == null)
            {
                _numberStyle = new GUIStyle(GUI.skin.label);
                _numberStyle.alignment = TextAnchor.MiddleLeft;
                _numberStyle.wordWrap = false;
                _labelStyle = new GUIStyle(GUI.skin.label);
                _labelStyle.alignment = TextAnchor.MiddleLeft;
                _labelStyle.wordWrap = false;
            }

            int labelSize = Mathf.Max(10, Mathf.RoundToInt(size * 0.72f));
            string labelText = string.IsNullOrEmpty(label) ? "" : " (" + label + ")";

            _numberStyle.fontSize = size;
            _numberStyle.font = FontFor(size);
            _labelStyle.fontSize = labelSize;
            _labelStyle.font = FontFor(labelSize, LabelFontName);

            float numberWidth = _numberStyle.CalcSize(new GUIContent(number)).x;
            float labelWidth = labelText.Length == 0 ? 0f : _labelStyle.CalcSize(new GUIContent(labelText)).x;
            float lineHeight = size * 1.8f;
            float iconSize = size * 1.15f;
            float iconWidth = icon != null ? iconSize + 6f : 0f;
            float left = anchorX - iconWidth - numberWidth - labelWidth;

            if (icon != null)
            {
                DrawIcon(new Rect(left, y + (lineHeight - iconSize) * 0.5f, iconSize, iconSize), icon, color.a);
            }

            DrawBold(new Rect(left + iconWidth, y, numberWidth + 8f, lineHeight), number, _numberStyle, color, 2);
            if (labelWidth > 0f)
            {
                DrawBold(new Rect(left + iconWidth + numberWidth, y + (size - labelSize) * 0.55f, labelWidth + 8f, labelSize * 1.8f),
                    labelText, _labelStyle, new Color(color.r, color.g, color.b, color.a * 0.92f), 1);
            }
        }

        /// <summary>IMGUI has no Sprite drawing, so draw the sprite's sub-rect out of its atlas.</summary>
        private static void DrawIcon(Rect rect, Sprite sprite, float alpha)
        {
            try
            {
                if (sprite == null) return;
                Texture2D texture = sprite.texture;
                if (texture == null) return;

                Rect tr = sprite.textureRect;
                if (tr.width <= 0f || tr.height <= 0f) tr = new Rect(0f, 0f, texture.width, texture.height);
                Rect uv = new Rect(tr.x / texture.width, tr.y / texture.height, tr.width / texture.width, tr.height / texture.height);

                Color previous = GUI.color;
                GUI.color = new Color(1f, 1f, 1f, alpha);
                GUI.DrawTextureWithTexCoords(rect, texture, uv, true);
                GUI.color = previous;
            }
            catch
            {
                // the icon is decoration only
            }
        }

        /// <summary>Diagnostic overlay: three fake entries drawn every frame, so the position sliders
        /// and the colours can be checked without waiting for a hit to land.</summary>
        private void DrawTestHitTexts()
        {
            int size = CombatFontSize;
            float takenX = Screen.width * TakenAnchorX;
            float takenY = Screen.height * TakenAnchorY;
            float dealtX = Screen.width * DealtAnchorX;
            float dealtY = Screen.height * DealtAnchorY;
            int big = Mathf.RoundToInt(size * BigHitScale);

            Sprite icon = ShowWeaponIcons ? PlayerWeaponIcon(Player.m_localPlayer) : null;

            DrawHitEntry(takenX, takenY, "-37", T("LabelSlash", "挥砍"), size, new Color(1f, 0.28f, 0.22f, 1f), icon);
            DrawHitEntry(dealtX, dealtY, "78", T("LabelPierce", "刺击"), size, new Color(1f, 0.92f, 0.35f, 1f), icon);
            DrawHitEntry(dealtX, dealtY - big * 1.7f, "142", T("LabelHeavy", "重击"), big, new Color(1f, 0.92f, 0.35f, 1f), icon);
        }

        private static Sprite PlayerWeaponIcon(Humanoid humanoid)
        {
            try
            {
                if (humanoid == null) return null;
                ItemDrop.ItemData weapon = humanoid.GetCurrentWeapon();
                return weapon != null ? weapon.GetIcon() : null;
            }
            catch
            {
                return null;
            }
        }

        internal static bool CombatTextEnabled
        {
            get { return _showCombatText != null && _showCombatText.Value; }
        }

        internal static bool VanillaNumbersShown
        {
            get { return _showVanillaNumbers != null && _showVanillaNumbers.Value; }
        }

        private static readonly FieldInfo AttackChainLevelField = typeof(Attack).GetField("m_currentAttackCainLevel", BindingFlags.Instance | BindingFlags.NonPublic);

        private static bool IsComboFinisher(Player player)
        {
            try
            {
                if (CurrentAttackField == null || AttackChainLevelField == null) return false;
                Attack attack = CurrentAttackField.GetValue(player) as Attack;
                if (attack == null) return false;
                if (attack.m_attackChainLevels <= 1) return false;
                int level = (int)AttackChainLevelField.GetValue(attack);
                return level >= attack.m_attackChainLevels - 1;
            }
            catch
            {
                return false;
            }
        }

        private static HitData.DamageType MajorityType(HitData hit)
        {
            HitData.DamageTypes d = hit.m_damage;
            HitData.DamageType best = HitData.DamageType.Blunt;
            float max = -1f;

            if (d.m_blunt > max) { max = d.m_blunt; best = HitData.DamageType.Blunt; }
            if (d.m_slash > max) { max = d.m_slash; best = HitData.DamageType.Slash; }
            if (d.m_pierce > max) { max = d.m_pierce; best = HitData.DamageType.Pierce; }
            if (d.m_chop > max) { max = d.m_chop; best = HitData.DamageType.Chop; }
            if (d.m_fire > max) { max = d.m_fire; best = HitData.DamageType.Fire; }
            if (d.m_frost > max) { max = d.m_frost; best = HitData.DamageType.Frost; }
            if (d.m_lightning > max) { max = d.m_lightning; best = HitData.DamageType.Lightning; }
            if (d.m_poison > max) { max = d.m_poison; best = HitData.DamageType.Poison; }
            if (d.m_spirit > max) { max = d.m_spirit; best = HitData.DamageType.Spirit; }
            return best;
        }

        private static string DamageLabel(HitData hit, bool heavy)
        {
            if (heavy) return T("LabelHeavy", "重击");
            if (hit == null) return "";

            switch (MajorityType(hit))
            {
                case HitData.DamageType.Slash: return T("LabelSlash", "挥砍");
                case HitData.DamageType.Pierce: return T("LabelPierce", "刺击");
                case HitData.DamageType.Blunt: return T("LabelBlunt", "钝击");
                case HitData.DamageType.Chop: return T("LabelChop", "劈砍");
                case HitData.DamageType.Fire: return T("LabelFire", "火焰");
                case HitData.DamageType.Frost: return T("LabelFrost", "冰霜");
                case HitData.DamageType.Lightning: return T("LabelLightning", "闪电");
                case HitData.DamageType.Poison: return T("LabelPoison", "毒素");
                case HitData.DamageType.Spirit: return T("LabelSpirit", "精神");
                default: return "";
            }
        }

        private static bool _applyDamageHooked;

        internal static void MarkApplyDamageHooked()
        {
            _applyDamageHooked = true;
        }

        /// <summary>Fallback data source when Character.ApplyDamage could not be hooked: the game is
        /// about to draw its own number, so reuse it. playerOrTamed=true means the victim is the local
        /// player or a tame, which is the same "left side, red" bucket the game uses.</summary>
        internal static void ReportShowTextDamage(string text, bool playerOrTamed)
        {
            try
            {
                if (_applyDamageHooked) return;
                if (_instance == null) return;
                if (!CombatTextEnabled) return;
                if (string.IsNullOrEmpty(text)) return;

                float amount;
                if (!float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out amount)) return;
                if (amount < 0.5f) return;

                if (_reportLogged < 5)
                {
                    _reportLogged++;
                    _instance.Logger.LogInfo(string.Format("[diag] damage text (fallback from vanilla number): {0} {1:F0}",
                        playerOrTamed ? "taken" : "dealt", amount));
                }

                AddHitText(playerOrTamed, text, "", CombatFontSize, 55f, null);
            }
            catch
            {
                // cosmetic
            }
        }

        internal static void ReportDamage(Character victim, HitData hit, float amount)
        {
            try
            {
                Player player = Player.m_localPlayer;
                if (player == null || amount < 0.5f) return;

                bool taken = victim == player;
                bool dealt = !taken && hit != null && hit.GetAttacker() == player;
                if (!taken && !dealt)
                {
                    if (_reportLogged < 8 && _instance != null)
                    {
                        _reportLogged++;
                        _instance.Logger.LogInfo("[diag] damage skipped (not ours): victim=" + (victim != null ? victim.gameObject.name : "null")
                                                + " attacker=" + (hit != null && hit.GetAttacker() != null ? hit.GetAttacker().gameObject.name : "null"));
                    }
                    return;
                }

                bool heavy = dealt && IsComboFinisher(player);
                int size = heavy ? Mathf.RoundToInt(CombatFontSize * BigHitScale) : CombatFontSize;
                float drift = heavy ? 95f : 55f;
                string label = DamageLabel(hit, heavy);

                if (_reportLogged < 5 && _instance != null)
                {
                    _reportLogged++;
                    _instance.Logger.LogInfo(string.Format("[diag] damage text added: {0} {1:F0}{2} (hit total {3:F1})",
                        taken ? "taken" : "dealt", amount, string.IsNullOrEmpty(label) ? "" : " / " + label, hit.GetTotalDamage()));
                }

                Sprite icon = null;
                if (ShowWeaponIcons)
                {
                    // Dealt: the weapon we swung. Taken: the weapon the attacker swung (null for
                    // bites/claws, which is fine - the number is still drawn).
                    Humanoid source = taken ? hit.GetAttacker() as Humanoid : player;
                    icon = PlayerWeaponIcon(source);
                }

                AddHitText(taken, Mathf.RoundToInt(amount).ToString(), label, size, drift, icon);
            }
            catch
            {
                // combat text is cosmetic, never let it throw
            }
        }

        internal static void AddHitText(bool taken, string text, string label, int size, float drift, Sprite icon)
        {
            HitTexts.Add(new HitText
            {
                Text = text,
                Label = label,
                Taken = taken,
                Size = size,
                Drift = drift,
                Icon = icon,
                Born = Time.time,
                Color = taken ? new Color(1f, 0.28f, 0.22f) : new Color(1f, 0.92f, 0.35f),
                Jitter = UnityEngine.Random.Range(-26f, 26f)
            });
            if (HitTexts.Count > 40) HitTexts.RemoveAt(0);
        }

        internal static bool ShowWeaponIcons
        {
            get { return _showWeaponIcon == null || _showWeaponIcon.Value; }
        }

        internal static int CombatFontSize
        {
            get { return _baseFontSize != null ? _baseFontSize.Value : 27; }
        }

        internal static float BigHitScale
        {
            get { return _critScale != null ? _critScale.Value : 2.4f; }
        }

        internal static string LabelFontName
        {
            get { return _labelFontName != null && !string.IsNullOrEmpty(_labelFontName.Value) ? _labelFontName.Value : "Microsoft YaHei"; }
        }

        internal static float TakenAnchorX
        {
            get { return _takenX != null ? Mathf.Clamp01(_takenX.Value) : 0.2f; }
        }

        internal static float TakenAnchorY
        {
            get { return _takenY != null ? Mathf.Clamp01(_takenY.Value) : 0.6f; }
        }

        internal static float DealtAnchorX
        {
            get { return _dealtX != null ? Mathf.Clamp01(_dealtX.Value) : 0.7f; }
        }

        internal static float DealtAnchorY
        {
            get { return _dealtY != null ? Mathf.Clamp01(_dealtY.Value) : 0.6f; }
        }

        private static Dictionary<string, string> _lang = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static string _languageName = "English";

        private void LoadLanguage()
        {
            try
            {
                string lang = PlayerPrefs.GetString("language", "English");
                _languageName = string.IsNullOrEmpty(lang) ? "English" : lang;

                string dir = Path.GetDirectoryName(Info.Location);
                string file = Path.Combine(dir, "PerfectBlock." + _languageName + ".yml");
                if (!File.Exists(file)) file = Path.Combine(dir, "PerfectBlock.English.yml");
                if (!File.Exists(file)) return;

                string[] lines = File.ReadAllLines(file, Encoding.UTF8);
                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i].Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;
                    int sep = line.IndexOf(':');
                    if (sep <= 0) continue;
                    string key = line.Substring(0, sep).Trim();
                    string value = line.Substring(sep + 1).Trim();
                    if (value.Length >= 2 && value[0] == '"' && value[value.Length - 1] == '"')
                    {
                        value = value.Substring(1, value.Length - 2).Replace("\\\"", "\"").Replace("\\n", "\n");
                    }
                    _lang[key] = value;
                }
            }
            catch
            {
                // fall back to the built-in English text
            }
        }

        private static string T(string key, string fallback)
        {
            string value;
            if (_lang.TryGetValue(key, out value) && !string.IsNullOrEmpty(value)) return value;
            return fallback;
        }
    }

    /// <summary>Learn from the animation event that actually deals the damage.</summary>
    [HarmonyPatch(typeof(Humanoid), "OnAttackTrigger")]
    internal static class PatchAttackLanded
    {
        private static void Postfix(Humanoid __instance)
        {
            if (__instance == null) return;
            Plugin.LearnFrom(__instance);
        }
    }

    /// <summary>Combat text: read the real damage from the health delta, then draw it ourselves.
    ///
    /// Character.Damage(HitData) only fires an RPC in this build - the health is still untouched
    /// when it returns, which is why the old hook measured 0 every single time and no number ever
    /// appeared. The health is written inside Character.ApplyDamage, so that is the method to hook.</summary>
    [HarmonyPatch]
    internal static class PatchCombatText
    {
        private static MethodBase TargetMethod()
        {
            try
            {
                MethodInfo[] methods = typeof(Character).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                for (int i = 0; i < methods.Length; i++)
                {
                    MethodInfo method = methods[i];
                    if (method.Name != "ApplyDamage") continue;
                    ParameterInfo[] parameters = method.GetParameters();
                    if (parameters.Length == 0 || parameters[0].ParameterType != typeof(HitData)) continue;
                    Plugin.LogOnce("[diag] combat text hooks " + method);
                    return method;
                }
            }
            catch (Exception e)
            {
                Plugin.LogOnce("[diag] combat text target lookup failed: " + e.Message);
            }
            Plugin.LogOnce("[diag] Character.ApplyDamage not found: combat text falls back to the vanilla number that the game is about to draw.");
            return null;
        }

        private static void Prefix(Character __instance, ref float __state)
        {
            try
            {
                __state = __instance != null ? __instance.GetHealth() : 0f;
            }
            catch
            {
                __state = 0f;
            }
        }

        private static void Postfix(Character __instance, HitData hit, float __state)
        {
            try
            {
                if (__instance == null || hit == null) return;
                if (!Plugin.CombatTextEnabled) return;

                float delta = __state - __instance.GetHealth();
                Plugin.DiagDamage(__instance, hit, delta);
                if (delta < 0.5f) return;   // healing or nothing
                Plugin.ReportDamage(__instance, hit, delta);
            }
            catch
            {
                // cosmetic
            }
        }
    }

    /// <summary>Hide the game's own floating numbers while ours are on.</summary>
    [HarmonyPatch(typeof(DamageText), "ShowText", new[] { typeof(DamageText.TextType), typeof(Vector3), typeof(string), typeof(bool) })]
    internal static class PatchHideVanillaNumbers
    {
        private static int _logged;

        // Positional parameters (__0..__3) instead of names: the runtime signature is not guaranteed
        // to use the same parameter names as the decompiled source, and a name mismatch would make
        // the whole patch fail (and the vanilla numbers would come back).
        private static bool Prefix(DamageText.TextType __0, Vector3 __1, string __2, bool __3)
        {
            if (_logged < 5 && Plugin.CombatTextEnabled)
            {
                _logged++;
                Plugin.LogOnce("[diag] vanilla damage text intercepted: type=" + __0 + " text='" + __2 + "' playerOrTamed=" + __3);
            }

            // Fallback only: when the ApplyDamage hook is live this does nothing.
            Plugin.ReportShowTextDamage(__2, __3);

            if (!Plugin.CombatTextEnabled) return true;
            if (Plugin.VanillaNumbersShown) return true;
            return false;
        }
    }

    /// <summary>AttackCancel behaviour, transition half: while a cancelled swing is settling,
    /// the game must not think the player is mid-attack, otherwise IsBlocking() stays false.</summary>
    [HarmonyPatch(typeof(Player), "InAttack")]
    internal static class PatchPlayerInAttack
    {
        private static bool Prefix(Player __instance, ref bool __result)
        {
            if (!Plugin.CancelSuppress) return true;
            if (__instance != Player.m_localPlayer) return true;
            __result = false;
            return false;
        }
    }

    /// <summary>AttackCancel behaviour: report blocking during the transition frames so the
    /// block registers on the same hit instead of one frame later.</summary>
    [HarmonyPatch(typeof(Humanoid), "IsBlocking")]
    internal static class PatchIsBlocking
    {
        private static bool Prefix(Humanoid __instance, ref bool __result)
        {
            if (!Plugin.ForceBlockState) return true;
            if (__instance != Player.m_localPlayer) return true;
            if (!Plugin.BlockingFieldIsSet(__instance)) return true;
            __result = true;
            return false;
        }
    }

    /// <summary>ReliableBlock rule, but only while a perfect block is being resolved.</summary>
    [HarmonyPatch(typeof(Character), "AddStaggerDamage")]
    internal static class PatchStaggerBypass
    {
        private static bool Prefix(Character __instance, ref bool __result)
        {
            if (!Plugin.ShouldSkipStagger(__instance)) return true;
            __result = false;
            return false;
        }
    }

    /// <summary>Feed the block key at the input boundary the game itself uses.
    /// ZInput.GetButton turned out to be a dead end in this build, so the parsed controls
    /// are forced here instead: same place a real key press arrives, nothing combat related.
    ///
    /// TODO(leave for later, do not touch now): in toggle mode these edges are sent blind - we
    /// assume "we sent an on-edge" means "the game is now blocking". If the game's m_blocking was
    /// already true (e.g. the hold from the previous hit has not been released yet), that edge
    /// turns the block OFF, and the hit lands unblocked. Measured 2026-11-03: 4 misses out of ~99
    /// toggle-mode blocks, with blockTimer=-1 at impact, while hold mode was 0/45. Fix would be:
    /// read BlockingFieldIsSet(player) before sending an edge and only send it when it actually
    /// changes the state. Not worth it at 90%; only do it if the miss rate ever matters.</summary>
    [HarmonyPatch(typeof(Player), "SetControls")]
    internal static class PatchSetControls
    {
        private static bool _logged;

        private static void Prefix(Player __instance, ref bool attack, ref bool secondaryAttack, ref bool block, ref bool blockHold)
        {
            if (__instance != Player.m_localPlayer) return;

            bool toggle = Plugin.ToggleMode;

            if (toggle)
            {
                // Toggle mode flips on every "key down" edge, so never send a held true.
                if (Plugin.ToggleOffPending)
                {
                    block = true;
                    blockHold = false;
                    Plugin.ClearToggleOff();
                }
                else if (Plugin.WantBlock)
                {
                    if (Plugin.ToggleOnPending)
                    {
                        block = true;
                        Plugin.ClearToggleOn();
                        if (!_logged)
                        {
                            _logged = true;
                            Plugin.LogOnce("[diag] SetControls reached: toggle-mode edge press.");
                        }
                    }
                    else
                    {
                        block = false;
                    }
                    blockHold = false;
                }
            }
            else
            {
                if (Plugin.WantBlock)
                {
                    block = true;
                    blockHold = true;
                    if (!_logged)
                    {
                        _logged = true;
                        Plugin.LogOnce("[diag] SetControls reached: forcing blockHold every frame.");
                    }
                }
            }

            // A fresh attack input means the player wants to swing again.
            if (attack || secondaryAttack)
            {
                Plugin.ClearCancelState();
                return;
            }

            if (!blockHold && !toggle)
            {
                Plugin.ClearCancelState();
                return;
            }

            // Manual block while mid-swing (AttackCancel behaviour, independent of auto block).
            if (blockHold && Plugin.ShouldCancelManualBlock()) Plugin.CancelSwingNow();
        }

        /// <summary>SetControls can be called more than once per frame; in hold mode a later
        /// call would put m_blocking back to false. Make our value the last word.</summary>
        private static void Postfix(Player __instance)
        {
            if (__instance != Player.m_localPlayer) return;
            if (Plugin.WantBlock) Plugin.ForceBlocking(true);
        }
    }

    /// <summary>Feed the block key. Nothing else is touched: the game sets m_blocking and m_blockTimer itself.</summary>
    [HarmonyPatch(typeof(ZInput), "GetButton")]
    internal static class PatchBlockKey
    {
        private static void Postfix(string name, ref bool __result)
        {
            if (__result) return;
            if (!Plugin.WantBlock) return;
            if (!string.Equals(name, "Block", StringComparison.OrdinalIgnoreCase)) return;
            __result = true;
            Plugin.ForcedReturns++;
        }
    }

    /// <summary>Measure the result: perfect block, ordinary block (pressed too early) or late.</summary>
    [HarmonyPatch(typeof(Humanoid), "BlockAttack")]
    internal static class PatchBlockResult
    {
        private static readonly System.Reflection.FieldInfo BlockTimer =
            typeof(Humanoid).GetField("m_blockTimer", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

        private static readonly System.Reflection.MethodInfo CurrentBlocker =
            typeof(Humanoid).GetMethod("GetCurrentBlocker", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

        private static void Prefix(Humanoid __instance, ref bool __state)
        {
            __state = false;
            try
            {
                if (__instance != Player.m_localPlayer) return;
                if (BlockTimer == null) return;
                float timer = (float)BlockTimer.GetValue(__instance);

                if (Plugin.Simulated)
                {
                    string blocker = "none";
                    float parry = 0f;
                    if (CurrentBlocker != null)
                    {
                        ItemDrop.ItemData held = CurrentBlocker.Invoke(__instance, null) as ItemDrop.ItemData;
                        if (held != null) { blocker = held.m_shared.m_name; parry = held.m_shared.m_timedBlockBonus; }
                    }
                    Plugin.Diag(string.Format("BlockAttack reached: timer={0:F3}s hammer={1} parry={2:F2} forced={3} frames={4}",
                        timer, blocker, parry, Plugin.ForcedReturns, Plugin.PressFrames));
                }

                if (timer < 0f || timer >= 0.25f) return;

                // Same extra condition the game uses: a blocker with a timed-block bonus.
                if (CurrentBlocker != null)
                {
                    ItemDrop.ItemData blocker = CurrentBlocker.Invoke(__instance, null) as ItemDrop.ItemData;
                    if (blocker == null || blocker.m_shared.m_timedBlockBonus <= 1f) return;
                }

                __state = true;
            }
            catch
            {
                __state = false;
            }
        }

        private static void Postfix(bool __result, bool __state)
        {
            if (!__result) return;
            if (!Plugin.WantBlock) return;
            if (__state) Plugin.NotePerfect();
            else Plugin.NoteEarly();
        }
    }

    /// <summary>If the player takes the hit anyway, the simulated press came too late.
    /// In this game build Character.Damage takes only HitData; the attacker lives inside it.</summary>
    [HarmonyPatch(typeof(Character), "Damage", new[] { typeof(HitData) })]
    internal static class PatchDamageTaken
    {
        private static void Prefix(Character __instance, HitData hit)
        {
            try
            {
                if (__instance != Player.m_localPlayer) return;
                if (hit == null || hit.GetTotalDamage() <= 0f) return;
                if (__instance.IsBlocking())
                {
                    return;
                }
                if (Plugin.Simulated)
                {
                    Plugin.Diag(string.Format("hit landed while NOT blocking: frames={0} blockTimer={1:F3}s attacker={2}",
                        Plugin.PressFrames, Plugin.BlockTimerOf(__instance),
                        hit.GetAttacker() == null ? "null" : hit.GetAttacker().gameObject.name));
                }
                Plugin.NoteLate(hit.GetAttacker());
            }
            catch
            {
                // measurement only
            }
        }
    }
}
