namespace PartnerCenterBridge.Core.Entities;

/// <summary>
/// A reusable Win32 app definition, authored once and deployed to many tenants. Bundles the
/// Intune metadata (install/uninstall/detection/requirements) with a pointer to the current
/// .intunewin content so an "update" is just a new <see cref="ContentVersion"/>.
/// </summary>
public class AppTemplate
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public required string DisplayName { get; set; }
    public string? Description { get; set; }
    public string? Publisher { get; set; }

    /// <summary>Command Intune runs to install, e.g. <c>msiexec /i app.msi /qn</c>.</summary>
    public required string InstallCommandLine { get; set; }
    public required string UninstallCommandLine { get; set; }

    /// <summary>Monotonically increasing local version; bumped whenever the content changes.</summary>
    public int ContentVersion { get; set; } = 1;

    /// <summary>
    /// Detection rules that tell Intune whether the app is already installed. Persisted as JSON.
    /// </summary>
    public List<DetectionRule> DetectionRules { get; set; } = new();

    /// <summary>Default assignment targets applied when the template is deployed. Persisted as JSON.</summary>
    public List<AssignmentSpec> Assignments { get; set; } = new();

    /// <summary>
    /// Encryption + sizing info parsed from the .intunewin Detection.xml, needed at commit time.
    /// Null until a package has been uploaded for this template.
    /// </summary>
    public Win32ContentInfo? Content { get; set; }

    /// <summary>Contract this template belongs to (part of that contract's desired state).</summary>
    public Guid? ContractId { get; set; }
    public Contract? Contract { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>A single Intune detection rule. Fields used depend on <see cref="Type"/>.</summary>
public class DetectionRule
{
    public DetectionRuleType Type { get; set; }

    // MsiProductCode
    public string? ProductCode { get; set; }
    public string? ProductVersion { get; set; }

    // File / Registry
    public string? Path { get; set; }
    public string? FileOrKeyName { get; set; }
    public bool Check32BitOn64System { get; set; }

    // PowerShellScript
    public string? ScriptContent { get; set; }
    public bool EnforceSignatureCheck { get; set; }
    public bool RunAs32Bit { get; set; }
}

/// <summary>A desired assignment: a target audience and the intent to apply to it.</summary>
public class AssignmentSpec
{
    public AssignmentTargetType TargetType { get; set; }
    public InstallIntent Intent { get; set; }

    /// <summary>Entra group id when <see cref="TargetType"/> is <see cref="AssignmentTargetType.Group"/>.</summary>
    public string? GroupId { get; set; }
    /// <summary>Optional group display name used to resolve/create the group per tenant.</summary>
    public string? GroupDisplayName { get; set; }
}

/// <summary>
/// Content descriptor for the current .intunewin payload of a template. The encryption values
/// come straight from the package's Detection.xml and are echoed back to Graph at commit time.
/// </summary>
public class Win32ContentInfo
{
    /// <summary>Original setup file name inside the package (e.g. <c>app.msi</c>).</summary>
    public required string FileName { get; set; }

    /// <summary>Unencrypted payload size in bytes.</summary>
    public long Size { get; set; }
    /// <summary>Encrypted payload size in bytes (what is actually uploaded to Azure Blob).</summary>
    public long SizeEncrypted { get; set; }

    // Values from EncryptionInfo in Detection.xml (base64).
    public required string EncryptionKey { get; set; }
    public required string MacKey { get; set; }
    public required string InitializationVector { get; set; }
    public required string Mac { get; set; }
    public required string ProfileIdentifier { get; set; }
    public required string FileDigest { get; set; }
    public required string FileDigestAlgorithm { get; set; }

    /// <summary>Where the extracted encrypted payload is staged for upload (blob key / path).</summary>
    public string? StagedPayloadRef { get; set; }
}
