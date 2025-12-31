using System.Net.Http.Headers;

namespace ClientInsightAPI.Services.Llm;

public sealed class ApiKeyHandler : DelegatingHandler
{
    private readonly string _key;

    public ApiKeyHandler(string key) => _key = key;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_key))
            throw new InvalidOperationException("OpenAIKey is missing from configuration.");

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _key);
        return base.SendAsync(request, cancellationToken);
    }
}
