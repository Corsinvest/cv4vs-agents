// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 Corsinvest Srl

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { Transcript } from '../core/transcript.ts';
import type { UiAssistantEntry, UiEntry, UiToolEntry, UiUserEntry } from '../core/types';

function userEntry(id: number, text = 't'): UiUserEntry {
    return { kind: 'text', id, role: 'user', text };
}

function toolEntry(id: number, toolUseId: string): UiToolEntry {
    return {
        kind: 'tool',
        id,
        toolUseId,
        data: { id: toolUseId, name: 'Agent', input: {} },
        status: 'pending',
        result: '',
        fullLineCount: 0,
        elapsedSec: 0,
    };
}

/** What cv-app uses to decide two children are the same row. */
const childKey = (e: UiEntry): string => (e.kind === 'tool' ? e.toolUseId : `t${e.id}`);

test('append: nuovo array, entry esistenti mantengono il riferimento', () => {
    const t = new Transcript();
    const a = userEntry(1);
    t.append(a);
    const first = t.entries;

    t.append(userEntry(2));

    assert.notEqual(t.entries, first, 'entries deve essere un array nuovo');
    assert.equal(t.entries[0], a, 'la entry esistente mantiene il riferimento');
    assert.equal(t.entries.length, 2);
});

test('find: ritorna la entry per id, null se assente', () => {
    const t = new Transcript();
    const a = userEntry(7);
    t.append(a);

    assert.equal(t.find(7), a);
    assert.equal(t.find(99), null);
});

test('clear: svuota entries e index', () => {
    const t = new Transcript();
    t.append(userEntry(1));

    t.clear();

    assert.equal(t.entries.length, 0);
    assert.equal(t.find(1), null, 'index deve essere svuotato con le entries');
});

test('update: sostituisce la entry, gli altri rami restano invariati', () => {
    const t = new Transcript();
    const a = userEntry(1, 'a');
    const b = userEntry(2, 'b');
    t.append(a);
    t.append(b);

    const ok = t.update<UiUserEntry>(1, (e) => ({ ...e, text: 'nuovo' }));

    assert.equal(ok, true);
    assert.notEqual(t.entries[0], a, 'la entry aggiornata ha un riferimento nuovo');
    assert.equal((t.entries[0] as UiUserEntry).text, 'nuovo');
    assert.equal(t.entries[1], b, 'i rami non toccati mantengono il riferimento');
});

test('update: id assente ritorna false senza lanciare', () => {
    const t = new Transcript();

    assert.equal(
        t.update(42, (e) => e),
        false,
    );
});

test('appendText: concatena e ricrea solo quella entry', () => {
    const t = new Transcript();
    const a = userEntry(1, 'ab');
    const b = userEntry(2, 'x');
    t.append(a);
    t.append(b);

    assert.equal(t.appendText(1, 'cd'), true);
    assert.equal((t.entries[0] as UiUserEntry).text, 'abcd');
    assert.equal(t.entries[1], b, 'gli altri rami restano invariati');
});

test('appendText: id assente ritorna false', () => {
    const t = new Transcript();

    assert.equal(t.appendText(9, 'x'), false);
});

test('appendChild: parent, children e items nuovi; fratelli invariati', () => {
    const t = new Transcript();
    const parent = toolEntry(1, 'tool-1');
    const sibling = userEntry(2);
    t.append(parent);
    t.append(sibling);

    const ok = t.appendChild('tool-1', userEntry(3), childKey);

    assert.equal(ok, true);
    const p = t.entries[0] as UiToolEntry;
    assert.notEqual(p, parent, 'il parent ha un riferimento nuovo');
    assert.notEqual(p.children, parent.children, 'anche il blocco children');
    assert.equal(p.children?.items.length, 1);
    assert.equal(t.entries[1], sibling, 'il fratello mantiene il riferimento');
});

// If the ring dropped a child by mutating items in place, the parent's children block would stay
// identical by ===: cv-tool-row would not be updated and its ChildParts would point at removed
// nodes.
test('appendChild: il ring che scarta un figlio ricrea comunque il blocco children', () => {
    const t = new Transcript();
    t.append(toolEntry(1, 'tool-1'));
    for (let i = 2; i <= 4; i++) {
        t.appendChild('tool-1', userEntry(i), childKey);
    }
    const before = (t.entries[0] as UiToolEntry).children;

    t.appendChild('tool-1', userEntry(5), childKey);

    const after = (t.entries[0] as UiToolEntry).children;
    assert.notEqual(after, before, 'il blocco children deve avere identita nuova');
    assert.notEqual(after?.items, before?.items, 'e cosi items');
    assert.equal(after?.items.length, 3, 'restano gli ultimi 3');
    assert.equal(after?.hasMore, true);
    assert.deepEqual(
        after?.items.map((e) => e.id),
        [3, 4, 5],
    );
});

test('appendChild: showAll tiene tutto e fa upsert invece di duplicare', () => {
    const t = new Transcript();
    t.append(toolEntry(1, 'tool-1'));
    t.update<UiToolEntry>(1, (e) => ({
        ...e,
        children: { items: [], hasMore: false, showAll: true },
    }));
    for (let i = 2; i <= 5; i++) {
        t.appendChild('tool-1', userEntry(i), childKey);
    }

    // re-emitting the same child: updates in place, does not duplicate
    t.appendChild('tool-1', userEntry(3, 'aggiornato'), childKey);

    const p = t.entries[0] as UiToolEntry;
    assert.equal(p.children?.items.length, 4, 'showAll tiene tutto, upsert non duplica');
    assert.equal((p.children?.items[1] as UiUserEntry).text, 'aggiornato');
});

test('appendChild: parent inesistente ritorna false', () => {
    const t = new Transcript();

    assert.equal(t.appendChild('nope', userEntry(1), childKey), false);
});

test('replaceAll: sostituisce l albero e ricostruisce l index da zero', () => {
    const t = new Transcript();
    t.append(userEntry(1));
    t.append(toolEntry(2, 'tool-2'));
    t.appendChild('tool-2', userEntry(3), childKey);

    t.replaceAll([userEntry(10), toolEntry(11, 'nuovo')]);

    assert.equal(t.entries.length, 2);
    assert.equal(t.find(1), null, 'gli id vecchi non devono sopravvivere');
    assert.equal(t.find(3), null, 'nemmeno quelli annidati');
    assert.equal(t.find(10)?.id, 10);
});

test('replaceAll: indicizza anche i figli annidati gia presenti', () => {
    const parent = toolEntry(1, 'p');
    parent.children = { items: [userEntry(2)], hasMore: false, showAll: false };
    const t = new Transcript();

    t.replaceAll([parent]);

    assert.equal(t.find(2)?.id, 2, 'i figli arrivati da history devono essere indicizzati');
    assert.equal(
        t.update<UiUserEntry>(2, (e) => ({ ...e, text: 'x' })),
        true,
    );
});

test('prepend: mette in testa e mantiene l index coerente', () => {
    const t = new Transcript();
    t.append(userEntry(5));

    t.prepend([userEntry(1), userEntry(2)]);

    assert.deepEqual(
        t.entries.map((e) => e.id),
        [1, 2, 5],
    );
    assert.equal(t.find(1)?.id, 1);
    assert.equal(t.find(5)?.id, 5, 'anche gli id preesistenti restano risolvibili');
});

test('updateMany: aggiorna N entry in un colpo', () => {
    const t = new Transcript();
    t.append(userEntry(1, 'a'));
    t.append(userEntry(2, 'b'));
    const before = t.entries;

    t.updateMany([1, 2], (e) => ('text' in e ? { ...e, text: e.text + '!' } : e));

    assert.notEqual(t.entries, before);
    assert.equal((t.entries[0] as UiUserEntry).text, 'a!');
    assert.equal((t.entries[1] as UiUserEntry).text, 'b!');
});

test('findToolByAgentId: trova la riga Agent che ha lanciato il sub-agent', () => {
    const t = new Transcript();
    const row = toolEntry(1, 'tool-1');
    row.agentId = 'agent-42';
    t.append(row);

    assert.equal(t.findToolByAgentId('agent-42')?.id, 1);
    assert.equal(t.findToolByAgentId('nope'), null);
});

// "Show all" on an Agent still running: the fetch replaces children.items with the whole history,
// but those children never go through appendChild. Without a reindex they stay out of the index,
// the live event arriving right after does not find them and the update hits the wrong branch.
test('update: sostituire children.items indicizza i figli arrivati da fuori', () => {
    const t = new Transcript();
    t.append(toolEntry(1, 'agent'));
    t.appendChild('agent', userEntry(2), childKey);

    // the "Show all" fetch: replaces the 3 kept ones with the full history
    t.update<UiToolEntry>(1, (e) => ({
        ...e,
        children: {
            items: [userEntry(10), userEntry(11), toolEntry(12, 'inner')],
            hasMore: false,
            showAll: true,
        },
    }));

    // the agent is still working: an event arrives for a child of the replaced list
    assert.equal(t.find(11)?.id, 11, 'i figli sostituiti devono essere raggiungibili');
    assert.equal(
        t.update<UiUserEntry>(11, (e) => ({ ...e, text: 'aggiornato' })),
        true,
        'update deve raggiungere un figlio arrivato dalla sostituzione',
    );
    assert.equal(t.findTool('inner')?.id, 12);
    assert.equal(t.find(2), null, 'il figlio rimpiazzato non deve piu risolvere');
});

test('appendChild annidato: l index risolve un figlio di figlio', () => {
    const t = new Transcript();
    t.append(toolEntry(1, 'outer'));
    t.appendChild('outer', toolEntry(2, 'inner'), childKey);
    t.appendChild('inner', userEntry(3, 'profondo'), childKey);

    assert.equal(t.find(3)?.id, 3);
    assert.equal(t.findTool('inner')?.id, 2);
    assert.equal(
        t.update<UiUserEntry>(3, (e) => ({ ...e, text: 'x' })),
        true,
        'update deve raggiungere un figlio di secondo livello',
    );
});

function assistantEntry(id: number, uuid?: string): UiAssistantEntry {
    return { kind: 'text', id, role: 'assistant', text: 'a', uuid };
}

test('removeByUuid: toglie solo le entry nominate', () => {
    const t = new Transcript();
    t.append(assistantEntry(1, 'u1'));
    t.append(assistantEntry(2, 'u2'));
    t.append(assistantEntry(3, 'u3'));

    assert.equal(t.removeByUuid(['u2']), 1);
    assert.deepEqual(
        t.entries.map((e) => e.id),
        [1, 3],
    );
});

test('removeByUuid: uuid sconosciuti sono un no-op, e ripeterlo non cambia nulla', () => {
    const t = new Transcript();
    t.append(assistantEntry(1, 'u1'));
    const before = t.entries;

    assert.equal(t.removeByUuid(['mai-visto']), 0, 'un uuid ignoto non rimuove niente');
    assert.equal(t.entries, before, 'senza rimozioni l array non viene ricreato');

    assert.equal(t.removeByUuid(['u1']), 1);
    assert.equal(t.removeByUuid(['u1']), 0, 'la seconda volta non ha piu niente da togliere');
});

test('removeByUuid: una entry senza uuid non viene mai toccata', () => {
    const t = new Transcript();
    t.append(assistantEntry(1));
    t.append(userEntry(2));

    assert.equal(t.removeByUuid(['u1', 'u2']), 0);
    assert.equal(t.entries.length, 2);
});

test('removeByUuid: rimuovere una entry non scolla le altre dall index', () => {
    const t = new Transcript();
    t.append(assistantEntry(1, 'u1'));
    t.append(toolEntry(2, 'outer'));
    t.appendChild('outer', userEntry(3), childKey);

    assert.equal(t.removeByUuid(['u1']), 1);
    // La rimozione ricostruisce l index: se lo facesse a meta, i figli della entry SOPRAVVISSUTA
    // resterebbero indicizzati sul percorso vecchio e un evento per loro finirebbe altrove.
    assert.equal(t.find(3)?.id, 3, 'il figlio di una entry rimasta deve ancora risolvere');
    assert.equal(t.findTool('outer')?.id, 2);
});

test('removeByUuid: onRemoved riceve anche i figli della entry rimossa', () => {
    const t = new Transcript();
    const seen: number[][] = [];
    t.onRemoved = (ids) => seen.push([...ids]);

    t.append(assistantEntry(1, 'u1'));
    t.append(toolEntry(2, 'outer'));
    t.appendChild('outer', userEntry(3), childKey);

    t.removeByUuid(['u1']);
    assert.deepEqual(seen, [[1]], 'una entry senza figli riporta solo se stessa');

    // Chi ha messo in mappa l id di una riga annidata resta appeso quanto chi ha messo il padre:
    // l evento deve nominare l intero sottoalbero, non la sola radice.
    seen.length = 0;
    const t2 = new Transcript();
    t2.onRemoved = (ids) => seen.push([...ids]);
    t2.append({ ...toolEntry(10, 'root'), uuid: 'ur' } as unknown as UiEntry);
    t2.appendChild('root', toolEntry(11, 'mid'), childKey);
    t2.appendChild('mid', userEntry(12), childKey);

    t2.removeByUuid(['ur']);
    assert.deepEqual(seen, [[10, 11, 12]]);
});

test('removeByUuid: senza rimozioni onRemoved non scatta', () => {
    const t = new Transcript();
    let calls = 0;
    t.onRemoved = () => calls++;
    t.append(assistantEntry(1, 'u1'));

    t.removeByUuid(['altro']);
    assert.equal(calls, 0);
});

test('removeByUuid: raggiunge anche una entry annidata in un sub-agent', () => {
    const t = new Transcript();
    const removed: number[][] = [];
    t.onRemoved = (ids) => removed.push([...ids]);

    t.append(toolEntry(1, 'agent'));
    t.appendChild('agent', assistantEntry(2, 'dentro'), childKey);
    t.appendChild('agent', userEntry(3), childKey);

    assert.equal(t.removeByUuid(['dentro']), 1);
    assert.equal(t.find(2), null, 'la entry annidata deve sparire');
    assert.equal(t.find(3)?.id, 3, 'i fratelli restano');
    assert.equal(t.findTool('agent')?.id, 1, 'il parent resta');
    assert.deepEqual(removed, [[2]]);
});

test('removeByUuid: un ramo senza rimozioni mantiene la sua identita', () => {
    const t = new Transcript();
    t.append(assistantEntry(1, 'u1'));
    t.append(toolEntry(2, 'intatto'));
    t.appendChild('intatto', userEntry(3), childKey);
    const before = t.entries[1];

    t.removeByUuid(['u1']);

    // Se il prune ricreasse anche i rami non toccati, Lit rirenderizzerebbe l intero sottoalbero
    // del sub-agent a ogni retraction.
    assert.equal(t.entries[0], before, 'il ramo non toccato mantiene il riferimento');
});
