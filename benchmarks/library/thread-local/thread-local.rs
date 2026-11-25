// Port of thread-local.sb: thread_local!.
use std::cell::Cell;

thread_local! {
    static COUNT: Cell<i64> = Cell::new(std::hint::black_box(0));
}

fn main() {
    for i in 0..10_000_000i64 {
        COUNT.with(|count| count.set(count.get() + (i & 3)));
    }
    println!("{}", COUNT.with(|count| count.get()));
}
