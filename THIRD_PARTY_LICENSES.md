# Third-Party Licenses

This VSIX bundles third-party open-source components. Their licenses and
copyright notices are listed below, grouped by where they ship.

Development-only tooling (esbuild, eslint, TypeScript, VSSDK build tools,
`@types/*`) is not redistributed with the extension and is therefore not
listed here.

---

## WebView UI (bundled into `dist/bundle.js`)

These npm packages are compiled into the chat WebView bundle shipped in the VSIX.

| Package | Version | License |
| --- | --- | --- |
| [lit](https://github.com/lit/lit) | ^3.3.3 | BSD-3-Clause |
| [@fluentui/web-components](https://github.com/microsoft/fluentui) | 3.0.2 | MIT |
| [@fluentui/svg-icons](https://github.com/microsoft/fluentui-system-icons) | ^1.1.331 | MIT |
| [@microsoft/fast-element](https://github.com/microsoft/fast) | ^3.0.0 | MIT |
| [marked](https://github.com/markedjs/marked) | ^18.0.7 | MIT |
| [highlight.js](https://github.com/highlightjs/highlight.js) | ^11.11.1 | BSD-3-Clause |
| [DOMPurify](https://github.com/cure53/DOMPurify) | ^3.4.12 | MPL-2.0 OR Apache-2.0 |
| [diff](https://github.com/kpdecker/jsdiff) | ^9.0.0 | BSD-3-Clause |
| [diff2html](https://github.com/rtfpessoa/diff2html) | ^3.4.55 | MIT |
| [Fuse.js](https://github.com/krisk/Fuse) | ^7.5.0 | Apache-2.0 |

## Extension host (.NET / NuGet)

Redistributable runtime packages referenced by the VS package.

| Package | Version | License |
| --- | --- | --- |
| [Newtonsoft.Json](https://github.com/JamesNK/Newtonsoft.Json) | 13.0.1 | MIT |
| [Microsoft.Web.WebView2](https://developer.microsoft.com/microsoft-edge/webview2/) | 1.0.3912.50 | Microsoft Software License (redistributable) |
| [Community.VisualStudio.Toolkit.17](https://github.com/VsixCommunity/Community.VisualStudio.Toolkit) | 17.0.551 | MIT |
| [Microsoft.VisualStudio.SDK](https://github.com/microsoft/vs-sdk) | 17.0.31902.203 | Microsoft Software License |
| Microsoft.Terminal.Wpf | (ships with Visual Studio) | Microsoft Software License |

The Visual Studio SDK and `Microsoft.Terminal.Wpf` assemblies are provided by
the installed Visual Studio and are **not** bundled in the VSIX; they are listed
for completeness. `claude.exe` (the Claude Code CLI) is likewise never bundled —
the extension drives whatever the user installed.

---

## License texts

### MIT

```
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

### BSD-3-Clause

```
Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are met:

1. Redistributions of source code must retain the above copyright notice, this
   list of conditions and the following disclaimer.
2. Redistributions in binary form must reproduce the above copyright notice,
   this list of conditions and the following disclaimer in the documentation
   and/or other materials provided with the distribution.
3. Neither the name of the copyright holder nor the names of its contributors
   may be used to endorse or promote products derived from this software
   without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND
ANY EXPRESS OR IMPLIED WARRANTIES ARE DISCLAIMED. IN NO EVENT SHALL THE
COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT,
INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES ARISING IN ANY WAY OUT
OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
```

### Apache-2.0

Full text: <https://www.apache.org/licenses/LICENSE-2.0>

### MPL-2.0

Full text: <https://www.mozilla.org/MPL/2.0/>

### Microsoft Software License

Microsoft components (Visual Studio SDK, WebView2, Terminal.Wpf) are governed
by their respective Microsoft license terms. See the package pages linked above.
