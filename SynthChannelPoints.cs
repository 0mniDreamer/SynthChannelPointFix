using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MelonLoader;
using MelonLoader.Utils;
using UnityEngine;

[assembly: MelonInfo(typeof(SynthChannelPoints.Core), "SynthChannelPoints", "1.2.2", "OmniDreamer")]
[assembly: MelonGame(null, null)]

namespace SynthChannelPoints
{
    /// <summary>
    /// Restores Twitch channel point redemptions in Synth Riders (EventSub bridge)
    /// and, as of v1.1.0, optionally manages the channel point rewards themselves:
    /// auto-creates rewards named after chat commands, mirrors the game's Twitch
    /// settings toggles to reward enabled state, and refunds failed song requests.
    ///
    /// Reward management uses the channel:manage:redemptions scope, which the mod
    /// obtains by extending the game's own requestedScopes list — the game's
    /// validation cascade then walks the streamer through a one-time re-consent
    /// using its normal auth flow.
    /// </summary>
    public class Core : MelonMod
    {
        private const string ManageScope = "channel:manage:redemptions";
        private const int RefundDeadlineSeconds = 30; // live-observed QueueAdd latency up to ~16s

        // ------------------------------------------------------------------
        // Config
        // ------------------------------------------------------------------

        private MelonPreferences_Entry<bool> _enabled;
        private MelonPreferences_Entry<bool> _debugLogging;
        private MelonPreferences_Entry<string> _eventSubUrl;
        private MelonPreferences_Entry<string> _helixSubUrl;
        private MelonPreferences_Entry<string> _helixBaseUrl;
        private MelonPreferences_Entry<bool> _suppressGamePubSub;
        private MelonPreferences_Entry<bool> _manageRewards;
        private MelonPreferences_Entry<string> _rewardCommands;
        private MelonPreferences_Entry<string> _commandPrefix;
        private MelonPreferences_Entry<string> _rewardDefinitions;
        private MelonPreferences_Entry<bool> _refundFailedRequests;
        private MelonPreferences_Entry<bool> _autoCompleteRequests;
        private MelonPreferences_Entry<bool> _dedupeRedemptions;
        private MelonPreferences_Entry<bool> _disableRewardsOnExit;
        private MelonPreferences_Entry<bool> _rewardsFollowCpm;

        // ------------------------------------------------------------------
        // Cross-branch candidate name sets
        // ------------------------------------------------------------------

        private static readonly string[] TwitchBotTypeCandidates =
        {
            "Il2CppSynth.Twitch.TwitchBot",
            "Synth.Twitch.TwitchBot",
            "Il2Cpp.TwitchBot",
            "TwitchBot"
        };

        private static readonly string[] PubSubTypeCandidates =
        {
            "Il2CppTwitchLib.PubSub.TwitchPubSub",
            "TwitchLib.PubSub.TwitchPubSub"
        };

        private static readonly string[] SettingsTypeCandidates =
        {
            "Il2Cpp.TwitchSettings",
            "TwitchSettings"
        };

        private static readonly string[] InstanceMemberCandidates = { "s_instance", "GetInstance" };

        private static readonly string[] SingletonMemberCandidates =
        {
            "s_instance", "instance", "Instance", "_instance",
            "current", "Current", "GetInstance", "Singleton", "singleton"
        };

        private static readonly string[] SkipAssemblyPrefixes =
        {
            "System", "mscorlib", "netstandard", "Microsoft.",
            "MelonLoader", "0Harmony", "Il2CppInterop", "Mono.",
            "SynthChannelPoints"
        };

        // Maps chat command -> game TwitchSettings feature toggle properties.
        // Multiple candidates are OR'd if listed. srr maps strictly to
        // RewardRequest: it is a real in-game toggle (empirically confirmed —
        // tester flipped it and the mirror followed), and the game likely gates
        // redemption handling on it, so creating the reward while it's off
        // would produce always-refunding redemptions.
        private static readonly Dictionary<string, string[]> CommandToggleMap =
            new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            { "srr", new[] { "RewardRequest" } },
            { "timewarp", new[] { "Timewarp" } },
            { "speed", new[] { "Speed" } },
            { "superspeed", new[] { "Superspeed" } },
            { "color", new[] { "Color" } },
            { "rainbow", new[] { "Rainbow" } },
            { "vanish", new[] { "Vanish" } },
            { "embiggen", new[] { "Embiggen" } },
            { "minimize", new[] { "Minimize" } },
            { "warp", new[] { "Warp" } },
            { "invaderz", new[] { "Invaderz" } }
        };

        // ------------------------------------------------------------------
        // Reward specs (parsed from config)
        // ------------------------------------------------------------------

        private sealed class RewardSpec
        {
            public string Command;
            public string Title;
            public string Prompt;
            public long Cost;
            public bool RequiresInput;
        }

        private List<RewardSpec> _rewardSpecs = new List<RewardSpec>();

        // ------------------------------------------------------------------
        // Game references (main thread only)
        // ------------------------------------------------------------------

        private Type _botType;
        private object _bot;
        private object _pubsub;
        private MethodInfo _testMessageParser;
        private bool _gameReady;
        private bool _suppressionApplied;
        private bool _scopeMutationDone;
        private bool _queueAddPatched;
        private bool _queueAddPatchAttempted;
        private HarmonyLib.Harmony _harmony;

        // Settings holder (discovered at runtime; read on main thread)
        private bool _settingsSearchDone;
        private Func<object> _settingsReader;

        // ------------------------------------------------------------------
        // Cross-thread state
        // ------------------------------------------------------------------

        private sealed class Creds
        {
            public string AccessToken;
            public string ClientId;
            public string UserId;
            public string Username;
            public bool IsComplete =>
                !string.IsNullOrEmpty(AccessToken) &&
                !string.IsNullOrEmpty(ClientId) &&
                !string.IsNullOrEmpty(UserId);
        }

        private volatile Creds _creds;
        private volatile Dictionary<string, bool> _desiredEnabled; // reward title -> enabled
        private volatile bool _syncNow; // set when desired state changes; manage loop reacts within ~2s
        private volatile bool _canManage;
        private string _effectiveClientId; // set by validate; read cross-thread under _stateLock
        private readonly object _stateLock = new object();

        // Managed rewards: reward id -> spec (populated by the manage loop)
        private readonly Dictionary<string, RewardSpec> _managedRewards = new Dictionary<string, RewardSpec>();

        // Pending song-request redemptions awaiting a queue-add signal
        private sealed class Pending
        {
            public string RedemptionId;
            public string RewardId;
            public string UserLogin;
            public string UserName;
            public DateTime DeadlineUtc;
        }

        private readonly List<Pending> _pending = new List<Pending>();
        private static readonly ConcurrentQueue<string> QueueAddSignals = new ConcurrentQueue<string>();

        // ------------------------------------------------------------------
        // Client state
        // ------------------------------------------------------------------

        private static MelonLogger.Instance SLog;
        private static MelonPreferences_Entry<bool> DebugEntry;
        private static bool DedupeEnabled;
        private static int _dedupeBlockCount;
        private static readonly ConcurrentDictionary<string, DateTime> RecentRedemptionIds =
            new ConcurrentDictionary<string, DateTime>();

        private Task _clientTask;
        private Task _manageTask;
        private CancellationTokenSource _cts;
        private readonly ConcurrentQueue<string> _injectQueue = new ConcurrentQueue<string>();
        private int _frameCounter;
        private bool _announcedActive;
        private bool _announcedDormant;

        public override void OnInitializeMelon()
        {
            SLog = LoggerInstance;
            var cat = MelonPreferences.CreateCategory("SynthChannelPoints");

            // Own config file instead of the shared MelonPreferences.cfg.
            // First run after updating: no dedicated file exists yet, so let the
            // entries load their values from the legacy [SynthChannelPoints]
            // section, then rehome them (migration). Every later run loads the
            // dedicated file directly.
            var configPath = Path.Combine(MelonEnvironment.UserDataDirectory, "SynthChannelPoints.cfg");
            var migrateLegacyConfig = !File.Exists(configPath);
            if (!migrateLegacyConfig)
            {
                cat.SetFilePath(configPath, true, false);
            }

            _enabled = cat.CreateEntry("Enabled", true,
                description: "Master switch. Set false to disable the mod without removing it.");
            _debugLogging = cat.CreateEntry("DebugLogging", false,
                description: "Verbose diagnostic output.");
            DebugEntry = _debugLogging;
            _eventSubUrl = cat.CreateEntry("EventSubUrl", "wss://eventsub.wss.twitch.tv/ws",
                description: "EventSub WebSocket endpoint. Point at ws://127.0.0.1:8080/ws to test against the Twitch CLI mock server.");
            _helixSubUrl = cat.CreateEntry("HelixSubscriptionsUrl", "https://api.twitch.tv/helix/eventsub/subscriptions",
                description: "Helix subscriptions endpoint. Point at http://127.0.0.1:8080/eventsub/subscriptions for the Twitch CLI mock server.");
            _helixBaseUrl = cat.CreateEntry("HelixBaseUrl", "https://api.twitch.tv/helix",
                description: "Helix API base URL for reward management.");
            _suppressGamePubSub = cat.CreateEntry("SuppressGamePubSubConnect", true,
                description: "Prevent the game from retrying its dead PubSub endpoint (decommissioned April 2025).");
            _manageRewards = cat.CreateEntry("ManageRewards", true,
                description: "Auto-create channel point rewards for enabled features and mirror the game's Twitch settings toggles. Requires a one-time Twitch re-consent (the game will prompt).");
            _rewardCommands = cat.CreateEntry("RewardCommands",
                "srr:500:input,timewarp:200,speed:200,superspeed:300,color:100,rainbow:150,vanish:200,embiggen:150,minimize:150,warp:250,invaderz:300",
                description: "Rewards to manage, comma-separated. Format: command[:cost][:input]. 'input' marks rewards requiring viewer text (song requests).");
            _rewardDefinitions = cat.CreateEntry("RewardDefinitions",
                "srr | Song Request | Request any song! Powered by !srr ; " +
                "timewarp | Slow Motion | Slows the song (no score upload). !timewarp ; " +
                "speed | Speed Up | Speeds the song up. !speed ; " +
                "superspeed | Super Speed | Speeds the song up even more! !superspeed ; " +
                "color | Random Colors | Randomises the note colours. !color ; " +
                "rainbow | Rainbow Notes | Prismatic rainbow notes. !rainbow ; " +
                "vanish | Vanishing Notes | Notes vanish as they fly in. !vanish ; " +
                "embiggen | Big Notes | Bigger notes (no score upload). !embiggen ; " +
                "minimize | Tiny Notes | Smaller notes. !minimize ; " +
                "warp | Warp Mode | Trippy warp visuals. !warp ; " +
                "invaderz | Invaderz | Adds an invader to the song! !invaderz",
                description: "Optional custom reward names/descriptions. Format per reward: command | Title | Description | cost | input — rewards separated by ';'. Empty fields keep defaults. Example: srr | Song Request | Request any song! Powered by !srr | 500 | input ; timewarp | Slow Motion | Slows the song. !timewarp");
            _commandPrefix = cat.CreateEntry("CommandPrefix", "!",
                description: "Chat command prefix used in reward titles.");
            _refundFailedRequests = cat.CreateEntry("RefundFailedRequests", true,
                description: "Refund points automatically when a song-request redemption does not result in a queued song (only works for mod-created rewards).");
            _rewardsFollowCpm = cat.CreateEntry("RewardsFollowChannelPointMode", true,
                description: "true: rewards are hidden on Twitch while Channel Point Mode is off (chat-only vs rewards-only modes). false: rewards follow only their feature toggles, allowing chat and redemptions simultaneously (the game's vanilla semantics — channelpointmode only blocks chat).");
            _disableRewardsOnExit = cat.CreateEntry("DisableRewardsOnExit", true,
                description: "Disable all mod-managed rewards when the game closes, so viewers can't redeem while the game isn't running. They re-enable automatically on next launch.");
            _dedupeRedemptions = cat.CreateEntry("DeduplicateRedemptions", true,
                description: "Block the game from processing the same redemption twice (observed rarely when the game double-attaches its handler). Redemption ids are unique, so this can never block a legitimate redemption.");
            _autoCompleteRequests = cat.CreateEntry("AutoCompleteRequests", true,
                description: "Mark successful song-request redemptions FULFILLED, clearing them from the Twitch redemption queue (only works for mod-created rewards).");

            if (migrateLegacyConfig)
            {
                cat.SetFilePath(configPath, false, false); // keep values loaded from the legacy file
                cat.SaveToFile(false);
                LoggerInstance.Msg("Config now lives in UserData/SynthChannelPoints.cfg (existing settings migrated). The old [SynthChannelPoints] section in MelonPreferences.cfg is no longer used and can be deleted.");
            }

            _rewardSpecs = ParseRewardSpecs(_rewardCommands.Value, _commandPrefix.Value);
            ApplyRewardDefinitions(_rewardSpecs, _rewardDefinitions.Value, _commandPrefix.Value);
            EnsureCommandTokens();

            LoggerInstance.Msg("SynthChannelPoints v1.2.2 loaded — channel point redemptions via EventSub.");

            const string defaultEventSub = "wss://eventsub.wss.twitch.tv/ws";
            const string defaultHelix = "https://api.twitch.tv/helix/eventsub/subscriptions";
            const string defaultBase = "https://api.twitch.tv/helix";
            if (!string.Equals(_eventSubUrl.Value, defaultEventSub, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(_helixSubUrl.Value, defaultHelix, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(_helixBaseUrl.Value, defaultBase, StringComparison.OrdinalIgnoreCase))
            {
                LoggerInstance.Warning($"Non-default endpoints configured (EventSubUrl={_eventSubUrl.Value}, HelixSubscriptionsUrl={_helixSubUrl.Value}, HelixBaseUrl={_helixBaseUrl.Value}). If you previously tested with the Twitch CLI mock server, reset these in UserData/SynthChannelPoints.cfg for live use.");
            }
            WarnIfTlsToLocalhost("EventSubUrl", _eventSubUrl.Value);
            WarnIfTlsToLocalhost("HelixSubscriptionsUrl", _helixSubUrl.Value);
            WarnIfTlsToLocalhost("HelixBaseUrl", _helixBaseUrl.Value);
        }

        public override void OnUpdate()
        {
            if (!_enabled.Value) return;

            while (_injectQueue.TryDequeue(out var envelope))
            {
                InjectEnvelope(envelope);
            }

            _frameCounter++;
            if (_frameCounter < 180) return;
            _frameCounter = 0;

            try
            {
                if (!_gameReady) TryResolveGame();
                if (_gameReady)
                {
                    EnsureRequestedScopes();
                    RefreshCredSnapshot();
                    ReadToggleStates();
                    EnsureClientStarted();
                }
            }
            catch (Exception ex)
            {
                Debug($"Housekeeping error: {ex}");
            }
        }

        public override void OnApplicationQuit()
        {
            try { DisableManagedRewardsOnExit(); } catch { }
            try { _cts?.Cancel(); } catch { }
        }

        // Best-effort, time-boxed: without this, enabled rewards linger in the
        // channel's points menu after the game closes, and viewers redeem into
        // a void with nothing running to respond or refund.
        private void DisableManagedRewardsOnExit()
        {
            if (!_disableRewardsOnExit.Value || !_manageRewards.Value || !_canManage) return;
            var creds = _creds;
            if (creds == null || !creds.IsComplete) return;
            string clientId;
            lock (_stateLock) { clientId = _effectiveClientId ?? creds.ClientId; }

            List<string> ids;
            lock (_managedRewards) { ids = new List<string>(_managedRewards.Keys); }
            if (ids.Count == 0) return;

            try
            {
                using var http = new HttpClient();
                http.Timeout = TimeSpan.FromSeconds(2);
                var tasks = new List<Task>(ids.Count);
                foreach (var id in ids)
                {
                    tasks.Add(PatchRewardAsync(http, creds, clientId, id, false, null, null, CancellationToken.None));
                }
                Task.WaitAll(tasks.ToArray(), 4000);
                LoggerInstance.Msg($"Disabled {ids.Count} channel point reward(s) on exit — they re-enable on next launch.");
            }
            catch (Exception ex)
            {
                Debug($"Exit cleanup incomplete: {ex.Message}");
            }
        }

        // ==================================================================
        // Game resolution (main thread)
        // ==================================================================

        private void TryResolveGame()
        {
            _botType = _botType ?? ResolveTypeByCandidates(TwitchBotTypeCandidates);
            if (_botType == null) return;

            ApplySuppressionPatch();

            object bot = null;
            foreach (var name in InstanceMemberCandidates)
            {
                bot = GetMemberValue(_botType, null, name, out var found);
                if (found && bot != null) break;
                bot = null;
            }
            if (bot == null) return;

            var pubsub = GetMemberValue(_botType, bot, "pubsub", out _);
            if (pubsub == null) return;

            var tmp = pubsub.GetType().GetMethod("TestMessageParser",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (tmp == null)
            {
                LoggerInstance.Error("TestMessageParser not found on the game's TwitchPubSub — cannot inject. Check game version.");
                _enabled.Value = false;
                return;
            }

            _bot = bot;
            _pubsub = pubsub;
            _testMessageParser = tmp;
            _gameReady = true;
            Debug("Game references resolved (TwitchBot + pubsub + TestMessageParser).");

            ApplyQueueAddPatch();
            ApplyRedemptionDedupePatch();
        }

        private void ApplySuppressionPatch()
        {
            if (_suppressionApplied || !_suppressGamePubSub.Value) return;
            try
            {
                var pubsubType = ResolveTypeByCandidates(PubSubTypeCandidates);
                if (pubsubType == null) return;

                var connect = pubsubType.GetMethod("Connect",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (connect == null)
                {
                    Debug("TwitchPubSub.Connect not found; suppression skipped.");
                    _suppressionApplied = true;
                    return;
                }

                EnsureHarmony();
                _harmony.Patch(connect, new HarmonyLib.HarmonyMethod(
                    typeof(Core).GetMethod(nameof(SuppressConnectPrefix), BindingFlags.Static | BindingFlags.NonPublic)));
                _suppressionApplied = true;
                Debug("Suppressed game PubSub Connect (dead endpoint).");
            }
            catch (Exception ex)
            {
                _suppressionApplied = true; // do not retry-spam
                Debug($"Suppression patch failed (continuing without): {ex.Message}");
            }
        }

        private void ApplyQueueAddPatch()
        {
            if (_queueAddPatchAttempted || !_manageRewards.Value) return;
            _queueAddPatchAttempted = true;
            try
            {
                var queueAdd = _botType.GetMethod("QueueAdd",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
                if (queueAdd == null)
                {
                    Debug("QueueAdd not found — refund/auto-complete unavailable on this game version.");
                    return;
                }
                EnsureHarmony();
                _harmony.Patch(queueAdd, null, new HarmonyLib.HarmonyMethod(
                    typeof(Core).GetMethod(nameof(QueueAddPostfix), BindingFlags.Static | BindingFlags.NonPublic)));
                _queueAddPatched = true;
                Debug("QueueAdd success-signal patch applied.");
            }
            catch (Exception ex)
            {
                Debug($"QueueAdd patch failed — refund/auto-complete unavailable: {ex.Message}");
            }
        }

        private bool _dedupePatchAttempted;

        // Guard against a vanilla game bug (identified 2026-07-12): the game
        // attaches its redemption handler twice in menu context (Startup plus a
        // scene-bound attach, detached during gameplay), so menu-state
        // redemptions execute every command twice. Invisible for a year while
        // PubSub was dead; inherited when this mod resurrected the pipeline.
        // Redemption ids are unique per redemption, so skipping a repeated id
        // within a short window is a zero-false-positive fix.
        private void ApplyRedemptionDedupePatch()
        {
            if (_dedupePatchAttempted || !_dedupeRedemptions.Value) return;
            _dedupePatchAttempted = true;
            try
            {
                var handler = _botType.GetMethod("Pubsub_OnChannelPointsRewardRedeemed",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
                if (handler == null)
                {
                    Debug("Redemption handler not found — dedupe guard unavailable.");
                    return;
                }
                EnsureHarmony();
                DedupeEnabled = true;
                _harmony.Patch(handler, new HarmonyLib.HarmonyMethod(
                    typeof(Core).GetMethod(nameof(RedemptionDedupePrefix), BindingFlags.Static | BindingFlags.NonPublic)));
                Debug("Redemption dedupe guard applied.");
            }
            catch (Exception ex)
            {
                Debug($"Dedupe patch failed (continuing without): {ex.Message}");
            }
        }

        private static bool RedemptionDedupePrefix(object __0, object __1)
        {
            try
            {
                if (!DedupeEnabled || __1 == null) return true;
                var rr = GetMemberValue(__1.GetType(), __1, "RewardRedeemed", out _);
                var red = rr == null ? null : GetMemberValue(rr.GetType(), rr, "Redemption", out _);
                var id = red == null ? null : GetMemberValue(red.GetType(), red, "Id", out _) as string;
                if (string.IsNullOrEmpty(id)) return true;

                var now = DateTime.UtcNow;
                if (RecentRedemptionIds.TryGetValue(id, out var seen) && (now - seen).TotalSeconds < 10)
                {
                    _dedupeBlockCount++;
                    if (_dedupeBlockCount == 1)
                        SLog.Msg("Blocked a duplicate redemption execution — known game quirk: the game double-processes redemptions while in the menu. The guard makes this harmless; further blocks log at debug level.");
                    else if (DebugEntry != null && DebugEntry.Value)
                        SLog.Msg($"[Debug] Blocked duplicate redemption {id} (x{_dedupeBlockCount} this session).");
                    return false; // skip the game's handler for the duplicate
                }
                RecentRedemptionIds[id] = now;

                if (RecentRedemptionIds.Count > 256)
                {
                    foreach (var kv in RecentRedemptionIds)
                    {
                        if ((now - kv.Value).TotalSeconds > 60) RecentRedemptionIds.TryRemove(kv.Key, out _);
                    }
                }
                return true;
            }
            catch
            {
                return true; // never block on guard failure
            }
        }

        private void EnsureHarmony()
        {
            if (_harmony == null) _harmony = new HarmonyLib.Harmony("SynthChannelPoints");
        }

        private static bool SuppressConnectPrefix() => false;

        private static void QueueAddPostfix(object __0, string __1, bool __2)
        {
            try
            {
                if (!string.IsNullOrEmpty(__1)) QueueAddSignals.Enqueue(__1);
            }
            catch { }
        }

        // ==================================================================
        // Scope upgrade (main thread)
        // ==================================================================

        private void EnsureRequestedScopes()
        {
            if (_scopeMutationDone || !_manageRewards.Value) return;

            var scopesObj = GetMemberValue(_botType, _bot, "requestedScopes", out var found);
            if (!found || scopesObj == null) return;

            var current = ReadStringArray(scopesObj);
            foreach (var c in current)
            {
                if (string.Equals(c, ManageScope, StringComparison.OrdinalIgnoreCase))
                {
                    _scopeMutationDone = true;
                    return;
                }
            }

            var merged = new List<string>(current) { ManageScope };
            var newArr = BuildIl2CppStringArray(scopesObj, merged.ToArray());
            if (newArr == null)
            {
                LoggerInstance.Warning("Could not construct the upgraded scope array — reward management unavailable this session.");
                _scopeMutationDone = true;
                return;
            }

            if (SetMemberValue(_botType, _bot, "requestedScopes", newArr, out var err))
            {
                _scopeMutationDone = true;
                LoggerInstance.Msg("Requested upgraded Twitch permission (manage channel point rewards). Approve the one-time browser prompt, or re-link Twitch in the game's settings.");
            }
            else
            {
                _scopeMutationDone = true;
                Debug($"Scope assignment failed: {err}");
            }
        }

        // ==================================================================
        // Game settings toggle mirror (main thread)
        // ==================================================================

        private void ReadToggleStates()
        {
            if (!_manageRewards.Value) return;
            if (!_settingsSearchDone) FindSettingsHolder();
            if (_settingsReader == null) return;

            try
            {
                var settings = _settingsReader();
                if (settings == null) return;
                var st = settings.GetType();

                var cpm = ReadBool(st, settings, "ChannelPointMode") ?? true;
                var gate = !_rewardsFollowCpm.Value || cpm;
                var map = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                foreach (var spec in _rewardSpecs)
                {
                    var featureOn = true;
                    if (CommandToggleMap.TryGetValue(spec.Command, out var propNames))
                    {
                        bool? any = null;
                        foreach (var pn in propNames)
                        {
                            var v = ReadBool(st, settings, pn);
                            if (v.HasValue) any = (any ?? false) || v.Value;
                        }
                        featureOn = any ?? true;
                    }
                    map[spec.Title] = gate && featureOn;
                }

                var previous = _desiredEnabled;
                var changed = previous == null || previous.Count != map.Count;
                if (!changed)
                {
                    foreach (var kv in map)
                    {
                        if (!previous.TryGetValue(kv.Key, out var old) || old != kv.Value)
                        {
                            changed = true;
                            break;
                        }
                    }
                }
                _desiredEnabled = map;
                if (changed) _syncNow = true;
            }
            catch (Exception ex)
            {
                Debug($"Toggle read failed: {ex.Message}");
            }
        }

        private void FindSettingsHolder()
        {
            _settingsSearchDone = true;
            try
            {
                var settingsType = ResolveTypeByCandidates(SettingsTypeCandidates);
                if (settingsType == null) return;

                const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic |
                                           BindingFlags.Instance | BindingFlags.Static |
                                           BindingFlags.DeclaredOnly;

                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    string asmName;
                    try { asmName = asm.GetName().Name ?? ""; }
                    catch { continue; }
                    if (ShouldSkipAssembly(asmName)) continue;

                    Type[] types;
                    try { types = asm.GetTypes(); }
                    catch (ReflectionTypeLoadException ex) { types = ex.Types ?? Array.Empty<Type>(); }
                    catch { continue; }

                    foreach (var t in types)
                    {
                        if (t == null || t == settingsType) continue;

                        PropertyInfo[] props;
                        FieldInfo[] fields;
                        try { props = t.GetProperties(flags); } catch { props = Array.Empty<PropertyInfo>(); }
                        try { fields = t.GetFields(flags); } catch { fields = Array.Empty<FieldInfo>(); }

                        foreach (var p in props)
                        {
                            Type pt = null;
                            try { pt = p.PropertyType; } catch { }
                            if (pt != settingsType) continue;
                            var reader = MakeSettingsReader(t, p, null);
                            if (reader != null && reader() != null)
                            {
                                _settingsReader = reader;
                                Debug($"TwitchSettings holder: {t.FullName}.{p.Name}");
                                return;
                            }
                        }
                        foreach (var f in fields)
                        {
                            Type ft = null;
                            try { ft = f.FieldType; } catch { }
                            if (ft != settingsType) continue;
                            var reader = MakeSettingsReader(t, null, f);
                            if (reader != null && reader() != null)
                            {
                                _settingsReader = reader;
                                Debug($"TwitchSettings holder: {t.FullName}.{f.Name}");
                                return;
                            }
                        }
                    }
                }
                Debug("No readable TwitchSettings holder found — rewards will be created enabled and not toggle-synced.");
            }
            catch (Exception ex)
            {
                Debug($"Settings holder search failed: {ex.Message}");
            }
        }

        private Func<object> MakeSettingsReader(Type holderType, PropertyInfo p, FieldInfo f)
        {
            var isStatic = false;
            try
            {
                isStatic = p != null
                    ? (p.GetGetMethod(true) != null && p.GetGetMethod(true).IsStatic)
                    : f.IsStatic;
            }
            catch { }

            if (isStatic)
            {
                return () =>
                {
                    try { return p != null ? p.GetValue(null) : f.GetValue(null); }
                    catch { return null; }
                };
            }

            return () =>
            {
                try
                {
                    object holder = null;
                    foreach (var name in SingletonMemberCandidates)
                    {
                        holder = GetMemberValue(holderType, null, name, out var found);
                        if (found && holder != null) break;
                        holder = null;
                    }
                    if (holder == null) return null;
                    return p != null ? p.GetValue(holder) : f.GetValue(holder);
                }
                catch { return null; }
            };
        }

        private static bool? ReadBool(Type type, object instance, string name)
        {
            var v = GetMemberValue(type, instance, name, out var found);
            if (found && v is bool b) return b;
            return null;
        }

        // ==================================================================
        // Credentials (main thread)
        // ==================================================================

        private void RefreshCredSnapshot()
        {
            try
            {
                var currentUser = GetMemberValue(_botType, _bot, "currentUser", out _);
                if (currentUser == null) return;
                var cuType = currentUser.GetType();

                var snapshot = new Creds
                {
                    AccessToken = SanitizeToken(GetMemberValue(cuType, currentUser, "AccessToken", out _) as string),
                    UserId = GetMemberValue(cuType, currentUser, "UserId", out _) as string,
                    Username = GetMemberValue(cuType, currentUser, "Username", out _) as string,
                    ClientId = GetMemberValue(_botType, _bot, "clientId", out _) as string
                };

                if (snapshot.IsComplete) _creds = snapshot;
            }
            catch (Exception ex)
            {
                Debug($"Credential snapshot failed: {ex.Message}");
            }
        }

        private void EnsureClientStarted()
        {
            var creds = _creds;
            if (creds == null || !creds.IsComplete) return;

            if (_cts == null) _cts = new CancellationTokenSource();

            if (_clientTask == null || _clientTask.IsCompleted)
            {
                var token = _cts.Token;
                _clientTask = Task.Run(() => RunClientAsync(token), token);
                Debug("EventSub client task started.");
            }

            if (_manageRewards.Value && (_manageTask == null || _manageTask.IsCompleted))
            {
                var token = _cts.Token;
                _manageTask = Task.Run(() => RunManageAsync(token), token);
                Debug("Reward manage task started.");
            }
        }

        // ==================================================================
        // Injection (main thread)
        // ==================================================================

        private void InjectEnvelope(string envelope)
        {
            try
            {
                if (!_gameReady) return;
                _testMessageParser.Invoke(_pubsub, new object[] { envelope });
                Debug("Injected redemption envelope into game parser.");
            }
            catch (Exception ex)
            {
                LoggerInstance.Warning($"Injection failed, re-resolving game refs: {(ex.InnerException ?? ex).Message}");
                _gameReady = false;
            }
        }

        // ==================================================================
        // EventSub client (background — never touches IL2CPP objects)
        // ==================================================================

        private async Task RunClientAsync(CancellationToken ct)
        {
            var reconnectUrl = (string)null;
            var backoffSeconds = 5;
            var dedupe = new HashSet<string>();
            var dedupeOrder = new Queue<string>();

            using var http = new HttpClient();
            http.Timeout = TimeSpan.FromSeconds(15);

            while (!ct.IsCancellationRequested)
            {
                var isReconnect = reconnectUrl != null;
                var url = reconnectUrl ?? _eventSubUrl.Value;
                reconnectUrl = null;

                try
                {
                    using var ws = new ClientWebSocket();
                    await ws.ConnectAsync(new Uri(url), ct).ConfigureAwait(false);
                    Debug($"WebSocket connected: {url}");

                    string sessionId = null;
                    var keepaliveSeconds = 10;
                    var lastMessage = DateTime.UtcNow;
                    var subscribed = isReconnect;

                    while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
                    {
                        var msg = await ReceiveTextAsync(ws, TimeSpan.FromSeconds(keepaliveSeconds + 15), ct).ConfigureAwait(false);
                        if (msg == null)
                        {
                            if ((DateTime.UtcNow - lastMessage).TotalSeconds > keepaliveSeconds + 15)
                            {
                                Debug("Keepalive timeout — reconnecting.");
                                break;
                            }
                            continue;
                        }
                        lastMessage = DateTime.UtcNow;

                        using var doc = JsonDocument.Parse(msg);
                        var root = doc.RootElement;
                        if (!root.TryGetProperty("metadata", out var meta)) continue;
                        var messageType = meta.GetProperty("message_type").GetString();

                        if (meta.TryGetProperty("message_id", out var midEl))
                        {
                            var mid = midEl.GetString();
                            if (!string.IsNullOrEmpty(mid))
                            {
                                if (dedupe.Contains(mid)) continue;
                                dedupe.Add(mid);
                                dedupeOrder.Enqueue(mid);
                                while (dedupeOrder.Count > 512) dedupe.Remove(dedupeOrder.Dequeue());
                            }
                        }

                        switch (messageType)
                        {
                            case "session_welcome":
                            {
                                var session = root.GetProperty("payload").GetProperty("session");
                                sessionId = session.GetProperty("id").GetString();
                                if (session.TryGetProperty("keepalive_timeout_seconds", out var ka) &&
                                    ka.ValueKind == JsonValueKind.Number)
                                {
                                    keepaliveSeconds = ka.GetInt32();
                                }
                                Debug($"Session welcome: {sessionId} (keepalive {keepaliveSeconds}s)");

                                if (!subscribed)
                                {
                                    var ok = await SubscribeAsync(http, sessionId, ct).ConfigureAwait(false);
                                    if (!ok)
                                    {
                                        try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "subscribe failed", ct).ConfigureAwait(false); } catch { }
                                        break;
                                    }
                                    subscribed = true;
                                    backoffSeconds = 5;
                                    if (!_announcedActive)
                                    {
                                        _announcedActive = true;
                                        LoggerInstance.Msg($"Channel point redemptions restored via EventSub for {_creds?.Username ?? "streamer"}.");
                                    }
                                }
                                break;
                            }
                            case "session_keepalive":
                                break;
                            case "session_reconnect":
                            {
                                var session = root.GetProperty("payload").GetProperty("session");
                                reconnectUrl = session.GetProperty("reconnect_url").GetString();
                                Debug("Session reconnect requested.");
                                try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "reconnect", ct).ConfigureAwait(false); } catch { }
                                break;
                            }
                            case "notification":
                            {
                                var payload = root.GetProperty("payload");
                                var subType = payload.GetProperty("subscription").GetProperty("type").GetString();
                                if (subType == "channel.channel_points_custom_reward_redemption.add")
                                {
                                    var ev = payload.GetProperty("event");
                                    if (TryHandleEmptyInput(ev)) break;
                                    if (TryHandleDisabledReward(ev)) break;
                                    var envelope = BuildEnvelopeFromEventSub(ev);
                                    if (envelope != null)
                                    {
                                        _injectQueue.Enqueue(envelope);
                                        Debug("Redemption notification queued for injection.");
                                        TrackPendingIfManaged(ev);
                                    }
                                }
                                break;
                            }
                            case "revocation":
                                LoggerInstance.Warning("EventSub subscription revoked (token invalid or scope missing). Will retry with fresh credentials.");
                                subscribed = false;
                                try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "revoked", ct).ConfigureAwait(false); } catch { }
                                break;
                        }

                        if (reconnectUrl != null) break;
                    }
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    Debug($"EventSub client error: {ex.Message}");
                }

                if (ct.IsCancellationRequested) return;
                if (reconnectUrl != null) continue;

                await Task.Delay(TimeSpan.FromSeconds(backoffSeconds), ct).ConfigureAwait(false);
                backoffSeconds = Math.Min(backoffSeconds * 2, 60);
            }
        }

        private async Task<bool> SubscribeAsync(HttpClient http, string sessionId, CancellationToken ct)
        {
            var creds = _creds;
            if (creds == null || !creds.IsComplete)
            {
                Debug("No credentials available for subscription.");
                return false;
            }

            var effectiveClientId = creds.ClientId;
            var usingRealHelix = _helixSubUrl.Value.StartsWith("https://api.twitch.tv", StringComparison.OrdinalIgnoreCase);
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, "https://id.twitch.tv/oauth2/validate");
                req.Headers.TryAddWithoutValidation("Authorization", "OAuth " + creds.AccessToken);
                using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
                var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode)
                {
                    try
                    {
                        using var vdoc = JsonDocument.Parse(body);
                        var vroot = vdoc.RootElement;
                        if (vroot.TryGetProperty("client_id", out var cidEl) && cidEl.ValueKind == JsonValueKind.String)
                        {
                            var issuedTo = cidEl.GetString();
                            if (!string.IsNullOrEmpty(issuedTo))
                            {
                                if (!string.Equals(issuedTo, creds.ClientId, StringComparison.Ordinal))
                                    Debug("Token was issued to a different client id than TwitchBot.clientId — using the token's own client id for Helix.");
                                effectiveClientId = issuedTo;
                            }
                        }

                        var hasManage = body.IndexOf(ManageScope, StringComparison.OrdinalIgnoreCase) >= 0;
                        _canManage = hasManage;
                        lock (_stateLock) { _effectiveClientId = effectiveClientId; }
                        if (_manageRewards.Value && !hasManage && !_announcedDormant)
                        {
                            _announcedDormant = true;
                            LoggerInstance.Msg("Reward management is dormant — re-link Twitch in the game's settings to grant the new permission. Redemption forwarding works regardless.");
                        }

                        if (body.IndexOf("channel:read:redemptions", StringComparison.OrdinalIgnoreCase) < 0)
                            RateWarn("scope", "Game token lacks channel:read:redemptions scope — subscription will likely be rejected. Re-link Twitch in the game's settings.");
                        else
                            Debug("Token validated; redemptions scope present.");
                    }
                    catch (Exception ex)
                    {
                        Debug($"Validate response parse failed: {ex.Message}");
                    }
                }
                else if ((int)resp.StatusCode == 401)
                {
                    if (usingRealHelix)
                    {
                        RateWarn("token-invalid", "The game's Twitch token is invalid or expired (validate returned 401). Waiting for the game to refresh it — if this persists, re-link Twitch in the game's settings.");
                        return false;
                    }
                    Debug("Validate returned 401 but a non-default Helix URL is configured (mock testing) — continuing.");
                }
                else
                {
                    Debug($"Token validate returned {(int)resp.StatusCode} — continuing.");
                }
            }
            catch (Exception ex)
            {
                Debug($"Token validation skipped: {ex.Message}");
            }

            var json =
                "{\"type\":\"channel.channel_points_custom_reward_redemption.add\"," +
                "\"version\":\"1\"," +
                "\"condition\":{\"broadcaster_user_id\":\"" + J(creds.UserId) + "\"}," +
                "\"transport\":{\"method\":\"websocket\",\"session_id\":\"" + J(sessionId) + "\"}}";

            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, _helixSubUrl.Value);
                req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + creds.AccessToken);
                req.Headers.TryAddWithoutValidation("Client-Id", effectiveClientId);
                req.Content = new StringContent(json, Encoding.UTF8, "application/json");
                using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
                var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

                if (resp.IsSuccessStatusCode)
                {
                    Debug("EventSub subscription created.");
                    _lastWarnKey = null;
                    return true;
                }

                var code = (int)resp.StatusCode;
                if (code == 401)
                    RateWarn("helix-401", "Helix rejected the game's token (401) — it may be mid-refresh. Retrying with backoff.");
                else if (code == 403)
                    RateWarn("helix-403", "Helix rejected the subscription (403 — missing scope or channel points unavailable on this account). Retrying periodically.");
                else if (code == 409)
                {
                    Debug("Subscription already exists (409) — treating as success.");
                    return true;
                }
                else
                    RateWarn("helix-" + code, $"Subscription failed ({code}): {Truncate(body, 200)}");
                return false;
            }
            catch (Exception ex)
            {
                Debug($"Subscription request failed: {ex.Message}");
                return false;
            }
        }

        private static async Task<string> ReceiveTextAsync(ClientWebSocket ws, TimeSpan timeout, CancellationToken ct)
        {
            var buffer = new ArraySegment<byte>(new byte[16 * 1024]);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout);

            var sb = new StringBuilder();
            try
            {
                while (true)
                {
                    var result = await ws.ReceiveAsync(buffer, timeoutCts.Token).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                        return null;
                    sb.Append(Encoding.UTF8.GetString(buffer.Array, 0, result.Count));
                    if (result.EndOfMessage) return sb.ToString();
                }
            }
            catch (OperationCanceledException)
            {
                if (ct.IsCancellationRequested) throw;
                return null;
            }
        }

        // ==================================================================
        // Reward management loop (background — Helix only)
        // ==================================================================

        private async Task RunManageAsync(CancellationToken ct)
        {
            using var http = new HttpClient();
            http.Timeout = TimeSpan.FromSeconds(15);

            var tick = 0;
            var consecutiveForbidden = 0;
            var remoteEnabled = new Dictionary<string, bool>(); // reward id -> is_enabled

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
                    tick++;

                    if (!_canManage || !_manageRewards.Value) continue;
                    var creds = _creds;
                    if (creds == null || !creds.IsComplete) continue;
                    string clientId;
                    lock (_stateLock) { clientId = _effectiveClientId ?? creds.ClientId; }

                    // Fast path every 2s: resolve pending redemptions.
                    await ProcessPendingAsync(http, creds, clientId, ct).ConfigureAwait(false);

                    // Slow path every ~16s: ensure rewards exist and mirror toggles.
                    // A settings change triggers an immediate pass (~2s) so
                    // toggling Channel Point Mode off removes rewards promptly.
                    // After repeated 403s (no affiliate — won't change
                    // mid-session), back off to roughly every 5 minutes.
                    var interval = consecutiveForbidden >= 3 ? 160 : 8;
                    var syncRequested = _syncNow && consecutiveForbidden < 3;
                    if (tick % interval != 0 && !syncRequested) continue;
                    _syncNow = false;

                    var rewards = await GetManageableRewardsAsync(http, creds, clientId, ct).ConfigureAwait(false);
                    if (rewards == null)
                    {
                        if (_lastRewardListForbidden) consecutiveForbidden++;
                        continue;
                    }
                    consecutiveForbidden = 0;

                    lock (_managedRewards)
                    {
                        _managedRewards.Clear();
                        foreach (var kv in rewards)
                        {
                            foreach (var spec in _rewardSpecs)
                            {
                                if (string.Equals(kv.Value.Title, spec.Title, StringComparison.OrdinalIgnoreCase))
                                {
                                    _managedRewards[kv.Key] = spec;
                                    break;
                                }
                            }
                        }
                    }

                    var desired = _desiredEnabled;

                    // Rewards we created under a previous config (e.g. before a
                    // rename) match no current spec: disable them rather than
                    // leave redeemable rewards the game no longer maps to.
                    foreach (var kv in rewards)
                    {
                        var matchesSpec = false;
                        foreach (var s in _rewardSpecs)
                        {
                            if (string.Equals(kv.Value.Title, s.Title, StringComparison.OrdinalIgnoreCase)) { matchesSpec = true; break; }
                        }
                        if (matchesSpec || !kv.Value.Enabled) continue;
                        if (await PatchRewardAsync(http, creds, clientId, kv.Key, false, null, null, ct).ConfigureAwait(false))
                        {
                            RateWarn("orphan-" + kv.Key, $"Reward '{kv.Value.Title}' no longer matches any configured command (renamed in config?) — disabled it. Delete it on your Twitch dashboard if unwanted.");
                        }
                    }

                    foreach (var spec in _rewardSpecs)
                    {
                        string existingId = null;
                        var existingEnabled = false;
                        long existingCost = 0;
                        string existingPrompt = "";
                        foreach (var kv in rewards)
                        {
                            if (string.Equals(kv.Value.Title, spec.Title, StringComparison.OrdinalIgnoreCase))
                            {
                                existingId = kv.Key;
                                existingEnabled = kv.Value.Enabled;
                                existingCost = kv.Value.Cost;
                                existingPrompt = kv.Value.Prompt ?? "";
                                break;
                            }
                        }

                        var wantEnabled = desired == null || !desired.TryGetValue(spec.Title, out var d) || d;

                        if (existingId == null)
                        {
                            // Only create a reward once its feature is enabled in the
                            // game's Twitch settings — the in-game panel is the control
                            // surface. Existing rewards are mirrored, never deleted.
                            if (!wantEnabled) continue;
                            var newId = await CreateRewardAsync(http, creds, clientId, spec, true, ct).ConfigureAwait(false);
                            if (newId != null)
                            {
                                lock (_managedRewards) { _managedRewards[newId] = spec; }
                                remoteEnabled[newId] = wantEnabled;
                                LoggerInstance.Msg($"Created channel point reward '{spec.Title}' ({spec.Cost} points).");
                            }
                            continue;
                        }

                        remoteEnabled[existingId] = existingEnabled;
                        var enabledDrift = desired != null && existingEnabled != wantEnabled;
                        var costDrift = existingCost > 0 && existingCost != spec.Cost;
                        var desiredPrompt = (spec.Prompt ?? "").Trim();
                        var promptDrift = !string.Equals(existingPrompt.Trim(), desiredPrompt, StringComparison.Ordinal);
                        if (enabledDrift || costDrift || promptDrift)
                        {
                            if (await PatchRewardAsync(http, creds, clientId, existingId,
                                    enabledDrift ? (bool?)wantEnabled : null,
                                    costDrift ? (long?)spec.Cost : null,
                                    promptDrift ? desiredPrompt : null, ct).ConfigureAwait(false))
                            {
                                if (enabledDrift)
                                {
                                    remoteEnabled[existingId] = wantEnabled;
                                    LoggerInstance.Msg($"Reward '{spec.Title}' {(wantEnabled ? "enabled" : "disabled")} (mirroring game settings).");
                                }
                                if (costDrift)
                                    LoggerInstance.Msg($"Reward '{spec.Title}' cost updated to {spec.Cost} points (from config).");
                                if (promptDrift)
                                    LoggerInstance.Msg($"Reward '{spec.Title}' description updated (from config).");
                            }
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    Debug($"Manage loop error: {ex.Message}");
                }
            }
        }

        // Empirically (probe, 2026-07-11): the game's handler silently consumes
        // an input-requiring redemption with empty text — no command dispatch, no
        // song, but the viewer's points and request slot are spent. Live
        // input-required rewards can't produce empty text, but manually created
        // rewards without required text can. Drop the injection and refund
        // instead — strictly better for the viewer than the game's behavior.
        private bool TryHandleEmptyInput(JsonElement ev)
        {
            try
            {
                var userInput = ev.TryGetProperty("user_input", out var ui) && ui.ValueKind == JsonValueKind.String
                    ? ui.GetString() : "";
                if (!string.IsNullOrWhiteSpace(userInput)) return false;

                string title = null, rewardId = null;
                if (ev.TryGetProperty("reward", out var reward) && reward.ValueKind == JsonValueKind.Object)
                {
                    title = reward.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
                    rewardId = reward.TryGetProperty("id", out var ri) && ri.ValueKind == JsonValueKind.String ? ri.GetString() : null;
                }
                if (string.IsNullOrEmpty(title)) return false;

                RewardSpec matched = null;
                foreach (var spec in _rewardSpecs)
                {
                    if (spec.RequiresInput && string.Equals(spec.Title, title, StringComparison.OrdinalIgnoreCase))
                    {
                        matched = spec;
                        break;
                    }
                }
                if (matched == null) return false; // not an input reward -> inject normally

                LoggerInstance.Warning($"Dropped redemption of '{title}' with empty input — the game would silently consume it without queuing anything. If this reward was created manually, enable 'Require Viewer to Enter Text' on your Twitch dashboard.");

                QueueImmediateRefundIfManaged(ev, rewardId);
                return true;
            }
            catch
            {
                return false;
            }
        }

        // Queue an instant CANCELED for a redemption we are dropping, when the
        // reward is mod-managed and refunds are enabled. Returns true if queued.
        private bool QueueImmediateRefundIfManaged(JsonElement ev, string rewardId)
        {
            if (string.IsNullOrEmpty(rewardId) || !_refundFailedRequests.Value) return false;
            bool managed;
            lock (_managedRewards) { managed = _managedRewards.ContainsKey(rewardId); }
            if (!managed) return false;
            var p = new Pending
            {
                RedemptionId = ev.TryGetProperty("id", out var idEl) ? idEl.GetString() : null,
                RewardId = rewardId,
                UserLogin = ev.TryGetProperty("user_login", out var ul) ? ul.GetString() : null,
                UserName = ev.TryGetProperty("user_name", out var un) ? un.GetString() : null,
                DeadlineUtc = DateTime.MinValue // refund on the next manage tick
            };
            if (string.IsNullOrEmpty(p.RedemptionId)) return false;
            lock (_pending) { _pending.Add(p); }
            return true;
        }

        // The game never gated redemption handling on channelpointmode — per its
        // own docs (and mock-verified), that flag only blocks chat commands. When
        // the mirror says a reward should be disabled, redemptions that slip
        // through anyway (mirror lag, leftovers after a crash-exit, mock tests)
        // are dropped here and refunded where possible.
        private bool TryHandleDisabledReward(JsonElement ev)
        {
            try
            {
                var desired = _desiredEnabled;
                if (desired == null) return false;

                string title = null, rewardId = null;
                if (ev.TryGetProperty("reward", out var reward) && reward.ValueKind == JsonValueKind.Object)
                {
                    title = reward.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
                    rewardId = reward.TryGetProperty("id", out var ri) && ri.ValueKind == JsonValueKind.String ? ri.GetString() : null;
                }
                if (string.IsNullOrEmpty(title)) return false;
                if (!desired.TryGetValue(title, out var enabled) || enabled) return false;

                var refundQueued = QueueImmediateRefundIfManaged(ev, rewardId);
                LoggerInstance.Msg($"Dropped redemption of '{title}' — this reward is disabled per the game's Twitch settings.{(refundQueued ? " Points will be refunded." : "")}");
                return true;
            }
            catch
            {
                return false;
            }
        }

        private void TrackPendingIfManaged(JsonElement ev)
        {
            try
            {
                if (!_canManage || !_manageRewards.Value) return;
                if (!_refundFailedRequests.Value && !_autoCompleteRequests.Value) return;

                if (!ev.TryGetProperty("reward", out var reward) || reward.ValueKind != JsonValueKind.Object) return;
                var rewardId = reward.TryGetProperty("id", out var ri) ? ri.GetString() : null;
                if (string.IsNullOrEmpty(rewardId)) return;

                RewardSpec spec = null;
                lock (_managedRewards) { _managedRewards.TryGetValue(rewardId, out spec); }
                if (spec == null || !spec.RequiresInput) return; // outcome tracking only for song requests

                var p = new Pending
                {
                    RedemptionId = ev.TryGetProperty("id", out var idEl) ? idEl.GetString() : null,
                    RewardId = rewardId,
                    UserLogin = ev.TryGetProperty("user_login", out var ul) ? ul.GetString() : null,
                    UserName = ev.TryGetProperty("user_name", out var un) ? un.GetString() : null,
                    DeadlineUtc = DateTime.UtcNow.AddSeconds(RefundDeadlineSeconds)
                };
                if (string.IsNullOrEmpty(p.RedemptionId)) return;

                lock (_pending) { _pending.Add(p); }
                Debug($"Tracking redemption {p.RedemptionId} for outcome (user {p.UserLogin}).");
            }
            catch (Exception ex)
            {
                Debug($"Pending tracking failed: {ex.Message}");
            }
        }

        private async Task ProcessPendingAsync(HttpClient http, Creds creds, string clientId, CancellationToken ct)
        {
            // Successful queue adds: fulfill (or just stop tracking).
            while (QueueAddSignals.TryDequeue(out var twitchName))
            {
                Pending match = null;
                lock (_pending)
                {
                    foreach (var p in _pending)
                    {
                        if (string.Equals(p.UserLogin, twitchName, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(p.UserName, twitchName, StringComparison.OrdinalIgnoreCase))
                        {
                            match = p;
                            break;
                        }
                    }
                    if (match != null) _pending.Remove(match);
                }
                if (match == null) continue;

                if (_autoCompleteRequests.Value)
                {
                    await UpdateRedemptionStatusAsync(http, creds, clientId, match, "FULFILLED", ct).ConfigureAwait(false);
                }
            }

            // Expired: no queue add happened -> refund.
            List<Pending> expired = null;
            lock (_pending)
            {
                for (int i = _pending.Count - 1; i >= 0; i--)
                {
                    if (_pending[i].DeadlineUtc <= DateTime.UtcNow)
                    {
                        (expired = expired ?? new List<Pending>()).Add(_pending[i]);
                        _pending.RemoveAt(i);
                    }
                }
            }
            if (expired == null) return;

            foreach (var p in expired)
            {
                if (_refundFailedRequests.Value)
                {
                    if (await UpdateRedemptionStatusAsync(http, creds, clientId, p, "CANCELED", ct).ConfigureAwait(false))
                        LoggerInstance.Msg($"Refunded failed song request from {p.UserName ?? p.UserLogin}.");
                }
            }
        }

        // ------------------------------------------------------------------
        // Helix reward endpoints
        // ------------------------------------------------------------------

        private volatile bool _lastRewardListForbidden;

        private sealed class RemoteReward
        {
            public string Title;
            public bool Enabled;
            public long Cost;
            public string Prompt;
        }

        private async Task<Dictionary<string, RemoteReward>> GetManageableRewardsAsync(HttpClient http, Creds creds, string clientId, CancellationToken ct)
        {
            try
            {
                var url = $"{_helixBaseUrl.Value}/channel_points/custom_rewards?broadcaster_id={Uri.EscapeDataString(creds.UserId)}&only_manageable_rewards=true";
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + creds.AccessToken);
                req.Headers.TryAddWithoutValidation("Client-Id", clientId);
                using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
                var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    _lastRewardListForbidden = (int)resp.StatusCode == 403;
                    if (_lastRewardListForbidden)
                        RateWarn("rewards-403", "Twitch rejected reward access (403) — channel points require Affiliate/Partner. Reward management disabled until then.");
                    else
                        Debug($"Reward list failed ({(int)resp.StatusCode}): {Truncate(body, 150)}");
                    return null;
                }
                _lastRewardListForbidden = false;

                var result = new Dictionary<string, RemoteReward>();
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in data.EnumerateArray())
                    {
                        var id = item.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                        if (string.IsNullOrEmpty(id)) continue;
                        result[id] = new RemoteReward
                        {
                            Title = item.TryGetProperty("title", out var t) ? t.GetString() : "",
                            Enabled = item.TryGetProperty("is_enabled", out var e) && e.ValueKind == JsonValueKind.True,
                            Cost = item.TryGetProperty("cost", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt64() : 0,
                            Prompt = item.TryGetProperty("prompt", out var pr) && pr.ValueKind == JsonValueKind.String ? pr.GetString() : ""
                        };
                    }
                }
                return result;
            }
            catch (Exception ex)
            {
                Debug($"Reward list request failed: {ex.Message}");
                return null;
            }
        }

        private async Task<string> CreateRewardAsync(HttpClient http, Creds creds, string clientId, RewardSpec spec, bool enabled, CancellationToken ct)
        {
            try
            {
                var url = $"{_helixBaseUrl.Value}/channel_points/custom_rewards?broadcaster_id={Uri.EscapeDataString(creds.UserId)}";
                var json =
                    "{\"title\":\"" + J(spec.Title) + "\"," +
                    "\"cost\":" + spec.Cost + "," +
                    "\"is_user_input_required\":" + (spec.RequiresInput ? "true" : "false") + "," +
                    (string.IsNullOrEmpty(spec.Prompt) ? "" : "\"prompt\":\"" + J(spec.Prompt) + "\",") +
                    "\"is_enabled\":" + (enabled ? "true" : "false") + "}";

                using var req = new HttpRequestMessage(HttpMethod.Post, url);
                req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + creds.AccessToken);
                req.Headers.TryAddWithoutValidation("Client-Id", clientId);
                req.Content = new StringContent(json, Encoding.UTF8, "application/json");
                using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
                var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

                if (resp.IsSuccessStatusCode)
                {
                    using var doc = JsonDocument.Parse(body);
                    if (doc.RootElement.TryGetProperty("data", out var data) &&
                        data.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in data.EnumerateArray())
                        {
                            if (item.TryGetProperty("id", out var idEl)) return idEl.GetString();
                        }
                    }
                    return null;
                }

                if ((int)resp.StatusCode == 400 && body.IndexOf("DUPLICATE_REWARD", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    RateWarn("dup-" + spec.Title, $"A reward titled '{spec.Title}' already exists but was created manually — the mod cannot manage or refund it. Delete it on your Twitch dashboard to let the mod take over.");
                }
                else
                {
                    Debug($"Create reward '{spec.Title}' failed ({(int)resp.StatusCode}): {Truncate(body, 150)}");
                }
                return null;
            }
            catch (Exception ex)
            {
                Debug($"Create reward request failed: {ex.Message}");
                return null;
            }
        }

        private async Task<bool> PatchRewardAsync(HttpClient http, Creds creds, string clientId, string rewardId, bool? enabled, long? cost, string prompt, CancellationToken ct)
        {
            try
            {
                var url = $"{_helixBaseUrl.Value}/channel_points/custom_rewards?broadcaster_id={Uri.EscapeDataString(creds.UserId)}&id={Uri.EscapeDataString(rewardId)}";
                var parts = new List<string>();
                if (enabled.HasValue) parts.Add("\"is_enabled\":" + (enabled.Value ? "true" : "false"));
                if (cost.HasValue) parts.Add("\"cost\":" + cost.Value);
                if (prompt != null) parts.Add("\"prompt\":\"" + J(prompt) + "\"");
                if (parts.Count == 0) return true;
                using var req = new HttpRequestMessage(HttpMethod.Patch, url);
                req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + creds.AccessToken);
                req.Headers.TryAddWithoutValidation("Client-Id", clientId);
                req.Content = new StringContent("{" + string.Join(",", parts) + "}", Encoding.UTF8, "application/json");
                using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    Debug($"Patch reward failed ({(int)resp.StatusCode}): {Truncate(body, 150)}");
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                Debug($"Patch reward request failed: {ex.Message}");
                return false;
            }
        }

        private async Task<bool> UpdateRedemptionStatusAsync(HttpClient http, Creds creds, string clientId, Pending p, string status, CancellationToken ct)
        {
            try
            {
                var url = $"{_helixBaseUrl.Value}/channel_points/custom_rewards/redemptions" +
                          $"?broadcaster_id={Uri.EscapeDataString(creds.UserId)}" +
                          $"&reward_id={Uri.EscapeDataString(p.RewardId)}" +
                          $"&id={Uri.EscapeDataString(p.RedemptionId)}";
                using var req = new HttpRequestMessage(HttpMethod.Patch, url);
                req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + creds.AccessToken);
                req.Headers.TryAddWithoutValidation("Client-Id", clientId);
                req.Content = new StringContent("{\"status\":\"" + status + "\"}", Encoding.UTF8, "application/json");
                using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    Debug($"Redemption {status} failed ({(int)resp.StatusCode}): {Truncate(body, 150)}");
                    return false;
                }
                Debug($"Redemption {p.RedemptionId} -> {status}.");
                return true;
            }
            catch (Exception ex)
            {
                Debug($"Redemption status request failed: {ex.Message}");
                return false;
            }
        }

        // ==================================================================
        // EventSub -> legacy PubSub translation
        // ==================================================================

        private string BuildEnvelopeFromEventSub(JsonElement ev)
        {
            try
            {
                string S(string name)
                {
                    return ev.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
                        ? el.GetString()
                        : "";
                }

                var channelId = S("broadcaster_user_id");
                var redemptionId = S("id");
                var userId = S("user_id");
                var userLogin = S("user_login");
                var userName = S("user_name");
                var userInput = S("user_input");
                var status = S("status");
                var redeemedAt = S("redeemed_at");

                var rewardId = "";
                var rewardTitle = "";
                var rewardPrompt = "";
                long rewardCost = 0;
                if (ev.TryGetProperty("reward", out var reward) && reward.ValueKind == JsonValueKind.Object)
                {
                    if (reward.TryGetProperty("id", out var ri) && ri.ValueKind == JsonValueKind.String) rewardId = ri.GetString();
                    if (reward.TryGetProperty("title", out var rt) && rt.ValueKind == JsonValueKind.String) rewardTitle = rt.GetString();
                    if (reward.TryGetProperty("prompt", out var rp) && rp.ValueKind == JsonValueKind.String) rewardPrompt = rp.GetString();
                    if (reward.TryGetProperty("cost", out var rc) && rc.ValueKind == JsonValueKind.Number) rewardCost = rc.GetInt64();
                }

                if (string.IsNullOrEmpty(status)) status = "unfulfilled";
                if (string.IsNullOrEmpty(redeemedAt)) redeemedAt = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");

                var inner =
                    "{\"type\":\"reward-redeemed\",\"data\":{" +
                    "\"timestamp\":\"" + J(redeemedAt) + "\"," +
                    "\"redemption\":{" +
                    "\"id\":\"" + J(redemptionId) + "\"," +
                    "\"user\":{\"id\":\"" + J(userId) + "\",\"login\":\"" + J(userLogin) + "\",\"display_name\":\"" + J(userName) + "\"}," +
                    "\"channel_id\":\"" + J(channelId) + "\"," +
                    "\"redeemed_at\":\"" + J(redeemedAt) + "\"," +
                    "\"reward\":{" +
                    "\"id\":\"" + J(rewardId) + "\"," +
                    "\"channel_id\":\"" + J(channelId) + "\"," +
                    "\"title\":\"" + J(rewardTitle) + "\"," +
                    "\"prompt\":\"" + J(rewardPrompt) + "\"," +
                    "\"cost\":" + rewardCost + "," +
                    "\"is_user_input_required\":true," +
                    "\"is_sub_only\":false," +
                    "\"image\":{\"url_1x\":\"\",\"url_2x\":\"\",\"url_4x\":\"\"}," +
                    "\"default_image\":{\"url_1x\":\"\",\"url_2x\":\"\",\"url_4x\":\"\"}," +
                    "\"background_color\":\"#9147FF\"," +
                    "\"is_enabled\":true," +
                    "\"is_paused\":false," +
                    "\"is_in_stock\":true," +
                    "\"max_per_stream\":{\"is_enabled\":false,\"max_per_stream\":0}," +
                    "\"should_redemptions_skip_request_queue\":false" +
                    "}," +
                    "\"user_input\":\"" + J(userInput) + "\"," +
                    "\"status\":\"" + J(status.ToUpperInvariant()) + "\"" +
                    "}}}";

                return
                    "{\"type\":\"MESSAGE\",\"data\":{" +
                    "\"topic\":\"channel-points-channel-v1." + J(channelId) + "\"," +
                    "\"message\":\"" + J(inner) + "\"" +
                    "}}";
            }
            catch (Exception ex)
            {
                Debug($"Envelope translation failed: {ex.Message}");
                return null;
            }
        }

        // ==================================================================
        // Helpers
        // ==================================================================

        // Applies "command | Title | Description | cost | input" overrides
        // (';'-separated) on top of the base spec list. Per the game's own
        // documentation, the chat command only needs to appear in the reward's
        // description for the game to recognize it — so titles can be fully
        // cosmetic.
        private static void ApplyRewardDefinitions(List<RewardSpec> specs, string config, string prefix)
        {
            if (string.IsNullOrWhiteSpace(config)) return;
            foreach (var raw in config.Split(';'))
            {
                var entry = raw.Trim();
                if (entry.Length == 0) continue;
                var f = entry.Split('|');
                var cmd = f[0].Trim();
                if (cmd.Length == 0) continue;

                RewardSpec spec = null;
                foreach (var s in specs)
                {
                    if (string.Equals(s.Command, cmd, StringComparison.OrdinalIgnoreCase)) { spec = s; break; }
                }
                if (spec == null)
                {
                    spec = new RewardSpec { Command = cmd, Title = prefix + cmd, Prompt = "", Cost = 500, RequiresInput = false };
                    specs.Add(spec);
                }

                if (f.Length > 1 && f[1].Trim().Length > 0) spec.Title = f[1].Trim();
                if (f.Length > 2 && f[2].Trim().Length > 0) spec.Prompt = f[2].Trim();
                if (f.Length > 3 && long.TryParse(f[3].Trim(), out var cost) && cost > 0) spec.Cost = cost;
                if (f.Length > 4 && f[4].Trim().Equals("input", StringComparison.OrdinalIgnoreCase)) spec.RequiresInput = true;
            }
        }

        // Safety net: per the official integration guide, the game activates
        // whatever command tokens it finds in the reward title OR description —
        // one reward may carry several. A reward containing no known token
        // would eat viewer points, so append this spec's token only when the
        // title+description contain no recognizable command at all.
        // Twitch caps prompts at 200 characters.
        private void EnsureCommandTokens()
        {
            var knownTokens = new List<string>();
            foreach (var s in _rewardSpecs) knownTokens.Add(_commandPrefix.Value + s.Command);

            foreach (var spec in _rewardSpecs)
            {
                var haystack = (spec.Title ?? "") + " " + (spec.Prompt ?? "");
                var discoverable = false;
                foreach (var t in knownTokens)
                {
                    if (haystack.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0) { discoverable = true; break; }
                }
                if (discoverable) continue;
                var token = _commandPrefix.Value + spec.Command;

                var basePrompt = spec.Prompt ?? "";
                var appended = basePrompt.Length == 0 ? token : basePrompt + " " + token;
                if (appended.Length > 200)
                    appended = appended.Substring(0, Math.Max(0, 199 - token.Length)).TrimEnd() + " " + token;
                spec.Prompt = appended;
                LoggerInstance.Msg($"Reward '{spec.Title}': appended '{token}' to its description so the game can recognize it.");
            }
        }

        private static List<RewardSpec> ParseRewardSpecs(string config, string prefix)
        {
            var specs = new List<RewardSpec>();
            if (string.IsNullOrEmpty(config)) return specs;
            foreach (var raw in config.Split(','))
            {
                var entry = raw.Trim();
                if (entry.Length == 0) continue;
                var parts = entry.Split(':');
                var spec = new RewardSpec
                {
                    Command = parts[0].Trim(),
                    Cost = 500,
                    RequiresInput = false,
                    Prompt = ""
                };
                if (spec.Command.Length == 0) continue;
                for (int i = 1; i < parts.Length; i++)
                {
                    var part = parts[i].Trim();
                    if (string.Equals(part, "input", StringComparison.OrdinalIgnoreCase))
                        spec.RequiresInput = true;
                    else if (long.TryParse(part, out var cost) && cost > 0)
                        spec.Cost = cost;
                }
                spec.Title = prefix + spec.Command;
                if (spec.RequiresInput && spec.Prompt.Length == 0) spec.Prompt = "Enter your request";
                specs.Add(spec);
            }
            return specs;
        }

        private static string J(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            var sb = new StringBuilder(s.Length + 8);
            foreach (var c in s)
            {
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"': sb.Append("\\\""); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }

        private static string Truncate(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length <= max ? s : s.Substring(0, max) + "...";
        }

        private static string SanitizeToken(string t)
        {
            if (string.IsNullOrEmpty(t)) return t;
            t = t.Trim();
            if (t.StartsWith("oauth:", StringComparison.OrdinalIgnoreCase))
                t = t.Substring(6);
            return t;
        }

        private string _lastWarnKey;
        private int _warnRepeat;

        private void RateWarn(string key, string message)
        {
            if (key == _lastWarnKey)
            {
                _warnRepeat++;
                if (_warnRepeat % 10 != 0)
                {
                    Debug(message + $" (x{_warnRepeat})");
                    return;
                }
                message += $" (repeated x{_warnRepeat})";
            }
            else
            {
                _lastWarnKey = key;
                _warnRepeat = 1;
            }
            LoggerInstance.Warning(message);
        }

        // The Twitch CLI mock server speaks plain http/ws; a https/wss scheme
        // pointed at localhost fails with an opaque SSL error. Catch it early.
        private void WarnIfTlsToLocalhost(string entryName, string url)
        {
            try
            {
                if (string.IsNullOrEmpty(url)) return;
                var u = new Uri(url);
                var isLocal = string.Equals(u.Host, "localhost", StringComparison.OrdinalIgnoreCase) ||
                              u.Host == "127.0.0.1" || u.Host == "::1";
                var isTls = string.Equals(u.Scheme, "https", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(u.Scheme, "wss", StringComparison.OrdinalIgnoreCase);
                if (isLocal && isTls)
                {
                    LoggerInstance.Warning($"{entryName} uses {u.Scheme}:// against {u.Host} — the Twitch CLI mock server is plain {(u.Scheme.StartsWith("w", StringComparison.OrdinalIgnoreCase) ? "ws" : "http")}://. This will fail with SSL errors; fix the scheme in UserData/SynthChannelPoints.cfg.");
                }
            }
            catch { }
        }

        private void Debug(string message)
        {
            if (_debugLogging.Value) LoggerInstance.Msg("[Debug] " + message);
        }

        private static object GetMemberValue(Type type, object instance, string name, out bool found)
        {
            found = false;
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic |
                                       BindingFlags.Instance | BindingFlags.Static;
            try
            {
                var prop = type.GetProperty(name, flags);
                if (prop != null && prop.CanRead)
                {
                    found = true;
                    return prop.GetValue(prop.GetGetMethod(true).IsStatic ? null : instance);
                }
            }
            catch { }
            try
            {
                var field = type.GetField(name, flags);
                if (field != null)
                {
                    found = true;
                    return field.GetValue(field.IsStatic ? null : instance);
                }
            }
            catch { }
            return null;
        }

        private static bool SetMemberValue(Type type, object instance, string name, object value, out string error)
        {
            error = null;
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic |
                                       BindingFlags.Instance | BindingFlags.Static;
            try
            {
                var prop = type.GetProperty(name, flags);
                if (prop != null && prop.CanWrite)
                {
                    var setter = prop.GetSetMethod(true);
                    prop.SetValue(setter != null && setter.IsStatic ? null : instance, value);
                    return true;
                }
            }
            catch (Exception ex) { error = $"property set: {ex.Message}"; }
            try
            {
                var field = type.GetField(name, flags);
                if (field != null)
                {
                    field.SetValue(field.IsStatic ? null : instance, value);
                    return true;
                }
            }
            catch (Exception ex) { error = (error == null ? "" : error + "; ") + $"field set: {ex.Message}"; }
            if (error == null) error = "no writable property or field found";
            return false;
        }

        private static List<string> ReadStringArray(object arr)
        {
            var list = new List<string>();
            if (arr == null) return list;
            try
            {
                var en = arr as System.Collections.IEnumerable;
                if (en != null && !(arr is string))
                {
                    foreach (var o in en) list.Add(o == null ? "" : o.ToString());
                    return list;
                }
                var t = arr.GetType();
                var lenP = t.GetProperty("Length") ?? t.GetProperty("Count");
                var getL = t.GetMethod("get_Item", new[] { typeof(long) });
                var getI = t.GetMethod("get_Item", new[] { typeof(int) });
                if (lenP != null && (getL != null || getI != null))
                {
                    int n = Convert.ToInt32(lenP.GetValue(arr));
                    for (int i = 0; i < n; i++)
                    {
                        object v = getL != null
                            ? getL.Invoke(arr, new object[] { (long)i })
                            : getI.Invoke(arr, new object[] { i });
                        list.Add(v == null ? "" : v.ToString());
                    }
                }
            }
            catch { }
            return list;
        }

        private object BuildIl2CppStringArray(object templateArray, string[] values)
        {
            var arrType = templateArray.GetType();
            try
            {
                var c = arrType.GetConstructor(new[] { typeof(string[]) });
                if (c != null) return c.Invoke(new object[] { values });
            }
            catch (Exception ex) { Debug($"ctor(string[]) failed: {ex.Message}"); }
            try
            {
                var op = arrType.GetMethod("op_Implicit",
                    BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string[]) }, null);
                if (op != null) return op.Invoke(null, new object[] { values });
            }
            catch (Exception ex) { Debug($"op_Implicit failed: {ex.Message}"); }
            try
            {
                var c = arrType.GetConstructor(new[] { typeof(long) });
                var setL = arrType.GetMethod("set_Item", new[] { typeof(long), typeof(string) });
                var setI = arrType.GetMethod("set_Item", new[] { typeof(int), typeof(string) });
                if (c != null && (setL != null || setI != null))
                {
                    var arr = c.Invoke(new object[] { (long)values.Length });
                    for (int i = 0; i < values.Length; i++)
                    {
                        if (setL != null) setL.Invoke(arr, new object[] { (long)i, values[i] });
                        else setI.Invoke(arr, new object[] { i, values[i] });
                    }
                    return arr;
                }
            }
            catch (Exception ex) { Debug($"ctor(long)+indexer failed: {ex.Message}"); }
            return null;
        }

        private static bool ShouldSkipAssembly(string name)
        {
            for (int i = 0; i < SkipAssemblyPrefixes.Length; i++)
            {
                if (name.StartsWith(SkipAssemblyPrefixes[i], StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private static Type ResolveTypeByCandidates(string[] candidates)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                string asmName;
                try { asmName = asm.GetName().Name ?? ""; }
                catch { continue; }
                if (ShouldSkipAssembly(asmName)) continue;

                foreach (var name in candidates)
                {
                    try
                    {
                        var t = asm.GetType(name, throwOnError: false);
                        if (t != null) return t;
                    }
                    catch { }
                }
            }
            return null;
        }
    }
}
