# OmpMcp

OmpMcp runs persistent OMP agents through MCP. Each agent uses its own `omp --mode rpc --no-session` process.

An agent retains conversation context across runs. The host can send steering while the agent works, read interim notifications, and wait through a background shell command.

## Build and run

Requirements:

- .NET 10 SDK for builds
- OMP 17.4.1 or a compatible version with RPC v2 and host tools
- Linux or another Unix system with Unix sockets. Tests currently cover Linux.

Publish the executable:

```sh
dotnet publish src/OmpMcp/OmpMcp.csproj -c Release -o artifacts/ompmcp
```

Configure the MCP host to run this command over stdio:

```sh
/absolute/path/to/OmpMcp/artifacts/ompmcp/ompmcp serve
```

The executable requires the .NET 10 runtime. For a standalone Linux build, publish with these arguments:

```sh
dotnet publish src/OmpMcp/OmpMcp.csproj -c Release -r linux-x64 --self-contained -o artifacts/ompmcp
```

Server flags:

| Flag | Purpose |
| --- | --- |
| `--omp PATH` | Select the OMP executable. The default is `omp` on `PATH`. |
| `--agent-dir DIR` | Select an isolated OMP directory with `models.yml`. This also selects OMP’s default profile. |
| `--model PROVIDER/MODEL` | Set the default model for new agents. |
| `--config FILE` | Apply an additional OMP configuration overlay. |
| `--event-capacity COUNT` | Set the retained event count. The default is 4096. |

If `--model` is absent, OMP uses its configured default. The local configuration used during development selects `spark/GLM-5.3-Flash` with high thinking.

OmpMcp does not hardcode that model. The host can select a different model for each agent.

## MCP tools

| Tool | Main arguments | Result |
| --- | --- | --- |
| `models_list` | Optional `filter` | Available models with exact `id`, `provider`, and `modelId` values. |
| `agent_create` | `workspace`, optional `model`, `instructions`, `prompt`, `tools`, `thinking` | Agent state, optional initial submission, and wait credentials. |
| `agent_send` | `agentId`, `message` | Accepted message and run identifiers. |
| `agents_list` | None | Agents owned by this server. |
| `agent_status` | `agentId`, optional `runId` | Agent state and the selected run’s retained result. |
| `events_read` | Optional `after`, `agentId`, `runId` | Event page and its next cursor. |
| `agent_cancel` | `agentId`, `runId` | Cancelled run state. The agent remains available. |
| `agent_close` | `agentId` | Closed agent state. Its process terminates. |
| `session_info` | None | Callback socket, session token, and wait executable. |

For model discovery, call `models_list` with a filter such as `GLM`. Pass an exact returned identifier to `agent_create.model`.

Agent creation reports the selected model. An explicit identifier never silently falls back to a different model.

The default tool allowlist contains `read`, `bash`, `edit`, `write`, `grep`, and `glob`. An empty `tools` array disables built-in tools.

The allowlist also accepts `python`, `notebook`, `inspect_image`, and `web_search`. OMP must support the selected tools and their dependencies.

OMP extension, skill, rule, and LSP discovery are disabled for these agents. The server disables native task delegation and the advisor through a temporary configuration overlay.

The server adds `ompmcp_agent_notify` and `ompmcp_agent_result` as essential host tools. Each call includes the current `run_id` and `text`.

## Results, steering, and events

`agent_send` starts a run on an idle agent. During an active run, it submits steering without waiting for a tool or model request to finish.

Acceptance does not mean delivery. A `message_delivered` event records when OMP adds the message to its conversation.

A result-tool call records a result. The run completes only after OMP emits terminal completion and processes all accepted messages.

Further steering requires a result for the updated request. Duplicate result calls for the same delivered messages retain the first result.

Run states:

- `running`
- `succeeded`
- `incomplete`, when OMP finishes without the required result
- `cancelled`
- `failed`.

The server sends events through MCP `notifications/message` with logger `ompmcp`. Hosts can also read retained events through `events_read`.

MCP notifications are best effort. They do not guarantee that the host resumes its model.

Event cursors are global to one server instance. Reading does not consume events. An expired cursor returns an explicit error.

Completed results remain available through `agent_status` until the server exits, even after event history expires.

## Background waits

Get the callback details from `session_info` or `agent_create`. Run the returned executable through the host’s permitted background shell mechanism:

```sh
ompmcp wait \
  --socket /tmp/ompmcp-INSTANCE/wait.sock \
  --token SESSION_TOKEN \
  --agent AGENT_ID \
  --run RUN_ID \
  --timeout 3600
```

If `waitPrefixArguments` contains values, put them before `wait`. This applies when the server runs through `dotnet ompmcp.dll`.

`OMPMCP_SESSION_TOKEN` can supply the token instead of `--token`.

Wait behavior:

- Default mode waits for the selected run’s completion.
- `--mode events` waits for an event page.
- `--after CURSOR` requests events after a known cursor.
- `--stream` prints event pages until the selected run ends.
- `--timeout` limits the entire wait to between 1 and 43200 seconds.

Output consists of JSON objects, one per line. Several waiters can observe the same result. A result that already exists returns immediately.

Exit codes:

| Code | Meaning |
| --- | --- |
| `0` | Successful run, or an event page in event mode. |
| `1` | Failed, incomplete, or cancelled run, or a callback error. |
| `2` | Timeout. |
| `130` | Interrupted wait. |

Stopping a wait does not cancel the run. Use `agent_cancel` to stop agent work.

## Permissions and lifetime

This version treats the MCP server and OMP as trusted local processes. OMP tools use the server’s launch permissions and operate without interactive approval prompts.

A sandboxed host shell can require elevated execution to access the Unix socket. The host controls that permission.

The socket resides in a private directory. A session token authorizes callback access to one server instance.

Disabling native delegation does not prevent a shell command from starting another program.

Host EOF, SIGTERM, and normal shutdown close owned agents. Forced termination cannot guarantee cleanup. Conversations and results do not survive server restart.

The server does not map `codex/sandbox-state` into OMP’s sandbox yet. This remains a TODO in [the specification](docs/spec.md).

## Tests

The test suite requires OMP, Python 3, and local socket access. `OMPMCP_TEST_OMP` can select a different OMP executable.

Run the suite:

```sh
dotnet test OmpMcp.slnx -m:1 -p:UseSharedCompilation=false
```

Tests use temporary OMP configuration and a scripted `/v1/chat/completions` provider on loopback. They do not make paid model calls.

The suite covers persistent context, model selection, steering, notifications, result boundaries, cancellation, concurrent waits, callback authentication, and host shutdown.

Separate fake RPC children exercise fragmented output, malformed frames, process failures, and completion races.

Protocol references: [OMP RPC v17.4.1](https://github.com/can1357/oh-my-pi/blob/v17.4.1/docs/rpc.md) and the [official MCP C# SDK](https://github.com/modelcontextprotocol/csharp-sdk).
