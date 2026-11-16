/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

// Kind names from NavigateTo (NavigateToItemKind) and from the navigation bar's glyphs, lower-cased,
// to the icon key the host maps to a Visual Studio image (IconCacheService.SymbolMoniker). Keep the
// two in step: a key the host does not know renders as the generic symbol.
const KEYS = new Map<string, string>(
    Object.entries({
        class: 'symbol-class',
        record: 'symbol-class',
        structure: 'symbol-struct',
        struct: 'symbol-struct',
        interface: 'symbol-interface',
        enum: 'symbol-enum',
        enummember: 'symbol-enummember',
        enumitem: 'symbol-enummember',
        method: 'symbol-method',
        constructor: 'symbol-method',
        operator: 'symbol-operator',
        property: 'symbol-property',
        field: 'symbol-field',
        constant: 'symbol-constant',
        event: 'symbol-event',
        delegate: 'symbol-delegate',
        module: 'symbol-module',
        namespace: 'symbol-namespace',
    }),
);

/** The icon key for a symbol kind; `symbol-other` when the kind is missing or not one we map. */
export function symbolIconKey(kind: string | null | undefined): string {
    return (kind && KEYS.get(kind.toLowerCase())) || 'symbol-other';
}
