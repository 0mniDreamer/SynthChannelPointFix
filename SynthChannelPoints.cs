using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MelonLoader;
using UnityEngine;

[assembly: MelonInfo(typeof(SynthChannelPoints.Core), "SynthChannelPoints", "1.0.2", "OmniDreamer")]
[assembly: MelonGame(null, null)]

namespace SynthChannelPoints
{
    /// <summary>
    /// Restores Twitch channel point redemptions in Synth Riders.
    ///
    /// The game's built-in integration listens on Twitch PubSub, which was
    /// decommissioned in April 2025. This mod connects to EventSub over
    /// WebSocket using the game's own OAuth credentials, translates
    /// channel.channel_points_custom_reward_redemption.add notifications into
    /// the legacy PubSub 'reward-redeemed' envelope, and feeds them into the
    /// game's own TwitchLib parser via the public TestMessageParser seam.
    /// The game's downstream handling (title matching, commands, queue,
    /// chat replies) runs completely unmodified.
    /// </summary>
    public class Core : MelonMod
    {
        // ------------------------------------------------------------------
        // Config
        // ------------------------------------------------------------------

        private MelonPreferences_Entry<bool> _enabled;
        private MelonPreferences_Entry<bool> _debugLogging;
        private MelonPreferences_Entry<string> _eventSubUrl;
        private MelonPreferences_Entry<string> _helixSubUrl;
        private MelonPreferences_Entry<bool> _suppressGamePubSub;

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

        private static readonly string[] InstanceMemberCandidates = { "s_instance", "GetInstance" };

        private static readonly string[] SkipAssemblyPrefixes =
        {
            "System", "mscorlib", "netstandard", "Microsoft.",
            "MelonLoader", "0Harmony", "Il2CppInterop", "Mono.",
            "SynthChannelPoints"
        };

        // ------------------------------------------------------------------
        // Game references (main thread only)
        // ------------------------------------------------------------------

        private Type _botType;
        private object _bot;
        private object _pubsub;
        private MethodInfo _testMessageParser;
        private bool _gameReady;
        private bool _suppressionApplied;
        private HarmonyLib.Harmony _harmony;

        // ------------------------------------------------------------------
        // Credential snapshot (written on main thread, read on client thread)
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

        // ------------------------------------------------------------------
        // Client state
        // ------------------------------------------------------------------

        private Task _clientTask;
        private CancellationTokenSource _cts;
        private readonly ConcurrentQueue<string> _injectQueue = new ConcurrentQueue<string>();
        private int _frameCounter;
        private bool _announcedActive;

        public override void OnInitializeMelon()
        {
            var cat = MelonPreferences.CreateCategory("SynthChannelPoints");
            _enabled = cat.CreateEntry("Enabled", true,
                description: "Master switch. Set false to disable the mod without removing it.");
            _debugLogging = cat.CreateEntry("DebugLogging", false,
                description: "Verbose diagnostic output.");
            _eventSubUrl = cat.CreateEntry("EventSubUrl", "wss://eventsub.wss.twitch.tv/ws",
                description: "EventSub WebSocket endpoint. Point at ws://127.0.0.1:8080/ws to test against the Twitch CLI mock server.");
            _helixSubUrl = cat.CreateEntry("HelixSubscriptionsUrl", "https://api.twitch.tv/helix/eventsub/subscriptions",
                description: "Helix subscriptions endpoint. Point at http://127.0.0.1:8080/eventsub/subscriptions for the Twitch CLI mock server.");
            _suppressGamePubSub = cat.CreateEntry("SuppressGamePubSubConnect", true,
                description: "Prevent the game from retrying its dead PubSub endpoint (decommissioned April 2025).");

            LoggerInstance.Msg("SynthChannelPoints v1.0.2 loaded — channel point redemptions via EventSub.");

            const string defaultEventSub = "wss://eventsub.wss.twitch.tv/ws";
            const string defaultHelix = "https://api.twitch.tv/helix/eventsub/subscriptions";
            if (!string.Equals(_eventSubUrl.Value, defaultEventSub, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(_helixSubUrl.Value, defaultHelix, StringComparison.OrdinalIgnoreCase))
            {
                LoggerInstance.Warning($"Non-default endpoints configured (EventSubUrl={_eventSubUrl.Value}, HelixSubscriptionsUrl={_helixSubUrl.Value}). If you previously tested with the Twitch CLI mock server, reset these in MelonPreferences.cfg for live use.");
            }
        }

        public override void OnUpdate()
        {
            if (!_enabled.Value) return;

            // Drain pending injections (main thread — game API access is safe here).
            while (_injectQueue.TryDequeue(out var envelope))
            {
                InjectEnvelope(envelope);
            }

            // Periodic housekeeping ~ every 2 seconds at 90 fps.
            _frameCounter++;
            if (_frameCounter < 180) return;
            _frameCounter = 0;

            try
            {
                if (!_gameReady) TryResolveGame();
                if (_gameReady)
                {
                    RefreshCredSnapshot();
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
            try { _cts?.Cancel(); } catch { }
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

                _harmony = new HarmonyLib.Harmony("SynthChannelPoints.suppress");
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

        private static bool SuppressConnectPrefix() => false;

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
            if (_clientTask != null && !_clientTask.IsCompleted) return;
            var creds = _creds;
            if (creds == null || !creds.IsComplete) return;

            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _clientTask = Task.Run(() => RunClientAsync(token), token);
            Debug("EventSub client task started.");
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
                _gameReady = false; // force re-resolution next tick; envelope is dropped
            }
        }

        // ==================================================================
        // EventSub client (background thread — never touches IL2CPP objects)
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
                    var subscribed = isReconnect; // subscriptions survive session_reconnect

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

                        // Dedupe on message id (redeliveries after reconnects).
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
                                    var envelope = BuildEnvelopeFromEventSub(ev);
                                    if (envelope != null)
                                    {
                                        _injectQueue.Enqueue(envelope);
                                        Debug("Redemption notification queued for injection.");
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
                if (reconnectUrl != null) continue; // immediate hop to reconnect URL

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

            // Validate the token: confirms it is Helix-usable, reveals the issuing
            // client id (which MUST be sent as Client-Id, or Helix returns 401),
            // and reports scopes.
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
                        return false; // no point hitting Helix with a known-bad token
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
                return null; // receive timeout — caller decides based on keepalive age
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

        // TwitchLib IRC token convention stores tokens as "oauth:xxxx"; Helix
        // requires the bare token. Strip the prefix and whitespace.
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

        // Collapses repeated identical warnings: first occurrence logs at
        // Warning, repeats log at Debug except every 10th.
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

        private static string Truncate(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length <= max ? s : s.Substring(0, max) + "...";
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

        private static Type ResolveTypeByCandidates(string[] candidates)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                string asmName;
                try { asmName = asm.GetName().Name ?? ""; }
                catch { continue; }

                var skip = false;
                for (int i = 0; i < SkipAssemblyPrefixes.Length; i++)
                {
                    if (asmName.StartsWith(SkipAssemblyPrefixes[i], StringComparison.OrdinalIgnoreCase))
                    {
                        skip = true;
                        break;
                    }
                }
                if (skip) continue;

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
