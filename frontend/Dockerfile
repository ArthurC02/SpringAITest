# 多階段建置：先用 Node 把 SPA 打包成靜態檔，再交給 nginx 提供並反向代理 /api。
# 注意：Spring 後端是跑在「主機」上（非容器內），nginx 透過 host.docker.internal 連回去。

# ---- build 階段：產出 dist/ ----
FROM node:22-alpine AS build
WORKDIR /app
COPY package.json package-lock.json ./
# 用 npm install 而非 npm ci：lockfile 在 Windows 產生，含平台相依的 optional 套件
# （@emnapi、@rollup/*-linux 等），到 Linux 容器用嚴格的 npm ci 會因跨平台差異報 EUSAGE。
RUN npm install --no-audit --no-fund
COPY . .
RUN npm run build

# ---- serve 階段：nginx 提供靜態檔 + /api 反向代理 ----
FROM nginx:1.27-alpine
COPY nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build /app/dist /usr/share/nginx/html
EXPOSE 80
