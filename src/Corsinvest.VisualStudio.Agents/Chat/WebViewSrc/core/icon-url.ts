/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
// File-type icon URL helper.
// Icons live on a virtual host; the C# side generates PNGs lazily from VS KnownMonikers and
// caches them on disk.
//
// Must match AppPaths.IconHost, which is where the suffix is explained — a copy rather than a
// value we are told, because URLs are built on the first render, before any message from the
// host could have arrived. Drift shows up as icons missing on the next run.

const ICON_HOST = 'https://cv4vs-icons.invalid';

/**
 * URL of the icon that best represents the given path.
 *
 * - Folders → `folder.png`
 * - Dotfiles like `.editorconfig` → keyed by the bare name
 * - Regular files → keyed by the lowercased extension
 * - Files without extension → fall back to `file.png`
 */
export function iconUrl(path: string | undefined | null, isDir = false): string {
    if (isDir) {
        return `${ICON_HOST}/folder.png`;
    }
    const baseName = (path ?? '').split(/[\\/]/).pop() ?? '';
    const dot = baseName.lastIndexOf('.');
    let key: string;
    if (dot < 0) {
        key = 'file';
    } else if (dot === 0) {
        key = baseName.slice(1).toLowerCase();
    } else {
        key = baseName.slice(dot + 1).toLowerCase();
    }
    return `${ICON_HOST}/${key || 'file'}.png`;
}
