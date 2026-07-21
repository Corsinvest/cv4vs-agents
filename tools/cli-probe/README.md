# cli-probe

Strumento di debug per ispezionare cosa ritorna `claude.exe` sul protocollo
stream-json, usando gli **stessi flag** di `ClaudeClient.cs`. Utile quando
cambia il CLI o un modello e vogliamo verificare init / settings / context.

> Nota: il probe **non** invia mai un turno utente reale. Per le `get_*` usa
> solo `control_request`; per `init` forza l'emissione con un "hi" e termina
> appena l'init arriva, prima che il modello generi una risposta.

## Uso

```sh
node probe.mjs init               # dumpa il messaggio system/init (default)
node probe.mjs get_settings       # control_request get_settings
node probe.mjs get_usage
node probe.mjs get_context_usage
node probe.mjs <subtype> '<json>' # qualsiasi control_request con argomenti
```

Evidenzia automaticamente i campi `effort` / `models` / `model` ovunque
compaiano nella risposta.

## Env

| Variabile          | Default            | Descrizione                          |
| ------------------ | ------------------ | ------------------------------------ |
| `CLAUDE_CLI`       | `claude` (nel PATH) | override del comando CLI             |
| `PROBE_CWD`        | cwd corrente       | working directory passata al CLI     |
| `PROBE_TIMEOUT_MS` | `25000`            | timeout complessivo in ms            |

## Esempio

```sh
# Da dove sono definiti i livelli di effort per-modello:
node probe.mjs init
```
