using p5rpc.SocialStatTracker.Configuration;
using p5rpc.SocialStatTracker.Template;
using p5rpc.lib.interfaces;
using static p5rpc.lib.interfaces.Sequence;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.Enums;
using Reloaded.Hooks.Definitions.X64;
using Reloaded.Memory.Sigscan.Definitions.Structs;
using Reloaded.Memory.SigScan.ReloadedII.Interfaces;
using Reloaded.Mod.Interfaces;
using System.Diagnostics;
using Reloaded.Memory;
using static Reloaded.Hooks.Definitions.X64.FunctionAttribute;
using IReloadedHooks = Reloaded.Hooks.ReloadedII.Interfaces.IReloadedHooks;

using static p5rpc.SocialStatTracker.Utils;

namespace p5rpc.SocialStatTracker
{
    /// <summary>
    /// Your mod logic goes here.
    /// </summary>
    public unsafe class Mod : ModBase // <= Do not Remove.
    {
        /// <summary>
        /// Provides access to the mod loader API.
        /// </summary>
        private readonly IModLoader _modLoader;

        /// <summary>
        /// Provides access to the Reloaded.Hooks API.
        /// </summary>
        /// <remarks>This is null if you remove dependency on Reloaded.SharedLib.Hooks in your mod.</remarks>
        private readonly IReloadedHooks? _hooks;

        /// <summary>
        /// Provides access to the Reloaded logger.
        /// </summary>
        private readonly ILogger _logger;

        /// <summary>
        /// Entry point into the mod, instance that created this class.
        /// </summary>
        private readonly IMod _owner;

        /// <summary>
        /// Provides access to this mod's configuration.
        /// </summary>
        private Config _configuration;

        /// <summary>
        /// The configuration of the currently executing mod.
        /// </summary>
        private readonly IModConfig _modConfig;

        private IAsmHook _textHook;
        private IAsmHook _textLvlUpHook;
        private IAsmHook _lvlUpGetStatHook;
        private IAsmHook _getStatHook;
        private IReverseWrapper<AddPointsNeededFunc> _addPointsNeededReverseWrapper;
        private short* _socialStatPoints;

        // Last observed point total per stat (-1 = not seen yet, so loading a save doesn't count as a gain)
        private short[] _lastPoints = { -1, -1, -1, -1, -1 };
        private short[] _lastGain = new short[5];

        private long[] _lastGainTime = new long[5];
        private long[] _lastDrawTime = new long[5];
        private bool[] _trackerShowing = new bool[5];

        // Whether we're in normal gameplay (field/calendar) as opposed to an event, battle etc.
        // Defaults to true so trackers stay menu-hidden rather than menu-shown if we never hear otherwise.
        private bool _inGameplay = true;
        private bool _sequencerAvailable;
        private EventInfo? _lastEvent;

        // Jumps bigger than this are treated as loading a different save, not a gain
        private const int MaxPlausibleGain = 30;
        // Gains recorded within this window count as one event; a newer gain clears older trackers
        private const int BurstWindowMs = 5000;
        // While a stat's text keeps being redrawn with gaps smaller than this, a shown tracker never vanishes mid-display
        private const int DrawContinuityMs = 2000;
        // Don't let a return to gameplay clear a gain this recent, its presentation may not have played yet
        private const int SequenceClearGraceMs = 10000;

        // One reusable native buffer per stat for the strings we hand back to the game
        private const int StringBufferSize = 256;
        private nuint _stringBuffers;

        private int* _currentSocialStat;
        private int* _currentStatLevel;

        private nuint _downwardMoveConst;

        public Mod(ModContext context)
        {
            _modLoader = context.ModLoader;
            _hooks = context.Hooks;
            _logger = context.Logger;
            _owner = context.Owner;
            _configuration = context.Configuration;
            _modConfig = context.ModConfig;

            Initialise(_logger, _configuration, _modLoader);

            var p5rLibController = _modLoader.GetController<IP5RLib>();
            if (p5rLibController != null && p5rLibController.TryGetTarget(out var p5rLib))
            {
                p5rLib.Sequencer.SequenceChanged += OnSequenceChanged;
                _sequencerAvailable = true;
                Log("Using p5rpc.lib to track game state, gain trackers will clear when returning to gameplay.");
            }
            else
                Log("Couldn't get p5rpc.lib, gain trackers will only clear based on the configured display time.");

            var memory = Memory.Instance;
            _currentSocialStat = (int*)memory.Allocate(4).Address;
            _currentStatLevel = (int*)memory.Allocate(4).Address;
            _stringBuffers = memory.Allocate(StringBufferSize * 5).Address;

            var addPointsCall = _hooks.Utilities.GetAbsoluteCallMnemonics(AddPointsNeeded, out _addPointsNeededReverseWrapper);
            
            SigScan("4C 8D 35 ?? ?? ?? ?? 0F B7 FD", "Social stat points", address =>
            {
                _socialStatPoints = (short*)GetGlobalAddress((nuint)address + 3);
                LogDebug($"Social stat points start at 0x{(nuint)_socialStatPoints:X}");
                StartGainWatcher();
            });
            
            SigScan("49 6B C0 64", "Social Stat Level", address =>
            {
                if (_socialStatPoints == null) return;
                
                string[] function =
                {
                    "use64",
                    $"mov [qword {(nuint)_currentSocialStat}], r8d",
                    $"mov [qword {(nuint)_currentStatLevel}], eax",
                };
                
                _getStatHook = _hooks.CreateAsmHook(function, address, AsmHookBehaviour.ExecuteFirst).Activate();
            });


            SigScan("E8 ?? ?? ?? ?? 4C 8B 0D ?? ?? ?? ?? 49 FF C4", "Social stat text", address =>
            {
                if (_socialStatPoints == null) return;
            
                string[] function =
                {
                    "use64",
                    $"push r8 \npush rdx \npush rcx \npush r9 \npush r11",
                    $"{PushXmm(0)}\n{PushXmm(4)}\n{PushXmm(1)}",
                    "mov rcx, r8", // Current stat name string
                    $"mov rdx, {(nuint)_currentSocialStat}", // Stat id
                    "mov rdx, [rdx]",
                    $"mov r8, {(nuint)_currentStatLevel}", // Stat level
                    "mov r8, [r8]",
                    "sub rsp, 40",
                    addPointsCall,
                    "add rsp, 40",
                    $"{PopXmm(1)}\n{PopXmm(4)}\n{PopXmm(0)}",
                    "pop r11 \npop r9 \npop rcx \npop rdx\npop r8",
                    "mov [rsp + 0x30], rax"
                 };
                _textHook = _hooks.CreateAsmHook(function, address, AsmHookBehaviour.ExecuteFirst).Activate();
            });
            
            SigScan("46 0F B7 5C ?? ?? 46 0F B7 54 ?? ??", "Social Stat Gain Stat Level", address =>
            {
                if (_socialStatPoints == null) return;
                
                string[] function =
                {
                    "use64",
                    $"mov [qword {(nuint)_currentSocialStat}], r14d",
                    $"mov [qword {(nuint)_currentStatLevel}], r10d",
                };
                
                _lvlUpGetStatHook = _hooks.CreateAsmHook(function, address, AsmHookBehaviour.ExecuteAfter).Activate();
            });

            SigScan("E8 ?? ?? ?? ?? 4C 8B 05 ?? ?? ?? ?? 49 FF C5", "Social stat gain", address =>
            {
                if (_socialStatPoints == null) return;

                string[] function =
                {
                    "use64",
                    $"push r8 \npush rdx \npush rcx \npush r9 \npush r11",
                    "mov r8, r10",
                    $"{PushXmm(0)}\n{PushXmm(4)}\n{PushXmm(5)}\n{PushXmm(1)}",
                    "mov rcx, r8", // Current stat name string
                    $"mov rdx, {(nuint)_currentSocialStat}", // Stat id
                    "mov rdx, [rdx]",
                    $"mov r8, {(nuint)_currentStatLevel}", // Stat level
                    "mov r8, [r8]",
                    "sub rsp, 40",
                    addPointsCall,
                    "add rsp, 40",
                    $"{PopXmm(1)}\n{PopXmm(5)}\n{PopXmm(4)}\n{PopXmm(0)}",
                    "pop r11 \npop r9 \npop rcx \npop rdx",
                    "mov [rsp + 0x38], rax",
                    "pop r8",
                 };
                _textLvlUpHook = _hooks.CreateAsmHook(function, address, AsmHookBehaviour.ExecuteFirst).Activate();
            });

        }
        
        // Polls the stat points so a baseline exists as soon as a save is loaded and
        // gains are picked up without the player having to open any menu
        private void StartGainWatcher()
        {
            var watcher = new Thread(() =>
            {
                while (true)
                {
                    for (int i = 0; i < 5; i++)
                        UpdateGainTracking(i);
                    Thread.Sleep(200);
                }
            });
            watcher.IsBackground = true;
            watcher.Start();
        }

        private void UpdateGainTracking(int socialStat)
        {
            short currentPoints = _socialStatPoints[socialStat];
            short lastSeen = _lastPoints[socialStat];
            if (currentPoints == lastSeen) return;
            _lastPoints[socialStat] = currentPoints;
            if (lastSeen < 0) return; // First time seeing this stat, just take the baseline
            int gained = currentPoints - lastSeen;
            if (gained <= 0 || gained > MaxPlausibleGain)
            {
                _lastGain[socialStat] = 0; // Save loaded rather than points gained, forget the old gain
                return;
            }
            _lastGain[socialStat] = (short)gained;
            long now = Environment.TickCount64;
            _lastGainTime[socialStat] = now;

            // This is a new gain event, trackers from earlier events shouldn't show anymore
            for (int i = 0; i < 5; i++)
            {
                if (i == socialStat || _lastGain[i] == 0) continue;
                if (now - _lastGainTime[i] > BurstWindowMs)
                    _lastGain[i] = 0;
            }
            LogDebug($"Stat {socialStat} gained {gained} points ({lastSeen} -> {currentPoints})");
        }

        // Back in normal gameplay (or the day ended) means any finished gain presentations are over.
        // Transitions between event, movie etc are ignored so multi-part presentations survive them.
        private void OnSequenceChanged(SequenceInfo sequence)
        {
            // On current game versions the raw sequence values sit one higher than p5rpc.lib's enum
            // labels (free roam reads as BATTLE, events as EVENT_VIEWER, day transition as
            // CALENDAR_RESET), so accept both the shifted and unshifted values for gameplay
            _inGameplay = sequence.CurrentSequence is SequenceType.FIELD or SequenceType.BATTLE
                or SequenceType.CALENDAR or SequenceType.CALENDAR_RESET;
            LogDebug($"Sequence changed to {sequence.CurrentSequence} (gameplay: {_inGameplay})");

            // A different event starting also means anything an earlier event gave is old news
            bool newEvent = sequence.EventInfo != null && !sequence.EventInfo.Equals(_lastEvent);
            if (sequence.EventInfo != null)
                _lastEvent = sequence.EventInfo;

            if (!_inGameplay && !newEvent)
                return;
            string reason = _inGameplay ? "returned to gameplay" : $"event {sequence.EventInfo} started";
            long now = Environment.TickCount64;
            for (int i = 0; i < 5; i++)
            {
                if (_lastGain[i] == 0 || now - _lastGainTime[i] < SequenceClearGraceMs) continue;
                _lastGain[i] = 0;
                LogDebug($"Cleared gain tracker for stat {i} ({reason})");
            }
        }

        // A tracker displays if its gain is recent enough (or no time limit is set), and once
        // on screen it stays until the text stops being drawn so it can't vanish mid-display
        private short GetDisplayedGain(int socialStat)
        {
            short gain = _lastGain[socialStat];
            long now = Environment.TickCount64;
            bool show = gain > 0;
            if (show && !(_trackerShowing[socialStat] && now - _lastDrawTime[socialStat] < DrawContinuityMs))
            {
                int displaySeconds = _configuration.GainDisplaySeconds;
                show = displaySeconds <= 0 || now - _lastGainTime[socialStat] < displaySeconds * 1000L;

                // Outside of events (i.e. checking your stats in the menu) trackers only
                // show if the user wants them there or the gain literally just happened
                if (show && _inGameplay && _sequencerAvailable && !_configuration.ShowInMenu
                    && now - _lastGainTime[socialStat] >= SequenceClearGraceMs)
                    show = false;
            }
            _trackerShowing[socialStat] = show;
            _lastDrawTime[socialStat] = now;
            return show ? gain : (short)0;
        }

        private const char Arrow = '→';

        private string AddPointsNeededSuffix(int socialStat, int level)
        {
            level--; // We want a 0 based level, this is 1 based
            UpdateGainTracking(socialStat); // Catch gains applied the same frame the text is drawn
            short currentPoints = _socialStatPoints[socialStat];
            short gain = GetDisplayedGain(socialStat);

            bool showArrow = gain > 0 && _configuration.ShowGainArrow;
            string plusSuffix = gain > 0 && _configuration.ShowGainPlus ? $" (+{gain})" : "";

            if (level > 4) level = GetSocialStatLevel(socialStat, currentPoints); // Normal way breaks when a stat levels up :(
            short lastPointsNeeded = _pointsNeeded[socialStat][level];
            if (level == 4)
            {
                int extraPoints = currentPoints - lastPointsNeeded;
                if (showArrow)
                    return $" +{Math.Max(0, extraPoints - gain)}{Arrow}+{extraPoints}{plusSuffix}";
                if (extraPoints == 0)
                    return plusSuffix;
                return $" +{extraPoints}{plusSuffix}";
            }
            short pointsNeeded = _pointsNeeded[socialStat][level + 1];
            int current = currentPoints - lastPointsNeeded;
            int needed = pointsNeeded - lastPointsNeeded;
            if (showArrow)
                return $" {Math.Max(0, current - gain)}{Arrow}{current}/{needed}{plusSuffix}";
            return $" {current}/{needed}{plusSuffix}";
        }

        // Builds the final string natively so we control the exact bytes the game sees.
        // The game uses ATLUS's custom double byte text encoding, which no marshaller speaks:
        // ascii is ascii but special glyphs like the arrow are two bytes from the game's charset table.
        private nint AddPointsNeeded(nint currentString, int socialStat, int level)
        {
            string suffix = AddPointsNeededSuffix(socialStat, level);
            if (suffix.Length == 0)
                return currentString;

            byte* buffer = (byte*)(_stringBuffers + (nuint)(socialStat * StringBufferSize));
            int pos = 0;

            // Copy the original stat name string as-is
            byte* source = (byte*)currentString;
            while (pos < StringBufferSize - 8 && source[pos] != 0)
            {
                buffer[pos] = source[pos];
                pos++;
            }

            foreach (char c in suffix)
            {
                if (pos >= StringBufferSize - 8) break;
                if (c == Arrow)
                {
                    // '→' in the P5R charset (glyph 367 in P5R_EFIGS.tsv): 0x82 0xCF
                    buffer[pos++] = 0x82;
                    buffer[pos++] = 0xCF;
                }
                else
                    buffer[pos++] = (byte)c;
            }
            buffer[pos] = 0;
            return (nint)buffer;
        }

        private short[][] _pointsNeeded =
        {
            new short[]{ 0, 34, 82, 126, 192},
            new short[]{ 0, 6, 52, 92, 132},
            new short[]{ 0, 12, 34, 60, 87},
            new short[]{ 0, 11, 38, 68, 113},
            new short[]{ 0, 14, 44, 91, 136},
        };

        // Ideally we don't do this for speed but when a stat levels up the level becomes 5 (which it isn't really)
        // Could probably find the actual level somewhere in a register or stack but I cba
        private int GetSocialStatLevel(int socialStat, int currentPoints)
        {
            short[] pointsNeeded = _pointsNeeded[socialStat];
            for(int i = 4; i >= 0; i--)
            {
                if (currentPoints >= pointsNeeded[i])
                    return i;
            }
            return 0;
        }

        private delegate nint AddPointsNeededFunc(nint currentString, int socialStat, int level);

        #region Standard Overrides
        public override void ConfigurationUpdated(Config configuration)
        {
            // Apply settings from configuration.
            // ... your code here.
            _configuration = configuration;
            _logger.WriteLine($"[{_modConfig.ModId}] Config Updated: Applying");
        }
        #endregion

        #region For Exports, Serialization etc.
#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
        public Mod() { }
#pragma warning restore CS8618
        #endregion
    }
}