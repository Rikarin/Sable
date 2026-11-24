"""lldb formatters for Sable programs (docs/04 Phase 5).

Load it into lldb with

    (lldb) command script import tools/lldb/sable_lldb.py

or put that line in ~/.lldbinit. `sable debug <file.sb>` loads it for you,
and on macOS `sable build -g` copies it into the program's .dSYM, where lldb
finds it once `settings set target.load-script-from-symbol-file true` is set.

What the compiler's DWARF says, and what this file builds on (see
src/Sable.CodeGen/LlvmEmitter.Debug.cs): every Sable type that needs help to
read well is a typedef named as Sable names it, over a type whose name ends
in a suffix saying what kind of thing it is:

    String          -> SableString *          header, length, count, bytes
    [T]             -> [T]$list               $buffer -> header, count, capacity, elements (null when empty)
    [T:]            -> [T:]$slice             $array (a [T], the buffer viewed), start, count
    class C         -> C$ref                  $object -> header, then the fields
    union U         -> U$union                $tag (an enumeration of the cases), $payload (a C union)
    T?              -> T?$optional            the same; an optional class is the reference, null for none
    (A) -> R        -> (A) -> R$closure       $function, $context
    any P           -> any P$existential      box (the value, in an any$box$of$T), type (its metadata), a witness table per protocol
    M.Holder<T>     -> M.Holder<T>$generic    a struct of unspecialised code's: its fields
    T, T?, (T, Int) -> SableValue$dynamic     in unspecialised code: $address (where it is), $metadata (its type's)
    @inline_layout  -> S$inline               $block -> header, length, capacity, fields (the fixed-size ones, packed)

The formatters match those names by regular expression, so they work with
any lldb that runs Python (no recognizer functions needed), and they read
the runtime's layouts (runtime/*.c) directly.

A value whose type only its metadata knows (#297) — one unspecialised code
holds, what an existential holds — is shown as the type the program has
under the metadata's name, one of 'sable.debug.types'' members; one of a
type made at run time, which no DWARF describes, from the metadata's own
field descriptors.

Nothing read from the program is trusted: where a breakpoint stops before a
variable is written, or a program has overwritten its memory, what is there
may be anything. Every record is checked before it is used, every count and
name is bounded, and what does not hold up is shown as a marker in angle
brackets: '<invalid list at 0x10>'.
"""

import re
import struct

import lldb

CATEGORY = "sable"

# How much of a string is read, at most.
STRING_LIMIT = 1024

# What a sane record has, at most: a type's name in bytes, its fields or
# cases, its type arguments, and the bytes a value of it takes.
NAME_LIMIT = 2048
FIELD_LIMIT = 1024
SIZE_LIMIT = 1 << 32

# How many of a dictionary's entries are looked at, and how deep a summary goes.
ENTRY_LIMIT = 4096
ARGUMENT_LIMIT = 256
DEPTH_LIMIT = 12

# What a dictionary's type is called: '[Key: Value]' is Sable.Core's
# Dictionary, a struct of its entries (in the order the keys were added,
# none where one was removed) and the index beside them.
DICTIONARY = "^Sable\\.Core\\.Dictionary<.*>$"



# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------

def _canonical_name(valobj):
    return valobj.GetType().GetCanonicalType().GetName() or ""


def _raw(valobj):
    """The value as DWARF describes it, past any synthetic children."""
    raw = valobj.GetNonSyntheticValue()
    return raw if raw.IsValid() else valobj


def _read(process, address, size):
    """Some of the program's memory; None where it cannot be read."""
    if address < 4096 or size < 0 or address + size > 1 << 56:
        return None
    if size == 0:
        return b""
    error = lldb.SBError()
    data = process.ReadMemory(address, size, error)
    return data if error.Success() and data is not None and len(data) == size else None


def _readable(process, address, size):
    """Whether a value's first and last bytes are the program's to read."""
    if size == 0:
        return True
    return _read(process, address, 1) is not None and (size == 1 or _read(process, address + size - 1, 1) is not None)


def _word(process, address):
    """A pointer-sized word; 0 where it cannot be read."""
    data = _read(process, address, 8)
    return struct.unpack("<Q", data)[0] if data is not None else 0


def _read_cstring(process, address, limit=NAME_LIMIT):
    """A name: text, all of it printable, that ends within the limit."""
    text = b""
    while len(text) < limit:
        # Up to the next such boundary, so that no page past the text's is read.
        chunk = _read(process, address + len(text), 256 - (address + len(text)) % 256)
        if chunk is None:
            return None
        end = chunk.find(b"\0")
        text += chunk if end < 0 else chunk[:end]
        if end >= 0:
            break
    else:
        return None
    try:
        name = text.decode("utf-8")
    except UnicodeDecodeError:
        return None
    return name if name and name.isprintable() else None


_name_cache = {}


def _metadata_name(process, object_address):
    """The type name in an object's metadata: what it dynamically is; None when it is no object."""
    metadata = _word(process, object_address)
    if metadata == 0:
        return None
    key = (process.GetUniqueID(), metadata)
    if key not in _name_cache:
        name = _read_cstring(process, _word(process, metadata + 8))
        if name is None:
            return None
        _name_cache[key] = name
    return _name_cache[key]


def _strong_count(process, object_address):
    error = lldb.SBError()
    strong = process.ReadUnsignedFromMemory(object_address + 8, 4, error)
    flags = process.ReadUnsignedFromMemory(object_address + 12, 4, error)
    if not error.Success():
        return None
    return "immortal" if flags & 1 else strong


_type_cache = {}


def _types(target):
    """The types the program has, by the names their metadata carries.

    The compiler keeps them as the members of a global, sable.debug.types —
    one in each unit — so that they are found without a search by name,
    which in some lldbs asks every language's type system, Swift's included,
    at some cost.
    """
    key = (target.GetProcess().GetUniqueID(), target.GetExecutable().fullpath)
    if key in _type_cache:
        return _type_cache[key]
    types = {}
    # Looked for in the program alone: asked of the whole target, lldb also
    # searches the system's Swift libraries, and tries to load Swift for them.
    module = target.FindModule(target.GetExecutable())
    if module.IsValid():
        holders = module.FindGlobalVariables(target, "sable.debug.types", 256)
        for i in range(holders.GetSize()):
            sbtype = holders.GetValueAtIndex(i).GetType()
            for j in range(sbtype.GetNumberOfFields()):
                field = sbtype.GetFieldAtIndex(j)
                if field.GetName() not in types:
                    types[field.GetName()] = field.GetType().GetPointeeType()
    _type_cache[key] = types
    return types


def _find_type(target, name):
    """A type by the name its metadata carries; None when the program has none."""
    return _types(target).get(name) if name else None


# ---------------------------------------------------------------------------
# Type metadata (runtime/objects.c)
# ---------------------------------------------------------------------------

CLASS, STRUCT, ENUM, UNION, OPTIONAL, TUPLE, FUNCTION, ARRAY, SLICE, EXISTENTIAL, METATYPE = range(1, 12)

FIELD_TYPES, NULLABLE, BOOL_NICHE, CHAR_NICHE = 16, 32, 64, 128


class TypeInfo(object):
    """A SableTypeMetadata, read and checked."""

    def __init__(self, process, address, record, name):
        (self.kind, self.field_count, self._fields, _, _, _, self.size, self.alignment,
         self.stride, self.flags, _, self.argument_count, self._arguments, self.object) = record
        self.process = process
        self.address = address
        self.name = name
        self._field_list = None
        self._argument_list = None
        self._layout = None

    def fields(self):
        """Its fields or cases: each a name, its type's name, its offset and its type's metadata, 0 for none."""
        if self._field_list is None:
            data = _read(self.process, self._fields, 40 * self.field_count) if self.field_count else b""
            fields = []
            for i in range(self.field_count if data is not None else 0):
                name, type_name, offset, _, sbtype = struct.unpack_from("<QQqqQ", data, 40 * i)
                fields.append((_read_cstring(self.process, name) or str(i), _read_cstring(self.process, type_name) or "", offset, sbtype))
            self._field_list = fields if all(0 <= f[2] < SIZE_LIMIT for f in fields) else []
        return self._field_list

    def arguments(self):
        if self._argument_list is None:
            data = _read(self.process, self._arguments, 8 * self.argument_count) if self.argument_count else b""
            self._argument_list = list(struct.unpack("<%dQ" % self.argument_count, data)) if data is not None else []
        return self._argument_list

    def argument(self, index):
        arguments = self.arguments()
        return _type_info(self.process, arguments[index]) if index < len(arguments) else None

    def enum_layout(self):
        """How an enum's case is told — ("tag", bytes), ("null", case), ("niche", case, first value, bytes) — and where its payload is.

        As the runtime lays one out (lay_out_enum); None for one whose cases
        the metadata does not describe.
        """
        if self._layout is None:
            self._layout = self._lay_out_enum() or ()
        return self._layout or None

    def _lay_out_enum(self):
        fields = self.fields()
        if not fields:
            return None
        parts = [_type_info(self.process, f[3]) if f[3] else None for f in fields]
        if any(part is None and (f[3] or f[1]) for part, f in zip(parts, fields)):
            return None
        held = [i for i, part in enumerate(parts) if part is not None]
        if len(held) == 1:
            some, payload = held[0], parts[held[0]]
            if len(parts) == 2 and payload.flags & NULLABLE:
                return ("null", some), 0
            if payload.flags & BOOL_NICHE and len(parts) <= 255:
                return ("niche", some, 2, 1), 0
            if payload.flags & CHAR_NICHE:
                return ("niche", some, 0x110000, 4), 0
        tag = 1 if len(parts) <= 256 else 4
        unit = max([1] + [parts[i].alignment for i in held])
        if all(parts[i].size == 0 for i in held):
            unit = 1
        return ("tag", tag), (tag + unit - 1) // unit * unit

    def case(self, address):
        """The case an enum at the address holds, by its index; a number that is no case's when it holds none."""
        (kind, *rest), _ = self.enum_layout()
        if kind == "null":
            return rest[0] if _word(self.process, address) != 0 else 1 - rest[0]
        size = rest[0] if kind == "tag" else rest[2]
        data = _read(self.process, address, size)
        if data is None:
            return -1
        value = int.from_bytes(data, "little")
        if kind == "tag":
            return value
        some, spare = rest[0], rest[1]
        if value < spare:
            return some
        empties = [i for i in range(len(self.fields())) if i != some]
        return empties[value - spare] if value - spare < len(empties) else value


_info_cache = {}


def _type_info(process, address):
    """What a type's metadata says; None where there is no such record.

    Metadata never changes and is never freed, so what was read once is kept.
    """
    key = (process.GetUniqueID(), address)
    known = _info_cache.get(key)
    if known is not None:
        return known
    data = _read(process, address, 112)
    if data is None or address % 8 != 0:
        return None
    record = struct.unpack("<qqQqQQqqqqQqQQ", data)
    kind, field_count, _, _, _, name, size, alignment, stride, _, _, argument_count, _, _ = record
    if not (1 <= kind <= 15 and 0 <= field_count <= FIELD_LIMIT and 0 <= argument_count <= FIELD_LIMIT
            and 0 <= size <= SIZE_LIMIT and 0 <= stride <= SIZE_LIMIT and 0 <= alignment <= 65536 and alignment & (alignment - 1) == 0):
        return None
    text = _read_cstring(process, name)
    if text is None:
        return None
    info = TypeInfo(process, address, record, text)
    _info_cache[key] = info
    return info


def _object_info(process, object_address):
    """The type metadata of an object's class; None for no object, and for the runtime's own objects."""
    metadata = _word(process, object_address)
    info = _type_info(process, _word(process, metadata + 64)) if metadata else None
    return info if info is not None and info.kind == CLASS else None


def _value_at(valobj, name, address, info):
    """A value of a type only its metadata is had of: as the program's own type by its name, or as a $dynamic."""
    target = valobj.GetTarget()
    sbtype = _find_type(target, info.name)
    if sbtype is not None and sbtype.GetByteSize() == info.size:
        return valobj.CreateValueFromAddress(name, address, sbtype)
    dynamic = _find_type(target, "$dynamic")
    if dynamic is None:
        return None
    data = lldb.SBData.CreateDataFromUInt64Array(target.GetByteOrder(), target.GetAddressByteSize(), [address, info.address])
    return valobj.CreateValueFromData(name, data, dynamic)


def _field_values(valobj, address, info):
    """The fields of a struct, a tuple or an object, where its metadata says; those whose type is known."""
    values = []
    for name, type_name, offset, metadata in info.fields():
        held = _type_info(valobj.GetProcess(), metadata) if metadata else None
        if held is not None:
            values.append(_value_at(valobj, name, address + offset, held))
        else:
            sbtype = _find_type(valobj.GetTarget(), type_name)
            if sbtype is not None:
                values.append(valobj.CreateValueFromAddress(name, address + offset, sbtype))
    return [v for v in values if v is not None]


# ---------------------------------------------------------------------------
# Type parameters
# ---------------------------------------------------------------------------

_parameter_cache = {}


def _type_arguments(frame):
    """What each type parameter of an unspecialised frame is, by name: {"T": "Int"}; empty for any other frame."""
    if frame is None or not frame.IsValid():
        return {}
    function = frame.GetFunction()
    if not function.IsValid():
        return {}
    process = frame.GetThread().GetProcess()
    key = (process.GetUniqueID(), function.GetStartAddress().GetLoadAddress(process.GetTarget()))
    names = _parameter_cache.get(key)
    if names is None:
        variables = function.GetBlock().GetVariables(process.GetTarget(), True, True, False)
        names = sorted({v.GetName() for v in variables if (v.GetName() or "").startswith("$metadata.")})
        _parameter_cache[key] = names
    arguments = {}
    for name in names:
        info = _type_info(process, frame.FindVariable(name).GetValueAsUnsigned(0))
        if info is not None:
            arguments[name[len("$metadata."):]] = info.name
    return arguments


def _substitute(text, arguments):
    """A name with each type parameter in it the type it is: '[T]' is '[Int]'."""
    if not arguments:
        return text
    names = "|".join(re.escape(n) for n in sorted(arguments, key=len, reverse=True))
    return re.sub(r"(?<![\w.$])(?:%s)(?![\w.])" % names, lambda m: arguments[m.group(0)], text)


def _type_name(valobj):
    """A value's type's name, with the frame's type parameters the types they are."""
    name = _raw(valobj).GetTypeName() or ""
    return _substitute(name, _type_arguments(valobj.GetFrame()))


_frame_format = None


def frame_name(frame, internal_dict):
    """A frame's function and its arguments, as '${function.name-with-args}' gives them, with an unspecialised one's type arguments."""
    global _frame_format
    name = frame.GetFunctionName() or ""
    # A thunk the compiler made is its own name.
    arguments = _type_arguments(frame) if not name.startswith("$") and ("<" in name or "$generic" in name) else {}
    if not arguments:
        if _frame_format is None:
            _frame_format = lldb.SBFormat("${function.name-with-args}", lldb.SBError())
        stream = lldb.SBStream()
        described = frame.GetDescriptionWithFormat(_frame_format, stream).Success()
        return stream.GetData() or "" if described else frame.GetDisplayFunctionName() or ""
    shown = _substitute(name, arguments)
    if shown == name:
        # A function with no type arguments in its name: an initialiser's body, a closure.
        shown = "%s<%s>" % (name.replace("$generic", ""), ", ".join(arguments[n] for n in arguments))
    given = ["%s=%s" % (a.GetName(), _describe(a)[:ARGUMENT_LIMIT]) for a in frame.GetVariables(True, False, False, True)]
    return "%s(%s)" % (shown, ", ".join(given)) if given else shown


def _pointer_value(valobj, name, address, sbtype):
    """A value of a pointer-sized type — a pointer, a class reference — holding the address given."""
    target = valobj.GetTarget()
    data = lldb.SBData.CreateDataFromUInt64Array(target.GetByteOrder(), target.GetAddressByteSize(), [address])
    return valobj.CreateValueFromData(name, data, sbtype)


def _object_address(reference):
    """The address of the object a class reference (a C$ref) refers to; 0 for none."""
    return _raw(reference).GetChildMemberWithName("$object").GetValueAsUnsigned(0)


def _object_body(reference):
    """The object a class reference refers to: header and fields."""
    return _raw(reference).GetChildMemberWithName("$object").Dereference()


def _reference_of_type(valobj, name, address):
    """A reference to the object at the address, typed as the class its metadata names; None if not found."""
    dynamic = _metadata_name(valobj.GetProcess(), address)
    sbtype = _find_type(valobj.GetTarget(), dynamic) if dynamic else None
    return _pointer_value(valobj, name, address, sbtype) if sbtype is not None else None


_depth = 0


def _describe(value):
    """A short text for any value: its summary, its scalar, or its children's."""
    global _depth
    if not value or not value.IsValid():
        return "?"
    if _depth >= DEPTH_LIMIT:
        return "..."
    _depth += 1
    try:
        return _description(value)
    finally:
        _depth -= 1


def _description(value):
    summary = value.GetSummary()
    if summary:
        return summary
    scalar = value.GetValue()
    if scalar is not None and value.GetNumChildren() == 0:
        return scalar
    count = value.GetNumChildren()
    if count == 0:
        return scalar if scalar is not None else "()"
    parts = []
    for i in range(min(count, 8)):
        child = value.GetChildAtIndex(i)
        name = child.GetName() or ""
        text = _describe(child)
        parts.append(text if name.isdigit() or name.startswith("[") else "%s: %s" % (name, text))
    if count > 8:
        parts.append("...")
    return "(" + ", ".join(parts) + ")"


def _quote(text):
    out = ['"']
    for ch in text:
        if ch == '"':
            out.append('\\"')
        elif ch == "\\":
            out.append("\\\\")
        elif ch == "\n":
            out.append("\\n")
        elif ch == "\t":
            out.append("\\t")
        elif ch == "\r":
            out.append("\\r")
        elif ord(ch) < 0x20:
            out.append("\\u{%x}" % ord(ch))
        else:
            out.append(ch)
    out.append('"')
    return "".join(out)


# ---------------------------------------------------------------------------
# String
# ---------------------------------------------------------------------------

def string_text(valobj):
    """The text of a String value, or None if it cannot be read."""
    address = _raw(valobj).GetValueAsUnsigned(0)
    if address == 0:
        return None
    process = valobj.GetProcess()
    if _metadata_name(process, address) != "String":
        return None
    length = _word(process, address + 16)
    data = _read(process, address + 32, min(length, STRING_LIMIT)) if length <= 1 << 32 else None
    if data is None:
        return None
    text = data.decode("utf-8", "replace")
    return text + ("..." if length > STRING_LIMIT else "")


def string_summary(valobj, internal_dict):
    text = string_text(valobj)
    if text is None:
        address = _raw(valobj).GetValueAsUnsigned(0)
        return "<null>" if address == 0 else "<invalid string at 0x%x>" % address
    return _quote(text)


# ---------------------------------------------------------------------------
# Lists: [T], and the arrays and dictionaries built on it
# ---------------------------------------------------------------------------

class Elements(object):
    """An array buffer's elements: how many, where, and what each is; a marker, where there is no buffer to read."""

    def __init__(self, valobj, pointer):
        self.valobj = valobj
        self.count = 0
        self.marker = None
        self.base = self.stride = 0
        self.sbtype = self.info = None
        address = pointer.GetValueAsUnsigned(0)
        if address == 0:
            return
        process = valobj.GetProcess()
        header = _read(process, address, 40)
        name = _metadata_name(process, address)
        if header is None or name is None:
            self.marker = "<invalid list at 0x%x>" % address
            return
        metadata, _, _, count, capacity, self.base = struct.unpack("<QIIqqQ", header)
        if not 0 <= count <= capacity:
            self.marker = "<invalid list at 0x%x: count = %d, capacity = %d>" % (address, count, capacity)
            return

        # Their type, as the DWARF has it; in unspecialised code, as the
        # buffer's metadata does: one made at run time holds the elements',
        # and one the program has is found by its name.
        element = pointer.Dereference().GetChildMemberWithName("elements").GetType().GetPointeeType()
        if element.GetBasicType() != lldb.eBasicTypeVoid:
            self.sbtype = element
        else:
            made = _type_info(process, metadata + 80)
            if made is not None and made.kind == ARRAY and made.name == name and made.arguments() == [_word(process, metadata + 72)]:
                self.info = made.argument(0)
            if self.info is None:
                known = _find_type(valobj.GetTarget(), name)
                self.sbtype = _list_element(known) if known is not None else None
        self.stride = self.sbtype.GetByteSize() if self.sbtype is not None else self.info.stride if self.info is not None else 0
        if count * self.stride > 1 << 48 or not _readable(process, self.base, count * self.stride):
            self.marker = "<invalid list at 0x%x: count = %d>" % (address, count)
            return
        self.count = count

    def child(self, name, index):
        at = self.base + index * self.stride
        if self.sbtype is not None:
            return self.valobj.CreateValueFromAddress(name, at, self.sbtype)
        return _value_at(self.valobj, name, at, self.info) if self.info is not None else None


def _list_element(sbtype):
    """A list type's elements' type."""
    fields = sbtype.GetCanonicalType().get_fields_array()
    body = fields[0].GetType().GetPointeeType().get_fields_array() if len(fields) == 1 and fields[0].GetName() == "$buffer" else []
    elements = [f for f in body if f.GetName() == "elements"]
    return elements[0].GetType().GetPointeeType() if elements else None


def _list_elements(valobj):
    """A list's, given the list."""
    return Elements(valobj, _raw(valobj).GetChildMemberWithName("$buffer"))


def _slice_run(valobj):
    """A slice's: its buffer's, where its run starts and how long it is; and a marker when it is no run of it."""
    raw = _raw(valobj)
    count = raw.GetChildMemberWithName("count").GetValueAsSigned(0)
    start = raw.GetChildMemberWithName("start").GetValueAsSigned(0)
    elements = Elements(valobj, _raw(raw.GetChildMemberWithName("$array")).GetChildMemberWithName("$buffer"))
    marker = elements.marker
    if marker is None and count != 0 and not (count > 0 and start >= 0 and start + count <= elements.count):
        marker = "<invalid slice: start = %d, count = %d>" % (start, count)
    return elements, start, 0 if marker else count, marker


class ListProvider(object):
    """A list's elements as children: [0], [1], ..."""

    def __init__(self, valobj, internal_dict):
        self.valobj = valobj
        self.elements = None
        self.start = self.count = 0

    def update(self):
        self.elements = _list_elements(self.valobj)
        self.count = self.elements.count
        return False

    def num_children(self, max_children=None):
        return self.count

    def has_children(self):
        return True

    def get_child_index(self, name):
        try:
            return int(name.lstrip("[").rstrip("]"))
        except ValueError:
            return -1

    def get_child_at_index(self, index):
        if index < 0 or index >= self.count:
            return None
        return self.elements.child("[%d]" % index, self.start + index)

    def get_type_name(self):
        return _type_name(self.valobj)


class SliceProvider(ListProvider):
    """A slice's elements as children, [0] to [count - 1]: its run of the buffer it views."""

    def update(self):
        self.elements, self.start, self.count, _ = _slice_run(self.valobj)
        return False


def slice_summary(valobj, internal_dict):
    _, _, count, marker = _slice_run(valobj)
    return marker or "count = %d" % count


def list_summary(valobj, internal_dict):
    elements = _list_elements(valobj)
    return elements.marker or "count = %d" % elements.count


# ---------------------------------------------------------------------------
# Unions and optionals
# ---------------------------------------------------------------------------

def _active_case(valobj):
    """The case a union value holds, and its payload (None when it has none); no case, when it holds none of its type's."""
    if _is_dynamic(valobj):
        view = DynamicView(valobj)
        return view.case, view.payload
    raw = _raw(valobj)
    tag = raw.GetChildMemberWithName("$tag")
    case = tag.GetValue()
    if case is None or not any(m.GetName() == case for m in tag.GetType().GetEnumMembers()):
        return None, None
    payload = raw.GetChildMemberWithName("$payload").GetChildMemberWithName(case)
    return case, payload if payload.IsValid() else None


def _is_dynamic(value):
    return _canonical_name(value).endswith("$dynamic")


def _dynamic_info(value):
    """The metadata a $dynamic holds; None for none."""
    return _type_info(value.GetProcess(), _raw(value).GetChildMemberWithName("$metadata").GetValueAsUnsigned(0))


def _is_tuple(value):
    """A tuple, or a union case's named fields ('U.Case$case'): shown as the case's own parentheses."""
    if _is_dynamic(value):
        info = _dynamic_info(value)
        return info is not None and info.kind == TUPLE
    name = value.GetType().GetCanonicalType().GetName() or ""
    return (name.startswith("(") and not name.endswith("$closure")) or name.endswith("$case")


class UnionProvider(object):
    """The active case's payload: a tuple's elements, or the one value."""

    def __init__(self, valobj, internal_dict):
        self.valobj = valobj
        self.children = []

    def update(self):
        self.children = []
        case, payload = _active_case(self.valobj)
        if payload is None:
            return False
        if _is_tuple(payload):
            for i in range(payload.GetNumChildren()):
                self.children.append(payload.GetChildAtIndex(i))
        else:
            self.children.append(payload.Clone(case))
        return False

    def num_children(self, max_children=None):
        return len(self.children)

    def has_children(self):
        return True

    def get_child_index(self, name):
        for i, child in enumerate(self.children):
            if child.GetName() == name:
                return i
        return -1

    def get_child_at_index(self, index):
        return self.children[index] if 0 <= index < len(self.children) else None


def union_summary(valobj, internal_dict):
    case, payload = _active_case(valobj)
    if case is None:
        return "<invalid case>"
    if payload is None:
        return case
    if _is_tuple(payload):
        return case + _describe(payload)
    return "%s(%s)" % (case, _describe(payload))


class OptionalProvider(object):
    """What a 'some' holds: its children, if it has any."""

    def __init__(self, valobj, internal_dict):
        self.valobj = valobj
        self.payload = None

    def update(self):
        case, payload = _active_case(self.valobj)
        self.payload = payload if case == "Some" else None
        return False

    def num_children(self, max_children=None):
        return self.payload.GetNumChildren() if self.payload is not None else 0

    def has_children(self):
        return self.payload is not None and self.payload.MightHaveChildren()

    def get_child_index(self, name):
        return self.payload.GetIndexOfChildWithName(name) if self.payload is not None else -1

    def get_child_at_index(self, index):
        return self.payload.GetChildAtIndex(index) if self.payload is not None else None


def optional_summary(valobj, internal_dict):
    case, payload = _active_case(valobj)
    if case is None:
        return "<invalid case>"
    return "none" if case != "Some" else _describe(payload)


# ---------------------------------------------------------------------------
# Class references
# ---------------------------------------------------------------------------

class ObjectProvider(object):
    """An object's fields, as its dynamic class has them, then [strong], its reference count."""

    def get_type_name(self):
        return _type_name(self.valobj)

    def __init__(self, valobj, internal_dict):
        self.valobj = valobj
        self.children = []

    def update(self):
        self.children = []
        address = _object_address(self.valobj)
        if address == 0:
            return False
        process = self.valobj.GetProcess()
        dynamic = _metadata_name(process, address)
        info = _object_info(process, address)
        if dynamic is None or info is None:
            return False
        reference = self.valobj
        made = False
        if _canonical_name(self.valobj) != dynamic + "$ref":
            # Its own class's; one made at run time, as its metadata has it.
            known = _reference_of_type(self.valobj, self.valobj.GetName(), address)
            made = known is None
            reference = known or reference
        body = _object_body(reference)
        if made:
            self.children = _field_values(self.valobj, address, info)
        else:
            for i in range(body.GetNumChildren()):
                child = body.GetChildAtIndex(i)
                if child.GetName() != "$header":
                    self.children.append(child)
        # The count is the header's own field, so its type comes from the
        # program's DWARF: a basic type asked of the target would set up every
        # language's scratch context, Swift's included, which can hang.
        if _strong_count(self.valobj.GetProcess(), address) != "immortal":
            strong = body.GetChildMemberWithName("$header").GetChildMemberWithName("strong")
            if strong.IsValid():
                self.children.append(strong.Clone("[strong]"))
        return False

    def num_children(self, max_children=None):
        return len(self.children)

    def has_children(self):
        return _object_address(self.valobj) != 0

    def get_child_index(self, name):
        for i, child in enumerate(self.children):
            if child.GetName() == name:
                return i
        return -1

    def get_child_at_index(self, index):
        return self.children[index] if 0 <= index < len(self.children) else None


# ---------------------------------------------------------------------------
# @inline_layout values
# ---------------------------------------------------------------------------

class InlineProvider(object):
    """An @inline_layout value's fixed fields, read where its bytes are."""

    def __init__(self, valobj, internal_dict):
        self.valobj = valobj
        self.fields = None

    def update(self):
        block = _raw(self.valobj).GetChildMemberWithName("$block")
        self.fields = block.Dereference().GetChildMemberWithName("fields") if block.IsValid() and block.GetValueAsUnsigned(0) != 0 else None
        return False

    def num_children(self, max_children=None):
        return self.fields.GetNumChildren() if self.fields is not None else 0

    def has_children(self):
        return True

    def get_child_index(self, name):
        return self.fields.GetIndexOfChildWithName(name) if self.fields is not None else -1

    def get_child_at_index(self, index):
        return self.fields.GetChildAtIndex(index) if self.fields is not None else None


# ---------------------------------------------------------------------------
# Dictionaries
# ---------------------------------------------------------------------------

def _dictionary_entries(value):
    """A dictionary's values, in the order their keys were added, each named by its key."""
    elements = _list_elements(_raw(value).GetChildMemberWithName("entries"))
    children = []
    for i in range(min(elements.count, ENTRY_LIMIT)):
        slot = elements.child("[%d]" % i, i)
        case, entry = _active_case(slot) if slot is not None else (None, None)
        if case != "Some" or entry is None:
            continue
        key = _describe(entry.GetChildMemberWithName("key"))
        children.append(entry.GetChildMemberWithName("value").Clone("[%s]" % key))
    return children


class DictionaryProvider(object):
    """A dictionary's values as children, each named by its key: ["ann"] = 31."""

    def get_type_name(self):
        return _type_name(self.valobj)

    def __init__(self, valobj, internal_dict):
        self.valobj = valobj
        self.children = []

    def update(self):
        self.children = _dictionary_entries(self.valobj)
        return False

    def num_children(self, max_children=None):
        return len(self.children)

    def has_children(self):
        return True

    def get_child_index(self, name):
        for i, child in enumerate(self.children):
            if child.GetName() == name:
                return i
        return -1

    def get_child_at_index(self, index):
        return self.children[index] if 0 <= index < len(self.children) else None


def dictionary_summary(valobj, internal_dict):
    raw = _raw(valobj)
    marker = _list_elements(raw.GetChildMemberWithName("entries")).marker
    return marker or "count = %d" % raw.GetChildMemberWithName("live").GetValueAsSigned(0)


def object_summary(valobj, internal_dict):
    raw = _raw(valobj)
    address = _object_address(raw)
    if address == 0:
        return "none" if (raw.GetType().GetName() or "").endswith("?") else "<null>"
    process = valobj.GetProcess()
    name = _metadata_name(process, address)
    if name is None or _object_info(process, address) is None:
        return "<invalid object at 0x%x>" % address
    # The metadata names a generic class's instance as its symbols do,
    # Box$of$String; its type is named as Sable writes it.
    sbtype = _find_type(valobj.GetTarget(), name)
    return sbtype.GetName() if sbtype is not None and sbtype.GetName() else name


# ---------------------------------------------------------------------------
# Closures and existentials
# ---------------------------------------------------------------------------

def _function_name(value):
    return _function_at(value.GetTarget(), value.GetValueAsUnsigned(0))


def _function_at(target, address):
    if address == 0:
        return None
    sbaddress = target.ResolveLoadAddress(address)
    function = sbaddress.GetFunction()
    if function.IsValid():
        return function.GetDisplayName() or function.GetName()
    symbol = sbaddress.GetSymbol()
    return symbol.GetName() if symbol.IsValid() else "0x%x" % address


class ClosureProvider(object):
    """The function a closure calls, and what it captured."""

    def __init__(self, valobj, internal_dict):
        self.valobj = valobj
        self.children = []

    def update(self):
        self.children = []
        raw = self.valobj
        function = raw.GetChildMemberWithName("$function")
        self.children.append(function.Clone("[function]"))
        context = raw.GetChildMemberWithName("$context").GetValueAsUnsigned(0)
        if context != 0:
            reference = _reference_of_type(raw, "$context", context)
            if reference is not None:
                body = _object_body(reference)
                for i in range(body.GetNumChildren()):
                    child = body.GetChildAtIndex(i)
                    # The header, and what unspecialised code's has for it: the metadata, the tables.
                    if (child.GetName() or "").startswith("$"):
                        continue
                    # One captured by reference lives in a box: shown is what it holds.
                    if _canonical_name(child).startswith("Box$of$") and _object_address(child) != 0:
                        child = _object_body(child).GetChildMemberWithName("value").Clone(child.GetName())
                    self.children.append(child)
        return False

    def num_children(self, max_children=None):
        return len(self.children)

    def has_children(self):
        return True

    def get_child_index(self, name):
        for i, child in enumerate(self.children):
            if child.GetName() == name:
                return i
        return -1

    def get_child_at_index(self, index):
        return self.children[index] if 0 <= index < len(self.children) else None


def closure_summary(valobj, internal_dict):
    name = _function_name(_raw(valobj).GetChildMemberWithName("$function"))
    return name or "<null closure>"


def _existential_value(valobj):
    """The dynamic type of what an existential holds, and the value: read from its box."""
    raw = _raw(valobj)
    box = _object_address(raw.GetChildMemberWithName("box"))
    if raw.GetNumChildren() == 0 or box == 0:
        return None, None
    process = raw.GetProcess()
    info = _type_info(process, raw.GetChildMemberWithName("type").GetValueAsUnsigned(0))
    if info is None or _metadata_name(process, box) is None:
        return "<invalid existential at 0x%x>" % box, None

    # The value is the box's one field, at the first place past the header
    # that its type's alignment allows.
    at = box + max(16, info.alignment)
    if not _readable(process, at, info.size):
        return "<invalid existential at 0x%x>" % box, None
    dynamic = info.name
    # A class held in it may be a subclass: its own metadata says.
    if info.kind == CLASS:
        held = _object_info(process, _word(process, at))
        dynamic = held.name if held is not None else dynamic
    return dynamic, _value_at(raw, "value", at, info)


class ExistentialProvider(object):
    """[type], the dynamic type, and [value], the value held."""

    def __init__(self, valobj, internal_dict):
        self.valobj = valobj
        self.children = []

    def update(self):
        self.children = []
        dynamic, value = _existential_value(self.valobj)
        if value is not None:
            self.children.append(value.Clone("[value]"))
        return False

    def num_children(self, max_children=None):
        return len(self.children)

    def has_children(self):
        return True

    def get_child_index(self, name):
        return 0 if name == "[value]" and self.children else -1

    def get_child_at_index(self, index):
        return self.children[index] if 0 <= index < len(self.children) else None


def existential_summary(valobj, internal_dict):
    dynamic, value = _existential_value(valobj)
    if dynamic is None:
        return "<empty>"
    return dynamic


# ---------------------------------------------------------------------------
# Values whose type only metadata knows (#297)
# ---------------------------------------------------------------------------

def _children(value):
    """A value's children, as many as are shown: none for a pointer's — a string's — which its summary says all of."""
    if value is None or value.GetType().GetCanonicalType().IsPointerType():
        return []
    return [value.GetChildAtIndex(i) for i in range(value.GetNumChildren(256))]


class DynamicView(object):
    """What a $dynamic shows: the value as the program's own type of it, or what its metadata says, or a marker."""

    def __init__(self, valobj):
        raw = _raw(valobj)
        self.valobj = raw
        self.value = None      # the value, as a type the program has
        self.children = []     # else its children
        self.text = None       # and its summary
        self.case = None       # an enum's case, and its payload
        self.payload = None
        process = raw.GetProcess()
        self.address = raw.GetChildMemberWithName("$address").GetValueAsUnsigned(0)
        metadata = raw.GetChildMemberWithName("$metadata").GetValueAsUnsigned(0)
        self.info = info = _type_info(process, metadata)
        if info is None:
            self.text = "<uninitialised>" if self.address == 0 else "<type not known here>" if metadata == 0 else "<invalid metadata at 0x%x>" % metadata
        elif self.address == 0:
            self.text = "<uninitialised>"
        elif not _readable(process, self.address, info.size):
            self.text = "<invalid value at 0x%x>" % self.address
        elif (sbtype := _find_type(raw.GetTarget(), info.name)) is not None and sbtype.GetByteSize() == info.size:
            # An optional string is the string, null for none, which lldb would call NULL.
            if info.kind == OPTIONAL and sbtype.GetCanonicalType().IsPointerType() and _word(process, self.address) == 0:
                self.text = self.case = "none"
                return
            self.value = raw.CreateValueFromAddress(raw.GetName(), self.address, sbtype)
            if info.kind in (ENUM, UNION, OPTIONAL):
                self.case, self.payload = _active_case(self.value)
        else:
            self._read(process, info)

    def _read(self, process, info):
        if info.kind in (STRUCT, TUPLE):
            self.children = _field_values(self.valobj, self.address, info)
            if info.kind == TUPLE:
                self.text = "(" + ", ".join(_describe(c) for c in self.children) + ")"
                self.children = []
            elif not self.children:
                self.text = "()" if info.size == 0 else "<%s: no description>" % info.name
        elif info.kind in (ENUM, UNION, OPTIONAL):
            self._read_enum(process, info)
        elif info.kind == CLASS:
            self._read_object(process)
        elif info.kind in (ARRAY, SLICE):
            self._read_list(process, info)
        elif info.kind == EXISTENTIAL:
            self._read_existential(process)
        elif info.kind == METATYPE:
            held = _type_info(process, _word(process, self.address))
            self.text = held.name if held is not None else "<invalid metadata>"
        elif info.kind == FUNCTION:
            self.text = _function_at(self.valobj.GetTarget(), _word(process, self.address)) or "<null closure>"
        else:
            self.text = "()" if info.size == 0 else "<%s: no description>" % info.name

    def _read_enum(self, process, info):
        layout = info.enum_layout()
        if layout is None:
            self.text = "<%s: no description>" % info.name
            return
        fields = info.fields()
        index = info.case(self.address)
        if not 0 <= index < len(fields):
            self.text = "<invalid case %d>" % index
            return
        self.case = fields[index][0]
        held = _type_info(process, fields[index][3]) if fields[index][3] else None
        if held is not None and held.size > 0:
            self.payload = _value_at(self.valobj, self.case, self.address + layout[1], held)
        if info.kind == OPTIONAL:
            self.text = "none" if self.case != "Some" else _describe(self.payload) if self.payload is not None else "()"
            self.children = _children(self.payload)
        elif self.payload is None:
            self.text = self.case
        else:
            described = _describe(self.payload)
            tuple_held = _is_tuple(self.payload)
            self.text = self.case + described if tuple_held else "%s(%s)" % (self.case, described)
            self.children = _children(self.payload) if tuple_held else [self.payload]

    def _read_object(self, process):
        address = _word(process, self.address)
        if address == 0:
            self.text = "<null>"
            return
        held = _object_info(process, address)
        if held is None:
            self.text = "<invalid object at 0x%x>" % address
            return
        sbtype = _find_type(self.valobj.GetTarget(), _metadata_name(process, address))
        if sbtype is not None:
            self.value = _pointer_value(self.valobj, self.valobj.GetName(), address, sbtype)
            return
        self.text = held.name
        self.children = _field_values(self.valobj, address, held)

    def _read_list(self, process, info):
        buffer = _word(process, self.address)
        element = info.argument(0)
        start, count = 0, None
        if info.kind == SLICE:
            start, count = _word(process, self.address + 8), _word(process, self.address + 16)
        header = _read(process, buffer, 40) if buffer else struct.pack("<5Q", 0, 0, 0, 0, 0)
        _, _, held, capacity, base = struct.unpack("<QQqqQ", header or struct.pack("<5q", 0, 0, -1, 0, 0))
        count = held if count is None else count
        if header is None or not 0 <= held <= capacity or start + count > held or element is None or not _readable(process, base, held * element.stride):
            self.text = "<invalid list at 0x%x>" % buffer
            return
        self.text = "count = %d" % count
        # As many as lldb shows, at most: there is no provider of its own to ask for more.
        self.children = [_value_at(self.valobj, "[%d]" % i, base + (start + i) * element.stride, element) for i in range(min(count, 256))]

    def _read_existential(self, process):
        box, info = _word(process, self.address), _type_info(process, _word(process, self.address + 8))
        if box == 0:
            self.text = "<empty>"
        elif info is None or not _readable(process, box + max(16, info.alignment), info.size):
            self.text = "<invalid existential at 0x%x>" % box
        else:
            self.text = info.name
            self.children = [_value_at(self.valobj, "[value]", box + max(16, info.alignment), info)]


class DynamicProvider(object):
    """A $dynamic's children: the value's own."""

    def __init__(self, valobj, internal_dict):
        self.valobj = valobj
        self.view = None

    def update(self):
        self.view = DynamicView(self.valobj)
        self.children = [c for c in self.view.children if c is not None]
        return False

    def num_children(self, max_children=None):
        if self.view.value is None:
            return len(self.children)
        return 0 if self.view.value.GetType().GetCanonicalType().IsPointerType() else self.view.value.GetNumChildren()

    def has_children(self):
        return self.num_children() > 0

    def get_child_index(self, name):
        if self.view.value is not None:
            return self.view.value.GetIndexOfChildWithName(name)
        for i, child in enumerate(self.children):
            if child.GetName() == name:
                return i
        return -1

    def get_child_at_index(self, index):
        if self.view.value is not None:
            return self.view.value.GetChildAtIndex(index)
        return self.children[index] if 0 <= index < len(self.children) else None

    def get_type_name(self):
        # Asked before update is, at times.
        info = _dynamic_info(self.valobj)
        return info.name if info is not None else _type_name(self.valobj)


def dynamic_summary(valobj, internal_dict):
    view = DynamicView(valobj)
    if view.value is not None:
        return view.value.GetSummary() or view.value.GetValue() or ""
    return view.text or ""


class GenericProvider(object):
    """A struct of unspecialised code's: its fields, under the name its type has in the frame."""

    def __init__(self, valobj, internal_dict):
        self.valobj = valobj

    def update(self):
        return False

    def num_children(self, max_children=None):
        return self.valobj.GetNumChildren()

    def has_children(self):
        return True

    def get_child_index(self, name):
        return self.valobj.GetIndexOfChildWithName(name)

    def get_child_at_index(self, index):
        return self.valobj.GetChildAtIndex(index)

    def get_type_name(self):
        return _type_name(self.valobj)


# ---------------------------------------------------------------------------
# Tuples and weak references
# ---------------------------------------------------------------------------

def char_summary(valobj, internal_dict):
    value = _raw(valobj).GetValueAsUnsigned(0)
    try:
        text = chr(value)
    except ValueError:
        return "U+%04X" % value
    if text == "'":
        return "'\\''"
    return "'\"'" if text == '"' else "'%s'" % _quote(text)[1:-1]


def type_summary(valobj, internal_dict):
    """A type as a value: its name."""
    address = _raw(valobj).GetValueAsUnsigned(0)
    info = _type_info(valobj.GetProcess(), address)
    return info.name if info is not None else "<null>" if address == 0 else "<invalid metadata at 0x%x>" % address


def tuple_summary(valobj, internal_dict):
    raw = _raw(valobj)
    if _canonical_name(raw).endswith("$closure"):
        return closure_summary(valobj, internal_dict)
    if _is_dynamic(raw):
        return dynamic_summary(valobj, internal_dict)
    return _describe(raw)


def weak_summary(valobj, internal_dict):
    raw = _raw(valobj)
    if raw.GetValueAsUnsigned(0) == 0:
        return "none"
    target = raw.Dereference().GetChildMemberWithName("object")
    if _object_address(target) == 0:
        return "none"
    return "weak " + (object_summary(target, internal_dict) or "")


# ---------------------------------------------------------------------------
# Registration
# ---------------------------------------------------------------------------

def _register(debugger, module):
    def run(command):
        result = lldb.SBCommandReturnObject()
        debugger.GetCommandInterpreter().HandleCommand(command, result)
        if not result.Succeeded():
            print("sable_lldb: %s: %s" % (command, result.GetError()))

    # Pointers to these and references to them are left alone (-p, -r): an
    # inout parameter shows as an address, which 'frame variable *name' reads.
    # A string's address is not shown (-v), only its text; a list, an object
    # and an existential show their children under the summary (-e).
    summaries = [
        ("^String$", "string_summary", "-v"),
        ("^SableString \\*$", "string_summary", "-v"),
        ("^Char$", "char_summary", "-v"),
        ("^Type$", "type_summary", "-v"),
        ("\\$list$", "list_summary", "-e"),
        (DICTIONARY, "dictionary_summary", "-e"),
        ("\\$slice$", "slice_summary", "-e"),
        ("\\$union$", "union_summary", ""),
        ("\\$optional$", "optional_summary", ""),
        ("\\$ref$", "object_summary", "-e"),
        ("\\$closure$", "closure_summary", ""),
        ("\\$existential$", "existential_summary", "-e"),
        ("\\$dynamic$", "dynamic_summary", "-e"),
        ("^\\(.*\\)$", "tuple_summary", ""),
        ("^SableWeakEntry \\*$", "weak_summary", "-v"),
    ]
    synthetics = [
        ("\\$list$", "ListProvider"),
        (DICTIONARY, "DictionaryProvider"),
        ("\\$slice$", "SliceProvider"),
        ("\\$union$", "UnionProvider"),
        ("\\$optional$", "OptionalProvider"),
        ("\\$ref$", "ObjectProvider"),
        ("\\$closure$", "ClosureProvider"),
        ("\\$existential$", "ExistentialProvider"),
        ("\\$inline$", "InlineProvider"),
        ("\\$generic$", "GenericProvider"),
        ("\\$dynamic$", "DynamicProvider"),
    ]
    for regex, function, flags in summaries:
        run('type summary add -w %s -p -r %s -x "%s" -F %s.%s' % (CATEGORY, flags, regex, module, function))
    for regex, provider in synthetics:
        run('type synthetic add -w %s -p -r -x "%s" -l %s.%s' % (CATEGORY, regex, module, provider))
    run("type category enable %s" % CATEGORY)

    # A frame is named for the types its type parameters are, in a backtrace
    # and where the program stops, where the format has the function's name
    # (a format the user wrote without it is left alone).
    if hasattr(lldb.SBFrame, "GetDescriptionWithFormat"):
        shown = debugger.GetInternalVariableValue("frame-format", debugger.GetInstanceName())
        text = shown.GetStringAtIndex(0) if shown is not None and shown.GetSize() > 0 else ""
        text = text[1:-1] if len(text) > 1 and text.startswith('"') and text.endswith('"') else text
        if "${function.name-with-args}" in text:
            text = text.replace("${function.name-with-args}", "${script.frame:%s.frame_name}" % module)
            debugger.SetInternalVariable("frame-format", text, debugger.GetInstanceName())

    # A function is found by its Sable name, 'b Demo.Point.length', which is
    # its whole DWARF name. With no language given, an lldb may read the name
    # as Swift's or C++'s would be and look up only what follows the last dot
    # ('length'), which no function is called; as C, the name is taken whole.
    # The compile unit says C, so expressions are read as they would be
    # anyway. A language the user chose is left alone.
    language = debugger.GetInternalVariableValue("target.language", debugger.GetInstanceName())
    if language is None or language.GetSize() == 0 or language.GetStringAtIndex(0) in ("", "unknown"):
        run("settings set target.language c")


def __lldb_init_module(debugger, internal_dict):
    _register(debugger, __name__)
