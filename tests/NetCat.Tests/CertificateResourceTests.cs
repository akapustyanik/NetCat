using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using NetCat.Updater;
using Xunit;

namespace NetCat.Tests;

public sealed class CertificateResourceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EnrollmentLookupReleasesEveryCertificateIncludingAfterEarlyMatch(bool match)
    {
        using var publisher = PublisherCertificateTrust.Load();
        var expected = match ? publisher.RawData : new byte[] { 0 };
        var entries = new X509Certificate2Collection();
        for (int i = 0; i < 8; i++) entries.Add(new X509Certificate2(publisher.RawData));
        try
        {
            var find = typeof(PublisherCertificateTrust).GetMethod("ContainsAndReleaseCertificates",
                BindingFlags.Static | BindingFlags.NonPublic)!;
            Assert.Equal(match, (bool)find.Invoke(null, [entries, expected])!);
            foreach (var entry in entries)
                Assert.Throws<CryptographicException>(() => entry.GetRawCertData());
            Assert.NotEmpty(publisher.RawData);
        }
        finally { foreach (var entry in entries) entry.Dispose(); }
    }
}
