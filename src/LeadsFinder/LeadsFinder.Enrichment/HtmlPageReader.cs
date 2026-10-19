using System.Net;
using System.Text.RegularExpressions;
using HtmlAgilityPack;

namespace LeadsFinder.Enrichment;

public sealed class HtmlPageReader(HttpClient http)
{
    public async Task<EvidencePage?> ReadAsync(string url, string strategy, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https"
            || !await IsPublicHostAsync(uri.Host, cancellationToken))
            return null;

        HttpResponseMessage? response = null;
        for (var redirects = 0; redirects <= 3; redirects++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd("LeadsFinder/0.1 (+public business contact enrichment)");
            response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if ((int)response.StatusCode is not (>= 300 and < 400) || response.Headers.Location is null) break;
            var next = response.Headers.Location.IsAbsoluteUri ? response.Headers.Location : new Uri(uri, response.Headers.Location);
            response.Dispose();
            response = null;
            if (next.Scheme != "https" || !await IsPublicHostAsync(next.Host, cancellationToken)) return null;
            uri = next;
        }
        if (response is null) return null;
        using (response)
        {
        if (!response.IsSuccessStatusCode || response.Content.Headers.ContentType?.MediaType is not ("text/html" or "application/xhtml+xml"))
            return null;
        if (response.Content.Headers.ContentLength is > 1_000_000)
            return null;
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var limited = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            if (limited.Length + read > 1_000_000) return null;
            limited.Write(buffer, 0, read);
        }
        limited.Position = 0;
        using var reader = new StreamReader(limited);
        var document = new HtmlDocument();
        document.LoadHtml(await reader.ReadToEndAsync(cancellationToken));
        var title = WebUtility.HtmlDecode(document.DocumentNode.SelectSingleNode("//title")?.InnerText ?? "").Trim();
        foreach (var node in document.DocumentNode.SelectNodes("//script|//style|//noscript|//svg|//template|//nav|//footer|//header")?.ToArray() ?? Array.Empty<HtmlNode>())
            node.Remove();
        var body = document.DocumentNode.SelectSingleNode("//body") ?? document.DocumentNode;
        var text = WebUtility.HtmlDecode(body.InnerText);
        text = Regex.Replace(text, @"\s+", " ").Trim();
        if (text.Length < 30) return null;
        // Keep contact-bearing spans and an identity-bearing opening. This bounds model input.
        var excerpts = Regex.Matches(text, @".{0,100}(?:contact|phone|tel:|email|address|@|\+\d{2}|www\.).{0,180}", RegexOptions.IgnoreCase)
            .Select(x => x.Value).Distinct().Take(18);
        var compact = string.Join("\n", new[] { text[..Math.Min(600, text.Length)] }.Concat(excerpts));
        return new EvidencePage(uri.ToString(), title, compact[..Math.Min(4000, compact.Length)], strategy);
        }
    }

    private static async Task<bool> IsPublicHostAsync(string host, CancellationToken cancellationToken)
    {
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase))
            return false;
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host, cancellationToken);
            return addresses.Length > 0 && addresses.All(ip =>
            {
                if (IPAddress.IsLoopback(ip) || ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast)
                    return false;
                if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
                    return !(ip.GetAddressBytes()[0] is >= 0xfc and <= 0xfd);
                var b = ip.GetAddressBytes();
                return b[0] != 0 && b[0] != 10 && b[0] != 127 && b[0] < 224
                    && !(b[0] == 169 && b[1] == 254) && !(b[0] == 172 && b[1] is >= 16 and <= 31)
                    && !(b[0] == 192 && b[1] == 168) && !(b[0] == 100 && b[1] is >= 64 and <= 127);
            });
        }
        catch { return false; }
    }
}
