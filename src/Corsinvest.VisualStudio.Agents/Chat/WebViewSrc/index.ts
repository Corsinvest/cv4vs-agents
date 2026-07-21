/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
// Entry point. Imports bundle the components (each registers its custom
// element on import) and the global CSS, then kicks off the UI bootstrap.

import './ui/styles';

// Components — order doesn't matter for registration.
import './ui/components/cv-spinner';
import './ui/components/cv-model-list';
import './ui/components/cv-permission-list';
import './ui/components/cv-permission-selector';
import './ui/components/cv-attach-menu';
import './ui/components/cv-cli-banner';
import './ui/components/cv-prompt';
import './ui/components/cv-message';
import './ui/components/cv-tool-row';
import './ui/components/cv-app';

import { init } from './ui/init';

init();

console.log('[cv4vs] TS+Lit WebView loaded.');
