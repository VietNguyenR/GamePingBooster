using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace GamePingBooster.Service.Update;

/// <summary>
/// "Is this file signed, by a certificate Windows trusts, and is that certificate OURS" - asked of a
/// setup .exe before the service runs it as SYSTEM.
///
/// The second of two independent checks. The first is the SHA-256 GitHub computed when the release was
/// uploaded (InstallerDownload): it proves the file is the one on the release page, but only as far as
/// the API answer it came in can be trusted. This one proves the file was built and signed by the
/// release workflow, whatever served it - forging both needs the GitHub account AND the Certum signing
/// login.
///
/// Revocation is not checked online: a café line that cannot reach Certum's CRL server would otherwise
/// stall or refuse every update, and the pin below already narrows "trusted" to one certificate.
/// </summary>
internal static partial class Authenticode
{
    /// <summary>
    /// SHA-1 thumbprints of the certificates release builds are signed with - the same value as
    /// CERTUM_CERT_SHA1 in .github/workflows/release.yml (CN=Tien Viet Nguyen, valid to 2027-10-11).
    ///
    /// RENEWING THE CERTIFICATE: add the new thumbprint here and ship that in a release signed with the OLD
    /// one, before the new one signs anything. A release signed by a certificate missing from this list is
    /// refused by every installed service, which then falls back to the app's own update window (UAC) - so
    /// forgetting costs one manual update per player, never a broken installation.
    /// </summary>
    internal static readonly string[] TrustedSigners =
    [
        "04A7242EFFEDCAD52742F63F95C04769B7C692E7",
    ];

    /// <summary>Null when the file is signed by one of <see cref="TrustedSigners"/> and Windows trusts the signature; otherwise why not.</summary>
    public static string? Check(string path)
    {
        var trust = Verify(path);
        if (trust != 0) return $"the signature is not valid (WinVerifyTrust 0x{trust:X8})";

        string thumbprint;
        try
        {
#pragma warning disable SYSLIB0057 // The signer of an Authenticode signature; no replacement API reads it.
            using var signer = X509Certificate.CreateFromSignedFile(path);
#pragma warning restore SYSLIB0057
            thumbprint = signer.GetCertHashString();
        }
        catch (Exception ex)
        {
            return $"the signer could not be read ({ex.Message})";
        }

        return TrustedSigners.Contains(thumbprint, StringComparer.OrdinalIgnoreCase)
            ? null
            : $"it is signed by an unknown certificate ({thumbprint})";
    }

    private static unsafe int Verify(string path)
    {
        var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE"); // WINTRUST_ACTION_GENERIC_VERIFY_V2
        var pathPtr = Marshal.StringToHGlobalUni(path);
        try
        {
            var file = new WintrustFileInfo
            {
                cbStruct = (uint)sizeof(WintrustFileInfo),
                pcwszFilePath = pathPtr,
            };
            var data = new WintrustData
            {
                cbStruct = (uint)sizeof(WintrustData),
                dwUIChoice = 2,             // WTD_UI_NONE: a service has nobody to show a dialog to
                fdwRevocationChecks = 0,    // WTD_REVOKE_NONE - see the class summary
                dwUnionChoice = 1,          // WTD_CHOICE_FILE
                pFile = (nint)(&file),
                dwStateAction = 0,          // WTD_STATEACTION_IGNORE: nothing kept, nothing to close
                dwProvFlags = 0x10 | 0x1000, // WTD_REVOCATION_CHECK_NONE | WTD_CACHE_ONLY_URL_RETRIEVAL
            };
            return WinVerifyTrust(-1, &action, &data);
        }
        finally
        {
            Marshal.FreeHGlobal(pathPtr);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WintrustFileInfo
    {
        public uint cbStruct;
        public nint pcwszFilePath;
        public nint hFile;
        public nint pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WintrustData
    {
        public uint cbStruct;
        public nint pPolicyCallbackData;
        public nint pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public nint pFile;
        public uint dwStateAction;
        public nint hWVTStateData;
        public nint pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public nint pSignatureSettings;
    }

    [LibraryImport("wintrust.dll")]
    private static unsafe partial int WinVerifyTrust(nint hwnd, Guid* action, WintrustData* data);
}
