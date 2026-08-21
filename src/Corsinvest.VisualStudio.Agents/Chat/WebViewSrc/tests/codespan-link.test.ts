/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
// Cosa fa un `code span` inline: linka quando il contenuto E' un riferimento, resta testo quando
// contiene anche altro. La regola vale o non vale sul contenuto intero — non ci sono link parziali.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { renderCodespanInner as render } from '../core/codespan-link.ts';
import { parseFileRef } from '../core/file-links.ts';
import { escapeHtml } from '../core/html.ts';

// Le dipendenze vere, non finte: la regola vale solo se combacia con il parser che gira davvero.
const renderCodespanInner = (text: string) => render(text, { parseFileRef, escapeHtml });

/** Quanti link a file nel frammento reso. */
function links(html: string): number {
    return (html.match(/class="cv-file-link"/g) ?? []).length;
}

function fileOf(html: string): string | null {
    return html.match(/data-file="([^"]*)"/)?.[1] ?? null;
}

// Linka: il contenuto e' il riferimento e nient'altro.

test('un riferimento con riga diventa link', () => {
    const html = renderCodespanInner('ClientEvents.cs:208');
    assert.equal(links(html), 1);
    assert.equal(fileOf(html), 'ClientEvents.cs');
    assert.match(html, /data-line="208"/);
});

test('la label tiene quello che era scritto', () => {
    assert.match(renderCodespanInner('ClientEvents.cs:208'), />ClientEvents\.cs:208</);
});

test('un percorso relativo linka', () => {
    const html = renderCodespanInner('Core/Stats/StatsService.cs:45');
    assert.equal(fileOf(html), 'Core/Stats/StatsService.cs');
});

test('un intervallo porta la riga finale nel data-line-end', () => {
    const html = renderCodespanInner('StatsService.cs:35-48');
    assert.match(html, /data-line="35" data-line-end="48"/);
    assert.match(html, />StatsService\.cs:35-48</, 'la label non va normalizzata alla prima riga');
});

test('una lista di righe da un link per riga, il primo col nome del file', () => {
    const html = renderCodespanInner('AgentsPackage.cs:124,185,202');
    assert.equal(links(html), 3);
    assert.match(html, />AgentsPackage\.cs:124</);
    assert.match(html, />185</);
    assert.match(html, />202</);
});

test('la forma GitHub linka e tiene la sua scrittura', () => {
    const html = renderCodespanInner('ClaudeInstall.cs#L103');
    assert.equal(links(html), 1);
    assert.match(html, /data-line="103"/);
    assert.match(html, />ClaudeInstall\.cs#L103</);
});

test('le parentesi tonde linkano', () => {
    const html = renderCodespanInner('foo.cs(339)');
    assert.equal(links(html), 1);
    assert.match(html, /data-line="339"/);
});

test('un percorso con cartella linka anche senza riga, aperto in cima', () => {
    const html = renderCodespanInner('docs/file-links.md');
    assert.equal(links(html), 1);
    assert.match(html, /data-line="0"/);
});

// Non linka: c'e' dell'altro oltre al riferimento, o non e' un riferimento.

test('un comando che contiene un riferimento resta testo', () => {
    assert.equal(links(renderCodespanInner('cat Foo.cs:12')), 0);
});

test('un riferimento seguito da altro resta testo', () => {
    assert.equal(links(renderCodespanInner('Foo.cs:12 e poi')), 0);
});

test('due riferimenti in uno span restano testo', () => {
    // Ambiguo: lo span e' una citazione doppia o un frammento? Non si indovina.
    assert.equal(links(renderCodespanInner('a.cs:1 b.ts:2')), 0);
});

test('una versione non e un file', () => {
    assert.equal(links(renderCodespanInner('1.5.0')), 0);
    assert.equal(links(renderCodespanInner('v2.1.237')), 0);
});

test('un orario, un host e un prezzo non sono file', () => {
    assert.equal(links(renderCodespanInner('10:30')), 0);
    assert.equal(links(renderCodespanInner('localhost.net:4040')), 0);
    assert.equal(links(renderCodespanInner('19.99:2')), 0);
});

test('un identificatore di codice qualunque resta testo', () => {
    assert.equal(links(renderCodespanInner('appState.ideContext')), 0);
    assert.equal(links(renderCodespanInner('const x = 1')), 0);
});

test('un nome nudo senza riga ne cartella resta testo', () => {
    // Stessa soglia della prosa: "README.md" da solo e' troppo rumoroso per linkare.
    assert.equal(links(renderCodespanInner('README.md')), 0);
});

test('testo vuoto non lancia', () => {
    assert.equal(renderCodespanInner(''), '');
});

// L'HTML prodotto deve restare sicuro: il contenuto viene dal modello.

test('il testo non linkato e escapato', () => {
    const html = renderCodespanInner('<script>alert(1)</script>');
    assert.doesNotMatch(html, /<script/);
    assert.match(html, /&lt;script&gt;/);
});

test('il percorso finisce escapato dentro lattributo', () => {
    // Un nome con virgolette romperebbe data-file="..." se non fosse escapato.
    const html = renderCodespanInner('a"b.cs:1');
    assert.doesNotMatch(html, /data-file="a"b/);
});
