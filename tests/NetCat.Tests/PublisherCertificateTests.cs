using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using NetCat.Updater;
using Xunit;

namespace NetCat.Tests;

public sealed class PublisherCertificateTests
{
    private sealed class Store : IPublisherCertificateStore
    {
        public readonly HashSet<StoreName> Entries = [];
        public int Adds, Removes;
        public bool FailPublisher;
        public bool Contains(StoreName name, X509Certificate2 certificate) => Entries.Contains(name);
        public void Add(StoreName name, X509Certificate2 certificate)
        { if (FailPublisher && name == StoreName.TrustedPublisher) throw new IOException("Synthetic store failure"); Entries.Add(name); Adds++; }
        public void Remove(StoreName name, X509Certificate2 certificate) { Entries.Remove(name); Removes++; }
    }

    [Fact]
    public void EmbeddedCertificateHasPinnedHashPublicKeyAndCodeSigningOnly()
    {
        using var cert = PublisherCertificateTrust.Load();
        Assert.False(cert.HasPrivateKey); Assert.Equal(PublisherCertificateTrust.Subject, cert.Subject);
        Assert.Equal(PublisherCertificateTrust.Sha256, Convert.ToHexString(SHA256.HashData(cert.RawData)));
        Assert.False(cert.Extensions.OfType<X509BasicConstraintsExtension>().Single().CertificateAuthority);
        Assert.Equal("1.3.6.1.5.5.7.3.3", Assert.Single(cert.Extensions.OfType<X509EnhancedKeyUsageExtension>().Single().EnhancedKeyUsages.Cast<Oid>()).Value);
    }

    [Fact]
    public void ChangedCertificateCannotBeEnrolled()
    {
        using var cert = PublisherCertificateTrust.Load(); var data = cert.RawData; data[^1] ^= 1;
        Assert.Throws<InvalidDataException>(() => PublisherCertificateTrust.ValidateCertificate(data, PublisherCertificateTrust.Sha256));
    }

    [Fact]
    public void EnrollmentIsIdempotentAndChecksSignatureEveryTime()
    {
        using var cert = PublisherCertificateTrust.Load(); var store = new Store(); int checks = 0;
        PublisherCertificateTrust.Enroll(cert, store, () => checks++);
        PublisherCertificateTrust.Enroll(cert, store, () => checks++);
        Assert.Equal(2, store.Adds); Assert.Equal(2, checks); Assert.True(PublisherCertificateTrust.IsEnrolled(store));
        PublisherCertificateTrust.Remove(store); PublisherCertificateTrust.Remove(store);
        Assert.Equal(2, store.Removes); Assert.Empty(store.Entries);
    }

    [Fact]
    public void FailedVerificationRollsBackOnlyNewEntries()
    {
        using var cert = PublisherCertificateTrust.Load(); var store = new Store(); store.Entries.Add(StoreName.Root);
        Assert.Throws<InvalidDataException>(() => PublisherCertificateTrust.Enroll(cert, store, () => throw new InvalidDataException("Synthetic bad signature")));
        Assert.Equal([StoreName.Root], store.Entries); Assert.Equal(1, store.Removes);
    }

    [Fact]
    public void FailedSecondStoreInsertionRemovesNewRoot()
    {
        using var cert = PublisherCertificateTrust.Load(); var store = new Store { FailPublisher = true };
        Assert.Throws<IOException>(() => PublisherCertificateTrust.Enroll(cert, store, () => { }));
        Assert.Empty(store.Entries); Assert.Equal(1, store.Removes);
    }
}
