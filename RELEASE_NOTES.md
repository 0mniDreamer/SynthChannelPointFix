# SynthChannelPoints v1.1.1 — release post

**Channel point redemptions are back — and better than they ever were.**

Twitch shut down the service Synth Riders used for channel point rewards in April 2025. Chat commands kept working; point redemptions silently died. SynthChannelPoints brings them back, and goes further than the original integration:

- **Redemptions work again** — song requests and effect rewards flow into the game exactly as they used to. Works on both game branches (current and previous).
- **The mod creates your rewards for you.** Enable a feature in the game's Twitch settings and the matching reward appears on your Twitch dashboard within seconds — correctly named, priced, and configured. Turn it off in-game and the reward disables. The master Channel Points toggle controls them all at once. Costs configurable.
- **Failed song requests refund automatically.** Song not found? Limit reached? The viewer's points come back within seconds. Successful requests are marked fulfilled and cleared from your redemption queue. (Applies to mod-created rewards — Twitch only lets the creating app manage a reward. If you made an `!srr` reward by hand, delete it and let the mod recreate it.)
- **Zero setup.** Uses your existing in-game Twitch login — the one extra permission the mod needs is folded into the game's normal link flow; approve it once when prompted.

Requires MelonLoader 0.7+ and Twitch Affiliate/Partner (channel points are a Twitch feature).

**Notes:**
- The `!srr` reward appears once the reward-request toggle is enabled in the game's Twitch settings — it mirrors that switch by design.
- `!invaderz` redemptions contribute their point cost toward the in-game bits meter (shared with cheered bits); set the reward cost to your bits threshold for one-redemption-one-invader.
- **Updating from a test build:** delete the `[SynthChannelPoints]` section from `UserData/MelonPreferences.cfg` so the new defaults regenerate.

Thanks to Blatzk for affiliate testing.

Download: [link] • Source: [link] • Issues: include `MelonLoader/Latest.log` with `DebugLogging = true`
