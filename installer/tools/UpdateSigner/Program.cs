using Coworker.UpdateSigner;

// Windows twin of Sparkle's generate_keys / sign_update for the Coworker update feed.
//
//   UpdateSigner keygen [--out <dir>] [--name <file-stem>]     new key pair, private key written OUTSIDE the repo
//   UpdateSigner public-key [--key-file <path>]                 print the public key of a private key
//   UpdateSigner sign <file> [--key-file <path>]                print: sparkle:edSignature="..." length="..."
//   UpdateSigner sign <file> --raw                              print only the base64 signature
//   UpdateSigner verify <file> --signature <b64> (--public-key <b64> | --public-key-file <path>)
//
// The private key is read from --key-file, else $COWORKER_UPDATE_PRIVATE_KEY_FILE, else
// $COWORKER_UPDATE_PRIVATE_KEY (base64 seed, for CI secrets), else the default key file below.
// It is never printed and never written inside a repository.

const string DefaultName = "coworker-windows-update";

static string DefaultKeyDirectory() =>
    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Coworker", "update-signing");

static string? Option(string[] args, string name)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

static string LoadPrivateKey(string[] args)
{
    var file = Option(args, "--key-file")
               ?? Environment.GetEnvironmentVariable("COWORKER_UPDATE_PRIVATE_KEY_FILE");
    if (!string.IsNullOrWhiteSpace(file))
    {
        if (!File.Exists(file)) throw new FileNotFoundException($"Private key file not found: {file}");
        return File.ReadAllText(file).Trim();
    }

    var inline = Environment.GetEnvironmentVariable("COWORKER_UPDATE_PRIVATE_KEY");
    if (!string.IsNullOrWhiteSpace(inline)) return inline.Trim();

    var fallback = Path.Combine(DefaultKeyDirectory(), DefaultName + ".key");
    if (File.Exists(fallback)) return File.ReadAllText(fallback).Trim();

    throw new InvalidOperationException(
        "No private key. Run `UpdateSigner keygen`, or set COWORKER_UPDATE_PRIVATE_KEY_FILE / COWORKER_UPDATE_PRIVATE_KEY.");
}

static bool InsideGitWorkTree(string directory)
{
    for (var dir = new DirectoryInfo(Path.GetFullPath(directory)); dir != null; dir = dir.Parent)
        if (Directory.Exists(Path.Combine(dir.FullName, ".git")) || File.Exists(Path.Combine(dir.FullName, ".git"))) return true;
    return false;
}

try
{
    var command = args.FirstOrDefault()?.ToLowerInvariant();
    switch (command)
    {
        case "keygen":
        {
            var directory = Option(args, "--out") ?? DefaultKeyDirectory();
            var name = Option(args, "--name") ?? DefaultName;
            if (InsideGitWorkTree(directory))
            {
                Console.Error.WriteLine($"Refusing to create a private key inside a git work tree ({directory}). Choose a folder outside the repository.");
                return 2;
            }

            Directory.CreateDirectory(directory);
            var privatePath = Path.Combine(directory, name + ".key");
            var publicPath = Path.Combine(directory, name + ".pub");
            if (File.Exists(privatePath))
            {
                Console.Error.WriteLine($"{privatePath} already exists; refusing to overwrite a signing key. Move it away first if you really want a new one.");
                return 2;
            }

            var (privateKey, publicKey) = Ed25519Tool.GenerateKeyPair();
            File.WriteAllText(privatePath, privateKey + Environment.NewLine);
            File.WriteAllText(publicPath, publicKey + Environment.NewLine);
            Console.WriteLine($"Private key: {privatePath}   (keep secret, back it up; losing it strands every installed copy)");
            Console.WriteLine($"Public key : {publicPath}");
            Console.WriteLine();
            Console.WriteLine("Put this public key in Coworker.csproj as <UpdatePublicKey> (or pass -p:UpdatePublicKey=... to dotnet publish); Debug builds also read COWORKER_UPDATE_PUBLIC_KEY:");
            Console.WriteLine(publicKey);
            return 0;
        }

        case "public-key":
            Console.WriteLine(Ed25519Tool.PublicKeyFromPrivate(LoadPrivateKey(args)));
            return 0;

        case "sign":
        {
            var path = args.Length > 1 ? args[1] : throw new ArgumentException("sign needs a file path.");
            if (!File.Exists(path)) throw new FileNotFoundException($"File not found: {path}");
            var signature = Ed25519Tool.SignFile(path, LoadPrivateKey(args));
            Console.WriteLine(args.Contains("--raw")
                ? signature
                : $"sparkle:edSignature=\"{signature}\" length=\"{new FileInfo(path).Length}\"");
            return 0;
        }

        case "verify":
        {
            var path = args.Length > 1 ? args[1] : throw new ArgumentException("verify needs a file path.");
            var signature = Option(args, "--signature") ?? throw new ArgumentException("verify needs --signature.");
            var publicKey = Option(args, "--public-key")
                            ?? (Option(args, "--public-key-file") is { } keyFile ? File.ReadAllText(keyFile) : null)
                            ?? throw new ArgumentException("verify needs --public-key or --public-key-file.");
            var valid = Ed25519Tool.VerifyFile(path, signature, publicKey);
            Console.WriteLine(valid ? "valid" : "INVALID");
            return valid ? 0 : 1;
        }

        default:
            Console.Error.WriteLine("Usage: UpdateSigner keygen | public-key | sign <file> | verify <file> --signature <b64> --public-key <b64>");
            return 64;
    }
}
catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}
