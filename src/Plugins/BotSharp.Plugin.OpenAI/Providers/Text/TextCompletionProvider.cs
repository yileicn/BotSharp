using BotSharp.Abstraction.Hooks;
using System.Net.Http;
using System.Net.Mime;

namespace BotSharp.Plugin.OpenAI.Providers.Text;

public class TextCompletionProvider : ITextCompletion
{
    private readonly IServiceProvider _services;
    private readonly ILogger<TextCompletionProvider> _logger;
    private readonly OpenAiSettings _settings;
    protected string _model;

    public virtual string Provider => "openai";
    public string Model => _model;

    public TextCompletionProvider(
        OpenAiSettings settings,
        ILogger<TextCompletionProvider> logger,
        IServiceProvider services)
    {
        _services = services;
        _logger = logger;
        _settings = settings;
    }

    public async Task<string> GetCompletion(string text, string agentId, string messageId)
    {
        var contentHooks = _services.GetHooks<IContentGeneratingHook>(agentId);

        // Before chat completion hook
        var agent = new Agent()
        {
            Id = agentId,
        };
        var message = new RoleDialogModel(AgentRole.User, text)
        {
            CurrentAgentId = agentId,
            MessageId = messageId
        };

        Task.WaitAll(contentHooks.Select(hook =>
            hook.BeforeGenerating(agent,
                new List<RoleDialogModel>
                {
                    message
                })).ToArray());

        var state = _services.GetRequiredService<IConversationStateService>();
        var temperature = float.Parse(state.GetState("temperature", "0.0"));

        var settingsService = _services.GetRequiredService<ILlmProviderService>();
        var modelSetting = settingsService.GetSetting(Provider, _model);
        var apiUrl = BuildApiUrl(modelSetting);
        var apiKey = modelSetting.ApiKey;
        var response = await GetTextCompletion(apiUrl, apiKey, text, temperature);

        // OpenAI
        var completion = "";
        foreach (var t in response.Choices)
        {
            completion += t?.Text ?? string.Empty;
        };

        // After chat completion hook
        var responseMessage = new RoleDialogModel(AgentRole.Assistant, completion)
        {
            CurrentAgentId = agentId,
            MessageId = messageId
        };
        Task.WaitAll(contentHooks.Select(hook =>
            hook.AfterGenerated(responseMessage, new TokenStatsModel
            {
                Prompt = text,
                Provider = Provider,
                Model = _model,
                TextInputTokens = response.Usage?.PromptTokens ?? 0,
                TextOutputTokens = response.Usage?.CompletionTokens ?? 0
            })).ToArray());

        return completion.Trim();
    }

    public void SetModelName(string model)
    {
        _model = model;
    }

    private async Task<TextCompletionResponse> GetTextCompletion(string apiUrl, string apiKey, string prompt, float temperature, int maxTokens = 256)
    {
        try
        {
            var http = _services.GetRequiredService<IHttpClientFactory>();
            using var httpClient = http.CreateClient();
            AddHeader(httpClient, apiKey);

            var request = new TextCompletionRequest
            {
                Model = _model,
                Prompt = prompt,
                MaxTokens = maxTokens,
                Temperature = temperature
            };
            var data = JsonSerializer.Serialize(request, BotSharpOptions.defaultJsonOptions);
            var httpRequest = new HttpRequestMessage
            {
                Method = HttpMethod.Post,
                RequestUri = new Uri(apiUrl),
                Content = new StringContent(data, Encoding.UTF8, MediaTypeNames.Application.Json)
            };

            var httpResponse = await httpClient.SendAsync(httpRequest);
            httpResponse.EnsureSuccessStatusCode();
            var responseStr = await httpResponse.Content.ReadAsStringAsync();
            var response = JsonSerializer.Deserialize<TextCompletionResponse>(responseStr, BotSharpOptions.defaultJsonOptions);
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error when {Provider}-{_model} generating text... {ex.Message}");
            throw;
        }
    }

    private string BuildApiUrl(LlmModelSetting modelSetting)
    {
        var endpoint = modelSetting.Endpoint.EndsWith("/") ?
            modelSetting.Endpoint.Substring(0, modelSetting.Endpoint.Length - 1) : modelSetting.Endpoint;
        return endpoint ?? string.Empty;
    }

    private void AddHeader(HttpClient httpClient, string apiKey)
    {
        httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");
    }
}
