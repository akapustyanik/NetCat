using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Runtime.InteropServices;

namespace NetCat.Updater;

public interface IPublisherCertificateStore
{
    bool Contains(StoreName store, X509Certificate2 certificate);
    void Add(StoreName store, X509Certificate2 certificate);
    void Remove(StoreName store, X509Certificate2 certificate);
}

// Only the bundled, pinned code-signing certificate may be enrolled. Enrollment
// is explicit and limited to the current Windows user, never LocalMachine.
public static class PublisherCertificateTrust
{
    public const string Sha256 = "DF65D213985F781F2732E12847C176B761BEB2FEFC720328181F339EC466A42A";
    public const string Subject = "CN=NetCat Publisher";
    private const int UntrustedRoot = unchecked((int)0x800B0109);
    private static readonly StoreName[] Stores = [StoreName.Root, StoreName.TrustedPublisher];

    public static X509Certificate2 Load()
    {
        using var resource = typeof(PublisherCertificateTrust).Assembly.GetManifestResourceStream("NetCat.PublisherCertificate")
            ?? throw new InvalidDataException("Нет сертификата издателя NetCat.");
        using var bytes = new MemoryStream(); resource.CopyTo(bytes);
        return ValidateCertificate(bytes.ToArray(), Sha256);
    }

    public static X509Certificate2 ValidateCertificate(byte[] data, string expectedSha256)
    {
        if (!Convert.ToHexString(SHA256.HashData(data)).Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Сертификат не соответствует издателю NetCat.");
        var certificate = new X509Certificate2(data);
        try
        {
            var constraints = certificate.Extensions.OfType<X509BasicConstraintsExtension>().SingleOrDefault();
            var usage = certificate.Extensions.OfType<X509KeyUsageExtension>().SingleOrDefault();
            var eku = certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>().SingleOrDefault();
            if (certificate.HasPrivateKey || certificate.Subject != Subject || certificate.Issuer != certificate.Subject ||
                constraints == null || constraints.CertificateAuthority ||
                usage?.KeyUsages != X509KeyUsageFlags.DigitalSignature ||
                eku == null || eku.EnhancedKeyUsages.Count != 1 || eku.EnhancedKeyUsages[0].Value != "1.3.6.1.5.5.7.3.3")
                throw new InvalidDataException("Недопустимый сертификат подписи NetCat.");
            return certificate;
        }
        catch { certificate.Dispose(); throw; }
    }

    public static void RequireMatchingSignature(string executable, X509Certificate2 certificate,
        Func<string, int>? verify = null)
    {
        using var signer = X509Certificate.CreateFromSignedFile(executable);
        if (!CryptographicOperations.FixedTimeEquals(signer.GetRawCertData(), certificate.RawData))
            throw new InvalidDataException("Эта сборка подписана другим сертификатом.");
        var status = (verify ?? PublisherTrust.VerificationStatus)(executable);
        // An untrusted root is the only permitted bootstrap failure. Missing,
        // expired, revoked or damaged signatures can never enroll a certificate.
        if (status != 0 && status != UntrustedRoot)
            throw new InvalidDataException("Подпись NetCat повреждена или недействительна.");
    }

    public static bool IsEnrolled(IPublisherCertificateStore? store = null)
    {
        using var certificate = Load(); store ??= new CurrentUserStore();
        return Stores.All(s => store.Contains(s, certificate));
    }

    internal static bool ContainsAndReleaseCertificates(X509Certificate2Collection entries, byte[] expected)
    {
        try
        {
            foreach (var entry in entries)
                if (entry.RawData.AsSpan().SequenceEqual(expected)) return true;
            return false;
        }
        finally
        {
            // X509Store.Dispose does not dispose the certificate copies returned
            // by Certificates, including copies after an early successful match.
            foreach (var entry in entries) entry.Dispose();
        }
    }

    public static void Install(string executable)
    {
        using var certificate = Load(); RequireMatchingSignature(executable, certificate);
        Enroll(certificate, new CurrentUserStore(), () => PublisherTrust.RequireSamePublisher(executable, executable));
    }

    public static void Enroll(X509Certificate2 certificate, IPublisherCertificateStore store, Action verify)
    {
        using var validated = ValidateCertificate(certificate.RawData, Sha256);
        if (DateTime.Now < validated.NotBefore || DateTime.Now > validated.NotAfter)
            throw new InvalidDataException("Срок действия сертификата издателя истёк или ещё не начался.");
        var added = new List<StoreName>();
        try
        {
            foreach (var name in Stores)
                if (!store.Contains(name, validated)) { store.Add(name, validated); added.Add(name); }
            verify();
        }
        catch
        {
            foreach (var name in added.AsEnumerable().Reverse()) store.Remove(name, validated);
            throw;
        }
    }

    public static void Remove(IPublisherCertificateStore? store = null)
    {
        using var certificate = Load(); store ??= new CurrentUserStore();
        foreach (var name in Stores) if (store.Contains(name, certificate)) store.Remove(name, certificate);
    }

    public static void Export(string path)
    {
        using var certificate = Load(); File.WriteAllBytes(path, certificate.RawData);
    }

    private sealed class CurrentUserStore : IPublisherCertificateStore
    {
        [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern nint CertOpenStore(nint provider, uint encoding, nint cryptProvider, uint flags, string name);
        [DllImport("crypt32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CertCloseStore(nint store, uint flags);

        private static X509Store? OpenOwned(StoreName name, bool readOnly)
        {
            // The logical CurrentUser Root store also contains LocalMachine
            // certificates. Read/remove only the physical per-user registry
            // store so inherited trust is never mistaken for our enrollment.
            const uint currentUser = 0x10000, existing = 0x4000, read = 0x8000;
            var handle = CertOpenStore((nint)13, 0, 0, currentUser | existing | (readOnly ? read : 0), name.ToString());
            if (handle == 0)
            {
                var error = Marshal.GetLastWin32Error();
                if (error is 2 or 3) return null;
                throw new CryptographicException(error);
            }
            try { return new X509Store(handle); }
            finally { CertCloseStore(handle, 0); }
        }

        public bool Contains(StoreName name, X509Certificate2 certificate)
        {
            using var store = OpenOwned(name, true);
            return store != null && ContainsAndReleaseCertificates(store.Certificates, certificate.RawData);
        }
        public void Add(StoreName name, X509Certificate2 certificate)
        {
            using var store = new X509Store(name, StoreLocation.CurrentUser); store.Open(OpenFlags.ReadWrite); store.Add(certificate);
        }
        public void Remove(StoreName name, X509Certificate2 certificate)
        {
            using var store = OpenOwned(name, false);
            if (store == null) return;
            var entries = store.Certificates;
            var expected = certificate.RawData;
            try
            {
                foreach (var entry in entries)
                    if (entry.RawData.AsSpan().SequenceEqual(expected)) store.Remove(entry);
            }
            finally { foreach (var entry in entries) entry.Dispose(); }
        }
    }
}
