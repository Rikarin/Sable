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
    class C         -> C$ref                  $object -> header, then the fields
    union U         -> U$union                $tag (an enumeration of the cases), $payload (a C union)
    T?              -> T?$optional            the same; an optional class is the reference, null for none
    (A) -> R        -> (A) -> R$closure       $function, $context
    any P           -> any P$existential      box (the value, in an any$box$of$T), then a witness per requirement

The formatters match those names by regular expression, so they work with
any lldb that runs Python (no recognizer functions needed), and they read
the runtime's layouts (runtime/*.c) directly.
"""

import lldb

CATEGORY = "sable"

# How much of a string is read, at most.
STRING_LIMIT = 1024

# What a HashMap's class is called, before its type arguments.
HASH_MAP = "Sable.Collections.HashMap$of$"



# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------

def _canonical_name(valobj):
    return valobj.GetType().GetCanonicalType().GetName() or ""


def _raw(valobj):
    """The value as DWARF describes it, past any synthetic children."""
    raw = valobj.GetNonSyntheticValue()
    return raw if raw.IsValid() else valobj


def _read_cstring(process, address, limit=256):
    if address == 0:
        return None
    error = lldb.SBError()
    text = process.ReadCStringFromMemory(address, limit, error)
    return text if error.Success() else None


def _metadata_name(process, object_address):
    """The type name in an object's metadata: what it dynamically is."""
    if object_address == 0:
        return None
    error = lldb.SBError()
    metadata = process.ReadPointerFromMemory(object_address, error)
    if not error.Success() or metadata == 0:
        return None
    name = process.ReadPointerFromMemory(metadata + 8, error)
    if not error.Success():
        return None
    return _read_cstring(process, name)


def _strong_count(process, object_address):
    error = lldb.SBError()
    strong = process.ReadUnsignedFromMemory(object_address + 8, 4, error)
    flags = process.ReadUnsignedFromMemory(object_address + 12, 4, error)
    if not error.Success():
        return None
    return "immortal" if flags & 1 else strong


_class_cache = {}


def _class_types(target):
    """Every class's reference type, by the name its metadata carries.

    The compiler keeps them as the members of one global, sable.debug.classes,
    so that they are found without a search by name — which in some lldbs
    asks every language's type system, Swift's included, at some cost.
    """
    key = (target.GetProcess().GetUniqueID(), target.GetExecutable().fullpath)
    if key in _class_cache:
        return _class_cache[key]
    classes = {}
    # Looked for in the program alone: asked of the whole target, lldb also
    # searches the system's Swift libraries, and tries to load Swift for them.
    module = target.FindModule(target.GetExecutable())
    holder = module.FindFirstGlobalVariable(target, "sable.debug.classes") if module.IsValid() else lldb.SBValue()
    if holder.IsValid():
        sbtype = holder.GetType()
        for i in range(sbtype.GetNumberOfFields()):
            field = sbtype.GetFieldAtIndex(i)
            classes[field.GetName()] = field.GetType()
    _class_cache[key] = classes
    return classes


def _find_type(target, name):
    """A class's reference type by its name; None when there is no such class."""
    return _class_types(target).get(name)


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


def _describe(value):
    """A short text for any value: its summary, its scalar, or its children's."""
    if not value or not value.IsValid():
        return "?"
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
    error = lldb.SBError()
    length = process.ReadUnsignedFromMemory(address + 16, 8, error)
    if not error.Success() or length > (1 << 32):
        return None
    if length == 0:
        return ""
    data = process.ReadMemory(address + 32, min(length, STRING_LIMIT), error)
    if not error.Success():
        return None
    text = data.decode("utf-8", "replace")
    return text + ("..." if length > STRING_LIMIT else "")


def string_summary(valobj, internal_dict):
    text = string_text(valobj)
    if text is None:
        address = _raw(valobj).GetValueAsUnsigned(0)
        return "<null>" if address == 0 else "<unreadable string at 0x%x>" % address
    return _quote(text)


# ---------------------------------------------------------------------------
# Lists: [T], and the arrays and dictionaries built on it
# ---------------------------------------------------------------------------

class ListProvider(object):
    """A list's elements as children: [0], [1], ..."""

    def __init__(self, valobj, internal_dict):
        self.valobj = valobj
        self.count = 0
        self.base = 0
        self.element_type = None
        self.element_size = 0

    def update(self):
        self.count = 0
        pointer = _raw(self.valobj).GetChildMemberWithName("$buffer")
        if pointer.GetValueAsUnsigned(0) == 0:
            return False
        buffer = pointer.Dereference()
        count = buffer.GetChildMemberWithName("count").GetValueAsSigned(0)
        elements = buffer.GetChildMemberWithName("elements")
        self.element_type = elements.GetType().GetPointeeType()
        self.element_size = self.element_type.GetByteSize()
        self.base = elements.GetValueAsUnsigned(0)
        if count < 0 or count > (1 << 32) or self.base == 0:
            return False
        self.count = count
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
        return self.valobj.CreateValueFromAddress("[%d]" % index, self.base + index * self.element_size, self.element_type)


def list_summary(valobj, internal_dict):
    pointer = _raw(valobj).GetChildMemberWithName("$buffer")
    if pointer.GetValueAsUnsigned(0) == 0:
        return "count = 0"
    count = pointer.Dereference().GetChildMemberWithName("count").GetValueAsSigned(0)
    return "count = %d" % count


# ---------------------------------------------------------------------------
# Unions and optionals
# ---------------------------------------------------------------------------

def _active_case(valobj):
    """The case a union value holds, and its payload (None when it has none)."""
    raw = _raw(valobj)
    tag = raw.GetChildMemberWithName("$tag")
    case = tag.GetValue()
    if case is None:
        return None, None
    payload = raw.GetChildMemberWithName("$payload").GetChildMemberWithName(case)
    return case, payload if payload.IsValid() else None


def _is_tuple(value):
    """A tuple, or a union case's named fields ('U.Case$case'): shown as the case's own parentheses."""
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
    if case != "Some":
        return "none"
    return _describe(payload)


# ---------------------------------------------------------------------------
# Class references
# ---------------------------------------------------------------------------

class ObjectProvider(object):
    """An object's fields, as its dynamic class has them, then [strong], its reference count."""

    def __init__(self, valobj, internal_dict):
        self.valobj = valobj
        self.children = []

    def update(self):
        self.children = []
        address = _object_address(self.valobj)
        if address == 0:
            return False
        reference = self.valobj
        dynamic = _metadata_name(self.valobj.GetProcess(), address)
        if dynamic and _canonical_name(self.valobj) != dynamic + "$ref":
            reference = _reference_of_type(self.valobj, self.valobj.GetName(), address) or reference
        body = _object_body(reference)
        if dynamic and dynamic.startswith(HASH_MAP):
            self.children = _hash_map_entries(body)
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


def _hash_map_entries(body):
    """A HashMap's live entries, in the order they were added, each named by its key."""
    entries = body.GetChildMemberWithName("entries")
    provider = ListProvider(entries, None)
    provider.update()
    children = []
    for i in range(provider.num_children()):
        entry = provider.get_child_at_index(i)
        if entry.GetChildMemberWithName("tag").GetValueAsSigned(-1) < 0:
            continue
        key = _describe(entry.GetChildMemberWithName("key"))
        children.append(entry.GetChildMemberWithName("value").Clone("[%s]" % key))
    return children


def object_summary(valobj, internal_dict):
    raw = _raw(valobj)
    address = _object_address(raw)
    if address == 0:
        return "none" if (raw.GetType().GetName() or "").endswith("?") else "<null>"
    process = valobj.GetProcess()
    name = _metadata_name(process, address) or raw.GetType().GetName()
    if name.startswith(HASH_MAP):
        body = _object_body(raw)
        used = body.GetChildMemberWithName("used").GetValueAsSigned(0)
        return "%s count = %d" % (name, used)
    return name


# ---------------------------------------------------------------------------
# Closures and existentials
# ---------------------------------------------------------------------------

def _function_name(value):
    address = value.GetValueAsUnsigned(0)
    if address == 0:
        return None
    sbaddress = value.GetTarget().ResolveLoadAddress(address)
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
                    if child.GetName() == "$header":
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
    if raw.GetNumChildren() == 0:
        return None, None
    held = _object_address(raw.GetChildMemberWithName("box"))
    if held == 0:
        return None, None
    process = raw.GetProcess()
    box = _metadata_name(process, held)
    prefix = "any$box$of$"
    if not box or not box.startswith(prefix):
        return box, None
    reference = _reference_of_type(raw, "box", held)
    if reference is None:
        return box[len(prefix):], None
    value = _object_body(reference).GetChildMemberWithName("value")
    dynamic = box[len(prefix):]
    # A class held in it may be a subclass: its own metadata says.
    if _canonical_name(value).endswith("$ref"):
        inner = _metadata_name(process, _object_address(value))
        if inner:
            dynamic = inner
    return dynamic, value


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


def tuple_summary(valobj, internal_dict):
    raw = _raw(valobj)
    if _canonical_name(raw).endswith("$closure"):
        return closure_summary(valobj, internal_dict)
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
        ("\\$list$", "list_summary", "-e"),
        ("\\$union$", "union_summary", ""),
        ("\\$optional$", "optional_summary", ""),
        ("\\$ref$", "object_summary", "-e"),
        ("\\$closure$", "closure_summary", ""),
        ("\\$existential$", "existential_summary", "-e"),
        ("^\\(.*\\)$", "tuple_summary", ""),
        ("^SableWeakEntry \\*$", "weak_summary", "-v"),
    ]
    synthetics = [
        ("\\$list$", "ListProvider"),
        ("\\$union$", "UnionProvider"),
        ("\\$optional$", "OptionalProvider"),
        ("\\$ref$", "ObjectProvider"),
        ("\\$closure$", "ClosureProvider"),
        ("\\$existential$", "ExistentialProvider"),
    ]
    for regex, function, flags in summaries:
        run('type summary add -w %s -p -r %s -x "%s" -F %s.%s' % (CATEGORY, flags, regex, module, function))
    for regex, provider in synthetics:
        run('type synthetic add -w %s -p -r -x "%s" -l %s.%s' % (CATEGORY, regex, module, provider))
    run("type category enable %s" % CATEGORY)

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
