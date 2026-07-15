# mem0/mem0-api-server:latest 出廠即壞：內建 psycopg 但沒有 libpq，
# 一啟動就 "no pq wrapper available" crash。這層只補上帶 libpq 的 psycopg binary，
# 不改任何程式。等官方修好後可拿掉本檔、compose 直接用 image。
#
# 本檔僅供「手動」重建薄封裝映像用；compose 直接吃本機既有的 springaitest-mem0:latest，不再自動 build：
#   docker build -f mem0.Dockerfile -t springaitest-mem0:latest .
# （在 amd64 主機上若基底 image 缺 amd64 manifest，重建時自行加 --platform=linux/arm64 走 QEMU。）
FROM mem0/mem0-api-server:latest
RUN pip install --no-cache-dir "psycopg[binary,pool]"
