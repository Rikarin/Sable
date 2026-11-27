// The Sable runtime: lines — a backtrace's frames named by the line of the
// source they are at, from the program's own DWARF line table.
// Part of sable_runtime.c, which includes every part in order (see there).
//
// A debug build (-g) describes every instruction's place in the source in
// its line table, .debug_line: the table a debugger reads. On macOS it is
// in the .dSYM beside the program, which clang's dsymutil writes; on Linux,
// in the program itself. The platform layer finds it (line_sections) and
// this reads it, run as the small program DWARF writes it in, until the row
// for the address asked about. No library does the reading, and nothing is
// run beside the program, so a trap names its lines wherever it stops.
//
// Windows describes a debug build in CodeView, in the PDB, which the
// platform layer reads through DbgHelp instead; WASI has no frames to name.
// An optimised build without -g has no line table: its frames are named by
// symbol and offset, as they always were.

#if !defined(_WIN32) && !defined(__wasi__)

// What a cursor over a section reads; 'end' is past its last byte, and a
// read past it leaves the cursor at the end, failed.
typedef struct LineCursor {
    const uint8_t *at, *end;
    bool failed;
} LineCursor;

static uint64_t line_read(LineCursor *cursor, int size) {
    if (cursor->end - cursor->at < size) {
        cursor->at = cursor->end;
        cursor->failed = true;
        return 0;
    }

    uint64_t value = 0;
    for (int i = 0; i < size; i++) {
        value |= (uint64_t)cursor->at[i] << (8 * i);
    }

    cursor->at += size;
    return value;
}

static uint64_t line_uleb(LineCursor *cursor) {
    uint64_t value = 0;
    for (int shift = 0; cursor->at < cursor->end; shift += 7) {
        uint8_t byte = *cursor->at++;
        if (shift < 64) {
            value |= (uint64_t)(byte & 0x7f) << shift;
        }

        if ((byte & 0x80) == 0) {
            return value;
        }
    }

    cursor->failed = true;
    return value;
}

static int64_t line_sleb(LineCursor *cursor) {
    int64_t value = 0;
    int shift = 0;
    uint8_t byte = 0x80;
    while ((byte & 0x80) != 0 && cursor->at < cursor->end) {
        byte = *cursor->at++;
        if (shift < 64) {
            value |= (int64_t)(byte & 0x7f) << shift;
        }

        shift += 7;
    }

    if ((byte & 0x80) != 0) {
        cursor->failed = true;
    } else if (shift < 64 && (byte & 0x40) != 0) {
        value |= -((int64_t)1 << shift);
    }

    return value;
}

static void line_skip(LineCursor *cursor, uint64_t size) {
    if ((uint64_t)(cursor->end - cursor->at) < size) {
        cursor->at = cursor->end;
        cursor->failed = true;
    } else {
        cursor->at += size;
    }
}

// DWARF's names for what a header's entries hold, and how.
enum {
    LNCT_PATH = 1,
    LNCT_DIRECTORY_INDEX = 2,
    FORM_BLOCK = 0x09,
    FORM_DATA1 = 0x0b,
    FORM_DATA2 = 0x05,
    FORM_DATA4 = 0x06,
    FORM_DATA8 = 0x07,
    FORM_DATA16 = 0x1e,
    FORM_STRING = 0x08,
    FORM_STRP = 0x0e,
    FORM_LINE_STRP = 0x1f,
    FORM_UDATA = 0x0f,
};

// One value of an entry, read as its form says: a string as 'text', a
// number as 'number'. False for a form the reader does not know.
static bool line_form(LineCursor *cursor, uint64_t form, int offset_size, const LineSections *sections, const char **text, uint64_t *number) {
    *text = NULL;
    *number = 0;
    switch (form) {
        case FORM_STRING: {
            const char *start = (const char *)cursor->at;
            const uint8_t *nul = memchr(cursor->at, 0, (size_t)(cursor->end - cursor->at));
            if (nul == NULL) {
                cursor->failed = true;
                return false;
            }

            cursor->at = nul + 1;
            *text = start;
            return true;
        }

        case FORM_LINE_STRP:
            *text = line_string_at(sections->line_str, sections->line_str_size, line_read(cursor, offset_size));
            return true;
        case FORM_STRP:
            *text = line_string_at(sections->str, sections->str_size, line_read(cursor, offset_size));
            return true;
        case FORM_UDATA:
            *number = line_uleb(cursor);
            return true;
        case FORM_DATA1:
            *number = line_read(cursor, 1);
            return true;
        case FORM_DATA2:
            *number = line_read(cursor, 2);
            return true;
        case FORM_DATA4:
            *number = line_read(cursor, 4);
            return true;
        case FORM_DATA8:
            *number = line_read(cursor, 8);
            return true;
        case FORM_DATA16:
            line_skip(cursor, 16);
            return true;
        case FORM_BLOCK:
            line_skip(cursor, line_uleb(cursor));
            return true;
        default:
            return false;
    }
}

// A version 5 header's table of directories or of files: the path and
// directory of entry 'wanted' (or of none, when it is past the end), the
// cursor left after the table.
static bool line_entries(LineCursor *cursor, int offset_size, const LineSections *sections, uint64_t wanted, const char **path, uint64_t *directory) {
    uint64_t formats[16][2];
    int format_count = (int)line_read(cursor, 1);
    if (format_count > 16) {
        return false;
    }

    for (int i = 0; i < format_count; i++) {
        formats[i][0] = line_uleb(cursor);
        formats[i][1] = line_uleb(cursor);
    }

    uint64_t count = line_uleb(cursor);
    for (uint64_t entry = 0; entry < count && !cursor->failed; entry++) {
        for (int i = 0; i < format_count; i++) {
            const char *text;
            uint64_t number;
            if (!line_form(cursor, formats[i][1], offset_size, sections, &text, &number)) {
                return false;
            }

            if (entry == wanted && formats[i][0] == LNCT_PATH) {
                *path = text;
            } else if (entry == wanted && formats[i][0] == LNCT_DIRECTORY_INDEX) {
                *directory = number;
            }
        }
    }

    return !cursor->failed;
}

// A version 4 header's directories, or its files: strings until an empty
// one, a file's with its directory, time and size after it.
static bool line_entries_v4(LineCursor *cursor, bool files, uint64_t wanted, const char **path, uint64_t *directory) {
    for (uint64_t entry = 0;; entry++) {
        const char *start = (const char *)cursor->at;
        const uint8_t *nul = memchr(cursor->at, 0, (size_t)(cursor->end - cursor->at));
        if (nul == NULL) {
            return false;
        }

        cursor->at = nul + 1;
        if (*start == '\0') {
            return true;
        }

        uint64_t index = files ? line_uleb(cursor) : 0;
        if (files) {
            line_uleb(cursor);
            line_uleb(cursor);
        }

        if (entry == wanted) {
            *path = start;
            *directory = index;
        }
    }
}

// The file a row names, as a trap's place names it: by its name, under the
// directory it is in when that is relative ('lib/Sable.Core/Array.sb');
// the program's own, in the directory it was compiled in, by name alone.
static bool line_file(const uint8_t *header, const uint8_t *end, int version, int offset_size, const LineSections *sections, uint64_t file, char *text, size_t size) {
    LineCursor cursor = { header, end, false };
    const char *name = NULL, *directory_name = NULL;
    uint64_t directory = 0, ignored = 0;
    if (version >= 5) {
        if (!line_entries(&cursor, offset_size, sections, UINT64_MAX, &directory_name, &ignored)) {
            return false;
        }

        if (!line_entries(&cursor, offset_size, sections, file, &name, &directory)) {
            return false;
        }

        cursor.at = header;
        cursor.failed = false;
        if (directory != 0 && !line_entries(&cursor, offset_size, sections, directory, &directory_name, &ignored)) {
            return false;
        }
    } else {
        // Version 4 counts files and directories from 1; 0 is the
        // compilation's own directory.
        if (file == 0) {
            return false;
        }

        if (!line_entries_v4(&cursor, false, UINT64_MAX, &directory_name, &ignored)) {
            return false;
        }

        if (!line_entries_v4(&cursor, true, file - 1, &name, &directory)) {
            return false;
        }

        cursor.at = header;
        if (directory != 0 && !line_entries_v4(&cursor, false, directory - 1, &directory_name, &ignored)) {
            return false;
        }
    }

    if (name == NULL) {
        return false;
    }

    if (directory != 0 && directory_name != NULL && directory_name[0] != '/' && name[0] != '/') {
        snprintf(text, size, "%s/%s", directory_name, name);
    } else {
        const char *slash = strrchr(name, '/');
        snprintf(text, size, "%s", slash != NULL && directory == 0 ? slash + 1 : name);
    }

    return true;
}

// One unit's line program run until the row holding 'address': its file,
// line and column. False when the unit has no such row.
static bool line_unit(LineCursor *unit, const LineSections *sections, uint64_t address, char *place, size_t size) {
    uint64_t length = line_read(unit, 4);
    int offset_size = 4;
    if (length == 0xffffffff) {
        length = line_read(unit, 8);
        offset_size = 8;
    }

    if (unit->failed || length > (uint64_t)(unit->end - unit->at)) {
        unit->at = unit->end;
        return false;
    }

    LineCursor cursor = { unit->at, unit->at + length, false };
    unit->at += length;

    int version = (int)line_read(&cursor, 2);
    if (version < 2 || version > 5) {
        return false;
    }

    int address_size = 8;
    if (version >= 5) {
        address_size = (int)line_read(&cursor, 1);
        line_read(&cursor, 1); // the segment selector's size
    }

    uint64_t header_length = line_read(&cursor, offset_size);
    const uint8_t *program = cursor.at + header_length;
    if (cursor.failed || header_length > (uint64_t)(cursor.end - cursor.at)) {
        return false;
    }

    int minimum_length = (int)line_read(&cursor, 1);
    if (version >= 4) {
        line_read(&cursor, 1); // operations per instruction: 1, but on VLIW
    }

    bool default_is_stmt = line_read(&cursor, 1) != 0;
    int line_base = (int8_t)line_read(&cursor, 1);
    int line_range = (int)line_read(&cursor, 1);
    int opcode_base = (int)line_read(&cursor, 1);
    uint8_t lengths[256] = { 0 };
    for (int i = 1; i < opcode_base; i++) {
        lengths[i] = (uint8_t)line_read(&cursor, 1);
    }

    if (cursor.failed || line_range == 0) {
        return false;
    }

    const uint8_t *entries = cursor.at;
    cursor.at = program;

    // The rows, each against the one before it in its sequence: the row
    // before the first past 'address' holds it.
    uint64_t row_address = 0, file = 1, line = 1, column = 0;
    bool started = false;
    uint64_t previous_address = 0, previous_file = 0, previous_line = 0, previous_column = 0;
    bool found = false;
    uint64_t found_file = 0, found_line = 0, found_column = 0;
    (void)default_is_stmt;

#define LINE_ROW()                                                                            \
    do {                                                                                      \
        if (started && previous_address <= address && address < row_address) {                \
            found = true;                                                                     \
            found_file = previous_file;                                                       \
            found_line = previous_line;                                                       \
            found_column = previous_column;                                                   \
        }                                                                                     \
        started = true;                                                                       \
        previous_address = row_address;                                                       \
        previous_file = file;                                                                 \
        previous_line = line;                                                                 \
        previous_column = column;                                                             \
    } while (0)

    while (!found && cursor.at < cursor.end && !cursor.failed) {
        int opcode = (int)line_read(&cursor, 1);
        if (opcode >= opcode_base) {
            int adjusted = opcode - opcode_base;
            row_address += (uint64_t)(adjusted / line_range) * (uint64_t)minimum_length;
            line += (uint64_t)(int64_t)(line_base + adjusted % line_range);
            LINE_ROW();
            continue;
        }

        switch (opcode) {
            case 0: { // extended
                uint64_t extended = line_uleb(&cursor);
                if (extended == 0 || extended > (uint64_t)(cursor.end - cursor.at)) {
                    cursor.failed = true;
                    break;
                }

                const uint8_t *next = cursor.at + extended;
                int sub = (int)line_read(&cursor, 1);
                if (sub == 1) { // the end of a sequence: a row past its last
                    LINE_ROW();
                    started = false;
                    row_address = 0;
                    file = 1;
                    line = 1;
                    column = 0;
                } else if (sub == 2) { // an address
                    row_address = line_read(&cursor, address_size);
                }

                cursor.at = next;
                break;
            }

            case 1: // copy
                LINE_ROW();
                break;
            case 2: // advance the address
                row_address += line_uleb(&cursor) * (uint64_t)minimum_length;
                break;
            case 3: // advance the line
                line += (uint64_t)line_sleb(&cursor);
                break;
            case 4:
                file = line_uleb(&cursor);
                break;
            case 5:
                column = line_uleb(&cursor);
                break;
            case 8: // the address a special opcode 255 would advance by
                row_address += (uint64_t)((255 - opcode_base) / line_range) * (uint64_t)minimum_length;
                break;
            case 9:
                row_address += line_read(&cursor, 2);
                break;
            default:
                for (int i = 0; i < lengths[opcode]; i++) {
                    line_uleb(&cursor);
                }

                break;
        }
    }

#undef LINE_ROW

    // Line 0 is code that is no line's: a place none of its own.
    if (!found || found_line == 0) {
        return false;
    }

    char file_name[256];
    if (!line_file(entries, program, version, offset_size, sections, found_file, file_name, sizeof file_name)) {
        return false;
    }

    if (found_column != 0) {
        snprintf(place, size, "%s:%llu:%llu", file_name, (unsigned long long)found_line, (unsigned long long)found_column);
    } else {
        snprintf(place, size, "%s:%llu", file_name, (unsigned long long)found_line);
    }

    return true;
}

// The program's line table, found once (line_sections), by whichever
// thread first asks: 0 not yet looked for, 1 being looked for, 2 found, 3
// there is none.
static int line_state;
static LineSections line_table;

// The place in the source of the instruction at 'at' (a return address
// less one: in the call), as 'file:line:column'; false where the program
// has no line table, or the address is not in the program itself
// ('image', where the system's dladdr says it is).
static bool frame_place(const void *at, const void *image, char *place, size_t size) {
    int state = __atomic_load_n(&line_state, __ATOMIC_ACQUIRE);
    if (state == 0) {
        int expected = 0;
        if (__atomic_compare_exchange_n(&line_state, &expected, 1, false, __ATOMIC_ACQ_REL, __ATOMIC_ACQUIRE)) {
            state = line_sections(&line_table) ? 2 : 3;
            __atomic_store_n(&line_state, state, __ATOMIC_RELEASE);
        }
    }

    while ((state = __atomic_load_n(&line_state, __ATOMIC_ACQUIRE)) == 1) {
        thread_yield();
    }

    // glibc's dladdr gives an image's first mapping, musl's its load bias.
    if (state != 2 || (image != line_table.image && image != (const void *)line_table.slide)) {
        return false;
    }

    uint64_t address = (uint64_t)((uintptr_t)at - (uintptr_t)line_table.slide);
    LineCursor units = { line_table.line, line_table.line + line_table.line_size, false };
    while (units.at < units.end && !units.failed) {
        if (line_unit(&units, &line_table, address, place, size)) {
            return true;
        }
    }

    return false;
}

#endif
