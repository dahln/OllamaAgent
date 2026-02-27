# OllamaAgent

***NOTE: This project is a continuation of my LUNA project. Excludes all the queue, scheduling, and github stuff for now and allows me to focus on building the agentic flow. My current plan is that this will become the V2 AI-Agent for LUNA. Thus far, it has better (and mixed) results.*** 

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

## Prompt Quality Checklist

Use this checklist whenever you add or modify prompts in `PromptLibrary`.

- **Immutable context contract**
	- Define concrete variables up front (for example: `PROJECT_NAME`, `ROOT_DIR`, `OUTPUT_FILE`).
	- Require reuse of the exact same values across all steps and commands.
- **Placeholder prevention**
	- Explicitly forbid unresolved tokens such as `ProjectName`, `TODO_PROJECT`, `__PROJECT__`, `<project-name>`, and `<name>`.
	- Require a validation step that scans output files for placeholder tokens before completion.
- **Pre-action checks**
	- Before write/build/run actions, verify: correct working directory, concrete names, and no placeholder tokens.
	- If any preflight check fails, stop and correct before continuing.
- **Retry and loop control**
	- Never retry the same failing command pattern more than once.
	- After repeated failure, force a strategy reset and require a different approach.
	- Add an explicit stop/escalation condition when the same error category repeats.
- **Definition of done (coding)**
	- Deliverable files exist and are non-empty.
	- Code compiles/builds and runs successfully.
	- Placeholder scan is clean.
- **Definition of done (non-coding)**
	- Output file exists, is complete, and matches requested audience/tone/format.
	- Required sections/fields are present (for example summary, findings, recommendation).
	- Placeholder scan is clean.
- **Task-shape guidance**
	- For coding tasks: include language/tool constraints and anti-pattern warnings.
	- For non-coding tasks: include evidence/structure rules and "if unknown, ask" behavior.

## Prompt Template (Copy/Paste)

Use this template when creating new prompts in `PromptLibrary`.

### 1) Coding Prompt Template

```text
ROLE:
You are an execution agent for a {LANGUAGE} task in {ROOT_DIR}.

IMMUTABLE VARIABLES:
- PROJECT_NAME: {PROJECT_NAME}
- ROOT_DIR: {ROOT_DIR}
- OUTPUT_FILE: {OUTPUT_FILE}
Use these exact values. Do not rename or substitute.

PLACEHOLDER RULE:
Never output unresolved tokens: ProjectName, TODO_PROJECT, __PROJECT__, <project-name>, <name>.

ALLOWED TOOLS:
- {LANGUAGE_TOOLCHAIN_ONLY}
- System packages via apt-get only when genuinely required.

PRE-ACTION CHECK (before write/build/run):
1. Path exists under ROOT_DIR.
2. Names are concrete and match immutable variables.
3. No unresolved placeholders in command/content.

RETRY POLICY:
- Do not retry the same failing command pattern more than once.
- After second failure in same category, switch strategy.
- If still blocked, stop and report root cause.

DEFINITION OF DONE:
1. Deliverable files are present and non-empty.
2. Code builds and runs successfully.
3. Placeholder scan is clean.
```

### 2) Non-Coding Prompt Template

```text
ROLE:
You are an execution agent producing a complete {DELIVERABLE_TYPE} for {AUDIENCE}.

IMMUTABLE VARIABLES:
- ROOT_DIR: {ROOT_DIR}
- OUTPUT_FILE: {OUTPUT_FILE}
- TONE: {TONE}

PLACEHOLDER RULE:
Never output unresolved tokens: ProjectName, TODO_PROJECT, __PROJECT__, <project-name>, <name>.

REQUIRED STRUCTURE:
- {REQUIRED_SECTION_1}
- {REQUIRED_SECTION_2}
- {REQUIRED_SECTION_3}

QUALITY RULES:
- Use evidence-based statements where applicable.
- Distinguish facts, assumptions, and recommendations.
- If a required fact is unknown, state uncertainty and request/flag missing input.

PRE-COMPLETION CHECK:
1. Output file exists in ROOT_DIR and is complete (not an outline unless requested).
2. Audience, tone, and format match requirements.
3. Placeholder scan is clean.

DEFINITION OF DONE:
- Deliverable is complete, structured, and validated against requirements.
```

### 3) Optional Runtime Validation Command

```bash
grep -RInE 'ProjectName|TODO_PROJECT|__PROJECT__|<project[-_ ]?name>|<name>' /workspace --exclude-dir=.git || true
```

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
