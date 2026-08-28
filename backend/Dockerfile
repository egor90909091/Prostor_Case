# Один Dockerfile на оба сервиса: отличаются только аргументом PROJECT.
ARG PROJECT=Prostor.Chat

# Версии зафиксированы (не 8.0-плавающий тег): SDK 8.0.423 / рантайм 8.0.29 —
# актуальный на момент написания патч .NET 8 LTS. Обновлять сознательно, вместе.
FROM mcr.microsoft.com/dotnet/sdk:8.0.423 AS build
ARG PROJECT
WORKDIR /src

# сначала только csproj — слой с restore кэшируется между сборками
COPY src/Prostor.Chat/Prostor.Chat.csproj src/Prostor.Chat/
COPY src/Prostor.Tz/Prostor.Tz.csproj     src/Prostor.Tz/
RUN dotnet restore "src/${PROJECT}/${PROJECT}.csproj"

COPY src/ src/
RUN dotnet publish "src/${PROJECT}/${PROJECT}.csproj" -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0.29
ARG PROJECT
WORKDIR /app

# Шрифт с кириллицей нужен выгрузке ТЗ в PDF (см. PdfFonts): в PDF нет
# встроенных шрифтов с русским, а тащить .ttf в репозиторий ради этого — хуже,
# чем 2 МБ пакета в образе. Ставится в оба сервиса: Dockerfile один на двоих,
# а лишний слой ничего не ломает.
RUN apt-get update \
 && apt-get install -y --no-install-recommends fonts-dejavu-core \
 && rm -rf /var/lib/apt/lists/*
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080 \
    DOTNET_ENTRY=${PROJECT}.dll
EXPOSE 8080
# ENTRYPOINT не поддерживает ARG напрямую — берём имя сборки из переменной
ENTRYPOINT ["/bin/sh", "-c", "exec dotnet $DOTNET_ENTRY"]
