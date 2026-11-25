// The system's trusted certificates, for Sable.Crypto.X509's TrustStore: on
// macOS the Security framework's anchors and the users' and the
// administrator's settings, on Windows the ROOT and Disallowed stores. The
// libraries are loaded only here, so that a program that does not ask links
// with neither. Elsewhere there are none, and the library reads the system's
// files.
//
// They are given as one block: how many bytes follow, as a uint64_t, and for
// each certificate a byte, 1 for one that is trusted and 0 for one that must
// not be, how many bytes it has, as a uint32_t, and those, in DER.

typedef struct {
    unsigned char *bytes;
    size_t count;
    size_t capacity;
} TrustBlock;

#if defined(__APPLE__) || defined(_WIN32)

static bool trust_append(TrustBlock *block, bool trusted, const void *der, size_t length) {
    if (length > 1 << 20) {
        return true;
    }

    size_t wanted = block->count + 5 + length;
    if (wanted > block->capacity) {
        size_t capacity = block->capacity < 1 << 16 ? 1 << 17 : block->capacity * 2;
        while (capacity < wanted) {
            capacity *= 2;
        }

        unsigned char *grown = realloc(block->bytes, capacity);
        if (grown == NULL) {
            return false;
        }

        block->bytes = grown;
        block->capacity = capacity;
    }

    uint32_t size = (uint32_t)length;
    block->bytes[block->count] = trusted ? 1 : 0;
    memcpy(block->bytes + block->count + 1, &size, 4);
    memcpy(block->bytes + block->count + 5, der, length);
    block->count = wanted;
    return true;
}

#endif

#if defined(__APPLE__)

#include <TargetConditionals.h>

#if TARGET_OS_OSX

#include <dlfcn.h>

typedef const void *TrustRef;

// What is called of the two frameworks.
typedef struct {
    void (*release)(TrustRef);
    long (*array_count)(TrustRef);
    TrustRef (*array_at)(TrustRef, long);
    TrustRef (*dictionary_value)(TrustRef, TrustRef);
    long (*data_length)(TrustRef);
    const unsigned char *(*data_bytes)(TrustRef);
    unsigned char (*number_value)(TrustRef, long, void *);
    unsigned char (*equal)(TrustRef, TrustRef);
    TrustRef (*string)(TrustRef, const char *, uint32_t);
    int (*anchors)(TrustRef *);
    int (*settings_certificates)(uint32_t, TrustRef *);
    int (*settings)(TrustRef, uint32_t, TrustRef *);
    TrustRef (*certificate_data)(TrustRef);
    TrustRef (*policy_properties)(TrustRef);
    TrustRef result_key, policy_key, policy_oid_key, ssl_oid;
} TrustCalls;

static bool trust_calls(TrustCalls *calls) {
    void *cf = dlopen("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation", RTLD_LAZY | RTLD_LOCAL);
    void *security = dlopen("/System/Library/Frameworks/Security.framework/Security", RTLD_LAZY | RTLD_LOCAL);
    if (cf == NULL || security == NULL) {
        return false;
    }

    struct { void **function; void *library; const char *name; } wanted[] = {
        {(void **)&calls->release, cf, "CFRelease"},
        {(void **)&calls->array_count, cf, "CFArrayGetCount"},
        {(void **)&calls->array_at, cf, "CFArrayGetValueAtIndex"},
        {(void **)&calls->dictionary_value, cf, "CFDictionaryGetValue"},
        {(void **)&calls->data_length, cf, "CFDataGetLength"},
        {(void **)&calls->data_bytes, cf, "CFDataGetBytePtr"},
        {(void **)&calls->number_value, cf, "CFNumberGetValue"},
        {(void **)&calls->equal, cf, "CFEqual"},
        {(void **)&calls->string, cf, "CFStringCreateWithCString"},
        {(void **)&calls->anchors, security, "SecTrustCopyAnchorCertificates"},
        {(void **)&calls->settings_certificates, security, "SecTrustSettingsCopyCertificates"},
        {(void **)&calls->settings, security, "SecTrustSettingsCopyTrustSettings"},
        {(void **)&calls->certificate_data, security, "SecCertificateCopyData"},
        {(void **)&calls->policy_properties, security, "SecPolicyCopyProperties"},
    };
    for (size_t i = 0; i < sizeof wanted / sizeof wanted[0]; i++) {
        *wanted[i].function = dlsym(wanted[i].library, wanted[i].name);
        if (*wanted[i].function == NULL) {
            return false;
        }
    }

    // Constants of the headers, but for two whose strings are theirs.
    const uint32_t utf8 = 0x08000100;
    calls->result_key = calls->string(NULL, "kSecTrustSettingsResult", utf8);
    calls->policy_key = calls->string(NULL, "kSecTrustSettingsPolicy", utf8);
    calls->policy_oid_key = calls->string(NULL, "SecPolicyOid", utf8);
    calls->ssl_oid = calls->string(NULL, "1.2.840.113635.100.1.3", utf8);
    return calls->result_key != NULL && calls->policy_key != NULL && calls->policy_oid_key != NULL && calls->ssl_oid != NULL;
}

// What the settings of a domain say of a certificate: 1 for trusted for TLS,
// 0 for not to be, -1 for nothing. None at all, where there are settings, is
// trusted for everything.
static int trust_verdict(const TrustCalls *calls, TrustRef certificate, uint32_t domain) {
    TrustRef settings = NULL;
    if (calls->settings(certificate, domain, &settings) != 0 || settings == NULL) {
        return -1;
    }

    long count = calls->array_count(settings);
    int verdict = count == 0 ? 1 : -1;
    for (long i = 0; i < count; i++) {
        TrustRef entry = calls->array_at(settings, i);
        TrustRef policy = calls->dictionary_value(entry, calls->policy_key);
        if (policy != NULL) {
            TrustRef properties = calls->policy_properties(policy);
            TrustRef oid = properties == NULL ? NULL : calls->dictionary_value(properties, calls->policy_oid_key);
            bool is_ssl = oid != NULL && calls->equal(oid, calls->ssl_oid) != 0;
            if (properties != NULL) {
                calls->release(properties);
            }

            if (!is_ssl) {
                continue;
            }
        }

        // kSecTrustSettingsResultTrustRoot, where it does not say; a
        // kCFNumberSInt32Type.
        int32_t result = 1;
        TrustRef number = calls->dictionary_value(entry, calls->result_key);
        if (number != NULL) {
            calls->number_value(number, 3, &result);
        }

        if (result == 3) {
            verdict = 0;
            break;
        }

        if (result == 1 || result == 2) {
            verdict = 1;
        }
    }

    calls->release(settings);
    return verdict;
}

static bool trust_add(const TrustCalls *calls, TrustBlock *block, TrustRef certificate, bool trusted) {
    TrustRef data = calls->certificate_data(certificate);
    if (data == NULL) {
        return true;
    }

    bool added = trust_append(block, trusted, calls->data_bytes(data), (size_t)calls->data_length(data));
    calls->release(data);
    return added;
}

static bool trust_load(TrustBlock *block) {
    TrustCalls calls;
    if (!trust_calls(&calls)) {
        return false;
    }

    bool good = true;
    TrustRef all = NULL;
    if (calls.anchors(&all) == 0 && all != NULL) {
        long count = calls.array_count(all);
        for (long i = 0; i < count && good; i++) {
            good = trust_add(&calls, block, calls.array_at(all, i), true);
        }

        calls.release(all);
    }

    // kSecTrustSettingsDomainUser and kSecTrustSettingsDomainAdmin.
    for (uint32_t domain = 0; domain < 2 && good; domain++) {
        all = NULL;
        if (calls.settings_certificates(domain, &all) != 0 || all == NULL) {
            continue;
        }

        long count = calls.array_count(all);
        for (long i = 0; i < count && good; i++) {
            TrustRef certificate = calls.array_at(all, i);
            int verdict = trust_verdict(&calls, certificate, domain);
            if (verdict >= 0) {
                good = trust_add(&calls, block, certificate, verdict == 1);
            }
        }

        calls.release(all);
    }

    calls.release(calls.result_key);
    calls.release(calls.policy_key);
    calls.release(calls.policy_oid_key);
    calls.release(calls.ssl_oid);
    return good;
}

#else

static bool trust_load(TrustBlock *block) {
    (void)block;
    return false;
}

#endif

#elif defined(_WIN32)

#include <wincrypt.h>

typedef struct {
    HCERTSTORE (WINAPI *open)(HCRYPTPROV_LEGACY, LPCWSTR);
    PCCERT_CONTEXT (WINAPI *next)(HCERTSTORE, PCCERT_CONTEXT);
    BOOL (WINAPI *close)(HCERTSTORE, DWORD);
    BOOL (WINAPI *usage)(PCCERT_CONTEXT, DWORD, PCERT_ENHKEY_USAGE, DWORD *);
} TrustCalls;

// Whether the certificate is for TLS servers: for everything, unless it, or
// the administrator, lists what it is for.
static bool trust_serves(const TrustCalls *calls, PCCERT_CONTEXT certificate) {
    union {
        CERT_ENHKEY_USAGE usage;
        unsigned char bytes[4096];
    } found;
    DWORD size = sizeof found;
    if (!calls->usage(certificate, 0, &found.usage, &size)) {
        return GetLastError() == (DWORD)CRYPT_E_NOT_FOUND;
    }

    if (found.usage.cUsageIdentifier == 0) {
        return GetLastError() == (DWORD)CRYPT_E_NOT_FOUND;
    }

    for (DWORD i = 0; i < found.usage.cUsageIdentifier; i++) {
        if (strcmp(found.usage.rgpszUsageIdentifier[i], "1.3.6.1.5.5.7.3.1") == 0 || strcmp(found.usage.rgpszUsageIdentifier[i], "2.5.29.37.0") == 0) {
            return true;
        }
    }

    return false;
}

static bool trust_load(TrustBlock *block) {
    HMODULE library = LoadLibraryExW(L"crypt32.dll", NULL, LOAD_LIBRARY_SEARCH_SYSTEM32);
    if (library == NULL) {
        return false;
    }

    TrustCalls calls;
    *(FARPROC *)&calls.open = GetProcAddress(library, "CertOpenSystemStoreW");
    *(FARPROC *)&calls.next = GetProcAddress(library, "CertEnumCertificatesInStore");
    *(FARPROC *)&calls.close = GetProcAddress(library, "CertCloseStore");
    *(FARPROC *)&calls.usage = GetProcAddress(library, "CertGetEnhancedKeyUsage");
    if (calls.open == NULL || calls.next == NULL || calls.close == NULL || calls.usage == NULL) {
        return false;
    }

    bool good = true;
    for (int i = 0; i < 2 && good; i++) {
        HCERTSTORE store = calls.open(0, i == 0 ? L"ROOT" : L"Disallowed");
        if (store == NULL) {
            continue;
        }

        PCCERT_CONTEXT certificate = NULL;
        while ((certificate = calls.next(store, certificate)) != NULL) {
            if ((certificate->dwCertEncodingType & X509_ASN_ENCODING) != 0 && (i == 1 || trust_serves(&calls, certificate))) {
                // The last one is let go of by asking for the next.
                good = good && trust_append(block, i == 0, certificate->pbCertEncoded, certificate->cbCertEncoded);
            }
        }

        calls.close(store, 0);
    }

    return good;
}

#else

static bool trust_load(TrustBlock *block) {
    (void)block;
    return false;
}

#endif

// The block's address, to be freed with sable_trust_free, or 0 for none.
int64_t sable_trust_load(void) {
    TrustBlock block = {NULL, sizeof(uint64_t), 0};
    if (!trust_load(&block) || block.bytes == NULL) {
        free(block.bytes);
        return 0;
    }

    uint64_t count = block.count - sizeof count;
    memcpy(block.bytes, &count, sizeof count);
    return (int64_t)(intptr_t)block.bytes;
}

void sable_trust_free(int64_t address) {
    free((void *)(intptr_t)address);
}
