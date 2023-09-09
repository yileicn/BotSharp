using BotSharp.Abstraction.Routing.Models;

namespace BotSharp.OpenAPI.ViewModels.Routing;

public class RoutingItemCreationModel
{
    public string AgentId { get; set; }
    public string Name { get; set; }
    public string Description { get; set; }
    public List<string> RequiredFields { get; set; } = new List<string>();
    public string? RedirectTo { get; set; }
    public bool Disabled { get; set; }

    public RoutingItem ToRoutingItem()
    {
        return new RoutingItem
        {
            AgentId = AgentId,
            Name = Name,
            Description = Description,
            RequiredFields = RequiredFields,
            RedirectTo = RedirectTo,
            Disabled = Disabled
        };
    }
}