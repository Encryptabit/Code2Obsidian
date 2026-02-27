import json
import os
from mitmproxy import ctx, http

MAX_CHARS = int(os.getenv("WS_TAP_MAX_CHARS", "1000"))
SHOW_ALL = os.getenv("WS_TAP_SHOW_ALL", "0") == "1"
MAX_STORED_MESSAGES = int(os.getenv("WS_TAP_MAX_STORED", "120"))

IMPORTANT_METHODS = {
    "codex/event/agent_reasoning",
    "codex/event/agent_message",
    "codex/event/task_started",
    "codex/event/task_complete",
    "codex/event/task_error",
    "codex/event/exec_command_begin",
    "codex/event/exec_command_end",
    "codex/event/exec_output",
    "turn/started",
    "turn/completed",
    "turn/failed",
}

IMPORTANT_ITEM_TYPES = {
    "reasoning",
    "agentMessage",
    "commandExecution",
}


def _truncate(text: str, max_chars: int = MAX_CHARS) -> str:
    text = text.replace("\n", "\\n")
    if len(text) <= max_chars:
        return text
    return text[:max_chars] + "...(truncated)"


def _is_important(method: str, payload: dict) -> bool:
    if method in IMPORTANT_METHODS:
        return True

    if method in {"item/started", "item/completed"}:
        item = (payload.get("params") or {}).get("item") or {}
        return item.get("type") in IMPORTANT_ITEM_TYPES

    return False


def _summarize(method: str, payload: dict, raw_text: str) -> str:
    params = payload.get("params") or {}

    if method == "codex/event/agent_reasoning":
        msg = params.get("msg") or {}
        return f"{method}: {msg.get('text', '')}"

    if method == "codex/event/agent_message":
        msg = params.get("msg") or {}
        phase = msg.get("phase", "")
        message = msg.get("message", "")
        return f"{method}[{phase}]: {message}"

    if method in {"item/started", "item/completed"}:
        item = params.get("item") or {}
        item_type = item.get("type", "?")
        if item_type == "reasoning":
            summary = item.get("summary") or []
            return f"{method}[reasoning]: {summary}"
        if item_type == "agentMessage":
            phase = item.get("phase", "")
            text = item.get("text", "")
            return f"{method}[agentMessage:{phase}]: {text}"
        if item_type == "commandExecution":
            cmd = item.get("command", "")
            status = item.get("status", "")
            return f"{method}[commandExecution:{status}]: {cmd}"
        return f"{method}[{item_type}]"

    if method in {
        "codex/event/exec_output",
        "codex/event/exec_command_begin",
        "codex/event/exec_command_end",
    }:
        return f"{method}: {raw_text}"

    return f"{method}: {raw_text}"


def _trim_history(flow: http.HTTPFlow) -> None:
    """Keep websocket history bounded so mitmweb's WS tab stays responsive."""
    ws = flow.websocket
    if not ws:
        return
    extra = len(ws.messages) - MAX_STORED_MESSAGES
    if extra > 0:
        del ws.messages[:extra]


def websocket_message(flow: http.HTTPFlow) -> None:
    if not flow.websocket or not flow.websocket.messages:
        return

    message = flow.websocket.messages[-1]
    direction = "C->S" if message.from_client else "S->C"

    payload = message.content
    if isinstance(payload, (bytes, bytearray)):
        text = payload.decode("utf-8", errors="replace")
        size = len(payload)
    else:
        text = str(payload)
        size = len(text.encode("utf-8", errors="replace"))

    if SHOW_ALL:
        _trim_history(flow)
        ctx.log.info(f"{direction} {size}b {_truncate(text)}")
        return

    keep_in_history = False

    try:
        obj = json.loads(text)
    except json.JSONDecodeError:
        if "error" in text.lower():
            keep_in_history = True
            ctx.log.warn(f"{direction} {size}b non-json: {_truncate(text)}")
    else:
        method = obj.get("method", "")
        if _is_important(method, obj):
            keep_in_history = True
            summary = _summarize(method, obj, text)
            ctx.log.info(f"{direction} {size}b {_truncate(summary)}")

    # Drop noisy frames from stored WS history to prevent mitmweb tab crashes.
    if not keep_in_history and flow.websocket.messages:
        flow.websocket.messages.pop()

    _trim_history(flow)
