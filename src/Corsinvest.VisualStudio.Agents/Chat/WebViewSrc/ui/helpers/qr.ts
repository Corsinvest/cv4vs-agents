/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
import { encodeQR } from '@paulmillr/qr';

/**
 * QR code for `text` as an inline SVG string.
 *
 * The library's own `svg` output emits one `<rect>` per module — ~700 elements and 24 KB for a
 * session URL. One `<path>` says the same in a third of the bytes and a single node.
 *
 * Black on white, not theme tokens: a scanner needs the real contrast, and the quiet zone has to
 * be light or a dark theme showing through stops the code being found at all.
 */
export function qrSvg(text: string, border = 2): string {
    const modules = encodeQR(text, 'raw', { border });
    const size = modules.length;
    let d = '';
    for (let y = 0; y < size; y++) {
        for (let x = 0; x < size; x++) {
            if (modules[y][x]) {
                d += `M${x} ${y}h1v1h-1z`;
            }
        }
    }
    return (
        `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 ${size} ${size}" ` +
        `shape-rendering="crispEdges" role="img" aria-label="QR code">` +
        `<rect width="${size}" height="${size}" fill="#fff"/>` +
        `<path d="${d}" fill="#000"/></svg>`
    );
}
