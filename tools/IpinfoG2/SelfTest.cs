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
            Console.WriteLine("G2 synthetic checks passed; no provider access or gate acceptance.");
            return 0;
        }
        catch (Exception error) when (error is InvalidOperationException or G2Failure)
        {
            Console.Error.WriteLine("G2 synthetic regression failed: " + error.GetType().Name);
            return 1;
        }
    }

    private static void Reject(Action action)
    {
        try { action(); }
        catch (G2Failure) { return; }
        throw new InvalidOperationException("Invalid rehearsal profile was accepted.");
    }
}
