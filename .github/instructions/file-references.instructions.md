---
applyTo: "**/*.md"
---

# File references

For the causes of these rules, refer to [file-references.findings.md](file-references.findings.md).

* To refer to a file, write a Markdown link. Start the path in the folder of the file that contains the link.
* To refer to a skill, write a Markdown link to its `SKILL.md` file. Use the name of the skill as the text of the link.
* Do not use `#file:'<path>'`.
* Do not write `@` before a path, except in an import in [CLAUDE.md](../../CLAUDE.md). Do not change an import in `CLAUDE.md` into a link.
* Do not put a link to a `*.instructions.md` file at the end of a line.

Examples in [CLAUDE.md](../../CLAUDE.md):

```markdown
[architecture.md](docs/src/content/docs/architecture.md) describes the architecture of the extension.
To build and run the extension, use the [cv4vs-run](.claude/skills/cv4vs-run/SKILL.md) skill.
```
