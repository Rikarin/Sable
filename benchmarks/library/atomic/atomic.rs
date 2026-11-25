// Port of atomic.sb: four threads.

#[path = "../../support/rust/bench.rs"]
mod bench;

use std::sync::atomic::{AtomicI64, Ordering::SeqCst};
use std::thread;

fn main() {
    bench::run(|| {
        let count = AtomicI64::new(0);
        thread::scope(|scope| {
            for _ in 0..4 {
                scope.spawn(|| {
                    for _ in 0..500000 {
                        count.fetch_add(1, SeqCst);
                    }
                    for _ in 0..500000 {
                        let mut seen = count.load(SeqCst);
                        while let Err(was) = count.compare_exchange(seen, seen + 1, SeqCst, SeqCst) {
                            seen = was;
                        }
                    }
                });
            }
        });
        format!("{}", count.load(SeqCst))
    });
}
