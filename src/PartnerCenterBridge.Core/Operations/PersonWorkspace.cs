using PartnerCenterBridge.Core.Entities;

namespace PartnerCenterBridge.Core.Operations;

public enum SectionStatus { Ok, Unavailable, Error }

/// <summary>
/// One independently loaded section of the person workspace. <see cref="SectionStatus.Unavailable"/>
/// means PCB cannot read this here (missing permission, dependency not configured) and says why;
/// <see cref="SectionStatus.Error"/> means the read was attempted and failed.
/// </summary>
public class PersonSection<T>
{
    public SectionStatus Status { get; set; }
    public string? Reason { get; set; }
    public T? Data { get; set; }

    public static PersonSection<T> Ok(T data, string? reason = null) => new() { Status = SectionStatus.Ok, Data = data, Reason = reason };
    public static PersonSection<T> Unavailable(string reason) => new() { Status = SectionStatus.Unavailable, Reason = reason };
    public static PersonSection<T> Error(string reason) => new() { Status = SectionStatus.Error, Reason = reason };
}

/// <param name="LastSignIn">Last interactive sign-in, when readable (needs AuditLog.Read.All and Entra ID P1); null otherwise.</param>
public record PersonProfile(
    string Id, string DisplayName, string? Upn, string? Mail, bool? AccountEnabled,
    string? JobTitle, string? Department, bool? OnPremisesSyncEnabled, string? CreatedDateTime,
    string? LastSignIn);

public record PersonLicense(string SkuPartNumber, string SkuId);

/// <summary>A direct membership; <c>category</c> uses the Access Parity categories.</summary>
public record PersonGroup(string Id, string DisplayName, string Category);

public record PersonDevice(
    string Id, string? DeviceName, string? OperatingSystem, string? OsVersion,
    string? ComplianceState, string? LastSyncDateTime, string? ManagementAgent);

/// <summary>Read-only Graph lookups behind the person workspace. Each call degrades independently.</summary>
public interface IPersonDirectoryReader
{
    Task<PersonSection<PersonProfile>> GetProfileAsync(Tenant tenant, string userId, CancellationToken ct = default);
    Task<PersonSection<IReadOnlyList<PersonLicense>>> GetLicensesAsync(Tenant tenant, string userId, CancellationToken ct = default);
    Task<PersonSection<IReadOnlyList<PersonGroup>>> GetGroupsAsync(Tenant tenant, string userId, CancellationToken ct = default);
    Task<PersonSection<IReadOnlyList<string>>> GetAuthMethodsAsync(Tenant tenant, string userId, CancellationToken ct = default);
    Task<PersonSection<IReadOnlyList<PersonDevice>>> GetDevicesAsync(Tenant tenant, string userId, CancellationToken ct = default);
}

/// <summary>Whether Exchange Online operations can run at all on this PCB instance, and if not, what is missing.</summary>
public record ExchangeCapabilityStatus(bool Available, string? MissingDependency);

public interface IExchangeCapability
{
    ExchangeCapabilityStatus Check();
}
