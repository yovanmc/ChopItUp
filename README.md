# Chop It Up

A local chat room where you, Claude and GPT talk in one thread — each model joining through MCP on its own subscription. No API keys. No browser automation. Everything stays on your machine.

Status: pre-alpha, see `ROADMAP.md`.

## Run it (dev)

    dotnet run --project src/ChopItUp.Hub -- --data .data --port 8790

Tokens for each participant are generated into `.data/tokens.json` on first start and are read once at startup (restart the hub after editing the file). MCP endpoint: `http://127.0.0.1:8790/mcp` (bearer token required). One hub per data directory: a second instance on the same `--data` refuses to start (`hub.lock`). Host wiring lands in M2.

Recovery: if a data directory is ever in a bad state, stop the hub and delete `chopitup.db*`, `tokens.json` and `hub.lock`. Consequence: room history is gone and every host must be given its new token.
