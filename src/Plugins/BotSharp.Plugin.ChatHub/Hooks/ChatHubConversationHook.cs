using BotSharp.Abstraction.SideCar;
using Microsoft.AspNetCore.SignalR;

namespace BotSharp.Plugin.ChatHub.Hooks;

public class ChatHubConversationHook : ConversationHookBase
{
    private readonly IServiceProvider _services;
    private readonly IHubContext<SignalRHub> _chatHub;
    private readonly IUserIdentity _user;
    private readonly BotSharpOptions _options;
    private readonly ChatHubSettings _settings;

    #region Events
    private const string INIT_CLIENT_CONVERSATION = "OnConversationInitFromClient";
    private const string RECEIVE_CLIENT_MESSAGE = "OnMessageReceivedFromClient";
    private const string RECEIVE_ASSISTANT_MESSAGE = "OnMessageReceivedFromAssistant";
    private const string GENERATE_SENDER_ACTION = "OnSenderActionGenerated";
    private const string DELETE_MESSAGE = "OnMessageDeleted";
    private const string GENERATE_NOTIFICATION = "OnNotificationGenerated";
    #endregion

    public ChatHubConversationHook(
        IServiceProvider services,
        IHubContext<SignalRHub> chatHub,
        BotSharpOptions options,
        ChatHubSettings settings,
        IUserIdentity user)
    {
        _services = services;
        _chatHub = chatHub;
        _user = user;
        _options = options;
        _settings = settings;
        Priority = -1; // Make sure this hook is the top one.
    }

    public override async Task OnConversationInitialized(Conversation conversation)
    {
        if (!AllowSendingMessage()) return;

        var userService = _services.GetRequiredService<IUserService>();
        var conv = ConversationViewModel.FromSession(conversation);

        var user = await userService.GetUser(conv.User.Id);
        conv.User = UserViewModel.FromUser(user);

        //await InitClientConversation(conv);
        await InitClientConversation(conv.Id, conv);
        await base.OnConversationInitialized(conversation);
    }

    public override async Task OnMessageReceived(RoleDialogModel message)
    {
        if (!AllowSendingMessage()) return;

        var conv = _services.GetRequiredService<IConversationService>();
        var userService = _services.GetRequiredService<IUserService>();
        var sender = await userService.GetMyProfile();

        // Update console conversation UI for CSR
        var model = new ChatResponseModel()
        {
            ConversationId = conv.ConversationId,
            MessageId = message.MessageId,
            Payload = message.Payload,
            Text = !string.IsNullOrEmpty(message.SecondaryContent) ? message.SecondaryContent : message.Content,
            Sender = UserViewModel.FromUser(sender)
        };
        await ReceiveClientMessage(conv.ConversationId, model);
        //await ReceiveClientMessage(model);

        // Send typing-on to client
        var action = new ConversationSenderActionModel
        {
            ConversationId = conv.ConversationId,
            SenderAction = SenderActionEnum.TypingOn
        };

        await GenerateSenderAction(conv.ConversationId, action);
        //await GenerateSenderAction(action);
        await base.OnMessageReceived(message);
    }

    public override async Task OnFunctionExecuting(RoleDialogModel message)
    {
        var conv = _services.GetRequiredService<IConversationService>();
        var action = new ConversationSenderActionModel
        {
            ConversationId = conv.ConversationId,
            SenderAction = SenderActionEnum.TypingOn,
            Indication = message.Indication
        };

        await GenerateSenderAction(conv.ConversationId, action);
        //await GenerateSenderAction(action);
        await base.OnFunctionExecuting(message);
    }

    public override async Task OnPostbackMessageReceived(RoleDialogModel message, PostbackMessageModel replyMsg)
    {
        await this.OnMessageReceived(message);
    }

    public override async Task OnResponseGenerated(RoleDialogModel message)
    {
        if (!AllowSendingMessage()) return;

        var conv = _services.GetRequiredService<IConversationService>();
        var json = JsonSerializer.Serialize(new ChatResponseModel()
        {
            ConversationId = conv.ConversationId,
            MessageId = message.MessageId,
            Text = !string.IsNullOrEmpty(message.SecondaryContent) ? message.SecondaryContent : message.Content,
            Function = message.FunctionName,
            RichContent = message.SecondaryRichContent ?? message.RichContent,
            Data = message.Data,
            Sender = new UserViewModel()
            {
                FirstName = "AI",
                LastName = "Assistant",
                Role = AgentRole.Assistant
            }
        }, _options.JsonSerializerOptions);

        // Send typing-off to client
        var action = new ConversationSenderActionModel
        {
            ConversationId = conv.ConversationId,
            SenderAction = SenderActionEnum.TypingOff
        };

        await GenerateSenderAction(conv.ConversationId, action);
        await ReceiveAssistantMessage(conv.ConversationId, json);
        //await GenerateSenderAction(action);
        //await ReceiveAssistantMessage(json);
        await base.OnResponseGenerated(message);
    }


    public override async Task OnNotificationGenerated(RoleDialogModel message)
    {
        var conv = _services.GetRequiredService<IConversationService>();
        var json = JsonSerializer.Serialize(new ChatResponseModel()
        {
            ConversationId = conv.ConversationId,
            MessageId = message.MessageId,
            Text = !string.IsNullOrEmpty(message.SecondaryContent) ? message.SecondaryContent : message.Content,
            Function = message.FunctionName,
            RichContent = message.SecondaryRichContent ?? message.RichContent,
            Data = message.Data,
            Sender = new UserViewModel()
            {
                FirstName = "AI",
                LastName = "Assistant",
                Role = AgentRole.Assistant
            }
        }, _options.JsonSerializerOptions);

        await GenerateNotification(conv.ConversationId, json);
        //await GenerateNotification(json);
        await base.OnNotificationGenerated(message);
    }


    public override async Task OnMessageDeleted(string conversationId, string messageId)
    {
        var model = new ChatResponseModel
        {
            ConversationId = conversationId,
            MessageId = messageId
        };

        await DeleteMessage(conversationId, model);
        //await DeleteMessage(model);
        await base.OnMessageDeleted(conversationId, messageId);
    }

    #region Private methods
    private bool AllowSendingMessage()
    {
        var sidecar = _services.GetService<IConversationSideCar>();
        return sidecar == null || !sidecar.IsEnabled();
    }

    private async Task InitClientConversation(string conversationId, ConversationViewModel conversation)
    {
        if (_settings.EventDispatchBy == EventDispatchType.Group)
        {
            await _chatHub.Clients.Group(conversationId).SendAsync(INIT_CLIENT_CONVERSATION, conversation);
        }
        else
        {
            await _chatHub.Clients.User(_user.Id).SendAsync(INIT_CLIENT_CONVERSATION, conversation);
        }
    }

    private async Task ReceiveClientMessage(string conversationId, ChatResponseModel model)
    {
        if (_settings.EventDispatchBy == EventDispatchType.Group)
        {
            await _chatHub.Clients.Group(conversationId).SendAsync(RECEIVE_CLIENT_MESSAGE, model);
        }
        else
        {
            await _chatHub.Clients.User(_user.Id).SendAsync(RECEIVE_CLIENT_MESSAGE, model);
        }
    }

    private async Task ReceiveAssistantMessage(string conversationId, string? json)
    {
        if (_settings.EventDispatchBy == EventDispatchType.Group)
        {
            await _chatHub.Clients.Group(conversationId).SendAsync(RECEIVE_ASSISTANT_MESSAGE, json);
        }
        else
        {
            await _chatHub.Clients.User(_user.Id).SendAsync(RECEIVE_ASSISTANT_MESSAGE, json);
        }
            
    }

    private async Task GenerateSenderAction(string conversationId, ConversationSenderActionModel action)
    {
        if (_settings.EventDispatchBy == EventDispatchType.Group)
        {
            await _chatHub.Clients.Group(conversationId).SendAsync(GENERATE_SENDER_ACTION, action);
        }
        else
        {
            await _chatHub.Clients.User(_user.Id).SendAsync(GENERATE_SENDER_ACTION, action);
        }
    }

    private async Task DeleteMessage(string conversationId, ChatResponseModel model)
    {
        if (_settings.EventDispatchBy == EventDispatchType.Group)
        {
            await _chatHub.Clients.Group(conversationId).SendAsync(DELETE_MESSAGE, model);
        }
        else
        {
            await _chatHub.Clients.User(_user.Id).SendAsync(DELETE_MESSAGE, model);
        }
    }

    private async Task GenerateNotification(string conversationId, string? json)
    {
        if (_settings.EventDispatchBy == EventDispatchType.Group)
        {
            await _chatHub.Clients.Group(conversationId).SendAsync(GENERATE_NOTIFICATION, json);
        }
        else
        {
            await _chatHub.Clients.User(_user.Id).SendAsync(GENERATE_NOTIFICATION, json);
        }
    }
    #endregion
}
