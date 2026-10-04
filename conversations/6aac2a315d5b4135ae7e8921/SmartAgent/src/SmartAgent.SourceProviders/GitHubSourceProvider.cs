using System.Formats.Tar;
using System.Text.Json;
using SmartAgent.Domain;

namespace SmartAgent.SourceProviders;

/// <summary>Mode b: fetches the default branch tarball of a GitHub repository.</summary>
public sealed class GitHubSourceProvider(HttpClient http) : ISourceProvider
{
    public SourceType SourceType => SourceType.GitHub;

    public async Task<SourceSnapshot> FetchAsync(SourceRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Url))
            throw new ArgumentException("A GitHub repository URL is required.", nameof(request));

        var (owner, repo, branch) = Parse(request.Url);
        var url = branch is null
            ? $"https://codeload.github.com/{owner}/{repo}/tar.gz/HEAD"
            : $"https://codeload.github.com/{owner}/{repo}/tar.gz/{Uri.EscapeDataString(branch)}";

        using var httpReq = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrWhiteSpace(request.GitHubToken))
            httpReq.Headers.Add("Authorization", $"Bearer {request.GitHubToken}");

        using var resp = await http.SendAsync(httpReq, HttpCompletionOption.ResponseHeadersRead, request.CancellationToken);
        resp.EnsureSuccessStatusCode();

        await using var tarStream = await resp.Content.ReadAsStreamAsync(request.CancellationToken);
        var files = await ExtractTarGzAsync(tarStream, $"{owner}-{repo}", repo);
        if (files.Count == 0)
            throw new InvalidOperationException($"No analyzable files found in {owner}/{repo}.");

        var history = await TryFetchHistoryAsync(owner, repo, request, maxCommits: 30);

        return new SourceSnapshot
        {
            SourceType = SourceType.GitHub,
            SourceName = $"{owner}/{repo}",
            SourceDetail = $"branch {(branch ?? "HEAD")}, {files.Count} files analyzed"
                + (history.Count > 0 ? $", {history.Count} commits reviewed" : string.Empty),
            Files = files,
            History = history
        };
    }

    /// <summary>
    /// Best-effort commit history review: recent commits with their touched
    /// paths, so downstream planning can detect real change/enhancement
    /// patterns in the repository. Any failure degrades to empty history.
    /// </summary>
    private async Task<IReadOnlyList<SourceCommit>> TryFetchHistoryAsync(
        string owner, string repo, SourceRequest request, int maxCommits)
    {
        try
        {
            using var listReq = new HttpRequestMessage(HttpMethod.Get,
                $"https://api.github.com/repos/{owner}/{repo}/commits?per_page={maxCommits}");
            if (!string.IsNullOrWhiteSpace(request.GitHubToken))
                listReq.Headers.Add("Authorization", $"Bearer {request.GitHubToken}");
            listReq.Headers.Add("User-Agent", "SmartAgent-PM");
            using var listResp = await http.SendAsync(listReq, request.CancellationToken);
            listResp.EnsureSuccessStatusCode();
            using var listDoc = JsonDocument.Parse(await listResp.Content.ReadAsStringAsync(request.CancellationToken));
            var commits = new List<SourceCommit>();
            foreach (var entry in listDoc.RootElement.EnumerateArray())
            {
                var sha = entry.GetProperty("sha").GetString()!;
                var commit = entry.GetProperty("commit");
                var message = commit.GetProperty("message").GetString() ?? "";
                var date = commit.GetProperty("author").TryGetProperty("date", out var d) && d.GetString() is { } ds
                    ? DateTimeOffset.Parse(ds) : DateTimeOffset.UtcNow;
                commits.Add(new SourceCommit { Sha = sha, Message = message, Date = date });
            }

            foreach (var c in commits.Take(30))
            {
                using var detailReq = new HttpRequestMessage(HttpMethod.Get,
                    $"https://api.github.com/repos/{owner}/{repo}/commits/{c.Sha}");
                if (!string.IsNullOrWhiteSpace(request.GitHubToken))
                    detailReq.Headers.Add("Authorization", $"Bearer {request.GitHubToken}");
                detailReq.Headers.Add("User-Agent", "SmartAgent-PM");
                using var detailResp = await http.SendAsync(detailReq, request.CancellationToken);
                if (!detailResp.IsSuccessStatusCode) continue;
                using var detailDoc = JsonDocument.Parse(await detailResp.Content.ReadAsStringAsync(request.CancellationToken));
                if (!detailDoc.RootElement.TryGetProperty("files", out var filesEl)) continue;
                var paths = new List<string>();
                var patches = new List<SourcePatch>();
                foreach (var f in filesEl.EnumerateArray().Take(40))
                {
                    if (f.TryGetProperty("filename", out var fn) && fn.GetString() is { } fp)
                    {
                        paths.Add(fp);
                        // keep code diffs (added/removed lines only) for field-history analysis
                        if ((fp.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                                || fp.EndsWith(".cshtml", StringComparison.OrdinalIgnoreCase))
                            && f.TryGetProperty("patch", out var pe)
                            && pe.GetString() is { } patchText && patchText.Length > 0)
                        {
                            var lines = patchText.Split('\n')
                                .Where(l => l.StartsWith('+') || l.StartsWith('-')).ToList();
                            if (lines.Count > 0)
                                patches.Add(new SourcePatch
                                {
                                    Path = fp,
                                    Patch = string.Join("\n", lines.Take(400))[..Math.Min(2000, string.Join("\n", lines.Take(400)).Length)]
                                });
                        }
                    }
                }
                var idx = commits.FindIndex(x => x.Sha == c.Sha);
                commits[idx] = c with { TouchedPaths = paths, Patches = patches };
            }
            return commits;
        }
        catch
        {
            return Array.Empty<SourceCommit>();
        }
    }

    public static (string Owner, string Repo, string? Branch) Parse(string raw)
    {
        // Accepts: https://github.com/owner/repo, .../tree/branch, owner/repo, github.com/owner/repo
        var trimmed = raw.Trim().TrimEnd('/');
        string path;
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var u))
        {
            if (!u.Host.EndsWith("github.com", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException($"'{raw}' is not a GitHub repository URL.", nameof(raw));
            path = u.AbsolutePath;
        }
        else if (trimmed.StartsWith("github.com/", StringComparison.OrdinalIgnoreCase))
            path = "/" + trimmed["github.com/".Length..];
        else
            path = "/" + trimmed;

        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2)
            throw new ArgumentException($"'{raw}' is not a GitHub repository URL.", nameof(raw));

        var owner = parts[0];
        var repo = parts[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? parts[1][..^4] : parts[1];
        string? branch = null;
        if (parts.Length >= 4 && parts[2] is "tree" or "blob") branch = string.Join("/", parts[3..]);
        return (owner, repo, branch);
    }

    private static async Task<List<SourceFile>> ExtractTarGzAsync(Stream tarGz, string repoSlug, string repoName)
    {
        var files = new List<SourceFile>();
        long total = 0;

        await using var gzip = new System.IO.Compression.GZipStream(tarGz, System.IO.Compression.CompressionMode.Decompress);
        using var tar = new TarReader(gzip);

        while (await tar.GetNextEntryAsync(copyData: false) is { } entry)
        {
            if (entry.EntryType != TarEntryType.RegularFile || entry.DataStream is null) continue;

            var path = (entry.Name ?? string.Empty).Replace('\\', '/');
            if (SourceProviderCommon.IsSkippedPath(path)) continue;

            // Strip the GitHub root folder ("owner-repo-<sha>/" or "repo-<branch>/").
            var slash = path.IndexOf('/');
            if (slash > 0 && (path[..slash].StartsWith(repoSlug, StringComparison.OrdinalIgnoreCase)
                              || path[..slash].StartsWith(repoName + "-", StringComparison.OrdinalIgnoreCase)))
                path = path[(slash + 1)..];
            if (path.Length == 0) continue;

            if (files.Count >= SourceProviderCommon.MaxFiles) break;
            if (total + entry.Length > SourceProviderCommon.MaxTotalBytes) break;

            using var ms = new MemoryStream();
            await entry.DataStream.CopyToAsync(ms);
            var bytes = ms.ToArray();

            var file = SourceProviderCommon.TryRead(path, bytes, out _);
            if (file is null) continue;

            files.Add(file);
            total += bytes.Length;
        }
        return files;
    }
}
