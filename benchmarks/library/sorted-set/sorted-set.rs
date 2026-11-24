// Port of sorted-set.sb.

use std::collections::BTreeSet;

fn main() {
    let mut values: Vec<i64> = Vec::new();
    let mut x: i64 = 12345;
    for _ in 0..200000 {
        x = (x * 1103515245 + 12345) % 2147483648;
        values.push(x);
    }

    let mut members: BTreeSet<i64> = BTreeSet::new();
    for &value in &values {
        members.insert(value);
    }
    let mut hits = 0;
    for value in &values {
        if members.contains(value) {
            hits += 1;
        }
        if members.contains(&(value + 2147483648)) {
            hits += 1;
        }
    }
    for i in (0..200000).step_by(3) {
        members.remove(&values[i]);
    }
    let mut total: i64 = 0;
    for value in &members {
        total = (total * 31 + value) % 1000000007;
    }
    println!("{} {hits} {total} {} {}", members.len(), members.first().unwrap(), members.last().unwrap());
}
