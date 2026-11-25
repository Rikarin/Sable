// Port of map-int.sb: std's HashMap, SipHash and all.

#[path = "../../support/rust/bench.rs"]
mod bench;

use std::collections::HashMap;

fn main() {
    let n = 1000000;
    let mut keys: Vec<i64> = Vec::new();
    let mut x: i64 = 12345;
    for _ in 0..n {
        x = (x * 1103515245 + 12345) % 2147483648;
        keys.push(x);
    }

    bench::run(|| {
        let mut map: HashMap<i64, i64> = HashMap::new();
        for i in 0..n {
            map.insert(keys[i], i as i64);
        }

        let mut sum: i64 = 0;
        for _ in 0..3 {
            for key in &keys {
                sum += map.get(key).copied().unwrap_or(0);
            }
        }

        let mut misses: i64 = 0;
        for i in 0..n {
            if !map.contains_key(&(keys[i] + 2147483648)) {
                misses += 1;
            }
        }

        for i in (0..n).step_by(2) {
            map.remove(&keys[i]);
        }

        let mut hits: i64 = 0;
        for key in &keys {
            if map.contains_key(key) {
                hits += 1;
            }
        }
        format!("{} {sum} {misses} {hits}", map.len())
    });
}
