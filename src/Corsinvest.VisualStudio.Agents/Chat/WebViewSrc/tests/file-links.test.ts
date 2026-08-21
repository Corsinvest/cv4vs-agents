/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
// Il parser dei riferimenti a file: ogni forma che docs/file-links.md promette, e i non-casi che
// la allow-list esiste per bloccare. Scritti contro il comportamento ATTUALE — un test che fallisce
// qui e' o un bug o una promessa della doc che il codice non mantiene, e le due vanno distinte.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { findFileRefs, parseFileRef, firstRefHint } from '../core/file-links.ts';

/** Il primo ref di un testo, o null. Le asserzioni sotto guardano quasi sempre solo quello. */
function first(text: string) {
    return findFileRefs(text)[0] ?? null;
}

// Le sette forme della tabella in docs/file-links.md.

test('forma: nome.ext:riga', () => {
    const r = first('vedi ClientEvents.cs:208 per il resto');
    assert.equal(r?.path, 'ClientEvents.cs');
    assert.deepEqual(r?.lines, [208]);
    assert.equal(r?.match, 'ClientEvents.cs:208');
});

test('forma: percorso relativo', () => {
    const r = first('in Core/Stats/StatsService.cs:45 succede');
    assert.equal(r?.path, 'Core/Stats/StatsService.cs');
    assert.deepEqual(r?.lines, [45]);
});

test('forma: intervallo, ends porta la riga finale', () => {
    const r = first('StatsService.cs:35-48 e il blocco');
    assert.deepEqual(r?.lines, [35]);
    assert.deepEqual(r?.ends, [48]);
});

test('forma: intervallo con en-dash', () => {
    const r = first('StatsService.cs:35–48');
    assert.deepEqual(r?.lines, [35]);
    assert.deepEqual(r?.ends, [48]);
});

test('forma: lista di righe, una per link', () => {
    const r = first('AgentsPackage.cs:124,185,202 tre punti');
    assert.deepEqual(r?.lines, [124, 185, 202]);
    assert.deepEqual(r?.ends, [124, 185, 202]);
});

test('forma: lista i cui elementi sono intervalli', () => {
    const r = first('StatsService.cs:100-120,130');
    assert.deepEqual(r?.lines, [100, 130]);
    assert.deepEqual(r?.ends, [120, 130]);
});

test('forma: parentesi tonde', () => {
    const r = first('in foo.cs(339) qui');
    assert.equal(r?.path, 'foo.cs');
    assert.deepEqual(r?.lines, [339]);
});

test('forma: parentesi quadre con colonna', () => {
    const r = first('bar.ts[45:12] la');
    assert.equal(r?.path, 'bar.ts');
    assert.deepEqual(r?.lines, [45]);
});

test('forma: GitHub #L', () => {
    const r = first('ClaudeInstall.cs#L103 e li');
    assert.deepEqual(r?.lines, [103]);
});

test('forma: GitHub #L con intervallo', () => {
    const r = first('x.ts#L35-L48');
    assert.deepEqual(r?.lines, [35]);
    assert.deepEqual(r?.ends, [48]);
});

test('forma: GitHub # senza L', () => {
    const r = first('bar.ts#45');
    assert.deepEqual(r?.lines, [45]);
});

test('la colonna e parsata e scartata, non diventa la fine di una selezione', () => {
    const r = first('x.ts:47:12 qui');
    assert.deepEqual(r?.lines, [47]);
    assert.deepEqual(r?.ends, [47]);
});

// Piu' riferimenti nello stesso testo.

test('due file in una riga danno due ref distinti', () => {
    const refs = findFileRefs('markdown.ts:57,file-links.ts:130');
    assert.equal(refs.length, 2);
    assert.equal(refs[0].path, 'markdown.ts');
    assert.equal(refs[1].path, 'file-links.ts');
});

test('i ref tornano in ordine e non si sovrappongono', () => {
    const refs = findFileRefs('prima a.cs:1 poi b.ts:2 infine c.py:3');
    assert.deepEqual(
        refs.map((r) => r.path),
        ['a.cs', 'b.ts', 'c.py'],
    );
    for (let i = 1; i < refs.length; i++) {
        assert.ok(refs[i].start >= refs[i - 1].end, 'ref sovrapposti');
    }
});

// I non-casi: cio' che la allow-list delle estensioni esiste per NON linkare.

test('un orario non e un file', () => {
    assert.equal(first('alle 10:30 di sera'), null);
});

test('un host:porta non e un file', () => {
    assert.equal(first('su localhost.net:4040 gira'), null);
});

test('una versione non e un file', () => {
    assert.equal(first('la 2.1.220:5 di ieri'), null);
});

test('un prezzo non e un file', () => {
    assert.equal(first('costa 19.99:2 euro'), null);
});

test('una estensione sconosciuta in prosa resta testo', () => {
    assert.equal(first('apri report.xyzzy:12'), null);
});

test('un nome senza riga ne cartella e troppo rumoroso per linkare', () => {
    // "vedi il README.md" a meta' frase: nessuna struttura, nessun link.
    assert.equal(first('vedi il README.md e basta'), null);
});

test('un nome con cartella linka anche senza riga', () => {
    const r = first('sta in docs/file-links.md quindi');
    assert.equal(r?.path, 'docs/file-links.md');
    assert.deepEqual(r?.lines, []);
});

// Casi limite del percorso.

test('una cartella con il punto nel nome non ruba lancora', () => {
    // "Corsinvest.VisualStudio.Agents" e' una cartella: il ref vero e' l'ultimo segmento.
    const r = first('src/Corsinvest.VisualStudio.Agents/Chat/x.ts:5');
    assert.equal(r?.path, 'src/Corsinvest.VisualStudio.Agents/Chat/x.ts');
    assert.deepEqual(r?.lines, [5]);
});

test('un percorso windows assoluto tiene la lettera di unita', () => {
    const r = first('apri C:\\src\\repo\\Foo.cs:12 adesso');
    assert.equal(r?.path, 'C:\\src\\repo\\Foo.cs');
    assert.deepEqual(r?.lines, [12]);
});

test('una estensione sola senza nome non e un ref', () => {
    assert.equal(first('un file .ts qualunque'), null);
});

test('le parentesi attorno al ref non ci finiscono dentro', () => {
    const r = first('(NdjsonTransport.cs:91)');
    assert.equal(r?.path, 'NdjsonTransport.cs');
    assert.equal(r?.match, 'NdjsonTransport.cs:91');
});

test('righe duplicate nella lista sono scartate, la prima vince', () => {
    const r = first('x.cs:10,10,20');
    assert.deepEqual(r?.lines, [10, 20]);
});

test('testo vuoto non produce ref', () => {
    assert.deepEqual(findFileRefs(''), []);
});

// parseFileRef: il token deve essere il ref e NIENTE altro. E' il test che serve anche
// all'inline code, dove il contenuto del backtick e' o un riferimento o un frammento di codice.

test('parseFileRef accetta un token che e interamente un ref', () => {
    const r = parseFileRef('Foo.cs:12', 'known-ext');
    assert.equal(r?.path, 'Foo.cs');
    assert.deepEqual(r?.lines, [12]);
});

test('parseFileRef rifiuta un token che contiene altro', () => {
    assert.equal(parseFileRef('cat Foo.cs:12', 'known-ext'), null);
    assert.equal(parseFileRef('Foo.cs:12 e poi', 'known-ext'), null);
});

test('parseFileRef rifiuta una versione', () => {
    assert.equal(parseFileRef('1.5.0', 'known-ext'), null);
    assert.equal(parseFileRef('v2.1.237', 'known-ext'), null);
});

// plausible-path: la strictness degli href markdown, dove il modello ha gia' dichiarato "e' un link".

test('plausible-path accetta una estensione fuori allow-list', () => {
    const r = parseFileRef('src/utils/helper.rb#L12', 'plausible-path');
    assert.equal(r?.path, 'src/utils/helper.rb');
    assert.deepEqual(r?.lines, [12]);
});

test('plausible-path rifiuta comunque cio che non ha forma di file', () => {
    // Una "estensione" tutta cifre e' un prezzo o una versione, mai un file.
    assert.equal(parseFileRef('19.99#L2', 'plausible-path'), null);
});

test('plausible-path accetta un percorso senza riga', () => {
    const r = parseFileRef('notes.md', 'plausible-path');
    assert.equal(r?.path, 'notes.md');
    assert.deepEqual(r?.lines, []);
});

// firstRefHint: il contratto con marked. Deve essere un limite INFERIORE — mai oltre l'inizio vero,
// o il tokenizer non viene mai offerto quella posizione e il ref sparisce in silenzio.

test('il suggerimento non supera mai linizio reale del ref', () => {
    for (const t of [
        'vedi ClientEvents.cs:208 qui',
        'apri C:\\src\\Foo.cs:12 adesso',
        'in Core/Stats/StatsService.cs:45',
        '(NdjsonTransport.cs:91)',
    ]) {
        const hint = firstRefHint(t);
        const ref = first(t);
        assert.ok(ref, `nessun ref in "${t}"`);
        assert.ok(hint !== undefined, `nessun hint per "${t}"`);
        assert.ok(hint <= ref.start, `hint ${hint} oltre lo start ${ref.start} in "${t}"`);
    }
});

test('nessun suggerimento quando non ci sono estensioni', () => {
    assert.equal(firstRefHint('nessun file qui dentro'), undefined);
});
