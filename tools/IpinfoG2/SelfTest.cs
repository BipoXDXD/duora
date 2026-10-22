using System.Text.Json;
using Duora.Infrastructure.Geolocation;
using Duora.IntegrationTests.Geolocation;

namespace Duora.Tools.IpinfoG2;

internal static class SelfTest
{
    public static int Run()
    {
        try
        {
            var profile = new G2Profile("https://github.com/BipoXDXD/duora/issues/108#issuecomment-1",
                1048576, 1000, 30, []);
            Reject(() => (profile with { MaxBytes = 0 }).Validate("inventory"));
            Reject(() => (profile with { MaxNetworks = 0 }).Validate("inventory"));
            Reject(() => (profile with { TimeoutSeconds = 0 }).Validate("inventory"));
            Reject(() => (profile with { Authorization = "" }).Validate("inventory"));
            Reject(() => (profile with { RedirectHosts = ["example.invalid"] }).Validate("inventory"));
            Reject(() => profile.Validate("validate"));
            Reject(() => (profile with { DatabaseType = "observed", ExpectedSha256 = "bad" }).Validate("validate"));
            profile.Validate("inventory");
            (profile with { DatabaseType = "observed", ExpectedSha256 = new string('a', 64) }).Validate("validate");

            // The same production validator must still reject the legacy product.
            var now = DateTimeOffset.UtcNow;
            var validator = new CountryDatabaseArtifactValidator(
                new(SyntheticMmdb.DatabaseType, profile.MaxBytes, profile.MaxNetworks), TimeProvider.System);
            validator.Validate(SyntheticMmdb.Create(now.AddHours(-1)), now, CancellationToken.None);
            try
            {
                validator.Validate(SyntheticMmdb.CreateLegacy(now.AddHours(-1), SyntheticMmdb.DatabaseType), now,
                    CancellationToken.None);
                throw new InvalidOperationException("Legacy MMDB was accepted.");
            }
            catch (CountryDatabaseValidationException error) when (error.Code == "Schema") { }

            var bytes = SyntheticMmdb.Create(now.AddHours(-1));
            var inventory = G2Evidence.Measure(bytes, now, profile, "inventory", CancellationToken.None);
            Ensure(inventory.Networks == 3 && inventory.Ipv4Networks == 2 && inventory.Ipv6Networks == 1);
            Ensure(inventory.LiteShapeMatches && !inventory.ValidationPassed);
            var json = JsonSerializer.Serialize(inventory);
            Ensure(!json.Contains("synthetic-organization-canary", StringComparison.Ordinal)
                && !json.Contains("synthetic.example.invalid", StringComparison.Ordinal)
                && !json.Contains("192.0.2.", StringComparison.Ordinal));
            var approved = profile with { DatabaseType = SyntheticMmdb.DatabaseType, ExpectedSha256 = inventory.Sha256 };
            var validated = G2Evidence.Measure(bytes, now, approved, "validate", CancellationToken.None);
            Ensure(validated.ValidationPassed && validated.Sha256 == inventory.Sha256);
            Reject(() => G2Evidence.Measure(bytes, now, approved with { ExpectedSha256 = new string('0', 64) },
                "validate", CancellationToken.None));
            Reject(() => G2Evidence.Measure(bytes, now, profile with { MaxBytes = 1 }, "inventory", CancellationToken.None));
            Reject(() => G2Evidence.Measure(bytes, now, profile with { MaxNetworks = 1 }, "inventory", CancellationToken.None));
            Reject(() => G2Profile.Parse(JsonSerializer.Serialize(profile).Replace("\"MaxBytes\":1048576", "\"MaxBytes\":1048576,\"Unknown\":1", StringComparison.Ordinal), "inventory"));
            Console.WriteLine("G2 synthetic checks passed; no provider access or gate acceptance.");
            return 0;
        }
        catch (Exception error) when (error is InvalidOperationException or G2Failure)
        {
            Console.Error.WriteLine("G2 synthetic regression failed: " + error.GetType().Name);
            return 1;
        }
    }

    private static void Ensure(bool condition)
    {
        if (!condition)
        { throw new InvalidOperationException("Synthetic G2 evidence is incoherent or discloses record data."); }
    }

    private static void Reject(Action action)
    {
        try { action(); }
        catch (G2Failure) { return; }
        throw new InvalidOperationException("Invalid rehearsal profile was accepted.");
    }
}
