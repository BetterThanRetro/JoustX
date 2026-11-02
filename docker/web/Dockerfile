# The browser build of JoustX, built and served entirely in containers
# (scripts/web-docker.ps1 builds and starts it). Build context: the repository root.

# 1. Build: assemble the game image from the original source, and compile the game to
#    WebAssembly ahead of time (the wasm-tools workload; python3 for its Emscripten toolchain).
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
RUN apt-get update && apt-get install -y --no-install-recommends python3 && rm -rf /var/lib/apt/lists/*
RUN dotnet workload install wasm-tools
WORKDIR /src
COPY . .
RUN dotnet run -c Release --project src/Joust.Tools -- build-image /out/joust.img
RUN dotnet publish src/Joust.Web -c Release -o /publish \
 && cp /out/joust.img /publish/wwwroot/

# 2. Serve: the page, the WebAssembly and the game image as static files.
FROM nginx:alpine
COPY docker/web/nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build /publish/wwwroot /usr/share/nginx/html
