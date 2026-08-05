# 多階段建置:SDK 階段 publish,執行階段用精簡的 aspnet runtime。
# compose 的 build context 是 backend/(dockerfile: Dockerfile),故以下路徑相對 backend/ 根目錄。
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
# UseAppHost=false:容器內以 `dotnet Backend.Api.dll` 啟動,不需要平台原生 apphost。
RUN dotnet publish src/Backend.Api -c Release -o /app /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
# .NET runtime images provide the non-root `app` user through APP_UID.
USER $APP_UID
# 對外只跑 http:8080(容器內不做 HTTPS)。
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "Backend.Api.dll"]
