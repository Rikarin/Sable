// The Sable runtime: profiles — which function each call through a vtable, a
// witness table or a closure reached, for 'sable build --profile-generate'.
// Part of sable_runtime.c, which includes every part in order (see there).

// ---- Profiles --------------------------------------------------------------

// A build that profiles its calls counts, before each such call, the code it
// is about to call (the compiler's CallProfile.cs): at each site, the first four
// functions it reaches and how often each, and how often anything else. The
// image's table (LlvmEmitter's ProfileTables) names the sites and its
// functions; as the program ends the counts go to the file SABLE_PROFILE_FILE
// names, or the one the build named, as text that '--profile-use' reads:
//
//     sable-profile 1
//     site<TAB>key<TAB>calls
//     target<TAB>calls<TAB>function
//
// Each run writes the file anew. The counts are of every thread, counted
// without a lock: a slot is taken by a compare-and-swap, and a count added
// atomically, so a race costs at most a slot to "anything else".

#define PROFILE_TARGETS 4

typedef struct SableProfileSite {
    void *targets[PROFILE_TARGETS];
    int64_t counts[PROFILE_TARGETS];
    int64_t other;
} SableProfileSite;

typedef struct SableProfile {
    struct SableProfile *next;
    const char *path;
    int64_t site_count;
    const char *const *keys;
    SableProfileSite *sites;
    int64_t function_count;
    void *const *code;
    const char *const *names;
    int32_t registered;
} SableProfile;

static SableProfile *profiles;
static SpinLock profiles_lock;

// The function an address is the code of, or NULL.
static const char *profile_name(const SableProfile *profile, void *code) {
    for (int64_t i = 0; i < profile->function_count; i++) {
        if (profile->code[i] == code) {
            return profile->names[i];
        }
    }

    return NULL;
}

static void profile_write_one(const SableProfile *profile) {
    const char *path = getenv("SABLE_PROFILE_FILE");
    if (path == NULL || path[0] == '\0') {
        path = profile->path;
    }

    if (path == NULL || path[0] == '\0') {
        return;
    }

    // Binary, as io.c writes: in text mode Windows would write each newline as CRLF.
    FILE *file = open_file(path, "wb");
    if (file == NULL) {
        fprintf(stderr, "sable: cannot write the profile '%s'\n", path);
        return;
    }

    fputs("sable-profile 1\n", file);
    for (int64_t s = 0; s < profile->site_count; s++) {
        const SableProfileSite *site = &profile->sites[s];
        int64_t total = __atomic_load_n(&site->other, __ATOMIC_RELAXED);
        for (int t = 0; t < PROFILE_TARGETS; t++) {
            total += __atomic_load_n(&site->counts[t], __ATOMIC_RELAXED);
        }

        if (total == 0) {
            continue;
        }

        fprintf(file, "site\t%s\t%lld\n", profile->keys[s], (long long)total);
        for (int t = 0; t < PROFILE_TARGETS; t++) {
            void *target = __atomic_load_n(&site->targets[t], __ATOMIC_RELAXED);
            const char *name = target == NULL ? NULL : profile_name(profile, target);
            if (name != NULL) {
                fprintf(file, "target\t%lld\t%s\n", (long long)__atomic_load_n(&site->counts[t], __ATOMIC_RELAXED), name);
            }
        }
    }

    fclose(file);
}

static void profile_write(void) {
    spin_lock(&profiles_lock);
    SableProfile *all = profiles;
    profiles = NULL;
    spin_unlock(&profiles_lock);
    for (SableProfile *profile = all; profile != NULL; profile = profile->next) {
        profile_write_one(profile);
    }
}

void sable_profile_count(SableProfile *profile, int64_t index, void *target) {
    if (__builtin_expect(__atomic_load_n(&profile->registered, __ATOMIC_ACQUIRE) == 0, 0)) {
        spin_lock(&profiles_lock);
        if (profile->registered == 0) {
            if (profiles == NULL) {
                static bool written;
                if (!written) {
                    written = true;
                    atexit(profile_write);
                }
            }

            profile->next = profiles;
            profiles = profile;
            __atomic_store_n(&profile->registered, 1, __ATOMIC_RELEASE);
        }

        spin_unlock(&profiles_lock);
    }

    SableProfileSite *site = &profile->sites[index];
    for (int t = 0; t < PROFILE_TARGETS; t++) {
        void *seen = __atomic_load_n(&site->targets[t], __ATOMIC_RELAXED);
        if (seen == NULL) {
            void *expected = NULL;
            if (__atomic_compare_exchange_n(&site->targets[t], &expected, target, false, __ATOMIC_RELAXED, __ATOMIC_RELAXED)) {
                seen = target;
            } else {
                seen = expected;
            }
        }

        if (seen == target) {
            __atomic_fetch_add(&site->counts[t], 1, __ATOMIC_RELAXED);
            return;
        }
    }

    __atomic_fetch_add(&site->other, 1, __ATOMIC_RELAXED);
}
