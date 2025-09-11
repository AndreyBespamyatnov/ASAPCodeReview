# ASAPCodeReview — Local AI code review (Tabby + .NET)

ASAPCodeReview is a tiny proof‑of‑concept that runs an AI‑assisted code review for a Bitbucket Pull Request using a locally hosted LLM via Tabby. No code leaves your machine.

## What it does
- Takes a Bitbucket PR URL (or workspace/repo/pr args)
- Clones the source branch and computes a diff to the target
- Sends the diff to a local model through Tabby
- Posts the review back to the PR, or starts a draft review you can publish later (configurable), or prints in dry‑run

## Stack
- .NET 9 console app
- TabbyML as the local LLM server (e.g., Qwen2‑1.5B‑Instruct for chat, StarCoder‑1B for code)
- Bitbucket REST API for PR metadata and comments

## Quick start (Windows / PowerShell)
1) Start Tabby (GPU example; CPU works with `--device cpu`):
```
.\tabby.exe serve --model StarCoder2‑7B --chat-model Qwen2‑7B‑Instruct --device cuda --port 8080
```
2) Create a user and copy the API key from: http://127.0.0.1:8080/

3) Configure app settings (preferred):
- Open ASAPCodeReview\\appsettings.json.
- Fill in your Bitbucket username and App Password, and your Tabby API key.
- Example minimal config:
```
{
  "Bitbucket": {
    "Username": "<your-username-or-email>",
    "ApiToken": "<your-app-password>"
  },
  "Tabby": {
    "Endpoint": "http://localhost:8080/v1/chat/completions",
    "ApiKey": "<tabby-api-key>"
  }
}
```
Note: Environment variables can still override these values if you prefer (BITBUCKET_USERNAME, BITBUCKET_API_TOKEN, TABBY_API_KEY, TABBY_ENDPOINT, etc.).

4) Run against a PR URL:
```
dotnet run --project .\ASAPCodeReview\ASAPCodeReview.csproj -- "https://bitbucket.org/<workspace>/<repo>/pull-requests/<id>"
```

Tips:
- For dry-run, set `Tool.DryRun` to `true` in appsettings.json (or export `TOOL_DRY_RUN=true`).
- Choose how to handle the review output using `Tool.PublishMode` (or env `TOOL_PUBLISH_MODE`):
  - `Publish` (default): immediately post a PR comment.
  - `StartReview`: attempt to create a draft review comment you can publish later in Bitbucket.
- Other optional settings (appsettings.json or env): `BITBUCKET_WORKSPACE`, `BITBUCKET_REPO_SLUG`, `TABBY_TIMEOUT_SECONDS`, `TOOL_TEMP_ROOT`, `TOOL_MAX_DIFF_CHARS`.

## Notes & limitations
- Works surprisingly well for readability, edge cases, and consistency nits.
- Lacks deep project context/history; better docs/ADRs and commit messages improve results.

## Roadmap
- Feed repo docs/ADRs and PR history for more context
- Try larger quantized models
- Add GitHub/GitLab adapters

## Acknowledgments
- Built with help from Junie Pro (Beta)
- Thanks to TabbyML for the local LLM server