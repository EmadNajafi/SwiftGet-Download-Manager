using System.Net;
using System.Text.RegularExpressions;

namespace SwiftGet.Core;

/// <summary>
/// Explores a website from a start page and collects links to files of the chosen types.
/// Follows only links on the same site (optional), up to a depth and a page limit.
/// </summary>
public sealed partial class SiteGrabber
{
    [GeneratedRegex("""(?:href|src|data-src)\s*=\s*["']([^"'#>]+)["']""", RegexOptions.IgnoreCase)]
    private static partial Regex LinkRegex();

    public required Uri Start { get; init; }
    public int Depth { get; init; } = 2;
    public int MaxPages { get; init; } = 200;
    public bool SameSite { get; init; } = true;
    public required HashSet<string> Extensions { get; init; }

    public int PagesVisited { get; private set; }
    public event Action<int, int>? Progress;   // pages visited, files found

    public async Task<List<string>> RunAsync(CancellationToken ct)
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Normalize(Start) };
        var frontier = new Queue<(Uri Uri, int Depth)>();
        frontier.Enqueue((Start, 0));

        while (frontier.Count > 0 && PagesVisited < MaxPages)
        {
            ct.ThrowIfCancellationRequested();
            var (page, depth) = frontier.Dequeue();
            string html;
            Uri baseUri;
            try
            {
                var (text, final, type) = await Http.GetTextAsync(page.ToString(), null, null, null, ct);
                if (type != null && !type.Contains("html", StringComparison.OrdinalIgnoreCase)) continue;
                (html, baseUri) = (text, final);
            }
            catch (OperationCanceledException) { throw; }
            catch { continue; }
            PagesVisited++;

            foreach (Match m in LinkRegex().Matches(html))
            {
                var raw = WebUtility.HtmlDecode(m.Groups[1].Value.Trim());
                if (raw.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase) || raw.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)) continue;
                if (!Uri.TryCreate(baseUri, raw, out var link) || (link.Scheme != "http" && link.Scheme != "https")) continue;

                var ext = Path.GetExtension(link.AbsolutePath).TrimStart('.').ToLowerInvariant();
                if (Extensions.Contains(ext))
                {
                    files.Add(link.ToString());
                    continue;
                }

                bool isPage = ext is "" or "html" or "htm" or "php" or "asp" or "aspx" or "jsp";
                if (!isPage || depth + 1 > Depth) continue;
                if (SameSite && !string.Equals(link.Host, Start.Host, StringComparison.OrdinalIgnoreCase)) continue;
                if (seen.Add(Normalize(link))) frontier.Enqueue((link, depth + 1));
            }
            Progress?.Invoke(PagesVisited, files.Count);
        }
        return files.ToList();
    }

    static string Normalize(Uri u) => u.GetLeftPart(UriPartial.Query).TrimEnd('/');
}
