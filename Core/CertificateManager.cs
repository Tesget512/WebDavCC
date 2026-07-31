using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace WebDavShare.Core;

/// <summary>自签名证书生成与复用。证书含本机所有 IPv4 的 SAN，客户端导入后可严格校验。</summary>
public static class CertificateManager
{
    private const string CertFileName = "server.crt";
    private const string PfxFileName = "server.pfx";

    public static string CertPemPath => Path.Combine(ConfigStore.CertDir, CertFileName);
    private static string PfxPath => Path.Combine(ConfigStore.CertDir, PfxFileName);

    /// <summary>取得可用于 SslStream 的证书；不存在或已过期则重新生成。</summary>
    public static X509Certificate2 GetOrCreate(Action<string>? progress = null)
    {
        Directory.CreateDirectory(ConfigStore.CertDir);

        if (File.Exists(PfxPath))
        {
            try
            {
                var existing = X509CertificateLoader.LoadPkcs12(File.ReadAllBytes(PfxPath), null);
                if (existing.NotAfter > DateTime.Now.AddDays(30) && existing.HasPrivateKey)
                {
                    return existing;
                }
                existing.Dispose();
                progress?.Invoke(Strings.CertExpiring);
            }
            catch (CryptographicException)
            {
                progress?.Invoke(Strings.CertCorrupt);
            }
        }

        progress?.Invoke(Strings.CertGenerating);
        var addresses = LocalAddresses();
        var cert = Create(addresses);

        // 私钥用 DPAPI 绑定当前用户，避免明文落盘被其他账户读取
        File.WriteAllBytes(PfxPath, cert.Export(X509ContentType.Pfx));
        File.WriteAllText(CertPemPath, cert.ExportCertificatePem());

        progress?.Invoke(Strings.CertGenerated(cert.NotAfter));
        progress?.Invoke("SAN: localhost, " + Environment.MachineName + ", "
                         + string.Join(", ", addresses));
        return cert;
    }

    private static X509Certificate2 Create(IReadOnlyList<string> addresses)
    {
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddDnsName(Environment.MachineName);
        foreach (var address in addresses)
        {
            if (IPAddress.TryParse(address, out var ip))
            {
                san.AddIpAddress(ip);
            }
        }

        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            $"CN={Environment.MachineName}, O=WebDAV Share",
            rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment
            | X509KeyUsageFlags.KeyCertSign, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [new Oid("1.3.6.1.5.5.7.3.1")], false));   // serverAuth
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(
            new X509SubjectKeyIdentifierExtension(request.PublicKey, false));

        // 回拨一小时，容忍客户端时钟偏差
        var from = DateTimeOffset.UtcNow.AddHours(-1);
        using var generated = request.CreateSelfSigned(from, from.AddYears(10));

        // CreateSelfSigned 的私钥不可直接持久化，往返一次 PFX 才能用于 SslStream
        return X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx), null,
            X509KeyStorageFlags.Exportable);
    }

    /// <summary>本机所有非回环 IPv4 地址，回环始终包含在内。</summary>
    public static List<string> LocalAddresses()
    {
        var result = new List<string>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up ||
                    nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                {
                    continue;
                }
                foreach (var addr in nic.GetIPProperties().UnicastAddresses)
                {
                    if (addr.Address.AddressFamily == AddressFamily.InterNetwork)
                    {
                        var text = addr.Address.ToString();
                        if (!result.Contains(text))
                        {
                            result.Add(text);
                        }
                    }
                }
            }
        }
        catch (NetworkInformationException)
        {
            // 网络栈异常时至少保证回环可用
        }
        result.Add("127.0.0.1");
        return result;
    }
}
