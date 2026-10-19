# OmpMcp — High-level specification

## 1. Overview and goals

OmpMcp exposes local OMP agents through MCP. A host can delegate work, continue conversations, and send messages during active work.

The goals are:

- Keep agents available across multiple requests.
- Accept steering messages while agents run.
- Deliver explicit results and interim notifications.
- Support waits through the host’s background shell mechanism.
- Provide deterministic tests without paid model calls.

Agents remain available until explicitly closed, their process fails, or the MCP server exits. Conversations do not survive server restarts.

## 2. Core concepts

| Concept | Meaning |
| --- | --- |
| **Host** | The MCP client that creates and directs agents. |
| **Server instance** | One OmpMcp process that owns agents, events, results, and a callback endpoint. |
| **Agent** | An OMP conversation with a stable identifier and dedicated process. |
| **Run** | A work request with its own identifier, result, and completion status. |
| **Steering message** | A message that updates active work at an OMP processing boundary. |
| **Event** | A notification, result, or lifecycle change associated with an agent and run. |
| **Waiter** | A host command that waits for events or completion. |
| **Session token** | A credential that authorizes callback access to one server instance. |

An agent can serve multiple runs. Run completion does not close the agent.

## 3. Architecture

### 3.1. MCP interface

The server exposes MCP over standard input and output.

The host can create agents, send messages, inspect status, read events, cancel runs, and close agents.

The `models_list` tool lists models available through OMP’s configured providers. Entries contain exact provider and model identifiers without credentials.

The `agent_create` tool accepts an optional `model` identifier. If the host omits it, OMP uses its configured default.

Agent creation reports the selected provider and model. An unavailable model produces an explicit error without a fallback to another model.

Agent creation and message submission return identifiers and acceptance status without waiting for completion. Agent lifetime is independent of individual MCP requests.

### 3.2. OMP interface

Each agent uses a dedicated `omp --mode rpc --no-session` process. The server keeps that process alive between runs.

Agent configuration selects its workspace, instructions, model, and available tools. OMP retains conversation context within the process without saving a conversation session.

The server registers two host tools through RPC:

| Tool | Purpose |
| --- | --- |
| `ompmcp_agent_result` | Submit the explicit result for the current run. |
| `ompmcp_agent_notify` | Submit an interim notification. |

OMP configuration disables built-in subagent spawning and the advisor. This does not prevent an unrestricted shell tool from launching another program.

The server does not modify the user’s normal OMP configuration to create an agent.

### 3.3. Background waits

The same executable provides an `ompmcp wait` command. The host runs this command through its approved elevated-execution mechanism.

The helper authenticates with a session token and connects to the server through a local Unix socket.

The server owns the socket and agent processes. No separate broker is required.

The host harness owns background execution and wakeup behavior. MCP notifications alone do not guarantee that the host resumes its model.

### 3.4. Trust boundary

The initial version treats OMP as a trusted local process.

The MCP server and its children use their launch permissions. They do not automatically inherit restrictions from the host’s sandboxed shell commands.

Mapping host sandbox metadata into OMP’s own sandbox is postponed in §7.

## 4. Observable behavior

### 4.1. Messages and lifecycle

A message to an idle agent starts a new run within its existing conversation.

A message to a running agent becomes steering for active work. Acceptance, delivery, and completion are distinct states.

Steering takes effect at an OMP processing boundary. Acceptance does not promise immediate interruption of a tool or model request.

Messages accepted near completion cannot disappear. Each accepted message belongs to the active run or starts a subsequent run.

Cancellation requests that the current run stop. If OMP remains usable after cancellation, the agent retains its conversation.

Closing an agent ends its process and prevents further messages.

Closing the host connection shuts down the server and its owned agents. Restarting the server does not restore their conversations.

### 4.2. Results and notifications

A successful run requires an explicit result-tool call. Ordinary assistant text does not substitute for that result.

Result receipt and runtime completion are separate events. The server reports terminal completion only after OMP finishes the run.

Duplicate result delivery does not produce duplicate completion events. Results identify their agent and run, preventing earlier results from satisfying later waits.

If OMP finishes without the required result, the server reports an incomplete run and releases completion waiters.

Interim notifications remain distinct from results. The host can read them through MCP or the wait helper.

### 4.3. Waiting and event access

Events have ordered cursors. A waiter can request events after a known cursor.

A waiter can wait for new events or run completion. Results that already exist return immediately.

Several waiters can observe the same completion. Reading a result does not consume it.

Timeout or cancellation of a waiter does not cancel the agent.

Completed results remain available until the server exits. If event history expires, the server reports the missing history explicitly.

Callback access requires a valid session token for the server instance.

## 5. Errors and edge cases

| Condition | Observable outcome |
| --- | --- |
| Unknown agent or run | The operation returns an explicit error. |
| Closed agent | New messages fail without creating a replacement conversation. |
| Invalid session token | Callback access fails without returning agent data. |
| Agent process failure | Active work fails and completion waiters receive that failure. |
| Unsupported RPC behavior | The server reports a compatibility error. |
| Missing result | The run ends as incomplete. |
| Wait timeout or cancellation | The waiter detaches and the agent remains available. |
| Server shutdown | Outstanding waits end and owned agents terminate. |
| Expired event cursor | The server reports unavailable history. |

## 6. Testing

Integration tests use real OMP processes with a scripted `/v1/chat/completions` endpoint. The endpoint emits streaming text and tool calls.

Each test uses a temporary `PI_CODING_AGENT_DIR` containing `models.yml`. Tests explicitly select the default profile and a custom `openai-completions` provider.

The provider uses a loopback URL and requires no credentials. Tests do not depend on personal model configuration or paid inference.

Tests coordinate response delivery through synchronization points rather than arbitrary delays.

Acceptance scenarios cover:

- Conversation context across multiple runs without persisted sessions.
- Steering during model output and pending tool execution.
- Distinct interim notifications and final results.
- Duplicate results, missing results, and terminal completion.
- Messages accepted at the end of a run.
- Concurrent waiters and waits started after completion.
- Independent waiter cancellation and run cancellation.
- Agent failure, explicit closure, and host shutdown.
- Isolation of messages and results between concurrent agents.
- Disabled built-in delegation.
- Model discovery, explicit model selection, the configured default, and unavailable models.
- Invalid tokens and expired event cursors.

Transport tests use fake RPC children to exercise malformed messages, fragmented output, process failures, and shutdown races.

## 7. Postponed — Do Not Address

- [ ] **Sandbox propagation:** Investigate `codex/sandbox-state` metadata on MCP calls that initiate agents. Verify its contract and OMP’s available sandbox controls. Map supported restrictions into OMP’s sandbox and define behavior for unsupported restrictions.
- **Session recovery:** Restore conversations after server restart or agent process failure.
- **Native MCP Tasks:** Add task-based waits and host-specific automatic wakeup integrations.
- **Additional transports:** Add ACP, remote access, shared TCP brokers, FIFO fallback, or platform-specific callback transports.
- **Multiple users:** Support shared hosting and authorization between independent users.

These items remain outside the initial version until explicitly revisited.
