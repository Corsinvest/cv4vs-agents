/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
// Side-effect imports: esbuild bundles these into dist/bundle.css, linked
// globally by index.html (no shadow DOM in our components).

import './styles/base.css';
import './styles/chat.css';
import './styles/markdown.css';
import './styles/diff.css';
