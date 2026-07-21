# ui/

Visual layer for the WebView. All Lit components, CSS, DOM/HTML helpers.

Layout:

- **components/** — Lit web components (`<cv-icon>`, `<cv-menu>`, …)
- **helpers/**
  - **format.ts** — display formatting (truncate, formatDate)
- **icons.ts** — SVG icon registry (imports from `icons/*.svg`)
- **icons/** — one `.svg` file per icon (loaded by esbuild via
  `loader: { '.svg': 'text' }`)
- **styles/** *(coming next)* — global CSS files reused across components

## Rule

`ui/` may freely import from `core/`. The reverse is forbidden — see
`.eslintrc.json`. The architectural direction is `ui → core`.
