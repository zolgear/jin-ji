// パスキー（WebAuthn）。ブラウザ標準の API（navigator.credentials）だけを使い、ライブラリは入れない。
// サーバー（ASP.NET Core Identity の IPasskeyHandler）は WebAuthn の JSON 形式（base64url）で options を返し、
// 結果も同じ JSON 形式（PublicKeyCredential.toJSON の形）で受け取る。
// 標準の変換（parseCreationOptionsFromJSON / parseRequestOptionsFromJSON / toJSON）が無いブラウザのために、同じ変換を自前でも持つ。

/** base64url（パディングなし）→ バイト列。 */
export function fromBase64Url(value: string): ArrayBuffer {
  const base64 = value.replace(/-/g, "+").replace(/_/g, "/").padEnd(Math.ceil(value.length / 4) * 4, "=");
  const binary = atob(base64);
  const bytes = new Uint8Array(binary.length);
  for (let i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i);
  return bytes.buffer;
}

/** バイト列 → base64url（パディングなし）。 */
export function toBase64Url(buffer: ArrayBuffer | ArrayBufferView): string {
  const bytes = buffer instanceof ArrayBuffer ? new Uint8Array(buffer) : new Uint8Array(buffer.buffer, buffer.byteOffset, buffer.byteLength);
  let binary = "";
  for (const b of bytes) binary += String.fromCharCode(b);
  return btoa(binary).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
}

type Json = Record<string, unknown>;

function credentialDescriptors(list: unknown): PublicKeyCredentialDescriptor[] | undefined {
  if (!Array.isArray(list)) return undefined;
  return list.map((d: Json) => ({ ...d, id: fromBase64Url(String(d.id)) }) as PublicKeyCredentialDescriptor);
}

/** 登録の options（JSON）→ navigator.credentials.create に渡す形。 */
export function parseCreationOptions(json: Json): PublicKeyCredentialCreationOptions {
  const user = json.user as Json;
  return {
    ...json,
    challenge: fromBase64Url(String(json.challenge)),
    user: { ...user, id: fromBase64Url(String(user.id)) },
    excludeCredentials: credentialDescriptors(json.excludeCredentials),
  } as PublicKeyCredentialCreationOptions;
}

/** ログインの options（JSON）→ navigator.credentials.get に渡す形。 */
export function parseRequestOptions(json: Json): PublicKeyCredentialRequestOptions {
  return {
    ...json,
    challenge: fromBase64Url(String(json.challenge)),
    allowCredentials: credentialDescriptors(json.allowCredentials),
  } as PublicKeyCredentialRequestOptions;
}

/** 資格情報 → サーバーに送る JSON（PublicKeyCredential.toJSON と同じ形）。 */
export function credentialToJson(credential: PublicKeyCredential): Json {
  // 型の上では toJSON は常にあるが、古いブラウザには無い
  const withJson = credential as unknown as { toJSON?: () => Json };
  if (typeof withJson.toJSON === "function") {
    return withJson.toJSON.call(credential);
  }
  const r = credential.response;
  const response: Json = { clientDataJSON: toBase64Url(r.clientDataJSON) };
  if ("attestationObject" in r) {
    const a = r as AuthenticatorAttestationResponse;
    response.attestationObject = toBase64Url(a.attestationObject);
    response.transports = typeof a.getTransports === "function" ? a.getTransports() : [];
    const authData = typeof a.getAuthenticatorData === "function" ? a.getAuthenticatorData() : null;
    if (authData) response.authenticatorData = toBase64Url(authData);
    const publicKey = typeof a.getPublicKey === "function" ? a.getPublicKey() : null;
    if (publicKey) response.publicKey = toBase64Url(publicKey);
    if (typeof a.getPublicKeyAlgorithm === "function") response.publicKeyAlgorithm = a.getPublicKeyAlgorithm();
  } else {
    const g = r as AuthenticatorAssertionResponse;
    response.authenticatorData = toBase64Url(g.authenticatorData);
    response.signature = toBase64Url(g.signature);
    if (g.userHandle) response.userHandle = toBase64Url(g.userHandle);
  }
  return {
    id: credential.id,
    rawId: toBase64Url(credential.rawId),
    type: credential.type,
    authenticatorAttachment: credential.authenticatorAttachment ?? undefined,
    clientExtensionResults: credential.getClientExtensionResults(),
    response,
  };
}

/** このブラウザでパスキーを使えるか。 */
export function webAuthnSupported(): boolean {
  return typeof window !== "undefined" && typeof window.PublicKeyCredential === "function" && !!navigator.credentials;
}

type PkcStatic = typeof PublicKeyCredential & {
  parseCreationOptionsFromJSON?: (json: unknown) => PublicKeyCredentialCreationOptions;
  parseRequestOptionsFromJSON?: (json: unknown) => PublicKeyCredentialRequestOptions;
};

/** パスキーを作る（登録）。結果はサーバーに送る JSON。 */
export async function createPasskey(optionsJson: Json, signal?: AbortSignal): Promise<Json> {
  const pkc = window.PublicKeyCredential as PkcStatic;
  const publicKey = pkc.parseCreationOptionsFromJSON ? pkc.parseCreationOptionsFromJSON(optionsJson) : parseCreationOptions(optionsJson);
  const credential = (await navigator.credentials.create({ publicKey, signal })) as PublicKeyCredential | null;
  if (!credential) throw new DOMException("パスキーが作られませんでした", "NotAllowedError");
  return credentialToJson(credential);
}

/** パスキーで署名する（ログイン）。結果はサーバーに送る JSON。 */
export async function getPasskey(optionsJson: Json, signal?: AbortSignal): Promise<Json> {
  const pkc = window.PublicKeyCredential as PkcStatic;
  const publicKey = pkc.parseRequestOptionsFromJSON ? pkc.parseRequestOptionsFromJSON(optionsJson) : parseRequestOptions(optionsJson);
  const credential = (await navigator.credentials.get({ publicKey, signal })) as PublicKeyCredential | null;
  if (!credential) throw new DOMException("パスキーが選ばれませんでした", "NotAllowedError");
  return credentialToJson(credential);
}
