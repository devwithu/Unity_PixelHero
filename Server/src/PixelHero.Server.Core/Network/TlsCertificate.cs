using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace PixelHero.Server.Network
{
    /// <summary>
    /// The certificate of the secure WebSocket listener (SSL_CERTIFICATE), with the intermediates the server sends
    /// along. C# server only: the original has no TLS listener.
    /// <list type="bullet">
    /// <item>A PKCS#12 file (.pfx, .p12): the certificate with a private key is the server's, the rest is the chain.</item>
    /// <item>PEM, as Let's Encrypt (certbot) writes it: <c>fullchain.pem</c> (the server's certificate first, then the
    /// intermediates) with the key in <c>privkey.pem</c> (SSL_CERTIFICATE_KEY; PKCS#8, PKCS#1 or SEC1, RSA or ECDSA).
    /// Without a key file the key is read from the certificate file. A password decrypts an encrypted key.</item>
    /// </list>
    /// Certificates are read once at startup; after a renewal the server must be restarted.
    /// </summary>
    public static class TlsCertificate
    {
        public static bool IsPkcs12(string path)
        {
            var extension = Path.GetExtension(path).ToLowerInvariant();
            return extension == ".pfx" || extension == ".p12";
        }

        public static SslStreamCertificateContext Load(string certificatePath, string keyPath = null, string password = null)
        {
            var (certificate, intermediates) = LoadCertificate(certificatePath, keyPath, password);
            return SslStreamCertificateContext.Create(certificate, intermediates, offline: true);
        }

        /// <summary>The server's certificate (with its private key) and the other certificates of the file.</summary>
        public static (X509Certificate2 certificate, X509Certificate2Collection intermediates) LoadCertificate(
            string certificatePath, string keyPath = null, string password = null)
        {
            if (!File.Exists(certificatePath)) throw new FileNotFoundException("The TLS certificate does not exist.", certificatePath);

            if (IsPkcs12(certificatePath))
            {
                var all = X509CertificateLoader.LoadPkcs12CollectionFromFile(certificatePath, password);
                var certificate = all.FirstOrDefault(c => c.HasPrivateKey)
                    ?? throw new InvalidOperationException($"{certificatePath} has no certificate with a private key.");
                var others = new X509Certificate2Collection();
                foreach (var other in all)
                    if (!ReferenceEquals(other, certificate)) others.Add(other);
                return (certificate, others);
            }

            var keyFile = string.IsNullOrEmpty(keyPath) ? certificatePath : keyPath;
            if (!File.Exists(keyFile)) throw new FileNotFoundException("The TLS private key does not exist.", keyFile);

            // The first certificate of the file is the server's (certbot's fullchain.pem order).
            var withKey = string.IsNullOrEmpty(password)
                ? X509Certificate2.CreateFromPemFile(certificatePath, keyFile)
                : X509Certificate2.CreateFromEncryptedPemFile(certificatePath, password, keyFile);

            // A key read from PEM is ephemeral, which Windows (SChannel) cannot use for TLS: re-import it through PKCS#12.
            var server = OperatingSystem.IsWindows()
                ? X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pkcs12), null)
                : withKey;

            var chain = new X509Certificate2Collection();
            chain.ImportFromPemFile(certificatePath);
            var intermediates = new X509Certificate2Collection();
            foreach (var certificate in chain)
                if (certificate.Thumbprint != server.Thumbprint) intermediates.Add(certificate);

            return (server, intermediates);
        }
    }
}
