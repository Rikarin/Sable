// Port of benchmarks/versus-dotnet/sorted-dictionary.sb: std's BTreeMap.

use std::collections::BTreeMap;

fn main() {
    let n = 200000;
    let mut keys: Vec<i64> = Vec::new();
    let mut x: i64 = 12345;
    for _ in 0..n {
        x = (x * 1103515245 + 12345) % 2147483648;
        keys.push(x);
    }

    let mut d: BTreeMap<i64, i64> = BTreeMap::new();
    for i in 0..n {
        d.insert(keys[i], i as i64);
    }

    let mut found: i64 = 0;
    for key in &keys {
        if let Some(value) = d.get(key) {
            found += value % 7;
        }
    }

    for i in (0..n).step_by(3) {
        d.remove(&keys[i]);
    }

    let mut total: i64 = 0;
    let mut previous: i64 = -1;
    let mut ordered = true;
    for (&key, value) in &d {
        if key <= previous {
            ordered = false;
        }
        previous = key;
        total = (total + value) % 1000000007;
    }

    let mut in_range: i64 = 0;
    for _ in d.range(500000000..1000000000) {
        in_range += 1;
    }
    println!("{} {found} {total} {in_range} {ordered}", d.len());
}
