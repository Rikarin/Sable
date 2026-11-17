#!/usr/bin/env python3
"""A synthetic workspace for measuring builds: N library packages in a DAG,
each a few hundred to a few thousand lines of ordinary Sable (structs,
unions, classes, generics, closures, loops, string interpolation), and an
`app` package whose binary uses all of them.

    genworkspace.py <directory> [--packages 100] [--units 12] [--deps 3] [--seed 1]

Each unit is ~75 lines; a package has --units units spread over several
files, so 12 units is ~900 lines a package. Package i depends on up to
--deps packages with a smaller index, chosen by a seeded PRNG, so the graph
is a DAG with a long critical path as well as wide layers.
"""

import argparse
import os
import random


def unit(p: int, u: int) -> str:
    n = f"{p}_{u}"
    return f"""
/// A record of unit {u}: an id, a label and a score.
public struct Item{n} {{
    public var id: Int
    public var label: String
    public var score: Int
}}

/// The shapes unit {u} knows about.
public union Shape{n} {{
    Dot(x: Int)
    Line(length: Int)
    Box(width: Int, height: Int)
}}

fn area{n}(_ s: Shape{n}) -> Int {{
    return match s {{
        case .Dot(let x) => x
        case .Line(let length) => length * 2
        case .Box(let width, let height) => width * height
    }}
}}

/// Adds numbers up, keeping each.
public class Counter{n} {{
    var total: Int = 0
    var seen: Int[>..] = []

    public init() {{ }}

    public fn add(_ x: Int) {{
        total = total + x
        seen.append(x)
    }}

    public var count: Int {{ get {{ return seen.count }} }}

    public fn sum() -> Int {{
        return total
    }}
}}

/// The largest of the values, or none.
fn largest{n}<T: Comparable>(_ xs: T[>..]) -> T? {{
    if xs.count == 0 {{
        return none
    }}
    var best = xs[0]
    for x in xs {{
        if x > best {{
            best = x
        }}
    }}
    return best
}}

fn build{n}(_ count: Int) -> Item{n}[>..] {{
    var items: Item{n}[>..] = []
    var i = 0
    while i < count {{
        items.append(Item{n} {{ id: i, label: "item \\(i) of {n}", score: i * 3 + {u} }})
        i = i + 1
    }}
    return items
}}

/// Unit {u}'s work for a seed.
public fn run{n}(_ seed: Int) -> Int {{
    let items = build{n}(seed % 7 + 3)
    let counter = Counter{n}()
    for item in items {{
        counter.add(item.score + item.label.count)
    }}
    let shapes: Shape{n}[>..] = [.Dot(x: seed % 11), .Line(length: {u}), .Box(width: 2, height: seed % 5)]
    var total = counter.sum() + counter.count
    for s in shapes {{
        total = total + area{n}(s)
    }}
    let scale = {{ (x: Int) -> Int in x * 2 + 1 }}
    var ids: Int[>..] = []
    for item in items {{
        ids.append(scale(item.id))
    }}
    if let top = largest{n}(ids) {{
        total = total + top
    }}
    return total % 100000
}}
"""


def package_sources(p: int, deps: list[int], units: int, files: int) -> dict[str, str]:
    module = f"Pkg{p}"
    imports = "".join(f"import Pkg{d}\n" for d in deps)
    per_file = max(1, (units + files - 1) // files)
    sources = {}
    for f in range(files):
        body = "".join(unit(p, u) for u in range(f * per_file, min(units, (f + 1) * per_file)))
        if body:
            sources[f"src/part{f}.sab"] = f"module {module}\n\n{imports}{body}"
    calls = "\n".join(f"    total = total + run{p}_{u}(seed + {u})" for u in range(units))
    dep_calls = "\n".join(f"    total = total + run{d}_0(seed)" for d in deps)
    sources["src/lib.sab"] = f"""module {module}

{imports}
/// Package {p}'s answer for a seed: its units', and a little of each dependency's.
public fn pkg{p}Value(_ seed: Int) -> Int {{
    var total = 0
{calls}
{dep_calls}
    return total % 1000000
}}
"""
    return sources


def write(path: str, text: str) -> None:
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w") as f:
        f.write(text)


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("directory")
    parser.add_argument("--packages", type=int, default=100)
    parser.add_argument("--units", type=int, default=12)
    parser.add_argument("--files", type=int, default=4)
    parser.add_argument("--deps", type=int, default=3)
    parser.add_argument("--seed", type=int, default=1)
    args = parser.parse_args()
    rng = random.Random(args.seed)

    root = args.directory
    members = []
    for p in range(args.packages):
        deps = sorted(rng.sample(range(p), min(p, args.deps))) if p > 0 else []
        # Bias towards the previous package, for a long critical path.
        if p > 0 and p - 1 not in deps:
            deps = sorted(deps[1:] + [p - 1])
        name = f"pkg{p}"
        members.append(name)
        dep_lines = "".join(f'pkg{d} = {{ path = "../pkg{d}" }}\n' for d in deps)
        write(f"{root}/{name}/sable.toml", f'[package]\nname = "{name}"\nversion = "0.1.0"\n\n[dependencies]\n{dep_lines}')
        for rel, text in package_sources(p, deps, args.units, args.files).items():
            write(f"{root}/{name}/{rel}", text)

    app_deps = "".join(f'pkg{p} = {{ path = "../pkg{p}" }}\n' for p in range(args.packages))
    write(f"{root}/app/sable.toml", f'[package]\nname = "app"\nversion = "0.1.0"\n\n[dependencies]\n{app_deps}')
    imports = "".join(f"import Pkg{p}\n" for p in range(args.packages))
    calls = "\n".join(f"    total = total + pkg{p}Value({p})" for p in range(args.packages))
    write(f"{root}/app/src/main.sab", f"""module App

import Sable.IO
{imports}
fn main() {{
    var total = 0
{calls}
    print("total \\(total)")
}}
""")
    members.append("app")
    member_list = ", ".join(f'"{m}"' for m in members)
    write(f"{root}/sable.toml", f"[workspace]\nmembers = [{member_list}]\n")


if __name__ == "__main__":
    main()
