using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PartnerCenterBridge.Api.Contracts;
using PartnerCenterBridge.Core.Entities;
using PartnerCenterBridge.Data;

namespace PartnerCenterBridge.Api.Controllers;

/// <summary>Read/upsert the per-contract new-hire provisioning defaults.</summary>
[ApiController]
[Route("api/contracts/{contractId:guid}/provisioning-template")]
[Authorize]
public class ProvisioningTemplatesController : ControllerBase
{
    private readonly BridgeDbContext _db;

    public ProvisioningTemplatesController(BridgeDbContext db) => _db = db;

    [HttpGet]
    public async Task<ActionResult<ProvisioningTemplateDto>> Get(Guid contractId, CancellationToken ct)
    {
        var template = await _db.ProvisioningTemplates.FirstOrDefaultAsync(t => t.ContractId == contractId, ct);
        return template is null ? NoContent() : Ok(ProvisioningTemplateDto.From(template));
    }

    [HttpPut]
    public async Task<ActionResult<ProvisioningTemplateDto>> Upsert(
        Guid contractId, UpsertProvisioningTemplateRequest req, CancellationToken ct)
    {
        if (!await _db.Contracts.AnyAsync(c => c.Id == contractId, ct)) return NotFound("Contract not found.");

        var template = await _db.ProvisioningTemplates.FirstOrDefaultAsync(t => t.ContractId == contractId, ct);
        if (template is null)
        {
            template = new ProvisioningTemplate { ContractId = contractId };
            _db.ProvisioningTemplates.Add(template);
        }
        template.UsageLocation = req.UsageLocation;
        template.UpnDomain = req.UpnDomain;
        template.DefaultJobTitle = req.DefaultJobTitle;
        template.DefaultDepartment = req.DefaultDepartment;
        template.LicenseSkuIds = req.LicenseSkuIds ?? new();
        template.GroupIds = req.GroupIds ?? new();
        template.UpdatedAt = DateTimeOffset.UtcNow;

        await _db.SaveChangesAsync(ct);
        return Ok(ProvisioningTemplateDto.From(template));
    }
}
