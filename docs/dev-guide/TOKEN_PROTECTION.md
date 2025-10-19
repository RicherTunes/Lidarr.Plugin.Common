# Token Protection (At-Rest)

This library encrypts token/session data on disk by default.

## Default Behavior

- Windows: DPAPI (CurrentUser) via `System.Security.Cryptography.ProtectedData`.
- macOS: Keychain-backed AES-GCM (stores a 32-byte key as a generic password in the login keychain).
- Linux: Secret Service (libsecret) if `secret-tool` is available; otherwise .NET Data Protection with a file key ring (`~/.config/lidarr.plugin.common/keys`).

The persisted file contains only a small envelope:

```json
{ "v": 2, "alg": "dpapi-user|dataprotection|dataprotection-cert", "payload": "<base64>" }
```

On first read of a legacy (plaintext) file, the store migrates it in-place to the protected format.

## Environment Configuration

- `LP_COMMON_PROTECTOR`: auto | dpapi | dpapi-user | dpapi-machine | keychain | secret-service | dataprotection
- `LP_COMMON_APP_NAME`: logical application name for DP purposes (default: `Lidarr.Plugin.Common`)
- `LP_COMMON_KEYS_PATH`: directory for DataProtection key ring (default varies by OS)
- `LP_COMMON_CERT_PATH` / `LP_COMMON_CERT_PASSWORD`: use an X.509 certificate to protect the DP key ring
- `LP_COMMON_CERT_THUMBPRINT`: alternatively, reference a certificate from the LocalMachine/My store (Windows)
- `LP_COMMON_AKV_KEY_ID` (or `LP_COMMON_KMS_URI`): Azure Key Vault key identifier to wrap the DP key ring (requires .NET 8+). Example: `https://<vault>.vault.azure.net/keys/<keyName>/<version>`

Notes:

- For multi-instance/server scenarios, prefer `dataprotection` with a shared `LP_COMMON_KEYS_PATH` and certificate or Azure Key Vault wrapping.
- On Linux, the token file is written with 0600 permissions when running on .NET 8+.
- Plaintext storage is not supported by default.

## Azure Key Vault (AKV) wrapping (optional)

- Set `LP_COMMON_AKV_KEY_ID` to a Key Vault key id. The library uses DefaultAzureCredential, so Managed Identity, workload identity, or developer credentials can be used.
- .NET 8+ only in this package; older TFMs ignore AKV wiring.

Minimal AKV setup (example):

1. Create a key: `az keyvault key create --vault-name <vault> --name lpc-tokens --protection software`
2. Grant wrap/unwrap to the app: `az keyvault set-policy --name <vault> --spn <clientId> --key-permissions wrapKey unwrapKey get`
3. Export env: `LP_COMMON_AKV_KEY_ID=https://<vault>.vault.azure.net/keys/lpc-tokens/<version>`
