using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Diagnostics;
using LibGit2Sharp;
using Microsoft.Extensions.Configuration;

internal class Program
{
    private static async Task<int> Main(string[] args)
    {
        try
        {
            var (cfg, input) = LoadConfigAndArgs(args);

            Console.WriteLine($"[INFO] Starting review: {input.Workspace}/{input.RepoSlug} PR #{input.PrId}");

            // 1) Get PR details
            var pr = await GetPullRequestAsync(cfg.Bitbucket, input);
            Console.WriteLine($"[INFO] PR found: {pr.title} by {pr.author?.display_name}");

            // 2) Compute diff locally after a single clone (as per Atlassian API token manual)
            var tempDir = PrepareTempDir(cfg.Tool.TempRoot);
            var repoDir = Path.Combine(tempDir, $"{input.Workspace}-{input.RepoSlug}");
            var cloneUrl = $"{cfg.Bitbucket.RepoBaseCloneUrl}/{input.Workspace}/{input.RepoSlug}.git";
            var srcCommit = pr.source?.commit?.hash as string;
            var dstCommit = pr.destination?.commit?.hash as string;
            var branchName = pr.source?.branch?.name as string;

            Console.WriteLine($"[INFO] Using checkout folder: {repoDir}");
            await CloneAndCheckoutAsync(cloneUrl, repoDir, cfg.Bitbucket, srcCommit, branchName);
            Console.WriteLine($"[INFO] Repo ready at {repoDir}");

            var diff = ComputeLocalDiff(repoDir, dstCommit, srcCommit);
            // Ensure the diff isn't too large for the LLM or Bitbucket comment limits
            diff = Truncate(diff, cfg.Tool.MaxDiffChars);

            // 3) Ask Tabby for code review
            Console.WriteLine($"[AI] Asking Tabby for code review...");
            var reviewText = await GetTabbyReviewAsync(cfg.Tabby, cfg.Tool, pr, diff);
            var finalText = (reviewText ?? string.Empty).Trim();

            if (IsNoIssuesSignal(finalText))
            {
                Console.WriteLine("[INFO] Tabby reported no actionable issues. Skipping PR comment:");
                Console.WriteLine("`" + finalText + "`");
            }
            else if (cfg.Tool.DryRun)
            {
                Console.WriteLine("[DRY-RUN] Would post the following comment:\n" + finalText);
            }
            else
            {
                switch (cfg.Tool.PublishMode)
                {
                    case PublishMode.StartReview:
                        await StartPrReviewAsync(cfg.Bitbucket, input, finalText);
                        Console.WriteLine("[INFO] Review started (draft comment created). You can finalize it in Bitbucket.");
                        break;
                    case PublishMode.Publish:
                    default:
                        await PostPrCommentAsync(cfg.Bitbucket, input, finalText);
                        Console.WriteLine("[INFO] Review comment posted.");
                        break;
                }
            }

            Console.WriteLine("[SUCCESS] Done.");
            return 0;
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync("[ERROR] " + ex.Message);
            await Console.Error.WriteLineAsync(ex.ToString());
            return 1;
        }
    }

    private static (AppConfig cfg, InputArgs input) LoadConfigAndArgs(string[] args)
    {
        // load appsettings.json if exists
        var cfg = AppConfig.Load();

        // override from env vars if present
        cfg.Bitbucket.Workspace = GetEnvOrDefault("BITBUCKET_WORKSPACE", cfg.Bitbucket.Workspace);
        cfg.Bitbucket.RepoSlug = GetEnvOrDefault("BITBUCKET_REPO_SLUG", cfg.Bitbucket.RepoSlug);
        cfg.Bitbucket.Username = GetEnvOrDefault("BITBUCKET_USERNAME", cfg.Bitbucket.Username);
        cfg.Bitbucket.ApiToken = GetEnvOrDefault("BITBUCKET_API_TOKEN", cfg.Bitbucket.ApiToken);
        cfg.Tabby.Endpoint = GetEnvOrDefault("TABBY_ENDPOINT", cfg.Tabby.Endpoint);
        cfg.Tabby.ApiKey = GetEnvOrDefault("TABBY_API_KEY", cfg.Tabby.ApiKey);
        var tabbyTimeout = GetEnvOrDefault("TABBY_TIMEOUT_SECONDS", cfg.Tabby.TimeoutSeconds.ToString());
        if (int.TryParse(tabbyTimeout, out var tto)) cfg.Tabby.TimeoutSeconds = tto;
        var tempRoot = GetEnvOrDefault("TOOL_TEMP_ROOT", cfg.Tool.TempRoot);
        if (!string.IsNullOrWhiteSpace(tempRoot)) cfg.Tool.TempRoot = tempRoot;
        var maxDiffChars = GetEnvOrDefault("TOOL_MAX_DIFF_CHARS", cfg.Tool.MaxDiffChars.ToString());
        if (int.TryParse(maxDiffChars, out var m)) cfg.Tool.MaxDiffChars = m;
        var dryRun = GetEnvOrDefault("TOOL_DRY_RUN", cfg.Tool.DryRun.ToString());
        if (bool.TryParse(dryRun, out var d)) cfg.Tool.DryRun = d;
        var publishMode = GetEnvOrDefault("TOOL_PUBLISH_MODE", cfg.Tool.PublishMode.ToString());
        if (Enum.TryParse<PublishMode>(publishMode, true, out var pm)) cfg.Tool.PublishMode = pm;
        var extraFolder = GetEnvOrDefault("TOOL_EXTRA_PROMPT_FOLDER", cfg.Tool.ExtraPromptFolder);
        if (!string.IsNullOrWhiteSpace(extraFolder)) cfg.Tool.ExtraPromptFolder = extraFolder;
        var extraRecursive = GetEnvOrDefault("TOOL_EXTRA_PROMPT_RECURSIVE", cfg.Tool.ExtraPromptRecursive.ToString());
        if (bool.TryParse(extraRecursive, out var er)) cfg.Tool.ExtraPromptRecursive = er;
        var maxExtraChars = GetEnvOrDefault("TOOL_MAX_EXTRA_PROMPT_CHARS", cfg.Tool.MaxExtraPromptChars.ToString());
        if (int.TryParse(maxExtraChars, out var mec)) cfg.Tool.MaxExtraPromptChars = mec;
        var maxExtraFileChars = GetEnvOrDefault("TOOL_MAX_EXTRA_PROMPT_FILE_CHARS", cfg.Tool.MaxExtraPromptFileChars.ToString());
        if (int.TryParse(maxExtraFileChars, out var mefc)) cfg.Tool.MaxExtraPromptFileChars = mefc;
        var systemFolder = GetEnvOrDefault("TOOL_SYSTEM_PROMPT_FOLDER", cfg.Tool.SystemPromptFolder);
        if (!string.IsNullOrWhiteSpace(systemFolder)) cfg.Tool.SystemPromptFolder = systemFolder;
        var systemRecursive = GetEnvOrDefault("TOOL_SYSTEM_PROMPT_RECURSIVE", cfg.Tool.SystemPromptRecursive.ToString());
        if (bool.TryParse(systemRecursive, out var sr)) cfg.Tool.SystemPromptRecursive = sr;

        var input = InputArgs.Parse(args, cfg.Bitbucket.Workspace!, cfg.Bitbucket.RepoSlug!);
        return (cfg, input);
    }

    private static string GetEnvOrDefault(string name, string? fallback)
        => Environment.GetEnvironmentVariable(name) ?? fallback ?? string.Empty;

    private static HttpClient CreateBitbucketHttp(BitbucketConfig bb)
    {
        if (string.IsNullOrWhiteSpace(bb.ApiToken))
            throw new InvalidOperationException("BITBUCKET_API_TOKEN is required. Set it in appsettings.json or as an environment variable.");
        if (string.IsNullOrWhiteSpace(bb.Username))
            throw new InvalidOperationException("BITBUCKET_USERNAME (your Bitbucket username) is required for API token Basic auth.");
        var http = new HttpClient();
        var bytes = Encoding.ASCII.GetBytes($"{bb.Username}:{bb.ApiToken}");
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(bytes));
        http.DefaultRequestHeaders.UserAgent.ParseAdd("ASAPCodeReview/1.0");
        return http;
    }

    private static async Task<string> SendBitbucketAsync(BitbucketConfig bb, HttpMethod method, string url, object? payload = null, string? errorContext = null)
    {
        using var http = CreateBitbucketHttp(bb);
        using var req = new HttpRequestMessage(method, url);
        if (payload != null)
        {
            var json = JsonSerializer.Serialize(payload);
            req.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        var resp = await http.SendAsync(req);
        var body = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
        {
            var ctx = string.IsNullOrWhiteSpace(errorContext) ? "Bitbucket request failed" : errorContext!;
            throw new HttpRequestException($"{ctx}. Status: {(int)resp.StatusCode} {resp.ReasonPhrase}. Body: {Truncate(body, 2000)}");
        }
        return body;
    }

    private static async Task<dynamic> GetPullRequestAsync(BitbucketConfig bb, InputArgs input)
    {
        var url = $"{bb.BaseUrl}/repositories/{Uri.EscapeDataString(input.Workspace)}/{Uri.EscapeDataString(input.RepoSlug)}/pullrequests/{input.PrId}";
        var body = await SendBitbucketAsync(bb, HttpMethod.Get, url, null, "Bitbucket PR fetch failed");
        return JsonSerializer.Deserialize<JsonElement>(body).ToDynamic();
    }

    private static async Task PostPrCommentAsync(BitbucketConfig bb, InputArgs input, string content)
    {
        var url = $"{bb.BaseUrl}/repositories/{Uri.EscapeDataString(input.Workspace)}/{Uri.EscapeDataString(input.RepoSlug)}/pullrequests/{input.PrId}/comments";
        var payload = new { content = new { raw = content } };
        _ = await SendBitbucketAsync(bb, HttpMethod.Post, url, payload, "Bitbucket comment post failed");
    }

    private static async Task StartPrReviewAsync(BitbucketConfig bb, InputArgs input, string content)
    {
        // Best-effort attempt to create a draft (unpublished) PR comment so it can be reviewed and published later in Bitbucket UI
        var url = $"{bb.BaseUrl}/repositories/{Uri.EscapeDataString(input.Workspace)}/{Uri.EscapeDataString(input.RepoSlug)}/pullrequests/{input.PrId}/comments";
        var payload = new { content = new { raw = content }, pending = true };
        try
        {
            _ = await SendBitbucketAsync(bb, HttpMethod.Post, url, payload, "Bitbucket draft review request failed");
        }
        catch (HttpRequestException ex)
        {
            // Add helpful tip while preserving the underlying HTTP details
            throw new HttpRequestException(ex.Message + ". Tip: Set Tool.PublishMode=Publish to post the comment immediately if drafts are not supported in your Bitbucket plan.", ex);
        }
    }

    private static string PrepareTempDir(string? tempRoot)
    {
        // Store temp files under the execution app directory by default
        var root = string.IsNullOrWhiteSpace(tempRoot) ? Path.Combine(AppContext.BaseDirectory, "temp") : tempRoot;
        if (!Directory.Exists(root)) Directory.CreateDirectory(root);
        return root;
    }

    private static string ExtractGitUsername(string username)
    {
        // Bitbucket API may accept email, but git basic auth typically requires the account username (not email).
        // If an email is provided, use the part before '@' as the git username.
        if (string.IsNullOrWhiteSpace(username)) return username;
        var at = username.IndexOf('@');
        return at > 0 ? username[..at] : username;
    }

    private static async Task CloneAndCheckoutAsync(string cloneUrl, string repoDir, BitbucketConfig bb, string? commitSha, string? branchName)
    {
        if (string.IsNullOrWhiteSpace(bb.ApiToken))
            throw new InvalidOperationException("BITBUCKET_API_TOKEN is required for git operations.");
        if (string.IsNullOrWhiteSpace(bb.Username))
            throw new InvalidOperationException("BITBUCKET_USERNAME is required for git operations with API token.");

        var gitUsername = ExtractGitUsername(bb.Username!);
        var authUrl = InjectCredentialsIntoHttpsUrlRaw(cloneUrl, gitUsername, bb.ApiToken!);

        var gitDir = Path.Combine(repoDir, ".git");
        if (!Directory.Exists(gitDir))
        {
            Console.WriteLine($"[GIT] Cloning via CLI using user '{gitUsername}' into {repoDir}...");
            GitCliClone(authUrl, repoDir);
        }
        else
        {
            Console.WriteLine("[GIT] Reusing existing repository folder.");
            // Ensure origin has correct URL with credentials
            RunGit($"remote set-url origin \"{authUrl}\"", repoDir);
        }

        // Always fetch latest changes
        RunGit("fetch --all --prune", repoDir);

        // Checkout/reset logic
        if (!string.IsNullOrWhiteSpace(branchName))
        {
            Console.WriteLine($"[GIT] Checking out branch '{branchName}' and hard resetting to origin/{branchName}...");
            // Create/switch local branch to track remote, then hard reset
            // Use -B to create or reset the branch to the specified start point
            RunGit($"checkout -B {branchName} origin/{branchName}", repoDir);
            RunGit($"reset --hard origin/{branchName}", repoDir);
        }
        else if (!string.IsNullOrWhiteSpace(commitSha))
        {
            Console.WriteLine($"[GIT] Checking out commit {commitSha} (detached) and hard resetting...");
            RunGit($"checkout --detach {commitSha}", repoDir);
            RunGit($"reset --hard {commitSha}", repoDir);
        }

        await Task.CompletedTask;
    }

    private static string InjectCredentialsIntoHttpsUrlRaw(string baseUrl, string username, string password)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
            throw new ArgumentException("Invalid clone URL: " + baseUrl);
        if (!string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Only HTTPS clone URLs are supported for embedded credentials.");
        // Compose raw without URL-encoding to match Atlassian CLI examples
        var sb = new StringBuilder();
        sb.Append("https://");
        sb.Append(username);
        sb.Append(":");
        sb.Append(password);
        sb.Append("@");
        sb.Append(uri.Host);
        sb.Append(uri.PathAndQuery);
        return sb.ToString();
    }

    private static void GitCliClone(string authUrl, string repoDir)
    {
        if (Directory.Exists(repoDir))
        {
            try { Directory.Delete(repoDir, true); } catch { }
        }
        RunGit($"clone \"{authUrl}\" \"{repoDir}\"", workingDir: null);
    }

    private static void RunGit(string arguments, string? workingDir)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        if (!string.IsNullOrWhiteSpace(workingDir)) psi.WorkingDirectory = workingDir;
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start git process.");
        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        if (proc.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {arguments} failed with exit code {proc.ExitCode}. Error: {stderr}\nOutput: {stdout}");
        }
    }

    private static string ComputeLocalDiff(string repoDir, string? baseCommitSha, string? headCommitSha)
    {
        if (string.IsNullOrWhiteSpace(baseCommitSha) || string.IsNullOrWhiteSpace(headCommitSha))
            throw new ArgumentException("Missing base or head commit SHA for local diff computation.");

        using var repo = new Repository(repoDir);
        var baseCommit = repo.Lookup<Commit>(baseCommitSha) ?? throw new InvalidOperationException($"Base commit not found locally: {baseCommitSha}");
        var headCommit = repo.Lookup<Commit>(headCommitSha) ?? throw new InvalidOperationException($"Head commit not found locally: {headCommitSha}");

        var patch = repo.Diff.Compare<Patch>(baseCommit.Tree, headCommit.Tree);
        return patch.Content;
    }

    private static string BuildTabbyPrompt(dynamic pr, string diff)
    {
        var title = pr.title?.ToString() ?? "(no title)";
        var description = pr.description?.ToString() ?? "(no description)";
        var author = pr.author?.display_name?.ToString() ?? "(unknown)";
        var sb = new StringBuilder();

        // Compact instructions for a Bitbucket-friendly Markdown review
        sb.AppendLine("You are a senior software engineer reviewing a Bitbucket Pull Request.");
        sb.AppendLine("Goal: provide concise, actionable feedback focused on correctness, security, performance, tests, readability, and maintainability.");
        sb.AppendLine();
        sb.AppendLine("Output rules (very important):");
        sb.AppendLine("- Use compact Bitbucket Markdown language: short bullet points, avoid large headings, use bold labels, use code fences only when needed.");
        sb.AppendLine("- Prefer small code snippets with language fences (```csharp, ```diff, ```json) when suggesting changes.");
        sb.AppendLine("- Keep the review under ~600 words. No greetings or repetition.");
        sb.AppendLine();

        // Context (compact)
        sb.AppendLine("PR context:");
        sb.AppendLine($"- Title: {title}");
        sb.AppendLine($"- Author: {author}");
        sb.AppendLine("- Description:");
        sb.AppendLine(description);
        sb.AppendLine();

        // Diff
        sb.AppendLine("Unified diff (may be truncated):");
        sb.AppendLine("```diff");
        sb.AppendLine(diff);
        sb.AppendLine("```");
        sb.AppendLine();

        return sb.ToString();
    }

    private static async Task<string> GetTabbyReviewAsync(TabbyConfig tabby, ToolConfig tool, dynamic pr, string diff)
    {
        var prompt = BuildTabbyPrompt(pr, diff);
        var extraUserContent = BuildExtraUserPromptFromFolder(tool);

        using var http = new HttpClient();
        // Increase timeout to avoid default 100s cancellation when Tabby is slow
        if (tabby.TimeoutSeconds > 0)
            http.Timeout = TimeSpan.FromSeconds(tabby.TimeoutSeconds);
        if (!string.IsNullOrWhiteSpace(tabby.ApiKey))
        {
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tabby.ApiKey);
        }

        // Build messages with optional persona system content and extra user content
        var extraSystemContent = BuildSystemPromptFromFolder(tool);
        var systemContent = string.IsNullOrWhiteSpace(extraSystemContent)
            ? "You are a code review assistant."
            : "You are a code review assistant.\n\n" + extraSystemContent;

        var messages = new List<object>
        {
            new { role = "system", content = systemContent },
            new { role = "user", content = prompt }
        };
        if (!string.IsNullOrWhiteSpace(extraUserContent))
        {
            messages.Add(new { role = "user", content = extraUserContent! });
        }

        var payload = new
        {
            model = "tabby",
            messages = messages.ToArray(),
            temperature = 0.2,
            max_tokens = 1000
        };

        using var resp = await http.PostAsJsonAsync(tabby.Endpoint, payload);
        // We intentionally do not throw immediately so we can surface useful error details
        var body = await resp.Content.ReadAsStringAsync();
        var contentType = resp.Content.Headers.ContentType?.MediaType ?? string.Empty;

        // If response is not success, try to extract helpful message
        if (!resp.IsSuccessStatusCode)
        {
            try
            {
                var errEl = JsonSerializer.Deserialize<JsonElement>(body);
                if (errEl.ValueKind == JsonValueKind.Object && errEl.TryGetProperty("error", out var errObj))
                {
                    var msg = errObj.TryGetProperty("message", out var m) ? m.GetString() : errObj.ToString();
                    throw new HttpRequestException($"Tabby request failed: {(int)resp.StatusCode} {resp.ReasonPhrase}. Error: {msg}");
                }
            }
            catch { /* fall back to raw body */ }
            throw new HttpRequestException($"Tabby request failed: {(int)resp.StatusCode} {resp.ReasonPhrase}. Body: {Truncate(body, 2000)}");
        }

        // First, handle Server-Sent Events (text/event-stream) by assembling chunks
        var trimmed = body.TrimStart();
        var isSse = contentType.Contains("event-stream", StringComparison.OrdinalIgnoreCase) || trimmed.StartsWith("data:", StringComparison.OrdinalIgnoreCase);
        if (isSse)
        {
            var assembled = ParseOpenAIEventStream(body);
            if (!string.IsNullOrWhiteSpace(assembled)) return assembled;
            // If we couldn't assemble anything, return the raw body for debugging
            return string.IsNullOrWhiteSpace(body) ? "(Tabby returned empty response)" : body;
        }

        // Fallback to raw body so users can see what's wrong
        return string.IsNullOrWhiteSpace(body) ? "(Tabby returned empty response)" : body;
    }

    private static string ParseOpenAIEventStream(string sseBody)
    {
        var sb = new StringBuilder();
        using (var sr = new System.IO.StringReader(sseBody))
        {
            string? line;
            while ((line = sr.ReadLine()) != null)
            {
                // Ignore comments/keepalive lines starting with ':'
                if (line.StartsWith(":")) continue;
                if (!line.StartsWith("data:")) continue;
                var data = line.Substring(5).TrimStart();
                if (string.Equals(data, "[DONE]", StringComparison.OrdinalIgnoreCase)) break;
                if (string.IsNullOrWhiteSpace(data)) continue;
                try
                {
                    var chunk = JsonSerializer.Deserialize<JsonElement>(data);
                    if (chunk.ValueKind == JsonValueKind.Object && chunk.TryGetProperty("choices", out var ch) && ch.ValueKind == JsonValueKind.Array && ch.GetArrayLength() > 0)
                    {
                        var first = ch[0];
                        if (first.ValueKind == JsonValueKind.Object)
                        {
                            // Standard streaming delta
                            if (first.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.Object)
                            {
                                if (delta.TryGetProperty("content", out var dc) && dc.ValueKind == JsonValueKind.String)
                                {
                                    var piece = dc.GetString();
                                    if (!string.IsNullOrEmpty(piece)) sb.Append(piece);
                                }
                            }
                            // Some providers may stream `text` or `message.content`
                            else if (first.TryGetProperty("text", out var textEl) && textEl.ValueKind == JsonValueKind.String)
                            {
                                var piece = textEl.GetString();
                                if (!string.IsNullOrEmpty(piece)) sb.Append(piece);
                            }
                            else if (first.TryGetProperty("message", out var msgObj) && msgObj.ValueKind == JsonValueKind.Object && msgObj.TryGetProperty("content", out var mc) && mc.ValueKind == JsonValueKind.String)
                            {
                                var piece = mc.GetString();
                                if (!string.IsNullOrEmpty(piece)) sb.Append(piece);
                            }
                        }
                    }
                }
                catch
                {
                    // Ignore malformed chunk lines
                }
            }
        }
        return sb.ToString();
    }

    private static bool IsNoIssuesSignal(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return true;
        var t = text.Trim();
        // Strip common fence/formatting wrappers
        t = t.Trim('`', '#', '*', '-', ' ', '\t', '\r', '\n');
        var lower = t.ToLowerInvariant();
        if (lower is "no_issues" or "no issues" or "ok" or "lgtm" or "looks good" or "approved" or "all good") return true;
        if (lower.StartsWith("lgtm")) return true;
        if (lower.StartsWith("ok")) return true;
        if (lower.Contains("no issues found") || lower.Contains("nothing to comment") || lower.Contains("nothing to review")) return true;
        return false;
    }

    private static string Truncate(string input, int max)
    {
        if (string.IsNullOrEmpty(input)) return input;
        if (input.Length <= max) return input;
        return input[..max] + "\n... [diff truncated]";
    }

    // Generic aggregator used by both system and user extra prompts
    private static string? BuildAggregatedFromFolder(string? folder, bool recursive, int maxTotalChars, int maxFileChars, string headerPrefix)
    {
        if (string.IsNullOrWhiteSpace(folder)) return null;

        var resolved = ResolveFolderPath(folder);
        if (string.IsNullOrWhiteSpace(resolved) || !Directory.Exists(resolved))
        {
            return null;
        }

        var searchOption = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(resolved, "*", searchOption)
                              .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                              .ToArray();
        }
        catch
        {
            return null;
        }

        if (!files.Any()) return null;

        var sb = new StringBuilder();
        sb.AppendLine($"{headerPrefix}{resolved}");
        var skipExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".exe", ".dll", ".pdb", ".zip", ".tar", ".gz", ".7z", ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".mp3", ".mp4", ".avi"
        };

        foreach (var file in files)
        {
            try
            {
                var ext = Path.GetExtension(file);
                if (skipExts.Contains(ext)) continue;

                string content;
                try
                {
                    content = File.ReadAllText(file);
                }
                catch
                {
                    continue; // skip unreadable files
                }

                if (string.IsNullOrWhiteSpace(content)) continue;

                if (content.Length > maxFileChars)
                {
                    content = content[..maxFileChars] + "\n... [file truncated]";
                }

                var rel = GetRelativePathSafe(resolved, file);
                sb.AppendLine($"--- file: {rel} ---");
                var fence = ExtToFence(ext);
                if (!string.IsNullOrEmpty(fence)) sb.AppendLine($"```{fence}");
                sb.AppendLine(content);
                if (!string.IsNullOrEmpty(fence)) sb.AppendLine("```");
                sb.AppendLine();

                if (sb.Length >= maxTotalChars)
                {
                    sb.AppendLine("... [additional context truncated]");
                    break;
                }
            }
            catch
            {
                // ignore single file issues
            }
        }

        var result = sb.ToString();
        if (string.IsNullOrWhiteSpace(result)) return null;
        return result.Length > maxTotalChars
            ? result[..maxTotalChars]
            : result;
    }

    private static string? BuildExtraUserPromptFromFolder(ToolConfig tool)
        => BuildAggregatedFromFolder(tool.ExtraPromptFolder, tool.ExtraPromptRecursive, tool.MaxExtraPromptChars, tool.MaxExtraPromptFileChars, "Additional context files from: ");

    private static string? BuildSystemPromptFromFolder(ToolConfig tool)
        => BuildAggregatedFromFolder(tool.SystemPromptFolder, tool.SystemPromptRecursive, tool.MaxExtraPromptChars, tool.MaxExtraPromptFileChars, "Persona system instructions from: ");

    private static string? ResolveFolderPath(string folder)
    {
        try
        {
            if (Path.IsPathRooted(folder))
                return folder;

            var candidates = new[]
            {
                Path.Combine(Environment.CurrentDirectory, folder),
                Path.Combine(AppContext.BaseDirectory, folder),
                Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", folder))
            };
            foreach (var c in candidates)
            {
                if (Directory.Exists(c)) return c;
            }
        }
        catch
        {
            // ignore
        }
        return null;
    }

    private static string GetRelativePathSafe(string baseDir, string file)
    {
        try
        {
            var rel = Path.GetRelativePath(baseDir, file);
            if (!string.IsNullOrWhiteSpace(rel) && !rel.StartsWith("..")) return rel; // within
        }
        catch { }
        return Path.GetFileName(file);
    }

    private static string ExtToFence(string? ext)
    {
        ext = (ext ?? string.Empty).TrimStart('.').ToLowerInvariant();
        return ext switch
        {
            "cs" => "csharp",
            "json" => "json",
            "xml" => "xml",
            "yml" => "yaml",
            "yaml" => "yaml",
            "md" => "markdown",
            "txt" => string.Empty,
            _ => string.Empty
        };
    }
}

#region Config and helpers

public class AppConfig
{
    public BitbucketConfig Bitbucket { get; set; } = new();
    public TabbyConfig Tabby { get; set; } = new();
    public ToolConfig Tool { get; set; } = new();

    public static AppConfig Load()
    {
        // Build configuration from appsettings.json in the app base directory (optional, reloadable)
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
            .AddJsonFile("appsettings.local.json", optional: true, reloadOnChange: true)
            .Build();

        // Start with defaults and bind any matching values from configuration
        var cfg = new AppConfig();
        configuration.Bind(cfg);
        return cfg;
    }
}

public class BitbucketConfig
{
    public string BaseUrl { get; set; } = "https://api.bitbucket.org/2.0";
    public string RepoBaseCloneUrl { get; set; } = "https://bitbucket.org";
    public string? Workspace { get; set; }
    public string? RepoSlug { get; set; }
    public string? Username { get; set; }
    public string? ApiToken { get; set; }
}

public class TabbyConfig
{
    public string Endpoint { get; set; } = "http://localhost:8080/v1/chat/completions";
    public string? ApiKey { get; set; }
    public int TimeoutSeconds { get; set; } = 600; // default to 10 minutes to avoid 100s HttpClient default timeout
}

public enum PublishMode
{
    Publish,
    StartReview
}

public class ToolConfig
{
    public string? TempRoot { get; set; }
    public int MaxDiffChars { get; set; } = 45000;
    public bool DryRun { get; set; } = false;
    public PublishMode PublishMode { get; set; } = PublishMode.Publish;

    // Additional prompt context configuration
    // If set, the application will read text files from this folder and include them
    // as an additional 'user' message in the Tabby request.
    public string? ExtraPromptFolder { get; set; }
    public bool ExtraPromptRecursive { get; set; } = false;
    public int MaxExtraPromptChars { get; set; } = 20000;
    public int MaxExtraPromptFileChars { get; set; } = 8000;

    // System persona (system role) configuration
    // If set, the application will read text files from this folder and include them
    // in the 'system' message sent to the LLM.
    public string? SystemPromptFolder { get; set; } = "persona";
    public bool SystemPromptRecursive { get; set; } = false;
}

public record InputArgs(string Workspace, string RepoSlug, int PrId)
{
    public static InputArgs Parse(string[] args, string defaultWorkspace, string defaultRepoSlug)
    {
        string? ws = null, slug = null, prUrl = null; int? pr = null;

        // Accept a single positional argument that looks like a Bitbucket PR URL
        if (args.Length == 1 && LooksLikeUrl(args[0]))
        {
            prUrl = args[0];
        }

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--workspace": ws = args.ElementAtOrDefault(++i); break;
                case "--repo": slug = args.ElementAtOrDefault(++i); break;
                case "--pr":
                    if (int.TryParse(args.ElementAtOrDefault(++i), out var p)) pr = p;
                    break;
                case "--prUrl":
                case "--pr-url":
                    prUrl = args.ElementAtOrDefault(++i);
                    break;
            }
        }

        // If PR URL is provided, parse it to extract workspace, repo, and id
        if (!string.IsNullOrWhiteSpace(prUrl) && TryParsePrUrl(prUrl, out var uWs, out var uSlug, out var uId))
        {
            ws ??= uWs;
            slug ??= uSlug;
            pr ??= uId;
        }

        ws ??= defaultWorkspace;
        slug ??= defaultRepoSlug;

        if (string.IsNullOrWhiteSpace(ws) || string.IsNullOrWhiteSpace(slug) || pr is null)
            throw new ArgumentException(
                "Usage:\n  ASAPCodeReview --workspace <workspace> --repo <repo-slug> --pr <id>\n  ASAPCodeReview --prUrl <bitbucket_pr_url>\n  ASAPCodeReview <bitbucket_pr_url>\n\nQuick check (no network):\n  ASAPCodeReview --test-parse --prUrl <bitbucket_pr_url>\n  ASAPCodeReview --test-parse <bitbucket_pr_url>\n\nAuth: Set BITBUCKET_API_TOKEN (API token) and optionally BITBUCKET_USERNAME (email) for Basic email+token fallback. Configure also in appsettings.json.\nNotes: You can also set BITBUCKET_WORKSPACE and BITBUCKET_REPO_SLUG env vars and provide --pr.");

        return new InputArgs(ws, slug, pr.Value);
    }

    private static bool LooksLikeUrl(string s)
        => s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || s.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    public static bool TryParsePrUrl(string url, out string workspace, out string repoSlug, out int prId)
    {
        workspace = string.Empty; repoSlug = string.Empty; prId = 0;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        // Example: https://bitbucket.org/{workspace}/{repo}/pull-requests/{id}/diff
        var segs = uri.Segments.Select(s => s.Trim('/')).Where(s => !string.IsNullOrEmpty(s)).ToArray();
        // We expect at least 4 segments: ws, repo, "pull-requests", id, (optional extras)
        if (segs.Length < 4) return false;
        // Find index of "pull-requests" to be robust if extra segments appear
        var prIndex = Array.FindIndex(segs, s => string.Equals(s, "pull-requests", StringComparison.OrdinalIgnoreCase));
        if (prIndex < 2 || prIndex + 1 >= segs.Length) return false;
        var idPart = segs[prIndex + 1];
        if (!int.TryParse(idPart, out var id)) return false;
        // workspace is segs[0], repo is segs[1] in standard layout
        workspace = segs[0];
        repoSlug = segs[1];
        prId = id;
        return true;
    }
}

static class JsonElementExt
{
    public static dynamic ToDynamic(this JsonElement element)
    {
        using var doc = JsonDocument.Parse(element.GetRawText());
        return new DynamicJson(doc.RootElement.Clone());
    }
}

class DynamicJson : System.Dynamic.DynamicObject
{
    private readonly JsonElement _element;
    public DynamicJson(JsonElement element) => _element = element;

    public override bool TryGetMember(System.Dynamic.GetMemberBinder binder, out object? result)
    {
        if (_element.ValueKind == JsonValueKind.Object && _element.TryGetProperty(binder.Name, out var prop))
        {
            result = Wrap(prop);
            return true;
        }
        result = null;
        return false;
    }

    public override bool TryGetIndex(System.Dynamic.GetIndexBinder binder, object[] indexes, out object? result)
    {
        if (indexes.Length == 1 && indexes[0] is int i && _element.ValueKind == JsonValueKind.Array)
        {
            result = Wrap(_element[i]);
            return true;
        }
        result = null;
        return false;
    }

    public override string? ToString() => _element.ToString();

    private static object? Wrap(JsonElement el)
        => el.ValueKind switch
        {
            JsonValueKind.Object or JsonValueKind.Array => new DynamicJson(el),
            JsonValueKind.String => el.GetString(),
            JsonValueKind.Number => el.TryGetInt64(out var l) ? l : el.TryGetDouble(out var d) ? d : el.GetRawText(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => el.GetRawText()
        };
}

#endregion