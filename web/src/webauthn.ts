// Browser-side WebAuthn glue. The backend (Fido2NetLib) serializes every credential/challenge
// byte field as base64url with WebAuthn-spec-matching camelCase property names, so these options
// objects are shaped like real PublicKeyCredentialCreationOptions/RequestOptions already -- only
// the byte fields (challenge, user.id, credential ids) need base64url <-> ArrayBuffer conversion.

function base64UrlToBytes(b64url: string): Uint8Array<ArrayBuffer> {
  const b64 = b64url.replace(/-/g, "+").replace(/_/g, "/").padEnd(Math.ceil(b64url.length / 4) * 4, "=");
  const bin = atob(b64);
  const bytes = new Uint8Array(bin.length);
  for (let i = 0; i < bin.length; i++) bytes[i] = bin.charCodeAt(i);
  return bytes;
}

function bytesToBase64Url(bytes: ArrayBuffer): string {
  let bin = "";
  new Uint8Array(bytes).forEach((b) => (bin += String.fromCharCode(b)));
  return btoa(bin).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
}

interface RawDescriptor { id: string; type: string; transports?: string[] }

function toCredentialDescriptor(d: RawDescriptor): PublicKeyCredentialDescriptor {
  return { id: base64UrlToBytes(d.id), type: "public-key", transports: d.transports as AuthenticatorTransport[] | undefined };
}

export interface RegisterOptionsWire {
  rp: { id: string; name: string };
  user: { id: string; name: string; displayName: string };
  challenge: string;
  pubKeyCredParams: PublicKeyCredentialParameters[];
  timeout?: number;
  attestation?: AttestationConveyancePreference;
  excludeCredentials?: RawDescriptor[];
  authenticatorSelection?: AuthenticatorSelectionCriteria;
}

/** Runs the create-passkey ceremony and returns a body shaped exactly like PasskeyRegisterVerifyRequest.attestationResponse. */
export async function createPasskey(options: RegisterOptionsWire) {
  const publicKey: PublicKeyCredentialCreationOptions = {
    ...options,
    challenge: base64UrlToBytes(options.challenge),
    user: { ...options.user, id: base64UrlToBytes(options.user.id) },
    excludeCredentials: (options.excludeCredentials ?? []).map(toCredentialDescriptor)
  };

  const cred = (await navigator.credentials.create({ publicKey })) as PublicKeyCredential | null;
  if (!cred) throw new Error("Passkey creation was cancelled.");
  const response = cred.response as AuthenticatorAttestationResponse;

  return {
    id: cred.id,
    rawId: bytesToBase64Url(cred.rawId),
    type: cred.type,
    clientExtensionResults: cred.getClientExtensionResults() as Record<string, unknown>,
    response: {
      attestationObject: bytesToBase64Url(response.attestationObject),
      clientDataJSON: bytesToBase64Url(response.clientDataJSON),
      transports: response.getTransports()
    }
  };
}

export interface LoginOptionsWire {
  challenge: string;
  timeout?: number;
  rpId?: string;
  allowCredentials?: RawDescriptor[];
  userVerification?: UserVerificationRequirement;
}

/**
 * Runs the usernameless get-passkey ceremony and returns a body shaped exactly like
 * PasskeyLoginVerifyRequest.assertionResponse. `mediation: "conditional"` + `signal` lets a caller
 * run this as a silent, backgrounded request tied to the browser's native autofill UI (see
 * `conditionalMediationSupported` below) instead of the default explicit, user-initiated ceremony.
 */
export async function getPasskey(
  options: LoginOptionsWire,
  init?: { mediation?: CredentialMediationRequirement; signal?: AbortSignal }
) {
  const publicKey: PublicKeyCredentialRequestOptions = {
    ...options,
    challenge: base64UrlToBytes(options.challenge),
    allowCredentials: (options.allowCredentials ?? []).map(toCredentialDescriptor)
  };

  const cred = (await navigator.credentials.get({
    publicKey,
    mediation: init?.mediation ?? "optional",
    signal: init?.signal
  })) as PublicKeyCredential | null;
  if (!cred) throw new Error("No passkey was selected.");
  const response = cred.response as AuthenticatorAssertionResponse;

  return {
    id: cred.id,
    rawId: bytesToBase64Url(cred.rawId),
    type: cred.type,
    clientExtensionResults: cred.getClientExtensionResults() as Record<string, unknown>,
    response: {
      authenticatorData: bytesToBase64Url(response.authenticatorData),
      clientDataJSON: bytesToBase64Url(response.clientDataJSON),
      signature: bytesToBase64Url(response.signature),
      userHandle: response.userHandle ? bytesToBase64Url(response.userHandle) : null
    }
  };
}

/** Whether this browser can do WebAuthn at all -- gates whether the "Sign in with a passkey" button renders. */
export const passkeysSupported =
  typeof window !== "undefined" && !!window.PublicKeyCredential;

/**
 * Whether this browser supports WebAuthn conditional UI: matching passkeys surfaced directly in
 * the native autofill dropdown for a field marked `autoComplete="webauthn"`, with no explicit
 * button needed. Async per spec (backed by a platform authenticator capability check) -- callers
 * should await this before starting a `mediation: "conditional"` request, not just check
 * `passkeysSupported`, since a browser can support WebAuthn without supporting this specific mode.
 */
export async function conditionalMediationSupported(): Promise<boolean> {
  if (!passkeysSupported || typeof PublicKeyCredential.isConditionalMediationAvailable !== "function") return false;
  return PublicKeyCredential.isConditionalMediationAvailable();
}
