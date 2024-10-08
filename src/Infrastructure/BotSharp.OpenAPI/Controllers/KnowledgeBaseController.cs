using BotSharp.Abstraction.Files.Constants;
using BotSharp.Abstraction.Graph.Models;
using BotSharp.Abstraction.Knowledges.Models;
using BotSharp.Abstraction.VectorStorage.Models;
using BotSharp.OpenAPI.ViewModels.Knowledges;

namespace BotSharp.OpenAPI.Controllers;

[Authorize]
[ApiController]
public class KnowledgeBaseController : ControllerBase
{
    private readonly IKnowledgeService _knowledgeService;
    private readonly IServiceProvider _services;

    public KnowledgeBaseController(IKnowledgeService knowledgeService, IServiceProvider services)
    {
        _knowledgeService = knowledgeService;
        _services = services;
    }

    #region Vector
    [HttpGet("knowledge/vector/collections")]
    public async Task<IEnumerable<string>> GetVectorCollections([FromQuery] string type)
    {
        return await _knowledgeService.GetVectorCollections(type);
    }

    [HttpPost("knowledge/vector/create-collection")]
    public async Task<bool> CreateVectorCollection([FromBody] CreateVectorCollectionRequest request)
    {
        return await _knowledgeService.CreateVectorCollection(request.CollectionName, request.CollectionType, request.Dimension, request.Provider, request.Model);
    }

    [HttpDelete("knowledge/vector/{collection}/delete-collection")]
    public async Task<bool> DeleteVectorCollection([FromRoute] string collection)
    {
        return await _knowledgeService.DeleteVectorCollection(collection);
    }

    [HttpPost("/knowledge/vector/{collection}/search")]
    public async Task<IEnumerable<VectorKnowledgeViewModel>> SearchVectorKnowledge([FromRoute] string collection, [FromBody] SearchVectorKnowledgeRequest request)
    {
        var options = new VectorSearchOptions
        {
            Fields = request.Fields,
            Limit = request.Limit ?? 5,
            Confidence = request.Confidence ?? 0.5f,
            WithVector = request.WithVector
        };

        var results = await _knowledgeService.SearchVectorKnowledge(request.Text, collection, options);
        return results.Select(x => VectorKnowledgeViewModel.From(x)).ToList();
    }

    [HttpPost("/knowledge/vector/{collection}/page")]
    public async Task<StringIdPagedItems<VectorKnowledgeViewModel>> GetPagedVectorCollectionData([FromRoute] string collection, [FromBody] VectorFilter filter)
    {
        var data = await _knowledgeService.GetPagedVectorCollectionData(collection, filter);
        var items = data.Items?.Select(x => VectorKnowledgeViewModel.From(x))?
                               .ToList() ?? new List<VectorKnowledgeViewModel>();

        return new StringIdPagedItems<VectorKnowledgeViewModel>
        {
            Count = data.Count,
            NextId = data.NextId,
            Items = items
        };
    }

    [HttpPost("/knowledge/vector/{collection}/create")]
    public async Task<bool> CreateVectorKnowledge([FromRoute] string collection, [FromBody] VectorKnowledgeCreateRequest request)
    {
        var create = new VectorCreateModel
        {
            Text = request.Text,
            Payload = request.Payload
        };

        var created = await _knowledgeService.CreateVectorCollectionData(collection, create);
        return created;
    }

    [HttpPut("/knowledge/vector/{collection}/update")]
    public async Task<bool> UpdateVectorKnowledge([FromRoute] string collection, [FromBody] VectorKnowledgeUpdateRequest request)
    {
        var update = new VectorUpdateModel
        {
            Id = request.Id,
            Text = request.Text,
            Payload = request.Payload
        };

        var updated = await _knowledgeService.UpdateVectorCollectionData(collection, update);
        return updated;
    }

    [HttpDelete("/knowledge/vector/{collection}/data/{id}")]
    public async Task<bool> DeleteVectorCollectionData([FromRoute] string collection, [FromRoute] string id)
    {
        return await _knowledgeService.DeleteVectorCollectionData(collection, id);
    }
    #endregion


    #region Graph
    [HttpPost("/knowledge/graph/search")]
    public async Task<GraphKnowledgeViewModel> SearchGraphKnowledge([FromBody] SearchGraphKnowledgeRequest request)
    {
        var options = new GraphSearchOptions
        {
            Method = request.Method
        };

        var result = await _knowledgeService.SearchGraphKnowledge(request.Query, options);
        return new GraphKnowledgeViewModel
        {
            Result = result.Result
        };
    }
    #endregion


    #region Document
    [HttpPost("/knowledge/document/{collection}/upload")]
    public async Task<UploadKnowledgeResponse> UploadKnowledgeDocuments([FromRoute] string collection, [FromBody] VectorKnowledgeUploadRequest request)
    {
        var response = await _knowledgeService.UploadKnowledgeDocuments(collection, request.Files);
        return response;
    }

    [HttpDelete("/knowledge/document/{collection}/delete/{fileId}")]
    public async Task<bool> DeleteKnowledgeDocument([FromRoute] string collection, [FromRoute] string fileId)
    {
        var response = await _knowledgeService.DeleteKnowledgeDocument(collection, fileId);
        return response;
    }

    [HttpGet("/knowledge/document/{collection}/list")]
    public async Task<IEnumerable<KnowledgeFileViewModel>> GetKnowledgeDocuments([FromRoute] string collection)
    {
        var files = await _knowledgeService.GetKnowledgeDocuments(collection);
        return files.Select(x => KnowledgeFileViewModel.From(x));
    }

    [HttpGet("/knowledge/document/{collection}/file/{fileId}")]
    public async Task<IActionResult> GetKnowledgeDocument([FromRoute] string collection, [FromRoute] string fileId)
    {
        var file = await _knowledgeService.GetKnowledgeDocumentBinaryData(collection, fileId);
        return BuildFileResult(file);
    }
    #endregion


    #region Common
    [HttpPost("/knowledge/vector/refresh-configs")]
    public async Task<string> RefreshVectorCollectionConfigs([FromBody] VectorCollectionConfigsModel request)
    {
        var saved = await _knowledgeService.RefreshVectorKnowledgeConfigs(request);
        return saved ? "Success" : "Fail";
    }
    #endregion


    #region Private methods
    private FileContentResult BuildFileResult(FileBinaryDataModel? file)
    {
        if (file == null)
        {
            return File(new byte[0], "application/octet-stream", "error.txt");
        }

        return File(file.FileBinaryData.ToArray(), "application/octet-stream", file.FileName);
    }
    #endregion
}
