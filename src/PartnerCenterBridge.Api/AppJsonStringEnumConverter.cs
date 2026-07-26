using System.Text.Json;
using System.Text.Json.Serialization;

namespace PartnerCenterBridge.Api;

/// <summary>
/// Wraps <see cref="JsonStringEnumConverter"/> but steps aside for Fido2NetLib's enums. Those
/// carry their own type-level <c>[JsonConverter]</c> mapping to WebAuthn-spec wire values via
/// <c>[EnumMember]</c> (e.g. <c>PublicKeyCredentialType.PublicKey</c> -&gt; <c>"public-key"</c>).
/// Entries in <c>JsonSerializerOptions.Converters</c> are checked before an enum's own type-level
/// attribute, so registering a blanket <see cref="JsonStringEnumConverter"/> globally silently
/// produced <c>"PublicKey"</c>/<c>"Required"</c> instead of the spec's lowercase-hyphenated
/// values -- valid-looking JSON that every real browser WebAuthn call would have rejected.
/// </summary>
public class AppJsonStringEnumConverter : JsonConverterFactory
{
    private static readonly JsonStringEnumConverter Inner = new();

    public override bool CanConvert(Type typeToConvert) =>
        typeToConvert.IsEnum && typeToConvert.Namespace?.StartsWith("Fido2NetLib") != true;

    public override JsonConverter? CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
        Inner.CreateConverter(typeToConvert, options);
}
