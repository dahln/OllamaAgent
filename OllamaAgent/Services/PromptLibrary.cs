namespace OllamaAgent.Services;

/// <summary>
/// A comprehensive library of focused, parameterized AI prompts organized by domain and purpose.
/// Prompts are designed to be composed together — each addresses a single concern and can be
/// combined with others to form complete system prompts for any agentic phase.
///
/// This is the core intelligence of the agent. Custom C# logic is kept minimal;
/// these prompts encode the decision-making, error recovery, scope control, and
/// domain expertise that drive agent behavior.
/// </summary>
public static class PromptLibrary
{
    // ═════════════════════════════════════════════════════════════════════════
    //  SCHEMAS — JSON schemas for structured AI output
    // ═════════════════════════════════════════════════════════════════════════

    public static class Schemas
    {
        public static readonly object TaskClassification = new
        {
            type = "object",
            properties = new
            {
                primaryCategory = new
                {
                    type = "string",
                    description = "One of: coding, research, writing, analysis, debugging, opinion, creative, system_admin",
                },
                language = new
                {
                    type = "string",
                    description = "Programming language if applicable. One of: csharp, python, javascript, typescript, sql, bash, none",
                },
                framework = new
                {
                    type = "string",
                    description = "Framework if applicable. One of: dotnet, react, angular, vue, nextjs, django, flask, express, none",
                },
                requiredCapabilities = new
                {
                    type = "array",
                    items = new { type = "string" },
                    description = "Required capabilities from: file_writing, web_research, compilation, testing, database, api_calls, data_processing",
                },
                complexity = new
                {
                    type = "string",
                    description = "One of: simple, moderate, complex",
                },
            },
            required = new[] { "primaryCategory", "language", "framework", "requiredCapabilities", "complexity" },
        };

        public static readonly object TaskPlan = new
        {
            type = "object",
            properties = new
            {
                title = new { type = "string", description = "A concise 2-5 word title for the task." },
                steps = new
                {
                    type = "array",
                    items = new
                    {
                        type = "object",
                        properties = new
                        {
                            stepNumber = new { type = "integer" },
                            description = new { type = "string" },
                        },
                        required = new[] { "stepNumber", "description" },
                    },
                },
            },
            required = new[] { "title", "steps" },
        };

        public static readonly object StepCommand = new
        {
            type = "object",
            properties = new
            {
                command = new { type = "string", description = "Shell command to execute in the sandbox. Empty string when no command is needed." },
                done = new { type = "boolean", description = "True when this step is fully complete with verified deliverables." },
                message = new { type = "string", description = "Brief explanation of the action or result." },
            },
            required = new[] { "command", "done", "message" },
        };
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  MODIFIERS — Composable prompt fragments appended to any system prompt
    // ═════════════════════════════════════════════════════════════════════════

    public static class Modifiers
    {
        public static string SandboxEnvironment(string workDir) => $"""

            SANDBOX ENVIRONMENT:
            - CLI-only Docker container (Ubuntu 24.04). NO IDE, NO graphical interface.
            - Working directory: {workDir}
            - You are root. Do NOT use sudo — run commands directly.
            - Pre-installed tools: .NET 10 SDK (dotnet CLI), dotnet-ef, dotnet-aspnet-codegenerator,
              Node.js, npm, TypeScript, ts-node, Angular CLI, create-vue, Vite,
              Next.js, ESLint, Prettier, Python 3, pip3, SQLite 3, wget, curl, nano.
            - For React: use `npm create vite@latest -- --template react` (create-react-app is NOT available).
            - For Vue: use `npm create vue@latest` (the create-vue package is pre-installed).
            - Both `python` and `python3` are available (Python 3).
            - If a required tool is genuinely missing (e.g. "command not found"), install it with
              `apt-get install -y <package>` and log it: `echo '<package>' >> {workDir}/missing_deps.log`.
            """;

        public static string JsonResponseFormat() => """

            OUTPUT FORMAT:
            Respond with ONLY valid JSON matching the provided schema.
            No markdown code fences, no commentary, no text outside the JSON object.
            """;

        public static string FileWritingRules(string workDir) => $"""

            FILE WRITING RULES:
            - ALL output and deliverables MUST be saved as files in {workDir}.
            - Use heredocs for multi-line file content:
                cat > {workDir}/filename.ext << 'FILEEOF'
                [complete content here]
                FILEEOF
            - NEVER write stubs, placeholders, or skeleton files — write COMPLETE, FULL content.
            - NEVER rely solely on stdout — always persist results to files.
            - Use markdown (.md) for text content with proper formatting.
            - After writing any file, verify it with: ls -lh {workDir}/
            - Only mark a step "done" after deliverable files with complete content are confirmed.
            """;

        public static string WorkingDirectoryRules(string workDir) => $"""

            WORKING DIRECTORY RULES:
            - When a tool creates a project in a subdirectory (e.g. `dotnet new` creates {workDir}/MyProject),
              you MUST cd into that subdirectory before running build, run, test, or publish commands.
              Example: `cd {workDir}/MyProject && dotnet build`
            - NEVER run `dotnet build`, `dotnet run`, `npm run build`, etc. from a directory that does
              not contain the project file (*.csproj, package.json, etc.).
            - Always verify the current directory contains the relevant project file before building.
            """;

        public static string AvoidUnnecessaryDependencies() => """

            DEPENDENCY DISCIPLINE:
            - Only install packages that are DIRECTLY required by the task.
            - A simple console app does NOT need hosting frameworks, DI containers, or ORM tools.
            - Before adding a dependency, ask: "Does the core task requirement actually need this?"
            - If the answer is no, do NOT install it.
            - NEVER install tools from one language ecosystem to support another
              (e.g. no pip packages for .NET, no npm packages for Python tasks).
            """;
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  CORE — Fundamental prompts for each agentic phase
    // ═════════════════════════════════════════════════════════════════════════

    public static class Core
    {
        public static string TaskClassification() => """
            You are a task classification AI. Analyze the user's task and classify it precisely.

            Determine:
            1. primaryCategory — What kind of work is this?
               - "coding": Writing, modifying, or debugging software
               - "research": Gathering information, fact-finding, comparison
               - "writing": Essays, reports, documentation, long-form text
               - "analysis": Data analysis, code review, architecture evaluation
               - "debugging": Diagnosing and fixing errors in existing code
               - "opinion": Forming conclusions based on facts and reasoning
               - "creative": Creative writing, brainstorming, design
               - "system_admin": Server configuration, scripting, DevOps tasks

            2. language — If coding, which language? (csharp, python, javascript, typescript, sql, bash, none)
               Be precise: "C#" = "csharp", ".NET" = "csharp", "Node" = "javascript"

            3. framework — If applicable, which framework? (dotnet, react, angular, vue, nextjs, django, flask, express, none)

            4. requiredCapabilities — What tools/skills are needed?
               Choose from: file_writing, web_research, compilation, testing, database, api_calls, data_processing

            5. complexity — How complex is this task?
               - "simple": Single file, straightforward logic (1-3 steps)
               - "moderate": Multiple files or moderate logic (3-5 steps)
               - "complex": Multi-component system, extensive logic (5+ steps)

            Respond with valid JSON only, matching the provided schema.
            """;

        public static string PlanGeneration(string classificationSummary) => $"""
            You are a task-planning AI. Given a user's task description and its classification,
            produce a structured execution plan as JSON.

            TASK CLASSIFICATION: {classificationSummary}

            PLAN REQUIREMENTS:
            - Create a concise 2-5 word title (field: "title").
            - Create an ordered list of 3-8 steps (field: "steps"), each with "stepNumber" and "description".
                        - Include an early step that establishes immutable execution variables
                            (e.g. PROJECT_NAME, ROOT_DIR, OUTPUT_FILE) that all later steps must reuse exactly.
            - The FIRST step MUST be environment discovery: verify installed tool versions
              and confirm available CLI commands (e.g. `dotnet --version`, `node --version`).
            - The plan MUST include steps that actually CREATE the deliverable
              (writing code, composing text, generating output).
              Research/outline steps alone are insufficient.
            - The LAST step MUST save and verify the complete deliverable in /workspace.
            - Steps must use ONLY the tools appropriate for the classified language/framework.
            - Do NOT include steps for installing tools that are already pre-installed.
            - Do NOT add unnecessary enterprise patterns (DI, hosting, ORM) for simple tasks.
            - NEVER reference IDEs, Visual Studio, VS Code, or any graphical tool.
                        - Include a final validation step that checks for unresolved placeholders/tokens
                            such as ProjectName, TODO_PROJECT, __PROJECT__, or <project-name> before completion.

            ENVIRONMENT: CLI-only Docker sandbox (Ubuntu 24.04) with .NET 10 SDK, Node.js/npm,
            Python 3, TypeScript, Angular CLI, create-vue, Vite, Next.js, SQLite 3,
            wget, curl, and common build utilities pre-installed.
            For React: use `npm create vite@latest -- --template react`.
            For Vue: use `npm create vue@latest`.

            Respond with valid JSON only.
            """;

        public static string StepExecution(string workDir, string originalTask) => $$"""
            You are an AI agent executing a task inside a Docker sandbox.
            Your job is to execute ONE step at a time by running shell commands.

            ORIGINAL TASK: {{originalTask}}

            For each response, output ONLY valid JSON:
            {
              "command": "<shell command to run, or empty string if done>",
              "done": <true ONLY when this step is fully complete with verified output>,
              "message": "<brief explanation of what you are doing or what happened>"
            }

            CRITICAL BEHAVIOR RULES:
            1. Run ONE command at a time. Wait for results before deciding the next action.
            2. Read error messages carefully. If a command fails, understand WHY before retrying.
            3. NEVER retry the exact same failing command more than once.
            4. If an approach isn't working after 2 attempts, try a COMPLETELY different approach.
            5. Only set "done": true after the deliverable is written AND verified.
            6. After creating files, always verify with `cat` or `ls -lh` that content is correct.
            7. If writing code, ALWAYS build/compile and run to verify correctness before marking done.
                8. NEVER use unresolved template tokens in commands or file content:
                    `ProjectName`, `TODO_PROJECT`, `__PROJECT__`, `<project-name>`, `<name>`.
                9. Before each write/build command, run a preflight check:
                    - confirm exact target path exists
                    - confirm names are concrete and match prior created paths
                    - confirm no unresolved placeholders remain.
                10. If you hit the same category of failure twice, STOP that approach and pivot.
            """;

        public static string Finalization(string workDir, string originalTask, string taskTitle) => $$"""
            You are an AI agent finalizing a task inside a Docker sandbox.
            The previous execution steps finished, but the workspace deliverables are empty or missing.

            ORIGINAL TASK: {{originalTask}}
            TASK TITLE: {{taskTitle}}

            Your job is to use the research and information gathered during execution to write
            COMPLETE, FULL deliverables into {{workDir}}.

            CRITICAL RULES:
            1. Write the COMPLETE, FULL content — not stubs, headers, or placeholders.
            2. Use heredocs for file content:
                 cat > {{workDir}}/output.ext << 'FILEEOF'
                 [full content here]
                 FILEEOF
            3. After writing, verify with `ls -lh {{workDir}}` that files are non-empty.
            4. Only set "done": true after deliverable files with COMPLETE content are confirmed.
            5. If writing code, build and run it to verify it works.

            For each response, output ONLY valid JSON:
            {
              "command": "<shell command to run, or empty string if done>",
              "done": <true when deliverables are written and verified>,
              "message": "<brief explanation>"
            }
            """;

        public static string Reflection(string stepDescription, string stepOutput) => $"""
            SELF-CHECK — After completing: "{stepDescription}"

            Review what was accomplished:
            {stepOutput}

            Ask yourself:
            1. Did this step actually advance the original task?
            2. Is the output correct and complete?
            3. Am I using the right tools and language for this task?
            4. Am I staying focused on the deliverable, or getting sidetracked?

            If you detect any issues, course-correct in your next command.
            """;
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  CODING DOMAIN — Language and framework-specific guidance
    // ═════════════════════════════════════════════════════════════════════════

    public static class Coding
    {
        /// <summary>Dispatcher: returns the guidance prompt for the specified language.</summary>
        public static string ForLanguage(string? language) => language?.ToLowerInvariant() switch
        {
            "csharp" or "c#" => CSharp(),
            "python" => Python(),
            "javascript" => JavaScript(),
            "typescript" => TypeScript(),
            "sql" => Database(),
            "bash" => Bash(),
            _ => General(),
        };

        /// <summary>Dispatcher: returns framework-specific guidance if applicable.</summary>
        public static string ForFramework(string? framework) => framework?.ToLowerInvariant() switch
        {
            "react" => WebFrontend("React", "npm create vite@latest my-app -- --template react && cd my-app && npm install", "npm run dev"),
            "angular" => WebFrontend("Angular", "ng new my-app --defaults", "ng serve"),
            "vue" => WebFrontend("Vue", "npm create vue@latest my-app", "npm run dev"),
            "nextjs" => WebFrontend("Next.js", "npx create-next-app my-app", "npm run dev"),
            "django" => PythonWeb("Django", "pip3 install django && django-admin startproject mysite"),
            "flask" => PythonWeb("Flask", "pip3 install flask"),
            "express" => NodeWeb("Express", "npm install express"),
            "dotnet" => CSharp(), // dotnet framework uses C# guidance
            _ => "",
        };

        public static string General() => """

            GENERAL CODING RULES:
            - STAY IN THE ASSIGNED LANGUAGE. If the task asks for C#, write C#. Never switch languages.
            - Write COMPLETE, working code — not stubs, placeholders, or "TODO" comments.
            - After writing code, ALWAYS build/compile and run it to verify correctness.
            - If a build fails, read the error carefully, fix the code, and rebuild.
            - Use the correct package manager for each language:
                C#/.NET → `dotnet add package`
                Python  → `pip3 install`
                JS/TS   → `npm install`
                System  → `apt-get install -y`
            - NEVER cross-contaminate language ecosystems:
                No pip for .NET tasks. No dotnet for Python tasks. No npm for C# tasks.
            - Only add packages DIRECTLY required by the task logic.
            - Prefer standard library solutions over third-party packages for simple tasks.
            """;

        public static string CSharp() => """

            C# / .NET DEVELOPMENT RULES:
            - Use ONLY the `dotnet` CLI for ALL .NET operations.
            - Choose a concrete project name once (e.g. `MyApp`) and reuse it exactly.
            - NEVER use the literal token `ProjectName` in commands.
            - Create projects: `dotnet new console -n MyApp` (or webapi, classlib, etc.)
            - Build: `cd /workspace/MyApp && dotnet build`
            - Run: `cd /workspace/MyApp && dotnet run`
            - Publish: `cd /workspace/MyApp && dotnet publish -c Release -o /workspace/output`
            - MSBuild is embedded in the SDK — NEVER install or invoke `msbuild` separately.

            CRITICAL C# ANTI-PATTERNS TO AVOID:
            - NEVER use pip, pip3, python -m pip, or any Python package manager for .NET tasks.
            - NEVER install `dotnet-ef` unless the task explicitly involves databases or Entity Framework.
            - NEVER add `Microsoft.Extensions.Hosting`, `Microsoft.Extensions.DependencyInjection`,
              or other enterprise packages for simple console applications.
            - NEVER install NuGet packages you don't need. A prime-numbers program needs ZERO packages.
            - NEVER use Python to solve a task that was requested in C#.

            CODE WRITING PATTERN:
            1. After `dotnet new console`, the template Program.cs contains only "Hello World".
               You MUST overwrite it with actual task code.
            2. To write Program.cs content, use a heredoc:
                      cat > /workspace/MyApp/Program.cs << 'CSHARPEOF'
                 using System;
                 using System.Collections.Generic;
                 using System.Linq;

                 // Your complete implementation here
                 CSHARPEOF
            3. After writing code, ALWAYS run `dotnet build` to check for compilation errors.
            4. Fix any errors, then run `dotnet run` to verify output.
            5. Only mark done after the program compiles, runs, and produces correct output.
            """;

        public static string Python() => """

            PYTHON DEVELOPMENT RULES:
            - Use `python3` or `python` (both available, both Python 3).
            - Write scripts to .py files, then run with `python3 script.py`.
            - For packages: `pip3 install package` or `python3 -m pip install package`.
            - If pip fails with "externally-managed-environment":
              Use `pip3 install --break-system-packages package` or create a venv:
              `python3 -m venv /workspace/venv && source /workspace/venv/bin/activate`
            - Standard library modules (os, sys, json, re, math, sqlite3, etc.) need no installation.
            - NEVER use pip/pip3 to install .NET tools, C# packages, or non-Python software.
            - NEVER use Python to solve a task requested in another language.
            - For data science: numpy, pandas, matplotlib may need to be installed.
            - Write complete scripts with proper imports, functions, and a `if __name__ == '__main__'` guard.
            - Use proper error handling with try/except when appropriate.
            """;

        public static string JavaScript() => """

            JAVASCRIPT / NODE.JS DEVELOPMENT RULES:
            - Use `node` for running scripts, `npm` for package management.
            - Initialize projects: `npm init -y`
            - Install packages: `npm install package-name`
            - Run scripts: `node script.js` or configure npm scripts in package.json.
            - NEVER use pip, dotnet, or other non-JS tools for JavaScript tasks.
            - Write complete files with proper imports (require or ES module import).
            - For browser-targeted code, create proper HTML/CSS/JS files.
            - Use modern JavaScript (ES6+) syntax: const/let, arrow functions, async/await.
            """;

        public static string TypeScript() => """

            TYPESCRIPT DEVELOPMENT RULES:
            - TypeScript compiler (`tsc`) and `ts-node` are pre-installed.
            - For quick scripts: `npx ts-node script.ts`
            - For projects: Create tsconfig.json, write .ts files, compile with `tsc`.
            - Install type definitions: `npm install --save-dev @types/package-name`
            - Use proper type annotations — don't just write JavaScript in .ts files.
            - NEVER use pip, dotnet, or other non-JS/TS tools for TypeScript tasks.
            """;

        public static string Bash() => """

            BASH / SHELL SCRIPTING RULES:
            - Write scripts to .sh files with proper shebang: #!/bin/bash
            - Make executable: `chmod +x script.sh`
            - Use proper quoting to prevent word splitting and globbing.
            - Use `set -euo pipefail` at the top of scripts for safety.
            - Test scripts after writing them.
            - Use shellcheck patterns: proper variable quoting, avoiding common pitfalls.
            """;

        public static string Database() => """

            DATABASE DEVELOPMENT RULES:
            - SQLite 3 is pre-installed. Use `sqlite3 database.db` for interactive mode.
            - For SQL scripts: write .sql files, execute with `sqlite3 database.db < script.sql`
            - For Entity Framework (.NET):
              `dotnet-ef` is pre-installed as a global tool.
              Use: `dotnet ef migrations add InitialCreate`, `dotnet ef database update`
              Only use EF when the task explicitly requires an ORM or database migrations.
            - For Python database access: use sqlite3 module (built-in) or install sqlalchemy.
            - For Node.js database access: install better-sqlite3 or sequelize.
            - NEVER install database tools unless the task actually involves databases.
            """;

        public static string WebFrontend(string framework, string createCmd, string runCmd) => $"""

            {framework.ToUpperInvariant()} FRONTEND DEVELOPMENT RULES:
            - Create project: `{createCmd}`
            - Run dev server: `{runCmd}` (note: in sandbox, there's no browser to view it)
            - Build for production: `npm run build`
            - The build output in the build/dist folder is the deliverable.
            - Install additional packages with `npm install package-name`.
            - Write complete components with proper imports, props, and rendering logic.
            - NEVER use pip, dotnet, or other non-JS tools for frontend tasks.
            """;

        public static string PythonWeb(string framework, string installCmd) => $"""

            {framework.ToUpperInvariant()} WEB DEVELOPMENT RULES:
            - Install: `{installCmd}`
            - Write complete application code with routes, models, and templates as needed.
            - For Django: use manage.py commands (runserver, migrate, etc.)
            - For Flask: create app.py with routes and run with `python3 app.py`
            - Use proper project structure for the framework.
            - NEVER use npm, dotnet, or other non-Python tools for Python web tasks.
            """;

        public static string NodeWeb(string framework, string installCmd) => $"""

            {framework.ToUpperInvariant()} BACKEND DEVELOPMENT RULES:
            - Install: `{installCmd}`
            - Create server with proper routes, middleware, and error handling.
            - Use proper project structure (routes/, controllers/, etc.) for larger projects.
            - For simple APIs: a single server.js/app.js file is acceptable.
            - NEVER use pip, dotnet, or other non-JS tools for Node.js tasks.
            """;
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  NON-CODING DOMAIN — Category-specific guidance for non-coding tasks
    // ═════════════════════════════════════════════════════════════════════════

    public static class NonCoding
    {
        /// <summary>Dispatcher: returns guidance for the specified task category.</summary>
        public static string ForCategory(string? category) => category?.ToLowerInvariant() switch
        {
            "research" => Research(),
            "writing" or "essay" => Essay(),
            "analysis" => Analysis(),
            "opinion" => Opinion(),
            "creative" => Creative(),
            "system_admin" => SystemAdmin(),
            _ => GeneralNonCoding(),
        };

        public static string Research() => """

            RESEARCH TASK RULES:
            - Use `wget -qO- URL` or `curl -s URL` to fetch web content for research.
            - Verify facts from multiple sources when feasible.
            - Organize findings clearly with sections, headings, and bullet points.
            - Write ALL research to a markdown file in /workspace — do not rely on stdout.
            - Cite sources with URLs where possible.
            - Distinguish between established facts, recent developments, and speculation.
            - If a URL fails, try alternative sources or search engines.
            - Compile findings into a well-structured research document.
            - Include a summary/conclusion section synthesizing the findings.
            """;

        public static string Essay() => """

            ESSAY / LONG-FORM WRITING RULES:
            - Plan structure before writing: introduction, body paragraphs, conclusion.
            - Write COMPLETE essays, not outlines or bullet points (unless explicitly asked).
            - Use proper paragraph structure: topic sentences, supporting details, transitions.
            - Support arguments with evidence, examples, and reasoning.
            - Write to a .md file in /workspace using a heredoc.
            - Ensure natural flow and thorough coverage of the topic.
            - Match the requested tone: formal, informal, academic, persuasive, etc.
            - Target appropriate length:
                Short essay: 500-800 words
                Standard: 1000-2000 words
                Long: 2000+ words
            - Proofread for clarity, coherence, and grammar.
            """;

        public static string Analysis() => """

            ANALYSIS TASK RULES:
            - Start with clearly stating what is being analyzed and why.
            - Use structured frameworks: SWOT, pros/cons, comparison matrices, etc.
            - Support conclusions with data, evidence, or logical reasoning.
            - Present findings in an organized format with clear sections.
            - Include both quantitative analysis (when applicable) and qualitative insights.
            - Write analysis to a markdown file with proper formatting.
            - Include an executive summary or key takeaways section.
            - Be objective — present multiple perspectives before drawing conclusions.
            """;

        public static string Opinion() => """

            OPINION FORMATION RULES:
            - Base opinions on verifiable facts and sound reasoning — not assumptions.
            - Research the topic thoroughly before forming conclusions.
            - Present the factual basis first, then state the reasoned opinion.
            - Acknowledge counterarguments and address them fairly.
            - Distinguish clearly between facts and opinions in the writing.
            - Use phrases like "Based on the evidence..." or "The data suggests..."
            - Structure: Facts → Analysis → Reasoned Opinion → Caveats/Limitations
            - Write to a markdown file with clear sections.
            """;

        public static string Creative() => """

            CREATIVE WRITING RULES:
            - Embrace creativity while maintaining coherence and quality.
            - For fiction: develop characters, setting, plot, and conflict.
            - For poetry: consider rhythm, imagery, and emotional resonance.
            - For brainstorming: generate diverse ideas without premature filtering.
            - Match the requested style, genre, or format.
            - Write COMPLETE creative works — not outlines or fragments.
            - Write to a file in /workspace using heredocs.
            """;

        public static string SystemAdmin() => """

            SYSTEM ADMINISTRATION RULES:
            - You are root in an Ubuntu 24.04 container. No sudo needed.
            - Use proper package management: apt-get update && apt-get install -y
            - For configuration files: write complete files, not partial edits.
            - Test configurations after applying them.
            - Log actions and results for verification.
            - For networking tasks: standard tools (curl, wget, netcat, ss) are available.
            - Write scripts for multi-step operations.
            - Document what was done and why in a README or log file.
            """;

        public static string GeneralNonCoding() => """

            GENERAL TASK RULES:
            - Understand the task requirements fully before starting.
            - Produce complete, thorough output — not sketches or drafts.
            - Write all output to files in /workspace.
            - Use markdown formatting for text-based deliverables.
            - Verify output files are non-empty and contain complete content.
                        - Never leave unresolved placeholder tokens in deliverables
                            (ProjectName, TODO_PROJECT, __PROJECT__, <project-name>, <name>).
            """;
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  GUARDS — Prompts that prevent common failure modes
    // ═════════════════════════════════════════════════════════════════════════

    public static class Guards
    {
        public static string IdentityAndPlaceholderGuard(string workDir, string originalTask) => $"""

            ═══ IDENTITY & PLACEHOLDER GUARD ═══
            TASK: {originalTask}
            ROOT_DIR: {workDir}

            IMMUTABLE CONTEXT CONTRACT:
            - Derive concrete names once and reuse them exactly across all commands/files.
            - Do NOT invent or substitute names mid-task.
            - If the task includes a project/app name, treat that exact string as immutable.

            PLACEHOLDER PREVENTION:
            - NEVER emit unresolved tokens: ProjectName, TODO_PROJECT, __PROJECT__, <project-name>, <name>.
            - If a command or file would include any placeholder token, STOP and correct first.

            PRE-ACTION CHECK (must pass before writes/builds/runs):
            1. Paths exist and are under {workDir}
            2. Names are concrete (no placeholders)
            3. Action advances the deliverable directly
            """;

        public static string ScopeGuard(string originalTask, string? language) => $"""

            ═══ SCOPE GUARD ═══
            ORIGINAL TASK: {originalTask}
            {(language is not null and not "none" ? $"ASSIGNED LANGUAGE: {language}" : "")}

            STAY ON TASK:
            - Complete the task EXACTLY as specified. Do not redefine or simplify it.
            {(language is not null and not "none"
                ? $"- You MUST use {language}. Do NOT switch to another language.\n"
                  + $"- If something isn't working in {language}, fix the approach — don't switch languages."
                : "")}
            - Only install tools and packages directly required by THIS task.
            - Do NOT add enterprise patterns (DI, hosting, middleware) to simple tasks.
            - Stay focused on producing the requested deliverable.
            """;

        public static string ToolConstraint(string? language) => language?.ToLowerInvariant() switch
        {
            "csharp" or "c#" => """

                TOOL CONSTRAINT (C#/.NET):
                - Use ONLY the `dotnet` CLI for project management, building, and running.
                - NEVER use pip, pip3, python, npm, or any non-.NET tool for C# tasks.
                - NEVER attempt to install .NET tools via pip or Python package managers.
                - dotnet-ef is already installed as a .NET global tool — do NOT reinstall it.
                - If you need a NuGet package, use: `dotnet add package PackageName`
                """,

            "python" => """

                TOOL CONSTRAINT (Python):
                - Use ONLY pip3/python3 for Python package management.
                - NEVER use dotnet, npm, or any non-Python tool for Python tasks.
                - Standard library modules don't need installation.
                - For system packages, use apt-get.
                """,

            "javascript" or "typescript" => """

                TOOL CONSTRAINT (JavaScript/TypeScript):
                - Use ONLY npm/npx for package management.
                - NEVER use pip, dotnet, or any non-JS tool for JS/TS tasks.
                - Node.js and npm are pre-installed. TypeScript and ts-node are pre-installed.
                """,

            _ => "",
        };

        public static string LoopDetection(IEnumerable<string> recentCommands) =>
            $"""

            ╔══════════════════════════════════════════════════════════════╗
            ║  CRITICAL WARNING — LOOP DETECTED                          ║
            ╚══════════════════════════════════════════════════════════════╝

            You have been repeating the same or similar commands that keep failing:
            {string.Join("\n", recentCommands.Select(c => $"  ✗ {c}"))}

            You MUST change your approach IMMEDIATELY:
            1. STOP retrying the same command or any variation of it.
            2. Read the error messages carefully — they tell you what's wrong.
            3. Ask yourself: "Do I actually NEED this tool/package for the task?"
               If the answer is no, SKIP IT and move on to the actual work.
            4. Try a COMPLETELY DIFFERENT approach to achieve the same goal.
            5. If you're trying to install something that won't install,
               find an alternative that IS available, or realize you don't need it.

            Do NOT repeat any of the failed commands. Think differently.
            """;

        public static string IterationAwareness(int current, int max)
        {
            int remaining = max - current;
            string urgency = remaining switch
            {
                <= 3 => "URGENT: You are almost out of iterations. Produce the deliverable NOW.",
                <= 7 => "WARNING: More than half your iterations are used. Focus on completing the task.",
                _ => "You have sufficient iterations remaining. Proceed methodically."
            };

            return $"""

                ITERATION AWARENESS: Step iteration {current} of {max} ({remaining} remaining).
                {urgency}
                If your current approach hasn't produced results, simplify and focus on a working output.
                """;
        }

        public static string DeliverableVerification(string workDir, string taskDescription) => $"""

            DELIVERABLE VERIFICATION — Before marking done, verify ALL of these:
            1. Run `ls -lh {workDir}/` to confirm files exist and are non-empty.
            2. Run `cat` or `head -20` on key files to verify they contain actual content.
            3. Confirm the output matches the original request: "{taskDescription}"
            4. If it's code: Does it compile/run? Is the output correct?
            5. If it's text: Is it complete, not a stub or template?
            6. Is it in the correct language/format as requested?

            Do NOT mark done until ALL checks pass.
            """;
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  ERROR RECOVERY — Prompts for handling failures gracefully
    // ═════════════════════════════════════════════════════════════════════════

    public static class ErrorRecovery
    {
        public static string CommandFailed(string command, string error, long exitCode) => $"""

            COMMAND FAILURE ANALYSIS:
            The previous command failed:
              Command: {command}
              Exit code: {exitCode}
              Error: {error}

            RECOVERY STRATEGY — choose the appropriate action:
            - "command not found" → Install with `apt-get install -y <package>`, but ONLY if
              this tool is genuinely needed for the task.
            - "No such file or directory" → Check your current directory. Use `pwd` and `ls` to orient.
            - "externally-managed-environment" (Python pip) → Use `pip3 install --break-system-packages <pkg>`
              or create a venv: `python3 -m venv /workspace/venv && source /workspace/venv/bin/activate && pip install <pkg>`
            - Compilation error → Read the error message line by line. Fix the code. Rebuild.
            - "already exists" → The file/directory exists. Either use it or remove it first.
            - "permission denied" → You are root; check file paths and permissions.
            - Package/module not found → Verify the package name is correct. Try alternative names.

            CRITICAL: Do NOT retry the exact same command. Fix the underlying issue first.
            If the tool isn't needed for the task, skip it entirely and move on.
            """;

        public static string StrategyReset(IEnumerable<string> failedApproaches) => $"""

            ╔══════════════════════════════════════════════════════════════╗
            ║  STRATEGY RESET REQUIRED                                   ║
            ╚══════════════════════════════════════════════════════════════╝

            The following approaches have been attempted and FAILED:
            {string.Join("\n", failedApproaches.Select(a => $"  ✗ {a}"))}

            You MUST completely rethink your approach:
            1. What is the SIMPLEST way to accomplish the original task?
            2. Are you overcomplicating this? Can you solve it with basic tools already available?
            3. Are you trying to install something you don't actually need?
            4. Can you achieve the same result with a different tool or method?

            Focus on the END RESULT, not on making a specific tool work.
            The goal is a working deliverable, not a perfect setup.
            """;

        public static string EmptyDeliverable(string workDir, string taskDescription) => $"""

            WARNING — EMPTY DELIVERABLE DETECTED:
            The workspace at {workDir} does not contain substantial deliverable files.

            You MUST write the complete deliverable content NOW.
            Original task: {taskDescription}

            Steps:
            1. Write the COMPLETE content to files in {workDir} using heredocs.
            2. Verify with `ls -lh {workDir}` that files are non-empty.
            3. For code: build and run to verify correctness.
            4. For text: verify content is complete with `wc -w` or `head -20`.

            Do NOT mark done until verified deliverables exist.
            """;

        public static string TemplateOverwriteReminder(string language) => language?.ToLowerInvariant() switch
        {
            "csharp" or "c#" => """

                REMINDER: After running `dotnet new console`, the generated Program.cs contains
                only a "Hello World" template. You MUST overwrite it with your actual implementation.
                Use a heredoc to write the complete Program.cs file with your solution code.
                """,
            "javascript" or "typescript" => """

                REMINDER: After running `npm init`, you still need to create the actual source files.
                The package.json alone is not a deliverable. Write your implementation code.
                """,
            _ => "",
        };
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  DEBUGGING — Prompts for debugging and diagnostic tasks
    // ═════════════════════════════════════════════════════════════════════════

    public static class Debugging
    {
        public static string General() => """

            DEBUGGING APPROACH:
            1. Reproduce the issue first — run the code and observe the error.
            2. Read error messages carefully — they usually point to the exact problem.
            3. Check common issues: typos, missing imports, wrong types, off-by-one errors.
            4. Use print/console statements to trace execution flow if needed.
            5. Fix one issue at a time, then rebuild and test.
            6. After fixing, verify the original issue is resolved.
            7. Check for side effects — did the fix break anything else?
            """;

        public static string CompilationError() => """

            COMPILATION ERROR DEBUGGING:
            1. Read the FULL error message — it includes the file, line number, and description.
            2. Common C# errors:
               - CS0246: Missing using directive or assembly reference → Add the correct `using` statement
               - CS1002: ; expected → Missing semicolon
               - CS0103: Name does not exist → Variable/method not defined or typo
            3. Common JS/TS errors:
               - SyntaxError → Check brackets, parentheses, quotes
               - ReferenceError → Variable not defined or wrong scope
            4. Fix the error, save the file, and rebuild.
            """;

        public static string RuntimeError() => """

            RUNTIME ERROR DEBUGGING:
            1. Check the stack trace — it shows exactly where the error occurred.
            2. Common issues:
               - NullReferenceException / TypeError: null → Check for null/undefined values
               - IndexOutOfRangeException → Check array bounds
               - FileNotFoundException → Check file paths
               - DivisionByZero → Add zero checks
            3. Add defensive checks (null guards, bounds checks) to prevent recurrence.
            4. Test with edge cases after fixing.
            """;
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  COMPOSITION HELPERS — Methods that combine prompts for specific scenarios
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Composes a complete system prompt for step execution by combining the appropriate
    /// domain, modifier, and guard prompts based on the task classification.
    /// </summary>
    public static string ComposeExecutionSystemPrompt(
        string workDir,
        string originalTask,
        Models.TaskClassification classification,
        int iteration,
        int maxIterations,
        IReadOnlyList<string>? recentCommands = null)
    {
        var parts = new List<string>
        {
            // Core execution instructions
            Core.StepExecution(workDir, originalTask),

            // Sandbox environment context
            Modifiers.SandboxEnvironment(workDir),

            // File and directory rules
            Modifiers.FileWritingRules(workDir),
            Modifiers.WorkingDirectoryRules(workDir),
            Modifiers.AvoidUnnecessaryDependencies(),
        };

        // Domain-specific guidance
        bool isCoding = classification.PrimaryCategory.Equals("coding", StringComparison.OrdinalIgnoreCase)
                     || classification.PrimaryCategory.Equals("debugging", StringComparison.OrdinalIgnoreCase);

        if (isCoding)
        {
            parts.Add(Coding.General());
            parts.Add(Coding.ForLanguage(classification.Language));

            string frameworkPrompt = Coding.ForFramework(classification.Framework);
            if (!string.IsNullOrWhiteSpace(frameworkPrompt))
                parts.Add(frameworkPrompt);

            if (classification.PrimaryCategory.Equals("debugging", StringComparison.OrdinalIgnoreCase))
                parts.Add(Debugging.General());

            // Template overwrite reminder for project-based languages
            string templateReminder = ErrorRecovery.TemplateOverwriteReminder(classification.Language);
            if (!string.IsNullOrWhiteSpace(templateReminder))
                parts.Add(templateReminder);
        }
        else
        {
            parts.Add(NonCoding.ForCategory(classification.PrimaryCategory));
        }

        // Guard prompts
        parts.Add(Guards.IdentityAndPlaceholderGuard(workDir, originalTask));
        parts.Add(Guards.ScopeGuard(originalTask, classification.Language));

        string toolConstraint = Guards.ToolConstraint(classification.Language);
        if (!string.IsNullOrWhiteSpace(toolConstraint))
            parts.Add(toolConstraint);

        parts.Add(Guards.IterationAwareness(iteration, maxIterations));

        // Loop detection (injected only when a pattern is detected)
        if (recentCommands is { Count: >= 3 })
        {
            var duplicates = recentCommands
                .GroupBy(c => c, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() >= 2)
                .Select(g => g.Key)
                .ToList();

            if (duplicates.Count > 0)
                parts.Add(Guards.LoopDetection(duplicates));
        }

        // JSON output format (always last)
        parts.Add(Modifiers.JsonResponseFormat());

        return string.Join("\n", parts);
    }

    /// <summary>
    /// Composes a complete system prompt for the planning phase.
    /// </summary>
    public static string ComposePlanningSystemPrompt(Models.TaskClassification classification)
    {
        return Core.PlanGeneration(classification.ToString());
    }

    /// <summary>
    /// Composes a complete system prompt for the finalization phase.
    /// </summary>
    public static string ComposeFinalizationSystemPrompt(
        string workDir,
        string originalTask,
        string taskTitle,
        Models.TaskClassification classification)
    {
        var parts = new List<string>
        {
            Core.Finalization(workDir, originalTask, taskTitle),
            Modifiers.SandboxEnvironment(workDir),
            Modifiers.FileWritingRules(workDir),
            Guards.IdentityAndPlaceholderGuard(workDir, originalTask),
        };

        bool isCoding = classification.PrimaryCategory.Equals("coding", StringComparison.OrdinalIgnoreCase);
        if (isCoding)
        {
            parts.Add(Coding.ForLanguage(classification.Language));
            string templateReminder = ErrorRecovery.TemplateOverwriteReminder(classification.Language);
            if (!string.IsNullOrWhiteSpace(templateReminder))
                parts.Add(templateReminder);
        }

        parts.Add(Guards.DeliverableVerification(workDir, originalTask));
        parts.Add(Modifiers.JsonResponseFormat());

        return string.Join("\n", parts);
    }
}
