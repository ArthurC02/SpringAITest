from __future__ import annotations

import asyncio
from dataclasses import dataclass

from app.runtime.checkpoints import (
    PostgresCheckpointStore,
    require_runtime_checkpoint_dsn,
)
from app.runtime.manager import RuntimeRunManager
from app.skills.deps import _default_deps


@dataclass
class RuntimeService:
    checkpoints: PostgresCheckpointStore
    manager: RuntimeRunManager
    recovery_task: asyncio.Task[None]

    @classmethod
    async def open(cls) -> "RuntimeService":
        store = PostgresCheckpointStore(require_runtime_checkpoint_dsn())
        saver = await store.open()
        manager = RuntimeRunManager(checkpointer=saver, deps=_default_deps())
        return cls(
            store,
            manager,
            asyncio.create_task(
                manager.recovery_loop(), name="agent-runtime-recovery"
            ),
        )

    async def close(self) -> None:
        self.recovery_task.cancel()
        await asyncio.gather(self.recovery_task, return_exceptions=True)
        await self.manager.close()
        await self.checkpoints.close()
