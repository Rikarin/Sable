// Port of set.sb.

#[path = "../../support/rust/bench.rs"]
mod bench;

use std::collections::HashSet;

fn main() {
    bench::run(|| {
        let mut a: HashSet<i64> = HashSet::new();
        let mut b: HashSet<i64> = HashSet::new();
        let mut x: i64 = 12345;
        for _ in 0..300000 {
            x = (x * 1103515245 + 12345) % 2147483648;
            a.insert(x / 7 % 500000);
            x = (x * 1103515245 + 12345) % 2147483648;
            b.insert(x / 7 % 500000);
        }

        let both: HashSet<i64> = a.union(&b).copied().collect();
        let common: HashSet<i64> = a.intersection(&b).copied().collect();
        let only: HashSet<i64> = a.difference(&b).copied().collect();
        let mut hits = 0;
        for i in 0..500000 {
            if both.contains(&i) {
                hits += 1;
            }
            if common.contains(&i) {
                hits += 1;
            }
            if only.contains(&i) {
                hits += 1;
            }
        }
        format!("{} {} {} {} {} {hits}", a.len(), b.len(), both.len(), common.len(), only.len())
    });
}
