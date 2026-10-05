FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
RUN apt-get update && apt-get install -y --no-install-recommends \
    build-essential \
    libxdp-dev \
    libbpf-dev \
    libelf-dev \
    zlib1g-dev \
    && rm -rf /var/lib/apt/lists/*
COPY . .
RUN bash src/DHMP.AfXdp/native/build-linux.sh
RUN dotnet publish samples/DHMP.FramebufferDemo/DHMP.FramebufferDemo.csproj -c Release -o /out --no-self-contained

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
RUN apt-get update && apt-get install -y --no-install-recommends \
    iproute2 \
    libxdp1 \
    libbpf1 \
    libelf1t64 \
    zlib1g \
    && rm -rf /var/lib/apt/lists/*
COPY --from=build /out .
COPY --from=build /src/src/DHMP.AfXdp/native/libdhmp_afxdp.so /app/native/libdhmp_afxdp.so
COPY samples/DHMP.FramebufferDemo/docker-entrypoint.sh /app/docker-entrypoint.sh
RUN chmod +x /app/docker-entrypoint.sh
ENV ASPNETCORE_URLS=http://+:8080
ENV LD_LIBRARY_PATH=/app/native
ENV DHMP_AFXDP_INTERFACE=dhmpxdp0
EXPOSE 8080
ENTRYPOINT ["/app/docker-entrypoint.sh"]
