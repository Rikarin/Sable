using System.Security.Cryptography;

// 100 PSS signatures and 100 of PKCS #1 v1.5, the last of which is checked.
[Benchmark("library/rsa-2048-sign")]
static class LibraryRsa2048Sign {
    // A key of this benchmark's alone.
    const string Pem = """
        -----BEGIN PRIVATE KEY-----
        MIIEvQIBADANBgkqhkiG9w0BAQEFAASCBKcwggSjAgEAAoIBAQC65MtLQOBeCx5W
        vZGBNZ1bOztPwvFP/DsDlUX7BqAabc2eFZDsAYEd1M4crZhZdTD+egAB/kYQFRSa
        vAp3NNgIe+yeeuR55klscOC8bf8e2Ro+FBoxSI7xBXCXrrVJOtWXK7yVLO7E6/Ud
        Q+4oAltd4ZPH7liumXOIJPsjUo9oNx+q2JC9UgV+mtxKCbp0DpIgV7VtyOMJnfxz
        Sv4VVeek8txtXAtncUT9FVygKBTzIFEjcwU3sTUqxcVKUSUKMQtoUkY6PebwNpkk
        Ohs9lCyRvz+JhD+W+bHiyZ9al0/yROkJEPpLkT/tMV1uJTmVehMMhyG1a9598/g3
        DFTPqzjZAgMBAAECggEAAJwxITVsrE9ndEeolbpP/fqN7R789kmvhltanT9LIxgi
        wX03v1kfE2bqYMQivWpbU6sNgUJiFcEfyhA2p40zz6jfNHe+MSN7AsMVxy4R/Tj0
        LBNGKQFCZhdbJRRv1VImbYzXiN7SgENvZfeevDn9MAus2l9q6Rh0EPnbvslc8sGZ
        58/VhUMWIrTbq0EJE4jVNnDwessE1qyZLUZKpVMcsQKQoxr31qK+x02yvkwK0+We
        eyN8z5tfyCcWkLBzivwvZUGi20iLTAK+TZoAkVRYBFJne7GMbOmu7SDAw3x6oqgD
        n2Z2tzPiXU3uRjrjQ2Cwfvi3EfXMsFHOaG0qxkWLUQKBgQD+JSNvA08JZymY7TD0
        M4BMkLXnpJEUHUA8dIQlXpZnPJQ49rr4IMZQdEFwU6UwYSk8GoKg1cWKTL920dtZ
        E0SRPdqysaQOo+I2OU1elR9uegpX3w/lDIHGcCZgai+ks4suHqVpoc4XhczPDQwf
        mwxwdOxgeiif+fz+LYGlNSsY6QKBgQC8Qf+yBzGcpK/TlBvP8h/cR9Fn6YmdPv4I
        YhsPZPJpwRyDj+s2Tk51WqwanYYKI+Qji3/7jKtkWYecWgzgYy6be/z7xKhq6MCF
        GA+3WPmWg3fThfdZdmxvUOAlCEc3dhNA7+lprQy3BuZ+ABKOaDDGA3ILY/UE4iHb
        lbVSX7cqcQKBgC+qv46ezEAzPTYVpszmq/WwXq3ny9497kJoiDCKISOT1Skp/b90
        Rj3o0C1FQwJj9tvzDPUlozthUbfzfHSkjIPBWjeR4fds/Yy2CrDmv1J1SWNZzx1i
        zyRs/IWw5Oa7Vit8TAIX+xOmYNek1tVGu3qMM5mUz/HRbYYu+tYFtuOhAoGAcnw0
        cmCblCY24xVsAH6/T2fBek70qFL5i8GVsTvzi8qre3qXURQ39Hgqt5jaPeprwA4C
        y8hVJpanuLauo/rNCjNRuw5yWWmoQohKbnbGefkDcvhCXv58nQly9jJdlOXSeags
        q15YoCmrHC8RGddU6eaIgVLNW6XlbzNQ7IJW0pECgYEAlGydPaK+OfCl2wXhBPv8
        kvDwvi+746ZUccBnKvlJHDiGe/sJJ8BBvHPexoBQV08zTc1jbYR2jgjzZkSwT2sL
        sWYzfyYtfgreZPLAF9YUkTJ6zP2FOUf+DkefXogiKZEISAccpyhmkuMn1dWWuhm0
        Xr4BpNp+0xb+YeUA8TvmAdw=
        -----END PRIVATE KEY-----
        """;

    public static Func<string> Setup() {
        checked {
            var key = RSA.Create();
            key.ImportFromPem(Pem);
            return () => Run(key);
        }
    }

    static string Run(RSA key) {
        checked {
            var message = new byte[64];
            Array.Fill(message, (byte)7);
            var signature = Array.Empty<byte>();
            for (var round = 0; round < 200; round++) {
                message[round % 64] = (byte)(round % 256);
                signature = key.SignData(message, HashAlgorithmName.SHA256, round % 2 == 0 ? RSASignaturePadding.Pss : RSASignaturePadding.Pkcs1);
            }

            return key.VerifyData(message, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1) ? "1" : "0";
        }
    }
}
