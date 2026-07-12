# SynthChannelPoints

**Restores Twitch channel point redemptions in Synth Riders (PCVR).**

Twitch shut down its legacy PubSub service in April 2025. Synth Riders' built-in Twitch integration relied on it for channel point redemptions, so point-based song requests and effect rewards silently stopped working — while chat commands (`!srr` etc.) kept working, because those use a different connection.

This mod fixes redemptions by bridging Twitch's replacement service (EventSub) into the game.

## Features

- Channel point redemptions work again — song requests, effects, everything the game supported
- **Zero setup**: uses the game's existing Twitch login; no separate authentication, no tokens to paste
- **Auto-created rewards** (v1.1): the mod creates channel point rewards for every game feature you enable — song requests, timewarp, speed, colors, effects — after a one-time permission approval. A reward is created the first time you enable its feature in the game's Twitch settings
- **Settings mirroring** (v1.1): turning a feature off in the game's Twitch settings disables its reward on Twitch, and vice versa
- **Point refunds** (v1.1): failed song requests (song not found, queue full, limits hit) automatically refund the viewer's points; successful ones are marked fulfilled
- The game's own logic handles everything downstream — reward matching, queue, cooldowns, per-user limits, and chat replies behave exactly as they originally did
- Stops the game from endlessly retrying the dead PubSub endpoint

## The one-time permission prompt (v1.1)

Managing rewards needs one extra Twitch permission the game never asked for. The mod adds it to the game's own login request, so on first launch after updating, the game will walk you through a quick re-authorization in your browser (approve once, done). Everything except reward management works even if you skip it. Set `ManageRewards = false` to disable the feature and the prompt entirely.

**Note:** refunds and settings mirroring only work for rewards the mod created — Twitch only allows the creating app to manage a reward. If you already made an `!srr` reward by hand, delete it on your dashboard and let the mod recreate it.

## Requirements

- Synth Riders PCVR with MelonLoader 0.7+
- Twitch linked in the game's Twitch settings, with Channel Point Mode enabled
- Twitch Affiliate or Partner (channel points are a Twitch account feature)

## Installation

Drop `SynthChannelPoints.dll` into your `Mods` folder:
`<SteamLibrary>\steamapps\common\SynthRiders\Mods\`

## Setting up rewards

The mod creates and names rewards for you. By default they get friendly names with the command in the description — "Song Request", "Slow Motion", "Rainbow Notes" and so on. Customize any of them (or add multi-command rewards) via `RewardDefinitions` — per the game's documentation, the command only needs to appear in the reward's **description** for the game to recognize it, e.g.:

```
RewardDefinitions = "srr | Song Request | Request any song! Powered by !srr | 500 | input ; timewarp | Slow Motion | Slows the song for a moment (!timewarp)"
```

If you forget to include a command anywhere, the mod appends it to the description automatically — a reward the game can't recognize would just eat viewer points.

Per the official integration guide, a single reward may carry **multiple** command tokens (each activates once) — e.g. a "Chaos Mode" reward with `!rainbow !vanish !embiggen` in its description fires all three. The invader-count variants `!1invaderz`, `!2invaderz`, `!3invaderz` are also valid tokens.

**Refunds and the game's token system:** when a song-request redemption finds no match, the game grants the viewer a request token (usable later with chat `!srr`) — and with `RefundFailedRequests` on, the mod refunds their points as well. That double compensation is deliberate generosity; set `RefundFailedRequests = false` if you prefer the game's token-only behavior.

## Config (`UserData/SynthChannelPoints.cfg`)

The mod keeps its settings in its own file so the shared `MelonPreferences.cfg` stays clean. On first launch after updating, existing settings migrate over automatically.

| Entry | Default | Purpose |
|---|---|---|
| `Enabled` | `true` | Master switch |
| `DebugLogging` | `false` | Verbose diagnostics for bug reports |
| `EventSubUrl` | `wss://eventsub.wss.twitch.tv/ws` | EventSub endpoint (change only for mock testing) |
| `HelixSubscriptionsUrl` | `https://api.twitch.tv/helix/eventsub/subscriptions` | Subscription endpoint (change only for mock testing) |
| `SuppressGamePubSubConnect` | `true` | Stops the game retrying the dead PubSub endpoint |
| `ManageRewards` | `true` | Auto-create rewards, mirror settings, enable refunds (needs one-time re-consent) |
| `RewardCommands` | all game commands | Rewards to manage: `command[:cost][:input]`, comma-separated |
| `CommandPrefix` | `!` | Prefix used in reward titles |
| `RewardDefinitions` | friendly names for all commands | Custom names/descriptions: `command \| Title \| Description \| cost \| input`, ';'-separated. Set to a single command entry or edit freely; costs default from `RewardCommands` |
| `RefundFailedRequests` | `true` | Refund points when a song request fails |
| `DisableRewardsOnExit` | `true` | Disable managed rewards when the game closes; re-enabled on next launch |
| `RewardsFollowChannelPointMode` | `true` | Hide rewards while Channel Point Mode is off (see below) |
| `AutoCompleteRequests` | `true` | Mark successful requests FULFILLED |
| `HelixBaseUrl` | `https://api.twitch.tv/helix` | Helix base for reward management |

## Troubleshooting

**"Channel point redemptions restored via EventSub for <name>"** in the console = everything is working.

**"The game's Twitch token is invalid or expired"** — open the game's Twitch settings and re-link your account, then restart.

**"Game token lacks channel:read:redemptions scope"** — re-link Twitch in the game's settings so a fresh token with full permissions is issued.

**"Helix rejected the subscription (403)"** — your Twitch account doesn't have channel points available (Affiliate/Partner required).

**"Non-default endpoints configured"** — you previously tested against the Twitch CLI mock server; delete the two URL entries from `UserData/SynthChannelPoints.cfg` (they'll regenerate with live defaults).

**`!invaderz` redemptions reply "N Bits until the next Invader"** — working as designed: the Invaderz feature runs on a bits meter, and a redemption contributes its point cost toward it (shared with real cheered bits). Two 300-point redemptions against a 500-bit threshold spawn one invader. For one-redemption-one-invader, set the reward cost to meet your in-game bits threshold (e.g. `invaderz:500` in `RewardCommands`) or lower the threshold in the game's Twitch settings.

**What Channel Point Mode actually does** — per the official guide, the game's `channelpointmode` flag only controls whether *chat* commands are blocked (true = rewards-only); the game never disabled redemptions with it. With `RewardsFollowChannelPointMode = true` (default), the mod turns it into a clean mode switch: CPM on = rewards-only, CPM off = chat-only (rewards hidden on Twitch, and any redemption slipping through the sync window is dropped and refunded). Set it to `false` for the vanilla behavior where chat commands and redemptions both work at once.

**No `!srr` reward appears (but effect rewards do)** — the song-request reward mirrors a dedicated toggle in the game's Twitch settings (separate from chat requests). Enable it in-game and the reward is created within ~16 seconds.

**"Blocked a duplicate redemption execution" in the console** — working as intended: the game itself double-processes redemptions while you're in the menu (a vanilla bug that predates this mod); the guard runs each redemption exactly once.

**Redemptions arrive but nothing happens in game** — check the reward title matches the chat command exactly (including `!`), and that Channel Point Mode + the specific feature are enabled in the game's Twitch settings.

For bug reports, set `DebugLogging = true`, reproduce, and include `MelonLoader/Latest.log`.

## How it works (technical)

The mod reads the game's own Twitch credentials via reflection, opens an EventSub WebSocket, and subscribes to `channel.channel_points_custom_reward_redemption.add` for your channel. Each notification is translated into the legacy PubSub `reward-redeemed` envelope and injected into the game's bundled TwitchLib parser through its public `TestMessageParser` method — so from the game's perspective, PubSub never died. Network I/O runs on a background thread; all game interaction happens on the main thread. Built as a single cross-branch assembly referencing only MelonLoader-bundled libraries, with all game types resolved at runtime via candidate name sets.

## Building from source

Set `<GamePath>` in `SynthChannelPoints.csproj` to your Synth Riders install, then `dotnet build -c Release`. Requires the game to have been run once with MelonLoader so the interop assemblies exist.

## Special Thanks 
Thanks to [Blatzk](https://www.twitch.tv/blatzk)) for affiliate testing and everyone else that offered.
