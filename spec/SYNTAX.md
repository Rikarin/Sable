# The Sable Syntax Reference

Complete syntax, construct by construct, with an example for each. The formal
grammar is in [grammar.ebnf](grammar.ebnf); lexical rules are in
[lexical.md](lexical.md).

File extension: `.sab`. Interface files: `.sabi`. Encoding: UTF-8, no BOM.

---

## Contents

1. [Modules and imports](#1-modules-and-imports)
2. [Declarations and visibility](#2-declarations-and-visibility)
3. [Bindings](#3-bindings)
4. [Types](#4-types)
5. [Functions](#5-functions)
6. [Structs](#6-structs)
7. [Classes](#7-classes)
8. [Protocols](#8-protocols)
9. [Unions](#9-unions)
10. [Enums](#10-enums)
11. [Extensions](#11-extensions)
12. [Generics](#12-generics)
13. [Properties, subscripts, operators](#13-properties-subscripts-operators)
14. [Statements](#14-statements)
15. [Pattern matching](#15-pattern-matching)
16. [Expressions and operators](#16-expressions-and-operators)
17. [Closures](#17-closures)
18. [Errors](#18-errors)
19. [Concurrency](#19-concurrency)
20. [Memory annotations](#20-memory-annotations)
21. [Compile-time evaluation](#21-compile-time-evaluation)
22. [Attributes](#22-attributes)
23. [Unsafe](#23-unsafe)
24. [Interop](#24-interop)
25. [Comments and documentation](#25-comments-and-documentation)

---

## 1. Modules and imports

One module declaration per file, first non-trivia item. Module names are
dotted; the root segment is the package name.

```sable
module App.Rendering

import Sable.Collections
import Sable.Math as M
import Sable.IO.{ File, Directory }
import Sable.Text.{ * }             // wildcard; discouraged outside prelude
```

A module may span many files. There is no file/type name correspondence
requirement. Circular module dependencies are an error; circular *file*
dependencies within a module are fine.

Re-export:

```sable
public import Sable.Math.Vectors    // consumers of this module see it too
```

---

## 2. Declarations and visibility

```sable
public    // visible to every consumer of the package
package   // visible within the workspace (all packages in sable.toml members)
internal  // visible within the module  — DEFAULT
private   // visible within the enclosing declaration (and its extensions in-module)
```

Inheritance/override control, Swift-style:

```sable
public open class Widget { }        // may be subclassed outside the module
public sealed class Button: Widget { }   // may not be subclassed further
public abstract class Shape { }     // cannot be instantiated
```

`open` is required for cross-module subclassing and cross-module method
overriding — without it the compiler may devirtualise.

---

## 3. Bindings

```sable
let x = 42                 // immutable, type inferred as Int
let y: F64 = 3.14          // explicit type
var count = 0              // mutable
var total: I64             // declared, must be assigned before use

let (a, b) = pair          // tuple destructuring
let { name, age } = person // record destructuring by field name
let { name: n } = person   // ...with rename

static let MaxUsers = 10_000      // module-level, lazily initialised once
const Pi: F64 = 3.14159265358979  // compile-time constant, inlined
```

Definite-assignment analysis is enforced: reading `total` before assignment is
an error, not a zero.

Shadowing in a nested scope is allowed; shadowing in the same scope is an error.

---

## 4. Types

### Built-in types

```
Bool
I8 I16 I32 I64 I128    U8 U16 U32 U64 U128
Int UInt               // platform word size; Int is the default integer literal type
F16 F32 F64
Char                   // one Unicode scalar value
String                 // UTF-8, growable
Void                   // the unit type; written () in type position
Never                  // the empty type; a function returning Never does not return
```

The literal `none` is sugar for `Optional.None` and is only valid where an
optional is expected. `Optional<T>` is a `@frozen union { Some(T), None }`, so
`.Some(x)` and `.None` are also spellable; `none` is the idiomatic form.

### Type syntax

```sable
T?                  // optional
T!                  // implicitly-unwrapped optional (interop only, discouraged)
T&                  // reference (machine address)
T&<u32>             // relative reference, 4-byte offset from the field
T&<u16 in pool>     // relative reference from a named pool base
T[]                 // fixed-at-construction array
T[8]                // fixed size
T[..]  T[..64]      // inline capacity, resizable within it
T[>..]              // grow-only array
T[>..<]             // grow/shrink array
T[:]                // slice view
[K: V]              // dictionary
(A, B, C)           // tuple
(label: A, b: B)    // labelled tuple
(A, B) -> C         // function type
(A) throws(E) -> C  // throwing function type
async (A) -> C      // async function type
A | B | C           // structural union
some P              // opaque type conforming to P
any P               // existential box conforming to P
(any P)[]           // an array of them: 'some' and 'any' take the whole
                    // type after them, so the array suffix needs parentheses
P & Q               // protocol composition
Foo<T, U>           // generic instantiation
```

### Type aliases

```sable
alias Handler = async (Request) throws(HttpError) -> Response
alias Matrix  = F64[4][4]
alias Id<T>   = U64                            // generic alias (phantom)
public alias Result<T> = Outcome<T, AppError>  // partial application
```

### Structural unions

```sable
alias Json = Null | Bool | F64 | String | Json[] | [String: Json]

fn render(v: Int | String | Vec3) -> String {
    if v is Int    { return v.toString() }    // v narrowed to Int here
    if v is String { return v }               // narrowed to String
    return "\(v.x), \(v.y), \(v.z)"           // narrowed to Vec3
}
```

Narrowing is flow-sensitive: `is`, `match`, `if let`, and `!= None` all narrow.
Structural unions are bounded — all cases must be known at compile time — and
are lowered to a nominal tagged union.

---

## 5. Functions

```sable
fn add(a: Int, b: Int) -> Int { return a + b }

fn greet(name: String) { print("hello, \(name)") }     // -> Void implied

fn single(x: Int) -> Int => x * 2                      // expression body

fn divmod(a: Int, b: Int) -> (q: Int, r: Int) {        // labelled tuple return
    return (q: a / b, r: a % b)
}
```

### Parameter labels

A parameter may declare an **external label** before its internal name:

```sable
fn relocate(from source: Point, to dest: Point) { }
relocate(from: a, to: b)

fn insert(_ item: T, at index: Int) { }    // '_' suppresses the label
insert(x, at: 3)
```

An external label may be a keyword, so `fn first(where p: ...)` and
`parseExpr(tokens, in: arena)` both read naturally.

With a single name, the label is the parameter name and is **optional** at the
call site (the C# rule):

```sable
fn resize(width: Int, height: Int) { }
resize(800, 600)
resize(width: 800, height: 600)    // both legal
```

### Defaults, variadics, output

```sable
fn connect(host: String, port: Int = 443, timeout: Duration = .seconds(30)) { }
fn sum(of values: Int...) -> Int { }
fn tryParse(_ s: String, out value: Int) -> Bool { }   // 'out' = must assign
fn scale(v: inout Vec3, by f: F32) { v.x *= f; v.y *= f; v.z *= f }
```

### Ownership modifiers

An ownership modifier may be written before the parameter name or after the
colon. The second form is preferred once a parameter has an external label,
because `fn read(into buf: inout U8[])` reads better than
`fn read(inout into buf: U8[])`.

```sable
fn store(consuming h: FileHandle)   // takes ownership
fn peek(borrowing h: FileHandle)    // may not escape it
fn edit(v: inout Vec3)              // exclusive mutable access
fn pick(ref a: Node, ref b: Node) -> ref Node    // root inferred as {a, b}
```

### Local and nested functions

```sable
fn outer() {
    fn helper(x: Int) -> Int => x + 1     // captures nothing: a plain function
    let f = (x: Int) => x + offset        // captures 'offset': a closure
}
```

### Overloading

Overloading on parameter types, counts and labels is allowed. Overloading on
return type alone is not. Resolution order: exact match → label match →
implicit widening → protocol conformance → generic.

---

## 6. Structs

```sable
struct Vec3 {
    var x: F32
    var y: F32
    var z: F32

    static let zero = Vec3(0, 0, 0)

    init(_ x: F32, _ y: F32, _ z: F32) {
        self.x = x; self.y = y; self.z = z
    }

    // memberwise init is generated automatically unless one is declared
    // init(x: F32, y: F32, z: F32)

    var length: F32 { get => (x*x + y*y + z*z).sqrt() }

    fn normalized() -> Vec3 {
        let l = length
        return l == 0 ? .zero : Vec3(x/l, y/l, z/l)
    }

    mutating fn scale(by f: F32) { x *= f; y *= f; z *= f }

    operator + (a: Vec3, b: Vec3) -> Vec3 => Vec3(a.x+b.x, a.y+b.y, a.z+b.z)
}
```

Inside a type body a field may be written without `let` or `var`:

```sable
struct Vec3 { x: F32, y: F32, z: F32 }      // three immutable stored fields
```

A bare `name: Type` declares an immutable stored field; `var` is written when
the field is mutable. Members of a one-line body are separated by `,` or `;`;
members on their own lines need no separator. The same holds for union and
enum cases.

- `mutating` is required on methods that modify `self`.
- Structs get a memberwise initialiser unless they declare their own `init`.
- Structs may conform to protocols, be generic, and nest other types.
- `@noncopy struct` makes the type move-only and may declare `deinit`.

```sable
@noncopy struct Lock {
    private let handle: RawPtr
    deinit { sys.unlock(handle) }
}
```

---

## 7. Classes

```sable
public open class Document: Serializable, Equatable {
    public var title: String
    private var revisions: Revision[>..] = []
    public weak var owner: Workspace?
    public unowned let store: Store        // never nil, not retained

    public init(title: String, store: Store) {
        self.title = title
        self.store = store
        super.init()           // required when the base has a non-trivial init
    }

    deinit { store.unregister(self) }

    public open fn render() -> String { return title }

    public override fn description() -> String => "Document(\(title))"

    public static fn empty() -> Document => Document(title: "", store: .shared)
}
```

- Single inheritance, multiple protocol conformance.
- `required init` must be implemented by subclasses.
- `convenience init` must delegate to a designated `init`.
- Members are non-virtual unless declared `open` or `abstract`; `override` is
  mandatory when overriding.
- `final` seals a single member.

### Explicit protocol implementation

When two protocols collide, C#-style explicit implementation disambiguates:

```sable
class Shape: Drawable, Printable {
    fn Drawable.render() -> Image { }
    fn Printable.render() -> Page { }
}
```

---

## 8. Protocols

```sable
public protocol Collection {
    associatedtype Element
    associatedtype Index: Comparable = Int      // with a default

    var count: Int { get }
    subscript(i: Index) -> Element { get }

    fn makeIterator() -> some Iterator<Element>

    // default implementation
    var isEmpty: Bool { get => count == 0 }
}

public protocol Drawable where Self: Sized {
    fn draw(into ctx: inout Context)
    static fn defaultStyle() -> Style
}
```

### Conformance

```sable
struct Range: Collection {
    typealias Element = Int
    typealias Index = Int
    var count: Int { get => upper - lower }
    subscript(i: Int) -> Int { get => lower + i }
    fn makeIterator() -> some Iterator<Int> => RangeIterator(self)
}
```

Conformance is **nominal and explicit** — satisfying the members by accident is
not enough. Retroactive conformance is via `extension` (§11), and coherence is
enforced: a given (type, protocol) pair may be declared in the type's module or
the protocol's module, never a third.

### `some` vs `any`

```sable
fn makeShape() -> some Drawable { return Circle(r: 1) }   // one concrete type, static
fn allShapes() -> any Drawable[] { return [Circle(), Rect()] }  // heterogeneous, boxed

fn draw(_ d: some Drawable) { }   // generic, monomorphised — same as <D: Drawable>
fn draw(_ d: any Drawable) { }    // existential, one copy of the code, vtable call
```

### Protocol composition

```sable
fn process(_ x: some Hashable & Serializable) { }
alias Key = any Hashable & Equatable
```

---

## 9. Unions

Nominal tagged unions with payloads.

```sable
public union Shape {
    Circle(radius: F64)
    Rect(width: F64, height: F64)
    Poly(points: Vec2[>..])
    Empty
}

let s = Shape.Circle(radius: 2.0)
let t: Shape = .Rect(width: 3, height: 4)      // leading-dot inference
```

### Variant-sized unions

```sable
public union Node.. {           // '..' = each value is exactly its variant's size
    Leaf(value: I32)
    Branch(left: Node&<u32>, right: Node&<u32>)
}
```

Variable-mode values are compact and sequential in arrays, references may point
into a payload, but the case of an existing value cannot be changed. See
[../docs/02-memory-model.md §2.5](../docs/02-memory-model.md).

### Generic and recursive unions

```sable
public union Outcome<T, E> {
    Ok(T)
    Err(E)

    fn map<U>(_ f: (T) -> U) -> Outcome<U, E> {
        return match self {
            case .Ok(let v)  => .Ok(f(v))
            case .Err(let e) => .Err(e)
        }
    }
}

public union Tree<T> {
    Leaf
    Node(value: T, left: Tree<T>, right: Tree<T>)   // recursive: boxed automatically
}
```

Unions may have methods, computed properties, static members, and protocol
conformances, exactly like structs.

---

## 10. Enums

Simple, payload-free, with an underlying integer representation.

```sable
public enum Direction { North, South, East, West }

public enum Status: U8 {
    Pending = 0
    Active  = 1
    Closed  = 9
}

@flags
public enum Permissions: U32 {
    None    = 0
    Read    = 1
    Write   = 2
    Execute = 4
    All     = Read | Write | Execute
}

let p: Permissions = [.Read, .Write]
if p.contains(.Write) { }
```

`enum` is a distinct declaration from `union` deliberately: an enum is a named
integer, a union is a tagged sum. Conflating them (as Rust and Swift do) makes
the C ABI story and the "just give me a bitfield" story both worse.

---

## 11. Extensions

```sable
extension String {
    var isBlank: Bool { get => trimmed().isEmpty }
    fn repeated(_ n: Int) -> String { }
}

// retroactive conformance
extension Vec3: Serializable {
    fn encode(to e: inout Encoder) { e.write(x); e.write(y); e.write(z) }
}

// constrained extension
extension Array<T> where T: Comparable {
    fn sorted() -> Self { }
}

// extend a protocol: adds a default to every conformer
extension Collection {
    fn first(where p: (Element) -> Bool) -> Element? { }
}
```

Extensions may add methods, computed properties, subscripts, operators, static
members, nested types, and conformances. They may **not** add stored properties
or designated initialisers.

---

## 12. Generics

```sable
fn max<T: Comparable>(_ a: T, _ b: T) -> T => a >= b ? a : b

struct Cache<Key: Hashable, Value> {
    private var items: [Key: Value] = [:]
}

fn zip<A, B, S1, S2>(_ a: S1, _ b: S2) -> some Sequence<(A, B)>
    where S1: Sequence<A>,
          S2: Sequence<B>,
          A: Sendable { }
```

- **Monomorphised by default.** Each distinct instantiation gets its own code.
- `any P` opts into a single boxed, dynamically dispatched copy.
- `@polymorphic` on a generic function forces a single boxed copy, to control
  code size in large generic trees.
- Constraints: protocol conformance (`T: P`), class inheritance (`T: Widget`),
  same-type (`where S.Element == T`), `Sized`, `Copyable`, `Sendable`.
- Variance: generic types are invariant. Protocol existentials are covariant in
  their associated types only when declared `out`:

```sable
protocol Producer<out T> { fn next() -> T? }
let p: any Producer<Animal> = DogProducer()     // legal: T is 'out'
```

### Conditional conformance

```sable
extension Array<T>: Equatable where T: Equatable { }
extension Optional<T>: Serializable where T: Serializable { }
```

---

## 13. Properties, subscripts, operators

```sable
class Temperature {
    private var celsius: F64 = 0

    var fahrenheit: F64 {
        get => celsius * 9/5 + 32
        set { celsius = (newValue - 32) * 5/9 }
    }

    var readonly: F64 { get => celsius }

    private(set) var updates: Int = 0       // public get, private set

    var tracked: Int = 0 {
        willSet { log("about to become \(newValue)") }
        didSet  { log("was \(oldValue)") }
    }

    lazy var expensive: Report = computeReport()    // computed once, on first read
}
```

A brace directly after a field's initialiser opens an accessor block, not a
trailing closure, when its first token is `get`, `set`, `willSet` or `didSet`:

```sable
var tracked: Int = 0 { willSet { } }    // accessors
let handler = makeHandler { }           // trailing closure
```

### Subscripts

```sable
struct Grid<T> {
    subscript(x: Int, y: Int) -> T {
        get => storage[y * width + x]
        set { storage[y * width + x] = newValue }
    }
    subscript(row r: Int) -> T[:] { get => storage[r*width ..< (r+1)*width] }
}
```

### Operators

```sable
struct Money {
    operator +  (a: Money, b: Money) -> Money
    operator == (a: Money, b: Money) -> Bool
    operator <  (a: Money, b: Money) -> Bool
    operator [] (m: Money, k: String) -> F64        // alternative subscript form
    prefix operator - (m: Money) -> Money
}
```

Overloadable: `+ - * / % & | ^ << >> == != < <= > >= [] ()` and prefix
`- ! ~`. Not overloadable: `&& || ?? ?. = . as is` and compound assignment
(derived from the binary operator automatically).

Custom operators are **not** supported. Precedence is fixed by the grammar.

---

## 14. Statements

```sable
if x > 0 { } else if x < 0 { } else { }

if let user = findUser(id) { use(user) }            // optional binding
if let a = p, let b = q, a.id == b.id { }           // multiple + condition
if case .Circle(let r) = shape { }                  // pattern condition

guard let user = findUser(id) else { return .notFound }
guard x > 0 else { throw .invalid("x must be positive") }

while cond { }
repeat { } while cond

for i in 0..<10 { }
for i in 0...10 { }                   // inclusive
for item in collection { }
for (k, v) in dictionary { }
for i in stride(from: 0, to: 100, by: 5) { }
for item in collection where item.isActive { }

outer: for i in rows {
    for j in cols {
        if done { break outer }
        if skip { continue outer }
    }
}

defer { file.close() }                // runs on scope exit, LIFO

using let f = File.open(path)         // scoped: deinit at end of enclosing block

switch value { }                      // see §15 — 'match' is preferred

return
return expr
throw AppError.notFound(id)
```

### `using`

```sable
using let conn = pool.acquire()
using var tx = conn.begin()
// both released in reverse order at the end of the enclosing block
```

Equivalent to `defer` for `@noncopy` resources, but the binding is consumed at
scope exit so use-after-release is a compile error.

`using` has no braced form of its own. `using let f = open(p) { ... }` would be
read as a call to `open` with a trailing closure, so the resource is scoped to
the enclosing block and an explicit `{ ... }` is used when a narrower scope is
wanted.

---

## 15. Pattern matching

`match` is both a statement and an expression.

```sable
let area = match shape {
    case .Circle(let r)               => Pi * r * r
    case .Rect(let w, let h)          => w * h
    case .Poly(let pts) where pts.count < 3 => 0
    case .Poly(let pts)               => shoelace(pts)
    case .Empty                       => 0
}
```

Exhaustiveness is checked. A non-exhaustive match without `default` is an error
that names the missing cases.

### Pattern forms

```sable
case 42                        // literal
case 1, 2, 3                   // alternatives
case 1...9                     // range
case let x                     // binding (irrefutable)
case let x where x > 0         // binding with guard
case _                         // wildcard
case (let a, let b)            // tuple
case (0, _)                    // tuple with wildcard
case .Circle(let r)            // union case with payload
case .Rect(width: let w, _)    // union case, by label
case Point { x: 0, y: let y }  // record pattern
case let s as String           // type test with binding
case is Drawable               // type test
case .Some(.Ok(let v))         // nested
case [first, _, last]          // array pattern, fixed length
case [let head, ...let tail]   // array pattern with rest
case "a" | "b" | "c"           // string alternatives
```

### `=>` in a `where` clause

Inside a match arm's `where` clause, a trailing `ident =>` is the arm's arrow,
not a one-parameter closure: `case .Busy(let t) where t > limit => retry()`
reads as expected. Parenthesise the closure if one is genuinely wanted there.

### `match` on multiple values

```sable
match (a, b) {
    case (.None, .None)       => .equal
    case (.None, _)           => .less
    case (_, .None)           => .greater
    case (.Some(let x), .Some(let y)) => compare(x, y)
}
```

### `switch` (fallthrough form)

`switch` exists for C-shaped dispatch on integers and enums where explicit
fallthrough is wanted. It does not bind patterns.

```sable
switch code {
    case 200, 201: handleOk()
    case 301: fallthrough
    case 302: redirect()
    default: fail()
}
```

---

## 16. Expressions and operators

### Precedence, tightest first

```
  1  a.b   a?.b   a!   a[i]   f(x)   a as T   a is T
  2  prefix:  -a  !a  ~a  &a  await a  try a  move a
  3  a * b   a / b   a % b
  4  a + b   a - b
  5  a << b  a >> b
  6  a ..< b   a ... b
  7  a < b   a <= b   a > b   a >= b
  8  a == b  a != b   a === b   a !== b
  9  a & b
 10  a ^ b
 11  a | b
 12  a && b
 13  a || b
 14  a ?? b
 15  a ? b : c
 16  a = b   a += b   (and all compound assignments)   — right associative
```

`&&`, `||` and `??` short-circuit. `===` is reference identity (Tier 2 only).

### Member names

Any keyword may be used as a member name after `.`, because the name is
resolved against the receiver's members rather than against the grammar:
`super.init()`, `Type.self`, `File.open(p)`, `x.default`.

### Metatypes

`T.self` is the type `T` as a value. Any type may be written this way, not
only a name: `Int?.self`, `Point[].self`, `Token[>..].self`,
`(Int, String).self`, `(Int) -> Bool.self`. A compound type is only read in
expression position when `.self` follows it, so `xs[0]` stays an index and
`p?.x` stays an optional chain.

### Member access and optionals

```sable
user?.address?.city ?? "unknown"
array?[0]
closure?()
value!                    // force-unwrap; traps on None
```

### Casts and tests

```sable
x as I32              // checked numeric conversion; traps on overflow in debug
x as? Widget          // dynamic downcast, yields Widget?
x as! Widget          // dynamic downcast, traps on failure
x is Drawable         // conformance/type test, narrows in the true branch
x unchecked as I32    // explicit wrapping/truncating conversion
```

### Literals

```sable
42        0xFF      0o755     0b1010_0110     1_000_000
3.14      1e-9      0x1.8p3
true      false
'a'       '\n'      '\u{1F600}'
"text"    "with \(interpolation) inside"
"""
multi-line, leading indentation stripped to the closing delimiter
"""
#"raw \n string, no escapes"#
[1, 2, 3]                  // array
[:]                        // empty dictionary
["a": 1, "b": 2]           // dictionary
(1, "two", 3.0)            // tuple
.Circle(radius: 1)         // inferred-context union case
Point { x: 1, y: 2 }       // record literal; every field carries its value
```

### Ranges

```sable
0..<10        // half-open
0...10        // closed
..<10         // partial, from the start
5...          // partial, to the end
```

---

## 17. Closures

```sable
let double = (x: Int) => x * 2
let add    = (a: Int, b: Int) => a + b
let noop   = () => { }

items.map(x => x.name)                      // types inferred
items.filter { $0.isActive }                // trailing closure, positional params
items.sorted { a, b in a.score > b.score }  // trailing closure, named params

fn withLock<T>(_ l: Lock, _ body: () throws -> T) rethrows -> T { }
withLock(mutex) {
    counter += 1
}
```

### Braces: record literal, trailing closure, or body

Three constructs open with `{`, and the rule between them is:

1. Where a brace would open a statement body — an `if`, `guard` or `while`
   condition, a `for` sequence, a `match` subject — a brace is **always** the
   body. Parentheses and brackets lift that suppression, so
   `if f(Point { x: 1 }) { }` reads as intended.
2. After a name (`Point {`, `Geo.Point {`), the contents decide: `{ }` or
   `{ ident: ...` is a **record literal**, anything else is a **trailing
   closure**.
3. Anywhere else a brace after an expression is a **trailing closure**:
   `items.filter { $0.isActive }`, `withLock(m) { ... }`.

Rule 2 is why a record literal has **no field shorthand**: `Point { x }` would
be indistinguishable from a closure returning `x`, so a field always carries
its value.

### Capture semantics

Closures capture by reference for `var`, by value for `let`, with an explicit
capture list to override:

```sable
let c = [weak self, count = localCount] (x: Int) => {
    guard let self else { return 0 }
    return self.total + count + x
}
```

An escaping closure must be declared `@escaping` on the parameter; non-escaping
closures are stack-allocated and cost nothing.

---

## 18. Errors

Typed, checked, and compiled to a discriminated return value — not to unwinding.

```sable
union IoError { NotFound(path: String), Denied, Interrupted }

fn read(_ path: String) throws(IoError) -> String { }

fn caller() throws(AppError) {
    let text = try read("a.txt")                       // propagates, converted
    let maybe = try? read("b.txt")                     // -> String?
    let text2 = try read("c.txt") catch { "" }         // inline default

    do {
        let t = try read("d.txt")
    } catch .NotFound(let p) {
        log.warn("missing \(p)")
    } catch {
        throw .io(error)                               // 'error' is bound implicitly
    }
}
```

`try e catch { ... }` supplies an inline fallback: the braces hold a block
whose value replaces the failed expression, or an expression after `catch`.

- `throws` with no type means `throws(any Error)`.
- `rethrows` means "throws iff a closure parameter throws".
- Error conversion between types requires a `From` conformance, applied
  implicitly at the `try` site.
- `defer` blocks run on the throwing path.

### Panics

```sable
abort("invariant violated: \(state)")
assert(index < count, "out of range")      // debug only
precondition(index < count)                // all builds
unreachable()                              // -> Never
```

Panics are not catchable in normal code. `Task.catchingPanic { }` exists for
supervisors and test harnesses only.

---

## 19. Concurrency

```sable
async fn fetch(_ url: Url) throws(HttpError) -> Response { }

async fn main() throws {
    let r = await fetch(url)

    async let a = fetch(url1)         // starts immediately
    async let b = fetch(url2)
    let both = await (a, b)           // structured, both awaited here

    await withTaskGroup(of: Response.self) { group in
        for u in urls { group.spawn { await fetch(u) } }
        for await r in group { handle(r) }
    }
}

actor Counter {
    private var value = 0
    fn increment() { value += 1 }          // isolated: callers must await
    nonisolated let id: Uuid               // immutable, freely readable
}

let c = Counter()
await c.increment()
```

`async let` is a statement, not an expression: it starts a child task bound to
the enclosing scope.

- Structured concurrency: child tasks cannot outlive their parent scope.
- `Sendable` is a protocol; crossing an isolation boundary requires it, checked
  at compile time.
- `@MainActor` pins a declaration to the main executor.
- `for await x in stream { }` iterates an `AsyncSequence`.

---

## 20. Memory annotations

```sable
@inline_layout struct Packet { }    // contiguous, pointer-free, serialisable
@noncopy struct Handle { }          // move-only
@reflectable class Model { }        // full runtime reflection
@immutable class Config { }         // deeply immutable, therefore Sendable
@immortal static let shared = ...   // never released

weak var delegate: Delegate?
unowned let parent: Node
unowned(unsafe) let fast: Node      // no trap, no check

region arena { }                    // scoped bump allocator
let node = arena.alloc(Node)
fn build(in r: region) -> ref Node  // root inferred as 'r'
```

See [../docs/02-memory-model.md](../docs/02-memory-model.md) for the semantics.

---

## 21. Compile-time evaluation

```sable
const fn factorial(_ n: Int) -> Int => n <= 1 ? 1 : n * factorial(n - 1)
const Table = buildTable()                     // evaluated at compile time

comptime {
    if Platform.pointerWidth != 64 {
        compileError("64-bit only")
    }
}

@comptime
fn sizeTable<T>() -> Int => MemoryLayout<T>.size

#if DEBUG
    log.verbose = true
#elseif RELEASE
    log.verbose = false
#endif

#if os(macos) && arch(arm64)
#endif
```

`const` declares a compile-time constant on its own (`const Pi = 3.14`) and
modifies a function in `const fn`. Functions and types may also be declared
inside a function body, where they are ordinary statements.

`const fn` bodies run in the compiler's interpreter. They may allocate, loop,
and call other `const fn`s; they may not do I/O or touch `unsafe`.

---

## 22. Attributes

| Attribute | Applies to | Effect |
|---|---|---|
| `@inline(always)` / `@inline(never)` | fn | Inlining hint |
| `@inline_layout` | struct, union | Contiguous pointer-free layout |
| `@noncopy` | struct, union | Move-only |
| `@reflectable` | type | Emit full reflection metadata |
| `@immutable` | class | Deeply immutable; implies `Sendable` |
| `@immortal` | static let | Skip refcounting |
| `@flags` | enum | Bitset semantics |
| `@escaping` | closure param | Closure may outlive the call |
| `@polymorphic` | generic fn | Single boxed instantiation |
| `@deprecated("use X")` | any | Warning at use site |
| `@available(macos: "14.0")` | any | Platform gate |
| `@extern("c", "name")` | fn | Foreign import |
| `@export("name")` | fn, var | Foreign export |
| `@packed` / `@align(16)` | struct | Layout control |
| `@test` / `@bench` | fn | Collected by `sable test` / `sable bench` |
| `@main` | fn, type | Program entry point |
| `@discardable` | fn | Result may be ignored without `_ =` |
| `@MainActor` | any | Main-executor isolation |
| `@frozen` | struct, union | ABI-stable layout; no new cases/fields ever |

User-defined attributes are declared as structs conforming to `Attribute` and
are readable through reflection on `@reflectable` types.

---

## 23. Unsafe

```sable
unsafe {
    let p = RawPtr<U8>.allocate(count: 1024)
    defer { p.deallocate() }
    p.store(0xFF, at: 0)
    let v = p.load(U32.self, at: 4)
}

let buf = UnsafeBuffer<F32>(start: ptr, count: n)
```

`unsafe` blocks are required for raw pointers, unchecked casts between
incompatible layouts, and FFI with non-`@safe`-annotated signatures. Inside an
`unsafe` block, root inference still runs but may be overridden with
`unsafe.assumeRoot(p, as: x)`.

`sable build --deny-unsafe` fails the build on any `unsafe` outside allow-listed
packages.

---

## 24. Interop

### C

```sable
@extern("c", "printf")
fn c_printf(_ fmt: RawPtr<U8>, _ args: CVarArg...) -> I32

@extern("c")
fn sqlite3_open(_ path: CString, _ db: out RawPtr<Opaque>) -> I32

@export("c", "sable_entry")
public fn entry(argc: I32, argv: RawPtr<CString>) -> I32 { }
```

Header import (`sable bindgen`) generates `.sabi` interface files from C headers
and is the recommended path for anything larger than a few functions.

### C++ and Objective-C

Out of scope for v1 beyond `extern "C"` shims. The runtime's object header is
deliberately layout-compatible with neither, to avoid the Swift/ObjC coupling
that constrained Swift's evolution for a decade.

---

## 25. Comments and documentation

```sable
// line comment
/* block comment, /* nests correctly */ */

/// Computes the sum of two numbers.
///
/// - parameter a: the first addend
/// - parameter b: the second addend
/// - returns: the sum
/// - throws: `MathError.overflow` if the result does not fit
///
/// ```sable
/// assert(add(2, 3) == 5)       // doc examples are compiled and run by `sable test`
/// ```
public fn add(_ a: Int, _ b: Int) throws(MathError) -> Int { }
```

Doc comments are Markdown. `sable doc` emits HTML and a JSON symbol graph.
Fenced `sable` blocks inside doc comments are extracted, compiled and executed
as tests.
