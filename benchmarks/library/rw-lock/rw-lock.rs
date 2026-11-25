// Port of rw-lock.sb: four threads and std's RwLock.

#[path = "../../support/rust/bench.rs"]
mod bench;

use std::hint::black_box;
use std::sync::RwLock;
use std::thread;

fn main() {
    bench::run(|| {
        let count = RwLock::new(0i64);
        thread::scope(|scope| {
            for task in 0..4 {
                let count = &count;
                scope.spawn(move || {
                    let mut sum = 0;
                    for i in 0..250000 {
                        if i % 100 == task {
                            *count.write().unwrap() += 1;
                        } else {
                            sum += *count.read().unwrap() % 2;
                        }
                    }
                    black_box(sum);
                });
            }
        });
        let total = *count.read().unwrap();
        format!("{total}")
    });
}
