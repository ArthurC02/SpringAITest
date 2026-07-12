# mem0/mem0-api-server:latest 出廠即壞：內建 psycopg 但沒有 libpq，
# 一啟動就 "no pq wrapper available" crash。這層只補上帶 libpq 的 psycopg binary，
# 不改任何程式。等官方修好後可拿掉本檔、compose 直接用 image。
#
# 2026-07 起官方 registry 只剩 linux/arm64（amd64 從 manifest 消失，upstream 迴歸），
# amd64 主機上 FROM 會直接 "no match for platform in manifest"。
# 因此顯式釘 arm64、靠 Docker Desktop 的 QEMU 模擬執行（mem0 是輕量 REST server，可接受）；
# compose 的 mem0 服務也要對應標 platform: linux/arm64。官方補回 amd64 後可移除這兩處。
FROM --platform=linux/arm64 mem0/mem0-api-server:latest
RUN pip install --no-cache-dir "psycopg[binary,pool]"
