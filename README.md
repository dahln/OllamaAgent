# OllamaAgent

A .NET 10 C# console application that acts as an autonomous AI agent, powered by a local [Ollama](https://ollama.ai) instance and a Docker sandbox.

## Features

- **Live streaming** – every token Ollama produces is printed to the terminal in real time, just like chatting with Ollama directly.
- **Structured planning** – the agent asks Ollama to produce a concise 2-5 word task title and a step-by-step execution plan (structured JSON output).
- **Isolated sandbox** – each task runs inside a fresh `ubuntu:24.04` Docker container so the host is never touched during execution.
- **Agentic loop** – the agent iterates over each step, sending commands to the sandbox, feeding results back to Ollama, and continuing until the AI reports the step is complete.
- **Artifact collection** – when the task finishes, everything under `/workspace` in the sandbox is copied to a local `tasks/<title>_<timestamp>/` folder on the host.
- **Clean teardown** – the sandbox container is stopped and deleted after each task.
- **Multi-task session** – after a task completes the program waits for the next prompt without restarting.

## Prerequisites

| Requirement | Notes |
|---|---|
| [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) | `dotnet --version` should show `10.x` |
| [Ollama](https://ollama.ai) running locally | Default: `http://localhost:11434` |
| A pulled Ollama model | Default: `llama3.2` – pull with `ollama pull llama3.2` |
| [Docker](https://docs.docker.com/get-docker/) running locally | The Docker socket must be accessible |

## Quick Start

```bash
# Clone
git clone https://github.com/dahln/OllamaAgent.git
cd OllamaAgent/OllamaAgent

# Build & run
dotnet run
```

At the prompt, describe the task you want the agent to complete:

```
> Write a Python script that prints the Fibonacci sequence up to 100
```

The agent will:
1. Stream the planning response to the terminal and display the execution plan.
2. Pull the sandbox image and start the container.
3. Work through each step, streaming every Ollama response live.
4. Copy the deliverables to `tasks/<TaskTitle>_<timestamp>/`.
5. Remove the sandbox and wait for the next task.

Type `exit` or `quit` (or press **Ctrl+C** during a task) to stop.

## Configuration

Override defaults with environment variables:

| Variable | Default | Description |
|---|---|---|
| `OLLAMA_MODEL` | `llama3.2` | Ollama model to use |
| `OLLAMA_URL` | `http://localhost:11434` | Ollama base URL |

```bash
OLLAMA_MODEL=qwen2.5-coder OLLAMA_URL=http://192.168.1.10:11434 dotnet run
```

## Project Structure

```
OllamaAgent/
├── Models/
│   ├── ExecutionStep.cs       # Single step in the execution plan
│   ├── OllamaChatMessage.cs   # Ollama chat message (role + content)
│   ├── OllamaChatRequest.cs   # Ollama /api/chat request body
│   ├── OllamaChatResponse.cs  # Ollama streaming response chunk
│   ├── StepCommand.cs         # Structured AI response during step execution
│   └── TaskPlan.cs            # Title + ordered list of steps
├── Services/
│   ├── AgentService.cs        # Orchestrates the full agent lifecycle
│   ├── DockerService.cs       # Docker sandbox management
│   └── OllamaService.cs       # Streaming Ollama chat client
└── Program.cs                 # Entry point & prompt loop
```
