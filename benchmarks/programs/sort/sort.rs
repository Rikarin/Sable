// Port of sort.sb: the same merge sort; the strings are sorted as
// references, as C's are.

#[path = "../../support/rust/bench.rs"]
mod bench;

fn sorted<T: Copy, F: Fn(T, T) -> bool>(items: &[T], before: &F) -> Vec<T> {
    if items.len() <= 1 {
        let mut copy = Vec::new();
        for &item in items {
            copy.push(item);
        }
        return copy;
    }
    let middle = items.len() / 2;
    let left = sorted(&items[..middle], before);
    let right = sorted(&items[middle..], before);

    let mut result = Vec::new();
    let (mut i, mut j) = (0, 0);
    while i < left.len() && j < right.len() {
        if before(right[j], left[i]) {
            result.push(right[j]);
            j += 1;
        } else {
            result.push(left[i]);
            i += 1;
        }
    }
    while i < left.len() {
        result.push(left[i]);
        i += 1;
    }
    while j < right.len() {
        result.push(right[j]);
        j += 1;
    }
    result
}

fn main() {
    bench::run(|| {
        let mut numbers: Vec<i64> = Vec::new();
        let mut seed: i64 = 42;
        for _ in 0..200000 {
            seed = (seed * 1103515245 + 12345) % 2147483648;
            numbers.push(seed % 1000000);
        }

        let ascending = sorted(&numbers, &|a, b| a < b);
        let mut labels = Vec::new();
        for i in 0..20000 {
            labels.push(format!("item-{}", ascending[i * 10] % 997));
        }
        let references: Vec<&str> = labels.iter().map(|label| label.as_str()).collect();
        let by_label = sorted(&references, &|a: &str, b: &str| a.len() < b.len());

        format!("{}\n{}\n{}\n{}", ascending[0], ascending[199999], by_label[0], by_label[19999])
    });
}
