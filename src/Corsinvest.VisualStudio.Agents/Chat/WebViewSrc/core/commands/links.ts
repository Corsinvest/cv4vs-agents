/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
// External links used by the Support commands. Single source of truth so the
// repo URL isn't duplicated across command files.
const REPO = 'https://github.com/Corsinvest/cv4vs-agents';

export const LINKS = {
    repo: REPO,
    helpDocs: `${REPO}#readme`,
    // Straight to the bug form: blank issues are disabled, so /issues/new alone lands on the
    // template chooser. The name must match .github/ISSUE_TEMPLATE/bug_report.yml.
    issues: `${REPO}/issues/new?template=bug_report.yml`,
} as const;
