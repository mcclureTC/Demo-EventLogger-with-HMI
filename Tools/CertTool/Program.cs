// Creates the demo certificates for the Mosquitto broker and its two clients.
//
// The files mirror what AWS IoT Core issues: an RSA 2048 certificate in PEM and its private key in
// PEM (PKCS#1, "BEGIN RSA PRIVATE KEY"). The demo CA plays the part of Amazon's root CA.
//
//   dotnet run --project Tools/CertTool                     writes to Broker/certs
//   dotnet run --project Tools/CertTool -- --out <dir>      writes elsewhere
//   dotnet run --project Tools/CertTool -- --host <name|ip> adds a name or address to the broker
//                                                           certificate (repeatable)
//   dotnet run --project Tools/CertTool -- --force          replaces an existing CA (re-issues everything)
//
// The broker certificate always covers localhost, this PC's name, the loopback addresses and this
// PC's current IPv4 addresses, so a PLC on another machine can connect by name or by address.

using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

const string PlcIdentity = "eventloggerdemo-01";
const string HmiIdentity = "eventloggerdemo-hmi";
const string ServerAuth = "1.3.6.1.5.5.7.3.1";
const string ClientAuth = "1.3.6.1.5.5.7.3.2";

var outDir = DefaultOutputDirectory();
var force = false;
var extraHosts = new List<string>();
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--out" when i + 1 < args.Length:
            outDir = Path.GetFullPath(args[++i]);
            break;
        case "--host" when i + 1 < args.Length:
            extraHosts.Add(args[++i]);
            break;
        case "--force":
            force = true;
            break;
        default:
            Console.Error.WriteLine($"Unknown argument '{args[i]}'. Use --out <dir>, --host <name|ip> and/or --force.");
            return 1;
    }
}

Directory.CreateDirectory(outDir);
Console.WriteLine($"Certificates in {outDir}");

var caCertPath = Path.Combine(outDir, "ca.crt");
var caKeyPath = Path.Combine(outDir, "ca.key");
var now = DateTimeOffset.UtcNow.AddMinutes(-5);

X509Certificate2 ca;
if (File.Exists(caCertPath) && File.Exists(caKeyPath) && !force)
{
    ca = X509Certificate2.CreateFromPemFile(caCertPath, caKeyPath);
    Console.WriteLine($"  ca.crt              kept (use --force to replace it), valid until {ca.NotAfter:yyyy-MM-dd}");
}
else
{
    ca = CreateCa("EventLoggerDemo Demo CA", now, now.AddYears(10), caCertPath, caKeyPath);
    Console.WriteLine($"  ca.crt, ca.key      new demo CA, valid until {ca.NotAfter:yyyy-MM-dd}");
}

var leafEnd = Min(now.AddYears(2), ca.NotAfter);

var serverNames = new List<string> { "localhost", Environment.MachineName };
var serverAddresses = new List<IPAddress> { IPAddress.Loopback, IPAddress.IPv6Loopback };
serverAddresses.AddRange(LocalIPv4Addresses());
foreach (var host in extraHosts)
{
    if (IPAddress.TryParse(host, out var address))
        serverAddresses.Add(address);
    else
        serverNames.Add(host);
}
serverNames = serverNames.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
serverAddresses = serverAddresses.Distinct().ToList();

CreateLeaf(ca, "broker", serverNames, serverAddresses, ServerAuth, now, leafEnd, outDir);
Console.WriteLine($"  broker.crt, .key    for {string.Join(", ", serverNames.Concat(serverAddresses.Select(a => a.ToString())))}");

CreateLeaf(ca, PlcIdentity, null, null, ClientAuth, now, leafEnd, outDir);
Console.WriteLine($"  {PlcIdentity}.crt, .key   PLC client (CN = Thing Name = client ID)");

CreateLeaf(ca, HmiIdentity, null, null, ClientAuth, now, leafEnd, outDir);
Console.WriteLine($"  {HmiIdentity}.crt, .key  HMI consumer");

Console.WriteLine();
Console.WriteLine("The .key files are private keys: keep them out of version control.");
Console.WriteLine($"PLC on another machine: copy ca.crt, {PlcIdentity}.crt and {PlcIdentity}.key to it.");
Console.WriteLine("If this PC's address changes, run CertTool again and restart the broker.");
return 0;

// This PC's IPv4 addresses on network interfaces that are up (loopback excluded).
static IEnumerable<IPAddress> LocalIPv4Addresses() =>
    NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
        .SelectMany(n => n.GetIPProperties().UnicastAddresses)
        .Select(u => u.Address)
        .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a));

static X509Certificate2 CreateCa(string name, DateTimeOffset from, DateTimeOffset to, string certPath, string keyPath)
{
    using var key = RSA.Create(2048);
    var request = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
    request.CertificateExtensions.Add(new X509KeyUsageExtension(
        X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
    request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));

    var ca = request.CreateSelfSigned(from, to);
    File.WriteAllText(certPath, ca.ExportCertificatePem() + Environment.NewLine);
    File.WriteAllText(keyPath, key.ExportRSAPrivateKeyPem() + Environment.NewLine);
    return ca;
}

static void CreateLeaf(X509Certificate2 ca, string commonName, IReadOnlyList<string>? dnsNames,
    IReadOnlyList<IPAddress>? addresses, string usageOid, DateTimeOffset from, DateTimeOffset to, string outDir)
{
    using var key = RSA.Create(2048);
    var request = new CertificateRequest($"CN={commonName}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
    request.CertificateExtensions.Add(new X509KeyUsageExtension(
        X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
    request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid(usageOid) }, false));
    request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
    request.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(ca, true, false));

    if (dnsNames is not null)
    {
        var san = new SubjectAlternativeNameBuilder();
        foreach (var name in dnsNames)
            san.AddDnsName(name);
        foreach (var address in addresses ?? Array.Empty<IPAddress>())
            san.AddIpAddress(address);
        request.CertificateExtensions.Add(san.Build());
    }

    var serial = RandomNumberGenerator.GetBytes(16);
    serial[0] &= 0x7F; // positive serial number

    using var cert = request.Create(ca, from, to, serial);
    File.WriteAllText(Path.Combine(outDir, commonName + ".crt"), cert.ExportCertificatePem() + Environment.NewLine);
    File.WriteAllText(Path.Combine(outDir, commonName + ".key"), key.ExportRSAPrivateKeyPem() + Environment.NewLine);
}

static DateTimeOffset Min(DateTimeOffset a, DateTime b) =>
    a < new DateTimeOffset(b.ToUniversalTime()) ? a : new DateTimeOffset(b.ToUniversalTime());

// Broker/certs in the repository, found by walking up from the current directory.
static string DefaultOutputDirectory()
{
    for (var dir = new DirectoryInfo(Directory.GetCurrentDirectory()); dir is not null; dir = dir.Parent)
    {
        if (Directory.Exists(Path.Combine(dir.FullName, "Broker")))
            return Path.Combine(dir.FullName, "Broker", "certs");
    }
    return Path.GetFullPath("certs");
}
