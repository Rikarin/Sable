using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

// A server's certificate read 5000 times, and whose it is.
[Benchmark("library/x509-parse")]
static class LibraryX509Parse {
    const string Pem = """
        -----BEGIN CERTIFICATE-----
        MIICtDCCAlqgAwIBAgIECgsMDTAKBggqhkjOPQQDAjA7MQswCQYDVQQGEwJDSDEV
        MBMGA1UECgwMRXhhbXBsZSwgTHRkMRUwEwYDVQQDDAxJbnRlcm1lZGlhdGUwHhcN
        MjYwMTAxMDAwMDAwWhcNMzYwMTAxMDAwMDAwWjAaMRgwFgYDVQQDDA93d3cuZXhh
        bXBsZS5jb20wdjAQBgcqhkjOPQIBBgUrgQQAIgNiAATBHpJx7VFo2nM1SMny1rlg
        GKXOYRr9jU61jhMkzmxKoFxW37Bj2ISK/o+0UrLpKqe90pB02O3OLds9fo8KuC7A
        1a0aQHfORtp0vFRcFLfCM1O+wYJr/yp813mvWkvOT4CjggFOMIIBSjAJBgNVHRME
        AjAAMA4GA1UdDwEB/wQEAwIHgDAdBgNVHSUEFjAUBggrBgEFBQcDAQYIKwYBBQUH
        AwIwHQYDVR0OBBYEFM860kydDRFNLtnUZyUZ3S1Cq20uMB8GA1UdIwQYMBaAFMlG
        9acB+L8pCOVI/wT3vIrY+kbWMDQGCCsGAQUFBwEBBCgwJjAkBggrBgEFBQcwAYYY
        aHR0cDovL29jc3AuZXhhbXBsZS5jb20vMIGXBgNVHREEgY8wgYyCD3d3dy5leGFt
        cGxlLmNvbYISKi5hcHBzLmV4YW1wbGUuY29thwTAAAIBhxAgAQ24AAAAAAAAAAAA
        AAABgQ1hQGV4YW1wbGUuY29thhVodHRwczovL2V4YW1wbGUuY29tL3iIAyoDBKAO
        BgMqAwWgBwwFaGVsbG+kEjAQMQ4wDAYDVQQDDAVpbm5lcjAKBggqhkjOPQQDAgNI
        ADBFAiEAtBZuvCZ1YqkkHPpxYkx6rhgVBPwtQ+1Tx5ndWO6AszYCIFbjSNLnUG/U
        vZBm/LVl76Od66qgavZ2WI1kkQ9l0kxp
        -----END CERTIFICATE-----
        """;

    public static Func<string> Setup() {
        checked {
            var fields = PemEncoding.Find(Pem);
            var der = Convert.FromBase64String(Pem[fields.Base64Data]);
            return () => Run(der);
        }
    }

    static string Run(byte[] der) {
        checked {
            var count = 0;
            for (var round = 0; round < 5000; round++) {
                using var certificate = X509CertificateLoader.LoadCertificate(der);
                count += certificate.Subject.Length + certificate.NotAfter.ToUniversalTime().Year;
            }

            return $"{count}";
        }
    }
}
