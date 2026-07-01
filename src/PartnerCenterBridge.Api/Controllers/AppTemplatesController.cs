using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PartnerCenterBridge.Api.Contracts;
using PartnerCenterBridge.Api.Orchestration;
using PartnerCenterBridge.Core.Abstractions;
using PartnerCenterBridge.Core.Entities;
using PartnerCenterBridge.Data;

namespace PartnerCenterBridge.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class AppTemplatesController : ControllerBase
{
    private readonly BridgeDbContext _db;

    public AppTemplatesController(BridgeDbContext db) => _db = db;

    [HttpGet]
    public async Task<IReadOnlyList<AppTemplateDto>> List(CancellationToken ct) =>
        (await _db.AppTemplates.ToListAsync(ct)).Select(AppTemplateDto.From).ToList();

    [HttpPost]
    public async Task<ActionResult<AppTemplateDto>> Create(CreateAppTemplateRequest req, CancellationToken ct)
    {
        var template = new AppTemplate
        {
            DisplayName = req.DisplayName,
            Description = req.Description,
            Publisher = req.Publisher,
            InstallCommandLine = req.InstallCommandLine,
            UninstallCommandLine = req.UninstallCommandLine,
            ContractId = req.ContractId,
            DetectionRules = req.DetectionRules ?? new(),
            Assignments = req.Assignments ?? new()
        };
        _db.AppTemplates.Add(template);
        await _db.SaveChangesAsync(ct);
        return CreatedAtAction(nameof(List), AppTemplateDto.From(template));
    }

    /// <summary>
    /// Upload (or replace) the .intunewin package for a template. Parsing the package captures the
    /// encryption info now; replacing it bumps the content version so an update fans out on deploy.
    /// </summary>
    [HttpPost("{id:guid}/package")]
    [RequestSizeLimit(2L * 1024 * 1024 * 1024)]
    public async Task<ActionResult<AppTemplateDto>> UploadPackage(
        Guid id,
        IFormFile file,
        [FromServices] IIntuneWinPackageReader reader,
        [FromServices] IPackageStore packages,
        CancellationToken ct)
    {
        var template = await _db.AppTemplates.FindAsync([id], ct);
        if (template is null) return NotFound();
        if (file is null || file.Length == 0) return BadRequest("No file uploaded.");

        // Persist the raw package for later per-tenant streaming, then parse its metadata.
        string reference;
        await using (var upload = file.OpenReadStream())
            reference = await packages.SaveAsync(upload, file.FileName, ct);

        await using var stored = await packages.OpenAsync(reference, ct);
        var content = await reader.ReadMetadataAsync(stored, ct);
        content.StagedPayloadRef = reference;

        var isFirst = template.Content is null;
        template.Content = content;
        if (!isFirst) template.ContentVersion++;
        template.UpdatedAt = DateTimeOffset.UtcNow;

        await _db.SaveChangesAsync(ct);
        return Ok(AppTemplateDto.From(template));
    }
}
