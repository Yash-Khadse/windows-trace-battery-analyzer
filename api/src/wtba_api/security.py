"""Host and Origin checks (DNS-rebinding defence), as a pure ASGI middleware."""

from __future__ import annotations

import json

from starlette.types import ASGIApp, Message, Receive, Scope, Send

DEFAULT_HOSTS = ("127.0.0.1", "localhost", "[::1]")


def host_name(value: str) -> str:
    """Lower-cased hostname of a Host header value, port stripped (IPv6 aware)."""
    v = value.strip().lower()
    if v.startswith("["):
        end = v.find("]")
        return v[: end + 1] if end != -1 else v
    return v.split(":", 1)[0]


class HostOriginGuard:
    def __init__(
        self,
        app: ASGIApp,
        allowed_hosts: tuple[str, ...],
        allowed_origins: tuple[str, ...],
    ) -> None:
        self.app = app
        self.hosts = frozenset(host_name(h) for h in allowed_hosts)
        self.origins = frozenset(o.rstrip("/").lower() for o in allowed_origins)

    async def __call__(self, scope: Scope, receive: Receive, send: Send) -> None:
        if scope["type"] not in ("http", "websocket"):
            await self.app(scope, receive, send)
            return
        host = origin = None
        for k, v in scope["headers"]:
            if k == b"host":
                host = v.decode("latin-1")
            elif k == b"origin":
                origin = v.decode("latin-1")
        if host is None or host_name(host) not in self.hosts:
            await self._reject(scope, send, 400, "invalid_host", "invalid Host header")
        elif origin is not None and origin.rstrip("/").lower() not in self.origins:
            await self._reject(
                scope, send, 403, "forbidden_origin", "origin not allowed"
            )
        else:
            await self.app(scope, receive, send)

    @staticmethod
    async def _reject(
        scope: Scope, send: Send, status: int, code: str, message: str
    ) -> None:
        if scope["type"] == "websocket":
            await send({"type": "websocket.close", "code": 1008})
            return
        body = json.dumps({"error": {"code": code, "message": message}}).encode()
        start: Message = {
            "type": "http.response.start",
            "status": status,
            "headers": [
                (b"content-type", b"application/json"),
                (b"content-length", str(len(body)).encode()),
            ],
        }
        await send(start)
        await send({"type": "http.response.body", "body": body})
