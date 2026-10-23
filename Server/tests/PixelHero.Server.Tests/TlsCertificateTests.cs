using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using PixelHero.Server.Network;
using NUnit.Framework;

namespace PixelHero.Server.Tests
{
    /// <summary>
    /// The wss listener's certificate files: certbot's PEM layout (fullchain.pem = server certificate then
    /// intermediate, privkey.pem in PKCS#8) and PKCS#12. Certificates are made here: root → intermediate → server.
    /// </summary>
    public sealed class TlsCertificateTests
    {
        string _dir;
        X509Certificate2 _root, _intermediate, _server;

        [OneTimeSetUp]
        public void MakeCertificates()
        {
            _dir = Path.Combine(Path.GetTempPath(), "pixelhero-tls-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);

            var rootKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var rootRequest = new CertificateRequest("CN=Test Root", rootKey, HashAlgorithmName.SHA256);
            rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            _root = rootRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(2));

            var intermediateKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var intermediateRequest = new CertificateRequest("CN=Test Intermediate", intermediateKey, HashAlgorithmName.SHA256);
            intermediateRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
            _intermediate = intermediateRequest.Create(_root, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1), Serial())
                .CopyWithPrivateKey(intermediateKey);

            var serverKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var serverRequest = new CertificateRequest("CN=localhost", serverKey, HashAlgorithmName.SHA256);
            _server = serverRequest.Create(_intermediate, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(90), Serial());

            File.WriteAllText(At("fullchain.pem"), _server.ExportCertificatePem() + "\n" + _intermediate.ExportCertificatePem() + "\n");
            File.WriteAllText(At("cert.pem"), _server.ExportCertificatePem() + "\n");
            File.WriteAllText(At("privkey.pem"), serverKey.ExportPkcs8PrivateKeyPem() + "\n");
            File.WriteAllText(At("combined.pem"), File.ReadAllText(At("fullchain.pem")) + File.ReadAllText(At("privkey.pem")));
            File.WriteAllText(At("encrypted.pem"), serverKey.ExportEncryptedPkcs8PrivateKeyPem("secret",
                new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 1000)) + "\n");

            var pfx = new X509Certificate2Collection { _server.CopyWithPrivateKey(serverKey), _intermediate };
            File.WriteAllBytes(At("server.pfx"), pfx.Export(X509ContentType.Pkcs12, "pfxpass")!);

            // Another key: a certificate paired with it must be refused.
            File.WriteAllText(At("otherkey.pem"), ECDsa.Create(ECCurve.NamedCurves.nistP256).ExportPkcs8PrivateKeyPem() + "\n");
        }

        [OneTimeTearDown]
        public void Clean()
        {
            _root?.Dispose();
            _intermediate?.Dispose();
            _server?.Dispose();
            Directory.Delete(_dir, true);
        }

        string At(string name) => Path.Combine(_dir, name);

        static byte[] Serial() => RandomNumberGenerator.GetBytes(16);

        [Test]
        public void LetsEncryptFullchainAndPrivkey()
        {
            var (certificate, intermediates) = TlsCertificate.LoadCertificate(At("fullchain.pem"), At("privkey.pem"));

            Assert.That(certificate.Thumbprint, Is.EqualTo(_server.Thumbprint));
            Assert.That(certificate.HasPrivateKey, Is.True);
            Assert.That(intermediates.Cast<X509Certificate2>().Select(c => c.Thumbprint), Is.EqualTo(new[] { _intermediate.Thumbprint }));

            // The context the listener uses sends the intermediate (not the root, which clients already have).
            var context = TlsCertificate.Load(At("fullchain.pem"), At("privkey.pem"));
            Assert.That(context.TargetCertificate.Thumbprint, Is.EqualTo(_server.Thumbprint));
            Assert.That(context.IntermediateCertificates.Select(c => c.Thumbprint), Is.EqualTo(new[] { _intermediate.Thumbprint }));
        }

        [Test]
        public void CertificateOnlyFileHasNoIntermediates()
        {
            var (certificate, intermediates) = TlsCertificate.LoadCertificate(At("cert.pem"), At("privkey.pem"));
            Assert.That(certificate.HasPrivateKey, Is.True);
            Assert.That(intermediates, Is.Empty);
        }

        [Test]
        public void KeyInTheCertificateFile()
        {
            var (certificate, intermediates) = TlsCertificate.LoadCertificate(At("combined.pem"));
            Assert.That(certificate.Thumbprint, Is.EqualTo(_server.Thumbprint));
            Assert.That(certificate.HasPrivateKey, Is.True);
            Assert.That(intermediates, Has.Count.EqualTo(1));
        }

        [Test]
        public void EncryptedKeyWithPassword()
        {
            var (certificate, _) = TlsCertificate.LoadCertificate(At("fullchain.pem"), At("encrypted.pem"), "secret");
            Assert.That(certificate.HasPrivateKey, Is.True);
            Assert.Throws(Is.InstanceOf<Exception>(), () => TlsCertificate.LoadCertificate(At("fullchain.pem"), At("encrypted.pem")));
        }

        [Test]
        public void Pkcs12WithChain()
        {
            var (certificate, intermediates) = TlsCertificate.LoadCertificate(At("server.pfx"), password: "pfxpass");
            Assert.That(certificate.Thumbprint, Is.EqualTo(_server.Thumbprint));
            Assert.That(certificate.HasPrivateKey, Is.True);
            Assert.That(intermediates.Cast<X509Certificate2>().Select(c => c.Thumbprint), Is.EqualTo(new[] { _intermediate.Thumbprint }));
        }

        [Test]
        public void MismatchedOrMissingFilesAreRefused()
        {
            Assert.Throws(Is.InstanceOf<ArgumentException>(), () => TlsCertificate.LoadCertificate(At("fullchain.pem"), At("otherkey.pem")));
            Assert.Throws<FileNotFoundException>(() => TlsCertificate.LoadCertificate(At("missing.pem"), At("privkey.pem")));
            Assert.Throws<FileNotFoundException>(() => TlsCertificate.LoadCertificate(At("fullchain.pem"), At("missing.pem")));
        }
    }
}
