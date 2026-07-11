import asyncio
from contextlib import asynccontextmanager

from fastapi import Depends, FastAPI, HTTPException
from pydantic import ValidationError

from app import tracing
from app.chunking import split_text
from app.embeddings import get_embeddings
from app.schemas import (
    DocumentCreate,
    DocumentCreated,
    DocumentInfo,
    InvokeRequest,
    InvokeResponse,
    WorkflowInfo,
)
from app.security import RequestContext, get_context
from app.settings import settings
from app.vectorstore import PgVectorStore, VectorStore, get_vector_store

# 這行 import 除了取得 registry 模組本身，也會連帶執行
# app/workflows/__init__.py 內各工作流模組的 @register 裝飾器，
# 讓 registry 在應用程式啟動時就已經填好所有工作流。
from app.workflows import registry

# 這些鍵一律由伺服器依 RequestContext 自動注入 state，不允許呼叫端經 input 蓋掉，
# 避免有心（或不小心）的呼叫端夾帶假的租戶／使用者資訊，破壞多租戶隔離邊界。
_RESERVED_INPUT_KEYS = {"tenant_id", "user_id", "role"}


@asynccontextmanager
async def lifespan(app: FastAPI):
    """應用程式生命週期：若目前的向量庫是 PgVectorStore，啟動時建表、關閉時釋放連線池。

    InMemoryVectorStore 不需要任何生命週期管理，因此這裡用 isinstance 判斷即可。
    """
    store = get_vector_store()
    if isinstance(store, PgVectorStore):
        await store.init()
    yield
    if isinstance(store, PgVectorStore):
        await store.close()


app = FastAPI(title="springaitest-workflow", lifespan=lifespan)


@app.get("/health")
async def health() -> dict:
    """健康檢查端點，供 compose / Spring 端探活使用；刻意不掛任何驗證，避免探活受認證設定影響。"""
    return {"status": "ok"}


@app.get("/workflows", response_model=list[WorkflowInfo])
async def list_workflows(ctx: RequestContext = Depends(get_context)) -> list[WorkflowInfo]:
    """列出目前已註冊的所有工作流（含各自的最低角色需求）。"""
    return [
        WorkflowInfo(
            name=spec.name,
            description=spec.description,
            required_role=spec.required_role,
        )
        for spec in registry.all_specs()
    ]


@app.post("/workflows/{name}/invoke", response_model=InvokeResponse)
async def invoke_workflow(
    name: str,
    req: InvokeRequest,
    ctx: RequestContext = Depends(get_context),
) -> InvokeResponse:
    """觸發指定工作流，同步執行到底並回傳最終 state。

    驗證順序：工作流是否存在（404）→ 呼叫者角色是否足夠（403）→
    input 是否符合該工作流宣告的 schema（422）→ 執行圖並套用逾時保護（504／500）。
    """
    spec = registry.get(name)
    if spec is None:
        raise HTTPException(
            status_code=404,
            detail={
                "error": "workflow_not_found",
                "message": f"unknown workflow: {name}",
                "workflows": [s.name for s in registry.all_specs()],
            },
        )

    if spec.required_role == "ADMIN" and ctx.role != "ADMIN":
        raise HTTPException(
            status_code=403,
            detail={
                "error": "workflow_forbidden",
                "message": f"workflow '{name}' 需要 {spec.required_role} 角色權限",
            },
        )

    if spec.input_model is not None:
        try:
            spec.input_model.model_validate(req.input)
        except ValidationError as e:
            raise HTTPException(
                status_code=422,
                detail={
                    "error": "workflow_input_invalid",
                    "message": str(e),
                },
            )

    cleaned_input = {k: v for k, v in req.input.items() if k not in _RESERVED_INPUT_KEYS}
    state = {"tenant_id": ctx.tenant_id, **cleaned_input}
    timeout_seconds = spec.timeout_seconds or settings.workflow_timeout_seconds

    try:
        async with asyncio.timeout(timeout_seconds):
            output = await spec.graph.ainvoke(state, config=tracing.runnable_config())
    except TimeoutError:
        raise HTTPException(
            status_code=504,
            detail={
                "error": "workflow_timeout",
                "message": f"workflow '{name}' 執行超過 {timeout_seconds} 秒",
            },
        )
    except Exception as e:
        raise HTTPException(
            status_code=500,
            detail={
                "error": "workflow_execution_failed",
                "message": str(e),
            },
        )

    return InvokeResponse(workflow=name, output=output)


@app.post("/documents", response_model=DocumentCreated, status_code=201)
async def create_document(
    body: DocumentCreate,
    ctx: RequestContext = Depends(get_context),
    store: VectorStore = Depends(get_vector_store),
) -> DocumentCreated:
    """切塊、嵌入並儲存一份文件；文件只會屬於呼叫者所在的租戶。

    嵌入呼叫（外部模型）或向量庫寫入（DB pool）都可能卡住，因此整段套上逾時保護，
    避免 handler 無限阻塞。
    """
    try:
        async with asyncio.timeout(settings.document_timeout_seconds):
            chunks = split_text(body.text) or [body.text.strip()]
            embeddings = await get_embeddings().aembed_documents(chunks)
            doc_id, chunk_count = await store.add_document(
                ctx.tenant_id, body.title, chunks, embeddings
            )
    except TimeoutError:
        raise HTTPException(
            status_code=504,
            detail={
                "error": "document_timeout",
                "message": f"新增文件執行超過 {settings.document_timeout_seconds} 秒",
            },
        )

    return DocumentCreated(id=doc_id, title=body.title, chunk_count=chunk_count)


@app.get("/documents", response_model=list[DocumentInfo])
async def list_documents(
    ctx: RequestContext = Depends(get_context),
    store: VectorStore = Depends(get_vector_store),
) -> list[DocumentInfo]:
    """列出呼叫者所在租戶底下的所有文件；這是多租戶隔離的其中一環，僅回傳本租戶資料。

    向量庫查詢（DB pool）可能卡住，因此套上逾時保護，避免 handler 無限阻塞。
    """
    try:
        async with asyncio.timeout(settings.document_timeout_seconds):
            records = await store.list_documents(ctx.tenant_id)
    except TimeoutError:
        raise HTTPException(
            status_code=504,
            detail={
                "error": "document_timeout",
                "message": f"列出文件執行超過 {settings.document_timeout_seconds} 秒",
            },
        )

    return [
        DocumentInfo(
            id=record.id,
            title=record.title,
            chunk_count=record.chunk_count,
            created_at=record.created_at,
        )
        for record in records
    ]


@app.delete("/documents/{doc_id}", status_code=204)
async def delete_document(
    doc_id: str,
    ctx: RequestContext = Depends(get_context),
    store: VectorStore = Depends(get_vector_store),
) -> None:
    """刪除指定文件；非本租戶或根本不存在一律回報 404，避免洩漏其他租戶是否持有該文件。

    向量庫刪除操作（DB pool）可能卡住，因此套上逾時保護；注意 404 判斷放在
    timeout 區塊之外，避免這裡主動拋出的 HTTPException 被 except TimeoutError 誤吞
    （TimeoutError 與 HTTPException 本來就不是同一個型別，但刻意分開讓意圖更清楚）。
    """
    try:
        async with asyncio.timeout(settings.document_timeout_seconds):
            deleted = await store.delete_document(ctx.tenant_id, doc_id)
    except TimeoutError:
        raise HTTPException(
            status_code=504,
            detail={
                "error": "document_timeout",
                "message": f"刪除文件執行超過 {settings.document_timeout_seconds} 秒",
            },
        )

    if not deleted:
        raise HTTPException(
            status_code=404,
            detail={
                "error": "document_not_found",
                "message": f"document not found: {doc_id}",
            },
        )
