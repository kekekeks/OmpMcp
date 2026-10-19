"""Deterministic OMP wire peer for boundary and failure tests. No model calls."""
import json
import re
import sys

scenario = sys.argv[1]


def emit(value):
    encoded = json.dumps(value) + "\n"
    # Fragment every frame across writes, including JSON tokens.
    for index in range(0, len(encoded), 7):
        sys.stdout.write(encoded[index:index + 7])
        sys.stdout.flush()


def reply(command, data=None):
    emit({"type": "response", "id": command["id"], "command": command["type"],
          "success": True, "data": data or {}})


if scenario == "malformed":
    print("not-json", flush=True)
else:
    emit({"type": "ready", "supportedProtocolVersions": [1, 2]})

prompts = 0
for line in sys.stdin:
    command = json.loads(line)
    kind = command["type"]
    if kind == "get_state":
        reply(command, {"model": {"provider": "fake", "id": "model", "name": "Fake"}, "dumpTools": []})
    elif kind == "prompt":
        prompts += 1
        if scenario == "crash":
            reply(command)
            sys.exit(7)
        if prompts == 2:
            # The previous cycle finishes after the host accepts another message,
            # but before OMP delivers that message to a new cycle.
            emit({"type": "agent_end", "isTerminal": True})
        reply(command)
        emit({"type": "message_end", "message": {"role": "user", "content": command["message"]}})
        if prompts == 2:
            run_id = re.search('run_id="([^"]+)"', command["message"]).group(1)
            emit({"type": "host_tool_call", "id": "host-result", "toolCallId": "call-result",
                  "toolName": "ompmcp_agent_result", "arguments": {"run_id": run_id, "text": "boundary result"}})
    elif kind == "host_tool_result":
        emit({"type": "agent_end", "isTerminal": False})
    elif kind == "finish":
        emit({"type": "agent_end", "isTerminal": True})
        reply(command)
    else:
        reply(command)
