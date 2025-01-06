FROM mcr.microsoft.com/dotnet/sdk:9.0 as builder
WORKDIR /app

RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        clang \
        make \
        cmake \
        zlib1g-dev \
        libicu-dev \
        libssl-dev \
        lld

COPY ./* ./

RUN dotnet publish -r linux-x64 -c Release --ucr --sc -o out \
    -p:AssemblyName=action

FROM gcr.io/distroless/static-debian12:latest
WORKDIR /app

COPY --from=builder --chmod=+x /app/out/action .

ENTRYPOINT [ "/app/action" ]
