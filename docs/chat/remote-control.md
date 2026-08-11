# Remote Control

Remote Control lets you drive a running chat session from `claude.ai/code` or the Claude mobile
app. The session itself keeps running on your machine, in this pane, talking to your solution and
your MCP tools — the web page and the phone are just a window onto it, not a copy of it.

## Turning it on

Run **`/remote-control`** (or the shorter `/remote`, `/rc`, `/phone`) from the `/` command menu. It
renders as a toggle, not a one-shot action: switching it on sends a control request to the running
CLI and waits for it to come up, switching it off tears the bridge down again. While the request is
in flight the toggle is briefly disabled, so a second click can't race the first.

## The banner

Once the CLI confirms the bridge is up, a banner appears at the top of the chat with the
`claude.ai/code` session link. Click it and it opens in your default browser — the WebView never
navigates there itself.

The banner has no close button, and that is deliberate: it mirrors the live state of the bridge
rather than something you dismiss. It appears when the bridge connects and disappears when the
bridge goes away, for any reason — you turned it off, or the CLI did.

If the CLI can't establish the bridge — no login, a disabled feature flag, a stale CLI — that
surfaces as a separate, dismissible error notice instead of a banner, with the CLI's own reason
turned into a plain sentence.

## It dies with the pane

Remote Control lives inside the `claude.exe` process the pane is driving. Anything that restarts
that process — closing the pane, reloading a session from history, changing the working directory,
forking — ends the remote session along with it, and the banner clears accordingly. This isn't a
choice this extension makes; it's how the CLI's control protocol works, and every pane in this
extension already respawns the process for those same actions.

The bridge can also drop on its own — the network goes away, the token expires, you end the session
from the phone. When that happens the CLI reports it as a `bridge_state` event and the banner goes
away here too, without you having to touch the toggle.

## Requirements

Remote Control depends on things outside this extension, and when it fails, the cause is almost
always one of these:

- You need to be logged in to claude.ai (`/login`) — an API key alone isn't enough.
- It isn't available on Amazon Bedrock, Google Vertex AI, or Microsoft Foundry, and not with a
  custom `ANTHROPIC_BASE_URL` — Remote Control assumes the standard Anthropic API.
- On Team and Enterprise plans, an Owner has to turn Remote Control on in the organization's admin
  settings before anyone on the plan can use it.
- `DISABLE_TELEMETRY`, `DO_NOT_TRACK`, `CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC` or
  `DISABLE_GROWTHBOOK` all disable the feature-flag check Remote Control relies on to know it's
  available, so any of them set will keep it off.

For the full picture of the underlying feature — what the web and mobile side look like, how the
session syncs — see the upstream docs at
[code.claude.com/docs/en/remote-control](https://code.claude.com/docs/en/remote-control).
