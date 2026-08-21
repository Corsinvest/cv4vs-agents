/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
// Che marked chiami i renderer giusti: la meta' che i test su funzioni pure non possono vedere.
// I casi limite di COSA sia un riferimento stanno in codespan-link.test.ts e file-links.test.ts.
//
// LIMITE, da sapere prima di aggiungere test qui: renderMarkdown chiude con DOMPurify, che ha
// bisogno di un `window` e sotto node --test non ce l'ha — `sanitize is not a function`, e la
// funzione degrada al suo ramo di errore. Quindi qui si testa il TOKENIZER, cioe' quali token
// marked produce e con quale renderer, non l'HTML finale. Per quello servirebbe jsdom (~10MB di
// devDependency) per verificare una sanificazione che e' di DOMPurify, non nostra.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { marked } from 'marked';
import { closeOpenMarkdown } from '../core/markdown.ts'; // importa anche renderer ed estensione

/** L'HTML di marked, prima di DOMPurify. */
function md(text: string): string {
    return marked.parse(text, { async: false }) as string;
}

function links(html: string): number {
    return (html.match(/class="cv-file-link"/g) ?? []).length;
}

// I tre percorsi che producono un link, uno per renderer.

test('prosa: lestensione fileLink intercetta un riferimento nudo', () => {
    const html = md('vedi ClientEvents.cs:208 per il resto');
    assert.equal(links(html), 1);
    assert.match(html, /data-file="ClientEvents\.cs"/);
});

test('inline code: il renderer codespan intercetta un riferimento in backtick', () => {
    // La forma che il modello usa quasi sempre: 33 volte contro 2 su 25 sessioni reali.
    const html = md('vedi `ClientEvents.cs:208` per il resto');
    assert.equal(links(html), 1);
    assert.match(html, /<code>/, 'il link vive DENTRO il code span, non al suo posto');
});

test('link markdown: il renderer link usa lhref e tiene la label', () => {
    const html = md('[McpServerHost.cs:192](src/Mcp/McpServerHost.cs:192)');
    assert.equal(links(html), 1);
    assert.match(html, /data-file="src\/Mcp\/McpServerHost\.cs"/);
    assert.match(html, />McpServerHost\.cs:192</);
});

// Il fence: la regola che NON deve cadere insieme a quella del code span.

test('fence: un riferimento in un blocco di codice resta testo', () => {
    assert.equal(links(md('```\ncat ClientEvents.cs:208\n```')), 0);
});

test('fence: nemmeno con un linguaggio dichiarato', () => {
    assert.equal(links(md('```bash\nvim Foo.cs:12\n```')), 0);
});

test('fence: il pulsante Copia resta, e legge dal pre', () => {
    // Un link dentro il blocco romperebbe la copia: cv-copy-btn prende il testo dal <pre> fratello.
    const html = md('```\nFoo.cs:12\n```');
    assert.match(html, /<cv-copy-btn[^>]*frompre="1"/);
});

// Gli attributi che il host legge al click. Non passano da DOMPurify qui, ma la ADD_ATTR di
// markdown.ts deve elencarli tutti: data-line-end mancava mentre il renderer lo emetteva da sempre,
// e l'intervallo apriva senza selezionare.

test('un intervallo emette data-line-end, in prosa e in backtick', () => {
    for (const src of ['guarda StatsService.cs:35-48', 'guarda `StatsService.cs:35-48`']) {
        const html = md(src);
        assert.match(html, /data-line="35"/, src);
        assert.match(html, /data-line-end="48"/, src);
    }
});

test('una lista di righe diventa un link per riga, in entrambe le forme', () => {
    assert.equal(links(md('AgentsPackage.cs:124,185,202 tre punti')), 3);
    assert.equal(links(md('`AgentsPackage.cs:124,185,202`')), 3);
});

test('un href http resta un link esterno, non un file', () => {
    const html = md('[doc](https://example.com/a.cs:12)');
    assert.equal(links(html), 0);
    assert.match(html, /href="https:\/\/example\.com/);
});

test('un code span che non e un riferimento resta un code span', () => {
    const html = md('esegui `npm run build` adesso');
    assert.equal(links(html), 0);
    assert.match(html, /<code>npm run build<\/code>/);
});

// La ADD_ATTR di DOMPurify. Non possiamo eseguire sanitize qui (serve un window), ma il modo in cui
// quel bug si manifesta e' un attributo emesso dai renderer e non elencato nella allow-list: il
// confronto fra i due insiemi lo prende senza bisogno di un DOM.

test('ogni attributo emesso dai renderer e nella ADD_ATTR di DOMPurify', () => {
    const src = new URL('../core/markdown.ts', import.meta.url);
    const allowed = new Set(
        (readFileSync(src, 'utf8').match(/ADD_ATTR:\s*\[([^\]]*)\]/)?.[1] ?? '')
            .split(',')
            .map((s) => s.trim().replace(/^['"]|['"]$/g, ''))
            .filter(Boolean),
    );
    // Sempre permessi da DOMPurify, non serve dichiararli.
    const builtin = new Set(['class', 'href', 'title', 'rel', 'src', 'alt', 'id', 'style']);

    // Un markdown che passa da TUTTI i renderer che emettono attributi nostri.
    const html = md(
        'prosa Foo.cs:12-20 e `Bar.ts:34` e [x](src/Baz.cs:5)\n\n```ts\nconst a = 1;\n```',
    );
    const emitted = new Set([...html.matchAll(/\s([a-z-]+)="/g)].map((m) => m[1]));
    const missing = [...emitted].filter((a) => !allowed.has(a) && !builtin.has(a));
    assert.deepEqual(missing, [], `attributi che DOMPurify butterebbe via: ${missing.join(', ')}`);
});

// closeOpenMarkdown: il percorso dello streaming, che gira a OGNI token. Chiude i costrutti aperti
// perche' un chunk parziale renda in modo stabile invece di ribaltare il layout a meta' risposta.

test('streaming: una fence aperta viene chiusa', () => {
    assert.equal(
        closeOpenMarkdown('testo\n```ts\nconst a = 1;'),
        'testo\n```ts\nconst a = 1;\n```',
    );
});

test('streaming: una fence gia chiusa non viene toccata', () => {
    const t = 'testo\n```ts\nconst a = 1;\n```\ncoda';
    assert.equal(closeOpenMarkdown(t), t);
});

test('streaming: un backtick spaiato sullultima riga viene chiuso', () => {
    assert.equal(closeOpenMarkdown('vedi `Foo.cs:12'), 'vedi `Foo.cs:12`');
});

test('streaming: un backtick pari resta com e', () => {
    const t = 'vedi `Foo.cs:12` qui';
    assert.equal(closeOpenMarkdown(t), t);
});

test('streaming: i backtick dentro una fence aperta sono letterali', () => {
    // Dentro un blocco i backtick non sono inline code: chiudere la fence basta, aggiungere anche
    // un backtick spaiato romperebbe il blocco.
    assert.equal(closeOpenMarkdown('```\nusa `x`'), '```\nusa `x`\n```');
});

test('streaming: un backtick spaiato su una riga PRECEDENTE non viene toccato', () => {
    // Solo l'ultima riga e' in costruzione; una precedente e' gia' come il modello l'ha scritta.
    const t = 'riga con ` spaiato\nriga finita';
    assert.equal(closeOpenMarkdown(t), t);
});

test('streaming: testo vuoto resta vuoto', () => {
    assert.equal(closeOpenMarkdown(''), '');
});

test('streaming: un riferimento a meta non linka, completo si', () => {
    // Il caso che conta a ogni token: il testo cresce sotto il render. Il nome senza riga non
    // qualifica (troppo rumoroso), quindi il link compare quando il ref e' davvero finito — e non
    // lampeggia mentre i numeri arrivano una cifra per volta.
    assert.equal(links(md(closeOpenMarkdown('vedi `Foo.cs'))), 0, 'senza riga: nessun link');
    assert.equal(links(md(closeOpenMarkdown('vedi `Foo.cs:12`'))), 1, 'completo: link');
});

test('streaming: le cifre che arrivano una per volta spostano solo la riga, non il link', () => {
    // "Foo.cs:1" e' gia' un riferimento valido (riga 1), quindi il link c'e' da subito e la riga si
    // aggiorna man mano. Meglio cosi' che un link che appare e sparisce: la bolla non si muove.
    for (const [src, line] of [
        ['vedi `Foo.cs:1`', '1'],
        ['vedi `Foo.cs:12`', '12'],
        ['vedi `Foo.cs:123`', '123'],
    ] as const) {
        const html = md(closeOpenMarkdown(src));
        assert.equal(links(html), 1, src);
        assert.match(html, new RegExp(`data-line="${line}"`), src);
    }
});
