# See https://aka.ms/customizecontainer to learn how to customize your debug container and how Visual Studio uses this Dockerfile to build your images for faster debugging.

# CUDA 12 base image for GPU support
FROM nvidia/cuda:12.4.1-cudnn-runtime-ubuntu22.04 AS base

# Install .NET runtime and FFmpeg for audio/video conversion
RUN apt-get update && apt-get install -y \
    wget \
    ca-certificates \
    ffmpeg \
    && wget https://dot.net/v1/dotnet-install.sh -O dotnet-install.sh \
    && chmod +x dotnet-install.sh \
    && ./dotnet-install.sh --channel 10.0 --runtime dotnet --install-dir /usr/share/dotnet \
    && ln -s /usr/share/dotnet/dotnet /usr/bin/dotnet \
    && rm dotnet-install.sh \
    && apt-get clean \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app

# Create directories for models and temp files
RUN mkdir -p /models /tmp/whisper

# This stage is used to build the service project
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["worker.csproj", "."]
RUN dotnet restore "./worker.csproj"
COPY . .
WORKDIR "/src/."
RUN dotnet build "./worker.csproj" -c $BUILD_CONFIGURATION -o /app/build

# This stage is used to publish the service project to be copied to the final stage
FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./worker.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

# This stage is used in production or when running from VS in regular mode (Default when not using the Debug configuration)
FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .

# Environment variables
ENV WORKER__Name=whisper-worker
ENV WORKER__ApiUrl=http://api:5000
ENV WORKER__Token=
ENV WORKER__ModelPath=/models
ENV WORKER__TempPath=/tmp/whisper
ENV WORKER__HeartbeatIntervalSeconds=30

# Models volume mount point
VOLUME ["/models"]

ENTRYPOINT ["dotnet", "worker.dll"]