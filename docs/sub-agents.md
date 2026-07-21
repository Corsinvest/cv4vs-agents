# Sub-agents

Claude can fan work out to **sub-agents** — separate agents it spawns to handle a piece of the task
in parallel (searching a large codebase, running independent checks, reviewing several files at
once). They run on their own, report back, and the main turn continues.

That is powerful and easy to lose track of: several agents can be running at once, each burning
tokens, and a chat that simply streamed everything would drown you. The extension surfaces them in
two places — a **live panel** while they run, and **collapsible rows** in the conversation.

---

## While they run: the composer chip

As soon as a sub-agent starts, a chip appears in the composer toolbar: a bot icon with a **badge
counting the active sub-agents**. It is only there while something is running — no sub-agents, no
chip.

Clicking it opens the sub-agents panel:

![Sub-agents panel](images/subagents-panel.png)

Each row shows:

| | |
|---|---|
| A live dot | the agent is running |
| Description | what it was asked to do |
| Current tool · totals | the tool it is using right now, then tool count and tokens spent |
| Elapsed | how long it has been running |
| Stop | ends that agent |

Plus **Stop all** in the header, which stops every one of them.

The current tool matters more than it looks: with several near-identical agents ("Run build
simulation loop", "Run scan simulation loop") it is often the only thing that tells them apart.

Background and async sub-agents are tracked too — the turn is reported as *finished* only once they
have actually finished, not when the main reply ends.

---

## In the conversation: nested rows

While a sub-agent works, its own tool calls appear nested inside the Agent row that spawned it, so
you can watch it without leaving the conversation.

The rows populate as they happen, and the box stays readable by showing only the **last 3** steps.
Expand the row and the full transcript is fetched — the whole run, every step, in order. Collapse it
and it goes back to the last 3.

This is the same lazy rule the rest of the chat follows: nothing heavy is loaded until you ask for
it. A sub-agent that ran for two hundred steps costs nothing to scroll past, and shows everything
the moment you open it.

Sub-agent transcripts are replayed in history too, so re-opening an old session shows the same
nested structure — again, fetched only when you expand.

---

## Stopping them

Two ways, both from the panel:

- **Stop** on a row — that agent only. The others carry on.
- **Stop all** — every running sub-agent.

There is no confirmation: stopping is immediate. A stopped sub-agent reports back as cancelled and
the main turn continues with what it has.
