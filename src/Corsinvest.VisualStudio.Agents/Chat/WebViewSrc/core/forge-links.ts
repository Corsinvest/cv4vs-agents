/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
// The short form of a link to a pull request, an issue, a commit, a release or a CI run.
//
// No two services share a URL shape or a notation (GitHub writes "#312" for a pull request,
// GitLab "!312"), so this is a table, one row per shape. A cloud service is known by its host; a
// self-hosted one by a path nothing else has ("/-/" for GitLab, "/_git/" for Azure DevOps). A
// shape that does not name its service ("/issues/12" alone) is left as it is: a wrong label on a
// link is worse than a long one.
//
// Checked against real URLs: GitHub and GitLab. From a published source: Azure DevOps work
// items, Bitbucket, Gerrit. From memory: an Azure DevOps pull request page, Gitea, Forgejo, Gitee.

export type ForgeProvider =
    | 'github'
    | 'gitlab'
    | 'azure'
    | 'bitbucket'
    | 'gitea'
    | 'codeberg'
    | 'gitee'
    | 'gerrit'
    /** The Gitea family on a host of its own: the path says "one of them", not which. */
    | 'git';
export type ForgeKind = 'pr' | 'issue' | 'commit' | 'release' | 'run';

export interface ForgeLink {
    provider: ForgeProvider;
    kind: ForgeKind;
    repo: string;
    /** Plain text: escape it on output. */
    label: string;
    /** What the link is, a line per fact, the address last. Plain text too. */
    tooltip: string;
}

type Groups = Record<string, string>;

interface ForgeRule {
    provider: ForgeProvider;
    kind: ForgeKind;
    /** Absent means any host: the path alone names the service. */
    host?: RegExp;
    /** Against the path without leading slash, query and hash. Groups: id, and repo unless
     *  `repo` builds it from others. */
    path: RegExp;
    repo?: (g: Groups) => string;
    /** What follows the repository in the label, separator included. */
    ref: (id: string) => string;
    /** When the service's word for it is not the one NOUN gives for the kind. */
    noun?: string;
}

const SERVICE: Record<ForgeProvider, string> = {
    github: 'GitHub',
    gitlab: 'GitLab',
    azure: 'Azure DevOps',
    bitbucket: 'Bitbucket',
    gitea: 'Gitea',
    codeberg: 'Codeberg',
    gitee: 'Gitee',
    gerrit: 'Gerrit',
    git: '',
};

// Each service's own word, like its notation: a merge request on GitLab, a work item on Azure.
const NOUN: Record<ForgeKind, string> = {
    pr: 'pull request',
    issue: 'issue',
    commit: 'commit',
    release: 'release',
    run: 'run',
};
const NOUN_OF: Partial<Record<ForgeProvider, Partial<Record<ForgeKind, string>>>> = {
    github: { run: 'workflow run' },
    gitlab: { pr: 'merge request', run: 'pipeline' },
    azure: { issue: 'work item' },
    bitbucket: { run: 'pipeline' },
    gerrit: { pr: 'change' },
};

const GITHUB = /^(?:www\.)?github\.com$/i;
const BITBUCKET = /^bitbucket\.org$/i;
const SHA = '[0-9a-f]{7,40}';
const OWNER_REPO = '(?<repo>[^/]+/[^/]+)';

/** A malformed escape stays as written. */
function decode(s: string): string {
    try {
        return decodeURIComponent(s);
    } catch {
        return s;
    }
}

// A repository is often named after its project: "Shop/Shop!12" would say it twice.
function azureRepo(g: Groups): string {
    const project = decode(g.project);
    const name = decode(g.name);
    return project === name ? name : `${project}/${name}`;
}

/** Gitea, Forgejo and Gitee share one shape. */
function giteaFamily(provider: ForgeProvider, host?: RegExp): ForgeRule[] {
    const rules: ForgeRule[] = [
        {
            provider,
            kind: 'pr',
            host,
            path: new RegExp(`^${OWNER_REPO}/pulls/(?<id>\\d+)(?:/.*)?$`),
            ref: (id) => `#${id}`,
        },
    ];
    // Without a known host only "/pulls/12" is this family's alone.
    if (host) {
        rules.push(
            {
                provider,
                kind: 'issue',
                host,
                path: new RegExp(`^${OWNER_REPO}/issues/(?<id>\\d+)$`),
                ref: (id) => `#${id}`,
            },
            {
                provider,
                kind: 'commit',
                host,
                path: new RegExp(`^${OWNER_REPO}/commit/(?<id>${SHA})$`, 'i'),
                ref: (id) => `@${id.slice(0, 7)}`,
            },
            {
                provider,
                kind: 'release',
                host,
                path: new RegExp(`^${OWNER_REPO}/releases/tag/(?<id>[^/]+)$`),
                ref: (id) => `@${decode(id)}`,
            },
        );
    }
    return rules;
}

// The first row that matches wins.
const RULES: readonly ForgeRule[] = [
    {
        provider: 'github',
        kind: 'pr',
        host: GITHUB,
        path: new RegExp(`^${OWNER_REPO}/pull/(?<id>\\d+)(?:/.*)?$`),
        ref: (id) => `#${id}`,
    },
    {
        provider: 'github',
        kind: 'issue',
        host: GITHUB,
        path: new RegExp(`^${OWNER_REPO}/issues/(?<id>\\d+)$`),
        ref: (id) => `#${id}`,
    },
    {
        provider: 'github',
        kind: 'commit',
        host: GITHUB,
        path: new RegExp(`^${OWNER_REPO}/commit/(?<id>${SHA})$`, 'i'),
        ref: (id) => `@${id.slice(0, 7)}`,
    },
    {
        provider: 'github',
        kind: 'release',
        host: GITHUB,
        path: new RegExp(`^${OWNER_REPO}/releases/tag/(?<id>[^/]+)$`),
        ref: (id) => `@${decode(id)}`,
    },
    {
        provider: 'github',
        kind: 'run',
        host: GITHUB,
        path: new RegExp(`^${OWNER_REPO}/actions/runs/(?<id>\\d+)(?:/.*)?$`),
        ref: (id) => ` run ${id}`,
    },
    // GitLab: the project can be nested (group/subgroup/project), so it is whatever precedes "/-/".
    {
        provider: 'gitlab',
        kind: 'pr',
        path: /^(?<repo>.+?)\/-\/merge_requests\/(?<id>\d+)(?:\/.*)?$/,
        ref: (id) => `!${id}`,
    },
    {
        provider: 'gitlab',
        kind: 'issue',
        path: /^(?<repo>.+?)\/-\/(?:work_items|issues)\/(?<id>\d+)$/,
        ref: (id) => `#${id}`,
    },
    {
        provider: 'gitlab',
        kind: 'commit',
        path: new RegExp(`^(?<repo>.+?)/-/commit/(?<id>${SHA})$`, 'i'),
        ref: (id) => `@${id.slice(0, 8)}`,
    },
    {
        provider: 'gitlab',
        kind: 'release',
        path: /^(?<repo>.+?)\/-\/releases\/(?<id>[^/]+)$/,
        ref: (id) => `@${decode(id)}`,
    },
    {
        provider: 'gitlab',
        kind: 'release',
        path: /^(?<repo>.+?)\/-\/tags\/(?<id>[^/]+)$/,
        ref: (id) => `@${decode(id)}`,
        noun: 'tag',
    },
    {
        provider: 'gitlab',
        kind: 'run',
        path: /^(?<repo>.+?)\/-\/pipelines\/(?<id>\d+)$/,
        ref: (id) => ` pipeline ${id}`,
    },
    // Azure DevOps, cloud (org/project/…) or Server (tfs/collection/project/…): the project is the
    // segment before "/_git/" or "/_workitems/".
    {
        provider: 'azure',
        kind: 'pr',
        path: /^(?:.*\/)?(?<project>[^/]+)\/_git\/(?<name>[^/]+)\/pullrequest\/(?<id>\d+)(?:\/.*)?$/i,
        repo: azureRepo,
        ref: (id) => `!${id}`,
    },
    {
        provider: 'azure',
        kind: 'commit',
        path: new RegExp(
            `^(?:.*/)?(?<project>[^/]+)/_git/(?<name>[^/]+)/commit/(?<id>${SHA})$`,
            'i',
        ),
        repo: azureRepo,
        ref: (id) => `@${id.slice(0, 8)}`,
    },
    {
        provider: 'azure',
        kind: 'issue',
        path: /^(?:.*\/)?(?<project>[^/]+)\/_workitems\/edit\/(?<id>\d+)$/i,
        repo: (g) => decode(g.project),
        ref: (id) => `#${id}`,
    },
    // Bitbucket Data Center: a project KEY, or a user for a personal repository.
    {
        provider: 'bitbucket',
        kind: 'pr',
        path: /^(?:.*\/)?(?:projects|users)\/(?<key>[^/]+)\/repos\/(?<name>[^/]+)\/pull-requests\/(?<id>\d+)(?:\/.*)?$/,
        repo: (g) => `${g.key}/${g.name}`,
        ref: (id) => `#${id}`,
    },
    {
        provider: 'bitbucket',
        kind: 'commit',
        path: new RegExp(
            `^(?:.*/)?(?:projects|users)/(?<key>[^/]+)/repos/(?<name>[^/]+)/commits/(?<id>${SHA})$`,
            'i',
        ),
        repo: (g) => `${g.key}/${g.name}`,
        ref: (id) => `@${id.slice(0, 7)}`,
    },
    // Bitbucket Cloud.
    {
        provider: 'bitbucket',
        kind: 'pr',
        host: BITBUCKET,
        path: new RegExp(`^${OWNER_REPO}/pull-requests/(?<id>\\d+)(?:/.*)?$`),
        ref: (id) => `#${id}`,
    },
    {
        provider: 'bitbucket',
        kind: 'issue',
        host: BITBUCKET,
        path: new RegExp(`^${OWNER_REPO}/issues/(?<id>\\d+)(?:/.*)?$`),
        ref: (id) => `#${id}`,
    },
    {
        provider: 'bitbucket',
        kind: 'commit',
        host: BITBUCKET,
        path: new RegExp(`^${OWNER_REPO}/commits/(?<id>${SHA})$`, 'i'),
        ref: (id) => `@${id.slice(0, 7)}`,
    },
    {
        provider: 'bitbucket',
        kind: 'run',
        host: BITBUCKET,
        path: new RegExp(`^${OWNER_REPO}/pipelines/results/(?<id>\\d+)(?:/.*)?$`),
        ref: (id) => ` pipeline ${id}`,
    },
    // Gerrit names a change "project~12345".
    {
        provider: 'gerrit',
        kind: 'pr',
        path: /^(?:.*\/)?c\/(?<repo>.+?)\/\+\/(?<id>\d+)(?:\/.*)?$/,
        ref: (id) => `~${id}`,
    },
    ...giteaFamily('codeberg', /^codeberg\.org$/i),
    ...giteaFamily('gitee', /^gitee\.com$/i),
    ...giteaFamily('gitea', /^gitea\.com$/i),
    ...giteaFamily('git'),
];

// Not `new URL`: it would percent-encode and punycode what is shown back as written.
const URL_PARTS = /^https?:\/\/([^/?#]+)\/([^?#]*)/i;

export function parseForgeLink(url: string | undefined | null): ForgeLink | null {
    const m = URL_PARTS.exec(url ?? '');
    if (!m) {
        return null;
    }
    const host = m[1];
    const path = m[2].replace(/\/+$/, '');
    for (const rule of RULES) {
        if (rule.host && !rule.host.test(host)) {
            continue;
        }
        const g = rule.path.exec(path)?.groups;
        if (!g) {
            continue;
        }
        const repo = rule.repo ? rule.repo(g) : g.repo;
        const ref = rule.ref(g.id);
        const noun = rule.noun ?? NOUN_OF[rule.provider]?.[rule.kind] ?? NOUN[rule.kind];
        const what = `${SERVICE[rule.provider]} ${noun}`.trim();
        // The host on a line of its own: it is what to check before a click, and in a long
        // address it is the part the eye skips.
        const tooltip = [
            what[0].toUpperCase() + what.slice(1),
            `Server: ${host}`,
            `Repo: ${repo}`,
            rule.kind === 'commit'
                ? `Commit: ${ref.slice(1)}`
                : rule.kind === 'release'
                  ? `Tag: ${ref.slice(1)}`
                  : `Number: ${g.id}`,
            url,
        ].join('\n');
        return {
            provider: rule.provider,
            kind: rule.kind,
            repo,
            label: repo + ref,
            tooltip,
        };
    }
    return null;
}
