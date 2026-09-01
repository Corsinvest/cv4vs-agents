# LangMatrix — language coverage probes

Four throwaway projects whose only job is to be *loaded* by Visual Studio, so the `nav_*` MCP tools
can be pointed at a real file in every language the IDE supports and the answer written down.

They are **not built**: the solution gives each one an `ActiveCfg` but no `Build.0`, so MSBuild and
CI skip them while VS still loads them and lets IntelliSense index their contents. That is all the
probes need — the tools ask the language service, not the compiler.

| project | files | language |
|---|---|---|
| `CppLib` | `shapes.h`, `shapes.cpp`, `main.cpp` | C++ (vcxproj, `NativeDesktop` workload) |
| `CsLib` | `Shapes.cs`, `Program.cs` | C# — the known-good control |
| `VbLib` | `Shapes.vb` | VB |
| `WebFiles` | `shapes.ts`, `shapes.js`, `queries.sql`, `MainWindow.xaml` | files carried by a project so they belong to the solution |

Every language declares the same shapes on purpose — an `IShape` interface, a `Rectangle` that
implements it, a `Perimeter` method nothing calls, a `TotalArea` free function, and calls to both.
So each `nav_*` tool has something to find in all of them, and a `supported=false` is about the
language rather than about the file being empty of anything interesting.

## Running the probes

There is nothing to run: the tools live in the extension, so measuring means calling
`mcp__vs__nav_*` against these paths from a Claude session while this solution is open in a VS with
the extension installed. Results go to `docs/internal/lang-support-matrix.md`.

Wait for IntelliSense to finish before believing a negative — C++ in particular answers
`supported=false` while it is still parsing, which is indistinguishable from not being supported at
all.
