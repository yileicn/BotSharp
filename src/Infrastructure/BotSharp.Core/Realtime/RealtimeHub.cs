using BotSharp.Abstraction.Realtime;
using System.Net.WebSockets;
using BotSharp.Abstraction.Realtime.Models;
using BotSharp.Abstraction.MLTasks;
using BotSharp.Abstraction.Conversations.Enums;
using BotSharp.Abstraction.Routing.Models;
using NetTopologySuite.Index.HPRtree;
using BotSharp.Abstraction.Agents.Models;
using Microsoft.Identity.Client.Extensions.Msal;
using Microsoft.AspNetCore.Cors.Infrastructure;

namespace BotSharp.Core.Realtime;

public class RealtimeHub : IRealtimeHub
{
    private readonly IServiceProvider _services;
    private readonly ILogger _logger;

    public RealtimeHub(IServiceProvider services, ILogger<RealtimeHub> logger)
    {
        _services = services;
        _logger = logger;
    }

    public async Task Listen(WebSocket userWebSocket, 
        Func<string, RealtimeHubConnection> onUserMessageReceived)
    {
        var buffer = new byte[1024 * 16];
        WebSocketReceiveResult result;

        var completer = _services.GetServices<IRealTimeCompletion>().First(x => x.Provider == "openai");

        do
        {
            result = await userWebSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
            string receivedText = Encoding.UTF8.GetString(buffer, 0, result.Count);

            if (string.IsNullOrEmpty(receivedText))
            {
                continue;
            }

            var conn = onUserMessageReceived(receivedText);

            if (conn.Event == "user_connected")
            {
                await ConnectToModel(completer, userWebSocket, conn);
            }
            else if (conn.Event == "user_data_received")
            {
                await completer.AppenAudioBuffer(conn.Data);
            }
            else if (conn.Event == "user_dtmf_received")
            {
                await HandleUserDtmfReceived(completer, conn);
            }
            else if (conn.Event == "user_disconnected")
            {
                await completer.Disconnect();
                await HandleUserDisconnected(conn);
            }
        } while (!result.CloseStatus.HasValue);

        await userWebSocket.CloseAsync(result.CloseStatus.Value, result.CloseStatusDescription, CancellationToken.None);
    }

    private async Task ConnectToModel(IRealTimeCompletion completer, WebSocket userWebSocket, RealtimeHubConnection conn)
    {
        var hookProvider = _services.GetRequiredService<ConversationHookProvider>();
        var convService = _services.GetRequiredService<IConversationService>();
        convService.SetConversationId(conn.ConversationId, []);
        var conversation = await convService.GetConversation(conn.ConversationId);

        var agentService = _services.GetRequiredService<IAgentService>();
        var agent = await agentService.LoadAgent(conversation.AgentId);
        conn.CurrentAgentId = agent.Id;

        // Set model
        var model = agent.LlmConfig.Model;
        if (!model.Contains("-realtime-"))
        {
            var llmProviderService = _services.GetRequiredService<ILlmProviderService>();
            model = llmProviderService.GetProviderModel("openai", "gpt-4", realTime: true).Name;
        }

        completer.SetModelName(model);
        conn.Model = model;

        var routing = _services.GetRequiredService<IRoutingService>();
        routing.Context.Push(agent.Id);

        var dialogs = convService.GetDialogHistory();
        if (dialogs.Count == 0)
        {
            dialogs.Add(new RoleDialogModel(AgentRole.User, "Hi"));
        }
        routing.Context.SetDialogs(dialogs);

        await completer.Connect(conn, 
            onModelReady: async () => 
            {
                // Control initial session, prevent initial response interruption
                await completer.UpdateSession(conn, turnDetection: false);

                // Add dialog history
                //foreach (var item in dialogs)
                //{
                //    await completer.InsertConversationItem(item);
                //}

                if (dialogs.LastOrDefault()?.Role == AgentRole.Assistant)
                {
                    await completer.TriggerModelInference($"Rephase your last response:\r\n{dialogs.LastOrDefault()?.Content}");
                }
                else
                {
                    await completer.TriggerModelInference("Reply based on the conversation context.");
                }

                // Start turn detection
                await Task.Delay(1000 * 8);
                await completer.UpdateSession(conn, turnDetection: true);
            },
            onModelAudioDeltaReceived: async (audioDeltaData, itemId) =>
            {
                var data = conn.OnModelMessageReceived(audioDeltaData);
                await SendEventToUser(userWebSocket, data);

                // If this is the first delta of a new response, set the start timestamp
                if (!conn.ResponseStartTimestampTwilio.HasValue)
                {
                    conn.ResponseStartTimestampTwilio = conn.LatestMediaTimestamp;
                    _logger.LogDebug($"Setting start timestamp for new response: {conn.ResponseStartTimestampTwilio}ms");
                }
                // Record last assistant item ID for interruption handling
                if (!string.IsNullOrEmpty(itemId))
                {
                    conn.LastAssistantItemId = itemId;
                }

                // Send mark messages to Media Streams so we know if and when AI response playback is finished
                await SendMark(userWebSocket, conn);
            }, 
            onModelAudioResponseDone: async () =>
            {
                var data = conn.OnModelAudioResponseDone();
                await SendEventToUser(userWebSocket, data);
            }, 
            onAudioTranscriptDone: async transcript =>
            {

            },
            onModelResponseDone: async messages =>
            {
                foreach (var message in messages)
                {
                    // Invoke function
                    if (message.MessageType == MessageTypeName.FunctionCall)
                    {
                        await routing.InvokeFunction(message.FunctionName, message);
                        message.Role = AgentRole.Function;

                        if (message.FunctionName == "route_to_agent")
                        {
                            var inst = JsonSerializer.Deserialize<RoutingArgs>(message.FunctionArgs ?? "{}");
                            message.Content = $"Connected to agent of {inst.AgentName}";
                            conn.CurrentAgentId = routing.Context.GetCurrentAgentId();

                            await completer.UpdateSession(conn);
                            await completer.InsertConversationItem(message);
                            await completer.TriggerModelInference($"Guide the user through the next steps of the process as this Agent ({inst.AgentName}), following its instructions and operational procedures.");
                        }
                        else if (message.FunctionName == "util-routing-fallback_to_router")
                        {
                            var inst = JsonSerializer.Deserialize<FallbackArgs>(message.FunctionArgs ?? "{}");
                            message.Content = $"Returned to Router due to {inst.Reason}";
                            conn.CurrentAgentId = routing.Context.GetCurrentAgentId();

                            await completer.UpdateSession(conn);
                            await completer.InsertConversationItem(message);
                            await completer.TriggerModelInference($"Check with user whether to proceed the new request: {inst.Reason}");
                        }
                        else
                        {
                            await completer.InsertConversationItem(message);
                            await completer.TriggerModelInference("Reply based on the function's output.");
                        }
                    }
                    else
                    {
                        // append output audio transcript to conversation
                        dialogs.Add(message);

                        foreach (var hook in hookProvider.HooksOrderByPriority)
                        {
                            hook.SetAgent(agent)
                                .SetConversation(conversation);

                            await hook.OnResponseGenerated(message);
                        }
                    }
                }
            },
            onConversationItemCreated: async response =>
            {
                
            },
            onInputAudioTranscriptionCompleted: async message =>
            {
                // append input audio transcript to conversation
                dialogs.Add(message);

                foreach (var hook in hookProvider.HooksOrderByPriority)
                {
                    hook.SetAgent(agent)
                        .SetConversation(conversation);

                    await hook.OnMessageReceived(message);
                }
            },
            onUserInterrupted: async () =>
            {
                // Reset states
                conn.ResetResponseState();

                var data = conn.OnModelUserInterrupted();
                await SendEventToUser(userWebSocket, data);
            });
    }

    private async Task SendMark(WebSocket userWebSocket, RealtimeHubConnection conn)
    {
        if (!string.IsNullOrEmpty(conn.StreamId))
        {
            var markEvent = new
            {
                @event = "mark",
                streamSid = conn.StreamId,
                mark = new { name = "responsePart" }
            };
            await SendEventToUser(userWebSocket, markEvent);
            conn.MarkQueue.Enqueue("responsePart");
        }
    }

    private async Task HandleUserDtmfReceived(IRealTimeCompletion completer, RealtimeHubConnection conn)
    {
        var routing = _services.GetRequiredService<IRoutingService>();
        var hookProvider = _services.GetRequiredService<ConversationHookProvider>();
        var agentService = _services.GetRequiredService<IAgentService>();
        var agent = await agentService.LoadAgent(conn.CurrentAgentId);
        var dialogs = routing.Context.GetDialogs();
        var convService = _services.GetRequiredService<IConversationService>();
        var conversation = await convService.GetConversation(conn.ConversationId);

        var message = new RoleDialogModel(AgentRole.User, conn.Data)
        {
            CurrentAgentId = routing.Context.GetCurrentAgentId()
        };
        dialogs.Add(message);

        foreach (var hook in hookProvider.HooksOrderByPriority)
        {
            hook.SetAgent(agent)
                .SetConversation(conversation);

            await hook.OnMessageReceived(message);
        }

        await completer.InsertConversationItem(message);
        await completer.TriggerModelInference("Reply based on the user input");
    }

    private async Task HandleUserDisconnected(RealtimeHubConnection conn)
    {
        // Save dialog history
        var routing = _services.GetRequiredService<IRoutingService>();
        var storage = _services.GetRequiredService<IConversationStorage>();
        var dialogs = routing.Context.GetDialogs();
        foreach (var item in dialogs)
        {
            storage.Append(conn.ConversationId, item);
        }
    }

    private async Task SendEventToUser(WebSocket webSocket, object message)
    {
        var data = JsonSerializer.Serialize(message);
        var buffer = Encoding.UTF8.GetBytes(data);
        await webSocket.SendAsync(new ArraySegment<byte>(buffer), WebSocketMessageType.Text, true, CancellationToken.None);
    }
}
