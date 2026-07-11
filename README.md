# SynthChannelPoints

**Restores Twitch channel point redemptions in Synth Riders (PCVR).**

Twitch shut down its legacy PubSub service in April 2025. Synth Riders' built-in Twitch integration relied on it for channel point redemptions, so point-based song requests and effect rewards silently stopped working — while chat commands (`!srr` etc.) kept working, because those use a different connection.

This mod fixes redemptions by bridging Twitch's replacement service (EventSub) into the game.

## Features

- Channel point redemptions work again — song requests, effects, everything the game supported
- **Zero setup**: uses the game's existing Twitch login; no separate authentication, no tokens to paste
- The game's own logic handles everything downstream — reward matching, queue, cooldowns, per-user limits, and chat replies behave exactly as they originally did
- Stops the game from endlessly retrying the dead PubSub endpoint

## Requirements

- Synth Riders PCVR with MelonLoader 0.7+
- Twitch linked in the game's Twitch settings, with Channel Point Mode enabled
- Twitch Affiliate or Partner (channel points are a Twitch account feature)

## Installation

Drop `SynthChannelPoints.dll` into your `Mods` folder:
`<SteamLibrary>\steamapps\common\SynthRiders\Mods\`

## Setting up rewards

Create channel point rewards on your Twitch dashboard named **exactly like the chat command**, including the prefix. For song requests: a reward titled `!srr` with "Require Viewer to Enter Text" enabled — the viewer's text is the song search. Other commands follow the same pattern (e.g. `!timewarp`). This is the naming the game itself expects; the mod doesn't change it.

## Config (`UserData/MelonPreferences.cfg` → `[SynthChannelPoints]`)

| Entry | Default | Purpose |
|---|---|---|
| `Enabled` | `true` | Master switch |
| `DebugLogging` | `false` | Verbose diagnostics for bug reports |
| `EventSubUrl` | `wss://eventsub.wss.twitch.tv/ws` | EventSub endpoint (change only for mock testing) |
| `HelixSubscriptionsUrl` | `https://api.twitch.tv/helix/eventsub/subscriptions` | Subscription endpoint (change only for mock testing) |
| `SuppressGamePubSubConnect` | `true` | Stops the game retrying the dead PubSub endpoint |

## Troubleshooting

**"Channel point redemptions restored via EventSub for <name>"** in the console = everything is working.

**"The game's Twitch token is invalid or expired"** — open the game's Twitch settings and re-link your account, then restart.

**"Game token lacks channel:read:redemptions scope"** — re-link Twitch in the game's settings so a fresh token with full permissions is issued.

**"Helix rejected the subscription (403)"** — your Twitch account doesn't have channel points available (Affiliate/Partner required).

**"Non-default endpoints configured"** — you previously tested against the Twitch CLI mock server; delete the two URL entries from `MelonPreferences.cfg` (they'll regenerate with live defaults).

**Redemptions arrive but nothing happens in game** — check the reward title matches the chat command exactly (including `!`), and that Channel Point Mode + the specific feature are enabled in the game's Twitch settings.

For bug reports, set `DebugLogging = true`, reproduce, and include `MelonLoader/Latest.log`.

## How it works (technical)

The mod reads the game's own Twitch credentials via reflection, opens an EventSub WebSocket, and subscribes to `channel.channel_points_custom_reward_redemption.add` for your channel. Each notification is translated into the legacy PubSub `reward-redeemed` envelope and injected into the game's bundled TwitchLib parser through its public `TestMessageParser` method — so from the game's perspective, PubSub never died. Network I/O runs on a background thread; all game interaction happens on the main thread. Built as a single cross-branch assembly referencing only MelonLoader-bundled libraries, with all game types resolved at runtime via candidate name sets.

## Building from source

Set `<GamePath>` in `SynthChannelPoints.csproj` to your Synth Riders install, then `dotnet build -c Release`. Requires the game to have been run once with MelonLoader so the interop assemblies exist.
