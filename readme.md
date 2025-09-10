ASAPCodeReview — Quick Start

Purpose
- Runs an automated code review for a Bitbucket Pull Request (PR).
- Clones the PR source branch, computes a diff against the destination, asks a local Tabby LLM for a review, and posts a comment back to the PR.

Prerequisites
- .NET SDK 9.x (required to build/run this tool)
- Git (required to clone repositories locally)
- Tabby (local LLM server)
  - Download from: https://github.com/TabbyML/tabby/releases
  - GPU recommended; CPU also works but is slower.

Start Tabby
1) Download and extract the Tabby release for your OS.
2) From the Tabby folder, start the server (example on Windows with NVIDIA GPU):
   .\tabby.exe serve --model StarCoder-1B --chat-model Qwen2-1.5B-Instruct --device cuda --port 8080
   Notes:
   - For CPU, replace --device cuda with --device cpu.
   - Keep this running while you use ASAPCodeReview.
3) Open the Tabby UI in your browser and create a user to obtain an API key:
   http://127.0.0.1:8080/

Configure ASAPCodeReview
You can configure via environment variables or appsettings.json (in the build output folder next to the executable). Environment variables override appsettings.json.

Required settings
- BITBUCKET_USERNAME: Your Bitbucket username (often your account name; sometimes email depending on your token setup)
- BITBUCKET_API_TOKEN: Bitbucket App Password (token) with permissions to read PRs and write comments
- TABBY_ENDPOINT: Tabby chat completions endpoint (default: http://localhost:8080/v1/chat/completions)
- TABBY_API_KEY: API key you created in the Tabby UI

Optional settings
- BITBUCKET_WORKSPACE: Default workspace (used when not provided on the command line)
- BITBUCKET_REPO_SLUG: Default repository slug (used when not provided on the command line)
- TABBY_TIMEOUT_SECONDS: HTTP timeout in seconds for Tabby calls (default 600)
- TOOL_TEMP_ROOT: Where to create temporary working folders (default is system temp)
- TOOL_MAX_DIFF_CHARS: Truncate the diff to this many characters before sending to LLM (default 45000)
- TOOL_DRY_RUN: true/false — if true, prints the review to console instead of posting to Bitbucket (default false)

Optional appsettings.json template
Place this file next to the built executable (e.g., ASAPCodeReview\bin\Debug\net9.0\appsettings.json). Environment variables still override these values.
{
  "Bitbucket": {
    "BaseUrl": "https://api.bitbucket.org/2.0",
    "RepoBaseCloneUrl": "https://bitbucket.org",
    "Workspace": "your-workspace",
    "RepoSlug": "your-repo",
    "Username": "your-username",
    "ApiToken": "your-app-password"
  },
  "Tabby": {
    "Endpoint": "http://localhost:8080/v1/chat/completions",
    "ApiKey": "your-tabby-api-key",
    "TimeoutSeconds": 600
  },
  "Tool": {
    "TempRoot": null,
    "MaxDiffChars": 45000,
    "DryRun": false
  }
}

How to Run
Option A — Provide workspace/repo/pr explicitly:
- Windows PowerShell
  $env:BITBUCKET_USERNAME = "<your-username>"
  $env:BITBUCKET_API_TOKEN = "<your-app-password>"
  $env:TABBY_API_KEY = "<tabby-api-key>"
  # TABBY_ENDPOINT defaults to http://localhost:8080/v1/chat/completions

  dotnet run --project .\ASAPCodeReview\ASAPCodeReview.csproj -- --workspace <workspace> --repo <repo-slug> --pr <id>

Option B — Use a Bitbucket PR URL (parsing is built-in):
  dotnet run --project .\ASAPCodeReview\ASAPCodeReview.csproj -- "https://bitbucket.org/<workspace>/<repo>/pull-requests/<id>"

You can also pass the URL with --prUrl:
  dotnet run --project .\ASAPCodeReview\ASAPCodeReview.csproj -- --prUrl "https://bitbucket.org/<workspace>/<repo>/pull-requests/<id>"

Notes
- Make sure Tabby is running and reachable at the endpoint you configured before running the tool.
- If you set BITBUCKET_WORKSPACE and BITBUCKET_REPO_SLUG, you can omit --workspace/--repo and only pass --pr.
- Use TOOL_DRY_RUN=true to preview the review text without posting a comment to the PR.
- The tool clones the repository to a temporary directory (TOOL_TEMP_ROOT if provided, otherwise system temp), checks out the PR source branch, and computes a diff against the PR destination commit.

Troubleshooting
- Authentication
  - Ensure BITBUCKET_USERNAME and BITBUCKET_API_TOKEN are set and valid (App Password with repository read and pull request write/comment permissions).
- Tabby connection
  - Confirm the server is up: visit http://127.0.0.1:8080/ and verify the API key in the UI. The code uses the chat completions endpoint: /v1/chat/completions.
- Large diffs
  - If reviews are cut off, increase TOOL_MAX_DIFF_CHARS (be aware of model/context limits).
- Timeouts
  - For slow models or large diffs, raise TABBY_TIMEOUT_SECONDS.

Quick Example
1) Start Tabby:
   .\tabby.exe serve --model StarCoder-1B --chat-model Qwen2-1.5B-Instruct --device cuda --port 8080
2) Create a user in the UI and copy the API key: http://127.0.0.1:8080/
3) Run the tool with a PR URL:
   dotnet run --project .\ASAPCodeReview\ASAPCodeReview.csproj -- "https://bitbucket.org/acme/widgets/pull-requests/42"