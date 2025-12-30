# GPU Dockerfile - requires NVIDIA GPU with CUDA support
# For CPU-only systems, use Dockerfile.cpu instead
#
# See https://aka.ms/customizecontainer to learn how to customize your debug container

# CUDA 13 base image for GPU support (whisper.net 1.9.0 requires CUDA 13)
FROM nvidia/cuda:13.1.0-runtime-ubuntu22.04 AS base

RUN apt-get update && apt-get install -y --no-install-recommends \
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

# Set library path for CUDA runtime to find whisper native libs
ENV LD_LIBRARY_PATH=/app/runtimes/cuda/linux-x64:/usr/local/cuda/lib64:${LD_LIBRARY_PATH}

# Models volume mount point
VOLUME ["/models"]

ENTRYPOINT ["dotnet", "worker.dll"]