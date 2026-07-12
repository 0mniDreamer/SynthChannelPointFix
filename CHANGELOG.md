# Changelog

## 1.2.0
- **Friendly reward names by default**: rewards are now created as "Song Request", "Slow Motion", "Rainbow Notes" etc., with the chat command in the description (description-based matching verified live). Existing plain-titled rewards are auto-disabled as orphans — delete them on your dashboard
- **Custom reward names and descriptions** (`RewardDefinitions`): rewards can now have cosmetic titles (e.g. "Song Request" instead of "!srr") with the chat command placed in the description, per the game's documentation. Format: `command | Title | Description | cost | input`, rewards separated by ';'
- Safety net: if a reward's title and description contain no recognizable command token, this reward's token is appended to the description automatically — a reward the game can't recognize would silently eat viewer points. Rewards carrying other commands' tokens (multi-command rewards, per the official guide) are left untouched
- Description changes sync to Twitch like cost changes; renamed rewards' old versions are disabled automatically (delete them on the dashboard if unwanted)
- Redemptions of rewards the mod considers disabled are now dropped and refunded — the game itself never gated redemptions on channelpointmode (that flag only blocks chat commands, per the official guide; mock-verified), so this closes the mirror-lag and crash-leftover windows
- Dedupe guard (`DeduplicateRedemptions`, default on) for a vanilla game bug: the game attaches its redemption handler twice while in the menu, so menu-state redemptions executed every command twice (invisible for a year while PubSub was dead). Redemption ids are unique, so the guard fixes it with zero risk of blocking a legitimate redemption
- Config moved to its own file, `UserData/SynthChannelPoints.cfg` — existing settings migrate automatically on first launch; the old section in MelonPreferences.cfg can be deleted
- New `RewardsFollowChannelPointMode` (default true): true keeps the chat-mode/rewards-mode behavior; false lets rewards follow only their feature toggles so chat and redemptions work simultaneously (vanilla semantics)


## 1.1.2
- Rewards are disabled automatically when the game closes (`DisableRewardsOnExit`, default on) — previously they stayed redeemable in the channel's points menu with nothing running to respond or refund. They re-enable on next launch per your settings
- Settings changes now mirror to Twitch within ~2 seconds instead of up to 16 — turning Channel Point Mode off removes the rewards from chat almost immediately, leaving chat commands as the only path


## 1.1.1
- Refund deadline raised 15s → 30s: live testing measured successful queue-adds up to ~16s after redemption, which risked refunding a successful request
- Documentation: the `!srr` reward only appears once the game's reward-request toggle is enabled in its Twitch settings — this is deliberate mirroring of a real in-game control, now explained in the README
- Confirmed live on the previous game branch (Unity 2021.3.45f2): redemptions, reward creation, toggle mirroring, FULFILLED on success, automatic refunds


## 1.1.0
- **Automatic reward management** (`ManageRewards`, default on): the mod extends the game's Twitch permission request; after a one-time re-consent it auto-creates channel point rewards for all game commands (`!srr`, `!timewarp`, `!speed`, `!superspeed`, `!color`, `!rainbow`, `!vanish`, `!embiggen`, `!minimize`, `!warp`, `!invaderz` — all empirically verified against the game's handler). Rewards are created when their feature is first enabled in-game; costs configurable via `RewardCommands`
- **Settings mirroring**: rewards are enabled/disabled on Twitch to match the game's Twitch settings toggles (Channel Point Mode + per-feature switches)
- **Point refunds** (`RefundFailedRequests`): song-request redemptions that don't result in a queued song are automatically CANCELED, returning the viewer's points
- **Auto-complete** (`AutoCompleteRequests`): successful song requests are marked FULFILLED, keeping the Twitch redemption queue clean
- Refunds/auto-complete only apply to mod-created rewards (a Twitch platform restriction); manually created rewards keep v1.0 behavior
- Clean uninstall: removing the mod never forces a re-auth
- Safety guard: redemptions of input-requiring rewards with empty text are dropped and refunded when mod-managed (the game otherwise silently consumes them — points spent, nothing queued)

## 1.0.2
- Startup warning when non-default (mock/test) endpoints are left in the config
- Release documentation

## 1.0.1
- Strip TwitchLib `oauth:` prefix from the game token before Helix use (fixes 401 rejections)
- Use the token's own issuing client id (from `/oauth2/validate`) for Helix requests
- Skip Helix entirely when the token is known-invalid; clear guidance to re-link Twitch
- Collapse repeated identical warnings (no more log spam)

## 1.0.0
- Initial release: EventSub WebSocket client using the game's own credentials, translation of redemption notifications into legacy PubSub envelopes, injection via TwitchLib's `TestMessageParser`, suppression of the game's dead PubSub reconnect loop
