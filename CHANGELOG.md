# Changelog

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
