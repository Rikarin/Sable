using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

// Port of tls.sb: SslStream over loopback sockets, the server's certificate
// of P-256, self-signed and pinned by the client; TLS 1.3 where the
// platform's TLS has it (OpenSSL on Linux; Apple's on macOS may make it
// TLS 1.2), AES-128-GCM where it may be chosen.
[Benchmark("library/tls", "handshakes", 20)]
[Benchmark("library/tls", "bulk", 4_194_304)]
static class LibraryTls {
    public static Func<string> Setup(string mode, int count) {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost");
        request.CertificateExtensions.Add(names.Build());
        using var made = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        // A certificate whose key the server's TLS can use on every platform.
        var certificate = X509CertificateLoader.LoadPkcs12(made.Export(X509ContentType.Pkcs12), null);
        var connections = mode == "handshakes" ? count : 1;
        var bytes = mode == "handshakes" ? 1 : count;
        return () => RunAsync(certificate, connections, bytes).GetAwaiter().GetResult();
    }

    static async Task<int> Serve(Socket listener, X509Certificate2 certificate, int connections, int bytes) {
        var total = 0;
        var buffer = new byte[65536];
        for (var i = 0; i < connections; i++) {
            using var socket = await listener.AcceptAsync();
            socket.NoDelay = true;
            await using var tls = new SslStream(new NetworkStream(socket, ownsSocket: false));
            await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions {
                ServerCertificate = certificate,
                EnabledSslProtocols = SslProtocols.None,
            });
            var got = 0;
            while (got < bytes) {
                var read = await tls.ReadAsync(buffer);
                if (read == 0) {
                    break;
                }

                got = checked(got + read);
            }

            await tls.WriteAsync(Encoding.ASCII.GetBytes(got.ToString()));
            await tls.ShutdownAsync();
            total = checked(total + got);
        }

        return total;
    }

    static async Task<int> Ask(EndPoint address, X509Certificate2 pinned, int count) {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        await socket.ConnectAsync(address);
        await using var tls = new SslStream(new NetworkStream(socket, ownsSocket: false));
        await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions {
            TargetHost = "localhost",
            RemoteCertificateValidationCallback = (_, certificate, _, _) => certificate is not null && certificate.GetRawCertData().AsSpan().SequenceEqual(pinned.RawData),
        });
        var chunk = new byte[16384];
        Array.Fill(chunk, (byte)1);
        var sent = 0;
        while (sent < count) {
            var n = Math.Min(count - sent, chunk.Length);
            await tls.WriteAsync(chunk.AsMemory(0, n));
            sent += n;
        }

        var answer = new MemoryStream();
        var buffer = new byte[64];
        while (true) {
            var read = await tls.ReadAsync(buffer);
            if (read == 0) {
                break;
            }

            answer.Write(buffer, 0, read);
        }

        return int.Parse(Encoding.ASCII.GetString(answer.ToArray()));
    }

    static async Task<string> RunAsync(X509Certificate2 certificate, int connections, int bytes) {
        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(16);
        var address = listener.LocalEndPoint!;
        var server = Task.Run(() => Serve(listener, certificate, connections, bytes));
        var answered = 0;
        for (var i = 0; i < connections; i++) {
            answered = checked(answered + await Ask(address, certificate, bytes));
        }

        var read = await server;
        return (read + answered).ToString();
    }
}
