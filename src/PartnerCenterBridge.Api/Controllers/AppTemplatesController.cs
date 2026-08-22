using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PartnerCenterBridge.Api.Auth;
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
    private readonly ITenantAccessService _access;

    public AppTemplatesController(BridgeDbContext db, ITenantAccessService access)
    {
        _db = db;
        _access = access;
    }

    [HttpGet]
    public async Task<IReadOnlyList<AppTemplateDto>> List(CancellationToken ct) =>
        (await _db.AppTemplates.ToListAsync(ct)).Select(AppTemplateDto.From).ToList();

    [HttpPost]
    public async Task<ActionResult<AppTemplateDto>> Create(CreateAppTemplateRequest req, CancellationToken ct)
    {
        if (!_access.IsSystemAdmin) return Forbid();

        if (req.ContractId is Guid contractId)
        {
            if (!await _db.Contracts.AnyAsync(c => c.Id == contractId, ct))
                return NotFound("Contract not found.");
        }

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
    /// Edits a template's authoring metadata. Deliberately excludes DetectionRules/Assignments/
    /// ContractId -- those aren't editable through this endpoint yet -- and never touches
    /// ContentVersion, which tracks the uploaded .intunewin package, not metadata.
    /// </summary>
    [HttpPut("{id:guid}")]
    public async Task<ActionResult<AppTemplateDto>> Update(Guid id, UpdateAppTemplateRequest req, CancellationToken ct)
    {
        if (!_access.IsSystemAdmin) return Forbid();

        var template = await _db.AppTemplates.FindAsync([id], ct);
        if (template is null) return NotFound();

        template.DisplayName = req.DisplayName;
        template.Description = req.Description;
        template.Publisher = req.Publisher;
        template.InstallCommandLine = req.InstallCommandLine;
        template.UninstallCommandLine = req.UninstallCommandLine;
        template.UpdatedAt = DateTimeOffset.UtcNow;

        await _db.SaveChangesAsync(ct);
        return Ok(AppTemplateDto.From(template));
    }

    /// <summary>
    /// Deletes a template. Refuses when any Deployment still references it -- the FK is
    /// DeleteBehavior.Cascade at the database level, so an unguarded delete would silently wipe
    /// that tenant's deployment history rather than actually protecting it.
    /// </summary>
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        if (!_access.IsSystemAdmin) return Forbid();

        var template = await _db.AppTemplates.FindAsync([id], ct);
        if (template is null) return NotFound();

        var deploymentCount = await _db.Deployments.CountAsync(d => d.AppTemplateId == id, ct);
        if (deploymentCount > 0)
            return Conflict($"Cannot delete: {deploymentCount} deployment(s) still reference this template.");

        _db.AppTemplates.Remove(template);
        await _db.SaveChangesAsync(ct);
        return NoContent();
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
        if (!_access.IsSystemAdmin) return Forbid();

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
