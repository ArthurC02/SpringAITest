# syntax=docker/dockerfile:1
# 後端多階段建置（選用：只有 docker compose --profile full 會用到；
# 平常後端是跑在「主機」上以利熱重載 / debug）。
# build 階段用 JDK 25 + 專案內附的 Maven Wrapper；runtime 只留精簡 JRE。

# ---- build：用 JDK 25 + Maven Wrapper 打包出可執行 jar ----
FROM eclipse-temurin:25-jdk AS build
WORKDIR /workspace
COPY . .
# BuildKit cache mount 快取 ~/.m2：原始碼改動時不必每次重抓依賴。
# 跳過測試（由 CI / 主機 ./mvnw test 負責），讓映像建置專注在打包。
# sed 去除 mvnw 可能的 CRLF 行尾：在 Windows checkout 時 mvnw 易帶 \r，
#   會讓 Linux 把 shebang 解析成「/bin/sh\r」而報 not found；先正規化成 LF 再執行。
RUN --mount=type=cache,target=/root/.m2 \
    sed -i 's/\r$//' mvnw && chmod +x mvnw && ./mvnw -B clean package -DskipTests

# ---- runtime：精簡 JRE，只放可執行 jar ----
FROM eclipse-temurin:25-jre
WORKDIR /app
# 只有 springaitest-web 模組會被 spring-boot-maven-plugin 重打包成可執行 jar。
COPY --from=build /workspace/springaitest-web/target/*.jar app.jar
EXPOSE 8080
ENTRYPOINT ["java", "-jar", "app.jar"]
