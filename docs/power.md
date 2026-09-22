# Keeping the machine awake

A long turn is easy to walk away from. If Windows suspends the machine while `claude.exe` is
mid-turn, the process is frozen where it stands: the turn never completes, and the session is still
hanging there when you come back and notice.

While a chat pane is working, the extension asks Windows to keep the **system** running.

## What it holds off, and what it does not

The request covers the **idle** sleep and hibernate timers — the case this exists for: start a long
refactor, walk away, come back to a finished turn instead of a hung one.

The **display still blanks** on its own timer. Only the machine is kept running, so nothing is lit
up for no reason.

It does **not** hold off:

- **Sleep you asked for** — closing the lid, the power button, Start → Sleep. Windows drops every
  power request on a deliberate sleep, by design: an application does not get to veto you.
- **Modern standby on battery.** On a laptop using modern standby (S0 low power idle), Windows
  terminates system power requests 5 minutes after the sleep timer expires, whatever we ask for. A
  turn longer than that can still be suspended while on battery. On AC power, and on older laptops
  using traditional S3 sleep, the hold lasts for the whole turn.
- **Forced sleep** — group policy, a critical battery level, or `shutdown`-style commands.

## When it holds

Only while a chat pane is actually working:

| | |
|---|---|
| a turn is running | held |
| background agents still running after the turn's reply | held — the work outlives the reply |
| waiting for you to approve a tool | **not** held — nothing is computing, and you may be away |
| pane open but idle | not held |
| a CLI pane | never held |

CLI panes are excluded on purpose. A terminal has no notion of a turn, and telling a running
command apart from an idle prompt would mean scraping its output — which would eventually hold the
machine awake forever on a pane sitting at a blinking cursor.

The hold is also released when the pane is closed, when `claude.exe` exits or is killed, and when
Visual Studio shuts down. If the CLI stops responding entirely — no reply, no exit — a watchdog
releases it after a few minutes of silence and says so in the Output window.

## Seeing it

Windows can tell you who is keeping the machine awake. From an **elevated** prompt:

```
powercfg /requests
```

While a turn runs, the `SYSTEM:` section names us:

```
SYSTEM:
[PROCESS] …\devenv.exe
cv4vs Agents: a chat turn is running
```

Nothing there means nothing is held — which is the expected answer between turns, and while a
permission prompt is waiting on you.

## Turning it off

Tools → Options → cv4vs Agents → General → **Prevent the machine from sleeping while a session is
running**. On by default; unticking it releases any hold immediately rather than at the end of the
current turn.

See [Options → General](options.md#general).
