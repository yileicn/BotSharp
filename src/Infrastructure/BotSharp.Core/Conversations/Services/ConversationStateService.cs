/*****************************************************************************
  Copyright 2024 Written by Jicheng Lu. All Rights Reserved.
 
  Licensed under the Apache License, Version 2.0 (the "License");
  you may not use this file except in compliance with the License.
  You may obtain a copy of the License at
 
      http://www.apache.org/licenses/LICENSE-2.0
 
  Unless required by applicable law or agreed to in writing, software
  distributed under the License is distributed on an "AS IS" BASIS,
  WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
  See the License for the specific language governing permissions and
  limitations under the License.
******************************************************************************/

using BotSharp.Abstraction.Conversations.Enums;
using BotSharp.Abstraction.Hooks;
using BotSharp.Abstraction.Options;
using BotSharp.Abstraction.SideCar;

namespace BotSharp.Core.Conversations.Services;

/// <summary>
/// Maintain the conversation state
/// </summary>
public class ConversationStateService : IConversationStateService
{
    private readonly ILogger _logger;
    private readonly IServiceProvider _services;
    private readonly IBotSharpRepository _db;
    private readonly IRoutingContext _routingContext;
    private readonly IConversationSideCar? _sidecar;
    private string _conversationId;
    /// <summary>
    /// States in the current round of conversation
    /// </summary>
    private ConversationState _curStates;
    /// <summary>
    /// States in the previous rounds of conversation
    /// </summary>
    private ConversationState _historyStates;

    public ConversationStateService(
        IServiceProvider services,
        IBotSharpRepository db,
        IRoutingContext routingContext,
        ILogger<ConversationStateService> logger)
    {
        _services = services;
        _db = db;
        _routingContext = routingContext;
        _logger = logger;
        _curStates = new ConversationState();
        _historyStates = new ConversationState();
        _sidecar = services.GetService<IConversationSideCar>();
    }

    public string GetConversationId() => _conversationId;

    /// <summary>
    /// Set conversation state
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="name"></param>
    /// <param name="value"></param>
    /// <param name="isNeedVersion">whether the state is related to message or not</param>
    /// <returns></returns>
    public IConversationStateService SetState<T>(string name, T value, bool isNeedVersion = true,
        int activeRounds = -1, string valueType = StateDataType.String, string source = StateSource.User, bool readOnly = false)
    {
        if (value == null)
        {
            return this;
        }

        var options = _services.GetRequiredService<BotSharpOptions>();

        var defaultRound = -1;
        var preValue = string.Empty;
        var currentValue = value.ConvertToString(options.JsonSerializerOptions);
        var curActive = true;
        StateKeyValue? pair = null;
        StateValue? prevLeafNode = null;
        var curActiveRounds = activeRounds > 0 ? activeRounds : defaultRound;

        if (ContainsState(name) && _curStates.TryGetValue(name, out pair))
        {
            prevLeafNode = pair?.Values?.LastOrDefault();
            preValue = prevLeafNode?.Data ?? string.Empty;
        }

        _logger.LogDebug($"[STATE] {name} = {value}");

        var isNoChange = ContainsState(name)
                          && preValue == currentValue
                          && prevLeafNode?.ActiveRounds == curActiveRounds
                          && curActiveRounds == defaultRound
                          && prevLeafNode?.Source == source
                          && prevLeafNode?.DataType == valueType
                          && prevLeafNode?.Active == curActive
                          && pair?.Readonly == readOnly;

        var hooks = _services.GetHooks<IConversationHook>(_routingContext.GetCurrentAgentId());
        if (!ContainsState(name) || preValue != currentValue || prevLeafNode?.ActiveRounds != curActiveRounds)
        {
            foreach (var hook in hooks)
            {
                hook.OnStateChanged(new StateChangeModel
                {
                    ConversationId = _conversationId,
                    MessageId = _routingContext.MessageId,
                    Name = name,
                    BeforeValue = preValue,
                    BeforeActiveRounds = prevLeafNode?.ActiveRounds,
                    AfterValue = currentValue,
                    AfterActiveRounds = curActiveRounds,
                    DataType = valueType,
                    Source = source,
                    Readonly = readOnly
                }).Wait();
            }
        }

        var newPair = new StateKeyValue
        {
            Key = name,
            Versioning = isNeedVersion,
            Readonly = readOnly
        };

        var newValue = new StateValue
        {
            Data = currentValue,
            MessageId = _routingContext.MessageId,
            Active = curActive,
            ActiveRounds = curActiveRounds,
            DataType = valueType,
            Source = source,
            UpdateTime = DateTime.UtcNow,
        };

        if (!isNeedVersion || !_curStates.ContainsKey(name))
        {
            newPair.Values = new List<StateValue> { newValue };
            _curStates[name] = newPair;
        }
        else if (!isNoChange)
        {
            _curStates[name].Values.Add(newValue);
        }

        return this;
    }

    public Dictionary<string, string> Load(string conversationId, bool isReadOnly = false)
    {
        _conversationId = !isReadOnly ? conversationId : null;
        Reset();

        var endNodes = new Dictionary<string, string>();
        if (_sidecar?.IsEnabled() == true)
        {
            return endNodes;
        }

        _historyStates = _db.GetConversationStates(conversationId);
        if (_historyStates.IsNullOrEmpty())
        {
            return endNodes;
        }

        var curMsgId = _routingContext.MessageId;
        var dialogs = _db.GetConversationDialogs(conversationId);
        var userDialogs = dialogs.Where(x => x.MetaData?.Role == AgentRole.User)
                                 .GroupBy(x => x.MetaData?.MessageId)
                                 .Select(g => g.First())
                                 .OrderBy(x => x.MetaData?.CreatedTime)
                                 .ToList();
        var curMsgIndex = userDialogs.FindIndex(x => !string.IsNullOrEmpty(curMsgId) && x.MetaData?.MessageId == curMsgId);
        curMsgIndex = curMsgIndex < 0 ? userDialogs.Count() : curMsgIndex;

        foreach (var state in _historyStates)
        {
            var key = state.Key;
            var value = state.Value;
            var leafNode = value?.Values?.LastOrDefault();
            if (leafNode == null) continue;

            _curStates[key] = new StateKeyValue
            {
                Key = key,
                Versioning = value.Versioning,
                Readonly = value.Readonly,
                Values = new List<StateValue> { leafNode }
            };

            if (!leafNode.Active) continue;

            // Handle state active rounds
            if (leafNode.ActiveRounds > 0)
            {
                var stateMsgIndex = userDialogs.FindIndex(x => !string.IsNullOrEmpty(x.MetaData?.MessageId) && x.MetaData.MessageId == leafNode.MessageId);
                if (stateMsgIndex >= 0 && curMsgIndex - stateMsgIndex >= leafNode.ActiveRounds)
                {
                    _curStates[key].Values.Add(new StateValue
                    {
                        Data = leafNode.Data,
                        MessageId = curMsgId,
                        Active = false,
                        ActiveRounds = leafNode.ActiveRounds,
                        DataType = leafNode.DataType,
                        Source = leafNode.Source,
                        UpdateTime = DateTime.UtcNow
                    });
                    continue;
                }
            }

            var data = leafNode.Data ?? string.Empty;
            endNodes[state.Key] = data;
            _logger.LogDebug($"[STATE] {key} : {data}");
        }

        _logger.LogInformation($"Loaded conversation states: {conversationId}");
        var hooks = _services.GetHooks<IConversationHook>(_routingContext.GetCurrentAgentId());
        foreach (var hook in hooks)
        {
            hook.OnStateLoaded(_curStates).Wait();
        }

        return endNodes;
    }

    public void Save()
    {
        if (_conversationId == null || _sidecar?.IsEnabled() == true)
        {
            return;
        }

        var states = new List<StateKeyValue>();
        foreach (var pair in _curStates)
        {
            var key = pair.Key;
            var curValue = pair.Value;

            if (!_historyStates.TryGetValue(key, out var historyValue)
                || historyValue == null
                || historyValue.Values.IsNullOrEmpty()
                || !curValue.Versioning)
            {
                states.Add(curValue);
            }
            else
            {
                var historyValues = historyValue.Values.Take(historyValue.Values.Count - 1).ToList();
                var newValues = historyValues.Concat(curValue.Values).ToList();
                var updatedNode = new StateKeyValue
                {
                    Key = pair.Key,
                    Versioning = curValue.Versioning,
                    Readonly = curValue.Readonly,
                    Values = newValues
                };
                states.Add(updatedNode);
            }
        }

        _db.UpdateConversationStates(_conversationId, states);
        _logger.LogInformation($"Saved states of conversation {_conversationId}");
    }

    public bool RemoveState(string name)
    {
        if (!ContainsState(name)) return false;

        var value = _curStates[name];
        var leafNode = value?.Values?.LastOrDefault();
        if (value == null || !value.Versioning || leafNode == null) return false;

        _curStates[name].Values.Add(new StateValue
        {
            Data = leafNode.Data,
            MessageId = _routingContext.MessageId,
            Active = false,
            ActiveRounds = leafNode.ActiveRounds,
            DataType = leafNode.DataType,
            Source = leafNode.Source,
            UpdateTime = DateTime.UtcNow
        });

        var hooks = _services.GetHooks<IConversationHook>(_routingContext.GetCurrentAgentId());
        foreach (var hook in hooks)
        {
            hook.OnStateChanged(new StateChangeModel
            {
                ConversationId = _conversationId,
                MessageId = _routingContext.MessageId,
                Name = name,
                BeforeValue = leafNode.Data,
                BeforeActiveRounds = leafNode.ActiveRounds,
                AfterValue = null,
                AfterActiveRounds = leafNode.ActiveRounds,
                DataType = leafNode.DataType,
                Source = leafNode.Source,
                Readonly = value.Readonly
            }).Wait();
        }

        return true;
    }

    public void CleanStates(params string[] excludedStates)
    {
        var curMsgId = _routingContext.MessageId;
        var utcNow = DateTime.UtcNow;

        foreach (var key in _curStates.Keys)
        {
            // skip state
            if (excludedStates.Contains(key))
            {
                continue;
            }

            var value = _curStates[key];
            if (value == null || !value.Versioning || value.Values.IsNullOrEmpty()) continue;

            var leafNode = value.Values.LastOrDefault();
            if (leafNode == null || !leafNode.Active) continue;

            value.Values.Add(new StateValue
            {
                Data = leafNode.Data,
                MessageId = curMsgId,
                Active = false,
                ActiveRounds = leafNode.ActiveRounds,
                DataType = leafNode.DataType,
                Source = leafNode.Source,
                UpdateTime = utcNow
            });
        }
    }

    public Dictionary<string, string> GetStates()
    {
        var endNodes = new Dictionary<string, string>();
        foreach (var state in _curStates)
        {
            var value = state.Value?.Values?.LastOrDefault();
            if (value == null || !value.Active) continue;

            endNodes[state.Key] = value.Data ?? string.Empty;
        }
        return endNodes;
    }

    public string GetState(string name, string defaultValue = "")
    {
        if (!_curStates.ContainsKey(name) || _curStates[name].Values.IsNullOrEmpty() || !_curStates[name].Values.Last().Active)
        {
            return defaultValue;
        }

        return _curStates[name].Values.Last().Data;
    }

    public void Dispose()
    {
        Save();
    }

    public bool ContainsState(string name)
    {
        return _curStates.ContainsKey(name)
            && !_curStates[name].Values.IsNullOrEmpty()
            && _curStates[name].Values.LastOrDefault()?.Active == true
            && !string.IsNullOrEmpty(_curStates[name].Values.Last().Data);
    }

    public void SaveStateByArgs(JsonDocument args)
    {
        if (args == null)
        {
            return;
        }

        if (args.RootElement is JsonElement root)
        {
            foreach (JsonProperty property in root.EnumerateObject())
            {
                var propertyValue = property.Value;
                var stateValue = propertyValue.ToString();
                if (!string.IsNullOrEmpty(stateValue))
                {
                    if (propertyValue.ValueKind == JsonValueKind.True ||
                        propertyValue.ValueKind == JsonValueKind.False)
                    {
                        stateValue = stateValue?.ToLower();
                    }

                    if (CheckArgType(property.Name, stateValue))
                    {
                        SetState(property.Name, stateValue, source: StateSource.Application);
                    }
                }
            }
        }
    }

    private bool CheckArgType(string name, string value)
    {
        // Defensive: Ensure AgentParameterTypes is not null or empty and values are not null
        if (AgentService.AgentParameterTypes.IsNullOrEmpty())
            return true;

        if (!AgentService.AgentParameterTypes.TryGetValue(_routingContext.GetCurrentAgentId(), out var agentTypes))
            return true;

        if (agentTypes.IsNullOrEmpty())
            return true;
            
        var found = agentTypes.FirstOrDefault(t => t.Key == name); 
        if (found.Key != null)
        {
            return found.Value switch
            {
                "boolean" => bool.TryParse(value, out _),
                "number" => long.TryParse(value, out _),
                _ => true,
            };
        }
        return true;
    }

    public ConversationState GetCurrentState()
    {
        var values = _curStates.Values.ToList();
        var copy = JsonSerializer.Deserialize<List<StateKeyValue>>(JsonSerializer.Serialize(values));
        return new ConversationState(copy ?? []);
    }

    public void SetCurrentState(ConversationState state)
    {
        var values = state.Values.ToList();
        var copy = JsonSerializer.Deserialize<List<StateKeyValue>>(JsonSerializer.Serialize(values));
        _curStates = new ConversationState(copy ?? []);
    }

    public void ResetCurrentState()
    {
        _curStates.Clear();
    }

    private void Reset()
    {
        _curStates.Clear();
        _historyStates.Clear();
    }
}
