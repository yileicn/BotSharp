using BotSharp.Abstraction.Instructs.Models;
using BotSharp.Abstraction.Loggers.Models;
using BotSharp.Abstraction.Repositories.Filters;
using System.Text.Json;

namespace BotSharp.Plugin.MongoStorage.Repository;

public partial class MongoRepository
{
    #region LLM Completion Log
    public void SaveLlmCompletionLog(LlmCompletionLog log)
    {
        if (log == null) return;

        var conversationId = log.ConversationId.IfNullOrEmptyAs(Guid.NewGuid().ToString());
        var messageId = log.MessageId.IfNullOrEmptyAs(Guid.NewGuid().ToString());

        var data = new LlmCompletionLogDocument
        {
            Id = Guid.NewGuid().ToString(),
            ConversationId = conversationId,
            MessageId = messageId,
            AgentId = log.AgentId,
            Prompt = log.Prompt,
            Response = log.Response,
            CreatedTime = log.CreatedTime
        };
        _dc.LlmCompletionLogs.InsertOne(data);
    }

    #endregion

    #region Conversation Content Log
    public void SaveConversationContentLog(ContentLogOutputModel log)
    {
        if (log == null) return;

        var found = _dc.Conversations.AsQueryable().FirstOrDefault(x => x.Id == log.ConversationId);
        if (found == null) return;

        var logDoc = new ConversationContentLogDocument
        {
            ConversationId = log.ConversationId,
            MessageId = log.MessageId,
            Name = log.Name,
            AgentId = log.AgentId,
            Role = log.Role,
            Source = log.Source,
            Content = log.Content,
            CreatedTime = log.CreatedTime
        };

        _dc.ContentLogs.InsertOne(logDoc);
    }

    public List<ContentLogOutputModel> GetConversationContentLogs(string conversationId)
    {
        var logs = _dc.ContentLogs
                      .AsQueryable()
                      .Where(x => x.ConversationId == conversationId)
                      .Select(x => new ContentLogOutputModel
                      {
                          ConversationId = x.ConversationId,
                          MessageId = x.MessageId,
                          Name = x.Name,
                          AgentId = x.AgentId,
                          Role = x.Role,
                          Source = x.Source,
                          Content = x.Content,
                          CreatedTime = x.CreatedTime
                      })
                      .OrderBy(x => x.CreatedTime)
                      .ToList();
        return logs;
    }
    #endregion

    #region Conversation State Log
    public void SaveConversationStateLog(ConversationStateLogModel log)
    {
        if (log == null) return;

        var found = _dc.Conversations.AsQueryable().FirstOrDefault(x => x.Id == log.ConversationId);
        if (found == null) return;

        var logDoc = new ConversationStateLogDocument
        {
            ConversationId = log.ConversationId,
            AgentId= log.AgentId,
            MessageId = log.MessageId,
            States = log.States,
            CreatedTime = log.CreatedTime
        };

        _dc.StateLogs.InsertOne(logDoc);
    }

    public List<ConversationStateLogModel> GetConversationStateLogs(string conversationId)
    {
        var logs = _dc.StateLogs
                      .AsQueryable()
                      .Where(x => x.ConversationId == conversationId)
                      .Select(x => new ConversationStateLogModel
                      {
                          ConversationId = x.ConversationId,
                          AgentId = x.AgentId,
                          MessageId = x.MessageId,
                          States = x.States,
                          CreatedTime = x.CreatedTime
                      })
                      .OrderBy(x => x.CreatedTime)
                      .ToList();
        return logs;
    }
    #endregion

    #region Instruction Log
    public bool SaveInstructionLogs(IEnumerable<InstructionLogModel> logs)
    {
        if (logs.IsNullOrEmpty()) return false;

        var docs = new List<InstructionLogBetaDocument>();
        foreach (var log in logs)
        {
            var doc = InstructionLogBetaDocument.ToMongoModel(log);
            foreach (var pair in log.States)
            {
                try
                {
                    var jsonStr = JsonSerializer.Serialize(new { Data = JsonDocument.Parse(pair.Value) }, _botSharpOptions.JsonSerializerOptions);
                    var json = BsonDocument.Parse(jsonStr);
                    doc.States[pair.Key] = json;
                }
                catch
                {
                    var jsonStr = JsonSerializer.Serialize(new { Data = pair.Value }, _botSharpOptions.JsonSerializerOptions);
                    var json = BsonDocument.Parse(jsonStr);
                    doc.States[pair.Key] = json;
                }
            }
            docs.Add(doc);
        }

        _dc.InstructionLogs.InsertMany(docs);
        return true;
    }

    public PagedItems<InstructionLogModel> GetInstructionLogs(InstructLogFilter filter)
    {
        if (filter == null)
        {
            filter = InstructLogFilter.Empty();
        }

        var builder = Builders<InstructionLogBetaDocument>.Filter;
        var filters = new List<FilterDefinition<InstructionLogBetaDocument>>() { builder.Empty };

        // Filter logs
        if (!filter.AgentIds.IsNullOrEmpty())
        {
            filters.Add(builder.In(x => x.AgentId, filter.AgentIds));
        }
        if (!filter.Providers.IsNullOrEmpty())
        {
            filters.Add(builder.In(x => x.Provider, filter.Providers));
        }
        if (!filter.Models.IsNullOrEmpty())
        {
            filters.Add(builder.In(x => x.Model, filter.Models));
        }
        if (!filter.TemplateNames.IsNullOrEmpty())
        {
            filters.Add(builder.In(x => x.TemplateName, filter.TemplateNames));
        }

        var filterDef = builder.And(filters);
        var sortDef = Builders<InstructionLogBetaDocument>.Sort.Descending(x => x.CreatedTime);
        var docs = _dc.InstructionLogs.Find(filterDef).Sort(sortDef).Skip(filter.Offset).Limit(filter.Size).ToList();
        var count = _dc.InstructionLogs.CountDocuments(filterDef);

        var agentIds = docs.Where(x => !string.IsNullOrEmpty(x.AgentId)).Select(x => x.AgentId).ToList();
        var agents = GetAgents(new AgentFilter
        {
            AgentIds = agentIds
        });

        var logs = docs.Select(x =>
        {
            var log = InstructionLogBetaDocument.ToDomainModel(x);
            log.AgentName = !string.IsNullOrEmpty(x.AgentId) ? agents.FirstOrDefault(a => a.Id == x.AgentId)?.Name : null;
            log.States = x.States.ToDictionary(p => p.Key, p =>
            {
                var jsonStr = p.Value.ToJson();
                var jsonDoc = JsonDocument.Parse(jsonStr);
                var data = jsonDoc.RootElement.GetProperty("data");
                return data.ValueKind != JsonValueKind.Null ? data.ToString() : null;
            });
            return log;
        }).ToList();

        return new PagedItems<InstructionLogModel>
        {
            Items = logs,
            Count = (int)count
        };
    }
    #endregion
}
