using Coworker.Services.Updates;
using Coworker.UpdateSigner;
using NetSparkleUpdater.Enums;
using Xunit;

namespace Coworker.UpdateTests;

public sealed class FeedRulesTests
{
    [Theory]
    [InlineData("1.10", "1.9", 1)]
    [InlineData("1.9", "1.10", -1)]
    [InlineData("2", "10", -1)]
    [InlineData("10", "9", 1)]
    [InlineData("1.2.0", "1.2.0", 0)]
    [InlineData("1.02", "1.2", 0)]
    [InlineData("1.2.1", "1.2", 1)]
    [InlineData("", "1", -1)]
    public void Compares_versions_the_way_foundation_numeric_compare_does(string left, string right, int expectedSign)
    {
        Assert.Equal(expectedSign, Math.Sign(FeedRules.CompareNumeric(left, right)));
    }

    [Fact]
    public void Prefers_the_local_feed_only_when_it_is_strictly_newer()
    {
        Assert.Equal(FeedRules.FeedChoice.Local, FeedRules.ChooseFeed("8", "7"));
        Assert.Equal(FeedRules.FeedChoice.Production, FeedRules.ChooseFeed("7", "7"));
        Assert.Equal(FeedRules.FeedChoice.Production, FeedRules.ChooseFeed("6", "7"));
        Assert.Equal(FeedRules.FeedChoice.Local, FeedRules.ChooseFeed("1", null));      // production unreachable
        Assert.Equal(FeedRules.FeedChoice.Production, FeedRules.ChooseFeed(null, "7")); // localhost not running
        Assert.Equal(FeedRules.FeedChoice.Production, FeedRules.ChooseFeed(null, null));
    }

    [Fact]
    public void Reads_the_newest_windows_build_from_a_feed()
    {
        var xml = Feed.Build(
            new FeedItem("1.1.0", "6", "https://x/a.exe", "sig", 1),
            new FeedItem("9.9", "99", "https://x/a.dmg", "sig", 1, Os: "macos"),
            new FeedItem("1.2.0", "10", "https://x/b.exe", "sig", 1));
        Assert.Equal("10", FeedRules.LatestBuild(xml));          // numeric, and the macOS item is not counted
        Assert.Null(FeedRules.LatestBuild("not xml"));
        Assert.Null(FeedRules.LatestBuild("<rss><channel></channel></rss>"));
    }

    [Fact]
    public void Finds_builds_tagged_critical()
    {
        var xml = Feed.Build(
            new FeedItem("1.2.0", "7", "https://x/b.exe", "sig", 1),
            new FeedItem("1.1.0", "6", "https://x/a.exe", "sig", 1, CriticalTag: true));
        Assert.Equal(new[] { "6" }, FeedRules.CriticalTaggedBuilds(xml));
    }
}

public sealed class SigningTests : IDisposable
{
    private readonly TempDir _temp = new();

    [Fact]
    public void Signs_and_verifies_with_the_release_tool_and_rejects_changes()
    {
        var (priv, pub) = Ed25519Tool.GenerateKeyPair();
        Assert.Equal(pub, Ed25519Tool.PublicKeyFromPrivate(priv));

        var file = _temp.File("installer.bin");
        File.WriteAllBytes(file, Enumerable.Range(0, 100_000).Select(i => (byte)(i * 31)).ToArray());
        var signature = Ed25519Tool.SignFile(file, priv);
        Assert.Equal(88, signature.Length);   // base64 of 64 bytes, the same shape as Sparkle's edSignature

        Assert.True(Ed25519Tool.VerifyFile(file, signature, pub));
        Assert.False(Ed25519Tool.VerifyFile(file, signature, Ed25519Tool.GenerateKeyPair().PublicKey));
        Assert.False(Ed25519Tool.VerifyFile(file, "not base64!", pub));

        File.AppendAllText(file, "x");
        Assert.False(Ed25519Tool.VerifyFile(file, signature, pub));
    }

    [Fact]
    public void A_signature_made_by_the_release_tool_is_accepted_by_the_apps_verifier()
    {
        var (priv, pub) = Ed25519Tool.GenerateKeyPair();
        var file = _temp.File("installer.bin");
        File.WriteAllBytes(file, new byte[64_000].Select((_, i) => (byte)(i ^ 0x5A)).ToArray());
        var signature = Ed25519Tool.SignFile(file, priv);

        var verifier = new Ed25519DownloadVerifier(pub);
        Assert.True(verifier.HasValidKeyInformation());
        Assert.Equal(ValidationResult.Valid, verifier.VerifySignatureOfFile(signature, file));
        Assert.Equal(ValidationResult.Invalid, verifier.VerifySignatureOfFile("", file));                 // installers must be signed
        Assert.Equal(ValidationResult.Invalid, verifier.VerifySignatureOfFile(signature, _Other(file)));  // wrong content

        // the feed itself may be unsigned (as on the Mac), but a signature that is present must be valid
        Assert.Equal(ValidationResult.Unchecked, verifier.VerifySignature("", new byte[] { 1, 2, 3 }));
        Assert.Equal(ValidationResult.Invalid, verifier.VerifySignature(signature, new byte[] { 1, 2, 3 }));
    }

    [Fact]
    public void An_empty_or_malformed_public_key_verifies_nothing()
    {
        var file = _temp.File("installer.bin");
        File.WriteAllBytes(file, new byte[] { 1, 2, 3 });
        var (priv, _) = Ed25519Tool.GenerateKeyPair();
        var signature = Ed25519Tool.SignFile(file, priv);

        Assert.False(new Ed25519DownloadVerifier("").HasValidKeyInformation());
        Assert.NotEqual(ValidationResult.Valid, new Ed25519DownloadVerifier("").VerifySignatureOfFile(signature, file));
        Assert.NotEqual(ValidationResult.Valid, new Ed25519DownloadVerifier("###").VerifySignatureOfFile(signature, file));
    }

    [Fact]
    public void Rejects_a_private_key_of_the_wrong_length()
    {
        Assert.Throws<ArgumentException>(() => Ed25519Tool.PublicKeyFromPrivate(Convert.ToBase64String(new byte[10])));
        Assert.Throws<ArgumentException>(() => Ed25519Tool.PublicKeyFromPrivate("###"));
    }

    private string _Other(string file)
    {
        var other = _temp.File("other.bin");
        File.WriteAllBytes(other, new byte[] { 9, 9, 9 });
        return other;
    }

    public void Dispose() => _temp.Dispose();
}

public sealed class ReleaseNotesParserTests
{
    [Fact]
    public void Parses_the_notes_the_release_script_publishes()
    {
        var blocks = ReleaseNotesParser.Parse("""
            <h2>Coworker 1.2.0</h2>
            <ul>
              <li>Added the <b>auto-update</b> pipeline</li>
              <li>Preserved login state &amp; settings <em>better</em></li>
            </ul>
            """);

        Assert.Equal(3, blocks.Count);
        Assert.Equal(NoteBlockKind.Heading, blocks[0].Kind);
        Assert.Equal(2, blocks[0].Level);
        Assert.Equal("Coworker 1.2.0", blocks[0].Runs.Single().Text);
        Assert.Equal(NoteBlockKind.ListItem, blocks[1].Kind);
        Assert.Equal("•", blocks[1].Marker);
        Assert.Contains(blocks[1].Runs, r => r.Bold && r.Text == "auto-update");
        Assert.Equal("Preserved login state & settings better", string.Concat(blocks[2].Runs.Select(r => r.Text)));
        Assert.Contains(blocks[2].Runs, r => r.Italic && r.Text == "better");
    }

    [Fact]
    public void Numbers_ordered_lists_and_nests_bullets()
    {
        var blocks = ReleaseNotesParser.Parse("<ol><li>one<li>two<ul><li>inner</li></ul></ol>");
        Assert.Equal(new[] { "1.", "2.", "•" }, blocks.Select(b => b.Marker));
        Assert.Equal(new[] { 0, 0, 1 }, blocks.Select(b => b.Level));
    }

    [Fact]
    public void Keeps_web_links_only_and_never_script_content()
    {
        var blocks = ReleaseNotesParser.Parse(
            """<p>See <a href="https://humanlike.co/coworker">the site</a> or <a href="javascript:alert(1)">this</a>.<script>evil()</script></p>""");
        var runs = blocks.Single().Runs;
        Assert.Contains(runs, r => r.Href == "https://humanlike.co/coworker" && r.Text == "the site");
        Assert.DoesNotContain(runs, r => r.Href != null && r.Href.StartsWith("javascript", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("evil", ReleaseNotesParser.ToPlainText(blocks));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Empty_notes_give_no_blocks(string? html)
    {
        Assert.Empty(ReleaseNotesParser.Parse(html));
    }

    [Fact]
    public void Survives_plain_text_and_stray_angle_brackets()
    {
        Assert.Equal("Fixed a < b bug", ReleaseNotesParser.ToPlainText(ReleaseNotesParser.Parse("Fixed a < b bug")));
        Assert.Equal("a b", ReleaseNotesParser.ToPlainText(ReleaseNotesParser.Parse("a<br>b")));
    }
}
