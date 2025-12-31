using System.Collections.Specialized;
using System.Web;

namespace ClientInsightAPI.Services.ArticleScan;

public static class UrlCanonicalizer
{
    // Add/remove keys as you discover noise in your data
    private static readonly HashSet<string> DropQueryKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        // Common tracking
        "utm_source","utm_medium","utm_campaign","utm_term","utm_content","utm_id",
        "gclid","dclid","fbclid","msclkid","yclid","_hsenc","_hsmi",
        "mc_cid","mc_eid","igshid","si","spm","cmpid","campaignid",
        "ref","ref_src","ref_url","source","src","rss","rssfeed",

        // Sometimes duplicates
        "mkt_tok","trk","trkCampaign","vero_conv","vero_id"
    };

    public static string Canonicalize(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return url;

        // Some sources return leading/trailing whitespace
        url = url.Trim();

        // If it’s not a valid absolute URL, return as-is (don’t break data)
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return url;

        var builder = new UriBuilder(uri);

        // Normalize host & scheme
        builder.Host = builder.Host.ToLowerInvariant();
        builder.Scheme = builder.Scheme.ToLowerInvariant();

        // Remove default ports
        if (builder.Scheme == "http" && builder.Port == 80 ||
            builder.Scheme == "https" && builder.Port == 443)
        {
            builder.Port = -1;
        }

        // Normalize path: remove trailing slash (but keep "/" if root)
        if (!string.IsNullOrEmpty(builder.Path) && builder.Path != "/")
        {
            builder.Path = builder.Path.TrimEnd('/');
            if (builder.Path.Length == 0) builder.Path = "/";
        }

        // Clean query string: drop tracking params, sort remaining keys/values
        if (!string.IsNullOrWhiteSpace(builder.Query))
        {
            NameValueCollection q = HttpUtility.ParseQueryString(builder.Query);

            // drop tracking params
            var keys = q.AllKeys.Where(k => !string.IsNullOrWhiteSpace(k)).ToList();
            foreach (var k in keys)
            {
                if (DropQueryKeys.Contains(k!))
                    q.Remove(k);
            }

            // Rebuild query deterministically (sorted)
            var remaining = q.AllKeys
                .Where(k => !string.IsNullOrWhiteSpace(k))
                .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (remaining.Count == 0)
            {
                builder.Query = string.Empty;
            }
            else
            {
                var pairs = new List<string>(remaining.Count);
                foreach (var key in remaining)
                {
                    var values = q.GetValues(key!) ?? Array.Empty<string>();
                    Array.Sort(values, StringComparer.OrdinalIgnoreCase);

                    foreach (var v in values)
                    {
                        // Keep blank values as key=
                        var encKey = Uri.EscapeDataString(key!);
                        var encVal = Uri.EscapeDataString(v ?? string.Empty);
                        pairs.Add($"{encKey}={encVal}");
                    }
                }

                builder.Query = string.Join("&", pairs);
            }
        }

        // Drop fragment (#...)
        builder.Fragment = string.Empty;

        return builder.Uri.ToString();
    }
}
