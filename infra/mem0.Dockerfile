# mem0/mem0-api-server:latest 出廠即壞：內建 psycopg 但沒有 libpq，
# 一啟動就 "no pq wrapper available" crash。這層只補上帶 libpq 的 psycopg binary，
# 不改任何程式。等官方修好後可拿掉本檔、compose 直接用 image。
FROM mem0/mem0-api-server:latest
RUN pip install --no-cache-dir "psycopg[binary]"
