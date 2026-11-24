using BorgStudio.Server.Domain.Access;
using BorgStudio.Server.Domain.Settings;

namespace BorgStudio.Server.Tests.Domain;

public sealed class AccessRulesTests
{
    /// <summary>A real key and the fingerprint "ssh-keygen -lf" shows for it.</summary>
    internal const string PublicKey = "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIEf6JZfblV7ooXxq+355BhP72PtrbUaTfJE6LH4zUIe3 test@example";

    internal const string Fingerprint = "SHA256:JskoJkZDfSsuIwk4H0gRO81FONSn2uEb0T4wJBFzT0Y";

    [Fact]
    public void PublicKeys_AreReadLikeOpenSsh()
    {
        var key = SshPublicKey.Parse("  " + PublicKey + "\n")!;

        Assert.Equal("ssh-ed25519", key.Algorithm);
        Assert.Equal("test@example", key.Comment);
        Assert.Equal(Fingerprint, key.Fingerprint);
        Assert.Null(SshPublicKey.Parse("ssh-ed25519 " + PublicKey.Split(' ')[1])!.Comment);
    }

    [Theory]
    [InlineData("")]
    [InlineData("ssh-ed25519")]
    [InlineData("ssh-ed25519 not-base64!")]
    [InlineData("ssh-dss AAAAB3NzaC1kc3MAAACBAP")]
    [InlineData("ssh-rsa AAAAC3NzaC1lZDI1NTE5AAAAIEf6JZfblV7ooXxq+355BhP72PtrbUaTfJE6LH4zUIe3")]
    [InlineData("command=\"rm -rf /\" ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIEf6JZfblV7ooXxq+355BhP72PtrbUaTfJE6LH4zUIe3")]
    [InlineData("ssh-ed25519 AAAAC3NzaC1lZDI1NTE5")]
    public void PublicKeys_RejectWhatIsNotOne(string text) => Assert.Null(SshPublicKey.Parse(text));

    [Theory]
    [InlineData("kmtd 4xqa", "KMTD-4XQA")]
    [InlineData("KMTD4XQA", "KMTD-4XQA")]
    [InlineData(" kmtd-4xqa ", "KMTD-4XQA")]
    [InlineData(null, "")]
    public void UserCodes_AreForgiving(string? typed, string expected) => Assert.Equal(expected, PairingRequest.NormalizeUserCode(typed));

    [Fact]
    public void Pairing_GivesALongSecretAndAShortCode()
    {
        var now = DateTimeOffset.UtcNow;
        var (request, code) = PairingRequest.Create("  NB-NIKLAS  ", "Windows 11", "0.6.0", now);

        Assert.Equal("NB-NIKLAS", request.DeviceName);
        Assert.Matches("^[A-Z0-9]{4}-[A-Z0-9]{4}$", request.UserCode);
        Assert.True(code.Length >= 40);
        Assert.Equal(Device.HashToken(code), request.PairingCodeHash);
        Assert.False(request.IsExpired(now.AddMinutes(9)));
        Assert.True(request.IsExpired(now.AddMinutes(10)));
    }

    [Fact]
    public void Tokens_AreHashed_AndOnlyApiTokensExpire()
    {
        var now = DateTimeOffset.UtcNow;
        var (app, appToken) = Device.Create("user", DeviceKind.App, "laptop", "Linux", "0.6.0", now.AddDays(1), now);
        var (script, _) = Device.Create("user", DeviceKind.ApiToken, "script", null, null, now.AddDays(1), now);

        Assert.StartsWith(Device.TokenPrefix, appToken);
        Assert.Equal(Device.HashToken(appToken), app.TokenHash);
        Assert.Null(app.ExpiresAt);
        Assert.False(script.IsExpired(now));
        Assert.True(script.IsExpired(now.AddDays(1)));

        Assert.True(app.Touch(now));
        Assert.False(app.Touch(now.AddSeconds(30)));
        Assert.True(app.Touch(now.AddMinutes(2)));
    }

    [Theory]
    [InlineData("", true, null, null)]
    [InlineData("backup.example.org", true, "backup.example.org", null)]
    [InlineData("backup.example.org:22022", true, "backup.example.org", 22022)]
    [InlineData("192.0.2.10:2222", true, "192.0.2.10", 2222)]
    [InlineData("[2001:db8::1]:2222", true, "2001:db8::1", 2222)]
    [InlineData("[2001:db8::1]", true, "2001:db8::1", null)]
    [InlineData("ssh://backup.example.org", false, null, null)]
    [InlineData("backup.example.org:0", false, null, null)]
    [InlineData("backup.example.org:70000", false, null, null)]
    [InlineData("bad host", false, null, null)]
    public void SshAddresses(string text, bool valid, string? host, int? port)
    {
        Assert.Equal(valid, ServerSettings.TryParseSshAddress(text, out var parsedHost, out var parsedPort));
        Assert.Equal(host, parsedHost);
        Assert.Equal(port, parsedPort);
    }
}
