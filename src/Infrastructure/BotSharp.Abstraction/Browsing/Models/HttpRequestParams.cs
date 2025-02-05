using System.Diagnostics;
using System.Net.Http;

namespace BotSharp.Abstraction.Browsing.Models;

[DebuggerStepThrough]
public class HttpRequestParams
{
    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    [JsonPropertyName("method")]
    public HttpMethod Method { get; set; }

    /// <summary>
    /// Http request payload
    /// </summary>
    [JsonPropertyName("payload")]
    public string? Payload { get; set; }

    public HttpRequestParams(string url,  HttpMethod method, string? payload = null)
    {
        Method = method;
        Url = url;
        Payload = payload;
    }
}
