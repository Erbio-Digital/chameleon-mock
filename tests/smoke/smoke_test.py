"""End-to-end smoke test of Chameleon against a running instance (docker compose).

Covers: .NET tests, image build, Bradesco Pix (happy path, idempotency, 401, 409, faults,
expiry by clock), NFS-e Sao Paulo (reject, 409, TTL authorizes), real webhook delivery (local
receiver, with Content-Length), structured history (correlation, masked payload, redacted
credential), verify, webhook outbox (retries, SSRF block, resend, duplication), expiry on its
own in real time, accounts and balances, mTLS, Inter (Pix, boleto, banking), Zendesk, full
Bradesco (payer, simulation, cobv, refund, SPI, TED, balance, statement), the panel Control
API (feed with cursor, entity with timeline, armed faults), live events over the SignalR hub,
the MCP endpoint and persistence after a restart.

Usage (from the repository root, with the compose stack up):
    python -I tests/smoke/smoke_test.py
    python -I tests/smoke/smoke_test.py --skip-dotnet --skip-build   # HTTP flows only
    python -I tests/smoke/smoke_test.py --skip-dotnet --skip-build --oidc   # against docker-compose.oidc.yml

Stdlib only. Ids are unique per run, so it can run several times without a reset.
"""

import argparse
import base64
import hashlib
import hmac
import json
import os
import re
import ssl
import shutil
import tempfile
import subprocess
import sys
import threading
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid
from http.server import BaseHTTPRequestHandler, HTTPServer

BASE = "http://localhost:5000"
MTLS = "https://localhost:5443"  # port that requires a client certificate
KEYCLOAK = "http://localhost:8180"
OIDC = False
WEBHOOK_PORT = 9999
WEBHOOK_HOST = "host.docker.internal"  # how the container sees the host

RUN = uuid.uuid4().hex[:10]
results: list[tuple[str, bool, str]] = []
received: list[dict] = []
hits: dict[str, int] = {}


# ---------------------------------------------------------------- infra

def check(name: str, ok: bool, detail: str = "") -> None:
    results.append((name, ok, detail))
    print(f"  [{'PASS' if ok else 'FAIL'}] {name}" + (f"  ({detail})" if detail and not ok else ""))


# Client certificate per sandbox (.crt/.key files), filled in by subscribe().
CERTS: dict[str, tuple[str, str]] = {}
CERT_DIR = tempfile.mkdtemp(prefix="chameleon-smoke-")


def tls_context(sandbox: str | None) -> ssl.SSLContext:
    ctx = ssl.create_default_context(cafile=os.path.join(CERT_DIR, "ca.crt"))
    if sandbox:
        ctx.load_cert_chain(*CERTS[sandbox])
    return ctx


def http(method: str, path: str, body=None, token: str | None = None,
         headers: dict | None = None, form: dict | None = None) -> tuple[int, dict | str]:
    data = (urllib.parse.urlencode(form).encode() if form is not None
            else json.dumps(body).encode() if body is not None else None)
    # A bank surface in a sandbox with a certificate goes over the mTLS port, like a configured app.
    sandbox = (m.group(1) if (m := re.match(r"^/s/([^/]+)/(bradesco|inter)/", path))
               else (headers or {}).get("X-Chameleon-Sandbox") if path.startswith("/bradesco/") else None)
    base, context = (MTLS, tls_context(sandbox)) if sandbox in CERTS else (BASE, None)
    req = urllib.request.Request(base + path, data=data, method=method, headers=headers or {})
    if data is not None:
        req.add_header("Content-Type", "application/x-www-form-urlencoded" if form is not None else "application/json")
    if token:
        req.add_header("Authorization", f"Bearer {token}")
    try:
        with urllib.request.urlopen(req, timeout=15, context=context) as resp:
            status, raw = resp.status, resp.read().decode()
    except urllib.error.HTTPError as e:
        status, raw = e.code, e.read().decode()
    try:
        return status, json.loads(raw) if raw else {}
    except json.JSONDecodeError:
        return status, raw


def http_raw_post(url: str, form: dict) -> tuple[int, dict | str]:
    req = urllib.request.Request(url, data=urllib.parse.urlencode(form).encode(), method="POST")
    try:
        with urllib.request.urlopen(req, timeout=15) as resp:
            status, raw = resp.status, resp.read().decode()
    except urllib.error.HTTPError as e:
        status, raw = e.code, e.read().decode()
    try:
        return status, json.loads(raw)
    except json.JSONDecodeError:
        return status, raw


def keycloak_token(user: str) -> str:
    form = {"grant_type": "password", "client_id": "chameleon-panel", "username": user,
            "password": "test-password", "scope": "openid email profile"}
    status, body = http_raw_post(f"{KEYCLOAK}/realms/test/protocol/openid-connect/token", form)
    assert status == 200, body
    return body["id_token"]


def use_default_bearer(bearer: str) -> None:
    """Every control-plane request (/_chameleon/) carries this Bearer unless it sets its own."""
    plain = urllib.request.Request

    class BearerRequest(plain):
        def __init__(self, url, *args, **kwargs):
            super().__init__(url, *args, **kwargs)
            if "/_chameleon/" in url and not self.has_header("Authorization"):
                self.add_header("Authorization", f"Bearer {bearer}")

    urllib.request.Request = BearerRequest


def step_oidc() -> str:
    print("\n0. OIDC authentication (Keycloak)")
    ana = keycloak_token("ana@example.com")
    status, cfg = http("GET", "/_chameleon/config")
    check("config reports auth oidc", status == 200 and cfg.get("auth") == "oidc", f"{status} {cfg}")
    check("no token -> 401", http("GET", "/_chameleon/sandboxes")[0] == 401)
    status, me = http("GET", "/_chameleon/me", token=ana)
    check("ana (allowed) -> OPERATOR", status == 200 and me.get("role") == "OPERATOR", f"{status} {me}")
    check("bia (e-mail not verified) -> 403", http("GET", "/_chameleon/me", token=keycloak_token("bia@example.org"))[0] == 403)
    check("caio (outside the allowlist) -> 403", http("GET", "/_chameleon/me", token=keycloak_token("caio@example.net"))[0] == 403)
    status, me = http("GET", "/_chameleon/me", token="chm_ci_local-test-key")
    check("CI key -> CI", status == 200 and me.get("role") == "CI", f"{status} {me}")
    status, created = http("POST", "/_chameleon/tokens", {"name": f"smoke-{RUN}"}, token=ana)
    pat = created.get("token") if isinstance(created, dict) else None
    check("ana creates an agent token", status == 201 and bool(pat), f"{status} {created}")
    status, me = http("GET", "/_chameleon/me", token=pat)
    check("agent token -> AGENT", status == 200 and me.get("role") == "AGENT", f"{status} {me}")
    status, _ = http("DELETE", f"/_chameleon/tokens/{created.get('id')}", token=ana)
    check("ana revokes the agent token", status == 204, str(status))
    check("revoked token -> 401", http("GET", "/_chameleon/me", token=pat)[0] == 401)
    return ana


def wait_ready(timeout: int = 60) -> bool:
    deadline = time.time() + timeout
    while time.time() < deadline:
        try:
            if http("GET", "/_chameleon/health")[0] == 200:
                return True
        except (urllib.error.URLError, ConnectionError, TimeoutError):
            pass
        time.sleep(1)
    return False


def run(cmd: list[str], timeout: int = 600) -> subprocess.CompletedProcess:
    return subprocess.run(cmd, capture_output=True, text=True, timeout=timeout)


class WebhookReceiver(BaseHTTPRequestHandler):
    def do_POST(self):
        # Chameleon sends with Content-Length; chunked stays accepted for other senders.
        if self.headers.get("Transfer-Encoding", "").lower() == "chunked":
            raw = b""
            while (size := int(self.rfile.readline().strip(), 16)) > 0:
                raw += self.rfile.read(size)
                self.rfile.readline()
            self.rfile.readline()
        else:
            raw = self.rfile.read(int(self.headers.get("Content-Length", 0)))
        hits[self.path] = hits.get(self.path, 0) + 1
        received.append({"path": self.path, "body": json.loads(raw or b"{}"), "raw": raw,
                         "headers": {k.lower(): v for k, v in self.headers.items()},
                         "content_length": self.headers.get("Content-Length")})
        # /flaky/...: answers 500 on the first two calls to that path.
        status = 500 if self.path.startswith("/flaky/") and hits[self.path] <= 2 else 200
        self.send_response(status)
        self.end_headers()

    def log_message(self, *_):
        pass


def start_receiver() -> None:
    server = HTTPServer(("0.0.0.0", WEBHOOK_PORT), WebhookReceiver)
    threading.Thread(target=server.serve_forever, daemon=True).start()


def until(check, timeout: float = 10):
    """Polls check() until it returns something truthy (or the timeout); returns the last value."""
    deadline = time.time() + timeout
    value = check()
    while not value and time.time() < deadline:
        time.sleep(0.2)
        value = check()
    return value


def wait_webhook(path: str, timeout: float = 5) -> dict | None:
    deadline = time.time() + timeout
    while time.time() < deadline:
        for w in received:
            if w["path"] == path:
                return w
        time.sleep(0.1)
    return None


# ---------------------------------------------------------------- domain helpers

def bradesco_token(prefix: str, client_id: str | None, secret: str | None = "") -> tuple[int, dict | str]:
    """Bradesco Pix token like the real API: form grant_type, client in HTTP Basic."""
    basic = base64.b64encode(f"{client_id}:{secret}".encode()).decode()
    return http("POST", f"{prefix}/bradesco/v2/oauth/token", headers={"Authorization": f"Basic {basic}"},
                form={"grant_type": "client_credentials"})


def token() -> str:
    _, body = bradesco_token("", "smoke")
    return body["access_token"]


def txid(n: int) -> str:
    return f"smoke{RUN}{n:020d}"[:35]


def create_cob(tx: str, tok: str | None, headers: dict | None = None) -> tuple[int, dict]:
    return http("PUT", f"/bradesco/v2/cob/{tx}", {
        "calendario": {"expiracao": 3600},
        "devedor": {"cpf": "12345678909", "nome": "Joao Silva"},
        "valor": {"original": "123.45"},
        "chave": "00000000-0000-4000-8000-000000000001",
        "webhookUrl": f"http://{WEBHOOK_HOST}:{WEBHOOK_PORT}/pix/{tx}",
    }, tok, headers)


def journal(entity_id: str, kind: str | None = None) -> list[dict]:
    return http("GET", f"/_chameleon/journal?entityId={entity_id}" + (f"&kind={kind}" if kind else ""))[1]


# ---------------------------------------------------------------- steps

def step_dotnet() -> None:
    print("\n1. dotnet test")
    r = run(["dotnet", "test"])
    check("integration tests green", r.returncode == 0, r.stdout[-400:])


def step_build() -> None:
    print("\n2. docker build")
    r = run(["docker", "build", "-q", "-t", "chameleon:smoke", "."])
    check("image builds (restore with every csproj)", r.returncode == 0, r.stderr[-400:])


def step_bradesco() -> None:
    print("\n3. Bradesco Pix")
    tok = token()
    tx = txid(1)

    status, cob = create_cob(tx, tok, {"X-Correlation-Id": f"smoke-{RUN}"})
    check("PUT creates an ATIVA charge (201)", status == 201 and cob.get("status") == "ATIVA", f"{status} {cob}")

    status, again = create_cob(tx, tok)
    check("repeated PUT is idempotent (200, same revision)", status == 200 and again.get("revisao") == 0, f"{status}")

    status, paid = http("POST", f"/_chameleon/bradesco/cob/{tx}/pay")
    check("pay moves the charge to CONCLUIDA", status == 200 and paid.get("status") == "CONCLUIDA", f"{status} {paid}")

    hook = wait_webhook(f"/pix/{tx}")
    ok = hook is not None and hook["body"]["pix"][0]["txid"] == tx
    check("Pix webhook delivered to the local receiver", ok,
          "nothing received: is a firewall blocking port 9999?" if hook is None else str(hook))
    check("webhook sent with Content-Length", hook is not None and hook["content_length"] is not None, str(hook))

    status, got = http("GET", f"/bradesco/v2/cob/{tx}", token=tok)
    check("GET shows CONCLUIDA with pix[]", got.get("status") == "CONCLUIDA" and len(got.get("pix", [])) == 1, str(got))

    entries = journal(tx)
    summaries = [e["summary"] for e in entries]
    put = f"PUT /bradesco/v2/cob/{tx}"
    expected = [put, "criada -> ATIVA", put, f"POST /_chameleon/bradesco/cob/{tx}/pay", "ATIVA -> CONCLUIDA"]
    check("timeline: the call comes before the transition it caused", summaries[:5] == expected, str(summaries))
    check("repeat annotated as idempotent hit", entries[2].get("detail") == "idempotent hit", str(entries[2]))

    created = entries[0]
    check("call with status, duration and correlation",
          created.get("statusCode") == 201 and created.get("durationMs") is not None
          and created.get("correlationId") == f"smoke-{RUN}", str(created))
    check("Authorization redacted in the history",
          created.get("requestHeaders", {}).get("Authorization") == "[redacted]", str(created.get("requestHeaders")))
    body = created.get("requestBody") or ""
    check("CPF and name masked, amount readable",
          "12345678909" not in body and "Joao Silva" not in body and "123.45" in body, body)
    webhooks = until(lambda: journal(tx, "OutboundWebhook"))
    check("webhook delivery recorded with status and payload",
          len(webhooks) == 1 and webhooks[0].get("statusCode") == 200 and tx in (webhooks[0].get("requestBody") or ""),
          str(webhooks))

    status, _ = create_cob(txid(2), None)
    check("no token -> 401", status == 401, str(status))

    status, _ = create_cob("short", tok)
    check("invalid txid -> 400", status == 400, str(status))

    status, _ = http("POST", f"/_chameleon/bradesco/cob/{tx}/pay")
    check("paying an already paid charge -> 409", status == 409, str(status))

    status, _ = http("POST", "/_chameleon/faults",
                     {"connector": "bradesco", "route": "PUT /v2/cob", "kind": "Error"})
    check("fault on an unknown route -> 400", status == 400, str(status))
    http("POST", "/_chameleon/faults",
         {"connector": "bradesco", "route": "PUT /bradesco/v2/cob/{txid}", "kind": "Error", "status": 503, "times": 1})
    first, _ = create_cob(txid(3), tok)
    second, _ = create_cob(txid(3), tok)
    check("armed fault: 503 once, then 201", (first, second) == (503, 201), f"{first}, {second}")

    status, verify = http("POST", "/_chameleon/verify",
                          {"connector": "bradesco", "entityId": tx, "kind": "StateTransition"})
    check("verify counts 2 transitions", verify.get("count") == 2, str(verify))


def step_clock() -> None:
    print("\n4. Virtual clock (Pix)")
    tx = txid(4)
    create_cob(tx, token())
    http("POST", "/_chameleon/clock/advance", {"seconds": 4000})
    # the previous token expired along with the clock (3600 s)
    _, got = http("GET", f"/bradesco/v2/cob/{tx}", token=token())
    check("advance past the TTL -> REMOVIDA_PELO_PSP", got.get("status") == "REMOVIDA_PELO_PSP", str(got))


def step_nfse() -> None:
    print("\n5. NFS-e Sao Paulo")
    rps_reject, rps_ok = f"RPS-{RUN}-1", f"RPS-{RUN}-2"
    hook = lambda rps: f"http://{WEBHOOK_HOST}:{WEBHOOK_PORT}/nfse/{rps}"

    _, emitted = http("POST", "/nfse-sp/nfse", {"valor": 250, "webhookUrl": hook(rps_reject), "rps": rps_reject})
    check("issue -> Processando", emitted.get("status") == "Processando", str(emitted))

    status, rejected = http("POST", f"/_chameleon/nfse-sp/nfse/{rps_reject}/reject", {"reason": "invalid CNPJ"})
    check("reject -> Rejeitada", status == 200 and rejected.get("status") == "Rejeitada", str(rejected))

    w = wait_webhook(f"/nfse/{rps_reject}")
    check("nfse.rejeitada webhook delivered", w is not None and w["body"].get("evento") == "nfse.rejeitada", str(w))

    status, _ = http("POST", f"/_chameleon/nfse-sp/nfse/{rps_reject}/authorize")
    check("authorize after a rejection -> 409", status == 409, str(status))

    http("POST", "/nfse-sp/nfse", {"valor": 250, "webhookUrl": hook(rps_ok), "rps": rps_ok})
    http("POST", "/_chameleon/clock/advance", {"seconds": 400})
    _, nfse = http("GET", f"/nfse-sp/nfse/{rps_ok}")
    ok = nfse.get("status") == "Autorizada" and str(nfse.get("protocolo", "")).startswith("SP-")
    check("TTL authorizes with an SP- protocol", ok, str(nfse))

    w = wait_webhook(f"/nfse/{rps_ok}")
    check("nfse.autorizada webhook delivered", w is not None and w["body"].get("evento") == "nfse.autorizada", str(w))


def step_sdk() -> None:
    print("\n6. Connector SDK (description, actions, webhook per key)")
    status, connectors = http("GET", "/_chameleon/connectors")
    names = sorted(c["name"] for c in connectors) if status == 200 else []
    check("description lists the connectors", names == ["bradesco", "inter", "nfse-sp", "pix", "zendesk"], str(names))
    bradesco = next((c for c in connectors if c["name"] == "bradesco"), {}) if status == 200 else {}
    pay = next((a for a in bradesco.get("actions", []) if a["name"] == "pay"), {})
    check("pay action allowed only from ATIVA", pay.get("allowedFrom") == ["ATIVA"], str(pay))
    check("provider routes in the description", "PUT /bradesco/v2/webhook/{chave}" in bradesco.get("routes", []),
          str(bradesco.get("routes")))

    tok = token()
    key = f"smoke-key-{RUN}"
    key_hook = f"http://{WEBHOOK_HOST}:{WEBHOOK_PORT}/pix-key/{RUN}"
    status, _ = http("PUT", f"/bradesco/v2/webhook/{key}", {"webhookUrl": key_hook}, tok)
    check("PUT /v2/webhook/{key} -> 204", status == 204, str(status))

    tx = txid(7)
    http("PUT", f"/bradesco/v2/cob/{tx}", {
        "calendario": {"expiracao": 3600}, "valor": {"original": "9.90"}, "chave": key}, tok)
    status, _ = http("POST", f"/_chameleon/connectors/bradesco/entities/cob/{tx}/actions/pay")
    check("generic pay action -> 200", status == 200, str(status))
    w = wait_webhook(f"/pix-key/{RUN}")
    check("webhook goes to the Pix key URL", w is not None and w["body"]["pix"][0]["txid"] == tx, str(w))

    status, conflict = http("POST", f"/_chameleon/connectors/bradesco/entities/cob/{tx}/actions/remove")
    check("action outside the lifecycle -> 409 with the reason", status == 409 and "CONCLUIDA" in str(conflict), f"{status} {conflict}")

    transitions = journal(tx, "StateTransition")
    last = transitions[-1] if transitions else {}
    check("transition with type, source and target",
          (last.get("entityType"), last.get("fromState"), last.get("toState")) == ("cob", "ATIVA", "CONCLUIDA")
          and len(transitions) == 2, str(transitions))

    tx_none = txid(8)
    http("PUT", f"/bradesco/v2/cob/{tx_none}", {
        "calendario": {"expiracao": 3600}, "valor": {"original": "1.00"}, "chave": f"no-hook-{RUN}"}, tok)
    status, _ = http("POST", f"/_chameleon/connectors/bradesco/entities/cob/{tx_none}/actions/pay")
    hooks = journal(tx_none, "OutboundWebhook")
    check("no webhook: transitions and records 'no webhook'",
          status == 200 and len(hooks) == 1 and "no webhook" in (hooks[0].get("detail") or ""), str(hooks))


def step_outbox() -> None:
    print("\n7. Webhook outbox and real time")
    sb = f"smoke-wh-{RUN}"
    http("POST", "/_chameleon/sandboxes", {"id": sb})
    status, _ = http("PUT", f"/_chameleon/sandboxes/{sb}/webhook-policy",
                     {"maxAttempts": 4, "baseDelayMs": 200, "timeoutMs": 3000})
    check("retry policy on the sandbox", status == 200, str(status))
    tok = sb_token(sb)
    hook = lambda path: f"http://{WEBHOOK_HOST}:{WEBHOOK_PORT}{path}"

    def pay(tx: str, url: str) -> None:
        register_hook(sb, tok, tx, url)
        http("PUT", f"/s/{sb}/bradesco/v2/cob/{tx}", {
            "calendario": {"expiracao": 3600}, "valor": {"original": "5.00"}, "chave": tx}, tok)
        http("POST", f"/s/{sb}/_chameleon/bradesco/cob/{tx}/pay")

    def delivery(tx: str, final: bool = True) -> dict:
        def get():
            _, items = http("GET", f"/s/{sb}/_chameleon/webhooks?entityId={tx}")
            items = items if isinstance(items, list) else []
            done = [d for d in items if d["status"] in ("Delivered", "DeadLettered")]
            return done[0] if done and final else None
        return until(get, timeout=15) or {}

    def attempts(d: dict) -> list[dict]:
        return http("GET", f"/s/{sb}/_chameleon/webhooks/{d.get('id')}")[1].get("attempts", []) if d else []

    tx = txid(9)
    pay(tx, hook(f"/flaky/{tx}"))
    d = delivery(tx)
    codes = [a.get("statusCode") for a in attempts(d)]
    check("app fails twice: delivered on the 3rd attempt", d.get("status") == "Delivered" and codes == [500, 500, 200], f"{d} {codes}")

    tx = txid(10)
    pay(tx, "http://169.254.169.254/latest/meta-data")
    d = delivery(tx)
    a = attempts(d)
    check("internal destination blocked without retrying",
          d.get("status") == "DeadLettered" and len(a) == 1 and a[0].get("blocked"), f"{d} {a}")

    status, _ = http("POST", f"/s/{sb}/_chameleon/webhooks/{d.get('id')}/resend", {"url": hook(f"/resend/{tx}")})
    w = wait_webhook(f"/resend/{tx}", timeout=10)
    check("resend to another destination delivered", status == 202 and w is not None, f"{status} {w}")

    tx = txid(11)
    http("POST", f"/s/{sb}/_chameleon/faults", {"connector": "bradesco", "route": "webhook", "kind": "Duplicate"})
    pay(tx, hook(f"/dup/{tx}"))
    until(lambda: hits.get(f"/dup/{tx}", 0) >= 2, timeout=10)
    check("armed fault duplicates the delivery", hits.get(f"/dup/{tx}") == 2, str(hits.get(f"/dup/{tx}")))

    rt = f"smoke-rt-{RUN}"
    http("POST", "/_chameleon/sandboxes", {"id": rt, "clockMode": "Realtime"})
    rt_tok = sb_token(rt)
    tx = txid(12)
    http("PUT", f"/s/{rt}/bradesco/v2/cob/{tx}", {"calendario": {"expiracao": 2}, "valor": {"original": "1.00"}, "chave": "k"}, rt_tok)
    gone = until(lambda: http("GET", f"/s/{rt}/bradesco/v2/cob/{tx}", token=rt_tok)[1].get("status") == "REMOVIDA_PELO_PSP", timeout=10)
    check("real time: the charge expires on its own", bool(gone), str(http("GET", f"/s/{rt}/bradesco/v2/cob/{tx}", token=rt_tok)[1]))


RS = "\x1e"  # SignalR record separator


class LiveFeed:
    """Minimal SignalR JSON client over long polling (stdlib only): joins one sandbox, collects events."""

    def __init__(self, sandbox: str):
        _, neg = http("POST", "/_chameleon/hub/negotiate?negotiateVersion=1")
        self.url = f"/_chameleon/hub?id={neg['connectionToken']}"
        self.events: list[dict] = []
        self._send({"protocol": "json", "version": 1})
        self._poll_once()  # handshake answer
        self._send({"type": 1, "target": "Join", "arguments": [sandbox], "invocationId": "1"})
        # The POST only queues the invocation: wait for its completion, or the first events race the group join.
        self.joined = False
        deadline = time.time() + 15
        while not self.joined and time.time() < deadline:
            self._poll_once()
        if not self.joined:
            raise RuntimeError(f"live feed did not join {sandbox}")
        self.alive = True
        threading.Thread(target=self._loop, daemon=True).start()

    def _send(self, msg: dict) -> None:
        req = urllib.request.Request(BASE + self.url, data=(json.dumps(msg) + RS).encode(), method="POST",
                                     headers={"Content-Type": "text/plain"})
        urllib.request.urlopen(req, timeout=15).read()

    def _poll_once(self) -> None:
        raw = urllib.request.urlopen(BASE + self.url, timeout=110).read().decode()
        for frame in filter(None, raw.split(RS)):
            msg = json.loads(frame)
            if msg.get("type") == 3 and msg.get("invocationId") == "1":
                if msg.get("error"):
                    raise RuntimeError(f"live feed join failed: {msg['error']}")
                self.joined = True
            elif msg.get("type") == 1 and msg.get("target") == "events":
                self.events.extend(msg["arguments"][0]["events"])

    def _loop(self) -> None:
        while self.alive:
            try:
                self._poll_once()
            except Exception:
                time.sleep(0.2)

    def close(self) -> None:
        self.alive = False
        try:
            urllib.request.urlopen(urllib.request.Request(BASE + self.url, method="DELETE"), timeout=5).read()
        except Exception:
            pass


def step_panel() -> None:
    print("\n8. Panel: Control API and live events")
    sb = f"smoke-live-{RUN}"
    other_sb = f"smoke-other-{RUN}"
    http("POST", "/_chameleon/sandboxes", {"id": sb})
    http("POST", "/_chameleon/sandboxes", {"id": other_sb})
    feed = LiveFeed(sb)
    other = LiveFeed(other_sb)
    tok = sb_token(sb)
    tx = txid(13)
    register_hook(sb, tok, tx, f"http://{WEBHOOK_HOST}:{WEBHOOK_PORT}/live/{tx}")
    http("PUT", f"/s/{sb}/bradesco/v2/cob/{tx}", {
        "calendario": {"expiracao": 3600}, "valor": {"original": "7.00"}, "chave": tx,
        "devedor": {"cpf": "12345678909", "nome": "Joao Silva"}}, tok)
    _, page = http("GET", f"/s/{sb}/_chameleon/interactions?limit=1")
    paused_at = page["items"][0]["seq"]
    http("POST", f"/s/{sb}/_chameleon/connectors/bradesco/entities/cob/{tx}/actions/pay")

    delivered = until(lambda: any(e["type"] == "webhookUpdated" and e["data"]["status"] == "Delivered" for e in feed.events), timeout=10)
    states = [e["data"]["state"] for e in feed.events if e["type"] == "entityChanged" and e["data"]["key"] == tx]
    check("live: call, state changes and delivery reach the feed",
          bool(delivered) and states == ["ATIVA", "CONCLUIDA"], f"{states} {[e['type'] for e in feed.events]}")
    check("live: another sandbox does not get the events", not any(tx in json.dumps(e) for e in other.events), "leaked to the other sandbox")
    feed.close()
    other.close()

    _, resumed = http("GET", f"/s/{sb}/_chameleon/interactions?afterSeq={paused_at}&limit=500")
    kinds = [i["kind"] for i in resumed.get("items", [])]
    check("feed resumes from the cursor, in order", kinds[:2] == ["ControlAction", "StateTransition"] and "OutboundWebhook" in kinds, str(kinds))

    _, entity = http("GET", f"/s/{sb}/_chameleon/connectors/bradesco/entities/cob/{tx}")
    check("entity with state, timeline and allowed actions",
          entity.get("state") == "CONCLUIDA" and entity.get("allowedActions") == [] and len(entity.get("timeline", [])) >= 4,
          str({k: entity.get(k) for k in ("state", "allowedActions")}))
    data = json.dumps(entity.get("data", {}))
    check("personal data masked in the entity", "12345678909" not in data and "Joao Silva" not in data, data)

    http("POST", f"/s/{sb}/_chameleon/faults", {"connector": "bradesco", "route": "webhook", "kind": "Duplicate", "times": 2})
    _, faults = http("GET", f"/s/{sb}/_chameleon/faults")
    check("armed faults listed", isinstance(faults, list) and len(faults) == 1 and faults[0]["remaining"] == 2, str(faults))

    status, page = http("GET", f"/ui/s/{sb}/entities/bradesco/cob/{tx}")
    check("web panel served at /ui (deep route falls back to index.html)",
          status == 200 and isinstance(page, str) and 'id="root"' in page and "/ui/assets/" in page, f"{status} {str(page)[:120]}")

    def mcp(name: str, arguments: dict) -> dict:
        req = urllib.request.Request(BASE + "/_chameleon/mcp", method="POST", data=json.dumps({
            "jsonrpc": "2.0", "id": 1, "method": "tools/call", "params": {"name": name, "arguments": arguments}}).encode(),
            headers={"Content-Type": "application/json", "Accept": "application/json, text/event-stream"})
        raw = urllib.request.urlopen(req, timeout=15).read().decode()
        data = next(line[5:] for line in raw.splitlines() if line.startswith("data:"))
        return json.loads(data)["result"]

    got = mcp("chameleon_get_entity", {"sandbox": sb, "connector": "bradesco", "entityType": "cob", "key": tx})
    entity = json.loads(got["content"][0]["text"]) if not got.get("isError") else {}
    check("MCP: the tool reads the entity through the Host itself", entity.get("state") == "CONCLUIDA", str(got)[:200])
    permanent = f"smoke-perm-{RUN}"
    http("POST", "/_chameleon/sandboxes", {"id": permanent, "permanent": True})
    refused = mcp("chameleon_reset_sandbox", {"sandbox": permanent})
    check("MCP: reset of a permanent sandbox refused", refused.get("isError") is True, str(refused)[:200])
    http("DELETE", f"/_chameleon/sandboxes/{permanent}?confirm={permanent}")


SANDBOX = f"smoke-{RUN}"


def subscribe(sandbox: str, connector: str = "bradesco") -> dict:
    """Subscribes the connector in the sandbox (already subscribed = 409, fine) and returns the generated credentials."""
    http("POST", f"/s/{sandbox}/_chameleon/connectors/{connector}/subscription")
    _, detail = http("GET", f"/s/{sandbox}/_chameleon/connectors/{connector}/subscription")
    if connector == "bradesco" and not detail.get("certificate"):
        # Subscription from before mTLS (old local data): rotating issues the certificate.
        http("POST", f"/s/{sandbox}/_chameleon/connectors/{connector}/subscription/rotate")
        _, detail = http("GET", f"/s/{sandbox}/_chameleon/connectors/{connector}/subscription")
    if cert := detail.get("certificate"):
        save_certificate(sandbox, cert)
    return {c["env"]: c["value"] for c in detail.get("credentials", [])}


def save_certificate(sandbox: str, cert: dict) -> None:
    files = (os.path.join(CERT_DIR, f"{sandbox}.crt"), os.path.join(CERT_DIR, f"{sandbox}.key"))
    with open(files[0], "w") as f:
        f.write(cert["pem"])
    if cert.get("keyPem"):
        with open(files[1], "w") as f:
            f.write(cert["keyPem"])
    with open(os.path.join(CERT_DIR, "ca.crt"), "w") as f:
        f.write(cert["caPem"])
    CERTS[sandbox] = files


def sb_token(sandbox: str) -> str:
    issued = subscribe(sandbox)
    _, body = bradesco_token(f"/s/{sandbox}", issued["CLIENT_ID"], issued["CLIENT_SECRET"])
    return body["access_token"]


def register_hook(sandbox: str, tok: str, key: str, url: str) -> None:
    """Once subscribed, Bradesco notifies only the webhook registered for the key, like the real PSP."""
    http("PUT", f"/s/{sandbox}/bradesco/v2/webhook/{key}", {"webhookUrl": url}, tok)


def step_sandboxes() -> None:
    print("\n9. Sandboxes")
    status, sb = http("POST", "/_chameleon/sandboxes", {"id": SANDBOX, "ttlSeconds": 3600})
    check("creates an ephemeral sandbox (201, with expiry)", status == 201 and sb.get("expiresAt"), f"{status} {sb}")

    qa_id, hml_id = f"smoke-qa-{RUN}", f"smoke-hml-{RUN}"
    for sid in (qa_id, hml_id):
        http("POST", "/_chameleon/sandboxes", {"id": sid, "permanent": True})
    tx = txid(5)
    qa, hml = sb_token(qa_id), sb_token(hml_id)
    body = {"calendario": {"expiracao": 3600}, "valor": {"original": "1.00"}, "chave": "k",
            "webhookUrl": f"http://{WEBHOOK_HOST}:{WEBHOOK_PORT}/sb/{tx}"}
    s_qa, _ = http("PUT", f"/s/{qa_id}/bradesco/v2/cob/{tx}", body, qa)
    s_hml, _ = http("PUT", f"/s/{hml_id}/bradesco/v2/cob/{tx}", body, hml)
    http("POST", f"/s/{qa_id}/_chameleon/bradesco/cob/{tx}/pay")
    _, c_qa = http("GET", f"/s/{qa_id}/bradesco/v2/cob/{tx}", token=qa)
    _, c_hml = http("GET", f"/s/{hml_id}/bradesco/v2/cob/{tx}", token=hml)
    check("two sandboxes: same key, separate charges",
          (s_qa, s_hml) == (201, 201) and c_qa.get("status") == "CONCLUIDA" and c_hml.get("status") == "ATIVA",
          f"{s_qa} {s_hml} {c_qa.get('status')} {c_hml.get('status')}")

    status, _ = http("GET", f"/s/{hml_id}/bradesco/v2/cob/{tx}", token=qa)
    check("token of one sandbox refused in another (401)", status == 401, str(status))

    status, _ = http("GET", f"/bradesco/v2/cob/{tx}", token=qa, headers={"X-Chameleon-Sandbox": qa_id})
    check("X-Chameleon-Sandbox header selects the sandbox", status == 200, str(status))

    status, _ = http("GET", "/s/does-not-exist/_chameleon/journal")
    check("unknown sandbox -> 404", status == 404, str(status))

    status, refused = bradesco_token(f"/s/{SANDBOX}", "x")
    check("unsubscribed connector refused outside default (404)",
          status == 404 and "not subscribed" in json.dumps(refused), f"{status} {refused}")
    issued = subscribe(SANDBOX)
    status, _ = bradesco_token(f"/s/{SANDBOX}", issued.get("CLIENT_ID"), "wrong")
    check("subscribed: only the generated credentials issue a token", status == 401 and issued.get("BASE_URL", "").endswith(f"/s/{SANDBOX}/bradesco"),
          f"{status} {issued.get('BASE_URL')}")

    tok = sb_token(SANDBOX)
    http("POST", f"/s/{SANDBOX}/_chameleon/faults",
         {"connector": "bradesco", "route": "PUT /bradesco/v2/cob/{txid}", "kind": "Error", "status": 503})
    s_default, _ = create_cob(txid(6), token())
    s_sb, _ = http("PUT", f"/s/{SANDBOX}/bradesco/v2/cob/{txid(6)}", body, tok)
    check("armed fault only in its own sandbox", (s_default, s_sb) == (201, 503), f"{s_default}, {s_sb}")

    http("POST", f"/s/{SANDBOX}/_chameleon/faults", {"connector": "bradesco", "route": "PUT /bradesco/v2/cob/{txid}",
                                                      "kind": "Error", "status": 503, "durationSeconds": 300})
    timed = [http("PUT", f"/s/{SANDBOX}/bradesco/v2/cob/{txid(30 + i)}", body, tok)[0] for i in range(3)]
    http("POST", f"/s/{SANDBOX}/_chameleon/clock/advance", {"seconds": 300})
    tok = sb_token(SANDBOX)
    after, _ = http("PUT", f"/s/{SANDBOX}/bradesco/v2/cob/{txid(33)}", body, tok)
    check("timed fault: fails every call until the clock passes the deadline", timed == [503, 503, 503] and after == 201, f"{timed} {after}")

    status, _ = http("POST", f"/_chameleon/sandboxes/{qa_id}/reset")
    check("reset of a permanent sandbox without confirmation -> 409", status == 409, str(status))

    copy = f"{SANDBOX}-cp"
    status, cp = http("POST", "/_chameleon/sandboxes", {"id": copy, "from": qa_id, "ttlSeconds": 3600})
    CERTS[copy] = CERTS[qa_id]  # the copy carries the same certificate
    _, j_cp = http("GET", f"/s/{copy}/_chameleon/journal")
    _, c_cp = http("GET", f"/s/{copy}/bradesco/v2/cob/{tx}", token=qa)
    check("copy of a sandbox: same charges, empty history",
          status == 201 and c_cp.get("status") == "CONCLUIDA" and j_cp == [], f"{status} {c_cp.get('status')} {len(j_cp)}")
    http("POST", f"/s/{copy}/_chameleon/clock/advance", {"seconds": 600})
    _, now = http("POST", f"/_chameleon/sandboxes/{copy}/clock/now")
    check("Now: the clock goes back to real time, running",
          now.get("clockMode") == "Realtime" and abs(now.get("driftSeconds", 99)) <= 1, str(now))
    http("DELETE", f"/_chameleon/sandboxes/{copy}")
    for sid in (qa_id, hml_id):
        http("DELETE", f"/_chameleon/sandboxes/{sid}?confirm={sid}")

    http("POST", f"/s/{SANDBOX}/_chameleon/clock/advance", {"seconds": 86400})


def step_persistence() -> None:
    print("\n10. Persistence (container restart)")
    tx = txid(1)
    before = len(journal(tx))
    if OIDC:  # step_sandboxes does not run with --oidc: create the sandbox whose clock must survive
        http("POST", "/_chameleon/sandboxes", {"id": SANDBOX, "ttlSeconds": 3600})
    status, sb_before = http("GET", f"/_chameleon/sandboxes/{SANDBOX}")
    check("sandbox exists before the restart", status == 200 and bool(sb_before.get("now")), f"{status} {sb_before}")
    # By service name: the container name depends on the checkout folder (compose project name).
    files = ["-f", "docker-compose.yml", "-f", "docker-compose.oidc.yml"] if OIDC else []
    r = run(["docker", "compose", *files, "restart", "chameleon"], timeout=120)
    if r.returncode != 0:
        check("docker restart", False, r.stderr.strip())
        return
    check("API is back after the restart", wait_ready())
    after = len(journal(tx))
    # With --oidc step_bradesco does not run, so the journal of its charge is empty on both sides.
    check("journal preserved in Postgres", after == before and (after > 0 or OIDC), f"before={before} after={after}")
    _, sb_after = http("GET", f"/_chameleon/sandboxes/{SANDBOX}")
    check("sandbox and advanced clock preserved",
          isinstance(sb_after, dict) and sb_after.get("now") == sb_before.get("now"), f"{sb_before} -> {sb_after}")
    http("DELETE", f"/_chameleon/sandboxes/{SANDBOX}")


def step_accounts() -> None:
    print("\n9b. Accounts and balances, and holding a delivery")
    tok = token()
    nickname, key = f"smoke-{RUN}".lower(), f"smoke-account-{RUN}"
    base = "/_chameleon/connectors/bradesco/accounts"
    status, _ = http("POST", base, {"nickname": nickname, "branch": "1234", "account": str(int(RUN, 16))[-8:], "pixKey": key})
    check("opens an account (201)", status == 201, str(status))
    status, _ = http("POST", base, {"nickname": nickname + "-2", "branch": "9999", "account": "1", "pixKey": key})
    check("repeated Pix key -> 409", status == 409, str(status))
    http("PUT", f"{base}/{nickname}/balance", {"balance": 1000})

    tx = txid(11)
    http("PUT", f"/bradesco/v2/cob/{tx}", {"calendario": {"expiracao": 3600}, "valor": {"original": "25.50"}, "chave": key}, tok)
    http("POST", f"/_chameleon/connectors/bradesco/entities/cob/{tx}/actions/pay")
    _, account = http("GET", f"/_chameleon/accounts/{nickname}")
    kinds = [e["kind"] for e in account.get("entries", [])]
    check("received Pix credits the account that owns the key", account.get("balance") == "1025.50" and kinds == ["Credit", "Adjustment"], str(account))

    status, _ = http("POST", "/_chameleon/faults", {"connector": "bradesco", "route": "webhook", "kind": "Hold"})
    check("arms a delivery hold", status == 200, str(status))
    tx = txid(12)
    create_cob(tx, tok)
    http("POST", f"/_chameleon/connectors/bradesco/entities/cob/{tx}/actions/pay")
    check("delivery withheld while held", wait_webhook(f"/pix/{tx}", timeout=2.5) is None)
    http("DELETE", "/_chameleon/faults?connector=bradesco&route=webhook")
    check("disarmed, the delivery arrives", wait_webhook(f"/pix/{tx}", timeout=5) is not None)


def step_mtls() -> None:
    print("\n9c. Client certificate (mTLS)")
    a, b = f"smoke-a-{RUN}", f"smoke-b-{RUN}"
    for sb in (a, b):
        http("POST", "/_chameleon/sandboxes", {"id": sb, "ttlSeconds": 3600})
    issued = subscribe(a)
    subscribe(b)
    status, _ = bradesco_token(f"/s/{a}", issued["CLIENT_ID"], issued["CLIENT_SECRET"])
    check("with the sandbox certificate, over the mTLS port: 200", status == 200, str(status))

    def raw(url: str, context: ssl.SSLContext | None) -> str:
        req = urllib.request.Request(url, data=b"{}", method="POST", headers={"Content-Type": "application/json"})
        try:
            with urllib.request.urlopen(req, timeout=10, context=context) as resp:
                return str(resp.status)
        except urllib.error.HTTPError as e:
            return str(e.code)
        except Exception as e:  # handshake or dropped connection
            return type(e).__name__

    no_cert = raw(f"{MTLS}/s/{a}/bradesco/v2/oauth/token", tls_context(None))
    check("mTLS port without a certificate: handshake refused", not no_cert.isdigit(), no_cert)
    plain = raw(f"{BASE}/s/{a}/bradesco/v2/oauth/token", None)
    check("plain port without a certificate: connection dropped", not plain.isdigit(), plain)
    foreign = raw(f"{MTLS}/s/{b}/bradesco/v2/oauth/token", tls_context(a))
    check("certificate of another sandbox: 403", foreign == "403", foreign)
    status, ca = http("GET", "/_chameleon/ca.crt")
    check("Chameleon CA available for download", status == 200 and "BEGIN CERTIFICATE" in str(ca), str(status))


def step_inter() -> None:
    print("\n10. Inter: OAuth by scope, Pix cob and Cobranca v3 (mTLS)")
    sb = f"smoke-inter-{RUN}"
    http("POST", "/_chameleon/sandboxes", {"id": sb, "ttlSeconds": 3600})
    issued = subscribe(sb, "inter")
    check("Inter subscription issues a certificate", sb in CERTS)
    form = {"client_id": issued["CLIENT_ID"], "client_secret": issued["CLIENT_SECRET"], "grant_type": "client_credentials",
            "scope": "cob.read cob.write pix.write boleto-cobranca.read boleto-cobranca.write"}
    status, tok = http("POST", f"/s/{sb}/inter/oauth/v2/token", form=form)
    check("form-urlencoded token over the mTLS port", status == 200 and "access_token" in tok, f"{status} {tok}")
    tok = tok.get("access_token") if isinstance(tok, dict) else None
    status, _ = http("PUT", f"/s/{sb}/inter/pix/v2/webhook/chave-inter", {"webhookUrl": "https://app.local/hook"}, tok)
    check("without the webhook.write scope: 403", status == 403, str(status))

    # The local receiver is http: register through the control API (the provider API requires https).
    pix_hook = f"http://{WEBHOOK_HOST}:{WEBHOOK_PORT}/inter-pix/{RUN}"
    boleto_hook = f"http://{WEBHOOK_HOST}:{WEBHOOK_PORT}/inter-boleto/{RUN}"
    http("PUT", f"/s/{sb}/_chameleon/connectors/inter/webhook-registrations/chave-inter", {"url": pix_hook})
    http("PUT", f"/s/{sb}/_chameleon/connectors/inter/webhook-registrations/cobranca", {"url": boleto_hook})
    http("POST", f"/s/{sb}/_chameleon/connectors/inter/accounts", {"nickname": "inter-op", "branch": "0001", "account": "1234567-8"})

    status, cob = http("POST", f"/s/{sb}/inter/pix/v2/cob",
                       {"calendario": {"expiracao": 3600}, "valor": {"original": "12.34"}, "chave": "chave-inter",
                        "devedor": {"cpf": "12345678909", "nome": "Maria Souza"}}, tok)
    check("POST /cob generates a txid", status == 201 and cob.get("txid"), f"{status} {cob}")
    status, paid = http("POST", f"/s/{sb}/inter/pix/v2/cob/pagar/{cob.get('txid')}", None, tok)
    check("paying a cob in the sandbox: 201 with e2e", status == 201 and str(paid.get("e2e", "")).startswith("E"), f"{status} {paid}")
    hook = wait_webhook(f"/inter-pix/{RUN}")
    pix = (hook or {}).get("body", {}).get("pix", [{}])[0]
    check("Pix callback with the payer", pix.get("pagador", {}).get("nome") == "Maria Souza"
          and "12345678909" not in json.dumps(pix), str(pix))

    body = {"seuNumero": f"S{RUN}"[:15], "valorNominal": 100.0, "dataVencimento": time.strftime("%Y-%m-%d", time.gmtime(time.time() + 5 * 86400)),
            "pagador": {"cpfCnpj": "12345678909", "tipoPessoa": "FISICA", "nome": "Maria Souza", "endereco": "Rua A",
                        "cidade": "Sao Paulo", "uf": "SP", "cep": "01000000"}}
    status, emitted = http("POST", f"/s/{sb}/inter/cobranca/v3/cobrancas", body, tok, {"x-conta-corrente": "12345678"})
    codigo = emitted.get("codigoSolicitacao") if isinstance(emitted, dict) else None
    check("issues a boleto: only codigoSolicitacao", status == 200 and codigo, f"{status} {emitted}")
    _, again = http("POST", f"/s/{sb}/inter/cobranca/v3/cobrancas", body, tok, {"x-conta-corrente": "12345678"})
    check("same issue within 30 min: same code", again.get("codigoSolicitacao") == codigo, str(again))
    _, got = http("GET", f"/s/{sb}/inter/cobranca/v3/cobrancas/{codigo}", token=tok)
    check("starts as EM_PROCESSAMENTO", got.get("cobranca", {}).get("situacao") == "EM_PROCESSAMENTO", str(got))
    http("POST", f"/s/{sb}/_chameleon/clock/advance", {"seconds": 1})
    _, got = http("GET", f"/s/{sb}/inter/cobranca/v3/cobrancas/{codigo}", token=tok)
    check("when the clock advances it becomes A_RECEBER with a digitable line",
          got.get("cobranca", {}).get("situacao") == "A_RECEBER" and len(got.get("boleto", {}).get("linhaDigitavel", "")) == 47, str(got))
    status, _ = http("POST", f"/s/{sb}/inter/cobranca/v3/cobrancas/{codigo}/pagar", {"pagarCom": "BOLETO"}, tok)
    check("paying a boleto in the sandbox: 204", status == 204, str(status))
    paid_hook = until(lambda: [w for w in received if w["path"] == f"/inter-boleto/{RUN}"
                               and w["body"] and w["body"][0].get("situacao") == "RECEBIDO"], timeout=5)
    check("RECEBIDO charge callback (array)", bool(paid_hook), str([w["body"] for w in received if "inter-boleto" in w["path"]]))
    _, account = http("GET", f"/s/{sb}/_chameleon/accounts/inter-op")
    check("boleto account credited (100.00)", account.get("balance") == "100.00", str(account))


def step_inter_cobv() -> None:
    print("\n10b. Inter: Pix with due date (loc, cobv, pay with amount, received Pix, refund)")
    sb = f"smoke-inter-cobv-{RUN}"
    http("POST", "/_chameleon/sandboxes", {"id": sb, "ttlSeconds": 3600})
    issued = subscribe(sb, "inter")
    scopes = ("cobv.read cobv.write payloadlocation.read payloadlocation.write pix.read pix.write "
              "pagamento-pix.write extrato.read boleto-cobranca.write boleto-cobranca.read")
    form = {"client_id": issued["CLIENT_ID"], "client_secret": issued["CLIENT_SECRET"], "grant_type": "client_credentials", "scope": scopes}
    status, tok = http("POST", f"/s/{sb}/inter/oauth/v2/token", form=form)
    check("token with the 10 scopes", status == 200 and tok.get("scope") == scopes, f"{status} {tok}")
    tok = tok.get("access_token") if isinstance(tok, dict) else None
    pix_hook = f"http://{WEBHOOK_HOST}:{WEBHOOK_PORT}/inter-cobv/{RUN}"
    http("PUT", f"/s/{sb}/_chameleon/connectors/inter/webhook-registrations/chave-cobv", {"url": pix_hook})

    status, loc = http("POST", f"/s/{sb}/inter/pix/v2/loc", {"tipoCob": "cobv"}, tok)
    check("loc with an integer id", status == 201 and isinstance(loc.get("id"), int), f"{status} {loc}")
    txid = f"{RUN}chameleoncobv".ljust(35, "0")[:35]
    vencimento = time.strftime("%Y-%m-%d", time.gmtime(time.time() + 86400))
    status, cobv = http("PUT", f"/s/{sb}/inter/pix/v2/cobv/{txid}",
                        {"calendario": {"dataDeVencimento": vencimento}, "loc": loc, "chave": "chave-cobv", "valor": {"original": "50.00"},
                         "devedor": {"cpf": "12345678909", "nome": "Maria Souza", "cidade": "Sao Paulo", "uf": "SP", "cep": "01000000"}}, tok)
    check("cobv created on the loc", status == 201 and cobv.get("loc", {}).get("id") == loc.get("id"), f"{status} {cobv}")
    status, paid = http("POST", f"/s/{sb}/inter/pix/v2/cobv/pagar/{txid}", {"valor": 45.5}, tok)
    e2e = paid.get("e2e") if isinstance(paid, dict) else None
    check("paying a cobv with another amount: 201 with e2e", status == 201 and str(e2e).startswith("E"), f"{status} {paid}")
    _, pix = http("GET", f"/s/{sb}/inter/pix/v2/pix/{e2e}", token=tok)
    check("received Pix with the paid amount", pix.get("valor") == "45.50" and pix.get("txid") == txid, str(pix))
    status, dev = http("PUT", f"/s/{sb}/inter/pix/v2/pix/{e2e}/devolucao/smoke{RUN}", {"valor": "4.50"}, tok)
    check("synchronous refund DEVOLVIDO", status == 201 and dev.get("status") == "DEVOLVIDO", f"{status} {dev}")
    refunded = until(lambda: [w for w in received if w["path"] == f"/inter-cobv/{RUN}"
                              and w["body"].get("pix", [{}])[0].get("devolucoes")], timeout=5)
    check("key webhook with devolucoes[]", bool(refunded), str([w["body"] for w in received if "inter-cobv" in w["path"]]))


def step_inter_banking() -> None:
    print("\n10c. Inter Banking: balance, statement, outgoing Pix and boleto with settlement")
    sb = f"smoke-bank-{RUN}"
    http("POST", "/_chameleon/sandboxes", {"id": sb, "ttlSeconds": 3600, "clockMode": "Realtime"})
    issued = subscribe(sb, "inter")
    form = {"client_id": issued["CLIENT_ID"], "client_secret": issued["CLIENT_SECRET"], "grant_type": "client_credentials",
            "scope": "extrato.read pagamento-pix.write pagamento-pix.read pagamento-boleto.write pagamento-boleto.read"}
    _, tok = http("POST", f"/s/{sb}/inter/oauth/v2/token", form=form)
    tok = tok.get("access_token") if isinstance(tok, dict) else None
    http("POST", f"/s/{sb}/_chameleon/connectors/inter/accounts", {"nickname": "payments", "branch": "0001", "account": "7654321", "balance": 1000})
    pix_hook = f"http://{WEBHOOK_HOST}:{WEBHOOK_PORT}/inter-pix-out/{RUN}"
    http("PUT", f"/s/{sb}/_chameleon/connectors/inter/webhook-registrations/pix-pagamento", {"url": pix_hook})

    _, saldo = http("GET", f"/s/{sb}/inter/banking/v2/saldo", token=tok)
    check("balance through the Inter API", saldo.get("disponivel") == 1000, str(saldo))
    status, pix = http("POST", f"/s/{sb}/inter/banking/v2/pix", {"valor": 300, "destinatario": {"tipo": "CHAVE", "chave": "external"}}, tok)
    codigo = pix.get("codigoSolicitacao") if isinstance(pix, dict) else None
    check("outgoing Pix answers APROVACAO", status == 200 and pix.get("tipoRetorno") == "APROVACAO", f"{status} {pix}")
    done = until(lambda: http("GET", f"/s/{sb}/inter/banking/v2/pix/{codigo}", token=tok)[1]
                 .get("transacaoPix", {}).get("status") == "EFETIVADO", timeout=12)
    check("real time: settles on its own on schedule (5 s)", bool(done))
    _, saldo = http("GET", f"/s/{sb}/inter/banking/v2/saldo", token=tok)
    check("balance debited (700)", saldo.get("disponivel") == 700, str(saldo))
    hook = wait_webhook(f"/inter-pix-out/{RUN}", timeout=5)
    check("cashout callback (EFETIVADO, endToEnd)", bool(hook) and hook["body"][0].get("status") == "EFETIVADO"
          and str(hook["body"][0].get("endToEnd", "")).startswith("E"), str(hook))

    status, _ = http("POST", f"/s/{sb}/inter/banking/v2/pix", {"valor": 5000, "destinatario": {"tipo": "CHAVE", "chave": "x"}}, tok)
    check("insufficient balance: 422", status == 422, str(status))

    status, _ = http("POST", f"/s/{sb}/_chameleon/faults", {"connector": "inter", "route": "settlement", "kind": "Hold"})
    check("arms Hold on settlement", status == 200, str(status))
    _, held = http("POST", f"/s/{sb}/inter/banking/v2/pagamento",
                   {"codBarraLinhaDigitavel": "1" * 44, "valorPagar": 50, "dataVencimento": time.strftime("%Y-%m-%d")}, tok)
    transacao = held.get("codigoTransacao") if isinstance(held, dict) else None
    time.sleep(7)
    _, listed = http("GET", f"/s/{sb}/inter/banking/v2/pagamento?dataInicio=2020-01-01&dataFim=2099-12-31", token=tok)
    status_of = lambda rows: next((r.get("statusPagamento") for r in rows if r.get("codigoTransacao") == transacao), None) if isinstance(rows, list) else None
    check("Hold: the boleto stays APROVADO after the deadline", status_of(listed) == "APROVADO", str(listed))
    http("DELETE", f"/s/{sb}/_chameleon/faults?connector=inter&route=settlement")
    paid = until(lambda: status_of(http("GET", f"/s/{sb}/inter/banking/v2/pagamento?dataInicio=2020-01-01&dataFim=2099-12-31", token=tok)[1]) == "REALIZADO", timeout=8)
    check("disarmed: settles on the next cycle", bool(paid))
    _, extrato = http("GET", f"/s/{sb}/inter/banking/v2/extrato?dataInicio=2020-01-01&dataFim=2099-12-31", token=tok)
    kinds = [(t.get("tipoOperacao"), t.get("valor")) for t in extrato.get("transacoes", [])] if isinstance(extrato, dict) else []
    check("statement with adjustment, Pix and boleto", kinds == [("C", "1000.00"), ("D", "300.00"), ("D", "50.00")], str(kinds))


def step_pix() -> None:
    print("\n10d. Pix (Central Bank standard): token, cob, pay, webhook, refund")
    key, tx = f"00000000-0000-4000-8000-{RUN[:10]}01", txid(50)
    hook = f"/pix-generic/{tx}"
    basic = base64.b64encode(b"smoke:secret").decode()
    status, tok = http("POST", "/pix/oauth/token", headers={"Authorization": f"Basic {basic}"},
                       form={"grant_type": "client_credentials", "scope": "cob.read cob.write webhook.write pix.read pix.write foo"})
    check("pix token grants only the known scopes requested", status == 200 and "foo" not in tok.get("scope", "") and "cob.write" in tok.get("scope", ""), f"{status} {tok}")
    bearer = tok.get("access_token")

    status, _ = http("PUT", f"/pix/v2/webhook/{key}", {"webhookUrl": f"http://{WEBHOOK_HOST}:{WEBHOOK_PORT}{hook}"}, bearer)
    check("pix webhook accepts a plain http url", status == 204, str(status))
    status, cob = http("PUT", f"/pix/v2/cob/{tx}", {
        "calendario": {"expiracao": 3600}, "devedor": {"cpf": "12345678909", "nome": "Joao Silva"},
        "valor": {"original": "20.00"}, "chave": key}, bearer)
    check("pix PUT creates an ATIVA charge", status == 201 and cob.get("status") == "ATIVA", f"{status} {cob}")

    status, _ = http("POST", f"/_chameleon/connectors/pix/entities/cob/{tx}/actions/pay")
    check("pix pay action returns 200", status == 200, str(status))
    got = wait_webhook(hook)
    check("pix webhook delivered with the txid", got is not None and got["body"]["pix"][0]["txid"] == tx, str(got))

    _, paid = http("GET", f"/pix/v2/cob/{tx}", token=bearer)
    e2e = (paid.get("pix") or [{}])[0].get("endToEndId", "")
    status, dev = http("PUT", f"/pix/v2/pix/{e2e}/devolucao/{uuid.uuid4().hex}", {"valor": "5.00"}, bearer)
    check("pix refund is created", status == 201 and dev.get("valor") == "5.00", f"{status} {dev}")


def step_zendesk() -> None:
    print("\n11. Zendesk: ticket, comment, search, resolve through an action, signed webhook")
    sb = f"smoke-zendesk-{RUN}"
    http("POST", "/_chameleon/sandboxes", {"id": sb, "ttlSeconds": 3600})
    issued = subscribe(sb, "zendesk")
    basic = base64.b64encode(f"{issued.get('EMAIL')}/token:{issued.get('API_TOKEN')}".encode()).decode()
    auth = {"Authorization": f"Basic {basic}"}
    hook = f"/zendesk/{RUN}"
    http("PUT", f"/s/{sb}/_chameleon/connectors/zendesk/webhook-registrations/status_changed",
         {"url": f"http://{WEBHOOK_HOST}:{WEBHOOK_PORT}{hook}"})

    external = f"C-{RUN}"
    status, created = http("POST", f"/s/{sb}/zendesk/api/v2/tickets", {"ticket": {
        "subject": "Smoke case", "comment": {"body": "The amount differs from the agreed one", "public": True},
        "requester": {"name": "Acme Customer", "email": "requester@example.com"}, "external_id": external,
        "ticket_form_id": 910000000200, "tags": ["sample_case"],
        "custom_fields": [{"id": 910000000005, "value": "case_status_in_progress"}]}}, headers=auth)
    ticket = created.get("ticket", {}) if isinstance(created, dict) else {}
    tid = ticket.get("id")
    check("ticket created (201) with the case status tag", status == 201
          and "case_status_in_progress" in ticket.get("tags", []), f"{status} {str(created)[:200]}")
    status, _ = http("PUT", f"/s/{sb}/zendesk/api/v2/tickets/{tid}", {"ticket": {"comment": {"body": "Note", "public": False}}}, headers=auth)
    _, last = http("GET", f"/s/{sb}/zendesk/api/v2/tickets/{tid}/comments?sort_order=desc&per_page=1", headers=auth)
    check("private comment is the last one", status == 200 and isinstance(last, dict)
          and last.get("comments", [{}])[0].get("public") is False, str(last)[:200])
    _, found = http("GET", f"/s/{sb}/zendesk/api/v2/search?query=" + urllib.parse.quote(f"type:ticket external_id:{external}"), headers=auth)
    check("search by external_id", isinstance(found, dict) and found.get("count") == 1, str(found)[:200])
    status, _ = http("GET", f"/s/{sb}/zendesk/api/v2/tickets/{tid}", headers={"Authorization": "Basic eDp5"})
    check("wrong credential: 401", status == 401, str(status))

    http("POST", f"/s/{sb}/_chameleon/connectors/zendesk/entities/ticket/{tid}/actions/resolve",
         {"outcome": "accepted", "comment": "Accepted"})
    w = wait_webhook(hook)
    body, headers = (w or {}).get("body", {}), (w or {}).get("headers", {})
    expected = base64.b64encode(hmac.new(issued.get("WEBHOOK_SECRET", "").encode(),
                                         headers.get("x-zendesk-webhook-signature-timestamp", "").encode() + (w or {}).get("raw", b""),
                                         hashlib.sha256).digest()).decode()
    check("status_changed webhook SOLVED with the accepted tag", body.get("event", {}).get("current") == "SOLVED"
          and "case_status_accepted" in body.get("detail", {}).get("tags", []), str(body)[:300])
    check("signature matches the Zendesk formula", headers.get("x-zendesk-webhook-signature") == expected,
          str(headers)[:200])


def step_bradesco_full() -> None:
    print("\n12. Full Bradesco: payer, simulation, cobv, refund, SPI, TED, balance, statement")
    sb = f"smoke-brad-{RUN}"
    http("POST", "/_chameleon/sandboxes", {"id": sb, "ttlSeconds": 3600})
    issued = subscribe(sb)
    _, body = bradesco_token(f"/s/{sb}", issued["CLIENT_ID"], issued["CLIENT_SECRET"])
    tok = body.get("access_token") if isinstance(body, dict) else None
    _, body = http("POST", f"/s/{sb}/bradesco/auth/server-mtls/v2/token", form={
        "grant_type": "client_credentials", "client_id": issued["CLIENT_ID"], "client_secret": issued["CLIENT_SECRET"]})
    account_tok = body.get("access_token") if isinstance(body, dict) else None
    check("account and TED token (form)", bool(account_tok), str(body)[:200])

    key, evp = f"wallet-{RUN}@chameleon.test", str(uuid.uuid4())
    http("POST", f"/s/{sb}/_chameleon/connectors/bradesco/accounts",
         {"nickname": "wallet", "branch": "0001", "account": "0012345", "pixKey": key, "balance": 100})
    http("POST", f"/s/{sb}/_chameleon/connectors/bradesco/accounts",
         {"nickname": "target", "branch": "1234", "account": "98765", "pixKey": evp, "balance": 0})
    hook = f"/bradesco-full/{RUN}"
    http("PUT", f"/s/{sb}/_chameleon/connectors/bradesco/webhook-registrations/{key}", {"url": f"http://{WEBHOOK_HOST}:{WEBHOOK_PORT}{hook}"})

    tx = uuid.uuid4().hex
    status, cob = http("PUT", f"/s/{sb}/bradesco/v2/cob/{tx}", {
        "chave": key, "calendario": {"expiracao": 1800}, "valor": {"original": "20.00", "modalidadeAlteracao": 0}}, tok)
    check("cob with a strictly typed body (201)", status == 201 and isinstance(cob.get("revisao"), int)
          and cob.get("valor", {}).get("modalidadeAlteracao") == 0, f"{status} {str(cob)[:200]}")
    status, _ = http("POST", f"/s/{sb}/bradesco/simulate/pix-payment-by-emv", {
        "emv": cob.get("pixCopiaECola"), "valor": 15.0, "pagador": {"cpf": "12345678909", "nome": "Simulated Payer"}, "infoPagador": "smoke"}, tok)
    _, got = http("GET", f"/s/{sb}/bradesco/v2/cob/{tx}", token=tok)
    pix = (got.get("pix") or [{}])[0] if isinstance(got, dict) else {}
    check("simulation pays a different amount with a payer", status == 200 and pix.get("valor") == "15.00"
          and pix.get("pagador", {}).get("cpf") == "12345678909", f"{status} {str(got)[:200]}")
    w = wait_webhook(hook)
    sent = (w or {}).get("body", {}).get("pix", [{}])[0]
    check("Bacen webhook with componentesValor and infoPagador", sent.get("infoPagador") == "smoke"
          and sent.get("componentesValor", {}).get("original", {}).get("valor") == "15.00", str(w)[:200])

    status, problem = http("PUT", f"/s/{sb}/bradesco/v2/cobv/{txid(12)}", {
        "chave": key, "calendario": {"dataDeVencimento": "", "validadeAposVencimento": None},
        "valor": {"original": 20.0, "multa": None}, "solicitacaoPagador": "Smoke"}, tok)
    check("cobv with empty fields: 400 with violations", status == 400 and len(problem.get("violacoes", [])) == 3, f"{status} {str(problem)[:200]}")

    e2e = pix.get("endToEndId")
    status, dev = http("PUT", f"/s/{sb}/bradesco/v2/pix/{e2e}/devolucao/{uuid.uuid4().hex}", {"valor": "5.00"}, tok)
    check("synchronous refund DEVOLVIDO", status == 201 and dev.get("status") == "DEVOLVIDO", f"{status} {dev}")
    status, _ = http("PUT", f"/s/{sb}/bradesco/pix/{e2e}/devolucao/{uuid.uuid4().hex}", {"valor": "5,00"}, tok)
    check("refund route without v2: 404", status == 404, str(status))

    def spi(target_key: str, key_type: str = "EVP") -> tuple[int, dict]:
        return http("POST", f"/s/{sb}/bradesco/v1/spi/solicitar-transferencia", {
            "idtransacao": uuid.uuid4().hex, "pagador": {"tipoChave": "AGENCIACONTA", "agencia": "0001", "conta": "0012345"},
            "recebedor": {"tipoChave": key_type, "chavePix": target_key}, "valor": "30.00", "descricao": "Transfer"}, tok)
    status, t = spi(evp)
    check("synchronous SPI CONCLUIDO with e2e", status == 200 and t.get("status") == "CONCLUIDO" and str(t.get("e2e", "")).startswith("E60746948"),
          f"{status} {t}")
    status, t = spi(str(uuid.uuid4()))
    check("SPI key outside the DICT: 412", status == 412 and "DICT" in t.get("detail", ""), f"{status} {t}")
    status, t = spi("12345678909", "TELEFONE")
    check("SPI CPF as TELEFONE: 400 on the recipient", status == 400
          and t.get("violacoes", [{}])[0].get("propriedade") == "solicitacao.recebedor.tipoChave", f"{status} {t}")

    today = time.strftime("%d.%m.%Y", time.gmtime(time.time() - 3 * 3600))

    def send_ted() -> tuple[int, dict]:
        return http("POST", f"/s/{sb}/bradesco/transferencia/ted/v1/efetiva", {
            "identificadorDoTipoDeTransferencia": 1, "agenciaRemetente": 1, "contaRemetenteComDigito": 12345, "tipoContaRemetente": "CC",
            "tipoDePessoaRemetente": "J", "nomeClienteRemetente": "ACME LTDA", "bancoDestinatario": 341, "agenciaDestinatario": 1234,
            "contaDestinatario": 987654, "tipoDeContaDestinatario": "CC", "tipodePessoaDestinatario": "J", "nomeClienteDestinatario": "TARGET",
            "numeroInscricao": "123456789", "numeroFilial": "00024", "numeroControle": "81", "valorDaTransferencia": 10.5,
            "finalidadeDaTransferencia": 5, "tipoDeDoc": "E", "dataMovimento": today,
            "codigoIdentificadorDaTransferencia": uuid.uuid4().hex.upper()[:25], "canalPagamento": 0, "indicadorDda": "N"}, account_tok)

    def ted_lookup(ted: dict) -> dict:
        number = int(str(ted.get("chaveUnicaParaApi", "0000000"))[:7])
        return http("GET", f"/s/{sb}/bradesco/transferencia/ted/v1/consulta?numeroDocumento={number}&dataOperacao={today}", token=account_tok)[1]

    status, ted = send_ted()
    check("TED effective codigoDeRetorno 23 (number)", status == 200 and ted.get("codigoDeRetorno") == 23, f"{status} {ted}")
    lookup = ted_lookup(ted)
    check("TED lookup: automatic mode answers PROCESSADA, codigoRetorno string", lookup.get("statusMensagem") == "PROCESSADA"
          and isinstance(lookup.get("codigoRetorno"), str), str(lookup))
    status, _ = http("GET", f"/s/{sb}/bradesco/transferencia/ted/v1/consulta?numeroDocumento=1&dataOperacao={today}", token=account_tok)
    check("TED lookup of an unknown document: 422", status == 422, str(status))

    _, saldo = http("GET", f"/s/{sb}/bradesco/v1/fornecimento-saldos-contas/saldos?agencia=0001&conta=0012345", token=account_tok)
    item = (saldo.get("saldoCC", {}).get("lstLancamentosSaldos") or [{}])[0] if isinstance(saldo, dict) else {}
    # 100 + 15 (Pix) - 5 (refund) - 30 (SPI) - 10.50 (TED)
    check("SALDO TOTAL balance in pt-BR format (69,50)", item.get("nomeProduto") == "SALDO TOTAL" and item.get("valorLancamento") == "69,50", str(saldo)[:200])
    day = time.strftime("%d%m%Y", time.gmtime(time.time() - 3 * 3600))
    _, extrato = http("GET", f"/s/{sb}/bradesco/v1/fornecimento-extratos-contas/extratos?dataInicio={day}&dataFim={day}"
                      f"&agencia=0001&conta=0012345&tipo=cc", token=account_tok)
    lines = extrato.get("extratoUltimosLancamentos", {}).get("listaLancamentos", {}).get("Lancamentos Dia", []) if isinstance(extrato, dict) else []
    check("day statement with tipoLancamento 2/9", [i.get("tipoLancamento") for i in lines] == ["2", "2", "9", "9", "9"], str(lines)[:300])

    status, _ = http("PUT", f"/s/{sb}/_chameleon/connectors/bradesco/subscription/settings", {"TED_MODE": "manual"})
    _, manual_ted = send_ted()
    check("TED_MODE=manual keeps the TED EM PROCESSAMENTO", status == 200 and ted_lookup(manual_ted).get("statusMensagem") == "EM PROCESSAMENTO",
          f"{status} {manual_ted}")


def main() -> int:
    global KEYCLOAK, OIDC
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--skip-dotnet", action="store_true", help="do not run dotnet test")
    parser.add_argument("--skip-build", action="store_true", help="do not run docker build")
    parser.add_argument("--oidc", action="store_true", help="run against docker-compose.oidc.yml (Keycloak login)")
    parser.add_argument("--keycloak", default=KEYCLOAK, help="Keycloak base URL as seen from the host")
    parser.add_argument("--skip-restart", action="store_true", help="do not restart the container")
    args = parser.parse_args()
    sys.stdout.reconfigure(encoding="utf-8")

    print(f"Chameleon smoke test (run {RUN})")
    if not args.skip_dotnet:
        step_dotnet()
    if not args.skip_build:
        step_build()

    if not wait_ready(10):
        print(f"\nAPI is not answering at {BASE}. Start it with: docker compose up --build -d")
        return 2

    start_receiver()
    if args.oidc:
        KEYCLOAK, OIDC = args.keycloak, True
        use_default_bearer(step_oidc())
    # The "default" sandbox is closed to provider routes with auth on, so with --oidc only the
    # scenarios that run in their own subscribed sandboxes apply.
    steps = [step_panel, step_outbox, step_mtls] if args.oidc else [
        step_bradesco, step_clock, step_nfse, step_sdk, step_outbox, step_panel, step_sandboxes, step_accounts,
        step_mtls, step_inter, step_inter_cobv, step_inter_banking, step_pix, step_zendesk, step_bradesco_full]
    for step in steps:
        step()
    if not args.skip_restart:
        step_persistence()

    failed = [name for name, ok, _ in results if not ok]
    print(f"\n{len(results) - len(failed)}/{len(results)} passed.")
    for name in failed:
        print(f"  failed: {name}")
    return 1 if failed else 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    finally:
        # The client private keys of the run never outlive it.
        shutil.rmtree(CERT_DIR, ignore_errors=True)
