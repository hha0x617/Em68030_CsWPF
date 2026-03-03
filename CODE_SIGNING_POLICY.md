# Code Signing Policy

## Signing Provider

Release builds of this project are code-signed by
**[SignPath Foundation](https://signpath.org)**, a free code signing service
for open source projects.

## Privacy Statement

The code signing process is handled entirely by SignPath Foundation's
infrastructure. During the signing process:

- Build artifacts (compiled binaries) are uploaded to SignPath's servers for
  signing and are deleted after the signing process completes.
- No personal data from end users of this software is collected or transmitted
  during the signing process.
- GitHub Actions workflow metadata (repository name, commit hash, workflow run
  ID) is sent to SignPath to verify the origin of the artifacts.

For full details, see [SignPath Foundation's privacy policy](https://signpath.org/privacy-policy).

## Signature Verification

You can verify the digital signature of the released binaries on Windows:

1. Right-click the `.exe` file and select **Properties**.
2. Go to the **Digital Signatures** tab.
3. Select the signature entry and click **Details**.
4. The dialog shows the signer name, timestamp, and whether the signature is
   valid.

Alternatively, using PowerShell:

```powershell
Get-AuthenticodeSignature .\Em68030.exe | Format-List
```

A valid signature confirms that the binary was built from this repository's
source code and has not been tampered with since signing.
