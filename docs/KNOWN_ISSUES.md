# Known Issues & Limitations

Current limitations of the extension. Most stem from Visual Studio shell
constraints, not from bugs in the extension itself.

---

## Restored panes don't keep their exact position or grouping

When **Restore panes on solution open** reopens the panes you had open for a
solution, they come back with their **sessions and profiles** intact and in the
**saved order** — but **not** their exact dock position, size, or tab grouping.
Visual Studio decides where each restored window lands.

**Why:** Visual Studio only honors a requested dock position the *first* time a
tool window is shown, and exposes no reliable API to read back and reapply the
precise layout of transient, multi-instance tool windows. Our panes are
multi-instance and transient (they aren't persisted by VS across sessions, on
purpose — so VS's global layout restore doesn't fight our per-solution one), so
VS has no stored slot to remember their placement.

**Workaround:** the panes are restored in the order you had them; arrange them
once and Visual Studio tends to keep that arrangement for the rest of the
session. Saving pixel-perfect layout per solution is not currently feasible with
the available VS APIs.

---

## A single loose file falls back to the home directory

Solutions, projects and opened folders give the pane a working directory. A
single file opened on its own (**File → Open → File**), with no solution or
folder around it, doesn't — so the pane starts in your home directory instead.

**Why:** the file's own folder isn't a stable choice: it would change every time
you switch tabs, while the working directory is fixed when the pane starts.

**Workaround:** none needed — the home directory is a reasonable fallback for a
loose file. Low priority.
