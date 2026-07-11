"""向量庫抽象介面與兩種實作：InMemoryVectorStore（開發／測試）與 PgVectorStore（正式環境）。

所有實作都以 tenant_id 作為第一層隔離邊界：任何查詢、列表、刪除操作都只會碰到
呼叫者所屬租戶的資料，確保多租戶之間的資料互不可見。
"""

import math
import uuid
from abc import ABC, abstractmethod
from dataclasses import dataclass, field
from datetime import datetime, timezone
from functools import lru_cache

from app.settings import settings


@dataclass(frozen=True)
class RetrievedChunk:
    """向量檢索命中的單一片段。"""

    document_id: str
    title: str
    content: str
    score: float


@dataclass(frozen=True)
class DocumentRecord:
    """文件中繼資料（不含片段內容），供 list_documents 回傳。"""

    id: str
    title: str
    chunk_count: int
    created_at: datetime


class VectorStore(ABC):
    """向量庫抽象介面：新增實作時只需實作以下四個非同步方法。"""

    @abstractmethod
    async def add_document(
        self,
        tenant_id: str,
        title: str,
        chunks: list[str],
        embeddings: list[list[float]],
    ) -> tuple[str, int]:
        """新增一份文件與其切塊、嵌入向量，回傳 (doc_id, chunk_count)。"""

    @abstractmethod
    async def search(
        self, tenant_id: str, query_embedding: list[float], top_k: int
    ) -> list[RetrievedChunk]:
        """在指定租戶內做向量相似度檢索，回傳依相似度由高到低排序的片段列表。"""

    @abstractmethod
    async def list_documents(self, tenant_id: str) -> list[DocumentRecord]:
        """列出指定租戶底下的所有文件（僅中繼資料，不含片段內容）。"""

    @abstractmethod
    async def delete_document(self, tenant_id: str, doc_id: str) -> bool:
        """刪除指定租戶底下的文件；成功回傳 True，找不到（含非本租戶）回傳 False。"""


def _cosine_similarity(a: list[float], b: list[float]) -> float:
    """兩向量的餘弦相似度，純 Python／math 實作，不引入 numpy。"""
    dot = sum(x * y for x, y in zip(a, b))
    norm_a = math.sqrt(sum(x * x for x in a))
    norm_b = math.sqrt(sum(y * y for y in b))
    if norm_a == 0.0 or norm_b == 0.0:
        return 0.0
    return dot / (norm_a * norm_b)


@dataclass
class _StoredChunk:
    content: str
    embedding: list[float]


@dataclass
class _StoredDocument:
    id: str
    tenant_id: str
    title: str
    created_at: datetime
    chunks: list[_StoredChunk] = field(default_factory=list)


class InMemoryVectorStore(VectorStore):
    """以行程記憶體字典模擬的向量庫：適合本機開發與測試，服務重啟即清空。"""

    def __init__(self) -> None:
        self._docs: dict[str, _StoredDocument] = {}

    def reset(self) -> None:
        """清空所有資料；僅供測試在各案例之間重置狀態使用，非 VectorStore 介面的一部分。"""
        self._docs.clear()

    async def add_document(
        self,
        tenant_id: str,
        title: str,
        chunks: list[str],
        embeddings: list[list[float]],
    ) -> tuple[str, int]:
        doc_id = str(uuid.uuid4())
        stored = _StoredDocument(
            id=doc_id,
            tenant_id=tenant_id,
            title=title,
            created_at=datetime.now(timezone.utc),
            chunks=[
                _StoredChunk(content=content, embedding=embedding)
                for content, embedding in zip(chunks, embeddings)
            ],
        )
        self._docs[doc_id] = stored
        return doc_id, len(stored.chunks)

    async def search(
        self, tenant_id: str, query_embedding: list[float], top_k: int
    ) -> list[RetrievedChunk]:
        hits: list[RetrievedChunk] = []
        for doc in self._docs.values():
            if doc.tenant_id != tenant_id:
                continue
            for chunk in doc.chunks:
                score = _cosine_similarity(query_embedding, chunk.embedding)
                hits.append(
                    RetrievedChunk(
                        document_id=doc.id,
                        title=doc.title,
                        content=chunk.content,
                        score=score,
                    )
                )
        hits.sort(key=lambda h: h.score, reverse=True)
        return hits[:top_k]

    async def list_documents(self, tenant_id: str) -> list[DocumentRecord]:
        records = [
            DocumentRecord(
                id=doc.id,
                title=doc.title,
                chunk_count=len(doc.chunks),
                created_at=doc.created_at,
            )
            for doc in self._docs.values()
            if doc.tenant_id == tenant_id
        ]
        return sorted(records, key=lambda r: r.created_at)

    async def delete_document(self, tenant_id: str, doc_id: str) -> bool:
        doc = self._docs.get(doc_id)
        if doc is None or doc.tenant_id != tenant_id:
            return False
        del self._docs[doc_id]
        return True


class PgVectorStore(VectorStore):
    """以 PostgreSQL + pgvector 延伸持久化向量資料的實作，適合正式環境長期保存。

    連線一律經由 psycopg3 的非同步連線池（psycopg_pool.AsyncConnectionPool）；
    init() 會在應用程式啟動時被呼叫一次，負責建立連線池、確保 extension／資料表／索引存在，
    close() 則在應用程式關閉時釋放連線池。
    """

    def __init__(self, database_url: str, embedding_dim: int) -> None:
        self._database_url = database_url
        self._embedding_dim = embedding_dim
        self._pool: "AsyncConnectionPool | None" = None

    async def init(self) -> None:
        """建立連線池，並確保 vector extension、資料表與租戶索引皆已就緒。

        注意順序：連線池的 configure（register_vector_async）會在每個連線建立當下
        立刻向資料庫查詢 vector 型別的 OID，若這時 extension 還不存在就會直接拋錯；
        因此必須先用一條與連線池無關的獨立連線把 extension 建好，才能開啟連線池。
        """
        import psycopg
        from psycopg_pool import AsyncConnectionPool

        from pgvector.psycopg import register_vector_async

        async with await psycopg.AsyncConnection.connect(self._database_url) as conn:
            await conn.execute("CREATE EXTENSION IF NOT EXISTS vector")

        pool = AsyncConnectionPool(
            self._database_url, open=False, configure=register_vector_async
        )
        await pool.open()

        async with pool.connection() as conn:
            await conn.execute(
                """
                CREATE TABLE IF NOT EXISTS rag_documents (
                    id uuid PRIMARY KEY,
                    tenant_id text NOT NULL,
                    title text NOT NULL,
                    chunk_count int NOT NULL,
                    created_at timestamptz NOT NULL DEFAULT now()
                )
                """
            )
            await conn.execute(
                f"""
                CREATE TABLE IF NOT EXISTS rag_chunks (
                    id uuid PRIMARY KEY,
                    document_id uuid REFERENCES rag_documents (id) ON DELETE CASCADE,
                    tenant_id text NOT NULL,
                    content text NOT NULL,
                    embedding vector({self._embedding_dim}) NOT NULL
                )
                """
            )
            await conn.execute(
                "CREATE INDEX IF NOT EXISTS rag_documents_tenant_idx"
                " ON rag_documents (tenant_id)"
            )
            await conn.execute(
                "CREATE INDEX IF NOT EXISTS rag_chunks_tenant_idx"
                " ON rag_chunks (tenant_id)"
            )

        self._pool = pool

    async def close(self) -> None:
        """釋放連線池；應用程式關閉時呼叫。"""
        if self._pool is not None:
            await self._pool.close()
            self._pool = None

    async def add_document(
        self,
        tenant_id: str,
        title: str,
        chunks: list[str],
        embeddings: list[list[float]],
    ) -> tuple[str, int]:
        from pgvector import Vector

        assert self._pool is not None, "PgVectorStore 尚未呼叫 init()"
        doc_id = uuid.uuid4()
        async with self._pool.connection() as conn:
            await conn.execute(
                "INSERT INTO rag_documents (id, tenant_id, title, chunk_count)"
                " VALUES (%s, %s, %s, %s)",
                (doc_id, tenant_id, title, len(chunks)),
            )
            for content, embedding in zip(chunks, embeddings):
                await conn.execute(
                    "INSERT INTO rag_chunks (id, document_id, tenant_id, content, embedding)"
                    " VALUES (%s, %s, %s, %s, %s)",
                    (uuid.uuid4(), doc_id, tenant_id, content, Vector(embedding)),
                )
        return str(doc_id), len(chunks)

    async def search(
        self, tenant_id: str, query_embedding: list[float], top_k: int
    ) -> list[RetrievedChunk]:
        from pgvector import Vector

        assert self._pool is not None, "PgVectorStore 尚未呼叫 init()"
        async with self._pool.connection() as conn:
            cur = await conn.execute(
                """
                SELECT c.document_id, d.title, c.content, c.embedding <=> %s AS distance
                FROM rag_chunks c
                JOIN rag_documents d ON d.id = c.document_id
                WHERE c.tenant_id = %s
                ORDER BY distance
                LIMIT %s
                """,
                (Vector(query_embedding), tenant_id, top_k),
            )
            rows = await cur.fetchall()
        return [
            RetrievedChunk(
                document_id=str(document_id),
                title=title,
                content=content,
                score=1.0 - distance,
            )
            for document_id, title, content, distance in rows
        ]

    async def list_documents(self, tenant_id: str) -> list[DocumentRecord]:
        assert self._pool is not None, "PgVectorStore 尚未呼叫 init()"
        async with self._pool.connection() as conn:
            cur = await conn.execute(
                "SELECT id, title, chunk_count, created_at FROM rag_documents"
                " WHERE tenant_id = %s ORDER BY created_at",
                (tenant_id,),
            )
            rows = await cur.fetchall()
        return [
            DocumentRecord(id=str(doc_id), title=title, chunk_count=chunk_count, created_at=created_at)
            for doc_id, title, chunk_count, created_at in rows
        ]

    async def delete_document(self, tenant_id: str, doc_id: str) -> bool:
        assert self._pool is not None, "PgVectorStore 尚未呼叫 init()"
        try:
            doc_uuid = uuid.UUID(doc_id)
        except ValueError:
            return False
        async with self._pool.connection() as conn:
            cur = await conn.execute(
                "DELETE FROM rag_documents WHERE id = %s AND tenant_id = %s",
                (doc_uuid, tenant_id),
            )
            return cur.rowcount > 0


@lru_cache(maxsize=1)
def get_vector_store() -> VectorStore:
    """取得共用的向量庫實例（單例快取）：DATABASE_URL 為空即用記憶體版，否則用 pgvector 版。

    PgVectorStore 需要額外的 init()／close() 生命週期管理，交由 app.main 的
    FastAPI lifespan 負責呼叫，這裡只負責「選擇並建立實例」。
    """
    if not settings.database_url:
        return InMemoryVectorStore()
    return PgVectorStore(settings.database_url, settings.embedding_dim)
