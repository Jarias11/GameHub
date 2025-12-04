# Adjust DOTNET_OS_VERSION as desired
ARG DOTNET_OS_VERSION="-alpine"
ARG DOTNET_SDK_VERSION=9.0

FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_SDK_VERSION}${DOTNET_OS_VERSION} AS build
WORKDIR /src

# copy everything in src (GameServer, GameLogic, GameContracts, etc.)
COPY . ./

# restore just GameServer (this will pull in GameLogic & GameContracts via ProjectReference)
RUN dotnet restore GameServer/GameServer.csproj

# build and publish GameServer
RUN dotnet publish GameServer/GameServer.csproj -c Release -o /app

# final stage/image
FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_SDK_VERSION}
ENV ASPNETCORE_URLS http://+:8080
ENV ASPNETCORE_ENVIRONMENT Production
EXPOSE 8080
WORKDIR /app
COPY --from=build /app .
ENTRYPOINT [ "dotnet", "GameServer.dll" ]