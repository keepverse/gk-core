"""The tool's one LLM client — configuration in one home, one reader (IC-2).

Two model uses exist in this tool: `suggest` proposes a replacement token for a mark, and `curate`
proposes registry candidates. Neither owns a client, so the endpoint, model and budget are read here
and injected as a plain callable. That mirrors the repo's "Core never reads a file; hosts load and
inject" discipline applied to a tool, and it keeps a provider SDK out of the package.

Precedence is seeded environment -> committed default -> built-in default, never a silent hardcode.
`--authored-only` reads none of this and needs no network.
"""

from __future__ import annotations

import json
import os
import time
import urllib.error
import urllib.request
from dataclasses import dataclass
from pathlib import Path
from typing import Callable, Mapping

ENV_KEYS: Mapping[str, str] = {
    "endpoint": "IPCENSOR_LLM_ENDPOINT",
    "model": "IPCENSOR_LLM_MODEL",
    "timeout": "IPCENSOR_LLM_TIMEOUT",
    "attempts": "IPCENSOR_LLM_ATTEMPTS",
    "retry_delay": "IPCENSOR_LLM_RETRY_DELAY",
}

# The last layer, used only when neither the environment nor a committed default names a value.
BUILT_IN_DEFAULTS: Mapping[str, str] = {
    "endpoint": "http://localhost:1234/v1/chat/completions",
    "model": "google/gemma-4-26b-a4b-qat",
    "timeout": "420",
    "attempts": "2",
    "retry_delay": "3",
}

TransportFn = Callable[..., object]


class LlmError(RuntimeError):
    """The model endpoint could not be reached, or answered something unusable."""


@dataclass(frozen=True, slots=True)
class LlmConfig:
    endpoint: str
    model: str
    timeout: float
    attempts: int
    retry_delay: float


def load_config(
    env: Mapping[str, str] | None = None,
    defaults: Mapping[str, str] | None = None,
) -> LlmConfig:
    """Resolve the client configuration from the three layers, in order."""
    environment = os.environ if env is None else env
    committed = {} if defaults is None else defaults

    def pick(key: str) -> str:
        for layer in (environment, committed):
            value = layer.get(ENV_KEYS[key])
            if value is not None and str(value).strip():
                return str(value).strip()
        return BUILT_IN_DEFAULTS[key]

    return LlmConfig(
        endpoint=pick("endpoint"),
        model=pick("model"),
        timeout=float(pick("timeout")),
        attempts=int(pick("attempts")),
        retry_delay=float(pick("retry_delay")),
    )


def read_env_file(path: Path | str) -> dict[str, str]:
    """Read a `KEY=VALUE` file (a real `.env`, or a committed default) into a mapping.

    A missing file is an empty layer, not an error: the tool must run with no `.env` at all.
    """
    file = Path(path)
    if not file.exists():
        return {}
    values: dict[str, str] = {}
    for raw in file.read_text(encoding="utf-8").splitlines():
        line = raw.strip()
        if not line or line.startswith("#") or "=" not in line:
            continue
        key, _, value = line.partition("=")
        values[key.strip()] = value.strip()
    return values


def build_proposer(
    config: LlmConfig,
    *,
    system_prompt: str,
    opener: TransportFn | None = None,
) -> Callable[[str], str]:
    """A text-in/text-out callable over the configured endpoint.

    `opener` is the transport seam: production passes nothing and gets `urlopen`, and a test passes a
    fake so the request shape is provable without a network or a model.
    """
    transport: TransportFn = urllib.request.urlopen if opener is None else opener

    def propose(text: str) -> str:
        payload = json.dumps(
            {
                "model": config.model,
                "messages": [
                    {"role": "system", "content": system_prompt},
                    {"role": "user", "content": text},
                ],
            }
        ).encode("utf-8")
        request = urllib.request.Request(
            config.endpoint,
            data=payload,
            headers={"Content-Type": "application/json"},
            method="POST",
        )
        body = _send(transport, request, config)
        try:
            document = json.loads(body)
            return str(document["choices"][0]["message"]["content"])
        except (KeyError, IndexError, TypeError, ValueError) as exc:
            raise LlmError(f"{config.endpoint}: unusable response ({exc})") from exc

    return propose


def _send(transport: TransportFn, request: urllib.request.Request, config: LlmConfig) -> str:
    last: Exception | None = None
    for attempt in range(1, config.attempts + 1):
        try:
            response = transport(request, timeout=config.timeout)
            try:
                return response.read().decode("utf-8")  # type: ignore[union-attr]
            finally:
                close = getattr(response, "close", None)
                if close is not None:
                    close()
        except (urllib.error.URLError, OSError, TimeoutError) as exc:
            # A transport failure is retried; a content defect is not (it would just repeat).
            last = exc
            if attempt < config.attempts:
                time.sleep(config.retry_delay)
    raise LlmError(f"{config.endpoint}: unreachable after {config.attempts} attempt(s): {last}")
