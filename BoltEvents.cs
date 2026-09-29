using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using HarmonyLib;
using MelonLoader;
using UnhollowerBaseLib;
using UnityEngine;
using CMS;

[assembly: MelonInfo(typeof(BoltEvents.BoltEventsMod), "BoltEvents", "1.6.1", "MidCrusadero", "")]
[assembly: MelonGame("Red Dot Games", "Car Mechanic Simulator 2021")]

namespace BoltEvents
{
    // Random events when fighting rusted stuck bolts, both with WD-40
    // (spray coroutine postfix) and with a slipping ratchet
    // (MountObject.PlayStuckAnimation postfix):
    //
    //  - "WD-40 didn't help" (ChanceWd40FailedPercent, default 15%):
    //    the bolt seizes back (IsStuck = true), player has to spray again.
    //
    //  - "The bolt gave way" (ChanceUnscrewNoWd40Percent, default 15%):
    //    pure force loosens the bolt without WD-40, awards
    //    BoltGaveWayExpReward (default 5, 0 = none) XP.
    //
    //  - "Stripped threads" (ChanceStrippedThreadsPercent / 4% WD-40 path,
    //    ChanceSlipStrippedPercent / 7% ratchet path): the bolt is flagged;
    //    when it later comes off, the part it held loses
    //    StrippedConditionLossPercent (default 5%) of its current condition,
    //    the player pays StrippedMoneyPenalty (default 50) CR and gains
    //    StrippedExpReward (default 5, 0 = none) XP.
    //
    //  1.6.1: input guard after "WD-40 didn't help" — if the mouse button
    //  is still held from the pre-spray unscrew attempt, the game keeps
    //  driving its rotation flow against the freshly re-stuck bolt (ratchet
    //  sound loops, cursor gets dragged back to the bolt). For
    //  Wd40FailGuardSeconds (default 1.5) the guard re-asserts IsStuck,
    //  keeps the cursor free and blocks new bolt actions until the button
    //  is released.
    //
    // Configuration: <game>\Mods\BoltEvents\BoltEvents.cfg (INI-style,
    // UTF-8, "#" or ";" comments, split on the FIRST "="). The file is
    // re-read before every event roll, so chance and text edits apply
    // without restarting the game.
    public class BoltEventsMod : MelonMod
    {
        private static bool cfgEnabled = true;
        private static int cfgWd40Fail = 15;
        private static int cfgStripped = 4;
        private static int cfgNoWd40 = 15;
        private static int cfgSlipStripped = 7;
        private static int cfgSeize = 10;
        private static bool cfgDebug = false;

        // "Stripped threads" outcome: percent of the part's CURRENT condition
        // lost when the flagged bolt comes off, plus the money/experience hit.
        private static int cfgCondLossPercent = 5;
        private static int cfgMoneyPenalty = 50;
        private static int cfgExpReward = 5;

        // "The bolt gave way" outcome: XP for loosening a stuck bolt by
        // pure force (0 = no reward).
        private static int cfgGaveExpReward = 5;

        // Anti-glitch guard after "WD-40 didn't help", seconds. While the
        // mouse button is still held from the pre-spray attempt the mod
        // keeps the bolt stuck and the cursor free, so the game's rotation
        // flow cannot loop against the re-stuck bolt.
        private static float cfgWd40GuardSeconds = 1.5f;

        // All popup phrases are user-configurable, per language. {0} in the
        // stripped-threads texts is replaced with the condition loss value.
        private static string cfgTitleRu = "Bolt Events";
        private static string cfgTitleEn = "Bolt Events";
        private static string cfgWd40Ru =
            "WD-40 не помогла — болт закис сильнее, чем казалось. Пшикни ещё раз.";
        private static string cfgWd40En =
            "WD-40 didn't help — the bolt was more stuck than it looked. Spray it again.";
        private static string cfgGaveRu =
            "Болт пошёл! Силой и упорством ты открутил его без WD-40.";
        private static string cfgGaveEn =
            "The bolt gave way! Pure force and stubbornness — no WD-40 needed.";
        private static string cfgStrRu =
            "Грани сорваны! Откручивая болт, ты повредил крепление — деталь потеряла {0}% состояния. Заодно поцарапаны нервы (-50 CR).";
        private static string cfgStrEn =
            "Stripped threads! Undoing the bolt damaged the mount — the part lost {0}% condition. Nerves scratched too (-50 CR).";
        private static string cfgSeizeRu =
            "Болт оказался прикипевшим! Попробуй смазать WD-40 (ПКМ) или рискни открутить силой трещоткой (ЛКМ).";
        private static string cfgSeizeEn =
            "The bolt turned out to be seized! Spray some WD-40 (RMB) or risk muscling it off with the ratchet (LMB).";

        private static readonly System.Random Rnd = new System.Random();
        private static readonly HashSet<IntPtr> Spraying = new HashSet<IntPtr>();
        private static readonly HashSet<IntPtr> StrippedBolts = new HashSet<IntPtr>();
        private static readonly HashSet<IntPtr> SeizedBolts = new HashSet<IntPtr>();
        private static readonly Dictionary<IntPtr, float> LastSlipRoll = new Dictionary<IntPtr, float>();
        private static readonly HashSet<IntPtr> Wd40Guard = new HashSet<IntPtr>();

        private static string ConfigPath
        {
            get
            {
                return Path.Combine(
                    Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location),
                    "BoltEvents", "BoltEvents.cfg");
            }
        }

        private static void ReloadConfig()
        {
            try
            {
                if (!File.Exists(ConfigPath))
                {
                    WriteDefaultConfig();
                    return; // static fields already hold the defaults
                }
                foreach (string raw in File.ReadAllLines(ConfigPath, Encoding.UTF8))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line[0] == '#' || line[0] == ';') continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string key = line.Substring(0, eq).Trim();
                    string val = line.Substring(eq + 1).Trim();
                    if (val.Length >= 2 && val[0] == '"' && val[val.Length - 1] == '"')
                        val = val.Substring(1, val.Length - 2);
                    ApplyConfigKey(key, val);
                }
            }
            catch (Exception e)
            {
                MelonLogger.Warning("BoltEvents: config read failed, keeping defaults: " + e.Message);
            }
        }

        private static void ApplyConfigKey(string key, string val)
        {
            switch (key)
            {
                case "Enabled": cfgEnabled = ParseBool(val, cfgEnabled); break;
                case "ChanceWd40FailedPercent": cfgWd40Fail = ParseInt(val, cfgWd40Fail); break;
                case "ChanceStrippedThreadsPercent": cfgStripped = ParseInt(val, cfgStripped); break;
                case "ChanceUnscrewNoWd40Percent": cfgNoWd40 = ParseInt(val, cfgNoWd40); break;
                case "ChanceSlipStrippedPercent": cfgSlipStripped = ParseInt(val, cfgSlipStripped); break;
                case "ChanceSeizeNormalBoltPercent": cfgSeize = ParseInt(val, cfgSeize); break;
                case "DebugLog": cfgDebug = ParseBool(val, cfgDebug); break;
                case "StrippedConditionLossPercent": cfgCondLossPercent = ParseInt(val, cfgCondLossPercent); break;
                case "StrippedMoneyPenalty": cfgMoneyPenalty = ParseInt(val, cfgMoneyPenalty); break;
                case "StrippedExpReward": cfgExpReward = ParseInt(val, cfgExpReward); break;
                case "BoltGaveWayExpReward": cfgGaveExpReward = ParseInt(val, cfgGaveExpReward); break;
                case "Wd40FailGuardSeconds": cfgWd40GuardSeconds = ParseFloat(val, cfgWd40GuardSeconds); break;
                case "TitleRU": cfgTitleRu = val; break;
                case "TitleEN": cfgTitleEn = val; break;
                case "Wd40FailedRU": cfgWd40Ru = val; break;
                case "Wd40FailedEN": cfgWd40En = val; break;
                case "BoltGaveWayRU": cfgGaveRu = val; break;
                case "BoltGaveWayEN": cfgGaveEn = val; break;
                case "StrippedRU": cfgStrRu = val; break;
                case "StrippedEN": cfgStrEn = val; break;
                case "BoltSeizedRU": cfgSeizeRu = val; break;
                case "BoltSeizedEN": cfgSeizeEn = val; break;
            }
        }

        private static int ParseInt(string val, int fallback)
        {
            return int.TryParse(val, out int n) ? n : fallback;
        }

        private static float ParseFloat(string val, float fallback)
        {
            return float.TryParse(val,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float f) ? f : fallback;
        }

        private static bool ParseBool(string val, bool fallback)
        {
            switch (val.Trim().ToLowerInvariant())
            {
                case "true": case "1": case "yes": case "on": return true;
                case "false": case "0": case "no": case "off": return false;
                default: return fallback;
            }
        }

        // First run (or a deleted config): create Mods\BoltEvents\ and drop a
        // documented default file there so users see every available key.
        private static void WriteDefaultConfig()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath));
                string[] lines =
                {
                    "# BoltEvents configuration",
                    "#",
                    "# Location: <game>\\Mods\\BoltEvents\\BoltEvents.cfg",
                    "# Encoding: UTF-8. Comments start with '#' or ';'.",
                    "# The file is re-read before every event roll - edit and play,",
                    "# no game restart needed.",
                    "",
                    "# Master switch for all events.",
                    "Enabled = true",
                    "",
                    "# Event chances, percent (0-100).",
                    "# WD-40 sprayed, bolt seized back - spray again.",
                    "ChanceWd40FailedPercent = 15",
                    "# WD-40 sprayed, threads stripped - part damage when the bolt comes off.",
                    "ChanceStrippedThreadsPercent = 4",
                    "# Ratchet slipped, bolt gave way without WD-40.",
                    "ChanceUnscrewNoWd40Percent = 15",
                    "# Ratchet slipped, threads stripped - part damage when the bolt comes off.",
                    "ChanceSlipStrippedPercent = 7",
                    "# Normal bolt seizes mid-unscrew - it becomes stuck and needs",
                    "# ratchet clicks (LMB) or WD-40 (RMB), where the events above apply.",
                    "ChanceSeizeNormalBoltPercent = 10",
                    "",
                    "# Input guard after \"WD-40 didn't help\", seconds: while the mouse",
                    "# button is still held from the pre-spray attempt, the mod keeps the",
                    "# bolt stuck and the cursor free so the game's rotation flow cannot",
                    "# loop against the re-stuck bolt (stuck ratchet sound / dragged",
                    "# cursor). 0 disables the guard.",
                    "Wd40FailGuardSeconds = 1.5",
                    "",
                    "# \"Stripped threads\" outcome: percent of the part's CURRENT",
                    "# condition lost (100% part -> 95 at default), money penalty in CR",
                    "# (charged only if you can afford it) and experience reward (0 = none).",
                    "StrippedConditionLossPercent = 5",
                    "StrippedMoneyPenalty = 50",
                    "StrippedExpReward = 5",
                    "",
                    "# \"The bolt gave way\" outcome: experience reward (0 = none).",
                    "BoltGaveWayExpReward = 5",
                    "",
                    "# Verbose logging to MelonLoader console / log file.",
                    "DebugLog = false",
                    "",
                    "# Popup texts, Russian and English (the mod picks one by the",
                    "# game's current UI language). {0} = condition loss percent.",
                    "TitleRU = Bolt Events",
                    "TitleEN = Bolt Events",
                    "Wd40FailedRU = WD-40 не помогла — болт закис сильнее, чем казалось. Пшикни ещё раз.",
                    "Wd40FailedEN = WD-40 didn't help — the bolt was more stuck than it looked. Spray it again.",
                    "BoltGaveWayRU = Болт пошёл! Силой и упорством ты открутил его без WD-40.",
                    "BoltGaveWayEN = The bolt gave way! Pure force and stubbornness — no WD-40 needed.",
                    "StrippedRU = Грани сорваны! Откручивая болт, ты повредил крепление — деталь потеряла {0}% состояния. Заодно поцарапаны нервы (-50 CR).",
                    "StrippedEN = Stripped threads! Undoing the bolt damaged the mount — the part lost {0}% condition. Nerves scratched too (-50 CR).",
                    "BoltSeizedRU = Болт оказался прикипевшим! Попробуй смазать WD-40 (ПКМ) или рискни открутить силой трещоткой (ЛКМ).",
                    "BoltSeizedEN = The bolt turned out to be seized! Spray some WD-40 (RMB) or risk muscling it off with the ratchet (LMB).",
                };
                File.WriteAllLines(ConfigPath, lines, new UTF8Encoding(false));
                MelonLogger.Msg("BoltEvents: default config written to " + ConfigPath);
            }
            catch (Exception e)
            {
                MelonLogger.Warning("BoltEvents: cannot write default config: " + e.Message);
            }
        }

        public override void OnLateInitializeMelon()
        {
            ReloadConfig();
            Log("config: " + ConfigPath);
            try
            {
                HarmonyLib.Harmony h = new HarmonyLib.Harmony("local.boltevents");

                MethodInfo slip = typeof(MountObject).GetMethod("PlayStuckAnimation",
                    BindingFlags.Instance | BindingFlags.Public);
                if (slip == null)
                {
                    MelonLogger.Warning("BoltEvents: MountObject.PlayStuckAnimation not found");
                }
                else
                {
                    h.Patch(slip,
                        postfix: new HarmonyMethod(typeof(BoltEventsMod).GetMethod("SlipPostfix", BindingFlags.Static | BindingFlags.NonPublic)));
                    MelonLogger.Msg("BoltEvents: MountObject.PlayStuckAnimation patched (ratchet slip events)");
                }

                // The real WD-40 hook: ShowMountObjectSpray() is a tiny iterator
                // factory that IL2CPP very likely inlines into ToolsManager.Use,
                // so its postfix never fires. The coroutine's MoveNext cannot be
                // inlined — patch it and read the hoisted bolt from the state
                // machine instance instead.
                Type iterType = typeof(ToolsManager).GetNestedType("_ShowMountObjectSpray_d__36",
                    BindingFlags.Public | BindingFlags.NonPublic);
                MethodInfo moveNext = iterType == null ? null :
                    iterType.GetMethod("MoveNext", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (moveNext == null)
                {
                    MelonLogger.Warning("BoltEvents: spray iterator MoveNext not found");
                }
                else
                {
                    sprayBoltProperty = iterType.GetProperty("_mountObject_5__3",
                        BindingFlags.Public | BindingFlags.Instance);
                    h.Patch(moveNext,
                        postfix: new HarmonyMethod(typeof(BoltEventsMod).GetMethod("SprayMoveNextPostfix", BindingFlags.Static | BindingFlags.NonPublic)));
                    MelonLogger.Msg("BoltEvents: WD-40 coroutine MoveNext patched (WD-40 events)");
                }

                // Normal-bolt seize: roll the chance when the game opens an
                // unscrew session. GameScript.SelectToUnMount arrives with the
                // EXACT bolt list the session works on — no cursor/hover
                // guessing. Runs as a POSTFIX: IsStuck gets set right after
                // the session is built, so the first ratchet pull already
                // plays the vanilla stuck-bolt flow (no rotation state, no
                // cursor lock).
                MethodInfo selectUn = typeof(GameScript).GetMethod("SelectToUnMount",
                    BindingFlags.Instance | BindingFlags.Public);
                if (selectUn == null)
                {
                    MelonLogger.Warning("BoltEvents: GameScript.SelectToUnMount not found");
                }
                else
                {
                    h.Patch(selectUn,
                        postfix: new HarmonyMethod(typeof(BoltEventsMod).GetMethod("SelectToUnMountPostfix",
                            BindingFlags.Static | BindingFlags.NonPublic)));
                    MelonLogger.Msg("BoltEvents: GameScript.SelectToUnMount patched (seize on unscrew entry)");
                }

                // Hides the seizure until the first ratchet slip: the game's
                // orange outline paint is rewritten to the bolt's normal
                // color while the bolt is in the hidden set. Both the steady
                // paint (On) and the hover pulse (FlashingOn) are covered.
                MethodInfo hlOn = typeof(Highlighter).GetMethod("On",
                    BindingFlags.Instance | BindingFlags.Public);
                if (hlOn == null)
                {
                    MelonLogger.Warning("BoltEvents: Highlighter.On not found");
                }
                else
                {
                    h.Patch(hlOn,
                        prefix: new HarmonyMethod(typeof(BoltEventsMod).GetMethod("HighlighterOnPrefix",
                            BindingFlags.Static | BindingFlags.NonPublic)));
                    MelonLogger.Msg("BoltEvents: Highlighter.On patched (hidden seize outline)");
                }
                MethodInfo hlFlash1 = typeof(Highlighter).GetMethod("FlashingOn",
                    BindingFlags.Instance | BindingFlags.Public, null,
                    new Type[] { typeof(Color) }, null);
                if (hlFlash1 == null)
                {
                    MelonLogger.Warning("BoltEvents: Highlighter.FlashingOn(Color) not found");
                }
                else
                {
                    h.Patch(hlFlash1,
                        prefix: new HarmonyMethod(typeof(BoltEventsMod).GetMethod("HighlighterFlashingOnPrefix",
                            BindingFlags.Static | BindingFlags.NonPublic)));
                    MelonLogger.Msg("BoltEvents: Highlighter.FlashingOn(Color) patched (hidden seize outline)");
                }
                MethodInfo hlFlash3 = typeof(Highlighter).GetMethod("FlashingOn",
                    BindingFlags.Instance | BindingFlags.Public, null,
                    new Type[] { typeof(Color), typeof(Color), typeof(float) }, null);
                if (hlFlash3 == null)
                {
                    MelonLogger.Warning("BoltEvents: Highlighter.FlashingOn(Color,Color,float) not found");
                }
                else
                {
                    h.Patch(hlFlash3,
                        prefix: new HarmonyMethod(typeof(BoltEventsMod).GetMethod("HighlighterFlashingOn3Prefix",
                            BindingFlags.Static | BindingFlags.NonPublic)));
                    MelonLogger.Msg("BoltEvents: Highlighter.FlashingOn(Color,Color,float) patched (hidden seize outline)");
                }

            }
            catch (Exception e)
            {
                MelonLogger.Error("BoltEvents: patch failed: " + e);
            }
        }

        private static PropertyInfo sprayBoltProperty;

        private static void Log(string msg)
        {
            if (cfgDebug) MelonLogger.Msg("[BoltEvents] " + msg);
        }

        private static int _sprayTicks, _slipCalls;

        // Event popup texts in Russian and English; the game's current UI
        // language is detected via the value of a well-known localization
        // key (Cyrillic -> Russian).
        private static string LastLang = "?";

        private static bool IsRussian()
        {
            try
            {
                GameManager gm = Singleton<GameManager>.Instance;
                if (gm == null || gm.Localization == null || gm.Localization.CurrentLanguage == null)
                    return false;
                string v = gm.Localization.CurrentLanguage.GetString("GUI_Pause_ContinueButton", "");
                for (int i = 0; i < v.Length; i++)
                {
                    char c = v[i];
                    if (c >= (char)0x400 && c <= (char)0x4FF) return true;
                }
                return false;
            }
            catch (Exception)
            {
                return false;
            }
        }

        // One language probe per popup, then both strings come from that
        // single result (never probe twice — results must not diverge).
        private static bool PopupLang()
        {
            bool ru = IsRussian();
            string lang = ru ? "RU" : "EN";
            if (lang != LastLang)
            {
                LastLang = lang;
                Log("detected UI language: " + lang);
            }
            return ru;
        }

        // The WD-40 coroutine ticks here on every frame of the spray. The
        // hoisted <mountObject>5__3 is a PROPERTY on the interop wrapper (not
        // a field). First tick with a non-null bolt starts the watcher;
        // the Spraying set dedupes the remaining ticks.
        private static void SprayMoveNextPostfix(Il2CppSystem.Object __instance)
        {
            MountObject bolt = null;
            try
            {
                if (sprayBoltProperty != null)
                    bolt = sprayBoltProperty.GetValue(__instance, null) as MountObject;
            }
            catch (Exception e)
            {
                MelonLogger.Warning("BoltEvents: cannot read spray bolt: " + e.Message);
            }
            if (bolt == null) return;

            IntPtr key = bolt.Pointer;
            lock (Spraying)
            {
                if (!Spraying.Add(key)) return; // already watching this bolt
            }
            _sprayTicks++;
            Log("SPRAY tick #" + _sprayTicks + " bolt=" + key + " stuck=" + bolt.IsStuck);
            ReloadConfig();
            if (!cfgEnabled) { lock (Spraying) { Spraying.Remove(key); } return; }
            MelonCoroutines.Start(WatchSpray(bolt, key));
        }

        private static IEnumerator WatchSpray(MountObject bolt, IntPtr key)
        {
            // wait until WD-40 actually loosens the bolt (60 s cap)
            float timeout = 60f;
            while (bolt != null && bolt.IsStuck && timeout > 0f)
            {
                timeout -= Time.deltaTime;
                yield return null;
            }
            lock (Spraying) { Spraying.Remove(key); }
            lock (PendingSeizeVisual) { PendingSeizeVisual.Remove(key); }
            ClearHiddenOutline(key);
            if (bolt == null) { Log("spray watcher: bolt destroyed"); yield break; }
            if (bolt.IsStuck) { Log("spray watcher: timeout, still stuck"); yield break; }
            if (bolt.unmounted) { Log("spray watcher: bolt already off"); yield break; }
            Log("spray watcher: bolt loosened, rolling");
            ReloadConfig();

            int roll = Rnd.Next(100);
            Log("roll=" + roll);
            if (roll < cfgStripped)
            {
                StartStrippedWatch(bolt);
            }
            else if (roll < cfgStripped + cfgWd40Fail)
            {
                bolt.IsStuck = true;
                ReleaseCursorLock();
                ShowEventPopup(cfgWd40Ru, cfgWd40En);
                // If the player is still holding LMB from the pre-spray
                // unscrew attempt, the game keeps driving its rotation
                // flow against the re-stuck bolt (ratchet sound loops,
                // cursor dragged back). Guard until the button is released.
                StartWd40Guard(bolt, key);
            }
        }

        // Blocks the stuck-bolt state in place while the mouse button is
        // still held after a "WD-40 didn't help" event. Every frame of the
        // guard: re-assert IsStuck, keep the cursor free and disable new
        // bolt actions, so the game's per-frame rotation flow has nothing
        // to chew on. Ends when LMB is released, the bolt goes away, or
        // the guard window expires. Wd40FailGuardSeconds = 0 disables it.
        private static void StartWd40Guard(MountObject bolt, IntPtr key)
        {
            if (cfgWd40GuardSeconds <= 0f) return;
            lock (Wd40Guard)
            {
                if (!Wd40Guard.Add(key)) return; // already guarded
            }
            MelonCoroutines.Start(GuardWd40FailedBolt(bolt, key, cfgWd40GuardSeconds));
        }

        private static IEnumerator GuardWd40FailedBolt(MountObject bolt, IntPtr key, float guardSeconds)
        {
            Log("WD40-fail guard ON bolt=" + key + " for " + guardSeconds + "s");
            // Disable new bolt actions for the guard window (the game's own
            // rotation loop does not consult it, but click-driven paths do).
            bool hadCanAction = true, touchedCanAction = false;
            try
            {
                hadCanAction = bolt.canAction;
                bolt.canAction = false;
                touchedCanAction = true;
            }
            catch { }
            try
            {
                float t = guardSeconds;
                while (bolt != null && t > 0f)
                {
                    t -= Time.deltaTime;
                    if (bolt.unmounted) break;
                    if (!Input.GetMouseButton(0)) break; // released: vanilla stuck flow resumes
                    try { bolt.IsStuck = true; } catch { }
                    ReleaseCursorLock();
                    yield return null;
                }
            }
            finally
            {
                if (touchedCanAction)
                {
                    try { bolt.canAction = hadCanAction; } catch { }
                }
                lock (Wd40Guard) { Wd40Guard.Remove(key); }
                Log("WD40-fail guard OFF bolt=" + key);
            }
        }

        // Ratchet slip on a stuck bolt (left click without WD-40):
        //  - ChanceUnscrewNoWd40Percent: the bolt gives way and loosens;
        //  - ChanceSlipStrippedPercent: "stripped threads" flag for this bolt.
        private static void SlipPostfix(MountObject __instance)
        {
            try
            {
                SlipPostfixSafe(__instance);
            }
            catch (Exception e)
            {
                MelonLogger.Warning("BoltEvents: slip handler failed: " + e.Message);
            }
        }

        private static void SlipPostfixSafe(MountObject __instance)
        {
            _slipCalls++;
            if (__instance == null)
            {
                Log("SLIP call #" + _slipCalls + " instance=null");
                return;
            }
            IntPtr key = __instance.Pointer;
            Log("SLIP call #" + _slipCalls + " ptr=" + key + " stuck=" + __instance.IsStuck +
                " unmounted=" + __instance.unmounted + " playing=" + __instance.PlayingStuckAnim);
            ReloadConfig();
            if (!cfgEnabled) return;
            if (!__instance.IsStuck || __instance.unmounted) return;
            // A fresh "WD-40 didn't help" guard owns this bolt until the
            // button is released — no slip rolls during the hand-over.
            lock (Wd40Guard)
            {
                if (Wd40Guard.Contains(key)) return;
            }

            lock (LastSlipRoll)
            {
                if (LastSlipRoll.TryGetValue(key, out float last) && Time.time - last < 2f)
                {
                    Log("slip dedup skip (dt=" + (Time.time - last).ToString("0.00") + ")");
                    return;
                }
                LastSlipRoll[key] = Time.time;
            }

            // First slip of a bolt that seized when the session opened: this
            // is the moment the seizure becomes visible. Unhide the orange
            // outline + show the popup here instead of at session entry, and
            // skip the slip event roll for this click.
            lock (PendingSeizeVisual)
            {
                if (PendingSeizeVisual.Remove(key))
                {
                    ClearHiddenOutline(key);
                    MarkBoltStuckVisual(__instance);
                    ShowEventPopup(cfgSeizeRu, cfgSeizeEn);
                    return;
                }
            }

            int roll = Rnd.Next(100);
            Log("slip roll=" + roll);
            if (roll < cfgSlipStripped)
            {
                StartStrippedWatch(__instance);
            }
            else if (roll < cfgSlipStripped + cfgNoWd40)
            {
                __instance.IsStuck = false;
                AwardExp(cfgGaveExpReward);
                ShowEventPopup(cfgGaveRu, cfgGaveEn);
            }
        }

        // Shared XP award; silently skipped when the configured value is 0.
        private static void AwardExp(int amount)
        {
            if (amount == 0) return;
            try
            {
                GlobalData.AddExpAmount = amount;
                UIManager.Get().RefreshStatsUICoroutine(StatType.Experience);
            }
            catch (Exception e)
            {
                MelonLogger.Warning("BoltEvents: XP award failed: " + e.Message);
            }
        }

        // Seize when the game opens an unscrew session: SelectToUnMount
        // arrives with the exact bolt list the session will work on. Runs
        // AFTER the original method, so the session is already built and the
        // bolt still looks completely normal — the player cannot tell it will
        // seize. IsStuck is set before any ratchet pull, so the first
        // attempt plays the vanilla stuck-bolt flow.
        private static void SelectToUnMountPostfix(PartScript target,
            Il2CppReferenceArray<MountObject> mountObjects)
        {
            try
            {
                SeizeOnSelectToUnMount(mountObjects);
            }
            catch (Exception e)
            {
                MelonLogger.Warning("BoltEvents: seize failed: " + e.Message);
            }
        }

        private static void SeizeOnSelectToUnMount(Il2CppReferenceArray<MountObject> mountObjects)
        {
            ReloadConfig();
            if (!cfgEnabled) return;
            if (mountObjects == null || mountObjects.Length == 0)
            {
                Log("seize@select: empty bolt list");
                return;
            }

            // Collect every fully-screwed, not-already-stuck bolt that has
            // not seized yet, then pick one at random — otherwise with a
            // fixed array order the same first bolt would seize every time.
            List<MountObject> eligible = null;
            for (int i = 0; i < mountObjects.Length; i++)
            {
                MountObject m = mountObjects[i];
                if (m == null) continue;
                if (cfgDebug)
                    Log("seize candidate ptr=" + m.Pointer + " stuck=" + m.IsStuck +
                        " reverse=" + m.reverseMode + " mountState=" + m.mountState +
                        " unmounted=" + m.unmounted + " playing=" + m.PlayingStuckAnim);
                if (m.IsStuck || m.reverseMode || m.mountState < 0.99f) continue;
                lock (SeizedBolts)
                {
                    if (SeizedBolts.Contains(m.Pointer)) continue;
                }
                if (eligible == null) eligible = new List<MountObject>();
                eligible.Add(m);
            }
            if (eligible == null)
            {
                Log("seize@select: no eligible bolt in list of " + mountObjects.Length);
                return;
            }

            int roll = Rnd.Next(100);
            if (roll >= cfgSeize)
            {
                Log("seize@select roll=" + roll + " -> no seize");
                return;
            }
            MountObject bolt = eligible[Rnd.Next(eligible.Count)];
            IntPtr key = bolt.Pointer;
            Log("seize@select roll=" + roll + " -> bolt=" + key +
                " (picked 1 of " + eligible.Count + ")");
            // Set IsStuck right away: the session then plays the vanilla
            // stuck-bolt flow from the first click (no rotation state, no
            // cursor lock). The orange outline the game paints from this
            // flag is hidden by the Highlighter prefixes, which rewrite
            // orange paints to the bolt's display color until the first
            // ratchet slip reveals the seizure.
            IntPtr hoPtr = IntPtr.Zero;
            try
            {
                if (bolt.ho != null) hoPtr = bolt.ho.Pointer;
            }
            catch { }
            lock (SeizedBolts) { SeizedBolts.Add(key); }
            lock (PendingSeizeVisual) { PendingSeizeVisual.Add(key); }
            lock (HiddenOutline)
            {
                HiddenOutline[key] = new HiddenBolt { Ho = hoPtr, Current = ColRest };
            }
            bolt.IsStuck = true;
            MelonCoroutines.Start(HoverSeizeSim(bolt, key));
            MelonCoroutines.Start(WatchSeizedRelease(bolt, key));
        }

        // The game never paints a hover color for a stuck bolt, so the hidden
        // seized bolt gets its hover feedback simulated here: at rest the
        // outline is the normal bolt-outline yellow (#dac81a), when the
        // player aims at it the outline turns green — exactly like a normal
        // bolt. The game's steady/pulsing orange paints are rewritten to the
        // same color by the Highlighter prefixes. "Aimed" is cursor proximity
        // on screen — the only detector that proved to work in the unmount
        // session (raycast/hover flags/game mouse-over never fire there).
        private static readonly Color ColRest = new Color(218f / 255f, 200f / 255f, 26f / 255f);
        private static readonly Color ColHover = new Color(0f, 1f, 0f);

        private static IEnumerator HoverSeizeSim(MountObject bolt, IntPtr key)
        {
            bool aimed = false;
            float timeout = 120f;
            bool enteredSession = false;
            while (timeout > 0f)
            {
                timeout -= Time.deltaTime;
                lock (PendingSeizeVisual)
                {
                    if (!PendingSeizeVisual.Contains(key)) yield break; // revealed
                }
                if (bolt == null) yield break;

                // Session abandoned (ESC) before the bolt was ever touched:
                // roll the latent seize back, otherwise every re-entry would
                // pile another seized bolt on top of the untouched ones.
                try
                {
                    GameMode gm = GameMode.Get();
                    if (gm != null)
                    {
                        gameMode mode = gm.GetCurrentMode();
                        if (mode == gameMode.PartUnMount) enteredSession = true;
                        else if (enteredSession)
                        {
                            RevertHiddenSeize(bolt, key);
                            yield break;
                        }
                    }
                }
                catch { }

                bool nowAimed = false;
                try
                {
                    Camera cam = Camera.main;
                    if (cam != null)
                    {
                        Vector3 sp = cam.WorldToScreenPoint(bolt.transform.position);
                        if (sp.z > 0f)
                        {
                            nowAimed = Vector2.Distance(
                                new Vector2(sp.x, sp.y),
                                new Vector2(Input.mousePosition.x, Input.mousePosition.y)) < 80f;
                        }
                    }
                }
                catch { }

                if (nowAimed != aimed)
                {
                    aimed = nowAimed;
                    Color now = aimed ? ColHover : ColRest;
                    lock (HiddenOutline)
                    {
                        HiddenBolt hb;
                        if (HiddenOutline.TryGetValue(key, out hb)) hb.Current = now;
                    }
                    Log("hover-sim bolt=" + key + " aimed=" + aimed);
                    try { if (bolt.ho != null) bolt.ho.On(now); } catch { }
                }
                yield return null;
            }
        }

        // Bolts seized at session entry whose orange outline is hidden and
        // whose popup waits for the first ratchet slip.
        private static readonly HashSet<IntPtr> PendingSeizeVisual = new HashSet<IntPtr>();

        // Hidden seized bolts: key = bolt pointer. The game's orange outline
        // paints (Highlighter.On / FlashingOn) are rewritten to Current while
        // the entry is present; any other color (aim/hover) passes through
        // untouched. Removed when the seizure is revealed. The set never
        // holds more than a couple of bolts, so lookups by highlighter are a
        // plain linear scan.
        private class HiddenBolt
        {
            public IntPtr Ho;
            public Color Current;
        }
        private static readonly Dictionary<IntPtr, HiddenBolt> HiddenOutline =
            new Dictionary<IntPtr, HiddenBolt>();

        // Roll back a latent seize the player never saw (session abandoned):
        // the bolt returns to a fully normal state and is eligible again.
        private static void RevertHiddenSeize(MountObject bolt, IntPtr key)
        {
            lock (SeizedBolts) { SeizedBolts.Remove(key); }
            lock (PendingSeizeVisual) { PendingSeizeVisual.Remove(key); }
            ClearHiddenOutline(key);
            try { if (bolt != null) bolt.IsStuck = false; } catch { }
            Log("seize reverted: session abandoned, bolt back to normal");
        }

        private static void ClearHiddenOutline(IntPtr key)
        {
            lock (HiddenOutline) { HiddenOutline.Remove(key); }
        }

        // Rewrite an orange-ish paint call on a hidden seized bolt's
        // highlighter to the bolt's current display color (set by the hover
        // simulation). Any other color passes through untouched.
        private static void TryRewriteHidden(Highlighter inst, ref Color c)
        {
            if (inst == null) return;
            bool orange = c.r > 0.9f && c.g > 0.25f && c.g < 0.75f && c.b < 0.35f;
            if (!orange) return;
            lock (HiddenOutline)
            {
                foreach (KeyValuePair<IntPtr, HiddenBolt> kv in HiddenOutline)
                {
                    if (kv.Value.Ho == inst.Pointer)
                    {
                        c = kv.Value.Current;
                        return;
                    }
                }
            }
        }

        private static void HighlighterOnPrefix(Highlighter __instance, ref Color __0)
        {
            try { TryRewriteHidden(__instance, ref __0); }
            catch (Exception e)
            {
                MelonLogger.Warning("BoltEvents: outline hide failed: " + e.Message);
            }
        }

        private static void HighlighterFlashingOnPrefix(Highlighter __instance, ref Color __0)
        {
            try { TryRewriteHidden(__instance, ref __0); }
            catch (Exception e)
            {
                MelonLogger.Warning("BoltEvents: outline hide (flash) failed: " + e.Message);
            }
        }

        private static void HighlighterFlashingOn3Prefix(Highlighter __instance,
            ref Color __0, ref Color __1)
        {
            try
            {
                TryRewriteHidden(__instance, ref __0);
                TryRewriteHidden(__instance, ref __1);
            }
            catch (Exception e)
            {
                MelonLogger.Warning("BoltEvents: outline hide (flash3) failed: " + e.Message);
            }
        }



        // Vanilla stuck bolts are marked at car generation
        // (CarLoader.SetMountObjectsStuck) — that also gives them their rust
        // look and orange highlight. A bolt that seized mid-game gets ONLY
        // the orange outline: its texture stays untouched, so before the
        // seizure shows the player sees a perfectly normal bolt.
        private static void MarkBoltStuckVisual(MountObject bolt)
        {
            try
            {
                if (bolt.ho != null) bolt.ho.On(new Color(1f, 0.55f, 0.1f));
            }
            catch (Exception e)
            {
                MelonLogger.Warning("BoltEvents: highlight failed: " + e.Message);
            }
        }

        // The game centers and hides the cursor while rotating a bolt; if the
        // bolt seizes outside the vanilla stuck-flow, that lock can persist.
        // Force the cursor free so the player can aim WD-40 or keep clicking.
        private static void ReleaseCursorLock()
        {
            try
            {
                if (Cursor.lockState != CursorLockMode.None)
                {
                    Cursor.lockState = CursorLockMode.None;
                    Log("cursor lock released");
                }
                if (!Cursor.visible) Cursor.visible = true;
            }
            catch (Exception e)
            {
                MelonLogger.Warning("BoltEvents: cannot release cursor: " + e.Message);
            }
        }

        // A seized bolt cannot seize again until it comes off (then the flag
        // is cleared, so a remounted bolt is eligible again). 2 Hz is plenty.
        // Doubles as a cursor-lock watchdog: in PartUnMount the cursor is
        // legitimately Locked only while LMB is held; Locked with the button
        // released for ~1 s means the lock got stuck — release it.
        private static IEnumerator WatchSeizedRelease(MountObject bolt, IntPtr key)
        {
            float timeout = 600f;
            float lockedIdle = 0f;
            while (bolt != null && !bolt.unmounted && timeout > 0f)
            {
                timeout -= 0.5f;
                try
                {
                    GameMode gm = GameMode.Get();
                    bool inUnMount = gm != null && gm.GetCurrentMode() == gameMode.PartUnMount;
                    if (inUnMount && !Input.GetMouseButton(0) &&
                        Cursor.lockState == CursorLockMode.Locked)
                    {
                        lockedIdle += 0.5f;
                        if (lockedIdle >= 1f)
                        {
                            ReleaseCursorLock();
                            lockedIdle = 0f;
                        }
                    }
                    else
                    {
                        lockedIdle = 0f;
                    }
                }
                catch { }
                yield return new WaitForSeconds(0.5f);
            }
            lock (SeizedBolts) { SeizedBolts.Remove(key); }
            lock (PendingSeizeVisual) { PendingSeizeVisual.Remove(key); }
            ClearHiddenOutline(key);
        }

        private static void StartStrippedWatch(MountObject bolt)
        {
            IntPtr k2 = bolt.Pointer;
            lock (StrippedBolts)
            {
                if (StrippedBolts.Contains(k2)) return; // already flagged
                StrippedBolts.Add(k2);
            }
            MelonCoroutines.Start(WatchStripped(bolt, k2));
        }

        private static IEnumerator WatchStripped(MountObject bolt, IntPtr key)
        {
            // wait until the flagged bolt actually comes off (5 min cap)
            float timeout = 300f;
            while (bolt != null && !bolt.unmounted && timeout > 0f)
            {
                timeout -= Time.deltaTime;
                yield return null;
            }
            lock (StrippedBolts) { StrippedBolts.Remove(key); }
            if (bolt == null || !bolt.unmounted) yield break;

            PartScript part = bolt.GetComponentInParent<PartScript>();
            if (part == null) yield break;

            float damage = part.Condition * (cfgCondLossPercent / 100f);
            part.SetCondition(part.Condition - damage);

            if (GlobalData.PlayerMoney >= cfgMoneyPenalty)
            {
                GlobalData.AddMoneyAmount = -cfgMoneyPenalty;
                UIManager.Get().RefreshStatsUICoroutine(StatType.Money);
            }
            AwardExp(cfgExpReward);

            // Condition is a 0..1 fraction; show the loss in percent points.
            string dmg = (damage * 100f).ToString("0.#");
            ReloadConfig();
            ShowEventPopup(cfgStrRu.Replace("{0}", dmg),
                           cfgStrEn.Replace("{0}", dmg));
        }

        // Single language probe per popup: title and body always match.
        private static void ShowEventPopup(string bodyRu, string bodyEn)
        {
            bool ru = PopupLang();
            string title = ru ? cfgTitleRu : cfgTitleEn;
            string body = ru ? bodyRu : bodyEn;
            Log("popup: title='" + title + "' body='" + body + "'");
            UIManager.Get().ShowPopup(title, body, PopupType.Normal);
        }
    }
}