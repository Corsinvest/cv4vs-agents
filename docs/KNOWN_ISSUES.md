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

---

## The status bar item depends on Visual Studio's own layout

The plan-usage item in the status bar is placed by finding the bar inside the
main window's WPF tree.

**Why:** Visual Studio's status bar API takes text only. An element with an
icon, bars and a popup has to be inserted into the window itself, and that tree
is Visual Studio's internal layout — free to change between releases.

**Workaround:** if a release moves it, the item simply doesn't appear and the
Output window says `[usage-status] Visual Studio's status bar was not found`
(Options → Debug → Log level `Warn` or higher). Nothing else is affected, and the
same numbers stay under **View → cv4vs Agents → Usage**.
