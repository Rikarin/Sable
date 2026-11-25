// Port of parallel-sum.sb: std has no pool, so a scoped thread a core.
use std::thread;

fn main() {
    let count: i64 = 30_000_000;
    let threads = thread::available_parallelism().unwrap().get() as i64;
    let total: i64 = thread::scope(|scope| {
        let parts: Vec<_> = (0..threads)
            .map(|t| {
                scope.spawn(move || {
                    let mut sum = 0i64;
                    for n in count * t / threads..count * (t + 1) / threads {
                        sum += n.wrapping_mul(n) & 1023;
                    }
                    sum
                })
            })
            .collect();
        parts.into_iter().map(|part| part.join().unwrap()).sum()
    });
    println!("{total}");
}
