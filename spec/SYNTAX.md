# The Sable Syntax Reference

Complete syntax, construct by construct, with an example for each. The formal
grammar is in [grammar.ebnf](grammar.ebnf); lexical rules are in
[lexical.md](lexical.md).

File extension: `.sb`. Interface files: `.sbi`. Encoding: UTF-8, no BOM.

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
package   // visible within the workspace (all packages in workspace.sb members)
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
T&                  // reference (machine address); the one way to write one
T&<u32>             // relative reference, 4-byte offset from the field
T&<u16 in pool>     // relative reference from a named pool base
T&?                 // optional reference
T[]                 // fixed-at-construction array
T[8]                // fixed size
T[..]  T[..64]      // inline capacity, resizable within it
T[>..]              // grow-only array
T[>..<]             // grow/shrink array
T[:]                // slice view
[K: V]              // dictionary (Dictionary<K, V>, K: Hashable), in insertion order
(A, B, C)           // tuple
(label: A, b: B)    // labelled tuple
(A, B) -> C         // function type
(A) throws(E) -> C  // throwing function type
async (A) -> C      // async function type
(inout A) -> ()     // takes the caller's A, to change in place; '()' is Void
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
fn edit(v: inout Vec3)              // exclusive mutable access
fn area(_ r: Rect&) -> F64          // shared: the caller's Rect, read in place
fn pick(_ a: Node&, _ b: Node&) -> Node&   // root inferred as {a, b}
```

A parameter that refers to its argument says so in its type, `T&`: it reads
the caller's storage in place for the call. Whether a reference rooted in it
may be returned is decided by root inference from the body and the result
type ([docs/02 §2.4](../docs/02-memory-model.md)). Classes and `@noncopy`
values are passed by reference without it, so `T&` is written mostly on large
structs and on functions that return a reference. `ref T`, `ref a: T` and
`borrowing h: T` are not Sable; the words are reserved so that the compiler
says so (S1013).

`&` at a call marks an argument the call may write: it is required for an
`inout` or `out` argument (an `out` one may be written `out x` instead), and
an error anywhere else (S3209). A `T&` parameter only reads, so it takes a
plain `x`. Anywhere else, `&expr` makes a reference value:
`let first = &bag.items[0]` is an `Item&`, root-checked like any other.

```sable
var v = Vec3(x: 1, y: 2, z: 3)
edit(v: &v)                         // may be written: '&'
print(area(bounds))                 // read in place: plain
let first = &bag.items[0]           // an 'Item&'
```

A function type's parameter may be `inout` too, and so may a closure's; the
call passes `&x`, as to a declared function:

```sable
fn apply(_ change: (inout Int) -> ()) -> Int { var n = 1; change(&n); return n }
apply { (v: inout Int) in v += 1 }   // 2
apply { $0 *= 7 }                     // 7: '$0' is inout as the type says
```

### Local and nested functions

```sable
fn outer() {
    fn helper(x: Int) -> Int => x + 1     // captures nothing: a plain function
    let f = (x: Int) => x + offset        // captures 'offset': a closure
}
```

### Generators

A function, method, computed property or closure whose result is
`some Sequence<T>` or `some Iterator<T>` and whose body contains `yield` is a
**generator**: each `yield` gives the next element, and `return` (with no
value), or the end of the body, ends it.

```sable
fn evens(upTo n: Int) -> some Sequence<Int> {
    var i = 0
    while i <= n {
        yield i
        i += 2
    }
}

for x in evens(upTo: 6) { print(x) }    // 0 2 4 6

let squares: some Sequence<Int> = { () -> some Sequence<Int> in
    for i in 0..<10 { yield i * i }
}()

extension Sequence {                     // for every sequence, each its own
    fn everyOther() throws(Failure) -> some Sequence<Element> {
        var keep = true
        for try e in self {
            if keep { yield e }
            keep = !keep
        }
    }
}
```

- **Lazy.** Calling it runs nothing: it captures its arguments (and `self`)
  by value. The body runs a step at a time, as `next()` asks for each
  element; each `makeIterator()` starts it afresh. An `inout` or `out`
  parameter is an error (S3483) — the call has returned before the body runs.
- **Throwing.** A generator declared `throws(E)` (bare `throws`: `any
  Error`) gives a sequence whose `Failure` is E: an error its body throws
  comes out of `next()`, so going through it is `for try`. Its call only
  captures, and throws nothing. Once it has thrown it is finished, its
  pending defers run, and `next()` gives none.
- **`defer`.** A `defer` runs as its scope is left, which may be when the
  generator finishes or when its iterator is dropped before it does — C#'s
  `finally` on `Dispose`. A `defer` body cannot `yield` (S3481).
- **`async`.** An `async` generator's result is `some AsyncSequence<T>` (or
  `some AsyncIterator<T>`); its body may `await`, and `for await` goes
  through it.

```sable
async fn lines(_ f: File) -> some AsyncSequence<String> {
    while let line = await f.readLine() {
        yield line
    }
}
```

`yield` anywhere else is an error (S3480), as is a result that names no
sequence of what it yields, and `return` with a value in a generator (S3482).
The sequence and its iterator are structs the compiler writes: the iterator
holds the arguments, the body's variables and where it stopped, so iterating
one costs no allocation, and a copy of an iterator goes on from where the
original was, on its own. A body with a `defer` keeps that state in one
object instead, whose last iterator's going runs the defers still pending; its
copies share it.

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
- `convenience init` must delegate to a designated `init`, `self.init(…)`
  (S3141); a struct's initialiser may delegate the same way. One that
  delegates leaves the fields' initial values to the one it calls.
- A class that declares no `init`, whose own stored fields all have initial
  values, is made with `init()` — or, a subclass, inherits its superclass's
  initialisers, each of which sets the subclass's fields and then calls the
  one it inherits.
- A subclass's own initialiser calls `super.init(…)` on every path that
  returns, or delegates with `self.init(…)` (S3143). Only a superclass made
  with the implicit `init()` may be left to it: one that never calls
  `super.init` has that `init()` called for it.
- In an expression, `Self` is the enclosing type: `Self(x: 3)` makes one,
  `Self.zero` reads a static member.
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

An associated type may carry a `where` clause, which is the protocol's: it
holds of every conformer, and wherever the protocol constrains a type. Here
an `S.Iter.Element` is an `S.Element` for every `S: Sequence`, and a
conformer that names only its iterator has that iterator's elements:

```sable
public protocol Sequence {
    associatedtype Element
    associatedtype Iter: Iterator where Iter.Element == Element
    fn makeIterator() -> Iter
}
```

### Conformance

```sable
struct Range: Collection {
    alias Element = Int
    alias Index = Int
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

A protocol's associated types may be given in angle brackets after a `some`,
in the order the protocol declares them: `some Sequence<Int>` is a sequence
whose `Element` is `Int`, which its users may rely on, and `some
Iterator<String>` an iterator of strings.

```sable
fn total(_ values: some Sequence<Int>) -> Int {
    var sum = 0
    for v in values { sum += v }
    return sum
}
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

A case's value is the integer it writes — a literal, an earlier case, or
those combined with `| & ^ + - * << >> ~` — or else the one after the
previous case's, starting from 0; in a `@flags` enum, the next bit up,
starting from 1. The type is the integer the base list names, or `Int`.
Outside `@flags` no two cases share a value (S3142).

- `Status.Closed as U8` is the integer a case is; `9 as Status` is the case
  an integer is, and traps when no case is it.
- `"\(direction)"` is the case's name, `East`.
- A `@flags` enum's value is any set of its cases: `[.Read, .Write]` is the
  two together, `[]` none of them; `|`, `&`, `^` and `~` combine values,
  `==` compares them, and `p.contains(.Write)` is true when every flag of the
  argument is set. Interpolated, a value is the name of the case it is
  exactly, or else its integer. A `match` on one needs a `default`.
- `@derive(Equatable, Hashable, Comparable, Debug)` on an enum compares cases,
  orders them as they are declared, and describes one as `Direction.East`.
  A `@flags` enum derives nothing: its values are compared as integers.

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

### Array extensions

An array type has no name to write parameters after, so an extension of every
array of a kind declares them after the keyword, as a generic function does
after `fn`:

```sable
// every grow-only array, whatever its element
extension<Element> Element[>..] {
    mutating fn appendTwice(_ e: Element) { append(e); append(e) }
}

// every array: each kind can be viewed as a slice, so a slice's members are all of theirs
extension<Element: Equatable> Element[:] {
    fn contains(element: Element) -> Bool {
        for x in self { if x == element { return true } }
        return false
    }
}

// one element type alone
extension U8[:] { fn toString() -> String { } }
```

- Each kind is extended on its own: `T[]` (every fixed size), `T[..]` (every
  inline capacity), `T[>..]`, `T[>..<]`, `T[:]`. No size or capacity is written.
  What `T[:]` declares, every kind has; a kind's own member of the same name
  comes first.
- Inside, `self` is the array. The parameters may be constrained, inline or
  with `where`; the members are an array's only where its element meets them.
- Each parameter must appear in the array type. An array extension adds
  members, not conformances.
- Its members are generic over the parameters and monomorphised per element
  type, like any generic. `Sable.Core` declares the array family's own members
  (`count`, `append`, `contains`, …) this way.

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
  code size in large generic trees: each type parameter stands for `any` of
  its constraints, so a value of it is passed boxed with its type's witness
  table, and a result of a type parameter is taken back out of its box as
  the type the call gives it. The function's own type parameters must each be
  constrained to protocols alone, with no associated types or requirements
  mentioning `Self`; each parameter and the result must be a type parameter
  or mention none, and none of a type parameter `inout` (S3512). Taken as a
  value rather than called, the function is still compiled for its types.
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

`willSet` and `didSet` observe a stored property: a store to it calls
`willSet` with the new value (`newValue`, or the name it gives in
`willSet(name)`), stores it, then calls `didSet` with the old one
(`oldValue`); compound assignment stores through them too. In its own
observers, and in its type's initialisers, the property names its storage,
so the observers do not run for the changes they make themselves, nor while
the value is being made; a record literal or memberwise initialiser sets it
without observing either. An observed property is a `var`, declares no `get`
or `set`, and belongs to a struct, class or actor (S3140).

A property with a getter alone may write it without the braces or the
keyword: `=>` and the value, as a function written `=> e` does.

```sable
struct Rect {
    var width: F64, var height: F64
    var area: F64 => width * height             // { get => width * height }
}
```

Either accessor may be `=> e` rather than a block; a setter's is an
assignment, and its incoming value is `newValue` (or the name it gives in
`set(name)`).

A property's accessors may say `field`: storage of the property's own, which
only they can name. The property is then stored — laid out with the type's
other fields, starting from its initialiser, which it must have — but read and
written through its accessors. `get` or `set` written alone is the plain one,
`get => field` or `set => field = newValue`; an accessor it does not declare,
it does not have, as for any computed property. Descriptions and reflection
show the storage under the property's name. An extension cannot add one,
since `field` is storage.

```sable
struct Account {
    var owner: String = "" {
        get => field
        set => field = newValue.trimmed()
    }
    var limit: Int = 100 {
        get
        set { field = newValue < 0 ? 0 : newValue }
    }
}
```

A `lazy var` is computed from its initialiser the first time it is read, and
kept; the initialiser may use `self`, as a method does. Assigning it replaces
the value. It is a `var`, has an initialiser and no accessors of its own, and
belongs to a struct, class or actor value (S3113). Since the first read of a
struct's lazy property writes it, it is read only where the struct can change:
from a `var`, or in a `mutating` method.

```sable
struct Document {
    var text: String
    lazy var words: String[>..] = text.split(separator: ' ')   // split on the first read
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

// Where a value is wanted, an 'if' with an 'else' gives one: each
// branch's last line, all of one type
let size = if n > 3 { "big" } else { "small" }

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
for try line in file.lines() { }      // a sequence that may fail as it goes

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
case let v?                    // an optional holding a value: .Some(let v)
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

The values are compared with the subject in order, and `default`, wherever it
is written, runs when none is equal. A case ends where the next begins:
`fallthrough` goes on into the case written after it, `break` leaves the
`switch` early, and `continue` goes on with the loop around it.

---

## 16. Expressions and operators

### Precedence, tightest first

```
  1  a.b   a?.b   a!   a[i]   f(x)   a as T   a is T
  2  prefix:  -a  !a  ~a  &a  ^a  await a  try a  move a
  3  a * b   a / b   a % b   a &* b
  4  a + b   a - b   a &+ b   a &- b
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

`&+`, `&-` and `&*` are the wrapping operators: a fixed-width integer's sum,
difference and product with the low bits kept, never trapping (where `+`,
`-` and `*` trap on overflow). They are `FixedWidthInteger`'s, as
`wrappingAdd`, `wrappingSub` and `wrappingMul` are, and bind as `+` and `*`
do; `&+=`, `&-=` and `&*=` assign through them. `&` followed by `+`, `-` or
`*` is always one of these tokens, so a bitwise-and with a negated operand
is written with a space: `a & -b`.

### Member names

Any keyword may be used as a member name after `.`, because the name is
resolved against the receiver's members rather than against the grammar:
`super.init()`, `Type.self`, `File.open(p)`, `x.default`.

### Metatypes

`T.self` is the type `T` as a value. Any type may be written this way, not
only a name: `Int?.self`, `Point[].self`, `Token[>..].self`,
`(Int, String).self`, `(Int) -> Bool.self`. A compound type is only read in
expression position when `.self` follows it, so `xs[0]` stays an index and
`p?.x` stays an optional chain — or, for a dictionary type and a growable
array's, when it is called: `[String: Int]()`, `Int[>..]()` and
`Int[>..<]()` call the type's `init()`, an empty one.

The type of `T.self` is the metatype `T.Type` (written after a type's name,
unless the type has a nested type called `Type`). Its one value is the type,
so it holds nothing at run time. `Int?.Type` and `Int[>..].Type` are the
metatypes of compound types. Two metatypes compare with `==` and `!=`:
equal when they are the type of the same type, which in generic code is
decided for each instance. A `T.Type` parameter infers `T`, and a metatype
value reaches the type's static members:

```sable
fn make<T: Zero>(_ type: T.Type) -> T { return type.zero() }
let m = make(Meters.self)              // T is Meters
let info = TypeInfo.of(Point.self)     // of<T: Reflectable>(_ type: T.Type)
```

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

`as` and `unchecked as` convert between numeric types. `unchecked as` never
traps: an integer keeps its low bits, and a float goes toward zero and stops
at the target's bounds (NaN gives 0). A cast that could never succeed — between
unrelated types, or classes on different lines of inheritance — is an error
(S3066).

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
[String: Int]()            // empty dictionary, by its type's init()
Int[>..]()                 // empty grow-only array, likewise; and Int[>..<]()
(1, "two", 3.0)            // tuple
.Circle(radius: 1)         // inferred-context union case
Point { x: 1, y: 2 }       // record literal; every field carries its value
```

### Interpolation and format specs

A hole, `\(expr)`, writes its value: a string as it is, a number, a `Bool`
or a `Char` as text, a `Formattable` value as its `format` writes it, an
enum as its case's name, a `Debug` value as it describes itself, and
anything else as `Reflect.describe` does. A colon after the expression starts
a **format spec**, which runs to the hole's closing paren:

```sable
"\(n:#06x)"          // 0x00ff
"\(price:>10.2)"     // right-aligned in 10, two decimals
"\(total:,)"         // 1,234,567
"\(ratio:.1%)"       // 12.5%
"\(name:*^9)"        // **sable**
"\(value:?)"         // as Debug describes it
"\(ok ? a : b:x)"    // a ternary's colon is the expression's own
```

A spec is text, not tokens, in this form, each part optional:

```
[[fill] align] [sign] [#] [0] [width] [, or _] [.precision] [kind]
```

| Part | Meaning |
|---|---|
| `align` | `<` left, `^` centre, `>` right (default: a number right, text left); a `fill` scalar before it pads (default a space) |
| `sign` | `+` every number signed, `-` only negative ones (default), a space where `+` would be |
| `#` | an integer's radix prefix (`0x`, `0o`, `0b`); a float's trailing zeros kept by `g` |
| `0` | a number padded with zeros after its sign, to the width |
| `width` | the least number of scalars written |
| `,` `_` | a separator between each three digits of a number's whole part (each four in another radix, for `_`) |
| `.precision` | digits after a float's point (all of `g`'s digits); a string's scalars kept |
| `kind` | `d x X o b` (integers), `e E f g G %` (floats), `?` (anything: `Debug`) |

A float without a precision is written in its shortest digits that read
back as the same value; with one, its exact value is rounded to nearest,
ties to even. The fill cannot be a quote, a backslash or a paren, which end
or nest the hole.

The spec becomes a `FormatSpec` (Sable.Core) given to the value's
`Formattable.format`, so a type of the program's own may honour it. The
compiler reads the spec (S3660) and checks it against the value's type: a
kind the type is not written in (S3661), an option it would ignore — a
precision on an integer, a sign on a string (S3662) — and anything but a
fill, an alignment, a width or `?` for a type that is not `Formattable`
(S3663), which is padded as it would be written without a spec.

### Ranges

```sable
0..<10        // half-open
0...10        // closed
..<10         // partial, from the start
5...          // partial, to the end
```

### Indexing from the end

Prefix `^n` is a position counted back from a collection's end, a
`FromEnd` (C#'s hat): `^1` is the last element, `^2` the one before it,
and `^0` the end itself. It binds tighter than every binary operator, so
a range with a bound counted from the end needs no parentheses. Prefix
`^` means nothing else; `a ^ b` is still exclusive or.

```sable
xs[^1]        // the last element
xs[1..<^1]    // all but the first and the last
xs[^3...]     // the last three
xs[^2...^1]   // the last two
xs[..<^1]     // all but the last
s[^1]         // a String's last character, found stepping back
```

A range with either bound written `^n` is a `RelativeRange`, which the
collection it slices resolves against its own end. Every bidirectional
collection reads an element by a `FromEnd` — in constant time, `count - n`,
on one with random access (every array, `List`, `Deque`), one step at a
time back from the end on a `String` — and every collection has one
slicing subscript, which takes `some RangeExpression`: any range of its
indices, or a `RelativeRange`. On an array, neither a `FromEnd` nor a
`RelativeRange` is made as the program runs: what is left after
optimisation is arithmetic on the count, and the bounds check.

A mutable collection (every array but a slice, `List`, `Deque`) is written
through a `FromEnd` as well, and one that can replace a range of its
elements (a growable array, `List`, `SmallVec`) through any range,
`RelativeRange` among them; the elements stored may be more or fewer than
the range's:

```sable
xs[^1] = 9         // the last element replaced
xs[^2] += 1
xs[1..<^1] = [7]   // all but the first and the last, replaced by one
```

Where a collection has a subscript of the same labels that only reads and
one that stores as well, a store takes the one that stores and a read the
one that only reads. A collection that only reads, as a `String`, refuses
`s[^1] = c` (S3010).

`^0` is a bound, never an element: reading at it traps, and written as an
index, `xs[^0]`, it is an error (S3090). A position from the end outside
the collection traps, saying so: `index ^5 is out of range for count 3`.
Unlike a negative index, counting from the end is in the type, so a
computed index that goes negative traps rather than counting from the
other end.

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

// What a closure throws is what its body throws, unless it says: 'throws(E)'
// as a function's, or bare 'throws' for 'throws(any Error)'. Never
// 'rethrows': a closure's type says what it throws.
let parse = { (s: String) throws(ParseError) -> Int in try Int.parse(s) }
let check = (s: String) throws(ParseError) => try validate(s)

// After the first trailing closure, more, each with its parameter's label,
// starting on the line the one before ends on
attempt {
    try fetch()
} onFail: {
    cached()
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

`[weak x]` and `[unowned x]` capture a class instance without keeping it
alive, under a name of the closure's own — `self` included. A weak capture is
optional, and reads as `none` once the object is gone, so the body unwraps
it (`guard let self`, `if let s = self`, `self?.total`); a member reached
implicitly through a weak `self` is an error until it is unwrapped. An
unowned capture is the object itself, and reading it once the object is gone
traps. `[weak x = expression]` captures what the expression gives.

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
- `Error` is Sable.Core's `protocol Error: Debug, Sendable`, with `var source:
  (any Error)? { get }` and `fn description() -> String`, each with a default.
  `any Error` reaches them (`error.description()`, `error.source`) and is an
  `any Debug`. Any type may still be thrown; one that is not an Error is
  described by what it is.
- `rethrows` means "throws iff a closure parameter throws". `rethrows(E)`
  throws E as well: a Sequence algorithm is `rethrows(Failure)`, throwing what
  its closures throw and what going through the sequence throws, so over an
  array with closures that cannot throw it needs no `try`.

### Throwing iteration

`Iterator` and `Sequence` (and their async kinds) have a `Failure`, what
going through them may throw, Never unless an iterator's `next()` throws:
`mutating fn next() throws(Failure) -> Element?`. A loop over a sequence
whose Failure is not Never is `for try` (`for try await`), and leaves with
the error as a `try` would; a missing `try` is an error (S3032), as on a
call. Arrays, ranges, collections and generators that do not throw cost
nothing and need none.

```sable
fn parse(_ path: String) throws(IoError) -> some Sequence<Record> {
    for line in try read(path).split(separator: "\n") {
        yield try Record(line)
    }
}

for try record in parse("a.csv") { use(record) }
let names = try parse("a.csv").map { $0.name }     // rethrows(Failure)

fn total<S: Sequence>(_ s: S) throws(S.Failure) -> Int where S.Element == Int {
    var sum = 0
    for try x in s { sum += x }
    return sum
}
fn quiet<S: Sequence>(_ s: S) -> Int where S.Element == Int, S.Failure == Never {
    var sum = 0
    for x in s { sum += x }
    return sum
}
```

`some Sequence<T>` says Failure is Never — an associated type with a default
that a `some P<…>` leaves out is that default — and `some Sequence<T, E>`
says it is E; in a generator's result, a Failure left out is what the
generator throws. A `Collection` cannot fail.
- Error conversion between types requires a `From` conformance, applied
  implicitly at the `try` site.
- `defer` blocks run on the throwing path.

### Panics

```sable
abort("invariant violated: \(state)")
assert(index < count, "out of range")      // debug only
precondition(index < count)                // all builds
fatalError("cannot go on")                 // -> Never
todo()                                     // -> Never: not written yet
unreachable()                              // -> Never
```

Each reports the place it was called from: a parameter whose default is
`#file`, `#line`, `#column` or `#function` takes the call's place, not its
own.

`assert` checks only where `DEBUG` is defined (an unoptimised build, or a
profile with `debug-assertions`), in the library's code as in the
program's. A file that imports Sable.Testing means that module's `assert` by
the bare name, as an import's names come before Sable.Core's: a test's
assertion, checked in every build.

Panics are not catchable in normal code. `Task.catchingPanic { }` exists for
supervisors and test harnesses only.

---

## 19. Concurrency

```sable
async fn fetch(_ url: Url) throws(HttpError) -> Response { }

async fn main() throws {
    let r = await fetch(url)

    async let a = fetch(url1)         // starts immediately, alongside
    async let b = fetch(url2)
    let both = try await (a, b)       // structured, both awaited here

    await withTaskGroup(of: Response?.self) { group in
        for u in urls { group.spawn { try? await fetch(u) } }
        for await r in group { handle(r) }   // in the order they finish
    }

    let worker = Task.spawn { await work() }    // a child of this task
    worker.cancel()                             // and of its children
    let free = Task.detached { await poll() }   // no parent: the one way out
}

async fn work() {
    while !Task.isCancelled {
        try? await Task.sleep(nanoseconds: 1_000_000)  // throws CancellationError once cancelled
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
the enclosing scope. Its value is the child's work, which may await and throw
without saying so; reading the name awaits it, and tries when it may throw.
A child never read, or left behind by a `return` or a throw, is cancelled and
awaited as the scope ends.

- Structured concurrency: child tasks cannot outlive their parent scope. A
  task spawned from another (`Task.spawn`, an `async let`, a group's
  `spawn`) is its child: it takes its priority (`Task.currentPriority`;
  `Task.spawn(priority:)` gives another), is cancelled with it, and its
  parent finishes only once it has. `Task.detached` has no parent.
- `withTaskGroup(of:)` and `withThrowingTaskGroup(of:)` run a body with a
  group: `spawn` adds a child, `next()` and `for await` (`for try await`)
  give results in the order the children finish, `cancelAll()` cancels them,
  and the children the body leaves are awaited as it returns — cancelled
  first when a throwing body throws.
- Cancellation is cooperative: `task.cancel()` marks the task and its
  children; a task asks `Task.isCancelled` or `try Task.checkCancellation()`,
  a sleep it is in ends at once and throws `CancellationError`, and
  `withTaskCancellationHandler(operation:onCancel:)` runs a handler as the
  task is cancelled.
- An actor runs one call at a time. A call into it from outside, or a
  read of its state, awaits its turn; a call it makes to itself, or to a
  `nonisolated` member, does not. Actors are reentrant where they wait: a
  call suspended at an `await` inside the actor lets others in, and goes
  on once it has the actor again, so state read before an `await` may
  have changed after it.
- `Sendable` is a protocol; crossing an isolation boundary requires it, checked
  at compile time. A task's closure — `Task.spawn`'s, `Task.detached`'s, a
  group's `spawn`'s, `Task.blocking`'s, an `async let`'s — crosses one:
  what it captures must be Sendable, and a `var` is captured only by value
  (S3236). A closure parameter run as a task is declared `@Sendable`, and
  takes a closure written there, a named function, or another `@Sendable`
  parameter. In an `unsafe` block the programmer vouches for what is shared.
- `@MainActor` pins a declaration to the main executor: the main thread,
  which runs the program's entry.
- `for await x in stream { }` iterates an `AsyncSequence`; `for try await`
  one whose Failure is not Never.

---

## 20. Memory annotations

```sable
@inline_layout struct Packet { }    // contiguous, pointer-free, serialisable
@noncopy struct Handle { }          // move-only
@reflectable class Model { }        // full runtime reflection
@immutable class Config { }         // deeply immutable, therefore Sendable
@immortal static let shared = Registry()  // never released

weak var delegate: Delegate?
unowned let parent: Node
unowned(unsafe) let fast: Node      // no trap, no check

region arena { }                    // scoped bump allocator
let node = arena.alloc(Node)
fn build(in r: region) -> Node&     // root inferred as 'r'; 'in' is the label
let tree = build(in: arena)
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

A `const` is computed while the program is compiled and written, as a
literal, wherever it is read; its type may be left out when its value is a
literal, another constant, a call, or arithmetic on those. What a constant's
value, a `const fn` or a `comptime` block calls must be a `const fn`,
something the compiler wrote (a memberwise initialiser), or a member of
`Sable.Core` that neither reads, writes nor waits (S3220). A trap while
computing one — overflow, division by zero, a loop past the step limit — is
an error at compile time (S3201 for arithmetic, S3221 otherwise), and so is reaching `compileError("...")`
(S3222). A `comptime` block, at the top level or in a body, runs once, while
compiling, and leaves no code; one in a body cannot see the function's
parameters or locals. `Platform.os`, `Platform.arch` and
`Platform.pointerWidth` are the target's, as constants.

`DEBUG` is defined in an unoptimised build (and by `sable check`), `RELEASE` in
an optimised one; never both. A package's profile may choose with
`debug-assertions` (docs/05 §5.2.11), whatever its `opt-level`.

---

## 22. Attributes

| Attribute | Applies to | Effect |
|---|---|---|
| `@inline(always)` / `@inline(never)` | fn | Inlined at every call, or at none: SIL's inliner and LLVM's (`alwaysinline`, `noinline`); always does not inline a function into itself, nor one that throws |
| `@inline_layout` | struct, union | Contiguous pointer-free layout |
| `@noncopy` | struct, union | Move-only |
| `@reflectable` | type | Emit full reflection metadata |
| `@immutable` | class | Deeply immutable; implies `Sendable` |
| `@immortal` | static let | Skip refcounting |
| `@flags` | enum | Bitset semantics |
| `@escaping` | param of function type | Closure may outlive the call |
| `@Sendable` | param of function type | Closure runs as a task: its captures are Sendable (§19) |
| `@polymorphic` | generic fn | Single boxed instantiation |
| `@deprecated("use X")` | any | Warning at use site |
| `@available(macos, linux)` | any | Platform gate: a use in a build for another platform is S3096 |
| `@extern("c", "name")` | fn | Foreign import |
| `@export("name")` | fn, var | Foreign export |
| `@packed` / `@align(16)` | struct | Layout control |
| `@test` / `@bench` | fn | Collected by `sable test` / `sable bench` |
| `@fuzz` | fn taking `U8[:]` | A fuzz target, run by `sable fuzz` under libFuzzer: it fails by trapping or throwing |
| `@main` | fn | Program entry point: a module-level fn taking nothing and returning nothing or an `Int` exit status, one per module (S3514) |
| `@generator("name", members: [...], always: true)` | fn in `generators/` | A source generator, run for declarations marked `@name`; `members` are the attributes it reads on fields, cases and parameters; `always` runs a package generator for every package (docs/08) |
| `@discardable` | fn | Result may be ignored without `_ =` |
| `@MainActor` | any | Main-executor isolation |
| `@frozen` | union | Never gains a case: another module may match it without `default` |

`@packed` lays a struct's fields one after another with no padding, and
aligns the struct to a byte. `@align(n)`, a power of two up to 16 (the
alignment of every block the runtime allocates; more is S3510), aligns the
struct to at least `n` and rounds its size up to a multiple of it; with
`@packed`, the struct is aligned to exactly `n`. Both are C's layouts —
`__attribute__((packed))` and `_Alignas(n)` — and a struct passed to or from
C is passed as C passes its own. A field of a packed struct passed `inout`
is copied to an aligned place and back. On an `@inline_layout` struct,
`@align(n)` puts the struct at a multiple of `n` from the start of a value
that holds it, and rounds its fixed part up to one.

User-defined attributes are declared as structs conforming to `Attribute` and
are readable through reflection on `@reflectable` types:

```sable
struct Route: Attribute {
    var path: String
    var method: String = "GET"
}

@reflectable
@Route("/users", method: "POST")
struct Users { }

for route in TypeInfo.of(Users.self).attributes(of: Route.self) { }
```

The attribute's arguments are checked as a call to one of the struct's
initialisers would be, and are constants — literals, cases, and arrays and
tuples of them (S3511). One may be written on any declaration, and more than
once; on a `@reflectable` type, `attributes(of:)` gives the values of those
of one type in the order written. A name that is neither in this table nor
such a struct, or `@derive`, `@indirect`, `@allow` and `@warn`, is S3092; one
of the table's written where it does not apply is S3093, with arguments it
does not take S3094, and twice on one declaration S3513 (`@derive`,
`@allow`, `@warn` and the program's own may be repeated). A public union
matched from another module needs `default` unless it is `@frozen` (docs/03
§3.3).

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

A function that is safe only when its caller keeps a promise the compiler
cannot check is declared `unsafe fn` — `unsafe static fn`, `unsafe mutating
fn` and so on; `unsafe` before a brace starts a block, before `fn` or another
modifier it modifies a function. Its documentation states the promise:

```sable
extension String {
    /// The bytes as a string, not checked: they must be well-formed UTF-8.
    public unsafe static fn fromUtf8Unchecked(_ bytes: U8[:]) -> String
}

unsafe {
    let s = String.fromUtf8Unchecked(bytes)   // the caller vouches
}
```

An `unsafe fn` may be called, or taken as a function value, only inside an
`unsafe` block or in the body of another `unsafe fn`, a closure's within either
included (S3470). Only a function can be `unsafe` (S3471).

`sable build --deny-unsafe` fails the build on any `unsafe` block or `unsafe
fn` in the program's own code (S3472); the standard library and the
package's dependencies may use them behind safe APIs. An allow-list of the
packages that may is not built yet.

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

`@extern("c")` alone takes the C name to be the function's, and
`@export("name")` alone takes the language to be C. Either is a function of
the module, not generic, not `async`, not throwing; an `@extern` one has no
body (S3223). What crosses is passed by value and must be something C has the
same way — an integer type, `F32`, `F64`, `Bool`, or a struct of those, which
is passed as the platform's C passes the struct of the same fields (S3224).
`@export("name") var count: Int = 0` names a stored variable of the module for
C, which reads and writes its storage by that name; it holds its initial value
once the program has started.
A variadic C function takes `CVarArg...` (Sable.Core: the integers, the floats
and `Bool`) after at least one fixed parameter, and an `@extern` alone may: each
call passes its values with C's default promotions (an integer narrower than
32 bits and `Bool` as an `int`, `F32` as a `double`). Pointers, C strings and
callbacks are `Sable.Interop`'s.

Header import (`sable bindgen`) generates `.sbi` interface files from C headers
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
