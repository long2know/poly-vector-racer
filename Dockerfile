FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source
RUN dotnet workload install wasm-tools --skip-manifest-update
COPY global.json Directory.Build.props ./
COPY src/ src/
# SDK-generated boot metadata can be owner-only; the runtime user must read it.
RUN dotnet publish src/StuntRacer.Browser/StuntRacer.Browser.csproj -c Release --nologo \
    && dotnet publish src/StuntRacer.Host/StuntRacer.Host.csproj -c Release --nologo -o /host \
    && cp -a src/StuntRacer.Browser/bin/Release/net10.0/browser-wasm/AppBundle /bundle \
    && chmod -R a+rX /host /bundle

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /host/ ./
COPY --from=build /bundle/ ./wwwroot/
ENV ASPNETCORE_HTTP_PORTS=8080 \
    STUNT_BUNDLE=/app/wwwroot \
    BASE_URL=""
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "StuntRacer.Host.dll"]
