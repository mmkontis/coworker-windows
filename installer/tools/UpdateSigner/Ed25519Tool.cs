using System.Security.Cryptography;
using Chaos.NaCl;

namespace Coworker.UpdateSigner;

/// <summary>
/// Ed25519 signing for update installers, byte-compatible with Sparkle's sign_update and with
/// NetSparkle's Ed25519Checker: the private key is the base64 of the 32-byte seed, the public key
/// the base64 of the 32-byte key, the signature the base64 of the 64-byte signature over the raw file.
/// </summary>
public static class Ed25519Tool
{
    public static (string PrivateKey, string PublicKey) GenerateKeyPair()
    {
        var seed = RandomNumberGenerator.GetBytes(Ed25519.PrivateKeySeedSizeInBytes);
        return (Convert.ToBase64String(seed), Convert.ToBase64String(Ed25519.PublicKeyFromSeed(seed)));
    }

    public static string PublicKeyFromPrivate(string privateKeyBase64) =>
        Convert.ToBase64String(Ed25519.PublicKeyFromSeed(ParseSeed(privateKeyBase64)));

    public static string SignFile(string path, string privateKeyBase64)
    {
        var expanded = Ed25519.ExpandedPrivateKeyFromSeed(ParseSeed(privateKeyBase64));
        return Convert.ToBase64String(Ed25519.Sign(File.ReadAllBytes(path), expanded));
    }

    public static bool VerifyFile(string path, string signatureBase64, string publicKeyBase64)
    {
        try
        {
            var signature = Convert.FromBase64String(signatureBase64.Trim());
            var publicKey = Convert.FromBase64String(publicKeyBase64.Trim());
            if (signature.Length != Ed25519.SignatureSizeInBytes || publicKey.Length != Ed25519.PublicKeySizeInBytes) return false;
            return Ed25519.Verify(signature, File.ReadAllBytes(path), publicKey);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static byte[] ParseSeed(string privateKeyBase64)
    {
        byte[] bytes;
        try { bytes = Convert.FromBase64String(privateKeyBase64.Trim()); }
        catch (FormatException) { throw new ArgumentException("The private key is not valid base64."); }

        // Sparkle's exported private key is the 32-byte seed.
        if (bytes.Length != Ed25519.PrivateKeySeedSizeInBytes)
            throw new ArgumentException($"The private key must decode to a 32-byte seed (got {bytes.Length} bytes).");
        return bytes;
    }
}
