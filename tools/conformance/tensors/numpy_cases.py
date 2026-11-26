#!/usr/bin/env python3
"""Sable.Math.Tensors' cases, with what NumPy gives for each.

    python3 -I tools/conformance/tensors/numpy_cases.py > tests/lib/Sable.Math.Tensors/NumpyTests.sb

Writes the Sable test file of broadcasting, basic indexing, reductions, matrix
products and shape operations: each case's inputs (NumPy's arange, reshaped),
and the shape and elements NumPy makes of them. CI's run of it checks that the
file in the repository is what this NumPy makes (tests/lib must not change).
"""

import sys

import numpy as np

INT_MAX = 9223372036854775807


def shape_text(shape):
    return "[" + ", ".join(str(n) for n in shape) + "]"


def elements_text(array):
    flat = np.asarray(array).reshape(-1)
    if flat.dtype == np.bool_:
        return "[" + ", ".join("true" if x else "false" for x in flat) + "]"
    if np.issubdtype(flat.dtype, np.floating):
        return "[" + ", ".join(repr(float(x)) for x in flat) + "]"
    return "[" + ", ".join(str(int(x)) for x in flat) + "]"


def arange(shape, start=0):
    return np.arange(start, start + int(np.prod(shape)), dtype=np.int64).reshape(shape)


def index_text(index):
    parts = []
    for part in index:
        if part is None:
            parts.append(".NewAxis")
        elif isinstance(part, slice):
            start = 0 if part.start is None else part.start
            stop = INT_MAX if part.stop is None else part.stop
            step = 1 if part.step is None else part.step
            parts.append(f".Slice(start: {start}, end: {stop}, step: {step})")
        else:
            parts.append(f".At({part})")
    return "[" + ", ".join(parts) + "]"


out = []
emit = out.append
emit(f"""module NumpyTests

// What NumPy {np.__version__} makes of Sable.Math.Tensors' cases: written by
// tools/conformance/tensors/numpy_cases.py, which is run again to check them.
// Inputs are NumPy's arange, reshaped; each case says the shape and the
// elements, row-major, NumPy gives.

import Sable.Math.Tensors
import Sable.Testing

fn arange(_ shape: Int[:], from start: Int = 0) -> Tensor<Int> {{
    var count = 1
    for length in shape {{
        count *= length
    }}
    var elements: Int[>..] = []
    for i in 0..<count {{
        elements.append(start + i)
    }}
    return Tensor<Int>(shape: shape, elements: elements)
}}

fn expectTensor<T: Equatable>(_ actual: Tensor<T>, _ shape: Int[:], _ elements: T[:], file: String = #file, line: Int = #line) {{
    expect(actual.shape == shape.toArray(), "shape \\(actual.shape), not \\(shape)", file: file, line: line)
    expect(actual.toArray() == elements.toArray(), "elements \\(actual.toArray()), not \\(elements)", file: file, line: line)
}}

fn expectClose(_ actual: Tensor<F64>, _ shape: Int[:], _ elements: F64[:], file: String = #file, line: Int = #line) {{
    expect(actual.shape == shape.toArray(), "shape \\(actual.shape), not \\(shape)", file: file, line: line)
    let got = actual.toArray()
    var near = got.count == elements.count
    var i = 0
    while near && i < got.count {{
        near = (got[i] - elements[i]).abs() <= 1e-12 * (elements[i].abs() > 1 ? elements[i].abs() : 1)
        i += 1
    }}
    expect(near, "elements \\(got), not \\(elements)", file: file, line: line)
}}
""")

# ---- Broadcasting -------------------------------------------------------------
pairs = [((3, 1), (1, 4)), ((2, 3, 4), (4,)), ((8, 1, 6, 1), (7, 1, 5)), ((1,), (5,)), ((2, 1, 3), (4, 1)),
         ((0, 3), (1, 3)), ((), (2, 2)), ((3,), (3,)), ((4, 1, 2), (1, 3, 1)), ((1, 1), (2, 3))]
for n, (sa, sb) in enumerate(pairs):
    a = arange(sa) if sa else np.array(5)
    b = arange(sb, 10)
    for op, name in (("+", "plus"), ("-", "minus"), ("*", "times")):
        result = {"+": a + b, "-": a - b, "*": a * b}[op]
        a_make = f"arange({shape_text(sa)})" if sa else "Tensor<Int>(shape: [], elements: [5])"
        emit(f"""@test
fn broadcast{n}{name.capitalize()}() {{
    expectTensor({a_make} {op} arange({shape_text(sb)}, from: 10), {shape_text(result.shape)}, {elements_text(result)})
    expectTensor(arange({shape_text(sb)}, from: 10) {op} {a_make}, {shape_text((b.__getattribute__({'+': '__add__', '-': '__sub__', '*': '__mul__'}[op])(a)).shape)}, {elements_text({'+': b + a, '-': b - a, '*': b * a}[op])})
}}
""")

for sa, sb in [((2, 3), (4, 3)), ((3,), (4,)), ((2, 1, 3), (2, 4, 2))]:
    emit(f"""@test(traps: "shapes {shape_text(sa)} and {shape_text(sb)} do not broadcast together")
fn shapes{''.join(str(x) for x in sa)}And{''.join(str(x) for x in sb)}DoNotBroadcast() {{
    _ = arange({shape_text(sa)}) + arange({shape_text(sb)})
}}
""")

# ---- Basic indexing ------------------------------------------------------------
base = arange((2, 3, 4))
indices = [
    (slice(None), slice(1, 3), slice(None, None, 2)),
    (1,),
    (-1, slice(None), 0),
    (slice(None), None, 1),
    (slice(-3, None), slice(0, 100), slice(1, None, 3)),
    (slice(5, 1),),
    (0, 2, -1),
    (slice(None, None, 3), slice(None, -1), slice(-2, None)),
    (None, slice(1, 2), None),
    (slice(1, 1), 0),
    (1, slice(None, None, 2), slice(None, None, 2)),
]
for n, index in enumerate(indices):
    result = base[index]
    emit(f"""@test
fn indexing{n}() {{
    let view = arange([2, 3, 4]).view.sliced({index_text(index)})
    expect(view.shape == {shape_text(result.shape)})
    expect(view.toArray() == {elements_text(result)})
    expect(view.toTensor().toArray() == {elements_text(result)})
}}
""")

# Views of views, and of a transposed view.
twice = base[:, 1:][1, ::2]
emit(f"""@test
fn aViewOfAView() {{
    let view = arange([2, 3, 4]).view.sliced([.All, .Slice(start: 1, end: {INT_MAX}, step: 1)]).sliced([.At(1), .Slice(start: 0, end: {INT_MAX}, step: 2)])
    expect(view.shape == {shape_text(twice.shape)})
    expect(view.toArray() == {elements_text(twice)})
}}
""")
transposed = base.transpose()[1:3, :, 1]
emit(f"""@test
fn aViewOfATransposedView() {{
    let view = arange([2, 3, 4]).view.transposed().sliced([.Slice(start: 1, end: 3, step: 1), .All, .At(1)])
    expect(view.shape == {shape_text(transposed.shape)})
    expect(view.toArray() == {elements_text(transposed)})
}}
""")

# ---- Reductions ----------------------------------------------------------------
values = np.array([[3, 1, 4, 1], [5, 9, 2, 6], [5, 3, 5, 8]], dtype=np.int64)
emit(f"""fn values() -> Tensor<Int> => Tensor<Int>(shape: [3, 4], elements: {elements_text(values)})
""")
for axis in (0, 1, -1):
    for op in ("sum", "max", "min", "argmax", "argmin"):
        result = getattr(values, op)(axis=axis)
        emit(f"""@test
fn {op}Axis{str(axis).replace('-', 'Minus')}() {{
    expectTensor(values().{op}(axis: {axis}), {shape_text(result.shape)}, {elements_text(result)})
}}
""")
emit(f"""@test
fn reductionsOfEverything() {{
    expect(values().sum() == {values.sum()})
    expect(values().max() == {values.max()})
    expect(values().min() == {values.min()})
    expect(values().argmax() == {values.argmax()})
    expect(values().argmin() == {values.argmin()})
    expectTensor(values().sum(axis: 1, keepingDimension: true), {shape_text(values.sum(axis=1, keepdims=True).shape)}, {elements_text(values.sum(axis=1, keepdims=True))})
}}
""")
floats = np.arange(12, dtype=np.float64).reshape(3, 4) / 4 - 1
mean0 = floats.mean(axis=0)
mean1 = floats.mean(axis=1)
e = np.exp(floats - floats.max(axis=1, keepdims=True))
soft = e / e.sum(axis=1, keepdims=True)
e0 = np.exp(floats - floats.max(axis=0, keepdims=True))
soft0 = e0 / e0.sum(axis=0, keepdims=True)
emit(f"""fn floats() -> Tensor<F64> => Tensor<F64>(shape: [3, 4], elements: {elements_text(floats)})

@test
fn meansAndSoftmax() {{
    expectClose(floats().mean(axis: 0), {shape_text(mean0.shape)}, {elements_text(mean0)})
    expectClose(floats().mean(axis: 1), {shape_text(mean1.shape)}, {elements_text(mean1)})
    expect((floats().mean() - {repr(float(floats.mean()))}).abs() < 1e-12)
    expectClose(floats().softmax(axis: 1), {shape_text(soft.shape)}, {elements_text(soft)})
    expectClose(floats().softmax(axis: 0), {shape_text(soft0.shape)}, {elements_text(soft0)})
}}
""")

# ---- Matrix products -----------------------------------------------------------
products = [((2, 3), (3, 4)), ((3,), (3, 2)), ((2, 3), (3,)), ((3,), (3,)), ((2, 2, 3), (3, 2)), ((1, 2, 3), (4, 3, 2)),
            ((5, 7), (7, 17)), ((9, 1), (1, 33))]
for n, (sa, sb) in enumerate(products):
    a = arange(sa, 1)
    b = arange(sb, -3)
    result = np.matmul(a, b)
    emit(f"""@test
fn matmul{n}() {{
    expectTensor(Tensor.matmul(arange({shape_text(sa)}, from: 1), arange({shape_text(sb)}, from: -3)), {shape_text(np.shape(result))}, {elements_text(result)})
}}
""")
    fa = a.astype(np.float64) / 8
    fb = b.astype(np.float64) / 4
    fresult = np.matmul(fa, fb)
    emit(f"""@test
fn matmul{n}OfF64AndF32() {{
    let a = arange({shape_text(sa)}, from: 1).map {{ F64($0) / 8 }}
    let b = arange({shape_text(sb)}, from: -3).map {{ F64($0) / 4 }}
    expectClose(Tensor.matmul(a, b), {shape_text(np.shape(fresult))}, {elements_text(fresult)})
    let narrow = Tensor.matmul(a.map {{ F32($0) }}, b.map {{ F32($0) }}).map {{ F64($0) }}
    expectClose(narrow, {shape_text(np.shape(fresult))}, {elements_text(fresult)})
}}
""")

# ---- Shape operations ----------------------------------------------------------
t = arange((2, 3, 4))
shape_cases = [
    ("reshaped([4, -1])", t.reshape(4, -1)),
    ("transposed()", t.transpose()),
    ("permuted([1, 2, 0])", t.transpose(1, 2, 0)),
    ("permuted([-1, 0, 1])", t.transpose(-1, 0, 1)),
    ("unsqueezed(axis: 1)", np.expand_dims(t, 1)),
    ("unsqueezed(axis: 3)", np.expand_dims(t, 3)),
    ("reshaped([1, 2, 1, 12]).squeezed()", t.reshape(1, 2, 1, 12).squeeze()),
    ("reshaped([1, 2, 1, 12]).squeezed(axis: 2)", t.reshape(1, 2, 1, 12).squeeze(2)),
    ("gathered([2, 0, 2], axis: 1)", np.take(t, [2, 0, 2], axis=1)),
    ("gathered([-1, 1], axis: 2)", np.take(t, [-1, 1], axis=2)),
    ("broadcast(to: [2, 2, 3, 4])", np.broadcast_to(t, (2, 2, 3, 4))),
]
for n, (expression, result) in enumerate(shape_cases):
    emit(f"""@test
fn shape{n}() {{
    expectTensor(arange([2, 3, 4]).{expression}, {shape_text(result.shape)}, {elements_text(result)})
}}
""")
u = arange((2, 3, 4), 100)
for n, (expression, result) in enumerate([
    ("Tensor.concatenate([arange([2, 3, 4]), arange([2, 3, 4], from: 100)], axis: 0)", np.concatenate([t, u], axis=0)),
    ("Tensor.concatenate([arange([2, 3, 4]), arange([2, 3, 4], from: 100)], axis: 1)", np.concatenate([t, u], axis=1)),
    ("Tensor.concatenate([arange([2, 3, 4]), arange([2, 3, 4], from: 100)], axis: -1)", np.concatenate([t, u], axis=-1)),
    ("Tensor.stack([arange([2, 3, 4]), arange([2, 3, 4], from: 100)], axis: 0)", np.stack([t, u], axis=0)),
    ("Tensor.stack([arange([2, 3, 4]), arange([2, 3, 4], from: 100)], axis: 2)", np.stack([t, u], axis=2)),
    ("Tensor.stack([arange([2, 3, 4]), arange([2, 3, 4], from: 100)], axis: 3)", np.stack([t, u], axis=3)),
]):
    emit(f"""@test
fn joining{n}() {{
    expectTensor({expression}, {shape_text(result.shape)}, {elements_text(result)})
}}
""")
parts = np.split(t, 2, axis=2)
emit(f"""@test
fn splitting() {{
    let parts = arange([2, 3, 4]).split(into: 2, axis: 2)
    expect(parts.count == 2)
    expectTensor(parts[0], {shape_text(parts[0].shape)}, {elements_text(parts[0])})
    expectTensor(parts[1], {shape_text(parts[1].shape)}, {elements_text(parts[1])})
    let uneven = arange([2, 3, 4]).split(lengths: [1, 2], axis: 1)
    expectTensor(uneven[1], {shape_text(t[:, 1:3].shape)}, {elements_text(t[:, 1:3])})
}}
""")

sys.stdout.write("\n".join(out).rstrip() + "\n")
