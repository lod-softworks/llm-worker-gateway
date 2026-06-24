using Microsoft.Extensions.Caching.Memory;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Lod.LlmGateway.WorkerHost.Clients;

public abstract class StreamingChatClientBase(HttpClient httpClient, IMemoryCache memoryCache)
{
    protected HttpClient HttpClient { get; } = httpClient;

    protected IMemoryCache MemoryCache { get; } = memoryCache;

    protected static JsonContent CreateJsonContent(object body, JsonSerializerOptions serializerOptions) =>
        JsonContent.Create(body, body.GetType(), mediaType: null, options: serializerOptions);

    protected async Task<T> ReadJsonAsync<T>(
        string requestUri,
        object body,
        JsonSerializerOptions serializerOptions,
        string errorMessage,
        CancellationToken cancellationToken)
    {
        using HttpRequestMessage requestMessage = CreatePostRequestWithBody(
            new Uri(requestUri, UriKind.RelativeOrAbsolute),
            body,
            serializerOptions);
        using HttpResponseMessage response = await HttpClient.SendAsync(requestMessage, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            string responseText = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException(
                !string.IsNullOrWhiteSpace(responseText) ? responseText : errorMessage,
                null,
                response.StatusCode);
        }

        T? result = await response.Content.ReadFromJsonAsync<T>(serializerOptions, cancellationToken);

        return result ?? throw new InvalidOperationException(errorMessage);
    }

    static HttpRequestMessage CreatePostRequestWithBody(
        Uri requestUri,
        object body,
        JsonSerializerOptions serializerOptions)
    {
        HttpRequestMessage requestMessage = new(HttpMethod.Post, requestUri);
        if (body is JsonObject jObj)
        {
            string json = jObj.ToJsonString(serializerOptions);
            requestMessage.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }
        else
        {
            requestMessage.Content = JsonContent.Create(
                body,
                body.GetType(),
                options: serializerOptions);
        }

        return requestMessage;
    }

    protected async IAsyncEnumerable<string> ReadSseStreamAsync(
        HttpRequestMessage request,
        string errorMessage,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await HttpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            string responseText = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException(
                !string.IsNullOrWhiteSpace(responseText) ? responseText : errorMessage,
                null,
                response.StatusCode);
        }

        await using Stream responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(responseStream, Encoding.UTF8);

        string? line;
        while ((line = await reader.ReadLineAsync(cancellationToken)) is not null)
        {
            const string dataPrefix = "data: ";
            if (!line.StartsWith(dataPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            string payload = line[dataPrefix.Length..].Trim();
            if (payload == "[DONE]")
            {
                yield return "[DONE]";
                yield break;
            }

            if (payload.Length > 0)
            {
                yield return payload;
            }
        }
    }
}
