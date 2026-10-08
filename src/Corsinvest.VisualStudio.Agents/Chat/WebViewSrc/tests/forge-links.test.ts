/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
// The GitLab shapes are the ones a real server answered with (GitLab 19.3), names replaced.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { parseForgeLink } from '../core/forge-links.ts';

const label = (url: string): string | undefined => parseForgeLink(url)?.label;

test('GitHub: pull request, issue, commit, release, run', () => {
    const base = 'https://github.com/acme/widgets';
    assert.equal(label(`${base}/pull/312`), 'acme/widgets#312');
    assert.equal(label(`${base}/issues/287`), 'acme/widgets#287');
    assert.equal(
        label(`${base}/commit/5269a595aabbccddeeff00112233445566778899`),
        'acme/widgets@5269a59',
    );
    assert.equal(label(`${base}/releases/tag/v1.4.0`), 'acme/widgets@v1.4.0');
    assert.equal(label(`${base}/actions/runs/37756008986`), 'acme/widgets run 37756008986');
});

test('GitHub: what follows the number does not change the reference', () => {
    const base = 'https://github.com/acme/widgets';
    assert.equal(label(`${base}/pull/312/files`), 'acme/widgets#312');
    assert.equal(label(`${base}/pull/312#issuecomment-99`), 'acme/widgets#312');
    assert.equal(label(`${base}/issues/287?notification=1`), 'acme/widgets#287');
    assert.equal(label(`${base}/actions/runs/1/job/2`), 'acme/widgets run 1');
    assert.equal(label('https://www.github.com/acme/widgets/pull/1/'), 'acme/widgets#1');
});

test('GitHub: the kind and the provider are reported', () => {
    const l = parseForgeLink('https://github.com/acme/widgets/pull/312');
    assert.deepEqual(l, {
        provider: 'github',
        kind: 'pr',
        repo: 'acme/widgets',
        label: 'acme/widgets#312',
        tooltip:
            'GitHub pull request\n' +
            'Server: github.com\n' +
            'Repo: acme/widgets\n' +
            'Number: 312\n' +
            'https://github.com/acme/widgets/pull/312',
    });
});

test('GitLab self-hosted: recognised by the shape of the path, whatever the host', () => {
    const base = 'https://git.example.org/group/project';
    assert.equal(label(`${base}/-/merge_requests/114`), 'group/project!114');
    assert.equal(label(`${base}/-/work_items/1402`), 'group/project#1402');
    assert.equal(label(`${base}/-/issues/45`), 'group/project#45');
    assert.equal(
        label(`${base}/-/commit/fcc9e910f482272ecebf8d135209ff8116664733`),
        'group/project@fcc9e910',
    );
    assert.equal(label(`${base}/-/tags/20230330.01`), 'group/project@20230330.01');
    assert.equal(label(`${base}/-/releases/v2.3.0`), 'group/project@v2.3.0');
    assert.equal(label(`${base}/-/pipelines/494`), 'group/project pipeline 494');
    assert.equal(parseForgeLink(`${base}/-/merge_requests/114`)?.provider, 'gitlab');
});

test('GitLab: a nested group stays in the reference, and gitlab.com is no special case', () => {
    assert.equal(
        label('https://gitlab.com/gitlab-org/sub/extension/-/merge_requests/91'),
        'gitlab-org/sub/extension!91',
    );
    assert.equal(
        label('https://gitlab.com/gitlab-org/gitlab/-/merge_requests/91/diffs'),
        'gitlab-org/gitlab!91',
    );
});

test('a tag is shown decoded, and a malformed one as written', () => {
    assert.equal(
        label('https://github.com/acme/widgets/releases/tag/v1.0%2Bbuild'),
        'acme/widgets@v1.0+build',
    );
    assert.equal(label('https://git.example.org/g/p/-/tags/bad%ZZtag'), 'g/p@bad%ZZtag');
});

test('Azure DevOps: cloud and Server, by the shape of the path', () => {
    const cloud = 'https://dev.azure.com/acme/Shop';
    assert.equal(label(`${cloud}/_git/Web/pullrequest/42`), 'Shop/Web!42');
    assert.equal(label(`${cloud}/_workitems/edit/1402`), 'Shop#1402');
    assert.equal(
        label(`${cloud}/_git/Web/commit/fcc9e910f482272ecebf8d135209ff8116664733`),
        'Shop/Web@fcc9e910',
    );
    assert.equal(
        label('https://tfs.example.org/tfs/DefaultCollection/Shop/_git/Web/pullrequest/7'),
        'Shop/Web!7',
    );
    assert.equal(parseForgeLink(`${cloud}/_git/Web/pullrequest/42`)?.provider, 'azure');
});

test('Azure DevOps: a repository named after its project is said once, names are decoded', () => {
    assert.equal(label('https://dev.azure.com/acme/Shop/_git/Shop/pullrequest/42'), 'Shop!42');
    assert.equal(
        label('https://dev.azure.com/acme/Driverless%20Car/_workitems/edit/4'),
        'Driverless Car#4',
    );
});

test('Bitbucket: Cloud by host, Data Center by its projects/KEY/repos path', () => {
    const cloud = 'https://bitbucket.org/acme/widgets';
    assert.equal(label(`${cloud}/pull-requests/12`), 'acme/widgets#12');
    assert.equal(label(`${cloud}/pull-requests/12/diff`), 'acme/widgets#12');
    assert.equal(label(`${cloud}/issues/3/some-title`), 'acme/widgets#3');
    assert.equal(
        label(`${cloud}/commits/5269a595aabbccddeeff00112233445566778899`),
        'acme/widgets@5269a59',
    );
    assert.equal(label(`${cloud}/pipelines/results/88`), 'acme/widgets pipeline 88');
    const dc = 'https://git.example.org/projects/SHOP/repos/web';
    assert.equal(label(`${dc}/pull-requests/12`), 'SHOP/web#12');
    assert.equal(label(`${dc}/pull-requests/12/overview`), 'SHOP/web#12');
    assert.equal(
        label('https://git.example.org/bitbucket/users/jdoe/repos/web/pull-requests/1'),
        'jdoe/web#1',
    );
    assert.equal(parseForgeLink(`${dc}/pull-requests/12`)?.provider, 'bitbucket');
});

test('Gerrit: a change, under a project that may hold slashes', () => {
    assert.equal(
        label('https://review.example.org/c/platform/build/+/12345'),
        'platform/build~12345',
    );
    assert.equal(label('https://review.example.org/c/tools/+/7/3'), 'tools~7');
    assert.equal(parseForgeLink('https://review.example.org/c/tools/+/7')?.provider, 'gerrit');
});

test('the Gitea family: named where the host is known, plain Git on a host of its own', () => {
    assert.equal(label('https://codeberg.org/acme/widgets/pulls/9'), 'acme/widgets#9');
    assert.equal(label('https://codeberg.org/acme/widgets/issues/4'), 'acme/widgets#4');
    assert.equal(label('https://codeberg.org/acme/widgets/releases/tag/v1.0'), 'acme/widgets@v1.0');
    assert.equal(parseForgeLink('https://codeberg.org/acme/widgets/pulls/9')?.provider, 'codeberg');
    assert.equal(parseForgeLink('https://gitee.com/acme/widgets/pulls/9')?.provider, 'gitee');
    assert.equal(parseForgeLink('https://gitea.com/acme/widgets/pulls/9')?.provider, 'gitea');
    const own = parseForgeLink('https://git.example.org/acme/widgets/pulls/9');
    assert.equal(own?.label, 'acme/widgets#9');
    assert.equal(own?.provider, 'git');
    assert.equal(parseForgeLink('https://git.example.org/acme/widgets/issues/4'), null);
});

const tip = (url: string): string[] => (parseForgeLink(url)?.tooltip ?? '').split('\n');

test('tooltip: what it is in the word of the service, the server, the repository, the address last', () => {
    const mr = 'https://git.example.org/group/project/-/merge_requests/114';
    assert.deepEqual(tip(mr), [
        'GitLab merge request',
        'Server: git.example.org',
        'Repo: group/project',
        'Number: 114',
        mr,
    ]);
    assert.equal(
        tip('https://dev.azure.com/acme/Shop/_workitems/edit/1402')[0],
        'Azure DevOps work item',
    );
    assert.equal(tip('https://github.com/acme/widgets/issues/287')[0], 'GitHub issue');
    assert.equal(tip('https://github.com/acme/widgets/actions/runs/7')[0], 'GitHub workflow run');
    assert.equal(tip('https://git.example.org/g/p/-/pipelines/494')[0], 'GitLab pipeline');
    assert.equal(tip('https://review.example.org/c/tools/+/7')[0], 'Gerrit change');
});

test('tooltip: a commit and a tag are named as such, as the label shows them', () => {
    const c = tip(
        'https://github.com/acme/widgets/commit/5269a595aabbccddeeff00112233445566778899',
    );
    assert.equal(c[0], 'GitHub commit');
    assert.equal(c[3], 'Commit: 5269a59');
    const r = tip('https://github.com/acme/widgets/releases/tag/v1.0%2Bbuild');
    assert.equal(r[0], 'GitHub release');
    assert.equal(r[3], 'Tag: v1.0+build');
    assert.equal(tip('https://git.example.org/g/p/-/tags/v2')[0], 'GitLab tag');
    assert.equal(tip('https://git.example.org/g/p/-/releases/v2')[0], 'GitLab release');
});

test('tooltip: a service the path does not name is not named, the address is as it was written', () => {
    const url = 'https://git.example.org/acme/widgets/pulls/9/files?x=1#top';
    const t = tip(url);
    assert.equal(t[0], 'Pull request');
    assert.equal(t[1], 'Server: git.example.org');
    assert.equal(t[4], url);
});

test('left alone: the repository itself, a file, a list', () => {
    for (const url of [
        'https://github.com/acme/widgets',
        'https://github.com/acme/widgets/pulls',
        'https://github.com/acme/widgets/issues',
        'https://github.com/acme/widgets/blob/main/README.md',
        'https://github.com/acme',
        'https://git.example.org/group/project/-/merge_requests',
        'https://git.example.org/group/project/-/blob/main/src/Program.cs',
    ]) {
        assert.equal(parseForgeLink(url), null, url);
    }
});

test('left alone: a GitHub shape on a host that is not GitHub', () => {
    // "/issues/12" is also Redmine, Gitea, Bitbucket: the path does not name the service.
    assert.equal(parseForgeLink('https://example.com/a/b/issues/12'), null);
    assert.equal(parseForgeLink('https://example.com/a/b/pull/3'), null);
});

test('left alone: what is not an http link at all', () => {
    for (const url of ['', 'mailto:a@b.c', 'src/Program.cs:12', 'github.com/acme/widgets/pull/1']) {
        assert.equal(parseForgeLink(url), null, url);
    }
    assert.equal(parseForgeLink(null), null);
    assert.equal(parseForgeLink(undefined), null);
});
