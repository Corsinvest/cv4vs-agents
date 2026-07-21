/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
// CLI probe — ispeziona cosa ritorna claude.exe sul protocollo stream-json,
// usando gli STESSI flag del nostro ClaudeClient.cs. Strumento di debug:
// utile per verificare il messaggio init, get_settings, get_context_usage,
// e in generale qualsiasi control_request, quando cambia il CLI o un modello.
//
// Uso:
//   node probe.mjs init                 # dumpa il system/init (default)
//   node probe.mjs get_settings         # invia control_request get_settings
//   node probe.mjs get_usage
//   node probe.mjs <subtype> [jsonArgs] # qualsiasi control_request
//
// Esempi:
//   node probe.mjs init
//   node probe.mjs get_settings
//   node probe.mjs get_context_usage
//
// Env:
//   CLAUDE_CLI   override del comando (default: "claude" nel PATH)
//   PROBE_CWD    working directory passata al CLI (default: cwd corrente)
//   PROBE_TIMEOUT_MS  timeout complessivo (default 25000)
//
// Lo script NON manda mai un turno utente reale: per get_* usa solo
// control_request; per "init" forza l'emissione con un turno "hi" e uccide
// il processo appena init arriva, prima che il modello generi una risposta.

import { spawn } from 'node:child_process';

const CLI = process.env.CLAUDE_CLI || 'claude';
const CWD = process.env.PROBE_CWD || process.cwd();
const TIMEOUT_MS = Number(process.env.PROBE_TIMEOUT_MS || 25000);

const subtype = process.argv[2] || 'init';
let extraArgs = {};
if (process.argv[3]) {
    try { extraArgs = JSON.parse(process.argv[3]); }
    catch { console.error('jsonArgs non è JSON valido:', process.argv[3]); process.exit(2); }
}

// Stessi flag di ClaudeClient.StartProcess (senza IDE/MCP/permission: non servono al probe).
const args = [
    '--output-format', 'stream-json',
    '--verbose',
    '--input-format', 'stream-json',
    '--include-partial-messages',
    '--setting-sources', 'user,project,local',
];
// Optional: resume an existing session — check what the CLI restores in `init` (model,
// permissionMode). PROBE_RESUME=<sessionId>
if (process.env.PROBE_RESUME) {
    args.push('--resume', process.env.PROBE_RESUME);
}
// Optional: pass a permission mode at launch (like ClaudeClient does) to see how it interacts
// with --resume. PROBE_PERM=acceptEdits|plan|auto
if (process.env.PROBE_PERM) {
    args.push('--permission-mode', process.env.PROBE_PERM, '--permission-prompt-tool', 'stdio');
}
// Optional: attach an MCP server (e.g. to populate get_context_usage.mcpTools).
//   PROBE_MCP_CONFIG=path/to/mcp.json   PROBE_ALLOWED_TOOLS="mcp__test__*"
if (process.env.PROBE_MCP_CONFIG) {
    args.push('--mcp-config', process.env.PROBE_MCP_CONFIG);
    args.push('--allowedTools', process.env.PROBE_ALLOWED_TOOLS || 'mcp__*');
}

const child = spawn(CLI, args, { shell: true, cwd: CWD });

let buf = '';
let done = false;
let requestId = null;

const finish = (reason, code = 0) => {
    if (done) return;
    done = true;
    console.error(`\n=== fine (${reason}) ===`);
    try { child.kill(); } catch {}
    setTimeout(() => process.exit(code), 100);
};
const timer = setTimeout(() => finish('timeout', 1), TIMEOUT_MS);

const send = (obj) => child.stdin.write(JSON.stringify(obj) + '\n');

// Evidenzia i campi "interessanti" (effort/models) ovunque nel messaggio.
const highlight = (msg) => {
    const hits = [];
    const walk = (o, path) => {
        if (!o || typeof o !== 'object') return;
        for (const [k, v] of Object.entries(o)) {
            const p = path ? `${path}.${k}` : k;
            if (/effort/i.test(k) || k === 'models' || k === 'model') hits.push([p, v]);
            if (v && typeof v === 'object') walk(v, p);
        }
    };
    walk(msg, '');
    return hits;
};

const onInit = (msg) => {
    console.log('=== system/init ===');
    console.log('chiavi top-level:', Object.keys(msg).join(', '));
    const hits = highlight(msg);
    console.log('\ncampi effort/models:');
    if (!hits.length) console.log('  (nessuno nell init)');
    for (const [p, v] of hits) console.log('  ', p, '=', JSON.stringify(v).slice(0, 600));
    console.log('\n--- init completo ---');
    console.log(JSON.stringify(msg, null, 2));
};

child.stdout.on('data', (chunk) => {
    buf += chunk.toString();
    let nl;
    while ((nl = buf.indexOf('\n')) >= 0) {
        const line = buf.slice(0, nl).trim();
        buf = buf.slice(nl + 1);
        if (!line) continue;
        let msg;
        try { msg = JSON.parse(line); } catch { continue; }

        // Modalità "commands": logga TUTTI i system subtype e cattura
        // commands_changed (la lista ricca che il CLI emette quando i comandi cambiano).
        if (subtype === 'commands' && msg.type === 'system') {
            console.error(`[debug] system/${msg.subtype}`);
            if (msg.subtype === 'commands_changed') {
                console.log('=== system/commands_changed ===');
                console.log('chiavi:', Object.keys(msg).join(', '));
                console.log(JSON.stringify(msg, null, 2));
                clearTimeout(timer);
                finish('commands_changed ricevuto');
                return;
            }
        }

        // init pronto: il CLI è inizializzato → possiamo mandare la control_request.
        if (msg.type === 'system' && msg.subtype === 'init') {
            if (subtype === 'init') {
                onInit(msg);
                clearTimeout(timer);
                finish('init ricevuto');
                return;
            }
            // Modalità che attendono messaggi successivi (non control_request).
            if (subtype === 'result' || subtype === 'turn' || subtype === 'commands') { continue; }
            // Per le altre query: ora che è inizializzato, manda la control_request.
            requestId = `probe_${Date.now()}`;
            send({ type: 'control_request', request_id: requestId, request: { subtype, ...extraArgs } });
            continue;
        }

        // Modalità "turn": dumpa OGNI messaggio del turno, evidenziando i campi
        // finestra/usage per capire cosa c'è già nel flusso normale (assistant/result).
        if (subtype === 'turn') {
            if (msg.type === 'stream_event') { continue; }
            console.log(`--- ${msg.type}${msg.subtype ? '/' + msg.subtype : ''} ---`);
            const grab = (o, path) => {
                if (!o || typeof o !== 'object') return;
                for (const [k, v] of Object.entries(o)) {
                    const p = path ? `${path}.${k}` : k;
                    if (/contextWindow|maxToken|maxOutput|maxTokens|window|usage|percentage|modelUsage/i.test(k)) {
                        console.log('   ', p, '=', JSON.stringify(v)?.slice(0, 300));
                    }
                    if (v && typeof v === 'object' && !Array.isArray(v)) grab(v, p);
                }
            };
            grab(msg, '');
            if (msg.type === 'result') { clearTimeout(timer); finish('turn completato'); return; }
            continue;
        }

        // Modalità "result": cattura il messaggio result con i dati di usage per-modello.
        if (subtype === 'result' && msg.type === 'result') {
            console.log('=== result ===');
            console.log('chiavi:', Object.keys(msg).join(', '));
            for (const k of ['total_cost_usd', 'modelUsage', 'usage', 'fast_mode_state']) {
                if (msg[k] !== undefined) console.log(`\n${k} =`, JSON.stringify(msg[k], null, 2));
            }
            clearTimeout(timer);
            finish('result ricevuto');
            return;
        }

        // Risposta alla nostra control_request.
        if (msg.type === 'control_response' && msg.response?.request_id === requestId) {
            console.log(`=== control_response (${subtype}) ===`);
            console.log(JSON.stringify(msg.response, null, 2));
            const hits = highlight(msg.response);
            if (hits.length) {
                console.log('\ncampi effort/models:');
                for (const [p, v] of hits) console.log('  ', p, '=', JSON.stringify(v).slice(0, 600));
            }
            clearTimeout(timer);
            finish('risposta ricevuta');
            return;
        }
    }
});

child.stderr.on('data', (d) => process.stderr.write(d));
child.on('error', (e) => { console.error('spawn error:', e.message); finish('spawn error', 1); });
child.on('close', (code) => finish(`close code=${code}`, code ?? 0));

// L'init in stream-json viene emesso solo dopo il primo turno utente.
// Per "init" mandiamo "hi" (e usciamo appena arriva, prima di ogni risposta).
// Per le control_request lo mandiamo lo stesso: serve a far partire l'init,
// dopo il quale inviamo la richiesta vera.
send({ type: 'user', message: { role: 'user', content: 'hi' } });
