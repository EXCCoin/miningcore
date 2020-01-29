FROM microsoft/dotnet:sdk as builder

RUN apt-get update -y && apt-get -y install git cmake build-essential libssl-dev pkg-config libboost-all-dev libsodium-dev

WORKDIR /tmp

COPY . .

RUN mkdir /dotnetapp
RUN chmod +x ./src/MiningCore/linux-build.sh
RUN cd ./src/MiningCore && ./linux-build.sh && cp -r ../../build/* /dotnetapp

FROM mcr.microsoft.com/dotnet/core/aspnet:2.1
RUN apt-get update -y && apt-get -y install build-essential libssl-dev pkg-config libboost-all-dev libsodium-dev
WORKDIR /dotnetapp
COPY --from=builder /dotnetapp .

# API
EXPOSE 4000
# admin API
EXPOSE 4001
# pool
EXPOSE 3032-3199

ENTRYPOINT dotnet MiningCore.dll -c /config.json
