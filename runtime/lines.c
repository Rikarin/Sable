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

// A NUL-terminated string in a section, or NULL past its end.
static const char *line_string_at(const uint8_t *section, size_t size, uint64_t offset) {
    if (section == NULL || offset >= size || memchr(section + offset, 0, size - offset) == NULL) {
        return NULL;
    }

    return (const char *)section + offset;
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


// ---- Functions by their DWARF (#300) ----------------------------------------
//
// A program may have its local symbols stripped (a profile's strip =
// "local"): a frame in one of its functions then has no symbol of its own,
// and dladdr names it by the nearest one that is left, before it. Its name
// is in the debug information all the same: the DW_TAG_subprogram whose
// addresses hold it, in .debug_info, its attributes read as .debug_abbrev
// says, of DWARF 4 or 5.

enum {
    TAG_SUBPROGRAM = 0x2e,
    AT_NAME = 0x03,
    AT_LOW_PC = 0x11,
    AT_HIGH_PC = 0x12,
    AT_ABSTRACT_ORIGIN = 0x31,
    AT_SPECIFICATION = 0x47,
    AT_STR_OFFSETS_BASE = 0x72,
    AT_ADDR_BASE = 0x73,
};

// One attribute's value, as its form says: an address, a number, an
// offset or a string; 'kind' says which: 0 none, 1 a number (or an index,
// for strx and addrx: 'index' set), 2 a string, 3 an address.
typedef struct DwarfValue {
    int kind;
    bool index;
    uint64_t number;
    const char *text;
} DwarfValue;

typedef struct DwarfUnit {
    const uint8_t *start; // the unit's header
    int offset_size, address_size, version;
    uint64_t str_offsets_base, addr_base;
} DwarfUnit;

static bool dwarf_value(LineCursor *cursor, uint64_t form, int64_t implicit, const DwarfUnit *unit, const LineSections *sections, DwarfValue *value) {
    value->kind = 1;
    value->index = false;
    value->number = 0;
    value->text = NULL;
    switch (form) {
    case 0x01: // addr
        value->kind = 3;
        value->number = line_read(cursor, unit->address_size);
        return true;
    case 0x1b: // addrx
        value->kind = 3;
        value->index = true;
        value->number = line_uleb(cursor);
        return true;
    case 0x29: case 0x2a: case 0x2b: case 0x2c: // addrx1, 2, 3, 4
        value->kind = 3;
        value->index = true;
        value->number = line_read(cursor, form == 0x29 ? 1 : form == 0x2a ? 2 : form == 0x2b ? 3 : 4);
        return true;
    case 0x08: // string
    case 0x0e: // strp
    case 0x1f: // line_strp
        if (!line_form(cursor, form, unit->offset_size, sections, &value->text, &value->number)) {
            return false;
        }

        value->kind = 2;
        return true;
    case 0x1a: // strx
        value->kind = 2;
        value->index = true;
        value->number = line_uleb(cursor);
        return true;
    case 0x25: case 0x26: case 0x27: case 0x28: // strx1, 2, 3, 4
        value->kind = 2;
        value->index = true;
        value->number = line_read(cursor, form == 0x25 ? 1 : form == 0x26 ? 2 : form == 0x27 ? 3 : 4);
        return true;
    case 0x0b: value->number = line_read(cursor, 1); return true;  // data1
    case 0x05: value->number = line_read(cursor, 2); return true;  // data2
    case 0x06: value->number = line_read(cursor, 4); return true;  // data4
    case 0x07: value->number = line_read(cursor, 8); return true;  // data8
    case 0x0d: value->number = (uint64_t)line_sleb(cursor); return true; // sdata
    case 0x0f: value->number = line_uleb(cursor); return true;     // udata
    case 0x11: value->number = line_read(cursor, 1); return true;  // ref1
    case 0x12: value->number = line_read(cursor, 2); return true;  // ref2
    case 0x13: value->number = line_read(cursor, 4); return true;  // ref4
    case 0x14: value->number = line_read(cursor, 8); return true;  // ref8
    case 0x15: value->number = line_uleb(cursor); return true;     // ref_udata
    case 0x0c: value->number = line_read(cursor, 1); return true;  // flag
    case 0x19: value->number = 1; return true;                     // flag_present
    case 0x21: value->number = (uint64_t)implicit; return true;    // implicit_const
    case 0x10: case 0x17: // ref_addr, sec_offset
        value->number = line_read(cursor, unit->offset_size);
        return true;
    case 0x22: case 0x23: // loclistx, rnglistx
        value->number = line_uleb(cursor);
        return true;
    case 0x1c: line_skip(cursor, 4); value->kind = 0; return true; // ref_sup4
    case 0x20: case 0x24: line_skip(cursor, 8); value->kind = 0; return true; // ref_sig8, ref_sup8
    case 0x1e: line_skip(cursor, 16); value->kind = 0; return true; // data16
    case 0x0a: line_skip(cursor, line_read(cursor, 1)); value->kind = 0; return true; // block1
    case 0x03: line_skip(cursor, line_read(cursor, 2)); value->kind = 0; return true; // block2
    case 0x04: line_skip(cursor, line_read(cursor, 4)); value->kind = 0; return true; // block4
    case 0x09: case 0x18: line_skip(cursor, line_uleb(cursor)); value->kind = 0; return true; // block, exprloc
    case 0x16: { // indirect
        uint64_t actual = line_uleb(cursor);
        return actual != 0x16 && dwarf_value(cursor, actual, 0, unit, sections, value);
    }
    default:
        return false;
    }
}

// A string or an address that a value names by its index, looked up.
static const char *dwarf_text(const DwarfValue *value, const DwarfUnit *unit, const LineSections *sections) {
    if (value->kind != 2 || !value->index) {
        return value->kind == 2 ? value->text : NULL;
    }

    uint64_t at = unit->str_offsets_base + value->number * (uint64_t)unit->offset_size;
    if (sections->str_offsets == NULL || at + (uint64_t)unit->offset_size > sections->str_offsets_size) {
        return NULL;
    }

    LineCursor cursor = { sections->str_offsets + at, sections->str_offsets + sections->str_offsets_size, false };
    return line_string_at(sections->str, sections->str_size, line_read(&cursor, unit->offset_size));
}

static bool dwarf_address(const DwarfValue *value, const DwarfUnit *unit, const LineSections *sections, uint64_t *address) {
    if (value->kind != 3) {
        return false;
    }

    if (!value->index) {
        *address = value->number;
        return true;
    }

    uint64_t at = unit->addr_base + value->number * (uint64_t)unit->address_size;
    if (sections->addr == NULL || at + (uint64_t)unit->address_size > sections->addr_size) {
        return false;
    }

    LineCursor cursor = { sections->addr + at, sections->addr + sections->addr_size, false };
    *address = line_read(&cursor, unit->address_size);
    return true;
}

// The abbreviation 'code' of the table at 'offset': a cursor at its tag.
static bool dwarf_abbreviation(const LineSections *sections, uint64_t offset, uint64_t code, LineCursor *found) {
    if (offset >= sections->abbrev_size) {
        return false;
    }

    LineCursor cursor = { sections->abbrev + offset, sections->abbrev + sections->abbrev_size, false };
    while (!cursor.failed) {
        uint64_t each = line_uleb(&cursor);
        if (each == 0) {
            return false;
        }

        if (each == code) {
            *found = cursor;
            return true;
        }

        line_uleb(&cursor); // the tag
        line_skip(&cursor, 1); // whether it has children
        for (;;) {
            uint64_t attribute = line_uleb(&cursor), form = line_uleb(&cursor);
            if (form == 0x21) {
                line_sleb(&cursor);
            }

            if ((attribute == 0 && form == 0) || cursor.failed) {
                break;
            }
        }
    }

    return false;
}

// One DIE read at 'cursor', which is left after it: its tag, whether it has
// children, and the attributes asked about. False at the end of a list of
// children (a 0 code) — 'tag' 0 — or where it cannot be read.
typedef struct DwarfDie {
    uint64_t tag;
    bool children;
    const char *name;
    bool low, high_is_offset, has_high;
    uint64_t low_pc, high_pc, origin;
    bool has_origin;
} DwarfDie;

static bool dwarf_die(LineCursor *cursor, DwarfUnit *unit, uint64_t abbrev_offset, const LineSections *sections, DwarfDie *die) {
    memset(die, 0, sizeof *die);
    uint64_t code = line_uleb(cursor);
    if (cursor->failed) {
        return false;
    }

    if (code == 0) {
        return true;
    }

    LineCursor abbreviation;
    if (!dwarf_abbreviation(sections, abbrev_offset, code, &abbreviation)) {
        return false;
    }

    die->tag = line_uleb(&abbreviation);
    die->children = line_read(&abbreviation, 1) != 0;
    DwarfValue name = { 0, false, 0, NULL };
    for (;;) {
        uint64_t attribute = line_uleb(&abbreviation), form = line_uleb(&abbreviation);
        int64_t implicit = form == 0x21 ? line_sleb(&abbreviation) : 0;
        if (abbreviation.failed) {
            return false;
        }

        if (attribute == 0 && form == 0) {
            break;
        }

        DwarfValue value;
        if (!dwarf_value(cursor, form, implicit, unit, sections, &value) || cursor->failed) {
            return false;
        }

        switch (attribute) {
        case AT_NAME:
            name = value;
            break;
        case AT_LOW_PC:
            die->low = true;
            die->low_pc = value.number;
            if (value.kind == 3 && !dwarf_address(&value, unit, sections, &die->low_pc)) {
                die->low = false;
            }

            break;
        case AT_HIGH_PC:
            die->has_high = true;
            die->high_is_offset = value.kind != 3;
            die->high_pc = value.number;
            if (value.kind == 3 && !dwarf_address(&value, unit, sections, &die->high_pc)) {
                die->has_high = false;
            }

            break;
        case AT_ABSTRACT_ORIGIN:
        case AT_SPECIFICATION:
            die->has_origin = form != 0x10; // within the unit
            die->origin = value.number;
            break;
        case AT_STR_OFFSETS_BASE:
            unit->str_offsets_base = value.number;
            break;
        case AT_ADDR_BASE:
            unit->addr_base = value.number;
            break;
        default:
            break;
        }
    }

    die->name = dwarf_text(&name, unit, sections);
    return true;
}

// The name of the DIE at 'offset' in the unit: what a concrete function
// that has no name of its own is named by, its abstract origin's.
static const char *dwarf_name_at(DwarfUnit *unit, uint64_t abbrev_offset, uint64_t offset, const LineSections *sections, int depth) {
    LineCursor cursor = { unit->start + offset, sections->info + sections->info_size, false };
    DwarfDie die;
    if (depth > 4 || cursor.at >= cursor.end || !dwarf_die(&cursor, unit, abbrev_offset, sections, &die)) {
        return NULL;
    }

    return die.name != NULL || !die.has_origin ? die.name : dwarf_name_at(unit, abbrev_offset, die.origin, sections, depth + 1);
}

// The function whose code holds 'address' (an address as the debug
// information has them, unmoved): its name and where it starts.
static bool dwarf_function(const LineSections *sections, uint64_t address, const char **name, uint64_t *start) {
    LineCursor units = { sections->info, sections->info + sections->info_size, false };
    while (units.at < units.end && !units.failed) {
        DwarfUnit unit = { units.at, 4, 8, 0, 0, 0 };
        uint64_t length = line_read(&units, 4);
        if (length == 0xffffffff) {
            unit.offset_size = 8;
            length = line_read(&units, 8);
        }

        const uint8_t *end = units.at + length;
        if (units.failed || length > (uint64_t)(units.end - units.at)) {
            return false;
        }

        LineCursor cursor = { units.at, end, false };
        unit.version = (int)line_read(&cursor, 2);
        uint64_t abbrev_offset;
        if (unit.version >= 5) {
            uint64_t type = line_read(&cursor, 1);
            unit.address_size = (int)line_read(&cursor, 1);
            abbrev_offset = line_read(&cursor, unit.offset_size);
            if (type != 1) { // DW_UT_compile only
                units.at = end;
                continue;
            }
        } else {
            abbrev_offset = line_read(&cursor, unit.offset_size);
            unit.address_size = (int)line_read(&cursor, 1);
        }

        // Every DIE in order, children among them; the unit's own first,
        // which gives the bases its indices are read from.
        while (cursor.at < cursor.end && !cursor.failed) {
            DwarfDie die;
            if (!dwarf_die(&cursor, &unit, abbrev_offset, sections, &die)) {
                break;
            }

            if (die.tag == 0) {
                continue;
            }

            if (die.tag == TAG_SUBPROGRAM && die.low && die.has_high) {
                uint64_t high = die.high_is_offset ? die.low_pc + die.high_pc : die.high_pc;
                if (address >= die.low_pc && address < high) {
                    *name = die.name != NULL || !die.has_origin ? die.name : dwarf_name_at(&unit, abbrev_offset, die.origin, sections, 0);
                    *start = die.low_pc;
                    return *name != NULL;
                }
            }
        }

        units.at = end;
    }

    return false;
}

// The function at 'at' by the program's debug information, where its
// symbol is gone (#300): its name, and where it starts in memory. False
// where the program has none, or 'at' is not in the program itself.
static bool frame_function(const void *at, const void *image, const char **name, const void **start) {
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

    if (state != 2 || line_table.info == NULL || line_table.abbrev == NULL
        || (image != NULL && image != line_table.image && image != (const void *)line_table.slide)) {
        return false;
    }

    uint64_t found = 0;
    if (!dwarf_function(&line_table, (uint64_t)((uintptr_t)at - (uintptr_t)line_table.slide), name, &found)) {
        return false;
    }

    *start = (const void *)(uintptr_t)(found + (uint64_t)line_table.slide);
    return true;
}
#endif
