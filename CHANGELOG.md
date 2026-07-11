# Changelog

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
