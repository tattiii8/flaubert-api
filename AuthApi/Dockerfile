# Stage 1: Build
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# プロジェクトファイルをコピーしてリストア
COPY AuthApi/AuthApi.csproj ./
RUN dotnet restore AuthApi.csproj

# 残りをコピーしてパブリッシュ
COPY . .
RUN dotnet publish AuthApi.csproj -c Release -o /app

# Stage 2: Runtime
FROM mcr.microsoft.com/dotnet/aspnet:9.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "AuthApi.dll"]
