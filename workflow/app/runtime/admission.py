"""Process-local admission control for durable runtime workers."""

from __future__ import annotations

import asyncio
import time
from collections import deque
from dataclasses import dataclass
from typing import Literal


AdmissionMode = Literal["off", "observe", "enforce"]
PoolName = Literal["root", "direct"]


class AdmissionClosed(RuntimeError):
    pass


@dataclass(frozen=True)
class AdmissionMetrics:
    active: int
    queued: int
    rejected: int
    wait_count: int
    wait_seconds: float


class AdmissionLease:
    def __init__(self, pool: "_AdmissionPool", *, active: bool, observed: bool = False) -> None:
        self._pool = pool
        self._active = active
        self._observed = observed
        self._released = False
        self.transferred = False
        self._queued_at = time.monotonic() if not active and not observed else None
        self._ready = asyncio.get_running_loop().create_future()
        if active or observed:
            self._ready.set_result(None)

    async def wait(self) -> None:
        try:
            await self._ready
        except BaseException:
            self.release()
            raise

    def release(self) -> None:
        if self._released:
            return
        self._released = True
        self._pool.release(self)

    def transfer(self) -> None:
        self.transferred = True


class _AdmissionPool:
    def __init__(self, active_limit: int, queue_limit: int, mode: AdmissionMode) -> None:
        self.active_limit = active_limit
        self.queue_limit = queue_limit
        self.mode = mode
        self.active = 0
        self.rejected = 0
        self.wait_count = 0
        self.wait_seconds = 0.0
        self._queue: deque[AdmissionLease] = deque()
        self._closed = False

    def reserve(self, *, allow_queue: bool) -> AdmissionLease | None:
        if self._closed:
            self.rejected += 1
            return None
        if self.mode == "off":
            return AdmissionLease(self, active=False, observed=True)
        if self.mode == "observe":
            if self.active >= self.active_limit:
                if self.active - self.active_limit >= self.queue_limit:
                    self.rejected += 1
                else:
                    self.wait_count += 1
            self.active += 1
            return AdmissionLease(self, active=False, observed=True)
        if self.active < self.active_limit:
            self.active += 1
            return AdmissionLease(self, active=True)
        if not allow_queue or len(self._queue) >= self.queue_limit:
            self.rejected += 1
            return None
        lease = AdmissionLease(self, active=False)
        self._queue.append(lease)
        return lease

    def release(self, lease: AdmissionLease) -> None:
        if lease._observed:
            if self.mode == "observe":
                self.active -= 1
            return
        if not lease._active:
            try:
                self._queue.remove(lease)
            except ValueError:
                pass
            return
        while self._queue:
            next_lease = self._queue.popleft()
            if next_lease._released:
                continue
            next_lease._active = True
            self.wait_count += 1
            if next_lease._queued_at is not None:
                self.wait_seconds += time.monotonic() - next_lease._queued_at
            if not next_lease._ready.done():
                next_lease._ready.set_result(None)
            return
        self.active -= 1

    def metrics(self) -> AdmissionMetrics:
        return AdmissionMetrics(
            active=(min(self.active, self.active_limit) if self.mode == "observe" else self.active),
            queued=(
                max(0, self.active - self.active_limit)
                if self.mode == "observe"
                else len(self._queue)
            ),
            rejected=self.rejected,
            wait_count=self.wait_count,
            wait_seconds=self.wait_seconds,
        )

    def close(self) -> None:
        self._closed = True
        queued = list(self._queue)
        self._queue.clear()
        for lease in queued:
            lease._released = True
            if not lease._ready.done():
                lease._ready.set_exception(AdmissionClosed("admission pool is closed"))


class AdmissionCoordinator:
    """One process-local coordinator with deadlock-independent runtime pools."""

    def __init__(
        self,
        *,
        mode: AdmissionMode,
        root_active: int,
        root_queue: int,
        direct_active: int,
        direct_queue: int,
    ) -> None:
        self.mode = mode
        self._pools = {
            "root": _AdmissionPool(root_active, root_queue, mode),
            "direct": _AdmissionPool(direct_active, direct_queue, mode),
        }

    def reserve(self, pool: PoolName, *, allow_queue: bool = True) -> AdmissionLease | None:
        return self._pools[pool].reserve(allow_queue=allow_queue)

    def reserve_recovery(self, pool: PoolName, requested: int) -> list[AdmissionLease]:
        leases: list[AdmissionLease] = []
        for _ in range(requested):
            lease = self.reserve(pool, allow_queue=False)
            if lease is None:
                break
            leases.append(lease)
        return leases

    def metrics(self, pool: PoolName) -> AdmissionMetrics:
        return self._pools[pool].metrics()

    def close(self, pool: PoolName) -> None:
        self._pools[pool].close()
