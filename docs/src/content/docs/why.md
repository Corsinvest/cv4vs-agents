---
title: Why
description: Why this Visual Studio extension for Claude Code exists, in the words of the person who wrote it.
---

I live in two editors. VS Code is light and quick, and Claude Code feels right at home there.
But when the work gets serious (a large solution, a real refactor, a debugger session that has
to just *work*), I reach for Visual Studio 2022/2026. For that kind of code, VS is, hands down,
the better tool.

There was only one thing missing: Claude, *inside* Visual Studio. Not in a browser tab, not in
another window, right there, next to the code, the solution, the debugger.

Everyone had the same advice: "just use the CLI", "use Claude Desktop", "use the chat in VS Code".
And they're all good. Each of them has something I genuinely like: the raw power of the terminal,
the polish of the desktop app, the rich in-editor chat. But not one of them had *everything*
together, in the one place I actually work: Visual Studio.

Someone was more direct: "There is the CLI, there is VS Code, there is the desktop app: what are
you missing? There is a whole team at Anthropic building these products. What do you think you are
going to do?"

It was a fair question. The answer is that VS Code was not enough for me. The CLI was not enough.
The desktop app was not enough. I work in Visual Studio, and my experience had to be immersive,
not split across several environments.

And I was not alone: I had open source, and I had Claude.

So I stopped waiting for it to exist and rebuilt it, my way: the terminal *and* the rich chat,
both belonging to Visual Studio, wired into its editor, solution, debugger and build. Everything I
liked, under one roof.

And then, watching Claude work, a question kept nagging at me. Claude is astonishingly capable,
yet it navigates code with almost primordial tools: `grep` to find a symbol, a text `Edit` to
rename it. I've done that by hand. It's slow, and it's how you introduce bugs: miss one call
site, rename the wrong match. Meanwhile, in my IDE, *Find All References* and *Rename Symbol* are
native, exact, one keystroke away. So I asked the obvious question: **why not give Claude that same
superpower?**

The same goes for debugging. When something's wrong, I don't guess from reading the source; I set
a breakpoint, step through, evaluate an expression, watch the values change. That's how you *know*
what the code actually does. Why should Claude be blind to it? So I gave it that too.

That's what the MCP tools here are: they hand Visual Studio's own understanding of your code
(navigation, references, rename, diagnostics, and the live debugger with breakpoints, stepping,
locals, evaluate) straight to Claude. Not text search over source, but the IDE's real, semantic,
*running* view of your program.

One more thing kept bothering me, and it wasn't about code at all. Claude fans work out to
sub-agents (several at once, off doing their own thing) and the chat just… sits there. Are they
still running? Have they finished already? What is that one actually doing? And if I've changed my
mind, how do I stop it? I found myself staring at a spinner with no idea whether to wait or give up.
So sub-agents got their own little panel: how many are alive, what each is doing right now, how long
it's been at it, and a Stop button next to every one of them. Not a progress bar: the truth.

Because what I really wanted was simple: a partner as sharp as me 😉 (maybe sharper), working the
IDE the way I do. This is that project. This is the why.

And yes, I built this CLI/chat *for* Claude *with* Claude. There's something funny about that, and
it was a genuinely fun little adventure: watching it sometimes deny things about itself, only to
turn around and build them. That's just part of the LLM game. The code here was written with
Claude's help.

**And it keeps a diary.** Self-hosting since 27 July 2026, it debugged itself, it left the desk, it
changed how I work, and in September it stopped being only mine:
[the days that were not in the plan](/cv4vs-agents/diary/).

*Daniele Corsini (Frank Lupo)*
