# 基底映像已內建 uv 執行檔與對應版本的 Python，免去額外安裝步驟。
FROM ghcr.io/astral-sh/uv:python3.12-bookworm-slim
WORKDIR /app

# 開啟位元組編譯（縮短冷啟動時間）與複製模式連結
# （容器檔案系統多為 overlay，硬連結/符號連結可能失敗，改用複製較穩定）。
ENV UV_COMPILE_BYTECODE=1 UV_LINK_MODE=copy

# 先只複製相依性描述檔，讓 Docker 層快取在原始碼變動時仍可重用。
COPY pyproject.toml uv.lock ./
RUN uv sync --frozen --no-dev

# 再複製應用程式原始碼（tests/ 已由 .dockerignore 排除，不會進到映像內）。
COPY app ./app

# 以非 root 使用者執行，降低容器被入侵時的風險。
RUN useradd -m appuser
USER appuser

# 將 uv 建立的虛擬環境加入 PATH，之後可直接呼叫 uvicorn 等指令。
ENV PATH="/app/.venv/bin:$PATH"

EXPOSE 8000
CMD ["uvicorn", "app.main:app", "--host", "0.0.0.0", "--port", "8000"]
