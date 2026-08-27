FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
WORKDIR /app
EXPOSE 8080

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# リポジトリのルート全体をコピー（flaubert-shared などの依存関係も一緒に取り込むため）
COPY . .

# ルートからの相対パスで復元を実行
RUN dotnet restore "flaubert-drive/flaubert-drive.csproj"

# ビルド実行
WORKDIR "/src/flaubert-drive"
RUN dotnet build "flaubert-drive.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "flaubert-drive.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "flaubert-drive.dll"]