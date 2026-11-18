// The Sable runtime: validation — what bytes from outside are checked against.
// Part of sable_runtime.c, which includes every part in order (see there).

// ---- Validation ----------------------------------------------------------

// Trust nothing: every count, tag and length is checked against the end of
// the bytes before it is used, and every relative reference must point at
// the start of a value of its target's type, one the walk found.

typedef struct InlinePlace {
    int64_t at;
    const SableInlineLayout *layout;
} InlinePlace;

typedef struct InlineCheck {
    const uint8_t *base, *end;
    InlinePlace *places;
    int64_t place_count, place_capacity;
    InlineLink *links;
    const SableInlineLayout **targets;
    int64_t link_count, link_capacity;
    const char *why;
} InlineCheck;

static bool fail(InlineCheck *check, const char *why) {
    if (check->why == NULL) {
        check->why = why;
    }

    return false;
}

static bool check_value(InlineCheck *check, const SableInlineLayout *layout, const uint8_t *p, const uint8_t **out, int depth);

static bool check_scalar(InlineCheck *check, int kind, int width, const uint8_t *p) {
    if (check->end - p < width) {
        return fail(check, "truncated");
    }

    if (kind == INLINE_BOOL && p[0] > 1) {
        return fail(check, "a Bool that is neither 0 nor 1");
    }

    if (kind == INLINE_CHAR) {
        uint32_t c = read_unsigned(p, 4);
        if (c > 0x10FFFF || (c >= 0xD800 && c <= 0xDFFF)) {
            return fail(check, "a Char that is not a Unicode scalar value");
        }
    }

    return true;
}

static bool check_slot(InlineCheck *check, const SableInlineSlot *slot, const uint8_t *p, const uint8_t **out, int depth) {
    switch (slot->kind) {
        case INLINE_SCALAR:
        case INLINE_BOOL:
        case INLINE_CHAR:
            *out = p + slot->width;
            return check_scalar(check, slot->kind, slot->width, p);

        case INLINE_REF: {
            if (check->end - p < slot->width) {
                return fail(check, "truncated");
            }

            int32_t offset = slot->width == 2 ? (int16_t)read_unsigned(p, 2) : slot->width == 1 ? (int8_t)p[0] : (int32_t)read_unsigned(p, 4);
            if (offset == 0 && !slot->optional) {
                return fail(check, "a relative reference that may not be null is null");
            }

            if (offset != 0) {
                if (check->link_count == check->link_capacity) {
                    check->link_capacity = check->link_capacity < 8 ? 8 : check->link_capacity * 2;
                    check->links = realloc(check->links, (size_t)check->link_capacity * sizeof(InlineLink));
                    check->targets = realloc(check->targets, (size_t)check->link_capacity * sizeof(*check->targets));
                }

                check->links[check->link_count] = (InlineLink){ p - check->base, (p - check->base) + offset, slot->width };
                check->targets[check->link_count++] = slot->layout;
            }

            *out = p + slot->width;
            return true;
        }

        case INLINE_STRUCT:
            return check_value(check, slot->layout, p, out, depth + 1);

        default: {
            int64_t count;
            const uint8_t *element;
            if (slot->kind == INLINE_BOUNDED) {
                if (check->end - p < slot->width) {
                    return fail(check, "truncated");
                }

                count = read_unsigned(p, slot->width);
                if (count > slot->capacity) {
                    return fail(check, "an inline array longer than its bound");
                }

                element = p + slot->width;
            } else {
                count = 0;
                int i = 0;
                for (;; i++) {
                    if (i == 5) {
                        return fail(check, "a count that is not a varint");
                    }

                    if (p + i >= check->end) {
                        return fail(check, "truncated");
                    }

                    count |= (int64_t)(p[i] & 0x7F) << (7 * i);
                    if (!(p[i] & 0x80)) {
                        break;
                    }
                }

                if (count > INT32_MAX) {
                    return fail(check, "a count that is not a varint");
                }

                element = p + i + 1;
            }

            // Every element takes a byte at least, or the count is a lie.
            int64_t least = slot->element_kind == INLINE_STRUCT ? empty_size(slot->layout) : slot->element_width;
            if (least > 0 ? count > (check->end - element) / least : count > 65536) {
                return fail(check, "a count longer than the bytes");
            }

            for (int64_t i = 0; i < count; i++) {
                if (slot->element_kind == INLINE_STRUCT) {
                    if (!check_value(check, slot->layout, element, &element, depth + 1)) {
                        return false;
                    }
                } else {
                    if (!check_scalar(check, slot->element_kind, slot->element_width, element)) {
                        return false;
                    }

                    element += slot->element_width;
                }
            }

            *out = element;
            return true;
        }
    }
}

static bool check_value(InlineCheck *check, const SableInlineLayout *layout, const uint8_t *p, const uint8_t **out, int depth) {
    if (depth > 64) {
        return fail(check, "nested too deeply");
    }

    if (check->end - p < layout->prefix) {
        return fail(check, "truncated");
    }

    if (check->place_count == check->place_capacity) {
        check->place_capacity = check->place_capacity < 8 ? 8 : check->place_capacity * 2;
        check->places = realloc(check->places, (size_t)check->place_capacity * sizeof(InlinePlace));
    }

    check->places[check->place_count++] = (InlinePlace){ p - check->base, layout };

    const uint8_t *q = p + layout->prefix;
    for (int32_t i = 0; i < layout->count; i++) {
        const SableInlineSlot *slot = &layout->slots[i];
        const uint8_t *after;
        if (slot->offset >= 0) {
            if (!check_slot(check, slot, p + slot->offset, &after, depth)) {
                return false;
            }
        } else {
            if (!check_slot(check, slot, q, &q, depth)) {
                return false;
            }
        }
    }

    *out = q;
    return true;
}

static int compare_places(const void *a, const void *b) {
    const InlinePlace *x = a, *y = b;
    return x->at < y->at ? -1 : x->at > y->at ? 1 : x->layout < y->layout ? -1 : x->layout > y->layout ? 1 : 0;
}

static const SableString empty_string_object = { { &sable_string_metadata, 1, SABLE_IMMORTAL }, 0, 0 };

// Null when the block holds a valid value of the layout; why not, otherwise.
SableString *sable_inline_invalid(const SableInlineLayout *layout, const SableBlock *block) {
    InlineCheck check = { block->bytes, block->bytes + block->length, NULL, 0, 0, NULL, NULL, 0, 0, NULL };
    const uint8_t *end;
    if (check_value(&check, layout, block->bytes, &end, 0) && end != check.end) {
        fail(&check, "bytes after the value");
    }

    if (check.why == NULL) {
        qsort(check.places, (size_t)check.place_count, sizeof(InlinePlace), compare_places);
        for (int64_t i = 0; i < check.link_count && check.why == NULL; i++) {
            InlinePlace key = { check.links[i].target, check.targets[i] };
            if (key.at < 0 || key.at >= block->length
                || bsearch(&key, check.places, (size_t)check.place_count, sizeof(InlinePlace), compare_places) == NULL) {
                fail(&check, "a relative reference that does not point at a value of its type");
            }
        }
    }

    free(check.places);
    free(check.links);
    free(check.targets);
    return check.why == NULL ? (SableString *)&empty_string_object : make_cstring(check.why);
}
