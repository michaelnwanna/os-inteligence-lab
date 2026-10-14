"""Per-client token-bucket rate limiting for write endpoints."""

from __future__ import annotations

import threading
import time


class RateLimiter:
    """`rate` requests per second per client with bursts up to `burst`."""

    def __init__(self, rate: float, burst: float | None = None) -> None:
        self.rate, self.burst = rate, burst or max(rate * 2, 1.0)
        self._buckets: dict[str, tuple[float, float]] = {}  # key -> (tokens, last_seen)
        self._lock = threading.Lock()

    def allow(self, key: str, now: float | None = None) -> tuple[bool, float]:
        """Returns (allowed, seconds_until_next_token)."""
        now = time.monotonic() if now is None else now
        with self._lock:
            tokens, last = self._buckets.get(key, (self.burst, now))
            tokens = min(self.burst, tokens + (now - last) * self.rate)
            if tokens >= 1.0:
                self._buckets[key] = (tokens - 1.0, now)
                allowed, wait = True, 0.0
            else:
                self._buckets[key] = (tokens, now)
                allowed, wait = False, (1.0 - tokens) / self.rate
            if len(self._buckets) > 10_000:  # bound memory: drop the idlest half
                for k, _ in sorted(self._buckets.items(), key=lambda kv: kv[1][1])[:5_000]:
                    self._buckets.pop(k, None)
            return allowed, wait
