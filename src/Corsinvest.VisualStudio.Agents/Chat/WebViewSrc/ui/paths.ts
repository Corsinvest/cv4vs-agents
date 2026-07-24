/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
// UI glue over the pure core/path helpers: binds them to the two globals every
// call-site would otherwise repeat (the working directory and the "Show relative
// paths" option). core/path.ts stays pure — this is where the state is read.

import { displayPath } from '../core/path';
import { state as appState } from '../core/state';

/** displayPath bound to the current workdir and the "Show relative paths" option. */
export function displayPathUi(filePath: string | undefined | null): string {
    return displayPath(filePath, appState.workingDirectory, appState.ui.showRelativePaths);
}
