FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
WORKDIR /app
EXPOSE 8080

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# ソースコード全体を一度に /src にコピーする（またはプロジェクトファイル単位で正確に配置する）
COPY . .

# 正しいディレクトリから復元とビルドを行う
RUN dotnet restore "flaubert-drive/flaubert-drive.csproj"
WORKDIR "/src/flaubert-drive"
RUN dotnet build "flaubert-drive.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "flaubert-drive.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "flaubert-drive.dll"]