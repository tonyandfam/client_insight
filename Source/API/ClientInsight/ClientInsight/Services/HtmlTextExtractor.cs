using System.Net;
using System.Text.RegularExpressions;

namespace ClientInsightAPI.Services;

public static class HtmlTextExtractor
{
    private static readonly Regex RxScript = new(@"<script\b[^<]*(?:(?!</script>)<[^<]*)*</script>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex RxStyle = new(@"<style\b[^<]*(?:(?!</style>)<[^<]*)*</style>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex RxTags = new(@"<[^>]+>", RegexOptions.Singleline);
    private static readonly Regex RxSpace = new(@"\s+", RegexOptions.Singleline);

    public static string Extract(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return "";

        html = RxScript.Replace(html, " ");
        html = RxStyle.Replace(html, " ");

        // crude but effective
        var text = RxTags.Replace(html, " ");
        text = WebUtility.HtmlDecode(text);
        text = RxSpace.Replace(text, " ").Trim();

        return text;
    }
}
