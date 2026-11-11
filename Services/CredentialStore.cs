using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Coworker.Helpers;

namespace Coworker.Services;

/// <summary>
/// Secrets (session token, API keys) encrypted with DPAPI for the current Windows user, so they
/// are unreadable from another account or a copied settings folder. Mac equivalent: Keychain
/// (SessionCredentialStore in Constants.swift). Credential Manager was rejected: its 2,560-byte
/// blob limit can be smaller than a JWT.
/// </summary>
public sealed class CredentialStore
{
    public static CredentialStore Shared { get; } = new();

    public const string SessionAccessToken = "humanlike.coworker.session/access_token";
    public const string OpenRouterApiKey = "humanlike.coworker.openrouter/api-key";

    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("humanlike.coworker.secrets.v1");

    private readonly string _path = AppPaths.GetPath("secrets.dat");
    private readonly object _gate = new();
    private Dictionary<string, string>? _cache;

    private CredentialStore() { }

    public string? Get(string key)
    {
        lock (_gate)
        {
            return Load().TryGetValue(key, out var value) ? value : null;
        }
    }

    /// Stores (or, for null/blank, removes) a secret. Returns false if it could not be persisted;
    /// the value is still kept in memory for this run.
    public bool Set(string key, string? value)
    {
        var trimmed = value?.Trim();
        lock (_gate)
        {
            var secrets = Load();
            if (string.IsNullOrEmpty(trimmed))
            {
                if (!secrets.Remove(key)) return true;
            }
            else
            {
                secrets[key] = trimmed;
            }
            return Persist(secrets);
        }
    }

    private Dictionary<string, string> Load()
    {
        if (_cache != null) return _cache;

        _cache = new Dictionary<string, string>();
        try
        {
            if (!File.Exists(_path)) return _cache;
            var plain = Unprotect(File.ReadAllBytes(_path));
            _cache = JsonSerializer.Deserialize<Dictionary<string, string>>(plain) ?? new();
        }
        catch (Exception ex)
        {
            // Wrong user/machine or a damaged file: treat as signed out rather than crash.
            DebugLog.Write($"CredentialStore: unreadable secrets file ({ex.GetType().Name}: {ex.Message})");
        }
        return _cache;
    }

    private bool Persist(Dictionary<string, string> secrets)
    {
        try
        {
            var protectedBytes = Protect(JsonSerializer.SerializeToUtf8Bytes(secrets));
            var tmp = _path + ".tmp";
            File.WriteAllBytes(tmp, protectedBytes);
            if (File.Exists(_path)) File.Replace(tmp, _path, null);
            else File.Move(tmp, _path);
            return true;
        }
        catch (Exception ex)
        {
            DebugLog.Write($"CredentialStore: failed to persist secrets ({ex.GetType().Name}: {ex.Message})");
            return false;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int cbData;
        public IntPtr pbData;
    }

    private const uint CRYPTPROTECT_UI_FORBIDDEN = 0x1;

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref DataBlob dataIn, string? description, ref DataBlob entropy,
        IntPtr reserved, IntPtr prompt, uint flags, out DataBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(ref DataBlob dataIn, IntPtr description, ref DataBlob entropy,
        IntPtr reserved, IntPtr prompt, uint flags, out DataBlob dataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);

    private static byte[] Protect(byte[] plain) => Transform(plain, protect: true);
    private static byte[] Unprotect(byte[] cipher) => Transform(cipher, protect: false);

    private static byte[] Transform(byte[] input, bool protect)
    {
        var inHandle = GCHandle.Alloc(input, GCHandleType.Pinned);
        var entropyHandle = GCHandle.Alloc(Entropy, GCHandleType.Pinned);
        try
        {
            var dataIn = new DataBlob { cbData = input.Length, pbData = inHandle.AddrOfPinnedObject() };
            var entropy = new DataBlob { cbData = Entropy.Length, pbData = entropyHandle.AddrOfPinnedObject() };
            DataBlob output;
            var ok = protect
                ? CryptProtectData(ref dataIn, null, ref entropy, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, out output)
                : CryptUnprotectData(ref dataIn, IntPtr.Zero, ref entropy, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, out output);
            if (!ok) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());

            try
            {
                var result = new byte[output.cbData];
                Marshal.Copy(output.pbData, result, 0, result.Length);
                return result;
            }
            finally
            {
                LocalFree(output.pbData);
            }
        }
        finally
        {
            inHandle.Free();
            entropyHandle.Free();
        }
    }
}
