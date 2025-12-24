# Whisper Worker

A containerized worker service for processing audio transcription tasks using OpenAI's Whisper model.

## Features

- **GPU & CPU support**: Runs on NVIDIA CUDA for fast transcription OR CPU-only for compatibility
- **Multi-format support**: Outputs in TXT, JSON, SRT, VTT
- **99 languages**: Automatic language detection + all Whisper-supported languages
- **Multiple models**: tiny, base, small, medium, large
- **Docker-ready**: Pre-built images available on GitHub Container Registry

## Quick Start

The easiest way to run a worker is through the admin panel:

1. Create a new worker in the admin interface
2. Copy the generated Docker command (GPU or CPU)
3. Run it on any machine with Docker

### GPU Command (Faster)

```bash
docker run -d --name my-worker-gpu --gpus all \
  -e WORKER__Name=my-worker-gpu \
  -e WORKER__ApiUrl=http://your-api:5239 \
  -e WORKER__Token=your-token \
  -v whisper_models:/models \
  ghcr.io/mitrevstudio/whisper-worker:gpu
```

### CPU Command (No GPU required)

```bash
docker run -d --name my-worker-cpu \
  -e WORKER__Name=my-worker-cpu \
  -e WORKER__ApiUrl=http://your-api:5239 \
  -e WORKER__Token=your-token \
  -v whisper_models:/models \
  ghcr.io/mitrevstudio/whisper-worker:cpu
```

## Documentation

See repository wiki for complete setup instructions, configuration options, and troubleshooting.

## Docker Images

Images are automatically built and published to GitHub Container Registry:

- `ghcr.io/mitrevstudio/whisper-worker:latest` (GPU)
- `ghcr.io/mitrevstudio/whisper-worker:gpu` (GPU)
- `ghcr.io/mitrevstudio/whisper-worker:cpu` (CPU-only)

## Development

### Building

```bash
docker build -t whisper-worker .
```

### Running Locally

```bash
dotnet run
```

Configuration is loaded from `appsettings.json` and environment variables prefixed with `WORKER__`.

## Configuration

Key settings (all optional with defaults):

- `WORKER__Name`: Worker identifier
- `WORKER__ApiUrl`: API server endpoint
- `WORKER__Token`: Authentication token (required)
- `WORKER__ModelPath`: Model storage path
- `WORKER__TempPath`: Temporary file storage

## Architecture

The worker:

1. Connects to the API via SignalR
2. Receives transcription tasks
3. Downloads audio files
4. Processes with Whisper (GPU-accelerated)
5. Uploads results back to API
6. Sends heartbeats to maintain connection

## License

See [LICENSE.txt](LICENSE.txt)
